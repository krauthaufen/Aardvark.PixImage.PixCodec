using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Aardvark.ImageCodecs.Compression;

namespace Aardvark.ImageCodecs
{
    /// <summary>Reusable per-thread buffers for chunk coding.</summary>
    internal sealed class ExrScratch
    {
        readonly byte[][] _bytes = new byte[6][];
        ushort[]? _words;
        public int[] Nx = Array.Empty<int>(), Ny = Array.Empty<int>();
        public long[] Cursor = Array.Empty<long>();   // per channel positions while (de)interleaving planes
        public ExrPiz.Tables? Piz;
        public float[]? Row;
        // The buffers come from the shared pool and all go back in Return, also the ones outgrown on the way, which
        // may still be referenced until the call is done.
        List<Action>? _rented;

        T[] RentArray<T>(int size)
        {
            var a = ArrayPool<T>.Shared.Rent(size);
            (_rented ??= new List<Action>()).Add(() => ArrayPool<T>.Shared.Return(a));
            return a;
        }

        public static ExrScratch Rent() => new ExrScratch();

        /// <summary>Hands the buffers back to the pool; the scratch must not be used afterwards.</summary>
        public static void Return(ExrScratch s)
        {
            if (s._rented != null) foreach (var r in s._rented) r();
            s._rented = null;
            Array.Clear(s._bytes); Array.Clear(s._arrays);
            s._words = null; s.Row = null;
        }

        public byte[] Bytes(int slot, int size)
        {
            var b = _bytes[slot];
            if (b == null || b.Length < size) _bytes[slot] = b = RentArray<byte>(Math.Max(size, 256));
            return b;
        }

        readonly object?[] _arrays = new object?[8];

        /// <summary>A zeroed reusable array of at least <paramref name="size"/> elements for the given slot (the rest is not cleared).</summary>
        public T[] Reuse<T>(int slot, int size)
        {
            if (_arrays[slot] is not T[] a || a.Length < size) _arrays[slot] = a = RentArray<T>(Math.Max(size, 64));
            a.AsSpan(0, size).Clear();
            return a;
        }

        public ushort[] Words(int size)
        {
            if (_words == null || _words.Length < size) _words = RentArray<ushort>(Math.Max(size, 256));
            return _words;
        }

        public float[] RowBuffer(int size)
        {
            if (Row == null || Row.Length < size) Row = RentArray<float>(size);
            return Row;
        }
    }

    /// <summary>
    /// Geometry of one chunk (scan line block or tile): per channel sample counts. The uncompressed chunk layout is,
    /// for each line, for each channel in file order, that channel's samples of the line (little endian).
    /// </summary>
    internal struct ExrChunk
    {
        public ExrChan[] Chans;
        public int[] Nx, Ny;          // samples per line / number of lines, per channel
        public int X0, X1, Y0, Y1;    // inclusive pixel bounds
        public long Size;             // uncompressed bytes

        public static ExrChunk Create(ExrChan[] chans, int x0, int x1, int y0, int y1, ExrScratch s)
        {
            if (s.Nx.Length < chans.Length) { s.Nx = new int[chans.Length]; s.Ny = new int[chans.Length]; s.Cursor = new long[chans.Length]; }
            var c = new ExrChunk { Chans = chans, Nx = s.Nx, Ny = s.Ny, X0 = x0, X1 = x1, Y0 = y0, Y1 = y1 };
            long size = 0;
            for (int i = 0; i < chans.Length; i++)
            {
                c.Nx[i] = NumSamples(chans[i].XS, x0, x1);
                c.Ny[i] = NumSamples(chans[i].YS, y0, y1);
                size += (long)c.Nx[i] * c.Ny[i] * chans[i].Bytes;
            }
            c.Size = size;
            return c;
        }

        public static int FloorDiv(int a, int s) => a >= 0 ? a / s : -((s - 1 - a) / s);
        public static bool IsSampled(int a, int s) => s == 1 || a - FloorDiv(a, s) * s == 0;

