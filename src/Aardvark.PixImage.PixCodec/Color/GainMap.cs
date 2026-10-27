using System;
using System.Threading.Tasks;
using Aardvark.ImageCodecs.Processing;

namespace Aardvark.ImageCodecs
{
    /// <summary>Settings for <see cref="GainMap.Compute"/>.</summary>
    public sealed class GainMapOptions
    {
        /// <summary>
        /// Store one gain per colour channel (an RGB gain map with per-channel metadata) instead of one achromatic gain
        /// computed from the luminance (the default, which Ultra HDR recommends).
        /// </summary>
        public bool Multichannel { get; set; }
        /// <summary>Downsampling factor of the gain map in each direction (1..128; 1 = full resolution).</summary>
        public int Scale { get; set; } = 1;
        /// <summary>γ applied to the normalized gain map before quantization (ISO 21496-1 A.3.4), greater than 0.</summary>
        public double Gamma { get; set; } = 1;
        /// <summary>k_baseline: offset added to the baseline before the quotient is taken (Ultra HDR offset_sdr).</summary>
        public double BaseOffset { get; set; } = 1 / 64.0;
        /// <summary>k_alternate: offset added to the alternate image (Ultra HDR offset_hdr).</summary>
        public double AlternateOffset { get; set; } = 1 / 64.0;
        /// <summary>H_baseline, log2 (0 for an SDR baseline).</summary>
        public double BaseHeadroom { get; set; }
        /// <summary>
        /// H_alternate, log2: the display headroom at which the gain map is fully applied. Default: the largest gain the
        /// image needs (Ultra HDR's advice to set HDRCapacityMax to GainMapMax), at least 1/64 stop above the baseline.
        /// </summary>
        public double? AlternateHeadroom { get; set; }
    }

    /// <summary>
    /// An HDR gain map (ISO 21496-1:2025): the metadata, the gain map image and the colour space of the alternate image.
    /// Read from JPEG (Ultra HDR / ISO 21496-1, see <see cref="Jpeg.GetGainMap"/>) and JPEG XL files
    /// (<see cref="JxlGainMap.ToGainMap"/>). <see cref="Apply(RawImage, GainMap, double?)"/> reconstructs the alternate
    /// (usually HDR) rendition from the baseline image the decoder returns.
    /// </summary>
    public sealed class GainMap
    {
        /// <summary>The ISO 21496-1 metadata (from the binary form, or mapped from Ultra HDR XMP).</summary>
        public GainMapMetadata Metadata { get; init; } = new();
        /// <summary>The gain map pixels as stored (1 or 3 channels), turned upright like the baseline image the decoder returns.</summary>
        public RawImage Image { get; init; } = null!;
        /// <summary>The ICC profile of the alternate image (ISO 21496-1 5.3.3), null when it has the baseline's colour space.</summary>
        public byte[]? AlternateIcc { get; init; }
        /// <summary>Where the metadata came from.</summary>
        public GainMapSource Source { get; init; }
        /// <summary>Largest code value of <see cref="Image"/> (255 for 8 bit, 4095 for 12 bit JPEG); 0 means the sample type's maximum.</summary>
        internal double ImageMax { get; init; }

        /// <summary>
        /// The alternate rendition (ISO 21496-1 clause 6): <paramref name="baseline"/> linearized, the gain map unnormalized
        /// (formula 1), bilinearly resampled to the baseline size (6.2.2, co-sited), weighted for
        /// <paramref name="headroom"/> (formula 3) and applied (formula 2) in the gain map application space. The result is
        /// linear Float32 RGB (alpha kept) where 1.0 is HDR reference white (SDR white), in the primaries of the alternate
        /// image (Annex B), with a linear ICC profile of them in its metadata. <paramref name="headroom"/> is the display's
        /// HDR headroom as log2 (peak / reference white); null applies the gain map fully (H_alternate), 0 gives the SDR
        /// rendition of an SDR baseline.
        /// </summary>
        public static RawImage Apply(RawImage baseline, GainMap gainMap, double? headroom = null)
        {
            ArgumentNullException.ThrowIfNull(gainMap);
            return Apply(baseline, gainMap.Image, gainMap.Metadata, gainMap.AlternateIcc, headroom, gainMap.ImageMax);
        }

