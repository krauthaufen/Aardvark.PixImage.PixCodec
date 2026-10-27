using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Aardvark.ImageCodecs.Processing;

namespace Aardvark.ImageCodecs
{
    /// <summary>Settings for writing a JPEG with an HDR gain map (<see cref="Jpeg.EncodeUltraHdr(RawImage, Stream, UltraHdrEncodeOptions?)"/>).</summary>
    public sealed class UltraHdrEncodeOptions
    {
        /// <summary>Encoder settings of the SDR (baseline) image.</summary>
        public JpegEncodeOptions Base { get; set; } = new() { Quality = 90 };
        /// <summary>Encoder settings of the gain map image (an RGB gain map is always stored with 4:4:4 chroma).</summary>
        public JpegEncodeOptions GainMapJpeg { get; set; } = new() { Quality = 85 };
        /// <summary>How the gain map is computed.</summary>
        public GainMapOptions GainMap { get; set; } = new();
        /// <summary>Write the Ultra HDR v1 / Adobe XMP metadata (hdrgm, GContainer directory). Default on.</summary>
        public bool WriteXmp { get; set; } = true;
        /// <summary>Write the ISO 21496-1 APP2 segments (Annex C.4). Default on.</summary>
        public bool WriteIso21496 { get; set; } = true;
        /// <summary>
        /// For <see cref="Jpeg.EncodeUltraHdr(RawImage, Stream, UltraHdrEncodeOptions?)"/>: the HDR luminance (1.0 = SDR white)
        /// that the tone mapping maps to SDR white. Default: the brightest pixel (at least 1).
        /// </summary>
        public double? ToneMapWhite { get; set; }
    }

    /// <summary>Settings for <see cref="Jpeg.EncodeMpo(IReadOnlyList{RawImage}, Stream, MpoEncodeOptions?)"/>.</summary>
    public sealed class MpoEncodeOptions
    {
        public JpegEncodeOptions Jpeg { get; set; } = new();
        /// <summary>The MP Type of every view: <see cref="MpfImageType.Disparity"/> (stereo, default), <see cref="MpfImageType.MultiAngle"/>, <see cref="MpfImageType.Panorama"/> or <see cref="MpfImageType.Undefined"/>.</summary>
        public MpfImageType Type { get; set; } = MpfImageType.Disparity;
        /// <summary>Index of the representative image (the one viewers show by default).</summary>
        public int Representative { get; set; }
        /// <summary>ConvergenceAngle in degrees for disparity images (null: written as unknown).</summary>
        public double? ConvergenceAngle { get; set; }
        /// <summary>BaselineLength in metres for disparity images (null: written as unknown).</summary>
        public double? BaselineLength { get; set; }
    }

    public static unsafe partial class Jpeg
    {
        const string ContainerNs = "http://ns.google.com/photos/1.0/container/";
        const string ItemNs = "http://ns.google.com/photos/1.0/container/item/";
        const string RdfNs = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

        static long StartOrNegative(ReadOnlySpan<byte> data, JpegDecodeOptions o)
        {
            if (IsMatch(data)) return 0;
            return o.RequireSoiAtStart ? -1 : data.IndexOf((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]);
        }

        // ------------------------------------------------------------------ Multi-Picture Format

        /// <summary>
        /// The Multi-Picture Format index (CIPA DC-007) of the file: its individual images with types (baseline primary,
        /// gain map, large thumbnails, disparity / multi-angle / panorama views of MPO files), positions and MP attributes;
        /// null when the file has no MPF segment or it is malformed.
        /// </summary>
        public static MpfInfo? GetMultiPicture(ReadOnlySpan<byte> data, JpegDecodeOptions? options = null)
        {
            long start = StartOrNegative(data, options ?? s_defaults);
            if (start < 0) return null;
            var h = JpegHeaders.Read(data, start);
            return h is { MpfAt: >= 0 } ? Mpf.Parse(data, h.MpfAt, h.MpfLength, start) : null;
        }

