using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Aardvark.ImageCodecs.Av1;
using Aardvark.ImageCodecs.Heif;

namespace Aardvark.ImageCodecs
{
    public sealed class AvifDecodeOptions
    {
        /// <summary>
        /// Apply the rotation (irot) and mirroring (imir) item properties (default), so that the image comes back upright
        /// with metadata orientation 1. False keeps the stored pixel order and reports the orientation in the metadata.
        /// Cropping (clap) is always applied.
        /// </summary>
        public bool ApplyOrientation { get; set; } = true;
        /// <summary>Synthesize film grain when the AV1 stream asks for it (default true).</summary>
        public bool ApplyFilmGrain { get; set; } = true;
        /// <summary>Decode tiles, loop filters and colour conversion on several threads (same result). Default true.</summary>
        public bool Multithreaded { get; set; } = true;
        /// <summary>Upper bound for each image or frame in pixels; larger files are rejected with <see cref="ImageNotSupportedException"/>.</summary>
        public long MaxPixels { get; set; } = 1L << 28;
        /// <summary>Leave out the alpha auxiliary image.</summary>
        public bool IgnoreAlpha { get; set; }
        /// <summary>Test hook: leave out the clean aperture crop (libavif's tools return the uncropped image).</summary>
        internal bool ApplyCrop { get; set; } = true;

        internal static readonly AvifDecodeOptions Default = new();
    }

    /// <summary>One frame of an AVIF image sequence.</summary>
    public sealed class AvifFrame
    {
        public int Index { get; internal set; }
        /// <summary>Display time in track timescale units.</summary>
        public uint DurationTicks { get; internal set; }
        /// <summary>Display time in milliseconds.</summary>
        public double DurationMs { get; internal set; }
        /// <summary>Whether the sample is a sync sample (key frame).</summary>
        public bool IsSync { get; internal set; }
        public RawImage Image { get; internal set; } = null!;
    }

    /// <summary>The frames of an AVIF image sequence (avis), or the single still image.</summary>
    public sealed class AvifAnimation
    {
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public bool IsAnimated { get; internal set; }
        /// <summary>Number of times to play, 0 = forever (MIAF Amd 1 Table C.1 of the edit list and track duration).</summary>
        public int LoopCount { get; internal set; } = 1;
        /// <summary>Time units per second of the track.</summary>
        public uint Timescale { get; internal set; }
        public IReadOnlyList<AvifFrame> Frames { get; internal set; } = Array.Empty<AvifFrame>();
    }

    /// <summary>
    /// The HDR gain map of an AVIF file: a tone map derived image item ('tmap') holding ISO 21496-1 metadata, whose inputs
    /// are the base image and the gain map image; its colr properties give the colour space of the alternate image.
    /// <see cref="Avif.Decode(ReadOnlySpan{byte}, AvifDecodeOptions)"/> returns the base image, <see cref="Avif.DecodeHdr"/>
    /// the alternate one.
    /// </summary>
    public sealed class AvifGainMap
    {
        /// <summary>Version byte of the tmap item: 0; items of other versions are to be ignored (<see cref="ParsedMetadata"/> is null).</summary>
        public int Version { get; internal set; }
        /// <summary>The ISO 21496-1 binary gain map metadata (Annex C.2), as stored after the version byte.</summary>
        public byte[] Metadata { get; internal set; } = Array.Empty<byte>();
        /// <summary>Transfer function of the alternate image (from the tmap item's nclx), Unknown when not given.</summary>
        public TransferFunction AlternateTransfer { get; internal set; } = TransferFunction.Unknown;
        /// <summary>nclx colour primaries / transfer / matrix of the alternate image, 2 (unspecified) when not given.</summary>
        public int AlternatePrimaries { get; internal set; } = 2;
        public int AlternateTransferCharacteristics { get; internal set; } = 2;
        /// <summary>ICC profile of the alternate image, if the tmap item has one.</summary>
        public byte[]? AlternateIcc { get; internal set; }
        /// <summary>Width and height of the tmap item (the alternate image).</summary>
        public int Width { get; internal set; }
        public int Height { get; internal set; }

