using Aardvark.ImageCodecs;
using Aardvark.ImageCodecs.JpegXl;
using Xunit;
using Xunit.Abstractions;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// Points of ISO/IEC 18181-1:2024 and 18181-2 the conformance files do not reach, checked on hand-built codestreams
/// (lossless modular float frames with patches, splines or noise written by the test) decoded by us and by libjxl.
/// </summary>
public class JxlSpecTests(ITestOutputHelper output)
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

    /// <summary>A float32 RGB image (sRGB, not XYB) with the given extra channels, coded as lossless modular frames.</summary>
    sealed class Stream
    {
        public readonly JxlImageHeader Header;
        readonly JxlBitWriter w = new();

        static readonly JxlBitDepth Float32 = new() { Float = true, Bits = 32, ExpBits = 8 };

        public Stream(int width, int height, params JxlExtraChannelType[] extra)
            : this(width, height, extra.Select(t => new JxlExtraChannel { Type = t, BitDepth = Float32 }).ToArray()) { }

        public Stream(int width, int height, JxlExtraChannel[] extra)
        {
            Header = new JxlImageHeader
            {
                Width = width, Height = height, BitDepth = Float32, XybEncoded = false, Modular16BitBuffers = false, ExtraChannels = extra,
            };
            JxlHeaderWriter.WriteImageHeader(w, Header);
            w.ZeroPadToByte();
        }

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

        /// <summary>Appends a frame; the planes are colour (3) then extra channels, at frame size before upsampling.</summary>
        public void Frame(JxlFrameHeader fh, Plane[] planes, Action<JxlBitWriter>? features = null)
        {
            var chans = planes.Select((p, k) =>
            {
                int up = k < 3 ? fh.Upsampling : fh.EcUpsampling[k - 3] << Header.ExtraChannels[k - 3].DimShift;
                int shift = System.Numerics.BitOperations.Log2((uint)up) - System.Numerics.BitOperations.Log2((uint)fh.Upsampling);
                var c = new JxlChannel((fh.Width + up - 1) / up, (fh.Height + up - 1) / up, shift, shift);
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

    /// <summary>Our float decode and libjxl's (null without the oracle), both as interleaved floats.</summary>
    (float[] ours, float[]? lib, int channels) DecodeBoth(byte[] data, string name)
    {
        var ours = Jxl.Decode(data, new JxlDecodeOptions { ApplyOrientation = false });
        Assert.Equal(SampleType.Float32, ours.SampleType);
        var lib = JxlEncodeTests.Libjxl(data);
        if (lib == null) output.WriteLine($"{name}: libjxl not available, decoded by us only");
        else Assert.Equal((ours.Width, ours.Height, ours.Channels), (lib.Width, lib.Height, lib.Channels));
        return ((float[])ours.Data, (float[]?)lib?.Data, ours.Channels);
    }

    void AssertClose(byte[] data, string name, double tol)
    {
        if (Environment.GetEnvironmentVariable("PIXCODEC_JXLSPEC_DUMP") is string dump) File.WriteAllBytes(Path.Combine(dump, name.Replace(" ", "_").Replace(",", "") + ".jxl"), data);
        var (ours, lib, nc) = DecodeBoth(data, name);
        if (lib == null) return;
        double m = 0;
        for (int i = 0; i < ours.Length; i++) m = Math.Max(m, Math.Abs(ours[i] - lib[i]));
        output.WriteLine($"{name}: largest difference to libjxl {m:g3} ({nc} channels)");
        Assert.True(m <= tol, $"{name}: {m} > {tol}");
    }

    // ------------------------------------------------------------------ L.4 extra channel upsampling

    /// <summary>K.2 taken literally: k x k upsampling with the default weights, 5 x 5 windows mirrored, clamped to their range.</summary>
    static float[] UpsampleReference(float[] src, int w, int h, int k, int ow, int oh)
    {
        var weights = k == 2 ? JxlUpsampling.Default2 : k == 4 ? JxlUpsampling.Default4 : JxlUpsampling.Default8;
        int M(int c, int n) { while (c < 0 || c >= n) c = c < 0 ? -c - 1 : 2 * n - 1 - c; return c; }
        var o = new float[ow * oh];
        for (int oy = 0; oy < oh; oy++)
            for (int ox = 0; ox < ow; ox++)
            {
                int px = ox / k, py = oy / k, kx = ox % k, ky = oy % k;
                double sum = 0, lo = double.MaxValue, hi = double.MinValue;
                for (int iy = 0; iy < 5; iy++)
                    for (int ix = 0; ix < 5; ix++)
                    {
                        double v = src[M(py + iy - 2, h) * w + M(px + ix - 2, w)];
                        lo = Math.Min(lo, v); hi = Math.Max(hi, v);
                        int j = ky < k / 2 ? iy + 5 * ky : 4 - iy + 5 * (k - 1 - ky);
                        int i = kx < k / 2 ? ix + 5 * kx : 4 - ix + 5 * (k - 1 - kx);
                        int a = Math.Min(i, j), b = Math.Max(i, j);
                        sum += weights[5 * k * a / 2 - a * (a - 1) / 2 + b - a] * v;
                    }
                o[oy * ow + ox] = (float)Math.Clamp(sum, lo, hi);
            }
        return o;
    }

    /// <summary>
    /// L.4: an extra channel upsampled by f = ec_upsampling &lt;&lt; dim_shift, beyond 8 by 8 first, then by f / 8. libjxl
    /// 0.12 decodes up to f = 8 (compared with it) and refuses more (compared with K.2 taken literally).
    /// </summary>
    [Theory]
    [InlineData(1, 4)]
    [InlineData(3, 1)]
    [InlineData(0, 8)]
    [InlineData(3, 2)]
    [InlineData(4, 1)]
    [InlineData(2, 8)]
    public void ExtraChannelUpsampling(int dimShift, int ecUpsampling)
    {
        const int W = 150, H = 110;
        int f = ecUpsampling << dimShift;
        var s = new Stream(W, H, new[] { new JxlExtraChannel { Type = JxlExtraChannelType.Alpha, BitDepth = new JxlBitDepth { Float = true, Bits = 32, ExpBits = 8 }, DimShift = dimShift } });
        var fr = s.NewFrame(W, H);
        fr.IsLast = true; fr.EcUpsampling[0] = ecUpsampling;
        int aw = (W + f - 1) / f, ah = (H + f - 1) / f;
        var alpha = new Plane(aw, ah, (x, y) => Rand(9, x, y));
        s.Frame(fr, Planes(W, H, 3, 2).Append(alpha).ToArray());
        if (f <= 8) { AssertClose(s.ToArray(), $"extra channel upsampled {f}x", 1e-5); return; }
        var ours = (float[])Jxl.Decode(s.ToArray()).Data;
        int rest = f / 8, iw = Math.Min(8 * aw, (W + rest - 1) / rest), ih = Math.Min(8 * ah, (H + rest - 1) / rest);
        var expected = UpsampleReference(UpsampleReference(alpha.V, aw, ah, 8, iw, ih), iw, ih, rest, W, H);
        double m = 0;
        for (int i = 0; i < W * H; i++) m = Math.Max(m, Math.Abs(ours[i * 4 + 3] - expected[i]));
        output.WriteLine($"extra channel upsampled {f}x: largest difference to K.2 / L.4 taken literally {m:g3}");
        Assert.True(m < 1e-5, $"{m}");
    }

    // ------------------------------------------------------------------ K.3 patches

    /// <summary>
    /// A reference frame, then a frame drawing patches from it with every blend mode (K.3.1, Table K.1), with and
    /// without clamping, on colour and on the extra channels.
    /// </summary>
    static byte[] PatchStream(JxlExtraChannelType[] extra, int alphaIndex, Func<int, bool> modeOk)
    {
        const int W = 48, H = 40;
        int ne = extra.Length;
        var s = new Stream(W, H, extra);
        var r = s.NewFrame(W, H);
        r.Type = JxlFrameType.ReferenceOnly; r.IsLast = false; r.SaveAsReference = 1; r.SaveBeforeColourTransform = true;   // libjxl wants that for patches
        // reference: values beyond [0, 1] too, so clamping matters
        s.Frame(r, Enumerable.Range(0, 3 + ne).Select(c => new Plane(W, H, (x, y) => Rand(7 + c, x, y) * 1.6f - 0.3f)).ToArray());
        var f = s.NewFrame(W, H);
        f.Flags = JxlFrameHeader.FlagPatches;
        s.Frame(f, Planes(W, H, 3 + ne, 20), bw =>
        {
            var t = new Tokens(10);
            var modes = Enumerable.Range(0, 8).Where(modeOk).ToArray();
            t.Add(0, modes.Length * 2);   // patch types
            for (int k = 0; k < modes.Length * 2; k++)
            {
                int mode = modes[k / 2];
                bool clamp = (k & 1) != 0;
                t.Add(1, 1);                       // reference 1
                t.Add(3, 3 + k); t.Add(3, 2 + k);  // x0, y0
                t.Add(2, 5 - 1); t.Add(2, 4 - 1);  // 5 x 4
                t.Add(7, 2 - 1);                   // two positions
                for (int j = 0; j < 2; j++)
                {
                    if (j == 0) { t.Add(4, k * 2 % 20); t.Add(4, k % 5 * 7); }
                    else { t.AddSigned(6, 20); t.AddSigned(6, 3 - k % 3); }
                    for (int c = 0; c <= ne; c++)
                    {
                        // colour gets the mode, extra channels cycle through the modes too
                        int m = c == 0 ? mode : modes[(k + c) % modes.Length];
                        t.Add(5, m);
                        if (m > 3 && ne > 1) t.Add(8, alphaIndex);
                        if (m > 2) t.Add(9, clamp ? 1 : 0);
                    }
                }
            }
            t.WriteTo(bw);
        });
        return s.ToArray();
    }

    [Fact]
    public void PatchesBlendLikeLibjxl()
    {
        var a = PatchStream(new[] { JxlExtraChannelType.Alpha }, 0, _ => true);
        AssertClose(a, "patches, alpha", 1e-4);
        var b = PatchStream(new[] { JxlExtraChannelType.Alpha, JxlExtraChannelType.Alpha }, 1, _ => true);
        AssertClose(b, "patches, two alpha channels", 1e-4);
    }

    /// <summary>
    /// K.3.1 reads a patch's alpha_channel when its mode uses alpha and "there is more than 1 alpha channel"; libjxl (and
    /// we) read it whenever there is more than one extra channel. This stream has one alpha channel and a depth channel
    /// and carries the index; a decoder following the letter of the text would lose sync.
    /// </summary>
    [Fact]
    public void PatchAlphaChannelIsReadWithSeveralExtraChannels()
    {
        var d = PatchStream(new[] { JxlExtraChannelType.Depth, JxlExtraChannelType.Alpha }, 1, _ => true);
        AssertClose(d, "patches, depth + alpha", 1e-4);
    }

    // ------------------------------------------------------------------ Annex M limits

    /// <summary>
    /// Table M.1: a local MA tree may have 1024 + (samples of its stream) nodes, more than the global tree limit for small
    /// frames. A 64 x 64 grey frame whose GlobalModular stream has its own tree of 2047 nodes.
    /// </summary>
    [Fact]
    public void LocalTreeMayExceedTheGlobalLimit()
    {
        const int W = 64, H = 64;
        var h = new JxlImageHeader { Width = W, Height = H, XybEncoded = false, Colour = new JxlColourEncoding { ColourSpace = JxlColourSpace.Grey } };
        var cs = new JxlBitWriter();
        JxlHeaderWriter.WriteImageHeader(cs, h);
        cs.ZeroPadToByte();
        var fh = new JxlFrameHeader { Modular = true, Width = W, Height = H, Filter = new JxlRestorationFilter { Gab = false, EpfIters = 0 } };
        var tree = new JxlEncTree();
        int Build(int depth, int lo, int hi, int lo2, int hi2)
        {
            if (depth == 10) return tree.AddLeaf(depth % 3 == 0 ? 5 : 1);
            bool onX = (depth & 1) == 0;
            int a = onX ? lo : lo2, b = onX ? hi : hi2, mid = (a + b) / 2;
            int left = onX ? Build(depth + 1, mid + 1, hi, lo2, hi2) : Build(depth + 1, lo, hi, mid + 1, hi2);
            int right = onX ? Build(depth + 1, lo, mid, lo2, hi2) : Build(depth + 1, lo, hi, lo2, mid);
            return tree.AddSplit(onX ? 3 : 2, mid, left, right);   // properties 3 = x, 2 = y; left when above
        }
        tree = tree.Finish(Build(0, 0, W - 1, 0, H - 1));
        Assert.Equal(2047, tree.Prop.Count);
        var chans = new List<JxlChannel> { new JxlChannel(W, H, 0, 0) };
        for (int i = 0; i < W * H; i++) chans[0].Data[i] = (int)(Rand(4, i % W, i / W) * 255);
        var tok = new JxlTokens();
        JxlModularTokenizer.EncodeChannel(chans, 0, tree.Specialize(0, 0), JxlWpHeader.Default, tok);
        var code = new JxlEntropyEncoder(tree.NumLeaves) { MaxClusters = 8 };
        code.Build(new[] { tok }, false);
        var lf = new JxlBitWriter();
        lf.WriteBool(true);    // default LF dequantization
        lf.WriteBool(false);   // no global tree
        JxlTransformEnc.WriteHeader(lf, Array.Empty<JxlTransformEnc>(), globalTree: false);
        tree.Write(lf);
        code.WriteCode(lf);
        code.WriteTokens(lf, tok);
        JxlLosslessEncoder.WriteFrame(cs, fh, h, new List<byte[]> { lf.ToArray() });
        var data = cs.ToArray();

        var img = Jxl.Decode(data);
        Assert.Equal(chans[0].Data, ((byte[])img.Data).Select(b => (int)b));
        var lib = JxlEncodeTests.Libjxl(data);
        if (lib != null) Assert.Equal((byte[])img.Data, (byte[])lib.Data);
    }

    /// <summary>Table M.1: at most min(2^24, frame pixels / 16) patches.</summary>
    [Fact]
    public void TooManyPatchesAreRejected()
    {
        byte[] Dictionary(int n)
        {
            var w = new JxlBitWriter();
            var t = new Tokens(10);
            t.Add(0, n);
            for (int i = 0; i < n; i++)
            {
                t.Add(1, 0); t.Add(3, 0); t.Add(3, 0); t.Add(2, 0); t.Add(2, 0); t.Add(7, 0); t.Add(4, 0); t.Add(4, 0); t.Add(5, 1);
            }
            t.WriteTo(w);
            w.ZeroPadToByte();
            return w.ToArray();
        }
        Assert.Equal(256, JxlPatches.Read(new JxlBitReader(Dictionary(256)), 0, 64 * 64).Items.Count);
        Assert.Throws<ImageFormatException>(() => JxlPatches.Read(new JxlBitReader(Dictionary(257)), 0, 64 * 64));
        Assert.Equal(5625, JxlPatches.Read(new JxlBitReader(Dictionary(5625)), 0, 300 * 300).Items.Count);
        Assert.Throws<ImageFormatException>(() => JxlPatches.Read(new JxlBitReader(Dictionary(5626)), 0, 300 * 300));
    }

    /// <summary>
    /// K.4.1 / Table M.1: splines whose estimated area exceeds min(1024 w h + 2^32, 2^42) are beyond level 10 and are
    /// rejected before they are drawn (libjxl rejects them too).
    /// </summary>
    [Fact]
    public void SplineAreaBeyondLevel10IsRejected()
    {
        const int W = 64, H = 48;
        byte[] Stream(int sigma)
        {
            var s = new Stream(W, H);
            var f = s.NewFrame(W, H);
            f.IsLast = true; f.Flags = JxlFrameHeader.FlagSplines;
            s.Frame(f, Planes(W, H, 3, 1), bw =>
            {
                var t = new Tokens(6);
                t.Add(2, 0); t.Add(1, 4); t.Add(1, 4); t.AddSigned(0, 0);
                t.Add(3, 2);
                foreach (int d in new[] { 25, 10, 0, 15 }) t.AddSigned(4, d);
                for (int c = 0; c < 4; c++)
                    for (int k = 0; k < 32; k++) t.AddSigned(5, c == 3 ? sigma : k == 0 ? 10 : 0);
                t.WriteTo(bw);
            });
            return s.ToArray();
        }
        // 32 sigma weights of 3000^2, colour log 5, Manhattan distance 85: 1.2e11 > 1024 * 64 * 48 + 2^32; sigma 30 is far below
        Jxl.Decode(Stream(30));
        var big = Stream(3000);
        Assert.Throws<ImageFormatException>(() => Jxl.Decode(big));
        if (PythonAvailable)
        {
            var tmp = Path.Combine(FixtureDir, "_tmp");
            Directory.CreateDirectory(tmp);
            var f = Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".jxl");
            try
            {
                File.WriteAllBytes(f, big);
                var (exit, text) = RunPython($"decode_to_ref.py \"{f}\" \"{f}.ref\"");
                if (!CannotVerify(exit, text)) Assert.NotEqual(0, exit);
            }
            finally { File.Delete(f); File.Delete(f + ".ref"); }
        }
    }

    // ------------------------------------------------------------------ K.5 noise

    static readonly int[] NoiseLut = { 80, 150, 260, 330, 420, 610, 700, 1010 };

    static void WriteNoise(JxlBitWriter w) { foreach (int v in NoiseLut) w.Write(10, (uint)v); }

    /// <summary>
    /// K.5: noise generated per group from seeds made of the visible / invisible frame counts and the group position,
    /// high-pass filtered over the whole frame, scaled by the LUT at the sample's intensity. Several groups, frames that
    /// are not shown before the shown one, a reference frame, upsampling.
    /// </summary>
    [Theory]
    [InlineData(300, 280, 1, false)]
    [InlineData(300, 280, 2, false)]
    [InlineData(200, 136, 1, true)]
    [InlineData(300, 280, 2, true)]
    public void NoiseMatchesLibjxl(int w, int h, int upsampling, bool frames)
    {
        var s = new Stream(w, h);
        int cw = (w + upsampling - 1) / upsampling, ch = (h + upsampling - 1) / upsampling;
        if (frames)
        {
            var r = s.NewFrame(w, h);
            r.Type = JxlFrameType.ReferenceOnly; r.IsLast = false; r.SaveAsReference = 2;
            s.Frame(r, Planes(w, h, 3, 5));
            var a = s.NewFrame(w, h);   // composited, not shown: invisible
            a.IsLast = false; a.Flags = JxlFrameHeader.FlagNoise; a.Upsampling = upsampling;
            s.Frame(a, Planes(cw, ch, 3, 9), WriteNoise);
            var b = s.NewFrame(w, h);
            b.IsLast = true; b.Flags = JxlFrameHeader.FlagNoise; b.Blending = new JxlBlendingInfo { Mode = JxlBlendMode.Add };
            s.Frame(b, Enumerable.Range(0, 3).Select(c => new Plane(w, h, (x, y) => 0.1f * Rand(c, x, y))).ToArray(), WriteNoise);
        }
        else
        {
            var f = s.NewFrame(w, h);
            f.IsLast = true; f.Flags = JxlFrameHeader.FlagNoise; f.Upsampling = upsampling;
            // intensities across the whole LUT range: (G + R) / 2 and (G - R) / 2 up to 7 / 6 and beyond
            s.Frame(f, new[]
            {
                new Plane(cw, ch, (x, y) => 0.3f * Rand(1, x, y)),
                new Plane(cw, ch, (x, y) => 2.6f * x / cw),
                new Plane(cw, ch, (x, y) => Rand(3, x, y)),
            }, WriteNoise);
        }
        AssertClose(s.ToArray(), $"noise {w}x{h} up {upsampling}{(frames ? " frames" : "")}", 2e-5);
    }

    /// <summary>K.5.2: the strength is LUT[6] .. LUT[7] interpolated up to 7, LUT[7] from there on, LUT[0] below 0.</summary>
    [Fact]
    public void NoiseStrengthEnds()
    {
        var n = new JxlNoise();
        for (int i = 0; i < 8; i++) n.Lut[i] = (i + 1) / 10f;
        Assert.Equal(0.8f, n.Strength(7 / 6f), 6);
        Assert.Equal(0.8f, n.Strength(5), 6);
        Assert.Equal(0.75f, n.Strength(6.5f / 6), 6);
        Assert.Equal(0.1f, n.Strength(-1), 6);
        Assert.Equal(0.1f, n.Strength(float.NaN), 6);
    }

    // ------------------------------------------------------------------ K.4 splines

    /// <summary>Splines (K.4.1 coding, K.4.2 Catmull-Rom with the Gaussian brush) on a modular frame.</summary>
    static void WriteSplines(JxlBitWriter w, int quantAdjust, (int x, int y)[][] splines, int seed)
    {
        var t = new Tokens(6);
        t.Add(2, splines.Length - 1);
        for (int i = 0; i < splines.Length; i++)
        {
            var p = splines[i][0];
            if (i == 0) { t.Add(1, p.x); t.Add(1, p.y); }
            else { t.AddSigned(1, p.x - splines[i - 1][0].x); t.AddSigned(1, p.y - splines[i - 1][0].y); }
        }
        t.AddSigned(0, quantAdjust);
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
                for (int k = 0; k < 32; k++)
                {
                    int v = k == 0 ? (c == 3 ? 6 : 14 + 3 * c) : k < 6 ? rng.Next(-4, 5) : 0;
                    t.AddSigned(5, v);
                }
        }
        t.WriteTo(w);
    }

    /// <summary>
    /// Splines (K.4.1 coding, K.4.2 rendering) against K.4.2 taken literally. libjxl parses the same splines but cuts the
    /// brush off at a few sigma; it differs from the literal rendering by up to ~0.07 here (where sigma gets small along
    /// the third spline), we by ~2e-6.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-6)]
    public void SplinesFollowK42(int quantAdjust)
    {
        const int W = 96, H = 80;
        var s = new Stream(W, H);
        var f = s.NewFrame(W, H);
        f.IsLast = true; f.Flags = JxlFrameHeader.FlagSplines;
        var splines = new[]
        {
            new[] { (10, 12), (40, 20), (70, 60), (85, 30) },
            new[] { (50, 70) },
            new[] { (5, 70), (30, 50), (31, 52), (60, 75), (90, 70) },
        };
        var planes = Enumerable.Range(0, 3).Select(c => new Plane(W, H, (x, y) => 0.2f + 0.3f * Rand(c, x, y))).ToArray();
        s.Frame(f, planes, bw => WriteSplines(bw, quantAdjust, splines, 3));
        var refPlanes = planes.Select(p => p.V.Select(v => (double)v).ToArray()).ToArray();
        SplinesReference(refPlanes, W, H, quantAdjust, splines, 3);
        var (ours, lib, nc) = DecodeBoth(s.ToArray(), "splines");
        double mo = 0, ml = 0;
        for (int i = 0; i < ours.Length; i++)
        {
            double r = refPlanes[i % 3][i / 3];
            mo = Math.Max(mo, Math.Abs(ours[i] - r));
            if (lib != null) ml = Math.Max(ml, Math.Abs(lib[i] - r));
        }
        output.WriteLine($"splines qa {quantAdjust}: largest difference to K.4.2 taken literally: ours {mo:g3}, libjxl {ml:g3}");
        Assert.True(mo < 2e-5, $"ours {mo}");
        Assert.True(ml < 0.1, $"libjxl {ml}");
    }

    static double Erf(double x)
    {
        // series for small |x|, continued fraction for erfc otherwise (double precision)
        double ax = Math.Abs(x);
        if (ax < 2.5)
        {
            double sum = ax, term = ax, x2 = ax * ax;
            for (int n = 1; n < 200; n++) { term *= -x2 / n; double add = term / (2 * n + 1); sum += add; if (Math.Abs(add) < 1e-17) break; }
            return Math.Sign(x) * 2 / Math.Sqrt(Math.PI) * sum;
        }
        double f = 0;
        for (int n = 60; n >= 1; n--) f = n / 2.0 / (ax + f);
        double erfc = Math.Exp(-ax * ax) / Math.Sqrt(Math.PI) / (ax + f);
        return Math.Sign(x) * (1 - erfc);
    }

    /// <summary>The spline rendering of K.4.2 taken literally (all pixels, double precision), added to the planes.</summary>
    static void SplinesReference(double[][] planes, int w, int h, int quantAdjust, (int x, int y)[][] splines, int seed, double corrB = 1)
    {
        double qa = quantAdjust >= 0 ? 1 + quantAdjust / 8.0 : 1 / (1 - quantAdjust / 8.0);
        var rng = new Random(seed);
        double[] mul = { 0.0042, 0.075, 0.07, 0.3333 };
        foreach (var sp in splines)
        {
            var q = new double[4][];
            for (int c = 0; c < 4; c++)
            {
                q[c] = new double[32];
                for (int k = 0; k < 32; k++) q[c][k] = k == 0 ? (c == 3 ? 6 : 14 + 3 * c) : k < 6 ? rng.Next(-4, 5) : 0;
            }
            var dct = new double[4][];
            for (int c = 0; c < 4; c++) { dct[c] = new double[32]; for (int k = 0; k < 32; k++) dct[c][k] = q[c][k] * mul[c] / qa; }
            for (int k = 0; k < 32; k++) dct[2][k] += corrB * dct[1][k];   // base correlations of a modular frame: x 0, b 1
            double Idct(double[] d, double t) { double r = d[0]; for (int k = 1; k < 32; k++) r += Math.Sqrt(2) * d[k] * Math.Cos(k * (Math.PI / 32) * (t + 0.5)); return r; }
            var S = sp.Select(p => ((double)p.x, (double)p.y)).ToList();
            var up = new List<(double x, double y)>();
            if (S.Count == 1) up.Add(S[0]);
            else
            {
                var ext = new List<(double x, double y)> { (2 * S[0].Item1 - S[1].Item1, 2 * S[0].Item2 - S[1].Item2) };
                ext.AddRange(S);
                ext.Add((2 * S[^1].Item1 - S[^2].Item1, 2 * S[^1].Item2 - S[^2].Item2));
                for (int i = 0; i + 3 < ext.Count; i++)
                {
                    var pp = ext.GetRange(i, 4);
                    up.Add(pp[1]);
                    var t = new double[4];
                    for (int k = 1; k < 4; k++) t[k] = t[k - 1] + Math.Pow(Math.Pow(pp[k].x - pp[k - 1].x, 2) + Math.Pow(pp[k].y - pp[k - 1].y, 2), 0.25);
                    for (int step = 1; step < 16; step++)
                    {
                        double knot = t[1] + step / 16.0 * (t[2] - t[1]);
                        var A = new (double x, double y)[3];
                        for (int k = 0; k < 3; k++) { double f = (knot - t[k]) / (t[k + 1] - t[k]); A[k] = (pp[k].x + f * (pp[k + 1].x - pp[k].x), pp[k].y + f * (pp[k + 1].y - pp[k].y)); }
                        var B = new (double x, double y)[2];
                        for (int k = 0; k < 2; k++) { double f = (knot - t[k]) / (t[k + 2] - t[k]); B[k] = (A[k].x + f * (A[k + 1].x - A[k].x), A[k].y + f * (A[k + 1].y - A[k].y)); }
                        double g = (knot - t[1]) / (t[2] - t[1]);
                        up.Add((B[0].x + g * (B[1].x - B[0].x), B[0].y + g * (B[1].y - B[0].y)));
                    }
                }
                up.Add(S[^1]);
            }
            var all = new List<((double x, double y) p, double len)>();
            var cur = up[0]; int next = 0;
            all.Add((cur, 1));
            while (next < up.Count)
            {
                var prev = cur; double len = 0;
                while (true)
                {
                    if (next == up.Count) { all.Add((prev, len)); break; }
                    double toNext = Math.Sqrt(Math.Pow(up[next].x - prev.x, 2) + Math.Pow(up[next].y - prev.y, 2));
                    if (len + toNext >= 1)
                    {
                        double f = (1 - len) / toNext;
                        cur = (prev.x + f * (up[next].x - prev.x), prev.y + f * (up[next].y - prev.y));
                        all.Add((cur, 1));
                        break;
                    }
                    len += toNext; prev = up[next]; ++next;
                }
            }
            double arc = all.Count - 2 + all[^1].len;
            if (!(arc > 0)) continue;   // a single point: K.4.2 divides by zero, nothing is drawn
            for (int i = 0; i < all.Count; i++)
            {
                double a = Math.Min(1.0, i / arc);
                var (p, d) = all[i];
                double sigma = Idct(dct[3], 31 * a);
                var v = Enumerable.Range(0, 3).Select(c => Idct(dct[c], 31 * a)).ToArray();
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        double dist = Math.Sqrt((x - p.x) * (x - p.x) + (y - p.y) * (y - p.y));
                        double fac = Erf((dist / 2 + Math.Sqrt(1 / 8.0)) / sigma) - Erf((dist / 2 - Math.Sqrt(1 / 8.0)) / sigma);
                        for (int c = 0; c < 3; c++) planes[c][y * w + x] += v[c] * d * sigma * fac * fac / 4;
                    }
            }
        }
    }

    // ------------------------------------------------------------------ 18181-2:2026 gain map box

    internal static byte[] Box(string type, byte[] body)
    {
        var b = new byte[8 + body.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
        for (int i = 0; i < 4; i++) b[4 + i] = (byte)type[i];
        body.CopyTo(b, 8);
        return b;
    }

    /// <summary>A jhgm box (18181-2:2026 9.11): version, ISO 21496-1 metadata, alternate colour encoding and ICC, gain map.</summary>
    internal static byte[] GainMapBox(byte[] meta, JxlColourEncoding? ce, byte[]? icc, byte[] gainMap)
    {
        var ms = new MemoryStream();
        ms.WriteByte(0);
        ms.WriteByte((byte)(meta.Length >> 8)); ms.WriteByte((byte)meta.Length);
        ms.Write(meta);
        byte[] ceBytes = Array.Empty<byte>();
        if (ce != null) { var w = new JxlBitWriter(); JxlHeaderWriter.WriteColourEncoding(w, ce); w.ZeroPadToByte(); ceBytes = w.ToArray(); }
        ms.WriteByte((byte)ceBytes.Length);
        ms.Write(ceBytes);
        byte[] iccBytes = Array.Empty<byte>();
        if (icc != null) { var w = new JxlBitWriter(); JxlIccWriter.Write(w, icc); w.ZeroPadToByte(); iccBytes = w.ToArray(); }
        var n = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(n, (uint)iccBytes.Length);
        ms.Write(n);
        ms.Write(iccBytes);
        ms.Write(gainMap);
        return Box("jhgm", ms.ToArray());
    }

    [Fact]
    public void GainMapBoxIsRead()
    {
        var baseline = JxlEncodeTests.Synthetic(40, 30, ColorModel.Rgb, SampleType.UInt8, 0);
        var gain = JxlEncodeTests.Synthetic(20, 15, ColorModel.Gray, SampleType.UInt8, 4);
        var gainFile = Jxl.Encode(gain, new JxlEncodeOptions { Lossless = true });
        var a98 = File.ReadAllBytes(Fixture("icc/ours/a98.icc"));
        var meta = Enumerable.Range(0, 37).Select(i => (byte)(i * 7)).ToArray();
        byte[] File_(byte[] jhgm) => JxlContainer.Signature.ToArray()
            .Concat(Box("ftyp", "jxl \0\0\0\0jxl "u8.ToArray()))
            .Concat(Box("jxlc", Jxl.Encode(baseline, new JxlEncodeOptions { Lossless = true })))
            .Concat(jhgm).ToArray();

        // alternate colour space as an encoding: PQ, BT.2100 primaries
        var pq = new JxlColourEncoding { Primaries = JxlPrimaries.Bt2100, Transfer = JxlTransfer.Pq };
        var f1 = File_(GainMapBox(meta, pq, null, gainFile));
        JxlEncodeTests.AssertExact(baseline, Jxl.Decode(f1));
        var g = Jxl.GetGainMap(f1)!;
        Assert.Equal((0, true, TransferFunction.Pq), (g.Version, g.HasAlternateColourSpace, g.AlternateTransfer));
        Assert.Equal(meta, g.Metadata);
        Assert.Equal(new IccCicp(9, 16, 0, true), IccProfile.Parse(g.AlternateIcc!).Cicp);   // PQ and the primaries in a cicp tag
        JxlEncodeTests.AssertExact(gain, g.DecodeImage());
        Assert.True(ImageCodec.ReadMetadata(f1).HasGainMap);
        var p3 = new JxlColourEncoding { Primaries = JxlPrimaries.P3, Transfer = JxlTransfer.Gamma, Gamma = 0.5 };
        g = Jxl.GetGainMap(File_(GainMapBox(meta, p3, null, gainFile)))!;
        Assert.Equal(TransferFunction.Gamma, g.AlternateTransfer);
        Assert.NotNull(g.AlternateIcc);   // described by a profile built from the encoding

        // as an ICC profile
        var f2 = File_(GainMapBox(meta, new JxlColourEncoding { WantIcc = true }, a98, gainFile));
        g = Jxl.GetGainMap(f2)!;
        Assert.Equal((true, TransferFunction.Unknown), (g.HasAlternateColourSpace, g.AlternateTransfer));
        Assert.Equal(a98, g.AlternateIcc);

        // the baseline's colour space
        var f3 = File_(GainMapBox(meta, null, null, gainFile));
        g = Jxl.GetGainMap(f3)!;
        Assert.False(g.HasAlternateColourSpace);
        Assert.Null(g.AlternateIcc);
        JxlEncodeTests.AssertExact(gain, g.DecodeImage());

        // none, an unknown version, damage
        var plain = Jxl.Encode(baseline, new JxlEncodeOptions { Lossless = true, ForceContainer = true });
        Assert.Null(Jxl.GetGainMap(plain));
        Assert.False(ImageCodec.ReadMetadata(plain).HasGainMap);
        var v1 = GainMapBox(meta, null, null, gainFile); v1[8] = 1;
        Assert.Null(Jxl.GetGainMap(File_(v1)));
        Assert.Throws<ImageFormatException>(() => Jxl.GetGainMap(File_(GainMapBox(meta, pq, null, gainFile)[..30])));
    }

    // ------------------------------------------------------------------ HDR encoding

    /// <summary>
    /// Lossy encoding of PQ and HLG images: XYB holds display light in units of 255 nits (L.2.2), so PQ samples are
    /// scaled from 10000 nits, HLG goes through its OOTF (E.6 NOTE 4) and both from BT.2100 primaries to sRGB; the
    /// intensity target defaults to 10000 / 1000 nits. Round trips through us and through libjxl.
    /// </summary>
    [Theory]
    [InlineData(TransferFunction.Hlg, 0.9f, 0.08)]
    [InlineData(TransferFunction.Pq, 0.3f, 0.05)]
    public void HdrLossyRoundTrips(TransferFunction tf, float peak, double tol)
    {
        int w = 64, h = 48;
        var d = new float[w * h * 3];
        for (int i = 0; i < d.Length; i++)
        {
            int p = i / 3, c = i % 3;
            d[i] = (float)(0.05 + (peak - 0.05) * (0.5 + 0.5 * Math.Sin(p % w * 0.07 + c) * Math.Cos(p / w * 0.05 - c)));
        }
        var enc = Jxl.Encode(new RawImage(w, h, ColorModel.Rgb, d, 3, new ImageMetadata { Transfer = tf }), new JxlEncodeOptions { Distance = 0.3f });
        var hdr = JxlImageHeader.Read(new JxlBitReader(JxlContainer.Parse(enc).Codestream(enc)));
        Assert.Equal(tf == TransferFunction.Pq ? 10000 : 1000, hdr.ToneMapping.IntensityTarget);
        var ours = Jxl.Decode(enc);
        Assert.Equal(tf, ours.Metadata?.Transfer);
        double Max(float[] o) { double m = 0; for (int i = 0; i < d.Length; i++) m = Math.Max(m, Math.Abs(o[i] - d[i])); return m; }
        double mo = Max((float[])ours.Data);
        var lib = JxlEncodeTests.Libjxl(enc);
        double ml = lib == null ? 0 : Max((float[])lib.Data);
        output.WriteLine($"{tf}: round trip through us {mo:g3}, through libjxl {ml:g3}");
        Assert.True(mo < tol && ml < tol, $"{mo} {ml}");
    }

    // ------------------------------------------------------------------ I.2.4 dequantization matrices

    /// <summary>
    /// Table I.5: the Hornuss, DCT2, DCT4, DCT4x8 and AFV encodings may describe any of the 8 x 8 tables (indices 0, 1,
    /// 2, 3, 9, 10), not only the one of the same name. Frames of one transform type whose table uses another table's
    /// encoding, decoded by us and by libjxl.
    /// </summary>
    [Theory]
    [InlineData(0, 3)]    // DCT8x8 table as DCT4
    [InlineData(0, 1)]    // DCT8x8 table as Hornuss
    [InlineData(12, 2)]   // DCT4x8 table as DCT2
    [InlineData(13, 5)]   // DCT8x4 table as AFV
    [InlineData(3, 5)]    // DCT4x4 table as AFV
    public void EightByEightEncodingsOnOtherTables(int strategy, int mode)
    {
        int table = JxlAcStrategy.QuantTable[strategy];
        void Write(JxlBitWriter w)
        {
            void F(params double[] v) { foreach (var x in v) w.WriteF16((float)x); }
            void DctParams(int n) { w.Write(4, (uint)(n - 1)); for (int c = 0; c < 3; c++) { F(2000 / (c + 1.0)); for (int i = 1; i < n; i++) F(-0.3 * i / n); } }
            for (int t = 0; t < 17; t++)
            {
                if (t != table) { w.Write(3, 0); continue; }   // library
                w.Write(3, (uint)mode);
                for (int c = 0; c < 3; c++)
                    switch (mode)
                    {
                        case 1: F(40, 20, 30); break;                        // Hornuss (x 64)
                        case 2: F(20, 25, 30, 35, 40, 45); break;            // DCT2 (x 64)
                        case 3: F(1.5, 0.8); break;                          // DCT4
                        case 4: F(1.2); break;                               // DCT4x8
                        default: F(1500, 1500, 120, 120, 120, 200, -0.2, -0.2, -0.3); break;   // AFV
                    }
                if (mode is 3 or 4 or 5) DctParams(4);
                if (mode == 5) DctParams(3);
            }
        }
        var img = JxlEncodeTests.Synthetic(64, 48, ColorModel.Rgb, SampleType.UInt8, 0);
        var data = Jxl.Encode(img, new JxlEncodeOptions { Distance = 1, ForceStrategy = strategy, DequantMatrices = Write });
        var ours = Jxl.Decode(data, new JxlDecodeOptions { ForceFloat = true });
        var lib = JxlEncodeTests.Libjxl(data);
        if (lib == null) return;
        var a = (float[])ours.Data; var e = JxlTests_Floats(lib);
        double m = 0;
        for (int i = 0; i < a.Length; i++) m = Math.Max(m, Math.Abs(Math.Clamp(a[i], 0, 1) - Math.Clamp(e[i], 0, 1)));
        output.WriteLine($"strategy {strategy}, table {table} as mode {mode}: largest difference to libjxl {m:g3}");
        Assert.True(m < 2 / 255.0, $"{m}");
    }

    static float[] JxlTests_Floats(RawImage r) => r.SampleType == SampleType.UInt8
        ? ((byte[])r.Data).Select(b => b / 255f).ToArray() : r.SampleType == SampleType.UInt16
        ? ((ushort[])r.Data).Select(b => b / 65535f).ToArray() : (float[])r.Data;

    // ------------------------------------------------------------------ fixtures of gen_jxl_spec.py

    static byte[] SpecFixture(string name) => File.ReadAllBytes(Path.Combine(FixtureDir, "jxl-spec", name));

    /// <summary>E.4.4: after an rXYZ / gXYZ / bXYZ run the next tag start is predicted from the rXYZ tag.</summary>
    [Fact]
    public void IccTagStartAfterXyzRun()
    {
        var img = Jxl.Decode(SpecFixture("icc_xyz_shared.jxl"));
        Assert.Equal(SpecFixture("icc_xyz_shared.icc"), img.Metadata?.IccProfile);
    }

    /// <summary>
    /// G.2.2: VarDCT frames whose LF comes from an LF frame (with LF thresholds in the block context, which then see no
    /// quantized LF). E.6 NOTE 4: XYB decoded to an HLG encoding goes through the inverse HLG OOTF, its gamma from the
    /// intensity target, its luminance from the encoding's primaries.
    /// </summary>
    [Theory]
    [InlineData("lf_frame_e7.jxl")]
    [InlineData("lf_frame_e9.jxl")]
    [InlineData("hlg_srgb.jxl")]
    [InlineData("hlg_bt2100.jxl")]
    [InlineData("hlg_gray.jxl")]
    [InlineData("hlg_4000.jxl")]
    public void LossyMatchesLibjxl(string name)
    {
        var img = Jxl.Decode(SpecFixture(name), new JxlDecodeOptions { ForceFloat = true });
        var reference = LoadRef(Path.Combine(FixtureDir, "jxl-spec", name + ".ref"));
        Assert.Equal((reference.Width, reference.Height, reference.Channels), (img.Width, img.Height, img.Channels));
        var a = (float[])img.Data; var e = (float[])reference.Data;
        double m = 0;
        for (int i = 0; i < a.Length; i++) m = Math.Max(m, Math.Abs(Math.Clamp(a[i], 0, 1) - Math.Clamp(e[i], 0, 1)));
        output.WriteLine($"{name}: largest difference to libjxl {m:g3}");
        Assert.True(m < 5e-4, $"{m}");
    }
}

