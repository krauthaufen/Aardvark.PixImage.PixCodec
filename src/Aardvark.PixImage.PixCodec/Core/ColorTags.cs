using System;
using System.Buffers.Binary;

namespace Aardvark.ImageCodecs
{
    /// <summary>Works out <see cref="ImageInfo.Transfer"/> for the formats whose decoders do not report it themselves.</summary>
    internal static class ColorTags
    {
        static bool IsFloat(SampleType t) => t is SampleType.Float16 or SampleType.Float32 or SampleType.Float64;

        /// <summary>What a format implies when the file does not say: linear for float samples, sRGB for 8 / 16 bit colour.</summary>
        public static TransferFunction Implied(ImageFormat format, SampleType type, ColorModel color)
            => format == ImageFormat.Unknown ? TransferFunction.Unknown
             : format is ImageFormat.Exr or ImageFormat.Hdr or ImageFormat.Pfm || IsFloat(type) ? TransferFunction.Linear
             : format is ImageFormat.Dds or ImageFormat.Dpx or ImageFormat.Cineon or ImageFormat.Astc or ImageFormat.Pvr or ImageFormat.Nrrd or ImageFormat.Nifti or ImageFormat.MetaImage or ImageFormat.Fits or ImageFormat.Envi ? TransferFunction.Unknown
             : Implied(type, color);

        public static TransferFunction Implied(SampleType type, ColorModel color)
            => IsFloat(type) ? TransferFunction.Linear
             : color != ColorModel.Other && type is SampleType.UInt8 or SampleType.UInt16 ? TransferFunction.Srgb
             : TransferFunction.Unknown;

        public static ImageInfo With(ImageInfo info, ReadOnlySpan<byte> data) => info with { Transfer = Of(info, data, out _) };

        /// <summary>The transfer function of a file; <paramref name="gamma"/> receives PNG's gAMA value for <see cref="TransferFunction.Gamma"/>.</summary>
        public static TransferFunction Of(ImageInfo info, ReadOnlySpan<byte> data, out double gamma)
        {
            gamma = 0;
            switch (info.Format)
            {
                case ImageFormat.Png: return Png(data, info, out gamma);
                case ImageFormat.Exr: case ImageFormat.Hdr: case ImageFormat.Pfm: return TransferFunction.Linear;
                // scientific data: values are measurements, not colours (float samples are reported linear)
                case ImageFormat.Nrrd: case ImageFormat.Nifti: case ImageFormat.MetaImage: case ImageFormat.Fits: case ImageFormat.Envi:
                    return IsFloat(info.SampleType) ? TransferFunction.Linear : TransferFunction.Unknown;
                case ImageFormat.Astc: return IsFloat(info.SampleType) ? TransferFunction.Linear : TransferFunction.Unknown;
                case ImageFormat.Pvr:
                    try { return Pvr.GetTextureInfo(data).Transfer; }
                    catch (Exception e) when (e is ImageFormatException or ImageNotSupportedException) { return TransferFunction.Unknown; }
                case ImageFormat.Dds:
                    {
                        try
                        {
                            var t = Dds.GetTextureInfo(data);
                            return t.IsSrgb ? TransferFunction.Srgb : IsFloat(t.SampleType) ? TransferFunction.Linear : TransferFunction.Unknown;
                        }
                        catch (ImageFormatException) { return TransferFunction.Unknown; }
                    }
                default: return Implied(info.SampleType, info.Color);
            }
        }

        /// <summary>PNG: cICP, then sRGB, then gAMA (an ICC profile is reported through HasIccProfile), else sRGB.</summary>
        static TransferFunction Png(ReadOnlySpan<byte> d, ImageInfo info, out double gamma)
        {
            gamma = 0;
            TransferFunction cicp = TransferFunction.Unknown;
            bool srgb = false, iccp = false;
            long pos = 8;
            while (pos + 8 <= d.Length)
            {
                uint len = BinaryPrimitives.ReadUInt32BigEndian(d.Slice((int)pos));
                var type = d.Slice((int)pos + 4, 4);
                if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8)) break;
                long body = pos + 8;
                if (body + len > d.Length) break;
                if (type.SequenceEqual("cICP"u8) && len >= 2)
                    cicp = d[(int)body + 1] switch
                    {
                        13 => TransferFunction.Srgb,
                        8 => TransferFunction.Linear,
                        16 => TransferFunction.Pq,
                        18 => TransferFunction.Hlg,
                        _ => TransferFunction.Unknown,
                    };
                else if (type.SequenceEqual("sRGB"u8)) srgb = true;
                else if (type.SequenceEqual("iCCP"u8)) iccp = true;
                else if (type.SequenceEqual("gAMA"u8) && len == 4) gamma = BinaryPrimitives.ReadUInt32BigEndian(d.Slice((int)body)) / 100000.0;
                pos = body + len + 4;
            }
            if (cicp != TransferFunction.Unknown) { gamma = 0; return cicp; }
            if (srgb || iccp) { gamma = 0; return TransferFunction.Srgb; }
            if (gamma > 0)
            {
                if (Math.Abs(gamma - 1) < 1e-4) { gamma = 0; return TransferFunction.Linear; }
                return TransferFunction.Gamma;
            }
            return Implied(info.SampleType, info.Color);
        }

        /// <summary>Records the file's transfer function in the image's metadata (when the decoder left it Unknown).</summary>
        public static RawImage Tag(RawImage img, ReadOnlySpan<byte> data, ImageFormat format)
        {
            if (img.Metadata?.Transfer is { } t && t != TransferFunction.Unknown) return img;
            var info = new ImageInfo(format, img.Width, img.Height, img.Channels, img.SampleType, img.Color);
            var tf = Of(info, data, out double gamma);
            // what the sample type implies needs no metadata of its own (Unknown is read that way)
            if (tf == TransferFunction.Unknown || img.Metadata == null && tf == Implied(img.SampleType, img.Color)) return img;
            var m = img.Metadata ??= new ImageMetadata();
            m.Transfer = tf;
            if (tf == TransferFunction.Gamma && m.Gamma == 0) m.Gamma = gamma;
            return img;
        }

        public static RawImage[] Tag(RawImage[] imgs, ReadOnlySpan<byte> data, ImageFormat format)
        {
            foreach (var i in imgs) Tag(i, data, format);
            return imgs;
        }
    }
}
