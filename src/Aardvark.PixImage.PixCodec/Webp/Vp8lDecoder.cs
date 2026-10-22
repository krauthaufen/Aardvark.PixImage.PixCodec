using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Aardvark.ImageCodecs
{
    /// <summary>LSB-first bit reader of the lossless bitstream. Reading past the end leaves <see cref="Bits"/> negative,
    /// which the callers turn into an <see cref="ImageFormatException"/> at their next check.</summary>
    internal unsafe struct Vp8lBitReader
    {
        public byte* Cur;
        public readonly byte* End;
        public ulong Val;
        public int Bits;

        public Vp8lBitReader(byte* buf, int len)
        {
            Cur = buf; End = buf + len; Val = 0; Bits = 0;
            Fill(ref Val, ref Bits, ref Cur, End);
        }

        /// <summary>Tops the window up to at least 57 bits as long as there is input left.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Fill(ref ulong val, ref int bits, ref byte* cur, byte* end)
        {
            if ((uint)bits <= 32)
            {
                if (end - cur >= 8)
                {
                    val |= Unsafe.ReadUnaligned<ulong>(cur) << bits;
                    int n = (63 - bits) >> 3;
                    cur += n; bits += n * 8;
                }
                else
                {
                    // no call here: state that lives across a call is kept on the stack by the JIT
                    while (bits <= 56 && cur < end)
                    {
                        val |= (ulong)*cur++ << bits;
                        bits += 8;
                    }
                }
            }
        }

        public void Fill() => Fill(ref Val, ref Bits, ref Cur, End);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Skip(int n) { Val >>= n; Bits -= n; }

        /// <summary>Reads up to 32 bits and fails when the stream is exhausted.</summary>
        public uint ReadBits(int n)
        {
            Fill();
            uint v = (uint)(Val & ((1UL << n) - 1));
            Val >>= n; Bits -= n;
            if (Bits < 0) throw new ImageFormatException("WebP lossless data ends prematurely");
            return v;
        }

        public bool Overrun => Bits < 0;
    }

    /// <summary>Decoder of the VP8L (WebP lossless) bitstream, RFC 9649 section 3.</summary>
    internal sealed unsafe class Vp8lDecoder
    {
        const int NumLiteral = 256, NumLength = 24, NumDistance = 40;
        const int RootBits = 8;
        const int GroupInts = 12;      // per prefix code group: 5 x (table offset, root mask), flags, constant ARGB part
        const int FlagTrivialLiteral = 1, FlagTrivialPixel = 2;

        Vp8lBitReader _br;
        readonly long _maxPixels;
        readonly int _length;
        readonly TablePool _scratch = new TablePool(256);

        Vp8lDecoder(byte* data, int length, long maxPixels)
        {
            _br = new Vp8lBitReader(data, length);
            _maxPixels = maxPixels;
            _length = length;
        }

        public static bool TryReadHeader(ReadOnlySpan<byte> d, out int width, out int height, out bool alpha)
        {
            width = height = 0; alpha = false;
            if (d.Length < 5 || d[0] != 0x2f) return false;
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(1));
            width = (int)(v & 0x3fff) + 1;
            height = (int)((v >> 14) & 0x3fff) + 1;
            alpha = ((v >> 28) & 1) != 0;
            return (v >> 29) == 0;
        }

        /// <summary>
        /// Decodes a complete VP8L chunk payload to ARGB pixels (0xAARRGGBB). The array may be longer than the image;
        /// when <paramref name="rented"/> it comes from <see cref="ArrayPool{T}.Shared"/> and goes back there after use.
        /// </summary>
        public static uint[] Decode(ReadOnlySpan<byte> data, out int width, out int height, long maxPixels, out bool rented)
        {
            if (!TryReadHeader(data, out width, out height, out _))
                throw new ImageFormatException("invalid WebP lossless header");
            if ((long)width * height > maxPixels)
                throw new ImageNotSupportedException($"WebP image of {width}x{height} exceeds WebpDecodeOptions.MaxPixels ({maxPixels})");
            fixed (byte* p = data)
            {
                var dec = new Vp8lDecoder(p + 5, data.Length - 5, maxPixels);
                var px = dec.DecodeImageStream(width, height, true);
                rented = px == dec._rented;
                return px;
            }
        }

        /// <summary>Decodes a header-less image stream of known size (the payload of a compressed ALPH chunk); as <see cref="Decode"/>.</summary>
        public static uint[] DecodeHeaderless(ReadOnlySpan<byte> data, int width, int height, out bool rented)
        {
            fixed (byte* p = data)
            {
                var dec = new Vp8lDecoder(p, data.Length, (long)width * height);
                var px = dec.DecodeImageStream(width, height, true);
                rented = px == dec._rented;
                return px;
            }
        }

        uint[]? _rented;   // the pixel buffer of the main image, when it came from the pool

        static ImageFormatException Bad(string what) => new ImageFormatException("invalid WebP lossless data: " + what);

        static int SubSize(int size, int bits) => (size + (1 << bits) - 1) >> bits;

        struct Transform
        {
            public int Type, XSize, Bits;
            public uint[] Data;
        }

        uint[] DecodeImageStream(int xsize, int ysize, bool level0)
        {
            Transform[]? transforms = null;
            int numTransforms = 0, fullWidth = xsize;
            if (level0)
            {
                transforms = new Transform[4];
                int seen = 0;
                while (_br.ReadBits(1) != 0)
                {
                    int type = (int)_br.ReadBits(2);
                    if ((seen & (1 << type)) != 0) throw Bad("transform used twice");
                    seen |= 1 << type;
                    ref Transform t = ref transforms[numTransforms++];
                    t.Type = type; t.XSize = xsize; t.Data = Array.Empty<uint>();
                    switch (type)
                    {
                        case 0:
                        case 1:
                            t.Bits = (int)_br.ReadBits(3) + 2;
                            t.Data = DecodeImageStream(SubSize(xsize, t.Bits), SubSize(ysize, t.Bits), false);
                            break;
                        case 3:
                        {
                            int n = (int)_br.ReadBits(8) + 1;
                            t.Bits = n > 16 ? 0 : n > 4 ? 1 : n > 2 ? 2 : 3;
                            var pal = DecodeImageStream(n, 1, false);
                            var table = new uint[256];      // entries past the end read as transparent black
                            uint prev = 0;
                            for (int i = 0; i < n; i++) { prev = AddPixels(pal[i], prev); table[i] = prev; }
                            t.Data = table;
                            xsize = SubSize(xsize, t.Bits);
                            break;
                        }
                    }
                }
            }

            int cacheBits = 0;
            if (_br.ReadBits(1) != 0)
            {
                cacheBits = (int)_br.ReadBits(4);
                if (cacheBits < 1 || cacheBits > 11) throw Bad("color cache size");
            }

            // meta prefix codes
            int prefixBits = 0, metaWidth = 0, numGroups = 1;
            uint[]? meta = null;
            int[]? mapping = null;
            if (level0 && _br.ReadBits(1) != 0)
            {
                prefixBits = (int)_br.ReadBits(3) + 2;
                metaWidth = SubSize(xsize, prefixBits);
                meta = DecodeImageStream(metaWidth, SubSize(ysize, prefixBits), false);
                int max = 0;
                for (int i = 0; i < meta.Length; i++)
                {
                    int g = (int)(meta[i] >> 8) & 0xffff;
                    meta[i] = (uint)g;
                    if (g > max) max = g;
                }
                // Only groups that are referenced get tables: a tiny file may claim 65536 groups.
                mapping = new int[max + 1];
                Array.Fill(mapping, -1);
                numGroups = 0;
                for (int i = 0; i < meta.Length; i++)
                {
                    ref int m = ref mapping[meta[i]];
                    if (m < 0) m = numGroups++;
                    meta[i] = (uint)m * GroupInts;
                }
            }

            var groups = new int[numGroups * GroupInts];
            var tables = ReadPrefixCodes(groups, mapping, cacheBits);
            // color indexing unpacks in place when the buffer has room for the full width
            var pixels = DecodePixels(xsize, ysize, groups, tables, meta, prefixBits, metaWidth, cacheBits, level0 ? (long)fullWidth * ysize : 0);

            if (level0)
            {
                for (int i = numTransforms - 1; i >= 0; i--)
                {
                    ref Transform t = ref transforms![i];
                    switch (t.Type)
                    {
                        case 0: InversePredictor(pixels, t.XSize, ysize, t.Bits, t.Data); break;
                        case 1: InverseColorTransform(pixels, t.XSize, ysize, t.Bits, t.Data); break;
                        case 2: AddGreen(pixels, t.XSize * ysize); break;
                        default: pixels = InverseColorIndexing(pixels, t.XSize, ysize, t.Bits, t.Data); break;
                    }
                }
                if (pixels.Length < (long)fullWidth * ysize) throw Bad("size");
            }
            return pixels;
        }

        // ------------------------------------------------------------------ prefix codes

        static ReadOnlySpan<byte> CodeLengthOrder => new byte[19] { 17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

        uint[] ReadPrefixCodes(int[] groups, int[]? mapping, int cacheBits)
        {
            int numCoded = mapping?.Length ?? 1;
            int greenSize = NumLiteral + NumLength + (cacheBits > 0 ? 1 << cacheBits : 0);
            var lengths = new byte[greenSize];
            var pool = new TablePool();
            for (int gi = 0; gi < numCoded; gi++)
            {
                int target = mapping == null ? 0 : mapping[gi];
                for (int k = 0; k < 5; k++)
                {
                    int alphabet = k == 0 ? greenSize : k == 4 ? NumDistance : NumLiteral;
                    var lens = lengths.AsSpan(0, alphabet);
                    ReadCodeLengths(lens);
                    if (target < 0) { CheckCode(lens); continue; }
                    BuildTable(lens, pool, out int off, out int mask);
                    groups[target * GroupInts + k * 2] = off;
                    groups[target * GroupInts + k * 2 + 1] = mask;
                }
                if (target < 0) continue;
                // codes that need no bits let the pixel loop skip table lookups
                ref int g = ref groups[target * GroupInts];
                int flags = 0;
                uint constant = 0;
                if (Unsafe.Add(ref g, 3) == 0 && Unsafe.Add(ref g, 5) == 0 && Unsafe.Add(ref g, 7) == 0)
                {
                    // a root mask of 0 means a single symbol
                    uint r = pool.Data[Unsafe.Add(ref g, 2)] >> 8, b = pool.Data[Unsafe.Add(ref g, 4)] >> 8, a = pool.Data[Unsafe.Add(ref g, 6)] >> 8;
                    flags = FlagTrivialLiteral;
                    constant = a << 24 | r << 16 | b;
                    uint ge = pool.Data[g] >> 8;
                    if (Unsafe.Add(ref g, 1) == 0 && ge < NumLiteral)
                    {
                        flags |= FlagTrivialPixel;
                        constant |= ge << 8;
                    }
                }
                Unsafe.Add(ref g, 10) = flags;
                Unsafe.Add(ref g, 11) = (int)constant;
            }
            return pool.Data;
        }

        void ReadCodeLengths(Span<byte> lens)
        {
            lens.Clear();
            if (_br.ReadBits(1) != 0)
            {
                // simple code: one or two symbols
                int n = (int)_br.ReadBits(1) + 1;
                int s0 = (int)_br.ReadBits(_br.ReadBits(1) != 0 ? 8 : 1);
                if (s0 >= lens.Length) throw Bad("prefix code symbol");
                lens[s0] = 1;
                if (n == 2)
                {
                    int s1 = (int)_br.ReadBits(8);
                    if (s1 >= lens.Length) throw Bad("prefix code symbol");
                    lens[s1] = 1;
                }
                return;
            }

            Span<byte> clLens = stackalloc byte[19];
            clLens.Clear();
            int numCodes = 4 + (int)_br.ReadBits(4);
            for (int i = 0; i < numCodes; i++) clLens[CodeLengthOrder[i]] = (byte)_br.ReadBits(3);
            _scratch.Count = 0;
            BuildTable(clLens, _scratch, out int off, out int mask);
            var table = _scratch.Data;

            int maxSymbol = lens.Length;
            if (_br.ReadBits(1) != 0)
            {
                int nbits = 2 + 2 * (int)_br.ReadBits(3);
                maxSymbol = 2 + (int)_br.ReadBits(nbits);
                if (maxSymbol > lens.Length) throw Bad("prefix code length count");
            }
            int sym = 0, prev = 8;
            while (sym < lens.Length && maxSymbol-- > 0)
            {
                _br.Fill();
                uint e = table[off + (int)((uint)_br.Val & (uint)mask)];
                _br.Skip((int)(e & 0xff));
                if (_br.Overrun) throw Bad("data ends prematurely");
                int len = (int)(e >> 8);
                if (len < 16)
                {
                    lens[sym++] = (byte)len;
                    if (len != 0) prev = len;
                }
                else
                {
                    int repeat = len == 16 ? 3 + (int)_br.ReadBits(2) : len == 17 ? 3 + (int)_br.ReadBits(3) : 11 + (int)_br.ReadBits(7);
                    if (sym + repeat > lens.Length) throw Bad("prefix code lengths");
                    lens.Slice(sym, repeat).Fill((byte)(len == 16 ? prev : 0));
                    sym += repeat;
                }
            }
        }

        sealed class TablePool
        {
            public uint[] Data;
            public int Count;
            public TablePool(int capacity = 1024) { Data = new uint[capacity]; }

            public int Alloc(int n)
            {
                if (Count + n > Data.Length) Array.Resize(ref Data, Math.Max(Data.Length * 2, Count + n));
                int off = Count;
                Count += n;
                return off;
            }
        }

        /// <summary>Number of symbols with a code, or throws when the lengths do not describe a complete prefix code.</summary>
        static int CheckCode(ReadOnlySpan<byte> lens)
        {
            int used = 0, kraft = 0;
            for (int i = 0; i < lens.Length; i++)
                if (lens[i] != 0) { used++; kraft += 1 << (15 - lens[i]); }
            if (used > 1 && kraft != 1 << 15) throw Bad("incomplete prefix code");
            return used;
        }

        static uint Reverse(uint v, int bits)
        {
            uint r = 0;
            for (int i = 0; i < bits; i++) { r = (r << 1) | (v & 1); v >>= 1; }
            return r;
        }

        /// <summary>
        /// Builds a two level lookup table for a canonical prefix code read LSB first. Entries are value &lt;&lt; 8 | bits;
        /// a root entry with bit 7 set points to a sub table (offset relative to the root &lt;&lt; 8 | 0x80 | index bits).
        /// The root has 2^min(8, longest code) entries, so trivial codes cost almost nothing.
        /// </summary>
        static void BuildTable(ReadOnlySpan<byte> lens, TablePool pool, out int offset, out int mask)
        {
            int used = CheckCode(lens);
            if (used <= 1)
            {
                // a single symbol (or none at all) takes no bits
                int s = 0;
                for (int i = 0; i < lens.Length; i++) if (lens[i] != 0) { s = i; break; }
                offset = pool.Alloc(1);
                pool.Data[offset] = (uint)s << 8;
                mask = 0;
                return;
            }
            Span<int> count = stackalloc int[16];
            count.Clear();
            int maxLen = 0;
            for (int i = 0; i < lens.Length; i++) { count[lens[i]]++; if (lens[i] > maxLen) maxLen = lens[i]; }
            Span<uint> next = stackalloc uint[16];
            uint code = 0;
            for (int l = 1; l <= 15; l++) { next[l] = code; code = (code + (uint)count[l]) << 1; }

            int root = Math.Min(maxLen, RootBits);
            int rootSize = 1 << root;
            offset = pool.Alloc(rootSize);
            mask = rootSize - 1;

            if (maxLen > root)
            {
                // size every sub table by the longest code below its root prefix
                Span<byte> subLen = stackalloc byte[256];
                subLen.Clear();
                Span<uint> nx = stackalloc uint[16];
                next.CopyTo(nx);
                for (int i = 0; i < lens.Length; i++)
                {
                    int l = lens[i];
                    if (l == 0) continue;
                    uint c = nx[l]++;
                    if (l > root)
                    {
                        int p = (int)Reverse(c >> (l - root), root);
                        if (l > subLen[p]) subLen[p] = (byte)l;
                    }
                }
                for (int p = 0; p < 256; p++)
                {
                    if (subLen[p] == 0) continue;
                    int bits = subLen[p] - root;
                    int sub = pool.Alloc(1 << bits);
                    pool.Data[offset + p] = (uint)(sub - offset) << 8 | 0x80u | (uint)bits;
                }
            }

            var t = pool.Data;
            for (int i = 0; i < lens.Length; i++)
            {
                int l = lens[i];
                if (l == 0) continue;
                uint c = next[l]++;
                if (l <= root)
                {
                    uint entry = (uint)i << 8 | (uint)l;
                    for (int k = (int)Reverse(c, l); k < rootSize; k += 1 << l) t[offset + k] = entry;
                }
                else
                {
                    uint ptr = t[offset + (int)Reverse(c >> (l - root), root)];
                    int bits = (int)(ptr & 0x7f), sub = offset + (int)(ptr >> 8);
                    int rest = l - root;
                    uint entry = (uint)i << 8 | (uint)rest;
                    for (int k = (int)Reverse(c & ((1u << rest) - 1), rest); k < 1 << bits; k += 1 << rest) t[sub + k] = entry;
                }
            }
        }

        // ------------------------------------------------------------------ entropy coded pixels

        static ReadOnlySpan<byte> DistanceMap => new byte[120]
        {
            0x18, 0x07, 0x17, 0x19, 0x28, 0x06, 0x27, 0x29, 0x16, 0x1a, 0x26, 0x2a, 0x38, 0x05, 0x37, 0x39, 0x15, 0x1b, 0x36, 0x3a,
            0x25, 0x2b, 0x48, 0x04, 0x47, 0x49, 0x14, 0x1c, 0x35, 0x3b, 0x46, 0x4a, 0x24, 0x2c, 0x58, 0x45, 0x4b, 0x34, 0x3c, 0x03,
            0x57, 0x59, 0x13, 0x1d, 0x56, 0x5a, 0x23, 0x2d, 0x44, 0x4c, 0x55, 0x5b, 0x33, 0x3d, 0x68, 0x02, 0x67, 0x69, 0x12, 0x1e,
            0x66, 0x6a, 0x22, 0x2e, 0x54, 0x5c, 0x43, 0x4d, 0x65, 0x6b, 0x32, 0x3e, 0x78, 0x01, 0x77, 0x79, 0x53, 0x5d, 0x11, 0x1f,
            0x64, 0x6c, 0x42, 0x4e, 0x76, 0x7a, 0x21, 0x2f, 0x75, 0x7b, 0x31, 0x3f, 0x63, 0x6d, 0x52, 0x5e, 0x00, 0x74, 0x7c, 0x41,
            0x4f, 0x10, 0x20, 0x62, 0x6e, 0x30, 0x73, 0x7d, 0x51, 0x5f, 0x40, 0x72, 0x7e, 0x61, 0x6f, 0x50, 0x71, 0x7f, 0x60, 0x70,
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint ReadSymbol(ref ulong val, ref int bits, uint* tables, int off, int mask)
        {
            uint e = tables[off + ((uint)val & (uint)mask)];
            if ((e & 0x80) != 0)
            {
                val >>= RootBits; bits -= RootBits;
                e = tables[off + (e >> 8) + ((uint)val & ((1u << (int)(e & 0x7f)) - 1))];
            }
            int n = (int)(e & 0xff);
            val >>= n; bits -= n;
            return e >> 8;
        }

        uint[] DecodePixels(int xsize, int ysize, int[] groups, uint[] tables, uint[]? meta, int prefixBits, int metaWidth, int cacheBits, long room)
        {
            long total = (long)xsize * ysize;
            if (total > _maxPixels) throw new ImageNotSupportedException($"WebP image exceeds WebpDecodeOptions.MaxPixels ({_maxPixels})");
            int end = (int)total;
            // Sized at once when the data could plausibly hold that many pixels; otherwise the buffer grows with the
            // decoded pixels, so that a header lying about the size cannot allocate much.
            const int Chunk = 1 << 16;
            const int PixelsPerByte = 16;
            // (the main image's buffer comes from the pool, the small transform images' are kept by the transforms)
            int first = end > Math.Max(Chunk, (long)_length * PixelsPerByte) ? Math.Min(end, Chunk) : (int)Math.Max(end, room);
            var data = room > 0 ? _rented = ArrayPool<uint>.Shared.Rent(first) : new uint[first];
            uint[]? cache = cacheBits > 0 ? new uint[1 << cacheBits] : null;
            int cacheShift = 32 - cacheBits;

            // the reader state lives in locals while the pixels are decoded
            ulong val = _br.Val;
            int bits = _br.Bits;
            byte* cur = _br.Cur, bufEnd = _br.End;
            int pos = 0;
            int blockSize = meta == null ? 0 : 1 << prefixBits;
            fixed (uint* t = tables, pm = meta, pc = cache)
            fixed (int* gs = groups)
            {
                int* g = gs;
                while (pos < end)
                {
                    int limit = Math.Min(end, data.Length);
                    int pendingLength = 0, pendingDist = 0;
                    fixed (uint* d = data)
                    {
                        // The loop keeps as little as possible alive: output pointers instead of coordinates, and a
                        // countdown to the next position where the prefix code group can change.
                        uint* dp = d + pos, dEnd = d + limit;
                        int until = 0;
                        while (dp < dEnd)
                        {
                            if (bits < 0) throw Bad("data ends prematurely");
                            if (until == 0)
                            {
                                until = int.MaxValue;
                                if (pm != null)
                                {
                                    int p0 = (int)(dp - d), x = p0 % xsize, y = p0 / xsize;
                                    g = gs + pm[(y >> prefixBits) * metaWidth + (x >> prefixBits)];
                                    until = Math.Min(blockSize - (x & (blockSize - 1)), xsize - x);
                                }
                            }
                            Vp8lBitReader.Fill(ref val, ref bits, ref cur, bufEnd);
                            uint px;
                            if ((g[10] & FlagTrivialPixel) != 0) px = (uint)g[11];
                            else
                            {
                                uint sym = ReadSymbol(ref val, ref bits, t, g[0], g[1]);
                                if (sym < NumLiteral)
                                {
                                    if ((g[10] & FlagTrivialLiteral) != 0) px = (uint)g[11] | sym << 8;
                                    else
                                    {
                                        // a refill guarantees 33 bits, enough for two symbols
                                        uint r = ReadSymbol(ref val, ref bits, t, g[2], g[3]);
                                        Vp8lBitReader.Fill(ref val, ref bits, ref cur, bufEnd);
                                        uint b = ReadSymbol(ref val, ref bits, t, g[4], g[5]);
                                        uint a = g[7] == 0 ? t[g[6]] >> 8 : ReadSymbol(ref val, ref bits, t, g[6], g[7]);
                                        px = a << 24 | r << 16 | sym << 8 | b;
                                    }
                                }
                                else if (sym < NumLiteral + NumLength)
                                {
                                    // backward reference: length, then the distance code
                                    int length = (int)sym - NumLiteral + 1;
                                    if (length > 4)
                                    {
                                        int extra = (length - 3) >> 1;
                                        length = ((2 + ((length - 1) & 1)) << extra) + (int)((uint)val & ((1u << extra) - 1)) + 1;
                                        val >>= extra; bits -= extra;
                                    }
                                    Vp8lBitReader.Fill(ref val, ref bits, ref cur, bufEnd);
                                    int dist = (int)ReadSymbol(ref val, ref bits, t, g[8], g[9]) + 1;
                                    if (dist > 4)
                                    {
                                        int extra = (dist - 3) >> 1;
                                        dist = ((2 + ((dist - 1) & 1)) << extra) + (int)((uint)val & ((1u << extra) - 1)) + 1;
                                        val >>= extra; bits -= extra;
                                    }
                                    if (dist > 120) dist -= 120;
                                    else
                                    {
                                        int m = DistanceMap[dist - 1];
                                        dist = (m >> 4) * xsize + (8 - (m & 15));
                                        if (dist < 1) dist = 1;
                                    }
                                    if (bits < 0) throw Bad("data ends prematurely");
                                    pos = (int)(dp - d);
                                    if (dist > pos || length > end - pos) throw Bad("backward reference out of range");
                                    if (length > limit - pos)
                                    {
                                        // does not fit the buffer yet: grow it first
                                        pendingLength = length; pendingDist = dist;
                                        break;
                                    }
                                    uint* src = dp - dist;
                                    if (dist >= length) Buffer.MemoryCopy(src, dp, (long)length * 4, (long)length * 4);
                                    else if (dist == 1) new Span<uint>(dp, length).Fill(*src);
                                    else
                                        for (int i = 0; i < length; i++) dp[i] = src[i];
                                    if (pc != null)
                                        for (int i = 0; i < length; i++) pc[(dp[i] * 0x1e35a7bdu) >> cacheShift] = dp[i];
                                    dp += length;
                                    until = 0;
                                    continue;
                                }
                                else px = pc[sym - (NumLiteral + NumLength)];   // below the cache size by construction of the alphabet
                            }
                            *dp++ = px;
                            if (pc != null) pc[(px * 0x1e35a7bdu) >> cacheShift] = px;
                            until--;
                        }
                        if (pendingLength == 0) pos = (int)(dp - d);
                    }
                    if (pos >= end) break;
                    int size = (int)Math.Min(end, Math.Max((long)data.Length * 2, (long)pos + pendingLength));
                    if (room == 0) Array.Resize(ref data, size);
                    else
                    {
                        var bigger = ArrayPool<uint>.Shared.Rent(size == end ? (int)Math.Max(end, room) : size);
                        data.AsSpan(0, pos).CopyTo(bigger);
                        ArrayPool<uint>.Shared.Return(data);
                        data = _rented = bigger;
                    }
                    if (pendingLength > 0)
                    {
                        if (pendingDist >= pendingLength) data.AsSpan(pos - pendingDist, pendingLength).CopyTo(data.AsSpan(pos));
                        else
                            for (int i = 0; i < pendingLength; i++) data[pos + i] = data[pos + i - pendingDist];
                        if (pc != null)
                            for (int i = 0; i < pendingLength; i++) pc[(data[pos + i] * 0x1e35a7bdu) >> cacheShift] = data[pos + i];
                        pos += pendingLength;
                    }
                }
            }
            if (bits < 0) throw Bad("data ends prematurely");
            _br.Val = val; _br.Bits = bits; _br.Cur = cur;
            return data;
        }

        // ------------------------------------------------------------------ inverse transforms

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint AddPixels(uint a, uint b)
            => (((a & 0xff00ff00) + (b & 0xff00ff00)) & 0xff00ff00) | (((a & 0x00ff00ff) + (b & 0x00ff00ff)) & 0x00ff00ff);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint Average2(uint a, uint b) => (((a ^ b) & 0xfefefefe) >> 1) + (a & b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint Select(uint l, uint t, uint tl)
        {
            // Manhattan distance of the gradient estimate L + T - TL to L is the distance of T to TL and vice versa
            int pl = Dist(t, tl), pt = Dist(l, tl);
            return pl < pt ? l : t;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Dist(uint a, uint b)
            => Math.Abs((int)(a >> 24) - (int)(b >> 24)) + Math.Abs((int)((a >> 16) & 0xff) - (int)((b >> 16) & 0xff))
             + Math.Abs((int)((a >> 8) & 0xff) - (int)((b >> 8) & 0xff)) + Math.Abs((int)(a & 0xff) - (int)(b & 0xff));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint Clamp8(int v) => (uint)v <= 255 ? (uint)v : v < 0 ? 0u : 255u;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint ClampAddSubFull(uint a, uint b, uint c)
            => Clamp8((int)(a >> 24) + (int)(b >> 24) - (int)(c >> 24)) << 24
             | Clamp8((int)((a >> 16) & 0xff) + (int)((b >> 16) & 0xff) - (int)((c >> 16) & 0xff)) << 16
             | Clamp8((int)((a >> 8) & 0xff) + (int)((b >> 8) & 0xff) - (int)((c >> 8) & 0xff)) << 8
             | Clamp8((int)(a & 0xff) + (int)(b & 0xff) - (int)(c & 0xff));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint HalfChannel(int a, int b) => Clamp8(a + (a - b) / 2);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint ClampAddSubHalf(uint a, uint b)
            => HalfChannel((int)(a >> 24), (int)(b >> 24)) << 24
             | HalfChannel((int)((a >> 16) & 0xff), (int)((b >> 16) & 0xff)) << 16
             | HalfChannel((int)((a >> 8) & 0xff), (int)((b >> 8) & 0xff)) << 8
             | HalfChannel((int)(a & 0xff), (int)(b & 0xff));

        /// <summary>The prediction of mode 0..13 from the left, top, top-left and top-right pixels.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint Predict(int mode, uint l, uint t, uint tl, uint tr)
        {
            switch (mode)
            {
                case 1: return l;
                case 2: return t;
                case 3: return tr;
                case 4: return tl;
                case 5: return Average2(Average2(l, tr), t);
                case 6: return Average2(l, tl);
                case 7: return Average2(l, t);
                case 8: return Average2(tl, t);
                case 9: return Average2(t, tr);
                case 10: return Average2(Average2(l, tl), Average2(t, tr));
                case 11: return Select(l, t, tl);
                case 12: return ClampAddSubFull(l, t, tl);
                case 13: return ClampAddSubHalf(Average2(l, t), tl);
                default: return 0xff000000;
            }
        }

        // ---- byte-wise helpers on vectors (a pixel is four lanes)

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<byte> AddSat(Vector128<byte> a, Vector128<byte> b)
        {
            if (Sse2.IsSupported) return Sse2.AddSaturate(a, b);
            if (AdvSimd.IsSupported) return AdvSimd.AddSaturate(a, b);
            var sum = a + b;
            return sum | Vector128.LessThan(sum, a);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<byte> SubSat(Vector128<byte> a, Vector128<byte> b)
        {
            if (Sse2.IsSupported) return Sse2.SubtractSaturate(a, b);
            if (AdvSimd.IsSupported) return AdvSimd.SubtractSaturate(a, b);
            return Vector128.Max(a, b) - b;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<byte> Half(Vector128<byte> a) => Vector128.ShiftRightLogical((a & Vector128.Create((byte)0xfe)).AsUInt16(), 1).AsByte();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<byte> Avg(Vector128<byte> a, Vector128<byte> b) => (a & b) + Half(a ^ b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<byte> Px(uint v) => Vector128.CreateScalarUnsafe(v).AsByte();

        static void InversePredictor(uint[] px, int w, int h, int bits, uint[] modes)
        {
            if (px.Length < (long)w * h) throw Bad("size");
            int tw = SubSize(w, bits);
            px[0] = AddPixels(px[0], 0xff000000);
            for (int x = 1; x < w; x++) px[x] = AddPixels(px[x], px[x - 1]);
            bool simd = Vector128.IsHardwareAccelerated;
            fixed (uint* p0 = px)
            {
                for (int y = 1; y < h; y++)
                {
                    uint* cur = p0 + (long)y * w, up = cur - w;
                    cur[0] = AddPixels(cur[0], up[0]);
                    int mrow = (y >> bits) * tw;
                    for (int x = 1; x < w;)
                    {
                        int mode = (int)(modes[mrow + (x >> bits)] >> 8) & 15;
                        int xe = Math.Min(w, ((x >> bits) + 1) << bits);
                        // for the last pixel of a row up[x + 1] is the first pixel of the current row, as the format wants it
                        if (simd)
                        {
                            // Predictors that only look at the row above take four pixels at a time. The others
                            // depend on the pixel just decoded; the costly ones compute their four channels in one vector.
                            switch (mode)
                            {
                                case 2: for (; x + 4 <= xe; x += 4) (Vector128.Load((byte*)(cur + x)) + Vector128.Load((byte*)(up + x))).Store((byte*)(cur + x)); break;
                                case 3: for (; x + 4 <= xe; x += 4) (Vector128.Load((byte*)(cur + x)) + Vector128.Load((byte*)(up + x + 1))).Store((byte*)(cur + x)); break;
                                case 4: for (; x + 4 <= xe; x += 4) (Vector128.Load((byte*)(cur + x)) + Vector128.Load((byte*)(up + x - 1))).Store((byte*)(cur + x)); break;
                                case 8: for (; x + 4 <= xe; x += 4) (Vector128.Load((byte*)(cur + x)) + Avg(Vector128.Load((byte*)(up + x - 1)), Vector128.Load((byte*)(up + x)))).Store((byte*)(cur + x)); break;
                                case 9: for (; x + 4 <= xe; x += 4) (Vector128.Load((byte*)(cur + x)) + Avg(Vector128.Load((byte*)(up + x)), Vector128.Load((byte*)(up + x + 1)))).Store((byte*)(cur + x)); break;
                                case 11:
                                    if (Sse2.IsSupported)
                                    {
                                        uint l = cur[x - 1];
                                        for (; x < xe; x++)
                                        {
                                            uint t = up[x];
                                            var tl = Px(up[x - 1]);
                                            // the gradient estimate L + T - TL is as far from L as T is from TL
                                            int toLeft = Sse2.SumAbsoluteDifferences(Px(t), tl).AsInt32().ToScalar();
                                            int toTop = Sse2.SumAbsoluteDifferences(Px(l), tl).AsInt32().ToScalar();
                                            cur[x] = l = AddPixels(cur[x], toLeft < toTop ? l : t);
                                        }
                                    }
                                    break;
                                case 12:
                                {
                                    var l = Px(cur[x - 1]);
                                    for (; x < xe; x++)
                                    {
                                        var t = Px(up[x]);
                                        var tl = Px(up[x - 1]);
                                        // clamp(L + T - TL): add what T has above TL, take off what it has below
                                        l = Px(cur[x]) + SubSat(AddSat(l, SubSat(t, tl)), SubSat(tl, t));
                                        cur[x] = l.AsUInt32().ToScalar();
                                    }
                                    break;
                                }
                                case 13:
                                {
                                    var l = Px(cur[x - 1]);
                                    for (; x < xe; x++)
                                    {
                                        var a = Avg(l, Px(up[x]));
                                        var tl = Px(up[x - 1]);
                                        // clamp(a + (a - TL) / 2), the division truncating towards zero
                                        l = Px(cur[x]) + SubSat(AddSat(a, Half(SubSat(a, tl))), Half(SubSat(tl, a)));
                                        cur[x] = l.AsUInt32().ToScalar();
                                    }
                                    break;
                                }
                            }
                        }
                        switch (mode)
                        {
                            case 0: for (; x < xe; x++) cur[x] = AddPixels(cur[x], 0xff000000); break;
                            case 1: { uint l = cur[x - 1]; for (; x < xe; x++) cur[x] = l = AddPixels(cur[x], l); break; }
                            case 2: for (; x < xe; x++) cur[x] = AddPixels(cur[x], up[x]); break;
                            case 3: for (; x < xe; x++) cur[x] = AddPixels(cur[x], up[x + 1]); break;
                            case 4: for (; x < xe; x++) cur[x] = AddPixels(cur[x], up[x - 1]); break;
                            case 5: for (; x < xe; x++) cur[x] = AddPixels(cur[x], Average2(Average2(cur[x - 1], up[x + 1]), up[x])); break;
                            case 6: for (; x < xe; x++) cur[x] = AddPixels(cur[x], Average2(cur[x - 1], up[x - 1])); break;
                            case 7: for (; x < xe; x++) cur[x] = AddPixels(cur[x], Average2(cur[x - 1], up[x])); break;
                            case 8: for (; x < xe; x++) cur[x] = AddPixels(cur[x], Average2(up[x - 1], up[x])); break;
                            case 9: for (; x < xe; x++) cur[x] = AddPixels(cur[x], Average2(up[x], up[x + 1])); break;
                            case 10: for (; x < xe; x++) cur[x] = AddPixels(cur[x], Average2(Average2(cur[x - 1], up[x - 1]), Average2(up[x], up[x + 1]))); break;
                            case 11: for (; x < xe; x++) cur[x] = AddPixels(cur[x], Select(cur[x - 1], up[x], up[x - 1])); break;
                            case 12: for (; x < xe; x++) cur[x] = AddPixels(cur[x], ClampAddSubFull(cur[x - 1], up[x], up[x - 1])); break;
                            case 13: for (; x < xe; x++) cur[x] = AddPixels(cur[x], ClampAddSubHalf(Average2(cur[x - 1], up[x]), up[x - 1])); break;
                            default: for (; x < xe; x++) cur[x] = AddPixels(cur[x], 0xff000000); break;
                        }
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int ColorDelta(sbyte t, sbyte c) => (t * c) >> 5;

        static void InverseColorTransform(uint[] px, int w, int h, int bits, uint[] elems)
        {
            if (px.Length < (long)w * h) throw Bad("size");
            int tw = SubSize(w, bits);
            bool simd = Vector128.IsHardwareAccelerated;
            fixed (uint* p0 = px)
            {
                for (int y = 0; y < h; y++)
                {
                    uint* row = p0 + (long)y * w;
                    int erow = (y >> bits) * tw;
                    for (int x = 0; x < w;)
                    {
                        uint e = elems[erow + (x >> bits)];
                        sbyte g2r = (sbyte)e, g2b = (sbyte)(e >> 8), r2b = (sbyte)(e >> 16);
                        int xe = Math.Min(w, ((x >> bits) + 1) << bits);
                        if (simd)
                        {
                            var keep = Vector128.Create(0xff00ff00u).AsInt32();
                            var low = Vector128.Create(0xff);
                            for (; x + 4 <= xe; x += 4)
                            {
                                var v = Vector128.Load((int*)(row + x));
                                var g = Vector128.ShiftRightArithmetic(Vector128.ShiftLeft(v, 16), 24);        // green, sign extended
                                var r = Vector128.ShiftRightLogical(v, 16) + Vector128.ShiftRightArithmetic(g * g2r, 5);
                                var rs = Vector128.ShiftRightArithmetic(Vector128.ShiftLeft(r, 24), 24);       // the new red as signed byte
                                var b = v + Vector128.ShiftRightArithmetic(g * g2b, 5) + Vector128.ShiftRightArithmetic(rs * r2b, 5);
                                ((v & keep) | Vector128.ShiftLeft(r & low, 16) | (b & low)).Store((int*)(row + x));
                            }
                        }
                        for (; x < xe; x++)
                        {
                            uint v = row[x];
                            sbyte g = (sbyte)(v >> 8);
                            int r = (int)(v >> 16) + ColorDelta(g2r, g);
                            int b = (int)v + ColorDelta(g2b, g) + ColorDelta(r2b, (sbyte)r);
                            row[x] = (v & 0xff00ff00) | (uint)(r & 0xff) << 16 | (uint)(b & 0xff);
                        }
                    }
                }
            }
        }

        static void AddGreen(uint[] px, int n)
        {
            int i = 0;
            if (Vector128.IsHardwareAccelerated)
            {
                fixed (uint* p = px)
                    for (; i + 4 <= n; i += 4)
                    {
                        var v = Vector128.Load(p + i);
                        var g = Vector128.ShiftRightLogical(v, 8) & Vector128.Create(0xffu);
                        // green lands in the red and blue bytes; the byte-wise sum wraps as it should
                        (v.AsByte() + (g | Vector128.ShiftLeft(g, 16)).AsByte()).Store((byte*)(p + i));
                    }
            }
            for (; i < n; i++)
            {
                uint v = px[i], g = (v >> 8) & 0xff;
                px[i] = (v & 0xff00ff00) | (((v & 0x00ff00ff) + (g << 16 | g)) & 0x00ff00ff);
            }
        }

        static uint[] InverseColorIndexing(uint[] px, int w, int h, int bits, uint[] table)
        {
            if (bits == 0)
            {
                if (px.Length < (long)w * h) throw Bad("size");
                for (int i = 0; i < w * h; i++) px[i] = table[(px[i] >> 8) & 0xff];
                return px;
            }
            int pw = SubSize(w, bits);
            if (px.Length < (long)pw * h) throw Bad("size");
            // In place when there is room: from the last pixel backwards, every write lands at or after the packed
            // pixel it comes from and after all packed pixels still to be read.
            var res = px.Length >= (long)w * h ? px : new uint[w * h];
            int per = 1 << bits, bpp = 8 >> bits;
            uint m = (1u << bpp) - 1;
            for (int y = h - 1; y >= 0; y--)
            {
                int so = y * pw, d0 = y * w;
                for (int x = w - 1; x >= 0; x--)
                    res[d0 + x] = table[(int)(((px[so + (x >> bits)] >> 8) & 0xff) >> ((x & (per - 1)) * bpp) & m)];
            }
            return res;
        }
    }
}
