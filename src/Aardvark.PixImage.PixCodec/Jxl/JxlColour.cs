using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>Colour transforms of JPEG XL: XYB to linear RGB, YCbCr, transfer functions, primaries.</summary>
    internal static class JxlColour
    {
        /// <summary>In place: XYB planes to linear RGB with sRGB primaries (scaled so that 1.0 is the intensity target).</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void XybToLinear(float[] x, float[] y, float[] b, int n, JxlOpsinParams p, float intensityTarget, bool parallel)
        {
            float scale = 255.0f / intensityTarget;
            var m = new float[9];
            for (int i = 0; i < 9; i++) m[i] = p.InvMat[i] * scale;
            float b0 = p.OpsinBias[0], b1 = p.OpsinBias[1], b2 = p.OpsinBias[2];
            float c0 = MathF.Cbrt(b0), c1 = MathF.Cbrt(b1), c2 = MathF.Cbrt(b2);
            void Range(int s, int e)
            {
                int vc = Vector<float>.Count;
                var vm = new Vector<float>[9];
                for (int k = 0; k < 9; k++) vm[k] = new Vector<float>(m[k]);
                Vector<float> vc0 = new(c0), vc1 = new(c1), vc2 = new(c2), vb0 = new(b0), vb1 = new(b1), vb2 = new(b2);
                var sx = System.Runtime.InteropServices.MemoryMarshal.Cast<float, Vector<float>>(x.AsSpan(s, (e - s) / vc * vc));
                var sy = System.Runtime.InteropServices.MemoryMarshal.Cast<float, Vector<float>>(y.AsSpan(s, (e - s) / vc * vc));
                var sb = System.Runtime.InteropServices.MemoryMarshal.Cast<float, Vector<float>>(b.AsSpan(s, (e - s) / vc * vc));
                for (int k = 0; k < sx.Length; k++)
                {
                    var xx = sx[k]; var yy = sy[k]; var bb = sb[k];
                    var lg = yy + xx - vc0; var mg = yy - xx - vc1; var sg = bb - vc2;
                    var l = lg * lg * lg + vb0; var mm = mg * mg * mg + vb1; var ss = sg * sg * sg + vb2;
                    sx[k] = vm[0] * l + vm[1] * mm + vm[2] * ss;
                    sy[k] = vm[3] * l + vm[4] * mm + vm[5] * ss;
                    sb[k] = vm[6] * l + vm[7] * mm + vm[8] * ss;
                }
                for (int i = s + sx.Length * vc; i < e; i++)
                {
                    float xx = x[i], yy = y[i], bb = b[i];
                    float lg = yy + xx - c0, mg = yy - xx - c1, sg = bb - c2;
                    float l = lg * lg * lg + b0, mm = mg * mg * mg + b1, ss = sg * sg * sg + b2;
                    x[i] = m[0] * l + m[1] * mm + m[2] * ss;
                    y[i] = m[3] * l + m[4] * mm + m[5] * ss;
                    b[i] = m[6] * l + m[7] * mm + m[8] * ss;
                }
            }
            Chunked(n, parallel, Range);
        }

        /// <summary>XYB samples to linear RGB with sRGB primaries (as <see cref="XybToLinear(float[], float[], float[], int, JxlOpsinParams, float, bool)"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void XybToLinear(ReadOnlySpan<float> x, ReadOnlySpan<float> y, ReadOnlySpan<float> b, Span<float> r, Span<float> g, Span<float> bo,
            JxlOpsinParams p, float intensityTarget)
        {
            float scale = 255.0f / intensityTarget;
            Span<float> m = stackalloc float[9];
            for (int i = 0; i < 9; i++) m[i] = p.InvMat[i] * scale;
            float b0 = p.OpsinBias[0], b1 = p.OpsinBias[1], b2 = p.OpsinBias[2];
            float c0 = MathF.Cbrt(b0), c1 = MathF.Cbrt(b1), c2 = MathF.Cbrt(b2);
            int n = x.Length, vc = Vector<float>.Count, k = 0;
            Vector<float> m0 = new(m[0]), m1 = new(m[1]), m2 = new(m[2]), m3 = new(m[3]), m4 = new(m[4]), m5 = new(m[5]), m6 = new(m[6]), m7 = new(m[7]), m8 = new(m[8]);
            Vector<float> vc0 = new(c0), vc1 = new(c1), vc2 = new(c2), vb0 = new(b0), vb1 = new(b1), vb2 = new(b2);
            for (; k + vc <= n; k += vc)
            {
                var xx = new Vector<float>(x.Slice(k)); var yy = new Vector<float>(y.Slice(k)); var bb = new Vector<float>(b.Slice(k));
                var lg = yy + xx - vc0; var mg = yy - xx - vc1; var sg = bb - vc2;
                var l = lg * lg * lg + vb0; var mm = mg * mg * mg + vb1; var ss = sg * sg * sg + vb2;
                (m0 * l + m1 * mm + m2 * ss).CopyTo(r.Slice(k));
                (m3 * l + m4 * mm + m5 * ss).CopyTo(g.Slice(k));
                (m6 * l + m7 * mm + m8 * ss).CopyTo(bo.Slice(k));
            }
            for (; k < n; k++)
            {
                float xx = x[k], yy = y[k], bb = b[k];
                float lg = yy + xx - c0, mg = yy - xx - c1, sg = bb - c2;
                float l = lg * lg * lg + b0, mm = mg * mg * mg + b1, ss = sg * sg * sg + b2;
                r[k] = m[0] * l + m[1] * mm + m[2] * ss;
                g[k] = m[3] * l + m[4] * mm + m[5] * ss;
                bo[k] = m[6] * l + m[7] * mm + m[8] * ss;
            }
        }

        public static void Chunked(int n, bool parallel, Action<int, int> range)
        {
            const int Chunk = 1 << 16;
            if (!parallel || n < 2 * Chunk) { range(0, n); return; }
            int chunks = (n + Chunk - 1) / Chunk;
            Parallel.For(0, chunks, k => range(k * Chunk, Math.Min(n, (k + 1) * Chunk)));
        }

        /// <summary>JPEG style YCbCr (full range, BT.601) to RGB; inputs are in [0, 1] with chroma centred on 0.</summary>
        public static void YCbCrToRgb(float[] yp, float[] cb, float[] cr, int n, out float[] r, out float[] g, out float[] b)
        {
            r = new float[n]; g = new float[n]; b = new float[n];
            for (int i = 0; i < n; i++)
            {
                float Y = yp[i] + 128 / 255f, Cb = cb[i], Cr = cr[i];
                r[i] = Y + 1.402f * Cr;
                g[i] = Y - 0.344136286f * Cb - 0.714136286f * Cr;
                b[i] = Y + 1.772f * Cb;
            }
        }

        /// <summary>In place YCbCr to RGB on the three planes (Y in the middle one, as in VarDCT channel order X, Y, B = Cb, Y, Cr).</summary>
        public static void YCbCrToRgbInPlace(float[] cb, float[] yp, float[] cr, int n)
        {
            for (int i = 0; i < n; i++)
            {
                float Y = yp[i] + 128 / 255f, Cb = cb[i], Cr = cr[i];
                cb[i] = Y + 1.402f * Cr;
                yp[i] = Y - 0.344136286f * Cb - 0.714136286f * Cr;
                cr[i] = Y + 1.772f * Cb;
            }
        }

        /// <summary>JPEG "fancy" chroma upsampling (triangle filter, 3/4 1/4) to the full plane size.</summary>
        public static float[] UpsampleJpegChroma(float[] src, int w, int h, int hs, int vs, int ow, int oh)
        {
            float[] cur = src;
            int cw = w, chh = h;
            if (hs > 0)
            {
                int nw = Math.Min(ow, cw * 2);
                var o = new float[nw * chh];
                for (int y = 0; y < chh; y++)
                    for (int x = 0; x < nw; x++)
                    {
                        int sx = x >> 1;
                        float c = cur[y * cw + sx];
                        int nx = (x & 1) == 0 ? Math.Max(sx - 1, 0) : Math.Min(sx + 1, cw - 1);
                        o[y * nw + x] = 0.75f * c + 0.25f * cur[y * cw + nx];
                    }
                cur = o; cw = nw;
            }
            if (vs > 0)
            {
                int nh = Math.Min(oh, chh * 2);
                var o = new float[cw * nh];
                for (int y = 0; y < nh; y++)
                {
                    int sy = y >> 1;
                    int ny = (y & 1) == 0 ? Math.Max(sy - 1, 0) : Math.Min(sy + 1, chh - 1);
                    for (int x = 0; x < cw; x++) o[y * cw + x] = 0.75f * cur[sy * cw + x] + 0.25f * cur[ny * cw + x];
                }
                cur = o; chh = nh;
            }
            if (cw == ow && chh == oh) return cur;
            var r = new float[ow * oh];
            for (int y = 0; y < oh; y++)
                for (int x = 0; x < ow; x++) r[y * ow + x] = cur[Math.Min(y, chh - 1) * cw + Math.Min(x, cw - 1)];
            return r;
        }

        // ------------------------------------------------------------------ transfer functions (linear -> encoded)

        const int SrgbLutSize = 1 << 16;
        static float[]? srgbLut;

        /// <summary>The sRGB curve sampled on [0, 1] for linear interpolation (error below 1e-7).</summary>
        static float[] SrgbLut
        {
            get
            {
                if (srgbLut != null) return srgbLut;
                var t = new float[SrgbLutSize + 2];
                for (int i = 0; i < t.Length; i++)
                {
                    double v = (double)i / SrgbLutSize;
                    t[i] = (float)(v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055);
                }
                return srgbLut = t;
            }
        }

        const int Srgb8Cells = 1 << 12;
        static (float[] thr, byte[] coarse)? srgb8;

        /// <summary>
        /// Linear light to 8 bit sRGB, rounded: thr[b] is the linear value from which the output is b + 1, coarse[k] the
        /// output at k / 4096 (at most one threshold lies within a cell: the curve rises by at most 0.81 steps per cell).
        /// </summary>
        static (float[] thr, byte[] coarse) Srgb8Tables
        {
            get
            {
                if (srgb8 is { } t) return t;
                var thr = new float[256];
                for (int b = 0; b < 255; b++)
                {
                    double e = (b + 0.5) / 255;
                    double lin = e <= 0.04045 ? e / 12.92 : Math.Pow((e + 0.055) / 1.055, 2.4);
                    float f = (float)lin;
                    // the first float whose (interpolated) encoding rounds up
                    while (f > 0 && SrgbInterp(BitDecrement(f)) * 255 + 0.5f >= b + 1) f = BitDecrement(f);
                    while (SrgbInterp(f) * 255 + 0.5f < b + 1) f = BitIncrement(f);
                    thr[b] = f;
                }
                thr[255] = float.PositiveInfinity;
                var coarse = new byte[Srgb8Cells + 1];
                int k0 = 0;
                for (int k = 0; k <= Srgb8Cells; k++)
                {
                    float v = (float)k / Srgb8Cells;
                    while (k0 < 255 && v >= thr[k0]) k0++;
                    coarse[k] = (byte)k0;
                }
                srgb8 = (thr, coarse);
                return (thr, coarse);
            }
        }

        static float BitDecrement(float f) => MathF.BitDecrement(f);
        static float BitIncrement(float f) => MathF.BitIncrement(f);

        static float SrgbInterp(float v)
        {
            if (v < 0) return -SrgbFromLinear(-v);
            if (v >= 1) return SrgbFromLinear(v);
            var lut = SrgbLut;
            float f = v * SrgbLutSize;
            int k = (int)f;
            return lut[k] + (lut[k + 1] - lut[k]) * (f - k);
        }

        /// <summary>
        /// Linear light to rounded 8 bit sRGB values, the same as <see cref="FromLinear(Span{float}, JxlColourEncoding, float)"/>
        /// followed by rounding and clamping, without evaluating the curve.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static unsafe void LinearToSrgb8(ReadOnlySpan<float> src, Span<byte> dst, int stride)
        {
            var (thr, coarse) = Srgb8Tables;
            const float Below1 = 0.99999994f;
            int i = 0;
            if (System.Runtime.Intrinsics.X86.Avx2.IsSupported && src.Length >= 8)
            {
                if ((long)(src.Length - 1) * stride >= dst.Length) throw new ArgumentException("destination too small");
                var ci = Srgb8CoarseInt;
                int* tmp = stackalloc int[8];
                fixed (float* ps = src, pt = thr)
                fixed (int* pc = ci)
                fixed (byte* pd = dst)
                {
                    var zero = System.Runtime.Intrinsics.Vector256<float>.Zero;
                    var below = System.Runtime.Intrinsics.Vector256.Create(Below1);
                    var cells = System.Runtime.Intrinsics.Vector256.Create((float)Srgb8Cells);
                    for (; i + 8 <= src.Length; i += 8)
                    {
                        var v = System.Runtime.Intrinsics.X86.Avx.LoadVector256(ps + i);
                        v = System.Runtime.Intrinsics.X86.Avx.Max(v, zero);      // NaN -> 0
                        v = System.Runtime.Intrinsics.X86.Avx.Min(v, below);
                        var k = System.Runtime.Intrinsics.X86.Avx.ConvertToVector256Int32WithTruncation(System.Runtime.Intrinsics.X86.Avx.Multiply(v, cells));
                        var b = System.Runtime.Intrinsics.X86.Avx2.GatherVector256(pc, k, 4);
                        var t = System.Runtime.Intrinsics.X86.Avx2.GatherVector256(pt, b, 4);
                        var ge = System.Runtime.Intrinsics.Vector256.AsInt32(System.Runtime.Intrinsics.X86.Avx.CompareGreaterThanOrEqual(v, t));
                        b = System.Runtime.Intrinsics.X86.Avx2.Subtract(b, ge);
                        System.Runtime.Intrinsics.X86.Avx.Store(tmp, b);
                        byte* o = pd + (long)i * stride;
                        o[0] = (byte)tmp[0]; o[stride] = (byte)tmp[1]; o[2 * stride] = (byte)tmp[2]; o[3 * stride] = (byte)tmp[3];
                        o[4 * stride] = (byte)tmp[4]; o[5 * stride] = (byte)tmp[5]; o[6 * stride] = (byte)tmp[6]; o[7 * stride] = (byte)tmp[7];
                    }
                }
            }
            ref float t0 = ref MemoryMarshal.GetArrayDataReference(thr);
            ref byte c0 = ref MemoryMarshal.GetArrayDataReference(coarse);
            for (; i < src.Length; i++)
            {
                float v = src[i];
                v = v > 0 ? v : 0;            // also NaN
                v = v < Below1 ? v : Below1;
                int b = Unsafe.Add(ref c0, (int)(v * Srgb8Cells));
                // branch free: one more when v >= thr[b] (the sign bit of v - thr[b] is clear)
                b += 1 - (int)((uint)BitConverter.SingleToInt32Bits(v - Unsafe.Add(ref t0, b)) >> 31);
                dst[i * stride] = (byte)b;
            }
        }

        /// <summary>
        /// <see cref="LinearToSrgb8"/> of three planes into interleaved RGB (dst[3 i + c]); AVX2 with byte shuffles for the
        /// interleaving, the scalar path otherwise.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static unsafe void LinearToSrgb8x3(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b, Span<byte> dst)
        {
            int n = r.Length;
            if (g.Length < n || b.Length < n || dst.Length < 3L * n) throw new ArgumentException("buffers too small");
            int i = 0;
            if (System.Runtime.Intrinsics.X86.Avx2.IsSupported && n >= 16)
            {
                var (thr, _) = Srgb8Tables;
                var ci = Srgb8CoarseInt;
                var m = Srgb8Shuffles;
                fixed (float* pr = r, pg = g, pb = b, pt = thr)
                fixed (int* pc = ci)
                fixed (byte* pd = dst, pm = m)
                {
                    var s0r = System.Runtime.Intrinsics.X86.Sse2.LoadVector128(pm); var s0g = System.Runtime.Intrinsics.X86.Sse2.LoadVector128(pm + 16); var s0b = System.Runtime.Intrinsics.X86.Sse2.LoadVector128(pm + 32);
                    var s1r = System.Runtime.Intrinsics.X86.Sse2.LoadVector128(pm + 48); var s1g = System.Runtime.Intrinsics.X86.Sse2.LoadVector128(pm + 64); var s1b = System.Runtime.Intrinsics.X86.Sse2.LoadVector128(pm + 80);
                    var s2r = System.Runtime.Intrinsics.X86.Sse2.LoadVector128(pm + 96); var s2g = System.Runtime.Intrinsics.X86.Sse2.LoadVector128(pm + 112); var s2b = System.Runtime.Intrinsics.X86.Sse2.LoadVector128(pm + 128);
                    for (; i + 16 <= n; i += 16)
                    {
                        var vr = Srgb8x16(pr + i, pc, pt); var vg = Srgb8x16(pg + i, pc, pt); var vb = Srgb8x16(pb + i, pc, pt);
                        byte* o = pd + 3L * i;
                        System.Runtime.Intrinsics.X86.Sse2.Store(o, System.Runtime.Intrinsics.X86.Sse2.Or(System.Runtime.Intrinsics.X86.Sse2.Or(
                            System.Runtime.Intrinsics.X86.Ssse3.Shuffle(vr, s0r), System.Runtime.Intrinsics.X86.Ssse3.Shuffle(vg, s0g)), System.Runtime.Intrinsics.X86.Ssse3.Shuffle(vb, s0b)));
                        System.Runtime.Intrinsics.X86.Sse2.Store(o + 16, System.Runtime.Intrinsics.X86.Sse2.Or(System.Runtime.Intrinsics.X86.Sse2.Or(
                            System.Runtime.Intrinsics.X86.Ssse3.Shuffle(vr, s1r), System.Runtime.Intrinsics.X86.Ssse3.Shuffle(vg, s1g)), System.Runtime.Intrinsics.X86.Ssse3.Shuffle(vb, s1b)));
                        System.Runtime.Intrinsics.X86.Sse2.Store(o + 32, System.Runtime.Intrinsics.X86.Sse2.Or(System.Runtime.Intrinsics.X86.Sse2.Or(
                            System.Runtime.Intrinsics.X86.Ssse3.Shuffle(vr, s2r), System.Runtime.Intrinsics.X86.Ssse3.Shuffle(vg, s2g)), System.Runtime.Intrinsics.X86.Ssse3.Shuffle(vb, s2b)));
                    }
                }
            }
            if (i < n)
            {
                LinearToSrgb8(r.Slice(i), dst.Slice(3 * i), 3);
                LinearToSrgb8(g.Slice(i), dst.Slice(3 * i + 1), 3);
                LinearToSrgb8(b.Slice(i), dst.Slice(3 * i + 2), 3);
            }
        }

        /// <summary>16 samples to bytes, as the AVX2 loop of <see cref="LinearToSrgb8"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe System.Runtime.Intrinsics.Vector128<byte> Srgb8x16(float* p, int* coarse, float* thr)
        {
            var lo = Srgb8x8(p, coarse, thr);
            var hi = Srgb8x8(p + 8, coarse, thr);
            // int -> short: per 128 bit lane, then put the lanes in order
            var s = System.Runtime.Intrinsics.X86.Avx2.PackSignedSaturate(lo, hi);
            s = System.Runtime.Intrinsics.X86.Avx2.Permute4x64(s.AsInt64(), 0xD8).AsInt16();
            return System.Runtime.Intrinsics.X86.Sse2.PackUnsignedSaturate(s.GetLower(), s.GetUpper());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe System.Runtime.Intrinsics.Vector256<int> Srgb8x8(float* p, int* coarse, float* thr)
        {
            var v = System.Runtime.Intrinsics.X86.Avx.LoadVector256(p);
            v = System.Runtime.Intrinsics.X86.Avx.Max(v, System.Runtime.Intrinsics.Vector256<float>.Zero);      // NaN -> 0
            v = System.Runtime.Intrinsics.X86.Avx.Min(v, System.Runtime.Intrinsics.Vector256.Create(0.99999994f));
            var k = System.Runtime.Intrinsics.X86.Avx.ConvertToVector256Int32WithTruncation(System.Runtime.Intrinsics.X86.Avx.Multiply(v, System.Runtime.Intrinsics.Vector256.Create((float)Srgb8Cells)));
            var b = System.Runtime.Intrinsics.X86.Avx2.GatherVector256(coarse, k, 4);
            var t = System.Runtime.Intrinsics.X86.Avx2.GatherVector256(thr, b, 4);
            var ge = System.Runtime.Intrinsics.Vector256.AsInt32(System.Runtime.Intrinsics.X86.Avx.CompareGreaterThanOrEqual(v, t));
            return System.Runtime.Intrinsics.X86.Avx2.Subtract(b, ge);
        }

        static byte[]? srgb8Shuffles;
        /// <summary>pshufb masks that interleave 16 R, G and B bytes into 48 RGB bytes (3 outputs x 3 sources).</summary>
        static byte[] Srgb8Shuffles
        {
            get
            {
                if (srgb8Shuffles is { } s) return s;
                var m = new byte[144];
                for (int o = 0; o < 3; o++)
                    for (int c = 0; c < 3; c++)
                        for (int j = 0; j < 16; j++)
                        {
                            int pos = 16 * o + j;   // output byte
                            m[(3 * o + c) * 16 + j] = pos % 3 == c ? (byte)(pos / 3) : (byte)0x80;
                        }
                return srgb8Shuffles = m;
            }
        }

        static int[]? srgb8CoarseInt;
        static int[] Srgb8CoarseInt
        {
            get
            {
                if (srgb8CoarseInt is { } c) return c;
                var coarse = Srgb8Tables.coarse;
                var ci = new int[coarse.Length];
                for (int k = 0; k < ci.Length; k++) ci[k] = coarse[k];
                return srgb8CoarseInt = ci;
            }
        }

        public static float SrgbFromLinear(float v)
        {
            if (v <= 0.0031308f) return v * 12.92f;
            return 1.055f * MathF.Pow(v, 1 / 2.4f) - 0.055f;
        }

        public static float Bt709FromLinear(float v)
        {
            if (v < 0.018f) return 4.5f * v;
            return 1.099f * MathF.Pow(v, 0.45f) - 0.099f;
        }

        public static float PqFromLinear(float v)
        {
            // v: 1.0 = 10000 nits
            const float m1 = 2610f / 16384f, m2 = 2523f / 4096f * 128f, c1 = 3424f / 4096f, c2 = 2413f / 4096f * 32f, c3 = 2392f / 4096f * 32f;
            if (v <= 0) return 0;
            float yp = MathF.Pow(v, m1);
            return MathF.Pow((c1 + c2 * yp) / (1 + c3 * yp), m2);
        }

        public static float HlgFromLinear(float v)
        {
            const float a = 0.17883277f, b = 0.28466892f, c = 0.55991073f;
            if (v <= 0) return 0;
            if (v <= 1 / 12f) return MathF.Sqrt(3 * v);
            return a * MathF.Log(12 * v - b) + c;
        }

        /// <summary>The exponent of the HLG OOTF for a peak display luminance (18181-1:2024 Table E.6, NOTE 4).</summary>
        public static float HlgGamma(float intensityTarget) => (float)(1.2 * Math.Pow(1.111, Math.Log2(intensityTarget / 1000.0)));

        /// <summary>
        /// The luminance weights of linear RGB in the encoding's primaries and white point (the Y row of its RGB to XYZ
        /// matrix).
        /// </summary>
        public static float[] LuminanceWeights(JxlColourEncoding ce)
        {
            var m = RgbToXyz(PrimariesXy(ce).r, PrimariesXy(ce).g, PrimariesXy(ce).b, WhiteXy(ce));
            return new[] { (float)m[3], (float)m[4], (float)m[5] };
        }

        /// <summary>
        /// Display referred linear light (XYB decodes to it) to the scene light HLG encodes: the inverse of the HLG OOTF,
        /// scene = display * Yd^((1 - gamma) / gamma) with Yd the display luminance (18181-1:2024 Table E.6, NOTE 4; libjxl
        /// does the same). For grey only the luminance plane is given.
        /// </summary>
        public static void HlgInverseOotf(Span<float> r, Span<float> g, Span<float> b, float[]? weights, float gamma)
        {
            if (gamma == 1) return;
            float e = (1 - gamma) / gamma;
            for (int i = 0; i < r.Length; i++)
            {
                float yd = weights == null ? r[i] : weights[0] * r[i] + weights[1] * g[i] + weights[2] * b[i];
                float f = yd > 0 ? MathF.Pow(yd, e) : 0;
                r[i] *= f;
                if (weights != null) { g[i] *= f; b[i] *= f; }
            }
        }

        /// <summary>Applies the encoding transfer function to linear values in place (sign preserving for out of range values).</summary>
        public static void FromLinear(float[] p, int n, JxlColourEncoding ce, float intensityTarget, bool parallel)
        {
            if (ce.Transfer is JxlTransfer.Linear or not (JxlTransfer.Srgb or JxlTransfer.Bt709 or JxlTransfer.Pq or JxlTransfer.Hlg or JxlTransfer.Dci or JxlTransfer.Gamma)) return;
            Chunked(n, parallel, (s, e) => FromLinear(p.AsSpan(s, e - s), ce, intensityTarget));
        }

        /// <summary>Linear light to the encoding's transfer function, in place (negative values mirrored).</summary>
        public static void FromLinear(Span<float> p, JxlColourEncoding ce, float intensityTarget)
        {
            switch (ce.Transfer)
            {
                case JxlTransfer.Srgb:
                    {
                        var lut = SrgbLut;
                        for (int i = 0; i < p.Length; i++)
                        {
                            float v = p[i];
                            if (v >= 0 && v < 1)
                            {
                                float f = v * SrgbLutSize;
                                int k = (int)f;
                                p[i] = lut[k] + (lut[k + 1] - lut[k]) * (f - k);
                            }
                            else p[i] = v < 0 ? -SrgbFromLinear(-v) : SrgbFromLinear(v);
                        }
                        return;
                    }
                case JxlTransfer.Bt709:
                    for (int i = 0; i < p.Length; i++) { float v = p[i]; p[i] = v < 0 ? -Bt709FromLinear(-v) : Bt709FromLinear(v); }
                    return;
                case JxlTransfer.Pq:
                    {
                        float sc = intensityTarget / 10000f;
                        for (int i = 0; i < p.Length; i++) { float v = p[i] * sc; p[i] = v < 0 ? -PqFromLinear(-v) : PqFromLinear(v); }
                        return;
                    }
                case JxlTransfer.Hlg:
                    for (int i = 0; i < p.Length; i++) { float v = p[i]; p[i] = v < 0 ? -HlgFromLinear(-v) : HlgFromLinear(v); }
                    return;
                case JxlTransfer.Dci:
                    for (int i = 0; i < p.Length; i++) { float v = p[i]; p[i] = v < 0 ? -MathF.Pow(-v, 1 / 2.6f) : MathF.Pow(v, 1 / 2.6f); }
                    return;
                case JxlTransfer.Gamma:
                    {
                        float g = (float)ce.Gamma;
                        for (int i = 0; i < p.Length; i++) { float v = p[i]; p[i] = v < 0 ? -MathF.Pow(-v, g) : MathF.Pow(v, g); }
                        return;
                    }
                default:
                    return;   // linear or unknown: leave linear
            }
        }

        // ------------------------------------------------------------------ primaries

        static (double x, double y) WhiteXy(JxlColourEncoding ce) => ce.WhitePoint switch
        {
            JxlWhitePoint.D65 => (0.3127, 0.3290),
            JxlWhitePoint.E => (1 / 3.0, 1 / 3.0),
            JxlWhitePoint.Dci => (0.314, 0.351),
            _ => (ce.White.x, ce.White.y),
        };

        static ((double, double) r, (double, double) g, (double, double) b) PrimariesXy(JxlColourEncoding ce) => ce.Primaries switch
        {
            JxlPrimaries.Srgb => ((0.639998686, 0.330010138), (0.300003784, 0.600003357), (0.150002046, 0.059997204)),
            JxlPrimaries.Bt2100 => ((0.708, 0.292), (0.170, 0.797), (0.131, 0.046)),
            JxlPrimaries.P3 => ((0.680, 0.320), (0.265, 0.690), (0.150, 0.060)),
            _ => ((ce.Red.x, ce.Red.y), (ce.Green.x, ce.Green.y), (ce.Blue.x, ce.Blue.y)),
        };

        /// <summary>
        /// An ICC profile describing an enumerated colour encoding the transfer function tag alone cannot (other primaries or
        /// white point, a gamma, BT.709 or DCI curve), so the returned pixels can be interpreted. PQ and HLG get a version 4.4
        /// profile with a cicp tag that names the curve (and the primaries, which the transfer function does not say).
        /// Null for sRGB and linear sRGB / gray (said by the transfer function), XYB and unknown transfers.
        /// </summary>
        public static byte[]? DescribingIcc(JxlColourEncoding ce)
        {
            if (ce.WantIcc || ce.ColourSpace is not (JxlColourSpace.Rgb or JxlColourSpace.Grey)) return null;
            IccCurve trc;
            switch (ce.Transfer)
            {
                case JxlTransfer.Pq or JxlTransfer.Hlg:
                    {
                        var (wx, wy) = WhiteXy(ce);
                        var (r, g, b) = PrimariesXy(ce);
                        bool d65 = ce.WhitePoint == JxlWhitePoint.D65;
                        // H.273 colour primaries: 1 BT.709, 9 BT.2020, 12 P3 D65, 11 P3 DCI; 2 (unspecified) for custom ones
                        int cp = ce.Primaries switch
                        {
                            JxlPrimaries.Srgb when d65 => 1, JxlPrimaries.Bt2100 when d65 => 9,
                            JxlPrimaries.P3 when d65 => 12, JxlPrimaries.P3 when ce.WhitePoint == JxlWhitePoint.Dci => 11, _ => 2,
                        };
                        if (ce.IsGray) cp = 2;
                        try
                        {
                            return IccProfile.CreateHdr(ce.Transfer == JxlTransfer.Pq ? "JPEG XL PQ" : "JPEG XL HLG", new CieXy(wx, wy), new CieXy(r.Item1, r.Item2),
                                new CieXy(g.Item1, g.Item2), new CieXy(b.Item1, b.Item2), ce.IsGray, cp, ce.Transfer == JxlTransfer.Pq ? 16 : 18)?.ToBytes();
                        }
                        catch (Exception e) when (e is ArgumentException or ImageFormatException) { return null; }
                    }
                case JxlTransfer.Srgb: trc = IccCurve.Srgb; break;
                case JxlTransfer.Linear: trc = IccCurve.Identity; break;
                case JxlTransfer.Gamma: trc = IccCurve.Gamma(1 / ce.Gamma); break;
                case JxlTransfer.Dci: trc = IccCurve.Gamma(2.6); break;
                case JxlTransfer.Bt709: trc = IccCurve.Parametric(3, 1 / 0.45, 1 / 1.099, 0.099 / 1.099, 1 / 4.5, 0.081); break;
                default: return null;
            }
            bool plainCurve = ce.Transfer is JxlTransfer.Srgb or JxlTransfer.Linear;
            if (plainCurve && ce.WhitePoint == JxlWhitePoint.D65 && (ce.IsGray || ce.Primaries == JxlPrimaries.Srgb)) return null;
            var w = WhiteXy(ce);
            var white = new CieXy(w.x, w.y);
            try
            {
                if (ce.IsGray) return IccProfile.CreateGray("JPEG XL gray", white, trc).ToBytes();
                var p = PrimariesXy(ce);
                return IccProfile.CreateRgb("JPEG XL RGB", white, new CieXy(p.r.Item1, p.r.Item2), new CieXy(p.g.Item1, p.g.Item2),
                    new CieXy(p.b.Item1, p.b.Item2), trc).ToBytes();
            }
            catch (Exception e) when (e is ArgumentException or ImageFormatException) { return null; }   // degenerate chromaticities
        }

        /// <summary>Whether linear sRGB (D65) needs a matrix to get to the encoding's primaries / white point.</summary>
        public static bool NeedsPrimaryConversion(JxlColourEncoding ce)
            => !ce.IsGray && (ce.Primaries != JxlPrimaries.Srgb || ce.WhitePoint != JxlWhitePoint.D65);

        static double[] RgbToXyz((double, double) r, (double, double) g, (double, double) b, (double x, double y) w)
        {
            double[] P =
            {
                r.Item1, g.Item1, b.Item1,
                r.Item2, g.Item2, b.Item2,
                1 - r.Item1 - r.Item2, 1 - g.Item1 - g.Item2, 1 - b.Item1 - b.Item2,
            };
            var inv = Inverse3(P);
            double wx = w.x / w.y, wy = 1, wz = (1 - w.x - w.y) / w.y;
            double sr = inv[0] * wx + inv[1] * wy + inv[2] * wz;
            double sg = inv[3] * wx + inv[4] * wy + inv[5] * wz;
            double sb = inv[6] * wx + inv[7] * wy + inv[8] * wz;
            return new[] { P[0] * sr, P[1] * sg, P[2] * sb, P[3] * sr, P[4] * sg, P[5] * sb, P[6] * sr, P[7] * sg, P[8] * sb };
        }

        static double[] Bradford((double x, double y) from, (double x, double y) to)
        {
            double[] M = { 0.8951, 0.2664, -0.1614, -0.7502, 1.7135, 0.0367, 0.0389, -0.0685, 1.0296 };
            double[] Lms(double x, double y) => Mul(M, new[] { x / y, 1, (1 - x - y) / y });
            var a = Lms(from.x, from.y); var b = Lms(to.x, to.y);
            double[] D = { b[0] / a[0], 0, 0, 0, b[1] / a[1], 0, 0, 0, b[2] / a[2] };
            return Mul3(Inverse3(M), Mul3(D, M));
        }

        /// <summary>3x3 matrix from linear sRGB (D65) to linear RGB in the encoding's primaries and white point.</summary>
        public static float[] SrgbToTargetMatrix(JxlColourEncoding ce)
        {
            var srgb = RgbToXyz((0.639998686, 0.330010138), (0.300003784, 0.600003357), (0.150002046, 0.059997204), (0.3127, 0.3290));
            var p = PrimariesXy(ce);
            var w = WhiteXy(ce);
            var target = RgbToXyz(p.r, p.g, p.b, w);
            var adapt = Bradford((0.3127, 0.3290), w);
            var m = Mul3(Inverse3(target), Mul3(adapt, srgb));
            var f = new float[9];
            for (int i = 0; i < 9; i++) f[i] = (float)m[i];
            return f;
        }

        /// <summary>3x3 matrix from linear RGB in the encoding's primaries and white point to linear sRGB (D65).</summary>
        public static float[] TargetToSrgbMatrix(JxlColourEncoding ce)
        {
            var m = SrgbToTargetMatrix(ce);
            var inv = Inverse3(Array.ConvertAll(m, v => (double)v));
            return Array.ConvertAll(inv, v => (float)v);
        }

        public static void ApplyMatrix(float[] r, float[] g, float[] b, int n, float[] m) => ApplyMatrix(r.AsSpan(0, n), g.AsSpan(0, n), b.AsSpan(0, n), m);

        public static void ApplyMatrix(Span<float> r, Span<float> g, Span<float> b, float[] m)
        {
            for (int i = 0; i < r.Length; i++)
            {
                float x = r[i], y = g[i], z = b[i];
                r[i] = m[0] * x + m[1] * y + m[2] * z;
                g[i] = m[3] * x + m[4] * y + m[5] * z;
                b[i] = m[6] * x + m[7] * y + m[8] * z;
            }
        }

        static double[] Mul(double[] m, double[] v) => new[] { m[0] * v[0] + m[1] * v[1] + m[2] * v[2], m[3] * v[0] + m[4] * v[1] + m[5] * v[2], m[6] * v[0] + m[7] * v[1] + m[8] * v[2] };

        static double[] Mul3(double[] a, double[] b)
        {
            var r = new double[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
            return r;
        }

        static double[] Inverse3(double[] m)
        {
            double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
            double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
            if (Math.Abs(det) < 1e-12) throw new ImageFormatException("JPEG XL: degenerate primaries");
            double s = 1 / det;
            return new[]
            {
                (e * i - f * h) * s, (c * h - b * i) * s, (b * f - c * e) * s,
                (f * g - d * i) * s, (a * i - c * g) * s, (c * d - a * f) * s,
                (d * h - e * g) * s, (b * g - a * h) * s, (a * e - b * d) * s,
            };
        }
    }
}
