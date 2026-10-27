using System;
using System.Collections.Generic;
using System.IO;
using Aardvark.ImageCodecs.JpegXl;

namespace Aardvark.ImageCodecs
{
    /// <summary>Options of <see cref="Jxl.Encode(RawImage, Stream, JxlEncodeOptions?)"/>.</summary>
    public sealed class JxlEncodeOptions
    {
        /// <summary>
        /// Store the samples exactly (modular mode): 8 / 16 bit integer and half / single float images come back bit exact.
        /// Same as <see cref="Distance"/> = 0 or <see cref="Quality"/> = 100.
        /// </summary>
        public bool Lossless { get; set; }

        /// <summary>
        /// Lossy (VarDCT, XYB) target distance as with cjxl -d: 1.0 (default) is visually lossless, 0.5 .. 3 the useful range,
        /// up to 25. 0 means lossless.
        /// </summary>
        public float Distance { get; set; } = 1.0f;

        /// <summary>
        /// When set, overrides <see cref="Distance"/> with cjxl's -q mapping: 100 lossless, 90 = distance 1, 30 = distance 6.4,
        /// 0 = distance 25.
        /// </summary>
        public float? Quality { get; set; }

        /// <summary>
        /// Speed versus size, 1 (fastest) .. 9 (smallest), like cjxl -e. Default 7. Lossless: 1 and 2 use the clamped gradient
        /// predictor with a fixed context tree, 3 the weighted predictor with a fixed tree, from 4 on the context tree is
        /// learned from samples, with more predictors, properties and samples the higher the effort; 8 and 9 add LZ77.
        /// Lossy: 1 .. 3 code everything with 8x8 DCTs (3 adds chroma from luma per 64x64 tile), from 4 on 16x16 and 32x32
        /// regions take the cheapest of the large and the split transforms, 5 adds DCT4x4 / 4x8 / 8x4 for busy blocks and
        /// 7 adds 64x64 transforms and a third inverse Gaborish iteration; 8 and 9 code like 7.
        /// </summary>
        public int Effort { get; set; } = 7;

        /// <summary>Encode groups on several threads (same output). Default true.</summary>
        public bool Multithreaded { get; set; } = true;

        /// <summary>Write the image metadata (EXIF / XMP boxes, ICC profile, orientation) of the image. Default true.</summary>
        public bool KeepMetadata { get; set; } = true;

        /// <summary>
        /// Lossy only: luminance in nits of the brightest value (intensity target of the header); 0 = default (255 for
        /// SDR, 10000 for PQ, 1000 for HLG images). Linear float images with values above 1 should set it.
        /// </summary>
        public float IntensityTarget { get; set; }

        /// <summary>Wrap the codestream in the ISO BMFF container even when no metadata box needs it.</summary>
        public bool ForceContainer { get; set; }

        /// <summary>Lossy only: alpha is coded lossless (default) or, when false, quantized to this many bits first.</summary>
        public int AlphaBits { get; set; }

        /// <summary>Lossy only: test hook for the transform selection (-1 = automatic, otherwise one AC strategy everywhere).</summary>
        internal int ForceStrategy { get; set; } = -1;

        /// <summary>Lossy only: test hooks for the restoration filters (null = automatic).</summary>
        internal bool? Gaborish { get; set; }
        internal int? EpfIterations { get; set; }
        /// <summary>Test hook: writes custom dequantization matrix encodings (after the "not all default" bit).</summary>
        internal Action<JxlBitWriter>? DequantMatrices { get; set; }
        /// <summary>Test hook: the HfMul of each 8 x 8 block (by block index) instead of the adaptive quantization's.</summary>
        internal Func<int, int>? HfMul { get; set; }
        /// <summary>Test hook: the number of HF histogram sets written (num_hf_presets, at most the number of groups).</summary>
        internal int HfPresets { get; set; } = 1;

