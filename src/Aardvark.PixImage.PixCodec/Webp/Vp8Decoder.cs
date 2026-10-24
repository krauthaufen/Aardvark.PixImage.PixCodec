using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Aardvark.ImageCodecs
{
    /// <summary>The boolean entropy decoder of RFC 6386 section 7. Past the end of its data it reads zero bits.</summary>
    internal unsafe struct Vp8BoolReader
    {
        // The fields are internal so that the token loop can keep them in registers (see Vp8Decoder.GetCoeffs).
        internal byte* _cur, _end;
        internal ulong _value;      // the low _bits + 8 bits are valid
        internal uint _range;       // range - 1, 127..254 when normalized
        internal int _bits;
        public bool Eof;

        public Vp8BoolReader(byte* data, int length)
        {
            _cur = data; _end = data + length;
            _value = 0; _range = 254; _bits = -8; Eof = false;
            Load();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Load()
        {
            if (_end - _cur >= 8)
            {
                ulong b = BinaryPrimitives.ReadUInt64BigEndian(new ReadOnlySpan<byte>(_cur, 8)) >> 8;
                _cur += 7;
                _value = (_value << 56) | b;
                _bits += 56;
            }
            else if (_cur < _end)
            {
                _value = (_value << 8) | *_cur++;
                _bits += 8;
            }
            else
            {
                _value <<= 8;
                _bits += 8;
                Eof = true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetBit(int prob)
        {
            if (_bits < 0) Load();
            uint range = _range;
            uint split = (range * (uint)prob) >> 8;
            uint v = (uint)(_value >> _bits);
            int bit;
            if (v > split)
            {
                range -= split + 1;
                _value -= (ulong)(split + 1) << _bits;
                bit = 1;
            }
            else
            {
                range = split;
                bit = 0;
            }
            if (range < 127)
            {
                int shift = BitOperations.LeadingZeroCount(range + 1) - 24;
                range = ((range + 1) << shift) - 1;
                _bits -= shift;
            }
            _range = range;
            return bit;
        }

        /// <summary>
        /// <see cref="GetBit"/> on reader state held in locals of the caller. There is deliberately no call in here:
        /// state that lives across a call ends up on the stack, and that load/store chain is slower than the arithmetic.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int Bit(ref ulong value, ref uint range, ref int bits, ref byte* cur, byte* end, int prob)
        {
            if (bits < 0)
            {
                if (end - cur >= 8)
                {
                    value = (value << 56) | (BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(cur)) >> 8);
                    cur += 7;
                    bits += 56;
                }
                else
                {
                    // the last bytes one at a time, then zeros
                    value <<= 8;
                    if (cur < end) value |= *cur++;
                    bits += 8;
                }
            }
            uint r = range;
            uint split = (r * (uint)prob) >> 8;
            uint v = (uint)(value >> bits);
            // The decision is a branch on purpose: predicted, it takes the comparison (which waits for the value
            // window) off the chain from one range to the next. The normalization has no pattern to predict and
            // stays branch-free.
            int bit;
            if (v > split)
            {
                r -= split + 1;
                value -= (ulong)(split + 1) << bits;
                bit = 1;
            }
            else
            {
                r = split;
                bit = 0;
            }
            int shift = BitOperations.LeadingZeroCount(r + 1) - 24;
            r = ((r + 1) << shift) - 1;
            bits -= shift;
            range = r;
            return bit;
        }

        /// <summary>
        /// <paramref name="v"/> negated if the next bit at probability 1/2 is set. The sign of a coefficient is as good as
        /// random, so a branch here would be mispredicted every other time: this one decides with masks instead.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int Sign(ref ulong value, ref uint range, ref int bits, ref byte* cur, byte* end, int v)
        {
            if (bits < 0)
            {
                if (end - cur >= 8)
                {
                    value = (value << 56) | (BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(cur)) >> 8);
                    cur += 7;
                    bits += 56;
                }
                else
                {
                    value <<= 8;
                    if (cur < end) value |= *cur++;
                    bits += 8;
                }
            }
            uint r = range;
            uint split = r >> 1;                    // (r * 128) >> 8
            uint x = (uint)(value >> bits);         // at most r
            int m = (int)(split - x) >> 31;         // all ones when the bit is 1 (x > split)
            r = split + ((r - 2 * split - 1) & (uint)m);
            value -= ((ulong)(split + 1) << bits) & (ulong)(long)m;
            int shift = BitOperations.LeadingZeroCount(r + 1) - 24;
            range = ((r + 1) << shift) - 1;
            bits -= shift;
            return (v ^ m) - m;
        }

        /// <summary>An unsigned literal of n bits, most significant bit first.</summary>
        public int GetValue(int n)
        {
            int v = 0;
            while (n-- > 0) v = (v << 1) | GetBit(128);
            return v;
        }

        public int GetSigned(int n)
        {
            int v = GetValue(n);
            return GetBit(128) != 0 ? -v : v;
        }
    }

    /// <summary>The decoded planes of a VP8 key frame, padded to whole macroblocks.</summary>
    internal sealed class Vp8Frame
    {
        public int Width, Height;
        public int YStride, UVStride;
        public byte[] Y = Array.Empty<byte>(), U = Array.Empty<byte>(), V = Array.Empty<byte>();
        bool _rented;

        /// <summary>Planes from the shared pool (longer than the frame needs); <see cref="Release"/> hands them back.</summary>
        public static Vp8Frame Rent(int width, int height, int mbW, int mbH)
        {
            var pool = System.Buffers.ArrayPool<byte>.Shared;
            return new Vp8Frame
            {
                Width = width, Height = height, YStride = mbW * 16, UVStride = mbW * 8, _rented = true,
                Y = pool.Rent(mbW * 16 * mbH * 16), U = pool.Rent(mbW * 8 * mbH * 8), V = pool.Rent(mbW * 8 * mbH * 8),
            };
        }

        public void Release()
        {
            if (!_rented) return;
            _rented = false;
            var pool = System.Buffers.ArrayPool<byte>.Shared;
            pool.Return(Y); pool.Return(U); pool.Return(V);
            Y = U = V = Array.Empty<byte>();
        }
    }

    /// <summary>Decoder for VP8 key frames (RFC 6386), the lossy bitstream of WebP.</summary>
    internal sealed unsafe class Vp8Decoder
    {
        // intra modes: the four whole-block modes share their numbers with the first four sub-block modes
        internal const int DcPred = 0, TmPred = 1, VPred = 2, HPred = 3;
        internal const int BDc = 0, BTm = 1, BVe = 2, BHe = 3, BLd = 4, BRd = 5, BVr = 6, BVl = 7, BHd = 8, BHu = 9;

        // work area for one macroblock with its prediction borders
        internal const int Bps = 32;
        internal const int YOff = Bps + 8, UOff = YOff + 17 * Bps, VOff = UOff + 16;
        internal const int WorkSize = Bps * 26;

        public static bool TryReadHeader(ReadOnlySpan<byte> d, out int width, out int height)
        {
            width = height = 0;
            if (d.Length < 10) return false;
            int tag = d[0] | d[1] << 8 | d[2] << 16;
            if ((tag & 1) != 0 || ((tag >> 1) & 7) > 3 || ((tag >> 4) & 1) == 0) return false;
            if (d[3] != 0x9d || d[4] != 0x01 || d[5] != 0x2a) return false;
            width = (d[6] | d[7] << 8) & 0x3fff;
            height = (d[8] | d[9] << 8) & 0x3fff;
            return width > 0 && height > 0;
        }

        static ImageFormatException Bad(string what) => new ImageFormatException("invalid WebP lossy data: " + what);

        struct Quant
        {
            public int Y1Dc, Y1Ac, Y2Dc, Y2Ac, UvDc, UvAc;
        }

        static byte[]? _zigzagKeepAlive;
        static readonly byte* ZigzagPtr = PinnedZigzag();

        static byte* PinnedZigzag()
        {
            var a = _zigzagKeepAlive = GC.AllocateArray<byte>(16, pinned: true);
            Vp8Tables.Zigzag.CopyTo(a);
            return (byte*)System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(a, 0);
        }

        int _mbW, _mbH;
        // frame header
        bool _useSegment, _updateMap;
        readonly int[] _segQuant = new int[4], _segFilter = new int[4];
        bool _segAbsolute;
        readonly byte[] _segProbs = { 255, 255, 255 };
        bool _simpleFilter, _useLfDelta;
        int _filterLevel, _sharpness;
        readonly int[] _refLfDelta = new int[4], _modeLfDelta = new int[4];
        int _numParts;
        readonly Quant[] _quant = new Quant[4];
        readonly byte[] _probs = new byte[4 * 8 * 3 * 11];
        bool _useSkip;
        int _skipProb;
        // filter strength per segment and "uses 4x4 prediction": limit (0 = off), interior limit, hev threshold
        readonly int[] _fLimit = new int[8], _fInner = new int[8], _fHev = new int[8];

        public static Vp8Frame Decode(ReadOnlySpan<byte> data, long maxPixels, bool threaded = false)
        {
            if (!TryReadHeader(data, out int w, out int h)) throw Bad("frame header");
            if ((long)w * h > maxPixels)
                throw new ImageNotSupportedException($"WebP image of {w}x{h} exceeds WebpDecodeOptions.MaxPixels ({maxPixels})");
            // A decoder keeps its tables and line buffers for the next frame decoded on the same thread.
            var dec = t_cached ?? new Vp8Decoder();
            t_cached = null;
            dec.Reset();
            Vp8Frame frame;
            fixed (byte* p = data) frame = dec.DecodeFrame(p, data.Length, w, h, threaded);
            t_cached = dec;
            return frame;
        }

        [ThreadStatic] static Vp8Decoder? t_cached;

        /// <summary>Back to the state of a new decoder wherever the frame header does not always set it.</summary>
        void Reset()
        {
            _updateMap = false;
            _segAbsolute = false;
            Array.Clear(_segQuant); Array.Clear(_segFilter);
            Array.Clear(_refLfDelta); Array.Clear(_modeLfDelta);
            _segProbs[0] = _segProbs[1] = _segProbs[2] = 255;
        }

        static T[] Sized<T>(T[] a, int n) => a.Length >= n ? a : new T[n];

        Vp8Frame DecodeFrame(byte* data, int length, int width, int height, bool threaded)
        {
            int part0 = (data[0] | data[1] << 8 | data[2] << 16) >> 5;
            if (part0 > length - 10) throw Bad("first partition exceeds the data");
            _mbW = (width + 15) >> 4; _mbH = (height + 15) >> 4;
            // every macroblock takes at least two bits of the first partition, so a tiny file cannot claim a huge frame
            if ((long)_mbW * _mbH > 4L * part0 + 8) throw Bad("too little data for the frame size");

            var br = new Vp8BoolReader(data + 10, part0);
            ParseHeader(ref br);

            // token partitions
            byte* sizes = data + 10 + part0;
            long left = length - 10 - part0 - 3L * (_numParts - 1);
            if (left < 0) throw Bad("partition table exceeds the data");
            var parts = _parts = Sized(_parts, 8);
            byte* start = sizes + 3 * (_numParts - 1);
            for (int i = 0; i < _numParts; i++)
            {
                long size = i < _numParts - 1 ? sizes[3 * i] | sizes[3 * i + 1] << 8 | sizes[3 * i + 2] << 16 : left;
                if (size > left) size = left;
                parts[i] = new Vp8BoolReader(start, (int)size);
                start += size; left -= size;
            }
            if (br.Eof) throw Bad("frame header exceeds the first partition");

            // every macroblock is written before it is read
            var frame = Vp8Frame.Rent(width, height, _mbW, _mbH);
            _br = br;
            // token probabilities by coefficient position instead of band: one lookup less per token
            var bands = Vp8Tables.Bands;
            for (int t = 0; t < 4; t++)
                for (int n = 0; n < 17; n++)
                    Array.Copy(_probs, (t * 8 + bands[n]) * 33, _posProbs, (t * 17 + n) * 33, 33);
            // the contexts above the first row are zero (BDc, no coefficients); the pixel rows are written before use
            _topModes = Sized(_topModes, 4 * _mbW); Array.Clear(_topModes, 0, 4 * _mbW);
            _topNz = Sized(_topNz, 9 * _mbW); Array.Clear(_topNz, 0, 9 * _mbW);
            _topY = Sized(_topY, 16 * _mbW + 4);
            _topU = Sized(_topU, 8 * _mbW);
            _topV = Sized(_topV, 8 * _mbW);
            // Parsing a macroblock row (modes and tokens) and rebuilding its pixels are separate steps that only meet
            // in the row record, so for large frames the parser runs ahead on a second thread.
            if (threaded && _mbW * _mbH >= 2048 && Environment.ProcessorCount > 1 && !OperatingSystem.IsBrowser()) DecodeRowsThreaded(frame);
            else
            {
                var row = _row;
                if (row == null || row.Width < _mbW) row = _row = new MbRow(_mbW);
                // all coefficients are zero after a complete decode, not necessarily after one that failed
                else Array.Clear(row.Coeffs);
                for (int y = 0; y < _mbH; y++)
                {
                    ParseRow(row, y);
                    ReconstructRow(row, y, frame, true);
                }
            }
            return frame;
        }

        void ParseHeader(ref Vp8BoolReader br)
        {
            br.GetBit(128);     // color space
            br.GetBit(128);     // clamping type: pixels are always clamped
            _useSegment = br.GetBit(128) != 0;
            if (_useSegment)
            {
                _updateMap = br.GetBit(128) != 0;
                if (br.GetBit(128) != 0)
                {
                    _segAbsolute = br.GetBit(128) != 0;
                    for (int i = 0; i < 4; i++) _segQuant[i] = br.GetBit(128) != 0 ? br.GetSigned(7) : 0;
                    for (int i = 0; i < 4; i++) _segFilter[i] = br.GetBit(128) != 0 ? br.GetSigned(6) : 0;
                }
                if (_updateMap)
                    for (int i = 0; i < 3; i++) _segProbs[i] = (byte)(br.GetBit(128) != 0 ? br.GetValue(8) : 255);
            }
            _simpleFilter = br.GetBit(128) != 0;
            _filterLevel = br.GetValue(6);
            _sharpness = br.GetValue(3);
            _useLfDelta = br.GetBit(128) != 0;
            if (_useLfDelta && br.GetBit(128) != 0)
            {
                for (int i = 0; i < 4; i++) if (br.GetBit(128) != 0) _refLfDelta[i] = br.GetSigned(6);
                for (int i = 0; i < 4; i++) if (br.GetBit(128) != 0) _modeLfDelta[i] = br.GetSigned(6);
            }
            _numParts = 1 << br.GetValue(2);

            int baseQ = br.GetValue(7);
            int dqY1Dc = br.GetBit(128) != 0 ? br.GetSigned(4) : 0;
            int dqY2Dc = br.GetBit(128) != 0 ? br.GetSigned(4) : 0;
            int dqY2Ac = br.GetBit(128) != 0 ? br.GetSigned(4) : 0;
            int dqUvDc = br.GetBit(128) != 0 ? br.GetSigned(4) : 0;
            int dqUvAc = br.GetBit(128) != 0 ? br.GetSigned(4) : 0;
            var dc = Vp8Tables.DcQLookup;
            var ac = Vp8Tables.AcQLookup;
            for (int s = 0; s < 4; s++)
            {
                int q = baseQ;
                if (_useSegment) q = _segAbsolute ? _segQuant[s] : _segQuant[s] + baseQ;
                ref Quant m = ref _quant[s];
                m.Y1Dc = dc[Math.Clamp(q + dqY1Dc, 0, 127)];
                m.Y1Ac = ac[Math.Clamp(q, 0, 127)];
                m.Y2Dc = dc[Math.Clamp(q + dqY2Dc, 0, 127)] * 2;
                m.Y2Ac = Math.Max(8, ac[Math.Clamp(q + dqY2Ac, 0, 127)] * 155 / 100);
                m.UvDc = Math.Min(132, (int)dc[Math.Clamp(q + dqUvDc, 0, 127)]);
                m.UvAc = ac[Math.Clamp(q + dqUvAc, 0, 127)];
            }

            br.GetBit(128);     // refresh_entropy_probs: meaningless for a lone key frame
            var update = Vp8Tables.CoeffUpdateProbs;
            var def = Vp8Tables.DefaultCoeffProbs;
            for (int i = 0; i < _probs.Length; i++)
                _probs[i] = br.GetBit(update[i]) != 0 ? (byte)br.GetValue(8) : def[i];
            _useSkip = br.GetBit(128) != 0;
            if (_useSkip) _skipProb = br.GetValue(8);

            // loop filter strengths
            for (int s = 0; s < 4; s++)
            {
                int baseLevel = _filterLevel;
                if (_useSegment) baseLevel = _segAbsolute ? _segFilter[s] : _segFilter[s] + _filterLevel;
                for (int i4 = 0; i4 < 2; i4++)
                {
                    int level = baseLevel;
                    if (_useLfDelta)
                    {
                        level += _refLfDelta[0];
                        if (i4 != 0) level += _modeLfDelta[0];
                    }
                    level = Math.Clamp(level, 0, 63);
                    int k = s * 2 + i4;
                    if (level > 0 && _filterLevel > 0)
                    {
                        int interior = level;
                        if (_sharpness > 0)
                        {
                            interior >>= _sharpness > 4 ? 2 : 1;
                            if (interior > 9 - _sharpness) interior = 9 - _sharpness;
                        }
                        if (interior < 1) interior = 1;
                        _fInner[k] = interior;
                        _fLimit[k] = 2 * level + interior;
                        _fHev[k] = level >= 40 ? 2 : level >= 15 ? 1 : 0;
                    }
                    else _fLimit[k] = 0;
                }
            }
        }

        // ------------------------------------------------------------------ macroblock loop

        const int ModeBytes = 20;       // per macroblock: 4x4 flag, 16x16 mode, chroma mode, 16 sub-block modes

        /// <summary>What the parser hands to the reconstruction for one row of macroblocks.</summary>
        sealed class MbRow
        {
            public readonly byte[] Modes;
            public readonly uint[] NzY, NzUv;       // two bits per block: 0 empty, 1 only DC, 2 more
            public readonly int[] Filter;           // limit | interior << 8 | hev << 16 | inner << 24
            /// <summary>384 dequantized coefficients per macroblock. All zero between uses: the parser writes the
            /// non-zero ones, the reconstruction clears every block it consumes.</summary>
            public readonly short[] Coeffs;

            public readonly int Width;

            public MbRow(int mbW)
            {
                Width = mbW;
                Modes = new byte[ModeBytes * mbW];
                NzY = new uint[mbW]; NzUv = new uint[mbW]; Filter = new int[mbW];
                Coeffs = new short[384 * mbW];
            }
        }

        Vp8BoolReader _br;
        Vp8BoolReader[] _parts = Array.Empty<Vp8BoolReader>();
        readonly byte[] _posProbs = new byte[4 * 17 * 33];
        const int TypeStride = 17 * 33;
        byte[] _topModes = Array.Empty<byte>();     // sub-block modes above, BDc at the frame edge
        byte[] _topNz = Array.Empty<byte>();        // "block had coefficients" above: 4 Y, 2 U, 2 V, Y2
        byte[] _topY = Array.Empty<byte>(), _topU = Array.Empty<byte>(), _topV = Array.Empty<byte>();
        readonly byte[] _work = new byte[WorkSize];
        MbRow? _row;

        /// <summary>
        /// Three stages on three threads: parsing runs ahead on a pool thread, the calling thread rebuilds the pixels,
        /// and a second pool thread follows with the loop filter (which only touches rows that are already rebuilt,
        /// while prediction reads the unfiltered copies in the line buffers).
        /// </summary>
        void DecodeRowsThreaded(Vp8Frame frame)
        {
            const int Ahead = 8;
            bool filter = _filterLevel > 0;
            using var free = new System.Collections.Concurrent.BlockingCollection<MbRow>();
            using var parsed = new System.Collections.Concurrent.BlockingCollection<MbRow>();
            using var rebuilt = new System.Collections.Concurrent.BlockingCollection<(MbRow row, int y)>();
            using var cancel = new System.Threading.CancellationTokenSource();
            for (int i = 0; i < Ahead; i++) free.Add(new MbRow(_mbW));
            var parser = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    for (int y = 0; y < _mbH; y++)
                    {
                        var row = free.Take(cancel.Token);
                        ParseRow(row, y);
                        parsed.Add(row);
                    }
                }
                catch (OperationCanceledException) { }
            });
            var filterer = !filter ? System.Threading.Tasks.Task.CompletedTask : System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    for (int n = 0; n < _mbH; n++)
                    {
                        var (row, y) = rebuilt.Take(cancel.Token);
                        FilterRow(row, y, frame);
                        free.Add(row);
                    }
                }
                catch (OperationCanceledException) { }
            });
            try
            {
                for (int y = 0; y < _mbH; y++)
                {
                    var row = parsed.Take();
                    ReconstructRow(row, y, frame, !filter);
                    if (filter) rebuilt.Add((row, y));
                    else free.Add(row);
                }
                filterer.Wait();
            }
            finally
            {
                // the other threads read the caller's pinned data and write the frame: they must be gone before this returns
                cancel.Cancel();
                parser.Wait();
                filterer.Wait();
            }
        }

        /// <summary>Reads the prediction records (first partition) and the tokens of one macroblock row.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void ParseRow(MbRow row, int mby)
        {
            int mbW = _mbW;
            byte* leftModes = stackalloc byte[4];
            byte* leftNz = stackalloc byte[9];
            short* dc = stackalloc short[16];
            new Span<byte>(leftModes, 4).Clear();
            new Span<byte>(leftNz, 9).Clear();
            new Span<short>(dc, 16).Clear();
            var bprobs = Vp8Tables.BModeProbs;
            bool filter = _filterLevel > 0;
            ref Vp8BoolReader tr = ref _parts[mby & (_numParts - 1)];
            // the state of the first partition's reader stays in locals (see GetCoeffs)
            ulong bv = _br._value;
            uint br1 = _br._range;
            int bb = _br._bits;
            byte* bc = _br._cur, be = _br._end;
            fixed (byte* probs = _posProbs, pTopNz = _topNz, topModes = _topModes, pModes = row.Modes)
            fixed (short* pCoeffs = row.Coeffs)
            {
                for (int mbx = 0; mbx < mbW; mbx++)
                {
                    int segment = 0;
                    if (_updateMap)
                        segment = Vp8BoolReader.Bit(ref bv, ref br1, ref bb, ref bc, be, _segProbs[0]) == 0 ? Vp8BoolReader.Bit(ref bv, ref br1, ref bb, ref bc, be, _segProbs[1]) : 2 + Vp8BoolReader.Bit(ref bv, ref br1, ref bb, ref bc, be, _segProbs[2]);
                    bool skip = _useSkip && Vp8BoolReader.Bit(ref bv, ref br1, ref bb, ref bc, be, _skipProb) != 0;
                    bool i4 = Vp8BoolReader.Bit(ref bv, ref br1, ref bb, ref bc, be, 145) == 0;
                    byte* modes = pModes + mbx * ModeBytes;
                    modes[0] = (byte)(i4 ? 1 : 0);
                    if (!i4)
                    {
                        int ymode = Vp8BoolReader.Bit(ref bv, ref br1, ref bb, ref bc, be, 156) != 0 ? (Vp8BoolReader.Bit(ref bv, ref br1, ref bb, ref bc, be, 128) != 0 ? TmPred : HPred) : (Vp8BoolReader.Bit(ref bv, ref br1, ref bb, ref bc, be, 163) != 0 ? VPred : DcPred);
                        modes[1] = (byte)ymode;
                        for (int i = 0; i < 4; i++) { topModes[4 * mbx + i] = (byte)ymode; leftModes[i] = (byte)ymode; }
                    }
                    else
                    {
                        for (int y = 0; y < 4; y++)
                        {
                            int l = leftModes[y];
                            for (int x = 0; x < 4; x++)
                            {
                                var p = bprobs.Slice((topModes[4 * mbx + x] * 10 + l) * 9, 9);
                                int m;
                                if (Vp8BoolReader.Bit(ref bv, ref br1, ref bb, ref bc, be, p[0]) == 0) m = BDc;
                                else if (Vp8BoolReader.Bit(ref bv, ref br1, ref bb, ref bc, be, p[1]) == 0) m = BTm;
                                else if (Vp8BoolReader.Bit(ref bv, ref br1, ref bb, ref bc, be, p[2]) == 0) m = BVe;
                                else if (Vp8BoolReader.Bit(ref bv, ref br1, ref bb, ref bc, be, p[3]) == 0) m = Vp8BoolReader.Bit(ref bv, ref br1, ref bb, ref bc, be, p[4]) == 0 ? BHe : Vp8BoolReader.Bit(ref bv, ref br1, ref bb, ref bc, be, p[5]) == 0 ? BRd : BVr;
                                else m = Vp8BoolReader.Bit(ref bv, ref br1, ref bb, ref bc, be, p[6]) == 0 ? BLd : Vp8BoolReader.Bit(ref bv, ref br1, ref bb, ref bc, be, p[7]) == 0 ? BVl : Vp8BoolReader.Bit(ref bv, ref br1, ref bb, ref bc, be, p[8]) == 0 ? BHd : BHu;
                                topModes[4 * mbx + x] = (byte)m;
                                modes[3 + y * 4 + x] = (byte)m;
                                l = m;
                            }
                            leftModes[y] = (byte)l;
                        }
                    }
                    modes[2] = (byte)(Vp8BoolReader.Bit(ref bv, ref br1, ref bb, ref bc, be, 142) == 0 ? DcPred : Vp8BoolReader.Bit(ref bv, ref br1, ref bb, ref bc, be, 114) == 0 ? VPred : Vp8BoolReader.Bit(ref bv, ref br1, ref bb, ref bc, be, 183) != 0 ? TmPred : HPred);

                    uint nzY = 0, nzUv = 0;
                    byte* tnz = pTopNz + 9 * mbx;
                    if (!skip) ParseResiduals(ref tr, probs, ref _quant[segment], i4, tnz, leftNz, pCoeffs + 384 * mbx, dc, out nzY, out nzUv);
                    else
                    {
                        Unsafe.WriteUnaligned(tnz, 0UL);
                        Unsafe.WriteUnaligned(leftNz, 0UL);
                        if (!i4) tnz[8] = leftNz[8] = 0;
                    }
                    row.NzY[mbx] = nzY; row.NzUv[mbx] = nzUv;
                    if (filter)
                    {
                        int k = segment * 2 + (i4 ? 1 : 0);
                        int inner = i4 || (nzY | nzUv) != 0 ? 1 : 0;
                        row.Filter[mbx] = _fLimit[k] | _fInner[k] << 8 | _fHev[k] << 16 | inner << 24;
                    }
                }
            }
            _br._value = bv; _br._range = br1; _br._bits = bb; _br._cur = bc;
        }

        /// <summary>Predicts and adds the residuals of one macroblock row; with filterToo the loop filter follows at once.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void ReconstructRow(MbRow row, int mby, Vp8Frame frame, bool filterToo)
        {
            int mbW = _mbW;
            fixed (byte* fy = frame.Y, fu = frame.U, fv = frame.V, work = _work, pModes = row.Modes)
            fixed (byte* pTopY = _topY, pTopU = _topU, pTopV = _topV)
            fixed (short* pCoeffs = row.Coeffs)
            {
                for (int mbx = 0; mbx < mbW; mbx++)
                {
                    byte* modes = pModes + mbx * ModeBytes;
                    short* coeffs = pCoeffs + 384 * mbx;
                    uint nzY = row.NzY[mbx], nzUv = row.NzUv[mbx];
                    // ---- prediction borders
                    byte* yb = work + YOff, ub = work + UOff, vb = work + VOff;
                    if (mbx == 0)
                    {
                        for (int j = 0; j < 16; j++) yb[j * Bps - 1] = 129;
                        for (int j = 0; j < 8; j++) { ub[j * Bps - 1] = 129; vb[j * Bps - 1] = 129; }
                        yb[-1 - Bps] = ub[-1 - Bps] = vb[-1 - Bps] = (byte)(mby > 0 ? 129 : 127);
                    }
                    else
                    {
                        for (int j = -1; j < 16; j++) yb[j * Bps - 1] = yb[j * Bps + 15];
                        for (int j = -1; j < 8; j++) { ub[j * Bps - 1] = ub[j * Bps + 7]; vb[j * Bps - 1] = vb[j * Bps + 7]; }
                    }
                    if (mby > 0)
                    {
                        Vector128.Load(pTopY + 16 * mbx).Store(yb - Bps);
                        Unsafe.WriteUnaligned(ub - Bps, Unsafe.ReadUnaligned<ulong>(pTopU + 8 * mbx));
                        Unsafe.WriteUnaligned(vb - Bps, Unsafe.ReadUnaligned<ulong>(pTopV + 8 * mbx));
                    }
                    else
                    {
                        Vector128.Create((byte)127).Store(yb - Bps);
                        Unsafe.WriteUnaligned(ub - Bps, 0x7f7f7f7f7f7f7f7fUL);
                        Unsafe.WriteUnaligned(vb - Bps, 0x7f7f7f7f7f7f7f7fUL);
                    }

                    // ---- luma
                    if (modes[0] != 0)
                    {
                        // The four pixels above and to the right of the macroblock serve all sub-blocks of the right column.
                        uint tr4;
                        if (mby == 0) tr4 = 0x7f7f7f7f;
                        else if (mbx < mbW - 1) tr4 = Unsafe.ReadUnaligned<uint>(pTopY + 16 * mbx + 16);
                        else tr4 = pTopY[16 * mbx + 15] * 0x01010101u;
                        Unsafe.WriteUnaligned(yb - Bps + 16, tr4);
                        Unsafe.WriteUnaligned(yb + 3 * Bps + 16, tr4);
                        Unsafe.WriteUnaligned(yb + 7 * Bps + 16, tr4);
                        Unsafe.WriteUnaligned(yb + 11 * Bps + 16, tr4);
                        for (int n = 0; n < 16; n++)
                        {
                            byte* dst = yb + (n >> 2) * 4 * Bps + (n & 3) * 4;
                            Predict4(dst, modes[3 + n]);
                            AddResidual(coeffs + n * 16, dst, (int)(nzY >> (2 * n)) & 3);
                        }
                    }
                    else
                    {
                        Predict16(yb, modes[1], mbx > 0, mby > 0);
                        if (nzY != 0)
                            for (int n = 0; n < 16; n++)
                                AddResidual(coeffs + n * 16, yb + (n >> 2) * 4 * Bps + (n & 3) * 4, (int)(nzY >> (2 * n)) & 3);
                    }
                    // ---- chroma
                    Predict8(ub, modes[2], mbx > 0, mby > 0);
                    Predict8(vb, modes[2], mbx > 0, mby > 0);
                    if (nzUv != 0)
                        for (int n = 0; n < 4; n++)
                        {
                            int o = (n >> 1) * 4 * Bps + (n & 1) * 4;
                            AddResidual(coeffs + (16 + n) * 16, ub + o, (int)(nzUv >> (2 * n)) & 3);
                            AddResidual(coeffs + (20 + n) * 16, vb + o, (int)(nzUv >> (2 * n + 8)) & 3);
                        }

                    // ---- store
                    byte* dy = fy + (long)mby * 16 * frame.YStride + mbx * 16;
                    for (int j = 0; j < 16; j++) Vector128.Load(yb + j * Bps).Store(dy + j * frame.YStride);
                    byte* du = fu + (long)mby * 8 * frame.UVStride + mbx * 8, dv = fv + (long)mby * 8 * frame.UVStride + mbx * 8;
                    for (int j = 0; j < 8; j++)
                    {
                        Unsafe.WriteUnaligned(du + j * frame.UVStride, Unsafe.ReadUnaligned<ulong>(ub + j * Bps));
                        Unsafe.WriteUnaligned(dv + j * frame.UVStride, Unsafe.ReadUnaligned<ulong>(vb + j * Bps));
                    }
                    // the unfiltered bottom rows predict the next macroblock row; the top-left pixel of the next
                    // macroblock is still in the work area
                    Vector128.Load(yb + 15 * Bps).Store(pTopY + 16 * mbx);
                    Unsafe.WriteUnaligned(pTopU + 8 * mbx, Unsafe.ReadUnaligned<ulong>(ub + 7 * Bps));
                    Unsafe.WriteUnaligned(pTopV + 8 * mbx, Unsafe.ReadUnaligned<ulong>(vb + 7 * Bps));
                }

            }
            if (filterToo) FilterRow(row, mby, frame);
        }

        /// <summary>The loop filter over one macroblock row. It only touches this row and the three lines above it,
        /// and prediction never reads the frame, so it may trail the reconstruction.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void FilterRow(MbRow row, int mby, Vp8Frame frame)
        {
            if (_filterLevel == 0) return;
            fixed (byte* fy = frame.Y, fu = frame.U, fv = frame.V)
                for (int mbx = 0; mbx < _mbW; mbx++)
                    FilterMacroblock(frame, fy, fu, fv, mbx, mby, row.Filter[mbx]);
        }

        // ------------------------------------------------------------------ tokens

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void ParseResiduals(ref Vp8BoolReader br, byte* probs, ref Quant q, bool i4, byte* top, byte* left, short* coeffs, short* dc, out uint nzY, out uint nzUv)
        {
            int first = 0;
            byte* acProbs = probs + 3 * TypeStride;
            if (!i4)
            {
                int nz = GetBlocks(ref br, probs + TypeStride, 0, q.Y2Dc, q.Y2Ac, dc, top, left, BlockOrder + 48, 1, out _);
                if (nz > 1) InverseWht(dc, coeffs);
                else
                {
                    short dc0 = (short)((dc[0] + 3) >> 3);
                    for (int i = 0; i < 16; i++) coeffs[i * 16] = dc0;
                }
                new Span<short>(dc, 16).Clear();
                first = 1;
                acProbs = probs;
            }
            GetBlocks(ref br, acProbs, first, q.Y1Dc, q.Y1Ac, coeffs, top, left, BlockOrder, 16, out nzY);
            GetBlocks(ref br, probs + 2 * TypeStride, 0, q.UvDc, q.UvAc, coeffs + 16 * 16, top, left, BlockOrder + 32, 4, out uint nzU);
            GetBlocks(ref br, probs + 2 * TypeStride, 0, q.UvDc, q.UvAc, coeffs + 20 * 16, top, left, BlockOrder + 40, 4, out uint nzV);
            nzUv = nzU | nzV << 8;
        }

        // for every block its slot in the "had coefficients" context above and to the left: 16 Y, 4 U, 4 V, Y2
        static byte[]? _blockOrderKeepAlive;
        static readonly byte* BlockOrder = PinnedBlockOrder();

        static byte* PinnedBlockOrder()
        {
            var a = _blockOrderKeepAlive = GC.AllocateArray<byte>(50, pinned: true);
            for (int n = 0; n < 16; n++) { a[2 * n] = (byte)(n & 3); a[2 * n + 1] = (byte)(n >> 2); }
            for (int n = 0; n < 4; n++)
            {
                a[32 + 2 * n] = (byte)(4 + (n & 1)); a[33 + 2 * n] = (byte)(4 + (n >> 1));
                a[40 + 2 * n] = (byte)(6 + (n & 1)); a[41 + 2 * n] = (byte)(6 + (n >> 1));
            }
            a[48] = a[49] = 8;
            return (byte*)System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(a, 0);
        }

        /// <summary>
        /// Reads the tokens of <paramref name="count"/> consecutive blocks of one type, starting each at coefficient
        /// <paramref name="first"/>, and stores the dequantized values in raster order (16 per block). Updates the
        /// contexts and returns two bits per block in <paramref name="kinds"/>: 0 nothing, 1 only a DC value, 2 more.
        /// The return value is the position after the last token of the last block.
        /// One loop for a whole plane of a macroblock keeps the reader state in registers across its blocks.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static int GetBlocks(ref Vp8BoolReader br, byte* probs, int first, int dqDc, int dqAc, short* dst, byte* top, byte* left, byte* order, int count, out uint kinds)
        {
            // probs: [position][context][11] of one block type
            byte* zigzag = ZigzagPtr;
            ulong value = br._value;
            uint range = br._range;
            int bits = br._bits;
            byte* cur = br._cur, end = br._end;
            uint k = 0;
            int n = first;
            for (int b = 0; b < count; b++, dst += 16)
            {
                byte* ctxTop = top + order[2 * b], ctxLeft = left + order[2 * b + 1];
                n = first;
                byte* p = probs + (n * 3 + *ctxTop + *ctxLeft) * 11;
                for (; n < 16; n++)
                {
                    if (Vp8BoolReader.Bit(ref value, ref range, ref bits, ref cur, end, p[0]) == 0) break;         // end of block
                    while (Vp8BoolReader.Bit(ref value, ref range, ref bits, ref cur, end, p[1]) == 0)             // zero; no end of block may follow
                    {
                        if (++n == 16) goto done;
                        p = probs + n * 33;
                    }
                    byte* next = probs + (n + 1) * 33;
                    int v;
                    if (Vp8BoolReader.Bit(ref value, ref range, ref bits, ref cur, end, p[2]) == 0) { v = 1; p = next + 11; }
                    else
                    {
                        if (Vp8BoolReader.Bit(ref value, ref range, ref bits, ref cur, end, p[3]) == 0)
                        {
                            if (Vp8BoolReader.Bit(ref value, ref range, ref bits, ref cur, end, p[4]) == 0) v = 2;
                            else v = 3 + Vp8BoolReader.Bit(ref value, ref range, ref bits, ref cur, end, p[5]);
                        }
                        else if (Vp8BoolReader.Bit(ref value, ref range, ref bits, ref cur, end, p[6]) == 0)
                        {
                            if (Vp8BoolReader.Bit(ref value, ref range, ref bits, ref cur, end, p[7]) == 0)
                                v = 5 + Vp8BoolReader.Bit(ref value, ref range, ref bits, ref cur, end, 159);
                            else
                            {
                                v = 7 + 2 * Vp8BoolReader.Bit(ref value, ref range, ref bits, ref cur, end, 165);
                                v += Vp8BoolReader.Bit(ref value, ref range, ref bits, ref cur, end, 145);
                            }
                        }
                        else
                        {
                            int b1 = Vp8BoolReader.Bit(ref value, ref range, ref bits, ref cur, end, p[8]);
                            int cat = 2 * b1 + Vp8BoolReader.Bit(ref value, ref range, ref bits, ref cur, end, p[9 + b1]);
                            var extra = cat == 0 ? Vp8Tables.Cat3 : cat == 1 ? Vp8Tables.Cat4 : cat == 2 ? Vp8Tables.Cat5 : Vp8Tables.Cat6;
                            v = 0;
                            for (int i = 0; extra[i] != 0; i++) v += v + Vp8BoolReader.Bit(ref value, ref range, ref bits, ref cur, end, extra[i]);
                            v += 3 + (8 << cat);
                        }
                        p = next + 22;
                    }
                    dst[zigzag[n]] = (short)(Vp8BoolReader.Sign(ref value, ref range, ref bits, ref cur, end, v) * (n > 0 ? dqAc : dqDc));
                }
            done:
                *ctxTop = *ctxLeft = (byte)(n > first ? 1 : 0);
                k |= (uint)(n > 1 ? 2 : dst[0] != 0 ? 1 : 0) << (2 * b);
            }
            br._value = value; br._range = range; br._bits = bits; br._cur = cur;
            kinds = k;
            return n;
        }

        // ------------------------------------------------------------------ inverse transforms

        /// <summary>Inverse Walsh-Hadamard transform of the Y2 block; the results become the DC of the 16 luma blocks.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static void InverseWht(short* src, short* dst)
        {
            int* tmp = stackalloc int[16];
            for (int i = 0; i < 4; i++)
            {
                int a0 = src[i] + src[12 + i], a1 = src[4 + i] + src[8 + i];
                int a2 = src[4 + i] - src[8 + i], a3 = src[i] - src[12 + i];
                tmp[i] = a0 + a1; tmp[8 + i] = a0 - a1;
                tmp[4 + i] = a3 + a2; tmp[12 + i] = a3 - a2;
            }
            for (int i = 0; i < 4; i++)
            {
                int dc = tmp[i * 4] + 3;
                int a0 = dc + tmp[3 + i * 4], a1 = tmp[1 + i * 4] + tmp[2 + i * 4];
                int a2 = tmp[1 + i * 4] - tmp[2 + i * 4], a3 = dc - tmp[3 + i * 4];
                dst[0] = (short)((a0 + a1) >> 3);
                dst[16] = (short)((a3 + a2) >> 3);
                dst[32] = (short)((a0 - a1) >> 3);
                dst[48] = (short)((a3 - a2) >> 3);
                dst += 64;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte Clip(int v) => (uint)v <= 255 ? (byte)v : v < 0 ? (byte)0 : (byte)255;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Mul1(int a) => ((a * 20091) >> 16) + a;      // a * sqrt(2) * cos(pi / 8)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Mul2(int a) => (a * 35468) >> 16;            // a * sqrt(2) * sin(pi / 8)

        /// <summary>Adds the inverse DCT of a block to the 4x4 pixels at dst and clears the coefficients.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void AddResidual(short* c, byte* dst, int kind)
        {
            if (kind == 0) return;
            if (kind == 1)
            {
                int dc = (c[0] + 4) >> 3;
                c[0] = 0;
                if (Vector128.IsHardwareAccelerated)
                {
                    // the four rows in one vector; clamped addition is saturating byte arithmetic
                    var px = Vector128.Create(Unsafe.ReadUnaligned<uint>(dst), Unsafe.ReadUnaligned<uint>(dst + Bps),
                        Unsafe.ReadUnaligned<uint>(dst + 2 * Bps), Unsafe.ReadUnaligned<uint>(dst + 3 * Bps)).AsByte();
                    px = dc >= 0 ? AddSat(px, Vector128.Create((byte)Math.Min(dc, 255))) : SubSat(px, Vector128.Create((byte)Math.Min(-dc, 255)));
                    var r = px.AsUInt32();
                    Unsafe.WriteUnaligned(dst, r.GetElement(0)); Unsafe.WriteUnaligned(dst + Bps, r.GetElement(1));
                    Unsafe.WriteUnaligned(dst + 2 * Bps, r.GetElement(2)); Unsafe.WriteUnaligned(dst + 3 * Bps, r.GetElement(3));
                    return;
                }
                for (int j = 0; j < 4; j++, dst += Bps)
                {
                    dst[0] = Clip(dst[0] + dc); dst[1] = Clip(dst[1] + dc);
                    dst[2] = Clip(dst[2] + dc); dst[3] = Clip(dst[3] + dc);
                }
                return;
            }
            InverseDctAdd(c, dst, Bps);
            Unsafe.InitBlockUnaligned(c, 0, 32);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<byte> AddSat(Vector128<byte> a, Vector128<byte> b)
        {
            if (System.Runtime.Intrinsics.X86.Sse2.IsSupported) return System.Runtime.Intrinsics.X86.Sse2.AddSaturate(a, b);
            if (System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported) return System.Runtime.Intrinsics.Arm.AdvSimd.AddSaturate(a, b);
            var s = a + b;
            return s | Vector128.LessThan(s, a);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<byte> SubSat(Vector128<byte> a, Vector128<byte> b)
        {
            if (System.Runtime.Intrinsics.X86.Sse2.IsSupported) return System.Runtime.Intrinsics.X86.Sse2.SubtractSaturate(a, b);
            if (System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported) return System.Runtime.Intrinsics.Arm.AdvSimd.SubtractSaturate(a, b);
            return Vector128.Max(a, b) - b;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static void InverseDctAdd(short* c, byte* dst, int stride)
        {
            if (Vector128.IsHardwareAccelerated)
            {
                InverseDctAddSimd(c, dst, stride);
                return;
            }
            int* tmp = stackalloc int[16];
            for (int i = 0; i < 4; i++)
            {
                int a = c[i] + c[8 + i], b = c[i] - c[8 + i];
                int cc = Mul2(c[4 + i]) - Mul1(c[12 + i]), d = Mul1(c[4 + i]) + Mul2(c[12 + i]);
                tmp[i] = a + d; tmp[12 + i] = a - d;
                tmp[4 + i] = b + cc; tmp[8 + i] = b - cc;
            }
            for (int i = 0; i < 4; i++, dst += stride)
            {
                int* t = tmp + i * 4;
                int dc = t[0] + 4;
                int a = dc + t[2], b = dc - t[2];
                int cc = Mul2(t[1]) - Mul1(t[3]), d = Mul1(t[1]) + Mul2(t[3]);
                dst[0] = Clip(dst[0] + ((a + d) >> 3));
                dst[1] = Clip(dst[1] + ((b + cc) >> 3));
                dst[2] = Clip(dst[2] + ((b - cc) >> 3));
                dst[3] = Clip(dst[3] + ((a - d) >> 3));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<int> Mul1(Vector128<int> a) => Vector128.ShiftRightArithmetic(a * 20091, 16) + a;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<int> Mul2(Vector128<int> a) => Vector128.ShiftRightArithmetic(a * 35468, 16);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<int> Row(short* p) => Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(p)).AsInt16());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void AddRow(byte* dst, Vector128<int> v)
        {
            var px = Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(dst)).AsByte())).AsInt32();
            var r = Vector128.Min(Vector128.Max(px + Vector128.ShiftRightArithmetic(v, 3), Vector128<int>.Zero), Vector128.Create(255));
            Unsafe.WriteUnaligned(dst, Vector128.Narrow(Vector128.Narrow(r, r), Vector128.Narrow(r, r)).AsUInt32().ToScalar());
        }

        /// <summary>The same arithmetic as the scalar version, four lanes at a time.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void InverseDctAddSimd(short* c, byte* dst, int stride)
        {
            // vertical pass: the rows of the coefficient block are the lanes' sources
            var r0 = Row(c); var r1 = Row(c + 4); var r2 = Row(c + 8); var r3 = Row(c + 12);
            var a = r0 + r2; var b = r0 - r2;
            var cc = Mul2(r1) - Mul1(r3); var d = Mul1(r1) + Mul2(r3);
            var t0 = a + d; var t1 = b + cc; var t2 = b - cc; var t3 = a - d;
            // horizontal pass on the columns
            Vp8LoopFilterSimd.Transpose4(ref t0, ref t1, ref t2, ref t3);
            var dc = t0 + Vector128.Create(4);
            a = dc + t2; b = dc - t2;
            cc = Mul2(t1) - Mul1(t3); d = Mul1(t1) + Mul2(t3);
            var o0 = a + d; var o1 = b + cc; var o2 = b - cc; var o3 = a - d;
            Vp8LoopFilterSimd.Transpose4(ref o0, ref o1, ref o2, ref o3);
            AddRow(dst, o0); AddRow(dst + stride, o1); AddRow(dst + 2 * stride, o2); AddRow(dst + 3 * stride, o3);
        }

        // ------------------------------------------------------------------ intra prediction
        // All predictors read the row above and the column left of dst inside the work area.

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static void Predict16(byte* dst, int mode, bool hasLeft, bool hasTop)
        {
            switch (mode)
            {
                case DcPred:
                {
                    int sum = 0, dc;
                    if (hasTop) for (int i = 0; i < 16; i++) sum += dst[i - Bps];
                    if (hasLeft) for (int i = 0; i < 16; i++) sum += dst[i * Bps - 1];
                    if (hasTop && hasLeft) dc = (sum + 16) >> 5;
                    else if (hasTop || hasLeft) dc = (sum + 8) >> 4;
                    else dc = 128;
                    for (int j = 0; j < 16; j++) Vector128.Create((byte)dc).Store(dst + j * Bps);
                    break;
                }
                case VPred:
                    for (int j = 0; j < 16; j++) Vector128.Load(dst - Bps).Store(dst + j * Bps);
                    break;
                case HPred:
                    for (int j = 0; j < 16; j++) Vector128.Create(dst[j * Bps - 1]).Store(dst + j * Bps);
                    break;
                default:
                    TrueMotion(dst, 16);
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static void Predict8(byte* dst, int mode, bool hasLeft, bool hasTop)
        {
            switch (mode)
            {
                case DcPred:
                {
                    int sum = 0, dc;
                    if (hasTop) for (int i = 0; i < 8; i++) sum += dst[i - Bps];
                    if (hasLeft) for (int i = 0; i < 8; i++) sum += dst[i * Bps - 1];
                    if (hasTop && hasLeft) dc = (sum + 8) >> 4;
                    else if (hasTop || hasLeft) dc = (sum + 4) >> 3;
                    else dc = 128;
                    for (int j = 0; j < 8; j++) Unsafe.WriteUnaligned(dst + j * Bps, (byte)dc * 0x0101010101010101UL);
                    break;
                }
                case VPred:
                {
                    ulong top = Unsafe.ReadUnaligned<ulong>(dst - Bps);
                    for (int j = 0; j < 8; j++) Unsafe.WriteUnaligned(dst + j * Bps, top);
                    break;
                }
                case HPred:
                    for (int j = 0; j < 8; j++) Unsafe.WriteUnaligned(dst + j * Bps, dst[j * Bps - 1] * 0x0101010101010101UL);
                    break;
                default:
                    TrueMotion(dst, 8);
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void TrueMotion(byte* dst, int size)
        {
            byte* top = dst - Bps;
            int p = top[-1];
            if (Vector128.IsHardwareAccelerated)
            {
                // top + (left - corner) in 16 bit lanes, clamped to 0..255 before narrowing
                var max = Vector128.Create((short)255);
                if (size == 16)
                {
                    var (t0, t1) = Vector128.Widen(Vector128.Load(top));
                    for (int j = 0; j < 16; j++, dst += Bps)
                    {
                        var d = Vector128.Create((short)(dst[-1] - p));
                        var a = Vector128.Min(Vector128.Max(t0.AsInt16() + d, Vector128<short>.Zero), max);
                        var b = Vector128.Min(Vector128.Max(t1.AsInt16() + d, Vector128<short>.Zero), max);
                        Vector128.Narrow(a.AsUInt16(), b.AsUInt16()).Store(dst);
                    }
                }
                else
                {
                    var t = Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(top)).AsByte()).AsInt16();
                    for (int j = 0; j < size; j++, dst += Bps)
                    {
                        var a = Vector128.Min(Vector128.Max(t + Vector128.Create((short)(dst[-1] - p)), Vector128<short>.Zero), max);
                        Unsafe.WriteUnaligned(dst, Vector128.Narrow(a.AsUInt16(), a.AsUInt16()).AsUInt64().ToScalar());
                    }
                }
                return;
            }
            for (int j = 0; j < size; j++, dst += Bps)
            {
                int d = dst[-1] - p;
                for (int i = 0; i < size; i++) dst[i] = Clip(top[i] + d);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte Avg3(int a, int b, int c) => (byte)((a + 2 * b + c + 2) >> 2);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte Avg2(int a, int b) => (byte)((a + b + 1) >> 1);

        /// <summary>Sub-block prediction (RFC 6386 section 12.3): A = row above (8 pixels), L = left column, P = corner.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static void Predict4(byte* dst, int mode)
        {
            byte* a = dst - Bps;
            int p = a[-1];
            int l0 = dst[-1], l1 = dst[Bps - 1], l2 = dst[2 * Bps - 1], l3 = dst[3 * Bps - 1];
            switch (mode)
            {
                case BDc:
                {
                    int v = (a[0] + a[1] + a[2] + a[3] + l0 + l1 + l2 + l3 + 4) >> 3;
                    uint f = (uint)v * 0x01010101u;
                    for (int j = 0; j < 4; j++) Unsafe.WriteUnaligned(dst + j * Bps, f);
                    break;
                }
                case BTm:
                    if (Vector128.IsHardwareAccelerated)
                    {
                        var t = Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(a)).AsByte()).AsInt16();
                        var max = Vector128.Create((short)255);
                        for (int j = 0; j < 4; j++)
                        {
                            var r = Vector128.Min(Vector128.Max(t + Vector128.Create((short)(dst[j * Bps - 1] - p)), Vector128<short>.Zero), max);
                            Unsafe.WriteUnaligned(dst + j * Bps, Vector128.Narrow(r.AsUInt16(), r.AsUInt16()).AsUInt32().ToScalar());
                        }
                        break;
                    }
                    for (int j = 0; j < 4; j++)
                    {
                        int d = dst[j * Bps - 1] - p;
                        for (int i = 0; i < 4; i++) dst[j * Bps + i] = Clip(a[i] + d);
                    }
                    break;
                case BVe:
                {
                    uint f = (uint)(Avg3(p, a[0], a[1]) | Avg3(a[0], a[1], a[2]) << 8 | Avg3(a[1], a[2], a[3]) << 16 | Avg3(a[2], a[3], a[4]) << 24);
                    for (int j = 0; j < 4; j++) Unsafe.WriteUnaligned(dst + j * Bps, f);
                    break;
                }
                case BHe:
                    Unsafe.WriteUnaligned(dst, Avg3(p, l0, l1) * 0x01010101u);
                    Unsafe.WriteUnaligned(dst + Bps, Avg3(l0, l1, l2) * 0x01010101u);
                    Unsafe.WriteUnaligned(dst + 2 * Bps, Avg3(l1, l2, l3) * 0x01010101u);
                    Unsafe.WriteUnaligned(dst + 3 * Bps, Avg3(l2, l3, l3) * 0x01010101u);
                    break;
                case BLd:
                {
                    byte b0 = Avg3(a[0], a[1], a[2]), b1 = Avg3(a[1], a[2], a[3]), b2 = Avg3(a[2], a[3], a[4]), b3 = Avg3(a[3], a[4], a[5]);
                    byte b4 = Avg3(a[4], a[5], a[6]), b5 = Avg3(a[5], a[6], a[7]), b6 = Avg3(a[6], a[7], a[7]);
                    Set(dst, 0, b0, b1, b2, b3); Set(dst, 1, b1, b2, b3, b4); Set(dst, 2, b2, b3, b4, b5); Set(dst, 3, b3, b4, b5, b6);
                    break;
                }
                case BRd:
                {
                    // the edge E of the RFC: l3 l2 l1 l0 p a0 a1 a2 a3
                    byte e1 = Avg3(l3, l2, l1), e2 = Avg3(l2, l1, l0), e3 = Avg3(l1, l0, p), e4 = Avg3(l0, p, a[0]);
                    byte e5 = Avg3(p, a[0], a[1]), e6 = Avg3(a[0], a[1], a[2]), e7 = Avg3(a[1], a[2], a[3]);
                    Set(dst, 0, e4, e5, e6, e7); Set(dst, 1, e3, e4, e5, e6); Set(dst, 2, e2, e3, e4, e5); Set(dst, 3, e1, e2, e3, e4);
                    break;
                }
                case BVr:
                {
                    byte s2 = Avg3(l2, l1, l0), s3 = Avg3(l1, l0, p), s4 = Avg3(l0, p, a[0]);
                    byte s5 = Avg3(p, a[0], a[1]), s6 = Avg3(a[0], a[1], a[2]), s7 = Avg3(a[1], a[2], a[3]);
                    byte h4 = Avg2(p, a[0]), h5 = Avg2(a[0], a[1]), h6 = Avg2(a[1], a[2]), h7 = Avg2(a[2], a[3]);
                    Set(dst, 0, h4, h5, h6, h7); Set(dst, 1, s4, s5, s6, s7); Set(dst, 2, s3, h4, h5, h6); Set(dst, 3, s2, s4, s5, s6);
                    break;
                }
                case BVl:
                {
                    byte h0 = Avg2(a[0], a[1]), h1 = Avg2(a[1], a[2]), h2 = Avg2(a[2], a[3]), h3 = Avg2(a[3], a[4]);
                    byte s1 = Avg3(a[0], a[1], a[2]), s2 = Avg3(a[1], a[2], a[3]), s3 = Avg3(a[2], a[3], a[4]), s4 = Avg3(a[3], a[4], a[5]);
                    byte s5 = Avg3(a[4], a[5], a[6]), s6 = Avg3(a[5], a[6], a[7]);
                    Set(dst, 0, h0, h1, h2, h3); Set(dst, 1, s1, s2, s3, s4); Set(dst, 2, h1, h2, h3, s5); Set(dst, 3, s2, s3, s4, s6);
                    break;
                }
                case BHd:
                {
                    byte h0 = Avg2(l3, l2), h1 = Avg2(l2, l1), h2 = Avg2(l1, l0), h3 = Avg2(l0, p);
                    byte s1 = Avg3(l3, l2, l1), s2 = Avg3(l2, l1, l0), s3 = Avg3(l1, l0, p), s4 = Avg3(l0, p, a[0]);
                    byte s5 = Avg3(p, a[0], a[1]), s6 = Avg3(a[0], a[1], a[2]);
                    Set(dst, 0, h3, s4, s5, s6); Set(dst, 1, h2, s3, h3, s4); Set(dst, 2, h1, s2, h2, s3); Set(dst, 3, h0, s1, h1, s2);
                    break;
                }
                default:    // BHu
                {
                    byte h0 = Avg2(l0, l1), h1 = Avg2(l1, l2), h2 = Avg2(l2, l3);
                    byte s1 = Avg3(l0, l1, l2), s2 = Avg3(l1, l2, l3), s3 = Avg3(l2, l3, l3);
                    byte e = (byte)l3;
                    Set(dst, 0, h0, s1, h1, s2); Set(dst, 1, h1, s2, h2, s3); Set(dst, 2, h2, s3, e, e); Set(dst, 3, e, e, e, e);
                    break;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Set(byte* dst, int row, byte a, byte b, byte c, byte d)
            => Unsafe.WriteUnaligned(dst + row * Bps, (uint)(a | b << 8 | c << 16 | d << 24));

        // ------------------------------------------------------------------ loop filter (RFC 6386 section 15)

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void FilterMacroblock(Vp8Frame f, byte* fy, byte* fu, byte* fv, int mbx, int mby, int info)
        {
            int limit = info & 0xff;
            if (limit == 0) return;
            int interior = (info >> 8) & 0xff, hev = (info >> 16) & 0xff;
            bool inner = info >> 24 != 0;
            int ys = f.YStride, uvs = f.UVStride;
            byte* y = fy + (long)mby * 16 * ys + mbx * 16;
            if (Vp8LoopFilterSimd.IsSupported)
            {
                // kinds: 0 simple, 1 between sub-blocks, 2 between macroblocks
                if (_simpleFilter)
                {
                    if (mbx > 0) Vp8LoopFilterSimd.VerticalEdge(y, y + 8 * ys, ys, 0, limit + 4, 0, 0);
                    if (inner) Vp8LoopFilterSimd.InnerVerticalEdges16(y, ys, true, limit, 0, 0);
                    if (mby > 0) Vp8LoopFilterSimd.HorizontalEdge16(y, ys, 0, limit + 4, 0, 0);
                    if (inner) for (int i = 4; i < 16; i += 4) Vp8LoopFilterSimd.HorizontalEdge16(y + i * ys, ys, 0, limit, 0, 0);
                    return;
                }
                byte* cu = fu + (long)mby * 8 * uvs + mbx * 8, cv = fv + (long)mby * 8 * uvs + mbx * 8;
                if (mbx > 0)
                {
                    Vp8LoopFilterSimd.VerticalEdge(y, y + 8 * ys, ys, 2, limit + 4, interior, hev);
                    Vp8LoopFilterSimd.VerticalEdge(cu, cv, uvs, 2, limit + 4, interior, hev);
                }
                if (inner)
                {
                    Vp8LoopFilterSimd.InnerVerticalEdges16(y, ys, false, limit, interior, hev);
                    Vp8LoopFilterSimd.VerticalEdge(cu + 4, cv + 4, uvs, 1, limit, interior, hev);
                }
                if (mby > 0)
                {
                    Vp8LoopFilterSimd.HorizontalEdge16(y, ys, 2, limit + 4, interior, hev);
                    Vp8LoopFilterSimd.HorizontalEdge8(cu, cv, uvs, 2, limit + 4, interior, hev);
                }
                if (inner)
                {
                    for (int i = 4; i < 16; i += 4) Vp8LoopFilterSimd.HorizontalEdge16(y + i * ys, ys, 1, limit, interior, hev);
                    Vp8LoopFilterSimd.HorizontalEdge8(cu + 4 * uvs, cv + 4 * uvs, uvs, 1, limit, interior, hev);
                }
                return;
            }
            int mbLimit = 2 * (limit + 4) + 1, subLimit = 2 * limit + 1;
            if (_simpleFilter)
            {
                if (mbx > 0) SimpleFilter(y, 1, ys, mbLimit);
                if (inner) for (int i = 4; i < 16; i += 4) SimpleFilter(y + i, 1, ys, subLimit);
                if (mby > 0) SimpleFilter(y, ys, 1, mbLimit);
                if (inner) for (int i = 4; i < 16; i += 4) SimpleFilter(y + i * ys, ys, 1, subLimit);
                return;
            }
            byte* u = fu + (long)mby * 8 * uvs + mbx * 8, v = fv + (long)mby * 8 * uvs + mbx * 8;
            if (mbx > 0)
            {
                MacroblockFilter(y, 1, ys, 16, mbLimit, interior, hev);
                MacroblockFilter(u, 1, uvs, 8, mbLimit, interior, hev);
                MacroblockFilter(v, 1, uvs, 8, mbLimit, interior, hev);
            }
            if (inner)
            {
                for (int i = 4; i < 16; i += 4) SubblockFilter(y + i, 1, ys, 16, subLimit, interior, hev);
                SubblockFilter(u + 4, 1, uvs, 8, subLimit, interior, hev);
                SubblockFilter(v + 4, 1, uvs, 8, subLimit, interior, hev);
            }
            if (mby > 0)
            {
                MacroblockFilter(y, ys, 1, 16, mbLimit, interior, hev);
                MacroblockFilter(u, uvs, 1, 8, mbLimit, interior, hev);
                MacroblockFilter(v, uvs, 1, 8, mbLimit, interior, hev);
            }
            if (inner)
            {
                for (int i = 4; i < 16; i += 4) SubblockFilter(y + i * ys, ys, 1, 16, subLimit, interior, hev);
                SubblockFilter(u + 4 * uvs, uvs, 1, 8, subLimit, interior, hev);
                SubblockFilter(v + 4 * uvs, uvs, 1, 8, subLimit, interior, hev);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Clamp8(int v) => v < -128 ? -128 : v > 127 ? 127 : v;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Abs(int v) => v < 0 ? -v : v;

        /// <summary>The adjustment shared by all filters: moves the two edge pixels towards each other, returns the step.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int CommonAdjust(bool outerTaps, byte* p, int step)
        {
            int p1 = p[-2 * step], p0 = p[-step], q0 = p[0], q1 = p[step];
            int a = Clamp8((outerTaps ? Clamp8(p1 - q1) : 0) + 3 * (q0 - p0));
            int b = Math.Min(a + 3, 127) >> 3;
            a = Math.Min(a + 4, 127) >> 3;
            p[0] = Clip(q0 - a);
            p[-step] = Clip(p0 + b);
            return a;
        }

        // step: distance between the pixels across the edge, along: distance between the positions on the edge;
        // limit2 is 2 * edge limit + 1
        static void SimpleFilter(byte* p, int step, int along, int limit2)
        {
            for (int i = 0; i < 16; i++, p += along)
                if (4 * Abs(p[-step] - p[0]) + Abs(p[-2 * step] - p[step]) <= limit2)
                    CommonAdjust(true, p, step);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool FilterYes(byte* p, int step, int limit2, int interior)
        {
            int p3 = p[-4 * step], p2 = p[-3 * step], p1 = p[-2 * step], p0 = p[-step];
            int q0 = p[0], q1 = p[step], q2 = p[2 * step], q3 = p[3 * step];
            return 4 * Abs(p0 - q0) + Abs(p1 - q1) <= limit2
                && Abs(p3 - p2) <= interior && Abs(p2 - p1) <= interior && Abs(p1 - p0) <= interior
                && Abs(q3 - q2) <= interior && Abs(q2 - q1) <= interior && Abs(q1 - q0) <= interior;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool HighEdgeVariance(byte* p, int step, int thresh)
            => Abs(p[-2 * step] - p[-step]) > thresh || Abs(p[step] - p[0]) > thresh;

        static void SubblockFilter(byte* p, int step, int along, int size, int limit2, int interior, int hevThresh)
        {
            for (int i = 0; i < size; i++, p += along)
            {
                if (!FilterYes(p, step, limit2, interior)) continue;
                if (HighEdgeVariance(p, step, hevThresh)) CommonAdjust(true, p, step);
                else
                {
                    int p1 = p[-2 * step], q1 = p[step];
                    int a = (CommonAdjust(false, p, step) + 1) >> 1;
                    p[step] = Clip(q1 - a);
                    p[-2 * step] = Clip(p1 + a);
                }
            }
        }

        static void MacroblockFilter(byte* p, int step, int along, int size, int limit2, int interior, int hevThresh)
        {
            for (int i = 0; i < size; i++, p += along)
            {
                if (!FilterYes(p, step, limit2, interior)) continue;
                if (HighEdgeVariance(p, step, hevThresh)) CommonAdjust(true, p, step);
                else
                {
                    int p2 = p[-3 * step], p1 = p[-2 * step], p0 = p[-step], q0 = p[0], q1 = p[step], q2 = p[2 * step];
                    int w = Clamp8(Clamp8(p1 - q1) + 3 * (q0 - p0));
                    int a = (27 * w + 63) >> 7;
                    p[0] = Clip(q0 - a); p[-step] = Clip(p0 + a);
                    a = (18 * w + 63) >> 7;
                    p[step] = Clip(q1 - a); p[-2 * step] = Clip(p1 + a);
                    a = (9 * w + 63) >> 7;
                    p[2 * step] = Clip(q2 - a); p[-3 * step] = Clip(p2 + a);
                }
            }
        }
    }
}
