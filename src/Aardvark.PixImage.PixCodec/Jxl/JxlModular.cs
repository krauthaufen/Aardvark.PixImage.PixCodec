using System;
using System.Collections.Generic;
using static Aardvark.ImageCodecs.JpegXl.JxlBitReader;

namespace Aardvark.ImageCodecs.JpegXl
{
    internal sealed class JxlChannel
    {
        public int W, H, HShift, VShift;
        public int[] Data;

        public JxlChannel(int w, int h, int hshift, int vshift, bool allocate = true)
        {
            W = w; H = h; HShift = hshift; VShift = vshift;
            Data = allocate && w > 0 && h > 0 ? new int[checked(w * h)] : Array.Empty<int>();
        }

        public void Allocate()
        {
            if (Data.Length != W * H) Data = W > 0 && H > 0 ? new int[checked(W * H)] : Array.Empty<int>();
        }

        public JxlChannel CloneShape(bool allocate) => new JxlChannel(W, H, HShift, VShift, allocate);
    }

    internal enum JxlTransformKind { Rct = 0, Palette = 1, Squeeze = 2 }

    internal struct JxlSqueezeParam
    {
        public bool Horizontal, InPlace;
        public int BeginC, NumC;
    }

    internal sealed class JxlTransform
    {
        public JxlTransformKind Kind;
        public int BeginC, RctType, NumC, NbColours, NbDeltas, DPred;
        public JxlSqueezeParam[] Squeeze = Array.Empty<JxlSqueezeParam>();

        public static JxlTransform Read(JxlBitReader br)
        {
            var t = new JxlTransform();
            uint k = br.ReadBits(2);
            if (k > 2) throw new ImageFormatException("JPEG XL: unknown modular transform");
            t.Kind = (JxlTransformKind)k;
            if (t.Kind != JxlTransformKind.Squeeze)
                t.BeginC = (int)br.ReadU32(Bits(3), BitsOffset(6, 8), BitsOffset(10, 72), BitsOffset(13, 1096));
            if (t.Kind == JxlTransformKind.Rct)
            {
                t.RctType = (int)br.ReadU32(Val(6), Bits(2), BitsOffset(4, 2), BitsOffset(6, 10));
                if (t.RctType >= 42) throw new ImageFormatException("JPEG XL: bad RCT type");
            }
            else if (t.Kind == JxlTransformKind.Palette)
            {
                t.NumC = (int)br.ReadU32(Val(1), Val(3), Val(4), BitsOffset(13, 1));
                t.NbColours = (int)br.ReadU32(BitsOffset(8, 0), BitsOffset(10, 256), BitsOffset(12, 1280), BitsOffset(16, 5376));
                t.NbDeltas = (int)br.ReadU32(Val(0), BitsOffset(8, 1), BitsOffset(10, 257), BitsOffset(16, 1281));
                t.DPred = (int)br.ReadBits(4);
                if (t.DPred >= 14) throw new ImageFormatException("JPEG XL: bad palette predictor");
            }
            else
            {
                int n = (int)br.ReadU32(Val(0), BitsOffset(4, 1), BitsOffset(6, 9), BitsOffset(8, 41));
                t.Squeeze = new JxlSqueezeParam[n];
                for (int i = 0; i < n; i++)
                {
                    t.Squeeze[i].Horizontal = br.ReadBool();
                    t.Squeeze[i].InPlace = br.ReadBool();
                    t.Squeeze[i].BeginC = (int)br.ReadU32(Bits(3), BitsOffset(6, 8), BitsOffset(10, 72), BitsOffset(13, 1096));
                    t.Squeeze[i].NumC = (int)br.ReadU32(Val(1), Val(2), Val(3), BitsOffset(4, 4));
                }
            }
            return t;
        }
    }

    internal sealed class JxlModularHeader
    {
        public bool UseGlobalTree;
        public JxlWpHeader Wp = JxlWpHeader.Default;
        public JxlTransform[] Transforms = Array.Empty<JxlTransform>();

