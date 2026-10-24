using System;

namespace Aardvark.ImageCodecs
{
    /// <summary>Options of <see cref="ImageOps.ConvertColor(RawImage, IccProfile, IccProfile, ColorConvertOptions)"/>.</summary>
    public sealed class ColorConvertOptions
    {
        public IccRenderingIntent Intent { get; set; } = IccRenderingIntent.Perceptual;
        /// <summary>Black point compensation (ISO 18619) for the relative colorimetric, perceptual and saturation intents.</summary>
        public bool BlackPointCompensation { get; set; }
        /// <summary>Sample type of the result (UInt8, UInt16, Float16, Float32); default: the input's (Float32 for Float64).</summary>
        public SampleType? OutputType { get; set; }
        /// <summary>The input samples are inverted (maximum = 0), as the CMYK of Adobe JPEGs (<see cref="JpegDecodeOptions.RawCmyk"/>).</summary>
        public bool InvertedInput { get; set; }
        /// <summary>Trilinear instead of tetrahedral interpolation of 3 dimensional CLUTs.</summary>
        public bool Trilinear { get; set; }
        /// <summary>Clamp float results to 0..1 (integer results are always clamped).</summary>
        public bool Clip { get; set; }
    }

    public static partial class ImageOps
    {
        #region ICC colour management

        /// <summary>
        /// Converts the colours of <paramref name="image"/> from profile <paramref name="from"/> to profile <paramref name="to"/>.
        /// The image's colour channels must match <paramref name="from"/> (Gray, Rgb, or <see cref="ColorModel.Other"/> with
        /// e.g. 4 CMYK channels, 0 = no ink); a trailing alpha channel (GrayAlpha, Rgba, or one extra channel of Other) is
        /// kept and only rescaled. The result has <paramref name="to"/>'s channels (Gray / Rgb / Other) plus alpha, its
        /// metadata embeds <paramref name="to"/> (<see cref="ImageMetadata.IccProfile"/>) and records its transfer function
        /// when it is sRGB, linear or a power law. Multi-threaded.
        /// </summary>
        public static RawImage ConvertColor(RawImage image, IccProfile from, IccProfile to,
            IccRenderingIntent intent = IccRenderingIntent.Perceptual, SampleType? outputType = null)
            => ConvertColor(image, from, to, new ColorConvertOptions { Intent = intent, OutputType = outputType });

        /// <inheritdoc cref="ConvertColor(RawImage, IccProfile, IccProfile, IccRenderingIntent, SampleType?)"/>
        public static RawImage ConvertColor(RawImage image, IccProfile from, IccProfile to, ColorConvertOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            var t = new IccTransform(from, to, options.Intent, options.BlackPointCompensation, options.Trilinear);
            return ConvertColor(image, t, options);
        }

        /// <summary>Converts with a prepared transform (reuse it for many images: its tables are cached).</summary>
        public static RawImage ConvertColor(RawImage image, IccTransform transform, ColorConvertOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(transform);
            options ??= new ColorConvertOptions();
            var r = transform.Apply(image, OutputTypeFor(image, options.OutputType), options.InvertedInput, options.Clip);
            var m = CopyAllMetadata(image.Metadata);
            m.IccProfile = transform.Destination.ToBytes();
            (m.Transfer, m.Gamma) = TransferOf(transform.Destination);
            r.Metadata = m;
            return r;
        }

        /// <summary>The image's embedded ICC profile (<see cref="ImageMetadata.IccProfile"/>), null when there is none or it is malformed.</summary>
        public static IccProfile? GetIccProfile(RawImage image)
        {
            ArgumentNullException.ThrowIfNull(image);
            return image.Metadata?.IccProfile is { Length: > 0 } b ? IccProfile.TryParse(b) : null;
        }

        /// <summary>
        /// Converts to sRGB (gray images to gray with the sRGB curve) using the image's embedded ICC profile; without one (or with a
        /// malformed or unusable one) the image's <see cref="ImageMetadata.Transfer"/> decides: sRGB / Unknown (linear for float
        /// samples) / Linear / Gamma with sRGB primaries. Images that need no change come back as they are. The result has no
        /// ICC profile and <see cref="TransferFunction.Srgb"/>. CMYK images (4 channels of <see cref="ColorModel.Other"/> with
        /// a CMYK profile) become Rgb; use <see cref="ConvertColor(RawImage, IccProfile, IccProfile, ColorConvertOptions)"/> with
        /// <see cref="ColorConvertOptions.InvertedInput"/> for inverted (Adobe JPEG) CMYK.
        /// </summary>
        public static RawImage ToSrgb(RawImage image, IccRenderingIntent intent = IccRenderingIntent.Perceptual, SampleType? outputType = null)
            => ToStandard(image, intent, outputType, linear: false, clip: false);

