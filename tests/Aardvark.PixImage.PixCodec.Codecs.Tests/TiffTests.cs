using System.Diagnostics;
using System.Runtime.InteropServices;
using Aardvark.ImageCodecs;
using Xunit;

namespace Aardvark.ImageCodecs.Tests;

public class TiffTests
{
    // Fixture folders under tests/fixtures/tiff. The top level and most folders are bit exact against third-party
    // decoders; a fixture with a `.tol` sidecar allows that sample difference (YCbCr / Lab / LogLuv maths, lossy codecs).
    // `unverified` holds the few variants no installed decoder reads (see gen_tiff.py for the list).
    static readonly string[] ActiveDirs = { "", "ycbcr", "lab", "logluv", "pixarlog", "jpeg", "jpeg12", "jpegll", "ojpeg", "jp2", "jbig", "unverified" };

    static IEnumerable<object[]> Dirs(params string[] dirs) => dirs.SelectMany(d => TestUtil.Files(d.Length == 0 ? "tiff" : Path.Combine("tiff", d), ".tif"));

    public static IEnumerable<object[]> Fixtures => Dirs(ActiveDirs);
    public static IEnumerable<object[]> ZstdFixtures => Dirs("zstd");
    public static IEnumerable<object[]> LzmaFixtures => Dirs("lzma");
    public static IEnumerable<object[]> WebpFixtures => Dirs("webp");
    public static IEnumerable<object[]> JpegFixtures => Dirs("jpeg");
    public static IEnumerable<object[]> LercFixtures => Dirs("lerc");
    public static IEnumerable<object[]> JxlFixtures => Dirs("jxl");
    public static IEnumerable<object[]> JxrFixtures => Dirs("jxr");

    // the references hold the stored pixels: these leave the orientation alone
    static readonly TiffDecodeOptions Stored = new() { ApplyOrientation = false };
    static readonly TiffDecodeOptions Serial = new() { Parallel = false, ApplyOrientation = false };
    static TiffDecodeOptions ForcedParallel(int index = 0) => new() { ParallelThreshold = 0, ImageIndex = index, ApplyOrientation = false };