        public static JxlModularHeader Read(JxlBitReader br)
        {
            var h = new JxlModularHeader { UseGlobalTree = br.ReadBool(), Wp = JxlWpHeader.Read(br) };
            int n = (int)br.ReadU32(Val(0), Val(1), BitsOffset(4, 2), BitsOffset(8, 18));
            h.Transforms = new JxlTransform[n];
            for (int i = 0; i < n; i++) h.Transforms[i] = JxlTransform.Read(br);
            return h;
        }
    }

    /// <summary>The MA tree and entropy code shared by the streams of a frame (GlobalModular).</summary>
    internal sealed class JxlModularGlobal
    {
        public JxlMaTree? Tree;
        public JxlEntropyCode? Code;
    }

    /// <summary>One modular image: header, channel list after the transforms, decoding and inverse transforms.</summary>
    internal sealed class JxlModularImage
    {
        public readonly List<JxlChannel> Channels;
        public int NbMetaChannels;
        public JxlModularHeader Header = new();
        public readonly int BitDepth;
        /// <summary>Squeeze parameters actually applied (defaults filled in), per transform.</summary>
        readonly List<JxlSqueezeParam[]> squeezeUsed = new();

        public JxlModularImage(List<JxlChannel> channels, int bitDepth)
        {
            Channels = channels;
            BitDepth = bitDepth;
        }

        /// <summary>
        /// Reads the header and the transforms of a modular image whose channels (before transforms) are given, then
        /// decodes the channels. With <paramref name="groupDim"/> &gt; 0 (GlobalModular) decoding stops at the first non meta
        /// channel larger than a group; <see cref="DecodedChannels"/> tells how far it got.
        /// </summary>
        public static JxlModularImage Decode(JxlBitReader br, List<JxlChannel> channels, int bitDepth, int streamId,
            JxlModularGlobal? global, int groupDim, long maxTreeNodes, bool allocate, out JxlModularHeader header)
        {
            var img = new JxlModularImage(channels, bitDepth);
            if (channels.Count == 0) { header = img.Header; return img; }   // nothing coded, not even the header
            header = img.Header = JxlModularHeader.Read(br);
            br.CheckEnd();
            foreach (var t in header.Transforms) img.ApplyMeta(t);
            if (allocate) foreach (var c in img.Channels) c.Allocate();
            img.DecodeChannels(br, streamId, global, groupDim, maxTreeNodes);
            return img;
        }

        public int DecodedChannels { get; private set; }

        void DecodeChannels(JxlBitReader br, int streamId, JxlModularGlobal? global, int groupDim, long maxTreeNodes)
        {
            int end = Channels.Count;
            if (groupDim > 0)
            {
                for (int i = NbMetaChannels; i < Channels.Count; i++)
                    if (Channels[i].W > groupDim || Channels[i].H > groupDim) { end = i; break; }
            }
            DecodedChannels = end;
            bool any = false;
            int distMul = 0;
            for (int i = 0; i < end; i++)
                if (Channels[i].W > 0 && Channels[i].H > 0) { any = true; distMul = Math.Max(distMul, Channels[i].W); }
            JxlMaTree tree;
            JxlEntropyCode code;
            if (Header.UseGlobalTree)
            {
                if (global?.Tree == null || global.Code == null) throw new ImageFormatException("JPEG XL: modular stream uses a global tree that is absent");
                tree = global.Tree; code = global.Code;
            }
            else
            {
                // H.2 reads the tree (and starts the stream) even with nothing to decode; libjxl and our encoder write
                // nothing then, and libjxl ignores whatever is there. Such a stream ends its section (GlobalModular, or a
                // group whose channels all have data), so skipping cannot lose sync with what follows.
                if (!any) return;
                // a local tree may have 1024 + (samples of the stream) nodes, up to 2^20 (18181-1:2024 Table M.1)
                long samples = 0;
                for (int i = 0; i < end; i++) samples += (long)Channels[i].W * Channels[i].H;
                tree = JxlMaTree.Read(br, Math.Max(maxTreeNodes, Math.Min(1L << 20, 1024 + samples)));
                code = JxlEntropyCode.Read(br, tree.NumLeaves);
            }
            if (!any) return;
            if (code.NumContexts < tree.NumLeaves) throw new ImageFormatException("JPEG XL: too few contexts for the MA tree");
            var reader = new JxlSymbolReader(code, br, distMul);
            for (int i = 0; i < end; i++)
            {
                var ch = Channels[i];
                if (ch.W == 0 || ch.H == 0) continue;
                JxlModularChannelDecoder.Decode(br, reader, tree.Specialize(i, streamId), Header.Wp, Channels, i);
            }
            reader.CheckFinalState();
            br.CheckEnd();
        }

