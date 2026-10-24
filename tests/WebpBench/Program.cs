using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs.Bench
{
    /// <summary>
    /// Single-threaded WebP timings on 514x514 tiles: synthetic and photo crops, lossy q88 (RGB and RGBA with lossless
    /// alpha), lossless method 4, JPEG q85 for comparison.
    /// Usage: WebpBench [--rounds N] [--out dir] [--no-encode] [--only name] photo.jpg ...
    /// The checksums printed per set cover the encoded files and the decoded pixels, so two builds can be compared for
    /// identical output. With --out the encoded tiles are written there (for timing other decoders on the same files).
    /// </summary>
    static class Program
    {
        const int Size = 514;

        static int Main(string[] args)
        {
            int rounds = 20;
            string outDir = null;
            bool encode = true;
            string only = null, kindOnly = null;
            double loop = 0;
            bool phases = false;
            var photos = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--rounds": rounds = int.Parse(args[++i]); break;
                    case "--out": outDir = args[++i]; break;
                    case "--no-encode": encode = false; break;
                    case "--only": only = args[++i]; break;
                    case "--kind": kindOnly = args[++i]; break;
                    case "--loop": loop = double.Parse(args[++i]); break;
                    case "--phases": phases = true; break;
                    default: photos.Add(args[i]); break;
                }
            }
            Console.WriteLine($"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} threads");

            var tiles = new List<(string kind, RawImage rgba)>();
            for (int s = 0; s < 4; s++) tiles.Add(("synthetic", Synthetic(s)));
            foreach (var p in photos)
            {
                var img = Jpeg.Decode(File.ReadAllBytes(p));
                int n = 0;
                for (int y = 0; y + Size <= img.Height && n < 8; y += Math.Max(Size, (img.Height - Size) / 2))
                    for (int x = 0; x + Size <= img.Width && n < 8; x += Math.Max(Size, (img.Width - Size) / 3), n++)
                        tiles.Add(("photo", Crop(img, x, y, n)));
            }
            Console.WriteLine($"{tiles.Count} tiles of {Size}x{Size}");

            var sets = new (string name, Func<RawImage, byte[]> enc)[]
            {
                ("jpeg q85", t => { var ms = new MemoryStream(); Jpeg.Encode(Rgb(t), ms, new JpegEncodeOptions { Quality = 85 }); return ms.ToArray(); }),
                ("webp lossy q88 rgb", t => Webp(Rgb(t), new WebpEncodeOptions { Quality = 88 })),
                ("webp lossy q88 rgba", t => Webp(t, new WebpEncodeOptions { Quality = 88 })),
                ("webp lossless m4", t => Webp(t, new WebpEncodeOptions { Lossless = true, Method = 4 })),
            };
            foreach (var (name, enc) in sets)
            {
                if (only != null && !name.Contains(only)) continue;
                foreach (var kind in new[] { "synthetic", "photo" })
                {
                    var src = tiles.Where(t => t.kind == kind).Select(t => t.rgba).ToArray();
                    if (src.Length == 0 || (kindOnly != null && kind != kindOnly)) continue;
                    var files = src.Select(enc).ToArray();
                    if (outDir != null)
                    {
                        Directory.CreateDirectory(outDir);
                        for (int i = 0; i < files.Length; i++)
                            File.WriteAllBytes(Path.Combine(outDir, $"{name.Replace(' ', '_')}_{kind}_{i}.{(name.StartsWith("jpeg") ? "jpg" : "webp")}"), files[i]);
                    }
                    Func<byte[], RawImage> dec = name.StartsWith("jpeg") ? d => Jpeg.Decode(d)
                        : d => ImageCodecs.Webp.Decode(d, new WebpDecodeOptions { Multithreaded = false });
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    foreach (var f in files) { hash.AppendData(f); hash.AppendData(dec(f).GetBytes()); }
                    string sum = Convert.ToHexString(hash.GetHashAndReset())[..16];

                    if (phases && name.StartsWith("webp")) Phases(files);
                    if (loop > 0)
                    {
                        // for a profiler: decode for that many seconds and stop
                        var lw = Stopwatch.StartNew();
                        int count = 0;
                        while (lw.Elapsed.TotalSeconds < loop) foreach (var f in files) { dec(f); count++; }
                        Console.WriteLine($"{name} {kind}: {count} decodes, {lw.Elapsed.TotalMilliseconds / count:F3} ms each");
                        return 0;
                    }
                    // decode
                    for (int w = 0; w < 30; w++) foreach (var f in files) dec(f);
                    var times = new List<double>();
                    long alloc = 0; int allocN = 0;
                    var perFile = files.Select(_ => new List<double>()).ToArray();
                    for (int r = 0; r < rounds; r++)
                        for (int fi = 0; fi < files.Length; fi++)
                        {
                            var f = files[fi];
                            long a0 = GC.GetAllocatedBytesForCurrentThread();
                            long t0 = Stopwatch.GetTimestamp();
                            var img = dec(f);
                            long t1 = Stopwatch.GetTimestamp();
                            alloc += GC.GetAllocatedBytesForCurrentThread() - a0 - img.Data.Length;
                            allocN++;
                            times.Add((t1 - t0) * 1000.0 / Stopwatch.Frequency);
                            perFile[fi].Add((t1 - t0) * 1000.0 / Stopwatch.Frequency);
                        }
                    times.Sort();
                    foreach (var l in perFile) l.Sort();
                    var medians = perFile.Select(l => P(l, 0.5)).ToList();
                    double kb = files.Average(f => f.Length) / 1024.0;
                    string line = $"{name,-20} {kind,-9} {kb,7:F1} KiB  decode p50 {P(times, 0.5),6:F3} p95 {P(times, 0.95),6:F3} min {times[0],6:F3} ms, per-file p50 {medians.Min():F2}..{medians.Max():F2}  alloc {alloc / allocN / 1024.0,7:F1} KiB beyond pixels  [{sum}]";

                    if (encode)
                    {
                        var et = new List<double>();
                        for (int r = 0; r < Math.Max(2, rounds / 10); r++)
                            foreach (var t in src)
                            {
                                long t0 = Stopwatch.GetTimestamp();
                                enc(t);
                                et.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                            }
                        et.Sort();
                        // throughput with every core busy, like a tile pyramid build
                        int jobs = 16 * Environment.ProcessorCount;
                        var sw = Stopwatch.StartNew();
                        Parallel.For(0, jobs, i => enc(src[i % src.Length]));
                        line += $"  encode p50 {P(et, 0.5),7:F2} ms, {jobs} in parallel {sw.Elapsed.TotalSeconds:F2} s";
                    }
                    Console.WriteLine(line);
                }
            }
            return 0;
        }

        /// <summary>Median times of the stages of a decode: VP8 (tokens, reconstruction, loop filter), ALPH, VP8L.</summary>
        static void Phases(byte[][] files)
        {
            var vp8 = new List<double>(); var alph = new List<double>(); var vp8l = new List<double>();
            for (int r = 0; r < 40; r++)
                foreach (var f in files)
                {
                    var c = WebpContainer.Parse(f);
                    var e = c.Frames[0];
                    var bits = f.AsSpan(e.DataOffset, e.DataLength);
                    long t0 = Stopwatch.GetTimestamp();
                    if (e.Lossless)
                    {
                        var px = Vp8lDecoder.Decode(bits, out _, out _, 1L << 30, out bool rented);
                        if (rented) System.Buffers.ArrayPool<uint>.Shared.Return(px);
                        if (r >= 10) vp8l.Add(Ms(t0));
                        continue;
                    }
                    var frame = Vp8Decoder.Decode(bits, 1L << 30, false);
                    frame.Release();
                    if (r >= 10) vp8.Add(Ms(t0));
                    if (e.AlphLength >= 0)
                    {
                        t0 = Stopwatch.GetTimestamp();
                        var a = WebpAlpha.Decode(f.AsSpan(e.AlphOffset, e.AlphLength), e.Width, e.Height);
                        System.Buffers.ArrayPool<byte>.Shared.Return(a);
                        if (r >= 10) alph.Add(Ms(t0));
                    }
                }
            foreach (var (n, l) in new[] { ("vp8", vp8), ("alph", alph), ("vp8l", vp8l) })
                if (l.Count > 0) { l.Sort(); Console.WriteLine($"    phase {n,-5} p50 {P(l, 0.5):F3} ms"); }
        }

        static double Ms(long t0) => (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

        static double P(List<double> sorted, double q) => sorted[Math.Min(sorted.Count - 1, (int)(q * sorted.Count))];

        static byte[] Webp(RawImage img, WebpEncodeOptions o)
        {
            var ms = new MemoryStream();
            ImageCodecs.Webp.Encode(img, ms, o);
            return ms.ToArray();
        }

        static RawImage Rgb(RawImage rgba)
        {
            var s = (byte[])rgba.Data;
            var d = new byte[s.Length / 4 * 3];
            for (int i = 0, o = 0; i < s.Length; i += 4, o += 3) { d[o] = s[i]; d[o + 1] = s[i + 1]; d[o + 2] = s[i + 2]; }
            return new RawImage(rgba.Width, rgba.Height, ColorModel.Rgb, d);
        }

        /// <summary>Alpha like a map tile at the edge of the data: opaque inside a wobbly boundary, a soft edge, transparent outside.</summary>
        static byte Alpha(int x, int y, int seed)
        {
            double cx = 300 + 40 * seed, cy = 260 - 30 * seed;
            double a = Math.Atan2(y - cy, x - cx);
            double r = 330 + 25 * Math.Sin(5 * a + seed) + 10 * Math.Sin(13 * a);
            double d = r - Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
            return (byte)Math.Clamp(128 + d * 64, 0, 255);
        }

        static RawImage Crop(RawImage img, int x0, int y0, int seed)
        {
            var s = (byte[])img.Data;
            int ch = img.Channels;
            var d = new byte[Size * Size * 4];
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    int si = ((y0 + y) * img.Width + x0 + x) * ch, di = (y * Size + x) * 4;
                    d[di] = s[si]; d[di + 1] = s[si + Math.Min(1, ch - 1)]; d[di + 2] = s[si + Math.Min(2, ch - 1)];
                    d[di + 3] = Alpha(x, y, seed);
                }
            return new RawImage(Size, Size, ColorModel.Rgba, d);
        }

        /// <summary>Smooth shading with texture and a few hard edges, like a rendered or orthophoto-ish tile.</summary>
        static RawImage Synthetic(int seed)
        {
            var rng = new Random(seed);
            var d = new byte[Size * Size * 4];
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    double v = 0.5 + 0.25 * Math.Sin(x * 0.021 + seed) * Math.Cos(y * 0.017) + 0.1 * Math.Sin((x + 2 * y) * 0.11);
                    bool edge = ((x / 64 + y / 48 + seed) & 3) == 0;
                    int n = rng.Next(-6, 7);
                    int i = (y * Size + x) * 4;
                    d[i] = (byte)Math.Clamp(v * 255 + n + (edge ? 40 : 0), 0, 255);
                    d[i + 1] = (byte)Math.Clamp(v * 200 + 30 + n, 0, 255);
                    d[i + 2] = (byte)Math.Clamp((1 - v) * 220 + n - (edge ? 30 : 0), 0, 255);
                    d[i + 3] = Alpha(x, y, seed);
                }
            return new RawImage(Size, Size, ColorModel.Rgba, d);
        }
    }
}