    static double Tolerance(string file)
    {
        var t = TestUtil.Fixture(file) + ".tol";
        return File.Exists(t) ? double.Parse(File.ReadAllText(t), System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    // ------------------------------------------------------------------------------------------ decoding

    static void CheckFixture(string file)
    {
        var path = TestUtil.Fixture(file);
        var bytes = File.ReadAllBytes(path);
        var expected = TestUtil.LoadRef(path + ".ref");
        double tol = Tolerance(file);
        Assert.True(Tiff.IsMatch(bytes));

        var img = Tiff.Decode(bytes, Serial);
        TestUtil.AssertEqual(expected, img, tol);
        // by default turned upright
        var up = Tiff.Decode(bytes);
        TestUtil.AssertEqual(ImageOps.ApplyOrientation(img), up);

        var info = Tiff.GetInfo(bytes);
        Assert.Equal((up.Width, up.Height, up.Channels, up.SampleType, up.Color),
            (info.Width, info.Height, info.Channels, info.SampleType, info.Color));
        Assert.Equal(ImageFormat.Tiff, info.Format);
        // the header level metadata is exactly what decoding reports
        var md = up.Metadata!;
        Assert.Equal((img.Metadata!.Orientation, md.DpiX, md.DpiY, md.IccProfile != null), (info.Orientation, info.DpiX, info.DpiY, info.HasIccProfile));
        Assert.False(info.IsAnimated);
        var stored = Tiff.ReadTags(bytes).FirstOrDefault(t => t.Id == 258)?.AsIntegers() ?? new long[] { 1 };
        bool palette = Tiff.ReadTags(bytes).Any(t => t.Id == 262 && t.AsIntegers()[0] == 3);
        Assert.Equal(palette ? 8 : (int)stored.Max(), info.BitsPerSample);
        Assert.Equal(info, Tiff.GetInfos(bytes)[0]);

        // strips/tiles decoded on several threads give the very same samples
        TestUtil.AssertEqual(img, Tiff.Decode(bytes, ForcedParallel()));

        // references for other decode options, where the generator wrote them
        foreach (var (suffix, options) in new[]
        {
            (".none.ref", new TiffDecodeOptions { BitScaling = TiffBitScaling.None, ApplyOrientation = false }),
            (".full.ref", new TiffDecodeOptions { BitScaling = TiffBitScaling.FullRange, ApplyOrientation = false }),
            (".raw.ref", new TiffDecodeOptions { ConvertToRgb = false, ApplyOrientation = false }),
            (".u16.ref", new TiffDecodeOptions { PixarLogOutput = TiffPixarLogOutput.UInt16, ApplyOrientation = false }),
            (".f32.ref", new TiffDecodeOptions { PixarLogOutput = TiffPixarLogOutput.Float32, ApplyOrientation = false }),
        })
            if (File.Exists(path + suffix))
            {
                var o = Tiff.Decode(bytes, options);
                TestUtil.AssertEqual(TestUtil.LoadRef(path + suffix), o, suffix == ".raw.ref" ? Tolerance(file + ".raw") : 0);
                if (suffix == ".raw.ref" && o.Channels > 2) Assert.Equal(ColorModel.Other, o.Color);
            }

        // through a stream, also embedded at an offset inside a larger stream
        using (var fs = File.OpenRead(path))
        {
            TestUtil.AssertEqual(img, Tiff.Decode(fs, Serial));
            Assert.Equal(0, fs.Position);
            TestUtil.AssertEqual(img, Tiff.Decode(fs, ForcedParallel()));
            Assert.Equal(info, Tiff.GetInfo(fs));
            Assert.Equal(info, Tiff.GetInfos(fs)[0]);
        }
        var ms = new MemoryStream();
        ms.Write(new byte[13]);
        ms.Write(bytes);
        ms.Position = 13;
        TestUtil.AssertEqual(img, Tiff.Decode(ms, Stored));

        // regions: exactly the corresponding part of the full decode
        int w = img.Width, h = img.Height;
        var rnd = new Random(file.Length * 7919 + w);
        var regions = new List<(int x, int y, int w, int h)> { (0, 0, w, h), (w - 1, h - 1, 1, 1), (0, h / 2, w, h - h / 2) };
        if (w > 2 && h > 2) regions.Add((1, 1, w - 2, h - 2));
        for (int i = 0; i < 4; i++)
        {
            int x = rnd.Next(w), y = rnd.Next(h);
            regions.Add((x, y, 1 + rnd.Next(w - x), 1 + rnd.Next(h - y)));
        }
        using var rs = new MemoryStream(bytes);
        foreach (var r in regions)
        {
            var crop = Crop(img, r.x, r.y, r.w, r.h);
            TestUtil.AssertEqual(crop, Tiff.DecodeRegion(bytes, r.x, r.y, r.w, r.h, Serial));
            TestUtil.AssertEqual(crop, Tiff.DecodeRegion(rs, r.x, r.y, r.w, r.h, ForcedParallel()));
        }
        Assert.ThrowsAny<ArgumentException>(() => Tiff.DecodeRegion(bytes, 0, 0, w + 1, h, Serial));
        Assert.ThrowsAny<ArgumentException>(() => Tiff.DecodeRegion(bytes, -1, 0, 1, 1, Serial));
        Assert.ThrowsAny<ArgumentException>(() => Tiff.DecodeRegion(bytes, 0, h, 1, 1, Serial));

        // by default the region is given in coordinates of the upright image
        if (img.Metadata?.Orientation > 1)
        {
            int uw = up.Width, uh = up.Height;
            foreach (var r in new (int x, int y, int w, int h)[] { (0, 0, uw, uh), (uw - 1, 0, 1, 1), (0, uh - 1, 1, 1), (1, 2, uw - 3, uh / 2), (uw / 2, uh / 3, uw - uw / 2, 1) })
                TestUtil.AssertEqual(Crop(up, r.x, r.y, r.w, r.h), Tiff.DecodeRegion(bytes, r.x, r.y, r.w, r.h));
            Assert.ThrowsAny<ArgumentException>(() => Tiff.DecodeRegion(bytes, 0, 0, uw + 1, uh));
            Assert.ThrowsAny<ArgumentException>(() => Tiff.DecodeRegion(bytes, 0, uh, 1, 1));
            Assert.ThrowsAny<ArgumentException>(() => Tiff.DecodeRegion(bytes, uw, 0, 1, 1));
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void DecodesFixture(string file) => CheckFixture(file);

    // The three codecs below are implemented by other owners (Aardvark.ImageCodecs.Compression.Zstd / Xz, Aardvark.ImageCodecs.Webp) and are
    // stubs in this branch. The TIFF side is complete: remove the Skip once the real implementations are merged.
    [Theory]
    [MemberData(nameof(ZstdFixtures))]
    public void DecodesZstdFixture(string file) => CheckFixture(file);

    [Theory]
    [MemberData(nameof(LzmaFixtures))]
    public void DecodesLzmaFixture(string file) => CheckFixture(file);

    [Theory]
    [MemberData(nameof(WebpFixtures))]
    public void DecodesWebpFixture(string file) => CheckFixture(file);

    // tifffile + imagecodecs files (tests/oracle/gen_tiff_jx.py): libjxl / jxrlib codestreams per strip or tile
    [Theory]
    [MemberData(nameof(JxlFixtures))]
    public void DecodesJpegXlFixture(string file) => CheckFixture(file);

    [Theory]
    [MemberData(nameof(JxrFixtures))]
    public void DecodesJpegXrFixture(string file) => CheckFixture(file);

    [Fact]
    public void PendingCodecsReportInfoAndFailCleanly()
    {
        // header level access must work without the codec; decoding either works or says "not supported"
        foreach (var row in ZstdFixtures.Concat(LzmaFixtures).Concat(WebpFixtures))
        {
            var file = (string)row[0];
            var bytes = File.ReadAllBytes(TestUtil.Fixture(file));
            var expected = TestUtil.LoadRef(TestUtil.Fixture(file) + ".ref");
            var info = Tiff.GetInfo(bytes);
            Assert.Equal((expected.Width, expected.Height, expected.Channels, expected.SampleType), (info.Width, info.Height, info.Channels, info.SampleType));
            Assert.NotEmpty(Tiff.ReadTags(bytes));
            try { TestUtil.AssertEqual(expected, Tiff.Decode(bytes), Tolerance(file)); }
            catch (ImageNotSupportedException) { }
        }
    }

    /// <summary>
    /// libtiff's classic sample pictures - files from real old writers (NeXT and HP old-style JPEG, SGI LogLuv,
    /// ThunderScan, fax machines). They are not committed anywhere (no explicit licence): run
    /// tests/oracle/fetch_tiff_samples.py once to download them into the test-data repository and build the
    /// third-party references; without them this test is skipped.
    /// </summary>
    [TestDataFact("downloads/tiff-samples/caspian.tif.ref")]
    public void RealWorldSamples()
    {
        var dir = TestUtil.TestData(Path.Combine("downloads", "tiff-samples"));
        int checkedFiles = 0;
        foreach (var path in Directory.GetFiles(dir, "*.tif").OrderBy(f => f, StringComparer.Ordinal))
        {
            if (!File.Exists(path + ".ref")) continue;
            string name = Path.GetFileName(path);
            var bytes = File.ReadAllBytes(path);
            var expected = TestUtil.LoadRef(path + ".ref");
            var img = Tiff.Decode(bytes);
            double tol = File.Exists(path + ".tol") ? double.Parse(File.ReadAllText(path + ".tol"), System.Globalization.CultureInfo.InvariantCulture) : 0;
            var compared = img;
            if (File.Exists(path + ".rows"))
            {
                // the reference decoder only gets this far into the file
                int rows = int.Parse(File.ReadAllText(path + ".rows"));
                expected = Crop(expected, 0, 0, expected.Width, rows);
                compared = Crop(img, 0, 0, img.Width, rows);
            }
            Assert.True(TestUtil.MaxDiff(expected, compared) <= tol, $"{name}: max difference {TestUtil.MaxDiff(expected, compared)} > {tol}");
            if (File.Exists(path + ".mean"))
            {
                double limit = double.Parse(File.ReadAllText(path + ".mean"), System.Globalization.CultureInfo.InvariantCulture);
                var e = expected.GetData<byte>(); var a = compared.GetData<byte>();
                double sum = 0;
                for (int i = 0; i < e.Length; i++) sum += Math.Abs(e[i] - a[i]);
                Assert.True(sum / e.Length <= limit, $"{name}: mean difference {sum / e.Length:F2} > {limit}");
            }
            TestUtil.AssertEqual(img, Tiff.Decode(bytes, ForcedParallel()));
            using var fs = File.OpenRead(path);
            TestUtil.AssertEqual(Crop(img, 3, 5, img.Width - 7, img.Height - 9), Tiff.DecodeRegion(fs, 3, 5, img.Width - 7, img.Height - 9));
            checkedFiles++;
        }
        Assert.True(checkedFiles >= 20, $"only {checkedFiles} sample files with references found");
    }

    /// <summary>
    /// A real Aperio slide with JPEG 2000 tiles holding subsampled YCbCr components (compression 33003), from the
    /// OpenSlide test data. Fetched into the test-data repository by `fetch_tiff_samples.py --aperio` (64 MB); without
    /// it this test is skipped.
    /// OpenJPEG rounds the 9/7 wavelet and the colour conversion slightly differently.
    /// </summary>
    [TestDataFact("downloads/tiff-samples/JP2K-33003-1.svs.3.ref")]
    public void AperioJpeg2000Sample()
    {
        var path = TestUtil.TestData(Path.Combine("downloads", "tiff-samples", "JP2K-33003-1.svs"));
        using var fs = File.OpenRead(path);
        Assert.Equal(6, Tiff.GetImageCount(fs));
        var infos = Tiff.GetInfos(fs);
        Assert.Equal((15374, 17497, 3, ColorModel.Rgb), (infos[0].Width, infos[0].Height, infos[0].Channels, infos[0].Color));
        var small = Tiff.Decode(fs, new TiffDecodeOptions { ImageIndex = 3 });
        TestUtil.AssertEqual(TestUtil.LoadRef(path + ".3.ref"), small, 3);
        TestUtil.AssertEqual(small, Tiff.Decode(fs, new TiffDecodeOptions { ImageIndex = 3, Parallel = false }));
        var region = Tiff.DecodeRegion(fs, 1024, 768, 700, 500, new TiffDecodeOptions { ImageIndex = 2 });
        TestUtil.AssertEqual(TestUtil.LoadRef(path + ".2.region.ref"), region, 3);
    }

    /// <summary>
    /// The TIFF files of GDAL's test suite (fetched into the test-data repository by `fetch_tiff_samples.py --gdal`;
    /// without them this test is skipped): what GDAL writes with every codec and layout it has, and its collection of broken files. Where the
    /// script stored GDAL's reading of a file, the stored samples have to be the same; no file may make the decoder
    /// fail with anything but the two documented exceptions.
    /// </summary>
    [TestDataFact("downloads/gdal-samples")]
    public void GdalAutotestSamples()
    {
        var dir = TestUtil.TestData(Path.Combine("downloads", "gdal-samples"));
        var raw = new TiffDecodeOptions { BitScaling = TiffBitScaling.None, ConvertToRgb = false };
        int same = 0;
        var unsupported = new List<string>();
        var rejected = new List<string>();
        var problems = new List<string>();
        foreach (var path in Directory.GetFiles(dir, "*.tif").OrderBy(f => f, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);
            var bytes = File.ReadAllBytes(path);
            bool hasRef = File.Exists(path + ".ref");
            RawImage img;
            try
            {
                img = Tiff.Decode(bytes, raw);
            }
            catch (ImageNotSupportedException) { unsupported.Add(name); continue; }
            catch (ImageFormatException) { if (hasRef) rejected.Add(name); continue; }
            catch (Exception e) { problems.Add($"{name}: {e.GetType().Name} {e.Message}"); continue; }
            if (!hasRef) continue;
            var expected = TestUtil.LoadRef(path + ".ref");
            if ((expected.Width, expected.Height, expected.Channels, expected.SampleType) != (img.Width, img.Height, img.Channels, img.SampleType))
                problems.Add($"{name}: {img.Width}x{img.Height}x{img.Channels} {img.SampleType}, GDAL reads {expected.Width}x{expected.Height}x{expected.Channels} {expected.SampleType}");
            else if (!expected.GetBytes().SequenceEqual(img.GetBytes()) && TestUtil.MaxDiff(expected, img) != 0)
                problems.Add($"{name}: samples differ from GDAL's by up to {TestUtil.MaxDiff(expected, img)}");
            else same++;
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        // the one broken file GDAL still reads (with a warning: its strip is said to be longer than the file)
        Assert.Equal(new[] { "minimum_tiff_tags_with_warning.tif" }, rejected);
        // valid files Aardvark.PixImage.PixCodec does not read: NeXT RLE, 12-bit YCbCr without JPEG, codecs nobody knows,
        // and headers describing images too large for one array
        Assert.Equal(new[]
        {
            "bigtiff_header_extract.tif", "gtiff_unsupported_codec_unknown.tif", "huge-number-strips.tif", "huge4GB.tif", "int12_ycbcr_contig.tif", "leak-ZIPSetupDecode.tif",
            "next_default_case.tif", "next_literalrow.tif", "next_literalspan.tif", "strip_larger_than_2GB_header.tif", "tiff_dos_strip_chop.tif", "unknown_compression.tif",
        }, unsupported);
        Assert.True(same >= 290, $"only {same} files compared");
    }

    [Fact]
    public void GenericEntryPoints()
    {
        var bytes = File.ReadAllBytes(TestUtil.Fixture(Path.Combine("tiff", "u16_rgb_lzw_pred2.tif")));
        var expected = TestUtil.LoadRef(TestUtil.Fixture(Path.Combine("tiff", "u16_rgb_lzw_pred2.tif.ref")));
        Assert.Equal(ImageFormat.Tiff, ImageCodec.FormatFromExtension("a.TIF"));
        Assert.Equal(ImageFormat.Tiff, ImageCodec.DetectFormat(bytes));
        TestUtil.AssertEqual(expected, ImageCodec.Decode(bytes));
        Assert.Equal(expected.Width, ImageCodec.GetInfo(bytes).Width);
    }

    static RawImage Crop(RawImage src, int x, int y, int w, int h)
    {
        var dst = RawImage.Create(w, h, src.Color, src.SampleType, src.Channels);
        int px = src.Channels * RawImage.BytesPerSample(src.SampleType);
        var s = src.GetBytes(); var d = dst.GetBytes();
        for (int r = 0; r < h; r++)
            s.Slice(((y + r) * src.Width + x) * px, w * px).CopyTo(d.Slice(r * w * px));
        return dst;
    }

    [Fact]
    public void JpegHookReplacesBuiltInDecoder()
    {
        // Tiff.JpegDecoder is a test seam: a fake decoder that paints a constant shows what the TIFF side hands over.
        var saved = Tiff.JpegDecoder;
        try
        {
            foreach (var row in JpegFixtures)
            {
                var f = TestUtil.Fixture((string)row[0]);
                var bytes = File.ReadAllBytes(f);
                var info = Tiff.GetInfo(bytes);
                int calls = 0;
                Tiff.JpegDecoder = (tables, data, w, h, comps, ycc, dst) =>
                {
                    Assert.True(data.Length > 2 && data[0] == 0xFF && data[1] == 0xD8, "strip is not a JPEG stream");
                    Assert.True(tables.Length == 0 || (tables[0] == 0xFF && tables[1] == 0xD8));
                    Assert.Equal(w * h * comps, dst.Length);
                    Assert.Equal(Path.GetFileName(f).Contains("ycbcr"), ycc);
                    dst.Fill(99);
                    Interlocked.Increment(ref calls);
                };
                var img = Tiff.Decode(bytes);
                Assert.True(calls > 0);
                Assert.Equal((info.Width, info.Height, info.Channels), (img.Width, img.Height, img.Channels));
                Assert.All(img.GetData<byte>(), v => Assert.Equal(99, v));
            }
        }
        finally { Tiff.JpegDecoder = saved; }
    }

    [Theory]
    [InlineData("u8_gray_none.tif", ColorModel.Gray, 1)]
    [InlineData("u8_rgb_none.tif", ColorModel.Rgb, 3)]
    [InlineData("graya_assoc_u8.tif", ColorModel.GrayAlpha, 2)]
    [InlineData("graya_unassoc_f32.tif", ColorModel.GrayAlpha, 2)]
    [InlineData("rgba_unassoc_u8.tif", ColorModel.Rgba, 4)]
    [InlineData("rgba_assoc_u16_lzw.tif", ColorModel.Rgba, 4)]
    [InlineData("rgbx_unspecified_u8.tif", ColorModel.Other, 4)]
    [InlineData("rgb_noextratag_4ch_u8.tif", ColorModel.Other, 4)]
    [InlineData("rgb_2extras_u8.tif", ColorModel.Other, 5)]
    [InlineData("gray_2extras_u16.tif", ColorModel.Other, 3)]
    [InlineData("gray_1extra_unspecified_f32.tif", ColorModel.Other, 2)]
    [InlineData("multiband7_u16_deflate.tif", ColorModel.Other, 7)]
    [InlineData("planar_u16_5band_deflate.tif", ColorModel.Other, 5)]
    [InlineData("palette8.tif", ColorModel.Rgb, 3)]
    [InlineData("palette1.tif", ColorModel.Rgb, 3)]
    [InlineData("palette8_alpha.tif", ColorModel.Rgba, 4)]
    [InlineData("cmyk8.tif", ColorModel.Rgb, 3)]
    [InlineData("cmyk8_alpha.tif", ColorModel.Rgba, 4)]
    [InlineData("gray1_minisblack.tif", ColorModel.Gray, 1)]
    public void ReportsColorModel(string name, ColorModel color, int channels)
    {
        var bytes = File.ReadAllBytes(TestUtil.Fixture(Path.Combine("tiff", name)));
        var img = Tiff.Decode(bytes);
        Assert.Equal((color, channels), (img.Color, img.Channels));
        var info = Tiff.GetInfo(bytes);
        Assert.Equal((color, channels), (info.Color, info.Channels));
    }

    [Theory]
    [InlineData("multipage.tif", 4)]
    [InlineData("multipage_big_be.tif", 2)]
    public void MultiPage(string name, int count)
    {
        var path = TestUtil.Fixture(Path.Combine("tiff", name));
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(count, Tiff.GetImageCount(bytes));
        Assert.Equal(count, Tiff.GetInfo(bytes).FrameCount);
        var infos = Tiff.GetInfos(bytes);
        Assert.Equal(count, infos.Length);
        using var fs = File.OpenRead(path);
        Assert.Equal(count, Tiff.GetImageCount(fs));
        fs.Position = 0;
        Assert.Equal(infos, Tiff.GetInfos(fs));
        for (int i = 0; i < count; i++)
        {
            var expected = TestUtil.LoadRef(path + (i == 0 ? ".ref" : $".{i}.ref"));
            var img = Tiff.Decode(bytes, new TiffDecodeOptions { ImageIndex = i });
            TestUtil.AssertEqual(expected, img);
            Assert.Equal((expected.Width, expected.Height, expected.Channels, expected.SampleType),
                (infos[i].Width, infos[i].Height, infos[i].Channels, infos[i].SampleType));
            fs.Position = 0;
            TestUtil.AssertEqual(expected, Tiff.Decode(fs, ForcedParallel(i)));
            Assert.NotEmpty(Tiff.ReadTags(bytes, i));
        }
        Assert.ThrowsAny<ArgumentException>(() => Tiff.Decode(bytes, new TiffDecodeOptions { ImageIndex = count }));
        Assert.ThrowsAny<ArgumentException>(() => Tiff.Decode(bytes, new TiffDecodeOptions { ImageIndex = -1 }));
        Assert.ThrowsAny<ArgumentException>(() => Tiff.ReadTags(bytes, count));
    }

    [Fact]
    public void SubImages()
    {
        var path = TestUtil.Fixture(Path.Combine("tiff", "subifds_pyramid.tif"));
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(2, Tiff.GetImageCount(bytes));             // sub-images are not pages
        Assert.Equal(2, Tiff.GetSubImageCount(bytes));
        Assert.Equal(1, Tiff.GetSubImageCount(bytes, 1));
        Assert.Equal(0, Tiff.GetSubImageCount(bytes, 0, 0));    // no nesting in this file
        using var fs = File.OpenRead(path);
        foreach (var (page, sub, refName) in new[] { (0, 0, ".sub0.ref"), (0, 1, ".sub1.ref"), (1, 0, ".1.sub0.ref") })
        {
            var expected = TestUtil.LoadRef(path + refName);
            var o = new TiffDecodeOptions { ImageIndex = page, SubImagePath = new[] { sub } };
            var img = Tiff.Decode(bytes, o);
            TestUtil.AssertEqual(expected, img);
            TestUtil.AssertEqual(expected, Tiff.Decode(fs, o));
            var info = Tiff.GetSubImageInfos(bytes, page)[sub];
            Assert.Equal((img.Width, img.Height, img.Channels, img.SampleType, img.Color), (info.Width, info.Height, info.Channels, info.SampleType, info.Color));
            Assert.Equal(info, Tiff.GetSubImageInfos(fs, page)[sub]);
            var tags = Tiff.ReadTags(bytes, page, new[] { sub });
            Assert.Equal(img.Width, tags.Single(t => t.Id == 256).AsIntegers()[0]);
            Assert.Equal(1, tags.Single(t => t.Id == 254).AsIntegers()[0]);   // reduced resolution
            Assert.Equal(tags.Length, Tiff.ReadTags(fs, page, new[] { sub }).Length);
            TestUtil.AssertEqual(Crop(expected, 1, 1, expected.Width - 2, expected.Height - 2),
                Tiff.DecodeRegion(bytes, 1, 1, expected.Width - 2, expected.Height - 2, o));
        }
        TestUtil.AssertEqual(TestUtil.LoadRef(path + ".1.ref"), Tiff.Decode(bytes, new TiffDecodeOptions { ImageIndex = 1 }));
        Assert.ThrowsAny<ArgumentException>(() => Tiff.Decode(bytes, new TiffDecodeOptions { SubImagePath = new[] { 2 } }));
        Assert.ThrowsAny<ArgumentException>(() => Tiff.Decode(bytes, new TiffDecodeOptions { SubImagePath = new[] { 0, 0 } }));
        Assert.ThrowsAny<ArgumentException>(() => Tiff.GetSubImageInfos(bytes, 0, 5));
        // an image without the tag has none
        Assert.Empty(Tiff.GetSubImageInfos(File.ReadAllBytes(TestUtil.Fixture(Path.Combine("tiff", "u8_rgb_none.tif")))));
    }

    [Fact]
    public void ReadsMetadata()
    {
        var path = TestUtil.Fixture(Path.Combine("tiff", "meta_res_icc_orient.tif"));
        var img = Tiff.Decode(File.ReadAllBytes(path), Stored);
        var m = img.Metadata!;
        Assert.Equal(6, m.Orientation);
        Assert.Equal(300, m.DpiX, 6);
        Assert.Equal(150.1, m.DpiY, 6);
        Assert.Equal(File.ReadAllBytes(Path.ChangeExtension(path, ".icc")), m.IccProfile);
        // orientation is reported, not applied
        Assert.Equal((23, 17), (img.Width, img.Height));

        m = Tiff.Decode(File.ReadAllBytes(TestUtil.Fixture(Path.Combine("tiff", "meta_res_cm.tif")))).Metadata!;
        Assert.Equal(254, m.DpiX, 6);
        Assert.Equal(127, m.DpiY, 6);
        Assert.Equal(0, m.Orientation);
        Assert.Null(m.IccProfile);
    }

    [Fact]
    public void ReadsGeoTiffTags()
    {
        var path = TestUtil.Fixture(Path.Combine("tiff", "geotiff_dem_f32.tif"));
        var bytes = File.ReadAllBytes(path);
        foreach (var tags in new[] { Tiff.ReadTags(bytes), Tiff.ReadTags(new MemoryStream(bytes)) })
        {
            TiffTag Get(int id) => tags.Single(t => t.Id == id);
            Assert.Equal(new[] { 0.5, 0.5, 0.0 }, (double[])Get(33550).Value);
            Assert.Equal(TiffTagType.Double, Get(33550).Type);
            Assert.Equal(new[] { 0.0, 0.0, 0.0, 600000.0, 5300000.0, 0.0 }, Get(33922).AsDoubles());
            var keys = (ushort[])Get(34735).Value;
            Assert.Equal(16, keys.Length);
            Assert.Equal(32633, keys[15]);
            Assert.Equal(32633, Get(34735).AsIntegers()[15]);
            Assert.Equal("-9999", Get(42113).AsString());
            Assert.Equal(TiffTagType.Ascii, Get(42113).Type);
            Assert.Equal("pixcodec fixture", Get(305).AsString());
            Assert.Equal(40, Get(256).AsIntegers()[0]);
            Assert.Equal(16, Get(322).AsIntegers()[0]);
            Assert.Equal(tags.Select(t => t.Id).OrderBy(i => i), tags.Select(t => t.Id));
        }
    }

    [Fact]
    public void RejectsNonTiff()
    {
        Assert.False(Tiff.IsMatch(ReadOnlySpan<byte>.Empty));
        Assert.False(Tiff.IsMatch("II"u8));
        Assert.False(Tiff.IsMatch(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0 }));
        Assert.True(Tiff.IsMatch(new byte[] { 0x49, 0x49, 42, 0 }));
        Assert.True(Tiff.IsMatch(new byte[] { 0x4D, 0x4D, 0, 43 }));
        Assert.Throws<ImageFormatException>(() => Tiff.Decode(ReadOnlySpan<byte>.Empty));
        Assert.Throws<ImageFormatException>(() => Tiff.Decode(new byte[] { 0x49, 0x49, 42, 0 }));
        Assert.Throws<ImageFormatException>(() => Tiff.GetInfo(new byte[100]));
        Assert.Throws<ImageFormatException>(() => Tiff.Decode(new MemoryStream(new byte[3])));
    }

    // ------------------------------------------------------------------------------------------ LZW / PackBits units

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(253)]
    [InlineData(254)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(700)]
    [InlineData(5000)]
    [InlineData(70000)]
    public unsafe void LzwRoundTrip(int n)
    {
        // noise walks through every code width change and table reset, the other patterns make long strings
        foreach (int kind in new[] { 0, 1, 2, 3 })
        {
            var rnd = new Random(n * 31 + kind);
            var data = new byte[n];
            for (int i = 0; i < n; i++)
                data[i] = kind switch { 0 => (byte)rnd.Next(256), 1 => (byte)(i / 300), 2 => (byte)rnd.Next(3), _ => (byte)(i % 7 == 0 ? rnd.Next(256) : 5) };
            var enc = new byte[TiffLzw.MaxEncodedSize(n)];
            int len = TiffLzw.Encode(data, enc);
            Assert.True(len <= enc.Length);
            var dec = new byte[n + 8];
            fixed (byte* d = dec)
                Assert.Equal(n, TiffLzw.Decode(enc.AsSpan(0, len), d, n));
            Assert.True(data.AsSpan().SequenceEqual(dec.AsSpan(0, n)));
            Assert.All(dec.Skip(n), b => Assert.Equal(0, b));
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(17, 5)]
    [InlineData(1000, 100)]
    [InlineData(4096, 129)]
    public unsafe void PackBitsRoundTrip(int n, int row)
    {
        foreach (int kind in new[] { 0, 1, 2 })
        {
            var rnd = new Random(n + kind);
            var data = new byte[n];
            for (int i = 0; i < n; i++) data[i] = kind switch { 0 => (byte)rnd.Next(256), 1 => (byte)(i / 200), _ => (byte)(rnd.Next(4) == 0 ? rnd.Next(256) : 9) };
            var enc = new byte[TiffPackBits.MaxEncodedSize(n, row)];
            int len = TiffPackBits.Encode(data, row, enc);
            var dec = new byte[n];
            fixed (byte* d = dec) Assert.Equal(n, TiffPackBits.Decode(enc.AsSpan(0, len), d, n));
            Assert.Equal(data, dec);
        }
    }

    // ------------------------------------------------------------------------------------------ encoding

    static readonly SampleType[] AllTypes =
    {
        SampleType.UInt8, SampleType.UInt16, SampleType.UInt32, SampleType.Int8, SampleType.Int16, SampleType.Int32,
        SampleType.Float16, SampleType.Float32, SampleType.Float64, SampleType.UInt64, SampleType.Int64,
    };

    static readonly (ColorModel color, int channels)[] AllModels =
    {
        (ColorModel.Gray, 1), (ColorModel.GrayAlpha, 2), (ColorModel.Rgb, 3), (ColorModel.Rgba, 4), (ColorModel.Other, 2), (ColorModel.Other, 6),
    };

    static readonly TiffCompression[] AllCompressions = { TiffCompression.None, TiffCompression.Lzw, TiffCompression.Deflate, TiffCompression.PackBits, TiffCompression.Zstd, TiffCompression.Lzma };

    /// <summary>Deterministic image with gradients, noise and (for floats) non-finite values.</summary>
    internal static RawImage MakeImage(int w, int h, ColorModel color, SampleType type, int channels, int seed = 1)
    {
        var img = RawImage.Create(w, h, color, type, channels);
        var rnd = new Random(seed * 1000 + (int)type * 10 + channels);
        int n = w * h * channels;
        double V(int i)
        {
            int c = i % channels, x = i / channels % w, y = i / channels / w;
            return 0.5 + 0.35 * Math.Sin(x / (5.0 + c) + c) * Math.Cos(y / 4.0) + 0.15 * rnd.NextDouble();
        }
        // (by sample type: array type patterns cannot tell sbyte[] from byte[])
        switch (type)
        {
            case SampleType.UInt8: { var a = (byte[])img.Data; for (int i = 0; i < n; i++) a[i] = (byte)(V(i) * 255); break; }
            case SampleType.Int8: { var a = (sbyte[])img.Data; for (int i = 0; i < n; i++) a[i] = (sbyte)(V(i) * 255 - 128); break; }
            case SampleType.UInt16: { var a = (ushort[])img.Data; for (int i = 0; i < n; i++) a[i] = (ushort)(V(i) * 65535); break; }
            case SampleType.Int16: { var a = (short[])img.Data; for (int i = 0; i < n; i++) a[i] = (short)(V(i) * 65535 - 32768); break; }
            case SampleType.UInt32: { var a = (uint[])img.Data; for (int i = 0; i < n; i++) a[i] = (uint)(V(i) * uint.MaxValue); break; }
            case SampleType.Int32: { var a = (int[])img.Data; for (int i = 0; i < n; i++) a[i] = (int)(V(i) * uint.MaxValue - int.MaxValue); break; }
            case SampleType.Float16:
            {
                var a = (Half[])img.Data;
                for (int i = 0; i < n; i++) a[i] = (Half)(V(i) * 200 - 50);
                a[0] = Half.NaN; if (n > 3) { a[1] = Half.PositiveInfinity; a[2] = Half.NegativeZero; }
                break;
            }
            case SampleType.Float32:
            {
                var a = (float[])img.Data;
                for (int i = 0; i < n; i++) a[i] = (float)(V(i) * 2000 - 500);
                a[0] = float.NaN; if (n > 3) { a[1] = float.NegativeInfinity; a[2] = -0f; }
                break;
            }
            case SampleType.UInt64: { var a = (ulong[])img.Data; for (int i = 0; i < n; i++) a[i] = (ulong)(V(i) * uint.MaxValue) << 32 | (uint)rnd.Next(); a[0] = ulong.MaxValue; break; }
            case SampleType.Int64: { var a = (long[])img.Data; for (int i = 0; i < n; i++) a[i] = (long)((ulong)(V(i) * uint.MaxValue) << 32 | (uint)rnd.Next()); a[0] = long.MinValue; break; }
            default:
            {
                var a = (double[])img.Data;
                for (int i = 0; i < n; i++) a[i] = V(i) * 2e6 - 5e5;
                a[0] = double.NaN; if (n > 3) { a[1] = double.PositiveInfinity; a[2] = -0d; }
                break;
            }
        }
        return img;
    }

    static byte[] Encode(RawImage img, TiffEncodeOptions? o = null)
    {
        var ms = new MemoryStream();
        Tiff.Encode(img, ms, o);
        return ms.ToArray();
    }

    /// <summary>What comes back for an image: a single-channel "Other" image cannot be told apart from gray.</summary>
    static ColorModel ExpectedColor(RawImage img) => img.Color == ColorModel.Other && img.Channels == 1 ? ColorModel.Gray : img.Color;

    static void AssertRoundTrip(RawImage img, byte[] file, string what)
    {
        RawImage back;
        try { back = Tiff.Decode(file, Stored); }
        catch (Exception e) { throw new Xunit.Sdk.XunitException($"{what}: {e}"); }
        Assert.True(back.SampleType == img.SampleType && (back.Width, back.Height, back.Channels) == (img.Width, img.Height, img.Channels), what);
        Assert.True(ExpectedColor(img) == back.Color, $"{what}: color {back.Color}");
        Assert.True(img.GetBytes().SequenceEqual(back.GetBytes()), $"{what}: samples differ");
    }

    public static IEnumerable<object[]> TypeAndCompression =>
        from t in AllTypes from c in AllCompressions select new object[] { t, c };

    [Theory]
    [MemberData(nameof(TypeAndCompression))]
    public void EncodeRoundTrip(SampleType type, TiffCompression compression)
    {
        foreach (var (color, channels) in AllModels)
        {
            var img = MakeImage(37, 23, color, type, channels);
            var layouts = new (string name, TiffEncodeOptions o)[]
            {
                ("default", new TiffEncodeOptions()),
                ("rps3", new TiffEncodeOptions { RowsPerStrip = 3 }),
                ("rps1000", new TiffEncodeOptions { RowsPerStrip = 1000 }),
                ("tile16", new TiffEncodeOptions { TileWidth = 16, TileHeight = 16 }),
                ("tile30x60", new TiffEncodeOptions { TileWidth = 30, TileHeight = 60 }),
                ("big", new TiffEncodeOptions { BigTiff = true, RowsPerStrip = 5 }),
                ("bigtile", new TiffEncodeOptions { BigTiff = true, TileWidth = 16, TileHeight = 32 }),
                ("nopred", new TiffEncodeOptions { Predictor = TiffPredictor.None, RowsPerStrip = 7 }),
                ("hpred", new TiffEncodeOptions { Predictor = TiffPredictor.Horizontal, RowsPerStrip = 7 }),
                ("serial", new TiffEncodeOptions { Parallel = false, RowsPerStrip = 2 }),
                ("be", new TiffEncodeOptions { BigEndian = true, RowsPerStrip = 6 }),
                ("bigbetile", new TiffEncodeOptions { BigEndian = true, BigTiff = true, TileWidth = 16, TileHeight = 16 }),
                ("planar", new TiffEncodeOptions { Planar = true, RowsPerStrip = 5 }),
                ("planartile", new TiffEncodeOptions { Planar = true, TileWidth = 32, TileHeight = 16 }),
            };
            foreach (var (name, o) in layouts)
            {
                o.Compression = compression;
                var file = Encode(img, o);
                string what = $"{type} {color}/{channels} {compression} {name}";
                AssertRoundTrip(img, file, what);
                Assert.Equal(name.StartsWith("big"), file[2] == 43 || file[3] == 43);
                Assert.Equal(name.Contains("be") ? (byte)'M' : (byte)'I', file[0]);

                // the same bytes must come out of a write-only, non-seekable stream
                var ns = new ForwardOnlyStream();
                Tiff.Encode(img, ns, o);
                Assert.True(file.AsSpan().SequenceEqual(ns.ToArray()), $"{what}: non-seekable output differs");
            }
        }
    }

    [Fact]
    public void EncodeDefaultsAndGenericEntryPoint()
    {
        var img = MakeImage(300, 200, ColorModel.Rgb, SampleType.UInt8, 3);
        var file = ImageCodec.Encode(img, ImageFormat.Tiff);
        AssertRoundTrip(img, file, "generic");
        var tags = Tiff.ReadTags(file);
        long Tag(int id) => tags.Single(t => t.Id == id).AsIntegers()[0];
        Assert.Equal(8, Tag(259));   // deflate by default
        Assert.Equal(2, Tag(317));   // horizontal predictor for integers
        Assert.Equal(2, Tag(262));
        Assert.InRange(Tag(278) * 300 * 3, 32 * 1024, 128 * 1024); // about 64 KB per strip
        Assert.Equal((byte)'I', file[0]);
        Assert.True(file.Length < 300 * 200 * 3);

        var f = MakeImage(64, 64, ColorModel.Gray, SampleType.Float32, 1);
        long FTag(byte[] b, int id) => Tiff.ReadTags(b).Single(t => t.Id == id).AsIntegers()[0];
        Assert.Equal(3, FTag(Encode(f), 317));
        Assert.Equal(3, FTag(Encode(f), 339));
        Assert.Equal(2, FTag(Encode(f, new TiffEncodeOptions { Predictor = TiffPredictor.Horizontal }), 317));
        Assert.DoesNotContain(Tiff.ReadTags(Encode(f, new TiffEncodeOptions { Compression = TiffCompression.PackBits })), t => t.Id == 317);
        Assert.DoesNotContain(Tiff.ReadTags(Encode(f, new TiffEncodeOptions { Compression = TiffCompression.None })), t => t.Id == 317);
        Assert.Throws<ArgumentException>(() => Encode(img, new TiffEncodeOptions { Predictor = TiffPredictor.FloatingPoint }));

        // tile sizes are rounded up to multiples of 16
        var t = Tiff.ReadTags(Encode(img, new TiffEncodeOptions { TileWidth = 100, TileHeight = 50 }));
        Assert.Equal(112, t.Single(x => x.Id == 322).AsIntegers()[0]);
        Assert.Equal(64, t.Single(x => x.Id == 323).AsIntegers()[0]);
    }

    [Fact]
    public void EncodeWritesMetadata()
    {
        var img = MakeImage(20, 10, ColorModel.Rgba, SampleType.UInt16, 4);
        var icc = new byte[777];
        new Random(5).NextBytes(icc);
        img.Metadata = new ImageMetadata { DpiX = 300, DpiY = 72.5, IccProfile = icc, Orientation = 3 };
        foreach (var big in new[] { false, true })
        {
            var back = Tiff.Decode(Encode(img, new TiffEncodeOptions { BigTiff = big }), Stored);
            Assert.Equal(300, back.Metadata!.DpiX, 6);
            Assert.Equal(72.5, back.Metadata.DpiY, 6);
            Assert.Equal(icc, back.Metadata.IccProfile);
            Assert.Equal(3, back.Metadata.Orientation);
        }
        img.Metadata = null;
        var m = Tiff.Decode(Encode(img)).Metadata!;
        Assert.Equal((0d, 0d, 0), (m.DpiX, m.DpiY, m.Orientation));
        Assert.Null(m.IccProfile);
    }

    [Fact]
    public void EncodeLargerImageInParallel()
    {
        // big enough for the parallel compression / decoding paths and several LZW table resets per strip
        foreach (var type in new[] { SampleType.UInt8, SampleType.UInt16, SampleType.Float32 })
            foreach (var comp in AllCompressions)
                foreach (var tile in new[] { 0, 256 })
                {
                    var img = MakeImage(1031, 517, ColorModel.Rgb, type, 3);
                    var o = new TiffEncodeOptions { Compression = comp, TileWidth = tile, TileHeight = tile };
                    var file = Encode(img, o);
                    AssertRoundTrip(img, file, $"{type} {comp} tile {tile}");
                    var region = Tiff.DecodeRegion(file, 100, 200, 600, 300);
                    TestUtil.AssertEqual(Crop(img, 100, 200, 600, 300), region);
                    o.Parallel = false;
                    Assert.True(file.AsSpan().SequenceEqual(Encode(img, o)), "parallel and serial encoders disagree");
                }
    }

    /// <summary>
    /// Decodes a file we wrote with the independent decoders of tests/oracle/decode_to_ref.py (tifffile + imagecodecs,
    /// libtiff through Pillow for bilevel data). Null only when python is not available.
    /// </summary>
    static RawImage? Oracle(byte[] encoded, int page = 0)
    {
        if (!TestUtil.PythonAvailable) return null;
        var tmp = Path.Combine(TestUtil.FixtureDir, "_tmp");
        Directory.CreateDirectory(tmp);
        var f = Path.Combine(tmp, Guid.NewGuid().ToString("N") + (page > 0 ? $".p{page}.tif" : ".tif"));
        try
        {
            File.WriteAllBytes(f, encoded);
            var (exit, output) = TestUtil.RunPython($"decode_to_ref.py \"{f}\" \"{f}.ref\"");
            if (TestUtil.CannotVerify(exit, output)) return null; // a decoder this file needs is not installed
            Assert.True(exit == 0, "oracle decode failed: " + output);
            return TestUtil.LoadRef(f + ".ref");
        }
        finally
        {
            File.Delete(f); File.Delete(f + ".ref");
        }
    }

    /// <summary>
    /// Wraps a python snippet so that a missing optional module (rasterio, OpenCV, tifffile, imagecodecs, ...) ends
    /// it with status 77 = "cannot be verified here" instead of an error (see <see cref="TestUtil.CannotVerify"/>).
    /// </summary>
    static string Guarded(string code) =>
        "def _snippet():\n" + string.Join("\n", code.Replace("\r", "").Split('\n').Select(l => "    " + l)) +
        "\ntry:\n    _snippet()\nexcept ImportError as e:\n    sys.stderr.write(f'[missing oracle: python:{e.name}] {e}\\n')\n    sys.exit(77)\n" +
        "except Exception as e:\n    if 'imagecodecs' in str(e):\n        sys.stderr.write(f'[missing oracle: python:imagecodecs] {e}\\n')\n        sys.exit(77)\n    raise\n";

    /// <summary>Runs a python snippet on a file we wrote and returns what it prints (for tags and EXIF as other libraries see them).</summary>
    static string? PythonOn(byte[] encoded, string code)
    {
        if (!TestUtil.PythonAvailable) return null;
        var tmp = Path.Combine(TestUtil.FixtureDir, "_tmp");
        Directory.CreateDirectory(tmp);
        var f = Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".tif");
        var script = f + ".py";
        try
        {
            File.WriteAllBytes(f, encoded);
            File.WriteAllText(script, "import sys, warnings\nwarnings.filterwarnings('ignore')\nPATH = sys.argv[1]\n" + Guarded(code));
            var (exit, output) = TestUtil.RunPython($"\"{script}\" \"{f}\"");
            if (TestUtil.CannotVerify(exit, output)) return null; // a module the snippet needs is not installed
            Assert.True(exit == 0, "python failed: " + output);
            return output.Replace("\r", "").Trim();
        }
        finally
        {
            File.Delete(f); File.Delete(script);
        }
    }

    [Fact]
    public void EncodedFilesDecodeWithReferenceDecoders()
    {
        if (!TestUtil.PythonAvailable) return;
        // every sample type x compression x colour model, the layouts rotating through strips, tiles, BigTIFF,
        // big endian and the predictor choices
        var cases = new List<(string what, RawImage img, TiffEncodeOptions o)>();
        int k = 0;
        foreach (var type in AllTypes)
            foreach (var comp in AllCompressions)
                foreach (var (color, channels) in AllModels)
                {
                    var o = (k++ % 9) switch
                    {
                        7 => new TiffEncodeOptions { Planar = true, RowsPerStrip = 6 },
                        8 => new TiffEncodeOptions { Planar = true, TileWidth = 16, TileHeight = 16, BigEndian = k % 2 == 0 },
                        0 => new TiffEncodeOptions(),
                        1 => new TiffEncodeOptions { RowsPerStrip = 4 },
                        2 => new TiffEncodeOptions { TileWidth = 16, TileHeight = 16 },
                        3 => new TiffEncodeOptions { BigTiff = true, RowsPerStrip = 9 },
                        4 => new TiffEncodeOptions { Predictor = k % 2 == 0 ? TiffPredictor.Horizontal : TiffPredictor.None, TileWidth = 32, TileHeight = 16, BigTiff = k % 3 == 0 },
                        5 => new TiffEncodeOptions { BigEndian = true, RowsPerStrip = 5 },
                        _ => new TiffEncodeOptions { BigEndian = true, BigTiff = k % 2 == 0, TileWidth = 16, TileHeight = 32, Predictor = k % 3 == 0 ? TiffPredictor.Horizontal : TiffPredictor.Auto },
                    };
                    o.Compression = comp;
                    cases.Add(($"{type} {color}/{channels} {comp} #{k % 7}", MakeImage(29, 19, color, type, channels), o));
                }
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        int verified = 0, unavailable = 0;
        Parallel.ForEach(cases, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) }, c =>
        {
            try
            {
                var o = Oracle(Encode(c.img, c.o));
                if (o == null) { Interlocked.Increment(ref unavailable); return; } // python or the decoder it needs is not installed
                Interlocked.Increment(ref verified);
                if (o.SampleType != c.img.SampleType || (o.Width, o.Height, o.Channels) != (c.img.Width, c.img.Height, c.img.Channels))
                    failures.Add($"{c.what}: oracle sees {o.Width}x{o.Height}x{o.Channels} {o.SampleType}");
                else if (!o.GetBytes().SequenceEqual(c.img.GetBytes()))
                    failures.Add($"{c.what}: oracle decodes different samples");
            }
            catch (Exception e) { failures.Add($"{c.what}: {e.Message}"); }
        });
        Assert.True(failures.IsEmpty, $"{failures.Count} of {cases.Count} failed:\n" + string.Join("\n", failures.Take(20)));
        Assert.Equal(cases.Count, verified + unavailable); // every single variant was read back by an independent decoder (where there is one)
    }

    // ------------------------------------------------------------------------------------------ bilevel, CCITT, JPEG

    /// <summary>A black on white page: text like blobs, long runs, thin lines.</summary>
    static RawImage MakeBilevel(int w, int h, int seed)
    {
        var img = RawImage.Create(w, h, ColorModel.Gray, SampleType.UInt8);
        var a = img.GetData<byte>();
        Array.Fill(a, (byte)255);
        var rnd = new Random(seed);
        for (int i = 0; i < Math.Max(3, w * h / 300); i++)
        {
            int x = rnd.Next(w), y = rnd.Next(h), rw = 1 + rnd.Next(Math.Max(1, w / 6)), rh = 1 + rnd.Next(4);
            for (int yy = y; yy < Math.Min(h, y + rh); yy++)
                for (int xx = x; xx < Math.Min(w, x + rw); xx++) a[yy * w + xx] = 0;
        }
        for (int i = 0; i < w * h / 40; i++) a[rnd.Next(a.Length)] = (byte)(rnd.Next(2) * 255);
        if (h > 4) for (int x = 0; x < w; x++) a[(h / 2) * w + x] = 0; // a full black row
        return img;
    }

    [Theory]
    [InlineData(TiffCompression.CcittGroup4)]
    [InlineData(TiffCompression.CcittGroup3)]
    [InlineData(TiffCompression.CcittRle)]
    [InlineData(TiffCompression.Jbig)]
    [InlineData(TiffCompression.None)]
    [InlineData(TiffCompression.Lzw)]
    [InlineData(TiffCompression.Deflate)]
    [InlineData(TiffCompression.PackBits)]
    public void EncodesBilevel(TiffCompression compression)
    {
        var sizes = new[] { (1, 1), (7, 3), (8, 8), (203, 61), (2700, 9), (331, 50), (64, 64) };
        foreach (var (w, h) in sizes)
            foreach (var layout in new[] { "strip", "strips", "tiles", "be", "2d" })
            {
                if (layout == "2d" && compression != TiffCompression.CcittGroup3) continue;
                var img = MakeBilevel(w, h, w + h);
                if (w == 8) Array.Fill(img.GetData<byte>(), (byte)0);          // all black
                if (w == 64) Array.Fill(img.GetData<byte>(), (byte)255);       // all white
                // gray input is thresholded at 128
                var gray = MakeBilevel(w, h, w + h);
                var g = gray.GetData<byte>();
                if (w != 8 && w != 64) for (int i = 0; i < g.Length; i++) g[i] = (byte)(g[i] == 0 ? i % 128 : 128 + i % 128);
                else g.AsSpan().Fill(img.GetData<byte>()[0]);
                var o = new TiffEncodeOptions { Compression = compression, Bilevel = true };
                if (layout == "strips") o.RowsPerStrip = 7;
                if (layout == "tiles") { o.TileWidth = 32; o.TileHeight = 16; }
                if (layout == "be") { o.BigEndian = true; o.BigTiff = true; }
                if (layout == "2d") { o.Ccitt2D = true; o.RowsPerStrip = 11; }
                bool fax = compression is TiffCompression.CcittGroup4 or TiffCompression.CcittGroup3 or TiffCompression.CcittRle;
                var file = Encode(gray, o);
                string what = $"{compression} {w}x{h} {layout}";
                var tags = Tiff.ReadTags(file);
                Assert.Equal(1, tags.Single(t => t.Id == 258).AsIntegers()[0]);
                Assert.Equal(fax ? 0 : 1, tags.Single(t => t.Id == 262).AsIntegers()[0]);
                if (compression == TiffCompression.CcittGroup3) Assert.Equal(layout == "2d" ? 1 : 0, tags.Single(t => t.Id == 292).AsIntegers()[0]);
                var back = Tiff.Decode(file);
                Assert.True(back.Color == ColorModel.Gray && back.SampleType == SampleType.UInt8, what);
                Assert.True(img.GetData<byte>().AsSpan().SequenceEqual(back.GetData<byte>()), what + ": round trip differs");
                var ns = new ForwardOnlyStream();
                Tiff.Encode(gray, ns, o);
                Assert.True(file.AsSpan().SequenceEqual(ns.ToArray()), what + ": non-seekable output differs");
                // (libtiff reads JBIG files of a single strip only)
                if (compression == TiffCompression.Jbig ? layout is "strip" or "be" : layout == "strips" || layout == "2d" || w == 2700 || w == 1)
                {
                    // libtiff (through Pillow) shows the same picture
                    var oracle = Oracle(file);
                    if (oracle != null) Assert.True(img.GetData<byte>().AsSpan().SequenceEqual(oracle.GetData<byte>()), what + ": libtiff decodes a different picture");
                }
            }
        // a 1-bit file is much smaller, and Group 4 smaller still
        var page = MakeBilevel(800, 600, 5);
        int bilevel = Encode(page, new TiffEncodeOptions { Compression = compression, Bilevel = true }).Length;
        Assert.True(bilevel < 800 * 600 / 8 + 2000);
        if (compression == TiffCompression.CcittGroup4) Assert.True(bilevel < 800 * 600 / 16);
        Assert.Throws<ArgumentException>(() => Encode(MakeImage(8, 8, ColorModel.Rgb, SampleType.UInt8, 3), new TiffEncodeOptions { Compression = compression, Bilevel = true }));
        Assert.Throws<ArgumentException>(() => Encode(MakeImage(8, 8, ColorModel.Gray, SampleType.UInt16, 1), new TiffEncodeOptions { Compression = compression, Bilevel = true }));
    }

    /// <summary>Smooth photo like content (JPEG is compared by PSNR).</summary>
    static RawImage MakeSmooth(int w, int h, int channels)
    {
        var img = RawImage.Create(w, h, channels == 1 ? ColorModel.Gray : ColorModel.Rgb, SampleType.UInt8);
        var a = img.GetData<byte>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < channels; c++)
                    a[(y * w + x) * channels + c] = (byte)(128 + 100 * Math.Sin(x / (23.0 + 5 * c) + c) * Math.Cos(y / (19.0 + 3 * c)));
        return img;
    }

