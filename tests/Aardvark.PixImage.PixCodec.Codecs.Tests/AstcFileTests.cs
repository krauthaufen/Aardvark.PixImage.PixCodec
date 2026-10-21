using System.Diagnostics;
using Xunit;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>The plain .astc container: astcenc fixtures (tests/oracle/gen_astc.py), round trips, astcenc decoding our files.</summary>
public class AstcFileTests
{
    public static IEnumerable<object[]> Fixtures => Files("astc", ".astc");

    static AstcProfile ProfileOf(string name) => name.Contains("hdr") ? AstcProfile.Hdr : name.Contains("srgb") ? AstcProfile.LdrSrgb : AstcProfile.Ldr;

    [Theory, MemberData(nameof(Fixtures))]
    public void DecodeMatchesAstcenc(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        var name = Path.GetFileName(file);
        var opt = new AstcDecodeOptions { Profile = ProfileOf(name) };
        var slices = Astc.DecodeVolume(data, opt);
        AssertEqual(LoadRef(Fixture(file) + ".ref"), Astc.Decode(data, opt));
        for (int z = 0; File.Exists(Fixture(file) + $".{z}.ref"); z++) AssertEqual(LoadRef(Fixture(file) + $".{z}.ref"), slices[z]);
        // the profile found by looking at the blocks
        var info = ImageCodec.GetInfo(data);
        Assert.Equal(ImageFormat.Astc, info.Format);
        Assert.Equal(ImageFormat.Astc, ImageCodec.FormatFromExtension(file));
        Assert.Equal(name.Contains("hdr") ? SampleType.Float16 : SampleType.UInt8, info.SampleType);
        Assert.Equal(slices.Length, info.FrameCount);
        var all = ImageCodec.DecodeAll(data);
        Assert.Equal(slices.Length, all.Length);
        if (!name.Contains("srgb")) AssertEqual(slices[^1], all[^1]);
        Assert.Equal(info, ImageCodec.GetInfo(new MemoryStream(data)) with { Metadata = info.Metadata });
        var h = Astc.ReadHeader(data);
        Assert.Equal(slices.Length, h.Depth);
    }

    /// <summary>astcenc's encodings of the encoder test images: header and blocks as the block decoder reads them.</summary>
    [Fact]
    public void ReadsAstcencEncoderFixtures()
    {
        foreach (var f in Directory.GetFiles(Path.Combine(FixtureDir, "astc_encode"), "*_medium.astc"))
        {
            var data = File.ReadAllBytes(f);
            var h = Astc.ReadHeader(data);
            Assert.Equal((160, 120, 1), (h.Width, h.Height, h.Depth));
            var img = Astc.Decode(data);
            Assert.Equal(Path.GetFileName(f).StartsWith("hdr") ? SampleType.Float16 : SampleType.UInt8, img.SampleType);
        }
    }