        /// <summary>
        /// The JPEG stream of individual image <paramref name="index"/> of an MP file (0 = the primary image, which a file
        /// without MPF also has), from its SOI to its EOI.
        /// </summary>
        public static byte[] ExtractPicture(ReadOnlySpan<byte> data, int index)
        {
            var (off, len) = PictureRange(data, index, null);
            return data.Slice((int)off, (int)len).ToArray();
        }

        static (long Offset, long Length) PictureRange(ReadOnlySpan<byte> data, int index, JpegDecodeOptions? options)
        {
            options ??= s_defaults;
            if (index == 0)
            {
                long s = FindStart(data, options);
                long e = JpegHeaders.FindEnd(data, s);
                return (s, (e < 0 ? data.Length : e) - s);
            }
            var mpf = GetMultiPicture(data, options);
            if (mpf == null || index < 0 || index >= mpf.Images.Count) throw new ArgumentOutOfRangeException(nameof(index), "the file has no such MPF image");
            var img = mpf.Images[index];
            if (!img.IsPresent) throw new ImageFormatException($"JPEG: MPF image {index} is not where its MP Entry says");
            return (img.Offset, Math.Min(img.Size == 0 ? long.MaxValue : img.Size, data.Length - img.Offset));
        }

        /// <summary>
        /// Decodes individual image <paramref name="index"/> of an MP file (e.g. the right view of a stereo MPO); 0 is the
        /// primary image, what <see cref="Decode"/> returns. Each image is turned upright by its own EXIF orientation.
        /// </summary>
        public static RawImage DecodePicture(ReadOnlySpan<byte> data, int index, JpegDecodeOptions? options = null)
        {
            if (index == 0) return Decode(data, options);
            var (off, len) = PictureRange(data, index, options);
            return Decode(data.Slice((int)off, (int)len), options);
        }

        /// <summary>
        /// All views of an Extended MP file (.MPO: the disparity / multi-angle / panorama / undefined images in MP Entry
        /// order; views that cannot be found or decoded are left out); a list holding the primary image for any other JPEG.
        /// </summary>
        public static RawImage[] DecodeAll(ReadOnlySpan<byte> data, JpegDecodeOptions? options = null)
        {
            var mpf = GetMultiPicture(data, options);
            if (mpf == null || !mpf.IsExtended) return new[] { Decode(data, options) };
            var r = new List<RawImage>();
            foreach (var img in mpf.Images)
            {
                if (!img.IsMultiFrame) continue;
                if (img.Index == 0) { r.Add(Decode(data, options)); continue; }
                if (!img.IsPresent) continue;
                try { r.Add(Decode(data.Slice((int)img.Offset, (int)Math.Min(img.Size == 0 ? long.MaxValue : img.Size, data.Length - img.Offset)), options)); }
                catch (ImageFormatException) { }
                catch (ImageNotSupportedException) { }
            }
            return r.Count > 0 ? r.ToArray() : new[] { Decode(data, options) };
        }

        /// <summary>What GetInfo adds for MP files and gain maps; never throws.</summary>
        static (MpfInfo? Mpf, bool GainMap) MultiPictureInfo(ReadOnlySpan<byte> data, JpegDecodeOptions o)
        {
            try
            {
                long start = StartOrNegative(data, o);
                var h = start >= 0 ? JpegHeaders.Read(data, start) : null;
                if (h == null) return (null, false);
                var mpf = h.MpfAt >= 0 ? Mpf.Parse(data, h.MpfAt, h.MpfLength, start) : null;
                bool gm = h.IsoAt >= 0 && h.IsoLength >= 4 && h.Iso(data)[0] == 0 && h.Iso(data)[1] == 0;
                if (mpf != null) foreach (var i in mpf.Images) gm |= i.Type == MpfImageType.GainMap;
                if (!gm && h.Xmp != null && h.Xmp.AsSpan().IndexOf("hdr-gain-map/1.0/"u8) >= 0) gm = h.ParseXmp() is { } x && Hdrgm(x, "Version") == "1.0";
                return (mpf, gm);
            }
            catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or ImageFormatException) { return (null, false); }
        }

