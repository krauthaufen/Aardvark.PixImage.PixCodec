using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Aardvark.ImageCodecs.Compression;

namespace Aardvark.ImageCodecs
{
    internal static unsafe class PngEncoder
    {
        const int DataChunkSize = 1 << 18;

        // ------------------------------------------------------------------ entry points

        public static void Encode(RawImage image, Stream output, PngEncodeOptions options)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(output);
            var source = Source.ForImage(image, options, allowTransforms: true);
            WriteStill(source, image.Metadata, output, options);
        }

        public static void EncodeIndexed(RawImage indices, ReadOnlySpan<byte> paletteRgba, Stream output, PngEncodeOptions options)
        {
            ArgumentNullException.ThrowIfNull(indices);
            ArgumentNullException.ThrowIfNull(output);
            var source = Source.ForIndices(indices, paletteRgba);
            WriteStill(source, indices.Metadata, output, options);
        }

        static void WriteStill(Source source, ImageMetadata? metadata, Stream output, PngEncodeOptions options)
        {
            source.Cancel = options.CancellationToken;
            int level = Math.Clamp(options.CompressionLevel, 0, 9);
            WriteHead(source, metadata, output, options, level);
            using (var chunks = new DataStream(output, PngChunks.IDAT, null))
            {
                using (var z = Zlib.CreateCompressor(chunks, level))
                    WriteScanlines(z, source, 0, 0, source.Width, source.Height, options.Interlace, ResolveFilter(options, source, level));
                chunks.Finish();
            }
            WriteChunk(output, PngChunks.IEND, default);
        }

        public static void EncodeAnimation(IEnumerable<PngInputFrame> frames, Stream output, PngEncodeOptions options)
        {
            ArgumentNullException.ThrowIfNull(frames);
            ArgumentNullException.ThrowIfNull(output);
            // The frame count goes into the acTL chunk in front of the first frame, so the list is needed up front.
            var list = frames as IReadOnlyList<PngInputFrame> ?? frames.ToList();
            if (list.Count == 0) throw new ArgumentException("an animation needs at least one frame", nameof(frames));
            if (options.LoopCount < 0) throw new ArgumentOutOfRangeException(nameof(options), "LoopCount must not be negative");
            var first = list[0].Image ?? throw new ArgumentException("frame without an image", nameof(frames));
            foreach (var f in list)
            {
                if (f.Image == null) throw new ArgumentException("frame without an image", nameof(frames));
                if (f.Image.Width != first.Width || f.Image.Height != first.Height || f.Image.SampleType != first.SampleType || f.Image.Color != first.Color)
                    throw new ArgumentException("all frames of an animation must have the same size, sample type and color model", nameof(frames));
                if (f.DelayNumerator is < 0 or > 65535 || f.DelayDenominator is < 0 or > 65535)
                    throw new ArgumentOutOfRangeException(nameof(frames), "frame delay numerator and denominator must be 0..65535");
            }

            int level = Math.Clamp(options.CompressionLevel, 0, 9);
            var sources = Source.ForFrames(list, options);
            foreach (var src in sources) src.Cancel = options.CancellationToken;
            var filter = ResolveFilter(options, sources[0], level);
            WriteHead(sources[0], first.Metadata, output, options, level);

            Span<byte> actl = stackalloc byte[8];
            BinaryPrimitives.WriteInt32BigEndian(actl, list.Count);
            BinaryPrimitives.WriteInt32BigEndian(actl.Slice(4), options.LoopCount);
            WriteChunk(output, PngChunks.acTL, actl);

            var sequence = new Sequence();
            if (options.OptimizeFrames && list.Count > 1)
            {
                WriteOptimizedFrames(list, sources, output, options, filter, level, sequence);
            }
            else
            {
                for (int k = 0; k < list.Count; k++)
                {
                    int x = 0, y = 0, w = first.Width, h = first.Height;
                    if (k > 0 && options.FrameDifferencing)
                    {
                        var (a, b) = (list[k - 1].Image, list[k].Image);
                        ChangedRegion(a.GetBytes(), b.GetBytes(), w, h, b.Channels * RawImage.BytesPerSample(b.SampleType), out x, out y, out w, out h);
                    }
                    WriteFrameControl(output, sequence, x, y, w, h, list[k], PngDisposal.None, PngBlend.Source);

                    // The first frame is the default image (IDAT); later frames go into sequence numbered fdAT chunks.
                    using var chunks = k == 0 ? new DataStream(output, PngChunks.IDAT, null) : new DataStream(output, PngChunks.fdAT, sequence);
                    using (var z = Zlib.CreateCompressor(chunks, level))
                        WriteScanlines(z, sources[k], x, y, w, h, options.Interlace, filter);
                    chunks.Finish();
                }
            }
            WriteChunk(output, PngChunks.IEND, default);
        }

        static void WriteFrameControl(Stream output, Sequence sequence, int x, int y, int w, int h, PngInputFrame frame, PngDisposal disposal, PngBlend blend)
        {
            Span<byte> fctl = stackalloc byte[26];
            BinaryPrimitives.WriteUInt32BigEndian(fctl, sequence.Next++);
            BinaryPrimitives.WriteInt32BigEndian(fctl.Slice(4), w);
            BinaryPrimitives.WriteInt32BigEndian(fctl.Slice(8), h);
            BinaryPrimitives.WriteInt32BigEndian(fctl.Slice(12), x);
            BinaryPrimitives.WriteInt32BigEndian(fctl.Slice(16), y);
            BinaryPrimitives.WriteUInt16BigEndian(fctl.Slice(20), (ushort)frame.DelayNumerator);
            BinaryPrimitives.WriteUInt16BigEndian(fctl.Slice(22), (ushort)frame.DelayDenominator);
            fctl[24] = (byte)disposal;
            fctl[25] = (byte)blend;
            WriteChunk(output, PngChunks.fcTL, fctl);
        }

        // ------------------------------------------------------------------ optimized animation frames

        /// <summary>A frame whose data is compressed but whose disposal is still open: the next frame decides it.</summary>
        sealed class PendingFrame
        {
            public int X, Y, W, H;
            public PngBlend Blend;
            public byte[] Data = Array.Empty<byte>();
            /// <summary>The canvas (Rgba) before this frame was drawn and after it was drawn.</summary>
            public byte[] Before = Array.Empty<byte>(), After = Array.Empty<byte>();
        }

