using System;
using System.Collections.Generic;
using System.IO;

namespace Aardvark.ImageCodecs
{
    /// <summary>What a decoded animation frame carries.</summary>
    public enum GifFrameMode
    {
        /// <summary>Every frame is the complete logical screen as the viewer would see it (UInt8 Rgba), disposal applied.</summary>
        Composited = 0,
        /// <summary>Every frame is the raw palette-indexed sub-image plus its palette; no compositing is done.</summary>
        Indexed = 1,
    }

    /// <summary>What happens to a frame's rectangle before the next frame is drawn.</summary>
    public enum GifDisposal : byte
    {
        Unspecified = 0,
        /// <summary>Leave the frame in place.</summary>
        None = 1,
        /// <summary>Clear the frame rectangle. Like browsers this clears to transparent, not to the background color.</summary>
        RestoreBackground = 2,
        /// <summary>Restore what was there before the frame was drawn.</summary>
        RestorePrevious = 3,
    }

    public enum GifDither
    {
        None = 0,
        /// <summary>Floyd-Steinberg error diffusion.</summary>
        FloydSteinberg = 1,
    }

    public enum GifPaletteMode
    {
        /// <summary>One palette for the whole file, computed from all frames.</summary>
        Global = 0,
        /// <summary>Each frame gets its own local palette (larger files, better colors when frames differ a lot).</summary>
        PerFrame = 1,
    }

    public sealed class GifDecodeOptions
    {
        /// <summary>Content of the frames returned by the animation API. <see cref="Gif.Decode(ReadOnlySpan{byte}, GifDecodeOptions?)"/> ignores it.</summary>
        public GifFrameMode FrameMode { get; set; } = GifFrameMode.Composited;

        /// <summary>
        /// Hard upper bound for the logical screen and for a single frame, in pixels; anything larger is rejected with
        /// <see cref="ImageNotSupportedException"/>.
        /// </summary>
        public long MaxPixels { get; set; } = 1L << 28;

        /// <summary>
        /// Sizes in GIF headers are not tied to the amount of data: a few bytes can announce a 65535 x 65535 screen. A
        /// screen or frame of up to this many pixels is accepted as announced; a larger one only if the data is long
        /// enough to paint it (at least pixels / 10923 bytes, the best LZW can do), otherwise the file is rejected as
        /// malformed. Images that fill their canvas always pass; raise this for a legitimately huge, mostly empty
        /// canvas, lower it (together with <see cref="MaxPixels"/>) for untrusted input. The default allows 64 megapixels.
        /// </summary>
        public long MaxUnbackedPixels { get; set; } = 1L << 26;

        /// <summary>
        /// Report frame delays the way browsers play them: a stored delay of 0 or 1 (1/100 s) becomes 100 ms in
        /// <see cref="GifFrame.DelayMs"/>, everything else is taken as stored. This is what Chromium was measured to do
        /// (an &lt;img&gt; playing frames with stored delays of 0, 1, 2, 3, 5, 10 and 20: see "playback" in
        /// tests/fixtures/gif/chrome.json). <see cref="GifFrame.DelayCs"/> always holds the stored value. Off by default.
        /// </summary>
        public bool ClampDelays { get; set; }

        /// <summary>
        /// By default a frame whose pixel data stops early - a truncated file, or data that ends properly before all
        /// pixels are there - is kept as far as it goes (<see cref="GifFrame.Truncated"/>), which is what a browser
        /// shows in an &lt;img&gt;. With this option such a frame throws <see cref="ImageFormatException"/> instead,
        /// like Chromium's WebCodecs ImageDecoder does once it knows the data is complete. Frames before it are still
        /// delivered by <see cref="Gif.EnumerateFrames"/>.
        /// </summary>
        public bool RejectIncompleteFrames { get; set; }

        /// <summary>
        /// <see cref="Gif.Decode(ReadOnlySpan{byte}, GifDecodeOptions?)"/> picks Rgb or Rgba from the header alone, so
        /// that <see cref="Gif.GetInfo"/> can tell without decoding; the missing part of an incomplete opaque first
        /// frame is then black. With this option an incomplete first frame is always returned as Rgba with the
        /// missing pixels transparent, exactly what Chromium shows (and what the animation API returns anyway) - at
        /// the price that GetInfo cannot predict the channel count for such files.
        /// </summary>
        public bool IncompleteFirstFrameAsRgba { get; set; }
    }

