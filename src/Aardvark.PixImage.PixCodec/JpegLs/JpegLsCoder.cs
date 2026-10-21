using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Aardvark.ImageCodecs
{
    /// <summary>JPEG-LS coding parameters of a scan (ITU-T T.87 / ISO 14495-1, C.2.4.1.1).</summary>
    internal readonly record struct JlsParams(int MaxVal, int T1, int T2, int T3, int Reset, int Near)
    {
        public const int DefaultReset = 64;

        /// <summary>
        /// The parameters in effect: zero entries of a preset (LSE id 1) take their defaults, which depend on MAXVAL and
        /// NEAR. Invalid combinations throw <see cref="ImageFormatException"/>.
        /// </summary>
        public static JlsParams Resolve(int bits, int near, int maxVal, int t1, int t2, int t3, int reset)
        {
            if (maxVal == 0) maxVal = (1 << bits) - 1;
            if (maxVal < 1 || maxVal > (1 << bits) - 1) throw new ImageFormatException($"JPEG-LS: bad MAXVAL {maxVal}");
            if (near < 0 || near > Math.Min(255, maxVal / 2)) throw new ImageFormatException($"JPEG-LS: bad NEAR {near}");
            var (d1, d2, d3) = DefaultThresholds(maxVal, near);
            if (t1 == 0) t1 = d1;
            if (t2 == 0) t2 = d2;
            if (t3 == 0) t3 = d3;
            if (reset == 0) reset = DefaultReset;
            if (t1 < near + 1 || t1 > maxVal || t2 < t1 || t2 > maxVal || t3 < t2 || t3 > maxVal)
                throw new ImageFormatException($"JPEG-LS: bad thresholds {t1} {t2} {t3} for MAXVAL {maxVal} NEAR {near}");
            if (reset < 3 || reset > Math.Max(255, maxVal)) throw new ImageFormatException($"JPEG-LS: bad RESET {reset}");
            return new JlsParams(maxVal, t1, t2, t3, reset, near);
        }

        public static (int t1, int t2, int t3) DefaultThresholds(int maxVal, int near)
        {
            static int Clamp(int i, int j, int max) => i > max || i < j ? j : i;
            if (maxVal >= 128)
            {
                int f = (Math.Min(maxVal, 4095) + 128) >> 8;
                int t1 = Clamp(f * (3 - 2) + 2 + 3 * near, near + 1, maxVal);
                int t2 = Clamp(f * (7 - 3) + 3 + 5 * near, t1, maxVal);
                int t3 = Clamp(f * (21 - 4) + 4 + 7 * near, t2, maxVal);
                return (t1, t2, t3);
            }
            else
            {
                int f = 256 / (maxVal + 1);
                int t1 = Clamp(Math.Max(2, 3 / f + 3 * near), near + 1, maxVal);
                int t2 = Clamp(Math.Max(3, 7 / f + 5 * near), t1, maxVal);
                int t3 = Clamp(Math.Max(4, 21 / f + 7 * near), t2, maxVal);
                return (t1, t2, t3);
            }
        }

        /// <summary>Whether these parameters equal the defaults for MAXVAL = 2^bits - 1 (no LSE segment needed).</summary>
        public bool IsDefault(int bits)
        {
            var (d1, d2, d3) = DefaultThresholds(MaxVal, Near);
            return MaxVal == (1 << bits) - 1 && T1 == d1 && T2 == d2 && T3 == d3 && Reset == DefaultReset;
        }
    }

    /// <summary>
    /// The JPEG-LS context modelling and Golomb coding of one scan (T.87 Annex A): regular mode with 365 contexts, run mode
    /// with its two run interruption contexts. Lines are int buffers with one guard sample on each side
    /// (index -1 and width).
    /// </summary>
    [SkipLocalsInit]
    internal sealed unsafe class JlsCoder
    {
        public static ReadOnlySpan<byte> J => new byte[] { 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

        public readonly int MaxVal, Near, Range, Qbpp, Limit, Reset, Step;
        readonly int _initA;
        /// <summary>Quantized gradient per difference d in [-MaxVal, MaxVal], at index d + MaxVal.</summary>
        readonly sbyte[] _quant;
        /// <summary>Regular contexts, 4 ints each: A, B, C, N.</summary>
        readonly int[] _ctx = GC.AllocateArray<int>(365 * 4, pinned: true);
        /// <summary>Run interruption contexts (RItype 0 / 1): A, N, Nn.</summary>
        readonly int[] _ri = GC.AllocateArray<int>(2 * 3, pinned: true);
        /// <summary>Pointers into the pinned tables: _qt at difference 0, _cx = _ctx.</summary>
        readonly sbyte* _qt;
        readonly int* _cx;
        /// <summary>Zeros before the escape of LG(k, LIMIT): LIMIT - qbpp - 1.</summary>
        readonly int _zl;

        public JlsCoder(JlsParams p)
        {
            MaxVal = p.MaxVal; Near = p.Near; Reset = p.Reset;
            Step = 2 * Near + 1;
            Range = (MaxVal + 2 * Near) / Step + 1;
            Qbpp = CeilLog2(Range);
            int bpp = Math.Max(2, CeilLog2(MaxVal + 1));
            Limit = 2 * (bpp + Math.Max(8, bpp));
            _initA = Math.Max(2, (Range + 32) >> 6);
            _quant = GC.AllocateArray<sbyte>(2 * MaxVal + 1, pinned: true);
            _qt = (sbyte*)Unsafe.AsPointer(ref _quant[0]) + MaxVal;
            _cx = (int*)Unsafe.AsPointer(ref _ctx[0]);
            _zl = Limit - Qbpp - 1;
            for (int d = -MaxVal; d <= MaxVal; d++)
            {
                int q = d <= -p.T3 ? -4 : d <= -p.T2 ? -3 : d <= -p.T1 ? -2 : d < -Near ? -1 : d <= Near ? 0 : d < p.T1 ? 1 : d < p.T2 ? 2 : d < p.T3 ? 3 : 4;
                _quant[d + MaxVal] = (sbyte)q;
            }
            ResetContexts();
        }

        static int CeilLog2(int v) => v <= 1 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)(v - 1));

        public void ResetContexts()
        {
            for (int q = 0; q < 365; q++)
            {
                _ctx[4 * q] = _initA; _ctx[4 * q + 1] = 0; _ctx[4 * q + 2] = 0; _ctx[4 * q + 3] = 1;
            }
            for (int t = 0; t < 2; t++) { _ri[3 * t] = _initA; _ri[3 * t + 1] = 1; _ri[3 * t + 2] = 0; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Med(int a, int b, int c)
        {
            // median of a, b, a + b - c = a + b - c clamped to [min(a, b), max(a, b)]; branch free (the branches would
            // be mispredicted a lot)
            int d = a - b, s = d >> 31;
            int mn = b + (d & s), mx = a - (d & s);
            int v = a + b - c;
            int t = v - mx; v = mx + (t & (t >> 31));     // min(v, mx)
            t = v - mn; return mn + (t & ~(t >> 31));     // max(v, mn)
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int GolombK(int n, int a)
        {
            // smallest k with (n << k) >= a: floor(log2 a) - floor(log2 n), or one more
            int k = BitOperations.Log2((uint)a) - BitOperations.Log2((uint)n);
            k &= ~(k >> 31);
            return k + (int)((uint)((n << k) - a) >> 31);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void UpdateRunInterruption(int* r, int err, int em, int riType, int reset)
        {
            if (err < 0) r[2]++;
            r[0] += (em + 1 - riType) >> 1;
            if (r[1] == reset) { r[0] >>= 1; r[1] >>= 1; r[2] >>= 1; }
            r[1]++;
        }

        // ================================================================== decoding

        /// <summary>Compile time switch between the lossless and the near-lossless code paths.</summary>
        interface IMode { bool Lossless { get; } }
        struct LosslessMode : IMode { public bool Lossless => true; }
        struct NearMode : IMode { public bool Lossless => false; }

        /// <summary>Decodes one line of one component. prev[-1], prev[w] and cur[-1] must be set by the caller.</summary>
        public void DecodeLine(ref JlsBitReader reader, int* prev, int* cur, int w, ref int runIndex)
        {
            if (Near == 0) DecodeLine<LosslessMode>(ref reader, prev, cur, w, ref runIndex);
            else DecodeLine<NearMode>(ref reader, prev, cur, w, ref runIndex);
        }

        /// <summary>Regular mode sample (A.4 - A.6) on reader state in the caller's locals; returns Rx.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int DecodeRegular<TM>(ref ulong cache, ref int count, ref byte* pos, ref byte* end, int ra, int rb, int rc, int qs) where TM : struct, IMode
        {
            int sign = qs >> 31;                 // 0 or -1
            int* c = _cx + 4 * ((qs ^ sign) - sign);
            int px = Med(ra, rb, rc) + ((c[2] ^ sign) - sign);
            int maxVal = MaxVal;
            if (px < 0) px = 0; else if (px > maxVal) px = maxVal;
            int n = c[3];
            int k = GolombK(n, c[0]);
            // LG(k, LIMIT)
            if (count < 49) JlsBitReader.Refill(ref cache, ref count, ref pos, ref end);
            int zl = _zl;
            int lz = BitOperations.LeadingZeroCount(cache);
            int m;
            if (lz < zl && lz < count)
            {
                cache <<= lz + 1;
                count -= lz + 1;
                m = lz;
                if (k != 0)
                {
                    if (count < k) JlsBitReader.Refill(ref cache, ref count, ref pos, ref end);
                    m = (lz << k) | (int)(cache >> (64 - k));
                    cache <<= k;
                    count -= k;
                }
            }
            else
            {
                // escape codes, and zero runs longer than the bits at hand
                var g = JlsBitReader.ReadGolombSlow(new JlsBitReader { Cache = cache, Count = count, Pos = pos, End = end }, k, zl, Qbpp);
                cache = g.Reader.Cache; count = g.Reader.Count; pos = g.Reader.Pos; end = g.Reader.End;
                m = g.Value;
            }
            int err = (m >> 1) ^ -(m & 1);
            int b = c[1];
            int rx;
            if (default(TM).Lossless)
            {
                if (k == 0 && 2 * b <= -n) err = ~err;
                b += err;
                rx = px + ((err ^ sign) - sign);
                if (rx < 0) rx += Range; else if (rx > maxVal) rx -= Range;
                if ((uint)rx > (uint)maxVal) rx = rx < 0 ? 0 : maxVal;   // only corrupt data gets here; keeps the tables in bounds
            }
            else
            {
                int step = Step;
                b += err * step;
                rx = px + (((err * step) ^ sign) - sign);
                int near = Near;
                if (rx < -near) rx += Range * step; else if (rx > maxVal + near) rx -= Range * step;
                if (rx < 0) rx = 0; else if (rx > maxVal) rx = maxVal;
            }
            int ea = err >> 31;
            int a = c[0] + ((err ^ ea) - ea);
            if (n == Reset) { a >>= 1; b >>= 1; n >>= 1; }
            n++;
            c[0] = a; c[3] = n;
            if (b <= -n)
            {
                b += n;
                if (c[2] > -128) c[2]--;
                if (b <= -n) b = -n + 1;
            }
            else if (b > 0)
            {
                b -= n;
                if (c[2] < 127) c[2]++;
                if (b > 0) b = 0;
            }
            c[1] = b;
            return rx;
        }

        void DecodeLine<TM>(ref JlsBitReader reader, int* prev, int* cur, int w, ref int runIndex) where TM : struct, IMode
        {
            // the reader state lives in locals here (registers), the context tables are reached through fields
            ulong cache = reader.Cache;
            int count = reader.Count;
            byte* pos = reader.Pos;
            ref byte* end = ref reader.End;
            int ri = runIndex;
            int* curEnd = cur + w;
            int ra = cur[-1];
            while (cur < curEnd)
            {
                int rb = prev[0], rc = prev[-1];
                sbyte* qt = _qt;
                int qs = 81 * qt[prev[1] - rb] + 9 * qt[rb - rc] + qt[rc - ra];
                if (qs != 0)
                {
                    int rx = DecodeRegular<TM>(ref cache, ref count, ref pos, ref end, ra, rb, rc, qs);
                    *cur++ = rx;
                    prev++;
                    ra = rx;
                    continue;
                }
                var rr = new JlsBitReader { Cache = cache, Count = count, Pos = pos, End = end };
                var res = DecodeRun(rr, prev, cur, (int)(curEnd - cur), ra, ri);
                cache = res.Reader.Cache; count = res.Reader.Count; pos = res.Reader.Pos; end = res.Reader.End;
                ri = res.RunIndex;
                cur += res.Samples; prev += res.Samples;
                ra = cur[-1];
            }
            runIndex = ri;
            reader.Cache = cache; reader.Count = count; reader.Pos = pos;
        }

        internal struct RunResult { public JlsBitReader Reader; public int Samples, RunIndex; }

        /// <summary>
        /// Run mode (A.7) at cur: the run, then the interruption sample unless the run reaches the end of the line (left
        /// samples away). Returns the number of samples decoded.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        RunResult DecodeRun(JlsBitReader r, int* prev, int* cur, int left, int ra, int ri)
        {
            ReadOnlySpan<byte> jt = J;
            int x = 0;
            while (true)
            {
                r.Fill();
                if (r.ReadBit() != 0)
                {
                    int rm = 1 << jt[ri];
                    int n = Math.Min(rm, left - x);
                    new Span<int>(cur + x, n).Fill(ra);
                    x += n;
                    if (n == rm && ri < 31) ri++;
                    if (x == left) break;
                }
                else
                {
                    int bits = jt[ri];
                    int cnt = bits == 0 ? 0 : r.ReadBits(bits);
                    if (cnt > left - x - 1) cnt = left - x - 1;   // corrupt data
                    new Span<int>(cur + x, cnt).Fill(ra);
                    x += cnt;
                    int glimit = Limit - bits - 1;
                    if (ri > 0) ri--;
                    int rb = prev[x];
                    var res = DecodeRunInterruption(r, ra, rb, glimit, Math.Abs(ra - rb) <= Near ? 1 : 0);
                    r = res.Reader;
                    cur[x++] = res.Rx;
                    break;
                }
            }
            return new RunResult { Reader = r, Samples = x, RunIndex = ri };
        }

        internal struct RiResult { public JlsBitReader Reader; public int Rx; }

        /// <remarks>Takes and returns the reader by value so that the caller's copy is not address exposed.</remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        RiResult DecodeRunInterruption(JlsBitReader r, int ra, int rb, int glimit, int riType)
        {
            int near = Near, step = Step, maxVal = MaxVal;
            int px = riType != 0 ? ra : rb;
            int sign = riType == 0 && ra > rb ? -1 : 0;
            int rx;
            fixed (int* ric = _ri)
            {
                int* rc0 = ric + 3 * riType;
                int temp = riType != 0 ? rc0[0] + (rc0[1] >> 1) : rc0[0];
                int k = GolombK(rc0[1], temp);
                int em = r.ReadGolomb(k, glimit, Qbpp);
                int t = em + riType;
                int map = t & 1;
                int abs = (t + map) >> 1;
                bool neg = k == 0 && 2 * rc0[2] < rc0[1] ? map == 0 : map != 0;
                int err = neg ? -abs : abs;
                UpdateRunInterruption(rc0, err, em, riType, Reset);
                rx = px + (((err * step) ^ sign) - sign);
                int rangeStep = Range * step;
                if (rx < -near) rx += rangeStep; else if (rx > maxVal + near) rx -= rangeStep;
                if (rx < 0) rx = 0; else if (rx > maxVal) rx = maxVal;
            }
            return new RiResult { Reader = r, Rx = rx };
        }

        /// <summary>
        /// Decodes one line of all components of a sample interleaved scan. prev[i] / cur[i] are the line buffers of
        /// component i, prepared as for <see cref="DecodeLine"/>.
        /// </summary>
        public void DecodeLineSample(ref JlsBitReader reader, int** prev, int** cur, int nc, int w, ref int runIndex)
        {
            if (Near == 0) DecodeLineSample<LosslessMode>(ref reader, prev, cur, nc, w, ref runIndex);
            else DecodeLineSample<NearMode>(ref reader, prev, cur, nc, w, ref runIndex);
        }

        void DecodeLineSample<TM>(ref JlsBitReader reader, int** prev, int** cur, int nc, int w, ref int runIndex) where TM : struct, IMode
        {
            ulong cache = reader.Cache;
            int count = reader.Count;
            byte* pos = reader.Pos;
            ref byte* end = ref reader.End;
            int ri = runIndex;
            int* qsv = stackalloc int[nc];
            int x = 0;
            while (x < w)
            {
                sbyte* qt = _qt;
                int any = 0;
                for (int i = 0; i < nc; i++)
                {
                    int* pv = prev[i] + x;
                    int rb = pv[0], rc = pv[-1];
                    int qs = 81 * qt[pv[1] - rb] + 9 * qt[rb - rc] + qt[rc - cur[i][x - 1]];
                    qsv[i] = qs;
                    any |= qs;
                }
                if (any != 0)
                {
                    for (int i = 0; i < nc; i++)
                    {
                        int* pv = prev[i] + x; int* cu = cur[i] + x;
                        cu[0] = DecodeRegular<TM>(ref cache, ref count, ref pos, ref end, cu[-1], pv[0], pv[-1], qsv[i]);
                    }
                    x++;
                    continue;
                }
                var res = DecodeRunSample(new JlsBitReader { Cache = cache, Count = count, Pos = pos, End = end }, prev, cur, nc, x, w, ri);
                cache = res.Reader.Cache; count = res.Reader.Count; pos = res.Reader.Pos; end = res.Reader.End;
                ri = res.RunIndex;
                x = res.Samples;
            }
            runIndex = ri;
            reader.Cache = cache; reader.Count = count; reader.Pos = pos;
        }

        /// <summary>
        /// Run mode of a sample interleaved line at x: the run of pixels equal to the pixel on the left, then the
        /// interruption pixel, whose components are all coded against Rb (RItype 0, as CharLS does). Returns the new x.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        RunResult DecodeRunSample(JlsBitReader r, int** prev, int** cur, int nc, int x, int w, int ri)
        {
            ReadOnlySpan<byte> jt = J;
            while (true)
            {
                r.Fill();
                if (r.ReadBit() != 0)
                {
                    int rm = 1 << jt[ri];
                    int n = Math.Min(rm, w - x);
                    for (int i = 0; i < nc; i++) new Span<int>(cur[i] + x, n).Fill(cur[i][x - 1]);
                    x += n;
                    if (n == rm && ri < 31) ri++;
                    if (x == w) break;
                }
                else
                {
                    int bits = jt[ri];
                    int cnt = bits == 0 ? 0 : r.ReadBits(bits);
                    if (cnt > w - x - 1) cnt = w - x - 1;
                    for (int i = 0; i < nc; i++) new Span<int>(cur[i] + x, cnt).Fill(cur[i][x - 1]);
                    x += cnt;
                    int glimit = Limit - bits - 1;
                    if (ri > 0) ri--;
                    for (int i = 0; i < nc; i++)
                    {
                        var res = DecodeRunInterruption(r, cur[i][x - 1], prev[i][x], glimit, 0);
                        r = res.Reader;
                        cur[i][x] = res.Rx;
                    }
                    x++;
                    break;
                }
            }
            return new RunResult { Reader = r, Samples = x, RunIndex = ri };
        }

        // ================================================================== encoding

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void PutGolomb(ref JlsBitWriter wr, int m, int k, int limit, int qbpp)
        {
            int high = m >> k;
            int zl = limit - qbpp - 1;
            if (high < zl)
            {
                int len = high + 1 + k;
                if (len <= 32) wr.Put((uint)((1 << k) | (m & ((1 << k) - 1))), len);
                else
                {
                    wr.PutZeros(high);
                    wr.Put((uint)((1 << k) | (m & ((1 << k) - 1))), 1 + k);
                }
            }
            else
            {
                wr.PutZeros(zl);
                wr.Put(1, 1);
                wr.Put((uint)(m - 1), qbpp);
            }
        }

        /// <summary>
        /// Encodes one line of one component: src holds the original samples (index 0..w-1), cur receives the
        /// reconstructed ones (they differ from src when NEAR > 0). prev[-1], prev[w] and cur[-1] set by the caller.
        /// </summary>
        public void EncodeLine(ref JlsBitWriter writer, int* src, int* prev, int* cur, int w, ref int runIndex)
        {
            if (Near == 0) EncodeLine<LosslessMode>(ref writer, src, prev, cur, w, ref runIndex);
            else EncodeLine<NearMode>(ref writer, src, prev, cur, w, ref runIndex);
        }

        /// <summary>Regular mode sample on writer state in the caller's locals; returns Rx.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int EncodeRegular<TM>(ref ulong acc, ref int nbits, ref byte* pos, ref bool lastFF, int ix, int ra, int rb, int rc, int qs) where TM : struct, IMode
        {
            int sign = qs >> 31;
            int* c = _cx + 4 * ((qs ^ sign) - sign);
            int px = Med(ra, rb, rc) + ((c[2] ^ sign) - sign);
            int maxVal = MaxVal;
            if (px < 0) px = 0; else if (px > maxVal) px = maxVal;
            int err = ((ix - px) ^ sign) - sign;
            int rx;
            if (default(TM).Lossless) rx = ix;
            else
            {
                int step = Step, near = Near;
                err = err > 0 ? (err + near) / step : -((near - err) / step);
                rx = px + (((err * step) ^ sign) - sign);
                if (rx < 0) rx = 0; else if (rx > maxVal) rx = maxVal;
            }
            int range = Range;
            if (err < 0) err += range;
            if (err >= (range + 1) >> 1) err -= range;
            int n = c[3], b = c[1];
            int k = GolombK(n, c[0]);
            int m = (err << 1) ^ (err >> 31);           // 2 err, or -2 err - 1
            if (default(TM).Lossless && k == 0 && 2 * b <= -n) m ^= 1;   // 2 err + 1, or -2 (err + 1)
            // LG(k, LIMIT)
            int high = m >> k;
            if (high < _zl && high + 1 + k <= 32)
            {
                int len = high + 1 + k;
                acc = (acc << len) | (uint)((1 << k) | (m & ((1 << k) - 1)));
                nbits += len;
                if (nbits >= 32) JlsBitWriter.Flush(ref acc, ref nbits, ref pos, ref lastFF);
            }
            else
            {
                var wr = JlsBitWriter.PutGolombSlow(new JlsBitWriter(acc, nbits, pos, lastFF), m, k, _zl, Qbpp);
                acc = wr.Acc; nbits = wr.Bits; pos = wr.Pos; lastFF = wr.LastFF;
            }
            // context update
            int ea = err >> 31;
            int a = c[0] + ((err ^ ea) - ea);
            b += default(TM).Lossless ? err : err * Step;
            if (n == Reset) { a >>= 1; b >>= 1; n >>= 1; }
            n++;
            c[0] = a; c[3] = n;
            if (b <= -n)
            {
                b += n;
                if (c[2] > -128) c[2]--;
                if (b <= -n) b = -n + 1;
            }
            else if (b > 0)
            {
                b -= n;
                if (c[2] < 127) c[2]++;
                if (b > 0) b = 0;
            }
            c[1] = b;
            return rx;
        }

        void EncodeLine<TM>(ref JlsBitWriter writer, int* src, int* prev, int* cur, int w, ref int runIndex) where TM : struct, IMode
        {
            // the writer state lives in locals (registers); see DecodeLine
            ulong acc = writer.Acc;
            int nbits = writer.Bits;
            byte* pos = writer.Pos;
            bool lastFF = writer.LastFF;
            int ri = runIndex;
            int* curEnd = cur + w;
            int ra = cur[-1];
            while (cur < curEnd)
            {
                int rb = prev[0], rc = prev[-1];
                sbyte* qt = _qt;
                int qs = 81 * qt[prev[1] - rb] + 9 * qt[rb - rc] + qt[rc - ra];
                if (qs != 0)
                {
                    int rx = EncodeRegular<TM>(ref acc, ref nbits, ref pos, ref lastFF, *src, ra, rb, rc, qs);
                    *cur++ = rx;
                    prev++; src++;
                    ra = rx;
                    continue;
                }
                var res = EncodeRun(new JlsBitWriter(acc, nbits, pos, lastFF), src, prev, cur, (int)(curEnd - cur), ra, ri);
                acc = res.Writer.Acc; nbits = res.Writer.Bits; pos = res.Writer.Pos; lastFF = res.Writer.LastFF;
                ri = res.RunIndex;
                cur += res.Samples; prev += res.Samples; src += res.Samples;
                ra = cur[-1];
            }
            runIndex = ri;
            writer = new JlsBitWriter(acc, nbits, pos, lastFF);
        }

        internal struct EncodeRunResult { public JlsBitWriter Writer; public int Samples, RunIndex; }

        /// <summary>Run mode (A.7) at cur, run interruption sample included; returns the number of samples coded.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        EncodeRunResult EncodeRun(JlsBitWriter wr, int* src, int* prev, int* cur, int left, int ra, int ri)
        {
            ReadOnlySpan<byte> jt = J;
            int near = Near, maxVal = MaxVal, step = Step, range = Range;
            int x = 0;
            if (near == 0) { while (x < left && src[x] == ra) cur[x++] = ra; }
            else { while (x < left && Math.Abs(src[x] - ra) <= near) cur[x++] = ra; }
            int cnt = x;
            while (cnt >= 1 << jt[ri])
            {
                wr.Put(1, 1);
                cnt -= 1 << jt[ri];
                if (ri < 31) ri++;
                wr.Flush();
            }
            if (x == left)
            {
                if (cnt > 0) wr.Put(1, 1);
                wr.Flush();
                return new EncodeRunResult { Writer = wr, Samples = x, RunIndex = ri };
            }
            wr.Put(0, 1);
            if (jt[ri] > 0) wr.Put((uint)cnt, jt[ri]);
            wr.Flush();
            int glimit = Limit - jt[ri] - 1;
            if (ri > 0) ri--;
            int rb = prev[x];
            int rx = EncodeRunInterruption(ref wr, src[x], ra, rb, glimit, Math.Abs(ra - rb) <= near ? 1 : 0);
            cur[x++] = rx;
            wr.Flush();
            return new EncodeRunResult { Writer = wr, Samples = x, RunIndex = ri };
        }

        /// <summary>Sample interleaved counterpart of <see cref="EncodeLine"/>.</summary>
        public void EncodeLineSample(ref JlsBitWriter writer, int** src, int** prev, int** cur, int nc, int w, ref int runIndex)
        {
            if (Near == 0) EncodeLineSample<LosslessMode>(ref writer, src, prev, cur, nc, w, ref runIndex);
            else EncodeLineSample<NearMode>(ref writer, src, prev, cur, nc, w, ref runIndex);
        }

        void EncodeLineSample<TM>(ref JlsBitWriter writer, int** src, int** prev, int** cur, int nc, int w, ref int runIndex) where TM : struct, IMode
        {
            ulong acc = writer.Acc;
            int nbits = writer.Bits;
            byte* pos = writer.Pos;
            bool lastFF = writer.LastFF;
            int ri = runIndex;
            int* qsv = stackalloc int[nc];
            int x = 0;
            while (x < w)
            {
                sbyte* qt = _qt;
                int any = 0;
                for (int i = 0; i < nc; i++)
                {
                    int* pv = prev[i] + x;
                    int rb = pv[0], rc = pv[-1];
                    int qs = 81 * qt[pv[1] - rb] + 9 * qt[rb - rc] + qt[rc - cur[i][x - 1]];
                    qsv[i] = qs;
                    any |= qs;
                }
                if (any != 0)
                {
                    for (int i = 0; i < nc; i++)
                    {
                        int* pv = prev[i] + x; int* cu = cur[i] + x;
                        cu[0] = EncodeRegular<TM>(ref acc, ref nbits, ref pos, ref lastFF, src[i][x], cu[-1], pv[0], pv[-1], qsv[i]);
                    }
                    x++;
                    continue;
                }
                var res = EncodeRunSample(new JlsBitWriter(acc, nbits, pos, lastFF), src, prev, cur, nc, x, w, ri);
                acc = res.Writer.Acc; nbits = res.Writer.Bits; pos = res.Writer.Pos; lastFF = res.Writer.LastFF;
                ri = res.RunIndex;
                x = res.Samples;
            }
            runIndex = ri;
            writer = new JlsBitWriter(acc, nbits, pos, lastFF);
        }

        /// <summary>Encoder side of <see cref="DecodeRunSample"/>; returns the new x.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        EncodeRunResult EncodeRunSample(JlsBitWriter wr, int** src, int** prev, int** cur, int nc, int x, int w, int ri)
        {
            ReadOnlySpan<byte> jt = J;
            int near = Near;
            int start = x;
            while (x < w)
            {
                bool same = true;
                for (int i = 0; i < nc; i++)
                    if (Math.Abs(src[i][x] - cur[i][start - 1]) > near) { same = false; break; }
                if (!same) break;
                for (int i = 0; i < nc; i++) cur[i][x] = cur[i][start - 1];
                x++;
            }
            int cnt = x - start;
            while (cnt >= 1 << jt[ri])
            {
                wr.Put(1, 1);
                cnt -= 1 << jt[ri];
                if (ri < 31) ri++;
                wr.Flush();
            }
            if (x == w)
            {
                if (cnt > 0) wr.Put(1, 1);
                wr.Flush();
                return new EncodeRunResult { Writer = wr, Samples = x, RunIndex = ri };
            }
            wr.Put(0, 1);
            if (jt[ri] > 0) wr.Put((uint)cnt, jt[ri]);
            wr.Flush();
            int glimit = Limit - jt[ri] - 1;
            if (ri > 0) ri--;
            for (int i = 0; i < nc; i++)
                cur[i][x] = EncodeRunInterruption(ref wr, src[i][x], cur[i][x - 1], prev[i][x], glimit, 0);
            x++;
            return new EncodeRunResult { Writer = wr, Samples = x, RunIndex = ri };
        }

        /// <summary>Run interruption sample (A.7.2) against Ra / Rb with the given RItype; returns Rx.</summary>
        int EncodeRunInterruption(ref JlsBitWriter wr, int ix, int ra, int rb, int glimit, int riType)
        {
            int near = Near, maxVal = MaxVal, step = Step, range = Range;
            int px = riType != 0 ? ra : rb;
            int sign = riType == 0 && ra > rb ? -1 : 0;
            int err = ((ix - px) ^ sign) - sign;
            int rx;
            if (near == 0) rx = ix;
            else
            {
                err = err > 0 ? (err + near) / step : -((near - err) / step);
                rx = px + (((err * step) ^ sign) - sign);
                if (rx < 0) rx = 0; else if (rx > maxVal) rx = maxVal;
            }
            if (err < 0) err += range;
            if (err >= (range + 1) >> 1) err -= range;
            fixed (int* ric = _ri)
            {
                int* rc0 = ric + 3 * riType;
                int temp = riType != 0 ? rc0[0] + (rc0[1] >> 1) : rc0[0];
                int k = GolombK(rc0[1], temp);
                int map = k == 0 && err > 0 && 2 * rc0[2] < rc0[1] ? 1
                        : err < 0 && 2 * rc0[2] >= rc0[1] ? 1
                        : err < 0 && k != 0 ? 1 : 0;
                int em = 2 * Math.Abs(err) - riType - map;
                PutGolomb(ref wr, em, k, glimit, Qbpp);
                UpdateRunInterruption(rc0, err, em, riType, Reset);
            }
            wr.Flush();
            return rx;
        }

    }

    /// <summary>
    /// MSB first bit reader over JPEG-LS entropy coded data: after a 0xFF byte the next byte carries 7 bits, a 0xFF followed
    /// by a byte with the high bit set is a marker and ends the data (zeros are read from there on, as at the end of input).
    /// </summary>
    internal unsafe struct JlsBitReader
    {
        ulong _cache;   // valid bits left aligned
        int _count;
        byte* _pos;
        /// <summary>End of the data (moved to a marker once one is met).</summary>
        public byte* End;

        public ulong Cache { readonly get => _cache; set => _cache = value; }
        public int Count { readonly get => _count; set => _count = value; }
        public byte* Pos { readonly get => _pos; set => _pos = value; }

        public JlsBitReader(byte* pos, byte* end) { _cache = 0; _count = 0; _pos = pos; End = end; }

        /// <summary>Position of the first byte not yet loaded.</summary>
        public readonly byte* Position => _pos;

        /// <summary>Many refills found no data: the scan reads far beyond its end (corrupt or truncated input).</summary>
        public readonly bool Exhausted => _pos - End > 16;

        /// <summary>
        /// <see cref="Fill"/> on reader state held in the caller's locals (which then stay in registers); end is the
        /// reader's End field.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Refill(ref ulong cache, ref int count, ref byte* pos, ref byte* end)
        {
            if (pos + 8 <= end)
            {
                ulong v = *(ulong*)pos;
                ulong nv = ~v;
                if (((nv - 0x0101010101010101UL) & ~nv & 0x8080808080808080UL) == 0)
                {
                    // no 0xFF among the next 8 bytes: take the whole bytes that fit. The bits of the partly taken byte
                    // below them are the true next bits, so loading that byte again later ORs in the same values.
                    cache |= System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v) >> count;
                    int nb = (63 - count) >> 3;
                    pos += nb;
                    count += nb << 3;
                    return;
                }
            }
            var r = new JlsBitReader { _cache = cache, _count = count, _pos = pos, End = end };
            r = FillBytes(r);
            cache = r._cache; count = r._count; pos = r._pos; end = r.End;
        }

        /// <summary>Ensures at least 49 valid bits.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Fill()
        {
            if (_count >= 49) return;
            Refill(ref _cache, ref _count, ref _pos, ref End);
        }

        /// <summary>Byte wise refill. Static and by value, so that the caller's reader stays in registers.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static JlsBitReader FillBytes(JlsBitReader r)
        {
            r.FillBytesCore();
            return r;
        }

        void FillBytesCore()
        {
            while (_count < 49)
            {
                if (_pos >= End)
                {
                    // past the data: zeros. End moves back once per such refill, so that Exhausted can tell a decoder
                    // that runs on corrupt or truncated data from one that finishes the last bits of a scan.
                    _count = 64;
                    End--;
                    return;
                }
                uint b = *_pos;
                if (b != 0xFF)
                {
                    _cache |= (ulong)b << (56 - _count);
                    _count += 8;
                    _pos++;
                }
                else if (_pos + 1 < End && _pos[1] < 0x80)
                {
                    _cache |= 0xFFUL << (56 - _count);
                    _cache |= (ulong)_pos[1] << (57 - _count - 8);
                    _count += 15;
                    _pos += 2;
                }
                else
                {
                    End = _pos;   // marker (or 0xFF at the very end): the data ends here
                    _count = 64;
                    return;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadBit()
        {
            int b = (int)(_cache >> 63);
            _cache <<= 1;
            _count--;
            return b;
        }

        /// <summary>1..32 bits; needs that many valid bits (call <see cref="Fill"/> before).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadBits(int n)
        {
            if (_count < n) Fill();
            int v = (int)(_cache >> (64 - n));
            _cache <<= n;
            _count -= n;
            return v;
        }

        /// <summary>Limited length Golomb code LG(k, limit) (T.87 A.5.3).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadGolomb(int k, int limit, int qbpp)
        {
            Fill();
            int zl = limit - qbpp - 1;
            int lz = BitOperations.LeadingZeroCount(_cache);
            if (lz < zl && lz < _count)
            {
                _cache <<= lz + 1;
                _count -= lz + 1;
                if (k == 0) return lz;
                return (lz << k) | ReadBits(k);
            }
            var g = ReadGolombSlow(this, k, zl, qbpp);
            this = g.Reader;
            return g.Value;
        }

        internal struct GolombResult { public JlsBitReader Reader; public int Value; }

        /// <summary>
        /// LG(k, limit) bit by bit: the unary part may be longer than the valid bits (up to 61 zeros for 16 bit samples
        /// with a large NEAR), and the escape (zl zeros, a one, qbpp bits of value - 1).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static GolombResult ReadGolombSlow(JlsBitReader r, int k, int zl, int qbpp)
        {
            int z = 0;
            while (true)
            {
                r.Fill();
                int lz = Math.Min(BitOperations.LeadingZeroCount(r._cache), r._count);
                if (z + lz >= zl)
                {
                    r.Skip(zl - z + 1);   // the zeros, then the one that ends them
                    int v = r.ReadBits(qbpp) + 1;
                    return new GolombResult { Reader = r, Value = v };
                }
                if (lz < r._count)
                {
                    r.Skip(lz + 1);
                    z += lz;
                    int v = k == 0 ? z : (z << k) | r.ReadBits(k);
                    return new GolombResult { Reader = r, Value = v };
                }
                z += lz;
                r.Skip(lz);
            }
        }

        /// <summary>Drops n (at most the valid count) bits.</summary>
        void Skip(int n)
        {
            while (n > 0)
            {
                if (_count == 0) Fill();
                int t = Math.Min(n, _count);
                _cache = t >= 64 ? 0 : _cache << t;
                _count -= t;
                n -= t;
            }
        }
    }

    /// <summary>MSB first bit writer with the JPEG-LS bit stuffing (7 bits after each 0xFF byte).</summary>
    internal unsafe struct JlsBitWriter
    {
        ulong _acc;     // right aligned, _n valid bits
        int _n;
        byte* _pos;
        bool _lastFF;

        public JlsBitWriter(byte* pos) { _acc = 0; _n = 0; _pos = pos; _lastFF = false; }
        public JlsBitWriter(ulong acc, int bits, byte* pos, bool lastFF) { _acc = acc; _n = bits; _pos = pos; _lastFF = lastFF; }

        public readonly ulong Acc => _acc;
        public readonly int Bits => _n;
        public readonly byte* Pos => _pos;
        public readonly bool LastFF => _lastFF;

        /// <summary>Writes out 32 or more pending bits, on writer state held in the caller's locals.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Flush(ref ulong acc, ref int n, ref byte* pos, ref bool lastFF)
        {
            if (!lastFF)
            {
                uint v = (uint)(acc >> (n - 32));
                uint nv = ~v;
                if (((nv - 0x01010101u) & ~nv & 0x80808080u) == 0)
                {
                    // no 0xFF among the four bytes: no stuffing
                    *(uint*)pos = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v);
                    pos += 4;
                    n -= 32;
                    return;
                }
            }
            var w = new JlsBitWriter(acc, n, pos, lastFF);
            w = FlushBytes(w);
            acc = w._acc; n = w._n; pos = w._pos; lastFF = w._lastFF;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static JlsBitWriter FlushBytes(JlsBitWriter w) { w.FlushBytes(); return w; }

        /// <summary>LG(k, limit) codes longer than 32 bits and escapes; by value like <see cref="FlushBytes(JlsBitWriter)"/>.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static JlsBitWriter PutGolombSlow(JlsBitWriter w, int m, int k, int zl, int qbpp)
        {
            int high = m >> k;
            if (high < zl)
            {
                w.PutZeros(high);
                w.Put((uint)((1 << k) | (m & ((1 << k) - 1))), 1 + k);
            }
            else
            {
                w.PutZeros(zl);
                w.Put(1, 1);
                w.Put((uint)(m - 1), qbpp);
            }
            w.Flush();
            return w;
        }

        public byte* Position => _pos;

        /// <summary>Moves the output pointer after the buffer was reallocated.</summary>
        public void Rebase(long delta) => _pos += delta;

        /// <summary>Appends len (0..32) bits; at most 32 bits may be pending before (call <see cref="Flush()"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Put(uint v, int len)
        {
            _acc = (_acc << len) | v;
            _n += len;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PutZeros(int len)
        {
            while (len > 24)
            {
                Put(0, 24);
                Flush();
                len -= 24;
            }
            Put(0, len);
            Flush();
        }

        /// <summary>Writes out whole bytes; leaves fewer than 8 bits pending.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Flush()
        {
            if (_n >= 24) FlushBytes();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void FlushBytes()
        {
            while (true)
            {
                int take = _lastFF ? 7 : 8;
                if (_n < take) return;
                uint b = (uint)(_acc >> (_n - take)) & (_lastFF ? 0x7Fu : 0xFFu);
                _n -= take;
                *_pos++ = (byte)b;
                _lastFF = b == 0xFF;
            }
        }

        /// <summary>Pads the last byte with zero bits (T.87 A.1) and returns the end of the data.</summary>
        public byte* Finish()
        {
            FlushBytes();
            if (_n > 0)
            {
                int take = _lastFF ? 7 : 8;
                uint b = (uint)(_acc << (take - _n)) & (_lastFF ? 0x7Fu : 0xFFu);
                *_pos++ = (byte)b;
                _lastFF = false;
                _n = 0;
            }
            if (_lastFF) { *_pos++ = 0; _lastFF = false; }
            return _pos;
        }
    }
}
