using Aardvark.ImageCodecs;
using Aardvark.ImageCodecs.JpegXl;
using Xunit;
using Xunit.Abstractions;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// Rendering stages of ISO/IEC 18181-1:2024 (patches, splines, noise, upsampling, VarDCT dequantization, colour
/// transforms) checked against the text and against libjxl on hand-built codestreams.
/// </summary>
public class JxlAuditRenderTests(ITestOutputHelper output)
{
    // ------------------------------------------------------------------ codestream builder

    sealed class Plane
    {
        public readonly float[] V;
        public Plane(int w, int h, Func<int, int, float> f)
        {
            V = new float[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) V[y * w + x] = f(x, y);
        }
    }

    /// <summary>A float32 image (RGB or grey, not XYB) with the given extra channels, coded as lossless modular frames.</summary>
    sealed class Stream
    {
        public readonly JxlImageHeader Header;
        readonly JxlBitWriter w = new();

        static readonly JxlBitDepth Float32 = new() { Float = true, Bits = 32, ExpBits = 8 };

        public Stream(int width, int height, params JxlExtraChannelType[] extra) : this(width, height, false, extra) { }

        public Stream(int width, int height, bool grey, params JxlExtraChannelType[] extra)
        {
            Header = new JxlImageHeader
            {
                Width = width, Height = height, BitDepth = Float32, XybEncoded = false, Modular16BitBuffers = false,
                ExtraChannels = extra.Select(t => new JxlExtraChannel { Type = t, BitDepth = Float32 }).ToArray(),
            };
            if (grey) Header.Colour = new JxlColourEncoding { ColourSpace = JxlColourSpace.Grey };
            JxlHeaderWriter.WriteImageHeader(w, Header);
            w.ZeroPadToByte();
        }

        public int NumColour => Header.Colour.IsGray ? 1 : 3;

        public JxlFrameHeader NewFrame(int width, int height)
        {
            int ne = Header.ExtraChannels.Length;
            var fh = new JxlFrameHeader
            {
                Modular = true, Width = width, Height = height, EcUpsampling = Enumerable.Repeat(1, ne).ToArray(),
                EcBlending = new JxlBlendingInfo[ne], Filter = new JxlRestorationFilter { Gab = false, EpfIters = 0 },
            };
            fh.HaveCrop = width != Header.Width || height != Header.Height;
            return fh;
        }

        /// <summary>Appends a frame; the planes are colour (1 or 3) then extra channels, at frame size.</summary>
        public void Frame(JxlFrameHeader fh, Plane[] planes, Action<JxlBitWriter>? features = null)
        {
            var chans = planes.Select(p =>
            {
                var c = new JxlChannel(fh.Width, fh.Height, 0, 0);
                for (int i = 0; i < c.Data.Length; i++) c.Data[i] = BitConverter.SingleToInt32Bits(p.V[i]);
                return c;
            }).ToList();
            JxlLosslessEncoder.EncodeFrame(w, chans, Header, fh, JxlLosslessParams.For(2), false, features);
        }

        public byte[] ToArray() => w.ToArray();
    }

    /// <summary>Hybrid uint tokens of one entropy coded stream with the given number of contexts, then written.</summary>
    sealed class Tokens(int contexts)
    {
        readonly JxlTokens t = new();
        public void Add(int ctx, long v) => t.Add(ctx, (uint)v);
        public void AddSigned(int ctx, long v) => t.Add(ctx, JxlBitWriter.PackSigned((int)v));
        public void WriteTo(JxlBitWriter w)
        {
            var enc = new JxlEntropyEncoder(contexts) { MaxClusters = contexts };
            enc.Build(new[] { t }, false);
            enc.WriteCode(w);
            enc.WriteTokens(w, t);
        }
    }

    static float Rand(int seed, int x, int y) => (float)(((uint)(x * 73856093 ^ y * 19349663 ^ seed * 83492791) % 10007) / 10007.0);

    static Plane[] Planes(int w, int h, int n, int seed) => Enumerable.Range(0, n).Select(c => new Plane(w, h, (x, y) => Rand(seed + c, x, y))).ToArray();

    static void Dump(string name, byte[] data)
    {
        if (Environment.GetEnvironmentVariable("PIXCODEC_JXLAUDIT_DUMP") is string dump) File.WriteAllBytes(Path.Combine(dump, name + ".jxl"), data);
    }

    /// <summary>Our float decode and libjxl's (null without the oracle), both as interleaved floats.</summary>
    (float[] ours, float[]? lib, int channels) DecodeBoth(byte[] data, string name)
    {
        Dump(name, data);
        var ours = Jxl.Decode(data, new JxlDecodeOptions { ApplyOrientation = false });
        Assert.Equal(SampleType.Float32, ours.SampleType);
        var lib = JxlEncodeTests.Libjxl(data);
        if (lib == null) output.WriteLine($"{name}: libjxl not available, decoded by us only");
        else Assert.Equal((ours.Width, ours.Height, ours.Channels), (lib.Width, lib.Height, lib.Channels));
        return ((float[])ours.Data, (float[]?)lib?.Data, ours.Channels);
    }