        /// <summary>Number of multiples of s in [a, b].</summary>
        public static int NumSamples(int s, int a, int b)
            => s == 1 ? b - a + 1 : FloorDiv(b, s) - FloorDiv(a, s) + (IsSampled(a, s) ? 1 : 0);
    }

    internal static unsafe class ExrCodecs
    {
        public static int LinesPerChunk(ExrCompression c) => c switch
        {
            ExrCompression.None or ExrCompression.Rle or ExrCompression.Zips or ExrCompression.Zstd => 1,
            ExrCompression.Zip or ExrCompression.Pxr24 => 16,
            ExrCompression.Piz or ExrCompression.B44 or ExrCompression.B44A or ExrCompression.Dwaa => 32,
            ExrCompression.Dwab or ExrCompression.Htj2k256 or ExrCompression.Lj2k => 256,
            ExrCompression.Htj2k32 => 32,
            _ => throw new ImageNotSupportedException($"EXR compression {(int)c} is not supported"),
        };

        /// <summary>Decompresses a chunk into the uncompressed line layout. dst.Length is the exact uncompressed size.</summary>
        public static void Decode(ExrCompression comp, ReadOnlySpan<byte> src, Span<byte> dst, in ExrChunk chunk, ExrScratch s)
        {
            switch (comp)
            {
                case ExrCompression.Rle:
                {
                    var tmp = s.Bytes(0, dst.Length).AsSpan(0, dst.Length);
                    RleDecode(src, tmp);
                    Reconstruct(tmp, dst);
                    break;
                }
                case ExrCompression.Zips:
                case ExrCompression.Zip:
                {
                    var tmp = s.Bytes(0, dst.Length).AsSpan(0, dst.Length);
                    Inflate(src, tmp);
                    Reconstruct(tmp, dst);
                    break;
                }
                case ExrCompression.Pxr24: Pxr24Decode(src, dst, chunk, s); break;
                case ExrCompression.Piz: ExrPiz.Decode(src, dst, chunk, s); break;
                case ExrCompression.B44:
                case ExrCompression.B44A: ExrB44.Decode(src, dst, chunk, s); break;
                case ExrCompression.Dwaa:
                case ExrCompression.Dwab: ExrDwa.Decode(src, dst, chunk, s); break;
                case ExrCompression.Zstd: ExrZstd.Decode(src, dst, chunk, s); break;
                case ExrCompression.Htj2k256:
                case ExrCompression.Htj2k32: ExrHtj2k.Decode(src, dst, chunk, s); break;
                case ExrCompression.Lj2k:
                    throw new ImageNotSupportedException("EXR LJ2K (lossy HTJ2K) compression is not supported");
                default: throw new ImageNotSupportedException($"EXR compression {comp} is not supported");
            }
        }

        /// <summary>Decompresses a plain block of bytes (deep sample count tables and deep sample data).</summary>
        public static void DecodeBytes(ExrCompression comp, ReadOnlySpan<byte> src, Span<byte> dst, ExrScratch s)
        {
            if (comp != ExrCompression.Rle && comp != ExrCompression.Zips)
                throw new ImageNotSupportedException($"EXR {comp} compression of deep data is not supported");
            Decode(comp, src, dst, default, s);
        }

