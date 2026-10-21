using System;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.IO;
using Aardvark.ImageCodecs.JpegXl;

namespace Aardvark.ImageCodecs
{
    public sealed class JxlDecodeOptions
    {
        /// <summary>
        /// Rotate / mirror the pixels according to the orientation in the codestream header (default), so that the image
        /// comes back upright with metadata orientation 1. False returns the pixels as stored, with the orientation in the
        /// metadata.
        /// </summary>
        public bool ApplyOrientation { get; set; } = true;

        /// <summary>
        /// Upper bound for the image and for each frame, in pixels; larger files are rejected with an
        /// <see cref="ImageNotSupportedException"/>. A JPEG XL file of a few bytes can describe a huge image.
        /// </summary>
        public long MaxPixels { get; set; } = 1L << 28;

        /// <summary>Decode groups, LF groups and colour conversion on several threads (same result). Default true.</summary>
        public bool Multithreaded { get; set; } = true;

        /// <summary>
        /// Draw spot colour extra channels onto the colour channels (as libjxl does by default). Default true.
        /// </summary>
        public bool RenderSpotColours { get; set; } = true;

        /// <summary>Test hook: return Float32 samples whatever the header says.</summary>
        internal bool ForceFloat { get; set; }

        /// <summary>Test hook: Float32 output of the colour channels followed by all extra channels, without spot rendering
        /// or unpremultiplication (as the conformance references have them).</summary>
        internal bool AllChannels { get; set; }

        internal static readonly JxlDecodeOptions Default = new();
    }

    /// <summary>One displayed frame of a JPEG XL animation (or the single composited still image).</summary>
    public sealed class JxlFrame
    {
        public int Index { get; internal set; }
        /// <summary>Display time in animation ticks (see <see cref="JxlAnimation.TicksPerSecond"/>); 0 for still images.</summary>
        public uint DurationTicks { get; internal set; }
        /// <summary>Display time in milliseconds (rounded), 0 for still images.</summary>
        public int DurationMs { get; internal set; }
        /// <summary>SMPTE timecode of the frame when the animation has timecodes.</summary>
        public uint Timecode { get; internal set; }
        public string Name { get; internal set; } = "";
        /// <summary>The whole canvas after this frame, as <see cref="Jxl.Decode(ReadOnlySpan{byte}, JxlDecodeOptions)"/> returns images.</summary>
        public RawImage Image { get; internal set; } = null!;
    }

    public sealed class JxlAnimation
    {
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public bool IsAnimated { get; internal set; }
        /// <summary>Number of repetitions, 0 = forever.</summary>
        public int LoopCount { get; internal set; }
        public double TicksPerSecond { get; internal set; }
        public IReadOnlyList<JxlFrame> Frames { get; internal set; } = Array.Empty<JxlFrame>();
    }

    /// <summary>What a JPEG XL extra channel holds (ISO/IEC 18181-1 ExtraChannelType).</summary>
    public enum JxlExtraChannelKind { Alpha, Depth, SpotColour, SelectionMask, Black, Cfa, Thermal, NonOptional, Optional }

    /// <summary>One extra channel of a JPEG XL image as decoded (<see cref="Jxl.DecodeExtraChannels"/>).</summary>
    public sealed class JxlExtraChannelImage
    {
        public JxlExtraChannelKind Kind { get; internal set; }
        public string Name { get; internal set; } = "";
        /// <summary>Bits per sample as stored.</summary>
        public int BitsPerSample { get; internal set; }
        /// <summary>Whether the samples are floating point in the file.</summary>
        public bool IsFloat { get; internal set; }
        /// <summary>For alpha: the colour is premultiplied by it in the file (<see cref="Jxl.Decode(ReadOnlySpan{byte}, JxlDecodeOptions)"/> returns straight alpha).</summary>
        public bool AlphaPremultiplied { get; internal set; }
        /// <summary>For spot colours: linear RGB and solidity of the ink; null otherwise.</summary>
        public float[]? SpotColour { get; internal set; }
        /// <summary>Upsampling of the channel relative to the image (1, 2, 4 or 8); the image here has the full size.</summary>
        public int Upsampling { get; internal set; } = 1;
        /// <summary>
        /// The samples as a one channel Float32 image of the image's size (orientation applied as for Decode): integer
        /// channels scaled to 0..1, float channels as stored.
        /// </summary>
        public RawImage Image { get; internal set; } = null!;
    }

    /// <summary>
    /// JPEG XL (ISO/IEC 18181-1 codestream, 18181-2 container) decoder: VarDCT and modular coding, all entropy coding
    /// modes, filters, image features, extra channels, frames and animation. Output is 8 / 16 bit (UInt8 / UInt16), 17 to 31
    /// bit (UInt32) integer or float (Float16 / Float32) as the header's bit depth says, in the colour space the header
    /// describes (XYB is converted to it); alpha is returned unpremultiplied.
    /// </summary>
    public static partial class Jxl
    {
        public static bool IsMatch(ReadOnlySpan<byte> header) => JxlContainer.IsMatch(header);

        /// <summary>Reads size, channels, sample type, orientation and colour description from the headers.</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data, JxlDecodeOptions? options = null)
        {
            try { return GetInfoCore(data, options); }
            catch (Exception e) when (IsCorruption(e)) { throw Corrupt(e); }
        }

        /// <summary>Malformed data must surface as <see cref="ImageFormatException"/>, whatever check caught it.</summary>
        static bool IsCorruption(Exception e) => e is IndexOutOfRangeException or ArgumentException or OverflowException
            or InvalidOperationException or InvalidCastException or DivideByZeroException or NullReferenceException
            || e is AggregateException a && a.InnerExceptions.Count > 0 && a.InnerExceptions[0] is not (ImageFormatException or ImageNotSupportedException);