        internal AvifReader Reader = null!;
        internal uint GainMapItemId, BaseItemId, TmapItemId;
        internal bool ApplyOrientation = true, TmapIsPrimary;

        /// <summary>
        /// The gain map of a tmap item (null without both inputs or a payload), for the image of its base (with
        /// <paramref name="ofTmap"/>: of the tmap item's output image, the tmap item's transforms included).
        /// </summary>
        internal static AvifGainMap? Of(AvifReader rd, HeifItem it, bool ofTmap, bool applyOrientation)
        {
            var refs = it.Refs("dimg");
            if (refs.Length < 2 || !rd.File.Items.ContainsKey(refs[0]) || !rd.File.Items.ContainsKey(refs[1])) return null;
            var payload = rd.File.ItemData(rd.Data, it);
            if (payload.Length < 1) return null;
            var c = rd.Colour(it);
            var size = rd.Ispe(it);
            return new AvifGainMap
            {
                Version = payload[0],
                Metadata = payload.AsSpan(1).ToArray(),
                AlternatePrimaries = c.HasNclx ? c.Primaries : 2,
                AlternateTransferCharacteristics = c.HasNclx ? c.Transfer : 2,
                AlternateTransfer = c.HasNclx ? AvifReader.TransferOf(c.Transfer, out _) : TransferFunction.Unknown,
                AlternateIcc = c.Icc,
                Width = size?.w ?? 0, Height = size?.h ?? 0,
                Reader = rd, GainMapItemId = refs[1], BaseItemId = refs[0], TmapItemId = it.Id, TmapIsPrimary = ofTmap,
                ApplyOrientation = applyOrientation,
            };
        }

        /// <summary>
        /// Decodes the gain map image: its stored pixels, which match the base image's stored pixels (scaled), without any
        /// transformative property (libavif repeats the base image's on the gain map item).
        /// </summary>
        public RawImage DecodeImage() => Reader.DecodeItem(Reader.File.Items[GainMapItemId], false, stored: true);

        /// <summary>
        /// <see cref="Metadata"/> parsed (ISO 21496-1 C.2); null for a tmap version other than 0, an unknown minimum_version,
        /// a truncated payload or values clause 5 does not allow.
        /// </summary>
        public GainMapMetadata? ParsedMetadata
        {
            get
            {
                if (Version != 0) return null;
                try { return GainMapMetadata.FromIso21496(Metadata); }
                catch (ImageFormatException) { return null; }
            }
        }

