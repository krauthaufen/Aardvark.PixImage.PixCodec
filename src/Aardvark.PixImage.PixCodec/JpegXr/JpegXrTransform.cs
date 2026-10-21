using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Aardvark.ImageCodecs.JpegXrImpl
{
    /// <summary>
    /// The lifting steps of the JPEG XR lapped transform: the inverse core transform and overlap post-filters of T.832
    /// 9.9.7 / 9.9.8, the forward core transform and overlap pre-filters of Annex D, and the geometric application of the
    /// overlap filters to a component (9.9.3, 9.9.6, D.5).
    /// </summary>
    internal static class JxrTransform
    {
        // ------------------------------------------------------------------ inverse (9.9.7, 9.9.8)

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void T2x2h(ref int a, ref int b, ref int c, ref int d, int round)
        {
            a += d;
            b -= c;
            int t1 = (a - b + round) >> 1;
            int t2 = c;
            c = t1 - d;
            d = t1 - t2;
            a -= d;
            b += c;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void InvTodd(ref int a, ref int b, ref int c, ref int d)
        {
            b += d;
            a -= c;
            d -= b >> 1;
            c += (a + 1) >> 1;
            a -= (3 * b + 4) >> 3;
            b += (3 * a + 4) >> 3;
            c -= (3 * d + 4) >> 3;
            d += (3 * c + 4) >> 3;
            c -= (b + 1) >> 1;
            d = ((a + 1) >> 1) - d;
            b += c;
            a -= d;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void InvToddOdd(ref int a, ref int b, ref int c, ref int d)
        {
            d += a;
            c -= b;
            int t1 = d >> 1, t2 = c >> 1;
            a -= t1;
            b += t2;
            a -= (b * 3 + 3) >> 3;
            b += (a * 3 + 3) >> 2;
            a -= (b * 3 + 4) >> 3;
            b -= t2;
            a += t1;
            c += b;
            d -= a;
            b = -b;
            c = -c;
        }

        /// <summary>ICT4x4( ) on 16 coefficients in raster order (Table 160), including InvPermute( ).</summary>
        public static void Ict4x4(Span<int> c)
        {
            Span<int> t = stackalloc int[16];
            var perm = JxrTables.InvPermArr;
            for (int i = 0; i < 16; i++) t[perm[i]] = c[i];
            Ict4x4Permuted(t);
            t.CopyTo(c);
        }

        /// <summary>ICT4x4( ) after the permutation.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Ict4x4Permuted(Span<int> c)
        {
            T2x2h(ref c[0], ref c[1], ref c[4], ref c[5], 1);
            InvTodd(ref c[2], ref c[3], ref c[6], ref c[7]);
            InvTodd(ref c[8], ref c[12], ref c[9], ref c[13]);
            InvToddOdd(ref c[10], ref c[11], ref c[14], ref c[15]);
            T2x2h(ref c[0], ref c[3], ref c[12], ref c[15], 0);
            T2x2h(ref c[5], ref c[6], ref c[9], ref c[10], 0);
            T2x2h(ref c[1], ref c[2], ref c[13], ref c[14], 0);
            T2x2h(ref c[4], ref c[7], ref c[8], ref c[11], 0);
        }

        /// <summary>T2pt( ) (Table 167).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void T2pt(ref int a, ref int b)
        {
            a -= (b + 1) >> 1;
            b += a;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void InvRotate(ref int a, ref int b)
        {
            a -= (b + 1) >> 1;
            b += (a + 1) >> 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void InvScale(ref int a, ref int b)
        {
            a += b;
            b = (a >> 1) - b;
            a += (b * 3) >> 3;
            b += (a * 3) >> 4;
            b += a >> 7;
            b -= a >> 10;
        }

        /// <summary>OverlapPostFilter4( ) (Table 169).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void PostFilter4(ref int a, ref int b, ref int c, ref int d)
        {
            a += d;
            b += c;
            d -= (a + 1) >> 1;
            c -= (b + 1) >> 1;
            InvScale(ref a, ref d);
            InvScale(ref b, ref c);
            a += (d * 3 + 4) >> 3;
            b += (c * 3 + 4) >> 3;
            d -= a >> 1;
            c -= b >> 1;
            a += d;
            b += c;
            d = -d;
            c = -c;
            InvRotate(ref c, ref d);
            d += (a + 1) >> 1;
            c += (b + 1) >> 1;
            a -= d;
            b -= c;
        }

        /// <summary>OverlapPostFilter2x2( ) (Table 170).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void PostFilter2x2(ref int a, ref int b, ref int c, ref int d)
        {
            a += d;
            b += c;
            d -= (a + 1) >> 1;
            c -= (b + 1) >> 1;
            b += (a + 2) >> 2;
            a += (b + 1) >> 1;
            a += b >> 5;
            a += b >> 9;
            a += b >> 13;
            b += (a + 2) >> 2;
            d += (a + 1) >> 1;
            c += (b + 1) >> 1;
            a -= d;
            b -= c;
        }

        /// <summary>OverlapPostFilter2( ) (Table 171).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void PostFilter2(ref int a, ref int b)
        {
            b += (a + 2) >> 2;
            a += (b + 1) >> 1;
            a += b >> 5;
            a += b >> 9;
            a += b >> 13;
            b += (a + 2) >> 2;
        }

        // ------------------------------------------------------------------ forward (Annex D)

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void FwdTodd(ref int a, ref int b, ref int c, ref int d)
        {
            b -= c;
            a += d;
            c += (b + 1) >> 1;
            d = ((a + 1) >> 1) - d;
            b -= (3 * a + 4) >> 3;
            a += (3 * b + 4) >> 3;
            d -= (3 * c + 4) >> 3;
            c += (3 * d + 4) >> 3;
            d += b >> 1;
            c -= (a + 1) >> 1;
            b -= d;
            a += c;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void FwdToddOdd(ref int a, ref int b, ref int c, ref int d)
        {
            b = -b;
            c = -c;
            d += a;
            c -= b;
            int t1 = d >> 1, t2 = c >> 1;
            a -= t1;
            b += t2;
            a += (b * 3 + 4) >> 3;
            b -= (a * 3 + 3) >> 2;
            a += (b * 3 + 3) >> 3;
            b -= t2;
            a += t1;
            c += b;
            d -= a;
        }

        /// <summary>
        /// The exact inverse of <see cref="T2x2h"/> with the same rounding (Annex D uses T2x2h( ) for both directions, which
        /// is an involution only for even sums).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void T2x2hInverse(ref int a, ref int b, ref int c, ref int d, int round)
        {
            // undo: a -= d; b += c (last two steps), then t1 = c + d_orig ... solved step by step
            b -= c;
            a += d;
            // now a = a1, b = b1 (after the first two steps), c = t1 - d0, d = t1 - c0
            int t1 = (a - b + round) >> 1;
            int d0 = t1 - c;
            int c0 = t1 - d;
            c = c0;
            d = d0;
            a -= d;
            b += c;
        }

        /// <summary>FCT4x4( ) (Table D.13) on 16 samples in raster order, including FwdPermute( ).</summary>
        public static void Fct4x4(Span<int> c)
        {
            T2x2hInverse(ref c[0], ref c[3], ref c[12], ref c[15], 0);
            T2x2hInverse(ref c[5], ref c[6], ref c[9], ref c[10], 0);
            T2x2hInverse(ref c[1], ref c[2], ref c[13], ref c[14], 0);
            T2x2hInverse(ref c[4], ref c[7], ref c[8], ref c[11], 0);
            T2x2hInverse(ref c[0], ref c[1], ref c[4], ref c[5], 1);
            FwdTodd(ref c[2], ref c[3], ref c[6], ref c[7]);
            FwdTodd(ref c[8], ref c[12], ref c[9], ref c[13]);
            FwdToddOdd(ref c[10], ref c[11], ref c[14], ref c[15]);
            Span<int> t = stackalloc int[16];
            var perm = JxrTables.InvPermArr;
            for (int i = 0; i < 16; i++) t[i] = c[perm[i]];   // inverse of ICT's t[perm[i]] = c[i]
            t.CopyTo(c);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void FwdScale(ref int a, ref int b)
        {
            b -= (a * 3) >> 4;
            b -= a >> 7;
            b += a >> 10;
            a -= (b * 3) >> 3;
            b = (a >> 1) - b;
            a -= b;
        }

        // inverse of InvRotate: a' = a - ((b+1)>>1); b' = b + ((a'+1)>>1)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void UndoInvRotate(ref int a, ref int b)
        {
            b -= (a + 1) >> 1;
            a += (b + 1) >> 1;
        }

        /// <summary>The exact inverse of <see cref="PostFilter4"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void PreFilter4(ref int a, ref int b, ref int c, ref int d)
        {
            a += d;
            b += c;
            d -= (a + 1) >> 1;
            c -= (b + 1) >> 1;
            UndoInvRotate(ref c, ref d);
            d = -d;
            c = -c;
            a -= d;
            b -= c;
            d += a >> 1;
            c += b >> 1;
            a -= (d * 3 + 4) >> 3;
            b -= (c * 3 + 4) >> 3;
            FwdScale(ref a, ref d);
            FwdScale(ref b, ref c);
            d += (a + 1) >> 1;
            c += (b + 1) >> 1;
            a -= d;
            b -= c;
        }

        /// <summary>The exact inverse of <see cref="PostFilter2x2"/> (Table D.20).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void PreFilter2x2(ref int a, ref int b, ref int c, ref int d)
        {
            a += d;
            b += c;
            d -= (a + 1) >> 1;
            c -= (b + 1) >> 1;
            b -= (a + 2) >> 2;
            a -= b >> 13;
            a -= b >> 9;
            a -= b >> 5;
            a -= (b + 1) >> 1;
            b -= (a + 2) >> 2;
            d += (a + 1) >> 1;
            c += (b + 1) >> 1;
            a -= d;
            b -= c;
        }

        /// <summary>The exact inverse of <see cref="PostFilter2"/> (Table D.21).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void PreFilter2(ref int a, ref int b)
        {
            b -= (a + 2) >> 2;
            a -= b >> 13;
            a -= b >> 9;
            a -= b >> 5;
            a -= (b + 1) >> 1;
            b -= (a + 2) >> 2;
        }

        // ------------------------------------------------------------------ unrolled kernels on a sample array
        // (generated: the lifting steps above with the 16 values in locals)

        /// <summary>ICT4x4( ) (Table 160, with InvPermute( )) of the 4x4 block at o of a sample array with row stride s.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Ict4x4(int[] p, int o, int s)
        {
            int t1, t2;
            ref int r0 = ref p[o];
            ref int r1 = ref p[o + 1 * s];
            ref int r2 = ref p[o + 2 * s];
            ref int r3 = ref p[o + 3 * s];
            int c0 = Unsafe.Add(ref r0, 0); int c1 = Unsafe.Add(ref r0, 1); int c2 = Unsafe.Add(ref r0, 2); int c3 = Unsafe.Add(ref r0, 3); int c4 = Unsafe.Add(ref r1, 0); int c5 = Unsafe.Add(ref r1, 1); int c6 = Unsafe.Add(ref r1, 2); int c7 = Unsafe.Add(ref r1, 3); int c8 = Unsafe.Add(ref r2, 0); int c9 = Unsafe.Add(ref r2, 1); int c10 = Unsafe.Add(ref r2, 2); int c11 = Unsafe.Add(ref r2, 3); int c12 = Unsafe.Add(ref r3, 0); int c13 = Unsafe.Add(ref r3, 1); int c14 = Unsafe.Add(ref r3, 2); int c15 = Unsafe.Add(ref r3, 3);
            c0 += c10; c8 -= c2; t1 = (c0 - c8 + 1) >> 1; t2 = c2; c2 = t1 - c10; c10 = t1 - t2; c0 -= c10; c8 += c2;
            c6 += c12; c4 -= c14; c12 -= c6 >> 1; c14 += (c4 + 1) >> 1; c4 -= (3 * c6 + 4) >> 3; c6 += (3 * c4 + 4) >> 3; c14 -= (3 * c12 + 4) >> 3; c12 += (3 * c14 + 4) >> 3; c14 -= (c6 + 1) >> 1; c12 = ((c4 + 1) >> 1) - c12; c6 += c14; c4 -= c12;
            c9 += c3; c1 -= c11; c3 -= c9 >> 1; c11 += (c1 + 1) >> 1; c1 -= (3 * c9 + 4) >> 3; c9 += (3 * c1 + 4) >> 3; c11 -= (3 * c3 + 4) >> 3; c3 += (3 * c11 + 4) >> 3; c11 -= (c9 + 1) >> 1; c3 = ((c1 + 1) >> 1) - c3; c9 += c11; c1 -= c3;
            c5 += c15; c7 -= c13; t1 = c5 >> 1; t2 = c7 >> 1; c15 -= t1; c13 += t2; c15 -= (c13 * 3 + 3) >> 3; c13 += (c15 * 3 + 3) >> 2; c15 -= (c13 * 3 + 4) >> 3; c13 -= t2; c15 += t1; c7 += c13; c5 -= c15; c13 = -c13; c7 = -c7;
            c0 += c5; c6 -= c9; t1 = (c0 - c6 + 0) >> 1; t2 = c9; c9 = t1 - c5; c5 = t1 - t2; c0 -= c5; c6 += c9;
            c10 += c15; c14 -= c11; t1 = (c10 - c14 + 0) >> 1; t2 = c11; c11 = t1 - c15; c15 = t1 - t2; c10 -= c15; c14 += c11;
            c8 += c7; c4 -= c3; t1 = (c8 - c4 + 0) >> 1; t2 = c3; c3 = t1 - c7; c7 = t1 - t2; c8 -= c7; c4 += c3;
            c2 += c13; c12 -= c1; t1 = (c2 - c12 + 0) >> 1; t2 = c1; c1 = t1 - c13; c13 = t1 - t2; c2 -= c13; c12 += c1;
            Unsafe.Add(ref r0, 0) = c0; Unsafe.Add(ref r0, 1) = c8; Unsafe.Add(ref r0, 2) = c4; Unsafe.Add(ref r0, 3) = c6; Unsafe.Add(ref r1, 0) = c2; Unsafe.Add(ref r1, 1) = c10; Unsafe.Add(ref r1, 2) = c14; Unsafe.Add(ref r1, 3) = c12; Unsafe.Add(ref r2, 0) = c1; Unsafe.Add(ref r2, 1) = c11; Unsafe.Add(ref r2, 2) = c15; Unsafe.Add(ref r2, 3) = c13; Unsafe.Add(ref r3, 0) = c9; Unsafe.Add(ref r3, 1) = c3; Unsafe.Add(ref r3, 2) = c7; Unsafe.Add(ref r3, 3) = c5;
        }

        /// <summary>FCT4x4( ) (Table D.13, with FwdPermute( )): the exact inverse of <see cref="Ict4x4(int[], int, int)"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Fct4x4(int[] p, int o, int s)
        {
            int t1, t2;
            ref int r0 = ref p[o];
            ref int r1 = ref p[o + 1 * s];
            ref int r2 = ref p[o + 2 * s];
            ref int r3 = ref p[o + 3 * s];
            int c0 = Unsafe.Add(ref r0, 0); int c1 = Unsafe.Add(ref r0, 1); int c2 = Unsafe.Add(ref r0, 2); int c3 = Unsafe.Add(ref r0, 3); int c4 = Unsafe.Add(ref r1, 0); int c5 = Unsafe.Add(ref r1, 1); int c6 = Unsafe.Add(ref r1, 2); int c7 = Unsafe.Add(ref r1, 3); int c8 = Unsafe.Add(ref r2, 0); int c9 = Unsafe.Add(ref r2, 1); int c10 = Unsafe.Add(ref r2, 2); int c11 = Unsafe.Add(ref r2, 3); int c12 = Unsafe.Add(ref r3, 0); int c13 = Unsafe.Add(ref r3, 1); int c14 = Unsafe.Add(ref r3, 2); int c15 = Unsafe.Add(ref r3, 3);
            c3 -= c12; c0 += c15; t1 = (c0 - c3 + 0) >> 1; t2 = t1 - c12; c12 = t1 - c15; c15 = t2; c0 -= c15; c3 += c12;
            c6 -= c9; c5 += c10; t1 = (c5 - c6 + 0) >> 1; t2 = t1 - c9; c9 = t1 - c10; c10 = t2; c5 -= c10; c6 += c9;
            c2 -= c13; c1 += c14; t1 = (c1 - c2 + 0) >> 1; t2 = t1 - c13; c13 = t1 - c14; c14 = t2; c1 -= c14; c2 += c13;
            c7 -= c8; c4 += c11; t1 = (c4 - c7 + 0) >> 1; t2 = t1 - c8; c8 = t1 - c11; c11 = t2; c4 -= c11; c7 += c8;
            c1 -= c4; c0 += c5; t1 = (c0 - c1 + 1) >> 1; t2 = t1 - c4; c4 = t1 - c5; c5 = t2; c0 -= c5; c1 += c4;
            c3 -= c6; c2 += c7; c6 += (c3 + 1) >> 1; c7 = ((c2 + 1) >> 1) - c7; c3 -= (3 * c2 + 4) >> 3; c2 += (3 * c3 + 4) >> 3; c7 -= (3 * c6 + 4) >> 3; c6 += (3 * c7 + 4) >> 3; c7 += c3 >> 1; c6 -= (c2 + 1) >> 1; c3 -= c7; c2 += c6;
            c12 -= c9; c8 += c13; c9 += (c12 + 1) >> 1; c13 = ((c8 + 1) >> 1) - c13; c12 -= (3 * c8 + 4) >> 3; c8 += (3 * c12 + 4) >> 3; c13 -= (3 * c9 + 4) >> 3; c9 += (3 * c13 + 4) >> 3; c13 += c12 >> 1; c9 -= (c8 + 1) >> 1; c12 -= c13; c8 += c9;
            c11 = -c11; c14 = -c14; c15 += c10; c14 -= c11; t1 = c15 >> 1; t2 = c14 >> 1; c10 -= t1; c11 += t2; c10 += (c11 * 3 + 4) >> 3; c11 -= (c10 * 3 + 3) >> 2; c10 += (c11 * 3 + 3) >> 3; c11 -= t2; c10 += t1; c14 += c11; c15 -= c10;
            Unsafe.Add(ref r0, 0) = c0; Unsafe.Add(ref r0, 1) = c8; Unsafe.Add(ref r0, 2) = c4; Unsafe.Add(ref r0, 3) = c13; Unsafe.Add(ref r1, 0) = c2; Unsafe.Add(ref r1, 1) = c15; Unsafe.Add(ref r1, 2) = c3; Unsafe.Add(ref r1, 3) = c14; Unsafe.Add(ref r2, 0) = c1; Unsafe.Add(ref r2, 1) = c12; Unsafe.Add(ref r2, 2) = c5; Unsafe.Add(ref r2, 3) = c9; Unsafe.Add(ref r3, 0) = c7; Unsafe.Add(ref r3, 1) = c11; Unsafe.Add(ref r3, 2) = c6; Unsafe.Add(ref r3, 3) = c10;
        }

        /// <summary>OverlapPostFilter4x4( ) (Table 168) of the 4x4 area at o.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void PostFilter4x4(int[] p, int o, int s)
        {
            int t1, t2;
            ref int r0 = ref p[o];
            ref int r1 = ref p[o + 1 * s];
            ref int r2 = ref p[o + 2 * s];
            ref int r3 = ref p[o + 3 * s];
            int c0 = Unsafe.Add(ref r0, 0); int c1 = Unsafe.Add(ref r0, 1); int c2 = Unsafe.Add(ref r0, 2); int c3 = Unsafe.Add(ref r0, 3); int c4 = Unsafe.Add(ref r1, 0); int c5 = Unsafe.Add(ref r1, 1); int c6 = Unsafe.Add(ref r1, 2); int c7 = Unsafe.Add(ref r1, 3); int c8 = Unsafe.Add(ref r2, 0); int c9 = Unsafe.Add(ref r2, 1); int c10 = Unsafe.Add(ref r2, 2); int c11 = Unsafe.Add(ref r2, 3); int c12 = Unsafe.Add(ref r3, 0); int c13 = Unsafe.Add(ref r3, 1); int c14 = Unsafe.Add(ref r3, 2); int c15 = Unsafe.Add(ref r3, 3);
            c0 += c15; c3 -= c12; t1 = (c0 - c3 + 0) >> 1; t2 = c12; c12 = t1 - c15; c15 = t1 - t2; c0 -= c15; c3 += c12;
            c1 += c14; c2 -= c13; t1 = (c1 - c2 + 0) >> 1; t2 = c13; c13 = t1 - c14; c14 = t1 - t2; c1 -= c14; c2 += c13;
            c4 += c11; c7 -= c8; t1 = (c4 - c7 + 0) >> 1; t2 = c8; c8 = t1 - c11; c11 = t1 - t2; c4 -= c11; c7 += c8;
            c5 += c10; c6 -= c9; t1 = (c5 - c6 + 0) >> 1; t2 = c9; c9 = t1 - c10; c10 = t1 - t2; c5 -= c10; c6 += c9;
            c13 -= (c12 + 1) >> 1; c12 += (c13 + 1) >> 1;
            c9 -= (c8 + 1) >> 1; c8 += (c9 + 1) >> 1;
            c7 -= (c3 + 1) >> 1; c3 += (c7 + 1) >> 1;
            c6 -= (c2 + 1) >> 1; c2 += (c6 + 1) >> 1;
            c15 += c10; c14 -= c11; t1 = c15 >> 1; t2 = c14 >> 1; c10 -= t1; c11 += t2; c10 -= (c11 * 3 + 6) >> 3; c11 += (c10 * 3 + 2) >> 2; c10 -= (c11 * 3 + 4) >> 3; c11 -= t2; c10 += t1; c14 += c11; c15 -= c10;
            c0 += c15; c15 = (c0 >> 1) - c15; c0 += (c15 * 3) >> 3; c15 += (c0 * 3) >> 4; c15 += c0 >> 7; c15 -= c0 >> 10;
            c1 += c14; c14 = (c1 >> 1) - c14; c1 += (c14 * 3) >> 3; c14 += (c1 * 3) >> 4; c14 += c1 >> 7; c14 -= c1 >> 10;
            c4 += c11; c11 = (c4 >> 1) - c11; c4 += (c11 * 3) >> 3; c11 += (c4 * 3) >> 4; c11 += c4 >> 7; c11 -= c4 >> 10;
            c5 += c10; c10 = (c5 >> 1) - c10; c5 += (c10 * 3) >> 3; c10 += (c5 * 3) >> 4; c10 += c5 >> 7; c10 -= c5 >> 10;
            c3 -= c12; c0 += (c15 * 3 + 4) >> 3; c15 -= c3 >> 1; t1 = ((c0 - c3) >> 1) - c12; c12 = c15; c15 = t1; c0 -= c15; c3 += c12;
            c2 -= c13; c1 += (c14 * 3 + 4) >> 3; c14 -= c2 >> 1; t1 = ((c1 - c2) >> 1) - c13; c13 = c14; c14 = t1; c1 -= c14; c2 += c13;
            c7 -= c8; c4 += (c11 * 3 + 4) >> 3; c11 -= c7 >> 1; t1 = ((c4 - c7) >> 1) - c8; c8 = c11; c11 = t1; c4 -= c11; c7 += c8;
            c6 -= c9; c5 += (c10 * 3 + 4) >> 3; c10 -= c6 >> 1; t1 = ((c5 - c6) >> 1) - c9; c9 = c10; c10 = t1; c5 -= c10; c6 += c9;
            Unsafe.Add(ref r0, 0) = c0; Unsafe.Add(ref r0, 1) = c1; Unsafe.Add(ref r0, 2) = c2; Unsafe.Add(ref r0, 3) = c3; Unsafe.Add(ref r1, 0) = c4; Unsafe.Add(ref r1, 1) = c5; Unsafe.Add(ref r1, 2) = c6; Unsafe.Add(ref r1, 3) = c7; Unsafe.Add(ref r2, 0) = c8; Unsafe.Add(ref r2, 1) = c9; Unsafe.Add(ref r2, 2) = c10; Unsafe.Add(ref r2, 3) = c11; Unsafe.Add(ref r3, 0) = c12; Unsafe.Add(ref r3, 1) = c13; Unsafe.Add(ref r3, 2) = c14; Unsafe.Add(ref r3, 3) = c15;
        }

        /// <summary>The exact inverse of <see cref="PostFilter4x4"/> (OverlapPreFilter4x4( ), Table D.18).</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void PreFilter4x4(int[] p, int o, int s)
        {
            int t1, t2;
            ref int r0 = ref p[o];
            ref int r1 = ref p[o + 1 * s];
            ref int r2 = ref p[o + 2 * s];
            ref int r3 = ref p[o + 3 * s];
            int c0 = Unsafe.Add(ref r0, 0); int c1 = Unsafe.Add(ref r0, 1); int c2 = Unsafe.Add(ref r0, 2); int c3 = Unsafe.Add(ref r0, 3); int c4 = Unsafe.Add(ref r1, 0); int c5 = Unsafe.Add(ref r1, 1); int c6 = Unsafe.Add(ref r1, 2); int c7 = Unsafe.Add(ref r1, 3); int c8 = Unsafe.Add(ref r2, 0); int c9 = Unsafe.Add(ref r2, 1); int c10 = Unsafe.Add(ref r2, 2); int c11 = Unsafe.Add(ref r2, 3); int c12 = Unsafe.Add(ref r3, 0); int c13 = Unsafe.Add(ref r3, 1); int c14 = Unsafe.Add(ref r3, 2); int c15 = Unsafe.Add(ref r3, 3);
            c6 -= c9; c5 += c10; t1 = c9; c9 = c10; c10 = t1; c9 = ((c5 - c6) >> 1) - c9; c10 += c6 >> 1; c5 -= (c10 * 3 + 4) >> 3; c6 += c9;
            c7 -= c8; c4 += c11; t1 = c8; c8 = c11; c11 = t1; c8 = ((c4 - c7) >> 1) - c8; c11 += c7 >> 1; c4 -= (c11 * 3 + 4) >> 3; c7 += c8;
            c2 -= c13; c1 += c14; t1 = c13; c13 = c14; c14 = t1; c13 = ((c1 - c2) >> 1) - c13; c14 += c2 >> 1; c1 -= (c14 * 3 + 4) >> 3; c2 += c13;
            c3 -= c12; c0 += c15; t1 = c12; c12 = c15; c15 = t1; c12 = ((c0 - c3) >> 1) - c12; c15 += c3 >> 1; c0 -= (c15 * 3 + 4) >> 3; c3 += c12;
            c10 -= (c5 * 3) >> 4; c10 -= c5 >> 7; c10 += c5 >> 10; c5 -= (c10 * 3) >> 3; c10 = (c5 >> 1) - c10; c5 -= c10;
            c11 -= (c4 * 3) >> 4; c11 -= c4 >> 7; c11 += c4 >> 10; c4 -= (c11 * 3) >> 3; c11 = (c4 >> 1) - c11; c4 -= c11;
            c14 -= (c1 * 3) >> 4; c14 -= c1 >> 7; c14 += c1 >> 10; c1 -= (c14 * 3) >> 3; c14 = (c1 >> 1) - c14; c1 -= c14;
            c15 -= (c0 * 3) >> 4; c15 -= c0 >> 7; c15 += c0 >> 10; c0 -= (c15 * 3) >> 3; c15 = (c0 >> 1) - c15; c0 -= c15;
            c15 += c10; c14 -= c11; t1 = c15 >> 1; t2 = c14 >> 1; c10 -= t1; c11 += t2; c10 += (c11 * 3 + 4) >> 3; c11 -= (c10 * 3 + 2) >> 2; c10 += (c11 * 3 + 6) >> 3; c11 -= t2; c10 += t1; c14 += c11; c15 -= c10;
            c2 -= (c6 + 1) >> 1; c6 += (c2 + 1) >> 1;
            c3 -= (c7 + 1) >> 1; c7 += (c3 + 1) >> 1;
            c8 -= (c9 + 1) >> 1; c9 += (c8 + 1) >> 1;
            c12 -= (c13 + 1) >> 1; c13 += (c12 + 1) >> 1;
            c6 -= c9; c5 += c10; t1 = (c5 - c6 + 0) >> 1; t2 = t1 - c9; c9 = t1 - c10; c10 = t2; c5 -= c10; c6 += c9;
            c7 -= c8; c4 += c11; t1 = (c4 - c7 + 0) >> 1; t2 = t1 - c8; c8 = t1 - c11; c11 = t2; c4 -= c11; c7 += c8;
            c2 -= c13; c1 += c14; t1 = (c1 - c2 + 0) >> 1; t2 = t1 - c13; c13 = t1 - c14; c14 = t2; c1 -= c14; c2 += c13;
            c3 -= c12; c0 += c15; t1 = (c0 - c3 + 0) >> 1; t2 = t1 - c12; c12 = t1 - c15; c15 = t2; c0 -= c15; c3 += c12;
            Unsafe.Add(ref r0, 0) = c0; Unsafe.Add(ref r0, 1) = c1; Unsafe.Add(ref r0, 2) = c2; Unsafe.Add(ref r0, 3) = c3; Unsafe.Add(ref r1, 0) = c4; Unsafe.Add(ref r1, 1) = c5; Unsafe.Add(ref r1, 2) = c6; Unsafe.Add(ref r1, 3) = c7; Unsafe.Add(ref r2, 0) = c8; Unsafe.Add(ref r2, 1) = c9; Unsafe.Add(ref r2, 2) = c10; Unsafe.Add(ref r2, 3) = c11; Unsafe.Add(ref r3, 0) = c12; Unsafe.Add(ref r3, 1) = c13; Unsafe.Add(ref r3, 2) = c14; Unsafe.Add(ref r3, 3) = c15;
        }

        // ------------------------------------------------------------------ block-major sample arrays

        /// <summary>
        /// Offset of the sample (x, y) in a block-major array: 4x4 blocks of 16 consecutive values (raster order inside),
        /// <paramref name="bw"/> blocks per row of blocks.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int At(int bw, int x, int y) => ((y >> 2) * bw + (x >> 2)) << 4 | (y & 3) << 2 | (x & 3);

        /// <summary>Row y (x0 .. x0 + n - 1, x0 a multiple of 4) of a block-major array.</summary>
        public static void ReadRow(int[] p, int bw, int y, int x0, int n, Span<int> dst)
        {
            int o = At(bw, x0, y);
            int i = 0;
            if ((x0 & 3) == 0 && dst.Length >= n && (long)o + ((n >> 2) - 1) * 16L + 4 <= p.Length)
            {
                ref int s = ref MemoryMarshal.GetArrayDataReference(p);
                ref int d = ref MemoryMarshal.GetReference(dst);
                for (; i + 4 <= n; i += 4, o += 16)
                    Vector128.StoreUnsafe(Vector128.LoadUnsafe(ref s, (nuint)o), ref d, (nuint)i);
            }
            for (; i + 4 <= n; i += 4, o += 16)
            {
                dst[i] = p[o]; dst[i + 1] = p[o + 1]; dst[i + 2] = p[o + 2]; dst[i + 3] = p[o + 3];
            }
            for (int k = 0; i < n; i++, k++) dst[i] = p[o + k];
        }

        /// <summary>Stores a whole row y (width a multiple of 4) of a block-major array.</summary>
        public static void WriteRow(int[] p, int bw, int y, ReadOnlySpan<int> src)
        {
            int n = bw << 2;
            if (src.Length < n || (long)At(bw, n - 4, y) + 4 > p.Length) throw new ArgumentOutOfRangeException(nameof(src));
            ref int d = ref MemoryMarshal.GetArrayDataReference(p);
            ref int s = ref MemoryMarshal.GetReference(src);
            for (int i = 0, o = At(bw, 0, y); i < n; i += 4, o += 16)
                Vector128.StoreUnsafe(Vector128.LoadUnsafe(ref s, (nuint)i), ref d, (nuint)o);
        }

        /// <summary>A raster array (width a multiple of 4) as a block-major one and back.</summary>
        public static void ToBlocks(int[] raster, int w, int h, int[] blocks)
        {
            int bw = w >> 2;
            for (int y = 0; y < h; y++)
            {
                int r = y * w, o = At(bw, 0, y);
                for (int x = 0; x < w; x += 4, o += 16)
                {
                    blocks[o] = raster[r + x]; blocks[o + 1] = raster[r + x + 1]; blocks[o + 2] = raster[r + x + 2]; blocks[o + 3] = raster[r + x + 3];
                }
            }
        }

        public static int[] ToRaster(int[] blocks, int w, int h)
        {
            var r = new int[w * h];
            for (int y = 0; y < h; y++) ReadRow(blocks, w >> 2, y, 0, w, r.AsSpan(y * w, w));
            return r;
        }

        /// <summary>OverlapPostFilter4x4( ) around a block corner of a block-major array (blocks b00, b10 to its right, b01 below, b11).</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void PostFilter4x4B(int[] p, int b00, int b10, int b01, int b11)
        {
            int t1, t2;
            ref int q00 = ref p[b00 + 15]; ref int q10 = ref p[b10 + 15]; ref int q01 = ref p[b01 + 15]; ref int q11 = ref p[b11 + 15];
            q00 = ref p[b00]; q10 = ref p[b10]; q01 = ref p[b01]; q11 = ref p[b11];
            int c0 = Unsafe.Add(ref q00, 10); int c1 = Unsafe.Add(ref q00, 11); int c2 = Unsafe.Add(ref q10, 8); int c3 = Unsafe.Add(ref q10, 9); int c4 = Unsafe.Add(ref q00, 14); int c5 = Unsafe.Add(ref q00, 15); int c6 = Unsafe.Add(ref q10, 12); int c7 = Unsafe.Add(ref q10, 13); int c8 = Unsafe.Add(ref q01, 2); int c9 = Unsafe.Add(ref q01, 3); int c10 = Unsafe.Add(ref q11, 0); int c11 = Unsafe.Add(ref q11, 1); int c12 = Unsafe.Add(ref q01, 6); int c13 = Unsafe.Add(ref q01, 7); int c14 = Unsafe.Add(ref q11, 4); int c15 = Unsafe.Add(ref q11, 5);
            c0 += c15; c3 -= c12; t1 = (c0 - c3 + 0) >> 1; t2 = c12; c12 = t1 - c15; c15 = t1 - t2; c0 -= c15; c3 += c12;
            c1 += c14; c2 -= c13; t1 = (c1 - c2 + 0) >> 1; t2 = c13; c13 = t1 - c14; c14 = t1 - t2; c1 -= c14; c2 += c13;
            c4 += c11; c7 -= c8; t1 = (c4 - c7 + 0) >> 1; t2 = c8; c8 = t1 - c11; c11 = t1 - t2; c4 -= c11; c7 += c8;
            c5 += c10; c6 -= c9; t1 = (c5 - c6 + 0) >> 1; t2 = c9; c9 = t1 - c10; c10 = t1 - t2; c5 -= c10; c6 += c9;
            c13 -= (c12 + 1) >> 1; c12 += (c13 + 1) >> 1;
            c9 -= (c8 + 1) >> 1; c8 += (c9 + 1) >> 1;
            c7 -= (c3 + 1) >> 1; c3 += (c7 + 1) >> 1;
            c6 -= (c2 + 1) >> 1; c2 += (c6 + 1) >> 1;
            c15 += c10; c14 -= c11; t1 = c15 >> 1; t2 = c14 >> 1; c10 -= t1; c11 += t2; c10 -= (c11 * 3 + 6) >> 3; c11 += (c10 * 3 + 2) >> 2; c10 -= (c11 * 3 + 4) >> 3; c11 -= t2; c10 += t1; c14 += c11; c15 -= c10;
            c0 += c15; c15 = (c0 >> 1) - c15; c0 += (c15 * 3) >> 3; c15 += (c0 * 3) >> 4; c15 += c0 >> 7; c15 -= c0 >> 10;
            c1 += c14; c14 = (c1 >> 1) - c14; c1 += (c14 * 3) >> 3; c14 += (c1 * 3) >> 4; c14 += c1 >> 7; c14 -= c1 >> 10;
            c4 += c11; c11 = (c4 >> 1) - c11; c4 += (c11 * 3) >> 3; c11 += (c4 * 3) >> 4; c11 += c4 >> 7; c11 -= c4 >> 10;
            c5 += c10; c10 = (c5 >> 1) - c10; c5 += (c10 * 3) >> 3; c10 += (c5 * 3) >> 4; c10 += c5 >> 7; c10 -= c5 >> 10;
            c3 -= c12; c0 += (c15 * 3 + 4) >> 3; c15 -= c3 >> 1; t1 = ((c0 - c3) >> 1) - c12; c12 = c15; c15 = t1; c0 -= c15; c3 += c12;
            c2 -= c13; c1 += (c14 * 3 + 4) >> 3; c14 -= c2 >> 1; t1 = ((c1 - c2) >> 1) - c13; c13 = c14; c14 = t1; c1 -= c14; c2 += c13;
            c7 -= c8; c4 += (c11 * 3 + 4) >> 3; c11 -= c7 >> 1; t1 = ((c4 - c7) >> 1) - c8; c8 = c11; c11 = t1; c4 -= c11; c7 += c8;
            c6 -= c9; c5 += (c10 * 3 + 4) >> 3; c10 -= c6 >> 1; t1 = ((c5 - c6) >> 1) - c9; c9 = c10; c10 = t1; c5 -= c10; c6 += c9;
            Unsafe.Add(ref q00, 10) = c0; Unsafe.Add(ref q00, 11) = c1; Unsafe.Add(ref q10, 8) = c2; Unsafe.Add(ref q10, 9) = c3; Unsafe.Add(ref q00, 14) = c4; Unsafe.Add(ref q00, 15) = c5; Unsafe.Add(ref q10, 12) = c6; Unsafe.Add(ref q10, 13) = c7; Unsafe.Add(ref q01, 2) = c8; Unsafe.Add(ref q01, 3) = c9; Unsafe.Add(ref q11, 0) = c10; Unsafe.Add(ref q11, 1) = c11; Unsafe.Add(ref q01, 6) = c12; Unsafe.Add(ref q01, 7) = c13; Unsafe.Add(ref q11, 4) = c14; Unsafe.Add(ref q11, 5) = c15;
        }

        /// <summary>The exact inverse of <see cref="PostFilter4x4B"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void PreFilter4x4B(int[] p, int b00, int b10, int b01, int b11)
        {
            int t1, t2;
            ref int q00 = ref p[b00 + 15]; ref int q10 = ref p[b10 + 15]; ref int q01 = ref p[b01 + 15]; ref int q11 = ref p[b11 + 15];
            q00 = ref p[b00]; q10 = ref p[b10]; q01 = ref p[b01]; q11 = ref p[b11];
            int c0 = Unsafe.Add(ref q00, 10); int c1 = Unsafe.Add(ref q00, 11); int c2 = Unsafe.Add(ref q10, 8); int c3 = Unsafe.Add(ref q10, 9); int c4 = Unsafe.Add(ref q00, 14); int c5 = Unsafe.Add(ref q00, 15); int c6 = Unsafe.Add(ref q10, 12); int c7 = Unsafe.Add(ref q10, 13); int c8 = Unsafe.Add(ref q01, 2); int c9 = Unsafe.Add(ref q01, 3); int c10 = Unsafe.Add(ref q11, 0); int c11 = Unsafe.Add(ref q11, 1); int c12 = Unsafe.Add(ref q01, 6); int c13 = Unsafe.Add(ref q01, 7); int c14 = Unsafe.Add(ref q11, 4); int c15 = Unsafe.Add(ref q11, 5);
            c6 -= c9; c5 += c10; t1 = c9; c9 = c10; c10 = t1; c9 = ((c5 - c6) >> 1) - c9; c10 += c6 >> 1; c5 -= (c10 * 3 + 4) >> 3; c6 += c9;
            c7 -= c8; c4 += c11; t1 = c8; c8 = c11; c11 = t1; c8 = ((c4 - c7) >> 1) - c8; c11 += c7 >> 1; c4 -= (c11 * 3 + 4) >> 3; c7 += c8;
            c2 -= c13; c1 += c14; t1 = c13; c13 = c14; c14 = t1; c13 = ((c1 - c2) >> 1) - c13; c14 += c2 >> 1; c1 -= (c14 * 3 + 4) >> 3; c2 += c13;
            c3 -= c12; c0 += c15; t1 = c12; c12 = c15; c15 = t1; c12 = ((c0 - c3) >> 1) - c12; c15 += c3 >> 1; c0 -= (c15 * 3 + 4) >> 3; c3 += c12;
            c10 -= (c5 * 3) >> 4; c10 -= c5 >> 7; c10 += c5 >> 10; c5 -= (c10 * 3) >> 3; c10 = (c5 >> 1) - c10; c5 -= c10;
            c11 -= (c4 * 3) >> 4; c11 -= c4 >> 7; c11 += c4 >> 10; c4 -= (c11 * 3) >> 3; c11 = (c4 >> 1) - c11; c4 -= c11;
            c14 -= (c1 * 3) >> 4; c14 -= c1 >> 7; c14 += c1 >> 10; c1 -= (c14 * 3) >> 3; c14 = (c1 >> 1) - c14; c1 -= c14;
            c15 -= (c0 * 3) >> 4; c15 -= c0 >> 7; c15 += c0 >> 10; c0 -= (c15 * 3) >> 3; c15 = (c0 >> 1) - c15; c0 -= c15;
            c15 += c10; c14 -= c11; t1 = c15 >> 1; t2 = c14 >> 1; c10 -= t1; c11 += t2; c10 += (c11 * 3 + 4) >> 3; c11 -= (c10 * 3 + 2) >> 2; c10 += (c11 * 3 + 6) >> 3; c11 -= t2; c10 += t1; c14 += c11; c15 -= c10;
            c2 -= (c6 + 1) >> 1; c6 += (c2 + 1) >> 1;
            c3 -= (c7 + 1) >> 1; c7 += (c3 + 1) >> 1;
            c8 -= (c9 + 1) >> 1; c9 += (c8 + 1) >> 1;
            c12 -= (c13 + 1) >> 1; c13 += (c12 + 1) >> 1;
            c6 -= c9; c5 += c10; t1 = (c5 - c6 + 0) >> 1; t2 = t1 - c9; c9 = t1 - c10; c10 = t2; c5 -= c10; c6 += c9;
            c7 -= c8; c4 += c11; t1 = (c4 - c7 + 0) >> 1; t2 = t1 - c8; c8 = t1 - c11; c11 = t2; c4 -= c11; c7 += c8;
            c2 -= c13; c1 += c14; t1 = (c1 - c2 + 0) >> 1; t2 = t1 - c13; c13 = t1 - c14; c14 = t2; c1 -= c14; c2 += c13;
            c3 -= c12; c0 += c15; t1 = (c0 - c3 + 0) >> 1; t2 = t1 - c12; c12 = t1 - c15; c15 = t2; c0 -= c15; c3 += c12;
            Unsafe.Add(ref q00, 10) = c0; Unsafe.Add(ref q00, 11) = c1; Unsafe.Add(ref q10, 8) = c2; Unsafe.Add(ref q10, 9) = c3; Unsafe.Add(ref q00, 14) = c4; Unsafe.Add(ref q00, 15) = c5; Unsafe.Add(ref q10, 12) = c6; Unsafe.Add(ref q10, 13) = c7; Unsafe.Add(ref q01, 2) = c8; Unsafe.Add(ref q01, 3) = c9; Unsafe.Add(ref q11, 0) = c10; Unsafe.Add(ref q11, 1) = c11; Unsafe.Add(ref q01, 6) = c12; Unsafe.Add(ref q01, 7) = c13; Unsafe.Add(ref q11, 4) = c14; Unsafe.Add(ref q11, 5) = c15;
        }

        /// <summary>
        /// The 4x4 / 4-point overlap filters (9.9.6, D.5) of one region of a block-major array, in blocks
        /// [bx0, bx1) x [by0, by1): every interior block corner, the two outer rows and columns, the four corners.
        /// </summary>
        public static void Overlap4B(int[] p, int bw, int bx0, int by0, int bx1, int by1, bool forward)
        {
            Overlap4BInterior(p, bw, bx0, by0, bx1, by1, by0, by1, forward);
            Overlap4BEdges(p, bw, bx0, by0, bx1, by1, forward);
        }

        /// <summary>The interior filters at the corners below block rows j with jFrom &lt;= j &lt; jTo.</summary>
        public static void Overlap4BInterior(int[] p, int bw, int bx0, int by0, int bx1, int by1, int jFrom, int jTo, bool forward)
        {
            int j0 = Math.Max(jFrom, by0), j1 = Math.Min(jTo, by1 - 1);
            int row = bw << 4;
            for (int j = j0; j < j1; j++)
            {
                int b = (j * bw + bx0) << 4;
                for (int i = bx0; i < bx1 - 1; i++, b += 16)
                    if (forward) PreFilter4x4B(p, b, b + 16, b + row, b + row + 16);
                    else PostFilter4x4B(p, b, b + 16, b + row, b + row + 16);
            }
        }

        public static void Overlap4BEdges(int[] p, int bw, int bx0, int by0, int bx1, int by1, bool forward)
        {
            int row = bw << 4;
            // left and right two columns: vertical 4-point filters across block rows
            for (int j = by0; j < by1 - 1; j++)
            {
                int l = (j * bw + bx0) << 4, r = (j * bw + bx1 - 1) << 4;
                for (int u = 0; u < 2; u++)
                {
                    F4(p, l + 8 + u, l + 12 + u, l + row + u, l + row + 4 + u, forward);
                    F4(p, r + 10 + u, r + 14 + u, r + row + 2 + u, r + row + 6 + u, forward);
                }
            }
            // top and bottom two rows: horizontal filters across block columns
            for (int i = bx0; i < bx1 - 1; i++)
            {
                int t = (by0 * bw + i) << 4, b = ((by1 - 1) * bw + i) << 4;
                for (int v = 0; v < 2; v++)
                {
                    F4(p, t + v * 4 + 2, t + v * 4 + 3, t + 16 + v * 4, t + 16 + v * 4 + 1, forward);
                    F4(p, b + 8 + v * 4 + 2, b + 8 + v * 4 + 3, b + 16 + 8 + v * 4, b + 16 + 8 + v * 4 + 1, forward);
                }
            }
            // corners: top-left, top-right, bottom-left, bottom-right
            int tl = (by0 * bw + bx0) << 4, tr = (by0 * bw + bx1 - 1) << 4, bl = ((by1 - 1) * bw + bx0) << 4, br = ((by1 - 1) * bw + bx1 - 1) << 4;
            F4(p, tl, tl + 1, tl + 4, tl + 5, forward);
            F4(p, tr + 2, tr + 3, tr + 6, tr + 7, forward);
            F4(p, bl + 8, bl + 9, bl + 12, bl + 13, forward);
            F4(p, br + 10, br + 11, br + 14, br + 15, forward);
        }

        // ------------------------------------------------------------------ geometric application

        /// <summary>
        /// The 4x4 / 4-point overlap filters of one region (the image, or a tile with hard tiling) of a sample array with block
        /// size 4 (9.9.6, and 9.9.3.2 for the DC-LP array): every interior block corner, the two outer rows and columns, the
        /// four corners. <paramref name="forward"/> runs the pre-filters of Annex D instead. The interior part (<see cref="Overlap4Interior"/>) can be
        /// restricted to a band of rows for parallel work; the edges are done by <see cref="Overlap4Edges"/>.
        /// </summary>
        public static void Overlap4(int[] p, int stride, int x0, int y0, int x1, int y1, bool forward)
        {
            Overlap4Interior(p, stride, x0, y0, x1, y1, y0 + 2, y1 - 2, forward);
            Overlap4Edges(p, stride, x0, y0, x1, y1, forward);
        }

        /// <summary>Interior 4x4 filters whose top row y satisfies yFrom &lt;= y &lt; yTo (with y = y0 + 2 + 4k).</summary>
        public static void Overlap4Interior(int[] p, int s, int x0, int y0, int x1, int y1, int yFrom, int yTo, bool forward)
        {
            int yStart = y0 + 2;
            if (yFrom > yStart) yStart += (yFrom - yStart + 3) / 4 * 4;
            int yEnd = Math.Min(yTo, y1 - 2);
            for (int y = yStart; y < yEnd; y += 4)
            {
                int o = y * s;
                if (o + 3 * s + x1 > p.Length) throw new IndexOutOfRangeException();
                for (int x = x0 + 2; x < x1 - 2; x += 4)
                    if (forward) PreFilter4x4(p, o + x, s); else PostFilter4x4(p, o + x, s);
            }
        }

        public static void Overlap4Edges(int[] p, int s, int x0, int y0, int x1, int y1, bool forward)
        {
            // left and right two columns: vertical 4-point filters across block boundaries
            Span<int> edge = stackalloc int[4];
            edge[0] = x0; edge[1] = x0 + 1; edge[2] = x1 - 2; edge[3] = x1 - 1;
            for (int y = y0 + 2; y < y1 - 2; y += 4)
                foreach (int x in edge)
                {
                    int o = y * s + x;
                    F4(p, o, o + s, o + 2 * s, o + 3 * s, forward);
                }
            // top and bottom two rows: horizontal
            edge[0] = y0; edge[1] = y0 + 1; edge[2] = y1 - 2; edge[3] = y1 - 1;
            for (int x = x0 + 2; x < x1 - 2; x += 4)
                foreach (int y in edge)
                {
                    int o = y * s + x;
                    F4(p, o, o + 1, o + 2, o + 3, forward);
                }
            // corners (top-left, top-right, bottom-left, bottom-right), 2x2 in raster order
            foreach (var (cx, cy) in new[] { (x0, y0), (x1 - 2, y0), (x0, y1 - 2), (x1 - 2, y1 - 2) })
            {
                int o = cy * s + cx;
                F4(p, o, o + 1, o + s, o + s + 1, forward);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void F4(int[] p, int i0, int i1, int i2, int i3, bool forward)
        {
            if (forward) PreFilter4(ref p[i0], ref p[i1], ref p[i2], ref p[i3]);
            else PostFilter4(ref p[i0], ref p[i1], ref p[i2], ref p[i3]);
        }

        /// <summary>
        /// The 2x2 / 2-point overlap filters of a chroma DC-LP array with block size 2 (YUV420, YUV422; 9.9.3.3, 9.9.3.4), with
        /// the corner difference ("OverlapPostFilter1") before and the addition after.
        /// </summary>
        public static void Overlap2(int[] p, int s, int x0, int y0, int x1, int y1, bool forward)
        {
            int tl = y0 * s + x0, tr = y0 * s + x1 - 1, bl = (y1 - 1) * s + x0, br = (y1 - 1) * s + x1 - 1;
            if (!forward)
            {
                p[tl] -= p[tl + 1];
                p[tr] -= p[tr - 1];
                p[bl] -= p[bl + 1];
                p[br] -= p[br - 1];
            }
            else
            {
                // the encoder undoes the decoder's final additions first
                p[br] -= p[br - 1];
                p[bl] -= p[bl + 1];
                p[tr] -= p[tr - 1];
                p[tl] -= p[tl + 1];
            }
            for (int y = y0 + 1; y < y1 - 1; y += 2)
                for (int x = x0 + 1; x < x1 - 1; x += 2)
                {
                    int o = y * s + x;
                    if (forward) PreFilter2x2(ref p[o], ref p[o + 1], ref p[o + s], ref p[o + s + 1]);
                    else PostFilter2x2(ref p[o], ref p[o + 1], ref p[o + s], ref p[o + s + 1]);
                }
            Span<int> edge = stackalloc int[2];
            edge[0] = x0; edge[1] = x1 - 1;
            for (int y = y0 + 1; y < y1 - 1; y += 2)
                foreach (int x in edge)
                {
                    int o = y * s + x;
                    if (forward) PreFilter2(ref p[o], ref p[o + s]); else PostFilter2(ref p[o], ref p[o + s]);
                }
            edge[0] = y0; edge[1] = y1 - 1;
            for (int x = x0 + 1; x < x1 - 1; x += 2)
                foreach (int y in edge)
                {
                    int o = y * s + x;
                    if (forward) PreFilter2(ref p[o], ref p[o + 1]); else PostFilter2(ref p[o], ref p[o + 1]);
                }
            if (!forward)
            {
                p[tl] += p[tl + 1];
                p[tr] += p[tr - 1];
                p[bl] += p[bl + 1];
                p[br] += p[br - 1];
            }
            else
            {
                p[br] += p[br - 1];
                p[bl] += p[bl + 1];
                p[tr] += p[tr - 1];
                p[tl] += p[tl + 1];
            }
        }
    }
}
