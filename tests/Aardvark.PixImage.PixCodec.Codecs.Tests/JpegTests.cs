using Aardvark.ImageCodecs;
using Aardvark.ImageCodecs.JpegImpl;
using Xunit;

namespace Aardvark.ImageCodecs.Tests;

public class JpegTests
{
    public static IEnumerable<object[]> Fixtures() => TestUtil.Files("jpeg", ".jpg");
    public static IEnumerable<object[]> MiscWithRef() => TestUtil.Files("jpeg/misc", ".jpg").Where(f => File.Exists(TestUtil.Fixture((string)f[0]) + ".ref"));
    public static IEnumerable<object[]> Unsupported() => TestUtil.Files("jpeg/unsupported", ".jpg");

    public static IEnumerable<object[]> IsoFixtures() => TestUtil.Files("jpeg/iso", ".jpg").Where(f => !((string)f[0]).Contains(".planes."));
    public static IEnumerable<object[]> IsoPlaneFixtures() => TestUtil.Files("jpeg/iso", ".jpg").Where(f => ((string)f[0]).Contains(".planes."));

    /// <summary>
    /// Hierarchical files with subsampled components, compared plane by plane at component resolution against the
    /// planes the reference software writes: exact for lossless coding, within the inverse DCT difference otherwise.
    /// (Comparing the final picture would mostly measure two different chroma interpolation filters.)
    /// </summary>
    [Theory]
    [MemberData(nameof(IsoPlaneFixtures))]
    public unsafe void DecodeReferenceSoftwarePlanes(string file)
    {
        var parts = Path.GetFileName(file).Split('.');
        int tolerance = int.Parse(parts.First(p => p.Length > 1 && p[0] == 't' && char.IsDigit(p[1]))[1..]);
        var data = Load(file);
        fixed (byte* p = data)
        {
            var d = new Aardvark.ImageCodecs.JpegImpl.JpegDecoder(p, data.Length);
            d.Parse(headerOnly: false);
            Assert.True(d.Hierarchical);
            for (int i = 0; i < d.Comps.Length; i++)
            {
                var expected = TestUtil.LoadRef(TestUtil.Fixture(file) + $".c{i}.ref");
                var c = d.Comps[i];
                Assert.Equal((expected.Width, expected.Height), (c.SampledW, c.SampledH));
                var actual = RawImage.Create(c.SampledW, c.SampledH, ColorModel.Gray, expected.SampleType);
                for (int y = 0; y < c.SampledH; y++)
                    for (int x = 0; x < c.SampledW; x++)
                    {
                        ushort v = c.Plane16Arr![y * c.Stride + x];
                        if (expected.SampleType == SampleType.UInt8) actual.GetData<byte>()[y * c.SampledW + x] = (byte)v;
                        else actual.GetData<ushort>()[y * c.SampledW + x] = v;
                    }
                TestUtil.AssertEqual(expected, actual, tolerance);
            }
        }
        // and the public decode gives a picture of the full size
        var img = Jpeg.Decode(data);
        Assert.Equal(Jpeg.GetInfo(data).Width, img.Width);
    }

    static byte[] Load(string file) => File.ReadAllBytes(TestUtil.Fixture(file));

    /// <summary>
    /// Files written by the ISO/IEC 10918 reference software (see gen_jpeg.py, round3): hierarchical mode, lossless with
    /// arithmetic coding, 12 bit arithmetic / progressive, subsampled lossless, DNL. The file name carries the tolerance:
    /// 0 wherever the reference is libjpeg-turbo or the coding is lossless, a few levels where only the reference
    /// software's own DCT decoder (different inverse DCT) is available.
    /// </summary>
    [Theory]
    [MemberData(nameof(IsoFixtures))]
    public void DecodeReferenceSoftwareFiles(string file)
    {
        var parts = Path.GetFileName(file).Split('.');
        int tolerance = int.Parse(parts.First(p => p.Length > 1 && p[0] == 't' && char.IsDigit(p[1]))[1..]);
        bool raw = parts.Contains("raw");
        var data = Load(file);
        var expected = TestUtil.LoadRef(TestUtil.Fixture(file) + ".ref");
        var actual = Jpeg.Decode(data, new JpegDecodeOptions { KeepYCbCr = raw });
        TestUtil.AssertEqual(expected, actual, tolerance);
        var info = Jpeg.GetInfo(data);
        Assert.Equal((actual.Width, actual.Height, actual.SampleType), (info.Width, info.Height, info.SampleType));

        if (parts[0].StartsWith("dnl") && !parts.Contains("plain"))
        {
            // the same encode without DNL decodes to the same pixels
            var twin = Jpeg.Decode(Load(file.Replace(parts[0] + ".", parts[0] + ".plain.")), new JpegDecodeOptions { KeepYCbCr = raw });
            TestUtil.AssertEqual(twin, actual, 0);
            Assert.True(data.AsSpan().IndexOf(new byte[] { 0xFF, 0xDC, 0x00, 0x04 }) > 0);
        }
        if (parts[0].StartsWith("hier"))
        {
            // scaling does not apply to hierarchical files; the option is ignored
            TestUtil.AssertEqual(actual, Jpeg.Decode(data, new JpegDecodeOptions { KeepYCbCr = raw, ScaleDenominator = 4 }), 0);
        }
    }

    // ------------------------------------------------------------------ decoding

    /// <summary>
    /// Every fixture decodes to exactly what Pillow (libjpeg-turbo) produces: the IDCT, the chroma upsampling
    /// filters and the color conversion reproduce libjpeg's arithmetic, so the tolerance is zero.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    [MemberData(nameof(MiscWithRef))]
    public void DecodeMatchesReference(string file)
    {
        var data = Load(file);
        // the references hold the stored pixels, Decode turns them upright
        var expected = ImageOps.ApplyOrientation(TestUtil.LoadRef(TestUtil.Fixture(file) + ".ref"), Jpeg.GetInfo(data).Orientation);
        var actual = Jpeg.Decode(data);
        TestUtil.AssertEqual(expected, actual, 0);

        Assert.True(Jpeg.IsMatch(data));
        var info = Jpeg.GetInfo(data);
        Assert.Equal((ImageFormat.Jpeg, actual.Width, actual.Height, actual.Channels, actual.SampleType, actual.Color),
            (info.Format, info.Width, info.Height, info.Channels, info.SampleType, info.Color));
        // the file-level properties are what Decode reports as metadata
        var meta = actual.Metadata ?? new ImageMetadata();
        Assert.Equal((meta.Orientation, meta.DpiX, meta.DpiY, meta.IccProfile != null), (Math.Min(info.Orientation, 1), info.DpiX, info.DpiY, info.HasIccProfile));
        Assert.InRange(info.BitsPerSample, actual.SampleType == SampleType.UInt8 ? 2 : 9, actual.SampleType == SampleType.UInt8 ? 8 : 16);
        Assert.False(info.IsAnimated);
    }

    /// <summary>The scalar and the generic vector code paths give the same pixels as the AVX2 one.</summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void DecodePathsAgree(string file)
    {
        var data = Load(file);
        var expected = ImageOps.ApplyOrientation(TestUtil.LoadRef(TestUtil.Fixture(file) + ".ref"), Jpeg.GetInfo(data).Orientation);
        lock (PathLock)
        {
            try
            {
                JpegDct.ForceLevel = 0; JpegPost.ForceScalar = true;
                TestUtil.AssertEqual(expected, Jpeg.Decode(data), 0);
                JpegDct.ForceLevel = 1; JpegPost.ForceScalar = false;
                TestUtil.AssertEqual(expected, Jpeg.Decode(data), 0);
            }
            finally
            {
                JpegDct.ForceLevel = -1; JpegPost.ForceScalar = false;
            }
        }
    }

    static readonly object PathLock = new();

    [Theory]
    [MemberData(nameof(Unsupported))]
    public void UnsupportedVariantsAreRejected(string file)
    {
        var data = Load(file);
        Assert.True(Jpeg.IsMatch(data));
        Assert.Throws<ImageNotSupportedException>(() => Jpeg.Decode(data));
    }

    /// <summary>
    /// Large, almost flat arithmetic coded images: the long runs of identical decisions take the probability estimator
    /// through its most skewed states, which ordinary pictures never reach. Compared against the oracle at run time.
    /// </summary>
    [Theory]
    [InlineData("jpeg/misc/arith_sparse.jpg")]
    [InlineData("jpeg/misc/arith_prog_sparse.jpg")]
    public void ArithmeticExtremeProbabilityStates(string file)
    {
        var data = Load(file);
        var img = Jpeg.Decode(data);
        Assert.Equal((640, 640, 1), (img.Width, img.Height, img.Channels));
        var oracle = TestUtil.OracleDecode(data, ".jpg");
        if (oracle != null) TestUtil.AssertEqual(oracle, img, 0);
    }

    [Fact]
    public void Metadata()
    {
        var img = Jpeg.Decode(Load("jpeg/meta_exif_icc.jpg"), Stored);
        var m = img.Metadata!;
        Assert.Equal(300, m.DpiX);
        Assert.Equal(150, m.DpiY);
        Assert.Equal(6, m.Orientation);
        // by default turned upright: resolution swapped, orientation 1 also in the EXIF blob
        var up = Jpeg.Decode(Load("jpeg/meta_exif_icc.jpg")).Metadata!;
        Assert.Equal((150.0, 300.0, 1, 1), (up.DpiX, up.DpiY, up.Orientation, ExifOrientationOf(up.Exif!)));
        Assert.NotNull(m.Exif);
        Assert.True(m.Exif!.AsSpan(0, 2).SequenceEqual("II"u8) || m.Exif.AsSpan(0, 2).SequenceEqual("MM"u8));
        Assert.Equal(File.ReadAllBytes(TestUtil.Fixture("jpeg/meta_exif_icc.icc")), m.IccProfile);

        m = Jpeg.Decode(Load("jpeg/meta_dpi.jpg")).Metadata!;
        Assert.Equal((72.0, 96.0), (m.DpiX, m.DpiY));
        Assert.Null(m.Exif);
        Assert.Null(m.IccProfile);
        Assert.Equal(0, m.Orientation);
    }

