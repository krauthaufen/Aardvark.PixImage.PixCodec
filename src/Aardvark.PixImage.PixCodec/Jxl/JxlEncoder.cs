using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>JPEG XL encoder: image header, frames (lossless modular or lossy VarDCT), container and metadata boxes.</summary>
    internal static partial class JxlEncoder
    {
        // ------------------------------------------------------------------ entry points

        public static byte[] EncodeStill(RawImage image, JxlEncodeOptions opt)
        {
            var src = Prepare(image, opt, out bool lossless);
            var h = BuildHeader(src, opt, lossless, animation: null);
            var cs = new JxlBitWriter(1 << 16);
            WriteHeaderAndIcc(cs, h);
            EncodeFrame(cs, src, h, opt, lossless, isLast: true, durationTicks: 0, name: "");
            return Wrap(cs.ToArray(), src.Metadata, h, opt);
        }

        public static void EncodeAnimation(IEnumerable<JxlInputFrame> frames, Stream output, JxlEncodeOptions opt, int loopCount)
        {
            var list = new List<JxlInputFrame>(frames);
            if (list.Count == 0) throw new ArgumentException("JPEG XL: an animation needs at least one frame", nameof(frames));
            var first = Prepare(list[0].Image, opt, out bool lossless);
            // ticks of 1 ms
            var anim = new JxlAnimationHeader { TpsNumerator = 1000, TpsDenominator = 1, NumLoops = (uint)loopCount };
            var h = BuildHeader(first, opt, lossless, anim);
            var cs = new JxlBitWriter(1 << 16);
            WriteHeaderAndIcc(cs, h);
            for (int i = 0; i < list.Count; i++)
            {
                var f = list[i];
                if (f.DurationMs < 0) throw new ArgumentOutOfRangeException(nameof(frames), "JPEG XL: negative frame duration");
                var img = i == 0 ? first : Prepare(f.Image, opt, out _);
                if (img.Width != first.Width || img.Height != first.Height || img.Channels != first.Channels || img.SampleType != first.SampleType)
                    throw new ArgumentException("JPEG XL: all animation frames need the size, channels and sample type of the first", nameof(frames));
                EncodeFrame(cs, img, h, opt, lossless, isLast: i == list.Count - 1, durationTicks: (uint)f.DurationMs, name: f.Name ?? "");
            }
            var bytes = Wrap(cs.ToArray(), first.Metadata, h, opt);
            output.Write(bytes, 0, bytes.Length);
        }

        /// <summary>Validates the image; lossy input of other sample types is converted to Float32.</summary>
        static RawImage Prepare(RawImage image, JxlEncodeOptions opt, out bool lossless)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (image.Width <= 0 || image.Height <= 0 || image.Width > (1 << 30) || image.Height > (1 << 30))
                throw new ImageNotSupportedException("JPEG XL: bad image dimensions");
            int nc = image.Channels;
            var color = image.Color;
            if (!(color is ColorModel.Gray or ColorModel.GrayAlpha or ColorModel.Rgb or ColorModel.Rgba))
            {
                if (color == ColorModel.Other && nc is >= 1 and <= 4) color = RawImage.DefaultColorModel(nc);
                else throw new ImageNotSupportedException($"JPEG XL encoder: colour model {image.Color} with {nc} channels is not supported");
            }
            if (RawImage.ChannelCount(color) != nc) throw new ImageNotSupportedException("JPEG XL encoder: channel count does not match the colour model");
            lossless = opt.EffectiveDistance == 0;
            var t = image.SampleType;
            bool native = t is SampleType.UInt8 or SampleType.UInt16 or SampleType.Float16 or SampleType.Float32;
            if (!native)
            {
                if (lossless) throw new ImageNotSupportedException($"JPEG XL encoder: lossless coding of {t} samples is not supported (UInt8, UInt16, Float16, Float32)");
                image = ToFloat(image);
            }
            if (color != image.Color) image = new RawImage(image.Width, image.Height, color, image.Data, nc, image.Metadata);
            return image;
        }

        static RawImage ToFloat(RawImage img)
        {
            var d = new float[(long)img.Width * img.Height * img.Channels];
            switch (img.SampleType)
            {
                case SampleType.UInt32: { var s = (uint[])img.Data; for (long i = 0; i < d.Length; i++) d[i] = (float)(s[i] / 4294967295.0); break; }
                case SampleType.Int8: { var s = (sbyte[])img.Data; for (long i = 0; i < d.Length; i++) d[i] = Math.Max(0, s[i] / 127f); break; }
                case SampleType.Int16: { var s = (short[])img.Data; for (long i = 0; i < d.Length; i++) d[i] = Math.Max(0, s[i] / 32767f); break; }
                case SampleType.Int32: { var s = (int[])img.Data; for (long i = 0; i < d.Length; i++) d[i] = (float)Math.Max(0, s[i] / 2147483647.0); break; }
                case SampleType.Float64: { var s = (double[])img.Data; for (long i = 0; i < d.Length; i++) d[i] = (float)s[i]; break; }
                default: throw new ImageNotSupportedException($"JPEG XL encoder: sample type {img.SampleType} is not supported");
            }
            return new RawImage(img.Width, img.Height, img.Color, d, img.Channels, img.Metadata);
        }

        // ------------------------------------------------------------------ header

        static JxlBitDepth DepthOf(SampleType t) => t switch
        {
            SampleType.UInt8 => new JxlBitDepth { Bits = 8 },
            SampleType.UInt16 => new JxlBitDepth { Bits = 16 },
            SampleType.Float16 => new JxlBitDepth { Float = true, Bits = 16, ExpBits = 5 },
            _ => new JxlBitDepth { Float = true, Bits = 32, ExpBits = 8 },
        };

        static JxlImageHeader BuildHeader(RawImage img, JxlEncodeOptions opt, bool lossless, JxlAnimationHeader? animation)
        {
            bool gray = img.Color is ColorModel.Gray or ColorModel.GrayAlpha;
            bool alpha = img.Color is ColorModel.GrayAlpha or ColorModel.Rgba;
            var bd = DepthOf(img.SampleType);
            if (lossless && opt.BitsPerSample is > 0 and < 16 && img.SampleType == SampleType.UInt16) bd = new JxlBitDepth { Bits = opt.BitsPerSample };
            var meta = opt.KeepMetadata ? img.Metadata : null;
            var h = new JxlImageHeader
            {
                Width = img.Width, Height = img.Height, BitDepth = bd, XybEncoded = !lossless, Animation = animation,
                Orientation = meta != null && meta.Orientation is >= 1 and <= 8 ? meta.Orientation : 1,
                // int16 buffers suffice for modular samples of up to 12 bits (with room for transforms and residuals); lossy
                // frames code only the extra channels at their bit depth
                Modular16BitBuffers = lossless ? !bd.Float && bd.Bits <= 12 : !alpha || !bd.Float && bd.Bits <= 12,
            };
            if (alpha) h.ExtraChannels = new[] { new JxlExtraChannel { Type = JxlExtraChannelType.Alpha, BitDepth = bd } };
            h.Colour = ColourOf(img, meta, gray);
            if (h.Colour.WantIcc) h.Icc = meta!.IccProfile;
            if (h.Colour.WantIcc && !lossless)
            {
                // lossy frames are coded from linear sRGB: the profile has to be usable for the conversion
                try { _ = new IccTransform(IccProfile.Parse(h.Icc!), gray ? IccProfile.GrayLinear : IccProfile.LinearSrgb, IccRenderingIntent.RelativeColorimetric); }
                catch (Exception e) when (e is ImageFormatException or ImageNotSupportedException or ArgumentException)
                {
                    h.Icc = null;
                    h.Colour = ColourOf(img, null, gray);
                }
            }
            if (!lossless && opt.IntensityTarget > 0) h.ToneMapping.IntensityTarget = opt.IntensityTarget;
            // HDR defaults as libjxl has them: PQ reaches 10000 nits, HLG's nominal peak is 1000
            else if (!lossless && !h.Colour.WantIcc && h.Colour.Transfer == JxlTransfer.Pq) h.ToneMapping.IntensityTarget = 10000;
            else if (!lossless && !h.Colour.WantIcc && h.Colour.Transfer == JxlTransfer.Hlg) h.ToneMapping.IntensityTarget = 1000;
            return h;
        }

        static JxlColourEncoding ColourOf(RawImage img, ImageMetadata? meta, bool gray)
        {
            var c = new JxlColourEncoding { ColourSpace = gray ? JxlColourSpace.Grey : JxlColourSpace.Rgb };
            if (meta?.IccProfile is { Length: > 0 } icc && IccFits(icc, gray))
            {
                if (!HdrCicp(icc, gray, c)) c.WantIcc = true;
                return c;
            }
            var tf = meta?.Transfer ?? TransferFunction.Unknown;
            bool isFloat = img.SampleType is SampleType.Float16 or SampleType.Float32;
            switch (tf)
            {
                case TransferFunction.Linear: c.Transfer = JxlTransfer.Linear; break;
                case TransferFunction.Pq: c.Transfer = JxlTransfer.Pq; c.Primaries = JxlPrimaries.Bt2100; break;
                case TransferFunction.Hlg: c.Transfer = JxlTransfer.Hlg; c.Primaries = JxlPrimaries.Bt2100; break;
                case TransferFunction.Gamma when meta!.Gamma > 0 && meta.Gamma <= 1:
                    c.Transfer = JxlTransfer.Gamma; c.Gamma = meta.Gamma; break;
                case TransferFunction.Srgb: c.Transfer = JxlTransfer.Srgb; break;
                default: c.Transfer = isFloat ? JxlTransfer.Linear : JxlTransfer.Srgb; break;
            }
            return c;
        }

        /// <summary>
        /// A profile whose cicp tag says PQ or HLG with primaries the enumerated encoding has (what the decoder describes such
        /// encodings with): fills in <paramref name="c"/> with that encoding instead of storing the profile.
        /// </summary>
        static bool HdrCicp(byte[] icc, bool gray, JxlColourEncoding c)
        {
            if (IccProfile.TryParse(icc)?.Cicp is not { TransferCharacteristics: 16 or 18, MatrixCoefficients: 0 } cicp) return false;
            if (!gray)
                switch (cicp.ColourPrimaries)
                {
                    case 1: c.Primaries = JxlPrimaries.Srgb; break;
                    case 9: c.Primaries = JxlPrimaries.Bt2100; break;
                    case 12: c.Primaries = JxlPrimaries.P3; break;
                    case 11: c.Primaries = JxlPrimaries.P3; c.WhitePoint = JxlWhitePoint.Dci; break;
                    default: return false;
                }
            c.Transfer = cicp.TransferCharacteristics == 16 ? JxlTransfer.Pq : JxlTransfer.Hlg;
            return true;
        }

        /// <summary>Whether an ICC profile describes the image's colour space (RGB for colour, gray for gray images).</summary>
        static bool IccFits(byte[] icc, bool gray)
        {
            if (icc.Length < 128) return false;
            string cs = System.Text.Encoding.ASCII.GetString(icc, 16, 4);
            return gray ? cs == "GRAY" : cs == "RGB ";
        }

        static void WriteHeaderAndIcc(JxlBitWriter w, JxlImageHeader h)
        {
            JxlHeaderWriter.WriteImageHeader(w, h);
            if (h.Colour.WantIcc) JxlIccWriter.Write(w, h.Icc!);
            w.ZeroPadToByte();
        }

        // ------------------------------------------------------------------ frames

        static void EncodeFrame(JxlBitWriter cs, RawImage img, JxlImageHeader h, JxlEncodeOptions opt, bool lossless, bool isLast, uint durationTicks, string name)
        {
            int ne = h.ExtraChannels.Length;
            var fh = new JxlFrameHeader
            {
                Modular = lossless, Width = h.Width, Height = h.Height, IsLast = isLast, Duration = durationTicks, Name = name,
                EcUpsampling = new int[ne], EcBlending = new JxlBlendingInfo[ne],
            };
            for (int i = 0; i < ne; i++) fh.EcUpsampling[i] = 1;
            if (lossless)
            {
                fh.Filter = new JxlRestorationFilter { Gab = false, EpfIters = 0 };
                var chans = ToChannels(img, h);
                JxlLosslessEncoder.EncodeFrame(cs, chans, h, fh, JxlLosslessParams.For(opt.Effort), opt.Multithreaded);
            }
            else JxlVarDctEncoder.EncodeFrame(cs, img, h, fh, opt);
        }

        /// <summary>The samples as modular channels: integers as they are, floats as their bit patterns.</summary>
        static List<JxlChannel> ToChannels(RawImage img, JxlImageHeader h)
        {
            int w = img.Width, hh = img.Height, nc = img.Channels, n = w * hh;
            var list = new List<JxlChannel>(nc);
            for (int c = 0; c < nc; c++) list.Add(new JxlChannel(w, hh, 0, 0));
            void Copy(int c)
            {
                var d = list[c].Data;
                switch (img.SampleType)
                {
                    case SampleType.UInt8: { var s = (byte[])img.Data; for (int i = 0, j = c; i < n; i++, j += nc) d[i] = s[j]; break; }
                    case SampleType.UInt16: { var s = (ushort[])img.Data; for (int i = 0, j = c; i < n; i++, j += nc) d[i] = s[j]; break; }
                    case SampleType.Float16:
                        { var s = (Half[])img.Data; for (int i = 0, j = c; i < n; i++, j += nc) d[i] = BitConverter.HalfToUInt16Bits(s[j]); break; }
                    default:
                        { var s = (float[])img.Data; for (int i = 0, j = c; i < n; i++, j += nc) d[i] = BitConverter.SingleToInt32Bits(s[j]); break; }
                }
            }
            if (n >= 1 << 16) Parallel.For(0, nc, Copy); else for (int c = 0; c < nc; c++) Copy(c);
            return list;
        }

        // ------------------------------------------------------------------ container

        /// <summary>The bare codestream, or the container with EXIF / XMP boxes when there are any (or when forced).</summary>
        static byte[] Wrap(byte[] codestream, ImageMetadata? meta, JxlImageHeader h, JxlEncodeOptions opt)
        {
            byte[]? exif = opt.KeepMetadata ? meta?.Exif : null;
            byte[]? xmp = opt.KeepMetadata ? meta?.Xmp : null;
            if (exif is { Length: 0 }) exif = null;
            if (xmp is { Length: 0 }) xmp = null;
            bool level10 = NeedsLevel10(h);
            if (exif == null && xmp == null && !opt.ForceContainer && !level10) return codestream;
            // the codestream orientation is authoritative; the EXIF / XMP copies say the same
            if (exif != null) exif = ExifTags.WithOrientation(exif, h.Orientation);
            if (xmp != null) xmp = ExifTags.XmpWithOrientation(xmp, h.Orientation);
            var ms = new MemoryStream(codestream.Length + 1024);
            ms.Write(JxlContainer.Signature);
            WriteBox(ms, "ftyp", new byte[] { (byte)'j', (byte)'x', (byte)'l', (byte)' ', 0, 0, 0, 0, (byte)'j', (byte)'x', (byte)'l', (byte)' ' });
            if (level10) WriteBox(ms, "jxll", new byte[] { 10 });
            if (exif != null)
            {
                var b = new byte[4 + exif.Length];
                exif.CopyTo(b, 4);   // offset 0 to the TIFF header
                WriteBox(ms, "Exif", b);
            }
            if (xmp != null) WriteBox(ms, "xml ", xmp);
            WriteBox(ms, "jxlc", codestream);
            return ms.ToArray();
        }

        /// <summary>
        /// Whether the codestream exceeds the limits of level 5 (18181-1:2024 Annex M): more than 16 bits per sample,
        /// 32 bit modular buffers, more than 4 extra channels or a black one, more than 2^28 pixels or 2^18 per side, an ICC
        /// profile above 4 MB. It then needs a jxll box declaring level 10 (18181-2). (The MA trees the encoder learns stay
        /// within the level 5 limits on nodes and depth.)
        /// </summary>
        internal static bool NeedsLevel10(JxlImageHeader h)
        {
            if (h.BitDepth.Bits > 16 || !h.Modular16BitBuffers || h.ExtraChannels.Length > 4) return true;
            foreach (var e in h.ExtraChannels) if (e.BitDepth.Bits > 16 || e.Type == JxlExtraChannelType.Black) return true;
            if ((long)h.Width * h.Height > 1L << 28 || h.Width > 1 << 18 || h.Height > 1 << 18) return true;
            return h.Icc != null && h.Icc.Length > 1 << 22;
        }

        static void WriteBox(Stream s, string type, byte[] body)
        {
            long len = 8L + body.Length;
            Span<byte> head = stackalloc byte[16];
            if (len <= uint.MaxValue)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(head, (uint)len);
                for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
                s.Write(head.Slice(0, 8));
            }
            else
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(head, 1);
                for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(head.Slice(8), (ulong)(len + 8));
                s.Write(head);
            }
            s.Write(body, 0, body.Length);
        }
    }
}
