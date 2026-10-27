using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using static Aardvark.ImageCodecs.JpegXl.JxlBitReader;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>VarDCT parts of the frame decoder: LF (DC) image, HF metadata, coefficients, dequantization and IDCT.</summary>
    internal sealed unsafe partial class JxlFrameDecoder
    {
        // quantizer
        int globalScale, quantLf;
        // block context map
        int[][] lfThr = { Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>() };
        int[] qfThr = Array.Empty<int>();
        byte[] blockCtxMap = DefaultBlockCtxMap;
        int numBlockCtx = 15;
        static readonly byte[] DefaultBlockCtxMap =
        {
            0, 1, 2, 2, 3, 3, 4, 5, 6, 6, 6, 6, 6,
            7, 8, 9, 9, 10, 11, 12, 13, 14, 14, 14, 14, 14,
            7, 8, 9, 9, 10, 11, 12, 13, 14, 14, 14, 14, 14,
        };
        // chroma from luma
        int colourFactor = 84;
        float baseCorrX, baseCorrB = 1;
        int xFactorLf = 128, bFactorLf = 128;

        // LF image and block information (frame wide, in 8x8 blocks)
        float[][]? dc;            // [3][XBlocks * YBlocks]
        int dcStride;             // stride of the block arrays (luma blocks)
        int[][]? quantDc;
        readonly int[] hs = new int[3], vs = new int[3];   // chroma subsampling shifts per channel (X, Y, B)
        readonly int[] dcw = new int[3], dch = new int[3];  // LF image size per channel
        bool Is444 => (hs[0] | hs[1] | hs[2] | vs[0] | vs[1] | vs[2]) == 0;
        byte[]? strategy;         // per block, 255 = not yet set
        bool[]? firstBlock;
        int[]? qfField;
        byte[]? sharpness;
        int[]? blockOrigin;       // first block of the varblock covering each block
        int[]? cflX, cflB;        // per 64x64 tile
        int tilesX;

        // HF
        JxlQuantEncoding[] quantEnc = new JxlQuantEncoding[17];
        readonly float[]?[,] dequantMatrix = new float[17, 3][];
        int numHistoSets = 1;
        int[]?[]? orders;         // [(pass * 13 + order) * 3 + channel], null = natural order
        JxlEntropyCode[]? hfCodes;

        // output (XYB, padded to whole blocks)
        float[][]? planes;
        int planeStride;          // luma plane stride; channel c uses planeStride >> hs[c]

        // per group coefficient accumulators (kept across passes)
        int[][][]? groupCoeffs;   // [group][channel], null until the first pass
        int[][]? groupOffsets;     // [group] varblock coefficient offset per block (-1 if not first)

        bool UsesLfFrame => fh.Has(JxlFrameHeader.FlagUseLfFrame);

        void DecodeVarDctGlobal(JxlBitReader br)
        {
            globalScale = (int)br.ReadU32(BitsOffset(11, 1), BitsOffset(11, 2049), BitsOffset(12, 4097), BitsOffset(16, 8193));
            quantLf = (int)br.ReadU32(Val(16), BitsOffset(5, 1), BitsOffset(8, 1), BitsOffset(16, 1));
            // block context map
            if (!br.ReadBool())
            {
                int prod = 1;
                for (int c = 0; c < 3; c++)
                {
                    int n = (int)br.ReadBits(4);
                    lfThr[c] = new int[n];
                    for (int i = 0; i < n; i++)
                        lfThr[c][i] = UnpackSigned(br.ReadU32(Bits(4), BitsOffset(8, 16), BitsOffset(16, 272), BitsOffset(32, 65808)));
                    prod *= n + 1;
                }
                int nq = (int)br.ReadBits(4);
                qfThr = new int[nq];
                for (int i = 0; i < nq; i++) qfThr[i] = 1 + (int)br.ReadU32(Bits(2), BitsOffset(3, 4), BitsOffset(5, 12), BitsOffset(8, 44));
                long size = 39L * (nq + 1) * prod;
                if (size > 39 * 64) throw new ImageFormatException("JPEG XL: block context map too large");
                blockCtxMap = JxlEntropyCode.ReadContextMap(br, (int)size, out numBlockCtx);
                if (numBlockCtx > 16) throw new ImageFormatException("JPEG XL: too many block contexts");
            }
            // LfChannelCorrelation
            if (!br.ReadBool())
            {
                colourFactor = (int)br.ReadU32(Val(84), Val(256), BitsOffset(8, 2), BitsOffset(16, 258));
                baseCorrX = br.ReadF16();
                baseCorrB = br.ReadF16();
                xFactorLf = (int)br.ReadBits(8);
                bFactorLf = (int)br.ReadBits(8);
            }
            // frame wide block arrays
            int nb = dims.XBlocks * dims.YBlocks;
            dcStride = dims.XBlocks;
            for (int c = 0; c < 3; c++) fh.ChromaShift(c, out hs[c], out vs[c]);
            dc = new float[3][];
            quantDc = new int[3][];
            for (int c = 0; c < 3; c++)
            {
                dcw[c] = dims.XBlocks >> hs[c]; dch[c] = dims.YBlocks >> vs[c];
                dc[c] = new float[dcw[c] * dch[c]];
                quantDc[c] = new int[dcw[c] * dch[c]];
            }
            strategy = new byte[nb];
            Array.Fill(strategy, (byte)255);
            firstBlock = new bool[nb];
            qfField = new int[nb];
            sharpness = new byte[nb];
            blockOrigin = new int[nb];
            tilesX = (dims.XBlocks + 7) / 8;
            int tilesY = (dims.YBlocks + 7) / 8;
            cflX = new int[tilesX * tilesY];
            cflB = new int[tilesX * tilesY];
            planeStride = dims.XBlocks * 8;
            long pn = (long)planeStride * dims.YBlocks * 8;
            if (pn > int.MaxValue) throw new ImageNotSupportedException("JPEG XL: frame too large");
            planes = new float[3][];
            for (int c = 0; c < 3; c++) planes[c] = new float[(long)(planeStride >> hs[c]) * ((dims.YBlocks * 8) >> vs[c])];
            groupCoeffs = new int[dims.NumGroups][][];
            groupOffsets = new int[dims.NumGroups][];
        }

        float InvGlobalScale => 65536.0f / globalScale;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void DecodeLfCoefficients(JxlBitReader br, int index)
        {
            int extra = (int)br.ReadBits(2);
            int gx = index % dims.LfGroupsX, gy = index / dims.LfGroupsX;
            int bx0 = gx * 256, by0 = gy * 256;
            int w = Math.Min(256, dims.XBlocks - bx0), h = Math.Min(256, dims.YBlocks - by0);
            // modular channels: Y, X, B
            int[] order = { 1, 0, 2 };
            var chans = new List<JxlChannel>();
            foreach (int c in order) chans.Add(new JxlChannel((w + (1 << hs[c]) - 1) >> hs[c], (h + (1 << vs[c]) - 1) >> vs[c], hs[c], vs[c], false));
            var img2 = JxlModularImage.Decode(br, chans, img.BitDepth.Bits, 1 + index, modularGlobal, 0, 1 << 22, true, out _);
            img2.Undo();
            if (img2.Channels.Count != 3) throw new ImageFormatException("JPEG XL: LF image has the wrong channel count");
            float mul = InvGlobalScale / quantLf / (1 << extra);
            if (Is444)
            {
                var qy = img2.Channels[0].Data; var qx = img2.Channels[1].Data; var qb = img2.Channels[2].Data;
                float mx = lfDequant[0] * mul, my = lfDequant[1] * mul, mb = lfDequant[2] * mul;
                float cfx = baseCorrX + (xFactorLf - 128) / (float)colourFactor;
                float cfb = baseCorrB + (bFactorLf - 128) / (float)colourFactor;
                var dx = dc![0]; var dy = dc[1]; var db = dc[2];
                for (int y = 0; y < h; y++)
                {
                    int o = (by0 + y) * dcStride + bx0, s = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        float vy = qy[s + x] * my;
                        dy[o + x] = vy;
                        dx[o + x] = qx[s + x] * mx + cfx * vy;
                        db[o + x] = qb[s + x] * mb + cfb * vy;
                        quantDc![0][o + x] = qx[s + x]; quantDc[1][o + x] = qy[s + x]; quantDc[2][o + x] = qb[s + x];
                    }
                }
                return;
            }
            // subsampled chroma: no chroma from luma
            for (int k = 0; k < 3; k++)
            {
                int c = order[k];
                var ch = img2.Channels[k];
                var q = ch.Data;
                float m = lfDequant[c] * mul;
                int cx0 = bx0 >> hs[c], cy0 = by0 >> vs[c];
                for (int y = 0; y < ch.H && cy0 + y < dch[c]; y++)
                    for (int x = 0; x < ch.W && cx0 + x < dcw[c]; x++)
                    {
                        int o = (cy0 + y) * dcw[c] + cx0 + x;
                        dc![c][o] = q[y * ch.W + x] * m;
                        quantDc![c][o] = q[y * ch.W + x];
                    }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void DecodeHfMetadata(JxlBitReader br, int index, long maxTree)
        {
            int gx = index % dims.LfGroupsX, gy = index / dims.LfGroupsX;
            int bx0 = gx * 256, by0 = gy * 256;
            int w = Math.Min(256, dims.XBlocks - bx0), h = Math.Min(256, dims.YBlocks - by0);
            int nbBits = JxlHybridUint.CeilLog2(w * h);
            int count = 1 + (int)br.ReadBits(nbBits);
            int cw = (w + 7) / 8, ch = (h + 7) / 8;
            var chans = new List<JxlChannel>
            {
                new(cw, ch, 3, 3, false), new(cw, ch, 3, 3, false), new(count, 2, 0, 0, false), new(w, h, 0, 0, false),
            };
            var m = JxlModularImage.Decode(br, chans, img.BitDepth.Bits, 1 + 2 * dims.NumLfGroups + index, modularGlobal, 0, maxTree, true, out _);
            m.Undo();
            if (m.Channels.Count != 4) throw new ImageFormatException("JPEG XL: HF metadata has the wrong channel count");
            var cx = m.Channels[0].Data; var cb = m.Channels[1].Data; var info = m.Channels[2].Data; var sh = m.Channels[3].Data;
            int tx0 = bx0 / 8, ty0 = by0 / 8;
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    cflX![(ty0 + y) * tilesX + tx0 + x] = cx[y * cw + x];
                    cflB![(ty0 + y) * tilesX + tx0 + x] = cb[y * cw + x];
                }
            int num = 0;
            int gd = 32;   // blocks per group
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int b = (by0 + y) * dcStride + bx0 + x;
                    int s = sh[y * w + x];
                    if (s < 0 || s > 7) throw new ImageFormatException("JPEG XL: bad EPF sharpness");
                    sharpness![b] = (byte)s;
                    if (strategy![b] != 255) continue;
                    if (num >= count) throw new ImageFormatException("JPEG XL: too few varblocks");
                    int st = info[num], q = info[count + num];
                    num++;
                    if (st < 0 || st >= JxlAcStrategy.NumStrategies) throw new ImageFormatException("JPEG XL: bad transform type");
                    int sx = JxlAcStrategy.CoveredX[st], sy = JxlAcStrategy.CoveredY[st];
                    if (x + sx > w || y + sy > h) throw new ImageFormatException("JPEG XL: varblock outside of the LF group");
                    int abx = bx0 + x, aby = by0 + y;
                    if (abx % gd + sx > gd || aby % gd + sy > gd) throw new ImageFormatException("JPEG XL: varblock crosses a group boundary");
                    // G.2.4 makes HfMul 1 + mul without bounds; libjxl clamps mul to [0, 255] (it decodes mul = -1, and
                    // mul = 699 as 255). libjxl is followed (JxlAuditRenderTests.HfMulIsClampedAsLibjxl).
                    int qf = 1 + Math.Clamp(q, 0, 255);
                    for (int j = 0; j < sy; j++)
                        for (int i = 0; i < sx; i++)
                        {
                            int bb = b + j * dcStride + i;
                            if (strategy[bb] != 255) throw new ImageFormatException("JPEG XL: overlapping varblocks");
                            strategy[bb] = (byte)st;
                            qfField![bb] = qf;
                            blockOrigin![bb] = b;
                        }
                    firstBlock![b] = true;
                }
            }
            if (num != count) throw new ImageFormatException("JPEG XL: unused varblock entries");
        }

        /// <summary>Adaptive smoothing of the LF image (unless the frame disables it), after all LF groups.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void AfterLfGroups()
        {
            if (UsesLfFrame)
            {
                var lf = state.LfFrames[fh.LfLevel] ?? throw new ImageFormatException("JPEG XL: frame uses a missing LF frame");
                for (int c = 0; c < 3; c++)
                    for (int y = 0; y < dch[c]; y++)
                        for (int x = 0; x < dcw[c]; x++)
                            dc![c][y * dcw[c] + x] = lf.Planes[c][Math.Min(y, lf.Height - 1) * lf.Width + Math.Min(x, lf.Width - 1)];
                return;
            }
            if (fh.Has(JxlFrameHeader.FlagSkipAdaptiveLfSmoothing) || !Is444) return;
            int W = dims.XBlocks, H = dims.YBlocks;
            if (W < 3 || H < 3) return;
            float mul = InvGlobalScale / quantLf;
            float[] fac = { lfDequant[0] * mul, lfDequant[1] * mul, lfDequant[2] * mul };
            const float w1 = 0.20345139757231578f, w2 = 0.0334829185968739f, w0 = 1 - 4 * (w1 + w2);
            var src = new float[3][];
            for (int c = 0; c < 3; c++) src[c] = (float[])dc![c].Clone();
            Span<float> sm = stackalloc float[3];
            Span<float> mc = stackalloc float[3];
            for (int y = 1; y < H - 1; y++)
            {
                for (int x = 1; x < W - 1; x++)
                {
                    float gap = 0.5f;
                    int i = y * W + x;
                    for (int c = 0; c < 3; c++)
                    {
                        var s = src[c];
                        float corner = s[i - W - 1] + s[i - W + 1] + s[i + W - 1] + s[i + W + 1];
                        float side = s[i - 1] + s[i + 1] + s[i - W] + s[i + W];
                        float m = s[i];
                        float smooth = corner * w2 + (side * w1 + m * w0);
                        sm[c] = smooth; mc[c] = m;
                        gap = Math.Max(gap, Math.Abs((m - smooth) / fac[c]));
                    }
                    float f = Math.Max(0, 3 - 4 * gap);
                    for (int c = 0; c < 3; c++) dc![c][i] = (sm[c] - mc[c]) * f + mc[c];
                }
            }
        }

        void DecodeHfGlobal(JxlBitReader br)
        {
            for (int t = 0; t < 17; t++) quantEnc[t] = JxlQuantEncoding.Default(t);
            if (!br.ReadBool())
            {
                for (int t = 0; t < 17; t++)
                    quantEnc[t] = JxlQuantEncoding.Read(br, t, (table, cols, rows) => ReadRawQuant(br, table, cols, rows));
            }
            numHistoSets = 1 + (int)br.ReadBits(JxlHybridUint.CeilLog2(dims.NumGroups));
            int np = fh.NumPasses;
            orders = new int[]?[np * 13 * 3];
            hfCodes = new JxlEntropyCode[np];
            for (int p = 0; p < np; p++)
            {
                uint used = br.ReadU32(Val(0x5F), Val(0x13), Val(0), Bits(13));
                if (used != 0)
                {
                    var code = JxlEntropyCode.Read(br, 8);
                    var r = new JxlSymbolReader(code, br, 0);
                    for (int o = 0; o < 13; o++)
                    {
                        if ((used >> o & 1) == 0) continue;
                        var nat = JxlAcStrategy.NaturalOrder(o);
                        int skip = JxlAcStrategy.OrderBlocksX[o] * JxlAcStrategy.OrderBlocksY[o];
                        for (int c = 0; c < 3; c++)
                        {
                            var perm = JxlToc.DecodePermutation(br, r, nat.Length, skip);
                            var ord = new int[nat.Length];
                            for (int i = 0; i < ord.Length; i++) ord[i] = nat[perm[i]];
                            orders[(p * 13 + o) * 3 + c] = ord;
                        }
                    }
                    r.CheckFinalState();
                }
                long nctx = 495L * numHistoSets * numBlockCtx;
                if (nctx > 1 << 20) throw new ImageFormatException("JPEG XL: too many HF contexts");
                hfCodes[p] = JxlEntropyCode.Read(br, (int)nctx);
            }
            br.CheckEnd();
        }

        int[][] ReadRawQuant(JxlBitReader br, int table, int cols, int rows)
        {
            var chans = new List<JxlChannel> { new(cols, rows, 0, 0, false), new(cols, rows, 0, 0, false), new(cols, rows, 0, 0, false) };
            var m = JxlModularImage.Decode(br, chans, img.BitDepth.Bits, 1 + 3 * dims.NumLfGroups + table, modularGlobal, 0, 1 << 22, true, out _);
            m.Undo();
            if (m.Channels.Count != 3) throw new ImageFormatException("JPEG XL: raw quantization table has the wrong channel count");
            return new[] { m.Channels[0].Data, m.Channels[1].Data, m.Channels[2].Data };
        }

        float[] Dequant(int table, int c)
        {
            var m = dequantMatrix[table, c];
            if (m != null) return m;
            lock (dequantMatrix)
            {
                m = dequantMatrix[table, c];
                if (m == null)
                {
                    m = quantEnc[table].Matrix(table, c);
                    dequantMatrix[table, c] = m;
                }
                return m;
            }
        }

        // ------------------------------------------------------------------ HF coefficients

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

        /// <summary>The LF context nests the channels in the order X, B, Y (Y varies fastest).</summary>
        static readonly int[] DcPerm = { 0, 2, 1 };

        int BlockContext(int c, int ord, int qf, int bx, int by)
        {
            int idx = (c < 2 ? c ^ 1 : 2) * 13 + ord;
            int qi = 0;
            foreach (int t in qfThr) if (qf > t) qi++;
            idx = idx * (qfThr.Length + 1) + qi;
            if (lfThr[0].Length + lfThr[1].Length + lfThr[2].Length > 0)
            {
                // with an LF frame there are no quantized LF values: they count as below every threshold (18181-1:2024 G.2.2)
                int d = 0;
                for (int ii = 0; ii < 3 && !UsesLfFrame; ii++)
                {
                    int cc = DcPerm[ii];
                    int v = quantDc![cc][(by >> vs[cc]) * dcw[cc] + (bx >> hs[cc])];
                    int k = 0;
                    foreach (int t in lfThr[cc]) if (v > t) k++;
                    d = d * (lfThr[cc].Length + 1) + k;
                }
                int ndc = (lfThr[0].Length + 1) * (lfThr[1].Length + 1) * (lfThr[2].Length + 1);
                idx = idx * ndc + d;
            }
            return blockCtxMap[idx];
        }

        void DecodeHfCoefficients(JxlBitReader br, int pass, int group)
        {
            int sel = (int)br.ReadBits(JxlHybridUint.CeilLog2(numHistoSets));
            if (sel >= numHistoSets) throw new ImageFormatException("JPEG XL: bad histogram selector");
            var reader = new JxlSymbolReader(hfCodes![pass], br, 0);
            int gx = group % dims.GroupsX, gy = group / dims.GroupsX;
            int bx0 = gx * 32, by0 = gy * 32;
            int gw = Math.Min(32, dims.XBlocks - bx0), gh = Math.Min(32, dims.YBlocks - by0);
            var offsets = groupOffsets![group];
            var coeffs = groupCoeffs![group];
            if (offsets == null)
            {
                offsets = new int[gw * gh];
                int total = 0;
                for (int y = 0; y < gh; y++)
                    for (int x = 0; x < gw; x++)
                    {
                        int b = (by0 + y) * dcStride + bx0 + x;
                        if (strategy![b] == 255) throw new ImageFormatException("JPEG XL: block without a transform");
                        if (!firstBlock![b]) { offsets[y * gw + x] = -1; continue; }
                        offsets[y * gw + x] = total;
                        int st = strategy[b];
                        total += JxlAcStrategy.CoveredX[st] * JxlAcStrategy.CoveredY[st] * 64;
                    }
                groupOffsets[group] = offsets;
                // pooled (returned after the group's transforms): cleared, as the passes accumulate into them
                coeffs = new int[3][];
                for (int c = 0; c < 3; c++) { coeffs[c] = System.Buffers.ArrayPool<int>.Shared.Rent(total); Array.Clear(coeffs[c], 0, total); }
                groupCoeffs[group] = coeffs;
            }
            int shift = pass < fh.NumPasses - 1 ? fh.Shift[pass] : 0;
            int histOff = 495 * numBlockCtx * sel;
            if (reader.CanFast)
            {
                fixed (byte* pd = br.Data)
                {
                    var fs = reader.BeginFast(br, pd);
                    HfBlocks(ref fs, pass, bx0, by0, gw, gh, offsets, coeffs!, shift, histOff);
                    reader.EndFast(fs, br, pd);
                }
            }
            else
            {
                var ss = new JxlSlowSymbols(reader, br);
                HfBlocks(ref ss, pass, bx0, by0, gw, gh, offsets, coeffs!, shift, histOff);
            }
            reader.CheckFinalState();
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void HfBlocks<TSym>(ref TSym reader, int pass, int bx0, int by0, int gw, int gh, int[] offsets, int[][] coeffs, int shift, int histOff)
            where TSym : struct, IJxlSymbols
        {
            int zdOff0 = numBlockCtx * 37;
            int* nzw = stackalloc int[3];
            int nzLen = 0;
            for (int c = 0; c < 3; c++)
            {
                nzw[c] = (gw + (1 << hs[c]) - 1) >> hs[c];
                nzLen = Math.Max(nzLen, nzw[c] * ((gh + (1 << vs[c]) - 1) >> vs[c]));
            }
            int* nzAll = stackalloc int[3 * nzLen];
            ref ushort nnzCtx = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(CoeffNumNonzeroContext);
            ref ushort freqCtx = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(CoeffFreqContext);
            for (int y = 0; y < gh; y++)
            {
                for (int x = 0; x < gw; x++)
                {
                    int off = offsets[y * gw + x];
                    if (off < 0) continue;
                    int b = (by0 + y) * dcStride + bx0 + x;
                    int st = strategy![b];
                    int sx = JxlAcStrategy.CoveredX[st], sy = JxlAcStrategy.CoveredY[st];
                    int covered = sx * sy, logCovered = BitOperations.Log2((uint)covered);
                    int size = covered * 64;
                    int ord = JxlAcStrategy.Order[st];
                    int qf = qfField![b];
                    for (int ci = 0; ci < 3; ci++)
                    {
                        int c = ci == 0 ? 1 : ci == 1 ? 0 : 2;
                        if ((x & ((1 << hs[c]) - 1)) != 0 || (y & ((1 << vs[c]) - 1)) != 0) continue;
                        int bctx = BlockContext(c, ord, qf, bx0 + x, by0 + y);
                        int* nzc = nzAll + c * nzLen;
                        int gwc = nzw[c], xc = x >> hs[c], yc = y >> vs[c];
                        int pred;
                        if (xc == 0 && yc == 0) pred = 32;
                        else if (xc == 0) pred = nzc[(yc - 1) * gwc];
                        else if (yc == 0) pred = nzc[xc - 1];
                        else pred = (nzc[(yc - 1) * gwc + xc] + nzc[yc * gwc + xc - 1] + 1) >> 1;
                        int nzBucket = pred >= 64 ? 36 : pred >= 8 ? 4 + pred / 2 : pred;
                        int nz = (int)reader.ReadUint(histOff + nzBucket * numBlockCtx + bctx);
                        if (nz > size - covered) throw new ImageFormatException("JPEG XL: too many nonzero coefficients");
                        int stored = (nz + covered - 1) >> logCovered;
                        for (int j = 0; j < sy; j++)
                            for (int i = 0; i < sx; i++) nzc[(yc + j) * gwc + xc + i] = stored;
                        if (nz == 0) continue;
                        var order = orders![(pass * 13 + ord) * 3 + c] ?? JxlAcStrategy.NaturalOrder(ord);
                        if (order.Length < size) throw new ImageFormatException("JPEG XL: coefficient order too short");
                        var cf = coeffs[c];
                        if (cf.Length < off + size) throw new ImageFormatException("JPEG XL: coefficient buffer too small");
                        int zdOff = histOff + zdOff0 + 458 * bctx;
                        int prev = nz <= size / 16 ? 1 : 0;
                        fixed (int* pcf = cf, pord = order)
                        {
                            int* dst = pcf + off;
                            for (int k = covered; k < size && nz > 0; k++)
                            {
                                int ctx = zdOff + (Unsafe.Add(ref nnzCtx, (nz + covered - 1) >> logCovered) + Unsafe.Add(ref freqCtx, k >> logCovered)) * 2 + prev;
                                uint u = reader.ReadUint(ctx);
                                int v = UnpackSigned(u);
                                dst[pord[k]] += v << shift;
                                prev = u != 0 ? 1 : 0;
                                if (v != 0) nz--;
                            }
                        }
                        if (nz != 0) throw new ImageFormatException("JPEG XL: coefficients end before the nonzero count");
                    }
                }
                if (reader.Overrun) throw new ImageFormatException("JPEG XL: unexpected end of HF data");
            }
        }

        /// <summary>After the last pass of a group: dequantization, chroma from luma, LLF and the inverse transforms.</summary>
        void FinishVarDctGroup(int group)
        {
            int gx = group % dims.GroupsX, gy = group / dims.GroupsX;
            int bx0 = gx * 32, by0 = gy * 32;
            int gw = Math.Min(32, dims.XBlocks - bx0), gh = Math.Min(32, dims.YBlocks - by0);
            var offsets = groupOffsets![group];
            var coeffs = groupCoeffs![group];
            if (offsets == null)
            {
                // no pass data (zero passes cannot happen; groups always have one section per pass)
                throw new ImageFormatException("JPEG XL: group without coefficients");
            }
            bool xyb = img.XybEncoded;
            float xm = xyb ? MathF.Pow(0.8f, fh.XQmScale - 2) : 1, bm = xyb ? MathF.Pow(0.8f, fh.BQmScale - 2) : 1;
            var qb = img.Opsin.QuantBias;
            float qbn = img.Opsin.QuantBiasNumerator;
            float inv = InvGlobalScale;
            var buf = groupScratch ??= new float[7 * GroupScratchMax + 32 * 32];
            fixed (float* pbuf = buf)
            fixed (float* p0 = planes![0], p1 = planes[1], p2 = planes[2])
            {
                float* pY = pbuf, pX = pbuf + GroupScratchMax, pB = pbuf + 2 * GroupScratchMax, ps = pbuf + 3 * GroupScratchMax, pl = pbuf + 7 * GroupScratchMax;
                FinishGroupCore(group, bx0, by0, gw, gh, offsets, coeffs, xyb, xm, bm, qb, qbn, inv, pY, pX, pB, ps, pl, p0, p1, p2);
            }
            groupCoeffs[group] = null!;
            foreach (var cf in coeffs) System.Buffers.ArrayPool<int>.Shared.Return(cf);
        }

        const int GroupScratchMax = 256 * 256;
        [ThreadStatic] static float[]? groupScratch;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void FinishGroupCore(int group, int bx0, int by0, int gw, int gh, int[] offsets, int[][] coeffs, bool xyb, float xm, float bm,
            float[] qb, float qbn, float inv, float* pY, float* pX, float* pB, float* ps, float* pl, float* p0, float* p1, float* p2)
        {
            int vc = Vector<float>.Count;
            float** outs = stackalloc float*[3];
            float** cbuf = stackalloc float*[3];
            outs[0] = p0; outs[1] = p1; outs[2] = p2;
            cbuf[0] = pX; cbuf[1] = pY; cbuf[2] = pB;
            bool* present = stackalloc bool[3];
            {
                for (int y = 0; y < gh; y++)
                    for (int x = 0; x < gw; x++)
                    {
                        int off = offsets[y * gw + x];
                        if (off < 0) continue;
                        int b = (by0 + y) * dcStride + bx0 + x;
                        int st = strategy![b];
                        int sx = JxlAcStrategy.CoveredX[st], sy = JxlAcStrategy.CoveredY[st];
                        int size = sx * sy * 64;
                        int table = JxlAcStrategy.QuantTable[st];
                        float mul = inv / qfField![b];
                        int tile = ((by0 + y) / 8) * tilesX + (bx0 + x) / 8;
                        float rx = baseCorrX + cflX![tile] / (float)colourFactor;
                        float rb = baseCorrB + cflB![tile] / (float)colourFactor;
                        for (int c = 0; c < 3; c++)
                            present[c] = (x & ((1 << hs[c]) - 1)) == 0 && (y & ((1 << vs[c]) - 1)) == 0;
                        for (int c = 0; c < 3; c++)
                        {
                            if (!present[c]) continue;
                            var q = coeffs![c];
                            var dm = Dequant(table, c);
                            float m = mul * (c == 0 ? xm : c == 2 ? bm : 1);
                            float* d = cbuf[c];
                            float bias = qb[c];
                            fixed (int* pq0 = q)
                            fixed (float* pdm = dm)
                            {
                                int* pq = pq0 + off;
                                int k = 0;
                                var vm = new Vector<float>(m);
                                var vbias = new Vector<float>(bias);
                                var vqbn = new Vector<float>(qbn);
                                var one = Vector<float>.One;
                                for (; k + vc <= size; k += vc)
                                {
                                    var vi = Unsafe.ReadUnaligned<Vector<int>>(pq + k);
                                    var vf = Vector.ConvertToSingle(vi);
                                    var r = vf - vqbn / vf;
                                    r = Vector.ConditionalSelect(Vector.Equals(Vector.Abs(vf), one), vf * vbias, r);
                                    r = Vector.ConditionalSelect(Vector.Equals(vi, Vector<int>.Zero), Vector<float>.Zero, r);
                                    Unsafe.WriteUnaligned(d + k, r * vm * Unsafe.ReadUnaligned<Vector<float>>(pdm + k));
                                }
                                for (; k < size; k++)
                                {
                                    int v = pq[k];
                                    float fv;
                                    if (v == 0) fv = 0;
                                    else if (v == 1 || v == -1) fv = v * bias;
                                    else fv = v - qbn / v;
                                    d[k] = fv * m * pdm[k];
                                }
                            }
                        }
                        if (Is444)
                            for (int k = 0; k < size; k++) { float yv = pY[k]; pX[k] += rx * yv; pB[k] += rb * yv; }
                        // LLF from DC
                        bool wide = sx > sy;
                        int W = sx * 8, H = sy * 8;
                        for (int c = 0; c < 3; c++)
                        {
                            if (!present[c]) continue;
                            float* d = cbuf[c];
                            int bc = ((by0 + y) >> vs[c]) * dcw[c] + ((bx0 + x) >> hs[c]);
                            fixed (float* pdc = dc![c])
                            {
                                if (sx == 1 && sy == 1) d[0] = pdc[bc];
                                else
                                {
                                    JxlTransforms.LlfFromDc(pdc + bc, dcw[c], sx, sy, pl);
                                    for (int ky = 0; ky < sy; ky++)
                                        for (int kx = 0; kx < sx; kx++)
                                        {
                                            if (wide) d[ky * W + kx] = pl[ky * sx + kx];
                                            else d[kx * H + ky] = pl[ky * sx + kx];
                                        }
                                }
                            }
                            int px = ((bx0 + x) >> hs[c]) * 8, py = ((by0 + y) >> vs[c]) * 8, pst = planeStride >> hs[c];
                            JxlTransforms.Inverse(st, d, outs[c] + (long)py * pst + px, pst, ps);
                        }
                    }
            }
        }

        float[][] VarDctColour()
        {
            var r = new float[3][];
            for (int c = 0; c < 3; c++)
            {
                var p = planes![c];
                int pst = planeStride >> hs[c];
                if (hs[c] != 0 || vs[c] != 0)
                {
                    // the stored chroma covers ceil(size / 2) samples
                    int cw = (dims.XSize + (1 << hs[c]) - 1) >> hs[c], chh = (dims.YSize + (1 << vs[c]) - 1) >> vs[c];
                    var small = new float[cw * chh];
                    for (int y = 0; y < chh; y++) Array.Copy(p, y * pst, small, y * cw, cw);
                    r[c] = JxlColour.UpsampleJpegChroma(small, cw, chh, hs[c], vs[c], dims.XSize, dims.YSize);
                    continue;
                }
                if (pst == dims.XSize && dims.YBlocks * 8 == dims.YSize) { r[c] = p; continue; }
                var o = new float[dims.XSize * dims.YSize];
                for (int y = 0; y < dims.YSize; y++) Array.Copy(p, y * pst, o, y * dims.XSize, dims.XSize);
                r[c] = o;
            }
            if (fh.DoYCbCr) JxlColour.YCbCrToRgbInPlace(r[0], r[1], r[2], dims.XSize * dims.YSize);
            return r;
        }
    }
}