        static Exception Corrupt(Exception e)
        {
            if (e is AggregateException a && a.InnerExceptions.Count == 1 && a.InnerExceptions[0] is ImageFormatException or ImageNotSupportedException)
                return a.InnerExceptions[0];
            return new ImageFormatException("JPEG XL: corrupt data (" + e.GetType().Name + ")", e);
        }

        static ImageInfo GetInfoCore(ReadOnlySpan<byte> data, JxlDecodeOptions? options)
        {
            var opt = options ?? JxlDecodeOptions.Default;
            var c = JxlContainer.Parse(data);
            var cs = c.CodestreamPrefix(data, int.MaxValue);
            var br = new JxlBitReader(cs);
            var h = JxlImageHeader.Read(br);
            int frames = 1;
            bool animated = h.Animation != null;
            if (animated)
            {
                if (h.Colour.WantIcc) JxlIcc.Read(br);
                br.ZeroPadToByte();
                frames = CountFrames(cs, br.BytePosition, h);
            }
            var (color, ch, type) = OutputFormat(h);
            int ow = h.Width, oh = h.Height;
            bool applied = opt.ApplyOrientation && h.Orientation > 1;
            if (applied && h.Orientation >= 5) (ow, oh) = (oh, ow);
            var tf = TransferOf(h, out _);
            var info = new ImageInfo(ImageFormat.Jxl, ow, oh, ch, type, color, frames)
            {
                BitsPerSample = h.BitDepth.Bits,
                Orientation = h.Orientation,
                OrientationApplied = applied,
                // CMYK is returned as sRGB without the profile; enumerated encodings other than (linear) sRGB get one built
                HasIccProfile = h.Colour.WantIcc ? !(BlackChannel(h) >= 0 && !h.Colour.IsGray && !h.XybEncoded) : JxlColour.DescribingIcc(h.Colour) != null,
                IsAnimated = animated,
                Transfer = tf,
                HasGainMap = HasGainMapBox(c, data),
            };
            return info;
        }

        internal static bool HasGainMapBox(JxlContainer c, ReadOnlySpan<byte> d)
        {
            if (!c.IsContainer) return false;
            foreach (var box in c.Boxes)
                if (box.Type == "jhgm" || box.Type == "brob" && box.Length >= 4 && box.Offset + 4 <= d.Length && d.Slice(box.Offset, 4).SequenceEqual("jhgm"u8)) return true;
            return false;
        }

        static int CountFrames(byte[] cs, int pos, JxlImageHeader h)
        {
            if (h.HavePreview)
            {
                var ph = new JxlImageHeader { Width = h.PreviewWidth, Height = h.PreviewHeight, ExtraChannels = h.ExtraChannels, XybEncoded = h.XybEncoded, Colour = h.Colour };
                pos = SkipFrame(cs, pos, ph, out _);
            }
            int n = 0;
            while (true)
            {
                pos = SkipFrame(cs, pos, h, out var fh);
                if ((fh.Type == JxlFrameType.Regular || fh.Type == JxlFrameType.SkipProgressive) && (fh.IsLast || fh.Duration != 0)) n++;
                if (fh.IsLast) return Math.Max(1, n);
                if (n > 1 << 16) throw new ImageNotSupportedException("JPEG XL: too many frames");
            }
        }

        static int SkipFrame(byte[] cs, int pos, JxlImageHeader h, out JxlFrameHeader fh)
        {
            if (pos >= cs.Length) throw new ImageFormatException("JPEG XL: truncated codestream");
            var br = new JxlBitReader(cs, pos, cs.Length - pos);
            fh = JxlFrameHeader.Read(br, h);
            var toc = JxlToc.Read(br, JxlFrameDims.Of(fh));
            long next = (long)pos + br.BytePosition + toc.TotalSize;
            if (next > cs.Length) throw new ImageFormatException("JPEG XL: truncated codestream");
            return (int)next;
        }

        static (ColorModel color, int channels, SampleType type) OutputFormat(JxlImageHeader h)
        {
            bool gray = h.Colour.IsGray;
            bool alpha = h.AlphaChannel >= 0;
            var color = gray ? (alpha ? ColorModel.GrayAlpha : ColorModel.Gray) : (alpha ? ColorModel.Rgba : ColorModel.Rgb);
            var bd = h.BitDepth;
            SampleType t = bd.Float ? (bd.Bits <= 16 ? SampleType.Float16 : SampleType.Float32)
                : bd.Bits <= 8 ? SampleType.UInt8 : bd.Bits <= 16 ? SampleType.UInt16 : SampleType.UInt32;
            return (color, RawImage.ChannelCount(color), t);
        }

        static TransferFunction TransferOf(JxlImageHeader h, out double gamma) => TransferOf(h.Colour, out gamma);

        internal static TransferFunction TransferOf(JxlColourEncoding ce, out double gamma)
        {
            gamma = 0;
            if (ce.WantIcc) return TransferFunction.Unknown;
            switch (ce.Transfer)
            {
                case JxlTransfer.Srgb: return TransferFunction.Srgb;
                case JxlTransfer.Linear: return TransferFunction.Linear;
                case JxlTransfer.Pq: return TransferFunction.Pq;
                case JxlTransfer.Hlg: return TransferFunction.Hlg;
                case JxlTransfer.Dci: gamma = 1 / 2.6; return TransferFunction.Gamma;
                case JxlTransfer.Gamma: gamma = ce.Gamma; return TransferFunction.Gamma;
                default: return TransferFunction.Unknown;
            }
        }

