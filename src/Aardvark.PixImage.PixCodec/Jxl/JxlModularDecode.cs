using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>Decodes the samples of one modular channel with its (specialized) MA tree.</summary>
    internal static unsafe class JxlModularChannelDecoder
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Predict(int predictor, long W, long N, long NW, long NE, long NN, long WW, long NEE, long wp)
        {
            switch (predictor)
            {
                case 0: return 0;
                case 1: return W;
                case 2: return N;
                case 3: return (W + N) / 2;
                case 4:
                    {
                        long p = W + N - NW;
                        return Math.Abs(p - W) < Math.Abs(p - N) ? W : N;
                    }
                case 5: return ClampedGradient(W, N, NW);
                case 6: return wp;
                case 7: return NE;
                case 8: return NW;
                case 9: return WW;
                case 10: return (W + NW) / 2;
                case 11: return (N + NW) / 2;
                case 12: return (N + NE) / 2;
                default: return (6 * N - 2 * NN + 7 * W + WW + NEE + 3 * NE + 8) / 16;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ClampedGradient(long W, long N, long NW)
        {
            long mn = Math.Min(W, N), mx = Math.Max(W, N);
            long g = W + N - NW;
            return g < mn ? mn : g > mx ? mx : g;
        }

        public static void Decode(JxlBitReader br, JxlSymbolReader reader, JxlMaTree tree, JxlWpHeader wpHeader, List<JxlChannel> channels, int index)
        {
            if (reader.CanFast)
            {
                fixed (byte* pd = br.Data)
                {
                    var fs = reader.BeginFast(br, pd);
                    Decode(ref fs, tree, wpHeader, channels, index);
                    reader.EndFast(fs, br, pd);
                }
            }
            else
            {
                var ss = new JxlSlowSymbols(reader, br);
                Decode(ref ss, tree, wpHeader, channels, index);
            }
        }

        static void Decode<TSym>(ref TSym reader, JxlMaTree tree, JxlWpHeader wpHeader, List<JxlChannel> channels, int index) where TSym : struct, IJxlSymbols
        {
            var ch = channels[index];
            if (tree.Property[0] < 0 && tree.Predictor[0] != 6)
            {
                DecodeSingleLeaf(ref reader, tree, ch);
                return;
            }
            DecodeGeneral(ref reader, tree, wpHeader, channels, index);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void DecodeSingleLeaf<TSym>(ref TSym reader, JxlMaTree tree, JxlChannel ch) where TSym : struct, IJxlSymbols
        {
            int w = ch.W, h = ch.H;
            var d = ch.Data;
            int ctx = tree.Context[0], pred = tree.Predictor[0];
            long mul = tree.Multiplier[0], off = tree.Offset[0];
            for (int y = 0; y < h; y++)
            {
                int row = y * w, prev = row - w;
                for (int x = 0; x < w; x++)
                {
                    long r = JxlBitReader.UnpackSigned(reader.ReadUint(ctx)) * mul + off;
                    long p;
                    switch (pred)
                    {
                        case 0: p = 0; break;
                        case 1: p = x > 0 ? d[row + x - 1] : y > 0 ? d[prev + x] : 0; break;
                        case 2: p = y > 0 ? d[prev + x] : x > 0 ? d[row + x - 1] : 0; break;
                        default:
                            {
                                Neighbours(d, w, x, y, out long W, out long N, out long NW, out long NE, out long NN, out long WW, out long NEE);
                                p = Predict(pred, W, N, NW, NE, NN, WW, NEE, 0);
                                break;
                            }
                    }
                    d[row + x] = (int)(r + p);
                }
                if (reader.Overrun) throw new ImageFormatException("JPEG XL: unexpected end of modular data");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Neighbours(int[] d, int w, int x, int y, out long W, out long N, out long NW, out long NE, out long NN, out long WW, out long NEE)
        {
            int i = y * w + x;
            if (y >= 2 && x >= 2 && x + 2 < w)
            {
                ref int r = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(d);
                W = Unsafe.Add(ref r, i - 1); WW = Unsafe.Add(ref r, i - 2);
                int n = i - w;
                N = Unsafe.Add(ref r, n); NW = Unsafe.Add(ref r, n - 1); NE = Unsafe.Add(ref r, n + 1); NEE = Unsafe.Add(ref r, n + 2);
                NN = Unsafe.Add(ref r, n - w);
                return;
            }
            W = x > 0 ? d[i - 1] : y > 0 ? d[i - w] : 0;
            N = y > 0 ? d[i - w] : W;
            NW = x > 0 && y > 0 ? d[i - w - 1] : W;
            NE = x + 1 < w && y > 0 ? d[i - w + 1] : N;
            NN = y > 1 ? d[i - 2 * w] : N;
            WW = x > 1 ? d[i - 2] : W;
            NEE = x + 2 < w && y > 0 ? d[i - w + 2] : NE;
        }

        /// <summary>
        /// Trees whose decisions all test one property: the leaves as intervals of that property (value &lt;= bounds[k] picks
        /// leaves[k], the last bound is int.MaxValue).
        /// </summary>
        static bool OnlyProperty(JxlMaTree t, int property, out int[] bounds, out int[] leaves)
        {
            bounds = leaves = Array.Empty<int>();
            for (int i = 0; i < t.Count; i++)
                if (t.Property[i] >= 0 && t.Property[i] != property) return false;
            if (t.Count > 4096) return false;
            var b = new List<int>(); var l = new List<int>();
            void Walk(int n, long lo, long hi)
            {
                if (lo > hi) return;
                if (t.Property[n] < 0) { b.Add((int)hi); l.Add(n); return; }
                long v = t.Value[n];
                Walk(t.Right[n], lo, Math.Min(hi, v));     // value <= v
                Walk(t.Left[n], Math.Max(lo, v + 1), hi);  // value > v
            }
            Walk(0, int.MinValue, int.MaxValue);
            bounds = b.ToArray(); leaves = l.ToArray();
            return true;
        }

        /// <summary>Weighted predictor trees that split on its maximum error only (lossless effort 3 and many DC images).</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void DecodeWpIntervals<TSym>(ref TSym readerRef, JxlMaTree tree, JxlWeightedPredictor wp, int[] d, int w, int h,
            int[] bounds, int[] leaves) where TSym : struct, IJxlSymbols
        {
            var reader = readerRef;   // a local copy the JIT can keep in registers
            int nl = leaves.Length;
            var lpred = new int[nl]; var loff = new long[nl]; var lmul = new long[nl]; var lctx = new int[nl];
            for (int k = 0; k < nl; k++)
            {
                lpred[k] = tree.Predictor[leaves[k]]; loff[k] = tree.Offset[leaves[k]];
                lmul[k] = tree.Multiplier[leaves[k]]; lctx[k] = tree.Context[leaves[k]];
            }
            if (typeof(TSym) == typeof(JxlAnsFast))
            {
                wp.DecodeIntervals(ref Unsafe.As<TSym, JxlAnsFast>(ref readerRef), d, h, bounds, lpred, lmul, loff, lctx);
                return;
            }
            for (int y = 0; y < h; y++)
            {
                wp.StartRow(y);
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    Neighbours(d, w, x, y, out long W, out long N, out long NW, out long NE, out long NN, out long WW, out long NEE);
                    long wpPred = wp.Predict(x, y, N, W, NE, NW, NN);
                    int me = wp.MaxError;
                    int k = 0;
                    while (me > bounds[k]) k++;
                    long r = JxlBitReader.UnpackSigned(reader.ReadUint(lctx[k])) * lmul[k] + loff[k];
                    long v = r + (lpred[k] == 6 ? wpPred : Predict(lpred[k], W, N, NW, NE, NN, WW, NEE, wpPred));
                    d[row + x] = (int)v;
                    wp.Update(x, (int)v);
                }
                if (reader.Overrun) throw new ImageFormatException("JPEG XL: unexpected end of modular data");
            }
            readerRef = reader;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void DecodeGeneral<TSym>(ref TSym reader, JxlMaTree tree, JxlWpHeader wpHeader, List<JxlChannel> channels, int index) where TSym : struct, IJxlSymbols
        {
            var ch = channels[index];
            int w = ch.W, h = ch.H;
            var d = ch.Data;
            int maxProp = tree.MaxProperty();
            bool useWp = tree.UsesWp();
            var wp = useWp ? new JxlWeightedPredictor(wpHeader, w) : null;
            // reference channels for properties 16+
            int numRefs = maxProp >= JxlMaTree.NumNonRefProperties ? (maxProp - JxlMaTree.NumNonRefProperties) / 4 + 1 : 0;
            var refs = new List<int[]>();
            for (int j = index - 1; j >= 0 && refs.Count < numRefs; j--)
            {
                var c = channels[j];
                if (c.W != w || c.H != h || c.HShift != ch.HShift || c.VShift != ch.VShift) continue;
                refs.Add(c.Data);
            }
            var prop = tree.Property; var val = tree.Value; var left = tree.Left; var right = tree.Right;
            var lpred = tree.Predictor; var loff = tree.Offset; var lmul = tree.Multiplier; var lctx = tree.Context;
            if (wp != null && OnlyProperty(tree, 15, out var bounds, out var leaves))
            {
                DecodeWpIntervals(ref reader, tree, wp, d, w, h, bounds, leaves);
                return;
            }
            for (int y = 0; y < h; y++)
            {
                wp?.StartRow(y);
                long lastProp9 = 0;
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    Neighbours(d, w, x, y, out long W, out long N, out long NW, out long NE, out long NN, out long WW, out long NEE);
                    long wpPred = 0;
                    if (wp != null) wpPred = wp.Predict(x, y, N, W, NE, NW, NN);
                    long prop9 = (int)(W + N - NW);
                    int n = 0;
                    while (prop[n] >= 0)
                    {
                        long pv;
                        int p = prop[n];
                        switch (p)
                        {
                            case 2: pv = y; break;
                            case 3: pv = x; break;
                            case 4: pv = Math.Abs(N); break;
                            case 5: pv = Math.Abs(W); break;
                            case 6: pv = N; break;
                            case 7: pv = W; break;
                            case 8: pv = (int)(W - lastProp9); break;
                            case 9: pv = prop9; break;
                            case 10: pv = W - NW; break;
                            case 11: pv = NW - N; break;
                            case 12: pv = N - NE; break;
                            case 13: pv = N - NN; break;
                            case 14: pv = W - WW; break;
                            case 15: pv = wp != null ? wp.MaxError : 0; break;
                            default:
                                {
                                    int k = p - JxlMaTree.NumNonRefProperties;
                                    int j = k >> 2;
                                    if (j >= refs.Count) { pv = 0; break; }
                                    var rd = refs[j];
                                    long rv = rd[row + x];
                                    if ((k & 2) != 0)
                                    {
                                        Neighbours(rd, w, x, y, out long rW, out long rN, out long rNW, out _, out _, out _, out _);
                                        rv -= ClampedGradient(rW, rN, rNW);
                                    }
                                    pv = (k & 1) != 0 ? rv : Math.Abs(rv);
                                    break;
                                }
                        }
                        // properties are 32 bit integers
                        n = (int)pv > val[n] ? left[n] : right[n];
                    }
                    lastProp9 = prop9;
                    long r = JxlBitReader.UnpackSigned(reader.ReadUint(lctx[n])) * (long)lmul[n] + loff[n];
                    long v = r + Predict(lpred[n], W, N, NW, NE, NN, WW, NEE, wpPred);
                    d[row + x] = (int)v;
                    wp?.Update(x, (int)v);
                }
                if (reader.Overrun) throw new ImageFormatException("JPEG XL: unexpected end of modular data");
            }
        }
    }

    internal static class JxlModularTransforms
    {
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void InverseRct(List<JxlChannel> channels, int begin, int rctType, bool parallel = false)
        {
            int perm = rctType / 7, type = rctType % 7;
            var c0 = channels[begin].Data; var c1 = channels[begin + 1].Data; var c2 = channels[begin + 2].Data;
            int n = c0.Length;
            if (type != 0) JxlColour.Chunked(n, parallel, (s, e) => Rct(c0, c1, c2, type, s, e));
            if (perm == 0) return;
            // the forward transform read its inputs as (ch[p0], ch[p1], ch[p2]); put them back
            var a = channels[begin]; var b = channels[begin + 1]; var c = channels[begin + 2];
            var arr = new JxlChannel[3];
            int p0 = perm % 3, p1 = (perm + 1 + perm / 3) % 3, p2 = (perm + 2 - perm / 3) % 3;
            arr[p0] = a; arr[p1] = b; arr[p2] = c;
            channels[begin] = arr[0]; channels[begin + 1] = arr[1]; channels[begin + 2] = arr[2];
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void Rct(int[] c0, int[] c1, int[] c2, int type, int s, int e)
        {
            switch (type)
            {
                case 1: for (int i = s; i < e; i++) c2[i] += c0[i]; break;
                case 2: for (int i = s; i < e; i++) c1[i] += c0[i]; break;
                case 3: for (int i = s; i < e; i++) { c1[i] += c0[i]; c2[i] += c0[i]; } break;
                case 4: for (int i = s; i < e; i++) c1[i] += (int)(((long)c0[i] + c2[i]) >> 1); break;
                case 5: for (int i = s; i < e; i++) { c1[i] += c0[i] + (c2[i] >> 1); c2[i] += c0[i]; } break;
                default:
                    for (int i = s; i < e; i++)
                    {
                        int Y = c0[i], Co = c1[i], Cg = c2[i];
                        int tmp = Y - (Cg >> 1);
                        int G = Cg + tmp;
                        int B = tmp - (Co >> 1);
                        int R = B + Co;
                        c0[i] = R; c1[i] = G; c2[i] = B;
                    }
                    break;
            }
        }

        static readonly short[] DeltaPalette =
        {
            0, 0, 0, 4, 4, 4, 11, 0, 0, 0, 0, -13, 0, -12, 0, -10, -10, -10,
            -18, -18, -18, -27, -27, -27, -18, -18, 0, 0, 0, -32, -32, 0, 0, -37, -37, -37,
            0, -32, -32, 24, 24, 45, 50, 50, 50, -45, -24, -24, -24, -45, -45, 0, -24, -24,
            -34, -34, 0, -24, 0, -24, -45, -45, -24, 64, 64, 64, -32, 0, -32, 0, -32, 0,
            -32, 0, 32, -24, -45, -24, 45, 24, 45, 24, -24, -45, -45, -24, 24, 80, 80, 80,
            64, 0, 0, 0, 0, -64, 0, -64, -64, -24, -24, 45, 96, 96, 96, 64, 64, 0,
            45, -24, -24, 34, -34, 0, 112, 112, 112, 24, -45, -45, 45, 45, -24, 0, -32, 32,
            24, -24, 45, 0, 96, 96, 45, -24, 24, 24, -45, -24, -24, -45, 24, 0, -64, 0,
            96, 0, 0, 128, 128, 128, 64, 0, 64, 144, 144, 144, 96, 96, 0, -36, -36, 36,
            45, -24, -45, 45, -45, -24, 0, 0, -96, 0, 128, 128, 0, 96, 0, 45, 24, -45,
            -128, 0, 0, 24, -45, 24, -45, 24, -45, 64, 0, -64, 64, -64, -64, 96, 0, 96,
            45, -45, 24, 24, 45, -45, 64, 64, -64, 128, 128, 0, 0, 0, -128, -24, 45, -45,
        };

        static long PaletteValue(JxlChannel pal, int nbTotal, long index, int c, int bitDepth)
        {
            if (index >= 0 && index < nbTotal) return pal.Data[c * pal.W + (int)index];
            if (index >= nbTotal)
            {
                index -= nbTotal;
                if (index < 64)   // (index >> 2c) Umod 4 is 0 from c = 3 on (C# would mask a shift by 64 to 0)
                    return (c < 3 ? (index >> (2 * c)) % 4 : 0) * ((1L << bitDepth) - 1) / 4 + (1L << Math.Max(0, bitDepth - 3));
                index -= 64;
                for (int i = 0; i < c; i++) index /= 5;
                return (index % 5) * ((1L << bitDepth) - 1) / 4;
            }
            if (c >= 3) return 0;
            index = -(index + 1);
            index %= 143;
            long r = DeltaPalette[((index + 1) >> 1) * 3 + c] * ((index & 1) != 0 ? 1 : -1);
            if (bitDepth > 8) r <<= Math.Min(bitDepth, 24) - 8;
            return r;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void InversePalette(JxlChannel pal, JxlChannel idx, JxlChannel[] outs, int nbColours, int nbDeltas, int dPred, int bitDepth, JxlWpHeader wpHeader)
        {
            int w = idx.W, h = idx.H, n = outs.Length;
            int nbTotal = pal.W;
            var id = idx.Data;
            bool anyDelta = false;
            for (int i = 0; i < id.Length; i++) if (id[i] < nbDeltas) { anyDelta = true; break; }
            if (!anyDelta)
            {
                for (int c = 0; c < n; c++)
                {
                    var o = outs[c].Data;
                    if (c < pal.H)
                    {
                        int pr = c * pal.W;
                        var pd = pal.Data;
                        for (int i = 0; i < id.Length; i++)
                        {
                            int v = id[i];
                            o[i] = (uint)v < (uint)nbTotal ? pd[pr + v] : (int)PaletteValue(pal, nbTotal, v, c, bitDepth);
                        }
                    }
                }
                return;
            }
            for (int c = 0; c < n; c++)
            {
                var o = outs[c].Data;
                var wp = dPred == 6 ? new JxlWeightedPredictor(wpHeader, w) : null;
                for (int y = 0; y < h; y++)
                {
                    wp?.StartRow(y);
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        long v = id[i];
                        long val = PaletteValue(pal, nbTotal, v, c, bitDepth);
                        if (v < nbDeltas)
                        {
                            JxlModularChannelDecoder.Neighbours(o, w, x, y, out long W, out long N, out long NW, out long NE, out long NN, out long WW, out long NEE);
                            long wpv = wp != null ? wp.Predict(x, y, N, W, NE, NW, NN) : 0;
                            val += JxlModularChannelDecoder.Predict(dPred, W, N, NW, NE, NN, WW, NEE, wpv);
                        }
                        else if (wp != null)
                        {
                            JxlModularChannelDecoder.Neighbours(o, w, x, y, out long W, out long N, out long NW, out long NE, out long NN, out _, out _);
                            wp.Predict(x, y, N, W, NE, NW, NN);
                        }
                        o[i] = (int)val;
                        wp?.Update(x, (int)val);
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static long SmoothTendency(long B, long a, long n)
        {
            long diff = 0;
            if (B >= a && a >= n)
            {
                diff = (4 * B - 3 * n - a + 6) / 12;
                if (diff - (diff & 1) > 2 * (B - a)) diff = 2 * (B - a) + 1;
                if (diff + (diff & 1) > 2 * (a - n)) diff = 2 * (a - n);
            }
            else if (B <= a && a <= n)
            {
                diff = (4 * B - 3 * n - a - 6) / 12;
                if (diff + (diff & 1) < 2 * (B - a)) diff = 2 * (B - a) - 1;
                if (diff - (diff & 1) < 2 * (a - n)) diff = 2 * (a - n);
            }
            return diff;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static JxlChannel UnsqueezeH(JxlChannel avg, JxlChannel res)
        {
            int w = avg.W + res.W, h = avg.H;
            if (res.H != h || res.W > avg.W || avg.W > res.W + 1) throw new ImageFormatException("JPEG XL: squeeze residual of wrong size");
            var o = new JxlChannel(w, h, avg.HShift >= 0 ? avg.HShift - 1 : avg.HShift, avg.VShift);
            var od = o.Data; var ad = avg.Data; var rd = res.Data;
            int aw = avg.W, rw = res.W;
            for (int y = 0; y < h; y++)
            {
                int ar = y * aw, rr = y * rw, orow = y * w;
                for (int x = 0; x < rw; x++)
                {
                    long a = ad[ar + x];
                    long next = x + 1 < aw ? ad[ar + x + 1] : a;
                    long left = x > 0 ? od[orow + 2 * x - 1] : a;
                    long diff = rd[rr + x] + SmoothTendency(left, a, next);
                    long A = a + diff / 2;
                    od[orow + 2 * x] = (int)A;
                    od[orow + 2 * x + 1] = (int)(A - diff);
                }
                if (aw > rw) od[orow + 2 * rw] = ad[ar + rw];
            }
            return o;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static JxlChannel UnsqueezeV(JxlChannel avg, JxlChannel res)
        {
            int w = avg.W, h = avg.H + res.H;
            if (res.W != w || res.H > avg.H || avg.H > res.H + 1) throw new ImageFormatException("JPEG XL: squeeze residual of wrong size");
            var o = new JxlChannel(w, h, avg.HShift, avg.VShift >= 0 ? avg.VShift - 1 : avg.VShift);
            var od = o.Data; var ad = avg.Data; var rd = res.Data;
            int ah = avg.H, rh = res.H;
            for (int y = 0; y < rh; y++)
            {
                int ar = y * w, an = (y + 1 < ah ? y + 1 : y) * w, rr = y * w, o0 = 2 * y * w, o1 = o0 + w, op = o0 - w;
                for (int x = 0; x < w; x++)
                {
                    long a = ad[ar + x];
                    long next = ad[an + x];
                    long top = y > 0 ? od[op + x] : a;
                    long diff = rd[rr + x] + SmoothTendency(top, a, next);
                    long A = a + diff / 2;
                    od[o0 + x] = (int)A;
                    od[o1 + x] = (int)(A - diff);
                }
            }
            if (ah > rh) Array.Copy(ad, rh * w, od, 2 * rh * w, w);
            return o;
        }
    }
}