        /// <inheritdoc cref="Apply(RawImage, GainMap, double?)"/>
        public static RawImage Apply(RawImage baseline, RawImage gainMapImage, GainMapMetadata metadata, byte[]? alternateIcc = null, double? headroom = null)
            => Apply(baseline, gainMapImage, metadata, alternateIcc, headroom, 0);

        static RawImage Apply(RawImage baseline, RawImage gainMapImage, GainMapMetadata m, byte[]? alternateIcc, double? headroom, double gainMax)
        {
            ArgumentNullException.ThrowIfNull(baseline);
            ArgumentNullException.ThrowIfNull(gainMapImage);
            ArgumentNullException.ThrowIfNull(m);
            if (!m.IsValid()) throw new ArgumentException("invalid gain map metadata");
            int w = baseline.Width, h = baseline.Height;
            var lin = Linearize(baseline, out bool alpha);
            var basePrim = Primaries(baseline.Metadata?.IccProfile);
            var altPrim = alternateIcc != null ? Primaries(alternateIcc) : basePrim;
            // Annex B: at most one primaries conversion, before (application space = alternate) or after (= baseline)
            double[]? pre = null, post = null;
            if (!SamePrimaries(basePrim, altPrim))
            {
                var conv = IccMath.Mul(IccMath.Invert(altPrim) ?? throw new ArgumentException("alternate primaries are degenerate"), basePrim);
                if (m.UseBaseColourSpace) post = conv; else pre = conv;
            }
            double weight = m.Weight(headroom ?? m.AlternateHdrHeadroom);
            var gain = LogGains(gainMapImage, m, gainMax, out int gc);
            int gw = gainMapImage.Width, gh = gainMapImage.Height;
            int och = alpha ? 4 : 3;
            var dst = new float[(long)w * h * och];
            float kb0 = (float)m.BaseOffset[0], kb1 = (float)m.BaseOffset[1], kb2 = (float)m.BaseOffset[2];
            float ka0 = (float)m.AlternateOffset[0], ka1 = (float)m.AlternateOffset[1], ka2 = (float)m.AlternateOffset[2];
            float wf = (float)weight;
            double sx = (double)gw / w, sy = (double)gh / h;
            Parallel.For(0, h, y =>
            {
                double gy = y * sy;
                int y0 = Math.Min((int)gy, gh - 1), y1 = Math.Min(y0 + 1, gh - 1);
                float fy = (float)(gy - y0);
                Span<float> g = stackalloc float[3];
                for (int x = 0; x < w; x++)
                {
                    double gx = x * sx;
                    int x0 = Math.Min((int)gx, gw - 1), x1 = Math.Min(x0 + 1, gw - 1);
                    float fx = (float)(gx - x0);
                    for (int c = 0; c < gc; c++)
                    {
                        float a = gain[((long)y0 * gw + x0) * gc + c], b = gain[((long)y0 * gw + x1) * gc + c];
                        float d = gain[((long)y1 * gw + x0) * gc + c], e = gain[((long)y1 * gw + x1) * gc + c];
                        float top = a + (b - a) * fx, bottom = d + (e - d) * fx;
                        g[c] = top + (bottom - top) * fy;
                    }
                    if (gc == 1) g[1] = g[2] = g[0];
                    long si = ((long)y * w + x) * (alpha ? 4 : 3), di = ((long)y * w + x) * och;
                    float r = lin[si], gg = lin[si + 1], bb = lin[si + 2];
                    if (pre != null) Mul3(pre, ref r, ref gg, ref bb);
                    r = (r + kb0) * float.Exp2(wf * g[0]) - ka0;
                    gg = (gg + kb1) * float.Exp2(wf * g[1]) - ka1;
                    bb = (bb + kb2) * float.Exp2(wf * g[2]) - ka2;
                    if (post != null) Mul3(post, ref r, ref gg, ref bb);
                    dst[di] = r; dst[di + 1] = gg; dst[di + 2] = bb;
                    if (alpha) dst[di + 3] = lin[si + 3];
                }
            });
            var meta = new ImageMetadata { Transfer = TransferFunction.Linear, DpiX = baseline.Metadata?.DpiX ?? 0, DpiY = baseline.Metadata?.DpiY ?? 0, Orientation = 1 };
            meta.IccProfile = LinearProfile(altPrim);
            return new RawImage(w, h, alpha ? ColorModel.Rgba : ColorModel.Rgb, dst, och, meta);
        }