        /// <summary>
        /// The gain map in the format independent form that <see cref="GainMap.Apply(RawImage, GainMap, double?)"/> takes
        /// (as JPEG and JPEG XL files give it), for the image <see cref="Avif.Decode(ReadOnlySpan{byte}, AvifDecodeOptions)"/>
        /// returns: the stored gain map image with the base image's clean aperture (scaled to the gain map's size), rotation
        /// and mirroring (ISO 21496-1 4.5), then a tmap primary item's own, the alternate colour space from the tmap item's ICC profile or nclx (none: the
        /// base's, 5.3.3). Null when the metadata is unusable.
        /// </summary>
        public GainMap? ToGainMap()
        {
            var m = ParsedMetadata;
            if (m == null) return null;
            var rd = Reader;
            var gi = rd.File.Items[GainMapItemId];
            var img = DecodeImage();
            img.Metadata = null;
            var bi = rd.File.Items[BaseItemId];
            // the transforms of the base and, when Decode returns the tmap item's output image, of the tmap item
            var chain = TmapIsPrimary ? new[] { bi, rd.File.Items[TmapItemId] } : new[] { bi };
            if (rd.Ispe(bi) is var (bw, bh) && bw > 0 && bh > 0)
            {
                foreach (var t in chain.SelectMany(rd.Transforms))
                {
                    switch (t.Kind)
                    {
                        case 'c':
                            if (rd.ApplyCrop && AvifReader.Clap(t.Clap!, bw, bh) is var (x, y, cw, ch))
                            {
                                // the base's crop rectangle in gain map pixels
                                double sx = (double)img.Width / bw, sy = (double)img.Height / bh;
                                int gx = Math.Clamp((int)Math.Round(x * sx), 0, img.Width - 1), gy = Math.Clamp((int)Math.Round(y * sy), 0, img.Height - 1);
                                int gw = Math.Clamp((int)Math.Round(cw * sx), 1, img.Width - gx), gh = Math.Clamp((int)Math.Round(ch * sy), 1, img.Height - gy);
                                img = ImageOps.Crop(img, gx, gy, gw, gh);
                                (bw, bh) = (cw, ch);
                            }
                            break;
                        case 'r':
                            img = t.A switch { 1 => ImageOps.Rotate270(img), 2 => ImageOps.Rotate180(img), 3 => ImageOps.Rotate90(img), _ => img };
                            if ((t.A & 1) != 0) (bw, bh) = (bh, bw);
                            break;
                        case 'm':
                            img = t.A == 0 ? ImageOps.FlipVertical(img) : ImageOps.FlipHorizontal(img);
                            break;
                    }
                }
                // without ApplyOrientation: back to the stored order, as the image Decode returns
                if (!ApplyOrientation) img = ImageOps.ApplyOrientation(img, AvifReader.InverseOrientation(rd.Orientation(chain)));
            }
            // the alternate's colour space: an ICC profile, else nclx primaries (a profile is only needed for them)
            byte[]? alt = AlternateIcc;
            if (alt == null && AlternatePrimaries is not (0 or 2))
                alt = AvifReader.DescribingIcc(AlternatePrimaries, AlternateTransferCharacteristics, false) ?? (AlternatePrimaries == 1 ? IccProfile.Srgb.ToBytes() : null);
            int bits = rd.ItemBitDepth(gi);
            return new GainMap
            {
                Metadata = m, Image = img, AlternateIcc = alt, Source = GainMapSource.Iso21496,
                ImageMax = bits is > 8 and < 16 ? (1 << bits) - 1 : 0,
            };
        }
    }

    /// <summary>
    /// AVIF (AV1 Image File Format) decoder: HEIF items (AV1 images, grids, overlays, identity and tone map derivations),
    /// alpha (also premultiplied), colour (nclx, ICC), transforms (clap, irot, imir), Exif / XMP items, layered images and
    /// image sequences. The AV1 decoder implements the whole AV1 specification (intra and inter tools, all filters, film
    /// grain). Samples are UInt8 for 8 bit streams and UInt16 holding the stored range (0..1023, 0..4095) for 10 and 12 bit.
    /// </summary>
    public static class Avif
    {
        public static bool IsMatch(ReadOnlySpan<byte> header) => HeifFile.IsMatch(header);

        static bool IsCorruption(Exception e) => e is IndexOutOfRangeException or ArgumentException or OverflowException
            or InvalidOperationException or InvalidCastException or DivideByZeroException or NullReferenceException
            || e is AggregateException a && a.InnerExceptions.Count > 0 && a.InnerExceptions[0] is not (ImageFormatException or ImageNotSupportedException);

        static Exception Corrupt(Exception e)
        {
            if (e is AggregateException a && a.InnerExceptions.Count == 1 && a.InnerExceptions[0] is ImageFormatException or ImageNotSupportedException)
                return a.InnerExceptions[0];
            return new ImageFormatException("AVIF: corrupt data (" + e.GetType().Name + ")", e);
        }

        static T Guard<T>(Func<T> f)
        {
            try { return f(); }
            catch (Exception e) when (IsCorruption(e)) { throw Corrupt(e); }
        }

        /// <summary>Size, channels, bit depth, colour and orientation from the container, without decoding.</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data, AvifDecodeOptions? options = null)
        {
            var copy = data.ToArray();
            return Guard(() => GetInfoCore(copy, options ?? AvifDecodeOptions.Default));
        }