        // ------------------------------------------------------------------ transforms, forward direction (channel list only)

        void ApplyMeta(JxlTransform t)
        {
            switch (t.Kind)
            {
                case JxlTransformKind.Rct:
                    {
                        int b = t.BeginC;
                        // H.6.3: three data channels, or three meta channels
                        if (b < NbMetaChannels ? b + 3 > NbMetaChannels : b + 3 > Channels.Count) throw new ImageFormatException("JPEG XL: RCT channels out of range");
                        var c0 = Channels[b];
                        for (int i = 1; i < 3; i++)
                        {
                            var c = Channels[b + i];
                            if (c.W != c0.W || c.H != c0.H || c.HShift != c0.HShift || c.VShift != c0.VShift)
                                throw new ImageFormatException("JPEG XL: RCT on channels of different size");
                        }
                        squeezeUsed.Add(Array.Empty<JxlSqueezeParam>());
                        break;
                    }
                case JxlTransformKind.Palette:
                    {
                        int b = t.BeginC, n = t.NumC;
                        if (n <= 0 || b + n > Channels.Count) throw new ImageFormatException("JPEG XL: palette channels out of range");
                        var c0 = Channels[b];
                        for (int i = 1; i < n; i++)
                        {
                            var c = Channels[b + i];
                            if (c.W != c0.W || c.H != c0.H || c.HShift != c0.HShift || c.VShift != c0.VShift)
                                throw new ImageFormatException("JPEG XL: palette over channels of different size");
                        }
                        if (b < NbMetaChannels)
                        {
                            if (b + n > NbMetaChannels) throw new ImageFormatException("JPEG XL: palette across meta and data channels");
                            NbMetaChannels += 2 - n;
                        }
                        else NbMetaChannels++;
                        Channels.RemoveRange(b + 1, n - 1);
                        long pw = (long)t.NbColours + t.NbDeltas;
                        if (pw > 1 << 24) throw new ImageFormatException("JPEG XL: palette too large");
                        Channels.Insert(0, new JxlChannel((int)pw, n, -1, -1, false));
                        squeezeUsed.Add(Array.Empty<JxlSqueezeParam>());
                        break;
                    }
                default:
                    {
                        var ps = t.Squeeze.Length > 0 ? t.Squeeze : DefaultSqueeze();
                        foreach (var p in ps)
                        {
                            int b = p.BeginC, e = p.BeginC + p.NumC - 1;
                            if (e >= Channels.Count || p.NumC <= 0) throw new ImageFormatException("JPEG XL: squeeze channels out of range");
                            int offset = p.InPlace ? e + 1 : Channels.Count;
                            // H.6.2.1: squeezing meta channels (in place only) adds the residuals to the meta channels
                            if (b < NbMetaChannels)
                            {
                                if (!p.InPlace || e >= NbMetaChannels) throw new ImageFormatException("JPEG XL: squeeze across meta and data channels");
                                NbMetaChannels += p.NumC;
                            }
                            for (int c = b; c <= e; c++)
                            {
                                var ch = Channels[c];
                                if (ch.HShift > 30 || ch.VShift > 30) throw new ImageFormatException("JPEG XL: squeezed too often");
                                JxlChannel res;
                                if (p.Horizontal)
                                {
                                    int w = ch.W;
                                    ch.W = (w + 1) / 2;
                                    if (ch.HShift >= 0) ch.HShift++;
                                    res = new JxlChannel(w - ch.W, ch.H, ch.HShift, ch.VShift, false);
                                }
                                else
                                {
                                    int h = ch.H;
                                    ch.H = (h + 1) / 2;
                                    if (ch.VShift >= 0) ch.VShift++;
                                    res = new JxlChannel(ch.W, h - ch.H, ch.HShift, ch.VShift, false);
                                }
                                Channels.Insert(offset + (c - b), res);
                            }
                        }
                        squeezeUsed.Add(ps);
                        break;
                    }
            }
        }

