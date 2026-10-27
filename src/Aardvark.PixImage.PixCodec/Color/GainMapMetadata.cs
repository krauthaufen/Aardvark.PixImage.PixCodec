using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// The metadata of an HDR gain map as ISO 21496-1:2025 defines it (clause 5.2): how to unnormalize the stored gain map
    /// and how far to apply it for a given display headroom. Headrooms, <see cref="GainMapMin"/> and
    /// <see cref="GainMapMax"/> are log2 values. The per-component arrays always hold 3 values (R, G, B); they are equal
    /// when the file stores one set (<see cref="IsMultichannel"/> false).
    /// <para>The Ultra HDR / Adobe XMP form (hdrgm namespace) maps onto it: GainMapMin / GainMapMax / Gamma as they are,
    /// OffsetSDR / OffsetHDR to the baseline / alternate offsets and HDRCapacityMin / HDRCapacityMax to the baseline /
    /// alternate headrooms (swapped, together with the offsets, when hdrgm:BaseRenditionIsHDR is True).</para>
    /// </summary>
    public sealed class GainMapMetadata
    {
        /// <summary>The URN that labels ISO 21496-1 metadata (C.3), which JPEG APP2 segments start with (C.4.6), with its terminating NUL.</summary>
        internal static ReadOnlySpan<byte> Urn => "urn:iso:std:iso:ts:21496:-1\0"u8;

        internal const string HdrgmNs = "http://ns.adobe.com/hdr-gain-map/1.0/";

        /// <summary>minimum_version (C.2.3): 0 for this edition.</summary>
        public int MinimumVersion { get; init; }
        /// <summary>
        /// writer_version (C.2.3) as read: the structure version of the writer that made the metadata, for information.
        /// <see cref="ToIso21496"/> always writes 0, the version of the structure it produces.
        /// </summary>
        public int WriterVersion { get; init; }
        /// <summary>Whether separate per-component values are stored for R, G and B (is_multichannel); not the channel count of the gain map image.</summary>
        public bool IsMultichannel { get; init; }
        /// <summary>
        /// Whether the gain map is applied in a linear space with the baseline image's primaries (use_base_colour_space,
        /// 5.3.4); otherwise with the alternate image's.
        /// </summary>
        public bool UseBaseColourSpace { get; init; } = true;
        /// <summary>H_baseline (5.2.6): the HDR headroom (log2) of the baseline image, at which the gain map is not applied.</summary>
        public double BaseHdrHeadroom { get; init; }
        /// <summary>H_alternate (5.2.7): the HDR headroom (log2) of the alternate image, at which the gain map is fully applied.</summary>
        public double AlternateHdrHeadroom { get; init; }
        /// <summary>min(G) per component (5.2.5.2), log2.</summary>
        public double[] GainMapMin { get; init; } = { 0, 0, 0 };
        /// <summary>max(G) per component (5.2.5.3), log2.</summary>
        public double[] GainMapMax { get; init; } = { 0, 0, 0 };
        /// <summary>γ per component (5.2.5.6), greater than 0.</summary>
        public double[] Gamma { get; init; } = { 1, 1, 1 };
        /// <summary>k_baseline per component (5.2.5.4).</summary>
        public double[] BaseOffset { get; init; } = { 1 / 64.0, 1 / 64.0, 1 / 64.0 };
        /// <summary>k_alternate per component (5.2.5.5).</summary>
        public double[] AlternateOffset { get; init; } = { 1 / 64.0, 1 / 64.0, 1 / 64.0 };

        /// <summary>Whether the baseline is the HDR rendition and the gain map maps it down (H_alternate below H_baseline).</summary>
        public bool BaseRenditionIsHdr => AlternateHdrHeadroom < BaseHdrHeadroom;

        /// <summary>
        /// The weighting factor W of ISO 21496-1 formula (3) for a display whose HDR headroom (log2 of peak over HDR
        /// reference white) is <paramref name="targetHeadroom"/>: sign(H_alt - H_base) clamp((H_target - H_base) / (H_alt - H_base), 0, 1).
        /// </summary>
        public double Weight(double targetHeadroom)
        {
            double d = AlternateHdrHeadroom - BaseHdrHeadroom;
            if (d == 0) return 0;
            return Math.Sign(d) * Math.Clamp((targetHeadroom - BaseHdrHeadroom) / d, 0, 1);
        }

        /// <summary>Checks the constraints of clause 5 (gamma above 0, max(G) not below min(G), the headrooms differ, finite values).</summary>
        internal bool IsValid()
        {
            if (!(double.IsFinite(BaseHdrHeadroom) && double.IsFinite(AlternateHdrHeadroom)) || BaseHdrHeadroom == AlternateHdrHeadroom) return false;
            foreach (var a in new[] { GainMapMin, GainMapMax, Gamma, BaseOffset, AlternateOffset })
            {
                if (a is not { Length: 3 }) return false;
                foreach (var v in a) if (!double.IsFinite(v)) return false;
            }
            for (int c = 0; c < 3; c++)
                if (!(Gamma[c] > 0) || GainMapMax[c] < GainMapMin[c]) return false;
            return true;
        }

        // ------------------------------------------------------------------ ISO 21496-1 binary form (C.2)

        /// <summary>
        /// Parses the GainMapMetadata structure of ISO 21496-1 C.2.2 (big-endian, data after the known fields ignored).
        /// Returns null for an unknown minimum_version, a bare GainMapVersion (the baseline image's form), a zero denominator
        /// or values that break clause 5; throws <see cref="ImageFormatException"/> when the data ends early.
        /// </summary>
        public static GainMapMetadata? FromIso21496(ReadOnlySpan<byte> payload)
        {
            var data = payload.ToArray();
            if (data.Length < 4) throw new ImageFormatException("ISO 21496-1: truncated gain map metadata");
            int min = BinaryPrimitives.ReadUInt16BigEndian(data), writer = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2));
            if (min != 0) return null;
            if (data.Length == 4) return null;
            int flags = data[4];
            bool multi = (flags & 0x80) != 0, useBase = (flags & 0x40) != 0;
            int channels = multi ? 3 : 1;
            int need = 5 + 16 + channels * 40;
            if (data.Length < need) throw new ImageFormatException("ISO 21496-1: truncated gain map metadata");
            int p = 5;
            bool ok = true;
            uint U() { uint v = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p)); p += 4; return v; }
            int S() { int v = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(p)); p += 4; return v; }
            double Unsigned() { double n = U(); uint d = U(); if (d == 0) ok = false; return d == 0 ? 0 : n / d; }
            double Signed() { double n = S(); uint d = U(); if (d == 0) ok = false; return d == 0 ? 0 : n / d; }
            double hb = Unsigned(), ha = Unsigned();
            double[] gmin = new double[3], gmax = new double[3], gamma = new double[3], kb = new double[3], ka = new double[3];
            for (int c = 0; c < channels; c++)
            {
                gmin[c] = Signed(); gmax[c] = Signed(); gamma[c] = Unsigned(); kb[c] = Signed(); ka[c] = Signed();
            }
            for (int c = channels; c < 3; c++) { gmin[c] = gmin[0]; gmax[c] = gmax[0]; gamma[c] = gamma[0]; kb[c] = kb[0]; ka[c] = ka[0]; }
            if (!ok) return null;
            var m = new GainMapMetadata
            {
                MinimumVersion = min, WriterVersion = writer, IsMultichannel = multi, UseBaseColourSpace = useBase,
                BaseHdrHeadroom = hb, AlternateHdrHeadroom = ha,
                GainMapMin = gmin, GainMapMax = gmax, Gamma = gamma, BaseOffset = kb, AlternateOffset = ka,
            };
            return m.IsValid() ? m : null;
        }

        /// <summary>
        /// The GainMapMetadata structure of ISO 21496-1 C.2.2 (minimum_version 0). Throws <see cref="ArgumentException"/> for
        /// values the structure cannot hold: those clause 5 forbids, negative headrooms (unsigned rationals, C.2.3), γ too
        /// small for a numerator other than 0, headrooms that become equal as rationals (5.2.7).
        /// </summary>
        public byte[] ToIso21496()
        {
            if (!IsValid()) throw new ArgumentException("invalid gain map metadata");
            if (BaseHdrHeadroom < 0 || AlternateHdrHeadroom < 0) throw new ArgumentException("ISO 21496-1 headrooms are unsigned (C.2.3)");
            if (Rational(BaseHdrHeadroom, false) == Rational(AlternateHdrHeadroom, false)) throw new ArgumentException("the headrooms are equal as rationals (ISO 21496-1 5.2.7)");
            int channels = IsMultichannel ? 3 : 1;
            var b = new byte[5 + 16 + channels * 40];
            int p = 0;
            void U16(int v) { BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(p), (ushort)v); p += 2; }
            void U32(uint v) { BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p), v); p += 4; }
            void Unsigned(double v) { var (n, d) = Rational(v, false); U32((uint)n); U32(d); }
            void Signed(double v) { var (n, d) = Rational(v, true); U32((uint)(int)n); U32(d); }
            void WriteGamma(double v)
            {
                var (n, d) = Rational(v, false);
                if (n == 0) throw new ArgumentException($"gain map gamma {v} is too small for ISO 21496-1 (numerator 0, C.2.3)");
                U32((uint)n); U32(d);
            }
            U16(0); U16(0);   // minimum_version and writer_version: the first edition's structure (5.2.8)
            b[p++] = (byte)((IsMultichannel ? 0x80 : 0) | (UseBaseColourSpace ? 0x40 : 0));
            Unsigned(BaseHdrHeadroom); Unsigned(AlternateHdrHeadroom);
            for (int c = 0; c < channels; c++)
            {
                Signed(GainMapMin[c]); Signed(GainMapMax[c]); WriteGamma(Gamma[c]); Signed(BaseOffset[c]); Signed(AlternateOffset[c]);
            }
            return b;
        }

        /// <summary>The GainMapVersion structure alone (C.4.3: what the baseline image of a JPEG carries).</summary>
        internal static byte[] VersionOnly() => new byte[4];

        /// <summary>
        /// A fraction for <paramref name="v"/>: the largest power of two denominator (up to 2^30) that keeps the numerator in
        /// range, then reduced. Signed numerators are int32, unsigned ones uint32 (negative values are refused).
        /// </summary>
        static (long Num, uint Den) Rational(double v, bool signed)
        {
            if (!double.IsFinite(v)) throw new ArgumentException("gain map metadata values must be finite");
            if (!signed && v < 0) throw new ArgumentException($"gain map metadata value {v} has to be unsigned");
            double limit = signed ? int.MaxValue : uint.MaxValue;
            if (Math.Abs(v) > limit) throw new ArgumentException($"gain map metadata value {v} is out of range");
            long den = 1L << 30;
            while (den > 1 && Math.Abs(Math.Round(v * den)) > limit) den >>= 1;
            long num = (long)Math.Round(v * den);
            while (den > 1 && (num & 1) == 0) { num >>= 1; den >>= 1; }
            return (num, (uint)den);
        }

        // ------------------------------------------------------------------ Ultra HDR / Adobe XMP form

        /// <summary>
        /// The hdrgm properties of an XMP packet (Ultra HDR v1.1 "HDR gain map metadata"): null when hdrgm:Version is not
        /// 1.0, a required property (GainMapMax, HDRCapacityMax) is missing or a value is unparsable or out of range.
        /// </summary>
        internal static GainMapMetadata? FromHdrgm(XmpData xmp)
        {
            XmpProperty? P(string name)
            {
                foreach (var p in xmp.Properties)
                    if (p.Namespace == HdrgmNs && !p.Key.Contains('/') && p.Key.EndsWith(":" + name, StringComparison.Ordinal)) return p;
                return null;
            }
            if (P("Version")?.Value.Trim() != "1.0") return null;
            bool ok = true;
            double[] Values(string name, double? def)
            {
                var p = P(name);
                if (p == null || p.Values.Count == 0)
                {
                    if (def is not double d) { ok = false; return new double[3]; }
                    return new[] { d, d, d };
                }
                if (p.Values.Count != 1 && p.Values.Count != 3) { ok = false; return new double[3]; }
                var r = new double[3];
                for (int c = 0; c < 3; c++)
                {
                    var s = p.Values[p.Values.Count == 1 ? 0 : c].Trim();
                    if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out r[c]) || !double.IsFinite(r[c])) ok = false;
                }
                return r;
            }
            bool multi = (P("GainMapMin")?.Values.Count ?? 1) == 3 || (P("GainMapMax")?.Values.Count ?? 1) == 3 || (P("Gamma")?.Values.Count ?? 1) == 3
                || (P("OffsetSDR")?.Values.Count ?? 1) == 3 || (P("OffsetHDR")?.Values.Count ?? 1) == 3;
            var gmin = Values("GainMapMin", 0);
            var gmax = Values("GainMapMax", null);
            var gamma = Values("Gamma", 1);
            var osdr = Values("OffsetSDR", 1 / 64.0);
            var ohdr = Values("OffsetHDR", 1 / 64.0);
            double cmin = Values("HDRCapacityMin", 0)[0];
            double cmax = Values("HDRCapacityMax", null)[0];
            var baseHdr = P("BaseRenditionIsHDR")?.Value.Trim();
            bool isHdr = string.Equals(baseHdr, "True", StringComparison.OrdinalIgnoreCase);
            if (baseHdr != null && !isHdr && !string.Equals(baseHdr, "False", StringComparison.OrdinalIgnoreCase)) ok = false;
            if (!ok || cmin < 0 || !(cmax > cmin)) return null;
            for (int c = 0; c < 3; c++) if (osdr[c] < 0 || ohdr[c] < 0) return null;
            var m = new GainMapMetadata
            {
                IsMultichannel = multi, UseBaseColourSpace = true,
                BaseHdrHeadroom = isHdr ? cmax : cmin, AlternateHdrHeadroom = isHdr ? cmin : cmax,
                GainMapMin = gmin, GainMapMax = gmax, Gamma = gamma,
                BaseOffset = isHdr ? ohdr : osdr, AlternateOffset = isHdr ? osdr : ohdr,
            };
            return m.IsValid() ? m : null;
        }

        /// <summary>The hdrgm attributes (with their namespace declaration) for an rdf:Description element.</summary>
        internal string HdrgmAttributes()
        {
            bool hdr = BaseRenditionIsHdr;
            var sb = new StringBuilder();
            sb.Append(" xmlns:hdrgm=\"").Append(HdrgmNs).Append("\" hdrgm:Version=\"1.0\"");
            void One(string name, double v) => sb.Append(" hdrgm:").Append(name).Append("=\"").Append(F(v)).Append('"');
            void Attr(string name, double[] v) { if (!IsMultichannel || v[0] == v[1] && v[1] == v[2]) One(name, v[0]); }
            Attr("GainMapMin", GainMapMin); Attr("GainMapMax", GainMapMax); Attr("Gamma", Gamma);
            Attr("OffsetSDR", hdr ? AlternateOffset : BaseOffset); Attr("OffsetHDR", hdr ? BaseOffset : AlternateOffset);
            One("HDRCapacityMin", Math.Min(BaseHdrHeadroom, AlternateHdrHeadroom));
            One("HDRCapacityMax", Math.Max(BaseHdrHeadroom, AlternateHdrHeadroom));
            sb.Append(" hdrgm:BaseRenditionIsHDR=\"").Append(hdr ? "True" : "False").Append('"');
            return sb.ToString();
        }

        /// <summary>The per-component hdrgm properties that differ between channels, as rdf:Seq elements.</summary>
        internal string HdrgmElements()
        {
            if (!IsMultichannel) return "";
            bool hdr = BaseRenditionIsHdr;
            var sb = new StringBuilder();
            void Seq(string name, double[] v)
            {
                if (v[0] == v[1] && v[1] == v[2]) return;
                sb.Append("<hdrgm:").Append(name).Append("><rdf:Seq>");
                foreach (var x in v) sb.Append("<rdf:li>").Append(F(x)).Append("</rdf:li>");
                sb.Append("</rdf:Seq></hdrgm:").Append(name).Append('>');
            }
            Seq("GainMapMin", GainMapMin); Seq("GainMapMax", GainMapMax); Seq("Gamma", Gamma);
            Seq("OffsetSDR", hdr ? AlternateOffset : BaseOffset); Seq("OffsetHDR", hdr ? BaseOffset : AlternateOffset);
            return sb.ToString();
        }

        static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        public override string ToString()
            => string.Format(CultureInfo.InvariantCulture, "gain map: G {0:G4}..{1:G4}, gamma {2:G4}, offsets {3:G4} / {4:G4}, headroom {5:G4} -> {6:G4}{7}",
                GainMapMin[0], GainMapMax[0], Gamma[0], BaseOffset[0], AlternateOffset[0], BaseHdrHeadroom, AlternateHdrHeadroom, IsMultichannel ? " (per channel)" : "");
    }
}
