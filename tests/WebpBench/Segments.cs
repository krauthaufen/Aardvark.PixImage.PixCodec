using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Aardvark.ImageCodecs.Bench
{
    /// <summary>
    /// Segment counts against each other: size, PSNR and SSIM over a range of qualities per image, and the average
    /// difference at equal size (piecewise linear in log size, like a BD-rate comparison) against the first count.
    /// Usage: WebpBench --segments 1,4 img ...
    /// </summary>
    static class SegmentsExperiment
    {
        public static void Run(List<string> paths, int[] variants)
        {
            var images = new List<(string name, RawImage img)>();
            foreach (var p in paths)
            {
                var img = ToRgb(ImageCodec.Decode(File.ReadAllBytes(p)));
                images.Add((Path.GetFileName(p), img));
                // a 514 tile from the middle as well, the size of the tiles in the report
                if (img.Width >= 1028 && img.Height >= 1028) images.Add((Path.GetFileName(p) + "@tile", Crop(img, img.Width / 2 - 257, img.Height / 2 - 257, 514)));
            }
            images.Add(("drawing", Drawing(640, 480)));
            int[] qs = { 60, 75, 82, 88, 94 };
            var total = new Dictionary<int, double[]>();
            foreach (var (name, img) in images)
            {
                var curves = new Dictionary<int, List<(double s, double p, double m)>>();
                foreach (int v in variants)
                {
                    var c = curves[v] = new List<(double, double, double)>();
                    foreach (int q in qs)
                    {
                        var ms = new MemoryStream();
                        Webp.Encode(img, ms, new WebpEncodeOptions { Quality = q, Segments = v });
                        var back = Webp.Decode(ms.ToArray());
                        c.Add((Math.Log(ms.Length), Psnr(img, back), SsimDb(img, back)));
                    }
                }
                Console.WriteLine($"{name} ({img.Width}x{img.Height}) at q88, KiB / PSNR / SSIM dB: "
                    + string.Join("  ", variants.Select(v => $"v{v} {Math.Exp(curves[v][3].s) / 1024:F1} {curves[v][3].p:F2} {curves[v][3].m:F2}")));
                foreach (int v in variants.Skip(1))
                {
                    var (dp, ds, dr) = Compare(curves[variants[0]], curves[v]);
                    Console.WriteLine($"    v{v} vs v{variants[0]} at equal size: PSNR {dp:+0.000;-0.000} dB, SSIM {ds:+0.000;-0.000} dB; size at equal PSNR {dr:+0.0;-0.0} %");
                    if (!total.ContainsKey(v)) total[v] = new double[4];
                    total[v][0] += dp; total[v][1] += ds; total[v][2] += dr; total[v][3]++;
                }
            }
            foreach (var (v, t) in total)
                Console.WriteLine($"mean v{v} vs v{variants[0]}: PSNR {t[0] / t[3]:+0.000;-0.000} dB, SSIM {t[1] / t[3]:+0.000;-0.000} dB, size at equal PSNR {t[2] / t[3]:+0.0;-0.0} %");
        }

        /// <summary>Mean metric differences of b over a on the common log-size range, and the size difference at equal PSNR.</summary>
        static (double psnr, double ssim, double rate) Compare(List<(double s, double p, double m)> a, List<(double s, double p, double m)> b)
        {
            double lo = Math.Max(a[0].s, b[0].s), hi = Math.Min(a[^1].s, b[^1].s);
            double dp = 0, dm = 0;
            for (int i = 0; i <= 100; i++)
            {
                double s = lo + (hi - lo) * i / 100;
                dp += At(b, s, x => x.p) - At(a, s, x => x.p);
                dm += At(b, s, x => x.m) - At(a, s, x => x.m);
            }
            double plo = Math.Max(a[0].p, b[0].p), phi = Math.Min(a[^1].p, b[^1].p), dr = 0;
            for (int i = 0; i <= 100; i++)
            {
                double p = plo + (phi - plo) * i / 100;
                dr += SizeAt(b, p) - SizeAt(a, p);
            }
            return (dp / 101, dm / 101, (Math.Exp(dr / 101) - 1) * 100);
        }

        static double At(List<(double s, double p, double m)> c, double s, Func<(double s, double p, double m), double> f)
        {
            for (int i = 0; i + 1 < c.Count; i++)
                if (s <= c[i + 1].s || i + 2 == c.Count)
                {
                    double t = (s - c[i].s) / (c[i + 1].s - c[i].s);
                    return f(c[i]) + t * (f(c[i + 1]) - f(c[i]));
                }
            return f(c[^1]);
        }

        static double SizeAt(List<(double s, double p, double m)> c, double p)
        {
            for (int i = 0; i + 1 < c.Count; i++)
                if (p <= c[i + 1].p || i + 2 == c.Count)
                {
                    double t = (p - c[i].p) / (c[i + 1].p - c[i].p);
                    return c[i].s + t * (c[i + 1].s - c[i].s);
                }
            return c[^1].s;
        }

        static RawImage ToRgb(RawImage img)
        {
            var s = (byte[])img.Data;
            int ch = img.Channels, n = img.Width * img.Height;
            var d = new byte[n * 3];
            for (int i = 0; i < n; i++)
                for (int c = 0; c < 3; c++) d[3 * i + c] = s[i * ch + (ch >= 3 ? c : 0)];
            return new RawImage(img.Width, img.Height, ColorModel.Rgb, d);
        }

        static RawImage Crop(RawImage img, int x0, int y0, int size)
        {
            var s = (byte[])img.Data; var d = new byte[size * size * 3];
            for (int y = 0; y < size; y++) Array.Copy(s, ((y0 + y) * img.Width + x0) * 3, d, y * size * 3, size * 3);
            return new RawImage(size, size, ColorModel.Rgb, d);
        }

        /// <summary>Flat colors, a gradient, thin lines and text-like strokes.</summary>
        static RawImage Drawing(int w, int h)
        {
            var d = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 3;
                    byte r = 240, g = 240, b = 235;
                    if (x > 40 && x < 300 && y > 40 && y < 200) { r = 30; g = 90; b = 200; }
                    if ((x - 450) * (x - 450) + (y - 150) * (y - 150) < 90 * 90) { r = 220; g = 60; b = 40; }
                    if (y > 260) { r = (byte)(x * 255 / w); g = (byte)(100 + y / 8); b = 120; }
                    if ((x + y) % 37 == 0 || (y > 300 && y < 420 && x % 9 < 2 && (y / 12) % 2 == 0)) { r = g = b = 20; }
                    d[i] = r; d[i + 1] = g; d[i + 2] = b;
                }
            return new RawImage(w, h, ColorModel.Rgb, d);
        }

        static double Psnr(RawImage a, RawImage b)
        {
            var x = (byte[])a.Data; var y = (byte[])b.Data; double se = 0;
            for (int i = 0; i < x.Length; i++) { double e = x[i] - y[i]; se += e * e; }
            return 10 * Math.Log10(255.0 * 255 / (se / x.Length));
        }

        /// <summary>Mean SSIM of luma over 8x8 windows at every fourth position, as -10 log10(1 - SSIM).</summary>
        static double SsimDb(RawImage a, RawImage b)
        {
            var x = (byte[])a.Data; var y = (byte[])b.Data;
            int w = a.Width, h = a.Height;
            double sum = 0; int n = 0;
            for (int y0 = 0; y0 + 8 <= h; y0 += 4)
                for (int x0 = 0; x0 + 8 <= w; x0 += 4)
                {
                    double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
                    for (int j = 0; j < 8; j++)
                        for (int i = 0; i < 8; i++)
                        {
                            int k = 3 * ((y0 + j) * w + x0 + i);
                            double p = 0.299 * x[k] + 0.587 * x[k + 1] + 0.114 * x[k + 2], q = 0.299 * y[k] + 0.587 * y[k + 1] + 0.114 * y[k + 2];
                            sx += p; sy += q; sxx += p * p; syy += q * q; sxy += p * q;
                        }
                    double mx = sx / 64, my = sy / 64, vx = sxx / 64 - mx * mx, vy = syy / 64 - my * my, cov = sxy / 64 - mx * my;
                    sum += (2 * mx * my + 6.5025) * (2 * cov + 58.5225) / ((mx * mx + my * my + 6.5025) * (vx + vy + 58.5225));
                    n++;
                }
            return -10 * Math.Log10(1 - sum / n);
        }
    }
}
