using Aardvark.ImageCodecs;
using Aardvark.ImageCodecs.JpegXl;
using Xunit;
using Xunit.Abstractions;
using static Aardvark.ImageCodecs.JpegXl.JxlBitReader;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// Header, ICC and frame composition rules of ISO/IEC 18181-1:2024 checked on hand-built codestreams: expected values
/// come from the text of the standard or from libjxl's decode of the same stream.
/// </summary>
public partial class JxlAuditHeaderTests(ITestOutputHelper output)
{
    // ------------------------------------------------------------------ codestream builder

    static readonly JxlBitDepth Float32 = new() { Float = true, Bits = 32, ExpBits = 8 };

    /// <summary>A float32 RGB header (not XYB) with the given number of alpha channels.</summary>
    static JxlImageHeader FloatHeader(int w, int h, int extra = 0, JxlAnimationHeader? anim = null) => new()
    {
        Width = w, Height = h, BitDepth = Float32, XybEncoded = false, Modular16BitBuffers = false, Animation = anim,
        ExtraChannels = Enumerable.Range(0, extra).Select(_ => new JxlExtraChannel { Type = JxlExtraChannelType.Alpha, BitDepth = Float32 }).ToArray(),
    };

    static JxlFrameHeader NewFrame(JxlImageHeader h, int width, int height)
    {
        int ne = h.ExtraChannels.Length;
        return new JxlFrameHeader
        {
            Modular = true, Width = width, Height = height, EcUpsampling = Enumerable.Repeat(1, ne).ToArray(),
            EcBlending = new JxlBlendingInfo[ne], Filter = new JxlRestorationFilter { Gab = false, EpfIters = 0 },
            HaveCrop = width != h.Width || height != h.Height,
        };
    }

    /// <summary>Appends a lossless frame of float planes (colour then extra channels, at frame size).</summary>
    static void Frame(JxlBitWriter w, JxlImageHeader h, JxlFrameHeader fh, float[][] planes, Action<JxlBitWriter>? features = null)
    {
        var chans = planes.Select(p =>
        {
            var c = new JxlChannel(fh.Width, fh.Height, 0, 0);
            for (int i = 0; i < c.Data.Length; i++) c.Data[i] = BitConverter.SingleToInt32Bits(p[i]);
            return c;
        }).ToList();
        JxlLosslessEncoder.EncodeFrame(w, chans, h, fh, JxlLosslessParams.For(2), false, features);
    }

    static JxlBitWriter Start(JxlImageHeader h)
    {
        var w = new JxlBitWriter();
        JxlHeaderWriter.WriteImageHeader(w, h);
        w.ZeroPadToByte();
        return w;
    }

    static float[] Fill(int w, int h, Func<int, int, float> f)
    {
        var v = new float[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) v[y * w + x] = f(x, y);
        return v;
    }

    static float Rand(int seed, int x, int y) => (float)(((uint)(x * 73856093 ^ y * 19349663 ^ seed * 83492791) % 10007) / 10007.0);

    static float[][] Planes(int w, int h, int n, int seed) => Enumerable.Range(0, n).Select(c => Fill(w, h, (x, y) => Rand(seed + c, x, y))).ToArray();

    /// <summary>All displayed frames of our float decode, and libjxl's (null without the oracle), each stacked vertically.</summary>
    (float[] ours, float[]? lib) DecodeBoth(byte[] data, string name)
    {
        if (Environment.GetEnvironmentVariable("PIXCODEC_JXLAUDIT_DUMP") is string dump) File.WriteAllBytes(Path.Combine(dump, name.Replace(' ', '_') + ".jxl"), data);
        var ours = Jxl.DecodeAll(data, new JxlDecodeOptions { ApplyOrientation = false }).SelectMany(f => (float[])f.Data).ToArray();
        var lib = JxlEncodeTests.Libjxl(data);
        if (lib == null) { output.WriteLine($"{name}: libjxl not available, decoded by us only"); return (ours, null); }
        var l = (float[])lib.Data;
        Assert.Equal(l.Length, ours.Length);
        return (ours, l);
    }

    void AssertLikeLibjxl(byte[] data, string name, double tol = 1e-6)
    {
        var (ours, lib) = DecodeBoth(data, name);
        if (lib == null) return;
        double m = 0;
        for (int i = 0; i < ours.Length; i++) m = Math.Max(m, Math.Abs(ours[i] - lib[i]));
        output.WriteLine($"{name}: largest difference to libjxl {m:g3}");
        Assert.True(m <= tol, $"{name}: {m} > {tol}");
    }

    // ------------------------------------------------------------------ E.4 ICC stream

    static void VarInt(List<byte> o, ulong v)
    {
        while (v >= 128) { o.Add((byte)(v & 127 | 128)); v >>= 7; }
        o.Add((byte)v);
    }

