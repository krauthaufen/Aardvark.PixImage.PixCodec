using System;
using System.Runtime.InteropServices;

namespace Aardvark.ImageCodecs
{
    /// <summary>Element type of a single channel sample. The numeric values are part of the test reference format.</summary>
    public enum SampleType : byte
    {
        UInt8 = 0,
        UInt16 = 1,
        UInt32 = 2,
        Int8 = 3,
        Int16 = 4,
        Int32 = 5,
        /// <summary>IEEE half float, stored as <see cref="Half"/>[].</summary>
        Float16 = 6,
        Float32 = 7,
        Float64 = 8,
        UInt64 = 9,
        Int64 = 10,
    }

    /// <summary>Meaning of the interleaved channels. Channel order is always as named (R, G, B, A).</summary>
    public enum ColorModel : byte
    {
        Gray = 0,
        GrayAlpha = 1,
        Rgb = 2,
        Rgba = 3,
        /// <summary>Arbitrary samples per pixel without a color interpretation (e.g. multispectral TIFF).</summary>
        Other = 4,
    }

    public enum ImageFormat : byte
    {
        Unknown = 0,
        Png,
        Jpeg,
        Tiff,
        Gif,
        Exr,
        Webp,
        Jp2,
        Bmp,
        Tga,
        Dds,
        Hdr,
        Pnm,
        Pfm,
        Qoi,
        Ico,
        Ktx,
        /// <summary>ZSoft PCX.</summary>
        Pcx,
        /// <summary>DCX: multi-page PCX.</summary>
        Dcx,
        /// <summary>SGI image (.sgi, .rgb, .rgba, .bw, .int, .inta).</summary>
        Sgi,
        /// <summary>Sun Raster (.ras, .sun).</summary>
        SunRaster,
        /// <summary>X BitMap (C source).</summary>
        Xbm,
        /// <summary>X PixMap (C source).</summary>
        Xpm,
        /// <summary>Wireless Bitmap (type 0).</summary>
        Wbmp,
        /// <summary>NRRD (.nrrd, detached .nhdr).</summary>
        Nrrd,
        /// <summary>NIfTI-1 / NIfTI-2 (.nii, .nii.gz, .hdr + .img).</summary>
        Nifti,
        /// <summary>MetaImage (.mha, .mhd with detached data).</summary>
        MetaImage,
        /// <summary>FITS (.fits, .fit, .fts).</summary>
        Fits,
        Icns,
        Ani,
        Astc,
        Pvr,
        /// <summary>SMPTE 268M Digital Moving-Picture Exchange.</summary>
        Dpx,
        /// <summary>Kodak Cineon.</summary>
        Cineon,
        JpegLs,
        Jxl,
        /// <summary>Adobe Digital Negative: a TIFF file with DNGVersion in its first directory (raw camera data).</summary>
        Dng,
        /// <summary>JPEG XR (HD Photo; .jxr, .wdp, .hdp).</summary>
        JpegXr,
        /// <summary>AV1 Image File Format (HEIF with AV1 images and sequences).</summary>
        Avif,
        /// <summary>DICOM medical images (.dcm, also files without extension and DICOMDIR media).</summary>
        Dicom,
        /// <summary>ENVI raster: a text header (.hdr) and the raw samples in a file next to it.</summary>
        Envi,
    }

    /// <summary>
    /// How the sample values relate to light intensity (the transfer function or "gamma" of the colour channels; alpha is
    /// always linear).
    /// </summary>
    public enum TransferFunction : byte
    {
        /// <summary>The file does not say and the format implies nothing (e.g. a DDS texture with a legacy header).</summary>
        Unknown = 0,
        /// <summary>The sRGB curve (IEC 61966-2-1): what 8 bit photos, web images and _SRGB texture formats hold.</summary>
        Srgb,
        /// <summary>Proportional to light: OpenEXR, Radiance HDR, PFM, float textures, data such as normal maps.</summary>
        Linear,
        /// <summary>A pure power law, value = light^g with g in <see cref="ImageMetadata.Gamma"/> (PNG gAMA, e.g. 0.45455 for "gamma 2.2").</summary>
        Gamma,
        /// <summary>SMPTE ST 2084 perceptual quantizer (HDR10), from a PNG cICP chunk.</summary>
        Pq,
        /// <summary>Hybrid log-gamma (ARIB STD-B67), from a PNG cICP chunk.</summary>
        Hlg,
    }

