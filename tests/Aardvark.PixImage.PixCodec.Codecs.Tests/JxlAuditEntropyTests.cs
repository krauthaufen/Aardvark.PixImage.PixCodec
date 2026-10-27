using Aardvark.ImageCodecs;
using Aardvark.ImageCodecs.JpegXl;
using Xunit;
using Xunit.Abstractions;
using static Aardvark.ImageCodecs.JpegXl.JxlBitReader;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// Points of ISO/IEC 18181-1:2024 Annexes C (entropy coding) and H (modular) checked on hand-built lossless modular
/// codestreams, decoded by us and by libjxl.
/// </summary>
public class JxlAuditEntropyTests(ITestOutputHelper output)
{
    // ------------------------------------------------------------------ codestream builder

    /// <summary>
    /// A lossless modular image (grey or RGB, integer samples) of one frame; sections in TOC order, the first one being
    /// LfGlobal (default LF dequantization, then GlobalModular without a global tree, written by globalModular).
    /// </summary>
    static byte[] Image(int w, int h, int channels, Action<JxlBitWriter> globalModular, int bits = 8, params byte[][] more)
        => ImageRaw(w, h, channels, lf => { lf.WriteBool(false); globalModular(lf); }, bits, more);

    /// <summary>As above; lfGlobal writes GlobalModular from its first Bool (global tree present) on.</summary>
    static byte[] ImageRaw(int w, int h, int channels, Action<JxlBitWriter> lfGlobal, int bits, params byte[][] more)
    {
        var hd = new JxlImageHeader { Width = w, Height = h, XybEncoded = false, BitDepth = new JxlBitDepth { Bits = bits } };
        if (channels == 1) hd.Colour = new JxlColourEncoding { ColourSpace = JxlColourSpace.Grey };
        var cs = new JxlBitWriter();
        JxlHeaderWriter.WriteImageHeader(cs, hd);
        cs.ZeroPadToByte();
        var fh = new JxlFrameHeader { Modular = true, Width = w, Height = h, Filter = new JxlRestorationFilter { Gab = false, EpfIters = 0 } };
        var lf = new JxlBitWriter();
        lf.WriteBool(true);    // default LF dequantization
        lfGlobal(lf);
        var sections = new List<byte[]> { lf.ToArray() };
        sections.AddRange(more);
        JxlLosslessEncoder.WriteFrame(cs, fh, hd, sections);
        return cs.ToArray();
    }

    static void NoTransforms(JxlBitWriter w) => w.WriteU32(0, Val(0), Val(1), BitsOffset(4, 2), BitsOffset(8, 18));

    static void WriteConfig(JxlBitWriter w, in JxlHybridUint c, int logAlpha)
    {
        w.Write(JxlHybridUint.CeilLog2(logAlpha + 1), (uint)c.SplitExponent);
        if (c.SplitExponent != logAlpha)
        {
            w.Write(JxlHybridUint.CeilLog2(c.SplitExponent + 1), (uint)c.MsbInToken);
            w.Write(JxlHybridUint.CeilLog2(c.SplitExponent - c.MsbInToken + 1), (uint)c.LsbInToken);
        }
    }

    /// <summary>
    /// A modular sub-bitstream with a local tree: the channels as they are after the transforms (written by transforms),
    /// tokenized with the tree. The entropy code is our encoder's, written by code (default: as the encoder writes it).
    /// </summary>
    sealed class Modular
    {
        public List<JxlChannel> Channels = new();
        public Action<JxlBitWriter> Transforms = NoTransforms;
        public JxlEncTree Tree = Leaf(0);
        public Action<JxlEntropyEncoder>? Configure;
        public Action<JxlTokens>? Edit;
        public Action<JxlBitWriter, JxlEntropyEncoder>? Code;

        public static JxlEncTree Leaf(int predictor)
        {
            var t = new JxlEncTree();
            return t.Finish(t.AddLeaf(predictor));
        }