    double MaxDiff(byte[] data, string name)
    {
        var (ours, lib, nc) = DecodeBoth(data, name);
        if (lib == null) return 0;
        double m = 0;
        for (int i = 0; i < ours.Length; i++) m = Math.Max(m, Math.Abs(ours[i] - lib[i]));
        output.WriteLine($"{name}: largest difference to libjxl {m:g3} ({nc} channels)");
        return m;
    }

    void AssertClose(byte[] data, string name, double tol)
    {
        double m = MaxDiff(data, name);
        Assert.True(m <= tol, $"{name}: {m} > {tol}");
    }

    /// <summary>
    /// The codestream of a file with the header of each frame changed by <paramref name="edit"/> (the sections kept, the
    /// TOC rewritten in logical order). For frames of cjxl that differ from what our encoder writes.
    /// </summary>
    static byte[] EditFrames(byte[] file, Action<JxlFrameHeader> edit)
    {
        var cs = JxlContainer.Parse(file).Codestream(file);
        var br = new JxlBitReader(cs);
        var h = JxlImageHeader.Read(br);
        if (h.Colour.WantIcc) JxlIcc.Read(br);
        br.ZeroPadToByte();
        Assert.False(h.HavePreview);
        int pos = br.BytePosition;
        var w = new JxlBitWriter();
        w.AppendBytes(cs[..pos]);
        while (pos < cs.Length)
        {
            br = new JxlBitReader(cs, pos, cs.Length - pos);
            var fh = JxlFrameHeader.Read(br, h);
            var toc = JxlToc.Read(br, JxlFrameDims.Of(fh));
            int start = pos + br.BytePosition;
            var sections = Enumerable.Range(0, toc.Size.Length).Select(i => cs[(start + toc.Offset[i])..(start + toc.Offset[i] + toc.Size[i])]).ToList();
            edit(fh);
            JxlHeaderWriter.WriteFrameHeader(w, fh, h, customGab: fh.Filter.Gab);
            JxlHeaderWriter.WriteToc(w, sections.Select(s => s.Length).ToArray());
            foreach (var s in sections) w.AppendBytes(s);
            pos = start + toc.TotalSize;
            if (fh.IsLast) break;
        }
        return w.ToArray();
    }

    static byte[] JxlFixture(string name) => File.ReadAllBytes(Path.Combine(FixtureDir, "jxl", name));

    // ------------------------------------------------------------------ J.1, L.3 YCbCr frames

    /// <summary>Per channel Gaborish weights (exact in F16) that do not commute with the YCbCr conversion.</summary>
    static readonly float[] GabWeights = { 0.25f, 0.125f, 0.0625f, 0.03125f, 0.1875f, 0.125f };

    static byte[] WithFilters(string name, bool gab, int epf) => EditFrames(JxlFixture(name), fh =>
    {
        Assert.True(fh.DoYCbCr && !fh.Filter.Gab && fh.Filter.EpfIters == 0);
        fh.Filter.Gab = gab;
        fh.Filter.GabWeights = (float[])GabWeights.Clone();
        fh.Filter.EpfIters = epf;
    });

    /// <summary>
    /// J.1, K.3.2, L.3: in a YCbCr frame chroma is upsampled first, then the restoration filters run on Cb, Y, Cr, and
    /// only the colour transform makes RGB of them. cjxl's recompressed JPEGs (frames without filters) with Gaborish and
    /// EPF switched on, against libjxl (EPF does nothing here: the sharpness of these frames is 0).
    /// </summary>
    [Theory]
    [InlineData("jpeg444.jxl", 0)]
    [InlineData("jpeg444.jxl", 3)]
    [InlineData("jpeg422.jxl", 2)]
    public void YCbCrIsFilteredBeforeTheColourTransform(string name, int epf)
    {
        var m = LossyDiff(WithFilters(name, true, epf), $"{name} gab epf {epf}");
        Assert.True(m == null || m < 1.5 / 255, $"{m}");
    }

