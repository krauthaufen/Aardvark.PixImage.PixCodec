using System;
using System.Collections.Generic;
using System.IO;

namespace Aardvark.ImageCodecs
{
    /// <summary>What a decoded animation frame carries.</summary>
    public enum WebpFrameMode
    {
        /// <summary>Every frame is the complete canvas as a viewer would show it (UInt8 Rgba), blending and disposal applied.</summary>
        Composited = 0,
        /// <summary>Every frame is its own decoded rectangle (UInt8 Rgba if the frame has alpha, else Rgb); no compositing.</summary>
        Raw = 1,
    }

    /// <summary>How a frame is drawn onto the canvas.</summary>
    public enum WebpBlend : byte
    {
        /// <summary>Alpha-blend the frame over the canvas.</summary>
        AlphaBlend = 0,
        /// <summary>Replace the frame rectangle, transparency included.</summary>
        NoBlend = 1,
    }

    /// <summary>What happens to a frame's rectangle before the next frame is drawn.</summary>
    public enum WebpDisposal : byte
    {
        /// <summary>Leave the frame in place.</summary>
        None = 0,
        /// <summary>Clear the frame rectangle. Like libwebp and browsers this clears to transparent, not to the ANIM background color.</summary>
        Background = 1,
    }

    /// <summary>How the lossy encoder derives the half-resolution chroma planes.</summary>
    public enum WebpChroma
    {
        /// <summary>The mean of each 2x2 block (what the decoder's interpolation undoes best on average).</summary>
        Average = 0,
        /// <summary>The mean in linear light: saturated edges keep their brightness to the eye, at a cost in PSNR.</summary>
        LinearLight = 1,
        /// <summary>The mean, then luma is corrected per pixel for the chroma the decoder will interpolate there:
        /// sharper colored edges and text. A little slower.</summary>
        Sharp = 2,
    }

    public sealed class WebpDecodeOptions
    {
        /// <summary>
        /// Stops the decode with an <see cref="OperationCanceledException"/>: checked per macroblock row for lossy data,
        /// every 65536 pixels and between the inverse transforms of lossless data (alpha planes included).
        /// </summary>
        public System.Threading.CancellationToken CancellationToken { get; set; }

        /// <summary>Content of the frames returned by the animation API. <see cref="Webp.Decode"/> ignores it.</summary>
        public WebpFrameMode FrameMode { get; set; } = WebpFrameMode.Composited;

        /// <summary>
        /// Rotate / mirror the pixels according to the EXIF orientation (default), so that the image comes back upright; its
        /// metadata then says orientation 1. False returns the pixels as stored with the file's orientation in the metadata.
        /// Composited animation frames are turned upright too, raw frames (sub-rectangles of the canvas) are always as stored.
        /// </summary>
        public bool ApplyOrientation { get; set; } = true;

        /// <summary>
        /// Upper bound for the canvas and for a single frame, in pixels; larger files are rejected with an
        /// <see cref="ImageNotSupportedException"/> that names this option. The default (2^28) admits the largest
        /// lossless image (16384 x 16384), which decodes to 1 GB of Rgba.
        /// Note that a lossless WebP of a few bytes can legitimately describe an image of that size (a single color
        /// costs no bits per pixel), so lower this when decoding untrusted data with a memory budget.
        /// </summary>
        public long MaxPixels { get; set; } = 1L << 28;

        /// <summary>
        /// Lossy frames of half a megapixel and more are decoded on several threads: bitstream parsing, rebuilding
        /// the pixels and the loop filter run as a pipeline (the calling thread plus two pool threads), the color
        /// conversion in up to four bands. Nearly twice as fast, same result. Set to false to stay on the calling
        /// thread. Ignored on single-core machines and in the browser.
        /// </summary>
        public bool Multithreaded { get; set; } = true;

        /// <summary>
        /// ICCP, EXIF and XMP chunks normally count only when the VP8X header flags them (that is what lets
        /// <see cref="Webp.GetInfo"/> answer from the start of the file, and <see cref="Webp.GetInfo"/> always follows
        /// the flags). With this option the decoding functions and <see cref="Webp.GetAnimationInfo"/> /
        /// <see cref="Webp.GetXmp"/> take such chunks wherever they find them, as libwebp based readers do.
        /// </summary>
        public bool AcceptUnflaggedMetadata { get; set; }
    }

    public sealed class WebpEncodeOptions
    {
        /// <summary>
        /// Stops the encode with an <see cref="OperationCanceledException"/>: checked before the pixels are converted, per
        /// row of the colour conversion and per macroblock row of the lossy passes, between the phases of a lossless
        /// encode (alpha planes included) and every 65536 positions of its match search.
        /// </summary>
        public System.Threading.CancellationToken CancellationToken { get; set; }

