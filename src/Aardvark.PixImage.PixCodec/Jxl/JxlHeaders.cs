using System;
using static Aardvark.ImageCodecs.JpegXl.JxlBitReader;

namespace Aardvark.ImageCodecs.JpegXl
{
    internal enum JxlExtraChannelType { Alpha = 0, Depth = 1, SpotColour = 2, SelectionMask = 3, Black = 4, Cfa = 5, Thermal = 6, NonOptional = 15, Optional = 16 }

    internal enum JxlColourSpace { Rgb = 0, Grey = 1, Xyb = 2, Unknown = 3 }

    internal enum JxlWhitePoint { D65 = 1, Custom = 2, E = 10, Dci = 11 }

    internal enum JxlPrimaries { Srgb = 1, Custom = 2, Bt2100 = 9, P3 = 11 }

    internal enum JxlTransfer { Bt709 = 1, Unknown = 2, Linear = 8, Srgb = 13, Pq = 16, Dci = 17, Hlg = 18, Gamma = 256 }

    internal enum JxlFrameType { Regular = 0, LfFrame = 1, ReferenceOnly = 2, SkipProgressive = 3 }

    internal enum JxlBlendMode { Replace = 0, Add = 1, Blend = 2, AlphaWeightedAdd = 3, Mul = 4 }

    internal struct JxlBitDepth
    {
        public bool Float;
        public int Bits;
        public int ExpBits;

        public static JxlBitDepth Read(JxlBitReader br)
        {
            var d = new JxlBitDepth { Float = br.ReadBool() };
            if (!d.Float)
            {
                d.Bits = (int)br.ReadU32(Val(8), Val(10), Val(12), BitsOffset(6, 1));
                if (d.Bits > 31) throw new ImageFormatException("JPEG XL: integer bit depth above 31");
            }
            else
            {
                d.Bits = (int)br.ReadU32(Val(32), Val(16), Val(24), BitsOffset(6, 1));
                d.ExpBits = 1 + (int)br.ReadBits(4);
                // D.3.5: exp_bits in [2, 8], mantissa_bits = bits_per_sample - exp_bits - 1 in [2, 23]
                int mantissa = d.Bits - d.ExpBits - 1;
                if (d.ExpBits < 2 || d.ExpBits > 8 || mantissa < 2 || mantissa > 23)
                    throw new ImageFormatException("JPEG XL: unsupported float sample format");
            }
            return d;
        }

        public static JxlBitDepth Default8 => new JxlBitDepth { Bits = 8 };
    }

    internal sealed class JxlExtraChannel
    {
        public JxlExtraChannelType Type = JxlExtraChannelType.Alpha;
        public JxlBitDepth BitDepth = JxlBitDepth.Default8;
        public int DimShift;
        public string Name = "";
        public bool AlphaAssociated;
        public float SpotRed, SpotGreen, SpotBlue, SpotSolidity;
        public int CfaChannel = 1;

        public static JxlExtraChannel Read(JxlBitReader br)
        {
            var e = new JxlExtraChannel();
            if (br.ReadBool()) return e;   // d_alpha
            int t = br.ReadEnum();
            if (t is not (<= 6 or 15 or 16)) throw new ImageFormatException("JPEG XL: unknown extra channel type");
            e.Type = (JxlExtraChannelType)t;
            e.BitDepth = JxlBitDepth.Read(br);
            e.DimShift = (int)br.ReadU32(Val(0), Val(3), Val(4), BitsOffset(3, 1));
            if (e.DimShift > 8) throw new ImageFormatException("JPEG XL: extra channel dim_shift too large");
            e.Name = JxlHeaderUtil.ReadName(br);
            if (e.Type == JxlExtraChannelType.Alpha) e.AlphaAssociated = br.ReadBool();
            if (e.Type == JxlExtraChannelType.SpotColour)
            {
                e.SpotRed = br.ReadF16(); e.SpotGreen = br.ReadF16(); e.SpotBlue = br.ReadF16(); e.SpotSolidity = br.ReadF16();
            }
            if (e.Type == JxlExtraChannelType.Cfa) e.CfaChannel = (int)br.ReadU32(Val(1), Bits(2), BitsOffset(4, 3), BitsOffset(8, 19));
            return e;
        }
    }

