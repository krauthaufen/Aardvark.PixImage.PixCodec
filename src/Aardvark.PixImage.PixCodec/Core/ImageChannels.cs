namespace Aardvark.ImageCodecs
{
    /// <summary>Channel layout of a decoded image.</summary>
    public enum ImageChannels
    {
        /// <summary>As the format decodes it (Gray, GrayAlpha, Rgb, Rgba or Other).</summary>
        AsDecoded = 0,
        Gray,
        GrayAlpha,
        Rgb,
        /// <summary>Rgb with alpha: an opaque alpha channel is added where the image has none, gray is expanded.</summary>
        Rgba,
    }
}