        /// <summary>Decodes the image (for an animation: the first frame).</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, JxlDecodeOptions? options = null)
        {
            try { return DecodeCore(data, options); }
            catch (Exception e) when (IsCorruption(e)) { throw Corrupt(e); }
        }

        static RawImage DecodeCore(ReadOnlySpan<byte> data, JxlDecodeOptions? options)
        {
            var opt = options ?? JxlDecodeOptions.Default;
            var c = JxlContainer.Parse(data);
            var dec = new JxlDecoder(c.Codestream(data), opt);
            var hd = dec.Header;
            XybOutputSink? sink = null;
            if (!opt.AllChannels && hd.XybEncoded && hd.ExtraChannels.Length == 0 && !(hd.Colour.WantIcc && hd.Icc != null))
            {
                // a still XYB image: the restoration filters of its last frame write straight into the output
                var (color0, nch0, type0) = OutputFormat(hd);
                if (opt.ForceFloat) type0 = SampleType.Float32;
                dec.SinkFactory = () => sink = new XybOutputSink(new XybOutputWriter(hd, hd.Width, hd.Height, nch0, type0, hd.Opsin, hd.ToneMapping.IntensityTarget), hd.Width);
            }
            var f = dec.Next() ?? throw new ImageFormatException("JPEG XL: no displayed frame");
            var meta = Metadata(c, data, dec.Header);
            var outImg = f.Canvas.Sink is XybOutputSink s && s == sink ? s.Writer.Finish(hd, meta, opt, OutputFormat(hd).color) : Output(dec.Header, f.Canvas, meta, opt);
            JxlProfile.Mark("output");
            return outImg;
        }

        public static RawImage Decode(Stream stream, JxlDecodeOptions? options = null) => Decode(ImageCodec.ReadAll(stream), options);

        /// <summary>Decodes all displayed frames (each the whole canvas).</summary>
        public static JxlAnimation DecodeAnimation(ReadOnlySpan<byte> data, JxlDecodeOptions? options = null)
        {
            try { return DecodeAnimationCore(data, options); }
            catch (Exception e) when (IsCorruption(e)) { throw Corrupt(e); }
        }

        static JxlAnimation DecodeAnimationCore(ReadOnlySpan<byte> data, JxlDecodeOptions? options)
        {
            var opt = options ?? JxlDecodeOptions.Default;
            var c = JxlContainer.Parse(data);
            var dec = new JxlDecoder(c.Codestream(data), opt);
            var h = dec.Header;
            var frames = new List<JxlFrame>();
            double tps = h.Animation != null ? (double)h.Animation.TpsNumerator / h.Animation.TpsDenominator : 0;
            while (dec.Next() is { } f)
            {
                var meta = Metadata(c, data, h);
                frames.Add(new JxlFrame
                {
                    Index = frames.Count, DurationTicks = f.Duration, Timecode = f.Timecode, Name = f.Name,
                    DurationMs = tps > 0 ? (int)Math.Round(f.Duration * 1000.0 / tps) : 0,
                    Image = Output(h, f.Canvas, meta, opt),
                });
            }
            var first = frames.Count > 0 ? frames[0].Image : null;
            return new JxlAnimation
            {
                Width = first?.Width ?? h.Width, Height = first?.Height ?? h.Height, IsAnimated = h.Animation != null,
                LoopCount = (int)(h.Animation?.NumLoops ?? 1), TicksPerSecond = tps, Frames = frames,
            };
        }

        /// <summary>Every displayed frame (one for a still image).</summary>
        public static RawImage[] DecodeAll(ReadOnlySpan<byte> data, JxlDecodeOptions? options = null)
        {
            var a = DecodeAnimation(data, options);
            var r = new RawImage[a.Frames.Count];
            for (int i = 0; i < r.Length; i++) r[i] = a.Frames[i].Image;
            return r;
        }

        /// <summary>
        /// Every extra channel of the image (for an animation: of the first frame), alpha included, as decoded: depth,
        /// thermal, selection masks, spot colours, the black channel of CMYK and so on, which <see cref="Decode(ReadOnlySpan{byte}, JxlDecodeOptions)"/> does not
        /// return (it returns colour and straight alpha).
        /// </summary>
        public static IReadOnlyList<JxlExtraChannelImage> DecodeExtraChannels(ReadOnlySpan<byte> data, JxlDecodeOptions? options = null)
        {
            try { return DecodeExtraChannelsCore(data, options); }
            catch (Exception e) when (IsCorruption(e)) { throw Corrupt(e); }
        }

