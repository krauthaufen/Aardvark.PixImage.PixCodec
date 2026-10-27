using System;
using System.IO;
using Aardvark.ImageCodecs.JpegImpl;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// Decoder settings. Independent of these: files whose components have sampling factors in a non-integer ratio
    /// (for instance 3:2), which the standard permits but libjpeg, libjpeg-turbo and the ISO reference software all
    /// refuse to decode, are decoded by bilinear interpolation of the smaller components. No conformance reference
    /// exists for such files; the result is a faithful picture, not a bit pattern any other decoder defines.
    /// </summary>
    public sealed class JpegDecodeOptions
    {
        /// <summary>
        /// Decode at 1/1, 1/2, 1/4 or 1/8 of the size (value 1, 2, 4 or 8) by using smaller inverse DCTs: much faster
        /// than decoding and shrinking, with the same result as libjpeg's scaled decoding. The output is
        /// ceil(size / denominator). Ignored for lossless JPEG.
        /// </summary>
        public int ScaleDenominator { get; set; } = 1;

        /// <summary>
        /// Decode at k/8 of the size, k = 1..8, by inverse DCTs of size k (0, the default, uses <see cref="ScaleDenominator"/>
        /// instead). The output is ceil(size * k / 8); each output sample is the area average of the full size decode over
        /// the part of the block it covers (the samples weighted by their overlap), so the result is close to decoding at
        /// full size and averaging areas, with memory for the smaller output only. k = 1, 2, 4 are also faster than a full
        /// decode; 3, 5, 6, 7 take about as long or longer. k = 1, 2, 4 give exactly what the matching
        /// <see cref="ScaleDenominator"/> gives; 3, 5, 6, 7 are not bit identical to any other decoder. Ignored for lossless JPEG.
        /// </summary>
        public int ScaleEighths { get; set; }

        /// <summary>
        /// Return 4 component (CMYK / YCCK) images as 4 channel <see cref="ColorModel.Other"/> data instead of converting
        /// them to RGB. The samples are C, M, Y, K as stored, which for the usual Adobe files means inverted (255 = no ink);
        /// YCCK is brought to that same form.
        /// </summary>
        public bool RawCmyk { get; set; }

        /// <summary>
        /// Skip the YCbCr to RGB conversion: YCbCr images come back as 3 channel <see cref="ColorModel.Other"/> data
        /// holding Y, Cb, Cr at full resolution (and YCCK, together with <see cref="RawCmyk"/>, as Y, Cb, Cr, K).
        /// </summary>
        public bool KeepYCbCr { get; set; }

        /// <summary>
        /// Rotate / mirror the pixels according to the EXIF orientation (default), so that the image comes back upright; its
        /// metadata then says orientation 1. False returns the pixels as stored with the file's orientation in the metadata.
        /// </summary>
        public bool ApplyOrientation { get; set; } = true;

        /// <summary>
        /// By default <see cref="Jpeg.Decode"/> and <see cref="Jpeg.GetInfo(ReadOnlySpan{byte})"/> skip junk in front of the
        /// first SOI marker (some cameras and container formats prepend data). Set to require the data to start with it.
        /// </summary>
        public bool RequireSoiAtStart { get; set; }

        /// <summary>
        /// Decode what is there when the file ends before its EOI marker (a cut-off download or copy) instead of throwing
        /// <see cref="ImageFormatException"/>, which is the default: data units that were never coded come out mid gray, the
        /// missing scans of a progressive file leave its detail coarse. <see cref="ImageMetadata.Truncated"/> tells such a
        /// result apart.
        /// </summary>
        public bool AllowTruncated { get; set; }

        /// <summary>
        /// Upper bound for the decoded size (after <see cref="ScaleDenominator"/> or <see cref="ScaleEighths"/>) in pixels; larger files are rejected with an
        /// <see cref="ImageNotSupportedException"/> before any pixel memory is allocated. 0 (the default) sets no limit beyond
        /// the format's own (65535 x 65535) and the array size. Lower it when decoding untrusted data with a memory budget.
        /// </summary>
        public long MaxPixels { get; set; }

        /// <summary>
        /// Return Gray and Rgb images as Rgba with an opaque alpha channel (gray expanded), written straight into the
        /// output while decoding instead of in a second copy. Other layouts (CMYK and YCbCr kept raw) are not affected.
        /// </summary>
        public bool Rgba { get; set; }

        /// <summary>Stops the decode with an <see cref="OperationCanceledException"/>; checked per row of MCUs.</summary>
        public System.Threading.CancellationToken CancellationToken { get; set; }

        internal JpegDecodeOptions Clone() => (JpegDecodeOptions)MemberwiseClone();
    }

    /// <summary>Chroma resolution of an encoded color JPEG.</summary>
    public enum JpegSubsampling
    {
        /// <summary>4:2:0 below quality 90, 4:4:4 from 90 up.</summary>
        Auto = 0,
        /// <summary>Full resolution chroma.</summary>
        Chroma444,
        /// <summary>Chroma halved horizontally.</summary>
        Chroma422,
        /// <summary>Chroma halved in both directions.</summary>
        Chroma420,
    }

    /// <summary>Quantization tables of a DCT based encode (both are scaled by <see cref="JpegEncodeOptions.Quality"/>).</summary>
    public enum JpegQuantTables
    {
        /// <summary>
        /// Tables tuned for this encoder: a smooth rise of the step size with spatial frequency, coarser for chroma.
        /// About 13 percent smaller files than <see cref="AnnexK"/> at the same visual quality (SSIMULACRA2 on the Kodak
        /// images; 8 percent at the same PSNR). At a given quality setting the files come out slightly smaller and
        /// look better than with the Annex K tables.
        /// </summary>
        Tuned = 0,
        /// <summary>The example tables of ITU-T T.81 Annex K with the IJG quality scaling, which libjpeg and most encoders use.</summary>
        AnnexK,
    }

    public sealed class JpegEncodeOptions
    {
        /// <summary>Stops the encode with an <see cref="OperationCanceledException"/>; checked per row of MCUs.</summary>
        public System.Threading.CancellationToken CancellationToken { get; set; }

        /// <summary>Quality 1..100.</summary>
        public int Quality { get; set; } = 90;

        /// <summary>Chroma subsampling for color images (ignored for grayscale).</summary>
        public JpegSubsampling Subsampling { get; set; } = JpegSubsampling.Auto;

        /// <summary>Build Huffman tables for this image instead of using the standard ones (smaller files, slightly slower).</summary>
        public bool OptimizeHuffman { get; set; } = true;

        /// <summary>
        /// Rate-distortion optimized quantization (trellis quantization): each block's quantized coefficients are chosen
        /// to minimize error plus lambda times the bits they cost with the Huffman tables actually used, instead of being
        /// rounded one by one. About 3 percent smaller files at the same visual quality (SSIMULACRA2; 5 percent at
        /// the same PSNR) for about twice the encode time, and a stream every decoder reads. Off (the default) rounds
        /// every coefficient on its own, as libjpeg does. Applies to 8 bit DCT JPEG (12 bit samples are always
        /// rounded); arithmetic coded files get the same coefficients as Huffman coded ones.
        /// </summary>
        public bool Trellis { get; set; }

        /// <summary>
        /// Which quantization tables to scale by <see cref="Quality"/>. Together with <see cref="Trellis"/> off,
        /// <see cref="JpegQuantTables.AnnexK"/> gives the classic libjpeg encode (the same tables, plain rounding).
        /// </summary>
        public JpegQuantTables QuantTables { get; set; }

        /// <summary>Write a progressive (multi scan) JPEG instead of a baseline one. Always uses optimized Huffman tables.</summary>
        public bool Progressive { get; set; }

        /// <summary>
        /// Use arithmetic coding instead of Huffman coding (sequential or, with <see cref="Progressive"/>, progressive):
        /// 5 to 10 percent smaller files, slower to write and read, and not supported by every decoder (browsers, for
        /// instance). <see cref="OptimizeHuffman"/> has no meaning then. Together with <see cref="Lossless"/> it gives
        /// arithmetic coded lossless JPEG (SOF11), which very few other decoders read.
        /// </summary>
        public bool Arithmetic { get; set; }

        /// <summary>
        /// Emit a restart marker every this many MCUs (0 = none, at most 65535). In lossless mode the interval is
        /// rounded up to whole lines.
        /// </summary>
        public int RestartInterval { get; set; }

        /// <summary>
        /// Write lossless JPEG (predictive coding, T.81 Annex H) instead of a DCT based one: exact for UInt8 and UInt16
        /// Gray / Rgb images (an alpha channel is dropped) and for <see cref="ColorModel.Other"/> images with up to 4
        /// channels, which are stored as they are. Quality, subsampling and <see cref="Progressive"/> do not apply.
        /// </summary>
        public bool Lossless { get; set; }

        /// <summary>Lossless predictor 1..7 (1 = left neighbour, 2 = above, 4 = plane fit a + b - c, 7 = average; 0 picks 1).</summary>
        public int Predictor { get; set; }

        /// <summary>
        /// Sample precision in bits; 0 picks the natural one. DCT based JPEG knows 8 bit (UInt8 images) and 12 bit
        /// (UInt16 images whose values must lie in 0..4095). Lossless JPEG takes 2..16 (default 8 for UInt8, 16 for
        /// UInt16; the values have to fit).
        /// </summary>
        public int Precision { get; set; }

        /// <summary>Test hook: explicit sampling factors h, v for Y, Cb, Cr (1..4 each) instead of <see cref="Subsampling"/>.</summary>
        internal int[]? SamplingFactors { get; set; }
    }

    /// <summary>
    /// JPEG codec.
    /// <para>Decoding: baseline, extended sequential and progressive Huffman coded JPEG with 8 bit samples, any
    /// sampling factors, restart intervals; grayscale, YCbCr and RGB decode to Gray / Rgb, CMYK and YCCK (Adobe,
    /// stored inverted) are converted to Rgb with R = (1 - C)(1 - K). The inverse DCT, the 2:1 chroma upsampling
    /// filters and the color conversion use the same integer arithmetic as libjpeg, so the output is bit identical
    /// to libjpeg(-turbo) based decoders with default settings. JFIF density, EXIF (incl. orientation) and ICC
    /// profiles are returned as metadata. Not supported: 12 bit samples, lossless, hierarchical and arithmetic coded files.</para>
    /// <para>Encoding: baseline or progressive JPEG from 8 bit Gray or Rgb images (an alpha channel is dropped).</para>
    /// </summary>
    public static unsafe partial class Jpeg
    {
        /// <summary>Returns whether the data starts with a JPEG signature (needs at most 16 bytes).</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header)
            => header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;

        static readonly JpegDecodeOptions s_defaults = new JpegDecodeOptions();

        /// <summary>Offset of the SOI marker that starts the image.</summary>
        static int FindStart(ReadOnlySpan<byte> data, JpegDecodeOptions o)
        {
            if (IsMatch(data)) return 0;
            int i = o.RequireSoiAtStart ? -1 : data.IndexOf((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]);
            if (i < 0) throw new ImageFormatException("invalid JPEG: missing SOI marker");
            return i;
        }

        static JpegDecoder Open(byte* p, ReadOnlySpan<byte> data, JpegDecodeOptions o, bool headerOnly, Func<JpegDecoder, JpegPost.Assembler?>? stream = null)
        {
            if (o.ScaleDenominator is not (1 or 2 or 4 or 8)) throw new ArgumentException("ScaleDenominator must be 1, 2, 4 or 8");
            if (o.ScaleEighths is < 0 or > 8) throw new ArgumentException("ScaleEighths must be 1..8 (or 0 to use ScaleDenominator)");
            int start = FindStart(data, o);
            var d = new JpegDecoder(p + start, data.Length - start) { ScaleEighths = o.ScaleEighths != 0 ? o.ScaleEighths : 8 / o.ScaleDenominator, OnStream = stream, MaxPixels = o.MaxPixels, Cancel = o.CancellationToken };
            d.Parse(headerOnly);
            return d;
        }

        /// <summary>What the decoder returns for this frame under the given options.</summary>
        static (JpegColorSpace Conversion, ColorModel Model, int Channels) Plan(JpegDecoder d, JpegDecodeOptions o)
        {
            int nc = d.Comps.Length;
            if (nc == 1) return (JpegColorSpace.Gray, ColorModel.Gray, 1);
            // lossless frames are never color transformed (there is no agreed convention, and it would not be lossless)
            var cs = d.Lossless ? JpegColorSpace.Raw : d.ColorSpace;
            switch (nc)
            {
                case 3:
                    if (cs == JpegColorSpace.YCbCr && !o.KeepYCbCr) return (cs, ColorModel.Rgb, 3);
                    return (JpegColorSpace.Raw, cs == JpegColorSpace.YCbCr ? ColorModel.Other : ColorModel.Rgb, 3);
                case 4:
                    if (cs == JpegColorSpace.Raw || (o.RawCmyk && (cs == JpegColorSpace.Cmyk || o.KeepYCbCr))) return (JpegColorSpace.Raw, ColorModel.Other, 4);
                    if (o.RawCmyk) return (JpegColorSpace.YcckToCmyk, ColorModel.Other, 4);
                    return (cs, ColorModel.Rgb, 3);
                default:
                    return (JpegColorSpace.Raw, ColorModel.Other, nc);
            }
        }

        /// <summary>
        /// Reads header level information without decoding pixel data (what <see cref="Decode"/> with default options returns).
        /// Only the segments in front of the frame header (SOF) are read - enough of the file has to be present up to
        /// there, or up to the DNL segment after the first scan when the frame header leaves the height open. Orientation,
        /// resolution and <see cref="ImageInfo.HasIccProfile"/> therefore reflect JFIF, EXIF and ICC segments that come
        /// before SOF, which is where every common writer puts them; segments placed after it show up only in the
        /// metadata of <see cref="Decode"/>.
        /// </summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => GetInfo(data, null);

        /// <summary>Reads header level information for a decode with the given options (scaled size, channel layout, orientation).</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data, JpegDecodeOptions? options)
        {
            options ??= s_defaults;
            fixed (byte* p = data)
            {
                var d = Open(p, data, options, headerOnly: true);
                var (_, model, channels) = Plan(d, options);
                var m = d.Metadata;
                var (mpf, gainMap) = MultiPictureInfo(data, options);
                var info = new ImageInfo(ImageFormat.Jpeg, d.OutWidth, d.OutHeight, channels,
                    d.Precision > 8 ? SampleType.UInt16 : SampleType.UInt8, model, mpf?.ViewCount ?? 1)
                {
                    MultiPicture = mpf,
                    HasGainMap = gainMap,
                    BitsPerSample = d.Precision,
                    Orientation = m.Orientation,
                    DpiX = m.DpiX,
                    DpiY = m.DpiY,
                    HasIccProfile = d.HasCompleteIcc,
                };
                return options.ApplyOrientation ? ExifTags.Upright(info) : info;
            }
        }

        /// <summary>
        /// Decodes a complete JPEG file held in memory. 8 bit frames give UInt8 samples, deeper ones (12 bit DCT, lossless
        /// up to 16 bit) UInt16 holding the values as coded, e.g. 0..4095 for 12 bit - they are not scaled to the full
        /// 16 bit range. Files that end early throw unless <see cref="JpegDecodeOptions.AllowTruncated"/> is set. The EXIF orientation
        /// is applied unless <see cref="JpegDecodeOptions.ApplyOrientation"/> is off.
        /// </summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, JpegDecodeOptions? options = null)
        {
            options ??= s_defaults;
            fixed (byte* p = data)
            {
                // 8 bit frames are converted band by band into the output while decoding instead of keeping whole planes
                // (rows go straight to their upright place, so a rotated photo needs no second copy either)
                byte[]? streamed = null;
                (JpegColorSpace, ColorModel, int) streamPlan = default;
                int streamOrientation = 1;
                JpegPost.Assembler? Stream(JpegDecoder dd)
                {
                    streamPlan = Plan(dd, options);
                    if (options.Rgba && streamPlan.Item2 is ColorModel.Gray or ColorModel.Rgb) streamPlan = (streamPlan.Item1, ColorModel.Rgba, 4);
                    int o = options.ApplyOrientation && dd.HasMetadata ? dd.Metadata.Orientation : 1;
                    streamOrientation = o is >= 2 and <= 8 ? o : 1;
                    streamed = GC.AllocateUninitializedArray<byte>(checked(dd.OutWidth * dd.OutHeight * streamPlan.Item3), pinned: true);
                    fixed (byte* q = streamed) return new JpegPost.Assembler(dd, q, dd.OutWidth, dd.OutHeight, streamPlan.Item1, streamOrientation, streamPlan.Item3 == 4 && options.Rgba);
                }
                var d = Open(p, data, options, headerOnly: false, Stream);
                if (d.Truncated && !options.AllowTruncated)
                    throw new ImageFormatException("invalid JPEG: the file ends before its EOI marker (truncated; JpegDecodeOptions.AllowTruncated decodes what is there)");
                // (as planned when the scan started, should a marker after it change the color space)
                var (cs, model, channels) = d.Streamed ? streamPlan : Plan(d, options);
                int w = d.OutWidth, h = d.OutHeight;
                int count = checked(w * h * channels);
                Array pixels;
                if (d.Streamed) pixels = streamed!;
                else if (!d.Wide)
                {
                    var bytes = GC.AllocateUninitializedArray<byte>(count);
                    JpegPost.Assemble(d, bytes, w, h, cs);
                    pixels = bytes;
                }
                else
                {
                    var words = GC.AllocateUninitializedArray<ushort>(count);
                    JpegPost16.Assemble(d, words, w, h, cs);
                    if (d.Precision > 8) pixels = words;
                    else
                    {
                        // lossless with up to 8 bit
                        var bytes = GC.AllocateUninitializedArray<byte>(count);
                        for (int i = 0; i < count; i++) bytes[i] = (byte)words[i];
                        pixels = bytes;
                    }
                }
                var meta = d.HasMetadata || d.Truncated ? d.Metadata : null;
                if (d.Truncated) meta!.Truncated = true;
                if (d.Streamed && streamOrientation >= 5) (w, h) = (h, w);
                if (d.Streamed && streamOrientation > 1) ImageOps.MarkUpright(meta, streamOrientation);
                var img = new RawImage(w, h, model, pixels, channels, meta);
                if (options.Rgba && model is ColorModel.Gray or ColorModel.Rgb)
                    img = img.Data is byte[] ? ToRgba(img, (byte)255) : ToRgba(img, (ushort)((1 << d.Precision) - 1));
                return options.ApplyOrientation ? ImageOps.ApplyOrientation(img) : img;
            }
        }

        /// <summary>
        /// Decodes in bands of <paramref name="bandHeight"/> rows, top to bottom, without ever holding the whole image: 8 bit
        /// files go through small ring buffers, so memory stays at a few rows of MCUs plus one band, whatever the image size
        /// (12 bit, lossless and multi-scan sequential files are decoded whole and then cut into bands). Progressive files
        /// are streamed too, but keep the coefficients of the whole image until the last scan: two bytes per sample of every
        /// component, as much as the decoded image or more. <paramref name="onBand"/>
        /// gets each band and the index of its first row; all bands but the last have <paramref name="bandHeight"/> rows. The
        /// band image is reused for the next band: copy what has to outlive the call. The pixels are as stored - the
        /// orientation is not applied (the metadata keeps it) - otherwise the options apply as for <see cref="Decode"/>
        /// (scaling, Rgba, MaxPixels, cancellation). A truncated file throws after the bands that could be decoded, unless
        /// <see cref="JpegDecodeOptions.AllowTruncated"/> is set. Returns the image's metadata (null if it has none).
        /// </summary>
        public static ImageMetadata? DecodeBands(ReadOnlySpan<byte> data, int bandHeight, Action<RawImage, int> onBand, JpegDecodeOptions? options = null)
        {
            ImageBands.CheckArguments(bandHeight, onBand);
            options ??= s_defaults;
            fixed (byte* p = data)
            {
                RawImage? band = null;
                JpegPost.Assembler? Stream(JpegDecoder dd)
                {
                    var (cs, model, channels) = Plan(dd, options);
                    bool rgba = options.Rgba && model is ColorModel.Gray or ColorModel.Rgb;
                    if (rgba) (model, channels) = (ColorModel.Rgba, 4);
                    int rows = Math.Min(bandHeight, dd.OutHeight);
                    var buf = GC.AllocateUninitializedArray<byte>(checked(dd.OutWidth * rows * channels), pinned: true);
                    var b = band = new RawImage(dd.OutWidth, rows, model, buf, channels, dd.HasMetadata ? dd.Metadata : null);
                    fixed (byte* q = buf)
                        return new JpegPost.Assembler(dd, q, dd.OutWidth, dd.OutHeight, cs, 1, rgba) { BandRows = rows, OnBand = (y, n) => ImageBands.Deliver(b, y, n, onBand) };
                }
                var d = Open(p, data, options, headerOnly: false, Stream);
                if (d.Truncated && !options.AllowTruncated)
                    throw new ImageFormatException("invalid JPEG: the file ends before its EOI marker (truncated; JpegDecodeOptions.AllowTruncated decodes what is there)");
                var meta = d.HasMetadata || d.Truncated ? d.Metadata : null;
                if (d.Truncated) meta!.Truncated = true;
                if (d.Streamed) return meta;
            }
            // not decodable in bands: whole, then cut
            var o = options.Clone();
            o.ApplyOrientation = false;
            var img = Decode(data, o);
            ImageBands.Slice(img, bandHeight, onBand);
            return img.Metadata;
        }

        /// <summary>Rgba of a Gray / Rgb image that was not assembled as such (12 bit and lossless frames); alpha = <paramref name="opaque"/>.</summary>
        static RawImage ToRgba<T>(RawImage img, T opaque) where T : unmanaged
        {
            int n = img.Width * img.Height, c = img.Channels, g = c == 1 ? 0 : 1;
            var s = (T[])img.Data;
            var o = GC.AllocateUninitializedArray<T>(n * 4);
            for (int i = 0; i < n; i++)
            {
                o[4 * i] = s[i * c]; o[4 * i + 1] = s[i * c + g]; o[4 * i + 2] = s[i * c + 2 * g]; o[4 * i + 3] = opaque;
            }
            return new RawImage(img.Width, img.Height, ColorModel.Rgba, o, 4, img.Metadata);
        }

        // ---------------------------------------------------------------- JPEG inside TIFF

        /// <summary>
        /// Decodes one JPEG compressed TIFF strip or tile. <paramref name="jpegTables"/> is the content of the JPEGTables
        /// tag (a tables-only stream, empty when absent) and is parsed on every call; use the overload taking a
        /// <see cref="JpegTiffTables"/> to parse it once.
        /// </summary>
        internal static void DecodeForTiff(ReadOnlySpan<byte> jpegTables, ReadOnlySpan<byte> data, int width, int height, int components, bool convertYCbCrToRgb, Span<byte> destination)
            => DecodeForTiff(jpegTables.Length > 0 ? JpegTiffTables.Parse(jpegTables) : null, data, width, height, components, convertYCbCrToRgb, destination);

        /// <summary>
        /// Decodes one JPEG compressed TIFF strip or tile: an abbreviated or complete stream (DCT based with 8 bit samples,
        /// or lossless with up to 8 bit) whose frame may be larger than width x height. The top left width x height pixels
        /// are written interleaved with <paramref name="components"/> samples each, chroma at full resolution. No color
        /// space guessing: YCbCr is converted to RGB only when asked (and the stream has 3 components), otherwise the
        /// components come out as stored. Thread safe.
        /// </summary>
        internal static void DecodeForTiff(JpegTiffTables? tables, ReadOnlySpan<byte> data, int width, int height, int components, bool convertYCbCrToRgb, Span<byte> destination)
        {
            var cs = TiffConversion(components, convertYCbCrToRgb);
            fixed (byte* p = data)
            fixed (byte* o = destination)
            {
                // 8 bit data goes band by band straight into the destination (frames that do not fit are rejected after parsing)
                byte* op = o;
                var d = OpenForTiff(p, data.Length, tables, width, height, components, destination.Length,
                    dd => dd.Comps.Length == components && dd.Width >= width && dd.Height >= height ? new JpegPost.Assembler(dd, op, width, height, cs) : null);
                if (d.Streamed) return;
                if (d.Precision > 8) throw new ImageFormatException($"JPEG data in TIFF has {d.Precision} bit samples, expected at most 8");
                if (!d.Wide) { JpegPost.Assemble(d, destination, width, height, cs); return; }
                var wide = new ushort[destination.Length];
                JpegPost16.Assemble(d, wide, width, height, cs);
                for (int i = 0; i < wide.Length; i++) destination[i] = (byte)wide[i];
            }
        }

        /// <summary>
        /// Like the byte overload for streams of any precision: 12 bit DCT data gives 0..4095, lossless data its values
        /// as coded (2..16 bit), 8 bit data is widened unchanged. Nothing is scaled.
        /// </summary>
        internal static void DecodeForTiff(JpegTiffTables? tables, ReadOnlySpan<byte> data, int width, int height, int components, bool convertYCbCrToRgb, Span<ushort> destination)
        {
            fixed (byte* p = data)
            {
                var d = OpenForTiff(p, data.Length, tables, width, height, components, destination.Length);
                var cs = TiffConversion(components, convertYCbCrToRgb);
                if (d.Wide) { JpegPost16.Assemble(d, destination, width, height, cs); return; }
                var narrow = new byte[destination.Length];
                JpegPost.Assemble(d, narrow, width, height, cs);
                for (int i = 0; i < narrow.Length; i++) destination[i] = narrow[i];
            }
        }

        static JpegColorSpace TiffConversion(int components, bool convertYCbCrToRgb)
            => components == 3 && convertYCbCrToRgb ? JpegColorSpace.YCbCr : components == 1 ? JpegColorSpace.Gray : JpegColorSpace.Raw;

        static JpegDecoder OpenForTiff(byte* p, int length, JpegTiffTables? tables, int width, int height, int components, int destinationLength,
            Func<JpegDecoder, JpegPost.Assembler?>? stream = null)
        {
            if (width <= 0 || height <= 0 || components <= 0 || destinationLength != (long)width * height * components)
                throw new ArgumentException("destination does not match width x height x components");
            var d = new JpegDecoder(p, length) { OnStream = stream };
            if (tables != null) d.ImportTables(tables);
            d.Parse(headerOnly: false);
            if (d.Comps.Length != components)
                throw new ImageFormatException($"JPEG data in TIFF has {d.Comps.Length} components, expected {components}");
            if (d.Width < width || d.Height < height)
                throw new ImageFormatException($"JPEG data in TIFF is {d.Width}x{d.Height}, smaller than the {width}x{height} strip or tile");
            return d;
        }

        /// <summary>Frame parameters of a strip / tile stream (no tables needed), e.g. to size the buffer for a lossless DNG tile.</summary>
        internal static (int Width, int Height, int Components, int Precision, bool Lossless) GetTiffStreamInfo(ReadOnlySpan<byte> data)
        {
            fixed (byte* p = data)
            {
                var d = new JpegDecoder(p, data.Length);
                d.Parse(headerOnly: true);
                return (d.Width, d.Height, d.Comps.Length, d.Precision, d.Lossless);
            }
        }

        /// <summary>
        /// Old-style TIFF JPEG (compression 6) whose strips / tiles hold bare entropy coded data: baseline, 8 bit, the
        /// frame described by the TIFF tags. <paramref name="tables"/> comes from <see cref="JpegTiffTables.FromOldStyle"/>;
        /// component i uses its tables number i (falling back to the last one given). Component 0 is sampled
        /// hSub x vSub, the others 1 x 1. If the data turns out to start with an SOI marker it is decoded as a normal
        /// stream with <paramref name="tables"/> as defaults, so this can be called for any compression 6 strip.
        /// </summary>
        internal static void DecodeForTiffRaw(JpegTiffTables tables, ReadOnlySpan<byte> entropyData, int frameWidth, int frameHeight,
            int components, int hSub, int vSub, int restartInterval, int width, int height, bool convertYCbCrToRgb, Span<byte> destination)
        {
            if (entropyData.Length >= 2 && entropyData[0] == 0xFF && entropyData[1] == 0xD8)
            {
                DecodeForTiff(tables, entropyData, width, height, components, convertYCbCrToRgb, destination);
                return;
            }
            if (frameWidth <= 0 || frameHeight <= 0 || frameWidth > 65535 || frameHeight > 65535 || components < 1 || components > 4
                || hSub < 1 || hSub > 4 || vSub < 1 || vSub > 4 || restartInterval < 0 || restartInterval > 65535)
                throw new ImageFormatException("invalid old-style JPEG parameters in TIFF");
            // wrap the data in the headers it lacks: SOI, SOF0, DRI, SOS ... EOI
            int head = 2 + 10 + 3 * components + 6 + 8 + 2 * components;
            var stream = new byte[head + entropyData.Length + 2];
            int o = 0;
            void Put(params int[] bytes) { foreach (int b in bytes) stream[o++] = (byte)b; }
            Put(0xFF, 0xD8);
            Put(0xFF, 0xC0, 0, 8 + 3 * components, 8, frameHeight >> 8, frameHeight, frameWidth >> 8, frameWidth, components);
            for (int i = 0; i < components; i++) Put(i + 1, i == 0 ? hSub << 4 | vSub : 0x11, tables.LastIndex(tables.Quant, i));
            Put(0xFF, 0xDD, 0, 4, restartInterval >> 8, restartInterval);
            Put(0xFF, 0xDA, 0, 6 + 2 * components, components);
            for (int i = 0; i < components; i++) Put(i + 1, tables.LastIndex(tables.Dc, i) << 4 | tables.LastIndex(tables.Ac, i));
            Put(0, 63, 0);
            entropyData.CopyTo(stream.AsSpan(o));
            stream[^2] = 0xFF; stream[^1] = 0xD9;
            DecodeForTiff(tables, stream, width, height, components, convertYCbCrToRgb, destination);
        }

        /// <summary>
        /// Encodes one strip or tile for a JPEG compressed TIFF: width x height pixels with 1 or 3 interleaved 8 bit
        /// components become a complete, self-contained baseline stream (own DQT / DHT, no JFIF, Adobe or EXIF segments,
        /// component ids 1, 2, 3). Quality, Subsampling, OptimizeHuffman and RestartInterval of the options apply;
        /// Progressive, Lossless and Precision are ignored. With <paramref name="rgbToYCbCr"/> three components are
        /// converted to YCbCr and subsampled as <see cref="ResolveSubsampling"/> says; without, they are stored as they
        /// are at full resolution. Thread safe.
        /// </summary>
        internal static void EncodeForTiff(ReadOnlySpan<byte> pixels, int width, int height, int components, JpegEncodeOptions options, bool rgbToYCbCr, Stream output)
            => JpegEncoder.EncodeForTiff(TiffImage(pixels.ToArray(), pixels.Length, width, height, components), output, TiffOptions(options), !rgbToYCbCr);

        /// <summary>The 12 bit variant: samples 0..4095, written as an extended sequential (SOF1) stream with optimized tables.</summary>
        internal static void EncodeForTiff(ReadOnlySpan<ushort> pixels, int width, int height, int components, JpegEncodeOptions options, bool rgbToYCbCr, Stream output)
            => JpegEncoder.EncodeForTiff(TiffImage(pixels.ToArray(), pixels.Length, width, height, components), output, TiffOptions(options), !rgbToYCbCr);

        /// <summary>Horizontal and vertical chroma subsampling a color encode with these options uses (what Auto resolves to).</summary>
        internal static (int H, int V) ResolveSubsampling(JpegEncodeOptions options)
        {
            var ss = options.Subsampling;
            if (ss == JpegSubsampling.Auto) ss = Math.Clamp(options.Quality, 1, 100) >= 90 ? JpegSubsampling.Chroma444 : JpegSubsampling.Chroma420;
            return ss == JpegSubsampling.Chroma444 ? (1, 1) : ss == JpegSubsampling.Chroma422 ? (2, 1) : (2, 2);
        }

        static RawImage TiffImage(Array pixels, int length, int width, int height, int components)
        {
            if (components != 1 && components != 3) throw new ArgumentException("JPEG in TIFF is encoded from 1 or 3 components");
            if (width <= 0 || height <= 0 || length != (long)width * height * components) throw new ArgumentException("pixels do not match width x height x components");
            return new RawImage(width, height, components == 1 ? ColorModel.Gray : ColorModel.Rgb, pixels);
        }

        static JpegEncodeOptions TiffOptions(JpegEncodeOptions o)
            => new JpegEncodeOptions { Quality = o.Quality, Subsampling = o.Subsampling, OptimizeHuffman = o.OptimizeHuffman, Trellis = o.Trellis, QuantTables = o.QuantTables, RestartInterval = o.RestartInterval, Arithmetic = o.Arithmetic };

        /// <summary>Encodes an image as JPEG to the stream (which only needs to be writable, not seekable).</summary>
        public static void Encode(RawImage image, Stream output, JpegEncodeOptions? options = null)
            => JpegEncoder.Encode(image, output, options ?? new JpegEncodeOptions());

        /// <summary>Encodes an image as JPEG into a byte array.</summary>
        public static byte[] Encode(RawImage image, JpegEncodeOptions? options = null)
        {
            using var ms = new MemoryStream();
            Encode(image, ms, options);
            return ms.ToArray();
        }
    }

    /// <summary>
    /// Quantization and Huffman tables shared by the strips / tiles of a JPEG compressed TIFF, parsed once.
    /// Immutable after creation and safe to use from several threads.
    /// </summary>
    internal sealed class JpegTiffTables
    {
        internal readonly int[]?[] Quant = new int[4][];
        internal readonly HuffDecodeTable?[] Dc = new HuffDecodeTable[4];
        internal readonly HuffDecodeTable?[] Ac = new HuffDecodeTable[4];
        internal int RestartInterval;
        /// <summary>Arithmetic coding conditioning (DC lower / upper bounds and AC thresholds, 4 each), if a DAC segment was present.</summary>
        internal byte[]? Conditioning;

        JpegTiffTables() { }

        /// <summary>The content of a TIFF JPEGTables tag: a tables-only JPEG stream (may be empty).</summary>
        public static unsafe JpegTiffTables Parse(ReadOnlySpan<byte> jpegTables)
        {
            var t = new JpegTiffTables();
            if (jpegTables.Length == 0) return t;
            fixed (byte* p = jpegTables)
            {
                var d = new JpegDecoder(p, jpegTables.Length) { TablesOnly = true };
                d.Parse(headerOnly: false);
                d.ExportTables(t);
            }
            return t;
        }

        /// <summary>
        /// Tables of old-style TIFF JPEG (compression 6) as stored at the offsets given by the JPEGQTables, JPEGDCTables
        /// and JPEGACTables tags, one per component: quantization tables are 64 bytes in zigzag order, Huffman tables 16
        /// code length counts followed by the symbols (trailing bytes are ignored). Arrays and entries may be missing.
        /// </summary>
        public static unsafe JpegTiffTables FromOldStyle(byte[]?[]? qTables, byte[]?[]? dcTables, byte[]?[]? acTables)
        {
            var t = new JpegTiffTables();
            for (int i = 0; i < 4; i++)
            {
                if (qTables != null && i < qTables.Length && qTables[i] is { } q)
                {
                    if (q.Length < 64) throw new ImageFormatException("truncated JPEG quantization table in TIFF");
                    var table = new int[64];
                    for (int k = 0; k < 64; k++) table[JpegTables.ZigZag[k]] = q[k];
                    t.Quant[i] = table;
                }
                if (dcTables != null && i < dcTables.Length && dcTables[i] is { } dc) t.Dc[i] = Huffman(dc);
                if (acTables != null && i < acTables.Length && acTables[i] is { } ac) t.Ac[i] = Huffman(ac);
            }
            return t;
        }

        static HuffDecodeTable Huffman(byte[] raw)
        {
            if (raw.Length < 16) throw new ImageFormatException("truncated JPEG Huffman table in TIFF");
            int total = 0;
            for (int i = 0; i < 16; i++) total += raw[i];
            if (total > 256 || raw.Length < 16 + total) throw new ImageFormatException("invalid JPEG Huffman table in TIFF");
            return new HuffDecodeTable(raw.AsSpan(0, 16), raw.AsSpan(16, total));
        }

        /// <summary>Table number for component i: its own if present, else the last one defined before it.</summary>
        internal int LastIndex(object?[] tables, int i)
        {
            while (i > 0 && tables[i] == null) i--;
            return i;
        }
    }
}