        public void Write(JxlBitWriter w)
        {
            w.WriteBool(false);   // local tree
            w.WriteBool(true);    // default weighted predictor parameters
            Transforms(w);
            Tree.Write(w);
            var tok = new JxlTokens();
            for (int i = 0; i < Channels.Count; i++) JxlModularTokenizer.EncodeChannel(Channels, i, Tree.Specialize(i, 0), JxlWpHeader.Default, tok);
            Edit?.Invoke(tok);
            var enc = new JxlEntropyEncoder(Tree.NumLeaves) { MaxClusters = Math.Min(256, Tree.NumLeaves) };
            Configure?.Invoke(enc);
            enc.Build(new[] { tok }, false);
            if (Code != null) Code(w, enc); else enc.WriteCode(w);
            if (AnsTokens) enc.WriteTokens(w, tok);
        }

        /// <summary>False when <see cref="Code"/> writes a code whose symbols take no bits (prefix codes of one symbol).</summary>
        public bool AnsTokens = true;
    }

    /// <summary>Appends the bits of src from bit position from on.</summary>
    static void AppendBits(JxlBitWriter dst, JxlBitWriter src, int from)
    {
        long n = src.BitLength;   // before ToArray pads it
        var br = new JxlBitReader(src.ToArray());
        for (long i = 0; i < n; i++)
        {
            uint b = br.ReadBits(1);
            if (i >= from) dst.Write(1, b);
        }
    }

    /// <summary>Our encoder's code written field by field, with the context map and the distributions replaceable.</summary>
    static void WriteCode(JxlBitWriter w, JxlEntropyEncoder e, Action<JxlBitWriter>? contextMap = null, Action<JxlBitWriter, int>? distribution = null)
    {
        Assert.False(e.Lz77);
        w.WriteBool(false);   // no LZ77
        if (contextMap != null) contextMap(w);
        else if (e.NumContexts > 1) JxlEntropyEncoder.WriteContextMap(w, e.ContextMap, e.NumClusters);
        w.WriteBool(false);   // ANS
        w.Write(2, (uint)(e.LogAlphaSize - 5));
        for (int k = 0; k < e.NumClusters; k++) WriteConfig(w, e.Configs[k], e.LogAlphaSize);
        for (int k = 0; k < e.NumClusters; k++)
        {
            if (distribution != null) distribution(w, k);
            else JxlAnsEnc.WriteDistribution(w, e.Freqs[k], e.LogAlphaSize, e.Shifts[k]);
        }
    }

    /// <summary>The prefix code of the log counts of C.2.5, in reading order (the table lists the bits right to left).</summary>
    static readonly string[] LogCountCode =
    {
        "10001", "1101", "1111", "1100", "1001", "1110", "001", "010", "101", "011", "000", "100001", "1000000", "1000001",
    };

