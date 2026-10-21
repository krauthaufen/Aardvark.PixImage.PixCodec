using System;
using System.IO;
using System.IO.Compression;

namespace Aardvark.ImageCodecs.Compression
{
    /// <summary>Which implementation <see cref="Zlib"/> uses for deflate and inflate.</summary>
    public enum ZlibBackend
    {
        /// <summary>The fully managed implementation in this library (no native code).</summary>
        Managed,
        /// <summary>System.IO.Compression, which calls the native zlib that ships with (or is used by) the runtime.</summary>
        Bcl,
    }

    /// <summary>
    /// zlib (RFC 1950) and raw deflate (RFC 1951) for all codecs. Every codec must go through this class
    /// so the backend can be swapped (BCL or fully managed) in one place.
    /// </summary>
    public static class Zlib
    {
        /// <summary>
        /// The implementation used by the compression and decompression methods of this class. Both are always
        /// available and produce interchangeable streams. The initial value can be chosen with the environment
        /// variable PIXCODEC_ZLIB (= "managed" or "bcl"). Checksums are always computed by managed code.
        /// </summary>
        public static ZlibBackend Backend { get; set; } = InitialBackend();

        /// <summary>The backend used when PIXCODEC_ZLIB is not set.</summary>
        public const ZlibBackend DefaultBackend = ZlibBackend.Managed;

        static ZlibBackend InitialBackend()
        {
            string? v = null;
            try { v = Environment.GetEnvironmentVariable("PIXCODEC_ZLIB"); } catch (Exception) { }
            if (string.Equals(v, "bcl", StringComparison.OrdinalIgnoreCase)) return ZlibBackend.Bcl;
            if (string.Equals(v, "managed", StringComparison.OrdinalIgnoreCase)) return ZlibBackend.Managed;
            return DefaultBackend;
        }

        /// <summary>
        /// Decompresses a zlib stream into a buffer of known size. Returns the number of bytes written. Decoding
        /// stops when the destination is full or the input ends; a short count therefore means truncated input.
        /// Corrupt data throws <see cref="ImageFormatException"/> (managed backend) or
        /// <see cref="InvalidDataException"/> (BCL backend).
        /// </summary>
        public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
            => Decompress(source, destination, true);

        /// <summary>
        /// As <see cref="Decompress(ReadOnlySpan{byte}, Span{byte})"/>; <paramref name="verifyChecksum"/> = false
        /// skips the Adler-32 check (managed backend only; useful when the container has its own checksum).
        /// </summary>
        public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination, bool verifyChecksum)
        {
            if (Backend == ZlibBackend.Managed) return InflateCore.Decompress(source, destination, true, verifyChecksum);
            if (source.IsEmpty) return 0; // (a null pointer would make UnmanagedMemoryStream throw)
            unsafe
            {
                fixed (byte* p = source)
                {
                    using var ms = new UnmanagedMemoryStream(p, source.Length);
                    using var z = new ZLibStream(ms, CompressionMode.Decompress);
                    return ReadFully(z, destination);
                }
            }
        }

        /// <summary>Decompresses a zlib stream of unknown decompressed size.</summary>
        public static byte[] Decompress(ReadOnlySpan<byte> source, int sizeHint = 0)
        {
            if (Backend == ZlibBackend.Managed) return InflateCore.Decompress(source, sizeHint, true, true);
            if (source.IsEmpty) return Array.Empty<byte>(); // (a null pointer would make UnmanagedMemoryStream throw)
            unsafe
            {
                fixed (byte* p = source)
                {
                    using var ms = new UnmanagedMemoryStream(p, source.Length);
                    using var z = new ZLibStream(ms, CompressionMode.Decompress);
                    using var o = new MemoryStream(Math.Max(sizeHint, 256));
                    z.CopyTo(o);
                    return o.ToArray();
                }
            }
        }

        /// <summary>Decompresses raw deflate data (no zlib header) into a buffer of known size.</summary>
        public static int DecompressRaw(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            if (Backend == ZlibBackend.Managed) return InflateCore.Decompress(source, destination, false, false);
            if (source.IsEmpty) return 0; // (a null pointer would make UnmanagedMemoryStream throw)
            unsafe
            {
                fixed (byte* p = source)
                {
                    using var ms = new UnmanagedMemoryStream(p, source.Length);
                    using var z = new DeflateStream(ms, CompressionMode.Decompress);
                    return ReadFully(z, destination);
                }
            }
        }

