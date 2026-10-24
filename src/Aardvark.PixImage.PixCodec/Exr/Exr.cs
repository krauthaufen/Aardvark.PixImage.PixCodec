using System;
using System.Collections.Generic;
using System.IO;

namespace Aardvark.ImageCodecs
{
    public sealed class ExrDecodeOptions
    {
        /// <summary>Convert half float channels to float32 (default). If false, all-half images are returned as Float16.</summary>
        public bool HalfAsFloat { get; set; } = true;

        /// <summary>Which part of a multi-part file to decode (default: the first).</summary>
        public int PartIndex { get; set; }

        /// <summary>
        /// Layer to decode with <see cref="Exr.Decode(ReadOnlySpan{byte}, ExrDecodeOptions)"/>, e.g. "diffuse" for the channels "diffuse.R", "diffuse.G", ...
        /// ("" is the root layer). Null picks a default, see <see cref="Exr.Decode(ReadOnlySpan{byte}, ExrDecodeOptions)"/>.
        /// </summary>
        public string? Layer { get; set; }

        /// <summary>Decode chunks on multiple threads when the image is large enough (default true).</summary>
        public bool Parallel { get; set; } = true;

        /// <summary>
        /// Resolution level of a tiled MIPMAP / RIPMAP part to decode (default 0,0 = full resolution). MIPMAP parts only
        /// have levels with LevelX == LevelY; see <see cref="ExrHeader.LevelCountX"/>, <see cref="ExrHeader.GetLevelSize"/>.
        /// </summary>
        public int LevelX { get; set; }
        public int LevelY { get; set; }

        /// <summary>Sets <see cref="LevelX"/> and <see cref="LevelY"/> to the same value (the usual case, mip level n).</summary>
        public int Level { set { LevelX = value; LevelY = value; } }

        internal ExrDecodeOptions Clone() => (ExrDecodeOptions)MemberwiseClone();

        /// <summary>
        /// How <see cref="Exr.Decode(ReadOnlySpan{byte}, ExrDecodeOptions)"/> flattens deep parts, see <see cref="ExrDeepImage.Flatten"/>: sort the samples of a
        /// pixel by depth (default true) and split / merge overlapping volume samples (default true). With both false
        /// the samples are composited in file order, which is what the reference library's compositor does.
        /// </summary>
        public bool DeepSortByDepth { get; set; } = true;
        public bool DeepTidy { get; set; } = true;

        /// <summary>
        /// Which window the decoded image covers when <see cref="Region"/> is null: the data window (default: exactly
        /// the stored pixels) or the display window (stored pixels outside it are cut off, the rest of it is 0, i.e.
        /// black and fully transparent).
        /// </summary>
        public ExrWindow Window { get; set; } = ExrWindow.Data;

        /// <summary>
        /// Decode only this rectangle, given in the pixel coordinates of the file (those of the data window; for mip /
        /// rip levels the level's own pixel grid, which starts at the data window's corner). The image has exactly
        /// the size of the rectangle; parts of it outside the data window are 0. Only the scan line blocks or tiles
        /// that intersect it are read and decompressed. Luminance/chroma images and subsampled channels are decoded
        /// completely and then cut, because their reconstruction reaches beyond the rectangle.
        /// </summary>
        public ExrBox2i? Region { get; set; }
    }

    public sealed class ExrEncodeOptions
    {
        /// <summary>Compression (default Zip).</summary>
        public ExrCompression Compression { get; set; } = ExrCompression.Zip;

        /// <summary>
        /// Pixel type to store. Null (default): Float32 and Float16 images are stored as HALF (the OpenEXR convention),
        /// UInt32 images as UINT. Float32 / Float16 images can be stored as Half or Float; UInt32 only as UInt.
        /// When float32 samples are stored as HALF they are rounded to nearest even; finite values beyond the half
        /// range are clamped to +-65504, infinities and NaNs are kept.
        /// </summary>
        public ExrPixelType? PixelType { get; set; }

        /// <summary>
        /// Channel names, one per image channel in image order. Required for <see cref="ColorModel.Other"/>; for the
        /// other color models it overrides the default names (Y / Y,A / R,G,B / R,G,B,A).
        /// </summary>
        public IReadOnlyList<string>? ChannelNames { get; set; }

        /// <summary>Order in which the scan line blocks are written (default increasing Y). RandomY is written as increasing.</summary>
        public ExrLineOrder LineOrder { get; set; } = ExrLineOrder.IncreasingY;

        /// <summary>zlib level 0..9 for Zip, Zips and Pxr24.</summary>
        public int ZipLevel { get; set; } = 6;

        /// <summary>Position of the top left pixel in the data window (default 0,0).</summary>
        public int DataWindowX { get; set; }
        public int DataWindowY { get; set; }

        /// <summary>Display window; null (default) makes it (0,0)-(width-1,height-1).</summary>
        public ExrBox2i? DisplayWindow { get; set; }

        /// <summary>
        /// Additional header attributes. "displayWindow", "pixelAspectRatio", "screenWindowCenter" and
        /// "screenWindowWidth" replace the defaults; the structural attributes (channels, compression, dataWindow,
        /// lineOrder, tiles, type, name, chunkCount, version, dwaCompressionLevel) cannot be supplied.
        /// To carry the descriptive attributes of a decoded file over (pixel aspect ratio, chromaticities, comments,
        /// custom attributes ...), add <see cref="ExrHeader.GetDescriptiveAttributes"/> of its header here; RawImage
        /// itself has no place for them.
        /// </summary>
        public IList<ExrAttribute> Attributes { get; private set; } = new List<ExrAttribute>();