        static ImageInfo GetInfoCore(byte[] data, AvifDecodeOptions opt)
        {
            var rd = new AvifReader(data, opt);
            var primary = rd.Primary;
            var track = rd.ColorTrack;
            if (primary != null && !(track != null && rd.File.MajorBrand == "avis" && primary.Type is not ("av01" or "grid" or "iden" or "tmap" or "iovl")))
            {
                var it = primary;
                // a tmap primary: the base's output image with the tmap item's transforms
                var chain = new[] { it };
                if (it.Type == "tmap" && it.Refs("dimg").Length > 0 && rd.File.Items.TryGetValue(it.Refs("dimg")[0], out var b)) chain = new[] { it = b, primary };
                var size = rd.Ispe(it) ?? throw new ImageFormatException("AVIF: primary item without ispe");
                int w = size.w, h = size.h;
                foreach (var c in chain)
                    foreach (var t in rd.Transforms(c))
                    {
                        if (t.Kind == 'c' && rd.ApplyCrop && AvifReader.Clap(t.Clap!, w, h) is var (_, _, cw, ch)) { w = cw; h = ch; }
                        else if (t.Kind == 'r' && (t.A & 1) != 0) (w, h) = (h, w);
                    }
                int orient = rd.Orientation(chain);
                if (!opt.ApplyOrientation && orient >= 5) (w, h) = (h, w);
                int bits = rd.ItemBitDepth(it);
                bool mono = Monochrome(rd, it);
                bool alpha = !opt.IgnoreAlpha && rd.HasAlpha(it);
                var color = mono ? (alpha ? ColorModel.GrayAlpha : ColorModel.Gray) : (alpha ? ColorModel.Rgba : ColorModel.Rgb);
                var colr = ColourOf(rd, it);
                int frames = track != null ? track.Samples.Count : 1;
                return new ImageInfo(ImageFormat.Avif, w, h, RawImage.ChannelCount(color), bits <= 8 ? SampleType.UInt8 : SampleType.UInt16, color, Math.Max(1, frames))
                {
                    BitsPerSample = bits,
                    Orientation = orient,
                    OrientationApplied = opt.ApplyOrientation && orient > 1,
                    HasIccProfile = colr.Icc != null || AvifReader.DescribingIcc(colr.Primaries, colr.Transfer, mono) != null,
                    Transfer = TransferOfColour(colr),
                    IsAnimated = track != null && track.Samples.Count > 1,
                };
            }
            if (track != null)
            {
                var cfg = track.Av1C is IsoBox ab ? Av1Config.Parse(data, ab.Payload, ab.End) : null;
                int bits = cfg?.BitDepth ?? 8;
                bool mono = cfg?.Monochrome ?? false;
                bool alpha = !opt.IgnoreAlpha && rd.AlphaTrack(track) != null;
                var color = mono ? (alpha ? ColorModel.GrayAlpha : ColorModel.Gray) : (alpha ? ColorModel.Rgba : ColorModel.Rgb);
                // the tkhd size (frames are scaled to it, see DecodeTrack), else the sample entry's; cropped by its clap
                int w = track.Width, h = track.Height;
                if (w == 0 || h == 0) (w, h) = SampleEntrySize(data, track);
                if (track.Clap != null && rd.ApplyCrop && AvifReader.Clap(track.Clap, w, h) is var (_, _, cw, ch)) (w, h) = (cw, ch);
                var colr = new HeifColour();
                if (track.Colr is IsoBox cb) AvifReader.ReadColr(data, cb.Payload, cb.End, colr);
                if (track.ColrIcc is IsoBox ib) AvifReader.ReadColr(data, ib.Payload, ib.End, colr);
                return new ImageInfo(ImageFormat.Avif, w, h, RawImage.ChannelCount(color), bits <= 8 ? SampleType.UInt8 : SampleType.UInt16, color, Math.Max(1, track.Samples.Count))
                {
                    BitsPerSample = bits,
                    HasIccProfile = colr.Icc != null || colr.HasNclx && AvifReader.DescribingIcc(colr.Primaries, colr.Transfer, mono) != null,
                    Transfer = TransferOfColour(colr),
                    IsAnimated = track.Samples.Count > 1,
                };
            }
            throw new ImageFormatException("AVIF: neither a primary image item nor an image sequence");
        }