    /// <summary>An encoded ICC stream (Table E.10): output_size, commands_size, command stream, data stream.</summary>
    static byte[] IccStream(ulong outputSize, byte[] commands, byte[] data)
    {
        var o = new List<byte>();
        VarInt(o, outputSize);
        VarInt(o, (ulong)commands.Length);
        o.AddRange(commands);
        o.AddRange(data);
        return o.ToArray();
    }

    /// <summary>
    /// Shuffle(bytes, width) as E.4.5 has it: a matrix of `width` rows and ceil(n / width) columns whose last column
    /// alone can miss elements (at the bottom), the input being its rows one after the other, read column by column.
    /// Returns p with output[k] = input[p[k]].
    /// </summary>
    static int[] ShufflePermutation(int n, int width)
    {
        int h = (n + width - 1) / width, full = n % width == 0 ? width : n % width;
        var p = new List<int>();
        for (int c = 0; c < h; c++)
            for (int r = 0; r < width; r++)
                if (c < h - 1 || r < full) p.Add(r * (h - 1) + Math.Min(r, full) + c);
        return p.ToArray();
    }

    /// <summary>
    /// Shuffle(bytes, width) as djxl 0.12 does it: `width` rows of ceil(n / width) bytes, the last rows shorter or
    /// empty, read column by column. That is a djxl defect (it leaves elements missing outside the last column, which
    /// E.4.5 rules out); it differs from the text when two or more elements are missing (see ShuffledProfileDecodes).
    /// </summary>
    static int[] DjxlShufflePermutation(int n, int width)
    {
        int h = (n + width - 1) / width;
        var p = new List<int>();
        for (int c = 0; c < h; c++) for (int r = 0; r < width; r++) if (r * h + c < n) p.Add(r * h + c);
        return p.ToArray();
    }

    /// <summary>The example of E.4.5: (1, ..., 7) shuffled with width 2.</summary>
    [Fact]
    public void ShuffleMatchesTheExample()
    {
        Assert.Equal(new[] { 0, 4, 1, 5, 2, 6, 3 }, ShufflePermutation(7, 2));
        var header = new byte[128];
        var data = header.Concat(new byte[] { 1, 2, 3, 4, 5, 6, 7 }).ToArray();
        var icc = JxlIcc.Unpredict(IccStream(135, new byte[] { 0, 2, 7 }, data));
        Assert.Equal(new byte[] { 1, 5, 2, 6, 3, 7, 4 }, icc[128..]);
    }

    /// <summary>
    /// Shuffle with width 4 of 5 bytes, as E.4.5: rows (d0, d1), (d2), (d3), (d4), so (d0, d2, d3, d4, d1) (djxl 0.12
    /// gives (d0, d2, d4, d1, d3), see DjxlShufflePermutation). The predicted command 4 shuffles the same way (order 0,
    /// stride 4): the first 4 bytes are predicted from header bytes 124..127 (zero), the fifth from the top byte of the
    /// 4 just written.
    /// </summary>
    [Fact]
    public void ShuffleWithShortRows()
    {
        Assert.Equal(new[] { 0, 2, 3, 4, 1 }, ShufflePermutation(5, 4));
        Assert.Equal(new[] { 0, 2, 4, 1, 3 }, DjxlShufflePermutation(5, 4));
        Assert.Equal(new[] { 0, 3, 6, 1, 4, 7, 2, 5 }, ShufflePermutation(8, 3));
        var d = new byte[] { 10, 11, 12, 13, 14 };
        var icc = JxlIcc.Unpredict(IccStream(133, new byte[] { 0, 3, 5 }, new byte[128].Concat(d).ToArray()));
        Assert.Equal(new byte[] { 10, 12, 13, 14, 11 }, icc[128..]);
        var icc4 = JxlIcc.Unpredict(IccStream(133, new byte[] { 0, 4, 3, 5 }, new byte[128].Concat(d).ToArray()));
        Assert.Equal(new byte[] { 0, 0, 0, 0, 10, 12, 13, 14, 11 + 10 }, icc4[124..]);
    }

    /// <summary>An 8 bit RGB lossless image whose ICC profile is the given encoded ICC stream (entropy coded as E.4.1).</summary>
    static byte[] WithIccStream(byte[] enc)
    {
        const int W = 8, H = 8;
        var h = new JxlImageHeader { Width = W, Height = H, XybEncoded = false, Colour = new JxlColourEncoding { WantIcc = true } };
        var w = new JxlBitWriter();
        JxlHeaderWriter.WriteImageHeader(w, h);
        w.WriteU64((ulong)enc.Length);
        var tok = new JxlTokens(enc.Length);
        for (int i = 0; i < enc.Length; i++)
        {
            int b1 = i > 0 ? enc[i - 1] : 0, b2 = i > 1 ? enc[i - 2] : 0;
            tok.Add(IccContext(i, b1, b2), enc[i]);
        }
        var code = new JxlEntropyEncoder(41) { MaxClusters = 16 };
        code.Build(new[] { tok }, false);
        code.WriteCode(w);
        code.WriteTokens(w, tok);
        w.ZeroPadToByte();
        var fh = NewFrame(h, W, H);
        fh.IsLast = true;
        var chans = Enumerable.Range(0, 3).Select(c => new JxlChannel(W, H, 0, 0)).ToList();
        foreach (var c in chans) for (int i = 0; i < c.Data.Length; i++) c.Data[i] = i * 3 % 256;
        JxlLosslessEncoder.EncodeFrame(w, chans, h, fh, JxlLosslessParams.For(2), false);
        return w.ToArray();
    }

