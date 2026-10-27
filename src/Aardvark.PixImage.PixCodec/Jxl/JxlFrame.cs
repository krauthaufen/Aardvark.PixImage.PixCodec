using System;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>
    /// Receives the restored colour of a frame tile by tile instead of full frame planes (the last frame of a still image,
    /// converted straight to the output).
    /// </summary>
    internal abstract unsafe class JxlTileSink
    {
        /// <summary>Rows y0 .. y0 + h - 1, columns x0 .. x0 + w - 1 of the three colour planes, at p[c] + row * stride.</summary>
        public abstract void WriteRows(int x0, int y0, int w, int h, float* p0, float* p1, float* p2, int stride);
    }

    /// <summary>A decoded frame (or reference / LF frame): float planes of the colour and extra channels.</summary>
    internal sealed class JxlFrameImage
    {
        public int Width, Height;
        /// <summary>Colour planes (1 or 3) followed by the extra channels, each Width * Height.</summary>
        public float[][] Planes
        {
            get
            {
                if (lazyPlanes != null) { planes = lazyPlanes(); lazyPlanes = null; }
                return planes;
            }
            set { planes = value; lazyPlanes = null; }
        }
        float[][] planes = Array.Empty<float[]>();
        Func<float[][]>? lazyPlanes;

        /// <summary>Planes built on first use (exact integer frames are usually output from <see cref="IntPlanes"/>).</summary>
        public void SetLazyPlanes(Func<float[][]> build) { planes = Array.Empty<float[]>(); lazyPlanes = build; }
        /// <summary>Exact integer samples of a lossless modular frame (colour + extra), when no float stage touched them.</summary>
        public int[][]? IntPlanes;
        public int NumColour;
        /// <summary>Set when the colour planes are still XYB: the conversion to linear RGB is left to the output stage.</summary>
        public JxlOpsinParams? PendingXyb;
        public float PendingIntensityTarget;
        /// <summary>Set when the colour planes are Cb, Y, Cr of a do_YCbCr frame: the colour transform (L.3) is still to come.</summary>
        public bool YCbCr;
        /// <summary>Set when the colour went to this sink: the colour planes are then absent.</summary>
        public JxlTileSink? Sink;

        /// <summary>Converts pending XYB colour planes to linear RGB in place.</summary>
        public void EnsureLinear(bool parallel)
        {
            if (PendingXyb == null) return;
            JxlColour.XybToLinear(Planes[0], Planes[1], Planes[2], Width * Height, PendingXyb, PendingIntensityTarget, parallel);
            PendingXyb = null;
        }
    }

    /// <summary>Decodes one frame: sections, modular and VarDCT data, filters, features and colour conversion.</summary>
    internal sealed partial class JxlFrameDecoder
    {
        readonly JxlImageHeader img;
        readonly JxlFrameHeader fh;
        readonly JxlFrameDims dims;
        readonly JxlDecoderState state;
        readonly byte[] data;
        readonly int sectionsStart;
        readonly JxlToc toc;
        readonly bool parallel;
        /// <summary>Where the restored colour goes instead of planes (only set when the restoration filters run).</summary>
        public JxlTileSink? Sink;

        // modular
        JxlModularGlobal modularGlobal = new();
        readonly float[] lfDequant = { 1 / 4096f, 1 / 512f, 1 / 256f };
        float[] LfDequant => lfDequant;
        JxlModularImage? globalImage;
        int numModularColour;          // colour channels coded in the modular image (0 for VarDCT)

        public JxlFrameDecoder(JxlImageHeader img, JxlFrameHeader fh, JxlDecoderState state, byte[] data, int sectionsStart, JxlToc toc, bool parallel)
        {
            this.img = img; this.fh = fh; this.state = state; this.data = data; this.sectionsStart = sectionsStart; this.toc = toc;
            this.parallel = parallel;
            dims = JxlFrameDims.Of(fh);
        }

        public JxlFrameDims Dims => dims;

        JxlBitReader Section(int index)
        {
            if (toc.Single) throw new InvalidOperationException();
            int off = sectionsStart + toc.Offset[index];
            int size = toc.Size[index];
            if ((long)off + size > data.Length) throw new ImageFormatException("JPEG XL: section beyond the end of the data");
            return new JxlBitReader(data, off, size);
        }

        JxlBitReader? single;

        JxlBitReader SectionReader(int index)
        {
            if (!toc.Single) return Section(index);
            if (single == null)
            {
                int size = toc.Size[0];
                if ((long)sectionsStart + size > data.Length) throw new ImageFormatException("JPEG XL: section beyond the end of the data");
                single = new JxlBitReader(data, sectionsStart, size);
            }
            return single;
        }

        long finishTicks;

        public JxlFrameImage Decode()
        {
            long maxTree = Math.Min(1L << 22, 1024L + (long)dims.XSize * dims.YSize * (3 + img.ExtraChannels.Length) / 16);
            JxlProfile.Reset();
            var lfGlobal = SectionReader(toc.LfGlobalIndex);
            DecodeLfGlobal(lfGlobal, maxTree);
            JxlProfile.Mark("LfGlobal");

            // LF groups; the HF global section (it does not depend on them) alongside
            var hfGlobal = !fh.Modular && parallel && !toc.Single
                ? System.Threading.Tasks.Task.Run(() => DecodeHfGlobal(SectionReader(toc.HfGlobalIndex(dims)))) : null;
            try { ForEach(dims.NumLfGroups, toc.Single, i => DecodeLfGroup(SectionReader(toc.LfGroupIndex(i)), i, maxTree)); }
            finally { try { hfGlobal?.Wait(); } catch (AggregateException) when (hfGlobal!.IsFaulted) { } }
            if (hfGlobal != null && hfGlobal.IsFaulted) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(hfGlobal.Exception!.InnerException!).Throw();
            if (!fh.Modular) AfterLfGroups();
            JxlProfile.Mark("LF groups");
            if (!fh.Modular && hfGlobal == null) DecodeHfGlobal(SectionReader(toc.HfGlobalIndex(dims)));
            JxlProfile.Mark("HfGlobal");
            ForEach(dims.NumGroups, toc.Single, g =>
            {
                for (int p = 0; p < fh.NumPasses; p++) DecodePassGroup(SectionReader(toc.PassGroupIndex(dims, p, g)), p, g, maxTree);
                long t0 = JxlProfile.On ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                if (!fh.Modular) FinishVarDctGroup(g);
                if (JxlProfile.On) System.Threading.Interlocked.Add(ref finishTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            });
            single?.CheckEnd();
            JxlProfile.Mark("pass groups");
            if (JxlProfile.On) Console.Error.WriteLine($"    of which dequant + IDCT {finishTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1} ms");
            return Finish();
        }

        void ForEach(int n, bool sequential, Action<int> body)
        {
            if (sequential || !parallel || n < 2) { for (int i = 0; i < n; i++) body(i); return; }
            try { Parallel.For(0, n, body); }
            catch (AggregateException e) when (e.InnerException is ImageFormatException or ImageNotSupportedException)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException!).Throw();
            }
        }

        // ------------------------------------------------------------------ LfGlobal

        void DecodeLfGlobal(JxlBitReader br, long maxTree)
        {
            if (fh.Has(JxlFrameHeader.FlagPatches)) DecodePatches(br);
            if (fh.Has(JxlFrameHeader.FlagSplines)) DecodeSplines(br);
            if (fh.Has(JxlFrameHeader.FlagNoise)) DecodeNoise(br);
            // LfChannelDequantization (all frames)
            if (!br.ReadBool())
            {
                for (int c = 0; c < 3; c++) lfDequant[c] = br.ReadF16() * (1 / 128f);
                if (lfDequant[0] <= 0 || lfDequant[1] <= 0 || lfDequant[2] <= 0) throw new ImageFormatException("JPEG XL: bad LF dequantization");
            }
            if (!fh.Modular) DecodeVarDctGlobal(br);
            // GlobalModular
            if (br.ReadBool())
            {
                modularGlobal.Tree = JxlMaTree.Read(br, maxTree);
                modularGlobal.Code = JxlEntropyCode.Read(br, modularGlobal.Tree.NumLeaves);
            }
            var channels = FrameChannels();
            globalImage = JxlModularImage.Decode(br, channels, ModularBitDepth(), 0, modularGlobal, dims.GroupDim, maxTree, true, out _);
            br.CheckEnd();
        }

        int ModularBitDepth() => img.BitDepth.Bits;

        /// <summary>The channels of the frame's modular image before any transform.</summary>
        List<JxlChannel> FrameChannels()
        {
            var list = new List<JxlChannel>();
            if (fh.Modular)
            {
                numModularColour = img.XybEncoded || !img.Colour.IsGray ? 3 : 1;
                if (fh.Type == JxlFrameType.LfFrame) numModularColour = 3;
                for (int c = 0; c < numModularColour; c++)
                {
                    int hs = 0, vs = 0;
                    if (fh.DoYCbCr && numModularColour == 3)
                    {
                        // modular colour channels are Y, Cb, Cr (frame channels 1, 0, 2)
                        fh.ChromaShift(c == 0 ? 1 : c == 1 ? 0 : 2, out hs, out vs);
                        int mhs = MaxJpegShift(true), mvs = MaxJpegShift(false);
                        int w = (dims.XSize + (1 << mhs) - 1 >> mhs << (mhs - hs));
                        int h = (dims.YSize + (1 << mvs) - 1 >> mvs << (mvs - vs));
                        w = (int)Math.Min(w, ((long)dims.XSize + (1 << hs) - 1) >> hs);
                        h = (int)Math.Min(h, ((long)dims.YSize + (1 << vs) - 1) >> vs);
                        list.Add(new JxlChannel(w, h, hs, vs, false));
                        continue;
                    }
                    list.Add(new JxlChannel(dims.XSize, dims.YSize, 0, 0, false));
                }
            }
            else numModularColour = 0;
            int upLog = Log2(fh.Upsampling);
            for (int e = 0; e < img.ExtraChannels.Length; e++)
            {
                int ecUp = fh.EcUpsampling[e] << img.ExtraChannels[e].DimShift;
                int shift = Log2(ecUp) - upLog;
                if (shift < 0) throw new ImageFormatException("JPEG XL: extra channel upsampling below the colour upsampling");
                int w = (int)(((long)dims.Width + ecUp - 1) / ecUp), h = (int)(((long)dims.Height + ecUp - 1) / ecUp);
                list.Add(new JxlChannel(w, h, shift, shift, false));
            }
            long total = 0;
            foreach (var c in list) total += (long)c.W * c.H;
            if (total > int.MaxValue) throw new ImageNotSupportedException("JPEG XL: frame too large");
            return list;
        }

        int MaxJpegShift(bool horizontal)
        {
            int m = 0;
            for (int i = 0; i < 3; i++)
            {
                int mode = fh.JpegUpsampling[i];
                int s = horizontal ? (mode is 1 or 2 ? 1 : 0) : (mode is 1 or 3 ? 1 : 0);
                m = Math.Max(m, s);
            }
            return m;
        }

        static int Log2(int v)
        {
            int r = 0;
            while ((1 << r) < v) r++;
            return r;
        }

        // ------------------------------------------------------------------ LF groups

        void DecodeLfGroup(JxlBitReader br, int index, long maxTree)
        {
            if (!fh.Modular && !fh.Has(JxlFrameHeader.FlagUseLfFrame)) DecodeLfCoefficients(br, index);
            // ModularLfGroup: channels with shift >= 3
            int gx = index % dims.LfGroupsX, gy = index / dims.LfGroupsX;
            DecodeModularRect(br, 1 + dims.NumLfGroups + index, gx * dims.LfGroupDim, gy * dims.LfGroupDim, dims.LfGroupDim, 3, int.MaxValue, maxTree);
            if (!fh.Modular) DecodeHfMetadata(br, index, maxTree);
            if (!toc.Single) br.CheckEnd();
        }

        // ------------------------------------------------------------------ pass groups

        void DecodePassGroup(JxlBitReader br, int pass, int group, long maxTree)
        {
            if (!fh.Modular) DecodeHfCoefficients(br, pass, group);
            int gx = group % dims.GroupsX, gy = group / dims.GroupsX;
            GetDownsamplingBracket(pass, out int minShift, out int maxShift);
            int stream = 1 + 3 * dims.NumLfGroups + 17 + dims.NumGroups * pass + group;
            DecodeModularRect(br, stream, gx * dims.GroupDim, gy * dims.GroupDim, dims.GroupDim, minShift, maxShift, maxTree);
            if (!toc.Single) br.CheckEnd();
        }

        void GetDownsamplingBracket(int pass, out int minShift, out int maxShift)
        {
            maxShift = 2; minShift = 3;
            for (int i = 0; ; i++)
            {
                for (int j = 0; j < fh.LastPass.Length; j++)
                {
                    if (i == fh.LastPass[j])
                    {
                        minShift = fh.Downsample[j] switch { 8 => 3, 4 => 2, 2 => 1, _ => 0 };
                    }
                }
                if (i == fh.NumPasses - 1) minShift = 0;
                if (i == pass) return;
                maxShift = minShift - 1;
            }
        }

        /// <summary>Decodes the part of the not yet decoded modular channels that falls into a group rectangle.</summary>
        void DecodeModularRect(JxlBitReader br, int stream, int x0, int y0, int dim, int minShift, int maxShift, long maxTree)
        {
            var full = globalImage!;
            var chans = full.Channels;
            var sel = new List<int>();
            var sub = new List<JxlChannel>();
            var origin = new List<(int x, int y)>();
            for (int c = full.DecodedChannels; c < chans.Count; c++)
            {
                var fc = chans[c];
                int shift = Math.Min(fc.HShift, fc.VShift);
                if (shift > maxShift || shift < minShift) continue;
                int cx = x0 >> fc.HShift, cy = y0 >> fc.VShift;
                int cw = Math.Min(dim >> fc.HShift, fc.W - cx), ch = Math.Min(dim >> fc.VShift, fc.H - cy);
                if (cw <= 0 || ch <= 0) continue;
                sel.Add(c);
                sub.Add(new JxlChannel(cw, ch, fc.HShift, fc.VShift, false));
                origin.Add((cx, cy));
            }
            if (sub.Count == 0) return;
            var gi = JxlModularImage.Decode(br, sub, full.BitDepth, stream, modularGlobal, 0, maxTree, true, out _);
            gi.Undo();
            if (gi.Channels.Count != sel.Count) throw new ImageFormatException("JPEG XL: group transforms changed the channel count");
            for (int k = 0; k < sel.Count; k++)
            {
                var src = gi.Channels[k];
                var dst = chans[sel[k]];
                var (ox, oy) = origin[k];
                if (src.W != sub[k].W || src.H != sub[k].H) throw new ImageFormatException("JPEG XL: group channel size changed");
                for (int y = 0; y < src.H; y++)
                    Array.Copy(src.Data, y * src.W, dst.Data, (oy + y) * dst.W + ox, src.W);
            }
        }

        // ------------------------------------------------------------------ finish

        JxlFrameImage Finish()
        {
            var full = globalImage!;
            full.Undo(parallel);
            var chans = full.Channels;
            int expected = numModularColour + img.ExtraChannels.Length;
            if (chans.Count != expected) throw new ImageFormatException("JPEG XL: modular transforms changed the channel count");
            var result = new JxlFrameImage { Width = dims.Width, Height = dims.Height };
            result.NumColour = img.XybEncoded || fh.Type == JxlFrameType.LfFrame || !img.Colour.IsGray ? 3 : 1;

            JxlProfile.Mark("modular undo");
            if (fh.Modular && !img.XybEncoded && !fh.DoYCbCr && IntegerExact() && numModularColour == result.NumColour)
            {
                // exact integers: the float planes only when something asks for them
                var ip = new int[result.NumColour + img.ExtraChannels.Length][];
                bool ok = true;
                for (int c = 0; c < numModularColour; c++) ip[c] = chans[c].Data;
                for (int e = 0; e < img.ExtraChannels.Length && ok; e++)
                {
                    var ch = chans[numModularColour + e];
                    if (ch.W != dims.Width || ch.H != dims.Height || img.ExtraChannels[e].BitDepth.Float) ok = false;
                    else ip[numModularColour + e] = ch.Data;
                }
                if (ok)
                {
                    result.IntPlanes = ip;
                    result.SetLazyPlanes(() => FloatPlanes(chans, result));
                    return result;
                }
            }
            result.Planes = FloatPlanes(chans, result);
            ApplyFeatures(result);
            if (fh.Modular && !img.XybEncoded && !fh.DoYCbCr && IntegerExact())
            {
                var planes = result.Planes;
                var ip = new int[planes.Length][];
                bool ok = numModularColour == result.NumColour;
                for (int c = 0; c < numModularColour && ok; c++) ip[c] = chans[c].Data;
                for (int e = 0; e < img.ExtraChannels.Length && ok; e++)
                {
                    var ch = chans[numModularColour + e];
                    if (ch.W != dims.Width || ch.H != dims.Height || img.ExtraChannels[e].BitDepth.Float) ok = false;
                    else ip[numModularColour + e] = ch.Data;
                }
                if (ok) result.IntPlanes = ip;
            }
            return result;
        }

        /// <summary>Colour (filtered, upsampled) and extra channels as float planes of the frame size.</summary>
        float[][] FloatPlanes(List<JxlChannel> chans, JxlFrameImage result)
        {
            // colour at the coded resolution, filtered, then upsampled
            float[][] colour = fh.Modular ? ModularColourToFloat(chans) : VarDctColour();
            JxlProfile.Mark("to float");
            ApplyRestorationFilters(colour);
            if (Sink != null) result.Sink = Sink;
            JxlProfile.Mark("filters");
            if (fh.Upsampling > 1)
                for (int c = 0; c < colour.Length; c++) colour[c] = Upsample(colour[c], dims.XSize, dims.YSize, fh.Upsampling, dims.Width, dims.Height);
            if (fh.DoYCbCr && colour.Length == 3)
            {
                // Cb, Y, Cr stay so through patches, splines and noise (K.3.2); the frame's colour transform makes RGB
                // of them (L.3). A grey image keeps only the first channel, R, so it is converted here.
                if (result.NumColour == 3) result.YCbCr = true;
                else JxlColour.YCbCrToRgbInPlace(colour[0], colour[1], colour[2], dims.Width * dims.Height);
            }
            var planes = new float[result.NumColour + img.ExtraChannels.Length][];
            for (int c = 0; c < result.NumColour; c++) planes[c] = colour[Math.Min(c, colour.Length - 1)];

            // extra channels
            for (int e = 0; e < img.ExtraChannels.Length; e++)
            {
                var ch = chans[numModularColour + e];
                var f = ToFloat(ch, img.ExtraChannels[e].BitDepth);
                int ecUp = fh.EcUpsampling[e] << img.ExtraChannels[e].DimShift;
                planes[result.NumColour + e] = ecUp > 1 ? Upsample(f, ch.W, ch.H, ecUp, dims.Width, dims.Height) : Crop(f, ch.W, ch.H, dims.Width, dims.Height);
            }
            return planes;
        }

        bool IntegerExact()
            => fh.Upsampling == 1 && !fh.Filter.Gab && fh.Filter.EpfIters == 0 && patches == null && splines == null && noise == null
               && dims.XSize == dims.Width && dims.YSize == dims.Height && !img.BitDepth.Float;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        float[] ToFloat(JxlChannel ch, JxlBitDepth bd)
        {
            var d = ch.Data;
            var f = new float[d.Length];
            if (bd.Float)
            {
                int mant = bd.Bits - bd.ExpBits - 1;
                for (int i = 0; i < d.Length; i++) f[i] = FloatFromBits(d[i], bd.ExpBits, mant);
            }
            else
            {
                float s = 1.0f / (float)((1L << bd.Bits) - 1);
                JxlColour.Chunked(d.Length, parallel, (a, e) =>
                {
                    var vs = new System.Numerics.Vector<float>(s);
                    int vc = System.Numerics.Vector<float>.Count, i = a;
                    for (; i + vc <= e; i += vc)
                        (System.Numerics.Vector.ConvertToSingle(new System.Numerics.Vector<int>(d, i)) * vs).CopyTo(f, i);
                    for (; i < e; i++) f[i] = d[i] * s;
                });
            }
            return f;
        }

        static float FloatFromBits(int v, int expBits, int mantBits)
        {
            if (expBits == 8 && mantBits == 23) return BitConverter.Int32BitsToSingle(v);
            uint u = (uint)v;
            uint sign = (u >> (expBits + mantBits)) & 1;
            uint exp = (u >> mantBits) & ((1u << expBits) - 1);
            uint mant = u & ((1u << mantBits) - 1);
            int bias = (1 << (expBits - 1)) - 1;
            float r;
            if (exp == 0) r = mant * MathF.Pow(2, 1 - bias - mantBits);
            else if (exp == (1u << expBits) - 1) r = mant != 0 ? float.NaN : float.PositiveInfinity;
            else r = (1 + mant / (float)(1u << mantBits)) * MathF.Pow(2, (int)exp - bias);
            return sign != 0 ? -r : r;
        }

        float[][] ModularColourToFloat(List<JxlChannel> chans)
        {
            int n = numModularColour;
            var res = new float[n][];
            if (img.XybEncoded && fh.Type != JxlFrameType.LfFrame || fh.Type == JxlFrameType.LfFrame && img.XybEncoded)
            {
                // XYB coded in modular mode: channels are Y, X, B (B - Y) quantized with the LF multipliers
                var lf = LfDequant;
                var y = chans[0].Data; var x = chans[1].Data; var b = chans[2].Data;
                int len = y.Length;
                var X = new float[len]; var Y = new float[len]; var B = new float[len];
                for (int i = 0; i < len; i++)
                {
                    float yy = y[i] * lf[1];
                    X[i] = x[i] * lf[0];
                    Y[i] = yy;
                    B[i] = (b[i] + y[i]) * lf[2];
                }
                res = new[] { X, Y, B };
                return Expand(res, chans[0].W, chans[0].H);
            }
            for (int c = 0; c < n; c++) res[c] = ToFloat(chans[c], img.BitDepth);
            if (fh.DoYCbCr)
            {
                // chroma channels may be subsampled; modular channels are Cb, Y, Cr, converted to RGB by the colour
                // transform after filters and features (J.1, L.3)
                var full = new float[3][];
                for (int c = 0; c < 3; c++) full[c] = UpsampleChroma(res[c], chans[c], dims.XSize, dims.YSize);
                return full;
            }
            return Expand(res, chans[0].W, chans[0].H);
        }

        float[][] Expand(float[][] planes, int w, int h)
        {
            if (w == dims.XSize && h == dims.YSize) return planes;
            var r = new float[planes.Length][];
            for (int c = 0; c < planes.Length; c++) r[c] = Crop(planes[c], w, h, dims.XSize, dims.YSize);
            return r;
        }

        static float[] Crop(float[] src, int w, int h, int ow, int oh)
        {
            if (w == ow && h == oh) return src;
            var o = new float[ow * oh];
            for (int y = 0; y < oh; y++)
                Array.Copy(src, Math.Min(y, h - 1) * w, o, y * ow, Math.Min(w, ow));
            return o;
        }

        static float[] UpsampleChroma(float[] src, JxlChannel ch, int w, int h)
        {
            if (ch.HShift == 0 && ch.VShift == 0) return Crop(src, ch.W, ch.H, w, h);
            return JxlColour.UpsampleJpegChroma(src, ch.W, ch.H, ch.HShift, ch.VShift, w, h);
        }
    }
}