    /// <summary>Optional, format independent metadata. Decoders fill in what they find, encoders write what they can.</summary>
    public sealed class ImageMetadata
    {
        /// <summary>Raw ICC profile, if embedded.</summary>
        public byte[]? IccProfile;
        /// <summary>Raw EXIF blob (starting at the TIFF header), if embedded.</summary>
        public byte[]? Exif;
        /// <summary>
        /// Raw XMP packet (UTF-8), if embedded (JPEG, PNG, TIFF, WebP, JPEG 2000); written back by the JPEG, PNG, TIFF and WebP
        /// encoders. Parse it with <see cref="XmpData.Parse"/>, or read all metadata of a file with ImageCodec.ReadMetadata.
        /// </summary>
        public byte[]? Xmp;
        /// <summary>Horizontal / vertical resolution in pixels per inch, 0 if unknown.</summary>
        public double DpiX, DpiY;
        /// <summary>File gamma (PNG gAMA), 0 if unknown.</summary>
        public double Gamma;
        /// <summary>
        /// The transfer function of the colour channels as the file declares it or the format implies it (see
        /// <see cref="ImageInfo.Transfer"/>). Encoders that can record it do (PNG sRGB / gAMA, QOI, KTX, DDS).
        /// </summary>
        public TransferFunction Transfer;
        /// <summary>
        /// EXIF/TIFF orientation 1..8, 0 if unknown. Decoders turn the image upright by default and then report 1 (and set the
        /// tag in <see cref="Exif"/> to 1 too); encoders write this value, also into a copied <see cref="Exif"/> blob.
        /// </summary>
        public int Orientation;
        /// <summary>
        /// Physical description of the sample grid (spacing, origin, orientation, value scaling, header entries) for images from
        /// scientific formats (NRRD, NIfTI, MetaImage, FITS); null otherwise.
        /// </summary>
        public PhysicalMetadata? Physical;
        /// <summary>Placement of the grid in world coordinates (GeoTIFF, ENVI, NITF), null when the file has none.</summary>
        public GeoReference? Geo;
        /// <summary>
        /// The file ended early and was decoded as far as its data goes (only when the format's decode options allow
        /// that, such as <see cref="JpegDecodeOptions.AllowTruncated"/>): part of the image is missing.
        /// </summary>
        public bool Truncated;
    }

