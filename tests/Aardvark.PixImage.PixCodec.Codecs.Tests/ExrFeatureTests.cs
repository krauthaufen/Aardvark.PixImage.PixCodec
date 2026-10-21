using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Aardvark.ImageCodecs;
using Xunit;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>Luminance/chroma, resolution levels, deep data, tiled and multi-part output, attribute pass-through.</summary>
public class ExrFeatureTests
{
    public static IEnumerable<object[]> Yc => TestUtil.Files(Path.Combine("exr", "yc"), ".exr");
    public static IEnumerable<object[]> Levels => TestUtil.Files(Path.Combine("exr", "levels"), ".exr");
    public static IEnumerable<object[]> Deep => TestUtil.Files(Path.Combine("exr", "deep"), ".exr");

    static byte[] Encode(RawImage img, ExrEncodeOptions opt)
    {
        var ms = new MemoryStream();
        Exr.Encode(img, ms, opt);
        return ms.ToArray();
    }

    static string[] Names(ExrHeader h) => h.Channels.Select(c => c.Name).ToArray();

    static float[] Pattern(int w, int h, int c, int seed, bool positive = false)
    {
        var rnd = new Random(seed);
        var f = new float[w * h * c];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int k = 0; k < c; k++)
                {
                    double v = Math.Sin(x / (5.0 + k) + k) * Math.Cos(y / (3.0 + k)) + ((x / 6 + y / 5 + k) % 2) + 0.05 * rnd.NextDouble();
                    f[(y * w + x) * c + k] = (float)(Half)(float)(positive ? Math.Abs(v) : v);
                }
        return f;
    }

    /// <summary>The optional C++ helper (tests/oracle/exr_oracle.cpp) for checks the python bindings cannot do; set EXR_ORACLE to its path.</summary>
    static string? OracleTool => Environment.GetEnvironmentVariable("EXR_ORACLE") is string p && File.Exists(p) ? p : null;

    static RawImage? RunOracle(byte[] exr, string arguments)
    {
        if (!Oracles.Have("exr_oracle", OracleTool != null, "$EXR_ORACLE: tests/oracle/exr_oracle.cpp built against OpenEXR")) return null;
        var tmp = Path.Combine(TestUtil.FixtureDir, "_tmp");
        Directory.CreateDirectory(tmp);
        var f = Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".exr");
        try
        {
            File.WriteAllBytes(f, exr);
            var psi = new ProcessStartInfo(OracleTool!, arguments.Replace("{in}", $"\"{f}\"").Replace("{out}", $"\"{f}.ref\""))
            { RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, "exr_oracle failed: " + err);
            return TestUtil.LoadRef(f + ".ref");
        }
        finally { File.Delete(f); File.Delete(f + ".ref"); }
    }

    // ------------------------------------------------------------------ luminance / chroma

    static RawImage DropAlpha(RawImage rgba)
    {
        var s = rgba.GetData<Half>();
        var d = new Half[rgba.Width * rgba.Height * 3];
        for (int i = 0; i < rgba.Width * rgba.Height; i++)
        {
            Assert.Equal((Half)1f, s[i * 4 + 3]);     // the RGBA interface fills a missing alpha with 1
            s.AsSpan(i * 4, 3).CopyTo(d.AsSpan(i * 3));
        }
        return new RawImage(rgba.Width, rgba.Height, ColorModel.Rgb, d);
    }

    [Theory]
    [MemberData(nameof(Yc))]
    public void LuminanceChromaDecodesLikeRgbaInterface(string file)
    {
        var path = TestUtil.Fixture(file);
        var data = File.ReadAllBytes(path);
        var header = Exr.ReadHeaders(data)[0];
        var rgba = TestUtil.LoadRef(path + ".ref");               // float16 R,G,B,A from the reference RGBA interface
        var img = Exr.Decode(data, new ExrDecodeOptions { HalfAsFloat = false });
        bool hasAlpha = header.FindChannel("A") != null;
        if (header.FindChannel("RY") == null)
        {
            // luminance only: stays a Gray image; the RGBA interface replicates Y into R, G, B
            Assert.Equal(hasAlpha ? ColorModel.GrayAlpha : ColorModel.Gray, img.Color);
            var y = img.GetData<Half>(); var r = rgba.GetData<Half>();
            for (int i = 0; i < img.Width * img.Height; i++)
            {
                Assert.Equal(r[i * 4], y[i * img.Channels]);
                Assert.Equal(r[i * 4 + 3], hasAlpha ? y[i * 2 + 1] : (Half)1f);
            }
            return;
        }
        Assert.Equal(hasAlpha ? ColorModel.Rgba : ColorModel.Rgb, img.Color);
        TestUtil.AssertEqual(hasAlpha ? rgba : DropAlpha(rgba), img);
        TestUtil.AssertEqual(ExrDecodeTests.HalfToFloat(img), Exr.Decode(data));
        var info = Exr.GetInfo(data);
        Assert.Equal((img.Channels, SampleType.Float32, img.Color), (info.Channels, info.SampleType, info.Color));
        // the stored channels are still reachable
        if (File.Exists(path + ".all.ref"))
            TestUtil.AssertEqual(TestUtil.LoadRef(path + ".all.ref"), Exr.DecodeChannels(data, Names(header), new ExrDecodeOptions { HalfAsFloat = false }));
        Assert.Equal((2, 2), (header.FindChannel("RY")!.XSampling, header.FindChannel("RY")!.YSampling));
    }

    public static IEnumerable<object[]> EncRef =>
        TestUtil.Files(Path.Combine("exr", "encref"), ".exr").Where(f => !((string)f[0]).EndsWith(".yc.exr"));

    [Theory]
    [MemberData(nameof(EncRef))]
    public void LuminanceChromaEncodeMatchesReferenceEncoder(string file)
    {
        // the same RGB(A) image converted by the reference library (name.yc.exr) and by us: Y, RY, BY and A are
        // bit identical for every size, including the reference writer's special handling of images lower than 13 lines
        var path = TestUtil.Fixture(file);
        var src = Exr.Decode(File.ReadAllBytes(path));
        var refData = File.ReadAllBytes(Path.ChangeExtension(path, ".yc.exr"));
        var mine = Encode(src, new ExrEncodeOptions { LuminanceChroma = true });
        var h = Exr.ReadHeaders(mine)[0];
        var rh = Exr.ReadHeaders(refData)[0];
        Assert.Equal(Names(rh), Names(h));
        foreach (var c in rh.Channels)
        {
            var m = h.FindChannel(c.Name)!;
            Assert.Equal((c.PixelType, c.XSampling, c.YSampling, c.PLinear), (m.PixelType, m.XSampling, m.YSampling, m.PLinear));
        }
        var opt = new ExrDecodeOptions { HalfAsFloat = false };
        TestUtil.AssertEqual(Exr.DecodeChannels(refData, Names(rh), opt), Exr.DecodeChannels(mine, Names(h), opt));
        TestUtil.AssertEqual(Exr.Decode(refData, opt), Exr.Decode(mine, opt));
    }

    [Theory]
    [InlineData(ExrCompression.Zip)]
    [InlineData(ExrCompression.Piz)]
    [InlineData(ExrCompression.B44)]
    [InlineData(ExrCompression.Pxr24)]
    [InlineData(ExrCompression.Rle)]
    [InlineData(ExrCompression.Dwaa)]
    public void LuminanceChromaEncodeRoundTrip(ExrCompression comp)
    {
        const int w = 46, h = 38;
        foreach (bool alpha in new[] { false, true })
        {
            int nc = alpha ? 4 : 3;
            // smooth colours: chroma subsampling keeps them, and with full precision so does the rounding
            var f = new float[w * h * nc];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * nc;
                    f[i] = 0.3f + 0.5f * x / w; f[i + 1] = 0.2f + 0.6f * y / h; f[i + 2] = 0.9f - 0.4f * (x + y) / (w + h);
                    if (alpha) f[i + 3] = (float)(Half)(float)((x ^ y) % 7 / 6.0);
                }
            var img = new RawImage(w, h, alpha ? ColorModel.Rgba : ColorModel.Rgb, f);
            var opt = new ExrEncodeOptions { Compression = comp, LuminanceChroma = true, LuminanceRoundBits = 10, ChromaRoundBits = 10, DataWindowX = -4, DataWindowY = 6 };
            opt.Attributes.Add(ExrAttribute.Chromaticities("chromaticities", new ExrChromaticities(0.708f, 0.292f, 0.170f, 0.797f, 0.131f, 0.046f, 0.3127f, 0.329f)));
            var data = Encode(img, opt);
            var back = Exr.Decode(data);
            Assert.Equal(img.Color, back.Color);
            // smooth colours come back up to the ringing of the chroma filter at the image border
            // (B44 additionally quantizes the chroma in its exp(x / 8) domain, as the format prescribes for pLinear channels)
            double tol = comp == ExrCompression.B44 ? 0.15 : comp == ExrCompression.Dwaa ? 0.05 : 0.02;
            Assert.True(TestUtil.MaxDiff(img, back) < tol, $"{comp}: {TestUtil.MaxDiff(img, back)}");
            if (alpha && comp != ExrCompression.B44)
                for (int i = 3; i < f.Length; i += 4) Assert.Equal(f[i], back.GetData<float>()[i]);

            // the reference reads the stored (subsampled) channels exactly as we do ...
            var names = Names(Exr.ReadHeaders(data)[0]);
            var oracle = ExrOracle.Decode(data, ".all.exr");
            if (oracle != null) TestUtil.AssertEqual(oracle, Exr.DecodeChannels(data, names));
            // ... and its RGBA interface converts them to the same RGB (needs the C++ helper)
            var rgba = RunOracle(data, "rgba {in} {out}");
            if (rgba != null) ExrOracle.AssertMatchesLossy(alpha ? rgba : DropAlpha(rgba), Exr.Decode(data, new ExrDecodeOptions { HalfAsFloat = false }));
        }
        // default rounding as the reference library: coarser, still close
        var g = new RawImage(w, h, ColorModel.Rgb, Pattern(w, h, 3, 4, true));
        var coarse = Exr.Decode(Encode(g, new ExrEncodeOptions { Compression = comp, LuminanceChroma = true }));
        var gs = g.GetData<float>(); var cs = coarse.GetData<float>();
        for (int i = 0; i < w * h; i++)
        {
            // luminance survives chroma subsampling: 7 bit rounding is 1/128 relative
            double ya = 0.2126 * gs[i * 3] + 0.7152 * gs[i * 3 + 1] + 0.0722 * gs[i * 3 + 2], yb = 0.2126 * cs[i * 3] + 0.7152 * cs[i * 3 + 1] + 0.0722 * cs[i * 3 + 2];
            if (comp is not (ExrCompression.B44 or ExrCompression.Dwaa)) Assert.True(Math.Abs(ya - yb) <= ya / 100 + 1e-3, $"{ya} vs {yb}");
        }
    }

    [Fact]
    public void LuminanceChromaParallelMatchesSequential()
    {
        var img = new RawImage(700, 420, ColorModel.Rgba, Pattern(700, 420, 4, 9, true));
        var a = Encode(img, new ExrEncodeOptions { LuminanceChroma = true });
        var b = Encode(img, new ExrEncodeOptions { LuminanceChroma = true, Parallel = false });
        Assert.Equal(a, b);
        TestUtil.AssertEqual(Exr.Decode(a, new ExrDecodeOptions { Parallel = false }), Exr.Decode(a));
        var rgba = RunOracle(a, "rgba {in} {out}");
        if (rgba != null) ExrOracle.AssertMatchesLossy(rgba, Exr.Decode(a, new ExrDecodeOptions { HalfAsFloat = false }));
    }

    [Fact]
    public void LuminanceChromaEncodeRejectsUnsuitableImages()
    {
        var o = new ExrEncodeOptions { LuminanceChroma = true };
        var ms = new MemoryStream();
        Assert.Throws<ImageNotSupportedException>(() => Exr.Encode(RawImage.Create(5, 4, ColorModel.Rgb, SampleType.Float32), ms, o));
        Assert.Throws<ImageNotSupportedException>(() => Exr.Encode(RawImage.Create(4, 4, ColorModel.Gray, SampleType.Float32), ms, o));
        Assert.Throws<ImageNotSupportedException>(() => Exr.Encode(RawImage.Create(4, 4, ColorModel.Rgb, SampleType.Float32), ms, new ExrEncodeOptions { LuminanceChroma = true, TileWidth = 2, TileHeight = 2 }));
        // grey pixels have no chroma and negative / non finite colours become black instead of poisoning their neighbourhood
        // (the far corner is out of reach of the 27 tap chroma filter and stays exactly grey)
        var f = new float[64 * 64 * 3];
        Array.Fill(f, 0.25f);
        f[0] = float.NaN; f[4] = -3; f[8] = float.PositiveInfinity;
        var back = Exr.Decode(Encode(new RawImage(64, 64, ColorModel.Rgb, f), new ExrEncodeOptions { LuminanceChroma = true })).GetData<float>();
        Assert.All(back, v => Assert.True(float.IsFinite(v) && v >= 0));
        Assert.Equal(0.25f, back[^1]);
    }

    // ------------------------------------------------------------------ levels

    static IEnumerable<(int lx, int ly)> AllLevels(ExrHeader h)
    {
        for (int ly = 0; ly < h.LevelCountY; ly++)
            for (int lx = 0; lx < h.LevelCountX; lx++)
                if (h.HasLevel(lx, ly)) yield return (lx, ly);
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void EveryLevelDecodesLikeReference(string file)
    {
        var path = TestUtil.Fixture(file);
        var data = File.ReadAllBytes(path);
        var h = Exr.ReadHeaders(data)[0];
        Assert.NotEqual(ExrLevelMode.OneLevel, h.LevelMode);
        int count = 0;
        foreach (var (lx, ly) in AllLevels(h))
        {
            var expected = TestUtil.LoadRef($"{path}.l{lx}_{ly}.ref");
            Assert.Equal((expected.Width, expected.Height), h.GetLevelSize(lx, ly));
            foreach (bool parallel in new[] { true, false })
                TestUtil.AssertEqual(expected, Exr.DecodeChannels(data, Names(h), new ExrDecodeOptions { LevelX = lx, LevelY = ly, Parallel = parallel }));
            count++;
        }
        Assert.Equal(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".l*.ref").Length, count);
        Assert.Throws<ArgumentException>(() => Exr.Decode(data, new ExrDecodeOptions { LevelX = h.LevelCountX, LevelY = h.LevelCountY }));
        if (h.LevelMode == ExrLevelMode.MipMap)
        {
            Assert.False(h.HasLevel(1, 0));
            Assert.Throws<ArgumentException>(() => Exr.Decode(data, new ExrDecodeOptions { LevelX = 1, LevelY = 0 }));
            var l1 = Exr.Decode(data, new ExrDecodeOptions { Level = 1 });
            Assert.Equal(h.GetLevelSize(1, 1), (l1.Width, l1.Height));
        }
    }

    [Fact]
    public void LevelGeometry()
    {
        var h = Exr.ReadHeaders(File.ReadAllBytes(TestUtil.Fixture("exr/levels/mip_down_zip.exr")))[0];       // 37x21, round down
        Assert.Equal((6, 6), (h.LevelCountX, h.LevelCountY));
        Assert.Equal(new[] { (37, 21), (18, 10), (9, 5), (4, 2), (2, 1), (1, 1) }, Enumerable.Range(0, 6).Select(l => h.GetLevelSize(l, l)));
        h = Exr.ReadHeaders(File.ReadAllBytes(TestUtil.Fixture("exr/levels/mip_up_piz.exr")))[0];             // 33x18, round up
        Assert.Equal(7, h.LevelCountX);
        Assert.Equal(new[] { (33, 18), (17, 9), (9, 5), (5, 3), (3, 2), (2, 1), (1, 1) }, Enumerable.Range(0, 7).Select(l => h.GetLevelSize(l, l)));
        h = Exr.ReadHeaders(File.ReadAllBytes(TestUtil.Fixture("exr/levels/rip_up_pxr24.exr")))[0];           // 19x9, round up
        Assert.Equal((6, 5), (h.LevelCountX, h.LevelCountY));
        Assert.Equal((5, 9), h.GetLevelSize(2, 0));
        Assert.Equal((19, 2), h.GetLevelSize(0, 3));
        Assert.Equal((1, 1), h.GetLevelSize(5, 4));
        h = Exr.ReadHeaders(File.ReadAllBytes(TestUtil.Fixture("exr/rgb_half_zip.exr")))[0];
        Assert.Equal((1, 1, true, false), (h.LevelCountX, h.LevelCountY, h.HasLevel(0, 0), h.HasLevel(1, 1)));
    }

    static string Sha(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    static Dictionary<string, string> Hashes(string path)
        => File.ReadAllLines(path).Where(l => l.Length > 0).ToDictionary(l => l.Substring(0, l.LastIndexOf(' ')), l => l.Substring(l.LastIndexOf(' ') + 1));

    [TestDataTheory]
    [InlineData("ColorCodedLevels.exr")]
    [InlineData("MirrorPattern.exr")]
    public void OfficialMipMaps(string name)
    {
        // from the openexr-images collection; the sidecar has SHA-256 of what the reference library reads per level
        var path = TestUtil.Fixture("exr/official/" + name);
        var data = File.ReadAllBytes(path);
        var h = Exr.ReadHeaders(data)[0];
        Assert.Equal((ExrLevelMode.MipMap, 10, 512), (h.LevelMode, h.LevelCountX, h.Width));
        var hashes = Hashes(path + ".sha256");
        for (int l = 0; l < 10; l++)
        {
            var img = Exr.DecodeChannels(data, Names(h), new ExrDecodeOptions { Level = l, HalfAsFloat = false });
            Assert.Equal((512 >> l, 512 >> l), (img.Width, img.Height));
            Assert.Equal(hashes[$"level {l} {l}"], Sha(img.GetBytes()));
        }
        Assert.Equal(ColorModel.Rgba == Exr.Decode(data).Color ? 4 : 3, h.Channels.Count);
    }

    // ------------------------------------------------------------------ deep

    sealed record DeepDump(int Width, int Height, int[] Counts, Dictionary<string, Array> Channels);

    static DeepDump LoadDeep(string path)
    {
        var b = File.ReadAllBytes(path);
        Assert.True(b.AsSpan(0, 4).SequenceEqual("PXDP"u8));
        int w = BitConverter.ToInt32(b, 4), h = BitConverter.ToInt32(b, 8), nc = BitConverter.ToInt32(b, 12), total = BitConverter.ToInt32(b, 16);
        var counts = MemoryMarshal.Cast<byte, int>(b.AsSpan(20, 4 * w * h)).ToArray();
        int p = 20 + 4 * w * h;
        var ch = new Dictionary<string, Array>();
        for (int k = 0; k < nc; k++)
        {
            int e = Array.IndexOf(b, (byte)0, p);
            string name = System.Text.Encoding.ASCII.GetString(b, p, e - p);
            var raw = b.AsSpan(e + 2, total * 4);
            ch[name] = b[e + 1] == 0 ? MemoryMarshal.Cast<byte, uint>(raw).ToArray() : MemoryMarshal.Cast<byte, float>(raw).ToArray();
            p = e + 2 + total * 4;
        }
        return new DeepDump(w, h, counts, ch);
    }

    [Theory]
    [MemberData(nameof(Deep))]
    public void DeepSamplesAndFlattening(string file)
    {
        var path = TestUtil.Fixture(file);
        var data = File.ReadAllBytes(path);
        var headers = Exr.ReadHeaders(data);
        for (int p = 0; p < headers.Length; p++)
        {
            var h = headers[p];
            var opt = new ExrDecodeOptions { PartIndex = p };
            if (!h.IsDeep)
            {
                TestUtil.AssertEqual(TestUtil.LoadRef($"{path}.part{p}.ref"), Exr.Decode(data, new ExrDecodeOptions { PartIndex = p, HalfAsFloat = false }));
                Assert.Throws<ArgumentException>(() => Exr.DecodeDeep(data, null, opt));
                continue;
            }
            Assert.Equal(file.Contains("tile") || h.Name == "deeptile" ? ExrPartType.DeepTiled : ExrPartType.DeepScanLine, h.Type);
            // every level of multi-level deep tiled parts
            if (h.LevelMode != ExrLevelMode.OneLevel)
            {
                int levels = 0;
                foreach (var (lx, ly) in AllLevels(h))
                {
                    if (lx == 0 && ly == 0) continue;
                    var ld = LoadDeep($"{path}.part{p}.l{lx}_{ly}.deep");
                    var lv = Exr.DecodeDeep(data, null, new ExrDecodeOptions { PartIndex = p, LevelX = lx, LevelY = ly });
                    Assert.Equal((ld.Width, ld.Height), h.GetLevelSize(lx, ly));
                    Assert.Equal((ld.Width, ld.Height), (lv.Width, lv.Height));
                    Assert.Equal(ld.Counts, lv.SampleCounts);
                    foreach (var (name, values) in ld.Channels)
                    {
                        if (values is uint[] u) Assert.Equal(u, lv.GetUIntChannel(name));
                        else Assert.True(MemoryMarshal.AsBytes<float>((float[])values).SequenceEqual(MemoryMarshal.AsBytes<float>(lv.GetFloatChannel(name))), name);
                    }
                    var flatLevel = Exr.Decode(data, new ExrDecodeOptions { PartIndex = p, LevelX = lx, LevelY = ly });
                    Assert.Equal((ld.Width, ld.Height), (flatLevel.Width, flatLevel.Height));
                    levels++;
                }
                Assert.Equal(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".part*.l*.deep").Length, levels);
                Assert.True(levels >= 4);
                Assert.Throws<ArgumentException>(() => Exr.DecodeDeep(data, null, new ExrDecodeOptions { PartIndex = p, LevelX = h.LevelCountX, LevelY = 0 }));
            }

            // the raw samples, as the reference library reads them
            var dump = LoadDeep($"{path}.part{p}.deep");
            var deep = Exr.DecodeDeep(data, null, opt);
            Assert.Equal((dump.Width, dump.Height), (deep.Width, deep.Height));
            Assert.Equal(dump.Counts, deep.SampleCounts);
            Assert.Equal(dump.Counts.Sum(), deep.TotalSamples);
            Assert.Equal(Names(h), deep.ChannelNames);
            for (int i = 0; i < dump.Counts.Length; i++) Assert.Equal(deep.SampleOffsets[i] + dump.Counts[i], deep.SampleOffsets[i + 1]);
            foreach (var (name, values) in dump.Channels)
            {
                if (values is uint[] u) Assert.Equal(u, deep.GetUIntChannel(name));
                else Assert.True(MemoryMarshal.AsBytes<float>((float[])values).SequenceEqual(MemoryMarshal.AsBytes<float>(deep.GetFloatChannel(name))), name);
            }
            // a subset of the channels
            var some = Exr.DecodeDeep(data, new[] { "Z", "R" }, opt);
            Assert.Equal(new[] { "Z", "R" }, some.ChannelNames);
            Assert.Equal(deep.GetFloatChannel("R"), some.GetFloatChannel("R"));
            Assert.Throws<ArgumentException>(() => some.GetFloatChannel("G"));
            Assert.Throws<ImageNotSupportedException>(() => Exr.DecodeChannels(data, new[] { "R" }, opt));

            // flattening in file order is what the reference library's compositor does (bit exact) ...
            var names = Names(h);
            TestUtil.AssertEqual(TestUtil.LoadRef($"{path}.part{p}.fileorder.ref"), deep.Flatten(names, sortByDepth: false, tidy: false));
            // ... the default sorts by depth and resolves overlapping volume samples (independent implementation in gen_exr.py)
            var flat = TestUtil.LoadRef($"{path}.part{p}.flat.ref");
            var mine = deep.Flatten(names);
            var fe = flat.GetData<float>(); var fm = mine.GetData<float>();
            Assert.Equal(fe.Length, fm.Length);
            for (int i = 0; i < fe.Length; i++)
                Assert.True(Math.Abs(fe[i] - fm[i]) <= 2e-5 * Math.Max(1, Math.Abs(fe[i])), $"{names[i % names.Length]} at {i / names.Length}: {fe[i]} vs {fm[i]}");
            bool volumes = file.Contains("overlap");    // volumes that get split, coinciding samples that get merged
            if (!volumes) TestUtil.AssertEqual(flat, mine);        // without splitting nothing is left to rounding
            if (file.Contains("sorted") || file.Contains("zback"))
                TestUtil.AssertEqual(TestUtil.LoadRef($"{path}.part{p}.fileorder.ref"), mine);    // stored front to back: sorting changes nothing

            // Exr.Decode gives the flattened colour image
            var img = Exr.Decode(data, opt);
            Assert.Equal(SampleType.Float32, img.SampleType);
            if (names.Contains("G"))
            {
                Assert.Equal(ColorModel.Rgba, img.Color);
                TestUtil.AssertEqual(deep.Flatten(new[] { "R", "G", "B", "A" }), img);
            }
            else TestUtil.AssertEqual(deep.Flatten(names), img);
            var raw = Exr.Decode(data, new ExrDecodeOptions { PartIndex = p, DeepSortByDepth = false, DeepTidy = false });
            TestUtil.AssertEqual(deep.Flatten(names.Contains("G") ? new[] { "R", "G", "B", "A" } : names, false, false), raw);
            if (p == 0) Assert.Equal((img.Width, img.Height, img.Channels, SampleType.Float32), (Exr.GetInfo(data).Width, Exr.GetInfo(data).Height, Exr.GetInfo(data).Channels, Exr.GetInfo(data).SampleType));
        }
    }

    [Fact]
    public void DeepFlatteningRules()
    {
        // one pixel, built by hand: write a 1x1 fixture's samples through the public type is not possible, so the rules
        // are checked on the overlap fixture: with tidy off the sorted result is plain "over" of the sorted samples
        var data = File.ReadAllBytes(TestUtil.Fixture("exr/deep/scan_overlap_zips.exr"));
        var deep = Exr.DecodeDeep(data);
        float[] z = deep.GetFloatChannel("Z"), zb = deep.GetFloatChannel("ZBack"), a = deep.GetFloatChannel("A"), r = deep.GetFloatChannel("R");
        var plain = deep.Flatten(new[] { "R", "A" }, sortByDepth: true, tidy: false).GetData<float>();
        var tidy = deep.Flatten(new[] { "R", "A" }).GetData<float>();
        int overlapping = 0;
        for (int p = 0; p < deep.SampleCounts.Length; p++)
        {
            var order = Enumerable.Range(deep.SampleOffsets[p], deep.SampleCounts[p]).OrderBy(i => z[i]).ThenBy(i => zb[i]).ThenBy(i => i).ToArray();
            float accR = 0, accA = 0;
            foreach (int s in order)
            {
                if (accA >= 1) break;
                float t = 1 - accA;
                accR += t * r[s]; accA += t * a[s];
            }
            Assert.Equal(accR, plain[p * 2]);
            Assert.Equal(accA, plain[p * 2 + 1]);
            bool overlap = false;
            for (int k = 0; k + 1 < order.Length; k++)
                overlap |= (zb[order[k]] > z[order[k]] && z[order[k + 1]] < zb[order[k]]) || (z[order[k]] == z[order[k + 1]] && zb[order[k]] == zb[order[k + 1]]);
            if (!overlap) Assert.Equal(plain[p * 2], tidy[p * 2]);
            else overlapping++;
            // splitting and merging never changes the total opacity (1 - product of (1 - alpha)) unless an opaque sample cuts it short
            if (!order.Any(s => a[s] >= 1))
            {
                double prod = 1;
                foreach (int s in order) prod *= 1 - a[s];
                Assert.InRange(tidy[p * 2 + 1] - (1 - prod), -1e-4, 1e-4);
            }
        }
        Assert.True(overlapping > 10, "fixture should have overlapping volume samples");
    }

    [TestDataFact]
    public void OfficialDeepImage()
    {
        // Trunks.exr of the openexr-images collection: deep scan line, ZIPS, 1024x435
        var path = TestUtil.Fixture("exr/official/Trunks.exr");
        var data = File.ReadAllBytes(path);
        var hashes = Hashes(path + ".sha256");
        var h = Exr.ReadHeaders(data)[0];
        Assert.Equal((ExrPartType.DeepScanLine, 1024, 435), (h.Type, h.Width, h.Height));
        var deep = Exr.DecodeDeep(data);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(MemoryMarshal.AsBytes<int>(deep.SampleCounts));
        foreach (var arr in deep.ChannelData) sha.AppendData(arr is uint[] u ? MemoryMarshal.AsBytes<uint>(u) : MemoryMarshal.AsBytes<float>((float[])arr));
        Assert.Equal(hashes["deep"], Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant());
        // the reference library's flattened R,G,B,A (file order compositing)
        Assert.Equal(hashes["flat"], Sha(deep.Flatten(new[] { "R", "G", "B", "A" }, sortByDepth: false, tidy: false).GetBytes()));
        // the samples are stored front to back, so sorting alone changes nothing ...
        Assert.Equal(hashes["flat"], Sha(deep.Flatten(new[] { "R", "G", "B", "A" }, sortByDepth: true, tidy: false).GetBytes()));
        Assert.Equal(hashes["flat"], Sha(Exr.Decode(data, new ExrDecodeOptions { DeepTidy = false }).GetBytes()));
        // ... but some pixels have several samples at the same depth, which the default merges
        var img = Exr.Decode(data);
        Assert.Equal((ColorModel.Rgba, SampleType.Float32), (img.Color, img.SampleType));
        Assert.True(TestUtil.MaxDiff(deep.Flatten(new[] { "R", "G", "B", "A" }, true, false), img) < 0.2);
    }

    // ------------------------------------------------------------------ tiled and multi-part output

    public static IEnumerable<object[]> TiledCases()
    {
        foreach (var comp in new[] { ExrCompression.None, ExrCompression.Zip, ExrCompression.Piz, ExrCompression.Pxr24, ExrCompression.B44A, ExrCompression.Rle, ExrCompression.Dwaa })
            foreach (var mode in new[] { ExrLevelMode.OneLevel, ExrLevelMode.MipMap, ExrLevelMode.RipMap })
                yield return new object[] { comp, mode };
    }

    static float[] BoxReduce(float[] src, int w, int h, int c, bool inX, bool inY, out int nw, out int nh, bool roundUp = false)
    {
        int Half(int n) => Math.Max(1, roundUp ? (n + 1) / 2 : n / 2);
        nw = inX ? Half(w) : w; nh = inY ? Half(h) : h;
        int sx = inX && w > 1 ? 2 : 1, sy = inY && h > 1 ? 2 : 1;
        var r = new float[nw * nh * c];
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
                for (int k = 0; k < c; k++)
                {
                    float sum = 0;
                    int n = 0;
                    for (int dy = 0; dy < sy && y * sy + dy < h; dy++)
                        for (int dx = 0; dx < sx && x * sx + dx < w; dx++, n++) sum += src[((y * sy + dy) * w + x * sx + dx) * c + k];
                    r[(y * nw + x) * c + k] = sum * (1f / n);
                }
        return r;
    }

    [Theory]
    [MemberData(nameof(TiledCases))]
    public void TiledOutput(ExrCompression comp, ExrLevelMode mode)
    {
        const int w = 45, h = 27;
        bool lossy = comp is ExrCompression.B44A or ExrCompression.Dwaa;
        foreach (var order in new[] { ExrLineOrder.IncreasingY, ExrLineOrder.DecreasingY, ExrLineOrder.RandomY, (ExrLineOrder)99 })
        {
            // the fourth round is increasing order again, with level sizes rounded up
            bool roundUp = (int)order == 99;
            var lineOrder = roundUp ? ExrLineOrder.IncreasingY : order;
            var f = Pattern(w, h, 4, 6, true);
            var img = new RawImage(w, h, ColorModel.Rgba, f);
            var data = Encode(img, new ExrEncodeOptions
            {
                Compression = comp, TileWidth = 16, TileHeight = 8, LevelMode = mode, LineOrder = lineOrder, PixelType = ExrPixelType.Float, LevelRoundUp = roundUp,
                DataWindowX = 3, DataWindowY = -5, DisplayWindow = new ExrBox2i(0, 0, 63, 47),
            });
            var hd = Exr.ReadHeaders(data)[0];
            Assert.Equal((ExrPartType.Tiled, 16, 8, mode, lineOrder, roundUp), (hd.Type, hd.TileWidth, hd.TileHeight, hd.LevelMode, hd.LineOrder, hd.LevelRoundUp));
            Assert.Equal(new ExrBox2i(3, -5, 47, 21), hd.DataWindow);
            var back = Exr.Decode(data);
            if (!lossy && comp != ExrCompression.Pxr24) TestUtil.AssertEqual(img, back);
            else Assert.True(TestUtil.MaxDiff(img, back) < 0.25);

            // level 0 through the reference library
            var oracle = ExrOracle.Decode(data, ".exr");
            if (oracle != null && lossy) ExrOracle.AssertMatchesLossy(oracle, back);
            else if (oracle != null) TestUtil.AssertEqual(oracle, back);

            // generated levels: box filtered, sizes rounded down
            var cur = f; int cw = w, ch = h;
            foreach (var (lx, ly) in AllLevels(hd))
            {
                if (lineOrder != ExrLineOrder.IncreasingY && lx + ly > 2) continue;
                float[] expected; int ew, eh;
                if (mode == ExrLevelMode.MipMap)
                {
                    if (lx > 0) cur = BoxReduce(cur, cw, ch, 4, true, true, out cw, out ch, roundUp);
                    expected = cur; ew = cw; eh = ch;
                }
                else
                {
                    expected = f; ew = w; eh = h;
                    for (int i = 0; i < ly; i++) expected = BoxReduce(expected, ew, eh, 4, false, true, out ew, out eh, roundUp);
                    for (int i = 0; i < lx; i++) expected = BoxReduce(expected, ew, eh, 4, true, false, out ew, out eh, roundUp);
                }
                var o = new ExrDecodeOptions { LevelX = lx, LevelY = ly };
                var level = Exr.Decode(data, o);
                Assert.Equal((ew, eh), (level.Width, level.Height));
                Assert.Equal((ew, eh), hd.GetLevelSize(lx, ly));
                var e = new RawImage(ew, eh, ColorModel.Rgba, expected);
                if (!lossy && comp != ExrCompression.Pxr24) TestUtil.AssertEqual(e, level);
                else Assert.True(TestUtil.MaxDiff(e, level) < 0.25);
                // every level through the reference library (needs the C++ helper)
                var refLevel = RunOracle(data, $"level {{in}} 0 {lx} {ly} {{out}} R G B A");
                if (refLevel != null && lossy) ExrOracle.AssertMatchesLossy(refLevel, level);
                else if (refLevel != null) TestUtil.AssertEqual(refLevel, level);
            }
        }
    }

    [Fact]
    public void TiledOutputOfOtherTypesAndSizes()
    {
        // UINT channels take the top left sample in lower levels; tile bigger than the image; 1 pixel images
        var ids = new uint[13 * 7];
        for (int i = 0; i < ids.Length; i++) ids[i] = (uint)(i * 2654435761u);
        var img = new RawImage(13, 7, ColorModel.Gray, ids);
        var data = Encode(img, new ExrEncodeOptions { TileWidth = 64, TileHeight = 64, LevelMode = ExrLevelMode.MipMap, ChannelNames = new[] { "id" } });
        var h = Exr.ReadHeaders(data)[0];
        Assert.Equal(4, h.LevelCountX);
        TestUtil.AssertEqual(img, Exr.Decode(data));
        var l1 = Exr.Decode(data, new ExrDecodeOptions { Level = 1 });
        Assert.Equal((6, 3), (l1.Width, l1.Height));
        for (int y = 0; y < 3; y++) for (int x = 0; x < 6; x++) Assert.Equal(ids[2 * y * 13 + 2 * x], l1.GetData<uint>()[y * 6 + x]);
        var l3 = Exr.Decode(data, new ExrDecodeOptions { Level = 3 });
        Assert.Equal((1, 1, ids[0]), (l3.Width, l3.Height, l3.GetData<uint>()[0]));
        var oracle = ExrOracle.Decode(data, ".all.exr");
        if (oracle != null) TestUtil.AssertEqual(oracle, Exr.Decode(data));

        var one = new RawImage(1, 1, ColorModel.Rgb, new Half[] { (Half)1f, (Half)2f, (Half)3f });
        foreach (var mode in new[] { ExrLevelMode.MipMap, ExrLevelMode.RipMap })
        {
            var d1 = Encode(one, new ExrEncodeOptions { TileWidth = 4, TileHeight = 4, LevelMode = mode });
            Assert.Equal(1, Exr.ReadHeaders(d1)[0].LevelCountX);
            TestUtil.AssertEqual(one, Exr.Decode(d1, new ExrDecodeOptions { HalfAsFloat = false }));
        }
        var ms = new MemoryStream();
        Assert.Throws<ArgumentException>(() => Exr.Encode(one, ms, new ExrEncodeOptions { TileWidth = 4 }));
        Assert.Throws<ArgumentException>(() => Exr.Encode(one, ms, new ExrEncodeOptions { LevelMode = ExrLevelMode.MipMap }));
    }

    [Fact]
    public void MultiPartOutput()
    {
        var display = new ExrBox2i(0, 0, 99, 79);
        var beauty = new RawImage(60, 44, ColorModel.Rgba, Pattern(60, 44, 4, 1));
        var depth = Pattern(31, 50, 1, 2);
        var ids = new uint[20 * 20];
        for (int i = 0; i < ids.Length; i++) ids[i] = (uint)(i * 40503u);
        var normals = Array.ConvertAll(Pattern(25, 19, 3, 3), v => (Half)v);

        var beautyOpt = new ExrEncodeOptions { Compression = ExrCompression.Piz, DisplayWindow = display, DataWindowX = 5, DataWindowY = 7, LineOrder = ExrLineOrder.DecreasingY };
        beautyOpt.Attributes.Add(ExrAttribute.String("comment", "beauty pass"));
        var parts = new[]
        {
            ExrPart.FromImage("beauty", beauty, beautyOpt),
            new ExrPart("depth", 31, 50, new[] { new ExrChannelData("Z", depth, ExrPixelType.Float) },
                new ExrEncodeOptions { Compression = ExrCompression.Zip, DisplayWindow = display, TileWidth = 16, TileHeight = 16, LevelMode = ExrLevelMode.MipMap }),
            new ExrPart("ids", 20, 20, new[] { new ExrChannelData("id", ids) }, new ExrEncodeOptions { Compression = ExrCompression.Rle, DisplayWindow = display }),
            new ExrPart("normals", 25, 19, new[] { new ExrChannelData("N.X", normals, null, 0, 3), new ExrChannelData("N.Y", normals, null, 1, 3), new ExrChannelData("N.Z", normals, null, 2, 3) },
                new ExrEncodeOptions { Compression = ExrCompression.B44, DisplayWindow = display, TileWidth = 8, TileHeight = 32, LineOrder = ExrLineOrder.RandomY }),
        };
        var ms = new MemoryStream();
        Exr.EncodeParts(parts, ms);
        var data = ms.ToArray();

        var h = Exr.ReadHeaders(data);
        Assert.Equal(new[] { "beauty", "depth", "ids", "normals" }, h.Select(x => x.Name));
        Assert.Equal(new[] { ExrPartType.ScanLine, ExrPartType.Tiled, ExrPartType.ScanLine, ExrPartType.Tiled }, h.Select(x => x.Type));
        Assert.Equal(new[] { ExrCompression.Piz, ExrCompression.Zip, ExrCompression.Rle, ExrCompression.B44 }, h.Select(x => x.Compression));
        Assert.All(h, x => Assert.Equal(display, x.DisplayWindow));
        Assert.Equal((new ExrBox2i(5, 7, 64, 50), ExrLineOrder.DecreasingY, "beauty pass"), (h[0].DataWindow, h[0].LineOrder, h[0].FindAttribute("comment")!.AsString()));
        Assert.Equal((ExrLevelMode.MipMap, 6), (h[1].LevelMode, h[1].LevelCountX));

        TestUtil.AssertEqual(beauty, Exr.Decode(data));
        Assert.Equal(depth, Exr.Decode(data, new ExrDecodeOptions { PartIndex = 1 }).GetData<float>());
        Assert.Equal((15, 25), (Exr.Decode(data, new ExrDecodeOptions { PartIndex = 1, Level = 1 }).Width, Exr.Decode(data, new ExrDecodeOptions { PartIndex = 1, Level = 1 }).Height));
        Assert.Equal(ids, Exr.Decode(data, new ExrDecodeOptions { PartIndex = 2 }).GetData<uint>());
        var n = Exr.Decode(data, new ExrDecodeOptions { PartIndex = 3, HalfAsFloat = false });
        Assert.Equal((ColorModel.Other, 3, SampleType.Float16), (n.Color, n.Channels, n.SampleType));

        // every part through the reference library
        for (int p = 0; p < 4; p++)
        {
            var oracle = ExrOracle.Decode(data, $".part{p}.all.exr");
            if (oracle == null) break;
            TestUtil.AssertEqual(oracle, Exr.DecodeChannels(data, Names(h[p]), new ExrDecodeOptions { PartIndex = p }));
        }
        var lvl = RunOracle(data, "level {in} 1 2 2 {out} Z");
        if (lvl != null) TestUtil.AssertEqual(lvl, Exr.Decode(data, new ExrDecodeOptions { PartIndex = 1, Level = 2 }));

        // a damaged offset table is rebuilt for multi-part files too
        var damaged = ZeroOffsetTables(data);
        TestUtil.AssertEqual(beauty, Exr.Decode(damaged));
        Assert.Equal(ids, Exr.Decode(damaged, new ExrDecodeOptions { PartIndex = 2 }).GetData<uint>());
        TestUtil.AssertEqual(Exr.Decode(data, new ExrDecodeOptions { PartIndex = 1, Level = 2 }), Exr.Decode(damaged, new ExrDecodeOptions { PartIndex = 1, Level = 2 }));

        // what the format does not allow
        var bad = new MemoryStream();
        Assert.Throws<ArgumentException>(() => Exr.EncodeParts(new[] { parts[0], parts[0] }, bad));
        Assert.Throws<ArgumentException>(() => Exr.EncodeParts(new[] { parts[0], new ExrPart(null, 20, 20, new[] { new ExrChannelData("id", ids) }) }, bad));
        Assert.Throws<ArgumentException>(() => Exr.EncodeParts(new[] { parts[0], new ExrPart("other", 20, 20, new[] { new ExrChannelData("id", ids) }) }, bad));   // display window differs
        Assert.Equal(0, bad.Length);

        // a single part written through EncodeParts is still a multi-part file with a name
        var single = new MemoryStream();
        Exr.EncodeParts(new[] { parts[2] }, single);
        Assert.Equal("ids", Exr.ReadHeaders(single.ToArray())[0].Name);
        Assert.Equal(ids, Exr.Decode(single.ToArray()).GetData<uint>());
    }

    /// <summary>Clears all offset tables (they lie between the headers and the first chunk).</summary>
    static byte[] ZeroOffsetTables(byte[] file)
    {
        bool multi = (file[5] & 0x10) != 0;
        int p = 8;
        while (true)
        {
            while (file[p] != 0)
            {
                p = Array.IndexOf(file, (byte)0, p) + 1;
                p = Array.IndexOf(file, (byte)0, p) + 1;
                p += 4 + BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(p));
            }
            p++;
            if (!multi) break;
            if (file[p] == 0) { p++; break; }
        }
        long first = long.MaxValue;
        for (int q = p; q < first; q += 8)
        {
            long o = BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(q));
            if (o > 0) first = Math.Min(first, o);
        }
        var d = (byte[])file.Clone();
        Array.Clear(d, p, (int)first - p);
        return d;
    }

    [Theory]
    [InlineData("deep/multi_zback_zips.exr")]
    [InlineData("deep/tile_plain_zips.exr")]
    [InlineData("deep/scan_overlap_zips.exr")]
    [InlineData("levels/rip_down_zip.exr")]
    [InlineData("multipart.exr")]
    public void MissingOffsetTablesAreRebuilt(string name)
    {
        var data = File.ReadAllBytes(TestUtil.Fixture("exr/" + name));
        var damaged = ZeroOffsetTables(data);
        var headers = Exr.ReadHeaders(data);
        for (int p = 0; p < headers.Length; p++)
        {
            var o = new ExrDecodeOptions { PartIndex = p };
            TestUtil.AssertEqual(Exr.Decode(data, o), Exr.Decode(damaged, o));
            if (headers[p].IsDeep) Assert.Equal(Exr.DecodeDeep(data, null, o).SampleCounts, Exr.DecodeDeep(damaged, null, o).SampleCounts);
            if (headers[p].LevelMode == ExrLevelMode.RipMap)
            {
                o = new ExrDecodeOptions { PartIndex = p, LevelX = 2, LevelY = 1 };
                TestUtil.AssertEqual(Exr.Decode(data, o), Exr.Decode(damaged, o));
            }
        }
    }

    [Fact]
    public void SubsampledChannelsThroughEncodeChannels()
    {
        const int w = 24, h = 36;
        var full = Array.ConvertAll(Pattern(w, h, 1, 1), v => (Half)v);
        var a = Pattern(w / 3, h, 1, 2);
        var b = Array.ConvertAll(Pattern(w / 4, h / 3, 1, 3), v => (Half)v);
        var u = new uint[w / 2 * (h / 2)];
        for (int i = 0; i < u.Length; i++) u[i] = (uint)(i * 2246822519u);
        var chans = new[]
        {
            new ExrChannelData("full", full),
            new ExrChannelData("a", a, ExrPixelType.Float) { XSampling = 3 },
            new ExrChannelData("b", b) { XSampling = 4, YSampling = 3 },
            new ExrChannelData("u", u) { XSampling = 2, YSampling = 2 },
        };
        foreach (var comp in new[] { ExrCompression.None, ExrCompression.Rle, ExrCompression.Zips, ExrCompression.Zip, ExrCompression.Piz, ExrCompression.Pxr24, ExrCompression.B44, ExrCompression.Dwaa })
        {
            var ms = new MemoryStream();
            Exr.EncodeChannels(w, h, chans, ms, new ExrEncodeOptions { Compression = comp, DataWindowX = -12, DataWindowY = 36 });
            var data = ms.ToArray();
            var hd = Exr.ReadHeaders(data)[0];
            Assert.Equal((4, 3), (hd.FindChannel("b")!.XSampling, hd.FindChannel("b")!.YSampling));
            var back = Exr.DecodeChannels(data, new[] { "a", "u" });
            if (comp != ExrCompression.Pxr24)
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) Assert.Equal(a[y * (w / 3) + x / 3], back.GetData<float>()[(y * w + x) * 2]);
            var uu = Exr.DecodeChannels(data, new[] { "u" }).GetData<uint>();
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) Assert.Equal(u[y / 2 * (w / 2) + x / 2], uu[y * w + x]);
            var oracle = ExrOracle.Decode(data, ".all.exr");
            if (oracle != null) TestUtil.AssertEqual(oracle, Exr.DecodeChannels(data, Names(hd)));
        }
        var bad = new MemoryStream();
        Assert.Throws<ArgumentException>(() => Exr.EncodeChannels(w, h, chans, bad, new ExrEncodeOptions { DataWindowX = 1 }));
        Assert.Throws<ArgumentException>(() => Exr.EncodeChannels(w, h, chans, bad, new ExrEncodeOptions { TileWidth = 8, TileHeight = 8 }));
        Assert.Throws<ImageNotSupportedException>(() => Exr.EncodeChannels(4, 4, new[] { new ExrChannelData("i", new int[16]) }, bad));
    }

    // ------------------------------------------------------------------ image info

    public static IEnumerable<object[]> AllExr =>
        new[] { "", "dwa", "yc", "levels", "deep", "encref", "selfcheck" }.SelectMany(d => TestUtil.Files(Path.Combine("exr", d), ".exr"));

    public static IEnumerable<object[]> OfficialExr => TestUtil.ThirdPartyFiles(Path.Combine("exr", "official"), ".exr");

    [Theory]
    [MemberData(nameof(AllExr))]
    public void InfoMatchesDecode(string file) => InfoMatchesDecodeOf(file);

    [TestDataTheory]
    [MemberData(nameof(OfficialExr))]
    public void OfficialInfoMatchesDecode(string file) => InfoMatchesDecodeOf(file);

    static void InfoMatchesDecodeOf(string file)
    {
        var data = File.ReadAllBytes(TestUtil.Fixture(file));
        var info = Exr.GetInfo(data);
        var headers = Exr.ReadHeaders(data);
        var img = Exr.Decode(data);
        Assert.Equal((ImageFormat.Exr, img.Width, img.Height, img.Channels, img.SampleType, img.Color), (info.Format, info.Width, info.Height, info.Channels, info.SampleType, info.Color));
        Assert.Equal(headers.Length, info.FrameCount);
        Assert.Equal((0, false, false), (info.Orientation, info.HasIccProfile, info.IsAnimated));
        Assert.Equal((img.Metadata?.DpiX ?? 0, img.Metadata?.DpiY ?? 0), (info.DpiX, info.DpiY));
        // bits as stored: 16 if every channel the default decode uses is HALF, 32 otherwise
        Assert.True(info.BitsPerSample is 16 or 32);
        if (headers[0].Channels.All(c => c.PixelType == ExrPixelType.Half)) Assert.Equal(16, info.BitsPerSample);
        if (headers[0].Channels.All(c => c.PixelType != ExrPixelType.Half)) Assert.Equal(32, info.BitsPerSample);

        // any prefix that ends inside the headers is reported as truncated, any longer one gives the same info
        for (int cut = 0; cut < Math.Min(data.Length, 4000); cut += cut < 64 ? 1 : 37)
        {
            ImageInfo? partial = null;
            try { partial = Exr.GetInfo(data.AsSpan(0, cut)); }
            catch (ImageFormatException) { }
            // once the headers are complete the answer never changes
            if (partial != null) Assert.Equal(info, partial.Value);
        }
        Assert.Equal(info, ImageCodec.GetInfo(data));
    }

    [Fact]
    public void ResolutionAndBitsInInfo()
    {
        ImageInfo Info(RawImage img, ExrEncodeOptions? o = null) => Exr.GetInfo(Encode(img, o ?? new ExrEncodeOptions()));
        Assert.Equal(16, Info(RawImage.Create(4, 4, ColorModel.Rgb, SampleType.Float32)).BitsPerSample);
        Assert.Equal(16, Info(RawImage.Create(4, 4, ColorModel.Rgb, SampleType.Float16)).BitsPerSample);
        Assert.Equal(32, Info(RawImage.Create(4, 4, ColorModel.Rgb, SampleType.Float32), new ExrEncodeOptions { PixelType = ExrPixelType.Float }).BitsPerSample);
        Assert.Equal(32, Info(RawImage.Create(4, 4, ColorModel.Gray, SampleType.UInt32)).BitsPerSample);
        Assert.Equal(16, Info(RawImage.Create(4, 4, ColorModel.Rgb, SampleType.Float32), new ExrEncodeOptions { LuminanceChroma = true }).BitsPerSample);
        // mixed.exr style: default decode uses R,G,B,A (HALF) although the file also has FLOAT and UINT channels
        Assert.Equal(16, Exr.GetInfo(File.ReadAllBytes(TestUtil.Fixture("exr/mixed_zip.exr"))).BitsPerSample);
        Assert.Equal(32, Exr.GetInfo(File.ReadAllBytes(TestUtil.Fixture("exr/aov_only_zip.exr"))).BitsPerSample);
        Assert.Equal(3, Exr.GetInfo(File.ReadAllBytes(TestUtil.Fixture("exr/multipart.exr"))).FrameCount);

        // resolution: image metadata -> xDensity (+ pixelAspectRatio) -> info and metadata of the decoded image
        var img = RawImage.Create(6, 4, ColorModel.Rgb, SampleType.Float32);
        img.Metadata = new ImageMetadata { DpiX = 300, DpiY = 150 };
        var data = Encode(img, new ExrEncodeOptions());
        var h = Exr.ReadHeaders(data)[0];
        Assert.Equal((300f, 0.5f), (h.FindAttribute("xDensity")!.AsFloat(), h.PixelAspectRatio));
        var info = Exr.GetInfo(data);
        Assert.Equal((300.0, 150.0), (info.DpiX, info.DpiY));
        var back = Exr.Decode(data);
        Assert.Equal((300.0, 150.0), (back.Metadata!.DpiX, back.Metadata.DpiY));
        Assert.Equal((300.0, 150.0), (Exr.DecodeChannels(data, new[] { "G" }).Metadata!.DpiX, Exr.DecodeChannels(data, new[] { "G" }).Metadata!.DpiY));
        // an explicit attribute wins over the metadata; no density, no metadata
        var o = new ExrEncodeOptions();
        o.Attributes.Add(ExrAttribute.Float("xDensity", 72f));
        Assert.Equal((72.0, 72.0), (Exr.GetInfo(Encode(img, o)).DpiX, Exr.GetInfo(Encode(img, o)).DpiY));
        Assert.Equal(1, o.Attributes.Count);
        Assert.Null(Exr.Decode(Encode(RawImage.Create(6, 4, ColorModel.Rgb, SampleType.Float32), new ExrEncodeOptions())).Metadata);
        Assert.Equal(0.0, Exr.GetInfo(File.ReadAllBytes(TestUtil.Fixture("exr/rgb_half_zip.exr"))).DpiX);
    }

    // ------------------------------------------------------------------ attribute pass-through

    [Fact]
    public void DescriptiveAttributesSurviveDecodeEncode()
    {
        // original file with pixel aspect, chromaticities, windows and assorted custom attributes
        var img = new RawImage(12, 9, ColorModel.Rgb, Pattern(12, 9, 3, 5));
        var opt = new ExrEncodeOptions { Compression = ExrCompression.Piz, DataWindowX = 2, DataWindowY = 1, DisplayWindow = new ExrBox2i(-1, -1, 30, 20), LineOrder = ExrLineOrder.DecreasingY };
        var chroma = new ExrChromaticities(0.708f, 0.292f, 0.170f, 0.797f, 0.131f, 0.046f, 0.3127f, 0.329f);
        opt.Attributes.Add(ExrAttribute.Float("pixelAspectRatio", 2f));
        opt.Attributes.Add(ExrAttribute.Chromaticities("chromaticities", chroma));
        opt.Attributes.Add(ExrAttribute.V2f("screenWindowCenter", 0.25f, -0.5f));
        opt.Attributes.Add(ExrAttribute.Float("screenWindowWidth", 1.5f));
        opt.Attributes.Add(ExrAttribute.String("owner", "someone"));
        opt.Attributes.Add(ExrAttribute.Float("whiteLuminance", 100f));
        opt.Attributes.Add(new ExrAttribute("renderStats", "myStats", new byte[] { 9, 8, 7 }));
        var original = Encode(img, opt);

        // decode, then encode again differently, carrying the header along
        var header = Exr.ReadHeaders(original)[0];
        var pixels = Exr.Decode(original);
        var again = new ExrEncodeOptions { Compression = ExrCompression.Zip, TileWidth = 8, TileHeight = 8, DataWindowX = header.DataWindow.MinX, DataWindowY = header.DataWindow.MinY };
        foreach (var a in header.GetDescriptiveAttributes()) again.Attributes.Add(a);
        var copy = Encode(pixels, again);

        var h2 = Exr.ReadHeaders(copy)[0];
        Assert.Equal((2f, chroma, (0.25f, -0.5f), 1.5f), (h2.PixelAspectRatio, h2.Chromaticities!.Value, h2.ScreenWindowCenter, h2.ScreenWindowWidth));
        Assert.Equal((header.DisplayWindow, header.DataWindow), (h2.DisplayWindow, h2.DataWindow));
        Assert.Equal("someone", h2.FindAttribute("owner")!.AsString());
        Assert.Equal(100f, h2.FindAttribute("whiteLuminance")!.AsFloat());
        Assert.Equal(("myStats", new byte[] { 9, 8, 7 }.Length), (h2.FindAttribute("renderStats")!.TypeName, h2.FindAttribute("renderStats")!.Value.Length));
        // the storage layout is the new one, not the old one
        Assert.Equal((ExrCompression.Zip, ExrPartType.Tiled, ExrLineOrder.IncreasingY), (h2.Compression, h2.Type, h2.LineOrder));
        var names = header.GetDescriptiveAttributes().Select(a => a.Name).ToArray();
        Assert.DoesNotContain("compression", names);
        Assert.DoesNotContain("channels", names);
        Assert.DoesNotContain("lineOrder", names);
        Assert.Contains("displayWindow", names);
        TestUtil.AssertEqual(pixels, Exr.Decode(copy));
        var oracle = ExrOracle.Decode(copy, ".exr");
        if (oracle != null) TestUtil.AssertEqual(oracle, pixels);

        // attributes of a reference written file (including ones we do not interpret) go through as well
        var fixture = File.ReadAllBytes(TestUtil.Fixture("exr/mixed_zip.exr"));
        var fh = Exr.ReadHeaders(fixture)[0];
        var o3 = new ExrEncodeOptions();
        foreach (var a in fh.GetDescriptiveAttributes()) o3.Attributes.Add(a);
        var h3 = Exr.ReadHeaders(Encode(Exr.Decode(fixture), o3))[0];
        Assert.Equal("mixed types", h3.FindAttribute("comment")!.AsString());
    }
}
