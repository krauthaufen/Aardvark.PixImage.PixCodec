using System.Buffers.Binary;
using Xunit;
using static Aardvark.ImageCodecs.Tests.VolumeTestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// DICOM details from the standard (PS3.3, PS3.5), on small files built here: expectations from the spec's text, and from
/// pydicom (tests/oracle/dicom_read.py) where it is installed.
/// </summary>
public class DicomSpecTests
{
    /// <summary>A Secondary Capture file of the given pixel description; the data set elements come from <paramref name="body"/>.</summary>
    static byte[] Build(string ts, int w, int h, int spp, int bits, string photometric, Action<DicomWriter> pixels, int frames = 1,
                        int pixelRep = 0, Action<DicomWriter>? extra = null)
    {
        var d = new DicomWriter();
        const string instance = "2.25.329800735698586629295641978511506172918"; // (fixed: the same length in every file)
        d.String(DicomTags.SopClassUid, "UI", DicomUids.SecondaryCapture);
        d.String(DicomTags.SopInstanceUid, "UI", instance);
        d.Strings(DicomTags.SamplesPerPixel, "US", new double[] { spp });
        d.String(DicomTags.PhotometricInterpretation, "CS", photometric);
        if (spp > 1) d.Strings(DicomTags.PlanarConfiguration, "US", new double[] { 0 });
        if (frames > 1) d.Strings(DicomTags.NumberOfFrames, "IS", new double[] { frames });
        d.Strings(DicomTags.Rows, "US", new double[] { h });
        d.Strings(DicomTags.Columns, "US", new double[] { w });
        d.Strings(DicomTags.BitsAllocated, "US", new double[] { bits });
        d.Strings(DicomTags.BitsStored, "US", new double[] { bits });
        d.Strings(DicomTags.HighBit, "US", new double[] { bits - 1 });
        d.Strings(DicomTags.PixelRepresentation, "US", new double[] { pixelRep });
        extra?.Invoke(d);
        pixels(d);
        var ms = new MemoryStream();
        Dicom.WriteFile(d, ms, DicomUids.SecondaryCapture, instance, ts);
        return ms.ToArray();
    }

    /// <summary>pydicom's pixel_array of a file, or null without python / pydicom.</summary>
    static RawVolume? Pydicom(byte[] file)
    {
        if (!TestUtil.PythonAvailable) return null;
        var dir = TempDir();
        try
        {
            var f = Path.Combine(dir, "x.dcm");
            File.WriteAllBytes(f, file);
            var (exit, output) = TestUtil.RunPython($"dicom_read.py pydicom \"{f}\" \"{f}.out\"");
            if (TestUtil.CannotVerify(exit, output)) return null;
            Assert.True(exit == 0, output);
            return LoadVref(f + ".out.vref");
        }
        finally { Directory.Delete(dir, true); }
    }

