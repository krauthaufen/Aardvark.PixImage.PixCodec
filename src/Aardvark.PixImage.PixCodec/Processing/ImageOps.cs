using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Aardvark.ImageCodecs.Processing;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// Geometric operations on <see cref="RawImage"/>s of any sample type and channel count: resizing with a choice of filters,
    /// cropping, flips, rotations by multiples of 90 degrees, applying the EXIF orientation, rotation by any angle, affine and
    /// perspective warps and arbitrary remapping. Every operation returns a new image; the source is never modified.
    /// </summary>
    public static partial class ImageOps
    {
        #region Resize

        /// <summary>
        /// Resizes to exactly <paramref name="width"/> x <paramref name="height"/> pixels with a separable filter (Lanczos3 by default),
        /// widened when downscaling so that it low-pass filters (no aliasing). Pixel areas are aligned: pixel i of the result is
        /// centred on source position (i + 0.5) * srcWidth / width. Alpha is premultiplied while filtering. Integer samples are
        /// rounded and clamped; float samples are not clamped (sharp filters can overshoot).
        /// </summary>
        public static RawImage Resize(RawImage image, int width, int height, ResizeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "output size must be positive");
            var r = Resampler.Resize(image, width, height, options ?? new ResizeOptions());
            r.Metadata = ScaledMetadata(image.Metadata, (double)width / image.Width, (double)height / image.Height);
            return r;
        }

        /// <summary>Resizes with the given filter (see <see cref="Resize(RawImage, int, int, ResizeOptions?)"/>).</summary>
        public static RawImage Resize(RawImage image, int width, int height, ResampleFilter filter)
            => Resize(image, width, height, new ResizeOptions { Filter = filter });

        /// <summary>
        /// Resizes by a factor (output size rounded to the nearest integer, at least 1).
        /// </summary>
        public static RawImage Scale(RawImage image, double scaleX, double scaleY, ResizeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (!(scaleX > 0) || !(scaleY > 0)) throw new ArgumentOutOfRangeException(nameof(scaleX), "scale must be positive");
            return Resize(image, Math.Max(1, (int)Math.Floor(image.Width * scaleX + 0.5)),
                Math.Max(1, (int)Math.Floor(image.Height * scaleY + 0.5)), options);
        }

        /// <summary>
        /// Resizes to fit into <paramref name="maxWidth"/> x <paramref name="maxHeight"/> keeping the aspect ratio. Images that fit
        /// already are returned unchanged (the same instance) unless <paramref name="allowUpscale"/> is set.
        /// </summary>
        public static RawImage ResizeToFit(RawImage image, int maxWidth, int maxHeight, ResizeOptions? options = null, bool allowUpscale = false)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (maxWidth <= 0 || maxHeight <= 0) throw new ArgumentOutOfRangeException(nameof(maxWidth));
            double s = Math.Min((double)maxWidth / image.Width, (double)maxHeight / image.Height);
            if (s >= 1 && !allowUpscale) return image;
            int w = Math.Clamp((int)Math.Floor(image.Width * s + 0.5), 1, maxWidth);
            int h = Math.Clamp((int)Math.Floor(image.Height * s + 0.5), 1, maxHeight);
            if (w == image.Width && h == image.Height) return image;
            return Resize(image, w, h, options);
        }

        #endregion

        #region Lossless rearrangements

        /// <summary>Copies the rectangle at (x, y) of size width x height. It must lie inside the image.</summary>
        public static RawImage Crop(RawImage image, int x, int y, int width, int height)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (width <= 0 || height <= 0 || x < 0 || y < 0 || x + (long)width > image.Width || y + (long)height > image.Height)
                throw new ArgumentOutOfRangeException(nameof(x), $"crop rectangle ({x}, {y}, {width}, {height}) is outside the {image.Width}x{image.Height} image");
            var dst = RawImage.Create(width, height, image.Color, image.SampleType, image.Channels);
            int bpp = image.Channels * RawImage.BytesPerSample(image.SampleType);
            var s = image.GetBytes();
            var d = dst.GetBytes();
            for (int r = 0; r < height; r++)
                s.Slice(((y + r) * image.Width + x) * bpp, width * bpp).CopyTo(d.Slice(r * width * bpp, width * bpp));
            dst.Metadata = CopyMetadata(image.Metadata);
            return dst;
        }

        /// <summary>Mirrors left to right.</summary>
        public static RawImage FlipHorizontal(RawImage image) => Reorient(image, 2);
        /// <summary>Mirrors top to bottom.</summary>
        public static RawImage FlipVertical(RawImage image) => Reorient(image, 4);
        /// <summary>Rotates by 90 degrees clockwise.</summary>
        public static RawImage Rotate90(RawImage image) => Reorient(image, 6);
        /// <summary>Rotates by 180 degrees.</summary>
        public static RawImage Rotate180(RawImage image) => Reorient(image, 3);
        /// <summary>Rotates by 270 degrees clockwise (90 counter-clockwise).</summary>
        public static RawImage Rotate270(RawImage image) => Reorient(image, 8);
        /// <summary>Mirrors across the main diagonal (rows become columns).</summary>
        public static RawImage Transpose(RawImage image) => Reorient(image, 5);
        /// <summary>Mirrors across the anti-diagonal.</summary>
        public static RawImage Transverse(RawImage image) => Reorient(image, 7);

        /// <summary>
        /// Turns an image stored with EXIF/TIFF orientation <paramref name="orientation"/> (1..8) upright, so that it looks as a viewer
        /// shows it. Without an argument the orientation in <see cref="RawImage.Metadata"/> is used. The result's metadata says
        /// orientation 1. Orientation 0 or 1 returns the image unchanged (the same instance). Decoders already do this by default.
        /// </summary>
        public static RawImage ApplyOrientation(RawImage image, int? orientation = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            int o = orientation ?? image.Metadata?.Orientation ?? 0;
            if (o < 0 || o > 8) throw new ArgumentOutOfRangeException(nameof(orientation), "EXIF orientation is 1..8");
            if (o <= 1) return image;
            var r = Reorient(image, o);
            MarkUpright(r.Metadata);
            return r;
        }

        /// <summary>
        /// Sets the orientation in the metadata (and its EXIF / XMP blocks) to 1 once the pixels are upright; for a turn
        /// by 90 degrees (orientation 5..8) the resolutions swap too, as in <see cref="ApplyOrientation"/>.
        /// </summary>
        internal static void MarkUpright(ImageMetadata? m, int turnedFrom = 1)
        {
            if (m == null) return;
            if (turnedFrom >= 5) (m.DpiX, m.DpiY) = (m.DpiY, m.DpiX);
            m.Orientation = 1;
            if (m.Exif != null) m.Exif = ExifTags.WithOrientation(m.Exif, 1);
            if (m.Xmp != null) m.Xmp = ExifTags.XmpWithOrientation(m.Xmp, 1);
        }

        static RawImage Reorient(RawImage image, int o)
        {
            ArgumentNullException.ThrowIfNull(image);
            bool swap = o >= 5;
            int w = image.Width, h = image.Height;
            int dw = swap ? h : w, dh = swap ? w : h;
            var dst = RawImage.Create(dw, dh, image.Color, image.SampleType, image.Channels);
            // source pixel index = base + x * dx + y * dy for destination pixel (x, y)
            (long b, long dx, long dy) = o switch
            {
                2 => (w - 1L, -1L, (long)w),
                3 => ((h - 1L) * w + w - 1, -1L, -(long)w),
                4 => ((h - 1L) * w, 1L, -(long)w),
                5 => (0L, (long)w, 1L),
                6 => ((h - 1L) * w, -(long)w, 1L),
                7 => ((h - 1L) * w + w - 1, -(long)w, -1L),
                8 => (w - 1L, (long)w, -1L),
                _ => (0L, 1L, (long)w),
            };
            int bpp = image.Channels * RawImage.BytesPerSample(image.SampleType);
            var s = image.GetBytes();
            var d = dst.GetBytes();
            switch (bpp)
            {
                case 1: Permute<byte>(s, d, dw, dh, b, dx, dy); break;
                case 2: Permute<ushort>(s, d, dw, dh, b, dx, dy); break;
                case 3: Permute<B3>(s, d, dw, dh, b, dx, dy); break;
                case 4: Permute<uint>(s, d, dw, dh, b, dx, dy); break;
                case 6: Permute<B6>(s, d, dw, dh, b, dx, dy); break;
                case 8: Permute<ulong>(s, d, dw, dh, b, dx, dy); break;
                case 12: Permute<B12>(s, d, dw, dh, b, dx, dy); break;
                case 16: Permute<B16>(s, d, dw, dh, b, dx, dy); break;
                default:
                    for (int y = 0; y < dh; y++)
                        for (int x = 0; x < dw; x++)
                            s.Slice((int)(b + x * dx + y * dy) * bpp, bpp).CopyTo(d.Slice((y * dw + x) * bpp, bpp));
                    break;
            }
            var m = CopyMetadata(image.Metadata);
            if (m != null && swap) (m.DpiX, m.DpiY) = (m.DpiY, m.DpiX);
            dst.Metadata = m;
            return dst;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct B3 { byte a, b, c; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct B6 { ushort a, b, c; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct B12 { uint a, b, c; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct B16 { ulong a, b; }

        static void Permute<TP>(Span<byte> srcBytes, Span<byte> dstBytes, int dw, int dh, long b, long dx, long dy) where TP : unmanaged
        {
            var src = MemoryMarshal.Cast<byte, TP>(srcBytes);
            var dst = MemoryMarshal.Cast<byte, TP>(dstBytes);
            unsafe
            {
                fixed (TP* ps = src)
                fixed (TP* pd = dst)
                {
                    TP* s = ps, d = pd;
                    const int T = 64; // tiles keep the strided reads of transposes in cache
                    int tilesY = (dh + T - 1) / T;
                    Parallel.For(0, tilesY, ty =>
                    {
                        int y0 = ty * T, y1 = Math.Min(dh, y0 + T);
                        for (int x0 = 0; x0 < dw; x0 += T)
                        {
                            int x1 = Math.Min(dw, x0 + T);
                            for (int y = y0; y < y1; y++)
                            {
                                long p = b + x0 * dx + y * dy;
                                TP* o = d + (long)y * dw;
                                for (int x = x0; x < x1; x++, p += dx) o[x] = s[p];
                            }
                        }
                    });
                }
            }
        }

        #endregion

        #region Warps

        /// <summary>
        /// Rotates counter-clockwise by <paramref name="degrees"/> about the image centre. With <paramref name="expand"/> the result is
        /// large enough to hold the whole rotated image, otherwise it has the source size. Areas outside the source get
        /// <see cref="WarpOptions.Border"/> (transparent black by default). Multiples of 90 degrees with the default nearest or
        /// interpolating filters reproduce the pixels exactly; prefer <see cref="Rotate90"/> and friends for those.
        /// </summary>
        public static RawImage Rotate(RawImage image, double degrees, bool expand = true, WarpOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            double rad = degrees * Math.PI / 180;
            double cos = Math.Cos(rad), sin = Math.Sin(rad);
            double q = degrees / 90;
            if (q == Math.Round(q))
            {
                // exact values for multiples of 90 degrees
                int k = (int)(((long)Math.Round(q) % 4 + 4) % 4);
                cos = k == 0 ? 1 : k == 2 ? -1 : 0;
                sin = k == 1 ? 1 : k == 3 ? -1 : 0;
            }
            int w = image.Width, h = image.Height, dw = w, dh = h;
            if (expand)
            {
                dw = Math.Max(1, (int)Math.Ceiling(Math.Abs(w * cos) + Math.Abs(h * sin) - 1e-7));
                dh = Math.Max(1, (int)Math.Ceiling(Math.Abs(w * sin) + Math.Abs(h * cos) - 1e-7));
            }
            double scx = w * 0.5, scy = h * 0.5, dcx = dw * 0.5, dcy = dh * 0.5;
            // destination offset (u, v) from the centre -> source offset: rotate back by the angle (y points down)
            return Warp(image, dw, dh, (y, sx, sy) =>
            {
                double v = y + 0.5 - dcy;
                for (int x = 0; x < sx.Length; x++)
                {
                    double u = x + 0.5 - dcx;
                    sx[x] = scx + cos * u - sin * v - 0.5;
                    sy[x] = scy + sin * u + cos * v - 0.5;
                }
            }, options);
        }

        /// <summary>
        /// Affine warp. <paramref name="dstToSrc"/> holds the 2x3 matrix (a, b, c, d, e, f) that maps a destination pixel (x, y)
        /// to the source position (a x + b y + c, d x + e y + f); pixel centres are at integer coordinates.
        /// </summary>
        public static RawImage WarpAffine(RawImage image, int width, int height, ReadOnlySpan<double> dstToSrc, WarpOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (dstToSrc.Length != 6) throw new ArgumentException("an affine matrix has 6 elements", nameof(dstToSrc));
            double a = dstToSrc[0], b = dstToSrc[1], c = dstToSrc[2], d = dstToSrc[3], e = dstToSrc[4], f = dstToSrc[5];
            return Warp(image, width, height, (y, sx, sy) =>
            {
                for (int x = 0; x < sx.Length; x++)
                {
                    sx[x] = a * x + b * y + c;
                    sy[x] = d * x + e * y + f;
                }
            }, options);
        }

        /// <summary>
        /// Perspective warp. <paramref name="dstToSrc"/> is the row-major 3x3 homography mapping a destination pixel (x, y, 1) to
        /// homogeneous source coordinates; pixel centres are at integer coordinates. Points mapped behind the projection centre get
        /// the border value.
        /// </summary>
        public static RawImage WarpPerspective(RawImage image, int width, int height, ReadOnlySpan<double> dstToSrc, WarpOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (dstToSrc.Length != 9) throw new ArgumentException("a homography has 9 elements", nameof(dstToSrc));
            var m = dstToSrc.ToArray();
            return Warp(image, width, height, (y, sx, sy) =>
            {
                for (int x = 0; x < sx.Length; x++)
                {
                    double wq = m[6] * x + m[7] * y + m[8];
                    if (!(wq > 1e-12)) { sx[x] = double.NaN; sy[x] = double.NaN; continue; }
                    sx[x] = (m[0] * x + m[1] * y + m[2]) / wq;
                    sy[x] = (m[3] * x + m[4] * y + m[5]) / wq;
                }
            }, options);
        }

        /// <summary>
        /// Generic remap: destination pixel (x, y) samples the source at (mapX[i], mapY[i]) with i = y * width + x; pixel centres
        /// are at integer coordinates. NaN positions get the border value.
        /// </summary>
        public static RawImage Remap(RawImage image, int width, int height, float[] mapX, float[] mapY, WarpOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(mapX);
            ArgumentNullException.ThrowIfNull(mapY);
            long n = (long)width * height;
            if (mapX.LongLength != n || mapY.LongLength != n) throw new ArgumentException("the maps must have width * height elements");
            return Warp(image, width, height, (y, sx, sy) =>
            {
                int o = y * width;
                for (int x = 0; x < sx.Length; x++) { sx[x] = mapX[o + x]; sy[x] = mapY[o + x]; }
            }, options);
        }

        static RawImage Warp(RawImage image, int width, int height, RowMap map, WarpOptions? options)
        {
            var r = Warper.Warp(image, width, height, map, options ?? new WarpOptions());
            r.Metadata = CopyMetadata(image.Metadata);
            return r;
        }

        #endregion

        static ImageMetadata? CopyMetadata(ImageMetadata? m) => m == null ? null : new ImageMetadata
        {
            IccProfile = m.IccProfile, Exif = m.Exif, Xmp = m.Xmp, DpiX = m.DpiX, DpiY = m.DpiY, Gamma = m.Gamma, Orientation = m.Orientation,
            Transfer = m.Transfer,
        };

        static ImageMetadata? ScaledMetadata(ImageMetadata? m, double sx, double sy)
        {
            var r = CopyMetadata(m);
            if (r != null) { r.DpiX *= sx; r.DpiY *= sy; }
            return r;
        }
    }
}
