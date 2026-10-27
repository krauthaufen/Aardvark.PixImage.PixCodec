using System;
using System.Collections.Generic;
using System.Text;

namespace Aardvark.ImageCodecs
{
    public sealed partial class IccProfile
    {
        // ------------------------------------------------------------------ standard profiles

        static IccProfile? srgb, linearSrgb, p3, rec2020, linearRec2020, prophoto, a98, gray22, graySrgb, grayLinear;

        /// <summary>sRGB (IEC 61966-2-1): D65, the Rec. 709 primaries and the sRGB curve, as an ICC v4 matrix / TRC profile.</summary>
        public static IccProfile Srgb => srgb ??= CreateRgb("sRGB", CieXy.D65, Rec709R, Rec709G, Rec709B, IccCurve.Srgb);
        /// <summary>sRGB primaries and white with a linear transfer function (scene linear / "linear sRGB", scRGB without the offset).</summary>
        public static IccProfile LinearSrgb => linearSrgb ??= CreateRgb("Linear sRGB", CieXy.D65, Rec709R, Rec709G, Rec709B, IccCurve.Identity);
        /// <summary>Display P3: DCI-P3 primaries, D65 white and the sRGB curve.</summary>
        public static IccProfile DisplayP3 => p3 ??= CreateRgb("Display P3", CieXy.D65,
            new CieXy(0.680, 0.320), new CieXy(0.265, 0.690), new CieXy(0.150, 0.060), IccCurve.Srgb);
        /// <summary>Rec. ITU-R BT.2020 primaries and white with the BT.2020 / BT.709 curve (inverse of the camera OETF).</summary>
        public static IccProfile Rec2020 => rec2020 ??= CreateRgb("Rec. ITU-R BT.2020", CieXy.D65, Rec2020R, Rec2020G, Rec2020B, Bt709Curve);
        /// <summary>Rec. ITU-R BT.2020 primaries and white, linear.</summary>
        public static IccProfile LinearRec2020 => linearRec2020 ??= CreateRgb("Linear Rec. ITU-R BT.2020", CieXy.D65, Rec2020R, Rec2020G, Rec2020B, IccCurve.Identity);
        /// <summary>ROMM RGB (ISO 22028-2, known as ProPhoto RGB): D50 white, gamma 1.8 with the linear segment below 1/512.</summary>
        public static IccProfile ProPhotoRgb => prophoto ??= CreateRgb("ROMM RGB (ProPhoto)", CieXy.D50,
            new CieXy(0.7347, 0.2653), new CieXy(0.1596, 0.8404), new CieXy(0.0366, 0.0001), IccCurve.Parametric(3, 1.8, 1, 0, 1 / 16.0, 1 / 32.0));
        /// <summary>
        /// An RGB space with the primaries, white and gamma (563/256) of Adobe RGB (1998), named "A98 RGB" as CSS Color 4 does.
        /// It is built here from the published parameters and is not Adobe's profile.
        /// </summary>
        public static IccProfile A98Rgb => a98 ??= CreateRgb("A98 RGB (compatible)", CieXy.D65,
            new CieXy(0.64, 0.33), new CieXy(0.21, 0.71), new CieXy(0.15, 0.06), IccCurve.Gamma(563 / 256.0));
        /// <summary>Gray with a gamma 2.2 power law (D65 white).</summary>
        public static IccProfile GrayGamma22 => gray22 ??= CreateGray("Gray gamma 2.2", CieXy.D65, IccCurve.Gamma(2.2));
        /// <summary>Gray with the sRGB curve (D65 white), what 8 bit gray images without a profile are taken as.</summary>
        public static IccProfile GraySrgb => graySrgb ??= CreateGray("Gray sRGB", CieXy.D65, IccCurve.Srgb);
        /// <summary>Linear gray (D65 white).</summary>
        public static IccProfile GrayLinear => grayLinear ??= CreateGray("Gray linear", CieXy.D65, IccCurve.Identity);