        /// <summary>Compresses a chunk given in the uncompressed line layout. Returns the bytes to store (raw if compression did not help).</summary>
        public static byte[] Encode(ExrCompression comp, ReadOnlySpan<byte> raw, in ExrChunk chunk, ExrScratch s, int zipLevel, float dwaLevel)
        {
            ReadOnlySpan<byte> packed;
            switch (comp)
            {
                case ExrCompression.None: return raw.ToArray();
                case ExrCompression.Rle:
                {
                    var tmp = s.Bytes(0, raw.Length).AsSpan(0, raw.Length);
                    Predict(raw, tmp);
                    var o = s.Bytes(1, raw.Length + raw.Length / 100 + 16);
                    packed = o.AsSpan(0, RleEncode(tmp, o));
                    break;
                }
                case ExrCompression.Zips:
                case ExrCompression.Zip:
                {
                    var tmp = s.Bytes(0, raw.Length).AsSpan(0, raw.Length);
                    Predict(raw, tmp);
                    packed = Zlib.Compress(tmp, zipLevel);
                    break;
                }
                case ExrCompression.Pxr24: packed = Pxr24Encode(raw, chunk, s, zipLevel); break;
                case ExrCompression.Piz: packed = ExrPiz.Encode(raw, chunk, s); break;
                case ExrCompression.B44:
                case ExrCompression.B44A: packed = ExrB44.Encode(raw, chunk, s, comp == ExrCompression.B44A); break;
                case ExrCompression.Dwaa:
                case ExrCompression.Dwab: packed = ExrDwa.Encode(raw, chunk, s, dwaLevel); break;
                case ExrCompression.Zstd: packed = ExrZstd.Encode(raw, chunk, s); break;
                case ExrCompression.Htj2k256:
                case ExrCompression.Htj2k32: packed = ExrHtj2k.Encode(raw, chunk, s); break;
                default: throw new ImageNotSupportedException($"EXR compression {comp} is not supported for encoding");
            }
            // The format stores a chunk raw when compression does not shrink it; readers detect this by the size.
            return packed.Length < raw.Length ? packed.ToArray() : raw.ToArray();
        }

        public static void Inflate(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            int n;
            try { n = Zlib.Decompress(src, dst); }
            catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException)
            {
                throw new ImageFormatException("EXR chunk has corrupt zlib data", e);
            }
            if (n != dst.Length) throw new ImageFormatException("EXR chunk decompresses to the wrong size");
        }

        // ---- byte delta predictor + even/odd byte split shared by RLE and ZIP ----

        /// <summary>Undoes the delta predictor in place, then interleaves the two halves of t into dst.</summary>
        internal static void Reconstruct(Span<byte> t, Span<byte> dst)
        {
            int n = t.Length;
            if (n == 0) return;
            fixed (byte* pt = t)
            fixed (byte* pd = dst)
            {
                int i = 0;
                if (Sse2.IsSupported && n >= 32)
                {
                    // prefix sum of (t[i] - 128), started with +128 so that t[0] stays as it is
                    var bias = Vector128.Create((byte)128);
                    var carry = bias;
                    for (; i + 16 <= n; i += 16)
                    {
                        var v = Sse2.Subtract(Sse2.LoadVector128(pt + i), bias);
                        v = Sse2.Add(v, Sse2.ShiftLeftLogical128BitLane(v, 1));
                        v = Sse2.Add(v, Sse2.ShiftLeftLogical128BitLane(v, 2));
                        v = Sse2.Add(v, Sse2.ShiftLeftLogical128BitLane(v, 4));
                        v = Sse2.Add(v, Sse2.ShiftLeftLogical128BitLane(v, 8));
                        v = Sse2.Add(v, carry);
                        Sse2.Store(pt + i, v);
                        carry = Vector128.Create(pt[i + 15]);
                    }
                }
                if (i == 0) i = 1;
                for (; i < n; i++) pt[i] = (byte)(pt[i - 1] + pt[i] - 128);

                int half = (n + 1) / 2, pairs = n / 2;
                byte* a = pt, b = pt + half;
                int k = 0;
                if (Sse2.IsSupported)
                    for (; k + 16 <= pairs; k += 16)
                    {
                        var va = Sse2.LoadVector128(a + k);
                        var vb = Sse2.LoadVector128(b + k);
                        Sse2.Store(pd + 2 * k, Sse2.UnpackLow(va, vb));
                        Sse2.Store(pd + 2 * k + 16, Sse2.UnpackHigh(va, vb));
                    }
                for (; k < pairs; k++) { pd[2 * k] = a[k]; pd[2 * k + 1] = b[k]; }
                if ((n & 1) != 0) pd[n - 1] = a[half - 1];
            }
        }