        static IReadOnlyList<JxlExtraChannelImage> DecodeExtraChannelsCore(ReadOnlySpan<byte> data, JxlDecodeOptions? options)
        {
            var opt = options ?? JxlDecodeOptions.Default;
            var c = JxlContainer.Parse(data);
            var dec = new JxlDecoder(c.Codestream(data), opt);
            var h = dec.Header;
            var f = dec.Next() ?? throw new ImageFormatException("JPEG XL: no displayed frame");
            var canvas = f.Canvas;
            var list = new List<JxlExtraChannelImage>();
            for (int i = 0; i < h.ExtraChannels.Length; i++)
            {
                var ec = h.ExtraChannels[i];
                var plane = (float[])canvas.Planes[canvas.NumColour + i].Clone();
                var img = new RawImage(canvas.Width, canvas.Height, ColorModel.Gray, plane, 1, new ImageMetadata { Orientation = h.Orientation });
                if (opt.ApplyOrientation && h.Orientation > 1) img = ImageOps.ApplyOrientation(img, h.Orientation);
                list.Add(new JxlExtraChannelImage
                {
                    Kind = ec.Type switch
                    {
                        JxlExtraChannelType.Alpha => JxlExtraChannelKind.Alpha, JxlExtraChannelType.Depth => JxlExtraChannelKind.Depth,
                        JxlExtraChannelType.SpotColour => JxlExtraChannelKind.SpotColour, JxlExtraChannelType.SelectionMask => JxlExtraChannelKind.SelectionMask,
                        JxlExtraChannelType.Black => JxlExtraChannelKind.Black, JxlExtraChannelType.Cfa => JxlExtraChannelKind.Cfa,
                        JxlExtraChannelType.Thermal => JxlExtraChannelKind.Thermal, JxlExtraChannelType.NonOptional => JxlExtraChannelKind.NonOptional,
                        _ => JxlExtraChannelKind.Optional,
                    },
                    Name = ec.Name, BitsPerSample = ec.BitDepth.Bits, IsFloat = ec.BitDepth.Float, AlphaPremultiplied = ec.AlphaAssociated,
                    SpotColour = ec.Type == JxlExtraChannelType.SpotColour ? new[] { ec.SpotRed, ec.SpotGreen, ec.SpotBlue, ec.SpotSolidity } : null,
                    Upsampling = 1 << ec.DimShift, Image = img,
                });
            }
            return list;
        }

        /// <summary>
        /// The HDR gain map of the file (ISO/IEC 18181-2:2026 jhgm box, Brotli compressed or not), or null when there is
        /// none (or its version is unknown).
        /// </summary>
        public static JxlGainMap? GetGainMap(ReadOnlySpan<byte> data)
        {
            var c = JxlContainer.Parse(data);
            if (!c.IsContainer) return null;
            var boxes = c.Metadata(data, "jhgm");
            try { return boxes.Count > 0 ? JxlGainMap.Parse(boxes[0]) : null; }
            catch (Exception e) when (IsCorruption(e)) { throw Corrupt(e); }
        }

        /// <summary>
        /// The alternate (usually HDR) rendition of a file with a jhgm gain map as linear Float32 RGB, 1.0 = HDR reference
        /// white, for a display with HDR headroom <paramref name="headroom"/> (log2; null = fully applied): <see cref="Decode(ReadOnlySpan{byte}, JxlDecodeOptions?)"/>
        /// plus <see cref="GainMap.Apply(RawImage, GainMap, double?)"/>, the code path JPEG gain maps use too. Without a
        /// usable gain map the image converted to linear light is returned.
        /// </summary>
        public static RawImage DecodeHdr(ReadOnlySpan<byte> data, double? headroom = null, JxlDecodeOptions? options = null)
        {
            var baseline = Decode(data, options);
            var gm = GetGainMap(data)?.ToGainMap(options);
            return gm == null ? GainMap.LinearRgb(baseline) : GainMap.Apply(baseline, gm, headroom);
        }

        /// <summary>EXIF (TIFF structure), XMP and JUMBF of the container, Brotli compressed boxes included.</summary>
        public static (byte[]? Exif, byte[]? Xmp, byte[]? Jumbf) GetMetadataBoxes(ReadOnlySpan<byte> data)
        {
            var c = JxlContainer.Parse(data);
            byte[]? exif = null, xmp = null, jumb = null;
            foreach (var e in c.Metadata(data, "Exif")) { exif = JxlContainer.ExifTiff(e); if (exif != null) break; }
            foreach (var x in c.Metadata(data, "xml ")) { xmp = x; break; }
            foreach (var j in c.Metadata(data, "jumb")) { jumb = j; break; }
            return (exif, xmp, jumb);
        }

        static ImageMetadata Metadata(JxlContainer c, ReadOnlySpan<byte> data, JxlImageHeader h)
        {
            var m = new ImageMetadata { Orientation = h.Orientation, IccProfile = h.Icc };
            if (c.IsContainer)
            {
                var (exif, xmp, _) = GetMetadataBoxes(data);
                m.Exif = exif; m.Xmp = xmp;
            }
            m.Transfer = TransferOf(h, out m.Gamma);
            // an enumerated encoding other than (linear) sRGB: described by a profile built from it
            m.IccProfile ??= JxlColour.DescribingIcc(h.Colour);
            return m;
        }

        // ------------------------------------------------------------------ output conversion

        /// <summary>Test hook output: Float32, colour channels then every extra channel as decoded (spot colours not rendered).</summary>
        static RawImage OutputAll(JxlImageHeader h, JxlFrameImage canvas, ImageMetadata meta, JxlDecodeOptions opt)
        {
            canvas.EnsureLinear(opt.Multithreaded);
            var col = ColourPlanes(h, canvas, meta, opt);
            int ne = h.ExtraChannels.Length, nch = col.Length + ne, n = canvas.Width * canvas.Height;
            var data = new float[(long)n * nch];
            for (int c = 0; c < nch; c++)
            {
                var src = c < col.Length ? col[c] : canvas.Planes[canvas.NumColour + c - col.Length];
                for (int i = 0; i < n; i++) data[(long)i * nch + c] = src[i];
            }
            var img = new RawImage(canvas.Width, canvas.Height, ColorModel.Other, data, nch, meta);
            if (opt.ApplyOrientation && h.Orientation > 1) img = ImageOps.ApplyOrientation(img, h.Orientation);
            return img;
        }

