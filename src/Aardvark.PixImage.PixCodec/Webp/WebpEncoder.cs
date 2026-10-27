using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Aardvark.ImageCodecs
{
    /// <summary>Turns images into WebP files: picks the bitstream, builds the chunks and writes the RIFF container.</summary>
    internal static class WebpEncoder
    {
        const int FlagAnim = 0x02, FlagXmp = 0x04, FlagExif = 0x08, FlagAlpha = 0x10, FlagIcc = 0x20;

        /// <summary>The image as ARGB (0xAARRGGBB); gray is expanded.</summary>
        internal static uint[] ToArgb(RawImage image)
        {
            if (image.SampleType != SampleType.UInt8)
                throw new ImageNotSupportedException($"WebP stores 8 bit samples, not {image.SampleType}");
            var src = (byte[])image.Data;
            int n = image.Width * image.Height;
            var argb = new uint[n];
            switch (image.Color)
            {
                case ColorModel.Gray:
                    for (int i = 0; i < n; i++) argb[i] = 0xff000000 | src[i] * 0x010101u;
                    break;
                case ColorModel.GrayAlpha:
                    for (int i = 0; i < n; i++) argb[i] = (uint)src[2 * i + 1] << 24 | src[2 * i] * 0x010101u;
                    break;
                case ColorModel.Rgb:
                    for (int i = 0, o = 0; i < n; i++, o += 3) argb[i] = 0xff000000 | (uint)src[o] << 16 | (uint)src[o + 1] << 8 | src[o + 2];
                    break;
                case ColorModel.Rgba:
                    for (int i = 0, o = 0; i < n; i++, o += 4) argb[i] = (uint)src[o + 3] << 24 | (uint)src[o] << 16 | (uint)src[o + 1] << 8 | src[o + 2];
                    break;
                default:
                    throw new ImageNotSupportedException($"WebP cannot store {image.Channels} channels without a color model");
            }
            return argb;
        }

        static void CheckSize(int w, int h)
        {
            if (w > 16383 || h > 16383) throw new ImageNotSupportedException($"WebP images are limited to 16383 x 16383 pixels, got {w}x{h}");
        }

        /// <summary>The chunks of one image: an optional ALPH chunk followed by VP8 or VP8L.</summary>
        static List<(string tag, byte[] data)> EncodeImage(uint[] argb, int w, int h, bool hasAlpha, WebpEncodeOptions opt)
        {
            var chunks = new List<(string, byte[])>();
            if (opt.Lossless)
            {
                if (!opt.Exact && hasAlpha)
                    for (int i = 0; i < argb.Length; i++) if (argb[i] >> 24 == 0) argb[i] = 0;
                if (opt.NearLossless < 100) argb = NearLossless(argb, w, h, 5 - Math.Clamp(opt.NearLossless, 0, 99) / 20);
                chunks.Add(("VP8L", Vp8lEncoder.Encode(argb, w, h, hasAlpha, opt.Method, opt.CancellationToken)));
            }
            else
            {
                // The alpha plane is a small part of the file, and the lossless search is what costs the time: at the
                // middle efforts it gets the effort of 2, about 2.5 times faster at alpha payloads 0 to 2 % larger
                // (soft-edged masks, hard masks and detailed mattes on photographs and synthetic tiles).
                int alphaMethod = opt.Method is 3 or 4 ? 2 : opt.Method;
                if (hasAlpha) chunks.Add(("ALPH", EncodeAlpha(argb, w, h, alphaMethod, opt.AlphaQuality, opt.CancellationToken)));
                chunks.Add(("VP8 ", Vp8Encoder.Encode(argb, w, h, opt)));
            }
            return chunks;
        }

        /// <summary>
        /// Near-lossless preprocessing: color channels of pixels that differ from a neighbor are rounded to multiples
        /// of 2^bits (255 stays reachable), which makes the prediction residuals much cheaper. Pixels inside flat
        /// areas, alpha and fully transparent pixels are left alone; no channel moves by more than 2^(bits-1).
        /// </summary>
        internal static uint[] NearLossless(uint[] argb, int w, int h, int bits)
        {
            var res = (uint[])argb.Clone();
            int half = 1 << (bits - 1);
            uint Round(uint v) => (uint)Math.Min(255, (int)((v + half) >> bits << bits));
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    uint v = argb[i];
                    if (v >> 24 == 0) continue;
                    bool flat = (x == 0 || argb[i - 1] == v) && (x == w - 1 || argb[i + 1] == v) && (y == 0 || argb[i - w] == v) && (y == h - 1 || argb[i + w] == v);
                    if (flat) continue;
                    res[i] = (v & 0xff000000) | Round((v >> 16) & 0xff) << 16 | Round((v >> 8) & 0xff) << 8 | Round(v & 0xff);
                }
            return res;
        }

        /// <summary>The alpha plane as ALPH chunk payload; below quality 100 it is reduced to fewer levels first.</summary>
        static byte[] EncodeAlpha(uint[] argb, int w, int h, int method, int quality, System.Threading.CancellationToken cancel)
        {
            // the plane goes into the green channel of a lossless stream, which brings its own prediction
            var plane = new uint[argb.Length];
            quality = Math.Clamp(quality, 0, 100);
            int header = 0;
            if (quality < 100)
            {
                // 2 levels at quality 0, 256 at 100; fully transparent and fully opaque always survive
                int levels = 2 + quality * quality * 254 / 10000;
                var map = new byte[256];
                for (int v = 0; v < 256; v++) map[v] = (byte)((v * (levels - 1) + 127) / 255 * 255 / (levels - 1));
                for (int i = 0; i < plane.Length; i++) plane[i] = 0xff000000 | (uint)map[argb[i] >> 24] << 8;
                header = 1 << 4;        // pre-processing: level reduction
            }
            else
                for (int i = 0; i < plane.Length; i++) plane[i] = 0xff000000 | (argb[i] >> 24) << 8;
            var stream = Vp8lEncoder.EncodeHeaderless(plane, w, h, method, cancel);
            if (stream.Length < argb.Length)
            {
                var res = new byte[stream.Length + 1];
                res[0] = (byte)(header | 1);    // lossless compression, no filter
                stream.CopyTo(res, 1);
                return res;
            }
            var raw = new byte[argb.Length + 1];
            raw[0] = (byte)header;
            for (int i = 0; i < argb.Length; i++) raw[i + 1] = (byte)(plane[i] >> 8);
            return raw;
        }

        public static void EncodeStill(RawImage image, Stream output, WebpEncodeOptions opt)
        {
            CheckSize(image.Width, image.Height);
            opt.CancellationToken.ThrowIfCancellationRequested();
            var argb = ToArgb(image);
            bool alpha = image.HasAlpha;
            var chunks = EncodeImage(argb, image.Width, image.Height, alpha, opt);
            GetMetadata(image, opt, out var icc, out var exif, out var xmp);
            bool extended = chunks.Count > 1 || icc != null || exif != null || xmp != null;
            var file = new List<(string, byte[])>();
            if (extended)
            {
                int flags = (alpha ? FlagAlpha : 0) | (icc != null ? FlagIcc : 0) | (exif != null ? FlagExif : 0) | (xmp != null ? FlagXmp : 0);
                file.Add(("VP8X", Vp8x(flags, image.Width, image.Height)));
                if (icc != null) file.Add(("ICCP", icc));
            }
            file.AddRange(chunks);
            if (exif != null) file.Add(("EXIF", exif));
            if (xmp != null) file.Add(("XMP ", xmp));
            WriteRiff(output, file);
        }

        static void GetMetadata(RawImage image, WebpEncodeOptions opt, out byte[]? icc, out byte[]? exif, out byte[]? xmp)
        {
            icc = exif = xmp = null;
            if (!opt.WriteMetadata) return;
            icc = image.Metadata?.IccProfile;
            exif = ExifTags.ForWriting(image.Metadata);
            xmp = opt.Xmp ?? ExifTags.XmpForWriting(image.Metadata);
            if (icc != null && icc.Length == 0) icc = null;
            if (exif != null && exif.Length == 0) exif = null;
            if (xmp != null && xmp.Length == 0) xmp = null;
        }

        static byte[] Vp8x(int flags, int w, int h)
        {
            var b = new byte[10];
            b[0] = (byte)flags;
            b[4] = (byte)(w - 1); b[5] = (byte)((w - 1) >> 8); b[6] = (byte)((w - 1) >> 16);
            b[7] = (byte)(h - 1); b[8] = (byte)((h - 1) >> 8); b[9] = (byte)((h - 1) >> 16);
            return b;
        }

        static long ChunkSize(byte[] data) => 8L + data.Length + (data.Length & 1);

        static void WriteChunk(Stream s, string tag, byte[] data)
        {
            Span<byte> hdr = stackalloc byte[8];
            for (int i = 0; i < 4; i++) hdr[i] = (byte)tag[i];
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(4), (uint)data.Length);
            s.Write(hdr);
            s.Write(data);
            if ((data.Length & 1) != 0) s.WriteByte(0);
        }

        static void WriteRiff(Stream s, List<(string tag, byte[] data)> chunks)
        {
            long size = 4;
            foreach (var c in chunks) size += ChunkSize(c.data);
            if (size > uint.MaxValue - 9) throw new ImageNotSupportedException("WebP files are limited to 4 GB");
            Span<byte> hdr = stackalloc byte[12];
            "RIFF"u8.CopyTo(hdr);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(4), (uint)size);
            "WEBP"u8.CopyTo(hdr.Slice(8));
            s.Write(hdr);
            foreach (var c in chunks) WriteChunk(s, c.tag, c.data);
        }

        public static void EncodeAnimation(IEnumerable<WebpInputFrame> frames, Stream output, WebpEncodeOptions opt)
        {
            var anmf = new List<(string, byte[])>();
            uint[]? prev = null;
            int w = 0, h = 0;
            bool anyAlpha = false;
            byte[]? icc = null, exif = null, xmp = null;
            foreach (var frame in frames)
            {
                var image = frame.Image ?? throw new ArgumentException("animation frame without image");
                opt.CancellationToken.ThrowIfCancellationRequested();
                var argb = ToArgb(image);
                if (prev == null)
                {
                    w = image.Width; h = image.Height;
                    CheckSize(w, h);
                    GetMetadata(image, opt, out icc, out exif, out xmp);
                }
                else if (image.Width != w || image.Height != h)
                    throw new ArgumentException("all animation frames must have the size of the first one");
                if (opt.Lossless && !opt.Exact && image.HasAlpha)
                    for (int i = 0; i < argb.Length; i++) if (argb[i] >> 24 == 0) argb[i] = 0;

                // the rectangle to store: everything, or the bounding box of what changed (offsets must be even)
                int x0 = 0, y0 = 0, x1 = w, y1 = h;
                bool blend = false;
                uint[] sub = argb;
                if (prev != null && opt.FrameDifferencing)
                {
                    ChangedBox(argb, prev, w, h, out x0, out y0, out x1, out y1);
                    x0 &= ~1; y0 &= ~1;
                    int sw = x1 - x0, sh = y1 - y0;
                    sub = new uint[sw * sh];
                    bool changedTranslucent = false, anyUnchanged = false;
                    for (int y = 0; y < sh; y++)
                        for (int x = 0; x < sw; x++)
                        {
                            int i = (y0 + y) * w + x0 + x;
                            uint v = argb[i];
                            sub[y * sw + x] = v;
                            if (v == prev[i]) anyUnchanged = true;
                            else if (v >> 24 != 255) changedTranslucent = true;
                        }
                    // Lossless: unchanged pixels inside the box can become transparent and be blended away - as long as
                    // every pixel that did change is opaque, since blending would alter a translucent one.
                    if (opt.Lossless && anyUnchanged && !changedTranslucent)
                    {
                        blend = true;
                        for (int y = 0; y < sh; y++)
                            for (int x = 0; x < sw; x++)
                                if (sub[y * sw + x] == prev[(y0 + y) * w + x0 + x]) sub[y * sw + x] = 0;
                    }
                }
                int fw = x1 - x0, fh = y1 - y0;
                bool alpha = sub == argb && image.HasAlpha;
                if (!alpha)
                    for (int i = 0; i < sub.Length; i++) if (sub[i] >> 24 != 255) { alpha = true; break; }
                anyAlpha |= alpha;
                if (sub == argb && opt.Lossless) sub = (uint[])argb.Clone();    // the encoder works in place, argb is kept for the next frame
                var chunks = EncodeImage(sub, fw, fh, alpha, opt);

                long size = 16;
                foreach (var c in chunks) size += ChunkSize(c.data);
                var payload = new byte[size];
                Put24(payload, 0, x0 / 2); Put24(payload, 3, y0 / 2);
                Put24(payload, 6, fw - 1); Put24(payload, 9, fh - 1);
                Put24(payload, 12, Math.Clamp(frame.DurationMs, 0, 0xffffff));
                payload[15] = (byte)(blend ? 0 : 2);    // bit 1: do not blend, bit 0: dispose to background (never)
                using (var ms = new MemoryStream(payload, 16, payload.Length - 16))
                    foreach (var c in chunks) WriteChunk(ms, c.tag, c.data);
                anmf.Add(("ANMF", payload));
                prev = argb;
            }
            if (prev == null) throw new ArgumentException("an animation needs at least one frame");

            var file = new List<(string, byte[])>();
            int flags = FlagAnim | (anyAlpha ? FlagAlpha : 0) | (icc != null ? FlagIcc : 0) | (exif != null ? FlagExif : 0) | (xmp != null ? FlagXmp : 0);
            file.Add(("VP8X", Vp8x(flags, w, h)));
            if (icc != null) file.Add(("ICCP", icc));
            var anim = new byte[6];
            var bg = opt.BackgroundColor;
            anim[0] = bg.B; anim[1] = bg.G; anim[2] = bg.R; anim[3] = bg.A;
            BinaryPrimitives.WriteUInt16LittleEndian(anim.AsSpan(4), (ushort)Math.Clamp(opt.LoopCount, 0, 65535));
            file.Add(("ANIM", anim));
            file.AddRange(anmf);
            if (exif != null) file.Add(("EXIF", exif));
            if (xmp != null) file.Add(("XMP ", xmp));
            WriteRiff(output, file);
        }

        static void Put24(byte[] b, int o, int v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16);
        }

        /// <summary>Bounding box of the pixels that differ; a single pixel when nothing changed.</summary>
        static void ChangedBox(uint[] cur, uint[] prev, int w, int h, out int x0, out int y0, out int x1, out int y1)
        {
            x0 = w; y0 = h; x1 = 0; y1 = 0;
            for (int y = 0; y < h; y++)
            {
                var a = cur.AsSpan(y * w, w);
                var b = prev.AsSpan(y * w, w);
                int first = a.CommonPrefixLength(b);
                if (first == w) continue;
                int last = w - 1;
                while (a[last] == b[last]) last--;
                if (first < x0) x0 = first;
                if (last + 1 > x1) x1 = last + 1;
                if (y0 == h) y0 = y;
                y1 = y + 1;
            }
            if (x1 == 0) { x0 = 0; y0 = 0; x1 = 1; y1 = 1; }
        }
    }
}
