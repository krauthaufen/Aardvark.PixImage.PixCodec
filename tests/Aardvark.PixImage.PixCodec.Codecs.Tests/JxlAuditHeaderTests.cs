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
    static void Frame(JxlBitWriter w, JxlImageHeader h, JxlFrameHeader fh, float[][] planes)
    {
        var chans = planes.Select(p =>
        {
            var c = new JxlChannel(fh.Width, fh.Height, 0, 0);
            for (int i = 0; i < c.Data.Length; i++) c.Data[i] = BitConverter.SingleToInt32Bits(p[i]);
            return c;
        }).ToList();
        JxlLosslessEncoder.EncodeFrame(w, chans, h, fh, JxlLosslessParams.For(2), false);
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
    /// Shuffle(bytes, width) as djxl 0.12 does it: `width` rows of ceil(n / width) bytes (the last rows shorter), read
    /// column by column. Returns p with output[k] = input[p[k]]. (The text of E.4.5 has only the last column short; the
    /// two differ when two or more elements are missing, see ShuffledProfileDecodes.)
    /// </summary>
    static int[] ShufflePermutation(int n, int width)
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
    /// Shuffle with width 4 of 5 bytes, as djxl: rows (d0, d1), (d2, d3), (d4), so (d0, d2, d4, d1, d3); the text of
    /// E.4.5 would give (d0, d2, d3, d4, d1). The predicted command 4 shuffles the same way (order 0, stride 4): the first
    /// 4 bytes are predicted from header bytes 124..127 (zero), the fifth from the top byte of the 4 just written.
    /// </summary>
    [Fact]
    public void ShuffleWithShortRows()
    {
        Assert.Equal(new[] { 0, 2, 4, 1, 3 }, ShufflePermutation(5, 4));
        var d = new byte[] { 10, 11, 12, 13, 14 };
        var icc = JxlIcc.Unpredict(IccStream(133, new byte[] { 0, 3, 5 }, new byte[128].Concat(d).ToArray()));
        Assert.Equal(new byte[] { 10, 12, 14, 11, 13 }, icc[128..]);
        var icc4 = JxlIcc.Unpredict(IccStream(133, new byte[] { 0, 4, 3, 5 }, new byte[128].Concat(d).ToArray()));
        Assert.Equal(new byte[] { 0, 0, 0, 0, 10, 12, 14, 11, 13 + 10 }, icc4[124..]);
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
    /// where djxl and the text of E.4.5 differ. djxl 0.12 --icc_out gives back the profile when the data is built with
    /// <see cref="ShufflePermutation"/> (checked by hand; built with the literal text, it gives other bytes), and
    /// libjxl-encoded profiles have to decode the way libjxl reads them: we follow djxl.
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
        var perm = ShufflePermutation(ns, 4);
        var data = new byte[ns];
        for (int k = 0; k < ns; k++) data[perm[k]] = icc[128 + k];
        var cmds = new List<byte> { 0, 3 };
        VarInt(cmds, (ulong)ns);
        if (tail > 0) { cmds.Add(1); VarInt(cmds, (ulong)tail); }
        var stream = IccStream((ulong)icc.Length, cmds.ToArray(), header.Concat(data).Concat(icc[(128 + ns)..]).ToArray());
        Assert.Equal(icc, JxlIcc.Unpredict(stream));
        var file = WithIccStream(stream);
        if (Environment.GetEnvironmentVariable("PIXCODEC_JXLAUDIT_DUMP") is string dump) { File.WriteAllBytes(Path.Combine(dump, "shuffled_icc.jxl"), file); File.WriteAllBytes(Path.Combine(dump, "shuffled_icc.icc"), icc); }
        Assert.Equal(icc, Jxl.Decode(file).Metadata?.IccProfile);
        _ = JxlEncodeTests.Libjxl(file);
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
}
