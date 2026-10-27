using System;
using System.Collections.Generic;
using static Aardvark.ImageCodecs.JpegXl.JxlBitReader;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>Writers of the codestream headers, the counterparts of the readers in JxlHeaders.cs / JxlToc.cs.</summary>
    internal static class JxlHeaderWriter
    {
        static uint Ratio(uint h, int ratio) => ratio switch
        {
            1 => h,
            2 => (uint)(h * 12UL / 10),
            3 => (uint)(h * 4UL / 3),
            4 => (uint)(h * 3UL / 2),
            5 => (uint)(h * 16UL / 9),
            6 => (uint)(h * 5UL / 4),
            _ => (uint)(h * 2UL),
        };

        static int FindRatio(uint w, uint h)
        {
            for (int r = 1; r <= 7; r++) if (Ratio(h, r) == w) return r;
            return 0;
        }

        static readonly ulong S0 = BitsOffset(9, 1), S1 = BitsOffset(13, 1), S2 = BitsOffset(18, 1), S3 = BitsOffset(30, 1);

        public static void WriteSize(JxlBitWriter w, int width, int height)
        {
            uint ww = (uint)width, hh = (uint)height;
            int ratio = FindRatio(ww, hh);
            bool hdiv = hh % 8 == 0 && hh / 8 >= 1 && hh / 8 <= 32;
            bool wdiv = ww % 8 == 0 && ww / 8 >= 1 && ww / 8 <= 32;
            bool div8 = hdiv && (ratio != 0 || wdiv);
            w.WriteBool(div8);
            if (div8) w.Write(5, hh / 8 - 1); else w.WriteU32(hh, S0, S1, S2, S3);
            w.Write(3, (uint)ratio);
            if (ratio == 0)
            {
                if (div8) w.Write(5, ww / 8 - 1); else w.WriteU32(ww, S0, S1, S2, S3);
            }
        }

        public static void WriteBitDepth(JxlBitWriter w, JxlBitDepth d)
        {
            w.WriteBool(d.Float);
            if (!d.Float) w.WriteU32((uint)d.Bits, Val(8), Val(10), Val(12), BitsOffset(6, 1));
            else
            {
                w.WriteU32((uint)d.Bits, Val(32), Val(16), Val(24), BitsOffset(6, 1));
                w.Write(4, (uint)(d.ExpBits - 1));
            }
        }

        public static void WriteName(JxlBitWriter w, string name)
        {
            var b = System.Text.Encoding.UTF8.GetBytes(name ?? "");
            w.WriteU32((uint)b.Length, Val(0), Bits(4), BitsOffset(5, 16), BitsOffset(10, 48));
            foreach (var x in b) w.Write(8, x);
        }

        static void WriteExtraChannel(JxlBitWriter w, JxlExtraChannel e)
        {
            bool dAlpha = e.Type == JxlExtraChannelType.Alpha && !e.BitDepth.Float && e.BitDepth.Bits == 8 && e.DimShift == 0
                          && string.IsNullOrEmpty(e.Name) && !e.AlphaAssociated;
            w.WriteBool(dAlpha);
            if (dAlpha) return;
            w.WriteEnum((int)e.Type);
            WriteBitDepth(w, e.BitDepth);
            w.WriteU32((uint)e.DimShift, Val(0), Val(3), Val(4), BitsOffset(3, 1));
            WriteName(w, e.Name);
            if (e.Type == JxlExtraChannelType.Alpha) w.WriteBool(e.AlphaAssociated);
            if (e.Type == JxlExtraChannelType.SpotColour)
            {
                w.WriteF16(e.SpotRed); w.WriteF16(e.SpotGreen); w.WriteF16(e.SpotBlue); w.WriteF16(e.SpotSolidity);
            }
            if (e.Type == JxlExtraChannelType.Cfa) w.WriteU32((uint)e.CfaChannel, Val(1), Bits(2), BitsOffset(4, 3), BitsOffset(8, 19));
        }

        static void WriteCustomXy(JxlBitWriter w, (float x, float y) xy)
        {
            foreach (float v in new[] { xy.x, xy.y })
            {
                int i = (int)Math.Round(v * 1e6);
                uint u = JxlBitWriter.PackSigned(i);
                w.WriteU32(u, Bits(19), BitsOffset(19, 524288), BitsOffset(20, 1048576), BitsOffset(21, 2097152));
            }
        }

        public static bool IsDefaultColour(JxlColourEncoding c)
            => !c.WantIcc && c.ColourSpace == JxlColourSpace.Rgb && c.WhitePoint == JxlWhitePoint.D65 && c.Primaries == JxlPrimaries.Srgb
               && c.Transfer == JxlTransfer.Srgb && c.RenderingIntent == 1;

        public static void WriteColourEncoding(JxlBitWriter w, JxlColourEncoding c)
        {
            bool def = IsDefaultColour(c);
            w.WriteBool(def);
            if (def) return;
            w.WriteBool(c.WantIcc);
            w.WriteEnum((int)c.ColourSpace);
            if (c.WantIcc) return;
            if (c.ColourSpace != JxlColourSpace.Xyb)
            {
                w.WriteEnum((int)c.WhitePoint);
                if (c.WhitePoint == JxlWhitePoint.Custom) WriteCustomXy(w, c.White);
            }
            if (c.ColourSpace != JxlColourSpace.Xyb && c.ColourSpace != JxlColourSpace.Grey)
            {
                w.WriteEnum((int)c.Primaries);
                if (c.Primaries == JxlPrimaries.Custom) { WriteCustomXy(w, c.Red); WriteCustomXy(w, c.Green); WriteCustomXy(w, c.Blue); }
            }
            bool gamma = c.Transfer == JxlTransfer.Gamma;
            w.WriteBool(gamma);
            if (gamma) w.Write(24, (uint)Math.Clamp(Math.Round(c.Gamma * 1e7), 1, 10000000));
            else w.WriteEnum((int)c.Transfer);
            w.WriteEnum(c.RenderingIntent);
        }

        /// <summary>SizeHeader, ImageMetadata and (default) CustomTransformData.</summary>
        public static void WriteImageHeader(JxlBitWriter w, JxlImageHeader h)
        {
            w.Write(16, 0x0AFF);
            WriteSize(w, h.Width, h.Height);
            var tm = h.ToneMapping;
            bool toneDefault = tm.IntensityTarget == 255 && tm.MinNits == 0 && !tm.RelativeToMaxDisplay && tm.LinearBelow == 0;
            bool extra = h.Orientation != 1 || h.Animation != null || h.HavePreview || h.HaveIntrinsicSize || !toneDefault;
            bool allDefault = !extra && !h.BitDepth.Float && h.BitDepth.Bits == 8 && h.ExtraChannels.Length == 0 && h.XybEncoded
                              && IsDefaultColour(h.Colour) && h.Modular16BitBuffers;
            w.WriteBool(allDefault);
            if (!allDefault)
            {
                w.WriteBool(extra);
                if (extra)
                {
                    w.Write(3, (uint)(h.Orientation - 1));
                    w.WriteBool(h.HaveIntrinsicSize);
                    if (h.HaveIntrinsicSize) WriteSize(w, h.IntrinsicWidth, h.IntrinsicHeight);
                    w.WriteBool(false);   // preview
                    w.WriteBool(h.Animation != null);
                    if (h.Animation != null)
                    {
                        var a = h.Animation;
                        w.WriteU32(a.TpsNumerator, Val(100), Val(1000), BitsOffset(10, 1), BitsOffset(30, 1));
                        w.WriteU32(a.TpsDenominator, Val(1), Val(1001), BitsOffset(8, 1), BitsOffset(10, 1));
                        w.WriteU32(a.NumLoops, Val(0), Bits(3), Bits(16), Bits(32));
                        w.WriteBool(a.HaveTimecodes);
                    }
                }
                WriteBitDepth(w, h.BitDepth);
                w.WriteBool(h.Modular16BitBuffers);
                w.WriteU32((uint)h.ExtraChannels.Length, Val(0), Val(1), BitsOffset(4, 2), BitsOffset(12, 1));
                foreach (var e in h.ExtraChannels) WriteExtraChannel(w, e);
                w.WriteBool(h.XybEncoded);
                WriteColourEncoding(w, h.Colour);
                if (extra)
                {
                    w.WriteBool(toneDefault);
                    if (!toneDefault)
                    {
                        w.WriteF16(tm.IntensityTarget);
                        w.WriteF16(tm.MinNits);
                        w.WriteBool(tm.RelativeToMaxDisplay);
                        w.WriteF16(tm.LinearBelow);
                    }
                }
                w.WriteU64(0);   // extensions
            }
            w.WriteBool(true);   // default CustomTransformData
        }

        static void WriteBlending(JxlBitWriter w, JxlBlendingInfo b, int numExtra, bool fullFrame)
        {
            w.WriteU32((uint)b.Mode, Val(0), Val(1), Val(2), BitsOffset(2, 3));
            bool alphaModes = b.Mode == JxlBlendMode.Blend || b.Mode == JxlBlendMode.AlphaWeightedAdd;
            if (numExtra > 0 && alphaModes) w.WriteU32((uint)b.AlphaChannel, Val(0), Val(1), Val(2), BitsOffset(3, 3));
            if (numExtra > 0 && alphaModes || b.Mode == JxlBlendMode.Mul) w.WriteBool(b.Clamp);
            if (b.Mode != JxlBlendMode.Replace || !fullFrame) w.Write(2, (uint)b.Source);
        }

        static readonly ulong C0 = Bits(8), C1 = BitsOffset(11, 256), C2 = BitsOffset(14, 2304), C3 = BitsOffset(30, 18688);

        static void WriteRestorationFilter(JxlBitWriter w, JxlRestorationFilter f, bool modular, bool customGab)
        {
            w.WriteBool(false);   // not all default
            w.WriteBool(f.Gab);
            if (f.Gab)
            {
                w.WriteBool(customGab);
                if (customGab) for (int i = 0; i < 6; i++) w.WriteF16(f.GabWeights[i]);
            }
            w.Write(2, (uint)f.EpfIters);
            if (f.EpfIters > 0)
            {
                if (!modular) w.WriteBool(false);   // default sharpness LUT
                bool customScales = f.EpfChannelScale[0] != 40 || f.EpfChannelScale[1] != 5 || f.EpfChannelScale[2] != 3.5f;
                w.WriteBool(customScales);
                if (customScales)
                {
                    for (int i = 0; i < 3; i++) w.WriteF16(f.EpfChannelScale[i]);
                    w.Write(32, 0);                 // ignored
                }
                w.WriteBool(false);                 // default sigma parameters
                if (modular) w.WriteF16(f.EpfSigmaForModular);
            }
            w.WriteU64(0);
        }

        /// <summary>Frame header (all fields written explicitly) as <see cref="JxlFrameHeader.Read"/> reads it.</summary>
        public static void WriteFrameHeader(JxlBitWriter w, JxlFrameHeader f, JxlImageHeader img, bool customGab = false)
        {
            int numExtra = img.ExtraChannels.Length;
            w.WriteBool(false);   // not all default
            w.Write(2, (uint)f.Type);
            w.Write(1, f.Modular ? 1u : 0u);
            w.WriteU64(f.Flags);
            if (!img.XybEncoded) w.WriteBool(f.DoYCbCr);
            if (!f.Has(JxlFrameHeader.FlagUseLfFrame))
            {
                if (f.DoYCbCr) for (int i = 0; i < 3; i++) w.Write(2, (uint)f.JpegUpsampling[i]);
                w.WriteU32((uint)f.Upsampling, Val(1), Val(2), Val(4), Val(8));
                for (int i = 0; i < numExtra; i++) w.WriteU32((uint)f.EcUpsampling[i], Val(1), Val(2), Val(4), Val(8));
            }
            if (f.Modular) w.Write(2, (uint)f.GroupSizeShift);
            if (!f.Modular && img.XybEncoded) { w.Write(3, (uint)f.XQmScale); w.Write(3, (uint)f.BQmScale); }
            if (f.Type != JxlFrameType.ReferenceOnly)
            {
                w.WriteU32((uint)f.NumPasses, Val(1), Val(2), Val(3), BitsOffset(3, 4));
                if (f.NumPasses != 1) throw new NotSupportedException("JPEG XL encoder: multiple passes");
            }
            if (f.Type == JxlFrameType.LfFrame) w.Write(2, (uint)(f.LfLevel - 1));
            if (f.Type != JxlFrameType.LfFrame)
            {
                w.WriteBool(f.HaveCrop);
                if (f.HaveCrop)
                {
                    if (f.Type != JxlFrameType.ReferenceOnly)
                    {
                        w.WriteU32(JxlBitWriter.PackSigned(f.X0), C0, C1, C2, C3);
                        w.WriteU32(JxlBitWriter.PackSigned(f.Y0), C0, C1, C2, C3);
                    }
                    w.WriteU32((uint)f.Width, C0, C1, C2, C3);
                    w.WriteU32((uint)f.Height, C0, C1, C2, C3);
                }
            }
            bool full = f.FullFrame(img);
            if (f.Type == JxlFrameType.Regular || f.Type == JxlFrameType.SkipProgressive)
            {
                WriteBlending(w, f.Blending, numExtra, full);
                for (int i = 0; i < numExtra; i++) WriteBlending(w, f.EcBlending[i], numExtra, full);
                if (img.Animation != null)
                {
                    w.WriteU32(f.Duration, Val(0), Val(1), Bits(8), Bits(32));
                    if (img.Animation.HaveTimecodes) w.Write(32, f.Timecode);
                }
                w.WriteBool(f.IsLast);
            }
            if (f.Type != JxlFrameType.LfFrame && !f.IsLast) w.Write(2, (uint)f.SaveAsReference);
            if (f.Type == JxlFrameType.ReferenceOnly
                || full && (f.Type == JxlFrameType.Regular || f.Type == JxlFrameType.SkipProgressive)
                   && f.Blending.Mode == JxlBlendMode.Replace && (f.Duration == 0 || f.SaveAsReference != 0) && !f.IsLast)
                w.WriteBool(f.SaveBeforeColourTransform);
            WriteName(w, f.Name);
            WriteRestorationFilter(w, f.Filter, f.Modular, customGab);
            w.WriteU64(0);
        }

        /// <summary>The table of contents (no permutation), byte aligned before and after.</summary>
        public static void WriteToc(JxlBitWriter w, IReadOnlyList<int> sizes)
        {
            w.WriteBool(false);
            w.ZeroPadToByte();
            foreach (int s in sizes) w.WriteU32((uint)s, Bits(10), BitsOffset(14, 1024), BitsOffset(22, 17408), BitsOffset(30, 4211712));
            w.ZeroPadToByte();
        }
    }
}
