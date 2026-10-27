using Aardvark.ImageCodecs;
using Aardvark.ImageCodecs.JpegXl;
using Xunit;
using Xunit.Abstractions;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// JPEG XL encoder: lossless output decoded bit exactly by our decoder and by libjxl (imagecodecs, through
/// tests/oracle/decode_to_ref.py; skipped without it), option validation.
/// </summary>
public class JxlEncodeTests(ITestOutputHelper output)
{
    /// <summary>
    /// Synthetic content: 0 smooth gradients with noise (photo like), 1 few colours (palette), 2 constant, 3 noise,
    /// 4 an 8 bit image scaled up (sparse values).
    /// </summary>
    internal static RawImage Synthetic(int w, int h, ColorModel color, SampleType type, int kind, int seed = 1)
    {
        int nc = RawImage.ChannelCount(color);
        var rng = new Random(seed);
        var v = new double[w * h * nc];
        var pal = new double[7][];
        for (int k = 0; k < pal.Length; k++) { pal[k] = new double[nc]; for (int c = 0; c < nc; c++) pal[k][c] = rng.Next(256) / 255.0; }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < nc; c++)
                {
                    double s;
                    switch (kind)
                    {
                        case 1: s = pal[(x / 5 + y / 7 + (x * y) % 3) % pal.Length][c]; break;
                        case 2: s = 0.25 + 0.1 * c; break;
                        case 3: s = rng.NextDouble(); break;
                        case 4: s = Math.Round(255 * (0.5 + 0.4 * Math.Sin(x * 0.05 + c) * Math.Cos(y * 0.07))) / 255; break;
                        default:
                            s = 0.5 + 0.35 * Math.Sin(x * 0.031 + c * 1.3) * Math.Cos(y * 0.047 - c) + 0.1 * ((x / 16 + y / 16) & 1) + 0.04 * rng.NextDouble();
                            break;
                    }
                    bool alpha = (color is ColorModel.GrayAlpha or ColorModel.Rgba) && c == nc - 1;
                    if (alpha) s = kind == 2 ? 1 : Math.Clamp((x + y) / (double)(w + h) + 0.05 * Math.Sin(x), 0, 1);
                    v[(y * w + x) * nc + c] = Math.Clamp(s, 0, 1);
                }
        Array data = type switch
        {
            SampleType.UInt8 => v.Select(d => (byte)Math.Round(d * 255)).ToArray(),
            SampleType.UInt16 => v.Select(d => kind == 4 ? (ushort)(Math.Round(d * 255) * 257) : (ushort)Math.Round(d * 65535)).ToArray(),
            SampleType.Float16 => v.Select(d => (Half)(d * 1.5 - 0.1)).ToArray(),
            _ => v.Select(d => (float)(d * 3 - 0.5)).ToArray(),
        };
        return new RawImage(w, h, color, data, nc);
    }

    static readonly ColorModel[] Colors = { ColorModel.Gray, ColorModel.GrayAlpha, ColorModel.Rgb, ColorModel.Rgba };
    static readonly SampleType[] Types = { SampleType.UInt8, SampleType.UInt16, SampleType.Float16, SampleType.Float32 };

    public static IEnumerable<object[]> LosslessCases()
    {
        foreach (var c in Colors)
            foreach (var t in Types)
                foreach (int e in new[] { 1, 2, 3, 4, 5, 7, 9 })
                    yield return new object[] { c, t, e };
    }

    public static IEnumerable<object[]> OracleCases()
    {
        foreach (var c in Colors)
            foreach (var t in Types)
                foreach (int e in new[] { 1, 3, 7 })
                    yield return new object[] { c, t, e };
    }

    internal static void AssertExact(RawImage expected, RawImage actual)
    {
        Assert.Equal((expected.Width, expected.Height, expected.Channels, expected.SampleType, expected.Color),
            (actual.Width, actual.Height, actual.Channels, actual.SampleType, actual.Color));
        switch (expected.SampleType)
        {
            case SampleType.UInt8: Assert.Equal((byte[])expected.Data, (byte[])actual.Data); break;
            case SampleType.UInt16: Assert.Equal((ushort[])expected.Data, (ushort[])actual.Data); break;
            case SampleType.Float16:
                Assert.Equal(((Half[])expected.Data).Select(BitConverter.HalfToUInt16Bits), ((Half[])actual.Data).Select(BitConverter.HalfToUInt16Bits));
                break;
            default:
                Assert.Equal(((float[])expected.Data).Select(BitConverter.SingleToInt32Bits), ((float[])actual.Data).Select(BitConverter.SingleToInt32Bits));
                break;
        }
    }

    /// <summary>libjxl's decode of our file (stored samples), or null when the oracle is not available.</summary>
    internal static RawImage? Libjxl(byte[] encoded)
    {
        if (!PythonAvailable) return null;
        var tmp = Path.Combine(FixtureDir, "_tmp");
        Directory.CreateDirectory(tmp);
        var f = Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".jxl");
        try
        {
            File.WriteAllBytes(f, encoded);
            var (exit, text) = RunPython($"decode_to_ref.py \"{f}\" \"{f}.ref\"");
            if (TestUtil.CannotVerify(exit, text)) return null;
            Assert.True(exit == 0, "libjxl decode failed: " + text);
            return LoadRef(f + ".ref");
        }
        finally
        {
            File.Delete(f); File.Delete(f + ".ref");
        }
    }

    internal static void AssertSameSamples(RawImage expected, RawImage reference)
    {
        Assert.Equal((expected.Width, expected.Height, expected.Channels, expected.SampleType), (reference.Width, reference.Height, reference.Channels, reference.SampleType));
        switch (expected.SampleType)
        {
            case SampleType.UInt8: Assert.Equal((byte[])expected.Data, (byte[])reference.Data); break;
            case SampleType.UInt16: Assert.Equal((ushort[])expected.Data, (ushort[])reference.Data); break;
            case SampleType.Float16:
                Assert.Equal(((Half[])expected.Data).Select(BitConverter.HalfToUInt16Bits), ((Half[])reference.Data).Select(BitConverter.HalfToUInt16Bits));
                break;
            default:
                Assert.Equal(((float[])expected.Data).Select(BitConverter.SingleToInt32Bits), ((float[])reference.Data).Select(BitConverter.SingleToInt32Bits));
                break;
        }
    }

    // ------------------------------------------------------------------ lossless

    [Theory, MemberData(nameof(LosslessCases))]
    public void LosslessRoundTripsExactly(ColorModel color, SampleType type, int effort)
    {
        // 300 x 170 spans two groups horizontally
        foreach (int kind in new[] { 0, 1 })
        {
            var img = Synthetic(300, 170, color, type, kind, seed: effort);
            var enc = Jxl.Encode(img, new JxlEncodeOptions { Lossless = true, Effort = effort });
            AssertExact(img, Jxl.Decode(enc));
            var info = Jxl.GetInfo(enc);
            Assert.Equal((img.Width, img.Height, img.Channels, img.SampleType), (info.Width, info.Height, info.Channels, info.SampleType));
        }
    }

    [Theory, MemberData(nameof(OracleCases))]
    public void LosslessDecodesExactlyWithLibjxl(ColorModel color, SampleType type, int effort)
    {
        var img = Synthetic(300, 170, color, type, 0, seed: 3);
        var enc = Jxl.Encode(img, new JxlEncodeOptions { Lossless = true, Effort = effort });
        var r = Libjxl(enc);
        if (r == null) { output.WriteLine("imagecodecs (libjxl) not available: skipped"); return; }
        AssertSameSamples(img, r);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 300)]
    [InlineData(300, 1)]
    [InlineData(257, 257)]
    [InlineData(513, 7)]
    [InlineData(64, 600)]
    public void LosslessOddSizes(int w, int h)
    {
        foreach (int effort in new[] { 1, 3, 7 })
            foreach (int kind in new[] { 0, 2, 3 })
            {
                var img = Synthetic(w, h, ColorModel.Rgba, SampleType.UInt8, kind, seed: w + h);
                var enc = Jxl.Encode(img, new JxlEncodeOptions { Lossless = true, Effort = effort });
                AssertExact(img, Jxl.Decode(enc));
                if (effort == 7 && kind == 0 && Libjxl(enc) is { } r) AssertSameSamples(img, r);
            }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void LosslessContentKinds(int kind)
    {
        // palettes, constant images, noise and sparse (scaled up) values, also through libjxl
        foreach (var type in new[] { SampleType.UInt8, SampleType.UInt16 })
            foreach (int effort in new[] { 1, 2, 7, 8 })
            {
                var img = Synthetic(270, 260, ColorModel.Rgb, type, kind, seed: kind);
                var enc = Jxl.Encode(img, new JxlEncodeOptions { Lossless = true, Effort = effort });
                AssertExact(img, Jxl.Decode(enc));
                if (effort == 8 && Libjxl(enc) is { } r) AssertSameSamples(img, r);
            }
    }

    [Fact]
    public void LosslessIsSingleThreadedTheSame()
    {
        var img = Synthetic(600, 300, ColorModel.Rgb, SampleType.UInt8, 0);
        foreach (int effort in new[] { 1, 3, 7 })
        {
            var a = Jxl.Encode(img, new JxlEncodeOptions { Lossless = true, Effort = effort });
            var b = Jxl.Encode(img, new JxlEncodeOptions { Lossless = true, Effort = effort, Multithreaded = false });
            Assert.Equal(a, b);
        }
    }

    [Fact]
    public void LosslessEffortTradesSize()
    {
        var img = Synthetic(400, 300, ColorModel.Rgb, SampleType.UInt8, 0);
        var sizes = new[] { 1, 3, 7, 9 }.Select(e => Jxl.Encode(img, new JxlEncodeOptions { Lossless = true, Effort = e }).Length).ToArray();
        output.WriteLine(string.Join(" ", sizes));
        Assert.True(sizes[2] < sizes[0], "effort 7 should be smaller than effort 1");
        Assert.True(sizes[3] <= sizes[2] * 1.01, "effort 9 should not be larger than effort 7");
        // well below the raw size (360 KB)
        Assert.True(sizes[2] < 400 * 300 * 3 / 2);
    }

    [Fact]
    public void QualityHundredAndDistanceZeroAreLossless()
    {
        var img = Synthetic(80, 60, ColorModel.Rgb, SampleType.UInt8, 0);
        AssertExact(img, Jxl.Decode(Jxl.Encode(img, new JxlEncodeOptions { Quality = 100 })));
        AssertExact(img, Jxl.Decode(Jxl.Encode(img, new JxlEncodeOptions { Distance = 0 })));
        Assert.Equal(0, new JxlEncodeOptions { Quality = 100 }.EffectiveDistance);
        Assert.Equal(1.0f, new JxlEncodeOptions { Quality = 90 }.EffectiveDistance, 4);
        Assert.Equal(6.4f, new JxlEncodeOptions { Quality = 30 }.EffectiveDistance, 4);
        Assert.Equal(25f, new JxlEncodeOptions { Quality = 0 }.EffectiveDistance, 4);
    }

    [Fact]
    public void StreamAndArrayOverloadsAgree()
    {
        var img = Synthetic(50, 40, ColorModel.GrayAlpha, SampleType.UInt16, 0);
        var opt = new JxlEncodeOptions { Lossless = true };
        var ms = new MemoryStream();
        Jxl.Encode(img, ms, opt);
        Assert.Equal(Jxl.Encode(img, opt), ms.ToArray());
        Assert.Equal(ImageFormat.Jxl, ImageCodec.DetectFormat(ms.ToArray()));
    }

    // ------------------------------------------------------------------ validation

    [Fact]
    public void OptionsAreValidated()
    {
        var img = Synthetic(8, 8, ColorModel.Rgb, SampleType.UInt8, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => Jxl.Encode(img, new JxlEncodeOptions { Effort = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Jxl.Encode(img, new JxlEncodeOptions { Effort = 10 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Jxl.Encode(img, new JxlEncodeOptions { Distance = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Jxl.Encode(img, new JxlEncodeOptions { Distance = 25.5f }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Jxl.Encode(img, new JxlEncodeOptions { Distance = float.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Jxl.Encode(img, new JxlEncodeOptions { Quality = 101 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Jxl.Encode(img, new JxlEncodeOptions { Quality = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Jxl.Encode(img, new JxlEncodeOptions { Quality = float.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Jxl.Encode(img, new JxlEncodeOptions { IntensityTarget = -5 }));
        Assert.Throws<ArgumentNullException>(() => Jxl.Encode(null!, new JxlEncodeOptions()));
        Assert.Throws<ArgumentNullException>(() => Jxl.Encode(img, (Stream)null!));
    }

    [Fact]
    public void UnsupportedInputIsRejected()
    {
        var lossless = new JxlEncodeOptions { Lossless = true };
        Assert.Throws<ImageNotSupportedException>(() => Jxl.Encode(RawImage.Create(4, 4, ColorModel.Rgb, SampleType.Int16), lossless));
        Assert.Throws<ImageNotSupportedException>(() => Jxl.Encode(RawImage.Create(4, 4, ColorModel.Rgb, SampleType.Float64), lossless));
        Assert.Throws<ImageNotSupportedException>(() => Jxl.Encode(new RawImage(2, 2, ColorModel.Other, new byte[2 * 2 * 5], 5), lossless));
        // a channel count without colour meaning maps to the usual models
        var two = new RawImage(3, 3, ColorModel.Other, Enumerable.Range(0, 18).Select(i => (byte)(i * 13)).ToArray(), 2);
        var back = Jxl.Decode(Jxl.Encode(two, lossless));
        Assert.Equal(ColorModel.GrayAlpha, back.Color);
        Assert.Equal((byte[])two.Data, (byte[])back.Data);
    }

    // ------------------------------------------------------------------ lossy

    /// <summary>PSNR of the colour channels (alpha left out) on a 0..1 scale.</summary>
    internal static double ColourPsnr(RawImage a, RawImage b)
    {
        Assert.Equal((a.Width, a.Height, a.Channels), (b.Width, b.Height, b.Channels));
        var x = ToDoubles(a); var y = ToDoubles(b);
        double sa = a.SampleType switch { SampleType.UInt8 => 255, SampleType.UInt16 => 65535, _ => 1 };
        double sb = b.SampleType switch { SampleType.UInt8 => 255, SampleType.UInt16 => 65535, _ => 1 };
        int nc = a.Channels;
        bool alpha = a.Color is ColorModel.GrayAlpha or ColorModel.Rgba;
        double se = 0; long n = 0;
        for (int i = 0; i < x.Length; i++)
        {
            if (alpha && i % nc == nc - 1) continue;
            double d = Math.Clamp(x[i] / sa, 0, 1) - Math.Clamp(y[i] / sb, 0, 1);
            se += d * d; n++;
        }
        return se == 0 ? 99 : 10 * Math.Log10(n / se);
    }

    /// <summary>
    /// Quality against the source at fixed thresholds. libjxl reaches 38.0 / 36.8 / 34.9 dB (cjxl -e 3) and
    /// 38.4 / 37.8 / 36.6 dB (-e 7) on this content (smooth gradients with 4 % noise) at distance 1 / 2 / 4; the
    /// thresholds allow 2 dB less than its effort 3.
    /// </summary>
    [Theory]
    [InlineData(1.0, 36.0)]
    [InlineData(2.0, 34.8)]
    [InlineData(4.0, 32.9)]
    public void LossyQualityAgainstSource(double distance, double minPsnr)
    {
        var img = Synthetic(300, 170, ColorModel.Rgb, SampleType.UInt8, 0, 1);
        foreach (int effort in new[] { 3, 7 })
        {
            var enc = Jxl.Encode(img, new JxlEncodeOptions { Distance = (float)distance, Effort = effort });
            var dec = Jxl.Decode(enc);
            double psnr = ColourPsnr(img, dec);
            output.WriteLine($"d {distance} e{effort}: {enc.Length} bytes, {psnr:F2} dB");
            Assert.True(psnr >= minPsnr, $"PSNR {psnr:F2} below {minPsnr}");
            Assert.True(enc.Length < img.Width * img.Height * 3 / 4, "lossy output should be far below the raw size");
            if (Libjxl(enc) is { } r)
            {
                // libjxl decodes the same stream to (nearly) the same pixels
                Assert.True(MaxDiff(dec, r) <= 1, $"libjxl differs by {MaxDiff(dec, r)}");
            }
        }
    }

    [Fact]
    public void LossyDistanceTradesSizeForQuality()
    {
        var img = Synthetic(256, 256, ColorModel.Rgb, SampleType.UInt8, 4, 2);
        double lastPsnr = double.MaxValue;
        int lastSize = int.MaxValue;
        foreach (float d in new[] { 0.5f, 1, 2, 4, 8 })
        {
            var enc = Jxl.Encode(img, new JxlEncodeOptions { Distance = d });
            double p = ColourPsnr(img, Jxl.Decode(enc));
            output.WriteLine($"d {d}: {enc.Length} bytes {p:F2} dB");
            Assert.True(enc.Length < lastSize && p < lastPsnr + 0.2);
            lastSize = enc.Length; lastPsnr = p;
        }
        // the quality knob maps like cjxl -q
        Assert.Equal(Jxl.Encode(img, new JxlEncodeOptions { Distance = 1 }), Jxl.Encode(img, new JxlEncodeOptions { Quality = 90 }));
    }

    public static IEnumerable<object[]> LossyCases()
    {
        foreach (var c in Colors)
            foreach (var t in Types)
                foreach (int e in new[] { 1, 3, 5, 7, 9 })
                    yield return new object[] { c, t, e };
    }

    /// <summary>
    /// Every colour model, sample type and effort: decodes with the sample type and colour model of the source, alpha
    /// exactly, colour within 1.6 % RMS (36 dB: libjxl reaches 38.4 dB at distance 1 on this content, see
    /// LossyQualityAgainstSource).
    /// </summary>
    [Theory, MemberData(nameof(LossyCases))]
    public void LossyRoundTrips(ColorModel color, SampleType type, int effort)
    {
        var img = Synthetic(300, 170, color, type, 0, effort);
        var enc = Jxl.Encode(img, new JxlEncodeOptions { Distance = 1, Effort = effort });
        var dec = Jxl.Decode(enc);
        Assert.Equal((img.Width, img.Height, img.Channels, img.SampleType, img.Color), (dec.Width, dec.Height, dec.Channels, dec.SampleType, dec.Color));
        // float samples of this content range over -0.5 .. 2.5: compare on that scale
        double range = type is SampleType.Float16 ? 1.5 : type is SampleType.Float32 ? 3 : 1;
        var x = ToDoubles(img); var y = ToDoubles(dec);
        double scale = type switch { SampleType.UInt8 => 255, SampleType.UInt16 => 65535, _ => 1 };
        int nc = img.Channels;
        bool alpha = color is ColorModel.GrayAlpha or ColorModel.Rgba;
        double se = 0; long n = 0;
        for (int i = 0; i < x.Length; i++)
        {
            if (alpha && i % nc == nc - 1) { Assert.Equal(x[i], y[i]); continue; }
            double d = (x[i] - y[i]) / scale / range;
            se += d * d; n++;
        }
        double rms = Math.Sqrt(se / n);
        output.WriteLine($"{enc.Length} bytes, rms {rms:F4}");
        Assert.True(rms < 0.016, $"rms error {rms:F4}");
        if (effort == 7 && Libjxl(enc) is { } r) Assert.Equal((dec.Width, dec.Height, dec.Channels), (r.Width, r.Height, r.Channels));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    public void LossyTransformsReconstruct(int strategy)
    {
        // every transform the encoder can choose, forced everywhere, at a very small distance: near exact (on smooth
        // content; the quantization of the large transforms is coarse at the highest frequencies by design)
        var img = Synthetic(200, 136, ColorModel.Rgb, SampleType.UInt8, 4, 5);
        var enc = Jxl.Encode(img, new JxlEncodeOptions { Distance = 0.1f, ForceStrategy = strategy });
        double p = ColourPsnr(img, Jxl.Decode(enc));
        output.WriteLine($"strategy {strategy}: {enc.Length} bytes {p:F2} dB");
        Assert.True(p > 44, $"PSNR {p:F2}");
        if (Libjxl(enc) is { } r) Assert.True(ColourPsnr(img, r) > 44);
    }

    /// <summary>
    /// The 128 and 256 sample transforms (I.2.6), forced everywhere on an image of two by two groups and more, so that
    /// varblocks end at group and LF group boundaries and the image edge falls back to DCT8: near exact, and libjxl's
    /// decode agrees with ours.
    /// </summary>
    [Theory]
    [InlineData(JxlAcStrategy.Dct64x128)]
    [InlineData(JxlAcStrategy.Dct128x64)]
    [InlineData(JxlAcStrategy.Dct128x128)]
    [InlineData(JxlAcStrategy.Dct128x256)]
    [InlineData(JxlAcStrategy.Dct256x128)]
    [InlineData(JxlAcStrategy.Dct256x256)]
    public void LossyLargeTransformsReconstruct(int strategy)
    {
        var img = Synthetic(600, 560, ColorModel.Rgb, SampleType.UInt8, 4, 5);
        var enc = Jxl.Encode(img, new JxlEncodeOptions { Distance = 0.1f, ForceStrategy = strategy });
        var ours = Jxl.Decode(enc);
        double p = ColourPsnr(img, ours);
        output.WriteLine($"strategy {strategy}: {enc.Length} bytes {p:F2} dB");
        Assert.True(p > 44, $"PSNR {p:F2}");
        if (Libjxl(enc) is not { } r) return;
        Assert.True(ColourPsnr(img, r) > 44);
        var a = (byte[])ours.Data; var b = (byte[])r.Data;
        Assert.Equal(a.Length, b.Length);
        int m = 0;
        for (int i = 0; i < a.Length; i++) m = Math.Max(m, Math.Abs(a[i] - b[i]));
        output.WriteLine($"largest difference to libjxl {m}");
        Assert.True(m <= 2, $"largest difference to libjxl {m}");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 300)]
    [InlineData(300, 7)]
    [InlineData(260, 260)]
    [InlineData(2100, 20)]
    public void LossyOddSizes(int w, int h)
    {
        foreach (int effort in new[] { 1, 7 })
        {
            var img = Synthetic(w, h, ColorModel.Rgba, SampleType.UInt8, 0, w * h);
            var enc = Jxl.Encode(img, new JxlEncodeOptions { Distance = 1, Effort = effort });
            var dec = Jxl.Decode(enc);
            Assert.Equal((w, h, 4), (dec.Width, dec.Height, dec.Channels));
            if (w * h > 1) Assert.True(ColourPsnr(img, dec) > 30);
            if (effort == 7 && Libjxl(enc) is { } r) Assert.Equal((w, h, 4), (r.Width, r.Height, r.Channels));
        }
    }

    [Fact]
    public void LossyIsBetterThanJpegAtEqualSize()
    {
        // at about the same file size the JPEG XL output has the higher PSNR (smooth content)
        var img = Synthetic(320, 240, ColorModel.Rgb, SampleType.UInt8, 4, 9);
        var ms = new MemoryStream();
        Jpeg.Encode(img, ms, new JpegEncodeOptions { Quality = 85 });
        var jpg = ms.ToArray();
        double pj = ColourPsnr(img, Jpeg.Decode(jpg));
        // search the distance giving a file no larger than the JPEG
        float d = 1;
        byte[] enc = Jxl.Encode(img, new JxlEncodeOptions { Distance = d });
        while (enc.Length > jpg.Length && d < 20) { d *= 1.25f; enc = Jxl.Encode(img, new JxlEncodeOptions { Distance = d }); }
        double px = ColourPsnr(img, Jxl.Decode(enc));
        output.WriteLine($"JPEG q85 {jpg.Length} bytes {pj:F2} dB, JPEG XL d{d:F2} {enc.Length} bytes {px:F2} dB");
        Assert.True(enc.Length <= jpg.Length && px > pj, "JPEG XL should beat JPEG at equal size");
    }
}
