using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Aardvark.ImageCodecs
{
    /// <summary>How <see cref="Dicom.Encode(RawVolume, Stream, DicomEncodeOptions?)"/> stores the pixels.</summary>
    public enum DicomCompression
    {
        /// <summary>Uncompressed, Explicit VR Little Endian.</summary>
        None,
        /// <summary>RLE Lossless (PS3.5 Annex G).</summary>
        Rle,
        /// <summary>Lossless JPEG, first order prediction (process 14, selection value 1).</summary>
        JpegLossless,
        /// <summary>JPEG-LS lossless.</summary>
        JpegLsLossless,
        /// <summary>JPEG 2000 lossless (reversible 5/3 wavelet, RCT for color).</summary>
        Jpeg2000Lossless,
        /// <summary>HTJ2K lossless (High Throughput JPEG 2000).</summary>
        HtJ2kLossless,
        /// <summary>Baseline JPEG, lossy (8 bit samples only); see <see cref="DicomEncodeOptions.Quality"/>.</summary>
        JpegBaseline,
    }

    public sealed class DicomEncodeOptions
    {
        public DicomCompression Compression { get; set; } = DicomCompression.None;
        /// <summary>Quality 1..100 of <see cref="DicomCompression.JpegBaseline"/>.</summary>
        public int Quality { get; set; } = 90;
        /// <summary>Modality (CT, MR, OT, ...); default: the source's (a decoded DICOM file), else OT.</summary>
        public string? Modality { get; set; }
        public string? PatientName { get; set; }
        public string? PatientId { get; set; }
        public string? StudyDescription { get; set; }
        public string? SeriesDescription { get; set; }
        /// <summary>Study and series UIDs (default: the source's, else new ones); new instance UIDs are always made.</summary>
        public string? StudyInstanceUid { get; set; }
        public string? SeriesInstanceUid { get; set; }
        public int SeriesNumber { get; set; } = 1;
    }

    public static partial class Dicom
    {
        /// <summary>A new UID in the 2.25 arc (a UUID as a decimal integer, PS3.5 B.2).</summary>
        public static string NewUid()
        {
            var b = Guid.NewGuid().ToByteArray();
            return "2.25." + new BigInteger(b, isUnsigned: true, isBigEndian: false).ToString(CultureInfo.InvariantCulture);
        }

        public static void Encode(RawImage image, Stream output, DicomEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            Encode(RawVolume.FromImage(image), output, options);
        }

        public static byte[] Encode(RawImage image, DicomEncodeOptions? options = null)
        {
            var ms = new MemoryStream();
            Encode(image, ms, options);
            return ms.ToArray();
        }

        /// <summary>
        /// Writes one DICOM file (Secondary Capture; multi-frame for more than one slice): gray (MONOCHROME2) or RGB
        /// samples of 8 to 32 bits, signed or unsigned, or float / double data (uncompressed only). Alpha is left out;
        /// 32 bit integers that fit 16 bits are written with 16. Geometry from <see cref="RawVolume.Metadata"/> goes to
        /// Image Position / Orientation and Pixel Spacing (and per-frame plane positions, which
        /// <see cref="DecodeVolume(DicomDataset, VolumeDecodeOptions?)"/> and enhanced-aware readers use), a rescale that
        /// is not applied yet to Rescale Slope / Intercept. Grids of more than three axes are written frame after frame.
        /// </summary>
        public static void Encode(RawVolume volume, Stream output, DicomEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(volume);
            ArgumentNullException.ThrowIfNull(output);
            options ??= new DicomEncodeOptions();
            var frames = Prepare(volume, out var px);
            var ids = Ids(volume.Metadata, options);
            int n = frames.Length;
            var w = new DicomWriter();
            string sop = n == 1 ? DicomUids.SecondaryCapture
                : px.Channels == 3 ? DicomUids.MultiFrameTrueColorSecondaryCapture
                : px.Bits == 8 ? DicomUids.MultiFrameGrayscaleByteSecondaryCapture : DicomUids.MultiFrameGrayscaleWordSecondaryCapture;
            Common(w, sop, ids, options, 1);
            var g = Geometry(volume.Metadata);
            if (g != null)
            {
                w.Strings(DicomTags.ImagePositionPatient, "DS", g.Position(0));
                w.Strings(DicomTags.ImageOrientationPatient, "DS", g.Orientation);
                w.Strings(DicomTags.PixelSpacing, "DS", g.PixelSpacing);
                if (n > 1 && g.SliceSpacing > 0)
                {
                    w.Strings(DicomTags.SpacingBetweenSlices, "DS", new[] { g.SliceSpacing });
                    w.Strings(DicomTags.SliceThickness, "DS", new[] { g.SliceSpacing });
                    // per-frame plane positions in the functional groups: the multi-frame way to say where each frame is
                    var shared = new DicomWriter();
                    var pm = new DicomWriter();
                    pm.Strings(DicomTags.PixelSpacing, "DS", g.PixelSpacing);
                    pm.Strings(DicomTags.SliceThickness, "DS", new[] { g.SliceSpacing });
                    var po = new DicomWriter();
                    po.Strings(DicomTags.ImageOrientationPatient, "DS", g.Orientation);
                    shared.Sequence(DicomTags.PixelMeasuresSequence, new[] { pm });
                    shared.Sequence(DicomTags.PlaneOrientationSequence, new[] { po });
                    w.Sequence(DicomTags.SharedFunctionalGroups, new[] { shared });
                    var per = new DicomWriter[n];
                    for (int k = 0; k < n; k++)
                    {
                        var pp = new DicomWriter();
                        pp.Strings(DicomTags.ImagePositionPatient, "DS", g.Position(k % Math.Max(1, volume.Depth)));
                        per[k] = new DicomWriter();
                        per[k].Sequence(DicomTags.PlanePositionSequence, new[] { pp });
                    }
                    w.Sequence(DicomTags.PerFrameFunctionalGroups, per);
                }
            }
            else if (volume.Metadata is { Spacing.Length: >= 2 } m0 && !double.IsNaN(m0.Spacing[0]) && !double.IsNaN(m0.Spacing[1]))
                w.Strings(DicomTags.PixelSpacing, "DS", new[] { m0.Spacing[1], m0.Spacing[0] });
            if (n > 1)
            {
                // Frame Increment Pointer: the slice locations along the stack when it has a geometry, else frame labels
                if (g != null && g.SliceSpacing > 0)
                {
                    w.Ints(0x00280009, "AT", new long[] { 0x0018, 0x2005 });
                    w.Strings(0x00182005, "DS", Enumerable.Range(0, n).Select(k => k % Math.Max(1, volume.Depth) * g.SliceSpacing).ToArray());
                }
                else
                {
                    w.Ints(0x00280009, "AT", new long[] { 0x0018, 0x2002 });
                    w.String(0x00182002, "SH", string.Join('\\', Enumerable.Range(1, n)));
                }
            }
            PixelModule(w, px, n, volume.Metadata, options);
            WritePixels(w, frames, px, options);
            WriteFile(w, output, sop, ids.Instance, Syntax(options.Compression));
        }

        public static byte[] Encode(RawVolume volume, DicomEncodeOptions? options = null)
        {
            var ms = new MemoryStream();
            Encode(volume, ms, options);
            return ms.ToArray();
        }

        /// <summary>
        /// Writes a volume as a series of single-frame files (IM00001.dcm, ... in <paramref name="directory"/>), each with
        /// its Image Position: the form most viewers and tools expect for CT and MR volumes. Returns the file paths.
        /// </summary>
        public static IReadOnlyList<string> EncodeSeries(RawVolume volume, string directory, DicomEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(volume);
            options ??= new DicomEncodeOptions();
            Directory.CreateDirectory(directory);
            var frames = Prepare(volume, out var px);
            var ids = Ids(volume.Metadata, options);
            var g = Geometry(volume.Metadata);
            var files = new List<string>();
            for (int k = 0; k < frames.Length; k++)
            {
                var w = new DicomWriter();
                string instance = NewUid();
                Common(w, DicomUids.SecondaryCapture, ids with { Instance = instance }, options, k + 1);
                if (g != null)
                {
                    int z = k % Math.Max(1, volume.Depth);
                    w.Strings(DicomTags.ImagePositionPatient, "DS", g.Position(z));
                    w.Strings(DicomTags.ImageOrientationPatient, "DS", g.Orientation);
                    w.Strings(DicomTags.PixelSpacing, "DS", g.PixelSpacing);
                    if (g.SliceSpacing > 0) w.Strings(DicomTags.SliceThickness, "DS", new[] { g.SliceSpacing });
                    if (volume.Sizes.Length > 3) w.Strings(DicomTags.TemporalPositionIdentifier, "IS", new double[] { k / Math.Max(1, volume.Depth) + 1 });
                }
                PixelModule(w, px, 1, volume.Metadata, options);
                WritePixels(w, new[] { frames[k] }, px, options);
                var path = Path.Combine(directory, $"IM{k + 1:D5}.dcm");
                using (var fs = File.Create(path)) WriteFile(w, fs, DicomUids.SecondaryCapture, instance, Syntax(options.Compression));
                files.Add(path);
            }
            return files;
        }

        // ================================================================ content

        sealed record IdSet(string Study, string Series, string Instance, string? Modality, string? PatientName, string? PatientId);

        static IdSet Ids(PhysicalMetadata? m, DicomEncodeOptions o)
        {
            string? Src(string key) => m?.SourceFormat == ImageFormat.Dicom ? m.Get(key) : null;
            return new IdSet(o.StudyInstanceUid ?? Src("StudyInstanceUID") ?? NewUid(), o.SeriesInstanceUid ?? NewUid(), NewUid(),
                o.Modality ?? Src("Modality") ?? "OT", o.PatientName ?? Src("PatientName"), o.PatientId ?? Src("PatientID"));
        }

        static void Common(DicomWriter w, string sop, IdSet ids, DicomEncodeOptions o, int instanceNumber)
        {
            w.String(DicomTags.SpecificCharacterSet, "CS", "ISO_IR 192");
            w.String(DicomTags.ImageType, "CS", "DERIVED\\SECONDARY");
            w.String(DicomTags.SopClassUid, "UI", sop);
            w.String(DicomTags.SopInstanceUid, "UI", ids.Instance);
            w.String(DicomTags.StudyDate, "DA", "");
            w.String(DicomTags.StudyTime, "TM", "");
            w.String(0x00080050, "SH", "");               // Accession Number
            w.String(DicomTags.Modality, "CS", ids.Modality ?? "OT");
            w.String(0x00080064, "CS", "WSD");            // Conversion Type: workstation
            w.String(0x00080090, "PN", "");               // Referring Physician's Name
            if (o.StudyDescription != null) w.String(DicomTags.StudyDescription, "LO", o.StudyDescription);
            if (o.SeriesDescription != null) w.String(DicomTags.SeriesDescription, "LO", o.SeriesDescription);
            w.String(DicomTags.PatientName, "PN", ids.PatientName ?? "");
            w.String(DicomTags.PatientId, "LO", ids.PatientId ?? "");
            w.String(DicomTags.PatientBirthDate, "DA", "");
            w.String(DicomTags.PatientSex, "CS", "");
            w.String(DicomTags.StudyInstanceUid, "UI", ids.Study);
            w.String(DicomTags.SeriesInstanceUid, "UI", ids.Series);
            w.String(DicomTags.StudyId, "SH", "");
            w.Strings(DicomTags.SeriesNumber, "IS", new double[] { o.SeriesNumber });
            w.Strings(DicomTags.InstanceNumber, "IS", new double[] { instanceNumber });
            w.String(0x00200020, "CS", "");               // Patient Orientation
        }

        /// <summary>The samples as written: channels (1 or 3), bits allocated, signedness, and whether they are float data.</summary>
        sealed class Px
        {
            public int Width, Height, Channels, Bits;
            public bool Signed, Float, Double;
        }

        /// <summary>Splits the grid into frames of the sample type that will be written (alpha dropped, 32 bit narrowed when it fits).</summary>
        static Array[] Prepare(RawVolume v, out Px px)
        {
            px = new Px { Width = v.Width, Height = v.Height };
            int c = v.Channels;
            px.Channels = c >= 3 && v.Color is ColorModel.Rgb or ColorModel.Rgba ? 3 : c == 2 && v.Color == ColorModel.GrayAlpha ? 1 : c;
            if (px.Channels is not (1 or 3)) throw new ImageNotSupportedException($"DICOM: {c} channels ({v.Color}): gray or RGB only");
            var data = v.Data;
            var t = v.SampleType;
            if (t == SampleType.Float16) { data = VolumeIO.Convert(data, SampleType.Float32); t = SampleType.Float32; }
            if (t is SampleType.UInt64 or SampleType.Int64) throw new ImageNotSupportedException($"DICOM: {t} samples");
            if (t is SampleType.Int32 or SampleType.UInt32)
            {
                var d = VolumeIOAccess.Doubles(data);
                double lo = d.Length == 0 ? 0 : d.Min(), hi = d.Length == 0 ? 0 : d.Max();
                if (lo >= 0 && hi <= ushort.MaxValue) { data = VolumeIO.Convert(data, SampleType.UInt16); t = SampleType.UInt16; }
                else if (lo >= short.MinValue && hi <= short.MaxValue) { data = VolumeIO.Convert(data, SampleType.Int16); t = SampleType.Int16; }
            }
            px.Float = t == SampleType.Float32;
            px.Double = t == SampleType.Float64;
            px.Signed = t is SampleType.Int8 or SampleType.Int16 or SampleType.Int32;
            px.Bits = RawImage.BytesPerSample(t) * 8;
            long plane = (long)v.Width * v.Height;
            int frames = (int)(v.Data.LongLength / (plane * c));
            var r = new Array[frames];
            for (int f = 0; f < frames; f++)
            {
                var a = RawImage.CreateArray(t, (int)(plane * px.Channels));
                if (px.Channels == c) Array.Copy(data, f * plane * c, a, 0, plane * c);
                else
                    for (long i = 0; i < plane; i++)
                        for (int k = 0; k < px.Channels; k++) a.SetValue(data.GetValue(f * plane * c + i * c + k), i * px.Channels + k);
                r[f] = a;
            }
            return r;
        }

        static void PixelModule(DicomWriter w, Px px, int frames, PhysicalMetadata? m, DicomEncodeOptions o)
        {
            bool jpegBaseline = o.Compression == DicomCompression.JpegBaseline;
            if (px.Float || px.Double)
            {
                if (o.Compression != DicomCompression.None) throw new ImageNotSupportedException("DICOM: float samples are only written uncompressed");
                if (px.Channels != 1) throw new ImageNotSupportedException("DICOM: float samples must be gray");
            }
            if (px.Bits == 32 && o.Compression is not (DicomCompression.None or DicomCompression.Rle) && !px.Float)
                throw new ImageNotSupportedException($"DICOM: 32 bit samples cannot be written with {o.Compression}");
            if (jpegBaseline && px.Bits != 8) throw new ImageNotSupportedException("DICOM: baseline JPEG needs 8 bit samples");
            w.Strings(DicomTags.SamplesPerPixel, "US", new double[] { px.Channels });
            string photometric = px.Channels == 1 ? "MONOCHROME2"
                : jpegBaseline ? "YBR_FULL"
                : o.Compression is DicomCompression.Jpeg2000Lossless or DicomCompression.HtJ2kLossless ? "YBR_RCT" : "RGB";
            w.String(DicomTags.PhotometricInterpretation, "CS", photometric);
            if (px.Channels > 1) w.Strings(DicomTags.PlanarConfiguration, "US", new double[] { 0 });
            if (frames > 1) w.Strings(DicomTags.NumberOfFrames, "IS", new double[] { frames });
            w.Strings(DicomTags.Rows, "US", new double[] { px.Height });
            w.Strings(DicomTags.Columns, "US", new double[] { px.Width });
            w.Strings(DicomTags.BitsAllocated, "US", new double[] { px.Bits });
            if (!px.Float && !px.Double)
            {
                w.Strings(DicomTags.BitsStored, "US", new double[] { px.Bits });
                w.Strings(DicomTags.HighBit, "US", new double[] { px.Bits - 1 });
                w.Strings(DicomTags.PixelRepresentation, "US", new double[] { px.Signed ? 1 : 0 });
                if (m != null && !m.ScalingApplied && VolumeIO.HasScaling(m.Slope, m.Intercept))
                {
                    w.Strings(DicomTags.RescaleIntercept, "DS", new[] { m.Intercept });
                    w.Strings(DicomTags.RescaleSlope, "DS", new[] { m.Slope });
                    w.String(DicomTags.RescaleType, "LO", m.ValueUnit ?? "US");
                }
            }
            if (jpegBaseline)
            {
                w.String(0x00282110, "CS", "01");     // Lossy Image Compression
                w.String(0x00282114, "CS", "ISO_10918_1");
            }
        }

        // ================================================================ geometry

        sealed class Plane
        {
            public double[] Origin = new double[3], Row = new double[3], Col = new double[3], Step = new double[3];
            public double[] PixelSpacing = new double[2];
            public double SliceSpacing;
            public double[] Orientation => Row.Concat(Col).ToArray();
            public double[] Position(int k) => new[] { Origin[0] + k * Step[0], Origin[1] + k * Step[1], Origin[2] + k * Step[2] };
        }

        /// <summary>The volume's geometry in LPS (needs at least a direction or an origin).</summary>
        static Plane? Geometry(PhysicalMetadata? m)
        {
            if (m == null || (m.Direction == null && m.Origin == null)) return null;
            var a = m.GetVoxelToLps();
            if (a == null || a.GetLength(0) < 3) return null;
            int axes = a.GetLength(1) - 1;
            var p = new Plane();
            double[] Col(int c) => c < axes ? new[] { a[0, c], a[1, c], a[2, c] } : new double[3];
            var x = Col(0); var y = Col(1); var z = Col(2);
            double dx = Norm(x), dy = Norm(y), dz = Norm(z);
            if (dx == 0 || dy == 0) return null;
            for (int k = 0; k < 3; k++) { p.Row[k] = x[k] / dx; p.Col[k] = y[k] / dy; p.Origin[k] = a[k, axes]; p.Step[k] = z[k]; }
            p.PixelSpacing = new[] { dy, dx };
            p.SliceSpacing = dz;
            return p;
        }

        static double Norm(double[] v) => Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);

        // ================================================================ pixel data

        static string Syntax(DicomCompression c) => c switch
        {
            DicomCompression.None => DicomUids.ExplicitVrLittleEndian,
            DicomCompression.Rle => DicomUids.RleLossless,
            DicomCompression.JpegLossless => DicomUids.JpegLosslessSV1,
            DicomCompression.JpegLsLossless => DicomUids.JpegLsLossless,
            DicomCompression.Jpeg2000Lossless => DicomUids.Jpeg2000Lossless,
            DicomCompression.HtJ2kLossless => DicomUids.HtJ2kLossless,
            DicomCompression.JpegBaseline => DicomUids.JpegBaseline,
            _ => throw new ArgumentOutOfRangeException(nameof(c)),
        };

        static void WritePixels(DicomWriter w, Array[] frames, Px px, DicomEncodeOptions o)
        {
            if (o.Compression == DicomCompression.None)
            {
                long bytes = frames.Sum(f => f.LongLength) * (px.Bits / 8);
                if (bytes + 1 > int.MaxValue) throw new ImageNotSupportedException("DICOM: pixel data above 2 GB");
                var b = new byte[bytes + (bytes & 1)];
                long at = 0;
                foreach (var f in frames)
                {
                    var src = VolumeIO.ByteSpan(f, RawImage.SampleTypeOf(f.GetType().GetElementType()!), 0, (int)f.LongLength);
                    src.CopyTo(b.AsSpan((int)at));
                    at += src.Length;
                }
                uint tag = px.Float ? DicomTags.FloatPixelData : px.Double ? DicomTags.DoubleFloatPixelData : DicomTags.PixelData;
                w.Bytes(tag, px.Float ? "OF" : px.Double ? "OD" : px.Bits == 8 ? "OB" : "OW", b);
                return;
            }
            var streams = new byte[frames.Length][];
            System.Threading.Tasks.Parallel.For(0, frames.Length, f => streams[f] = Compress(frames[f], px, o));
            w.Encapsulated(DicomTags.PixelData, streams);
        }

        static byte[] Compress(Array frame, Px px, DicomEncodeOptions o)
        {
            if (o.Compression == DicomCompression.Rle) return DicomRle.Encode(frame, px.Width, px.Height, px.Channels, px.Bits);
            // the code streams take unsigned samples: signed ones go as their two's complement bits
            var unsigned = px.Signed ? Reinterpret(frame) : frame;
            var img = new RawImage(px.Width, px.Height, px.Channels == 1 ? ColorModel.Gray : ColorModel.Rgb, unsigned, px.Channels);
            var ms = new MemoryStream();
            switch (o.Compression)
            {
                case DicomCompression.JpegLossless:
                    Jpeg.Encode(img, ms, new JpegEncodeOptions { Lossless = true, Predictor = 1 });
                    break;
                case DicomCompression.JpegLsLossless:
                    JpegLs.Encode(img, ms, new JpegLsEncodeOptions());
                    break;
                case DicomCompression.Jpeg2000Lossless:
                case DicomCompression.HtJ2kLossless:
                    Jp2.Encode(img, ms, new Jp2EncodeOptions
                    {
                        Lossless = true, RawCodestream = true, HighThroughput = o.Compression == DicomCompression.HtJ2kLossless,
                        WriteMetadata = false, Parallel = false,
                    });
                    break;
                case DicomCompression.JpegBaseline:
                    Jpeg.Encode(img, ms, new JpegEncodeOptions { Quality = o.Quality, Subsampling = JpegSubsampling.Chroma444 });
                    break;
            }
            return ms.ToArray();
        }

        static Array Reinterpret(Array a) => a switch
        {
            sbyte[] s => Array.ConvertAll(s, v => (byte)v),
            short[] s => Array.ConvertAll(s, v => (ushort)v),
            int[] s => Array.ConvertAll(s, v => (uint)v),
            _ => a,
        };

        // ================================================================ file

        static void WriteFile(DicomWriter body, Stream output, string sop, string instance, string syntax)
        {
            var meta = new DicomWriter();
            meta.Bytes(0x00020001, "OB", new byte[] { 0, 1 });
            meta.String(DicomTags.MediaStorageSopClass, "UI", sop);
            meta.String(DicomTags.MediaStorageSopInstance, "UI", instance);
            meta.String(DicomTags.TransferSyntax, "UI", syntax);
            meta.String(DicomTags.ImplementationClassUid, "UI", ImplementationUid);
            meta.String(DicomTags.ImplementationVersionName, "SH", "PIXCODEC");
            var metaBytes = meta.ToArray();
            var head = new byte[132 + 12];
            "DICM"u8.CopyTo(head.AsSpan(128));
            // (0002,0000) UL: the length of the rest of the meta group
            BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(132), 0x0002);
            BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(134), 0x0000);
            head[136] = (byte)'U'; head[137] = (byte)'L';
            BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(138), 4);
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(140), (uint)metaBytes.Length);
            output.Write(head);
            output.Write(metaBytes);
            body.WriteTo(output);
        }

        /// <summary>Implementation Class UID of files this library writes (a fixed UUID in the 2.25 arc).</summary>
        const string ImplementationUid = "2.25.203361532419104779311829574004613451585";
    }

    /// <summary>Collects data set elements and writes them in tag order, Explicit VR Little Endian.</summary>
    internal sealed class DicomWriter
    {
        readonly SortedDictionary<uint, byte[]> _elements = new();

        static readonly HashSet<string> s_long = new() { "OB", "OD", "OF", "OL", "OV", "OW", "SQ", "UC", "UN", "UR", "UT" };

        void Add(uint tag, string vr, ReadOnlySpan<byte> value)
        {
            int len = value.Length + (value.Length & 1);
            bool lng = s_long.Contains(vr);
            var b = new byte[(lng ? 12 : 8) + len];
            BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)(tag >> 16));
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)tag);
            b[4] = (byte)vr[0]; b[5] = (byte)vr[1];
            if (lng) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), (uint)len);
            else
            {
                if (len > ushort.MaxValue) throw new ArgumentException($"DICOM: value of ({tag >> 16:X4},{tag & 0xFFFF:X4}) too long for {vr}");
                BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), (ushort)len);
            }
            value.CopyTo(b.AsSpan(lng ? 12 : 8));
            // padding: UI with NUL, text with a space, binary with 0
            if ((value.Length & 1) != 0) b[^1] = vr is "UI" or "OB" or "UN" ? (byte)0 : vr is "AE" or "AS" or "CS" or "DA" or "DS" or "DT" or "IS" or "LO" or "LT" or "PN" or "SH" or "ST" or "TM" or "UC" or "UR" or "UT" ? (byte)' ' : (byte)0;
            _elements[tag] = b;
        }

        public void String(uint tag, string vr, string value)
            => Add(tag, vr, (vr is "PN" or "LO" or "SH" or "LT" or "ST" or "UT" or "UC" ? Encoding.UTF8 : Encoding.ASCII).GetBytes(value));

        /// <summary>Numbers as DS / IS text or binary US / SS / UL / FL / FD, by VR.</summary>
        public void Strings(uint tag, string vr, double[] values)
        {
            switch (vr)
            {
                case "DS":
                    String(tag, vr, string.Join('\\', values.Select(Ds)));
                    break;
                case "IS":
                    String(tag, vr, string.Join('\\', values.Select(v => ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture))));
                    break;
                case "US":
                {
                    var b = new byte[2 * values.Length];
                    for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2 * i), (ushort)values[i]);
                    Add(tag, vr, b);
                    break;
                }
                default: throw new ArgumentException(vr);
            }
        }

        /// <summary>AT values (pairs of group, element).</summary>
        public void Ints(uint tag, string vr, long[] values)
        {
            var b = new byte[2 * values.Length];
            for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2 * i), (ushort)values[i]);
            Add(tag, vr, b);
        }

        /// <summary>A decimal string of at most 16 characters (PS3.5 DS).</summary>
        static string Ds(double v)
        {
            if (Math.Abs(v) < 1e-12) return "0";
            for (int digits = 15; digits > 0; digits--)
            {
                var s = v.ToString("G" + digits, CultureInfo.InvariantCulture);
                if (s.Length <= 16) return s;
            }
            return v.ToString("E6", CultureInfo.InvariantCulture);
        }

        public void Bytes(uint tag, string vr, byte[] value) => Add(tag, vr, value);

        public void Sequence(uint tag, IEnumerable<DicomWriter> items)
        {
            var ms = new MemoryStream();
            Span<byte> h = stackalloc byte[8];
            foreach (var it in items)
            {
                var body = it.ToArray();
                BinaryPrimitives.WriteUInt32LittleEndian(h, 0xE000FFFE);
                BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(4), (uint)body.Length);
                ms.Write(h);
                ms.Write(body);
            }
            Add(tag, "SQ", ms.ToArray());
        }

        /// <summary>Encapsulated pixel data: a basic offset table (empty for one frame) and one even length fragment per frame.</summary>
        public void Encapsulated(uint tag, byte[][] frames)
        {
            var ms = new MemoryStream();
            var h = new byte[8];
            void Item(ReadOnlySpan<byte> data, int length)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(h, 0xE000FFFE);
                BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), (uint)length);
                ms.Write(h);
                ms.Write(data);
                if (length > data.Length) ms.WriteByte(0);
            }
            var bot = new byte[frames.Length > 1 ? 4 * frames.Length : 0];
            long off = 0;
            for (int f = 0; f < frames.Length && frames.Length > 1; f++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bot.AsSpan(4 * f), (uint)off);
                off += 8 + frames[f].Length + (frames[f].Length & 1);
            }
            Item(bot, bot.Length);
            foreach (var f in frames) Item(f, f.Length + (f.Length & 1));
            BinaryPrimitives.WriteUInt32LittleEndian(h, 0xE0DDFFFE);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), 0);
            ms.Write(h);
            // undefined length OB
            var head = new byte[12];
            BinaryPrimitives.WriteUInt16LittleEndian(head, (ushort)(tag >> 16));
            BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(2), (ushort)tag);
            head[4] = (byte)'O'; head[5] = (byte)'B';
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(8), 0xFFFFFFFF);
            _elements[tag] = head.Concat(ms.ToArray()).ToArray();
        }

        public byte[] ToArray()
        {
            var ms = new MemoryStream();
            WriteTo(ms);
            return ms.ToArray();
        }

        public void WriteTo(Stream s)
        {
            foreach (var e in _elements.Values) s.Write(e);
        }
    }

    internal static class VolumeIOAccess
    {
        public static double[] Doubles(Array a)
        {
            var r = new double[a.LongLength];
            switch (a)
            {
                case int[] i when a.GetType() == typeof(int[]): for (long k = 0; k < r.Length; k++) r[k] = i[k]; break;
                case uint[] u when a.GetType() == typeof(uint[]): for (long k = 0; k < r.Length; k++) r[k] = u[k]; break;
                default: for (long k = 0; k < r.Length; k++) r[k] = Convert.ToDouble(a.GetValue(k), CultureInfo.InvariantCulture); break;
            }
            return r;
        }
    }
}