        /// <summary>
        /// Converts to linear light with the sRGB primaries (gray: linear gray) as Float32, like <see cref="ToSrgb"/> but with
        /// <see cref="TransferFunction.Linear"/>. Values outside the sRGB gamut are kept (negative or above 1) where the source
        /// profile is a matrix / TRC profile.
        /// </summary>
        public static RawImage ToLinearSrgb(RawImage image, IccRenderingIntent intent = IccRenderingIntent.Perceptual)
            => ToStandard(image, intent, SampleType.Float32, linear: true, clip: false);

        static RawImage ToStandard(RawImage image, IccRenderingIntent intent, SampleType? outputType, bool linear, bool clip)
        {
            ArgumentNullException.ThrowIfNull(image);
            var outType = OutputTypeFor(image, outputType);
            var src = SourceProfile(image, out bool embedded);
            bool gray = src.ColorSpace == IccColorSpace.Gray;
            var dst = gray ? (linear ? IccProfile.GrayLinear : IccProfile.GraySrgb) : (linear ? IccProfile.LinearSrgb : IccProfile.Srgb);
            var target = linear ? TransferFunction.Linear : TransferFunction.Srgb;
            IccTransform t;
            try { t = new IccTransform(src, dst, intent); }
            catch (Exception e) when (embedded && e is IccException or ImageFormatException)
            {
                src = SourceProfile(image, out _, ignoreIcc: true);
                gray = src.ColorSpace == IccColorSpace.Gray;
                dst = gray ? (linear ? IccProfile.GrayLinear : IccProfile.GraySrgb) : (linear ? IccProfile.LinearSrgb : IccProfile.Srgb);
                t = new IccTransform(src, dst, intent);
            }
            var meta = CopyAllMetadata(image.Metadata);
            meta.IccProfile = null;
            meta.Transfer = target;
            meta.Gamma = 0;
            if (t.IsIdentity)
            {
                bool noMeta = image.Metadata == null || image.Metadata.IccProfile == null
                    && (image.Metadata.Transfer == target || image.Metadata.Transfer == TransferFunction.Unknown && ImpliedTransfer(image) == target);
                if (outType == image.SampleType && noMeta) return image;
                var r = Retype(image, outType);
                r.Metadata = meta;
                return r;
            }
            var res = t.Apply(image, outType, false, clip);
            res.Metadata = meta;
            return res;
        }

        static TransferFunction ImpliedTransfer(RawImage image)
            => image.SampleType is SampleType.Float16 or SampleType.Float32 or SampleType.Float64 ? TransferFunction.Linear : TransferFunction.Srgb;

        /// <summary>The image's colour profile: the embedded one, else one built from its transfer function and the sRGB primaries.</summary>
        static IccProfile SourceProfile(RawImage image, out bool embedded, bool ignoreIcc = false)
        {
            embedded = false;
            if (!ignoreIcc && GetIccProfile(image) is { } p && p.Channels > 0 && p.Class is not (IccProfileClass.DeviceLink or IccProfileClass.NamedColor))
            {
                int colour = image.Color == ColorModel.Other ? image.Channels : image.HasAlpha ? image.Channels - 1 : image.Channels;
                if (p.Channels == colour || image.Color == ColorModel.Other && p.Channels == colour - 1) { embedded = true; return p; }
            }
            bool gray = image.Color is ColorModel.Gray or ColorModel.GrayAlpha;
            if (!gray && image.Color == ColorModel.Other) throw new ArgumentException("an image of colour model Other needs an ICC profile");
            var tf = image.Metadata?.Transfer ?? TransferFunction.Unknown;
            if (tf == TransferFunction.Unknown) tf = ImpliedTransfer(image);
            switch (tf)
            {
                case TransferFunction.Srgb: return gray ? IccProfile.GraySrgb : IccProfile.Srgb;
                case TransferFunction.Linear: return gray ? IccProfile.GrayLinear : IccProfile.LinearSrgb;
                case TransferFunction.Gamma:
                    {
                        double g = image.Metadata!.Gamma;
                        if (!(g > 0 && g < 100)) throw new ArgumentException("TransferFunction.Gamma needs ImageMetadata.Gamma");
                        var c = IccCurve.Gamma(1 / g);
                        return gray ? IccProfile.CreateGray("Gray gamma", CieXy.D65, c)
                                    : IccProfile.CreateRgb("sRGB primaries, gamma", CieXy.D65, new CieXy(0.64, 0.33), new CieXy(0.30, 0.60), new CieXy(0.15, 0.06), c);
                    }
                default: throw new NotSupportedException($"{tf} images (HDR video curves) can not be converted through ICC profiles");
            }
        }

