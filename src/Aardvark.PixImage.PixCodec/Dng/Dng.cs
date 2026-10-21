using System;
using System.IO;
using System.Linq;

namespace Aardvark.ImageCodecs
{
    public enum DngDemosaicAlgorithm
    {
        /// <summary>Edge directed (Menon, Andriani, Calvagno 2007) for Bayer data, colour difference interpolation for other patterns.</summary>
        Directional,
        /// <summary>Bilinear: the mean of the nearest pixels of each colour. Fast, soft, colour fringes at edges.</summary>
        Bilinear,
    }

    /// <summary>Colour space of a rendered DNG.</summary>
    public enum DngColorSpace
    {
        /// <summary>sRGB primaries and white (D65): linear values for Float32 output, sRGB encoded for UInt8 / UInt16.</summary>
        Srgb,
        /// <summary>CIE XYZ relative to D50 (the DNG profile connection space), linear.</summary>
        XyzD50,
        /// <summary>The camera's white balanced colour planes (neutral = 1, 1, 1), linear, without colour matrix.</summary>
        Camera,
    }

    /// <summary>Options for rendering a DNG (<see cref="Dng.Decode(ReadOnlySpan{byte}, DngDecodeOptions?)"/>).</summary>
    public sealed class DngDecodeOptions
    {
        /// <summary>
        /// UInt16 (default) or UInt8: <see cref="ColorSpace"/> sRGB encoded, clipped to 0..1. Float32: linear values, 1 = white,
        /// highlights above 1 kept where <see cref="ClipHighlights"/> allows.
        /// </summary>
        public SampleType OutputType { get; set; } = SampleType.UInt16;

        public DngColorSpace ColorSpace { get; set; } = DngColorSpace.Srgb;

        public DngDemosaicAlgorithm Demosaic { get; set; } = DngDemosaicAlgorithm.Directional;

        /// <summary>Scale by 2^(BaselineExposure + BaselineExposureOffset), as the file asks for (default true).</summary>
        public bool ApplyBaselineExposure { get; set; } = true;

        /// <summary>Additional exposure in EV.</summary>
        public double ExposureCompensation { get; set; }

        /// <summary>White balance as a camera neutral (one value per colour plane) instead of the as-shot one.</summary>
        public double[]? WhiteBalanceNeutral { get; set; }

        /// <summary>White balance as an x-y chromaticity instead of the as-shot one (ignored when <see cref="WhiteBalanceNeutral"/> is set).</summary>
        public (double X, double Y)? WhiteBalanceXY { get; set; }

        /// <summary>
        /// Clip the white balanced camera values where the first channel saturates (default true), so that blown highlights
        /// come out white instead of tinted. Off keeps every channel up to its own saturation (useful with Float32 output).
        /// </summary>
        public bool ClipHighlights { get; set; } = true;

        /// <summary>Apply the camera profile's hue / saturation / value maps (ProfileHueSatMapData, part of its colour calibration; default true).</summary>
        public bool ApplyHueSatMap { get; set; } = true;

        /// <summary>Apply the profile's look table (ProfileLookTableData, a default "look"; default false). Its tone curve is never applied.</summary>
        public bool ApplyLookTable { get; set; }

        /// <summary>Apply OpcodeList1..3 (default true). Unknown opcodes not marked optional make decoding fail.</summary>
        public bool ApplyOpcodes { get; set; } = true;

        /// <summary>Crop to DefaultCropOrigin / DefaultCropSize (default true); false returns the whole active area.</summary>
        public bool ApplyDefaultCrop { get; set; } = true;

        /// <summary>Resample non-square pixels by DefaultScale (default true).</summary>
        public bool ApplyDefaultScale { get; set; } = true;

        /// <summary>Turn the image upright by its Orientation tag (default true).</summary>
        public bool ApplyOrientation { get; set; } = true;

        public bool Parallel { get; set; } = true;

        /// <summary>Upper bound for the number of threads, 0 or less = no limit.</summary>
        public int MaxDegreeOfParallelism { get; set; }

        internal static readonly DngDecodeOptions Default = new();
    }

    /// <summary>Which processing stage <see cref="Dng.DecodeRaw(ReadOnlySpan{byte}, DngRawStage, bool)"/> returns.</summary>
    public enum DngRawStage
    {
        /// <summary>
        /// The samples as stored, the whole raw image (masked areas included), RowInterleaveFactor / ColumnInterleaveFactor
        /// undone: UInt8 / UInt16 / UInt32 or Float16 / Float32 as in the file. Nothing else is applied.
        /// </summary>
        Stored,
        /// <summary>
        /// Linear reference values (chapter 5) of the active area as Float32, 0 = black, 1 = white level: OpcodeList1,
        /// LinearizationTable, black levels, white level, OpcodeList2 (when opcodes are applied). Not white balanced, not
        /// demosaiced; negative values (noise around black) are kept unless an opcode clipped them.
        /// </summary>
        Linear,
    }