        static RawImage Output(JxlImageHeader h, JxlFrameImage canvas, ImageMetadata meta, JxlDecodeOptions opt)
        {
            if (opt.AllChannels) return OutputAll(h, canvas, meta, opt);
            var (color, nch, type) = OutputFormat(h);
            if (opt.ForceFloat) type = SampleType.Float32;
            int w = canvas.Width, hh = canvas.Height, n = w * hh;
            int ncol = h.Colour.IsGray ? 1 : 3;
            int alpha = h.AlphaChannel;
            var planes = new float[nch][];
            int[][]? ints = canvas.IntPlanes;
            if (ints != null && (opt.ForceFloat || opt.RenderSpotColours && HasSpot(h) || alpha >= 0 && h.ExtraChannels[alpha].AlphaAssociated || BlackChannel(h) >= 0)) ints = null;
            if (ints == null && h.XybEncoded && !(h.Colour.WantIcc && h.Icc != null) && !(opt.RenderSpotColours && HasSpot(h)))
                return FusedXybOutput(h, canvas, meta, opt, color, nch, type);
            canvas.EnsureLinear(opt.Multithreaded);
            if (ints == null)
            {
                var p = canvas.Planes;
                var col = CmykToSrgb(h, canvas, meta, opt) ?? ColourPlanes(h, canvas, meta, opt);
                if (opt.RenderSpotColours && HasSpot(h)) col = RenderSpot(h, canvas, col, n);
                for (int c = 0; c < ncol; c++) planes[c] = col[c];
                if (alpha >= 0)
                {
                    var a = p[canvas.NumColour + alpha];
                    planes[ncol] = a;
                    if (h.ExtraChannels[alpha].AlphaAssociated)
                    {
                        for (int c = 0; c < ncol; c++)
                        {
                            var src = planes[c];
                            var o = new float[n];
                            for (int i = 0; i < n; i++) o[i] = a[i] > 0 ? src[i] / a[i] : 0;
                            planes[c] = o;
                        }
                    }
                }
            }
            Array data = RawImage.CreateArray(type, checked(n * nch));
            int maxv = h.BitDepth.Float ? 0 : (int)Math.Min((1L << h.BitDepth.Bits) - 1, int.MaxValue);
            bool rgb8 = ints != null && type == SampleType.UInt8 && nch == 3 && ncol == 3 && h.BitDepth.Bits == 8;
            JxlColour.Chunked(n, opt.Multithreaded, (s0, e0) =>
            {
                if (rgb8)
                {
                    // 8 bit RGB as coded: clamp and interleave in one pass
                    var d = ((byte[])data).AsSpan(3 * s0, 3 * (e0 - s0));
                    var r = ints![0].AsSpan(s0, e0 - s0); var g = ints[1].AsSpan(s0, e0 - s0); var b = ints[2].AsSpan(s0, e0 - s0);
                    for (int i = 0; i < r.Length; i++)
                    {
                        d[3 * i] = (byte)Math.Clamp(r[i], 0, 255);
                        d[3 * i + 1] = (byte)Math.Clamp(g[i], 0, 255);
                        d[3 * i + 2] = (byte)Math.Clamp(b[i], 0, 255);
                    }
                    return;
                }
                for (int c = 0; c < nch; c++)
                {
                    bool isAlpha = c == ncol;
                    if (ints != null)
                    {
                        var src = isAlpha ? ints[canvas.NumColour + alpha] : ints[c];
                        int srcBits = isAlpha ? h.ExtraChannels[alpha].BitDepth.Bits : h.BitDepth.Bits;
                        WriteInts(data, type, c, nch, src, srcBits, h.BitDepth.Bits, s0, e0);
                    }
                    else WriteFloats(data, type, c, nch, planes[c], maxv, s0, e0);
                }
            });
            var img = new RawImage(w, hh, color, data, nch, meta);
            if (opt.ApplyOrientation && h.Orientation > 1) img = ImageOps.ApplyOrientation(img, h.Orientation);
            return img;
        }

        /// <summary>
        /// XYB images to an enumerated encoding: primaries, transfer function, unpremultiplication and quantization in one
        /// pass over small blocks (no full size intermediate planes).
        /// </summary>
        static RawImage FusedXybOutput(JxlImageHeader h, JxlFrameImage canvas, ImageMetadata meta, JxlDecodeOptions opt, ColorModel color, int nch, SampleType type)
        {
            int w = canvas.Width, hh = canvas.Height, n = w * hh;
            int alpha = h.AlphaChannel;
            var p = canvas.Planes;
            float[]? a = alpha >= 0 ? p[canvas.NumColour + alpha] : null;
            var wr = new XybOutputWriter(h, w, hh, nch, type, canvas.PendingXyb, canvas.PendingIntensityTarget);
            const int Block = 4096;
            JxlColour.Chunked(n, opt.Multithreaded, (s0, e0) =>
            {
                var buf = new float[3 * Block];
                for (int s = s0; s < e0; s += Block)
                {
                    int cnt = Math.Min(Block, e0 - s);
                    wr.Convert(p[0].AsSpan(s, cnt), p[1].AsSpan(s, cnt), p[2].AsSpan(s, cnt), a != null ? a.AsSpan(s, cnt) : default, s, buf);
                }
            });
            return wr.Finish(h, meta, opt, color);
        }

        /// <summary>The conversion of <see cref="FusedXybOutput"/> for runs of pixels, into one output array.</summary>
        sealed class XybOutputWriter
        {
            public readonly Array Data;
            readonly int width, height, nch, ncol, maxv;
            readonly SampleType type;
            readonly float[]? matrix;
            readonly JxlColourEncoding ce;
            readonly float it;
            readonly bool srgb8, premul;
            readonly JxlOpsinParams? xyb;
            readonly float xybIntensity;
            readonly float[]? hlgWeights;

