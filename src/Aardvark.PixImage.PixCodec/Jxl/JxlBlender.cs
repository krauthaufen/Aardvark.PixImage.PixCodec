using System;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>Frame blending onto the canvas (reference frames as backgrounds).</summary>
    internal static class JxlBlender
    {
        /// <summary>Whether Blend returns the frame itself (every channel replaces the whole canvas).</summary>
        public static bool IsPassthrough(JxlImageHeader img, JxlFrameHeader fh, JxlFrameImage frame)
        {
            bool allReplace = fh.Blending.Mode == JxlBlendMode.Replace;
            for (int e = 0; e < fh.EcBlending.Length; e++) allReplace &= fh.EcBlending[e].Mode == JxlBlendMode.Replace;
            return allReplace && fh.X0 == 0 && fh.Y0 == 0 && frame.Width == img.Width && frame.Height == img.Height;
        }

        /// <summary>
        /// Blends the frame onto Reference[source] (18181-1:2024 F.2, Tables F.7 and F.8). Table F.7 blends the colour by
        /// blending_info and each extra channel by its own ec_blending_info. libjxl (djxl 0.12) differs where the colour
        /// uses kBlend: the extra channel that is its alpha_channel is alpha-composited (old_alpha + new_alpha * (1 -
        /// old_alpha), new_alpha clamped by blending_info.clamp) whatever its own mode, and old_alpha, for that channel,
        /// the colour and every other channel blending with that alpha, is the alpha of Reference[source of the alpha
        /// channel's ec_blending_info] (0 when that source is not coded), not of the blending channel's own source. We
        /// match djxl (JxlAuditRenderTests.FrameAlphaWithBlendedColour, fixtures jxl-spec/blend_*.jxl).
        /// </summary>
        public static JxlFrameImage Blend(JxlImageHeader img, JxlFrameHeader fh, JxlFrameImage frame, JxlDecoderState state)
        {
            int W = img.Width, H = img.Height;
            if (IsPassthrough(img, fh, frame)) return frame;
            int nch = frame.Planes.Length;
            int nc = frame.NumColour;
            int x0 = fh.X0, y0 = fh.Y0;

            var canvas = new JxlFrameImage { Width = W, Height = H, NumColour = nc, Planes = new float[nch][] };
            // overlap of the frame with the canvas
            int fx0 = Math.Max(0, -x0), fy0 = Math.Max(0, -y0);
            int cx0 = Math.Max(0, x0), cy0 = Math.Max(0, y0);
            int ow = Math.Min(W - cx0, frame.Width - fx0), oh = Math.Min(H - cy0, frame.Height - fy0);
            int colourAlpha = fh.Blending.Mode == JxlBlendMode.Blend && nc + fh.Blending.AlphaChannel < nch ? nc + fh.Blending.AlphaChannel : -1;
            for (int c = 0; c < nch; c++)
            {
                var bi = c < nc ? fh.Blending : fh.EcBlending[c - nc];
                if (c == colourAlpha) bi = fh.Blending with { Source = bi.Source };   // composited as djxl does, see above
                var src = state.References[bi.Source];
                var bg = BackgroundPlane(src, c, W, H);
                var o = (float[])bg.Clone();
                canvas.Planes[c] = o;
                if (ow <= 0 || oh <= 0) continue;
                var fg = frame.Planes[c];
                float[]? fa = null, ba = null;
                bool premul = false;
                if (bi.Mode is JxlBlendMode.Blend or JxlBlendMode.AlphaWeightedAdd)
                {
                    int ai = nc + bi.AlphaChannel;
                    if (ai < nch)
                    {
                        fa = frame.Planes[ai];
                        ba = BackgroundPlane(state.References[fh.EcBlending[bi.AlphaChannel].Source], ai, W, H);   // old_alpha as djxl
                        premul = img.ExtraChannels[bi.AlphaChannel].AlphaAssociated;
                    }
                }
                bool isAlpha = fa != null && c == nc + bi.AlphaChannel;
                bool isAwaAlpha = isAlpha && bi.Mode == JxlBlendMode.AlphaWeightedAdd;
                for (int y = 0; y < oh; y++)
                {
                    int fr = (fy0 + y) * frame.Width + fx0, cr = (cy0 + y) * W + cx0;
                    switch (bi.Mode)
                    {
                        case JxlBlendMode.Replace:
                            Array.Copy(fg, fr, o, cr, ow);
                            break;
                        case JxlBlendMode.Add:
                            for (int x = 0; x < ow; x++) o[cr + x] = bg[cr + x] + fg[fr + x];
                            break;
                        case JxlBlendMode.Mul:
                            for (int x = 0; x < ow; x++)
                            {
                                float f = fg[fr + x];
                                if (bi.Clamp) f = Math.Clamp(f, 0, 1);
                                o[cr + x] = bg[cr + x] * f;
                            }
                            break;
                        case JxlBlendMode.AlphaWeightedAdd:
                            if (isAwaAlpha) break;   // the alpha channel itself keeps the background
                            for (int x = 0; x < ow; x++)
                            {
                                float a = fa != null ? fa[fr + x] : 1;
                                if (bi.Clamp) a = Math.Clamp(a, 0, 1);
                                o[cr + x] = bg[cr + x] + fg[fr + x] * a;
                            }
                            break;
                        case JxlBlendMode.Blend:
                            for (int x = 0; x < ow; x++)
                            {
                                float a = fa != null ? fa[fr + x] : 1;
                                float b = ba != null ? ba[cr + x] : 0;
                                if (bi.Clamp) a = Math.Clamp(a, 0, 1);   // new_alpha only (F.2)
                                if (isAlpha) { o[cr + x] = a + b * (1 - a); continue; }
                                if (premul) { o[cr + x] = fg[fr + x] + bg[cr + x] * (1 - a); continue; }
                                float na = a + b * (1 - a);
                                o[cr + x] = na > 0 ? (fg[fr + x] * a + bg[cr + x] * b * (1 - a)) / na : 0;
                            }
                            break;
                    }
                }
            }
            return canvas;
        }

        static float[] BackgroundPlane(JxlFrameImage? src, int c, int w, int h)
        {
            if (src == null || c >= src.Planes.Length) return new float[w * h];
            if (src.Width == w && src.Height == h) return src.Planes[c];
            var o = new float[w * h];
            int cw = Math.Min(w, src.Width), chh = Math.Min(h, src.Height);
            for (int y = 0; y < chh; y++) Array.Copy(src.Planes[c], y * src.Width, o, y * w, cw);
            return o;
        }
    }
}