        /// <summary>Quality 0..100 for lossy output.</summary>
        public int Quality { get; set; } = 75;
        /// <summary>Write lossless (VP8L) instead of lossy (VP8).</summary>
        public bool Lossless { get; set; }

        /// <summary>
        /// Speed versus size, 0 (fastest) .. 6 (smallest), the same scale as libwebp's "method".
        /// Lossless: 0 uses one fixed predictor and short greedy matching; 1 picks predictors per block, adds lazy
        /// matching, the color cache and the entropy image; from 2 on all predictors and the cross-color transform are
        /// searched and matches are chosen by a cost model; higher levels search longer, and 5 / 6 also encode with
        /// alternative transform sets and keep the smallest result.
        /// Lossy: 0 predicts whole macroblocks only; from 1 on 4x4 prediction is tried, with more candidate modes
        /// run through a full encode-and-measure trial the higher the level. The alpha plane of a lossy image is
        /// compressed losslessly at the same level, except that 3 and 4 use the lossless level 2 for it.
        /// </summary>
        public int Method { get; set; } = 4;

        /// <summary>
        /// Lossless only: keep the color of fully transparent pixels. When false their color is replaced by whatever
        /// compresses best (only alpha 0 is guaranteed to come back).
        /// </summary>
        public bool Exact { get; set; } = true;

        /// <summary>
        /// Lossy only: quality of the alpha channel, 0..100. 100 stores it lossless; below that it is reduced to fewer
        /// levels (2 at quality 0; alpha 0 and 255 are always kept) and then stored lossless.
        /// </summary>
        public int AlphaQuality { get; set; } = 100;

        /// <summary>
        /// Lossless only: 100 is lossless; lower values round the color channels of pixels on edges and in texture to
        /// multiples of 2 (80..99), 4 (60..79), 8, 16 or 32 (0..19) before encoding, so no channel is off by more than
        /// half of that. Flat areas, alpha and fully transparent pixels stay exact.
        /// </summary>
        public int NearLossless { get; set; } = 100;

        /// <summary>Lossy only: how chroma is subsampled. Sharp gives the best PSNR on photographs as well as on drawings.</summary>
        public WebpChroma Chroma { get; set; } = WebpChroma.Sharp;

        /// <summary>Test hook: unusual but valid choices in the lossy bitstream syntax.</summary>
        internal Vp8Syntax? Vp8Syntax { get; set; }

        /// <summary>Lossy only: number of token partitions, 1, 2, 4 or 8.</summary>
        public int Partitions { get; set; } = 1;

        /// <summary>
        /// Lossy only: number of segments (1..4) with their own quantizer, chosen by local activity (flat areas are
        /// quantized finer, busy ones coarser). At equal file size, 4 segments against 1 measured on six Kodak
        /// photographs and two drawings: PSNR -0.23 dB, SSIM +0.24 dB, MS-SSIM +0.49 dB (structure in decibels,
        /// -10 log10(1 - x)); the photographs all gain, drawings are a wash. Use 1 for the highest PSNR.
        /// </summary>
        public int Segments { get; set; } = 4;

        /// <summary>Lossy only: loop filter strength 0..63, or -1 to derive it from the quantizer.</summary>
        public int FilterStrength { get; set; } = -1;

        /// <summary>Lossy only: loop filter sharpness 0..7.</summary>
        public int FilterSharpness { get; set; }

        /// <summary>Lossy only: use the simple loop filter (luma only) instead of the normal one.</summary>
        public bool SimpleFilter { get; set; }

        /// <summary>
        /// Write the ICC profile, EXIF block and <see cref="Xmp"/> packet. ICC and EXIF come from
        /// <see cref="RawImage.Metadata"/> (animation: of the first frame).
        /// </summary>
        public bool WriteMetadata { get; set; } = true;

        /// <summary>Optional XMP packet stored in an 'XMP ' chunk.</summary>
        public byte[]? Xmp { get; set; }

        /// <summary>Animation only: number of repetitions, 0 = forever.</summary>
        public int LoopCount { get; set; }

        /// <summary>Animation only: the background color hint of the ANIM chunk.</summary>
        public (byte R, byte G, byte B, byte A) BackgroundColor { get; set; }

        /// <summary>
        /// Animation only: store just the rectangle that changed since the previous frame (and, lossless, mark unchanged
        /// pixels inside it transparent). The decoded animation is the same either way.
        /// </summary>
        public bool FrameDifferencing { get; set; } = true;
    }

    /// <summary>One frame handed to <see cref="Webp.EncodeAnimation(IEnumerable{WebpInputFrame}, Stream, WebpEncodeOptions?)"/>:
    /// the complete picture to show and for how long.</summary>
    public readonly record struct WebpInputFrame(RawImage Image, int DurationMs = 100);