    internal static string? Astcenc()
    {
        var env = Environment.GetEnvironmentVariable("ASTCENC");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var n in new[] { "astcenc", "astcenc-avx2", "astcenc-sse4.1", "astcenc-neon" })
                foreach (var e in OperatingSystem.IsWindows() ? new[] { ".exe", "" } : new[] { "" })
                    if (dir.Length > 0 && File.Exists(Path.Combine(dir, n + e))) return Path.Combine(dir, n + e);
        return null;
    }

    /// <summary>astcenc's decode of an .astc file (KTX 1 output, (slice, rgba) images).</summary>
    static RawImage[]? AstcencDecode(byte[] astc, string mode)
    {
        var exe = Astcenc();
        if (!Oracles.Have("astcenc", exe != null, "ARM astcenc ($ASTCENC or on the PATH)")) return null;
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(tmp + ".astc", astc);
            var p = Process.Start(new ProcessStartInfo(exe!, $"{mode} \"{tmp}.astc\" \"{tmp}.ktx\" -silent") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
            var t = Ktx.DecodeTexture(File.ReadAllBytes(tmp + ".ktx"));
            var r = t.GetSlices();
            // astcenc writes RGB when every alpha is 1
            return Array.ConvertAll(r, s => s.Color == ColorModel.Rgb ? IconTextureTestUtil.ToRgba(s) : s);
        }
        finally { File.Delete(tmp + ".astc"); File.Delete(tmp + ".ktx"); }
    }

    [Theory]
    [InlineData(4, 4, false)]
    [InlineData(6, 5, false)]
    [InlineData(12, 12, false)]
    [InlineData(4, 4, true)]
    public void EncodeRoundTrip(int bw, int bh, bool hdr)
    {
        var src = hdr ? MiscFormatTests.RandomFloat(29, 19, 4, 3, 30) : MiscFormatTests.RandomInt(29, 19, ColorModel.Rgba, SampleType.UInt8, 7, smooth: true);
        var opt = new AstcEncodeOptions { BlockWidth = bw, BlockHeight = bh, Srgb = false };
        var enc = Astc.Encode(src, opt);
        var h = Astc.ReadHeader(enc);
        Assert.Equal((bw, bh, 1, 29, 19, 1), (h.BlockWidth, h.BlockHeight, h.BlockDepth, h.Width, h.Height, h.Depth));
        Assert.Equal(16 + h.BlockCount * 16, enc.Length);
        var dec = Astc.Decode(enc);
        Assert.Equal(hdr ? SampleType.Float16 : SampleType.UInt8, dec.SampleType);
        // the same blocks as KTX writes
        var ktx = Ktx.Decode(Ktx.Encode(src, new KtxEncodeOptions { Compression = TextureCompression.Astc, AstcBlockWidth = bw, AstcBlockHeight = bh, Srgb = false, Supercompression = KtxSupercompression.None }));
        AssertEqual(ktx, dec);
        if (AstcencDecode(enc, hdr ? "-dh" : "-dl") is { } theirs) AssertEqual(theirs[0], dec);
    }

    [Fact]
    public void EncodeVolumeRoundTrip()
    {
        // with alpha: astcenc writes opaque 3D results as RGB KTX 1 without the row padding KTX 1 asks for
        var slices = Enumerable.Range(0, 5).Select(z => MiscFormatTests.RandomInt(18, 11, ColorModel.Rgba, SampleType.UInt8, 40 + z, smooth: true)).ToArray();
        var enc = Astc.EncodeVolume(slices, new AstcEncodeOptions { BlockWidth = 5, BlockHeight = 5 });
        var dec = Astc.DecodeVolume(enc);
        Assert.Equal(5, dec.Length);
        for (int z = 0; z < 5; z++) Assert.True(Psnr(slices[z], dec[z], 255) > 28);
        if (AstcencDecode(enc, "-dl") is { } theirs)
            for (int z = 0; z < 5; z++) AssertEqual(theirs[z], dec[z]);
    }

    [Fact]
    public void Robustness()
    {
        var data = File.ReadAllBytes(Fixture("astc/vol_4x4x4.astc"));
        Assert.Throws<ImageFormatException>(() => Astc.Decode(data.AsSpan(0, data.Length - 1)));
        Assert.Throws<ImageFormatException>(() => Astc.ReadHeader(data.AsSpan(0, 10)));
        var bad = (byte[])data.Clone();
        bad[4] = 7;   // 7x4x4 is no footprint
        Assert.Throws<ImageFormatException>(() => Astc.Decode(bad));
        bad = (byte[])data.Clone();
        bad[7] = 0; bad[8] = 0; bad[9] = 0;
        Assert.Throws<ImageFormatException>(() => Astc.Decode(bad));
        bad = (byte[])data.Clone();
        bad[9] = 0xFF; bad[12] = 0xFF;   // huge, without the data
        IconTextureTestUtil.AssertRejects(() => Astc.Decode(bad));
        // random block bits decode (to the error colour where illegal) without throwing
        var rnd = new Random(3);
        var noise = (byte[])data.Clone();
        for (int i = 16; i < noise.Length; i++) noise[i] = (byte)rnd.Next(256);
        Astc.DecodeVolume(noise);
        Astc.DecodeVolume(noise, new AstcDecodeOptions { Profile = AstcProfile.Hdr });
    }
}