        static string? Hdrgm(XmpData x, string name)
        {
            foreach (var p in x.Properties)
                if (p.Namespace == GainMapMetadata.HdrgmNs && !p.Key.Contains('/') && p.Key.EndsWith(":" + name, StringComparison.Ordinal)) return p.Value.Trim();
            return null;
        }

        // ------------------------------------------------------------------ gain maps

        /// <summary>
        /// The HDR gain map of a JPEG file, or null when there is none or it is unusable (Ultra HDR v1.1: invalid metadata
        /// means showing the SDR image). Both signallings are read; ISO 21496-1 metadata (APP2 "urn:iso:std:iso:ts:21496:-1"
        /// in the gain map image, Annex C.4; used only when the primary has the version segment too, C.4.3) wins over Ultra
        /// HDR / Adobe XMP (hdrgm). The gain map image is found through
        /// its MPF entry (MP Type gain map), the GContainer directory of the primary's XMP (Item:Semantic GainMap), or as an
        /// MPF image whose headers carry gain map metadata. Its image is decoded and turned upright with the primary's
        /// orientation (unless <see cref="JpegDecodeOptions.ApplyOrientation"/> is off); for ISO 21496-1 metadata, an ICC
        /// profile in the gain map image gives the alternate image's colour space (C.4.7).
        /// </summary>
        public static GainMap? GetGainMap(ReadOnlySpan<byte> data, JpegDecodeOptions? options = null)
        {
            options ??= s_defaults;
            try { return FindGainMap(data, options); }
            catch (Exception e) when (e is ImageFormatException or ImageNotSupportedException or ArgumentException or IndexOutOfRangeException or OverflowException) { return null; }
        }

        static GainMap? FindGainMap(ReadOnlySpan<byte> data, JpegDecodeOptions options)
        {
            long start = StartOrNegative(data, options);
            var h = start >= 0 ? JpegHeaders.Read(data, start) : null;
            if (h == null) return null;
            var mpf = h.MpfAt >= 0 ? Mpf.Parse(data, h.MpfAt, h.MpfLength, start) : null;
            var xmp = h.ParseXmp();
            // candidate positions, most specific first
            var candidates = new List<long>();
            if (mpf != null)
                foreach (var i in mpf.Images) if (i.Type == MpfImageType.GainMap && i.IsPresent) candidates.Add(i.Offset);
            if (xmp != null && Directory(xmp) is { Count: > 0 } dir && dir[0].Semantic == "Primary")
            {
                long end = JpegHeaders.FindEnd(data, start);
                if (end > 0)
                {
                    long pos = end + dir[0].Padding;
                    for (int k = 1; k < dir.Count; k++)
                    {
                        if (dir[k].Semantic == "GainMap" && dir[k].Mime is null or "image/jpeg") { candidates.Add(pos); break; }
                        pos += dir[k].Length + dir[k].Padding;
                    }
                }
            }
            if (mpf != null)
                foreach (var i in mpf.Images)
                    if (i.Index > 0 && i.IsPresent && i.Type is not (MpfImageType.GainMap or MpfImageType.LargeThumbnail)) candidates.Add(i.Offset);
            bool primaryIso = h.IsoAt >= 0;
            if (primaryIso && h.IsoLength >= 2 && (h.Iso(data)[0] != 0 || h.Iso(data)[1] != 0)) primaryIso = false;   // unknown minimum_version
            var tried = new HashSet<long>();
            foreach (var at in candidates)
            {
                if (!tried.Add(at)) continue;
                var gh = JpegHeaders.Read(data, at);
                if (gh == null) continue;
                GainMapMetadata? meta = null;
                var source = GainMapSource.Iso21496;
                // C.4.3: a conforming file has the GainMapVersion APP2 in the baseline too; without it the ISO metadata is
                // ignored (4.1 NOTE) and XMP is used if there is any
                if (gh.IsoAt >= 0 && primaryIso)
                {
                    try { meta = GainMapMetadata.FromIso21496(gh.Iso(data)); } catch (ImageFormatException) { meta = null; }
                }
                if (meta == null && gh.ParseXmp() is { } gx) { meta = GainMapMetadata.FromHdrgm(gx); source = GainMapSource.Xmp; }
                // (Adobe style files may keep the whole hdrgm description in the primary)
                if (meta == null && xmp != null) { meta = GainMapMetadata.FromHdrgm(xmp); source = GainMapSource.Xmp; }
                if (meta == null) continue;
                long end = JpegHeaders.FindEnd(data, at);
                var stream = data.Slice((int)at, (int)((end < 0 ? data.Length : end) - at));
                var gopt = new JpegDecodeOptions { ApplyOrientation = false };
                var info = GetInfo(stream, gopt);
                var img = Decode(stream, gopt);
                img.Metadata = null;
                int orientation = h.Exif != null ? JpegImpl.JpegMeta.ExifOrientation(h.Exif) : 0;
                if (options.ApplyOrientation && orientation > 1) img = ImageOps.ApplyOrientation(img, orientation);
                return new GainMap
                {
                    Metadata = meta, Image = img, Source = source,
                    AlternateIcc = source == GainMapSource.Iso21496 ? gh.Icc : null,
                    ImageMax = info.BitsPerSample is > 8 and < 16 ? (1 << info.BitsPerSample) - 1 : 0,
                    BaselineIsSrgb = ExifColorSpaceIsSrgb(h.Exif),
                };
            }
            return null;
        }

