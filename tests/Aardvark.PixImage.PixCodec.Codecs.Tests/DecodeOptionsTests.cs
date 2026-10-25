using System.Threading;
using Xunit;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>Truncation, pixel limits, cancellation and the format independent decode options.</summary>
public class DecodeOptionsTests
{
    static RawImage Photo(int w, int h, int channels = 3)
    {
        var img = RawImage.Create(w, h, RawImage.DefaultColorModel(channels), SampleType.UInt8, channels);
        var b = img.GetData<byte>();
        for (int y = 0, i = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < channels; c++, i++)
                    b[i] = (byte)(128 + 100 * Math.Sin(x * 0.05 + c) * Math.Cos(y * 0.07) + (x * y + c) % 13);
        return img;
    }

    static RawImage AsFloat(RawImage img)
        => new RawImage(img.Width, img.Height, img.Color, Array.ConvertAll(img.GetData<byte>(), v => v / 255f), img.Channels);

    static byte[] Encode(ImageFormat f, RawImage img) => f switch
    {
        ImageFormat.Jpeg => Jpeg.Encode(img, new JpegEncodeOptions { Quality = 85 }),
        ImageFormat.Png => Png.Encode(img),
        ImageFormat.Tiff => Tiff.Encode(img, new TiffEncodeOptions { Compression = TiffCompression.Deflate }),
        ImageFormat.Exr => Exr.Encode(AsFloat(img)),
        _ => ImageCodec.Encode(img, f),
    };

    [Theory]
    [InlineData(false, 0.9)]
    [InlineData(false, 0.6)]
    [InlineData(true, 0.9)]
    [InlineData(true, 0.6)]
    [InlineData(false, 0.9999)]
    public void TruncatedJpegThrowsUnlessAllowed(bool progressive, double keep)
    {
        var full = Jpeg.Encode(Photo(256, 128), new JpegEncodeOptions { Quality = 85, Progressive = progressive });
        var cut = full.AsSpan(0, (int)(full.Length * keep)).ToArray();
        if (cut.Length == full.Length) cut = cut[..^1];
        var ex = Assert.Throws<ImageFormatException>(() => Jpeg.Decode(cut));
        Assert.Contains("EOI", ex.Message);
        Assert.Throws<ImageFormatException>(() => ImageCodec.Decode(cut));
        var partial = Jpeg.Decode(cut, new JpegDecodeOptions { AllowTruncated = true });
        Assert.True(partial.Metadata!.Truncated);
        Assert.Equal((256, 128), (partial.Width, partial.Height));
        Assert.True(ImageCodec.Decode(cut, new ImageDecodeOptions { AllowTruncated = true }).Metadata!.Truncated);
        // the complete file is not flagged
        Assert.False(Jpeg.Decode(full).Metadata?.Truncated ?? false);
    }

    [Theory]
    [InlineData(ImageFormat.Jpeg)]
    [InlineData(ImageFormat.Png)]
    [InlineData(ImageFormat.Tiff)]
    [InlineData(ImageFormat.Webp)]
    [InlineData(ImageFormat.Bmp)]
    [InlineData(ImageFormat.Exr)]
    public void MaxPixelsRejectsLargerImages(ImageFormat f)
    {
        var data = Encode(f, Photo(64, 48));
        var ex = Assert.Throws<ImageNotSupportedException>(() => ImageCodec.Decode(data, new ImageDecodeOptions { MaxPixels = 64 * 48 - 1 }));
        Assert.Contains("MaxPixels", ex.Message);
        Assert.IsAssignableFrom<ImageCodecException>(ex);
        Assert.Equal(64, ImageCodec.Decode(data, new ImageDecodeOptions { MaxPixels = 64 * 48 }).Width);
    }

