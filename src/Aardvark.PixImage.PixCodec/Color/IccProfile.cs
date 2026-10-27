using System;
using System.Collections.Generic;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// An ICC colour profile (ICC.1 version 2 and 4): header, tag directory and the tags colour conversion needs
    /// (matrix / TRC, gray TRC, lut8 / lut16 / lutAToB / lutBToA tables per rendering intent, media white point,
    /// chromatic adaptation, descriptions). <see cref="Parse"/> reads embedded profiles, the static properties
    /// (<see cref="Srgb"/>, <see cref="DisplayP3"/>, ...) and <see cref="CreateRgb(string, CieXy, CieXy, CieXy, CieXy, IccCurve, int)"/> / <see cref="CreateGray"/> build
    /// profiles from primaries and transfer curves, <see cref="ToBytes"/> gives the bytes to embed. Convert colours with
    /// <see cref="IccTransform"/> or <see cref="ImageOps.ConvertColor(RawImage, IccProfile, IccProfile, IccRenderingIntent, SampleType?)"/>.
    /// Instances are immutable and thread safe.
    /// </summary>
    public sealed partial class IccProfile
    {
        readonly byte[] data;
        readonly Dictionary<uint, (int Offset, int Size)> tags = new();
        readonly List<uint> tagOrder = new();

        /// <summary>Profile version from the header (major.minor.bugfix, e.g. 4.3.0 or 2.1.0).</summary>
        public Version Version { get; }
        public IccProfileClass Class { get; }
        /// <summary>The colour space of the device side (the image data).</summary>
        public IccColorSpace ColorSpace { get; }
        /// <summary>The profile connection space: <see cref="IccColorSpace.Xyz"/> or <see cref="IccColorSpace.Lab"/>.</summary>
        public IccColorSpace Pcs { get; }
        /// <summary>The rendering intent the header suggests.</summary>
        public IccRenderingIntent RenderingIntent { get; }
        /// <summary>Creation date from the header, null when it is not a valid date.</summary>
        public DateTime? Created { get; }
        public uint PreferredCmm { get; }
        public uint Platform { get; }
        public uint Flags { get; }
        public uint Manufacturer { get; }
        public uint Model { get; }
        public ulong Attributes { get; }
        public uint Creator { get; }
        /// <summary>The header's PCS illuminant (D50 in valid profiles).</summary>
        public CieXyz Illuminant { get; }
        /// <summary>The 16 byte profile ID (MD5) of the header, all zero when not computed.</summary>
        public ReadOnlyMemory<byte> ProfileId { get; }
        /// <summary>The profile description (desc tag; English if a multi-localised text offers it), null if absent.</summary>
        public string? Description { get; }
        /// <summary>The copyright text (cprt tag), null if absent.</summary>
        public string? Copyright { get; }

        /// <summary>Channels of <see cref="ColorSpace"/> (1 gray, 3 RGB, 4 CMYK, n for nCLR), 0 if unknown.</summary>
        public int Channels => IccSig.Channels(ColorSpace);

        IccProfile(byte[] bytes)
        {
            var d = (ReadOnlySpan<byte>)bytes;
            if (d.Length < 132) throw IccRead.Bad("profile is truncated");
            uint size = IccRead.U32(d, 0);
            if (size < 132) throw IccRead.Bad("bad profile size");
            if (size > d.Length) throw IccRead.Bad("profile is truncated");
            if (IccRead.U32(d, 36) != IccSig.Acsp) throw IccRead.Bad("missing 'acsp' signature");
            data = size == d.Length ? bytes : d.Slice(0, (int)size).ToArray();
            d = data;
            PreferredCmm = IccRead.U32(d, 4);
            Version = new Version(d[8], d[9] >> 4, d[9] & 15);
            Class = (IccProfileClass)IccRead.U32(d, 12);
            ColorSpace = (IccColorSpace)IccRead.U32(d, 16);
            Pcs = (IccColorSpace)IccRead.U32(d, 20);
            try
            {
                Created = new DateTime(IccRead.U16(d, 24), IccRead.U16(d, 26), IccRead.U16(d, 28), IccRead.U16(d, 30), IccRead.U16(d, 32),
                    IccRead.U16(d, 34), DateTimeKind.Utc);
            }
            catch (ArgumentOutOfRangeException) { Created = null; }
            Platform = IccRead.U32(d, 40);
            Flags = IccRead.U32(d, 44);
            Manufacturer = IccRead.U32(d, 48);
            Model = IccRead.U32(d, 52);
            Attributes = (ulong)IccRead.U32(d, 56) << 32 | IccRead.U32(d, 60);
            RenderingIntent = (IccRenderingIntent)(IccRead.U32(d, 64) & 3);
            Illuminant = IccRead.Xyz(d, 68);
            Creator = IccRead.U32(d, 80);
            ProfileId = d.Slice(84, 16).ToArray();
            uint count = IccRead.U32(d, 128);
            if (count > (uint)(d.Length - 132) / 12) throw IccRead.Bad("tag count exceeds the profile");
            for (int i = 0; i < count; i++)
            {
                int e = 132 + 12 * i;
                uint sig = IccRead.U32(d, e), off = IccRead.U32(d, e + 4), len = IccRead.U32(d, e + 8);
                if (off < 128 || off > d.Length || len > d.Length - off) throw IccRead.Bad($"tag '{IccSig.ToText(sig)}' lies outside the profile");
                if (tags.TryAdd(sig, ((int)off, (int)len))) tagOrder.Add(sig);
            }
            Description = SafeText(IccSig.Desc);
            Copyright = SafeText(IccSig.Cprt);
        }

        string? SafeText(uint sig)
        {
            try { return HasTag(sig) ? IccRead.Text(Tag(sig)) : null; }
            catch (ImageFormatException) { return null; }
        }

        /// <summary>Parses a profile; throws <see cref="ImageFormatException"/> when the header or tag directory is malformed.
        /// Tags are read when they are used, so a broken tag only fails the conversions that need it.</summary>
        public static IccProfile Parse(ReadOnlySpan<byte> profile) => new(profile.ToArray());

        /// <summary>Parses a profile, null when it is malformed.</summary>
        public static IccProfile? TryParse(ReadOnlySpan<byte> profile)
        {
            try { return Parse(profile); }
            catch (ImageFormatException) { return null; }
        }

        /// <summary>The profile bytes (a copy), e.g. for <see cref="ImageMetadata.IccProfile"/>.</summary>
        public byte[] ToBytes() => (byte[])data.Clone();

        /// <summary>Size of the profile in bytes.</summary>
        public int Size => data.Length;

        /// <summary>The tag signatures in directory order, as 4 character strings.</summary>
        public IReadOnlyList<string> TagSignatures => tagOrder.ConvertAll(IccSig.ToText);

        public bool HasTag(string signature) => HasTag(SigOf(signature));
        internal bool HasTag(uint sig) => tags.ContainsKey(sig);

        /// <summary>The raw data of a tag (a copy), null if absent.</summary>
        public byte[]? GetTag(string signature) => HasTag(SigOf(signature)) ? Tag(SigOf(signature)).ToArray() : null;

        static uint SigOf(string s)
        {
            if (s == null || s.Length != 4) throw new ArgumentException("tag signatures have 4 characters");
            return IccSig.Make(s);
        }

        internal ReadOnlySpan<byte> Tag(uint sig)
        {
            if (!tags.TryGetValue(sig, out var t)) throw new IccException($"ICC: profile has no '{IccSig.ToText(sig)}' tag");
            return data.AsSpan(t.Offset, t.Size);
        }

        // ------------------------------------------------------------------ parsed tags (cached)

        CieXyz? wtpt;
        /// <summary>The media white point (wtpt), D50 when absent or broken.</summary>
        public CieXyz MediaWhitePoint
        {
            get
            {
                if (wtpt is { } w) return w;
                CieXyz v = CieXyz.D50;
                try { if (HasTag(IccSig.Wtpt)) v = IccRead.XyzTag(Tag(IccSig.Wtpt)); } catch (ImageFormatException) { }
                if (!(v.Y > 0)) v = CieXyz.D50;
                return (wtpt = v).Value;
            }
        }

        /// <summary>The chromatic adaptation matrix (chad tag, row-major 3x3), null when absent.</summary>
        public double[]? ChromaticAdaptation => HasTag(IccSig.Chad) ? IccRead.Sf32Matrix(Tag(IccSig.Chad)) : null;

        /// <summary>Whether this is an RGB matrix / TRC or a gray TRC profile (the tags are present; LUTs may exist too).</summary>
        public bool IsMatrixShaper => ColorSpace == IccColorSpace.Rgb
            ? HasTag(IccSig.RXyz) && HasTag(IccSig.GXyz) && HasTag(IccSig.BXyz) && HasTag(IccSig.RTrc) && HasTag(IccSig.GTrc) && HasTag(IccSig.BTrc)
            : ColorSpace == IccColorSpace.Gray && HasTag(IccSig.KTrc);

        /// <summary>The red / green / blue colorants (rXYZ, gXYZ, bXYZ), null when absent.</summary>
        public CieXyz? RedColorant => HasTag(IccSig.RXyz) ? IccRead.XyzTag(Tag(IccSig.RXyz)) : null;
        public CieXyz? GreenColorant => HasTag(IccSig.GXyz) ? IccRead.XyzTag(Tag(IccSig.GXyz)) : null;
        public CieXyz? BlueColorant => HasTag(IccSig.BXyz) ? IccRead.XyzTag(Tag(IccSig.BXyz)) : null;

        /// <summary>The tone reproduction curves (rTRC, gTRC, bTRC, kTRC), null when absent.</summary>
        public IccCurve? RedTrc => Trc(IccSig.RTrc);
        public IccCurve? GreenTrc => Trc(IccSig.GTrc);
        public IccCurve? BlueTrc => Trc(IccSig.BTrc);
        public IccCurve? GrayTrc => Trc(IccSig.KTrc);

        IccCurve? Trc(uint sig) => HasTag(sig) ? IccCurve.Read(Tag(sig), 0, out _) : null;

        /// <summary>
        /// The cicp tag (ICC.1:2022): the H.273 code points of the colour encoding, which ISO 21496-1 requires in the profiles of
        /// HDR images (C.4.4, C.4.7); null when absent or malformed.
        /// </summary>
        public IccCicp? Cicp
        {
            get
            {
                if (!HasTag(IccSig.Cicp)) return null;
                var t = Tag(IccSig.Cicp);
                // type signature 'cicp', 4 reserved bytes, then the four code points of one byte each
                if (t.Length < 12 || IccRead.U32(t, 0) != IccSig.Cicp) return null;
                return new IccCicp(t[8], t[9], t[10], t[11] != 0);
            }
        }

        /// <summary>Whether the profile holds the LUT (A2Bn or B2An) for an intent itself, without falling back to another.</summary>
        public bool HasLut(IccRenderingIntent intent, bool toPcs)
        {
            int n = intent == IccRenderingIntent.AbsoluteColorimetric ? 1 : (int)intent;
            return HasTag(LutSig(n, toPcs));
        }

        internal static uint LutSig(int n, bool toPcs)
            => toPcs ? (n == 0 ? IccSig.A2B0 : n == 1 ? IccSig.A2B1 : IccSig.A2B2) : (n == 0 ? IccSig.B2A0 : n == 1 ? IccSig.B2A1 : IccSig.B2A2);

        internal bool IsV4 => Version.Major >= 4;

        public override string ToString() => $"{Description ?? "ICC profile"} ({Class}, {ColorSpace} -> {Pcs}, v{Version.Major}.{Version.Minor})";
    }
}