        /// <summary>
        /// For every frame tries the three ways the previous frame can be disposed of (leave, clear, restore) and,
        /// where the pixel format has an alpha channel, both blend modes, compresses each candidate and keeps the
        /// smallest. The canvas is simulated exactly as a decoder composes it, so the animation that comes out is
        /// the same whatever is chosen.
        /// </summary>
        static void WriteOptimizedFrames(IReadOnlyList<PngInputFrame> list, Source[] sources, Stream output, PngEncodeOptions options,
            PngFilterMode filter, int level, Sequence sequence)
        {
            var first = list[0].Image;
            int width = first.Width, height = first.Height;
            int px = 4 * RawImage.BytesPerSample(first.SampleType);
            // "Over" needs transparent pixels in the stored format: only plain gray+alpha / RGBA frames have them.
            bool canBlend = sources[0].ColorType is 4 or 6;

            var pending = new PendingFrame
            {
                W = width, H = height, Blend = PngBlend.Source,
                Data = Deflate(sources[0], 0, 0, width, height, options.Interlace, filter, level),
                Before = new byte[(long)width * height * px], After = ToRgba(first),
            };
            for (int k = 1; k < list.Count; k++)
            {
                byte[] target = ToRgba(list[k].Image);
                PendingFrame? best = null;
                var bestDisposal = PngDisposal.None;
                foreach (var disposal in new[] { PngDisposal.None, PngDisposal.Background, PngDisposal.Previous })
                {
                    // on the first frame "previous" means "background"
                    if (disposal == PngDisposal.Previous && k == 1) continue;
                    byte[] canvas = pending.After;
                    if (disposal == PngDisposal.Previous) canvas = pending.Before;
                    else if (disposal == PngDisposal.Background)
                    {
                        canvas = (byte[])pending.After.Clone();
                        for (int row = 0; row < pending.H; row++)
                            canvas.AsSpan(((pending.Y + row) * width + pending.X) * px, pending.W * px).Clear();
                    }
                    ChangedRegion(canvas, target, width, height, px, out int x, out int y, out int w, out int h);

                    var data = Deflate(sources[k], x, y, w, h, options.Interlace, filter, level);
                    var blend = PngBlend.Source;
                    if (canBlend && OverFrame(list[k].Image, canvas, target, x, y, w, h) is { } sparse)
                    {
                        var sparseSource = Source.ForImage(sparse, options, allowTransforms: false);
                        sparseSource.Cancel = options.CancellationToken;
                        var over = Deflate(sparseSource, 0, 0, w, h, options.Interlace, filter, level);
                        if (over.Length < data.Length) { data = over; blend = PngBlend.Over; }
                    }
                    if (best == null || data.Length < best.Data.Length)
                    {
                        best = new PendingFrame { X = x, Y = y, W = w, H = h, Blend = blend, Data = data, Before = canvas, After = target };
                        bestDisposal = disposal;
                    }
                }
                WritePending(output, sequence, pending, list[k - 1], bestDisposal, first: k == 1);
                pending = best!;
            }
            WritePending(output, sequence, pending, list[^1], PngDisposal.None, first: list.Count == 1);
        }

        static void WritePending(Stream output, Sequence sequence, PendingFrame f, PngInputFrame frame, PngDisposal disposal, bool first)
        {
            WriteFrameControl(output, sequence, f.X, f.Y, f.W, f.H, frame, disposal, f.Blend);
            using var chunks = first ? new DataStream(output, PngChunks.IDAT, null) : new DataStream(output, PngChunks.fdAT, sequence);
            chunks.Write(f.Data);
            chunks.Finish();
        }

        static byte[] Deflate(Source source, int x, int y, int w, int h, bool interlace, PngFilterMode filter, int level)
        {
            using var ms = new MemoryStream();
            using (var z = Zlib.CreateCompressor(ms, level))
                WriteScanlines(z, source, x, y, w, h, interlace, filter);
            return ms.ToArray();
        }

        /// <summary>The image as Rgba samples of its own sample type, as raw bytes: what a decoder's canvas holds.</summary>
        static byte[] ToRgba(RawImage image)
        {
            int n = image.Width * image.Height, ch = image.Channels;
            if (image.SampleType == SampleType.UInt8)
            {
                var src = image.GetData<byte>();
                if (ch == 4) return (byte[])src.Clone();
                var dst = new byte[n * 4];
                for (int i = 0; i < n; i++)
                {
                    dst[i * 4] = src[i * ch];
                    dst[i * 4 + 1] = src[i * ch + (ch == 3 ? 1 : 0)];
                    dst[i * 4 + 2] = src[i * ch + (ch == 3 ? 2 : 0)];
                    dst[i * 4 + 3] = ch == 2 ? src[i * 2 + 1] : (byte)255;
                }
                return dst;
            }
            else
            {
                var src = image.GetData<ushort>();
                var dst = new byte[n * 8];
                var d = MemoryMarshal.Cast<byte, ushort>(dst);
                if (ch == 4) { src.CopyTo(d); return dst; }
                for (int i = 0; i < n; i++)
                {
                    d[i * 4] = src[i * ch];
                    d[i * 4 + 1] = src[i * ch + (ch == 3 ? 1 : 0)];
                    d[i * 4 + 2] = src[i * ch + (ch == 3 ? 2 : 0)];
                    d[i * 4 + 3] = ch == 2 ? src[i * 2 + 1] : (ushort)65535;
                }
                return dst;
            }
        }

        /// <summary>
        /// The region of a gray+alpha / RGBA frame for "over" blending: pixels the canvas already shows become fully
        /// transparent (zero), which compresses well. Null when blending cannot produce the frame - a changed pixel
        /// has to be opaque, or to sit on a fully transparent canvas pixel without being transparent itself.
        /// </summary>
        static RawImage? OverFrame(RawImage image, byte[] canvas, byte[] target, int x, int y, int w, int h)
        {
            int width = image.Width, ch = image.Channels;
            bool wide = image.SampleType == SampleType.UInt16;
            int sampleBytes = wide ? 2 : 1, px = 4 * sampleBytes, srcPx = ch * sampleBytes;
            var sparse = RawImage.Create(w, h, image.Color, image.SampleType);
            var src = image.GetBytes();
            var dst = sparse.GetBytes();
            bool anyKept = false;
            for (int row = 0; row < h; row++)
                for (int col = 0; col < w; col++)
                {
                    int at = (y + row) * width + x + col;
                    var c = canvas.AsSpan(at * px, px);
                    var t = target.AsSpan(at * px, px);
                    if (c.SequenceEqual(t)) { anyKept = true; continue; }
                    // alpha is the last sample: all ones = opaque, all zero = transparent
                    var ta = t.Slice(px - sampleBytes);
                    var ca = c.Slice(px - sampleBytes);
                    bool opaque = ta.IndexOfAnyExcept((byte)0xFF) < 0, transparent = ta.IndexOfAnyExcept((byte)0) < 0;
                    bool onEmpty = ca.IndexOfAnyExcept((byte)0) < 0;
                    if (!opaque && !(onEmpty && !transparent)) return null;
                    src.Slice(at * srcPx, srcPx).CopyTo(dst.Slice((row * w + col) * srcPx));
                }
            return anyKept ? sparse : null; // without unchanged pixels there is nothing to gain over "source"
        }