    [Fact]
    public void MaxPixelsOfTheFormatOptions()
    {
        var img = Photo(64, 48);
        var jpeg = Jpeg.Encode(img);
        Assert.Throws<ImageNotSupportedException>(() => Jpeg.Decode(jpeg, new JpegDecodeOptions { MaxPixels = 1000 }));
        // the limit applies to the decoded size, after scaling
        Assert.Equal(16, Jpeg.Decode(jpeg, new JpegDecodeOptions { MaxPixels = 16 * 12, ScaleDenominator = 4 }).Width);
        var png = Png.Encode(img);
        Assert.Throws<ImageNotSupportedException>(() => Png.Decode(png, new PngDecodeOptions { MaxPixels = 1000 }));
        Assert.Throws<ImageNotSupportedException>(() => Png.DecodeAnimation(png, new PngDecodeOptions { MaxPixels = 1000 }));
        var tiff = Tiff.Encode(img);
        Assert.Throws<ImageNotSupportedException>(() => Tiff.Decode(tiff, new TiffDecodeOptions { MaxPixels = 1000 }));
        // a region within the limit decodes from a larger image
        Assert.Equal(20, Tiff.DecodeRegion(tiff, 0, 0, 20, 20, new TiffDecodeOptions { MaxPixels = 400 }).Width);
    }

    [Theory]
    [InlineData(ImageFormat.Jpeg)]
    [InlineData(ImageFormat.Png)]
    [InlineData(ImageFormat.Tiff)]
    [InlineData(ImageFormat.Webp)]
    public void GenericOptionsCanKeepTheStoredOrientation(ImageFormat f)
    {
        var src = Photo(40, 24);
        src.Metadata = new ImageMetadata { Orientation = 6 };
        var data = Encode(f, src);
        var upright = ImageCodec.Decode(data);
        Assert.Equal((24, 40), (upright.Width, upright.Height));
        var stored = ImageCodec.Decode(data, new ImageDecodeOptions { ApplyOrientation = false });
        Assert.Equal((40, 24), (stored.Width, stored.Height));
        Assert.Equal(6, stored.Metadata!.Orientation);
        var info = ImageCodec.GetInfo(data, new ImageDecodeOptions { ApplyOrientation = false });
        Assert.Equal((40, 24, false), (info.Width, info.Height, info.OrientationApplied));
        Assert.Equal((24, 40), (ImageCodec.GetInfo(data).Width, ImageCodec.GetInfo(data).Height));
    }

    [Fact]
    public void CancelledDecodesAndEncodesThrow()
    {
        var img = Photo(512, 256);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var t = cts.Token;
        var jpeg = Jpeg.Encode(img);
        var prog = Jpeg.Encode(img, new JpegEncodeOptions { Progressive = true });
        var png = Png.Encode(img);
        var tiff = Tiff.Encode(img, new TiffEncodeOptions { RowsPerStrip = 16 });
        Assert.ThrowsAny<OperationCanceledException>(() => Jpeg.Decode(jpeg, new JpegDecodeOptions { CancellationToken = t }));
        Assert.ThrowsAny<OperationCanceledException>(() => Jpeg.Decode(prog, new JpegDecodeOptions { CancellationToken = t }));
        Assert.ThrowsAny<OperationCanceledException>(() => Png.Decode(png, new PngDecodeOptions { CancellationToken = t }));
        Assert.ThrowsAny<OperationCanceledException>(() => Tiff.Decode(tiff, new TiffDecodeOptions { CancellationToken = t }));
        Assert.ThrowsAny<OperationCanceledException>(() => Tiff.Decode(tiff, new TiffDecodeOptions { CancellationToken = t, Parallel = false }));
        Assert.ThrowsAny<OperationCanceledException>(() => ImageCodec.Decode(jpeg, new ImageDecodeOptions { CancellationToken = t }));
        Assert.ThrowsAny<OperationCanceledException>(() => Jpeg.Encode(img, new JpegEncodeOptions { CancellationToken = t }));
        Assert.ThrowsAny<OperationCanceledException>(() => Jpeg.Encode(img, new JpegEncodeOptions { CancellationToken = t, OptimizeHuffman = false }));
        Assert.ThrowsAny<OperationCanceledException>(() => Png.Encode(img, new PngEncodeOptions { CancellationToken = t }));
        Assert.ThrowsAny<OperationCanceledException>(() => Tiff.Encode(img, new TiffEncodeOptions { CancellationToken = t, RowsPerStrip = 16 }));
        var webp = Webp.Encode(img, new WebpEncodeOptions { Quality = 80 });
        Assert.ThrowsAny<OperationCanceledException>(() => Webp.Decode(webp, new WebpDecodeOptions { CancellationToken = t }));
        Assert.ThrowsAny<OperationCanceledException>(() => Webp.Decode(Webp.Encode(img, new WebpEncodeOptions { Lossless = true }), new WebpDecodeOptions { CancellationToken = t }));
        Assert.ThrowsAny<OperationCanceledException>(() => Webp.Encode(img, new WebpEncodeOptions { CancellationToken = t }));
        Assert.ThrowsAny<OperationCanceledException>(() => Webp.Encode(img, new WebpEncodeOptions { CancellationToken = t, Lossless = true }));
        Assert.ThrowsAny<OperationCanceledException>(() => ImageCodec.Decode(webp, new ImageDecodeOptions { CancellationToken = t }));
        // a token that is not cancelled changes nothing
        var live = new CancellationTokenSource().Token;
        Assert.Equal(Jpeg.Decode(jpeg).GetData<byte>(), Jpeg.Decode(jpeg, new JpegDecodeOptions { CancellationToken = live }).GetData<byte>());
        Assert.Equal(jpeg, Jpeg.Encode(img, new JpegEncodeOptions { CancellationToken = live }));
    }