        /// <summary>Whether an EXIF blob has ColorSpace (A001.H) = 1, sRGB.</summary>
        static bool ExifColorSpaceIsSrgb(byte[]? exif) => exif != null && ExifData.Parse(exif)?.Get(ExifIfd.Exif, 0xA001)?.GetDouble() == 1;

        /// <summary>The GContainer directory (Item:Semantic, Item:Mime, Item:Length, Item:Padding of each item, in order).</summary>
        static List<(string? Semantic, string? Mime, long Length, long Padding)> Directory(XmpData x)
        {
            var items = new SortedDictionary<int, (string? Semantic, string? Mime, long Length, long Padding)>();
            var rx = new Regex(@"\[(\d+)\]");
            foreach (var p in x.Properties)
            {
                if (p.Namespace != ItemNs) continue;
                var m = rx.Match(p.Key);
                if (!m.Success || !int.TryParse(m.Groups[1].Value, out int i)) continue;
                items.TryGetValue(i, out var it);
                string local = p.Key[(p.Key.LastIndexOf(':') + 1)..];
                long Num() => long.TryParse(p.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) && v >= 0 ? v : 0;
                switch (local)
                {
                    case "Semantic": it.Semantic = p.Value.Trim(); break;
                    case "Mime": it.Mime = p.Value.Trim(); break;
                    case "Length": it.Length = Num(); break;
                    case "Padding": it.Padding = Num(); break;
                }
                items[i] = it;
            }
            return new List<(string?, string?, long, long)>(items.Values);
        }

        /// <summary>
        /// The alternate (HDR) rendition of a JPEG with a gain map as linear Float32 RGB, 1.0 = SDR white, for a display with
        /// HDR headroom <paramref name="headroom"/> (log2; null = the full alternate rendition): <see cref="Decode"/> plus
        /// <see cref="GainMap.Apply(RawImage, GainMap, double?)"/>. Without a usable gain map the baseline converted to
        /// linear light is returned. EXIF ColorSpace 1 declares the baseline sRGB whatever ICC profile it has (ISO 21496-1 C.4.4).
        /// </summary>
        public static RawImage DecodeHdr(ReadOnlySpan<byte> data, double? headroom = null, JpegDecodeOptions? options = null)
        {
            var baseline = Decode(data, options);
            var gm = GetGainMap(data, options);
            // (the gain map applies C.4.4 itself; without one the rule is followed all the same)
            if (gm == null) return GainMap.LinearRgb(ExifColorSpaceIsSrgb(baseline.Metadata?.Exif) && baseline.Metadata!.IccProfile != null ? GainMap.WithoutIcc(baseline) : baseline);
            return GainMap.Apply(baseline, gm, headroom);
        }

