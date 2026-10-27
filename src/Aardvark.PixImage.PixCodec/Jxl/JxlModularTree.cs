using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>Meta-adaptive (MA) context tree of a modular image.</summary>
    internal sealed class JxlMaTree
    {
        // decision nodes: Property >= 0; leaves: Property = -1
        public int[] Property = Array.Empty<int>();
        public int[] Value = Array.Empty<int>();
        public int[] Left = Array.Empty<int>();     // taken when property > value
        public int[] Right = Array.Empty<int>();
        // leaves
        public int[] Predictor = Array.Empty<int>();
        public long[] Offset = Array.Empty<long>();
        public uint[] Multiplier = Array.Empty<uint>();
        public int[] Context = Array.Empty<int>();
        public int NumLeaves;
        public int Count;

        public const int NumNonRefProperties = 16;
        public const int MaxNodes = 1 << 22;

        public static JxlMaTree Read(JxlBitReader br, long maxNodes)
        {
            var code = JxlEntropyCode.Read(br, 6);
            var r = new JxlSymbolReader(code, br, 0);
            var prop = new List<int>(); var val = new List<int>(); var left = new List<int>(); var right = new List<int>();
            var pred = new List<int>(); var off = new List<long>(); var mul = new List<uint>(); var ctx = new List<int>();
            int toDecode = 1, nextIndex = 1, leaves = 0;
            long limit = Math.Min(maxNodes, MaxNodes);
            while (toDecode > 0)
            {
                if (prop.Count >= limit) throw new ImageFormatException("JPEG XL: MA tree too large");
                toDecode--;
                uint p = r.ReadUint(br, 1);
                if (p > 0)
                {
                    int v = JxlBitReader.UnpackSigned(r.ReadUint(br, 0));
                    // any property index is valid, those not defined (H.4.1) read as 0; all beyond int.MaxValue are
                    // such (libjxl rejects e.g. 300)
                    prop.Add((int)Math.Min(p - 1, int.MaxValue)); val.Add(v); left.Add(nextIndex); right.Add(nextIndex + 1);
                    pred.Add(0); off.Add(0); mul.Add(0); ctx.Add(0);
                    nextIndex += 2;
                    toDecode += 2;
                }
                else
                {
                    uint predictor = r.ReadUint(br, 2);
                    if (predictor >= 14) throw new ImageFormatException("JPEG XL: bad predictor");
                    long offset = JxlBitReader.UnpackSigned(r.ReadUint(br, 3));
                    uint mulLog = r.ReadUint(br, 4);
                    if (mulLog > 30) throw new ImageFormatException("JPEG XL: bad multiplier");
                    uint mulBits = r.ReadUint(br, 5);
                    if (mulBits >= (1u << (31 - (int)mulLog)) - 1) throw new ImageFormatException("JPEG XL: bad multiplier");
                    prop.Add(-1); val.Add(0); left.Add(0); right.Add(0);
                    pred.Add((int)predictor); off.Add(offset); mul.Add((mulBits + 1) << (int)mulLog); ctx.Add(leaves++);
                }
                if (br.Overrun) throw new ImageFormatException("JPEG XL: truncated MA tree");
            }
            r.CheckFinalState();
            var t = new JxlMaTree
            {
                Property = prop.ToArray(), Value = val.ToArray(), Left = left.ToArray(), Right = right.ToArray(),
                Predictor = pred.ToArray(), Offset = off.ToArray(), Multiplier = mul.ToArray(), Context = ctx.ToArray(),
                NumLeaves = leaves, Count = prop.Count,
            };
            return t;
        }

        /// <summary>
        /// The tree for one channel of one stream: decisions on the channel (property 0) and stream (property 1) resolved.
        /// Returns a compacted tree whose node 0 is the root.
        /// </summary>
        public JxlMaTree Specialize(int channel, int stream)
        {
            var map = new Dictionary<int, int>();
            var prop = new List<int>(); var val = new List<int>(); var left = new List<int>(); var right = new List<int>();
            var pred = new List<int>(); var off = new List<long>(); var mul = new List<uint>(); var ctx = new List<int>();
            var queue = new Queue<(int src, int dst)>();
            int Resolve(int n)
            {
                while (Property[n] == 0 || Property[n] == 1)
                {
                    int pv = Property[n] == 0 ? channel : stream;
                    n = pv > Value[n] ? Left[n] : Right[n];
                }
                return n;
            }
            int Add(int src)
            {
                int d = prop.Count;
                prop.Add(Property[src]); val.Add(Value[src]); left.Add(0); right.Add(0);
                pred.Add(Predictor[src]); off.Add(Offset[src]); mul.Add(Multiplier[src]); ctx.Add(Context[src]);
                queue.Enqueue((src, d));
                return d;
            }
            Add(Resolve(0));
            while (queue.Count > 0)
            {
                var (s, d) = queue.Dequeue();
                if (Property[s] < 0) continue;
                left[d] = Add(Resolve(Left[s]));
                right[d] = Add(Resolve(Right[s]));
            }
            return new JxlMaTree
            {
                Property = prop.ToArray(), Value = val.ToArray(), Left = left.ToArray(), Right = right.ToArray(),
                Predictor = pred.ToArray(), Offset = off.ToArray(), Multiplier = mul.ToArray(), Context = ctx.ToArray(),
                NumLeaves = NumLeaves, Count = prop.Count,
            };
        }

        public int MaxProperty()
        {
            int m = -1;
            for (int i = 0; i < Count; i++) m = Math.Max(m, Property[i]);
            return m;
        }

        public bool UsesWp()
        {
            for (int i = 0; i < Count; i++)
                if (Property[i] == 15 || Property[i] < 0 && Predictor[i] == 6) return true;
            return false;
        }
    }

    /// <summary>Parameters of the self-correcting (weighted) predictor.</summary>
    internal sealed class JxlWpHeader
    {
        public int P1C = 16, P2C = 10, P3Ca = 7, P3Cb = 7, P3Cc = 7, P3Cd = 0, P3Ce = 0;
        public uint W0 = 0xd, W1 = 0xc, W2 = 0xc, W3 = 0xc;

        public static readonly JxlWpHeader Default = new();

        public static JxlWpHeader Read(JxlBitReader br)
        {
            if (br.ReadBool()) return Default;
            return new JxlWpHeader
            {
                P1C = (int)br.ReadBits(5), P2C = (int)br.ReadBits(5),
                P3Ca = (int)br.ReadBits(5), P3Cb = (int)br.ReadBits(5), P3Cc = (int)br.ReadBits(5), P3Cd = (int)br.ReadBits(5), P3Ce = (int)br.ReadBits(5),
                W0 = br.ReadBits(4), W1 = br.ReadBits(4), W2 = br.ReadBits(4), W3 = br.ReadBits(4),
            };
        }
    }

    /// <summary>
    /// State of the self-correcting predictor over one channel. Two rows of errors with a padding of two; the sub-predictor
    /// errors of a pixel are also added to the entry north-east of it, so that the entries N and NW of the next pixel
    /// include its W and WW errors (this also defines the behaviour at the image edges).
    /// </summary>
    internal sealed class JxlWeightedPredictor
    {
        struct Cell
        {
            public uint E0, E1, E2, E3;   // sub-predictor errors (the N-E cell also accumulates the errors of W and WW)
            public int True;              // error of the final prediction
        }

        readonly JxlWpHeader h;
        readonly int w, stride;
        readonly Cell[] cells;
        int cur, prev;
        long pred, sub0, sub1, sub2, sub3;
        public int MaxError;
        readonly int p1c, p2c, p3a, p3b, p3c, p3d, p3e;
        readonly uint mw0, mw1, mw2, mw3;

        static readonly uint[] DivLookup = BuildDiv();

        static uint[] BuildDiv()
        {
            var d = new uint[64];
            for (int i = 0; i < 64; i++) d[i] = (uint)((1 << 24) / (i + 1));
            return d;
        }

        public JxlWeightedPredictor(JxlWpHeader header, int width)
        {
            h = header; w = width; stride = width + 2;
            cells = new Cell[2 * stride];
            p1c = h.P1C; p2c = h.P2C; p3a = h.P3Ca; p3b = h.P3Cb; p3c = h.P3Cc; p3d = h.P3Cd; p3e = h.P3Ce;
            mw0 = h.W0; mw1 = h.W1; mw2 = h.W2; mw3 = h.W3;
        }

        public void StartRow(int y)
        {
            cur = (y & 1) != 0 ? 0 : stride;
            prev = (y & 1) != 0 ? stride : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint ErrorWeight(uint x, uint maxWeight)
        {
            int shift = 63 - BitOperations.LeadingZeroCount((ulong)x + 1) - 5;
            if (shift < 0) shift = 0;
            return 4 + ((maxWeight * DivLookup[x >> shift]) >> shift);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static long Abs(long v) => (v ^ (v >> 63)) - (v >> 63);

        /// <summary>Returns the prediction (pixel units) for (x, y); the neighbours are the plain sample values.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long Predict(int x, int y, long N, long W, long NE, long NW, long NN)
        {
            ref Cell c0 = ref MemoryMarshal.GetArrayDataReference(cells);
            int pN = prev + x;
            ref Cell cn = ref Unsafe.Add(ref c0, pN);
            ref Cell cne = ref Unsafe.Add(ref c0, x < w - 1 ? pN + 1 : pN);
            ref Cell cnw = ref Unsafe.Add(ref c0, x > 0 ? pN - 1 : pN);
            uint w0 = ErrorWeight(cn.E0 + cne.E0 + cnw.E0, mw0);
            uint w1 = ErrorWeight(cn.E1 + cne.E1 + cnw.E1, mw1);
            uint w2 = ErrorWeight(cn.E2 + cne.E2 + cnw.E2, mw2);
            uint w3 = ErrorWeight(cn.E3 + cne.E3 + cnw.E3, mw3);
            long N3 = N << 3, W3 = W << 3, NE3 = NE << 3, NW3 = NW << 3, NN3 = NN << 3;
            long teW = x == 0 ? 0 : Unsafe.Add(ref c0, cur + x - 1).True;
            long teN = cn.True, teNW = cnw.True, teNE = cne.True;
            long sumWN = teN + teW;
            long p0 = W3 + NE3 - N3;
            long p1 = N3 - ((sumWN + teNE) * p1c >> 5);
            long p2 = W3 - ((sumWN + teNW) * p2c >> 5);
            long p3 = N3 - ((teNW * p3a + teN * p3b + teNE * p3c + (NN3 - N3) * p3d + (NW3 - W3) * p3e) >> 5);
            uint ws = w0 + w1 + w2 + w3;
            int lw = 31 - BitOperations.LeadingZeroCount(ws) - 4;
            w0 >>= lw; w1 >>= lw; w2 >>= lw; w3 >>= lw;
            uint sum = w0 + w1 + w2 + w3;
            long s = (sum >> 1) - 1 + p0 * w0 + p1 * w1 + p2 * w2 + p3 * w3;
            long p = (s * DivLookup[sum - 1]) >> 24;
            if (((teN ^ teW) | (teN ^ teNW)) <= 0)
            {
                long mx = Math.Max(W3, Math.Max(N3, NE3)), mn = Math.Min(W3, Math.Min(N3, NE3));
                if (p < mn) p = mn; else if (p > mx) p = mx;
            }
            long me = teW;
            if (Abs(teN) > Abs(me)) me = teN;
            if (Abs(teNW) > Abs(me)) me = teNW;
            if (Abs(teNE) > Abs(me)) me = teNE;
            MaxError = (int)me;
            pred = p; sub0 = p0; sub1 = p1; sub2 = p2; sub3 = p3;
            return (p + 3) >> 3;
        }

        /// <summary>
        /// The loop of JxlModularChannelDecoder.DecodeWpIntervals for the fast ANS reader, with the predictor and the entropy
        /// decoder state in locals: the same arithmetic as <see cref="Predict"/> and <see cref="Update"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public unsafe void DecodeIntervals(ref JxlAnsFast rd, int[] d, int h, int[] bounds, int[] lpred, long[] lmul, long[] loff, int[] lctx)
        {
            var regs = rd.Load();
            uint st = regs.St; ulong buf = regs.Buf; int nbits = regs.NBits; byte* bp = regs.P;
            int nl = bounds.Length;
            if (lpred.Length < nl || lmul.Length < nl || loff.Length < nl || lctx.Length < nl || nl == 0 || bounds[nl - 1] != int.MaxValue)
                throw new InvalidOperationException();
            if (d.Length < (long)w * h) throw new InvalidOperationException();
            long* lm = stackalloc long[nl]; long* lo = stackalloc long[nl];
            int* lp = stackalloc int[nl]; int* lc = stackalloc int[nl]; int* bd = stackalloc int[nl];
            for (int k = 0; k < nl; k++) { lm[k] = lmul[k]; lo[k] = loff[k]; lp[k] = lpred[k]; lc[k] = lctx[k]; bd[k] = bounds[k]; }
            // the leaf: the number of bounds below the maximum error, counted with vector compares (no data dependent branch)
            bool simd = System.Runtime.Intrinsics.X86.Avx2.IsSupported;
            int nv = (nl + 7) / 8;
            int* bpad = stackalloc int[nv * 8];
            for (int k = 0; k < nv * 8; k++) bpad[k] = k < nl ? bounds[k] : int.MaxValue;
            uint mw0_ = mw0, mw1_ = mw1, mw2_ = mw2, mw3_ = mw3;
            long p1c_ = p1c, p2c_ = p2c, p3a_ = p3a, p3b_ = p3b, p3c_ = p3c, p3d_ = p3d, p3e_ = p3e;
            fixed (Cell* c0 = cells)
            fixed (int* pd = d)
            fixed (uint* div = DivLookup)
            {
                for (int y = 0; y < h; y++)
                {
                    Cell* cur_ = c0 + ((y & 1) != 0 ? 0 : stride);
                    Cell* prev_ = c0 + ((y & 1) != 0 ? stride : 0);
                    int* row = pd + (long)y * w;
                    for (int x = 0; x < w; x++)
                    {
                        long W, N, NW, NE, NN, WW, NEE;
                        if (y >= 2 && x >= 2 && x + 2 < w)
                        {
                            int* q = row + x;
                            W = q[-1]; WW = q[-2];
                            int* n_ = q - w;
                            N = n_[0]; NW = n_[-1]; NE = n_[1]; NEE = n_[2]; NN = n_[-w];
                        }
                        else JxlModularChannelDecoder.Neighbours(d, w, x, y, out W, out N, out NW, out NE, out NN, out WW, out NEE);
                        // prediction (Predict)
                        Cell* cn = prev_ + x;
                        Cell* cne = x < w - 1 ? cn + 1 : cn;
                        Cell* cnw = x > 0 ? cn - 1 : cn;
                        uint w0 = Weight(cn->E0 + cne->E0 + cnw->E0, mw0_, div);
                        uint w1 = Weight(cn->E1 + cne->E1 + cnw->E1, mw1_, div);
                        uint w2 = Weight(cn->E2 + cne->E2 + cnw->E2, mw2_, div);
                        uint w3 = Weight(cn->E3 + cne->E3 + cnw->E3, mw3_, div);
                        long N3 = N << 3, W3 = W << 3, NE3 = NE << 3, NW3 = NW << 3, NN3 = NN << 3;
                        long teW = x == 0 ? 0 : cur_[x - 1].True;
                        long teN = cn->True, teNW = cnw->True, teNE = cne->True;
                        long sumWN = teN + teW;
                        long s0 = W3 + NE3 - N3;
                        long s1 = N3 - ((sumWN + teNE) * p1c_ >> 5);
                        long s2 = W3 - ((sumWN + teNW) * p2c_ >> 5);
                        long s3 = N3 - ((teNW * p3a_ + teN * p3b_ + teNE * p3c_ + (NN3 - N3) * p3d_ + (NW3 - W3) * p3e_) >> 5);
                        uint ws = w0 + w1 + w2 + w3;
                        int lw = 31 - BitOperations.LeadingZeroCount(ws) - 4;
                        w0 >>= lw; w1 >>= lw; w2 >>= lw; w3 >>= lw;
                        uint sum = w0 + w1 + w2 + w3;
                        long sp = (sum >> 1) - 1 + s0 * w0 + s1 * w1 + s2 * w2 + s3 * w3;
                        long pr = (sp * div[sum - 1]) >> 24;
                        {
                            // branch free (data dependent): clamp to the range of W, N, NE when the three errors agree in sign
                            long mx = Max(W3, Max(N3, NE3)), mn = Min(W3, Min(N3, NE3));
                            long cl = Min(Max(pr, mn), mx);
                            long cond = (((teN ^ teW) | (teN ^ teNW)) - 1) >> 63;   // all ones when <= 0
                            pr = (cl & cond) | (pr & ~cond);
                        }
                        long me = teW, ame = Abs(teW);
                        { long a = Abs(teN), m_ = (ame - a) >> 63; me = (teN & m_) | (me & ~m_); ame = (a & m_) | (ame & ~m_); }
                        { long a = Abs(teNW), m_ = (ame - a) >> 63; me = (teNW & m_) | (me & ~m_); ame = (a & m_) | (ame & ~m_); }
                        { long a = Abs(teNE), m_ = (ame - a) >> 63; me = (teNE & m_) | (me & ~m_); }
                        long wpPred = (pr + 3) >> 3;
                        // leaf by the maximum error: the number of bounds below it
                        int mei = (int)me, k;
                        if (simd)
                        {
                            var vm = System.Runtime.Intrinsics.Vector256.Create(mei);
                            k = 0;
                            for (int j = 0; j < nv; j++)
                            {
                                var gt = System.Runtime.Intrinsics.X86.Avx2.CompareGreaterThan(vm, System.Runtime.Intrinsics.Vector256.Load(bpad + 8 * j));
                                k += BitOperations.PopCount((uint)System.Runtime.Intrinsics.X86.Avx.MoveMask(System.Runtime.Intrinsics.Vector256.AsSingle(gt)));
                            }
                        }
                        else
                        {
                            k = 0;
                            while (mei > bd[k]) k++;
                        }
                        uint u = rd.Read(lc[k], ref st, ref buf, ref nbits, ref bp);
                        long r = (long)JxlBitReader.UnpackSigned(u) * lm[k] + lo[k];
                        int pk = lp[k];
                        long v = r + (pk == 6 ? wpPred : JxlModularChannelDecoder.Predict(pk, W, N, NW, NE, NN, WW, NEE, wpPred));
                        int vi = (int)v;
                        row[x] = vi;
                        // Update
                        long v3 = (long)vi << 3;
                        Cell* c = cur_ + x;
                        Cell* ne = prev_ + x + 1;
                        c->True = (int)(pr - v3);
                        uint e;
                        e = (uint)((Abs(s0 - v3) + 3) >> 3); c->E0 = e; ne->E0 += e;
                        e = (uint)((Abs(s1 - v3) + 3) >> 3); c->E1 = e; ne->E1 += e;
                        e = (uint)((Abs(s2 - v3) + 3) >> 3); c->E2 = e; ne->E2 += e;
                        e = (uint)((Abs(s3 - v3) + 3) >> 3); c->E3 = e; ne->E3 += e;
                    }
                    if (rd.OverrunAt(nbits, bp)) throw new ImageFormatException("JPEG XL: unexpected end of modular data");
                }
            }
            rd.Store(new JxlAnsFast.Regs { St = st, Buf = buf, NBits = nbits, P = bp });
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static long Max(long a, long b) { long d = a - b; return a - (d & (d >> 63)); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static long Min(long a, long b) { long d = a - b; return b + (d & (d >> 63)); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe uint Weight(uint x, uint maxWeight, uint* div)
        {
            int shift = 63 - BitOperations.LeadingZeroCount((ulong)x + 1) - 5;
            shift = shift < 0 ? 0 : shift;
            return 4 + ((maxWeight * div[x >> shift]) >> shift);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Update(int x, long value)
        {
            long v3 = value << 3;
            ref Cell c0 = ref MemoryMarshal.GetArrayDataReference(cells);
            ref Cell c = ref Unsafe.Add(ref c0, cur + x);
            ref Cell ne = ref Unsafe.Add(ref c0, prev + x + 1);
            c.True = (int)(pred - v3);
            uint e;
            e = (uint)((Abs(sub0 - v3) + 3) >> 3); c.E0 = e; ne.E0 += e;
            e = (uint)((Abs(sub1 - v3) + 3) >> 3); c.E1 = e; ne.E1 += e;
            e = (uint)((Abs(sub2 - v3) + 3) >> 3); c.E2 = e; ne.E2 += e;
            e = (uint)((Abs(sub3 - v3) + 3) >> 3); c.E3 = e; ne.E3 += e;
        }
    }
}