        /// <summary>
        /// Computes a gain map that turns <paramref name="baseline"/> into <paramref name="alternate"/> (ISO 21496-1
        /// Annex A): the log2 quotient (A.1), downsampled by box averages at co-sited positions (A.3.2), normalized by its
        /// minimum and maximum (A.3.3, extended to include 0 so that the Ultra HDR content boosts stay valid), raised to γ
        /// (A.3.4) and quantized to 8 bit. Both images have the same size and primaries; the baseline is linearized as in
        /// <see cref="Apply(RawImage, GainMap, double?)"/>, the alternate is linear light (float samples, 1.0 = HDR
        /// reference white; integer samples are linearized by their transfer function).
        /// </summary>
        public static GainMap Compute(RawImage baseline, RawImage alternate, GainMapOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(baseline);
            ArgumentNullException.ThrowIfNull(alternate);
            var o = options ?? new GainMapOptions();
            if (o.Scale < 1 || o.Scale > 128) throw new ArgumentException("gain map scale must be 1..128");
            if (!(o.Gamma > 0) || !double.IsFinite(o.Gamma)) throw new ArgumentException("gain map gamma must be above 0");
            if (!(o.BaseOffset >= 0) || !(o.AlternateOffset >= 0) || o.BaseOffset == 0 && o.AlternateOffset == 0)
                throw new ArgumentException("gain map offsets must not be negative (and not both 0)");
            // (both metadata forms store headrooms as unsigned values: ISO 21496-1 C.2.3, Ultra HDR HDRCapacityMin / Max)
            if (!(o.BaseHeadroom >= 0) || !double.IsFinite(o.BaseHeadroom) || o.AlternateHeadroom is double ah && (!(ah >= 0) || !double.IsFinite(ah)))
                throw new ArgumentException("gain map headrooms must not be negative");
            if (baseline.Width != alternate.Width || baseline.Height != alternate.Height) throw new ArgumentException("baseline and alternate differ in size");
            int w = baseline.Width, h = baseline.Height;
            var b = Linearize(baseline, out bool ba);
            var a = Linearize(alternate, out bool aa);
            int bs = ba ? 4 : 3, asz = aa ? 4 : 3;
            var prim = Primaries(baseline.Metadata?.IccProfile);
            double yr = prim[3], yg = prim[4], yb = prim[5];   // the luminance row of RGB -> XYZ
            double sum = yr + yg + yb; yr /= sum; yg /= sum; yb /= sum;
            int gc = o.Multichannel ? 3 : 1;
            double hb = o.BaseHeadroom;
            double sign = o.AlternateHeadroom is double hset && hset < hb ? -1 : 1;
            float kb = (float)o.BaseOffset, ka = (float)o.AlternateOffset;
            // A.1 at full resolution
            var full = new float[(long)w * h * gc];
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    long p = (long)y * w + x;
                    float br = Math.Max(b[p * bs], 0), bg = Math.Max(b[p * bs + 1], 0), bb = Math.Max(b[p * bs + 2], 0);
                    float ar = Math.Max(a[p * asz], 0), ag = Math.Max(a[p * asz + 1], 0), ab = Math.Max(a[p * asz + 2], 0);
                    if (gc == 1)
                    {
                        double yB = yr * br + yg * bg + yb * bb, yA = yr * ar + yg * ag + yb * ab;
                        full[p] = (float)(sign * Math.Log2((yA + ka) / (yB + kb)));
                    }
                    else
                    {
                        full[p * 3] = (float)(sign * Math.Log2((ar + ka) / (br + kb)));
                        full[p * 3 + 1] = (float)(sign * Math.Log2((ag + ka) / (bg + kb)));
                        full[p * 3 + 2] = (float)(sign * Math.Log2((ab + ka) / (bb + kb)));
                    }
                }
            });
            // A.3.2: box averages around the co-sited sample positions i * w / gw
            int s = o.Scale;
            int gw = Math.Max(1, (w + s - 1) / s), gh = Math.Max(1, (h + s - 1) / s);
            float[] g = full;
            if (gw != w || gh != h)
            {
                g = new float[(long)gw * gh * gc];
                Parallel.For(0, gh, j =>
                {
                    int cy = (int)((long)j * h / gh), ya = Math.Max(0, cy - s / 2), ye = Math.Min(h, cy - s / 2 + s);
                    if (ye <= ya) ye = ya + 1;
                    for (int i = 0; i < gw; i++)
                    {
                        int cx = (int)((long)i * w / gw), xa = Math.Max(0, cx - s / 2), xe = Math.Min(w, cx - s / 2 + s);
                        if (xe <= xa) xe = xa + 1;
                        for (int c = 0; c < gc; c++)
                        {
                            double acc = 0;
                            for (int y = ya; y < ye; y++)
                                for (int x = xa; x < xe; x++) acc += full[((long)y * w + x) * gc + c];
                            g[((long)j * gw + i) * gc + c] = (float)(acc / ((ye - ya) * (xe - xa)));
                        }
                    }
                });
            }
            // A.3.3: normalization range (per channel or common)
            var mn = new double[3]; var mx = new double[3];
            for (int c = 0; c < gc; c++) { mn[c] = double.MaxValue; mx[c] = double.MinValue; }
            for (long i = 0; i < g.LongLength; i++)
            {
                int c = (int)(i % gc);
                if (g[i] < mn[c]) mn[c] = g[i];
                if (g[i] > mx[c]) mx[c] = g[i];
            }
            for (int c = 0; c < gc; c++) { mn[c] = Math.Min(mn[c], 0); mx[c] = Math.Max(mx[c], 0); }
            for (int c = gc; c < 3; c++) { mn[c] = mn[0]; mx[c] = mx[0]; }
            double top = Math.Max(mx[0], Math.Max(mx[1], mx[2])), bottom = Math.Min(mn[0], Math.Min(mn[1], mn[2]));
            double ha = o.AlternateHeadroom ?? hb + sign * Math.Max(sign > 0 ? top : -bottom, 1 / 64.0);
            if (ha == hb) throw new ArgumentException("the alternate headroom must differ from the baseline headroom");
            var q = new byte[g.LongLength];
            double gamma = o.Gamma;
            for (long i = 0; i < g.LongLength; i++)
            {
                int c = (int)(i % gc);
                double range = mx[c] - mn[c];
                double n = range > 0 ? Math.Clamp((g[i] - mn[c]) / range, 0, 1) : 0;
                if (gamma != 1) n = Math.Pow(n, gamma);
                q[i] = (byte)Math.Floor(n * 255 + 0.5);
            }
            var md = new GainMapMetadata
            {
                IsMultichannel = o.Multichannel, UseBaseColourSpace = true,
                BaseHdrHeadroom = hb, AlternateHdrHeadroom = ha,
                GainMapMin = mn, GainMapMax = mx,
                Gamma = new[] { gamma, gamma, gamma },
                BaseOffset = new[] { o.BaseOffset, o.BaseOffset, o.BaseOffset },
                AlternateOffset = new[] { o.AlternateOffset, o.AlternateOffset, o.AlternateOffset },
            };
            return new GainMap
            {
                Metadata = md, Source = GainMapSource.Computed,
                Image = new RawImage(gw, gh, gc == 1 ? ColorModel.Gray : ColorModel.Rgb, q, gc),
            };
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>An image as linear Float32 RGB(A) with a linear profile of its primaries (what DecodeHdr returns without a gain map).</summary>
        internal static RawImage LinearRgb(RawImage img)
        {
            var lin = Linearize(img, out bool alpha);
            var meta = new ImageMetadata { Transfer = TransferFunction.Linear, DpiX = img.Metadata?.DpiX ?? 0, DpiY = img.Metadata?.DpiY ?? 0, Orientation = 1 };
            meta.IccProfile = LinearProfile(Primaries(img.Metadata?.IccProfile));
            return new RawImage(img.Width, img.Height, alpha ? ColorModel.Rgba : ColorModel.Rgb, lin, alpha ? 4 : 3, meta);
        }

        /// <summary>
        /// Formula (1) for every stored sample: log2 gains, 1 or 3 per pixel. Per-component metadata with 3 different
        /// values applies to R, G and B separately whatever the number of gain map components (5.2.5.1): the one sample of
        /// an achromatic gain map is then unnormalized with each channel's min(G), max(G) and γ, giving 3 gains.
        /// </summary>
        static float[] LogGains(RawImage gm, GainMapMetadata m, double max, out int gc)
        {
            int stored = gm.Color switch { ColorModel.Gray or ColorModel.GrayAlpha => 1, _ => gm.Channels >= 3 ? 3 : 1 };
            var lo = m.GainMapMin; var hi = m.GainMapMax; var gamma = m.Gamma;
            bool perChannel = lo[0] != lo[1] || lo[1] != lo[2] || hi[0] != hi[1] || hi[1] != hi[2] || gamma[0] != gamma[1] || gamma[1] != gamma[2];
            gc = stored == 3 || perChannel ? 3 : 1;
            int ch = gm.Channels, n = gm.Width * gm.Height;
            if (max <= 0) max = gm.SampleType is SampleType.Float16 or SampleType.Float32 or SampleType.Float64 ? 1 : Samples.MaxValue(gm.SampleType);
            var r = new float[(long)n * gc];
            float Un(double v, int c)
            {
                v = Math.Clamp(v / max, 0, 1);
                if (gamma[c] != 1) v = Math.Pow(v, 1 / gamma[c]);
                return (float)(v * (hi[c] - lo[c]) + lo[c]);
            }
            float[]?[] lut = new float[3][];
            if (gm.SampleType is SampleType.UInt8 or SampleType.UInt16)
                for (int c = 0; c < gc; c++)
                {
                    var t = lut[c] = new float[gm.SampleType == SampleType.UInt8 ? 256 : 65536];
                    for (int i = 0; i < t.Length; i++) t[i] = Un(i, c);
                }
            var data = gm.Data;
            for (int i = 0; i < n; i++)
                for (int c = 0; c < gc; c++)
                {
                    long s = (long)i * ch + (stored == 1 ? 0 : c);
                    r[(long)i * gc + c] = gm.SampleType switch
                    {
                        SampleType.UInt8 => lut[c]![((byte[])data)[s]],
                        SampleType.UInt16 => lut[c]![((ushort[])data)[s]],
                        SampleType.Float16 => Un((double)((Half[])data)[s], c),
                        SampleType.Float32 => Un(((float[])data)[s], c),
                        SampleType.Float64 => Un(((double[])data)[s], c),
                        _ => throw new ImageNotSupportedException($"gain map samples of type {gm.SampleType}"),
                    };
                }
            return r;
        }

        /// <summary>
        /// Linear RGB(A) floats of an image, 1.0 = HDR reference white: ICC matrix / TRC curves when the metadata has such a
        /// profile, else its transfer function (Unknown: sRGB for integer, linear for float samples). PQ is scaled so that
        /// 203 cd/m² is 1.0, HLG goes through the BT.2100 OOTF for a 1000 cd/m² display first.
        /// </summary>
        internal static float[] Linearize(RawImage img, out bool alpha)
        {
            int ch = img.Channels;
            int colours = img.Color switch { ColorModel.Gray or ColorModel.GrayAlpha => 1, ColorModel.Rgb or ColorModel.Rgba => 3, _ => ch >= 3 ? 3 : 1 };
            bool hasAlpha = img.Color is ColorModel.Rgba or ColorModel.GrayAlpha || img.Color == ColorModel.Other && ch == colours + 1;
            alpha = hasAlpha;
            bool isFloat = img.SampleType is SampleType.Float16 or SampleType.Float32 or SampleType.Float64;
            if (img.SampleType is not (SampleType.UInt8 or SampleType.UInt16) && !isFloat) throw new ImageNotSupportedException($"{img.SampleType} samples have no colour meaning");
            double max = isFloat ? 1 : Samples.MaxValue(img.SampleType);
            var t = img.Metadata?.Transfer ?? TransferFunction.Unknown;
            if (t == TransferFunction.Unknown) t = isFloat ? TransferFunction.Linear : TransferFunction.Srgb;
            double gamma = img.Metadata?.Gamma ?? 0;
            var curves = new Func<double, double>[3];
            var icc = img.Metadata?.IccProfile is { } ib ? IccProfile.TryParse(ib) : null;
            // a cicp tag (the HDR profiles of ISO 21496-1 C.4.4 / C.4.7 carry one) says the transfer, over the profile's curves
            if (icc?.Cicp is { } cicp && Heif.AvifReader.TransferOf(cicp.TransferCharacteristics, out double cg) is var ct && ct != TransferFunction.Unknown)
            {
                t = ct; gamma = cg;
            }
            if (icc != null && !isFloat && icc.IsMatrixShaper && t is not (TransferFunction.Pq or TransferFunction.Hlg or TransferFunction.Linear))
            {
                var trc = colours == 1 ? new[] { icc.GrayTrc, icc.GrayTrc, icc.GrayTrc } : new[] { icc.RedTrc, icc.GreenTrc, icc.BlueTrc };
                for (int c = 0; c < 3; c++) { var cv = trc[c]!; curves[c] = v => cv.Evaluate(Math.Clamp(v, 0, 1)); }
            }
            else
            {
                Func<double, double> f = t switch
                {
                    TransferFunction.Srgb => v => Samples.SrgbDecode(Math.Clamp(v, 0, 1)),
                    TransferFunction.Linear => v => v,
                    TransferFunction.Gamma when gamma > 0 => v => v <= 0 ? 0 : Math.Pow(v, 1 / gamma),
                    TransferFunction.Pq => v => PqEotf(v) * (10000 / 203.0),
                    TransferFunction.Hlg => HlgInverseOetf,
                    _ => v => Samples.SrgbDecode(Math.Clamp(v, 0, 1)),
                };
                curves[0] = curves[1] = curves[2] = f;
            }
            int n = img.Width * img.Height, och = hasAlpha ? 4 : 3;
            var r = new float[(long)n * och];
            float[]?[] lut = new float[3][];
            if (!isFloat)
                for (int c = 0; c < colours; c++)
                {
                    var tb = lut[c] = new float[(int)max + 1];
                    for (int i = 0; i < tb.Length; i++) tb[i] = (float)curves[c](i / max);
                }
            var data = img.Data;
            double Raw(long s) => img.SampleType switch
            {
                SampleType.UInt8 => ((byte[])data)[s],
                SampleType.UInt16 => ((ushort[])data)[s],
                SampleType.Float16 => (double)((Half[])data)[s],
                SampleType.Float32 => ((float[])data)[s],
                _ => ((double[])data)[s],
            };
            Parallel.For(0, img.Height, y =>
            {
                for (int x = 0; x < img.Width; x++)
                {
                    long i = (long)y * img.Width + x, s = i * ch, d = i * och;
                    for (int c = 0; c < 3; c++)
                    {
                        int sc = colours == 1 ? 0 : c;
                        r[d + c] = isFloat ? (float)curves[c](Raw(s + sc)) : lut[colours == 1 ? 0 : c]![(int)Raw(s + sc)];
                    }
                    if (hasAlpha) r[d + 3] = (float)(Raw(s + ch - 1) / max);
                }
            });
            if (t == TransferFunction.Hlg)
            {
                // BT.2100 HLG OOTF for a 1000 cd/m² display (system gamma 1.2, BT.2020 luminance), then 203 cd/m² = 1
                for (long i = 0; i < n; i++)
                {
                    long d = i * och;
                    double ys = 0.2627 * r[d] + 0.6780 * r[d + 1] + 0.0593 * r[d + 2];
                    double k = 1000 * Math.Pow(Math.Max(ys, 0), 0.2) / 203;
                    r[d] = (float)(r[d] * k); r[d + 1] = (float)(r[d + 1] * k); r[d + 2] = (float)(r[d + 2] * k);
                }
            }
            return r;
        }

        // SMPTE ST 2084: code value 0..1 -> luminance relative to 10000 cd/m²
        static double PqEotf(double e)
        {
            const double m1 = 2610.0 / 16384, m2 = 2523.0 / 4096 * 128, c1 = 3424.0 / 4096, c2 = 2413.0 / 4096 * 32, c3 = 2392.0 / 4096 * 32;
            double p = Math.Pow(Math.Clamp(e, 0, 1), 1 / m2);
            return Math.Pow(Math.Max(p - c1, 0) / (c2 - c3 * p), 1 / m1);
        }

        // BT.2100 HLG inverse OETF: signal 0..1 -> scene linear light 0..1
        static double HlgInverseOetf(double e)
        {
            const double a = 0.17883277, b = 0.28466892, c = 0.55991073;
            e = Math.Clamp(e, 0, 1);
            return e <= 0.5 ? e * e / 3 : (Math.Exp((e - c) / a) + b) / 12;
        }

        /// <summary>
        /// RGB -> PCS XYZ (D50) matrix, row-major, of an RGB profile: the primaries its cicp tag names (H.273), else its
        /// colorants; sRGB when there is neither (or another kind of profile).
        /// </summary>
        internal static double[] Primaries(byte[]? iccBytes)
        {
            var icc = iccBytes != null ? IccProfile.TryParse(iccBytes) : null;
            if (icc is { ColorSpace: IccColorSpace.Rgb, Cicp: { } cicp } && cicp.ColourPrimaries is not (0 or 2) && Heif.AvifColor.Primaries(cicp.ColourPrimaries, out var xy))
            {
                var white = new CieXy(xy[6], xy[7]);
                return IccMath.Mul(IccMath.BradfordAdaptation(white.ToXyz(), CieXyz.D50), IccMath.RgbToXyz(white, new CieXy(xy[0], xy[1]), new CieXy(xy[2], xy[3]), new CieXy(xy[4], xy[5])));
            }
            if (icc is not { ColorSpace: IccColorSpace.Rgb } || icc.RedColorant is not { } r || icc.GreenColorant is not { } g || icc.BlueColorant is not { } b)
            {
                var s = IccProfile.Srgb;
                r = s.RedColorant!.Value; g = s.GreenColorant!.Value; b = s.BlueColorant!.Value;
            }
            return new[] { r.X, g.X, b.X, r.Y, g.Y, b.Y, r.Z, g.Z, b.Z };
        }

        static bool SamePrimaries(double[] a, double[] b)
        {
            for (int i = 0; i < 9; i++) if (Math.Abs(a[i] - b[i]) > 2e-3) return false;
            return true;
        }

        static void Mul3(double[] m, ref float r, ref float g, ref float b)
        {
            float x = (float)(m[0] * r + m[1] * g + m[2] * b), y = (float)(m[3] * r + m[4] * g + m[5] * b), z = (float)(m[6] * r + m[7] * g + m[8] * b);
            r = x; g = y; b = z;
        }

        /// <summary>A linear matrix profile with the given (D50 adapted) colorants.</summary>
        internal static byte[] LinearProfile(double[] prim)
        {
            if (SamePrimaries(prim, Primaries(null))) return IccProfile.LinearSrgb.ToBytes();
            var r = new CieXyz(prim[0], prim[3], prim[6]).ToXy();
            var g = new CieXyz(prim[1], prim[4], prim[7]).ToXy();
            var b = new CieXyz(prim[2], prim[5], prim[8]).ToXy();
            return IccProfile.CreateRgb("Linear (gain map alternate)", CieXy.D50, r, g, b, IccCurve.Identity).ToBytes();
        }
    }

    /// <summary>Where the metadata of a <see cref="GainMap"/> came from.</summary>
    public enum GainMapSource
    {
        /// <summary>Made by <see cref="GainMap.Compute"/>.</summary>
        Computed,
        /// <summary>The ISO 21496-1 binary payload (JPEG APP2 segment, JPEG XL jhgm box).</summary>
        Iso21496,
        /// <summary>Ultra HDR / Adobe XMP (hdrgm namespace).</summary>
        Xmp,
    }
}