        static CieXy Rec709R => new(0.64, 0.33);
        static CieXy Rec709G => new(0.30, 0.60);
        static CieXy Rec709B => new(0.15, 0.06);
        static CieXy Rec2020R => new(0.708, 0.292);
        static CieXy Rec2020G => new(0.170, 0.797);
        static CieXy Rec2020B => new(0.131, 0.046);
        static IccCurve Bt709Curve => IccCurve.Parametric(3, 1 / 0.45, 1 / 1.099, 0.099 / 1.099, 1 / 4.5, 0.081);

        const string NoCopyright = "No copyright, use freely";

        /// <summary>
        /// Builds an RGB display profile (matrix / TRC) from the chromaticities of white and the primaries and a transfer curve
        /// per channel (one curve for all three when only <paramref name="red"/>'s curve is given). The colorants are adapted
        /// to D50 with the Bradford transform, which is stored as the chad tag. <paramref name="version"/> 4 writes
        /// parametric curves and multi-localised texts, 2 writes curv tables / gammas and text descriptions.
        /// </summary>
        public static IccProfile CreateRgb(string description, CieXy white, CieXy red, CieXy green, CieXy blue, IccCurve trc, int version = 4)
            => CreateRgb(description, white, red, green, blue, trc, trc, trc, version);

        /// <inheritdoc cref="CreateRgb(string, CieXy, CieXy, CieXy, CieXy, IccCurve, int)"/>
        public static IccProfile CreateRgb(string description, CieXy white, CieXy red, CieXy green, CieXy blue,
            IccCurve redTrc, IccCurve greenTrc, IccCurve blueTrc, int version = 4)
            => CreateRgb(description, white, red, green, blue, redTrc, greenTrc, blueTrc, version, null);

        static IccProfile CreateRgb(string description, CieXy white, CieXy red, CieXy green, CieXy blue,
            IccCurve redTrc, IccCurve greenTrc, IccCurve blueTrc, int version, IccCicp? cicp)
        {
            ArgumentNullException.ThrowIfNull(redTrc); ArgumentNullException.ThrowIfNull(greenTrc); ArgumentNullException.ThrowIfNull(blueTrc);
            CheckVersion(version);
            bool v2 = version == 2;
            var chad = IccMath.BradfordAdaptation(white.ToXyz(), CieXyz.D50);
            var m = IccMath.Mul(chad, IccMath.RgbToXyz(white, red, green, blue));
            var w = new List<(uint, byte[])>();
            AddTexts(w, description, v2);
            w.Add((IccSig.Wtpt, XyzTag(CieXyz.D50)));
            w.Add((IccSig.Chad, Sf32Tag(chad)));
            w.Add((IccSig.RXyz, XyzTag(new CieXyz(m[0], m[3], m[6]))));
            w.Add((IccSig.GXyz, XyzTag(new CieXyz(m[1], m[4], m[7]))));
            w.Add((IccSig.BXyz, XyzTag(new CieXyz(m[2], m[5], m[8]))));
            var rc = CurveTag(redTrc, v2);
            var gc = greenTrc == redTrc ? rc : CurveTag(greenTrc, v2);
            var bc = blueTrc == redTrc ? rc : blueTrc == greenTrc ? gc : CurveTag(blueTrc, v2);
            w.Add((IccSig.RTrc, rc));
            w.Add((IccSig.GTrc, gc));
            w.Add((IccSig.BTrc, bc));
            if (cicp is { } cc) w.Add((IccSig.Cicp, CicpTag(cc)));
            return new IccProfile(Assemble(IccProfileClass.Display, IccColorSpace.Rgb, IccColorSpace.Xyz, version, w, cicp != null));
        }

        /// <summary>Builds a gray display profile (kTRC) with the given white and transfer curve.</summary>
        public static IccProfile CreateGray(string description, CieXy white, IccCurve trc, int version = 4) => CreateGray(description, white, trc, version, null);

