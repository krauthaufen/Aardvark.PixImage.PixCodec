using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// Black point compensation (ISO 18619:2015, ISO/TS 21830:2018) on synthetic v2 profiles whose black points follow by
/// hand from the spec procedure (lut16 tables with a Lab PCS; device values and the legacy Lab encoding normalised to 0..1).
/// </summary>
public class IccBpcTests
{
    // ------------------------------------------------------------------ synthetic profiles

    /// <summary>Normalised legacy (lut16) Lab encoding: L* 100 = 0xFF00, a* 0 = 0x8000.</summary>
    static double LN(double l) => l / 100 * 65280 / 65535;
    static double LOf(double n) => n * 100 * 65535 / 65280;
    static readonly double Ab0 = 32768 / 65535.0;

    /// <summary>A lut16 tag: identity matrix, the input curves given (sampled at 4096 points, default identity), a CLUT of
    /// <paramref name="grid"/> points per input evaluated by <paramref name="node"/> (normalised inputs to outputs), identity output curves.</summary>
    static byte[] Lut16(int inCh, int outCh, int grid, Func<double[], double[]> node, Func<double, double>[]? inCurves = null)
    {
        const int n = 4096;
        var b = new List<byte>();
        void U16(double v) { ushort u = (ushort)Math.Round(Math.Clamp(v, 0, 1) * 65535); b.Add((byte)(u >> 8)); b.Add((byte)u); }
        void U32(uint v) { b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 8)); b.Add((byte)v); }
        b.AddRange("mft2"u8.ToArray()); U32(0);
        b.Add((byte)inCh); b.Add((byte)outCh); b.Add((byte)grid); b.Add(0);
        for (int i = 0; i < 9; i++) U32(i % 4 == 0 ? 0x10000u : 0);
        b.Add(n >> 8); b.Add(n & 255); b.Add(0); b.Add(2);
        for (int c = 0; c < inCh; c++)
            for (int i = 0; i < n; i++) U16(inCurves?[c] is { } f ? f(i / (n - 1.0)) : i / (n - 1.0));
        int total = 1;
        for (int c = 0; c < inCh; c++) total *= grid;
        var x = new double[inCh];
        for (int i = 0; i < total; i++)
        {
            // the first input varies slowest
            for (int c = inCh - 1, q = i; c >= 0; c--, q /= grid) x[c] = q % grid / (grid - 1.0);
            foreach (var v in node(x)) U16(v);
        }
        for (int c = 0; c < outCh; c++) { U16(0); U16(1); }
        return b.ToArray();
    }

    /// <summary>A v2 profile with a Lab PCS, a 'desc' and a D50 'wtpt' tag and the tags given.</summary>
    static IccProfile Profile(IccProfileClass cls, IccColorSpace cs, params (string Sig, byte[] Data)[] tags)
    {
        var desc = new List<byte>("desc"u8.ToArray()) { 0, 0, 0, 0, 0, 0, 0, 5 };
        desc.AddRange("test\0"u8.ToArray());
        desc.AddRange(new byte[4 + 4 + 2 + 1 + 67]);
        var wtpt = new byte[20];
        "XYZ "u8.CopyTo(wtpt);
        BinaryPrimitives.WriteInt32BigEndian(wtpt.AsSpan(8), (int)Math.Round(0.9642 * 65536));
        BinaryPrimitives.WriteInt32BigEndian(wtpt.AsSpan(12), 65536);
        BinaryPrimitives.WriteInt32BigEndian(wtpt.AsSpan(16), (int)Math.Round(0.8249 * 65536));
        var all = new List<(string Sig, byte[] Data)> { ("desc", desc.ToArray()), ("wtpt", wtpt) };
        all.AddRange(tags);
        int off = 132 + 12 * all.Count;
        var body = new List<byte>();
        var dir = new List<(string, int, int)>();
        foreach (var (sig, data) in all)
        {
            while ((off + body.Count) % 4 != 0) body.Add(0);
            dir.Add((sig, off + body.Count, data.Length));
            body.AddRange(data);
        }
        var p = new byte[off + body.Count];
        BinaryPrimitives.WriteUInt32BigEndian(p, (uint)p.Length);
        p[8] = 2; p[9] = 0x10;
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(12), (uint)cls);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(16), (uint)cs);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(20), (uint)IccColorSpace.Lab);
        "acsp"u8.CopyTo(p.AsSpan(36));
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(68), (int)Math.Round(0.9642 * 65536));
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(72), 65536);
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(76), (int)Math.Round(0.8249 * 65536));
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(128), (uint)all.Count);
        for (int i = 0; i < dir.Count; i++)
        {
            var (sig, o, len) = dir[i];
            Encoding.ASCII.GetBytes(sig).CopyTo(p, 132 + 12 * i);
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(136 + 12 * i), (uint)o);
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(140 + 12 * i), (uint)len);
        }
        body.CopyTo(p, off);
        return IccProfile.Parse(p);
    }

    /// <summary>A Lab node of an A2B table from L* (a* = b* = 0).</summary>
    static double[] Gray(double l) => new[] { LN(l), Ab0, Ab0 };

    static double BlackL(IccProfile p, IccRenderingIntent intent, bool asSource)
    {
        var b = IccTransform.BlackPoint(p, intent, asSource, false);
        return IccMath.XyzToLab(b.X, b.Y, b.Z).L;
    }

    static void Near(double expected, double actual, double tol = 0.02)
        => Assert.True(Math.Abs(actual - expected) <= tol, $"L* {actual:F3}, expected {expected:F3}");

    static readonly IccRenderingIntent[] Intents =
        { IccRenderingIntent.Perceptual, IccRenderingIntent.RelativeColorimetric, IccRenderingIntent.Saturation };

    // ------------------------------------------------------------------ source black points

    [Fact]
    public void LabSourceBlackIsNeutral()
    {
        // ISO 18619 4.2.3: a Lab data colour space starts from Lab (0, 0, 0), i.e. a* = b* = 0 (0x8000 in lut16), not the
        // normalised zeros (a* = b* = -128). The A2B here darkens with falling a*: L*out = L*in + 0.2 (1 - a) normalised.
        var p = Profile(IccProfileClass.ColorSpace, IccColorSpace.Lab,
            ("A2B0", Lut16(3, 3, 2, x => new[] { x[0] + 0.2 * (1 - x[1]), Ab0, Ab0 })));
        double expected = LOf(0.2 * (1 - Ab0)); // 10.04 (20.08 from a* = -128)
        foreach (var intent in Intents) Near(expected, BlackL(p, intent, true));
    }

    [Fact]
    public void DeviceBlackAsLittleCms()
    {
        // ISO 18619 4.2.2.2 takes the darkest of a vertex set; LittleCMS (PIL.ImageCms 2.16, black box) takes device black
        // alone, gives black 0 when that is lighter than L* 50, and black 0 for an intent without its own A2B table.
        var rgb = Profile(IccProfileClass.Display, IccColorSpace.Rgb, ("A2B0", Lut16(3, 3, 2, x => Gray(5 + 95 * (x[0] + x[1] + x[2]) / 3))));
        Near(5, BlackL(rgb, IccRenderingIntent.Perceptual, true));
        Near(0, BlackL(rgb, IccRenderingIntent.RelativeColorimetric, true)); // spec: D = 5 through A2B0
        Near(0, BlackL(rgb, IccRenderingIntent.Saturation, true));
        // inverse polarity: device black is L* 100 (spec: D = (1, 1, 1) -> 5, resp. 1 -> 10)
        var rgbInv = Profile(IccProfileClass.Display, IccColorSpace.Rgb, ("A2B0", Lut16(3, 3, 2, x => Gray(100 - 95 * (x[0] + x[1] + x[2]) / 3))));
        Near(0, BlackL(rgbInv, IccRenderingIntent.Perceptual, true));
        var grayInv = IccProfile.CreateGray("inverse", CieXy.D65, IccCurve.Table(Enumerable.Range(0, 256).Select(i => (ushort)(65535 - 257 * i)).ToArray()), 2);
        foreach (var intent in Intents) Near(0, BlackL(grayInv, intent, true));
        // CMYK without a B2A: full ink, L* 20 (spec: D = 0001 -> 15)
        var cmyk = Profile(IccProfileClass.Output, IccColorSpace.Cmyk,
            ("A2B0", Lut16(4, 3, 2, x => Gray(100 - 85 * x[3] - 10 * x[0] + 15 * x[0] * x[3]))));
        Near(20, BlackL(cmyk, IccRenderingIntent.Perceptual, true));
        Near(0, BlackL(cmyk, IccRenderingIntent.Saturation, true));
    }

    [Fact]
    public void RgbOutputProfileUsesDarkestColour()
    {
        // ISO 18619 4.2.3: only CMYK (or xCLR) output-capable profiles start from the B2A; an RGB profile, of the output
        // class and with a B2A1 that stops short of black (Lab 0 -> 0.1), still takes D: (0, 0, 0) -> L* 5
        var rgb = Profile(IccProfileClass.Output, IccColorSpace.Rgb,
            ("A2B0", Lut16(3, 3, 2, x => Gray(5 + 95 * (x[0] + x[1] + x[2]) / 3))),
            ("A2B1", Lut16(3, 3, 2, x => Gray(5 + 95 * (x[0] + x[1] + x[2]) / 3))),
            ("B2A0", Lut16(3, 3, 2, x => Enumerable.Repeat(LOf(x[0]) / 100, 3).ToArray())),
            ("B2A1", Lut16(3, 3, 2, x => Enumerable.Repeat(0.1 + 0.9 * LOf(x[0]) / 100, 3).ToArray())));
        Near(5, BlackL(rgb, IccRenderingIntent.RelativeColorimetric, true));
    }

    /// <summary>
    /// An ink limited CMYK printer (or xCLR with <paramref name="channels"/> colourants, K the fourth): the A2B tables give
    /// L* = 100 - 80 K - 10 C, the B2A0 / B2A2 tables reach black L* 20 with K alone (L* below 20 clips to K = 1), the B2A1
    /// table stops at 90 % K (L* 28).
    /// </summary>
    static IccProfile InkLimited(IccColorSpace cs = IccColorSpace.Cmyk, int channels = 4)
    {
        var a2b = Lut16(channels, 3, 2, x => Gray(100 - 80 * x[3] - 10 * x[0]));
        var shadow = new Func<double, double>[] { n => Math.Clamp((LOf(n) - 20) / 80, 0, 1), n => n, n => n };
        double[] Ink(double k) { var v = new double[channels]; v[3] = k; return v; }
        var b2a = Lut16(3, channels, 2, x => Ink(1 - x[0]), shadow);
        var b2a1 = Lut16(3, channels, 2, x => Ink(0.9 * (1 - x[0])), shadow);
        return Profile(IccProfileClass.Output, cs, ("A2B0", a2b), ("A2B1", a2b), ("A2B2", a2b), ("B2A0", b2a), ("B2A1", b2a1), ("B2A2", b2a));
    }

    [Fact]
    public void OutputCmykSourceBlack()
    {
        // ISO 18619 4.2.3: LocalBlack = T(Lab 0, LabIdentity, Source, Perceptual) (B2A0: K = 1), then the A2B of the intent:
        // L* 20, where B2A1 would give 28. LittleCMS (PIL.ImageCms, black box) does so for relative colorimetric only and
        // takes full ink (L* 10) for perceptual and saturation; we follow it.
        var p = InkLimited();
        Near(20, BlackL(p, IccRenderingIntent.RelativeColorimetric, true));
        Near(10, BlackL(p, IccRenderingIntent.Perceptual, true));
        Near(10, BlackL(p, IccRenderingIntent.Saturation, true));
        // relative colorimetric without a B2A: black 0 in LittleCMS (the spec takes D)
        var a2b = Lut16(4, 3, 2, x => Gray(100 - 80 * x[3] - 10 * x[0]));
        var input = Profile(IccProfileClass.Output, IccColorSpace.Cmyk, ("A2B0", a2b), ("A2B1", a2b));
        Near(0, BlackL(input, IccRenderingIntent.RelativeColorimetric, true));
        Near(10, BlackL(input, IccRenderingIntent.Perceptual, true));
    }

    // ------------------------------------------------------------------ destination black points

    /// <summary>A CMYK printer whose B2A tables are sampled from functions of L* alone (a grid of 21 points per input),
    /// with the ink limited A2B of <see cref="InkLimited"/>.</summary>
    static IccProfile Printer(Func<double, double[]> b2a0, Func<double, double[]> b2a1)
    {
        var a2b = Lut16(4, 3, 2, x => Gray(100 - 80 * x[3] - 10 * x[0]));
        return Profile(IccProfileClass.Output, IccColorSpace.Cmyk, ("A2B0", a2b), ("A2B1", a2b), ("A2B2", a2b),
            ("B2A0", Lut16(3, 4, 21, x => b2a0(LOf(x[0])))), ("B2A1", Lut16(3, 4, 21, x => b2a1(LOf(x[0])))),
            ("B2A2", Lut16(3, 4, 21, x => b2a0(LOf(x[0])))));
    }

    [Fact]
    public void LutDestinationBlack()
    {
        // ISO 18619 4.2.5. Ink limited: BT (B2A0 / B2A2 and back) = max(L*, 20); the quadratic through the shadow points is
        // the straight line through (20, 0), z = 20. Relative colorimetric: BT through B2A1 = 10 + 0.9 L* above L* 20 (28
        // below), the midrange is not straight (L* 50: 55), the fit through the line gives z = 20 again.
        var p = InkLimited();
        foreach (var intent in Intents) Near(20, BlackL(p, intent, false), 0.05);
        // a straight midrange keeps InitialLab = T(T(Lab 0, B2A0), A2B1): B2A0 adds C below L* 10, giving black L* 10, where
        // B2A1 (K only) stops at 20
        double K(double l) => Math.Clamp((100 - l) / 80, 0, 1);
        var q = Printer(l => new[] { Math.Clamp((10 - l) / 10, 0, 1), 0, 0, K(l) }, l => new[] { 0, 0, 0, K(l) });
        Near(10, BlackL(q, IccRenderingIntent.RelativeColorimetric, false), 0.05);
        // 4.2.5.3: a flat BT is no valid ramp, black 0
        var flat = Printer(l => new[] { 0, 0, 0, 0.5 }, l => new[] { 0, 0, 0, 0.5 });
        foreach (var intent in Intents) Near(0, BlackL(flat, intent, false));
    }

    [Fact]
    public void DestinationWithoutTheIntentsB2A()
    {
        // LittleCMS (PIL.ImageCms, black box) takes device black, as for matrix / TRC profiles (4.2.4), when the intent has no
        // B2A table of its own, except CMYK in relative colorimetric; ISO 18619 4.2.5 would use the B2A0 table
        var a2b = Lut16(4, 3, 2, x => Gray(100 - 80 * x[3] - 10 * x[0]));
        var b2a = Lut16(3, 4, 21, x => new[] { 0, 0, 0, Math.Clamp((100 - LOf(x[0])) / 80, 0, 1) });
        var noB2A2 = Profile(IccProfileClass.Output, IccColorSpace.Cmyk, ("A2B0", a2b), ("A2B2", a2b), ("B2A0", b2a));
        Near(10, BlackL(noB2A2, IccRenderingIntent.Saturation, false)); // full ink
        Near(20, BlackL(noB2A2, IccRenderingIntent.RelativeColorimetric, false)); // 4.2.5 with B2A0
        var noA2B2 = Profile(IccProfileClass.Output, IccColorSpace.Cmyk, ("A2B0", a2b), ("B2A0", b2a));
        Near(0, BlackL(noA2B2, IccRenderingIntent.Saturation, false));
        var rgbA2b = Lut16(3, 3, 2, x => Gray(5 + 95 * (x[0] + x[1] + x[2]) / 3));
        var rgb = Profile(IccProfileClass.Output, IccColorSpace.Rgb, ("A2B0", rgbA2b),
            ("B2A0", Lut16(3, 3, 21, x => Enumerable.Repeat(Math.Clamp((LOf(x[0]) - 5) / 95, 0, 1), 3).ToArray())));
        Near(0, BlackL(rgb, IccRenderingIntent.RelativeColorimetric, false));
    }

    static readonly Dictionary<string, byte[]> PilBpc = new()
    {
        ["FOGRA29L_uncoated/0"] = new byte[] { 241, 81, 143, 248, 233, 94, 124, 243, 216, 115, 91, 236, 189, 118, 87, 212, 152, 116, 92, 178, 130, 105, 91, 112, 107, 86, 77, 67, 64, 48, 46, 5, 0, 0, 0, 0 },
        ["FOGRA29L_uncoated/1"] = new byte[] { 236, 90, 130, 245, 227, 102, 111, 241, 211, 119, 84, 233, 182, 118, 88, 206, 151, 116, 92, 174, 129, 105, 90, 110, 107, 85, 77, 66, 64, 48, 46, 5, 0, 0, 0, 0 },
        ["FOGRA29L_uncoated/2"] = new byte[] { 241, 81, 143, 248, 233, 94, 124, 243, 216, 115, 91, 236, 189, 118, 87, 212, 152, 116, 92, 178, 130, 105, 91, 112, 107, 86, 77, 67, 64, 48, 46, 5, 0, 0, 0, 0 },
        ["FOGRA39L_coated/0"] = new byte[] { 201, 178, 136, 249, 196, 177, 148, 241, 194, 171, 154, 207, 193, 168, 158, 162, 183, 156, 151, 128, 155, 129, 127, 79, 128, 104, 103, 45, 74, 55, 58, 1, 0, 0, 0, 0 },
        ["FOGRA39L_coated/1"] = new byte[] { 202, 178, 136, 249, 196, 177, 147, 241, 194, 171, 154, 207, 193, 168, 158, 163, 183, 156, 151, 128, 155, 129, 127, 80, 128, 104, 103, 45, 74, 55, 58, 1, 0, 0, 0, 0 },
        ["FOGRA39L_coated/2"] = new byte[] { 201, 178, 136, 249, 196, 177, 148, 241, 194, 171, 154, 207, 193, 168, 158, 162, 183, 156, 151, 128, 155, 129, 127, 79, 128, 104, 103, 45, 74, 55, 58, 1, 0, 0, 0, 0 },
        ["IFRA26S_2004_newsprint/0"] = new byte[] { 155, 183, 0, 242, 155, 183, 0, 242, 126, 183, 28, 242, 125, 176, 35, 242, 125, 138, 72, 242, 97, 61, 58, 242, 22, 4, 0, 186, 5, 1, 0, 76, 0, 0, 0, 0 },
        ["IFRA26S_2004_newsprint/1"] = new byte[] { 107, 72, 68, 242, 100, 64, 60, 242, 85, 47, 45, 242, 61, 22, 24, 242, 45, 14, 14, 228, 22, 4, 0, 187, 14, 3, 0, 137, 3, 0, 0, 65, 0, 0, 0, 0 },
        ["IFRA26S_2004_newsprint/2"] = new byte[] { 155, 183, 0, 242, 155, 183, 0, 242, 126, 183, 28, 242, 125, 176, 35, 242, 125, 138, 72, 242, 97, 61, 58, 242, 22, 4, 0, 186, 5, 1, 0, 76, 0, 0, 0, 0 },
    };

    [TestDataTheory("fixtures/icc/colord/FOGRA29L_uncoated.icc", "fixtures/icc/colord/FOGRA39L_coated.icc", "fixtures/icc/colord/IFRA26S_2004_newsprint.icc")]
    [InlineData("FOGRA29L_uncoated")]
    [InlineData("FOGRA39L_coated")]
    [InlineData("IFRA26S_2004_newsprint")]
    public void PrinterDestinationsMatchLittleCms(string name)
    {
        // sRGB grays to colord's CMYK profiles with BPC, against PIL.ImageCms (LittleCMS 2.16); IFRA26S's perceptual
        // black is z = 0 by the curve fit (4.2.5.5), where full ink would say L* 39
        var dst = IccProfile.Parse(File.ReadAllBytes(Path.Combine(TestUtil.TestDataFixtureDir!, "icc", "colord", name + ".icc")));
        byte[] ramp = { 0, 16, 32, 48, 64, 96, 128, 192, 255 };
        var img = new RawImage(ramp.Length, 1, ColorModel.Rgb, ramp.SelectMany(v => new[] { v, v, v }).ToArray());
        for (int it = 0; it < 3; it++)
        {
            var res = ImageOps.ConvertColor(img, IccProfile.Srgb, dst,
                new ColorConvertOptions { Intent = (IccRenderingIntent)it, BlackPointCompensation = true }).GetData<byte>();
            var expected = PilBpc[$"{name}/{it}"];
            int max = 0;
            for (int i = 0; i < expected.Length; i++) max = Math.Max(max, Math.Abs(res[i] - expected[i]));
            Assert.True(max <= 1, $"{name} intent {it}: max difference {max}");
        }
    }

    // ------------------------------------------------------------------ n-colour profiles

    [Fact]
    public void XclrIsHandledLikeCmyk()
    {
        // ISO/TS 21830 4.2.2: an output-capable xCLR source is treated like CMYK (with the LittleCMS choices of
        // OutputCmykSourceBlack): relative colorimetric from the perceptual B2A (L* 20), else full ink (L* 10)
        foreach (var (cs, n) in new[] { (IccColorSpace.Color5, 5), (IccColorSpace.Color7, 7) })
        {
            var p = InkLimited(cs, n);
            Near(20, BlackL(p, IccRenderingIntent.RelativeColorimetric, true));
            Near(10, BlackL(p, IccRenderingIntent.Perceptual, true));
            foreach (var intent in Intents) Near(20, BlackL(p, intent, false), 0.05);
        }
    }

    [Fact]
    public void TwoColourProfilesGetNoCompensation()
    {
        // ISO/TS 21830 1 and 4.1 leave 2CLR out (and ISO 18619 4.1 allows Gray, RGB, CMYK and Lab only): no BPC, where its
        // 4.2.5 black would be L* 20 here
        var a2b = Lut16(2, 3, 2, x => Gray(100 - 80 * x[1]));
        var b2a = Lut16(3, 2, 21, x => new[] { 0, Math.Clamp((100 - LOf(x[0])) / 80, 0, 1) });
        var p = Profile(IccProfileClass.Output, IccColorSpace.Color2, ("A2B0", a2b), ("A2B1", a2b), ("B2A0", b2a), ("B2A1", b2a));
        var rgb = new float[] { 0, 0, 0, 0.1f, 0.1f, 0.1f, 0.5f, 0.5f, 0.5f };
        foreach (var intent in Intents)
        {
            var a = new float[6]; var b = new float[6];
            new IccTransform(IccProfile.Srgb, p, intent).Transform(rgb, a);
            new IccTransform(IccProfile.Srgb, p, intent, true).Transform(rgb, b);
            Assert.Equal(a, b);
            var c = new float[9]; var d = new float[9];
            new IccTransform(p, IccProfile.Srgb, intent).Transform(a, c);
            new IccTransform(p, IccProfile.Srgb, intent, true).Transform(a, d);
            Assert.Equal(c, d);
        }
    }
}
