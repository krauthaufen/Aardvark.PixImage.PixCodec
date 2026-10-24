using System;
using System.IO;

namespace Aardvark.ImageCodecs
{
    public static partial class ImageCodec
    {
        /// <summary>Decodes an image of any supported format with format independent settings (see <see cref="ImageDecodeOptions"/>).</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, ImageDecodeOptions? options)
        {
            if (options == null || options.IsDefault) return Decode(data);
            var f = DetectFormat(data);
            options.CancellationToken.ThrowIfCancellationRequested();
            var img = ColorTags.Tag(DecodeCore(data, f, options), data, f);
            options.CancellationToken.ThrowIfCancellationRequested();
            // UInt16 samples of fewer bits (12 bit JPEG, 10 bit DPX) are rescaled from their own range
            int bits = img.SampleType == SampleType.UInt16 && options.SampleType is { } t && t != SampleType.UInt16 ? HeaderInfo(data).BitsPerSample : 0;
            return Finish(img, options, bits);
        }

        /// <inheritdoc cref="Decode(ReadOnlySpan{byte}, ImageDecodeOptions?)"/>
        public static RawImage Decode(Stream stream, ImageDecodeOptions? options) => WithData(stream, d => Decode(d, options));

        /// <inheritdoc cref="Decode(ReadOnlySpan{byte}, ImageDecodeOptions?)"/>
        public static RawImage Decode(string path, ImageDecodeOptions? options)
        {
            if (options == null || options.IsDefault) return Decode(path);
            var f = SniffFile(path);
            if (IsVolumeFormat(f))
            {
                CheckMaxPixels(GetInfo(path), options.MaxPixels);
                return Finish(ColorTags.Tag(DecodeVolume(path).GetSlice(0), default, f), options, 0);
            }
            return WithFile(path, d => Decode(d, options));
        }

        /// <summary>
        /// Decodes in bands of <paramref name="bandHeight"/> rows, top to bottom: JPEG (8 bit) and PNG (non-interlaced) without
        /// ever holding the whole image (see <see cref="Jpeg.DecodeBands"/>, <see cref="Png.DecodeBands"/>), every other format
        /// decoded whole and then cut. <paramref name="onBand"/> gets each band and the index of its first row; the band image
        /// may be reused for the next band, so copy what has to outlive the call. The pixels are as stored: the orientation is
        /// not applied, whatever <see cref="ImageDecodeOptions.ApplyOrientation"/> says. The other options apply. Returns the
        /// image's metadata.
        /// </summary>
        public static ImageMetadata? DecodeBands(ReadOnlySpan<byte> data, int bandHeight, Action<RawImage, int> onBand, ImageDecodeOptions? options = null)
        {
            ImageBands.CheckArguments(bandHeight, onBand);
            var o = options ?? ImageDecodeOptions.Default;
            var f = DetectFormat(data);
            int bits = o.SampleType is { } t && t != SampleType.UInt16 ? HeaderInfo(data).BitsPerSample : 0;
            Action<RawImage, int> cb = o.SampleType == null && o.Channels == ImageChannels.AsDecoded ? onBand : (b, y) => onBand(Finish(b, o, bits), y);
            var ct = o.CancellationToken;
            switch (f)
            {
                case ImageFormat.Jpeg:
                    return Jpeg.DecodeBands(data, bandHeight, cb, new JpegDecodeOptions
                    {
                        MaxPixels = o.MaxPixels, AllowTruncated = o.AllowTruncated, CancellationToken = ct, Rgba = o.Channels == ImageChannels.Rgba,
                    });
                case ImageFormat.Png:
                    return Png.DecodeBands(data, bandHeight, cb, new PngDecodeOptions { MaxPixels = o.MaxPixels, CancellationToken = ct });
            }
            var stored = new ImageDecodeOptions { ApplyOrientation = false, MaxPixels = o.MaxPixels, AllowTruncated = o.AllowTruncated, CancellationToken = ct };
            var img = ColorTags.Tag(DecodeCore(data, f, stored), data, f);
            ImageBands.Slice(img, bandHeight, cb);
            return img.Metadata;
        }

        /// <summary>
        /// <see cref="GetInfo(ReadOnlySpan{byte})"/> for a decode with these options: with <see cref="ImageDecodeOptions.ApplyOrientation"/>
        /// off it describes the stored pixels; the requested channels and sample type are reflected.
        /// </summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data, ImageDecodeOptions? options)
        {
            var info = GetInfo(data);
            return options == null ? info : Adjust(info, options);
        }

