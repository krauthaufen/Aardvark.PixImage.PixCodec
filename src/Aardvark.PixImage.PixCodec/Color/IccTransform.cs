using System;
using System.Collections.Generic;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// A colour transform from one ICC profile to another for a rendering intent, optionally with black point compensation.
    /// Device values are normalised to 0..1 (for CMYK 0 = no ink); profiles whose data colour space is Lab or XYZ take
    /// their values in the ICC encoding normalised to 0..1. Build it once and use it from any number of threads.
    /// <para>
    /// Matrix / TRC profiles combine into curves, one 3x3 matrix and inverse curves (exactly rounded for integer output);
    /// LUT based profiles run their curves, matrices and CLUTs (tetrahedral interpolation, or trilinear) in single
    /// precision. The connection goes through XYZ or CIE Lab relative to D50; absolute colorimetric scales by the media
    /// white points, black point compensation follows the ISO 18619 scaling in XYZ.
    /// </para>
    /// </summary>
    public sealed partial class IccTransform
    {
        public IccProfile Source { get; }
        public IccProfile Destination { get; }
        public IccRenderingIntent Intent { get; }
        public bool BlackPointCompensation { get; }
        /// <summary>Device channels of the source / destination (without alpha).</summary>
        public int InputChannels { get; }
        public int OutputChannels { get; }

        internal readonly List<IccStage> Stages;
        internal readonly int Stride;

        /// <summary>Builds the transform; throws <see cref="IccException"/> when a profile cannot serve (a device link, no
        /// usable tags) and <see cref="ImageFormatException"/> when a needed tag is malformed.</summary>
        public IccTransform(IccProfile source, IccProfile destination, IccRenderingIntent intent = IccRenderingIntent.Perceptual,
            bool blackPointCompensation = false, bool trilinear = false)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(destination);
            if ((uint)intent > 3) throw new ArgumentOutOfRangeException(nameof(intent));
            Source = source; Destination = destination; Intent = intent; BlackPointCompensation = blackPointCompensation;
            Check(source); Check(destination);
            InputChannels = source.Channels;
            OutputChannels = destination.Channels;
            var stages = new List<IccStage>();
            stages.AddRange(ToPcs(source, intent, trilinear, out var srcPcs));
            var back = FromPcs(destination, intent, trilinear, out var dstPcs);
            Connect(stages, srcPcs, dstPcs, intent, blackPointCompensation, trilinear);
            stages.AddRange(back);
            Stages = IccPipeline.Optimize(stages);
            Stride = Math.Max(IccPipeline.MaxChannels(Stages, InputChannels), 1);
        }

        static void Check(IccProfile p)
        {
            if (p.Class is IccProfileClass.DeviceLink or IccProfileClass.NamedColor)
                throw new IccException($"ICC: {p.Class} profiles are not supported");
            if (p.Channels == 0) throw new IccException($"ICC: colour space '{IccSig.ToText((uint)p.ColorSpace)}' is not supported");
            if (p.Pcs is not (IccColorSpace.Xyz or IccColorSpace.Lab)) throw new IccException("ICC: the PCS must be XYZ or Lab");
        }

        // ------------------------------------------------------------------ profile sides

        /// <summary>The LUT tag used for an intent: the intent's own, else the perceptual one; 0 when there is none.</summary>
        static uint PickLut(IccProfile p, IccRenderingIntent intent, bool toPcs)
        {
            int n = intent == IccRenderingIntent.AbsoluteColorimetric ? 1 : (int)intent;
            uint sig = IccProfile.LutSig(n, toPcs);
            if (p.HasTag(sig)) return sig;
            sig = IccProfile.LutSig(0, toPcs);
            return p.HasTag(sig) ? sig : 0;
        }

        /// <summary>Whether the profile works through a LUT for this intent (and direction).</summary>
        internal static bool UsesLut(IccProfile p, IccRenderingIntent intent, bool toPcs) => PickLut(p, intent, toPcs) != 0;

        /// <summary>Device (normalised) to real PCS values: XYZ (Y = 1 for the media white) or Lab (L 0..100).</summary>
        internal static List<IccStage> ToPcs(IccProfile p, IccRenderingIntent intent, bool trilinear, out IccColorSpace pcs)
        {
            var s = new List<IccStage>();
            uint sig = PickLut(p, intent, true);
            if (sig != 0)
            {
                pcs = p.Pcs;
                s.AddRange(IccLut.Read(p.Tag(sig), p.ColorSpace == IccColorSpace.Xyz, out int ni, out int no, out var kind, trilinear));
                if (ni != p.Channels || no != 3) throw IccRead.Bad($"'{IccSig.ToText(sig)}' has {ni} -> {no} channels");
                s.Add(PcsDecode(pcs, kind));
                return s;
            }
            if (p.ColorSpace == IccColorSpace.Rgb && p.IsMatrixShaper)
            {
                pcs = IccColorSpace.Xyz;
                s.Add(new CurvesStage(new[] { p.RedTrc!, p.GreenTrc!, p.BlueTrc! }));
                s.Add(new AffineStage(3, 3, ColorantMatrix(p)));
                return s;
            }
            if (p.ColorSpace == IccColorSpace.Gray && p.IsMatrixShaper)
            {
                s.Add(new CurvesStage(new[] { p.GrayTrc! }));
                if (p.Pcs == IccColorSpace.Lab)
                {
                    pcs = IccColorSpace.Lab;
                    s.Add(new AffineStage(1, 3, new double[] { 100, 0, 0 }));
                }
                else
                {
                    pcs = IccColorSpace.Xyz;
                    s.Add(new AffineStage(1, 3, new[] { IccMath.D50X, IccMath.D50Y, IccMath.D50Z }));
                }
                return s;
            }
            throw new IccException($"ICC: '{p.Description}' has neither an A2B table nor matrix / TRC tags");
        }

        /// <summary>Real PCS values to device values.</summary>
        internal static List<IccStage> FromPcs(IccProfile p, IccRenderingIntent intent, bool trilinear, out IccColorSpace pcs)
        {
            var s = new List<IccStage>();
            uint sig = PickLut(p, intent, false);
            if (sig != 0)
            {
                pcs = p.Pcs;
                // CLUTs indexed by Lab are interpolated trilinearly: the tetrahedra of a Lab grid do not follow its neutral axis
                var lut = IccLut.Read(p.Tag(sig), p.Pcs == IccColorSpace.Xyz, out int ni, out int no, out var kind,
                    trilinear || p.Pcs == IccColorSpace.Lab);
                if (ni != 3 || no != p.Channels) throw IccRead.Bad($"'{IccSig.ToText(sig)}' has {ni} -> {no} channels");
                s.Add(PcsEncode(pcs, kind));
                s.AddRange(lut);
                return s;
            }
            if (p.ColorSpace == IccColorSpace.Rgb && p.IsMatrixShaper)
            {
                pcs = IccColorSpace.Xyz;
                var inv = IccMath.Invert(ColorantMatrix(p)) ?? throw IccRead.Bad("the colorant matrix is singular");
                s.Add(new AffineStage(3, 3, inv));
                s.Add(new InverseCurvesStage(new[] { p.RedTrc!, p.GreenTrc!, p.BlueTrc! }));
                return s;
            }
            if (p.ColorSpace == IccColorSpace.Gray && p.IsMatrixShaper)
            {
                if (p.Pcs == IccColorSpace.Lab)
                {
                    pcs = IccColorSpace.Lab;
                    s.Add(new AffineStage(3, 1, new[] { 0.01, 0, 0 }));
                }
                else
                {
                    pcs = IccColorSpace.Xyz;
                    s.Add(new AffineStage(3, 1, new double[] { 0, 1, 0 }));
                }
                s.Add(new InverseCurvesStage(new[] { p.GrayTrc! }));
                return s;
            }
            throw new IccException($"ICC: '{p.Description}' has neither a B2A table nor matrix / TRC tags");
        }

        static double[] ColorantMatrix(IccProfile p)
        {
            var r = p.RedColorant!.Value; var g = p.GreenColorant!.Value; var b = p.BlueColorant!.Value;
            return new[] { r.X, g.X, b.X, r.Y, g.Y, b.Y, r.Z, g.Z, b.Z };
        }

        // PCS encodings of the LUT types (ICC.1 6.3.4): lutAToB / lutBToA and lut8 use the v4 Lab encoding, lut16 the legacy one
        // (0xFF00 = L 100, 0x8000 = a 0); XYZ is u1Fixed15 (0x8000 = 1.0).
        const double LegacyL = 100 * 65535.0 / 65280, LegacyAb = 65535.0 / 256, XyzScale = 65535.0 / 32768;

        static AffineStage PcsDecode(IccColorSpace pcs, IccLutKind kind)
        {
            if (pcs == IccColorSpace.Xyz) return AffineStage.Diagonal(new[] { XyzScale, XyzScale, XyzScale }, new double[3]);
            if (kind == IccLutKind.Lut16) return AffineStage.Diagonal(new[] { LegacyL, LegacyAb, LegacyAb }, new double[] { 0, -128, -128 });
            return AffineStage.Diagonal(new double[] { 100, 255, 255 }, new double[] { 0, -128, -128 });
        }

        static AffineStage PcsEncode(IccColorSpace pcs, IccLutKind kind)
        {
            if (pcs == IccColorSpace.Xyz) return AffineStage.Diagonal(new[] { 1 / XyzScale, 1 / XyzScale, 1 / XyzScale }, new double[3]);
            if (kind == IccLutKind.Lut16) return AffineStage.Diagonal(new[] { 1 / LegacyL, 1 / LegacyAb, 1 / LegacyAb }, new[] { 0, 128 / LegacyAb, 128 / LegacyAb });
            return AffineStage.Diagonal(new double[] { 0.01, 1 / 255.0, 1 / 255.0 }, new[] { 0, 128 / 255.0, 128 / 255.0 });
        }

        // ------------------------------------------------------------------ PCS connection

        void Connect(List<IccStage> s, IccColorSpace from, IccColorSpace to, IccRenderingIntent intent, bool bpc, bool trilinear)
        {
            double[]? scale = null, offset = null;
            if (intent == IccRenderingIntent.AbsoluteColorimetric)
            {
                var ws = Source.MediaWhitePoint; var wd = Destination.MediaWhitePoint;
                scale = new[] { ws.X / wd.X, ws.Y / wd.Y, ws.Z / wd.Z };
                offset = new double[3];
            }
            else if (bpc || AutoBpc(intent))
            {
                var bs = BlackPoint(Source, intent, true, trilinear);
                var bd = BlackPoint(Destination, intent, false, trilinear);
                if (bs != bd)
                {
                    // ISO 18619: XYZ' = (XYZ - Bs) (W - Bd) / (W - Bs) + Bd per component, W = D50
                    double[] w = { IccMath.D50X, IccMath.D50Y, IccMath.D50Z }, b1 = { bs.X, bs.Y, bs.Z }, b2 = { bd.X, bd.Y, bd.Z };
                    scale = new double[3]; offset = new double[3];
                    for (int i = 0; i < 3; i++)
                    {
                        scale[i] = (w[i] - b2[i]) / (w[i] - b1[i]);
                        offset[i] = b2[i] - b1[i] * scale[i];
                    }
                }
            }
            if (scale == null)
            {
                if (from == to) return;
                s.Add(from == IccColorSpace.Lab ? new LabToXyzStage() : new XyzToLabStage());
                return;
            }
            if (from == IccColorSpace.Lab) s.Add(new LabToXyzStage());
            s.Add(AffineStage.Diagonal(scale, offset!));
            if (to == IccColorSpace.Lab) s.Add(new XyzToLabStage());
        }

        /// <summary>
        /// The perceptual and saturation intents of ICC v4 work with the perceptual reference medium black: when the
        /// destination is a v4 profile, the source black is scaled to the destination's (a no-op between two black points at
        /// zero, e.g. two matrix / TRC profiles). This is also what LittleCMS does.
        /// </summary>
        bool AutoBpc(IccRenderingIntent intent)
        {
            if (intent is not (IccRenderingIntent.Perceptual or IccRenderingIntent.Saturation)) return false;
            return Destination.IsV4;
        }

        /// <summary>The perceptual reference medium black of ICC v4 (ICC.1 6.3.4.3, Y = 0.0034731), made neutral.</summary>
        static readonly CieXyz PerceptualBlack = new(0.0034731 * IccMath.D50X, 0.0034731, 0.0034731 * IccMath.D50Z);

        /// <summary>
        /// The neutral black point of a profile for an intent (after ISO 18619): the v4 perceptual reference black for v4
        /// perceptual / saturation tables; for the colorimetric intents of output (CMYK) profiles with B2A tables the darkest
        /// reproducible colour (Lab 0 through the B2A and back through the A2B, which honours ink limits); else device black
        /// (no light, or full ink) through the A2B / TRCs. a* and b* are dropped, L* is limited to 50.
        /// </summary>
        internal static CieXyz BlackPoint(IccProfile p, IccRenderingIntent intent, bool asSource, bool trilinear)
        {
            if (intent is IccRenderingIntent.Perceptual or IccRenderingIntent.Saturation && p.IsV4
                && (UsesLut(p, intent, true) || UsesLut(p, intent, false)))
                return PerceptualBlack;
            float[] device;
            bool corner = false;
            bool subtractive = p.ColorSpace is IccColorSpace.Cmyk or IccColorSpace.Cmy;
            bool colorimetric = intent is IccRenderingIntent.RelativeColorimetric or IccRenderingIntent.AbsoluteColorimetric;
            if (colorimetric && subtractive
                && UsesLut(p, IccRenderingIntent.RelativeColorimetric, false) && UsesLut(p, intent, true))
            {
                var back = FromPcs(p, IccRenderingIntent.RelativeColorimetric, trilinear, out var pcs);
                if (pcs == IccColorSpace.Xyz) back.Insert(0, new LabToXyzStage());
                device = IccPipeline.Eval(back, new float[] { 0, 0, 0 });
            }
            else if (p.ColorSpace == IccColorSpace.Lab)
                device = LabBlack(p, intent);
            else
            {
                // ISO 18619 4.2.2.2 defines D as the darkest of a vertex set ({0, 1}, {000, 111}, {0000, 1111, 0001, 1110}),
                // whatever the polarity; LittleCMS takes device black alone (no light, or full ink), gives no compensation (black
                // 0) when that is lighter than L* 50 instead of limiting it to 50, and none for an intent whose own A2B table the
                // profile lacks (IccBpcTests.DeviceBlackAsLittleCms, against PIL.ImageCms). We follow LittleCMS.
                if (!HasIntent(p, intent)) return default;
                corner = true;
                device = new float[p.Channels];
                if (subtractive) Array.Fill(device, 1f);
            }
            var fwd = ToPcs(p, intent == IccRenderingIntent.AbsoluteColorimetric ? IccRenderingIntent.RelativeColorimetric : intent, trilinear, out var fpcs);
            if (fpcs == IccColorSpace.Xyz) fwd.Add(new XyzToLabStage());
            var lab = IccPipeline.Eval(fwd, device);
            if (corner && !(lab[0] <= 50)) return default;
            double l = Math.Clamp((double)lab[0], 0, 50);
            var (x, y, z) = IccMath.LabToXyz(l, 0, 0);
            return new CieXyz(x, y, z);
        }

        /// <summary>Whether the profile serves the intent in LittleCMS's sense: matrix / TRC profiles every intent, LUT profiles
        /// those with their own A2B table.</summary>
        static bool HasIntent(IccProfile p, IccRenderingIntent intent) => p.IsMatrixShaper || p.HasLut(intent, true);

        /// <summary>Lab (0, 0, 0) as the normalised device values of a profile with a Lab data colour space (ISO 18619 4.2.3):
        /// a* = b* = 0 is 0x8000 in the lut16 encoding, 128 / 255 in the others (ICC.1 6.3.4).</summary>
        static float[] LabBlack(IccProfile p, IccRenderingIntent intent)
        {
            uint sig = PickLut(p, intent, true);
            float ab = sig != 0 && IccRead.U32(p.Tag(sig), 0) == IccSig.TMft2 ? 32768f / 65535 : 128f / 255;
            return new[] { 0f, ab, ab };
        }

        // ------------------------------------------------------------------ evaluation

        /// <summary>
        /// Converts pixels of normalised device values: <paramref name="input"/> holds <see cref="InputChannels"/> values per
        /// pixel, <paramref name="output"/> receives <see cref="OutputChannels"/> per pixel. Values are not clamped beyond what
        /// the profiles imply (LUTs clamp their inputs to 0..1, matrix / TRC profiles extend their curves).
        /// </summary>
        public void Transform(ReadOnlySpan<float> input, Span<float> output)
        {
            int ni = InputChannels, no = OutputChannels;
            if (input.Length % ni != 0) throw new ArgumentException("input length must be a multiple of the input channels");
            int n = input.Length / ni;
            if (output.Length < n * no) throw new ArgumentException("output is too small");
            const int chunk = 256;
            var buf = new float[chunk * Stride];
            for (int start = 0; start < n; start += chunk)
            {
                int cnt = Math.Min(chunk, n - start);
                for (int i = 0; i < cnt; i++)
                    for (int c = 0; c < ni; c++) buf[i * Stride + c] = input[(start + i) * ni + c];
                foreach (var st in Stages) st.Apply(buf, cnt, Stride);
                for (int i = 0; i < cnt; i++)
                    for (int c = 0; c < no; c++) output[(start + i) * no + c] = buf[i * Stride + c];
            }
        }

        /// <summary>Converts one colour (normalised device values).</summary>
        public float[] Transform(params float[] color)
        {
            ArgumentNullException.ThrowIfNull(color);
            if (color.Length != InputChannels) throw new ArgumentException($"{InputChannels} values expected");
            var r = new float[OutputChannels];
            Transform(color, r);
            return r;
        }

        bool? identity;

        /// <summary>
        /// True when the transform does not change colours (within 1e-4 on a grid of test colours), e.g. between two
        /// sRGB profiles.
        /// </summary>
        public bool IsIdentity => identity ??= ComputeIdentity();

        bool ComputeIdentity()
        {
            if (InputChannels != OutputChannels || Source.ColorSpace != Destination.ColorSpace) return false;
            int n = InputChannels;
            var pts = new List<float>();
            for (int c = 0; c < n; c++)
                for (int k = 0; k <= 64; k++)
                {
                    var v = new float[n];
                    v[c] = k / 64f;
                    pts.AddRange(v);
                }
            if (n <= 4)
            {
                int total = 1;
                for (int c = 0; c < n; c++) total *= 5;
                for (int i = 0; i < total; i++)
                {
                    int q = i;
                    for (int c = 0; c < n; c++) { pts.Add(q % 5 / 4f); q /= 5; }
                }
            }
            var input = pts.ToArray();
            var output = new float[input.Length];
            Transform(input, output);
            for (int i = 0; i < input.Length; i++) if (!(Math.Abs(output[i] - input[i]) <= 1e-4)) return false;
            return true;
        }
    }
}