        /// <summary>Compress chunks on multiple threads when the image is large enough (default true).</summary>
        public bool Parallel { get; set; } = true;

        /// <summary>
        /// Tile size; both 0 (default) writes a scan line part, both positive a tiled part. For tiled parts
        /// <see cref="LineOrder"/> DecreasingY writes the tile rows bottom up and RandomY is recorded as such (tiles are
        /// then written in increasing order, which RandomY permits).
        /// </summary>
        public int TileWidth { get; set; }
        public int TileHeight { get; set; }

        /// <summary>
        /// Resolution levels of a tiled part: OneLevel (default), MipMap or RipMap. The lower levels are generated with a
        /// 2x2 (or 2x1 / 1x2) box filter; UINT channels take the top left sample instead. See <see cref="LevelRoundUp"/>.
        /// </summary>
        public ExrLevelMode LevelMode { get; set; } = ExrLevelMode.OneLevel;

        /// <summary>
        /// Level sizes: false (default) rounds down (ROUND_DOWN: 5 -> 2 -> 1), true rounds up (ROUND_UP: 5 -> 3 -> 2 -> 1;
        /// the last pixel of an odd sized level then covers one sample instead of two in that direction).
        /// </summary>
        public bool LevelRoundUp { get; set; }

        /// <summary>
        /// Filter for generated levels: Box (default, each pixel is the mean of the 2x2 samples it covers) or Triangle
        /// (separable 1-3-3-1 weights over 4x4 samples: smoother, less aliasing, about four times the work).
        /// UINT channels are never filtered.
        /// </summary>
        public ExrLevelFilter LevelFilter { get; set; } = ExrLevelFilter.Box;

        /// <summary>
        /// Store an Rgb / Rgba image as luminance Y plus 2x2 subsampled chroma RY, BY (and A), like the reference
        /// library's WRITE_YC / WRITE_YCA modes: half floats, luma weights from the "chromaticities" attribute in
        /// <see cref="Attributes"/> (Rec. 709 if absent), chroma low-pass filtered before subsampling. Lossy for chroma;
        /// negative or non finite colours become 0. Needs even width, height and data window position; scan line only.
        /// </summary>
        public bool LuminanceChroma { get; set; }

        /// <summary>
        /// Mantissa bits kept for Y and for RY / BY in luminance/chroma output. The defaults 7 and 5 are those of the
        /// reference library (it rounds to make the channels compress better); 10 keeps full half precision.
        /// </summary>
        public int LuminanceRoundBits { get; set; } = 7;
        public int ChromaRoundBits { get; set; } = 5;

        /// <summary>
        /// Quality of DWAA / DWAB (the "dwaCompressionLevel" attribute, default 45): the allowed error of the DCT
        /// coefficients grows with this value; 0 quantizes nothing, larger values give smaller files.
        /// </summary>
        public float DwaCompressionLevel { get; set; } = 45f;

        internal ExrEncodeOptions Clone()
        {
            var c = (ExrEncodeOptions)MemberwiseClone();
            c.Attributes = new List<ExrAttribute>(Attributes);
            return c;
        }
    }

    /// <summary>Filter for generated mip / rip levels.</summary>
    public enum ExrLevelFilter { Box, Triangle }

    /// <summary>The window a decoded image covers, see <see cref="ExrDecodeOptions.Window"/>.</summary>
    public enum ExrWindow { Data, Display }

    /// <summary>One part for <see cref="Exr.EncodeParts"/>.</summary>
    public sealed class ExrPart
    {
        /// <summary>Part name; required and unique in multi-part files.</summary>
        public string? Name { get; }
        public int Width { get; }
        public int Height { get; }
        public IReadOnlyList<ExrChannelData> Channels { get; }
        public ExrEncodeOptions Options { get; }

        /// <summary>The samples of a deep part, level 0 (then <see cref="Channels"/> is empty).</summary>
        public ExrDeepImage? Deep { get; }
        /// <summary>All levels of a deep part in offset table order (one entry for ordinary deep parts).</summary>
        public IReadOnlyList<ExrDeepImage>? DeepLevels { get; }

        public ExrPart(string? name, int width, int height, IReadOnlyList<ExrChannelData> channels, ExrEncodeOptions? options = null)
        {
            Name = name; Width = width; Height = height;
            Channels = channels ?? throw new ArgumentNullException(nameof(channels));
            Options = options ?? new ExrEncodeOptions();
        }

        /// <summary>
        /// A deep part: deep scan line, or deep tiled when the options have a tile size. Deep data can be stored with
        /// None, Rle or Zips compression (the default Zip is taken as Zips) and has a single resolution level.
        /// </summary>
        public ExrPart(string? name, ExrDeepImage deep, ExrEncodeOptions? options = null)
        {
            Deep = deep ?? throw new ArgumentNullException(nameof(deep));
            DeepLevels = new[] { deep };
            Name = name; Width = deep.Width; Height = deep.Height;
            Channels = Array.Empty<ExrChannelData>();
            Options = options ?? new ExrEncodeOptions();
        }

