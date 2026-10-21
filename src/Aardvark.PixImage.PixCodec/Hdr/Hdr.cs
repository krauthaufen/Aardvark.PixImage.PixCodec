using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Aardvark.ImageCodecs.HdrImpl;

namespace Aardvark.ImageCodecs
{
    public sealed class HdrDecodeOptions
    {
        /// <summary>
        /// Divide the pixel values by the product of the header's EXPOSURE lines, which gives the radiance before the exposure
        /// adjustments a program recorded. Off by default: the values are returned as stored, like most readers do.
        /// </summary>
        public bool ApplyExposure { get; set; }

        /// <summary>
        /// Convert XYZE files (FORMAT=32-bit_rle_xyze) to RGB with the header's PRIMARIES (Radiance's standard primaries when
        /// there are none). On by default; when off, X, Y, Z are returned as the three channels of a <see cref="ColorModel.Other"/> image.
        /// </summary>
        public bool ConvertXyzToRgb { get; set; } = true;

        /// <summary>Upper bound for the image in pixels; larger files are rejected with <see cref="ImageNotSupportedException"/>.</summary>
        public long MaxPixels { get; set; } = 1L << 28;

        internal static readonly HdrDecodeOptions Default = new();
    }

    public sealed class HdrEncodeOptions
    {
        /// <summary>Run length encode the scanlines (the usual "new" RLE; lines narrower than 8 or wider than 32767 pixels are stored flat). On by default.</summary>
        public bool Rle { get; set; } = true;

        /// <summary>An EXPOSURE value to record in the header (0 = none). The pixels are not changed.</summary>
        public double Exposure { get; set; }
    }

    /// <summary>The text header of a Radiance file.</summary>
    public sealed class HdrHeader
    {
        /// <summary>The program identifier after "#?" (usually RADIANCE or RGBE).</summary>
        public string Program { get; internal set; } = "";
        /// <summary>True for FORMAT=32-bit_rle_xyze.</summary>
        public bool IsXyz { get; internal set; }
        /// <summary>Product of all EXPOSURE lines (1 when there are none).</summary>
        public double Exposure { get; internal set; } = 1;
        /// <summary>The PRIMARIES line: red, green, blue and white CIE xy chromaticities, or null.</summary>
        public double[]? Primaries { get; internal set; }
        /// <summary>The GAMMA line (a display hint), 0 if absent.</summary>
        public double Gamma { get; internal set; }
        /// <summary>The PIXASPECT line (product), 1 if absent.</summary>
        public double PixelAspect { get; internal set; } = 1;
        /// <summary>Every header line except the first, in order (comments, commands, variables).</summary>
        public IReadOnlyList<string> Lines { get; internal set; } = Array.Empty<string>();
        /// <summary>The resolution string, e.g. "-Y 512 +X 768".</summary>
        public string Resolution { get; internal set; } = "";
    }

    /// <summary>
    /// Radiance RGBE (.hdr, .pic): reads both RLE schemes and flat data, RGBE and XYZE, all eight scan orders of the resolution
    /// string, and the EXPOSURE / PRIMARIES header lines. Decodes to Float32 Rgb (linear light; a code value c with exponent e
    /// means (c + 0.5) * 2^(e - 136), as in Radiance itself). Writes Float32 / Float16 / Float64 images (UInt8 / UInt16 ones are
    /// converted to linear light first according to their <see cref="ImageMetadata.Transfer"/>); gray is written as equal
    /// R, G, B and alpha is dropped.
    /// </summary>
    public static class Hdr
    {
        /// <summary>Whether the data starts with "#?" and a program name, the Radiance signature.</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header)
            => header.Length >= 3 && header[0] == '#' && header[1] == '?' && (header[2] is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z');

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data)
        {
            var h = HdrCodec.ParseHeader(data, out _, out int w, out int ht, out _);
            return new ImageInfo(ImageFormat.Hdr, w, ht, 3, SampleType.Float32, ColorModel.Rgb)
            {
                BitsPerSample = 32, Transfer = TransferFunction.Linear,
            };
        }