        /// <summary>Splits raw into even bytes followed by odd bytes and applies the delta predictor.</summary>
        internal static void Predict(ReadOnlySpan<byte> raw, Span<byte> t)
        {
            int n = raw.Length;
            if (n == 0) return;
            fixed (byte* pr = raw)
            fixed (byte* pt = t)
            {
                int half = (n + 1) / 2, pairs = n / 2;
                byte* a = pt, b = pt + half;
                for (int k = 0; k < pairs; k++) { a[k] = pr[2 * k]; b[k] = pr[2 * k + 1]; }
                if ((n & 1) != 0) a[half - 1] = pr[n - 1];
                int prev = pt[0];
                for (int i = 1; i < n; i++)
                {
                    int cur = pt[i];
                    pt[i] = (byte)(cur - prev + 128);
                    prev = cur;
                }
            }
        }

        // ---- RLE: signed count byte; negative = -count literal bytes, otherwise count + 1 copies of the next byte ----

        internal static void RleDecode(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            int p = 0, o = 0;
            while (p < src.Length)
            {
                int c = (sbyte)src[p++];
                if (c < 0)
                {
                    int n = -c;
                    if (n > src.Length - p || n > dst.Length - o) throw new ImageFormatException("EXR RLE data is corrupt");
                    src.Slice(p, n).CopyTo(dst.Slice(o));
                    p += n; o += n;
                }
                else
                {
                    int n = c + 1;
                    if (p >= src.Length || n > dst.Length - o) throw new ImageFormatException("EXR RLE data is corrupt");
                    dst.Slice(o, n).Fill(src[p++]);
                    o += n;
                }
            }
            if (o != dst.Length) throw new ImageFormatException("EXR RLE data is truncated");
        }

        internal static int RleEncode(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            const int MaxRun = 127, MinRun = 3;
            int n = src.Length, i = 0, o = 0, litStart = 0;
            void FlushLiterals(ReadOnlySpan<byte> s, Span<byte> d, int end)
            {
                while (litStart < end)
                {
                    int c = Math.Min(MaxRun, end - litStart);
                    d[o++] = (byte)(sbyte)(-c);
                    s.Slice(litStart, c).CopyTo(d.Slice(o));
                    o += c; litStart += c;
                }
            }
            while (i < n)
            {
                byte v = src[i];
                int run = 1;
                while (i + run < n && run < MaxRun && src[i + run] == v) run++;
                if (run >= MinRun)
                {
                    FlushLiterals(src, dst, i);
                    dst[o++] = (byte)(run - 1);
                    dst[o++] = v;
                    i += run;
                    litStart = i;
                }
                else i += run;
            }
            FlushLiterals(src, dst, n);
            return o;
        }

        // ---- PXR24: per line and channel, byte planes (most significant first) of horizontal differences, then zlib.
        //      FLOAT samples are rounded to 24 bits, HALF and UINT are lossless. ----

        static int Pxr24Bytes(int type) => type == 0 ? 4 : type == 1 ? 2 : 3;

        static long Pxr24Size(in ExrChunk c)
        {
            long size = 0;
            for (int i = 0; i < c.Chans.Length; i++) size += (long)c.Nx[i] * c.Ny[i] * Pxr24Bytes(c.Chans[i].Type);
            return size;
        }

