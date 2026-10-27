using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Aardvark.ImageCodecs.JpegXl;
using Xunit;
using Xunit.Abstractions;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// HDR gain maps (ISO 21496-1:2025, Ultra HDR v1.1) in JPEG and JPEG XL, and the Multi-Picture Format (CIPA DC-007)
/// they and MPO stereo files use. libultrahdr's ultrahdr_app is the oracle (<see cref="UltraHdrOracle"/>), ExifTool checks MPF.
/// </summary>
public class GainMapTests(ITestOutputHelper output)
{
    // ------------------------------------------------------------------ test images

    /// <summary>A linear HDR test image (1.0 = SDR white, peak about 8): gradients, a checker pattern, saturated colours, dark areas.</summary>
    internal static RawImage SyntheticHdr(int w, int h, int seed = 1)
    {
        var f = new float[w * h * 3];
        var rnd = new Random(seed);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double u = x / (double)(w - 1), v = y / (double)(h - 1);
                double r = 8 * u * u, g = 3 * v, b = 0.05 + 0.6 * (((x / 8) + (y / 8)) % 2);
                if (x > w / 2 && y > h / 2) { r = 0.002 * (x % 7); g *= 0.01; b = 0.001; }
                double n = 1 + 0.02 * (rnd.NextDouble() - 0.5);
                f[(y * w + x) * 3] = (float)(r * n); f[(y * w + x) * 3 + 1] = (float)(g * n); f[(y * w + x) * 3 + 2] = (float)(b * n);
            }
        return new RawImage(w, h, ColorModel.Rgb, f);
    }

    /// <summary>An 8 bit sRGB rendition of <paramref name="hdr"/> (per channel x / (1 + x)).</summary>
    internal static RawImage SyntheticSdr(RawImage hdr)
    {
        var f = (float[])hdr.Data;
        var b = new byte[f.Length];
        for (int i = 0; i < f.Length; i++)
        {
            double x = Math.Max(f[i], 0), s = x / (1 + x);
            double e = s <= 0.0031308 ? s * 12.92 : 1.055 * Math.Pow(s, 1 / 2.4) - 0.055;
            b[i] = (byte)Math.Clamp(Math.Floor(e * 255 + 0.5), 0, 255);
        }
        return new RawImage(hdr.Width, hdr.Height, ColorModel.Rgb, b);
    }

    /// <summary>Largest and mean difference in stops (log2 ratio, with an offset of 1/64 for dark values) of two linear RGB images.</summary>
    internal static (double Max, double Mean) StopError(RawImage a, RawImage b)
    {
        var x = (float[])a.Data; var y = (float[])b.Data;
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        Assert.Equal(x.Length, y.Length);
        double max = 0, sum = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double d = Math.Abs(Math.Log2((Math.Max(x[i], 0) + 1 / 64.0) / (Math.Max(y[i], 0) + 1 / 64.0)));
            max = Math.Max(max, d); sum += d;
        }
        return (max, sum / x.Length);
    }

    void AssertClose(RawImage expected, RawImage actual, double maxStops, double meanStops, string what)
    {
        var e = StopError(expected, actual);
        output.WriteLine($"{what}: max {e.Max:F4} mean {e.Mean:F5} stops");
        Assert.True(e.Max <= maxStops && e.Mean <= meanStops, $"{what}: max {e.Max:F4} (<= {maxStops}) mean {e.Mean:F5} (<= {meanStops}) stops");
    }

    static byte[] UltraHdr(RawImage hdr, UltraHdrEncodeOptions? o = null)
    {
        var ms = new MemoryStream();
        Jpeg.EncodeUltraHdr(SyntheticSdr(hdr), hdr, ms, o);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ ISO 21496-1 metadata (C.2)

    [Fact]
    public void IsoMetadataRoundTrips()
    {
        var m = new GainMapMetadata
        {
            WriterVersion = 3, IsMultichannel = true, UseBaseColourSpace = false, BaseHdrHeadroom = 0.25, AlternateHdrHeadroom = 3.75,
            GainMapMin = new[] { -0.5, -0.25, -1 / 3.0 }, GainMapMax = new[] { 3.5, 2.75, 1e-3 }, Gamma = new[] { 1, 2.2, 0.5 },
            BaseOffset = new[] { 1 / 64.0, 0, 1e-7 }, AlternateOffset = new[] { 1 / 64.0, 0.125, -0.01 },
        };
        var b = m.ToIso21496();
        Assert.Equal(5 + 16 + 3 * 40, b.Length);
        Assert.Equal(0x80, b[4]);   // is_multichannel, not use_base_colour_space
        var r = GainMapMetadata.FromIso21496(b)!;
        Assert.Equal((0, 3, true, false), (r.MinimumVersion, r.WriterVersion, r.IsMultichannel, r.UseBaseColourSpace));
        Assert.Equal(0.25, r.BaseHdrHeadroom, 9); Assert.Equal(3.75, r.AlternateHdrHeadroom, 9);
        for (int c = 0; c < 3; c++)
        {
            Assert.Equal(m.GainMapMin[c], r.GainMapMin[c], 8); Assert.Equal(m.GainMapMax[c], r.GainMapMax[c], 8);
            Assert.Equal(m.Gamma[c], r.Gamma[c], 8); Assert.Equal(m.BaseOffset[c], r.BaseOffset[c], 8);
            Assert.Equal(m.AlternateOffset[c], r.AlternateOffset[c], 8);
        }
        // one channel: the same values for R, G, B; data after the structure is ignored (C.2.1)
        var one = new GainMapMetadata { AlternateHdrHeadroom = 2, GainMapMax = new[] { 2.0, 2, 2 } };
        var ob = one.ToIso21496().Concat(new byte[] { 1, 2, 3 }).ToArray();
        Assert.Equal(0x40, ob[4]);
        var o = GainMapMetadata.FromIso21496(ob)!;
        Assert.False(o.IsMultichannel);
        Assert.Equal(new[] { 2.0, 2, 2 }, o.GainMapMax);
        Assert.Equal(1 / 64.0, o.AlternateOffset[2]);
        // reserved bits are not interpreted
        ob[4] |= 0x3F;
        Assert.NotNull(GainMapMetadata.FromIso21496(ob));
    }

    /// <summary>The gain map image's payload of a file ultrahdr_app 1.4.0 wrote for a synthetic image (no third-party content).</summary>
    [Fact]
    public void IsoMetadataReadsLibultrahdrPayload()
    {
        var b = Convert.FromHexString("000000004000000000000000010059f54100100000ffe3bc89200000000059eded0020000000000001000000010000004d2de544770000004d2de54477");
        var m = GainMapMetadata.FromIso21496(b)!;
        Assert.False(m.IsMultichannel);
        Assert.True(m.UseBaseColourSpace);
        Assert.Equal(0, m.BaseHdrHeadroom);
        Assert.Equal(5895489 / 1048576.0, m.AlternateHdrHeadroom, 12);
        Assert.Equal(-1852279 / 536870912.0, m.GainMapMin[0], 12);
        Assert.Equal(5893613 / 2097152.0, m.GainMapMax[1], 12);
        Assert.Equal(1, m.Gamma[2]);
        Assert.Equal(77 / 770000000.0, m.BaseOffset[0], 12);
    }

    [Fact]
    public void IsoMetadataRejectsWhatClauseCForbids()
    {
        var good = new GainMapMetadata { AlternateHdrHeadroom = 2, GainMapMax = new[] { 2.0, 2, 2 } }.ToIso21496();
        byte[] With(int at, params byte[] v) { var c = (byte[])good.Clone(); v.CopyTo(c, at); return c; }
        Assert.Null(GainMapMetadata.FromIso21496(With(0, 0, 1)));          // minimum_version 1: ignore
        Assert.Null(GainMapMetadata.FromIso21496(good.AsSpan(0, 4)));      // the baseline's GainMapVersion only
        Assert.Throws<ImageFormatException>(() => GainMapMetadata.FromIso21496(good.AsSpan(0, 30)));
        Assert.Throws<ImageFormatException>(() => GainMapMetadata.FromIso21496(good.AsSpan(0, 3)));
        Assert.Null(GainMapMetadata.FromIso21496(With(9, 0, 0, 0, 0)));     // base headroom denominator 0
        Assert.Null(GainMapMetadata.FromIso21496(With(13, 0, 0, 0, 0)));    // alternate headroom numerator 0 = base headroom
        Assert.Null(GainMapMetadata.FromIso21496(With(37, 0, 0, 0, 0)));    // gamma numerator 0
        var neg = new GainMapMetadata { AlternateHdrHeadroom = 2, GainMapMin = new[] { 1.0, 1, 1 }, GainMapMax = new[] { 0.5, 0.5, 0.5 } };
        Assert.Null(GainMapMetadata.FromIso21496(neg.ToIso21496()));      // max(G) < min(G)
    }

    static XmpData Xmp(string description)
        => XmpData.Parse(Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description xmlns:hdrgm=\"http://ns.adobe.com/hdr-gain-map/1.0/\" " + description + "</rdf:RDF></x:xmpmeta>"))!;

    [Fact]
    public void HdrgmXmpIsMapped()
    {
        // defaults (Ultra HDR v1.1 table): GainMapMin 0, Gamma 1, offsets 1/64, HDRCapacityMin 0
        var m = GainMapMetadata.FromHdrgm(Xmp("hdrgm:Version=\"1.0\" hdrgm:GainMapMax=\"3\" hdrgm:HDRCapacityMax=\"3\"/>"))!;
        Assert.Equal((0.0, 3.0, 1.0, 1 / 64.0, 1 / 64.0, 0.0, 3.0, false),
            (m.GainMapMin[0], m.GainMapMax[2], m.Gamma[1], m.BaseOffset[0], m.AlternateOffset[0], m.BaseHdrHeadroom, m.AlternateHdrHeadroom, m.IsMultichannel));
        // the example of the specification, and per channel values as rdf:Seq
        m = GainMapMetadata.FromHdrgm(Xmp("hdrgm:Version=\"1.0\" hdrgm:GainMapMin=\"-0.57609993\" hdrgm:GainMapMax=\"4.7090998\" hdrgm:Gamma=\"1\" "
            + "hdrgm:OffsetSDR=\"0.015625\" hdrgm:OffsetHDR=\"0.015625\" hdrgm:HDRCapacityMin=\"0\" hdrgm:HDRCapacityMax=\"4.7090998\" hdrgm:BaseRenditionIsHDR=\"False\"/>"))!;
        Assert.Equal(-0.57609993, m.GainMapMin[1], 9);
        Assert.Equal(4.7090998, m.AlternateHdrHeadroom, 9);
        m = GainMapMetadata.FromHdrgm(Xmp("hdrgm:Version=\"1.0\" hdrgm:HDRCapacityMax=\"2\"><hdrgm:GainMapMax><rdf:Seq><rdf:li>1</rdf:li><rdf:li>2</rdf:li><rdf:li>3</rdf:li></rdf:Seq></hdrgm:GainMapMax></rdf:Description>"))!;
        Assert.True(m.IsMultichannel);
        Assert.Equal(new[] { 1.0, 2, 3 }, m.GainMapMax);
        // an HDR base swaps headrooms and offsets: W = F - 1 (Adobe) = sign(...) clamp(...) (ISO formula 3)
        m = GainMapMetadata.FromHdrgm(Xmp("hdrgm:Version=\"1.0\" hdrgm:GainMapMax=\"2\" hdrgm:OffsetSDR=\"0.1\" hdrgm:OffsetHDR=\"0.2\" hdrgm:HDRCapacityMin=\"0.5\" hdrgm:HDRCapacityMax=\"2\" hdrgm:BaseRenditionIsHDR=\"True\"/>"))!;
        Assert.True(m.BaseRenditionIsHdr);
        Assert.Equal((2.0, 0.5, 0.2, 0.1), (m.BaseHdrHeadroom, m.AlternateHdrHeadroom, m.BaseOffset[0], m.AlternateOffset[0]));
        Assert.Equal((1.25 - 0.5) / 1.5 - 1, m.Weight(1.25), 12);
        // invalid metadata means no gain map
        Assert.Null(GainMapMetadata.FromHdrgm(Xmp("hdrgm:Version=\"1.0\" hdrgm:HDRCapacityMax=\"3\"/>")));                       // GainMapMax missing
        Assert.Null(GainMapMetadata.FromHdrgm(Xmp("hdrgm:Version=\"1.0\" hdrgm:GainMapMax=\"3\"/>")));                           // HDRCapacityMax missing
        Assert.Null(GainMapMetadata.FromHdrgm(Xmp("hdrgm:Version=\"2.0\" hdrgm:GainMapMax=\"3\" hdrgm:HDRCapacityMax=\"3\"/>")));
        Assert.Null(GainMapMetadata.FromHdrgm(Xmp("hdrgm:Version=\"1.0\" hdrgm:GainMapMax=\"x\" hdrgm:HDRCapacityMax=\"3\"/>")));
        Assert.Null(GainMapMetadata.FromHdrgm(Xmp("hdrgm:Version=\"1.0\" hdrgm:GainMapMax=\"3\" hdrgm:Gamma=\"0\" hdrgm:HDRCapacityMax=\"3\"/>")));
        Assert.Null(GainMapMetadata.FromHdrgm(Xmp("hdrgm:Version=\"1.0\" hdrgm:GainMapMax=\"3\" hdrgm:OffsetSDR=\"-1\" hdrgm:HDRCapacityMax=\"3\"/>")));
        Assert.Null(GainMapMetadata.FromHdrgm(Xmp("hdrgm:Version=\"1.0\" hdrgm:GainMapMin=\"4\" hdrgm:GainMapMax=\"3\" hdrgm:HDRCapacityMax=\"3\"/>")));
        Assert.Null(GainMapMetadata.FromHdrgm(Xmp("hdrgm:Version=\"1.0\" hdrgm:GainMapMax=\"3\" hdrgm:HDRCapacityMin=\"3\" hdrgm:HDRCapacityMax=\"3\"/>")));
        Assert.Null(GainMapMetadata.FromHdrgm(Xmp("hdrgm:Version=\"1.0\" hdrgm:GainMapMax=\"3\" hdrgm:HDRCapacityMax=\"3\" hdrgm:BaseRenditionIsHDR=\"maybe\"/>")));
    }

    [Fact]
    public void WeightFollowsFormula3()
    {
        var m = new GainMapMetadata { BaseHdrHeadroom = 1, AlternateHdrHeadroom = 3 };
        Assert.Equal(0, m.Weight(0)); Assert.Equal(0, m.Weight(1)); Assert.Equal(0.25, m.Weight(1.5));
        Assert.Equal(1, m.Weight(3)); Assert.Equal(1, m.Weight(10));
        var down = new GainMapMetadata { BaseHdrHeadroom = 3, AlternateHdrHeadroom = 1 };
        Assert.Equal(0, down.Weight(3)); Assert.Equal(-0.5, down.Weight(2)); Assert.Equal(-1, down.Weight(0));
    }

    // ------------------------------------------------------------------ gain map application (clause 6)

    /// <summary>Formulas (1) to (3) evaluated directly, against <see cref="GainMap.Apply(RawImage, RawImage, GainMapMetadata, byte[], double?)"/>.</summary>
    [Fact]
    public void ApplyFollowsFormulas1To3()
    {
        var baseline = new RawImage(2, 2, ColorModel.Rgb, new byte[] { 0, 64, 128, 255, 255, 255, 10, 200, 30, 90, 90, 90 });
        var gain = new RawImage(2, 2, ColorModel.Rgb, new byte[] { 0, 128, 255, 255, 255, 255, 30, 60, 90, 200, 100, 0 });
        var m = new GainMapMetadata
        {
            IsMultichannel = true, BaseHdrHeadroom = 0.5, AlternateHdrHeadroom = 4,
            GainMapMin = new[] { -1.0, -0.5, 0 }, GainMapMax = new[] { 3.0, 4, 2 }, Gamma = new[] { 1, 2.2, 0.5 },
            BaseOffset = new[] { 0.01, 0.02, 0.03 }, AlternateOffset = new[] { 0.04, 0.05, 0.06 },
        };
        foreach (double? target in new double?[] { null, 0, 1.5, 9 })
        {
            var r = (float[])GainMap.Apply(baseline, gain, m, null, target).Data;
            double t = target ?? m.AlternateHdrHeadroom;
            double w = Math.Clamp((t - 0.5) / 3.5, 0, 1);
            for (int i = 0; i < 12; i++)
            {
                int c = i % 3;
                double lin = Processing.Samples.SrgbDecode(((byte[])baseline.Data)[i] / 255.0);
                double g = Math.Pow(((byte[])gain.Data)[i] / 255.0, 1 / m.Gamma[c]) * (m.GainMapMax[c] - m.GainMapMin[c]) + m.GainMapMin[c];
                double expected = (lin + m.BaseOffset[c]) * Math.Pow(2, w * g) - m.AlternateOffset[c];
                Assert.Equal(expected, r[i], 4);
            }
        }
        // an achromatic gain map applies to all three channels, the result keeps alpha and is labelled linear
        var rgba = new RawImage(1, 1, ColorModel.Rgba, new byte[] { 255, 255, 255, 128 });
        var one = new GainMapMetadata { AlternateHdrHeadroom = 1, GainMapMax = new[] { 1.0, 1, 1 }, BaseOffset = new[] { 0.0, 0, 0 }, AlternateOffset = new[] { 0.0, 0, 0 } };
        var a = GainMap.Apply(rgba, new RawImage(1, 1, ColorModel.Gray, new byte[] { 255 }), one);
        Assert.Equal(new float[] { 2, 2, 2, 128 / 255f }, (float[])a.Data);
        Assert.Equal(TransferFunction.Linear, a.Metadata!.Transfer);
    }

    /// <summary>
    /// 5.2.5.1: three per-component values apply to R, G and B separately "regardless of the number of components of the
    /// gain map", so the one sample of an achromatic gain map is unnormalized three times (formula 1).
    /// </summary>
    [Fact]
    public void AchromaticGainMapWithPerChannelMetadata()
    {
        var white = new RawImage(1, 1, ColorModel.Rgb, new byte[] { 255, 255, 255 });
        var zero = new[] { 0.0, 0, 0 };
        var m = new GainMapMetadata { IsMultichannel = true, AlternateHdrHeadroom = 3, GainMapMax = new[] { 1.0, 2, 3 }, BaseOffset = zero, AlternateOffset = zero };
        // sample 255: G = max(G) per channel, 2^1, 2^2, 2^3
        Assert.Equal(new float[] { 2, 4, 8 }, (float[])GainMap.Apply(white, new RawImage(1, 1, ColorModel.Gray, new byte[] { 255 }), m).Data);
        // sample 51 = 0.2 with γ 1, 0.5, 2 (max(G) 1): G = 0.2, 0.2^2 = 0.04, 0.2^0.5 = 0.4472136
        var g = new GainMapMetadata { IsMultichannel = true, AlternateHdrHeadroom = 1, GainMapMax = new[] { 1.0, 1, 1 }, Gamma = new[] { 1, 0.5, 2 }, BaseOffset = zero, AlternateOffset = zero };
        var r = (float[])GainMap.Apply(white, new RawImage(1, 1, ColorModel.Gray, new byte[] { 51 }), g).Data;
        Assert.Equal(1.148698355, r[0], 5);   // 2^0.2
        Assert.Equal(1.028113827, r[1], 5);   // 2^0.04
        Assert.Equal(1.363404449, r[2], 5);   // 2^0.4472136
    }

    /// <summary>An ICC profile with a cicp tag (ICC.1:2022: 'cicp', 4 reserved bytes, primaries, transfer, matrix, full range) added at its end.</summary>
    internal static byte[] WithCicp(byte[] icc, byte primaries, byte transfer)
    {
        int n = (int)BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan(128));
        var r = new List<byte>();
        r.AddRange(icc.AsSpan(0, 132 + 12 * n).ToArray());
        r.AddRange(new byte[12]);                              // the new directory entry
        r.AddRange(icc.AsSpan(132 + 12 * n).ToArray());        // tag data, 12 bytes further on
        var b = r.ToArray();
        for (int i = 0; i < n; i++)
        {
            int e = 132 + 12 * i + 4;
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(e), BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(e)) + 12);
        }
        int at = b.Length;
        b = b.Concat("cicp"u8.ToArray()).Concat(new byte[] { 0, 0, 0, 0, primaries, transfer, 0, 1 }).ToArray();
        "cicp"u8.CopyTo(b.AsSpan(132 + 12 * n));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(132 + 12 * n + 4), (uint)at);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(132 + 12 * n + 8), 12);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(128), (uint)n + 1);
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
        b[8] = 4; b[9] = 0x40;
        return b;
    }

    /// <summary>
    /// A cicp tag in the baseline's profile (C.4.4: HDR baselines carry one) gives its transfer and primaries over the
    /// profile's curves and colorants. PQ: 203 cd/m² (code 0.58069, BT.2408) is HDR reference white, 1.0 (3.4); 1000 cd/m²
    /// is 1000 / 203. HLG: 75 % (BT.2408 reference white on a 1000 cd/m² display) is 1.0.
    /// </summary>
    [Fact]
    public void CicpTagOfTheBaselineProfile()
    {
        var zero = new[] { 0.0, 0, 0 };
        var m = new GainMapMetadata { AlternateHdrHeadroom = 1, GainMapMax = zero, BaseOffset = zero, AlternateOffset = zero };
        var gain = new RawImage(1, 1, ColorModel.Gray, new byte[] { 0 });
        // a BT.2020 profile with the sRGB curve, the cicp tag says BT.2020 PQ
        var pqIcc = WithCicp(IccProfile.CreateRgb("2020", CieXy.D65, new(0.708, 0.292), new(0.170, 0.797), new(0.131, 0.046), IccCurve.Srgb).ToBytes(), 9, 16);
        Assert.Equal(new IccCicp(9, 16, 0, true), IccProfile.Parse(pqIcc).Cicp);
        ushort Pq(double nits) => (ushort)Math.Round(PqInverseEotf(nits / 10000) * 65535);
        var pq = new RawImage(1, 1, ColorModel.Rgb, new[] { Pq(203), Pq(1000), (ushort)0 }) { Metadata = new ImageMetadata { IccProfile = pqIcc } };
        var a = GainMap.Apply(pq, gain, m);
        var r = (float[])a.Data;
        Assert.Equal(1, r[0], 3);
        Assert.Equal(1000 / 203.0, r[1], 3);
        Assert.Equal(0, r[2], 6);
        // the result is labelled with the (linear) BT.2020 primaries
        var label = IccProfile.Parse(a.Metadata!.IccProfile!);
        var bt2020 = IccProfile.LinearRec2020;
        Assert.Equal(bt2020.RedColorant!.Value.X, label.RedColorant!.Value.X, 1e-3);
        Assert.Equal(bt2020.GreenColorant!.Value.Y, label.GreenColorant!.Value.Y, 1e-3);
        // HLG: the profile's colorants are sRGB's, the tag says BT.2020
        var hlgIcc = WithCicp(IccProfile.Srgb.ToBytes(), 9, 18);
        var hlg = new RawImage(1, 1, ColorModel.Rgb, new ushort[] { 49151, 49151, 49151 }) { Metadata = new ImageMetadata { IccProfile = hlgIcc } };
        var h = GainMap.Apply(hlg, gain, m);
        Assert.All((float[])h.Data, v => Assert.InRange(v, 0.998, 1.003));   // 75 %: 203.2 cd/m²
        Assert.Equal(bt2020.BlueColorant!.Value.Z, IccProfile.Parse(h.Metadata!.IccProfile!).BlueColorant!.Value.Z, 1e-3);
    }

    /// <summary>SMPTE ST 2084 inverse EOTF: luminance relative to 10000 cd/m² -> code value.</summary>
    static double PqInverseEotf(double y)
    {
        const double m1 = 2610.0 / 16384, m2 = 2523.0 / 4096 * 128, c1 = 3424.0 / 4096, c2 = 2413.0 / 4096 * 32, c3 = 2392.0 / 4096 * 32;
        double p = Math.Pow(y, m1);
        return Math.Pow((c1 + c2 * p) / (1 + c3 * p), m2);
    }

    /// <summary>A gain map smaller than the baseline is resampled bilinearly at co-sited positions (6.2.2, NOTE 1).</summary>
    [Fact]
    public void SmallGainMapIsResampledCosited()
    {
        var baseline = new RawImage(4, 1, ColorModel.Gray, new byte[] { 255, 255, 255, 255 });
        var m = new GainMapMetadata { AlternateHdrHeadroom = 2, GainMapMax = new[] { 2.0, 2, 2 }, BaseOffset = new[] { 0.0, 0, 0 }, AlternateOffset = new[] { 0.0, 0, 0 } };
        var r = (float[])GainMap.Apply(baseline, new RawImage(2, 1, ColorModel.Gray, new byte[] { 0, 255 }), m).Data;
        // G at x = 0, 1, 2, 3: gain map positions 0, 0.5, 1, 1 (clamped) -> 0, 1, 2, 2 stops
        Assert.Equal(new[] { 1.0, 2, 4, 4 }, new[] { r[0], r[3], r[6], r[9] }.Select(v => Math.Round(v, 4)));
    }

    [Theory]
    [InlineData(false, 1, 0.06, 0.01)]
    [InlineData(true, 1, 0.02, 0.004)]
    [InlineData(false, 4, 1.4, 0.08)]
    public void ComputeThenApplyRestoresAlternate(bool multichannel, int scale, double maxStops, double meanStops)
    {
        var hdr = SyntheticHdr(96, 64);
        var sdr = SyntheticSdr(hdr);
        var gm = GainMap.Compute(sdr, hdr, new GainMapOptions { Multichannel = multichannel, Scale = scale });
        Assert.Equal((96 / scale, 64 / scale, multichannel ? 3 : 1), (gm.Image.Width, gm.Image.Height, gm.Image.Channels));
        Assert.True(gm.Metadata.GainMapMin[0] <= 0 && gm.Metadata.GainMapMax[0] > 0);
        var r = GainMap.Apply(sdr, gm);
        // an achromatic gain map recovers the luminance, not the colour: compare luminance then
        if (!multichannel) { hdr = Luminance(hdr); r = Luminance(r); }
        AssertClose(hdr, r, maxStops, meanStops, $"multichannel {multichannel}, scale {scale}");
        // headroom 0 gives the baseline back
        AssertClose(GainMap.LinearRgb(sdr), GainMap.Apply(sdr, gm, 0), 1e-5, 1e-6, "headroom 0");
    }

    static RawImage Luminance(RawImage rgb)
    {
        var f = (float[])rgb.Data;
        var y = new float[f.Length / 3];
        for (int i = 0; i < y.Length; i++) y[i] = 0.2126f * f[i * 3] + 0.7152f * f[i * 3 + 1] + 0.0722f * f[i * 3 + 2];
        return new RawImage(rgb.Width, rgb.Height, ColorModel.Gray, y);
    }

    /// <summary>Annex B: with use_base_colour_space off the baseline is converted to the alternate's primaries first.</summary>
    [Fact]
    public void ApplicationSpaceFollowsTheMetadata()
    {
        var baseline = new RawImage(1, 1, ColorModel.Rgb, new byte[] { 255, 0, 0 });
        var gain = new RawImage(1, 1, ColorModel.Gray, new byte[] { 255 });
        var p3 = IccProfile.DisplayP3.ToBytes();
        var m = new GainMapMetadata { AlternateHdrHeadroom = 1, GainMapMax = new[] { 1.0, 1, 1 }, BaseOffset = new[] { 0.0, 0, 0 }, AlternateOffset = new[] { 0.0, 0, 0 } };
        // sRGB red in P3: about (0.8225, 0.0332, 0.0171); doubled either way since the offsets are 0
        var a = (float[])GainMap.Apply(baseline, gain, m, p3).Data;
        Assert.Equal(new[] { 1.645, 0.066, 0.034 }, a.Select(v => Math.Round(v, 3)));
        var b = (float[])GainMap.Apply(baseline, gain, new GainMapMetadata { UseBaseColourSpace = false, AlternateHdrHeadroom = 1, GainMapMax = m.GainMapMax, BaseOffset = m.BaseOffset, AlternateOffset = m.AlternateOffset }, p3).Data;
        Assert.Equal(a.Select(v => Math.Round(v, 3)), b.Select(v => Math.Round(v, 3)));
        // with offsets the order matters: (B + k) in the base space vs. in the alternate space
        var k = new[] { 0.2, 0, 0 };
        var c = (float[])GainMap.Apply(baseline, gain, new GainMapMetadata { AlternateHdrHeadroom = 1, GainMapMax = m.GainMapMax, BaseOffset = k, AlternateOffset = k }, p3).Data;
        var d = (float[])GainMap.Apply(baseline, gain, new GainMapMetadata { UseBaseColourSpace = false, AlternateHdrHeadroom = 1, GainMapMax = m.GainMapMax, BaseOffset = k, AlternateOffset = k }, p3).Data;
        Assert.Equal(0.8225 * 2 + 0.2, d[0], 2);
        Assert.NotEqual(Math.Round(c[1], 3), Math.Round(d[1], 3));
    }

    // ------------------------------------------------------------------ Ultra HDR JPEG

    [Theory]
    [InlineData(true, true, false, 1)]
    [InlineData(true, false, false, 1)]
    [InlineData(false, true, false, 1)]
    [InlineData(true, true, true, 1)]
    [InlineData(true, true, false, 4)]
    public void UltraHdrRoundTrip(bool xmp, bool iso, bool multichannel, int scale)
    {
        var hdr = SyntheticHdr(96, 64);
        var o = new UltraHdrEncodeOptions { WriteXmp = xmp, WriteIso21496 = iso, GainMap = new GainMapOptions { Multichannel = multichannel, Scale = scale } };
        var jpeg = UltraHdr(hdr, o);
        var gm = Jpeg.GetGainMap(jpeg)!;
        Assert.NotNull(gm);
        Assert.Equal(iso ? GainMapSource.Iso21496 : GainMapSource.Xmp, gm.Source);
        Assert.Equal(multichannel, gm.Metadata.IsMultichannel);
        var r = Jpeg.DecodeHdr(jpeg);
        var (a, b) = multichannel ? (hdr, r) : (Luminance(hdr), Luminance(r));
        AssertClose(a, b, scale == 1 ? 0.4 : 1.8, scale == 1 ? 0.025 : 0.09, $"xmp {xmp} iso {iso} multi {multichannel} scale {scale}");
        // both forms say the same
        if (xmp && iso)
        {
            var h = JpegHeaders.Read(jpeg, Jpeg.GetMultiPicture(jpeg)!.Images[1].Offset)!;
            var fromXmp = GainMapMetadata.FromHdrgm(h.ParseXmp()!)!;
            Assert.Equal(gm.Metadata.GainMapMax[0], fromXmp.GainMapMax[0], 6);
            Assert.Equal(gm.Metadata.AlternateHdrHeadroom, fromXmp.AlternateHdrHeadroom, 6);
        }
    }

    [Fact]
    public void UltraHdrFileStructure()
    {
        var hdr = SyntheticHdr(64, 48);
        var jpeg = UltraHdr(hdr);
        var info = ImageCodec.GetInfo(jpeg);
        Assert.True(info.HasGainMap);
        Assert.True(info.Metadata!.HasGainMap);
        Assert.Equal(1, info.FrameCount);
        var mpf = info.MultiPicture!;
        Assert.Equal("0100", mpf.Version);
        Assert.False(mpf.IsExtended);
        Assert.Equal(new[] { MpfImageType.BaselinePrimary, MpfImageType.GainMap }, mpf.Images.Select(i => i.Type));
        Assert.True(mpf.Images[0].IsRepresentative && mpf.Images[0].IsDependentParent && mpf.Images[0].DependentImage1 == 2);
        Assert.True(mpf.Images[1].IsDependentChild && mpf.Images[1].IsPresent);
        Assert.Equal(jpeg.Length, mpf.Images[0].Size + mpf.Images[1].Size);
        Assert.Equal(mpf.Images[0].Size, mpf.Images[1].Offset);
        // the baseline decodes as before; the gain map image is individual image 1
        TestUtil.AssertEqual(Jpeg.Decode(Jpeg.ExtractPicture(jpeg, 0)), Jpeg.Decode(jpeg));
        var g = Jpeg.DecodePicture(jpeg, 1);
        Assert.Equal((64, 48, 1), (g.Width, g.Height, g.Channels));
        // the GContainer directory names the gain map's length
        var x = JpegHeaders.Read(jpeg, 0)!.ParseXmp()!;
        Assert.Equal(mpf.Images[1].Size.ToString(), x.Properties.First(p => p.Key.EndsWith("Item:Length")).Value);
        Assert.False(Jpeg.GetInfo(StripApp(Jpeg.ExtractPicture(jpeg, 0))).HasGainMap);
    }

    /// <summary>A JPEG with every APPn segment removed.</summary>
    static byte[] StripApp(byte[] jpeg)
    {
        var h = JpegHeaders.Read(jpeg, 0)!;
        var ms = new MemoryStream();
        int p = 0;
        foreach (var (m, s, n) in h.Segments)
            if (m is >= 0xE0 and <= 0xEF) { ms.Write(jpeg, p, s - 4 - p); p = s + n; }
        ms.Write(jpeg, p, jpeg.Length - p);
        return ms.ToArray();
    }

    [Fact]
    public void ToneMappedUltraHdr()
    {
        var hdr = SyntheticHdr(80, 60, 3);
        var ms = new MemoryStream();
        Jpeg.EncodeUltraHdr(hdr, ms, new UltraHdrEncodeOptions { GainMap = new GainMapOptions { Multichannel = true } });
        var jpeg = ms.ToArray();
        var sdr = Jpeg.Decode(jpeg);
        Assert.Equal((80, 60, 3), (sdr.Width, sdr.Height, sdr.Channels));
        Assert.Equal(IccProfile.Srgb.ToBytes(), sdr.Metadata!.IccProfile);
        AssertClose(hdr, Jpeg.DecodeHdr(jpeg), 0.4, 0.025, "tone mapped base + gain map");
        AssertClose(GainMap.LinearRgb(sdr), Jpeg.DecodeHdr(jpeg, 0), 1e-4, 1e-5, "headroom 0");
        // partial headroom: between SDR and HDR, in the log domain
        var half = Jpeg.DecodeHdr(jpeg, Jpeg.GetGainMap(jpeg)!.Metadata.AlternateHdrHeadroom / 2);
        var full = Jpeg.DecodeHdr(jpeg);
        var s = GainMap.LinearRgb(sdr);
        float[] fh = (float[])half.Data, ff = (float[])full.Data, fs = (float[])s.Data;
        int i = Array.IndexOf(ff, ff.Max());
        Assert.InRange(fh[i], fs[i] * 1.01, ff[i] * 0.99);
    }

    [Fact]
    public void UserMetadataIsKeptAndOrientationApplied()
    {
        var hdr = SyntheticHdr(64, 40);
        var sdr = SyntheticSdr(hdr);
        var exif = Jpeg.Decode(TestJpegWithOrientation(6)).Metadata!.Exif;
        var userXmp = Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:dc=\"http://purl.org/dc/elements/1.1/\" rdf:about=\"\"><dc:creator><rdf:Seq><rdf:li>Someone</rdf:li></rdf:Seq></dc:creator></rdf:Description></rdf:RDF></x:xmpmeta>");
        sdr.Metadata = new ImageMetadata { Exif = exif, Orientation = 6, Xmp = userXmp, IccProfile = IccProfile.Srgb.ToBytes() };
        var ms = new MemoryStream();
        Jpeg.EncodeUltraHdr(sdr, hdr, ms);
        var jpeg = ms.ToArray();
        var info = Jpeg.GetInfo(jpeg);
        Assert.Equal((40, 64, 6), (info.Width, info.Height, info.Orientation));
        var md = ImageCodec.ReadMetadata(jpeg);
        Assert.Equal("Someone", md.Xmp!["dc:creator"]);
        Assert.True(md.HasGainMap);
        var gm = Jpeg.GetGainMap(jpeg)!;
        Assert.Equal((40, 64), (gm.Image.Width, gm.Image.Height));   // turned like the baseline
        var r = Jpeg.DecodeHdr(jpeg);
        Assert.Equal((40, 64), (r.Width, r.Height));
        AssertClose(Luminance(ImageOps.ApplyOrientation(hdr, 6)), Luminance(r), 0.4, 0.025, "rotated");
        var raw = Jpeg.GetGainMap(jpeg, new JpegDecodeOptions { ApplyOrientation = false })!;
        Assert.Equal((64, 40), (raw.Image.Width, raw.Image.Height));
    }

    static byte[] TestJpegWithOrientation(int o)
    {
        var img = new RawImage(8, 8, ColorModel.Rgb, new byte[192]) { Metadata = new ImageMetadata { Orientation = o } };
        var ms = new MemoryStream();
        Jpeg.Encode(img, ms);
        return ms.ToArray();
    }

    [Fact]
    public void UnusableGainMapsAreIgnored()
    {
        var hdr = SyntheticHdr(48, 32);
        var jpeg = UltraHdr(hdr, new UltraHdrEncodeOptions { WriteXmp = false });
        var mpf = Jpeg.GetMultiPicture(jpeg)!;
        long g = mpf.Images[1].Offset;
        // ISO payload of the gain map with gamma 0: no gain map, the SDR image stays
        var bad = (byte[])jpeg.Clone();
        int iso = bad.AsSpan((int)g).IndexOf("urn:iso:std:iso:ts:21496:-1\0"u8) + (int)g + 28;
        bad.AsSpan(iso + 5 + 16 + 16, 4).Clear();
        Assert.Null(Jpeg.GetGainMap(bad));
        TestUtil.AssertEqual(Jpeg.Decode(jpeg), Jpeg.Decode(bad));
        AssertClose(GainMap.LinearRgb(Jpeg.Decode(bad)), Jpeg.DecodeHdr(bad), 0, 0, "no gain map");
        // a gain map image that is not where the entry says, or damaged
        bad = (byte[])jpeg.Clone();
        bad[g] = 0;
        Assert.Null(Jpeg.GetGainMap(bad));
        Assert.Single(Jpeg.DecodeAll(bad));
        bad = jpeg[..(int)(g + 200)];
        Assert.Null(Jpeg.GetGainMap(bad));
        Assert.NotNull(Jpeg.Decode(bad));
        // unknown minimum_version in the primary: the ISO metadata is ignored
        bad = (byte[])jpeg.Clone();
        int piso = bad.AsSpan().IndexOf("urn:iso:std:iso:ts:21496:-1\0"u8) + 28;
        bad[piso + 1] = 9;
        Assert.Null(Jpeg.GetGainMap(bad));
        Assert.False(Jpeg.GetInfo(bad).HasGainMap && Jpeg.GetMultiPicture(bad) == null);
    }

    [Fact]
    public void MetadataWritingKeepsTheGainMap()
    {
        var hdr = SyntheticHdr(64, 48);
        var jpeg = UltraHdr(hdr);
        var edit = new MetadataEdit { Make = "Maker", Model = new string('M', 3000), Description = "a gain map file " + new string('x', 5000) };
        var edited = ImageCodec.WriteMetadata(jpeg, edit);
        Assert.NotEqual(jpeg.Length, edited.Length);
        var mpf = Jpeg.GetMultiPicture(edited)!;
        Assert.True(mpf.Images[1].IsPresent);
        Assert.Equal(edited.Length, mpf.Images[1].Offset + mpf.Images[1].Size);
        var a = Jpeg.DecodeHdr(jpeg); var b = Jpeg.DecodeHdr(edited);
        TestUtil.AssertEqual(a, b);
        Assert.Equal("Maker", ImageCodec.ReadMetadata(edited).Make);
        // the GContainer directory survives an XMP rewrite
        Assert.NotNull(JpegHeaders.Read(edited, 0)!.ParseXmp()!.Properties.FirstOrDefault(p => p.Key.EndsWith("Item:Semantic") && p.Value == "GainMap"));
        if (UltraHdrOracle.Exe is { } exe)
            TestUtil.AssertEqual(UltraHdrOracle.Decode(exe, jpeg).Image, UltraHdrOracle.Decode(exe, edited).Image);
    }

    // ------------------------------------------------------------------ against libultrahdr

    /// <summary>
    /// ultrahdr_app reads our files: its full-boost linear decode matches ours within the half float output plus the
    /// different gain map resampling filter (scale 4).
    /// </summary>
    [Theory]
    [InlineData(true, true, false, 1, 0.04)]
    [InlineData(false, true, false, 1, 0.04)]
    [InlineData(true, false, false, 1, 0.04)]
    [InlineData(true, true, true, 1, 0.04)]
    [InlineData(true, true, false, 4, 0.8)]
    public void OracleDecodesOurFiles(bool xmp, bool iso, bool multichannel, int scale, double maxStops)
    {
        var exe = UltraHdrOracle.Exe;
        if (exe == null) return;
        var hdr = SyntheticHdr(96, 64, 5);
        var jpeg = UltraHdr(hdr, new UltraHdrEncodeOptions { WriteXmp = xmp, WriteIso21496 = iso, GainMap = new GainMapOptions { Multichannel = multichannel, Scale = scale } });
        var (oracle, meta) = UltraHdrOracle.Decode(exe, jpeg);
        var gm = Jpeg.GetGainMap(jpeg)!.Metadata;
        Assert.Equal(Math.Pow(2, gm.GainMapMax[0]), meta["maxContentBoost"], 3);
        Assert.Equal(Math.Pow(2, gm.GainMapMin[0]), meta["minContentBoost"], 3);
        AssertClose(oracle, Jpeg.DecodeHdr(jpeg), maxStops, scale == 1 ? 0.008 : 0.03, "ultrahdr_app vs ours");
    }

    /// <summary>We read ultrahdr_app's files (XMP and ISO 21496-1 metadata, single and multi channel, scaled gain maps, gamma).</summary>
    [Theory]
    [InlineData("", 0.04)]
    [InlineData("-M 0", 0.1)]
    [InlineData("-s 4", 2.0)]
    [InlineData("-s 2 -M 0 -G 2.2", 0.7)]
    [InlineData("-q 70 -Q 70", 0.04)]
    public void WeDecodeOracleFiles(string args, double maxStops)
    {
        var exe = UltraHdrOracle.Exe;
        if (exe == null) return;
        var hdr = SyntheticHdr(96, 64, 7);
        var jpeg = UltraHdrOracle.Encode(exe, hdr, SyntheticSdr(hdr), args);
        var (oracle, meta) = UltraHdrOracle.Decode(exe, jpeg);
        var info = ImageCodec.GetInfo(jpeg);
        Assert.True(info.HasGainMap);
        // (libultrahdr 1.4 marks the gain map image as of undefined type, and the file stays a Baseline MP file)
        Assert.Equal(new[] { MpfImageType.BaselinePrimary, MpfImageType.Undefined }, info.MultiPicture!.Images.Select(i => i.Type));
        Assert.Equal(1, info.FrameCount);
        Assert.Single(Jpeg.DecodeAll(jpeg));
        var gm = Jpeg.GetGainMap(jpeg)!;
        Assert.Equal(GainMapSource.Iso21496, gm.Source);
        Assert.Equal(meta["gamma"], gm.Metadata.Gamma[0], 3);
        Assert.Equal(meta["maxContentBoost"], Math.Pow(2, gm.Metadata.GainMapMax[0]), 3);
        Assert.Equal(meta["hdrCapacityMax"], Math.Pow(2, gm.Metadata.AlternateHdrHeadroom), 2);
        AssertClose(oracle, Jpeg.DecodeHdr(jpeg), maxStops, args.Contains("-s") ? 0.05 : 0.008, $"ours vs ultrahdr_app ({args})");
        // its XMP-only reading gives the same
        var xmpOnly = WithoutIso(jpeg);
        Assert.Equal(GainMapSource.Xmp, Jpeg.GetGainMap(xmpOnly)!.Source);
        AssertClose(Jpeg.DecodeHdr(jpeg), Jpeg.DecodeHdr(xmpOnly), 0.002, 0.0005, "XMP vs ISO metadata");
    }

    /// <summary>The file with every ISO 21496-1 APP2 segment blanked out (renamed to APP15, so offsets stay valid).</summary>
    static byte[] WithoutIso(byte[] jpeg)
    {
        var r = (byte[])jpeg.Clone();
        int from = 0;
        while (true)
        {
            int i = r.AsSpan(from).IndexOf("urn:iso:std:iso:ts:21496:-1\0"u8);
            if (i < 0) return r;
            i += from;
            if (r[i - 4] == 0xFF && r[i - 3] == 0xE2) r[i - 3] = 0xEF;
            from = i + 1;
        }
    }

    // ------------------------------------------------------------------ fixtures (tests/oracle/gen_gainmap.py)

    public static IEnumerable<object[]> Fixtures() => TestUtil.Files("gainmap", ".jpg");

    /// <summary>Files ultrahdr_app wrote from synthetic images, against its linear decode (no oracle needed at test time).</summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void FixturesMatchLibultrahdr(string file)
    {
        var path = TestUtil.Fixture(file);
        var jpeg = File.ReadAllBytes(path);
        var json = JsonDocument.Parse(File.ReadAllText(path + ".json")).RootElement;
        var r = TestUtil.LoadRef(path + ".ref");
        var f = new float[r.Width * r.Height * 3];
        var h = (Half[])r.Data;
        for (int i = 0; i < r.Width * r.Height; i++) for (int c = 0; c < 3; c++) f[i * 3 + c] = (float)h[i * r.Channels + c];
        var expected = new RawImage(r.Width, r.Height, ColorModel.Rgb, f);
        var gm = Jpeg.GetGainMap(jpeg)!;
        Assert.Equal(json.GetProperty("maxContentBoost").GetDouble(), Math.Pow(2, gm.Metadata.GainMapMax[0]), 3);
        double tol = json.GetProperty("tolerance").GetDouble();
        AssertClose(expected, Jpeg.DecodeHdr(jpeg), tol, tol / 5, file);
    }

    // ------------------------------------------------------------------ MPF / MPO

    static RawImage View(int w, int h, int shift)
    {
        var b = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 3;
                b[i] = (byte)((x + shift) * 255 / w); b[i + 1] = (byte)(y * 255 / h); b[i + 2] = (byte)(((x + shift) / 8 + y / 8) % 2 * 200);
            }
        return new RawImage(w, h, ColorModel.Rgb, b);
    }

    [Fact]
    public void MpoRoundTrip()
    {
        var left = View(64, 48, 0); var right = View(64, 48, 6);
        var ms = new MemoryStream();
        Jpeg.EncodeMpo(new[] { left, right }, ms, new MpoEncodeOptions { ConvergenceAngle = -1.5, BaselineLength = 0.077, Jpeg = new JpegEncodeOptions { Quality = 95 } });
        var mpo = ms.ToArray();
        var info = Jpeg.GetInfo(mpo);
        Assert.Equal(2, info.FrameCount);
        Assert.False(info.HasGainMap);
        var mpf = info.MultiPicture!;
        Assert.True(mpf.IsExtended);
        Assert.Equal(2, mpf.ViewCount);
        Assert.All(mpf.Images, i => Assert.Equal(MpfImageType.Disparity, i.Type));
        Assert.True(mpf.Images[0].IsRepresentative);
        Assert.False(mpf.Images[1].IsRepresentative);
        var full = Jpeg.GetMultiPicture(mpo)!;
        Assert.Equal(new uint?[] { 1, 2 }, full.Images.Select(i => i.IndividualNumber));
        Assert.All(full.Images, i => Assert.Equal(1u, i.BaseViewpoint));
        Assert.All(full.Images, i => Assert.Equal(-1.5, i.ConvergenceAngle!.Value, 4));
        Assert.All(full.Images, i => Assert.Equal(0.077, i.BaselineLength!.Value, 4));
        var all = Jpeg.DecodeAll(mpo);
        Assert.Equal(2, all.Length);
        Assert.True(TestUtil.Psnr(left, all[0], 255) > 35);
        Assert.True(TestUtil.Psnr(right, all[1], 255) > 35);
        TestUtil.AssertEqual(all[1], Jpeg.DecodePicture(mpo, 1));
        Assert.Equal(2, ImageCodec.DecodeAll(mpo).Length);
        Assert.Equal(ImageFormat.Jpeg, ImageCodec.FormatFromExtension("x.mpo"));
        // unknown angle / length are written as FFFFFFFF/FFFFFFFF and read as null
        ms = new MemoryStream();
        Jpeg.EncodeMpo(new[] { left, right, left }, ms, new MpoEncodeOptions { Type = MpfImageType.MultiAngle, Representative = 1 });
        var m3 = Jpeg.GetMultiPicture(ms.ToArray())!;
        Assert.Equal(3, m3.ViewCount);
        Assert.True(m3.Images[1].IsRepresentative);
        Assert.Null(m3.Images[2].ConvergenceAngle);
        Assert.Equal(3u, m3.Images[2].IndividualNumber);
        // WriteMpo takes JPEG streams and drops MPF segments they already have
        var again = new MemoryStream();
        Jpeg.WriteMpo(new[] { Jpeg.ExtractPicture(mpo, 1), Jpeg.ExtractPicture(mpo, 0) }, again);
        var swapped = Jpeg.DecodeAll(again.ToArray());
        TestUtil.AssertEqual(all[1], swapped[0]);
        TestUtil.AssertEqual(all[0], swapped[1]);
    }

    [Fact]
    public void MpoAndUltraHdrPassExiftool()
    {
        var exiftool = MetadataWriteTests.ExifTool;
        if (exiftool == null) return;
        var ms = new MemoryStream();
        Jpeg.EncodeMpo(new[] { View(32, 24, 0), View(32, 24, 3) }, ms, new MpoEncodeOptions { BaselineLength = 0.1 });
        var mpo = ms.ToArray();
        var j = Exiftool(exiftool, mpo);
        Assert.Equal(2, Find(j, "MPF0:NumberOfImages").GetInt32());
        Assert.Equal("0100", Find(j, "MPF0:MPFVersion").GetString());
        Assert.Equal("Multi-frame Disparity", Find(j, "MPImage2:MPImageType").GetString());
        Assert.Equal("Representative image", Find(j, "MPImage1:MPImageFlags").GetString());
        Assert.Equal(mpo.Length, Find(j, "MPImage2:MPImageStart").GetInt64() + Find(j, "MPImage2:MPImageLength").GetInt64());
        Assert.Equal(0.1, Find(j, ":BaselineLength").GetDouble(), 4);
        Assert.DoesNotContain(j.EnumerateObject(), p => p.Name.EndsWith(":Warning") || p.Name.EndsWith(":Error"));
        var u = Exiftool(exiftool, UltraHdr(SyntheticHdr(48, 32)));
        Assert.Equal(2, Find(u, "MPF0:NumberOfImages").GetInt32());
        Assert.Contains("Gain", Find(u, "MPImage2:MPImageType").GetString());
        Assert.Equal(1.0, Find(u, "XMP-hdrgm:Version").GetDouble());
        var dir = Find(u, "XMP-GContainer:ContainerDirectory").GetRawText();
        Assert.Contains("\"Primary\"", dir);
        Assert.Contains("\"GainMap\"", dir);
        Assert.DoesNotContain(u.EnumerateObject(), p => p.Name.EndsWith(":Warning") || p.Name.EndsWith(":Error"));
    }

    static JsonElement Find(JsonElement j, string name)
    {
        foreach (var p in j.EnumerateObject()) if (p.Name == name || name.StartsWith(':') && p.Name.EndsWith(name)) return p.Value;
        throw new KeyNotFoundException(name + " not in ExifTool's output");
    }

    JsonElement Exiftool(string exe, byte[] file)
    {
        var tmp = Path.Combine(TestUtil.FixtureDir, "_tmp");
        Directory.CreateDirectory(tmp);
        var f = Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            File.WriteAllBytes(f, file);
            var psi = new ProcessStartInfo(OperatingSystem.IsWindows() ? "perl" : exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            if (OperatingSystem.IsWindows()) psi.ArgumentList.Add(exe);
            foreach (var a in new[] { "-j", "-G1", "-a", "-struct", "-ee", f }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            output.WriteLine(o);
            return JsonDocument.Parse(o).RootElement[0];
        }
        finally { File.Delete(f); }
    }

    /// <summary>Damaged MP extensions never stop the primary image from decoding.</summary>
    [Fact]
    public void MalformedMpfFallsBackToThePrimary()
    {
        var ms = new MemoryStream();
        Jpeg.EncodeMpo(new[] { View(32, 24, 0), View(32, 24, 3) }, ms);
        var mpo = ms.ToArray();
        var primary = Jpeg.Decode(mpo);
        int mp = mpo.AsSpan().IndexOf("MPF\0"u8) + 4;
        var cases = new List<byte[]>();
        byte[] Patch(int at, params byte[] v) { var c = (byte[])mpo.Clone(); v.CopyTo(c, at); return c; }
        cases.Add(Patch(mp, (byte)'X'));                       // byte order mark
        cases.Add(Patch(mp + 4, 0xFF, 0xFF, 0, 0));            // index IFD offset
        cases.Add(Patch(mp + 8, 0xFF, 0xFF));                  // entry count
        cases.Add(Patch(mp + 10 + 12 + 8, 0x7F, 0, 0, 0));     // NumberOfImages huge
        int entries = (int)BinaryPrimitives.ReadUInt32BigEndian(mpo.AsSpan(mp + 10 + 24 + 8));
        cases.Add(Patch(mp + entries + 16 + 8, 0x7F, 0, 0, 0)); // second image's offset outside the file
        cases.Add(mpo[..^300]);                                // truncated in the second image
        foreach (var c in cases)
        {
            TestUtil.AssertEqual(primary, Jpeg.Decode(c));
            var info = Jpeg.GetInfo(c);
            Assert.Equal(32, info.Width);
            Assert.NotEmpty(Jpeg.DecodeAll(c));
            Assert.Null(Jpeg.GetGainMap(c));
            _ = ImageCodec.ReadMetadata(c);
        }
        Assert.Null(Jpeg.GetMultiPicture(cases[0]));
        Assert.False(Jpeg.GetMultiPicture(cases[4])!.Images[1].IsPresent);
        Assert.Single(Jpeg.DecodeAll(cases[4]));
        Assert.Throws<ImageFormatException>(() => Jpeg.DecodePicture(cases[4], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Jpeg.DecodePicture(mpo, 2));
    }

    public static IEnumerable<object[]> PillowMpo() => TestUtil.ThirdPartyFiles("mpo/pillow", ".mpo", ".jpg");

    /// <summary>Pillow's MPO samples (fetched, see the test-data README): real stereo files and damaged variants.</summary>
    [TestDataTheory("fixtures/mpo/pillow/sugarshack.mpo")]
    [MemberData(nameof(PillowMpo))]
    public void PillowMpoSamples(string file)
    {
        var data = File.ReadAllBytes(TestUtil.Fixture(file));
        var primary = Jpeg.Decode(data);
        var views = Jpeg.DecodeAll(data);
        var mpf = Jpeg.GetMultiPicture(data);
        output.WriteLine($"{file}: {primary.Width}x{primary.Height}, {views.Length} views, {mpf}");
        Assert.Equal(primary.Width, views[0].Width);
        string name = Path.GetFileName(file);
        // (sugarshack_ifd_offset.mpo pads the MP header by 2 bytes and leaves the offsets: the SOI is found just after)
        if (name is "sugarshack.mpo" or "frozenpond.mpo" or "sugarshack_ifd_offset.mpo" or "sugarshack_frame_size.mpo" or "ignore_frame_size.mpo")
        {
            Assert.Equal(2, views.Length);
            Assert.Equal(2, Jpeg.GetInfo(data).FrameCount);
            Assert.True(mpf!.IsExtended);
            Assert.All(mpf.Images, i => Assert.True(i.IsPresent));
            output.WriteLine($"view 2: {views[1].Width}x{views[1].Height}");
            if (views[1].Width == views[0].Width && views[1].Height == views[0].Height)
                Assert.True(TestUtil.Psnr(views[0], views[1], 255) < 60);   // two different views
        }
        else Assert.Single(views);
    }

    // ------------------------------------------------------------------ real camera files

    public static IEnumerable<object[]> Photos() => TestUtil.ThirdPartyFiles("gainmap/photos", ".jpg");

    /// <summary>Gain map photos from phones (test-data repository, CC licensed): found, valid, and decoded like ultrahdr_app does.</summary>
    [TestDataTheory("fixtures/gainmap/photos")]
    [MemberData(nameof(Photos))]
    public void PhonePhotos(string file)
    {
        var path = TestUtil.Fixture(file);
        var data = File.ReadAllBytes(path);
        var info = ImageCodec.GetInfo(data);
        Assert.True(info.HasGainMap);
        var gm = Jpeg.GetGainMap(data)!;
        Assert.NotNull(gm);
        output.WriteLine($"{file}: {info.Width}x{info.Height}, {gm.Source}, {gm.Metadata}, gain map {gm.Image.Width}x{gm.Image.Height}x{gm.Image.Channels}");
        var json = JsonDocument.Parse(File.ReadAllText(path + ".json")).RootElement;
        Assert.Equal(json.GetProperty("maxContentBoost").GetDouble(), Math.Pow(2, gm.Metadata.GainMapMax[0]), 2);
        Assert.Equal(json.GetProperty("hdrCapacityMax").GetDouble(), Math.Pow(2, gm.Metadata.AlternateHdrHeadroom), 2);
        // ultrahdr_app's decode in 32 x 32 block averages (it leaves the EXIF orientation alone)
        var blocks = TestUtil.LoadRef(path + ".blocks.ref");
        var mine = Jpeg.DecodeHdr(data, null, new JpegDecodeOptions { ApplyOrientation = false });
        int bw = blocks.Width, bh = blocks.Height;
        var f = (float[])mine.Data;
        var avg = new float[bw * bh * 3];
        for (int y = 0; y < bh * 32; y++)
            for (int x = 0; x < bw * 32; x++)
                for (int c = 0; c < 3; c++) avg[((y / 32) * bw + x / 32) * 3 + c] += f[((long)y * mine.Width + x) * 3 + c] / 1024;
        AssertClose(new RawImage(bw, bh, ColorModel.Rgb, (float[])blocks.Data), new RawImage(bw, bh, ColorModel.Rgb, avg), 0.1, 0.01, file);
        Assert.True(f.Max() > 1.5);
    }

    // ------------------------------------------------------------------ JPEG XL uses the same code

    [Fact]
    public void JxlGainMapUsesTheSharedPath()
    {
        var hdr = SyntheticHdr(40, 30);
        var sdr = SyntheticSdr(hdr);
        var gm = GainMap.Compute(sdr, hdr, new GainMapOptions { Scale = 2 });
        var gainFile = Jxl.Encode(gm.Image, new JxlEncodeOptions { Lossless = true });
        var file = JxlContainer.Signature.ToArray()
            .Concat(JxlSpecTests.Box("ftyp", "jxl \0\0\0\0jxl "u8.ToArray()))
            .Concat(JxlSpecTests.Box("jxlc", Jxl.Encode(sdr, new JxlEncodeOptions { Lossless = true })))
            .Concat(JxlSpecTests.GainMapBox(gm.Metadata.ToIso21496(), null, null, gainFile)).ToArray();
        Assert.True(Jxl.GetInfo(file).HasGainMap);
        var jg = Jxl.GetGainMap(file)!.ToGainMap()!;
        Assert.Equal(gm.Metadata.AlternateHdrHeadroom, jg.Metadata.AlternateHdrHeadroom, 6);
        TestUtil.AssertEqual(GainMap.Apply(sdr, gm), Jxl.DecodeHdr(file), 1e-4);
        TestUtil.AssertEqual(GainMap.Apply(sdr, gm, 1), ImageCodec.DecodeHdr(file, 1), 1e-4);
        Assert.False(Jxl.GetInfo(Jxl.Encode(sdr, new JxlEncodeOptions { Lossless = true })).HasGainMap);
    }

    /// <summary>A JPEG XL file of a white baseline with a gain map (jhgm box) and the given metadata.</summary>
    static byte[] JxlWithGainMap(RawImage baseline, RawImage gain, GainMapMetadata m, JxlColourEncoding? alternate = null, int gainBits = 0)
    {
        var b = Jxl.Encode(baseline, new JxlEncodeOptions { Lossless = true });
        return JxlContainer.Signature.ToArray()
            .Concat(JxlSpecTests.Box("ftyp", "jxl \0\0\0\0jxl "u8.ToArray()))
            .Concat(JxlSpecTests.Box("jxlc", JxlContainer.Parse(b).Codestream(b)))
            .Concat(JxlSpecTests.GainMapBox(m.ToIso21496(), alternate, null, Jxl.Encode(gain, new JxlEncodeOptions { Lossless = true, BitsPerSample = gainBits }))).ToArray();
    }

    /// <summary>
    /// JPEG XL colour encodings given by enums keep their primaries for the gain map (5.3.2 to 5.3.4, Annex B): a PQ
    /// BT.2100 baseline is linearized in BT.2020 (203 cd/m² = 1.0) and the result is labelled so; a P3 PQ alternate with
    /// use_base_colour_space 0 converts sRGB red to P3 (about 0.8225, 0.0332, 0.0171).
    /// </summary>
    [Fact]
    public void JxlEnumeratedColourEncodingsKeepTheirPrimaries()
    {
        var zero = new[] { 0.0, 0, 0 };
        var m = new GainMapMetadata { AlternateHdrHeadroom = 1, GainMapMax = zero, BaseOffset = zero, AlternateOffset = zero };
        var gain = new RawImage(1, 1, ColorModel.Gray, new byte[] { 0 });
        ushort pq203 = (ushort)Math.Round(PqInverseEotf(0.0203) * 65535);
        var pqBase = new RawImage(1, 1, ColorModel.Rgb, new[] { pq203, pq203, (ushort)0 }) { Metadata = new ImageMetadata { Transfer = TransferFunction.Pq } };
        var f1 = JxlWithGainMap(pqBase, gain, m);
        var baseline = Jxl.Decode(f1);
        Assert.Equal(TransferFunction.Pq, baseline.Metadata!.Transfer);
        Assert.Equal(new IccCicp(9, 16, 0, true), IccProfile.Parse(baseline.Metadata.IccProfile!).Cicp);
        var a = Jxl.DecodeHdr(f1);
        var r = (float[])a.Data;
        Assert.Equal((1, 1, 0), (Math.Round(r[0], 3), Math.Round(r[1], 3), Math.Round(r[2], 6)));
        Assert.Equal(IccProfile.LinearRec2020.RedColorant!.Value.X, IccProfile.Parse(a.Metadata!.IccProfile!).RedColorant!.Value.X, 1e-3);
        // the alternate's encoding (P3, PQ) gives the application space primaries
        var red = new RawImage(1, 1, ColorModel.Rgb, new byte[] { 255, 0, 0 });
        var alt = new GainMapMetadata { UseBaseColourSpace = false, AlternateHdrHeadroom = 1, GainMapMax = zero, BaseOffset = zero, AlternateOffset = zero };
        var f2 = JxlWithGainMap(red, gain, alt, new JxlColourEncoding { Primaries = JxlPrimaries.P3, Transfer = JxlTransfer.Pq });
        var p = (float[])Jxl.DecodeHdr(f2).Data;
        Assert.Equal(0.8225, p[0], 1e-3); Assert.Equal(0.0332, p[1], 1e-3); Assert.Equal(0.0171, p[2], 1e-3);
        // decoding and encoding again keeps the enumerated encoding
        var again = Jxl.Decode(Jxl.Encode(baseline, new JxlEncodeOptions { Lossless = true }));
        Assert.Equal((TransferFunction.Pq, new IccCicp(9, 16, 0, true)), (again.Metadata!.Transfer, IccProfile.Parse(again.Metadata.IccProfile!).Cicp));
        Assert.Throws<NotSupportedException>(() => ImageOps.ToSrgb(baseline));   // as without a profile: PQ is no ICC curve
    }

    /// <summary>
    /// A 10 bit gain map (5.2.3: the bit depth of its codestream) is normalized by 1023: code 1023 is max(G), 512 is
    /// 512 / 1023 of the range (formula 1).
    /// </summary>
    [Fact]
    public void JxlGainMapOfTenBits()
    {
        var white = new RawImage(2, 1, ColorModel.Rgb, new byte[] { 255, 255, 255, 255, 255, 255 });
        var zero = new[] { 0.0, 0, 0 };
        var m = new GainMapMetadata { AlternateHdrHeadroom = 2, GainMapMax = new[] { 2.0, 2, 2 }, BaseOffset = zero, AlternateOffset = zero };
        var file = JxlWithGainMap(white, new RawImage(2, 1, ColorModel.Gray, new ushort[] { 1023, 512 }), m, gainBits: 10);
        var gm = Jxl.GetGainMap(file)!.ToGainMap()!;
        Assert.Equal(SampleType.UInt16, gm.Image.SampleType);
        var r = (float[])Jxl.DecodeHdr(file).Data;
        Assert.Equal(4, r[0], 4);
        Assert.Equal(Math.Pow(2, 2 * 512 / 1023.0), r[3], 4);
    }
}