        /// <summary>Decompresses raw deflate data (no zlib header) of unknown decompressed size.</summary>
        public static byte[] DecompressRaw(ReadOnlySpan<byte> source, int sizeHint = 0)
        {
            if (Backend == ZlibBackend.Managed) return InflateCore.Decompress(source, sizeHint, false, false);
            if (source.IsEmpty) return Array.Empty<byte>(); // (a null pointer would make UnmanagedMemoryStream throw)
            unsafe
            {
                fixed (byte* p = source)
                {
                    using var ms = new UnmanagedMemoryStream(p, source.Length);
                    using var z = new DeflateStream(ms, CompressionMode.Decompress);
                    using var o = new MemoryStream(Math.Max(sizeHint, 256));
                    z.CopyTo(o);
                    return o.ToArray();
                }
            }
        }

        /// <summary>Compresses to a zlib stream. Level 0..9 (0 = store, 6 = default, 9 = smallest).</summary>
        public static byte[] Compress(ReadOnlySpan<byte> source, int level = 6)
        {
            // (for empty input the BCL stream writes nothing at all, not even a header: the managed encoder covers that)
            if (Backend == ZlibBackend.Managed || source.IsEmpty) return Deflater.Compress(source, level, true);
            using var o = new MemoryStream(source.Length / 2 + 64);
            using (var z = BclCompressor(o, level, true))
                z.Write(source);
            return o.ToArray();
        }

        /// <summary>Compresses to raw deflate data (no zlib header or checksum). Level 0..9.</summary>
        public static byte[] CompressRaw(ReadOnlySpan<byte> source, int level = 6)
        {
            if (Backend == ZlibBackend.Managed || source.IsEmpty) return Deflater.Compress(source, level, false);
            using var o = new MemoryStream(source.Length / 2 + 64);
            using (var z = BclCompressor(o, level, false))
                z.Write(source);
            return o.ToArray();
        }

        /// <summary>A stream that zlib-compresses everything written to it into <paramref name="output"/> (left open).</summary>
        public static Stream CreateCompressor(Stream output, int level = 6)
            => Backend == ZlibBackend.Managed ? new DeflaterStream(output, level, true) : BclCompressor(output, level, true);

        /// <summary>
        /// A new incremental decompressor (always the managed implementation): feed compressed spans, pull output
        /// spans of any size. See <see cref="Inflater"/>.
        /// </summary>
        public static Inflater CreateInflater(bool zlibWrapper = true, bool verifyChecksum = true) => new Inflater(zlibWrapper, verifyChecksum);

        static Stream BclCompressor(Stream output, int level, bool zlibWrapper)
        {
#if NET9_0_OR_GREATER
            // the BCL exposes the real zlib levels from .NET 9 on
            var options = new ZLibCompressionOptions { CompressionLevel = Math.Clamp(level, 0, 9) };
            return zlibWrapper ? new ZLibStream(output, options, leaveOpen: true) : new DeflateStream(output, options, leaveOpen: true);
#else
            // before .NET 9 only these four levels exist (zlib 0, 1, 6, 9)
            var l = MapLevel(level);
            return zlibWrapper ? new ZLibStream(output, l, leaveOpen: true) : new DeflateStream(output, l, leaveOpen: true);
#endif
        }

        static CompressionLevel MapLevel(int level) => level switch
        {
            <= 0 => CompressionLevel.NoCompression,
            <= 3 => CompressionLevel.Fastest,
            <= 7 => CompressionLevel.Optimal,
            _ => CompressionLevel.SmallestSize,
        };

        static int ReadFully(Stream s, Span<byte> destination)
        {
            if (destination.IsEmpty)
            {
                // nothing is wanted, but corrupt data should still be reported as with the managed backend
                s.ReadByte();
                return 0;
            }
            int total = 0;
            while (total < destination.Length)
            {
                int n = s.Read(destination.Slice(total));
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        /// <summary>Adler-32 checksum (zlib trailer). Vectorised where the hardware allows.</summary>
        public static uint Adler32(ReadOnlySpan<byte> data, uint adler = 1) => Checksums.Adler32(data, adler);

        /// <summary>CRC-32 (IEEE, as used by PNG and gzip). Uses carry-less multiplication or CRC instructions where available.</summary>
        public static uint Crc32(ReadOnlySpan<byte> data, uint crc = 0) => Checksums.Crc32(data, crc);
    }
}
