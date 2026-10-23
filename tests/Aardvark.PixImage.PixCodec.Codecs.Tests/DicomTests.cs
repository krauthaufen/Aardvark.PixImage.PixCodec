using System.Text.Json;
using Xunit;
using static Aardvark.ImageCodecs.Tests.VolumeTestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>DICOM decoding against pydicom (tests/oracle/fetch_dicom_samples.py) and the synthetic series of gen_dicom.py.</summary>
public class DicomTests
{
    const string Samples = "downloads/dicom-samples";

    static IEnumerable<(string Path, JsonElement Json)> SampleFiles()
    {
        var dir = TestUtil.TestData(Samples);
        foreach (var j in Directory.GetFiles(dir, "*.dcm.json").OrderBy(f => f, StringComparer.Ordinal))
            yield return (j[..^5], JsonDocument.Parse(File.ReadAllText(j)).RootElement);
    }

    static bool Lossy(string? ts) => ts is DicomUids.JpegBaseline or DicomUids.JpegExtended or DicomUids.Jpeg2000 or DicomUids.HtJ2k or DicomUids.Jpeg2000Part2;

    static double MaxDiff(RawVolume e, RawVolume a)
    {
        var x = Doubles(e.Data); var y = Doubles(a.Data);
        double m = 0;
        for (int i = 0; i < x.Length; i++) m = Math.Max(m, Math.Abs(x[i] - y[i]));
        return m;
    }

