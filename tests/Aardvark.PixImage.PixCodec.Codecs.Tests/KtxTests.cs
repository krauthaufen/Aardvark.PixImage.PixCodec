using Xunit;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>KTX 1 and KTX 2: fixtures written by KTX-Software and by hand (tests/oracle/gen_misc.py), writing, validation.</summary>
public class KtxTests
{
    public static IEnumerable<object[]> Fixtures
        => Files("ktx", ".ktx", ".ktx2").Where(r => !File.Exists(Fixture((string)r[0]) + ".blocks"));

    public static IEnumerable<object[]> EtcFixtures
        => Files("ktx", ".ktx2").Where(r => File.Exists(Fixture((string)r[0]) + ".blocks"));

    public static IEnumerable<object[]> BasisFixtures => Files("basis", ".ktx2");

    /// <summary>ETC2 / EAC in KTX 2 (random blocks written by `ktx create`): the container gives what the block decoders give.</summary>
    [Theory, MemberData(nameof(EtcFixtures))]
    public void EtcContainer(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        var blocks = File.ReadAllBytes(Fixture(file) + ".blocks");
        var info = Ktx.GetTextureInfo(data);
        var img = Ktx.Decode(data);
        Assert.Equal((13, 10, info.Channels), (img.Width, img.Height, img.Channels));
        Assert.Equal(info.IsSrgb, info.Format.Contains("SRGB"));
        int bb = blocks.Length / 12, ch = img.Channels;
        var rgba = new byte[64];
        var u = new ushort[32];
        var sv = new short[32];
        for (int by = 0; by < 3; by++)
            for (int bx = 0; bx < 4; bx++)
            {
                var b = blocks.AsSpan((by * 4 + bx) * bb, bb);
                switch (info.Format)
                {
                    case var f when f.StartsWith("ETC2_R8G8B8_"): EtcBlocks.Etc2Rgb(b, rgba); break;
                    case var f when f.StartsWith("ETC2_R8G8B8A1_"): EtcBlocks.Etc2PunchThrough(b, rgba); break;
                    case var f when f.StartsWith("ETC2_R8G8B8A8_"): EtcBlocks.Etc2Rgba(b, rgba); break;
                    case "EAC_R11_UNORM_BLOCK": EtcBlocks.EacR11(b, u, 1); break;
                    case "EAC_R11_SNORM_BLOCK": EtcBlocks.EacR11Signed(b, sv, 1); break;
                    case "EAC_R11G11_UNORM_BLOCK": EtcBlocks.EacRg11(b, u); break;
                    default: EtcBlocks.EacRg11Signed(b, sv); break;
                }
                for (int y = 0; y < 4 && by * 4 + y < 10; y++)
                    for (int x = 0; x < 4 && bx * 4 + x < 13; x++)
                        for (int c = 0; c < ch; c++)
                        {
                            long o = ((long)(by * 4 + y) * 13 + bx * 4 + x) * ch + c;
                            int t = y * 4 + x;
                            double expected = img.SampleType switch
                            {
                                SampleType.UInt8 => rgba[t * 4 + c],
                                SampleType.UInt16 => u[t * ch + c],
                                _ => sv[t * ch + c],
                            };
                            Assert.Equal(expected, ToDoubles(img)[o]);
                        }
            }
    }