    internal static class JxlHeaderUtil
    {
        public static string ReadName(JxlBitReader br)
        {
            int len = (int)br.ReadU32(Val(0), Bits(4), BitsOffset(5, 16), BitsOffset(10, 48));
            if (len == 0) return "";
            var b = new byte[len];
            for (int i = 0; i < len; i++) b[i] = (byte)br.ReadBits(8);
            return System.Text.Encoding.UTF8.GetString(b);
        }

        public static void ReadSize(JxlBitReader br, out int width, out int height)
        {
            bool div8 = br.ReadBool();
            uint h = div8 ? (1 + br.ReadBits(5)) * 8 : br.ReadU32(BitsOffset(9, 1), BitsOffset(13, 1), BitsOffset(18, 1), BitsOffset(30, 1));
            int ratio = (int)br.ReadBits(3);
            uint w = ratio != 0 ? Ratio(h, ratio)
                : div8 ? (1 + br.ReadBits(5)) * 8 : br.ReadU32(BitsOffset(9, 1), BitsOffset(13, 1), BitsOffset(18, 1), BitsOffset(30, 1));
            Check(w, h);
            width = (int)w; height = (int)h;
        }

        public static void ReadPreviewSize(JxlBitReader br, out int width, out int height)
        {
            bool div8 = br.ReadBool();
            uint h = div8 ? br.ReadU32(Val(16), Val(32), BitsOffset(5, 1), BitsOffset(9, 33)) * 8
                : br.ReadU32(BitsOffset(6, 1), BitsOffset(8, 65), BitsOffset(10, 321), BitsOffset(12, 1345));
            int ratio = (int)br.ReadBits(3);
            uint w = ratio != 0 ? Ratio(h, ratio)
                : div8 ? br.ReadU32(Val(16), Val(32), BitsOffset(5, 1), BitsOffset(9, 33)) * 8
                : br.ReadU32(BitsOffset(6, 1), BitsOffset(8, 65), BitsOffset(10, 321), BitsOffset(12, 1345));
            Check(w, h);
            if (w > 4096 || h > 4096) throw new ImageFormatException("JPEG XL: preview larger than 4096");   // D.3.3
            width = (int)w; height = (int)h;
        }

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

        static void Check(uint w, uint h)
        {
            if (w == 0 || h == 0 || w > (1u << 30) || h > (1u << 30)) throw new ImageFormatException("JPEG XL: bad image dimensions");
        }

        public static (float x, float y) ReadCustomXy(JxlBitReader br)
        {
            int x = UnpackSigned(br.ReadU32(Bits(19), BitsOffset(19, 524288), BitsOffset(20, 1048576), BitsOffset(21, 2097152)));
            int y = UnpackSigned(br.ReadU32(Bits(19), BitsOffset(19, 524288), BitsOffset(20, 1048576), BitsOffset(21, 2097152)));
            return (x * 1e-6f, y * 1e-6f);
        }
    }

    internal sealed class JxlColourEncoding
    {
        public bool WantIcc;
        public JxlColourSpace ColourSpace = JxlColourSpace.Rgb;
        public JxlWhitePoint WhitePoint = JxlWhitePoint.D65;
        public (float x, float y) White;
        public JxlPrimaries Primaries = JxlPrimaries.Srgb;
        public (float x, float y) Red, Green, Blue;
        public JxlTransfer Transfer = JxlTransfer.Srgb;
        /// <summary>Gamma as stored (the exponent of the encoding, e.g. 0.45455), when <see cref="Transfer"/> is Gamma.</summary>
        public double Gamma;
        public int RenderingIntent = 1;

