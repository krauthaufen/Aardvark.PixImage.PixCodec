using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using Aardvark.ImageCodecs.Processing;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// ICC profiles and colour conversion, checked against LittleCMS (tests/oracle/gen_icc.py): profiles made by LittleCMS,
/// Aardvark.PixImage.PixCodec's own profiles read back by LittleCMS, third-party profiles of the test-data repository, and images with
/// embedded profiles.
/// </summary>
public class IccTests
{
    readonly ITestOutputHelper log;
    public IccTests(ITestOutputHelper log) => this.log = log;

    static string IccDir => Path.Combine(FixtureDir, "icc");

    /// <summary>Aardvark.PixImage.PixCodec's own profiles as they are written to tests/fixtures/icc/ours (and read by the oracle).</summary>
    public static readonly (string Name, Func<IccProfile> Make)[] OwnProfiles =
    {
        ("srgb", () => IccProfile.Srgb),
        ("srgb_v2", () => IccProfile.CreateRgb("sRGB v2", CieXy.D65, new CieXy(0.64, 0.33), new CieXy(0.30, 0.60), new CieXy(0.15, 0.06), IccCurve.Srgb, 2)),
        ("linear_srgb", () => IccProfile.LinearSrgb),
        ("p3", () => IccProfile.DisplayP3),
        ("rec2020", () => IccProfile.Rec2020),
        ("prophoto", () => IccProfile.ProPhotoRgb),
        ("a98", () => IccProfile.A98Rgb),
        ("gray22", () => IccProfile.GrayGamma22),
        ("gray_srgb", () => IccProfile.GraySrgb),
    };