    /// <summary>Basis Universal (ETC1S / UASTC) files written by basisu: every image through the KTX reader.</summary>
    [Theory, MemberData(nameof(BasisFixtures))]
    public void BasisThroughKtx(string file)
    {
        var path = Fixture(file);
        var t = Ktx.DecodeTexture(File.ReadAllBytes(path));
        int n = 0;
        foreach (var r in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.ref"))
        {
            var parts = r.Substring(path.Length + 1).Split('.');   // level.layer.face.ref
            var expected = LoadRef(r);
            var img = t.GetImage(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
            Assert.Equal(expected.GetBytes().ToArray(), img.GetBytes().ToArray());
            n++;
        }
        Assert.True(n > 0);
        Assert.Equal(t.Format.StartsWith("ETC1S") ? "ETC1S" : "UASTC", t.Format);
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void DecodeMatchesReference(string file)
    {
        var path = Fixture(file);
        var data = File.ReadAllBytes(path);
        var info = Ktx.GetTextureInfo(data);
        bool astc = info.Format.StartsWith("ASTC");
        var t = Ktx.DecodeTexture(data);
        int refs = 0;
        foreach (var r in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*.ref"))
        {
            var suffix = r.Substring(path.Length, r.Length - path.Length - 4);   // "", ".mip1", ".face2", ...
            var expected = LoadRef(r);
            int k = suffix.Length > 0 ? int.Parse(new string(suffix.Where(char.IsDigit).ToArray())) : 0;
            var img = suffix.StartsWith(".mip") ? t.GetImage(mip: k)
                : suffix.StartsWith(".face") ? t.GetImage(face: k)
                : suffix.StartsWith(".layer") ? t.GetImage(layer: k)
                : suffix.StartsWith(".slice") ? t.GetImage(slice: k)
                : t.GetImage();
            // ASTC: ktx extract's decoding through astcenc, which interpolates sRGB blocks in a slightly different way
            AssertEqual(expected, img, astc ? 1 : 0);
            refs++;
        }
        Assert.True(refs > 0, "no reference");

        // header information agrees with the decoded images, also through the generic entry points
        var ii = ImageCodec.GetInfo(data);
        var top = ImageCodec.Decode(data);
        Assert.Equal(ImageFormat.Ktx, ii.Format);
        Assert.Equal((top.Width, top.Height, top.Channels, top.SampleType, top.Color), (ii.Width, ii.Height, ii.Channels, ii.SampleType, ii.Color));
        Assert.Equal((info.MipLevels, info.Layers * info.Faces), (ii.MipLevels, ii.FrameCount));
        Assert.Equal(info.IsSrgb ? TransferFunction.Srgb : ii.Transfer, ii.Transfer);
        Assert.Equal(info.MipLevels, ImageCodec.DecodeMipmaps(data).Length);
        Assert.Equal(info.Layers * info.Faces * info.Depth, ImageCodec.DecodeAll(data).Length);
    }

    [Fact]
    public void FixtureStructure()
    {
        var cube = Ktx.GetTextureInfo(File.ReadAllBytes(Fixture("ktx/cube.ktx2")));
        Assert.Equal((KtxTextureKind.CubeMap, 6, true, TransferFunction.Srgb), (cube.Kind, cube.Faces, cube.IsSrgb, cube.Transfer));
        var vol = Ktx.GetTextureInfo(File.ReadAllBytes(Fixture("ktx/volume.ktx2")));
        Assert.Equal((KtxTextureKind.Texture3D, 4, KtxSupercompression.Zstd), (vol.Kind, vol.Depth, vol.Supercompression));
        var arr = Ktx.GetTextureInfo(File.ReadAllBytes(Fixture("ktx/array.ktx2")));
        Assert.Equal((true, 3), (arr.IsArray, arr.Layers));
        Assert.Equal(KtxTextureKind.Texture1D, Ktx.GetTextureInfo(File.ReadAllBytes(Fixture("ktx/line.ktx2"))).Kind);
        var k1 = Ktx.GetTextureInfo(File.ReadAllBytes(Fixture("ktx/k1_rgb8.ktx")));
        Assert.Equal((1, 0x8051, "R8G8B8_UNORM"), (k1.Version, k1.GlInternalFormat, k1.Format));
        Assert.Equal("S=r,T=d", k1.GetString("KTXorientation"));
        // a bottom-up KTX 1 file is turned upright unless asked not to
        var up = File.ReadAllBytes(Fixture("ktx/k1_bottomup.ktx"));
        AssertEqual(ImageOps.FlipVertical(Ktx.Decode(up)), Ktx.Decode(up, new KtxDecodeOptions { ApplyOrientation = false }));
    }

    public static IEnumerable<object[]> WriteCases()
    {
        foreach (var t in new[] { SampleType.UInt8, SampleType.Int8, SampleType.UInt16, SampleType.Int16, SampleType.UInt32, SampleType.Int32, SampleType.Float16, SampleType.Float32 })
            foreach (var c in new[] { ColorModel.Gray, ColorModel.GrayAlpha, ColorModel.Rgb, ColorModel.Rgba })
                yield return new object[] { t, c };
    }

    [Theory, MemberData(nameof(WriteCases))]
    public void UncompressedRoundTrip(SampleType t, ColorModel c)
    {
        var src = Pattern(19, 11, c, t, 1);
        var enc = Ktx.Encode(src, new KtxEncodeOptions { GenerateMipmaps = true });
        Validate(enc);
        var tex = Ktx.DecodeTexture(enc);
        var top = tex.GetImage();
        bool srgbGrayAlpha = t == SampleType.UInt8 && c == ColorModel.GrayAlpha;   // sRGB gray + alpha is stored as RGBA
        if (srgbGrayAlpha) AssertEqual(ToRgba(src), top);
        else
        {
            Assert.Equal(src.Channels, top.Channels);
            Assert.Equal(src.GetBytes().ToArray(), top.GetBytes().ToArray());
        }
        Assert.Equal(5, tex.MipLevels);
        Assert.Equal(1, tex.GetImage(mip: 4).Width);
        var info = Ktx.GetTextureInfo(enc);
        Assert.Equal(t == SampleType.UInt8, info.IsSrgb);
        if (c == ColorModel.Gray) Assert.Equal("rrr1", info.GetString("KTXswizzle"));
        Assert.Equal("PixCodec", info.GetString("KTXwriter"));
    }

    [Theory]
    [InlineData(TextureCompression.BC1)]
    [InlineData(TextureCompression.BC2)]
    [InlineData(TextureCompression.BC3)]
    [InlineData(TextureCompression.BC4)]
    [InlineData(TextureCompression.BC5)]
    [InlineData(TextureCompression.BC6H)]
    [InlineData(TextureCompression.BC7)]
    public void BlockCompressedMatchesDds(TextureCompression bc)
    {
        var src = bc == TextureCompression.BC6H ? Pattern(37, 21, ColorModel.Rgb, SampleType.Float32, 2) : Pattern(37, 21, bc == TextureCompression.BC5 ? ColorModel.GrayAlpha : ColorModel.Rgba, SampleType.UInt8, 2);
        var enc = Ktx.Encode(src, new KtxEncodeOptions { Compression = bc, GenerateMipmaps = true, Srgb = false });
        Validate(enc);
        var dds = Dds.Encode(src, new DdsEncodeOptions { Compression = (DdsCompression)(int)bc, Mipmaps = DdsMipmaps.Generate });
        var a = Ktx.DecodeMipmaps(enc);
        var b = Dds.DecodeMipmaps(dds);
        Assert.Equal(b.Length, a.Length);
        for (int m = 0; m < a.Length; m++)
        {
            var expected = bc == TextureCompression.BC1 && !src.HasAlpha ? b[m] : b[m];
            Assert.Equal(expected.GetBytes().ToArray(), a[m].GetBytes().ToArray());
        }
        Assert.True(Ktx.GetTextureInfo(enc).IsBlockCompressed);
    }

    [Fact]
    public void StructuresRoundTrip()
    {
        var faces = Enumerable.Range(0, 6).Select(i => (IReadOnlyList<RawImage>)new[] { Pattern(16, 16, ColorModel.Rgba, SampleType.UInt8, 10 + i) }).ToArray();
        var cube = Ktx.EncodeCube(faces, new KtxEncodeOptions { GenerateMipmaps = true, Compression = TextureCompression.BC7 });
        Validate(cube);
        var ct = Ktx.DecodeTexture(cube);
        Assert.Equal((KtxTextureKind.CubeMap, 6, 5), (ct.Kind, ct.Faces, ct.MipLevels));
        Assert.Equal(6, ImageCodec.DecodeAll(cube).Length);

        var layers = Enumerable.Range(0, 3).Select(i => (IReadOnlyList<RawImage>)new[] { Pattern(9, 5, ColorModel.Rgb, SampleType.UInt16, 20 + i) }).ToArray();
        var arr = Ktx.EncodeArray(layers);
        Validate(arr);
        var at = Ktx.DecodeTexture(arr);
        for (int i = 0; i < 3; i++) AssertEqual(layers[i][0], at.GetImage(layer: i));

        var slices = Enumerable.Range(0, 5).Select(i => Pattern(8, 6, ColorModel.Gray, SampleType.Float32, 30 + i)).ToArray();
        var vol = Ktx.EncodeVolume(slices, new KtxEncodeOptions { GenerateMipmaps = true });
        Validate(vol);
        var vt = Ktx.DecodeTexture(vol);
        Assert.Equal((KtxTextureKind.Texture3D, 5, 4), (vt.Kind, vt.Depth, vt.MipLevels));
        for (int z = 0; z < 5; z++) AssertEqual(slices[z], vt.GetImage(slice: z));
        Assert.Equal(2, vt.GetSlices(1).Length);
        Assert.Single(vt.GetSlices(3));
        // the second level averages pairs of slices
        var avg = vt.GetImage(1, slice: 0).GetData<float>();
        var s0 = ImageOps.Resize(slices[0], 4, 3, ResampleFilter.Box).GetData<float>();
        var s1 = ImageOps.Resize(slices[1], 4, 3, ResampleFilter.Box).GetData<float>();
        for (int i = 0; i < avg.Length; i++) Assert.Equal((s0[i] + s1[i]) * 0.5f, avg[i], 5);
    }

    /// <summary>
    /// 3D ASTC: KTX 1 with the OES enums and KTX 2 described by the data format descriptor alone (vkFormat 0, model ASTC):
    /// blocks span several slices, edges are cropped in x, y and z.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Astc3DVolumes(bool ktx2)
    {
        var blocks = File.ReadAllBytes(Fixture("etc_astc/astc_ldr_4x4x4_ldr.bin"));
        int n = blocks.Length / 16;                 // one row of blocks along x
        int w = n * 4 - 1, h = 3, d = 3;            // partial blocks everywhere
        byte[] file = ktx2 ? Ktx2Astc3D(blocks, w, h, d) : Ktx1Astc3D(blocks, w, h, d);
        var t = Ktx.DecodeTexture(file);
        Assert.Equal((KtxTextureKind.Texture3D, d, "ASTC_4x4x4_UNORM_BLOCK"), (t.Kind, t.Depth, t.Format));
        var texels = new byte[4 * 4 * 4 * 4];
        for (int b = 0; b < n; b++)
        {
            AstcBlocks.DecodeUnorm8(blocks.AsSpan(b * 16, 16), 4, 4, 4, false, texels);
            for (int z = 0; z < d; z++)
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < 4 && b * 4 + x < w; x++)
                        for (int c = 0; c < 4; c++)
                            Assert.Equal(texels[((z * 4 + y) * 4 + x) * 4 + c], t.GetImage(slice: z).GetData<byte>()[(y * w + b * 4 + x) * 4 + c]);
        }
    }

    static byte[] Ktx1Astc3D(byte[] blocks, int w, int h, int d)
    {
        var o = new MemoryStream();
        o.Write(new byte[] { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x31, 0x31, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A });
        foreach (uint v in new uint[] { 0x04030201, 0, 1, 0, 0x93C3, 0x1908, (uint)w, (uint)h, (uint)d, 0, 1, 1, 0, (uint)blocks.Length })
            o.Write(BitConverter.GetBytes(v));
        o.Write(blocks);
        return o.ToArray();
    }

    static byte[] Ktx2Astc3D(byte[] blocks, int w, int h, int d)
    {
        // basic data format descriptor: model ASTC (162), 4x4x4 texels, one 128 bit sample
        var dfd = new byte[4 + 24 + 16];
        BitConverter.GetBytes((uint)dfd.Length).CopyTo(dfd, 0);
        BitConverter.GetBytes(2u | 40u << 16).CopyTo(dfd, 8);
        dfd[12] = 162; dfd[13] = 1; dfd[14] = 1;
        dfd[16] = 3; dfd[17] = 3; dfd[18] = 3;
        dfd[20] = 16;
        BitConverter.GetBytes((uint)(0 | 127 << 16)).CopyTo(dfd, 28);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(dfd, 40);
        var head = new byte[80 + 24];
        new byte[] { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(head, 0);
        uint[] f = { 0, 1, (uint)w, (uint)h, (uint)d, 0, 1, 1, 0, 104, (uint)dfd.Length, 0, 0 };
        for (int i = 0; i < f.Length; i++) BitConverter.GetBytes(f[i]).CopyTo(head, 12 + 4 * i);
        long dataOff = 104 + dfd.Length;
        BitConverter.GetBytes((ulong)dataOff).CopyTo(head, 80);
        BitConverter.GetBytes((ulong)blocks.Length).CopyTo(head, 88);
        BitConverter.GetBytes((ulong)blocks.Length).CopyTo(head, 96);
        return head.Concat(dfd).Concat(blocks).ToArray();
    }

    public static IEnumerable<object[]> MobileCases()
    {
        yield return new object[] { TextureCompression.Etc2, ColorModel.Rgb, SampleType.UInt8, 4, 4 };
        yield return new object[] { TextureCompression.Etc2, ColorModel.Rgba, SampleType.UInt8, 4, 4 };
        yield return new object[] { TextureCompression.Etc2, ColorModel.Gray, SampleType.UInt8, 4, 4 };
        yield return new object[] { TextureCompression.Eac, ColorModel.Gray, SampleType.UInt16, 4, 4 };
        yield return new object[] { TextureCompression.Eac, ColorModel.GrayAlpha, SampleType.UInt8, 4, 4 };
        yield return new object[] { TextureCompression.Eac, ColorModel.Gray, SampleType.Int16, 4, 4 };
        yield return new object[] { TextureCompression.Astc, ColorModel.Rgba, SampleType.UInt8, 4, 4 };
        yield return new object[] { TextureCompression.Astc, ColorModel.Rgb, SampleType.UInt8, 6, 6 };
        yield return new object[] { TextureCompression.Astc, ColorModel.Rgb, SampleType.UInt8, 12, 10 };
        yield return new object[] { TextureCompression.Astc, ColorModel.Rgb, SampleType.Float32, 4, 4 };
        yield return new object[] { TextureCompression.Astc, ColorModel.Rgba, SampleType.Float16, 8, 8 };
    }

    /// <summary>ETC2 / EAC / ASTC writing: valid files (`ktx validate`), sensible quality, mip chains, the expected formats.</summary>
    [Theory, MemberData(nameof(MobileCases))]
    public void MobileFormatsRoundTrip(TextureCompression c, ColorModel color, SampleType type, int bw, int bh)
    {
        var src = Pattern(37, 21, color, type, 7, noise: 0.02);
        var enc = Ktx.Encode(src, new KtxEncodeOptions { Compression = c, GenerateMipmaps = true, AstcBlockWidth = bw, AstcBlockHeight = bh });
        Validate(enc);
        var info = Ktx.GetTextureInfo(enc);
        Assert.Equal(6, info.MipLevels);
        Assert.True(info.IsBlockCompressed);
        string expect = c switch
        {
            TextureCompression.Etc2 => color == ColorModel.Rgba ? "ETC2_R8G8B8A8_SRGB_BLOCK" : "ETC2_R8G8B8_SRGB_BLOCK",
            TextureCompression.Eac => (color == ColorModel.GrayAlpha ? "EAC_R11G11_" : "EAC_R11_") + (type == SampleType.Int16 ? "SNORM" : "UNORM") + "_BLOCK",
            _ => $"ASTC_{bw}x{bh}_" + (type is SampleType.Float16 or SampleType.Float32 ? "SFLOAT" : "SRGB") + "_BLOCK",
        };
        Assert.Equal(expect, info.Format);
        var dec = Ktx.Decode(enc);
        Assert.Equal((37, 21), (dec.Width, dec.Height));
        // quality: compare the shared channels in [0, 1]
        var a = ToDoubles(src);
        var b = ToDoubles(dec);
        int sc = src.Channels, dc = dec.Channels, n = 37 * 21;
        double se = 0;
        int cnt = 0;
        bool hdr = type is SampleType.Float16 or SampleType.Float32;
        double scaleA = type switch { SampleType.UInt8 => 255, SampleType.UInt16 => 65535, SampleType.Int16 => 32767, _ => 3 };
        double scaleB = dec.SampleType switch { SampleType.UInt8 => 255, SampleType.UInt16 => 65535, SampleType.Int16 => 32767, _ => 3 };
        bool gray = color is ColorModel.Gray or ColorModel.GrayAlpha;
        for (int i = 0; i < n; i++)
            for (int k = 0; k < Math.Min(hdr ? 3 : sc, gray && c != TextureCompression.Eac ? 1 : sc); k++)
            {
                double x = a[i * sc + k];
                if (hdr) x = Math.Max(x, 0);   // ASTC HDR stores no negative values
                double d = x / scaleA - b[i * dc + k] / scaleB;
                se += d * d; cnt++;
            }
        double psnr = 10 * Math.Log10(1 / (se / cnt));
        // a sanity floor for this chroma-rich pattern (BC1 reaches 26 dB on it, BC7 35); the encoders' own tests gate quality
        // against the reference encoders
        double floor = c switch
        {
            TextureCompression.Etc2 => 27,
            TextureCompression.Eac => 30,
            // linear error of log-domain HDR blocks; at 8x8 astcenc -medium reaches RMSE 0.75 (12 dB here) on this pattern
            _ when hdr => bw * bh > 16 ? 12 : 20,
            _ when bw * bh > 36 => 20,
            _ when bw * bh > 16 => 24,
            _ => 28,
        };
        Assert.True(psnr > floor, $"PSNR {psnr:F1}");
    }

    [Fact]
    public void SupercompressionVariants()
    {
        var src = Pattern(33, 17, ColorModel.Rgba, SampleType.UInt8, 3);
        foreach (var s in new[] { KtxSupercompression.None, KtxSupercompression.Zstd, KtxSupercompression.Zlib })
        {
            var enc = Ktx.Encode(src, new KtxEncodeOptions { Supercompression = s, GenerateMipmaps = true });
            Validate(enc);
            Assert.Equal(s, Ktx.GetTextureInfo(enc).Supercompression);
            AssertEqual(src, Ktx.Decode(enc));
        }
        Assert.Throws<ArgumentException>(() => Ktx.Encode(src, new KtxEncodeOptions { Supercompression = KtxSupercompression.BasisLZ }));
    }

    [Fact]
    public void TransferFunctionChoice()
    {
        var src = Pattern(8, 8, ColorModel.Rgb, SampleType.UInt8, 4);
        Assert.Equal(TransferFunction.Srgb, Ktx.GetInfo(Ktx.Encode(src)).Transfer);
        src.Metadata = new ImageMetadata { Transfer = TransferFunction.Linear };
        var lin = Ktx.Encode(src);
        Assert.Equal((TransferFunction.Linear, "R8G8B8_UNORM"), (Ktx.GetInfo(lin).Transfer, Ktx.GetTextureInfo(lin).Format));
        Assert.Equal("R8G8B8_SRGB", Ktx.GetTextureInfo(Ktx.Encode(src, new KtxEncodeOptions { Srgb = true })).Format);
        Assert.Equal(TransferFunction.Linear, Ktx.GetInfo(Ktx.Encode(Pattern(4, 4, ColorModel.Rgb, SampleType.Float16, 1))).Transfer);
    }

    [Fact]
    public void Broken()
    {
        var good = Ktx.Encode(Pattern(8, 8, ColorModel.Rgba, SampleType.UInt8, 5), new KtxEncodeOptions { Supercompression = KtxSupercompression.None });
        Assert.Throws<ImageFormatException>(() => Ktx.Decode(good.AsSpan(0, 50)));
        var trunc = Ktx.Decode(good.AsSpan(0, good.Length - 100));   // missing pixels read as zeros
        Assert.Equal(8, trunc.Width);
        var bad = (byte[])good.Clone();
        bad[36] = 3;   // faceCount
        Assert.Throws<ImageFormatException>(() => Ktx.Decode(bad));
        bad = (byte[])good.Clone();
        bad[12] = 0xEE; bad[13] = 0xEE;   // unknown vkFormat
        Assert.Throws<ImageNotSupportedException>(() => Ktx.Decode(bad));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Runs `ktx validate` (KTX-Software, $PIXCODEC_KTX or on the PATH) when it is available.</summary>
    internal static void Validate(byte[] ktx2)
    {
        var tool = Environment.GetEnvironmentVariable("PIXCODEC_KTX") ?? TestUtil.FindOnPath("ktx");
        if (!Oracles.Have("ktx", tool != null, "KTX-Software's ktx ($PIXCODEC_KTX or on the PATH)")) return;
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".ktx2");
        try
        {
            File.WriteAllBytes(path, ktx2);
            var output = System.Text.Encoding.UTF8.GetString(MiscFormatTests.RunTool(tool!, $"validate \"{path}\""));
            Assert.True(output.Trim().Length == 0, output);
        }
        finally { File.Delete(path); }
    }

    static RawImage ToRgba(RawImage ga)
    {
        var s = ga.GetData<byte>();
        int n = ga.Width * ga.Height;
        var o = new byte[n * 4];
        for (int i = 0; i < n; i++) { o[i * 4] = o[i * 4 + 1] = o[i * 4 + 2] = s[i * 2]; o[i * 4 + 3] = s[i * 2 + 1]; }
        return new RawImage(ga.Width, ga.Height, ColorModel.Rgba, o, 4);
    }

    internal static RawImage Pattern(int w, int h, ColorModel c, SampleType t, int seed, double noise = 0.1)
    {
        var r = new Random(seed);
        var img = RawImage.Create(w, h, c, t);
        int ch = img.Channels;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int k = 0; k < ch; k++)
                {
                    double v = 0.5 + 0.4 * Math.Sin(x * 0.4 + k) * Math.Cos(y * 0.3 + seed) + noise * r.NextDouble();
                    long i = ((long)y * w + x) * ch + k;
                    switch (img.Data)
                    {
                        // signed first: sbyte[] also matches byte[] (CLR array covariance)
                        case sbyte[] b when t == SampleType.Int8: b[i] = (sbyte)(v * 254 - 127); break;
                        case short[] b when t == SampleType.Int16: b[i] = (short)(v * 65534 - 32767); break;
                        case int[] b when t == SampleType.Int32: b[i] = (int)(v * 4e9 - 2e9); break;
                        case byte[] b: b[i] = (byte)(v * 255); break;
                        case ushort[] b: b[i] = (ushort)(v * 65535); break;
                        case uint[] b: b[i] = (uint)(v * 4e9); break;
                        case Half[] b: b[i] = (Half)(v * 3 - 0.5); break;
                        case float[] b: b[i] = (float)(v * 3 - 0.5); break;
                    }
                }
        return img;
    }
}