        JxlSqueezeParam[] DefaultSqueeze()
        {
            var list = new List<JxlSqueezeParam>();
            int first = NbMetaChannels, count = Channels.Count - first;
            if (count <= 0) return Array.Empty<JxlSqueezeParam>();
            int w = Channels[first].W, h = Channels[first].H;
            if (count > 2 && Channels[first + 1].W == w && Channels[first + 1].H == h)
            {
                list.Add(new JxlSqueezeParam { Horizontal = true, InPlace = false, BeginC = first + 1, NumC = 2 });
                list.Add(new JxlSqueezeParam { Horizontal = false, InPlace = false, BeginC = first + 1, NumC = 2 });
            }
            if (h >= w && h > 8)
            {
                list.Add(new JxlSqueezeParam { Horizontal = false, InPlace = true, BeginC = first, NumC = count });
                h = (h + 1) / 2;
            }
            while (w > 8 || h > 8)
            {
                if (w > 8)
                {
                    list.Add(new JxlSqueezeParam { Horizontal = true, InPlace = true, BeginC = first, NumC = count });
                    w = (w + 1) / 2;
                }
                if (h > 8)
                {
                    list.Add(new JxlSqueezeParam { Horizontal = false, InPlace = true, BeginC = first, NumC = count });
                    h = (h + 1) / 2;
                }
            }
            return list.ToArray();
        }

        // ------------------------------------------------------------------ inverse transforms

        public void Undo(bool parallel = false)
        {
            var tr = Header.Transforms;
            for (int i = tr.Length - 1; i >= 0; i--)
            {
                var t = tr[i];
                switch (t.Kind)
                {
                    case JxlTransformKind.Rct: JxlModularTransforms.InverseRct(Channels, t.BeginC, t.RctType, parallel); break;
                    case JxlTransformKind.Palette: UndoPalette(t, i); break;
                    default: UndoSqueeze(squeezeUsed[i]); break;
                }
            }
        }

        void UndoPalette(JxlTransform t, int index)
        {
            _ = index;
            UndoPaletteCore(t);
        }

        void UndoPaletteCore(JxlTransform t)
        {
            int b = t.BeginC, n = t.NumC;
            var pal = Channels[0];
            var idx = Channels[b + 1];
            var outs = new JxlChannel[n];
            for (int c = 0; c < n; c++) outs[c] = new JxlChannel(idx.W, idx.H, idx.HShift, idx.VShift);
            JxlModularTransforms.InversePalette(pal, idx, outs, t.NbColours, t.NbDeltas, t.DPred, BitDepth, Header.Wp);
            Channels.RemoveAt(b + 1);
            Channels.InsertRange(b + 1, outs);
            Channels.RemoveAt(0);
        }

        void UndoSqueeze(JxlSqueezeParam[] ps)
        {
            for (int k = ps.Length - 1; k >= 0; k--)
            {
                var p = ps[k];
                int b = p.BeginC, e = p.BeginC + p.NumC - 1;
                int offset = p.InPlace ? e + 1 : Channels.Count - p.NumC;
                for (int c = b; c <= e; c++)
                {
                    var ch = Channels[c];
                    var res = Channels[offset + (c - b)];
                    Channels[c] = p.Horizontal ? JxlModularTransforms.UnsqueezeH(ch, res) : JxlModularTransforms.UnsqueezeV(ch, res);
                }
                Channels.RemoveRange(offset, p.NumC);
            }
        }
    }
}