    [Fact]
    public void OwnProfilesMatchFixtures()
    {
        var dir = Path.Combine(IccDir, "ours");
        bool dump = Environment.GetEnvironmentVariable("PIXCODEC_ICC_DUMP") == "1";
        if (dump) Directory.CreateDirectory(dir);
        foreach (var (name, make) in OwnProfiles)
        {
            var bytes = make().ToBytes();
            var path = Path.Combine(dir, name + ".icc");
            if (dump) File.WriteAllBytes(path, bytes);
            Assert.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(path)), $"{name}: profile bytes changed, regenerate the fixtures");
            // and it parses back to the same thing
            var p = IccProfile.Parse(bytes);
            Assert.True(p.IsMatrixShaper);
            Assert.Equal(IccProfileClass.Display, p.Class);
        }
    }

    // ------------------------------------------------------------------ LittleCMS conversions

    sealed record Case(string name, string src, string? dst, string intent, bool bpc, string input, string @out, bool lab);

    static List<Case> LoadCases(string dir)
    {
        var f = Path.Combine(dir, "cases.json");
        if (!File.Exists(f)) return new();
        return JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(f))!;
    }

    public static IEnumerable<object[]> LcmsCases() => LoadCases(IccDir).Select(c => new object[] { c.name });

    public static IEnumerable<object[]> ThirdPartyCases()
    {
        var d = TestDataFixtureDir == null ? null : Path.Combine(TestDataFixtureDir, "icc");
        if (d == null || !Directory.Exists(d)) return new[] { new object[] { "" } };
        return LoadCases(d).Select(c => new object[] { c.name });
    }

    static IccProfile Profile(string dir, string spec)
    {
        string path = spec.StartsWith("testdata:") ? Path.Combine(TestDataFixtureDir!, "icc", spec[9..])
            : spec.StartsWith("pixcodec:") ? Path.Combine(IccDir, spec[9..])
            : Path.Combine(dir, spec);
        return IccProfile.Parse(File.ReadAllBytes(path));
    }

    static IccRenderingIntent IntentOf(string s) => s switch
    {
        "perceptual" => IccRenderingIntent.Perceptual,
        "relative" => IccRenderingIntent.RelativeColorimetric,
        "saturation" => IccRenderingIntent.Saturation,
        _ => IccRenderingIntent.AbsoluteColorimetric,
    };

    [Theory]
    [MemberData(nameof(LcmsCases))]
    public void MatchesLittleCms(string name) => RunCase(IccDir, name);

    [TestDataTheory("fixtures/icc/cases.json")]
    [MemberData(nameof(ThirdPartyCases))]
    public void ThirdPartyMatchesLittleCms(string name) => RunCase(Path.Combine(TestDataFixtureDir!, "icc"), name);

    void RunCase(string dir, string name)
    {
        var c = LoadCases(dir).Single(x => x.name == name);
        var src = Profile(dir, c.src);
        var input = LoadRef(Path.Combine(dir, c.input + ".ref"));
        if (input.Channels == 4) input = new RawImage(input.Width, input.Height, ColorModel.Other, input.Data, 4);
        var expected = LoadRef(Path.Combine(dir, c.name + ".ref")).GetData<float>();
        if (c.lab)
        {
            // one LUT tag alone (c.intent names it): normalised outputs in the tag's own encoding
            bool toPcs = c.intent.StartsWith("A2B");
            var stages = IccLut.Read(src.Tag(IccSig.Make(c.intent)), toPcs ? src.ColorSpace == IccColorSpace.Xyz : src.Pcs == IccColorSpace.Xyz,
                out int ni, out int no, out _);
            var x = input.GetData<float>();
            double maxD = 0;
            int n = input.Width * input.Height;
            Assert.Equal(input.Channels, ni);
            for (int i = 0; i < n; i++)
            {
                var o = IccPipeline.Eval(stages, x.AsSpan(i * ni, ni));
                // relative to the slope-free tolerance: steep curves (x^0.4 near 0) and far out of range values amplify float noise
                // (device values are compared within 0..1: what lies beyond is clipped in any conversion)
                for (int k = 0; k < no; k++)
                {
                    double e = expected[i * no + k], v = o[k];
                    if (!toPcs) { e = Math.Clamp(e, 0, 1); v = Math.Clamp(v, 0, 1); }
                    maxD = Math.Max(maxD, Math.Abs(v - e) / ((toPcs ? 5e-4 : 2e-3) + 1e-3 * Math.Abs(e)));
                }
            }
            log.WriteLine($"{name}: max difference {maxD:F3} of the tolerance");
            Assert.True(maxD <= 1, $"differs by {maxD} times the tolerance");
            return;
        }
        var intent = IntentOf(c.intent);
        var dst = Profile(dir, c.dst!);
        var outType = c.@out switch { "u8" => SampleType.UInt8, "u16" => SampleType.UInt16, _ => SampleType.Float32 };
        var res = ImageOps.ConvertColor(input, src, dst, new ColorConvertOptions { Intent = intent, BlackPointCompensation = c.bpc, OutputType = outType });
        var got = ToDoubles(res);
        Assert.Equal(expected.Length, got.Length);
        double max = 0, outMax = outType == SampleType.UInt8 ? 255 : outType == SampleType.UInt16 ? 65535 : 1;
        int worst = 0;
        for (int i = 0; i < got.Length; i++)
        {
            double e = outMax > 1 ? Math.Clamp(Math.Floor(expected[i] * outMax + 0.5), 0, outMax) : expected[i];
            double d = Math.Abs(got[i] - e);
            if (d > max) { max = d; worst = i; }
        }
        log.WriteLine($"{name}: max difference {max} (at {worst}: got {got[worst]}, LittleCMS {expected[worst] * outMax})");
        // 16 bit: LittleCMS evaluates sampled curves on a 16 bit grid even in float transforms, which steep inverse curves
        // (gamma 2.2 near black) amplify to tens of codes
        double tol = outType == SampleType.UInt8 ? 1 : outType == SampleType.UInt16 ? 64 : 1e-3;
        Assert.True(max <= tol, $"max difference {max} at sample {worst}");
    }

    // ------------------------------------------------------------------ images with embedded profiles

    [Theory]
    [InlineData("img_a98.png", false, 1)]
    [InlineData("img_p3.webp", false, 1)]
    [InlineData("img_cmyk.tif", false, 1)]
    [InlineData("img_cmyk.jpg", true, 2)]
    [InlineData("img_a98.jpg", false, 2)]
    public void EmbeddedProfiles(string file, bool adobeCmyk, int tol)
    {
        var bytes = File.ReadAllBytes(Path.Combine(IccDir, file));
        var img = file.EndsWith(".tif") ? Tiff.Decode(bytes, new TiffDecodeOptions { ConvertToRgb = false })
            : file.EndsWith(".jpg") ? Jpeg.Decode(bytes, new JpegDecodeOptions { RawCmyk = true })
            : ImageCodec.Decode(bytes);
        var icc = ImageOps.GetIccProfile(img);
        Assert.NotNull(icc);
        RawImage srgb = adobeCmyk
            ? ImageOps.ConvertColor(img, icc!, IccProfile.Srgb, new ColorConvertOptions { InvertedInput = true })
            : ImageOps.ToSrgb(img);
        Assert.Equal(ColorModel.Rgb, srgb.Color);
        Assert.Equal(SampleType.UInt8, srgb.SampleType);
        if (!adobeCmyk)
        {
            Assert.Null(srgb.Metadata!.IccProfile);
            Assert.Equal(TransferFunction.Srgb, srgb.Metadata.Transfer);
        }
        var expected = LoadRef(Path.Combine(IccDir, file + ".srgb.ref")).GetData<float>();
        var got = srgb.GetData<byte>();
        double max = 0;
        for (int i = 0; i < got.Length; i++) max = Math.Max(max, Math.Abs(got[i] - Math.Clamp(Math.Floor(expected[i] * 255 + 0.5), 0, 255)));
        log.WriteLine($"{file}: max difference {max}");
        Assert.True(max <= tol, $"max difference {max}");
    }

    // ------------------------------------------------------------------ own behaviour

    [Fact]
    public void StandardProfilesRoundTrip()
    {
        foreach (var (name, make) in OwnProfiles)
        {
            var p = make();
            var q = IccProfile.Parse(p.ToBytes());
            Assert.Equal(p.ColorSpace, q.ColorSpace);
            Assert.Equal(p.Description, q.Description);
            Assert.False(string.IsNullOrEmpty(q.Description));
            Assert.True(new IccTransform(p, q).IsIdentity, name);
            if (q.Version.Major == 4) Assert.Equal(IccProfile.ComputeId(q.ToBytes()), q.ProfileId.ToArray());
        }
        // the D50 adapted sRGB colorants as published (e.g. in the ICC's sRGB profiles), to the s15Fixed16 grid
        var s = IccProfile.Srgb;
        Assert.Equal(0.4361, s.RedColorant!.Value.X, 3);
        Assert.Equal(0.2225, s.RedColorant.Value.Y, 3);
        Assert.Equal(0.1431, s.BlueColorant!.Value.X, 3);
        Assert.Equal(0.7141, s.BlueColorant.Value.Z, 3);
        Assert.Equal("sRGB", s.Description);
    }

    [Fact]
    public void Md5MatchesKnownDigests()
    {
        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", Convert.ToHexString(Md5.Hash(Array.Empty<byte>())).ToLowerInvariant());
        Assert.Equal("9e107d9d372bb6826bd81d3542a419d6",
            Convert.ToHexString(Md5.Hash("The quick brown fox jumps over the lazy dog"u8)).ToLowerInvariant());
        var big = new byte[1000];
        for (int i = 0; i < big.Length; i++) big[i] = (byte)(i * 7);
        Assert.Equal(System.Security.Cryptography.MD5.HashData(big), Md5.Hash(big));
    }

    [Fact]
    public void CurvesAndInverses()
    {
        var curves = new[]
        {
            IccCurve.Gamma(2.2), IccCurve.Srgb, IccCurve.Parametric(1, 2.2, 1.05, -0.05), IccCurve.Parametric(2, 2.0, 0.98, 0, 0.01),
            IccCurve.Parametric(4, 2.4, 0.95, 0.05, 0.1, 0.06, 0.002, 0.001),
            IccCurve.Table(Enumerable.Range(0, 1024).Select(i => (ushort)Math.Round(65535 * Math.Pow(i / 1023.0, 1.8))).ToArray()),
        };
        foreach (var c in curves)
            for (int i = 1; i <= 100; i++)
            {
                double x = i / 100.0, y = c.Evaluate(x);
                if (!(y > c.Evaluate(x - 1e-3))) continue; // flat: no unique inverse
                Assert.Equal(x, c.EvaluateInverse(y), 3);
            }
        Assert.Equal(Samples.SrgbDecode(0.5), IccCurve.Srgb.Evaluate(0.5), 12);
        Assert.Equal(0.5, IccCurve.Srgb.EvaluateInverse(Samples.SrgbDecode(0.5)), 12);
    }

    [Fact]
    public void ToSrgbBehaviour()
    {
        // no profile, 8 bit: nothing to do, the same image
        var plain = new RawImage(4, 2, ColorModel.Rgb, Enumerable.Range(0, 24).Select(i => (byte)(i * 10)).ToArray());
        Assert.Same(plain, ImageOps.ToSrgb(plain));
        // an embedded sRGB profile is dropped, the samples stay
        var tagged = new RawImage(4, 2, ColorModel.Rgba, Enumerable.Range(0, 32).Select(i => (byte)(i * 7)).ToArray())
        { Metadata = new ImageMetadata { IccProfile = IccProfile.Srgb.ToBytes(), DpiX = 72 } };
        var r = ImageOps.ToSrgb(tagged);
        Assert.Null(r.Metadata!.IccProfile);
        Assert.Equal(72, r.Metadata.DpiX);
        Assert.Equal(tagged.GetData<byte>(), r.GetData<byte>());
        // linear float -> sRGB
        var lin = new RawImage(3, 1, ColorModel.Rgb, new float[] { 0, 0, 0, 0.5f, 0.5f, 0.5f, 1, 1, 1 });
        var s = ImageOps.ToSrgb(lin, outputType: SampleType.UInt8).GetData<byte>();
        Assert.Equal(new byte[] { 0, 0, 0, 188, 188, 188, 255, 255, 255 }, s);
        // P3 -> sRGB keeps alpha and clips out of gamut colours
        var p3 = new RawImage(1, 1, ColorModel.Rgba, new ushort[] { 0, 65535, 0, 30000 }) { Metadata = new ImageMetadata { IccProfile = IccProfile.DisplayP3.ToBytes() } };
        var q = ImageOps.ToSrgb(p3).GetData<ushort>();
        Assert.Equal(0, q[0]);
        Assert.Equal(65535, q[1]);
        Assert.Equal(30000, q[3]);
        // and to linear sRGB, unclipped
        var l = ImageOps.ToLinearSrgb(p3);
        Assert.Equal(TransferFunction.Linear, l.Metadata!.Transfer);
        Assert.True(l.GetData<float>()[0] < -0.01f);
        // gray stays gray
        var g = new RawImage(2, 1, ColorModel.Gray, new byte[] { 10, 200 }) { Metadata = new ImageMetadata { IccProfile = IccProfile.GrayGamma22.ToBytes() } };
        var gs = ImageOps.ToSrgb(g);
        Assert.Equal(ColorModel.Gray, gs.Color);
        Assert.NotEqual(10, gs.GetData<byte>()[0]);
        // a broken profile falls back to the transfer function
        var broken = new RawImage(1, 1, ColorModel.Rgb, new byte[] { 1, 2, 3 }) { Metadata = new ImageMetadata { IccProfile = new byte[] { 1, 2, 3 } } };
        Assert.Equal(new byte[] { 1, 2, 3 }, ImageOps.ToSrgb(broken).GetData<byte>());
    }

    [Fact]
    public void ConvertColorRecordsTarget()
    {
        var img = new RawImage(2, 1, ColorModel.Rgb, new byte[] { 255, 0, 0, 10, 20, 30 });
        var r = ImageOps.ConvertColor(img, IccProfile.Srgb, IccProfile.DisplayP3);
        Assert.Equal(IccProfile.DisplayP3.ToBytes(), r.Metadata!.IccProfile);
        Assert.Equal(TransferFunction.Srgb, r.Metadata.Transfer);
        var back = ImageOps.ConvertColor(r, IccProfile.DisplayP3, IccProfile.Srgb);
        Assert.True(MaxDiff(img, back) <= 1);
        var l = ImageOps.ConvertColor(img, IccProfile.Srgb, IccProfile.LinearRec2020, IccRenderingIntent.RelativeColorimetric, SampleType.Float32);
        Assert.Equal(TransferFunction.Linear, l.Metadata!.Transfer);
        var a98 = ImageOps.ConvertColor(img, IccProfile.Srgb, IccProfile.A98Rgb);
        Assert.Equal(TransferFunction.Gamma, a98.Metadata!.Transfer);
        Assert.Equal(256 / 563.0, a98.Metadata.Gamma, 4);
        // mismatched channels
        Assert.Throws<ArgumentException>(() => ImageOps.ConvertColor(new RawImage(1, 1, ColorModel.Gray, new byte[1]), IccProfile.Srgb, IccProfile.DisplayP3));
    }

    [Theory]
    [InlineData(ImageFormat.Png)]
    [InlineData(ImageFormat.Jpeg)]
    [InlineData(ImageFormat.Tiff)]
    [InlineData(ImageFormat.Webp)]
    public void OwnProfilesEmbedAndConvert(ImageFormat format)
    {
        // an sRGB image converted to Display P3 and saved with that profile comes back as the same sRGB colours
        var w = 16;
        var px = new byte[w * w * 3];
        for (int i = 0; i < px.Length; i++) px[i] = (byte)(i * 37 % 256);
        var img = new RawImage(w, w, ColorModel.Rgb, px);
        var p3 = ImageOps.ConvertColor(img, IccProfile.Srgb, IccProfile.DisplayP3);
        var file = ImageCodec.Encode(p3, format);
        Assert.True(ImageCodec.GetInfo(file).HasIccProfile);
        var back = ImageCodec.Decode(file);
        var icc = ImageOps.GetIccProfile(back);
        Assert.NotNull(icc);
        Assert.Equal("Display P3", icc!.Description);
        var srgb = ImageOps.ToSrgb(back);
        double d = MaxDiff(img, srgb);
        log.WriteLine($"{format}: round trip max difference {d}");
        // lossless formats: only the 8 bit quantisation in P3 (coarser for saturated sRGB colours); lossy ones: the profile is enough
        if (format is ImageFormat.Png or ImageFormat.Tiff) Assert.True(d <= 3, $"max difference {d}");
    }

    [Fact]
    public void MalformedProfilesThrowCleanly()
    {
        var seeds = new List<byte[]> { IccProfile.Srgb.ToBytes(), IccProfile.GrayGamma22.ToBytes() };
        foreach (var f in new[] { "lcms_cmyk_v4", "lcms_cmyk_v2", "lcms_cmyk8_v2", "lcms_rgblut_v4", "lcms_tables_v2" })
        {
            var p = Path.Combine(IccDir, "profiles", f + ".icc");
            if (File.Exists(p)) seeds.Add(File.ReadAllBytes(p));
        }
        var rng = new Random(1234);
        int n = 0;
        foreach (var seed in seeds)
            for (int iter = 0; iter < 400; iter++)
            {
                var b = (byte[])seed.Clone();
                int kind = iter % 4;
                if (kind == 0) b = b.AsSpan(0, rng.Next(b.Length)).ToArray();
                else
                    for (int k = 0, cnt = rng.Next(1, 12); k < cnt; k++)
                    {
                        int pos = kind == 1 ? rng.Next(Math.Min(b.Length, 132 + 12 * 12)) : rng.Next(b.Length);
                        b[pos] = kind == 3 ? (byte)0xFF : (byte)rng.Next(256);
                    }
                try
                {
                    var p = IccProfile.Parse(b);
                    var other = p.Channels == 1 ? IccProfile.GraySrgb : IccProfile.Srgb;
                    var t = new IccTransform(p, other);
                    t.Transform(new float[p.Channels]);
                    var t2 = new IccTransform(other, p, IccRenderingIntent.RelativeColorimetric, true);
                    t2.Transform(new float[other.Channels]);
                    n++;
                }
                catch (ImageFormatException) { }
                catch (IccException) { }
                catch (ArgumentException) { } // e.g. degenerate profiles with zero channels on one side
            }
        log.WriteLine($"{n} mutated profiles still converted");
    }

    [Fact]
    public void HugeClaimsDoNotAllocate()
    {
        // a lut16 claiming 255^15 grid points in a tiny tag
        var p = IccProfile.Srgb.ToBytes().ToList();
        // append an A2B0 tag pointing at a bogus lut16 header
        var lut = new byte[64];
        "mft2"u8.CopyTo(lut);
        lut[8] = 15; lut[9] = 3; lut[10] = 255;
        lut[48] = 0x10; lut[50] = 0x10;
        var bytes = IccProfile.Srgb.ToBytes();
        int count = (bytes[128] << 24) | (bytes[129] << 16) | (bytes[130] << 8) | bytes[131];
        var nb = new byte[bytes.Length + 12 + lut.Length];
        Array.Copy(bytes, 0, nb, 0, 132 + 12 * count);
        // shift tag data by 12
        Array.Copy(bytes, 132 + 12 * count, nb, 132 + 12 * count + 12, bytes.Length - 132 - 12 * count);
        for (int i = 0; i < count; i++)
        {
            int e = 132 + 12 * i + 4;
            uint off = (uint)(nb[e] << 24 | nb[e + 1] << 16 | nb[e + 2] << 8 | nb[e + 3]) + 12;
            nb[e] = (byte)(off >> 24); nb[e + 1] = (byte)(off >> 16); nb[e + 2] = (byte)(off >> 8); nb[e + 3] = (byte)off;
        }
        int ne = 132 + 12 * count;
        "A2B0"u8.CopyTo(nb.AsSpan(ne));
        uint lo = (uint)(bytes.Length + 12);
        nb[ne + 4] = (byte)(lo >> 24); nb[ne + 5] = (byte)(lo >> 16); nb[ne + 6] = (byte)(lo >> 8); nb[ne + 7] = (byte)lo;
        nb[ne + 11] = (byte)lut.Length;
        count++;
        nb[131] = (byte)count;
        lut.CopyTo(nb, bytes.Length + 12);
        int size = nb.Length;
        nb[0] = (byte)(size >> 24); nb[1] = (byte)(size >> 16); nb[2] = (byte)(size >> 8); nb[3] = (byte)size;
        var prof = IccProfile.Parse(nb);
        Assert.True(prof.HasTag("A2B0"));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ImageFormatException>(() => new IccTransform(prof, IccProfile.Srgb));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 10_000_000);
    }

    [Fact]
    public void Speed()
    {
        int w = 2000, h = 1500;
        var img = new RawImage(w, h, ColorModel.Rgb, new byte[w * h * 3]);
        new Random(5).NextBytes((byte[])img.Data);
        var t = new IccTransform(IccProfile.DisplayP3, IccProfile.Srgb);
        ImageOps.ConvertColor(img, t);
        var sw = Stopwatch.StartNew();
        ImageOps.ConvertColor(img, t);
        log.WriteLine($"P3 -> sRGB 8 bit, 3 MP: {sw.ElapsedMilliseconds} ms");
        var cmykPath = Path.Combine(IccDir, "profiles", "lcms_cmyk_v4.icc");
        if (File.Exists(cmykPath))
        {
            var cmyk = new RawImage(w, h, ColorModel.Other, new byte[w * h * 4], 4);
            new Random(6).NextBytes((byte[])cmyk.Data);
            var tc = new IccTransform(IccProfile.Parse(File.ReadAllBytes(cmykPath)), IccProfile.Srgb);
            ImageOps.ConvertColor(cmyk, tc);
            sw.Restart();
            ImageOps.ConvertColor(cmyk, tc);
            log.WriteLine($"CMYK (lutAToB) -> sRGB 8 bit, 3 MP: {sw.ElapsedMilliseconds} ms");
        }
        var f = new RawImage(w, h, ColorModel.Rgb, new float[w * h * 3]);
        var tf = new IccTransform(IccProfile.DisplayP3, IccProfile.Srgb);
        ImageOps.ConvertColor(f, tf);
        sw.Restart();
        ImageOps.ConvertColor(f, tf);
        log.WriteLine($"P3 -> sRGB float, 3 MP: {sw.ElapsedMilliseconds} ms");
    }
}