    [Fact]
    public void TruncatedScanDecodesWhatIsThere()
    {
        var full = Jpeg.Decode(Load("jpeg/misc/truncated_444_full.jpg"));
        var cut = Jpeg.Decode(Load("jpeg/misc/truncated_444.jpg"));
        Assert.Equal((full.Width, full.Height), (cut.Width, cut.Height));
        var f = full.GetData<byte>(); var c = cut.GetData<byte>();
        // two thirds of the data are present: the top half must be intact, the bottom rows are filled with gray
        int half = f.Length / 2;
        Assert.True(f.AsSpan(0, half).SequenceEqual(c.AsSpan(0, half)));
        Assert.All(c.Skip(f.Length - 8 * 64 * 3).ToArray(), v => Assert.Equal(128, v));
    }

    [Fact]
    public void LostRestartMarkerOnlyDamagesItsInterval()
    {
        var full = Jpeg.Decode(Load("jpeg/misc/rst_lost_full.jpg"));
        var cut = Jpeg.Decode(Load("jpeg/misc/rst_lost.jpg"));
        var f = full.GetData<byte>(); var c = cut.GetData<byte>();
        int differing = 0;
        for (int i = 0; i < f.Length; i += 3) if (f[i] != c[i] || f[i + 1] != c[i + 1] || f[i + 2] != c[i + 2]) differing++;
        // restart interval = 4 blocks: at most two intervals (the one that ran into the next and the skipped one) are affected
        Assert.InRange(differing, 1, 2 * 4 * 64);
    }