    // a cancelled WebP encode stops before converting the pixels (the ARGB copy alone is 4 MiB here)
    [Fact]
    public void WebpEncodeChecksCancellationFirst()
    {
        var img = Photo(1024, 1024);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        foreach (var lossless in new[] { false, true })
        {
            var o = new WebpEncodeOptions { Lossless = lossless, CancellationToken = cts.Token };
            Assert.ThrowsAny<OperationCanceledException>(() => Webp.Encode(img, o));
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.ThrowsAny<OperationCanceledException>(() => Webp.Encode(img, o));
            long used = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(used < 1 << 20, $"lossless {lossless}: {used} bytes allocated");
        }
    }

    // the colour conversion before the first macroblock row is about a fifth of a lossy encode
    [Fact]
    public void WebpLossyEncodeCancelsBeforeTheMacroblocks()
    {
        var img = Photo(2048, 2048);
        AssertCancelsEarly(ct => Webp.Encode(img, new WebpEncodeOptions { Quality = 88, CancellationToken = ct }), 40, 10);
    }

    [Fact]
    public void WebpLosslessDecodeCancelsWhileRunning()
    {
        var data = Webp.Encode(Photo(2048, 2048), new WebpEncodeOptions { Lossless = true, Method = 0 });
        AssertCancelsEarly(ct => Webp.Decode(data, new WebpDecodeOptions { CancellationToken = ct }), 10, 2);
    }

    [Fact]
    public void WebpLosslessEncodeCancelsWhileRunning()
    {
        var img = Photo(1024, 1024);
        AssertCancelsEarly(ct => Webp.Encode(img, new WebpEncodeOptions { Lossless = true, CancellationToken = ct }), 10, 2);
    }

