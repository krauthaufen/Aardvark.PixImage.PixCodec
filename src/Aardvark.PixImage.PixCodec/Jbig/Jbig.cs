using System;
using System.IO;
using Aardvark.ImageCodecs.JbigImpl;

namespace Aardvark.ImageCodecs
{
    public sealed class JbigDecodeOptions
    {
        /// <summary>
        /// How the bit planes of a multi-plane image combine into a level. JBIG does not record this; planes are
        /// conventionally Gray coded (the default here, as in jbigkit), which keeps the planes smooth. Set for plain
        /// binary weighted planes.
        /// </summary>
        public bool BinaryPlanes { get; set; }

        /// <summary>
        /// Upper limit for width x height. A JBIG header can announce a huge blank image in a few bytes, so the data
        /// length gives no protection; images beyond the limit are refused with <see cref="ImageNotSupportedException"/>.
        /// </summary>
        public long MaxPixels { get; set; } = 1L << 27;
    }

    public sealed class JbigEncodeOptions
    {
        /// <summary>
        /// Number of bit planes. 1 (the default) writes a bi-level image: samples below 128 become foreground (black).
        /// 2..8 keep that many most significant bits of each sample as separate planes.
        /// </summary>
        public int BitPlanes { get; set; } = 1;

        /// <summary>Planes carry plain binary weighted bits instead of Gray coded levels (see <see cref="JbigDecodeOptions.BinaryPlanes"/>).</summary>
        public bool BinaryPlanes { get; set; }

        /// <summary>Lines per stripe; 0 puts the whole image into one stripe. T.85 fax profiles use 128.</summary>
        public int LinesPerStripe { get; set; }

        /// <summary>Use the two line model template instead of the three line one (a little faster to decode, about 5% larger).</summary>
        public bool TwoLineTemplate { get; set; }

        /// <summary>Skip lines that repeat the line above ("typical prediction"). On by default; nearly free and very effective on documents.</summary>
        public bool TypicalPrediction { get; set; } = true;
    }

    /// <summary>
    /// JBIG1 (ITU-T T.82) bi-level image entities, as used inside TIFF (compression 34661) and fax.
    /// <para>Decoding covers the whole Recommendation: one or more bit planes, resolution reduction layers with typical
    /// and deterministic prediction (default or private tables), both lowest-layer templates, adaptive template moves,
    /// all six stripe orderings, SDRST, NEWLEN and comments, and several concatenated entities that add resolution
    /// layers to the same image.</para>
    /// <para>Encoding writes a single resolution layer (sequential mode).</para>
    /// </summary>
    public static unsafe class Jbig
    {
        static readonly JbigDecodeOptions s_defaults = new JbigDecodeOptions();

        /// <summary>
        /// Reads the 20 byte header: Width, Height and, as <see cref="ImageInfo.BitsPerSample"/>, the number of bit planes.
        /// The height is the one in the header; a NEWLEN marker later in the data (variable length images) can only
        /// shorten it. JBIG has no signature, and <see cref="ImageInfo.Format"/> is <see cref="ImageFormat.Unknown"/>.
        /// </summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> bie)
        {
            var h = JbigHeader.Parse(bie);
            if (h.XD > int.MaxValue || h.YD > int.MaxValue) throw new ImageNotSupportedException("JBIG image is too large");
            if (h.P > 16) throw new ImageNotSupportedException($"JBIG with {h.P} bit planes is not supported");
            return new ImageInfo(ImageFormat.Unknown, (int)h.XD, (int)h.YD, 1, h.P > 8 ? SampleType.UInt16 : SampleType.UInt8, ColorModel.Gray) { BitsPerSample = h.P };
        }

        /// <summary>
        /// Decodes a complete bi-level image entity (BIE) to a UInt8 Gray image with 0 = black (foreground, bit 1)
        /// and 255 = white (background, bit 0).
        /// </summary>
        public static RawImage Decode(ReadOnlySpan<byte> bie) => Decode(bie, null);

        /// <summary>
        /// Decodes a bi-level image entity, or several concatenated ones carrying successive resolution layers of one image.
        /// A single bit plane gives UInt8 Gray with 0 = black (foreground, bit 1) and 255 = white (background, bit 0).
        /// Several planes give the levels themselves, 0 .. 2^planes - 1 (UInt8 up to 8 planes, UInt16 up to 16), unscaled.
        /// </summary>
        public static RawImage Decode(ReadOnlySpan<byte> bie, JbigDecodeOptions? options)
        {
            options ??= s_defaults;
            var d = new JbigDecoder();
            d.Decode(bie, options.MaxPixels, null);
            int w = d.Width, h = d.Height, planes = d.Header.P;
            if (planes == 1)
            {
                var (pixels, stride, offset) = d.Plane(0);
                var gray = GC.AllocateUninitializedArray<byte>(checked(w * h));
                for (int y = 0; y < h; y++)
                {
                    var src = pixels.AsSpan(y * stride + offset, w);
                    var dst = gray.AsSpan(y * w, w);
                    for (int x = 0; x < w; x++) dst[x] = (byte)(src[x] - 1); // 1 -> 0, 0 -> 255
                }
                return new RawImage(w, h, ColorModel.Gray, gray);
            }

            var levels = new ushort[checked(w * h)];
            for (int k = 0; k < planes; k++)
            {
                // planes come most significant first; a Gray coded bit is the XOR of the binary bit and the one above it
                var (pixels, stride, offset) = d.Plane(k);
                bool gray = !options.BinaryPlanes;
                for (int y = 0; y < h; y++)
                {
                    var src = pixels.AsSpan(y * stride + offset, w);
                    var dst = levels.AsSpan(y * w, w);
                    for (int x = 0; x < w; x++)
                    {
                        int bit = src[x];
                        if (gray) bit ^= dst[x] & 1;
                        dst[x] = (ushort)(dst[x] << 1 | bit);
                    }
                }
            }
            if (planes > 8) return new RawImage(w, h, ColorModel.Gray, levels);
            var bytes = GC.AllocateUninitializedArray<byte>(levels.Length);
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)levels[i];
            return new RawImage(w, h, ColorModel.Gray, bytes);
        }