        // ------------------------------------------------------------------ writing gain maps

        /// <summary>
        /// Writes an Ultra HDR JPEG from an HDR image (linear light, 1.0 = SDR white; integer images with a PQ / HLG / sRGB
        /// transfer are linearized first): the SDR baseline comes from a global tone mapping (extended Reinhard on the
        /// luminance, white point <see cref="UltraHdrEncodeOptions.ToneMapWhite"/>), then as
        /// <see cref="EncodeUltraHdr(RawImage, RawImage, Stream, UltraHdrEncodeOptions?)"/>.
        /// </summary>
        public static void EncodeUltraHdr(RawImage hdr, Stream output, UltraHdrEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(hdr);
            options ??= new UltraHdrEncodeOptions();
            var lin = GainMap.Linearize(hdr, out bool alpha);
            int n = hdr.Width * hdr.Height, cs = alpha ? 4 : 3;
            var prim = GainMap.Primaries(hdr.Metadata?.IccProfile);
            double yr = prim[3], yg = prim[4], yb = prim[5], ys = yr + yg + yb;
            yr /= ys; yg /= ys; yb /= ys;
            double white = options.ToneMapWhite ?? 0;
            if (white <= 0)
                for (int i = 0; i < n; i++) white = Math.Max(white, yr * lin[i * cs] + yg * lin[i * cs + 1] + yb * lin[i * cs + 2]);
            white = Math.Max(white, 1);
            var sdr = new byte[n * 3];
            for (int i = 0; i < n; i++)
            {
                double r = Math.Max(lin[i * cs], 0), g = Math.Max(lin[i * cs + 1], 0), b = Math.Max(lin[i * cs + 2], 0);
                double l = yr * r + yg * g + yb * b;
                double k = l > 0 ? (1 + l / (white * white)) / (1 + l) : 1;
                sdr[i * 3] = (byte)Math.Floor(Samples.SrgbEncode(Math.Clamp(r * k, 0, 1)) * 255 + 0.5);
                sdr[i * 3 + 1] = (byte)Math.Floor(Samples.SrgbEncode(Math.Clamp(g * k, 0, 1)) * 255 + 0.5);
                sdr[i * 3 + 2] = (byte)Math.Floor(Samples.SrgbEncode(Math.Clamp(b * k, 0, 1)) * 255 + 0.5);
            }
            var hm = hdr.Metadata;
            var meta = new ImageMetadata
            {
                Exif = hm?.Exif, Xmp = hm?.Xmp, DpiX = hm?.DpiX ?? 0, DpiY = hm?.DpiY ?? 0, Orientation = hm?.Orientation ?? 0,
                IccProfile = SrgbCurveProfile(prim),
            };
            var linear = new RawImage(hdr.Width, hdr.Height, alpha ? ColorModel.Rgba : ColorModel.Rgb, lin, cs,
                new ImageMetadata { Transfer = TransferFunction.Linear, IccProfile = meta.IccProfile });
            EncodeUltraHdr(new RawImage(hdr.Width, hdr.Height, ColorModel.Rgb, sdr, 3, meta), linear, output, options);
        }

        /// <summary>A profile with the sRGB curve and the given primaries (sRGB itself when they are sRGB's).</summary>
        static byte[] SrgbCurveProfile(double[] prim)
        {
            var s = GainMap.Primaries(null);
            bool same = true;
            for (int i = 0; i < 9; i++) same &= Math.Abs(prim[i] - s[i]) < 2e-3;
            if (same) return IccProfile.Srgb.ToBytes();
            return IccProfile.CreateRgb("RGB (sRGB curve)", CieXy.D50, new CieXyz(prim[0], prim[3], prim[6]).ToXy(),
                new CieXyz(prim[1], prim[4], prim[7]).ToXy(), new CieXyz(prim[2], prim[5], prim[8]).ToXy(), IccCurve.Srgb).ToBytes();
        }

