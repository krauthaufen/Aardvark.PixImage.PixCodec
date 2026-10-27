using System.Text;
using System.Text.Json;
using Xunit;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// ENVI rasters against what GDAL reads from them (tests/oracle/gen_envi.py, envi_read.py): the fixtures, and every file
/// the encoder writes.
/// </summary>
public class EnviTests
{
    static string Dir => Path.Combine(FixtureDir, "envi");

    public static IEnumerable<object[]> Fixtures()
        => Directory.Exists(Dir)
            ? Directory.GetFiles(Dir, "*.json").OrderBy(f => f, StringComparer.Ordinal).Select(f => new object[] { Path.GetFileNameWithoutExtension(f) })
            : Enumerable.Empty<object[]>();

    static JsonElement Json(string path) => JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8)).RootElement;

    static SampleType TypeOf(string dtype) => dtype switch
    {
        "uint8" => SampleType.UInt8, "int8" => SampleType.Int8, "int16" => SampleType.Int16, "uint16" => SampleType.UInt16,
        "int32" => SampleType.Int32, "uint32" => SampleType.UInt32, "int64" => SampleType.Int64, "uint64" => SampleType.UInt64,
        "float32" or "complex64" => SampleType.Float32, "float64" or "complex128" => SampleType.Float64,
        _ => throw new ArgumentException(dtype),
    };

    static bool Near(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));

    static string Squeeze(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    // ------------------------------------------------------------------ what GDAL reads

    /// <summary>
    /// Everything GDAL reads from a raster agrees with the library: size, sample type, samples (as channels and as a grid),
    /// header fields, placement, EPSG code (where the library gives one), units, no-data value, band names and wavelengths.
    /// </summary>
    static void AssertMatchesGdal(string dataPath, string? headerPath, JsonElement j, RawImage expected)
    {
        int bands = j.GetProperty("bands").GetInt32();
        bool complex = j.GetProperty("complex").GetBoolean();
        var type = TypeOf(j.GetProperty("dtype").GetString()!);
        int channels = bands * (complex ? 2 : 1);

        foreach (var p in new[] { dataPath, headerPath }.Where(p => p != null).Distinct())
        {
            var info = Envi.GetInfo(p!);
            Assert.Equal((j.GetProperty("width").GetInt32(), j.GetProperty("height").GetInt32(), channels, type),
                         (info.Width, info.Height, info.Channels, info.SampleType));
            Assert.Equal(info with { Metadata = null }, ImageCodec.GetInfo(p!) with { Metadata = null });
            AssertEqual(expected, Envi.Decode(p!));
            AssertEqual(expected, ImageCodec.Decode(p!));
        }

        var img = Envi.Decode(dataPath);
        Assert.Equal(channels == 1 ? ColorModel.Gray : ColorModel.Other, img.Color);
        var vol = Envi.DecodeVolume(dataPath);
        Assert.Equal(new[] { img.Width, img.Height, bands }, vol.Sizes);
        Assert.Equal(complex ? 2 : 1, vol.Channels);
        for (int b = 0; b < bands; b++)
        {
            var plane = vol.GetSlice(b);
            for (int q = 0; q < vol.Channels; q++)
                AssertEqual(Channel(expected, b * vol.Channels + q), vol.Channels == 1 ? plane : Channel(plane, q));
        }
        Assert.Equal(ImageFormat.Envi, vol.Metadata!.SourceFormat);

        var h = Envi.ReadHeader(dataPath);
        // every field GDAL parsed from the header (its keys have '_' for spaces), with the same value
        foreach (var f in j.GetProperty("fields").EnumerateObject())
        {
            var mine = h.Fields.LastOrDefault(kv => string.Join('_', kv.Key.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)) == f.Name.ToLowerInvariant());
            Assert.True(mine.Key != null, $"{dataPath}: GDAL reads the field '{f.Name}', the library does not");
            Assert.Equal(Squeeze(f.Value.GetString()!), Squeeze(mine.Value));
        }

        var geo = img.Metadata?.Geo;
        var t = j.GetProperty("transform");
        if (t.ValueKind == JsonValueKind.Null) Assert.True(geo == null || h.MapInfo == null, "GDAL finds no placement");
        else
        {
            Assert.NotNull(geo);
            var want = t.EnumerateArray().Select(v => v.GetDouble()).ToArray();
            for (int k = 0; k < 6; k++)
                Assert.True(Near(want[k], geo!.Affine[k]), $"{dataPath}: affine[{k}] GDAL {want[k]}, here {geo.Affine[k]}");
        }

        int? gdalEpsg = j.GetProperty("epsg").ValueKind == JsonValueKind.Null ? null : j.GetProperty("epsg").GetInt32();
        if (geo?.Crs?.Epsg is int e) Assert.Equal(gdalEpsg, e);
        // the codes the header makes certain have to be found: WGS 84 geographic and UTM
        if (gdalEpsg is 4326 or (>= 32601 and <= 32660) or (>= 32701 and <= 32760))
            Assert.Equal(gdalEpsg, geo?.Crs?.Epsg);
        if (j.GetProperty("geographic").ValueKind == JsonValueKind.True) Assert.Equal(GeoModelType.Geographic, geo!.ModelType);
        if (j.GetProperty("projected").ValueKind == JsonValueKind.True) Assert.Equal(GeoModelType.Projected, geo!.ModelType);
        if (j.GetProperty("linearFactor") is { ValueKind: JsonValueKind.Number } lf)
        {
            // (GDAL reads "units=Feet" as the international foot even where the header's WKT says US survey foot, which the
            // library takes from the WKT: the two differ by 2e-6)
            var mine = GeoUnits.Factor(geo!.Crs!.LinearUnit);
            Assert.True(mine is double m && Math.Abs(m - lf.GetDouble()) <= 1e-5 * lf.GetDouble(),
                $"{dataPath}: metres per unit GDAL {lf.GetDouble()}, here {mine}");
        }

        if (j.GetProperty("nodata") is { ValueKind: JsonValueKind.Number } nd)
        {
            Assert.Equal(nd.GetDouble(), h.DataIgnoreValue);
            Assert.Equal(nd.GetDouble(), img.Metadata!.Physical!.Blank);
        }
        var wl = j.GetProperty("wavelengths");
        if (wl.ValueKind != JsonValueKind.Null) Assert.Equal(wl.EnumerateArray().Select(v => v.GetDouble()).ToArray(), h.Wavelengths);
        if (j.GetProperty("fields").TryGetProperty("band_names", out _))
        {
            // GDAL appends " (wavelength unit)" to a band's name when the header has wavelengths
            var names = j.GetProperty("descriptions").EnumerateArray().Select(v => v.GetString()!).ToArray();
            if (wl.ValueKind != JsonValueKind.Null)
                names = names.Select(n => n.Contains(" (") ? n.Substring(0, n.LastIndexOf(" (", StringComparison.Ordinal)) : n).ToArray();
            Assert.Equal(names, h.BandNames);
        }
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void FixturesReadAsGdalReadsThem(string name)
    {
        var j = Json(Path.Combine(Dir, name + ".json"));
        string data = Path.Combine(Dir, j.GetProperty("dataFile").GetString()!);
        string? header = j.GetProperty("headerFile").GetString() is string hf ? Path.Combine(Dir, hf) : null;
        AssertMatchesGdal(data, header, j, LoadRef(Path.Combine(Dir, name + ".ref")));
    }

    // ------------------------------------------------------------------ what GDAL makes of what is written

    // one folder per test process: the frameworks run side by side and the files have fixed names
    static readonly string Tmp = Path.Combine(FixtureDir, "_tmp", "envi-" + Environment.ProcessId);

    /// <summary>Writes with <see cref="Envi.Encode(RawImage, string, EnviEncodeOptions?)"/> and reads back with GDAL; null when it cannot run.</summary>
    static (JsonElement Json, RawImage Samples, string Path)? Gdal(RawImage image, EnviEncodeOptions o, string name)
    {
        if (!PythonAvailable) return null;
        Directory.CreateDirectory(Tmp);
        var path = Path.Combine(Tmp, name + "_" + Guid.NewGuid().ToString("N")[..8] + ".img");
        Envi.Encode(image, path, o);
        var (exit, output) = RunPython($"envi_read.py \"{path}\" \"{path}.json\" \"{path}.ref\"");
        if (CannotVerify(exit, output)) return null;
        Assert.True(exit == 0, output);
        return (Json(path + ".json"), LoadRef(path + ".ref"), path);
    }

    static RawImage Pattern(int w, int h, int c, SampleType t, int seed)
    {
        var img = RawImage.Create(w, h, c == 1 ? ColorModel.Gray : ColorModel.Other, t, c);
        var rng = new Random(seed);
        var bytes = img.GetBytes();
        rng.NextBytes(bytes);
        if (t is SampleType.Float32)
        {
            var f = (float[])img.Data;
            for (int i = 0; i < f.Length; i++) f[i] = (float)(rng.NextDouble() * 2000 - 1000);
        }
        if (t is SampleType.Float64)
        {
            var f = (double[])img.Data;
            for (int i = 0; i < f.Length; i++) f[i] = rng.NextDouble() * 2000 - 1000;
        }
        return img;
    }

    public static IEnumerable<object[]> WriteCases()
    {
        var types = new[] { SampleType.UInt8, SampleType.Int16, SampleType.UInt16, SampleType.Int32, SampleType.UInt32, SampleType.Int64, SampleType.UInt64, SampleType.Float32, SampleType.Float64 };
        foreach (var t in types)
            foreach (var il in new[] { EnviInterleave.Bsq, EnviInterleave.Bil, EnviInterleave.Bip })
                foreach (bool big in new[] { false, true })
                    if (big || il == EnviInterleave.Bsq || t == SampleType.UInt16)
                        yield return new object[] { t, il, big, false };
        foreach (var t in new[] { SampleType.Float32, SampleType.Float64 })
            foreach (var il in new[] { EnviInterleave.Bsq, EnviInterleave.Bip })
                yield return new object[] { t, il, il == EnviInterleave.Bip, true };
    }

    [Theory, MemberData(nameof(WriteCases))]
    public void GdalReadsWhatIsWritten(SampleType type, EnviInterleave interleave, bool bigEndian, bool complex)
    {
        int channels = complex ? 4 : 3;
        var img = Pattern(11, 7, channels, type, (int)type * 7 + (int)interleave);
        var o = new EnviEncodeOptions
        {
            Interleave = interleave, BigEndian = bigEndian, Complex = complex,
            Geo = GeoReference.FromOrigin(500000, 4000000, 10, 20, new GeoCrs { Epsg = 32633 }),
            BandNames = Enumerable.Range(0, channels / (complex ? 2 : 1)).Select(i => "band " + i).ToArray(),
            DataIgnoreValue = complex ? null : 3,
        };
        var r = Gdal(img, o, $"w_{type}_{interleave}");
        // without GDAL: the library reads its own file back
        Directory.CreateDirectory(Tmp);
        var self = Path.Combine(Tmp, $"self_{type}_{interleave}_{bigEndian}.img");
        Envi.Encode(img, self, o);
        AssertEqual(img, Envi.Decode(self));
        if (r == null) return;
        var (j, samples, path) = r.Value;
        AssertEqual(img, samples);
        Assert.Equal(32633, j.GetProperty("epsg").GetInt32());
        Assert.Equal(new[] { 10.0, 0, 500000, 0, -20, 4000000 }, j.GetProperty("transform").EnumerateArray().Select(v => v.GetDouble() == 0 ? 0 : v.GetDouble()).ToArray());
        if (!complex) Assert.Equal(3.0, j.GetProperty("nodata").GetDouble());
        Assert.Equal(o.BandNames, j.GetProperty("descriptions").EnumerateArray().Select(v => v.GetString()).ToArray());
        AssertMatchesGdal(path, Envi.HeaderPathFor(path), j, samples);
    }

    /// <summary>One channel of an image as a one channel image.</summary>
    static RawImage Channel(RawImage img, int c)
    {
        int bps = RawImage.BytesPerSample(img.SampleType), n = img.Width * img.Height;
        var r = RawImage.Create(img.Width, img.Height, ColorModel.Gray, img.SampleType, 1);
        var src = img.GetBytes();
        var dst = r.GetBytes();
        for (int i = 0; i < n; i++) src.Slice((i * img.Channels + c) * bps, bps).CopyTo(dst.Slice(i * bps, bps));
        return r;
    }

    public static IEnumerable<object[]> GeoCases()
    {
        yield return new object[] { "utm33n" };
        yield return new object[] { "utm17s" };
        yield return new object[] { "wgs84" };
        yield return new object[] { "rotated" };
        yield return new object[] { "southup" };
        yield return new object[] { "webmercator-wkt" };
        yield return new object[] { "stateplane-wkt" };
        yield return new object[] { "nad83-wkt" };
        yield return new object[] { "nocrs" };
    }

    // a CRS as WKT, from pyproj (null when it cannot run)
    static string? WktOf(int epsg)
    {
        if (!PythonAvailable) return null;
        var (exit, output) = RunPython($"-c \"import pyproj; print(pyproj.CRS.from_epsg({epsg}).to_wkt('WKT1_GDAL'))\"");
        if (exit != 0) { Oracles.Missing("python:pyproj", output); return null; }
        return output.Trim();
    }

    [Theory, MemberData(nameof(GeoCases))]
    public void GdalPlacesWhatIsWritten(string kind)
    {
        var (geo, epsg) = kind switch
        {
            "utm33n" => (GeoReference.FromOrigin(500000, 4000000, 10, 10, new GeoCrs { Epsg = 32633 }), (int?)32633),
            "utm17s" => (GeoReference.FromOrigin(300000, 9000000, 30, 30, new GeoCrs { Epsg = 32717 }), 32717),
            "wgs84" => (GeoReference.FromOrigin(16.3, 48.2, 0.001, 0.002, new GeoCrs { Epsg = 4326 }), 4326),
            "rotated" => (new GeoReference { Affine = new[] { 10 * Math.Cos(Math.PI / 6), 10 * Math.Sin(Math.PI / 6), 500000, 20 * Math.Sin(Math.PI / 6), -20 * Math.Cos(Math.PI / 6), 4000000 }, Crs = new GeoCrs { Epsg = 32633 } }, 32633),
            "southup" => (new GeoReference { Affine = new[] { 10.0, 0, 500000, 0, 10, 4000000 }, Crs = new GeoCrs { Epsg = 32633 } }, 32633),
            "webmercator-wkt" => (WktOf(3857) is string w1 ? GeoReference.FromOrigin(1e6, 6e6, 100, 100, new GeoCrs { Wkt = w1 }) : null, 3857),
            "stateplane-wkt" => (WktOf(2227) is string w2 ? GeoReference.FromOrigin(6e6, 2e6, 2, 2, new GeoCrs { Wkt = w2 }) : null, 2227),
            "nad83-wkt" => (WktOf(4269) is string w3 ? GeoReference.FromOrigin(-120, 40, 0.01, 0.01, new GeoCrs { Wkt = w3 }) : null, 4269),
            _ => (new GeoReference { Affine = new[] { 2.0, 0, 100, 0, -2, 200 } }, (int?)null),
        };
        if (geo == null) return;
        var img = Pattern(9, 6, 1, SampleType.UInt8, 5);
        var r = Gdal(img, new EnviEncodeOptions { Geo = geo }, "geo_" + kind);
        if (r == null) return;
        var (j, samples, path) = r.Value;
        AssertEqual(img, samples);
        var t = j.GetProperty("transform").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        for (int k = 0; k < 6; k++) Assert.True(Near(geo.Affine[k], t[k]), $"{kind}: affine[{k}] written {geo.Affine[k]}, GDAL reads {t[k]}");
        if (epsg != null) Assert.Equal(epsg, j.GetProperty("epsg").GetInt32());
        // and the library reads its own placement back the same
        var mine = Envi.ReadGeo(path)!;
        for (int k = 0; k < 6; k++) Assert.True(Near(geo.Affine[k], mine.Affine[k]));
        if (geo.Crs?.Epsg is int e) Assert.Equal(e, mine.Crs!.Epsg);
        if (geo.Crs?.Wkt is string wkt) Assert.Equal(wkt, mine.Crs!.Wkt);
    }

    [Fact]
    public void WhatMapInfoCannotHoldIsRefused()
    {
        var img = Pattern(4, 3, 1, SampleType.UInt8, 1);
        Directory.CreateDirectory(Tmp);
        var p = Path.Combine(Tmp, "refused.img");
        // skewed (not a scaled rotation)
        Assert.Throws<ImageNotSupportedException>(() => Envi.Encode(img, p, new EnviEncodeOptions
        { Geo = new GeoReference { Affine = new[] { 10.0, 3, 0, 0, -10, 0 }, Crs = new GeoCrs { Epsg = 32633 } } }));
        // a CRS map info cannot name, without its WKT: never written as WGS 84
        Assert.Throws<ImageNotSupportedException>(() => Envi.Encode(img, p, new EnviEncodeOptions { Geo = GeoReference.FromOrigin(0, 0, 1, 1, new GeoCrs { Epsg = 3857 }) }));
        Assert.Throws<ImageNotSupportedException>(() => Envi.Encode(img, p, new EnviEncodeOptions { Geo = GeoReference.FromOrigin(0, 0, 1, 1, new GeoCrs { Epsg = 4269 }) }));
        // list separators in a band name, a band name count that does not fit, types ENVI has no code for
        Assert.Throws<ArgumentException>(() => Envi.Encode(img, p, new EnviEncodeOptions { BandNames = new[] { "red, wide" } }));
        Assert.Throws<ArgumentException>(() => Envi.Encode(img, p, new EnviEncodeOptions { BandNames = new[] { "a", "b" } }));
        Assert.Throws<ArgumentException>(() => Envi.Encode(img, p, new EnviEncodeOptions { Description = "a } b" }));
        Assert.Throws<ImageNotSupportedException>(() => Envi.Encode(Pattern(4, 3, 1, SampleType.Int8, 1), p));
        Assert.Throws<ImageNotSupportedException>(() => Envi.Encode(Pattern(4, 3, 3, SampleType.Float32, 1), p, new EnviEncodeOptions { Complex = true }));
        Assert.Throws<ImageNotSupportedException>(() => ImageCodec.Encode(img, new MemoryStream(), ImageFormat.Envi));
    }

    [Fact]
    public void TextRoundTripsAsUtf8()
    {
        var img = Pattern(5, 4, 2, SampleType.UInt16, 2);
        var o = new EnviEncodeOptions { Description = "Größe über Wien, Straße", BandNames = new[] { "Grün", "Rot über" } };
        Directory.CreateDirectory(Tmp);
        var p = Path.Combine(Tmp, "utf8.img");
        Envi.Encode(img, p, o);
        var h = Envi.ReadHeader(p);
        Assert.Equal(o.Description, h.Description);
        Assert.Equal(o.BandNames, h.BandNames);
        var r = Gdal(img, o, "utf8");
        if (r == null) return;
        Assert.Equal(o.BandNames, r.Value.Json.GetProperty("descriptions").EnumerateArray().Select(v => v.GetString()).ToArray());
        Assert.Equal(o.Description, r.Value.Json.GetProperty("fields").GetProperty("description").GetString());
    }

    [Fact]
    public void SpectralFieldsAndLongHeadersAreWrittenAndRead()
    {
        var img = Pattern(6, 5, 3, SampleType.Float32, 3);
        var o = new EnviEncodeOptions
        {
            Wavelengths = new[] { 450.5, 550, 650.25 }, WavelengthUnits = "Nanometers", Fwhm = new[] { 10, 12.5, 15 },
            DataIgnoreValue = -9999, BandNames = new[] { "b", "g", "r" },
            Geo = GeoReference.FromOrigin(400000, 5000000, 5, 5, new GeoCrs { Epsg = 32632 }),
        };
        // a header far longer than 64 KB, the placement among the first fields and more fields after it
        for (int k = 0; k < 2000; k++) o.ExtraFields.Add(new($"note {k:00000}", $"value {k}, with a comma"));
        var r = Gdal(img, o, "spectral");
        Directory.CreateDirectory(Tmp);
        var p = Path.Combine(Tmp, "spectral.img");
        Envi.Encode(img, p, o);
        Assert.True(new FileInfo(Envi.HeaderPathFor(p)).Length > 64 << 10);
        var h = Envi.ReadHeader(p);
        Assert.Equal(o.Wavelengths, h.Wavelengths);
        Assert.Equal(o.Fwhm, h.Fwhm);
        Assert.Equal("value 1999, with a comma", h["note 01999"]);
        Assert.Equal(32632, Envi.ReadGeo(p)!.Crs!.Epsg);
        if (r == null) return;
        Assert.Equal(o.Wavelengths, r.Value.Json.GetProperty("wavelengths").EnumerateArray().Select(v => v.GetDouble()).ToArray());
        AssertMatchesGdal(r.Value.Path, Envi.HeaderPathFor(r.Value.Path), r.Value.Json, r.Value.Samples);
    }

    [Fact]
    public void VolumesAreBandsAsTheThirdAxis()
    {
        var img = Pattern(7, 5, 4, SampleType.Int16, 4);
        Directory.CreateDirectory(Tmp);
        var p = Path.Combine(Tmp, "volume.bil");
        Envi.Encode(img, p, new EnviEncodeOptions { Interleave = EnviInterleave.Bil });
        var v = Envi.DecodeVolume(p);
        Assert.Equal(new[] { 7, 5, 4 }, v.Sizes);
        for (int b = 0; b < 4; b++) AssertEqual(Channel(img, b), v.GetSlice(b));
        Assert.Equal(v.Sizes, ImageCodec.DecodeVolume(p).Sizes);
        // and a grid written as a volume reads back as the same image
        var q = Path.Combine(Tmp, "volume2.img");
        Envi.EncodeVolume(v, q, new EnviEncodeOptions { Interleave = EnviInterleave.Bip, BigEndian = true });
        AssertEqual(img, Envi.Decode(q));
        using var ds = new MemoryStream();
        using var hs = new MemoryStream();
        Envi.EncodeVolume(v, ds, hs);
        ds.Position = 0;
        AssertEqual(img, Envi.Decode(Envi.ReadHeader(hs.ToArray()), ds));
    }

    [Fact]
    public void DetectionNeedsAnEnviHeader()
    {
        Directory.CreateDirectory(Tmp);
        // raw bytes named .img without a header next to them are not ENVI
        var lone = Path.Combine(Tmp, "lone.img");
        File.WriteAllBytes(lone, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.NotEqual(ImageFormat.Envi, ImageCodec.SniffFile(lone));
        Assert.Throws<ImageFormatException>(() => Envi.Decode(lone));
        // nor are they next to a header of another kind (a NIfTI-1 .hdr is binary)
        var nifti = Path.Combine(Tmp, "pair.img");
        File.WriteAllBytes(nifti, new byte[16]);
        File.WriteAllBytes(Path.Combine(Tmp, "pair.hdr"), new byte[348]);
        Assert.NotEqual(ImageFormat.Envi, ImageCodec.SniffFile(nifti));
        // an ENVI header for a TIFF ("file type = TIFF") leaves the TIFF a TIFF
        var tif = Path.Combine(Tmp, "pic.tif");
        File.WriteAllBytes(tif, Tiff.Encode(Pattern(4, 3, 1, SampleType.UInt8, 1)));
        File.WriteAllText(Path.Combine(Tmp, "pic.hdr"), "ENVI\nsamples = 4\nlines = 3\nbands = 1\ndata type = 1\nfile type = TIFF\n");
        Assert.Equal(ImageFormat.Tiff, ImageCodec.SniffFile(tif));
        // the header itself
        Assert.Equal(ImageFormat.Envi, ImageCodec.DetectFormat("ENVI\nsamples = 1\n"u8));
        Assert.False(Envi.IsMatch("ENVIRONMENT = 1\n"u8));
    }

    [Fact]
    public void BrokenFilesAreNamed()
    {
        Directory.CreateDirectory(Tmp);
        var p = Path.Combine(Tmp, "short.img");
        Envi.Encode(Pattern(6, 5, 2, SampleType.UInt16, 6), p);
        var full = File.ReadAllBytes(p);
        File.WriteAllBytes(p, full[..^3]);
        Assert.Throws<ImageFormatException>(() => Envi.Decode(p));
        var header = Envi.ReadHeader(Envi.HeaderPathFor(p));
        using (var ns = new InfoTests.CountingStream(new MemoryStream(full[..^3]), seekable: false))
            Assert.Throws<ImageFormatException>(() => Envi.Decode(header, ns));
        using (var ns = new InfoTests.CountingStream(new MemoryStream(full), seekable: false))
            Assert.Equal(6, Envi.Decode(header, ns).Width);
        // a header without its samples: the information is there, the samples are not
        var h = Path.Combine(Tmp, "alone.hdr");
        File.WriteAllText(h, "ENVI\nsamples = 3\nlines = 2\nbands = 1\ndata type = 1\n");
        Assert.Equal((3, 2), (Envi.GetInfo(h).Width, Envi.GetInfo(h).Height));
        Assert.Throws<ImageFormatException>(() => Envi.Decode(h));
        Assert.Throws<ImageFormatException>(() => ImageCodec.Decode(File.ReadAllBytes(h)));
        Assert.Throws<ImageFormatException>(() => Envi.ReadHeader("ENVI\nsamples = 3\nlines = 2\ndata type = 1\nband names = {a, b\n"u8));
        Assert.Throws<ImageFormatException>(() => Envi.ReadHeader("ENVI\nsamples = 3\ndata type = 1\n"u8));
        Assert.Throws<ImageNotSupportedException>(() => Envi.GetInfo("ENVI\nsamples = 3\nlines = 2\ndata type = 8\n"u8));
        Assert.Throws<ImageNotSupportedException>(() => Envi.Decode(p.Replace("short", "short"), new EnviDecodeOptions { MaxSamples = 10 }));
    }

    [Fact]
    public void DecodingAllocatesLittleBeyondTheResult()
    {
        Directory.CreateDirectory(Tmp);
        var img = Pattern(1024, 512, 16, SampleType.UInt16, 7);
        foreach (var il in new[] { EnviInterleave.Bsq, EnviInterleave.Bil, EnviInterleave.Bip })
        {
            var p = Path.Combine(Tmp, "big_" + il + ".img");
            Envi.Encode(img, p, new EnviEncodeOptions { Interleave = il, BigEndian = il == EnviInterleave.Bil });
            Envi.Decode(p);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var back = Envi.Decode(p);
            long used = GC.GetAllocatedBytesForCurrentThread() - before;
            long result = back.GetBytes().Length;
            Assert.True(used < result + (6L << 20), $"{il}: {used} bytes allocated for a {result} byte result");
            AssertEqual(img, back);
            before = GC.GetAllocatedBytesForCurrentThread();
            var vol = Envi.DecodeVolume(p);
            used = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(used < result + (6L << 20), $"{il} volume: {used} bytes allocated for a {result} byte result");
            File.Delete(p);
        }
    }

    [Fact]
    public void LoaderEntryPointsFindThePair()
    {
        var name = "gdal_u16_utm33n_bsq";
        var data = Path.Combine(Dir, name + ".img");
        var hdr = Path.Combine(Dir, name + ".hdr");
        if (!File.Exists(data)) return;
        Assert.Equal(ImageFormat.Envi, ImageCodec.SniffFile(data));
        Assert.Equal(ImageFormat.Envi, ImageCodec.SniffFile(hdr));
        Assert.Single(ImageCodec.DecodeAll(data));
        var withOptions = ImageCodec.Decode(data, new ImageDecodeOptions { SampleType = SampleType.Float32 });
        Assert.Equal(SampleType.Float32, withOptions.SampleType);
        Assert.Throws<ImageNotSupportedException>(() => ImageCodec.Decode(data, new ImageDecodeOptions { MaxPixels = 10 }));
        Assert.Equal(new[] { 9, 7, 3 }, ImageCodec.DecodeVolume(hdr).Sizes);
        Assert.Equal(32633, ImageCodec.Decode(hdr).Metadata!.Geo!.Crs!.Epsg);
    }

    const string Avng = "downloads/aviris-ng/ang20150811t172724_";

    /// <summary>
    /// The meaning of map info "rotation=" on a real ENVI file with an independent ground truth: AVIRIS-NG's orthocorrected
    /// lookup table (GLT, map info with rotation=-80) names for every output cell the raw pixel that fills it, and the
    /// input geometry (IGM) gives that raw pixel centre's UTM position. Every cell centre the GLT fills from a real
    /// pixel (positive entries; negative ones are infill) lies within half a cell diagonal of that pixel's IGM position,
    /// the nearest neighbour bound of a 1.7 m grid. The other rotation sense misses by kilometres, and a half pixel
    /// slip doubles the bound. GDAL reads the same affine (gdal.json).
    /// </summary>
    [TestDataFact(Avng + "ort_glt", Avng + "ort_igm", "fixtures/envi/aviris-ng/gdal.json")]
    public void RotatedMapInfoPlacesAvirisNgCellsOnTheirGroundPositions()
    {
        var glt = Envi.Decode(TestData(Avng + "ort_glt.hdr"));
        var g = glt.Metadata!.Geo!;
        Assert.Equal(32619, g.Crs!.Epsg);
        var gdal = Json(TestData("fixtures/envi/aviris-ng/gdal.json"));
        var want = gdal.GetProperty("affine").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        for (int k = 0; k < 6; k++) Assert.True(Near(want[k], g.Affine[k]), $"affine[{k}]: GDAL {want[k]}, here {g.Affine[k]}");

        var igm = Envi.Decode(TestData(Avng + "ort_igm.hdr"));
        Assert.Equal((598, 1883, 3), (igm.Width, igm.Height, igm.Channels));
        var lut = glt.GetData<short>();
        var pos = igm.GetData<double>();
        double bound = 1.7 * Math.Sqrt(2) / 2 + 0.005, worst = 0, sum = 0;
        long n = 0;
        for (int r = 0; r < glt.Height; r++)
            for (int c = 0; c < glt.Width; c++)
            {
                int s = lut[(r * glt.Width + c) * 2], l = lut[(r * glt.Width + c) * 2 + 1];
                if (s <= 0 || l <= 0) continue;
                long at = ((long)(l - 1) * igm.Width + (s - 1)) * 3;
                var (x, y) = g.ToWorld(c + 0.5, r + 0.5);
                double d = Math.Sqrt((x - pos[at]) * (x - pos[at]) + (y - pos[at + 1]) * (y - pos[at + 1]));
                worst = Math.Max(worst, d); sum += d * d; n++;
            }
        Assert.True(n > 1_000_000, $"{n} cells filled from real pixels");
        Assert.True(worst <= bound, $"a cell centre lies {worst:F3} m from its pixel's ground position (bound {bound:F3} m)");
        Assert.True(Math.Sqrt(sum / n) < 0.75, $"rms {Math.Sqrt(sum / n):F3} m");
    }
}
