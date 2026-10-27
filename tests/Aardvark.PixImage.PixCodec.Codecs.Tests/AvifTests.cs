using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aardvark.ImageCodecs;
using Aardvark.ImageCodecs.Av1;
using Aardvark.ImageCodecs.Heif;
using Xunit;
using Xunit.Abstractions;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// AVIF against libavif + dav1d (tests/oracle/gen_avif.py): the decoded YUV planes bit exact against dav1d, the RGB
/// conversion within one step of avifdec (bilinear chroma upsampling), and the container features (transforms, alpha,
/// grids, metadata, sequences). AV1 inter streams (tests/fixtures/av1, aomenc) bit exact against dav1d; the libaom test
/// vectors and the Argon conformance streams from the test-data repository bit exact against their MD5s.
/// </summary>
public class AvifTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Fixtures => Files("avif", ".avif");
    public static IEnumerable<object[]> Streams => Files("av1", ".ivf");
    public static IEnumerable<object[]> AomVectors => ThirdPartyFiles("av1/aom", ".ivf");

    static JsonElement Json(string path) => JsonDocument.Parse(File.ReadAllText(path + ".json")).RootElement;

    static int Int(JsonElement j, string name, int def = -1) => j.TryGetProperty(name, out var v) ? v.GetInt32() : def;

    /// <summary>The stored pixels, as avifdec writes them (no rotation, mirroring or crop).</summary>
    static readonly AvifDecodeOptions Stored = new() { ApplyOrientation = false, ApplyCrop = false };

    /// <summary>The planes as y4m / raw yuv files hold them: 8 bit samples as bytes, deeper ones little endian.</summary>
    static void WritePlanes(Stream o, YuvImage y, bool grayChroma = false)
    {
        for (int p = 0; p < (grayChroma ? 3 : y.NumPlanes); p++)
        {
            int w = y.PlaneWidth(p), h = y.PlaneHeight(p);
            var row = new byte[w * (y.BitDepth > 8 ? 2 : 1)];
            for (int r = 0; r < h; r++)
            {
                for (int x = 0; x < w; x++)
                {
                    ushort v = p < y.NumPlanes ? y.Planes[p][r * y.Stride[p] + x] : (ushort)(1 << (y.BitDepth - 1));
                    if (y.BitDepth > 8) { row[2 * x] = (byte)v; row[2 * x + 1] = (byte)(v >> 8); }
                    else row[x] = (byte)v;
                }
                o.Write(row);
            }
        }
    }

    static string Sha256(YuvImage y)
    {
        var ms = new MemoryStream();
        WritePlanes(ms, y);
        return Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant();
    }

    /// <summary>The YUV planes of every image (the primary item, or each frame of the sequence).</summary>
    static List<YuvImage> DecodeYuv(byte[] data)
    {
        var rd = new AvifReader(data, Stored);
        var r = new List<YuvImage>();
        if (rd.ColorTrack is HeifTrack t)
        {
            var dec = new Av1Decoder(new Av1DecoderOptions());
            if (t.Av1C is IsoBox ab && Av1Config.Parse(data, ab.Payload, ab.End) is Av1Config c && c.ConfigObus.Length > 0) dec.Decode(c.ConfigObus, 0, c.ConfigObus.Length);
            foreach (var s in t.Samples)
            {
                dec.Output.Clear();
                dec.Decode(data, (int)s.Offset, s.Size);
                r.Add(AvifReader.FromPicture(dec.Output[^1]));
            }
        }
        else r.Add(rd.DecodeYuv(rd.Primary!));
        return r;
    }

    /// <summary>
    /// avifdec's output in our sample range: 8 bit as is, deeper as the stored range (avifdec scales to 0..65535);
    /// gray written as RGB taken back to one channel.
    /// </summary>
    static RawImage RefInStoredRange(RawImage r, int bitDepth, int channels)
    {
        if (r.Channels == 3 && channels == 1 || r.Channels == 4 && channels == 2)
        {
            var g = RawImage.Create(r.Width, r.Height, channels == 1 ? ColorModel.Gray : ColorModel.GrayAlpha, r.SampleType, channels);
            int n = r.Width * r.Height;
            for (int i = 0; i < n; i++)
                for (int c = 0; c < channels; c++)
                {
                    int sc = c == 0 ? 0 : r.Channels - 1;
                    if (r.SampleType == SampleType.UInt8) ((byte[])g.Data)[i * channels + c] = ((byte[])r.Data)[i * r.Channels + sc];
                    else ((ushort[])g.Data)[i * channels + c] = ((ushort[])r.Data)[i * r.Channels + sc];
                }
            r = g;
        }
        if (bitDepth <= 8) return r;
        var d = (ushort[])r.Data;
        var o = RawImage.Create(r.Width, r.Height, r.Color, SampleType.UInt16, r.Channels);
        var od = (ushort[])o.Data;
        int max = (1 << bitDepth) - 1;
        for (int i = 0; i < d.Length; i++) od[i] = (ushort)Math.Round(d[i] * (double)max / 65535);
        return o;
    }

    /// <summary>
    /// 8 bit RGBA with the colour multiplied by alpha: un-premultiplied colour is ill-conditioned where alpha is small
    /// (one step of the stored colour becomes 255 / alpha steps), so premultiplied files are compared like this.
    /// </summary>
    static RawImage Premultiply(RawImage s)
    {
        var d = (byte[])((byte[])s.Data).Clone();
        for (int i = 0; i < d.Length; i += 4)
            for (int c = 0; c < 3; c++) d[i + c] = (byte)((d[i + c] * d[i + 3] + 127) / 255);
        var r = RawImage.Create(s.Width, s.Height, s.Color, s.SampleType, s.Channels);
        d.CopyTo((byte[])r.Data, 0);
        return r;
    }

    static string RefPath(string path, int frames, int i) => frames == 1 ? path + ".ref" : $"{path}.{i}.ref";

    // ------------------------------------------------------------------ decoder vs. references

    [Theory, MemberData(nameof(Fixtures))]
    public void YuvMatchesDav1d(string file)
    {
        var path = Fixture(file);
        var j = Json(path);
        var want = j.GetProperty("yuv").EnumerateArray().Select(e => e.GetString()).ToArray();
        var got = DecodeYuv(File.ReadAllBytes(path));
        Assert.Equal(want.Length, got.Count);
        Assert.Equal(j.GetProperty("bitDepth").GetInt32(), got[0].BitDepth);
        for (int i = 0; i < want.Length; i++) Assert.True(want[i] == Sha256(got[i]), $"frame {i}: YUV differs from dav1d");
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void RgbMatchesAvifdec(string file)
    {
        var path = Fixture(file);
        var data = File.ReadAllBytes(path);
        var j = Json(path);
        int frames = j.GetProperty("frames").GetInt32(), bd = j.GetProperty("bitDepth").GetInt32();
        var imgs = Avif.DecodeAll(data, Stored);
        Assert.Equal(frames, imgs.Length);
        double worst = 0;
        for (int i = 0; i < frames; i++)
        {
            var img = imgs[i];
            Assert.Equal(bd <= 8 ? SampleType.UInt8 : SampleType.UInt16, img.SampleType);
            var expected = RefInStoredRange(LoadRef(RefPath(path, frames, i)), bd, img.Channels);
            if (file.Contains("premultiplied")) { expected = Premultiply(expected); img = Premultiply(img); }
            worst = Math.Max(worst, MaxDiff(expected, img));
        }
        output.WriteLine($"{file}: max difference {worst}");
        // libavif converts 8 bit limited range 4:2:0 in fixed point: a few samples are two steps off; premultiplied
        // files round twice (avifdec's integer un-premultiply, then the premultiply here)
        double tolerance = file.Contains("limited_range") || file.Contains("premultiplied") ? 2 : 1;
        Assert.True(worst <= tolerance, $"max difference {worst} to avifdec");
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void InfoMatchesDecode(string file)
    {
        var path = Fixture(file);
        var data = File.ReadAllBytes(path);
        var j = Json(path);
        var info = Avif.GetInfo(data);
        var img = Avif.Decode(data);
        Assert.Equal((info.Width, info.Height, info.Channels, info.SampleType, info.Color), (img.Width, img.Height, img.Channels, img.SampleType, img.Color));
        Assert.Equal(j.GetProperty("bitDepth").GetInt32(), info.BitsPerSample);
        Assert.Equal(j.GetProperty("frames").GetInt32(), info.FrameCount);
        Assert.Equal(j.TryGetProperty("animated", out _), info.IsAnimated);
        Assert.Equal(ImageFormat.Avif, ImageCodec.DetectFormat(data));
        var generic = ImageCodec.GetInfo(data);
        Assert.Equal(info with { Metadata = generic.Metadata }, generic);
        // single and multithreaded decoding agree
        var st = Avif.Decode(data, new AvifDecodeOptions { Multithreaded = false });
        Assert.Equal(img.GetBytes().ToArray(), st.GetBytes().ToArray());
    }

    // ------------------------------------------------------------------ container features

    /// <summary>The stored image turned 90 degrees anti-clockwise <paramref name="quarters"/> times (irot).</summary>
    static RawImage RotateCcw(RawImage s, int quarters)
    {
        for (int q = 0; q < (quarters & 3); q++)
        {
            int w = s.Width, h = s.Height, c = s.Channels, bps = s.GetBytes().Length / (w * h * c);
            var r = RawImage.Create(h, w, s.Color, s.SampleType, c);
            var src = s.GetBytes(); var dst = r.GetBytes();
            int px = c * bps;
            // out(x', y') = in(w - 1 - y', x')
            for (int y2 = 0; y2 < w; y2++)
                for (int x2 = 0; x2 < h; x2++)
                    src.Slice(((x2 * w) + (w - 1 - y2)) * px, px).CopyTo(dst.Slice((y2 * h + x2) * px, px));
            s = r;
        }
        return s;
    }

    /// <summary>imir: axis 0 swaps top and bottom, axis 1 left and right.</summary>
    static RawImage Mirror(RawImage s, int axis)
    {
        int w = s.Width, h = s.Height, px = s.GetBytes().Length / (w * h);
        var r = RawImage.Create(w, h, s.Color, s.SampleType, s.Channels);
        var src = s.GetBytes(); var dst = r.GetBytes();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int sx = axis == 1 ? w - 1 - x : x, sy = axis == 0 ? h - 1 - y : y;
                src.Slice((sy * w + sx) * px, px).CopyTo(dst.Slice((y * w + x) * px, px));
            }
        return r;
    }

    [Theory]
    [InlineData("avif/irot1.avif")]
    [InlineData("avif/irot3_imir0.avif")]
    [InlineData("avif/imir1.avif")]
    [InlineData("avif/clap.avif")]
    public void TransformsApplied(string file)
    {
        var path = Fixture(file);
        var data = File.ReadAllBytes(path);
        var j = Json(path);
        var stored = Avif.Decode(data, Stored);
        var expected = stored;
        if (j.TryGetProperty("crop", out var crop))
        {
            var c = crop.EnumerateArray().Select(e => e.GetInt32()).ToArray();
            expected = ImageOps.Crop(expected, c[0], c[1], c[2], c[3]);
        }
        int irot = Int(j, "irot", 0), imir = Int(j, "imir");
        expected = RotateCcw(expected, irot);
        if (imir >= 0) expected = Mirror(expected, imir);
        var img = Avif.Decode(data);
        AssertEqual(expected, img);
        Assert.Equal(1, img.Metadata!.Orientation);
        var info = Avif.GetInfo(data);
        Assert.Equal((expected.Width, expected.Height), (info.Width, info.Height));
        Assert.Equal(irot != 0 || imir >= 0, info.OrientationApplied);

        // without ApplyOrientation: stored order (cropped), the orientation as metadata
        var kept = Avif.Decode(data, new AvifDecodeOptions { ApplyOrientation = false });
        Assert.Equal(irot != 0 || imir >= 0 ? info.Orientation : 1, kept.Metadata!.Orientation);
        Assert.Equal(ImageOps.ApplyOrientation(kept, kept.Metadata.Orientation).GetBytes().ToArray(), img.GetBytes().ToArray());
    }

    /// <summary>
    /// An overlay (iovl, made by gen_avif.py; libavif does not read overlays): the inputs' avifdec references pasted on
    /// the fill colour, the second input partly outside the canvas.
    /// </summary>
    /// <summary>The overlay compositor must not grow the stack per pixel (it once overflowed macOS test threads).</summary>
    [Fact]
    public void OverlayOnSmallStack()
    {
        var data = File.ReadAllBytes(Fixture("avif/overlay/overlay.avif"));
        Exception? error = null;
        var t = new Thread(() => { try { Avif.Decode(data, new AvifDecodeOptions { Multithreaded = false }); } catch (Exception e) { error = e; } }, 512 << 10);
        t.Start();
        t.Join();
        Assert.Null(error);
    }

    [Fact]
    public void Overlay()
    {
        var path = Fixture("avif/overlay/overlay.avif");
        var j = Json(path);
        var c = j.GetProperty("canvas").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        var fill = j.GetProperty("fill").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        var expected = RawImage.Create(c[0], c[1], ColorModel.Rgb, SampleType.UInt8, 3);
        var ed = (byte[])expected.Data;
        for (int i = 0; i < c[0] * c[1]; i++)
            for (int k = 0; k < 3; k++) ed[i * 3 + k] = (byte)Math.Round(fill[k] / 65535.0 * 255);
        foreach (var inp in j.GetProperty("inputs").EnumerateArray())
        {
            var r = LoadRef(Fixture("avif/" + inp.GetProperty("file").GetString()) + ".ref");
            int ox = inp.GetProperty("x").GetInt32(), oy = inp.GetProperty("y").GetInt32();
            var rd = (byte[])r.Data;
            for (int y = 0; y < r.Height; y++)
                for (int x = 0; x < r.Width; x++)
                {
                    int cx = ox + x, cy = oy + y;
                    if (cx < 0 || cy < 0 || cx >= c[0] || cy >= c[1]) continue;
                    for (int k = 0; k < 3; k++) ed[(cy * c[0] + cx) * 3 + k] = rd[(y * r.Width + x) * r.Channels + k];
                }
        }
        var data = File.ReadAllBytes(path);
        var img = Avif.Decode(data);
        Assert.True(MaxDiff(expected, img) <= 1, $"max difference {MaxDiff(expected, img)}");
        var info = Avif.GetInfo(data);
        Assert.Equal((c[0], c[1], 3), (info.Width, info.Height, info.Channels));
    }

    [Fact]
    public void Thumbnail()
    {
        var data = File.ReadAllBytes(Fixture("avif/overlay/thumbnail.avif"));
        Assert.True(MaxDiff(LoadRef(Fixture("avif/nat_420_s6.avif.ref")), Avif.Decode(data)) <= 1);
        var t = Avif.DecodeThumbnail(data)!;
        Assert.True(MaxDiff(LoadRef(Fixture("avif/tiny.avif.ref")), t) <= 1);
        Assert.Null(Avif.DecodeThumbnail(File.ReadAllBytes(Fixture("avif/tiny.avif"))));
    }

    [Fact]
    public void DepthAuxiliaryImage()
    {
        var data = File.ReadAllBytes(Fixture("avif/overlay/depth.avif"));
        var img = Avif.Decode(data);
        Assert.Equal(ColorModel.Rgb, img.Color);   // a depth map is no alpha
        var aux = Avif.DecodeAuxiliaryImages(data);
        Assert.Single(aux);
        Assert.Equal("urn:mpeg:hevc:2015:auxid:2", aux[0].AuxType);
        Assert.True(MaxDiff(LoadRef(Fixture("avif/gray_400.avif.ref")), aux[0].Image) <= 1);
    }

    [Fact]
    public void ExifAndXmpItems()
    {
        var data = File.ReadAllBytes(Fixture("avif/exif_xmp.avif"));
        var img = Avif.Decode(data);
        Assert.Contains("PixCodecTest", Encoding.ASCII.GetString(img.Metadata!.Exif!));
        Assert.StartsWith("MM", Encoding.ASCII.GetString(img.Metadata.Exif!, 0, 2));
        Assert.Contains("AVIF fixture", Encoding.UTF8.GetString(img.Metadata.Xmp!));
        var (exif, xmp) = Avif.GetMetadataItems(data);
        Assert.Equal(img.Metadata.Exif, exif);
        Assert.Equal(img.Metadata.Xmp, xmp);
        var meta = ImageCodec.ReadMetadata(data);
        Assert.Equal(ImageFormat.Avif, meta.Format);
        Assert.Equal("PixCodecTest", meta.Make);
    }

    [Fact]
    public void IccAndNclx()
    {
        var icc = Avif.Decode(File.ReadAllBytes(Fixture("avif/icc.avif")));
        Assert.NotNull(icc.Metadata!.IccProfile);
        Assert.Equal("acsp", Encoding.ASCII.GetString(icc.Metadata.IccProfile!, 36, 4));
        // nclx without ICC: BT.2020 PQ is reported as such, sRGB (BT.709 primaries) as sRGB
        var pqData = File.ReadAllBytes(Fixture("avif/bt2020_pq_10.avif"));
        var pq = Avif.Decode(pqData);
        Assert.Equal(TransferFunction.Pq, pq.Metadata!.Transfer);
        Assert.Equal(TransferFunction.Pq, Avif.GetInfo(pqData).Transfer);
        Assert.Equal(TransferFunction.Srgb, Avif.Decode(File.ReadAllBytes(Fixture("avif/bt709.avif"))).Metadata!.Transfer);
    }

    [Fact]
    public void AlphaChannels()
    {
        var a = Avif.Decode(File.ReadAllBytes(Fixture("avif/alpha.avif")));
        Assert.Equal(ColorModel.Rgba, a.Color);
        var noAlpha = Avif.Decode(File.ReadAllBytes(Fixture("avif/alpha.avif")), new AvifDecodeOptions { IgnoreAlpha = true });
        Assert.Equal(ColorModel.Rgb, noAlpha.Color);
        Assert.Equal(ColorModel.Rgba, Avif.Decode(File.ReadAllBytes(Fixture("avif/grid_alpha_3x1.avif"))).Color);
        Assert.Equal(SampleType.UInt16, Avif.Decode(File.ReadAllBytes(Fixture("avif/alpha_lossless_10.avif"))).SampleType);
    }

    [Fact]
    public void Sequence()
    {
        var data = File.ReadAllBytes(Fixture("avif/seq.avif"));
        var a = Avif.DecodeAnimation(data);
        Assert.True(a.IsAnimated);
        Assert.Equal(8, a.Frames.Count);
        Assert.All(a.Frames, f => Assert.Equal(40, f.DurationMs, 6));
        Assert.True(a.Frames[0].IsSync);
        Assert.True(a.Frames[4].IsSync);
        Assert.False(a.Frames[1].IsSync);
        var all = ImageCodec.DecodeAll(data);
        Assert.Equal(8, all.Length);
        Assert.Equal(a.Frames[0].Image.GetBytes().ToArray(), ImageCodec.Decode(data).GetBytes().ToArray());
        var al = Avif.DecodeAnimation(File.ReadAllBytes(Fixture("avif/seq_alpha.avif")));
        Assert.All(al.Frames, f => Assert.Equal(ColorModel.Rgba, f.Image.Color));
        Assert.All(al.Frames, f => Assert.Equal(100, f.DurationMs, 6));
    }

    [Fact]
    public void GainMap()
    {
        var path = Fixture("avif/gainmap.avif");
        var data = File.ReadAllBytes(path);
        var j = Json(path).GetProperty("gainMap");
        var gm = Avif.GetGainMap(data)!;
        Assert.NotNull(gm);
        Assert.Equal(0, gm.Version);
        Assert.Equal(j.GetProperty("alternatePrimaries").GetInt32(), gm.AlternatePrimaries);
        Assert.Equal(j.GetProperty("alternateTransfer").GetInt32(), gm.AlternateTransferCharacteristics);
        Assert.Equal(TransferFunction.Pq, gm.AlternateTransfer);
        // ISO 21496-1 C.2: minimum_version, writer_version (16 bit), flags byte, then base / alternate headroom fractions
        var m = gm.Metadata;
        uint U32(int o) => (uint)(m[o] << 24 | m[o + 1] << 16 | m[o + 2] << 8 | m[o + 3]);
        Assert.Equal(0, m[0] << 8 | m[1]);
        var bh = j.GetProperty("baseHeadroom").EnumerateArray().Select(e => (uint)e.GetInt64()).ToArray();
        var ah = j.GetProperty("alternateHeadroom").EnumerateArray().Select(e => (uint)e.GetInt64()).ToArray();
        Assert.Equal(bh, new[] { U32(5), U32(9) });
        Assert.Equal(ah, new[] { U32(13), U32(17) });
        // the gain map image: bit exact planes, its stored size
        var y = gm.Reader.DecodeYuv(gm.Reader.File.Items[gm.GainMapItemId]);
        Assert.Equal(j.GetProperty("yuv").GetString(), Sha256(y));
        var img = gm.DecodeImage();
        Assert.Equal((j.GetProperty("width").GetInt32(), j.GetProperty("height").GetInt32()), (img.Width, img.Height));
        // the primary image is the base image
        var b = Avif.Decode(data);
        Assert.Equal((128, 96), (b.Width, b.Height));
        Assert.Null(Avif.GetGainMap(File.ReadAllBytes(Fixture("avif/nat_420_s6.avif"))));
    }

    /// <summary>A PQ reference (avifgainmaputil tonemap, 16 bit BT.2100 PQ) as linear light, 203 cd/m² = 1.0 (BT.2408).</summary>
    static RawImage PqToLinear(RawImage r)
    {
        const double m1 = 2610.0 / 16384, m2 = 2523.0 / 4096 * 128, c1 = 3424.0 / 4096, c2 = 2413.0 / 4096 * 32, c3 = 2392.0 / 4096 * 32;
        var s = (ushort[])r.Data;
        var f = new float[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            double p = Math.Pow(s[i] / 65535.0, 1 / m2);
            f[i] = (float)(Math.Pow(Math.Max(p - c1, 0) / (c2 - c3 * p), 1 / m1) * 10000 / 203);
        }
        return new RawImage(r.Width, r.Height, ColorModel.Rgb, f);
    }

    /// <summary>
    /// A gain map image (integer samples, <paramref name="max"/> = 1.0) bilinearly upsampled to w x h with pixel centres
    /// aligned, as Float32 0..1: libavif resamples that way, where ISO 21496-1 6.2.2 NOTE 1 prefers co-sited samples
    /// (which <see cref="Aardvark.ImageCodecs.GainMap.Apply(RawImage, Aardvark.ImageCodecs.GainMap, double?)"/> uses).
    /// </summary>
    static RawImage CentredUpsample(RawImage g, int w, int h, double max)
    {
        int gw = g.Width, gh = g.Height, ch = g.Channels;
        double V(long i) => g.SampleType == SampleType.UInt8 ? ((byte[])g.Data)[i] : ((ushort[])g.Data)[i];
        var up = new float[w * h * ch];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double fx = Math.Clamp((x + 0.5) * gw / w - 0.5, 0, gw - 1), fy = Math.Clamp((y + 0.5) * gh / h - 0.5, 0, gh - 1);
                int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, gw - 1), y1 = Math.Min(y0 + 1, gh - 1);
                double ax = fx - x0, ay = fy - y0;
                for (int c = 0; c < ch; c++)
                {
                    double v = (V((y0 * gw + x0) * ch + c) * (1 - ax) + V((y0 * gw + x1) * ch + c) * ax) * (1 - ay)
                             + (V((y1 * gw + x0) * ch + c) * (1 - ax) + V((y1 * gw + x1) * ch + c) * ax) * ay;
                    up[(y * w + x) * ch + c] = (float)(v / max);
                }
            }
        return new RawImage(w, h, ch == 1 ? ColorModel.Gray : ColorModel.Rgb, up, ch);
    }

    void AssertStops(RawImage expected, RawImage actual, double max, double mean, string what)
    {
        var e = GainMapTests.StopError(expected, actual);
        output.WriteLine($"{what}: max {e.Max:F4} mean {e.Mean:F5} stops");
        Assert.True(e.Max <= max && e.Mean <= mean, $"{what}: max {e.Max:F4} (<= {max}) mean {e.Mean:F5} (<= {mean}) stops");
    }

    /// <summary>
    /// The gain map applied (ISO 21496-1 clause 6) at headroom 2.5 against avifgainmaputil tonemap (libavif 1.4.2): the
    /// alternate is BT.2100 PQ (the tmap item's nclx) and use_base_colour_space 0, so the sRGB base is converted to
    /// BT.2020 first (Annex B). With the gain map resampled as libavif does the results agree closely; with the co-sited
    /// resampling of 6.2.2 NOTE 1 they differ by the half gain map pixel shift.
    /// </summary>
    [Theory]
    [InlineData("avif/gainmap.avif", 0.1, 0.02)]
    [InlineData("avif/gainmap/deep.avif", 0.08, 0.01)]
    public void GainMapAppliedLikeLibavif(string file, double maxStops, double meanStops)
    {
        var path = Fixture(file);
        var data = File.ReadAllBytes(path);
        var gm = Avif.GetGainMap(data)!.ToGainMap()!;
        Assert.False(gm.Metadata.UseBaseColourSpace);
        Assert.Equal(new IccCicp(9, 16, 0, true), IccProfile.Parse(gm.AlternateIcc!).Cicp);
        var expected = PqToLinear(LoadRef(path + ".tonemap.ref"));
        var mine = Avif.DecodeHdr(data, 2.5);
        TestUtil.AssertEqual(mine, ImageCodec.DecodeHdr(data, 2.5));
        Assert.Equal(IccProfile.LinearRec2020.RedColorant!.Value.X, IccProfile.Parse(mine.Metadata!.IccProfile!).RedColorant!.Value.X, 1e-3);
        var b = Avif.Decode(data);
        bool deep = gm.Image.SampleType == SampleType.UInt16;
        Assert.Equal(deep ? 1023 : 0, gm.ImageMax);   // 10 bit samples hold 0..1023
        var centred = Aardvark.ImageCodecs.GainMap.Apply(b, CentredUpsample(gm.Image, b.Width, b.Height, deep ? 1023 : 255), gm.Metadata, gm.AlternateIcc, 2.5);
        AssertStops(expected, centred, maxStops, meanStops, file + ", centred resampling");
        if (gm.Image.Width == b.Width) AssertStops(expected, mine, maxStops, meanStops, file);
        else AssertStops(expected, mine, 0.6, 0.08, file + ", co-sited resampling");
    }

    /// <summary>
    /// 4.5: the gain map has the orientation of the base image. libavif keeps the base's irot / imir (and clap) on the gain
    /// map item too and applies the gain map in the stored geometry; turned upright, the gain map is transformed like the
    /// base (a clean aperture scaled to the gain map's size). With the resampling libavif uses (which commutes with the
    /// transforms) the upright result is libavif's turned; the co-sited one is off by up to a gain map pixel after mirroring.
    /// </summary>
    [Fact]
    public void GainMapFollowsTheBaseTransforms()
    {
        var path = Fixture("avif/gainmap/irot.avif");
        var data = File.ReadAllBytes(path);
        var expected = PqToLinear(LoadRef(path + ".tonemap.ref"));
        RawImage Centred(byte[] d, AvifDecodeOptions? o)
        {
            var b = Avif.Decode(d, o);
            var g = Avif.GetGainMap(d, o)!.ToGainMap()!;
            return Aardvark.ImageCodecs.GainMap.Apply(b, CentredUpsample(g.Image, b.Width, b.Height, 255), g.Metadata, g.AlternateIcc, 2.5);
        }
        // the stored geometry, as libavif
        Assert.Equal((64, 48), (Avif.DecodeHdr(data, 2.5, Stored).Width, Avif.DecodeHdr(data, 2.5, Stored).Height));
        AssertStops(expected, Centred(data, Stored), 0.1, 0.02, "stored");
        // upright: irot 1 (90 degrees anti-clockwise), then imir 1 (left-right)
        static RawImage Turn(RawImage i) => ImageOps.FlipHorizontal(ImageOps.Rotate270(i));
        Assert.Equal((24, 32), (Avif.GetGainMap(data)!.ToGainMap()!.Image.Width, Avif.GetGainMap(data)!.ToGainMap()!.Image.Height));
        AssertStops(Turn(expected), Centred(data, null), 0.1, 0.02, "upright");
        var upright = Avif.DecodeHdr(data, 2.5);
        Assert.Equal((48, 64), (upright.Width, upright.Height));
        AssertStops(Turn(expected), upright, 0.9, 0.12, "upright, co-sited");
        // clap 48 x 40 at (8, 4) of the 64 x 48 base: (4, 2, 24, 20) of the 32 x 24 gain map, then turned
        var clap = File.ReadAllBytes(Fixture("avif/gainmap/clap.avif"));
        var cg = Avif.GetGainMap(clap)!.ToGainMap()!;
        Assert.Equal((20, 24), (cg.Image.Width, cg.Image.Height));
        var cropped = Avif.DecodeHdr(clap, 2.5);
        Assert.Equal((40, 48), (cropped.Width, cropped.Height));
        // (but for the outermost pixels, where the cropped gain map's samples are clamped)
        static RawImage Inner(RawImage i) => ImageOps.Crop(i, 1, 1, i.Width - 2, i.Height - 2);
        TestUtil.AssertEqual(Inner(Turn(ImageOps.Crop(Centred(clap, Stored), 8, 4, 48, 40))), Inner(Centred(clap, null)), 1e-5);
    }

    /// <summary>A tmap item of a version other than 0 is ignored (as libavif 1.4.2 does): the base image converted to linear light.</summary>
    [Fact]
    public void UnknownTmapVersionIsIgnored()
    {
        var data = File.ReadAllBytes(Fixture("avif/gainmap.avif"));
        var g = Avif.GetGainMap(data)!;
        // the tmap payload: version byte, then the metadata (minimum_version 0, writer_version 0, flags, headrooms...)
        int at = data.AsSpan().IndexOf(g.Metadata.AsSpan(0, 21)) - 1;
        Assert.Equal(0, data[at]);
        var v1 = (byte[])data.Clone();
        v1[at] = 1;
        var g1 = Avif.GetGainMap(v1)!;
        Assert.Equal(1, g1.Version);
        Assert.Null(g1.ParsedMetadata);
        Assert.Null(g1.ToGainMap());
        TestUtil.AssertEqual(Aardvark.ImageCodecs.GainMap.LinearRgb(Avif.Decode(v1)), Avif.DecodeHdr(v1));
        TestUtil.AssertEqual(Avif.DecodeHdr(v1), ImageCodec.DecodeHdr(v1));
        Assert.NotNull(g.ParsedMetadata);
    }

    public static IEnumerable<object[]> LibavifFiles
    {
        get
        {
            var refs = TestDataDir == null ? null : Path.Combine(TestDataDir, "fixtures", "avif", "libavif", "refs.txt");
            if (refs == null || !File.Exists(refs)) yield break;
            foreach (var l in File.ReadLines(refs))
                if (l.Length > 0 && l[0] != '#') yield return new object[] { l.Split(' ')[0] };
        }
    }

    /// <summary>
    /// libavif's test files (fixtures/avif/libavif of the test-data repository, fetch_libavif_testdata.sh): what avifdec
    /// (dav1d) decodes has to decode to the same planes; files avifdec rejects may be rejected or decoded, but never
    /// with anything but the format exceptions.
    /// </summary>
    [TestDataTheory("fixtures/avif/libavif/white_1x1.avif"), MemberData(nameof(LibavifFiles))]
    public void LibavifTestFiles(string file)
    {
        var dir = TestData(Path.Combine("fixtures", "avif", "libavif"));
        var r = File.ReadLines(Path.Combine(dir, "refs.txt")).First(l => l.StartsWith(file + " ")).Split(' ');
        var data = File.ReadAllBytes(Path.Combine(dir, file));
        Assert.Equal(r[1], Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant());
        if (r[2] == "fail")
        {
            try
            {
                var img = Avif.Decode(data);
                output.WriteLine($"avifdec rejects it, decoded {img.Width}x{img.Height} {img.Color}");
            }
            catch (Exception e) when (e is ImageFormatException or ImageNotSupportedException) { output.WriteLine("rejected: " + e.Message); }
            return;
        }
        var y = DecodeYuv(data)[0];
        Assert.Equal((int.Parse(r[2]), int.Parse(r[3]), int.Parse(r[4])), (y.Width, y.Height, y.BitDepth));
        Assert.Equal(r[5], y.Mono ? "mono" : $"{y.SubX}{y.SubY}");
        Assert.Equal(r[6], Sha256(y));
        var info = Avif.GetInfo(data);
        var full = Avif.Decode(data);
        Assert.Equal((info.Width, info.Height, info.Channels), (full.Width, full.Height, full.Channels));
        Avif.GetGainMap(data)?.DecodeImage();
        Avif.DecodeAll(data);
    }

    static string AlphaSha256(RawImage img)
    {
        var d = (byte[])img.Data;
        var a = new byte[img.Width * img.Height];
        for (int i = 0; i < a.Length; i++) a[i] = d[i * img.Channels + img.Channels - 1];
        return Convert.ToHexString(SHA256.HashData(a)).ToLowerInvariant();
    }

    /// <summary>
    /// libavif's color_grid_alpha_nogrid.avif: a 1x2 grid whose alpha is an auxl item on each tile, without an alpha grid.
    /// The alpha channel as avifdec 1.4.2 (-d 8) writes it.
    /// </summary>
    [TestDataFact("fixtures/avif/libavif/color_grid_alpha_nogrid.avif")]
    public void AlphaOnGridTiles()
    {
        var data = File.ReadAllBytes(Path.Combine(TestData(Path.Combine("fixtures", "avif", "libavif")), "color_grid_alpha_nogrid.avif"));
        var img = Avif.Decode(data);
        Assert.Equal((80, 80, ColorModel.Rgba), (img.Width, img.Height, img.Color));
        Assert.Equal("248f2d33c474a17c8e5a7c5b125ee587ff5be05be00f1edc54aff9a0f564179f", AlphaSha256(img));
        Assert.Equal(ColorModel.Rgba, Avif.GetInfo(data).Color);
        Assert.Equal(ColorModel.Rgb, Avif.Decode(data, new AvifDecodeOptions { IgnoreAlpha = true }).Color);
    }

    /// <summary>
    /// Grid tiles (gen_avif.py container): 'iden' tiles with irot 2 (MIAF 7.3.11.4.1; avifdec refuses them) against
    /// avifdec's decode of the coded tiles turned per HEIF 6.5.10; irot on the coded tiles themselves is ignored, as
    /// avifdec does (HEIF 6.6.1 would apply it).
    /// </summary>
    [Theory]
    [InlineData("avif/container/grid_iden.avif")]
    [InlineData("avif/container/grid_tile_irot.avif")]
    public void GridTileTransforms(string file)
    {
        var path = Fixture(file);
        var data = File.ReadAllBytes(path);
        var img = Avif.Decode(data);
        var expected = LoadRef(path + ".ref");
        Assert.True(MaxDiff(expected, img) <= 1, $"max difference {MaxDiff(expected, img)}");
        var info = Avif.GetInfo(data);
        Assert.Equal((img.Width, img.Height, img.Color), (info.Width, info.Height, info.Color));
    }

    /// <summary>
    /// imir, irot, clap in that association order (gen_avif.py container): applied as avifdec 1.4.2 does, clap on the stored
    /// image, then irot, then imir. Without ApplyOrientation the stored order of that crop, which the reported
    /// orientation turns upright; GetInfo reports the sizes of either.
    /// </summary>
    [Fact]
    public void TransformOrder()
    {
        var path = Fixture("avif/container/transform_order.avif");
        var data = File.ReadAllBytes(path);
        var expected = LoadRef(path + ".ref");
        var img = Avif.Decode(data);
        Assert.True(MaxDiff(expected, img) <= 1, $"max difference {MaxDiff(expected, img)}");
        Assert.Equal((48, 64), (Avif.GetInfo(data).Width, Avif.GetInfo(data).Height));
        var opt = new AvifDecodeOptions { ApplyOrientation = false };
        var kept = Avif.Decode(data, opt);
        Assert.Equal((64, 48), (kept.Width, kept.Height));
        Assert.Equal((64, 48), (Avif.GetInfo(data, opt).Width, Avif.GetInfo(data, opt).Height));
        AssertEqual(img, ImageOps.ApplyOrientation(kept, kept.Metadata!.Orientation));
    }

    /// <summary>
    /// A tmap primary item with an irot of its own (gen_avif.py container; avifdec refuses it): the base's output image
    /// (avifdec's decode of gainmap/irot.avif) turned 180 degrees (HEIF Amd 1 6.6.2.4); the gain map follows.
    /// </summary>
    [Fact]
    public void TmapItemTransforms()
    {
        var path = Fixture("avif/container/tmap_primary_irot.avif");
        var data = File.ReadAllBytes(path);
        var img = Avif.Decode(data);
        var expected = LoadRef(path + ".ref");
        Assert.True(MaxDiff(expected, img) <= 1, $"max difference {MaxDiff(expected, img)}");
        var info = Avif.GetInfo(data);
        Assert.Equal((img.Width, img.Height), (info.Width, info.Height));
        var kept = Avif.Decode(data, new AvifDecodeOptions { ApplyOrientation = false });
        Assert.Equal((64, 48), (kept.Width, kept.Height));
        AssertEqual(img, ImageOps.ApplyOrientation(kept, kept.Metadata!.Orientation));
        Assert.Equal((64, 48), (Avif.GetInfo(data, new AvifDecodeOptions { ApplyOrientation = false }).Width, Avif.GetInfo(data, new AvifDecodeOptions { ApplyOrientation = false }).Height));
        // the gain map: the one of the base primary file turned too
        var original = Avif.GetGainMap(File.ReadAllBytes(Fixture("avif/gainmap/irot.avif")))!.ToGainMap()!.Image;
        AssertEqual(ImageOps.Rotate180(original), Avif.GetGainMap(data)!.ToGainMap()!.Image);
        var hdr = Avif.DecodeHdr(data, 2.5);
        Assert.Equal((img.Width, img.Height), (hdr.Width, hdr.Height));
    }

    /// <summary>The images side by side (same height, sample type and channels).</summary>
    static RawImage SideBySide(RawImage a, RawImage b)
    {
        var r = RawImage.Create(a.Width + b.Width, a.Height, a.Color, a.SampleType, a.Channels);
        int bpp = a.GetBytes().Length / (a.Width * a.Height);
        for (int y = 0; y < a.Height; y++)
        {
            a.GetBytes().Slice(y * a.Width * bpp, a.Width * bpp).CopyTo(r.GetBytes().Slice(y * r.Width * bpp));
            b.GetBytes().Slice(y * b.Width * bpp, b.Width * bpp).CopyTo(r.GetBytes().Slice((y * r.Width + a.Width) * bpp));
        }
        r.Metadata = a.Metadata;
        return r;
    }

    /// <summary>
    /// Derived items whose input is the tmap item of gainmap.avif (gen_avif.py gen_tmap_inputs; avifdec and avifgainmaputil
    /// 1.4.2 read none of them). HEIF Amd 1 6.6.2.4.1: the derivation takes the tmap item's image with the gain map applied,
    /// in the tmap item's colour, so DecodeHdr applies the gain map to the tmap input (at the requested headroom) and then
    /// the derivation: the references are the derivations of avifgainmaputil's tonemap of gainmap.avif (headroom 2.5,
    /// co-sited resampling tolerance as in <see cref="GainMapAppliedLikeLibavif"/>). Decode keeps the base image as input.
    /// </summary>
    [Theory]
    [InlineData("iden")]
    [InlineData("iovl")]
    [InlineData("grid")]
    public void DerivedItemOfTmapIsHdr(string kind)
    {
        var plain = File.ReadAllBytes(Fixture("avif/gainmap.avif"));
        var tm = PqToLinear(LoadRef(Fixture("avif/gainmap.avif.tonemap.ref")));
        var sdr = LoadRef(Fixture("avif/gainmap.avif.ref"));
        var path = Fixture($"avif/container/{kind}_tmap.avif");
        var data = File.ReadAllBytes(path);
        Func<RawImage, RawImage> derive = kind switch
        {
            "iden" => ImageOps.Rotate270,   // irot 1
            "grid" => i => SideBySide(i, i),
            _ => i => i,
        };
        RawImage expectedSdr = kind == "iovl" ? LoadRef(path + ".ref") : derive(sdr);
        RawImage expectedHdr = kind == "iovl" ? PqToLinear(LoadRef(path + ".tonemap.ref")) : derive(tm);
        var img = Avif.Decode(data);
        Assert.True(MaxDiff(expectedSdr, img) <= 1, $"max difference {MaxDiff(expectedSdr, img)}");
        var info = Avif.GetInfo(data);
        Assert.Equal((img.Width, img.Height), (info.Width, info.Height));
        var hdr = Avif.DecodeHdr(data, 2.5);
        TestUtil.AssertEqual(hdr, ImageCodec.DecodeHdr(data, 2.5));
        Assert.Equal((img.Width, img.Height, ColorModel.Rgb), (hdr.Width, hdr.Height, hdr.Color));
        Assert.Equal(IccProfile.LinearRec2020.RedColorant!.Value.X, IccProfile.Parse(hdr.Metadata!.IccProfile!).RedColorant!.Value.X, 1e-3);
        AssertStops(expectedHdr, hdr, 0.6, 0.08, kind + ", co-sited resampling");
        // the derivation of the plain file's HDR rendition, at any headroom
        foreach (double? h in new double?[] { 2.5, 0, null })
        {
            var tmap = Avif.DecodeHdr(plain, h);
            var mine = Avif.DecodeHdr(data, h);
            if (kind == "iovl") mine = ImageOps.Crop(mine, 16, 12, 128, 96);
            TestUtil.AssertEqual(derive(tmap), mine, 1e-6);
        }
        if (kind == "iovl")
        {
            // the fill is a PQ signal (the overlay has its inputs' colour, MIAF 7.3.11.3): ST 2084 with 203 cd/m² = 1.0
            var f = (float[])hdr.Data;
            var e = (float[])expectedHdr.Data;
            for (int c = 0; c < 3; c++) Assert.Equal(e[c], f[c], 1e-4);
        }
        if (kind == "iden")
        {
            var kept = Avif.DecodeHdr(data, 2.5, new AvifDecodeOptions { ApplyOrientation = false });
            Assert.Equal((128, 96), (kept.Width, kept.Height));
            TestUtil.AssertEqual(hdr, ImageOps.ApplyOrientation(kept, kept.Metadata!.Orientation));
        }
    }

    /// <summary>
    /// gen_avif.py container: an hvc1 primary item with an AV1 alternative in its altr group decodes the alternative
    /// (MIAF 7.3.12.2; avifdec gives up), as avifdec decodes the AV1 item alone; a protected AV1 item (scheme 'cenc') is
    /// decoded as plain AV1, as avifdec does (HEIF 10.2.2.2 says readers should fail).
    /// </summary>
    [Theory]
    [InlineData("avif/container/altr_hvc1.avif", "avif/nat_420_s6.avif.ref")]
    [InlineData("avif/container/protected.avif", "avif/container/protected.avif.ref")]
    public void AlternativesAndProtection(string file, string reference)
    {
        var data = File.ReadAllBytes(Fixture(file));
        var img = Avif.Decode(data);
        var expected = LoadRef(Fixture(reference));
        Assert.True(MaxDiff(expected, img) <= 1, $"max difference {MaxDiff(expected, img)}");
        var info = Avif.GetInfo(data);
        Assert.Equal((img.Width, img.Height, img.Color), (info.Width, info.Height, info.Color));
    }

    /// <summary>
    /// Sequences (gen_avif.py container, from seq.avif): a sample entry clap crops the frames (MIAF 7.4.4.1); a tkhd
    /// matrix turning 90 degrees is ignored, as avifdec 1.4.2 does (MIAF 7.4.3 allows it), and a tkhd size of 200x100
    /// scales the 128x80 frames to it, as avifdec does (bilinearly here: libavif's scaler differs a little). Frames 0 and
    /// 7 against avifdec.
    /// </summary>
    [Theory]
    [InlineData("avif/container/seq_clap.avif", 96, 64, 1)]
    [InlineData("avif/container/seq_matrix.avif", 200, 100, 8)]
    public void SequenceTransforms(string file, int w, int h, double tolerance)
    {
        var path = Fixture(file);
        var data = File.ReadAllBytes(path);
        var a = Avif.DecodeAnimation(data);
        Assert.Equal(8, a.Frames.Count);
        Assert.Equal((w, h), (a.Width, a.Height));
        foreach (int i in new[] { 0, 7 })
        {
            var expected = LoadRef($"{path}.{i}.ref");
            double d = MaxDiff(expected, a.Frames[i].Image);
            output.WriteLine($"{file} frame {i}: max difference {d}");
            Assert.True(d <= tolerance, $"frame {i}: max difference {d}");
        }
        // the track alone (no primary item): GetInfo has the sample entry's size, cropped
        var noMeta = (byte[])data.Clone();
        noMeta[data.AsSpan().IndexOf("meta"u8) + 1] = (byte)'x';   // the meta box renamed "mxta"
        Assert.Equal((w, h), (Avif.GetInfo(noMeta).Width, Avif.GetInfo(noMeta).Height));
        Assert.Equal((w, h), (Avif.Decode(noMeta).Width, Avif.Decode(noMeta).Height));
    }

    /// <summary>
    /// The loop count of MIAF Amd 1 Table C.1 (gen_avif.py container, from seq.avif with edit list duration 8): avifdec
    /// 1.4.2 reports the repeat counts Infinite (seq.avif), Unknown (no edit list: played forever per Table C.1), 0 (no
    /// RepeatEdits), 0 (R = 0.5), 3 (R = 3.25: ceil(R) plays), 3 (R = 4); LoopCount counts plays, 0 = forever.
    /// </summary>
    [Theory]
    [InlineData("avif/seq.avif", 0)]
    [InlineData("avif/container/seq_loop_none.avif", 0)]
    [InlineData("avif/container/seq_loop_once.avif", 1)]
    [InlineData("avif/container/seq_loop_r0_5.avif", 1)]
    [InlineData("avif/container/seq_loop_r3_25.avif", 4)]
    [InlineData("avif/container/seq_loop_r4.avif", 4)]
    public void SequenceLoopCount(string file, int plays)
    {
        Assert.Equal(plays, Avif.DecodeAnimation(File.ReadAllBytes(Fixture(file))).LoopCount);
    }

    /// <summary>
    /// Low-overhead files (HEIF Amd 2 Annex O, gen_avif.py mini): avifdec 1.4.2 cannot read the mini box, so the
    /// reference is its decode of the equivalent MetaBox file of O.4. alpha: orientation 6 (irot 3 on colour and alpha);
    /// deep: 10 bit, explicit codec types and CICP, orientation 2 (imir 1).
    /// </summary>
    [Theory]
    [InlineData("basic", 8)]
    [InlineData("alpha", 8)]
    [InlineData("deep", 10)]
    [InlineData("meta", 8)]
    [InlineData("gainmap", 8)]
    [InlineData("hdr", 8)]
    public void LowOverheadFiles(string name, int bits)
    {
        var path = Fixture($"avif/mini/{name}.avif");
        var data = File.ReadAllBytes(path);
        Assert.Equal(ImageFormat.Avif, ImageCodec.DetectFormat(data));
        var img = Avif.Decode(data);
        var expected = RefInStoredRange(LoadRef(path + ".ref"), bits, img.Channels);
        Assert.True(MaxDiff(expected, img) <= 1, $"max difference {MaxDiff(expected, img)}");
        var info = Avif.GetInfo(data);
        Assert.Equal((img.Width, img.Height, img.Color, bits), (info.Width, info.Height, info.Color, info.BitsPerSample));
    }

    /// <summary>The metadata of the mini files: Exif and XMP (deflate compressed there), ICC profile and gain map as in their sources.</summary>
    [Fact]
    public void LowOverheadMetadata()
    {
        var meta = File.ReadAllBytes(Fixture("avif/mini/meta.avif"));
        var src = Avif.GetMetadataItems(File.ReadAllBytes(Fixture("avif/exif_xmp.avif")));
        var (exif, xmp) = Avif.GetMetadataItems(meta);
        Assert.Equal(src.Exif, exif);
        Assert.Equal(src.Xmp, xmp);
        Assert.Equal("PixCodecTest", ImageCodec.ReadMetadata(meta).Make);
        Assert.Equal(Avif.Decode(File.ReadAllBytes(Fixture("avif/icc.avif"))).Metadata!.IccProfile, Avif.Decode(meta).Metadata!.IccProfile);
        var gmFile = File.ReadAllBytes(Fixture("avif/mini/gainmap.avif"));
        var gm = Avif.GetGainMap(gmFile)!;
        var srcGm = Avif.GetGainMap(File.ReadAllBytes(Fixture("avif/gainmap.avif")))!;
        Assert.Equal(srcGm.Metadata, gm.Metadata);
        Assert.Equal((9, 16), (gm.AlternatePrimaries, gm.AlternateTransferCharacteristics));
        AssertEqual(srcGm.DecodeImage(), gm.DecodeImage());
        Assert.NotNull(gm.ToGainMap());
    }

    /// <summary>
    /// mini/hdr.avif (gen_avif.py HDR_BODIES): the six HDR boxes of O.3.2 expand to the item 1 properties of O.4.6
    /// (entries 11 - 16, non-essential), with the bodies of 14496-12 12.1.6 - 12.1.9 and HEIF Amd 1 6.5.44 / 6.5.45.
    /// The cclv flags byte 0x3C (14496-12 12.1.8.2: cancel and persistence reserved, then primaries, min, max, avg
    /// present, 2 reserved) is followed by 36 bytes; reading it with the flags one bit off misplaced every later field.
    /// </summary>
    [Fact]
    public void LowOverheadHdrBoxes()
    {
        static byte[] Be(params (long v, int n)[] fields)
        {
            var r = new List<byte>();
            foreach (var (v, n) in fields) for (int i = n - 1; i >= 0; i--) r.Add((byte)(v >> (8 * i)));
            return r.ToArray();
        }
        var expected = new Dictionary<string, byte[]>
        {
            ["clli"] = Be((1000, 2), (400, 2)),
            ["mdcv"] = Be((13250, 2), (34500, 2), (7500, 2), (3000, 2), (34000, 2), (16000, 2), (15635, 2), (16450, 2), (10000000, 4), (50, 4)),
            ["cclv"] = Be((0x3C, 1), (13250, 4), (34500, 4), (7500, 4), (-3000, 4), (34000, 4), (16000, 4), (50000, 4), (4000000000, 4), (1000000000, 4)),
            ["amve"] = Be((3140000, 4), (15635, 2), (16450, 2)),
            // ItemFullProperty: version 0, flags 0
            ["reve"] = Be((0, 4), (50000, 4), (15635, 2), (16450, 2), (100000, 4), (15635, 2), (16450, 2)),
            ["ndwt"] = Be((0, 4), (2030000, 4)),
        };
        var data = HeifMini.Expand(File.ReadAllBytes(Fixture("avif/mini/hdr.avif")))!;
        var f = HeifFile.Parse(data);
        var main = f.Items[1];
        var seen = new List<string>();
        foreach (var pr in main.Properties)
        {
            var p = f.Properties[pr.Index];
            if (!expected.TryGetValue(p.Type, out var body)) continue;
            seen.Add(p.Type);
            Assert.False(pr.Essential, p.Type);
            Assert.Equal(body, data.AsSpan((int)p.Payload, (int)(p.End - p.Payload)).ToArray());
            Assert.Equal(11 + Array.IndexOf(new[] { "clli", "mdcv", "cclv", "amve", "reve", "ndwt" }, p.Type), pr.Index + 1);
        }
        Assert.Equal(new[] { "clli", "mdcv", "cclv", "amve", "reve", "ndwt" }, seen);
    }

    // ------------------------------------------------------------------ robustness

    [Fact]
    public void DamagedFilesFailCleanly()
    {
        var rnd = new Random(1234);
        foreach (var f in new[] { "avif/nat_420_s6.avif", "avif/alpha.avif", "avif/grid_2x2.avif", "avif/seq.avif", "avif/nat_422_10.avif", "avif/mini/gainmap.avif", "avif/mini/meta.avif" })
        {
            var good = File.ReadAllBytes(Fixture(f));
            for (int k = 0; k < 60; k++)
            {
                var d = (byte[])good.Clone();
                if (k % 3 == 0) d = d.AsSpan(0, rnd.Next(1, d.Length)).ToArray();
                else for (int m = 0, n = 1 + rnd.Next(8); m < n; m++) d[rnd.Next(d.Length)] ^= (byte)(1 << rnd.Next(8));
                try { Avif.DecodeAll(d, new AvifDecodeOptions { MaxPixels = 1 << 22 }); }
                catch (ImageFormatException) { }
                catch (ImageNotSupportedException) { }
                try { Avif.GetInfo(d); }
                catch (ImageFormatException) { }
                catch (ImageNotSupportedException) { }
            }
        }
    }

    [Fact]
    public void MaxPixelsRejectsLargeImages()
    {
        var data = File.ReadAllBytes(Fixture("avif/tiles_2x2.avif"));
        Assert.Throws<ImageNotSupportedException>(() => Avif.Decode(data, new AvifDecodeOptions { MaxPixels = 10000 }));
    }

    // ------------------------------------------------------------------ AV1 streams

    static IEnumerable<(int off, int size)> IvfFrames(byte[] d)
    {
        int p = d[6] | d[7] << 8;
        while (p + 12 <= d.Length)
        {
            int sz = BitConverter.ToInt32(d, p);
            p += 12;
            if (sz < 0 || p + sz > d.Length) throw new InvalidDataException("truncated IVF frame");
            yield return (p, sz);
            p += sz;
        }
    }

    [Theory, MemberData(nameof(Streams))]
    public void Av1StreamMatchesDav1d(string file)
    {
        var path = Fixture(file);
        var data = File.ReadAllBytes(path);
        var j = Json(path);
        foreach (bool mt in new[] { false, true })
        {
            var dec = new Av1Decoder(new Av1DecoderOptions { Multithreaded = mt });
            var ms = new MemoryStream();
            int frames = 0;
            foreach (var (off, size) in IvfFrames(data))
            {
                dec.Decode(data, off, size);
                foreach (var pic in dec.Output) { WritePlanes(ms, AvifReader.FromPicture(pic)); frames++; }
                dec.Output.Clear();
            }
            Assert.Equal(j.GetProperty("frames").GetInt32(), frames);
            Assert.Equal(j.GetProperty("bytes").GetInt64(), ms.Length);
            Assert.Equal(j.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant());
        }
    }

    /// <summary>The libaom test vectors (fixtures/av1/aom of the test-data repository): per-frame MD5 of the I420 output.</summary>
    [TestDataTheory("fixtures/av1/aom/av1-1-b8-01-size-16x16.ivf"), MemberData(nameof(AomVectors))]
    public void AomTestVector(string file)
    {
        var path = Fixture(file);
        var want = File.ReadAllLines(path + ".md5").Where(l => l.Length > 32).Select(l => l[..32]).ToArray();
        var data = File.ReadAllBytes(path);
        var dec = new Av1Decoder(new Av1DecoderOptions());
        var got = new List<string>();
        foreach (var (off, size) in IvfFrames(data))
        {
            dec.Decode(data, off, size);
            foreach (var pic in dec.Output)
            {
                var ms = new MemoryStream();
                WritePlanes(ms, AvifReader.FromPicture(pic), grayChroma: true);
                got.Add(Convert.ToHexString(MD5.HashData(ms.ToArray())).ToLowerInvariant());
            }
            dec.Output.Clear();
        }
        Assert.Equal(want, got);
    }

    public static IEnumerable<object[]> ArgonSets
    {
        get
        {
            foreach (var p in new[] { "profile0", "profile1", "profile2" })
                foreach (var s in new[] { "core", "core_special", "not_annexb", "not_annexb_special", "stress" })
                    yield return new object[] { $"{p}_{s}" };
        }
    }

    /// <summary>
    /// The Argon conformance streams (downloads/argon-av1 of the test-data repository, fetch_argon_av1.sh): every
    /// stream of a set against its MD5 (all layers, aomdec's raw output). Hours of work for all sets: runs only with
    /// PIXCODEC_ARGON=1 (or a comma separated list of set names).
    /// </summary>
    [ArgonTheory, MemberData(nameof(ArgonSets))]
    public void ArgonConformance(string set)
    {
        var sel = Environment.GetEnvironmentVariable("PIXCODEC_ARGON")!;
        if (sel != "1" && !sel.Split(',').Contains(set)) return;
        var dir = Path.Combine(ArgonRoot!, set);
        var streams = Directory.GetFiles(Path.Combine(dir, "streams"), "*.obu").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        int ok = 0;
        Parallel.ForEach(streams, new ParallelOptions { MaxDegreeOfParallelism = 4 }, f =>
        {
            string name = Path.GetFileNameWithoutExtension(f);
            var md5f = Path.Combine(dir, "md5_ref", name + ".md5");
            if (!File.Exists(md5f)) return;
            string want = File.ReadAllText(md5f).Split(' ')[0].Trim();
            bool annexb = File.ReadAllText(Path.Combine(dir, "ref_cmd", name + ".sh")).Contains("--annexb");
            try
            {
                var data = File.ReadAllBytes(f);
                var dec = new Av1Decoder(new Av1DecoderOptions { OutputAllLayers = true, Multithreaded = false });
                if (annexb) dec.DecodeAnnexB(data, 0, data.Length); else dec.Decode(data, 0, data.Length);
                using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                foreach (var pic in dec.Output)
                {
                    var ms = new MemoryStream();
                    WritePlanes(ms, AvifReader.FromPicture(pic));
                    md5.AppendData(ms.ToArray());
                }
                if (Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant() == want) Interlocked.Increment(ref ok);
                else failures.Add(name + " mismatch");
            }
            catch (Exception e) { failures.Add($"{name} {e.GetType().Name}: {e.Message}"); }
        });
        output.WriteLine($"{set}: {ok} of {streams.Length} match");
        Assert.True(failures.IsEmpty, string.Join("\n", failures.OrderBy(x => x)));
    }

    internal static string? ArgonRoot => TestDataDir == null ? null
        : Directory.GetDirectories(Path.Combine(TestDataDir, "downloads", "argon-av1"), "argon_coveragetool_av1*").FirstOrDefault();
}

/// <summary>Runs <see cref="AvifTests.ArgonConformance"/> only when PIXCODEC_ARGON is set and the streams are there.</summary>
public sealed class ArgonTheoryAttribute : TheoryAttribute
{
    public ArgonTheoryAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PIXCODEC_ARGON"))) Skip = "set PIXCODEC_ARGON=1 (or set names) to run the Argon conformance streams";
        else if (TestUtil.TestDataDir == null || !Directory.Exists(Path.Combine(TestUtil.TestDataDir, "downloads", "argon-av1")))
            Skip = "the Argon streams are missing (fetch_argon_av1.sh in the test-data repository)";
    }
}
