using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>Lossless modular frames: transforms (palette, channel palettes, RCT), MA tree, groups and sections.</summary>
    internal static class JxlLosslessEncoder
    {
        /// <param name="cs">Output writer.</param>
        /// <param name="chans">The channels of the frame.</param>
        /// <param name="h">The image header.</param>
        /// <param name="fh">The frame header.</param>
        /// <param name="lp">Encoder parameters.</param>
        /// <param name="parallel">Encode groups in parallel.</param>
        /// <param name="features">writes the patches / splines / noise that start LfGlobal, for the flags of
        /// <paramref name="fh"/> (only tests use it)</param>
        public static void EncodeFrame(JxlBitWriter cs, List<JxlChannel> chans, JxlImageHeader h, JxlFrameHeader fh, JxlLosslessParams lp, bool parallel,
            Action<JxlBitWriter>? features = null)
        {
            if (lp.TryPaletteBoth && lp.PaletteMaxColours > 0)
            {
                // encode with and without palette (on copies of the channel list) and keep the smaller frame
                var a = new JxlBitWriter(); var b = new JxlBitWriter();
                var copy = new List<JxlChannel>(chans);
                Encode(a, copy, h, fh, lp, parallel, allowPalette: true, features, out bool usedPalette);
                if (usedPalette)
                {
                    Encode(b, new List<JxlChannel>(chans), h, fh, lp, parallel, allowPalette: false, features, out _);
                    cs.Append(b.BitLength < a.BitLength ? b : a);
                }
                else cs.Append(a);
                return;
            }
            Encode(cs, chans, h, fh, lp, parallel, allowPalette: true, features, out _);
        }

        static void Encode(JxlBitWriter cs, List<JxlChannel> chans, JxlImageHeader h, JxlFrameHeader fh, JxlLosslessParams lp, bool parallel,
            bool allowPalette, Action<JxlBitWriter>? features, out bool usedPalette)
        {
            int nColour = h.Colour.IsGray ? 1 : 3;
            bool smallInts = !h.BitDepth.Float && h.BitDepth.Bits <= 16;
            int nbMeta = 0;
            var transforms = new List<JxlTransformEnc>();
            usedPalette = false;
            if (smallInts && allowPalette && lp.PaletteMaxColours > 0 && TryPalette(chans, lp.PaletteMaxColours, transforms, ref nbMeta))
                usedPalette = true;
            else
            {
                if (lp.ChannelPalette && (smallInts || h.BitDepth.Float && h.BitDepth.Bits == 16))
                {
                    int nOrig = chans.Count;
                    for (int c = 0; c < nOrig; c++) TryChannelPalette(chans, c + nbMeta, transforms, ref nbMeta);
                }
                if (nColour == 3)
                {
                    int rct = ChooseRct(chans, nbMeta, lp.RctMode);
                    if (rct != 0)
                    {
                        JxlForwardTransforms.Rct(chans, nbMeta, rct);
                        transforms.Add(new JxlTransformEnc { Kind = JxlTransformKind.Rct, BeginC = nbMeta, RctType = rct });
                    }
                }
            }

            JxlProfile.Mark("enc transforms");
            fh.GroupSizeShift = 1;
            var dims = JxlFrameDims.Of(fh);
            int gd = dims.GroupDim;
            int end = chans.Count;
            for (int i = nbMeta; i < chans.Count; i++)
                if (chans[i].W > gd || chans[i].H > gd) { end = i; break; }
            var streams = new JxlModularStreams();
            streams.Add(new JxlStreamImage(chans.GetRange(0, end), 0));
            int groupStream0 = 1 + 3 * dims.NumLfGroups + 17;
            var groupIndex = new int[dims.NumGroups];
            if (end < chans.Count)
            {
                var groupImages = new JxlStreamImage[dims.NumGroups];
                void Cut(int g)
                {
                    int gx = g % dims.GroupsX, gy = g / dims.GroupsX;
                    var sub = new List<JxlChannel>();
                    for (int c = end; c < chans.Count; c++)
                    {
                        var fc = chans[c];
                        int cx = (gx * gd) >> fc.HShift, cy = (gy * gd) >> fc.VShift;
                        int cw = Math.Min(gd >> fc.HShift, fc.W - cx), chh = Math.Min(gd >> fc.VShift, fc.H - cy);
                        if (cw <= 0 || chh <= 0) continue;
                        var s = new JxlChannel(cw, chh, fc.HShift, fc.VShift);
                        for (int y = 0; y < chh; y++) Array.Copy(fc.Data, (cy + y) * fc.W + cx, s.Data, y * cw, cw);
                        sub.Add(s);
                    }
                    groupImages[g] = new JxlStreamImage(sub, groupStream0 + g);
                }
                if (parallel) Parallel.For(0, dims.NumGroups, Cut); else for (int g = 0; g < dims.NumGroups; g++) Cut(g);
                for (int g = 0; g < dims.NumGroups; g++) groupIndex[g] = streams.Add(groupImages[g]);
            }
            else Array.Fill(groupIndex, -1);

            int maxCh = 0;
            foreach (var s in streams.Streams) maxCh = Math.Max(maxCh, s.Channels.Count);
            var tree = lp.FixedTree switch
            {
                1 => FixedTree(maxCh, h.BitDepth, wp: false),
                2 => FixedTree(maxCh, h.BitDepth, wp: true),
                _ => JxlTreeLearner.Learn(streams.Streams, lp.Tree, streams.Wp, parallel),
            };
            JxlProfile.Mark("enc tree");
            streams.Prepare(tree, lp, parallel);

            // sections
            var lfGlobal = new JxlBitWriter(1 << 12);
            features?.Invoke(lfGlobal);
            lfGlobal.WriteBool(true);   // default LF channel dequantization
            streams.WriteGlobal(lfGlobal);
            streams.WriteStream(lfGlobal, 0, transforms, alwaysHeader: true);
            var sections = new List<byte[]>();
            bool single = dims.NumGroups == 1;
            if (single) sections.Add(lfGlobal.ToArray());
            else
            {
                sections.Add(lfGlobal.ToArray());
                for (int i = 0; i < dims.NumLfGroups; i++) sections.Add(Array.Empty<byte>());
                sections.Add(Array.Empty<byte>());   // HF global
                var groups = new byte[dims.NumGroups][];
                void Write(int g)
                {
                    if (groupIndex[g] < 0) { groups[g] = Array.Empty<byte>(); return; }
                    var gw = new JxlBitWriter(4096);
                    streams.WriteStream(gw, groupIndex[g], Array.Empty<JxlTransformEnc>());
                    groups[g] = gw.ToArray();
                }
                if (parallel) Parallel.For(0, dims.NumGroups, Write); else for (int g = 0; g < dims.NumGroups; g++) Write(g);
                sections.AddRange(groups);
            }
            WriteFrame(cs, fh, h, sections);
            JxlProfile.Mark("enc write");
        }

        public static void WriteFrame(JxlBitWriter cs, JxlFrameHeader fh, JxlImageHeader h, List<byte[]> sections)
        {
            JxlHeaderWriter.WriteFrameHeader(cs, fh, h);
            var sizes = new int[sections.Count];
            for (int i = 0; i < sizes.Length; i++) sizes[i] = sections[i].Length;
            JxlHeaderWriter.WriteToc(cs, sizes);
            foreach (var s in sections) cs.AppendBytes(s);
        }

        // ------------------------------------------------------------------ fixed trees (efforts 1 to 3)

        static int Intervals(JxlEncTree t, int prop, int[] thr, int lo, int hi, Func<int, int> leaf)
        {
            if (lo == hi) return leaf(lo);
            int mid = (lo + hi) >> 1;
            int left = Intervals(t, prop, thr, mid + 1, hi, leaf);
            int right = Intervals(t, prop, thr, lo, mid, leaf);
            return t.AddSplit(prop, thr[mid], left, right);
        }

        internal static JxlEncTree FixedTree(int channels, JxlBitDepth bd, bool wp)
        {
            var t = new JxlEncTree();
            int s = bd.Float ? 1 : 1 << Math.Max(0, bd.Bits - 8);
            int[] Scaled(int[] a) { var r = new int[a.Length]; for (int i = 0; i < a.Length; i++) r[i] = a[i] * s; return r; }
            int Channel(int c)
            {
                if (wp)
                {
                    var thr = Scaled(new[] { -512, -256, -128, -64, -32, -16, -8, -3, 0, 2, 7, 15, 31, 63, 127, 255, 511 });
                    return Intervals(t, 15, thr, 0, thr.Length, _ => t.AddLeaf(6));
                }
                var g = Scaled(new[] { -12, -4, -1, 0, 3, 11 });
                return Intervals(t, 10, g, 0, g.Length, _ => Intervals(t, 12, g, 0, g.Length, _ => t.AddLeaf(5)));
            }
            var ch = new int[Math.Max(1, channels) - 1];
            for (int i = 0; i < ch.Length; i++) ch[i] = i;
            int root = Intervals(t, 0, ch, 0, ch.Length, Channel);
            return t.Finish(root);
        }

        // ------------------------------------------------------------------ palettes

        static bool TryPalette(List<JxlChannel> chans, int maxColours, List<JxlTransformEnc> transforms, ref int nbMeta)
        {
            int nc = chans.Count;
            if (nc > 4 || nc == 0) return false;
            int n = chans[0].Data.Length;
            var map = new Dictionary<ulong, int>();
            var src = new int[nc][];
            for (int c = 0; c < nc; c++) src[c] = chans[c].Data;
            ulong last = ulong.MaxValue;
            for (int i = 0; i < n; i++)
            {
                ulong key = 0;
                for (int c = 0; c < nc; c++) key = key << 16 | (uint)(ushort)src[c][i];
                if (key == last) continue;
                last = key;
                if (!map.ContainsKey(key))
                {
                    if (map.Count >= maxColours) return false;
                    map[key] = 0;
                }
            }
            int k = map.Count;
            // worth it only when the palette is small against the image
            if ((long)k * nc * 4 > n) return false;
            var colours = new int[k][];
            int j = 0;
            foreach (var key in map.Keys)
            {
                var col = new int[nc];
                ulong v = key;
                for (int c = nc - 1; c >= 0; c--) { col[c] = (int)(v & 0xFFFF); v >>= 16; }
                colours[j++] = col;
            }
            // sorted by luminance (then lexicographically) so that neighbouring indices are similar colours
            Array.Sort(colours, (a, b) =>
            {
                long la = Luma(a), lb = Luma(b);
                if (la != lb) return la.CompareTo(lb);
                for (int c = 0; c < a.Length; c++) if (a[c] != b[c]) return a[c].CompareTo(b[c]);
                return 0;
            });
            var index = new Dictionary<ulong, int>(k);
            for (int i = 0; i < k; i++)
            {
                ulong key = 0;
                for (int c = 0; c < nc; c++) key = key << 16 | (uint)(ushort)colours[i][c];
                index[key] = i;
            }
            JxlForwardTransforms.Palette(chans, 0, nc, colours, index, ref nbMeta);
            transforms.Add(new JxlTransformEnc { Kind = JxlTransformKind.Palette, BeginC = 0, NumC = nc, NbColours = k });
            return true;
        }

        static long Luma(int[] c) => c.Length >= 3 ? 299L * c[0] + 587L * c[1] + 114L * c[2] + (c.Length > 3 ? c[3] : 0) : c[0] * 1000L + (c.Length > 1 ? c[1] : 0);

        static void TryChannelPalette(List<JxlChannel> chans, int c, List<JxlTransformEnc> transforms, ref int nbMeta)
        {
            var d = chans[c].Data;
            if (d.Length < 64) return;
            int mn = int.MaxValue, mx = int.MinValue;
            foreach (int v in d) { if (v < mn) mn = v; if (v > mx) mx = v; }
            long range = (long)mx - mn + 1;
            if (range > 1 << 16 || range < 4) return;
            var seen = new bool[range];
            int distinct = 0;
            foreach (int v in d) if (!seen[v - mn]) { seen[v - mn] = true; distinct++; }
            if (distinct * 2 > range || distinct * 4 > d.Length || distinct > 70000) return;
            var values = new int[distinct];
            var lut = new int[range];
            int k = 0;
            for (int i = 0; i < range; i++) if (seen[i]) { lut[i] = k; values[k++] = i + mn; }
            int begin = c;
            JxlForwardTransforms.ChannelPalette(chans, begin, values, mn, lut, ref nbMeta);
            transforms.Add(new JxlTransformEnc { Kind = JxlTransformKind.Palette, BeginC = begin, NumC = 1, NbColours = distinct });
        }

        // ------------------------------------------------------------------ RCT choice

        static readonly int[] FewRcts = { 0, 6, 3, 4, 5, 1, 2 };

        /// <summary>The colour transform with the smallest estimated cost of gradient residuals on a sample of pixels.</summary>
        static int ChooseRct(List<JxlChannel> chans, int begin, int mode)
        {
            if (mode == 0) return 6;
            var c0 = chans[begin].Data; var c1 = chans[begin + 1].Data; var c2 = chans[begin + 2].Data;
            int w = chans[begin].W, h = chans[begin].H;
            if (w < 2 || h < 2) return 0;
            int step = Math.Max(1, (int)Math.Sqrt((double)w * h / (mode == 2 ? 12000 : 30000)));
            // the pixel and its W, N, NW neighbours of every sample, all three channels
            var sm = new List<int>();
            for (int y = 1; y < h; y += step)
                for (int x = 1; x < w; x += step)
                {
                    int i = y * w + x;
                    foreach (int j in new[] { i, i - 1, i - w, i - w - 1 }) { sm.Add(c0[j]); sm.Add(c1[j]); sm.Add(c2[j]); }
                }
            var sa = sm.ToArray();
            int ns = sa.Length / 12;
            int best = 0;
            double bestCost = double.MaxValue;
            int count = mode == 1 ? FewRcts.Length : 42;
            Span<long> hist = stackalloc long[3 * 72];
            Span<int> o = stackalloc int[12];
            Span<int> src = stackalloc int[3];
            for (int ci = 0; ci < count; ci++)
            {
                int rct = mode == 1 ? FewRcts[ci] : ci;
                int perm = rct / 7, type = rct % 7;
                int p0 = perm % 3, p1 = (perm + 1 + perm / 3) % 3, p2 = (perm + 2 - perm / 3) % 3;
                hist.Clear();
                double extra = 0;
                long total = ns;
                for (int si = 0; si < ns; si++)
                {
                    int sb = si * 12;
                    for (int q = 0; q < 4; q++)
                    {
                        int qb = sb + q * 3;
                        JxlForwardTransforms.Forward(type, sa[qb + p0], sa[qb + p1], sa[qb + p2], out o[q * 3], out o[q * 3 + 1], out o[q * 3 + 2]);
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        long pred = JxlEncPredict.ClampedGradient(o[3 + k], o[6 + k], o[9 + k]);
                        uint r = JxlBitWriter.PackSigned((int)(o[k] - pred));
                        int tok;
                        if (r < 16) tok = (int)r;
                        else
                        {
                            int e = 31 - System.Numerics.BitOperations.LeadingZeroCount(r);
                            tok = 16 + ((e - 4) << 1) + (int)((r >> (e - 1)) & 1);
                            extra += e - 1;
                        }
                        hist[k * 72 + tok]++;
                    }
                }
                double cost = extra;
                for (int k = 0; k < 3; k++) cost += JxlEncMath.Entropy(hist.Slice(k * 72, 72));
                if (cost < bestCost * 0.999) { bestCost = cost; best = rct; }
            }
            return best;
        }
    }
}