    /// <summary>
    /// Cheap header level information about an encoded image. Width, Height, Channels, SampleType, Color and DpiX / DpiY
    /// describe what Decode returns with default options, i.e. after the orientation has been applied; the remaining
    /// properties describe the file.
    /// </summary>
    public readonly record struct ImageInfo(
        ImageFormat Format, int Width, int Height, int Channels, SampleType SampleType, ColorModel Color, int FrameCount = 1)
    {
        /// <summary>
        /// Bits per sample as stored in the file (e.g. 1, 4, 12, 16), which can be less than the SampleType holds.
        /// For palette images the bits of one palette entry channel (8). 0 if the codec does not report it.
        /// </summary>
        public int BitsPerSample { get; init; }

        /// <summary>
        /// EXIF/TIFF orientation 1..8 as stored in the file (1 = top-left, i.e. no transformation needed), 0 if the file does
        /// not say. Decoders apply it by default (see <see cref="OrientationApplied"/>).
        /// </summary>
        public int Orientation { get; init; }

        /// <summary>
        /// True when Decode rotates / mirrors the stored pixels according to an <see cref="Orientation"/> of 2..8 (the default of
        /// every decoder), so that Width / Height / DpiX / DpiY are those of the upright image. False when there is nothing to
        /// do (orientation 0 or 1) or the orientation was explicitly left alone through decode options.
        /// </summary>
        public bool OrientationApplied { get; init; }

        /// <summary>Horizontal / vertical resolution in pixels per inch, 0 if the file does not say.</summary>
        public double DpiX { get; init; }
        /// <inheritdoc cref="DpiX"/>
        public double DpiY { get; init; }

        /// <summary>Whether the file embeds an ICC colour profile.</summary>
        public bool HasIccProfile { get; init; }

        /// <summary>
        /// The transfer function ("gamma") of the decoded colour values: what the file declares (PNG sRGB / gAMA / cICP, JPEG 2000
        /// colour space, _SRGB texture formats, KTX data format descriptor, QOI header), else what the format implies (Linear for
        /// OpenEXR, Radiance HDR, PFM and float samples; Srgb for JPEG, WebP, GIF, BMP, TGA, PNM, ICO, PCX, SGI, Sun Raster, XBM,
        /// XPM, WBMP and integer TIFF / PNG
        /// without colour chunks). Unknown when neither says. An ICC profile (<see cref="HasIccProfile"/>) overrides it.
        /// Decoders never convert: use <see cref="ImageOps.SrgbToLinear"/> / <see cref="ImageOps.LinearToSrgb"/>.
        /// </summary>
        public TransferFunction Transfer
        {
            get => transfer != TransferFunction.Unknown ? transfer : ColorTags.Implied(Format, SampleType, Color);
            init => transfer = value;
        }
        readonly TransferFunction transfer;

        /// <summary>Whether the file is an animation (GIF, APNG, WebP, ANI) rather than a still image or a set of pages.</summary>
        public bool IsAnimated { get; init; }

        /// <summary>
        /// Number of stored mip levels of the image (DDS mip chains, OpenEXR mip maps), 1 when there is only the image itself.
        /// See <see cref="ImageCodec.DecodeMipmaps(ReadOnlySpan{byte})"/>.
        /// </summary>
        public int MipLevels { get; init; } = 1;

        /// <summary>
        /// Number of z slices of a volume (NRRD, NIfTI, MetaImage, FITS cubes, DDS / KTX volume textures), 1 for 2D images. Decode
        /// returns the first slice; the format's DecodeVolume (or <see cref="ImageCodec.DecodeVolume(string, VolumeDecodeOptions?)"/>) all of them.
        /// For scientific formats <see cref="FrameCount"/> is the product of the axes beyond the third (time, ...).
        /// </summary>
        public int Depth { get; init; } = 1;

        /// <summary>
        /// The file's descriptive metadata (EXIF, XMP, IPTC; camera, exposure, dates, GPS, drone attitude, description), filled
        /// by <see cref="ImageCodec.GetInfo(string)"/> and its overloads without decoding pixels; null from the format specific
        /// GetInfo methods. Not part of equality.
        /// </summary>
        public PhotoMetadata? Metadata { get; init; }

        /// <summary>
        /// Whether the file carries an HDR gain map (JPEG: Ultra HDR XMP, ISO 21496-1 APP2 or an MPF gain map image; JPEG XL:
        /// a jhgm box). Decode returns the baseline; Jpeg.DecodeHdr / Jxl.DecodeHdr the alternate rendition. Not part of equality.
        /// </summary>
        public bool HasGainMap { get; init; }

        /// <summary>
        /// The Multi-Picture Format index of a JPEG (CIPA DC-007: MPO stereo / multi-view files, gain map and large thumbnail
        /// images), null for other files. For Extended MP files (.MPO) <see cref="FrameCount"/> is the number of views.
        /// Not part of equality.
        /// </summary>
        public MpfInfo? MultiPicture { get; init; }

        public bool Equals(ImageInfo o)
            => Format == o.Format && Width == o.Width && Height == o.Height && Channels == o.Channels && SampleType == o.SampleType
               && Color == o.Color && FrameCount == o.FrameCount && BitsPerSample == o.BitsPerSample && Orientation == o.Orientation
               && OrientationApplied == o.OrientationApplied && DpiX.Equals(o.DpiX) && DpiY.Equals(o.DpiY) && HasIccProfile == o.HasIccProfile
               && IsAnimated == o.IsAnimated && MipLevels == o.MipLevels && Transfer == o.Transfer && Depth == o.Depth;

        public override int GetHashCode()
            => HashCode.Combine(HashCode.Combine(Format, Width, Height, Channels, SampleType, Color, FrameCount),
                HashCode.Combine(BitsPerSample, Orientation, OrientationApplied, DpiX, DpiY, HasIccProfile, IsAnimated, HashCode.Combine(MipLevels, Transfer, Depth)));

        /// <summary>Width / Height of the pixels as stored in the file, before the orientation is applied (swapped for 5..8).</summary>
        public int StoredWidth => OrientationApplied && Orientation >= 5 ? Height : Width;
        /// <inheritdoc cref="StoredWidth"/>
        public int StoredHeight => OrientationApplied && Orientation >= 5 ? Width : Height;
    }

