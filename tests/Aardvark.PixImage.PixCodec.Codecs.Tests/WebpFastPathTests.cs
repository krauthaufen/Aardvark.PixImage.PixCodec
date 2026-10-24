using Aardvark.ImageCodecs;
using Xunit;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// The shortcuts of the WebP codec against the plain ways they replace: vector quantizer, token cost tables, the
/// batched bool writer, decoder state kept from one frame to the next on a thread, alpha written during the color
/// conversion.
/// </summary>
public unsafe class WebpFastPathTests
{
    static RawImage Photo(int w, int h, bool alpha, int seed)
    {
        var rng = new Random(seed);
        int ch = alpha ? 4 : 3;
        var d = new byte[w * h * ch];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < ch; c++)
                {
                    double v = c == 3 ? (x * 7 + y * 3) % 300 - 20 : 128 + 80 * Math.Sin(x / (7.0 + c) + seed) * Math.Cos(y / 9.0) + rng.Next(-12, 13);
                    d[(y * w + x) * ch + c] = (byte)Math.Clamp(v, 0, 255);
                }
        return new RawImage(w, h, alpha ? ColorModel.Rgba : ColorModel.Rgb, d);
    }

    static byte[] Encode(RawImage img, WebpEncodeOptions opt)
    {
        using var ms = new MemoryStream();
        Webp.Encode(img, ms, opt);
        return ms.ToArray();
    }

    [Fact]
    public void VectorQuantizerMatchesScalar()
    {
        var rng = new Random(7);
        int* coefs = stackalloc int[16];
        short* l1 = stackalloc short[16], l2 = stackalloc short[16], d1 = stackalloc short[16], d2 = stackalloc short[16];
        int[] steps = { 4, 5, 8, 13, 31, 60, 132, 157, 284, 314, 440 };
        for (int k = 0; k < 20000; k++)
        {
            int range = k % 4 == 0 ? 40 : k % 4 == 1 ? 400 : k % 4 == 2 ? 4000 : 33000;
            for (int i = 0; i < 16; i++) coefs[i] = rng.Next(4) == 0 ? 0 : rng.Next(-range, range + 1);
            int dcq = steps[rng.Next(steps.Length)], acq = steps[rng.Next(steps.Length)], first = rng.Next(2);
            // exact multiples of the step are where a rounding division would go wrong
            if (k % 5 == 0) for (int i = 0; i < 16; i++) coefs[i] = (coefs[i] / acq) * acq - (acq * 104 >> 8);
            int a = Vp8Encoder.QuantizeScalar(coefs, l1, d1, dcq, acq, first);
            int b = Vp8Encoder.QuantizeSimd(coefs, l2, d2, dcq, acq, first);
            if (!System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated) continue;
            Assert.Equal(a, b);
            Assert.True(new Span<short>(l1, 16).SequenceEqual(new Span<short>(l2, 16)), $"levels, case {k}");
            Assert.True(new Span<short>(d1, 16).SequenceEqual(new Span<short>(d2, 16)), $"dequantized, case {k}");
        }
    }

    static int Cost(int bit, int prob) => (int)Math.Round(-Math.Log2((bit != 0 ? 256 - prob : prob) is var p && p == 0 ? 1 / 256.0 : p / 256.0) * 256);

    /// <summary>The token tree walked coefficient by coefficient, as the encoder priced tokens before the tables.</summary>
    static int TreeCost(int type, int ctx, short[] levels, int first, int last)
    {
        var probs = Vp8Tables.DefaultCoeffProbs.ToArray();
        var bands = Vp8Tables.Bands.ToArray();
        int P(int band, int c, int i) => probs[((type * 8 + band) * 3 + c) * 11 + i];
        int n = first, band = bands[n], cx = ctx;
        if (last <= n) return Cost(0, P(band, cx, 0));
        int cost = Cost(1, P(band, cx, 0));
        while (n < 16)
        {
            int v = Math.Abs((int)levels[n++]);
            int b = band, c = cx;
            if (v == 0) { cost += Cost(0, P(b, c, 1)); band = bands[n]; cx = 0; continue; }
            cost += Cost(1, P(b, c, 1));
            if (v == 1) { cost += Cost(0, P(b, c, 2)); cx = 1; }
            else
            {
                cost += Cost(1, P(b, c, 2));
                if (v <= 4) { cost += Cost(0, P(b, c, 3)); cost += v == 2 ? Cost(0, P(b, c, 4)) : Cost(1, P(b, c, 4)) + Cost(v == 4 ? 1 : 0, P(b, c, 5)); }
                else if (v <= 10)
                {
                    cost += Cost(1, P(b, c, 3)) + Cost(0, P(b, c, 6));
                    cost += v <= 6 ? Cost(0, P(b, c, 7)) + Cost(v == 6 ? 1 : 0, 159) : Cost(1, P(b, c, 7)) + Cost(v >= 9 ? 1 : 0, 165) + Cost((v & 1) == 0 ? 1 : 0, 145);
                }
                else
                {
                    cost += Cost(1, P(b, c, 3)) + Cost(1, P(b, c, 6));
                    if (v < 19) cost += Cost(0, P(b, c, 8)) + Cost(0, P(b, c, 9)) + 3 * 256;
                    else if (v < 35) cost += Cost(0, P(b, c, 8)) + Cost(1, P(b, c, 9)) + 4 * 256;
                    else if (v < 67) cost += Cost(1, P(b, c, 8)) + Cost(0, P(b, c, 10)) + 5 * 256;
                    else cost += Cost(1, P(b, c, 8)) + Cost(1, P(b, c, 10)) + 11 * 256;
                }
                cx = 2;
            }
            band = bands[n];
            cost += 256;
            if (n == 16) break;
            if (n >= last) { cost += Cost(0, P(band, cx, 0)); break; }
            cost += Cost(1, P(band, cx, 0));
        }
        return cost;
    }

    [Fact]
    public void TokenCostTablesMatchTheTree()
    {
        var rng = new Random(11);
        var levels = new short[16];
        for (int k = 0; k < 20000; k++)
        {
            int max = k % 3 == 0 ? 3 : k % 3 == 1 ? 80 : 2047;
            for (int i = 0; i < 16; i++) levels[i] = rng.Next(3) == 0 ? (short)rng.Next(-max, max + 1) : (short)0;
            int first = rng.Next(2), type = rng.Next(4), ctx = rng.Next(3);
            int last = 0;
            for (int i = first; i < 16; i++) if (levels[i] != 0) last = i + 1;
            if (k % 7 == 0) last = first;
            fixed (short* p = levels)
                Assert.Equal(TreeCost(type, ctx, levels, first, last), Vp8Encoder.CoeffCost(type, ctx, p, first, last));
        }
    }

    [Fact]
    public void BoolWriterRoundTrips()
    {
        var rng = new Random(3);
        foreach (int n in new[] { 1, 7, 100, 5000, 200000 })
        {
            var bits = new int[n]; var probs = new int[n];
            var w = new Vp8BoolWriter();
            for (int i = 0; i < n; i++)
            {
                // runs of nearly certain decisions, which carry far, mixed with even ones
                probs[i] = rng.Next(4) == 0 ? 1 + rng.Next(255) : rng.Next(2) == 0 ? 255 : 1;
                bits[i] = rng.Next(256) >= probs[i] ? 1 : 0;
                if (rng.Next(50) == 0) bits[i] ^= 1;
                w.Put(bits[i], probs[i]);
            }
            var data = w.Finish();
            fixed (byte* p = data)
            {
                var r = new Vp8BoolReader(p, data.Length);
                for (int i = 0; i < n; i++) Assert.Equal(bits[i], r.GetBit(probs[i]));
            }
        }
    }

    /// <summary>Frames decoded one after the other on a thread share the decoders' buffers; none of it may show.</summary>
    [Fact]
    public void DecodersForgetThePreviousFrame()
    {
        var img = Photo(200, 150, false, 1);
        var files = new List<byte[]>
        {
            Encode(img, new WebpEncodeOptions { Quality = 50, Segments = 4, Vp8Syntax = new Vp8Syntax { SegmentDeltas = true, FilterDeltaIntra = 9, FilterDelta4x4 = -5 } }),
            Encode(Photo(64, 300, false, 2), new WebpEncodeOptions { Quality = 80, Segments = 1, Vp8Syntax = new Vp8Syntax { NoSkipFlags = true } }),
            Encode(img, new WebpEncodeOptions { Quality = 30, Segments = 4, Vp8Syntax = new Vp8Syntax { NoSegmentData = true } }),
            Encode(Photo(300, 40, true, 3), new WebpEncodeOptions { Quality = 70 }),
            Encode(Photo(130, 90, true, 4), new WebpEncodeOptions { Lossless = true }),
            Encode(Photo(33, 17, true, 5), new WebpEncodeOptions { Lossless = true, Method = 0 }),
            Encode(img, new WebpEncodeOptions { Quality = 90, Partitions = 4 }),
        };
        var single = new WebpDecodeOptions { Multithreaded = false };
        // each file alone on a thread of its own
        var fresh = files.Select(f =>
        {
            RawImage? res = null;
            var t = new Thread(() => res = Webp.Decode(f, single));
            t.Start(); t.Join();
            return res!;
        }).ToList();
        // lossless data cut in half: the decoder fails in the middle of the pixels
        var entry = WebpContainer.Parse(files[4]).Frames[0];
        var half = files[4].AsSpan(entry.DataOffset, entry.DataLength / 2).ToArray();
        for (int round = 0; round < 3; round++)
            for (int i = 0; i < files.Count; i++)
            {
                AssertEqual(fresh[i], Webp.Decode(files[i], single));
                // a decode that fails half way must not leave anything behind either
                if (i % 3 == 1) Assert.Throws<ImageFormatException>(() => Vp8lDecoder.Decode(half, out _, out _, 1L << 28, out _));
            }
    }

    [Fact]
    public void LossyAlphaAtOddWidths()
    {
        foreach (var (w, h) in new[] { (1, 1), (15, 3), (16, 16), (17, 9), (31, 33), (47, 5) })
        {
            var img = Photo(w, h, true, w);
            var back = Webp.Decode(Encode(img, new WebpEncodeOptions { Quality = 75 }));
            var s = (byte[])img.Data; var d = (byte[])back.Data;
            Assert.Equal(ColorModel.Rgba, back.Color);
            for (int i = 3; i < s.Length; i += 4) Assert.Equal(s[i], d[i]);
            // and the color channels are those of the opaque decode
            var rgb = Webp.Decode(Encode(new RawImage(w, h, ColorModel.Rgb, Enumerable.Range(0, w * h * 3).Select(i => s[i / 3 * 4 + i % 3]).ToArray()), new WebpEncodeOptions { Quality = 75 }));
            var c = (byte[])rgb.Data;
            for (int i = 0; i < w * h; i++)
                for (int k = 0; k < 3; k++) Assert.Equal(c[3 * i + k], d[4 * i + k]);
        }
    }
}
