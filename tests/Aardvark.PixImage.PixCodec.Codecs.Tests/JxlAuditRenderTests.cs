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
}