        /// <summary>The distance that will be used (0 = lossless).</summary>
        public float EffectiveDistance
        {
            get
            {
                if (Lossless) return 0;
                if (Quality is float q)
                {
                    if (q >= 100) return 0;
                    return q >= 30 ? 0.1f + (100 - q) * 0.09f : 53.0f / 3000.0f * q * q - 23.0f / 20.0f * q + 25.0f;
                }
                return Distance;
            }
        }

        internal void Validate()
        {
            if (Effort < 1 || Effort > 9) throw new ArgumentOutOfRangeException(nameof(Effort), "JPEG XL effort is 1..9");
            if (Quality is float q && (!(q >= 0) || q > 100)) throw new ArgumentOutOfRangeException(nameof(Quality), "JPEG XL quality is 0..100");
            if (!(Distance >= 0) || Distance > 25) throw new ArgumentOutOfRangeException(nameof(Distance), "JPEG XL distance is 0..25");
            if (!(IntensityTarget >= 0) || IntensityTarget > 65504) throw new ArgumentOutOfRangeException(nameof(IntensityTarget));
            if (AlphaBits < 0 || AlphaBits > 16) throw new ArgumentOutOfRangeException(nameof(AlphaBits), "alpha bits are 0 (lossless) or 1..16");
            if (ForceStrategy < -1 || ForceStrategy >= JxlAcStrategy.NumStrategies) throw new ArgumentOutOfRangeException(nameof(ForceStrategy));
            if (EpfIterations is int e && (e < 0 || e > 3)) throw new ArgumentOutOfRangeException(nameof(EpfIterations));
        }

        internal JxlEncodeOptions Clone() => (JxlEncodeOptions)MemberwiseClone();
    }

    /// <summary>One frame of <see cref="Jxl.EncodeAnimation(IEnumerable{JxlInputFrame}, Stream, JxlEncodeOptions?, int)"/>.</summary>
    public readonly record struct JxlInputFrame(RawImage Image, int DurationMs = 100, string? Name = null);

    public static partial class Jxl
    {
        /// <summary>
        /// Encodes an image as JPEG XL: lossless (modular) when <see cref="JxlEncodeOptions.Lossless"/> / distance 0, else
        /// lossy VarDCT in XYB. Gray, GrayAlpha, Rgb and Rgba images with UInt8, UInt16, Float16 or Float32 samples (lossy
        /// also takes other types, converted to float). The container is only used when metadata boxes need it.
        /// </summary>
        public static void Encode(RawImage image, Stream output, JxlEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(output);
            var opt = options ?? new JxlEncodeOptions();
            opt.Validate();
            var bytes = JxlEncoder.EncodeStill(image, opt);
            output.Write(bytes, 0, bytes.Length);
        }

        public static byte[] Encode(RawImage image, JxlEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            var opt = options ?? new JxlEncodeOptions();
            opt.Validate();
            return JxlEncoder.EncodeStill(image, opt);
        }

        /// <summary>
        /// Encodes an animation: every frame is a complete picture of the same size, shown for its duration; loopCount 0 =
        /// forever. Frames are coded as a whole (no frame differencing).
        /// </summary>
        public static void EncodeAnimation(IEnumerable<JxlInputFrame> frames, Stream output, JxlEncodeOptions? options = null, int loopCount = 0)
        {
            ArgumentNullException.ThrowIfNull(frames);
            ArgumentNullException.ThrowIfNull(output);
            var opt = options ?? new JxlEncodeOptions();
            opt.Validate();
            if (loopCount < 0) throw new ArgumentOutOfRangeException(nameof(loopCount));
            JxlEncoder.EncodeAnimation(frames, output, opt, loopCount);
        }

        public static byte[] EncodeAnimation(IEnumerable<JxlInputFrame> frames, JxlEncodeOptions? options = null, int loopCount = 0)
        {
            using var ms = new MemoryStream();
            EncodeAnimation(frames, ms, options, loopCount);
            return ms.ToArray();
        }
    }
}