        /// <summary>
        /// Decodes one TIFF strip (compression 34661): a single plane BIE of the given width into packed 1 bit rows, most
        /// significant bit first, (width + 7) / 8 bytes per row, 1 = JBIG foreground. The entity may hold more lines than
        /// <paramref name="height"/> (the rest is dropped) but not fewer. Thread safe.
        /// </summary>
        internal static void DecodeForTiff(ReadOnlySpan<byte> bie, int width, int height, Span<byte> destination)
        {
            int rowBytes = (width + 7) / 8;
            if (width <= 0 || height <= 0 || destination.Length != (long)rowBytes * height)
                throw new ArgumentException("destination does not match the packed size of width x height");
            var header = JbigHeader.Parse(bie);
            if (header.P != 1) throw new ImageFormatException($"JBIG strip in TIFF has {header.P} bit planes, expected 1");
            if (header.XD != width) throw new ImageFormatException($"JBIG strip in TIFF is {header.XD} pixels wide, expected {width}");
            var d = new JbigDecoder();
            // the strip may announce more lines than it is asked for (and correct that with NEWLEN), within reason
            d.Decode(bie, Math.Max(4L * width * height, 1L << 24), null);
            if (d.Height < height) throw new ImageFormatException($"JBIG strip in TIFF has {d.Height} lines, expected {height}");
            var (pixels, stride, offset) = d.Plane(0);
            destination.Clear();
            for (int y = 0; y < height; y++)
            {
                var src = pixels.AsSpan(y * stride + offset, width);
                var dst = destination.Slice(y * rowBytes, rowBytes);
                for (int x = 0; x < width; x++) dst[x >> 3] |= (byte)(src[x] << (7 - (x & 7)));
            }
        }

        /// <summary>
        /// Encodes a UInt8 Gray image (an alpha channel is ignored) as a single layer JBIG entity. See
        /// <see cref="JbigEncodeOptions"/> for how samples map to bit planes.
        /// </summary>
        public static void Encode(RawImage image, Stream output, JbigEncodeOptions? options = null)
            => JbigEncoder.Encode(image, output, options ?? new JbigEncodeOptions());

        /// <summary>Encodes an image as JBIG into a byte array.</summary>
        public static byte[] Encode(RawImage image, JbigEncodeOptions? options = null)
        {
            using var ms = new MemoryStream();
            Encode(image, ms, options);
            return ms.ToArray();
        }

        /// <summary>
        /// Decodes a single plane BIE (one TIFF strip) to packed rows: most significant bit first, (width + 7) / 8 bytes
        /// per row, bits exactly as coded (1 = JBIG foreground), padding bits 0. The height is the final one (after
        /// NEWLEN). Multi-plane entities throw <see cref="ImageNotSupportedException"/>. Thread safe.
        /// </summary>
        internal static byte[] DecodeBits(ReadOnlySpan<byte> bie, out int width, out int height)
        {
            var header = JbigHeader.Parse(bie);
            if (header.P != 1) throw new ImageNotSupportedException($"JBIG entity with {header.P} bit planes; one was expected");
            var d = new JbigDecoder();
            d.Decode(bie, s_defaults.MaxPixels, null);
            width = d.Width; height = d.Height;
            int rowBytes = (width + 7) / 8;
            var bits = new byte[checked(rowBytes * height)];
            var (pixels, stride, offset) = d.Plane(0);
            for (int y = 0; y < height; y++)
            {
                var src = pixels.AsSpan(y * stride + offset, width);
                var dst = bits.AsSpan(y * rowBytes, rowBytes);
                for (int x = 0; x < width; x++) dst[x >> 3] |= (byte)(src[x] << (7 - (x & 7)));
            }
            return bits;
        }

        /// <summary>Encodes packed 1 bit rows (as <see cref="DecodeForTiff"/> produces them) into one BIE for a TIFF strip. Thread safe.</summary>
        internal static void EncodeForTiff(ReadOnlySpan<byte> packedRows, int width, int height, JbigEncodeOptions options, Stream output)
        {
            int rowBytes = (width + 7) / 8;
            if (width <= 0 || height <= 0 || packedRows.Length != (long)rowBytes * height)
                throw new ArgumentException("rows do not match the packed size of width x height");
            var plane = new byte[checked(width * height)];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++) plane[y * width + x] = (byte)(packedRows[y * rowBytes + (x >> 3)] >> (7 - (x & 7)) & 1);
            JbigEncoder.EncodePlanes(new[] { plane }, width, height, options, output);
        }
    }
}