        /// <summary>Reads the text header.</summary>
        public static HdrHeader ReadHeader(ReadOnlySpan<byte> data) => HdrCodec.ParseHeader(data, out _, out _, out _, out _);

        public static RawImage Decode(ReadOnlySpan<byte> data, HdrDecodeOptions? options = null)
            => HdrCodec.Decode(data, options ?? HdrDecodeOptions.Default);

        public static void Encode(RawImage image, Stream output, HdrEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(output);
            HdrCodec.Encode(image, output, options ?? new HdrEncodeOptions());
        }

        public static byte[] Encode(RawImage image, HdrEncodeOptions? options = null)
        {
            var ms = new MemoryStream();
            Encode(image, ms, options);
            return ms.ToArray();
        }
    }
}

namespace Aardvark.ImageCodecs.HdrImpl
{
    internal static class HdrCodec
    {
        // scan order: rows along the first axis of the resolution string, pixels along the second
        internal struct Order
        {
            public bool XMajor;        // first axis is X: each scanline is a column
            public bool FirstNeg;      // first axis sign '-'
            public bool SecondNeg;
        }

        static string ReadLine(ReadOnlySpan<byte> d, ref int pos)
        {
            int start = pos;
            while (pos < d.Length && d[pos] != '\n') pos++;
            if (pos >= d.Length) throw new ImageFormatException("HDR: header ends early");
            var s = Encoding.Latin1.GetString(d.Slice(start, pos - start)).TrimEnd('\r');
            pos++;
            return s;
        }

