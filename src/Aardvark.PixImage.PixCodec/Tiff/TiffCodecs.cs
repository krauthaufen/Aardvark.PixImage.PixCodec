using System;
using System.Runtime.Intrinsics;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aardvark.ImageCodecs
{
    /// <summary>TIFF flavoured LZW (compression 5): MSB-first codes with "early change", plus the pre-6.0 LSB-first variant on decode.</summary>
    internal static unsafe class TiffLzw
    {
        const int Clear = 256, Eoi = 257, First = 258, MaxCodes = 4096;

        /// <summary>
        /// Decodes into <paramref name="dst"/> and returns the number of bytes produced (at most <paramref name="dstLen"/>,
        /// which may exceed 2 GB). The string table stores, per code, where in the output that string was last written,
        /// so emitting a code is a plain forward copy inside the output buffer - no prefix chain walking and no
        /// per-code allocations.
        /// </summary>
        public static long Decode(ReadOnlySpan<byte> src, byte* dst, long dstLen)
        {
            // Old-style LZW (libtiff's "compat" mode) packs codes LSB first, so its 9-bit Clear code reads 00 01.
            if (src.Length >= 2 && src[0] == 0 && (src[1] & 1) != 0) return DecodeOldStyle(src, dst, dstLen);

            // per code: output offset of its string << 16 | length (a string is never longer than the table)
            ulong* tab = stackalloc ulong[MaxCodes];
            int n = src.Length, sp = 0;
            long op = 0;
            ulong buf = 0;
            int bits = 0, width = 9, next = First;
            long prevPos = 0;
            int prevLen = 0;
            fixed (byte* s = src)
            {
                while (op < dstLen)
                {
                    if (bits < width)
                    {
                        if (sp + 8 <= n)
                        {
                            ulong v = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(s + sp));
                            buf |= v >> bits;
                            int adv = (63 - bits) >> 3;
                            sp += adv; bits += adv << 3;
                        }
                        else
                        {
                            while (bits <= 56 && sp < n) { buf |= (ulong)s[sp++] << (56 - bits); bits += 8; }
                            if (bits < width) break; // data ends without EOI: tolerated, the caller checks the length
                        }
                    }
                    int code = (int)(buf >> (64 - width));
                    buf <<= width; bits -= width;

                    int len;
                    if (code < 256)
                    {
                        dst[op] = (byte)code;
                        len = 1;
                    }
                    else if (code > Eoi)
                    {
                        long so;
                        if (code < next) { ulong e = tab[code]; so = (long)(e >> 16); len = (int)(e & 0xFFFF); }
                        else if (code == next && prevLen > 0) { so = prevPos; len = prevLen + 1; } // KwKwK: overlaps its own output
                        else throw new ImageFormatException("TIFF: corrupt LZW data");
                        byte* d = dst + op, f = dst + so;
                        if (op + len + 8 <= dstLen && (op - so >= 8 || op - so >= len))
                        {
                            // 8 bytes at a time, possibly writing a few bytes too many (they are overwritten by what follows)
                            byte* end = d + len;
                            do
                            {
                                Unsafe.WriteUnaligned(d, Unsafe.ReadUnaligned<ulong>(f));
                                d += 8; f += 8;
                            } while (d < end);
                        }
                        else
                        {
                            if (len > dstLen - op) len = (int)(dstLen - op);
                            for (int i = 0; i < len; i++) d[i] = f[i];
                        }
                    }
                    else if (code == Clear)
                    {
                        width = 9; next = First; prevLen = 0;
                        continue;
                    }
                    else break; // EOI

                    if (prevLen > 0 && next < MaxCodes)
                    {
                        tab[next] = (ulong)prevPos << 16 | (uint)(prevLen + 1);
                        next++;
                        // early change: the width grows one code before the table would actually need it
                        if (next == (1 << width) - 1 && width < 12) width++;
                    }
                    prevPos = op; prevLen = len;
                    op += len;
                }
            }
            return op;
        }

        static long DecodeOldStyle(ReadOnlySpan<byte> src, byte* dst, long dstLen)
        {
            ulong* tab = stackalloc ulong[MaxCodes];
            int n = src.Length, sp = 0;
            long op = 0;
            uint buf = 0;
            int bits = 0, width = 9, next = First;
            long prevPos = 0;
            int prevLen = 0;
            while (op < dstLen)
            {
                while (bits < width && sp < n) { buf |= (uint)src[sp++] << bits; bits += 8; }
                if (bits < width) break;
                int code = (int)(buf & ((1u << width) - 1));
                buf >>= width; bits -= width;

                int len;
                if (code < 256) { dst[op] = (byte)code; len = 1; }
                else if (code == Clear) { width = 9; next = First; prevLen = 0; continue; }
                else if (code == Eoi) break;
                else
                {
                    long so;
                    if (code < next) { ulong e = tab[code]; so = (long)(e >> 16); len = (int)(e & 0xFFFF); }
                    else if (code == next && prevLen > 0) { so = prevPos; len = prevLen + 1; }
                    else throw new ImageFormatException("TIFF: corrupt LZW data");
                    if (len > dstLen - op) len = (int)(dstLen - op);
                    byte* d = dst + op, f = dst + so;
                    for (int i = 0; i < len; i++) d[i] = f[i];
                }
                if (prevLen > 0 && next < MaxCodes)
                {
                    tab[next] = (ulong)prevPos << 16 | (uint)(prevLen + 1);
                    next++;
                    if (next == 1 << width && width < 12) width++;
                }
                prevPos = op; prevLen = len;
                op += len;
            }
            return op;
        }

        const int HashBits = 13, HashSize = 1 << HashBits;

        public static int MaxEncodedSize(int n) => n + n / 2 + n / 1024 + 64;

        /// <summary>Encodes <paramref name="src"/> into <paramref name="dst"/> (at least <see cref="MaxEncodedSize"/> bytes), returns the length.</summary>
        public static int Encode(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            int* keys = stackalloc int[HashSize];
            ushort* vals = stackalloc ushort[HashSize];
            new Span<int>(keys, HashSize).Fill(-1);
            ulong acc = 0; int nbits = 0, op = 0;
            int width = 9, next = First;

            void Put(int code, ref ulong a, ref int nb, ref int o, Span<byte> d, int w)
            {
                a = (a << w) | (uint)code; nb += w;
                while (nb >= 8) { nb -= 8; d[o++] = (byte)(a >> nb); }
            }

            Put(Clear, ref acc, ref nbits, ref op, dst, width);
            if (src.Length > 0)
            {
                int prefix = src[0];
                for (int i = 1; i < src.Length; i++)
                {
                    int c = src[i];
                    int key = (prefix << 8) | c;
                    int h = (int)(((uint)key * 0x9E3779B1u) >> (32 - HashBits));
                    while (true)
                    {
                        int k = keys[h];
                        if (k == key) { prefix = vals[h]; break; }
                        if (k < 0)
                        {
                            Put(prefix, ref acc, ref nbits, ref op, dst, width);
                            keys[h] = key; vals[h] = (ushort)next;
                            next++;
                            if (next == 1 << width && width < 12) width++;
                            if (next >= MaxCodes - 2)
                            {
                                Put(Clear, ref acc, ref nbits, ref op, dst, width);
                                new Span<int>(keys, HashSize).Fill(-1);
                                width = 9; next = First;
                            }
                            prefix = c;
                            break;
                        }
                        h = (h + 1) & (HashSize - 1);
                    }
                }
                Put(prefix, ref acc, ref nbits, ref op, dst, width);
                // the decoder adds a table entry for this last code as well, which may widen the EOI code
                next++;
                if (next == 1 << width && width < 12) width++;
            }
            Put(Eoi, ref acc, ref nbits, ref op, dst, width);
            if (nbits > 0) dst[op++] = (byte)(acc << (8 - nbits));
            return op;
        }
    }

    /// <summary>
    /// The LZW encoder as a stream filter, for strips too large to hold in one buffer: write the data in pieces,
    /// then <see cref="Finish"/>. Produces the same codes as <see cref="TiffLzw.Encode"/>.
    /// </summary>
    internal sealed class TiffLzwWriter
    {
        const int Clear = 256, Eoi = 257, First = 258, MaxCodes = 4096, HashBits = 13, HashSize = 1 << HashBits;
        readonly System.IO.Stream _out;
        readonly int[] _keys = new int[HashSize];
        readonly ushort[] _vals = new ushort[HashSize];
        readonly byte[] _buf = new byte[1 << 16];
        int _n, _nbits, _width = 9, _next = First, _prefix = -1;
        ulong _acc;

        public TiffLzwWriter(System.IO.Stream output)
        {
            _out = output;
            Array.Fill(_keys, -1);
            Put(Clear);
        }

        void Put(int code)
        {
            _acc = (_acc << _width) | (uint)code; _nbits += _width;
            while (_nbits >= 8)
            {
                _nbits -= 8;
                if (_n == _buf.Length) { _out.Write(_buf, 0, _n); _n = 0; }
                _buf[_n++] = (byte)(_acc >> _nbits);
            }
        }

        public void Write(ReadOnlySpan<byte> src)
        {
            int i = 0;
            if (_prefix < 0 && src.Length > 0) _prefix = src[i++];
            int prefix = _prefix;
            for (; i < src.Length; i++)
            {
                int c = src[i];
                int key = (prefix << 8) | c;
                int h = (int)(((uint)key * 0x9E3779B1u) >> (32 - HashBits));
                while (true)
                {
                    int k = _keys[h];
                    if (k == key) { prefix = _vals[h]; break; }
                    if (k < 0)
                    {
                        Put(prefix);
                        _keys[h] = key; _vals[h] = (ushort)_next;
                        _next++;
                        if (_next == 1 << _width && _width < 12) _width++;
                        if (_next >= MaxCodes - 2)
                        {
                            Put(Clear);
                            Array.Fill(_keys, -1);
                            _width = 9; _next = First;
                        }
                        prefix = c;
                        break;
                    }
                    h = (h + 1) & (HashSize - 1);
                }
            }
            _prefix = prefix;
        }

        public void Finish()
        {
            if (_prefix >= 0)
            {
                Put(_prefix);
                _next++;
                if (_next == 1 << _width && _width < 12) _width++;
            }
            Put(Eoi);
            if (_nbits > 0) { if (_n == _buf.Length) { _out.Write(_buf, 0, _n); _n = 0; } _buf[_n++] = (byte)(_acc << (8 - _nbits)); }
            _out.Write(_buf, 0, _n);
            _n = 0;
        }
    }

    /// <summary>Apple PackBits (compression 32773).</summary>
    internal static unsafe class TiffPackBits
    {
        public static long Decode(ReadOnlySpan<byte> src, byte* dst, long dstLen)
        {
            int sp = 0, n = src.Length;
            long op = 0;
            while (sp < n && op < dstLen)
            {
                int c = (sbyte)src[sp++];
                if (c >= 0)
                {
                    int len = c + 1;
                    if (len > n - sp) len = n - sp;
                    if (len > dstLen - op) len = (int)(dstLen - op);
                    src.Slice(sp, len).CopyTo(new Span<byte>(dst + op, len));
                    sp += c + 1; op += len;
                }
                else if (c != -128)
                {
                    if (sp >= n) break;
                    int len = 1 - c;
                    if (len > dstLen - op) len = (int)(dstLen - op);
                    new Span<byte>(dst + op, len).Fill(src[sp++]);
                    op += len;
                }
            }
            return op;
        }

        public static int MaxEncodedSize(int n, int rowBytes) => n + (n / 128 + 1) + (rowBytes > 0 ? n / rowBytes + 1 : 1) + 16;

        /// <summary>Encodes row by row (runs never cross a row boundary, as the spec requires).</summary>
        public static int Encode(ReadOnlySpan<byte> src, int rowBytes, Span<byte> dst)
        {
            int op = 0;
            for (int r = 0; r < src.Length; r += rowBytes)
                op = EncodeRow(src.Slice(r, Math.Min(rowBytes, src.Length - r)), dst, op);
            return op;
        }

        static int EncodeRow(ReadOnlySpan<byte> s, Span<byte> d, int op)
        {
            int i = 0, n = s.Length;
            while (i < n)
            {
                int run = 1;
                byte b = s[i];
                while (i + run < n && run < 128 && s[i + run] == b) run++;
                if (run >= 2)
                {
                    d[op++] = (byte)(1 - run); d[op++] = b;
                    i += run;
                    continue;
                }
                // literal: extend until a run of 3 starts (a run of 2 inside a literal is not worth breaking for)
                int start = i;
                i++;
                while (i < n && i - start < 128)
                {
                    if (i + 2 < n && s[i] == s[i + 1] && s[i] == s[i + 2]) break;
                    i++;
                }
                int len = i - start;
                d[op++] = (byte)(len - 1);
                s.Slice(start, len).CopyTo(d.Slice(op));
                op += len;
            }
            return op;
        }
    }

    /// <summary>TIFF predictors (tag 317) and small byte level helpers.</summary>
    internal static unsafe class TiffPredict
    {
        // byte shuffle with run time indices, 0 for indices of 16 and up
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static Vector128<byte> Shuf(Vector128<byte> v, Vector128<byte> idx)
        {
            if (System.Runtime.Intrinsics.X86.Ssse3.IsSupported)
                return System.Runtime.Intrinsics.X86.Ssse3.Shuffle(v, idx | Vector128.GreaterThan(idx, Vector128.Create((byte)15)));
            if (System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported) return System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.VectorTableLookup(v, idx);
            return Vector128.Shuffle(v, idx);
        }

        /// <summary>
        /// Byte samples, 3 or 4 per pixel: a running sum per channel, a vector at a time (15 bytes for 3 channels, 16 for 4).
        /// Within the vector the sums are built in log steps of shifts by whole pixels, then the previous pixel is added.
        /// Byte arithmetic wraps exactly as the scalar loop's.
        /// </summary>
        static void UndoHorizontalBytes(byte* row, int count, int stride)
        {
            int step = stride == 3 ? 15 : 16;
            Vector128<byte> Shift(Vector128<byte> v, int k)
            {
                // byte i takes byte i - k, zero below (out of range shuffle indices give 0)
                var idx = Vector128.Create((byte)0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15) - Vector128.Create((byte)k);
                return Shuf(v, idx);
            }
            // previous pixel repeated over the lanes; the last pixel of a vector repeated the same way
            var spread = stride == 3
                ? Vector128.Create((byte)0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0)
                : Vector128.Create((byte)0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3);
            var lastSpread = spread + Vector128.Create((byte)(step - stride));
            var keep = stride == 3 ? Vector128.Create(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, (byte)0xFF) : Vector128<byte>.Zero;
            // the first pixel stays; the carry starts as it
            var carry = Shuf(Vector128.LoadUnsafe(ref *row), spread);
            int i = stride;
            // the next vector is loaded before this one is stored: they overlap by a byte for 3 channels, and a load right
            // after a partly overlapping store would wait for it
            var x = Vector128.LoadUnsafe(ref row[i]);
            for (; i + 16 + step <= count; i += step)
            {
                var y = x + Shift(x, stride);
                y += Shift(y, 2 * stride);
                if (stride == 3) y += Shift(y, 4 * stride);
                y += carry;
                var next = Vector128.LoadUnsafe(ref row[i + step]);
                Vector128.ConditionalSelect(keep, x, y).StoreUnsafe(ref row[i]);
                carry = Shuf(y, lastSpread);
                x = next;
            }
            for (; i < count; i++) row[i] = (byte)(row[i] + row[i - stride]);
        }

        /// <summary>Undoes horizontal differencing on one row of <paramref name="count"/> samples (native byte order).</summary>
        public static void UndoHorizontal(byte* row, int count, int stride, int bps)
        {
            if (count <= stride) return;
            switch (bps)
            {
                case 1:
                    if ((stride == 3 || stride == 4) && Vector128.IsHardwareAccelerated && count >= 32)
                        UndoHorizontalBytes(row, count, stride);
                    else if (stride == 3)
                    {
                        byte r = row[0], g = row[1], b = row[2];
                        byte* p = row + 3, end = row + count - 2;
                        for (; p < end; p += 3)
                        {
                            p[0] = r = (byte)(r + p[0]); p[1] = g = (byte)(g + p[1]); p[2] = b = (byte)(b + p[2]);
                        }
                    }
                    else if (stride == 4)
                    {
                        byte r = row[0], g = row[1], b = row[2], a = row[3];
                        byte* p = row + 4, end = row + count - 3;
                        for (; p < end; p += 4)
                        {
                            p[0] = r = (byte)(r + p[0]); p[1] = g = (byte)(g + p[1]); p[2] = b = (byte)(b + p[2]); p[3] = a = (byte)(a + p[3]);
                        }
                    }
                    else if (stride == 1)
                    {
                        byte v = row[0];
                        for (int i = 1; i < count; i++) row[i] = v = (byte)(v + row[i]);
                    }
                    else
                        for (int i = stride; i < count; i++) row[i] = (byte)(row[i] + row[i - stride]);
                    break;
                case 2:
                {
                    ushort* p = (ushort*)row;
                    if (stride == 1)
                    {
                        ushort v = p[0];
                        for (int i = 1; i < count; i++) p[i] = v = (ushort)(v + p[i]);
                    }
                    else
                        for (int i = stride; i < count; i++) p[i] = (ushort)(p[i] + p[i - stride]);
                    break;
                }
                case 4:
                {
                    uint* p = (uint*)row;
                    for (int i = stride; i < count; i++) p[i] += p[i - stride];
                    break;
                }
                case 8:
                {
                    ulong* p = (ulong*)row;
                    for (int i = stride; i < count; i++) p[i] += p[i - stride];
                    break;
                }
            }
        }

        /// <summary>Horizontal differencing on 3-byte samples (24-bit floats), added as 24-bit integers in the file byte order.</summary>
        public static void UndoHorizontal24(byte* row, int count, int stride, bool bigEndian)
        {
            for (int i = stride; i < count; i++)
            {
                byte* a = row + (long)(i - stride) * 3, c = row + (long)i * 3;
                int x = bigEndian ? a[0] << 16 | a[1] << 8 | a[2] : a[2] << 16 | a[1] << 8 | a[0];
                int y = bigEndian ? c[0] << 16 | c[1] << 8 | c[2] : c[2] << 16 | c[1] << 8 | c[0];
                int v = x + y;
                if (bigEndian) { c[0] = (byte)(v >> 16); c[1] = (byte)(v >> 8); c[2] = (byte)v; }
                else { c[2] = (byte)(v >> 16); c[1] = (byte)(v >> 8); c[0] = (byte)v; }
            }
        }

        public static void ApplyHorizontal(byte* row, int count, int stride, int bps)
        {
            switch (bps)
            {
                case 1:
                    for (int i = count - 1; i >= stride; i--) row[i] = (byte)(row[i] - row[i - stride]);
                    break;
                case 2:
                {
                    ushort* p = (ushort*)row;
                    for (int i = count - 1; i >= stride; i--) p[i] = (ushort)(p[i] - p[i - stride]);
                    break;
                }
                case 4:
                {
                    uint* p = (uint*)row;
                    for (int i = count - 1; i >= stride; i--) p[i] -= p[i - stride];
                    break;
                }
                case 8:
                {
                    ulong* p = (ulong*)row;
                    for (int i = count - 1; i >= stride; i--) p[i] -= p[i - stride];
                    break;
                }
            }
        }

        /// <summary>
        /// Undoes the floating point predictor on one row: byte-wise differencing over the whole row, with the bytes of
        /// all samples stored as planes (most significant byte plane first, independent of the file byte order).
        /// The result is little endian (machine order). <paramref name="tmp"/> needs count * bps bytes.
        /// </summary>
        public static void UndoFloat(byte* row, int count, int stride, int bps, byte* tmp)
        {
            long n = (long)count * bps;
            if (stride == 1)
            {
                byte v = row[0];
                for (long i = 1; i < n; i++) row[i] = v = (byte)(v + row[i]);
            }
            else
                for (long i = stride; i < n; i++) row[i] = (byte)(row[i] + row[i - stride]);
            Buffer.MemoryCopy(row, tmp, n, n);
            if (bps == 4)
            {
                byte* p0 = tmp, p1 = tmp + count, p2 = p1 + count, p3 = p2 + count;
                uint* o = (uint*)row;
                for (int i = 0; i < count; i++) o[i] = (uint)(p0[i] << 24 | p1[i] << 16 | p2[i] << 8 | p3[i]);
                return;
            }
            for (int b = 0; b < bps; b++)
            {
                byte* plane = tmp + (long)b * count;
                byte* o = row + (bps - 1 - b);
                for (int i = 0; i < count; i++) o[(long)i * bps] = plane[i];
            }
        }

        public static void ApplyFloat(byte* row, int count, int stride, int bps, byte* tmp)
        {
            long n = (long)count * bps;
            for (int b = 0; b < bps; b++)
            {
                byte* plane = tmp + (long)b * count;
                byte* o = row + (bps - 1 - b);
                for (int i = 0; i < count; i++) plane[i] = o[(long)i * bps];
            }
            for (long i = n - 1; i >= stride; i--) row[i] = (byte)(tmp[i] - tmp[i - stride]);
            for (long i = Math.Min(stride, n) - 1; i >= 0; i--) row[i] = tmp[i];
        }

        public static void SwapBytes(byte* p, long bytes, int bps)
        {
            while (bytes > 0)
            {
                int n = (int)Math.Min(bytes, 1 << 30);
                switch (bps)
                {
                    case 2: { var s = new Span<ushort>(p, n / 2); BinaryPrimitives.ReverseEndianness(s, s); break; }
                    case 4: { var s = new Span<uint>(p, n / 4); BinaryPrimitives.ReverseEndianness(s, s); break; }
                    case 8: { var s = new Span<ulong>(p, n / 8); BinaryPrimitives.ReverseEndianness(s, s); break; }
                }
                p += n; bytes -= n;
            }
        }

        static readonly byte[] Reversed = MakeReversed();

        static byte[] MakeReversed()
        {
            var t = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                int v = 0;
                for (int b = 0; b < 8; b++) if ((i & (1 << b)) != 0) v |= 0x80 >> b;
                t[i] = (byte)v;
            }
            return t;
        }

        /// <summary>Reverses the bit order inside each byte (FillOrder 2).</summary>
        public static void ReverseBits(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            var t = Reversed;
            for (int i = 0; i < src.Length; i++) dst[i] = t[src[i]];
        }
    }

    /// <summary>
    /// SGI LogLuv / LogL (compression 34676): each row stores the bytes of its pixels as planes, most significant
    /// first, and every plane is run length coded (count >= 128: repeat the next byte count - 126 times, otherwise
    /// count literal bytes follow).
    /// </summary>
    internal static unsafe class TiffSgiLog
    {
        /// <summary>Decodes rows of <paramref name="width"/> pixels of <paramref name="bytesPerPixel"/> (2: LogL16, 4: LogLuv32) bytes each, big endian per pixel.</summary>
        public static void Decode(ReadOnlySpan<byte> src, byte* dst, int width, int rows, int bytesPerPixel)
        {
            int sp = 0, n = src.Length;
            for (int y = 0; y < rows; y++)
            {
                byte* row = dst + (long)y * width * bytesPerPixel;
                for (int plane = 0; plane < bytesPerPixel; plane++)
                {
                    int i = 0;
                    while (i < width)
                    {
                        if (sp >= n) throw new ImageFormatException("TIFF: LogLuv data is truncated");
                        int c = src[sp++];
                        if (c >= 128)
                        {
                            int rc = c - 126;
                            if (sp >= n || rc > width - i) throw new ImageFormatException("TIFF: corrupt LogLuv data");
                            byte b = src[sp++];
                            for (int k = 0; k < rc; k++, i++) row[(long)i * bytesPerPixel + plane] = b;
                        }
                        else
                        {
                            if (c == 0 || c > n - sp || c > width - i) throw new ImageFormatException("TIFF: corrupt LogLuv data");
                            for (int k = 0; k < c; k++, i++) row[(long)i * bytesPerPixel + plane] = src[sp++];
                        }
                    }
                }
            }
        }

        /// <summary>Luminance of a 16-bit log encoded value: sign bit, then a 15-bit log2 scaled by 256 and offset by 64 stops.</summary>
        public static float LogL16ToY(int p)
        {
            int le = p & 0x7FFF;
            if (le == 0) return 0;
            double y = Math.Exp(Math.Log(2) / 256 * (le + 0.5) - Math.Log(2) * 64);
            return (float)((p & 0x8000) != 0 ? -y : y);
        }

        /// <summary>CIE XYZ of a 32-bit LogLuv pixel (16-bit log luminance, 8 bits each for u' and v' scaled by 410).</summary>
        public static void LogLuv32ToXyz(uint p, out float x, out float y, out float z)
        {
            double l = LogL16ToY((int)(p >> 16));
            if (l <= 0) { x = y = z = 0; return; }
            double u = (((p >> 8) & 0xFF) + 0.5) / 410, v = ((p & 0xFF) + 0.5) / 410;
            double s = 1 / (6 * u - 16 * v + 12);
            double cx = 9 * u * s, cy = 4 * v * s;
            x = (float)(cx / cy * l); y = (float)l; z = (float)((1 - cx - cy) / cy * l);
        }

        /// <summary>Luminance of the 10-bit log value of 24-bit LogLuv: a log2 scaled by 64 and offset by 12 stops.</summary>
        public static double LogL10ToY(int p) => p == 0 ? 0 : Math.Exp(Math.Log(2) / 64 * (p + 0.5) - Math.Log(2) * 12);

        /// <summary>
        /// CIE XYZ of a 24-bit LogLuv pixel: 10 bits of log luminance and a 14-bit index into the squares that tile
        /// the visible part of the (u', v') plane (see <see cref="TiffLogLuvTable"/>). Indices outside of the table
        /// decode to the neutral chromaticity.
        /// </summary>
        public static void LogLuv24ToXyz(uint p, out float x, out float y, out float z)
        {
            double l = LogL10ToY((int)(p >> 14 & 0x3FF));
            if (l <= 0) { x = y = z = 0; return; }
            int c = (int)(p & 0x3FFF);
            double u = 0.210526316, v = 0.473684211;
            if (c < TiffLogLuvTable.Squares)
            {
                var first = TiffLogLuvTable.First;
                int lo = 0, hi = TiffLogLuvTable.Rows - 1;
                while (lo < hi) // last row whose first square is not beyond c
                {
                    int mid = (lo + hi + 1) >> 1;
                    if (first[mid] <= c) lo = mid; else hi = mid - 1;
                }
                u = TiffLogLuvTable.UStart[lo] + (c - first[lo] + 0.5) * TiffLogLuvTable.UvSquare;
                v = TiffLogLuvTable.VStart + (lo + 0.5) * TiffLogLuvTable.UvSquare;
            }
            double s = 1 / (6 * u - 16 * v + 12);
            double cx = 9 * u * s, cy = 4 * v * s;
            x = (float)(cx / cy * l); y = (float)l; z = (float)((1 - cx - cy) / cy * l);
        }
    }

    /// <summary>
    /// PixarLog (compression 32909): samples are 11-bit codes on a curve that is linear near black and logarithmic
    /// above (ratio 1.004 per step, code 1250 = 1.0), horizontally differenced modulo 2048 and deflated as 16-bit
    /// words. Decoding maps the codes back to linear floats, or to 8 / 16 bit integers of them.
    /// </summary>
    internal static unsafe class TiffPixarLog
    {
        const int Codes = 2048, One = 1250;
        static readonly float[] ToFloat = BuildFloat();
        static readonly ushort[] To16 = Array.ConvertAll(ToFloat, f => { double v = f * 65535.0 + 0.5; return v > 65535 ? (ushort)65535 : (ushort)v; });
        static readonly byte[] To8 = Array.ConvertAll(ToFloat, f => { double v = f * 255.0 + 0.5; return v > 255 ? (byte)255 : (byte)v; });

        static float[] BuildFloat()
        {
            double c = Math.Log(1.004);
            int nlin = (int)(1 / c);           // number of linear steps: where the log curve's slope takes over
            c = 1.0 / nlin;
            double b = Math.Exp(-c * One);     // so that b * exp(c * One) = 1
            double linstep = b * c * Math.E;
            var t = new float[Codes];
            for (int i = 0; i < nlin; i++) t[i] = (float)(i * linstep);
            for (int i = nlin; i < Codes; i++) t[i] = (float)(b * Math.Exp(c * i));
            return t;
        }

        /// <summary>Undoes the differencing of one row of n codes and writes the linear values in the given sample type.</summary>
        public static void Row(ushort* src, byte* dst, int n, int stride, SampleType type)
        {
            for (int i = stride; i < n; i++) src[i] = (ushort)((src[i] + src[i - stride]) & (Codes - 1));
            for (int i = 0; i < Math.Min(stride, n); i++) src[i] &= Codes - 1;
            switch (type)
            {
                case SampleType.UInt8: { var t = To8; for (int i = 0; i < n; i++) dst[i] = t[src[i]]; break; }
                case SampleType.UInt16: { var t = To16; var d = (ushort*)dst; for (int i = 0; i < n; i++) d[i] = t[src[i]]; break; }
                default: { var t = ToFloat; var d = (float*)dst; for (int i = 0; i < n; i++) d[i] = t[src[i]]; break; }
            }
        }
    }

    /// <summary>
    /// ThunderScan (compression 32809): 4-bit gray pixels coded per byte as a run of the last pixel (00, 6-bit count),
    /// three 2-bit deltas (01), two 3-bit deltas (10) or a literal pixel (11). Delta code "skip" (2 resp. 4) emits nothing.
    /// Every row starts from pixel value 0 and is packed two pixels per byte.
    /// </summary>
    internal static unsafe class TiffThunder
    {
        public static long Decode(ReadOnlySpan<byte> src, byte* dst, int width, int rows)
        {
            int rowBytes = (width + 1) / 2, sp = 0;
            new Span<byte>(dst, rowBytes * rows).Clear();
            ReadOnlySpan<sbyte> two = stackalloc sbyte[] { 0, 1, 0, -1 }, three = stackalloc sbyte[] { 0, 1, 2, 3, 0, -3, -2, -1 };
            for (int y = 0; y < rows; y++)
            {
                byte* row = dst + (long)y * rowBytes;
                int n = 0, last = 0;
                void Put(byte* r, int v, ref int count, ref int lastPixel)
                {
                    lastPixel = v & 15;
                    if (count < width) r[count >> 1] |= (byte)((count & 1) == 0 ? lastPixel << 4 : lastPixel);
                    count++;
                }
                while (n < width)
                {
                    // Data that ends early leaves the remaining rows at 0: libtiff reads such files (its own sample
                    // text.tif stops three rows short) and so do we.
                    if (sp >= src.Length) return (long)rows * rowBytes;
                    int b = src[sp++], v = b & 0x3F;
                    switch (b >> 6)
                    {
                        case 0:
                            for (int i = 0; i < v && n < width; i++) Put(row, last, ref n, ref last);
                            break;
                        case 1:
                            for (int shift = 4; shift >= 0; shift -= 2)
                            {
                                int d = v >> shift & 3;
                                if (d != 2) Put(row, last + two[d], ref n, ref last);
                            }
                            break;
                        case 2:
                            for (int shift = 3; shift >= 0; shift -= 3)
                            {
                                int d = v >> shift & 7;
                                if (d != 4) Put(row, last + three[d], ref n, ref last);
                            }
                            break;
                        default:
                            Put(row, v, ref n, ref last);
                            break;
                    }
                }
            }
            return (long)rows * rowBytes;
        }
    }

    /// <summary>
    /// Decoders for strips / tiles whose COMPRESSED data exceeds 2 GB (only reachable through the stream entry
    /// points): the input is pulled from the source in pieces and fed to resumable versions of the LZW, PackBits and
    /// Deflate decoders. Slower than the in-memory decoders, which is why those remain the normal path.
    /// </summary>
    internal static unsafe class TiffLargeChunk
    {
        const int Piece = 32 << 20;

        public static long Decode(TiffSource src, long offset, long count, int compression, byte* dst, long dstLen)
        {
            var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(Piece);
            try
            {
                long op = 0, done = 0;
                // LZW state
                ulong[]? tab = compression == 5 ? new ulong[4096] : null;
                ulong bitBuf = 0; int bits = 0, width = 9, next = 258, prevLen = 0; long prevPos = 0; bool eoi = false;
                // PackBits state
                int literal = 0, run = 0;
                var inflater = compression is 8 or 32946 ? Aardvark.ImageCodecs.Compression.Zlib.CreateInflater() : null;

                while (done < count && op < dstLen && !eoi)
                {
                    int n = (int)Math.Min(Piece, count - done);
                    src.Read(offset + done, buf.AsSpan(0, n));
                    if (done == 0 && compression == 5 && n >= 2 && buf[0] == 0 && (buf[1] & 1) != 0)
                        throw new ImageNotSupportedException("TIFF: old-style LZW strips larger than 2 GB are not supported");
                    done += n;
                    int sp = 0;
                    if (inflater != null)
                    {
                        while (sp < n && op < dstLen)
                        {
                            var status = inflater.Inflate(buf.AsSpan(sp, n - sp), new Span<byte>(dst + op, (int)Math.Min(dstLen - op, 1 << 30)), out int c, out int w);
                            sp += c; op += w;
                            if (status == Aardvark.ImageCodecs.Compression.InflateStatus.Done) { eoi = true; break; }
                            if (c == 0 && w == 0) break;
                        }
                    }
                    else if (compression == 32773)
                    {
                        while (sp < n && op < dstLen)
                        {
                            if (literal > 0)
                            {
                                int len = (int)Math.Min(Math.Min(literal, n - sp), dstLen - op);
                                buf.AsSpan(sp, len).CopyTo(new Span<byte>(dst + op, len));
                                sp += len; op += len; literal -= len;
                            }
                            else if (run > 0)
                            {
                                int len = (int)Math.Min(run, dstLen - op);
                                new Span<byte>(dst + op, len).Fill(buf[sp++]);
                                op += len; run = 0;
                            }
                            else
                            {
                                int c = (sbyte)buf[sp++];
                                if (c >= 0) literal = c + 1;
                                else if (c != -128) run = 1 - c;
                            }
                        }
                    }
                    else
                    {
                        // the LZW decoder of TiffLzw.Decode with the bit reservoir carried across pieces
                        while (op < dstLen)
                        {
                            while (bits <= 56 && sp < n) { bitBuf |= (ulong)buf[sp++] << (56 - bits); bits += 8; }
                            if (bits < width) break; // needs the next piece
                            int code = (int)(bitBuf >> (64 - width));
                            bitBuf <<= width; bits -= width;
                            int len;
                            if (code < 256) { dst[op] = (byte)code; len = 1; }
                            else if (code == 256) { width = 9; next = 258; prevLen = 0; continue; }
                            else if (code == 257) { eoi = true; break; }
                            else
                            {
                                long so;
                                if (code < next) { ulong e = tab![code]; so = (long)(e >> 16); len = (int)(e & 0xFFFF); }
                                else if (code == next && prevLen > 0) { so = prevPos; len = prevLen + 1; }
                                else throw new ImageFormatException("TIFF: corrupt LZW data");
                                if (len > dstLen - op) len = (int)(dstLen - op);
                                byte* d = dst + op, f = dst + so;
                                for (int i = 0; i < len; i++) d[i] = f[i];
                            }
                            if (prevLen > 0 && next < 4096)
                            {
                                tab![next] = (ulong)prevPos << 16 | (uint)(prevLen + 1);
                                next++;
                                if (next == (1 << width) - 1 && width < 12) width++;
                            }
                            prevPos = op; prevLen = len;
                            op += len;
                        }
                    }
                }
                return op;
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(buf); }
        }
    }
}
