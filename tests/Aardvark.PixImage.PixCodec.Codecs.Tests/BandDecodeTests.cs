using Xunit;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>Band-wise decoding gives the rows of a full decode (as stored), in order, in bands of the requested height.</summary>
public class BandDecodeTests
{
    static RawImage Photo(int w, int h, int channels, SampleType type = SampleType.UInt8)
    {
        var img = RawImage.Create(w, h, RawImage.DefaultColorModel(channels), type, channels);
        int n = w * h * channels;
        for (int i = 0; i < n; i++)
        {
            int x = i / channels % w, y = i / channels / w, c = i % channels;
            double v = 0.5 + 0.4 * Math.Sin(x * 0.07 + c) * Math.Cos(y * 0.05) + (x * 7 + y * 3) % 11 / 100.0;
            if (type == SampleType.UInt8) ((byte[])img.Data)[i] = (byte)(v * 255);
            else ((ushort[])img.Data)[i] = (ushort)(v * 65535);
        }
        return img;
    }

    /// <summary>Joins the bands and checks their sizes and order.</summary>
    static RawImage Collect(Func<Action<RawImage, int>, ImageMetadata?> decode, int bandHeight, int height)
    {
        RawImage? result = null;
        int next = 0;
        decode((band, y) =>
        {
            Assert.Equal(next, y);
            Assert.Equal(Math.Min(bandHeight, height - y), band.Height);
            result ??= RawImage.Create(band.Width, height, band.Color, band.SampleType, band.Channels);
            int row = band.Width * band.Channels;
            Array.Copy(band.Data, 0, result.Data, (long)y * row, (long)band.Height * row);
            next += band.Height;
        });
        Assert.Equal(height, next);
        return result!;
    }

    static void AssertSame(RawImage expected, RawImage actual)
    {
        Assert.Equal((expected.Width, expected.Height, expected.Channels, expected.Color, expected.SampleType),
            (actual.Width, actual.Height, actual.Channels, actual.Color, actual.SampleType));
        Assert.True(expected.GetBytes().SequenceEqual(actual.GetBytes()), "pixels differ");
    }

    public static IEnumerable<object[]> JpegCases()
    {
        foreach (int bh in new[] { 1, 7, 16, 64, 1000 })
            foreach (var kind in new[] { "gray", "444", "420", "progressive", "scaled5", "rgba", "12bit", "oriented" })
                yield return new object[] { kind, bh };
    }

    [Theory]
    [MemberData(nameof(JpegCases))]
    public void JpegBandsMatchTheFullDecode(string kind, int bandHeight)
    {
        int w = 213, h = 141;
        var src = kind switch
        {
            "gray" => Photo(w, h, 1),
            "12bit" => new RawImage(w, h, ColorModel.Rgb, Array.ConvertAll(Photo(w, h, 3, SampleType.UInt16).GetData<ushort>(), v => (ushort)(v >> 4))),
            _ => Photo(w, h, 3),
        };
        if (kind == "oriented") src.Metadata = new ImageMetadata { Orientation = 6 };
        var data = Jpeg.Encode(src, new JpegEncodeOptions
        {
            Subsampling = kind == "444" ? JpegSubsampling.Chroma444 : JpegSubsampling.Chroma420,
            Progressive = kind == "progressive",
            Precision = kind == "12bit" ? 12 : 0,
        });
        var o = new JpegDecodeOptions { ApplyOrientation = false, ScaleEighths = kind == "scaled5" ? 5 : 0, Rgba = kind == "rgba" };
        var full = Jpeg.Decode(data, o);
        var bands = Collect(cb => Jpeg.DecodeBands(data, bandHeight, cb, o), bandHeight, full.Height);
        AssertSame(full, bands);
        // the default options too (orientation is never applied to bands)
        if (kind == "oriented")
        {
            var meta = Jpeg.DecodeBands(data, bandHeight, (_, _) => { });
            Assert.Equal(6, meta!.Orientation);
        }
    }

    public static IEnumerable<object[]> PngCases()
    {
        foreach (int bh in new[] { 1, 5, 32, 1000 })
            foreach (var kind in new[] { "rgb8", "rgba16", "gray8", "palette", "interlaced" })
                yield return new object[] { kind, bh };
    }

    [Theory]
    [MemberData(nameof(PngCases))]
    public void PngBandsMatchTheFullDecode(string kind, int bandHeight)
    {
        int w = 97, h = 83;
        var src = kind switch
        {
            "rgba16" => Photo(w, h, 4, SampleType.UInt16),
            "gray8" => Photo(w, h, 1),
            "palette" => Quantized(Photo(w, h, 3)),
            _ => Photo(w, h, 3),
        };
        var data = Png.Encode(src, new PngEncodeOptions { Interlace = kind == "interlaced", AutoPalette = kind == "palette" });
        var full = Png.Decode(data);
        AssertSame(full, Collect(cb => Png.DecodeBands(data, bandHeight, cb), bandHeight, h));
        AssertSame(full, Collect(cb => ImageCodec.DecodeBands(data, bandHeight, cb), bandHeight, h));
    }

    static RawImage Quantized(RawImage img)
    {
        var b = img.GetData<byte>();
        for (int i = 0; i < b.Length; i++) b[i] = (byte)(b[i] & 0xC0);
        return img;
    }

    [Fact]
    public void OtherFormatsAreCut()
    {
        var src = Photo(50, 37, 3);
        src.Metadata = new ImageMetadata { Orientation = 6 };
        var tiff = Tiff.Encode(src);
        var stored = Tiff.Decode(tiff, new TiffDecodeOptions { ApplyOrientation = false });
        AssertSame(stored, Collect(cb => ImageCodec.DecodeBands(tiff, 8, cb), 8, 37));
        var rgba = Collect(cb => ImageCodec.DecodeBands(Jpeg.Encode(Photo(50, 37, 1)), 8, cb, new ImageDecodeOptions { Channels = ImageChannels.Rgba }), 8, 37);
        Assert.Equal(ColorModel.Rgba, rgba.Color);
    }

    [Fact]
    public void JpegBandsNeedLittleMemory()
    {
        var data = Jpeg.Encode(Photo(2048, 2048, 3), new JpegEncodeOptions { Quality = 85 });
        Jpeg.DecodeBands(data, 16, (_, _) => { });
        long before = GC.GetAllocatedBytesForCurrentThread();
        Jpeg.DecodeBands(data, 16, (_, _) => { });
        long used = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(used < 2048 * 2048 * 3 / 20, $"{used} bytes allocated");
    }

    [Fact]
    public void TruncatedJpegThrowsAfterTheBandsItHas()
    {
        var full = Jpeg.Encode(Photo(128, 128, 3));
        var cut = full[..(full.Length * 2 / 3)];
        int rows = 0;
        Assert.Throws<ImageFormatException>(() => Jpeg.DecodeBands(cut, 16, (b, _) => rows += b.Height));
        Assert.Equal(128, rows);
        var meta = Jpeg.DecodeBands(cut, 16, (_, _) => { }, new JpegDecodeOptions { AllowTruncated = true });
        Assert.True(meta!.Truncated);
    }
}
