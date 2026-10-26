using System.Text.Json;
using Xunit;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// NITF against what GDAL reads from the same files (tests/oracle/gen_nitf.py writes the fixtures and records GDAL's
/// samples, metadata, placement and TREs), and against MIL-STD-2500C where GDAL reads a file differently.
/// </summary>
public class NitfTests
{
    public static IEnumerable<object[]> Fixtures => Files("nitf", ".ntf");

    static JsonElement Json(string file) => JsonDocument.Parse(File.ReadAllText(Fixture(file) + ".json")).RootElement;

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [Theory, MemberData(nameof(Fixtures))]
    public void SamplesAreWhatGdalOrTheStandardSay(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        var j = Json(file);
        var gdal = j.GetProperty("gdal");
        bool reads = gdal.GetProperty("status").GetString() == "reads";
        bool lossy = j.TryGetProperty("lossy", out var l) && l.GetBoolean();
        var img = Nitf.Decode(data);

        if (lossy)
        {
            // JPEG: our IDCT against libjpeg's, within a few levels; reversible JPEG 2000: exact
            var g = LoadRef(Fixture(file) + ".gdal.ref");
            bool jpeg = file.Contains("jpeg");
            AssertEqual(g, img, jpeg ? 3 : 0);
        }
        else AssertEqual(LoadRef(Fixture(file) + ".ref"), img);

        // where GDAL reads the file another way than the standard, the sidecar says so, and GDAL's samples really differ
        if (reads && j.TryGetProperty("gdalAgrees", out var agrees) && !agrees.GetBoolean())
        {
            Assert.NotNull(Str(j, "gdalDiffers"));
            var g = LoadRef(Fixture(file) + ".gdal.ref");
            Assert.False(g.SampleType == img.SampleType && g.Channels == img.Channels && g.GetBytes().SequenceEqual(img.GetBytes()),
                $"{file}: recorded as read differently by GDAL, but GDAL's samples are ours");
        }

        var info = Nitf.GetInfo(data);
        Assert.Equal((img.Width, img.Height, img.Channels, img.SampleType, img.Color), (info.Width, info.Height, info.Channels, info.SampleType, info.Color));
        Assert.Equal(ImageFormat.Nitf, ImageCodec.DetectFormat(data));
        Assert.Equal(info, ImageCodec.GetInfo(data));
        AssertEqual(img, ImageCodec.Decode(data));
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void HeaderFieldsAndTresAreWhatGdalReads(string file)
    {
        var j = Json(file).GetProperty("gdal");
        if (j.GetProperty("status").GetString() != "reads") return;
        var f = Nitf.ReadFile(File.ReadAllBytes(Fixture(file)));
        var seg = f.Images[0];
        var tags = j.GetProperty("tags");
        void Tag(string name, string? ours)
        {
            if (tags.TryGetProperty(name, out var v)) Assert.Equal(v.GetString()!.Trim(), (ours ?? "").Trim());
        }
        Tag("NITF_FHDR", f.Version);
        Tag("NITF_FTITLE", f.Title);
        Tag("NITF_FSCLAS", f.Classification.ToString());
        Tag(f.IsVersion20 ? "NITF_IID" : "NITF_IID1", seg.Id);
        Tag(f.IsVersion20 ? "NITF_ITITLE" : "NITF_IID2", seg.Title);
        Tag("NITF_IREP", seg.Representation);
        Tag("NITF_ICAT", seg.Category);
        Tag("NITF_IC", seg.Compression);
        Tag("NITF_IMODE", seg.Mode.ToString());
        Tag("NITF_PVTYPE", seg.PixelValueType);
        Tag("NITF_PJUST", seg.Justification.ToString());
        Tag("NITF_ICORDS", seg.CoordinateSystem.ToString());
        Tag("NITF_IGEOLO", seg.GeoLocation);
        Tag("NITF_ABPP", seg.ActualBitsPerPixel.ToString("00"));
        if (tags.TryGetProperty("NITF_IMAGE_COMMENTS", out var com))
            Assert.Equal(com.GetString()!.Trim(), string.Join("", seg.Comments.Select(c => c.PadRight(80))).Trim());
        Assert.Equal(j.GetProperty("width").GetInt32(), seg.Columns);
        Assert.Equal(j.GetProperty("height").GetInt32(), seg.Rows);
        int count = j.GetProperty("count").GetInt32();
        Assert.Equal(count, seg.Bands.Length);
        if (j.GetProperty("subdatasets").GetInt32() > 0) Assert.Equal(j.GetProperty("subdatasets").GetInt32(), f.Images.Length);

        // GDAL lists the TREs of the file and of the image together
        var ours = f.Tres.Concat(seg.Tres).Select(t => t.Tag).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToArray();
        var theirs = j.GetProperty("tre").EnumerateArray().Select(t => t.GetString()!).ToArray();
        Assert.Equal(theirs, ours);
        Assert.Empty(f.Warnings);

        // a palette: GDAL shows the look-up tables as a colour map
        if (j.TryGetProperty("colormap", out var cm) && seg.Bands[0].LookUpTables.Length > 0)
        {
            var luts = seg.Bands[0].LookUpTables;
            var entries = cm.EnumerateArray().Select(e => e.EnumerateArray().Select(v => v.GetInt32()).ToArray()).ToArray();
            Assert.True(entries.Length >= luts[0].Length);
            for (int k = 0; k < luts[0].Length; k++)
                for (int c = 0; c < 3; c++)
                    Assert.Equal(entries[k][c], luts[luts.Length == 3 ? c : 0][k]);
        }
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void PlacementIsWhatGdalReads(string file)
    {
        var j = Json(file).GetProperty("gdal");
        if (j.GetProperty("status").GetString() != "reads") return;
        var data = File.ReadAllBytes(Fixture(file));
        var seg = Nitf.ReadFile(data).Images[0];
        var geo = Nitf.ReadGeo(seg);
        Assert.Equal(geo?.ToString(), Nitf.Decode(data).Metadata?.Geo?.ToString());
        var t = j.GetProperty("transform").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        bool identity = t.SequenceEqual(new[] { 1.0, 0, 0, 0, 1, 0 });
        int gcps = j.GetProperty("gcps").GetArrayLength();
        if (identity)
        {
            // no grid: either no corners at all, or corners that GDAL can only hand out as control points
            Assert.Null(geo);
            // MGRS corners (ICORDS U) GDAL converts to control points; this reader does not interpret them
            if (gcps == 4 && seg.CoordinateSystem != 'U')
            {
                var corners = Nitf.ReadCorners(seg)!.Value.Corners;
                int k = 0;
                foreach (var g in j.GetProperty("gcps").EnumerateArray())
                {
                    var v = g.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                    // GDAL puts the corner points at pixel centres (col + 0.5, row + 0.5)
                    Assert.Equal((v[2], v[3]), (corners[k].X, corners[k].Y));
                    k++;
                }
            }
            return;
        }
        Assert.NotNull(geo);
        // the affine maps grid positions to cell corners, as GDAL's geotransform does: the two agree
        Assert.Equal(GeoRasterType.PixelIsArea, geo!.RasterType);
        double tol = seg.CoordinateSystem is 'N' or 'S' ? 1e-6 : 1e-9;
        for (int k = 0; k < 6; k++) Assert.Equal(t[k], geo.Affine[k], tol);
        Assert.Equal(Str(j, "crs"), "EPSG:" + geo.Crs!.Epsg);
    }

    [Fact]
    public void LookUpTablesCanBeLeftUnapplied()
    {
        foreach (var file in new[] { "nitf/g_lut.ntf", "nitf/p_lut_rgb.ntf", "nitf/p_lut_grey.ntf" })
        {
            var data = File.ReadAllBytes(Fixture(file));
            var idx = Nitf.Decode(data, new NitfDecodeOptions { ApplyLookUpTables = false });
            // GDAL returns the indices
            AssertEqual(LoadRef(Fixture(file) + ".gdal.ref"), idx);
            Assert.Equal(ColorModel.Gray, idx.Color);
        }
    }

    [Fact]
    public void RgbBandsComeBackInColourOrder()
    {
        // GDAL keeps the file's band order (B, G, R) and names each band's colour; we return R, G, B
        var file = "nitf/p_rgb_bgr.ntf";
        var img = Nitf.Decode(File.ReadAllBytes(Fixture(file)));
        var g = LoadRef(Fixture(file) + ".gdal.ref");
        var interp = Json(file).GetProperty("gdal").GetProperty("colorinterp").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "blue", "green", "red" }, interp);
        Assert.Equal(ColorModel.Rgb, img.Color);
        var a = img.GetData<byte>(); var b = g.GetData<byte>();
        for (int p = 0; p < img.Width * img.Height; p++)
            for (int c = 0; c < 3; c++) Assert.Equal(b[p * 3 + (2 - c)], a[p * 3 + c]);
    }