    /// <summary>
    /// A decoded image: tightly packed, row-major (top row first), channel-interleaved samples in a typed array.
    /// <see cref="Data"/> has exactly Width * Height * Channels elements and its element type matches <see cref="SampleType"/>
    /// (byte[], ushort[], uint[], ulong[], sbyte[], short[], int[], long[], Half[], float[], double[]).
    /// Alpha is never premultiplied.
    /// </summary>
    public sealed class RawImage
    {
        public int Width { get; }
        public int Height { get; }
        public int Channels { get; }
        public SampleType SampleType { get; }
        public ColorModel Color { get; }
        public Array Data { get; }
        public ImageMetadata? Metadata { get; set; }

        public RawImage(int width, int height, ColorModel color, Array data, int channels = 0, ImageMetadata? metadata = null)
        {
            if (width <= 0 || height <= 0) throw new ArgumentException("image dimensions must be positive");
            if (channels == 0) channels = ChannelCount(color);
            if (color != ColorModel.Other && channels != ChannelCount(color))
                throw new ArgumentException($"{color} requires {ChannelCount(color)} channels, got {channels}");
            if (channels <= 0) throw new ArgumentException("channel count must be positive");
            if (data.LongLength != (long)width * height * channels)
                throw new ArgumentException($"data length {data.LongLength} does not match {width}x{height}x{channels}");
            Width = width; Height = height; Channels = channels; Color = color; Data = data;
            SampleType = SampleTypeOf(data.GetType().GetElementType()!);
            Metadata = metadata;
        }

        /// <summary>Allocates a zeroed image.</summary>
        public static RawImage Create(int width, int height, ColorModel color, SampleType type, int channels = 0)
        {
            if (channels == 0) channels = ChannelCount(color);
            return new RawImage(width, height, color, CreateArray(type, checked(width * height * channels)), channels);
        }

        public bool HasAlpha => Color == ColorModel.GrayAlpha || Color == ColorModel.Rgba;

        /// <summary>The typed sample array. Throws if T does not match <see cref="SampleType"/>.</summary>
        public T[] GetData<T>() where T : unmanaged
            => typeof(T) == Data.GetType().GetElementType() // exact: `as byte[]` would also accept sbyte[]
                ? (T[])Data
                : throw new InvalidOperationException($"image holds {SampleType} samples, not {typeof(T).Name}");

