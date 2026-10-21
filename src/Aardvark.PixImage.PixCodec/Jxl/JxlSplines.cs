using System;
using System.Collections.Generic;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>Splines: centripetal Catmull-Rom curves drawn with a Gaussian brush whose colour and width vary along them.</summary>
    internal sealed class JxlSplines
    {
        sealed class Spline
        {
            public readonly List<(double X, double Y)> Points = new();
            public readonly float[][] Colour = { new float[32], new float[32], new float[32] };
            public readonly float[] Sigma = new float[32];
            /// <summary>For the area estimate of 18181-1:2024 K.4.1: sums of the dequantized magnitudes (rounded up) per
            /// channel, the sum of the squared sigma weights and the Manhattan distance of the control points so far.</summary>
            public double ColourX, ColourY, ColourB, SigmaWeights, Manhattan;
        }

        readonly List<Spline> splines = new();
        static readonly float[] ChannelWeight = { 0.0042f, 0.075f, 0.07f, 0.3333f };
        const float Sqrt2 = 1.41421356237f;

        public static JxlSplines Read(JxlBitReader br, long numPixels)
        {
            var code = JxlEntropyCode.Read(br, 6);
            var r = new JxlSymbolReader(code, br, 0);
            uint num = r.ReadUint(br, 2) + 1;
            long maxSplines = Math.Min(1L << 24, numPixels / 4 + 16);
            if (num > maxSplines) throw new ImageFormatException("JPEG XL: too many splines");
            var starts = new (long x, long y)[num];
            long lx = 0, ly = 0;
            for (int i = 0; i < num; i++)
            {
                long x = r.ReadUint(br, 1), y = r.ReadUint(br, 1);
                if (i != 0) { x = JxlBitReader.UnpackSigned((uint)x) + lx; y = JxlBitReader.UnpackSigned((uint)y) + ly; }
                starts[i] = (x, y);
                lx = x; ly = y;
            }
            int qa = JxlBitReader.UnpackSigned(r.ReadUint(br, 0));
            float invQuant = qa >= 0 ? 1f / (1f + 0.125f * qa) : 1f - 0.125f * qa;
            // |q| divided by the quantization factor (1 + qa / 8, or 1 / (1 - qa / 8)), exact where the result is an integer
            double Dequant(int q) => qa >= 0 ? Math.Abs((double)q) / (1 + qa / 8.0) : Math.Abs((double)q) * (1 - qa / 8.0);
            var s = new JxlSplines();
            long totalPoints = 0;
            double manhattan = 0;   // over all splines so far, as K.4.1 accumulates it
            for (int i = 0; i < num; i++)
            {
                var sp = new Spline();
                uint np = r.ReadUint(br, 3);
                totalPoints += np;
                if (totalPoints > Math.Min(1L << 20, numPixels / 2 + 64)) throw new ImageFormatException("JPEG XL: too many spline control points");
                long cx = starts[i].x, cy = starts[i].y, dx = 0, dy = 0;
                sp.Points.Add((cx, cy));
                for (int j = 0; j < np; j++)
                {
                    dx += JxlBitReader.UnpackSigned(r.ReadUint(br, 4));
                    dy += JxlBitReader.UnpackSigned(r.ReadUint(br, 4));
                    cx += dx; cy += dy;
                    manhattan += Math.Abs(dx) + Math.Abs(dy);
                    if (Math.Abs(cx) > 1L << 30 || Math.Abs(cy) > 1L << 30) throw new ImageFormatException("JPEG XL: spline out of range");
                    sp.Points.Add((cx, cy));
                }
                sp.Manhattan = manhattan;
                double[] sums = new double[3];
                for (int c = 0; c < 3; c++)
                {
                    for (int k = 0; k < 32; k++)
                    {
                        int q = JxlBitReader.UnpackSigned(r.ReadUint(br, 5));
                        sums[c] += Math.Ceiling(Dequant(q));
                        sp.Colour[c][k] = q * (k == 0 ? 1 / Sqrt2 : 1) * invQuant * ChannelWeight[c];
                    }
                }
                (sp.ColourX, sp.ColourY, sp.ColourB) = (sums[0], sums[1], sums[2]);
                for (int k = 0; k < 32; k++)
                {
                    int q = JxlBitReader.UnpackSigned(r.ReadUint(br, 5));
                    double weight = Math.Max(1, Math.Ceiling(Dequant(q)));
                    sp.SigmaWeights += weight * weight;
                    sp.Sigma[k] = q * (k == 0 ? 1 / Sqrt2 : 1) * invQuant * ChannelWeight[3];
                }
                s.splines.Add(sp);
                if (br.Overrun) throw new ImageFormatException("JPEG XL: truncated splines");
            }
            r.CheckFinalState();
            return s;
        }

        static float ContinuousIdct(float[] dct, float t)
        {
            double r = 0;
            for (int i = 0; i < 32; i++) r += Sqrt2 * dct[i] * Math.Cos(i * (Math.PI / 32) * (t + 0.5));
            return (float)r;
        }

        static List<(double X, double Y)> CatmullRom(List<(double X, double Y)> pts)
        {
            var res = new List<(double X, double Y)>();
            if (pts.Count == 1) { res.Add(pts[0]); return res; }
            var ext = new List<(double X, double Y)>(pts.Count + 2)
            {
                (2 * pts[0].X - pts[1].X, 2 * pts[0].Y - pts[1].Y),
            };
            ext.AddRange(pts);
            int last = pts.Count - 1;
            ext.Add((2 * pts[last].X - pts[last - 1].X, 2 * pts[last].Y - pts[last - 1].Y));
            const int NumPoints = 16;
            Span<double> d = stackalloc double[3];
            Span<double> t = stackalloc double[4];
            Span<double> ax = stackalloc double[3], ay = stackalloc double[3];
            Span<double> bx = stackalloc double[2], by = stackalloc double[2];
            for (int start = 0; start + 3 < ext.Count; start++)
            {
                res.Add(ext[start + 1]);
                t[0] = 0;
                for (int k = 0; k < 3; k++)
                {
                    var a0 = ext[start + k]; var a1 = ext[start + k + 1];
                    d[k] = Math.Sqrt(Math.Sqrt((a1.X - a0.X) * (a1.X - a0.X) + (a1.Y - a0.Y) * (a1.Y - a0.Y)));
                    t[k + 1] = t[k] + d[k];
                }
                for (int i = 1; i < NumPoints; i++)
                {
                    double tt = d[0] + (double)i / NumPoints * d[1];
                    for (int k = 0; k < 3; k++)
                    {
                        var p0 = ext[start + k]; var p1 = ext[start + k + 1];
                        double f = (tt - t[k]) / d[k];
                        ax[k] = p0.X + f * (p1.X - p0.X);
                        ay[k] = p0.Y + f * (p1.Y - p0.Y);
                    }
                    for (int k = 0; k < 2; k++)
                    {
                        double f = (tt - t[k]) / (d[k] + d[k + 1]);
                        bx[k] = ax[k] + f * (ax[k + 1] - ax[k]);
                        by[k] = ay[k] + f * (ay[k + 1] - ay[k]);
                    }
                    double g = (tt - t[1]) / d[1];
                    res.Add((bx[0] + g * (bx[1] - bx[0]), by[0] + g * (by[1] - by[0])));
                }
            }
            res.Add(pts[last]);
            return res;
        }

        /// <summary>Points spaced by arc length 1 along the polyline, with the length each one stands for.</summary>
        static List<((double X, double Y) P, double M)> EquallySpaced(List<(double X, double Y)> pts)
        {
            var o = new List<((double, double), double)>();
            var current = pts[0];
            o.Add((current, 1.0));
            int next = 0;
            while (next < pts.Count)
            {
                var previous = current;
                double fromPrevious = 0;
                while (true)
                {
                    if (next == pts.Count) { o.Add((previous, fromPrevious)); return o; }
                    var nx = pts[next];
                    double toNext = Math.Sqrt((nx.X - previous.X) * (nx.X - previous.X) + (nx.Y - previous.Y) * (nx.Y - previous.Y));
                    if (fromPrevious + toNext >= 1.0)
                    {
                        double f = (1.0 - fromPrevious) / toNext;
                        current = (previous.X + f * (nx.X - previous.X), previous.Y + f * (nx.Y - previous.Y));
                        o.Add((current, 1.0));
                        break;
                    }
                    fromPrevious += toNext;
                    previous = nx;
                    next++;
                }
                if (o.Count > 1 << 22) throw new ImageNotSupportedException("JPEG XL: spline too long");
            }
            return o;
        }

        static float Erf(double x) => (float)ErfD(x);

        static double ErfD(double x)
        {
            // Abramowitz-Stegun 7.1.26 is too coarse; use a series / continued fraction precise to 1e-7
            double t = 1.0 / (1.0 + 0.5 * Math.Abs(x));
            double y = 1 - t * Math.Exp(-x * x - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 + t * (-0.18628806 +
                t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))));
            return x >= 0 ? y : -y;
        }

        /// <summary>
        /// The estimated area the splines reach (18181-1:2024 K.4.1), which level 10 limits to
        /// min(1024 w h + 2^32, 2^42) (Table M.1); a frame beyond that is not conforming and is rejected before drawing.
        /// </summary>
        public double EstimatedArea(float ytox, float ytob)
        {
            double area = 0;
            foreach (var sp in splines)
            {
                double cx = sp.ColourX + Math.Ceiling(Math.Abs(ytox)) * sp.ColourY;
                double cb = sp.ColourB + Math.Ceiling(Math.Abs(ytob)) * sp.ColourY;
                double logColour = Math.Max(1, Math.Ceiling(Math.Log2(1 + Math.Max(cx, Math.Max(sp.ColourY, cb)))));
                area += sp.SigmaWeights * logColour * sp.Manhattan;
            }
            return area;
        }

        /// <summary>Adds the splines to the three colour planes of the frame.</summary>
        public void Draw(float[][] planes, int w, int h, float ytox, float ytob)
        {
            double maxArea = Math.Min(1024.0 * w * h + 4294967296.0, 4398046511104.0);
            if (EstimatedArea(ytox, ytob) > maxArea) throw new ImageFormatException("JPEG XL: splines exceed the level 10 area limit");
            foreach (var sp in splines)
            {
                // chroma from luma with the frame's base correlations
                var cx0 = new float[32]; var cx2 = new float[32];
                for (int k = 0; k < 32; k++)
                {
                    cx0[k] = sp.Colour[0][k] + ytox * sp.Colour[1][k];
                    cx2[k] = sp.Colour[2][k] + ytob * sp.Colour[1][k];
                }
                var curve = CatmullRom(sp.Points);
                var pts = EquallySpaced(curve);
                double arcLength = (pts.Count - 2) * 1.0 + pts[pts.Count - 1].M;
                if (!(arcLength > 0)) continue;
                for (int i = 0; i < pts.Count; i++)
                {
                    float progress = (float)Math.Min(1.0, i / arcLength);
                    float t = 31 * progress;
                    float c0 = ContinuousIdct(cx0, t), c1 = ContinuousIdct(sp.Colour[1], t), c2 = ContinuousIdct(cx2, t);
                    float sigma = ContinuousIdct(sp.Sigma, t);
                    float intensity = (float)pts[i].M;
                    if (!(float.IsFinite(sigma) && sigma != 0 && float.IsFinite(1 / sigma))) continue;
                    // K.4.2 sums over all pixels. The brush is a box of half width sqrt(1/2) blurred by sigma: at distance
                    // 2 (sqrt(1/8) + z |sigma|) a sample gets less than amplitude * exp(-2 z^2) / 4, kept below 1e-7.
                    // (A cut-off at a few sigma alone, as libjxl has, loses up to ~0.5 % of the brush, much more where
                    // sigma is small.)
                    float maxColour = Math.Max(Math.Abs(c0), Math.Max(Math.Abs(c1), Math.Abs(c2))) * intensity;
                    double amplitude = maxColour * Math.Abs(sigma);
                    if (!(amplitude > 4e-7)) continue;
                    double maxDist = 2 * (0.353553391 + Math.Sqrt(Math.Log(amplitude / 4e-7) / 2) * Math.Abs(sigma));
                    if (!double.IsFinite(maxDist)) continue;
                    var (cx, cy) = pts[i].P;
                    float invSigma = 1 / sigma;
                    float s4 = 0.25f * sigma * intensity;
                    int y0 = (int)Math.Max(0, Math.Floor(cy - maxDist + 0.5)), y1 = (int)Math.Min(h, Math.Floor(cy + maxDist + 1.5));
                    int x0 = (int)Math.Max(0, Math.Floor(cx - maxDist + 0.5)), x1 = (int)Math.Min(w, Math.Floor(cx + maxDist + 1.5));
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++)
                        {
                            double dist = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                            float f = Erf((dist * 0.5 + 0.353553391) * invSigma) - Erf((dist * 0.5 - 0.353553391) * invSigma);
                            float li = s4 * f * f;
                            int idx = y * w + x;
                            planes[0][idx] += c0 * li;
                            planes[1][idx] += c1 * li;
                            planes[2][idx] += c2 * li;
                        }
                }
            }
        }
    }
}
