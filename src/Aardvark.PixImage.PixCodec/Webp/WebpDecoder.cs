using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.Intrinsics;

namespace Aardvark.ImageCodecs
{
    /// <summary>Decodes the frames of a parsed WebP file one after the other and composites animations.</summary>
    internal sealed class WebpDecoder
    {
        readonly WebpContainer _c;
        readonly WebpDecodeOptions _opt;
        byte[]? _canvas;
        int _index;
        int _orientation = -1;   // of the EXIF chunk, read on the first frame
        WebpFrameEntry? _prev;
        bool _prevKey;

        public WebpDecoder(WebpContainer c, WebpDecodeOptions options)
        {
            _c = c; _opt = options;
        }

        public static bool OutputHasAlpha(WebpContainer c)
        {
            var f = c.Frames[0];
            if (c.AlphaFlag || f.HasAlpha) return true;
            return c.Animated && !IsFull(c, f);
        }

        static bool IsFull(WebpContainer c, WebpFrameEntry f) => f.Width == c.Width && f.Height == c.Height;

        public static RawImage DecodeFirst(WebpContainer c, ReadOnlySpan<byte> data, WebpDecodeOptions opt)
        {
            bool alpha = OutputHasAlpha(c);
            byte[] px;
            if (!c.Animated) px = DecodeFrame(data, c.Frames[0], alpha ? 4 : 3, opt);
            else
            {
                var st = new WebpDecoder(c, new WebpDecodeOptions { MaxPixels = opt.MaxPixels, Multithreaded = opt.Multithreaded, ApplyOrientation = false, CancellationToken = opt.CancellationToken });
                px = (byte[])st.Next(data)!.Image.Data;
                if (!alpha)
                {
                    var rgb = new byte[px.Length / 4 * 3];
                    for (int i = 0, o = 0; i < px.Length; i += 4, o += 3) { rgb[o] = px[i]; rgb[o + 1] = px[i + 1]; rgb[o + 2] = px[i + 2]; }
                    px = rgb;
                }
            }
            var img = new RawImage(c.Width, c.Height, alpha ? ColorModel.Rgba : ColorModel.Rgb, px, 0, Metadata(c, data));
            return opt.ApplyOrientation ? ImageOps.ApplyOrientation(img) : img;
        }

        /// <summary>The frame's own pixels as RGB or RGBA; alpha is 255 where the frame has none.</summary>
        static byte[] DecodeFrame(ReadOnlySpan<byte> data, WebpFrameEntry f, int channels, WebpDecodeOptions opt)
        {
            var bits = data.Slice(f.DataOffset, f.DataLength);
            if (f.Lossless)
            {
                opt.CancellationToken.ThrowIfCancellationRequested();
                var argb = Vp8lDecoder.Decode(bits, out int w, out int h, opt.MaxPixels, out bool rented, opt.CancellationToken);
                opt.CancellationToken.ThrowIfCancellationRequested();
                var px = GC.AllocateUninitializedArray<byte>(w * h * channels);
                ArgbToBytes(argb, w * h, px, channels);
                if (rented) ArrayPool<uint>.Shared.Return(argb);
                return px;
            }
            var frame = Vp8Decoder.Decode(bits, opt.MaxPixels, opt.Multithreaded, opt.CancellationToken);
            byte[]? a = null;
            try
            {
                // the alpha plane goes straight into the conversion
                if (channels == 4 && f.AlphLength >= 0) a = WebpAlpha.Decode(data.Slice(f.AlphOffset, f.AlphLength), frame.Width, frame.Height, opt.CancellationToken);
                var dst = GC.AllocateUninitializedArray<byte>(frame.Width * frame.Height * channels);
                WebpYuv.ToRgb(frame, dst, channels, opt.Multithreaded, a);
                return dst;
            }
            finally
            {
                frame.Release();
                if (a != null) ArrayPool<byte>.Shared.Return(a);
            }
        }