    public sealed class GifEncodeOptions
    {
        /// <summary>Palette size limit 2..256, including the slot used for transparency.</summary>
        public int MaxColors { get; set; } = 256;

        /// <summary>
        /// Dithering used when the image has more colors than the palette can hold. Images that fit the palette are
        /// always written losslessly, whatever this is set to.
        /// </summary>
        public GifDither Dither { get; set; } = GifDither.FloydSteinberg;

        /// <summary>Pixels with alpha below this value become fully transparent, all others fully opaque.</summary>
        public int AlphaThreshold { get; set; } = 128;

        /// <summary>k-means passes that polish the quantized palette (0 = plain variance-minimizing box split).</summary>
        public int RefineIterations { get; set; } = 3;

        public bool Interlace { get; set; }

        /// <summary>Optional comment extension (ASCII).</summary>
        public string? Comment { get; set; }

        /// <summary>Animation only.</summary>
        public GifPaletteMode PaletteMode { get; set; } = GifPaletteMode.Global;

        /// <summary>
        /// Animation only: store just the bounding rectangle of the pixels that changed and mark unchanged pixels inside
        /// it transparent. The decoded animation is the same either way.
        /// </summary>
        public bool FrameDifferencing { get; set; } = true;

        /// <summary>Animation only: value of the Netscape loop extension, 0 = loop forever, null = do not write one.</summary>
        public int? LoopCount { get; set; } = 0;
    }

    /// <summary>One frame handed to <see cref="Gif.EncodeAnimation(IEnumerable{GifInputFrame}, Stream, GifEncodeOptions?)"/>:
    /// the complete picture to show and for how long.</summary>
    public readonly record struct GifInputFrame(RawImage Image, int DelayMs = 100);

    /// <summary>File level information about a GIF.</summary>
    public class GifAnimationInfo
    {
        /// <summary>Logical screen size.</summary>
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public int FrameCount { get; internal set; }
        /// <summary>Raw Netscape/ANIMEXTS loop value: 0 = forever, null = no loop extension (play once).</summary>
        public int? LoopCount { get; internal set; }
        public int BackgroundIndex { get; internal set; }
        /// <summary>Global palette entry of <see cref="BackgroundIndex"/> as R, G, B; null without a global palette.
        /// Informational: the canvas starts out transparent, as in browsers.</summary>
        public (byte R, byte G, byte B)? BackgroundColor { get; internal set; }
        /// <summary>Global color table as RGB triplets, or null.</summary>
        public byte[]? GlobalPalette { get; internal set; }
        /// <summary>Raw pixel aspect ratio byte of the header (0 = not given).</summary>
        public int PixelAspectRatio { get; internal set; }
        /// <summary>Pixel width / height: (byte + 15) / 64 as the specification defines it, 1 when not given.</summary>
        public double PixelAspect => PixelAspectRatio == 0 ? 1.0 : (PixelAspectRatio + 15) / 64.0;
        /// <summary>ICC profile from an ICCRGBG1012 application extension, or null. Also set as the decoded images' metadata.</summary>
        public byte[]? IccProfile { get; internal set; }
        public bool IsGif89a { get; internal set; }
        public IReadOnlyList<string> Comments { get; internal set; } = Array.Empty<string>();
    }

    public sealed class GifAnimation : GifAnimationInfo
    {
        public IReadOnlyList<GifFrame> Frames { get; internal set; } = Array.Empty<GifFrame>();
    }

    public sealed class GifFrame
    {
        public int Index { get; internal set; }
        /// <summary>Frame rectangle on the logical screen exactly as stored (a later frame may reach beyond the screen).</summary>
        public int X { get; internal set; }
        public int Y { get; internal set; }
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        /// <summary>Delay in 1/100 s exactly as stored.</summary>
        public int DelayCs { get; internal set; }
        /// <summary>Delay in milliseconds; with <see cref="GifDecodeOptions.ClampDelays"/> what a browser would wait.</summary>
        public int DelayMs { get; internal set; }
        public GifDisposal Disposal { get; internal set; }
        public bool Interlaced { get; internal set; }
        /// <summary>Transparent palette index, -1 if the frame has none.</summary>
        public int TransparentIndex { get; internal set; } = -1;
        public bool HasLocalPalette { get; internal set; }
        /// <summary>The pixel data ended before the frame was complete; the missing pixels were left untouched.</summary>
        public bool Truncated { get; internal set; }