        static (int, int) SampleEntrySize(byte[] data, HeifTrack t)
        {
            // width / height of the VisualSampleEntry sit 24 bytes into its payload; the av1C box follows the entry header
            if (t.Av1C is IsoBox a)
            {
                long entryPayload = a.Start - 78;
                if (entryPayload >= 0 && entryPayload + 28 <= data.Length)
                    return ((data[entryPayload + 24] << 8) | data[entryPayload + 25], (data[entryPayload + 26] << 8) | data[entryPayload + 27]);
            }
            return (0, 0);
        }

        static bool Monochrome(AvifReader rd, HeifItem it, int depth = 0)
        {
            if (rd.Av1C(it) is Av1Config c) return c.Monochrome;
            if (depth < 8)
                foreach (var r in it.Refs("dimg"))
                    if (rd.File.Items.TryGetValue(r, out var s)) return Monochrome(rd, s, depth + 1);
            return rd.Pixi(it) is int[] px && px.Length == 1;
        }

        static HeifColour ColourOf(AvifReader rd, HeifItem it)
        {
            var c = rd.Colour(it);
            if (!c.HasNclx)
            {
                // the AV1 sequence header carries the colour description when there is no nclx
                var src = it;
                for (int i = 0; i < 8 && src.Type != "av01"; i++)
                {
                    var r = src.Refs("dimg");
                    if (r.Length == 0 || !rd.File.Items.TryGetValue(r[0], out var n)) break;
                    src = n;
                }
                if (src.Type == "av01" && SequenceColour(rd, src) is HeifColour sc)
                {
                    c.HasNclx = true; c.Primaries = sc.Primaries; c.Transfer = sc.Transfer; c.Matrix = sc.Matrix; c.FullRange = sc.FullRange;
                }
            }
            return c;
        }

        static HeifColour? SequenceColour(AvifReader rd, HeifItem it)
        {
            try
            {
                var d = rd.File.ItemData(rd.Data, it);
                int pos = 0;
                while (pos < d.Length)
                {
                    var r = new Av1BitReader(d, pos, d.Length - pos);
                    r.Bit();
                    int type = r.I(4);
                    bool ext = r.Flag(), hasSize = r.Flag();
                    r.Bit();
                    if (ext) r.I(8);
                    long size = hasSize ? (long)r.Leb128() : d.Length - pos - 1 - (ext ? 1 : 0);
                    int payload = pos + r.BytePosition;
                    if (payload + size > d.Length) return null;
                    if (type == Av1C.ObuSequenceHeader)
                    {
                        var s = Av1SequenceHeader.Parse(new Av1BitReader(d, payload, (int)size));
                        return new HeifColour { HasNclx = true, Primaries = s.ColorPrimaries, Transfer = s.TransferCharacteristics, Matrix = s.MatrixCoefficients, FullRange = s.ColorRange };
                    }
                    pos = payload + (int)size;
                }
            }
            catch (Exception e) when (e is ImageFormatException or ImageNotSupportedException) { }
            return null;
        }

        static TransferFunction TransferOfColour(HeifColour c)
        {
            var t = AvifReader.TransferOf(c.Transfer, out _);
            if (t == TransferFunction.Unknown && (!c.HasNclx || c.Transfer is 0 or 2)) return TransferFunction.Srgb;
            return t;
        }

        /// <summary>Decodes the primary image (for a sequence without one: its first frame).</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, AvifDecodeOptions? options = null)
        {
            var copy = data.ToArray();
            return Guard(() => DecodeCore(copy, options ?? AvifDecodeOptions.Default));
        }

        public static RawImage Decode(Stream stream, AvifDecodeOptions? options = null) => ImageCodec.WithData(stream, d => Decode(d, options));

        static RawImage DecodeCore(byte[] data, AvifDecodeOptions opt)
        {
            var rd = new AvifReader(data, opt);
            var primary = rd.Primary;
            RawImage img;
            if (primary != null)
            {
                img = rd.DecodeItem(primary, opt.ApplyOrientation);
                AddMetadata(rd, primary, img);
                return img;
            }
            var track = rd.ColorTrack ?? throw new ImageFormatException("AVIF: neither a primary image item nor an image sequence");
            var frames = DecodeTrack(rd, track, opt, 1);
            return frames[0].Image;
        }

