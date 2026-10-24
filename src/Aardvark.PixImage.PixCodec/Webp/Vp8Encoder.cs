using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Aardvark.ImageCodecs
{
    /// <summary>The boolean entropy encoder of RFC 6386 section 7.</summary>
    internal sealed class Vp8BoolWriter
    {
        byte[] _buf = new byte[1024];
        int _pos;
        uint _range = 255, _bottom;
        int _bitCount = 24;

        void Emit(byte b)
        {
            if (_pos == _buf.Length) Array.Resize(ref _buf, _buf.Length * 2);
            _buf[_pos++] = b;
        }

        void AddOne()
        {
            int p = _pos - 1;
            while (p >= 0 && _buf[p] == 255) _buf[p--] = 0;
            if (p >= 0) _buf[p]++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Put(int bit, int prob)
        {
            uint split = 1 + (((_range - 1) * (uint)prob) >> 8);
            uint r;
            if (bit != 0) { _bottom += split; r = _range - split; }
            else r = split;
            if (r >= 128) { _range = r; return; }
            // Normalizing takes s shifts. Without a byte to emit and without a carry among the bits shifted out,
            // the bit by bit loop below would just do this:
            int s = System.Numerics.BitOperations.LeadingZeroCount(r) - 24;
            if (s < _bitCount && _bottom >> (32 - s) == 0)
            {
                _range = r << s;
                _bottom <<= s;
                _bitCount -= s;
                return;
            }
            _range = r;
            while (_range < 128)
            {
                _range <<= 1;
                if ((_bottom & 0x80000000) != 0) AddOne();
                _bottom <<= 1;
                if (--_bitCount == 0)
                {
                    Emit((byte)(_bottom >> 24));
                    _bottom &= 0xffffff;
                    _bitCount = 8;
                }
            }
        }

        public void PutBit(bool bit) => Put(bit ? 1 : 0, 128);

        public void PutLiteral(int value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--) Put((value >> i) & 1, 128);
        }

        public void PutSigned(int value, int bits)
        {
            PutLiteral(Math.Abs(value), bits);
            Put(value < 0 ? 1 : 0, 128);
        }

        public int Length => _pos;

        public byte[] Finish()
        {
            int c = _bitCount;
            uint v = _bottom;
            if ((v & (1u << (32 - c))) != 0) AddOne();
            v <<= c & 7;
            c >>= 3;
            while (--c >= 0) v <<= 8;
            for (c = 0; c < 4; c++) { Emit((byte)(v >> 24)); v <<= 8; }
            var res = new byte[_pos];
            Buffer.BlockCopy(_buf, 0, res, 0, _pos);
            return res;
        }
    }

    /// <summary>
    /// Switches for parts of the key frame syntax the encoder has no use for. They exist so that the tests can write
    /// files that exercise the decoder (and are checked against libwebp); every combination gives a valid stream.
    /// </summary>
    internal sealed class Vp8Syntax
    {
        public static readonly Vp8Syntax Default = new Vp8Syntax();
        /// <summary>Segment quantizers and filter levels as differences to the frame values instead of absolute values.</summary>
        public bool SegmentDeltas;
        /// <summary>Segmentation enabled, but no segment map: every macroblock is in segment 0.</summary>
        public bool NoSegmentMap;
        /// <summary>Segmentation with a map but without segment data: all segments use the frame values.</summary>
        public bool NoSegmentData;
        /// <summary>Leave the segment tree probabilities at 255.</summary>
        public bool DefaultSegmentProbabilities;
        /// <summary>No "macroblock has no coefficients" flags: every macroblock codes its tokens.</summary>
        public bool NoSkipFlags;
        /// <summary>Code with the default token probabilities.</summary>
        public bool DefaultTokenProbabilities;
        public bool ColorSpaceBit, ClampingBit, RefreshProbabilitiesBit;
        /// <summary>Quantizer index offsets for luma DC, the DC and AC of the Y2 block, chroma DC and AC (-15..15).</summary>
        public int DqY1Dc, DqY2Dc, DqY2Ac, DqUvDc = -4, DqUvAc = -4;
        /// <summary>Loop filter level offsets for intra frames and for macroblocks with 4x4 prediction (-63..63).</summary>
        public int? FilterDeltaIntra, FilterDelta4x4;
        /// <summary>Signal filter deltas as enabled without sending any.</summary>
        public bool FilterDeltasWithoutUpdate;
        /// <summary>Loop filter level per segment.</summary>
        public int[]? SegmentFilterLevels;
    }

    /// <summary>
    /// Encoder for VP8 key frames: intra mode decision by rate-distortion estimate, forward DCT / WHT, quantization,
    /// token probabilities fitted to the frame in a second pass.
    /// </summary>
    internal sealed unsafe class Vp8Encoder
    {
        const int Bps = Vp8Decoder.Bps, YOff = Vp8Decoder.YOff, UOff = Vp8Decoder.UOff, VOff = Vp8Decoder.VOff;
        const int MaxLevel = 2047;
        const int MaxPartition0 = (1 << 19) - 1;
        // Rounding offsets of the quantizer in 1/256: DC rounds to nearest, AC has a slightly wider dead zone
        // (small coefficients cost more bits than the error they remove is worth).
        const int BiasDc = 128, BiasAc = 104;
        // Chroma is quantized a little finer than luma (index offset -4, the default of Vp8Syntax): its errors spread
        // into two of the three RGB channels.
        readonly Vp8Syntax _sx;

        // ------------------------------------------------------------------ cost tables

        /// <summary>Cost of coding a 0 with probability p / 256, in 1/256 bit.</summary>
        static readonly ushort[] BitCost = BuildBitCost();

        static ushort[] BuildBitCost()
        {
            var t = new ushort[257];
            for (int i = 1; i <= 256; i++) t[i] = (ushort)Math.Round(-Math.Log2(i / 256.0) * 256);
            t[0] = t[1];
            return t;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Cost(int bit, int prob) => BitCost[bit != 0 ? 256 - prob : prob];

        /// <summary>Recorded bool decisions of one token partition: bit 15 value, bit 14 fixed probability, rest index or probability.</summary>
        sealed class TokenBuffer
        {
            public ushort[] Data = new ushort[4096];
            public int Count;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Add(bool bit, int probIndex)
            {
                if (Count == Data.Length) Array.Resize(ref Data, Count * 2);
                Data[Count++] = (ushort)((bit ? 0x8000 : 0) | probIndex);
                return bit;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void AddFixed(bool bit, int prob)
            {
                if (Count == Data.Length) Array.Resize(ref Data, Count * 2);
                Data[Count++] = (ushort)((bit ? 0x8000 : 0) | 0x4000 | prob);
            }
        }

        struct SegmentQuant
        {
            public int Index;
            public int Y1Dc, Y1Ac, Y2Dc, Y2Ac, UvDc, UvAc;
            public double Lambda;       // squared error that one bit is worth
            public int FilterLevel;
        }

        readonly int _method;
        readonly int _mbW, _mbH;
        readonly Vp8Frame _src;
        readonly WebpEncodeOptions _opt;
        readonly SegmentQuant[] _seg;
        readonly int _numSegments;
        readonly byte[] _segmentMap;
        readonly byte[] _probs = Vp8Tables.DefaultCoeffProbs.ToArray();
        // per macroblock: flags (1 = 4x4 prediction, 2 = no coefficients), 16x16 mode, chroma mode, 16 sub-block modes
        const int ModeRecord = 19;
        readonly byte[] _modes;
        bool _allowI4;

        public static byte[] Encode(uint[] argb, int w, int h, WebpEncodeOptions opt)
        {
            var frame = WebpYuv.FromArgb(argb, w, h, opt.Chroma);
            var enc = new Vp8Encoder(frame, opt, true);
            var res = enc.Run();
            if (res == null)
            {
                // the mode partition outgrew its 19 bit size field: 16x16 prediction needs far fewer mode bits
                res = new Vp8Encoder(frame, opt, false).Run()
                    ?? throw new ImageNotSupportedException("image is too large for a lossy WebP at this quality");
            }
            return res;
        }

        /// <summary>Quality 0..100 to the quantizer index 127..0, on the same curve libwebp uses.</summary>
        internal static int QualityToIndex(int quality)
        {
            double c = Math.Clamp(quality, 0, 100) / 100.0;
            double linear = c < 0.75 ? c * (2.0 / 3.0) : 2.0 * c - 1.0;
            return Math.Clamp((int)Math.Round(127 * (1 - Math.Pow(linear, 1 / 3.0))), 0, 127);
        }

        Vp8Encoder(Vp8Frame src, WebpEncodeOptions opt, bool allowI4)
        {
            _src = src; _opt = opt;
            _sx = opt.Vp8Syntax ?? Vp8Syntax.Default;
            _method = Math.Clamp(opt.Method, 0, 6);
            _allowI4 = allowI4 && _method >= 1;
            _mbW = src.YStride / 16; _mbH = src.Y.Length / src.YStride / 16;
            _modes = new byte[_mbW * _mbH * ModeRecord];
            _numSegments = Math.Clamp(opt.Segments, 1, 4);
            if (_mbW * _mbH < _numSegments * 2) _numSegments = 1;
            _seg = new SegmentQuant[4];
            _segmentMap = new byte[_mbW * _mbH];
            int baseQ = QualityToIndex(opt.Quality);
            if (_numSegments > 1 && !_sx.NoSegmentMap) AssignSegments();
            for (int s = 0; s < 4; s++)
            {
                // flat areas show quantization most, busy ones hide it
                int q = baseQ;
                if (_numSegments > 1 && !_sx.NoSegmentData) q = Math.Clamp(baseQ + (int)Math.Round((s - (_numSegments - 1) / 2.0) * (2 + baseQ * 0.12)), 0, 127);
                SetQuant(ref _seg[s], q, s);
            }
        }

        void SetQuant(ref SegmentQuant m, int q, int segment)
        {
            var dc = Vp8Tables.DcQLookup;
            var ac = Vp8Tables.AcQLookup;
            m.Index = q;
            m.Y1Dc = dc[Math.Clamp(q + _sx.DqY1Dc, 0, 127)]; m.Y1Ac = ac[q];
            m.Y2Dc = dc[Math.Clamp(q + _sx.DqY2Dc, 0, 127)] * 2; m.Y2Ac = Math.Max(8, ac[Math.Clamp(q + _sx.DqY2Ac, 0, 127)] * 155 / 100);
            m.UvDc = Math.Min(132, (int)dc[Math.Clamp(q + _sx.DqUvDc, 0, 127)]); m.UvAc = ac[Math.Clamp(q + _sx.DqUvAc, 0, 127)];
            // high resolution theory puts the slope of distortion over rate near q^2 / 35 per bit
            m.Lambda = 0.03 * m.Y1Ac * m.Y1Ac;
            int level = _opt.FilterStrength >= 0 ? _opt.FilterStrength : (m.Y1Ac * 3 + 4) >> 3;
            if (_opt.FilterStrength < 0 && q < 4) level = 0;
            m.FilterLevel = Math.Clamp(level, 0, 63);
            if (_sx.SegmentFilterLevels != null && _numSegments > 1 && !_sx.NoSegmentData) m.FilterLevel = Math.Clamp(_sx.SegmentFilterLevels[segment], 0, 63);
        }

        /// <summary>Sorts the macroblocks into segments by luma activity (mean absolute deviation).</summary>
        void AssignSegments()
        {
            int n = _mbW * _mbH;
            var act = new int[n];
            var order = new int[n];
            fixed (byte* py = _src.Y)
            {
                for (int my = 0; my < _mbH; my++)
                    for (int mx = 0; mx < _mbW; mx++)
                    {
                        byte* p = py + (long)my * 16 * _src.YStride + mx * 16;
                        int sum = 0;
                        for (int j = 0; j < 16; j++) for (int i = 0; i < 16; i++) sum += p[j * _src.YStride + i];
                        int mean = (sum + 128) >> 8, dev = 0;
                        for (int j = 0; j < 16; j++) for (int i = 0; i < 16; i++) dev += Math.Abs(p[j * _src.YStride + i] - mean);
                        act[my * _mbW + mx] = dev;
                        order[my * _mbW + mx] = my * _mbW + mx;
                    }
            }
            Array.Sort(act, order);
            for (int i = 0; i < n; i++) _segmentMap[order[i]] = (byte)((long)i * _numSegments / n);
        }

        // ------------------------------------------------------------------ transforms and quantization

        const int Cc = 85627, Ss = 35468;       // sqrt(2) * cos(pi / 8) and sqrt(2) * sin(pi / 8) in 16.16

        /// <summary>
        /// Forward DCT of the 4x4 difference src - pred, scaled so that the decoder's inverse returns the input:
        /// coefficients = (M^T X M) / 2 for the matrix M of the inverse transform. Output in raster order.
        /// </summary>
        static void ForwardDct(byte* src, int srcStride, byte* pred, int predStride, int* dst)
        {
            if (Vector128.IsHardwareAccelerated)
            {
                // the rows of differences, turned into columns so that the horizontal pass works on whole vectors
                var d0 = Diff(src, pred); var d1 = Diff(src + srcStride, pred + predStride);
                var d2 = Diff(src + 2 * srcStride, pred + 2 * predStride); var d3 = Diff(src + 3 * srcStride, pred + 3 * predStride);
                Vp8LoopFilterSimd.Transpose4(ref d0, ref d1, ref d2, ref d3);
                var a = d0 + d3; var b = d1 + d2; var c = d1 - d2; var d = d0 - d3;
                var t0 = (a + b) * 8; var t2 = (a - b) * 8;
                var t1 = Vector128.ShiftRightArithmetic(d * Cc + c * Ss + Vector128.Create(1 << 12), 13);
                var t3 = Vector128.ShiftRightArithmetic(d * Ss - c * Cc + Vector128.Create(1 << 12), 13);
                Vp8LoopFilterSimd.Transpose4(ref t0, ref t1, ref t2, ref t3);
                a = t0 + t3; b = t1 + t2; c = t1 - t2; d = t0 - t3;
                var eight = Vector128.Create(8);
                var round = Vector128.Create(1 << 17);
                Vector128.ShiftRightArithmetic(a + b + eight, 4).Store(dst);
                Vector128.ShiftRightArithmetic(d * (Cc >> 2) + c * (Ss >> 2) + round, 18).Store(dst + 4);
                Vector128.ShiftRightArithmetic(a - b + eight, 4).Store(dst + 8);
                Vector128.ShiftRightArithmetic(d * (Ss >> 2) - c * (Cc >> 2) + round, 18).Store(dst + 12);
                return;
            }
            int* tmp = stackalloc int[16];
            for (int i = 0; i < 4; i++, src += srcStride, pred += predStride)
            {
                int d0 = src[0] - pred[0], d1 = src[1] - pred[1], d2 = src[2] - pred[2], d3 = src[3] - pred[3];
                int a = d0 + d3, b = d1 + d2, c = d1 - d2, d = d0 - d3;
                tmp[i * 4] = (a + b) * 8;
                tmp[i * 4 + 2] = (a - b) * 8;
                tmp[i * 4 + 1] = (d * Cc + c * Ss + (1 << 12)) >> 13;
                tmp[i * 4 + 3] = (d * Ss - c * Cc + (1 << 12)) >> 13;
            }
            for (int i = 0; i < 4; i++)
            {
                int a = tmp[i] + tmp[12 + i], b = tmp[4 + i] + tmp[8 + i], c = tmp[4 + i] - tmp[8 + i], d = tmp[i] - tmp[12 + i];
                dst[i] = (a + b + 8) >> 4;
                dst[8 + i] = (a - b + 8) >> 4;
                // the constants with two bits less, so that the products stay within 32 bits
                dst[4 + i] = (d * (Cc >> 2) + c * (Ss >> 2) + (1 << 17)) >> 18;
                dst[12 + i] = (d * (Ss >> 2) - c * (Cc >> 2) + (1 << 17)) >> 18;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<int> Diff(byte* a, byte* b)
            => Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(a)).AsByte())).AsInt32()
             - Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(b)).AsByte())).AsInt32();

        /// <summary>Forward Walsh-Hadamard transform of the 16 luma DC values, the inverse of the decoder's.</summary>
        static void ForwardWht(int* src, int* dst)
        {
            int* tmp = stackalloc int[16];
            for (int i = 0; i < 4; i++)
            {
                int a = src[i * 4] + src[i * 4 + 3], b = src[i * 4 + 1] + src[i * 4 + 2];
                int c = src[i * 4 + 1] - src[i * 4 + 2], d = src[i * 4] - src[i * 4 + 3];
                tmp[i * 4] = a + b; tmp[i * 4 + 1] = d + c; tmp[i * 4 + 2] = a - b; tmp[i * 4 + 3] = d - c;
            }
            for (int i = 0; i < 4; i++)
            {
                int a = tmp[i] + tmp[12 + i], b = tmp[4 + i] + tmp[8 + i], c = tmp[4 + i] - tmp[8 + i], d = tmp[i] - tmp[12 + i];
                dst[i] = Half(a + b); dst[4 + i] = Half(d + c); dst[8 + i] = Half(a - b); dst[12 + i] = Half(d - c);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Half(int v) => v >= 0 ? (v + 1) >> 1 : -((1 - v) >> 1);

        /// <summary>
        /// Quantizes raster coefficients into levels in zigzag order (from position first) and writes the dequantized
        /// values back in raster order. Returns the position after the last non-zero level.
        /// </summary>
        /// <summary>
        /// 2^32 / q + 1: a * Recip[q] >> 32 is a / q for every a below 2^32 / q (the error of the reciprocal stays
        /// below the distance from a / q to the next integer), far more than coefficients reach.
        /// </summary>
        static readonly ulong[] Recip = BuildRecip();

        static ulong[] BuildRecip()
        {
            var t = new ulong[1024];
            for (int q = 1; q < t.Length; q++) t[q] = (1UL << 32) / (ulong)q + 1;
            return t;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Divide(int a, int q)
            => a < 1 << 22 && (uint)q < 1024u ? (int)((ulong)a * Recip[q] >> 32) : a / q;

        internal static int Quantize(int* coefs, short* levels, short* dq, int dcq, int acq, int first)
        {
            if (Vector128.IsHardwareAccelerated)
            {
                int res = QuantizeSimd(coefs, levels, dq, dcq, acq, first);
                if (res >= 0) return res;
            }
            return QuantizeScalar(coefs, levels, dq, dcq, acq, first);
        }

        internal static int QuantizeScalar(int* coefs, short* levels, short* dq, int dcq, int acq, int first)
        {
            var zigzag = Vp8Tables.Zigzag;
            int last = 0;
            if (first == 1) { levels[0] = 0; dq[0] = 0; }
            for (int n = first; n < 16; n++)
            {
                int j = zigzag[n];
                int c = coefs[j], q = n == 0 ? dcq : acq;
                int a = (c < 0 ? -c : c) + ((q * (n == 0 ? BiasDc : BiasAc)) >> 8);
                if (a < q)
                {
                    // most coefficients end here
                    levels[n] = 0; dq[j] = 0;
                    continue;
                }
                int l = Divide(a, q);
                if (l > MaxLevel) l = MaxLevel;
                last = n + 1;
                if (c < 0) l = -l;
                levels[n] = (short)l;
                dq[j] = (short)(l * q);
            }
            return last;
        }

        /// <summary>
        /// <see cref="Quantize"/> on four coefficients at a time, in raster order, then reordered. The division is one in
        /// single precision: below 2^24 its rounding error stays below the distance from a / q to the next integer, so
        /// the truncation is exact. Returns -1 (nothing written) for coefficients beyond that, which do not occur.
        /// </summary>
        internal static int QuantizeSimd(int* coefs, short* levels, short* dq, int dcq, int acq, int first)
        {
            var c0 = Vector128.Load(coefs); var c1 = Vector128.Load(coefs + 4);
            var c2 = Vector128.Load(coefs + 8); var c3 = Vector128.Load(coefs + 12);
            var limit = Vector128.Create(1 << 22);
            if ((Vector128.GreaterThanOrEqual(Vector128.Abs(c0), limit) | Vector128.GreaterThanOrEqual(Vector128.Abs(c1), limit)
                | Vector128.GreaterThanOrEqual(Vector128.Abs(c2), limit) | Vector128.GreaterThanOrEqual(Vector128.Abs(c3), limit)) != Vector128<int>.Zero) return -1;
            var qa = Vector128.Create(acq);
            var q0 = Vector128.Create(dcq, acq, acq, acq);
            var ba = Vector128.Create((acq * BiasAc) >> 8);
            var b0 = Vector128.Create((dcq * BiasDc) >> 8, (acq * BiasAc) >> 8, (acq * BiasAc) >> 8, (acq * BiasAc) >> 8);
            var fa = Vector128.ConvertToSingle(qa);
            var f0 = Vector128.ConvertToSingle(q0);
            var l0 = Level(c0, q0, b0, f0, out var d0);
            var l1 = Level(c1, qa, ba, fa, out var d1);
            var l2 = Level(c2, qa, ba, fa, out var d2);
            var l3 = Level(c3, qa, ba, fa, out var d3);
            if (first == 1)
            {
                var keep = Vector128.Create(0, -1, -1, -1);
                l0 &= keep; d0 &= keep;
            }
            var lo = Vector128.Narrow(l0, l1).AsByte();     // raster 0..7
            var hi = Vector128.Narrow(l2, l3).AsByte();     // raster 8..15
            Vector128.Narrow(d0, d1).Store(dq);
            Vector128.Narrow(d2, d3).Store(dq + 8);
            // zigzag order 0 1 4 8 5 2 3 6 | 9 12 13 10 7 11 14 15, two bytes per level
            const byte z = 255;
            var z0 = Vector128.Shuffle(lo, Vector128.Create((byte)0, 1, 2, 3, 8, 9, z, z, 10, 11, 4, 5, 6, 7, 12, 13))
                   | Vector128.Shuffle(hi, Vector128.Create(z, z, z, z, z, z, 0, 1, z, z, z, z, z, z, z, z));
            var z1 = Vector128.Shuffle(hi, Vector128.Create((byte)2, 3, 8, 9, 10, 11, 4, 5, z, z, 6, 7, 12, 13, 14, 15))
                   | Vector128.Shuffle(lo, Vector128.Create(z, z, z, z, z, z, z, z, 14, 15, z, z, z, z, z, z));
            z0.AsInt16().Store(levels);
            z1.AsInt16().Store(levels + 8);
            uint nz = (~Vector128.Equals(z0.AsInt16(), Vector128<short>.Zero)).ExtractMostSignificantBits()
                    | (~Vector128.Equals(z1.AsInt16(), Vector128<short>.Zero)).ExtractMostSignificantBits() << 8;
            return 32 - System.Numerics.BitOperations.LeadingZeroCount(nz);
        }

        /// <summary>Signed levels of four coefficients and their dequantized values.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<int> Level(Vector128<int> c, Vector128<int> q, Vector128<int> bias, Vector128<float> fq, out Vector128<int> dq)
        {
            var a = Vector128.Abs(c) + bias;
            var l = Vector128.ConvertToInt32(Vector128.ConvertToSingle(a) / fq);
            l = Vector128.Min(l, Vector128.Create(MaxLevel)) & Vector128.GreaterThanOrEqual(a, q);
            var sign = Vector128.ShiftRightArithmetic(c, 31);
            l = (l ^ sign) - sign;
            dq = l * q;
            return l;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<int> SquaredDifferences(Vector128<byte> a, Vector128<byte> b)
        {
            // |a - b| fits a byte; squares of pairs summed into 32 bit lanes
            var d = Vector128.Max(a, b) - Vector128.Min(a, b);
            var (lo, hi) = Vector128.Widen(d);
            if (Sse2.IsSupported) return Sse2.MultiplyAddAdjacent(lo.AsInt16(), lo.AsInt16()) + Sse2.MultiplyAddAdjacent(hi.AsInt16(), hi.AsInt16());
            var (l0, l1) = Vector128.Widen(lo);
            var (h0, h1) = Vector128.Widen(hi);
            return (l0 * l0 + l1 * l1 + h0 * h0 + h1 * h1).AsInt32();
        }

        static int Sse4(byte* a, int aStride, byte* b, int bStride)
        {
            if (Vector128.IsHardwareAccelerated)
            {
                var x = Vector128.Create(Unsafe.ReadUnaligned<uint>(a), Unsafe.ReadUnaligned<uint>(a + aStride),
                    Unsafe.ReadUnaligned<uint>(a + 2 * aStride), Unsafe.ReadUnaligned<uint>(a + 3 * aStride)).AsByte();
                var y = Vector128.Create(Unsafe.ReadUnaligned<uint>(b), Unsafe.ReadUnaligned<uint>(b + bStride),
                    Unsafe.ReadUnaligned<uint>(b + 2 * bStride), Unsafe.ReadUnaligned<uint>(b + 3 * bStride)).AsByte();
                return Vector128.Sum(SquaredDifferences(x, y));
            }
            int s = 0;
            for (int j = 0; j < 4; j++, a += aStride, b += bStride)
            {
                int d0 = a[0] - b[0], d1 = a[1] - b[1], d2 = a[2] - b[2], d3 = a[3] - b[3];
                s += d0 * d0 + d1 * d1 + d2 * d2 + d3 * d3;
            }
            return s;
        }

        static int Sse(byte* a, int aStride, byte* b, int bStride, int size)
        {
            if (Vector128.IsHardwareAccelerated)
            {
                var acc = Vector128<int>.Zero;
                if (size == 16)
                    for (int j = 0; j < 16; j++, a += aStride, b += bStride) acc += SquaredDifferences(Vector128.Load(a), Vector128.Load(b));
                else
                    for (int j = 0; j < 8; j += 2, a += 2 * aStride, b += 2 * bStride)
                        acc += SquaredDifferences(Vector128.Create(Unsafe.ReadUnaligned<ulong>(a), Unsafe.ReadUnaligned<ulong>(a + aStride)).AsByte(),
                            Vector128.Create(Unsafe.ReadUnaligned<ulong>(b), Unsafe.ReadUnaligned<ulong>(b + bStride)).AsByte());
                return Vector128.Sum(acc);
            }
            int s = 0;
            for (int j = 0; j < size; j++, a += aStride, b += bStride)
                for (int i = 0; i < size; i++) { int d = a[i] - b[i]; s += d * d; }
            return s;
        }

        // ------------------------------------------------------------------ token costs and recording

        // The mode decision prices tokens with the default probabilities (the fitted ones only come in Assemble),
        // so the price of every coefficient value in every context is fixed: tables instead of walking the tree.
        const int MaxPriced = 67;       // all larger values cost the same (their extra bits counted as 11)
        /// <summary>[type, band, context][value 0..67]: the "not zero" decision, the magnitude tree and the sign.</summary>
        static readonly ushort[] LevelCost = BuildLevelCost(out EobCost);
        /// <summary>[type, band, context][0 = end of block here, 1 = more]</summary>
        static readonly ushort[] EobCost;

        static ushort[] BuildLevelCost(out ushort[] eob)
        {
            var probs = Vp8Tables.DefaultCoeffProbs;
            var t = new ushort[4 * 8 * 3 * (MaxPriced + 1)];
            eob = new ushort[4 * 8 * 3 * 2];
            for (int k = 0; k < 4 * 8 * 3; k++)
            {
                var p = probs.Slice(k * 11, 11);
                eob[2 * k] = (ushort)Cost(0, p[0]);
                eob[2 * k + 1] = (ushort)Cost(1, p[0]);
                for (int v = 0; v <= MaxPriced; v++)
                {
                    int cost;
                    if (v == 0) cost = Cost(0, p[1]);
                    else
                    {
                        cost = Cost(1, p[1]);
                        if (v == 1) cost += Cost(0, p[2]);
                        else
                        {
                            cost += Cost(1, p[2]);
                            if (v <= 4)
                            {
                                cost += Cost(0, p[3]);
                                if (v == 2) cost += Cost(0, p[4]);
                                else cost += Cost(1, p[4]) + Cost(v == 4 ? 1 : 0, p[5]);
                            }
                            else if (v <= 10)
                            {
                                cost += Cost(1, p[3]) + Cost(0, p[6]);
                                if (v <= 6) cost += Cost(0, p[7]) + Cost(v == 6 ? 1 : 0, 159);
                                else cost += Cost(1, p[7]) + Cost(v >= 9 ? 1 : 0, 165) + Cost((v & 1) == 0 ? 1 : 0, 145);
                            }
                            else
                            {
                                cost += Cost(1, p[3]) + Cost(1, p[6]);
                                // the extra bits are close to one bit each
                                if (v < 19) cost += Cost(0, p[8]) + Cost(0, p[9]) + 3 * 256;
                                else if (v < 35) cost += Cost(0, p[8]) + Cost(1, p[9]) + 4 * 256;
                                else if (v < 67) cost += Cost(1, p[8]) + Cost(0, p[10]) + 5 * 256;
                                else cost += Cost(1, p[8]) + Cost(1, p[10]) + 11 * 256;
                            }
                        }
                        cost += 256;        // sign
                    }
                    t[k * (MaxPriced + 1) + v] = (ushort)cost;
                }
            }
            return t;
        }

        /// <summary>Cost (1/256 bit) of the tokens of one block of the given type; mirrors <see cref="RecordCoeffs"/>.</summary>
        internal static int CoeffCost(int type, int ctx, short* levels, int first, int last)
        {
            var bands = Vp8Tables.Bands;
            ref ushort lc = ref LevelCost[0];
            ref ushort ec = ref EobCost[0];
            int n = first;
            int k = (type * 8 + bands[n]) * 3 + ctx;
            if (last <= n) return Unsafe.Add(ref ec, 2 * k);
            int cost = Unsafe.Add(ref ec, 2 * k + 1);
            while (n < 16)
            {
                int v = levels[n++];
                if (v < 0) v = -v;
                cost += Unsafe.Add(ref lc, k * (MaxPriced + 1) + Math.Min(v, MaxPriced));
                k = (type * 8 + bands[n]) * 3 + (v > 1 ? 2 : v);
                if (v == 0) continue;
                if (n == 16) break;
                if (n >= last) { cost += Unsafe.Add(ref ec, 2 * k); break; }
                cost += Unsafe.Add(ref ec, 2 * k + 1);
            }
            return cost;
        }

        /// <summary>Records the tokens of one block. probBase is the index of the block type's first probability.</summary>
        static void RecordCoeffs(TokenBuffer tb, int probBase, int ctx, short* levels, int first, int last)
        {
            var bands = Vp8Tables.Bands;
            int n = first;
            int p = probBase + (bands[n] * 3 + ctx) * 11;
            if (!tb.Add(last > n, p)) return;
            while (n < 16)
            {
                int c = levels[n++];
                int v = c < 0 ? -c : c;
                if (!tb.Add(v != 0, p + 1))
                {
                    p = probBase + bands[n] * 33;
                    continue;
                }
                int next = probBase + bands[n] * 33;
                if (!tb.Add(v > 1, p + 2)) p = next + 11;
                else
                {
                    if (!tb.Add(v > 4, p + 3))
                    {
                        if (tb.Add(v != 2, p + 4)) tb.Add(v == 4, p + 5);
                    }
                    else if (!tb.Add(v > 10, p + 6))
                    {
                        if (!tb.Add(v > 6, p + 7)) tb.AddFixed(v == 6, 159);
                        else { tb.AddFixed(v >= 9, 165); tb.AddFixed((v & 1) == 0, 145); }
                    }
                    else
                    {
                        ReadOnlySpan<byte> extra;
                        int rest;
                        if (v < 19) { tb.Add(false, p + 8); tb.Add(false, p + 9); extra = Vp8Tables.Cat3; rest = v - 11; }
                        else if (v < 35) { tb.Add(false, p + 8); tb.Add(true, p + 9); extra = Vp8Tables.Cat4; rest = v - 19; }
                        else if (v < 67) { tb.Add(true, p + 8); tb.Add(false, p + 10); extra = Vp8Tables.Cat5; rest = v - 35; }
                        else { tb.Add(true, p + 8); tb.Add(true, p + 10); extra = Vp8Tables.Cat6; rest = v - 67; }
                        int nb = extra.Length - 1;
                        for (int i = 0; i < nb; i++) tb.AddFixed(((rest >> (nb - 1 - i)) & 1) != 0, extra[i]);
                    }
                    p = next + 22;
                }
                tb.AddFixed(c < 0, 128);
                if (n == 16 || !tb.Add(n < last, p)) return;
            }
        }

        // ------------------------------------------------------------------ mode costs

        static readonly int[] I16ModeCost = BuildI16Cost();     // includes the "not 4x4" flag
        static readonly int[] UvModeCost = BuildUvCost();
        static readonly ushort[] BModeCost = BuildBModeCost();  // [above][left][mode]

        static int[] BuildI16Cost()
        {
            var c = new int[4];
            int notI4 = Cost(1, 145);
            c[Vp8Decoder.DcPred] = notI4 + Cost(0, 156) + Cost(0, 163);
            c[Vp8Decoder.VPred] = notI4 + Cost(0, 156) + Cost(1, 163);
            c[Vp8Decoder.HPred] = notI4 + Cost(1, 156) + Cost(0, 128);
            c[Vp8Decoder.TmPred] = notI4 + Cost(1, 156) + Cost(1, 128);
            return c;
        }

        static int[] BuildUvCost()
        {
            var c = new int[4];
            c[Vp8Decoder.DcPred] = Cost(0, 142);
            c[Vp8Decoder.VPred] = Cost(1, 142) + Cost(0, 114);
            c[Vp8Decoder.HPred] = Cost(1, 142) + Cost(1, 114) + Cost(0, 183);
            c[Vp8Decoder.TmPred] = Cost(1, 142) + Cost(1, 114) + Cost(1, 183);
            return c;
        }

        static ushort[] BuildBModeCost()
        {
            var t = new ushort[1000];
            var probs = Vp8Tables.BModeProbs;
            for (int k = 0; k < 100; k++)
            {
                var p = probs.Slice(k * 9, 9);
                int c3 = Cost(1, p[0]) + Cost(1, p[1]) + Cost(1, p[2]);
                t[k * 10 + Vp8Decoder.BDc] = (ushort)Cost(0, p[0]);
                t[k * 10 + Vp8Decoder.BTm] = (ushort)(Cost(1, p[0]) + Cost(0, p[1]));
                t[k * 10 + Vp8Decoder.BVe] = (ushort)(Cost(1, p[0]) + Cost(1, p[1]) + Cost(0, p[2]));
                t[k * 10 + Vp8Decoder.BHe] = (ushort)(c3 + Cost(0, p[3]) + Cost(0, p[4]));
                t[k * 10 + Vp8Decoder.BRd] = (ushort)(c3 + Cost(0, p[3]) + Cost(1, p[4]) + Cost(0, p[5]));
                t[k * 10 + Vp8Decoder.BVr] = (ushort)(c3 + Cost(0, p[3]) + Cost(1, p[4]) + Cost(1, p[5]));
                t[k * 10 + Vp8Decoder.BLd] = (ushort)(c3 + Cost(1, p[3]) + Cost(0, p[6]));
                t[k * 10 + Vp8Decoder.BVl] = (ushort)(c3 + Cost(1, p[3]) + Cost(1, p[6]) + Cost(0, p[7]));
                t[k * 10 + Vp8Decoder.BHd] = (ushort)(c3 + Cost(1, p[3]) + Cost(1, p[6]) + Cost(1, p[7]) + Cost(0, p[8]));
                t[k * 10 + Vp8Decoder.BHu] = (ushort)(c3 + Cost(1, p[3]) + Cost(1, p[6]) + Cost(1, p[7]) + Cost(1, p[8]));
            }
            return t;
        }

        // ------------------------------------------------------------------ the macroblock loop

        byte[]? Run()
        {
            int mbW = _mbW, mbH = _mbH;
            int numParts = _opt.Partitions >= 8 ? 8 : _opt.Partitions >= 4 ? 4 : _opt.Partitions >= 2 ? 2 : 1;
            var parts = new TokenBuffer[numParts];
            for (int i = 0; i < numParts; i++) parts[i] = new TokenBuffer();

            var topModes = new byte[4 * mbW];
            var topNz = new byte[9 * mbW];
            var topY = new byte[16 * mbW + 4];
            var topU = new byte[8 * mbW];
            var topV = new byte[8 * mbW];
            byte* work = stackalloc byte[Vp8Decoder.WorkSize];
            byte* leftModes = stackalloc byte[4];
            byte* leftNz = stackalloc byte[9];
            // scratch for the mode trials
            int* coefs = stackalloc int[17 * 16];
            short* levels = stackalloc short[25 * 16];
            short* bestLevels = stackalloc short[25 * 16];
            short* dq = stackalloc short[26 * 16];
            byte* recon = stackalloc byte[256];
            byte* bestRecon = stackalloc byte[256];
            int* lasts = stackalloc int[25];
            int* bestLasts = stackalloc int[25];
            byte* tmpNz = stackalloc byte[9];
            byte* bmodes = stackalloc byte[16];
            byte* topM = stackalloc byte[4];
            byte* leftM = stackalloc byte[4];
            uint* bestPix = stackalloc uint[4];
            uint* preds = stackalloc uint[40];
            short* uvLevels = stackalloc short[8 * 16];
            int* uvLasts = stackalloc int[8];
            Span<double> rough = stackalloc double[10];
            int skipped = 0;

            fixed (byte* sy = _src.Y, su = _src.U, sv = _src.V, probs = _probs)
            fixed (byte* pTopY = topY, pTopU = topU, pTopV = topV, pTopNz = topNz, pTopModes = topModes)
            {
                int ys = _src.YStride, uvs = _src.UVStride;
                for (int mby = 0; mby < mbH; mby++)
                {
                    var tb = parts[mby & (numParts - 1)];
                    new Span<byte>(leftModes, 4).Clear();
                    new Span<byte>(leftNz, 9).Clear();
                    for (int mbx = 0; mbx < mbW; mbx++)
                    {
                        int mbIndex = mby * mbW + mbx;
                        ref SegmentQuant q = ref _seg[_segmentMap[mbIndex]];
                        byte* srcY = sy + (long)mby * 16 * ys + mbx * 16;
                        byte* srcU = su + (long)mby * 8 * uvs + mbx * 8, srcV = sv + (long)mby * 8 * uvs + mbx * 8;
                        byte* yb = work + YOff, ub = work + UOff, vb = work + VOff;
                        byte* tnz = pTopNz + 9 * mbx;
                        byte* tmodes = pTopModes + 4 * mbx;
                        bool hasLeft = mbx > 0, hasTop = mby > 0;

                        // ---- prediction borders, exactly as the decoder sets them up
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
                            Buffer.MemoryCopy(pTopY + 16 * mbx, yb - Bps, 16, 16);
                            Buffer.MemoryCopy(pTopU + 8 * mbx, ub - Bps, 8, 8);
                            Buffer.MemoryCopy(pTopV + 8 * mbx, vb - Bps, 8, 8);
                        }
                        else
                        {
                            new Span<byte>(yb - Bps, 16).Fill(127);
                            new Span<byte>(ub - Bps, 8).Fill(127);
                            new Span<byte>(vb - Bps, 8).Fill(127);
                        }
                        uint tr4;
                        if (mby == 0) tr4 = 0x7f7f7f7f;
                        else if (mbx < mbW - 1) tr4 = Unsafe.ReadUnaligned<uint>(pTopY + 16 * mbx + 16);
                        else tr4 = pTopY[16 * mbx + 15] * 0x01010101u;
                        Unsafe.WriteUnaligned(yb - Bps + 16, tr4);
                        Unsafe.WriteUnaligned(yb + 3 * Bps + 16, tr4);
                        Unsafe.WriteUnaligned(yb + 7 * Bps + 16, tr4);
                        Unsafe.WriteUnaligned(yb + 11 * Bps + 16, tr4);

                        // ---- luma, 16x16 prediction: rank the modes by prediction error, encode the best one(s) for real
                        double best16 = double.MaxValue;
                        int mode16 = 0;
                        for (int m = 0; m < 4; m++)
                        {
                            Vp8Decoder.Predict16(yb, m, hasLeft, hasTop);
                            rough[m] = Sse(srcY, ys, yb, Bps, 16) * 256.0 + q.Lambda * I16ModeCost[m];
                        }
                        int tries16 = _method < 2 ? 1 : _method < 5 ? 2 : 4;
                        for (int t = 0; t < tries16; t++)
                        {
                            int m = 0;
                            for (int k = 1; k < 4; k++) if (rough[k] < rough[m]) m = k;
                            rough[m] = double.MaxValue;
                            double j = EncodeI16(srcY, ys, yb, m, hasLeft, hasTop, ref q, probs, tnz, leftNz, coefs, levels, dq, recon, lasts);
                            if (j < best16)
                            {
                                best16 = j; mode16 = m;
                                short* tl = levels; levels = bestLevels; bestLevels = tl;
                                byte* tr = recon; recon = bestRecon; bestRecon = tr;
                                int* tla = lasts; lasts = bestLasts; bestLasts = tla;
                            }
                        }
                        // nothing left to code with whole-block prediction: finer prediction cannot gain much
                        bool flat16 = bestLasts[0] == 0;
                        for (int n = 1; n <= 16 && flat16; n++) flat16 = bestLasts[n] <= 1;

                        // ---- luma, 4x4 prediction: block after block, each predicted from what was reconstructed before
                        bool i4 = false;
                        if (_allowI4 && !flat16)
                        {
                            double total = q.Lambda * Cost(0, 145);
                            for (int i = 0; i < 4; i++) { topM[i] = tmodes[i]; leftM[i] = leftModes[i]; }
                            new Span<byte>(tmpNz, 9).Clear();
                            for (int i = 0; i < 4; i++) { tmpNz[i] = tnz[i]; tmpNz[4 + i] = leftNz[i]; }
                            int tries = _method >= 6 ? 10 : _method >= 5 ? 6 : _method;
                            for (int n = 0; n < 16 && total < best16; n++)
                            {
                                int bx = n & 3, by = n >> 2;
                                byte* dst = yb + by * 4 * Bps + bx * 4;
                                byte* s = srcY + by * 4 * ys + bx * 4;
                                int ctxNz = tmpNz[bx] + tmpNz[4 + by];
                                var modeCost = new ReadOnlySpan<ushort>(BModeCost, (topM[bx] * 10 + leftM[by]) * 10, 10);
                                // rank the modes by prediction error, then encode the most promising ones for real
                                for (int m = 0; m < 10; m++)
                                {
                                    Vp8Decoder.Predict4(dst, m);
                                    for (int r = 0; r < 4; r++) preds[m * 4 + r] = Unsafe.ReadUnaligned<uint>(dst + r * Bps);
                                    rough[m] = Sse4(s, ys, dst, Bps) * 256.0 + q.Lambda * modeCost[m];
                                }
                                double bestJ = double.MaxValue;
                                int bestMode = 0, bestLast = 0;
                                short* bl = levels + 16;        // scratch levels of the candidate
                                for (int t = 0; t < tries; t++)
                                {
                                    int m = 0;
                                    for (int k = 1; k < 10; k++) if (rough[k] < rough[m]) m = k;
                                    rough[m] = double.MaxValue;
                                    for (int r = 0; r < 4; r++) Unsafe.WriteUnaligned(dst + r * Bps, preds[m * 4 + r]);
                                    ForwardDct(s, ys, dst, Bps, coefs);
                                    int last = Quantize(coefs, bl, dq, q.Y1Dc, q.Y1Ac, 0);
                                    int rate = CoeffCost(3, ctxNz, bl, 0, last) + modeCost[m];
                                    if (last > 0) Vp8Decoder.InverseDctAdd(dq, dst, Bps);
                                    double j = Sse4(s, ys, dst, Bps) * 256.0 + q.Lambda * rate;
                                    if (j < bestJ)
                                    {
                                        bestJ = j; bestMode = m; bestLast = last;
                                        for (int r = 0; r < 4; r++) bestPix[r] = Unsafe.ReadUnaligned<uint>(dst + r * Bps);
                                        Buffer.MemoryCopy(bl, levels + 32 + n * 16, 32, 32);
                                    }
                                }
                                for (int r = 0; r < 4; r++) Unsafe.WriteUnaligned(dst + r * Bps, bestPix[r]);
                                bmodes[n] = (byte)bestMode;
                                lasts[n] = bestLast;
                                topM[bx] = leftM[by] = (byte)bestMode;
                                tmpNz[bx] = tmpNz[4 + by] = (byte)(bestLast > 0 ? 1 : 0);
                                total += bestJ;
                                if (n == 15) i4 = total < best16;
                            }
                            if (!i4)
                            {
                                for (int j = 0; j < 16; j++) Buffer.MemoryCopy(bestRecon + j * 16, yb + j * Bps, 16, 16);
                            }
                        }
                        else
                        {
                            for (int j = 0; j < 16; j++) Buffer.MemoryCopy(bestRecon + j * 16, yb + j * Bps, 16, 16);
                        }

                        // ---- chroma: mode by prediction error, then transform and quantize
                        int uvMode = 0;
                        double bestUv = double.MaxValue;
                        for (int m = 0; m < 4; m++)
                        {
                            Vp8Decoder.Predict8(ub, m, hasLeft, hasTop);
                            Vp8Decoder.Predict8(vb, m, hasLeft, hasTop);
                            double j = (Sse(srcU, uvs, ub, Bps, 8) + Sse(srcV, uvs, vb, Bps, 8)) * 256.0 + q.Lambda * UvModeCost[m];
                            if (j < bestUv) { bestUv = j; uvMode = m; }
                        }
                        Vp8Decoder.Predict8(ub, uvMode, hasLeft, hasTop);
                        Vp8Decoder.Predict8(vb, uvMode, hasLeft, hasTop);
                        bool uvAny = false;
                        for (int ch = 0; ch < 2; ch++)
                        {
                            byte* pb = ch == 0 ? ub : vb;
                            byte* ps = ch == 0 ? srcU : srcV;
                            for (int n = 0; n < 4; n++)
                            {
                                int o = (n >> 1) * 4, ox = (n & 1) * 4;
                                ForwardDct(ps + o * uvs + ox, uvs, pb + o * Bps + ox, Bps, coefs);
                                int last = Quantize(coefs, uvLevels + (ch * 4 + n) * 16, dq, q.UvDc, q.UvAc, 0);
                                uvLasts[ch * 4 + n] = last;
                                if (last > 0) { uvAny = true; Vp8Decoder.InverseDctAdd(dq, pb + o * Bps + ox, Bps); }
                            }
                        }

                        // ---- record modes and tokens, keeping the contexts the decoder will have
                        bool anyY = false;
                        short* yLevels = i4 ? levels + 32 : bestLevels + 16;
                        int* yLasts = i4 ? lasts : bestLasts + 1;
                        for (int n = 0; n < 16; n++) if (yLasts[n] > (i4 ? 0 : 1)) anyY = true;
                        bool y2Any = !i4 && bestLasts[0] > 0;
                        bool skip = !anyY && !uvAny && !y2Any && !_sx.NoSkipFlags;
                        if (skip) skipped++;
                        fixed (byte* mr = &_modes[mbIndex * ModeRecord])
                        {
                            mr[0] = (byte)((i4 ? 1 : 0) | (skip ? 2 : 0));
                            mr[1] = (byte)mode16; mr[2] = (byte)uvMode;
                            for (int n = 0; n < 16; n++) mr[3 + n] = bmodes[n];
                        }
                        if (i4)
                        {
                            for (int i = 0; i < 4; i++) { tmodes[i] = bmodes[12 + i]; leftModes[i] = bmodes[i * 4 + 3]; }
                        }
                        else
                        {
                            for (int i = 0; i < 4; i++) tmodes[i] = leftModes[i] = (byte)mode16;
                        }
                        if (skip)
                        {
                            new Span<byte>(tnz, 8).Clear();
                            new Span<byte>(leftNz, 8).Clear();
                            if (!i4) tnz[8] = leftNz[8] = 0;
                        }
                        else
                        {
                            int first = 0, type = 3;
                            if (!i4)
                            {
                                RecordCoeffs(tb, 264, tnz[8] + leftNz[8], bestLevels, 0, bestLasts[0]);
                                tnz[8] = leftNz[8] = (byte)(bestLasts[0] > 0 ? 1 : 0);
                                first = 1; type = 0;
                            }
                            for (int by = 0; by < 4; by++)
                            {
                                int l = leftNz[by];
                                for (int bx = 0; bx < 4; bx++)
                                {
                                    int n = by * 4 + bx;
                                    RecordCoeffs(tb, type * 264, l + tnz[bx], yLevels + n * 16, first, yLasts[n]);
                                    l = yLasts[n] > first ? 1 : 0;
                                    tnz[bx] = (byte)l;
                                }
                                leftNz[by] = (byte)l;
                            }
                            for (int ch = 0; ch < 2; ch++)
                                for (int by = 0; by < 2; by++)
                                {
                                    int l = leftNz[4 + ch * 2 + by];
                                    for (int bx = 0; bx < 2; bx++)
                                    {
                                        int n = ch * 4 + by * 2 + bx;
                                        RecordCoeffs(tb, 2 * 264, l + tnz[4 + ch * 2 + bx], uvLevels + n * 16, 0, uvLasts[n]);
                                        l = uvLasts[n] > 0 ? 1 : 0;
                                        tnz[4 + ch * 2 + bx] = (byte)l;
                                    }
                                    leftNz[4 + ch * 2 + by] = (byte)l;
                                }
                        }

                        // the unfiltered bottom rows predict the next macroblock row
                        Buffer.MemoryCopy(yb + 15 * Bps, pTopY + 16 * mbx, 16, 16);
                        Buffer.MemoryCopy(ub + 7 * Bps, pTopU + 8 * mbx, 8, 8);
                        Buffer.MemoryCopy(vb + 7 * Bps, pTopV + 8 * mbx, 8, 8);
                    }
                }
            }
            return Assemble(parts, skipped);
        }

        /// <summary>
        /// Encodes the luma of a macroblock with a 16x16 mode into levels (Y2 block first, then the 16 blocks) and the
        /// reconstruction; returns distortion * 256 + lambda * rate.
        /// </summary>
        double EncodeI16(byte* srcY, int ys, byte* yb, int mode, bool hasLeft, bool hasTop, ref SegmentQuant q, byte* probs,
            byte* tnz, byte* leftNz, int* coefs, short* levels, short* dq, byte* recon, int* lasts)
        {
            Vp8Decoder.Predict16(yb, mode, hasLeft, hasTop);
            int* dcs = stackalloc int[16];
            int* y2 = stackalloc int[16];
            short* y2dq = stackalloc short[16];
            short* dcRec = stackalloc short[256];
            for (int n = 0; n < 16; n++)
            {
                int o = (n >> 2) * 4, ox = (n & 3) * 4;
                ForwardDct(srcY + o * ys + ox, ys, yb + o * Bps + ox, Bps, coefs + (n + 1) * 16);
                dcs[n] = coefs[(n + 1) * 16];
            }
            ForwardWht(dcs, y2);
            lasts[0] = Quantize(y2, levels, y2dq, q.Y2Dc, q.Y2Ac, 0);
            Vp8Decoder.InverseWht(y2dq, dcRec);
            int rate = I16ModeCost[mode] + CoeffCost(1, tnz[8] + leftNz[8], levels, 0, lasts[0]);
            byte* nzTop = stackalloc byte[4];
            byte* nzLeft = stackalloc byte[4];
            for (int i = 0; i < 4; i++) { nzTop[i] = tnz[i]; nzLeft[i] = leftNz[i]; }
            int sse = 0;
            for (int n = 0; n < 16; n++)
            {
                int bx = n & 3, by = n >> 2;
                short* d = dq + (n + 1) * 16;
                int last = Quantize(coefs + (n + 1) * 16, levels + (n + 1) * 16, d, q.Y1Dc, q.Y1Ac, 1);
                lasts[n + 1] = last;
                rate += CoeffCost(0, nzTop[bx] + nzLeft[by], levels + (n + 1) * 16, 1, last);
                nzTop[bx] = nzLeft[by] = (byte)(last > 1 ? 1 : 0);
                d[0] = dcRec[n * 16];
                byte* r = recon + by * 4 * 16 + bx * 4;
                byte* p = yb + by * 4 * Bps + bx * 4;
                for (int j = 0; j < 4; j++) Unsafe.WriteUnaligned(r + j * 16, Unsafe.ReadUnaligned<uint>(p + j * Bps));
                if (last > 1 || d[0] != 0) Vp8Decoder.InverseDctAdd(d, r, 16);
                sse += Sse4(srcY + by * 4 * ys + bx * 4, ys, r, 16);
            }
            return sse * 256.0 + q.Lambda * rate;
        }

        // ------------------------------------------------------------------ bitstream assembly

        byte[]? Assemble(TokenBuffer[] parts, int skipped)
        {
            int mbCount = _mbW * _mbH;
            // token statistics and the probabilities that fit them
            var stats = new int[_probs.Length * 2];
            foreach (var tb in parts)
                for (int i = 0; i < tb.Count; i++)
                {
                    int t = tb.Data[i];
                    if ((t & 0x4000) == 0) stats[(t & 0x3fff) * 2 + (t >> 15)]++;
                }
            var update = Vp8Tables.CoeffUpdateProbs;
            var newProbs = new byte[_probs.Length];
            var updated = new bool[_probs.Length];
            for (int i = 0; i < _probs.Length; i++)
            {
                int n0 = stats[2 * i], n1 = stats[2 * i + 1];
                int old = _probs[i];
                newProbs[i] = (byte)old;
                if (n0 + n1 == 0) continue;
                int p = Math.Clamp((int)((255L * n0 + (n0 + n1) / 2) / (n0 + n1)), 1, 255);
                long costOld = (long)n0 * Cost(0, old) + (long)n1 * Cost(1, old) + Cost(0, update[i]);
                long costNew = (long)n0 * Cost(0, p) + (long)n1 * Cost(1, p) + Cost(1, update[i]) + 8 * 256;
                if (costNew < costOld) { newProbs[i] = (byte)p; updated[i] = true; }
            }

            if (_sx.DefaultTokenProbabilities)
            {
                Array.Clear(updated);
                _probs.CopyTo(newProbs, 0);
            }
            var bw = new Vp8BoolWriter();
            bw.PutBit(_sx.ColorSpaceBit);
            bw.PutBit(_sx.ClampingBit);     // 0: the decoder has to clamp
            // segmentation
            var segProbs = new byte[] { 255, 255, 255 };
            bool segmentMap = _numSegments > 1 && !_sx.NoSegmentMap;
            int baseIndex = _seg[0].Index;
            // the frame level must not be 0 or the decoder skips filtering altogether
            int frameLevel = _seg[0].FilterLevel;
            if (_numSegments > 1)
            {
                for (int s = 1; s < 4; s++) frameLevel = Math.Max(frameLevel, _seg[s].FilterLevel);
                if (_sx.SegmentDeltas) baseIndex = _seg[1].Index;
                if (segmentMap && !_sx.DefaultSegmentProbabilities)
                {
                    Span<int> cnt = stackalloc int[4];
                    cnt.Clear();
                    for (int i = 0; i < mbCount; i++) cnt[_segmentMap[i]]++;
                    segProbs[0] = Prob(cnt[0] + cnt[1], mbCount);
                    segProbs[1] = Prob(cnt[0], cnt[0] + cnt[1]);
                    segProbs[2] = Prob(cnt[2], cnt[2] + cnt[3]);
                }
                bw.PutBit(true);
                bw.PutBit(segmentMap);
                bw.PutBit(!_sx.NoSegmentData);
                if (!_sx.NoSegmentData)
                {
                    bw.PutBit(!_sx.SegmentDeltas);      // absolute values?
                    for (int s = 0; s < 4; s++)
                    {
                        int v = _sx.SegmentDeltas ? _seg[s].Index - baseIndex : _seg[s].Index;
                        bw.PutBit(v != 0);
                        if (v != 0) bw.PutSigned(v, 7);
                    }
                    for (int s = 0; s < 4; s++)
                    {
                        int v = _sx.SegmentDeltas ? _seg[s].FilterLevel - frameLevel : _seg[s].FilterLevel;
                        bw.PutBit(v != 0);
                        if (v != 0) bw.PutSigned(v, 6);
                    }
                }
                if (segmentMap)
                    for (int i = 0; i < 3; i++)
                    {
                        bw.PutBit(segProbs[i] != 255);
                        if (segProbs[i] != 255) bw.PutLiteral(segProbs[i], 8);
                    }
            }
            else bw.PutBit(false);
            bw.PutBit(_opt.SimpleFilter);
            bw.PutLiteral(frameLevel, 6);
            bw.PutLiteral(Math.Clamp(_opt.FilterSharpness, 0, 7), 3);
            if (_sx.FilterDeltaIntra != null || _sx.FilterDelta4x4 != null || _sx.FilterDeltasWithoutUpdate)
            {
                bw.PutBit(true);
                bool send = !_sx.FilterDeltasWithoutUpdate;
                bw.PutBit(send);
                if (send)
                {
                    // four deltas by reference frame (only the intra one matters here), four by prediction mode
                    for (int i = 0; i < 8; i++)
                    {
                        int? v = i == 0 ? _sx.FilterDeltaIntra : i == 4 ? _sx.FilterDelta4x4 : i == 2 || i == 7 ? 9 : null;
                        bw.PutBit(v != null);
                        if (v != null) bw.PutSigned(v.Value, 6);
                    }
                }
            }
            else bw.PutBit(false);
            int numParts = parts.Length;
            bw.PutLiteral(numParts == 8 ? 3 : numParts == 4 ? 2 : numParts == 2 ? 1 : 0, 2);
            bw.PutLiteral(baseIndex, 7);
            foreach (int dq in new[] { _sx.DqY1Dc, _sx.DqY2Dc, _sx.DqY2Ac, _sx.DqUvDc, _sx.DqUvAc })
            {
                bw.PutBit(dq != 0);
                if (dq != 0) bw.PutSigned(dq, 4);
            }
            bw.PutBit(_sx.RefreshProbabilitiesBit);
            for (int i = 0; i < _probs.Length; i++)
            {
                bw.Put(updated[i] ? 1 : 0, update[i]);
                if (updated[i]) bw.PutLiteral(newProbs[i], 8);
            }
            int skipProb = Prob(mbCount - skipped, mbCount);
            bw.PutBit(!_sx.NoSkipFlags);
            if (!_sx.NoSkipFlags) bw.PutLiteral(skipProb, 8);

            // prediction records
            var bprobs = Vp8Tables.BModeProbs;
            var top = new byte[4 * _mbW];
            Span<byte> left = stackalloc byte[4];
            for (int mby = 0; mby < _mbH; mby++)
            {
                left.Clear();
                for (int mbx = 0; mbx < _mbW; mbx++)
                {
                    int idx = mby * _mbW + mbx;
                    var mr = _modes.AsSpan(idx * ModeRecord, ModeRecord);
                    if (segmentMap)
                    {
                        int s = _segmentMap[idx];
                        bw.Put(s >> 1, segProbs[0]);
                        bw.Put(s & 1, segProbs[1 + (s >> 1)]);
                    }
                    if (!_sx.NoSkipFlags) bw.Put((mr[0] >> 1) & 1, skipProb);
                    bool i4 = (mr[0] & 1) != 0;
                    bw.Put(i4 ? 0 : 1, 145);
                    if (!i4)
                    {
                        int m = mr[1];
                        bool hOrTm = m == Vp8Decoder.HPred || m == Vp8Decoder.TmPred;
                        bw.Put(hOrTm ? 1 : 0, 156);
                        if (hOrTm) bw.Put(m == Vp8Decoder.TmPred ? 1 : 0, 128);
                        else bw.Put(m == Vp8Decoder.VPred ? 1 : 0, 163);
                        for (int i = 0; i < 4; i++) top[4 * mbx + i] = left[i] = (byte)m;
                    }
                    else
                    {
                        for (int y = 0; y < 4; y++)
                        {
                            int l = left[y];
                            for (int x = 0; x < 4; x++)
                            {
                                var p = bprobs.Slice((top[4 * mbx + x] * 10 + l) * 9, 9);
                                int m = mr[3 + y * 4 + x];
                                PutBMode(bw, m, p);
                                top[4 * mbx + x] = (byte)m;
                                l = m;
                            }
                            left[y] = (byte)l;
                        }
                    }
                    int uv = mr[2];
                    bw.Put(uv != Vp8Decoder.DcPred ? 1 : 0, 142);
                    if (uv != Vp8Decoder.DcPred)
                    {
                        bw.Put(uv != Vp8Decoder.VPred ? 1 : 0, 114);
                        if (uv != Vp8Decoder.VPred) bw.Put(uv == Vp8Decoder.TmPred ? 1 : 0, 183);
                    }
                }
            }
            var part0 = bw.Finish();
            if (part0.Length > MaxPartition0) return null;

            // token partitions with the final probabilities
            var data = new byte[numParts][];
            long total = 10 + part0.Length + 3 * (numParts - 1);
            for (int k = 0; k < numParts; k++)
            {
                var w = new Vp8BoolWriter();
                var tb = parts[k];
                for (int i = 0; i < tb.Count; i++)
                {
                    int t = tb.Data[i];
                    w.Put(t >> 15, (t & 0x4000) != 0 ? t & 0xff : newProbs[t & 0x3fff]);
                }
                data[k] = w.Finish();
                if (k < numParts - 1 && data[k].Length >= 1 << 24) return null;
                total += data[k].Length;
            }

            var res = new byte[total];
            int tag = 0 | (_opt.SimpleFilter ? 1 : 0) << 1 | 1 << 4 | part0.Length << 5;
            res[0] = (byte)tag; res[1] = (byte)(tag >> 8); res[2] = (byte)(tag >> 16);
            res[3] = 0x9d; res[4] = 0x01; res[5] = 0x2a;
            res[6] = (byte)_src.Width; res[7] = (byte)(_src.Width >> 8);
            res[8] = (byte)_src.Height; res[9] = (byte)(_src.Height >> 8);
            int pos = 10;
            part0.CopyTo(res, pos); pos += part0.Length;
            for (int k = 0; k < numParts - 1; k++)
            {
                res[pos++] = (byte)data[k].Length; res[pos++] = (byte)(data[k].Length >> 8); res[pos++] = (byte)(data[k].Length >> 16);
            }
            for (int k = 0; k < numParts; k++) { data[k].CopyTo(res, pos); pos += data[k].Length; }
            return res;
        }

        static byte Prob(long zeros, long total)
            => total == 0 ? (byte)255 : (byte)Math.Clamp((255 * zeros + total / 2) / total, 1, 255);

        static void PutBMode(Vp8BoolWriter bw, int m, ReadOnlySpan<byte> p)
        {
            if (m == Vp8Decoder.BDc) { bw.Put(0, p[0]); return; }
            bw.Put(1, p[0]);
            if (m == Vp8Decoder.BTm) { bw.Put(0, p[1]); return; }
            bw.Put(1, p[1]);
            if (m == Vp8Decoder.BVe) { bw.Put(0, p[2]); return; }
            bw.Put(1, p[2]);
            if (m == Vp8Decoder.BHe || m == Vp8Decoder.BRd || m == Vp8Decoder.BVr)
            {
                bw.Put(0, p[3]);
                if (m == Vp8Decoder.BHe) { bw.Put(0, p[4]); return; }
                bw.Put(1, p[4]);
                bw.Put(m == Vp8Decoder.BVr ? 1 : 0, p[5]);
                return;
            }
            bw.Put(1, p[3]);
            if (m == Vp8Decoder.BLd) { bw.Put(0, p[6]); return; }
            bw.Put(1, p[6]);
            if (m == Vp8Decoder.BVl) { bw.Put(0, p[7]); return; }
            bw.Put(1, p[7]);
            bw.Put(m == Vp8Decoder.BHu ? 1 : 0, p[8]);
        }
    }
}