        /// <summary>
        /// Writes an Ultra HDR JPEG from an SDR rendition (8 bit Gray / Rgb, written as the baseline with its EXIF, XMP and
        /// ICC metadata; without an ICC profile sRGB is recorded) and an HDR rendition of the same size and primaries
        /// (linear light, 1.0 = SDR white). The gain map is computed against the baseline as it decodes after JPEG
        /// compression, so the compression error of the baseline is corrected by it.
        /// </summary>
        public static void EncodeUltraHdr(RawImage sdr, RawImage hdr, Stream output, UltraHdrEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(sdr);
            ArgumentNullException.ThrowIfNull(hdr);
            options ??= new UltraHdrEncodeOptions();
            var meta = CopyMeta(sdr.Metadata);
            meta.IccProfile ??= IccProfile.Srgb.ToBytes();
            var baseJpeg = EncodeToArray(new RawImage(sdr.Width, sdr.Height, sdr.Color, sdr.Data, sdr.Channels, Without(meta)), options.Base);
            var decoded = Decode(baseJpeg, new JpegDecodeOptions { ApplyOrientation = false });
            decoded.Metadata = new ImageMetadata { IccProfile = meta.IccProfile };
            var gm = GainMap.Compute(decoded, hdr, options.GainMap);
            WriteGainMapFile(baseJpeg, meta.Xmp, gm, output, options);
        }

        /// <summary>
        /// Writes <paramref name="baseline"/> as a JPEG with an existing gain map (for instance one read with
        /// <see cref="GetGainMap"/>, or made by <see cref="GainMap.Compute"/>): MPF, Ultra HDR XMP and / or ISO 21496-1
        /// metadata as <paramref name="options"/> say. The gain map image must be upright like the baseline.
        /// </summary>
        public static void EncodeWithGainMap(RawImage baseline, GainMap gainMap, Stream output, UltraHdrEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(baseline);
            ArgumentNullException.ThrowIfNull(gainMap);
            options ??= new UltraHdrEncodeOptions();
            var meta = CopyMeta(baseline.Metadata);
            meta.IccProfile ??= IccProfile.Srgb.ToBytes();
            var baseJpeg = EncodeToArray(new RawImage(baseline.Width, baseline.Height, baseline.Color, baseline.Data, baseline.Channels, Without(meta)), options.Base);
            WriteGainMapFile(baseJpeg, meta.Xmp, gainMap, output, options);
        }

        static ImageMetadata CopyMeta(ImageMetadata? m) => m == null ? new ImageMetadata() : new ImageMetadata
        {
            IccProfile = m.IccProfile, Exif = m.Exif, Xmp = m.Xmp, DpiX = m.DpiX, DpiY = m.DpiY, Orientation = m.Orientation,
        };

        static ImageMetadata Without(ImageMetadata m)
        {
            var c = CopyMeta(m);
            c.Xmp = null;
            return c;
        }

        static byte[] EncodeToArray(RawImage img, JpegEncodeOptions o)
        {
            var ms = new MemoryStream();
            Encode(img, ms, o);
            return ms.ToArray();
        }

