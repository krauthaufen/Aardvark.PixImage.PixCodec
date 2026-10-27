using System;
using System.Threading;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs
{
    public sealed partial class IccTransform
    {
        // tables cached per transform: input curve tables for 8 / 16 bit samples, output thresholds for 8 / 16 bit results
        readonly float[][]?[] inTables = new float[][]?[4];
        readonly Quantizer[]?[] outQuantizers = new Quantizer[]?[2];
        bool? outMonotonic;

        float[][] InputTables(int max, bool inverted)
        {
            ref float[][]? slot = ref inTables[(max == 255 ? 0 : 2) + (inverted ? 1 : 0)];
            var t = Volatile.Read(ref slot);
            if (t != null) return t;
            var first = Stages.Count > 0 ? Stages[0] as CurvesStage : null;
            t = new float[InputChannels][];
            for (int c = 0; c < InputChannels; c++)
            {
                var tc = new float[max + 1];
                var curve = first?.Curves[c];
                for (int i = 0; i <= max; i++)
                {
                    double v = (inverted ? max - i : i) / (double)max;
                    tc[i] = (float)(curve != null ? curve.Evaluate(v) : v);
                }
                t[c] = tc;
            }
            Volatile.Write(ref slot, t);
            return t;
        }

        /// <summary>
        /// Exact rounding of an inverse curve to integer codes: code k is the number of thresholds curve((j + 0.5) / max) at or
        /// below the value. A table over 0..1 narrows the search to the few codes of one cell.
        /// </summary>
        internal sealed class Quantizer
        {
            readonly float[] th;
            readonly int[] cell;
            readonly int n;

            public Quantizer(IccCurve curve, int max)
            {
                th = new float[max];
                for (int k = 0; k < max; k++) th[k] = (float)curve.Evaluate((k + 0.5) / max);
                n = max == 255 ? 4096 : 65536;
                cell = new int[n + 1];
                for (int i = 0, k = 0; i <= n; i++)
                {
                    float v = i / (float)n;
                    while (k < th.Length && th[k] <= v) k++;
                    cell[i] = k;
                }
            }

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            public int Code(float v)
            {
                int lo, hi;
                if (v >= 0 && v < 1)
                {
                    int i = (int)(v * n);
                    lo = cell[i]; hi = cell[i + 1];
                    if (lo == hi) return lo;
                }
                else if (v >= 1) { lo = cell[n]; hi = th.Length; }
                else if (v < 0) { lo = 0; hi = cell[0]; }
                else return 0; // NaN
                // first threshold above v within [lo, hi)
                while (lo < hi) { int m = (lo + hi) >> 1; if (th[m] <= v) lo = m + 1; else hi = m; }
                return lo;
            }
        }

        Quantizer[]? OutputQuantizers(int max)
        {
            if (Stages.Count == 0 || Stages[^1] is not InverseCurvesStage inv) return null;
            outMonotonic ??= Array.TrueForAll(inv.Curves, c => c.IsNonDecreasing());
            if (outMonotonic == false) return null;
            ref Quantizer[]? slot = ref outQuantizers[max == 255 ? 0 : 1];
            var t = Volatile.Read(ref slot);
            if (t != null) return t;
            t = new Quantizer[inv.Curves.Length];
            for (int c = 0; c < t.Length; c++) t[c] = new Quantizer(inv.Curves[c], max);
            Volatile.Write(ref slot, t);
            return t;
        }

        /// <summary>
        /// Converts an image whose colour channels match <see cref="Source"/> (plus an optional trailing alpha channel) to
        /// <paramref name="outType"/>. <paramref name="inverted"/>: the input samples are inverted (max = 0), as in Adobe CMYK JPEGs.
        /// <paramref name="whiteToWhite"/>: integer in and out take the white correction (<see cref="WhiteFix"/>).
        /// </summary>
        internal RawImage Apply(RawImage image, SampleType outType, bool inverted, bool clip, bool whiteToWhite)
        {
            int ni = InputChannels, no = OutputChannels;
            int sch = image.Channels;
            bool alpha;
            if (image.Color == ColorModel.Other) alpha = sch == ni + 1;
            else alpha = image.HasAlpha;
            int colour = alpha ? sch - 1 : sch;
            if (colour != ni)
                throw new ArgumentException($"the image has {colour} colour channels, the source profile ({Source.ColorSpace}) {ni}");
            if (image.SampleType is not (SampleType.UInt8 or SampleType.UInt16 or SampleType.Float16 or SampleType.Float32 or SampleType.Float64))
                throw new ArgumentException($"{image.SampleType} samples have no colour meaning; use UInt8, UInt16 or float samples");
            if (outType is not (SampleType.UInt8 or SampleType.UInt16 or SampleType.Float16 or SampleType.Float32))
                throw new ArgumentException("colour conversion produces UInt8, UInt16, Float16 or Float32", nameof(outType));
            int dch = no + (alpha ? 1 : 0);
            var color = no switch
            {
                1 => alpha ? ColorModel.GrayAlpha : ColorModel.Gray,
                3 => alpha ? ColorModel.Rgba : ColorModel.Rgb,
                _ => ColorModel.Other,
            };
            int w = image.Width, h = image.Height;
            var dst = RawImage.CreateArray(outType, checked(w * h * dch));
            var src = image.Data;

            int inMax = image.SampleType == SampleType.UInt8 ? 255 : image.SampleType == SampleType.UInt16 ? 65535 : 0;
            int outMax = outType == SampleType.UInt8 ? 255 : outType == SampleType.UInt16 ? 65535 : 0;
            bool firstIsCurves = Stages.Count > 0 && Stages[0] is CurvesStage;
            float[][]? inTab = inMax > 0 ? InputTables(inMax, inverted) : null;
            int stageStart = inTab != null && firstIsCurves ? 1 : 0;
            Quantizer[]? thresh = outMax > 0 ? OutputQuantizers(outMax) : null;
            int stageEnd = thresh != null ? Stages.Count - 1 : Stages.Count;
            float inScale = 1f, alphaIn = inMax > 0 ? 1f / inMax : 1f;
            float outScale = outMax > 0 ? outMax : 1f;

            long pixels = (long)w * h;
            const int chunk = 512;
            int blocks = (int)Math.Min((pixels + 16383) / 16384, Environment.ProcessorCount * 4L);
            if (blocks < 1) blocks = 1;
            long per = (pixels + blocks - 1) / blocks;
            // LittleCMS pins white in its integer transforms only, not in its float ones (WhiteFix)
            var fix = whiteToWhite && inMax > 0 && outMax > 0 ? whiteFix : null;
            Parallel.For(0, blocks, () => new Scratch(chunk, Stride, ni, fix != null), (blk, _, scratch) =>
            {
                var buf = scratch.Buf;
                long p0 = blk * per, p1 = Math.Min(pixels, p0 + per);
                int stride = Stride;
                for (long start = p0; start < p1; start += chunk)
                {
                    int cnt = (int)Math.Min(chunk, p1 - start);
                    int so = (int)(start * sch);
                    // load
                    switch (src)
                    {
                        case byte[] b:
                            for (int c = 0; c < ni; c++)
                            {
                                var tab = inTab![c];
                                for (int i = 0, q = so + c, p = c; i < cnt; i++, q += sch, p += stride) buf[p] = tab[b[q]];
                            }
                            break;
                        case ushort[] u:
                            for (int c = 0; c < ni; c++)
                            {
                                var tab = inTab![c];
                                for (int i = 0, q = so + c, p = c; i < cnt; i++, q += sch, p += stride) buf[p] = tab[u[q]];
                            }
                            break;
                        case Half[] hh:
                            for (int c = 0; c < ni; c++)
                                for (int i = 0, q = so + c, p = c; i < cnt; i++, q += sch, p += stride)
                                    buf[p] = inverted ? 1 - (float)hh[q] : (float)hh[q] * inScale;
                            break;
                        case float[] f:
                            for (int c = 0; c < ni; c++)
                                for (int i = 0, q = so + c, p = c; i < cnt; i++, q += sch, p += stride)
                                    buf[p] = inverted ? 1 - f[q] : f[q];
                            break;
                        case double[] d:
                            for (int c = 0; c < ni; c++)
                                for (int i = 0, q = so + c, p = c; i < cnt; i++, q += sch, p += stride)
                                    buf[p] = (float)(inverted ? 1 - d[q] : d[q]);
                            break;
                    }
                    // pixels next to white take the white correction (WhiteFix): they go through all the stages separately
                    int nfix = 0;
                    if (fix != null)
                    {
                        var raw = scratch.Raw!;
                        bool cmyk = Source.ColorSpace == IccColorSpace.Cmyk;
                        for (int i = 0; i < cnt; i++)
                        {
                            for (int c = 0; c < ni; c++) raw[c] = RawSample(src, so + i * sch + c, inMax, inverted);
                            float wt = WhiteWeight(raw, cmyk);
                            if (!(wt > 0)) continue;
                            Array.Copy(buf, i * stride, scratch.FixBuf!, nfix * stride, stride);
                            scratch.FixIndex![nfix] = i; scratch.FixWeight![nfix] = wt;
                            nfix++;
                        }
                    }
                    for (int s = stageStart; s < stageEnd; s++) Stages[s].Apply(buf, cnt, stride);
                    // store
                    int dob = (int)(start * dch);
                    for (int c = 0; c < no; c++)
                    {
                        if (thresh != null)
                        {
                            var qz = thresh[c];
                            if (dst is byte[] db8)
                                for (int i = 0, p = c, q = dob + c; i < cnt; i++, p += stride, q += dch) db8[q] = (byte)qz.Code(buf[p]);
                            else
                            {
                                var du16 = (ushort[])dst;
                                for (int i = 0, p = c, q = dob + c; i < cnt; i++, p += stride, q += dch) du16[q] = (ushort)qz.Code(buf[p]);
                            }
                            continue;
                        }
                        switch (dst)
                        {
                            case byte[] db:
                                for (int i = 0, p = c, q = dob + c; i < cnt; i++, p += stride, q += dch)
                                {
                                    float v = buf[p] * 255f + 0.5f;
                                    db[q] = v >= 255f ? (byte)255 : v > 0 ? (byte)v : (byte)0;
                                }
                                break;
                            case ushort[] du:
                                for (int i = 0, p = c, q = dob + c; i < cnt; i++, p += stride, q += dch)
                                {
                                    float v = buf[p] * 65535f + 0.5f;
                                    du[q] = v >= 65535f ? (ushort)65535 : v > 0 ? (ushort)v : (ushort)0;
                                }
                                break;
                            case Half[] dh:
                                for (int i = 0, p = c, q = dob + c; i < cnt; i++, p += stride, q += dch)
                                    dh[q] = (Half)(clip ? Math.Clamp(buf[p], 0f, 1f) : buf[p]);
                                break;
                            case float[] df:
                                for (int i = 0, p = c, q = dob + c; i < cnt; i++, p += stride, q += dch)
                                    df[q] = clip ? Math.Clamp(buf[p], 0f, 1f) : buf[p];
                                break;
                        }
                    }
                    if (nfix > 0)
                    {
                        var fb = scratch.FixBuf!;
                        for (int s = stageStart; s < Stages.Count; s++) Stages[s].Apply(fb, nfix, stride);
                        for (int k = 0; k < nfix; k++)
                            for (int c = 0; c < no; c++)
                                Store(dst, dob + scratch.FixIndex![k] * dch + c, fb[k * stride + c] + scratch.FixWeight![k] * fix![c]);
                    }
                    if (alpha) CopyAlpha(src, dst, so + sch - 1, dob + dch - 1, sch, dch, cnt, alphaIn, outScale, outMax > 0);
                }
                return scratch;
            }, _ => { });
            return new RawImage(w, h, color, dst, dch);
        }

        sealed class Scratch
        {
            public readonly float[] Buf;
            public readonly float[]? FixBuf, FixWeight, Raw;
            public readonly int[]? FixIndex;

            public Scratch(int chunk, int stride, int ni, bool fix)
            {
                Buf = new float[chunk * stride];
                if (!fix) return;
                FixBuf = new float[chunk * stride];
                FixWeight = new float[chunk];
                FixIndex = new int[chunk];
                Raw = new float[ni];
            }
        }

        /// <summary>An integer source sample normalised to 0..1 (after the inversion of inverted input).</summary>
        static float RawSample(Array src, int q, int max, bool inverted)
        {
            int v = src is byte[] b ? b[q] : ((ushort[])src)[q];
            return (inverted ? max - v : v) / (float)max;
        }

        /// <summary>Stores one device value rounded and clamped to an integer output.</summary>
        static void Store(Array dst, int q, float v)
        {
            if (dst is byte[] db) { float r = v * 255f + 0.5f; db[q] = r >= 255f ? (byte)255 : r > 0 ? (byte)r : (byte)0; }
            else { float r = v * 65535f + 0.5f; ((ushort[])dst)[q] = r >= 65535f ? (ushort)65535 : r > 0 ? (ushort)r : (ushort)0; }
        }

        static void CopyAlpha(Array src, Array dst, int so, int dob, int sch, int dch, int cnt, float inScale, float outScale, bool intOut)
        {
            for (int i = 0, q = so, r = dob; i < cnt; i++, q += sch, r += dch)
            {
                float a = src switch
                {
                    byte[] b => b[q] * inScale,
                    ushort[] u => u[q] * inScale,
                    Half[] h => (float)h[q],
                    float[] f => f[q],
                    double[] d => (float)d[q],
                    _ => 0,
                };
                switch (dst)
                {
                    case byte[] db: db[r] = (byte)Math.Clamp(MathF.Floor(a * outScale + 0.5f), 0, 255); break;
                    case ushort[] du: du[r] = (ushort)Math.Clamp(MathF.Floor(a * outScale + 0.5f), 0, 65535); break;
                    case Half[] dh: dh[r] = (Half)a; break;
                    case float[] df: df[r] = a; break;
                }
            }
        }
    }
}