    /// <summary>File level information about a WebP file.</summary>
    public class WebpAnimationInfo
    {
        /// <summary>Canvas size as the composited frames have it (turned upright like them).</summary>
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public int FrameCount { get; internal set; }
        /// <summary>The file is an animation (VP8X animation flag); otherwise it holds one still image.</summary>
        public bool IsAnimated { get; internal set; }
        /// <summary>Number of repetitions, 0 = forever. 1 for a still image.</summary>
        public int LoopCount { get; internal set; }
        /// <summary>Background color of the ANIM chunk. Informational: the canvas starts out transparent, as in browsers.</summary>
        public (byte R, byte G, byte B, byte A) BackgroundColor { get; internal set; }
        /// <summary>Whether <see cref="Webp.Decode"/> returns Rgba for this file.</summary>
        public bool HasAlpha { get; internal set; }
        public byte[]? IccProfile { get; internal set; }
        /// <summary>EXIF block starting at the TIFF header.</summary>
        public byte[]? Exif { get; internal set; }
        /// <summary>XMP packet.</summary>
        public byte[]? Xmp { get; internal set; }
    }

    public sealed class WebpAnimation : WebpAnimationInfo
    {
        public IReadOnlyList<WebpFrame> Frames { get; internal set; } = Array.Empty<WebpFrame>();
    }

    public sealed class WebpFrame
    {
        public int Index { get; internal set; }
        /// <summary>Frame rectangle on the canvas (offsets are always even).</summary>
        public int X { get; internal set; }
        public int Y { get; internal set; }
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        /// <summary>Raw display time in milliseconds. No browser-style clamping of tiny values is applied.</summary>
        public int DurationMs { get; internal set; }
        public WebpBlend Blend { get; internal set; }
        public WebpDisposal Disposal { get; internal set; }
        /// <summary>The frame is stored lossless (VP8L) rather than lossy (VP8).</summary>
        public bool IsLossless { get; internal set; }
        /// <summary>The frame carries transparency (an ALPH chunk, or the alpha hint of a lossless frame).</summary>
        public bool HasAlpha { get; internal set; }
        /// <summary>
        /// <see cref="WebpFrameMode.Composited"/>: the canvas (UInt8 Rgba) after this frame.
        /// <see cref="WebpFrameMode.Raw"/>: the frame rectangle alone, Rgba if <see cref="HasAlpha"/>, else Rgb.
        /// </summary>
        public RawImage Image { get; internal set; } = null!;
    }