            public XybOutputWriter(JxlImageHeader h, int w, int hh, int nch, SampleType type, JxlOpsinParams? xyb, float xybIntensity)
            {
                width = w; height = hh; this.nch = nch; this.type = type; this.xyb = xyb; this.xybIntensity = xybIntensity;
                ncol = h.Colour.IsGray ? 1 : 3;
                int alpha = h.AlphaChannel;
                premul = alpha >= 0 && h.ExtraChannels[alpha].AlphaAssociated;
                matrix = !h.Colour.WantIcc && JxlColour.NeedsPrimaryConversion(h.Colour) ? JxlColour.SrgbToTargetMatrix(h.Colour) : null;
                ce = h.Colour.WantIcc ? new JxlColourEncoding() : h.Colour;
                it = h.ToneMapping.IntensityTarget;
                if (ce.Transfer == JxlTransfer.Hlg && !ce.IsGray) hlgWeights = JxlColour.LuminanceWeights(ce);
                Data = RawImage.CreateArray(type, checked(w * hh * nch));
                maxv = h.BitDepth.Float ? 0 : (int)Math.Min((1L << h.BitDepth.Bits) - 1, int.MaxValue);
                srgb8 = type == SampleType.UInt8 && ce.Transfer == JxlTransfer.Srgb && !premul;
            }

            /// <summary>Pixels s .. s + x.Length - 1 (row major) from their colour (XYB when pending) and alpha; scratch: 3 * count floats.</summary>
            public void Convert(ReadOnlySpan<float> x, ReadOnlySpan<float> y, ReadOnlySpan<float> bp, ReadOnlySpan<float> a, int s, Span<float> scratch)
            {
                int cnt = x.Length;
                var r = scratch.Slice(0, cnt); var g = scratch.Slice(cnt, cnt); var b = scratch.Slice(2 * cnt, cnt);
                if (xyb != null) JxlColour.XybToLinear(x, y, bp, r, g, b, xyb, xybIntensity);
                else { x.CopyTo(r); y.CopyTo(g); bp.CopyTo(b); }
                if (matrix != null) JxlColour.ApplyMatrix(r, g, b, matrix);
                if (ncol == 1)
                    for (int i = 0; i < cnt; i++) r[i] = 0.2126f * r[i] + 0.7152f * g[i] + 0.0722f * b[i];
                var data = Data;
                if (srgb8)
                {
                    var d = ((byte[])data).AsSpan(s * nch);
                    if (nch == 3) JxlColour.LinearToSrgb8x3(r, g, b, d);
                    else for (int c = 0; c < ncol; c++) JxlColour.LinearToSrgb8(c == 0 ? r : c == 1 ? g : b, d.Slice(c), nch);
                    if (!a.IsEmpty) WriteFloats(data, type, ncol, nch, a, maxv, s);
                    return;
                }
                if (ce.Transfer == JxlTransfer.Hlg) JxlColour.HlgInverseOotf(r, g, b, ncol == 1 ? null : hlgWeights, JxlColour.HlgGamma(it));
                if (ncol == 1) JxlColour.FromLinear(r, ce, it);
                else
                {
                    JxlColour.FromLinear(r, ce, it); JxlColour.FromLinear(g, ce, it); JxlColour.FromLinear(b, ce, it);
                }
                for (int c = 0; c < ncol; c++)
                {
                    var col = c == 0 ? r : c == 1 ? g : b;
                    if (premul)
                        for (int i = 0; i < cnt; i++) { float av = a[i]; col[i] = av > 0 ? col[i] / av : 0; }
                    WriteFloats(data, type, c, nch, col, maxv, s);
                }
                if (!a.IsEmpty) WriteFloats(data, type, ncol, nch, a, maxv, s);
            }

            public RawImage Finish(JxlImageHeader h, ImageMetadata meta, JxlDecodeOptions opt, ColorModel color)
            {
                if (h.Colour.WantIcc) meta.IccProfile = null;   // converted to sRGB
                var img = new RawImage(width, height, color, Data, nch, meta);
                if (opt.ApplyOrientation && h.Orientation > 1) img = ImageOps.ApplyOrientation(img, h.Orientation);
                return img;
            }
        }

        /// <summary>Restored XYB tiles of the last frame converted straight into the output (no full size colour planes).</summary>
        sealed unsafe class XybOutputSink : JxlTileSink
        {
            public readonly XybOutputWriter Writer;
            readonly int width;

            public XybOutputSink(XybOutputWriter writer, int width) { Writer = writer; this.width = width; }

            public override void WriteRows(int x0, int y0, int w, int h, float* p0, float* p1, float* p2, int stride)
            {
                Span<float> scratch = stackalloc float[3 * w];
                for (int r = 0; r < h; r++)
                    Writer.Convert(new ReadOnlySpan<float>(p0 + r * stride, w), new ReadOnlySpan<float>(p1 + r * stride, w),
                        new ReadOnlySpan<float>(p2 + r * stride, w), default, (y0 + r) * width + x0, scratch);
            }
        }

        static int BlackChannel(JxlImageHeader h)
        {
            for (int i = 0; i < h.ExtraChannels.Length; i++) if (h.ExtraChannels[i].Type == JxlExtraChannelType.Black) return i;
            return -1;
        }