    static byte[] RgbRamp(int w, int h)
    {
        var b = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 3;
                b[i] = (byte)(x * 255 / (w - 1)); b[i + 1] = (byte)(y * 255 / (h - 1)); b[i + 2] = (byte)((x * 7 + y * 13) % 256);
            }
        return b;
    }

    /// <summary>
    /// Code streams that keep their components as stored, labelled YBR_FULL (PS3.5 tables 8.2.1-2, 8.2.3-1, 8.2.4-1 allow
    /// it for lossless JPEG, JPEG-LS and JPEG 2000 without a multiple component transform): the components are Y, Cb, Cr
    /// and come out converted to RGB with the equations of PS3.3 C.7.6.3.1.2, as pydicom does. Labelled RGB they stay as
    /// they are.
    /// </summary>
    [Theory]
    [InlineData("jpegls")]
    [InlineData("jpeg-lossless")]
    [InlineData("j2k")]
    public void YbrFullCodestreamsAreConverted(string kind)
    {
        int w = 16, h = 12;
        var stored = RgbRamp(w, h);
        var img = new RawImage(w, h, ColorModel.Rgb, stored, 3);
        var ms = new MemoryStream();
        string ts;
        switch (kind)
        {
            case "jpegls":
                JpegLs.Encode(img, ms, new JpegLsEncodeOptions());
                ts = DicomUids.JpegLsLossless;
                break;
            case "jpeg-lossless":
                Jpeg.Encode(img, ms, new JpegEncodeOptions { Lossless = true, Predictor = 1 });
                ts = DicomUids.JpegLosslessSV1;
                break;
            default:
                Jp2.Encode(img, ms, new Jp2EncodeOptions { Lossless = true, RawCodestream = true, ColorTransform = false, WriteMetadata = false });
                ts = DicomUids.Jpeg2000Lossless;
                break;
        }
        var cs = ms.ToArray();
        if (kind == "jpeg-lossless") cs = PlainJpegComponents(cs);
        byte[] File(string photometric) => Build(ts, w, h, 3, 8, photometric, d => d.Encapsulated(DicomTags.PixelData, new[] { cs }));

        var rgb = Dicom.DecodeVolume(File("RGB"));
        Assert.Equal(stored, rgb.GetData<byte>());

        var ybrFile = File("YBR_FULL");
        var ybr = Dicom.DecodeVolume(ybrFile).GetData<byte>();
        for (int i = 0; i < w * h; i++)
        {
            double y = stored[3 * i], cb = stored[3 * i + 1] - 128, cr = stored[3 * i + 2] - 128;
            double[] e = { y + 1.402 * cr, y - 0.344136 * cb - 0.714136 * cr, y + 1.772 * cb };
            for (int c = 0; c < 3; c++) Assert.True(Math.Abs(Math.Clamp(e[c], 0, 255) - ybr[3 * i + c]) <= 1, $"pixel {i} component {c}: {ybr[3 * i + c]}, expected {e[c]:F1}");
        }
        var py = Pydicom(ybrFile);
        if (py != null) Assert.True(Doubles(py.Data).Zip(ybr).All(z => Math.Abs(z.First - z.Second) <= 1), "differs from pydicom");
    }

    /// <summary>Drops the Adobe segment of a JPEG and numbers its components 1, 2, 3: nothing in the stream says RGB.</summary>
    static byte[] PlainJpegComponents(byte[] s)
    {
        var b = s.ToList();
        if (b[2] == 0xFF && b[3] == 0xEE) b.RemoveRange(2, 2 + (b[4] << 8 | b[5]));
        var a = b.ToArray();
        for (int p = 2; p + 4 <= a.Length;)
        {
            int m = a[p + 1], len = a[p + 2] << 8 | a[p + 3];
            if (m is 0xC3 or 0xC0 or 0xC1) for (int k = 0; k < a[p + 9]; k++) a[p + 10 + 3 * k] = (byte)(k + 1);
            if (m == 0xDA) { for (int k = 0; k < a[p + 4]; k++) a[p + 5 + 2 * k] = (byte)(k + 1); break; }
            p += 2 + len;
        }
        return a;
    }

    /// <summary>
    /// RLE of 1 bit samples (binary segmentations, PS3.5 table 8.2.2-1): each sample is padded to a byte (G.2), so one
    /// segment holds a byte per pixel.
    /// </summary>
    [Fact]
    public void RleOneBit()
    {
        int w = 9, h = 3;
        var bits = new byte[w * h];
        for (int i = 0; i < bits.Length; i++) bits[i] = (byte)((i * 5 / 3) & 1);
        var frame = new List<byte>();
        var header = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 64);
        frame.AddRange(header);
        frame.Add((byte)(bits.Length - 1)); // one literal run (G.3.1)
        frame.AddRange(bits);
        var file = Build(DicomUids.RleLossless, w, h, 1, 1, "MONOCHROME2", d => d.Encapsulated(DicomTags.PixelData, new[] { frame.ToArray() }));
        var v = Dicom.DecodeVolume(file);
        Assert.Equal(SampleType.UInt8, v.SampleType);
        Assert.Equal(bits, v.GetData<byte>());
        var py = Pydicom(file);
        if (py != null) Assert.Equal(bits.Select(x => (double)x), Doubles(py.Data));
    }

    /// <summary>
    /// Per-frame Pixel Value Transformation (PS3.3 C.7.6.16.2.9) in the per-frame functional groups: every frame is
    /// rescaled with its own slope and intercept. (pydicom 3.0's apply_rescale reads no functional groups.)
    /// </summary>
    [Fact]
    public void PerFrameRescale()
    {
        int w = 3, h = 2, n = 3;
        var px = new byte[w * h * n * 2];
        for (int i = 0; i < w * h * n; i++) BinaryPrimitives.WriteUInt16LittleEndian(px.AsSpan(2 * i), (ushort)(i * 100));
        double[] slopes = { 1, 0.5, 2 }, intercepts = { 0, -10, 1000 };
        var file = Build(DicomUids.ExplicitVrLittleEndian, w, h, 1, 16, "MONOCHROME2", d => d.Bytes(DicomTags.PixelData, "OW", px), n, extra: d =>
        {
            var per = new DicomWriter[n];
            for (int k = 0; k < n; k++)
            {
                var pv = new DicomWriter();
                pv.Strings(DicomTags.RescaleIntercept, "DS", new[] { intercepts[k] });
                pv.Strings(DicomTags.RescaleSlope, "DS", new[] { slopes[k] });
                pv.String(DicomTags.RescaleType, "LO", "US");
                per[k] = new DicomWriter();
                per[k].Sequence(DicomTags.PixelValueTransformationSequence, new[] { pv });
            }
            d.Sequence(DicomTags.SharedFunctionalGroups, new[] { new DicomWriter() });
            d.Sequence(DicomTags.PerFrameFunctionalGroups, per);
        });
        var v = Dicom.DecodeVolume(file);
        Assert.Equal(SampleType.Float32, v.SampleType);
        Assert.Equal(SampleType.Float32, Dicom.GetVolumeInfo(file).SampleType);
        var f = v.GetData<float>();
        for (int i = 0; i < f.Length; i++)
        {
            int k = i / (w * h);
            Assert.Equal(i * 100 * slopes[k] + intercepts[k], f[i]);
        }
        Assert.True(v.Metadata!.ScalingApplied);
        // raw: the stored values
        var raw = Dicom.DecodeVolume(file, new DicomDecodeOptions { Scaling = DataScaling.Raw });
        Assert.Equal(Enumerable.Range(0, w * h * n).Select(i => (ushort)(i * 100)), raw.GetData<ushort>());
    }

    /// <summary>The header of float and double float pixel data (7FE0,0008 / 0009) tells their type before the pixels are read.</summary>
    [Theory]
    [InlineData(SampleType.Float32)]
    [InlineData(SampleType.Float64)]
    public void FloatPixelDataInfo(SampleType t)
    {
        var data = RawImage.CreateArray(t, 5 * 4 * 2);
        for (int i = 0; i < data.Length; i++) data.SetValue(t == SampleType.Float32 ? (object)(i * 0.25f - 3) : i * 0.125 - 2, i);
        var bytes = Dicom.Encode(new RawVolume(new[] { 5, 4, 2 }, ColorModel.Gray, data, 1));
        var info = Dicom.GetVolumeInfo(bytes);
        Assert.Equal(t, info.SampleType);
        Assert.Equal(t, Dicom.DecodeVolume(bytes).SampleType);
        Assert.Equal(t, ImageCodec.GetInfo(bytes).SampleType);
    }

    /// <summary>SV and UV have the reserved bytes and a 32 bit length in explicit VR (PS3.5 table 7.1-1), like OB.</summary>
    [Theory]
    [InlineData("SV")]
    [InlineData("UV")]
    public void SixtyFourBitVrs(string vr)
    {
        var file = Build(DicomUids.ExplicitVrLittleEndian, 2, 2, 1, 8, "MONOCHROME2", d => d.Bytes(DicomTags.PixelData, "OB", new byte[] { 1, 2, 3, 4 }), extra: d =>
        {
            d.String(0x00090010, "LO", "PIXCODEC TEST");
            var b = new byte[16];
            BinaryPrimitives.WriteInt64LittleEndian(b, vr == "SV" ? -5_000_000_000 : 7_000_000_000);
            BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(8), 42);
            d.Bytes(0x00091001, vr, b);
        });
        // the long form: tag, VR, two reserved bytes, 32 bit length
        int at = file.AsSpan().IndexOf(new byte[] { 0x09, 0x00, 0x01, 0x10 });
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 8)));
        var ds = Dicom.ReadDataset(file);
        Assert.Equal(new double[] { vr == "SV" ? -5_000_000_000 : 7_000_000_000, 42 }, ds.GetDoubles(0x00091001));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, Dicom.DecodeVolume(file).GetData<byte>());
    }

    /// <summary>
    /// A file whose 64 KB header prefix ends exactly between two elements, before Rows: the series scan reads on instead
    /// of taking the cut header for the whole one.
    /// </summary>
    [Fact]
    public void HeaderPrefixEndingBetweenElements()
    {
        byte[] Make(int pad) => Build(DicomUids.ExplicitVrLittleEndian, 4, 3, 1, 8, "MONOCHROME2", d => d.Bytes(DicomTags.PixelData, "OB", new byte[12]), extra: d =>
        {
            d.String(0x00090010, "LO", "PIXCODEC TEST");
            d.Bytes(0x00091002, "OB", new byte[pad]);
        });
        // size the private element so that the next element (the Pixel Module's first) starts at 64 KB
        var probe = Make(1000);
        int next = probe.AsSpan().IndexOf(new byte[] { 0x28, 0x00, 0x02, 0x00 });
        var file = Make(1000 + (1 << 16) - next);
        Assert.Equal(1 << 16, file.AsSpan().IndexOf(new byte[] { 0x28, 0x00, 0x02, 0x00 }));
        var dir = TempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "a.dcm"), file);
            var s = Assert.Single(Dicom.FindSeries(dir));
            Assert.Equal(new[] { 4, 3 }, s.Decode().Sizes);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ------------------------------------------------------------------ writer

    /// <summary>
    /// Multi-frame Secondary Capture: the SC Multi-frame Image module's Type 1 / 1C attributes (PS3.3 C.8.6.3: Burned In
    /// Annotation, and for MONOCHROME2 Presentation LUT Shape and the rescale), and with per-frame plane positions the
    /// Frame of Reference module and the Content Date / Time of the Multi-frame Functional Groups module (A.8.4.3, C.7.6.16).
    /// </summary>
    [Fact]
    public void MultiFrameScAttributes()
    {
        var data = new ushort[6 * 5 * 3];
        for (int i = 0; i < data.Length; i++) data[i] = (ushort)(i * 300);
        var m = new PhysicalMetadata
        {
            Space = "LPS", Spacing = new[] { 0.5, 0.5, 2.0 }, Origin = new[] { 1.0, 2.0, 3.0 },
            Direction = new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } }, Units = new string?[] { "mm", "mm", "mm" },
        };
        var ds = Dicom.ReadDataset(Dicom.Encode(new RawVolume(new[] { 6, 5, 3 }, ColorModel.Gray, data, 1, m)));
        Assert.Equal(DicomUids.MultiFrameGrayscaleWordSecondaryCapture, ds.GetString(DicomTags.SopClassUid));
        Assert.Equal("NO", ds.GetString(0x00280301));
        Assert.Equal("IDENTITY", ds.GetString(0x20500020));
        Assert.Equal(0, ds.GetDouble(DicomTags.RescaleIntercept));
        Assert.Equal(1, ds.GetDouble(DicomTags.RescaleSlope));
        Assert.Equal("US", ds.GetString(DicomTags.RescaleType));
        Assert.StartsWith("2.25.", ds.GetString(DicomTags.FrameOfReferenceUid));
        Assert.True(ds.Contains(0x00201040));
        Assert.Matches("^[0-9]{8}$", ds.GetString(0x00080023));
        Assert.Matches(@"^[0-9]{6}(\.[0-9]+)?$", ds.GetString(0x00080033));
        Assert.Equal(3, ds.GetSequence(DicomTags.PerFrameFunctionalGroups).Count);

        // RGB has no rescale or Presentation LUT Shape
        var rgb = Dicom.ReadDataset(Dicom.Encode(new RawVolume(new[] { 2, 2, 2 }, ColorModel.Rgb, new byte[24], 3)));
        Assert.Equal(DicomUids.MultiFrameTrueColorSecondaryCapture, rgb.GetString(DicomTags.SopClassUid));
        Assert.Equal("NO", rgb.GetString(0x00280301));
        Assert.False(rgb.Contains(0x20500020));
        Assert.False(rgb.Contains(DicomTags.RescaleSlope));
    }

    /// <summary>Baseline JPEG color is YBR_FULL_422 (or RGB), PS3.5 table 8.2.1-1: the writer subsamples the chroma 4:2:2.</summary>
    [Fact]
    public void JpegBaselineColorIsYbrFull422()
    {
        int w = 16, h = 12;
        var src = new RawImage(w, h, ColorModel.Rgb, RgbRamp(w, h), 3);
        var file = Dicom.Encode(src, new DicomEncodeOptions { Compression = DicomCompression.JpegBaseline, Quality = 95 });
        var ds = Dicom.ReadDataset(file);
        Assert.Equal("YBR_FULL_422", ds.GetString(DicomTags.PhotometricInterpretation));
        var cs = ds[DicomTags.PixelData]!.Fragments![1].Span;
        int sof = cs.IndexOf(new byte[] { 0xFF, 0xC0 });
        // component 1 sampled 2 x 1, components 2 and 3 1 x 1
        Assert.Equal(0x21, cs[sof + 11]);
        Assert.Equal(0x11, cs[sof + 14]);
        Assert.Equal(0x11, cs[sof + 17]);
        var back = Dicom.Decode(file);
        Assert.True(TestUtil.Psnr(src, back, 255) > 30);
    }
}
