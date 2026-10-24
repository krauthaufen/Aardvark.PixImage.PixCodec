using System;
using System.Threading;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// A reconstruction filter for resampling: a symmetric kernel k(x) that is zero for |x| &gt;= <see cref="Support"/>,
    /// with x in source pixels. The predefined filters cover the usual choices; <see cref="Cubic"/>,
    /// <see cref="Lanczos(int)"/> and <see cref="Create"/> make others.
    /// </summary>
    public sealed class ResampleFilter
    {
        readonly Func<double, double> kernel;

        /// <summary>Name for display.</summary>
        public string Name { get; }

        /// <summary>Radius in source pixels beyond which the kernel is zero.</summary>
        public double Support { get; }

        /// <summary>True for <see cref="Nearest"/>: picks one source pixel, no weighting at all.</summary>
        public bool IsNearest { get; }

        ResampleFilter(string name, double support, Func<double, double> kernel, bool nearest = false)
        {
            Name = name; Support = support; this.kernel = kernel; IsNearest = nearest;
        }

        /// <summary>The kernel value at offset <paramref name="x"/> (source pixels).</summary>
        public double Evaluate(double x) => kernel(x);

        public override string ToString() => Name;

        /// <summary>A custom filter. The kernel must be symmetric and should be 1 at 0.</summary>
        public static ResampleFilter Create(string name, double support, Func<double, double> kernel)
        {
            if (!(support > 0)) throw new ArgumentOutOfRangeException(nameof(support));
            return new ResampleFilter(name, support, kernel ?? throw new ArgumentNullException(nameof(kernel)));
        }

        /// <summary>Nearest neighbour: blocky, no new values, the fastest.</summary>
        public static ResampleFilter Nearest { get; } = new("Nearest", 0.5, x => x >= -0.5 && x < 0.5 ? 1 : 0, nearest: true);

        /// <summary>Box: area averaging when downscaling, nearest neighbour when upscaling.</summary>
        public static ResampleFilter Box { get; } = new("Box", 0.5, x => x > -0.5 && x <= 0.5 ? 1 : 0);

        /// <summary>Triangle (tent): bilinear interpolation.</summary>
        public static ResampleFilter Bilinear { get; } = new("Bilinear", 1, x => { x = Math.Abs(x); return x < 1 ? 1 - x : 0; });

        /// <summary>Hermite: cubic smoothstep between neighbours (C1, no overshoot).</summary>
        public static ResampleFilter Hermite { get; } = new("Hermite", 1, x => { x = Math.Abs(x); return x < 1 ? (2 * x - 3) * x * x + 1 : 0; });

        /// <summary>Bicubic, Keys' cubic convolution with a = -0.5 (= Catmull-Rom, Mitchell-Netravali B = 0, C = 0.5).</summary>
        public static ResampleFilter Bicubic { get; } = Cubic(0, 0.5, "Bicubic");

        /// <summary>Catmull-Rom spline, the same kernel as <see cref="Bicubic"/>.</summary>
        public static ResampleFilter CatmullRom { get; } = Cubic(0, 0.5, "CatmullRom");

        /// <summary>Mitchell-Netravali B = C = 1/3: a little softer than bicubic, very little ringing.</summary>
        public static ResampleFilter Mitchell { get; } = Cubic(1.0 / 3, 1.0 / 3, "Mitchell");

        /// <summary>Cubic B-spline (B = 1, C = 0): smooth, blurs, never overshoots.</summary>
        public static ResampleFilter BSpline { get; } = Cubic(1, 0, "BSpline");

        /// <summary>Hamming windowed sinc with a support of one pixel.</summary>
        public static ResampleFilter Hamming { get; } = new("Hamming", 1, x =>
        {
            x = Math.Abs(x);
            return x < 1 ? Sinc(x) * (0.54 + 0.46 * Math.Cos(Math.PI * x)) : 0;
        });

        /// <summary>Lanczos with 2 lobes.</summary>
        public static ResampleFilter Lanczos2 { get; } = Lanczos(2);

        /// <summary>Lanczos with 3 lobes, the usual high quality choice.</summary>
        public static ResampleFilter Lanczos3 { get; } = Lanczos(3);

        /// <summary>Gaussian with sigma 0.5 pixel, truncated at 2 pixels: soft, no ringing.</summary>
        public static ResampleFilter Gaussian { get; } = new("Gaussian", 2, x => Math.Abs(x) < 2 ? Math.Exp(-2 * x * x) : 0);

        /// <summary>The Mitchell-Netravali family of cubic filters with parameters B and C.</summary>
        public static ResampleFilter Cubic(double b, double c, string? name = null)
        {
            // piecewise cubic, from the definition in Mitchell & Netravali, "Reconstruction Filters in Computer Graphics" (1988)
            double p0 = (6 - 2 * b) / 6, p2 = (-18 + 12 * b + 6 * c) / 6, p3 = (12 - 9 * b - 6 * c) / 6;
            double q0 = (8 * b + 24 * c) / 6, q1 = (-12 * b - 48 * c) / 6, q2 = (6 * b + 30 * c) / 6, q3 = (-b - 6 * c) / 6;
            return new ResampleFilter(name ?? $"Cubic(B={b},C={c})", 2, x =>
            {
                x = Math.Abs(x);
                if (x < 1) return p0 + x * x * (p2 + x * p3);
                if (x < 2) return q0 + x * (q1 + x * (q2 + x * q3));
                return 0;
            });
        }

        /// <summary>Lanczos windowed sinc with <paramref name="lobes"/> lobes (support = lobes).</summary>
        public static ResampleFilter Lanczos(int lobes)
        {
            if (lobes < 1) throw new ArgumentOutOfRangeException(nameof(lobes));
            return new ResampleFilter($"Lanczos{lobes}", lobes, x =>
            {
                x = Math.Abs(x);
                return x < lobes ? Sinc(x) * Sinc(x / lobes) : 0;
            });
        }

        static double Sinc(double x)
        {
            if (x == 0) return 1;
            x *= Math.PI;
            return Math.Sin(x) / x;
        }
    }

    /// <summary>How samples outside the source image are obtained by the warping operations.</summary>
    public enum BorderMode
    {
        /// <summary>A constant value (see <see cref="WarpOptions.BorderValue"/>, transparent black by default).</summary>
        Constant = 0,
        /// <summary>The nearest edge pixel is repeated: aaa|abc|ccc.</summary>
        Clamp,
        /// <summary>The image tiles: bc|abc|ab.</summary>
        Wrap,
        /// <summary>Mirrored without repeating the edge pixel: cb|abc|ba.</summary>
        Mirror,
        /// <summary>Mirrored with the edge pixel repeated: ba|abc|cb.</summary>
        MirrorRepeat,
    }

    /// <summary>Options for <see cref="ImageOps.Resize(RawImage, int, int, ResizeOptions?)"/>.</summary>
    public sealed class ResizeOptions
    {
        /// <summary>The filter. When downscaling it is widened by the scale factor, so that it also low-pass filters (antialiasing).</summary>
        public ResampleFilter Filter { get; set; } = ResampleFilter.Lanczos3;

        /// <summary>
        /// Weight the colour channels with alpha while filtering (premultiplied alpha), so that the colour of fully transparent pixels
        /// does not bleed into visible ones. Only has an effect on images with an alpha channel.
        /// </summary>
        public bool PremultiplyAlpha { get; set; } = true;

        /// <summary>
        /// Index of the alpha channel, or -1 for none. Null (the default) takes it from the colour model: the last channel of
        /// GrayAlpha and Rgba images.
        /// </summary>
        public int? AlphaChannel { get; set; }

        /// <summary>
        /// Treat 8 and 16 bit samples as sRGB encoded and filter in linear light (the alpha channel stays linear). This avoids the
        /// darkening of fine bright and dark detail that filtering the encoded values causes. Float images are assumed to be linear
        /// already and are not affected.
        /// </summary>
        public bool LinearLight { get; set; }

        /// <summary>Upper bound on the number of threads, &lt;= 0 for all cores.</summary>
        public int MaxDegreeOfParallelism { get; set; }

        /// <summary>
        /// Cancels the resize: checked before every output row, a cancelled resize throws <see cref="OperationCanceledException"/>.
        /// </summary>
        public CancellationToken CancellationToken { get; set; }

        /// <summary>Tests: no integer box path, always the float one.</summary>
        internal bool FloatPathOnly { get; set; }
    }

    /// <summary>Options for the warping operations (<see cref="ImageOps.Rotate"/>, <see cref="ImageOps.WarpAffine"/>,
    /// <see cref="ImageOps.WarpPerspective"/>, <see cref="ImageOps.Remap"/>).</summary>
    public sealed class WarpOptions
    {
        /// <summary>
        /// The interpolation filter, evaluated at the exact source position (no antialiasing; for strong minification resize first).
        /// </summary>
        public ResampleFilter Filter { get; set; } = ResampleFilter.Bilinear;

        /// <summary>How samples outside the source image are obtained.</summary>
        public BorderMode Border { get; set; } = BorderMode.Constant;

        /// <summary>
        /// The value of every channel outside the image for <see cref="BorderMode.Constant"/>, in sample units (e.g. 255 for opaque
        /// 8 bit alpha). Null means 0 for all channels. A single value applies to all channels.
        /// </summary>
        public double[]? BorderValue { get; set; }

        /// <inheritdoc cref="ResizeOptions.PremultiplyAlpha"/>
        public bool PremultiplyAlpha { get; set; } = true;

        /// <inheritdoc cref="ResizeOptions.AlphaChannel"/>
        public int? AlphaChannel { get; set; }

        /// <inheritdoc cref="ResizeOptions.MaxDegreeOfParallelism"/>
        public int MaxDegreeOfParallelism { get; set; }
    }
}
