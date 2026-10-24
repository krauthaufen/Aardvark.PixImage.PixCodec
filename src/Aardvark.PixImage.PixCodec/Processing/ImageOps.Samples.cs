using System;
using System.Numerics;
using System.Threading.Tasks;
using Aardvark.ImageCodecs.Processing;

namespace Aardvark.ImageCodecs
{
    public static partial class ImageOps
    {
        #region Sample types, channels, alpha

        /// <summary>
        /// The same image with samples of another type: UInt8, UInt16, Float16, Float32 and Float64, in and out. Values are
        /// scaled between the ranges: integer samples mean 0..max (255, 65535), float samples 0..1, so the value is multiplied
        /// by outMax / inMax (computed in double). Integer results are rounded half up (floor(v + 0.5)) and clamped to 0..max,
        /// NaN becomes 0; float results are neither clamped nor rounded beyond the precision of the type. Converting to the
        /// same type copies. The colour model, channels and metadata are kept.
        /// </summary>
        public static RawImage Retype(RawImage image, SampleType type)
        {
            ArgumentNullException.ThrowIfNull(image);
            CheckColourType(image.SampleType, nameof(image));
            CheckColourType(type, nameof(type));
            var dst = RawImage.CreateArray(type, image.Data.Length);
            if (type == image.SampleType) Array.Copy(image.Data, dst, image.Data.Length);
            else
            {
                double scale = Samples.MaxValue(type) / Samples.MaxValue(image.SampleType);
                switch (image.SampleType)
                {
                    case SampleType.UInt8: Retype((byte[])image.Data, dst, type, scale); break;
                    case SampleType.UInt16: Retype((ushort[])image.Data, dst, type, scale); break;
                    case SampleType.Float16: Retype((Half[])image.Data, dst, type, scale); break;
                    case SampleType.Float32: Retype((float[])image.Data, dst, type, scale); break;
                    default: Retype((double[])image.Data, dst, type, scale); break;
                }
            }
            return new RawImage(image.Width, image.Height, image.Color, dst, image.Channels, CopyMetadata(image.Metadata));
        }

        static void CheckColourType(SampleType t, string arg)
        {
            if (t is not (SampleType.UInt8 or SampleType.UInt16 or SampleType.Float16 or SampleType.Float32 or SampleType.Float64))
                throw new ArgumentException($"{t} samples are not supported; use UInt8, UInt16, Float16, Float32 or Float64", arg);
        }

        static void Retype<TS>(TS[] src, Array dst, SampleType type, double scale) where TS : unmanaged, INumberBase<TS>
        {
            switch (type)
            {
                case SampleType.UInt8: Retype(src, (byte[])dst, scale, 255); break;
                case SampleType.UInt16: Retype(src, (ushort[])dst, scale, 65535); break;
                case SampleType.Float16: Retype(src, (Half[])dst, scale, 0); break;
                case SampleType.Float32: Retype(src, (float[])dst, scale, 0); break;
                default: Retype(src, (double[])dst, scale, 0); break;
            }
        }

        // max > 0: integer result, rounded and clamped to 0..max
        static void Retype<TS, TD>(TS[] src, TD[] dst, double scale, double max)
            where TS : unmanaged, INumberBase<TS> where TD : unmanaged, INumberBase<TD>
        {
            const int Chunk = 1 << 16;
            Parallel.For(0, (src.Length + Chunk - 1) / Chunk, b =>
            {
                int i1 = Math.Min(src.Length, (b + 1) * Chunk);
                for (int i = b * Chunk; i < i1; i++)
                {
                    double v = double.CreateTruncating(src[i]) * scale;
                    dst[i] = max > 0 ? TD.CreateTruncating(double.IsNaN(v) ? 0 : Math.Clamp(Math.Floor(v + 0.5), 0, max)) : TD.CreateTruncating(v);
                }
            });
        }