    /// <summary>Raw sensor data of a DNG with the parameters needed to process it.</summary>
    public sealed class DngRawImage
    {
        /// <summary>
        /// The raw samples: one channel (Gray) for CFA data, <see cref="DngInfo.SamplesPerPixel"/> channels for LinearRaw (Rgb
        /// for three, Gray for one). Stored orientation, no crop.
        /// </summary>
        public RawImage Image { get; init; } = null!;
        public DngInfo Info { get; init; } = null!;
        public DngRawStage Stage { get; init; }
        /// <summary>
        /// For <see cref="DngRawStage.Linear"/>: position of the image's top-left pixel in active area coordinates (non-zero
        /// after a TrimBounds opcode). The CFA pattern and default crop are relative to the active area's origin.
        /// </summary>
        public int OffsetX { get; init; }
        public int OffsetY { get; init; }
    }

    /// <summary>
    /// Adobe Digital Negative (DNG 1.0 to 1.7.1) reader: raw CFA and LinearRaw data in any of the DNG compressions
    /// (uncompressed, lossless JPEG, Deflate with the DNG predictors, lossy JPEG, JPEG XL), the complete parameter set
    /// (<see cref="DngInfo"/>), the processing model of the specification (linearization, black / white levels, opcode
    /// lists 1..3, white balance, demosaicing, colour calibrations interpolated by temperature, forward matrices, default
    /// crop, orientation) and the embedded previews.
    /// </summary>
    public static unsafe class Dng
    {
        /// <summary>
        /// Whether the data is a TIFF file whose first directory carries DNGVersion. Needs the bytes up to the end of that
        /// directory (for most files the first few KB).
        /// </summary>
        public static bool IsMatch(ReadOnlySpan<byte> data)
        {
            if (!TiffFile.IsMatch(data) || data.Length < 16) return false;
            fixed (byte* p = data)
            {
                try { return DngReader.IsDng(new TiffFile(new TiffMemorySource(p, data.Length))); }
                catch (ImageFormatException) { return false; }
            }
        }

        /// <summary>Whether a seekable stream (at the start of the file) holds a DNG; the position is restored.</summary>
        public static bool IsMatch(Stream seekableStream)
        {
            var src = new TiffStreamSource(seekableStream);
            try { return DngReader.IsDng(new TiffFile(src)); }
            catch (ImageFormatException) { return false; }
            catch (EndOfStreamException) { return false; }
            finally { seekableStream.Position = src.Origin; }
        }

        /// <summary>All DNG parameters (camera, raw layout, levels, CFA, colour calibrations, opcodes, previews) without decoding pixels.</summary>
        public static DngInfo ReadInfo(ReadOnlySpan<byte> data)
        {
            fixed (byte* p = data)
            {
                var src = new TiffMemorySource(p, data.Length);
                return Guard(() => DngReader.Read(new TiffFile(src), out _));
            }
        }

        public static DngInfo ReadInfo(Stream seekableStream)
        {
            var src = new TiffStreamSource(seekableStream);
            return Guard(() => DngReader.Read(new TiffFile(src), out _), seekableStream, src.Origin);
        }

        /// <summary>
        /// Header level description of the default rendering: the final (cropped, scaled) size turned upright, 3 channels
        /// (1 for monochrome files) of UInt16, BitsPerSample of the raw data, the file's orientation.
        /// </summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => Info(ReadInfo(data));

        public static ImageInfo GetInfo(Stream seekableStream) => Info(ReadInfo(seekableStream));

        static ImageInfo Info(DngInfo d)
        {
            int ch = d.ColorPlanes == 1 ? 1 : 3;
            return ExifTags.Upright(new ImageInfo(ImageFormat.Dng, d.FinalWidth, d.FinalHeight, ch, SampleType.UInt16, ch == 1 ? ColorModel.Gray : ColorModel.Rgb)
            {
                BitsPerSample = d.BitsPerSample,
                Orientation = d.Orientation,
                Transfer = TransferFunction.Srgb,
            });
        }

        /// <summary>Renders the raw image (see <see cref="DngDecodeOptions"/>). The result is upright and carries the file's EXIF / XMP.</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, DngDecodeOptions? options = null)
        {
            fixed (byte* p = data)
            {
                var src = new TiffMemorySource(p, data.Length);
                return Guard(() => DecodeCore(new TiffFile(src), options ?? DngDecodeOptions.Default));
            }
        }

