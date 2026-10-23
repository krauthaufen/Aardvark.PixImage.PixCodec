using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs
{
    /// <summary>Options of the DICOM decoder.</summary>
    public sealed class DicomDecodeOptions : VolumeDecodeOptions
    {
        /// <summary>
        /// Map PALETTE COLOR images through their lookup tables to RGB (default); false returns the stored indices as
        /// gray samples.
        /// </summary>
        public bool ApplyPalette { get; set; } = true;

        /// <summary>
        /// MONOCHROME1 images store dark as high values. True flips them within the stored range so that every gray image
        /// reads like MONOCHROME2; off by default, so that the values keep their meaning (and the rescale its).
        /// </summary>
        public bool InvertMonochrome1 { get; set; }

        /// <summary>Decode the frames of a multi-frame file and the files of a series on several threads (default).</summary>
        public bool Parallel { get; set; } = true;

        internal static readonly DicomDecodeOptions DefaultDicom = new();

        internal static DicomDecodeOptions From(VolumeDecodeOptions? o)
            => o as DicomDecodeOptions ?? (o == null ? DefaultDicom : new DicomDecodeOptions { Scaling = o.Scaling, MaxSamples = o.MaxSamples });
    }

    /// <summary>One series of a DICOM study (from a DICOMDIR or a folder scan), see <see cref="Dicom.FindSeries"/>.</summary>
    public sealed class DicomSeries
    {
        public string SeriesInstanceUid { get; internal set; } = "";
        public string? StudyInstanceUid { get; internal set; }
        public string? PatientName { get; internal set; }
        public string? PatientId { get; internal set; }
        public string? StudyDate { get; internal set; }
        public string? StudyDescription { get; internal set; }
        public string? SeriesDescription { get; internal set; }
        public string? Modality { get; internal set; }
        public int? SeriesNumber { get; internal set; }
        /// <summary>The files of the series (images only, no presentation states or reports).</summary>
        public IReadOnlyList<string> Files { get; internal set; } = Array.Empty<string>();

        /// <summary>Decodes all files of the series into one volume (<see cref="Dicom.DecodeSeries(IEnumerable{string}, DicomDecodeOptions?)"/>).</summary>
        public RawVolume Decode(DicomDecodeOptions? options = null) => Dicom.DecodeSeries(Files, options);

        public override string ToString() => $"{Modality} {SeriesNumber} {SeriesDescription} ({Files.Count} files)";
    }

    /// <summary>
    /// DICOM (PS3.10 files, PS3.5 encoding): every uncompressed transfer syntax (explicit / implicit VR little endian,
    /// explicit big endian, deflated) and the image compressions JPEG (baseline, extended, lossless), JPEG-LS, JPEG 2000,
    /// HTJ2K, JPEG XL and RLE, also files without preamble or file meta information.
    /// <para>
    /// Pixels: 1 to 32 bit integers (bits above Bits Stored are masked off, signed values sign extended), float and
    /// double float pixel data, MONOCHROME1 / 2, RGB, YBR_FULL / YBR_FULL_422 (converted to RGB) and PALETTE COLOR (see
    /// <see cref="DicomDecodeOptions.ApplyPalette"/>). The rescale slope and intercept (or an RT dose grid scaling) are
    /// applied per <see cref="VolumeDecodeOptions.Scaling"/>, which turns CT images into Hounsfield units.
    /// </para>
    /// <para>
    /// Geometry goes to <see cref="PhysicalMetadata"/> in LPS (the patient coordinate system): x along the rows (Image
    /// Orientation's first vector), y down the columns, origin at the centre of the first pixel. Multi-frame files decode
    /// as volumes (enhanced files with their per-frame positions, RT dose grids with their frame offsets), and
    /// <see cref="DecodeSeries(string, DicomDecodeOptions?)"/> stacks the single-frame files of a series by position.
    /// <see cref="FindSeries"/> lists the series of a DICOMDIR or a folder (a CD or USB stick from a hospital).
    /// </para>
    /// </summary>
    public static partial class Dicom
    {
        const string Fmt = "DICOM";

        /// <summary>A DICOM file (with the "DICM" prefix, or a plausible bare data set as on old media).</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header) => DicomParser.IsMatch(header);

        /// <summary>Parses a file's data set; with <paramref name="headerOnly"/> it stops at the pixel data. The data set refers to <paramref name="data"/>.</summary>
        public static DicomDataset ReadDataset(byte[] data, bool headerOnly = false) => DicomParser.Parse(data, headerOnly);

        /// <summary>Parses the data set of a file.</summary>
        public static DicomDataset ReadDataset(string path, bool headerOnly = false) => DicomParser.Parse(File.ReadAllBytes(path), headerOnly);

        // ================================================================ info

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => GetInfo(data, null);

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data, DicomDecodeOptions? options)
        {
            var v = GetVolumeInfo(data, options);
            return v.ToImageInfo(v.Metadata.Get("BitsStored") is string b && int.TryParse(b, out int bits) ? bits : 0) with { FrameCount = v.Depth };
        }

        public static VolumeInfo GetVolumeInfo(ReadOnlySpan<byte> data, VolumeDecodeOptions? options = null)
        {
            var o = DicomDecodeOptions.From(options);
            var ds = DicomParser.Parse(data.ToArray(), true);
            // (a cut header must not pass for a complete one: the stream path grows its prefix on ImageFormatException)
            if (!ds.ReachedPixels) throw new ImageFormatException($"{Fmt}: no pixel data (data ends early, or the file holds no image)");
            if (!HasPixels(ds)) throw new ImageNotSupportedException($"{Fmt}: the file holds no image (SOP class {ds.GetString(DicomTags.SopClassUid) ?? ds.GetString(DicomTags.MediaStorageSopClass)})");
            var p = PixelInfo.Of(ds, o);
            var m = Metadata(ds, p, ds.GetInt(DicomTags.NumberOfFrames) ?? 1);
            return new VolumeInfo
            {
                Format = ImageFormat.Dicom, Sizes = p.Frames > 1 ? new[] { p.Columns, p.Rows, p.Frames } : new[] { p.Columns, p.Rows },
                Channels = p.OutChannels, Color = p.OutColor, StoredType = p.OutStored,
                SampleType = p.ModalityLut != null && o.Scaling != DataScaling.Raw ? VolumeIO.ScaledType(p.ModalityType, 1, 0, o.Scaling)
                    : VolumeIO.ScaledType(p.OutStored, m.Slope, m.Intercept, p.Scalable ? o.Scaling : DataScaling.Raw),
                Encoding = TransferSyntaxName(ds.TransferSyntax), Metadata = m,
            };
        }

        static bool HasPixels(DicomDataset ds) => ds.Contains(DicomTags.Rows) && ds.Contains(DicomTags.Columns);

        /// <summary>A short name of a transfer syntax ("explicit little", "JPEG lossless", ...).</summary>
        public static string TransferSyntaxName(string uid) => uid switch
        {
            DicomUids.ImplicitVrLittleEndian => "implicit little",
            DicomUids.ExplicitVrLittleEndian => "explicit little",
            DicomUids.EncapsulatedUncompressed => "encapsulated uncompressed",
            DicomUids.DeflatedExplicitVrLittleEndian => "deflated",
            DicomUids.ExplicitVrBigEndian => "explicit big",
            DicomUids.JpegBaseline => "JPEG baseline",
            DicomUids.JpegExtended => "JPEG extended",
            DicomUids.JpegLossless or DicomUids.JpegLosslessSV1 => "JPEG lossless",
            DicomUids.JpegLsLossless => "JPEG-LS lossless",
            DicomUids.JpegLsNearLossless => "JPEG-LS near lossless",
            DicomUids.Jpeg2000Lossless or DicomUids.Jpeg2000Part2Lossless => "JPEG 2000 lossless",
            DicomUids.Jpeg2000 or DicomUids.Jpeg2000Part2 => "JPEG 2000",
            DicomUids.HtJ2kLossless or DicomUids.HtJ2kLosslessRpcl => "HTJ2K lossless",
            DicomUids.HtJ2k => "HTJ2K",
            DicomUids.JpegXlLossless => "JPEG XL lossless",
            DicomUids.JpegXlJpegRecompression or DicomUids.JpegXl => "JPEG XL",
            DicomUids.RleLossless => "RLE",
            _ => uid,
        };

        // ================================================================ decode

        /// <summary>The first frame.</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, DicomDecodeOptions? options = null)
        {
            var ds = DicomParser.Parse(data.ToArray(), false);
            return DecodeFrames(ds, DicomDecodeOptions.From(options), 0, 1).GetSlice(0);
        }

        /// <summary>All frames of a file: a 2D grid for one frame, else a 3D one (x, y, frame).</summary>
        public static RawVolume DecodeVolume(ReadOnlySpan<byte> data, VolumeDecodeOptions? options = null)
            => DecodeVolume(DicomParser.Parse(data.ToArray(), false), options);

        /// <summary>
        /// All frames of a file. For a DICOMDIR the largest image series it lists (see <see cref="FindSeries"/>); for a folder
        /// the largest series in it.
        /// </summary>
        public static RawVolume DecodeVolume(string path, VolumeDecodeOptions? options = null)
        {
            var o = DicomDecodeOptions.From(options);
            if (Directory.Exists(path)) return Largest(path).Decode(o);
            var ds = DicomParser.Parse(File.ReadAllBytes(path), false);
            if (!HasPixels(ds) && ds.Contains(DicomTags.DirectoryRecordSequence)) return Largest(path).Decode(o);
            return DecodeVolume(ds, o);
        }

        static DicomSeries Largest(string path)
        {
            var all = FindSeries(path);
            if (all.Count == 0) throw new ImageFormatException($"{Fmt}: no image series in '{path}'");
            return all.OrderByDescending(s => s.Files.Count).First();
        }

        /// <summary>All frames of a parsed data set.</summary>
        public static RawVolume DecodeVolume(DicomDataset ds, VolumeDecodeOptions? options = null)
        {
            var o = DicomDecodeOptions.From(options);
            if (!HasPixels(ds)) throw new ImageNotSupportedException($"{Fmt}: the file holds no image");
            int frames = Math.Max(1, ds.GetInt(DicomTags.NumberOfFrames) ?? 1);
            return DecodeFrames(ds, o, 0, frames);
        }

        /// <summary>Every frame as a separate image.</summary>
        public static RawImage[] DecodeAll(ReadOnlySpan<byte> data, DicomDecodeOptions? options = null)
            => DecodeVolume(data, options).GetSlices();

        static RawVolume DecodeFrames(DicomDataset ds, DicomDecodeOptions o, int first, int count)
        {
            var p = PixelInfo.Of(ds, o);
            count = Math.Min(count, p.Frames - first);
            if (count <= 0) throw new ImageFormatException($"{Fmt}: no such frame");
            long frameSamples = (long)p.Rows * p.Columns * p.OutChannels;
            var arr = VolumeIO.Allocate(p.OutStored, VolumeIO.Mul(frameSamples, count, Fmt), o.MaxSamples, Fmt);
            var frames = FrameSource.Of(ds, p);
            void One(int i) => p.DecodeFrame(frames.Get(first + i), arr, i * frameSamples);
            if (o.Parallel && count > 1) Parallel.For(0, count, One);
            else for (int i = 0; i < count; i++) One(i);
            var m = Metadata(ds, p, count, first);
            return Finish(arr, p, m, count > 1 ? new[] { p.Columns, p.Rows, count } : new[] { p.Columns, p.Rows }, o);
        }

        static RawVolume Finish(Array arr, PixelInfo p, PhysicalMetadata m, int[] sizes, VolumeDecodeOptions o)
        {
            if (p.ModalityLut != null && o.Scaling != DataScaling.Raw)
            {
                arr = p.ApplyModalityLut(arr);
                var lt = VolumeIO.ScaledType(p.ModalityType, 1, 0, o.Scaling);
                if (lt != p.ModalityType) arr = VolumeIO.Convert(arr, lt);
                m.Slope = 1; m.Intercept = 0; m.ScalingApplied = true;
            }
            else if (p.Scalable)
            {
                var t = VolumeIO.ScaledType(p.OutStored, m.Slope, m.Intercept, o.Scaling);
                if ((VolumeIO.HasScaling(m.Slope, m.Intercept) && o.Scaling != DataScaling.Raw) || t != p.OutStored)
                {
                    arr = VolumeIO.Scale(arr, t, m.Slope, m.Intercept, null);
                    m.ScalingApplied = VolumeIO.HasScaling(m.Slope, m.Intercept);
                }
            }
            return new RawVolume(sizes, p.OutColor, arr, p.OutChannels, m);
        }

        // ================================================================ pixel description

        internal sealed class PixelInfo
        {
            public int Rows, Columns, Frames, Spp, Planar, BitsAllocated, BitsStored, HighBit;
            public bool Signed;
            public string Photometric = "MONOCHROME2", TransferSyntax = DicomUids.ExplicitVrLittleEndian;
            public bool BigEndian;
            public uint PixelTag = DicomTags.PixelData;
            public SampleType Stored;              // the samples as stored (before palette or color conversion)
            public SampleType OutStored;           // as decoded (palette: the LUT's type)
            public int OutChannels;
            public ColorModel OutColor;
            public ushort[][]? Lut;                // palette: R, G, B
            public int LutFirst, LutBits;
            public bool Invert;
            /// <summary>Whether rescale slope / intercept apply (gray, not a palette or float data).</summary>
            public bool Scalable;

            public bool IsFloat => PixelTag != DicomTags.PixelData;

            /// <summary>Samples of one uncompressed frame: 4:2:2 data holds two luma and two chroma samples per pixel pair.</summary>
            public long NativeFrameSamples => Photometric == "YBR_FULL_422" && Spp == 3 ? (long)Rows * ((Columns + 1) / 2) * 4 : (long)Rows * Columns * Spp;
            public bool Ybr => Photometric.StartsWith("YBR_FULL", StringComparison.Ordinal);

            public static PixelInfo Of(DicomDataset ds, DicomDecodeOptions o)
            {
                var p = new PixelInfo
                {
                    Rows = ds.GetInt(DicomTags.Rows) ?? 0,
                    Columns = ds.GetInt(DicomTags.Columns) ?? 0,
                    Frames = Math.Max(1, ds.GetInt(DicomTags.NumberOfFrames) ?? 1),
                    Spp = ds.GetInt(DicomTags.SamplesPerPixel) ?? 1,
                    Planar = ds.GetInt(DicomTags.PlanarConfiguration) ?? 0,
                    Photometric = (ds.GetString(DicomTags.PhotometricInterpretation) ?? "MONOCHROME2").Trim().ToUpperInvariant(),
                    TransferSyntax = ds.TransferSyntax,
                    BigEndian = ds.TransferSyntax == DicomUids.ExplicitVrBigEndian,
                };
                if (p.Rows <= 0 || p.Columns <= 0) throw new ImageFormatException($"{Fmt}: bad image size {p.Columns} x {p.Rows}");
                if (ds.Contains(DicomTags.FloatPixelData)) p.PixelTag = DicomTags.FloatPixelData;
                else if (ds.Contains(DicomTags.DoubleFloatPixelData)) p.PixelTag = DicomTags.DoubleFloatPixelData;
                if (p.Spp is not (1 or 3 or 4)) throw new ImageNotSupportedException($"{Fmt}: {p.Spp} samples per pixel");
                p.BitsAllocated = ds.GetInt(DicomTags.BitsAllocated) ?? (p.PixelTag == DicomTags.DoubleFloatPixelData ? 64 : p.IsFloat ? 32 : 8);
                p.BitsStored = ds.GetInt(DicomTags.BitsStored) ?? p.BitsAllocated;
                p.HighBit = ds.GetInt(DicomTags.HighBit) ?? p.BitsStored - 1;
                p.Signed = (ds.GetInt(DicomTags.PixelRepresentation) ?? 0) == 1;
                if (p.BitsStored <= 0 || p.BitsStored > p.BitsAllocated) p.BitsStored = p.BitsAllocated;
                if (p.HighBit < p.BitsStored - 1 || p.HighBit >= p.BitsAllocated) p.HighBit = p.BitsStored - 1;
                p.Stored = p.PixelTag == DicomTags.DoubleFloatPixelData ? SampleType.Float64
                    : p.IsFloat ? SampleType.Float32
                    : p.BitsAllocated switch
                    {
                        1 => SampleType.UInt8,
                        8 => p.Signed ? SampleType.Int8 : SampleType.UInt8,
                        16 => p.Signed ? SampleType.Int16 : SampleType.UInt16,
                        32 => p.Signed ? SampleType.Int32 : SampleType.UInt32,
                        _ => throw new ImageNotSupportedException($"{Fmt}: {p.BitsAllocated} bits allocated"),
                    };
                // a lossy JPEG of 12 bit data keeps the type; 8 bit JPEG in 16 bit allocation (rare) too
                p.OutStored = p.Stored;
                p.OutChannels = p.Spp;
                p.OutColor = p.Spp == 1 ? ColorModel.Gray : p.Spp == 3 ? ColorModel.Rgb : ColorModel.Other;
                if (p.Photometric == "PALETTE COLOR" && p.Spp == 1 && o.ApplyPalette)
                {
                    p.ReadPalette(ds);
                    p.OutStored = p.LutBits > 8 ? SampleType.UInt16 : SampleType.UInt8;
                    p.OutChannels = 3;
                    p.OutColor = ColorModel.Rgb;
                }
                p.Invert = o.InvertMonochrome1 && p.Photometric == "MONOCHROME1";
                p.Scalable = p.Spp == 1 && p.Lut == null && !p.IsFloat;
                if (p.Scalable) p.ReadModalityLut(ds);
                return p;
            }

            /// <summary>A LUT descriptor (entries, 0 meaning 65536; first mapped value, signed for signed data; bits per entry).</summary>
            (int Entries, int First, int Bits) Descriptor(DicomElement desc)
            {
                if (desc.Length < 6) throw new ImageFormatException($"{Fmt}: bad lookup table descriptor");
                var w = Words(desc);
                int entries = w[0], first = w[1];
                return (entries == 0 ? 65536 : entries, Signed ? (short)first : first, w[2]);
            }

            static ushort[] Words(DicomElement e)
            {
                var b = e.Bytes;
                var w = new ushort[b.Length / 2];
                for (int i = 0; i < w.Length; i++) w[i] = e.BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(b.Slice(2 * i)) : BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(2 * i));
                return w;
            }

            /// <summary>LUT data: 16 bit words, or (shorter data) one byte per entry.</summary>
            static ushort[] LutData(DicomElement data, int entries)
            {
                var lut = new ushort[entries];
                if (data.Length >= 2 * entries) Words(data).AsSpan(0, entries).CopyTo(lut);
                else if (data.Length >= entries) for (int i = 0; i < entries; i++) lut[i] = data.Bytes[i];
                else throw new ImageFormatException($"{Fmt}: lookup table data too short");
                return lut;
            }

            void ReadPalette(DicomDataset ds)
            {
                Lut = new ushort[3][];
                for (int c = 0; c < 3; c++)
                {
                    var desc = ds[DicomTags.RedPaletteDescriptor + (uint)c];
                    var data = ds[DicomTags.RedPaletteData + (uint)c];
                    var seg = ds[DicomTags.SegmentedRedPalette + (uint)c];
                    if (desc == null || (data == null && seg == null)) throw new ImageFormatException($"{Fmt}: PALETTE COLOR without lookup tables");
                    var (entries, first, bits) = Descriptor(desc);
                    LutFirst = first; LutBits = bits;
                    Lut[c] = data != null ? LutData(data, entries) : ExpandSegmented(Words(seg!), entries);
                }
                if (LutBits <= 8)
                {
                    // 8 bit tables written as 16 bit words: the value is in the low byte (or, by some writers, the high one)
                    bool high = Lut.All(l => l.All(v => (v & 0xFF) == 0)) && Lut.Any(l => l.Any(v => v != 0));
                    foreach (var l in Lut) for (int i = 0; i < l.Length; i++) l[i] = (ushort)(high ? l[i] >> 8 : l[i] & 0xFF);
                }
            }

            /// <summary>
            /// A segmented palette (PS3.3 C.7.9.2): discrete segments (opcode 0: length values), linear ones (1: length
            /// entries on the line from the last value to the one given) and indirect ones (2: length segments copied
            /// from a byte offset into the data, low word first).
            /// </summary>
            static ushort[] ExpandSegmented(ushort[] w, int entries)
            {
                var r = new List<ushort>(entries);
                void Run(int at, int segments, bool nested)
                {
                    int i = at;
                    for (int done = 0; done < segments && i + 1 < w.Length; done++)
                    {
                        int op = w[i], n = w[i + 1];
                        i += 2;
                        switch (op)
                        {
                            case 0:
                                if (i + n > w.Length) throw new ImageFormatException($"{Fmt}: segmented palette runs past its data");
                                for (int k = 0; k < n; k++) r.Add(w[i + k]);
                                i += n;
                                break;
                            case 1:
                                if (r.Count == 0 || i >= w.Length) throw new ImageFormatException($"{Fmt}: linear palette segment without a start");
                                double y0 = r[^1], y1 = w[i++];
                                for (int k = 1; k <= n; k++) r.Add((ushort)Math.Round(y0 + (y1 - y0) * k / n));
                                break;
                            case 2:
                                if (nested || i + 1 >= w.Length) throw new ImageFormatException($"{Fmt}: bad indirect palette segment");
                                long off = w[i] | (long)w[i + 1] << 16;
                                i += 2;
                                Run((int)(off / 2), n, true);
                                break;
                            default:
                                throw new ImageFormatException($"{Fmt}: palette segment type {op}");
                        }
                    }
                }
                Run(0, int.MaxValue, false);
                var lut = new ushort[entries];
                for (int i = 0; i < entries; i++) lut[i] = r.Count == 0 ? (ushort)0 : r[Math.Min(i, r.Count - 1)];
                return lut;
            }

            /// <summary>The Modality LUT Sequence's table (instead of rescale slope / intercept), else null.</summary>
            public ushort[]? ModalityLut;
            public int ModalityFirst, ModalityBits;

            void ReadModalityLut(DicomDataset ds)
            {
                var items = ds.GetSequence(DicomTags.ModalityLutSequence);
                if (items.Count == 0) return;
                var desc = items[0][DicomTags.LutDescriptor];
                var data = items[0][DicomTags.LutData];
                if (desc == null || data == null) return;
                var (entries, first, bits) = Descriptor(desc);
                ModalityLut = LutData(data, entries);
                ModalityFirst = first; ModalityBits = bits;
            }

            public SampleType ModalityType => ModalityBits > 8 ? SampleType.UInt16 : SampleType.UInt8;

            /// <summary>Stored values through the modality LUT (clamped to its range).</summary>
            public Array ApplyModalityLut(Array a)
            {
                var lut = ModalityLut!;
                long n = a.LongLength;
                var dst = RawImage.CreateArray(ModalityType, (int)n);
                for (long i = 0; i < n; i++)
                {
                    long v = Stored switch
                    {
                        SampleType.UInt8 => ((byte[])a)[i], SampleType.Int8 => ((sbyte[])a)[i],
                        SampleType.UInt16 => ((ushort[])a)[i], SampleType.Int16 => ((short[])a)[i],
                        SampleType.UInt32 => ((uint[])a)[i], SampleType.Int32 => ((int[])a)[i], _ => 0,
                    };
                    ushort x = lut[(int)Math.Clamp(v - ModalityFirst, 0, lut.Length - 1)];
                    if (dst is byte[] b) b[i] = (byte)x; else ((ushort[])dst)[i] = x;
                }
                return dst;
            }

            /// <summary>
            /// Decodes one frame (stored bytes, or a compressed code stream) into <paramref name="dst"/> at
            /// <paramref name="offset"/> (samples), applying the bit layout, color model and palette.
            /// </summary>
            public void DecodeFrame(FrameData f, Array dst, long offset)
            {
                long pixels = (long)Rows * Columns;
                if (Lut == null && OutStored == Stored)
                {
                    DecodeStored(f, dst, offset);
                    return;
                }
                var tmp = RawImage.CreateArray(Stored, (int)(pixels * Spp));
                DecodeStored(f, tmp, 0);
                if (Lut != null) ApplyPalette(tmp, dst, offset, pixels);
            }

            void DecodeStored(FrameData f, Array dst, long offset)
            {
                long pixels = (long)Rows * Columns;
                int n = (int)(pixels * Spp);
                switch (f.Kind)
                {
                    case FrameKind.Native:
                        int stored = (int)NativeFrameSamples;
                        ReadNative(f.Data.Span, f.BitOffset, dst, offset, stored);
                        FixBits(dst, offset, stored, mask: true);
                        if (Spp > 1 && Planar == 1) Interleave(dst, offset, pixels);
                        if (Ybr) YbrToRgb(dst, offset, pixels);
                        break;
                    case FrameKind.Rle:
                        DicomRle.Decode(f.Data.Span, Rows, Columns, Spp, BitsAllocated, dst, offset);
                        FixBits(dst, offset, n, mask: true);
                        if (Ybr) YbrToRgb(dst, offset, pixels);
                        break;
                    default:
                        var img = DecodeCodestream(f);
                        if (img.Width != Columns || img.Height != Rows || img.Channels != Spp)
                            throw new ImageFormatException($"{Fmt}: the compressed frame is {img.Width} x {img.Height} x {img.Channels}, the header says {Columns} x {Rows} x {Spp}");
                        CopyConverted(img.Data, dst, offset, n);
                        FixBits(dst, offset, n, mask: false);
                        break;
                }
                if (Invert) InvertGray(dst, offset, n);
            }

            RawImage DecodeCodestream(FrameData f)
            {
                var d = f.Data.Span;
                switch (f.Kind)
                {
                    case FrameKind.Jpeg:
                        // RGB in DICOM means no color transform was applied, whatever the JPEG markers suggest
                        return Jpeg.Decode(d, new JpegDecodeOptions { KeepYCbCr = Photometric == "RGB", ApplyOrientation = false });
                    case FrameKind.JpegLs:
                        return JpegLs.Decode(d);
                    case FrameKind.Jpeg2000:
                        return Jp2.Decode(d, new Jp2DecodeOptions { ApplyOrientation = false, Parallel = false });
                    case FrameKind.JpegXl:
                        return Jxl.Decode(d, new JxlDecodeOptions { ApplyOrientation = false });
                    default:
                        throw new ImageNotSupportedException($"{Fmt}: transfer syntax {TransferSyntax}");
                }
            }

            // -------------------------------------------------------------- native samples

            void ReadNative(ReadOnlySpan<byte> src, int bitOffset, Array dst, long offset, int n)
            {
                if (BitsAllocated == 1)
                {
                    var d = (byte[])dst;
                    for (int i = 0; i < n; i++)
                    {
                        int bit = bitOffset + i;
                        d[offset + i] = (byte)(bit >> 3 < src.Length ? src[bit >> 3] >> (bit & 7) & 1 : 0);
                    }
                    return;
                }
                int bps = BitsAllocated / 8;
                long need = (long)n * bps;
                if (src.Length < need)
                {
                    // a short last frame (cut files): what is there, zeros after
                    VolumeIO.CopySamples(src.Slice(0, src.Length / bps * bps), dst, offset, BigEndian);
                    return;
                }
                VolumeIO.CopySamples(src.Slice(0, (int)need), dst, offset, BigEndian);
            }

            /// <summary>Removes bits above Bits Stored (overlays) and sign extends; with a high bit above them, shifts down first.</summary>
            void FixBits(Array a, long offset, int n, bool mask)
            {
                if (IsFloat || BitsAllocated == 1) return;
                int shift = mask ? HighBit + 1 - BitsStored : 0;
                int bits = BitsStored;
                if (bits >= BitsAllocated && shift == 0) return;
                long m = (1L << bits) - 1, sign = 1L << (bits - 1);
                switch (a)
                {
                    case ushort[] u when Stored == SampleType.UInt16: for (long i = offset; i < offset + n; i++) u[i] = (ushort)((u[i] >> shift) & m); break;
                    case short[] s when Stored == SampleType.Int16:
                        for (long i = offset; i < offset + n; i++) { long v = ((ushort)s[i] >> shift) & m; s[i] = (short)((v ^ sign) - sign); }
                        break;
                    case byte[] b when Stored == SampleType.UInt8: for (long i = offset; i < offset + n; i++) b[i] = (byte)((b[i] >> shift) & m); break;
                    case sbyte[] sb when Stored == SampleType.Int8:
                        for (long i = offset; i < offset + n; i++) { long v = ((byte)sb[i] >> shift) & m; sb[i] = (sbyte)((v ^ sign) - sign); }
                        break;
                    case uint[] ui when Stored == SampleType.UInt32: for (long i = offset; i < offset + n; i++) ui[i] = (uint)((ui[i] >> shift) & m); break;
                    case int[] si when Stored == SampleType.Int32:
                        for (long i = offset; i < offset + n; i++) { long v = ((uint)si[i] >> shift) & m; si[i] = (int)((v ^ sign) - sign); }
                        break;
                }
            }

            /// <summary>Codec output (unsigned or signed integers of its own width) into the stored type.</summary>
            void CopyConverted(Array src, Array dst, long offset, int n)
            {
                var st = RawImage.SampleTypeOf(src.GetType().GetElementType()!);
                if (st == Stored && Stored is not (SampleType.Int8 or SampleType.Int16 or SampleType.Int32))
                {
                    Array.Copy(src, 0, dst, offset, n);
                    return;
                }
                for (int i = 0; i < n; i++)
                {
                    long v = st switch
                    {
                        SampleType.UInt8 => ((byte[])src)[i], SampleType.Int8 => ((sbyte[])src)[i],
                        SampleType.UInt16 => ((ushort[])src)[i], SampleType.Int16 => ((short[])src)[i],
                        SampleType.UInt32 => ((uint[])src)[i], SampleType.Int32 => ((int[])src)[i],
                        SampleType.Float32 => (long)((float[])src)[i],
                        _ => throw new ImageNotSupportedException($"{Fmt}: codec output {st}"),
                    };
                    // unsigned code stream samples of signed data: two's complement in Bits Stored bits
                    if (Signed && st is SampleType.UInt8 or SampleType.UInt16 or SampleType.UInt32)
                    {
                        long sign = 1L << (BitsStored - 1);
                        v &= (sign << 1) - 1;
                        v = (v ^ sign) - sign;
                    }
                    Store(dst, offset + i, v);
                }
            }

            static void Store(Array dst, long i, long v)
            {
                switch (dst)
                {
                    case byte[] b: b[i] = (byte)v; break;
                    case sbyte[] sb: sb[i] = (sbyte)v; break;
                    case ushort[] u: u[i] = (ushort)v; break;
                    case short[] s: s[i] = (short)v; break;
                    case uint[] ui: ui[i] = (uint)v; break;
                    case int[] si: si[i] = (int)v; break;
                }
            }

            void Interleave(Array a, long offset, long pixels)
            {
                int n = (int)(pixels * Spp);
                var t = RawImage.CreateArray(Stored, n);
                Array.Copy(a, offset, t, 0, n);
                var r = VolumeIO.Interleave(t, pixels, Spp);
                Array.Copy(r, 0, a, offset, n);
            }

            /// <summary>YBR_FULL (and the 4:2:2 form, expanded first) to RGB, PS3.3 C.7.6.3.1.2.</summary>
            void YbrToRgb(Array a, long offset, long pixels)
            {
                if (Spp != 3 || Stored is not (SampleType.UInt8 or SampleType.UInt16)) return;
                int max = (1 << BitsStored) - 1, half = 1 << (BitsStored - 1);
                long Get(long i) => a is byte[] b ? b[i] : ((ushort[])a)[i];
                void Set(long i, double v) { int x = (int)Math.Clamp(Math.Round(v), 0, max); if (a is byte[] b) b[i] = (byte)x; else ((ushort[])a)[i] = (ushort)x; }
                if (Photometric == "YBR_FULL_422" && !IsCompressedKind)
                {
                    // uncompressed 4:2:2 is stored as Y0 Y1 Cb Cr per pixel pair in the first two thirds of the frame
                    int cols = Columns;
                    var full = new long[pixels * 3];
                    long src = 0;
                    for (int y = 0; y < Rows; y++)
                        for (int x = 0; x < cols; x += 2)
                        {
                            long p0 = (long)y * cols + x;
                            long y0 = Get(offset + src), y1 = Get(offset + src + 1), cb = Get(offset + src + 2), cr = Get(offset + src + 3);
                            src += 4;
                            full[p0 * 3] = y0; full[p0 * 3 + 1] = cb; full[p0 * 3 + 2] = cr;
                            if (x + 1 < cols) { full[(p0 + 1) * 3] = y1; full[(p0 + 1) * 3 + 1] = cb; full[(p0 + 1) * 3 + 2] = cr; }
                        }
                    for (long i = 0; i < pixels * 3; i++) Set(offset + i, full[i]);
                }
                for (long i = 0; i < pixels; i++)
                {
                    long o = offset + i * 3;
                    double y = Get(o), cb = Get(o + 1) - half, cr = Get(o + 2) - half;
                    Set(o, y + 1.402 * cr);
                    Set(o + 1, y - 0.344136 * cb - 0.714136 * cr);
                    Set(o + 2, y + 1.772 * cb);
                }
            }

            bool IsCompressedKind => TransferSyntax != DicomUids.ImplicitVrLittleEndian && TransferSyntax != DicomUids.ExplicitVrLittleEndian
                && TransferSyntax != DicomUids.ExplicitVrBigEndian && TransferSyntax != DicomUids.DeflatedExplicitVrLittleEndian
                && TransferSyntax != DicomUids.EncapsulatedUncompressed;

            void InvertGray(Array a, long offset, int n)
            {
                if (Spp != 1 || Lut != null || IsFloat) return;
                // unsigned: max - v; signed (two's complement): -1 - v
                long max = (1L << BitsStored) - 1;
                // (switch on the type: a byte[] pattern also matches sbyte[] arrays)
                switch (Stored)
                {
                    case SampleType.UInt8: { var b = (byte[])a; for (long i = offset; i < offset + n; i++) b[i] = (byte)(max - b[i]); break; }
                    case SampleType.UInt16: { var u = (ushort[])a; for (long i = offset; i < offset + n; i++) u[i] = (ushort)(max - u[i]); break; }
                    case SampleType.UInt32: { var u = (uint[])a; for (long i = offset; i < offset + n; i++) u[i] = (uint)(max - u[i]); break; }
                    case SampleType.Int8: { var b = (sbyte[])a; for (long i = offset; i < offset + n; i++) b[i] = (sbyte)(-1 - b[i]); break; }
                    case SampleType.Int16: { var u = (short[])a; for (long i = offset; i < offset + n; i++) u[i] = (short)(-1 - u[i]); break; }
                    case SampleType.Int32: { var u = (int[])a; for (long i = offset; i < offset + n; i++) u[i] = -1 - u[i]; break; }
                }
            }

            void ApplyPalette(Array idx, Array dst, long offset, long pixels)
            {
                var lut = Lut!;
                int last = lut[0].Length - 1;
                for (long i = 0; i < pixels; i++)
                {
                    long v = Stored switch
                    {
                        SampleType.UInt8 => ((byte[])idx)[i], SampleType.Int8 => ((sbyte[])idx)[i],
                        SampleType.UInt16 => ((ushort[])idx)[i], SampleType.Int16 => ((short[])idx)[i], _ => 0,
                    };
                    int k = (int)Math.Clamp(v - LutFirst, 0, last);
                    for (int c = 0; c < 3; c++)
                    {
                        long o = offset + i * 3 + c;
                        if (dst is byte[] db) db[o] = (byte)lut[c][k]; else ((ushort[])dst)[o] = lut[c][k];
                    }
                }
            }
        }

        // ================================================================ frames

        internal enum FrameKind { Native, Rle, Jpeg, JpegLs, Jpeg2000, JpegXl }

        internal readonly struct FrameData
        {
            public readonly FrameKind Kind;
            public readonly ReadOnlyMemory<byte> Data;
            public readonly int BitOffset; // 1 bit native data: where the frame starts within Data's first byte run
            public FrameData(FrameKind kind, ReadOnlyMemory<byte> data, int bitOffset = 0) { Kind = kind; Data = data; BitOffset = bitOffset; }
        }

        /// <summary>Finds the bytes of each frame: native slices, or the fragments of encapsulated pixel data.</summary>
        internal sealed class FrameSource
        {
            readonly PixelInfo _p;
            readonly ReadOnlyMemory<byte> _native;
            readonly List<ReadOnlyMemory<byte>>? _frames;
            readonly FrameKind _kind;

            FrameSource(PixelInfo p, ReadOnlyMemory<byte> native, List<ReadOnlyMemory<byte>>? frames, FrameKind kind)
            {
                _p = p; _native = native; _frames = frames; _kind = kind;
            }

            public static FrameSource Of(DicomDataset ds, PixelInfo p)
            {
                var px = ds[p.PixelTag] ?? throw new ImageFormatException($"{Fmt}: no pixel data");
                if (px.Fragments == null)
                {
                    if (DicomUids.IsEncapsulated(p.TransferSyntax) && p.TransferSyntax != DicomUids.EncapsulatedUncompressed && px.Length > 0)
                    {
                        // a compressed transfer syntax with native pixel data: some writers keep the file meta of the source
                    }
                    var native = px.Memory;
                    if (p.BigEndian && p.BitsAllocated < 16 && px.VR == "OW")
                    {
                        // 8 (or 1) bit samples in an OW value of a big endian file: the bytes are swapped in pairs
                        var b = native.ToArray();
                        for (int i = 0; i + 1 < b.Length; i += 2) (b[i], b[i + 1]) = (b[i + 1], b[i]);
                        native = b;
                    }
                    return new FrameSource(p, native, null, FrameKind.Native);
                }
                var kind = KindOf(p.TransferSyntax);
                return new FrameSource(p, default, SplitFrames(ds, px.Fragments, p.Frames, kind), kind);
            }

            static FrameKind KindOf(string ts) => ts switch
            {
                DicomUids.EncapsulatedUncompressed => FrameKind.Native,
                DicomUids.RleLossless => FrameKind.Rle,
                DicomUids.JpegBaseline or DicomUids.JpegExtended or DicomUids.JpegLossless or DicomUids.JpegLosslessSV1 => FrameKind.Jpeg,
                DicomUids.JpegLsLossless or DicomUids.JpegLsNearLossless => FrameKind.JpegLs,
                DicomUids.Jpeg2000Lossless or DicomUids.Jpeg2000 or DicomUids.Jpeg2000Part2Lossless or DicomUids.Jpeg2000Part2
                    or DicomUids.HtJ2kLossless or DicomUids.HtJ2kLosslessRpcl or DicomUids.HtJ2k => FrameKind.Jpeg2000,
                DicomUids.JpegXlLossless or DicomUids.JpegXlJpegRecompression or DicomUids.JpegXl => FrameKind.JpegXl,
                _ => throw new ImageNotSupportedException($"{Fmt}: transfer syntax {ts} (video or unknown)"),
            };

            public FrameData Get(int i)
            {
                if (_frames != null)
                {
                    if (i >= _frames.Count) throw new ImageFormatException($"{Fmt}: frame {i} is missing");
                    return new FrameData(_kind, _frames[i]);
                }
                long pixels = _p.NativeFrameSamples;
                if (_p.BitsAllocated == 1)
                {
                    long bit = pixels * i;
                    int start = (int)(bit >> 3);
                    return new FrameData(FrameKind.Native, start < _native.Length ? _native.Slice(start) : default, (int)(bit & 7));
                }
                long bytes = pixels * (_p.BitsAllocated / 8);
                long at = bytes * i;
                if (at >= _native.Length) throw new ImageFormatException($"{Fmt}: frame {i} is missing");
                return new FrameData(FrameKind.Native, _native.Slice((int)at, (int)Math.Min(bytes, _native.Length - at)));
            }

            /// <summary>
            /// The code stream of every frame: from the extended or the basic offset table, one fragment per frame, or the
            /// fragments grouped by where a code stream starts.
            /// </summary>
            static List<ReadOnlyMemory<byte>> SplitFrames(DicomDataset ds, IReadOnlyList<ReadOnlyMemory<byte>> frags, int frames, FrameKind kind)
            {
                var result = new List<ReadOnlyMemory<byte>>(frames);
                if (frags.Count < 2) return result;
                var bot = frags[0].Span;
                int nf = frags.Count - 1;
                // item offsets of the fragments, measured from the first fragment's item tag
                var starts = new long[nf + 1];
                for (int k = 0; k < nf; k++) starts[k + 1] = starts[k] + 8 + frags[k + 1].Length;
                int IndexAt(long off)
                {
                    int k = Array.BinarySearch(starts, 0, nf, off);
                    if (k < 0) throw new ImageFormatException($"{Fmt}: offset table points between fragments");
                    return k;
                }
                var eot = ds[DicomTags.ExtendedOffsetTable];
                if (eot != null && eot.Length >= 8 * frames)
                {
                    var e = eot.Bytes;
                    for (int f = 0; f < frames; f++)
                    {
                        int k0 = IndexAt((long)BinaryPrimitives.ReadUInt64LittleEndian(e.Slice(8 * f)));
                        int k1 = f + 1 < frames ? IndexAt((long)BinaryPrimitives.ReadUInt64LittleEndian(e.Slice(8 * f + 8))) : nf;
                        result.Add(Join(frags, k0 + 1, k1 + 1));
                    }
                    return result;
                }
                if (bot.Length >= 4 * frames && frames > 1)
                {
                    for (int f = 0; f < frames; f++)
                    {
                        int k0 = IndexAt(BinaryPrimitives.ReadUInt32LittleEndian(bot.Slice(4 * f)));
                        int k1 = f + 1 < frames ? IndexAt(BinaryPrimitives.ReadUInt32LittleEndian(bot.Slice(4 * f + 4))) : nf;
                        result.Add(Join(frags, k0 + 1, k1 + 1));
                    }
                    return result;
                }
                if (frames == 1) { result.Add(Join(frags, 1, nf + 1)); return result; }
                if (nf == frames) { for (int k = 1; k <= nf; k++) result.Add(frags[k]); return result; }
                // no offsets and more fragments than frames: a frame starts with a code stream marker
                int first = 1;
                for (int k = 2; k <= nf; k++)
                    if (StartsCodestream(frags[k].Span, kind)) { result.Add(Join(frags, first, k)); first = k; }
                result.Add(Join(frags, first, nf + 1));
                return result;
            }

            static bool StartsCodestream(ReadOnlySpan<byte> s, FrameKind kind) => kind switch
            {
                FrameKind.Jpeg or FrameKind.JpegLs => s.Length >= 2 && s[0] == 0xFF && s[1] == 0xD8,
                FrameKind.Jpeg2000 => s.Length >= 4 && ((s[0] == 0xFF && s[1] == 0x4F && s[2] == 0xFF && s[3] == 0x51) || Jp2.IsMatch(s)),
                FrameKind.JpegXl => Jxl.IsMatch(s),
                _ => false,
            };

            static ReadOnlyMemory<byte> Join(IReadOnlyList<ReadOnlyMemory<byte>> frags, int from, int to)
            {
                if (to - from == 1) return frags[from];
                int len = 0;
                for (int k = from; k < to; k++) len += frags[k].Length;
                var b = new byte[len];
                int o = 0;
                for (int k = from; k < to; k++) { frags[k].Span.CopyTo(b.AsSpan(o)); o += frags[k].Length; }
                return b;
            }
        }

        // ================================================================ metadata

        /// <summary>
        /// Geometry (LPS), value scaling and the descriptive elements of the frames [first, first + count) of a data set.
        /// </summary>
        static PhysicalMetadata Metadata(DicomDataset ds, PixelInfo p, int count, int first = 0)
        {
            var m = new PhysicalMetadata { SourceFormat = ImageFormat.Dicom };
            ListHeader(ds, m.Header);
            // value scaling
            var pvt = ds.Find(DicomTags.RescaleSlope, DicomTags.SharedFunctionalGroups, DicomTags.PixelValueTransformationSequence)
                ?? ds.Find(DicomTags.RescaleSlope, DicomTags.PerFrameFunctionalGroups, DicomTags.PixelValueTransformationSequence)
                ?? ds;
            if (p.Scalable)
            {
                m.Slope = pvt.GetDouble(DicomTags.RescaleSlope) ?? 1;
                m.Intercept = pvt.GetDouble(DicomTags.RescaleIntercept) ?? 0;
                if (m.Slope == 0) m.Slope = 1;
                if (ds.GetDouble(DicomTags.DoseGridScaling) is double dose && dose != 0 && !ds.Contains(DicomTags.RescaleSlope)) m.Slope = dose;
                m.ValueUnit = pvt.GetString(DicomTags.RescaleType) ?? ds.GetString(DicomTags.DoseUnits)
                    ?? (ds.GetString(DicomTags.Modality) == "CT" && ds.Contains(DicomTags.RescaleIntercept) ? "HU" : null);
            }

            // geometry: orientation, position and pixel spacing, from the data set or the (shared, then first per-frame) functional groups
            var perFrame = ds.GetSequence(DicomTags.PerFrameFunctionalGroups);
            DicomDataset? Group(uint seq, uint tag, int frame)
            {
                if (frame < perFrame.Count && perFrame[frame].GetSequence(seq) is { Count: > 0 } pf && pf[0].Contains(tag)) return pf[0];
                var sh = ds.GetSequence(DicomTags.SharedFunctionalGroups);
                if (sh.Count > 0 && sh[0].GetSequence(seq) is { Count: > 0 } s && s[0].Contains(tag)) return s[0];
                return null;
            }
            var iop = (Group(DicomTags.PlaneOrientationSequence, DicomTags.ImageOrientationPatient, first) ?? ds).GetDoubles(DicomTags.ImageOrientationPatient);
            var spacing = (Group(DicomTags.PixelMeasuresSequence, DicomTags.PixelSpacing, first) ?? ds).GetDoubles(DicomTags.PixelSpacing);
            double[]? Position(int frame) => (Group(DicomTags.PlanePositionSequence, DicomTags.ImagePositionPatient, frame) ?? ds).GetDoubles(DicomTags.ImagePositionPatient);
            var ipp = Position(first);
            double dx = spacing is { Length: >= 2 } ? spacing[1] : double.NaN, dy = spacing is { Length: >= 2 } ? spacing[0] : double.NaN;
            int axes = count > 1 ? 3 : 2;
            if (iop is { Length: >= 6 } && ipp is { Length: >= 3 })
            {
                var r = Unit(iop[0], iop[1], iop[2]); var c = Unit(iop[3], iop[4], iop[5]);
                var nrm = Unit(r[1] * c[2] - r[2] * c[1], r[2] * c[0] - r[0] * c[2], r[0] * c[1] - r[1] * c[0]);
                double dz = double.NaN;
                var z = nrm;
                if (count > 1)
                {
                    // the step between frames: positions (enhanced), frame offsets (RT dose), else the slice spacing
                    var last = Position(first + count - 1);
                    var offsets = ds.GetDoubles(DicomTags.GridFrameOffsetVector);
                    if (perFrame.Count >= first + count && last != null && !last.SequenceEqual(ipp))
                    {
                        var step = new[] { (last[0] - ipp[0]) / (count - 1), (last[1] - ipp[1]) / (count - 1), (last[2] - ipp[2]) / (count - 1) };
                        dz = Math.Sqrt(step[0] * step[0] + step[1] * step[1] + step[2] * step[2]);
                        z = Unit(step[0], step[1], step[2]);
                    }
                    else if (offsets != null && offsets.Length >= first + count && count > 1)
                    {
                        double o0 = offsets[first], o1 = offsets[first + count - 1];
                        double d = (o1 - o0) / (count - 1);
                        dz = Math.Abs(d);
                        if (d < 0) z = new[] { -nrm[0], -nrm[1], -nrm[2] };
                        // offsets relative to the position: the first frame may not be at 0
                        ipp = new[] { ipp[0] + o0 * nrm[0], ipp[1] + o0 * nrm[1], ipp[2] + o0 * nrm[2] };
                    }
                    else dz = ds.GetDouble(DicomTags.SpacingBetweenSlices) ?? double.NaN;
                }
                m.Space = "LPS";
                m.Origin = ipp.Take(3).ToArray();
                m.Direction = new double[3, axes];
                for (int k = 0; k < 3; k++)
                {
                    m.Direction[k, 0] = r[k]; m.Direction[k, 1] = c[k];
                    if (axes == 3) m.Direction[k, 2] = z[k];
                }
                m.Spacing = axes == 3 ? new[] { dx, dy, dz } : new[] { dx, dy };
                m.Units = Enumerable.Repeat<string?>("mm", axes).ToArray();
            }
            else if (spacing is { Length: >= 2 })
            {
                double dz = ds.GetDouble(DicomTags.SpacingBetweenSlices) ?? double.NaN;
                m.Spacing = axes == 3 ? new[] { dx, dy, dz } : new[] { dx, dy };
                m.Units = Enumerable.Repeat<string?>("mm", axes).ToArray();
            }
            return m;
        }

        static double[] Unit(double x, double y, double z)
        {
            double l = Math.Sqrt(x * x + y * y + z * z);
            return l > 0 ? new[] { x / l, y / l, z / l } : new[] { x, y, z };
        }

        /// <summary>The top level text and number elements (not the file meta group, binary data or sequences) as "Keyword" or "(gggg,eeee)" = value.</summary>
        static void ListHeader(DicomDataset ds, List<KeyValuePair<string, string>> header)
        {
            foreach (var e in ds.Elements)
            {
                if (e.Items != null || e.Fragments != null || e.Group == 0x7FE0 || e.Length > 1024) continue;
                if (e.VR is "OB" or "OW" or "OF" or "OD" or "OL" or "OV" or "UN" or "AT" or "SQ") continue;
                var v = ds.GetString(e.Tag);
                if (v == null) continue;
                header.Add(new(DicomDictionary.Keyword(e.Tag) ?? $"({e.Group:X4},{e.Element:X4})", v));
            }
            header.Add(new("TransferSyntaxUID", ds.TransferSyntax));
        }
    }

    /// <summary>The RLE Lossless compression of PS3.5 Annex G.</summary>
    internal static class DicomRle
    {
        /// <summary>Decodes one frame into <paramref name="dst"/> (interleaved samples).</summary>
        public static void Decode(ReadOnlySpan<byte> frame, int rows, int cols, int spp, int bitsAllocated, Array dst, long offset)
        {
            if (frame.Length < 64) throw new ImageFormatException("DICOM: RLE frame without header");
            int segs = (int)BinaryPrimitives.ReadUInt32LittleEndian(frame);
            int bps = bitsAllocated / 8;
            if (bitsAllocated % 8 != 0 || segs != spp * bps || segs > 15) throw new ImageFormatException($"DICOM: RLE frame with {segs} segments for {spp} x {bitsAllocated} bits");
            int pixels = rows * cols;
            var seg = new byte[pixels];
            for (int s = 0; s < segs; s++)
            {
                int start = (int)BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(4 + 4 * s));
                int end = s + 1 < segs ? (int)BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(8 + 4 * s)) : frame.Length;
                if (start < 64 || start > frame.Length || end < start || end > frame.Length) throw new ImageFormatException("DICOM: bad RLE segment offset");
                Array.Clear(seg);
                Unpack(frame.Slice(start, end - start), seg);
                // segment s holds byte (s % bps) of sample (s / bps), most significant byte first
                int sample = s / bps, shift = 8 * (bps - 1 - s % bps);
                long o = offset + sample;
                switch (dst)
                {
                    case byte[] b: for (int i = 0; i < pixels; i++) b[o + (long)i * spp] = seg[i]; break;
                    case sbyte[] sb: for (int i = 0; i < pixels; i++) sb[o + (long)i * spp] = (sbyte)seg[i]; break;
                    case ushort[] u: for (int i = 0; i < pixels; i++) u[o + (long)i * spp] |= (ushort)(seg[i] << shift); break;
                    case short[] sh: for (int i = 0; i < pixels; i++) sh[o + (long)i * spp] |= (short)(seg[i] << shift); break;
                    case uint[] ui: for (int i = 0; i < pixels; i++) ui[o + (long)i * spp] |= (uint)seg[i] << shift; break;
                    case int[] si: for (int i = 0; i < pixels; i++) si[o + (long)i * spp] |= seg[i] << shift; break;
                }
            }
        }

        /// <summary>
        /// Encodes one frame (interleaved samples of <paramref name="bits"/> bits): one segment per sample and byte, most
        /// significant byte first, each row packed on its own (G.3.1); segments padded to even length.
        /// </summary>
        public static byte[] Encode(Array frame, int width, int height, int spp, int bits)
        {
            int bps = bits / 8, segs = spp * bps;
            if (segs > 15) throw new ImageNotSupportedException($"DICOM: RLE holds at most 15 segments, not {segs}");
            var ms = new MemoryStream();
            ms.Write(new byte[64]);
            var header = new uint[16];
            header[0] = (uint)segs;
            var row = new byte[width];
            var bytes = VolumeIO.ByteSpan(frame, RawImage.SampleTypeOf(frame.GetType().GetElementType()!), 0, (int)frame.LongLength).ToArray();
            for (int s = 0; s < segs; s++)
            {
                header[1 + s] = (uint)ms.Length;
                int sample = s / bps, b = bps - 1 - s % bps; // little endian samples: byte b of each
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++) row[x] = bytes[(((long)y * width + x) * spp + sample) * bps + b];
                    Pack(row, ms);
                }
                if ((ms.Length & 1) != 0) ms.WriteByte(0);
            }
            var r = ms.ToArray();
            for (int i = 0; i < 16; i++) BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(4 * i), header[i]);
            return r;
        }

        static void Pack(byte[] row, Stream o)
        {
            int n = row.Length, i = 0;
            while (i < n)
            {
                int run = 1;
                while (i + run < n && run < 128 && row[i + run] == row[i]) run++;
                if (run >= 2)
                {
                    o.WriteByte((byte)(sbyte)(1 - run));
                    o.WriteByte(row[i]);
                    i += run;
                    continue;
                }
                // literal bytes up to the next run of three or more
                int start = i;
                while (i < n && i - start < 128 && !(i + 2 < n && row[i] == row[i + 1] && row[i] == row[i + 2])) i++;
                o.WriteByte((byte)(i - start - 1));
                o.Write(row, start, i - start);
            }
        }

        /// <summary>One segment: n in 0..127 copies n + 1 bytes, n in -127..-1 repeats the next byte 1 - n times, -128 does nothing.</summary>
        static void Unpack(ReadOnlySpan<byte> src, byte[] dst)
        {
            int i = 0, o = 0;
            while (i < src.Length && o < dst.Length)
            {
                int n = (sbyte)src[i++];
                if (n >= 0)
                {
                    int c = Math.Min(n + 1, Math.Min(src.Length - i, dst.Length - o));
                    src.Slice(i, c).CopyTo(dst.AsSpan(o));
                    i += n + 1; o += c;
                }
                else if (n != -128)
                {
                    if (i >= src.Length) break;
                    int c = Math.Min(1 - n, dst.Length - o);
                    dst.AsSpan(o, c).Fill(src[i++]);
                    o += c;
                }
            }
        }
    }
}