        /// <summary>The samples reinterpreted as raw bytes in machine byte order.</summary>
        public Span<byte> GetBytes() => Data switch
        {
            byte[] a => a,
            sbyte[] a => MemoryMarshal.AsBytes<sbyte>(a),
            ushort[] a => MemoryMarshal.AsBytes<ushort>(a),
            short[] a => MemoryMarshal.AsBytes<short>(a),
            uint[] a => MemoryMarshal.AsBytes<uint>(a),
            int[] a => MemoryMarshal.AsBytes<int>(a),
            Half[] a => MemoryMarshal.AsBytes<Half>(a),
            float[] a => MemoryMarshal.AsBytes<float>(a),
            double[] a => MemoryMarshal.AsBytes<double>(a),
            ulong[] a => MemoryMarshal.AsBytes<ulong>(a),
            long[] a => MemoryMarshal.AsBytes<long>(a),
            _ => throw new InvalidOperationException(),
        };

        public static int ChannelCount(ColorModel color) => color switch
        {
            ColorModel.Gray => 1,
            ColorModel.GrayAlpha => 2,
            ColorModel.Rgb => 3,
            ColorModel.Rgba => 4,
            _ => 0,
        };

        public static ColorModel DefaultColorModel(int channels) => channels switch
        {
            1 => ColorModel.Gray,
            2 => ColorModel.GrayAlpha,
            3 => ColorModel.Rgb,
            4 => ColorModel.Rgba,
            _ => ColorModel.Other,
        };

        public static int BytesPerSample(SampleType type) => type switch
        {
            SampleType.UInt8 or SampleType.Int8 => 1,
            SampleType.UInt16 or SampleType.Int16 or SampleType.Float16 => 2,
            SampleType.UInt32 or SampleType.Int32 or SampleType.Float32 => 4,
            SampleType.Float64 or SampleType.UInt64 or SampleType.Int64 => 8,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

        public static Array CreateArray(SampleType type, int length) => type switch
        {
            SampleType.UInt8 => new byte[length],
            SampleType.Int8 => new sbyte[length],
            SampleType.UInt16 => new ushort[length],
            SampleType.Int16 => new short[length],
            SampleType.UInt32 => new uint[length],
            SampleType.Int32 => new int[length],
            SampleType.Float16 => new Half[length],
            SampleType.Float32 => new float[length],
            SampleType.Float64 => new double[length],
            SampleType.UInt64 => new ulong[length],
            SampleType.Int64 => new long[length],
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

        public static SampleType SampleTypeOf(Type t)
        {
            if (t == typeof(byte)) return SampleType.UInt8;
            if (t == typeof(sbyte)) return SampleType.Int8;
            if (t == typeof(ushort)) return SampleType.UInt16;
            if (t == typeof(short)) return SampleType.Int16;
            if (t == typeof(uint)) return SampleType.UInt32;
            if (t == typeof(int)) return SampleType.Int32;
            if (t == typeof(Half)) return SampleType.Float16;
            if (t == typeof(float)) return SampleType.Float32;
            if (t == typeof(double)) return SampleType.Float64;
            if (t == typeof(ulong)) return SampleType.UInt64;
            if (t == typeof(long)) return SampleType.Int64;
            throw new ArgumentException($"unsupported sample element type {t}");
        }
    }

    /// <summary>Base of the exceptions the codecs throw for the data they are given (<see cref="ImageFormatException"/>, <see cref="ImageNotSupportedException"/>).</summary>
    public abstract class ImageCodecException : Exception
    {
        protected ImageCodecException(string message) : base(message) { }
        protected ImageCodecException(string message, Exception? inner) : base(message, inner) { }
    }

    /// <summary>The encoded data is malformed or truncated.</summary>
    public class ImageFormatException : ImageCodecException
    {
        public ImageFormatException(string message) : base(message) { }
        public ImageFormatException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// The encoded data is valid but uses a feature this library does not implement, or exceeds a limit set in the options
    /// (MaxPixels).
    /// </summary>
    public class ImageNotSupportedException : ImageCodecException
    {
        public ImageNotSupportedException(string message) : base(message) { }
    }
}