        /// <inheritdoc cref="GetInfo(ReadOnlySpan{byte}, ImageDecodeOptions?)"/>
        public static ImageInfo GetInfo(string path, ImageDecodeOptions? options)
        {
            var info = GetInfo(path);
            return options == null ? info : Adjust(info, options);
        }

        static ImageInfo Adjust(ImageInfo info, ImageDecodeOptions o)
        {
            if (!o.ApplyOrientation && info.OrientationApplied)
            {
                bool swap = info.Orientation >= 5;
                info = info with
                {
                    Width = swap ? info.Height : info.Width, Height = swap ? info.Width : info.Height,
                    DpiX = swap ? info.DpiY : info.DpiX, DpiY = swap ? info.DpiX : info.DpiY,
                    OrientationApplied = false,
                };
            }
            if (o.Channels != ImageChannels.AsDecoded)
            {
                var c = o.Channels switch { ImageChannels.Gray => ColorModel.Gray, ImageChannels.GrayAlpha => ColorModel.GrayAlpha, ImageChannels.Rgb => ColorModel.Rgb, _ => ColorModel.Rgba };
                info = info with { Color = c, Channels = RawImage.ChannelCount(c) };
            }
            if (o.SampleType is { } t) info = info with { SampleType = t };
            return info;
        }

        static void CheckMaxPixels(ImageInfo info, long maxPixels)
        {
            if (maxPixels > 0 && (long)info.Width * info.Height > maxPixels)
                throw new ImageNotSupportedException($"{info.Format} image of {info.Width}x{info.Height} exceeds ImageDecodeOptions.MaxPixels ({maxPixels})");
        }

