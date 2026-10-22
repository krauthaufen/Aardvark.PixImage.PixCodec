using Xunit;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// Decoders allocate about their output: no second full copy of the pixels, no per-chunk buffers that add up to one.
/// Large working buffers come from the shared array pool, so the second decode is measured. Counted on the current
/// thread, so multithreaded decoding is switched off.
/// </summary>
public class AllocationTests
{
    const int W = 1024, H = 768;

    /// <summary>A smooth photo-like RGB(A) image with some noise.</summary>
    static RawImage Photo(int channels, SampleType type = SampleType.UInt8)
    {
        var rnd = new Random(3);
        var img = RawImage.Create(W, H, channels == 4 ? ColorModel.Rgba : ColorModel.Rgb, type, channels);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                for (int c = 0; c < channels; c++)
                {
                    double v = c == 3 ? 0.5 + 0.4 * Math.Sin(x * 0.01) : 0.5 + 0.3 * Math.Sin(x * 0.013 * (c + 1) + y * 0.007) + rnd.NextDouble() * 0.05;
                    int i = (y * W + x) * channels + c;
                    if (type == SampleType.Float32) img.GetData<float>()[i] = (float)(v * 4);
                    else img.GetData<byte>()[i] = (byte)Math.Clamp(v * 255, 0, 255);
                }
        return img;
    }

    static byte[] Encode(Action<Stream> encode)
    {
        using var ms = new MemoryStream();
        encode(ms);
        return ms.ToArray();
    }

    static void AssertAboutOutput(Func<RawImage> decode, double factor)
    {
        decode();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var img = decode();
        long used = GC.GetAllocatedBytesForCurrentThread() - before;
        long output = (long)img.Data.Length * RawImage.BytesPerSample(img.SampleType);
        Assert.True(used < output * factor, $"{used} bytes allocated for {output} bytes of pixels");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tga(bool rle)
    {
        var data = Encode(s => ImageCodecs.Tga.Encode(Photo(3), s, new TgaEncodeOptions { Rle = rle }));
        AssertAboutOutput(() => ImageCodecs.Tga.Decode(data), 1.1);
    }

    [Fact]
    public void Hdr()
    {
        var data = Encode(s => ImageCodecs.Hdr.Encode(Photo(3, SampleType.Float32), s));
        AssertAboutOutput(() => ImageCodecs.Hdr.Decode(data), 1.1);
    }

    // (every strip of this encoder has its own optimized Huffman tables, some 20 KB to build per strip)
    [Fact]
    public void TiffJpegStrips()
    {
        var data = Encode(s => ImageCodecs.Tiff.Encode(Photo(3), s, new TiffEncodeOptions { Compression = TiffCompression.Jpeg, RowsPerStrip = 128, Parallel = false }));
        AssertAboutOutput(() => ImageCodecs.Tiff.Decode(data, new TiffDecodeOptions { Parallel = false }), 1.15);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void WebpLossless(int channels)
    {
        var data = Encode(s => ImageCodecs.Webp.Encode(Photo(channels), s, new WebpEncodeOptions { Lossless = true }));
        AssertAboutOutput(() => ImageCodecs.Webp.Decode(data, new WebpDecodeOptions { Multithreaded = false }), 1.15);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void WebpLossy(int channels)
    {
        var data = Encode(s => ImageCodecs.Webp.Encode(Photo(channels), s, new WebpEncodeOptions { Quality = 80 }));
        AssertAboutOutput(() => ImageCodecs.Webp.Decode(data, new WebpDecodeOptions { Multithreaded = false }), 1.15);
    }

    // all resolution levels of a component share one buffer
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 0)]
    [InlineData(false, 256)]
    public void Jp2(bool lossless, int tile)
    {
        var data = Encode(s => ImageCodecs.Jp2.Encode(Photo(3), s, new Jp2EncodeOptions { Lossless = lossless, TileWidth = tile, TileHeight = tile }));
        AssertAboutOutput(() => ImageCodecs.Jp2.Decode(data, new Jp2DecodeOptions { Parallel = false }), 1.3);
    }

    [Theory]
    [InlineData(ExrCompression.Piz)]
    [InlineData(ExrCompression.Dwaa)]
    [InlineData(ExrCompression.Dwab)]
    public void Exr(ExrCompression compression)
    {
        var data = Encode(s => ImageCodecs.Exr.Encode(Photo(3, SampleType.Float32), s, new ExrEncodeOptions { Compression = compression, PixelType = ExrPixelType.Half }));
        AssertAboutOutput(() => ImageCodecs.Exr.Decode(data, new ExrDecodeOptions { Parallel = false }), 1.1);
    }

    /// <summary>Encoding allocates next to nothing once the pools are warm (the output stream is sized beforehand).</summary>
    static void AssertSmallEncode(RawImage img, Action<Stream> encode)
    {
        var first = new MemoryStream();
        encode(first);
        var target = new MemoryStream((int)first.Length + 4096);
        long before = GC.GetAllocatedBytesForCurrentThread();
        encode(target);
        long used = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(first.Length, target.Length);
        long input = (long)img.Data.Length * RawImage.BytesPerSample(img.SampleType);
        Assert.True(used < input / 20, $"{used} bytes allocated to encode {input} bytes of pixels");
    }

    [Theory]
    [InlineData(3, true, JpegSubsampling.Chroma444, 0)]
    [InlineData(3, true, JpegSubsampling.Chroma420, 0)]
    [InlineData(4, true, JpegSubsampling.Chroma422, 7)]
    [InlineData(3, false, JpegSubsampling.Chroma420, 0)]
    [InlineData(4, false, JpegSubsampling.Chroma444, 5)]
    public void JpegEncode(int channels, bool optimize, JpegSubsampling subsampling, int restart)
    {
        var img = Photo(channels);
        AssertSmallEncode(img, s => ImageCodecs.Jpeg.Encode(img, s, new JpegEncodeOptions { OptimizeHuffman = optimize, Subsampling = subsampling, RestartInterval = restart }));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void PngEncode(int channels)
    {
        var img = Photo(channels);
        AssertSmallEncode(img, s => Png.Encode(img, s));
    }

    [Fact]
    public void TiffEncode()
    {
        var img = Photo(3);
        AssertSmallEncode(img, s => ImageCodecs.Tiff.Encode(img, s, new TiffEncodeOptions { Parallel = false }));
    }

    // a MemoryStream is decoded from its own buffer, from the current position on
    [Fact]
    public void StreamFromMemoryStreamPosition()
    {
        var png = Encode(s => Png.Encode(Photo(3), s));
        var padded = new byte[17 + png.Length];
        png.CopyTo(padded, 17);
        using var ms = new MemoryStream(padded, 0, padded.Length, writable: false, publiclyVisible: true) { Position = 17 };
        var img = ImageCodec.Decode(ms);
        Assert.Equal((W, H, padded.Length), (img.Width, img.Height, (int)ms.Position));
        TestUtil.AssertEqual(ImageCodec.Decode(png), img, 0);
    }
}