    /// <summary>IccContext of E.4.1, as written there.</summary>
    static int IccContext(int i, int b1, int b2)
    {
        if (i <= 128) return 0;
        int p1 = b1 is >= 'a' and <= 'z' or >= 'A' and <= 'Z' ? 0 : b1 is >= '0' and <= '9' or '.' or ',' ? 1 : b1 <= 1 ? 2 + b1
            : b1 < 16 ? 4 : b1 is > 240 and < 255 ? 5 : b1 == 255 ? 6 : 7;
        int p2 = b2 is >= 'a' and <= 'z' or >= 'A' and <= 'Z' ? 0 : b2 is >= '0' and <= '9' or '.' or ',' ? 1 : b2 < 16 ? 2 : b2 > 240 ? 3 : 4;
        return 1 + p1 + p2 * 8;
    }

    /// <summary>
    /// A real profile whose content after the header is one Shuffle command with width 4 over a length that is 1 mod 4,
    /// where djxl and the text of E.4.5 differ. We follow the text: built with <see cref="ShufflePermutation"/> the
    /// stream decodes to the profile. djxl 0.12 (--icc_out) reads it as <see cref="DjxlShufflePermutation"/>, a djxl
    /// defect pinned here: it gives other bytes for the text's stream and the profile for one built its way. No
    /// encoder output depends on it: cjxl (0.12) wrote only width 2 shuffles for every profile tried (some 450, among
    /// them 196 built with 4 byte tag arrays of every length mod 4), and our encoder writes no shuffles.
    /// </summary>
    [Fact]
    public void ShuffledProfileDecodes()
    {
        var icc = IccProfile.DisplayP3.ToBytes();
        int n = icc.Length - 128, tail = (n - 1) % 4;   // shuffle n - tail bytes (1 mod 4: three short rows), copy the rest
        int ns = n - tail;
        var enc = JxlIccWriter.Encode(icc);
        // the header data from our encoder: output_size, commands_size, commands, then 128 header bytes
        int p = 0;
        while ((enc[p++] & 128) != 0) { }
        int csize = 0, shift = 0;
        while (true) { byte b = enc[p++]; csize |= (b & 127) << shift; shift += 7; if (b < 128) break; }
        var header = enc.AsSpan(p + csize, 128).ToArray();
        byte[] Stream(int[] perm)
        {
            var data = new byte[ns];
            for (int k = 0; k < ns; k++) data[perm[k]] = icc[128 + k];
            var cmds = new List<byte> { 0, 3 };
            VarInt(cmds, (ulong)ns);
            if (tail > 0) { cmds.Add(1); VarInt(cmds, (ulong)tail); }
            return IccStream((ulong)icc.Length, cmds.ToArray(), header.Concat(data).Concat(icc[(128 + ns)..]).ToArray());
        }
        var stream = Stream(ShufflePermutation(ns, 4));
        Assert.Equal(icc, JxlIcc.Unpredict(stream));
        var file = WithIccStream(stream);
        if (Environment.GetEnvironmentVariable("PIXCODEC_JXLAUDIT_DUMP") is string dump) { File.WriteAllBytes(Path.Combine(dump, "shuffled_icc.jxl"), file); File.WriteAllBytes(Path.Combine(dump, "shuffled_icc.icc"), icc); }
        Assert.Equal(icc, Jxl.Decode(file).Metadata?.IccProfile);
        var djxlFile = WithIccStream(Stream(DjxlShufflePermutation(ns, 4)));
        Assert.NotEqual(icc, Jxl.Decode(djxlFile).Metadata?.IccProfile);
        if (DjxlIcc(file) is not { } lib) { output.WriteLine("djxl not available"); return; }
        {
            output.WriteLine($"djxl: {lib.Zip(icc).Count(p => p.First != p.Second)} of {icc.Length} bytes differ from the profile");
            Assert.NotEqual(icc, lib);
            Assert.Equal(icc, DjxlIcc(djxlFile));
        }
    }