        static void AddMetadata(AvifReader rd, HeifItem primary, RawImage img)
        {
            var (exif, xmp) = rd.MetadataItems(primary);
            var m = img.Metadata ??= new ImageMetadata();
            if (exif != null) m.Exif = exif;
            if (xmp != null) m.Xmp = xmp;
        }

        /// <summary>Decodes all frames of the image sequence; a still image gives one frame.</summary>
        public static AvifAnimation DecodeAnimation(ReadOnlySpan<byte> data, AvifDecodeOptions? options = null)
        {
            var copy = data.ToArray();
            return Guard(() => DecodeAnimationCore(copy, options ?? AvifDecodeOptions.Default));
        }

        static AvifAnimation DecodeAnimationCore(byte[] data, AvifDecodeOptions opt)
        {
            var rd = new AvifReader(data, opt);
            var track = rd.ColorTrack;
            if (track == null)
            {
                var img = DecodeCore(data, opt);
                return new AvifAnimation
                {
                    Width = img.Width, Height = img.Height, IsAnimated = false, LoopCount = 1,
                    Frames = new[] { new AvifFrame { Index = 0, Image = img, IsSync = true } },
                };
            }
            var frames = DecodeTrack(rd, track, opt, int.MaxValue);
            return new AvifAnimation
            {
                Width = frames.Count > 0 ? frames[0].Image.Width : track.Width,
                Height = frames.Count > 0 ? frames[0].Image.Height : track.Height,
                IsAnimated = track.Samples.Count > 1, LoopCount = track.LoopCount, Timescale = track.Timescale, Frames = frames,
            };
        }

        /// <summary>Every frame of a sequence, or the single image.</summary>
        public static RawImage[] DecodeAll(ReadOnlySpan<byte> data, AvifDecodeOptions? options = null)
        {
            var a = DecodeAnimation(data, options);
            var r = new RawImage[a.Frames.Count];
            for (int i = 0; i < r.Length; i++) r[i] = a.Frames[i].Image;
            return r;
        }