        static IccProfile CreateGray(string description, CieXy white, IccCurve trc, int version, IccCicp? cicp)
        {
            ArgumentNullException.ThrowIfNull(trc);
            CheckVersion(version);
            bool v2 = version == 2;
            var w = new List<(uint, byte[])>();
            AddTexts(w, description, v2);
            w.Add((IccSig.Wtpt, XyzTag(CieXyz.D50)));
            w.Add((IccSig.Chad, Sf32Tag(IccMath.BradfordAdaptation(white.ToXyz(), CieXyz.D50))));
            w.Add((IccSig.KTrc, CurveTag(trc, v2)));
            if (cicp is { } cc) w.Add((IccSig.Cicp, CicpTag(cc)));
            return new IccProfile(Assemble(IccProfileClass.Display, IccColorSpace.Gray, IccColorSpace.Xyz, version, w, cicp != null));
        }

        /// <summary>
        /// A version 4.4 profile for an H.273 colour encoding with the PQ (transfer characteristics 16) or HLG (18) curve,
        /// which ICC curves cannot say: the primaries and white as usual, a cicp tag with the code points (primaries
        /// <paramref name="primaries"/>, 2 when they have no code; matrix 0, full range) and, for colour management that
        /// ignores the cicp tag, sampled curves (PQ: the EOTF relative to 10000 cd/m², HLG: the inverse OETF, scene light).
        /// Null for other transfers.
        /// </summary>
        internal static IccProfile? CreateHdr(string description, CieXy white, CieXy red, CieXy green, CieXy blue, bool gray, int primaries, int transfer)
        {
            Func<double, double> f;
            if (transfer == 16)
            {
                const double m1 = 2610.0 / 16384, m2 = 2523.0 / 4096 * 128, c1 = 3424.0 / 4096, c2 = 2413.0 / 4096 * 32, c3 = 2392.0 / 4096 * 32;
                f = e => { double p = Math.Pow(e, 1 / m2); return Math.Pow(Math.Max(p - c1, 0) / (c2 - c3 * p), 1 / m1); };
            }
            else if (transfer == 18)
            {
                const double a = 0.17883277, b = 0.28466892, c = 0.55991073;
                f = e => e <= 0.5 ? e * e / 3 : (Math.Exp((e - c) / a) + b) / 12;
            }
            else return null;
            var t = new ushort[4096];
            for (int i = 0; i < t.Length; i++) t[i] = (ushort)Math.Round(Math.Clamp(f(i / (t.Length - 1.0)), 0, 1) * 65535);
            var trc = IccCurve.Table(t);
            var cicp = new IccCicp(primaries, transfer, 0, true);
            return gray ? CreateGray(description, white, trc, 4, cicp) : CreateRgb(description, white, red, green, blue, trc, trc, trc, 4, cicp);
        }

        static byte[] CicpTag(IccCicp c)
        {
            var w = new IccWrite();
            w.Sig(IccSig.Cicp); w.U32(0);
            w.U8((byte)c.ColourPrimaries); w.U8((byte)c.TransferCharacteristics); w.U8((byte)c.MatrixCoefficients); w.U8(c.FullRange ? (byte)1 : (byte)0);
            return w.ToArray();
        }

        static void CheckVersion(int version)
        {
            if (version != 2 && version != 4) throw new ArgumentOutOfRangeException(nameof(version), "ICC version 2 or 4");
        }

        static void AddTexts(List<(uint, byte[])> w, string description, bool v2)
        {
            w.Add((IccSig.Desc, v2 ? DescTag(description ?? "") : MlucTag(description ?? "")));
            w.Add((IccSig.Cprt, v2 ? TextTag(NoCopyright) : MlucTag(NoCopyright)));
        }

        static byte[] XyzTag(CieXyz v)
        {
            var w = new IccWrite();
            w.Sig(IccSig.TXyz); w.U32(0); w.Xyz(v);
            return w.ToArray();
        }

        static byte[] Sf32Tag(double[] m)
        {
            var w = new IccWrite();
            w.Sig(IccSig.TSf32); w.U32(0);
            foreach (var v in m) w.S15(v);
            return w.ToArray();
        }

        static byte[] CurveTag(IccCurve c, bool v2)
        {
            var w = new IccWrite();
            c.Quantized(v2).Write(w, v2);
            return w.ToArray();
        }