    /// <summary>The ICC profile djxl reads from the file (tests/oracle/djxl_icc.py), or null without djxl.</summary>
    static byte[]? DjxlIcc(byte[] encoded)
    {
        if (!PythonAvailable) return null;
        var tmp = Path.Combine(FixtureDir, "_tmp");
        Directory.CreateDirectory(tmp);
        var f = Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".jxl");
        try
        {
            File.WriteAllBytes(f, encoded);
            var (exit, text) = RunPython($"djxl_icc.py \"{f}\" \"{f}.icc\"");
            if (CannotVerify(exit, text)) return null;
            Assert.True(exit == 0, "djxl failed: " + text);
            return File.ReadAllBytes(f + ".icc");
        }
        finally
        {
            File.Delete(f); File.Delete(f + ".icc");
        }
    }

    /// <summary>E.4.5: commands 1 to 4 carry num &gt; 0.</summary>
    [Theory]
    [InlineData(new byte[] { 0, 1, 0 })]
    [InlineData(new byte[] { 0, 2, 0 })]
    [InlineData(new byte[] { 0, 3, 0 })]
    [InlineData(new byte[] { 0, 4, 0, 0 })]
    public void EmptyDataCommandIsRejected(byte[] commands)
    {
        // the commands after a one byte copy, so that command 4 has output to predict from
        var cmds = new byte[] { 0, 1, 1 }.Concat(commands.Skip(1)).ToArray();
        Assert.Throws<ImageFormatException>(() => JxlIcc.Unpredict(IccStream(129, cmds, new byte[129])));
        Assert.Equal(129, JxlIcc.Unpredict(IccStream(129, new byte[] { 0, 1, 1 }, new byte[129])).Length);
    }

    /// <summary>
    /// E.4.2: the decoder reads all of the data stream and outputs exactly output_size bytes; no command copies data
    /// left over after the command stream. (djxl 0.12 refuses such profiles too: no commands after the header, a copy
    /// shorter than the data, data beyond output_size; checked by hand. It accepts num == 0 above.)
    /// </summary>
    [Fact]
    public void LeftoverIccDataIsRejected()
    {
        // 3 data bytes after the header, a copy of 2: one byte left unread, whether output_size counts it or not
        Assert.Throws<ImageFormatException>(() => JxlIcc.Unpredict(IccStream(131, new byte[] { 0, 1, 2 }, new byte[131])));
        Assert.Throws<ImageFormatException>(() => JxlIcc.Unpredict(IccStream(130, new byte[] { 0, 1, 2 }, new byte[131])));
        // no commands at all but data beyond the header
        Assert.Throws<ImageFormatException>(() => JxlIcc.Unpredict(IccStream(138, Array.Empty<byte>(), new byte[138])));
        // a header-only profile with more data, or with commands
        Assert.Throws<ImageFormatException>(() => JxlIcc.Unpredict(IccStream(100, Array.Empty<byte>(), new byte[101])));
        Assert.Throws<ImageFormatException>(() => JxlIcc.Unpredict(IccStream(100, new byte[] { 0 }, new byte[100])));
        Assert.Equal(130, JxlIcc.Unpredict(IccStream(130, new byte[] { 0, 1, 2 }, new byte[130])).Length);
        Assert.Equal(100, JxlIcc.Unpredict(IccStream(100, Array.Empty<byte>(), new byte[100])).Length);
    }

    // ------------------------------------------------------------------ F.2 frame header and blending

    const int W = 24, H = 20;

    /// <summary>A first frame replacing the canvas (values beyond [0, 1] too), saved as Reference[slot].</summary>
    static void Background(JxlBitWriter w, JxlImageHeader h, int slot, uint duration = 0)
    {
        var f = NewFrame(h, W, H);
        f.IsLast = false; f.SaveAsReference = slot; f.Duration = duration;
        Frame(w, h, f, Enumerable.Range(0, 3 + h.ExtraChannels.Length).Select(c => Fill(W, H, (x, y) => Rand(7 + c, x, y) * 1.9f - 0.3f)).ToArray());
    }

    /// <summary>
    /// Table F.7 reads clamp when "extra and (mode == kBlend or kMulAdd or kMul)"; libjxl (djxl 0.12) reads it for kMul
    /// without extra channels too, and refuses the stream without that bit (checked by hand). We read and write it as
    /// libjxl does, so that its kMul frames decode.
    /// </summary>
    [Fact]
    public void MulWithoutExtraChannelsHasAClampBitLikeLibjxl()
    {
        var h = FloatHeader(W, H);
        var w = Start(h);
        Background(w, h, 1);
        var f = NewFrame(h, W, H);
        f.IsLast = true; f.Blending = new JxlBlendingInfo { Mode = JxlBlendMode.Mul, Source = 1, Clamp = false };
        Frame(w, h, f, Planes(W, H, 3, 30).Select(p => p.Select(v => v * 2).ToArray()).ToArray());
        AssertLikeLibjxl(w.ToArray(), "kMul without extra channels");
    }

    /// <summary>
    /// Table F.7 reads source when !resets_canvas, which F.2 defines as full_frame and blending_info.mode == kReplace (the
    /// colour channels' mode). libjxl (djxl 0.12) decides it in ec_blending_info by the extra channel's own mode, and
    /// refuses these streams written with the colour mode (checked by hand). We follow libjxl.
    /// </summary>
    [Theory]
    [InlineData(0, 2)]   // kReplace, kBlend
    [InlineData(1, 0)]   // kAdd, kReplace
    [InlineData(0, 1)]
    public void ExtraChannelSourceFollowsItsOwnModeLikeLibjxl(int colourMode, int extraMode)
    {
        var colour = (JxlBlendMode)colourMode;
        var extra = (JxlBlendMode)extraMode;
        var h = FloatHeader(W, H, 1);
        var w = Start(h);
        Background(w, h, 0);
        var f = NewFrame(h, W, H);
        f.IsLast = true;
        // source 0, where the background is (also when it is not coded)
        f.Blending = new JxlBlendingInfo { Mode = colour, Source = 0 };
        f.EcBlending[0] = new JxlBlendingInfo { Mode = extra, Source = 0 };
        Frame(w, h, f, Planes(W, H, 4, 40));
        AssertLikeLibjxl(w.ToArray(), $"colour {colour}, extra channel {extra}");
    }

    /// <summary>
    /// F.2: can_reference is !is_last and (duration == 0 or save_as_reference != 0) and frame_type != kLFFrame: an
    /// animation frame with a duration saved as slot 0 is not recorded, the next frame adds to an empty Reference[0].
    /// </summary>
    [Fact]
    public void ShownFrameIsNotRecordedInSlotZero()
    {
        var h = FloatHeader(W, H, 0, new JxlAnimationHeader { TpsNumerator = 1000, TpsDenominator = 1 });
        var w = Start(h);
        Background(w, h, 0, duration: 10);
        var f = NewFrame(h, W, H);
        f.IsLast = true; f.Duration = 10; f.Blending = new JxlBlendingInfo { Mode = JxlBlendMode.Add, Source = 0 };
        Frame(w, h, f, Planes(W, H, 3, 50));
        AssertLikeLibjxl(w.ToArray(), "shown frame in slot 0");
    }

    /// <summary>
    /// Table F.8 with clamp: only new_alpha is clamped to [0, 1]; the old alpha of the source frame (here beyond [0, 1])
    /// enters the kBlend formulas as it is.
    /// </summary>
    [Fact]
    public void BlendClampsOnlyTheNewAlpha()
    {
        var h = FloatHeader(W, H, 1);
        var w = Start(h);
        Background(w, h, 1);
        var f = NewFrame(h, W, H);
        f.IsLast = true;
        f.Blending = new JxlBlendingInfo { Mode = JxlBlendMode.Blend, AlphaChannel = 0, Clamp = true, Source = 1 };
        f.EcBlending[0] = new JxlBlendingInfo { Mode = JxlBlendMode.Blend, AlphaChannel = 0, Clamp = true, Source = 1 };
        Frame(w, h, f, Planes(W, H, 4, 60).Select(p => p.Select(v => v * 1.6f - 0.3f).ToArray()).ToArray());
        AssertLikeLibjxl(w.ToArray(), "kBlend clamped", 1e-5);
    }

    /// <summary>
    /// F.2 and L.2.2: frames of an XYB image blend in the colour space after the inverse colour transforms, the colour
    /// encoding of the image header (transfer function and primaries included), not in linear sRGB. Two lossy frames,
    /// the second added (kAdd) or blended (kBlend) onto the first; libjxl does the same (and cjxl-made blended
    /// animations decode within one level of it, checked by hand). The samples stay away from 0: how a transfer curve
    /// treats negative values (lossy coding makes some) is not specified, and libjxl clamps them for a gamma curve.
    /// </summary>
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(true, 0)]
    public void XybFramesBlendInTheHeaderEncoding(bool p3Gamma, int mode)
    {
        const int S = 40;
        bool alpha = mode == 2;
        var h = new JxlImageHeader { Width = S, Height = S };
        if (p3Gamma) h.Colour = new JxlColourEncoding { Primaries = JxlPrimaries.P3, Transfer = JxlTransfer.Gamma, Gamma = 0.45455 };
        if (alpha) h.ExtraChannels = new[] { new JxlExtraChannel() };
        var w = Start(h);
        int nc = alpha ? 4 : 3;
        RawImage Image(int seed) => new(S, S, alpha ? ColorModel.Rgba : ColorModel.Rgb,
            Enumerable.Range(0, S * S * nc).Select(i => (byte)(i % nc == 3 ? 40 + i * 7 % 180 : (int)(64 + Rand(seed + i % nc, i / nc % S, i / nc / S) * 64))).ToArray(), nc);
        var opt = new JxlEncodeOptions { Distance = 1, Effort = 3 };
        for (int k = 0; k < 2; k++)
        {
            var f = NewFrame(h, S, S);
            f.IsLast = k == 1;
            if (k == 1)
            {
                f.Blending = new JxlBlendingInfo { Mode = (JxlBlendMode)mode, Source = 0 };
                if (alpha) f.EcBlending[0] = new JxlBlendingInfo { Mode = JxlBlendMode.Blend, Source = 0 };
            }
            JxlVarDctEncoder.EncodeFrame(w, Image(10 * k + 3), h, f, opt);
        }
        var data = w.ToArray();
        var ours = (byte[])Jxl.Decode(data).Data;
        var lib = JxlEncodeTests.Libjxl(data);
        if (lib == null) { output.WriteLine("libjxl not available"); return; }
        var l = (byte[])lib.Data;
        Assert.Equal(l.Length, ours.Length);
        int m = 0;
        for (int i = 0; i < ours.Length; i++) m = Math.Max(m, Math.Abs(ours[i] - l[i]));
        output.WriteLine($"XYB {(p3Gamma ? "P3 gamma" : "sRGB")} {(JxlBlendMode)mode}: largest difference to libjxl {m}");
        Assert.True(m <= 2, $"{m}");
    }

    /// <summary>
    /// The patches of a frame (K.3.1): one patch type copying a w x h rectangle at (sx, sy) of Reference[1] with kReplace
    /// (on colour and every extra channel) to (px, py).
    /// </summary>
    static Action<JxlBitWriter> OnePatch(int ne, int sx, int sy, int pw, int ph, int px, int py) => bw =>
    {
        var t = new JxlTokens();
        t.Add(0, 1);                                // one patch type
        t.Add(1, 1);                                // reference 1
        t.Add(3, (uint)sx); t.Add(3, (uint)sy);
        t.Add(2, (uint)pw - 1); t.Add(2, (uint)ph - 1);
        t.Add(7, 0);                                // one position
        t.Add(4, (uint)px); t.Add(4, (uint)py);
        for (int c = 0; c <= ne; c++) t.Add(5, 1);  // kReplace
        var enc = new JxlEntropyEncoder(10) { MaxClusters = 10 };
        enc.Build(new[] { t }, false);
        enc.WriteCode(bw);
        enc.WriteTokens(bw, t);
    };

    /// <summary>
    /// F.2 says "Blending is performed before recording the reference frame" after describing both cases; libjxl (djxl
    /// 0.12) records a frame saved before its colour transform as decoded: in frame coordinates (here a full frame larger
    /// than the image, with a negative origin) and without blending its extra channel (kAdd here). We do the same. libjxl
    /// refuses to blend from such a reference, so a patch copying the whole canvas reads it back.
    /// </summary>
    [Fact]
    public void SavedBeforeCtIsRecordedAsDecoded()
    {
        var h = FloatHeader(W, H, 1);
        var w = Start(h);
        Background(w, h, 0);
        var f = NewFrame(h, W + 8, H + 6);
        f.X0 = -4; f.Y0 = -2; f.IsLast = false; f.SaveAsReference = 1; f.SaveBeforeColourTransform = true;
        f.EcBlending[0] = new JxlBlendingInfo { Mode = JxlBlendMode.Add, Source = 0 };
        var fp = Planes(W + 8, H + 6, 4, 90);
        Frame(w, h, f, fp);
        var g = NewFrame(h, W, H);
        g.IsLast = true; g.Flags = JxlFrameHeader.FlagPatches;
        Frame(w, h, g, Planes(W, H, 4, 91), OnePatch(1, 0, 0, W, H, 0, 0));
        AssertLikeLibjxl(w.ToArray(), "save_before_ct, larger frame");
        var ours = (float[])Jxl.DecodeAll(w.ToArray(), new JxlDecodeOptions { ApplyOrientation = false })[0].Data;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                for (int c = 0; c < 4; c++) Assert.Equal(fp[c][y * (W + 8) + x], ours[(y * W + x) * 4 + c]);
    }

    // ------------------------------------------------------------------ J.1 restoration filter

    /// <summary>A modular frame with the edge preserving filter and custom channel scales (the last one given).</summary>
    static byte[] EpfStream(float scale2)
    {
        var h = FloatHeader(W, H);
        var w = Start(h);
        var f = NewFrame(h, W, H);
        f.IsLast = true;
        f.Filter = new JxlRestorationFilter { Gab = false, EpfIters = 1, EpfChannelScale = new[] { 30f, 6f, scale2 } };
        Frame(w, h, f, Planes(W, H, 3, 70));
        return w.ToArray();
    }

    /// <summary>
    /// Table J.1 (2024): after the custom channel scales, epf_weight_custom has a u(32) that is ignored, not two F16 values.
    /// With the bits of two F16 ones libjxl decodes the stream as with zeros (it ignores the value too); with all bits
    /// set (F16 NaNs, which ReadF16 refuses) libjxl 0.12 refuses the stream, the text allows any value: we decode it.
    /// </summary>
    [Fact]
    public void EpfWeightsHaveAnIgnoredU32()
    {
        // the third scale starts where the streams for 3.5 and the next F16 (one more in the lowest bit) first differ
        var a = EpfStream(3.5f);
        var b = EpfStream(3.501953125f);
        Assert.Equal(a.Length, b.Length);
        int bit = 0;
        while ((a[bit >> 3] >> (bit & 7) & 1) == (b[bit >> 3] >> (bit & 7) & 1)) bit++;
        byte[] WithIgnored(uint v)
        {
            var s = (byte[])a.Clone();
            for (int k = 0; k < 32; k++)
            {
                int p = bit + 16 + k;
                s[p >> 3] = (byte)(s[p >> 3] & ~(1 << (p & 7)) | (int)(v >> k & 1) << (p & 7));
            }
            return s;
        }
        var ones = WithIgnored(0x3C003C00);
        Assert.NotEqual(a, ones);
        var expected = (float[])Jxl.Decode(a).Data;
        Assert.Equal(expected, (float[])Jxl.Decode(ones).Data);
        Assert.Equal(expected, (float[])Jxl.Decode(WithIgnored(0xFFFFFFFF)).Data);
        AssertLikeLibjxl(a, "epf custom weights", 1e-5);
        AssertLikeLibjxl(ones, "epf custom weights, ignored bits set", 1e-5);
    }

    // ------------------------------------------------------------------ range checks

    static JxlBitReader Reader(Action<JxlBitWriter> write)
    {
        var w = new JxlBitWriter();
        write(w);
        w.Write(32, 0); w.Write(32, 0);   // room after the field, so that only the check can fail
        return new JxlBitReader(w.ToArray());
    }

    /// <summary>D.3.5: exp_bits in [2, 8] and mantissa_bits = bits_per_sample - exp_bits - 1 in [2, 23].</summary>
    [Theory]
    [InlineData(16, 5, true)]
    [InlineData(32, 8, true)]
    [InlineData(5, 2, true)]
    [InlineData(16, 1, false)]   // exp_bits 1
    [InlineData(4, 2, false)]    // mantissa_bits 1
    [InlineData(33, 8, false)]   // mantissa_bits 24
    [InlineData(12, 9, false)]
    public void FloatBitDepthRanges(int bits, int expBits, bool valid)
    {
        var br = Reader(w =>
        {
            w.WriteBool(true);
            w.WriteU32((uint)bits, Val(32), Val(16), Val(24), BitsOffset(6, 1));
            w.Write(4, (uint)(expBits - 1));
        });
        if (valid) { var d = JxlBitDepth.Read(br); Assert.Equal((bits, expBits), (d.Bits, d.ExpBits)); }
        else Assert.Throws<ImageFormatException>(() => JxlBitDepth.Read(br));
    }

    /// <summary>D.3.3: the preview width and height do not exceed 4096.</summary>
    [Theory]
    [InlineData(4096, true)]
    [InlineData(4097, false)]
    public void PreviewSizeLimit(int size, bool valid)
    {
        var br = Reader(w =>
        {
            w.WriteBool(false);   // div8
            w.WriteU32((uint)size, BitsOffset(6, 1), BitsOffset(8, 65), BitsOffset(10, 321), BitsOffset(12, 1345));
            w.Write(3, 1);        // ratio 1:1
        });
        if (valid) { JxlHeaderUtil.ReadPreviewSize(br, out int pw, out int ph); Assert.Equal((size, size), (pw, ph)); }
        else Assert.Throws<ImageFormatException>(() => JxlHeaderUtil.ReadPreviewSize(br, out _, out _));
    }

    /// <summary>E.3: min_nits in [0, intensity_target]; linear_below in [0, 1] if relative_to_max_display, else &gt;= 0.</summary>
    [Theory]
    [InlineData(255, 0.5, false, 2, true)]
    [InlineData(255, 255, true, 1, true)]
    [InlineData(255, 300, false, 0, false)]
    [InlineData(255, -1, false, 0, false)]
    [InlineData(1000, 0, true, 1.5, false)]
    [InlineData(1000, 0, false, -0.5, false)]
    public void ToneMappingRanges(double target, double minNits, bool relative, double linearBelow, bool valid)
    {
        var br = Reader(w =>
        {
            w.WriteBool(false);
            w.WriteF16((float)target); w.WriteF16((float)minNits); w.WriteBool(relative); w.WriteF16((float)linearBelow);
        });
        if (valid) Assert.Equal((float)minNits, JxlToneMapping.Read(br).MinNits);
        else Assert.Throws<ImageFormatException>(() => JxlToneMapping.Read(br));
    }

    /// <summary>B.3: ext_id is in [0, 63), and the extension bit counts may not wrap around when summed.</summary>
    [Fact]
    public void ExtensionChecks()
    {
        Assert.Throws<ImageFormatException>(() => Reader(w => { w.WriteU64(1UL << 63); w.WriteU64(0); }).SkipExtensions());
        Assert.Throws<ImageFormatException>(() => Reader(w => { w.WriteU64(3); w.WriteU64(10); w.WriteU64(ulong.MaxValue - 4); }).SkipExtensions());
        var ok = Reader(w => { w.WriteU64(3); w.WriteU64(10); w.WriteU64(5); w.Write(15, 0); w.WriteBool(true); });
        ok.SkipExtensions();
        Assert.True(ok.ReadBool());
    }

    /// <summary>
    /// B.2.7: the bits skipped by ZeroPadToByte are zero. A one in the padding after the image header, or after the TOC's
    /// permuted flag, makes the stream invalid; djxl 0.12 refuses both too (checked by hand).
    /// </summary>
    [Fact]
    public void NonzeroPaddingIsRejected()
    {
        var h = FloatHeader(33, H);
        var hw = new JxlBitWriter();
        JxlHeaderWriter.WriteImageHeader(hw, h);
        long headerBits = hw.BitLength;
        Assert.NotEqual(0, headerBits % 8);
        var w = Start(h);
        var f = NewFrame(h, 33, H);
        f.IsLast = true;
        var fw = new JxlBitWriter();
        JxlHeaderWriter.WriteFrameHeader(fw, f, h);
        long tocPad = (headerBits + 7) / 8 * 8 + fw.BitLength + 1;
        Assert.NotEqual(0, tocPad % 8);
        Frame(w, h, f, Planes(33, H, 3, 80));
        var a = w.ToArray();
        Jxl.Decode(a);
        foreach (long bit in new[] { headerBits, tocPad })
        {
            var s = (byte[])a.Clone();
            s[bit >> 3] |= (byte)(1 << (int)(bit & 7));
            Assert.Throws<ImageFormatException>(() => Jxl.Decode(s));
        }
    }

    /// <summary>A frame header with num_passes 3 and the given two downsample factors (last passes 0 and 1).</summary>
    static JxlBitReader PassesFrame(int d0, int d1) => Reader(w =>
    {
        w.WriteBool(false);   // not all_default
        w.Write(2, 0);        // kRegularFrame
        w.Write(1, 1);        // kModular
        w.WriteU64(0);        // flags
        w.WriteBool(false);   // do_YCbCr
        w.WriteU32(1, Val(1), Val(2), Val(4), Val(8));
        w.Write(2, 1);        // group_size_shift
        w.WriteU32(3, Val(1), Val(2), Val(3), BitsOffset(3, 4));
        w.WriteU32(2, Val(0), Val(1), Val(2), BitsOffset(1, 3));
        w.Write(2, 0); w.Write(2, 0);
        w.WriteU32((uint)d0, Val(1), Val(2), Val(4), Val(8)); w.WriteU32((uint)d1, Val(1), Val(2), Val(4), Val(8));
        w.WriteU32(0, Val(0), Val(1), Val(2), Bits(3)); w.WriteU32(1, Val(0), Val(1), Val(2), Bits(3));
        w.WriteBool(false);   // have_crop
        w.WriteU32(0, Val(0), Val(1), Val(2), BitsOffset(2, 3));   // kReplace, full frame: no source
        w.WriteBool(true);    // is_last
        w.WriteU32(0, Val(0), Bits(4), BitsOffset(5, 16), BitsOffset(10, 48));
        w.WriteBool(true);    // default restoration filter
        w.WriteU64(0);
    });

    /// <summary>F.2: the sequence downsample is strictly decreasing.</summary>
    [Fact]
    public void DownsampleIsStrictlyDecreasing()
    {
        var h = FloatHeader(W, H);
        var f = JxlFrameHeader.Read(PassesFrame(4, 2), h);
        Assert.Equal(3, f.NumPasses);
        Assert.Equal(new[] { 4, 2 }, f.Downsample);
        Assert.Throws<ImageFormatException>(() => JxlFrameHeader.Read(PassesFrame(2, 4), h));
        Assert.Throws<ImageFormatException>(() => JxlFrameHeader.Read(PassesFrame(2, 2), h));
    }

    /// <summary>D.3.6: 1 &lt;&lt; dim_shift does not exceed the group_dim of any frame (128 with group_size_shift 0).</summary>
    [Theory]
    [InlineData(7, 0, true)]
    [InlineData(8, 1, true)]
    [InlineData(8, 0, false)]
    public void DimShiftWithinGroupDim(int dimShift, int groupSizeShift, bool valid)
    {
        var h = FloatHeader(W, H, 1);
        h.ExtraChannels[0].DimShift = dimShift;
        var f = NewFrame(h, W, H);
        f.IsLast = true; f.GroupSizeShift = groupSizeShift;
        var br = Reader(w => JxlHeaderWriter.WriteFrameHeader(w, f, h));
        if (valid) Assert.Equal(groupSizeShift, JxlFrameHeader.Read(br, h).GroupSizeShift);
        else Assert.Throws<ImageFormatException>(() => JxlFrameHeader.Read(br, h));
    }

    /// <summary>F.2: d_xqms, the default x_qm_scale, is 3 for VarDCT frames of XYB images and 2 otherwise.</summary>
    [Fact]
    public void DefaultXQmScale()
    {
        var xyb = new JxlImageHeader { Width = W, Height = H };
        var rgb = FloatHeader(W, H);
        Assert.Equal(3, JxlFrameHeader.Read(Reader(w => w.WriteBool(true)), xyb).XQmScale);
        Assert.Equal(2, JxlFrameHeader.Read(Reader(w => w.WriteBool(true)), rgb).XQmScale);
        var f = NewFrame(xyb, W, H);
        f.IsLast = true;
        Assert.Equal(2, JxlFrameHeader.Read(Reader(w => JxlHeaderWriter.WriteFrameHeader(w, f, xyb)), xyb).XQmScale);
    }
}