        static List<AvifFrame> DecodeTrack(AvifReader rd, HeifTrack track, AvifDecodeOptions opt, int maxFrames)
        {
            var colr = new HeifColour();
            if (track.Colr is IsoBox cb) AvifReader.ReadColr(rd.Data, cb.Payload, cb.End, colr);
            if (track.ColrIcc is IsoBox ib) AvifReader.ReadColr(rd.Data, ib.Payload, ib.End, colr);
            var dec = new Av1Decoder(new Av1DecoderOptions { ApplyFilmGrain = opt.ApplyFilmGrain, Multithreaded = opt.Multithreaded, MaxPixels = opt.MaxPixels });
            var alphaTrack = opt.IgnoreAlpha ? null : rd.AlphaTrack(track);
            Av1Decoder? adec = alphaTrack != null ? new Av1Decoder(new Av1DecoderOptions { Multithreaded = opt.Multithreaded, MaxPixels = opt.MaxPixels }) : null;
            bool prem = false;
            foreach (var r in track.References) if (r.type == "prem" && alphaTrack != null && Array.IndexOf(r.ids, alphaTrack.TrackId) >= 0) prem = true;
            // the av1C config OBUs (a sequence header) may precede the samples
            if (track.Av1C is IsoBox ab && Av1Config.Parse(rd.Data, ab.Payload, ab.End) is Av1Config cfg && cfg.ConfigObus.Length > 0)
                dec.Decode(cfg.ConfigObus, 0, cfg.ConfigObus.Length);
            if (adec != null && alphaTrack!.Av1C is IsoBox aab && Av1Config.Parse(rd.Data, aab.Payload, aab.End) is Av1Config acfg && acfg.ConfigObus.Length > 0)
                adec.Decode(acfg.ConfigObus, 0, acfg.ConfigObus.Length);
            dec.Output.Clear();
            var frames = new List<AvifFrame>();
            int n = Math.Min(maxFrames, track.Samples.Count);
            for (int i = 0; i < n; i++)
            {
                var s = track.Samples[i];
                if (s.Offset < 0 || s.Offset + s.Size > rd.Data.Length) throw new ImageFormatException("AVIF: sample outside the file");
                dec.Output.Clear();
                dec.Decode(rd.Data, (int)s.Offset, s.Size);
                if (dec.Output.Count == 0) continue;
                var y = AvifReader.FromPicture(dec.Output[^1]);
                if (colr.HasNclx)
                {
                    y.ColorPrimaries = colr.Primaries; y.TransferCharacteristics = colr.Transfer; y.MatrixCoefficients = colr.Matrix; y.FullRange = colr.FullRange;
                }
                YuvImage? a = null;
                if (adec != null && i < alphaTrack!.Samples.Count)
                {
                    var asmp = alphaTrack.Samples[i];
                    if (asmp.Offset >= 0 && asmp.Offset + asmp.Size <= rd.Data.Length)
                    {
                        adec.Output.Clear();
                        adec.Decode(rd.Data, (int)asmp.Offset, asmp.Size);
                        if (adec.Output.Count > 0)
                        {
                            a = AvifReader.FromPicture(adec.Output[^1]);
                            if (a.Width != y.Width || a.Height != y.Height) a = null;
                        }
                    }
                }
                var img = AvifColor.ToRaw(y, a, prem, opt.Multithreaded);
                // a tkhd size other than the coded one: scaled to it, as avifdec 1.4.2 does (ISO/IEC 14496-12 8.3.2
                // presentation size), before the clap (unverified order: avifdec does not scale a cropped track of the coded size)
                if (track.Width > 0 && track.Height > 0 && (track.Width, track.Height) != (img.Width, img.Height))
                    img = ImageOps.Resize(img, track.Width, track.Height, ResampleFilter.Bilinear);
                if (track.Clap != null && opt.ApplyCrop && AvifReader.Clap(track.Clap, img.Width, img.Height) is var (cx, cy, cw, ch)) img = ImageOps.Crop(img, cx, cy, cw, ch);
                img.Metadata = AvifReader.ColourMetadata(y, colr);
                img.Metadata.Orientation = 1;
                double ms = track.Timescale > 0 ? s.Duration * 1000.0 / track.Timescale : 0;
                frames.Add(new AvifFrame { Index = frames.Count, DurationTicks = s.Duration, DurationMs = ms, IsSync = s.Sync, Image = img });
            }
            if (frames.Count == 0) throw new ImageFormatException("AVIF: the sequence has no decodable frame");
            return frames;
        }

        /// <summary>The HDR gain map (tmap item) of the file, null if there is none.</summary>
        public static AvifGainMap? GetGainMap(ReadOnlySpan<byte> data, AvifDecodeOptions? options = null)
        {
            var copy = data.ToArray();
            return Guard(() => GetGainMapCore(copy, options ?? AvifDecodeOptions.Default));
        }

        static AvifGainMap? GetGainMapCore(byte[] data, AvifDecodeOptions opt)
        {
            var rd = new AvifReader(data, opt);
            if (!rd.File.HasMeta) return null;
            foreach (var id in rd.File.ItemOrder)
            {
                var it = rd.File.Items[id];
                if (it.Type != "tmap") continue;
                var refs = it.Refs("dimg");
                if (refs.Length < 2 || !rd.File.Items.ContainsKey(refs[0]) || !rd.File.Items.ContainsKey(refs[1])) continue;
                // a gain map of the image Decode returns: the tmap item itself or its base image is the primary item
                if (rd.Primary is HeifItem p && p.Id != it.Id && p.Id != refs[0]) continue;
                if (AvifGainMap.Of(rd, it, rd.Primary?.Id == it.Id, opt.ApplyOrientation) is AvifGainMap g) return g;
            }
            return null;
        }

