using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// ETC1 / ETC2 / EAC encoding (<see cref="EtcEncoder"/>), round tripped through the decoders (<see cref="EtcBlocks"/>).
/// Quality gates come from the oracles in tests/oracle/gen_etc_encode.py (etcpak and etc2comp, run on the same fixture
/// images; see tests/fixtures/etc_encode/oracle.txt).
/// </summary>
public class EtcEncodeTests
{
    readonly ITestOutputHelper _out;
    public EtcEncodeTests(ITestOutputHelper output) => _out = output;

    public enum Fmt { Etc1, Etc2Rgb, Etc2A1, Etc2Rgba, R11, Rg11, R11s, Rg11s }

    static int BlockBytes(Fmt f) => f is Fmt.Etc2Rgba or Fmt.Rg11 or Fmt.Rg11s ? 16 : 8;

    /// <summary>The 4x4 RGBA8 block at (bx, by), edges replicated.</summary>
    static void Gather(byte[] img, int w, int h, int bx, int by, Span<byte> blk)
    {
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                int sx = Math.Min(bx * 4 + x, w - 1), sy = Math.Min(by * 4 + y, h - 1);
                for (int c = 0; c < 4; c++) blk[(y * 4 + x) * 4 + c] = img[(sy * w + sx) * 4 + c];
            }
    }

    /// <summary>Encodes an RGBA8 image (block rows in parallel). EAC formats take R (and G) as 8 bit values widened to
    /// 16 bits (v * 257; signed: (v - 128) * 258, clamped), as the oracles see them.</summary>
    internal static byte[] Encode(byte[] img, int w, int h, Fmt f, BcQuality q, bool perceptual = false, int threads = -1)
    {
        int bw = (w + 3) / 4, bh = (h + 3) / 4, bpb = BlockBytes(f);
        var o = new byte[bw * bh * bpb];
        var opt = new ParallelOptions { MaxDegreeOfParallelism = threads };
        Parallel.For(0, bh, opt, by =>
        {
            Span<byte> blk = stackalloc byte[64];
            Span<ushort> u = stackalloc ushort[32];
            Span<short> s = stackalloc short[32];
            for (int bx = 0; bx < bw; bx++)
            {
                Gather(img, w, h, bx, by, blk);
                var dst = o.AsSpan((by * bw + bx) * bpb, bpb);
                for (int i = 0; i < 16; i++)
                    for (int c = 0; c < 2; c++)
                    {
                        u[i * 2 + c] = (ushort)(blk[i * 4 + c] * 257);
                        s[i * 2 + c] = Snorm(blk[i * 4 + c]);
                    }
                switch (f)
                {
                    case Fmt.Etc1: EtcEncoder.Etc1(blk, dst, q, perceptual); break;
                    case Fmt.Etc2Rgb: EtcEncoder.Etc2Rgb(blk, dst, q, perceptual); break;
                    case Fmt.Etc2A1: EtcEncoder.Etc2PunchThrough(blk, dst, q, perceptual); break;
                    case Fmt.Etc2Rgba: EtcEncoder.Etc2Rgba(blk, dst, q, perceptual); break;
                    case Fmt.R11: EtcEncoder.EacR11(u, 2, dst, q); break;
                    case Fmt.Rg11: EtcEncoder.EacRg11(u, dst, q); break;
                    case Fmt.R11s: EtcEncoder.EacR11Signed(s, 2, dst, q); break;
                    case Fmt.Rg11s: EtcEncoder.EacRg11Signed(s, dst, q); break;
                }
            }
        });
        return o;
    }

    /// <summary>8 bit to SNORM16 the way the oracle script maps its signed input: (v - 128) / 127, clamped.</summary>
    internal static short Snorm(byte v) => (short)Math.Max(-32767, (int)Math.Round((v - 128) * 32767.0 / 127));

    /// <summary>Decoded image: RGBA8 for the colour formats, for EAC 16 bit values per channel (1 or 2 channels).</summary>
    internal static (byte[] rgba, int[] eac) Decode(byte[] data, int w, int h, Fmt f)
    {
        int bw = (w + 3) / 4, bh = (h + 3) / 4, bpb = BlockBytes(f), ch = f is Fmt.Rg11 or Fmt.Rg11s ? 2 : 1;
        var rgba = new byte[w * h * 4];
        var eac = new int[w * h * ch];
        Span<byte> blk = stackalloc byte[64];
        Span<ushort> u = stackalloc ushort[32];
        Span<short> s = stackalloc short[32];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                var b = data.AsSpan((by * bw + bx) * bpb, bpb);
                switch (f)
                {
                    case Fmt.Etc1: EtcBlocks.Etc1(b, blk); break;
                    case Fmt.Etc2Rgb: EtcBlocks.Etc2Rgb(b, blk); break;
                    case Fmt.Etc2A1: EtcBlocks.Etc2PunchThrough(b, blk); break;
                    case Fmt.Etc2Rgba: EtcBlocks.Etc2Rgba(b, blk); break;
                    case Fmt.R11: EtcBlocks.EacR11(b, u, 1); break;
                    case Fmt.Rg11: EtcBlocks.EacRg11(b, u); break;
                    case Fmt.R11s: EtcBlocks.EacR11Signed(b, s, 1); break;
                    case Fmt.Rg11s: EtcBlocks.EacRg11Signed(b, s); break;
                }
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        int px = bx * 4 + x, py = by * 4 + y;
                        if (px >= w || py >= h) continue;
                        int i = y * 4 + x, d = py * w + px;
                        if (f <= Fmt.Etc2Rgba) blk.Slice(i * 4, 4).CopyTo(rgba.AsSpan(d * 4));
                        else
                            for (int c = 0; c < ch; c++)
                                eac[d * ch + c] = f is Fmt.R11 or Fmt.Rg11 ? u[i * ch + c] : s[i * ch + c];
                    }
            }
        return (rgba, eac);
    }

    /// <summary>PSNR of the format's channels: RGB (punch-through: of the opaque texels, transparency must match),
    /// alpha separately for RGBA8; EAC in 16 bit units against the widened input.</summary>
    internal static (double color, double alpha) Psnr(byte[] img, int w, int h, Fmt f, byte[] data)
    {
        var (rgba, eac) = Decode(data, w, h, f);
        if (f >= Fmt.R11)
        {
            int ch = f is Fmt.Rg11 or Fmt.Rg11s ? 2 : 1;
            bool signed = f is Fmt.R11s or Fmt.Rg11s;
            double se = 0;
            for (int i = 0; i < w * h; i++)
                for (int c = 0; c < ch; c++)
                {
                    double t = signed ? Snorm(img[i * 4 + c]) : img[i * 4 + c] * 257;
                    double e = eac[i * ch + c] - t;
                    se += e * e;
                }
            double peak = signed ? 65534 : 65535;
            return (se == 0 ? 99 : 10 * Math.Log10(peak * peak * w * h * ch / se), 0);
        }
        double sc = 0, sa = 0; long n = 0;
        for (int i = 0; i < w * h; i++)
        {
            bool opaque = f != Fmt.Etc2A1 || img[i * 4 + 3] >= 128;
            if (f == Fmt.Etc2A1) Assert.Equal(opaque ? 255 : 0, rgba[i * 4 + 3]);
            if (f == Fmt.Etc2Rgba) { double e = rgba[i * 4 + 3] - img[i * 4 + 3]; sa += e * e; }
            if (!opaque) continue;
            for (int c = 0; c < 3; c++) { double e = rgba[i * 4 + c] - img[i * 4 + c]; sc += e * e; }
            n++;
        }
        double pc = sc == 0 ? 99 : 10 * Math.Log10(255.0 * 255 * 3 * n / sc);
        double pa = sa == 0 ? 99 : 10 * Math.Log10(255.0 * 255 * w * h / sa);
        return (pc, pa);
    }

    static byte[] LoadRgba(string path, out int w, out int h)
    {
        var img = ImageCodec.Decode(File.ReadAllBytes(path));
        w = img.Width; h = img.Height;
        var s = img.GetData<byte>();
        int ch = img.Channels;
        var o = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            for (int c = 0; c < 3; c++) o[i * 4 + c] = s[i * ch + Math.Min(c, ch - 1)];
            o[i * 4 + 3] = ch == 4 ? s[i * ch + 3] : (byte)255;
        }
        return o;
    }

    // ------------------------------------------------------------------ quality against the oracles

    /// <summary>oracle.txt: (image, format, oracle) -> (colour PSNR, alpha PSNR).</summary>
    static Dictionary<(string, Fmt, string), (double c, double a)> Oracle()
    {
        var d = new Dictionary<(string, Fmt, string), (double, double)>();
        foreach (var line in File.ReadAllLines(Fixture("etc_encode/oracle.txt")))
        {
            if (line.StartsWith('#') || line.Length == 0) continue;
            var p = line.Split(' ');
            double c = double.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture);
            double a = p.Length > 4 ? double.Parse(p[4], System.Globalization.CultureInfo.InvariantCulture) : 0;
            d[(p[0], Enum.Parse<Fmt>(p[1]), p[2])] = (c, a);
        }
        return d;
    }

    public static IEnumerable<object[]> QualityCases()
    {
        foreach (var img in new[] { "kodim23_crop", "kodim13_crop", "synthetic" })
            foreach (var f in Enum.GetValues<Fmt>())
                foreach (var q in new[] { BcQuality.Fast, BcQuality.Normal, BcQuality.High })
                    yield return new object[] { img, f, q };
    }

    /// <summary>
    /// Gates, all relative to the oracles' results on the same image: Normal at least etcpak's PSNR (where etcpak has the
    /// format; else etc2comp at effort 40 less 0.3 dB), High within 0.2 dB of etc2comp's best (effort 100), Fast within
    /// 0.6 dB of etcpak (else 1 dB of etc2comp 40). ETC2 RGBA8: etc2comp weighs colour by alpha, so its colour PSNR is not
    /// comparable and colour is held to etcpak; alpha to etc2comp. Punch-through and signed EAC: etc2comp only.
    /// </summary>
    [Theory, MemberData(nameof(QualityCases))]
    public void QualityAgainstOracles(string image, Fmt f, BcQuality q)
    {
        var oracle = Oracle();
        var img0 = LoadRgba(Fixture($"etc_encode/{image}.png"), out int w, out int h);
        var img = f is Fmt.Etc2A1 or Fmt.Etc2Rgba ? WithAlpha(img0, w, h, f == Fmt.Etc2A1) : img0;
        var (pc, pa) = Psnr(img, w, h, f, Encode(img, w, h, f, q));
        bool hasPak = oracle.TryGetValue((image, f, "etcpak"), out var pak);
        var e40 = oracle[(image, f, "etc2comp40")];
        var e100 = oracle[(image, f, "etc2comp100")];
        _out.WriteLine($"{image} {f} {q}: {pc:F3} dB" + (f == Fmt.Etc2Rgba ? $" alpha {pa:F3}" : "")
                       + $"   etcpak {(hasPak ? pak.c.ToString("F3") : "-")}, etc2comp 40 {e40.c:F3}, 100 {e100.c:F3}"
                       + (f == Fmt.Etc2Rgba ? $" alpha {e100.a:F3}" : ""));
        double gate = q switch
        {
            BcQuality.Fast => hasPak ? pak.c - 0.6 : e40.c - 1.0,
            BcQuality.Normal => hasPak ? pak.c : e40.c - 0.3,
            _ => f == Fmt.Etc2Rgba ? pak.c : e100.c - 0.2,
        };
        Assert.True(pc >= gate, $"{image} {f} {q}: {pc:F3} dB < {gate:F3}");
        if (f == Fmt.Etc2Rgba)
        {
            double ga = q switch { BcQuality.Fast => pak.a, BcQuality.Normal => e40.a - 0.3, _ => e100.a - 0.2 };
            Assert.True(pa >= ga, $"{image} alpha {q}: {pa:F3} dB < {ga:F3}");
        }
    }

    // ------------------------------------------------------------------ the search's error is the decoder's

    [Theory]
    [InlineData(BcQuality.Fast)]
    [InlineData(BcQuality.Normal)]
    [InlineData(BcQuality.High)]
    public void MeasuredErrorMatchesDecoder(BcQuality q)
    {
        var rng = new Random(7);
        Span<byte> px = stackalloc byte[64];
        Span<byte> blk = stackalloc byte[8];
        Span<byte> dec = stackalloc byte[64];
        int[] modes = new int[5];
        for (int n = 0; n < 3000; n++)
        {
            RandomBlock(rng, px, n);
            foreach (var fmt in new[] { EtcEncoder.ColorFormat.Etc1, EtcEncoder.ColorFormat.Etc2, EtcEncoder.ColorFormat.PunchThrough })
                foreach (bool perceptual in new[] { false, true })
                {
                    int err = EtcEncoder.Color(px, blk, fmt, q, perceptual);
                    if (fmt == EtcEncoder.ColorFormat.PunchThrough) EtcBlocks.Etc2PunchThrough(blk, dec);
                    else EtcBlocks.Etc2Rgb(blk, dec);
                    int wr = perceptual ? 14 : 1, wg = perceptual ? 45 : 1, wb = perceptual ? 5 : 1, e = 0;
                    for (int i = 0; i < 16; i++)
                    {
                        bool opaque = fmt != EtcEncoder.ColorFormat.PunchThrough || px[i * 4 + 3] >= 128;
                        Assert.Equal(opaque ? 255 : 0, dec[i * 4 + 3]);
                        if (!opaque) continue;
                        int dr = dec[i * 4] - px[i * 4], dg = dec[i * 4 + 1] - px[i * 4 + 1], db = dec[i * 4 + 2] - px[i * 4 + 2];
                        e += wr * dr * dr + wg * dg * dg + wb * db * db;
                    }
                    Assert.True(err == e, $"block {n} {fmt} {q}: search {err}, decoded {e}");
                    if (fmt == EtcEncoder.ColorFormat.Etc2 && !perceptual) modes[ModeOf(blk)]++;
                    if (fmt == EtcEncoder.ColorFormat.Etc1) Assert.True(ModeOf(blk) <= 1, "ETC1 block uses an ETC2 mode");
                }
        }
        _out.WriteLine($"ETC2 modes (individual, differential, T, H, planar): {string.Join(", ", modes)}");
    }

    // ------------------------------------------------------------------ trivial blocks, edge values, determinism

    /// <summary>
    /// Single colour blocks: the encoder searches every constant the formats can produce (a base colour plus one modifier,
    /// T / H paint colours, constant planes), so the result must equal the best constant found by brute force over the
    /// decoder in <see cref="SolidColoursOptimal"/>; here every texel must decode alike, the largest channel error over a
    /// grid of colours is reported (the formats leave up to 3 for some colours, as that brute force confirms) and colours
    /// ETC2 holds exactly (4 bit levels as T mode paint, black, white) decode exactly.
    /// </summary>
    [Theory]
    [InlineData(0)]   // ETC1
    [InlineData(1)]   // ETC2
    [InlineData(2)]   // punch-through
    public void SolidColours(int format)
    {
        var fmt = (EtcEncoder.ColorFormat)format;
        Span<byte> px = stackalloc byte[64];
        Span<byte> blk = stackalloc byte[8];
        Span<byte> dec = stackalloc byte[64];
        int worst = 0;
        foreach (var q in new[] { BcQuality.Fast, BcQuality.High })
            for (int r = 0; r < 256; r += 8)
                for (int g = 3; g < 256; g += 8)
                    for (int b = 5; b < 256; b += 8)
                    {
                        for (int i = 0; i < 16; i++) { px[i * 4] = (byte)r; px[i * 4 + 1] = (byte)g; px[i * 4 + 2] = (byte)b; px[i * 4 + 3] = 255; }
                        EtcEncoder.Color(px, blk, fmt, q, false);
                        if (fmt == EtcEncoder.ColorFormat.PunchThrough) EtcBlocks.Etc2PunchThrough(blk, dec); else EtcBlocks.Etc2Rgb(blk, dec);
                        for (int i = 0; i < 16; i++)
                            for (int c = 0; c < 4; c++) Assert.Equal(dec[c], dec[i * 4 + c]);
                        worst = Math.Max(worst, Math.Max(Math.Abs(dec[0] - r), Math.Max(Math.Abs(dec[1] - g), Math.Abs(dec[2] - b))));
                    }
        _out.WriteLine($"{fmt}: largest channel error of a solid block {worst}");
        Assert.True(worst <= 3, $"{worst}");
        if (fmt == EtcEncoder.ColorFormat.Etc1) return;   // its colours always carry a modifier

        foreach (var (r, g, b) in new[] { (0, 0, 0), (255, 255, 255), (17, 17, 17), (136, 136, 136), (255, 0, 0), (0, 255, 0), (0, 0, 255), (51, 204, 119) })
        {
            for (int i = 0; i < 16; i++) { px[i * 4] = (byte)r; px[i * 4 + 1] = (byte)g; px[i * 4 + 2] = (byte)b; px[i * 4 + 3] = 255; }
            Assert.Equal(0, EtcEncoder.Color(px, blk, fmt, BcQuality.Fast, false));
            EtcBlocks.Etc2Rgb(blk, dec);
            Assert.True(dec[0] == r && dec[1] == g && dec[2] == b, $"({r}, {g}, {b})");
        }
    }

    /// <summary>Solid blocks for every format choice against the brute force best constant (differential mode: every
    /// 5 bit base, table and modifier; ETC2 adds 4 bit colours with the T / H distances and 6/7/6 bit planes).</summary>
    [Fact]
    public void SolidColoursOptimal()
    {
        var rng = new Random(3);
        Span<byte> px = stackalloc byte[64];
        Span<byte> blk = stackalloc byte[8];
        Span<byte> dec = stackalloc byte[64];
        int[] small = { 2, 5, 9, 13, 18, 24, 33, 47 }, large = { 8, 17, 29, 42, 60, 80, 106, 183 }, dist = { 3, 6, 11, 16, 23, 32, 41, 64 };
        for (int n = 0; n < 300; n++)
        {
            int r = rng.Next(256), g = rng.Next(256), b = rng.Next(256);
            for (int i = 0; i < 16; i++) { px[i * 4] = (byte)r; px[i * 4 + 1] = (byte)g; px[i * 4 + 2] = (byte)b; px[i * 4 + 3] = 255; }
            int got = EtcEncoder.Color(px, blk, EtcEncoder.ColorFormat.Etc2, BcQuality.Normal, false) / 16;
            // brute force: each channel independently for a shared offset
            int Best(int v, int levels, Func<int, int> ext, int m)
            {
                int best = int.MaxValue;
                for (int x = 0; x < levels; x++) { int d = Math.Clamp(ext(x) + m, 0, 255) - v; best = Math.Min(best, d * d); }
                return best;
            }
            int Fit(int levels, Func<int, int> ext, int m) => Best(r, levels, ext, m) + Best(g, levels, ext, m) + Best(b, levels, ext, m);
            int opt = int.MaxValue;
            Func<int, int> e4 = x => x * 17, e5 = x => x << 3 | x >> 2, e6 = x => x << 2 | x >> 4, e7 = x => x << 1 | x >> 6;
            for (int t = 0; t < 8; t++)
                foreach (int m in new[] { small[t], -small[t], large[t], -large[t] })
                    opt = Math.Min(opt, Math.Min(Fit(32, e5, m), Fit(16, e4, m)));
            foreach (int d in dist) opt = Math.Min(opt, Math.Min(Fit(16, e4, d), Fit(16, e4, -d)));
            opt = Math.Min(opt, Fit(16, e4, 0));
            opt = Math.Min(opt, Best(r, 64, e6, 0) + Best(g, 128, e7, 0) + Best(b, 64, e6, 0));
            Assert.True(got == opt, $"({r}, {g}, {b}): {got} vs best {opt}");
        }
    }

    [Fact]
    public void PunchThrough()
    {
        Span<byte> px = stackalloc byte[64];
        Span<byte> blk = stackalloc byte[8];
        Span<byte> dec = stackalloc byte[64];
        var rng = new Random(11);
        // all transparent: every texel (0, 0, 0, 0)
        for (int i = 0; i < 64; i++) px[i] = (byte)rng.Next(256);
        for (int i = 0; i < 16; i++) px[i * 4 + 3] = 127;
        EtcEncoder.Etc2PunchThrough(px, blk);
        EtcBlocks.Etc2PunchThrough(blk, dec);
        for (int i = 0; i < 64; i++) Assert.Equal(0, dec[i]);
        // the threshold is 128, every pattern of opaque texels survives, opaque colours stay close
        for (int n = 0; n < 500; n++)
        {
            int mask = rng.Next(1 << 16);
            int r = rng.Next(256), g = rng.Next(256), b = rng.Next(256);
            for (int i = 0; i < 16; i++)
            {
                px[i * 4] = (byte)Math.Clamp(r + (i & 3) * 5, 0, 255); px[i * 4 + 1] = (byte)g; px[i * 4 + 2] = (byte)Math.Clamp(b - (i >> 2) * 7, 0, 255);
                px[i * 4 + 3] = (byte)((mask >> i & 1) != 0 ? 128 + rng.Next(128) : rng.Next(128));
            }
            foreach (var q in new[] { BcQuality.Fast, BcQuality.Normal })
            {
                EtcEncoder.Etc2PunchThrough(px, blk, q);
                EtcBlocks.Etc2PunchThrough(blk, dec);
                for (int i = 0; i < 16; i++)
                {
                    bool opaque = (mask >> i & 1) != 0;
                    Assert.Equal(opaque ? 255 : 0, dec[i * 4 + 3]);
                    if (opaque)
                        for (int c = 0; c < 3; c++) Assert.True(Math.Abs(dec[i * 4 + c] - px[i * 4 + c]) <= 24, $"block {n} texel {i}");
                    else
                        for (int c = 0; c < 3; c++) Assert.Equal(0, dec[i * 4 + c]);
                }
            }
        }
    }

    /// <summary>EAC: constant blocks decode to the nearest representable value (alpha exactly), the signed extremes and
    /// zero exactly, out of range floats clamp, NaN reads as 0, the reserved signed base -128 is never written.</summary>
    [Fact]
    public void EacEdgeValues()
    {
        Span<byte> blk = stackalloc byte[8];
        Span<ushort> u = stackalloc ushort[16];
        Span<short> s = stackalloc short[16];
        Span<byte> a = stackalloc byte[16];
        Span<float> f = stackalloc float[16];
        Span<ushort> du = stackalloc ushort[16];
        Span<short> ds = stackalloc short[16];
        Span<byte> da = stackalloc byte[64];
        Span<byte> blk2 = stackalloc byte[8];
        foreach (var q in new[] { BcQuality.Fast, BcQuality.Normal, BcQuality.High })
        {
            for (int v = 0; v < 256; v++)
            {
                a.Fill((byte)v);
                EtcEncoder.EacAlpha8(a, 1, blk, q);
                EtcBlocks.EacAlpha8(blk, da, 1);
                for (int i = 0; i < 16; i++) Assert.Equal(v, da[i]);
            }
            for (int v = 0; v <= 65535; v += 97)
            {
                u.Fill((ushort)v);
                EtcEncoder.EacR11(u, 1, blk, q);
                EtcBlocks.EacR11(blk, du, 1);
                int nearest = Enumerable.Range(0, 2048).Select(x => Math.Abs(EtcBlocks.Extend11(x) - v)).Min();
                for (int i = 0; i < 16; i++) Assert.Equal(nearest, Math.Abs(du[i] - v));
            }
            foreach (int v in new[] { -32768, -32767, -32766, -1, 0, 1, 32766, 32767 }.Concat(Enumerable.Range(0, 300).Select(k => k * 219 - 32767)))
            {
                s.Fill((short)v);
                EtcEncoder.EacR11Signed(s, 1, blk, q);
                Assert.NotEqual(0x80, blk[0]);
                EtcBlocks.EacR11Signed(blk, ds, 1);
                int t = Math.Max(v, -32767);
                int nearest = Enumerable.Range(-1023, 2047).Select(x => Math.Abs(EtcBlocks.Extend11Signed(x) - t)).Min();
                for (int i = 0; i < 16; i++) Assert.Equal(nearest, Math.Abs(ds[i] - t));
                if (t is -32767 or 0 or 32767) Assert.Equal(t, ds[0]);
            }
            // a block holding both extremes and zero
            for (int i = 0; i < 16; i++) s[i] = (short)(i % 3 == 0 ? -32768 : i % 3 == 1 ? 32767 : 0);
            EtcEncoder.EacR11Signed(s, 1, blk, q);
            Assert.NotEqual(0x80, blk[0]);
            EtcBlocks.EacR11Signed(blk, ds, 1);
            for (int i = 0; i < 16; i++) Assert.True(Math.Abs(ds[i] - Math.Max((int)s[i], -32767)) <= 2048, $"{q} {i}: {ds[i]}");
            // floats: the same blocks as the 16 bit values they map to (clamped, NaN as 0)
            for (int i = 0; i < 16; i++) f[i] = i switch { 0 => -2f, 1 => 2f, 2 => float.NaN, 3 => -1f, 4 => 1f, _ => (i - 10) / 6f };
            EtcEncoder.EacR11(f, 1, true, blk, q);
            for (int i = 0; i < 16; i++) s[i] = (short)(i switch { 0 => -32767, 1 => 32767, 2 => 0, _ => (int)MathF.Round(Math.Clamp(f[i], -1f, 1f) * 32767f) });
            EtcEncoder.EacR11Signed(s, 1, blk2, q);
            Assert.True(blk.SequenceEqual(blk2), $"{q} signed floats");
            EtcEncoder.EacR11(f, 1, false, blk, q);
            for (int i = 0; i < 16; i++) u[i] = (ushort)(i switch { 0 => 0, 1 => 65535, 2 => 0, _ => (int)MathF.Round(Math.Clamp(f[i], 0f, 1f) * 65535f) });
            EtcEncoder.EacR11(u, 1, blk2, q);
            Assert.True(blk.SequenceEqual(blk2), $"{q} unsigned floats");
        }
    }

    [Fact]
    public void Deterministic()
    {
        var img0 = LoadRgba(Fixture("etc_encode/kodim23_crop.png"), out int w, out int h);
        foreach (var f in Enum.GetValues<Fmt>())
        {
            var img = f is Fmt.Etc2A1 or Fmt.Etc2Rgba ? WithAlpha(img0, w, h, f == Fmt.Etc2A1) : img0;
            foreach (bool perceptual in new[] { false, true })
            {
                var a = Encode(img, w, h, f, BcQuality.Normal, perceptual, threads: 1);
                var b = Encode(img, w, h, f, BcQuality.Normal, perceptual, threads: 8);
                var c = Encode(img, w, h, f, BcQuality.Normal, perceptual, threads: 3);
                Assert.Equal(a, b);
                Assert.Equal(a, c);
            }
        }
    }

    /// <summary>0 individual, 1 differential, 2 T, 3 H, 4 planar.</summary>
    static int ModeOf(ReadOnlySpan<byte> b)
    {
        uint hi = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        if ((hi & 2) == 0) return 0;
        static int S3(uint v) => (int)((v ^ 4) - 4);
        if ((uint)((hi >> 27 & 31) + S3(hi >> 24 & 7)) > 31) return 2;
        if ((uint)((hi >> 19 & 31) + S3(hi >> 16 & 7)) > 31) return 3;
        if ((uint)((hi >> 11 & 31) + S3(hi >> 8 & 7)) > 31) return 4;
        return 1;
    }

    /// <summary>Blocks of several kinds: noise, two colours, gradients, near-solid, with random alpha (some transparent).</summary>
    static void RandomBlock(Random rng, Span<byte> px, int n)
    {
        int kind = n % 6;
        int r0 = rng.Next(256), g0 = rng.Next(256), b0 = rng.Next(256), r1 = rng.Next(256), g1 = rng.Next(256), b1 = rng.Next(256);
        int amode = rng.Next(4);
        for (int i = 0; i < 16; i++)
        {
            int x = i & 3, y = i >> 2;
            int r, g, b;
            switch (kind)
            {
                case 0: r = rng.Next(256); g = rng.Next(256); b = rng.Next(256); break;
                case 1: bool s = rng.Next(2) == 0; r = s ? r0 : r1; g = s ? g0 : g1; b = s ? b0 : b1; break;
                case 2: r = r0 + (r1 - r0) * x / 3; g = g0 + (g1 - g0) * y / 3; b = b0 + (b1 - b0) * (x + y) / 6; break;
                case 3: r = r0 + rng.Next(-3, 4); g = g0 + rng.Next(-3, 4); b = b0 + rng.Next(-3, 4); break;
                case 4: int l = rng.Next(-40, 41); r = r0 + l; g = g0 + l; b = b0 + l; break;
                default: r = r0; g = g0; b = b0; break;
            }
            px[i * 4] = (byte)Math.Clamp(r, 0, 255); px[i * 4 + 1] = (byte)Math.Clamp(g, 0, 255); px[i * 4 + 2] = (byte)Math.Clamp(b, 0, 255);
            px[i * 4 + 3] = amode switch { 0 => 255, 1 => (byte)rng.Next(256), 2 => (byte)(x < 2 ? 0 : 255), _ => 0 };
        }
    }

    // ------------------------------------------------------------------ benchmark (not a test)

    /// <summary>PSNR and speed over a directory of PNGs; runs only when PIXCODEC_ETC_BENCH names the directory.
    /// PIXCODEC_ETC_FORMATS / PIXCODEC_ETC_QUALITY restrict the formats / levels.</summary>
    [Fact]
    public void Bench()
    {
        var dir = Environment.GetEnvironmentVariable("PIXCODEC_ETC_BENCH");
        if (string.IsNullOrEmpty(dir)) return;
        var fmts = (Environment.GetEnvironmentVariable("PIXCODEC_ETC_FORMATS") ?? "Etc1,Etc2Rgb,Etc2A1,Etc2Rgba,R11,Rg11,R11s,Rg11s")
            .Split(',').Select(Enum.Parse<Fmt>).ToArray();
        var qs = (Environment.GetEnvironmentVariable("PIXCODEC_ETC_QUALITY") ?? "Fast,Normal,High")
            .Split(',').Select(Enum.Parse<BcQuality>).ToArray();
        int threads = int.TryParse(Environment.GetEnvironmentVariable("PIXCODEC_ETC_THREADS"), out var t) ? t : 1;
        var files = Directory.GetFiles(dir, "*.png").OrderBy(x => x).ToArray();
        var images = files.Select(p => (name: Path.GetFileNameWithoutExtension(p), img: LoadRgba(p, out int w, out int h), w, h)).ToArray();
        var lines = new List<string>();
        foreach (var f in fmts)
            foreach (var q in qs)
            {
                double sumC = 0, sumA = 0, secs = 0; long pixels = 0;
                var per = new List<string>();
                foreach (var (name, img0, w, h) in images)
                {
                    var img = f is Fmt.Etc2A1 or Fmt.Etc2Rgba ? WithAlpha(img0, w, h, f == Fmt.Etc2A1) : img0;
                    Encode(img, w, h, f, q, threads: threads);   // warm up
                    // CPU time, not wall time: the machine may be shared
                    var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
                    var data = Encode(img, w, h, f, q, threads: threads);
                    secs += (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalSeconds;
                    pixels += (long)w * h;
                    var (pc, pa) = Psnr(img, w, h, f, data);
                    sumC += pc; sumA += pa;
                    per.Add($"{name}={pc:F3}" + (f == Fmt.Etc2Rgba ? $"/{pa:F3}" : ""));
                }
                string line = $"{f} {q}: mean {sumC / images.Length:F3} dB" + (f == Fmt.Etc2Rgba ? $" alpha {sumA / images.Length:F3}" : "")
                              + $", {pixels / secs / 1e6:F2} MP per CPU second  " + string.Join(" ", per);
                _out.WriteLine(line);
                lines.Add(line);
            }
        // decoders: blocks of the first image, decoded repeatedly into a block buffer
        foreach (var f in fmts)
        {
            var (_, img0, w, h) = images[0];
            var data = Encode(img0, w, h, f, BcQuality.Fast, threads: 8);
            int bpb = BlockBytes(f), nb = data.Length / bpb;
            var blk = new byte[64]; var u = new ushort[32]; var s = new short[32];
            long blocks = 0;
            var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
            for (int rep = 0; rep < 200; rep++)
                for (int k = 0; k < nb; k++, blocks++)
                {
                    var b = data.AsSpan(k * bpb, bpb);
                    switch (f)
                    {
                        case Fmt.Etc1: EtcBlocks.Etc1(b, blk); break;
                        case Fmt.Etc2Rgb: EtcBlocks.Etc2Rgb(b, blk); break;
                        case Fmt.Etc2A1: EtcBlocks.Etc2PunchThrough(b, blk); break;
                        case Fmt.Etc2Rgba: EtcBlocks.Etc2Rgba(b, blk); break;
                        case Fmt.R11: EtcBlocks.EacR11(b, u, 1); break;
                        case Fmt.Rg11: EtcBlocks.EacRg11(b, u); break;
                        case Fmt.R11s: EtcBlocks.EacR11Signed(b, s, 1); break;
                        case Fmt.Rg11s: EtcBlocks.EacRg11Signed(b, s); break;
                    }
                }
            double secs = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalSeconds;
            string line = $"{f} decode: {blocks * 16 / secs / 1e6:F1} MP per CPU second";
            _out.WriteLine(line);
            lines.Add(line);
        }
        File.AppendAllLines(Path.Combine(dir, "bench.txt"), lines);
    }

    /// <summary>An alpha channel for the alpha formats, the same as the oracle script makes: a ramp with a hard edge
    /// and a transparent disc (punch-through: alpha 0 or 255 from the same pattern).</summary>
    internal static byte[] WithAlpha(byte[] img, int w, int h, bool binary)
    {
        var o = (byte[])img.Clone();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double dx = x - w * 0.3, dy = y - h * 0.6;
                int a = dx * dx + dy * dy < (h * 0.2) * (h * 0.2) ? 0 : x < w / 2 ? x * 255 / (w / 2) : 255 - (y * 7 % 64);
                if (binary) a = a >= 128 ? 255 : 0;
                o[(y * w + x) * 4 + 3] = (byte)a;
            }
        return o;
    }
}
