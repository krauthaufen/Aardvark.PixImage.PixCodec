using System;

namespace Aardvark.ImageCodecs
{
    /// <summary>Hands decoded rows to a band callback (see Jpeg.DecodeBands, Png.DecodeBands, ImageCodec.DecodeBands).</summary>
    internal static class ImageBands
    {
        public static void CheckArguments(int bandHeight, Action<RawImage, int> onBand)
        {
            ArgumentNullException.ThrowIfNull(onBand);
            if (bandHeight <= 0) throw new ArgumentOutOfRangeException(nameof(bandHeight), "bandHeight must be positive");
        }

        /// <summary>
        /// Calls <paramref name="onBand"/> with the first <paramref name="rows"/> rows of <paramref name="band"/>: the band
        /// itself when it is full, else (the last band) a copy of exactly that many rows.
        /// </summary>
        public static void Deliver(RawImage band, int y, int rows, Action<RawImage, int> onBand)
        {
            if (rows == band.Height) { onBand(band, y); return; }
            var part = RawImage.Create(band.Width, rows, band.Color, band.SampleType, band.Channels);
            Array.Copy(band.Data, part.Data, part.Data.Length);
            part.Metadata = band.Metadata;
            onBand(part, y);
        }

        /// <summary>Cuts a decoded image into bands of <paramref name="bandHeight"/> rows (for decoders without a band mode).</summary>
        public static void Slice(RawImage image, int bandHeight, Action<RawImage, int> onBand)
        {
            if (bandHeight >= image.Height) { onBand(image, 0); return; }
            int rowSamples = image.Width * image.Channels;
            var band = RawImage.Create(image.Width, bandHeight, image.Color, image.SampleType, image.Channels);
            band.Metadata = image.Metadata;
            for (int y = 0; y < image.Height; y += bandHeight)
            {
                int rows = Math.Min(bandHeight, image.Height - y);
                Array.Copy(image.Data, (long)y * rowSamples, band.Data, 0, (long)rows * rowSamples);
                Deliver(band, y, rows, onBand);
            }
        }
    }
}
