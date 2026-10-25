using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Aardvark.ImageCodecs
{
    /// <summary>LSB-first bit writer of the lossless bitstream.</summary>
    internal sealed class Vp8lBitWriter
    {
        byte[] _buf = new byte[4096];
        int _pos;
        ulong _acc;
        int _n;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Put(uint value, int bits)
        {
            _acc |= (ulong)value << _n;
            _n += bits;
            if (_n >= 32) Flush32();
        }

        void Flush32()
        {
            if (_pos + 4 > _buf.Length) Array.Resize(ref _buf, _buf.Length * 2);
            BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(_pos), (uint)_acc);
            _pos += 4;
            _acc >>= 32;
            _n -= 32;
        }

        public long BitCount => (long)_pos * 8 + _n;

        /// <summary>Appends all bits of another writer.</summary>
        public void Append(Vp8lBitWriter other)
        {
            for (int i = 0; i < other._pos; i += 4) Put(BinaryPrimitives.ReadUInt32LittleEndian(other._buf.AsSpan(i)), 32);
            if (other._n > 0) Put((uint)(other._acc & ((1UL << other._n) - 1)), other._n);
        }

        public byte[] ToArray()
        {
            int bytes = (_n + 7) >> 3;
            var res = new byte[_pos + bytes];
            Buffer.BlockCopy(_buf, 0, res, 0, _pos);
            ulong a = _acc;
            for (int i = 0; i < bytes; i++) { res[_pos + i] = (byte)a; a >>= 8; }
            return res;
        }
    }

    /// <summary>
    /// Encoder for the VP8L (WebP lossless) bitstream: subtract-green, predictor and cross-color transforms chosen per
    /// block by entropy, color indexing with pixel bundling, LZ77 with the 2-D distance codes, color cache, clustered
    /// prefix codes (entropy image) and length limited Huffman codes.
    /// </summary>
    internal sealed unsafe class Vp8lEncoder
    {
        const int NumLiteral = 256, NumLength = 24, NumDistance = 40;
        const int MaxLength = 4095, Window = (1 << 20) - 121;     // a match packs into 12 + 20 bits
        const int MaxCacheBits = 10;
        const int MaxEntropyBlocks = 2600;

        readonly int _method;
        /// <summary>Checked between the phases and every <see cref="CancelStep"/> positions of the long passes.</summary>
        readonly System.Threading.CancellationToken _cancel;
        const int CancelStep = 1 << 16;

        Vp8lEncoder(int method, System.Threading.CancellationToken cancel) { _method = method; _cancel = cancel; }

        /// <summary>Encodes ARGB pixels as a complete VP8L chunk payload (signature and size header included).</summary>
        public static byte[] Encode(uint[] argb, int width, int height, bool alphaHint, int method, System.Threading.CancellationToken cancel = default)
        {
            var enc = new Vp8lEncoder(Math.Clamp(method, 0, 6), cancel);
            var bw = enc.EncodeBest(argb, width, height, true, w =>
            {
                w.Put(0x2f, 8);
                w.Put((uint)width - 1, 14);
                w.Put((uint)height - 1, 14);
                w.Put(alphaHint ? 1u : 0u, 1);
                w.Put(0, 3);
            });
            return bw.ToArray();
        }

        /// <summary>Encodes a header-less image stream; only the green channel matters (ALPH chunk payload).</summary>
        public static byte[] EncodeHeaderless(uint[] argb, int width, int height, int method, System.Threading.CancellationToken cancel = default)
        {
            var enc = new Vp8lEncoder(Math.Clamp(method, 0, 6), cancel);
            return enc.EncodeBest(argb, width, height, false, _ => { }).ToArray();
        }

        struct Config
        {
            public bool Palette, SubGreen, Predictor, CrossColor;
            /// <summary>log2 of the block size of the predictor and cross-color transforms, 0 = by image size.</summary>
            public int Bits;
        }

        Vp8lBitWriter EncodeBest(uint[] argb, int w, int h, bool color, Action<Vp8lBitWriter> header)
        {
            var palette = FindPalette(argb);
            var configs = new List<Config>();
            var spatial = new Config { SubGreen = color, Predictor = true, CrossColor = color && _method >= 2 };
            if (palette == null) configs.Add(spatial);
            else if (palette.Length <= 16)
            {
                // few colors: several pixels go into one symbol, hard to beat
                configs.Add(new Config { Palette = true });
                if (_method >= 6 && palette.Length > 2) configs.Add(spatial);
            }
            else
            {
                // Indices as they are suit drawings; smooth images with few colors (gray photographs) want prediction.
                // Rank the three ways by the entropy of their symbols and try the most promising one(s).
                EstimatePaletteModes(argb, palette, out double plain, out double predicted, out double direct);
                var ranked = new List<(double cost, Config cfg)>
                {
                    (plain, new Config { Palette = true }),
                    (predicted, new Config { Palette = true, Predictor = true }),
                    (direct, spatial),
                };
                if (_method >= 1)
                {
                    // a quick trial encode says more than the entropy of the symbols: it sees what LZ77 does
                    var quick = new Vp8lEncoder(0, _cancel);
                    for (int i = 0; i < ranked.Count; i++)
                    {
                        var trial = new Vp8lBitWriter();
                        var cfg = ranked[i].cfg;
                        quick.EncodeStream(trial, cfg.Palette ? argb : (uint[])argb.Clone(), w, h, cfg, palette);
                        ranked[i] = (trial.BitCount, cfg);
                    }
                }
                ranked.Sort((x, y) => x.cost.CompareTo(y.cost));
                int tries = _method >= 6 ? 3 : _method >= 5 ? 2 : 1;
                if (_method == 4 && ranked[1].cost < ranked[0].cost * 1.1) tries = 2;
                for (int i = 0; i < tries; i++) configs.Add(ranked[i].cfg);
            }
            if (_method >= 6 && (palette == null || configs.Contains(spatial)))
            {
                // finer and coarser blocks for the predictors
                int std = (long)w * h > 1 << 22 ? 5 : 4;
                var v = spatial;
                v.Bits = std - 1; configs.Add(v);
                v.Bits = std + 1; configs.Add(v);
            }
            if (_method >= 6 && color && palette == null)
            {
                configs.Add(new Config { SubGreen = true });
                configs.Add(new Config { Predictor = true });
                configs.Add(new Config());
            }

            Vp8lBitWriter? best = null;
            foreach (var cfg in configs)
            {
                _cancel.ThrowIfCancellationRequested();
                var bw = new Vp8lBitWriter();
                header(bw);
                // the transforms work in place; the palette path builds its own index image
                EncodeStream(bw, configs.Count > 1 && !cfg.Palette ? (uint[])argb.Clone() : argb, w, h, cfg, palette);
                if (best == null || bw.BitCount < best.BitCount) best = bw;
            }
            return best!;
        }

        // ------------------------------------------------------------------ entropy helpers

        static readonly double[] XLogXTable = BuildXLogX();

        static double[] BuildXLogX()
        {
            var t = new double[1 << 14];
            for (int i = 2; i < t.Length; i++) t[i] = i * Math.Log2(i);
            return t;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double XLogX(int v) => (uint)v < (uint)XLogXTable.Length ? XLogXTable[v] : v * Math.Log2(v);

        /// <summary>Bits needed for the symbols of a histogram at their entropy.</summary>
        static double EntropyBits(ReadOnlySpan<int> h)
        {
            long n = 0;
            double s = 0;
            for (int i = 0; i < h.Length; i++)
            {
                int c = h[i];
                if (c == 0) continue;
                n += c; s += XLogX(c);
            }
            return n == 0 ? 0 : n * Math.Log2(n) - s;
        }

        /// <summary>A guess of what it costs to transmit a code with nz used symbols in zr separate runs of unused ones.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double TableCost(int nz, int zeroRuns) => nz == 0 ? 4 : nz == 1 ? 12 : nz == 2 ? 20 : 36 + 3.4 * nz + 7 * zeroRuns;

        /// <summary>Entropy plus the cost of the code itself.</summary>
        static double CodeCost(ReadOnlySpan<int> h)
        {
            long n = 0;
            double s = 0;
            int nz = 0, zr = 0;
            bool inZeros = false;
            for (int i = 0; i < h.Length; i++)
            {
                int c = h[i];
                if (c == 0) { if (!inZeros) { zr++; inZeros = true; } continue; }
                inZeros = false;
                n += c; s += XLogX(c); nz++;
            }
            if (nz <= 1) return TableCost(nz, zr);
            return n * Math.Log2(n) - s + TableCost(nz, zr);
        }

        // ------------------------------------------------------------------ Huffman codes

        /// <summary>Code lengths of a Huffman code limited to maxLen bits (0 for unused symbols).</summary>
        internal static void BuildLengths(ReadOnlySpan<int> hist, Span<byte> lens, int maxLen)
        {
            lens.Clear();
            int n = 0;
            for (int i = 0; i < hist.Length; i++) if (hist[i] > 0) n++;
            if (n == 0) return;
            if (n == 1)
            {
                for (int i = 0; i < hist.Length; i++) if (hist[i] > 0) lens[i] = 1;
                return;
            }
            var sym = new int[n];
            var cnt = new long[2 * n];
            var parent = new int[2 * n];
            var key = new long[n];
            // Raising the small counts flattens the tree until it fits the length limit.
            for (long min = 1; ; min *= 2)
            {
                int k = 0;
                for (int i = 0; i < hist.Length; i++)
                    if (hist[i] > 0) { key[k] = Math.Max(hist[i], min); sym[k] = i; k++; }
                Array.Sort(key, sym);
                for (int i = 0; i < n; i++) cnt[i] = key[i];
                // two queues: leaves [0, n) and internal nodes [n, 2n - 1), both ascending
                int leaf = 0, inner = n, next = n;
                while (next < 2 * n - 1)
                {
                    long sum = 0;
                    for (int j = 0; j < 2; j++)
                    {
                        int pick = leaf < n && (inner >= next || cnt[leaf] <= cnt[inner]) ? leaf++ : inner++;
                        sum += cnt[pick];
                        parent[pick] = next;
                    }
                    cnt[next++] = sum;
                }
                int deepest = 0;
                parent[2 * n - 2] = -1;
                // the depth of an internal node is known before its children are visited (children come first in the array)
                var depth = new int[2 * n];
                for (int i = 2 * n - 3; i >= 0; i--) depth[i] = depth[parent[i]] + 1;
                for (int i = 0; i < n; i++) if (depth[i] > deepest) deepest = depth[i];
                if (deepest <= maxLen)
                {
                    for (int i = 0; i < n; i++) lens[sym[i]] = (byte)depth[i];
                    return;
                }
            }
        }

        /// <summary>Canonical codes for the lengths, bit reversed so that they can be written LSB first.</summary>
        internal static void BuildCodes(ReadOnlySpan<byte> lens, Span<ushort> codes)
        {
            Span<int> count = stackalloc int[16];
            count.Clear();
            for (int i = 0; i < lens.Length; i++) count[lens[i]]++;
            count[0] = 0;
            Span<uint> next = stackalloc uint[16];
            uint code = 0;
            for (int l = 1; l <= 15; l++) { code = (code + (uint)count[l - 1]) << 1; next[l] = code; }
            for (int i = 0; i < lens.Length; i++)
            {
                int l = lens[i];
                if (l == 0) { codes[i] = 0; continue; }
                uint c = next[l]++, r = 0;
                for (int b = 0; b < l; b++) { r = (r << 1) | (c & 1); c >>= 1; }
                codes[i] = (ushort)r;
            }
        }

        static ReadOnlySpan<byte> CodeLengthOrder => new byte[19] { 17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

        /// <summary>
        /// Writes the code lengths of one prefix code and fixes them up for writing symbols: a code with a single symbol
        /// takes no bits, so its length becomes 0.
        /// </summary>
        static void WriteCode(Vp8lBitWriter bw, Span<byte> lens)
        {
            int n = 0, s0 = 0, s1 = 0;
            for (int i = 0; i < lens.Length; i++)
                if (lens[i] != 0) { if (n == 0) s0 = i; else if (n == 1) s1 = i; n++; }
            if (n == 0)
            {
                bw.Put(1, 4);       // simple code, one symbol, 1 bit: symbol 0
                return;
            }
            if (n <= 2 && s0 < 256 && s1 < 256)
            {
                bw.Put(1, 1);
                bw.Put((uint)n - 1, 1);
                if (s0 < 2) { bw.Put(0, 1); bw.Put((uint)s0, 1); }
                else { bw.Put(1, 1); bw.Put((uint)s0, 8); }
                if (n == 2) bw.Put((uint)s1, 8);
                if (n == 1) lens[s0] = 0;
                return;
            }

            // run length tokens: 0..15 literal lengths, 16 repeat previous non-zero, 17 / 18 zero runs
            Span<byte> tok = lens.Length <= 512 ? stackalloc byte[lens.Length] : new byte[lens.Length];
            Span<byte> extra = lens.Length <= 512 ? stackalloc byte[lens.Length] : new byte[lens.Length];
            int nt = 0, prev = 8;
            for (int i = 0; i < lens.Length;)
            {
                int v = lens[i], run = 1;
                while (i + run < lens.Length && lens[i + run] == v) run++;
                i += run;
                if (v == 0)
                {
                    while (run >= 11) { int r = Math.Min(run, 138); tok[nt] = 18; extra[nt++] = (byte)(r - 11); run -= r; }
                    if (run >= 3) { tok[nt] = 17; extra[nt++] = (byte)(run - 3); run = 0; }
                    while (run-- > 0) { tok[nt] = 0; extra[nt++] = 0; }
                }
                else
                {
                    if (v != prev) { tok[nt] = (byte)v; extra[nt++] = 0; run--; prev = v; }
                    while (run >= 3) { int r = Math.Min(run, 6); tok[nt] = 16; extra[nt++] = (byte)(r - 3); run -= r; }
                    while (run-- > 0) { tok[nt] = (byte)v; extra[nt++] = 0; }
                }
            }
            Span<int> hist = stackalloc int[19];
            hist.Clear();
            for (int i = 0; i < nt; i++) hist[tok[i]]++;
            Span<byte> clLens = stackalloc byte[19];
            BuildLengths(hist, clLens, 7);
            Span<ushort> clCodes = stackalloc ushort[19];
            BuildCodes(clLens, clCodes);
            int clUsed = 0;
            for (int i = 0; i < 19; i++) if (clLens[i] != 0) clUsed++;

            bw.Put(0, 1);
            int numCl = 19;
            while (numCl > 4 && clLens[CodeLengthOrder[numCl - 1]] == 0) numCl--;
            bw.Put((uint)numCl - 4, 4);
            for (int i = 0; i < numCl; i++) bw.Put(clLens[CodeLengthOrder[i]], 3);

            // trailing zero tokens need not be sent when the token count is given
            int trimmed = nt;
            int saved = 0;
            while (trimmed > 2 && (tok[trimmed - 1] == 0 || tok[trimmed - 1] >= 17))
            {
                int t = tok[trimmed - 1];
                saved += (clUsed > 1 ? clLens[t] : 0) + (t == 17 ? 3 : t == 18 ? 7 : 0);
                trimmed--;
            }
            if (trimmed < nt && saved > 12)
            {
                bw.Put(1, 1);
                int nbits = trimmed == 2 ? 0 : BitOperations.Log2((uint)(trimmed - 2));
                int pairs = nbits / 2 + 1;
                bw.Put((uint)pairs - 1, 3);
                bw.Put((uint)trimmed - 2, pairs * 2);
            }
            else
            {
                bw.Put(0, 1);
                trimmed = nt;
            }
            for (int i = 0; i < trimmed; i++)
            {
                int t = tok[i];
                if (clUsed > 1) bw.Put(clCodes[t], clLens[t]);
                if (t == 16) bw.Put(extra[i], 2);
                else if (t == 17) bw.Put(extra[i], 3);
                else if (t == 18) bw.Put(extra[i], 7);
            }
            if (n == 1) lens[s0] = 0;
        }

        // ------------------------------------------------------------------ LZ77

        /// <summary>The symbol stream: literals (Len 0, Val = ARGB) and copies (Len = length, Val = pixel distance).</summary>
        sealed class Tokens
        {
            public uint[] Val;
            public ushort[] Len;
            public int Count;

            public Tokens(int capacity)
            {
                Val = new uint[capacity]; Len = new ushort[capacity];
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Add(uint val, int len)
            {
                if (Count == Val.Length)
                {
                    Array.Resize(ref Val, Count * 2);
                    Array.Resize(ref Len, Count * 2);
                }
                Val[Count] = val; Len[Count++] = (ushort)len;
            }
        }

        static readonly byte[] PlaneCodeLut = BuildPlaneLut();

        static byte[] BuildPlaneLut()
        {
            // inverse of the decoder's distance map: index is y << 4 | (8 - x)
            var map = new byte[120]
            {
                0x18, 0x07, 0x17, 0x19, 0x28, 0x06, 0x27, 0x29, 0x16, 0x1a, 0x26, 0x2a, 0x38, 0x05, 0x37, 0x39, 0x15, 0x1b, 0x36, 0x3a,
                0x25, 0x2b, 0x48, 0x04, 0x47, 0x49, 0x14, 0x1c, 0x35, 0x3b, 0x46, 0x4a, 0x24, 0x2c, 0x58, 0x45, 0x4b, 0x34, 0x3c, 0x03,
                0x57, 0x59, 0x13, 0x1d, 0x56, 0x5a, 0x23, 0x2d, 0x44, 0x4c, 0x55, 0x5b, 0x33, 0x3d, 0x68, 0x02, 0x67, 0x69, 0x12, 0x1e,
                0x66, 0x6a, 0x22, 0x2e, 0x54, 0x5c, 0x43, 0x4d, 0x65, 0x6b, 0x32, 0x3e, 0x78, 0x01, 0x77, 0x79, 0x53, 0x5d, 0x11, 0x1f,
                0x64, 0x6c, 0x42, 0x4e, 0x76, 0x7a, 0x21, 0x2f, 0x75, 0x7b, 0x31, 0x3f, 0x63, 0x6d, 0x52, 0x5e, 0x00, 0x74, 0x7c, 0x41,
                0x4f, 0x10, 0x20, 0x62, 0x6e, 0x30, 0x73, 0x7d, 0x51, 0x5f, 0x40, 0x72, 0x7e, 0x61, 0x6f, 0x50, 0x71, 0x7f, 0x60, 0x70,
            };
            var lut = new byte[128];
            for (int i = 0; i < 120; i++) lut[map[i]] = (byte)(i + 1);
            return lut;
        }

        /// <summary>The distance code for a pixel distance: 1..120 for the 2-D neighborhood, else distance + 120.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int DistanceCode(int xsize, int dist)
        {
            int yo = dist / xsize, xo = dist - yo * xsize;
            if (xo <= 8 && yo < 8) return PlaneCodeLut[yo * 16 + 8 - xo];
            if (xo > xsize - 8 && yo < 7) return PlaneCodeLut[(yo + 1) * 16 + 8 + (xsize - xo)];
            return dist + 120;
        }

        /// <summary>Splits a length or distance code (>= 1) into its prefix symbol and extra bits.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int PrefixEncode(int v, out int extraBits, out int extra)
        {
            if (v <= 4) { extraBits = 0; extra = 0; return v - 1; }
            v--;
            int hi = BitOperations.Log2((uint)v);
            int second = (v >> (hi - 1)) & 1;
            extraBits = hi - 1;
            extra = v & ((1 << extraBits) - 1);
            return 2 * hi + second;
        }

        /// <summary>Bit costs of the symbols under the statistics of a first parse; drives the optimal parse.</summary>
        sealed class CostModel
        {
            public readonly float[] Literal = new float[4 * 256];      // green, red, blue, alpha
            public readonly float[] Length = new float[MaxLength + 1];
            public readonly float[] Distance = new float[NumDistance];

            static void Fill(ReadOnlySpan<int> h, Span<float> cost)
            {
                long n = 0;
                for (int i = 0; i < h.Length; i++) n += h[i];
                double ln = Math.Log2(n + 1);
                for (int i = 0; i < cost.Length; i++)
                    cost[i] = (float)(h[i] == 0 ? ln + 2 : ln - Math.Log2(h[i]));
            }

            public CostModel(int[] h, Layout lay)
            {
                Span<float> green = stackalloc float[NumLiteral + NumLength];
                Fill(h.AsSpan(0, lay.Green), green);
                green.Slice(0, 256).CopyTo(Literal);
                Fill(h.AsSpan(lay.Red, 256), Literal.AsSpan(256, 256));
                Fill(h.AsSpan(lay.Blue, 256), Literal.AsSpan(512, 256));
                Fill(h.AsSpan(lay.Alpha, 256), Literal.AsSpan(768, 256));
                for (int l = 1; l <= MaxLength; l++)
                    Length[l] = green[NumLiteral + PrefixEncode(l, out int eb, out _)] + eb;
                Fill(h.AsSpan(lay.Dist, NumDistance), Distance);
                for (int i = 4; i < NumDistance; i++) Distance[i] += (i - 2) >> 1;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public float LiteralCost(uint v)
                => Literal[(v >> 8) & 0xff] + Literal[256 + ((v >> 16) & 0xff)] + Literal[512 + (v & 0xff)] + Literal[768 + (v >> 24)];
        }

        /// <summary>
        /// LZ77 parse with a hash chain over pixel pairs. The first pass is greedy with lazy evaluation and only takes
        /// a match that is cheaper than its pixels as literals; with <paramref name="optimal"/> the statistics of that
        /// pass then price every symbol and the cheapest path through all matches is taken.
        /// </summary>
        Tokens BackwardReferences(uint[] px, int n, int xsize, int maxIter, bool lazy, bool optimal)
        {
            var tokens = new Tokens(Math.Max(16, n / 4));
            if (n < 4 || maxIter == 0)
            {
                for (int i = 0; i < n; i++) tokens.Add(px[i], 0);
                return tokens;
            }
            // what the pixels cost without any match
            var lay = new Layout(0);
            var lit = new int[lay.Total];
            for (int i = 0; i < n; i++)
            {
                uint v = px[i];
                lit[(v >> 8) & 0xff]++; lit[lay.Red + (int)((v >> 16) & 0xff)]++; lit[lay.Blue + (int)(v & 0xff)]++; lit[lay.Alpha + (int)(v >> 24)]++;
            }
            var model = new CostModel(lit, lay);

            // few positions per bucket: every step along a chain is a cache miss
            int hashBits = Math.Clamp(BitOperations.Log2((uint)n) + 1, 8, 22);
            var head = new int[1 << hashBits];
            Array.Fill(head, -1);
            var chain = new int[n];
            int hashShift = 32 - hashBits;
            int inserted = 0;       // positions below this are in the chain
            uint[]? matches = optimal ? new uint[n] : null;
            var runs = new ushort[n];       // number of pixels from here on with the same color, capped
            runs[n - 1] = 1;
            for (int i = n - 2; i >= 0; i--) runs[i] = px[i] == px[i + 1] ? (ushort)Math.Min(runs[i + 1] + 1, MaxLength) : (ushort)1;

            fixed (uint* p = px)
            fixed (ushort* pr = runs)
            fixed (int* ph = head, pc = chain)
            {
                int pos, nextCheck = 0;
                // large images at the usual effort: leave the hash chain out when a probe finds it useless
                if (n >= 1 << 19 && _method <= 4 && !ChainPaysOff(p, pr, n, xsize, ph, pc, hashShift, _cancel))
                {
                    for (pos = 0; pos < n;)
                    {
                        if (pos >= nextCheck) { _cancel.ThrowIfCancellationRequested(); nextCheck = pos + CancelStep; }
                        int len = Search(p, n, xsize, pos, pc, -1, 0, 0, out int dist);
                        if (len >= 2 && Worthwhile(p + pos, len, DistanceCode(xsize, dist), model)) { tokens.Add((uint)dist, len); pos += len; }
                        else tokens.Add(p[pos++], 0);
                    }
                    return tokens;
                }
                if (matches != null)
                {
                    // the longest match at every position; inside a long match the tail of the previous one is good enough
                    int prevLen = 0, prevDist = 0;
                    for (pos = 0; pos < n; pos++)
                    {
                        if ((pos & (CancelStep - 1)) == 0) _cancel.ThrowIfCancellationRequested();
                        int len, dist = 0;
                        // look the position up and enter it in one go: a single visit to its bucket
                        int candidate = -1;
                        // the buckets of the positions a few steps ahead are cache misses: ask for them early
                        if (System.Runtime.Intrinsics.X86.Sse.IsSupported && pos + 9 < n)
                            System.Runtime.Intrinsics.X86.Sse.Prefetch0(ph + Hash2(p + pos + 8, pr + pos + 8, hashShift));
                        if (pos + 1 < n)
                        {
                            int h = Hash2(p + pos, pr + pos, hashShift);
                            candidate = ph[h];
                            pc[pos] = candidate;
                            ph[h] = pos;
                        }
                        if (prevLen > 64) { len = prevLen - 1; dist = prevDist; }
                        else len = Search(p, n, xsize, pos, pc, candidate, Math.Max(0, pos - Window), maxIter, out dist);
                        matches[pos] = len >= 2 ? (uint)len << 20 | (uint)dist : 0;
                        prevLen = len; prevDist = dist;
                        // the match may also reach further back than the search of the earlier positions found
                        for (int k = 1; len >= 2 && k <= pos && pos - k >= dist && len + k <= MaxLength && p[pos - k] == p[pos - k - dist]; k++)
                        {
                            if (matches[pos - k] >> 20 >= (uint)(len + k)) break;
                            matches[pos - k] = (uint)(len + k) << 20 | (uint)dist;
                        }
                    }
                }

                pos = 0;
                nextCheck = 0;
                int pendingLen = 0, pendingDist = 0;
                while (pos < n)
                {
                    if (pos >= nextCheck) { _cancel.ThrowIfCancellationRequested(); nextCheck = pos + CancelStep; }
                    int len, dist;
                    if (pendingLen > 0) { len = pendingLen; dist = pendingDist; pendingLen = 0; }
                    else if (matches != null) { len = (int)(matches[pos] >> 20); dist = (int)(matches[pos] & 0xfffff); }
                    else len = FindMatch(p, pr, n, xsize, pos, ph, pc, hashShift, maxIter, ref inserted, out dist);
                    if (len >= 2 && Worthwhile(p + pos, len, DistanceCode(xsize, dist), model))
                    {
                        if (lazy && len < 64 && pos + 1 < n)
                        {
                            int len2, dist2;
                            if (matches != null) { len2 = (int)(matches[pos + 1] >> 20); dist2 = (int)(matches[pos + 1] & 0xfffff); }
                            else len2 = FindMatch(p, pr, n, xsize, pos + 1, ph, pc, hashShift, maxIter, ref inserted, out dist2);
                            if (len2 > len + 1)
                            {
                                tokens.Add(p[pos], 0);
                                pos++;
                                pendingLen = len2; pendingDist = dist2;
                                continue;
                            }
                        }
                        tokens.Add((uint)dist, len);
                        pos += len;
                    }
                    else
                    {
                        tokens.Add(p[pos], 0);
                        pos++;
                    }
                }
                if (!optimal) return tokens;
            }
            // photographs: with next to no matches there is nothing for the optimal parse to improve
            long copied = 0;
            for (int i = 0; i < tokens.Count; i++) copied += tokens.Len[i];
            if (copied * 64 < n) return tokens;
            // at the lowest effort with matching, only drawings get the second look
            if (_method == 1 && copied * 8 < n) return tokens;

            var cost = new float[n + 1];
            // length of the step that reaches a position | which candidate made it << 16 (0 longest match, 1 left, 2 above)
            var step = new int[n + 1];
            int passes = _method >= 4 ? 2 : 1;
            for (int pass = 0; pass < passes; pass++)
            {
                var hist = new[] { new int[lay.Total] };
                BuildHistograms(tokens, px, xsize, 0, -1, 0, hist, lay);
                model = new CostModel(hist[0], lay);
                Array.Fill(cost, float.MaxValue);
                cost[0] = 0;
                var lenCost = model.Length;
                float costLeft = model.Distance[PrefixEncode(DistanceCode(xsize, 1), out _, out _)];
                float costAbove = xsize > 1 ? model.Distance[PrefixEncode(DistanceCode(xsize, xsize), out _, out _)] : float.MaxValue;
                int runLeft = 0, runAbove = 0;      // match lengths at distance 1 and xsize, carried from pixel to pixel
                fixed (uint* p = px)
                {
                    for (int i = 0; i < n; i++)
                    {
                        if ((i & (CancelStep - 1)) == 0) _cancel.ThrowIfCancellationRequested();
                        float c0 = cost[i];
                        float c = c0 + model.LiteralCost(px[i]);
                        if (c < cost[i + 1]) { cost[i + 1] = c; step[i + 1] = 1; }      // (the candidate does not matter here)
                        int maxLen = Math.Min(MaxLength, n - i);
                        runLeft = runLeft > 1 ? Math.Min(runLeft - 1, maxLen) : i >= 1 ? MatchLength(p + i - 1, p + i, maxLen) : 0;
                        runAbove = runAbove > 1 ? Math.Min(runAbove - 1, maxLen) : i >= xsize && xsize > 1 ? MatchLength(p + i - xsize, p + i, maxLen) : 0;
                        uint m = matches![i];
                        for (int cand = 0; cand < 3; cand++)
                        {
                            int len;
                            float cd;
                            if (cand == 0)
                            {
                                if (m == 0) continue;
                                len = (int)(m >> 20);
                                cd = c0 + model.Distance[PrefixEncode(DistanceCode(xsize, (int)(m & 0xfffff)), out _, out _)];
                            }
                            else if (cand == 1) { len = runLeft; cd = c0 + costLeft; }
                            else { len = runAbove; cd = c0 + costAbove; }
                            if (len < 2) continue;
                            int upTo = len <= 64 ? len : 16;
                            int tag = cand << 16;
                            int l = 2;
                            if (Vector128.IsHardwareAccelerated)
                            {
                                // the lengths reach different positions, so four of them at a time give the same result
                                var vcd = Vector128.Create(cd);
                                var lanes = Vector128.Create(0, 1, 2, 3);
                                for (; l + 3 <= upTo; l += 4)
                                {
                                    var cc = vcd + Vector128.LoadUnsafe(ref lenCost[l]);
                                    ref float target = ref cost[i + l];
                                    var old = Vector128.LoadUnsafe(ref target);
                                    var better = Vector128.LessThan(cc, old);
                                    if (better == Vector128<float>.Zero) continue;
                                    Vector128.ConditionalSelect(better, cc, old).StoreUnsafe(ref target);
                                    ref int st = ref step[i + l];
                                    Vector128.ConditionalSelect(better.AsInt32(), Vector128.Create(l | tag) + lanes, Vector128.LoadUnsafe(ref st)).StoreUnsafe(ref st);
                                }
                            }
                            for (; l <= upTo; l++)
                            {
                                c = cd + lenCost[l];
                                if (c < cost[i + l]) { cost[i + l] = c; step[i + l] = l | tag; }
                            }
                            if (upTo < len)
                            {
                                c = cd + lenCost[len];
                                if (c < cost[i + len]) { cost[i + len] = c; step[i + len] = len | tag; }
                            }
                        }
                    }
                }
                // walk back along the cheapest path
                int count = 0;
                for (int i = n; i > 0; i -= step[i] & 0xffff) count++;
                var best = new Tokens(count);
                best.Count = count;
                for (int i = n, k = count - 1; i > 0; k--)
                {
                    int l = step[i] & 0xffff;
                    int v = step[i] >> 16;
                    i -= l;
                    if (l == 1) { best.Val[k] = px[i]; best.Len[k] = 0; }
                    else { best.Val[k] = v == 0 ? matches![i] & 0xfffff : v == 1 ? 1u : (uint)xsize; best.Len[k] = (ushort)l; }
                }
                tokens = best;
            }
            return tokens;
        }

        /// <summary>Whether a copy is likely cheaper than the pixels it covers coded as literals.</summary>
        static bool Worthwhile(uint* p, int len, int distCode, CostModel model)
        {
            if (len >= 24) return true;
            float copy = 6 + (len <= 4 ? 0 : BitOperations.Log2((uint)len - 1) - 1);
            copy += distCode <= 4 ? 4 : distCode <= 120 ? 7 : 6 + BitOperations.Log2((uint)distCode - 1);
            float literals = 0;
            for (int i = 0; i < len; i++) literals += model.LiteralCost(p[i]);
            return copy < literals;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Hash2(uint* p, ushort* run, int shift)
        {
            // Inside a run of one color the remaining run length takes the place of the second pixel: otherwise all
            // positions of flat areas would share one chain and matches that continue past the run would never be found.
            uint second = *run > 1 ? *run : p[1];
            return (int)((p[0] * 0x9e3779b1u + second * 0x85ebca6bu) * 0xc2b2ae35u >> shift);
        }

        static int MatchLength(uint* a, uint* b, int max)
        {
            int i = 0;
            while (i + 2 <= max && *(ulong*)(a + i) == *(ulong*)(b + i)) i += 2;
            if (i < max && a[i] == b[i]) i++;
            return i;
        }

        static int FindMatch(uint* p, ushort* run, int n, int xsize, int pos, int* head, int* chain, int hashShift, int maxIter, ref int inserted, out int bestDist)
        {
            // bring the chain up to date; a position enters once it lies behind the one being searched
            int limit = Math.Min(pos, n - 1);
            for (int i = inserted; i < limit; i++)
            {
                int h = Hash2(p + i, run + i, hashShift);
                chain[i] = head[h];
                head[h] = i;
            }
            if (limit > inserted) inserted = limit;
            int candidate = pos + 1 < n ? head[Hash2(p + pos, run + pos, hashShift)] : -1;
            return Search(p, n, xsize, pos, chain, candidate, Math.Max(0, pos - Window), maxIter, out bestDist);
        }

        /// <summary>The longest match for the pixels at pos: one row up, one pixel left, then along the hash chain.</summary>
        static int Search(uint* p, int n, int xsize, int pos, int* chain, int candidate, int minPos, int maxIter, out int bestDist)
        {
            int maxLen = Math.Min(MaxLength, n - pos);
            int best = 0;
            bestDist = 0;
            if (maxLen < 2) return 0;
            uint* cur = p + pos;
            // the cheapest distances first: the pixel above and the one to the left
            if (pos >= xsize && xsize > 1 && cur[-xsize] == cur[0])
            {
                best = MatchLength(cur - xsize, cur, maxLen);
                bestDist = xsize;
            }
            if (pos >= 1 && cur[-1] == cur[0])
            {
                int l = MatchLength(cur - 1, cur, maxLen);
                if (l > best) { best = l; bestDist = 1; }
            }
            if (best >= maxLen) return best;
            for (int j = candidate; j >= minPos && maxIter-- > 0; j = chain[j])
            {
                // the next candidate's pixels are a cache miss: start it while this one is compared
                if (System.Runtime.Intrinsics.X86.Sse.IsSupported) System.Runtime.Intrinsics.X86.Sse.Prefetch0(p + chain[j]);
                if (p[j + best] != cur[best] || p[j] != cur[0]) continue;
                int l = MatchLength(p + j, cur, maxLen);
                if (l > best)
                {
                    best = l; bestDist = pos - j;
                    if (l >= maxLen) break;
                }
            }
            return best;
        }

        /// <summary>
        /// Tries the match search on a few windows spread over the image and tells whether matches beyond plain runs
        /// occur at all. Photographs have next to none, and the hash chain is the most expensive part of the encoder.
        /// </summary>
        static bool ChainPaysOff(uint* p, ushort* run, int n, int xsize, int* head, int* chain, int hashShift, System.Threading.CancellationToken cancel)
        {
            const int Windows = 16, Size = 8192;
            long covered = 0;
            for (int k = 0; k < Windows; k++)
            {
                cancel.ThrowIfCancellationRequested();
                int start = (int)((long)(n - Size) * k / (Windows - 1)), end = start + Size;
                for (int pos = start; pos < end;)
                {
                    int h = Hash2(p + pos, run + pos, hashShift);
                    int candidate = head[h];
                    chain[pos] = candidate;
                    head[h] = pos;
                    int len = Search(p, end, xsize, pos, chain, candidate, start, 8, out int dist);
                    if (len >= 4 && dist != 1 && dist != xsize) { covered += len; pos += len; }
                    else pos++;
                }
                for (int pos = start; pos < end; pos++) head[Hash2(p + pos, run + pos, hashShift)] = -1;
            }
            return covered * 200 >= (long)Windows * Size;
        }

        // ------------------------------------------------------------------ histograms and the entropy image

        /// <summary>Layout of a histogram set: green / length / cache symbols, red, blue, alpha, distance.</summary>
        readonly struct Layout
        {
            public readonly int Green, Total;
            public Layout(int cacheBits)
            {
                Green = NumLiteral + NumLength + (cacheBits > 0 ? 1 << cacheBits : 0);
                Total = Green + 3 * 256 + NumDistance;
            }
            public int Red => Green;
            public int Blue => Green + 256;
            public int Alpha => Green + 512;
            public int Dist => Green + 768;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint CacheKey(uint argb, int cacheBits) => (argb * 0x1e35a7bdu) >> (32 - cacheBits);

        /// <summary>
        /// Walks the tokens and adds every symbol to the histogram of its block (blockBits &lt; 0: one histogram).
        /// </summary>
        static void BuildHistograms(Tokens t, uint[] px, int xsize, int cacheBits, int blockBits, int blocksWide, int[][] hist, Layout lay)
        {
            uint[]? cache = cacheBits > 0 ? new uint[1 << cacheBits] : null;
            int pos = 0, x = 0, y = 0;
            for (int i = 0; i < t.Count; i++)
            {
                var h = blockBits < 0 ? hist[0] : hist[(y >> blockBits) * blocksWide + (x >> blockBits)];
                int len = t.Len[i];
                if (len == 0)
                {
                    uint v = t.Val[i];
                    if (cache != null)
                    {
                        uint k = CacheKey(v, cacheBits);
                        if (cache[k] == v) { h[NumLiteral + NumLength + (int)k]++; goto advance; }
                        cache[k] = v;
                    }
                    h[(v >> 8) & 0xff]++;
                    h[lay.Red + (int)((v >> 16) & 0xff)]++;
                    h[lay.Blue + (int)(v & 0xff)]++;
                    h[lay.Alpha + (int)(v >> 24)]++;
                advance:
                    pos++;
                    if (++x == xsize) { x = 0; y++; }
                }
                else
                {
                    h[NumLiteral + PrefixEncode(len, out _, out _)]++;
                    h[lay.Dist + PrefixEncode(DistanceCode(xsize, (int)t.Val[i]), out _, out _)]++;
                    if (cache != null)
                        for (int k = 0; k < len; k++) { uint v = px[pos + k]; cache[CacheKey(v, cacheBits)] = v; }
                    pos += len;
                    x += len;
                    while (x >= xsize) { x -= xsize; y++; }
                }
            }
        }

        static double HistogramCost(int[] h, Layout lay)
        {
            var s = h.AsSpan();
            return CodeCost(s.Slice(0, lay.Green)) + CodeCost(s.Slice(lay.Red, 256)) + CodeCost(s.Slice(lay.Blue, 256))
                 + CodeCost(s.Slice(lay.Alpha, 256)) + CodeCost(s.Slice(lay.Dist, NumDistance)) + ExtraBits(h, lay);
        }

        static double ExtraBits(int[] h, Layout lay)
        {
            double bits = 0;
            for (int i = 4; i < NumLength; i++) bits += (double)h[NumLiteral + i] * ((i - 2) >> 1);
            for (int i = 4; i < NumDistance; i++) bits += (double)h[lay.Dist + i] * ((i - 2) >> 1);
            return bits;
        }

        /// <summary>Cost of the union of two histograms, without building it.</summary>
        static double CombinedCost(int[] a, int[] b, Layout lay)
        {
            double total = 0;
            Span<int> starts = stackalloc int[6] { 0, lay.Red, lay.Blue, lay.Alpha, lay.Dist, lay.Total };
            fixed (int* pa = a, pb = b)
            {
                for (int k = 0; k < 5; k++)
                {
                    long n = 0;
                    double s = 0;
                    int nz = 0, zr = 0;
                    bool inZeros = false;
                    for (int i = starts[k]; i < starts[k + 1]; i++)
                    {
                        int c = pa[i] + pb[i];
                        if (c == 0) { if (!inZeros) { zr++; inZeros = true; } continue; }
                        inZeros = false;
                        n += c; s += XLogX(c); nz++;
                    }
                    total += nz <= 1 ? TableCost(nz, zr) : n * Math.Log2(n) - s + TableCost(nz, zr);
                }
            }
            return total + ExtraBits(a, lay) + ExtraBits(b, lay);
        }

        static void AddInto(int[] dst, int[] src)
        {
            for (int i = 0; i < dst.Length; i++) dst[i] += src[i];
        }

        /// <summary>
        /// Clusters the per-block histograms: blocks that code well with a common set of prefix codes share one.
        /// Blocks are ordered by how expensive their pixels are and cut into equal bins; bins are then merged greedily
        /// while that saves bits, and finally every block moves to the cluster whose statistics suit it best.
        /// Returns the cluster index of every block and the number of clusters.
        /// </summary>
        int[] ClusterHistograms(int[][] hist, Layout lay, int bins, out int numClusters)
        {
            int n = hist.Length;
            var assign = new int[n];
            var order = new int[n];
            var keys = new double[n];
            for (int i = 0; i < n; i++)
            {
                order[i] = i;
                long cnt = 0;
                for (int k = 0; k < lay.Green; k++) cnt += hist[i][k];
                keys[i] = cnt == 0 ? 0 : HistogramCost(hist[i], lay) / cnt;
            }
            Array.Sort(keys, order);
            int m = Math.Min(n, bins);
            for (int i = 0; i < n; i++) assign[order[i]] = (int)((long)i * m / n);

            for (int round = 0; ; round++)
            {
                _cancel.ThrowIfCancellationRequested();
                var clusters = new int[m][];
                for (int c = 0; c < m; c++) clusters[c] = new int[lay.Total];
                for (int b = 0; b < n; b++) AddInto(clusters[assign[b]], hist[b]);
                var merged = MergeClusters(clusters, lay, out int[] target);
                for (int b = 0; b < n; b++) assign[b] = target[assign[b]];
                m = merged.Count;
                if (m == 1 || round == 2) break;

                // move every block to the cluster whose code suits it best
                var logs = new float[m][];
                for (int c = 0; c < m; c++) logs[c] = SymbolCosts(merged[c], lay);
                bool moved = false;
                var idx = new int[lay.Total];
                for (int b = 0; b < n; b++)
                {
                    var h = hist[b];
                    int nnz = 0;
                    for (int i = 0; i < h.Length; i++) if (h[i] != 0) idx[nnz++] = i;
                    int bestC = assign[b];
                    double bestCost = double.MaxValue;
                    for (int c = 0; c < m; c++)
                    {
                        var lc = logs[c];
                        double sum = 0;
                        for (int i = 0; i < nnz; i++) sum += h[idx[i]] * lc[idx[i]];
                        if (sum < bestCost) { bestCost = sum; bestC = c; }
                    }
                    if (bestC != assign[b]) { assign[b] = bestC; moved = true; }
                }
                if (!moved) break;
            }
            // renumber in order of first use, dropping clusters without blocks
            var map = new int[m];
            Array.Fill(map, -1);
            numClusters = 0;
            for (int b = 0; b < n; b++)
            {
                if (map[assign[b]] < 0) map[assign[b]] = numClusters++;
                assign[b] = map[assign[b]];
            }
            return assign;
        }

        /// <summary>Greedy agglomeration: repeatedly merges the pair of clusters that saves most. target maps old to new indices.</summary>
        static List<int[]> MergeClusters(int[][] clusters, Layout lay, out int[] target)
        {
            int m = clusters.Length;
            var cost = new double[m];
            var alive = new bool[m];
            var gain = new double[m, m];
            target = new int[m];
            for (int i = 0; i < m; i++) { cost[i] = HistogramCost(clusters[i], lay); alive[i] = true; target[i] = i; }
            for (int i = 0; i < m; i++)
                for (int j = i + 1; j < m; j++) gain[i, j] = CombinedCost(clusters[i], clusters[j], lay) - cost[i] - cost[j];
            while (true)
            {
                int bi = -1, bj = -1;
                double bestGain = 0;
                for (int i = 0; i < m; i++)
                {
                    if (!alive[i]) continue;
                    for (int j = i + 1; j < m; j++)
                        if (alive[j] && gain[i, j] < bestGain) { bestGain = gain[i, j]; bi = i; bj = j; }
                }
                if (bi < 0) break;
                AddInto(clusters[bi], clusters[bj]);
                cost[bi] += cost[bj] + bestGain;
                alive[bj] = false;
                for (int k = 0; k < m; k++)
                {
                    if (target[k] == bj) target[k] = bi;
                    if (!alive[k] || k == bi) continue;
                    double g = CombinedCost(clusters[bi], clusters[k], lay) - cost[bi] - cost[k];
                    if (k < bi) gain[k, bi] = g; else gain[bi, k] = g;
                }
            }
            var res = new List<int[]>();
            var index = new int[m];
            for (int i = 0; i < m; i++) if (alive[i]) { index[i] = res.Count; res.Add(clusters[i]); }
            for (int i = 0; i < m; i++) target[i] = index[target[i]];
            return res;
        }

        /// <summary>Approximate cost in bits of every symbol under the code of a histogram (unseen symbols are expensive).</summary>
        static float[] SymbolCosts(int[] h, Layout lay)
        {
            var res = new float[h.Length];
            Span<int> starts = stackalloc int[6] { 0, lay.Red, lay.Blue, lay.Alpha, lay.Dist, lay.Total };
            for (int k = 0; k < 5; k++)
            {
                long n = 0;
                for (int i = starts[k]; i < starts[k + 1]; i++) n += h[i];
                double ln = Math.Log2(n + 1);
                for (int i = starts[k]; i < starts[k + 1]; i++)
                    res[i] = (float)(h[i] == 0 ? ln + 4 : Math.Min(15, ln - Math.Log2(h[i])));
            }
            for (int i = 4; i < NumLength; i++) res[NumLiteral + i] += (i - 2) >> 1;
            for (int i = 4; i < NumDistance; i++) res[lay.Dist + i] += (i - 2) >> 1;
            return res;
        }

        /// <summary>The color cache size (0 = none) that minimizes the entropy of the literal symbols.</summary>
        int SelectCacheBits(Tokens t, uint[] px, int maxBits)
        {
            if (maxBits <= 0) return 0;
            int n = maxBits + 1;
            var caches = new uint[n][];
            var hist = new int[n][];     // per size: green literals, cache symbols, red, blue, alpha
            for (int b = 0; b < n; b++)
            {
                caches[b] = new uint[b == 0 ? 1 : 1 << b];
                hist[b] = new int[256 * 4 + (b == 0 ? 0 : 1 << b)];
            }
            int pos = 0;
            // the first few megapixels tell enough
            for (int i = 0; i < t.Count && pos < 3 << 20; i++)
            {
                int len = t.Len[i];
                if (len == 0)
                {
                    uint v = t.Val[i];
                    uint key = v * 0x1e35a7bdu;
                    int g = (int)((v >> 8) & 0xff), r = 256 + (int)((v >> 16) & 0xff), bl = 512 + (int)(v & 0xff), a = 768 + (int)(v >> 24);
                    var h0 = hist[0];
                    h0[g]++; h0[r]++; h0[bl]++; h0[a]++;
                    for (int b = 1; b < n; b++)
                    {
                        uint k = key >> (32 - b);
                        var h = hist[b];
                        if (caches[b][k] == v) h[1024 + (int)k]++;
                        else
                        {
                            caches[b][k] = v;
                            h[g]++; h[r]++; h[bl]++; h[a]++;
                        }
                    }
                    pos++;
                }
                else
                {
                    for (int k = 0; k < len; k++)
                    {
                        uint v = px[pos + k];
                        uint key = v * 0x1e35a7bdu;
                        for (int b = 1; b < n; b++) caches[b][key >> (32 - b)] = v;
                    }
                    pos += len;
                }
            }
            int best = 0;
            double bestCost = double.MaxValue;
            Span<int> green = stackalloc int[256 + (1 << maxBits)];
            for (int b = 0; b < n; b++)
            {
                var h = hist[b].AsSpan();
                var gr = green.Slice(0, 256 + (b == 0 ? 0 : 1 << b));
                h.Slice(0, 256).CopyTo(gr);
                if (b > 0) h.Slice(1024).CopyTo(gr.Slice(256));
                double c = CodeCost(gr) + CodeCost(h.Slice(256, 256)) + CodeCost(h.Slice(512, 256)) + CodeCost(h.Slice(768, 256));
                if (c < bestCost) { bestCost = c; best = b; }
            }
            return best;
        }

        // ------------------------------------------------------------------ writing image data

        sealed class GroupCodes
        {
            public byte[] Lens = Array.Empty<byte>();
            public ushort[] Codes = Array.Empty<ushort>();
        }

        static GroupCodes BuildAndWriteCodes(Vp8lBitWriter bw, int[] h, Layout lay)
        {
            var g = new GroupCodes { Lens = new byte[lay.Total], Codes = new ushort[lay.Total] };
            Span<int> starts = stackalloc int[6] { 0, lay.Red, lay.Blue, lay.Alpha, lay.Dist, lay.Total };
            for (int k = 0; k < 5; k++)
            {
                int s = starts[k], len = starts[k + 1] - s;
                var lens = g.Lens.AsSpan(s, len);
                BuildLengths(h.AsSpan(s, len), lens, 15);
                BuildCodes(lens, g.Codes.AsSpan(s, len));
                WriteCode(bw, lens);
            }
            return g;
        }

        static void WriteTokens(Vp8lBitWriter bw, Tokens t, uint[] px, int xsize, int cacheBits, int blockBits, int blocksWide,
            int[]? assign, GroupCodes[] groups, Layout lay)
        {
            uint[]? cache = cacheBits > 0 ? new uint[1 << cacheBits] : null;
            int pos = 0, x = 0, y = 0;
            for (int i = 0; i < t.Count; i++)
            {
                var g = assign == null ? groups[0] : groups[assign[(y >> blockBits) * blocksWide + (x >> blockBits)]];
                var lens = g.Lens;
                var codes = g.Codes;
                int len = t.Len[i];
                if (len == 0)
                {
                    uint v = t.Val[i];
                    if (cache != null)
                    {
                        uint k = CacheKey(v, cacheBits);
                        if (cache[k] == v)
                        {
                            int s = NumLiteral + NumLength + (int)k;
                            bw.Put(codes[s], lens[s]);
                            goto advance;
                        }
                        cache[k] = v;
                    }
                    int gs = (int)((v >> 8) & 0xff), rs = lay.Red + (int)((v >> 16) & 0xff), bs = lay.Blue + (int)(v & 0xff), als = lay.Alpha + (int)(v >> 24);
                    bw.Put(codes[gs], lens[gs]);
                    bw.Put(codes[rs], lens[rs]);
                    bw.Put(codes[bs], lens[bs]);
                    bw.Put(codes[als], lens[als]);
                advance:
                    pos++;
                    if (++x == xsize) { x = 0; y++; }
                }
                else
                {
                    int ls = NumLiteral + PrefixEncode(len, out int eb, out int ev);
                    bw.Put(codes[ls], lens[ls]);
                    if (eb > 0) bw.Put((uint)ev, eb);
                    int ds = lay.Dist + PrefixEncode(DistanceCode(xsize, (int)t.Val[i]), out eb, out ev);
                    bw.Put(codes[ds], lens[ds]);
                    if (eb > 0) bw.Put((uint)ev, eb);
                    if (cache != null)
                        for (int k = 0; k < len; k++) { uint v = px[pos + k]; cache[CacheKey(v, cacheBits)] = v; }
                    pos += len;
                    x += len;
                    while (x >= xsize) { x -= xsize; y++; }
                }
            }
        }

        /// <summary>A sub-resolution image (transform data, entropy image, color table): no cache, a single set of codes.</summary>
        void EncodeSubImage(Vp8lBitWriter bw, uint[] px, int w, int h)
        {
            bw.Put(0, 1);       // no color cache
            var t = BackwardReferences(px, w * h, w, _method == 0 ? 4 : 32, false, false);
            var lay = new Layout(0);
            var hist = new[] { new int[lay.Total] };
            BuildHistograms(t, px, w, 0, -1, 0, hist, lay);
            var g = BuildAndWriteCodes(bw, hist[0], lay);
            WriteTokens(bw, t, px, w, 0, 0, 0, null, new[] { g }, lay);
        }

        void EncodeImage(Vp8lBitWriter bw, uint[] px, int w, int h, bool palette)
        {
            int n = w * h;
            int iters = _method switch { 0 => 2, 1 => 8, 2 => 16, 3 => 32, 4 => 64, 5 => 128, _ => 256 };
            var t = BackwardReferences(px, n, w, iters, _method >= 1, _method >= 1);
            _cancel.ThrowIfCancellationRequested();
            int cacheBits = _method >= 1 ? SelectCacheBits(t, px, _method >= 4 ? MaxCacheBits : _method >= 2 ? 8 : 6) : 0;
            _cancel.ThrowIfCancellationRequested();
            // a longer match search is no promise of a smaller file: above the standard effort the standard parse runs too
            Tokens? standard = _method >= 5 ? BackwardReferences(px, n, w, 64, true, true) : null;
            int blockBits = Math.Clamp((palette ? 9 : 7) - _method, 2, 9);
            while ((long)SubSize(w, blockBits) * SubSize(h, blockBits) > MaxEntropyBlocks) blockBits++;
            int bins = _method >= 4 ? 64 : _method >= 2 ? 32 : 16;
            bool entropyImage = _method >= 1 && n >= 256;
            if (_method < 5)
            {
                WriteImage(bw, t, px, w, h, cacheBits, entropyImage ? blockBits : 0, bins);
                return;
            }
            // The estimates behind the choices above are good, not exact: write the image a few ways and keep the smallest.
            var variants = new List<(int cache, int block, int bins)> { (cacheBits, blockBits, bins) };
            if (entropyImage)
            {
                variants.Add((cacheBits, 0, bins));
                variants.Add((cacheBits, blockBits + 1, bins));
                if (blockBits > 2 && (long)SubSize(w, blockBits - 1) * SubSize(h, blockBits - 1) <= 4 * MaxEntropyBlocks) variants.Add((cacheBits, blockBits - 1, bins));
                variants.Add((cacheBits, blockBits, 24));
                if (_method >= 6) { variants.Add((cacheBits, blockBits, 128)); variants.Add((cacheBits, blockBits + 2, bins)); }
            }
            if (cacheBits > 0) variants.Add((0, entropyImage ? blockBits : 0, bins));
            else if (_method >= 6) variants.Add((4, entropyImage ? blockBits : 0, bins));
            Vp8lBitWriter? best = null;
            foreach (var v in variants)
            {
                _cancel.ThrowIfCancellationRequested();
                if (v.block > 9) continue;
                var trial = new Vp8lBitWriter();
                WriteImage(trial, t, px, w, h, v.cache, v.block, v.bins);
                if (best == null || trial.BitCount < best.BitCount) best = trial;
            }
            if (standard != null)
            {
                int cache = SelectCacheBits(standard, px, MaxCacheBits);
                foreach (int block in entropyImage ? new[] { blockBits, blockBits == 2 ? 0 : (palette ? 5 : 3) } : new[] { 0 })
                {
                    _cancel.ThrowIfCancellationRequested();
                    int b = block;
                    while (b > 0 && (long)SubSize(w, b) * SubSize(h, b) > MaxEntropyBlocks) b++;
                    var trial = new Vp8lBitWriter();
                    WriteImage(trial, standard, px, w, h, cache, b, 64);
                    if (trial.BitCount < best!.BitCount) best = trial;
                }
            }
            bw.Append(best!);
        }

        /// <summary>Writes color cache info, the entropy image (blockBits 0: none), the prefix codes and the pixels.</summary>
        void WriteImage(Vp8lBitWriter bw, Tokens t, uint[] px, int w, int h, int cacheBits, int blockBits, int bins)
        {
            bw.Put(cacheBits > 0 ? 1u : 0u, 1);
            if (cacheBits > 0) bw.Put((uint)cacheBits, 4);
            var lay = new Layout(cacheBits);
            int[]? assign = null;
            int blocksWide = 0, numGroups = 1;
            int[][] hist;
            if (blockBits > 0)
            {
                blocksWide = SubSize(w, blockBits);
                int blocksHigh = SubSize(h, blockBits);
                if (blocksWide * blocksHigh > 1)
                {
                    var blockHist = new int[blocksWide * blocksHigh][];
                    for (int i = 0; i < blockHist.Length; i++) blockHist[i] = new int[lay.Total];
                    BuildHistograms(t, px, w, cacheBits, blockBits, blocksWide, blockHist, lay);
                    _cancel.ThrowIfCancellationRequested();
                    assign = ClusterHistograms(blockHist, lay, bins, out numGroups);
                    if (numGroups == 1) assign = null;
                }
            }
            if (assign == null)
            {
                bw.Put(0, 1);
                hist = new[] { new int[lay.Total] };
                BuildHistograms(t, px, w, cacheBits, -1, 0, hist, lay);
            }
            else
            {
                bw.Put(1, 1);
                bw.Put((uint)blockBits - 2, 3);
                var img = new uint[assign.Length];
                for (int i = 0; i < img.Length; i++) img[i] = 0xff000000 | (uint)assign[i] << 8;
                EncodeSubImage(bw, img, blocksWide, SubSize(h, blockBits));
                // the final statistics of every group, now that all blocks have their place
                hist = new int[numGroups][];
                for (int i = 0; i < numGroups; i++) hist[i] = new int[lay.Total];
                BuildGroupHistograms(t, px, w, cacheBits, blockBits, blocksWide, assign, hist, lay);
            }
            _cancel.ThrowIfCancellationRequested();
            var groups = new GroupCodes[hist.Length];
            for (int i = 0; i < hist.Length; i++) groups[i] = BuildAndWriteCodes(bw, hist[i], lay);
            WriteTokens(bw, t, px, w, cacheBits, blockBits, blocksWide, assign, groups, lay);
        }

        static void BuildGroupHistograms(Tokens t, uint[] px, int xsize, int cacheBits, int blockBits, int blocksWide, int[] assign, int[][] hist, Layout lay)
        {
            // same walk as BuildHistograms, with the block index mapped to its group
            var perBlock = new int[assign.Length][];
            for (int i = 0; i < assign.Length; i++) perBlock[i] = hist[assign[i]];
            BuildHistograms(t, px, xsize, cacheBits, blockBits, blocksWide, perBlock, lay);
        }

        static int SubSize(int size, int bits) => (size + (1 << bits) - 1) >> bits;

        // ------------------------------------------------------------------ transforms

        void EncodeStream(Vp8lBitWriter bw, uint[] px, int w, int h, Config cfg, uint[]? palette)
        {
            int cw = w;
            if (cfg.Palette)
            {
                var pal = palette!;
                bw.Put(1, 1); bw.Put(3, 2);
                bw.Put((uint)pal.Length - 1, 8);
                Vp8lBitWriter? table = null;
                // With prediction the indices should follow brightness. Without, the order only matters for the table
                // itself, which is stored as differences: try a chain of nearest colors against the numeric order.
                var orders = cfg.Predictor ? new[] { SortByLuma(pal) } : _method == 0 || pal.Length < 4 ? new[] { pal } : new[] { pal, NearestChain(pal) };
                foreach (var order in orders)
                {
                    var deltas = new uint[order.Length];
                    for (int i = 0; i < order.Length; i++) deltas[i] = i == 0 ? order[0] : SubPixels(order[i], order[i - 1]);
                    var trial = new Vp8lBitWriter();
                    EncodeSubImage(trial, deltas, order.Length, 1);
                    if (table == null || trial.BitCount < table.BitCount) { table = trial; pal = order; }
                }
                bw.Append(table!);
                px = ApplyPalette(px, w, h, pal, out cw);
            }
            _cancel.ThrowIfCancellationRequested();
            if (cfg.SubGreen)
            {
                bw.Put(1, 1); bw.Put(2, 2);
                for (int i = 0; i < px.Length; i++)
                {
                    uint v = px[i], g = (v >> 8) & 0xff;
                    px[i] = (v & 0xff00ff00) | ((((v & 0x00ff00ff) | 0xff00ff00) - (g << 16 | g)) & 0x00ff00ff);
                }
            }
            if (cfg.Predictor)
            {
                int bits = cfg.Bits != 0 ? cfg.Bits : cw * h > 1 << 22 ? 5 : 4;
                bw.Put(1, 1); bw.Put(0, 2);
                bw.Put((uint)bits - 2, 3);
                int tw = SubSize(cw, bits), th = SubSize(h, bits);
                var modes = ChoosePredictors(px, cw, h, bits);
                var img = new uint[tw * th];
                for (int i = 0; i < img.Length; i++) img[i] = 0xff000000 | (uint)modes[i] << 8;
                EncodeSubImage(bw, img, tw, th);
                _cancel.ThrowIfCancellationRequested();
                ApplyPredictors(px, cw, h, bits, modes);
            }
            _cancel.ThrowIfCancellationRequested();
            if (cfg.CrossColor)
            {
                int bits = cfg.Bits != 0 ? cfg.Bits : cw * h > 1 << 22 ? 5 : 4;
                int tw = SubSize(cw, bits), th = SubSize(h, bits);
                var img = ChooseCrossColor(px, cw, h, bits);
                if (img != null)
                {
                    bw.Put(1, 1); bw.Put(1, 2);
                    bw.Put((uint)bits - 2, 3);
                    EncodeSubImage(bw, img, tw, th);
                }
            }
            _cancel.ThrowIfCancellationRequested();
            bw.Put(0, 1);
            EncodeImage(bw, px, cw, h, cfg.Palette);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint SubPixels(uint a, uint b)
        {
            uint ag = 0x00ff00ffu + (a & 0xff00ff00u) - (b & 0xff00ff00u);
            uint rb = 0xff00ff00u + (a & 0x00ff00ffu) - (b & 0x00ff00ffu);
            return (ag & 0xff00ff00u) | (rb & 0x00ff00ffu);
        }

        /// <summary>The distinct colors in ascending order, or null when there are more than 256.</summary>
        static uint[]? FindPalette(uint[] px)
        {
            var set = new HashSet<uint>();
            uint last = px[0];
            set.Add(last);
            for (int i = 1; i < px.Length; i++)
            {
                uint v = px[i];
                if (v == last) continue;
                last = v;
                if (set.Add(v) && set.Count > 256) return null;
            }
            var pal = new uint[set.Count];
            set.CopyTo(pal);
            Array.Sort(pal);
            return pal;
        }

        /// <summary>Orders the colors so that each one is close to its predecessor (greedy), starting from the smallest.</summary>
        static uint[] NearestChain(uint[] pal)
        {
            var res = new uint[pal.Length];
            var used = new bool[pal.Length];
            uint cur = pal[0];
            used[0] = true;
            res[0] = cur;
            for (int k = 1; k < pal.Length; k++)
            {
                int best = -1, bestDist = int.MaxValue;
                for (int i = 0; i < pal.Length; i++)
                {
                    if (used[i]) continue;
                    uint v = pal[i];
                    int d = Math.Abs((int)(v >> 24) - (int)(cur >> 24)) + Math.Abs((int)((v >> 16) & 0xff) - (int)((cur >> 16) & 0xff))
                          + Math.Abs((int)((v >> 8) & 0xff) - (int)((cur >> 8) & 0xff)) + Math.Abs((int)(v & 0xff) - (int)(cur & 0xff));
                    if (d < bestDist) { bestDist = d; best = i; }
                }
                used[best] = true;
                cur = res[k] = pal[best];
            }
            return res;
        }

        static uint[] SortByLuma(uint[] pal)
        {
            var res = (uint[])pal.Clone();
            var keys = new long[res.Length];
            for (int i = 0; i < res.Length; i++)
            {
                uint v = res[i];
                long luma = 2 * ((v >> 16) & 0xff) + 5 * ((v >> 8) & 0xff) + (v & 0xff);
                keys[i] = (long)(v >> 24) << 40 | luma << 24 | (v & 0xffffff);
            }
            Array.Sort(keys, res);
            return res;
        }

        /// <summary>Open addressing table from color to palette index.</summary>
        sealed class PaletteLookup
        {
            readonly uint[] _keys = new uint[1024];
            readonly short[] _vals = new short[1024];

            public PaletteLookup(uint[] pal)
            {
                Array.Fill(_vals, (short)-1);
                for (int i = 0; i < pal.Length; i++)
                {
                    uint k = pal[i] * 0x9e3779b1u >> 22;
                    while (_vals[k] >= 0) k = (k + 1) & 1023;
                    _keys[k] = pal[i]; _vals[k] = (short)i;
                }
            }

            public int this[uint color]
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    uint k = color * 0x9e3779b1u >> 22;
                    while (_keys[k] != color || _vals[k] < 0) k = (k + 1) & 1023;
                    return _vals[k];
                }
            }
        }

        static uint[] ApplyPalette(uint[] px, int w, int h, uint[] pal, out int packedWidth)
        {
            int bits = pal.Length > 16 ? 0 : pal.Length > 4 ? 1 : pal.Length > 2 ? 2 : 3;
            packedWidth = SubSize(w, bits);
            var index = new PaletteLookup(pal);
            var res = new uint[packedWidth * h];
            int bpp = 8 >> bits, per = 1 << bits;
            uint lastColor = pal[0];
            uint lastIndex = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    uint v = px[y * w + x];
                    if (v != lastColor) { lastColor = v; lastIndex = (uint)index[v]; }
                    res[y * packedWidth + (x >> bits)] |= lastIndex << (8 + (x & (per - 1)) * bpp);
                }
                for (int x = 0; x < packedWidth; x++) res[y * packedWidth + x] |= 0xff000000;
            }
            return res;
        }

        /// <summary>
        /// Entropy of the symbols for the three ways to code an image of at most 256 colors: palette indices, indices of
        /// the luma-sorted palette predicted from the left, and left-predicted pixels after subtracting green.
        /// </summary>
        static void EstimatePaletteModes(uint[] px, uint[] pal, out double plain, out double predicted, out double direct)
        {
            var index = new PaletteLookup(SortByLuma(pal));
            var idx = new int[256];
            var idxPred = new int[256];
            var pred = new int[4 * 256];
            uint prev = 0xff000000;
            int prevIndex = 0;
            for (int i = 0; i < px.Length; i++)
            {
                uint v = px[i];
                int k = index[v];
                idx[k]++;
                idxPred[(k - prevIndex) & 255]++;
                prevIndex = k;
                uint g = (v >> 8) & 0xff;
                uint sg = (v & 0xff00ff00) | ((((v & 0x00ff00ff) | 0xff00ff00) - (g << 16 | g)) & 0x00ff00ff);
                uint d = SubPixels(sg, prev);
                prev = sg;
                pred[d >> 24]++; pred[256 + ((d >> 16) & 0xff)]++; pred[512 + ((d >> 8) & 0xff)]++; pred[768 + (d & 0xff)]++;
            }
            plain = EntropyBits(idx);
            // the real predictors do somewhat better than the left pixel alone
            predicted = 0.9 * EntropyBits(idxPred);
            direct = 0.9 * (EntropyBits(pred.AsSpan(0, 256)) + EntropyBits(pred.AsSpan(256, 256)) + EntropyBits(pred.AsSpan(512, 256)) + EntropyBits(pred.AsSpan(768, 256)));
        }

        // ---- predictor transform

        /// <summary>Residuals of the pixels [x0, x1) of row y for a prediction mode (the frame edges have fixed predictors).</summary>
        static void Residuals(uint* img, int w, int y, int x0, int x1, int mode, uint* dst)
        {
            uint* cur = img + (long)y * w;
            if (y == 0)
            {
                if (x0 == 0) { *dst++ = SubPixels(cur[0], 0xff000000); x0 = 1; }
                for (int x = x0; x < x1; x++) *dst++ = SubPixels(cur[x], cur[x - 1]);
                return;
            }
            uint* up = cur - w;
            if (x0 == 0) { *dst++ = SubPixels(cur[0], up[0]); x0 = 1; }
            if (Vector128.IsHardwareAccelerated)
            {
                // four pixels at a time; every neighbor is an original pixel, so nothing here depends on the pixel before
                for (; x0 + 4 <= x1; x0 += 4, dst += 4)
                {
                    var c = Vector128.Load((byte*)(cur + x0));
                    var l = Vector128.Load((byte*)(cur + x0 - 1));
                    var t = Vector128.Load((byte*)(up + x0));
                    var tl = Vector128.Load((byte*)(up + x0 - 1));
                    Vector128<byte> pred;
                    switch (mode)
                    {
                        case 0: pred = Vector128.Create(0xff000000u).AsByte(); break;
                        case 1: pred = l; break;
                        case 2: pred = t; break;
                        case 3: pred = Vector128.Load((byte*)(up + x0 + 1)); break;
                        case 4: pred = tl; break;
                        case 5: pred = Avg(Avg(l, Vector128.Load((byte*)(up + x0 + 1))), t); break;
                        case 6: pred = Avg(l, tl); break;
                        case 7: pred = Avg(l, t); break;
                        case 8: pred = Avg(tl, t); break;
                        case 9: pred = Avg(t, Vector128.Load((byte*)(up + x0 + 1))); break;
                        case 10: pred = Avg(Avg(l, tl), Avg(t, Vector128.Load((byte*)(up + x0 + 1)))); break;
                        case 11:
                        {
                            // the gradient estimate L + T - TL is as far from L as T is from TL
                            var toLeft = ChannelSum(Vector128.Max(t, tl) - Vector128.Min(t, tl));
                            var toTop = ChannelSum(Vector128.Max(l, tl) - Vector128.Min(l, tl));
                            pred = Vector128.ConditionalSelect(Vector128.LessThan(toLeft.AsInt32(), toTop.AsInt32()).AsByte(), l, t);
                            break;
                        }
                        case 12:
                            // clamp(L + T - TL): add what T has above TL, take off what it has below
                            pred = SubSat(AddSat(l, SubSat(t, tl)), SubSat(tl, t));
                            break;
                        default:
                        {
                            var a = Avg(l, t);
                            // clamp(a + (a - TL) / 2), the division truncating towards zero
                            pred = SubSat(AddSat(a, HalfBytes(SubSat(a, tl))), HalfBytes(SubSat(tl, a)));
                            break;
                        }
                    }
                    (c - pred).Store((byte*)dst);
                }
            }
            switch (mode)
            {
                case 0: for (int x = x0; x < x1; x++) *dst++ = SubPixels(cur[x], 0xff000000); break;
                case 1: for (int x = x0; x < x1; x++) *dst++ = SubPixels(cur[x], cur[x - 1]); break;
                case 2: for (int x = x0; x < x1; x++) *dst++ = SubPixels(cur[x], up[x]); break;
                case 3: for (int x = x0; x < x1; x++) *dst++ = SubPixels(cur[x], up[x + 1]); break;
                case 4: for (int x = x0; x < x1; x++) *dst++ = SubPixels(cur[x], up[x - 1]); break;
                case 5: for (int x = x0; x < x1; x++) *dst++ = SubPixels(cur[x], Vp8lDecoder.Average2(Vp8lDecoder.Average2(cur[x - 1], up[x + 1]), up[x])); break;
                case 6: for (int x = x0; x < x1; x++) *dst++ = SubPixels(cur[x], Vp8lDecoder.Average2(cur[x - 1], up[x - 1])); break;
                case 7: for (int x = x0; x < x1; x++) *dst++ = SubPixels(cur[x], Vp8lDecoder.Average2(cur[x - 1], up[x])); break;
                case 8: for (int x = x0; x < x1; x++) *dst++ = SubPixels(cur[x], Vp8lDecoder.Average2(up[x - 1], up[x])); break;
                case 9: for (int x = x0; x < x1; x++) *dst++ = SubPixels(cur[x], Vp8lDecoder.Average2(up[x], up[x + 1])); break;
                case 10: for (int x = x0; x < x1; x++) *dst++ = SubPixels(cur[x], Vp8lDecoder.Average2(Vp8lDecoder.Average2(cur[x - 1], up[x - 1]), Vp8lDecoder.Average2(up[x], up[x + 1]))); break;
                case 11: for (int x = x0; x < x1; x++) *dst++ = SubPixels(cur[x], Vp8lDecoder.Select(cur[x - 1], up[x], up[x - 1])); break;
                case 12: for (int x = x0; x < x1; x++) *dst++ = SubPixels(cur[x], Vp8lDecoder.ClampAddSubFull(cur[x - 1], up[x], up[x - 1])); break;
                default: for (int x = x0; x < x1; x++) *dst++ = SubPixels(cur[x], Vp8lDecoder.ClampAddSubHalf(Vp8lDecoder.Average2(cur[x - 1], up[x]), up[x - 1])); break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<byte> HalfBytes(Vector128<byte> a)
            => Vector128.ShiftRightLogical((a & Vector128.Create((byte)0xfe)).AsUInt16(), 1).AsByte();

        /// <summary>Per byte (a + b) / 2, rounded down.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<byte> Avg(Vector128<byte> a, Vector128<byte> b) => (a & b) + HalfBytes(a ^ b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<byte> AddSat(Vector128<byte> a, Vector128<byte> b)
        {
            var sum = a + b;
            return sum | Vector128.LessThan(sum, a);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<byte> SubSat(Vector128<byte> a, Vector128<byte> b) => Vector128.Max(a, b) - b;

        /// <summary>The sum of the four bytes of every 32 bit lane.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<uint> ChannelSum(Vector128<byte> a)
        {
            var pairs = (a.AsUInt16() & Vector128.Create((ushort)0xff)) + Vector128.ShiftRightLogical(a.AsUInt16(), 8);
            return (pairs.AsUInt32() & Vector128.Create(0xffffu)) + Vector128.ShiftRightLogical(pairs.AsUInt32(), 16);
        }

        static ReadOnlySpan<byte> FastModes => new byte[] { 1, 2, 11, 12, 5, 7 };
        static ReadOnlySpan<byte> AllModes => new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 };

        /// <summary>
        /// Picks for every block the mode whose residuals are cheapest given the statistics of the blocks chosen so far
        /// (the bits the block adds to an adaptive code), with a small preference for the modes of its neighbors.
        /// </summary>
        byte[] ChoosePredictors(uint[] px, int w, int h, int bits)
        {
            int tw = SubSize(w, bits), th = SubSize(h, bits);
            var modes = new byte[tw * th];
            if (_method == 0)
            {
                Array.Fill(modes, (byte)11);
                return modes;
            }
            var candidates = _method == 1 ? FastModes : AllModes;
            int size = 1 << bits;
            var acc = new int[1024];
            // two sparse tile histograms (alpha, red, green, blue residuals): the candidate and the best so far
            var tile = new int[1024];
            var bestTile = new int[1024];
            var seen = new ushort[1024];
            var bestSeen = new ushort[1024];
            uint* res = stackalloc uint[size];
            fixed (uint* img = px)
            fixed (int* pt0 = tile, pb0 = bestTile, pacc = acc)
            fixed (ushort* ps0 = seen, pbs0 = bestSeen)
            {
                int* pt = pt0, pb = pb0;
                ushort* ps = ps0, pbs = pbs0;
                int count = 0, bestCount = 0;
                for (int by = 0; by < th; by++)
                {
                    _cancel.ThrowIfCancellationRequested();
                    int y0 = by << bits, y1 = Math.Min(h, y0 + size);
                    for (int bx = 0; bx < tw; bx++)
                    {
                        int x0 = bx << bits, x1 = Math.Min(w, x0 + size);
                        int left = bx > 0 ? modes[by * tw + bx - 1] : -1, top = by > 0 ? modes[(by - 1) * tw + bx] : -1;
                        double bestCost = double.MaxValue;
                        int best = 0;
                        for (int i = 0; i < bestCount; i++) pb[pbs[i]] = 0;
                        bestCount = 0;
                        foreach (int mode in candidates)
                        {
                            for (int i = 0; i < count; i++) pt[ps[i]] = 0;
                            count = 0;
                            for (int y = y0; y < y1; y++)
                            {
                                Residuals(img, w, y, x0, x1, mode, res);
                                for (int i = 0; i < x1 - x0; i++)
                                {
                                    uint r = res[i];
                                    int a = (int)(r >> 24), rr = 256 + (int)((r >> 16) & 0xff), g = 512 + (int)((r >> 8) & 0xff), b = 768 + (int)(r & 0xff);
                                    if (pt[a]++ == 0) ps[count++] = (ushort)a;
                                    if (pt[rr]++ == 0) ps[count++] = (ushort)rr;
                                    if (pt[g]++ == 0) ps[count++] = (ushort)g;
                                    if (pt[b]++ == 0) ps[count++] = (ushort)b;
                                }
                            }
                            // the bits the block adds to an adaptive code that has seen the blocks before it
                            // (the term for the totals is the same for every mode and left out)
                            double cost = 0;
                            for (int i = 0; i < count; i++)
                            {
                                int k = ps[i];
                                cost -= XLogX(pacc[k] + pt[k]) - XLogX(pacc[k]);
                            }
                            if (mode == left || mode == top) cost -= 3;
                            if (cost < bestCost)
                            {
                                bestCost = cost; best = mode;
                                int* tmp = pt; pt = pb; pb = tmp;
                                ushort* tmps = ps; ps = pbs; pbs = tmps;
                                int tc = count; count = bestCount; bestCount = tc;
                            }
                        }
                        modes[by * tw + bx] = (byte)best;
                        for (int i = 0; i < bestCount; i++) pacc[pbs[i]] += pb[pbs[i]];
                    }
                }
            }
            return modes;
        }

        static void ApplyPredictors(uint[] px, int w, int h, int bits, byte[] modes)
        {
            int tw = SubSize(w, bits), size = 1 << bits;
            var row = new uint[w];
            fixed (uint* img = px, pr = row)
            {
                // bottom up, so that every prediction still sees original pixels
                for (int y = h - 1; y >= 0; y--)
                {
                    for (int x0 = 0; x0 < w; x0 += size)
                        Residuals(img, w, y, x0, Math.Min(w, x0 + size), modes[(y >> bits) * tw + (x0 >> bits)], pr + x0);
                    Buffer.MemoryCopy(pr, img + (long)y * w, (long)w * 4, (long)w * 4);
                }
            }
        }

        // ---- cross-color transform

        /// <summary>
        /// Searches per block the three coefficients that decorrelate red and blue from green (and blue from red),
        /// applies them and returns the transform image, or null when the transform would not pay.
        /// </summary>
        uint[]? ChooseCrossColor(uint[] px, int w, int h, int bits)
        {
            int tw = SubSize(w, bits), th = SubSize(h, bits), size = 1 << bits;
            var img = new uint[tw * th];
            var accR = new int[256];
            var accB = new int[256];
            int accN = 0;
            var before = new int[512];
            var g = new sbyte[size * size];
            var r = new sbyte[size * size];
            var b = new sbyte[size * size];
            var tile = new int[256];
            var seen = new byte[256];
            bool any = false;
            int prevG2r = 0, prevG2b = 0, prevR2b = 0;
            for (int by = 0; by < th; by++)
            {
                _cancel.ThrowIfCancellationRequested();
                int y0 = by << bits, y1 = Math.Min(h, y0 + size);
                for (int bx = 0; bx < tw; bx++)
                {
                    int x0 = bx << bits, x1 = Math.Min(w, x0 + size);
                    int n = 0;
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++)
                        {
                            uint v = px[y * w + x];
                            before[(v >> 16) & 0xff]++; before[256 + (v & 0xff)]++;
                            g[n] = (sbyte)(v >> 8); r[n] = (sbyte)(v >> 16); b[n] = (sbyte)v;
                            n++;
                        }
                    if (bx == 0) prevG2r = prevG2b = prevR2b = 0;

                    // red from green
                    int g2r = 0;
                    double best = RedCost(g, r, n, 0, tile, seen, accR, accN, prevG2r);
                    for (int step = 32; step >= 1; step >>= 1)
                    {
                        int center = g2r;
                        for (int s = -1; s <= 1; s += 2)
                        {
                            int cand = center + s * step;
                            if (cand < -128 || cand > 127) continue;
                            double c = RedCost(g, r, n, cand, tile, seen, accR, accN, prevG2r);
                            if (c < best) { best = c; g2r = cand; }
                        }
                    }
                    // blue from green and red
                    int g2b = 0, r2b = 0;
                    best = BlueCost(g, r, b, n, 0, 0, tile, seen, accB, accN, prevG2b, prevR2b);
                    for (int step = 32; step >= 1; step >>= 1)
                    {
                        for (int axis = 0; axis < 2; axis++)
                        {
                            int cg = g2b, cr = r2b;
                            for (int s = -1; s <= 1; s += 2)
                            {
                                int ng = axis == 0 ? cg + s * step : cg, nr = axis == 1 ? cr + s * step : cr;
                                if (ng < -128 || ng > 127 || nr < -128 || nr > 127) continue;
                                double c = BlueCost(g, r, b, n, ng, nr, tile, seen, accB, accN, prevG2b, prevR2b);
                                if (c < best) { best = c; g2b = ng; r2b = nr; }
                            }
                        }
                    }
                    prevG2r = g2r; prevG2b = g2b; prevR2b = r2b;
                    if ((g2r | g2b | r2b) != 0) any = true;
                    img[by * tw + bx] = 0xff000000 | (uint)(byte)r2b << 16 | (uint)(byte)g2b << 8 | (byte)g2r;
                    // apply and account
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++)
                        {
                            uint v = px[y * w + x];
                            sbyte gg = (sbyte)(v >> 8), rr = (sbyte)(v >> 16);
                            int nr = (int)(v >> 16) - Vp8lDecoder.ColorDelta((sbyte)g2r, gg);
                            int nb = (int)v - Vp8lDecoder.ColorDelta((sbyte)g2b, gg) - Vp8lDecoder.ColorDelta((sbyte)r2b, rr);
                            px[y * w + x] = (v & 0xff00ff00) | (uint)(nr & 0xff) << 16 | (uint)(nb & 0xff);
                            accR[nr & 0xff]++; accB[nb & 0xff]++;
                        }
                    accN += n;
                }
            }
            if (any)
            {
                double gain = EntropyBits(before.AsSpan(0, 256)) + EntropyBits(before.AsSpan(256, 256)) - EntropyBits(accR) - EntropyBits(accB);
                if (gain > 6.0 * tw * th + 64) return img;
            }
            // undo: not worth its transform image
            for (int by = 0; by < th; by++)
                for (int bx = 0; bx < tw; bx++)
                {
                    uint e = img[by * tw + bx];
                    if ((e & 0xffffff) == 0) continue;
                    sbyte g2r = (sbyte)e, g2b = (sbyte)(e >> 8), r2b = (sbyte)(e >> 16);
                    int y1 = Math.Min(h, (by + 1) << bits), x1 = Math.Min(w, (bx + 1) << bits);
                    for (int y = by << bits; y < y1; y++)
                        for (int x = bx << bits; x < x1; x++)
                        {
                            uint v = px[y * w + x];
                            sbyte gg = (sbyte)(v >> 8);
                            int rr = (int)(v >> 16) + Vp8lDecoder.ColorDelta(g2r, gg);
                            int bb = (int)v + Vp8lDecoder.ColorDelta(g2b, gg) + Vp8lDecoder.ColorDelta(r2b, (sbyte)rr);
                            px[y * w + x] = (v & 0xff00ff00) | (uint)(rr & 0xff) << 16 | (uint)(bb & 0xff);
                        }
                }
            return null;
        }

        // The tile histograms of the searches are sparse: the values seen are listed, so that costing and clearing a
        // histogram takes time proportional to the pixels of the block and not to the 256 bins.
        static double RedCost(sbyte[] g, sbyte[] r, int n, int g2r, int[] tile, byte[] seen, int[] acc, int accN, int prev)
        {
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                int v = (byte)(r[i] - ((g2r * g[i]) >> 5));
                if (tile[v]++ == 0) seen[count++] = (byte)v;
            }
            double c = MarginalCost(tile, seen, count, acc, accN, n);
            if (g2r == prev) c -= 3;
            if (g2r == 0) c -= 3;
            return c;
        }

        static double BlueCost(sbyte[] g, sbyte[] r, sbyte[] b, int n, int g2b, int r2b, int[] tile, byte[] seen, int[] acc, int accN, int prevG, int prevR)
        {
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                int v = (byte)(b[i] - ((g2b * g[i]) >> 5) - ((r2b * r[i]) >> 5));
                if (tile[v]++ == 0) seen[count++] = (byte)v;
            }
            double c = MarginalCost(tile, seen, count, acc, accN, n);
            if (g2b == prevG) c -= 3;
            if (g2b == 0) c -= 3;
            if (r2b == prevR) c -= 3;
            if (r2b == 0) c -= 3;
            return c;
        }

        /// <summary>Bits the tile adds to an adaptive code that has seen acc so far; clears the tile.</summary>
        static double MarginalCost(int[] tile, byte[] seen, int count, int[] acc, int accN, int n)
        {
            double cost = XLogX(accN + n) - XLogX(accN);
            for (int i = 0; i < count; i++)
            {
                int v = seen[i];
                cost -= XLogX(acc[v] + tile[v]) - XLogX(acc[v]);
                tile[v] = 0;
            }
            return cost;
        }
    }
}