    [Theory]
    [InlineData(1, JpegSubsampling.Auto, 90, 0)]
    [InlineData(1, JpegSubsampling.Auto, 60, 32)]
    [InlineData(3, JpegSubsampling.Auto, 92, 0)]
    [InlineData(3, JpegSubsampling.Auto, 75, 0)]
    [InlineData(3, JpegSubsampling.Chroma444, 80, 64)]
    [InlineData(3, JpegSubsampling.Chroma422, 85, 0)]
    [InlineData(3, JpegSubsampling.Chroma420, 85, 48)]
    [InlineData(3, JpegSubsampling.Auto, -90, 0)]     // negative quality: RGB components (JpegRgb)
    [InlineData(3, JpegSubsampling.Auto, -80, 64)]
    public void EncodesJpegCompressed(int channels, JpegSubsampling subsampling, int quality, int tile)
    {
        bool rgbComponents = quality < 0;
        quality = Math.Abs(quality);
        var img = MakeSmooth(203, 131, channels);
        var o = new TiffEncodeOptions { Compression = TiffCompression.Jpeg, JpegQuality = quality, JpegSubsampling = subsampling, TileWidth = tile, TileHeight = tile, RowsPerStrip = 40, JpegRgb = rgbComponents };
        var file = Encode(img, o);
        var tags = Tiff.ReadTags(file);
        long Tag(int id) => tags.Single(t => t.Id == id).AsIntegers()[0];
        Assert.Equal(7, Tag(259));
        Assert.Equal(rgbComponents ? 2 : channels == 3 ? 6 : 1, Tag(262));
        if (channels == 3 && !rgbComponents)
        {
            var expected = subsampling switch
            {
                JpegSubsampling.Chroma444 => new long[] { 1, 1 },
                JpegSubsampling.Chroma422 => new long[] { 2, 1 },
                JpegSubsampling.Chroma420 => new long[] { 2, 2 },
                _ => quality >= 90 ? new long[] { 1, 1 } : new long[] { 2, 2 },
            };
            Assert.Equal(expected, tags.Single(t => t.Id == 530).AsIntegers());
        }
        if (tile == 0) Assert.Equal(0, Tag(278) % 16); // strips hold whole MCU rows
        var back = Tiff.Decode(file);
        Assert.Equal((img.Width, img.Height, img.Channels, img.Color), (back.Width, back.Height, back.Channels, back.Color));
        Assert.True(TestUtil.Psnr(img, back, 255) > 34, $"PSNR {TestUtil.Psnr(img, back, 255):F1}");
        TestUtil.AssertEqual(back, Tiff.Decode(file, ForcedParallel()));
        Assert.True(file.Length < img.GetBytes().Length / 3);
        var ns = new ForwardOnlyStream();
        Tiff.Encode(img, ns, o);
        Assert.True(file.AsSpan().SequenceEqual(ns.ToArray()));
        // libjpeg-turbo (tifffile + imagecodecs) decodes our file to exactly the same samples as we do
        var oracle = Oracle(file);
        if (oracle != null) TestUtil.AssertEqual(oracle, back);

        Assert.Throws<ArgumentException>(() => Encode(MakeImage(16, 16, ColorModel.Rgba, SampleType.UInt8, 4), o));
        Assert.Throws<ArgumentException>(() => Encode(MakeImage(16, 16, ColorModel.Gray, SampleType.UInt16, 1), o));
    }