        /// <summary>The composited logical screen (UInt8 Rgba) after this frame; null in <see cref="GifFrameMode.Indexed"/>.</summary>
        public RawImage? Image { get; internal set; }
        /// <summary>Width * Height palette indices, top row first (already de-interlaced); only in <see cref="GifFrameMode.Indexed"/>.
        /// Pixels missing from a truncated frame hold <see cref="TransparentIndex"/>, or 0 if there is none.</summary>
        public byte[]? Indices { get; internal set; }
        /// <summary>The frame's active palette (local, else global) as RGB triplets; only in <see cref="GifFrameMode.Indexed"/>.</summary>
        public byte[]? Palette { get; internal set; }
    }

    /// <summary>
    /// GIF codec (GIF87a / GIF89a).
    /// <para>
    /// Decoding follows what Chromium displays (the test references are Chromium's frames): the canvas starts
    /// transparent and pixels outside every frame stay transparent, whatever the background color says. Disposal 2
    /// clears to transparent; disposal 3 on the first frame restores the empty canvas; 4 means 3, 5..7 mean nothing.
    /// A first frame that reaches beyond the logical screen enlarges the screen, later frames are clipped to it.
    /// Palette indices beyond the color table, and files without any color table, decode as opaque black.
    /// </para>
    /// <para>
    /// Pixel data that ends early (truncated file, missing end code) leaves the rest of the frame untouched, except
    /// that an opaque interlaced first frame fills the gaps between the rows it has from the rows above, as browsers
    /// do while an image loads. Anything structurally wrong before the first frame, and invalid LZW codes, throw
    /// <see cref="ImageFormatException"/>. One deliberate difference: a frame whose data properly ends before all
    /// pixels are there is kept as far as it goes, where Chromium drops it.
    /// </para>
    /// </summary>
    public static class Gif
    {
        /// <summary>Returns whether the data starts with a GIF signature (needs at most 16 bytes).</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header)
            => header.Length >= 6 && header[0] == 'G' && header[1] == 'I' && header[2] == 'F' && header[3] == '8'
               && (header[4] == '7' || header[4] == '9') && header[5] == 'a';

        /// <summary>
        /// Reads header level information without decoding pixel data. Width/Height are the logical screen (enlarged by
        /// a first frame that reaches beyond it); Channels/Color are what
        /// <see cref="Decode(ReadOnlySpan{byte}, GifDecodeOptions?)"/> returns with default options. BitsPerSample is 8,
        /// GIF has no resolution or orientation (0).
        /// <para>
        /// FrameCount and IsAnimated (more than one frame) need the WHOLE file: the block structure is walked to the
        /// end. Given only a prefix, this still succeeds as soon as the first image descriptor, its color table and
        /// its LZW code size byte are there, and then counts the frames whose headers lie inside the prefix - so a
        /// prefix can report fewer frames than the file has (and IsAnimated = false for an animation).
        /// <see cref="ImageCodec"/> therefore always hands over the complete GIF.
        /// </para>
        /// <para>
        /// HasIccProfile is true when an ICCRGBG1012 application extension precedes the first image, which is where
        /// writers put it and exactly when Decode attaches the profile to the image's metadata. (A profile that only
        /// shows up after the first image is still reported by <see cref="GetAnimationInfo"/>.)
        /// </para>
        /// </summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data)
        {
            var st = new GifDecoderState(data, null);
            var first = st.Next(data, GifDecoderState.Mode.Skip) ?? throw new ImageFormatException("GIF contains no image");
            bool alpha = st.FirstFrameHasAlpha(first);
            bool icc = st.HasIccProfile;
            while (st.Next(data, GifDecoderState.Mode.Skip) != null) { }
            return new ImageInfo(ImageFormat.Gif, st.Width, st.Height, alpha ? 4 : 3, SampleType.UInt8,
                alpha ? ColorModel.Rgba : ColorModel.Rgb, st.FrameCount)
            {
                BitsPerSample = 8,
                HasIccProfile = icc,
                IsAnimated = st.FrameCount > 1,
            };
        }

        /// <summary>
        /// Decodes the FIRST frame of a GIF held in memory, composited onto the logical screen.
        /// The result is UInt8 Rgba if the first frame has a transparent color index (graphic control extension with the
        /// transparency flag) or its rectangle does not cover the whole logical screen, else UInt8 Rgb.
        /// Transparent pixels are (0,0,0,0).
        /// </summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, GifDecodeOptions? options = null)
        {
            var st = new GifDecoderState(data, options);
            var first = st.Next(data, GifDecoderState.Mode.FirstFrame) ?? throw new ImageFormatException("GIF contains no image");
            return first.Image!;
        }