        /// <summary>
        /// CMYK (three colour channels plus a black extra channel under a CMYK ICC profile, all stored as 1 - ink): converted
        /// to sRGB through the profile, since RawImage has no CMYK colour model. Null when the image is not CMYK.
        /// </summary>
        static float[][]? CmykToSrgb(JxlImageHeader h, JxlFrameImage canvas, ImageMetadata meta, JxlDecodeOptions opt)
        {
            int k = BlackChannel(h);
            if (k < 0 || h.Icc == null || h.Colour.IsGray || h.XybEncoded) return null;
            IccTransform t;
            try
            {
                var src = IccProfile.Parse(h.Icc);
                if (src.Channels != 4) return null;
                t = new IccTransform(src, IccProfile.Srgb, IccRenderingIntent.RelativeColorimetric);
            }
            catch (Exception e) when (e is ImageFormatException or ImageNotSupportedException) { return null; }
            int n = canvas.Width * canvas.Height;
            var p = canvas.Planes;
            var kp = p[canvas.NumColour + k];
            var col = new[] { new float[n], new float[n], new float[n] };
            JxlColour.Chunked(n, opt.Multithreaded, (s, e) =>
            {
                int cnt = e - s;
                var inp = new float[cnt * 4];
                var outp = new float[cnt * 3];
                for (int i = 0; i < cnt; i++)
                {
                    inp[4 * i] = 1 - p[0][s + i]; inp[4 * i + 1] = 1 - p[1][s + i]; inp[4 * i + 2] = 1 - p[2][s + i]; inp[4 * i + 3] = 1 - kp[s + i];
                }
                t.Transform(inp, outp);
                for (int i = 0; i < cnt; i++) { col[0][s + i] = outp[3 * i]; col[1][s + i] = outp[3 * i + 1]; col[2][s + i] = outp[3 * i + 2]; }
            });
            meta.IccProfile = null;
            meta.Transfer = TransferFunction.Srgb;
            return col;
        }

        /// <summary>The colour planes in the output colour space (XYB converted to the header's encoding or ICC profile).</summary>
        static float[][] ColourPlanes(JxlImageHeader h, JxlFrameImage canvas, ImageMetadata meta, JxlDecodeOptions opt)
        {
            int n = canvas.Width * canvas.Height;
            int ncol = h.Colour.IsGray ? 1 : 3;
            var p = canvas.Planes;
            float[][] col = new float[ncol][];

            if (h.XybEncoded && h.Colour.WantIcc && h.Icc != null && IccOutput(h, p, n, ncol, opt) is { } viaIcc)
            {
                col = viaIcc;
            }
            else if (h.XybEncoded)
            {
                var r = (float[])p[0].Clone(); var g = (float[])p[1].Clone(); var b = (float[])p[2].Clone();
                if (!h.Colour.WantIcc && JxlColour.NeedsPrimaryConversion(h.Colour))
                    JxlColour.ApplyMatrix(r, g, b, n, JxlColour.SrgbToTargetMatrix(h.Colour));
                var ce = h.Colour.WantIcc ? new JxlColourEncoding() : h.Colour;
                if (ncol == 1)
                {
                    // grey: the luminance of the (achromatic) linear RGB
                    var gr = new float[n];
                    for (int i = 0; i < n; i++) gr[i] = 0.2126f * r[i] + 0.7152f * g[i] + 0.0722f * b[i];
                    if (ce.Transfer == JxlTransfer.Hlg) JxlColour.HlgInverseOotf(gr, gr, gr, null, JxlColour.HlgGamma(h.ToneMapping.IntensityTarget));
                    JxlColour.FromLinear(gr, n, ce, h.ToneMapping.IntensityTarget, opt.Multithreaded);
                    col[0] = gr;
                }
                else
                {
                    if (ce.Transfer == JxlTransfer.Hlg)
                        JxlColour.HlgInverseOotf(r, g, b, JxlColour.LuminanceWeights(ce), JxlColour.HlgGamma(h.ToneMapping.IntensityTarget));
                    JxlColour.FromLinear(r, n, ce, h.ToneMapping.IntensityTarget, opt.Multithreaded);
                    JxlColour.FromLinear(g, n, ce, h.ToneMapping.IntensityTarget, opt.Multithreaded);
                    JxlColour.FromLinear(b, n, ce, h.ToneMapping.IntensityTarget, opt.Multithreaded);
                    col[0] = r; col[1] = g; col[2] = b;
                }
                if (h.Colour.WantIcc) meta.IccProfile = null;   // converted to sRGB
            }
            else
            {
                for (int c = 0; c < ncol; c++) col[c] = p[Math.Min(c, canvas.NumColour - 1)];
            }
            return col;
        }

        /// <summary>XYB images with an ICC profile: linear sRGB to the profile's device space (relative colorimetric).</summary>
        static float[][]? IccOutput(JxlImageHeader h, float[][] p, int n, int ncol, JxlDecodeOptions opt)
        {
            IccTransform t;
            try
            {
                var dst = IccProfile.Parse(h.Icc);
                if (dst.Channels != ncol) return null;
                t = new IccTransform(ncol == 1 ? IccProfile.GrayLinear : IccProfile.LinearSrgb, dst, IccRenderingIntent.RelativeColorimetric);
            }
            catch (Exception e) when (e is ImageFormatException or ImageNotSupportedException) { return null; }
            var col = new float[ncol][];
            for (int c = 0; c < ncol; c++) col[c] = new float[n];
            JxlColour.Chunked(n, opt.Multithreaded, (s, e) =>
            {
                int cnt = e - s;
                var inp = new float[cnt * ncol];
                var outp = new float[cnt * ncol];
                if (ncol == 1)
                    for (int i = 0; i < cnt; i++) inp[i] = 0.2126f * p[0][s + i] + 0.7152f * p[1][s + i] + 0.0722f * p[2][s + i];
                else
                    for (int i = 0; i < cnt; i++) { inp[3 * i] = p[0][s + i]; inp[3 * i + 1] = p[1][s + i]; inp[3 * i + 2] = p[2][s + i]; }
                t.Transform(inp, outp);
                for (int i = 0; i < cnt; i++)
                    for (int c = 0; c < ncol; c++) col[c][s + i] = outp[i * ncol + c];
            });
            return col;
        }