    [Fact]
    public void EveryImageSegmentAndEveryTreIsThere()
    {
        var data = File.ReadAllBytes(Fixture("nitf/p_two_images.ntf"));
        var f = Nitf.ReadFile(data);
        Assert.Equal(2, f.Images.Length);
        Assert.Equal(new[] { "FILET1" }, f.Tres.Select(t => t.Tag));
        // IMGTR1 from the subheader, OVERFL from the TRE_OVERFLOW extension that names IXSHD of image 1 (5.8.3.1)
        Assert.Equal(new[] { "IMGTR1", "OVERFL" }, f.Images[0].Tres.Select(t => t.Tag));
        Assert.Equal("from the overflow", f.Images[0].Tres[1].Text);
        Assert.Equal("TRE_OVERFLOW", f.OtherSegments.Single().DesId);
        var all = Nitf.DecodeAll(data);
        Assert.Equal(2, all.Length);
        AssertEqual(LoadRef(Fixture("nitf/p_two_images.ntf") + ".second.ref"), new RawImage(all[1].Width, all[1].Height, ColorModel.Other, all[1].Data, 3));
        Assert.Equal(ColorModel.Rgb, all[1].Color);
        AssertEqual(all[1], Nitf.Decode(data, new NitfDecodeOptions { ImageIndex = 1 }));
        Assert.Equal(2, ImageCodec.DecodeAll(data).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => Nitf.Decode(data, new NitfDecodeOptions { ImageIndex = 2 }));
    }