        /// <summary>
        /// Like <see cref="Decode(ReadOnlySpan{byte}, GifDecodeOptions?)"/>, and also returns what else the file says:
        /// comments, loop count, pixel aspect ratio, frame count, palette, ICC profile.
        /// </summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, out GifAnimationInfo info, GifDecodeOptions? options = null)
        {
            var st = new GifDecoderState(data, options);
            var first = st.Next(data, GifDecoderState.Mode.FirstFrame) ?? throw new ImageFormatException("GIF contains no image");
            while (st.Next(data, GifDecoderState.Mode.Skip) != null) { }
            info = new GifAnimationInfo();
            st.FillInfo(info);
            return first.Image!;
        }

        /// <summary>Reads the file level properties and counts the frames without decoding pixel data.</summary>
        public static GifAnimationInfo GetAnimationInfo(ReadOnlySpan<byte> data)
        {
            var st = new GifDecoderState(data, null);
            while (st.Next(data, GifDecoderState.Mode.Skip) != null) { }
            if (st.FrameCount == 0) throw new ImageFormatException("GIF contains no image");
            var info = new GifAnimationInfo();
            st.FillInfo(info);
            return info;
        }

        /// <summary>Decodes all frames. See <see cref="GifDecodeOptions.FrameMode"/> for what each frame holds.</summary>
        public static GifAnimation DecodeAnimation(ReadOnlySpan<byte> data, GifDecodeOptions? options = null)
        {
            var st = new GifDecoderState(data, options);
            var mode = st.FrameMode;
            var frames = new List<GifFrame>();
            while (st.Next(data, mode) is { } f) frames.Add(f);
            if (frames.Count == 0) throw new ImageFormatException("GIF contains no image");
            var anim = new GifAnimation { Frames = frames };
            st.FillInfo(anim);
            return anim;
        }

        /// <summary>
        /// Decodes frame by frame, so that a long animation never has to be held in memory at once. Every frame owns its
        /// pixels. Use <see cref="GetAnimationInfo"/> for the loop count and frame count. The header is validated on the
        /// first MoveNext.
        /// </summary>
        public static IEnumerable<GifFrame> EnumerateFrames(ReadOnlyMemory<byte> data, GifDecodeOptions? options = null)
        {
            var st = new GifDecoderState(data.Span, options);
            var mode = st.FrameMode;
            int n = 0;
            while (st.Next(data.Span, mode) is { } f) { n++; yield return f; }
            if (n == 0) throw new ImageFormatException("GIF contains no image");
        }

        /// <summary>
        /// Encodes a single image (UInt8 Gray, GrayAlpha, Rgb or Rgba) as GIF to the stream (which only needs to be
        /// writable, not seekable). Lossless when the image has at most <see cref="GifEncodeOptions.MaxColors"/> colors
        /// (transparency counts as one), otherwise quantized.
        /// </summary>
        public static void Encode(RawImage image, Stream output, GifEncodeOptions? options = null)
        {
            var enc = new GifEncoder(output, options ?? new GifEncodeOptions(), animation: false);
            enc.Run(new[] { new GifInputFrame(image, 0) });
        }

        /// <summary>Encodes an image as GIF into a byte array.</summary>
        public static byte[] Encode(RawImage image, GifEncodeOptions? options = null)
        {
            using var ms = new MemoryStream();
            Encode(image, ms, options);
            return ms.ToArray();
        }

        /// <summary>
        /// Encodes an animation. All frames must have the same size (the logical screen) and are complete pictures; the
        /// encoder picks frame rectangles and disposal methods itself. Delays are rounded to 1/100 s.
        /// With <see cref="GifPaletteMode.PerFrame"/> the frames are consumed lazily, one at a time.
        /// </summary>
        public static void EncodeAnimation(IEnumerable<GifInputFrame> frames, Stream output, GifEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(frames);
            var enc = new GifEncoder(output, options ?? new GifEncodeOptions(), animation: true);
            enc.Run(frames);
        }

        public static byte[] EncodeAnimation(IEnumerable<GifInputFrame> frames, GifEncodeOptions? options = null)
        {
            using var ms = new MemoryStream();
            EncodeAnimation(frames, ms, options);
            return ms.ToArray();
        }
    }
}
