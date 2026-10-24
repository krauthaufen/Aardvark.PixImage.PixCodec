namespace Aardvark.ImageCodecs
{
    // TEMPORARY until the ImageOps branch is merged (it brings the real ConvertChannels)
    public static partial class ImageOps
    {
        internal static RawImage ConvertChannels(RawImage image, ImageChannels channels)
            => image.Color == ColorModel.Rgba && channels == ImageChannels.Rgba ? image : throw new System.NotImplementedException("PENDING-MERGE");
    }
}
