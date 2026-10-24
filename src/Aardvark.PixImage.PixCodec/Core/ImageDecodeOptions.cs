using System;
using System.Threading;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// Format independent decode settings for <see cref="ImageCodec.Decode(ReadOnlySpan{byte}, ImageDecodeOptions?)"/> and its
    /// overloads (and Aardvark's PixCodec.Load). They map onto the format specific options where the format has them; the
    /// format specific Decode methods offer more.
    /// </summary>
    public sealed class ImageDecodeOptions
    {
        /// <summary>
        /// Rotate / mirror the pixels according to the EXIF / TIFF orientation (default), so that the image comes back upright.
        /// False returns the pixels as stored, which is what calibrated imagery refers to (camera calibrations, GeoTIFF
        /// tiepoints, texture coordinates); the metadata then keeps the file's orientation.
        /// </summary>
        public bool ApplyOrientation { get; set; } = true;

        /// <summary>
        /// Upper bound for the decoded image in pixels; larger files are rejected with an <see cref="ImageNotSupportedException"/>,
        /// before pixel memory is allocated where the format allows (JPEG, PNG, TIFF, WebP and every format with a MaxPixels
        /// option; for the others the header is read first). 0 (the default) keeps each format's own default.
        /// </summary>
        public long MaxPixels { get; set; }

        /// <summary>
        /// Decode what is there when a JPEG or JPEG 2000 file ends early (see <see cref="JpegDecodeOptions.AllowTruncated"/>),
        /// instead of throwing <see cref="ImageFormatException"/>; <see cref="ImageMetadata.Truncated"/> then tells. Other
        /// formats always throw on truncated data.
        /// </summary>
        public bool AllowTruncated { get; set; }

        /// <summary>Channel layout of the result; <see cref="ImageChannels.AsDecoded"/> (the default) leaves it as decoded.</summary>
        public ImageChannels Channels { get; set; }

        /// <summary>
        /// Sample type of the result, converted as <see cref="ImageOps.Retype"/> does (integers rescaled to the full range of
        /// the target and rounded); null (the default) leaves it as decoded.
        /// </summary>
        public SampleType? SampleType { get; set; }

        /// <summary>
        /// Stops the decode with an <see cref="OperationCanceledException"/>. JPEG, PNG, TIFF and WebP check it while decoding
        /// (per band, row block or strip); the other formats only before and after.
        /// </summary>
        public CancellationToken CancellationToken { get; set; }

        internal static readonly ImageDecodeOptions Default = new ImageDecodeOptions();

        internal bool IsDefault => ApplyOrientation && MaxPixels == 0 && !AllowTruncated && Channels == ImageChannels.AsDecoded
            && SampleType == null && !CancellationToken.CanBeCanceled;
    }
}