        public static JxlColourEncoding Read(JxlBitReader br)
        {
            var c = new JxlColourEncoding();
            if (br.ReadBool()) return c;   // all_default
            c.WantIcc = br.ReadBool();
            int cs = br.ReadEnum();
            if (cs > 3) throw new ImageFormatException("JPEG XL: bad colour space");
            c.ColourSpace = (JxlColourSpace)cs;
            if (c.WantIcc) return c;
            if (c.ColourSpace != JxlColourSpace.Xyb)
            {
                int wp = br.ReadEnum();
                if (wp is not (1 or 2 or 10 or 11)) throw new ImageFormatException("JPEG XL: bad white point");
                c.WhitePoint = (JxlWhitePoint)wp;
                if (c.WhitePoint == JxlWhitePoint.Custom) c.White = JxlHeaderUtil.ReadCustomXy(br);
            }
            if (c.ColourSpace != JxlColourSpace.Xyb && c.ColourSpace != JxlColourSpace.Grey)
            {
                int p = br.ReadEnum();
                if (p is not (1 or 2 or 9 or 11)) throw new ImageFormatException("JPEG XL: bad primaries");
                c.Primaries = (JxlPrimaries)p;
                if (c.Primaries == JxlPrimaries.Custom)
                {
                    c.Red = JxlHeaderUtil.ReadCustomXy(br);
                    c.Green = JxlHeaderUtil.ReadCustomXy(br);
                    c.Blue = JxlHeaderUtil.ReadCustomXy(br);
                }
            }
            if (br.ReadBool())   // have_gamma
            {
                uint g = br.ReadBits(24);
                if (g == 0 || g > 10000000) throw new ImageFormatException("JPEG XL: bad gamma");
                c.Transfer = JxlTransfer.Gamma;
                c.Gamma = g / 1e7;
            }
            else
            {
                int tf = br.ReadEnum();
                if (tf is not (1 or 2 or 8 or 13 or 16 or 17 or 18)) throw new ImageFormatException("JPEG XL: bad transfer function");
                c.Transfer = (JxlTransfer)tf;
            }
            c.RenderingIntent = br.ReadEnum();
            if (c.RenderingIntent > 3) throw new ImageFormatException("JPEG XL: bad rendering intent");
            return c;
        }

        public bool IsGray => ColourSpace == JxlColourSpace.Grey;
    }

    internal sealed class JxlToneMapping
    {
        public float IntensityTarget = 255, MinNits;
        public bool RelativeToMaxDisplay;
        public float LinearBelow;

        public static JxlToneMapping Read(JxlBitReader br)
        {
            var t = new JxlToneMapping();
            if (br.ReadBool()) return t;
            t.IntensityTarget = br.ReadF16();
            t.MinNits = br.ReadF16();
            t.RelativeToMaxDisplay = br.ReadBool();
            t.LinearBelow = br.ReadF16();
            if (t.IntensityTarget <= 0) throw new ImageFormatException("JPEG XL: bad intensity target");
            // E.3: min_nits in [0, intensity_target]; linear_below a ratio in [0, 1] or nits >= 0
            if (t.MinNits < 0 || t.MinNits > t.IntensityTarget) throw new ImageFormatException("JPEG XL: bad min_nits");
            if (t.LinearBelow < 0 || t.RelativeToMaxDisplay && t.LinearBelow > 1) throw new ImageFormatException("JPEG XL: bad linear_below");
            return t;
        }
    }

    internal sealed class JxlAnimationHeader
    {
        public uint TpsNumerator = 100, TpsDenominator = 1, NumLoops;
        public bool HaveTimecodes;

        public static JxlAnimationHeader Read(JxlBitReader br)
        {
            var a = new JxlAnimationHeader
            {
                TpsNumerator = br.ReadU32(Val(100), Val(1000), BitsOffset(10, 1), BitsOffset(30, 1)),
                TpsDenominator = br.ReadU32(Val(1), Val(1001), BitsOffset(8, 1), BitsOffset(10, 1)),
                NumLoops = br.ReadU32(Val(0), Bits(3), Bits(16), Bits(32)),
                HaveTimecodes = br.ReadBool(),
            };
            return a;
        }
    }

    /// <summary>The inverse opsin (XYB) parameters with their defaults.</summary>
    internal sealed class JxlOpsinParams
    {
        public float[] InvMat =
        {
            11.031566901960783f, -9.866943921568629f, -0.16462299647058826f,
            -3.254147380392157f, 4.418770392156863f, -0.16462299647058826f,
            -3.6588512862745097f, 2.7129230470588235f, 1.9459282392156863f,
        };
        public float[] OpsinBias = { -0.0037930732552754493f, -0.0037930732552754493f, -0.0037930732552754493f };
        public float[] QuantBias = { 1 - 0.05465007330715401f, 1 - 0.07005449891748593f, 1 - 0.049935103337343655f };
        public float QuantBiasNumerator = 0.145f;