    /// <summary>Every sample file decodes to pydicom's stored values (lossy ones within a few levels), palette and modality LUT included.</summary>
    [TestDataFact(Samples)]
    public void SamplesMatchPydicom()
    {
        var failures = new List<string>();
        int compared = 0;
        foreach (var (path, j) in SampleFiles())
        {
            var name = Path.GetFileName(path);
            var bytes = File.ReadAllBytes(path);
            bool image = j.TryGetProperty("image", out var im) && im.GetBoolean();
            string? ts = j.TryGetProperty("ts", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            try
            {
                if (j.TryGetProperty("gdcm", out _))
                {
                    // pydicom cannot decode it, GDCM can (rescale and palette applied)
                    var g = LoadVref(path + ".gdcm.vref.gz");
                    var v = Dicom.DecodeVolume(bytes);
                    if (v.SampleType == SampleType.UInt16 && g.SampleType == SampleType.UInt8)
                    {
                        // GDCM narrows 16 bit palettes to their high bytes; the descriptor (and pydicom) say 16 bits
                        var u = v.GetData<ushort>();
                        var b = new byte[u.Length];
                        for (int i = 0; i < u.Length; i++) b[i] = (byte)(u[i] >> 8);
                        v = new RawVolume(v.Sizes, v.Color, b, v.Channels);
                    }
                    if (!g.Sizes.SequenceEqual(v.Sizes) || g.Channels != v.Channels || MaxDiff(g, v) > (Lossy(ts) ? 8 : 0))
                        failures.Add($"{name}: differs from GDCM ({string.Join("x", v.Sizes)}x{v.Channels} {v.SampleType}, GDCM {string.Join("x", g.Sizes)}x{g.Channels} {g.SampleType})");
                    else compared++;
                    continue;
                }
                if (!image || j.TryGetProperty("error", out _) || j.TryGetProperty("parse_error", out _))
                {
                    // pydicom cannot decode these either: no crash, a clean exception or some image
                    try { Dicom.DecodeVolume(bytes); }
                    catch (Exception e) when (e is ImageFormatException or ImageNotSupportedException) { }
                    continue;
                }
                var expected = LoadVref(path + ".vref.gz");
                var raw = Dicom.DecodeVolume(bytes, new DicomDecodeOptions { Scaling = DataScaling.Raw, ApplyPalette = false });
                if (!expected.Sizes.SequenceEqual(raw.Sizes) || expected.Channels != raw.Channels || expected.SampleType != raw.SampleType)
                {
                    failures.Add($"{name}: {string.Join("x", raw.Sizes)}x{raw.Channels} {raw.SampleType}, pydicom {string.Join("x", expected.Sizes)}x{expected.Channels} {expected.SampleType}");
                    continue;
                }
                double d = MaxDiff(expected, raw);
                double tol = Lossy(ts) ? 8 : 0; // (lossy: other IDCT and upsampling than libjpeg / OpenJPEG)
                if (d > tol) { failures.Add($"{name}: max difference {d} ({ts})"); continue; }

                if (File.Exists(path + ".pal.vref.gz"))
                {
                    var pal = Dicom.DecodeVolume(bytes);
                    // pydicom 3.0.1 reads the palette words of big endian files byte swapped: those are checked against
                    // the little endian copy of the same image
                    var le = path.Replace("_expb", "");
                    var e = LoadVref((ts == DicomUids.ExplicitVrBigEndian && File.Exists(le + ".pal.vref.gz") ? le : path) + ".pal.vref.gz");
                    if (!e.Sizes.SequenceEqual(pal.Sizes) || e.Channels != pal.Channels || MaxDiff(e, pal) > tol) failures.Add($"{name}: palette differs");
                }
                if (File.Exists(path + ".mod.vref.gz"))
                {
                    var mod = Dicom.DecodeVolume(bytes, new DicomDecodeOptions { Scaling = DataScaling.Float64 });
                    var e = LoadVref(path + ".mod.vref.gz");
                    double md = MaxDiff(e, mod);
                    if (md > Math.Max(tol, 1e-6) * Math.Max(1, (mod.Metadata?.Slope ?? 1))) failures.Add($"{name}: rescaled values differ by {md}");
                }
                if (j.TryGetProperty("position", out var pos) && j.TryGetProperty("orientation", out _) && raw.Metadata?.Origin is { } origin)
                {
                    var p = pos.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                    if (raw.Depth == 1 && p.Zip(origin).Any(z => Math.Abs(z.First - z.Second) > 1e-6)) failures.Add($"{name}: origin differs");
                }
                compared++;
            }
            catch (Exception e)
            {
                failures.Add($"{name}: {e.GetType().Name}: {e.Message}");
            }
        }
        Assert.True(failures.Count == 0, $"{failures.Count} of {compared + failures.Count} files:\n" + string.Join("\n", failures));
        Assert.True(compared >= 130, $"only {compared} files compared");
    }

    // ------------------------------------------------------------------ synthetic fixtures (gen_dicom.py)

    static string Fx(string name) => TestUtil.Fixture(Path.Combine("dicom", name));

    static void AssertCase(string name, RawVolume v, double tol = 0)
    {
        AssertSame(LoadVref(Fx(name) + ".vref"), v, tol);
        AssertMetadata(Fx(name) + ".json", v);
    }

    [Fact]
    public void SeriesFromFolder()
    {
        var series = Dicom.FindSeries(Fx("series_ct"));
        // the structured report has no pixels; the localizer belongs to the series but is left out when stacking
        var s = Assert.Single(series);
        Assert.Equal(11, s.Files.Count);
        Assert.Equal("CT", s.Modality);
        Assert.Equal(2, s.SeriesNumber);
        AssertCase("series_ct", s.Decode());
        // the same from one of its files, from the folder, and through ImageCodec
        AssertCase("series_ct", Dicom.DecodeSeries(Path.Combine(Fx("series_ct"), "IM0004")));
        AssertCase("series_ct", ImageCodec.DecodeVolume(Fx("series_ct")));
        // one file alone is one slice
        var one = ImageCodec.DecodeVolume(Path.Combine(Fx("series_ct"), "IM0004"));
        Assert.Equal(new[] { 32, 24 }, one.Sizes);
        Assert.Equal(ImageFormat.Dicom, ImageCodec.DetectFormat(File.ReadAllBytes(Path.Combine(Fx("series_ct"), "IM0004"))));
    }

    [Fact]
    public void SeriesFromDicomDir()
    {
        using var map = JsonDocument.Parse(File.ReadAllText(Fx("cd.json")));
        foreach (var path in new[] { Fx("cd"), Path.Combine(Fx("cd"), "DICOMDIR") })
        {
            var series = Dicom.FindSeries(path);
            Assert.Equal(3, series.Count);
            foreach (var s in series)
            {
                var name = map.RootElement.GetProperty(s.SeriesNumber!.Value.ToString()).GetString()!;
                Assert.Equal("Test^Patient", s.PatientName);
                AssertCase(name, s.Decode(), name == "cd_pt" ? 1e-6 : 0);
            }
        }
        // the DICOMDIR itself decodes as its largest series (the 4D MR)
        AssertCase("cd_mr", ImageCodec.DecodeVolume(Path.Combine(Fx("cd"), "DICOMDIR")));
    }

    [Theory]
    [InlineData("enhanced.dcm")]
    [InlineData("rtdose.dcm")]
    [InlineData("encap_bot.dcm")]
    [InlineData("encap_frag.dcm")]
    [InlineData("encap_eot.dcm")]
    public void MultiFrame(string name)
    {
        var v = ImageCodec.DecodeVolume(Fx(name));
        AssertCase(name, v, name == "rtdose.dcm" ? 1e-12 : 0);
        var bytes = File.ReadAllBytes(Fx(name));
        var info = ImageCodec.GetInfo(bytes);
        Assert.Equal((ImageFormat.Dicom, v.Width, v.Height, v.Depth, v.Channels, v.SampleType), (info.Format, info.Width, info.Height, info.Depth, info.Channels, info.SampleType));
        using (var st = new InfoTests.CountingStream(new MemoryStream(bytes), seekable: false))
            Assert.Equal(info with { Metadata = null }, ImageCodec.GetInfo(st) with { Metadata = null });
        // the 2D path gives the first frame, DecodeAll every frame
        var first = ImageCodec.Decode(bytes);
        Assert.True(first.GetBytes().SequenceEqual(v.GetSlice(0).GetBytes()));
        Assert.Equal(v.Depth, ImageCodec.DecodeAll(bytes).Length);
        Assert.Equal(v.Depth, Dicom.DecodeAll(bytes, new DicomDecodeOptions { Parallel = false }).Length);
    }

    // ------------------------------------------------------------------ encoding

    static RawVolume Grid(SampleType t, int channels, int depth, int seed = 7)
    {
        int w = 37, h = 29;
        var rnd = new Random(seed);
        var data = RawImage.CreateArray(t, w * h * depth * channels);
        for (int i = 0; i < data.Length; i++)
        {
            // smooth gradients plus noise, over the type's range
            int x = i / channels % w, y = i / channels / w % h;
            double v = (x * 3 + y * 5 + rnd.Next(16)) / 260.0;
            object o = t switch
            {
                // (each arm boxed: a switch over numbers would widen them all to double)
                SampleType.UInt8 => (object)(byte)(v * 255), SampleType.Int8 => (object)(sbyte)(v * 255 - 128),
                SampleType.UInt16 => (object)(ushort)(v * 65535), SampleType.Int16 => (object)(short)(v * 4000 - 2000),
                SampleType.UInt32 => (object)(uint)(v * 4e9), SampleType.Int32 => (object)(int)(v * 2e9 - 1e9),
                SampleType.Float32 => (object)(float)(v * 3 - 1), _ => (object)(v * 7 - 2),
            };
            data.SetValue(o, i);
        }
        var color = channels == 3 ? ColorModel.Rgb : ColorModel.Gray;
        return new RawVolume(depth > 1 ? new[] { w, h, depth } : new[] { w, h }, color, data, channels);
    }

    public static IEnumerable<object[]> EncodeCases()
    {
        foreach (DicomCompression c in Enum.GetValues<DicomCompression>())
            foreach (var (t, ch) in new[] { (SampleType.UInt8, 1), (SampleType.UInt8, 3), (SampleType.UInt16, 1), (SampleType.Int16, 1), (SampleType.UInt16, 3),
                                            (SampleType.Int8, 1), (SampleType.UInt32, 1), (SampleType.Int32, 1), (SampleType.Float32, 1), (SampleType.Float64, 1) })
            {
                bool wide = t is SampleType.UInt32 or SampleType.Int32, flt = t is SampleType.Float32 or SampleType.Float64;
                if (flt && c != DicomCompression.None) continue;
                if (wide && c is not (DicomCompression.None or DicomCompression.Rle)) continue;
                if (c == DicomCompression.JpegBaseline && t != SampleType.UInt8) continue;
                if (t == SampleType.Int8 && c is not (DicomCompression.None or DicomCompression.Rle)) continue;
                foreach (int depth in new[] { 1, 3 }) yield return new object[] { c, t, ch, depth };
            }
    }

    /// <summary>Encoded files decode to the same samples, here and in pydicom (lossy JPEG: close).</summary>
    [Theory, MemberData(nameof(EncodeCases))]
    public void EncodeRoundTrip(DicomCompression c, SampleType t, int channels, int depth)
    {
        var v = Grid(t, channels, depth);
        var bytes = Dicom.Encode(v, new DicomEncodeOptions { Compression = c });
        Assert.Equal(ImageFormat.Dicom, ImageCodec.DetectFormat(bytes));
        var back = Dicom.DecodeVolume(bytes, new DicomDecodeOptions { Scaling = DataScaling.Raw });
        bool lossy = c == DicomCompression.JpegBaseline;
        Assert.Equal(v.Sizes, back.Sizes);
        Assert.Equal(v.Channels, back.Channels);
        Assert.Equal(v.SampleType, back.SampleType);
        if (lossy) Assert.True(TestUtil.Psnr(new RawImage(1, (int)v.Data.LongLength, ColorModel.Gray, v.Data), new RawImage(1, (int)back.Data.LongLength, ColorModel.Gray, back.Data), 255) > 35);
        else AssertSame(v, back);

        // pydicom (with pylibjpeg / pyjpegls for the compressed ones) reads the same
        if (!TestUtil.PythonAvailable) return;
        var dir = TempDir();
        try
        {
            var f = Path.Combine(dir, "x.dcm");
            File.WriteAllBytes(f, bytes);
            var (exit, output) = TestUtil.RunPython($"dicom_read.py pydicom \"{f}\" \"{f}.out\"");
            if (TestUtil.CannotVerify(exit, output)) return;
            Assert.True(exit == 0, output);
            var py = LoadVref(f + ".out.vref");
            Assert.Equal(v.Sizes, py.Sizes);
            if (lossy) return;
            AssertSame(v, py);
        }
        finally { Directory.Delete(dir, true); }
    }

    static PhysicalMetadata Oblique(int depth) => new()
    {
        Space = "LPS",
        Spacing = new[] { 0.7, 0.8, 2.5 },
        Origin = new[] { -100.25, 50.5, 12.0 },
        // rows along (0.8, 0.6, 0), columns along (0, 0, -1), slices along their cross product (-0.6, 0.8, 0): series are
        // stacked along that normal, as GDCM and ITK do
        Direction = new double[,] { { 0.8, 0, -0.6 }, { 0.6, 0, 0.8 }, { 0, -1, 0 } },
        Units = new string?[] { "mm", "mm", "mm" },
    };

    /// <summary>A volume written as a series of single-frame files comes back with its geometry, here and in GDCM (SimpleITK).</summary>
    [Fact]
    public void EncodeSeriesGeometry()
    {
        var src = Grid(SampleType.Int16, 1, 6);
        var v = new RawVolume(src.Sizes, src.Color, src.Data, 1, Oblique(6));
        var dir = TempDir();
        try
        {
            var files = Dicom.EncodeSeries(v, dir, new DicomEncodeOptions { Modality = "CT", SeriesDescription = "round trip" });
            Assert.Equal(6, files.Count);
            var series = Assert.Single(Dicom.FindSeries(dir));
            Assert.Equal("CT", series.Modality);
            Assert.Equal("round trip", series.SeriesDescription);
            var back = series.Decode();
            AssertSame(v, back);
            AssertMetadataEqual(v.Metadata!, back.Metadata!);

            if (!TestUtil.PythonAvailable) return;
            var (exit, output) = TestUtil.RunPython($"dicom_read.py sitk \"{dir}\" \"{dir}/sitk\"");
            if (TestUtil.CannotVerify(exit, output)) return;
            Assert.True(exit == 0, output);
            AssertSame(v, LoadVref(Path.Combine(dir, "sitk.vref")));
            AssertMetadata(Path.Combine(dir, "sitk.json"), back);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>A multi-frame file keeps the geometry in per-frame plane positions (pydicom sees them too) and the rescale.</summary>
    [Fact]
    public void EncodeMultiFrameGeometry()
    {
        var src = Grid(SampleType.UInt16, 1, 4);
        var m = Oblique(4);
        m.Slope = 0.5; m.Intercept = -1024; m.ValueUnit = "HU";
        var v = new RawVolume(src.Sizes, src.Color, src.Data, 1, m);
        var bytes = Dicom.Encode(v, new DicomEncodeOptions { Compression = DicomCompression.JpegLsLossless });
        var raw = Dicom.DecodeVolume(bytes, new DicomDecodeOptions { Scaling = DataScaling.Raw });
        AssertSame(v, raw);
        AssertMetadataEqual(m, raw.Metadata!);
        Assert.Equal(0.5, raw.Metadata!.Slope);
        Assert.Equal(-1024, raw.Metadata.Intercept);
        // with the default scaling the values come back rescaled
        var hu = Dicom.DecodeVolume(bytes);
        Assert.Equal(SampleType.Float32, hu.SampleType);
        Assert.Equal(src.GetData<ushort>()[5] * 0.5 - 1024, hu.GetData<float>()[5]);
    }

    static void AssertMetadataEqual(PhysicalMetadata e, PhysicalMetadata a)
    {
        Assert.Equal("LPS", a.Space);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(e.Spacing[i], a.Spacing[i], 6);
            Assert.Equal(e.Origin![i], a.Origin![i], 6);
            for (int j = 0; j < 3; j++) Assert.Equal(e.Direction![i, j], a.Direction![i, j], 6);
        }
    }
}