    [Theory]
    [InlineData(1, JpegSubsampling.Auto, 92, 0)]
    [InlineData(3, JpegSubsampling.Chroma444, 90, 64)]
    [InlineData(3, JpegSubsampling.Chroma420, 80, 0)]
    public void EncodesJpeg12Bit(int channels, JpegSubsampling subsampling, int quality, int tile)
    {
        // UInt16 images with samples 0..4095 are written as 12-bit JPEG
        var smooth = MakeSmooth(160, 97, channels);
        var img = RawImage.Create(160, 97, smooth.Color, SampleType.UInt16);
        var a = img.GetData<ushort>();
        var s8 = smooth.GetData<byte>();
        for (int i = 0; i < a.Length; i++) a[i] = (ushort)(s8[i] * 16 + (i * 7 & 15));
        var o = new TiffEncodeOptions { Compression = TiffCompression.Jpeg, JpegQuality = quality, JpegSubsampling = subsampling, TileWidth = tile, TileHeight = tile, RowsPerStrip = 32 };
        var file = Encode(img, o);
        var tags = Tiff.ReadTags(file);
        Assert.Equal(12, tags.Single(t => t.Id == 258).AsIntegers()[0]);
        Assert.Equal(7, tags.Single(t => t.Id == 259).AsIntegers()[0]);
        Assert.Equal(12, Tiff.GetInfo(file).BitsPerSample);
        var back = Tiff.Decode(file);
        Assert.Equal((SampleType.UInt16, img.Color, img.Width, img.Height), (back.SampleType, back.Color, back.Width, back.Height));
        Assert.True(TestUtil.Psnr(img, back, 4095) > 38, $"PSNR {TestUtil.Psnr(img, back, 4095):F1}");
        Assert.All(back.GetData<ushort>(), v => Assert.True(v <= 4095));
        // libjpeg-turbo's 12-bit decoder (tifffile + imagecodecs) gives exactly the same samples
        var oracle = Oracle(file);
        if (oracle != null) TestUtil.AssertEqual(oracle, back);

        a[5] = 4096;
        Assert.Throws<ArgumentException>(() => Encode(img, o));
    }

    // ------------------------------------------------------------------------------------------ overviews, cloud optimized

    /// <summary>Independent 2 x 2 mean for the test (integer rounding half up, edges use the pixels that exist).</summary>
    static double[] BoxHalf(double[] s, int w, int h, int ch, bool round)
    {
        int ow = (w + 1) / 2, oh = (h + 1) / 2;
        var d = new double[ow * oh * ch];
        for (int y = 0; y < oh; y++)
            for (int x = 0; x < ow; x++)
                for (int c = 0; c < ch; c++)
                {
                    double sum = 0; int n = 0;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                            if (2 * y + dy < h && 2 * x + dx < w) { sum += s[((2 * y + dy) * w + 2 * x + dx) * ch + c]; n++; }
                    d[(y * ow + x) * ch + c] = round ? Math.Floor(sum / n + 0.5) : sum / n;
                }
        return d;
    }

    [Theory]
    [InlineData(SampleType.UInt8, 3)]
    [InlineData(SampleType.UInt16, 1)]
    [InlineData(SampleType.Int16, 1)]
    [InlineData(SampleType.Float32, 1)]
    [InlineData(SampleType.Float64, 2)]
    [InlineData(SampleType.UInt64, 1)]
    public void BuildsOverviews(SampleType type, int channels)
    {
        var img = MakeImage(301, 173, RawImage.DefaultColorModel(channels), type, channels);
        if (type is SampleType.Float32 or SampleType.Float64)
        {
            // (finite values: a mean with NaN or infinity in it is not worth comparing)
            var b = img.GetBytes();
            var clean = MakeImage(301, 173, img.Color, SampleType.UInt16, channels);
            var u = clean.GetData<ushort>();
            if (type == SampleType.Float32) for (int i = 0; i < u.Length; i++) img.GetData<float>()[i] = u[i] / 7f - 100;
            else for (int i = 0; i < u.Length; i++) img.GetData<double>()[i] = u[i] / 7.0 - 100;
        }
        var o = new TiffEncodeOptions { BuildOverviews = true, OverviewMinSize = 40, TileWidth = 64, TileHeight = 64, Compression = TiffCompression.Lzw };
        var file = Encode(img, o);
        var infos = Tiff.GetInfos(file);
        Assert.Equal(new[] { (301, 173), (151, 87), (76, 44), (38, 22) }, infos.Select(i => (i.Width, i.Height)).ToArray());
        bool integer = !(type is SampleType.Float32 or SampleType.Float64);
        RawImage level = img;
        for (int i = 0; i < infos.Length; i++)
        {
            var page = Tiff.Decode(file, new TiffDecodeOptions { ImageIndex = i });
            var tags = Tiff.ReadTags(file, i);
            Assert.Equal(i > 0, tags.Any(t => t.Id == 254 && t.AsIntegers()[0] == 1));
            Assert.Contains(tags, t => t.Id == 322);
            if (i > 0)
            {
                // each level is the box average of the one before
                var expected = BoxHalf(ToDoubles(level), level.Width, level.Height, channels, integer);
                var got = ToDoubles(page);
                double tol = type == SampleType.UInt64 ? 4096 : type == SampleType.Float32 ? 1e-3 : integer ? 0 : 1e-9;
                for (int k = 0; k < got.Length; k++) Assert.True(Math.Abs(expected[k] - got[k]) <= tol, $"level {i} sample {k}: {got[k]} vs {expected[k]}");
            }
            else Assert.True(img.GetBytes().SequenceEqual(page.GetBytes()));
            level = page;
            var oracle = Oracle(file, i);
            if (oracle != null) Assert.True(page.GetBytes().SequenceEqual(oracle.GetBytes()), $"level {i}: independent decoder disagrees");
        }
        // GDAL sees the reduced pages as overviews of the image
        var seen = PythonOn(file, "import rasterio\nds = rasterio.open(PATH)\nprint(ds.width, ds.height, ds.count, ds.overviews(1))");
        if (seen != null) Assert.Equal($"301 173 {channels} [2, 4, 8]", seen);
        // without the option there is just the image
        Assert.Equal(1, Tiff.GetImageCount(Encode(img, new TiffEncodeOptions())));
    }

    static double[] ToDoubles(RawImage img) => img.SampleType switch
    {
        SampleType.UInt8 => Array.ConvertAll((byte[])img.Data, x => (double)x),
        SampleType.UInt16 => Array.ConvertAll((ushort[])img.Data, x => (double)x),
        SampleType.Int16 => Array.ConvertAll((short[])img.Data, x => (double)x),
        SampleType.Float32 => Array.ConvertAll((float[])img.Data, x => (double)x),
        SampleType.UInt64 => Array.ConvertAll((ulong[])img.Data, x => (double)x),
        _ => (double[])img.Data,
    };