        public static JxlOpsinParams Read(JxlBitReader br)
        {
            var o = new JxlOpsinParams();
            if (br.ReadBool()) return o;
            for (int i = 0; i < 9; i++) o.InvMat[i] = br.ReadF16();
            for (int i = 0; i < 3; i++) o.OpsinBias[i] = br.ReadF16();
            for (int i = 0; i < 3; i++) o.QuantBias[i] = br.ReadF16();
            o.QuantBiasNumerator = br.ReadF16();
            return o;
        }
    }

    /// <summary>SizeHeader, ImageMetadata and CustomTransformData of the codestream.</summary>
    internal sealed class JxlImageHeader
    {
        public int Width, Height;
        public int Orientation = 1;
        public bool HaveIntrinsicSize;
        public int IntrinsicWidth, IntrinsicHeight;
        public bool HavePreview;
        public int PreviewWidth, PreviewHeight;
        public JxlAnimationHeader? Animation;
        public JxlBitDepth BitDepth = JxlBitDepth.Default8;
        public bool Modular16BitBuffers = true;
        public JxlExtraChannel[] ExtraChannels = Array.Empty<JxlExtraChannel>();
        public bool XybEncoded = true;
        public JxlColourEncoding Colour = new();
        public JxlToneMapping ToneMapping = new();
        public JxlOpsinParams Opsin = new();
        public float[]? Up2Weights, Up4Weights, Up8Weights;
        /// <summary>Embedded ICC profile (decoded from the ICC stream), when <see cref="JxlColourEncoding.WantIcc"/>.</summary>
        public byte[]? Icc;

        public int NumColourChannels => Colour.IsGray ? 1 : 3;

        public int AlphaChannel
        {
            get
            {
                for (int i = 0; i < ExtraChannels.Length; i++) if (ExtraChannels[i].Type == JxlExtraChannelType.Alpha) return i;
                return -1;
            }
        }

        public static JxlImageHeader Read(JxlBitReader br)
        {
            if (br.ReadBits(16) != 0x0AFF) throw new ImageFormatException("JPEG XL: missing codestream signature");
            var h = new JxlImageHeader();
            JxlHeaderUtil.ReadSize(br, out h.Width, out h.Height);
            // ImageMetadata
            if (!br.ReadBool())
            {
                bool extraFields = br.ReadBool();
                if (extraFields)
                {
                    h.Orientation = 1 + (int)br.ReadBits(3);
                    h.HaveIntrinsicSize = br.ReadBool();
                    if (h.HaveIntrinsicSize) JxlHeaderUtil.ReadSize(br, out h.IntrinsicWidth, out h.IntrinsicHeight);
                    h.HavePreview = br.ReadBool();
                    if (h.HavePreview) JxlHeaderUtil.ReadPreviewSize(br, out h.PreviewWidth, out h.PreviewHeight);
                    if (br.ReadBool()) h.Animation = JxlAnimationHeader.Read(br);
                }
                h.BitDepth = JxlBitDepth.Read(br);
                h.Modular16BitBuffers = br.ReadBool();
                int numExtra = (int)br.ReadU32(Val(0), Val(1), BitsOffset(4, 2), BitsOffset(12, 1));
                if (numExtra > 256) throw new ImageNotSupportedException("JPEG XL: more than 256 extra channels");
                h.ExtraChannels = new JxlExtraChannel[numExtra];
                for (int i = 0; i < numExtra; i++) h.ExtraChannels[i] = JxlExtraChannel.Read(br);
                h.XybEncoded = br.ReadBool();
                h.Colour = JxlColourEncoding.Read(br);
                if (extraFields) h.ToneMapping = JxlToneMapping.Read(br);
                br.SkipExtensions();
            }
            // CustomTransformData
            if (!br.ReadBool())
            {
                if (h.XybEncoded) h.Opsin = JxlOpsinParams.Read(br);
                int mask = (int)br.ReadBits(3);
                if ((mask & 1) != 0) h.Up2Weights = ReadF16s(br, 15);
                if ((mask & 2) != 0) h.Up4Weights = ReadF16s(br, 55);
                if ((mask & 4) != 0) h.Up8Weights = ReadF16s(br, 210);
            }
            br.CheckEnd();
            return h;
        }

        static float[] ReadF16s(JxlBitReader br, int n)
        {
            var a = new float[n];
            for (int i = 0; i < n; i++) a[i] = br.ReadF16();
            return a;
        }
    }