        static PngFilterMode ResolveFilter(PngEncodeOptions options, Source source, int level)
        {
            if (options.Filter != PngFilterMode.Auto) return options.Filter;
            // Filtering rarely helps indexed and sub-byte data, and never without compression.
            return source.ColorType == 3 || source.BitDepth < 8 || level == 0 ? PngFilterMode.None : PngFilterMode.Adaptive;
        }

        /// <summary>Smallest rectangle containing every pixel that differs between two pictures (1x1 if none does).</summary>
        static void ChangedRegion(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int width, int height, int pixelBytes, out int x, out int y, out int w, out int h)
        {
            int rowBytes = width * pixelBytes;
            int top = 0, bottom = height - 1;
            while (top <= bottom && a.Slice(top * rowBytes, rowBytes).SequenceEqual(b.Slice(top * rowBytes, rowBytes))) top++;
            if (top > bottom) { x = 0; y = 0; w = 1; h = 1; return; }
            while (a.Slice(bottom * rowBytes, rowBytes).SequenceEqual(b.Slice(bottom * rowBytes, rowBytes))) bottom--;
            int left = width, right = -1;
            for (int row = top; row <= bottom; row++)
            {
                var ra = a.Slice(row * rowBytes, rowBytes);
                var rb = b.Slice(row * rowBytes, rowBytes);
                int firstDiff = ra.CommonPrefixLength(rb);
                if (firstDiff == rowBytes) continue;
                int lastDiff = rowBytes - 1;
                while (ra[lastDiff] == rb[lastDiff]) lastDiff--;
                left = Math.Min(left, firstDiff / pixelBytes);
                right = Math.Max(right, lastDiff / pixelBytes);
            }
            x = left; y = top; w = right - left + 1; h = bottom - top + 1;
        }

        // ------------------------------------------------------------------ what gets written

        /// <summary>
        /// The image as it goes into the file: PNG color type and bit depth, palette and transparency chunks, and
        /// access to its rows. Rows are handed out "unpacked": the bytes of the PNG scanline for 8 and 16 bit
        /// samples, one byte per sample below that.
        /// </summary>
        sealed class Source
        {
            enum Kind { Bytes, Swap16, DropAlpha8, DropAlpha16, GrayReduce }

            public int Width, Height, ColorType, BitDepth, Channels;
            public byte[]? Plte, Trns;
            /// <summary>Checked per row (PngEncodeOptions.CancellationToken).</summary>
            public System.Threading.CancellationToken Cancel;
            /// <summary>The palette was given by the caller (rather than derived from the pixels).</summary>
            public bool ExplicitPalette;
            /// <summary>Divisor applied to 8 bit gray samples when writing at a lower bit depth, else 1.</summary>
            public int GrayDivisor = 1;

            Kind _kind;
            byte[]? _bytes;
            ushort[]? _words;
            int _sourceChannels;

            public int UnpackedPixelBytes => BitDepth >= 8 ? Channels * BitDepth >> 3 : 1;

            public static Source ForImage(RawImage image, PngEncodeOptions options, bool allowTransforms)
            {
                int depth = image.SampleType switch
                {
                    SampleType.UInt8 => 8,
                    SampleType.UInt16 => 16,
                    _ => throw new ImageNotSupportedException($"PNG cannot store {image.SampleType} samples"),
                };
                int colorType = image.Color switch
                {
                    ColorModel.Gray => 0,
                    ColorModel.Rgb => 2,
                    ColorModel.GrayAlpha => 4,
                    ColorModel.Rgba => 6,
                    _ => throw new ImageNotSupportedException($"PNG cannot store the color model {image.Color}"),
                };
                var s = new Source
                {
                    Width = image.Width, Height = image.Height, ColorType = colorType, BitDepth = depth,
                    Channels = image.Channels, _sourceChannels = image.Channels,
                };
                if (depth == 8) { s._bytes = image.GetData<byte>(); s._kind = Kind.Bytes; }
                else { s._words = image.GetData<ushort>(); s._kind = Kind.Swap16; }
                if (((long)s.Width * s.Channels * depth + 7) >> 3 > Array.MaxLength - 64)
                    throw new ImageNotSupportedException("image rows are too long for the PNG encoder");

                if (allowTransforms) Transform(new[] { s }, options);
                return s;
            }

            /// <summary>The frames of an animation, which have to share color type, bit depth, palette and transparency.</summary>
            public static Source[] ForFrames(IReadOnlyList<PngInputFrame> frames, PngEncodeOptions options)
            {
                var sources = new Source[frames.Count];
                for (int i = 0; i < sources.Length; i++) sources[i] = ForImage(frames[i].Image, options, allowTransforms: false);
                Transform(sources, options);
                return sources;
            }

            public static Source ForIndices(RawImage indices, ReadOnlySpan<byte> paletteRgba)
            {
                if (indices.SampleType != SampleType.UInt8 || indices.Channels != 1)
                    throw new ArgumentException("palette indices must be a single channel UInt8 image", nameof(indices));
                if (paletteRgba.Length == 0 || paletteRgba.Length % 4 != 0 || paletteRgba.Length > 1024)
                    throw new ArgumentException("the palette must hold 1..256 RGBA entries (4 bytes each)", nameof(paletteRgba));
                int count = paletteRgba.Length / 4;
                var data = indices.GetData<byte>();
                foreach (byte i in data)
                    if (i >= count) throw new ArgumentException($"pixel refers to palette entry {i} but the palette has {count}", nameof(indices));

                var plte = new byte[count * 3];
                int alphaCount = 0;
                for (int i = 0; i < count; i++)
                {
                    paletteRgba.Slice(i * 4, 3).CopyTo(plte.AsSpan(i * 3));
                    if (paletteRgba[i * 4 + 3] != 255) alphaCount = i + 1;
                }
                var trns = new byte[alphaCount];
                for (int i = 0; i < alphaCount; i++) trns[i] = paletteRgba[i * 4 + 3];
                return new Source
                {
                    Width = indices.Width, Height = indices.Height, ColorType = 3, Channels = 1, _sourceChannels = 1,
                    BitDepth = count <= 2 ? 1 : count <= 4 ? 2 : count <= 16 ? 4 : 8,
                    Plte = plte, Trns = alphaCount > 0 ? trns : null, ExplicitPalette = true,
                    _bytes = data, _kind = Kind.Bytes,
                };
            }