    /// <summary>
    /// The same with 4:2:0 chroma, where libjxl differs from the text (by up to 0.07, on every other row): the expected
    /// image is libjxl's decode of the frame without filters (chroma upsampled as in J.2), taken back to Cb, Y, Cr,
    /// filtered as in J.3 and converted as in L.3.
    /// </summary>
    [Fact]
    public void YCbCr420IsFilteredBeforeTheColourTransform()
    {
        var lib = JxlEncodeTests.Libjxl(WithFilters("jpeg420.jxl", false, 0));
        if (lib == null) return;
        var ours = Floats(Jxl.Decode(WithFilters("jpeg420.jxl", true, 0), new JxlDecodeOptions { ForceFloat = true }));
        int w = lib.Width, h = lib.Height, n = w * h;
        var src = Floats(lib);
        const double a = 0.344136286, b = 0.714136286, off = 128 / 255.0;
        var ycc = new double[3][];
        for (int c = 0; c < 3; c++) ycc[c] = new double[n];
        for (int i = 0; i < n; i++)
        {
            double R = src[3 * i], G = src[3 * i + 1], B = src[3 * i + 2];
            double y = (G + a * B / 1.772 + b * R / 1.402) / (1 + a / 1.772 + b / 1.402);
            ycc[0][i] = (B - y) / 1.772; ycc[1][i] = y - off; ycc[2][i] = (R - y) / 1.402;
        }
        int M(int v, int len) => v < 0 ? -v - 1 : v >= len ? 2 * len - 1 - v : v;
        // where libjxl's 8 bit output clipped a sample its Cb, Y, Cr cannot be recovered: such neighbourhoods are skipped
        bool Clipped(int x, int y) { int j = 3 * (M(y, h) * w + M(x, w)); return Enumerable.Range(j, 3).Any(k => src[k] <= 0 || src[k] >= 1); }
        var libOn = Floats(JxlEncodeTests.Libjxl(WithFilters("jpeg420.jxl", true, 0))!);
        double m = 0, ml = 0;
        int used = 0;
        for (int i = 0; i < n; i++)
        {
            int x = i % w, yy = i / w;
            bool skip = false;
            for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) skip |= Clipped(x + dx, yy + dy);
            if (skip) continue;
            used++;
            var f = new double[3];
            for (int c = 0; c < 3; c++)
            {
                double w1 = GabWeights[2 * c], w2 = GabWeights[2 * c + 1];
                double S(int dx, int dy) => ycc[c][M(yy + dy, h) * w + M(x + dx, w)];
                f[c] = (S(0, 0) + w1 * (S(-1, 0) + S(1, 0) + S(0, -1) + S(0, 1)) + w2 * (S(-1, -1) + S(1, -1) + S(-1, 1) + S(1, 1))) / (1 + 4 * (w1 + w2));
            }
            double Y = f[1] + off;
            double[] rgb = { Y + 1.402 * f[2], Y - a * f[0] - b * f[2], Y + 1.772 * f[0] };
            for (int c = 0; c < 3; c++) m = Math.Max(m, Math.Abs(Math.Clamp(ours[3 * i + c], 0, 1) - Math.Clamp(rgb[c], 0, 1)));
            for (int c = 0; c < 3; c++) ml = Math.Max(ml, Math.Abs(libOn[3 * i + c] - Math.Clamp(rgb[c], 0, 1)));
        }
        output.WriteLine($"4:2:0 with Gaborish: largest difference to J.3 / L.3 on libjxl's unfiltered decode: ours {m:g3}, libjxl {ml:g3} ({used} of {n} pixels)");
        Assert.True(used > n / 2);
        Assert.True(m < 1.5 / 255, $"{m}");
    }

    /// <summary>I.5.2: "If this adaptive smoothing procedure is applied, no channel is subsampled."</summary>
    [Fact]
    public void AdaptiveLfSmoothingWithSubsampledChromaIsRefused()
    {
        var data = EditFrames(JxlFixture("jpeg420.jxl"), fh => fh.Flags &= ~JxlFrameHeader.FlagSkipAdaptiveLfSmoothing);
        Dump("smoothing 420", data);
        Assert.Throws<ImageFormatException>(() => Jxl.Decode(data));
    }

    // ------------------------------------------------------------------ K.3 patches

    /// <summary>
    /// A reference frame, then a frame drawing one patch at two positions; <paramref name="modes"/> gives the blend mode
    /// of colour and of each extra channel, <paramref name="index"/> the alpha_channel written for a mode above 3 (none
    /// when null).
    /// </summary>
    static byte[] PatchStream(JxlExtraChannelType[] extra, int[] modes, Func<int, int?> index, bool clamp = false)
    {
        const int W = 40, H = 32;
        int ne = extra.Length;
        var s = new Stream(W, H, extra);
        var r = s.NewFrame(W, H);
        r.Type = JxlFrameType.ReferenceOnly; r.IsLast = false; r.SaveAsReference = 1; r.SaveBeforeColourTransform = true;
        s.Frame(r, Enumerable.Range(0, 3 + ne).Select(c => new Plane(W, H, (x, y) => Rand(7 + c, x, y) * 1.6f - 0.3f)).ToArray());
        var f = s.NewFrame(W, H);
        f.Flags = JxlFrameHeader.FlagPatches;
        s.Frame(f, Planes(W, H, 3 + ne, 20), bw =>
        {
            var t = new Tokens(10);
            t.Add(0, 1);
            t.Add(1, 1);
            t.Add(3, 5); t.Add(3, 4);
            t.Add(2, 9 - 1); t.Add(2, 7 - 1);
            t.Add(7, 2 - 1);
            for (int j = 0; j < 2; j++)
            {
                if (j == 0) { t.Add(4, 3); t.Add(4, 2); }
                else { t.AddSigned(6, 17); t.AddSigned(6, 11); }
                for (int c = 0; c <= ne; c++)
                {
                    t.Add(5, modes[c]);
                    if (modes[c] > 3 && index(c) is int k) t.Add(8, k);
                    if (modes[c] > 2) t.Add(9, clamp ? 1 : 0);
                }
            }
            t.WriteTo(bw);
        });
        return s.ToArray();
    }

    /// <summary>
    /// K.3.1 reads a patch's alpha_channel when its mode uses alpha and "there is more than 1 alpha channel". libjxl
    /// reads it whenever there is more than one extra channel: with extra channels {depth, alpha} it decodes the stream
    /// that carries the index and refuses the one written by the letter of the text. We do the same.
    /// </summary>
    [Fact]
    public void PatchAlphaChannelFollowsLibjxl()
    {
        var ex = new[] { JxlExtraChannelType.Depth, JxlExtraChannelType.Alpha };
        AssertClose(PatchStream(ex, new[] { 4, 1, 4 }, _ => 1), "patch alpha index, depth + alpha", 1e-4);
        var letter = PatchStream(ex, new[] { 4, 1, 4 }, _ => null);
        Dump("patch alpha index by the letter", letter);
        Assert.ThrowsAny<ImageFormatException>(() => Jxl.Decode(letter));
        if (PythonAvailable)
        {
            var tmp = Path.Combine(FixtureDir, "_tmp");
            Directory.CreateDirectory(tmp);
            var f = Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".jxl");
            try
            {
                File.WriteAllBytes(f, letter);
                var (exit, text) = RunPython($"decode_to_ref.py \"{f}\" \"{f}.ref\"");
                if (!CannotVerify(exit, text)) Assert.NotEqual(0, exit);
            }
            finally { File.Delete(f); File.Delete(f + ".ref"); }
        }
    }

    /// <summary>
    /// K.3.2: "the blending is performed according to patch[i].blending[j].mode[c]", so the alpha channel blends by its
    /// own mode even when the colour alpha-blends with it. libjxl instead gives that alpha channel the composited alpha
    /// whenever the colour uses kBlendAbove / kBlendBelow. Colour modes 4 and 5 with each alpha mode, against libjxl.
    /// </summary>
    [Theory]
    [InlineData(4, 0)]
    [InlineData(4, 1)]
    [InlineData(4, 2)]
    [InlineData(4, 3)]
    [InlineData(4, 6)]
    [InlineData(5, 2)]
    [InlineData(5, 7)]
    [InlineData(6, 2)]
    public void PatchAlphaModeWithBlendedColour(int colourMode, int alphaMode)
    {
        var data = PatchStream(new[] { JxlExtraChannelType.Alpha }, new[] { colourMode, alphaMode }, _ => null, clamp: true);
        AssertClose(data, $"patch colour {colourMode} alpha {alphaMode}", 1e-4);
    }

    // ------------------------------------------------------------------ K.4 splines, K.5 noise

    /// <summary>Splines (K.4.1) with the given control points; colour and sigma DCTs from a seeded generator.</summary>
    static void WriteSplines(JxlBitWriter w, (int x, int y)[][] splines, int seed)
    {
        var t = new Tokens(6);
        t.Add(2, splines.Length - 1);
        for (int i = 0; i < splines.Length; i++)
        {
            var p = splines[i][0];
            if (i == 0) { t.Add(1, p.x); t.Add(1, p.y); }
            else { t.AddSigned(1, p.x - splines[i - 1][0].x); t.AddSigned(1, p.y - splines[i - 1][0].y); }
        }
        t.AddSigned(0, 0);
        var rng = new Random(seed);
        foreach (var sp in splines)
        {
            t.Add(3, sp.Length - 1);
            int dx = 0, dy = 0;
            for (int j = 1; j < sp.Length; j++)
            {
                int ndx = sp[j].x - sp[j - 1].x, ndy = sp[j].y - sp[j - 1].y;
                t.AddSigned(4, ndx - dx); t.AddSigned(4, ndy - dy);
                dx = ndx; dy = ndy;
            }
            for (int c = 0; c < 4; c++)
                for (int k = 0; k < 32; k++) t.AddSigned(5, k == 0 ? (c == 3 ? 6 : 14 + 3 * c) : k < 6 ? rng.Next(-4, 5) : 0);
        }
        t.WriteTo(w);
    }

    static void WriteNoise(JxlBitWriter w) { foreach (int v in new[] { 80, 150, 260, 330, 420, 610, 700, 1010 }) w.Write(10, (uint)v); }

    /// <summary>
    /// K.4.2 and K.5.2 add splines and noise to the colour channels (X, Y, B) only, never to extra channels. Grey and RGB
    /// images with up to two extra channels, against libjxl.
    /// </summary>
    [Theory]
    [InlineData(true, 2, false)]
    [InlineData(true, 2, true)]
    [InlineData(true, 0, false)]
    [InlineData(true, 1, true)]
    [InlineData(false, 2, false)]
    [InlineData(false, 1, true)]
    public void SplinesAndNoiseOnColourOnly(bool grey, int extra, bool noise)
    {
        const int W = 72, H = 56;
        var ex = new[] { JxlExtraChannelType.Alpha, JxlExtraChannelType.Depth }.Take(extra).ToArray();
        var s = new Stream(W, H, grey, ex);
        var f = s.NewFrame(W, H);
        f.IsLast = true; f.Flags = noise ? JxlFrameHeader.FlagNoise : JxlFrameHeader.FlagSplines;
        var splines = new[] { new[] { (10, 12), (40, 20), (60, 45) }, new[] { (8, 50), (30, 30) } };
        s.Frame(f, Planes(W, H, s.NumColour + extra, 3).Select(p => new Plane(W, H, (x, y) => 0.2f + 0.5f * p.V[y * W + x])).ToArray(),
            bw => { if (noise) WriteNoise(bw); else WriteSplines(bw, splines, 5); });
        AssertClose(s.ToArray(), $"{(noise ? "noise" : "splines")} {(grey ? "grey" : "rgb")} + {extra}", noise ? 2e-5 : 0.02);
    }

    /// <summary>
    /// K.4.2 with a single control point: all_samples is {(S[0], 1), (S[0], 0)} and the arc length 0, so the first sample
    /// has arclength_from_start = min(1, 0 / 0), which the text leaves undefined. libjxl draws nothing (drawn at t = 0
    /// or 31 the brush would add up to 0.08 / 0.12 here), and neither do we.
    /// </summary>
    [Fact]
    public void SinglePointSpline()
    {
        const int W = 64, H = 48;
        var s = new Stream(W, H);
        var f = s.NewFrame(W, H);
        f.IsLast = true; f.Flags = JxlFrameHeader.FlagSplines;
        s.Frame(f, Enumerable.Range(0, 3).Select(c => new Plane(W, H, (x, y) => 0.3f)).ToArray(),
            bw => WriteSplines(bw, new[] { new[] { (30, 25) } }, 9));
        AssertClose(s.ToArray(), "single point spline", 1e-3);
    }

    // ------------------------------------------------------------------ Annex M limits

    /// <summary>
    /// Table M.1 sets no limit on the area patches cover: 1025 patches of a 1024 x 1024 frame (more than 4 times its
    /// pixels, within min(2^24, pixels / 16) patches) are read.
    /// </summary>
    [Fact]
    public void PatchAreaIsNotLimited()
    {
        var w = new JxlBitWriter();
        var t = new Tokens(10);
        t.Add(0, 1);
        t.Add(1, 0); t.Add(3, 0); t.Add(3, 0); t.Add(2, 1023); t.Add(2, 1023); t.Add(7, 1024);
        for (int j = 0; j < 1025; j++)
        {
            if (j == 0) { t.Add(4, 0); t.Add(4, 0); } else { t.AddSigned(6, 0); t.AddSigned(6, 0); }
            t.Add(5, 2);
        }
        t.WriteTo(w);
        w.ZeroPadToByte();
        Assert.Equal(1025, JxlPatches.Read(new JxlBitReader(w.ToArray()), 0, 1024 * 1024).Items.Count);
    }

    /// <summary>Splines with the given numbers of control points (each including the starting point).</summary>
    static byte[] SplineDictionary(int[] points)
    {
        var w = new JxlBitWriter();
        var t = new Tokens(6);
        t.Add(2, points.Length - 1);
        for (int i = 0; i < points.Length; i++) { if (i == 0) { t.Add(1, 2); t.Add(1, 2); } else { t.AddSigned(1, 1); t.AddSigned(1, 0); } }
        t.AddSigned(0, 0);
        foreach (int n in points)
        {
            t.Add(3, n - 1);
            for (int j = 1; j < n; j++) { t.AddSigned(4, j == 1 ? 1 : 0); t.AddSigned(4, 0); }
            for (int k = 0; k < 128; k++) t.AddSigned(5, k % 32 == 0 ? 1 : 0);
        }
        t.WriteTo(w);
        w.ZeroPadToByte();
        return w.ToArray();
    }

    /// <summary>
    /// Table M.1: at most min(2^24, pixels / 4) splines and min(2^20, pixels / 2) control points in all, where
    /// num_control_points includes the starting point (K.4.1). An 8 x 8 frame: 16 splines, 32 points.
    /// </summary>
    [Fact]
    public void SplineLimitsAreTableM1()
    {
        JxlSplines Read(params int[] points) => JxlSplines.Read(new JxlBitReader(SplineDictionary(points)), 64);
        Read(Enumerable.Repeat(1, 16).ToArray());
        Assert.Throws<ImageFormatException>(() => Read(Enumerable.Repeat(1, 17).ToArray()));
        Read(16, 16);
        Assert.Throws<ImageFormatException>(() => Read(16, 17));
        Assert.Throws<ImageFormatException>(() => Read(Enumerable.Repeat(2, 15).Append(3).ToArray()));
    }

    // ------------------------------------------------------------------ K.2 upsampling weights

    /// <summary>d_up2, d_up4 and d_up8 of K.2 as printed in 18181-1:2024.</summary>
    static readonly string[] SpecUpWeights =
    {
        // d_up2
        "-0.01716200, -0.03452303, -0.04022174, -0.02921014, -0.00624645, 0.14111091, 0.28896755, 0.00278718, " +
        "-0.01610267, 0.56661550, 0.03777607, -0.01986694, -0.03144731, -0.01185068, -0.00213539 ",
        // d_up4
        "-0.02419067, -0.03491987, -0.03693351, -0.03094285, -0.00529785, -0.01663432, -0.03556863, -0.03888905, " +
        "-0.03516850, -0.00989469, 0.23651958, 0.33392945, -0.01073543, -0.01313181, -0.03556694, 0.13048175, " +
        "0.40103025, 0.03951150, -0.02077584, 0.46914198, -0.00209270, -0.01484589, -0.04064806, 0.18942530, " +
        "0.56279892, 0.06674400, -0.02335494, -0.03551682, -0.00754830, -0.02267919, -0.02363578, 0.00315804, " +
        "-0.03399098, -0.01359519, -0.00091653, -0.00335467, -0.01163294, -0.01610294, -0.00974088, -0.00191622, " +
        "-0.01095446, -0.03198464, -0.04455121, -0.02799790, -0.00645912, 0.06390599, 0.22963888, 0.00630981, " +
        "-0.01897349, 0.67537268, 0.08483369, -0.02534994, -0.02205197, -0.01667999, -0.00384443 ",
        // d_up8
        "-0.02928613, -0.03706353, -0.03783812, -0.03324558, -0.00447632, -0.02519406, -0.03752601, -0.03901508, " +
        "-0.03663285, -0.00646649, -0.02066407, -0.03838633, -0.04002101, -0.03900035, -0.00901973, -0.01626393, " +
        "-0.03954148, -0.04046620, -0.03979621, -0.01224485, 0.29895328, 0.35757708, -0.02447552, -0.01081748, " +
        "-0.04314594, 0.23903219, 0.41119301, -0.00573046, -0.01450239, -0.04246845, 0.17567618, 0.45220643, " +
        "0.02287757, -0.01936783, -0.03583255, 0.11572472, 0.47416733, 0.06284440, -0.02685066, 0.42720050, " +
        "-0.02248939, -0.01155273, -0.04562755, 0.28689496, 0.49093869, -0.00007891, -0.01545926, -0.04562659, " +
        "0.21238920, 0.53980934, 0.03369474, -0.02070211, -0.03866988, 0.14229550, 0.56593398, 0.08045181, -0.02888298, " +
        "-0.03680918, -0.00542229, -0.02920477, -0.02788574, -0.02118180, -0.03942402, -0.00775547, -0.02433614, " +
        "-0.03193943, -0.02030828, -0.04044014, -0.01074016, -0.01930822, -0.03620399, -0.01974125, -0.03919545, " +
        "-0.01456093, -0.00045072, -0.00360110, -0.01020207, -0.01231907, -0.00638988, -0.00071592, -0.00279122, " +
        "-0.00957115, -0.01288327, -0.00730937, -0.00107783, -0.00210156, -0.00890705, -0.01317668, -0.00813895, " +
        "-0.00153491, -0.02128481, -0.04173044, -0.04831487, -0.03293190, -0.00525260, -0.01720322, -0.04052736, " +
        "-0.05045706, -0.03607317, -0.00738030, -0.01341764, -0.03965629, -0.05151616, -0.03814886, -0.01005819, " +
        "0.18968273, 0.33063684, -0.01300105, -0.01372950, -0.04017465, 0.13727832, 0.36402234, 0.01027890, " +
        "-0.01832107, -0.03365072, 0.08734506, 0.38194295, 0.04338228, -0.02525993, 0.56408126, 0.00458352, " +
        "-0.01648227, -0.04887868, 0.24585519, 0.62026135, 0.04314807, -0.02213737, -0.04158014, 0.16637289, " +
        "0.65027023, 0.09621636, -0.03101388, -0.04082742, -0.00904519, -0.02790922, -0.02117818, 0.00798662, " +
        "-0.03995711, -0.01243427, -0.02231705, -0.02946266, 0.00992055, -0.03600283, -0.01684920, -0.00111684, " +
        "-0.00411204, -0.01297130, -0.01723725, -0.01022545, -0.00165306, -0.00313110, -0.01218016, -0.01763266, " +
        "-0.01125620, -0.00231663, -0.01374149, -0.03797620, -0.05142937, -0.03117307, -0.00581914, -0.01064003, " +
        "-0.03608089, -0.05272168, -0.03375670, -0.00795586, 0.09628104, 0.27129991, -0.00353779, -0.01734151, " +
        "-0.03153981, 0.05686230, 0.28500998, 0.02230594, -0.02374955, 0.68214326, 0.05018048, -0.02320852, " +
        "-0.04383616, 0.18459474, 0.71517975, 0.10805613, -0.03263677, -0.03637639, -0.01394373, -0.02511203, " +
        "-0.01728636, 0.05407331, -0.02867568, -0.01893131, -0.00240854, -0.00446511, -0.01636187, -0.02377053, " +
        "-0.01522848, -0.00333334, -0.00819975, -0.02964169, -0.04499287, -0.02745350, -0.00612408, 0.02727416, " +
        "0.19446600, 0.00159832, -0.02232473, 0.74982506, 0.11452620, -0.03348048, -0.01605681, -0.02070339, " +
        "-0.00458223 "
    };

    /// <summary>K.2: the default upsampling weights are the spec's decimals, each rounded to float.</summary>
    [Fact]
    public void DefaultUpsamplingWeightsAreTheSpecs()
    {
        var tables = new[] { JxlUpsampling.Default2, JxlUpsampling.Default4, JxlUpsampling.Default8 };
        for (int t = 0; t < 3; t++)
        {
            var spec = SpecUpWeights[t].Split(',').Select(v => (float)double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Assert.Equal(spec.Length, tables[t].Length);
            var bad = Enumerable.Range(0, spec.Length).Where(i => spec[i] != tables[t][i]).ToArray();
            Assert.True(bad.Length == 0, $"d_up{2 << t}: {bad.Length} weights differ, first at {bad.FirstOrDefault()}");
        }
    }

    // ------------------------------------------------------------------ G.2.4 HF metadata

    static float[] Floats(RawImage r) => r.SampleType == SampleType.UInt8
        ? ((byte[])r.Data).Select(b => b / 255f).ToArray() : r.SampleType == SampleType.UInt16
        ? ((ushort[])r.Data).Select(b => b / 65535f).ToArray() : (float[])r.Data;

    /// <summary>Largest difference of our decode of a lossy file to libjxl's (both clamped to [0, 1]), or null without it.</summary>
    double? LossyDiff(byte[] data, string name)
    {
        Dump(name, data);
        var ours = Floats(Jxl.Decode(data, new JxlDecodeOptions { ForceFloat = true }));
        var lib = JxlEncodeTests.Libjxl(data);
        if (lib == null) return null;
        var e = Floats(lib);
        Assert.Equal(e.Length, ours.Length);
        double m = 0;
        for (int i = 0; i < ours.Length; i++) m = Math.Max(m, Math.Abs(Math.Clamp(ours[i], 0, 1) - Math.Clamp(e[i], 0, 1)));
        output.WriteLine($"{name}: largest difference to libjxl {m:g3}");
        return m;
    }

    /// <summary>
    /// G.2.4: HfMul is 1 + mul with no bounds. libjxl clamps mul to [0, 255]: blocks coded with HfMul 300 .. 100000
    /// decode as HfMul 256 (taking the text literally differs from libjxl by 0.15 / 0.97 here), mul = -1 as HfMul 1.
    /// We follow libjxl.
    /// </summary>
    [Theory]
    [InlineData(300, 700)]
    [InlineData(1, 100000)]
    [InlineData(0, 40)]
    public void HfMulIsClampedAsLibjxl(int a, int b)
    {
        var img = JxlEncodeTests.Synthetic(64, 48, ColorModel.Rgb, SampleType.UInt8, 0);
        var data = Jxl.Encode(img, new JxlEncodeOptions { Distance = 1, ForceStrategy = 0, HfMul = i => i % 5 == 0 ? a : b });
        var m = LossyDiff(data, $"hfmul {a} {b}");
        Assert.True(m == null || m < 2 / 255.0, $"{m}");
    }

    /// <summary>
    /// I.4: the HF histograms have 495 * num_hf_presets * nb_block_ctx distributions, num_hf_presets up to num_groups,
    /// and no limit beyond that. 144 groups with 144 presets and the 15 default block contexts: 1069200 distributions.
    /// </summary>
    [Fact]
    public void ManyHfPresets()
    {
        const int W = 3072, H = 3072;
        var d = new byte[W * H * 3];
        for (int i = 0; i < d.Length; i++) { int p = i / 3; d[i] = (byte)(64 + (p % W) / 24 + (p / W) / 32 + 20 * (i % 3)); }
        var img = new RawImage(W, H, ColorModel.Rgb, d, 3);
        var data = Jxl.Encode(img, new JxlEncodeOptions { Distance = 2, Effort = 3, HfPresets = 144 });
        var m = LossyDiff(data, "hf presets 144");
        Assert.True(m == null || m < 2 / 255.0, $"{m}");
    }

    // ------------------------------------------------------------------ I.2 dequantization matrices

    /// <summary>A RAW (mode 7) encoding of the 8 x 8 table: F16 denominator, then a modular image of the given values.</summary>
    static void RawDct8(JxlBitWriter w, float denominator, int[] perChannel)
    {
        for (int t = 0; t < 17; t++)
        {
            if (t != 0) { w.Write(3, 0); continue; }
            w.Write(3, 7);
            w.WriteF16(denominator);
            var chans = Enumerable.Range(0, 3).Select(c =>
            {
                var ch = new JxlChannel(8, 8, 0, 0);
                for (int i = 0; i < 64; i++) ch.Data[i] = perChannel[c] * (1 + (i % 8 + i / 8) / 4);
                return ch;
            }).ToList();
            var tree = new JxlEncTree();
            tree = tree.Finish(tree.AddLeaf(0));
            var tok = new JxlTokens();
            for (int c = 0; c < 3; c++) JxlModularTokenizer.EncodeChannel(chans, c, tree.Specialize(c, 0), JxlWpHeader.Default, tok);
            var code = new JxlEntropyEncoder(tree.NumLeaves) { MaxClusters = 1 };
            code.Build(new[] { tok }, false);
            JxlTransformEnc.WriteHeader(w, Array.Empty<JxlTransformEnc>(), globalTree: false);
            tree.Write(w);
            code.WriteCode(w);
            code.WriteTokens(w, tok);
        }
    }

    /// <summary>
    /// I.2.4: a RAW matrix is params times params.denominator, with no condition on the denominator; the products have
    /// to be positive. A negative denominator with negative params decodes like the positive pair (which matches
    /// libjxl). libjxl 0.12 refuses the negative denominator; the text does not, and we accept it.
    /// </summary>
    [Fact]
    public void RawQuantTableWithNegativeDenominator()
    {
        var img = JxlEncodeTests.Synthetic(64, 48, ColorModel.Rgb, SampleType.UInt8, 0);
        byte[] Enc(float den, int s) => Jxl.Encode(img, new JxlEncodeOptions
        {
            Distance = 1, ForceStrategy = 0, DequantMatrices = w => RawDct8(w, den, new[] { s, 3 * s, 3 * s }),
        });
        var pos = Enc(1 / 1024f, 1);
        var neg = Enc(-1 / 1024f, -1);
        var m = LossyDiff(pos, "raw quant table");
        Assert.True(m == null || m < 2 / 255.0, $"{m}");
        Assert.Equal((float[])Jxl.Decode(pos, new JxlDecodeOptions { ForceFloat = true }).Data, (float[])Jxl.Decode(neg, new JxlDecodeOptions { ForceFloat = true }).Data);
        // a product that is not positive is refused
        Assert.Throws<ImageFormatException>(() => Jxl.Decode(Enc(-1 / 1024f, 1)));
    }

    /// <summary>Table I.6: DCT128x256 (and DCT256x128), channel B, starts at 12979.84647584004484.</summary>
    [Fact]
    public void Dct128x256BandOfBIsTheSpecs()
    {
        Assert.Equal(12979.84647584004484, JxlQuantEncoding.Default(16).Bands![2][0]);
    }

    /// <summary>I.2.4: in the Hornuss encoding coefficient (0, 0) is 1, (0, 1) and (1, 0) params(c, 1), (1, 1) params(c, 2).</summary>
    [Fact]
    public void HornussWeightAtZeroIsOne()
    {
        var q = JxlQuantEncoding.Default(1);
        for (int c = 0; c < 3; c++)
        {
            var m = q.Matrix(1, c);
            var p = q.Params![c];
            Assert.Equal(1f, m[0]);
            Assert.Equal((float)(1 / p[1]), m[1]);
            Assert.Equal((float)(1 / p[1]), m[8]);
            Assert.Equal((float)(1 / p[2]), m[9]);
            Assert.Equal((float)(1 / p[0]), m[63]);
        }
    }
}