        /// <summary>Renders from a seekable stream (strips / tiles are read on demand); the position is restored afterwards.</summary>
        public static RawImage Decode(Stream seekableStream, DngDecodeOptions? options = null)
        {
            var src = new TiffStreamSource(seekableStream);
            return Guard(() => DecodeCore(new TiffFile(src), options ?? DngDecodeOptions.Default), seekableStream, src.Origin);
        }

        static RawImage DecodeCore(TiffFile f, DngDecodeOptions o)
        {
            var info = DngReader.Read(f, out long raw);
            return DngRender.Render(f, info, raw, o, Metadata(f));
        }

        /// <summary>
        /// Tags of TIFF-EP that DNG allows in IFD 0 instead of the EXIF directory (exposure, aperture, ISO, dates, focal
        /// length, flash, ...): kept in the EXIF blob together with the EXIF and GPS directories.
        /// </summary>
        internal static bool IsTiffEpExif(int tag) => tag is 306 or 33434 or 33437 or 34850 or 34855 or 34856 or 34857 or 34858 or 34859
            or 36864 or 36867 or 36868 or (>= 37377 and <= 37386) or (>= 37393 and <= 37399) or (>= 37520 and <= 37522) or 41495;

        static ImageMetadata Metadata(TiffFile f)
        {
            var m = new ImageMetadata();
            try
            {
                var e = f.ReadIfd(f.FirstIfd);
                var node = TiffIfdNode.Read(f, f.FirstIfd, t => TiffExif.IsDescriptive(t) || IsTiffEpExif(t));
                if (node.Fields.Count > 0)
                {
                    var dir = node.Serialize(8, 0, false, false);
                    var blob = new byte[8 + dir.Length];
                    blob[0] = blob[1] = (byte)'I'; blob[2] = 42; blob[4] = 8;
                    dir.CopyTo(blob, 8);
                    m.Exif = blob;
                }
                var xmp = f.Bytes(e, 700);
                if (xmp != null && xmp.Length > 0) m.Xmp = xmp;
            }
            catch (ImageFormatException) { }
            return m;
        }

        /// <summary>The raw sensor data with its parameters, at the given processing stage.</summary>
        /// <param name="data">The DNG file.</param>
        /// <param name="stage">How far the raw data is processed.</param>
        /// <param name="applyOpcodes">For <see cref="DngRawStage.Linear"/>: apply OpcodeList1 and OpcodeList2.</param>
        public static DngRawImage DecodeRaw(ReadOnlySpan<byte> data, DngRawStage stage = DngRawStage.Stored, bool applyOpcodes = true)
        {
            fixed (byte* p = data)
            {
                var src = new TiffMemorySource(p, data.Length);
                return Guard(() => RawCore(new TiffFile(src), stage, applyOpcodes));
            }
        }

        public static DngRawImage DecodeRaw(Stream seekableStream, DngRawStage stage = DngRawStage.Stored, bool applyOpcodes = true)
        {
            var src = new TiffStreamSource(seekableStream);
            return Guard(() => RawCore(new TiffFile(src), stage, applyOpcodes), seekableStream, src.Origin);
        }

        static DngRawImage RawCore(TiffFile f, DngRawStage stage, bool opcodes)
        {
            var info = DngReader.Read(f, out long raw);
            var stored = DngRender.ReadStored(f, info, raw, true, 0);
            if (stage == DngRawStage.Stored)
            {
                stored.Metadata = Metadata(f);   // EXIF / XMP, for writing the image back
                return new DngRawImage { Image = stored, Info = info, Stage = stage };
            }
            var lin = DngRender.Linearize(stored, info, opcodes, new System.Threading.Tasks.ParallelOptions(), out var trim);
            int n = lin.Planes, w = lin.Width, h = lin.Height;
            var d = new float[(long)w * h * n];
            for (int pl = 0; pl < n; pl++)
            {
                var s = lin.Data[pl];
                for (long i = 0; i < s.Length; i++) d[i * n + pl] = s[i];
            }
            var color = n == 1 ? ColorModel.Gray : n == 3 ? ColorModel.Rgb : ColorModel.Other;
            return new DngRawImage { Image = new RawImage(w, h, color, d, n), Info = info, Stage = stage, OffsetX = trim.X, OffsetY = trim.Y };
        }

        // ------------------------------------------------------------------------------------------ encoding

        /// <summary>
        /// Writes a DNG from raw samples as stored (one channel CFA data, or LinearRaw planes; UInt8 / UInt16 / UInt32 /
        /// Float16 / Float32) and the parameters in <paramref name="info"/> (all of them are written: CFA, levels,
        /// linearization, active area, crop, calibrations, white balance, exposure, profile tables, opcode lists). EXIF / XMP
        /// come from the image's metadata. Lossless: decoding the file gives the same samples and the same parameters.
        /// </summary>
        public static void Encode(RawImage stored, DngInfo info, Stream output, DngEncodeOptions? options = null)
            => DngEncoder.Encode(stored, info, output ?? throw new ArgumentNullException(nameof(output)), options ?? new DngEncodeOptions());