            /// <summary>Copies <paramref name="count"/> pixels of row <paramref name="y"/> starting at <paramref name="x0"/>.</summary>
            public void FillRow(int y, int x0, int count, Span<byte> dst)
            {
                int start = checked((y * Width + x0) * _sourceChannels);
                switch (_kind)
                {
                    case Kind.Bytes:
                        _bytes.AsSpan(start, count * _sourceChannels).CopyTo(dst);
                        break;
                    case Kind.Swap16: // PNG samples are big endian
                        BinaryPrimitives.ReverseEndianness(_words.AsSpan(start, count * _sourceChannels), MemoryMarshal.Cast<byte, ushort>(dst));
                        break;
                    case Kind.GrayReduce:
                    {
                        var src = _bytes.AsSpan(start, count);
                        for (int i = 0; i < src.Length; i++) dst[i] = (byte)(src[i] / GrayDivisor);
                        break;
                    }
                    case Kind.DropAlpha8:
                    {
                        var src = _bytes.AsSpan(start, count * _sourceChannels);
                        if (Channels == 1)
                            for (int i = 0; i < count; i++) dst[i] = src[i * 2];
                        else
                            for (int i = 0; i < count; i++) { dst[i * 3] = src[i * 4]; dst[i * 3 + 1] = src[i * 4 + 1]; dst[i * 3 + 2] = src[i * 4 + 2]; }
                        break;
                    }
                    case Kind.DropAlpha16:
                    {
                        var src = _words.AsSpan(start, count * _sourceChannels);
                        var d = MemoryMarshal.Cast<byte, ushort>(dst);
                        for (int i = 0, o = 0; i < src.Length; i += _sourceChannels)
                            for (int c = 0; c < Channels; c++) d[o++] = BinaryPrimitives.ReverseEndianness(src[i + c]);
                        break;
                    }
                }
            }

            /// <summary>
            /// The lossless re-expressions the options ask for, decided over all images together: an animation's
            /// frames share one IHDR, palette and transparency chunk, so a transform applies to all of them or none.
            /// </summary>
            static void Transform(Source[] sources, PngEncodeOptions options)
            {
                var first = sources[0];
                if (options.AutoPalette && first.BitDepth == 8 && first.Channels >= 3 && TryPalette(sources)) return;
                if (options.ColorKeyTransparency && first.ColorType is 4 or 6 && TryColorKey(sources)) return;
                if (options.ReduceGrayDepth && first.BitDepth == 8 && first.ColorType == 0) TryReduceGray(sources);
            }

            /// <summary>8 bit gray at 1, 2 or 4 bits when every sample is a multiple of 255, 85 or 17.</summary>
            static void TryReduceGray(Source[] sources)
            {
                bool fits1 = true, fits2 = true;
                foreach (var s in sources)
                    foreach (byte v in s._bytes!)
                    {
                        if (v % 17 != 0) return;
                        if (v % 85 != 0) { fits2 = false; fits1 = false; }
                        else if (v != 0 && v != 255) fits1 = false;
                    }
                foreach (var s in sources)
                {
                    s.BitDepth = fits1 ? 1 : fits2 ? 2 : 4;
                    s.GrayDivisor = fits1 ? 255 : fits2 ? 85 : 17;
                    s._kind = Kind.GrayReduce;
                }
            }

            /// <summary>
            /// Drops the alpha channel in favor of a tRNS color key when alpha is binary, the transparent pixels share
            /// one color and no opaque pixel has it. Without any transparent pixel nothing changes: a key would have
            /// to be a color the image does not use, and dropping alpha silently would change the decoded color model.
            /// </summary>
            static bool TryColorKey(Source[] sources)
            {
                int ch = sources[0]._sourceChannels, color = ch - 1;
                Span<int> key = stackalloc int[3];
                bool haveKey = false;
                int max = sources[0].BitDepth == 16 ? 65535 : 255;
                for (int pass = 0; pass < 2; pass++)
                {
                    foreach (var s in sources)
                    {
                        long samples = (long)s.Width * s.Height * ch;
                        for (long i = 0; i < samples; i += ch)
                        {
                            int alpha = s.Sample(i + color);
                            if (alpha != 0 && alpha != max) return false;
                            bool isKey = haveKey;
                            for (int c = 0; c < color && isKey; c++) isKey = s.Sample(i + c) == key[c];
                            if (pass == 0)
                            {
                                if (alpha != 0) continue;
                                if (!haveKey) { for (int c = 0; c < color; c++) key[c] = s.Sample(i + c); haveKey = true; }
                                else if (!isKey) return false;      // two different transparent colors
                            }
                            else if (alpha != 0 && isKey) return false; // an opaque pixel of the key color would vanish
                        }
                    }
                    if (!haveKey) return false;
                }
                var trns = new byte[color * 2];
                for (int c = 0; c < color; c++) BinaryPrimitives.WriteUInt16BigEndian(trns.AsSpan(c * 2), (ushort)key[c]);
                foreach (var s in sources)
                {
                    s.Trns = trns;
                    s.Channels = color;
                    s.ColorType = color == 1 ? 0 : 2;
                    s._kind = s.BitDepth == 16 ? Kind.DropAlpha16 : Kind.DropAlpha8;
                }
                return true;
            }

            int Sample(long i) => _bytes != null ? _bytes[i] : _words![i];