        /// <summary>
        /// A deep tiled part with several resolution levels. There is no defined way to reduce deep samples, so every
        /// level is supplied by the caller: for <see cref="ExrLevelMode.MipMap"/> levels 0, 1, 2 ... down to 1x1, for
        /// <see cref="ExrLevelMode.RipMap"/> all (x level, y level) combinations with the x level varying fastest;
        /// sizes follow <see cref="ExrEncodeOptions.LevelRoundUp"/> (5 -> 2 -> 1 or 5 -> 3 -> 2 -> 1).
        /// </summary>
        public ExrPart(string? name, IReadOnlyList<ExrDeepImage> deepLevels, ExrEncodeOptions options)
        {
            if (deepLevels == null || deepLevels.Count == 0 || deepLevels[0] == null) throw new ArgumentException("no deep levels given");
            Deep = deepLevels[0];
            DeepLevels = deepLevels;
            Name = name; Width = Deep.Width; Height = Deep.Height;
            Channels = Array.Empty<ExrChannelData>();
            Options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>A part from an interleaved image, with the channel naming and options of <see cref="Exr.Encode"/>.</summary>
        public static ExrPart FromImage(string? name, RawImage image, ExrEncodeOptions? options = null)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            options ??= new ExrEncodeOptions();
            if (image.SampleType != SampleType.Float32 && image.SampleType != SampleType.Float16 && image.SampleType != SampleType.UInt32)
                throw new ImageNotSupportedException($"OpenEXR cannot store {image.SampleType} samples; convert to Float32, Float16 or UInt32");
            if (image.Metadata is { DpiX: > 0 } md && !HasAttribute(options, "xDensity"))
            {
                // resolution travels as xDensity, with the pixel aspect ratio giving the vertical one
                var o2 = options.Clone();
                o2.Attributes.Add(ExrAttribute.Float("xDensity", (float)md.DpiX));
                if (md.DpiY > 0 && md.DpiY != md.DpiX && !HasAttribute(options, "pixelAspectRatio"))
                    o2.Attributes.Add(ExrAttribute.Float("pixelAspectRatio", (float)(md.DpiY / md.DpiX)));
                options = o2;
            }
            if (options.LuminanceChroma) return LuminanceChromaPart(name, image, options);
            IReadOnlyList<string>? names = options.ChannelNames;
            if (names == null)
                names = image.Color switch
                {
                    ColorModel.Gray => new[] { "Y" },
                    ColorModel.GrayAlpha => new[] { "Y", "A" },
                    ColorModel.Rgb => new[] { "R", "G", "B" },
                    ColorModel.Rgba => new[] { "R", "G", "B", "A" },
                    _ => throw new ArgumentException("ExrEncodeOptions.ChannelNames is required for ColorModel.Other"),
                };
            if (names.Count != image.Channels)
                throw new ArgumentException($"{names.Count} channel names given for an image with {image.Channels} channels");
            var chans = new ExrChannelData[image.Channels];
            for (int i = 0; i < chans.Length; i++)
                chans[i] = new ExrChannelData(names[i], image.Data, options.PixelType, i, image.Channels);
            return new ExrPart(name, image.Width, image.Height, chans, options);
        }

        static bool HasAttribute(ExrEncodeOptions o, string name)
        {
            foreach (var a in o.Attributes) if (a.Name == name) return true;
            return false;
        }

        static ExrPart LuminanceChromaPart(string? name, RawImage image, ExrEncodeOptions options)
        {
            if (image.Color != ColorModel.Rgb && image.Color != ColorModel.Rgba || image.SampleType == SampleType.UInt32)
                throw new ImageNotSupportedException("luminance/chroma EXR output needs a floating point Rgb or Rgba image");
            int w = image.Width, h = image.Height, nc = image.Channels;
            if (((w | h | options.DataWindowX | options.DataWindowY) & 1) != 0)
                throw new ImageNotSupportedException("luminance/chroma EXR output needs an even width, height and data window position");
            if (options.TileWidth > 0 || options.TileHeight > 0) throw new ImageNotSupportedException("luminance/chroma EXR output cannot be tiled");
            var bits = new ushort[w * h * nc];
            if (image.SampleType == SampleType.Float16)
                System.Runtime.InteropServices.MemoryMarshal.Cast<Half, ushort>(image.GetData<Half>()).CopyTo(bits);
            else
            {
                var f = image.GetData<float>();
                for (int i = 0; i < f.Length; i++) bits[i] = ExrHalf.FromFloat(f[i]);
            }
            ExrChromaticities? chroma = null;
            foreach (var a in options.Attributes) if (a.Name == "chromaticities") chroma = a.AsChromaticities();
            ExrYca.FromRgb(bits, nc, w, h, ExrYca.LumaWeights(chroma), options.LuminanceRoundBits, options.ChromaRoundBits, options.Parallel, out var luma, out var ry, out var by);
            Half[] AsHalf(ushort[] u) => System.Runtime.InteropServices.MemoryMarshal.Cast<ushort, Half>(u).ToArray();
            var chans = new List<ExrChannelData>
            {
                new("Y", AsHalf(luma), ExrPixelType.Half),
                new("RY", AsHalf(ry), ExrPixelType.Half) { XSampling = 2, YSampling = 2, PLinear = true },
                new("BY", AsHalf(by), ExrPixelType.Half) { XSampling = 2, YSampling = 2, PLinear = true },
            };
            if (nc == 4)
            {
                var alpha = new Half[w * h];
                var all = System.Runtime.InteropServices.MemoryMarshal.Cast<ushort, Half>(bits);
                for (int i = 0; i < alpha.Length; i++) alpha[i] = all[i * 4 + 3];
                chans.Add(new ExrChannelData("A", alpha, ExrPixelType.Half));
            }
            return new ExrPart(name, w, h, chans, options);
        }
    }

