using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using static Aardvark.ImageCodecs.JpegXl.JxlBitReader;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>
    /// Lossy VarDCT frames in XYB: inverse Gaborish, LF (DC) image, adaptive quantization field, transform selection,
    /// chroma from luma per 64x64 tile, HF coefficients with the context model of the standard; alpha as a modular extra
    /// channel.
    /// </summary>
    internal static unsafe partial class JxlVarDctEncoder
    {
        // ------------------------------------------------------------------ tables of the HF context model (ISO/IEC 18181-1 C.8)

        static readonly ushort[] CoeffFreqContext =
        {
            0xBAD, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 15, 16, 16, 17, 17, 18, 18, 19, 19, 20, 20, 21, 21, 22, 22,
            23, 23, 23, 23, 24, 24, 24, 24, 25, 25, 25, 25, 26, 26, 26, 26, 27, 27, 27, 27, 28, 28, 28, 28, 29, 29, 29, 29, 30, 30, 30, 30,
        };
        static readonly ushort[] CoeffNumNonzeroContext =
        {
            0xBAD, 0, 31, 62, 62, 93, 93, 93, 93, 123, 123, 123, 123, 152, 152, 152, 152, 152, 152, 152, 152, 180, 180, 180, 180,
            180, 180, 180, 180, 180, 180, 180, 180, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206,
            206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206,
        };
        static readonly byte[] DefaultBlockCtxMap =
        {
            0, 1, 2, 2, 3, 3, 4, 5, 6, 6, 6, 6, 6,
            7, 8, 9, 9, 10, 11, 12, 13, 14, 14, 14, 14, 14,
            7, 8, 9, 9, 10, 11, 12, 13, 14, 14, 14, 14, 14,
        };
        const int NumBlockCtx = 15;
        const int NumHfContexts = 495 * NumBlockCtx;

        static readonly float[] LfDequant = { 1 / 4096f, 1 / 512f, 1 / 256f };

        /// <summary>Dequantization multipliers per quantization table and channel (default tables).</summary>
        static readonly float[][,] DequantTables = BuildDequant();

        static float[][,] BuildDequant()
        {
            var r = new float[17][,];
            for (int t = 0; t < 17; t++)
            {
                var q = JxlQuantEncoding.Default(t);
                int n = JxlAcStrategy.TableRows[t] * JxlAcStrategy.TableCols[t];
                var m = new float[3, n];
                for (int c = 0; c < 3; c++)
                {
                    var mc = q.Matrix(t, c);
                    for (int i = 0; i < n; i++) m[c, i] = mc[i];
                }
                r[t] = m;
            }
            return r;
        }

        static readonly float[][,] InvDequantTables = BuildInv();

        static float[][,] BuildInv()
        {
            var r = new float[17][,];
            for (int t = 0; t < 17; t++)
            {
                var d = DequantTables[t];
                var m = new float[3, d.GetLength(1)];
                for (int c = 0; c < 3; c++) for (int i = 0; i < m.GetLength(1); i++) m[c, i] = 1 / d[c, i];
                r[t] = m;
            }
            return r;
        }

        static readonly float[] RateTable = BuildRate();

        static float[] BuildRate()
        {
            var r = new float[64];
            r[0] = 0.25f;
            for (int a = 1; a < 64; a++) r[a] = 3.2f + 2.0f * MathF.Log2(a);
            return r;
        }

        /// <summary>The first CoveredX * CoveredY entries of the natural order are the LLF positions (not coded).</summary>
        static readonly bool[][] LlfMask = BuildLlf();

        static bool[][] BuildLlf()
        {
            var r = new bool[JxlAcStrategy.NumStrategies][];
            for (int s = 0; s < r.Length; s++)
            {
                int cov = JxlAcStrategy.CoveredX[s] * JxlAcStrategy.CoveredY[s];
                var nat = JxlAcStrategy.NaturalOrder(JxlAcStrategy.Order[s]);
                var m = new bool[cov * 64];
                for (int i = 0; i < cov; i++) m[nat[i]] = true;
                r[s] = m;
            }
            return r;
        }

        // ------------------------------------------------------------------ frame state

        sealed class Frame
        {
            public JxlImageHeader H = null!;
            public JxlFrameHeader Fh = null!;
            public JxlFrameDims Dims = null!;
            public int Bw, Bh, Pw, Ph;
            public float[][] Xyb = null!;          // padded planes X, Y, B
            public float Distance;
            public int Effort;
            public bool Parallel;
            public int GlobalScale, QuantLf;
            public float InvScale;                 // 65536 / globalScale
            public float[] DcStep = new float[3];
            public int[][] QDc = null!;            // quantized LF per block, channels X, Y, B
            public float[][] Dc = null!;           // dequantized LF (no smoothing)
            public Action<JxlBitWriter>? DequantMatrices;
            public int[] Qf = null!;               // quant field per block (1..256)
            public byte[] Strategy = null!;        // per block (255 = covered by an earlier block of the varblock)
            public byte[] Sharp = null!;
            public int[] CflX = null!, CflB = null!;
            public int TilesX;
            public float XQm, BQm;
            public float[] QuantBias = null!;
            public float QuantBiasNum;
            public int[][][] Coeffs = null!;       // per group, per channel: quantized coefficients of its varblocks
            public JxlTokens[] HfTokens = null!;
        }

        static int EpfFor(float d) => d < 0.6f ? 0 : d < 1.6f ? 1 : d < 4.5f ? 2 : 3;

        public static void EncodeFrame(JxlBitWriter cs, RawImage img, JxlImageHeader h, JxlFrameHeader fh, JxlEncodeOptions opt)
        {
            float d = Math.Max(0.05f, opt.EffectiveDistance);
            var f = new Frame { H = h, Fh = fh, Distance = d, Effort = opt.Effort, Parallel = opt.Multithreaded };
            fh.Modular = false;
            fh.XQmScale = 3; fh.BQmScale = 2;
            f.XQm = MathF.Pow(0.8f, fh.XQmScale - 2); f.BQm = MathF.Pow(0.8f, fh.BQmScale - 2);
            bool gab = opt.Gaborish ?? true;
            f.DequantMatrices = opt.DequantMatrices;
            fh.Filter = new JxlRestorationFilter { Gab = gab, EpfIters = opt.EpfIterations ?? EpfFor(d) };
            f.QuantBias = h.Opsin.QuantBias; f.QuantBiasNum = h.Opsin.QuantBiasNumerator;
            var dims = f.Dims = JxlFrameDims.Of(fh);
            f.Bw = dims.XBlocks; f.Bh = dims.YBlocks; f.Pw = f.Bw * 8; f.Ph = f.Bh * 8;
            f.TilesX = (f.Bw + 7) / 8;

            // colour: linear sRGB, XYB, inverse Gaborish
            var tf = img.Metadata?.Transfer ?? TransferFunction.Unknown;
            if (opt.KeepMetadata == false || tf == TransferFunction.Unknown)
                tf = img.SampleType is SampleType.Float16 or SampleType.Float32 ? TransferFunction.Linear : TransferFunction.Srgb;
            IccTransform? icc = null;
            if (h.Colour.WantIcc && h.Icc != null)
            {
                var src = IccProfile.Parse(h.Icc);
                bool gray = h.Colour.IsGray;
                icc = new IccTransform(src, gray ? IccProfile.GrayLinear : IccProfile.LinearSrgb, IccRenderingIntent.RelativeColorimetric);
            }
            // XYB holds linear light in units of 255 nits (the decoder scales by 255 / intensity target, L.2.2): samples
            // whose 1.0 is the intensity target (SDR, and HLG after its OOTF) are scaled by it / 255, PQ (1.0 = 10000 nits)
            // by 10000 / 255
            float it = h.ToneMapping.IntensityTarget;
            float mul = tf == TransferFunction.Pq && icc == null ? 10000f / 255 : it / 255;
            var planes = JxlEncColour.LinearPlanes(img, tf, img.Metadata?.Gamma ?? 0, icc, f.Pw, f.Ph, mul, f.Parallel);
            if (icc == null && !h.Colour.WantIcc)
            {
                // HLG is scene light: the OOTF makes it display light (18181-1:2024 Table E.6, NOTE 4), as the decoder undoes
                if (tf == TransferFunction.Hlg)
                    JxlEncColour.HlgOotf(planes, h.Colour.IsGray ? null : JxlColour.LuminanceWeights(h.Colour), JxlColour.HlgGamma(it), mul);
                // XYB starts from linear sRGB
                if (JxlColour.NeedsPrimaryConversion(h.Colour))
                    JxlColour.ApplyMatrix(planes[0], planes[1], planes[2], planes[0].Length, JxlColour.TargetToSrgbMatrix(h.Colour));
            }
            JxlProfile.Mark("vardct linear");
            JxlEncColour.ToXyb(planes[0], planes[1], planes[2], f.Parallel);
            JxlProfile.Mark("vardct xyb");
            if (gab) InverseGaborish(planes, f, dims.XSize, dims.YSize);
            f.Xyb = planes;
            JxlProfile.Mark("vardct colour");

            ChooseQuantizer(f);
            AdaptiveQuantization(f);
            ChooseStrategies(f, opt.ForceStrategy);
            JxlProfile.Mark("vardct aq + strategies");
            QuantizeLf(f, ComputeDc(f));
            JxlProfile.Mark("vardct lf");
            QuantizeGroups(f);
            JxlProfile.Mark("vardct hf");
            WriteFrame(cs, f, img, opt);
            JxlProfile.Mark("vardct write");
        }

        // ------------------------------------------------------------------ Gaborish

        /// <summary>
        /// Pre-sharpens so that the decoder's Gaborish smoothing returns the image: a few Richardson iterations
        /// x += original - Gab(x) on the image area, the block padding repeated again afterwards.
        /// </summary>
        static void InverseGaborish(float[][] planes, Frame f, int w, int h)
        {
            var gw = f.Fh.Filter.GabWeights;
            int iters = f.Effort < 7 ? 2 : 3;
            int n = w * h, pw = f.Pw;
            var orig = new float[n]; var x = new float[n]; var x2 = new float[n];
            for (int c = 0; c < 3; c++)
            {
                var p = planes[c];
                float norm = 1 / (1 + 4 * gw[2 * c] + 4 * gw[2 * c + 1]);
                float c0 = norm, c1 = gw[2 * c] * norm, c2 = gw[2 * c + 1] * norm;
                for (int y = 0; y < h; y++) Array.Copy(p, y * pw, orig, y * w, w);
                Array.Copy(orig, x, n);
                for (int it = 0; it < iters; it++)
                {
                    var src = x; var dst = x2;
                    void Row(int y)
                    {
                        int ru = Math.Max(0, y - 1) * w, rm = y * w, rd = Math.Min(h - 1, y + 1) * w;
                        for (int xx = 0; xx < w; xx++)
                        {
                            int xl = xx > 0 ? xx - 1 : 0, xr = xx + 1 < w ? xx + 1 : w - 1;
                            float side = src[ru + xx] + src[rd + xx] + src[rm + xl] + src[rm + xr];
                            float corner = src[ru + xl] + src[ru + xr] + src[rd + xl] + src[rd + xr];
                            float g = src[rm + xx] * c0 + side * c1 + corner * c2;
                            dst[rm + xx] = src[rm + xx] + orig[rm + xx] - g;
                        }
                    }
                    if (f.Parallel && h >= 64) Parallel.For(0, h, Row); else for (int y = 0; y < h; y++) Row(y);
                    (x, x2) = (x2, x);
                }
                for (int y = 0; y < h; y++)
                {
                    Array.Copy(x, y * w, p, y * pw, w);
                    for (int xx = w; xx < pw; xx++) p[y * pw + xx] = x[y * w + w - 1];
                }
                for (int y = h; y < f.Ph; y++) Array.Copy(p, (h - 1) * pw, p, y * pw, pw);
            }
        }

        // ------------------------------------------------------------------ quantizer and LF

        const int QfBase = 64;

        /// <summary>Average step multiplier (65536 / globalScale / qf) per unit of distance.</summary>
        internal static float StepPerDistance = 2.0f;
        /// <summary>LF step of Y at distance 1 and its growth with the distance.</summary>
        internal static float LfStep1 = 0.00085f, LfExponent = 0.65f;

        static void ChooseQuantizer(Frame f)
        {
            float s = StepPerDistance * f.Distance;
            int gs = (int)Math.Clamp(Math.Round(65536.0 / (s * QfBase)), 1, (1 << 16) + 8192);
            f.GlobalScale = gs;
            f.InvScale = 65536f / gs;
            float lfY = LfStep1 * MathF.Pow(f.Distance, LfExponent);
            // Y LF step = (1/512) * invScale / quantLf
            int ql = (int)Math.Clamp(Math.Round(f.InvScale / 512 / lfY), 1, 65536);
            f.QuantLf = ql;
            float mul = f.InvScale / ql;
            for (int c = 0; c < 3; c++) f.DcStep[c] = LfDequant[c] * mul;
        }

        static readonly double[][] LlfScale = BuildLlfScale();

        static double[][] BuildLlfScale()
        {
            var r = new double[6][];
            for (int l = 0; l <= 5; l++)
            {
                int n = 1 << l;
                var s = new double[n];
                for (int k = 0; k < n; k++) s[k] = k == 0 ? 1.0 : 8 * Math.Sin(Math.PI * k / (16.0 * n)) / Math.Sin(Math.PI * k / (2.0 * n));
                r[l] = s;
            }
            return r;
        }

        /// <summary>
        /// The LF image: block means for 8x8 transforms; for larger ones the values from which the decoder's LLF
        /// computation (a scaled DCT of them) gives back the lowest coefficients of the varblock's own DCT.
        /// </summary>
        static float[][] ComputeDc(Frame f)
        {
            int bw = f.Bw, bh = f.Bh, pw = f.Pw;
            var dc = new[] { new float[bw * bh], new float[bw * bh], new float[bw * bh] };
            void Row(int by)
            {
                var coef = new float[4 * 64 * 64 + 512];
                for (int bx = 0; bx < bw; bx++)
                {
                    int st = f.Strategy[by * bw + bx];
                    if (st == 255) continue;
                    int sx = JxlAcStrategy.CoveredX[st], sy = JxlAcStrategy.CoveredY[st];
                    if (sx * sy == 1)
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            var p = f.Xyb[c];
                            float s = 0;
                            for (int y = 0; y < 8; y++)
                            {
                                int o = (by * 8 + y) * pw + bx * 8;
                                for (int x = 0; x < 8; x++) s += p[o + x];
                            }
                            dc[c][by * bw + bx] = s / 64;
                        }
                        continue;
                    }
                    int W = sx * 8, H = sy * 8, size = W * H;
                    var mx = JxlTransforms.IdctMatrix(sx); var my = JxlTransforms.IdctMatrix(sy);
                    var scx = LlfScale[BitOperations.Log2((uint)sx)]; var scy = LlfScale[BitOperations.Log2((uint)sy)];
                    fixed (float* pc = coef)
                        for (int c = 0; c < 3; c++)
                        {
                            fixed (float* src = f.Xyb[c]) JxlEncDct.Forward(st, src + (long)by * 8 * pw + bx * 8, pw, pc, pc + size);
                            // G = LLF / scales, dc = IDCT(G)
                            for (int j = 0; j < sy; j++)
                                for (int i = 0; i < sx; i++)
                                {
                                    double v = 0;
                                    for (int ky = 0; ky < sy; ky++)
                                        for (int kx = 0; kx < sx; kx++)
                                        {
                                            float llf = W > H ? pc[ky * W + kx] : pc[kx * H + ky];
                                            v += llf / (scy[ky] * scx[kx]) * my[j * sy + ky] * mx[i * sx + kx];
                                        }
                                    dc[c][(by + j) * bw + bx + i] = (float)v;
                                }
                        }
                }
            }
            if (f.Parallel) Parallel.For(0, bh, Row); else for (int y = 0; y < bh; y++) Row(y);
            return dc;
        }

        static void QuantizeLf(Frame f, float[][] dcIn)
        {
            int bw = f.Bw, bh = f.Bh;
            f.QDc = new[] { new int[bw * bh], new int[bw * bh], new int[bw * bh] };
            f.Dc = new[] { new float[bw * bh], new float[bw * bh], new float[bw * bh] };
            void Row(int by)
            {
                Span<float> m = stackalloc float[3];
                for (int bx = 0; bx < bw; bx++)
                {
                    int b = by * bw + bx;
                    for (int c = 0; c < 3; c++) m[c] = dcIn[c][b];
                    int qy = (int)MathF.Round(m[1] / f.DcStep[1]);
                    float dy = qy * f.DcStep[1];
                    int qx = (int)MathF.Round(m[0] / f.DcStep[0]);
                    int qb = (int)MathF.Round((m[2] - dy) / f.DcStep[2]);
                    f.QDc[0][b] = qx; f.QDc[1][b] = qy; f.QDc[2][b] = qb;
                    f.Dc[0][b] = qx * f.DcStep[0]; f.Dc[1][b] = dy; f.Dc[2][b] = qb * f.DcStep[2] + dy;
                }
            }
            if (f.Parallel) Parallel.For(0, bh, Row); else for (int y = 0; y < bh; y++) Row(y);
        }

        /// <summary>Masking strength of the adaptive quantization (0 = uniform).</summary>
        internal static float AqStrength = 0f, AqOffset = 0.012f;

        /// <summary>
        /// The quant field: finer in flat areas, coarser where texture masks the error. Activity is the mean absolute
        /// gradient of Y in each 4x4 quarter of a block; the flattest quarter decides (ringing next to an edge shows in the
        /// flat part of the block).
        /// </summary>
        static void AdaptiveQuantization(Frame f)
        {
            int bw = f.Bw, bh = f.Bh, pw = f.Pw;
            var y = f.Xyb[1];
            var act = new float[bw * bh];
            void Row(int by)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    float mn = float.MaxValue;
                    for (int q = 0; q < 4; q++)
                    {
                        int x0 = bx * 8 + (q & 1) * 4, y0 = by * 8 + (q >> 1) * 4;
                        float s = 0;
                        for (int yy = y0; yy < y0 + 4; yy++)
                            for (int xx = x0; xx < x0 + 4; xx++)
                            {
                                int i = yy * pw + xx;
                                float v = y[i];
                                float r = xx + 1 < f.Pw ? y[i + 1] : v, dn = yy + 1 < f.Ph ? y[i + pw] : v;
                                s += MathF.Abs(r - v) + MathF.Abs(dn - v);
                            }
                        mn = Math.Min(mn, s / 16);
                    }
                    act[by * bw + bx] = mn;
                }
            }
            if (f.Parallel) Parallel.For(0, bh, Row); else for (int by = 0; by < bh; by++) Row(by);
            f.Qf = new int[bw * bh];
            float a0 = AqOffset;
            float refA = 0.02f;
            for (int i = 0; i < act.Length; i++)
            {
                float m = MathF.Pow((refA + a0) / (act[i] + a0), AqStrength);
                f.Qf[i] = Math.Clamp((int)MathF.Round(QfBase * m), 1, 256);
            }
            f.Sharp = new byte[bw * bh];
            Array.Fill(f.Sharp, (byte)SharpnessDefault);
        }

        internal static int SharpnessDefault = 4;

        // ------------------------------------------------------------------ coefficient quantization

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static int Quantize(float v, float invStep, float bias1, float biasNum, float deadzone)
        {
            float a = MathF.Abs(v) * invStep;
            if (a < deadzone) return 0;
            int r = (int)(a + 0.5f);
            if (r <= 1)
            {
                // 1 is reconstructed as bias1, 2 as 2 - biasNum / 2
                float e1 = MathF.Abs(a - bias1), e2 = MathF.Abs(a - (2 - biasNum * 0.5f));
                r = e2 < e1 ? 2 : 1;
            }
            else
            {
                float rec = r - biasNum / r, recUp = r + 1 - biasNum / (r + 1), recDn = r - 1 <= 1 ? bias1 : r - 1 - biasNum / (r - 1);
                float e = MathF.Abs(a - rec);
                if (MathF.Abs(a - recUp) < e) r++;
                else if (MathF.Abs(a - recDn) < e) r--;
            }
            return v < 0 ? -r : r;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static float Dequant(int q, float bias1, float biasNum) => q == 0 ? 0 : q == 1 || q == -1 ? q * bias1 : q - biasNum / q;

        internal static float Deadzone = 0.55f;

        /// <summary>Quantizes the HF coefficients of a varblock channel (LLF positions get 0) and returns the rate estimate.</summary>
        static void QuantizeBlock(float* coef, int* q, int st, int c, float mulBase, Frame f)
        {
            int table = JxlAcStrategy.QuantTable[st];
            var dm = DequantTables[table];
            int size = JxlAcStrategy.CoveredX[st] * JxlAcStrategy.CoveredY[st] * 64;
            var llf = LlfMask[st];
            float bias1 = f.QuantBias[c], bn = f.QuantBiasNum;
            float m = mulBase * (c == 0 ? f.XQm : c == 2 ? f.BQm : 1);
            float dz = Deadzone;
            for (int k = 0; k < size; k++)
            {
                if (llf[k]) { q[k] = 0; continue; }
                float step = m * dm[c, k];
                q[k] = Quantize(coef[k], 1 / step, bias1, bn, dz);
            }
        }

        // ------------------------------------------------------------------ transform selection

        /// <summary>Rate (bits) of a quantized value in the cost model of the transform selection.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static float Rate(int q)
        {
            int a = q < 0 ? -q : q;
            return a < 64 ? RateTable[a] : 3.2f + 2.0f * MathF.Log2(a);
        }

        internal static float Lambda = 9f;
        /// <summary>The smaller transforms are only tried on blocks whose 8x8 DCT costs more than this.</summary>
        internal static float SmallGate = 150f;

        /// <summary>
        /// Cost (rate + lambda * distortion in units of the quantization steps) of coding the region of the varblock at
        /// block (bx, by) with strategy st, all three channels, with chroma predicted from the quantized luma (factor of
        /// the default: X 0, B 1).
        /// </summary>
        static float Cost(Frame f, int st, int bx, int by, float[] buffer)
        {
            fixed (float* buf = buffer) return CostCore(f, st, bx, by, buf);
        }

        static float CostCore(Frame f, int st, int bx, int by, float* buf)
        {
            int cx = JxlAcStrategy.CoveredX[st], cy = JxlAcStrategy.CoveredY[st];
            int size = cx * cy * 64;
            float* coef = buf, yrec = buf + size, scratch = buf + 2 * size;
            int table = JxlAcStrategy.QuantTable[st];
            var dm = DequantTables[table];
            var idm = InvDequantTables[table];
            var llf = LlfMask[st];
            // average quant field of the covered blocks
            float qf = 0;
            for (int j = 0; j < cy; j++) for (int i = 0; i < cx; i++) qf += f.Qf[(by + j) * f.Bw + bx + i];
            qf /= cx * cy;
            float mulBase = f.InvScale / qf;
            float cost = 0;
            for (int ci = 0; ci < 3; ci++)
            {
                int c = ci == 0 ? 1 : ci == 1 ? 0 : 2;
                fixed (float* src = f.Xyb[c])
                    JxlEncDct.Forward(st, src + (long)by * 8 * f.Pw + bx * 8, f.Pw, coef, scratch);
                float m = mulBase * (c == 0 ? f.XQm : c == 2 ? f.BQm : 1);
                float im = 1 / m;
                float bias1 = f.QuantBias[c], bn = f.QuantBiasNum;
                for (int k = 0; k < size; k++)
                {
                    if (llf[k]) { if (c == 1) yrec[k] = 0; continue; }
                    float v = coef[k];
                    if (c == 2) v -= yrec[k];
                    float inv = idm[c, k] * im;
                    int q = Quantize(v, inv, bias1, bn, Deadzone);
                    float rec = Dequant(q, bias1, bn);
                    float e = v * inv - rec;
                    cost += Rate(q) + Lambda * e * e;
                    if (c == 1) yrec[k] = rec * m * dm[c, k];
                }
            }
            return cost;
        }

        static bool Fits(Frame f, int st, int bx, int by)
            => bx + JxlAcStrategy.CoveredX[st] <= f.Bw && by + JxlAcStrategy.CoveredY[st] <= f.Bh;

        static void Place(Frame f, int st, int bx, int by)
        {
            int cx = JxlAcStrategy.CoveredX[st], cy = JxlAcStrategy.CoveredY[st];
            for (int j = 0; j < cy; j++)
                for (int i = 0; i < cx; i++) f.Strategy[(by + j) * f.Bw + bx + i] = 255;
            f.Strategy[by * f.Bw + bx] = (byte)st;
        }

        /// <summary>
        /// Transform per block: 8x8 DCTs only at efforts 1 to 3; from 4 on, each aligned 16x16 and 32x32 (and from 7 on
        /// 64x64) region takes the cheapest of the large transform and the best split into smaller ones; from 5 on single
        /// blocks also try DCT4x4, DCT4x8 and DCT8x4.
        /// </summary>
        static void ChooseStrategies(Frame f, int force)
        {
            int bw = f.Bw, bh = f.Bh;
            f.Strategy = new byte[bw * bh];
            if (force >= 0 || f.Effort <= 3)
            {
                int st = force >= 0 ? force : JxlAcStrategy.Dct8;
                int cx = JxlAcStrategy.CoveredX[st], cy = JxlAcStrategy.CoveredY[st];
                var assigned = new bool[bw * bh];
                for (int by = 0; by < bh; by++)
                    for (int bx = 0; bx < bw; bx++)
                    {
                        if (assigned[by * bw + bx]) continue;
                        int use = bx % cx == 0 && by % cy == 0 && Fits(f, st, bx, by) && GroupAligned(st, bx, by) ? st : JxlAcStrategy.Dct8;
                        Place(f, use, bx, by);
                        for (int j = 0; j < JxlAcStrategy.CoveredY[use]; j++)
                            for (int i = 0; i < JxlAcStrategy.CoveredX[use]; i++) assigned[(by + j) * bw + bx + i] = true;
                    }
                return;
            }
            bool small = f.Effort >= 5;
            bool huge = f.Effort >= 7;
            int cell = huge ? 8 : 4;   // in blocks
            int cellsX = (bw + cell - 1) / cell, cellsY = (bh + cell - 1) / cell;
            void Cell(int ci)
            {
                int cx0 = (ci % cellsX) * cell, cy0 = (ci / cellsX) * cell;
                var pb = new float[4 * 64 * 64 + 512];
                {
                    // best per 8x8 block
                    int n = cell * cell;
                    var c8 = new float[n];
                    var s8 = new byte[n];
                    for (int j = 0; j < cell; j++)
                        for (int i = 0; i < cell; i++)
                        {
                            int bx = cx0 + i, by = cy0 + j;
                            if (bx >= bw || by >= bh) continue;
                            float best = Cost(f, JxlAcStrategy.Dct8, bx, by, pb);
                            byte bs = JxlAcStrategy.Dct8;
                            if (small && best > SmallGate)
                                foreach (int st in new[] { JxlAcStrategy.Dct4x4, JxlAcStrategy.Dct4x8, JxlAcStrategy.Dct8x4 })
                                {
                                    float c = Cost(f, st, bx, by, pb);
                                    if (c < best) { best = c; bs = (byte)st; }
                                }
                            c8[j * cell + i] = best; s8[j * cell + i] = bs;
                        }
                    // the cheapest tiling of an s x s block square at (i0, j0) of the cell, recursively
                    var plan = new List<(int st, int bx, int by)>();
                    float Best(int i0, int j0, int s, List<(int st, int bx, int by)> outPlan)
                    {
                        int bx = cx0 + i0, by = cy0 + j0;
                        if (s == 1)
                        {
                            if (bx >= bw || by >= bh) return 0;
                            outPlan.Add((s8[j0 * cell + i0], bx, by));
                            return c8[j0 * cell + i0];
                        }
                        int h = s / 2;
                        var split = new List<(int, int, int)>();
                        float cs = Best(i0, j0, h, split) + Best(i0 + h, j0, h, split) + Best(i0, j0 + h, h, split) + Best(i0 + h, j0 + h, h, split);
                        float bestCost = cs;
                        List<(int, int, int)> bestPlan = split;
                        int full = s == 2 ? JxlAcStrategy.Dct16x16 : s == 4 ? JxlAcStrategy.Dct32x32 : JxlAcStrategy.Dct64x64;
                        if (Fits(f, full, bx, by))
                        {
                            float c = Cost(f, full, bx, by, pb);
                            if (c < bestCost) { bestCost = c; bestPlan = new List<(int, int, int)> { (full, bx, by) }; }
                            // two halves: tall (s wide / 2, s high) side by side, or wide stacked
                            int tall = s == 2 ? JxlAcStrategy.Dct16x8 : s == 4 ? JxlAcStrategy.Dct32x16 : JxlAcStrategy.Dct64x32;
                            int wide = s == 2 ? JxlAcStrategy.Dct8x16 : s == 4 ? JxlAcStrategy.Dct16x32 : JxlAcStrategy.Dct32x64;
                            float ct = Cost(f, tall, bx, by, pb) + Cost(f, tall, bx + h, by, pb);
                            if (ct < bestCost) { bestCost = ct; bestPlan = new List<(int, int, int)> { (tall, bx, by), (tall, bx + h, by) }; }
                            float cw = Cost(f, wide, bx, by, pb) + Cost(f, wide, bx, by + h, pb);
                            if (cw < bestCost) { bestCost = cw; bestPlan = new List<(int, int, int)> { (wide, bx, by), (wide, bx, by + h) }; }
                        }
                        outPlan.AddRange(bestPlan);
                        return bestCost;
                    }
                    Best(0, 0, cell, plan);
                    foreach (var (st, bx, by) in plan) Place(f, st, bx, by);
                }
            }
            int cells = cellsX * cellsY;
            if (f.Parallel) Parallel.For(0, cells, Cell); else for (int i = 0; i < cells; i++) Cell(i);
        }

        static bool GroupAligned(int st, int bx, int by)
            => bx % 32 + JxlAcStrategy.CoveredX[st] <= 32 && by % 32 + JxlAcStrategy.CoveredY[st] <= 32;

        // ------------------------------------------------------------------ HF coefficients per group

        static void QuantizeGroups(Frame f)
        {
            var dims = f.Dims;
            f.CflX = new int[f.TilesX * ((f.Bh + 7) / 8)];
            f.CflB = new int[f.CflX.Length];
            f.HfTokens = new JxlTokens[dims.NumGroups];
            bool cfl = f.Effort >= 3;
            void Group(int g)
            {
                int gx = g % dims.GroupsX, gy = g / dims.GroupsX;
                int bx0 = gx * 32, by0 = gy * 32;
                int gw = Math.Min(32, f.Bw - bx0), gh = Math.Min(32, f.Bh - by0);
                // varblocks of the group: offsets of their coefficients
                var offsets = new int[gw * gh];
                int total = 0;
                for (int y = 0; y < gh; y++)
                    for (int x = 0; x < gw; x++)
                    {
                        int st = f.Strategy[(by0 + y) * f.Bw + bx0 + x];
                        if (st == 255) { offsets[y * gw + x] = -1; continue; }
                        offsets[y * gw + x] = total;
                        total += JxlAcStrategy.CoveredX[st] * JxlAcStrategy.CoveredY[st] * 64;
                    }
                var q = new[] { new int[total], new int[total], new int[total] };
                var coefBuf = new float[3 * total + 64];
                var scratch = new float[3 * 64 * 64 * 4 + 512];
                fixed (float* pc = coefBuf, ps = scratch)
                fixed (int* qx = q[0], qy = q[1], qb = q[2])
                {
                    // DCT of every varblock, Y quantized first
                    for (int y = 0; y < gh; y++)
                        for (int x = 0; x < gw; x++)
                        {
                            int off = offsets[y * gw + x];
                            if (off < 0) continue;
                            int bx = bx0 + x, by = by0 + y;
                            int st = f.Strategy[by * f.Bw + bx];
                            for (int c = 0; c < 3; c++)
                                fixed (float* src = f.Xyb[c])
                                    JxlEncDct.Forward(st, src + (long)by * 8 * f.Pw + bx * 8, f.Pw, pc + c * total + off, ps);
                            QuantizeBlock(pc + total + off, qy + off, st, 1, f.InvScale / f.Qf[by * f.Bw + bx], f);
                        }
                    // chroma from luma per 64x64 tile, then X and B
                    int t0x = bx0 / 8, t0y = by0 / 8, ntx = (gw + 7) / 8, nty = (gh + 7) / 8;
                    for (int ty = 0; ty < nty; ty++)
                        for (int tx = 0; tx < ntx; tx++)
                        {
                            int tile = (t0y + ty) * f.TilesX + t0x + tx;
                            int fx = 0, fb = 0;
                            if (cfl)
                            {
                                double sxy = 0, sbyv = 0, syy = 0;
                                foreach (var (st, off, mul) in TileBlocks(f, offsets, gw, gh, bx0, by0, tx, ty))
                                {
                                    int size = JxlAcStrategy.CoveredX[st] * JxlAcStrategy.CoveredY[st] * 64;
                                    var dm = DequantTables[JxlAcStrategy.QuantTable[st]];
                                    var llf = LlfMask[st];
                                    for (int k = 0; k < size; k++)
                                    {
                                        if (llf[k] || qy[off + k] == 0) continue;
                                        double yd = Dequant(qy[off + k], f.QuantBias[1], f.QuantBiasNum) * mul * dm[1, k];
                                        sxy += yd * pc[off + k];
                                        sbyv += yd * (pc[2 * total + off + k] - yd);
                                        syy += yd * yd;
                                    }
                                }
                                if (syy > 0)
                                {
                                    fx = (int)Math.Clamp(Math.Round(84 * sxy / syy), -128, 127);
                                    fb = (int)Math.Clamp(Math.Round(84 * sbyv / syy), -128, 127);
                                }
                            }
                            f.CflX[tile] = fx; f.CflB[tile] = fb;
                            float rx = fx / 84f, rb = 1 + fb / 84f;
                            foreach (var (st, off, mul) in TileBlocks(f, offsets, gw, gh, bx0, by0, tx, ty))
                            {
                                int size = JxlAcStrategy.CoveredX[st] * JxlAcStrategy.CoveredY[st] * 64;
                                var dm = DequantTables[JxlAcStrategy.QuantTable[st]];
                                float* cx = pc + off, cb = pc + 2 * total + off;
                                for (int k = 0; k < size; k++)
                                {
                                    float yd = Dequant(qy[off + k], f.QuantBias[1], f.QuantBiasNum) * mul * dm[1, k];
                                    cx[k] -= rx * yd;
                                    cb[k] -= rb * yd;
                                }
                                QuantizeBlock(cx, qx + off, st, 0, mul, f);
                                QuantizeBlock(cb, qb + off, st, 2, mul, f);
                            }
                        }
                    f.HfTokens[g] = Tokenize(f, offsets, gw, gh, bx0, by0, q);
                }
            }
            if (f.Parallel) Parallel.For(0, dims.NumGroups, Group); else for (int g = 0; g < dims.NumGroups; g++) Group(g);
        }

        static List<(int st, int off, float mul)> TileBlocks(Frame f, int[] offsets, int gw, int gh, int bx0, int by0, int tx, int ty)
        {
            var r = new List<(int, int, float)>();
            for (int y = ty * 8; y < Math.Min(gh, ty * 8 + 8); y++)
                for (int x = tx * 8; x < Math.Min(gw, tx * 8 + 8); x++)
                {
                    int off = offsets[y * gw + x];
                    if (off < 0) continue;
                    int b = (by0 + y) * f.Bw + bx0 + x;
                    r.Add((f.Strategy[b], off, f.InvScale / f.Qf[b]));
                }
            return r;
        }

        /// <summary>The HF tokens of a group in decoding order, with the contexts of the standard (one histogram set).</summary>
        static JxlTokens Tokenize(Frame f, int[] offsets, int gw, int gh, int bx0, int by0, int[][] q)
        {
            var tok = new JxlTokens(gw * gh * 3 * 12 + 64);
            var nzeros = new[] { new int[gw * gh], new int[gw * gh], new int[gw * gh] };
            for (int y = 0; y < gh; y++)
                for (int x = 0; x < gw; x++)
                {
                    int off = offsets[y * gw + x];
                    if (off < 0) continue;
                    int b = (by0 + y) * f.Bw + bx0 + x;
                    int st = f.Strategy[b];
                    int sx = JxlAcStrategy.CoveredX[st], sy = JxlAcStrategy.CoveredY[st];
                    int covered = sx * sy, logCovered = BitOperations.Log2((uint)covered);
                    int size = covered * 64;
                    int ord = JxlAcStrategy.Order[st];
                    var order = JxlAcStrategy.NaturalOrder(ord);
                    for (int ci = 0; ci < 3; ci++)
                    {
                        int c = ci == 0 ? 1 : ci == 1 ? 0 : 2;
                        int bctx = DefaultBlockCtxMap[(c < 2 ? c ^ 1 : 2) * 13 + ord];
                        var nzc = nzeros[c];
                        int pred;
                        if (x == 0 && y == 0) pred = 32;
                        else if (x == 0) pred = nzc[(y - 1) * gw];
                        else if (y == 0) pred = nzc[x - 1];
                        else pred = (nzc[(y - 1) * gw + x] + nzc[y * gw + x - 1] + 1) >> 1;
                        int nzBucket = pred >= 64 ? 36 : pred >= 8 ? 4 + pred / 2 : pred;
                        var qc = q[c];
                        int nz = 0;
                        for (int k = covered; k < size; k++) if (qc[off + order[k]] != 0) nz++;
                        tok.Add(nzBucket * NumBlockCtx + bctx, (uint)nz);
                        int stored = (nz + covered - 1) >> logCovered;
                        for (int j = 0; j < sy; j++)
                            for (int i = 0; i < sx; i++) nzc[(y + j) * gw + x + i] = stored;
                        if (nz == 0) continue;
                        int zdOff = NumBlockCtx * 37 + 458 * bctx;
                        int prev = nz <= size / 16 ? 1 : 0;
                        for (int k = covered; k < size && nz > 0; k++)
                        {
                            int ctx = zdOff + (CoeffNumNonzeroContext[(nz + covered - 1) >> logCovered] + CoeffFreqContext[k >> logCovered]) * 2 + prev;
                            int v = qc[off + order[k]];
                            uint u = JxlBitWriter.PackSigned(v);
                            tok.Add(ctx, u);
                            prev = u != 0 ? 1 : 0;
                            if (v != 0) nz--;
                        }
                    }
                }
            return tok;
        }

        // ------------------------------------------------------------------ writing

        static void WriteFrame(JxlBitWriter cs, Frame f, RawImage img, JxlEncodeOptions opt)
        {
            var h = f.H; var fh = f.Fh; var dims = f.Dims;
            int nLf = dims.NumLfGroups;
            var lp = JxlLosslessParams.For(Math.Min(f.Effort, 7));
            if (lp.FixedTree == 0)
            {
                // the modular parts of a lossy frame (1/64 of the pixels and the block data) are small: sample more of
                // them, but not too many
                lp.Tree.SampleFraction = Math.Min(1.0, lp.Tree.SampleFraction * 4);
                lp.Tree.MaxSamples = Math.Min(lp.Tree.MaxSamples, 120_000);
            }
            var streams = new JxlModularStreams();
            // extra channels (alpha) as modular channels: global part and groups, as in lossless frames
            var extra = ExtraChannels(img, h, opt);
            int gd = dims.GroupDim;
            int end = extra.Count;
            for (int i = 0; i < extra.Count; i++) if (extra[i].W > gd || extra[i].H > gd) { end = i; break; }
            int globalIdx = streams.Add(new JxlStreamImage(extra.GetRange(0, end), 0));
            // LF coefficients and HF metadata of every LF group
            var lfIdx = new int[nLf]; var metaIdx = new int[nLf]; var counts = new int[nLf];
            for (int i = 0; i < nLf; i++)
            {
                int lgx = i % dims.LfGroupsX, lgy = i / dims.LfGroupsX;
                int bx0 = lgx * 256, by0 = lgy * 256;
                int w = Math.Min(256, f.Bw - bx0), hh = Math.Min(256, f.Bh - by0);
                var ys = new JxlChannel(w, hh, 0, 0); var xs = new JxlChannel(w, hh, 0, 0); var bs = new JxlChannel(w, hh, 0, 0);
                for (int y = 0; y < hh; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int b = (by0 + y) * f.Bw + bx0 + x;
                        ys.Data[y * w + x] = f.QDc[1][b]; xs.Data[y * w + x] = f.QDc[0][b]; bs.Data[y * w + x] = f.QDc[2][b];
                    }
                lfIdx[i] = streams.Add(new JxlStreamImage(new List<JxlChannel> { ys, xs, bs }, 1 + i));
                int cw = (w + 7) / 8, ch = (hh + 7) / 8;
                var cx = new JxlChannel(cw, ch, 0, 0); var cb = new JxlChannel(cw, ch, 0, 0);
                for (int y = 0; y < ch; y++)
                    for (int x = 0; x < cw; x++)
                    {
                        int t = (by0 / 8 + y) * f.TilesX + bx0 / 8 + x;
                        cx.Data[y * cw + x] = f.CflX[t]; cb.Data[y * cw + x] = f.CflB[t];
                    }
                var stl = new List<int>(); var qfl = new List<int>();
                var sharp = new JxlChannel(w, hh, 0, 0);
                for (int y = 0; y < hh; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int b = (by0 + y) * f.Bw + bx0 + x;
                        sharp.Data[y * w + x] = f.Sharp[b];
                        int st = f.Strategy[b];
                        if (st == 255) continue;
                        stl.Add(st); qfl.Add(f.Qf[b] - 1);
                    }
                counts[i] = stl.Count;
                var info = new JxlChannel(stl.Count, 2, 0, 0);
                for (int k = 0; k < stl.Count; k++) { info.Data[k] = stl[k]; info.Data[stl.Count + k] = qfl[k]; }
                metaIdx[i] = streams.Add(new JxlStreamImage(new List<JxlChannel> { cx, cb, info, sharp }, 1 + 2 * nLf + i));
            }
            int groupStream0 = 1 + 3 * nLf + 17;
            var groupIdx = new int[dims.NumGroups];
            Array.Fill(groupIdx, -1);
            if (end < extra.Count)
                for (int g = 0; g < dims.NumGroups; g++)
                {
                    int gx = g % dims.GroupsX, gy = g / dims.GroupsX;
                    var sub = new List<JxlChannel>();
                    for (int c = end; c < extra.Count; c++)
                    {
                        var fc = extra[c];
                        int cx0 = gx * gd, cy0 = gy * gd;
                        int cw = Math.Min(gd, fc.W - cx0), chh = Math.Min(gd, fc.H - cy0);
                        if (cw <= 0 || chh <= 0) continue;
                        var s = new JxlChannel(cw, chh, 0, 0);
                        for (int y = 0; y < chh; y++) Array.Copy(fc.Data, (cy0 + y) * fc.W + cx0, s.Data, y * cw, cw);
                        sub.Add(s);
                    }
                    groupIdx[g] = streams.Add(new JxlStreamImage(sub, groupStream0 + g));
                }
            int maxCh = 0;
            foreach (var s in streams.Streams) maxCh = Math.Max(maxCh, s.Channels.Count);
            var tree = lp.FixedTree != 0
                ? JxlLosslessEncoder.FixedTree(maxCh, new JxlBitDepth { Bits = 8 }, lp.FixedTree == 2)
                : JxlTreeLearner.Learn(streams.Streams, lp.Tree, streams.Wp, f.Parallel);
            lp.Lz77 = false;
            streams.Prepare(tree, lp, f.Parallel);

            // HF entropy code over all groups
            var hf = new JxlEntropyEncoder(NumHfContexts) { MaxClusters = f.Effort >= 7 ? 64 : 32, ClusterEffort = f.Effort >= 5 ? 2 : 1 };
            hf.Build(f.HfTokens, f.Parallel);

            // sections
            var lfGlobal = new JxlBitWriter(1 << 12);
            lfGlobal.WriteBool(true);   // default LF channel dequantization
            lfGlobal.WriteU32((uint)f.GlobalScale, BitsOffset(11, 1), BitsOffset(11, 2049), BitsOffset(12, 4097), BitsOffset(16, 8193));
            lfGlobal.WriteU32((uint)f.QuantLf, Val(16), BitsOffset(5, 1), BitsOffset(8, 1), BitsOffset(16, 1));
            lfGlobal.WriteBool(true);   // default block context map
            lfGlobal.WriteBool(true);   // default LF channel correlation
            streams.WriteGlobal(lfGlobal);
            if (extra.Count > 0) streams.WriteStream(lfGlobal, globalIdx, Array.Empty<JxlTransformEnc>(), alwaysHeader: true);
            var lfGroups = new byte[nLf][];
            var none = Array.Empty<JxlTransformEnc>();
            void LfGroup(int i, JxlBitWriter w)
            {
                w.Write(2, 0);   // no extra LF precision
                streams.WriteStream(w, lfIdx[i], none);
                int lgx = i % dims.LfGroupsX, lgy = i / dims.LfGroupsX;
                int bw = Math.Min(256, f.Bw - lgx * 256), bh = Math.Min(256, f.Bh - lgy * 256);
                w.Write(JxlHybridUint.CeilLog2(bw * bh), (uint)(counts[i] - 1));
                streams.WriteStream(w, metaIdx[i], none);
            }
            void HfGlobal(JxlBitWriter w)
            {
                w.WriteBool(f.DequantMatrices == null);   // default dequantization matrices
                f.DequantMatrices?.Invoke(w);
                w.Write(JxlHybridUint.CeilLog2(dims.NumGroups), 0);   // one histogram set
                w.WriteU32(0, Val(0x5F), Val(0x13), Val(0), Bits(13));   // natural coefficient orders
                hf.WriteCode(w);
            }
            void PassGroup(int g, JxlBitWriter w)
            {
                // the single histogram set needs no selector bits
                hf.WriteTokens(w, f.HfTokens[g]);
                if (groupIdx[g] >= 0) streams.WriteStream(w, groupIdx[g], none);
            }
            var sections = new List<byte[]>();
            if (dims.NumGroups == 1 && dims.NumLfGroups == 1)
            {
                LfGroup(0, lfGlobal);
                HfGlobal(lfGlobal);
                PassGroup(0, lfGlobal);
                sections.Add(lfGlobal.ToArray());
            }
            else
            {
                sections.Add(lfGlobal.ToArray());
                var lfs = new byte[nLf][];
                void Lf(int i) { var w = new JxlBitWriter(4096); LfGroup(i, w); lfs[i] = w.ToArray(); }
                if (f.Parallel) Parallel.For(0, nLf, Lf); else for (int i = 0; i < nLf; i++) Lf(i);
                sections.AddRange(lfs);
                var hw = new JxlBitWriter(1 << 14);
                HfGlobal(hw);
                sections.Add(hw.ToArray());
                var gs = new byte[dims.NumGroups][];
                void Pg(int g) { var w = new JxlBitWriter(1 << 14); PassGroup(g, w); gs[g] = w.ToArray(); }
                if (f.Parallel) Parallel.For(0, dims.NumGroups, Pg); else for (int g = 0; g < dims.NumGroups; g++) Pg(g);
                sections.AddRange(gs);
            }
            JxlLosslessEncoder.WriteFrame(cs, fh, h, sections);
        }

        /// <summary>The extra channels (alpha) of the image as modular channels, exactly as stored (or with fewer alpha bits).</summary>
        static List<JxlChannel> ExtraChannels(RawImage img, JxlImageHeader h, JxlEncodeOptions opt)
        {
            var list = new List<JxlChannel>();
            if (h.ExtraChannels.Length == 0) return list;
            int w = img.Width, hh = img.Height, nc = img.Channels, n = w * hh;
            var ch = new JxlChannel(w, hh, 0, 0);
            var d = ch.Data;
            int a = nc - 1;
            switch (img.SampleType)
            {
                case SampleType.UInt8: { var s = (byte[])img.Data; for (int i = 0; i < n; i++) d[i] = s[i * nc + a]; break; }
                case SampleType.UInt16: { var s = (ushort[])img.Data; for (int i = 0; i < n; i++) d[i] = s[i * nc + a]; break; }
                case SampleType.Float16: { var s = (Half[])img.Data; for (int i = 0; i < n; i++) d[i] = BitConverter.HalfToUInt16Bits(s[i * nc + a]); break; }
                default: { var s = (float[])img.Data; for (int i = 0; i < n; i++) d[i] = BitConverter.SingleToInt32Bits(s[i * nc + a]); break; }
            }
            int bits = opt.AlphaBits;
            var bd = h.ExtraChannels[0].BitDepth;
            if (bits > 0 && !bd.Float && bits < bd.Bits)
            {
                int max = (1 << bd.Bits) - 1, lv = (1 << bits) - 1;
                for (int i = 0; i < n; i++) d[i] = (int)Math.Round(Math.Round(d[i] * (double)lv / max) * max / lv);
            }
            list.Add(ch);
            return list;
        }
    }
}
