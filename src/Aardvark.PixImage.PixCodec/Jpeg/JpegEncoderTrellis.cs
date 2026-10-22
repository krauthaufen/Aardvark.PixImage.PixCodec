using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Aardvark.ImageCodecs.JpegImpl
{
    /// <summary>
    /// Rate-distortion optimized quantization ("trellis quantization"). Every block's quantized values are chosen to
    /// minimize D + lambda * R instead of rounding each coefficient on its own: D is the error the choice causes, R the
    /// number of bits the Huffman coder spends on it. Following the dynamic programming formulation of Ramchandran and
    /// Vetterli (1994) and Crouse and Ramchandran (1997), the AC coefficients of a block are walked in zigzag order with
    /// the position of the previous nonzero coefficient as state, so zero runs, ZRL symbols and the end-of-block
    /// symbol are priced as the coder will write them; each coefficient keeps its rounded value, becomes zero, or (where
    /// that saves a value bit) drops one step. The DC values are differentially coded across blocks, so they are chosen
    /// by a Viterbi search along the scan order with the two nearest levels of each block as states.
    /// <para>The error of a coefficient is measured in quantizer steps weighted by the square root of the step size,
    /// a compromise between plain squared error (which favors low frequencies) and error relative to the table (which
    /// trusts the table's perceptual weighting completely); lambda scales with the square root of the table's mean
    /// step. The rate model comes from the Huffman tables in use, or for optimized tables, which only exist afterwards,
    /// from the Annex K ones. The constants were tuned for the SSIMULACRA2 BD-rate on the Kodak images.</para>
    /// <para>All arithmetic is single precision with a fixed order of operations, and the vector and scalar code
    /// compute the same values, so the output does not depend on the instruction set.</para>
    /// </summary>
    internal sealed unsafe partial class JpegEncoder
    {
        const float TrellisLambda = 0.04f;       // lambda for luma, times sqrt(mean step)
        const float TrellisChromaLambda = 0.3f;  // relative lambda of the chroma components
        const float TrellisDcLambda = 0.3f;      // relative lambda of the DC search

        /// <summary>Test hook: use the scalar code of the trellis even where AVX2 is available.</summary>
        internal static bool TrellisForceScalar;

        // arithmetic coding gets the same coefficients as Huffman coding (rated with the Huffman model, a fair proxy)
        bool UseTrellis => _opt.Trellis && _precision == 8;

        /// <summary>Unquantized forward DCT output (scaled by 8, zigzag order), per component, kept for the trellis.</summary>
        short[]?[]? _raw;

        /// <summary>Bit costs of one Huffman table pair as the trellis sees them (code length plus value bits).</summary>
        sealed class RateModel
        {
            public readonly float[] Dc = new float[17];
            public readonly float[] Ac = new float[256];

            public RateModel(HuffEnc dc, HuffEnc ac)
            {
                Fill(dc.Packed, Dc, 17);
                Fill(ac.Packed, Ac, 256);
            }

            static void Fill(uint[] packed, float[] dst, int n)
            {
                int longest = 0;
                for (int i = 0; i < 256; i++) longest = Math.Max(longest, (int)(packed[i] & 255));
                // a symbol without a code costs a little more than the longest one: the rebuilt table will need one
                for (int s = 0; s < n; s++)
                {
                    int len = (int)(packed[s] & 255);
                    dst[s] = (len != 0 ? len : longest + 2) + (s & 15);
                }
            }
        }

        /// <summary>Forward DCT without quantization (what <see cref="Transform"/> does in trellis mode), of one MCU row or of every block for -1.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)] // runs once per image: no point in starting unoptimized
        void TransformRaw(int my)
        {
            float* one = stackalloc float[64];
            for (int i = 0; i < 64; i++) one[i] = 1f;
            for (int ci = 0; ci < _comps.Length; ci++)
            {
                var c = _comps[ci];
                var raw = _raw![ci] ??= GC.AllocateUninitializedArray<short>(c.BlocksW * c.BlocksH * 64);
                fixed (byte* plane = c.Plane)
                fixed (short* r = raw)
                {
                    int by0 = my < 0 ? 0 : my * c.V, by1 = my < 0 ? c.BlocksH : by0 + c.V;
                    int top = StripPlanes ? by0 : 0;
                    short* o = r + (nint)by0 * c.BlocksW * 64;
                    for (int by = by0; by < by1; by++)
                    {
                        byte* row = plane + (nint)(by - top) * 8 * c.Stride;
                        for (int bx = 0; bx < c.BlocksW; bx++, o += 64)
                        {
                            if (IsDummy(c, bx, by)) new Span<short>(o, 64).Clear(); // its DC is set by FillDummyDc
                            else JpegDct.FdctQuantize(row + bx * 8, c.Stride, one, o);
                        }
                    }
                }
            }
        }

        /// <summary>Block indices of each component in the order the (interleaved or single component) scan codes them, with restart points.</summary>
        int[] ScanOrder(Comp c, out bool[] reset)
        {
            bool single = _comps.Length == 1;
            int mcusX = single ? c.ScanBlocksW : _mcusX, mcusY = single ? c.ScanBlocksH : _mcusY;
            int h = single ? 1 : c.H, v = single ? 1 : c.V;
            var order = new int[mcusX * mcusY * h * v];
            reset = new bool[order.Length];
            int n = 0, mcu = 0;
            bool pendingReset = false;
            for (int my = 0; my < mcusY; my++)
                for (int mx = 0; mx < mcusX; mx++, mcu++)
                {
                    if (_restart != 0 && mcu % _restart == 0) pendingReset = true;
                    for (int by = 0; by < v; by++)
                        for (int bx = 0; bx < h; bx++)
                        {
                            int x = mx * h + bx, y = my * v + by;
                            // padding blocks repeat the previous DC, so they are left out: the next real block is
                            // predicted from the real one before it (or from 0 after a restart)
                            if (!single && IsDummy(c, x, y)) continue;
                            reset[n] = pendingReset; pendingReset = false;
                            order[n++] = y * c.BlocksW + x;
                        }
                }
            Array.Resize(ref order, n);
            Array.Resize(ref reset, n);
            return order;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)] // runs once per image: no point in starting unoptimized
        void RunTrellis()
        {
            int nt = _comps.Length == 1 ? 1 : 2;
            // zigzag ordered reciprocals of 8 q, and the distortion weights: a coefficient's error x - l (in steps)
            // counts (x - l)^2 * sqrt(q)
            var invq = new float[2][];
            var weight = new float[2][];
            var lambdaScale = new float[2];
            var dcWeight = new float[2];
            for (int t = 0; t < nt; t++)
            {
                float mean = 0;
                for (int k = 0; k < 64; k++) mean += _quant[t][k];
                lambdaScale[t] = MathF.Sqrt(mean / 64);
                invq[t] = new float[64]; weight[t] = new float[64];
                for (int k = 0; k < 64; k++)
                {
                    int q = _quant[t][JpegTables.ZigZag[k]];
                    invq[t][k] = Reciprocal(q);
                    weight[t][k] = MathF.Sqrt(q);
                }
                dcWeight[t] = weight[t][0];
                weight[t][0] = 0; // the AC trellis sums zeroing errors from position 1 on
            }

            // the rate model: the tables in use, or for optimized tables, which only exist afterwards, the Annex K ones
            // (measured: hardly worse than a model from the image's own statistics, and it saves a pass)
            bool standard = !_opt.Progressive && !_opt.OptimizeHuffman && !_opt.Arithmetic;
            for (int ci = 0; ci < _comps.Length; ci++)
            {
                var c = _comps[ci];
                int t = c.Tq;
                var model = standard ? new RateModel(c.Dc, c.Ac)
                    : t == 0 ? new RateModel(new HuffEnc(JpegTables.DcLumaCounts, JpegTables.DcValues), new HuffEnc(JpegTables.AcLumaCounts, JpegTables.AcLumaValues))
                    : new RateModel(new HuffEnc(JpegTables.DcChromaCounts, JpegTables.DcValues), new HuffEnc(JpegTables.AcChromaCounts, JpegTables.AcChromaValues));
                float lambda = TrellisLambda * lambdaScale[t] * (ci == 0 ? 1f : TrellisChromaLambda);
                var order = ScanOrder(c, out var reset);
                TrellisComponent(ci, invq[t], weight[t], dcWeight[t], model, lambda, order, reset);
            }
            _raw = null;

            // statistics of the final coefficients for the optimized sequential tables (DC is gathered by the scan)
            if (OptimizedBaseline)
                foreach (var c in _comps)
                    fixed (short* coefs = c.Coefs)
                    fixed (int* freq = c.Ac.Freq)
                        for (int by = 0; by < c.StatBlocksH; by++)
                            for (int bx = 0; bx < c.StatBlocksW; bx++)
                                GatherAc(coefs + ((nint)by * c.BlocksW + bx) * 64, freq, _maxAcSize);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)] // runs once per image: no point in starting unoptimized
        void TrellisComponent(int ci, float[] invq, float[] weight, float dcWeight, RateModel model, float lambda, int[] order, bool[] reset)
        {
            var c = _comps[ci];
            int nBlocks = c.BlocksW * c.BlocksH;
            // the rate model with lambda folded in
            float* lbits = stackalloc float[256];
            for (int i = 0; i < 256; i++) lbits[i] = lambda * model.Ac[i];
            // the most a longer zero run can save over a shorter one with the same size category (ZRLs only add):
            // a predecessor that is worse than a later one by more than that can never be chosen again
            float spread = 0;
            for (int sz = 1; sz <= 10; sz++)
            {
                float lo = float.MaxValue, hi = 0;
                for (int r = 0; r < 16; r++) { lo = Math.Min(lo, lbits[r << 4 | sz]); hi = Math.Max(hi, lbits[r << 4 | sz]); }
                spread = Math.Max(spread, hi - lo);
            }
            // lambda * bits of a run of zeros (ZRLs included) followed by a coefficient of size category s, at s * 64 + run
            float* lrun = stackalloc float[11 * 64];
            for (int sz = 0; sz <= 10; sz++)
                for (int r = 0; r < 64; r++) lrun[sz * 64 + r] = lbits[(r & 15) << 4 | sz] + (r >> 4) * lbits[0xF0];
            fixed (short* raw = _raw![ci], coefs = c.Coefs)
            fixed (float* iq = invq, w = weight, dcBits = model.Dc)
            {
                // (blocks outside the scan, MCU padding of a single component image, get the same treatment: harmless)
                for (int b = 0; b < nBlocks; b++) QuantizeBlockAc(raw + (nint)b * 64, iq, w, lbits, lrun, spread, coefs + (nint)b * 64);
                TrellisDc(raw, coefs, iq[0], dcWeight, dcBits, lambda * TrellisDcLambda, order, reset);
            }
        }

        /// <summary>
        /// Chooses the AC levels of one block (zigzag positions 1..63) by dynamic programming over the position of the
        /// last nonzero coefficient; output[0] gets the rounded DC value. <paramref name="lbits"/> holds lambda times
        /// the bits of every run/size symbol including its value bits.
        /// </summary>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void QuantizeBlockAc(short* raw, float* invq, float* weight, float* lbits, float* lrun, float spread, short* output)
        {
            float* x = stackalloc float[64];     // magnitudes in quantizer steps
            float* zcost = stackalloc float[64]; // distortion of zeroing positions 1..k
            float* baseCost = stackalloc float[64]; // best cost with k the last nonzero position, minus zcost[k]
            byte* active = stackalloc byte[64];
            byte* from = stackalloc byte[64];
            short* level = stackalloc short[64];

            ulong candidates = 0; // positions whose rounded magnitude is at least 1
            if (Avx2.IsSupported && !TrellisForceScalar)
            {
                var half = Vector256.Create(0.5f);
                var zero = Vector256<short>.Zero;
                var carry = Vector256<float>.Zero;
                for (int i = 0; i < 64; i += 8)
                {
                    var a = Avx.ConvertToVector256Single(Avx2.Abs(Avx2.ConvertToVector256Int32(raw + i)).AsInt32()) * Avx.LoadVector256(invq + i);
                    Avx.Store(x + i, a);
                    candidates |= (ulong)(uint)Avx.MoveMask(Avx.CompareGreaterThanOrEqual(a, half)) << i;
                    // running sum of the zeroing errors (weight[0] is 0, so the DC does not count): prefix within
                    // each 128 bit half, then the lower half's total into the upper one, then the carry
                    var e = a * a * Avx.LoadVector256(weight + i);
                    e += Avx2.ShiftLeftLogical128BitLane(e.AsByte(), 4).AsSingle();
                    e += Avx2.ShiftLeftLogical128BitLane(e.AsByte(), 8).AsSingle();
                    var low = Avx.Permute(e, 0xFF);
                    e += Avx.Permute2x128(low, low, 0x08) + carry;
                    Avx.Store(zcost + i, e);
                    carry = Avx.Permute2x128(Avx.Permute(e, 0xFF), Avx.Permute(e, 0xFF), 0x11);
                }
                Avx.Store(output, zero); Avx.Store(output + 16, zero); Avx.Store(output + 32, zero); Avx.Store(output + 48, zero);
            }
            else
            {
                // the same additions in the same order as the vector path, so both give identical results
                float* e = stackalloc float[8];
                float carry = 0;
                for (int i = 0; i < 64; i += 8)
                {
                    for (int n = 0; n < 8; n++)
                    {
                        float a = MathF.Abs(raw[i + n]) * invq[i + n];
                        x[i + n] = a;
                        e[n] = a * a * weight[i + n];
                        if (a >= 0.5f) candidates |= 1UL << (i + n);
                        output[i + n] = 0;
                    }
                    for (int h = 0; h < 8; h += 4)
                    {
                        float e0 = e[h], e1 = e[h + 1] + e[h], e2 = e[h + 2] + e[h + 1], e3 = e[h + 3] + e[h + 2];
                        e[h] = e0; e[h + 1] = e1; e[h + 2] = e2 + e0; e[h + 3] = e3 + e1;
                    }
                    float lowTotal = e[3];
                    for (int n = 0; n < 4; n++) zcost[i + n] = e[n] + (0f + carry);
                    for (int n = 4; n < 8; n++) zcost[i + n] = e[n] + (lowTotal + carry);
                    carry = zcost[i + 7];
                }
            }
            {
                float f = raw[0] * invq[0];
                output[0] = (short)(int)(f + (f < 0 ? -0.5f : 0.5f)); // the DC trellis refines this for blocks in the scan
            }
            candidates &= ~1UL;
            if (candidates == 0) return;

            float leob = lbits[0];
            int nActive = 1;
            active[0] = 0; baseCost[0] = 0;
            ulong m = candidates;
            while (m != 0)
            {
                int k = BitOperations.TrailingZeroCount(m);
                m &= m - 1;
                float xk = x[k], wk = weight[k];
                int l0 = Math.Min((int)(xk + 0.5f), 1023);
                int s = BitLength(l0);
                // cheapest predecessor for this size category; lrun[s * 64 + run] is the cost of a run before it
                float* row = lrun + (s << 6) + k - 1;
                int bestFrom = active[0];
                float rate = baseCost[bestFrom] + row[-bestFrom];
                for (int a = 1; a < nActive; a++)
                {
                    int j = active[a];
                    float c = baseCost[j] + row[-j];
                    bool better = c < rate;
                    rate = better ? c : rate;
                    bestFrom = better ? j : bestFrom;
                }
                float e = xk - l0;
                float best = rate + wk * e * e;
                int bestLevel = l0;
                if (l0 > 1 && (l0 & (l0 - 1)) == 0)
                {
                    // one step down only helps when it saves a value bit (l0 a power of two); otherwise the code is
                    // the same and the error larger
                    row -= 64;
                    int from1 = active[0];
                    float rate1 = baseCost[from1] + row[-from1];
                    for (int a = 1; a < nActive; a++)
                    {
                        int j = active[a];
                        float c = baseCost[j] + row[-j];
                        bool better = c < rate1;
                        rate1 = better ? c : rate1;
                        from1 = better ? j : from1;
                    }
                    e += 1;
                    rate1 += wk * e * e;
                    if (rate1 < best) { best = rate1; bestFrom = from1; bestLevel = l0 - 1; }
                }
                float bk = baseCost[k] = best + zcost[k - 1] - zcost[k];
                from[k] = (byte)bestFrom;
                level[k] = (short)(raw[k] < 0 ? -bestLevel : bestLevel);
                if (nActive >= 3)
                {
                    // drop the predecessors k dominates
                    float limit = bk + spread;
                    int kept = 0;
                    for (int a = 0; a < nActive; a++)
                    {
                        int j = active[a];
                        active[kept] = (byte)j;
                        kept += baseCost[j] < limit ? 1 : 0;
                    }
                    nActive = kept;
                }
                active[nActive++] = (byte)k;
            }
            // where to stop: the last nonzero coefficient, followed by an end of block unless it is position 63
            float end = float.MaxValue;
            int lastPos = 0;
            for (int a = 0; a < nActive; a++)
            {
                int j = active[a];
                float cost = baseCost[j] + (j < 63 ? leob : 0);
                if (cost < end) { end = cost; lastPos = j; }
            }
            for (int k = lastPos; k > 0; k = from[k]) output[k] = level[k];
        }

        /// <summary>Viterbi search over the DC levels of a component in scan order: each block may take either of the two levels around its value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void TrellisDc(short* raw, short* coefs, float invq, float weight, float* dcBits, float lambda, int[] order, bool[] reset)
        {
            int n = order.Length;
            if (n == 0) return;
            var choice = new byte[n * 2]; // predecessor state per block and state
            float c0 = 0, c1 = 0;          // costs of the two states of the previous block
            int p0 = 0, p1 = 0;            // their levels
            for (int i = 0; i < n; i++)
            {
                float x = raw[(nint)order[i] * 64] * invq;
                int lo = (int)MathF.Floor(x), hi = lo + 1;
                float dlo = weight * (x - lo) * (x - lo), dhi = weight * (hi - x) * (hi - x);
                float n0, n1;
                if (i == 0 || reset[i])
                {
                    float b = i == 0 ? 0 : Math.Min(c0, c1);
                    byte from = (byte)(i == 0 || c0 <= c1 ? 0 : 1);
                    n0 = b + dlo + lambda * dcBits[BitLength(Math.Abs(lo))];
                    n1 = b + dhi + lambda * dcBits[BitLength(Math.Abs(hi))];
                    choice[2 * i] = from; choice[2 * i + 1] = from;
                }
                else
                {
                    float a0 = c0 + lambda * dcBits[BitLength(Math.Abs(lo - p0))], a1 = c1 + lambda * dcBits[BitLength(Math.Abs(lo - p1))];
                    float b0 = c0 + lambda * dcBits[BitLength(Math.Abs(hi - p0))], b1 = c1 + lambda * dcBits[BitLength(Math.Abs(hi - p1))];
                    if (a0 <= a1) { n0 = a0 + dlo; choice[2 * i] = 0; } else { n0 = a1 + dlo; choice[2 * i] = 1; }
                    if (b0 <= b1) { n1 = b0 + dhi; choice[2 * i + 1] = 0; } else { n1 = b1 + dhi; choice[2 * i + 1] = 1; }
                }
                c0 = n0; c1 = n1; p0 = lo; p1 = hi;
            }
            int state = c0 <= c1 ? 0 : 1;
            for (int i = n - 1; i >= 0; i--)
            {
                float x = raw[(nint)order[i] * 64] * invq;
                int lo = (int)MathF.Floor(x);
                coefs[(nint)order[i] * 64] = (short)(lo + state);
                state = choice[2 * i + state];
            }
        }
    }
}
