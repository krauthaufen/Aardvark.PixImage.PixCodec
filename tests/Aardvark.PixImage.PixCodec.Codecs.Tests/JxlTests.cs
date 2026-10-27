using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Aardvark.ImageCodecs;
using Aardvark.ImageCodecs.Compression;
using Xunit;
using Xunit.Abstractions;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// JPEG XL against libjxl (tests/oracle/gen_jxl.py): lossless files bit exact against libjxl's decode; lossy files as
/// Float32 against libjxl's float decode (djxl), within <see cref="LossyTolerance"/>.
/// </summary>
public class JxlTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Fixtures => Files("jxl", ".jxl");

    /// <summary>
    /// Largest difference allowed against libjxl's float output on lossy files (samples 0..1). Both decoders compute in
    /// float32, but libjxl evaluates the inverse sRGB curve and some filters with polynomial / rational approximations and
    /// fused operations in a different order; the measured differences are 2e-5 .. 1e-4 (well under half a step of a
    /// 12 bit output, 1/8 of an 8 bit step).
    /// </summary>
    const double LossyTolerance = 5e-4;

    static readonly JxlDecodeOptions Stored = new() { ApplyOrientation = false };
    static readonly JxlDecodeOptions AsFloat = new() { ForceFloat = true };

    static JsonElement Json(string path) => JsonDocument.Parse(File.ReadAllText(path + ".json")).RootElement;

    static bool Flag(JsonElement j, string name) => j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Float32 samples of an image; integer samples scaled to 0..1 with the given maximum.</summary>
    static float[] Floats(RawImage img, double max = 0)
    {
        var d = ToDoubles(img);
        double s = img.SampleType is SampleType.Float16 or SampleType.Float32 or SampleType.Float64 ? 1
            : 1 / (max > 0 ? max : img.SampleType switch { SampleType.UInt8 => 255.0, SampleType.UInt16 => 65535.0, _ => 4294967295.0 });
        var f = new float[d.Length];
        for (int i = 0; i < d.Length; i++) f[i] = (float)(d[i] * s);
        return f;
    }

    /// <summary>Compares a Float32 decode to libjxl's float reference: largest difference, after clamping both to 0..1.</summary>
    static double LossyDiff(RawImage expected, RawImage actual, bool premultiplied, out double rms)
    {
        Assert.Equal((expected.Width, expected.Height, expected.Channels), (actual.Width, actual.Height, actual.Channels));
        var e = Floats(expected); var a = Floats(actual);
        int nc = actual.Channels;
        bool alpha = nc is 2 or 4;
        double m = 0, sq = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double ai = a[i];
            int c = i % nc;
            // libjxl returns premultiplied colour for premultiplied files, Jxl.Decode straight alpha
            if (premultiplied && alpha && c != nc - 1) ai *= a[i - c + nc - 1];
            double d = Math.Abs(Math.Clamp(ai, 0, 1) - Math.Clamp(e[i], 0, 1));
            sq += d * d;
            if (d > m) m = d;
        }
        rms = Math.Sqrt(sq / Math.Max(1, a.Length));
        return m;
    }

    // ------------------------------------------------------------------ decoder vs. references

    [Theory, MemberData(nameof(Fixtures))]
    public void DecodeMatchesReference(string file)
    {
        var path = Fixture(file);
        var data = File.ReadAllBytes(path);
        var j = Json(path);
        var expected = LoadRef(path + ".ref");
        var img = Jxl.Decode(data);
        var info = Jxl.GetInfo(data);
        Assert.Equal((info.Width, info.Height, info.Channels, info.SampleType, info.Color), (img.Width, img.Height, img.Channels, img.SampleType, img.Color));
        Assert.Equal(j.GetProperty("frames").GetInt32(), info.FrameCount);
        if (j.GetProperty("lossless").GetBoolean())
        {
            AssertEqual(expected, img);
            return;
        }
        var f = Jxl.Decode(data, AsFloat);
        Assert.Equal(SampleType.Float32, f.SampleType);
        double max = LossyDiff(expected, f, Flag(j, "premultiplied"), out double rms);
        output.WriteLine($"{file}: max {max:E2} rms {rms:E2}");
        Assert.True(max <= LossyTolerance, $"{file}: max difference {max:E2} > {LossyTolerance:E2}");
        // the integer output is the float output rounded (libjxl's own integer output is dithered)
        if (img.SampleType is SampleType.UInt8 or SampleType.UInt16)
        {
            var fi = Floats(img);
            var ff = Floats(f);
            double step = img.SampleType == SampleType.UInt8 ? 1 / 255.0 : 1 / 65535.0;
            int nc = img.Channels;
            bool alpha = nc is 2 or 4;
            for (int i = 0; i < fi.Length; i++)
                Assert.True(Math.Abs(fi[i] - Math.Clamp(ff[i], 0, 1)) <= step * 0.5 + 1e-6, $"{file}: integer sample {i} {fi[i]} vs {ff[i]}");
        }
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void AnimationFramesMatchReferences(string file)
    {
        var path = Fixture(file);
        if (!File.Exists(path + ".0.ref")) return;
        var data = File.ReadAllBytes(path);
        var j = Json(path);
        var a = Jxl.DecodeAnimation(data);
        Assert.True(a.IsAnimated);
        Assert.Equal(j.GetProperty("frames").GetInt32(), a.Frames.Count);
        bool lossless = j.GetProperty("lossless").GetBoolean();
        var fl = lossless ? null : Jxl.DecodeAnimation(data, AsFloat);
        for (int k = 0; k < a.Frames.Count; k++)
        {
            var r = LoadRef($"{path}.{k}.ref");
            if (lossless) AssertEqual(r, a.Frames[k].Image);
            else Assert.True(LossyDiff(r, fl!.Frames[k].Image, false, out _) <= LossyTolerance, $"frame {k}");
        }
        if (j.TryGetProperty("durations_ms", out var dur))
            Assert.Equal(dur.EnumerateArray().Select(e => e.GetInt32()), a.Frames.Select(f => f.DurationMs));
        var all = Jxl.DecodeAll(data);
        Assert.Equal(a.Frames.Count, all.Length);
        var viaCodec = ImageCodec.DecodeAll(data);
        Assert.Equal(a.Frames.Count, viaCodec.Length);
        AssertEqual(all[^1], viaCodec[^1]);
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void SingleThreadedMatchesMultithreaded(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        var mt = Jxl.Decode(data, new JxlDecodeOptions { ForceFloat = true });
        var st = Jxl.Decode(data, new JxlDecodeOptions { ForceFloat = true, Multithreaded = false });
        AssertEqual(mt, st);
    }

    /// <summary>
    /// Decode hands the restored tiles of a still XYB image straight to the output conversion; DecodeAnimation goes through
    /// full frame planes. Both must give the same samples, integer and float.
    /// </summary>
    [Theory, MemberData(nameof(Fixtures))]
    public void TiledOutputMatchesFramePlanes(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        if (Jxl.GetInfo(data).FrameCount != 1) return;
        foreach (var opt in new[] { new JxlDecodeOptions(), new JxlDecodeOptions { ForceFloat = true }, new JxlDecodeOptions { Multithreaded = false } })
            AssertEqual(Jxl.DecodeAnimation(data, opt).Frames[0].Image, Jxl.Decode(data, opt));
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void ImageCodecRoutesJxl(string file)
    {
        var path = Fixture(file);
        var data = File.ReadAllBytes(path);
        Assert.True(Jxl.IsMatch(data));
        Assert.Equal(ImageFormat.Jxl, ImageCodec.DetectFormat(data));
        Assert.Equal(ImageFormat.Jxl, ImageCodec.FormatFromExtension(path));
        var info = ImageCodec.GetInfo(data);
        var direct = Jxl.GetInfo(data);
        Assert.Equal((direct.Width, direct.Height, direct.Channels, direct.SampleType, direct.FrameCount), (info.Width, info.Height, info.Channels, info.SampleType, info.FrameCount));
        var viaPath = ImageCodec.GetInfo(path);
        Assert.Equal((info.Width, info.Height, info.Channels, info.SampleType, info.FrameCount, info.HasIccProfile), (viaPath.Width, viaPath.Height, viaPath.Channels, viaPath.SampleType, viaPath.FrameCount, viaPath.HasIccProfile));
        using var s = new MemoryStream(data);
        var viaStream = ImageCodec.GetInfo(s);
        Assert.Equal((info.Width, info.Height, info.Channels, info.FrameCount), (viaStream.Width, viaStream.Height, viaStream.Channels, viaStream.FrameCount));
        AssertEqual(Jxl.Decode(data), ImageCodec.Decode(data));
        var encoded = ImageCodec.Encode(RawImage.Create(4, 4, ColorModel.Rgb, SampleType.UInt8), ImageFormat.Jxl);
        Assert.Equal((4, 4), (Jxl.Decode(encoded).Width, Jxl.Decode(encoded).Height));
    }

    // ------------------------------------------------------------------ header information

    [Fact]
    public void InfoReportsBitDepthAlphaAndTransfer()
    {
        ImageInfo I(string f) => Jxl.GetInfo(File.ReadAllBytes(Fixture("jxl/" + f)));
        var i = I("vd_16bit.jxl");
        Assert.Equal((SampleType.UInt16, 16, ColorModel.Rgb, TransferFunction.Srgb), (i.SampleType, i.BitsPerSample, i.Color, i.Transfer));
        i = I("ll_rgb16.jxl");   // imagecodecs tags 16 bit input as linear
        Assert.Equal((SampleType.UInt16, 16, TransferFunction.Linear), (i.SampleType, i.BitsPerSample, i.Transfer));
        i = I("ll_10bit.jxl");
        Assert.Equal((SampleType.UInt16, 10), (i.SampleType, i.BitsPerSample));
        i = I("ll_float32.jxl");
        Assert.Equal((SampleType.Float32, 32, TransferFunction.Linear), (i.SampleType, i.BitsPerSample, i.Transfer));
        i = I("ll_float16.jxl");
        Assert.Equal((SampleType.Float16, 16), (i.SampleType, i.BitsPerSample));
        i = I("ll_gray_alpha.jxl");
        Assert.Equal((ColorModel.GrayAlpha, 2), (i.Color, i.Channels));
        i = I("vd_alpha.jxl");
        Assert.Equal((ColorModel.Rgba, SampleType.UInt8, false), (i.Color, i.SampleType, i.HasIccProfile));
        i = I("ll_icc.jxl");
        Assert.True(i.HasIccProfile);
        i = I("vd_icc.jxl");   // custom primaries and a gamma as an enumerated encoding: a profile is built
        Assert.Equal((true, TransferFunction.Gamma), (i.HasIccProfile, i.Transfer));
        i = I("anim_lossless.jxl");
        Assert.Equal((true, 5), (i.IsAnimated, i.FrameCount));
        i = I("vd_e1.jxl");
        Assert.Equal((false, 1), (i.IsAnimated, i.FrameCount));
    }

    [Theory]
    [InlineData("orient3.jxl", 3)]
    [InlineData("orient6.jxl", 6)]
    [InlineData("orient8.jxl", 8)]
    public void OrientationIsApplied(string file, int orientation)
    {
        var data = File.ReadAllBytes(Fixture("jxl/" + file));
        var stored = Jxl.Decode(data, Stored);
        var upright = Jxl.Decode(data);
        Assert.Equal(orientation, stored.Metadata?.Orientation);
        AssertEqual(ImageOps.ApplyOrientation(stored, orientation), upright);
        var si = Jxl.GetInfo(data, Stored);
        var ui = Jxl.GetInfo(data);
        Assert.Equal((stored.Width, stored.Height, orientation, false), (si.Width, si.Height, si.Orientation, si.OrientationApplied));
        Assert.Equal((upright.Width, upright.Height, orientation, true), (ui.Width, ui.Height, ui.Orientation, ui.OrientationApplied));
        if (orientation >= 5) Assert.Equal((stored.Width, stored.Height), (upright.Height, upright.Width));
    }

    [Fact]
    public void IccProfileIsReturned()
    {
        var a98 = File.ReadAllBytes(Fixture("icc/ours/a98.icc"));
        // stored as an ICC profile: returned as is
        Assert.Equal(a98, Jxl.Decode(File.ReadAllBytes(Fixture("jxl/ll_icc.jxl"))).Metadata?.IccProfile);
        // libjxl recognised the profile as primaries + gamma: the profile built from them describes the same colours
        var vd = Jxl.Decode(File.ReadAllBytes(Fixture("jxl/vd_icc.jxl")));
        Assert.NotNull(vd.Metadata?.IccProfile);
        Assert.Equal(TransferFunction.Gamma, vd.Metadata!.Transfer);
        Assert.Equal(1 / 2.19921875, vd.Metadata.Gamma, 3);
        var t = new IccTransform(IccProfile.Parse(vd.Metadata.IccProfile!), IccProfile.Parse(a98), IccRenderingIntent.RelativeColorimetric);
        // (no zero components: a pure power law amplifies the rounding of the primaries there)
        foreach (var c in new[] { new[] { 0.2f, 0.5f, 0.8f }, new[] { 0.95f, 0.2f, 0.15f }, new[] { 0.9f, 0.9f, 0.1f }, new[] { 0.5f, 0.5f, 0.5f } })
        {
            var o = t.Transform(c);
            for (int k = 0; k < 3; k++) Assert.True(Math.Abs(o[k] - c[k]) < 2e-3, $"{c[k]} -> {o[k]}");
        }
        // plain sRGB needs no profile
        Assert.Null(Jxl.Decode(File.ReadAllBytes(Fixture("jxl/vd_e1.jxl"))).Metadata?.IccProfile);
    }

    [Fact]
    public void ExtraChannelsAreReturned()
    {
        var data = File.ReadAllBytes(Fixture("jxl/ll_rgba.jxl"));
        var img = Jxl.Decode(data);
        var ecs = Jxl.DecodeExtraChannels(data);
        var a = Assert.Single(ecs);
        Assert.Equal((JxlExtraChannelKind.Alpha, 8, false, false), (a.Kind, a.BitsPerSample, a.IsFloat, a.AlphaPremultiplied));
        Assert.Equal((img.Width, img.Height, 1, SampleType.Float32), (a.Image.Width, a.Image.Height, a.Image.Channels, a.Image.SampleType));
        var px = (byte[])img.Data; var ap = (float[])a.Image.Data;
        for (int i = 0; i < ap.Length; i++) Assert.Equal(px[i * 4 + 3], (int)Math.Round(ap[i] * 255));
        Assert.Empty(Jxl.DecodeExtraChannels(File.ReadAllBytes(Fixture("jxl/vd_e1.jxl"))));
        Assert.True(Jxl.DecodeExtraChannels(File.ReadAllBytes(Fixture("jxl/ll_premultiplied.jxl")))[0].AlphaPremultiplied);
    }

    [TestDataFact("downloads/jxl-conformance/spot/input.jxl", "downloads/jxl-conformance/cmyk_layers/input.jxl")]
    public void SpotAndBlackChannelsAreReturned()
    {
        string C(string n) => Path.Combine(TestDataDir!, "downloads", "jxl-conformance", n, "input.jxl");
        var spot = Jxl.DecodeExtraChannels(File.ReadAllBytes(C("spot")));
        var s = spot.First(e => e.Kind == JxlExtraChannelKind.SpotColour);
        Assert.Equal(2, spot.Count(e => e.Kind == JxlExtraChannelKind.SpotColour));
        Assert.NotNull(s.SpotColour);
        Assert.Contains(Jxl.DecodeExtraChannels(File.ReadAllBytes(C("cmyk_layers"))), e => e.Kind == JxlExtraChannelKind.Black);
        // CMYK comes back as sRGB without the CMYK profile
        var cmyk = Jxl.Decode(File.ReadAllBytes(C("cmyk_layers")));
        Assert.Equal((ColorModel.Rgba, TransferFunction.Srgb), (cmyk.Color, cmyk.Metadata?.Transfer));
        Assert.Null(cmyk.Metadata?.IccProfile);
        Assert.False(Jxl.GetInfo(File.ReadAllBytes(C("cmyk_layers"))).HasIccProfile);
    }

    // ------------------------------------------------------------------ metadata boxes

    [Theory]
    [InlineData("meta_brob.jxl")]
    [InlineData("meta_plain.jxl")]
    public void MetadataBoxesAreRead(string file)
    {
        var path = Fixture("jxl/" + file);
        var data = File.ReadAllBytes(path);
        var (exif, xmp, _) = Jxl.GetMetadataBoxes(data);
        Assert.NotNull(exif);
        Assert.True(exif!.AsSpan(0, 2).SequenceEqual("II"u8) || exif.AsSpan(0, 2).SequenceEqual("MM"u8));
        Assert.NotNull(xmp);
        Assert.Contains("JXL fixture", System.Text.Encoding.UTF8.GetString(xmp!));

        foreach (var m in new[] { ImageCodec.ReadMetadata(data), ImageCodec.ReadMetadata(path), ImageCodec.ReadMetadata(new MemoryStream(data)) })
        {
            Assert.Equal(ImageFormat.Jxl, m.Format);
            Assert.Equal(("JxlMake", "JxlModel"), (m.Make, m.Model));
            Assert.NotNull(m.Gps);
            Assert.Equal(48 + 12 / 60.0 + 30.5 / 3600, m.Gps!.Latitude!.Value, 9);
            Assert.Equal(16 + 22 / 60.0 + 10.25 / 3600, m.Gps.Longitude!.Value, 9);
            Assert.Equal("JXL fixture", m.Title);
        }
        Assert.Equal("JxlMake", ImageCodec.GetInfo(path).Metadata?.Make);
        using (var s = File.OpenRead(path)) Assert.Equal("JxlModel", ImageCodec.GetInfo(s).Metadata?.Model);
        var img = Jxl.Decode(data);
        Assert.Equal(exif, img.Metadata?.Exif);
        Assert.Equal(xmp, img.Metadata?.Xmp);
    }

    // ------------------------------------------------------------------ damaged input

    static readonly JxlDecodeOptions Bounded = new() { MaxPixels = 1 << 22 };

    static void Survives(byte[] data, string what)
    {
        try { Jxl.GetInfo(data, Bounded); }
        catch (ImageFormatException) { }
        catch (ImageNotSupportedException) { }
        catch (Exception e) { throw new Xunit.Sdk.XunitException($"{what}: GetInfo threw {e.GetType().Name}: {e.Message}\n{e.StackTrace}"); }
        try { Jxl.DecodeAnimation(data, Bounded); }
        catch (ImageFormatException) { }
        catch (ImageNotSupportedException) { }
        catch (Exception e) { throw new Xunit.Sdk.XunitException($"{what}: Decode threw {e.GetType().Name}: {e.Message}\n{e.StackTrace}"); }
    }

    [Theory]
    [InlineData("ll_rgb_e3.jxl")]
    [InlineData("ll_squeeze.jxl")]
    [InlineData("ll_palette.jxl")]
    [InlineData("vd_e7.jxl")]
    [InlineData("vd_patches.jxl")]
    [InlineData("vd_progressive.jxl")]
    [InlineData("jpeg420.jxl")]
    [InlineData("anim_lossy.jxl")]
    [InlineData("meta_brob.jxl")]
    [InlineData("vd_noise.jxl")]
    public void DamagedInputFailsCleanly(string file)
    {
        var data = File.ReadAllBytes(Fixture("jxl/" + file));
        var sw = Stopwatch.StartNew();
        for (int n = 0; n < data.Length; n += Math.Max(1, data.Length / 50))
        {
            var cut = data.AsSpan(0, n).ToArray();
            Survives(cut, $"truncated to {n}");
            if (n > 0) Assert.ThrowsAny<Exception>(() => Jxl.DecodeAnimation(cut));
        }
        var rnd = new Random(file.GetHashCode() & 0x7fff);
        for (int k = 0; k < 150; k++)
        {
            var bad = (byte[])data.Clone();
            int pos = k < 40 ? Math.Min(k, data.Length - 1) : rnd.Next(data.Length);
            bad[pos] ^= (byte)(1 << rnd.Next(8));
            if (k % 3 == 0) bad[rnd.Next(data.Length)] = (byte)rnd.Next(256);
            Survives(bad, $"bit flip at {pos}");
        }
        output.WriteLine($"{file}: {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void NotJxlIsRejected()
    {
        Assert.False(Jxl.IsMatch(new byte[] { 0xFF, 0xD8, 0xFF }));
        Assert.False(Jxl.IsMatch(ReadOnlySpan<byte>.Empty));
        Assert.True(Jxl.IsMatch(new byte[] { 0xFF, 0x0A }));
        Assert.True(Jxl.IsMatch(new byte[] { 0, 0, 0, 12, (byte)'J', (byte)'X', (byte)'L', (byte)' ', 0x0D, 0x0A, 0x87, 0x0A }));
        Assert.Throws<ImageFormatException>(() => Jxl.Decode(new byte[] { 1, 2, 3, 4 }));
        Assert.Throws<ImageFormatException>(() => Jxl.GetInfo(new byte[] { 0xFF, 0x0A }));
    }

    [Fact]
    public void MaxPixelsIsEnforced()
    {
        var data = File.ReadAllBytes(Fixture("jxl/vd_e7.jxl"));
        Assert.ThrowsAny<Exception>(() => Jxl.Decode(data, new JxlDecodeOptions { MaxPixels = 100 }));
    }

    // ------------------------------------------------------------------ Brotli (brob boxes)

    public static IEnumerable<object[]> BrotliFixtures => Files("compression/brotli", ".br");

    static byte[] BrotliExpected(string file)
    {
        var name = Path.GetFileName(file);
        return File.ReadAllBytes(Fixture("compression/brotli/" + name[..name.IndexOf('_')] + ".bin"));
    }

    [Theory, MemberData(nameof(BrotliFixtures))]
    public void BrotliDecodes(string file)
    {
        var src = File.ReadAllBytes(Fixture(file));
        var expected = BrotliExpected(file);
        Assert.Equal(expected, Brotli.Decompress(src, 1 << 24));
        if (expected.Length > 10) Assert.Throws<ImageFormatException>(() => Brotli.Decompress(src, expected.Length - 1));
    }

    [Theory, MemberData(nameof(BrotliFixtures))]
    public void BrotliDamagedFailsCleanly(string file)
    {
        var src = File.ReadAllBytes(Fixture(file));
        var expected = BrotliExpected(file);
        void Try(byte[] b, string what)
        {
            try
            {
                var r = Brotli.Decompress(b, 1 << 20);
                Assert.True(r.Length <= 1 << 20, what);
            }
            catch (ImageFormatException) { }
            catch (Exception e) { throw new Xunit.Sdk.XunitException($"{what}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}"); }
        }
        for (int n = 0; n < src.Length; n += Math.Max(1, src.Length / 200))
        {
            Try(src.AsSpan(0, n).ToArray(), $"truncated to {n}");
            // a complete stream cannot decode from a prefix (the last meta-block flag is missing)
            if (expected.Length > 0 && n < src.Length - 1)
                Assert.Throws<ImageFormatException>(() => Brotli.Decompress(src.AsSpan(0, n), 1 << 24));
        }
        var rnd = new Random(src.Length);
        for (int k = 0; k < 300; k++)
        {
            var b = (byte[])src.Clone();
            b[rnd.Next(b.Length)] ^= (byte)(1 << rnd.Next(8));
            Try(b, "bit flip");
        }
    }

    // ------------------------------------------------------------------ conformance suite (TestData)

    public static IEnumerable<object[]> ConformanceCases()
    {
        var root = TestDataDir == null ? null : Path.Combine(TestDataDir, "downloads", "jxl-conformance");
        if (root == null || !Directory.Exists(root))
        {
            yield return new object[] { "bike" };   // the theory is skipped without the test data
            yield break;
        }
        foreach (var d in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
            if (File.Exists(Path.Combine(d, "input.jxl"))) yield return new object[] { Path.GetFileName(d) };
    }

    /// <summary>Cases that do not meet the suite's tolerance, and why.</summary>
    static readonly Dictionary<string, string> KnownFailures = new();

    /// <summary>Reads a little endian float32 .npy array (frames x height x width x channels).</summary>
    static (float[] data, int[] shape) LoadNpy(string path)
    {
        var b = File.ReadAllBytes(path);
        Assert.True(b[0] == 0x93 && b.AsSpan(1, 5).SequenceEqual("NUMPY"u8), "npy magic");
        int hlen = b[6] == 1 ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8)) : (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8));
        int start = b[6] == 1 ? 10 + hlen : 12 + hlen;
        var hdr = System.Text.Encoding.ASCII.GetString(b, b[6] == 1 ? 10 : 12, hlen);
        Assert.Contains("'<f4'", hdr);
        Assert.Contains("'fortran_order': False", hdr);
        var sh = hdr[(hdr.IndexOf("'shape': (") + 10)..];
        sh = sh[..sh.IndexOf(')')];
        var shape = sh.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToArray();
        var data = new float[(b.Length - start) / 4];
        Buffer.BlockCopy(b, start, data, 0, data.Length * 4);
        return (data, shape);
    }

    [TestDataTheory("downloads/jxl-conformance/bike/test.json"), MemberData(nameof(ConformanceCases))]
    public void Conformance(string name)
    {
        var dir = Path.Combine(TestDataDir!, "downloads", "jxl-conformance", name);
        var desc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "test.json"))).RootElement;
        var frames = desc.GetProperty("frames").EnumerateArray().ToArray();
        var data = File.ReadAllBytes(Path.Combine(dir, "input.jxl"));
        var (refData, shape) = LoadNpy(Path.Combine(dir, "reference_image.npy"));
        var refIcc = File.ReadAllBytes(Path.Combine(dir, "reference.icc"));
        var sw = Stopwatch.StartNew();
        // every channel as decoded, as the suite's references hold them (premultiplied colour, spot colours not rendered)
        var anim = Jxl.DecodeAnimation(data, new JxlDecodeOptions { ForceFloat = true, AllChannels = true, RenderSpotColours = false });
        long ms = sw.ElapsedMilliseconds;
        var container = JpegXl.JxlContainer.Parse(data);
        bool fileHasIcc = JpegXl.JxlImageHeader.Read(new JpegXl.JxlBitReader(container.CodestreamPrefix(data, int.MaxValue))).Colour.WantIcc;
        Assert.Equal(frames.Length, anim.Frames.Count);
        Assert.Equal(shape[0], anim.Frames.Count);
        int h = shape[1], w = shape[2], nc = shape[3];
        var lines = new List<string>();
        bool ok = true;
        for (int k = 0; k < frames.Length; k++)
        {
            var img = anim.Frames[k].Image;
            Assert.Equal((w, h), (img.Width, img.Height));
            Assert.True(img.Channels == nc, $"frame {k}: {img.Channels} channels, reference {nc}");
            var a = (float[])img.Data;
            int ac = img.Channels;
            int ncol = nc is 1 or 2 ? 1 : 3;
            // a file with an enumerated colour encoding is decoded to exactly that encoding, which reference.icc describes;
            // only an embedded profile needs converting (the profile Decode builds for an enumerated encoding is skipped)
            var icc = fileHasIcc ? img.Metadata?.IccProfile : null;
            double peakTol = frames[k].GetProperty("peak_error").GetDouble(), rmsTol = frames[k].GetProperty("rms_error").GetDouble();
            float[]? col = null;
            var t = icc != null && !icc.AsSpan().SequenceEqual(refIcc) && peakTol > 0
                ? new IccTransform(IccProfile.Parse(icc), IccProfile.Parse(refIcc), IccRenderingIntent.RelativeColorimetric) : null;
            if (t != null && !t.IsIdentity)
            {
                var inp = new float[w * h * ncol];
                for (int i = 0; i < w * h; i++) for (int c = 0; c < ncol; c++) inp[i * ncol + c] = Math.Clamp(a[(long)i * ac + c], 0, 1);
                col = new float[w * h * ncol];
                t.Transform(inp, col);
            }
            var sq = new double[nc];
            double peak = 0;
            long off = (long)k * w * h * nc;
            for (int i = 0; i < w * h; i++)
                for (int c = 0; c < nc; c++)
                {
                    double v = col != null && c < ncol ? col[i * ncol + c] : a[(long)i * ac + c];
                    double d = Math.Abs(Math.Clamp(v, 0, 1) - Math.Clamp(refData[off + (long)i * nc + c], 0, 1));
                    sq[c] += d * d;
                    if (d > peak) peak = d;
                }
            double rms = sq.Max(s => Math.Sqrt(s / (w * h)));
            bool good = rms <= rmsTol && peak <= peakTol;
            ok &= good;
            if (!good || k < 2) lines.Add($"f{k} rms {rms:E2}/{rmsTol:E0} peak {peak:E2}/{peakTol:E0}");
        }
        output.WriteLine($"{name}: {(ok ? "PASS" : "FAIL")} {ms} ms {string.Join("; ", lines.Take(4))}");
        if (KnownFailures.TryGetValue(name, out var why))
        {
            output.WriteLine("known failure: " + why);
            return;
        }
        Assert.True(ok, $"{name}: {string.Join("; ", lines.Take(4))}");
    }
}
