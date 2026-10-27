using System;
using System.Runtime.CompilerServices;
using static Aardvark.ImageCodecs.JpegXl.JxlBitReader;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>Hybrid integer configuration: tokens below 2^split_exponent are literal, larger ones carry raw bits.</summary>
    internal struct JxlHybridUint
    {
        public int SplitExponent, MsbInToken, LsbInToken;
        public uint Split;

        public static JxlHybridUint Read(JxlBitReader br, int logAlphaSize)
        {
            var c = new JxlHybridUint();
            c.SplitExponent = (int)br.ReadBits(CeilLog2(logAlphaSize + 1));
            if (c.SplitExponent > logAlphaSize) throw new ImageFormatException("JPEG XL: bad hybrid uint config");
            if (c.SplitExponent != logAlphaSize)
            {
                c.MsbInToken = (int)br.ReadBits(CeilLog2(c.SplitExponent + 1));
                if (c.MsbInToken > c.SplitExponent) throw new ImageFormatException("JPEG XL: bad hybrid uint config");
                c.LsbInToken = (int)br.ReadBits(CeilLog2(c.SplitExponent - c.MsbInToken + 1));
                if (c.MsbInToken + c.LsbInToken > c.SplitExponent) throw new ImageFormatException("JPEG XL: bad hybrid uint config");
            }
            c.Split = 1u << c.SplitExponent;
            return c;
        }

        public static JxlHybridUint Make(int split, int msb, int lsb) => new JxlHybridUint { SplitExponent = split, MsbInToken = msb, LsbInToken = lsb, Split = 1u << split };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Decode(uint token, JxlBitReader br)
        {
            if (token < Split) return token;
            int ml = MsbInToken + LsbInToken;
            int n = SplitExponent - ml + (int)((token - Split) >> ml);
            if (n > 31) throw new ImageFormatException("JPEG XL: hybrid uint too large");
            uint low = token & ((1u << LsbInToken) - 1);
            token >>= LsbInToken;
            uint high = (token & ((1u << MsbInToken) - 1)) | (1u << MsbInToken);
            ulong v = ((((ulong)high << n) | br.ReadBits(n)) << LsbInToken) | low;
            if (v > uint.MaxValue) throw new ImageFormatException("JPEG XL: hybrid uint too large");
            return (uint)v;
        }

        public static int CeilLog2(int x)
        {
            int r = 0;
            while ((1 << r) < x) r++;
            return r;
        }
    }

    /// <summary>One ANS distribution with its alias table (12 bit precision).</summary>
    internal sealed class JxlAnsTable
    {
        public const int LogTabSize = 12, TabSize = 1 << LogTabSize;
        // per alias bucket: cutoff, the alias symbol and its offset, packed for speed
        public ushort[] Freq = Array.Empty<ushort>();     // by symbol (only < 1 << log_alpha_size)
        public ushort[] Cutoff = Array.Empty<ushort>();
        public ushort[] Symbol = Array.Empty<ushort>();
        public ushort[] Offset = Array.Empty<ushort>();
        public int LogBucket;

        static readonly byte[,] LogCountLut = BuildLogCountLut();

        static byte[,] BuildLogCountLut()
        {
            // codes in read order (first bit first) for the log counts 0..13
            string[] codes =
            {
                "10001", "1101", "1111", "1100", "1001", "1110", "001", "010", "101", "011", "000", "100001", "1000000", "1000001",
            };
            var lut = new byte[128, 2];
            for (int v = 0; v < codes.Length; v++)
            {
                string c = codes[v];
                int bits = 0;
                for (int i = 0; i < c.Length; i++) if (c[i] == '1') bits |= 1 << i;
                for (int k = bits; k < 128; k += 1 << c.Length) { lut[k, 0] = (byte)c.Length; lut[k, 1] = (byte)v; }
            }
            return lut;
        }

        static int ReadU8(JxlBitReader br)
        {
            if (!br.ReadBool()) return 0;
            int n = (int)br.ReadBits(3);
            return (1 << n) + (int)br.ReadBits(n);
        }

        public static JxlAnsTable Read(JxlBitReader br, int logAlphaSize)
        {
            int alphaSize = 1 << logAlphaSize;
            var freq = new int[alphaSize];
            if (br.ReadBool())
            {
                // simple: one or two symbols
                if (br.ReadBool())
                {
                    int v1 = ReadU8(br), v2 = ReadU8(br);
                    if (v1 == v2 || v1 >= alphaSize || v2 >= alphaSize) throw new ImageFormatException("JPEG XL: bad ANS distribution");
                    int p = (int)br.ReadBits(LogTabSize);
                    freq[v1] = p; freq[v2] = TabSize - p;
                }
                else
                {
                    int v = ReadU8(br);
                    if (v >= alphaSize) throw new ImageFormatException("JPEG XL: bad ANS distribution");
                    freq[v] = TabSize;
                }
            }
            else if (br.ReadBool())
            {
                // flat
                int n = ReadU8(br) + 1;
                if (n > alphaSize) throw new ImageFormatException("JPEG XL: bad ANS distribution");
                for (int i = 0; i < n; i++) freq[i] = TabSize / n + (i < TabSize % n ? 1 : 0);
            }
            else
            {
                int len = 0;
                while (len < 3 && br.ReadBool()) len++;
                int shift = (int)(br.ReadBits(len) | (1u << len)) - 1;
                if (shift > 13) throw new ImageFormatException("JPEG XL: bad ANS distribution shift");
                int length = ReadU8(br) + 3;
                // D has 1 << log_alphabet_size entries (C.2.5) and the alias mapping of C.2.6 is only defined for
                // alphabet_size <= table_size (it fills cutoffs[alphabet_size .. table_size)); libjxl rejects it too
                if (length > alphaSize) throw new ImageFormatException("JPEG XL: ANS alphabet too large");
                var logCounts = new int[length];
                var same = new int[length];
                int omitLog = -1, omitPos = -1;
                for (int i = 0; i < length; i++)
                {
                    uint p = br.PeekBits(7);
                    br.SkipBits(LogCountLut[p, 0]);
                    logCounts[i] = LogCountLut[p, 1];
                    if (logCounts[i] == 13)
                    {
                        int rle = ReadU8(br);
                        same[i] = rle + 5;
                        i += rle + 3;
                        continue;
                    }
                    if (logCounts[i] > omitLog) { omitLog = logCounts[i]; omitPos = i; }
                }
                if (omitPos < 0 || omitPos + 1 < length && logCounts[omitPos + 1] == 13) throw new ImageFormatException("JPEG XL: bad ANS distribution");
                int total = 0, prev = 0, numSame = 0;
                for (int i = 0; i < length; i++)
                {
                    if (same[i] != 0) { numSame = same[i] - 1; prev = i > 0 ? freq[i - 1] : 0; }
                    if (numSame > 0) { freq[i] = prev; numSame--; }
                    else
                    {
                        int code = logCounts[i];
                        if (i == omitPos || code == 0) continue;
                        if (code == 1) freq[i] = 1;
                        else
                        {
                            int bitcount = Math.Min(code - 1, Math.Max(0, shift - ((LogTabSize - (code - 1)) >> 1)));
                            freq[i] = (1 << (code - 1)) + ((int)br.ReadBits(bitcount) << (code - 1 - bitcount));
                        }
                    }
                    total += freq[i];
                }
                // D[omit_pos] >= 0: the omitted symbol may get probability 0 (libjxl rejects that, the standard does not)
                if (total > TabSize) throw new ImageFormatException("JPEG XL: ANS distribution sums above 4096");
                freq[omitPos] = TabSize - total;
            }
            return Build(freq, logAlphaSize);
        }

        public static JxlAnsTable Build(int[] freq, int logAlphaSize)
        {
            int tableSize = 1 << logAlphaSize;
            int bucket = TabSize >> logAlphaSize;
            var t = new JxlAnsTable
            {
                Freq = new ushort[tableSize], Cutoff = new ushort[tableSize], Symbol = new ushort[tableSize], Offset = new ushort[tableSize],
                LogBucket = LogTabSize - logAlphaSize,
            };
            int single = -1, sum = 0;
            for (int i = 0; i < tableSize; i++)
            {
                t.Freq[i] = (ushort)freq[i];
                sum += freq[i];
                if (freq[i] == TabSize) single = i;
            }
            if (sum != TabSize) throw new ImageFormatException("JPEG XL: ANS distribution does not sum to 4096");
            if (single >= 0)
            {
                for (int i = 0; i < tableSize; i++)
                {
                    t.Symbol[i] = (ushort)single;
                    t.Offset[i] = (ushort)(i * bucket);
                    t.Cutoff[i] = 0;
                }
                return t;
            }
            var cutoffs = new int[tableSize];
            var offsets = new int[tableSize];
            var symbols = new int[tableSize];
            var under = new int[tableSize];
            var over = new int[tableSize];
            int nu = 0, no = 0;
            for (int i = 0; i < tableSize; i++)
            {
                cutoffs[i] = freq[i];
                if (cutoffs[i] > bucket) over[no++] = i;
                else if (cutoffs[i] < bucket) under[nu++] = i;
            }
            while (no > 0)
            {
                if (nu == 0) throw new ImageFormatException("JPEG XL: ANS alias table construction failed");
                int o = over[--no], u = under[--nu];
                int by = bucket - cutoffs[u];
                cutoffs[o] -= by;
                symbols[u] = o;
                offsets[u] = cutoffs[o];
                if (cutoffs[o] < bucket) under[nu++] = o;
                else if (cutoffs[o] > bucket) over[no++] = o;
            }
            for (int i = 0; i < tableSize; i++)
            {
                if (cutoffs[i] == bucket) { symbols[i] = i; offsets[i] = 0; cutoffs[i] = 0; }
                else offsets[i] -= cutoffs[i];
                t.Cutoff[i] = (ushort)cutoffs[i];
                t.Symbol[i] = (ushort)symbols[i];
                t.Offset[i] = (ushort)offsets[i];
            }
            return t;
        }
    }

    /// <summary>
    /// The entropy code of one stream: LZ77 parameters, the context map and per cluster a hybrid uint config with an ANS
    /// distribution or a prefix code. Read once, shared by the symbol readers that decode with it.
    /// </summary>
    internal sealed class JxlEntropyCode
    {
        public bool Lz77;
        public uint Lz77MinSymbol, Lz77MinLength;
        public JxlHybridUint Lz77LenConf;
        public byte[] ContextMap = Array.Empty<byte>();
        public int NumClusters;
        public bool UsePrefix;
        public int LogAlphaSize;
        public JxlHybridUint[] Configs = Array.Empty<JxlHybridUint>();
        public JxlAnsTable[] Ans = Array.Empty<JxlAnsTable>();
        public JxlPrefixCode[] Prefix = Array.Empty<JxlPrefixCode>();
        public int NumContexts;
        /// <summary>True when every context of the stream maps to a single-symbol distribution that consumes no bits.</summary>
        public bool[] IsSingle = Array.Empty<bool>();

        /// <summary>Tables of <see cref="JxlAnsFast"/> on the pinned object heap (built on first use).</summary>
        internal sealed class FastTables
        {
            public ulong[] Ans = null!;
            public byte[] Map = null!;
            public JxlHybridUint[] Cfg = null!;
        }

        FastTables? fast;

        /// <summary>
        /// Per cluster and alias bucket: cutoff (bits 0..11), alias symbol (12..19), alias offset (20..31), the bucket
        /// symbol's frequency (32..44) and the alias symbol's frequency (45..57). Null for prefix codes.
        /// </summary>
        internal FastTables? Fast
        {
            get
            {
                if (UsePrefix || LogAlphaSize > 8) return null;
                if (fast != null) return fast;
                int n = 1 << LogAlphaSize;
                var tab = GC.AllocateUninitializedArray<ulong>(NumClusters * n, pinned: true);
                for (int c = 0; c < NumClusters; c++)
                {
                    var t = Ans[c];
                    for (int i = 0; i < n; i++)
                    {
                        ulong cutoff = t.Cutoff[i], sym = t.Symbol[i], off = t.Offset[i];
                        ulong f0 = t.Freq[i], f1 = t.Freq[t.Symbol[i]];
                        tab[c * n + i] = cutoff | sym << 12 | off << 20 | f0 << 32 | f1 << 45;
                    }
                }
                var map = GC.AllocateUninitializedArray<byte>(ContextMap.Length, pinned: true);
                ContextMap.CopyTo(map, 0);
                var cfg = GC.AllocateUninitializedArray<JxlHybridUint>(Configs.Length, pinned: true);
                Configs.CopyTo(cfg, 0);
                // groups decode in parallel and readers keep raw pointers into these arrays: publish once, so no
                // reader holds tables that only it references (a GC would free them under it)
                var built = new FastTables { Ans = tab, Map = map, Cfg = cfg };
                return System.Threading.Interlocked.CompareExchange(ref fast, built, null) ?? built;
            }
        }

        public static JxlEntropyCode Read(JxlBitReader br, int numDist, int depth = 0, bool lz77Allowed = true)
        {
            if (depth > 2) throw new ImageFormatException("JPEG XL: nested context maps too deep");
            var e = new JxlEntropyCode { NumContexts = numDist };
            e.Lz77 = br.ReadBool();
            if (e.Lz77)
            {
                // C.2.2: the code of a context map of two distributions has lz77.enabled false (the bit is still
                // there; libjxl rejects a 1). This also bounds the nesting to two levels.
                if (!lz77Allowed) throw new ImageFormatException("JPEG XL: LZ77 in the code of a context map of two distributions");
                e.Lz77MinSymbol = br.ReadU32(Val(224), Val(512), Val(4096), BitsOffset(15, 8));
                e.Lz77MinLength = br.ReadU32(Val(3), Val(4), BitsOffset(2, 5), BitsOffset(8, 9));
                e.Lz77LenConf = JxlHybridUint.Read(br, 8);
                numDist++;
            }
            if (numDist > 1) e.ContextMap = ReadContextMap(br, numDist, out e.NumClusters, depth);
            else { e.ContextMap = new byte[1]; e.NumClusters = 1; }
            e.UsePrefix = br.ReadBool();
            e.LogAlphaSize = e.UsePrefix ? 15 : 5 + (int)br.ReadBits(2);
            e.Configs = new JxlHybridUint[e.NumClusters];
            for (int i = 0; i < e.NumClusters; i++) e.Configs[i] = JxlHybridUint.Read(br, e.LogAlphaSize);
            if (e.UsePrefix)
            {
                var counts = new int[e.NumClusters];
                for (int i = 0; i < e.NumClusters; i++)
                {
                    if (br.ReadBool())
                    {
                        int n = (int)br.ReadBits(4);
                        counts[i] = 1 + (1 << n) + (int)br.ReadBits(n);
                        if (counts[i] > 1 << 15) throw new ImageFormatException("JPEG XL: prefix alphabet too large");
                    }
                    else counts[i] = 1;
                }
                e.Prefix = new JxlPrefixCode[e.NumClusters];
                for (int i = 0; i < e.NumClusters; i++) e.Prefix[i] = JxlPrefixCode.Read(br, counts[i]);
            }
            else
            {
                e.Ans = new JxlAnsTable[e.NumClusters];
                for (int i = 0; i < e.NumClusters; i++) e.Ans[i] = JxlAnsTable.Read(br, e.LogAlphaSize);
            }
            br.CheckEnd();
            return e;
        }

        /// <summary>Clustering: maps numDist contexts to clusters (D.3.5 of the standard).</summary>
        public static byte[] ReadContextMap(JxlBitReader br, int numDist, out int numClusters, int depth = 0)
        {
            var map = new byte[numDist];
            if (br.ReadBool())
            {
                // simple
                int nbits = (int)br.ReadBits(2);
                for (int i = 0; i < numDist; i++) map[i] = (byte)br.ReadBits(nbits);
            }
            else
            {
                bool mtf = br.ReadBool();
                var code = Read(br, 1, depth + 1, lz77Allowed: numDist != 2);
                var r = new JxlSymbolReader(code, br, 0);
                for (int i = 0; i < numDist; i++)
                {
                    uint v = r.ReadUint(br, 0);
                    if (v >= 256) throw new ImageFormatException("JPEG XL: context map entry out of range");
                    map[i] = (byte)v;
                }
                r.CheckFinalState();
                if (mtf)
                {
                    Span<byte> order = stackalloc byte[256];
                    for (int i = 0; i < 256; i++) order[i] = (byte)i;
                    for (int i = 0; i < numDist; i++)
                    {
                        int idx = map[i];
                        byte v = order[idx];
                        map[i] = v;
                        for (int k = idx; k > 0; k--) order[k] = order[k - 1];
                        order[0] = v;
                    }
                }
            }
            // C.2.2 states num_clusters < 256; libjxl decodes 256 clusters, and so do we (a cluster id is a byte)
            int max = 0;
            for (int i = 0; i < numDist; i++) max = Math.Max(max, map[i]);
            numClusters = max + 1;
            var seen = new bool[numClusters];
            for (int i = 0; i < numDist; i++) seen[map[i]] = true;
            for (int i = 0; i < numClusters; i++) if (!seen[i]) throw new ImageFormatException("JPEG XL: context map leaves a cluster unused");
            return map;
        }
    }

    /// <summary>A source of entropy coded integers by context (generic parameter of the hot decoding loops).</summary>
    internal interface IJxlSymbols
    {
        uint ReadUint(int ctx);
        bool Overrun { get; }
    }

    /// <summary>The general symbol reader (prefix codes, LZ77) as an <see cref="IJxlSymbols"/>.</summary>
    internal readonly struct JxlSlowSymbols : IJxlSymbols
    {
        readonly JxlSymbolReader r;
        readonly JxlBitReader br;
        public JxlSlowSymbols(JxlSymbolReader r, JxlBitReader br) { this.r = r; this.br = br; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint ReadUint(int ctx) => r.ReadUint(br, ctx);
        public bool Overrun => br.Overrun;
    }

    /// <summary>
    /// ANS without LZ77 with the state in a value: the packed alias tables of <see cref="JxlEntropyCode.Fast"/>, the bit
    /// reader as a <see cref="JxlBitState"/>. Obtained from <see cref="JxlSymbolReader.BeginFast"/> and handed back with
    /// <see cref="JxlSymbolReader.EndFast"/>.
    /// </summary>
    internal unsafe struct JxlAnsFast : IJxlSymbols
    {
        public JxlBitState Bits;
        public uint State;
        public ulong* Tab;
        public byte* Map;
        public JxlHybridUint* Cfg;
        public int LogBucket, LogAlpha, MapLength;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint ReadUint(int ctx)
        {
            uint st = State; ulong buf = Bits.Buf; int nbits = Bits.NBits; byte* p = Bits.P;
            uint v = Read(ctx, ref st, ref buf, ref nbits, ref p);
            State = st; Bits.Buf = buf; Bits.NBits = nbits; Bits.P = p;
            return v;
        }

        public bool Overrun => Bits.Overrun;

        /// <summary>
        /// The decoder state split into locals for loops that keep it in registers (<see cref="Read"/>); written back with
        /// <see cref="Store"/>.
        /// </summary>
        public struct Regs
        {
            public uint St;
            public ulong Buf;
            public int NBits;
            public byte* P;
        }

        public Regs Load() => new() { St = State, Buf = Bits.Buf, NBits = Bits.NBits, P = Bits.P };

        public void Store(in Regs r) { State = r.St; Bits.Buf = r.Buf; Bits.NBits = r.NBits; Bits.P = r.P; }

        /// <summary><see cref="ReadUint"/> on separate state locals (st, buf, nbits, p) that stay in registers once inlined.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly uint Read(int ctx, ref uint st, ref ulong buf, ref int nbits, ref byte* p)
        {
            if ((uint)ctx >= (uint)MapLength) ThrowContext();
            uint cl = Map[ctx];
            uint s = st;
            uint idx = s & (JxlAnsTable.TabSize - 1);
            uint i = idx >> LogBucket;
            uint pos = idx & ((1u << LogBucket) - 1);
            ulong e = Tab[(cl << LogAlpha) + i];
            // branch free: lo = all ones when pos is below the cutoff (the bucket's own symbol), else the alias
            uint lo = (uint)((int)(pos - ((uint)e & 0xFFF)) >> 31);
            uint sym = ((uint)(e >> 12) & 0xFF & ~lo) | (i & lo);
            uint off = ((((uint)(e >> 20) & 0xFFF) + pos) & ~lo) | (pos & lo);
            uint freq = (uint)(e >> (45 - (int)(13 & lo))) & 0x1FFF;
            s = freq * (s >> JxlAnsTable.LogTabSize) + off;
            Refill(ref buf, ref nbits, ref p);   // unconditional (a branch on nbits mispredicts): >= 56 bits
            bool rn = s < (1u << 16);
            uint m = (uint)-(int)Unsafe.As<bool, byte>(ref rn);
            int sh = (int)(16 & m);
            st = (((s << 16) | (uint)(buf & 0xFFFF)) & m) | (s & ~m);
            buf >>= sh; nbits -= sh;
            JxlHybridUint* c = Cfg + cl;
            if (sym < c->Split) return sym;
            // hybrid uint: n raw bits between the token's msb and lsb parts
            int ml = c->MsbInToken + c->LsbInToken;
            int n = c->SplitExponent - ml + (int)((sym - c->Split) >> ml);
            if ((uint)n > 31) ThrowHybrid();
            int lsb = c->LsbInToken;
            uint low = sym & ((1u << lsb) - 1);
            uint high = ((sym >> lsb) & ((1u << c->MsbInToken) - 1)) | (1u << c->MsbInToken);
            ulong raw = buf & ((1UL << n) - 1);
            buf >>= n; nbits -= n;
            ulong v = ((((ulong)high << n) | raw) << lsb) | low;
            if (v > uint.MaxValue) ThrowHybrid();
            return (uint)v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly void Refill(ref ulong buf, ref int nbits, ref byte* p)
        {
            if (p + 8 <= Bits.End)
            {
                buf |= Unsafe.ReadUnaligned<ulong>(p) << nbits;
                int take = (63 - nbits) >> 3;
                p += take;
                nbits += take << 3;
            }
            else
            {
                var s = Bits;
                s.Buf = buf; s.NBits = nbits; s.P = p;
                s = RefillSlowCopy(s);
                buf = s.Buf; nbits = s.NBits; p = s.P;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static JxlBitState RefillSlowCopy(JxlBitState s)
        {
            while (s.NBits <= 56)
            {
                if (s.P < s.End) s.Buf |= (ulong)*s.P << s.NBits;
                s.P++;
                s.NBits += 8;
                if (s.P > s.End + 64) throw new ImageFormatException("JPEG XL: unexpected end of data");
            }
            return s;
        }

        /// <summary>More bits consumed than the section holds, for state locals.</summary>
        public readonly bool OverrunAt(int nbits, byte* p) => (p - Bits.End) * 8 > nbits;

        static void ThrowHybrid() => throw new ImageFormatException("JPEG XL: hybrid uint too large");

        static void ThrowContext() => throw new ImageFormatException("JPEG XL: entropy context out of range");
    }

    /// <summary>Decoding state of one entropy coded stream (ANS state, LZ77 window).</summary>
    internal sealed class JxlSymbolReader
    {
        readonly JxlEntropyCode code;
        uint state;
        readonly bool ans;
        // LZ77
        readonly bool lz77;
        uint[]? window;
        const int WindowSize = 1 << 20, WindowMask = WindowSize - 1;
        long numToCopy;
        int copyPos, numDecoded;
        readonly int distMultiplier;
        readonly int lzCtx;

        public JxlSymbolReader(JxlEntropyCode code, JxlBitReader br, int distMultiplier)
        {
            this.code = code;
            ans = !code.UsePrefix;
            if (ans) state = br.ReadBits(32);
            lz77 = code.Lz77;
            this.distMultiplier = distMultiplier;
            lzCtx = code.ContextMap.Length - 1;
        }

        public bool UsesLz77 => lz77;

        /// <summary>Whether <see cref="BeginFast"/> can be used (ANS, no LZ77, no copy in progress).</summary>
        public bool CanFast => ans && !lz77 && code.Fast != null;

        /// <summary>The fast state for a hot loop; basePtr pins br.Data. Hand it back with <see cref="EndFast"/>.</summary>
        public unsafe JxlAnsFast BeginFast(JxlBitReader br, byte* basePtr)
        {
            var f = code.Fast!;
            return new JxlAnsFast
            {
                Bits = br.Export(basePtr), State = state,
                Tab = (ulong*)Unsafe.AsPointer(ref f.Ans[0]), Map = (byte*)Unsafe.AsPointer(ref f.Map[0]),
                Cfg = (JxlHybridUint*)Unsafe.AsPointer(ref f.Cfg[0]),
                LogAlpha = code.LogAlphaSize, LogBucket = JxlAnsTable.LogTabSize - code.LogAlphaSize, MapLength = f.Map.Length,
            };
        }

        public unsafe void EndFast(in JxlAnsFast f, JxlBitReader br, byte* basePtr)
        {
            br.Import(f.Bits, basePtr);
            state = f.State;
        }

        public void CheckFinalState()
        {
            if (ans && state != 0x130000) throw new ImageFormatException("JPEG XL: ANS stream does not end in the final state");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        uint ReadToken(JxlBitReader br, int cluster)
        {
            if (ans)
            {
                var t = code.Ans[cluster];
                uint idx = state & (JxlAnsTable.TabSize - 1);
                int i = (int)(idx >> t.LogBucket);
                uint pos = idx & ((1u << t.LogBucket) - 1);
                uint sym, off;
                if (pos >= t.Cutoff[i]) { sym = t.Symbol[i]; off = t.Offset[i] + pos; }
                else { sym = (uint)i; off = pos; }
                state = t.Freq[sym] * (state >> JxlAnsTable.LogTabSize) + off;
                if (state < (1u << 16)) state = (state << 16) | br.ReadBits(16);
                return sym;
            }
            return (uint)code.Prefix[cluster].Decode(br);
        }

        /// <summary>Reads one integer in context ctx (hybrid uint plus LZ77).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint ReadUint(JxlBitReader br, int ctx)
        {
            if (!lz77)
            {
                int cl = code.ContextMap[ctx];
                uint tok = ReadToken(br, cl);
                return code.Configs[cl].Decode(tok, br);
            }
            return ReadUintLz77(br, ctx);
        }

        uint ReadUintLz77(JxlBitReader br, int ctx)
        {
            window ??= new uint[WindowSize];
            if (numToCopy > 0)
            {
                uint r = window[copyPos++ & WindowMask];
                numToCopy--;
                window[numDecoded++ & WindowMask] = r;
                return r;
            }
            int cl = code.ContextMap[ctx];
            uint token = ReadToken(br, cl);
            if (token >= code.Lz77MinSymbol)
            {
                // any length is valid (a copy allocates nothing; what the stream does not ask for is never produced)
                numToCopy = (long)code.Lz77LenConf.Decode(token - code.Lz77MinSymbol, br) + code.Lz77MinLength;
                int lc = code.ContextMap[lzCtx];
                uint dtok = ReadToken(br, lc);
                uint distance = code.Configs[lc].Decode(dtok, br);
                long d;
                if (distMultiplier == 0) d = (long)distance + 1;
                else if (distance < 120)
                {
                    d = SpecialDistances[2 * distance] + (long)distMultiplier * SpecialDistances[2 * distance + 1];
                    if (d < 1) d = 1;
                }
                else d = (long)distance - 119;
                if (d > numDecoded) d = numDecoded;
                if (d > WindowSize) d = WindowSize;
                // d == 0 (nothing decoded yet) copies from the window as initialized: zeros
                copyPos = numDecoded - (int)d;
                uint r = window[copyPos++ & WindowMask];
                numToCopy--;
                window[numDecoded++ & WindowMask] = r;
                return r;
            }
            uint v = code.Configs[cl].Decode(token, br);
            window[numDecoded++ & WindowMask] = v;
            return v;
        }

        /// <summary>The 120 special distances (dx, dy) of LZ77 in 2D images; distance = dx + multiplier * dy.</summary>
        static readonly sbyte[] SpecialDistances =
        {
            0, 1, 1, 0, 1, 1, -1, 1, 0, 2, 2, 0, 1, 2, -1, 2, 2, 1, -2, 1, 2, 2, -2, 2, 0, 3, 3, 0, 1, 3, -1, 3, 3, 1, -3, 1, 2, 3, -2, 3, 3, 2,
            -3, 2, 0, 4, 4, 0, 1, 4, -1, 4, 4, 1, -4, 1, 3, 3, -3, 3, 2, 4, -2, 4, 4, 2, -4, 2, 0, 5, 3, 4, -3, 4, 4, 3, -4, 3, 5, 0, 1, 5, -1, 5,
            5, 1, -5, 1, 2, 5, -2, 5, 5, 2, -5, 2, 4, 4, -4, 4, 3, 5, -3, 5, 5, 3, -5, 3, 0, 6, 6, 0, 1, 6, -1, 6, 6, 1, -6, 1, 2, 6, -2, 6, 6, 2,
            -6, 2, 4, 5, -4, 5, 5, 4, -5, 4, 3, 6, -3, 6, 6, 3, -6, 3, 0, 7, 7, 0, 1, 7, -1, 7, 5, 5, -5, 5, 7, 1, -7, 1, 4, 6, -4, 6, 6, 4, -6, 4,
            2, 7, -2, 7, 7, 2, -7, 2, 3, 7, -3, 7, 7, 3, -7, 3, 5, 6, -5, 6, 6, 5, -6, 5, 8, 0, 4, 7, -4, 7, 7, 4, -7, 4, 8, 1, 8, 2, 6, 6, -6, 6,
            8, 3, 5, 7, -5, 7, 7, 5, -7, 5, 8, 4, 6, 7, -6, 7, 7, 6, -7, 6, 8, 5, 7, 7, -7, 7, 8, 6, 8, 7,
        };

        /// <summary>Single-context helper used by headers: U(ctx) with the given context.</summary>
        public int ReadSigned(JxlBitReader br, int ctx) => UnpackSigned(ReadUint(br, ctx));
    }
}
