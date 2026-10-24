using System;
using System.Runtime.InteropServices;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// Walks the frames of an APNG (or the single image of a plain PNG) one at a time and keeps the canvas
    /// between them. Holds no reference to the file data; the caller passes it to every call.
    /// </summary>
    internal sealed class PngAnimationDecoder
    {
        readonly PngStructure _s;
        readonly PngDecodeOptions _options;      // as given: used for raw frames
        readonly PngDecodeOptions _composite;    // forces the plain expanded decode that compositing needs
        readonly bool _wide;                     // 16 bit samples
        readonly int _pixelBytes;                // bytes per canvas pixel: 4 or 8
        int _next;
        byte[]? _canvas;                         // Rgba canvas as raw bytes (UInt16 samples in host order)
        byte[]? _saved;                          // region backup for dispose "previous"

        public PngAnimationDecoder(ReadOnlySpan<byte> data, PngDecodeOptions? options)
        {
            _options = options ?? new PngDecodeOptions();
            _s = PngChunks.Parse(data, _options, PngChunks.ParseFlags.Animation);
            _composite = new PngDecodeOptions
            {
                VerifyChecksums = _options.VerifyChecksums,
                AllowInvalidPaletteIndices = _options.AllowInvalidPaletteIndices,
                MaxPixels = _options.MaxPixels,
                CancellationToken = _options.CancellationToken,
            };
            PngDecoder.CheckMaxPixels(_s.Header.Width, _s.Header.Height, _options.MaxPixels);
            // The canvas is as large as the default image, which the file must be able to hold: a header that
            // lies about the size is rejected before a canvas is allocated for it.
            PngDecoder.InflatedSize(_s.Header, _s.IdatTotal);
            _wide = _s.Header.BitDepth == 16;
            _pixelBytes = _wide ? 8 : 4;
        }

        public void FillInfo(PngAnimationInfo info)
        {
            bool swap = Orientation >= 5;
            info.Width = swap ? _s.Header.Height : _s.Header.Width;
            info.Height = swap ? _s.Header.Width : _s.Header.Height;
            info.FrameCount = _s.Frames!.Count;
            info.IsAnimated = _s.Animated;
            info.LoopCount = _s.Plays;
            info.DefaultImageIsFirstFrame = _s.Frames[0].UsesIdat;
            info.SampleType = _wide ? SampleType.UInt16 : SampleType.UInt8;
        }

        public PngFrame? Next(ReadOnlySpan<byte> data)
        {
            var frames = _s.Frames!;
            if (_next >= frames.Count) return null;
            var e = frames[_next];
            var frame = new PngFrame
            {
                Index = _next, X = e.X, Y = e.Y, Width = e.Width, Height = e.Height,
                DelayNumerator = e.DelayNumerator, DelayDenominator = e.DelayDenominator,
                Disposal = e.Disposal, Blend = e.Blend,
            };

            if (_options.FrameMode == PngFrameMode.Raw)
            {
                frame.Image = PngDecoder.DecodeImage(data, _s, e, _options);
                if (e.UsesIdat) frame.Image.Metadata = _s.Meta;
                _next++;
                return frame;
            }

            int width = _s.Header.Width, height = _s.Header.Height;
            if (_canvas == null)
            {
                long bytes = (long)width * height * _pixelBytes;
                if (bytes > int.MaxValue) throw new ImageNotSupportedException("PNG canvas is too large (more than 2 GB of pixel data)");
                _canvas = new byte[bytes];
            }

            var rgba = ToRgba(PngDecoder.DecodeImage(data, _s, e, _composite));
            // On the first frame "previous" means "background": there is nothing to go back to.
            var disposal = _next == 0 && e.Disposal == PngDisposal.Previous ? PngDisposal.Background : e.Disposal;
            int rowBytes = e.Width * _pixelBytes;
            if (disposal == PngDisposal.Previous)
            {
                if (_saved == null || _saved.Length < rowBytes * e.Height) _saved = new byte[rowBytes * e.Height];
                for (int y = 0; y < e.Height; y++) Region(e, y).CopyTo(_saved.AsSpan(y * rowBytes));
            }

            for (int y = 0; y < e.Height; y++)
            {
                var src = rgba.Slice(y * rowBytes, rowBytes);
                var dst = Region(e, y);
                if (e.Blend == PngBlend.Source) src.CopyTo(dst);
                else if (_wide) Over16(MemoryMarshal.Cast<byte, ushort>(src), MemoryMarshal.Cast<byte, ushort>(dst));
                else Over8(src, dst);
            }

            var image = RawImage.Create(width, height, ColorModel.Rgba, _wide ? SampleType.UInt16 : SampleType.UInt8);
            _canvas.CopyTo(image.GetBytes());
            if (_next == 0) image.Metadata = _s.Meta;
            frame.Image = Orientation > 1 ? ImageOps.ApplyOrientation(image, Orientation) : image;

            if (disposal == PngDisposal.Background)
                for (int y = 0; y < e.Height; y++) Region(e, y).Clear();
            else if (disposal == PngDisposal.Previous)
                for (int y = 0; y < e.Height; y++) _saved.AsSpan(y * rowBytes, rowBytes).CopyTo(Region(e, y));

            _next++;
            return frame;
        }

        /// <summary>The orientation composited frames are turned upright with, 0 when they are not.</summary>
        int Orientation => _options.ApplyOrientation && _options.FrameMode == PngFrameMode.Composited ? _s.Meta?.Orientation ?? 0 : 0;

        /// <summary>One row of the frame's region on the canvas.</summary>
        Span<byte> Region(PngFrameEntry e, int row)
            => _canvas.AsSpan(((e.Y + row) * _s.Header.Width + e.X) * _pixelBytes, e.Width * _pixelBytes);

        /// <summary>The decoded frame as Rgba samples (raw bytes) of the same sample type.</summary>
        static ReadOnlySpan<byte> ToRgba(RawImage img)
        {
            if (img.Color == ColorModel.Rgba) return img.GetBytes();
            int pixels = img.Width * img.Height;
            if (img.SampleType == SampleType.UInt16)
            {
                var src = img.GetData<ushort>();
                var dst = new ushort[pixels * 4];
                Expand<ushort>(src, dst, img.Color, ushort.MaxValue);
                return MemoryMarshal.AsBytes<ushort>(dst);
            }
            else
            {
                var src = img.GetData<byte>();
                var dst = new byte[pixels * 4];
                Expand<byte>(src, dst, img.Color, byte.MaxValue);
                return dst;
            }
        }

        static void Expand<T>(ReadOnlySpan<T> src, Span<T> dst, ColorModel color, T opaque) where T : unmanaged
        {
            int d = 0;
            switch (color)
            {
                case ColorModel.Gray:
                    for (int i = 0; i < src.Length; i++, d += 4) { dst[d] = dst[d + 1] = dst[d + 2] = src[i]; dst[d + 3] = opaque; }
                    break;
                case ColorModel.GrayAlpha:
                    for (int i = 0; i < src.Length; i += 2, d += 4) { dst[d] = dst[d + 1] = dst[d + 2] = src[i]; dst[d + 3] = src[i + 1]; }
                    break;
                default: // Rgb
                    for (int i = 0; i < src.Length; i += 3, d += 4) { dst[d] = src[i]; dst[d + 1] = src[i + 1]; dst[d + 2] = src[i + 2]; dst[d + 3] = opaque; }
                    break;
            }
        }

        // "Over" for straight (non-premultiplied) alpha, rounded exactly:
        //   alpha = sa + da * (1 - sa),  color = (sc * sa + dc * da * (1 - sa)) / alpha
        // An opaque source, or any source over a fully transparent destination, replaces the pixel unchanged.

        static void Over8(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            for (int i = 0; i < src.Length; i += 4)
            {
                uint sa = src[i + 3], da = dst[i + 3];
                if (sa == 0) continue;
                if (sa == 255 || da == 0)
                {
                    src.Slice(i, 4).CopyTo(dst.Slice(i));
                    continue;
                }
                uint sw = sa * 255, dw = da * (255 - sa);   // weights scaled by 255 * 255
                uint total = sw + dw;
                for (int c = 0; c < 3; c++)
                    dst[i + c] = (byte)((src[i + c] * sw + dst[i + c] * dw + total / 2) / total);
                dst[i + 3] = (byte)((total + 127) / 255);
            }
        }

        static void Over16(ReadOnlySpan<ushort> src, Span<ushort> dst)
        {
            for (int i = 0; i < src.Length; i += 4)
            {
                ulong sa = src[i + 3], da = dst[i + 3];
                if (sa == 0) continue;
                if (sa == 65535 || da == 0)
                {
                    src.Slice(i, 4).CopyTo(dst.Slice(i));
                    continue;
                }
                ulong sw = sa * 65535, dw = da * (65535 - sa);
                ulong total = sw + dw;
                for (int c = 0; c < 3; c++)
                    dst[i + c] = (ushort)((src[i + c] * sw + dst[i + c] * dw + total / 2) / total);
                dst[i + 3] = (ushort)((total + 32767) / 65535);
            }
        }
    }
}
