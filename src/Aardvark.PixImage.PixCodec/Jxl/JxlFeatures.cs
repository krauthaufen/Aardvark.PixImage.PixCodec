using System;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>Image features of a frame: patches, splines and noise.</summary>
    internal sealed partial class JxlFrameDecoder
    {
        JxlPatches? patches;
        JxlSplines? splines;
        JxlNoise? noise;

        void DecodePatches(JxlBitReader br) => patches = JxlPatches.Read(br, img.ExtraChannels.Length, (long)dims.Width * dims.Height);
        void DecodeSplines(JxlBitReader br) => splines = JxlSplines.Read(br, (long)dims.Width * dims.Height);
        void DecodeNoise(JxlBitReader br) => noise = JxlNoise.Read(br);

        /// <summary>
        /// Patches, then splines, then noise, on the full resolution frame before its colour transform. The noise groups
        /// are groups of the upsampled frame (group_dim, not group_dim times the upsampling; libjxl agrees).
        /// </summary>
        void ApplyFeatures(JxlFrameImage f)
        {
            patches?.Apply(f, state, img);
            if (splines == null && noise == null) return;
            // Only on X, Y, B (K.4.2, K.5.2), never on extra channels. The text leaves grey frames open; libjxl draws on
            // three copies of the grey channel and keeps the first (JxlAuditRenderTests.SplinesAndNoiseOnColourOnly).
            var c = f.NumColour == 3 ? f.Planes : new[] { f.Planes[0], (float[])f.Planes[0].Clone(), (float[])f.Planes[0].Clone() };
            if (splines != null) splines.Draw(c, f.Width, f.Height, baseCorrX, baseCorrB);
            if (noise != null)
                noise.Apply(c[0], c[1], c[2], f.Width, f.Height, dims.GroupDim,
                    ((ulong)(uint)state.VisibleFrames << 32) + (uint)state.NonVisibleFrames, baseCorrX, baseCorrB, parallel);
        }
    }
}