    [Theory]
    [InlineData(TiffCompression.Deflate, false, false)]
    [InlineData(TiffCompression.Lzw, true, false)]
    [InlineData(TiffCompression.Zstd, false, true)]
    [InlineData(TiffCompression.Jpeg, false, false)]
    public void WritesCloudOptimizedGeoTiff(TiffCompression compression, bool big, bool nonSeekable)
    {
        var img = compression == TiffCompression.Jpeg ? MakeSmooth(1300, 900, 3) : MakeImage(1300, 900, ColorModel.Rgb, SampleType.UInt8, 3);
        var o = new TiffEncodeOptions { CloudOptimized = true, Compression = compression, TileWidth = 256, TileHeight = 256, BigTiff = big ? true : null };
        o.ExtraTags.Add(TiffTag.Doubles(33550, 0.5, 0.5, 0.0));
        o.ExtraTags.Add(TiffTag.Doubles(33922, 0, 0, 0, 600000.0, 5300000.0, 0));
        o.ExtraTags.Add(TiffTag.Shorts(34735, 1, 1, 0, 3, 1024, 0, 1, 1, 1025, 0, 1, 1, 3072, 0, 1, 32633));
        byte[] file;
        if (nonSeekable) { var ns = new ForwardOnlyStream(); Tiff.Encode(img, ns, o); file = ns.ToArray(); }
        else file = Encode(img, o);

        // The layout rules of GDAL's validate_cloud_optimized_geotiff: tiled, with overviews, the main directory right
        // after the header, the directories in order and before all pixel data, the data of smaller overviews before
        // that of larger ones and the full resolution data last.
        int count = Tiff.GetImageCount(file);
        Assert.Equal(4, count); // 1300x900, 650x450, 325x225, 163x113 (<= 256)
        long header = file[2] == 43 ? 16 : 8;
        long firstIfd = file[2] == 43 ? BitConverter.ToInt64(file, 8) : BitConverter.ToUInt32(file, 4);
        // GDAL's structural metadata block sits between the header and the first directory
        string ghost = System.Text.Encoding.ASCII.GetString(file, (int)header, 183);
        Assert.StartsWith("GDAL_STRUCTURAL_METADATA_SIZE=000140 bytes\nLAYOUT=IFDS_BEFORE_DATA\nBLOCK_ORDER=ROW_MAJOR\nBLOCK_LEADER=SIZE_AS_UINT4\nBLOCK_TRAILER=LAST_4_BYTES_REPEATED\n", ghost);
        Assert.Equal(header + 184, firstIfd);
        var firstBlock = new long[count];
        long lastDirectoryEnd = 0, minData = long.MaxValue;
        for (int i = 0; i < count; i++)
        {
            var tags = Tiff.ReadTags(file, i);
            long Tag(int id) => tags.Single(t => t.Id == id).AsIntegers()[0];
            Assert.Equal(256, Tag(322));
            Assert.Equal(256, Tag(323));
            Assert.Equal(i > 0, tags.Any(t => t.Id == 254 && t.AsIntegers()[0] == 1));
            Assert.Equal(i == 0, tags.Any(t => t.Id == 34735)); // georeferencing on the full resolution image only
            var offsets = tags.Single(t => t.Id == 324).AsIntegers();
            var sizes = tags.Single(t => t.Id == 325).AsIntegers();
            Assert.Equal(offsets.OrderBy(v => v), offsets);     // tiles in order
            for (int k = 0; k < offsets.Length; k++)
            {
                // every tile: its size in front, its last four bytes repeated behind
                Assert.Equal(sizes[k], BitConverter.ToUInt32(file, (int)offsets[k] - 4));
                Assert.True(file.AsSpan((int)(offsets[k] + sizes[k] - 4), 4).SequenceEqual(file.AsSpan((int)(offsets[k] + sizes[k]), 4)));
            }
            firstBlock[i] = offsets[0];
            minData = Math.Min(minData, offsets.Min());
        }
        for (int i = 0; i + 1 < count; i++) Assert.True(firstBlock[i] > firstBlock[i + 1], "overview data has to precede the data of the larger level");
        // every directory (found by walking the chain) lies before the first tile
        long ifd = firstIfd;
        for (int i = 0; i < count; i++)
        {
            Assert.True(ifd >= lastDirectoryEnd && ifd < minData, $"directory {i} at {ifd} is not in the header part");
            bool bigFile = file[2] == 43;
            long entries = bigFile ? BitConverter.ToInt64(file, (int)ifd) : BitConverter.ToUInt16(file, (int)ifd);
            long nextPos = ifd + (bigFile ? 8 + entries * 20 : 2 + entries * 12);
            lastDirectoryEnd = nextPos;
            ifd = bigFile ? BitConverter.ToInt64(file, (int)nextPos) : BitConverter.ToUInt32(file, (int)nextPos);
        }
        Assert.Equal(0, ifd);

        var back = Tiff.Decode(file);
        if (compression == TiffCompression.Jpeg) Assert.True(TestUtil.Psnr(img, back, 255) > 34);
        else Assert.True(img.GetBytes().SequenceEqual(back.GetBytes()));
        // a preview needs only the header part and the smallest overview
        var s = new SparseStream();
        s.Write(file);
        s.Position = 0; s.BytesRead = 0;
        var preview = Tiff.Decode(s, new TiffDecodeOptions { ImageIndex = count - 1 });
        Assert.Equal((163, 113), (preview.Width, preview.Height));
        Assert.True(s.BytesRead < file.Length / 8, $"read {s.BytesRead} of {file.Length}");

        // GDAL: georeferencing, block layout and overviews as intended
        var seen = PythonOn(file, string.Join("\n",
            "import rasterio",
            "ds = rasterio.open(PATH)",
            "print(ds.crs.to_epsg(), ds.transform.a, ds.transform.e, ds.transform.c, ds.transform.f, ds.block_shapes[0], ds.overviews(1), ds.read(1, out_shape=(113, 163)).shape, ds.tags(ns='IMAGE_STRUCTURE').get('LAYOUT'))"));
        if (seen != null) Assert.Equal("32633 0.5 -0.5 600000.0 5300000.0 (256, 256) [2, 4, 8] (113, 163) COG", seen);
        // rio-cogeo's validator (the checks of GDAL's validate_cloud_optimized_geotiff.py), strict; skipped when it is not installed
        var valid = PythonOn(file, string.Join("\n",
            "try:",
            "    from rio_cogeo.cogeo import cog_validate",
            "    ok, errors, warn = cog_validate(PATH, strict=True, quiet=True)",
            "    print(ok, errors, warn)",
            "except ImportError:",
            "    print('unavailable')"));
        if (valid != null && valid != "unavailable") Assert.Equal("True [] []", valid);

        // several images: all directories still come first
        var two = new MemoryStream();
        Tiff.Encode(new[] { img, MakeImage(700, 300, ColorModel.Gray, SampleType.UInt16, 1) }, two, new TiffEncodeOptions { CloudOptimized = true, TileWidth = 256, TileHeight = 256 });
        var twoBytes = two.ToArray();
        Assert.Equal(4 + 3, Tiff.GetImageCount(twoBytes));
        long firstData = Enumerable.Range(0, 7).Min(i => Tiff.ReadTags(twoBytes, i).Single(t => t.Id == 324).AsIntegers().Min());
        Assert.True(Tiff.ReadTags(twoBytes, 6).Single(t => t.Id == 324).AsIntegers()[0] == firstData + 4 - 4, "the last directory's tiles come first");
        Assert.True(MakeImage(700, 300, ColorModel.Gray, SampleType.UInt16, 1).GetBytes().SequenceEqual(Tiff.Decode(twoBytes, new TiffDecodeOptions { ImageIndex = 4 }).GetBytes()));
    }

    // ------------------------------------------------------------------------------------------ colour spaces, palettes, depths