    internal struct JxlBlendingInfo
    {
        public JxlBlendMode Mode;
        public int AlphaChannel;
        public bool Clamp;
        public int Source;

        public static JxlBlendingInfo Read(JxlBitReader br, int numExtra, bool fullFrame)
        {
            var b = new JxlBlendingInfo();
            uint mode = br.ReadU32(Val(0), Val(1), Val(2), BitsOffset(2, 3));
            if (mode > 4) throw new ImageFormatException("JPEG XL: bad blend mode");
            b.Mode = (JxlBlendMode)mode;
            if (numExtra > 0 && (b.Mode == JxlBlendMode.Blend || b.Mode == JxlBlendMode.AlphaWeightedAdd))
            {
                b.AlphaChannel = (int)br.ReadU32(Val(0), Val(1), Val(2), BitsOffset(3, 3));
                if (b.AlphaChannel >= numExtra) throw new ImageFormatException("JPEG XL: blend alpha channel out of range");
            }
            if (numExtra > 0 && (b.Mode == JxlBlendMode.Blend || b.Mode == JxlBlendMode.AlphaWeightedAdd) || b.Mode == JxlBlendMode.Mul)
                b.Clamp = br.ReadBool();
            if (b.Mode != JxlBlendMode.Replace || !fullFrame) b.Source = (int)br.ReadBits(2);
            return b;
        }
    }

    /// <summary>RestorationFilter: Gaborish and edge preserving filter parameters.</summary>
    internal sealed class JxlRestorationFilter
    {
        public bool Gab = true;
        public float[] GabWeights = { 0.115169525f, 0.061248592f, 0.115169525f, 0.061248592f, 0.115169525f, 0.061248592f };
        public int EpfIters = 2;
        public float[] EpfSharpLut = { 0, 1 / 7f, 2 / 7f, 3 / 7f, 4 / 7f, 5 / 7f, 6 / 7f, 1 };
        public float[] EpfChannelScale = { 40.0f, 5.0f, 3.5f };
        public float EpfQuantMul = 0.46f, EpfPass0SigmaScale = 0.9f, EpfPass2SigmaScale = 6.5f, EpfBorderSadMul = 2 / 3f;
        public float EpfSigmaForModular = 1.0f;

        public static JxlRestorationFilter Read(JxlBitReader br, bool modular)
        {
            var f = new JxlRestorationFilter();
            if (br.ReadBool()) return f;
            f.Gab = br.ReadBool();
            if (f.Gab && br.ReadBool())
                for (int i = 0; i < 6; i++) f.GabWeights[i] = br.ReadF16();
            f.EpfIters = (int)br.ReadBits(2);
            if (f.EpfIters > 0)
            {
                if (!modular && br.ReadBool())
                    for (int i = 0; i < 8; i++) f.EpfSharpLut[i] = br.ReadF16();
                if (br.ReadBool())
                {
                    for (int i = 0; i < 3; i++) f.EpfChannelScale[i] = br.ReadF16();
                    br.ReadBits(32);   // ignored (Table J.1)
                }
                if (br.ReadBool())
                {
                    if (!modular) f.EpfQuantMul = br.ReadF16();
                    f.EpfPass0SigmaScale = br.ReadF16();
                    f.EpfPass2SigmaScale = br.ReadF16();
                    f.EpfBorderSadMul = br.ReadF16();
                }
                if (modular) f.EpfSigmaForModular = br.ReadF16();
            }
            br.SkipExtensions();
            return f;
        }
    }

    internal sealed class JxlFrameHeader
    {
        public const ulong FlagNoise = 1, FlagPatches = 2, FlagSplines = 16, FlagUseLfFrame = 32, FlagSkipAdaptiveLfSmoothing = 128;

        public JxlFrameType Type = JxlFrameType.Regular;
        public bool Modular;
        public ulong Flags;
        public bool DoYCbCr;
        public int[] JpegUpsampling = new int[3];