        static SampleType OutputTypeFor(RawImage image, SampleType? requested)
        {
            if (requested is { } r) return r;
            return image.SampleType switch
            {
                SampleType.UInt8 or SampleType.UInt16 or SampleType.Float16 or SampleType.Float32 => image.SampleType,
                SampleType.Float64 => SampleType.Float32,
                _ => throw new ArgumentException($"{image.SampleType} samples have no colour meaning; use UInt8, UInt16 or float samples"),
            };
        }

        /// <summary>The same samples in another type (rescaled between integer ranges, rounded and clamped).</summary>
        static RawImage Retype(RawImage image, SampleType type)
        {
            int n = image.Data.Length;
            double inMax = Processing.Samples.MaxValue(image.SampleType), outMax = Processing.Samples.MaxValue(type);
            var dst = RawImage.CreateArray(type, n);
            if (type == image.SampleType) Array.Copy(image.Data, dst, n);
            else
            {
                double s = outMax / inMax;
                for (int i = 0; i < n; i++)
                {
                    double v = image.Data switch
                    {
                        byte[] b => b[i], ushort[] u => u[i], Half[] h => (double)h[i], float[] f => f[i], double[] d => d[i], _ => 0,
                    } * s;
                    switch (dst)
                    {
                        case byte[] b: b[i] = (byte)Math.Clamp(Math.Floor(v + 0.5), 0, 255); break;
                        case ushort[] u: u[i] = (ushort)Math.Clamp(Math.Floor(v + 0.5), 0, 65535); break;
                        case Half[] h: h[i] = (Half)v; break;
                        case float[] f: f[i] = (float)v; break;
                    }
                }
            }
            return new RawImage(image.Width, image.Height, image.Color, dst, image.Channels);
        }

        static ImageMetadata CopyAllMetadata(ImageMetadata? m) => m == null ? new ImageMetadata() : new ImageMetadata
        {
            IccProfile = m.IccProfile, Exif = m.Exif, Xmp = m.Xmp, DpiX = m.DpiX, DpiY = m.DpiY, Gamma = m.Gamma,
            Transfer = m.Transfer, Orientation = m.Orientation,
        };

        /// <summary>The transfer function a profile's TRCs amount to (sRGB, linear, a power law), else Unknown.</summary>
        static (TransferFunction, double) TransferOf(IccProfile p)
        {
            try
            {
                if (!p.IsMatrixShaper || UsesAnyLut(p)) return (TransferFunction.Unknown, 0);
                var c = p.ColorSpace == IccColorSpace.Gray ? p.GrayTrc! : p.RedTrc!;
                if (p.ColorSpace == IccColorSpace.Rgb && !(c.Same(p.GreenTrc!, 1e-5) && c.Same(p.BlueTrc!, 1e-5))) return (TransferFunction.Unknown, 0);
                if (c.Same(IccCurve.Srgb, 2e-5)) return (TransferFunction.Srgb, 0);
                if (c.IsIdentity || c.Same(IccCurve.Identity, 1e-6)) return (TransferFunction.Linear, 0);
                if (c.PureGamma is double g && !double.IsNaN(g) && g > 0) return (TransferFunction.Gamma, 1 / g);
            }
            catch (ImageFormatException) { }
            return (TransferFunction.Unknown, 0);
        }

        static bool UsesAnyLut(IccProfile p)
        {
            for (int i = 0; i < 3; i++)
                if (p.HasTag(IccProfile.LutSig(i, true)) || p.HasTag(IccProfile.LutSig(i, false))) return true;
            return false;
        }

        #endregion
    }
}
