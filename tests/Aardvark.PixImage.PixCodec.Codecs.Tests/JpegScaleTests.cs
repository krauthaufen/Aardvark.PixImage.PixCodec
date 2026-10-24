using Xunit;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>Decoding JPEG at k/8 of its size.</summary>
public class JpegScaleTests
{
    static RawImage Photo(int w, int h, int channels)
    {
        var img = RawImage.Create(w, h, RawImage.DefaultColorModel(channels), SampleType.UInt8, channels);
        var b = img.GetData<byte>();
        for (int y = 0, i = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < channels; c++, i++)
                    b[i] = (byte)(128 + 90 * Math.Sin(x * 0.11 + 2 * c) * Math.Cos(y * 0.09 - c) + (x ^ y) % 17);
        return img;
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (int k in new[] { 1, 2, 3, 4, 5, 6, 7, 8 })
            foreach (var (ch, ss) in new[] { (1, JpegSubsampling.Chroma444), (3, JpegSubsampling.Chroma444), (3, JpegSubsampling.Chroma420), (3, JpegSubsampling.Chroma422) })
                foreach (bool progressive in new[] { false, true })
                    yield return new object[] { k, ch, ss, progressive };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EighthsMatchABoxShrinkOfTheFullDecode(int k, int channels, JpegSubsampling ss, bool progressive)
    {
        // multiples of 16, so that the box shrink below has exactly the scale k/8 (other sizes are covered by the size check)
        int w = 208, h = 112;
        Assert.Equal(((203 * k + 7) / 8, (117 * k + 7) / 8), Size(Jpeg.Decode(Jpeg.Encode(Photo(203, 117, channels), new JpegEncodeOptions { Subsampling = ss }), new JpegDecodeOptions { ScaleEighths = k })));
        var data = Jpeg.Encode(Photo(w, h, channels), new JpegEncodeOptions { Quality = 92, Subsampling = ss, Progressive = progressive });
        var o = new JpegDecodeOptions { ScaleEighths = k };
        var small = Jpeg.Decode(data, o);
        int sw = (w * k + 7) / 8, sh = (h * k + 7) / 8;
        Assert.Equal((sw, sh), (small.Width, small.Height));
        var info = Jpeg.GetInfo(data, o);
        Assert.Equal((sw, sh), (info.Width, info.Height));
        if (k is 1 or 2 or 4 or 8)
        {
            Assert.Equal(Jpeg.Decode(data, new JpegDecodeOptions { ScaleDenominator = 8 / k }).GetData<byte>(), small.GetData<byte>());
            if (k != 8) return;
        }
        // the box average of the full decode over the same area: equal up to rounding where every component is at full
        // resolution, close where chroma is upsampled after the reduced transform instead of before it
        var full = Jpeg.Decode(data);
        var box = AreaAverage(full, sw, sh);
        var a = small.GetData<byte>(); var b = box.GetData<byte>();
        int inner = 0, maxDiff = 0; double sum = 0;
        for (int y = 0; y < sh; y++)
            for (int x = 0; x < sw * channels; x++)
            {
                int d = Math.Abs(a[y * sw * channels + x] - b[y * sw * channels + x]);
                maxDiff = Math.Max(maxDiff, d); sum += d; inner++;
            }
        double mean = sum / inner;
        // (colour conversion after the averaging instead of before it rounds differently)
        if (channels == 1 || ss == JpegSubsampling.Chroma444) Assert.True(maxDiff <= channels, $"max difference {maxDiff}");
        else Assert.True(mean < 2.5 && maxDiff <= 40, $"mean difference {mean:F2}, max {maxDiff}");
    }

    static (int, int) Size(RawImage i) => (i.Width, i.Height);

    /// <summary>Exact area average (pixels weighted by how much of them an output pixel covers), rounded.</summary>
    static RawImage AreaAverage(RawImage img, int w, int h)
    {
        int c = img.Channels;
        var src = img.GetData<byte>();
        var dst = new byte[w * h * c];
        double fx = (double)img.Width / w, fy = (double)img.Height / h;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int ch = 0; ch < c; ch++)
                {
                    double sum = 0, wsum = 0;
                    for (int sy = (int)(y * fy); sy < Math.Min(img.Height, (int)Math.Ceiling((y + 1) * fy)); sy++)
                    {
                        double wy = Math.Min((y + 1) * fy, sy + 1) - Math.Max(y * fy, sy);
                        for (int sx = (int)(x * fx); sx < Math.Min(img.Width, (int)Math.Ceiling((x + 1) * fx)); sx++)
                        {
                            double wx = Math.Min((x + 1) * fx, sx + 1) - Math.Max(x * fx, sx);
                            sum += wx * wy * src[(sy * img.Width + sx) * c + ch];
                            wsum += wx * wy;
                        }
                    }
                    dst[(y * w + x) * c + ch] = (byte)Math.Round(sum / wsum);
                }
        return new RawImage(w, h, img.Color, dst, c);
    }

    [Fact]
    public void BadScalesAreRejected()
    {
        var data = Jpeg.Encode(Photo(16, 16, 1));
        Assert.Throws<ArgumentException>(() => Jpeg.Decode(data, new JpegDecodeOptions { ScaleEighths = 9 }));
        Assert.Throws<ArgumentException>(() => Jpeg.Decode(data, new JpegDecodeOptions { ScaleEighths = -1 }));
    }
}