        /// <summary>
        /// Chroma subsampling shift of channel c (VarDCT order: Cb, Y, Cr): the jpeg_upsampling modes are sampling factors
        /// (0 = 1x1, 1 = 2x2, 2 = 2x1, 3 = 1x2), a channel is subsampled by the maximum factor minus its own.
        /// </summary>
        public void ChromaShift(int c, out int hs, out int vs)
        {
            hs = vs = 0;
            if (!DoYCbCr) return;
            int mh = 0, mv = 0;
            for (int i = 0; i < 3; i++) { mh = Math.Max(mh, ModeH(JpegUpsampling[i])); mv = Math.Max(mv, ModeV(JpegUpsampling[i])); }
            hs = mh - ModeH(JpegUpsampling[c]);
            vs = mv - ModeV(JpegUpsampling[c]);
        }

        static int ModeH(int m) => m is 1 or 2 ? 1 : 0;
        static int ModeV(int m) => m is 1 or 3 ? 1 : 0;
        public int Upsampling = 1;
        public int[] EcUpsampling = Array.Empty<int>();
        public int GroupSizeShift = 1;
        public int XQmScale = 3, BQmScale = 2;
        public int NumPasses = 1;
        public int[] Shift = Array.Empty<int>();          // per pass except the last
        public int[] Downsample = Array.Empty<int>();
        public int[] LastPass = Array.Empty<int>();
        public int LfLevel;
        public bool HaveCrop;
        public int X0, Y0;
        public int Width, Height;     // frame size in pixels (before upsampling it is ceil(Width / Upsampling))
        public JxlBlendingInfo Blending;
        public JxlBlendingInfo[] EcBlending = Array.Empty<JxlBlendingInfo>();
        public uint Duration;
        public uint Timecode;
        public bool IsLast = true;
        public int SaveAsReference;
        public bool SaveBeforeColourTransform;
        public string Name = "";
        public JxlRestorationFilter Filter = new();

        public bool Has(ulong flag) => (Flags & flag) != 0;

        public int GroupDim => 128 << GroupSizeShift;

        /// <summary>Whether the frame (after cropping) covers the whole image.</summary>
        public bool FullFrame(JxlImageHeader img) => X0 <= 0 && Y0 <= 0 && (long)X0 + Width >= img.Width && (long)Y0 + Height >= img.Height;