        /// <summary>Writes a raw image read with <see cref="DecodeRaw(ReadOnlySpan{byte}, DngRawStage, bool)"/> (stage <see cref="DngRawStage.Stored"/>) back as DNG.</summary>
        public static void Encode(DngRawImage raw, Stream output, DngEncodeOptions? options = null)
        {
            if (raw == null) throw new ArgumentNullException(nameof(raw));
            if (raw.Stage != DngRawStage.Stored) throw new ArgumentException("DNG: only stored samples can be written", nameof(raw));
            Encode(raw.Image, raw.Info, output, options);
        }

        // ------------------------------------------------------------------------------------------ previews

        /// <summary>The rendered previews / thumbnails in the file, smallest first.</summary>
        public static DngPreviewInfo[] GetPreviews(ReadOnlySpan<byte> data) => ReadInfo(data).Previews.ToArray();

        static DngPreviewInfo? Pick(DngInfo info, int minSize, bool jpegOnly)
        {
            DngPreviewInfo? best = null;
            foreach (var p in info.Previews)
            {
                if (jpegOnly && !p.IsJpegFile) continue;
                best = p;
                if (Math.Max(p.Width, p.Height) >= minSize) break;
            }
            return best;
        }

        /// <summary>
        /// Decodes an embedded preview: the smallest one whose longer side is at least <paramref name="minSize"/> pixels, else
        /// the largest (0 = the smallest). Turned upright by the file's orientation. Null when the file has no preview.
        /// Much faster than rendering the raw data.
        /// </summary>
        public static RawImage? DecodePreview(ReadOnlySpan<byte> data, int minSize = int.MaxValue)
        {
            fixed (byte* p = data)
            {
                var src = new TiffMemorySource(p, data.Length);
                return Guard(() => PreviewCore(new TiffFile(src), minSize));
            }
        }

        public static RawImage? DecodePreview(Stream seekableStream, int minSize = int.MaxValue)
        {
            var src = new TiffStreamSource(seekableStream);
            return Guard(() => PreviewCore(new TiffFile(src), minSize), seekableStream, src.Origin);
        }

        static RawImage? PreviewCore(TiffFile f, int minSize)
        {
            var info = DngReader.Read(f, out _);
            var pv = Pick(info, minSize, false);
            if (pv == null) return null;
            var o = new TiffDecodeOptions { ApplyOrientation = false };
            var ti = new TiffImage(f, pv.Offset, o);
            var img = ti.Decode(0, 0, ti.Width, ti.Height, o);
            img.Metadata ??= new ImageMetadata();
            img.Metadata.Orientation = info.Orientation;
            return info.Orientation > 1 ? ImageOps.ApplyOrientation(img, info.Orientation) : img;
        }

        /// <summary>
        /// The bytes of an embedded preview that is a complete JPEG file (the largest, or the smallest at least
        /// <paramref name="minSize"/> on its longer side), ready to be written out or decoded; null when there is none. The
        /// JPEG is as stored: the DNG's orientation applies to it (<see cref="DngInfo.Orientation"/>).
        /// </summary>
        public static byte[]? ExtractPreviewJpeg(ReadOnlySpan<byte> data, int minSize = int.MaxValue)
        {
            var info = ReadInfo(data);
            var pv = Pick(info, minSize, true);
            if (pv == null) return null;
            return data.Slice((int)pv.JpegOffset, (int)pv.JpegLength).ToArray();
        }

        static T Guard<T>(Func<T> f, Stream? stream = null, long origin = 0)
        {
            try
            {
                try { return f(); }
                catch (AggregateException ae)
                {
                    // from the parallel loops: the first real failure
                    var inner = ae.Flatten().InnerExceptions.FirstOrDefault(x => x is ImageFormatException or ImageNotSupportedException) ?? ae.Flatten().InnerExceptions[0];
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();
                    throw;
                }
            }
            catch (Exception e) when (e is IndexOutOfRangeException || e is OverflowException || e is InvalidCastException
                || e is DivideByZeroException || e is EndOfStreamException || e is InvalidDataException
                || (e is ArgumentException && e is not TiffArgumentException && !e.Message.StartsWith("DNG:")))
            {
                throw new ImageFormatException("DNG: corrupt file", e);
            }
            finally
            {
                if (stream != null) stream.Position = origin;
            }
        }
    }
}