    /// <summary>
    /// Cancelled at 1 / <paramref name="cancelAt"/> of its uncancelled time T (the best of two runs after a warm-up), the
    /// work throws OperationCanceledException before T / <paramref name="endBy"/>: checks only before and after a long
    /// stretch of work let it run on to the end of that stretch.
    /// </summary>
    static void AssertCancelsEarly(Action<CancellationToken> work, int cancelAt, int endBy)
    {
        work(CancellationToken.None);
        double full = double.MaxValue;
        for (int i = 0; i < 2; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            work(CancellationToken.None);
            full = Math.Min(full, sw.Elapsed.TotalMilliseconds);
        }
        using var cts = new CancellationTokenSource();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var canceller = new Thread(() =>
        {
            while (clock.Elapsed.TotalMilliseconds < full / cancelAt) Thread.SpinWait(100);
            cts.Cancel();
        }) { IsBackground = true };
        canceller.Start();
        Assert.ThrowsAny<OperationCanceledException>(() => work(cts.Token));
        double ended = clock.Elapsed.TotalMilliseconds;
        canceller.Join();
        Assert.True(ended < full / endBy, $"cancelled at {full / cancelAt:F1} ms, ended at {ended:F1} ms, uncancelled {full:F1} ms");
    }

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(3, 1, false)]
    [InlineData(3, 6, false)]
    [InlineData(3, 3, true)]
    [InlineData(1, 8, true)]
    public void JpegDecodesStraightToRgba(int channels, int orientation, bool progressive)
    {
        var src = Photo(70, 45, channels);
        src.Metadata = new ImageMetadata { Orientation = orientation };
        var data = Jpeg.Encode(src, new JpegEncodeOptions { Progressive = progressive });
        var plain = Jpeg.Decode(data);
        var rgba = Jpeg.Decode(data, new JpegDecodeOptions { Rgba = true });
        Assert.Equal((ColorModel.Rgba, 4, plain.Width, plain.Height), (rgba.Color, rgba.Channels, rgba.Width, rgba.Height));
        var p = plain.GetData<byte>(); var q = rgba.GetData<byte>();
        for (int i = 0; i < plain.Width * plain.Height; i++)
        {
            for (int c = 0; c < 3; c++) Assert.Equal(p[i * channels + (channels == 1 ? 0 : c)], q[i * 4 + c]);
            Assert.Equal(255, q[i * 4 + 3]);
        }
        Assert.Equal(q, ImageCodec.Decode(data, new ImageDecodeOptions { Channels = ImageChannels.Rgba }).GetData<byte>());
        // 12 bit samples: alpha is the precision's maximum
        var deep = new RawImage(20, 10, ColorModel.Rgb, Array.ConvertAll(Photo(20, 10, 3).GetData<byte>(), v => (ushort)(v * 16)));
        var d = Jpeg.Decode(Jpeg.Encode(deep, new JpegEncodeOptions { Precision = 12 }), new JpegDecodeOptions { Rgba = true });
        Assert.Equal(4095, d.GetData<ushort>()[3]);
    }

    [Fact]
    public void TwelveBitJpegTo8BitUsesItsOwnRange()
    {
        var deep = new RawImage(20, 10, ColorModel.Gray, Enumerable.Repeat((ushort)4095, 200).ToArray());
        var data = Jpeg.Encode(deep, new JpegEncodeOptions { Precision = 12, Quality = 100 });
        var b = ImageCodec.Decode(data, new ImageDecodeOptions { SampleType = SampleType.UInt8 });
        Assert.Equal(SampleType.UInt8, b.SampleType);
        Assert.All(b.GetData<byte>(), v => Assert.InRange(v, 254, 255));
    }

    [Fact]
    public void GenericChannelsAndSampleType()
    {
        var src = RawImage.Create(30, 20, ColorModel.Rgb, SampleType.UInt16);
        var w = src.GetData<ushort>();
        for (int i = 0; i < w.Length; i++) w[i] = (ushort)(i * 997);
        var png = Png.Encode(src);
        var b = ImageCodec.Decode(png, new ImageDecodeOptions { SampleType = SampleType.UInt8, Channels = ImageChannels.Rgba });
        Assert.Equal((SampleType.UInt8, ColorModel.Rgba), (b.SampleType, b.Color));
        var d = b.GetData<byte>();
        for (int i = 0; i < 600; i++)
        {
            for (int c = 0; c < 3; c++) Assert.Equal(Math.Round(w[i * 3 + c] / 257.0, MidpointRounding.AwayFromZero), d[i * 4 + c]);
            Assert.Equal(255, d[i * 4 + 3]);
        }
        var info = ImageCodec.GetInfo(png, new ImageDecodeOptions { SampleType = SampleType.UInt8, Channels = ImageChannels.Gray });
        Assert.Equal((SampleType.UInt8, ColorModel.Gray, 1), (info.SampleType, info.Color, info.Channels));
        Assert.Equal(ColorModel.Gray, ImageCodec.Decode(png, new ImageDecodeOptions { Channels = ImageChannels.Gray }).Color);
    }

    [Fact]
    public void EncodersWriteByteArrays()
    {
        var img = Photo(16, 8);
        Assert.Equal(16, Jpeg.Decode(Jpeg.Encode(img)).Width);
        Assert.Equal(16, Tiff.Decode(Tiff.Encode(img)).Width);
        Assert.Equal(16, Gif.Decode(Gif.Encode(img)).Width);
        Assert.Equal(16, Exr.Decode(Exr.Encode(AsFloat(img))).Width);
        Assert.Equal(16, Webp.Decode(Webp.Encode(img)).Width);
    }

    [Fact]
    public void ExceptionsShareABase()
    {
        Assert.Throws<ImageFormatException>(() => ImageCodec.Decode(new byte[] { 1, 2, 3 }));
        try { ImageCodec.Decode(new byte[] { 1, 2, 3 }); }
        catch (ImageCodecException) { return; }
        Assert.Fail("not an ImageCodecException");
    }
}
