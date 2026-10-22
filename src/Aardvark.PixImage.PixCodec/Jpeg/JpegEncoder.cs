using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Aardvark.ImageCodecs.JpegImpl
{
    /// <summary>
    /// Baseline / progressive Huffman JPEG encoder. Sequential 8 bit output is converted and transformed one MCU row
    /// at a time: with the standard tables each block is coded right away, for optimized tables the quantized blocks
    /// are kept in compact form (<see cref="BlockStore"/>) until the statistics are complete. The other modes keep all
    /// quantized coefficients (zigzag order) and walk them as often as they need. Working buffers come from the
    /// shared array pool.
    /// </summary>
    internal sealed unsafe partial class JpegEncoder
    {
        sealed class Comp
        {
            public int Id, H, V, Tq;
            public int BlocksW, BlocksH;         // whole MCUs
            public int ScanBlocksW, ScanBlocksH; // blocks covering the image, for non-interleaved scans
            public int Stride;
            public int StatBlocksW, StatBlocksH; // blocks a sequential scan actually codes
            public byte[] Plane = null!;
            public ushort[]? Plane16; // 12 bit samples
            public short[] Coefs = null!;
            public int Pred;
            public int LastDc; // DC of the block coded last (padding blocks repeat it)
            public int ArithContext, ArithTable; // arithmetic coding: DC conditioning state, statistics table (0 luma, 1 chroma)
            public HuffEnc Dc = null!, Ac = null!;
        }

        sealed class HuffEnc
        {
            public readonly int[] Freq = new int[257];
            /// <summary>code &lt;&lt; 8 | length per symbol (pinned, so the block coder can index it without bounds checks).</summary>
            public readonly uint[] Packed = GC.AllocateArray<uint>(256, pinned: true);
            /// <summary>For run/size symbols: code &lt;&lt; (8 + size) | (length + size), ready to take the value bits.</summary>
            public readonly ulong[] Extended = GC.AllocateArray<ulong>(256, pinned: true);
            /// <summary>Which of the two tables of a progressive scan this is.</summary>
            public uint Slot;
            public byte[] Counts = new byte[16];
            public byte[] Values = Array.Empty<byte>();

            public HuffEnc() { }

            public HuffEnc(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> values)
            {
                Counts = counts.ToArray();
                Values = values.ToArray();
                AssignCodes();
            }

            void AssignCodes()
            {
                Array.Clear(Packed);
                Array.Clear(Extended);
                uint code = 0;
                int k = 0;
                for (int len = 1; len <= 16; len++, code <<= 1)
                    for (int i = 0; i < Counts[len - 1]; i++, k++)
                    {
                        int sym = Values[k], size = sym & 15;
                        Extended[sym] = (ulong)code << (8 + size) | (uint)(len + size);
                        Packed[sym] = code++ << 8 | (uint)len;
                    }
            }

            /// <summary>Builds length limited (16 bit) Huffman codes from <see cref="Freq"/> following T.81 Annex K.2.</summary>
            public void BuildFromFrequencies()
            {
                Span<int> freq = stackalloc int[257];
                Span<int> codeSize = stackalloc int[257];
                Span<int> others = stackalloc int[257];
                Freq.CopyTo(freq);
                freq[256] = 1; // reserves one code point so no real symbol gets the all-ones code
                others.Fill(-1);
                while (true)
                {
                    // the two least frequent trees; ties go to the larger index
                    int c1 = -1, c2 = -1;
                    for (int i = 0; i <= 256; i++)
                        if (freq[i] != 0 && (c1 < 0 || freq[i] <= freq[c1])) c1 = i;
                    for (int i = 0; i <= 256; i++)
                        if (freq[i] != 0 && i != c1 && (c2 < 0 || freq[i] <= freq[c2])) c2 = i;
                    if (c2 < 0) break;
                    freq[c1] += freq[c2];
                    freq[c2] = 0;
                    // every symbol of both trees moves one level down; then chain tree 2 onto tree 1
                    codeSize[c1]++;
                    while (others[c1] >= 0) { c1 = others[c1]; codeSize[c1]++; }
                    others[c1] = c2;
                    codeSize[c2]++;
                    while (others[c2] >= 0) { c2 = others[c2]; codeSize[c2]++; }
                }

                Span<int> bits = stackalloc int[258];
                bits.Clear();
                for (int i = 0; i <= 256; i++) bits[codeSize[i]]++;
                // shorten codes longer than 16 bits: move a pair of the longest up next to a split shorter code
                for (int i = 257; i > 16; i--)
                {
                    while (bits[i] > 0)
                    {
                        int j = i - 2;
                        while (bits[j] == 0) j--;
                        bits[i] -= 2;
                        bits[i - 1]++;
                        bits[j + 1] += 2;
                        bits[j]--;
                    }
                }
                int longest = 16;
                while (bits[longest] == 0) longest--;
                bits[longest]--; // drop the reserved code point

                Counts = new byte[16];
                int total = 0;
                for (int i = 1; i <= 16; i++) { Counts[i - 1] = (byte)bits[i]; total += bits[i]; }
                Values = new byte[total];
                int k = 0;
                for (int len = 1; len <= 256 && k < total; len++)
                    for (int s = 0; s < 256; s++)
                        if (codeSize[s] == len) Values[k++] = (byte)s;
                AssignCodes();
            }
        }

        // ---------------------------------------------------------------- output

        readonly Stream _out;
        readonly byte[] _buf = ArrayPool<byte>.Shared.Rent(1 << 16);
        int _pos;
        ulong _acc;
        int _accBits;
        bool _gather; // statistics pass of a sequential scan: count symbols instead of writing them

        void Reserve(int n)
        {
            if (_pos + n > _buf.Length)
            {
                _out.Write(_buf, 0, _pos);
                _pos = 0;
            }
        }

        void Byte(int b) { Reserve(1); _buf[_pos++] = (byte)b; }
        void U16(int v) { Reserve(2); _buf[_pos++] = (byte)(v >> 8); _buf[_pos++] = (byte)v; }
        void Marker(int m) { Byte(0xFF); Byte(m); }

        void Bytes(ReadOnlySpan<byte> data)
        {
            while (data.Length > 0)
            {
                Reserve(1);
                int n = Math.Min(data.Length, _buf.Length - _pos);
                data.Slice(0, n).CopyTo(_buf.AsSpan(_pos));
                _pos += n;
                data = data.Slice(n);
            }
        }

        /// <summary>Writes 4 entropy coded bytes, stuffing a zero after every 0xFF. Returns the advanced pointer.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte* Write32(byte* o, uint w)
        {
            if (((~w - 0x01010101u) & w & 0x80808080u) == 0)
            {
                Unsafe.WriteUnaligned(o, System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(w));
                return o + 4;
            }
            for (int s = 24; s >= 0; s -= 8)
            {
                byte b = (byte)(w >> s);
                *o++ = b;
                if (b == 0xFF) *o++ = 0;
            }
            return o;
        }

        /// <summary>Pads the pending bits with ones to a byte boundary and writes them out.</summary>
        void FlushBits()
        {
            Reserve(16);
            while (_accBits > 0)
            {
                int take = Math.Min(8, _accBits);
                byte b = (byte)((_acc >> (_accBits - take) << (8 - take)) | (0xFFu >> take));
                _accBits -= take;
                _buf[_pos++] = b;
                if (b == 0xFF) _buf[_pos++] = 0;
            }
            _acc = 0;
        }

        // Progressive scans are coded in one pass that counts the symbols and records them as tokens; once the
        // Huffman tables are known the tokens are replayed into the bit stream.
        //   bits 0-7 symbol, 8-12 number of extra bits, 13-28 extra bits, 29 table slot, 30 restart marker, 31 raw bits only
        const uint TokenRestart = 1u << 30, TokenRaw = 1u << 31;
        uint[] _tokens = Array.Empty<uint>();
        int _tokenCount;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Push(uint token)
        {
            if (_tokenCount == _tokens.Length) Array.Resize(ref _tokens, Math.Max(_tokens.Length * 2, 1 << 16));
            _tokens[_tokenCount++] = token;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Symbol(HuffEnc t, int sym)
        {
            t.Freq[sym]++;
            Push((uint)sym | t.Slot << 29);
        }

        /// <summary>A symbol followed by nbits raw bits (taken from the low end of <paramref name="bits"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Symbol(HuffEnc t, int sym, int bits, int nbits)
        {
            t.Freq[sym]++;
            Push((uint)sym | (uint)nbits << 8 | ((uint)bits & ((1u << nbits) - 1)) << 13 | t.Slot << 29);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void RawBit(int bit)
        {
            // consecutive raw bits share a token (up to 16)
            if (_tokenCount > 0)
            {
                uint last = _tokens[_tokenCount - 1];
                if ((last & TokenRaw) != 0 && (last & 0x1F00) < 16 << 8)
                {
                    uint bits = (last >> 13 & 0xFFFF) << 1 | (uint)(bit & 1);
                    _tokens[_tokenCount - 1] = TokenRaw | ((last & 0x1F00) + 0x100) | bits << 13;
                    return;
                }
            }
            Push(TokenRaw | 1 << 8 | (uint)(bit & 1) << 13);
        }

        /// <summary>Writes the recorded tokens with the (now final) tables of the two slots.</summary>
        void Replay(HuffEnc? slot0, HuffEnc? slot1)
        {
            slot0 ??= slot1 ?? new HuffEnc();
            slot1 ??= slot0;
            fixed (uint* tokens = _tokens, t0 = slot0.Packed, t1 = slot1.Packed)
            fixed (byte* buf = _buf)
            {
                ulong acc = _acc;
                int n = _accBits;
                for (int i = 0; i < _tokenCount;)
                {
                    // each token appends at most 8 bytes (4 plus stuffing)
                    _accBits = n; _acc = acc;
                    Reserve(64 * 8 + 16);
                    byte* o = buf + _pos;
                    int stop = Math.Min(_tokenCount, i + 64);
                    for (; i < stop; i++)
                    {
                        uint tok = tokens[i];
                        int nbits = (int)(tok >> 8) & 31;
                        uint bits = tok >> 13 & 0xFFFF;
                        if (tok < TokenRestart)
                        {
                            uint e = ((tok & 1u << 29) != 0 ? t1 : t0)[tok & 255];
                            int len = (int)(e & 255) + nbits;
                            acc = acc << len | (e >> 8) << nbits | bits;
                            n += len;
                        }
                        else if ((tok & TokenRaw) != 0)
                        {
                            acc = acc << nbits | bits;
                            n += nbits;
                        }
                        else
                        {
                            _acc = acc; _accBits = n; _pos = (int)(o - buf);
                            FlushBits();
                            Marker(0xD0 + (int)(tok & 7));
                            acc = 0; n = 0;
                            o = buf + _pos;
                            stop = i + 1; // the marker used up buffer space: start a fresh batch
                            continue;
                        }
                        if (n >= 32) { n -= 32; o = Write32(o, (uint)(acc >> n)); }
                    }
                    _pos = (int)(o - buf);
                }
                _acc = acc; _accBits = n;
            }
            _tokenCount = 0;
        }

        // ---------------------------------------------------------------- setup

        readonly JpegEncodeOptions _opt;
        readonly int _w, _h;
        readonly Comp[] _comps;
        readonly int _hMax, _vMax, _mcusX, _mcusY;
        readonly byte[][] _quant = new byte[2][];
        readonly int _restart;
        readonly bool _genericSampling;
        readonly bool _bare, _rawColor; // TIFF strips: no application segments / no color transform
        readonly int _precision = 8;
        readonly int _maxAcSize = 10; // largest AC magnitude category the precision allows


        /// <summary>
        /// Whether a block of an interleaved scan lies wholly in the MCU padding beyond the image. Such blocks are
        /// coded as libjpeg does: no AC coefficients and the DC value of the component's previous block, which costs a
        /// few bits instead of a transform of replicated edge pixels.
        /// </summary>
        bool IsDummy(Comp c, int bx, int by) => _comps.Length > 1 && (bx >= c.ScanBlocksW || by >= c.ScanBlocksH);

        bool HasDummies => _comps.Length > 1 && _comps.Any(c => c.ScanBlocksW < c.BlocksW || c.ScanBlocksH < c.BlocksH);

        /// <summary>Gives the (already zeroed) padding blocks the DC value of the block coded before them, in MCU order.</summary>
        void FillDummyDc()
        {
            if (!HasDummies) return;
            foreach (var c in _comps)
                fixed (short* coefs = c.Coefs)
                {
                    int prev = 0;
                    for (int my = 0; my < _mcusY; my++)
                        for (int mx = 0; mx < _mcusX; mx++)
                            for (int by = 0; by < c.V; by++)
                                for (int bx = 0; bx < c.H; bx++)
                                {
                                    int x = mx * c.H + bx, y = my * c.V + by;
                                    short* b = coefs + ((nint)y * c.BlocksW + x) * 64;
                                    if (IsDummy(c, x, y)) b[0] = (short)prev;
                                    prev = b[0];
                                }
                }
        }

        public static void Encode(RawImage image, Stream output, JpegEncodeOptions options)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(output);
            if (image.SampleType != SampleType.UInt8 && image.SampleType != SampleType.UInt16)
                throw new ImageNotSupportedException($"JPEG encoding supports UInt8 and UInt16 samples, not {image.SampleType}");
            if (image.Width > 65535 || image.Height > 65535)
                throw new ImageNotSupportedException("image is too large for JPEG (65535 x 65535 at most)");
            bool wide = image.SampleType == SampleType.UInt16;
            if (options.Lossless)
            {
                if (image.Color == ColorModel.Other && image.Channels > 4)
                    throw new ImageNotSupportedException("lossless JPEG holds at most 4 components");
                int precision = options.Precision != 0 ? options.Precision : wide ? 16 : 8;
                if (precision < 2 || precision > (wide ? 16 : 8)) throw new ArgumentException($"lossless JPEG precision {precision} is not possible for {image.SampleType} samples");
                var le = new JpegEncoder(output, options);
                try { le.RunLossless(image, precision); }
                finally { le.Release(); }
                return;
            }
            if (image.Color == ColorModel.Other)
                throw new ImageNotSupportedException("JPEG encoding needs a gray or RGB image (lossless mode also takes up to 4 arbitrary channels)");
            if (options.Precision != 0 && options.Precision != (wide ? 12 : 8))
                throw new ArgumentException(wide ? "UInt16 images are encoded with 12 bit precision (or lossless with up to 16)" : "UInt8 images are encoded with 8 bit precision");
            var e = new JpegEncoder(image, output, options, false, false);
            e.Run(image);
        }

        /// <summary>
        /// One strip or tile for a JPEG compressed TIFF: a complete baseline (12 bit: extended sequential) stream with its
        /// own tables and nothing but SOI in front of them - the TIFF tags say what the components mean.
        /// With <paramref name="rawColor"/> the three channels are stored as they are at full resolution.
        /// </summary>
        public static void EncodeForTiff(RawImage image, Stream output, JpegEncodeOptions options, bool rawColor)
            => new JpegEncoder(image, output, options, true, rawColor).Run(image);

        /// <summary>Lossless mode: only the output machinery is used.</summary>
        JpegEncoder(Stream output, JpegEncodeOptions opt)
        {
            _out = output; _opt = opt;
            _comps = Array.Empty<Comp>();
        }

        JpegEncoder(RawImage image, Stream output, JpegEncodeOptions opt, bool bare, bool rawColor)
        {
            _out = output; _opt = opt;
            _bare = bare;
            _w = image.Width; _h = image.Height;
            _restart = Math.Clamp(opt.RestartInterval, 0, 65535);
            if (image.SampleType == SampleType.UInt16) { _precision = 12; _maxAcSize = 14; }
            bool gray = image.Color == ColorModel.Gray || image.Color == ColorModel.GrayAlpha;
            int quality = Math.Clamp(opt.Quality, 1, 100);
            var ss = opt.Subsampling;
            if (ss == JpegSubsampling.Auto) ss = quality >= 90 ? JpegSubsampling.Chroma444 : JpegSubsampling.Chroma420;
            int[] factors = gray ? [1, 1] : ss == JpegSubsampling.Chroma444 ? [1, 1, 1, 1, 1, 1] : ss == JpegSubsampling.Chroma422 ? [2, 1, 1, 1, 1, 1] : [2, 2, 1, 1, 1, 1];
            if (rawColor && !gray)
            {
                factors = [1, 1, 1, 1, 1, 1];
                _genericSampling = true;
                _rawColor = true;
            }
            else if (opt.SamplingFactors != null && !gray)
            {
                factors = opt.SamplingFactors;
                if (factors.Length != 6 || factors.Any(f => f < 1 || f > 4)) throw new ArgumentException("SamplingFactors needs 6 values between 1 and 4");
                if (factors[0] * factors[1] + factors[2] * factors[3] + factors[4] * factors[5] > 10) throw new ArgumentException("an MCU holds at most 10 blocks");
                _genericSampling = true;
            }
            _comps = new Comp[gray ? 1 : 3];
            for (int i = 0; i < _comps.Length; i++)
            {
                _comps[i] = new Comp { Id = i + 1, H = factors[2 * i], V = factors[2 * i + 1], Tq = i == 0 ? 0 : 1 };
                _hMax = Math.Max(_hMax, _comps[i].H);
                _vMax = Math.Max(_vMax, _comps[i].V);
            }
            _mcusX = (_w + 8 * _hMax - 1) / (8 * _hMax);
            _mcusY = (_h + 8 * _vMax - 1) / (8 * _vMax);
            foreach (var c in _comps)
            {
                c.BlocksW = _mcusX * c.H;
                c.BlocksH = _mcusY * c.V;
                c.ScanBlocksW = ((_w * c.H + _hMax - 1) / _hMax + 7) / 8;
                c.ScanBlocksH = ((_h * c.V + _vMax - 1) / _vMax + 7) / 8;
                c.Stride = c.BlocksW * 8;
                // a lone component is coded without MCU padding
                c.StatBlocksW = _comps.Length == 1 ? c.ScanBlocksW : c.BlocksW;
                c.StatBlocksH = _comps.Length == 1 ? c.ScanBlocksH : c.BlocksH;
                if ((long)c.Stride * c.BlocksH * 8 > Array.MaxLength) throw new ImageNotSupportedException("image is too large to encode");
            }

            // IJG quality scaling of the base tables (the tuned ones are given in tenths)
            int scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
            bool annexK = opt.QuantTables == JpegQuantTables.AnnexK;
            for (int t = 0; t < 2; t++)
            {
                var q = new byte[64];
                if (annexK)
                {
                    var basis = t == 0 ? JpegTables.LumaQuant : JpegTables.ChromaQuant;
                    for (int i = 0; i < 64; i++) q[i] = (byte)Math.Clamp((basis[i] * scale + 50) / 100, 1, 255);
                }
                else
                {
                    var basis = t == 0 ? JpegTables.TunedLumaQuant : JpegTables.TunedChromaQuant;
                    for (int i = 0; i < 64; i++) q[i] = (byte)Math.Clamp((basis[i] * scale + 500) / 1000, 1, 255);
                }
                _quant[t] = q;
            }
        }

        /// <summary>The fast color conversion fills the planes one MCU row at a time (else: whole planes, <see cref="FillPlanesGeneric"/>).</summary>
        bool StripPlanes => _precision == 8 && !_genericSampling;

        /// <summary>Optimized sequential tables from a statistics pass over compactly stored blocks (<see cref="EncodeTwoPass"/>).</summary>
        bool TwoPass => StripPlanes && OptimizedBaseline && !UseTrellis;

        void Run(RawImage image)
        {
            try
            {
                Allocate();
                byte[]? src = StripPlanes ? image.GetData<byte>() : null;
                int ch = image.Channels;
                if (!StripPlanes) FillPlanesGeneric(image);
                if (!_opt.Progressive) SetupBaselineTables();
                if (SinglePass)
                {
                    // standard tables, sequential: nothing depends on the whole image, so each MCU is transformed and
                    // coded in one go without ever storing the coefficients
                    WriteHeaders(image.Metadata);
                    EncodeSinglePass(src, ch);
                }
                else if (TwoPass) EncodeTwoPass(src!, ch, image.Metadata);
                else
                {
                    if (UseTrellis && _raw == null) _raw = new short[_comps.Length][];
                    for (int my = 0; my < (StripPlanes ? _mcusY : 1); my++)
                    {
                        if (StripPlanes) FillStrip(src!, ch, my);
                        int row = StripPlanes ? my : -1;
                        if (UseTrellis) TransformRaw(row); else Transform(row);
                    }
                    if (UseTrellis) RunTrellis();
                    FillDummyDc();
                    WriteHeaders(image.Metadata);
                    if (_opt.Progressive) WriteProgressive();
                    else WriteBaseline();
                }
                Marker(0xD9);
                _out.Write(_buf, 0, _pos);
                _pos = 0;
            }
            finally { Release(); }
        }

        byte[]? _cbRows, _crRows; // full resolution chroma rows of FillStrip

        /// <summary>
        /// Rents the planes (one MCU row of blocks high with <see cref="StripPlanes"/>, else the whole image) and,
        /// unless the blocks are coded as they are transformed, the coefficients.
        /// </summary>
        void Allocate()
        {
            bool coefs = !SinglePass && !TwoPass;
            foreach (var c in _comps)
            {
                int rows = StripPlanes ? 8 * c.V : 8 * c.BlocksH, size = c.Stride * rows;
                if (_precision == 8) c.Plane = ArrayPool<byte>.Shared.Rent(size);
                else c.Plane16 = ArrayPool<ushort>.Shared.Rent(size);
                if (coefs) c.Coefs = ArrayPool<short>.Shared.Rent(c.Stride * c.BlocksH * 8);
            }
            if (StripPlanes && _comps.Length > 1)
            {
                _cbRows = ArrayPool<byte>.Shared.Rent(2 * _comps[0].Stride);
                _crRows = ArrayPool<byte>.Shared.Rent(2 * _comps[0].Stride);
            }
        }

        /// <summary>Gives the rented buffers back.</summary>
        void Release()
        {
            foreach (var c in _comps)
            {
                if (c.Plane != null) ArrayPool<byte>.Shared.Return(c.Plane);
                if (c.Plane16 != null) ArrayPool<ushort>.Shared.Return(c.Plane16);
                if (c.Coefs != null) ArrayPool<short>.Shared.Return(c.Coefs);
                c.Plane = null!; c.Plane16 = null; c.Coefs = null!;
            }
            if (_cbRows != null) ArrayPool<byte>.Shared.Return(_cbRows);
            if (_crRows != null) ArrayPool<byte>.Shared.Return(_crRows);
            _cbRows = _crRows = null;
            ArrayPool<byte>.Shared.Return(_buf);
        }

        // ---------------------------------------------------------------- color conversion and downsampling

        /// <summary>
        /// Color conversion and chroma downsampling of MCU row <paramref name="my"/> (8 bit samples, 1 x 1 chroma). With
        /// <see cref="StripPlanes"/> it goes to the top of the planes, else to its place in them. Rows and columns past
        /// the image repeat the last ones.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)] // runs once per image: no point in starting unoptimized
        void FillStrip(byte[] src, int ch, int my)
        {
            int w = _w, h = _h, vMax = _vMax;
            var yc = _comps[0];
            int fullW = yc.Stride; // luma plane width: a whole number of MCUs
            int y0 = my * 8 * vMax; // first luma row of the MCU row (always inside the image)
            int top = StripPlanes ? 0 : y0;
            if (_comps.Length == 1)
            {
                for (int r = 0; r < 8; r++)
                {
                    var row = yc.Plane.AsSpan((top + r) * fullW, fullW);
                    int y = y0 + r;
                    if (y >= h) { yc.Plane.AsSpan((top + r - 1) * fullW, fullW).CopyTo(row); continue; }
                    if (ch == 1) src.AsSpan(y * w, w).CopyTo(row);
                    else for (int x = 0; x < w; x++) row[x] = src[(y * w + x) * ch];
                    row.Slice(w).Fill(row[w - 1]);
                }
                return;
            }
            var cbc = _comps[1]; var crc = _comps[2];
            // full resolution chroma rows, two of them when halving vertically
            byte[] cb = _cbRows!, cr = _crRows!;
            int ctop = StripPlanes ? 0 : my * 8;
            for (int g = 0; g < 8; g++)
            {
                int y = y0 + g * vMax;
                var cbDst = cbc.Plane.AsSpan((ctop + g) * cbc.Stride, cbc.Stride);
                var crDst = crc.Plane.AsSpan((ctop + g) * crc.Stride, crc.Stride);
                if (y >= h)
                {
                    for (int r = 0; r < vMax; r++)
                    {
                        int l = top + g * vMax + r;
                        yc.Plane.AsSpan((l - 1) * fullW, fullW).CopyTo(yc.Plane.AsSpan(l * fullW, fullW));
                    }
                    cbc.Plane.AsSpan((ctop + g - 1) * cbc.Stride, cbc.Stride).CopyTo(cbDst);
                    crc.Plane.AsSpan((ctop + g - 1) * crc.Stride, crc.Stride).CopyTo(crDst);
                    continue;
                }
                for (int r = 0; r < vMax; r++)
                {
                    int sy = Math.Min(y + r, h - 1); // odd height: the last row counts twice
                    var yrow = yc.Plane.AsSpan((top + g * vMax + r) * fullW, fullW);
                    var cbr = cb.AsSpan(r * fullW, fullW); var crr = cr.AsSpan(r * fullW, fullW);
                    RgbToYcc(src.AsSpan(sy * w * ch, w * ch), ch, yrow, cbr, crr, w);
                    yrow.Slice(w).Fill(yrow[w - 1]);
                    cbr.Slice(w).Fill(cbr[w - 1]);
                    crr.Slice(w).Fill(crr[w - 1]);
                }
                Downsample(cb, fullW, cbDst);
                Downsample(cr, fullW, crDst);
            }
        }

        /// <summary>
        /// Straightforward version of <see cref="FillStrip"/> for the cases without a fast path (12 bit samples,
        /// arbitrary sampling factors): full resolution planes first, then an area average down to each component's size.
        /// </summary>
        void FillPlanesGeneric(RawImage image)
        {
            var full = new ushort[_comps.Length][];
            for (int i = 0; i < full.Length; i++) full[i] = ArrayPool<ushort>.Shared.Rent(_w * _h);
            try { FillPlanesFrom(image, full); }
            finally { foreach (var f in full) ArrayPool<ushort>.Shared.Return(f); }
        }

        void FillPlanesFrom(RawImage image, ushort[][] full)
        {
            int w = _w, h = _h, ch = image.Channels, nc = _comps.Length;
            int center = 1 << (_precision - 1), max = (1 << _precision) - 1;
            byte[]? s8 = image.SampleType == SampleType.UInt8 ? image.GetData<byte>() : null;
            ushort[]? s16 = s8 == null ? image.GetData<ushort>() : null;
            for (int i = 0, n = w * h; i < n; i++)
            {
                int r = s8 != null ? s8[i * ch] : s16![i * ch];
                if (r > max) throw new ArgumentException($"sample value {r} exceeds the {_precision} bit range");
                if (nc == 1) { full[0][i] = (ushort)r; continue; }
                int g = s8 != null ? s8[i * ch + 1] : s16![i * ch + 1], b = s8 != null ? s8[i * ch + 2] : s16![i * ch + 2];
                if (g > max || b > max) throw new ArgumentException($"sample value {Math.Max(g, b)} exceeds the {_precision} bit range");
                if (_rawColor)
                {
                    full[0][i] = (ushort)r; full[1][i] = (ushort)g; full[2][i] = (ushort)b;
                    continue;
                }
                full[0][i] = (ushort)((19595 * r + 38470 * g + 7471 * b + 32768) >> 16);
                full[1][i] = (ushort)((-11059 * r - 21709 * g + 32768 * b + (center << 16) + 32767) >> 16);
                full[2][i] = (ushort)((32768 * r - 27439 * g - 5329 * b + (center << 16) + 32767) >> 16);
            }
            for (int i = 0; i < nc; i++)
            {
                var c = _comps[i];
                int sw = (w * c.H + _hMax - 1) / _hMax, sh = (h * c.V + _vMax - 1) / _vMax;
                if (_hMax % c.H == 0 && _vMax % c.V == 0)
                {
                    // whole-number ratios: plain averages over fx x fy samples; rows / columns past the image repeat the edge
                    int fx = _hMax / c.H, fy = _vMax / c.V, planeRows = c.BlocksH * 8, st = c.Stride;
                    var src = full[i];
                    var acc = new int[sw];
                    for (int y = 0; y < planeRows; y++)
                    {
                        if (y < sh)
                        {
                            Array.Clear(acc);
                            for (int j = 0; j < fy; j++)
                            {
                                int o = Math.Min(y * fy + j, h - 1) * w;
                                int whole = fx == 1 ? sw : w / fx; // outputs whose inputs all exist
                                if (fx == 1) for (int x = 0; x < whole; x++) acc[x] += src[o + x];
                                else if (fx == 2) for (int x = 0; x < whole; x++) acc[x] += src[o + 2 * x] + src[o + 2 * x + 1];
                                else for (int x = 0; x < whole; x++) for (int k = 0; k < fx; k++) acc[x] += src[o + x * fx + k];
                                for (int x = whole; x < sw; x++) for (int k = 0; k < fx; k++) acc[x] += src[o + Math.Min(x * fx + k, w - 1)];
                            }
                            int area = fx * fy, round = area >> 1;
                            if (c.Plane16 != null) for (int x = 0; x < sw; x++) c.Plane16[y * st + x] = (ushort)((acc[x] + round) / area);
                            else for (int x = 0; x < sw; x++) c.Plane[y * st + x] = (byte)((acc[x] + round) / area);
                            if (c.Plane16 != null) c.Plane16.AsSpan(y * st + sw, st - sw).Fill(c.Plane16[y * st + sw - 1]);
                            else c.Plane.AsSpan(y * st + sw, st - sw).Fill(c.Plane[y * st + sw - 1]);
                        }
                        else if (c.Plane16 != null) c.Plane16.AsSpan((sh - 1) * st, st).CopyTo(c.Plane16.AsSpan(y * st, st));
                        else c.Plane.AsSpan((sh - 1) * st, st).CopyTo(c.Plane.AsSpan(y * st, st));
                    }
                    continue;
                }
                // horizontal, then vertical box filter; source samples past the edge repeat the last one
                var tmp = new long[sw * h];
                for (int y = 0; y < h; y++) BoxFilter(full[i], y * w, 1, w, tmp, y * sw, 1, sw, c.H, _hMax);
                var col = new long[h];
                var outCol = new long[sh];
                int rows = c.BlocksH * 8, stride = c.Stride;
                for (int x = 0; x < sw; x++)
                {
                    for (int y = 0; y < h; y++) col[y] = tmp[y * sw + x];
                    BoxFilter(col, sh, outCol, c.V, _vMax);
                    for (int y = 0; y < rows; y++)
                    {
                        // both passes left the sums unscaled: divide by the two box sizes at once
                        long div = (long)_hMax * _vMax;
                        int v = (int)((outCol[Math.Min(y, sh - 1)] + div / 2) / div);
                        if (c.Plane16 != null) c.Plane16[y * stride + x] = (ushort)v; else c.Plane[y * stride + x] = (byte)v;
                    }
                }
                for (int y = 0; y < rows; y++)
                {
                    if (c.Plane16 != null) c.Plane16.AsSpan(y * stride + sw, stride - sw).Fill(c.Plane16[y * stride + sw - 1]);
                    else c.Plane.AsSpan(y * stride + sw, stride - sw).Fill(c.Plane[y * stride + sw - 1]);
                }
            }
        }

        /// <summary>
        /// Area average of n source samples onto m outputs where one output spans den / num source samples. The
        /// results are sums over a window of weight den (the caller divides).
        /// </summary>
        static void BoxFilter(ushort[] src, int srcStart, int srcStep, int n, long[] dst, int dstStart, int dstStep, int m, int num, int den)
        {
            for (int i = 0; i < m; i++)
            {
                // in units of 1/num source samples the output covers [i * den, (i + 1) * den)
                long lo = (long)i * den, hi = lo + den, sum = 0;
                for (long j = lo / num; j * num < hi; j++)
                {
                    long overlap = Math.Min(hi, (j + 1) * num) - Math.Max(lo, j * num);
                    sum += overlap * src[srcStart + (int)Math.Min(j, n - 1) * srcStep];
                }
                dst[dstStart + i * dstStep] = sum;
            }
        }

        static void BoxFilter(long[] src, int m, long[] dst, int num, int den)
        {
            int n = src.Length;
            for (int i = 0; i < m; i++)
            {
                long lo = (long)i * den, hi = lo + den, sum = 0;
                for (long j = lo / num; j * num < hi; j++)
                    sum += (Math.Min(hi, (j + 1) * num) - Math.Max(lo, j * num)) * src[Math.Min(j, n - 1)];
                dst[i] = sum;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Downsample(byte[] full, int fullW, Span<byte> dst)
        {
            if (_hMax == 1) { full.AsSpan(0, fullW).CopyTo(dst); return; }
            int n = dst.Length, x = 0;
            fixed (byte* a = full, d = dst)
            {
                byte* b = a + fullW;
                bool v2 = _vMax == 2;
                if (Vector.IsHardwareAccelerated)
                {
                    // each 16 bit lane holds a horizontal pair of samples; the rounding bias alternates between
                    // output columns (0,1 or 1,2) so flat areas do not drift
                    int vc = Vector<ushort>.Count;
                    Span<ushort> biasInit = stackalloc ushort[vc];
                    for (int i = 0; i < vc; i++) biasInit[i] = (ushort)((v2 ? 1 : 0) + (i & 1));
                    var bias = new Vector<ushort>(biasInit);
                    var low = new Vector<ushort>(0xFF);
                    for (; x + 2 * vc <= n; x += 2 * vc)
                    {
                        var p0 = Vector.Load((ushort*)(a + 2 * x));
                        var p1 = Vector.Load((ushort*)(a + 2 * x) + vc);
                        var s0 = (p0 & low) + Vector.ShiftRightLogical(p0, 8) + bias;
                        var s1 = (p1 & low) + Vector.ShiftRightLogical(p1, 8) + bias;
                        if (v2)
                        {
                            var q0 = Vector.Load((ushort*)(b + 2 * x));
                            var q1 = Vector.Load((ushort*)(b + 2 * x) + vc);
                            s0 += (q0 & low) + Vector.ShiftRightLogical(q0, 8);
                            s1 += (q1 & low) + Vector.ShiftRightLogical(q1, 8);
                        }
                        int shift = v2 ? 2 : 1;
                        Vector.Store(Vector.Narrow(Vector.ShiftRightLogical(s0, shift), Vector.ShiftRightLogical(s1, shift)), d + x);
                    }
                }
                if (v2)
                    for (; x < n; x++) d[x] = (byte)((a[2 * x] + a[2 * x + 1] + b[2 * x] + b[2 * x + 1] + 1 + (x & 1)) >> 2);
                else
                    for (; x < n; x++) d[x] = (byte)((a[2 * x] + a[2 * x + 1] + (x & 1)) >> 1);
            }
        }

        /// <summary>8 values below 256 in 32 bit lanes to 8 bytes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Store8(Vector256<int> v, byte* dst)
        {
            var words = Avx2.PackSignedSaturate(v, v);
            var bytes = Avx2.PackUnsignedSaturate(words, words).AsUInt32();
            Unsafe.WriteUnaligned(dst, bytes.GetElement(0));
            Unsafe.WriteUnaligned(dst + 4, bytes.GetElement(4));
        }

        /// <summary>Byte shuffle with a run time mask (Vector128.Shuffle is only accelerated for constant masks). Indices of 0x80 give zero.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<byte> Shuffle(Vector128<byte> v, Vector128<byte> idx)
            => Ssse3.IsSupported ? Ssse3.Shuffle(v, idx) : AdvSimd.Arm64.VectorTableLookup(v, idx);

        /// <summary>JFIF (BT.601 full range) RGB to YCbCr in 16 bit fixed point.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void RgbToYcc(ReadOnlySpan<byte> src, int ch, Span<byte> yo, Span<byte> cbo, Span<byte> cro, int w)
        {
            const int half = 32768, center = (128 << 16) + 32767;
            fixed (byte* sp = src, yp = yo, cbp = cbo, crp = cro)
            {
                byte* s = sp;
                int x = 0;
                if (Avx2.IsSupported)
                {
                    // 8 pixels per step with 16 bit multiply-adds; the coefficients 38470 (G to Y) and 32768 do not
                    // fit a signed 16 bit factor, so their 32768 part is added as a shift
                    var spread3 = Vector128.Create((byte)0, 1, 2, 0x80, 3, 4, 5, 0x80, 6, 7, 8, 0x80, 9, 10, 11, 0x80);
                    var lowBytes = Vector256.Create(0x00FF00FF).AsInt16();
                    var yRB = Vector256.Create(19595 | 7471 << 16).AsInt16();
                    var yG = Vector256.Create(38470 - 32768).AsInt16();
                    var cbR = Vector256.Create(-11059 & 0xFFFF).AsInt16();
                    var cbG = Vector256.Create(-21709 & 0xFFFF).AsInt16();
                    var crG = Vector256.Create(-27439 & 0xFFFF).AsInt16();
                    var crB = Vector256.Create(-5329 << 16).AsInt16();
                    var vh = Vector256.Create(half); var vc = Vector256.Create(center);
                    var byteMask = Vector256.Create(255);
                    int end8 = w - (ch == 3 ? 10 : 8);
                    for (; x <= end8; x += 8, s += 8 * ch)
                    {
                        var px = ch == 3
                            ? Vector256.Create(Ssse3.Shuffle(Sse2.LoadVector128(s), spread3), Ssse3.Shuffle(Sse2.LoadVector128(s + 12), spread3))
                            : Avx.LoadVector256(s);
                        var rb = Avx2.And(px.AsInt16(), lowBytes);
                        var g32 = Avx2.And(Avx2.ShiftRightLogical(px.AsInt32(), 8), byteMask); // (the top byte may be alpha)
                        var g = g32.AsInt16();
                        var r32 = Avx2.And(px.AsInt32(), byteMask);
                        var b32 = Avx2.And(Avx2.ShiftRightLogical(px.AsInt32(), 16), byteMask);
                        var yv = Avx2.Add(Avx2.Add(Avx2.MultiplyAddAdjacent(rb, yRB), Avx2.MultiplyAddAdjacent(g, yG)), Avx2.Add(Avx2.ShiftLeftLogical(g32, 15), vh));
                        var cb = Avx2.Add(Avx2.Add(Avx2.MultiplyAddAdjacent(rb, cbR), Avx2.MultiplyAddAdjacent(g, cbG)), Avx2.Add(Avx2.ShiftLeftLogical(b32, 15), vc));
                        var cr = Avx2.Add(Avx2.Add(Avx2.MultiplyAddAdjacent(rb, crB), Avx2.MultiplyAddAdjacent(g, crG)), Avx2.Add(Avx2.ShiftLeftLogical(r32, 15), vc));
                        Store8(Avx2.ShiftRightLogical(yv, 16), yp + x);
                        Store8(Avx2.ShiftRightLogical(cb, 16), cbp + x);
                        Store8(Avx2.ShiftRightLogical(cr, 16), crp + x);
                    }
                }
                else if (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported)
                {
                    // 4 pixels per step in 32 bit lanes; reads 16 source bytes, so stop while that stays inside the row
                    var spread = ch == 3
                        ? Vector128.Create((byte)0, 1, 2, 0x80, 3, 4, 5, 0x80, 6, 7, 8, 0x80, 9, 10, 11, 0x80)
                        : Vector128.Create((byte)0, 1, 2, 0x80, 4, 5, 6, 0x80, 8, 9, 10, 0x80, 12, 13, 14, 0x80);
                    var gather = Vector128.Create((byte)0, 4, 8, 12, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
                    var m = Vector128.Create(255);
                    var vhalf = Vector128.Create(half); var vcenter = Vector128.Create(center);
                    int last = w - (ch == 3 ? 6 : 4);
                    for (; x <= last; x += 4, s += 4 * ch)
                    {
                        var px = Shuffle(Vector128.Load(s), spread).AsInt32();
                        var r = px & m;
                        var g = Vector128.ShiftRightLogical(px, 8) & m;
                        var b = Vector128.ShiftRightLogical(px, 16);
                        var yv = Vector128.ShiftRightLogical(r * 19595 + g * 38470 + b * 7471 + vhalf, 16);
                        var cb = Vector128.ShiftRightLogical(Vector128.ShiftLeft(b, 15) - r * 11059 - g * 21709 + vcenter, 16);
                        var cr = Vector128.ShiftRightLogical(Vector128.ShiftLeft(r, 15) - g * 27439 - b * 5329 + vcenter, 16);
                        Unsafe.WriteUnaligned(yp + x, Shuffle(yv.AsByte(), gather).AsUInt32().ToScalar());
                        Unsafe.WriteUnaligned(cbp + x, Shuffle(cb.AsByte(), gather).AsUInt32().ToScalar());
                        Unsafe.WriteUnaligned(crp + x, Shuffle(cr.AsByte(), gather).AsUInt32().ToScalar());
                    }
                }
                for (; x < w; x++, s += ch)
                {
                    int r = s[0], g = s[1], b = s[2];
                    yp[x] = (byte)((19595 * r + 38470 * g + 7471 * b + half) >> 16);
                    cbp[x] = (byte)((-11059 * r - 21709 * g + 32768 * b + center) >> 16);
                    crp[x] = (byte)((32768 * r - 27439 * g - 5329 * b + center) >> 16);
                }
            }
        }

        // ---------------------------------------------------------------- DCT

        /// <summary>The factor the quantizers multiply the DCT output (8 times the coefficient) with.</summary>
        internal static float Reciprocal(int q) => 1f / (8 * q);

        /// <summary>Quantized DCT of MCU row <paramref name="my"/> (at the top of strip planes), or of every block for -1.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)] // runs once per image: no point in starting unoptimized
        void Transform(int my)
        {
            float* recip = stackalloc float[64];
            int* m = stackalloc int[64];
            foreach (var c in _comps)
            {
                var q = _quant[c.Tq];
                // the quantizers want the divisors transposed and scaled by the DCT's factor of 8
                for (int i = 0; i < 64; i++) recip[(i & 7) * 8 + (i >> 3)] = Reciprocal(q[i]);
                // statistics for optimized tables are taken while the block is still in the cache
                int[]? stats = OptimizedBaseline ? c.Ac.Freq : null;
                fixed (byte* plane = c.Plane)
                fixed (ushort* plane16 = c.Plane16)
                fixed (short* coefs = c.Coefs)
                fixed (int* freq = stats)
                {
                    int by0 = my < 0 ? 0 : my * c.V, by1 = my < 0 ? c.BlocksH : by0 + c.V;
                    int top = StripPlanes ? by0 : 0; // block row at the top of the plane
                    short* o = coefs + (nint)by0 * c.BlocksW * 64;
                    for (int by = by0; by < by1; by++)
                    {
                        if (plane16 == null)
                        {
                            byte* row = plane + (nint)(by - top) * 8 * c.Stride;
                            for (int bx = 0; bx < c.BlocksW; bx++, o += 64)
                            {
                                if (IsDummy(c, bx, by))
                                {
                                    new Span<short>(o, 64).Clear();
                                    if (freq != null) freq[0]++; // just an end of block
                                    continue;
                                }
                                JpegDct.FdctQuantize(row + bx * 8, c.Stride, recip, o);
                                if (freq != null && bx < c.StatBlocksW && by < c.StatBlocksH) GatherAc(o, freq, _maxAcSize);
                            }
                            continue;
                        }
                        for (int bx = 0; bx < c.BlocksW; bx++, o += 64)
                        {
                            if (IsDummy(c, bx, by))
                            {
                                new Span<short>(o, 64).Clear();
                                if (freq != null) freq[0]++;
                                continue;
                            }
                            JpegDct.FdctQuantizeWide(plane16 + ((nint)(by - top) * c.Stride + bx) * 8, c.Stride, 1 << (_precision - 1), recip, o);
                            if (freq != null && bx < c.StatBlocksW && by < c.StatBlocksH) GatherAc(o, freq, _maxAcSize);
                        }
                    }
                }
            }
        }

        // ---------------------------------------------------------------- headers

        void WriteHeaders(ImageMetadata? meta)
        {
            WriteMetadata(_bare ? null : meta, jfif: !_bare);
            for (int t = 0; t < (_comps.Length == 1 ? 1 : 2); t++)
            {
                Marker(0xDB); U16(67); Byte(t);
                for (int i = 0; i < 64; i++) Byte(_quant[t][JpegTables.ZigZag[i]]);
            }

            // baseline, extended sequential (needed beyond 8 bit) or progressive
            Marker(_opt.Arithmetic ? (_opt.Progressive ? 0xCA : 0xC9) : _opt.Progressive ? 0xC2 : _precision == 8 ? 0xC0 : 0xC1);
            U16(8 + 3 * _comps.Length); Byte(_precision); U16(_h); U16(_w); Byte(_comps.Length);
            foreach (var c in _comps) { Byte(c.Id); Byte(c.H << 4 | c.V); Byte(c.Tq); }

            if (_restart != 0) { Marker(0xDD); U16(4); U16(_restart); }
        }

        /// <summary>SOI and the application segments. A JFIF header implies gray or YCbCr, so RGB / raw data goes without.</summary>
        void WriteMetadata(ImageMetadata? meta, bool jfif)
        {
            Marker(0xD8);
            if (jfif)
            {
                // JFIF 1.01
                Marker(0xE0); U16(16);
                Bytes("JFIF\0"u8); Byte(1); Byte(1);
                int dx = meta == null ? 0 : (int)Math.Round(meta.DpiX), dy = meta == null ? 0 : (int)Math.Round(meta.DpiY);
                if (dx >= 1 && dy >= 1 && dx <= 65535 && dy <= 65535) { Byte(1); U16(dx); U16(dy); }
                else { Byte(0); U16(1); U16(1); }
                Byte(0); Byte(0);
            }

            byte[]? exif = ExifTags.ForWriting(meta);
            if (exif != null && exif.Length <= 65533 - 6)
            {
                Marker(0xE1); U16(exif.Length + 8);
                Bytes("Exif\0\0"u8);
                Bytes(exif);
            }

            // XMP: one APP1 segment, or a standard and an extended part (XMP part 3) for packets beyond 64 KB
            byte[]? xmp = ExifTags.XmpForWriting(meta);
            if (xmp is { Length: > 0 })
            {
                System.Collections.Generic.List<byte[]>? segments = null;
                try { segments = XmpEditor.JpegSegments(xmp); }
                catch (ImageNotSupportedException) { }   // (an oversized packet that is not XML is left out)
                if (segments != null)
                    foreach (var seg in segments)
                    {
                        Marker(0xE1); U16(seg.Length + 2);
                        Bytes(seg);
                    }
            }

            byte[]? icc = meta?.IccProfile;
            const int iccChunk = 65533 - 14;
            if (icc != null && icc.Length > 0 && icc.Length <= iccChunk * 255)
            {
                int count = (icc.Length + iccChunk - 1) / iccChunk;
                for (int i = 0; i < count; i++)
                {
                    int n = Math.Min(iccChunk, icc.Length - i * iccChunk);
                    Marker(0xE2); U16(n + 16);
                    Bytes("ICC_PROFILE\0"u8); Byte(i + 1); Byte(count);
                    Bytes(icc.AsSpan(i * iccChunk, n));
                }
            }
        }

        void WriteHuffmanTable(HuffEnc t, int cls, int id)
        {
            Marker(0xC4); U16(19 + t.Values.Length); Byte(cls << 4 | id);
            Bytes(t.Counts); Bytes(t.Values);
        }

        void WriteScanHeader(Comp[] comps, int[] dcIds, int[] acIds, int ss, int se, int ah, int al)
        {
            Marker(0xDA); U16(6 + 2 * comps.Length); Byte(comps.Length);
            for (int i = 0; i < comps.Length; i++) { Byte(comps[i].Id); Byte(dcIds[i] << 4 | acIds[i]); }
            Byte(ss); Byte(se); Byte(ah << 4 | al);
        }

        // ---------------------------------------------------------------- scans

        bool SinglePass => !UseTrellis && !_opt.Progressive && !_opt.Arithmetic && !OptimizedBaseline && _precision == 8;

        bool OptimizedBaseline => !_opt.Progressive && !_opt.Arithmetic && (_opt.OptimizeHuffman || _precision != 8);

        /// <summary>The standard tables, or empty ones to be built from the image's statistics (always beyond 8 bit: the standard tables stop at 8 bit magnitudes).</summary>
        void SetupBaselineTables()
        {
            bool optimize = OptimizedBaseline;
            var dc0 = optimize ? new HuffEnc() : new HuffEnc(JpegTables.DcLumaCounts, JpegTables.DcValues);
            var ac0 = optimize ? new HuffEnc() : new HuffEnc(JpegTables.AcLumaCounts, JpegTables.AcLumaValues);
            var dc1 = optimize ? new HuffEnc() : new HuffEnc(JpegTables.DcChromaCounts, JpegTables.DcValues);
            var ac1 = optimize ? new HuffEnc() : new HuffEnc(JpegTables.AcChromaCounts, JpegTables.AcChromaValues);
            for (int i = 0; i < _comps.Length; i++)
            {
                _comps[i].Dc = i == 0 ? dc0 : dc1;
                _comps[i].Ac = i == 0 ? ac0 : ac1;
            }
        }

        /// <summary>The tables of a sequential scan and its header.</summary>
        void WriteSequentialHeader()
        {
            bool color = _comps.Length == 3;
            WriteHuffmanTable(_comps[0].Dc, 0, 0); WriteHuffmanTable(_comps[0].Ac, 1, 0);
            if (color) { WriteHuffmanTable(_comps[1].Dc, 0, 1); WriteHuffmanTable(_comps[1].Ac, 1, 1); }
            WriteScanHeader(_comps, color ? [0, 1, 1] : [0], color ? [0, 1, 1] : [0], 0, 63, 0, 0);
        }

        /// <param name="src">The 8 bit samples with <see cref="StripPlanes"/> (each MCU row is converted when it is coded), else null (the planes are complete).</param>
        /// <param name="ch">Channels of <paramref name="src"/>.</param>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)] // runs once per image: no point in starting unoptimized
        void EncodeSinglePass(byte[]? src, int ch)
        {
            bool single = _comps.Length == 1;
            WriteSequentialHeader();

            int nc = _comps.Length;
            float* recips = stackalloc float[2 * 64];
            for (int t = 0; t < 2; t++)
                for (int i = 0; i < 64; i++) recips[t * 64 + (i & 7) * 8 + (i >> 3)] = Reciprocal(_quant[t][i]);
            short* blk = stackalloc short[64];
            int mcusX = single ? _comps[0].ScanBlocksW : _mcusX, mcusY = single ? _comps[0].ScanBlocksH : _mcusY;
            int todo = _restart, rst = 0;
            foreach (var c in _comps) { c.Pred = 0; c.LastDc = 0; }
            var c0 = _comps[0]; var c1 = _comps[nc > 1 ? 1 : 0]; var c2 = _comps[nc > 2 ? 2 : 0];
            fixed (byte* buf = _buf, p0 = c0.Plane, p1 = c1.Plane, p2 = c2.Plane)
            fixed (uint* dc0 = c0.Dc.Packed, dc1 = c1.Dc.Packed)
            fixed (ulong* acx0 = c0.Ac.Extended, acx1 = c1.Ac.Extended)
            {
                byte** planes = stackalloc byte*[3];
                planes[0] = p0; planes[1] = p1; planes[2] = p2;
                BitSink sink = default;
                sink.Acc = _acc; sink.Bits = _accBits; sink.Out = (nint)(buf + _pos);
                for (int my = 0; my < mcusY; my++)
                {
                    if (src != null) FillStrip(src, ch, my);
                    for (int mx = 0; mx < mcusX; mx++)
                    {
                        // the bit state lives in the sink while blocks are coded; the stream methods see the fields
                        _acc = sink.Acc; _accBits = sink.Bits; _pos = (int)((byte*)sink.Out - buf);
                        if (_restart != 0)
                        {
                            if (todo == 0)
                            {
                                FlushBits();
                                Marker(0xD0 + rst);
                                rst = (rst + 1) & 7;
                                todo = _restart;
                                foreach (var c in _comps) c.Pred = 0;
                            }
                            todo--;
                        }
                        Reserve(10 * 600);
                        sink.Acc = _acc; sink.Bits = _accBits; sink.Out = (nint)(buf + _pos);
                        for (int i = 0; i < nc; i++)
                        {
                            var c = _comps[i];
                            int h = single ? 1 : c.H, v = single ? 1 : c.V;
                            nint stride = c.Stride;
                            byte* p = planes[i] + ((src != null ? 0 : (nint)my * v * stride) + mx * h) * 8;
                            for (int by = 0; by < v; by++, p += 8 * stride)
                                for (int bx = 0; bx < h; bx++)
                                {
                                    ulong mask;
                                    if (!single && (mx * h + bx >= c.ScanBlocksW || my * v + by >= c.ScanBlocksH))
                                    {
                                        // padding block (see IsDummy)
                                        new Span<short>(blk, 64).Clear();
                                        blk[0] = (short)c.LastDc;
                                        mask = blk[0] != 0 ? 1UL : 0;
                                    }
                                    else
                                    {
                                        JpegDct.FdctQuantizeTransposed(p + bx * 8, stride, recips + (i == 0 ? 0 : 64), blk);
                                        mask = JpegTables.TransposedMaskToZigZag(JpegTables.NonZeroMask(blk));
                                    }
                                    c.LastDc = blk[0];
                                    int diff = blk[0] - c.Pred;
                                    c.Pred = blk[0];
                                    if (i == 0) EmitBlockFast(blk, JpegTables.ZigZagTransposed, mask, diff, dc0, acx0, &sink);
                                    else EmitBlockFast(blk, JpegTables.ZigZagTransposed, mask, diff, dc1, acx1, &sink);
                                }
                        }
                    }
                }
                _acc = sink.Acc; _accBits = sink.Bits; _pos = (int)((byte*)sink.Out - buf);
            }
            FlushBits();
        }

        /// <summary>
        /// Quantized blocks between the two passes of <see cref="EncodeTwoPass"/>, in scan order and compact form: the
        /// DC value (16 bits), then for each nonzero AC coefficient its zigzag index (bit 7 set: a 16 bit value
        /// follows, else an 8 bit one) and value, then a 0 byte. The chunks are rented, pinned and twice the size
        /// of the one before, up to 16 MB; a block never straddles two.
        /// </summary>
        sealed class BlockStore : IDisposable
        {
            public const int MaxBlock = 2 + 63 * 3 + 1;
            readonly List<(byte[] Array, GCHandle Pin, int Used)> _chunks = new();
            byte* _p, _end;

            /// <summary>Room for one more block; <see cref="Commit"/> says how much of it was used.</summary>
            public byte* Reserve()
            {
                if (_end - _p >= MaxBlock) return _p;
                if (_chunks.Count > 0) Seal();
                int size = _chunks.Count == 0 ? 1 << 16 : Math.Min(_chunks[^1].Array.Length * 2, 1 << 24);
                var a = ArrayPool<byte>.Shared.Rent(size);
                var pin = GCHandle.Alloc(a, GCHandleType.Pinned);
                _chunks.Add((a, pin, 0));
                _p = (byte*)pin.AddrOfPinnedObject();
                _end = _p + a.Length;
                return _p;
            }

            public void Commit(byte* p) => _p = p;

            void Seal()
            {
                var last = _chunks[^1];
                _chunks[^1] = (last.Array, last.Pin, (int)(_p - (byte*)last.Pin.AddrOfPinnedObject()));
            }

            /// <summary>Ends writing.</summary>
            public void Close() { if (_chunks.Count > 0) Seal(); }

            public int Count => _chunks.Count;
            public byte* Start(int i) => (byte*)_chunks[i].Pin.AddrOfPinnedObject();
            public byte* End(int i) => Start(i) + _chunks[i].Used;

            public void Dispose()
            {
                foreach (var c in _chunks) { c.Pin.Free(); ArrayPool<byte>.Shared.Return(c.Array); }
                _chunks.Clear();
                _p = _end = null;
            }
        }

        /// <summary>
        /// Sequential 8 bit scan with optimized tables: the first pass transforms one MCU row at a time, counts the
        /// symbols and keeps the quantized blocks in a <see cref="BlockStore"/>; the second codes them.
        /// </summary>
        void EncodeTwoPass(byte[] src, int ch, ImageMetadata? meta)
        {
            using var store = new BlockStore();
            GatherBlocks(src, ch, store);
            store.Close();
            foreach (var c in _comps.Take(_comps.Length == 1 ? 1 : 2)) { c.Dc.BuildFromFrequencies(); c.Ac.BuildFromFrequencies(); }
            WriteHeaders(meta);
            WriteSequentialHeader();
            WriteStoredBlocks(store);
        }

        /// <summary>Counts down the restart interval once per MCU: true for the MCU that follows a restart marker.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool RestartAt(ref int todo)
        {
            if (_restart == 0) return false;
            bool at = todo == 0;
            if (at) todo = _restart;
            todo--;
            return at;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)] // runs once per image: no point in starting unoptimized
        void GatherBlocks(byte[] src, int ch, BlockStore store)
        {
            int nc = _comps.Length;
            bool single = nc == 1;
            float* recips = stackalloc float[2 * 64];
            for (int t = 0; t < 2; t++)
                for (int i = 0; i < 64; i++) recips[t * 64 + (i & 7) * 8 + (i >> 3)] = Reciprocal(_quant[t][i]);
            short* blk = stackalloc short[64];
            int mcusX = single ? _comps[0].ScanBlocksW : _mcusX, mcusY = single ? _comps[0].ScanBlocksH : _mcusY;
            int todo = _restart, maxAc = _maxAcSize;
            foreach (var c in _comps) { c.Pred = 0; c.LastDc = 0; }
            for (int my = 0; my < mcusY; my++)
            {
                FillStrip(src, ch, my);
                for (int mx = 0; mx < mcusX; mx++)
                {
                    if (RestartAt(ref todo)) foreach (var c in _comps) c.Pred = 0;
                    for (int i = 0; i < nc; i++)
                    {
                        var c = _comps[i];
                        int h = single ? 1 : c.H, v = single ? 1 : c.V;
                        nint stride = c.Stride;
                        fixed (byte* plane = c.Plane)
                        fixed (int* dcFreq = c.Dc.Freq, acFreq = c.Ac.Freq)
                        {
                            byte* p = plane + (nint)mx * h * 8;
                            for (int by = 0; by < v; by++, p += 8 * stride)
                                for (int bx = 0; bx < h; bx++)
                                {
                                    if (!single && (mx * h + bx >= c.ScanBlocksW || my * v + by >= c.ScanBlocksH))
                                    {
                                        // padding block (see IsDummy)
                                        new Span<short>(blk, 64).Clear();
                                        blk[0] = (short)c.LastDc;
                                    }
                                    else JpegDct.FdctQuantize(p + bx * 8, stride, recips + (i == 0 ? 0 : 64), blk);
                                    c.LastDc = blk[0];
                                    int diff = blk[0] - c.Pred;
                                    c.Pred = blk[0];
                                    dcFreq[BitLength(Math.Abs(diff))]++;
                                    GatherAc(blk, acFreq, maxAc);
                                    store.Commit(PutBlock(blk, store.Reserve()));
                                }
                        }
                    }
                }
            }
        }

        /// <summary>Writes a zigzag ordered block in the <see cref="BlockStore"/> form; returns the end.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte* PutBlock(short* blk, byte* o)
        {
            Unsafe.WriteUnaligned(o, blk[0]);
            o += 2;
            ulong mask = JpegTables.NonZeroMask(blk) & ~1UL;
            while (mask != 0)
            {
                int k = BitOperations.TrailingZeroCount(mask);
                mask &= mask - 1;
                int v = blk[k];
                if ((uint)(v + 128) < 256) { o[0] = (byte)k; o[1] = (byte)v; o += 2; }
                else { o[0] = (byte)(k | 0x80); Unsafe.WriteUnaligned(o + 1, (short)v); o += 3; }
            }
            *o++ = 0;
            return o;
        }

        /// <summary>Second pass of <see cref="EncodeTwoPass"/>: codes the stored blocks with <see cref="EmitBlockFast"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)] // runs once per image: no point in starting unoptimized
        void WriteStoredBlocks(BlockStore store)
        {
            int nc = _comps.Length;
            bool single = nc == 1;
            int mcusX = single ? _comps[0].ScanBlocksW : _mcusX, mcusY = single ? _comps[0].ScanBlocksH : _mcusY;
            int todo = _restart, rst = 0;
            foreach (var c in _comps) c.Pred = 0;
            short* blk = stackalloc short[64]; // (only the positions in a block's mask are read)
            int chunk = 0;
            byte* q = store.Count > 0 ? store.Start(0) : null, end = store.Count > 0 ? store.End(0) : null;
            var c0 = _comps[0]; var c1 = _comps[nc > 1 ? 1 : 0];
            BitSink sink = default;
            fixed (byte* buf = _buf)
            fixed (uint* dc0 = c0.Dc.Packed, dc1 = c1.Dc.Packed)
            fixed (ulong* acx0 = c0.Ac.Extended, acx1 = c1.Ac.Extended)
            {
                for (int my = 0; my < mcusY; my++)
                    for (int mx = 0; mx < mcusX; mx++)
                    {
                        if (RestartAt(ref todo))
                        {
                            FlushBits();
                            Marker(0xD0 + rst);
                            rst = (rst + 1) & 7;
                            foreach (var c in _comps) c.Pred = 0;
                        }
                        Reserve(10 * 600);
                        sink.Acc = _acc; sink.Bits = _accBits; sink.Out = (nint)(buf + _pos);
                        for (int i = 0; i < nc; i++)
                        {
                            var c = _comps[i];
                            int blocks = single ? 1 : c.H * c.V;
                            for (int b = 0; b < blocks; b++)
                            {
                                if (q == end) { chunk++; q = store.Start(chunk); end = store.End(chunk); }
                                int dc = Unsafe.ReadUnaligned<short>(q);
                                q += 2;
                                ulong mask = 0;
                                uint t;
                                while ((t = *q++) != 0)
                                {
                                    int k = (int)(t & 63);
                                    if (t < 0x80) blk[k] = (sbyte)*q++;
                                    else { blk[k] = Unsafe.ReadUnaligned<short>(q); q += 2; }
                                    mask |= 1UL << k;
                                }
                                int diff = dc - c.Pred;
                                c.Pred = dc;
                                if (i == 0) EmitBlockFast(blk, JpegTables.Identity, mask, diff, dc0, acx0, &sink);
                                else EmitBlockFast(blk, JpegTables.Identity, mask, diff, dc1, acx1, &sink);
                            }
                        }
                        _acc = sink.Acc; _accBits = sink.Bits; _pos = (int)((byte*)sink.Out - buf);
                    }
            }
            FlushBits();
        }

        JpegArithEncoder? _arith;

        /// <summary>
        /// One scan with arithmetic coding: a single pass (the coder adapts, there are no tables to prepare), for
        /// sequential (ss = 0, se = 63) and progressive scans alike.
        /// </summary>
        void EncodeScanArithmetic(Comp[] comps, int ss, int se, int ah, int al)
        {
            var coder = _arith ??= new JpegArithEncoder();
            for (int i = 0; i < _comps.Length; i++) _comps[i].ArithTable = i == 0 ? 0 : 1;
            var ids = new int[comps.Length];
            for (int i = 0; i < comps.Length; i++) ids[i] = comps[i].ArithTable;
            WriteScanHeader(comps, ids, ids, ss, se, ah, al);

            bool single = comps.Length == 1, sequential = !_opt.Progressive;
            int mcusX = single ? comps[0].ScanBlocksW : _mcusX, mcusY = single ? comps[0].ScanBlocksH : _mcusY;
            int todo = _restart, rst = 0;
            foreach (var c in comps) { c.Pred = 0; c.ArithContext = 0; }
            coder.Start();
            for (int my = 0; my < mcusY; my++)
            {
                for (int mx = 0; mx < mcusX; mx++)
                {
                    if (_restart != 0)
                    {
                        if (todo == 0)
                        {
                            coder.Finish();
                            Bytes(coder.Buffer.AsSpan(0, coder.Length));
                            Marker(0xD0 + rst);
                            rst = (rst + 1) & 7;
                            todo = _restart;
                            foreach (var c in comps) { c.Pred = 0; c.ArithContext = 0; }
                            coder.Start();
                        }
                        todo--;
                    }
                    foreach (var c in comps)
                    {
                        int h = single ? 1 : c.H, v = single ? 1 : c.V;
                        fixed (short* coefs = c.Coefs)
                        {
                            for (int by = 0; by < v; by++)
                            {
                                short* blk = coefs + ((nint)(my * v + by) * c.BlocksW + mx * h) * 64;
                                for (int bx = 0; bx < h; bx++, blk += 64)
                                {
                                    if (ss == 0)
                                    {
                                        if (ah != 0) coder.EncodeDcRefine(blk[0] >> al);
                                        else
                                        {
                                            int value = blk[0] >> al;
                                            coder.EncodeDc(c.ArithTable, ref c.ArithContext, value - c.Pred);
                                            c.Pred = value;
                                        }
                                        if (sequential) coder.EncodeAc(c.ArithTable, blk, 1, 63, 0);
                                    }
                                    else if (ah == 0) coder.EncodeAc(c.ArithTable, blk, ss, se, al);
                                    else coder.EncodeAcRefine(c.ArithTable, blk, ss, se, ah, al);
                                }
                            }
                        }
                    }
                }
            }
            coder.Finish();
            Bytes(coder.Buffer.AsSpan(0, coder.Length));
        }

        void WriteBaseline()
        {
            if (_opt.Arithmetic)
            {
                EncodeScanArithmetic(_comps, 0, 63, 0, 0);
                return;
            }
            bool color = _comps.Length == 3;
            HuffEnc dc0 = _comps[0].Dc, ac0 = _comps[0].Ac, dc1 = _comps[^1].Dc, ac1 = _comps[^1].Ac;
            if (OptimizedBaseline)
            {
                // the AC statistics were collected while transforming; the DC differences depend on the scan order
                _gather = true;
                EncodeScan(_comps, 0, 63, 0, 0);
                _gather = false;
                dc0.BuildFromFrequencies(); ac0.BuildFromFrequencies();
                if (color) { dc1.BuildFromFrequencies(); ac1.BuildFromFrequencies(); }
            }
            WriteHuffmanTable(dc0, 0, 0); WriteHuffmanTable(ac0, 1, 0);
            if (color) { WriteHuffmanTable(dc1, 0, 1); WriteHuffmanTable(ac1, 1, 1); }
            WriteScanHeader(_comps, color ? [0, 1, 1] : [0], color ? [0, 1, 1] : [0], 0, 63, 0, 0);
            EncodeScan(_comps, 0, 63, 0, 0);
        }

        void WriteProgressive()
        {
            // DC first with one bit held back, then the AC bands coarse to fine, each refined one bit at a time
            ProgressiveScan(_comps, 0, 0, 0, 1);
            var y = new[] { _comps[0] };
            if (_comps.Length == 3)
            {
                var cb = new[] { _comps[1] }; var cr = new[] { _comps[2] };
                ProgressiveScan(y, 1, 5, 0, 2);
                ProgressiveScan(cb, 1, 63, 0, 1);
                ProgressiveScan(cr, 1, 63, 0, 1);
                ProgressiveScan(y, 6, 63, 0, 2);
                ProgressiveScan(y, 1, 63, 2, 1);
                ProgressiveScan(_comps, 0, 0, 1, 0);
                ProgressiveScan(cb, 1, 63, 1, 0);
                ProgressiveScan(cr, 1, 63, 1, 0);
                ProgressiveScan(y, 1, 63, 1, 0);
            }
            else
            {
                ProgressiveScan(y, 1, 5, 0, 2);
                ProgressiveScan(y, 6, 63, 0, 2);
                ProgressiveScan(y, 1, 63, 2, 1);
                ProgressiveScan(y, 0, 0, 1, 0);
                ProgressiveScan(y, 1, 63, 1, 0);
            }
        }

        void ProgressiveScan(Comp[] comps, int ss, int se, int ah, int al)
        {
            if (_opt.Arithmetic)
            {
                EncodeScanArithmetic(comps, ss, se, ah, al);
                return;
            }
            var ids = new int[comps.Length];
            var slots = new HuffEnc?[2];
            bool dcRefine = ss == 0 && ah != 0; // raw bits only, no table
            if (!dcRefine)
            {
                for (int i = 0; i < comps.Length; i++)
                {
                    // one table for luma and one shared by the chroma components, rebuilt for every scan
                    ids[i] = comps[i] == _comps[0] ? 0 : 1;
                    var t = slots[ids[i]] ??= new HuffEnc { Slot = (uint)ids[i] };
                    if (ss == 0) comps[i].Dc = t; else comps[i].Ac = t;
                }
            }
            EncodeScan(comps, ss, se, ah, al); // records tokens and symbol statistics
            for (int id = 0; id < 2; id++)
            {
                if (slots[id] == null) continue;
                slots[id]!.BuildFromFrequencies();
                WriteHuffmanTable(slots[id]!, ss == 0 ? 0 : 1, id);
            }
            WriteScanHeader(comps, ss == 0 ? ids : new int[comps.Length], ss == 0 ? new int[comps.Length] : ids, ss, se, ah, al);
            Replay(slots[0], slots[1]);
            FlushBits();
        }

        // end-of-band run state of progressive AC scans
        int _eobRun;
        HuffEnc _eobTable = null!;
        readonly byte[] _corr = new byte[1024]; // buffered correction bits (refinement scans)
        int _corrCount;

        /// <summary>
        /// Runs over all MCUs of a scan. Sequential scans either count symbols (<see cref="_gather"/>) or write them,
        /// progressive scans record tokens for <see cref="Replay"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)] // runs once per image: no point in starting unoptimized
        void EncodeScan(Comp[] comps, int ss, int se, int ah, int al)
        {
            if (!_opt.Progressive && !_gather && _precision == 8)
            {
                EncodeSequentialScan(comps);
                return;
            }
            bool single = comps.Length == 1, progressive = _opt.Progressive;
            int mcusX = single ? comps[0].ScanBlocksW : _mcusX, mcusY = single ? comps[0].ScanBlocksH : _mcusY;
            int todo = _restart, rst = 0;
            foreach (var c in comps) c.Pred = 0;
            _eobRun = 0; _corrCount = 0;
            if (ss != 0) _eobTable = comps[0].Ac;

            for (int my = 0; my < mcusY; my++)
            {
                for (int mx = 0; mx < mcusX; mx++)
                {
                    if (_restart != 0)
                    {
                        if (todo == 0)
                        {
                            if (ss != 0) FlushEobRun();
                            if (progressive) Push(TokenRestart | (uint)rst);
                            else if (!_gather) { FlushBits(); Marker(0xD0 + rst); }
                            rst = (rst + 1) & 7;
                            todo = _restart;
                            foreach (var c in comps) c.Pred = 0;
                        }
                        todo--;
                    }
                    foreach (var c in comps)
                    {
                        int h = single ? 1 : c.H, v = single ? 1 : c.V;
                        for (int by = 0; by < v; by++)
                        {
                            fixed (short* coefs = c.Coefs)
                            {
                                short* blk = coefs + ((nint)(my * v + by) * c.BlocksW + mx * h) * 64;
                                for (int bx = 0; bx < h; bx++, blk += 64)
                                {
                                    if (!progressive) { Reserve(600); EncodeBlock(blk, c); }
                                    else if (ss == 0)
                                    {
                                        if (ah == 0) EncodeDc(blk[0] >> al, c);
                                        else RawBit(blk[0] >> al);
                                    }
                                    else if (ah == 0) EncodeAcFirst(blk, c.Ac, ss, se, al);
                                    else EncodeAcRefine(blk, c.Ac, ss, se, al);
                                }
                            }
                        }
                    }
                }
            }
            if (ss != 0) FlushEobRun();
            if (!progressive && !_gather) FlushBits();
        }

        /// <summary>Writes a sequential 8 bit scan from the stored coefficients with <see cref="EmitBlockFast"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)] // runs once per image: no point in starting unoptimized
        void EncodeSequentialScan(Comp[] comps)
        {
            bool single = comps.Length == 1;
            int mcusX = single ? comps[0].ScanBlocksW : _mcusX, mcusY = single ? comps[0].ScanBlocksH : _mcusY;
            int todo = _restart, rst = 0;
            foreach (var c in comps) c.Pred = 0;
            BitSink sink = default;
            fixed (byte* buf = _buf)
            {
                for (int my = 0; my < mcusY; my++)
                {
                    for (int mx = 0; mx < mcusX; mx++)
                    {
                        if (_restart != 0)
                        {
                            if (todo == 0)
                            {
                                FlushBits();
                                Marker(0xD0 + rst);
                                rst = (rst + 1) & 7;
                                todo = _restart;
                                foreach (var c in comps) c.Pred = 0;
                            }
                            todo--;
                        }
                        Reserve(10 * 600);
                        sink.Acc = _acc; sink.Bits = _accBits; sink.Out = (nint)(buf + _pos);
                        foreach (var c in comps)
                        {
                            int h = single ? 1 : c.H, v = single ? 1 : c.V;
                            fixed (short* coefs = c.Coefs)
                            fixed (uint* dc = c.Dc.Packed)
                            fixed (ulong* acx = c.Ac.Extended)
                            {
                                for (int by = 0; by < v; by++)
                                {
                                    short* blk = coefs + ((nint)(my * v + by) * c.BlocksW + mx * h) * 64;
                                    for (int bx = 0; bx < h; bx++, blk += 64)
                                    {
                                        int diff = blk[0] - c.Pred;
                                        c.Pred = blk[0];
                                        EmitBlockFast(blk, JpegTables.Identity, JpegTables.NonZeroMask(blk), diff, dc, acx, &sink);
                                    }
                                }
                            }
                        }
                        _acc = sink.Acc; _accBits = sink.Bits; _pos = (int)((byte*)sink.Out - buf);
                    }
                }
            }
            FlushBits();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int BitLength(int abs) => 32 - BitOperations.LeadingZeroCount((uint)abs);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void EncodeDc(int value, Comp c)
        {
            int diff = value - c.Pred;
            c.Pred = value;
            int s = BitLength(Math.Abs(diff));
            // negative values are sent as the low bits of (diff - 1)
            Symbol(c.Dc, s, diff < 0 ? diff - 1 : diff, s);
        }

        void EncodeBlock(short* blk, Comp c)
        {
            if (_gather) GatherDc(blk, c);
            else fixed (byte* buf = _buf) fixed (uint* dc = c.Dc.Packed, ac = c.Ac.Packed) fixed (ulong* acx = c.Ac.Extended) EmitBlock(blk, JpegTables.Identity, JpegTables.NonZeroMask(blk), c, buf, dc, ac, acx);
        }

        /// <summary>Statistics for optimized tables, DC part: counts the difference categories in scan order.</summary>
        static void GatherDc(short* blk, Comp c)
        {
            int diff = blk[0] - c.Pred;
            c.Pred = blk[0];
            c.Dc.Freq[BitLength(Math.Abs(diff))]++;
        }

        /// <summary>Statistics for optimized tables, AC part: counts the run/size symbols <see cref="EmitBlock"/> would write for the block.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void GatherAc(short* blk, int* freq, int maxSize)
        {
            ulong mask = JpegTables.NonZeroMask(blk) & ~1UL;
            int prev = 0;
            while (mask != 0)
            {
                int k = BitOperations.TrailingZeroCount(mask);
                mask &= mask - 1;
                int run = k - prev - 1;
                prev = k;
                if (run > 15) { freq[0xF0] += run >> 4; run &= 15; }
                freq[run << 4 | Math.Min(maxSize, BitLength(Math.Abs((int)blk[k])))]++;
            }
            if (prev != 63) freq[0]++;
        }

        /// <summary>Sequential block coder. The bit accumulator and output pointer live in locals for the duration of the block.</summary>
        /// <param name="blk">The coefficients, in any storage order.</param>
        /// <param name="order">Zigzag position -> index into blk.</param>
        /// <param name="mask">Nonzero coefficients by zigzag position.</param>
        /// <param name="c">The component being coded.</param>
        /// <param name="buf">Output buffer.</param>
        /// <param name="dc">DC Huffman code table.</param>
        /// <param name="ac">AC Huffman code table.</param>
        /// <param name="acx">AC Huffman code per symbol, shifted to leave room for its value bits, with code plus value length in the low byte.</param>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void EmitBlock(short* blk, byte* order, ulong mask, Comp c, byte* buf, uint* dc, uint* ac, ulong* acx)
        {
            ulong acc = _acc;
            int n = _accBits;
            byte* o = buf + _pos;

            int v = blk[0] - c.Pred;
            c.Pred = blk[0];
            int s = BitLength(Math.Abs(v));
            uint e = dc[s];
            int len = (int)(e & 255) + s;
            // negative values are sent as the low bits of (value - 1)
            acc = acc << len | (e >> 8) << s | ((uint)(v + (v >> 31)) & ((1u << s) - 1));
            n += len;
            if (n >= 32) { n -= 32; o = Write32(o, (uint)(acc >> n)); }

            mask &= ~1UL;
            int prev = 0; // index of the previous nonzero coefficient
            int maxSize = _maxAcSize;
            uint zrl = ac[0xF0];
            while (mask != 0)
            {
                int k = BitOperations.TrailingZeroCount(mask);
                mask &= mask - 1;
                int run = k - prev - 1;
                prev = k;
                while (run > 15)
                {
                    len = (int)(zrl & 255);
                    acc = acc << len | (zrl >> 8);
                    n += len;
                    if (n >= 32) { n -= 32; o = Write32(o, (uint)(acc >> n)); }
                    run -= 16;
                }
                v = blk[order[k]];
                int sign = v >> 31;
                s = BitLength((v ^ sign) - sign);
                if (s > maxSize) { s = maxSize; v = ((1 << s) - 1 ^ sign) - sign; } // cannot happen for real DCT output, but keep the stream legal
                ulong x = acx[run << 4 | s]; // code already shifted up by s, total length in the low byte
                len = (int)x & 63;
                acc = acc << len | x >> 8 | ((uint)(v + sign) & ~(uint.MaxValue << s));
                n += len;
                if (n >= 32) { n -= 32; o = Write32(o, (uint)(acc >> n)); }
            }
            if (prev != 63)
            {
                e = ac[0];
                len = (int)(e & 255);
                acc = acc << len | (e >> 8);
                n += len;
                if (n >= 32) { n -= 32; o = Write32(o, (uint)(acc >> n)); }
            }
            _acc = acc; _accBits = n; _pos = (int)(o - buf);
        }

        // 8 bit AC coefficient -> magnitude category | value bits << 4 (index: the low 11 bits of the value)
        static readonly ushort[] s_valueBits = BuildValueBits();
        static readonly ushort* s_valueBitsPtr;

        static ushort[] BuildValueBits()
        {
            var t = GC.AllocateArray<ushort>(2048, pinned: true);
            for (int v = -1023; v <= 1023; v++)
            {
                int size = BitLength(Math.Abs(v));
                // negative values are sent as the low bits of (value - 1)
                t[v & 2047] = (ushort)(size | ((v < 0 ? v - 1 : v) & ((1 << size) - 1)) << 4);
            }
            return t;
        }

        static JpegEncoder()
        {
            fixed (ushort* p = s_valueBits) s_valueBitsPtr = p;
        }

        /// <summary>Bit writer state of the single pass coder, handed to <see cref="EmitBlockFast"/> by pointer.</summary>
        struct BitSink
        {
            public ulong Acc;
            public nint Out;
            public int Bits;
        }

        /// <summary>
        /// Block coder of sequential 8 bit scans (coefficients in any order, <paramref name="order"/> maps zigzag positions to them): symbol and value
        /// bits come from two table lookups. Fewer than 32 bits stay pending between symbols: after each symbol the
        /// next 32 bits are stored unconditionally and the output pointer moves on only when they are complete, so
        /// the only branch is the rare one for a 0xFF byte that needs stuffing. Keeps the accumulator under a
        /// different invariant than <see cref="EmitBlock"/>, so the two must not be mixed within a scan.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void EmitBlockFast(short* blk, byte* order, ulong mask, int v, uint* dc, ulong* acx, BitSink* sink)
        {
            // (everything in plain locals: a reference to the accumulator would keep it in memory)
            ulong acc = sink->Acc;
            int n = sink->Bits;
            nint o = sink->Out;
            ushort* valueBits = s_valueBitsPtr;

            int sign = v >> 31;
            int s = BitLength((v ^ sign) - sign);
            uint e = dc[s];
            int len = (int)(e & 255) + s;
            acc = acc << len | (e >> 8) << s | ((uint)(v + sign) & ~(uint.MaxValue << s));
            (o, n) = Flush(o, acc, n + len);

            mask &= ~1UL;
            int prev = 0; // zigzag index of the previous nonzero coefficient
            while (mask != 0)
            {
                int k = BitOperations.TrailingZeroCount(mask);
                mask &= mask - 1;
                uint run = (uint)(k - prev - 1);
                prev = k;
                if (run > 15)
                {
                    ulong zrl = acx[0xF0]; // (size 0: the plain code)
                    do
                    {
                        len = (int)zrl & 63;
                        acc = acc << len | (zrl >> 8);
                        (o, n) = Flush(o, acc, n + len);
                        run -= 16;
                    } while (run > 15);
                }
                // the DCT of 8 bit samples cannot produce a quantized AC value beyond +-1023; the mask keeps a wild one harmless
                uint t = valueBits[(nuint)blk[order[(nuint)k]] & 2047];
                ulong x = acx[(nuint)(run << 4 | (t & 15))]; // code already shifted up by the size, total length in the low byte
                len = (int)x & 63;
                acc = acc << len | x >> 8 | (t >> 4);
                (o, n) = Flush(o, acc, n + len);
            }
            if (prev != 63)
            {
                ulong eob = acx[0];
                len = (int)eob & 63;
                acc = acc << len | (eob >> 8);
                (o, n) = Flush(o, acc, n + len);
            }
            sink->Acc = acc; sink->Bits = n; sink->Out = o;
        }

        /// <summary>
        /// After a symbol: n bits (at most 58) are pending in acc. Stores the oldest 32 of them and moves on when they
        /// are complete, so that fewer than 32 remain; returns the new output position and pending count.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static (nint, int) Flush(nint o, ulong acc, int n)
        {
            uint word = (uint)(acc << (64 - n) >> 32); // the oldest 32 pending bits (garbage below them if fewer)
            int full = n >> 5;                        // 1 when they are all there
            bool stuff = ((~word - 0x01010101u) & word & 0x80808080u) != 0; // a 0xFF byte among the 4
            if ((full != 0) & stuff) // (no short circuit: one well predicted branch)
                return ((nint)WriteStuffed((byte*)o, word), n - 32);
            Unsafe.WriteUnaligned((byte*)o, System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(word));
            return (o + (full << 2), n - (full << 5));
        }

        // inlined although rarely needed: a call in the block coder's loop would force its state out of registers
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte* WriteStuffed(byte* o, uint word)
        {
            for (int i = 24; i >= 0; i -= 8)
            {
                byte b = (byte)(word >> i);
                *o++ = b;
                if (b == 0xFF) *o++ = 0;
            }
            return o;
        }

        void FlushEobRun()
        {
            if (_eobRun > 0)
            {
                int n = BitLength(_eobRun) - 1;
                Symbol(_eobTable, n << 4, _eobRun, n);
                _eobRun = 0;
            }
            // correction bits of the blocks inside the run follow its symbol
            for (int i = 0; i < _corrCount; i++) RawBit(_corr[i]);
            _corrCount = 0;
        }

        void EncodeAcFirst(short* blk, HuffEnc ac, int ss, int se, int al)
        {
            ulong mask = JpegTables.NonZeroMask(blk) & JpegTables.BandMask(ss, se);
            int last = ss - 1; // position of the last coefficient sent
            while (mask != 0)
            {
                int k = BitOperations.TrailingZeroCount(mask);
                mask &= mask - 1;
                int v = blk[k];
                int t = Math.Abs(v) >> al; // the point transform truncates toward zero
                if (t == 0) continue;
                if (_eobRun > 0) FlushEobRun();
                int run = k - last - 1;
                last = k;
                while (run > 15) { Symbol(ac, 0xF0); run -= 16; }
                int s = BitLength(t);
                Symbol(ac, run << 4 | s, v < 0 ? ~t : t, s);
            }
            if (last != se && ++_eobRun == 0x7FFF) FlushEobRun();
        }

        void EncodeAcRefine(short* blk, HuffEnc ac, int ss, int se, int al)
        {
            ulong mask = JpegTables.NonZeroMask(blk) & JpegTables.BandMask(ss, se);
            // position of the last coefficient that becomes nonzero in this scan: zero runs after it fold into the EOB
            int eob = 0;
            for (ulong m = mask; m != 0;)
            {
                int k = 63 - BitOperations.LeadingZeroCount(m);
                if (Math.Abs((int)blk[k]) >> al == 1) { eob = k; break; }
                m &= ~(1UL << k);
            }

            int start = _corrCount, pending = 0; // this block's correction bits sit at _corr[start .. start + pending)
            int last = ss - 1; // position of the last newly nonzero coefficient
            int skipped = 0;   // positions since then that are not part of the zero run: old nonzero coefficients and zeros covered by a ZRL
            while (mask != 0)
            {
                int k = BitOperations.TrailingZeroCount(mask);
                mask &= mask - 1;
                int t = Math.Abs((int)blk[k]) >> al;
                if (t == 0) continue; // still zero at this precision
                int run = k - last - 1 - skipped;
                while (run > 15 && k <= eob)
                {
                    FlushEobRun();
                    Symbol(ac, 0xF0);
                    run -= 16; skipped += 16;
                    for (int i = 0; i < pending; i++) RawBit(_corr[start + i]);
                    start = 0; pending = 0;
                }
                if (t > 1)
                {
                    // was nonzero before this scan: only its next magnitude bit is sent, after the following symbol
                    _corr[start + pending++] = (byte)(t & 1);
                    skipped++;
                    continue;
                }
                FlushEobRun();
                Symbol(ac, run << 4 | 1, blk[k] < 0 ? 0 : 1, 1);
                for (int i = 0; i < pending; i++) RawBit(_corr[start + i]);
                start = 0; pending = 0;
                last = k; skipped = 0;
            }
            if (se - last - skipped > 0 || pending > 0)
            {
                _eobRun++;
                _corrCount = start + pending;
                // flush before the run counter or the bit buffer could overflow with the next block
                if (_eobRun == 0x7FFF || _corrCount > _corr.Length - 64) FlushEobRun();
            }
        }

        // ---------------------------------------------------------------- lossless (T.81 Annex H)

        void RunLossless(RawImage image, int precision)
        {
            int w = image.Width, h = image.Height, ch = image.Channels;
            int nc = image.Color switch { ColorModel.Gray or ColorModel.GrayAlpha => 1, ColorModel.Rgb or ColorModel.Rgba => 3, _ => ch };
            int predictor = _opt.Predictor == 0 ? 1 : _opt.Predictor;
            if (predictor < 1 || predictor > 7) throw new ArgumentException("lossless JPEG predictors are numbered 1 to 7");
            // restart intervals have to cover whole lines in lossless mode
            int restart = _opt.RestartInterval <= 0 ? 0 : (int)(((long)_opt.RestartInterval + w - 1) / w * w);
            if (restart > 65535) restart = 0;

            byte[]? s8 = image.SampleType == SampleType.UInt8 ? image.GetData<byte>() : null;
            ushort[]? s16 = s8 == null ? image.GetData<ushort>() : null;
            if (precision < (s8 != null ? 8 : 16))
            {
                int limit = 1 << precision;
                for (int i = 0, n = w * h; i < n; i++)
                    for (int c = 0; c < nc; c++)
                        if ((s8 != null ? s8[i * ch + c] : s16![i * ch + c]) >= limit) throw new ArgumentException($"sample values exceed the {precision} bit range");
            }

            var tables = new HuffEnc[nc];
            for (int c = 0; c < nc; c++) tables[c] = new HuffEnc();
            bool arithmetic = _opt.Arithmetic;
            if (!arithmetic) LosslessPass(s8, s16, w, h, ch, nc, precision, predictor, restart, tables, emit: false);

            bool rgb = image.Color is ColorModel.Rgb or ColorModel.Rgba;
            WriteMetadata(image.Metadata, jfif: nc == 1);
            if (rgb)
            {
                // Adobe segment with transform 0: the components are RGB, not YCbCr
                Marker(0xEE); U16(14); Bytes("Adobe"u8); U16(100); U16(0); U16(0); Byte(0);
            }
            Marker(arithmetic ? 0xCB : 0xC3); U16(8 + 3 * nc); Byte(precision); U16(h); U16(w); Byte(nc);
            for (int c = 0; c < nc; c++) { Byte(rgb ? "RGB"[c] : c + 1); Byte(0x11); Byte(0); }
            for (int c = 0; c < nc && !arithmetic; c++)
            {
                tables[c].BuildFromFrequencies();
                WriteHuffmanTable(tables[c], 0, c);
            }
            if (restart != 0) { Marker(0xDD); U16(4); U16(restart); }
            Marker(0xDA); U16(6 + 2 * nc); Byte(nc);
            for (int c = 0; c < nc; c++) { Byte(rgb ? "RGB"[c] : c + 1); Byte(c << 4); }
            Byte(predictor); Byte(0); Byte(0);
            if (arithmetic) LosslessPassArithmetic(s8, s16, w, h, ch, nc, precision, predictor, restart);
            else
            {
                LosslessPass(s8, s16, w, h, ch, nc, precision, predictor, restart, tables, emit: true);
                FlushBits();
            }
            Marker(0xD9);
            _out.Write(_buf, 0, _pos);
            _pos = 0;
        }

        /// <summary>Lossless scan with arithmetic coding (SOF11): every difference is coded in the context of its left and upper neighbours.</summary>
        void LosslessPassArithmetic(byte[]? s8, ushort[]? s16, int w, int h, int ch, int nc, int precision, int predictor, int restart)
        {
            var cur = new int[w * nc];
            var prev = new int[w * nc];
            var diff = new int[w * nc];
            var above = new int[w * nc]; // the differences of the line above
            int rowsPerInterval = restart == 0 ? int.MaxValue : restart / w;
            int topRow = 0, rst = 0;
            var coder = _arith ??= new JpegArithEncoder();
            coder.Start();
            for (int y = 0; y < h; y++)
            {
                if (y != 0 && y % rowsPerInterval == 0)
                {
                    coder.Finish();
                    Bytes(coder.Buffer.AsSpan(0, coder.Length));
                    Marker(0xD0 + rst);
                    rst = (rst + 1) & 7;
                    topRow = y;
                    coder.Start();
                }
                (cur, prev) = (prev, cur);
                (diff, above) = (above, diff);
                for (int x = 0, i = y * w * ch; x < w; x++, i += ch)
                    for (int c = 0; c < nc; c++) cur[x * nc + c] = s8 != null ? s8[i + c] : s16![i + c];
                PredictLine(cur, prev, diff, w * nc, nc, y == topRow ? 0 : predictor, 1 << (precision - 1));
                for (int k = 0; k < w * nc; k++)
                {
                    // differences live modulo 65536, like in the decoder that conditions on them
                    int d = (short)diff[k];
                    diff[k] = d;
                    int da = k < nc ? 0 : diff[k - nc], db = y == topRow ? 0 : above[k];
                    coder.EncodeLosslessDifference(k % nc, da, db, d);
                }
            }
            coder.Finish();
            Bytes(coder.Buffer.AsSpan(0, coder.Length));
        }

        /// <summary>
        /// Prediction differences of one line (components interleaved, n values). Predictor 0 stands for the first
        /// line of the image or of a restart interval, which predicts from the left only.
        /// </summary>
        static void PredictLine(int[] cur, int[] prev, int[] diff, int n, int nc, int predictor, int initial)
        {
            for (int c = 0; c < nc; c++) diff[c] = cur[c] - (predictor == 0 ? initial : prev[c]);
            int k = nc;
            if (Vector.IsHardwareAccelerated)
            {
                fixed (int* pc = cur, pp = prev, pd = diff)
                {
                    for (int vn = Vector<int>.Count; k + vn <= n; k += vn)
                    {
                        var ra = Vector.Load(pc + k - nc);
                        var rb = Vector.Load(pp + k);
                        var rc = Vector.Load(pp + k - nc);
                        var pred = predictor switch
                        {
                            0 or 1 => ra,
                            2 => rb,
                            3 => rc,
                            4 => ra + rb - rc,
                            5 => ra + Vector.ShiftRightArithmetic(rb - rc, 1),
                            6 => rb + Vector.ShiftRightArithmetic(ra - rc, 1),
                            _ => Vector.ShiftRightArithmetic(ra + rb, 1),
                        };
                        Vector.Store(Vector.Load(pc + k) - pred, pd + k);
                    }
                }
            }
            for (; k < n; k++)
            {
                int ra = cur[k - nc], rb = prev[k], rc = prev[k - nc];
                int pred = predictor switch
                {
                    0 or 1 => ra,
                    2 => rb,
                    3 => rc,
                    4 => ra + rb - rc,
                    5 => ra + ((rb - rc) >> 1),
                    6 => rb + ((ra - rc) >> 1),
                    _ => (ra + rb) >> 1,
                };
                diff[k] = cur[k] - pred;
            }
        }

        /// <summary>Predicts every sample and either counts the difference categories or writes the coded differences.</summary>
        void LosslessPass(byte[]? s8, ushort[]? s16, int w, int h, int ch, int nc, int precision, int predictor, int restart, HuffEnc[] tables, bool emit)
        {
            // current and previous line as plain ints, components interleaved
            var cur = new int[w * nc];
            var prev = new int[w * nc];
            var diff = new int[w * nc];
            int rowsPerInterval = restart == 0 ? int.MaxValue : restart / w;
            int topRow = 0, rst = 0;
            ulong acc = _acc;
            int n = _accBits;
            fixed (byte* buf = _buf)
            {
                for (int y = 0; y < h; y++)
                {
                    if (y != 0 && y % rowsPerInterval == 0)
                    {
                        if (emit)
                        {
                            _acc = acc; _accBits = n;
                            FlushBits();
                            Marker(0xD0 + rst);
                            acc = 0; n = 0;
                        }
                        rst = (rst + 1) & 7;
                        topRow = y;
                    }
                    (cur, prev) = (prev, cur);
                    for (int x = 0, i = y * w * ch; x < w; x++, i += ch)
                        for (int c = 0; c < nc; c++) cur[x * nc + c] = s8 != null ? s8[i + c] : s16![i + c];

                    PredictLine(cur, prev, diff, w * nc, nc, y == topRow ? 0 : predictor, 1 << (precision - 1));

                    for (int x0 = 0; x0 < w; x0 += 256)
                    {
                        // every sample appends at most 8 bytes (31 bits plus stuffing)
                        if (emit) Reserve(256 * 4 * 8 + 16);
                        byte* o = buf + _pos;
                        int x1 = Math.Min(w, x0 + 256);
                        for (int x = x0; x < x1; x++)
                        {
                            for (int c = 0; c < nc; c++)
                            {
                                // differences are taken modulo 65536; -32768 and +32768 coincide and get their own category
                                int d = (short)diff[x * nc + c];
                                int s = d == short.MinValue ? 16 : BitLength(Math.Abs(d));
                                if (!emit) { tables[c].Freq[s]++; continue; }
                                uint e = tables[c].Packed[s];
                                int vb = s == 16 ? 0 : s;
                                int len = (int)(e & 255) + vb;
                                acc = acc << len | (e >> 8) << vb | ((uint)(d + (d >> 31)) & ((1u << vb) - 1));
                                n += len;
                                if (n >= 32) { n -= 32; o = Write32(o, (uint)(acc >> n)); }
                            }
                        }
                        _pos = (int)(o - buf);
                    }
                }
            }
            _acc = acc; _accBits = n;
        }
    }
}
