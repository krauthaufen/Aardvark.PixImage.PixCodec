using System;
using System.Collections.Generic;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>Reference frames and LF frames kept between frames.</summary>
    internal sealed class JxlDecoderState
    {
        public readonly JxlFrameImage?[] References = new JxlFrameImage?[4];
        /// <summary>Whether the reference slot holds a frame saved before its colour transform.</summary>
        public readonly bool[] ReferencePreCt = new bool[4];
        public readonly JxlFrameImage?[] LfFrames = new JxlFrameImage?[4];
        /// <summary>
        /// Displayed frames so far and frames since the last displayed one, both counting the frame being decoded (they seed
        /// the noise generator).
        /// </summary>
        public int VisibleFrames, NonVisibleFrames;
    }

    /// <summary>A displayed frame: the composited canvas after a frame with a duration (or the last one).</summary>
    internal sealed class JxlDisplayedFrame
    {
        public JxlFrameImage Canvas = null!;
        /// <summary>
        /// Whether the colour of an XYB image's canvas is in the header's colour encoding (blended there), not linear sRGB;
        /// grey images then have the encoded grey in each of the three colour planes.
        /// </summary>
        public bool Encoded;
        public uint Duration;
        public uint Timecode;
        public string Name = "";
    }

    /// <summary>Walks the frames of a codestream and composes them.</summary>
    internal sealed class JxlDecoder
    {
        public readonly byte[] Data;
        public readonly JxlImageHeader Header;
        readonly JxlDecodeOptions opt;
        readonly JxlDecoderState state = new();
        int pos;          // byte offset of the next frame
        public bool Done { get; private set; }
        public int FramesDecoded;
        /// <summary>Supplies a sink for the restored colour of the last frame when it is shown as it is (see CanFuse).</summary>
        public Func<JxlTileSink?>? SinkFactory;

        public JxlDecoder(byte[] codestream, JxlDecodeOptions opt)
        {
            Data = codestream;
            this.opt = opt;
            var br = new JxlBitReader(codestream);
            Header = JxlImageHeader.Read(br);
            CheckSize(Header.Width, Header.Height);
            if (Header.Colour.WantIcc) Header.Icc = JxlIcc.Read(br);
            br.ZeroPadToByte();
            br.CheckEnd();
            pos = br.BytePosition;
            if (Header.HavePreview) SkipPreview();
        }

        void CheckSize(long w, long h)
        {
            if (w * h > opt.MaxPixels)
                throw new ImageNotSupportedException($"JPEG XL: {w}x{h} exceeds JxlDecodeOptions.MaxPixels ({opt.MaxPixels})");
        }

        void SkipPreview()
        {
            var pimg = PreviewHeader();
            var br = new JxlBitReader(Data, pos, Data.Length - pos);
            var fh = JxlFrameHeader.Read(br, pimg);
            var toc = JxlToc.Read(br, JxlFrameDims.Of(fh));
            long next = (long)pos + br.BytePosition + toc.TotalSize;
            if (next > Data.Length) throw new ImageFormatException("JPEG XL: truncated preview frame");
            pos = (int)next;
        }

        JxlImageHeader PreviewHeader()
        {
            var h = (JxlImageHeader)MemberwiseCloneHeader(Header);
            h.Width = Header.PreviewWidth; h.Height = Header.PreviewHeight;
            h.Animation = null;
            return h;
        }

        static JxlImageHeader MemberwiseCloneHeader(JxlImageHeader h)
        {
            var c = new JxlImageHeader
            {
                Width = h.Width, Height = h.Height, Orientation = h.Orientation, BitDepth = h.BitDepth, Modular16BitBuffers = h.Modular16BitBuffers,
                ExtraChannels = h.ExtraChannels, XybEncoded = h.XybEncoded, Colour = h.Colour, ToneMapping = h.ToneMapping, Opsin = h.Opsin,
                Up2Weights = h.Up2Weights, Up4Weights = h.Up4Weights, Up8Weights = h.Up8Weights, Animation = h.Animation, Icc = h.Icc,
            };
            return c;
        }

        /// <summary>Decodes frames until the next displayed one; null after the last frame.</summary>
        public JxlDisplayedFrame? Next()
        {
            while (!Done)
            {
                if (pos >= Data.Length) throw new ImageFormatException("JPEG XL: codestream ends before the last frame");
                var br = new JxlBitReader(Data, pos, Data.Length - pos);
                var fh = JxlFrameHeader.Read(br, Header);
                CheckSize(fh.Width, fh.Height);
                var dims = JxlFrameDims.Of(fh);
                var toc = JxlToc.Read(br, dims);
                int sectionsStart = pos + br.BytePosition;
                long next = (long)sectionsStart + toc.TotalSize;
                if (next > Data.Length) throw new ImageFormatException("JPEG XL: truncated frame");
                if (++FramesDecoded > 1 << 16) throw new ImageNotSupportedException("JPEG XL: too many frames");
                if (IsDisplayed(fh)) { state.VisibleFrames++; state.NonVisibleFrames = 0; }
                else state.NonVisibleFrames++;
                var dec = new JxlFrameDecoder(Header, fh, state, Data, sectionsStart, toc, opt.Multithreaded);
                if (SinkFactory != null && CanFuse(fh)) dec.Sink = SinkFactory();
                var frame = dec.Decode();
                JxlProfile.Mark("finish frame");
                pos = (int)next;
                if (fh.IsLast) Done = true;
                var shown = Process(fh, frame);
                if (shown != null) return shown;
            }
            return null;
        }

        /// <summary>
        /// Whether the restoration filters of this frame can hand its colour straight to the output: the last, displayed XYB
        /// frame covering the image, without extra channels, features, upsampling or blending.
        /// </summary>
        bool CanFuse(JxlFrameHeader fh)
            => fh.IsLast && fh.Type == JxlFrameType.Regular && IsDisplayed(fh) && Header.XybEncoded && Header.ExtraChannels.Length == 0
               && fh.Upsampling == 1 && !fh.Has(JxlFrameHeader.FlagNoise) && !fh.Has(JxlFrameHeader.FlagPatches) && !fh.Has(JxlFrameHeader.FlagSplines)
               && fh.X0 == 0 && fh.Y0 == 0 && fh.Width == Header.Width && fh.Height == Header.Height && fh.Blending.Mode == JxlBlendMode.Replace
               && (fh.Filter.Gab || fh.Filter.EpfIters > 0);

        bool IsDisplayed(JxlFrameHeader fh)
            => (fh.Type == JxlFrameType.Regular || fh.Type == JxlFrameType.SkipProgressive)
               && (fh.IsLast || Header.Animation != null && fh.Duration != 0);

        JxlDisplayedFrame? Process(JxlFrameHeader fh, JxlFrameImage frame)
        {
            if (fh.Type == JxlFrameType.LfFrame)
            {
                state.LfFrames[fh.LfLevel - 1] = frame;
                return null;
            }
            bool canReference = !fh.IsLast && (fh.Duration == 0 || fh.SaveAsReference != 0) && fh.Type != JxlFrameType.LfFrame;
            if (fh.SaveBeforeColourTransform && canReference)
            {
                state.References[fh.SaveAsReference] = frame;
                state.ReferencePreCt[fh.SaveAsReference] = true;
            }
            // the last frame shown as it is: XYB is converted to linear RGB by the output stage, block by block
            bool defer = fh.IsLast && Header.XybEncoded && frame.Planes.Length >= 3 && IsDisplayed(fh) && JxlBlender.IsPassthrough(Header, fh, frame);
            // frames of XYB images that may blend are converted to the colour encoding of the header (not to an ICC profile)
            bool encoded = !defer && Header.XybEncoded && !Header.Colour.WantIcc && frame.Planes.Length >= 3;
            JxlFrameImage post;
            if (defer)
            {
                frame.PendingXyb = Header.Opsin;
                frame.PendingIntensityTarget = Header.ToneMapping.IntensityTarget;
                post = frame;
            }
            else post = ColourTransform(frame, encoded);
            JxlProfile.Mark("XYB to linear");
            var canvas = JxlBlender.Blend(Header, fh, post, state);
            if (!fh.SaveBeforeColourTransform && canReference)
            {
                state.References[fh.SaveAsReference] = canvas;
                state.ReferencePreCt[fh.SaveAsReference] = false;
            }
            if (!IsDisplayed(fh)) return null;
            return new JxlDisplayedFrame { Canvas = canvas, Encoded = encoded, Duration = fh.Duration, Timecode = fh.Timecode, Name = fh.Name };
        }

        /// <summary>
        /// XYB to linear RGB (sRGB primaries), and with <paramref name="encoded"/> on to the header's colour encoding, where
        /// blending happens (F.2, L.2.2); YCbCr to RGB; other frames are already in the output colour space.
        /// </summary>
        JxlFrameImage ColourTransform(JxlFrameImage f, bool encoded)
        {
            if (f.YCbCr)
            {
                var p = (float[][])f.Planes.Clone();
                p[0] = (float[])p[0].Clone(); p[1] = (float[])p[1].Clone(); p[2] = (float[])p[2].Clone();
                JxlColour.YCbCrToRgbInPlace(p[0], p[1], p[2], f.Width * f.Height);
                return new JxlFrameImage { Width = f.Width, Height = f.Height, NumColour = f.NumColour, Planes = p };
            }
            if (!Header.XybEncoded) return f;
            var r = new JxlFrameImage { Width = f.Width, Height = f.Height, NumColour = f.NumColour, Planes = (float[][])f.Planes.Clone() };
            int n = f.Width * f.Height;
            var x = (float[])f.Planes[0].Clone(); var y = (float[])f.Planes[1].Clone(); var b = (float[])f.Planes[2].Clone();
            JxlColour.XybToLinear(x, y, b, n, Header.Opsin, Header.ToneMapping.IntensityTarget, opt.Multithreaded);
            r.Planes[0] = x; r.Planes[1] = y; r.Planes[2] = b;
            if (encoded)
            {
                var col = Jxl.LinearToEncoding(Header, r.Planes, n, opt.Multithreaded);
                for (int c = 0; c < 3; c++) r.Planes[c] = c < col.Length ? col[c] : (float[])col[0].Clone();
            }
            return r;
        }
    }
}
