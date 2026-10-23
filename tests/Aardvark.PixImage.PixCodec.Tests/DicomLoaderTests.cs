using Aardvark.Data;
using Aardvark.Base;
using Aardvark.ImageCodecs;
using Xunit;

namespace PixCodecTests;

using PixImage = Aardvark.Base.PixImage;

/// <summary>DICOM through the PixImage loader: slices, series folders as volumes, saving.</summary>
public class DicomLoaderTests
{
    static readonly PixCodecLoader Loader = PixCodec.Loader;

    static string Fixture(string rel)
    {
        var d = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(d, "tests", "fixtures"))) d = Path.GetDirectoryName(d)!;
        return Path.Combine(d, "tests", "fixtures", rel);
    }

    [Fact]
    public void SliceLoadsInHounsfieldUnits()
    {
        var path = Path.Combine(Fixture("dicom/series_ct"), "IM0004");
        var raw = ImageCodec.Decode(path);
        var p = Loader.LoadFromFile(path);
        // 12 bit signed CT samples with intercept -1024: Int32 Hounsfield units
        Assert.Equal(typeof(int), p.PixFormat.Type);
        Assert.Equal(Col.Format.Gray, p.Format);
        Assert.Equal(new V2i(32, 24), p.Size);
        Assert.Equal((int[])raw.Data, ((PixImage<int>)p).ToImageLayout().Volume.CopyToImage().Data);
        var img = PixCodec.Load(path, out var meta);
        Assert.NotNull(meta);
        Assert.Equal("LPS", meta!.Space);
        Assert.Equal(-1024, meta.Intercept);
    }

    [Fact]
    public void SeriesFolderLoadsAsVolume()
    {
        var dir = Fixture("dicom/series_ct");
        var v = PixCodec.LoadVolume(dir, out var meta);
        Assert.Equal(new V3i(32, 24, 10), v.Size);
        Assert.Equal(2.5, meta!.Spacing[2], 6);
        var plain = PixCodec.LoadVolume(dir);
        Assert.Equal(v.Size, plain.Size);
    }

    [Fact]
    public void SavesDicom()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "pixcodec-dicom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var img = new PixImage<ushort>(Col.Format.Gray, 20, 10);
            img.GetChannel(0L).SetByCoord((x, y) => (ushort)(x * 100 + y));
            var path = Path.Combine(tmp, "x.dcm");
            PixCodec.Save(img, path);
            var back = (PixImage<ushort>)Loader.LoadFromFile(path);
            Assert.Equal(img.Volume.CopyToImage().Data, back.ToImageLayout().Volume.CopyToImage().Data);

            var vol = PixCodec.LoadVolume(Fixture("dicom/series_ct"), out var meta);
            var vpath = Path.Combine(tmp, "v.dcm");
            PixCodec.SaveVolume(vol, vpath, meta);
            var again = PixCodec.LoadVolume(vpath, out var meta2);
            Assert.Equal(vol.Size, again.Size);
            Assert.Equal(meta!.Origin, meta2!.Origin);
        }
        finally { Directory.Delete(tmp, true); }
    }
}