        public static JxlFrameHeader Read(JxlBitReader br, JxlImageHeader img)
        {
            var f = new JxlFrameHeader();
            int numExtra = img.ExtraChannels.Length;
            f.EcUpsampling = new int[numExtra];
            f.EcBlending = new JxlBlendingInfo[numExtra];
            for (int i = 0; i < numExtra; i++) f.EcUpsampling[i] = 1;
            f.Width = img.Width; f.Height = img.Height;
            bool allDefault = br.ReadBool();
            f.XQmScale = img.XybEncoded ? 3 : 2;   // d_xqms (all_default frames are VarDCT)
            if (!allDefault)
            {
                f.Type = (JxlFrameType)br.ReadBits(2);
                f.Modular = br.ReadBits(1) == 1;
                if (f.Modular) f.XQmScale = 2;
                f.Flags = br.ReadU64();
                if (!img.XybEncoded) f.DoYCbCr = br.ReadBool();
                if (!f.Has(FlagUseLfFrame))
                {
                    if (f.DoYCbCr)
                        for (int i = 0; i < 3; i++) f.JpegUpsampling[i] = (int)br.ReadBits(2);
                    f.Upsampling = (int)br.ReadU32(Val(1), Val(2), Val(4), Val(8));
                    for (int i = 0; i < numExtra; i++) f.EcUpsampling[i] = (int)br.ReadU32(Val(1), Val(2), Val(4), Val(8));
                }
                if (f.Modular) f.GroupSizeShift = (int)br.ReadBits(2);
                if (!f.Modular && img.XybEncoded)
                {
                    f.XQmScale = (int)br.ReadBits(3);
                    f.BQmScale = (int)br.ReadBits(3);
                }
                if (f.Type != JxlFrameType.ReferenceOnly)
                {
                    f.NumPasses = (int)br.ReadU32(Val(1), Val(2), Val(3), BitsOffset(3, 4));
                    if (f.NumPasses != 1)
                    {
                        int numDs = (int)br.ReadU32(Val(0), Val(1), Val(2), BitsOffset(1, 3));
                        if (numDs >= f.NumPasses) throw new ImageFormatException("JPEG XL: too many downsampled passes");
                        f.Shift = new int[f.NumPasses - 1];
                        for (int i = 0; i < f.NumPasses - 1; i++) f.Shift[i] = (int)br.ReadBits(2);
                        f.Downsample = new int[numDs];
                        f.LastPass = new int[numDs];
                        for (int i = 0; i < numDs; i++)
                        {
                            f.Downsample[i] = (int)br.ReadU32(Val(1), Val(2), Val(4), Val(8));
                            if (i > 0 && f.Downsample[i] >= f.Downsample[i - 1]) throw new ImageFormatException("JPEG XL: downsample not decreasing");
                        }
                        for (int i = 0; i < numDs; i++)
                        {
                            f.LastPass[i] = (int)br.ReadU32(Val(0), Val(1), Val(2), Bits(3));
                            if (f.LastPass[i] >= f.NumPasses || i > 0 && f.LastPass[i] <= f.LastPass[i - 1])
                                throw new ImageFormatException("JPEG XL: bad pass description");
                        }
                    }
                }
                if (f.Type == JxlFrameType.LfFrame) f.LfLevel = 1 + (int)br.ReadBits(2);
                if (f.Type != JxlFrameType.LfFrame)
                {
                    f.HaveCrop = br.ReadBool();
                    if (f.HaveCrop)
                    {
                        if (f.Type != JxlFrameType.ReferenceOnly)
                        {
                            f.X0 = UnpackSigned(br.ReadU32(Bits(8), BitsOffset(11, 256), BitsOffset(14, 2304), BitsOffset(30, 18688)));
                            f.Y0 = UnpackSigned(br.ReadU32(Bits(8), BitsOffset(11, 256), BitsOffset(14, 2304), BitsOffset(30, 18688)));
                        }
                        uint w = br.ReadU32(Bits(8), BitsOffset(11, 256), BitsOffset(14, 2304), BitsOffset(30, 18688));
                        uint h = br.ReadU32(Bits(8), BitsOffset(11, 256), BitsOffset(14, 2304), BitsOffset(30, 18688));
                        if (w == 0 || h == 0 || w > (1u << 30) || h > (1u << 30)) throw new ImageFormatException("JPEG XL: bad frame size");
                        f.Width = (int)w; f.Height = (int)h;
                    }
                }
                bool full = f.FullFrame(img);
                if (f.Type == JxlFrameType.Regular || f.Type == JxlFrameType.SkipProgressive)
                {
                    f.Blending = JxlBlendingInfo.Read(br, numExtra, full);
                    for (int i = 0; i < numExtra; i++) f.EcBlending[i] = JxlBlendingInfo.Read(br, numExtra, full);
                    if (img.Animation != null)
                    {
                        f.Duration = br.ReadU32(Val(0), Val(1), Bits(8), Bits(32));
                        if (img.Animation.HaveTimecodes) f.Timecode = br.ReadBits(32);
                    }
                    f.IsLast = br.ReadBool();
                }
                else f.IsLast = false;
                if (f.Type != JxlFrameType.LfFrame && !f.IsLast) f.SaveAsReference = (int)br.ReadBits(2);
                if (f.Type == JxlFrameType.ReferenceOnly
                    || full && (f.Type == JxlFrameType.Regular || f.Type == JxlFrameType.SkipProgressive)
                       && f.Blending.Mode == JxlBlendMode.Replace && (f.Duration == 0 || f.SaveAsReference != 0) && !f.IsLast)
                    f.SaveBeforeColourTransform = br.ReadBool();
                else f.SaveBeforeColourTransform = f.Type == JxlFrameType.LfFrame;
                f.Name = JxlHeaderUtil.ReadName(br);
                f.Filter = JxlRestorationFilter.Read(br, f.Modular);
                br.SkipExtensions();
            }
            // D.3.6: 1 << dim_shift does not exceed the group_dim of any frame
            foreach (var e in img.ExtraChannels)
                if (1 << e.DimShift > f.GroupDim) throw new ImageFormatException("JPEG XL: extra channel dim_shift exceeds group_dim");
            if (f.Type == JxlFrameType.LfFrame)
            {
                // an LF frame holds the DC of the frames using it: 1 / 8^level of the image size
                int div = 1 << (3 * f.LfLevel);
                f.Width = (int)(((long)img.Width + div - 1) / div);
                f.Height = (int)(((long)img.Height + div - 1) / div);
                f.X0 = f.Y0 = 0;
            }
            br.CheckEnd();
            return f;
        }
    }
}
