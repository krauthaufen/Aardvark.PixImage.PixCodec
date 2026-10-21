using System;

namespace Aardvark.ImageCodecs.Compression
{
    /// <summary>
    /// Zstandard (RFC 8878), fully managed. The decoder supports all block types, FSE and Huffman coded literals
    /// and sequences, repeat offsets, multiple and skippable frames, the xxHash64 content checksum and dictionaries
    /// (structured or raw content). The compressor is a simple fast one with a single setting (see <see cref="Compress(ReadOnlySpan{byte}, int, bool)"/>).
    /// </summary>
    public static class Zstd
    {
        [ThreadStatic] static ZstdDecoder? cached;

        /// <summary>
        /// Decompresses one or more concatenated zstd frames (skippable frames are skipped) into a buffer of known
        /// maximum size. Returns the number of bytes written. Throws <see cref="ImageFormatException"/> on malformed
        /// input or when the output does not fit, and <see cref="ImageNotSupportedException"/> when a dictionary
        /// is required.
        /// </summary>
        public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            byte[]? none = null;
            return (cached ??= new ZstdDecoder()).Decode(source, destination, ref none);
        }

        /// <summary>
        /// Decompresses one or more concatenated zstd frames of unknown decompressed size. The output grows with
        /// the data; a frame header that announces a huge size does not by itself cause a huge allocation.
        /// </summary>
        public static byte[] Decompress(ReadOnlySpan<byte> source)
        {
            byte[]? buffer = new byte[(int)Math.Clamp((long)source.Length * 4, 4096, 1 << 20)];
            int n = (cached ??= new ZstdDecoder()).Decode(source, default, ref buffer);
            if (n != buffer!.Length) Array.Resize(ref buffer, n);
            return buffer;
        }

        /// <summary>
        /// As <see cref="Decompress(ReadOnlySpan{byte}, Span{byte})"/>, with a dictionary: either a structured
        /// one as made by <c>zstd --train</c> (its id must match a frame's dictionary id) or any bytes used as
        /// plain content ("raw" dictionary). Frames that do not need the dictionary decode as usual.
        /// </summary>
        public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination, ReadOnlySpan<byte> dictionary)
        {
            byte[]? none = null;
            return (cached ??= new ZstdDecoder()).Decode(source, destination, ref none, dictionary);
        }

        /// <summary>Decompresses frames of unknown decompressed size with a dictionary.</summary>
        public static byte[] DecompressWithDictionary(ReadOnlySpan<byte> source, ReadOnlySpan<byte> dictionary)
        {
            byte[]? buffer = new byte[(int)Math.Clamp((long)source.Length * 4, 4096, 1 << 20)];
            int n = (cached ??= new ZstdDecoder()).Decode(source, default, ref buffer, dictionary);
            if (n != buffer!.Length) Array.Resize(ref buffer, n);
            return buffer;
        }

        /// <summary>
        /// Compresses to a single zstd frame (with the content size in its header) that any zstd decoder reads.
        /// Levels: 1 = fast greedy matching, 3 = default (lazy matching through hash chains), higher levels search
        /// deeper with larger windows (up to 22; there is no optimal parser, so the top levels gain less than the
        /// reference implementation's). Literals are Huffman coded, sequences FSE coded, with raw/RLE fallbacks.
        /// <paramref name="checksum"/> appends the xxHash64 content checksum.
        /// </summary>
        public static byte[] Compress(ReadOnlySpan<byte> source, int level, bool checksum = false) => ZstdEncoder.Compress(source, level, checksum);

        /// <summary>
        /// Compresses with a dictionary: a structured one (as made by <c>zstd --train</c>; its id is stored in the
        /// frame) or any bytes used as plain content. The result needs the same dictionary to decompress
        /// (<c>zstd -D</c>, or <see cref="Decompress(ReadOnlySpan{byte}, Span{byte}, ReadOnlySpan{byte})"/>).
        /// </summary>
        public static byte[] Compress(ReadOnlySpan<byte> source, ReadOnlySpan<byte> dictionary, int level = 3, bool checksum = false)
            => ZstdEncoder.Compress(source, level, checksum, dictionary);

        /// <summary>Compresses at the default level (3).</summary>
        public static byte[] Compress(ReadOnlySpan<byte> source, bool checksum = false) => ZstdEncoder.Compress(source, 3, checksum);
    }
}