            /// <summary>Re-expresses 8 bit Rgb/Rgba images as one palette of at most 256 colors plus indices.</summary>
            static bool TryPalette(Source[] sources)
            {
                int channels = sources[0]._sourceChannels;
                long pixelCount = (long)sources[0].Width * sources[0].Height;
                if (pixelCount > Array.MaxLength) return false;

                // Open addressing hash set of the colors seen so far (key = RGBA, value = palette index).
                const int TableSize = 1024;
                Span<uint> keys = stackalloc uint[TableSize];
                Span<short> slots = stackalloc short[TableSize];
                slots.Fill(-1);
                Span<uint> colors = stackalloc uint[256];
                int count = 0;
                var allIndices = new byte[sources.Length][];

                for (int f = 0; f < sources.Length; f++)
                {
                    var src = sources[f]._bytes!;
                    var indices = allIndices[f] = new byte[pixelCount];
                    uint lastKey = 0;
                    int lastIndex = -1;
                    for (int i = 0, s = 0; i < indices.Length; i++, s += channels)
                    {
                        uint key = (uint)(src[s] | src[s + 1] << 8 | src[s + 2] << 16) | (channels == 4 ? (uint)src[s + 3] << 24 : 0xFF000000u);
                        if (key != lastKey || lastIndex < 0)
                        {
                            uint slot = (key * 0x9E3779B1u) >> 22;
                            while (true)
                            {
                                if (slots[(int)slot] < 0)
                                {
                                    if (count == 256) return false;
                                    keys[(int)slot] = key;
                                    slots[(int)slot] = (short)count;
                                    colors[count] = key;
                                    lastIndex = count++;
                                    break;
                                }
                                if (keys[(int)slot] == key) { lastIndex = slots[(int)slot]; break; }
                                slot = (slot + 1) & (TableSize - 1);
                            }
                            lastKey = key;
                        }
                        indices[i] = (byte)lastIndex;
                    }
                }

                // tRNS lists alpha for the first entries only, so translucent colors go to the front.
                Span<byte> remap = stackalloc byte[256];
                var rgb = new byte[count * 3];
                int next = 0, translucent = 0;
                for (int pass = 0; pass < 2; pass++)
                {
                    for (int c = 0; c < count; c++)
                    {
                        bool opaque = colors[c] >> 24 == 255;
                        if (opaque != (pass == 1)) continue;
                        remap[c] = (byte)next;
                        rgb[next * 3] = (byte)colors[c];
                        rgb[next * 3 + 1] = (byte)(colors[c] >> 8);
                        rgb[next * 3 + 2] = (byte)(colors[c] >> 16);
                        next++;
                    }
                    if (pass == 0) translucent = next;
                }
                // Rgba input always gets a tRNS chunk, so that it decodes as Rgba again.
                int alphaCount = channels == 4 ? Math.Max(translucent, 1) : 0;
                var alpha = new byte[alphaCount];
                for (int c = 0; c < count; c++)
                    if (remap[c] < alphaCount) alpha[remap[c]] = (byte)(colors[c] >> 24);

                for (int f = 0; f < sources.Length; f++)
                {
                    var indices = allIndices[f];
                    for (int i = 0; i < indices.Length; i++) indices[i] = remap[indices[i]];
                    var s = sources[f];
                    s.Plte = rgb;
                    s.Trns = alphaCount > 0 ? alpha : null;
                    s.ColorType = 3;
                    s.Channels = 1;
                    s.BitDepth = count <= 2 ? 1 : count <= 4 ? 2 : count <= 16 ? 4 : 8;
                    s._bytes = indices;
                    s._sourceChannels = 1;
                    s._kind = Kind.Bytes;
                }
                return true;
            }
        }

        // ------------------------------------------------------------------ pixel data

        /// <summary>
        /// Filters the region (x0, y0, w, h) of the source and writes the scanlines to the compressor; with
        /// <paramref name="interlace"/> as the seven Adam7 passes, each of which is filtered as an image of its own.
        /// </summary>
        static void WriteScanlines(Stream z, Source source, int x0, int y0, int w, int h, bool interlace, PngFilterMode mode)
        {
            int depth = source.BitDepth, channels = source.Channels;
            int unpacked = source.UnpackedPixelBytes;
            int rowBytes = (int)(((long)w * channels * depth + 7) >> 3);
            int bpp = Math.Max(1, channels * depth >> 3);

            // cur/prev hold the raw rows behind bpp zero bytes, so "the pixel to the left" is always readable.
            // Each candidate holds a filter type byte followed by the filtered row.
            int candidateCount = mode == PngFilterMode.Adaptive ? 5 : 1;
            int stride = 1 + rowBytes;
            var rows = new byte[2 * (bpp + rowBytes)];
            var candidates = new byte[candidateCount * stride];
            // Sub-byte samples are packed after, and interlaced rows are picked from, a complete unpacked row.
            byte[]? full = interlace || depth < 8 ? new byte[w * unpacked] : null;
            byte[]? picked = interlace && depth < 8 ? new byte[w] : null;

            fixed (byte* rowsP = rows, cand = candidates)
            {
                byte* cur = rowsP + bpp, prev = rowsP + bpp + rowBytes + bpp;
                for (int pass = 0; pass < (interlace ? 7 : 1); pass++)
                {
                    int px0 = 0, py0 = 0, dx = 1, dy = 1;
                    if (interlace) { px0 = PngDecoder.PassX0[pass]; py0 = PngDecoder.PassY0[pass]; dx = PngDecoder.PassDx[pass]; dy = PngDecoder.PassDy[pass]; }
                    int pw = (w - px0 + dx - 1) / dx, ph = (h - py0 + dy - 1) / dy;
                    if (pw <= 0 || ph <= 0) continue;
                    int n = (int)(((long)pw * channels * depth + 7) >> 3);
                    new Span<byte>(prev, n).Clear(); // every pass starts without a row above

                    for (int r = 0; r < ph; r++)
                    {
                        source.Cancel.ThrowIfCancellationRequested();
                        int y = y0 + py0 + r * dy;
                        var curSpan = new Span<byte>(cur, n);
                        if (full == null) source.FillRow(y, x0, w, curSpan);
                        else
                        {
                            source.FillRow(y, x0, w, full);
                            ReadOnlySpan<byte> samples = full;
                            if (interlace)
                            {
                                var target = depth < 8 ? picked.AsSpan(0, pw) : curSpan;
                                for (int i = 0, s = px0 * unpacked; i < pw; i++, s += dx * unpacked)
                                    full.AsSpan(s, unpacked).CopyTo(target.Slice(i * unpacked));
                                samples = target;
                            }
                            if (depth < 8)
                            {
                                curSpan.Clear();
                                for (int i = 0; i < pw; i++)
                                    cur[i * depth >> 3] |= (byte)(samples[i] << (8 - depth - (i * depth & 7)));
                            }
                        }

                        byte* best = cand;
                        if (mode == PngFilterMode.Adaptive)
                        {
                            long bestSum = PngFilters.SumAbs(cur, n);
                            int bestType = 0;
                            long s;
                            if ((s = PngFilters.FilterSub(cur, cand + stride + 1, n, bpp)) < bestSum) { bestSum = s; bestType = 1; }
                            if ((s = PngFilters.FilterUp(cur, prev, cand + 2 * stride + 1, n)) < bestSum) { bestSum = s; bestType = 2; }
                            if ((s = PngFilters.FilterAverage(cur, prev, cand + 3 * stride + 1, n, bpp)) < bestSum) { bestSum = s; bestType = 3; }
                            if ((s = PngFilters.FilterPaeth(cur, prev, cand + 4 * stride + 1, n, bpp)) < bestSum) { bestSum = s; bestType = 4; }
                            best = cand + bestType * stride;
                            if (bestType == 0) Buffer.MemoryCopy(cur, best + 1, n, n);
                            *best = (byte)bestType;
                        }
                        else
                        {
                            switch (mode)
                            {
                                case PngFilterMode.Sub: *best = 1; PngFilters.FilterSub(cur, best + 1, n, bpp); break;
                                case PngFilterMode.Up: *best = 2; PngFilters.FilterUp(cur, prev, best + 1, n); break;
                                case PngFilterMode.Average: *best = 3; PngFilters.FilterAverage(cur, prev, best + 1, n, bpp); break;
                                case PngFilterMode.Paeth: *best = 4; PngFilters.FilterPaeth(cur, prev, best + 1, n, bpp); break;
                                default: *best = 0; Buffer.MemoryCopy(cur, best + 1, n, n); break;
                            }
                        }
                        z.Write(new ReadOnlySpan<byte>(best, 1 + n));

                        byte* t = cur; cur = prev; prev = t;
                    }
                }
            }
        }

