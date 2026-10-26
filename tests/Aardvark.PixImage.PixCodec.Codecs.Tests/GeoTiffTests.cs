using System.Text.Json;
using Xunit;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// GeoTIFF georeferencing. The fixtures (tests/oracle/gen_geotiff.py) carry what GDAL and tifffile report for each file;
/// files we write are read back by GDAL (rasterio) and tifffile.
/// </summary>
public class GeoTiffTests
{
    public static IEnumerable<object[]> Fixtures => TestUtil.Files("geotiff", ".tif");

    static JsonElement Json(string file) => JsonDocument.Parse(File.ReadAllText(TestUtil.Fixture(file) + ".json")).RootElement;

    static double[]? Doubles(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.EnumerateArray().Select(x => x.GetDouble()).ToArray();

    static void Near(double expected, double actual, string what)
        => Assert.True(Math.Abs(expected - actual) <= 1e-9 * Math.Max(1, Math.Abs(expected)), $"{what}: expected {expected}, got {actual}");

    static void Near(double[] expected, double[] actual, string what)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) Near(expected[i], actual[i], $"{what}[{i}]");
    }

    // rasterio's Affine order (a, b, c, d, e, f) is GeoReference.Affine's
    static (double X, double Y) Apply(double[] a, double col, double row) => (a[0] * col + a[1] * row + a[2], a[3] * col + a[4] * row + a[5]);

    static bool IsIdentity(double[] a) => a.SequenceEqual(new double[] { 1, 0, 0, 0, 1, 0 });

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void FixturesReadAsGdalAndTifffileSeeThem(string file)
    {
        var data = File.ReadAllBytes(TestUtil.Fixture(file));
        var j = Json(file);
        var raw = j.GetProperty("raw");
        var std = j.GetProperty("gdal").GetProperty("standard");
        var def = j.GetProperty("gdal").GetProperty("default");

        // the samples, as stored
        TestUtil.AssertEqual(TestUtil.LoadRef(TestUtil.Fixture(file) + ".ref"), Tiff.Decode(data, new TiffDecodeOptions { ApplyOrientation = false }));

        var info = Tiff.ReadGeo(data);
        bool anyTag = raw.GetProperty("pixelScale").ValueKind != JsonValueKind.Null || raw.GetProperty("tiepoints").ValueKind != JsonValueKind.Null
            || raw.GetProperty("modelTransformation").ValueKind != JsonValueKind.Null || raw.GetProperty("keys").ValueKind != JsonValueKind.Null
            || raw.GetProperty("noData").ValueKind != JsonValueKind.Null;
        if (!anyTag)
        {
            Assert.Null(info);
            Assert.Null(Tiff.Decode(data).Metadata?.Geo);
            return;
        }
        Assert.NotNull(info);

        // the tags as tifffile lists them
        Assert.Equal(Doubles(raw.GetProperty("pixelScale")), info!.PixelScale);
        Assert.Equal(Doubles(raw.GetProperty("tiepoints")) ?? Array.Empty<double>(), info.TiePoints.SelectMany(t => t).ToArray());
        Assert.Equal(Doubles(raw.GetProperty("modelTransformation")), info.ModelTransformation);
        Assert.Equal(raw.GetProperty("noData").ValueKind == JsonValueKind.Null ? null : raw.GetProperty("noData").GetString(), info.NoDataText);
        if (raw.GetProperty("keys").ValueKind != JsonValueKind.Null)
        {
            var dir = raw.GetProperty("keyDirectory").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            var want = raw.GetProperty("keys").EnumerateArray().ToArray();
            // requirement 2.11: a directory announcing more entries than it holds is broken and read as holding none
            bool broken = dir[3] > want.Length;
            Assert.Equal(broken ? 0 : want.Length, info.Keys.Count);
            if (!broken)
            {
                Assert.Equal((dir[0], dir[1], dir[2]), info.KeyDirectoryVersion);
                for (int i = 0; i < want.Length; i++)
                {
                    var w = want[i]; var k = info.Keys[i];
                    Assert.Equal(w.GetProperty("id").GetInt32(), k.Id);
                    Assert.Equal(w.GetProperty("location").GetInt32(), k.Location);
                    if (w.TryGetProperty("shorts", out var s)) Assert.Equal(s.EnumerateArray().Select(x => (ushort)x.GetInt32()).ToArray(), k.Shorts);
                    if (w.TryGetProperty("doubles", out var dd)) Assert.Equal(Doubles(dd), k.Doubles);
                    if (w.TryGetProperty("text", out var t)) Assert.Equal(t.GetString(), k.Text);
                }
            }
        }

        // the placement GDAL reads, in the reading GeoTIFF 1.1 prescribes (see gen_geotiff.py)
        var gt = Doubles(std.GetProperty("transform"))!;
        Assert.Equal(!IsIdentity(gt), info.HasTransform);
        var g = info.Reference;
        if (info.HasTransform) Near(gt, g.Affine, file + " affine");
        Assert.Equal(def.GetProperty("epsg").ValueKind == JsonValueKind.Null ? null : def.GetProperty("epsg").GetInt32(), g.Crs?.Epsg);
        var nd = def.GetProperty("nodata");
        if (nd.ValueKind == JsonValueKind.Null) Assert.Null(g.NoData);
        else if (nd.ValueKind == JsonValueKind.String) Assert.True(double.IsNaN(g.NoData!.Value));
        else Assert.Equal(nd.GetDouble(), g.NoData);
        bool point = info.Key(GeoTiff.KeyRasterType)?.Code == 2;
        Assert.Equal(point ? GeoRasterType.PixelIsPoint : GeoRasterType.PixelIsArea, g.RasterType);
        if (point && info.HasTransform)
        {
            // GDAL's default turns a point grid into an area grid half a cell up and left
            var gd = Doubles(def.GetProperty("transform"))!;
            var (x, y) = g.ToWorld(-0.5, -0.5);
            Near(gd[2], x, "GDAL's area origin x"); Near(gd[5], y, "GDAL's area origin y");
        }

        // decoded: the placement travels with the image, through orientation and regions
        var stored = TestUtil.LoadRef(TestUtil.Fixture(file) + ".ref");
        Assert.Equal(info.HasTransform, Tiff.Decode(data).Metadata?.Geo != null);
        if (!info.HasTransform) return;
        Near(gt, Tiff.Decode(data, new TiffDecodeOptions { ApplyOrientation = false }).Metadata!.Geo!.Affine, "stored decode");
        double c = point ? 0 : 0.5;   // where a sample sits in its cell
        var upright = Tiff.Decode(data);
        SamplesLieWhereGdalPutsThem(upright, 0, 0, stored, gt, c, file);
        var info2 = Tiff.GetInfo(data);
        if (info2.Width >= 5 && info2.Height >= 4)
        {
            var region = Tiff.DecodeRegion(data, 1, 2, 4, 2);
            SamplesLieWhereGdalPutsThem(region, 1, 2, stored, gt, c, file + " region");
        }
    }

    // every sample of `image` (all values unique in the fixtures) sits where GDAL places the stored sample of that value
    static void SamplesLieWhereGdalPutsThem(RawImage image, int x0, int y0, RawImage stored, double[] gt, double c, string what)
    {
        var geo = image.Metadata!.Geo!;
        var at = new Dictionary<double, (int, int)>();
        for (int j = 0; j < stored.Height; j++)
            for (int i = 0; i < stored.Width; i++) at[Sample(stored, i, j)] = (i, j);
        for (int v = 0; v < image.Height; v++)
            for (int u = 0; u < image.Width; u++)
            {
                var (i, j) = at[Sample(image, u, v)];
                var (wx, wy) = Apply(gt, i + c, j + c);
                var (ox, oy) = geo.ToWorld(u + c, v + c);
                Near(wx, ox, $"{what} x of sample ({u}, {v})"); Near(wy, oy, $"{what} y of sample ({u}, {v})");
            }
    }

    static double Sample(RawImage img, int x, int y)
    {
        int k = (y * img.Width + x) * img.Channels;
        return img.SampleType switch
        {
            SampleType.UInt8 => ((byte[])img.Data)[k] * 256.0 + (img.Channels > 1 ? ((byte[])img.Data)[k + 1] : 0),
            SampleType.UInt16 => ((ushort[])img.Data)[k],
            SampleType.Float32 => ((float[])img.Data)[k],
            _ => throw new NotSupportedException(img.SampleType.ToString()),
        };
    }

    // ------------------------------------------------------------------------------------------ key names

    [Fact]
    public void KeyIdsAreThoseTifffileKnows()
    {
        // tifffile's GEO_KEYS uses the GeoTIFF 1.0 names; every id named here has to be one of its keys, and the keys
        // this library interprets have to be the ones the name says
        var ids = GeoTiff.KnownKeys.OrderBy(i => i).ToArray();
        var output = Python("import tifffile\nfor k in [" + string.Join(",", ids) + "]:\n    print(k, tifffile.TIFF.GEO_KEYS(k).name)");
        if (output == null) return;
        var theirs = output.Split('\n').Select(l => l.Split(' ')).ToDictionary(p => int.Parse(p[0]), p => p[1]);
        Assert.Equal(ids.Length, theirs.Count);
        void Says(int id, params string[] words)
        {
            foreach (var w in words)
                Assert.True(theirs[id].Contains(w, StringComparison.OrdinalIgnoreCase), $"key {id} is {theirs[id]} in tifffile, which does not say {w}");
        }
        Says(GeoTiff.KeyModelType, "ModelType"); Says(GeoTiff.KeyRasterType, "RasterType"); Says(GeoTiff.KeyCitation, "GTCitation");
        Says(GeoTiff.KeyGeodeticCrs, "Geographic"); Says(GeoTiff.KeyGeodeticCitation, "Geog", "Citation");
        Says(GeoTiff.KeyGeodeticDatum, "Datum"); Says(GeoTiff.KeyGeogAngularUnits, "Angular", "Units");
        Says(GeoTiff.KeyProjectedCrs, "ProjectedCS"); Says(GeoTiff.KeyProjectedCitation, "Citation");
        Says(GeoTiff.KeyProjLinearUnits, "Proj", "LinearUnits"); Says(GeoTiff.KeyVerticalCrs, "Vertical");
        Says(GeoTiff.KeyVerticalUnits, "VerticalUnits");
    }

    // ------------------------------------------------------------------------------------------ writing

    static RawImage Pattern(int w, int h, SampleType t = SampleType.UInt16)
    {
        var img = RawImage.Create(w, h, ColorModel.Gray, t, 1);
        for (int i = 0; i < w * h; i++)
        {
            if (t == SampleType.UInt16) ((ushort[])img.Data)[i] = (ushort)i;
            else ((float[])img.Data)[i] = i;
        }
        return img;
    }

    public static IEnumerable<object[]> WriteCases()
    {
        yield return new object[] { "utm" };
        yield return new object[] { "geographic" };
        yield return new object[] { "rotated" };
        yield return new object[] { "flipped-x" };
        yield return new object[] { "south-up" };
        yield return new object[] { "point" };
        yield return new object[] { "nodata-nan" };
        yield return new object[] { "feet" };
    }

    static GeoReference Case(string name) => name switch
    {
        "utm" => new GeoReference { Affine = new double[] { 10, 0, 600000, 0, -10, 5300000 }, ModelType = GeoModelType.Projected,
            Crs = new GeoCrs { Epsg = 32633, Name = "WGS 84 / UTM zone 33N", LinearUnit = 9001 }, NoData = 65535 },
        "geographic" => new GeoReference { Affine = new double[] { 0.001, 0, 15.5, 0, -0.001, 48.25 }, ModelType = GeoModelType.Geographic,
            Crs = new GeoCrs { Epsg = 4326, AngularUnit = 9102 } },
        "rotated" => new GeoReference { Affine = new double[] { 10, 2, 600000, 1.5, -10, 5300000 }, ModelType = GeoModelType.Projected, Crs = new GeoCrs { Epsg = 32633 } },
        "flipped-x" => new GeoReference { Affine = new double[] { -10, 0, 600000, 0, -10, 5300000 }, ModelType = GeoModelType.Projected, Crs = new GeoCrs { Epsg = 32633 } },
        "south-up" => new GeoReference { Affine = new double[] { 10, 0, 600000, 0, 10, 5300000 }, ModelType = GeoModelType.Projected, Crs = new GeoCrs { Epsg = 32633 } },
        "point" => new GeoReference { Affine = new double[] { 10, 0, 600005, 0, -10, 5299995 }, ModelType = GeoModelType.Projected,
            RasterType = GeoRasterType.PixelIsPoint, Crs = new GeoCrs { Epsg = 32633 } },
        "nodata-nan" => new GeoReference { Affine = new double[] { 0.5, 0, 1700000, 0, -0.5, 6100000 }, ModelType = GeoModelType.Projected,
            Crs = new GeoCrs { Epsg = 3857 }, NoData = double.NaN },
        _ => new GeoReference { Affine = new double[] { 2, 0, 6000000, 0, -2, 2100000 }, Crs = new GeoCrs { Epsg = 2227, LinearUnit = 9003 } },
    };

    // what GDAL (standard reading, see gen_geotiff.py) and tifffile see in a file we wrote
    const string Readback = """
        import rasterio, tifffile, json, math
        from rasterio.env import Env
        with Env(GTIFF_HONOUR_NEGATIVE_SCALEY='YES', GTIFF_POINT_GEO_IGNORE='TRUE'):
            with rasterio.open(PATH) as ds:
                t = ds.transform
                nd = ds.nodata
                r = dict(transform=[t.a, t.b, t.c, t.d, t.e, t.f], epsg=ds.crs.to_epsg() if ds.crs else None,
                         nodata=None if nd is None else ('nan' if math.isnan(nd) else nd),
                         point=ds.tags().get('AREA_OR_POINT'), overviews=ds.overviews(1))
        with tifffile.TiffFile(PATH) as tf:
            d = [int(v) for v in tf.pages[0].tags[34735].value]
            r['keyIds'] = [d[4 + 4 * e] for e in range(d[3])]
            r['tags'] = sorted(t.code for t in tf.pages[0].tags.values())
            r['tagOrder'] = [t.code for t in tf.pages[0].tags.values()]
            r['pageGeo'] = [any(t.code in (33550, 33922, 34264, 34735) for t in p.tags.values()) for p in tf.pages]
        print(json.dumps(r))
        """;

    [Theory]
    [MemberData(nameof(WriteCases))]
    public void WhatWeWriteGdalReadsBack(string name)
    {
        var geo = Case(name);
        var img = Pattern(13, 7, name == "nodata-nan" ? SampleType.Float32 : SampleType.UInt16);
        var file = Tiff.Encode(img, new TiffEncodeOptions { Geo = geo });

        // our own reading
        var back = Tiff.ReadGeo(file)!;
        Assert.True(back.HasTransform);
        Near(geo.Affine, back.Reference.Affine, "read back");
        Assert.Equal(geo.Crs?.Epsg, back.Reference.Crs?.Epsg);
        Assert.Equal(geo.RasterType, back.Reference.RasterType);
        Assert.Equal(geo.NoData, back.Reference.NoData);
        Assert.Equal(geo.Crs?.Name, back.Reference.Crs?.Name);
        Assert.Equal(geo.Crs?.LinearUnit ?? 0, back.Reference.Crs?.LinearUnit ?? 0);

        var output = Python(Readback, file);
        if (output == null) return;
        var j = Reading(output);
        Near(geo.Affine, Doubles(j.GetProperty("transform"))!, "GDAL");
        Assert.Equal(geo.Crs?.Epsg, j.GetProperty("epsg").ValueKind == JsonValueKind.Null ? null : j.GetProperty("epsg").GetInt32());
        var nd = j.GetProperty("nodata");
        if (geo.NoData is double v)
        {
            if (double.IsNaN(v)) Assert.Equal("nan", nd.GetString());
            else Assert.Equal(v, nd.GetDouble());
        }
        else Assert.Equal(JsonValueKind.Null, nd.ValueKind);
        Assert.Equal(geo.RasterType == GeoRasterType.PixelIsPoint ? "Point" : "Area", j.GetProperty("point").GetString());
        // requirements 1.5 and 1.6: tags and keys in ascending order
        var keyIds = j.GetProperty("keyIds").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        Assert.Equal(keyIds.OrderBy(x => x).ToArray(), keyIds);
        var order = j.GetProperty("tagOrder").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        Assert.Equal(order.OrderBy(x => x).ToArray(), order);
        // a flip or rotation is written as ModelTransformation, never as a negative scale (which GDAL ignores by default)
        bool northUp = geo.Affine[1] == 0 && geo.Affine[3] == 0 && geo.Affine[0] > 0 && geo.Affine[4] < 0;
        var tags = j.GetProperty("tags").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        Assert.Equal(northUp, tags.Contains(GeoTiff.TagModelPixelScale));
        Assert.Equal(!northUp, tags.Contains(GeoTiff.TagModelTransformation));
    }

    [Fact]
    public void ADecodedGeoTiffKeepsItsPlacementWhenWrittenAgain()
    {
        var src = File.ReadAllBytes(TestUtil.Fixture("geotiff/orientation-6.tif"));
        var upright = Tiff.Decode(src);
        var again = Tiff.Encode(upright);
        var back = Tiff.Decode(again);
        Near(upright.Metadata!.Geo!.Affine, back.Metadata!.Geo!.Affine, "round trip");
        Assert.Equal(32633, back.Metadata.Geo.Crs?.Epsg);
        // GDAL reads the upright file with the turned placement
        var output = Python(Readback, again);
        if (output == null) return;
        Near(upright.Metadata.Geo.Affine, Doubles(Reading(output).GetProperty("transform"))!, "GDAL");
    }

    [Fact]
    public void OverviewsAreReducedAndMasksAreNotPlaced()
    {
        var geo = Case("utm");
        var img = Pattern(600, 300);
        var mask = RawImage.Create(600, 300, ColorModel.Gray, SampleType.UInt8, 1);
        Array.Fill((byte[])mask.Data, (byte)255);
        var ms = new MemoryStream();
        Tiff.Encode(new[] { new TiffPage(img, new TiffEncodeOptions { Geo = geo, BuildOverviews = true, OverviewMinSize = 100 }) { Mask = mask } }, ms);
        var file = ms.ToArray();
        int n = Tiff.GetImageCount(file);
        double factor = 1;
        for (int i = 0; i < n; i++)
        {
            var info = Tiff.GetInfos(file)[i];
            var g = Tiff.ReadGeo(file, i);
            bool isMask = Tiff.ReadTags(file, i).SingleOrDefault(t => t.Id == 254)?.AsIntegers()[0] is 4 or 5;
            if (isMask) { Assert.Null(g); continue; }
            Assert.NotNull(g);
            Near(geo.Reduced(factor).Affine, g!.Reference.Affine, $"image {i}");
            Assert.Equal(geo.Reduced(factor).Affine[0] * info.Width, 10.0 * 600, 6);
            factor *= 2;
        }
        Assert.True(factor > 2, "overviews were written");
        var output = Python(Readback, file);
        if (output == null) return;
        var j = Reading(output);
        Near(geo.Affine, Doubles(j.GetProperty("transform"))!, "GDAL");
        Assert.Equal(new[] { 2, 4, 8 }, j.GetProperty("overviews").EnumerateArray().Select(x => x.GetInt32()).ToArray());
    }

    [Fact]
    public void ExtraTagsOverrideSingleGeoTags()
    {
        var file = Tiff.Encode(Pattern(5, 4), new TiffEncodeOptions
        {
            Geo = Case("utm"),
            ExtraTags = { TiffTag.Ascii(GeoTiff.TagGdalNoData, "42") },
        });
        Assert.Equal(42, Tiff.ReadGeo(file)!.Reference.NoData);
    }

    [Fact]
    public void BadPlacementsAreRefused()
    {
        var g = Case("utm");
        g.Crs!.Name = "a|b";
        Assert.Throws<ArgumentException>(() => GeoTiff.BuildTags(g));
        var u = new GeoReference { Affine = new double[] { 1, 0, 0, 0, -1, 0 }, Crs = new GeoCrs { Epsg = 32633 } };
        Assert.Throws<ArgumentException>(() => GeoTiff.BuildTags(u));   // projected or geographic: which key?
        var inf = Case("utm"); inf.Affine[2] = double.PositiveInfinity;
        Assert.Throws<ArgumentException>(() => GeoTiff.BuildTags(inf));
    }

    [Fact]
    public void AnEpsgCodeWithAUnitNeedsNoModelType()
    {
        // the README example
        var g = GeoReference.FromOrigin(600000, 5300000, 10, 10, new GeoCrs { Epsg = 32633, LinearUnit = GeoUnits.Metre });
        var back = Tiff.ReadGeo(Tiff.Encode(Pattern(4, 3), new TiffEncodeOptions { Geo = g }))!.Reference;
        Assert.Equal((GeoModelType.Projected, 32633), (back.ModelType, back.Crs!.Epsg));
        Assert.Equal(new double[] { 10, 0, 600000, 0, -10, 5300000 }, back.Affine);
    }

    [Fact]
    public void StreamAndSubImageOverloadsAgree()
    {
        var data = File.ReadAllBytes(TestUtil.Fixture("geotiff/gdal-rotated.tif"));
        var a = Tiff.ReadGeo(data)!;
        var b = Tiff.ReadGeo(new MemoryStream(data))!;
        Assert.Equal(a.Reference.Affine, b.Reference.Affine);
        Assert.Equal(a.Keys.Count, b.Keys.Count);
    }

    // ------------------------------------------------------------------------------------------ python

    // the JSON line of the readback; anything else (a warning of GDAL or tifffile about the file) fails the test
    static JsonElement Reading(string output)
    {
        Assert.True(output.IndexOf('\n') < 0, "the readers said more than the reading: " + output);
        return JsonDocument.Parse(output).RootElement;
    }

    static string? Python(string code, byte[]? file = null)
    {
        if (!TestUtil.PythonAvailable) return null;
        var tmp = Path.Combine(TestUtil.FixtureDir, "_tmp");
        Directory.CreateDirectory(tmp);
        var f = Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".tif");
        var script = f + ".py";
        try
        {
            if (file != null) File.WriteAllBytes(f, file);
            var body = string.Join("\n", code.Replace("\r", "").Split('\n').Select(l => "    " + l));
            File.WriteAllText(script, "import sys, warnings\nwarnings.filterwarnings('ignore')\nPATH = sys.argv[1]\ntry:\n" + body +
                "\nexcept ImportError as e:\n    sys.stderr.write(f'[missing oracle: python:{e.name}] {e}\\n')\n    sys.exit(77)\n");
            var (exit, output) = TestUtil.RunPython($"\"{script}\" \"{f}\"");
            if (TestUtil.CannotVerify(exit, output)) return null;
            Assert.True(exit == 0, "python failed: " + output);
            return output.Replace("\r", "").Trim();
        }
        finally
        {
            File.Delete(f); File.Delete(script);
        }
    }
}