    [Fact]
    public void SegmentsAfterTheImageArePlaced()
    {
        var data = File.ReadAllBytes(Fixture("nitf/g_textseg.ntf"));
        var t = Nitf.ReadFile(data).OtherSegments.Single();
        Assert.Equal(NitfSegmentKind.Text, t.Kind);
        Assert.Equal("TE", System.Text.Encoding.ASCII.GetString(data, (int)t.SubheaderOffset, 2));
        Assert.Equal("hello text segment", System.Text.Encoding.ASCII.GetString(data, (int)t.DataOffset, (int)t.DataLength));
        Assert.Equal(data.Length, t.DataOffset + t.DataLength);
    }

    [Fact]
    public void Nitf20HeadersAreRead()
    {
        var f = Nitf.ReadFile(File.ReadAllBytes(Fixture("nitf/p_nitf20.ntf")));
        Assert.True(f.IsVersion20);
        Assert.Equal("a NITF 2.0 file", f.Title);
        var s = f.Images[0];
        Assert.Equal("ITITLE of a 2.0 file", s.Title);
        Assert.Equal(new[] { "a NITF 2.0 comment" }, s.Comments);
        Assert.NotNull(f.Fields["FSDWNG"]);
        Assert.Null(f.Fields["FBKGC"]);
    }

    [Fact]
    public void CornersThatAreNoGridOrNotUnderstoodGiveNoPlacement()
    {
        foreach (var file in new[] { "nitf/p_geo_trapezoid.ntf", "nitf/p_geo_mgrs.ntf" })
        {
            var data = File.ReadAllBytes(Fixture(file));
            Assert.Null(Nitf.Decode(data).Metadata!.Geo);
            Assert.NotNull(Nitf.ReadFile(data).Images[0].GeoLocation);
        }
        // seconds left blank are zero: 50 00 N, 10 00 E .. 49 30 N, 10 30 E
        var g = Nitf.ReadGeo(Nitf.ReadFile(File.ReadAllBytes(Fixture("nitf/p_geo_blank_seconds.ntf"))).Images[0])!;
        // the corners are cell centres: (0, 0) is the centre of the first cell, (30, 20) that of the last
        Assert.Equal((10.0, 50.0), (Math.Round(g.ToWorld(0.5, 0.5).X, 12), Math.Round(g.ToWorld(0.5, 0.5).Y, 12)));
        Assert.Equal((10.5, 49.5), (Math.Round(g.ToWorld(30.5, 20.5).X, 12), Math.Round(g.ToWorld(30.5, 20.5).Y, 12)));
    }

    // ------------------------------------------------------------------ writing