    /// <summary>A named channel for <see cref="Exr.EncodeChannels"/>: samples are read from Data[Offset + i * Stride].</summary>
    public sealed class ExrChannelData
    {
        public string Name { get; }
        /// <summary>float[], Half[] or uint[] with at least Offset + (width * height - 1) * Stride + 1 elements, row major, top row first.</summary>
        public Array Data { get; }
        public int Offset { get; }
        public int Stride { get; }
        /// <summary>Stored type: float[] / Half[] may be stored as Half or Float, uint[] only as UInt. Null: Half for float data, UInt for uint.</summary>
        public ExrPixelType? PixelType { get; }
        public bool PLinear { get; set; }
        /// <summary>
        /// Subsampling (default 1): the channel then has (width / XSampling) x (height / YSampling) samples and the part's
        /// size and data window position must be multiples of the sampling. Scan line parts only.
        /// </summary>
        public int XSampling { get; init; } = 1;
        public int YSampling { get; init; } = 1;

        public ExrChannelData(string name, Array data, ExrPixelType? pixelType = null, int offset = 0, int stride = 1)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Data = data ?? throw new ArgumentNullException(nameof(data));
            if (offset < 0 || stride < 1) throw new ArgumentException("invalid offset / stride");
            PixelType = pixelType; Offset = offset; Stride = stride;
        }
    }

    /// <summary>
    /// OpenEXR codec.
    /// <para>
    /// Decoding: version 2 scan line and tiled files with all mip / rip levels, multi-part files, deep scan line and
    /// deep tiled parts (as samples, or flattened), HALF / FLOAT / UINT channels, subsampled channels and
    /// luminance/chroma images. Compressions: None, RLE, ZIPS, ZIP, PIZ, PXR24, B44, B44A, DWAA, DWAB, ZSTD and - with
    /// the JPEG 2000 codec - HTJ2K256 / HTJ2K32; the lossy LJ2K is recognised but not decoded. Input is a span or a
    /// seekable stream (only the chunks that are needed are read, so files above 2 GB work); the result covers the
    /// data window, the display window or any rectangle (<see cref="ExrDecodeOptions.Region"/>). Every scheme
    /// decodes bit exactly like the reference library; for DWAA / DWAB that means the reference library's AVX code
    /// path (its other SIMD variants round a few samples per million differently).
    /// </para>
    /// <para>
    /// Encoding: scan line and tiled parts (optionally with generated mip / rip levels), multi-part files, deep parts
    /// (levels supplied by the caller), every compression that can be decoded, subsampled channels and
    /// luminance/chroma. A seekable output is written chunk by chunk; any other stream gets the finished file.
    /// </para>
    /// <para>
    /// Like the rest of the library this codec runs on little-endian hosts only: sample data is copied between the
    /// (little-endian) file layout and typed arrays without byte swapping.
    /// </para>
    /// </summary>
    public static class Exr
    {
        /// <summary>Returns whether the data starts with a OpenEXR signature (needs at most 16 bytes).</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header) => ExrFileStructure.IsMatch(header);

        /// <summary>
        /// Reads header level information without decoding pixel data; needs only the headers, and throws
        /// <see cref="ImageFormatException"/> if the data ends inside them. Size, channels, sample type and color model
        /// are those <see cref="Decode(ReadOnlySpan{byte}, ExrDecodeOptions)"/> returns for the first part with default options. BitsPerSample is 16 if all
        /// those channels are HALF, else 32 (FLOAT, UINT). DpiX is the "xDensity" attribute and DpiY is
        /// xDensity * pixelAspectRatio (0 without xDensity), the same values Decode puts into the image metadata.
        /// Orientation is always 0 and HasIccProfile false (OpenEXR has neither). FrameCount is the number of parts of
        /// the file (1 for ordinary files); parts are independent images, not animation frames.
        /// </summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data)
        {
            var file = ExrFileStructure.Parse(data);
            var part = file.Parts[0];
            var (idx, color, yca) = SelectDefault(part, null);
            int channels = yca ? RawImage.ChannelCount(color) : idx.Length;
            var type = part.IsDeep || yca ? SampleType.Float32 : OutputType(part, idx, true);
            int bits = 16;
            foreach (int i in idx)
                if (i >= 0 && part.Channels[i].PixelType != ExrPixelType.Half) bits = 32;
            var (dpiX, dpiY) = Density(part);
            return new ImageInfo(ImageFormat.Exr, part.Width, part.Height, channels, type, color, file.Parts.Length)
            {
                BitsPerSample = bits, DpiX = dpiX, DpiY = dpiY, MipLevels = MipLevelCount(part),
            };
        }

        /// <summary>Levels with equal x and y level of a tiled part: all mip levels, the diagonal of a rip map.</summary>
        static int MipLevelCount(ExrHeader part) => Math.Min(part.LevelCountX, part.LevelCountY);

        /// <summary>
        /// The mip levels of a part (<see cref="ExrDecodeOptions.PartIndex"/>), largest first: all levels of a mip map, the levels
        /// with equal x and y resolution of a rip map, or just the image.
        /// </summary>
        public static RawImage[] DecodeMipmaps(ReadOnlySpan<byte> data, ExrDecodeOptions? options = null)
        {
            options ??= new ExrDecodeOptions();
            var part = ExrFileStructure.Parse(data).Parts[options.PartIndex];
            var r = new RawImage[part.IsDeep ? 1 : MipLevelCount(part)];
            for (int k = 0; k < r.Length; k++)
            {
                var o = options.Clone();
                o.Level = k;
                r[k] = Decode(data, o);
            }
            return r;
        }

        /// <summary>Pixels per inch from the "xDensity" attribute (horizontal; vertical is xDensity * pixelAspectRatio), 0 if absent.</summary>
        static (double X, double Y) Density(ExrHeader part)
        {
            var a = part.FindAttribute("xDensity");
            if (a == null || a.Value.Length < 4) return (0, 0);
            float x = a.AsFloat();
            if (!(x > 0) || float.IsInfinity(x)) return (0, 0);
            return (x, (double)x * part.PixelAspectRatio);
        }

        static RawImage WithMetadata(RawImage image, ExrHeader part)
        {
            var (x, y) = Density(part);
            if (x > 0) image.Metadata = new ImageMetadata { DpiX = x, DpiY = y };
            return image;
        }

        /// <summary>Parses the headers of all parts (one for ordinary files). Does not touch pixel data.</summary>
        public static ExrHeader[] ReadHeaders(ReadOnlySpan<byte> data) => ExrFileStructure.Parse(data).Parts;

        /// <summary>Parses the headers from a stream, reading only the start of the file (the position is not restored).</summary>
        public static ExrHeader[] ReadHeaders(Stream stream) => Open(stream).ParseStructure().Parts;

        /// <summary>
        /// A random access view of a seekable stream (from its current position to its end); a stream that cannot
        /// seek is read into memory first. Only the headers, offset tables and the chunks a decode call needs are
        /// read, so files larger than 2 GB work.
        /// </summary>
        static ExrInput Open(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            return stream.CanSeek ? new ExrStreamInput(stream) : new ExrStreamInput(new MemoryStream(ImageCodec.ReadAll(stream)));
        }

        /// <summary>
        /// Decodes from a seekable stream without loading the file: headers, offset tables and only the chunks that
        /// the selected part, level and region need are read (files above 2 GB work this way). Otherwise like
        /// <see cref="Decode(ReadOnlySpan{byte}, ExrDecodeOptions)"/>.
        /// </summary>
        public static RawImage Decode(Stream stream, ExrDecodeOptions? options = null) => DecodeCore(Open(stream), options ?? new ExrDecodeOptions());

        /// <summary><see cref="DecodeChannels(ReadOnlySpan{byte}, IReadOnlyList{string}, ExrDecodeOptions)"/> from a seekable stream, reading only what is needed.</summary>
        public static RawImage DecodeChannels(Stream stream, IReadOnlyList<string> channelNames, ExrDecodeOptions? options = null)
            => DecodeChannelsCore(Open(stream), channelNames, options ?? new ExrDecodeOptions());

        /// <summary><see cref="DecodeDeep(ReadOnlySpan{byte}, IReadOnlyList{string}, ExrDecodeOptions)"/> from a seekable stream, reading only what is needed.</summary>
        public static ExrDeepImage DecodeDeep(Stream stream, IReadOnlyList<string>? channelNames = null, ExrDecodeOptions? options = null)
            => DecodeDeepCore(Open(stream), channelNames, options ?? new ExrDecodeOptions());

        /// <summary>
        /// Decodes a complete OpenEXR file held in memory.
        /// <para>
        /// Channel mapping (channels are stored alphabetically in the file, the result is always in R,G,B,A order):
        /// within the selected layer, R,G,B → Rgb (Rgba with A); otherwise Y (unless the layer also has an X, as in
        /// normals or positions) → Gray (GrayAlpha with A); otherwise a
        /// single channel → Gray; otherwise all channels of the layer in file order as <see cref="ColorModel.Other"/>.
        /// With <see cref="ExrDecodeOptions.Layer"/> == null the root layer (names without '.') is used if it has
        /// R,G,B or Y, else the first layer that has them, else all channels of the part are taken (Gray / Other).
        /// </para>
        /// <para>
        /// Luminance/chroma images (Y with 2x2 subsampled RY / BY, optional A) are converted to Rgb / Rgba exactly like
        /// the reference library's RGBA interface does: chroma reconstruction filter, luma weights from the
        /// "chromaticities" attribute (Rec. 709 if absent) and its saturation correction; the result is identical to
        /// the reference bit for bit. The raw Y, RY, BY channels remain available through <see cref="DecodeChannels(ReadOnlySpan{byte}, IReadOnlyList{string}, ExrDecodeOptions)"/>.
        /// </para>
        /// <para>
        /// Deep parts are flattened (see <see cref="ExrDeepImage.Flatten"/>) and always give Float32; use
        /// <see cref="DecodeDeep(ReadOnlySpan{byte}, IReadOnlyList{string}, ExrDecodeOptions)"/> for the samples themselves.
        /// </para>
        /// <para>
        /// Sample type: all selected channels HALF → Float32 (or Float16 if HalfAsFloat is false); any FLOAT → Float32;
        /// all UINT → UInt32; UINT mixed with HALF / FLOAT → Float32 (UINT values are converted by value, which is
        /// inexact above 2^24).
        /// </para>
        /// </summary>
        public static unsafe RawImage Decode(ReadOnlySpan<byte> data, ExrDecodeOptions? options = null)
        {
            fixed (byte* p = data) return DecodeCore(new ExrMemoryInput(p, data.Length), options ?? new ExrDecodeOptions());
        }

        static RawImage DecodeCore(ExrInput input, ExrDecodeOptions options)
        {
            var file = input.ParseStructure();
            var part = GetPart(file, options.PartIndex);
            var (idx, color, yca) = SelectDefault(part, options.Layer);
            if (part.IsDeep)
            {
                // flattening needs alpha and depth besides the requested channels
                var names = new List<string>();
                foreach (int i in idx) if (i >= 0) names.Add(part.Channels[i].Name);
                var need = new List<int>(idx.Length + 3);
                foreach (int i in idx) if (i >= 0) need.Add(i);
                foreach (var extra in new[] { "A", "Z", "ZBack" })
                {
                    int i = part.ChannelIndex(extra);
                    if (i >= 0 && !need.Contains(i)) need.Add(i);
                }
                var flat = ExrDeepDecoder.Decode(input, file, options.PartIndex, need.ToArray(), options).Flatten(names, options.DeepSortByDepth, options.DeepTidy);
                return WithMetadata(new RawImage(flat.Width, flat.Height, yca ? ColorModel.Other : color, flat.Data, flat.Channels), part);
            }
            if (yca) return WithMetadata(DecodeYca(input, file, part, idx, options), part);
            return WithMetadata(ExrDecoder.Decode(input, file, options.PartIndex, idx, color, options), part);
        }

        static RawImage DecodeYca(ExrInput data, ExrFileStructure file, ExrHeader part, int[] idx, ExrDecodeOptions options)
        {
            // idx: Y, RY, BY, A (-1 = absent)
            int w = part.Width, h = part.Height;
            for (int k = 1; k <= 2; k++)
                if (idx[k] >= 0 && (part.Channels[idx[k]].XSampling != 2 || part.Channels[idx[k]].YSampling != 2))
                    throw new ImageNotSupportedException("EXR luminance/chroma images need 2x2 subsampled RY / BY channels");
            if (part.Channels[idx[0]].XSampling != 1 || part.Channels[idx[0]].YSampling != 1
                || (idx[3] >= 0 && (part.Channels[idx[3]].XSampling != 1 || part.Channels[idx[3]].YSampling != 1)))
                throw new ImageNotSupportedException("EXR luminance/chroma images need full resolution Y and A channels");
            if (((w | h | part.DataWindow.MinX | part.DataWindow.MinY) & 1) != 0)
                throw new ImageFormatException("EXR luminance/chroma image has an odd data window");
            if (options.LevelX != 0 || options.LevelY != 0) throw new ArgumentException("EXR scan line part has no levels");
            if ((long)w * h * 4 > 0x7FFFFFC7) throw ExrDecoder.TooLarge(w, h, 4);

            var planes = new ushort[4][];
            var one = new int[1];
            for (int k = 0; k < 4; k++)
            {
                if (idx[k] < 0) continue;
                one[0] = idx[k];
                var img = ExrDecoder.Decode(data, file, options.PartIndex, one, ColorModel.Gray, new ExrDecodeOptions { HalfAsFloat = false, PartIndex = options.PartIndex, Parallel = options.Parallel });
                // the RGBA interface works on half floats whatever the stored type is
                planes[k] = img.SampleType switch
                {
                    SampleType.Float16 => System.Runtime.InteropServices.MemoryMarshal.Cast<Half, ushort>(img.GetData<Half>()).ToArray(),
                    SampleType.Float32 => Array.ConvertAll(img.GetData<float>(), ExrHalf.FromFloatIeee),
                    _ => Array.ConvertAll(img.GetData<uint>(), v => ExrHalf.FromFloatIeee(v)),
                };
            }
            var bits = ExrYca.ToRgb(planes[0], planes[1], planes[2], planes[3], w, h, ExrYca.LumaWeights(part.Chromaticities), options.Parallel);
            var color = planes[3] != null ? ColorModel.Rgba : ColorModel.Rgb;
            RawImage result;
            if (!options.HalfAsFloat)
                result = new RawImage(w, h, color, System.Runtime.InteropServices.MemoryMarshal.Cast<ushort, Half>(bits).ToArray());
            else
            {
                var f = new float[bits.Length];
                unsafe
                {
                    fixed (ushort* ps = bits)
                    fixed (float* pf = f) ExrHalf.ToFloat(ps, pf, bits.Length);
                }
                result = new RawImage(w, h, color, f);
            }
            // the chroma filters reach 13 pixels beyond a region, so the whole image is converted and then cut
            return ExrDecoder.Crop(result, part.DataWindow, ExrDecoder.OutputWindow(part, options, part.DataWindow));
        }

        /// <summary>
        /// Decodes a deep part (deep scan line or deep tiled; <see cref="ExrDecodeOptions.PartIndex"/>, LevelX / LevelY
        /// apply) with its per pixel sample counts and the samples of the given channels (null = all channels).
        /// </summary>
        public static unsafe ExrDeepImage DecodeDeep(ReadOnlySpan<byte> data, IReadOnlyList<string>? channelNames = null, ExrDecodeOptions? options = null)
        {
            fixed (byte* p = data) return DecodeDeepCore(new ExrMemoryInput(p, data.Length), channelNames, options ?? new ExrDecodeOptions());
        }

        static ExrDeepImage DecodeDeepCore(ExrInput input, IReadOnlyList<string>? channelNames, ExrDecodeOptions options)
        {
            var file = input.ParseStructure();
            var part = GetPart(file, options.PartIndex);
            int[] idx;
            if (channelNames == null)
            {
                idx = new int[part.Channels.Count];
                for (int i = 0; i < idx.Length; i++) idx[i] = i;
            }
            else idx = ChannelIndices(part, channelNames);
            return ExrDeepDecoder.Decode(input, file, options.PartIndex, idx, options);
        }

        static int[] ChannelIndices(ExrHeader part, IReadOnlyList<string> channelNames)
        {
            if (channelNames.Count == 0) throw new ArgumentException("no channels given", nameof(channelNames));
            var idx = new int[channelNames.Count];
            var used = new HashSet<int>();
            for (int i = 0; i < idx.Length; i++)
            {
                idx[i] = part.ChannelIndex(channelNames[i]);
                if (idx[i] < 0) throw new ArgumentException($"EXR part has no channel '{channelNames[i]}'", nameof(channelNames));
                if (!used.Add(idx[i])) throw new ArgumentException($"channel '{channelNames[i]}' is listed twice", nameof(channelNames));
            }
            return idx;
        }

        /// <summary>
        /// Decodes an explicit list of channels (full names such as "Z" or "diffuse.R") of the part selected by
        /// <see cref="ExrDecodeOptions.PartIndex"/> into one interleaved image, in the given order. The color model is
        /// Rgb / Rgba / Gray / GrayAlpha if the names are exactly R,G,B(,A) or Y(,A) of one layer in that order, Gray
        /// for a single channel, otherwise <see cref="ColorModel.Other"/>. Sample type rules are those of <see cref="Decode(ReadOnlySpan{byte}, ExrDecodeOptions)"/>.
        /// Subsampled channels are expanded to full resolution by sample replication.
        /// </summary>
        public static unsafe RawImage DecodeChannels(ReadOnlySpan<byte> data, IReadOnlyList<string> channelNames, ExrDecodeOptions? options = null)
        {
            fixed (byte* p = data) return DecodeChannelsCore(new ExrMemoryInput(p, data.Length), channelNames, options ?? new ExrDecodeOptions());
        }

        static RawImage DecodeChannelsCore(ExrInput input, IReadOnlyList<string> channelNames, ExrDecodeOptions options)
        {
            if (channelNames == null || channelNames.Count == 0) throw new ArgumentException("no channels given", nameof(channelNames));
            var file = input.ParseStructure();
            var part = GetPart(file, options.PartIndex);
            var idx = ChannelIndices(part, channelNames);
            return WithMetadata(ExrDecoder.Decode(input, file, options.PartIndex, idx, NaturalModel(channelNames), options), part);
        }

        /// <summary>
        /// Encodes an image as an OpenEXR file to the stream (which only needs to be writable, not seekable): a scan
        /// line file by default, a tiled one (optionally with generated mip / rip levels) when
        /// <see cref="ExrEncodeOptions.TileWidth"/> / TileHeight are set.
        /// Float32, Float16 and UInt32 images are accepted; integer 8/16 bit images throw
        /// <see cref="ImageNotSupportedException"/> (convert them to float first). Channels are named Y / Y,A / R,G,B /
        /// R,G,B,A by color model; <see cref="ColorModel.Other"/> needs <see cref="ExrEncodeOptions.ChannelNames"/>.
        /// With <see cref="ExrEncodeOptions.LuminanceChroma"/> an Rgb / Rgba image is stored as Y, RY, BY(, A).
        /// </summary>
        public static void Encode(RawImage image, Stream output, ExrEncodeOptions? options = null)
            => ExrEncoder.Encode(new[] { ExrPart.FromImage(null, image, options) }, output, false);

        /// <summary>Encodes an image as OpenEXR into a byte array.</summary>
        public static byte[] Encode(RawImage image, ExrEncodeOptions? options = null)
        {
            using var ms = new MemoryStream();
            Encode(image, ms, options);
            return ms.ToArray();
        }

        /// <summary>
        /// Encodes arbitrary named channels (AOVs, depth, ids, layers such as "diffuse.R") as one part. Every channel
        /// has width * height samples (fewer for subsampled channels); types may be mixed. Custom header attributes go
        /// into <see cref="ExrEncodeOptions.Attributes"/>. PixelType, ChannelNames and LuminanceChroma are ignored here.
        /// </summary>
        public static void EncodeChannels(int width, int height, IReadOnlyList<ExrChannelData> channels, Stream output, ExrEncodeOptions? options = null)
            => ExrEncoder.Encode(new[] { new ExrPart(null, width, height, channels, options) }, output, false);

        /// <summary>
        /// Writes a multi-part file: each part has its own channels, size, compression and scan line / tiled layout
        /// (from its <see cref="ExrPart.Options"/>). Part names must be unique; all parts must share the display window
        /// and pixel aspect ratio, as the format requires.
        /// </summary>
        public static void EncodeParts(IReadOnlyList<ExrPart> parts, Stream output) => ExrEncoder.Encode(parts, output, true);

        /// <summary>
        /// Writes a deep image as a single-part deep scan line file, or a deep tiled one when
        /// <see cref="ExrEncodeOptions.TileWidth"/> / TileHeight are set. Compression None, Rle or Zips (the default Zip
        /// is taken as Zips); samples are written in the order given, HALF channels rounded like in <see cref="Encode"/>.
        /// For deep parts next to others use <see cref="EncodeParts"/> with <see cref="ExrPart(string, ExrDeepImage, ExrEncodeOptions)"/>.
        /// </summary>
        public static void EncodeDeep(ExrDeepImage image, Stream output, ExrEncodeOptions? options = null)
            => ExrEncoder.Encode(new[] { new ExrPart(null, image, options) }, output, false);

        /// <summary>
        /// Writes a deep tiled file with several resolution levels, see
        /// <see cref="ExrPart(string, IReadOnlyList{ExrDeepImage}, ExrEncodeOptions)"/> for the order of the levels.
        /// </summary>
        public static void EncodeDeep(IReadOnlyList<ExrDeepImage> levels, Stream output, ExrEncodeOptions options)
            => ExrEncoder.Encode(new[] { new ExrPart(null, levels, options) }, output, false);

        static ExrHeader GetPart(ExrFileStructure file, int index)
        {
            if (index < 0 || index >= file.Parts.Length)
                throw new ArgumentException($"EXR file has {file.Parts.Length} part(s), part index {index} is out of range");
            return file.Parts[index];
        }

        internal static SampleType OutputType(ExrHeader part, int[] idx, bool halfAsFloat)
        {
            bool anyHalf = false, anyFloat = false, anyUInt = false;
            foreach (int i in idx)
                switch (part.Channels[i].PixelType)
                {
                    case ExrPixelType.Half: anyHalf = true; break;
                    case ExrPixelType.Float: anyFloat = true; break;
                    default: anyUInt = true; break;
                }
            if (anyUInt && !anyHalf && !anyFloat) return SampleType.UInt32;
            if (anyHalf && !anyFloat && !anyUInt && !halfAsFloat) return SampleType.Float16;
            return SampleType.Float32;
        }

        /// <summary>Tries R,G,B(,A) then Y(,A) among the channels of one layer. Luminance/chroma gives idx = Y, RY, BY, A with -1 for absent ones.</summary>
        static bool TryColor(ExrHeader part, string layer, out int[] idx, out ColorModel color, out bool yca)
        {
            int Find(string baseName) => part.ChannelIndex(layer.Length == 0 ? baseName : layer + "." + baseName);
            int r = Find("R"), g = Find("G"), b = Find("B"), a = Find("A"), y = Find("Y");
            yca = false;
            if (r >= 0 && g >= 0 && b >= 0)
            {
                idx = a >= 0 ? new[] { r, g, b, a } : new[] { r, g, b };
                color = a >= 0 ? ColorModel.Rgba : ColorModel.Rgb;
                return true;
            }
            // a Y next to an X is a vector component (normals, positions), not luminance
            if (y >= 0 && Find("X") < 0)
            {
                int ry = Find("RY"), by = Find("BY");
                if ((ry >= 0 || by >= 0) && !part.IsDeep)
                {
                    yca = true;
                    idx = new[] { y, ry, by, a };
                    color = a >= 0 ? ColorModel.Rgba : ColorModel.Rgb;
                    return true;
                }
                idx = a >= 0 ? new[] { y, a } : new[] { y };
                color = a >= 0 ? ColorModel.GrayAlpha : ColorModel.Gray;
                return true;
            }
            idx = Array.Empty<int>(); color = ColorModel.Other;
            return false;
        }

        internal static (int[] idx, ColorModel color, bool yca) SelectDefault(ExrHeader part, string? layer)
        {
            int[] idx; ColorModel color; bool yca;
            var all = new List<int>();
            if (layer != null)
            {
                if (TryColor(part, layer, out idx, out color, out yca)) return (idx, color, yca);
                for (int i = 0; i < part.Channels.Count; i++) if (part.Channels[i].Layer == layer) all.Add(i);
                if (all.Count == 0) throw new ArgumentException($"EXR part has no layer '{layer}'");
            }
            else
            {
                if (TryColor(part, "", out idx, out color, out yca)) return (idx, color, yca);
                foreach (var l in part.Layers)
                    if (l.Length > 0 && TryColor(part, l, out idx, out color, out yca)) return (idx, color, yca);
                for (int i = 0; i < part.Channels.Count; i++) all.Add(i);
            }
            return (all.ToArray(), all.Count == 1 ? ColorModel.Gray : ColorModel.Other, false);
        }

        /// <summary>Color model for an explicit channel list: R,G,B(,A) / Y,A of one layer in that order, a single channel is Gray.</summary>
        internal static ColorModel NaturalModel(IReadOnlyList<string> names)
        {
            if (names.Count == 1) return ColorModel.Gray;
            string? layer = null;
            var bases = new string[names.Count];
            for (int i = 0; i < bases.Length; i++)
            {
                int dot = names[i].LastIndexOf('.');
                string l = dot < 0 ? "" : names[i].Substring(0, dot);
                if (layer != null && l != layer) return ColorModel.Other;
                layer = l;
                bases[i] = names[i].Substring(dot + 1);
            }
            return string.Join(",", bases) switch
            {
                "R,G,B" => ColorModel.Rgb,
                "R,G,B,A" => ColorModel.Rgba,
                "Y,A" => ColorModel.GrayAlpha,
                _ => ColorModel.Other,
            };
        }
    }
}

namespace Aardvark.ImageCodecs
{
    /// <summary>OpenEXR compression schemes (values as stored in the file).</summary>
    public enum ExrCompression
    {
        None, Rle, Zips, Zip, Piz, Pxr24, B44, B44A, Dwaa, Dwab,
        /// <summary>Lossless High Throughput JPEG 2000, 256 scan lines per chunk (OpenEXR 3.4).</summary>
        Htj2k256 = 10,
        /// <summary>Lossless High Throughput JPEG 2000, 32 scan lines per chunk (OpenEXR 3.4).</summary>
        Htj2k32 = 11,
        /// <summary>Lossy High Throughput JPEG 2000 (OpenEXR 3.5). Recognised in headers; not decoded or encoded.</summary>
        Lj2k = 12,
        /// <summary>Zstandard with per channel delta and byte plane filters (OpenEXR 3.5).</summary>
        Zstd = 13,
    }
}
