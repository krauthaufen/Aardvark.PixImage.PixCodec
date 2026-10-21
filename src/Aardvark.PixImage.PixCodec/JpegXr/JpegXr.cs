using System;
using System.IO;
using Aardvark.ImageCodecs.JpegXrImpl;

namespace Aardvark.ImageCodecs
{
    public sealed class JpegXrDecodeOptions
    {
        /// <summary>
        /// Rotate / mirror the pixels according to the file's SPATIAL_XFRM_PRIMARY (default), so that the image comes back
        /// upright; the metadata then says orientation 1. False returns the pixels as coded, with the orientation (as an EXIF
        /// value) in the metadata.
        /// </summary>
        public bool ApplyOrientation { get; set; } = true;

        /// <summary>
        /// true (default): CMYK images (with or without alpha) are converted to RGB (naively, as TIFF does). false: they come
        /// back as their 4 (or 5) samples with <see cref="ColorModel.Other"/>.
        /// </summary>
        public bool ConvertToRgb { get; set; } = true;

        /// <summary>Divide premultiplied colours (32bppPBGRA, 64bppPRGBA, 128bppPRGBAFloat) by alpha (default), since RawImage alpha is straight.</summary>
        public bool Unpremultiply { get; set; } = true;

        /// <summary>
        /// Upper bound for the image in pixels; larger files are rejected with <see cref="ImageNotSupportedException"/>. The
        /// default (2^28, e.g. 16384 x 16384) bounds the working memory, about 4 bytes per pixel and component plus the result.
        /// </summary>
        public long MaxPixels { get; set; } = 1L << 28;

        /// <summary>Reconstruct and format large images on several threads (same result).</summary>
        public bool Multithreaded { get; set; } = true;

        internal static readonly JpegXrDecodeOptions Default = new();
    }

    /// <summary>The pixel format (Table A.6 of T.832) to write; Auto picks one from the sample type and colour model.</summary>
    public enum JpegXrPixelFormat
    {
        /// <summary>Gray / Rgb / Rgba (GrayAlpha as Rgba): 8 bit, 16 bit, half or float formats by sample type; Other: n channels.</summary>
        Auto = 0,
        Gray8, Gray16, Gray16Fixed, Gray16Half, Gray32Fixed, Gray32Float, BlackWhite,
        Rgb24, Bgr24, Bgr32, Rgb48, Rgb48Fixed, Rgb48Half, Rgb64Fixed, Rgb64Half, Rgb96Fixed, Rgb128Fixed, Rgb128Float,
        /// <summary>Packed RGB: 5 / 6 bit fields from 8 bit samples; Bgr101010 (and the 10 bit YCC formats) take UInt16 samples as 10 bit values 0..1023, as decoding returns them.</summary>
        Bgr555, Bgr565, Bgr101010,
        /// <summary>Radiance RGBE (from float samples).</summary>
        Rgbe,
        Bgra32, Rgba64, Rgba64Fixed, Rgba64Half, Rgba128Fixed, Rgba128Float,
        /// <summary>Premultiplied alpha formats: the colours are multiplied by alpha when encoding.</summary>
        Pbgra32, Prgba64, Prgba128Float,
        /// <summary>CMYK formats take 4 (or 5 with alpha) channels in the order C, M, Y, K (, alpha).</summary>
        Cmyk32, Cmyk64, CmykAlpha40, CmykAlpha80, CmykDirect32, CmykDirect64, CmykDirectAlpha40, CmykDirectAlpha80,
        /// <summary>3 .. 8 channels of 8 / 16 bits (<see cref="JpegXrEncodeOptions.LastChannelIsAlpha"/>: plus alpha).</summary>
        Channels8, Channels16,
        /// <summary>YCC interchange formats take 3 (or 4 with alpha) channels Y, Cb, Cr at full size; 4:2:0 / 4:2:2 ones are subsampled.</summary>
        Ycc420, Ycc422, Ycc444, Ycc422_10, Ycc444_10, Ycc422_16, Ycc444_16, Ycc420Alpha, Ycc422Alpha, Ycc444Alpha,
    }

    /// <summary>Internal chroma format of RGB images.</summary>
    public enum JpegXrSubsampling
    {
        Yuv444 = 0,
        Yuv422 = 1,
        Yuv420 = 2,
        /// <summary>Luma only: the image is stored gray (decoders return R = G = B).</summary>
        YOnly = 3,
    }

    /// <summary>Where the alpha channel goes.</summary>
    public enum JpegXrAlphaMode
    {
        /// <summary>A separate coded image (ALPHA_OFFSET), what jxrlib writes by default.</summary>
        Planar = 0,
        /// <summary>An alpha image plane in the same codestream, macroblock interleaved.</summary>
        Interleaved = 1,
    }

    /// <summary>Which frequency bands are kept (BANDS_PRESENT).</summary>
    public enum JpegXrBands
    {
        All = 0,
        NoFlexbits = 1,
        NoHighpass = 2,
        DcOnly = 3,
    }