    /// <summary>GDAL's reading of a file we wrote (tests/oracle/nitf_read.py), null when GDAL is not there.</summary>
    static (RawImage? Samples, JsonElement Report)? GdalRead(byte[] file)
    {
        if (!PythonAvailable) return null;
        var tmp = Path.Combine(FixtureDir, "_tmp");
        Directory.CreateDirectory(tmp);
        var f = Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".ntf");
        try
        {
            File.WriteAllBytes(f, file);
            var (exit, output) = RunPython($"nitf_read.py \"{f}\" \"{f}.out\"");
            if (CannotVerify(exit, output)) return null;
            Assert.True(exit == 0, "nitf_read.py failed: " + output);
            var rep = JsonDocument.Parse(File.ReadAllText(f + ".out.json")).RootElement.Clone();
            return (File.Exists(f + ".out.ref") ? LoadRef(f + ".out.ref") : null, rep);
        }
        finally
        {
            foreach (var p in new[] { f, f + ".out.ref", f + ".out.json", f + ".aux.xml" }) File.Delete(p);
        }
    }

    static RawImage Pattern(int w, int h, int c, SampleType t, int seed)
    {
        var img = RawImage.Create(w, h, c == 3 ? ColorModel.Rgb : c == 1 ? ColorModel.Gray : ColorModel.Other, t, c);
        var rng = new Random(seed);
        var b = img.GetBytes();
        rng.NextBytes(b);
        if (t == SampleType.Float32) { var a = img.GetData<float>(); for (int i = 0; i < a.Length; i++) a[i] = (float)(rng.NextDouble() * 200 - 100); }
        if (t == SampleType.Float64) { var a = img.GetData<double>(); for (int i = 0; i < a.Length; i++) a[i] = rng.NextDouble() * 2e6 - 1e6; }
        return img;
    }

    public static IEnumerable<object[]> WriteCases()
    {
        foreach (var t in new[] { SampleType.UInt8, SampleType.UInt16, SampleType.UInt32, SampleType.Int8, SampleType.Int16, SampleType.Int32, SampleType.Float32, SampleType.Float64 })
            yield return new object[] { t, 3, 'B', 0 };
        foreach (var m in new[] { 'B', 'P', 'R', 'S' })
        {
            yield return new object[] { SampleType.UInt8, 3, m, 16 };
            yield return new object[] { SampleType.UInt16, 4, m, 16 };
            yield return new object[] { SampleType.Int16, 2, m, 0 };
        }
        yield return new object[] { SampleType.UInt8, 1, 'B', 16 };
        yield return new object[] { SampleType.UInt8, 12, 'P', 0 };
        yield return new object[] { SampleType.Float32, 11, 'S', 16 };
    }

    [Theory, MemberData(nameof(WriteCases))]
    public void WhatWeWriteGdalReadsTheSame(SampleType type, int channels, char mode, int block)
    {
        var img = Pattern(37, 29, channels, type, channels * 31 + mode);
        var file = Nitf.Encode(img, new NitfEncodeOptions { Mode = mode, BlockWidth = block, BlockHeight = block, DateTime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) });
        var back = Nitf.Decode(file);
        AssertEqual(img, back);
        Assert.Equal(img.Color, back.Color);
        var seg = Nitf.ReadFile(file).Images[0];
        char written = channels == 1 || mode == 'S' && block == 0 ? 'B' : mode;
        Assert.Equal(written, seg.Mode);
        var g = GdalRead(file);
        if (g == null) return;
        var (samples, rep) = g.Value;
        Assert.Equal("reads", rep.GetProperty("status").GetString());
        // GDAL (3.12) hands out 8 bit SI samples as uint8: compare the stored bytes
        Assert.True(img.GetBytes().SequenceEqual(samples!.GetBytes()), "GDAL reads other samples");
        Assert.Equal((img.Width, img.Height, img.Channels), (samples.Width, samples.Height, samples.Channels));
        if (type != SampleType.Int8) Assert.Equal(type, samples.SampleType);
        Assert.Equal(written.ToString(), rep.GetProperty("tags").GetProperty("NITF_IMODE").GetString());
        Assert.Equal(channels == 3 ? "RGB" : channels == 1 ? "MONO" : "MULTI", rep.GetProperty("tags").GetProperty("NITF_IREP").GetString());
    }

    [Fact]
    public void HeaderTextCommentsAndTresSurvive()
    {
        var img = Pattern(9, 7, 1, SampleType.UInt16, 5);
        var a = img.GetData<ushort>();
        for (int i = 0; i < a.Length; i++) a[i] &= 0x0FFF;
        var o = new NitfEncodeOptions
        {
            Title = "a title", ImageId = "IMG01", ImageTitle = "an image title", Category = "IR", ActualBitsPerPixel = 12,
            Comments = { "first comment", "second comment" },
            FileTres = { new NitfTre("FILEAA", "file tre data"u8.ToArray()) },
            ImageTres = { new NitfTre("IMGBBB", "image tre data"u8.ToArray()), new NitfTre("IMGCCC", new byte[] { 0, 1, 2 }) },
        };
        var file = Nitf.Encode(img, o);
        var f = Nitf.ReadFile(file);
        Assert.Empty(f.Warnings);
        Assert.Equal("a title", f.Title);
        var s = f.Images[0];
        Assert.Equal(("IMG01", "an image title", "IR", 12), (s.Id, s.Title, s.Category, s.ActualBitsPerPixel));
        Assert.Equal(new[] { "first comment", "second comment" }, s.Comments);
        Assert.Equal(new[] { "FILEAA" }, f.Tres.Select(t => t.Tag));
        Assert.Equal(new[] { "IMGBBB", "IMGCCC" }, s.Tres.Select(t => t.Tag));
        Assert.Equal(new byte[] { 0, 1, 2 }, s.Tres[1].Data);
        AssertEqual(img, Nitf.Decode(file));
        Assert.Throws<ArgumentException>(() => Nitf.Encode(Pattern(9, 7, 1, SampleType.UInt16, 6), o));   // samples above 12 bits

        var g = GdalRead(file);
        if (g == null) return;
        var tags = g.Value.Report.GetProperty("tags");
        Assert.Equal("a title", tags.GetProperty("NITF_FTITLE").GetString());
        Assert.Equal("IMG01", tags.GetProperty("NITF_IID1").GetString());
        Assert.Equal("12", tags.GetProperty("NITF_ABPP").GetString());
        Assert.Equal("first comment", tags.GetProperty("NITF_IMAGE_COMMENTS").GetString()!.Substring(0, 80).Trim());
        Assert.Equal(new[] { "FILEAA", "IMGBBB", "IMGCCC" }, g.Value.Report.GetProperty("tre").EnumerateArray().Select(e => e.GetString()));
        AssertEqual(img, g.Value.Samples!);
    }

    public static IEnumerable<object[]> PlacementCases()
    {
        // (EPSG, affine of an area grid, decimal degrees, precision of IGEOLO in world units)
        yield return new object[] { 4326, new[] { 0.01, 0, 10.0, 0, -0.01, 50.0 }, false, 1.0 / 7200 };
        yield return new object[] { 4326, new[] { 0.025, 0, -74.5, 0, -0.025, -20.25 }, true, 0.0005 };
        yield return new object[] { 4326, new[] { 0.01, 0.002, 10.0, 0.001, -0.01, 50.0 }, false, 1.0 / 7200 };
        yield return new object[] { 32633, new[] { 10.0, 0, 500000, 0, -10.0, 5000000 }, false, 0.5 };
        yield return new object[] { 32719, new[] { 30.0, 0, 300000, 0, -30.0, 7000000 }, false, 0.5 };
    }

    [Theory, MemberData(nameof(PlacementCases))]
    public void PlacementIsWrittenAsCornersGdalReads(int epsg, double[] affine, bool decimalDegrees, double precision)
    {
        var img = Pattern(53, 37, 1, SampleType.UInt8, epsg);
        var geo = new GeoReference { Affine = affine, Crs = new GeoCrs { Epsg = epsg } };
        var file = Nitf.Encode(img, new NitfEncodeOptions { Geo = geo, DecimalDegrees = decimalDegrees });
        var seg = Nitf.ReadFile(file).Images[0];
        Assert.Equal(epsg == 4326 ? (decimalDegrees ? 'D' : 'G') : epsg > 32700 ? 'S' : 'N', seg.CoordinateSystem);
        var back = Nitf.ReadGeo(seg)!;
        Assert.Equal(epsg, back.Crs!.Epsg);
        var (x0, y0) = back.ToWorld(0, 0);
        var (x1, y1) = back.ToWorld(53, 37);
        Assert.Equal(affine[2], x0, 2 * precision);
        Assert.Equal(affine[5], y0, 2 * precision);
        Assert.Equal(affine[0] * 53 + affine[1] * 37 + affine[2], x1, 2 * precision);
        Assert.Equal(affine[3] * 53 + affine[4] * 37 + affine[5], y1, 2 * precision);

        var g = GdalRead(file);
        if (g == null) return;
        var rep = g.Value.Report;
        Assert.Equal("EPSG:" + epsg, rep.GetProperty("crs").GetString());
        var t = rep.GetProperty("transform").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        // GDAL's grid from the same corners agrees with what was written within the precision IGEOLO keeps
        Assert.Equal(affine[2], t[2], 2 * precision);
        Assert.Equal(affine[5], t[5], 2 * precision);
        foreach (int k in new[] { 0, 1, 3, 4 }) Assert.Equal(affine[k], t[k], 4 * precision / 36);
        for (int k = 0; k < 6; k++) Assert.Equal(t[k], back.Affine[k], 1e-6 * Math.Max(1, Math.Abs(t[k])));
    }

    [Fact]
    public void PlacementsIgeoloHoldsExactlyComeBackExactly()
    {
        // corner centres on whole metres / whole arc seconds: written and read back without a change
        var img = Pattern(53, 37, 1, SampleType.UInt8, 3);
        foreach (var (epsg, affine) in new[] { (32633, new[] { 10.0, 0, 500000, 0, -10.0, 5000000 }), (4326, new[] { 2.0 / 3600, 0, 10.0, 0, -2.0 / 3600, 50.0 }) })
        {
            var geo = new GeoReference { Affine = affine, Crs = new GeoCrs { Epsg = epsg } };
            var back = Nitf.Decode(Nitf.Encode(img, new NitfEncodeOptions { Geo = geo })).Metadata!.Geo!;
            for (int k = 0; k < 6; k++) Assert.Equal(affine[k], back.Affine[k], 1e-12 * Math.Max(1, Math.Abs(affine[k])));
            Assert.Equal(epsg, back.Crs!.Epsg);
        }
    }

    [Fact]
    public void WhatTheStandardCannotHoldIsRefused()
    {
        var img = Pattern(9, 7, 1, SampleType.UInt8, 1);
        var lambert = new GeoReference { Affine = new[] { 1.0, 0, 0, 0, -1, 0 }, Crs = new GeoCrs { Epsg = 31287 } };
        Assert.Throws<ImageNotSupportedException>(() => Nitf.Encode(img, new NitfEncodeOptions { Geo = lambert }));
        Assert.Throws<ImageNotSupportedException>(() => Nitf.Encode(img, new NitfEncodeOptions { Geo = new GeoReference { Affine = new[] { 1.0, 0, 0, 0, -1, 0 } } }));
        Assert.Throws<ImageNotSupportedException>(() => Nitf.Encode(RawImage.Create(4, 4, ColorModel.Gray, SampleType.Float16, 1)));
        Assert.Throws<ArgumentException>(() => Nitf.Encode(img, new NitfEncodeOptions { Title = new string('x', 81) }));
        Assert.Throws<ArgumentException>(() => Nitf.Encode(img, new NitfEncodeOptions { BlockWidth = 9000, BlockHeight = 9000 }));
        // the placement of a decoded image goes back into the file it is written to
        var placed = Nitf.Decode(File.ReadAllBytes(Fixture("nitf/g_utm_n.ntf")));
        var again = Nitf.ReadGeo(Nitf.ReadFile(Nitf.Encode(placed)).Images[0])!;
        Assert.Equal(placed.Metadata!.Geo!.Affine, again.Affine);
        Assert.Equal(placed.Metadata.Geo.Crs!.Epsg, again.Crs!.Epsg);
    }

    // ------------------------------------------------------------------ the NITFS Technical Board's sample files

    /// <summary>
    /// The NTB / JITC sample files of NITF 2.0, 2.1 and NSIF 1.0 (test-data repository, fetch/fetch_nitf_samples.sh)
    /// against tests/oracle/gen_nitf_samples.py's refs.txt: GDAL's samples, or where GDAL reads a file differently from
    /// MIL-STD-2500C (packed 12 bit samples, RGB JPEG) an independent reading of the standard, and GDAL's own samples
    /// recorded as differing. Segments the reader refuses (bi-level and VQ compression, JPEG with the default tables of
    /// MIL-STD-188-198A, NITF 1.1) have to be refused with the exception refs.txt names.
    /// </summary>
    [TestDataFact("fixtures/nitf/jitc/refs.txt", "fixtures/nitf/jitc/U_1001a.ntf")]
    public void TechnicalBoardSamples()
    {
        var dir = TestData(Path.Combine("fixtures", "nitf", "jitc"));
        int checkedSegments = 0;
        foreach (var line in File.ReadAllLines(Path.Combine(dir, "refs.txt")))
        {
            var f = line.Split(' ', 9);
            var path = Path.Combine(dir, f[0]);
            Assert.True(File.Exists(path), f[0] + " missing: run fetch/fetch_nitf_samples.sh of the test-data repository again");
            var data = File.ReadAllBytes(path);
            var o = new NitfDecodeOptions { ImageIndex = int.Parse(f[1]) };
            string what = $"{f[0]} image {f[1]}";
            if (f[7].StartsWith("unsupported:"))
            {
                var e = Record.Exception(() => Nitf.Decode(data, o));
                Assert.True(e != null && e.GetType().Name == f[7]["unsupported:".Length..], $"{what}: {e?.GetType().Name ?? "no exception"}, {f[7]} expected ({f[8]})");
                continue;
            }
            var img = Nitf.Decode(data, o);
            Assert.True((int.Parse(f[2]), int.Parse(f[3]), int.Parse(f[4])) == (img.Width, img.Height, img.Channels), what + ": size");
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(img.GetBytes())).ToLowerInvariant();
            Assert.True(f[6] == sha, $"{what}: samples differ from {f[7]}");
            // where the standard is the reference, GDAL really reads the file another way
            if (f[7] == "standard") Assert.NotEqual(f[6], f[8]);
            checkedSegments++;
        }
        Assert.True(checkedSegments >= 40, $"only {checkedSegments} segments compared");
    }

    [Fact]
    public void JpegFillBytesBeforeMarkersAreSkipped()
    {
        // ISO/IEC 10918-1 B.1.1.2: any marker may be preceded by fill bytes 0xFF, SOI and the restart markers included
        // (the NTB sample U_4022a has them in front of SOI)
        var file = File.ReadAllBytes(Fixture("nitf/g_jpeg_rgb.ntf"));
        var seg = Nitf.ReadFile(file).Images[0];
        var src = Nitf.Decode(file);
        var jpeg = Jpeg.Encode(src, new JpegEncodeOptions { RestartInterval = 1, Quality = 95 });
        int rst = jpeg.AsSpan().IndexOf(new byte[] { 0xFF, 0xD0 });
        Assert.True(rst > 0);
        var stream = new byte[] { 0xFF, 0xFF, 0xFF }.Concat(jpeg[..rst]).Concat(new byte[] { 0xFF, 0xFF }).Concat(jpeg[rst..]).ToArray();
        var patched = file[..(int)seg.DataOffset].Concat(stream).ToArray();
        void Field(int at, int width, long v) => System.Text.Encoding.ASCII.GetBytes(v.ToString(new string('0', width))).CopyTo(patched, at);
        Field(342, 12, patched.Length);   // FL
        Field(369, 10, stream.Length);    // LI of the one image segment
        AssertEqual(Jpeg.Decode(jpeg), Nitf.Decode(patched));
    }

    [Fact]
    public void BrokenFilesAreNamed()
    {
        var data = File.ReadAllBytes(Fixture("nitf/g_blocked.ntf"));
        Assert.Throws<ImageFormatException>(() => Nitf.Decode(data.AsSpan(0, data.Length - 10)));
        Assert.Throws<ImageFormatException>(() => Nitf.ReadFile(data.AsSpan(0, 300)));
        Assert.Throws<ImageNotSupportedException>(() => Nitf.Decode(data, new NitfDecodeOptions { MaxPixels = 100 }));
        var enc = (byte[])data.Clone();
        // ENCRYP of the image subheader follows IM, IID1, IDATIM, TGTID, IID2 and the 167 security bytes
        int hl = int.Parse(System.Text.Encoding.ASCII.GetString(data, 354, 6));
        Assert.Equal((byte)'0', enc[hl + 2 + 10 + 14 + 17 + 80 + 167]);
        enc[hl + 2 + 10 + 14 + 17 + 80 + 167] = (byte)'1';
        Assert.Throws<ImageNotSupportedException>(() => Nitf.Decode(enc));
        var bad = (byte[])data.Clone();
        bad[0] = (byte)'X';
        Assert.Throws<ImageFormatException>(() => Nitf.ReadFile(bad));
    }
}