    [Fact]
    public void HeaderErrors()
    {
        Assert.False(Jpeg.IsMatch(new byte[] { 0xFF, 0xD8 }));
        Assert.False(Jpeg.IsMatch(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));
        Assert.Throws<ImageFormatException>(() => Jpeg.Decode(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }));
        Assert.Throws<ImageFormatException>(() => Jpeg.GetInfo(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }));
        Assert.Throws<ImageFormatException>(() => Jpeg.Decode(ReadOnlySpan<byte>.Empty));

        var data = Load("jpeg/rgb_420_17x33.jpg");
        // cut inside the headers: nothing to decode
        int sos = data.AsSpan().IndexOf(new byte[] { 0xFF, 0xDA });
        Assert.Throws<ImageFormatException>(() => Jpeg.Decode(data.AsSpan(0, sos)));
        Assert.Throws<ImageFormatException>(() => Jpeg.Decode(data.AsSpan(0, sos + 5)));
        Assert.Throws<ImageFormatException>(() => Jpeg.Decode(data.AsSpan(0, 30)));

        // a header that claims a gigantic image backed by almost no data must not allocate it
        var lie = (byte[])data.Clone();
        int sof = lie.AsSpan().IndexOf(new byte[] { 0xFF, 0xC0 });
        lie[sof + 5] = 0xFF; lie[sof + 6] = 0xFF; lie[sof + 7] = 0xFF; lie[sof + 8] = 0xFF;
        Assert.Equal((65535, 65535), (Jpeg.GetInfo(lie).Width, Jpeg.GetInfo(lie).Height));
        Assert.ThrowsAny<Exception>(() => Jpeg.Decode(lie));
        try { Jpeg.Decode(lie); } catch (Exception e) { Assert.True(e is ImageFormatException or ImageNotSupportedException, e.ToString()); }
    }

    // ------------------------------------------------------------------ decode options

    public static IEnumerable<object[]> OptionRefs(string suffix)
        => Directory.GetFiles(TestUtil.Fixture("jpeg/options"), "*" + suffix + ".ref").OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => new object[] { Path.GetFileName(f) });

    /// <summary>
    /// DCT domain downscaling gives exactly what libjpeg-turbo produces for scale 1/2, 1/4 and 1/8 (Pillow's draft mode):
    /// reduced inverse DCTs, larger DCTs instead of upsampling for subsampled chroma, same filters for what remains.
    /// </summary>
    [Theory]
    [MemberData(nameof(OptionRefs), ".s2")]
    [MemberData(nameof(OptionRefs), ".s4")]
    [MemberData(nameof(OptionRefs), ".s8")]
    public void DecodeScaled(string refName)
    {
        // <fixture>.s<N>.ref
        var parts = refName.Split('.');
        int scale = parts[1][1] - '0';
        var data = Load("jpeg/" + parts[0] + ".jpg");
        var expected = TestUtil.LoadRef(TestUtil.Fixture("jpeg/options/" + refName));
        var o = new JpegDecodeOptions { ScaleDenominator = scale };
        var actual = Jpeg.Decode(data, o);
        TestUtil.AssertEqual(expected, actual, 0);
        var info = Jpeg.GetInfo(data, o);
        Assert.Equal((actual.Width, actual.Height, actual.Channels), (info.Width, info.Height, info.Channels));
        var full = Jpeg.GetInfo(data);
        Assert.Equal(((full.Width + scale - 1) / scale, (full.Height + scale - 1) / scale), (actual.Width, actual.Height));
    }

    /// <summary>Scaling works for every fixture (12 bit and progressive included) and roughly agrees with shrinking the full decode.</summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void DecodeScaledIsConsistent(string file)
    {
        var data = Load(file);
        var full = Jpeg.Decode(data);
        foreach (int scale in new[] { 2, 4, 8 })
        {
            var small = Jpeg.Decode(data, new JpegDecodeOptions { ScaleDenominator = scale });
            Assert.Equal(full.SampleType, small.SampleType);
            Assert.Equal(full.Channels, small.Channels);
            if (Path.GetFileName(file).StartsWith("ll_"))
            {
                TestUtil.AssertEqual(full, small, 0); // lossless ignores the scale
                continue;
            }
            Assert.Equal(((full.Width + scale - 1) / scale, (full.Height + scale - 1) / scale), (small.Width, small.Height));
            if (full.Width % (8 * scale) != 0 || full.Height % (8 * scale) != 0 || full.Width < 32) continue;
            // box-shrink the full decode; chroma filtering differs a little, so only the mean error is checked
            double peak = full.SampleType == SampleType.UInt8 ? 255 : 4095, sum = 0;
            int ch = full.Channels;
            for (int y = 0; y < small.Height; y++)
                for (int x = 0; x < small.Width; x++)
                    for (int c = 0; c < ch; c++)
                    {
                        double acc = 0;
                        for (int j = 0; j < scale; j++)
                            for (int i = 0; i < scale; i++) acc += Sample(full, x * scale + i, y * scale + j, c);
                        sum += Math.Abs(acc / (scale * scale) - Sample(small, x, y, c));
                    }
            double mean = sum / (small.Width * small.Height * ch) / peak;
            Assert.True(mean < 0.06, $"{file} scale {scale}: mean abs error {mean:F4}");
        }
    }

    static double Sample(RawImage img, int x, int y, int c)
    {
        int i = (y * img.Width + x) * img.Channels + c;
        return img.SampleType == SampleType.UInt8 ? img.GetData<byte>()[i] : img.GetData<ushort>()[i];
    }

    [Theory]
    [MemberData(nameof(OptionRefs), ".ycc")]
    public void DecodeKeepYCbCr(string refName)
    {
        var data = Load("jpeg/" + refName.Split('.')[0] + ".jpg");
        var o = new JpegDecodeOptions { KeepYCbCr = true };
        var actual = Jpeg.Decode(data, o);
        Assert.Equal((ColorModel.Other, 3), (actual.Color, actual.Channels));
        TestUtil.AssertEqual(TestUtil.LoadRef(TestUtil.Fixture("jpeg/options/" + refName)), actual, 0);
        Assert.Equal((ColorModel.Other, 3), (Jpeg.GetInfo(data, o).Color, Jpeg.GetInfo(data, o).Channels));
    }

    [Theory]
    [MemberData(nameof(OptionRefs), ".cmyk")]
    public void DecodeRawCmyk(string refName)
    {
        var data = Load("jpeg/" + refName.Split('.')[0] + ".jpg");
        var o = new JpegDecodeOptions { RawCmyk = true };
        var actual = Jpeg.Decode(data, o);
        Assert.Equal((ColorModel.Other, 4), (actual.Color, actual.Channels));
        TestUtil.AssertEqual(TestUtil.LoadRef(TestUtil.Fixture("jpeg/options/" + refName)), actual, 0);
        Assert.Equal(4, Jpeg.GetInfo(data, o).Channels);
        Assert.Equal(3, Jpeg.GetInfo(data).Channels);

        // the default conversion is the multiplicative model on those samples
        var rgb = Jpeg.Decode(data).GetData<byte>();
        var raw = actual.GetData<byte>();
        for (int i = 0; i < rgb.Length / 3; i++)
            for (int c = 0; c < 3; c++) Assert.Equal((raw[i * 4 + c] * raw[i * 4 + 3] + 127) / 255, rgb[i * 3 + c]);

        // both options together: no conversion at all
        var both = Jpeg.Decode(data, new JpegDecodeOptions { RawCmyk = true, KeepYCbCr = true });
        Assert.Equal(4, both.Channels);
        if (refName.StartsWith("cmyk")) TestUtil.AssertEqual(actual, both, 0);
        else Assert.NotEqual(raw, both.GetData<byte>());
    }

    public static IEnumerable<object[]> OrientFiles() => TestUtil.Files("jpeg/options", ".jpg");

    [Theory]
    [MemberData(nameof(OrientFiles))]
    public void DecodeApplyOrientation(string file)
    {
        var data = Load(file);
        int orientation = file[file.IndexOf("orient") + 6] - '0';
        var plain = Jpeg.Decode(data, new JpegDecodeOptions { ApplyOrientation = false });
        Assert.Equal(orientation, plain.Metadata!.Orientation);
        Assert.Equal((300.0, 100.0), (plain.Metadata.DpiX, plain.Metadata.DpiY));

        var o = new JpegDecodeOptions();
        var upright = Jpeg.Decode(data);
        TestUtil.AssertEqual(TestUtil.LoadRef(TestUtil.Fixture(file) + ".ref"), upright, 0);
        TestUtil.AssertEqual(ImageOps.ApplyOrientation(plain), upright, 0);
        Assert.Equal(1, upright.Metadata!.Orientation);
        Assert.Equal(1, ExifOrientationOf(upright.Metadata.Exif!));
        bool swap = orientation >= 5;
        Assert.Equal(swap ? (plain.Height, plain.Width) : (plain.Width, plain.Height), (upright.Width, upright.Height));
        Assert.Equal(swap ? (100.0, 300.0) : (300.0, 100.0), (upright.Metadata.DpiX, upright.Metadata.DpiY));
        var info = Jpeg.GetInfo(data, o);
        Assert.Equal((upright.Width, upright.Height), (info.Width, info.Height));
        Assert.Equal((orientation, orientation > 1), (info.Orientation, info.OrientationApplied));
        Assert.Equal((upright.Metadata.DpiX, upright.Metadata.DpiY), (info.DpiX, info.DpiY));

        // re-encoding the upright image must not rotate it a second time when read back
        var again = Jpeg.Decode(ImageCodec.Encode(upright, ImageFormat.Jpeg));
        Assert.Equal((upright.Width, upright.Height, 1), (again.Width, again.Height, again.Metadata!.Orientation));
    }

    public static IEnumerable<object[]> StreamedOrientationCases()
    {
        for (int o = 1; o <= 8; o++)
            foreach (var color in new[] { ColorModel.Gray, ColorModel.Rgb })
                foreach (bool progressive in new[] { false, true })
                    foreach (int scale in new[] { 1, 4 })
                        yield return new object[] { o, color, progressive, scale };
    }

    // Streamed decodes write rows straight to their upright place: the same pixels as turning the stored image afterwards.
    [Theory]
    [MemberData(nameof(StreamedOrientationCases))]
    public void StreamedOrientationMatchesApplyOrientation(int orientation, ColorModel color, bool progressive, int scale)
    {
        var src = Synthetic(83, 45, color, seed: orientation);
        src.Metadata = new ImageMetadata { Orientation = orientation };
        var data = Encode(src, new JpegEncodeOptions { Quality = 90, Progressive = progressive, Subsampling = color == ColorModel.Rgb ? JpegSubsampling.Chroma420 : JpegSubsampling.Auto });
        var stored = Jpeg.Decode(data, new JpegDecodeOptions { ApplyOrientation = false, ScaleDenominator = scale });
        var upright = Jpeg.Decode(data, new JpegDecodeOptions { ScaleDenominator = scale });
        TestUtil.AssertEqual(ImageOps.ApplyOrientation(stored), upright, 0);
        Assert.Equal(1, upright.Metadata!.Orientation);
    }

    // Baseline and progressive decodes allocate about the output, rotated or not (no whole component planes, no second copy).
    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 6)]
    [InlineData(true, 8)]
    public void DecodeAllocatesAboutTheOutput(bool progressive, int orientation)
    {
        var src = Synthetic(1024, 768, ColorModel.Rgb, seed: 5);
        src.Metadata = new ImageMetadata { Orientation = orientation };
        var data = Encode(src, new JpegEncodeOptions { Quality = 90, Progressive = progressive, Subsampling = JpegSubsampling.Chroma444 });
        Jpeg.Decode(data);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var img = Jpeg.Decode(data);
        long used = GC.GetAllocatedBytesForCurrentThread() - before, output = img.Data.Length;
        // progressive files keep their coefficients (two bytes per sample) until the last scan
        Assert.True(used < output * (progressive ? 3.2 : 1.2), $"{used} bytes for {output} bytes of pixels");
    }

    static readonly JpegDecodeOptions Stored = new() { ApplyOrientation = false };

    /// <summary>The orientation tag of an EXIF blob, read back through a JPEG carrying it.</summary>
    static int ExifOrientationOf(byte[] exif)
    {
        var probe = new RawImage(1, 1, ColorModel.Gray, new byte[1], 1, new ImageMetadata { Exif = exif });
        return Jpeg.Decode(Encode(probe), Stored).Metadata!.Orientation;
    }

    [Fact]
    public void JunkBeforeSoiIsSkipped()
    {
        var data = Load("jpeg/rgb_420_17x33.jpg");
        var expected = Jpeg.Decode(data);
        var junk = new byte[300];
        new Random(1).NextBytes(junk);
        junk[10] = 0xFF; junk[11] = 0xD8; junk[12] = 0x00; // an SOI look-alike that is not followed by a marker
        var padded = junk.Concat(data).ToArray();
        Assert.False(Jpeg.IsMatch(padded));
        TestUtil.AssertEqual(expected, Jpeg.Decode(padded), 0);
        Assert.Equal(Jpeg.GetInfo(data), Jpeg.GetInfo(padded));
        var strict = new JpegDecodeOptions { RequireSoiAtStart = true };
        Assert.Throws<ImageFormatException>(() => Jpeg.Decode(padded, strict));
        Assert.Throws<ImageFormatException>(() => Jpeg.GetInfo(padded, strict));
        TestUtil.AssertEqual(expected, Jpeg.Decode(data, strict), 0);
        Assert.Throws<ImageFormatException>(() => Jpeg.Decode(junk));
        Assert.Throws<ArgumentException>(() => Jpeg.Decode(data, new JpegDecodeOptions { ScaleDenominator = 3 }));
    }

    // ------------------------------------------------------------------ JPEG in TIFF

    public static IEnumerable<object[]> TiffStreams()
        => Directory.GetFiles(TestUtil.Fixture("jpeg/tiff"), "*.data.bin").OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => new object[] { Path.GetFileName(f).Replace(".data.bin", "") });

    static RawImage Crop(RawImage img, int w, int h)
    {
        var o = RawImage.Create(w, h, img.Color, img.SampleType, img.Channels);
        int bytes = img.Channels * RawImage.BytesPerSample(img.SampleType);
        for (int y = 0; y < h; y++) img.GetBytes().Slice(y * img.Width * bytes, w * bytes).CopyTo(o.GetBytes().Slice(y * w * bytes));
        return o;
    }

    /// <summary>
    /// Tables-only + abbreviated stream pairs (8 and 12 bit DCT, arithmetic, lossless); the name says whether YCbCr is to
    /// be converted (_rgb) or samples returned as stored (_raw). Checked against Pillow / libjpeg-turbo 3.
    /// </summary>
    [Theory]
    [MemberData(nameof(TiffStreams))]
    public void DecodeForTiff(string name)
    {
        var dir = TestUtil.Fixture("jpeg/tiff");
        var tableBytes = File.ReadAllBytes(Path.Combine(dir, name + ".tables.bin"));
        var data = File.ReadAllBytes(Path.Combine(dir, name + ".data.bin"));
        var full = TestUtil.LoadRef(Path.Combine(dir, name + ".ref"));
        bool convert = name.EndsWith("_rgb"), wide = full.SampleType == SampleType.UInt16;
        int ch = full.Channels;
        var tables = JpegTiffTables.Parse(tableBytes);

        var streamInfo = Jpeg.GetTiffStreamInfo(data);
        Assert.Equal((full.Width, full.Height, ch), (streamInfo.Width, streamInfo.Height, streamInfo.Components));
        Assert.Equal(name.StartsWith("ll"), streamInfo.Lossless);
        Assert.Equal(wide, streamInfo.Precision > 8);

        // whole frame, and strips / tiles smaller than the (padded) frame
        foreach (var (w, h) in new[] { (full.Width, full.Height), (full.Width - 1, full.Height), (full.Width, full.Height - 3), (17, 9), (1, 1) })
        {
            var expected = Crop(full, w, h);
            var words = new ushort[w * h * ch];
            Jpeg.DecodeForTiff(tables, data, w, h, ch, convert, words);
            if (wide) Assert.True(expected.GetData<ushort>().AsSpan().SequenceEqual(words), $"{name} {w}x{h}");
            else
            {
                Assert.True(expected.GetData<byte>().Select(v => (ushort)v).SequenceEqual(words), $"{name} {w}x{h} widened");
                var dst = new byte[w * h * ch];
                Jpeg.DecodeForTiff(tables, data, w, h, ch, convert, dst);
                Assert.True(expected.GetData<byte>().AsSpan().SequenceEqual(dst), $"{name} {w}x{h}");
                Array.Clear(dst);
                Jpeg.DecodeForTiff(tableBytes, data, w, h, ch, convert, dst); // the overload that parses the tables itself
                Assert.True(expected.GetData<byte>().AsSpan().SequenceEqual(dst), $"{name} {w}x{h} raw tables");
            }
        }

        // the abbreviated stream is useless without its tables; wrong geometry, depth or component count is an error
        var buf = new ushort[full.Width * full.Height * ch];
        Assert.Throws<ImageFormatException>(() => Jpeg.DecodeForTiff((JpegTiffTables?)null, data, full.Width, full.Height, ch, convert, buf));
        Assert.Throws<ImageFormatException>(() => Jpeg.DecodeForTiff(tables, data, full.Width + 1, full.Height, ch, convert, new ushort[(full.Width + 1) * full.Height * ch]));
        Assert.Throws<ImageFormatException>(() => Jpeg.DecodeForTiff(tables, data, full.Width, full.Height, ch + 1, convert, new ushort[full.Width * full.Height * (ch + 1)]));
        Assert.Throws<ImageFormatException>(() => JpegTiffTables.Parse(tableBytes.AsSpan(0, tableBytes.Length - 5)));
        if (wide) Assert.Throws<ImageFormatException>(() => Jpeg.DecodeForTiff(tables, data, full.Width, full.Height, ch, convert, new byte[buf.Length]));

        // damaged strips never escape with anything but the codec's exceptions; one tables object serves many threads
        Parallel.For(0, 200, round =>
        {
            var rnd = new Random(name.Length * 31 + data.Length + round);
            var bad = (byte[])data.Clone();
            if (round % 2 == 0) bad = bad.AsSpan(0, rnd.Next(bad.Length)).ToArray();
            else for (int i = 0; i < 3; i++) bad[rnd.Next(bad.Length)] = (byte)rnd.Next(256);
            try { Jpeg.DecodeForTiff(tables, bad, full.Width, full.Height, ch, convert, new ushort[buf.Length]); }
            catch (ImageFormatException) { }
            catch (ImageNotSupportedException) { }
        });
    }

    public static IEnumerable<object[]> OldStyleFiles()
        => Directory.GetFiles(TestUtil.Fixture("jpeg/tiff"), "*.old.bin").OrderBy(f => f, StringComparer.Ordinal).Select(f => new object[] { Path.GetFileName(f) });

    /// <summary>Old-style TIFF JPEG (compression 6): tables from the TIFF tags, strips of bare entropy coded data.</summary>
    [Theory]
    [MemberData(nameof(OldStyleFiles))]
    public void DecodeForTiffOldStyle(string file)
    {
        var path = Path.Combine(TestUtil.Fixture("jpeg/tiff"), file);
        var b = File.ReadAllBytes(path);
        var expected = TestUtil.LoadRef(path.Replace(".old.bin", ".old.ref"));
        int nc = b[0], o = 1;
        var q = new byte[nc][]; var dc = new byte[nc][]; var ac = new byte[nc][];
        for (int i = 0; i < nc; i++)
        {
            q[i] = b.AsSpan(o, 64).ToArray(); o += 64;
            int n = b[o] | b[o + 1] << 8;
            dc[i] = b.AsSpan(o + 2, n).ToArray().Concat(new byte[] { 1, 2, 3 }).ToArray(); // trailing bytes are tolerated
            o += 2 + n;
            n = b[o] | b[o + 1] << 8;
            ac[i] = b.AsSpan(o + 2, n).ToArray(); o += 2 + n;
        }
        int fw = b[o] | b[o + 1] << 8, fh = b[o + 2] | b[o + 3] << 8, hSub = b[o + 4], vSub = b[o + 5], ri = b[o + 6] | b[o + 7] << 8;
        var strip = b.AsSpan(o + 8).ToArray();
        Assert.Equal((expected.Width, expected.Height, expected.Channels), (fw, fh, nc));

        var tables = JpegTiffTables.FromOldStyle(q, dc, ac);
        foreach (var (w, h) in new[] { (fw, fh), (fw - 2, fh - 1), (9, 5) })
        {
            var dst = new byte[w * h * nc];
            Jpeg.DecodeForTiffRaw(tables, strip, fw, fh, nc, hSub, vSub, ri, w, h, nc == 3, dst);
            Assert.True(Crop(expected, w, h).GetData<byte>().AsSpan().SequenceEqual(dst), $"{file} {w}x{h}");
        }
        if (nc == 3)
        {
            // chroma tables given only once: the later components fall back to the last table present
            var shared = JpegTiffTables.FromOldStyle(new[] { q[0], q[1] }, new[] { dc[0], dc[1], null }, new[] { ac[0], ac[1] });
            var dst = new byte[fw * fh * 3];
            Jpeg.DecodeForTiffRaw(shared, strip, fw, fh, 3, hSub, vSub, ri, fw, fh, true, dst);
            Assert.Equal(expected.GetData<byte>(), dst);
        }

        // a strip that is a complete stream after all goes through the same call
        var whole = Load("jpeg/photo_420.jpg");
        var photo = TestUtil.LoadRef(TestUtil.Fixture("jpeg/photo_420.jpg.ref"));
        var p = new byte[photo.Width * photo.Height * 3];
        Jpeg.DecodeForTiffRaw(tables, whole, photo.Width, photo.Height, 3, 2, 2, 0, photo.Width, photo.Height, true, p);
        Assert.Equal(photo.GetData<byte>(), p);

        Assert.Throws<ImageFormatException>(() => Jpeg.DecodeForTiffRaw(JpegTiffTables.FromOldStyle(null, null, null), strip, fw, fh, nc, hSub, vSub, ri, fw, fh, false, new byte[fw * fh * nc]));
        Assert.Throws<ImageFormatException>(() => JpegTiffTables.FromOldStyle(new[] { new byte[10] }, null, null));
        Assert.Throws<ImageFormatException>(() => JpegTiffTables.FromOldStyle(null, new[] { new byte[] { 200, 200, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 } }, null));
        var rnd = new Random(file.Length);
        for (int round = 0; round < 200; round++)
        {
            var bad = (byte[])strip.Clone();
            if (round % 2 == 0) bad = bad.AsSpan(0, rnd.Next(bad.Length)).ToArray();
            else for (int i = 0; i < 3; i++) bad[rnd.Next(bad.Length)] = (byte)rnd.Next(256);
            try { Jpeg.DecodeForTiffRaw(tables, bad, fw, fh, nc, hSub, vSub, ri, fw, fh, nc == 3, new byte[fw * fh * nc]); }
            catch (ImageFormatException) { }
            catch (ImageNotSupportedException) { }
        }
    }

    /// <summary>Complete streams with no JPEGTables, markers ignored for the color decision, and concurrent use.</summary>
    [Fact]
    public void DecodeForTiffCompleteStreamsAndThreads()
    {
        // a JFIF YCbCr file: converted on request, raw YCbCr otherwise (never guessed from the JFIF marker)
        var ycc = Load("jpeg/photo_420.jpg");
        var rgb = TestUtil.LoadRef(TestUtil.Fixture("jpeg/photo_420.jpg.ref"));
        var a = new byte[rgb.Width * rgb.Height * 3]; var b = new byte[a.Length];
        Jpeg.DecodeForTiff(ReadOnlySpan<byte>.Empty, ycc, rgb.Width, rgb.Height, 3, true, a);
        Assert.Equal(rgb.GetData<byte>(), a);
        Jpeg.DecodeForTiff(ReadOnlySpan<byte>.Empty, ycc, rgb.Width, rgb.Height, 3, false, b);
        Assert.NotEqual(a, b);

        // an Adobe RGB file decodes the same as stored, and "converting" it applies the YCbCr matrix regardless of the marker
        var stored = TestUtil.LoadRef(TestUtil.Fixture("jpeg/rgb_adobe.jpg.ref"));
        var c = new byte[stored.Width * stored.Height * 3];
        Jpeg.DecodeForTiff(ReadOnlySpan<byte>.Empty, Load("jpeg/rgb_adobe.jpg"), stored.Width, stored.Height, 3, false, c);
        Assert.Equal(stored.GetData<byte>(), c);
        Jpeg.DecodeForTiff(ReadOnlySpan<byte>.Empty, Load("jpeg/rgb_adobe.jpg"), stored.Width, stored.Height, 3, true, c);
        Assert.NotEqual(stored.GetData<byte>(), c);

        var dir = TestUtil.Fixture("jpeg/tiff");
        var tables = File.ReadAllBytes(Path.Combine(dir, "ycc420_rgb.tables.bin"));
        var data = File.ReadAllBytes(Path.Combine(dir, "ycc420_rgb.data.bin"));
        var expected = TestUtil.LoadRef(Path.Combine(dir, "ycc420_rgb.ref"));
        Parallel.For(0, 64, i =>
        {
            var dst = new byte[expected.Width * expected.Height * 3];
            Jpeg.DecodeForTiff(tables, data, expected.Width, expected.Height, 3, true, dst);
            Assert.Equal(expected.GetData<byte>(), dst);
        });
    }

    /// <summary>Strips for TiffCompression.Jpeg: bare self-contained streams, YCbCr or untransformed, 8 and 12 bit.</summary>
    [Theory]
    [InlineData(1, true, JpegSubsampling.Auto, 85, true, 0)]
    [InlineData(3, true, JpegSubsampling.Auto, 85, true, 0)]
    [InlineData(3, true, JpegSubsampling.Auto, 95, false, 4)]
    [InlineData(3, true, JpegSubsampling.Chroma422, 80, false, 0)]
    [InlineData(3, true, JpegSubsampling.Chroma444, 80, true, 2)]
    [InlineData(3, false, JpegSubsampling.Chroma420, 90, true, 0)]
    [InlineData(3, false, JpegSubsampling.Auto, 60, false, 3)]
    public void EncodeForTiff(int components, bool ycc, JpegSubsampling ss, int quality, bool optimize, int restart)
    {
        var opt = new JpegEncodeOptions { Quality = quality, Subsampling = ss, OptimizeHuffman = optimize, RestartInterval = restart, Progressive = true, Lossless = true };
        var expectedSub = ss == JpegSubsampling.Chroma444 || (ss == JpegSubsampling.Auto && quality >= 90) ? (1, 1) : ss == JpegSubsampling.Chroma422 ? (2, 1) : (2, 2);
        Assert.Equal(expectedSub, Jpeg.ResolveSubsampling(opt));
        foreach (var (w, h) in new[] { (67, 45), (16, 16), (1, 1), (130, 9) })
        {
            var src = Synthetic(w, h, components == 1 ? ColorModel.Gray : ColorModel.Rgb, seed: w + quality);
            var px = src.GetData<byte>();
            using var ms = new MemoryStream();
            Jpeg.EncodeForTiff(px, w, h, components, opt, ycc, ms);
            var bytes = ms.ToArray();

            // baseline, no application segments, the declared sampling
            var info = Jpeg.GetTiffStreamInfo(bytes);
            Assert.Equal((w, h, components, 8, false), info);
            Assert.True(bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0xFF, 0xD8, 0xFF, 0xDB }));
            int sof = bytes.AsSpan().IndexOf(new byte[] { 0xFF, 0xC0 });
            Assert.True(sof > 0);
            if (components == 3)
            {
                var (sh, sv) = ycc ? expectedSub : (1, 1);
                Assert.Equal(new byte[] { 1, (byte)(sh << 4 | sv), 2, 0x11, 3, 0x11 }, new[] { bytes[sof + 10], bytes[sof + 11], bytes[sof + 13], bytes[sof + 14], bytes[sof + 16], bytes[sof + 17] });
            }

            var back = new byte[px.Length];
            Jpeg.DecodeForTiff((JpegTiffTables?)null, bytes, w, h, components, ycc && components == 3, back);
            var decoded = new RawImage(w, h, src.Color, back);
            if (w >= 16 && h >= 16) Assert.True(TestUtil.Psnr(src, decoded, 255) > (ycc && expectedSub != (1, 1) ? 23 : 27), $"{w}x{h}: {TestUtil.Psnr(src, decoded, 255):F1} dB");

            // an independent decoder sees the same samples; without markers it takes 3 components for YCbCr
            var oracle = TestUtil.OracleDecode(bytes, ".jpg");
            if (oracle == null) continue;
            var asYcc = new byte[px.Length];
            Jpeg.DecodeForTiff((JpegTiffTables?)null, bytes, w, h, components, components == 3, asYcc);
            Assert.Equal(oracle.GetData<byte>(), asYcc);
        }

        // 12 bit strips
        var wide = Synthetic16(40, 24, components == 1 ? ColorModel.Gray : ColorModel.Rgb, 12, seed: quality);
        using var ms12 = new MemoryStream();
        Jpeg.EncodeForTiff(wide.GetData<ushort>(), 40, 24, components, opt, ycc, ms12);
        Assert.Equal((40, 24, components, 12, false), Jpeg.GetTiffStreamInfo(ms12.ToArray()));
        var back12 = new ushort[40 * 24 * components];
        Jpeg.DecodeForTiff(null, ms12.ToArray(), 40, 24, components, ycc && components == 3, back12);
        Assert.True(TestUtil.Psnr(wide, new RawImage(40, 24, wide.Color, back12), 4095) > 23);

        Assert.Throws<ArgumentException>(() => Jpeg.EncodeForTiff(new byte[10], 2, 2, 2, opt, false, new MemoryStream()));
        Assert.Throws<ArgumentException>(() => Jpeg.EncodeForTiff(new byte[10], 2, 2, 3, opt, false, new MemoryStream()));

        // concurrent strips
        var strip = Synthetic(64, 16, components == 1 ? ColorModel.Gray : ColorModel.Rgb).GetData<byte>();
        using var first = new MemoryStream();
        Jpeg.EncodeForTiff(strip, 64, 16, components, opt, ycc, first);
        Parallel.For(0, 32, _ =>
        {
            using var m = new MemoryStream();
            Jpeg.EncodeForTiff(strip, 64, 16, components, opt, ycc, m);
            Assert.Equal(first.ToArray(), m.ToArray());
        });
    }

    // ------------------------------------------------------------------ robustness

    static void DecodeDamaged(ReadOnlySpan<byte> data, string what)
    {
        try
        {
            var img = Jpeg.Decode(data);
            Assert.True(img.Width > 0 && img.Height > 0);
        }
        catch (ImageFormatException) { }
        catch (ImageNotSupportedException) { }
        catch (Exception e)
        {
            Assert.Fail($"{what}: unexpected {e.GetType().Name}: {e}");
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    [MemberData(nameof(IsoFixtures))]
    public void TruncationNeverCrashes(string file)
    {
        var data = Load(file);
        if (data.Length > 20000) return; // the big ICC fixture: covered by the smaller ones
        int step = Math.Max(1, data.Length / 150);
        for (int n = 0; n < data.Length; n += step) DecodeDamaged(data.AsSpan(0, n), $"{file} truncated at {n}");
        for (int n = Math.Max(0, data.Length - 12); n < data.Length; n++) DecodeDamaged(data.AsSpan(0, n), $"{file} truncated at {n}");
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    [MemberData(nameof(IsoFixtures))]
    public void CorruptionNeverCrashes(string file)
    {
        var data = Load(file);
        if (data.Length > 20000) return;
        var rnd = new Random(file.GetHashCode(StringComparison.Ordinal) ^ 12345);
        rnd = new Random(data.Length * 7919 + data[data.Length / 2]);
        for (int round = 0; round < 120; round++)
        {
            var d = (byte[])data.Clone();
            int flips = 1 + rnd.Next(4);
            for (int i = 0; i < flips; i++)
            {
                // bias half of the damage into the first bytes, where the headers live
                int pos = rnd.Next(2) == 0 ? rnd.Next(Math.Min(d.Length, 700)) : rnd.Next(d.Length);
                d[pos] = rnd.Next(3) == 0 ? (byte)0xFF : (byte)rnd.Next(256);
            }
            DecodeDamaged(d, $"{file} corruption round {round}");
        }
    }

    // ------------------------------------------------------------------ encoding

    /// <summary>Smooth blobs with a little texture and a hard edge: representative for a photo codec.</summary>
    static RawImage Synthetic(int w, int h, ColorModel color, int seed = 1)
    {
        var img = RawImage.Create(w, h, color, SampleType.UInt8);
        var d = img.GetData<byte>();
        int ch = img.Channels;
        var rnd = new Random(seed);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < ch; c++)
                {
                    double v = 0.5 + 0.35 * Math.Sin(x * (0.11 + 0.05 * c) + seed) * Math.Cos(y * (0.07 + 0.03 * c)) + 0.1 * Math.Sin((x + y) * 0.31 + c);
                    if (x > w * 2 / 3 && y < h / 3) v = c == 0 ? 0.9 : 0.15;
                    v += (rnd.NextDouble() - 0.5) * 0.04;
                    bool alpha = (color == ColorModel.Rgba && c == 3) || (color == ColorModel.GrayAlpha && c == 1);
                    d[(y * w + x) * ch + c] = alpha ? (byte)rnd.Next(256) : (byte)Math.Clamp(v * 255, 0, 255);
                }
        return img;
    }

    /// <summary>Drops the alpha channel: what the encoder is expected to store.</summary>
    static RawImage Opaque(RawImage img)
    {
        if (!img.HasAlpha) return img;
        int ch = img.Channels - 1, n = img.Width * img.Height;
        var o = RawImage.Create(img.Width, img.Height, ch == 1 ? ColorModel.Gray : ColorModel.Rgb, img.SampleType);
        for (int i = 0; i < n; i++)
            for (int c = 0; c < ch; c++)
            {
                if (img.SampleType == SampleType.UInt8) o.GetData<byte>()[i * ch + c] = img.GetData<byte>()[i * img.Channels + c];
                else o.GetData<ushort>()[i * ch + c] = img.GetData<ushort>()[i * img.Channels + c];
            }
        return o;
    }

    static byte[] Encode(RawImage img, JpegEncodeOptions? o = null)
    {
        using var ms = new MemoryStream();
        Jpeg.Encode(img, ms, o);
        return ms.ToArray();
    }

    public static IEnumerable<object[]> EncodeCases()
    {
        var sizes = new[] { (1, 1), (7, 5), (8, 8), (17, 33), (33, 17), (64, 48), (130, 97) };
        foreach (var color in new[] { ColorModel.Gray, ColorModel.Rgb })
            foreach (var (w, h) in sizes)
                foreach (var ss in color == ColorModel.Gray ? new[] { JpegSubsampling.Auto } : new[] { JpegSubsampling.Chroma444, JpegSubsampling.Chroma422, JpegSubsampling.Chroma420 })
                    foreach (bool progressive in new[] { false, true })
                        yield return new object[] { color, w, h, 85, ss, true, progressive, 0 };
        foreach (var color in new[] { ColorModel.Gray, ColorModel.GrayAlpha, ColorModel.Rgb, ColorModel.Rgba })
        {
            foreach (int q in new[] { 1, 25, 50, 75, 95, 100 })
            {
                yield return new object[] { color, 67, 45, q, JpegSubsampling.Auto, true, false, 0 };
                yield return new object[] { color, 67, 45, q, JpegSubsampling.Chroma420, false, false, 0 };
                yield return new object[] { color, 67, 45, q, JpegSubsampling.Chroma422, true, true, 0 };
            }
            foreach (int restart in new[] { 1, 3, 100 })
            {
                yield return new object[] { color, 70, 50, 80, JpegSubsampling.Chroma420, true, false, restart };
                yield return new object[] { color, 70, 50, 80, JpegSubsampling.Chroma444, false, false, restart };
                yield return new object[] { color, 70, 50, 80, JpegSubsampling.Chroma422, true, true, restart };
            }
        }
    }

    [Theory]
    [MemberData(nameof(EncodeCases))]
    public void EncodeRoundTrips(ColorModel color, int w, int h, int quality, JpegSubsampling ss, bool optimize, bool progressive, int restart)
    {
        var src = Synthetic(w, h, color, seed: w + h + quality);
        var opt = new JpegEncodeOptions { Quality = quality, Subsampling = ss, OptimizeHuffman = optimize, Progressive = progressive, RestartInterval = restart };
        var bytes = Encode(src, opt);
        var expected = Opaque(src);

        var back = Jpeg.Decode(bytes);
        Assert.Equal(expected.Color, back.Color);
        double psnr = TestUtil.Psnr(expected, back, 255);
        // chroma subsampling of tiny / hard edged content costs fidelity; the floors are far below what a working codec gives
        double floor = quality >= 95 ? 33 : quality >= 75 ? 27 : quality >= 50 ? 25 : quality >= 25 ? 22 : 12;
        bool subsampled = color is ColorModel.Rgb or ColorModel.Rgba && (ss is JpegSubsampling.Chroma420 or JpegSubsampling.Chroma422 || (ss == JpegSubsampling.Auto && quality < 90));
        if (subsampled) floor = Math.Min(floor, 23); // the saturated hard edge in the test image dominates the error
        if (w >= 16 && h >= 16) Assert.True(psnr >= floor, $"PSNR {psnr:F1} dB below {floor}");

        var info = Jpeg.GetInfo(bytes);
        Assert.Equal((w, h, expected.Channels), (info.Width, info.Height, info.Channels));

        // an independent decoder must read the same pixels out of our stream (our decoder matches libjpeg exactly)
        var oracle = TestUtil.OracleDecode(bytes, ".jpg");
        if (oracle == null) return;
        TestUtil.AssertEqual(oracle, back, 0);
    }

    /// <summary>More than 32767 blocks with empty AC bands: end-of-band runs and the correction bit buffer have to be split.</summary>
    [Theory]
    [InlineData(ColorModel.Gray, 0, false)]
    [InlineData(ColorModel.Rgb, 0, false)]
    [InlineData(ColorModel.Gray, 5, false)]
    [InlineData(ColorModel.Gray, 0, true)]
    [InlineData(ColorModel.Rgb, 0, true)]
    public void EncodeProgressiveLongRuns(ColorModel color, int restart, bool flat)
    {
        int w = 1808, h = 1210; // 34352 luma blocks
        var src = RawImage.Create(w, h, color, SampleType.UInt8);
        var d = src.GetData<byte>();
        int ch = src.Channels;
        var rnd = new Random(9);
        if (flat)
        {
            // every AC band is empty up to the very last block
            Array.Fill(d, (byte)90);
            d[^1] = 255;
        }
        else
        {
            // faint texture everywhere: after the first AC passes most blocks only refine existing coefficients,
            // which piles up correction bits inside long end-of-band runs
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < ch; c++)
                        d[(y * w + x) * ch + c] = (byte)(100 + c * 20 + (int)(14 * Math.Sin(x * 0.8) * Math.Sin(y * 0.8)));
            for (int i = 0; i < 300; i++) d[rnd.Next(d.Length)] = (byte)rnd.Next(256);
        }

        var bytes = Encode(src, new JpegEncodeOptions { Quality = 92, Progressive = true, RestartInterval = restart, Subsampling = JpegSubsampling.Chroma420 });
        var back = Jpeg.Decode(bytes);
        Assert.True(TestUtil.Psnr(src, back, 255) > 30);
        var oracle = TestUtil.OracleDecode(bytes, ".jpg");
        if (oracle != null) TestUtil.AssertEqual(oracle, back, 0);

        // the same coefficients written sequentially decode to the same pixels
        var baseline = Jpeg.Decode(Encode(src, new JpegEncodeOptions { Quality = 92, RestartInterval = restart, Subsampling = JpegSubsampling.Chroma420 }));
        TestUtil.AssertEqual(baseline, back, 0);
    }

    /// <summary>
    /// Blocks whose 63 AC coefficients are all significant from the first pass on: the refinement scans then consist of
    /// end-of-band runs carrying 63 correction bits per block, which overflows the encoder's correction bit buffer
    /// and forces it to cut the run.
    /// </summary>
    [Fact]
    public void EncodeProgressiveDenseRefinement()
    {
        int w = 320, h = 64;
        var src = RawImage.Create(w, h, ColorModel.Gray, SampleType.UInt8);
        var d = src.GetData<byte>();
        var rnd = new Random(4);
        for (int by = 0; by < h / 8; by++)
            for (int bx = 0; bx < w / 8; bx++)
            {
                // synthesize the block from AC coefficients of magnitude 24 with random signs
                var coef = new double[64];
                for (int i = 1; i < 64; i++) coef[i] = rnd.Next(2) == 0 ? 24 : -24;
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        double v = 0;
                        for (int vv = 0; vv < 8; vv++)
                            for (int u = 0; u < 8; u++)
                                v += (u == 0 ? Math.Sqrt(0.5) : 1) * (vv == 0 ? Math.Sqrt(0.5) : 1) / 4 * coef[vv * 8 + u]
                                     * Math.Cos((2 * x + 1) * u * Math.PI / 16) * Math.Cos((2 * y + 1) * vv * Math.PI / 16);
                        d[(by * 8 + y) * w + bx * 8 + x] = (byte)Math.Clamp(Math.Round(v + 128), 0, 255);
                    }
            }
        var bytes = Encode(src, new JpegEncodeOptions { Quality = 100, Progressive = true });
        var back = Jpeg.Decode(bytes);
        Assert.True(TestUtil.Psnr(src, back, 255) > 45);
        TestUtil.AssertEqual(Jpeg.Decode(Encode(src, new JpegEncodeOptions { Quality = 100 })), back, 0);
        var oracle = TestUtil.OracleDecode(bytes, ".jpg");
        if (oracle != null) TestUtil.AssertEqual(oracle, back, 0);
    }

    static RawImage Synthetic16(int w, int h, ColorModel color, int bits, int seed = 1, int channels = 0)
    {
        var img = RawImage.Create(w, h, color, bits > 8 ? SampleType.UInt16 : SampleType.UInt8, channels);
        int ch = img.Channels, max = (1 << bits) - 1;
        var rnd = new Random(seed * 77 + bits);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < ch; c++)
                {
                    double v = 0.5 + 0.35 * Math.Sin(x * (0.11 + 0.05 * c) + seed) * Math.Cos(y * (0.07 + 0.03 * c)) + 0.1 * Math.Sin((x + y) * 0.31 + c);
                    if (x > w * 2 / 3 && y < h / 3) v = c == 0 ? 1.2 : -0.2; // saturates to the range limits
                    v += (rnd.NextDouble() - 0.5) * 0.03;
                    int s = (int)Math.Clamp(v * max, 0, max);
                    int i = (y * w + x) * ch + c;
                    if (bits > 8) img.GetData<ushort>()[i] = (ushort)s; else img.GetData<byte>()[i] = (byte)s;
                }
        return img;
    }

    public static IEnumerable<object[]> Encode12Cases()
    {
        foreach (var color in new[] { ColorModel.Gray, ColorModel.Rgb })
            foreach (var ss in color == ColorModel.Gray ? new[] { JpegSubsampling.Auto } : new[] { JpegSubsampling.Chroma444, JpegSubsampling.Chroma422, JpegSubsampling.Chroma420 })
                foreach (bool progressive in new[] { false, true })
                {
                    foreach (var (w, h) in new[] { (1, 1), (7, 5), (17, 33), (64, 48), (67, 45) })
                        yield return new object[] { color, w, h, 90, ss, progressive, 0 };
                    yield return new object[] { color, 70, 50, 100, ss, progressive, 3 };
                    yield return new object[] { color, 70, 50, 30, ss, progressive, 0 };
                }
    }

    /// <summary>12 bit DCT JPEG from UInt16 images: our decoder and libjpeg-turbo 3 read the same pixels back.</summary>
    [Theory]
    [MemberData(nameof(Encode12Cases))]
    public void Encode12BitRoundTrips(ColorModel color, int w, int h, int quality, JpegSubsampling ss, bool progressive, int restart)
    {
        var src = Synthetic16(w, h, color, 12, seed: w + quality);
        var bytes = Encode(src, new JpegEncodeOptions { Quality = quality, Subsampling = ss, Progressive = progressive, RestartInterval = restart });
        var back = Jpeg.Decode(bytes);
        Assert.Equal((SampleType.UInt16, color), (back.SampleType, back.Color));
        Assert.Equal(SampleType.UInt16, Jpeg.GetInfo(bytes).SampleType);
        Assert.All(back.GetData<ushort>(), v => Assert.InRange(v, 0, 4095));
        double psnr = TestUtil.Psnr(src, back, 4095);
        double floor = ss is JpegSubsampling.Chroma420 or JpegSubsampling.Chroma422 ? 23 : quality >= 90 ? 40 : 28;
        if (w >= 16 && h >= 16) Assert.True(psnr >= floor, $"PSNR {psnr:F1} dB below {floor}");
        var oracle = TestUtil.OracleDecode(bytes, ".jpg");
        if (oracle != null) TestUtil.AssertEqual(oracle, back, 0);
    }

    [Fact]
    public void Encode12BitChecksRange()
    {
        var img = RawImage.Create(8, 8, ColorModel.Gray, SampleType.UInt16);
        img.GetData<ushort>()[5] = 4096;
        Assert.Throws<ArgumentException>(() => Encode(img));
        Assert.Throws<ArgumentException>(() => Encode(img, new JpegEncodeOptions { Precision = 16 }));
        Assert.Throws<ArgumentException>(() => Encode(RawImage.Create(8, 8, ColorModel.Gray, SampleType.UInt8), new JpegEncodeOptions { Precision = 12 }));
        img.GetData<ushort>()[5] = 4095;
        Assert.Equal(4095, Jpeg.Decode(Encode(img, new JpegEncodeOptions { Quality = 100, Precision = 12 })).GetData<ushort>().Max(), 40.0);
    }

    public static IEnumerable<object[]> LosslessCases()
    {
        foreach (int predictor in new[] { 0, 1, 2, 3, 4, 5, 6, 7 })
        {
            yield return new object[] { ColorModel.Gray, 0, 8, 33, 21, predictor, 0 };
            yield return new object[] { ColorModel.Rgb, 0, 8, 23, 31, predictor, 0 };
            yield return new object[] { ColorModel.Gray, 0, 16, 33, 21, predictor, 0 };
            yield return new object[] { ColorModel.Rgb, 0, 16, 23, 31, predictor, 0 };
        }
        foreach (int bits in new[] { 2, 3, 5, 7, 9, 10, 12, 13, 15 })
        {
            yield return new object[] { ColorModel.Gray, 0, bits, 20, 17, 4, 0 };
            yield return new object[] { ColorModel.Rgb, 0, bits, 20, 17, 7, 0 };
        }
        foreach (int bits in new[] { 8, 16 })
        {
            foreach (var (w, h) in new[] { (1, 1), (1, 9), (9, 1), (2, 2), (130, 97) })
                yield return new object[] { ColorModel.Rgb, 0, bits, w, h, 4, 0 };
            yield return new object[] { ColorModel.GrayAlpha, 0, bits, 20, 17, 1, 0 };
            yield return new object[] { ColorModel.Rgba, 0, bits, 20, 17, 6, 0 };
            yield return new object[] { ColorModel.Other, 2, bits, 20, 17, 4, 0 };
            yield return new object[] { ColorModel.Other, 4, bits, 20, 17, 5, 0 };
            foreach (int restart in new[] { 1, 40, 45, 200 })
            {
                yield return new object[] { ColorModel.Gray, 0, bits, 40, 23, 4, restart };
                yield return new object[] { ColorModel.Rgb, 0, bits, 40, 23, 7, restart };
            }
        }
    }

    /// <summary>Lossless JPEG is exact through our decoder and through libjpeg-turbo 3, for every predictor and precision.</summary>
    [Theory]
    [MemberData(nameof(LosslessCases))]
    public void EncodeLosslessRoundTrips(ColorModel color, int channels, int bits, int w, int h, int predictor, int restart)
    {
        var src = Synthetic16(w, h, color, bits, seed: w + predictor, channels);
        if (bits == 16)
        {
            // make sure the extreme differences (+-32768, 65535) occur
            var d = src.GetData<ushort>();
            for (int i = 0; i + 4 < d.Length; i += 37) { d[i] = 0; d[i + 1] = 65535; d[i + 2] = 32768; d[i + 3] = 0; }
        }
        var bytes = Encode(src, new JpegEncodeOptions { Lossless = true, Predictor = predictor, RestartInterval = restart, Precision = bits is 8 or 16 ? 0 : bits });
        var expected = Opaque(src);
        var back = Jpeg.Decode(bytes);
        Assert.Equal(expected.SampleType, back.SampleType);
        if (color != ColorModel.Other) Assert.Equal(expected.Color, back.Color);
        TestUtil.AssertEqual(expected, back, 0);
        var info = Jpeg.GetInfo(bytes);
        Assert.Equal((w, h, expected.Channels, expected.SampleType), (info.Width, info.Height, info.Channels, info.SampleType));
        var oracle = TestUtil.OracleDecode(bytes, ".jpg");
        if (oracle != null) TestUtil.AssertEqual(oracle, back, 0);
    }

    /// <summary>The ISO reference software on our output, components as stored; null when the tool is not installed.</summary>
    static RawImage? OracleDecodeIso(byte[] jpeg)
    {
        if (!TestUtil.PythonAvailable) return null;
        var tmp = Path.Combine(TestUtil.FixtureDir, "_tmp");
        Directory.CreateDirectory(tmp);
        var f = Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            File.WriteAllBytes(f, jpeg);
            var (exit, output) = TestUtil.RunPython($"-c \"import sys, gen_jpeg; gen_jpeg.iso_decode(sys.argv[1], sys.argv[2])\" \"{f}\" \"{f}.ref\"");
            if (TestUtil.CannotVerify(exit, output)) return null;
            Assert.True(exit == 0, "reference software failed: " + output);
            return TestUtil.LoadRef(f + ".ref");
        }
        finally
        {
            File.Delete(f); File.Delete(f + ".ref");
        }
    }

    public static IEnumerable<object[]> ArithmeticLosslessCases()
    {
        foreach (int predictor in new[] { 1, 2, 3, 4, 5, 6, 7 })
        {
            yield return new object[] { ColorModel.Gray, 8, 33, 21, predictor, 0 };
            yield return new object[] { ColorModel.Rgb, 8, 23, 31, predictor, 0 };
            yield return new object[] { ColorModel.Gray, 16, 33, 21, predictor, 0 };
            yield return new object[] { ColorModel.Rgb, 16, 23, 31, predictor, 0 };
        }
        foreach (int bits in new[] { 2, 5, 10, 12, 15 })
            yield return new object[] { ColorModel.Rgb, bits, 20, 17, 4, 0 };
        foreach (var (w, h) in new[] { (1, 1), (1, 9), (9, 1), (130, 97) })
            yield return new object[] { ColorModel.Rgb, 16, w, h, 7, 0 };
        foreach (int restart in new[] { 1, 40, 200 })
        {
            yield return new object[] { ColorModel.Gray, 16, 40, 23, 4, restart };
            yield return new object[] { ColorModel.Rgb, 8, 40, 23, 1, restart };
        }
    }

    /// <summary>
    /// Arithmetic coded lossless JPEG (SOF11): exact through our decoder and through the ISO reference software
    /// (libjpeg-turbo does not implement this process).
    /// </summary>
    [Theory]
    [MemberData(nameof(ArithmeticLosslessCases))]
    public void EncodeArithmeticLosslessRoundTrips(ColorModel color, int bits, int w, int h, int predictor, int restart)
    {
        var src = Synthetic16(w, h, color, bits, seed: w + predictor);
        if (bits == 16)
        {
            var d = src.GetData<ushort>();
            for (int i = 0; i + 4 < d.Length; i += 37) { d[i] = 0; d[i + 1] = 65535; d[i + 2] = 32768; d[i + 3] = 0; }
        }
        var opt = new JpegEncodeOptions { Lossless = true, Arithmetic = true, Predictor = predictor, RestartInterval = restart, Precision = bits is 8 or 16 ? 0 : bits };
        var bytes = Encode(src, opt);
        Assert.True(bytes.AsSpan().IndexOf(new byte[] { 0xFF, 0xCB }) > 0);
        Assert.True(bytes.AsSpan().IndexOf(new byte[] { 0xFF, 0xC4 }) < 0);
        var back = Jpeg.Decode(bytes);
        TestUtil.AssertEqual(src, back, 0);
        var huffman = Encode(src, new JpegEncodeOptions { Lossless = true, Predictor = predictor, Precision = bits is 8 or 16 ? 0 : bits });
        if (w * h > 3000) Assert.True(bytes.Length < huffman.Length, $"arithmetic {bytes.Length} >= Huffman {huffman.Length}");
        var oracle = OracleDecodeIso(bytes);
        if (oracle != null) TestUtil.AssertEqual(oracle, back, 0);
    }

    [Fact]
    public void EncodeLosslessChecksArguments()
    {
        var img = RawImage.Create(8, 8, ColorModel.Gray, SampleType.UInt16);
        img.GetData<ushort>()[3] = 1024;
        Assert.Throws<ArgumentException>(() => Encode(img, new JpegEncodeOptions { Lossless = true, Precision = 10 }));
        Assert.Throws<ArgumentException>(() => Encode(img, new JpegEncodeOptions { Lossless = true, Precision = 17 }));
        Assert.Throws<ArgumentException>(() => Encode(img, new JpegEncodeOptions { Lossless = true, Predictor = 8 }));
        Assert.Throws<ImageNotSupportedException>(() => Encode(RawImage.Create(4, 4, ColorModel.Other, SampleType.UInt8, 5), new JpegEncodeOptions { Lossless = true }));
        Assert.Throws<ImageNotSupportedException>(() => Encode(RawImage.Create(4, 4, ColorModel.Gray, SampleType.Float32), new JpegEncodeOptions { Lossless = true }));
        // metadata survives; gray keeps its density (JFIF), RGB cannot carry one
        var rgb = Synthetic16(9, 7, ColorModel.Rgb, 16);
        rgb.Metadata = new ImageMetadata { DpiX = 300, DpiY = 300, Orientation = 3, IccProfile = new byte[] { 1, 2, 3 } };
        var m = Jpeg.Decode(Encode(rgb, new JpegEncodeOptions { Lossless = true }), Stored).Metadata!;
        Assert.Equal((3, 0.0), (m.Orientation, m.DpiX));
        Assert.Equal(new byte[] { 1, 2, 3 }, m.IccProfile);
    }

    public static IEnumerable<object[]> SamplingCases()
    {
        // whole-number ratios: libjpeg decodes these, so the oracle applies
        foreach (var f in new[] { "1,1,2,1,2,1", "2,2,2,1,2,1", "4,1,1,1,1,1", "1,2,1,1,1,1", "2,1,1,2,1,2", "4,1,2,1,2,1", "2,2,1,1,2,2", "1,4,1,2,1,1" })
            yield return new object[] { f, true };
        // fractional ratios (2:3, 3:4, ...): valid JPEG that libjpeg refuses to decode
        foreach (var f in new[] { "3,1,2,1,2,1", "3,2,2,1,1,2", "4,1,3,1,3,1", "2,3,1,2,2,1", "3,1,1,1,2,1", "2,1,3,1,3,1" })
            yield return new object[] { f, false };
    }

    /// <summary>
    /// Arbitrary sampling factors. Whole-number ratios must match libjpeg; for fractional ones (which libjpeg rejects
    /// with "fractional sampling not implemented") components are interpolated bilinearly and the result has to be a
    /// faithful picture of the source.
    /// </summary>
    [Theory]
    [MemberData(nameof(SamplingCases))]
    public void ArbitrarySamplingFactors(string factors, bool integral)
    {
        var f = factors.Split(',').Select(int.Parse).ToArray();
        foreach (var (w, h) in new[] { (97, 71), (48, 48), (5, 3), (1, 1) })
            foreach (bool progressive in new[] { false, true })
            {
                // smooth content: chroma is band limited enough that subsampling it costs little
                var src = RawImage.Create(w, h, ColorModel.Rgb, SampleType.UInt8);
                var d = src.GetData<byte>();
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        for (int c = 0; c < 3; c++)
                            d[(y * w + x) * 3 + c] = (byte)(128 + 100 * Math.Sin(x * 0.07 + c) * Math.Cos(y * 0.05 + 0.5 * c));
                var bytes = Encode(src, new JpegEncodeOptions { Quality = 95, SamplingFactors = f, Progressive = progressive });
                var back = Jpeg.Decode(bytes);
                Assert.Equal((w, h), (back.Width, back.Height));
                if (w > 16) Assert.True(TestUtil.Psnr(src, back, 255) > 36, $"{factors} {w}x{h}: PSNR {TestUtil.Psnr(src, back, 255):F1}");
                foreach (int scale in new[] { 2, 4, 8 })
                {
                    var small = Jpeg.Decode(bytes, new JpegDecodeOptions { ScaleDenominator = scale });
                    Assert.Equal(((w + scale - 1) / scale, (h + scale - 1) / scale), (small.Width, small.Height));
                }
                if (!integral) continue;
                var oracle = TestUtil.OracleDecode(bytes, ".jpg");
                if (oracle != null) TestUtil.AssertEqual(oracle, back, 0);
            }
    }

    public static IEnumerable<object[]> ArithmeticEncodeCases()
    {
        foreach (bool progressive in new[] { false, true })
        {
            foreach (var color in new[] { ColorModel.Gray, ColorModel.Rgb })
                foreach (var ss in color == ColorModel.Gray ? new[] { JpegSubsampling.Auto } : new[] { JpegSubsampling.Chroma444, JpegSubsampling.Chroma422, JpegSubsampling.Chroma420 })
                {
                    foreach (var (w, h) in new[] { (1, 1), (7, 5), (17, 33), (64, 48), (131, 97) })
                        yield return new object[] { color, 8, w, h, 85, ss, progressive, 0 };
                    foreach (int q in new[] { 2, 30, 97, 100 })
                        yield return new object[] { color, 8, 67, 45, q, ss, progressive, 0 };
                    foreach (int restart in new[] { 1, 3, 50 })
                        yield return new object[] { color, 8, 70, 50, 80, ss, progressive, restart };
                    yield return new object[] { color, 12, 67, 45, 90, ss, progressive, 0 };
                    yield return new object[] { color, 12, 40, 31, 100, ss, progressive, 2 };
                }
        }
    }

    /// <summary>
    /// Arithmetic coded output (sequential and progressive, 8 and 12 bit): libjpeg-turbo 3 reads it to the same pixels as
    /// our decoder, and those are the pixels of the Huffman coded file with the same settings (only the entropy coding differs).
    /// </summary>
    [Theory]
    [MemberData(nameof(ArithmeticEncodeCases))]
    public void EncodeArithmeticRoundTrips(ColorModel color, int bits, int w, int h, int quality, JpegSubsampling ss, bool progressive, int restart)
    {
        var src = bits == 8 ? Synthetic(w, h, color, seed: w + quality) : Synthetic16(w, h, color, 12, seed: w + quality);
        var opt = new JpegEncodeOptions { Quality = quality, Subsampling = ss, Progressive = progressive, RestartInterval = restart, Arithmetic = true };
        var bytes = Encode(src, opt);
        Assert.True(bytes.AsSpan().IndexOf(new byte[] { 0xFF, (byte)(progressive ? 0xCA : 0xC9) }) > 0);
        Assert.True(bytes.AsSpan().IndexOf(new byte[] { 0xFF, 0xC4 }) < 0, "no Huffman tables in an arithmetic coded file");
        var back = Jpeg.Decode(bytes);
        var huffman = Encode(src, new JpegEncodeOptions { Quality = quality, Subsampling = ss, Progressive = progressive, RestartInterval = restart });
        TestUtil.AssertEqual(Jpeg.Decode(huffman), back, 0);
        if (w * h > 3000 && restart == 0) Assert.True(bytes.Length < huffman.Length, $"arithmetic {bytes.Length} >= Huffman {huffman.Length}");
        // imagecodecs (libjpeg-turbo 3 with arithmetic and 12 bit support) is the independent reader here
        var oracle = OracleDecodeTurbo(bytes);
        if (oracle != null) TestUtil.AssertEqual(oracle, back, 0);
    }

    /// <summary>Decodes with imagecodecs' libjpeg-turbo 3 regardless of the file type (Pillow cannot feed arithmetic decoding).</summary>
    static RawImage? OracleDecodeTurbo(byte[] jpeg)
    {
        if (!TestUtil.PythonAvailable) return null;
        var tmp = Path.Combine(TestUtil.FixtureDir, "_tmp");
        Directory.CreateDirectory(tmp);
        var f = Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            File.WriteAllBytes(f, jpeg);
            var (exit, output) = TestUtil.RunPython($"-c \"import sys, imagecodecs; from pxref import write_ref; write_ref(sys.argv[2], imagecodecs.jpeg8_decode(open(sys.argv[1], 'rb').read()))\" \"{f}\" \"{f}.ref\"");
            Assert.True(exit == 0, "oracle decode failed: " + output);
            return TestUtil.LoadRef(f + ".ref");
        }
        finally
        {
            File.Delete(f); File.Delete(f + ".ref");
        }
    }

    [Fact]
    public void EncodeQualityOrdersSizeAndFidelity()
    {
        var src = Synthetic(160, 120, ColorModel.Rgb, 3);
        long lastSize = 0; double lastPsnr = 0;
        foreach (int q in new[] { 10, 40, 70, 90, 98 })
        {
            var bytes = Encode(src, new JpegEncodeOptions { Quality = q, Subsampling = JpegSubsampling.Chroma444 });
            double psnr = TestUtil.Psnr(src, Jpeg.Decode(bytes), 255);
            Assert.True(bytes.Length > lastSize, $"q{q}: size {bytes.Length} <= {lastSize}");
            Assert.True(psnr > lastPsnr, $"q{q}: psnr {psnr} <= {lastPsnr}");
            lastSize = bytes.Length; lastPsnr = psnr;
        }
        Assert.True(lastPsnr > 40);

        // optimized tables never lose against the standard ones, progressive is in the same ballpark
        var o = new JpegEncodeOptions { Quality = 80 };
        int plain = Encode(src, new JpegEncodeOptions { Quality = 80, OptimizeHuffman = false }).Length;
        int optimized = Encode(src, o).Length;
        int progressive = Encode(src, new JpegEncodeOptions { Quality = 80, Progressive = true }).Length;
        Assert.True(optimized <= plain, $"{optimized} > {plain}");
        Assert.True(progressive < plain * 1.05, $"{progressive} vs {plain}");
    }

    [Fact]
    public void EncodeMetadataRoundTrips()
    {
        var src = Synthetic(20, 12, ColorModel.Rgb);
        var icc = new byte[150_000];
        new Random(5).NextBytes(icc);
        var exif = Jpeg.Decode(Load("jpeg/meta_exif_icc.jpg"), Stored).Metadata!.Exif!;
        src.Metadata = new ImageMetadata { DpiX = 300, DpiY = 72, IccProfile = icc, Exif = exif };
        foreach (bool progressive in new[] { false, true })
        {
            var bytes = Encode(src, new JpegEncodeOptions { Progressive = progressive });
            var m = Jpeg.Decode(bytes, Stored).Metadata!;
            Assert.Equal((300.0, 72.0), (m.DpiX, m.DpiY));
            Assert.Equal(icc, m.IccProfile);
            Assert.Equal(exif, m.Exif);
            Assert.Equal(6, m.Orientation);
        }

        // orientation without an EXIF blob is still carried
        src.Metadata = new ImageMetadata { Orientation = 8 };
        var m2 = Jpeg.Decode(Encode(src), Stored).Metadata!;
        Assert.Equal(8, m2.Orientation);
        // the metadata's orientation wins over the one in the EXIF blob
        src.Metadata = new ImageMetadata { Orientation = 3, Exif = exif };
        Assert.Equal(3, Jpeg.Decode(Encode(src), Stored).Metadata!.Orientation);
        Assert.Equal(0, m2.DpiX);

        src.Metadata = null;
        Assert.Null(Jpeg.Decode(Encode(src)).Metadata);
    }

    [Fact]
    public void EncodeRejectsUnsupportedInput()
    {
        Assert.Throws<ImageNotSupportedException>(() => Encode(RawImage.Create(4, 4, ColorModel.Rgb, SampleType.Int16)));
        Assert.Throws<ImageNotSupportedException>(() => Encode(RawImage.Create(4, 4, ColorModel.Gray, SampleType.Float32)));
        Assert.Throws<ImageNotSupportedException>(() => Encode(RawImage.Create(4, 4, ColorModel.Other, SampleType.UInt8, 5)));
    }

    /// <summary>All forward / inverse DCT code paths are bit identical, and a round trip is accurate to the rounding.</summary>
    [Fact]
    public unsafe void DctPathsAgree()
    {
        var rnd = new Random(42);
        short* coef = stackalloc short[64];
        int* quant = stackalloc int[64];
        float* recip = stackalloc float[64];
        byte* a = stackalloc byte[64]; byte* b = stackalloc byte[64]; byte* src = stackalloc byte[64];
        short* za = stackalloc short[64]; short* zb = stackalloc short[64];
        lock (PathLock)
        {
            try
            {
                for (int iter = 0; iter < 3000; iter++)
                {
                    int density = rnd.Next(1, 65), range = iter % 3 == 0 ? 32768 : 300;
                    for (int i = 0; i < 64; i++)
                    {
                        coef[i] = (short)(rnd.Next(64) < density ? rnd.Next(-range, range) : 0);
                        quant[i] = iter % 5 == 0 ? rnd.Next(1, 256) : rnd.Next(1, 20);
                        recip[i] = 1f / (8 * rnd.Next(1, 50));
                        src[i] = (byte)rnd.Next(256);
                    }
                    JpegDct.ForceLevel = -1; JpegDct.Idct(coef, quant, a, 8);
                    for (int level = 0; level <= 1; level++)
                    {
                        JpegDct.ForceLevel = level; JpegDct.Idct(coef, quant, b, 8);
                        Assert.True(new Span<byte>(a, 64).SequenceEqual(new Span<byte>(b, 64)), $"idct level {level} iteration {iter}");
                    }
                    JpegDct.ForceLevel = -1; JpegDct.FdctQuantize(src, 8, recip, za);
                    for (int level = 0; level <= 1; level++)
                    {
                        JpegDct.ForceLevel = level; JpegDct.FdctQuantize(src, 8, recip, zb);
                        Assert.True(new Span<short>(za, 64).SequenceEqual(new Span<short>(zb, 64)), $"fdct level {level} iteration {iter}");
                    }

                    // forward then inverse with unit quantization reproduces the samples to within one level
                    for (int i = 0; i < 64; i++) { recip[i] = 1f / 8; quant[i] = 1; }
                    JpegDct.ForceLevel = -1;
                    JpegDct.FdctQuantize(src, 8, recip, za);
                    for (int i = 0; i < 64; i++) coef[JpegTables.ZigZag[i]] = za[i];
                    JpegDct.Idct(coef, quant, b, 8);
                    for (int i = 0; i < 64; i++) Assert.InRange(b[i] - src[i], -1, 1);
                }
            }
            finally
            {
                JpegDct.ForceLevel = -1;
            }
        }
    }
}