        // ------------------------------------------------------------------ chunks

        static void WriteChunk(Stream output, uint type, ReadOnlySpan<byte> body)
        {
            Span<byte> head = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(head, (uint)body.Length);
            BinaryPrimitives.WriteUInt32BigEndian(head.Slice(4), type);
            output.Write(head);
            output.Write(body);
            uint crc = Zlib.Crc32(body, Zlib.Crc32(head.Slice(4)));
            BinaryPrimitives.WriteUInt32BigEndian(head, crc);
            output.Write(head.Slice(0, 4));
        }

        /// <summary>Signature, IHDR and everything that precedes the pixel data.</summary>
        static void WriteHead(Source source, ImageMetadata? metadata, Stream output, PngEncodeOptions options, int level)
        {
            output.Write(PngChunks.Signature);

            Span<byte> ihdr = stackalloc byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdr, source.Width);
            BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4), source.Height);
            ihdr[8] = (byte)source.BitDepth;
            ihdr[9] = (byte)source.ColorType;
            ihdr[10] = 0; ihdr[11] = 0;
            ihdr[12] = options.Interlace ? (byte)1 : (byte)0;
            WriteChunk(output, PngChunks.IHDR, ihdr);

            var meta = options.WriteMetadata ? metadata : null;
            var png = options.Metadata;
            Span<byte> b = stackalloc byte[32];

            // Color space chunks have to precede PLTE.
            bool hasIcc = meta?.IccProfile is { Length: > 0 };
            if (png?.SrgbIntent is { } intent && !hasIcc) // sRGB and iCCP exclude each other; the profile wins
            {
                b[0] = (byte)intent;
                WriteChunk(output, PngChunks.sRGB, b.Slice(0, 1));
            }
            // linear data (e.g. from ImageOps.ToLinear) is marked with gamma 1.0
            double gamma = meta == null ? 0 : meta.Gamma > 0 ? meta.Gamma : meta.Transfer == TransferFunction.Linear && !hasIcc ? 1.0 : 0;
            if (meta != null && gamma > 0 && !double.IsInfinity(gamma))
            {
                BinaryPrimitives.WriteUInt32BigEndian(b, (uint)Math.Clamp(Math.Round(gamma * 100000.0), 1, int.MaxValue));
                WriteChunk(output, PngChunks.gAMA, b.Slice(0, 4));
            }
            if (png?.Chromaticities is { } c)
            {
                ReadOnlySpan<double> v = stackalloc double[] { c.WhiteX, c.WhiteY, c.RedX, c.RedY, c.GreenX, c.GreenY, c.BlueX, c.BlueY };
                for (int i = 0; i < 8; i++)
                {
                    if (!(v[i] >= 0 && v[i] <= 21474.83647)) throw new ArgumentOutOfRangeException(nameof(options), "chromaticity out of range");
                    BinaryPrimitives.WriteUInt32BigEndian(b.Slice(i * 4), (uint)Math.Round(v[i] * 100000.0));
                }
                WriteChunk(output, PngChunks.cHRM, b);
            }
            if (hasIcc)
            {
                var name = "ICC Profile"u8;
                var z = Zlib.Compress(meta!.IccProfile, Math.Max(level, 1));
                var body = new byte[name.Length + 2 + z.Length];
                name.CopyTo(body);
                z.CopyTo(body.AsSpan(name.Length + 2)); // name, 0 terminator, compression method 0
                WriteChunk(output, PngChunks.iCCP, body);
            }
            if (png?.SignificantBits is { } bits)
            {
                // The entries follow the stored color type, which may have lost its alpha channel on the way.
                int needed = PngChunks.SignificantBitsCount(source.ColorType);
                if (bits.Length == needed || bits.Length == needed + 1)
                {
                    int maxBits = source.ColorType == 3 ? 8 : source.BitDepth;
                    for (int i = 0; i < needed; i++) b[i] = (byte)Math.Clamp((int)bits[i], 1, maxBits);
                    WriteChunk(output, PngChunks.sBIT, b.Slice(0, needed));
                }
            }

            if (png?.CodingIndependentCodePoints is { } cicp)
            {
                b[0] = cicp.ColorPrimaries; b[1] = cicp.TransferFunction; b[2] = cicp.MatrixCoefficients; b[3] = cicp.FullRange ? (byte)1 : (byte)0;
                WriteChunk(output, PngChunks.cICP, b.Slice(0, 4));
            }
            if (png?.MasteringDisplay is { } md)
            {
                ReadOnlySpan<double> xy = stackalloc double[] { md.RedX, md.RedY, md.GreenX, md.GreenY, md.BlueX, md.BlueY, md.WhiteX, md.WhiteY };
                for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt16BigEndian(b.Slice(i * 2), (ushort)Scaled(xy[i], 0.00002, ushort.MaxValue, "mastering display chromaticity"));
                BinaryPrimitives.WriteUInt32BigEndian(b.Slice(16), Scaled(md.MaxLuminance, 0.0001, uint.MaxValue, "mastering display luminance"));
                BinaryPrimitives.WriteUInt32BigEndian(b.Slice(20), Scaled(md.MinLuminance, 0.0001, uint.MaxValue, "mastering display luminance"));
                WriteChunk(output, PngChunks.mDCV, b.Slice(0, 24));
            }
            if (png?.ContentLightLevel is { } cll)
            {
                BinaryPrimitives.WriteUInt32BigEndian(b, Scaled(cll.MaxContentLightLevel, 0.0001, uint.MaxValue, "content light level"));
                BinaryPrimitives.WriteUInt32BigEndian(b.Slice(4), Scaled(cll.MaxFrameAverageLightLevel, 0.0001, uint.MaxValue, "content light level"));
                WriteChunk(output, PngChunks.cLLI, b.Slice(0, 8));
            }

            if (source.Plte != null) WriteChunk(output, PngChunks.PLTE, source.Plte);
            if (source.Trns != null) WriteChunk(output, PngChunks.tRNS, source.Trns);

            if (png?.Histogram is { } hist && source.ExplicitPalette && hist.Length == source.Plte!.Length / 3)
            {
                var body = new byte[hist.Length * 2];
                for (int i = 0; i < hist.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(i * 2), hist[i]);
                WriteChunk(output, PngChunks.hIST, body);
            }
            if (png?.Background is { } bg) WriteBackground(output, source, bg);
            if (png != null) WriteRareChunks(output, png);
            if (meta != null)
            {
                double dx = meta.DpiX > 0 ? meta.DpiX : meta.DpiY, dy = meta.DpiY > 0 ? meta.DpiY : meta.DpiX;
                if (dx > 0 && dy > 0 && !double.IsInfinity(dx) && !double.IsInfinity(dy))
                {
                    BinaryPrimitives.WriteUInt32BigEndian(b, (uint)Math.Clamp(Math.Round(dx / 0.0254), 1, int.MaxValue));
                    BinaryPrimitives.WriteUInt32BigEndian(b.Slice(4), (uint)Math.Clamp(Math.Round(dy / 0.0254), 1, int.MaxValue));
                    b[8] = 1; // unit: meter
                    WriteChunk(output, PngChunks.pHYs, b.Slice(0, 9));
                }
            }
            if (png?.LastModified is { } time)
            {
                var utc = time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : time;
                BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)utc.Year);
                b[2] = (byte)utc.Month; b[3] = (byte)utc.Day; b[4] = (byte)utc.Hour; b[5] = (byte)utc.Minute; b[6] = (byte)utc.Second;
                WriteChunk(output, PngChunks.tIME, b.Slice(0, 7));
            }
            if (png != null)
                foreach (var entry in png.Text) WriteText(output, entry, level);
            if (ExifTags.ForWriting(meta) is { } exif) WriteChunk(output, PngChunks.eXIf, exif);
            if (ExifTags.XmpForWriting(meta) is { } xmp && !(png?.Text.Exists(t => t.Keyword == "XML:com.adobe.xmp") ?? false))
                WriteText(output, new PngTextEntry("XML:com.adobe.xmp", Encoding.UTF8.GetString(xmp).Replace("\0", "")) { International = true }, level);
        }

        /// <summary>value / unit as an unsigned integer, rejecting what the field cannot hold.</summary>
        static uint Scaled(double value, double unit, uint max, string what)
        {
            double v = Math.Round(value / unit);
            if (!(v >= 0 && v <= max)) throw new ArgumentOutOfRangeException(nameof(PngEncodeOptions.Metadata), $"{what} out of range");
            return (uint)v;
        }

        static string Float(double value, string what)
        {
            if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(PngEncodeOptions.Metadata), $"{what} must be finite");
            return value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        static void CheckName(string? name, string what)
        {
            bool ok = name != null && name.Length is >= 1 and <= 79 && name[0] != ' ' && name[^1] != ' ' && !name.Contains("  ");
            if (ok) foreach (char ch in name!) ok &= ch is >= ' ' and <= '~' or >= '\u00A1' and <= '\u00FF';
            if (!ok) throw new ArgumentException($"invalid PNG {what} '{name}' (1..79 printable Latin-1 characters, no leading, trailing or double spaces)");
        }

        /// <summary>sPLT, oFFs, pCAL, sCAL and sTER: all of them only have to precede the pixel data.</summary>
        static void WriteRareChunks(Stream output, PngMetadata png)
        {
            Span<byte> b = stackalloc byte[16];
            foreach (var palette in png.SuggestedPalettes)
            {
                CheckName(palette.Name, "suggested palette name");
                if (palette.SampleDepth != 8 && palette.SampleDepth != 16) throw new ArgumentException("suggested palette sample depth must be 8 or 16");
                using var body = new MemoryStream();
                body.Write(Encoding.Latin1.GetBytes(palette.Name));
                body.WriteByte(0);
                body.WriteByte((byte)palette.SampleDepth);
                foreach (var e in palette.Entries ?? Array.Empty<PngSuggestedPaletteEntry>())
                {
                    if (palette.SampleDepth == 8)
                    {
                        if ((e.R | e.G | e.B | e.A) > 255) throw new ArgumentException("suggested palette entry exceeds its 8 bit sample depth");
                        b[0] = (byte)e.R; b[1] = (byte)e.G; b[2] = (byte)e.B; b[3] = (byte)e.A;
                        BinaryPrimitives.WriteUInt16BigEndian(b.Slice(4), e.Frequency);
                        body.Write(b.Slice(0, 6));
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt16BigEndian(b, e.R);
                        BinaryPrimitives.WriteUInt16BigEndian(b.Slice(2), e.G);
                        BinaryPrimitives.WriteUInt16BigEndian(b.Slice(4), e.B);
                        BinaryPrimitives.WriteUInt16BigEndian(b.Slice(6), e.A);
                        BinaryPrimitives.WriteUInt16BigEndian(b.Slice(8), e.Frequency);
                        body.Write(b.Slice(0, 10));
                    }
                }
                WriteChunk(output, PngChunks.sPLT, body.GetBuffer().AsSpan(0, (int)body.Length));
            }
            if (png.Offset is { } offset)
            {
                BinaryPrimitives.WriteInt32BigEndian(b, offset.X);
                BinaryPrimitives.WriteInt32BigEndian(b.Slice(4), offset.Y);
                b[8] = (byte)offset.Unit;
                if (b[8] > 1) throw new ArgumentOutOfRangeException(nameof(PngEncodeOptions.Metadata), "invalid offset unit");
                WriteChunk(output, PngChunks.oFFs, b.Slice(0, 9));
            }
            if (png.PixelCalibration is { } cal)
            {
                CheckName(cal.Name, "calibration name");
                var parameters = cal.Parameters ?? Array.Empty<double>();
                if ((byte)cal.Equation > 3 || parameters.Length != PngChunks.CalibrationParameterCount(cal.Equation))
                    throw new ArgumentException($"pixel calibration equation {cal.Equation} takes {PngChunks.CalibrationParameterCount(cal.Equation)} parameters");
                if (cal.X0 == cal.X1) throw new ArgumentException("pixel calibration X0 and X1 must differ");
                string unit = cal.Unit ?? "";
                foreach (char ch in unit) if (ch is '\0' or > '\u00FF') throw new ArgumentException("invalid pixel calibration unit");
                using var body = new MemoryStream();
                body.Write(Encoding.Latin1.GetBytes(cal.Name));
                body.WriteByte(0);
                BinaryPrimitives.WriteInt32BigEndian(b, cal.X0);
                BinaryPrimitives.WriteInt32BigEndian(b.Slice(4), cal.X1);
                b[8] = (byte)cal.Equation; b[9] = (byte)parameters.Length;
                body.Write(b.Slice(0, 10));
                body.Write(Encoding.Latin1.GetBytes(unit));
                for (int i = 0; i < parameters.Length; i++)
                {
                    body.WriteByte(0); // separates the unit and the parameters; the last parameter has no terminator
                    body.Write(Encoding.ASCII.GetBytes(Float(parameters[i], "pixel calibration parameter")));
                }
                WriteChunk(output, PngChunks.pCAL, body.GetBuffer().AsSpan(0, (int)body.Length));
            }
            if (png.PhysicalScale is { } scale)
            {
                if (scale.Unit is not (PngScaleUnit.Meter or PngScaleUnit.Radian) || !(scale.PixelWidth > 0) || !(scale.PixelHeight > 0))
                    throw new ArgumentOutOfRangeException(nameof(PngEncodeOptions.Metadata), "physical scale needs a unit and positive pixel sizes");
                var text = Encoding.ASCII.GetBytes(Float(scale.PixelWidth, "physical scale") + "\0" + Float(scale.PixelHeight, "physical scale"));
                var body = new byte[1 + text.Length];
                body[0] = (byte)scale.Unit;
                text.CopyTo(body, 1);
                WriteChunk(output, PngChunks.sCAL, body);
            }
            if (png.StereoMode is { } stereo)
            {
                b[0] = (byte)stereo;
                if (b[0] > 1) throw new ArgumentOutOfRangeException(nameof(PngEncodeOptions.Metadata), "invalid stereo mode");
                WriteChunk(output, PngChunks.sTER, b.Slice(0, 1));
            }
        }

        static void WriteBackground(Stream output, Source source, PngBackground bg)
        {
            Span<byte> b = stackalloc byte[6];
            int max = (1 << source.BitDepth) - 1;
            switch (source.ColorType)
            {
                case 3:
                {
                    // the caller's index for a caller supplied palette, otherwise whichever entry has the color
                    var plte = source.Plte!;
                    int index = -1;
                    if (source.ExplicitPalette && bg.PaletteIndex >= 0 && bg.PaletteIndex < plte.Length / 3) index = bg.PaletteIndex;
                    for (int i = 0; index < 0 && i < plte.Length; i += 3)
                        if (plte[i] == bg.R && plte[i + 1] == bg.G && plte[i + 2] == bg.B) index = i / 3;
                    if (index < 0) return;
                    b[0] = (byte)index;
                    WriteChunk(output, PngChunks.bKGD, b.Slice(0, 1));
                    break;
                }
                case 0:
                case 4:
                    BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)Math.Min(max, (bg.R + source.GrayDivisor / 2) / source.GrayDivisor));
                    WriteChunk(output, PngChunks.bKGD, b.Slice(0, 2));
                    break;
                default:
                    BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)Math.Min(max, (int)bg.R));
                    BinaryPrimitives.WriteUInt16BigEndian(b.Slice(2), (ushort)Math.Min(max, (int)bg.G));
                    BinaryPrimitives.WriteUInt16BigEndian(b.Slice(4), (ushort)Math.Min(max, (int)bg.B));
                    WriteChunk(output, PngChunks.bKGD, b);
                    break;
            }
        }

        static void WriteText(Stream output, PngTextEntry entry, int level)
        {
            string key = entry.Keyword ?? "", text = entry.Text ?? "";
            bool keyOk = key.Length is >= 1 and <= 79 && key[0] != ' ' && key[^1] != ' ' && !key.Contains("  ");
            foreach (char ch in key) keyOk &= ch is >= ' ' and <= '~' or >= '¡' and <= 'ÿ';
            if (!keyOk) throw new ArgumentException($"invalid PNG text keyword '{key}' (1..79 printable Latin-1 characters, no leading, trailing or double spaces)");
            if (text.Contains('\0')) throw new ArgumentException("PNG text must not contain NUL characters");

            bool international = entry.International || entry.LanguageTag.Length > 0 || entry.TranslatedKeyword.Length > 0;
            foreach (char ch in text) international |= ch > 'ÿ';

            using var body = new MemoryStream();
            body.Write(Encoding.Latin1.GetBytes(key));
            body.WriteByte(0);
            if (!international)
            {
                var raw = Encoding.Latin1.GetBytes(text);
                if (entry.Compressed) { body.WriteByte(0); raw = Zlib.Compress(raw, Math.Max(level, 1)); }
                body.Write(raw);
                WriteChunk(output, entry.Compressed ? PngChunks.zTXt : PngChunks.tEXt, body.GetBuffer().AsSpan(0, (int)body.Length));
                return;
            }
            foreach (char ch in entry.LanguageTag)
                if (ch is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-'))
                    throw new ArgumentException($"invalid PNG text language tag '{entry.LanguageTag}'");
            if (entry.TranslatedKeyword.Contains('\0')) throw new ArgumentException("PNG text must not contain NUL characters");
            body.WriteByte(entry.Compressed ? (byte)1 : (byte)0);
            body.WriteByte(0);
            body.Write(Encoding.ASCII.GetBytes(entry.LanguageTag));
            body.WriteByte(0);
            body.Write(Encoding.UTF8.GetBytes(entry.TranslatedKeyword));
            body.WriteByte(0);
            var utf8 = Encoding.UTF8.GetBytes(text);
            body.Write(entry.Compressed ? Zlib.Compress(utf8, Math.Max(level, 1)) : utf8);
            WriteChunk(output, PngChunks.iTXt, body.GetBuffer().AsSpan(0, (int)body.Length));
        }

        /// <summary>Running APNG sequence number shared by the fcTL and fdAT chunks of a file.</summary>
        sealed class Sequence
        {
            public uint Next;
        }

        /// <summary>
        /// Collects the compressed stream and emits it as IDAT chunks of a fixed size, or as fdAT chunks, each of
        /// which starts with the next sequence number.
        /// </summary>
        sealed class DataStream : Stream
        {
            readonly Stream _output;
            readonly uint _type;
            readonly Sequence? _sequence;
            readonly int _prefix;
            byte[]? _buffer = ArrayPool<byte>.Shared.Rent(DataChunkSize);
            int _count;

            public DataStream(Stream output, uint type, Sequence? sequence)
            {
                _output = output; _type = type; _sequence = sequence;
                _prefix = sequence != null ? 4 : 0;
                _count = _prefix;
            }

            public override void Write(ReadOnlySpan<byte> data)
            {
                var buffer = _buffer ?? throw new ObjectDisposedException(nameof(DataStream));
                while (!data.IsEmpty)
                {
                    int n = Math.Min(data.Length, DataChunkSize - _count);
                    data.Slice(0, n).CopyTo(buffer.AsSpan(_count));
                    _count += n;
                    data = data.Slice(n);
                    if (_count == DataChunkSize) Emit();
                }
            }

            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
            public override void WriteByte(byte value) => Write(new ReadOnlySpan<byte>(in value));

            void Emit()
            {
                if (_sequence != null) BinaryPrimitives.WriteUInt32BigEndian(_buffer, _sequence.Next++);
                WriteChunk(_output, _type, _buffer.AsSpan(0, _count));
                _count = _prefix;
            }

            /// <summary>Writes the remaining data (call after the compressor has been closed).</summary>
            public void Finish()
            {
                if (_count > _prefix) Emit();
            }

            protected override void Dispose(bool disposing)
            {
                if (_buffer != null) { ArrayPool<byte>.Shared.Return(_buffer); _buffer = null; }
                base.Dispose(disposing);
            }

            public override void Flush() { } // chunks are cut by size only
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