        static void Pxr24Decode(ReadOnlySpan<byte> src, Span<byte> dst, in ExrChunk c, ExrScratch s)
        {
            int size = (int)Pxr24Size(c);
            var tmp = s.Bytes(0, size).AsSpan(0, size);
            Inflate(src, tmp);
            fixed (byte* pt = tmp)
            fixed (byte* pd = dst)
            {
                byte* t = pt, d = pd;
                for (int y = c.Y0; y <= c.Y1; y++)
                    for (int ci = 0; ci < c.Chans.Length; ci++)
                    {
                        if (!ExrChunk.IsSampled(y, c.Chans[ci].YS)) continue;
                        int n = c.Nx[ci];
                        uint pixel = 0;
                        switch (c.Chans[ci].Type)
                        {
                            case 0:
                                for (int j = 0; j < n; j++)
                                {
                                    pixel += ((uint)t[j] << 24) | ((uint)t[n + j] << 16) | ((uint)t[2 * n + j] << 8) | t[3 * n + j];
                                    *(uint*)(d + 4 * j) = pixel;
                                }
                                t += 4 * n; d += 4 * n;
                                break;
                            case 1:
                                for (int j = 0; j < n; j++)
                                {
                                    pixel += ((uint)t[j] << 8) | t[n + j];
                                    *(ushort*)(d + 2 * j) = (ushort)pixel;
                                }
                                t += 2 * n; d += 2 * n;
                                break;
                            default:
                                for (int j = 0; j < n; j++)
                                {
                                    pixel += ((uint)t[j] << 24) | ((uint)t[n + j] << 16) | ((uint)t[2 * n + j] << 8);
                                    *(uint*)(d + 4 * j) = pixel;
                                }
                                t += 3 * n; d += 4 * n;
                                break;
                        }
                    }
            }
        }

        /// <summary>Rounds a float bit pattern to its 24 most significant bits (nearest; never rounds a finite value to infinity).</summary>
        internal static uint Float24(uint bits)
        {
            uint sign = bits & 0x80000000u, e = bits & 0x7f800000u, m = bits & 0x007fffffu, r;
            if (e == 0x7f800000u)
            {
                if (m != 0) { m >>= 8; r = (e >> 8) | m | (m == 0 ? 1u : 0u); }   // keep NaNs NaN
                else r = e >> 8;
            }
            else
            {
                r = ((e | m) + (m & 0x80)) >> 8;
                if (r >= 0x7f8000) r = (e | m) >> 8;
            }
            return (sign >> 8) | r;
        }

        static byte[] Pxr24Encode(ReadOnlySpan<byte> raw, in ExrChunk c, ExrScratch s, int zipLevel)
        {
            int size = (int)Pxr24Size(c);
            var tmp = s.Bytes(0, size).AsSpan(0, size);
            fixed (byte* pt = tmp)
            fixed (byte* pr = raw)
            {
                byte* t = pt, r = pr;
                for (int y = c.Y0; y <= c.Y1; y++)
                    for (int ci = 0; ci < c.Chans.Length; ci++)
                    {
                        if (!ExrChunk.IsSampled(y, c.Chans[ci].YS)) continue;
                        int n = c.Nx[ci];
                        uint prev = 0;
                        switch (c.Chans[ci].Type)
                        {
                            case 0:
                                for (int j = 0; j < n; j++)
                                {
                                    uint v = *(uint*)(r + 4 * j), diff = v - prev;
                                    prev = v;
                                    t[j] = (byte)(diff >> 24); t[n + j] = (byte)(diff >> 16); t[2 * n + j] = (byte)(diff >> 8); t[3 * n + j] = (byte)diff;
                                }
                                t += 4 * n; r += 4 * n;
                                break;
                            case 1:
                                for (int j = 0; j < n; j++)
                                {
                                    uint v = *(ushort*)(r + 2 * j), diff = v - prev;
                                    prev = v;
                                    t[j] = (byte)(diff >> 8); t[n + j] = (byte)diff;
                                }
                                t += 2 * n; r += 2 * n;
                                break;
                            default:
                                for (int j = 0; j < n; j++)
                                {
                                    uint v = Float24(*(uint*)(r + 4 * j)), diff = v - prev;
                                    prev = v;
                                    t[j] = (byte)(diff >> 16); t[n + j] = (byte)(diff >> 8); t[2 * n + j] = (byte)diff;
                                }
                                t += 3 * n; r += 4 * n;
                                break;
                        }
                    }
            }
            return Zlib.Compress(tmp, zipLevel);
        }
    }
}