    /// <summary>
    /// WebP codec: lossy (VP8 key frames), lossless (VP8L), alpha, animation and metadata, reading and writing.
    /// <para>
    /// Alpha rule: <see cref="Decode"/> returns UInt8 Rgba when the VP8X header has its alpha flag set or when a
    /// lossless image sets its alpha_is_used bit; for an animation also when the first frame has an ALPH chunk or
    /// does not cover the canvas. Otherwise it returns UInt8 Rgb. An ALPH chunk next to a still lossy image is
    /// ignored unless the VP8X alpha flag is set (as libwebp does). <see cref="GetInfo"/> reports the same.
    /// Alpha is straight; the color of fully transparent pixels is returned as stored.
    /// </para>
    /// <para>
    /// Lossy images decode bit exact with libwebp (default settings: fancy upsampling, no dithering). Animations are
    /// composited like libwebp's WebPAnimDecoder: transparent canvas, disposal to transparent, non-premultiplied blending.
    /// </para>
    /// </summary>
    public static class Webp
    {
        /// <summary>Returns whether the data starts with a WebP signature (RIFF....WEBP, needs 12 bytes). Must not throw on short input.</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header)
            => header.Length >= 12 && header.Slice(0, 4).SequenceEqual("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8);

        /// <summary>
        /// Reads header level information without decoding pixel data. Width/Height are the canvas; Channels/Color are
        /// what <see cref="Decode"/> returns; FrameCount is the number of animation frames (1 for a still image);
        /// Orientation, DpiX / DpiY (from the EXIF chunk; WebP itself has no density) and HasIccProfile are what
        /// Decode with default options puts into the metadata.
        /// <para>
        /// The data may be a prefix of the file. A still image is answered from its first chunks, unless the VP8X
        /// header flags an ICC profile or EXIF data whose chunk has not been seen by then (EXIF normally follows the
        /// pixel data): the walk then continues to that chunk. An animation needs the whole file. A prefix that ends
        /// before what is needed gives an <see cref="ImageFormatException"/>.
        /// </para>
        /// </summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data)
        {
            var c = WebpContainer.Parse(data, infoOnly: true);
            bool alpha = WebpDecoder.OutputHasAlpha(c);
            WebpDecoder.ExifInfo(c, data, out int orientation, out double dpiX, out double dpiY);
            return ExifTags.Upright(new ImageInfo(ImageFormat.Webp, c.Width, c.Height, alpha ? 4 : 3, SampleType.UInt8,
                alpha ? ColorModel.Rgba : ColorModel.Rgb, c.Frames.Count)
            {
                BitsPerSample = 8,
                IsAnimated = c.Animated,
                HasIccProfile = c.IccLength >= 0,
                Orientation = orientation, DpiX = dpiX, DpiY = dpiY,
            });
        }

        /// <summary>Decodes a complete WebP file held in memory (first frame of an animation, composited).</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, WebpDecodeOptions? options = null)
        {
            options ??= new WebpDecodeOptions();
            var c = WebpContainer.Parse(data, false, options.AcceptUnflaggedMetadata);
            return WebpDecoder.DecodeFirst(c, data, options);
        }

        /// <summary>Reads the file level properties (and metadata) without decoding pixel data.</summary>
        public static WebpAnimationInfo GetAnimationInfo(ReadOnlySpan<byte> data, WebpDecodeOptions? options = null)
        {
            var c = WebpContainer.Parse(data, false, options?.AcceptUnflaggedMetadata ?? false);
            var info = new WebpAnimationInfo();
            WebpDecoder.FillInfo(c, data, info, options ?? new WebpDecodeOptions());
            return info;
        }

        /// <summary>The XMP packet of the file ('XMP ' chunk), or null.</summary>
        public static byte[]? GetXmp(ReadOnlySpan<byte> data, WebpDecodeOptions? options = null)
        {
            var c = WebpContainer.Parse(data, false, options?.AcceptUnflaggedMetadata ?? false);
            return c.XmpLength < 0 ? null : data.Slice(c.XmpOffset, c.XmpLength).ToArray();
        }

        /// <summary>Decodes all frames (a still image yields one). See <see cref="WebpDecodeOptions.FrameMode"/>.</summary>
        public static WebpAnimation DecodeAnimation(ReadOnlySpan<byte> data, WebpDecodeOptions? options = null)
        {
            options ??= new WebpDecodeOptions();
            var c = WebpContainer.Parse(data, false, options.AcceptUnflaggedMetadata);
            var st = new WebpDecoder(c, options);
            var frames = new List<WebpFrame>(c.Frames.Count);
            while (st.Next(data) is { } f) frames.Add(f);
            var anim = new WebpAnimation { Frames = frames };
            WebpDecoder.FillInfo(c, data, anim, options);
            return anim;
        }

        /// <summary>
        /// Decodes frame by frame, so that a long animation never has to be held in memory at once. Every frame owns its
        /// pixels. The file structure is validated on the first MoveNext.
        /// </summary>
        public static IEnumerable<WebpFrame> EnumerateFrames(ReadOnlyMemory<byte> data, WebpDecodeOptions? options = null)
        {
            var c = WebpContainer.Parse(data.Span);
            var st = new WebpDecoder(c, options ?? new WebpDecodeOptions());
            while (st.Next(data.Span) is { } f) yield return f;
        }

        /// <summary>
        /// Encodes an image (UInt8 Gray, GrayAlpha, Rgb or Rgba) as WebP to the stream (which only needs to be writable,
        /// not seekable). Images with an alpha channel keep it, also when it is fully opaque.
        /// </summary>
        public static void Encode(RawImage image, Stream output, WebpEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(output);
            WebpEncoder.EncodeStill(image, output, options ?? new WebpEncodeOptions());
        }

        /// <summary>Encodes an image as WebP into a byte array.</summary>
        public static byte[] Encode(RawImage image, WebpEncodeOptions? options = null)
        {
            using var ms = new MemoryStream();
            Encode(image, ms, options);
            return ms.ToArray();
        }

        /// <summary>
        /// Encodes an animation. All frames must have the same size (the canvas) and are complete pictures; the encoder
        /// picks frame rectangles, blending and disposal itself. Frames are consumed lazily, one at a time.
        /// </summary>
        public static void EncodeAnimation(IEnumerable<WebpInputFrame> frames, Stream output, WebpEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(frames);
            ArgumentNullException.ThrowIfNull(output);
            WebpEncoder.EncodeAnimation(frames, output, options ?? new WebpEncodeOptions());
        }

        public static byte[] EncodeAnimation(IEnumerable<WebpInputFrame> frames, WebpEncodeOptions? options = null)
        {
            using var ms = new MemoryStream();
            EncodeAnimation(frames, ms, options);
            return ms.ToArray();
        }
    }
}
