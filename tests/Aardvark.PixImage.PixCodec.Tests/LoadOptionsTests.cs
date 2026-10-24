using Aardvark.Data;
using Aardvark.ImageCodecs;
using Xunit;

namespace PixCodecTests;

using PixImage = Aardvark.Base.PixImage;

/// <summary>PixCodec.Load with ImageDecodeOptions, and the typed RawImage conversion.</summary>
public class LoadOptionsTests
{
    static byte[] RotatedJpeg()
    {
        var img = RawImage.Create(40, 20, ColorModel.Rgb, SampleType.UInt8);
        var b = img.GetData<byte>();
        for (int i = 0; i < b.Length; i++) b[i] = (byte)(i * 7);
        img.Metadata = new ImageMetadata { Orientation = 6 };
        return Jpeg.Encode(img);
    }

    [Fact]
    public void LoadKeepsTheStoredPixelsWhenAsked()
    {
        var data = RotatedJpeg();
        var upright = PixCodec.Load(data);
        Assert.Equal((20L, 40L), (upright.Size.X, upright.Size.Y));
        var stored = PixCodec.Load(data, new ImageDecodeOptions { ApplyOrientation = false });
        Assert.Equal((40L, 20L), (stored.Size.X, stored.Size.Y));
        var rgba = PixCodec.Load<byte>(data, new ImageDecodeOptions { ApplyOrientation = false, Channels = ImageChannels.Rgba });
        Assert.Equal(4L, rgba.ChannelCount);
        Assert.Throws<ImageNotSupportedException>(() => PixCodec.Load(data, new ImageDecodeOptions { MaxPixels = 100 }));
    }

    [Fact]
    public void ToPixImageIsTyped()
    {
        var raw = Jpeg.Decode(RotatedJpeg());
        Aardvark.Base.PixImage<byte> b = PixCodec.ToPixImage<byte>(raw);
        Assert.Same(raw.Data, b.Volume.Data);
        Aardvark.Base.PixImage<float> f = PixCodec.ToPixImage<float>(raw);
        Assert.Equal(raw.Width, (int)f.Size.X);
        PixImage p = PixCodec.ToPixImage(raw);
        Assert.IsType<Aardvark.Base.PixImage<byte>>(p);
    }
}