        /// <summary>
        /// Converts between channel layouts, for every sample type. Gray is expanded to Rgb by repeating it; Rgb becomes Gray by
        /// the Rec. 601 luma 0.299 R + 0.587 G + 0.114 B of the stored values (no linearisation; integer samples rounded half up
        /// and clamped). Dropping alpha just drops it; an added alpha channel is opaque: the type's maximum for integer samples
        /// (255, 65535, ..., 127 for Int8), 1 for float samples. An image of colour model Other with 1 to 4 channels is taken as
        /// Gray, GrayAlpha, Rgb or Rgba by its channel count. <see cref="ImageChannels.AsDecoded"/>, and a layout the image
        /// already has, return the image itself. The metadata is copied; its ICC profile is dropped when gray becomes colour or
        /// colour gray.
        /// </summary>
        public static RawImage ConvertChannels(RawImage image, ImageChannels channels)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (channels == ImageChannels.AsDecoded) return image;
            var target = channels switch
            {
                ImageChannels.Gray => ColorModel.Gray,
                ImageChannels.GrayAlpha => ColorModel.GrayAlpha,
                ImageChannels.Rgb => ColorModel.Rgb,
                ImageChannels.Rgba => ColorModel.Rgba,
                _ => throw new ArgumentOutOfRangeException(nameof(channels)),
            };
            var from = image.Color;
            if (from == ColorModel.Other)
            {
                if (image.Channels > 4) throw new ArgumentException($"an image of colour model Other with {image.Channels} channels has no channel layout", nameof(image));
                from = RawImage.DefaultColorModel(image.Channels);
            }
            if (from == target && image.Color == target) return image;
            int sc = RawImage.ChannelCount(from), dc = RawImage.ChannelCount(target);
            bool sColour = sc >= 3, dColour = dc >= 3;
            var dst = RawImage.CreateArray(image.SampleType, checked(image.Width * image.Height * dc));
            var l = new Layout(image.Width, image.Height, sc, dc, sColour, sc is 2 or 4, dColour, dc is 2 or 4);
            switch (image.SampleType)
            {
                case SampleType.UInt8: ConvertChannels((byte[])image.Data, (byte[])dst, l, true); break;
                case SampleType.Int8: ConvertChannels((sbyte[])image.Data, (sbyte[])dst, l, true); break;
                case SampleType.UInt16: ConvertChannels((ushort[])image.Data, (ushort[])dst, l, true); break;
                case SampleType.Int16: ConvertChannels((short[])image.Data, (short[])dst, l, true); break;
                case SampleType.UInt32: ConvertChannels((uint[])image.Data, (uint[])dst, l, true); break;
                case SampleType.Int32: ConvertChannels((int[])image.Data, (int[])dst, l, true); break;
                case SampleType.UInt64: ConvertChannels((ulong[])image.Data, (ulong[])dst, l, true); break;
                case SampleType.Int64: ConvertChannels((long[])image.Data, (long[])dst, l, true); break;
                case SampleType.Float16: ConvertChannels((Half[])image.Data, (Half[])dst, l, false); break;
                case SampleType.Float32: ConvertChannels((float[])image.Data, (float[])dst, l, false); break;
                case SampleType.Float64: ConvertChannels((double[])image.Data, (double[])dst, l, false); break;
                default: throw new NotSupportedException(image.SampleType.ToString());
            }
            var m = CopyMetadata(image.Metadata);
            if (m != null && sColour != dColour) m.IccProfile = null;
            return new RawImage(image.Width, image.Height, target, dst, dc, m);
        }

        readonly record struct Layout(int W, int H, int SC, int DC, bool SColour, bool SAlpha, bool DColour, bool DAlpha);

        static void ConvertChannels<T>(T[] src, T[] dst, Layout l, bool isInt) where T : unmanaged, INumberBase<T>, IMinMaxValue<T>
        {
            T opaque = isInt ? T.MaxValue : T.One;
            int sc = l.SC, dc = l.DC;
            Parallel.For(0, l.H, y =>
            {
                int ps = y * l.W * sc, pd = y * l.W * dc;
                for (int x = 0; x < l.W; x++, ps += sc, pd += dc)
                {
                    if (l.DColour)
                    {
                        if (l.SColour) { dst[pd] = src[ps]; dst[pd + 1] = src[ps + 1]; dst[pd + 2] = src[ps + 2]; }
                        else dst[pd] = dst[pd + 1] = dst[pd + 2] = src[ps];
                    }
                    else if (l.SColour)
                    {
                        double v = 0.299 * double.CreateTruncating(src[ps]) + 0.587 * double.CreateTruncating(src[ps + 1])
                                 + 0.114 * double.CreateTruncating(src[ps + 2]);
                        dst[pd] = isInt ? T.CreateSaturating(Math.Floor(v + 0.5)) : T.CreateTruncating(v);
                    }
                    else dst[pd] = src[ps];
                    if (l.DAlpha) dst[pd + dc - 1] = l.SAlpha ? src[ps + sc - 1] : opaque;
                }
            });
        }

