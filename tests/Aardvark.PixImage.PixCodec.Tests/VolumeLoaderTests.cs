using Aardvark.Data;
using Aardvark.Base;
using Aardvark.ImageCodecs;
using Xunit;

namespace PixCodecTests;

using PixImage = Aardvark.Base.PixImage;

/// <summary>NRRD, NIfTI, MetaImage and FITS through the loader: PixVolume / PixImage with the physical metadata.</summary>
public class VolumeLoaderTests
{
    static string Fixture(string rel)
    {
        var d = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(d, "tests", "fixtures"))) d = Path.GetDirectoryName(d)!;
        return Path.Combine(d, "tests", "fixtures", rel);
    }

    [Fact]
    public void LoadsScientificVolumes()
    {
        // NRRD: x, y, z as stored; PixVolume tensor index [x, y, z, channel]
        var path = Fixture("volume/nrrd/u8_raw.nrrd");
        var raw = ImageCodec.DecodeVolume(path);
        var v = (PixVolume<byte>)PixCodec.LoadVolume(path, out var meta);
        Assert.Equal(new V3i(7, 5, 4), v.Size);
        var t = v.Tensor4;
        var d = raw.GetData<byte>();
        for (int z = 0; z < 4; z++) for (int y = 0; y < 5; y++) for (int x = 0; x < 7; x++)
            Assert.Equal(d[raw.GetIndex(x, y, z)], t[x, y, z, 0]);
        Assert.Equal("LPS", meta!.Space);
        Assert.Equal(2.5, meta.Spacing[2], 9);
        Assert.Equal(new[] { 1.0, -2.0, 3.5 }, meta.Origin);

        // without the out parameter, through the existing entry point, and converted
        Assert.Equal(v.Size, PixCodec.LoadVolume(path).Size);
        var f = PixCodec.LoadVolume<float>(path);
        Assert.Equal(t[3, 2, 1, 0] / 255f, f.Tensor4[3, 2, 1, 0], 6); // (converted as PixVolume.ToPixVolume does: integer ranges normalised)

        // RGB NIfTI, MetaImage vectors, a FITS cube, a detached NRRD
        var rgb = PixCodec.LoadVolume(Fixture("volume/nifti/rgb24.nii"));
        Assert.Equal(Col.Format.RGB, rgb.Format);
        Assert.Equal(new V3i(5, 4, 2), rgb.Size);
        Assert.Equal(3L, ((PixVolume<byte>)PixCodec.LoadVolume(Fixture("volume/metaimage/vec3.mha"))).Tensor4.Size.W);
        Assert.IsType<PixVolume<int>>(PixCodec.LoadVolume(Fixture("volume/fits/cube.fits")));
        Assert.IsType<PixVolume<ushort>>(PixCodec.LoadVolume(Fixture("volume/nrrd/detached.nhdr")));
        Assert.IsType<PixVolume<ulong>>(PixCodec.LoadVolume(Fixture("volume/nrrd/raw_uint64.nrrd")));

        // frames of a 4D series
        var f2 = (PixVolume<short>)PixCodec.LoadVolume(Fixture("volume/nrrd/time_4d.nrrd"), 0, 2);
        var all = ImageCodec.DecodeVolume(Fixture("volume/nrrd/time_4d.nrrd"));
        Assert.Equal(all.GetData<short>()[all.GetIndex(1, 2, 1, 2)], f2.Tensor4[1, 2, 1, 0]);
    }

    [Fact]
    public void LoadsScientificImages()
    {
        // a 2D FITS image: top row first like every PixImage (FITS stores it last)
        var img = (PixImage<short>)PixCodec.Load(Fixture("volume/fits/bitpix_int16.fits"), out var meta);
        Assert.Equal(new V2i(7, 5), img.Size);
        Assert.NotNull(meta);
        Assert.Equal(ImageFormat.Fits, meta!.SourceFormat);
        var flipped = Fits.DecodeVolume(File.ReadAllBytes(Fixture("volume/fits/bitpix_int16.fits")), new FitsDecodeOptions { ApplyOrientation = false });
        Assert.Equal(flipped.GetData<short>()[0], img.Volume[0, 4, 0]); // FITS (1, 1) is the bottom left pixel
        // through the registered loader too
        PixCodec.Init();
        var viaLoader = PixImage.Load(Fixture("volume/metaimage/slice2d.mha"));
        Assert.Equal(new V2i(6, 5), viaLoader.Size);
        Assert.Equal(new V2i(6, 5), PixCodec.Loader.GetInfoFromFile(Fixture("volume/metaimage/slice2d.mha")).Size);
    }

    [Theory]
    [InlineData(".nrrd")]
    [InlineData(".nhdr")]
    [InlineData(".nii")]
    [InlineData(".nii.gz")]
    [InlineData(".mha")]
    [InlineData(".mhd")]
    [InlineData(".fits")]
    public void SavesWithMetadata(string ext)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pixcodec-vol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var v = new PixVolume<float>(Col.Format.Gray, 6, 5, 4);
            var t = v.Tensor4;
            for (int z = 0; z < 4; z++) for (int y = 0; y < 5; y++) for (int x = 0; x < 6; x++) t[x, y, z, 0] = x + 10 * y + 100 * z + 0.25f;
            var meta = new PhysicalMetadata
            {
                Space = "LPS", Spacing = new[] { 0.5, 0.5, 3.0 }, Origin = new[] { 1.0, 2.0, 3.0 },
                Direction = new double[,] { { 0, 1, 0 }, { -1, 0, 0 }, { 0, 0, 1 } },
            };
            var p = Path.Combine(dir, "vol" + ext);
            PixCodec.SaveVolume(v, p, meta);
            var back = (PixVolume<float>)PixCodec.LoadVolume(p, out var m2);
            Assert.Equal(v.Size, back.Size);
            for (int z = 0; z < 4; z++) for (int y = 0; y < 5; y++) for (int x = 0; x < 6; x++) Assert.Equal(t[x, y, z, 0], back.Tensor4[x, y, z, 0]);
            var e = meta.GetVoxelToLps()!;
            var a = m2!.Space == "FITS-WCS" ? m2.GetVoxelToWorld() : m2.GetVoxelToLps()!;
            for (int r = 0; r < 3; r++) for (int c = 0; c < 4; c++) Assert.Equal(e[r, c], a[r, c], 5);
            // a 2D image through Save / Load by extension
            var img = new PixImage<ushort>(Col.Format.Gray, 9, 4);
            img.Volume.SetByIndex(i => (ushort)(i * 300));
            var ip = Path.Combine(dir, "img" + ext);
            PixCodec.Save(img, ip);
            var ib = (PixImage<ushort>)PixCodec.Load(ip);
            Assert.Equal(img.Volume[8, 3, 0], ib.Volume[8, 3, 0]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RawVolumeConversions()
    {
        var v = new PixVolume<byte>(Col.Format.BGR, 4, 3, 2);
        var t = v.Tensor4;
        t.SetByCoord((x, y, z, c) => (byte)(x + 4 * y + 12 * z + 50 * c));
        var raw = v.ToRawVolume();
        Assert.Equal(ColorModel.Rgb, raw.Color);
        Assert.Equal(t[1, 2, 1, 2], raw.GetData<byte>()[raw.GetIndex(1, 2, 1)]); // B moved to channel 0 ... R is channel 0 now
        var back = raw.ToPixVolume();
        Assert.Equal(Col.Format.RGB, back.Format);
        Assert.Equal(t[3, 1, 0, 0], ((PixVolume<byte>)back).Tensor4[3, 1, 0, 2]);
    }

    [Fact]
    public void LoadsEnviRasters()
    {
        // the sample file and the header both name the pair; the bands become channels, or the z axis of a volume
        var data = Fixture("envi/gdal_u16_utm33n_bsq.img");
        var raw = ImageCodec.Decode(data);
        foreach (var p in new[] { data, Fixture("envi/gdal_u16_utm33n_bsq.hdr") })
        {
            var img = (PixImage<ushort>)PixCodec.Load(p);
            Assert.Equal(new V2i(9, 7), img.Size);
            Assert.Equal(3, img.ChannelCount);
            Assert.Equal(raw.GetData<ushort>()[(2 * 9 + 4) * 3 + 1], img.Volume[4, 2, 1]);
        }
        var v = (PixVolume<ushort>)PixCodec.LoadVolume(data);
        Assert.Equal(new V3i(9, 7, 3), v.Size);
        Assert.Equal(raw.GetData<ushort>()[(2 * 9 + 4) * 3 + 1], v.Tensor4[4, 2, 1, 0]);
    }
}