        static byte[] MlucTag(string s)
        {
            var w = new IccWrite();
            var u = Encoding.BigEndianUnicode.GetBytes(s);
            w.Sig(IccSig.TMluc); w.U32(0); w.U32(1); w.U32(12);
            w.U8('e'); w.U8('n'); w.U8('U'); w.U8('S'); w.U32((uint)u.Length); w.U32(28);
            w.Bytes(u);
            return w.ToArray();
        }

        static byte[] DescTag(string s)
        {
            var w = new IccWrite();
            var a = Ascii(s);
            w.Sig(IccSig.TDesc); w.U32(0); w.U32((uint)a.Length + 1); w.Bytes(a); w.U8(0);
            w.U32(0); w.U32(0);         // Unicode language and count
            w.U16(0); w.U8(0); w.Zeros(67); // ScriptCode
            return w.ToArray();
        }

        static byte[] TextTag(string s)
        {
            var w = new IccWrite();
            w.Sig(IccSig.TText); w.U32(0); w.Bytes(Ascii(s)); w.U8(0);
            return w.ToArray();
        }

        static byte[] Ascii(string s)
        {
            var b = new byte[s.Length];
            for (int i = 0; i < s.Length; i++) b[i] = s[i] is >= ' ' and <= '~' ? (byte)s[i] : (byte)'?';
            return b;
        }

        /// <summary>Header, tag directory and tag data; identical tag arrays (by reference) share their data.</summary>
        static byte[] Assemble(IccProfileClass cls, IccColorSpace cs, IccColorSpace pcs, int version, List<(uint Sig, byte[] Data)> tagList, bool v44 = false)
        {
            var w = new IccWrite();
            w.U32(0);                                  // size, patched below
            w.U32(0);                                  // preferred CMM
            w.U32(version == 4 ? (v44 ? 0x04400000u : 0x04300000u) : 0x02100000u);   // (the cicp tag is new in 4.4)
            w.Sig((uint)cls); w.Sig((uint)cs); w.Sig((uint)pcs);
            // a fixed creation date keeps the bytes (and the profile ID) reproducible
            w.U16(2026); w.U16(1); w.U16(1); w.U16(0); w.U16(0); w.U16(0);
            w.Sig(IccSig.Acsp);
            w.U32(0); w.U32(0); w.U32(0); w.U32(0);    // platform, flags, manufacturer, model
            w.U32(0); w.U32(0);                        // attributes
            w.U32(0);                                  // rendering intent: perceptual
            w.S15(CieXyz.D50.X); w.S15(CieXyz.D50.Y); w.S15(CieXyz.D50.Z);
            w.U32(0);                                  // creator
            w.Zeros(16);                               // profile ID, computed below
            w.Zeros(28);
            w.U32((uint)tagList.Count);
            int dir = w.Length;
            w.Zeros(12 * tagList.Count);
            var placed = new Dictionary<byte[], (int, int)>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < tagList.Count; i++)
            {
                var (sig, d) = tagList[i];
                if (!placed.TryGetValue(d, out var at))
                {
                    w.Pad4();
                    at = (w.Length, d.Length);
                    w.Bytes(d);
                    placed[d] = at;
                }
                w.SetU32(dir + 12 * i, sig);
                w.SetU32(dir + 12 * i + 4, (uint)at.Item1);
                w.SetU32(dir + 12 * i + 8, (uint)at.Item2);
            }
            w.Pad4();
            w.SetU32(0, (uint)w.Length);
            var bytes = w.ToArray();
            if (version == 4) ComputeId(bytes).CopyTo(bytes, 84);
            return bytes;
        }

        /// <summary>The profile ID of ICC.1 7.2.18: MD5 of the profile with the flags, rendering intent and ID fields zeroed.</summary>
        internal static byte[] ComputeId(ReadOnlySpan<byte> profile)
        {
            var copy = profile.ToArray();
            Array.Clear(copy, 44, 4);
            Array.Clear(copy, 64, 4);
            Array.Clear(copy, 84, 16);
            return Md5.Hash(copy);
        }
    }
}