    static void WriteU8(JxlBitWriter w, int v)
    {
        if (v == 0) { w.WriteBool(false); return; }
        int n = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)v);
        w.WriteBool(true); w.Write(3, (uint)n); w.Write(n, (uint)(v - (1 << n)));
    }

    static int LogCount(int f) => f == 0 ? 0 : 1 + (31 - System.Numerics.BitOperations.LeadingZeroCount((uint)f));

    /// <summary>
    /// A complex ANS distribution (C.2.5) at full precision (shift 13): logCounts[i] per entry, the value of every entry
    /// but the omitted one (the first with the largest log count) coded exactly, no runs.
    /// </summary>
    static void WriteComplexDistribution(JxlBitWriter w, int[] freq, int[] logCounts)
    {
        w.WriteBool(false); w.WriteBool(false);
        w.WriteBool(true); w.WriteBool(true); w.WriteBool(true); w.Write(3, 6);   // len 3, shift = 6 + 8 - 1 = 13
        WriteU8(w, freq.Length - 3);
        int omit = -1, max = -1;
        for (int i = 0; i < freq.Length; i++)
        {
            foreach (char c in LogCountCode[logCounts[i]]) w.WriteBool(c == '1');
            if (logCounts[i] > max) { max = logCounts[i]; omit = i; }
        }
        for (int i = 0; i < freq.Length; i++)
        {
            int code = logCounts[i];
            if (i == omit || code <= 1) continue;
            Assert.Equal(code, LogCount(freq[i]));
            w.Write(code - 1, (uint)(freq[i] - (1 << (code - 1))));   // bitcount = code - 1 at shift 13
        }
    }

    static JxlChannel Channel(int w, int h, Func<int, int, int> f, int shift = 0)
    {
        var c = new JxlChannel(w, h, shift, shift);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) c.Data[y * w + x] = f(x, y);
        return c;
    }

    static int Rand(int seed, int x, int y, int n) => (int)((uint)(x * 73856093 ^ y * 19349663 ^ seed * 83492791) % (uint)n);

    static void Dump(string name, byte[] data)
    {
        if (Environment.GetEnvironmentVariable("PIXCODEC_JXLAUDIT_DUMP") is string dir) File.WriteAllBytes(Path.Combine(dir, name + ".jxl"), data);
    }

    /// <summary>Decodes with us and with libjxl (when available); asserts identical samples and returns ours.</summary>
    RawImage DecodeLikeLibjxl(byte[] data, string name)
    {
        Dump(name, data);
        var ours = Jxl.Decode(data, new JxlDecodeOptions { ApplyOrientation = false });
        var lib = JxlEncodeTests.Libjxl(data);
        if (lib == null) output.WriteLine($"{name}: libjxl not available, decoded by us only");
        else JxlEncodeTests.AssertSameSamples(ours, lib);
        return ours;
    }

    /// <summary>Whether libjxl rejects the stream (null without the oracle).</summary>
    static bool? LibjxlRejects(byte[] data)
    {
        if (!PythonAvailable) return null;
        var tmp = Path.Combine(FixtureDir, "_tmp");
        Directory.CreateDirectory(tmp);
        var f = Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".jxl");
        try
        {
            File.WriteAllBytes(f, data);
            var (exit, text) = RunPython($"decode_to_ref.py \"{f}\" \"{f}.ref\"");
            if (CannotVerify(exit, text)) return null;
            return exit != 0;
        }
        finally { File.Delete(f); File.Delete(f + ".ref"); }
    }

    static byte[] Bytes(RawImage r) => (byte[])r.Data;

    static void Transforms(JxlBitWriter w, int n) => w.WriteU32((uint)n, Val(0), Val(1), BitsOffset(4, 2), BitsOffset(8, 18));

    static void BeginC(JxlBitWriter w, int b) => w.WriteU32((uint)b, Bits(3), BitsOffset(6, 8), BitsOffset(10, 72), BitsOffset(13, 1096));

    static void Palette(JxlBitWriter w, int begin, int numC, int nbColours, int nbDeltas, int dPred)
    {
        w.Write(2, 1);
        BeginC(w, begin);
        w.WriteU32((uint)numC, Val(1), Val(3), Val(4), BitsOffset(13, 1));
        w.WriteU32((uint)nbColours, BitsOffset(8, 0), BitsOffset(10, 256), BitsOffset(12, 1280), BitsOffset(16, 5376));
        w.WriteU32((uint)nbDeltas, Val(0), BitsOffset(8, 1), BitsOffset(10, 257), BitsOffset(16, 1281));
        w.Write(4, (uint)dPred);
    }

    static void Rct(JxlBitWriter w, int begin, int type)
    {
        w.Write(2, 0);
        BeginC(w, begin);
        w.WriteU32((uint)type, Val(6), Bits(2), BitsOffset(4, 2), BitsOffset(6, 10));
    }

    static void Squeeze(JxlBitWriter w, params (bool horizontal, bool inPlace, int begin, int num)[] sp)
    {
        w.Write(2, 2);
        w.WriteU32((uint)sp.Length, Val(0), BitsOffset(4, 1), BitsOffset(6, 9), BitsOffset(8, 41));
        foreach (var p in sp)
        {
            w.WriteBool(p.horizontal); w.WriteBool(p.inPlace);
            BeginC(w, p.begin);
            w.WriteU32((uint)p.num, Val(1), Val(2), Val(3), BitsOffset(4, 4));
        }
    }

    /// <summary>A tree of the given number of leaves (predictor zero) on property 3 (x), one leaf per column.</summary>
    static JxlEncTree ColumnTree(int leaves)
    {
        var t = new JxlEncTree();
        int Build(int lo, int hi)
        {
            if (lo == hi) return t.AddLeaf(0);
            int mid = (lo + hi) >> 1;
            int left = Build(mid + 1, hi), right = Build(lo, mid);
            return t.AddSplit(3, mid, left, right);
        }
        return t.Finish(Build(0, leaves - 1));
    }

    // ------------------------------------------------------------------ H.6.4 palette

    /// <summary>
    /// H.6.4 gives the palette meta channel a width of nb_colours, its first nb_deltas entries being the deltas.
    /// libjxl reads nb_colours + nb_deltas entries (deltas first, implicit colours from there on) and rejects the
    /// layout of the text; we follow libjxl. Indices below nb_deltas (negative ones too) add the d_pred prediction.
    /// </summary>
    [Fact]
    public void DeltaPaletteHoldsColoursAndDeltas()
    {
        const int W = 12, H = 10, NbColours = 6, NbDeltas = 3;
        Modular Stream(int width) => new Modular
        {
            Transforms = w => { Transforms(w, 1); Palette(w, 0, 3, NbColours, NbDeltas, 5); },
            Channels =
            {
                Channel(width, 3, (x, c) => x < NbDeltas ? Rand(c, x, 1, 7) - 3 : 40 + Rand(c, x, 2, 180), -1),
                Channel(W, H, (x, y) => Rand(3, x, y, 30) - 5),
            },
        };
        var img = DecodeLikeLibjxl(Image(W, H, 3, Stream(NbColours + NbDeltas).Write), "delta palette");
        Assert.Equal((W, H, 3), (img.Width, img.Height, img.Channels));
        Assert.NotEqual(false, LibjxlRejects(Image(W, H, 3, Stream(NbColours).Write)));
    }

    /// <summary>H.6.4: a delta palette entry of a sample with more than 8 bits is shifted left by min(bitdepth, 24) - 8.</summary>
    [Fact]
    public void DeltaPaletteShiftStopsAt24Bits()
    {
        // index -> k = (-index - 1) Umod 143, kDeltaPalette[(k + 1) >> 1][0], negated for even k
        int[] index = { -2, -4, -11, -13, -15, -7 };
        int[] delta = { 4, 11, 10, 18, 27, 0 };
        var m = new Modular
        {
            // one colour and one delta entry (libjxl's layout, see above), predictor zero
            Transforms = w => { Transforms(w, 1); Palette(w, 0, 1, 1, 1, 0); },
            Channels = { Channel(2, 1, (_, _) => 0, -1), Channel(index.Length, 1, (x, _) => index[x]) },
        };
        var data = Image(index.Length, 1, 1, m.Write, bits: 26);
        Dump("delta palette 26 bit", data);
        var ours = (uint[])Jxl.Decode(data).Data;
        Assert.Equal(delta.Select(d => (uint)(d << 16)), ours);
        if (JxlEncodeTests.Libjxl(data) is { } lib)
            Assert.Equal(ours, ((float[])lib.Data).Select(v => (uint)Math.Round(v * ((1 << 26) - 1.0))));
    }

    /// <summary>
    /// H.6.4: the implicit colours below nb_colours + 64 take ((index &gt;&gt; 2c) Umod 4) per channel c, which is 0 for
    /// c &gt;= 3 (also where 2c &gt;= 64, a shift C# would mask).
    /// </summary>
    [Fact]
    public void ImplicitPaletteColourOfHighChannels()
    {
        const int N = 33, Bits = 8;
        var pal = new JxlChannel(1, N, -1, -1);
        var idx = new JxlChannel(1, 1, 0, 0);
        idx.Data[0] = 1 + 1;   // nb_colours + 1
        var outs = Enumerable.Range(0, N).Select(_ => new JxlChannel(1, 1, 0, 0)).ToArray();
        JxlModularTransforms.InversePalette(pal, idx, outs, 1, 0, 0, Bits, JxlWpHeader.Default);
        int bias = 1 << (Bits - 3), step = ((1 << Bits) - 1) / 4;
        Assert.Equal(step + bias, outs[0].Data[0]);
        for (int c = 1; c < N; c++) Assert.Equal(bias, outs[c].Data[0]);
    }

    // ------------------------------------------------------------------ H.6.2 / H.6.3 transforms of meta channels

    /// <summary>H.6.3: an RCT may work on three meta channels (here three single channel palettes).</summary>
    [Fact]
    public void RctOnMetaChannels()
    {
        const int W = 10, H = 6;
        var m = new Modular
        {
            Transforms = w => { Transforms(w, 4); Palette(w, 0, 1, 4, 0, 0); Palette(w, 2, 1, 4, 0, 0); Palette(w, 4, 1, 4, 0, 0); Rct(w, 0, 1); },
            Channels =
            {
                Channel(4, 1, (x, _) => 10 + 20 * x, -1), Channel(4, 1, (x, _) => 200 - 30 * x, -1), Channel(4, 1, (x, _) => 5 + 7 * x, -1),
                Channel(W, H, (x, y) => Rand(1, x, y, 4)), Channel(W, H, (x, y) => Rand(2, x, y, 4)), Channel(W, H, (x, y) => Rand(3, x, y, 4)),
            },
        };
        DecodeLikeLibjxl(Image(W, H, 3, m.Write), "rct on meta channels");
    }

    /// <summary>H.6.2.1: an in place squeeze of meta channels; its residuals become meta channels too.</summary>
    [Fact]
    public void SqueezeOnMetaChannels()
    {
        const int W = 10, H = 6;
        var m = new Modular
        {
            Transforms = w => { Transforms(w, 2); Palette(w, 0, 3, 8, 0, 0); Squeeze(w, (true, true, 0, 1)); },
            Channels =
            {
                Channel(4, 3, (x, c) => 60 + 30 * x + 10 * c, -1), Channel(4, 3, (x, c) => Rand(c, x, 7, 9) - 4, -1),
                Channel(W, H, (x, y) => Rand(4, x, y, 8)),
            },
        };
        DecodeLikeLibjxl(Image(W, H, 3, m.Write), "squeeze on meta channels");
    }

    // ------------------------------------------------------------------ H.4 MA trees, H.2 empty streams

    /// <summary>
    /// H.4.1: a property not defined for the channel reads as 0, whatever its index; the standard has no upper bound
    /// (libjxl rejects 300). The root tests property[k] &gt; -1, so every sample takes the left leaf (predictor zero).
    /// </summary>
    [Fact]
    public void MaTreePropertyAbove255ReadsZero()
    {
        foreach (int prop in new[] { 255, 300, 1 << 30 })
        {
            var tree = new JxlEncTree();
            int l = tree.AddLeaf(0), r = tree.AddLeaf(1);
            tree = tree.Finish(tree.AddSplit(prop, -1, l, r));
            var m = new Modular { Tree = tree, Channels = { Channel(8, 8, (x, y) => Rand(4, x, y, 256)) } };
            var img = Jxl.Decode(Image(8, 8, 1, m.Write));
            Assert.Equal(m.Channels[0].Data, Bytes(img).Select(b => (int)b));
        }
    }

    /// <summary>
    /// H.2: a GlobalModular stream none of whose channels is decoded there (all larger than a group). The text reads
    /// a local tree and starts the stream anyway; libjxl writes and reads nothing (and ignores what is there). We
    /// decode both forms, as libjxl does.
    /// </summary>
    [Fact]
    public void ModularStreamWithNothingToDecode()
    {
        const int W = 300, H = 8;
        var px = new int[W * H];
        for (int i = 0; i < px.Length; i++) px[i] = Rand(9, i % W, i / W, 256);
        byte[] Group(int x0, int w)
        {
            var g = new JxlBitWriter();
            new Modular { Tree = Modular.Leaf(5), Channels = { Channel(w, H, (x, y) => px[y * W + x0 + x]) } }.Write(g);
            return g.ToArray();
        }
        byte[] Stream(bool treeAndState) => ImageRaw(W, H, 1, w =>
        {
            w.WriteBool(false);   // no global tree
            w.WriteBool(false); w.WriteBool(true); NoTransforms(w);
            if (!treeAndState) return;
            var empty = new JxlTokens();
            var code = new JxlEntropyEncoder(1);
            code.Build(new[] { empty }, false);
            Modular.Leaf(0).Write(w);
            code.WriteCode(w);
            code.WriteTokens(w, empty);   // the initial state, 0x130000
        }, 8, Array.Empty<byte>(), Array.Empty<byte>(), Group(0, 256), Group(256, W - 256));
        foreach (bool full in new[] { false, true })
        {
            var img = DecodeLikeLibjxl(Stream(full), full ? "empty global modular with tree" : "empty global modular");
            Assert.Equal(px, Bytes(img).Select(b => (int)b));
        }
    }

    // ------------------------------------------------------------------ C.2.2 clustering

    /// <summary>
    /// C.2.2: in the code of a non-simple context map for num_dist == 2, lz77.enabled is false. libjxl reads the bit
    /// (a stream without it fails) and rejects a 1.
    /// </summary>
    [Fact]
    public void ContextMapOfTwoDistributionsHasNoLz77()
    {
        const int W = 8, H = 4;
        var tree = new JxlEncTree();
        int l = tree.AddLeaf(0), r = tree.AddLeaf(0);
        tree = tree.Finish(tree.AddSplit(3, 3, l, r));
        var px = Channel(W, H, (x, y) => x > 3 ? 200 + Rand(1, x, y, 50) : Rand(2, x, y, 4));
        // 0: LZ77 bit 0, 1: no LZ77 bit, 2: LZ77 enabled in the nested code
        byte[] Stream(int variant) => Image(W, H, 1, new Modular
        {
            Tree = tree,
            Channels = { px },
            Code = (w, e) => WriteCode(w, e, contextMap: bw =>
            {
                Assert.Equal(new byte[] { 0, 1 }, e.ContextMap);
                bw.WriteBool(false);   // not simple
                bw.WriteBool(false);   // no MTF
                var nested = new JxlEntropyEncoder(1) { MaxClusters = 1 };
                if (variant == 2) JxlLz77.Configure(nested);
                var tok = new JxlTokens();
                tok.Add(0, 0); tok.Add(0, 1);
                nested.Build(new[] { tok }, false);
                var nw = new JxlBitWriter();
                nested.WriteCode(nw);
                AppendBits(bw, nw, variant == 1 ? 1 : 0);
                nested.WriteTokens(bw, tok);
            }),
        }.Write);
        var img = DecodeLikeLibjxl(Stream(0), "context map of two, no lz77");
        Assert.Equal(px.Data, Bytes(img).Select(b => (int)b));
        foreach (int v in new[] { 1, 2 })
        {
            var bad = Stream(v);
            Assert.Throws<ImageFormatException>(() => Jxl.Decode(bad));
            Assert.NotEqual(false, LibjxlRejects(bad));
        }
    }

    /// <summary>
    /// C.2.2 states num_clusters &lt; 256, but libjxl decodes a context map of 256 clusters; so do we. Prefix codes of
    /// one symbol: every residual is 0.
    /// </summary>
    [Fact]
    public void ContextMapOf256Clusters()
    {
        foreach (int n in new[] { 255, 256 })
        {
            var m = new Modular
            {
                Tree = ColumnTree(n), AnsTokens = false,
                Channels = { Channel(n, 1, (_, _) => 0) },
                Code = (w, e) =>
                {
                    w.WriteBool(false);   // no LZ77
                    JxlEntropyEncoder.WriteContextMap(w, Enumerable.Range(0, n).Select(i => (byte)i).ToArray(), n);
                    w.WriteBool(true);    // prefix codes (log_alphabet_size 15)
                    for (int i = 0; i < n; i++) w.Write(4, 15);       // split_exponent 15
                    for (int i = 0; i < n; i++) w.WriteBool(false);   // count 1: no bits per symbol
                },
            };
            var img = DecodeLikeLibjxl(Image(n, 1, 1, m.Write), $"{n} clusters");
            Assert.All(Bytes(img), b => Assert.Equal(0, b));
        }
    }

    // ------------------------------------------------------------------ C.3.3 LZ77

    /// <summary>
    /// C.3.3: the window starts all zero and a copy distance is clamped to num_decoded, so a copy as the very first
    /// symbol of a stream is valid and yields zeros.
    /// </summary>
    [Fact]
    public void Lz77CopyBeforeAnySymbolYieldsZeros()
    {
        const int W = 8, H = 6, Copy = 5;
        var m = new Modular
        {
            Channels = { Channel(W, H, (x, y) => y * W + x < Copy ? 0 : 1 + Rand(1, x, y, 200)) },
            Configure = JxlLz77.Configure,
            Edit = t =>
            {
                // the first residuals (zeros) become one copy with distance code 0, (0, 1): a row up, clamped to 0
                var ctx = new List<int> { t.Ctx[0] | JxlTokens.LzFlag, 1 };
                var val = new List<uint> { Copy - JxlLz77.MinLength, 0 };
                for (int i = Copy; i < t.Count; i++) { ctx.Add(t.Ctx[i]); val.Add(t.Val[i]); }
                t.Ctx = ctx.ToArray(); t.Val = val.ToArray(); t.Count = ctx.Count;
            },
        };
        var img = DecodeLikeLibjxl(Image(W, H, 1, m.Write), "lz77 copy first");
        Assert.Equal(m.Channels[0].Data, Bytes(img).Select(b => (int)b));
    }

    /// <summary>C.3.3 bounds no copy length: a copy of 2^29 symbols whose first five end the stream.</summary>
    [Fact]
    public void Lz77CopyLongerThanTheStream()
    {
        const int W = 8, H = 6, Tail = 5;
        var m = new Modular
        {
            Channels = { Channel(W, H, (x, y) => y * W + x >= W * H - Tail - 1 ? 77 : Rand(2, x, y, 200)) },
            Configure = JxlLz77.Configure,
            Edit = t =>
            {
                // the last residuals repeat the one before: one copy of distance code 1, (1, 0) = the previous symbol
                int n = t.Count - Tail;
                var ctx = t.Ctx.Take(n).Append(t.Ctx[n] | JxlTokens.LzFlag).Append(1).ToArray();
                var val = t.Val.Take(n).Append((1u << 29) - JxlLz77.MinLength).Append(1u).ToArray();
                t.Ctx = ctx; t.Val = val; t.Count = ctx.Length;
            },
        };
        var img = DecodeLikeLibjxl(Image(W, H, 1, m.Write), "lz77 long copy");
        Assert.Equal(m.Channels[0].Data, Bytes(img).Select(b => (int)b));
    }

    // ------------------------------------------------------------------ C.2.5 ANS distributions

    /// <summary>
    /// C.2.5: D[omit_pos] = 4096 - total_count only has to be &gt;= 0; with the other entries summing to 4096 the
    /// omitted symbol gets probability 0 (and the alias table a zero entry). libjxl rejects that stream; the expected
    /// samples are the ones coded.
    /// </summary>
    [Fact]
    public void OmittedAnsEntryMayBeZero()
    {
        const int W = 64;
        int v = 0;
        var row = new int[W];
        for (int x = 0; x < W; x++) { v += v == 0 || Rand(5, x, 0, 3) == 0 ? 1 : -1; row[x] = v; }   // residuals +-1 under predictor W
        var m = new Modular
        {
            Tree = Modular.Leaf(1),
            Channels = { Channel(W, 1, (x, _) => row[x]) },
            Code = (w, e) => WriteCode(w, e, distribution: (bw, k) =>
            {
                var f = e.Freqs[k];
                Assert.True(f[0] == 0 && f[1] > 0 && f[2] > 0 && f[1] + f[2] == 4096);
                // symbol 0 omitted (log count 12, the largest), the others coded: D[0] = 4096 - 4096
                WriteComplexDistribution(bw, new[] { 0, f[1], f[2] }, new[] { 12, LogCount(f[1]), LogCount(f[2]) });
            }),
        };
        var data = Image(W, 1, 1, m.Write);
        Dump("ans omit zero", data);
        Assert.Equal(row, Bytes(Jxl.Decode(data)).Select(b => (int)b));
    }

    /// <summary>
    /// C.2.5 / C.2.6: a complex distribution whose alphabet_size exceeds 1 &lt;&lt; log_alphabet_size is rejected, even
    /// with the entries beyond the table zero (as libjxl does); the same distribution within the table decodes.
    /// </summary>
    [Fact]
    public void AnsAlphabetBeyondTheTableIsRejected()
    {
        const int W = 16, H = 8;
        int beyond = 0;
        var m = new Modular
        {
            Channels = { Channel(W, H, (x, y) => Rand(6, x, y, 20)) },
            Code = (w, e) => WriteCode(w, e, distribution: (bw, k) =>
            {
                var f = new int[(1 << e.LogAlphaSize) + beyond];
                Array.Copy(e.Freqs[k], f, 1 << e.LogAlphaSize);
                WriteComplexDistribution(bw, f, f.Select(LogCount).ToArray());
            }),
        };
        var img = DecodeLikeLibjxl(Image(W, H, 1, m.Write), "ans alphabet in table");
        Assert.Equal(m.Channels[0].Data, Bytes(img).Select(b => (int)b));
        beyond = 8;
        var bad = Image(W, H, 1, m.Write);
        Assert.Throws<ImageFormatException>(() => Jxl.Decode(bad));
        Assert.NotEqual(false, LibjxlRejects(bad));
    }

    // ------------------------------------------------------------------ permutations (TOC, coefficient orders)

    static int[] NaivePermutation(int[] lehmer)
    {
        var rest = Enumerable.Range(0, lehmer.Length).ToList();
        var perm = new int[lehmer.Length];
        for (int i = 0; i < lehmer.Length; i++) { perm[i] = rest[lehmer[i]]; rest.RemoveAt(lehmer[i]); }
        return perm;
    }

    [Fact]
    public void LehmerCodeToPermutation()
    {
        var rnd = new Random(7);
        foreach (int n in new[] { 0, 1, 2, 3, 7, 8, 64, 100, 1000, 4097 })
            for (int rep = 0; rep < 5; rep++)
            {
                var lehmer = new int[n];
                for (int i = 0; i < n; i++) lehmer[i] = rep == 0 ? 0 : rep == 1 ? n - 1 - i : rnd.Next(n - i);
                Assert.Equal(NaivePermutation(lehmer), JxlToc.LehmerToPermutation(lehmer));
            }
    }

    /// <summary>A permutation of 65536 entries (a TOC of that many sections) through its entropy coded Lehmer code.</summary>
    [Fact]
    public void LargePermutationDecodesFast()
    {
        const int N = 65536;
        var rnd = new Random(3);
        var lehmer = new int[N];
        for (int i = 0; i < N; i++) lehmer[i] = i < N / 2 ? 0 : rnd.Next(N - i);   // zeros: the most moving for a list
        static int Ctx(int x) => Math.Min(7, 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)x));
        var tok = new JxlTokens();
        tok.Add(Ctx(N), N);   // end
        for (int i = 0; i < N; i++) tok.Add(Ctx(i > 0 ? lehmer[i - 1] : 0), (uint)lehmer[i]);
        var enc = new JxlEntropyEncoder(8) { MaxClusters = 8 };
        enc.Build(new[] { tok }, false);
        var w = new JxlBitWriter();
        enc.WriteCode(w);
        enc.WriteTokens(w, tok);
        var br = new JxlBitReader(w.ToArray());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var code = JxlEntropyCode.Read(br, 8);
        var perm = JxlToc.DecodePermutation(br, new JxlSymbolReader(code, br, 0), N, 0);
        sw.Stop();
        Assert.Equal(NaivePermutation(lehmer), perm);
        output.WriteLine($"{N} entries in {sw.Elapsed.TotalMilliseconds:F1} ms");
        Assert.True(sw.Elapsed.TotalSeconds < 2, $"{sw.Elapsed.TotalMilliseconds} ms");

        // 2^18 entries, all zero: moving the rest of a list for each would take seconds (O(n^2)), a Fenwick tree ms
        sw.Restart();
        var id = JxlToc.LehmerToPermutation(new int[1 << 18]);
        sw.Stop();
        output.WriteLine($"{id.Length} entries in {sw.Elapsed.TotalMilliseconds:F1} ms");
        Assert.Equal(Enumerable.Range(0, 1 << 18), id);
        Assert.True(sw.Elapsed.TotalSeconds < 1, $"{sw.Elapsed.TotalMilliseconds} ms");
    }
}