        static bool HasSpot(JxlImageHeader h)
        {
            foreach (var e in h.ExtraChannels) if (e.Type == JxlExtraChannelType.SpotColour) return true;
            return false;
        }

        static float[][] RenderSpot(JxlImageHeader h, JxlFrameImage canvas, float[][] col, int n)
        {
            var r = new float[col.Length][];
            for (int c = 0; c < col.Length; c++) r[c] = (float[])col[c].Clone();
            for (int e = 0; e < h.ExtraChannels.Length; e++)
            {
                var ec = h.ExtraChannels[e];
                if (ec.Type != JxlExtraChannelType.SpotColour) continue;
                var s = canvas.Planes[canvas.NumColour + e];
                float[] sc = { ec.SpotRed, ec.SpotGreen, ec.SpotBlue };
                for (int c = 0; c < col.Length; c++)
                {
                    var o = r[c];
                    float v = col.Length == 1 ? (sc[0] + sc[1] + sc[2]) / 3 : sc[c];
                    for (int i = 0; i < n; i++)
                    {
                        float mix = ec.SpotSolidity * s[i];
                        o[i] = mix * v + (1 - mix) * o[i];
                    }
                }
            }
            return r;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void WriteInts(Array data, SampleType type, int c, int nch, int[] src, int srcBits, int dstBits, int start, int n)
        {
            long maxSrc = (1L << srcBits) - 1, maxDst = (1L << dstBits) - 1;
            if (srcBits != dstBits)
            {
                for (int i = start; i < n; i++)
                {
                    long v = Math.Clamp(src[i], 0, maxSrc);
                    v = (v * maxDst + maxSrc / 2) / maxSrc;
                    int o = i * nch + c;
                    switch (type)
                    {
                        case SampleType.UInt8: ((byte[])data)[o] = (byte)v; break;
                        case SampleType.UInt16: ((ushort[])data)[o] = (ushort)v; break;
                        default: ((uint[])data)[o] = (uint)v; break;
                    }
                }
                return;
            }
            // same depth: clamp and store, one loop per sample type
            var s = src.AsSpan(start, n - start);
            switch (type)
            {
                case SampleType.UInt8:
                    {
                        var d = ((byte[])data).AsSpan(start * nch + c);
                        int mx = (int)maxSrc;
                        for (int i = 0; i < s.Length; i++) d[i * nch] = (byte)Math.Clamp(s[i], 0, mx);
                        break;
                    }
                case SampleType.UInt16:
                    {
                        var d = ((ushort[])data).AsSpan(start * nch + c);
                        int mx = (int)maxSrc;
                        for (int i = 0; i < s.Length; i++) d[i * nch] = (ushort)Math.Clamp(s[i], 0, mx);
                        break;
                    }
                default:
                    {
                        var d = ((uint[])data).AsSpan(start * nch + c);
                        for (int i = 0; i < s.Length; i++) d[i * nch] = (uint)Math.Clamp(s[i], 0, maxSrc);
                        break;
                    }
            }
        }

        static void WriteFloats(Array data, SampleType type, int c, int nch, float[] src, int maxv, int start, int n)
            => WriteFloats(data, type, c, nch, src.AsSpan(start, n - start), maxv, start);

        /// <summary>Writes src as channel c of pixels start, start + 1, ... (rounded and clamped for integer types).</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void WriteFloats(Array data, SampleType type, int c, int nch, ReadOnlySpan<float> src, int maxv, int start)
        {
            int o = start * nch + c;
            switch (type)
            {
                case SampleType.Float32:
                    {
                        var d = ((float[])data).AsSpan(o);
                        for (int i = 0; i < src.Length; i++) d[i * nch] = src[i];
                        break;
                    }
                case SampleType.Float16:
                    {
                        var d = ((Half[])data).AsSpan(o);
                        for (int i = 0; i < src.Length; i++) d[i * nch] = (Half)src[i];
                        break;
                    }
                case SampleType.UInt8:
                    {
                        var d = ((byte[])data).AsSpan(o);
                        float mf = maxv;
                        for (int i = 0; i < src.Length; i++)
                        {
                            float v = src[i] * mf;
                            d[i * nch] = (byte)(v <= 0 ? 0 : v >= mf ? maxv : (int)(v + 0.5f));
                        }
                        break;
                    }
                case SampleType.UInt16:
                    {
                        var d = ((ushort[])data).AsSpan(o);
                        float mf = maxv;
                        for (int i = 0; i < src.Length; i++)
                        {
                            float v = src[i] * mf;
                            d[i * nch] = (ushort)(v <= 0 ? 0 : v >= mf ? maxv : (int)(v + 0.5f));
                        }
                        break;
                    }
                default:
                    {
                        var d = ((uint[])data).AsSpan(o);
                        for (int i = 0; i < src.Length; i++)
                        {
                            double v = (double)src[i] * maxv;
                            d[i * nch] = (uint)(v <= 0 ? 0 : v >= maxv ? maxv : (long)(v + 0.5));
                        }
                        break;
                    }
            }
        }
    }
}