        static RawImage DecodeCore(ReadOnlySpan<byte> data, ImageFormat f, ImageDecodeOptions o)
        {
            bool up = o.ApplyOrientation;
            long Mp(long formatDefault) => o.MaxPixels > 0 ? o.MaxPixels : formatDefault;
            var ct = o.CancellationToken;
            switch (f)
            {
                case ImageFormat.Png: return Png.Decode(data, new PngDecodeOptions { ApplyOrientation = up, MaxPixels = Mp(0), CancellationToken = ct });
                case ImageFormat.Jpeg: return Jpeg.Decode(data, new JpegDecodeOptions { ApplyOrientation = up, MaxPixels = Mp(0), AllowTruncated = o.AllowTruncated, CancellationToken = ct, Rgba = o.Channels == ImageChannels.Rgba });
                case ImageFormat.Tiff: return Tiff.Decode(data, new TiffDecodeOptions { ApplyOrientation = up, MaxPixels = Mp(0), CancellationToken = ct });
                case ImageFormat.Webp: return Webp.Decode(data, new WebpDecodeOptions { ApplyOrientation = up, MaxPixels = Mp(new WebpDecodeOptions().MaxPixels), CancellationToken = ct });
                case ImageFormat.Gif: return Gif.Decode(data, new GifDecodeOptions { MaxPixels = Mp(new GifDecodeOptions().MaxPixels) });
                case ImageFormat.Jp2: return Jp2.Decode(data, new Jp2DecodeOptions { ApplyOrientation = up, MaxPixels = Mp(new Jp2DecodeOptions().MaxPixels), AllowTruncated = o.AllowTruncated });
                case ImageFormat.Bmp: return Bmp.Decode(data, new BmpDecodeOptions { MaxPixels = Mp(new BmpDecodeOptions().MaxPixels) });
                case ImageFormat.Tga: return Tga.Decode(data, new TgaDecodeOptions { MaxPixels = Mp(new TgaDecodeOptions().MaxPixels) });
                case ImageFormat.Dds: return Dds.Decode(data, new DdsDecodeOptions { MaxPixels = Mp(new DdsDecodeOptions().MaxPixels) });
                case ImageFormat.Hdr: return Hdr.Decode(data, new HdrDecodeOptions { MaxPixels = Mp(new HdrDecodeOptions().MaxPixels) });
                case ImageFormat.Pnm: return Pnm.Decode(data, new PnmDecodeOptions { MaxPixels = Mp(new PnmDecodeOptions().MaxPixels) });
                case ImageFormat.Qoi: return Qoi.Decode(data, new QoiDecodeOptions { MaxPixels = Mp(new QoiDecodeOptions().MaxPixels) });
                case ImageFormat.Ktx: return Ktx.Decode(data, new KtxDecodeOptions { ApplyOrientation = up, MaxPixels = Mp(new KtxDecodeOptions().MaxPixels) });
                case ImageFormat.Pcx or ImageFormat.Dcx: return Pcx.Decode(data, new PcxDecodeOptions { MaxPixels = Mp(new PcxDecodeOptions().MaxPixels) });
                case ImageFormat.Sgi: return Sgi.Decode(data, new SgiDecodeOptions { MaxPixels = Mp(new SgiDecodeOptions().MaxPixels) });
                case ImageFormat.SunRaster: return SunRaster.Decode(data, new SunRasterDecodeOptions { MaxPixels = Mp(new SunRasterDecodeOptions().MaxPixels) });
                case ImageFormat.Xbm: return Xbm.Decode(data, new XbmDecodeOptions { MaxPixels = Mp(new XbmDecodeOptions().MaxPixels) });
                case ImageFormat.Xpm: return Xpm.Decode(data, new XpmDecodeOptions { MaxPixels = Mp(new XpmDecodeOptions().MaxPixels) });
                case ImageFormat.Wbmp: return Wbmp.Decode(data, new WbmpDecodeOptions { MaxPixels = Mp(new WbmpDecodeOptions().MaxPixels) });
                case ImageFormat.Astc: return Astc.Decode(data, new AstcDecodeOptions { MaxPixels = Mp(new AstcDecodeOptions().MaxPixels) });
                case ImageFormat.Pvr: return Pvr.Decode(data, new PvrDecodeOptions { ApplyOrientation = up, MaxPixels = Mp(new PvrDecodeOptions().MaxPixels) });
                case ImageFormat.Dpx: return Dpx.Decode(data, new DpxDecodeOptions { ApplyOrientation = up, MaxPixels = Mp(new DpxDecodeOptions().MaxPixels) });
                case ImageFormat.Cineon: return Cineon.Decode(data, new CineonDecodeOptions { ApplyOrientation = up, MaxPixels = Mp(new CineonDecodeOptions().MaxPixels) });
                case ImageFormat.JpegLs: return JpegLs.Decode(data, new JpegLsDecodeOptions { ApplyOrientation = up, MaxPixels = Mp(new JpegLsDecodeOptions().MaxPixels) });
                case ImageFormat.Jxl: return Jxl.Decode(data, new JxlDecodeOptions { ApplyOrientation = up, MaxPixels = Mp(new JxlDecodeOptions().MaxPixels) });
                case ImageFormat.JpegXr: return JpegXr.Decode(data, new JpegXrDecodeOptions { ApplyOrientation = up, MaxPixels = Mp(new JpegXrDecodeOptions().MaxPixels) });
                case ImageFormat.Avif: return Avif.Decode(data, new AvifDecodeOptions { ApplyOrientation = up, MaxPixels = Mp(new AvifDecodeOptions().MaxPixels) });
            }
            // no pixel limit of their own: the header decides
            CheckMaxPixels(HeaderInfo(data), o.MaxPixels);
            return f switch
            {
                ImageFormat.Dng => Dng.Decode(data, new DngDecodeOptions { ApplyOrientation = up }),
                ImageFormat.Fits => Fits.Decode(data, new FitsDecodeOptions { ApplyOrientation = up }),
                _ => DecodeCore(data),
            };
        }

        /// <summary>The channel layout and sample type the options ask for; <paramref name="bits"/> is the stored precision of UInt16 samples (0 = 16).</summary>
        static RawImage Finish(RawImage img, ImageDecodeOptions o, int bits)
        {
            if (o.SampleType is { } t && t != img.SampleType)
            {
                if (img.SampleType == SampleType.UInt16 && bits is > 0 and < 16) img = Widen(img, bits);
                img = ImageOps.Retype(img, t);
            }
            if (o.Channels != ImageChannels.AsDecoded) img = ImageOps.ConvertChannels(img, o.Channels);
            return img;
        }

        /// <summary>UInt16 samples holding <paramref name="bits"/> bit values, stretched to the full 16 bit range.</summary>
        static RawImage Widen(RawImage img, int bits)
        {
            var s = img.GetData<ushort>();
            var d = GC.AllocateUninitializedArray<ushort>(s.Length);
            int max = (1 << bits) - 1;
            for (int i = 0; i < s.Length; i++) d[i] = (ushort)((Math.Min((int)s[i], max) * 65535 + max / 2) / max);
            return new RawImage(img.Width, img.Height, img.Color, d, img.Channels, img.Metadata);
        }
    }
}