        static void WriteGainMapFile(byte[] baseJpeg, byte[]? userXmp, GainMap gm, Stream output, UltraHdrEncodeOptions o)
        {
            if (!gm.Metadata.IsValid()) throw new ArgumentException("invalid gain map metadata");
            if (!o.WriteXmp && !o.WriteIso21496) throw new ArgumentException("write XMP, ISO 21496-1 metadata or both");
            var img = gm.Image;
            if (img.SampleType != SampleType.UInt8 || img.Color is not (ColorModel.Gray or ColorModel.Rgb))
                throw new ImageNotSupportedException("JPEG gain maps are 8 bit Gray or Rgb images");
            var go = o.GainMapJpeg;
            var gopt = new JpegEncodeOptions
            {
                Quality = go.Quality, OptimizeHuffman = go.OptimizeHuffman, QuantTables = go.QuantTables, Trellis = go.Trellis,
                Progressive = go.Progressive, Arithmetic = go.Arithmetic, RestartInterval = go.RestartInterval,
                Subsampling = JpegSubsampling.Chroma444,
            };
            var gjpeg = EncodeToArray(new RawImage(img.Width, img.Height, img.Color, img.Data, img.Channels), gopt);
            // the gain map image: its XMP (hdrgm) and the full ISO 21496-1 metadata (C.4.5, C.4.6)
            var gsegs = new MemoryStream();
            if (o.WriteXmp)
            {
                var x = BuildXmp(null, gm.Metadata.HdrgmAttributes(), gm.Metadata.HdrgmElements());
                foreach (var pl in XmpEditor.JpegSegments(x)) gsegs.Write(Mpf.App(0xE1, pl, default));
            }
            if (o.WriteIso21496) gsegs.Write(Mpf.App(0xE2, GainMapMetadata.Urn, gm.Metadata.ToIso21496()));
            var gbody = Mpf.Insert(gjpeg, gsegs.ToArray());
            // the primary: GContainer directory and hdrgm:Version, the ISO 21496-1 version (C.4.3)
            var psegs = new List<byte[]>();
            if (o.WriteXmp)
            {
                string dir = "<Container:Directory><rdf:Seq>"
                    + "<rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Semantic=\"Primary\" Item:Mime=\"image/jpeg\"/></rdf:li>"
                    + $"<rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Semantic=\"GainMap\" Item:Mime=\"image/jpeg\" Item:Length=\"{gbody.Length}\"/></rdf:li>"
                    + "</rdf:Seq></Container:Directory>";
                var x = BuildXmp(userXmp, $" xmlns:Container=\"{ContainerNs}\" xmlns:Item=\"{ItemNs}\" xmlns:hdrgm=\"{GainMapMetadata.HdrgmNs}\" hdrgm:Version=\"1.0\"", dir);
                foreach (var pl in XmpEditor.JpegSegments(x)) psegs.Add(Mpf.App(0xE1, pl, default));
            }
            else if (userXmp is { Length: > 0 })
                foreach (var pl in XmpEditor.JpegSegments(userXmp)) psegs.Add(Mpf.App(0xE1, pl, default));
            if (o.WriteIso21496) psegs.Add(Mpf.App(0xE2, GainMapMetadata.Urn, GainMapMetadata.VersionOnly()));
            var parts = new[]
            {
                new Mpf.Part { Jpeg = baseJpeg, TypeCode = Mpf.TypeBaselinePrimary, Representative = true, Parent = true, Dependent1 = 2 },
                new Mpf.Part { Jpeg = gbody, TypeCode = Mpf.TypeGainMap, Child = true },
            };
            Mpf.Write(parts, output, i => i == 0 ? psegs : Array.Empty<byte[]>());
        }

        /// <summary>
        /// An XMP packet: <paramref name="existing"/> without any hdrgm / GContainer properties, plus an rdf:Description with
        /// the given attributes and child elements (or a new packet holding just that description).
        /// </summary>
        static byte[] BuildXmp(byte[]? existing, string attributes, string children)
        {
            XmlDocument? doc = existing is { Length: > 0 } ? XmpEditor.Load(existing) : null;
            if (doc == null || doc.GetElementsByTagName("RDF", RdfNs).Count == 0) doc = XmpEditor.NewDocument();
            var rdf = (XmlElement)doc.GetElementsByTagName("RDF", RdfNs)[0]!;
            foreach (XmlNode n in rdf.ChildNodes)
            {
                if (n is not XmlElement d) continue;
                var drop = new List<XmlNode>();
                foreach (XmlAttribute a in d.Attributes) if (a.NamespaceURI is GainMapMetadata.HdrgmNs or ContainerNs or ItemNs) drop.Add(a);
                foreach (XmlNode c in d.ChildNodes) if (c.NamespaceURI is GainMapMetadata.HdrgmNs or ContainerNs or ItemNs) drop.Add(c);
                foreach (var x in drop) { if (x is XmlAttribute xa) d.Attributes.Remove(xa); else d.RemoveChild(x); }
            }
            var frag = new XmlDocument { XmlResolver = null };
            frag.LoadXml($"<rdf:Description xmlns:rdf=\"{RdfNs}\" rdf:about=\"\"{attributes}>{children}</rdf:Description>");
            rdf.AppendChild(doc.ImportNode(frag.DocumentElement!, true));
            return XmpEditor.Serialize(doc, 256);
        }

