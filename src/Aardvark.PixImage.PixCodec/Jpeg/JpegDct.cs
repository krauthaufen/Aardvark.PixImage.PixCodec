using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Aardvark.ImageCodecs.JpegImpl
{
    /// <summary>
    /// 8x8 forward and inverse DCT after Loeffler, Ligtenberg and Moschytz (11 multiplies per 1-D transform) in
    /// 13 bit fixed point. The inverse uses the same pass order, scaling and rounding as the IJG "slow integer"
    /// method, so decoded samples agree bit for bit with libjpeg based decoders.
    /// All code paths (scalar, 4 lane, 8 lane) produce identical results.
    /// </summary>
    internal static unsafe class JpegDct
    {
        const int ConstBits = 13;
        const int Pass1Bits = 2;

        // round(x * 2^13)
        const int F0_298631336 = 2446;
        const int F0_390180644 = 3196;
        const int F0_541196100 = 4433;
        const int F0_765366865 = 6270;
        const int F0_899976223 = 7373;
        const int F1_175875602 = 9633;
        const int F1_501321110 = 12299;
        const int F1_847759065 = 15137;
        const int F1_961570560 = 16069;
        const int F2_053119869 = 16819;
        const int F2_562915447 = 20995;
        const int F3_072711026 = 25172;

        /// <summary>Test hook: 0 forces the scalar path, 1 the generic Vector&lt;int&gt; path without AVX2 shuffles, -1 = automatic.</summary>
        internal static int ForceLevel = -1;

        static bool Use8 => Vector<int>.Count == 8 && Avx2.IsSupported && ForceLevel < 0;
        static bool UseVec => Vector.IsHardwareAccelerated && (Vector<int>.Count == 4 || Vector<int>.Count == 8) && ForceLevel != 0;

        // ---------------------------------------------------------------- inverse

        /// <summary>Block with only a DC coefficient: a flat 8x8 area.</summary>
        public static void IdctDc(int dc, byte* dst, nint stride)
        {
            int v = ((dc + 4) >> 3) + 128;
            ulong fill = (byte)Math.Clamp(v, 0, 255) * 0x0101010101010101UL;
            for (int y = 0; y < 8; y++, dst += stride) Unsafe.WriteUnaligned(dst, fill);
        }

        /// <summary>Dequantizes (natural order coefficients times natural order table) and inverse transforms one block.</summary>
        public static void Idct(short* coef, int* quant, byte* dst, nint stride)
        {
            if (Use8) Idct8(coef, quant, dst, stride);
            else if (UseVec) IdctVec(coef, quant, dst, stride);
            else IdctScalar(coef, quant, dst, stride);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void IdctPass(ref Vector<int> a0, ref Vector<int> a1, ref Vector<int> a2, ref Vector<int> a3,
            ref Vector<int> a4, ref Vector<int> a5, ref Vector<int> a6, ref Vector<int> a7, int shift)
        {
            var round = new Vector<int>(1 << (shift - 1));
            // even part: rotation of (2,6), butterflies with (0,4)
            var z1 = (a2 + a6) * F0_541196100;
            var t2 = z1 - a6 * F1_847759065;
            var t3 = z1 + a2 * F0_765366865;
            var t0 = Vector.ShiftLeft(a0 + a4, ConstBits) + round;
            var t1 = Vector.ShiftLeft(a0 - a4, ConstBits) + round;
            var t10 = t0 + t3; var t13 = t0 - t3;
            var t11 = t1 + t2; var t12 = t1 - t2;

            // odd part
            var o0 = a7; var o1 = a5; var o2 = a3; var o3 = a1;
            z1 = o0 + o3;
            var z2 = o1 + o2;
            var z3 = o0 + o2;
            var z4 = o1 + o3;
            var z5 = (z3 + z4) * F1_175875602;
            o0 *= F0_298631336;
            o1 *= F2_053119869;
            o2 *= F3_072711026;
            o3 *= F1_501321110;
            z1 *= -F0_899976223;
            z2 *= -F2_562915447;
            z3 = z3 * -F1_961570560 + z5;
            z4 = z4 * -F0_390180644 + z5;
            o0 += z1 + z3;
            o1 += z2 + z4;
            o2 += z2 + z3;
            o3 += z1 + z4;

            a0 = Vector.ShiftRightArithmetic(t10 + o3, shift);
            a7 = Vector.ShiftRightArithmetic(t10 - o3, shift);
            a1 = Vector.ShiftRightArithmetic(t11 + o2, shift);
            a6 = Vector.ShiftRightArithmetic(t11 - o2, shift);
            a2 = Vector.ShiftRightArithmetic(t12 + o1, shift);
            a5 = Vector.ShiftRightArithmetic(t12 - o1, shift);
            a3 = Vector.ShiftRightArithmetic(t13 + o0, shift);
            a4 = Vector.ShiftRightArithmetic(t13 - o0, shift);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Transpose8(ref Vector<int> r0, ref Vector<int> r1, ref Vector<int> r2, ref Vector<int> r3,
            ref Vector<int> r4, ref Vector<int> r5, ref Vector<int> r6, ref Vector<int> r7)
        {
            var t0 = Avx2.UnpackLow(r0.AsVector256(), r1.AsVector256()).AsInt64();
            var t1 = Avx2.UnpackHigh(r0.AsVector256(), r1.AsVector256()).AsInt64();
            var t2 = Avx2.UnpackLow(r2.AsVector256(), r3.AsVector256()).AsInt64();
            var t3 = Avx2.UnpackHigh(r2.AsVector256(), r3.AsVector256()).AsInt64();
            var t4 = Avx2.UnpackLow(r4.AsVector256(), r5.AsVector256()).AsInt64();
            var t5 = Avx2.UnpackHigh(r4.AsVector256(), r5.AsVector256()).AsInt64();
            var t6 = Avx2.UnpackLow(r6.AsVector256(), r7.AsVector256()).AsInt64();
            var t7 = Avx2.UnpackHigh(r6.AsVector256(), r7.AsVector256()).AsInt64();
            var u0 = Avx2.UnpackLow(t0, t2); var u1 = Avx2.UnpackHigh(t0, t2);
            var u2 = Avx2.UnpackLow(t1, t3); var u3 = Avx2.UnpackHigh(t1, t3);
            var u4 = Avx2.UnpackLow(t4, t6); var u5 = Avx2.UnpackHigh(t4, t6);
            var u6 = Avx2.UnpackLow(t5, t7); var u7 = Avx2.UnpackHigh(t5, t7);
            r0 = Avx2.Permute2x128(u0, u4, 0x20).AsInt32().AsVector();
            r1 = Avx2.Permute2x128(u1, u5, 0x20).AsInt32().AsVector();
            r2 = Avx2.Permute2x128(u2, u6, 0x20).AsInt32().AsVector();
            r3 = Avx2.Permute2x128(u3, u7, 0x20).AsInt32().AsVector();
            r4 = Avx2.Permute2x128(u0, u4, 0x31).AsInt32().AsVector();
            r5 = Avx2.Permute2x128(u1, u5, 0x31).AsInt32().AsVector();
            r6 = Avx2.Permute2x128(u2, u6, 0x31).AsInt32().AsVector();
            r7 = Avx2.Permute2x128(u3, u7, 0x31).AsInt32().AsVector();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void StoreRow8(Vector<int> v, byte* dst)
        {
            var x = (v + new Vector<int>(128)).AsVector256();
            var s = Sse2.PackSignedSaturate(x.GetLower(), x.GetUpper());
            Sse2.StoreScalar((long*)dst, Sse2.PackUnsignedSaturate(s, s).AsInt64());
        }

        // kept out of line: inlined into Idct, the JIT runs out of inlining budget for the passes below
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Idct8(short* coef, int* quant, byte* dst, nint stride)
        {
            Vector.Widen(Vector.Load(coef), out var r0, out var r1);
            Vector.Widen(Vector.Load(coef + 16), out var r2, out var r3);
            Vector.Widen(Vector.Load(coef + 32), out var r4, out var r5);
            Vector.Widen(Vector.Load(coef + 48), out var r6, out var r7);
            r0 *= Vector.Load(quant); r1 *= Vector.Load(quant + 8);
            r2 *= Vector.Load(quant + 16); r3 *= Vector.Load(quant + 24);
            r4 *= Vector.Load(quant + 32); r5 *= Vector.Load(quant + 40);
            r6 *= Vector.Load(quant + 48); r7 *= Vector.Load(quant + 56);
            IdctPass(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, ConstBits - Pass1Bits);
            Transpose8(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
            IdctPass(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, ConstBits + Pass1Bits + 3);
            Transpose8(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
            StoreRow8(r0, dst); dst += stride;
            StoreRow8(r1, dst); dst += stride;
            StoreRow8(r2, dst); dst += stride;
            StoreRow8(r3, dst); dst += stride;
            StoreRow8(r4, dst); dst += stride;
            StoreRow8(r5, dst); dst += stride;
            StoreRow8(r6, dst); dst += stride;
            StoreRow8(r7, dst);
        }

        static void TransposeInPlace(int* m)
        {
            for (int i = 0; i < 8; i++)
                for (int j = i + 1; j < 8; j++)
                    (m[i * 8 + j], m[j * 8 + i]) = (m[j * 8 + i], m[i * 8 + j]);
        }

        /// <summary>Runs one 1-D pass down the columns of the 8x8 int matrix using whatever vector width is available.</summary>
        static void VecPass(int* m, int shift, bool inverse)
        {
            int n = Vector<int>.Count;
            for (int x = 0; x < 8; x += n)
            {
                var a0 = Vector.Load(m + x); var a1 = Vector.Load(m + 8 + x);
                var a2 = Vector.Load(m + 16 + x); var a3 = Vector.Load(m + 24 + x);
                var a4 = Vector.Load(m + 32 + x); var a5 = Vector.Load(m + 40 + x);
                var a6 = Vector.Load(m + 48 + x); var a7 = Vector.Load(m + 56 + x);
                if (inverse) IdctPass(ref a0, ref a1, ref a2, ref a3, ref a4, ref a5, ref a6, ref a7, shift);
                else FdctPass(ref a0, ref a1, ref a2, ref a3, ref a4, ref a5, ref a6, ref a7, shift != 0);
                Vector.Store(a0, m + x); Vector.Store(a1, m + 8 + x);
                Vector.Store(a2, m + 16 + x); Vector.Store(a3, m + 24 + x);
                Vector.Store(a4, m + 32 + x); Vector.Store(a5, m + 40 + x);
                Vector.Store(a6, m + 48 + x); Vector.Store(a7, m + 56 + x);
            }
        }

        static void IdctVec(short* coef, int* quant, byte* dst, nint stride)
        {
            int* m = stackalloc int[64];
            for (int i = 0; i < 64; i++) m[i] = coef[i] * quant[i];
            VecPass(m, ConstBits - Pass1Bits, true);
            TransposeInPlace(m);
            VecPass(m, ConstBits + Pass1Bits + 3, true);
            for (int y = 0; y < 8; y++, dst += stride)
                for (int x = 0; x < 8; x++)
                    dst[x] = (byte)Math.Clamp(m[x * 8 + y] + 128, 0, 255);
        }

        static void IdctScalar(short* coef, int* quant, byte* dst, nint stride)
        {
            int* m = stackalloc int[64];
            for (int i = 0; i < 64; i++) m[i] = coef[i] * quant[i];
            for (int x = 0; x < 8; x++) IdctScalar1D(m + x, 8, ConstBits - Pass1Bits);
            for (int y = 0; y < 8; y++) IdctScalar1D(m + y * 8, 1, ConstBits + Pass1Bits + 3);
            for (int y = 0; y < 8; y++, dst += stride)
                for (int x = 0; x < 8; x++)
                    dst[x] = (byte)Math.Clamp(m[y * 8 + x] + 128, 0, 255);
        }

        static void IdctScalar1D(int* p, int s, int shift)
        {
            int round = 1 << (shift - 1);
            int a0 = p[0], a1 = p[s], a2 = p[2 * s], a3 = p[3 * s], a4 = p[4 * s], a5 = p[5 * s], a6 = p[6 * s], a7 = p[7 * s];
            int z1 = (a2 + a6) * F0_541196100;
            int t2 = z1 - a6 * F1_847759065;
            int t3 = z1 + a2 * F0_765366865;
            int t0 = ((a0 + a4) << ConstBits) + round;
            int t1 = ((a0 - a4) << ConstBits) + round;
            int t10 = t0 + t3, t13 = t0 - t3, t11 = t1 + t2, t12 = t1 - t2;

            int o0 = a7, o1 = a5, o2 = a3, o3 = a1;
            z1 = o0 + o3;
            int z2 = o1 + o2, z3 = o0 + o2, z4 = o1 + o3;
            int z5 = (z3 + z4) * F1_175875602;
            o0 *= F0_298631336;
            o1 *= F2_053119869;
            o2 *= F3_072711026;
            o3 *= F1_501321110;
            z1 *= -F0_899976223;
            z2 *= -F2_562915447;
            z3 = z3 * -F1_961570560 + z5;
            z4 = z4 * -F0_390180644 + z5;
            o0 += z1 + z3;
            o1 += z2 + z4;
            o2 += z2 + z3;
            o3 += z1 + z4;

            p[0] = (t10 + o3) >> shift; p[7 * s] = (t10 - o3) >> shift;
            p[s] = (t11 + o2) >> shift; p[6 * s] = (t11 - o2) >> shift;
            p[2 * s] = (t12 + o1) >> shift; p[5 * s] = (t12 - o1) >> shift;
            p[3 * s] = (t13 + o0) >> shift; p[4 * s] = (t13 - o0) >> shift;
        }

        // ---------------------------------------------------------------- inverse, any precision / reduced size

        // Reduced size outputs are the exact box averages of the full 8 point inverse transform. Averaging pairs
        // (4 outputs) or quadruples (2 outputs) of its cosines collapses to one cosine per coefficient:
        //   4: sqrt2 cos(u pi/16) cos((2x+1) u pi/8)             (zero for u = 4)
        //   2: sqrt2 cos(u pi/16) cos(u pi/8) cos((2x+1) u pi/4)  (zero for even u)
        // The tables hold these in 13 bit fixed point, pre-scaled by 2 resp. 4 like the DC term below.
        // Entry 0 of every row is the DC weight (1.0 at the same scale).
        static readonly int[] s_reduce4 = ReductionTable(4), s_reduce2 = ReductionTable(2);

        static int[] ReductionTable(int n)
        {
            var t = GC.AllocateArray<int>(n * 8, pinned: true);
            for (int x = 0; x < n; x++)
            {
                t[x * 8] = 1 << (ConstBits + (n == 4 ? 1 : 2));
                for (int u = 1; u < 8; u++)
                {
                    double v = Math.Sqrt(2) * Math.Cos(u * Math.PI / 16);
                    v *= n == 4 ? 2 * Math.Cos((2 * x + 1) * u * Math.PI / 8) : 4 * Math.Cos(u * Math.PI / 8) * Math.Cos((2 * x + 1) * u * Math.PI / 4);
                    int fix = (int)(Math.Abs(v) * (1 << ConstBits) + 0.5);
                    t[x * 8 + u] = Math.Abs(v) < 1e-9 ? 0 : v < 0 ? -fix : fix;
                }
            }
            return t;
        }

        // Other sizes (k/8 scaling, k = 3, 5, 6, 7): output x is the box average of the 8 point inverse transform over
        // [8x/n, 8(x+1)/n), the samples it covers weighted by their overlap, exactly as for 4 and 2 but with
        // fractional coverage. Unscaled (DC weight 1.0), so these sizes need no extra shift.
        static readonly int[]?[] s_reduceN = new int[]?[9];

        static int[] Reduction(int n) => n == 4 ? s_reduce4 : n == 2 ? s_reduce2 : s_reduceN[n] ??= FractionalReductionTable(n);

        static int[] FractionalReductionTable(int n)
        {
            var t = GC.AllocateArray<int>(n * 8, pinned: true);
            for (int x = 0; x < n; x++)
            {
                double a = 8.0 * x / n, b = 8.0 * (x + 1) / n;
                for (int u = 0; u < 8; u++)
                {
                    double v = 0;
                    for (int i = 0; i < 8; i++)
                    {
                        double w = Math.Max(0, Math.Min(b, i + 1) - Math.Max(a, i));
                        v += w * (u == 0 ? 1 : Math.Sqrt(2) * Math.Cos((2 * i + 1) * u * Math.PI / 16));
                    }
                    v *= n / 8.0;
                    int fix = (int)(Math.Abs(v) * (1 << ConstBits) + 0.5);
                    t[x * 8 + u] = Math.Abs(v) < 1e-9 ? 0 : v < 0 ? -fix : fix;
                }
            }
            return t;
        }

        /// <summary>
        /// Inverse DCT for the cases the fast 8 bit path does not cover: 12 bit samples (one guard bit less in the
        /// first pass) and DCT domain downscaling to size x size outputs (1 to 8; see the tables above). Writes size * size values,
        /// row major, without level shift or clamping.
        /// </summary>
        [SkipLocalsInit]
        public static void IdctGeneric(short* coef, int* quant, int size, int pass1Bits, int* output)
        {
            if (size == 1)
            {
                output[0] = (coef[0] * quant[0] + 4) >> 3;
                return;
            }
            int* m = stackalloc int[64];
            if (size == 8)
            {
                for (int i = 0; i < 64; i++) m[i] = coef[i] * quant[i];
                for (int x = 0; x < 8; x++) IdctScalar1D(m + x, 8, ConstBits - pass1Bits);
                for (int y = 0; y < 8; y++) IdctScalar1D(m + y * 8, 1, ConstBits + pass1Bits + 3);
                for (int i = 0; i < 64; i++) output[i] = m[i];
                return;
            }
            int extra = size == 4 ? 1 : size == 2 ? 2 : 0;
            int shift1 = ConstBits - pass1Bits + extra, shift2 = ConstBits + pass1Bits + 3 + extra;
            fixed (int* k = Reduction(size))
            {
                if (Vector<int>.Count == 8 && Vector.IsHardwareAccelerated && ForceLevel != 0)
                {
                    Vector.Widen(Vector.Load(coef), out var r0, out var r1);
                    Vector.Widen(Vector.Load(coef + 16), out var r2, out var r3);
                    Vector.Widen(Vector.Load(coef + 32), out var r4, out var r5);
                    Vector.Widen(Vector.Load(coef + 48), out var r6, out var r7);
                    r0 *= Vector.Load(quant); r1 *= Vector.Load(quant + 8);
                    r2 *= Vector.Load(quant + 16); r3 *= Vector.Load(quant + 24);
                    r4 *= Vector.Load(quant + 32); r5 *= Vector.Load(quant + 40);
                    r6 *= Vector.Load(quant + 48); r7 *= Vector.Load(quant + 56);
                    var round1 = new Vector<int>(1 << (shift1 - 1));
                    int round2 = 1 << (shift2 - 1);
                    for (int y = 0; y < size; y++)
                    {
                        // columns: every lane is one column's weighted sum of its 8 coefficients
                        int* ky = k + y * 8;
                        var w = r0 * ky[0] + r1 * ky[1] + r2 * ky[2] + r3 * ky[3] + r4 * ky[4] + r5 * ky[5] + r6 * ky[6] + r7 * ky[7] + round1;
                        w = Vector.ShiftRightArithmetic(w, shift1);
                        // rows: one dot product per output sample
                        for (int x = 0; x < size; x++) output[y * size + x] = (Vector.Dot(w, Vector.Load(k + x * 8)) + round2) >> shift2;
                    }
                    return;
                }
                for (int i = 0; i < 64; i++) m[i] = coef[i] * quant[i];
                int* ws = stackalloc int[size * 8];
                for (int x = 0; x < 8; x++)
                    for (int y = 0; y < size; y++)
                    {
                        int acc = 1 << (shift1 - 1);
                        for (int u = 0; u < 8; u++) acc += k[y * 8 + u] * m[u * 8 + x];
                        ws[y * 8 + x] = acc >> shift1;
                    }
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        int acc = 1 << (shift2 - 1);
                        for (int u = 0; u < 8; u++) acc += k[x * 8 + u] * ws[y * 8 + u];
                        output[y * size + x] = acc >> shift2;
                    }
            }
        }

        public static bool HasWide => Use8;

        /// <summary>
        /// Full size inverse DCT for 16 bit planes (12 bit samples) with 8 lane vectors; same arithmetic as the scalar
        /// path of <see cref="IdctGeneric"/>, including level shift and clamping to 0..max.
        /// </summary>
        public static void Idct8Wide(short* coef, int* quant, ushort* dst, nint stride, int pass1Bits, int center, int max)
        {
            Vector.Widen(Vector.Load(coef), out var r0, out var r1);
            Vector.Widen(Vector.Load(coef + 16), out var r2, out var r3);
            Vector.Widen(Vector.Load(coef + 32), out var r4, out var r5);
            Vector.Widen(Vector.Load(coef + 48), out var r6, out var r7);
            r0 *= Vector.Load(quant); r1 *= Vector.Load(quant + 8);
            r2 *= Vector.Load(quant + 16); r3 *= Vector.Load(quant + 24);
            r4 *= Vector.Load(quant + 32); r5 *= Vector.Load(quant + 40);
            r6 *= Vector.Load(quant + 48); r7 *= Vector.Load(quant + 56);
            IdctPass(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, ConstBits - pass1Bits);
            Transpose8(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
            IdctPass(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, ConstBits + pass1Bits + 3);
            Transpose8(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
            var c = new Vector<int>(center); var hi = new Vector<int>(max);
            StoreRow16(r0, c, hi, dst); dst += stride;
            StoreRow16(r1, c, hi, dst); dst += stride;
            StoreRow16(r2, c, hi, dst); dst += stride;
            StoreRow16(r3, c, hi, dst); dst += stride;
            StoreRow16(r4, c, hi, dst); dst += stride;
            StoreRow16(r5, c, hi, dst); dst += stride;
            StoreRow16(r6, c, hi, dst); dst += stride;
            StoreRow16(r7, c, hi, dst);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void StoreRow16(Vector<int> v, Vector<int> center, Vector<int> max, ushort* dst)
        {
            var x = Vector.Min(Vector.Max(v + center, Vector<int>.Zero), max).AsVector256();
            Sse2.Store(dst, Sse41.PackUnsignedSaturate(x.GetLower(), x.GetUpper()));
        }

        /// <summary>Forward DCT + quantization of an 8x8 block of 16 bit samples (12 bit precision); zigzag ordered output.</summary>
        [SkipLocalsInit]
        public static void FdctQuantizeWide(ushort* src, nint stride, int center, float* recip, short* zigzagOut)
        {
            int* m = stackalloc int[64];
            if (Use8)
            {
                var bias = new Vector<int>(center);
                var r0 = LoadRow16(src) - bias; var r1 = LoadRow16(src + stride) - bias;
                var r2 = LoadRow16(src + 2 * stride) - bias; var r3 = LoadRow16(src + 3 * stride) - bias;
                var r4 = LoadRow16(src + 4 * stride) - bias; var r5 = LoadRow16(src + 5 * stride) - bias;
                var r6 = LoadRow16(src + 6 * stride) - bias; var r7 = LoadRow16(src + 7 * stride) - bias;
                FdctPass(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, false, 1);
                Transpose8(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
                FdctPass(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, true, 1);
                Vector.Store(Quantize(r0, recip), m); Vector.Store(Quantize(r1, recip + 8), m + 8);
                Vector.Store(Quantize(r2, recip + 16), m + 16); Vector.Store(Quantize(r3, recip + 24), m + 24);
                Vector.Store(Quantize(r4, recip + 32), m + 32); Vector.Store(Quantize(r5, recip + 40), m + 40);
                Vector.Store(Quantize(r6, recip + 48), m + 48); Vector.Store(Quantize(r7, recip + 56), m + 56);
                byte* zz = JpegTables.ZigZagTransposed;
                for (int i = 0; i < 64; i++) zigzagOut[i] = (short)Math.Clamp(m[zz[i]], -32767, 32767);
                return;
            }
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++) m[y * 8 + x] = src[y * stride + x] - center;
            FdctQuantizeGeneric(m, 1, recip, zigzagOut);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector<int> LoadRow16(ushort* p) => Avx2.ConvertToVector256Int32(Sse2.LoadVector128(p)).AsVector();

        public static bool HasIdct4 => Avx2.IsSupported && ForceLevel < 0;

        /// <summary>
        /// 8 bit half size inverse DCT (8x8 coefficients to 4x4 samples) with AVX2; same arithmetic as
        /// <see cref="IdctGeneric"/>, using that the weights are symmetric (even coefficients) resp. antisymmetric
        /// (odd ones) between output x and 3 - x.
        /// </summary>
        public static void Idct4(short* coef, int* quant, byte* dst, nint stride)
        {
            fixed (int* k = s_reduce4)
            {
                int* k1 = k + 8;
                var r0 = Avx2.MultiplyLow(Avx2.ConvertToVector256Int32(coef), Avx.LoadVector256(quant));
                var r1 = Avx2.MultiplyLow(Avx2.ConvertToVector256Int32(coef + 8), Avx.LoadVector256(quant + 8));
                var r2 = Avx2.MultiplyLow(Avx2.ConvertToVector256Int32(coef + 16), Avx.LoadVector256(quant + 16));
                var r3 = Avx2.MultiplyLow(Avx2.ConvertToVector256Int32(coef + 24), Avx.LoadVector256(quant + 24));
                var r5 = Avx2.MultiplyLow(Avx2.ConvertToVector256Int32(coef + 40), Avx.LoadVector256(quant + 40));
                var r6 = Avx2.MultiplyLow(Avx2.ConvertToVector256Int32(coef + 48), Avx.LoadVector256(quant + 48));
                var r7 = Avx2.MultiplyLow(Avx2.ConvertToVector256Int32(coef + 56), Avx.LoadVector256(quant + 56));

                // columns
                const int shift1 = ConstBits - Pass1Bits + 1, shift2 = ConstBits + Pass1Bits + 3 + 1;
                var dc = Avx2.ShiftLeftLogical(r0, ConstBits + 1) + Vector256.Create(1 << (shift1 - 1));
                var ea = dc + r2 * k[2] + r6 * k[6];
                var eb = dc + r2 * k1[2] + r6 * k1[6];
                var oa = r1 * k[1] + r3 * k[3] + r5 * k[5] + r7 * k[7];
                var ob = r1 * k1[1] + r3 * k1[3] + r5 * k1[5] + r7 * k1[7];
                var w0 = Avx2.ShiftRightArithmetic(ea + oa, shift1);
                var w3 = Avx2.ShiftRightArithmetic(ea - oa, shift1);
                var w1 = Avx2.ShiftRightArithmetic(eb + ob, shift1);
                var w2 = Avx2.ShiftRightArithmetic(eb - ob, shift1);

                // 4 rows of 8 -> columns (j in the lower, j + 4 in the upper half)
                var t0 = Avx2.UnpackLow(w0, w1).AsInt64(); var t1 = Avx2.UnpackHigh(w0, w1).AsInt64();
                var t2 = Avx2.UnpackLow(w2, w3).AsInt64(); var t3 = Avx2.UnpackHigh(w2, w3).AsInt64();
                var u0 = Avx2.UnpackLow(t0, t2).AsInt32(); var u1 = Avx2.UnpackHigh(t0, t2).AsInt32();
                var u2 = Avx2.UnpackLow(t1, t3).AsInt32(); var u3 = Avx2.UnpackHigh(t1, t3).AsInt32();
                Vector128<int> c1 = u1.GetLower(), c5 = u1.GetUpper(), c2 = u2.GetLower(), c6 = u2.GetUpper(), c3 = u3.GetLower(), c7 = u3.GetUpper();

                // rows
                var dc2 = Sse2.ShiftLeftLogical(u0.GetLower(), ConstBits + 1) + Vector128.Create(1 << (shift2 - 1));
                var fa = dc2 + c2 * k[2] + c6 * k[6];
                var fb = dc2 + c2 * k1[2] + c6 * k1[6];
                var pa = c1 * k[1] + c3 * k[3] + c5 * k[5] + c7 * k[7];
                var pb = c1 * k1[1] + c3 * k1[3] + c5 * k1[5] + c7 * k1[7];
                var bias = Vector128.Create(128);
                var x0 = Sse2.ShiftRightArithmetic(fa + pa, shift2) + bias;
                var x3 = Sse2.ShiftRightArithmetic(fa - pa, shift2) + bias;
                var x1 = Sse2.ShiftRightArithmetic(fb + pb, shift2) + bias;
                var x2 = Sse2.ShiftRightArithmetic(fb - pb, shift2) + bias;

                // columns back to rows, clamp, store
                var a0 = Sse2.UnpackLow(x0, x1).AsInt64(); var a1 = Sse2.UnpackHigh(x0, x1).AsInt64();
                var a2 = Sse2.UnpackLow(x2, x3).AsInt64(); var a3 = Sse2.UnpackHigh(x2, x3).AsInt64();
                var top = Sse2.PackSignedSaturate(Sse2.UnpackLow(a0, a2).AsInt32(), Sse2.UnpackHigh(a0, a2).AsInt32());
                var bottom = Sse2.PackSignedSaturate(Sse2.UnpackLow(a1, a3).AsInt32(), Sse2.UnpackHigh(a1, a3).AsInt32());
                var bytes = Sse2.PackUnsignedSaturate(top, bottom).AsUInt32();
                Unsafe.WriteUnaligned(dst, bytes.GetElement(0));
                Unsafe.WriteUnaligned(dst + stride, bytes.GetElement(1));
                Unsafe.WriteUnaligned(dst + 2 * stride, bytes.GetElement(2));
                Unsafe.WriteUnaligned(dst + 3 * stride, bytes.GetElement(3));
            }
        }

        /// <summary>Scalar forward DCT + quantization for sample precisions above 8 bit (input already level shifted, natural order).</summary>
        [SkipLocalsInit]
        public static void FdctQuantizeGeneric(int* m, int pass1Bits, float* recip, short* zigzagOut)
        {
            for (int x = 0; x < 8; x++) FdctScalar1D(m + x, 8, false, pass1Bits);
            TransposeInPlace(m);
            for (int x = 0; x < 8; x++) FdctScalar1D(m + x, 8, true, pass1Bits);
            byte* zz = JpegTables.ZigZagTransposed;
            for (int i = 0; i < 64; i++)
            {
                float f = m[zz[i]] * recip[zz[i]];
                zigzagOut[i] = (short)Math.Clamp((int)(f + (f < 0 ? -0.5f : 0.5f)), -32767, 32767);
            }
        }

        // ---------------------------------------------------------------- forward

        /// <summary>
        /// Forward DCT of an 8x8 sample block (level shifted by -128) followed by quantization with the reciprocal
        /// table (1 / (8 * q), natural order). Writes the 64 quantized coefficients in zigzag order.
        /// </summary>
        [SkipLocalsInit]
        public static void FdctQuantize(byte* src, nint stride, float* recip, short* zigzagOut)
        {
            int* m = stackalloc int[64];
            if (Use8)
            {
                var bias = new Vector<int>(128);
                var r0 = LoadRow8(src) - bias; var r1 = LoadRow8(src + stride) - bias;
                var r2 = LoadRow8(src + 2 * stride) - bias; var r3 = LoadRow8(src + 3 * stride) - bias;
                var r4 = LoadRow8(src + 4 * stride) - bias; var r5 = LoadRow8(src + 5 * stride) - bias;
                var r6 = LoadRow8(src + 6 * stride) - bias; var r7 = LoadRow8(src + 7 * stride) - bias;
                FdctPass(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, false);
                Transpose8(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
                FdctPass(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, true);
                // the matrix is transposed now; the transposed zigzag table below undoes that
                Vector.Store(Quantize(r0, recip), m); Vector.Store(Quantize(r1, recip + 8), m + 8);
                Vector.Store(Quantize(r2, recip + 16), m + 16); Vector.Store(Quantize(r3, recip + 24), m + 24);
                Vector.Store(Quantize(r4, recip + 32), m + 32); Vector.Store(Quantize(r5, recip + 40), m + 40);
                Vector.Store(Quantize(r6, recip + 48), m + 48); Vector.Store(Quantize(r7, recip + 56), m + 56);
            }
            else
            {
                // m is kept transposed (m[x * 8 + y]) so both paths share the same layout
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        m[y * 8 + x] = src[y * stride + x] - 128;
                if (UseVec)
                {
                    VecPass(m, 0, false);
                    TransposeInPlace(m);
                    VecPass(m, 1, false);
                    int n = Vector<int>.Count;
                    for (int i = 0; i < 64; i += n) Vector.Store(Quantize(Vector.Load(m + i), recip + i), m + i);
                }
                else
                {
                    for (int x = 0; x < 8; x++) FdctScalar1D(m + x, 8, false);
                    TransposeInPlace(m);
                    for (int x = 0; x < 8; x++) FdctScalar1D(m + x, 8, true);
                    for (int i = 0; i < 64; i++)
                    {
                        float f = m[i] * recip[i];
                        m[i] = (int)(f + (f < 0 ? -0.5f : 0.5f));
                    }
                }
            }
            byte* zz = JpegTables.ZigZagTransposed;
            for (int i = 0; i < 64; i += 4)
            {
                zigzagOut[i] = (short)m[zz[i]];
                zigzagOut[i + 1] = (short)m[zz[i + 1]];
                zigzagOut[i + 2] = (short)m[zz[i + 2]];
                zigzagOut[i + 3] = (short)m[zz[i + 3]];
            }
        }

        /// <summary>
        /// Like <see cref="FdctQuantize"/>, but leaves the 64 coefficients in transposed (column major) order: with
        /// AVX2 that is how they come out of the transform, and the block coder can walk them through an index table.
        /// </summary>
        [SkipLocalsInit]
        public static void FdctQuantizeTransposed(byte* src, nint stride, float* recip, short* output)
        {
            if (!Use8)
            {
                short* zz = stackalloc short[64];
                FdctQuantize(src, stride, recip, zz);
                byte* order = JpegTables.ZigZagTransposed;
                for (int i = 0; i < 64; i++) output[order[i]] = zz[i];
                return;
            }
            var bias = new Vector<int>(128);
            var r0 = LoadRow8(src) - bias; var r1 = LoadRow8(src + stride) - bias;
            var r2 = LoadRow8(src + 2 * stride) - bias; var r3 = LoadRow8(src + 3 * stride) - bias;
            var r4 = LoadRow8(src + 4 * stride) - bias; var r5 = LoadRow8(src + 5 * stride) - bias;
            var r6 = LoadRow8(src + 6 * stride) - bias; var r7 = LoadRow8(src + 7 * stride) - bias;
            FdctPass(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, false);
            Transpose8(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
            FdctPass(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, true);
            StorePair(Quantize(r0, recip), Quantize(r1, recip + 8), output);
            StorePair(Quantize(r2, recip + 16), Quantize(r3, recip + 24), output + 16);
            StorePair(Quantize(r4, recip + 32), Quantize(r5, recip + 40), output + 32);
            StorePair(Quantize(r6, recip + 48), Quantize(r7, recip + 56), output + 48);
        }

        /// <summary>Two rows of 8 ints to 16 shorts.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void StorePair(Vector<int> a, Vector<int> b, short* dst)
            => Avx.Store(dst, Avx2.Permute4x64(Avx2.PackSignedSaturate(a.AsVector256(), b.AsVector256()).AsInt64(), 0b11011000).AsInt16());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector<int> LoadRow8(byte* p)
            => Avx2.ConvertToVector256Int32(Sse2.LoadScalarVector128((ulong*)p).AsByte()).AsVector();

        /// <summary>recip is indexed transposed relative to the natural order (see <see cref="FdctQuantize"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector<int> Quantize(Vector<int> v, float* recip)
        {
            var f = Vector.ConvertToSingle(v) * Vector.Load(recip);
            // round half away from zero: add 0.5 carrying the sign of f, then truncate
            var half = Vector.BitwiseOr(Vector.BitwiseAnd(f, new Vector<float>(-0.0f)), new Vector<float>(0.5f));
            return Vector.ConvertToInt32(f + half);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void FdctPass(ref Vector<int> a0, ref Vector<int> a1, ref Vector<int> a2, ref Vector<int> a3,
            ref Vector<int> a4, ref Vector<int> a5, ref Vector<int> a6, ref Vector<int> a7, bool second, int pass1 = Pass1Bits)
        {
            var t0 = a0 + a7; var t7 = a0 - a7;
            var t1 = a1 + a6; var t6 = a1 - a6;
            var t2 = a2 + a5; var t5 = a2 - a5;
            var t3 = a3 + a4; var t4 = a3 - a4;
            var t10 = t0 + t3; var t13 = t0 - t3;
            var t11 = t1 + t2; var t12 = t1 - t2;

            int shift = second ? ConstBits + pass1 : ConstBits - pass1;
            var round = new Vector<int>(1 << (shift - 1));
            if (second)
            {
                var r2 = new Vector<int>(1 << (pass1 - 1));
                a0 = Vector.ShiftRightArithmetic(t10 + t11 + r2, pass1);
                a4 = Vector.ShiftRightArithmetic(t10 - t11 + r2, pass1);
            }
            else
            {
                a0 = Vector.ShiftLeft(t10 + t11, pass1);
                a4 = Vector.ShiftLeft(t10 - t11, pass1);
            }
            var z1 = (t12 + t13) * F0_541196100 + round;
            a2 = Vector.ShiftRightArithmetic(z1 + t13 * F0_765366865, shift);
            a6 = Vector.ShiftRightArithmetic(z1 - t12 * F1_847759065, shift);

            z1 = t4 + t7;
            var z2 = t5 + t6;
            var z3 = t4 + t6;
            var z4 = t5 + t7;
            var z5 = (z3 + z4) * F1_175875602 + round;
            t4 *= F0_298631336;
            t5 *= F2_053119869;
            t6 *= F3_072711026;
            t7 *= F1_501321110;
            z1 *= -F0_899976223;
            z2 *= -F2_562915447;
            z3 = z3 * -F1_961570560 + z5;
            z4 = z4 * -F0_390180644 + z5;
            a7 = Vector.ShiftRightArithmetic(t4 + z1 + z3, shift);
            a5 = Vector.ShiftRightArithmetic(t5 + z2 + z4, shift);
            a3 = Vector.ShiftRightArithmetic(t6 + z2 + z3, shift);
            a1 = Vector.ShiftRightArithmetic(t7 + z1 + z4, shift);
        }

        static void FdctScalar1D(int* p, int s, bool second, int pass1 = Pass1Bits)
        {
            int a0 = p[0], a1 = p[s], a2 = p[2 * s], a3 = p[3 * s], a4 = p[4 * s], a5 = p[5 * s], a6 = p[6 * s], a7 = p[7 * s];
            int t0 = a0 + a7, t7 = a0 - a7, t1 = a1 + a6, t6 = a1 - a6, t2 = a2 + a5, t5 = a2 - a5, t3 = a3 + a4, t4 = a3 - a4;
            int t10 = t0 + t3, t13 = t0 - t3, t11 = t1 + t2, t12 = t1 - t2;
            int shift = second ? ConstBits + pass1 : ConstBits - pass1;
            int round = 1 << (shift - 1);
            if (second)
            {
                p[0] = (t10 + t11 + (1 << (pass1 - 1))) >> pass1;
                p[4 * s] = (t10 - t11 + (1 << (pass1 - 1))) >> pass1;
            }
            else
            {
                p[0] = (t10 + t11) << pass1;
                p[4 * s] = (t10 - t11) << pass1;
            }
            int z1 = (t12 + t13) * F0_541196100 + round;
            p[2 * s] = (z1 + t13 * F0_765366865) >> shift;
            p[6 * s] = (z1 - t12 * F1_847759065) >> shift;

            z1 = t4 + t7;
            int z2 = t5 + t6, z3 = t4 + t6, z4 = t5 + t7;
            int z5 = (z3 + z4) * F1_175875602 + round;
            t4 *= F0_298631336;
            t5 *= F2_053119869;
            t6 *= F3_072711026;
            t7 *= F1_501321110;
            z1 *= -F0_899976223;
            z2 *= -F2_562915447;
            z3 = z3 * -F1_961570560 + z5;
            z4 = z4 * -F0_390180644 + z5;
            p[7 * s] = (t4 + z1 + z3) >> shift;
            p[5 * s] = (t5 + z2 + z4) >> shift;
            p[3 * s] = (t6 + z2 + z3) >> shift;
            p[s] = (t7 + z1 + z4) >> shift;
        }
    }
}
