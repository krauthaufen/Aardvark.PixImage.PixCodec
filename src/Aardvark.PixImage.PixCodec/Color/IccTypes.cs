using System;

namespace Aardvark.ImageCodecs
{
    /// <summary>ICC rendering intents (the header and tag numbering of the ICC specification).</summary>
    public enum IccRenderingIntent
    {
        /// <summary>Gamut mapped for pleasing results (A2B0 / B2A0); for matrix / TRC profiles the same as relative colorimetric.</summary>
        Perceptual = 0,
        /// <summary>Colorimetric relative to the media white (A2B1 / B2A1).</summary>
        RelativeColorimetric = 1,
        /// <summary>Saturation preserving (A2B2 / B2A2, falling back to the perceptual tables).</summary>
        Saturation = 2,
        /// <summary>Colorimetric including the media white (relative colorimetric tables scaled by the media white points).</summary>
        AbsoluteColorimetric = 3,
    }

    /// <summary>Profile / device class of an ICC profile; the values are the header signatures.</summary>
    public enum IccProfileClass : uint
    {
        Unknown = 0,
        Input = 0x73636E72,      // 'scnr'
        Display = 0x6D6E7472,    // 'mntr'
        Output = 0x70727472,     // 'prtr'
        DeviceLink = 0x6C696E6B, // 'link'
        ColorSpace = 0x73706163, // 'spac'
        Abstract = 0x61627374,   // 'abst'
        NamedColor = 0x6E6D636C, // 'nmcl'
    }

    /// <summary>Data / connection colour spaces of ICC profiles; the values are the header signatures.</summary>
    public enum IccColorSpace : uint
    {
        Unknown = 0,
        Xyz = 0x58595A20,   // 'XYZ '
        Lab = 0x4C616220,   // 'Lab '
        Luv = 0x4C757620,   // 'Luv '
        YCbCr = 0x59436272, // 'YCbr'
        Yxy = 0x59787920,   // 'Yxy '
        Rgb = 0x52474220,   // 'RGB '
        Gray = 0x47524159,  // 'GRAY'
        Hsv = 0x48535620,   // 'HSV '
        Hls = 0x484C5320,   // 'HLS '
        Cmyk = 0x434D594B,  // 'CMYK'
        Cmy = 0x434D5920,   // 'CMY '
        Color2 = 0x32434C52, Color3 = 0x33434C52, Color4 = 0x34434C52, Color5 = 0x35434C52, Color6 = 0x36434C52,
        Color7 = 0x37434C52, Color8 = 0x38434C52, Color9 = 0x39434C52, Color10 = 0x41434C52, Color11 = 0x42434C52,
        Color12 = 0x43434C52, Color13 = 0x44434C52, Color14 = 0x45434C52, Color15 = 0x46434C52,
    }

    /// <summary>
    /// The coding-independent code points of an ICC cicp tag (ICC.1:2022, ITU-T H.273): colour primaries, transfer
    /// characteristics, matrix coefficients and the video full range flag.
    /// </summary>
    public readonly record struct IccCicp(int ColourPrimaries, int TransferCharacteristics, int MatrixCoefficients, bool FullRange);

    /// <summary>CIE 1931 XYZ tristimulus values (Y = 1 for the reference white).</summary>
    public readonly record struct CieXyz(double X, double Y, double Z)
    {
        /// <summary>The ICC profile connection space illuminant, D50 = (0.9642, 1, 0.8249).</summary>
        public static CieXyz D50 => new(0.9642, 1.0, 0.8249);
        /// <summary>The chromaticity of these values.</summary>
        public CieXy ToXy() { double s = X + Y + Z; return s == 0 ? new CieXy(0, 0) : new CieXy(X / s, Y / s); }
    }

    /// <summary>CIE 1931 xy chromaticity coordinates.</summary>
    public readonly record struct CieXy(double X, double Y)
    {
        /// <summary>D65 as used by sRGB, Display P3, Rec. 709 / 2020 and A98 RGB (0.3127, 0.3290).</summary>
        public static CieXy D65 => new(0.3127, 0.3290);
        /// <summary>D50 as used by ROMM / ProPhoto RGB (0.3457, 0.3585).</summary>
        public static CieXy D50 => new(0.3457, 0.3585);
        /// <summary>XYZ with the given luminance.</summary>
        public CieXyz ToXyz(double luminance = 1) => new(X / Y * luminance, luminance, (1 - X - Y) / Y * luminance);
    }

    /// <summary>The ICC profile cannot be used for the requested conversion (malformed tags are an <see cref="ImageFormatException"/>).</summary>
    public class IccException : ImageNotSupportedException
    {
        public IccException(string message) : base(message) { }
    }

    internal static class IccSig
    {
        public static uint Make(string s) => (uint)s[0] << 24 | (uint)s[1] << 16 | (uint)s[2] << 8 | s[3];

        public static string ToText(uint sig)
        {
            Span<char> c = stackalloc char[4];
            for (int i = 0; i < 4; i++)
            {
                char ch = (char)(sig >> (24 - 8 * i) & 0xFF);
                c[i] = ch is >= ' ' and <= '~' ? ch : '?';
            }
            return new string(c);
        }

        // tags
        public static readonly uint A2B0 = Make("A2B0"), A2B1 = Make("A2B1"), A2B2 = Make("A2B2");
        public static readonly uint B2A0 = Make("B2A0"), B2A1 = Make("B2A1"), B2A2 = Make("B2A2");
        public static readonly uint D2B0 = Make("D2B0"), B2D0 = Make("B2D0");
        public static readonly uint RXyz = Make("rXYZ"), GXyz = Make("gXYZ"), BXyz = Make("bXYZ");
        public static readonly uint RTrc = Make("rTRC"), GTrc = Make("gTRC"), BTrc = Make("bTRC"), KTrc = Make("kTRC");
        public static readonly uint Wtpt = Make("wtpt"), Bkpt = Make("bkpt"), Chad = Make("chad"), Desc = Make("desc"), Cprt = Make("cprt");
        public static readonly uint Dmnd = Make("dmnd"), Dmdd = Make("dmdd");
        /// <summary>The cicp tag (ICC.1:2022, v4.4) and its type, which have the same signature.</summary>
        public static readonly uint Cicp = Make("cicp");
        // types
        public static readonly uint TXyz = Make("XYZ "), TCurv = Make("curv"), TPara = Make("para"), TMft1 = Make("mft1"), TMft2 = Make("mft2");
        public static readonly uint TMab = Make("mAB "), TMba = Make("mBA "), TDesc = Make("desc"), TMluc = Make("mluc"), TText = Make("text");
        public static readonly uint TSf32 = Make("sf32");
        public static readonly uint Acsp = Make("acsp");

        public static int Channels(IccColorSpace cs) => cs switch
        {
            IccColorSpace.Gray => 1,
            IccColorSpace.Xyz or IccColorSpace.Lab or IccColorSpace.Luv or IccColorSpace.YCbCr or IccColorSpace.Yxy or IccColorSpace.Rgb
                or IccColorSpace.Hsv or IccColorSpace.Hls or IccColorSpace.Cmy => 3,
            IccColorSpace.Cmyk => 4,
            >= IccColorSpace.Color2 and <= IccColorSpace.Color9 => (int)((uint)cs >> 24) - '0',
            >= IccColorSpace.Color10 and <= IccColorSpace.Color15 => (int)((uint)cs >> 24) - 'A' + 10,
            _ => 0,
        };
    }
}