    public sealed class JpegXrEncodeOptions
    {
        /// <summary>1 (default) is lossless (for 32 bit formats: as close as JPEG XR gets, 22 bits or more are kept); below 1 lossy, lower is smaller.</summary>
        public double Quality { get; set; } = 1.0;
        /// <summary>Quality of the alpha channel; null (default) is lossless.</summary>
        public double? AlphaQuality { get; set; }
        /// <summary>Explicit quantization parameter (1 .. 255) for all bands and components of lossy coding, instead of the one derived from Quality.</summary>
        public int Qp { get; set; }
        public JpegXrPixelFormat PixelFormat { get; set; } = JpegXrPixelFormat.Auto;
        /// <summary>For n-channel formats from <see cref="ColorModel.Other"/>: the last channel is alpha.</summary>
        public bool LastChannelIsAlpha { get; set; }
        /// <summary>Overlap filtering 0, 1 or 2 levels; null: 1, or 2 for qualities below 0.5 (as jxrlib).</summary>
        public int? Overlap { get; set; }
        /// <summary>Chroma subsampling of RGB images; null: 4:4:4, or 4:2:0 for qualities below 0.5 (as jxrlib).</summary>
        public JpegXrSubsampling? Subsampling { get; set; }
        public JpegXrAlphaMode AlphaMode { get; set; } = JpegXrAlphaMode.Planar;
        /// <summary>Frequency ordered codestream (DC, LP, HP and flexbits packets per tile) instead of spatial order.</summary>
        public bool FrequencyOrder { get; set; }
        /// <summary>Tile size in pixels (rounded up to macroblocks of 16); 0 = one tile across / down the image.</summary>
        public int TileWidth { get; set; }
        public int TileHeight { get; set; }
        /// <summary>Hard tiles: no overlap filtering across tile boundaries.</summary>
        public bool HardTiling { get; set; }
        /// <summary>Flexbits trimmed from the highpass coefficients (0 .. 15).</summary>
        public int TrimFlexbits { get; set; }
        public JpegXrBands Bands { get; set; } = JpegXrBands.All;
        /// <summary>Encode tiles and large images on several threads (same result).</summary>
        public bool Multithreaded { get; set; } = true;

        /// <summary>Top and left windowing margins (TOP_MARGIN, LEFT_MARGIN, 0 .. 63): coded but not output (tests).</summary>
        internal int TopMargin { get; set; }
        internal int LeftMargin { get; set; }
    }

    /// <summary>
    /// JPEG XR (ITU-T T.832 | ISO/IEC 29199-2, formerly HD Photo / Windows Media Photo; .jxr, .wdp, .hdp).
    /// <para>Decoding: the tag based file format of Annex A (and bare codestreams), every pixel format of Table A.6, every
    /// codestream feature: spatial and frequency order, tiles (soft and hard), all overlap modes, windowing, all subband
    /// selections (DC only, no highpass, no flexbits, trimmed flexbits), per tile / band / component quantization with
    /// several QP sets per tile, interleaved and separate (planar) alpha, internal 4:2:0 / 4:2:2 / 4:4:4 / Y only / YUVK /
    /// n-component formats. Samples come back in the dense layout: UInt8 / UInt16 for unsigned integer formats (BGR orders
    /// as Rgb; 5 / 6 bit packed channels stretched to UInt8 0..255, 10 bit ones as UInt16 0..1023), Float32 for fixed point (s2.13, s7.24)
    /// and float formats, Float16 for half floats, Float32 Rgb for RGBE, UInt8 0 / 255 Gray for BlackWhite, CMYK as Rgb (or
    /// stored, <see cref="JpegXrDecodeOptions.ConvertToRgb"/>), n-channel and YCC interchange formats as
    /// <see cref="ColorModel.Other"/> (YCC chroma replicated to the full size). Premultiplied alpha is undone. Metadata:
    /// resolution, ICC profile, XMP, EXIF and GPS directories, orientation (SPATIAL_XFRM_PRIMARY).</para>
    /// </summary>
    public static class JpegXr
    {
        /// <summary>Whether the data starts like a JPEG XR file (II 0xBC) or a bare JPEG XR codestream ("WMPHOTO").</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header) => JxrContainer.IsMatch(header) || JxrContainer.IsCodestream(header);

        /// <summary>Reads header level information without decoding pixel data (what <see cref="Decode(ReadOnlySpan{byte}, JpegXrDecodeOptions)"/> returns).</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => GetInfo(data, null);

        /// <inheritdoc cref="GetInfo(ReadOnlySpan{byte})"/>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data, JpegXrDecodeOptions? options)
        {
            options ??= JpegXrDecodeOptions.Default;
            var f = JxrFile.Open(data, true);
            var (ch, type, model, bits) = f.Plan(options);
            var info = new ImageInfo(ImageFormat.JpegXr, f.Cs.Ih.Width, f.Cs.Ih.Height, ch, type, model)
            {
                BitsPerSample = bits,
                DpiX = f.DpiX,
                DpiY = f.DpiY,
                HasIccProfile = f.Container?.Icc != null,
                Orientation = f.Orientation,
            };
            return options.ApplyOrientation ? ExifTags.Upright(info) : info;
        }

        /// <summary>Decodes a JPEG XR file held in memory (see the class summary for what is supported).</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, JpegXrDecodeOptions? options = null)
        {
            options ??= JpegXrDecodeOptions.Default;
            var f = JxrFile.Open(data, true);
            if ((long)f.Cs.Ih.Width * f.Cs.Ih.Height > options.MaxPixels)
                throw new ImageNotSupportedException($"JPEG XR: {f.Cs.Ih.Width} x {f.Cs.Ih.Height} exceeds MaxPixels ({options.MaxPixels})");
            var img = f.Decode(data, options);
            return options.ApplyOrientation && f.Orientation > 1 ? ImageOps.ApplyOrientation(img) : img;
        }

        public static RawImage Decode(Stream stream, JpegXrDecodeOptions? options = null) => ImageCodec.WithData(stream, d => Decode(d, options));

        /// <summary>Encodes an image (lossless by default) with its ICC profile, XMP and resolution.</summary>
        public static byte[] Encode(RawImage image, JpegXrEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            return JxrEncoder.Encode(image, options ?? new JpegXrEncodeOptions());
        }

        public static void Encode(RawImage image, Stream output, JpegXrEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(output);
            output.Write(Encode(image, options));
        }
    }
}