        /// <summary>
        /// The alternate (usually HDR) rendition of a file with a gain map (tmap item) as linear Float32 RGB, 1.0 = HDR
        /// reference white, for a display with HDR headroom <paramref name="headroom"/> (log2; null = fully applied):
        /// <see cref="Decode(ReadOnlySpan{byte}, AvifDecodeOptions)"/> plus <see cref="GainMap.Apply(RawImage, GainMap, double?)"/>
        /// with <see cref="AvifGainMap.ToGainMap"/>, as for JPEG and JPEG XL. Without a usable gain map the image converted
        /// to linear light is returned. A derived primary item (iden, grid, iovl) over tmap items is derived from their
        /// alternate renditions at that headroom (HEIF Amd 1 6.6.2.4.1), where <see cref="Decode(ReadOnlySpan{byte}, AvifDecodeOptions)"/>
        /// takes their base images.
        /// </summary>
        public static RawImage DecodeHdr(ReadOnlySpan<byte> data, double? headroom = null, AvifDecodeOptions? options = null)
        {
            var copy = data.ToArray();
            var opt = options ?? AvifDecodeOptions.Default;
            if (Guard(() => DerivedHdr(copy, headroom, opt)) is RawImage derived) return derived;
            var baseline = Decode(data, options);
            var gm = GetGainMap(data, options);
            var g = gm == null ? null : Guard(() => gm.ToGainMap());
            return g == null ? GainMap.LinearRgb(baseline) : GainMap.Apply(baseline, g, headroom);
        }

        /// <summary>The HDR rendition of a derived primary item with tmap inputs; null for every other file.</summary>
        static RawImage? DerivedHdr(byte[] data, double? headroom, AvifDecodeOptions opt)
        {
            var rd = new AvifReader(data, opt);
            if (rd.Primary is not HeifItem p || p.Type == "tmap" || rd.TmapInput(p) == null) return null;
            var img = rd.DecodeItemHdr(p, headroom);
            // without ApplyOrientation: the primary item's irot / imir undone, reported in the metadata (as Decode)
            if (!opt.ApplyOrientation && rd.Orientation(p) is int o && o > 1)
            {
                var meta = img.Metadata;
                img = ImageOps.ApplyOrientation(img, AvifReader.InverseOrientation(o));
                img.Metadata = meta;
                if (meta != null) meta.Orientation = o;
            }
            return img;
        }

        /// <summary>The raw Exif (from the TIFF header on) and XMP items describing the primary image.</summary>
        public static (byte[]? Exif, byte[]? Xmp) GetMetadataItems(ReadOnlySpan<byte> data)
        {
            var copy = data.ToArray();
            return Guard(() =>
            {
                var rd = new AvifReader(copy, AvifDecodeOptions.Default);
                return rd.MetadataItems(rd.Primary);
            });
        }

        /// <summary>The first thumbnail item ('thmb' reference to the primary image), decoded; null if there is none.</summary>
        public static RawImage? DecodeThumbnail(ReadOnlySpan<byte> data, AvifDecodeOptions? options = null)
        {
            var copy = data.ToArray();
            return Guard(() =>
            {
                var opt = options ?? AvifDecodeOptions.Default;
                var rd = new AvifReader(copy, opt);
                if (rd.Primary is not HeifItem p) return null;
                foreach (var it in rd.File.Referencing(p.Id, "thmb")) return rd.DecodeItem(it, opt.ApplyOrientation);
                return null;
            });
        }

        /// <summary>
        /// Decodes the auxiliary images attached to the primary item other than alpha (depth maps and others), with their
        /// aux_type URN; each comes back as a one channel image of its stored size.
        /// </summary>
        public static IReadOnlyList<(string AuxType, RawImage Image)> DecodeAuxiliaryImages(ReadOnlySpan<byte> data, AvifDecodeOptions? options = null)
        {
            var copy = data.ToArray();
            return Guard(() =>
            {
                var opt = options ?? AvifDecodeOptions.Default;
                var rd = new AvifReader(copy, opt);
                var r = new List<(string, RawImage)>();
                var p = rd.Primary;
                if (p == null) return (IReadOnlyList<(string, RawImage)>)r;
                foreach (var it in rd.File.Referencing(p.Id, "auxl"))
                {
                    string t = rd.AuxType(it);
                    if (AvifReader.IsAlpha(t)) continue;
                    var y = rd.DecodeYuv(it);
                    y.Mono = true;
                    y.FullRange = true;
                    r.Add((t, AvifColor.ToRaw(y, null, false, opt.Multithreaded)));
                }
                return (IReadOnlyList<(string, RawImage)>)r;
            });
        }
    }
}
