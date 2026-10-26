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
        Assert.Equal(GeoRasterType.PixelIsPoint, geo!.RasterType);
        // GDAL's area grid is the same placement shifted by half a pixel: its corner is our position (-0.5, -0.5)
        var (x0, y0) = geo.ToWorld(-0.5, -0.5);
        double tol = seg.CoordinateSystem is 'N' or 'S' ? 1e-6 : 1e-9;
        Assert.Equal(t[2], x0, tol);
        Assert.Equal(t[5], y0, tol);
        Assert.Equal(t[0], geo.Affine[0], tol); Assert.Equal(t[1], geo.Affine[1], tol);
        Assert.Equal(t[3], geo.Affine[3], tol); Assert.Equal(t[4], geo.Affine[4], tol);
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
        Assert.Equal((10.0, 50.0), g.ToWorld(0, 0));
        Assert.Equal((10.5, 49.5), (Math.Round(g.ToWorld(30, 20).X, 12), Math.Round(g.ToWorld(30, 20).Y, 12)));
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