    /// <summary>Runs python on a file we wrote; the snippet stores an array with write(arr) and it comes back as an image.</summary>
    static RawImage? PythonImage(byte[] encoded, string code)
    {
        if (!TestUtil.PythonAvailable) return null;
        var tmp = Path.Combine(TestUtil.FixtureDir, "_tmp");
        Directory.CreateDirectory(tmp);
        var f = Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".tif");
        try
        {
            File.WriteAllBytes(f, encoded);
            File.WriteAllText(f + ".py", "import sys, os, warnings\nwarnings.filterwarnings('ignore')\nos.environ['OPENCV_LOG_LEVEL'] = 'SILENT'\nsys.path.insert(0, os.getcwd())\nimport numpy as np\nfrom pxref import write_ref\n" +
                "PATH = sys.argv[1]\ndef write(a):\n    write_ref(PATH + '.ref', np.ascontiguousarray(a))\n" + Guarded(code));
            var (exit, output) = TestUtil.RunPython($"\"{f}.py\" \"{f}\"");
            if (TestUtil.CannotVerify(exit, output)) return null; // the snippet's reader is not installed
            Assert.True(exit == 0, "python failed: " + output);
            return TestUtil.LoadRef(f + ".ref");
        }
        finally
        {
            File.Delete(f); File.Delete(f + ".py"); File.Delete(f + ".ref");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    public void EncodesPalette(int bits)
    {
        int n = 1 << bits;
        var rnd = new Random(bits);
        var palette = new ushort[3 * n];
        for (int i = 0; i < palette.Length; i++) palette[i] = (ushort)rnd.Next(65536);
        var img = RawImage.Create(41, 23, ColorModel.Gray, bits <= 8 ? SampleType.UInt8 : SampleType.UInt16);
        if (bits <= 8) { var a = img.GetData<byte>(); for (int i = 0; i < a.Length; i++) a[i] = (byte)rnd.Next(n); }
        else { var a = img.GetData<ushort>(); for (int i = 0; i < a.Length; i++) a[i] = (ushort)rnd.Next(n); }
        foreach (var comp in new[] { TiffCompression.None, TiffCompression.Lzw, TiffCompression.Deflate })
            foreach (bool tiles in new[] { false, true })
            {
                var o = new TiffEncodeOptions { Palette = palette, BitsPerSample = bits, Compression = comp, TileWidth = tiles ? 16 : 0, TileHeight = tiles ? 16 : 0, RowsPerStrip = 7 };
                var file = Encode(img, o);
                var tags = Tiff.ReadTags(file);
                Assert.Equal(3, tags.Single(t => t.Id == 262).AsIntegers()[0]);
                Assert.Equal(bits, tags.Single(t => t.Id == 258).AsIntegers()[0]);
                Assert.Equal(3 * n, tags.Single(t => t.Id == 320).Count);
                var back = Tiff.Decode(file);
                Assert.Equal((ColorModel.Rgb, SampleType.UInt8), (back.Color, back.SampleType));
                var px = back.GetData<byte>();
                for (int i = 0; i < img.Width * img.Height; i++)
                {
                    int idx = bits <= 8 ? img.GetData<byte>()[i] : img.GetData<ushort>()[i];
                    for (int c = 0; c < 3; c++) Assert.Equal((byte)(palette[idx * 3 + c] >> 8), px[i * 3 + c]);
                }
                // tifffile applies the colormap we wrote to the indices we wrote
                var seen = PythonImage(file, "import tifffile\np = tifffile.TiffFile(PATH).pages[0]\nwrite(np.moveaxis(np.asarray(p.colormap)[:, p.asarray().astype(np.intp)], 0, -1) >> 8)");
                if (seen != null) Assert.True(seen.GetData<ushort>().Select(v => (byte)v).SequenceEqual(px), $"{bits} bit {comp}: tifffile sees other colours");
            }
        Assert.Equal(TiffEncodeOptions.PaletteFromRgb8(new byte[] { 0, 128, 255 }), new ushort[] { 0, 128 * 257, 65535 });
        Assert.Throws<ArgumentException>(() => Encode(MakeImage(4, 4, ColorModel.Gray, SampleType.Float32, 1), new TiffEncodeOptions { Palette = palette }));
    }

    [Theory]
    [InlineData(TiffPhotometric.Cmyk, SampleType.UInt8, 4)]
    [InlineData(TiffPhotometric.Cmyk, SampleType.UInt16, 5)]
    [InlineData(TiffPhotometric.CieLab, SampleType.UInt8, 3)]
    [InlineData(TiffPhotometric.CieLab, SampleType.UInt16, 4)]
    [InlineData(TiffPhotometric.IccLab, SampleType.UInt8, 3)]
    [InlineData(TiffPhotometric.ItuLab, SampleType.UInt16, 3)]
    [InlineData(TiffPhotometric.Rgb, SampleType.Float32, 5)]
    [InlineData(TiffPhotometric.MinIsWhite, SampleType.UInt8, 1)]
    [InlineData(TiffPhotometric.MinIsWhite, SampleType.UInt16, 2)]
    [InlineData(TiffPhotometric.MinIsWhite, SampleType.Int16, 1)]
    public void EncodesPhotometric(TiffPhotometric photometric, SampleType type, int channels)
    {
        var img = MakeImage(37, 23, ColorModel.Other, type, channels);
        int colour = photometric switch { TiffPhotometric.Cmyk => 4, TiffPhotometric.MinIsWhite => 1, _ => 3 };
        int tag = photometric switch { TiffPhotometric.Cmyk => 5, TiffPhotometric.CieLab => 8, TiffPhotometric.IccLab => 9, TiffPhotometric.ItuLab => 10, TiffPhotometric.Rgb => 2, _ => 0 };
        foreach (bool planar in new[] { false, true })
            foreach (var comp in new[] { TiffCompression.None, TiffCompression.Lzw })
            {
                var o = new TiffEncodeOptions { Photometric = photometric, Compression = comp, Planar = planar, RowsPerStrip = 6 };
                if (channels > colour) o.ExtraSamples = Enumerable.Repeat(TiffExtraSample.UnassociatedAlpha, 1).Concat(Enumerable.Repeat(TiffExtraSample.Unspecified, channels - colour - 1)).ToArray();
                var file = Encode(img, o);
                var tags = Tiff.ReadTags(file);
                Assert.Equal(tag, tags.Single(t => t.Id == 262).AsIntegers()[0]);
                if (channels > colour) Assert.Equal(2, tags.Single(t => t.Id == 338).AsIntegers()[0]);
                // the samples as we stored them come back with the conversion switched off ...
                var raw = Tiff.Decode(file, new TiffDecodeOptions { ConvertToRgb = false });
                Assert.True(img.GetBytes().SequenceEqual(raw.GetBytes()), $"{photometric} {type} planar {planar} {comp}");
                // ... and the default decode interprets them
                var shown = Tiff.Decode(file);
                Assert.Equal(photometric == TiffPhotometric.MinIsWhite ? channels : photometric == TiffPhotometric.Rgb ? channels : channels > colour ? 4 : 3, shown.Channels);
                // what an independent reader finds in the file
                var oracle = Oracle(file);
                if (oracle == null) continue;
                if (photometric == TiffPhotometric.MinIsWhite && type != SampleType.Int16)
                {
                    // stored inverted, so that a reader's inversion restores the image
                    var e = img.GetBytes().ToArray();
                    int bps = RawImage.BytesPerSample(type);
                    for (int i = 0; i < e.Length; i += bps * channels) for (int b = 0; b < bps; b++) e[i + b] = (byte)~e[i + b];
                    Assert.True(e.AsSpan().SequenceEqual(oracle.GetBytes()), "MinIsWhite data is not stored inverted");
                }
                else Assert.True(img.GetBytes().SequenceEqual(oracle.GetBytes()), $"{photometric} {type}: independent decoder disagrees");
            }
        Assert.Throws<ArgumentException>(() => Encode(MakeImage(4, 4, ColorModel.Rgb, SampleType.UInt8, 3), new TiffEncodeOptions { Photometric = TiffPhotometric.Cmyk }));
        Assert.Throws<ArgumentException>(() => Encode(img, new TiffEncodeOptions { Photometric = photometric, ExtraSamples = new TiffExtraSample[7] }));
    }

    [Theory]
    [InlineData(JpegSubsampling.Chroma444, TiffCompression.None, 0)]
    [InlineData(JpegSubsampling.Chroma422, TiffCompression.Lzw, 0)]
    [InlineData(JpegSubsampling.Chroma420, TiffCompression.Deflate, 0)]
    [InlineData(JpegSubsampling.Auto, TiffCompression.None, 32)]
    [InlineData(JpegSubsampling.Chroma420, TiffCompression.PackBits, 48)]
    public void EncodesYCbCrWithoutJpeg(JpegSubsampling subsampling, TiffCompression compression, int tile)
    {
        foreach (var (w, h) in new[] { (64, 48), (67, 45), (1, 1), (3, 5) })
        {
            var img = w >= 60 ? MakeSmooth(w, h, 3) : MakeImage(w, h, ColorModel.Rgb, SampleType.UInt8, 3);
            var o = new TiffEncodeOptions { Photometric = TiffPhotometric.YCbCr, JpegSubsampling = subsampling, Compression = compression, TileWidth = tile, TileHeight = tile, RowsPerStrip = 10 };
            var file = Encode(img, o);
            var tags = Tiff.ReadTags(file);
            Assert.Equal(6, tags.Single(t => t.Id == 262).AsIntegers()[0]);
            var expectedSub = subsampling switch { JpegSubsampling.Chroma444 => new long[] { 1, 1 }, JpegSubsampling.Chroma422 => new long[] { 2, 1 }, _ => new long[] { 2, 2 } };
            Assert.Equal(expectedSub, tags.Single(t => t.Id == 530).AsIntegers());
            var back = Tiff.Decode(file);
            Assert.Equal((w, h, ColorModel.Rgb), (back.Width, back.Height, back.Color));
            if (w >= 60) Assert.True(TestUtil.Psnr(img, back, 255) > 38, $"PSNR {TestUtil.Psnr(img, back, 255):F1}");
            var ns = new ForwardOnlyStream();
            Tiff.Encode(img, ns, o);
            Assert.True(file.AsSpan().SequenceEqual(ns.ToArray()));
            // libtiff's YCbCr reading (through OpenCV) of our file is what we decode, up to its fixed point rounding
            var seen = PythonImage(file, "import cv2\na = cv2.imread(PATH, cv2.IMREAD_UNCHANGED)\nassert a is not None\nwrite(a[:, :, ::-1] if a.ndim == 3 else a)");
            if (seen != null && seen.Channels == 3) TestUtil.AssertEqual(seen, back, 2);
        }
        Assert.Throws<ArgumentException>(() => Encode(MakeImage(4, 4, ColorModel.Gray, SampleType.UInt8, 1), new TiffEncodeOptions { Photometric = TiffPhotometric.YCbCr }));
    }

    [Theory]
    [InlineData(3, SampleType.UInt8, 1)]
    [InlineData(5, SampleType.UInt8, 3)]
    [InlineData(7, SampleType.UInt8, 2)]
    [InlineData(10, SampleType.UInt16, 1)]
    [InlineData(12, SampleType.UInt16, 3)]
    [InlineData(15, SampleType.UInt16, 4)]
    [InlineData(24, SampleType.UInt32, 1)]
    [InlineData(31, SampleType.UInt32, 3)]
    [InlineData(12, SampleType.Int16, 1)]
    [InlineData(5, SampleType.Int8, 3)]
    [InlineData(20, SampleType.Int32, 1)]
    public void EncodesOddBitDepths(int bits, SampleType type, int channels)
    {
        bool signed = type is SampleType.Int8 or SampleType.Int16 or SampleType.Int32;
        var img = RawImage.Create(41, 23, RawImage.DefaultColorModel(channels), type, channels);
        var rnd = new Random(bits * 10 + channels);
        long lo = signed ? -(1L << (bits - 1)) : 0, hi = signed ? 1L << (bits - 1) : 1L << bits;
        var bytes = img.GetBytes();
        int bps = RawImage.BytesPerSample(type);
        for (int i = 0; i < bytes.Length; i += bps)
        {
            long v = i == 0 ? hi - 1 : i == bps ? lo : rnd.NextInt64(lo, hi);
            for (int b = 0; b < bps; b++) bytes[i + b] = (byte)(v >> (8 * b));
        }
        foreach (bool planar in new[] { false, true })
            foreach (var comp in new[] { TiffCompression.None, TiffCompression.Lzw, TiffCompression.Deflate })
                foreach (bool be in new[] { false, true })
                {
                    var o = new TiffEncodeOptions { BitsPerSample = bits, Planar = planar, Compression = comp, BigEndian = be, RowsPerStrip = 6, TileWidth = planar ? 16 : 0, TileHeight = planar ? 16 : 0 };
                    var file = Encode(img, o);
                    string what = $"{bits} bit {type} x{channels} planar {planar} {comp} be {be}";
                    Assert.Equal(bits, Tiff.ReadTags(file).Single(t => t.Id == 258).AsIntegers()[0]);
                    Assert.DoesNotContain(Tiff.ReadTags(file), t => t.Id == 317);
                    var back = Tiff.Decode(file, new TiffDecodeOptions { BitScaling = TiffBitScaling.None });
                    Assert.True(back.SampleType == type && bytes.SequenceEqual(back.GetBytes()), what);
                    if (be || comp == TiffCompression.Deflate) continue;
                    // tifffile unpacks the unsigned ones, GDAL the signed ones (as unsigned bit patterns)
                    var seen = signed
                        ? PythonImage(file, "import rasterio\na = np.moveaxis(rasterio.open(PATH).read(), 0, -1)\n" +
                            $"v = a.astype(np.int64)\nwrite(np.where(v >= {1L << (bits - 1)}, v - {1L << bits}, v).astype(a.dtype.name[1:]))")
                        : Oracle(file);
                    if (seen != null) Assert.True(seen.SampleType == type && bytes.SequenceEqual(seen.GetBytes()), what + ": independent decoder disagrees");
                }
        Assert.Throws<ArgumentException>(() => Encode(img, new TiffEncodeOptions { BitsPerSample = bps * 8 + 1 }));
        Assert.Throws<ArgumentException>(() => Encode(MakeImage(4, 4, ColorModel.Gray, SampleType.Float32, 1), new TiffEncodeOptions { BitsPerSample = 12 }));
    }

    [Theory]
    [InlineData(3, true, 0)]
    [InlineData(4, true, 32)]
    [InlineData(3, false, 0)]
    [InlineData(4, false, 64)]
    public void EncodesWebp(int channels, bool lossless, int tile)
    {
        var img = lossless ? MakeImage(131, 77, channels == 4 ? ColorModel.Rgba : ColorModel.Rgb, SampleType.UInt8, channels) : MakeSmooth(131, 77, 3);
        if (!lossless && channels == 4)
        {
            var rgba = RawImage.Create(131, 77, ColorModel.Rgba, SampleType.UInt8);
            var s3 = img.GetData<byte>(); var d4 = rgba.GetData<byte>();
            for (int i = 0; i < 131 * 77; i++) { d4[i * 4] = s3[i * 3]; d4[i * 4 + 1] = s3[i * 3 + 1]; d4[i * 4 + 2] = s3[i * 3 + 2]; d4[i * 4 + 3] = (byte)(i % 131 * 2); }
            img = rgba;
        }
        var o = new TiffEncodeOptions { Compression = TiffCompression.Webp, WebpLossless = lossless, WebpQuality = 85, TileWidth = tile, TileHeight = tile, RowsPerStrip = 25 };
        var file = Encode(img, o);
        Assert.Equal(50001, Tiff.ReadTags(file).Single(t => t.Id == 259).AsIntegers()[0]);
        var back = Tiff.Decode(file);
        Assert.Equal((img.Width, img.Height, img.Channels, img.Color), (back.Width, back.Height, back.Channels, back.Color));
        if (lossless) Assert.True(img.GetBytes().SequenceEqual(back.GetBytes()));
        else Assert.True(TestUtil.Psnr(img, back, 255) > 32, $"PSNR {TestUtil.Psnr(img, back, 255):F1}");
        // libwebp (tifffile + imagecodecs) decodes our file to the same samples
        var oracle = Oracle(file);
        if (oracle != null) TestUtil.AssertEqual(oracle, back);
        Assert.Throws<ArgumentException>(() => Encode(MakeImage(8, 8, ColorModel.Gray, SampleType.UInt8, 1), o));
        Assert.Throws<ArgumentException>(() => Encode(MakeImage(8, 8, ColorModel.Rgb, SampleType.UInt16, 3), o));
    }

    [Theory]
    [InlineData(SampleType.UInt8, 3, 0)]
    [InlineData(SampleType.UInt8, 1, 32)]
    [InlineData(SampleType.UInt8, 4, 64)]
    [InlineData(SampleType.UInt16, 3, 0)]
    [InlineData(SampleType.Int16, 1, 32)]
    [InlineData(SampleType.UInt16, 5, 0)]
    public void EncodesJpeg2000(SampleType type, int channels, int tile)
    {
        var img = MakeImage(131, 77, RawImage.DefaultColorModel(channels), type, channels);
        foreach (bool planar in new[] { false, true })
        {
            var o = new TiffEncodeOptions { Compression = TiffCompression.Jpeg2000, TileWidth = tile, TileHeight = tile, RowsPerStrip = 30, Planar = planar };
            var file = Encode(img, o);
            Assert.Equal(34712, Tiff.ReadTags(file).Single(t => t.Id == 259).AsIntegers()[0]);
            var back = Tiff.Decode(file);
            Assert.Equal((img.Color, img.SampleType), (back.Color, back.SampleType));
            Assert.True(img.GetBytes().SequenceEqual(back.GetBytes()), "lossless round trip differs");
            // OpenJPEG (tifffile + imagecodecs) decodes our codestreams to the same samples
            var oracle = Oracle(file);
            if (oracle != null) Assert.True(img.GetBytes().SequenceEqual(oracle.GetBytes()), "OpenJPEG decodes other samples");
        }
        Assert.Throws<ArgumentException>(() => Encode(MakeImage(8, 8, ColorModel.Gray, SampleType.Float32, 1), new TiffEncodeOptions { Compression = TiffCompression.Jpeg2000 }));
    }

    /// <summary>A smooth pattern with some noise: integers over their range, floats in 0..1 (lossy JPEG XL's nominal range).</summary>
    static RawImage MakeJx(int w, int h, int channels, SampleType type)
    {
        var img = RawImage.Create(w, h, RawImage.DefaultColorModel(channels), type, channels);
        var rnd = new Random(channels * 31 + (int)type);
        int n = w * h * channels;
        double V(int i)
        {
            int c = i % channels, x = i / channels % w, y = i / channels / w;
            return Math.Clamp(0.5 + 0.4 * Math.Sin(x / (9.0 + 2 * c) + c) * Math.Cos(y / (7.0 + c)) + 0.06 * rnd.NextDouble(), 0, 1);
        }
        switch (type)
        {
            case SampleType.UInt8: { var a = img.GetData<byte>(); for (int i = 0; i < n; i++) a[i] = (byte)Math.Round(V(i) * 255); break; }
            case SampleType.UInt16: { var a = img.GetData<ushort>(); for (int i = 0; i < n; i++) a[i] = (ushort)Math.Round(V(i) * 65535); break; }
            case SampleType.Float16: { var a = img.GetData<Half>(); for (int i = 0; i < n; i++) a[i] = (Half)V(i); break; }
            default: { var a = img.GetData<float>(); for (int i = 0; i < n; i++) a[i] = (float)V(i); break; }
        }
        return img;
    }

    [Theory]
    [InlineData(TiffCompression.JpegXl, SampleType.UInt8, 1, false, 0, true)]
    [InlineData(TiffCompression.JpegXl, SampleType.UInt8, 2, false, 32, true)]
    [InlineData(TiffCompression.JpegXl, SampleType.UInt8, 3, false, 32, true)]
    [InlineData(TiffCompression.JpegXl, SampleType.UInt8, 4, false, 0, true)]
    [InlineData(TiffCompression.JpegXl, SampleType.UInt8, 5, true, 16, true)]
    [InlineData(TiffCompression.JpegXl, SampleType.UInt16, 3, false, 0, true)]
    [InlineData(TiffCompression.JpegXl, SampleType.UInt16, 4, true, 32, true)]
    [InlineData(TiffCompression.JpegXl, SampleType.Float16, 3, false, 32, true)]
    [InlineData(TiffCompression.JpegXl, SampleType.Float32, 1, false, 0, true)]
    [InlineData(TiffCompression.JpegXl, SampleType.Float32, 4, false, 16, true)]
    [InlineData(TiffCompression.JpegXl, SampleType.UInt8, 3, false, 32, false)]
    [InlineData(TiffCompression.JpegXl, SampleType.UInt8, 4, false, 0, false)]
    [InlineData(TiffCompression.JpegXl, SampleType.UInt8, 1, true, 0, false)]
    [InlineData(TiffCompression.JpegXl, SampleType.UInt16, 3, false, 0, false)]
    [InlineData(TiffCompression.JpegXl, SampleType.Float32, 3, false, 16, false)]
    [InlineData(TiffCompression.JpegXr, SampleType.UInt8, 1, false, 0, true)]
    [InlineData(TiffCompression.JpegXr, SampleType.UInt8, 2, true, 0, true)]
    [InlineData(TiffCompression.JpegXr, SampleType.UInt8, 3, false, 32, true)]
    [InlineData(TiffCompression.JpegXr, SampleType.UInt8, 4, false, 16, true)]
    [InlineData(TiffCompression.JpegXr, SampleType.UInt8, 5, false, 0, true)]
    [InlineData(TiffCompression.JpegXr, SampleType.UInt16, 3, true, 32, true)]
    [InlineData(TiffCompression.JpegXr, SampleType.UInt16, 4, false, 0, true)]
    [InlineData(TiffCompression.JpegXr, SampleType.UInt16, 6, false, 16, true)]
    [InlineData(TiffCompression.JpegXr, SampleType.Float16, 1, false, 16, true)]
    [InlineData(TiffCompression.JpegXr, SampleType.Float16, 4, false, 0, true)]
    [InlineData(TiffCompression.JpegXr, SampleType.Float32, 3, false, 0, true)]
    [InlineData(TiffCompression.JpegXr, SampleType.Float32, 4, false, 32, true)]
    [InlineData(TiffCompression.JpegXr, SampleType.UInt8, 3, false, 32, false)]
    [InlineData(TiffCompression.JpegXr, SampleType.UInt8, 4, false, 0, false)]
    [InlineData(TiffCompression.JpegXr, SampleType.UInt16, 1, false, 16, false)]
    [InlineData(TiffCompression.JpegXr, SampleType.Float16, 3, true, 0, false)]
    public void EncodesJpegXlAndJpegXr(TiffCompression comp, SampleType type, int channels, bool planar, int tile, bool lossless)
    {
        bool jxl = comp == TiffCompression.JpegXl;
        var img = MakeJx(71, 45, channels, type);
        var o = new TiffEncodeOptions
        {
            Compression = comp, Planar = planar, TileWidth = tile, TileHeight = tile, RowsPerStrip = 20,
            JpegXlLossless = lossless, JpegXrLossless = lossless, JpegXlQuality = 85, JpegXrQuality = 85,
        };
        var file = Encode(img, o);
        Assert.Equal(jxl ? 50002 : 34934, Tiff.ReadTags(file).Single(t => t.Id == 259).AsIntegers()[0]);
        Assert.Equal(1, Tiff.ReadTags(file).FirstOrDefault(t => t.Id == 317)?.AsIntegers()[0] ?? 1);
        var back = Tiff.Decode(file);
        Assert.Equal((img.Width, img.Height, img.Channels, img.SampleType, img.Color), (back.Width, back.Height, back.Channels, back.SampleType, back.Color));
        double peak = type switch { SampleType.UInt8 => 255, SampleType.UInt16 => 65535, _ => 1 };
        if (lossless && !(type == SampleType.Float32 && !jxl)) Assert.True(img.GetBytes().SequenceEqual(back.GetBytes()), "lossless round trip differs");
        // (JPEG XR's 32-bit float formats are not bit exact; jxrlib's round trip through tifffile is off by as much)
        else if (lossless) Assert.True(TestUtil.MaxDiff(img, back) < 1e-4, $"JPEG XR float difference {TestUtil.MaxDiff(img, back)}");
        else Assert.True(TestUtil.Psnr(img, back, peak) > 30, $"PSNR {TestUtil.Psnr(img, back, peak):F1}");
        // libjxl / jxrlib (tifffile + imagecodecs) decode our file to the same samples; libjxl rounds its lossy maths
        // its own way (the fixtures above show the same 1 LSB)
        var oracle = Oracle(file);
        if (oracle != null)
        {
            double tol = jxl && !lossless ? (type == SampleType.UInt8 ? 1 : type == SampleType.UInt16 ? 4 : 2e-5) : 0;
            TestUtil.AssertEqual(oracle, back, tol);
        }
    }

    [Fact]
    public void JpegXlAndJpegXrRefuseWhatTheyCannotStore()
    {
        foreach (var comp in new[] { TiffCompression.JpegXl, TiffCompression.JpegXr })
        {
            var o = new TiffEncodeOptions { Compression = comp };
            Assert.Throws<ArgumentException>(() => Encode(MakeImage(8, 8, ColorModel.Gray, SampleType.Int16, 1), o));
            Assert.Throws<ArgumentException>(() => Encode(MakeImage(8, 8, ColorModel.Gray, SampleType.UInt32, 1), o));
            Assert.Throws<ArgumentException>(() => Encode(MakeImage(8, 8, ColorModel.Gray, SampleType.Float64, 1), o));
            Assert.Throws<ArgumentException>(() => Encode(MakeImage(8, 8, ColorModel.Gray, SampleType.UInt8, 1), new TiffEncodeOptions { Compression = comp, BitsPerSample = 4 }));
            Assert.Throws<ArgumentException>(() => Encode(MakeImage(8, 8, ColorModel.Gray, SampleType.UInt8, 1), new TiffEncodeOptions { Compression = comp, Photometric = TiffPhotometric.MinIsWhite }));
            Assert.Throws<ArgumentException>(() => Encode(MakeImage(8, 8, ColorModel.Rgb, SampleType.UInt8, 3), new TiffEncodeOptions { Compression = comp, Photometric = TiffPhotometric.YCbCr }));
            Assert.Throws<ArgumentException>(() => Encode(MakeImage(8, 8, ColorModel.Other, SampleType.Float32, 5), o));
            Assert.Throws<ArgumentException>(() => Encode(MakeImage(8, 8, ColorModel.Other, SampleType.UInt8, 9), o));
            // (as planes they can)
            Assert.Equal(5, Tiff.Decode(Encode(MakeJx(8, 8, 5, SampleType.Float32), new TiffEncodeOptions { Compression = comp, Planar = true })).Channels);
        }
        // JPEG XL takes at most 4 contiguous samples, JPEG XR has no gray + alpha
        Assert.Throws<ArgumentException>(() => Encode(MakeImage(8, 8, ColorModel.Other, SampleType.UInt8, 5), new TiffEncodeOptions { Compression = TiffCompression.JpegXl }));
        Assert.Throws<ArgumentException>(() => Encode(MakeImage(8, 8, ColorModel.GrayAlpha, SampleType.UInt8, 2), new TiffEncodeOptions { Compression = TiffCompression.JpegXr }));
    }

    [Fact]
    public void JpegXlAndJpegXrStripsHoldBareStreams()
    {
        // what tifffile / imagecodecs store (gen_tiff_jx.py): a JPEG XL codestream, a JPEG XR file per strip
        foreach (var (comp, magic) in new[] { (TiffCompression.JpegXl, new byte[] { 0xFF, 0x0A }), (TiffCompression.JpegXr, new byte[] { 0x49, 0x49, 0xBC }) })
        {
            var file = Encode(MakeJx(20, 10, 3, SampleType.UInt8), new TiffEncodeOptions { Compression = comp });
            long off = Tiff.ReadTags(file).Single(t => t.Id == 273).AsIntegers()[0];
            Assert.True(file.AsSpan((int)off, magic.Length).SequenceEqual(magic));
        }
        // the big endian byte order changes nothing inside the codestreams
        var img = MakeJx(33, 17, 3, SampleType.UInt16);
        foreach (var comp in new[] { TiffCompression.JpegXl, TiffCompression.JpegXr })
        {
            var be = Encode(img, new TiffEncodeOptions { Compression = comp, BigEndian = true, TileWidth = 16, TileHeight = 16 });
            Assert.True(img.GetBytes().SequenceEqual(Tiff.Decode(be).GetBytes()));
            var oracle = Oracle(be);
            if (oracle != null) Assert.True(img.GetBytes().SequenceEqual(oracle.GetBytes()), "tifffile reads other samples from the big endian file");
        }
    }

    [Theory]
    [MemberData(nameof(LercFixtures))]
    public void DecodesLercFixture(string file) => CheckFixture(file);

    [Theory]
    [InlineData(TiffCompression.Deflate, 0, false)]
    [InlineData(TiffCompression.Lzw, 64, false)]
    [InlineData(TiffCompression.Deflate, 256, true)]
    public void WritesMasks(TiffCompression compression, int tile, bool cog)
    {
        var img = MakeImage(700, 500, ColorModel.Rgb, SampleType.UInt8, 3);
        var mask = RawImage.Create(700, 500, ColorModel.Gray, SampleType.UInt8);
        var m = mask.GetData<byte>();
        for (int y = 0; y < 500; y++)
            for (int x = 0; x < 700; x++) m[y * 700 + x] = (byte)((x - 350) * (x - 350) + (y - 250) * (y - 250) < 200 * 200 ? 1 + (x & 7) : 0); // any non-zero = valid
        var o = new TiffEncodeOptions { Compression = compression, TileWidth = tile, TileHeight = tile, CloudOptimized = cog, OverviewMinSize = 200, BuildOverviews = !cog && tile > 0 };
        var ms = new MemoryStream();
        Tiff.Encode(new[] { new TiffPage(img) { Mask = mask } }, ms, o);
        var file = ms.ToArray();
        int levels = cog ? 3 : tile > 0 ? 3 : 1;   // 700x500, 350x250, 175x125
        Assert.Equal(2 * levels, Tiff.GetImageCount(file));
        Assert.True(img.GetBytes().SequenceEqual(Tiff.Decode(file).GetBytes()));
        // directory 1 is the mask: 1 bit, transparency mask, the mask subfile type
        var tags = Tiff.ReadTags(file, 1);
        Assert.Equal((4L, 1L, 4L), (tags.Single(t => t.Id == 254).AsIntegers()[0], tags.Single(t => t.Id == 258).AsIntegers()[0], tags.Single(t => t.Id == 262).AsIntegers()[0]));
        var decoded = Tiff.Decode(file, new TiffDecodeOptions { ImageIndex = 1 }).GetData<byte>();
        for (int i = 0; i < m.Length; i++) Assert.Equal(m[i] != 0 ? 255 : 0, decoded[i]);
        if (levels > 1) Assert.Equal(5, Tiff.ReadTags(file, 3).Single(t => t.Id == 254).AsIntegers()[0]);   // mask of an overview
        // GDAL takes it as the internal mask of the dataset
        var seen = PythonImage(file, "import rasterio\nds = rasterio.open(PATH)\n" +
            "assert [f.name for f in ds.mask_flag_enums[0]] == ['per_dataset'], ds.mask_flag_enums\nassert ds.count == 3 and len(ds.overviews(1)) == " + (levels - 1) + "\nwrite(ds.dataset_mask())");
        if (seen != null) Assert.True(seen.GetData<byte>().AsSpan().SequenceEqual(decoded), "GDAL reads another mask");
        Assert.Throws<ArgumentException>(() => Tiff.Encode(new[] { new TiffPage(img) { Mask = MakeImage(10, 10, ColorModel.Gray, SampleType.UInt8, 1) } }, new MemoryStream()));
    }

    // ------------------------------------------------------------------------------------------ multi-page, tags, EXIF

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void EncodesMultiPage(bool big, bool bigEndian)
    {
        var main = MakeImage(64, 48, ColorModel.Rgb, SampleType.UInt16, 3);
        var pages = new List<TiffPage>
        {
            new(main),
            new(MakeImage(32, 24, ColorModel.Rgb, SampleType.UInt16, 3, 2)) { ReducedResolution = true },
            new(MakeImage(16, 12, ColorModel.Rgb, SampleType.UInt16, 3, 3), new TiffEncodeOptions { Compression = TiffCompression.Lzw, TileWidth = 16, TileHeight = 16 }) { ReducedResolution = true },
            new(MakeImage(21, 13, ColorModel.Gray, SampleType.Float32, 1), new TiffEncodeOptions { Compression = TiffCompression.None }),
            new(MakeBilevel(40, 30, 1), new TiffEncodeOptions { Compression = TiffCompression.CcittGroup4 }),
            new(MakeImage(9, 7, ColorModel.Other, SampleType.Int64, 5), new TiffEncodeOptions { Compression = TiffCompression.PackBits }),
        };
        var o = new TiffEncodeOptions { BigTiff = big, BigEndian = bigEndian };
        var ms = new MemoryStream();
        Tiff.Encode(pages, ms, o);
        var file = ms.ToArray();
        Assert.Equal(bigEndian ? (byte)'M' : (byte)'I', file[0]);
        Assert.Equal(pages.Count, Tiff.GetImageCount(file));
        var infos = Tiff.GetInfos(file);
        for (int i = 0; i < pages.Count; i++)
        {
            var src = pages[i].Image;
            var back = Tiff.Decode(file, new TiffDecodeOptions { ImageIndex = i });
            Assert.Equal((src.Width, src.Height, src.Channels, src.SampleType), (infos[i].Width, infos[i].Height, infos[i].Channels, infos[i].SampleType));
            Assert.True(src.GetBytes().SequenceEqual(back.GetBytes()), $"page {i} differs");
            var tags = Tiff.ReadTags(file, i);
            Assert.Equal(pages[i].ReducedResolution, tags.Any(t => t.Id == 254 && t.AsIntegers()[0] == 1));
            var oracle = Oracle(file, i);
            if (oracle != null) Assert.True(src.GetBytes().SequenceEqual(oracle.GetBytes()), $"page {i}: independent decoder disagrees");
        }
        // same bytes on a non-seekable stream, and when the pages come from a lazy sequence
        var ns = new ForwardOnlyStream();
        Tiff.Encode(pages, ns, o);
        Assert.True(file.AsSpan().SequenceEqual(ns.ToArray()));
        int pulled = 0;
        IEnumerable<TiffPage> Lazy() { foreach (var p in pages) { pulled++; yield return p; } }
        var lazy = new MemoryStream();
        Tiff.Encode(Lazy(), lazy, o);
        Assert.Equal(pages.Count, pulled);
        Assert.True(file.AsSpan().SequenceEqual(lazy.ToArray()));

        // the RawImage overload, with automatic BigTIFF choice
        var simple = new MemoryStream();
        Tiff.Encode(pages.Take(2).Select(p => p.Image), simple, new TiffEncodeOptions { BigEndian = bigEndian });
        Assert.Equal(2, Tiff.GetImageCount(simple.ToArray()));
        Assert.Throws<ArgumentException>(() => Tiff.Encode(Array.Empty<TiffPage>(), new MemoryStream()));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void WritesExtraTags(bool big, bool bigEndian)
    {
        var img = MakeImage(40, 30, ColorModel.Gray, SampleType.Float32, 1);
        img.Metadata = new ImageMetadata { DpiX = 96, DpiY = 96 };
        var o = new TiffEncodeOptions { BigTiff = big, BigEndian = bigEndian, TileWidth = 16, TileHeight = 16 };
        o.ExtraTags.Add(TiffTag.Doubles(33550, 0.5, 0.5, 0.0));                                   // ModelPixelScale
        o.ExtraTags.Add(TiffTag.Doubles(33922, 0, 0, 0, 600000.0, 5300000.0, 0));                 // ModelTiepoint
        o.ExtraTags.Add(TiffTag.Shorts(34735, 1, 1, 0, 3, 1024, 0, 1, 1, 1025, 0, 1, 1, 3072, 0, 1, 32633)); // GeoKeyDirectory
        o.ExtraTags.Add(TiffTag.Ascii(42113, "-9999"));                                            // GDAL_NODATA
        o.ExtraTags.Add(TiffTag.Ascii(305, "PixCodec test"));
        o.ExtraTags.Add(TiffTag.Rationals(282, 72.5));                                             // overrides the metadata resolution
        o.ExtraTags.Add(TiffTag.Longs(65000, 1, 2, 4000000000));
        o.ExtraTags.Add(new TiffTag(65001, TiffTagType.SShort, new short[] { -3, 7 }));
        o.ExtraTags.Add(new TiffTag(65002, TiffTagType.SRational, new[] { -0.25, 3.0 }));
        o.ExtraTags.Add(new TiffTag(65003, TiffTagType.Float, new[] { 1.5f }));
        o.ExtraTags.Add(TiffTag.Bytes(65004, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
        o.ExtraTags.Add(new TiffTag(65005, TiffTagType.SLong, new[] { -70000 }));
        o.ExtraTags.Add(new TiffTag(65006, TiffTagType.Byte, new byte[] { 200 }));
        o.ExtraTags.Add(new TiffTag(65007, TiffTagType.SByte, new sbyte[] { -5, 5 }));
        if (big) o.ExtraTags.Add(new TiffTag(65008, TiffTagType.Long8, new[] { 1UL << 40 }));
        var file = Encode(img, o);
        AssertRoundTrip(img, file, "tags");

        var tags = Tiff.ReadTags(file);
        TiffTag Get(int id) => tags.Single(t => t.Id == id);
        Assert.Equal(new[] { 0.5, 0.5, 0.0 }, (double[])Get(33550).Value);
        Assert.Equal(new[] { 0, 0, 0, 600000.0, 5300000.0, 0 }, Get(33922).AsDoubles());
        Assert.Equal(32633, ((ushort[])Get(34735).Value)[15]);
        Assert.Equal("-9999", Get(42113).AsString());
        Assert.Equal("PixCodec test", Get(305).AsString());
        Assert.Equal(72.5, Get(282).AsDoubles()[0]);
        Assert.Equal(96, Get(283).AsDoubles()[0]);
        Assert.Equal(new uint[] { 1, 2, 4000000000 }, (uint[])Get(65000).Value);
        Assert.Equal(new short[] { -3, 7 }, (short[])Get(65001).Value);
        Assert.Equal(new[] { -0.25, 3.0 }, (double[])Get(65002).Value);
        Assert.Equal(1.5f, ((float[])Get(65003).Value)[0]);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, Get(65004).AsBytes());
        Assert.Equal(-70000, ((int[])Get(65005).Value)[0]);
        Assert.Equal(new sbyte[] { -5, 5 }, (sbyte[])Get(65007).Value);
        if (big) Assert.Equal(1UL << 40, ((ulong[])Get(65008).Value)[0]);
        Assert.Equal(tags.Select(t => t.Id).OrderBy(i => i), tags.Select(t => t.Id)); // sorted, as the format demands

        // tifffile sees the same GeoTIFF tags
        var seen = PythonOn(file, "import tifffile\nt = tifffile.TiffFile(PATH).pages[0].tags\n" +
            "print(list(t[33550].value), list(t[33922].value)[3:5], list(t[34735].value)[-1], t[42113].value, t[305].value, t[282].value, list(t[65002].value))");
        if (seen != null) Assert.Equal("[0.5, 0.5, 0.0] [600000.0, 5300000.0] 32633 -9999 PixCodec test (145, 2) [-1, 4, 3, 1]", seen);

        // tags that describe the pixel layout belong to the encoder
        foreach (int id in new[] { 256, 258, 259, 273, 277, 322, 339 })
        {
            var bad = new TiffEncodeOptions();
            bad.ExtraTags.Add(TiffTag.Longs(id, 1));
            Assert.Throws<ArgumentException>(() => Encode(img, bad));
        }
        var needsBig = new TiffEncodeOptions { BigTiff = false };
        needsBig.ExtraTags.Add(new TiffTag(65008, TiffTagType.Long8, new[] { 1UL }));
        Assert.Throws<ArgumentException>(() => Encode(img, needsBig));
        Assert.Throws<ArgumentException>(() => new TiffTag(1, TiffTagType.Short, new short[] { 1 }));
        Assert.Throws<ArgumentException>(() => new TiffTag(1, TiffTagType.Byte, new sbyte[] { 1 }));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ExifTravelsThroughTiff(bool big, bool bigEndian)
    {
        // EXIF made by Pillow: IFD0 tags, an Exif directory with rationals and an interoperability directory, GPS
        var made = PythonOn(Array.Empty<byte>(), string.Join("\n",
            "from PIL import Image",
            "from PIL.TiffImagePlugin import IFDRational as R",
            "e = Image.Exif()",
            "e[0x010F] = 'PixCam'; e[0x0110] = 'Model X'; e[0x0132] = '2026:10:13 12:00:00'; e[0x0112] = 6",
            "x = e.get_ifd(0x8769); x[0x829A] = R(1, 3); x[0x9003] = '2026:10:13 11:59:58'; x[0x8827] = 400; x[0x920A] = R(35, 1); x[0x9286] = b'ASCII\\x00\\x00\\x00hello'",
            "g = e.get_ifd(0x8825); g[1] = 'N'; g[2] = (R(48, 1), R(12, 1), R(3099, 100)); g[3] = 'E'; g[4] = (R(16, 1), R(22, 1), R(0, 1))",
            "sys.stdout.write(e.tobytes().hex())"));
        if (made == null) return;
        var exif = Convert.FromHexString(made);
        if (exif.AsSpan(0, 4).SequenceEqual("Exif"u8)) exif = exif.AsSpan(6).ToArray();

        var img = MakeImage(31, 17, ColorModel.Rgb, SampleType.UInt8, 3);
        img.Metadata = new ImageMetadata { Exif = exif };
        var file = Encode(img, new TiffEncodeOptions { BigTiff = big, BigEndian = bigEndian });
        AssertRoundTrip(img, file, "exif");

        // tifffile reads the EXIF and GPS directories of our TIFF, fractions intact
        string dump = string.Join("\n",
            "import tifffile",
            "p = tifffile.TiffFile(PATH).pages[0]; x = p.tags[34665].value; g = p.tags[34853].value",
            "print(p.tags[271].value, p.tags[272].value, p.tags[306].value, int(p.tags[274].value), x['ExposureTime'], x['DateTimeOriginal'], x['ISOSpeedRatings'], x['FocalLength'], x['UserComment'], g['GPSLatitudeRef'], g['GPSLatitude'], g['GPSLongitudeRef'], g['GPSLongitude'])");
        var seen = PythonOn(file, dump);
        if (seen != null) Assert.Equal("PixCam Model X 2026:10:13 12:00:00 6 (1, 3) 2026:10:13 11:59:58 400 (35, 1) hello N (48, 1, 12, 1, 3099, 100) E (16, 1, 22, 1, 0, 1)", seen);
        if (!big)
        {
            // so does Pillow (which cannot follow the directories of a BigTIFF)
            var pil = PythonOn(file, "from PIL import Image\ne = Image.open(PATH).getexif(); x = e.get_ifd(0x8769); g = e.get_ifd(0x8825)\n" +
                "print(e[0x010F], e[0x0112], float(x[0x829A]), x[0x8827], g[1], [float(v) for v in g[2]])");
            if (pil != null) Assert.Equal("PixCam 6 0.3333333333333333 400 N [48.0, 12.0, 30.99]", pil);
        }

        // and it comes back as an EXIF blob: orientation in the metadata, the directories intact
        var back = Tiff.Decode(file, Stored);
        Assert.Equal(6, back.Metadata!.Orientation);
        var blob = back.Metadata.Exif!;
        Assert.NotNull(blob);
        var ifd0 = Tiff.ReadTags(blob);
        Assert.Equal("PixCam", ifd0.Single(t => t.Id == 0x010F).AsString());
        Assert.Equal("2026:10:13 12:00:00", ifd0.Single(t => t.Id == 0x0132).AsString());
        Assert.Contains(ifd0, t => t.Id == 34665);
        Assert.Contains(ifd0, t => t.Id == 34853);
        // through a second generation (TIFF -> EXIF blob -> TIFF) Pillow still sees everything
        img.Metadata = new ImageMetadata { Exif = blob };
        Assert.Equal(seen, PythonOn(Encode(img, new TiffEncodeOptions { Compression = TiffCompression.Lzw }), dump));

        // an image without EXIF directories has no EXIF blob
        Assert.Null(Tiff.Decode(Encode(MakeImage(4, 4, ColorModel.Gray, SampleType.UInt8, 1))).Metadata!.Exif);
        // a broken blob is ignored rather than failing the encode
        img.Metadata = new ImageMetadata { Exif = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 } };
        AssertRoundTrip(img, Encode(img), "broken exif");
    }

    // ------------------------------------------------------------------------------------------ large files, partial reads

    /// <summary>
    /// A seekable in-memory stream for files far larger than memory: content is kept in 64 KB blocks, all-zero blocks
    /// are not stored at all and identical blocks are stored once. Also counts what is read.
    /// </summary>
    sealed class SparseStream : Stream
    {
        const int Block = 1 << 16;
        readonly Dictionary<long, byte[]> _blocks = new();
        readonly Dictionary<ulong, byte[]> _pool = new();
        long _length, _pos;
        public long BytesRead, ReadCalls;
        public int StoredBlocks => _pool.Count;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _pos; set => _pos = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => _pos = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => _length + offset };
        public override void SetLength(long value) => _length = value;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> d)
        {
            int n = (int)Math.Min(d.Length, Math.Max(0, _length - _pos));
            for (int done = 0; done < n;)
            {
                long bi = (_pos + done) / Block; int bo = (int)((_pos + done) % Block);
                int len = Math.Min(n - done, Block - bo);
                if (_blocks.TryGetValue(bi, out var b)) b.AsSpan(bo, len).CopyTo(d.Slice(done));
                else d.Slice(done, len).Clear();
                done += len;
            }
            _pos += n; BytesRead += n; ReadCalls++;
            return n;
        }

        static ulong Hash(ReadOnlySpan<byte> s)
        {
            ulong h = 14695981039346656037;
            foreach (ulong v in MemoryMarshal.Cast<byte, ulong>(s)) h = (h ^ v) * 1099511628211 + (h >> 29);
            return h;
        }

        public override void Write(ReadOnlySpan<byte> s)
        {
            for (int done = 0; done < s.Length;)
            {
                long bi = (_pos + done) / Block; int bo = (int)((_pos + done) % Block);
                int len = Math.Min(s.Length - done, Block - bo);
                var part = s.Slice(done, len);
                if (len == Block)
                {
                    // whole block: nothing for zeros, a shared copy for content seen before
                    if (part.IndexOfAnyExcept((byte)0) < 0) _blocks.Remove(bi);
                    else
                    {
                        ulong h = Hash(part);
                        if (!_pool.TryGetValue(h, out var shared) || !part.SequenceEqual(shared)) _pool[h] = shared = part.ToArray();
                        _blocks[bi] = shared;
                    }
                }
                else if (_blocks.TryGetValue(bi, out var b) || part.IndexOfAnyExcept((byte)0) >= 0)
                {
                    var own = b == null ? new byte[Block] : (byte[])b.Clone(); // copy on write: blocks may be shared
                    part.CopyTo(own.AsSpan(bo));
                    _blocks[bi] = own;
                }
                done += len;
            }
            _pos += s.Length;
            _length = Math.Max(_length, _pos);
        }
    }

    [Theory]
    [InlineData(TiffCompression.None, 0)]
    [InlineData(TiffCompression.Lzw, 0)]
    [InlineData(TiffCompression.Deflate, 64)]
    public void OffsetsBeyond4GB(TiffCompression compression, int tile)
    {
        // The image data and directories are pushed behind 4.3 GB of padding (held by a sparse stream): the automatic
        // choice has to fall on BigTIFF, and every 64-bit offset has to survive the round trip.
        const long padding = 4_300_000_000;
        var pages = new[]
        {
            new TiffPage(MakeImage(300, 200, ColorModel.Rgb, SampleType.UInt16, 3)),
            new TiffPage(MakeImage(150, 100, ColorModel.Gray, SampleType.Float32, 1)) { ReducedResolution = true },
        };
        var o = new TiffEncodeOptions { Compression = compression, TileWidth = tile, TileHeight = tile, TestPadding = padding };
        var s = new SparseStream();
        Tiff.Encode(pages, s, o);
        Assert.True(s.Length > padding + 300 * 200 * 6 / 4);
        Assert.True(s.StoredBlocks < 64, "the padding must not be held in memory");

        s.Position = 0;
        var head = new byte[16];
        s.ReadExactly(head);
        Assert.Equal(43, head[2]);                                        // BigTIFF was chosen automatically
        Assert.True(BitConverter.ToInt64(head, 8) > uint.MaxValue);       // first directory beyond 4 GB
        s.Position = 0;
        Assert.Equal(2, Tiff.GetImageCount(s));
        var tags = Tiff.ReadTags(s);
        Assert.True(tags.Single(t => t.Id == (tile > 0 ? 324 : 273)).AsIntegers().All(v => v > uint.MaxValue));
        for (int i = 0; i < pages.Length; i++)
        {
            var back = Tiff.Decode(s, new TiffDecodeOptions { ImageIndex = i });
            Assert.True(pages[i].Image.GetBytes().SequenceEqual(back.GetBytes()), $"page {i}");
            TestUtil.AssertEqual(back, Tiff.Decode(s, ForcedParallel(i)));
        }
        TestUtil.AssertEqual(Crop(pages[0].Image, 17, 33, 200, 99), Tiff.DecodeRegion(s, 17, 33, 200, 99));

        // a classic TIFF cannot hold this
        Assert.Throws<InvalidOperationException>(() => Tiff.Encode(pages, new SparseStream(), new TiffEncodeOptions { Compression = compression, TestPadding = padding, BigTiff = false }));
        // just below the limit the automatic choice stays classic
        var small = new SparseStream();
        Tiff.Encode(pages, small, new TiffEncodeOptions { Compression = compression, TestPadding = 100_000_000 });
        small.Position = 0;
        small.ReadExactly(head);
        Assert.Equal(42, head[2]);
        small.Position = 0;
        Assert.True(pages[1].Image.GetBytes().SequenceEqual(Tiff.Decode(small, new TiffDecodeOptions { ImageIndex = 1 }).GetBytes()));
    }

    /// <summary>
    /// Real multi-gigabyte data (2.2 GB single strip, 6.6 GB file). Needs about 5 GB of RAM and a minute, so it only
    /// runs with PIXCODEC_BIG_TESTS=1.
    /// </summary>
    [Fact]
    public void RealFilesBeyond4GB()
    {
        if (Environment.GetEnvironmentVariable("PIXCODEC_BIG_TESTS") != "1") return;
        // 33000 x 33000 16-bit gray = 2.18 GB of samples, periodic so that the sparse stream stores it once
        int w = 33000, h = 33000;
        var img = RawImage.Create(w, h, ColorModel.Gray, SampleType.UInt16);
        var a = img.GetData<ushort>();
        var row = new ushort[32768];
        for (int i = 0; i < row.Length; i++) row[i] = (ushort)(i * 40503 >> 3);
        for (long i = 0; i < a.LongLength; i += row.Length) row.AsSpan(0, (int)Math.Min(row.Length, a.LongLength - i)).CopyTo(a.AsSpan((int)i));
        // three pages, each one uncompressed strip of more than 2 GB: 6.5 GB, BigTIFF chosen automatically
        var s = new SparseStream();
        Tiff.Encode(new[] { new TiffPage(img), new TiffPage(img), new TiffPage(img) }, s, new TiffEncodeOptions { Compression = TiffCompression.None, RowsPerStrip = h });
        Assert.True(s.Length > 6_500_000_000);
        s.Position = 0;
        var head = new byte[4];
        s.ReadExactly(head);
        Assert.Equal(43, head[2]);
        s.Position = 0;
        Assert.Equal(3, Tiff.GetImageCount(s));
        var last = Tiff.Decode(s, new TiffDecodeOptions { ImageIndex = 2 });          // lies entirely beyond 4 GB
        Assert.True(MemoryMarshal.AsBytes<ushort>(a.AsSpan(0, 1 << 20)).SequenceEqual(MemoryMarshal.AsBytes<ushort>(last.GetData<ushort>().AsSpan(0, 1 << 20))));
        Assert.True(a.AsSpan().SequenceEqual(last.GetData<ushort>()));
        last = null;
        GC.Collect();
        // a region out of the middle of the 2 GB strip reads only its rows
        s.BytesRead = 0;
        var region = Tiff.DecodeRegion(s, 1000, 20000, 500, 300, new TiffDecodeOptions { ImageIndex = 1 });
        Assert.True(region.GetData<ushort>().AsSpan(0, 500).SequenceEqual(a.AsSpan(20000 * w + 1000, 500)));
        Assert.True(s.BytesRead < 300L * w * 2 + 100_000, $"read {s.BytesRead} bytes");

    }

    /// <summary>
    /// Compressed strips that decode to more than 2 GB (LZW, PackBits, Deflate), written row by row and read back
    /// in one piece. About 5 GB of RAM and a few minutes: only with PIXCODEC_BIG_TESTS=1.
    /// </summary>
    [Fact]
    public void CompressedStripsBeyond2GB()
    {
        if (Environment.GetEnvironmentVariable("PIXCODEC_BIG_TESTS") != "1") return;
        int w = 33000, h = 33000;
        var img = RawImage.Create(w, h, ColorModel.Gray, SampleType.UInt16);
        var a = img.GetData<ushort>();
        var rnd = new Random(9);
        var row = new ushort[w];
        // runs of pixels whose two bytes are equal, so that PackBits finds runs as well (its compressed strip has to
        // stay below 2 GB: compressed data beyond that is not supported)
        for (int i = 0; i < row.Length; i++) row[i] = (ushort)(((i / 40 * 7 + rnd.Next(2)) & 0xFF) * 0x0101);
        for (int y = 0; y < h; y++)
        {
            row.AsSpan().CopyTo(a.AsSpan(y * w, w));
            a[y * w + y % w] = (ushort)y;   // every row differs
            a[y * w + (y * 7 + 13) % w] = (ushort)(y * 31);
        }
        foreach (var comp in new[] { TiffCompression.PackBits, TiffCompression.Lzw, TiffCompression.Deflate })
        {
            var s = new SparseStream();
            Tiff.Encode(img, s, new TiffEncodeOptions { Compression = comp, RowsPerStrip = h, BigEndian = comp == TiffCompression.Lzw, CompressionLevel = 1 });
            s.Position = 0;
            var tags = Tiff.ReadTags(s);
            Assert.Single(tags.Single(t => t.Id == 273).AsIntegers());                       // one strip ...
            Assert.Equal(h, tags.Single(t => t.Id == 278).AsIntegers()[0]);                  // ... of 2.18 GB
            var back = Tiff.Decode(s);
            Assert.True(a.AsSpan().SequenceEqual(back.GetData<ushort>()), comp.ToString());
            back = null;
            GC.Collect();
            var region = Tiff.DecodeRegion(s, 5, 31000, 700, 100);
            Assert.True(region.GetData<ushort>().AsSpan(0, 700).SequenceEqual(a.AsSpan(31000 * w + 5, 700)), comp + " region");
            // such strips need a seekable target
            if (comp == TiffCompression.PackBits)
                Assert.Throws<ArgumentException>(() => Tiff.Encode(img, new ForwardOnlyStream(), new TiffEncodeOptions { Compression = comp, RowsPerStrip = h }));
        }
    }

    /// <summary>
    /// Strips whose COMPRESSED data exceeds 2 GB (noise does not compress): written row by row, read back through
    /// the stream entry point, which feeds the decoders piece by piece. About 6 GB of RAM: only with PIXCODEC_BIG_TESTS=1.
    /// </summary>
    [Fact]
    public void CompressedDataBeyond2GB()
    {
        if (Environment.GetEnvironmentVariable("PIXCODEC_BIG_TESTS") != "1") return;
        int w = 33000, h = 33000;
        var img = RawImage.Create(w, h, ColorModel.Gray, SampleType.UInt16);
        var a = img.GetData<ushort>();
        // noise from a small generator state: SparseStream cannot share anything here, so the file is kept on disk
        ulong x = 88172645463325252;
        for (int i = 0; i < a.Length; i++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; a[i] = (ushort)x; }
        var dir = Path.Combine(TestUtil.FixtureDir, "_tmp");
        Directory.CreateDirectory(dir);
        foreach (var comp in new[] { TiffCompression.PackBits, TiffCompression.Lzw, TiffCompression.Deflate })
        {
            var path = Path.Combine(dir, $"big_{comp}.tif");
            try
            {
                using (var fs = File.Create(path))
                    Tiff.Encode(img, fs, new TiffEncodeOptions { Compression = comp, RowsPerStrip = h, Predictor = TiffPredictor.None, CompressionLevel = 1 });
                using var rs = File.OpenRead(path);
                var tags = Tiff.ReadTags(rs);
                Assert.True(tags.Single(t => t.Id == 279).AsIntegers()[0] > int.MaxValue, $"{comp}: the strip is not larger than 2 GB");
                var back = Tiff.Decode(rs);
                Assert.True(a.AsSpan().SequenceEqual(back.GetData<ushort>()), comp.ToString());
            }
            finally { File.Delete(path); }
        }
    }

    [Theory]
    [InlineData(TiffCompression.None, 0, 64)]
    [InlineData(TiffCompression.None, 0, 100000)]
    [InlineData(TiffCompression.None, 256, 0)]
    [InlineData(TiffCompression.Lzw, 0, 64)]
    [InlineData(TiffCompression.Deflate, 0, 64)]
    [InlineData(TiffCompression.PackBits, 256, 0)]
    public void RegionDecodingReadsOnlyWhatItNeeds(TiffCompression compression, int tile, int rowsPerStrip)
    {
        var img = MakeImage(2000, 1500, ColorModel.Rgb, SampleType.UInt8, 3);
        var o = new TiffEncodeOptions { Compression = compression, TileWidth = tile, TileHeight = tile, RowsPerStrip = rowsPerStrip, Predictor = TiffPredictor.None };
        var s = new SparseStream();
        Tiff.Encode(img, s, o);
        long file = s.Length;
        int strips = tile > 0 ? 0 : (1500 + rowsPerStrip - 1) / rowsPerStrip;
        int rx = 700, ry = 600, rw = 50, rh = 40; // touches rows 600..639: strip 9 only (rows 576..639), tiles 2,2 .. 2,2

        s.Position = 0;
        s.BytesRead = 0;
        var region = Tiff.DecodeRegion(s, rx, ry, rw, rh, Serial);
        TestUtil.AssertEqual(Crop(img, rx, ry, rw, rh), region);
        long directory = 4096 + 16L * Math.Max(strips, 48);
        long expected;
        if (compression == TiffCompression.None)
            // uncompressed: just the needed rows of the strip, or of the one tile
            expected = tile > 0 ? rh * tile * 3 : (long)rh * 2000 * 3;
        else if (tile > 0) expected = (long)tile * tile * 3 * 3 / 2;               // one tile
        else expected = (long)rowsPerStrip * 2000 * 3 * 3 / 2;                     // one strip (the noisy test image does not compress)
        Assert.True(s.BytesRead <= expected + directory, $"read {s.BytesRead} of {file} bytes, expected at most {expected + directory}");
        Assert.True(s.BytesRead < file / 4);

        // a region spanning two strips / four tiles, parallel
        s.BytesRead = 0;
        TestUtil.AssertEqual(Crop(img, 250, 630, 20, 20), Tiff.DecodeRegion(s, 250, 630, 20, 20, ForcedParallel()));
        Assert.True(s.BytesRead <= (tile > 0 ? 4L * tile * tile * 3 * 3 / 2 : Math.Min(2L * rowsPerStrip, compression == TiffCompression.None ? 20 : 1500) * 2000 * 3 * 3 / 2) + directory);
    }

    sealed class ForwardOnlyStream : Stream
    {
        readonly MemoryStream _ms = new();
        public byte[] ToArray() => _ms.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _ms.Write(buffer, offset, count);
    }

    // ------------------------------------------------------------------------------------------ robustness

    static void MustFailCleanly(byte[] data, string what)
    {
        void Run(Action a)
        {
            try { a(); }
            catch (ImageFormatException) { }
            catch (ImageNotSupportedException) { }
            catch (Exception e) { throw new Xunit.Sdk.XunitException($"{what}: unexpected {e.GetType().Name}: {e}"); }
        }
        Run(() => Tiff.Decode(data));
        Run(() => Tiff.GetInfo(data));
        Run(() => Tiff.GetInfos(data));
        Run(() => Tiff.ReadTags(data));
    }

    public static IEnumerable<object[]> AllFixtures => Fixtures.Concat(ZstdFixtures).Concat(LzmaFixtures).Concat(WebpFixtures);

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void SurvivesTruncation(string file)
    {
        var bytes = File.ReadAllBytes(TestUtil.Fixture(file));
        var sw = Stopwatch.StartNew();
        var cuts = new SortedSet<int>();
        for (int i = 0; i <= 64 && i < bytes.Length; i++) cuts.Add(i);
        for (int i = 1; i <= 40; i++) cuts.Add((int)((long)bytes.Length * i / 41));
        for (int i = 1; i <= 16 && i < bytes.Length; i++) cuts.Add(bytes.Length - i);
        foreach (int cut in cuts) MustFailCleanly(bytes.AsSpan(0, cut).ToArray(), $"{file} cut at {cut}");
        // and through the stream path
        foreach (int cut in cuts.Where((_, i) => i % 5 == 0))
        {
            try { Tiff.Decode(new MemoryStream(bytes, 0, cut)); }
            catch (ImageFormatException) { }
            catch (ImageNotSupportedException) { }
        }
        Assert.True(sw.Elapsed.TotalSeconds < 20, $"took {sw.Elapsed}");
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void SurvivesCorruption(string file)
    {
        var bytes = File.ReadAllBytes(TestUtil.Fixture(file));
        var rnd = new Random(1234 + bytes.Length);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 300; i++)
        {
            var b = (byte[])bytes.Clone();
            int flips = 1 + i % 3;
            int pos = 0;
            for (int f = 0; f < flips; f++)
            {
                pos = rnd.Next(b.Length);
                b[pos] = i % 4 == 0 ? (byte)rnd.Next(256) : (byte)(b[pos] ^ (1 << rnd.Next(8)));
            }
            MustFailCleanly(b, $"{file} corruption #{i} (last at {pos})");
        }
        Assert.True(sw.Elapsed.TotalSeconds < 30, $"took {sw.Elapsed}");
    }

    [Fact]
    public void RejectsLyingHeaders()
    {
        // a 16x16 file that claims to be gigantic (image size, samples, tile size) must be refused without allocating for it
        var img = MakeImage(16, 16, ColorModel.Gray, SampleType.UInt8, 1);
        var files = new List<(string what, byte[] file)>();
        foreach (var comp in AllCompressions)
        {
            files.Add(($"{comp} strips", Encode(img, new TiffEncodeOptions { Compression = comp })));
            files.Add(($"{comp} tiles", Encode(img, new TiffEncodeOptions { Compression = comp, TileWidth = 16, TileHeight = 16 })));
        }
        files.Add(("g4", Encode(img, new TiffEncodeOptions { Compression = TiffCompression.CcittGroup4 })));
        files.Add(("jpeg", Encode(img, new TiffEncodeOptions { Compression = TiffCompression.Jpeg, TileWidth = 16, TileHeight = 16 })));
        foreach (var (what, file) in files)
        {
            int ifd = BitConverter.ToInt32(file, 4);
            int n = BitConverter.ToUInt16(file, ifd);
            foreach (var (tag, value) in new[] { (256, 60000), (257, 2_000_000_000), (277, 60000), (322, 1_000_000_000), (323, 1_500_000_000), (278, 2_000_000_000) })
            {
                var b = (byte[])file.Clone();
                bool found = false;
                for (int i = 0; i < n; i++)
                {
                    int e = ifd + 2 + i * 12;
                    if (BitConverter.ToUInt16(b, e) != tag) continue;
                    BitConverter.TryWriteBytes(b.AsSpan(e + 2), (ushort)4);
                    BitConverter.TryWriteBytes(b.AsSpan(e + 8), value);
                    found = true;
                }
                if (!found || tag == 278) continue; // (RowsPerStrip beyond the image height is legal)
                long before = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                RawImage? got = null;
                var ex = Record.Exception(() => got = Tiff.Decode(b));
                // (an uncompressed tile claiming more rows than it has is fine as long as the rows the image needs are there)
                if (ex == null) Assert.True(tag == 323 && img.GetBytes().SequenceEqual(got!.GetBytes()), $"{what} tag {tag}: decoded something else");
                else Assert.True(ex is ImageFormatException || ex is ImageNotSupportedException, $"{what} tag {tag}: {ex.GetType().Name}");
                Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64 << 20, $"{what} tag {tag}: allocated too much");
                Assert.True(sw.Elapsed.TotalSeconds < 2, $"{what} tag {tag}: took {sw.Elapsed}");
            }
        }
    }
}