        // ------------------------------------------------------------------ MPO

        /// <summary>
        /// Writes an Extended MP file (.MPO, CIPA DC-007) from several images (stereo pairs: left view first), each encoded
        /// as a JPEG with its own metadata. Every image gets MPIndividualNum 1, 2, ... (the viewpoint order for disparity
        /// images: leftmost first), disparity and multi-angle images BaseViewpointNum 1; disparity images also record
        /// <see cref="MpoEncodeOptions.ConvergenceAngle"/> and <see cref="MpoEncodeOptions.BaselineLength"/>.
        /// </summary>
        public static void EncodeMpo(IReadOnlyList<RawImage> views, Stream output, MpoEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(views);
            options ??= new MpoEncodeOptions();
            var jpegs = new List<byte[]>();
            foreach (var v in views) jpegs.Add(EncodeToArray(v, options.Jpeg));
            WriteMpo(jpegs, output, options);
        }

        /// <summary>
        /// Combines complete JPEG streams into an Extended MP file as <see cref="EncodeMpo"/> does (MPF segments already in
        /// them are dropped).
        /// </summary>
        public static void WriteMpo(IReadOnlyList<byte[]> jpegs, Stream output, MpoEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(jpegs);
            ArgumentNullException.ThrowIfNull(output);
            options ??= new MpoEncodeOptions();
            if (jpegs.Count < 1) throw new ArgumentException("an MP file needs at least one image");
            if (options.Type is not (MpfImageType.Disparity or MpfImageType.MultiAngle or MpfImageType.Panorama or MpfImageType.Undefined))
                throw new ArgumentException("MPO views are disparity, multi-angle, panorama or undefined images");
            if (options.Representative < 0 || options.Representative >= jpegs.Count) throw new ArgumentOutOfRangeException(nameof(options), "representative image index");
            int code = Mpf.CodeOf(options.Type);
            var parts = new List<Mpf.Part>();
            for (int i = 0; i < jpegs.Count; i++)
            {
                var p = new Mpf.Part { Jpeg = StripMpf(jpegs[i]), TypeCode = code, Representative = i == options.Representative };
                if (options.Type != MpfImageType.Undefined) p.Attributes[0xB101] = (4, new[] { (uint)(i + 1) });
                if (options.Type is MpfImageType.Disparity or MpfImageType.MultiAngle) p.Attributes[0xB204] = (4, new uint[] { 1 });
                if (options.Type == MpfImageType.Disparity)
                {
                    p.Attributes[0xB205] = (10, Rational(options.ConvergenceAngle, true));
                    p.Attributes[0xB206] = (5, Rational(options.BaselineLength, false));
                }
                parts.Add(p);
            }
            Mpf.Write(parts, output);
        }

        static uint[] Rational(double? v, bool signed)
        {
            if (v is not double d || !double.IsFinite(d)) return new[] { 0xFFFFFFFFu, 0xFFFFFFFFu };
            uint den = 10000;
            if (signed) return new[] { (uint)(int)Math.Round(d * den), den };
            return new[] { (uint)Math.Round(Math.Max(d, 0) * den), den };
        }

        /// <summary>The stream without MPF APP2 segments in front of its first scan.</summary>
        static byte[] StripMpf(byte[] jpeg)
        {
            var h = JpegHeaders.Read(jpeg, 0) ?? throw new ArgumentException("not a JPEG stream");
            if (h.MpfAt < 0) return jpeg;
            var ms = new MemoryStream(jpeg.Length);
            int p = 0;
            foreach (var (m, s, n) in h.Segments)
            {
                if (m != 0xE2 || !jpeg.AsSpan(s).StartsWith("MPF\0"u8)) continue;
                ms.Write(jpeg, p, s - 4 - p);
                p = s + n;
            }
            ms.Write(jpeg, p, jpeg.Length - p);
            return ms.ToArray();
        }
    }
}