        public static HdrHeader ParseHeader(ReadOnlySpan<byte> d, out int dataStart, out int width, out int height, out Order order)
        {
            if (!Hdr.IsMatch(d)) throw new ImageFormatException("HDR: not a Radiance file");
            int pos = 0;
            var h = new HdrHeader { Program = ReadLine(d, ref pos).Substring(2).Trim() };
            var lines = new List<string>();
            bool formatSeen = false;
            while (true)
            {
                if (lines.Count > 10000) throw new ImageFormatException("HDR: header too long");
                var line = ReadLine(d, ref pos);
                if (line.Length == 0) break;
                lines.Add(line);
                int eq = line.IndexOf('=');
                if (line.StartsWith('#') || eq <= 0) continue;
                string key = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
                switch (key.ToUpperInvariant())
                {
                    case "FORMAT":
                        if (val == "32-bit_rle_xyze") h.IsXyz = true;
                        else if (val != "32-bit_rle_rgbe") throw new ImageNotSupportedException($"HDR: format {val}");
                        formatSeen = true;
                        break;
                    case "EXPOSURE":
                        if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var e) && e > 0 && double.IsFinite(e)) h.Exposure *= e;
                        break;
                    case "PIXASPECT":
                        if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && a > 0 && double.IsFinite(a)) h.PixelAspect *= a;
                        break;
                    case "GAMMA":
                        if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var g) && g > 0) h.Gamma = g;
                        break;
                    case "PRIMARIES":
                        {
                            var parts = val.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length == 8)
                            {
                                var p = new double[8];
                                bool ok = true;
                                for (int i = 0; i < 8; i++) ok &= double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out p[i]);
                                if (ok) h.Primaries = p;
                            }
                            break;
                        }
                }
            }
            _ = formatSeen; // FORMAT is optional; RGBE is the default
            h.Lines = lines;
            var res = ReadLine(d, ref pos);
            h.Resolution = res;
            var t = res.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length != 4 || t[0].Length != 2 || t[2].Length != 2
                || !int.TryParse(t[1], NumberStyles.None, CultureInfo.InvariantCulture, out int n1)
                || !int.TryParse(t[3], NumberStyles.None, CultureInfo.InvariantCulture, out int n2) || n1 <= 0 || n2 <= 0)
                throw new ImageFormatException($"HDR: bad resolution string '{res}'");
            char a1 = char.ToUpperInvariant(t[0][1]), a2 = char.ToUpperInvariant(t[2][1]);
            if (t[0][0] is not ('+' or '-') || t[2][0] is not ('+' or '-') || !(a1 == 'Y' && a2 == 'X' || a1 == 'X' && a2 == 'Y'))
                throw new ImageFormatException($"HDR: bad resolution string '{res}'");
            order = new Order { XMajor = a1 == 'X', FirstNeg = t[0][0] == '-', SecondNeg = t[2][0] == '-' };
            (width, height) = order.XMajor ? (n1, n2) : (n2, n1);
            dataStart = pos;
            return h;
        }

        public static RawImage Decode(ReadOnlySpan<byte> d, HdrDecodeOptions opt)
        {
            var h = ParseHeader(d, out int pos, out int w, out int ht, out var order);
            if ((long)w * ht > opt.MaxPixels) throw new ImageNotSupportedException($"HDR: {w}x{ht} exceeds MaxPixels");
            int lines = order.XMajor ? w : ht, len = order.XMajor ? ht : w;
            if ((long)lines * len * 3 > Array.MaxLength) throw new ImageNotSupportedException("HDR: too large");
            var px = new float[(long)w * ht * 3];
            double scale = opt.ApplyExposure ? 1 / h.Exposure : 1;
            var line = new byte[len * 4];
            for (int s = 0; s < lines; s++)
            {
                ReadScanline(d, ref pos, line, len);
                // place scanline s: first axis index s, second axis index i
                for (int i = 0; i < len; i++)
                {
                    int x, y;
                    if (order.XMajor)
                    {
                        // "+X": first scanline is the left column; "-X": the right column
                        x = order.FirstNeg ? lines - 1 - s : s;
                        y = order.SecondNeg ? i : len - 1 - i;   // "-Y": top to bottom
                    }
                    else
                    {
                        y = order.FirstNeg ? s : lines - 1 - s;   // "-Y": first scanline is the top row
                        x = order.SecondNeg ? len - 1 - i : i;    // "+X": left to right
                    }
                    int e = line[i * 4 + 3];
                    if (e == 0) continue;
                    double f = Math.ScaleB(scale, e - 136);
                    int o = (y * w + x) * 3;
                    px[o] = (float)(line[i * 4] * f);
                    px[o + 1] = (float)(line[i * 4 + 1] * f);
                    px[o + 2] = (float)(line[i * 4 + 2] * f);
                }
            }
            var color = ColorModel.Rgb;
            if (h.IsXyz)
            {
                if (opt.ConvertXyzToRgb) XyzToRgb(px, h.Primaries ?? StandardPrimaries);
                else color = ColorModel.Other;
            }
            return new RawImage(w, ht, color, px, 3, new ImageMetadata { Transfer = TransferFunction.Linear });
        }

        /// <summary>Radiance's standard primaries (CIE xy of red, green, blue, white).</summary>
        internal static readonly double[] StandardPrimaries = { 0.640, 0.330, 0.290, 0.600, 0.150, 0.060, 1 / 3.0, 1 / 3.0 };

        static void XyzToRgb(float[] px, double[] p)
        {
            var m = RgbToXyzMatrix(p);
            var inv = Invert3(m);
            for (int i = 0; i < px.Length; i += 3)
            {
                double X = px[i], Y = px[i + 1], Z = px[i + 2];
                px[i] = (float)(inv[0] * X + inv[1] * Y + inv[2] * Z);
                px[i + 1] = (float)(inv[3] * X + inv[4] * Y + inv[5] * Z);
                px[i + 2] = (float)(inv[6] * X + inv[7] * Y + inv[8] * Z);
            }
        }

        /// <summary>RGB to XYZ from primaries and white point, white mapping to Y = 1 (row major 3x3).</summary>
        internal static double[] RgbToXyzMatrix(double[] p)
        {
            // columns: xyz of each primary, scaled so that R + G + B = white with Y = 1
            double[] c = new double[9];
            for (int k = 0; k < 3; k++)
            {
                double x = p[2 * k], y = p[2 * k + 1];
                c[k] = x / y; c[3 + k] = 1; c[6 + k] = (1 - x - y) / y;
            }
            double wx = p[6], wy = p[7];
            double[] W = { wx / wy, 1, (1 - wx - wy) / wy };
            var ci = Invert3(c);
            double[] s = new double[3];
            for (int r = 0; r < 3; r++) s[r] = ci[r * 3] * W[0] + ci[r * 3 + 1] * W[1] + ci[r * 3 + 2] * W[2];
            for (int r = 0; r < 3; r++) for (int k = 0; k < 3; k++) c[r * 3 + k] *= s[k];
            return c;
        }

        static double[] Invert3(double[] m)
        {
            double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
            double A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
            double det = a * A + b * B + c * C;
            if (Math.Abs(det) < 1e-12) throw new ImageFormatException("HDR: degenerate primaries");
            double r = 1 / det;
            return new[]
            {
                A * r, -(b * i - c * h) * r, (b * f - c * e) * r,
                B * r, (a * i - c * g) * r, -(a * f - c * d) * r,
                C * r, -(a * h - b * g) * r, (a * e - b * d) * r,
            };
        }

        static void ReadScanline(ReadOnlySpan<byte> d, ref int pos, byte[] line, int len)
        {
            if (pos + 4 > d.Length) { Array.Clear(line); pos = d.Length; return; } // truncated: black
            if (len >= 8 && len <= 0x7FFF && d[pos] == 2 && d[pos + 1] == 2 && (d[pos + 2] << 8 | d[pos + 3]) == len && (d[pos + 2] & 0x80) == 0)
            {
                pos += 4;
                for (int c = 0; c < 4; c++)
                {
                    int i = 0;
                    while (i < len)
                    {
                        if (pos >= d.Length) { for (; i < len; i++) line[i * 4 + c] = 0; break; }
                        int n = d[pos++];
                        if (n > 128)
                        {
                            n -= 128;
                            if (i + n > len) throw new ImageFormatException("HDR: run crosses the scanline end");
                            byte v = pos < d.Length ? d[pos++] : (byte)0;
                            for (int k = 0; k < n; k++) line[(i++) * 4 + c] = v;
                        }
                        else
                        {
                            if (n == 0) throw new ImageFormatException("HDR: zero length run");
                            if (i + n > len) throw new ImageFormatException("HDR: literal crosses the scanline end");
                            for (int k = 0; k < n; k++) line[(i++) * 4 + c] = pos < d.Length ? d[pos++] : (byte)0;
                        }
                    }
                }
                return;
            }
            // flat pixels with the old run length scheme: 1,1,1,n repeats the previous pixel n << shift times
            int shift = 0, x = 0;
            while (x < len)
            {
                if (pos + 4 > d.Length) { Array.Clear(line, x * 4, (len - x) * 4); pos = d.Length; return; }
                byte r = d[pos], g = d[pos + 1], b = d[pos + 2], e = d[pos + 3];
                pos += 4;
                if (r == 1 && g == 1 && b == 1)
                {
                    if (x == 0) throw new ImageFormatException("HDR: repeat at the start of a scanline");
                    long count = (long)e << shift;
                    if (x + count > len) throw new ImageFormatException("HDR: repeat crosses the scanline end");
                    for (long k = 0; k < count; k++, x++) line.AsSpan((x - 1) * 4, 4).CopyTo(line.AsSpan(x * 4, 4));
                    shift += 8;
                    if (shift > 24) shift = 24;
                }
                else
                {
                    line[x * 4] = r; line[x * 4 + 1] = g; line[x * 4 + 2] = b; line[x * 4 + 3] = e;
                    x++;
                    shift = 0;
                }
            }
        }

        public static void Encode(RawImage image, Stream output, HdrEncodeOptions opt)
        {
            var src = image.SampleType switch
            {
                SampleType.Float32 => image,
                SampleType.Float16 or SampleType.Float64 or SampleType.UInt8 or SampleType.UInt16 => ImageOps.ToLinear(image,
                    image.SampleType is SampleType.Float16 or SampleType.Float64 ? TransferFunction.Linear : null),
                _ => throw new ArgumentException($"HDR: cannot write {image.SampleType} samples"),
            };
            if (image.Color == ColorModel.Other) throw new ArgumentException("HDR: needs a Gray or Rgb image");
            var f = src.GetData<float>();
            int w = image.Width, ht = image.Height, ch = image.Channels;
            bool gray = image.Color is ColorModel.Gray or ColorModel.GrayAlpha;
            var sb = new StringBuilder();
            sb.Append("#?RADIANCE\n# PixCodec\nFORMAT=32-bit_rle_rgbe\n");
            if (opt.Exposure > 0 && double.IsFinite(opt.Exposure)) sb.Append("EXPOSURE=").Append(opt.Exposure.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append('\n').Append("-Y ").Append(ht).Append(" +X ").Append(w).Append('\n');
            output.Write(Encoding.ASCII.GetBytes(sb.ToString()));
            var line = new byte[w * 4];
            var outBuf = new byte[w * 5 + 16];
            for (int y = 0; y < ht; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * ch;
                    float r = f[o], g = gray ? r : f[o + 1], b = gray ? r : f[o + 2];
                    ToRgbe(r, g, b, line.AsSpan(x * 4, 4));
                }
                if (!opt.Rle || w < 8 || w > 0x7FFF) { output.Write(line); continue; }
                int n = 0;
                outBuf[n++] = 2; outBuf[n++] = 2; outBuf[n++] = (byte)(w >> 8); outBuf[n++] = (byte)w;
                for (int c = 0; c < 4; c++) n = RleComponent(line, c, w, outBuf, n);
                output.Write(outBuf, 0, n);
            }
        }

        internal static void ToRgbe(float r, float g, float b, Span<byte> o)
        {
            // negative and NaN become 0, infinities the largest value
            double R = r > 0 ? r : 0, G = g > 0 ? g : 0, B = b > 0 ? b : 0;
            double max = Math.Max(R, Math.Max(G, B));
            if (!(max >= 1e-32)) { o[0] = o[1] = o[2] = o[3] = 0; return; }
            if (double.IsInfinity(max) || max >= Math.ScaleB(1.0, 127))
            {
                double lim = Math.ScaleB(255.0 / 256, 127);
                R = Math.Min(R, lim); G = Math.Min(G, lim); B = Math.Min(B, lim);
                max = lim;
            }
            int e = Math.ILogB(max) + 1;          // max = m * 2^e with m in [0.5, 1)
            double scale = Math.ScaleB(256.0, -e);
            if (Math.Round(max * scale) >= 256) { e++; scale *= 0.5; }   // rounding would carry into the exponent
            if (e + 128 < 1) { o[0] = o[1] = o[2] = o[3] = 0; return; }
            o[0] = (byte)Math.Round(R * scale); o[1] = (byte)Math.Round(G * scale); o[2] = (byte)Math.Round(B * scale);
            o[3] = (byte)(e + 128);
        }

        static int RleComponent(byte[] line, int c, int w, byte[] o, int n)
        {
            int i = 0;
            while (i < w)
            {
                // find the next run of at least 4 equal bytes
                int runStart = i, runLen = 0;
                while (runStart < w)
                {
                    runLen = 1;
                    byte v = line[runStart * 4 + c];
                    while (runStart + runLen < w && runLen < 127 && line[(runStart + runLen) * 4 + c] == v) runLen++;
                    if (runLen >= 4) break;
                    runStart += runLen;
                    runLen = 0;
                }
                // literals before it
                while (i < runStart)
                {
                    int k = Math.Min(128, runStart - i);
                    o[n++] = (byte)k;
                    for (int j = 0; j < k; j++) o[n++] = line[(i + j) * 4 + c];
                    i += k;
                }
                if (runLen >= 4)
                {
                    o[n++] = (byte)(128 + runLen);
                    o[n++] = line[runStart * 4 + c];
                    i = runStart + runLen;
                }
            }
            return n;
        }
    }
}