        /// <summary>0xAARRGGBB pixels to interleaved R, G, B(, A) bytes.</summary>
        static unsafe void ArgbToBytes(uint[] argb, int n, byte[] px, int channels)
        {
            int i = 0;
            fixed (uint* src = argb)
            fixed (byte* dst = px)
            {
                if (Vector128.IsHardwareAccelerated)
                {
                    if (channels == 4)
                    {
                        var order = Vector128.Create((byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);
                        for (; i + 4 <= n; i += 4) Vector128.Shuffle(Vector128.Load((byte*)(src + i)), order).Store(dst + i * 4);
                    }
                    else
                    {
                        // 12 bytes per step; the store writes 16, so the last pixels are left to the loop below
                        var order = Vector128.Create((byte)2, 1, 0, 6, 5, 4, 10, 9, 8, 14, 13, 12, 255, 255, 255, 255);
                        for (; i + 6 <= n; i += 4) Vector128.Shuffle(Vector128.Load((byte*)(src + i)), order).Store(dst + i * 3);
                    }
                }
                for (; i < n; i++)
                {
                    uint v = src[i];
                    byte* d = dst + i * channels;
                    d[0] = (byte)(v >> 16); d[1] = (byte)(v >> 8); d[2] = (byte)v;
                    if (channels == 4) d[3] = (byte)(v >> 24);
                }
            }
        }

        public WebpFrame? Next(ReadOnlySpan<byte> data)
        {
            if (_index >= _c.Frames.Count) return null;
            var f = _c.Frames[_index];
            var frame = new WebpFrame
            {
                Index = _index, X = f.X, Y = f.Y, Width = f.Width, Height = f.Height, DurationMs = f.Duration,
                Blend = f.NoBlend ? WebpBlend.NoBlend : WebpBlend.AlphaBlend,
                Disposal = f.DisposeBackground ? WebpDisposal.Background : WebpDisposal.None,
                IsLossless = f.Lossless, HasAlpha = f.HasAlpha,
            };
            if (_opt.FrameMode == WebpFrameMode.Raw)
            {
                int ch = f.HasAlpha ? 4 : 3;
                frame.Image = new RawImage(f.Width, f.Height, ch == 4 ? ColorModel.Rgba : ColorModel.Rgb, DecodeFrame(data, f, ch, _opt));
            }
            else if (!_c.Animated)
                frame.Image = new RawImage(f.Width, f.Height, ColorModel.Rgba, DecodeFrame(data, f, 4, _opt));
            else
                frame.Image = new RawImage(_c.Width, _c.Height, ColorModel.Rgba, Composite(data, f));
            if (_opt.FrameMode != WebpFrameMode.Raw && _opt.ApplyOrientation)
            {
                if (_orientation < 0) ExifInfo(_c, data, out _orientation, out _, out _);
                frame.Image = ImageOps.ApplyOrientation(frame.Image, _orientation);
            }
            _index++;
            return frame;
        }

        /// <summary>
        /// Draws the frame onto the canvas the way libwebp's animation decoder does. A "key frame" starts from an empty
        /// canvas and is copied as stored; other frames are blended over what the previous frame left behind.
        /// </summary>
        byte[] Composite(ReadOnlySpan<byte> data, WebpFrameEntry f)
        {
            int cw = _c.Width, chh = _c.Height;
            if ((long)cw * chh > _opt.MaxPixels)
                throw new ImageNotSupportedException($"WebP canvas of {cw}x{chh} exceeds WebpDecodeOptions.MaxPixels ({_opt.MaxPixels})");
            var px = DecodeFrame(data, f, 4, _opt);       // before the canvas: garbage must not allocate one
            var canvas = _canvas ??= new byte[cw * chh * 4];
            var prev = _prev;
            bool key = prev == null
                || ((!f.HasAlpha || f.NoBlend) && IsFull(_c, f))
                || (prev.DisposeBackground && (IsFull(_c, prev) || _prevKey));
            if (key)
            {
                if (prev != null && !IsFull(_c, f)) Array.Clear(canvas);
            }
            else if (prev!.DisposeBackground)
            {
                for (int y = 0; y < prev.Height; y++)
                    canvas.AsSpan(((prev.Y + y) * cw + prev.X) * 4, prev.Width * 4).Clear();
            }

            bool blend = !key && !f.NoBlend && f.HasAlpha;
            // pixels inside a rectangle that was just cleared are copied, not blended with transparency
            int clearX0 = 0, clearX1 = 0, clearY0 = 0, clearY1 = 0;
            if (blend && prev!.DisposeBackground)
            {
                clearX0 = prev.X; clearX1 = prev.X + prev.Width; clearY0 = prev.Y; clearY1 = prev.Y + prev.Height;
            }
            for (int y = 0; y < f.Height; y++)
            {
                var src = px.AsSpan(y * f.Width * 4, f.Width * 4);
                var dst = canvas.AsSpan(((f.Y + y) * cw + f.X) * 4, f.Width * 4);
                if (!blend) { src.CopyTo(dst); continue; }
                bool rowCleared = f.Y + y >= clearY0 && f.Y + y < clearY1;
                for (int x = 0; x < f.Width; x++)
                {
                    var s = src.Slice(x * 4, 4);
                    var d = dst.Slice(x * 4, 4);
                    uint sa = s[3];
                    if (sa == 255 || (rowCleared && f.X + x >= clearX0 && f.X + x < clearX1)) { s.CopyTo(d); continue; }
                    if (sa == 0) continue;
                    uint da = d[3] * (256 - sa) >> 8;
                    uint ba = sa + da;
                    uint scale = (1u << 24) / ba;
                    d[0] = (byte)((s[0] * sa + d[0] * da) * scale >> 24);
                    d[1] = (byte)((s[1] * sa + d[1] * da) * scale >> 24);
                    d[2] = (byte)((s[2] * sa + d[2] * da) * scale >> 24);
                    d[3] = (byte)ba;
                }
            }
            _prev = f; _prevKey = key;
            return (byte[])canvas.Clone();
        }

        // ------------------------------------------------------------------ metadata

        /// <summary>The file level properties; Width / Height are those of the frames <paramref name="opt"/> makes.</summary>
        public static void FillInfo(WebpContainer c, ReadOnlySpan<byte> data, WebpAnimationInfo info, WebpDecodeOptions opt)
        {
            ExifInfo(c, data, out int orientation, out _, out _);
            bool swap = opt.ApplyOrientation && opt.FrameMode != WebpFrameMode.Raw && orientation >= 5;
            info.Width = swap ? c.Height : c.Width; info.Height = swap ? c.Width : c.Height;
            info.FrameCount = c.Frames.Count;
            info.IsAnimated = c.Animated;
            info.LoopCount = c.Animated ? c.LoopCount : 1;
            uint bg = c.Background;     // stored as B, G, R, A
            info.BackgroundColor = ((byte)(bg >> 16), (byte)(bg >> 8), (byte)bg, (byte)(bg >> 24));
            info.HasAlpha = OutputHasAlpha(c);
            var m = Metadata(c, data);
            info.IccProfile = m?.IccProfile;
            info.Exif = m?.Exif;
            info.Xmp = c.XmpLength < 0 ? null : data.Slice(c.XmpOffset, c.XmpLength).ToArray();
        }

        /// <summary>Orientation and resolution (pixels per inch) of the EXIF chunk, zeros where it does not say.</summary>
        public static void ExifInfo(WebpContainer c, ReadOnlySpan<byte> data, out int orientation, out double dpiX, out double dpiY)
        {
            orientation = 0; dpiX = dpiY = 0;
            if (c.ExifLength < 0) return;
            ReadExif(StripExifPrefix(data.Slice(c.ExifOffset, c.ExifLength)), out orientation, out dpiX, out dpiY);
        }

        // some writers keep the JPEG APP1 prefix
        static ReadOnlySpan<byte> StripExifPrefix(ReadOnlySpan<byte> exif)
            => exif.Length >= 6 && exif.Slice(0, 6).SequenceEqual("Exif\0\0"u8) ? exif.Slice(6) : exif;

        static ImageMetadata? Metadata(WebpContainer c, ReadOnlySpan<byte> data)
        {
            if (c.IccLength < 0 && c.ExifLength < 0 && c.XmpLength < 0) return null;
            var m = new ImageMetadata();
            if (c.XmpLength >= 0) m.Xmp = data.Slice(c.XmpOffset, c.XmpLength).ToArray();
            if (c.IccLength >= 0) m.IccProfile = data.Slice(c.IccOffset, c.IccLength).ToArray();
            if (c.ExifLength >= 0)
            {
                var exif = StripExifPrefix(data.Slice(c.ExifOffset, c.ExifLength));
                m.Exif = exif.ToArray();
                ReadExif(exif, out m.Orientation, out m.DpiX, out m.DpiY);
            }
            return m;
        }

        /// <summary>
        /// Reads Orientation (0x0112), XResolution / YResolution (0x011A / 0x011B) and ResolutionUnit (0x0128, inch
        /// unless it says centimeter; "no unit" leaves the resolution unknown) from the first IFD of an EXIF block.
        /// </summary>
        internal static void ReadExif(ReadOnlySpan<byte> t, out int orientation, out double dpiX, out double dpiY)
        {
            orientation = 0; dpiX = dpiY = 0;
            if (t.Length < 8) return;
            bool le;
            if (t[0] == 'I' && t[1] == 'I') le = true;
            else if (t[0] == 'M' && t[1] == 'M') le = false;
            else return;
            uint ifd = U32(t, 4, le);
            if (ifd > (uint)t.Length - 2) return;
            int n = U16(t, (int)ifd, le);
            double rx = 0, ry = 0;
            int unit = 2;
            for (int i = 0; i < n; i++)
            {
                long e = ifd + 2 + 12L * i;
                if (e + 12 > t.Length) break;
                int tag = U16(t, (int)e, le), type = U16(t, (int)e + 2, le);
                if (tag == 0x0112 && type == 3)
                {
                    int v = U16(t, (int)e + 8, le);
                    if (v >= 1 && v <= 8) orientation = v;
                }
                else if (tag == 0x0128 && type == 3) unit = U16(t, (int)e + 8, le);
                else if ((tag == 0x011a || tag == 0x011b) && type == 5)
                {
                    uint off = U32(t, (int)e + 8, le);
                    if (off > (uint)t.Length - 8 || t.Length < 8) continue;
                    uint num = U32(t, (int)off, le), den = U32(t, (int)off + 4, le);
                    double v = den == 0 ? 0 : (double)num / den;
                    if (tag == 0x011a) rx = v; else ry = v;
                }
            }
            double scale = unit == 2 ? 1 : unit == 3 ? 2.54 : 0;
            dpiX = rx * scale; dpiY = ry * scale;
        }

        static int U16(ReadOnlySpan<byte> t, int o, bool le)
            => le ? BinaryPrimitives.ReadUInt16LittleEndian(t.Slice(o)) : BinaryPrimitives.ReadUInt16BigEndian(t.Slice(o));

        static uint U32(ReadOnlySpan<byte> t, int o, bool le)
            => le ? BinaryPrimitives.ReadUInt32LittleEndian(t.Slice(o)) : BinaryPrimitives.ReadUInt32BigEndian(t.Slice(o));
    }
}