        /// <summary>
        /// Multiplies the colour channels by alpha, as <see cref="ResizeOptions.PremultiplyAlpha"/> does while filtering:
        /// colour * alpha / max for UInt8 and UInt16 samples (rounded half up, exactly), colour * alpha for float samples.
        /// The alpha channel is <paramref name="alphaChannel"/>, by default the last channel of GrayAlpha and Rgba images;
        /// images without one are returned unchanged (the same instance). Note that the result holds premultiplied colour,
        /// which the other operations and the encoders take as straight alpha (see <see cref="RawImage"/>).
        /// </summary>
        public static RawImage Premultiply(RawImage image, int? alphaChannel = null) => Premultiplied(image, alphaChannel, true);

        /// <summary>
        /// Divides the colour channels by alpha, the inverse of <see cref="Premultiply"/> and what
        /// <see cref="ResizeOptions.PremultiplyAlpha"/> does after filtering: colour * max / alpha for UInt8 and UInt16 samples
        /// (rounded half up exactly, clamped to max), colour * (1 / alpha) for float samples. Where alpha is 0 (or negative)
        /// the colour becomes 0. Images without alpha channel are returned unchanged (the same instance).
        /// </summary>
        public static RawImage Unpremultiply(RawImage image, int? alphaChannel = null) => Premultiplied(image, alphaChannel, false);

        static RawImage Premultiplied(RawImage image, int? alphaChannel, bool multiply)
        {
            ArgumentNullException.ThrowIfNull(image);
            int nc = image.Channels;
            int a = alphaChannel ?? (image.HasAlpha ? nc - 1 : -1);
            if (a >= nc || a < -1) throw new ArgumentOutOfRangeException(nameof(alphaChannel));
            if (a < 0) return image;
            CheckColourType(image.SampleType, nameof(image));
            var dst = RawImage.CreateArray(image.SampleType, image.Data.Length);
            switch (image.SampleType)
            {
                case SampleType.UInt8: PremultiplyInt((byte[])image.Data, (byte[])dst, image.Width, nc, a, 255, multiply); break;
                case SampleType.UInt16: PremultiplyInt((ushort[])image.Data, (ushort[])dst, image.Width, nc, a, 65535, multiply); break;
                case SampleType.Float16: PremultiplyFloat<Half, float>((Half[])image.Data, (Half[])dst, image.Width, nc, a, multiply); break;
                case SampleType.Float32: PremultiplyFloat<float, float>((float[])image.Data, (float[])dst, image.Width, nc, a, multiply); break;
                default: PremultiplyFloat<double, double>((double[])image.Data, (double[])dst, image.Width, nc, a, multiply); break;
            }
            return new RawImage(image.Width, image.Height, image.Color, dst, nc, CopyMetadata(image.Metadata));
        }

        static void PremultiplyInt<T>(T[] src, T[] dst, int w, int nc, int a, long max, bool multiply) where T : unmanaged, IBinaryInteger<T>
        {
            int rowLen = w * nc;
            Parallel.For(0, src.Length / rowLen, y =>
            {
                for (int p = y * rowLen, e = p + rowLen; p < e; p += nc)
                {
                    long al = long.CreateTruncating(src[p + a]);
                    for (int c = 0; c < nc; c++)
                    {
                        long v = long.CreateTruncating(src[p + c]);
                        if (c != a)
                            v = multiply ? (2 * v * al + max) / (2 * max) : al == 0 ? 0 : Math.Min(max, (2 * v * max + al) / (2 * al));
                        dst[p + c] = T.CreateTruncating(v);
                    }
                }
            });
        }

        // in the working type of the resampler: float for Half and float, double for double
        static void PremultiplyFloat<TS, TF>(TS[] src, TS[] dst, int w, int nc, int a, bool multiply)
            where TS : unmanaged, INumberBase<TS> where TF : unmanaged, IFloatingPointIeee754<TF>
        {
            int rowLen = w * nc;
            Parallel.For(0, src.Length / rowLen, y =>
            {
                for (int p = y * rowLen, e = p + rowLen; p < e; p += nc)
                {
                    TF al = TF.CreateTruncating(src[p + a]);
                    TF f = multiply ? al : al > TF.Zero ? TF.One / al : TF.Zero;
                    for (int c = 0; c < nc; c++)
                        dst[p + c] = c == a ? src[p + c] : TS.CreateTruncating(TF.CreateTruncating(src[p + c]) * f);
                }
            });
        }

        #endregion
    }
}
