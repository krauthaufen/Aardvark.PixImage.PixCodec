using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Aardvark.ImageCodecs.Heif
{
    /// <summary>
    /// The low-overhead image file format (ISO/IEC 23008-12 Amd 2 Annex O): a MinimizedImageBox ('mini', version 0) is
    /// expanded to the equivalent MetaBox and MediaDataBox of O.4, so that the rest of the reader sees a regular file.
    /// </summary>
    internal static class HeifMini
    {
        ref struct Bits
        {
            readonly ReadOnlySpan<byte> d;
            long pos;   // in bits
            public Bits(ReadOnlySpan<byte> d) { this.d = d; pos = 0; }

            public uint U(int n)
            {
                if (pos + n > (long)d.Length * 8) throw new ImageFormatException("AVIF: truncated mini box");
                uint v = 0;
                for (int i = 0; i < n; i++, pos++) v = (v << 1) | (uint)((d[(int)(pos >> 3)] >> (7 - (int)(pos & 7))) & 1);
                return v;
            }
            public bool Flag() => U(1) != 0;
            public byte[] Bytes(int n)
            {
                var r = new byte[n];
                for (int i = 0; i < n; i++) r[i] = (byte)U(8);
                return r;
            }
            /// <summary>The byte position after the trailing bits.</summary>
            public int Align() => (int)((pos + 7) >> 3);
        }

        /// <summary>The file with its top-level mini box expanded (O.4), or null if it has none (or a meta box).</summary>
        public static byte[]? Expand(byte[] file)
        {
            var top = IsoBoxes.List(file, 0, file.Length);
            if (top.Count == 0 || top[0].Type != "ftyp" || IsoBoxes.Find(top, "meta") != null || IsoBoxes.Find(top, "mini") is not IsoBox mini) return null;
            var f = top[0];
            if (f.Size < 8) throw new ImageFormatException("HEIF: bad ftyp box");
            string major = Encoding.ASCII.GetString(file, (int)f.Payload, 4);
            string minor = Encoding.ASCII.GetString(file, (int)f.Payload + 4, 4);
            bool minorIsBrand = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan((int)f.Payload + 4)) != 0;
            var brands = new List<string>();
            for (long p = f.Payload + 8; p + 4 <= f.End; p += 4) brands.Add(Encoding.ASCII.GetString(file, (int)p, 4));
            var m = Parse(file.AsSpan((int)mini.Payload, (int)mini.Size).ToArray(), major == "mif3" && minorIsBrand ? minor : null);
            // O.2.1.2: treated as having mif1 (and tmap with a gain map), the major brand being the minor_version's brand
            string newMajor = major == "mif3" && minorIsBrand ? minor : major;
            if (!brands.Contains("mif1")) brands.Add("mif1");
            if (m.GainMap && !brands.Contains("tmap")) brands.Add("tmap");
            if (!brands.Contains(newMajor)) brands.Add(newMajor);
            return m.Build(newMajor, brands);
        }

        sealed class Mini
        {
            public string InfeType = "", ConfigType = "";
            public int Width, Height, Sub, Bits, Orientation;
            public bool FullRange, Premultiplied, GainMap;
            public int Cp, Tc, Mc;
            public byte[] MainConfig = Array.Empty<byte>(), MainData = Array.Empty<byte>();
            public byte[]? AlphaConfig, AlphaData, Icc, Exif, Xmp, TmapIcc, GainMapMetadata, GainMapConfig, GainMapData;
            public bool Compressed;
            public int GmWidth, GmHeight, GmMc, GmSub, GmBits;
            public bool GmFullRange;
            public int TCp = 1, TTc = 13, TMc = 6;
            public bool TFull = true, TExplicitCicp;
            public readonly Dictionary<string, byte[]> Hdr = new(), TmapHdr = new();

            static readonly string[] HdrTypes = { "clli", "mdcv", "cclv", "amve", "reve", "ndwt" };

            static byte[] Box(string type, params byte[][] parts)
            {
                int n = 8;
                foreach (var p in parts) n += p.Length;
                var r = new byte[n];
                BinaryPrimitives.WriteUInt32BigEndian(r, (uint)n);
                Encoding.ASCII.GetBytes(type, 0, 4, r, 4);
                int o = 8;
                foreach (var p in parts) { p.CopyTo(r, o); o += p.Length; }
                return r;
            }
            static byte[] Full(string type, int version, int flags, params byte[][] parts)
            {
                var all = new byte[parts.Length + 1][];
                all[0] = new byte[] { (byte)version, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags };
                parts.CopyTo(all, 1);
                return Box(type, all);
            }
            static byte[] U16(int v) => new[] { (byte)(v >> 8), (byte)v };
            static byte[] U32(long v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
            static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
            static byte[] Concat(List<byte[]> parts)
            {
                var ms = new MemoryStream();
                foreach (var p in parts) ms.Write(p);
                return ms.ToArray();
            }

            static byte[] Ispe(int w, int h) => Full("ispe", 0, 0, U32(w), U32(h));
            static byte[] Pixi(int channels, int bits)
            {
                var b = new byte[1 + channels];
                b[0] = (byte)channels;
                for (int i = 0; i < channels; i++) b[1 + i] = (byte)bits;
                return Full("pixi", 0, 0, b);
            }
            static byte[] Nclx(int cp, int tc, int mc, bool full) => Box("colr", Ascii("nclx"), U16(cp), U16(tc), U16(mc), new[] { (byte)(full ? 0x80 : 0) });

            public byte[] Build(string major, List<string> brands)
            {
                bool hasAlpha = AlphaData != null, hasGm = GainMapData != null;
                // O.4.6: the 32 entries of the ItemPropertyContainerBox (null: a FreeSpaceBox)
                var e = new byte[33][];
                if (MainConfig.Length > 0) e[1] = Box(ConfigType, MainConfig);
                e[2] = Ispe(Width, Height);
                e[3] = Pixi(Sub == 0 ? 1 : 3, Bits);
                e[4] = Nclx(Cp, Tc, Mc, FullRange);
                if (Icc != null) e[5] = Box("colr", Ascii("prof"), Icc);
                if (hasAlpha)
                {
                    if (AlphaConfig!.Length > 0) e[6] = Box(ConfigType, AlphaConfig);
                    e[7] = Full("auxC", 0, 0, Ascii(AvifReader.AlphaUrn), new byte[1]);
                    e[8] = Pixi(1, Bits);
                }
                int o = Orientation - 1;
                if (o is 2 or 4 or 5 or 6 or 7) e[9] = Box("irot", new[] { (byte)(o switch { 2 => 2, 4 => 1, 5 => 3, _ => 1 }) });
                if (o is 1 or 3 or 4 or 6) e[10] = Box("imir", new[] { (byte)(o is 1 or 6 ? 1 : 0) });
                for (int i = 0; i < 6; i++)
                {
                    if (Hdr.TryGetValue(HdrTypes[i], out var body)) e[11 + i] = i >= 4 ? Full(HdrTypes[i], 0, 0, body) : Box(HdrTypes[i], body);
                    if (TmapHdr.TryGetValue(HdrTypes[i], out var tb)) e[24 + i] = i >= 4 ? Full(HdrTypes[i], 0, 0, tb) : Box(HdrTypes[i], tb);
                }
                if (hasGm)
                {
                    if (GainMapConfig!.Length > 0) e[17] = Box(ConfigType, GainMapConfig);
                    e[18] = Ispe(GmWidth, GmHeight);
                    e[19] = Pixi(GmSub == 0 ? 1 : 3, GmBits);
                    e[20] = Nclx(2, 2, GmMc, GmFullRange);
                }
                if (GainMap)
                {
                    e[21] = o >= 4 ? Ispe(Height, Width) : Ispe(Width, Height);
                    if (TExplicitCicp || TmapIcc == null) e[22] = Nclx(TCp, TTc, TMc, TFull);
                    if (TmapIcc != null) e[23] = Box("colr", Ascii("prof"), TmapIcc);
                }
                var ipco = new List<byte[]>();
                for (int i = 1; i <= 32; i++) ipco.Add(e[i] ?? Box("free"));

                // the associations of O.4.6 (FreeSpaceBoxes dropped): (index, essential)
                var assoc = new List<(uint item, List<(int, bool)> props)>();
                var main = new List<(int, bool)> { (1, true), (2, false), (3, false), (4, true), (5, true) };
                main.AddRange(new[] { (11, false), (12, false), (13, false), (14, false), (15, false), (16, false) });
                main.AddRange(new[] { (9, true), (10, true) });
                assoc.Add((1, main));
                if (hasAlpha) assoc.Add((2, new List<(int, bool)> { (6, true), (2, false), (7, true), (8, false), (9, true), (10, true) }));
                if (GainMap) assoc.Add((3, new List<(int, bool)> { (21, false), (22, true), (23, true), (24, false), (25, false), (26, false), (27, false), (28, false), (29, false) }));
                if (hasGm) assoc.Add((4, new List<(int, bool)> { (17, true), (18, false), (19, false), (20, true), (9, true), (10, true) }));
                var ipma = new List<byte[]> { U32(assoc.Count) };
                foreach (var (item, props) in assoc)
                {
                    var kept = props.FindAll(p => e[p.Item1] != null);
                    ipma.Add(U16((int)item));
                    ipma.Add(new[] { (byte)kept.Count });
                    foreach (var (idx, ess) in kept) ipma.Add(new[] { (byte)((ess ? 0x80 : 0) | idx) });
                }
                var iprp = Box("iprp", Box("ipco", Concat(ipco)), Full("ipma", 0, 0, Concat(ipma)));

                // O.4.3, O.4.4, O.4.5: items and references
                var items = new List<(uint id, string type, int flags, byte[] data, string contentType, string encoding)>();
                byte[] tmapData = GainMap ? Concat(new List<byte[]> { new byte[1], GainMapMetadata ?? Array.Empty<byte>() }) : Array.Empty<byte>();
                items.Add((1, InfeType, 0, MainData, "", ""));
                if (hasAlpha) items.Add((2, InfeType, 1, AlphaData!, "", ""));
                if (GainMap) items.Add((3, "tmap", 0, tmapData, "", ""));
                if (hasGm) items.Add((4, InfeType, 1, GainMapData!, "", ""));
                if (Exif != null) items.Add((6, Compressed ? "dExf" : "Exif", 1, Exif, "", ""));
                if (Xmp != null) items.Add((7, "mime", 1, Xmp, "application/rdf+xml", Compressed ? "deflate" : ""));
                var infes = new List<byte[]> { U16(items.Count) };
                foreach (var it in items)
                {
                    var body = new List<byte[]> { U16((int)it.id), U16(0), Ascii(it.type), new byte[1] };
                    if (it.type == "mime")
                    {
                        body.Add(Ascii(it.contentType)); body.Add(new byte[1]);
                        if (it.encoding.Length > 0) { body.Add(Ascii(it.encoding)); body.Add(new byte[1]); }
                    }
                    infes.Add(Full("infe", 2, it.flags, Concat(body)));
                }
                var refs = new List<byte[]>();
                void Ref(string type, uint from, params uint[] to)
                {
                    var b = new List<byte[]> { U16((int)from), U16(to.Length) };
                    foreach (var t in to) b.Add(U16((int)t));
                    refs.Add(Box(type, Concat(b)));
                }
                if (hasAlpha)
                {
                    Ref("auxl", 2, 1);
                    if (Premultiplied) Ref("prem", 1, 2);
                }
                if (GainMap) { if (hasGm) Ref("dimg", 3, 1, 4); else Ref("dimg", 3, 1); }
                if (Exif != null) Ref("cdsc", 6, 1);
                if (Xmp != null) Ref("cdsc", 7, 1);

                // O.4.9, O.4.10: mdat holds alpha, tmap, gain map, main, Exif, XMP data in this order
                var order = new List<(uint id, byte[] data)>();
                foreach (uint id in new uint[] { 2, 3, 4, 1, 6, 7 })
                    foreach (var it in items) if (it.id == id) order.Add((id, it.data));
                byte[] Meta(long mdatData)
                {
                    var iloc = new List<byte[]> { new byte[] { 0x44, 0x00 }, U16(order.Count) };
                    long off = mdatData;
                    foreach (var (id, data) in order)
                    {
                        iloc.Add(U16((int)id)); iloc.Add(U16(0)); iloc.Add(U16(0)); iloc.Add(U16(1));
                        iloc.Add(U32(off)); iloc.Add(U32(data.Length));
                        off += data.Length;
                    }
                    var parts = new List<byte[]>
                    {
                        Full("hdlr", 0, 0, new byte[4], Ascii("pict"), new byte[12], new byte[1]),
                        Full("pitm", 0, 0, U16(1)),
                        Full("iloc", 1, 0, Concat(iloc)),
                        Full("iinf", 0, 0, Concat(infes)),
                    };
                    if (refs.Count > 0) parts.Add(Full("iref", 0, 0, Concat(refs)));
                    parts.Add(iprp);
                    if (GainMap) parts.Add(Box("grpl", Full("altr", 0, 0, U32(5), U32(2), U32(3), U32(1))));
                    return Full("meta", 0, 0, Concat(parts));
                }
                var ftypParts = new List<byte[]> { Ascii(major), U32(0) };
                foreach (var b in brands) ftypParts.Add(Ascii(b));
                var ftyp = Box("ftyp", Concat(ftypParts));
                long dataStart = ftyp.Length + Meta(0).Length + 8;
                if (dataStart > uint.MaxValue) throw new ImageNotSupportedException("AVIF: mini box too large");
                var mdatParts = new List<byte[]>();
                foreach (var (_, data) in order) mdatParts.Add(data);
                var mdatData = Concat(mdatParts);
                long total = dataStart + mdatData.Length;
                if (total > int.MaxValue) throw new ImageNotSupportedException("AVIF: mini box too large");
                return Concat(new List<byte[]> { ftyp, Meta(dataStart), Box("mdat", mdatData) });
            }

            public static int BitDepth(ref Bits b, bool isFloat)
            {
                if (isFloat) return 1 << ((int)b.U(2) + 4);
                return b.Flag() ? (int)b.U(3) + 9 : 8;
            }

            /// <summary>The body of one of the HDR boxes of O.3.2 (byte aligned only by chance: read bit by bit).</summary>
            public static byte[] HdrBody(ref Bits b, string type)
            {
                switch (type)
                {
                    case "clli": return b.Bytes(4);
                    case "mdcv": return b.Bytes(24);
                    case "amve": return b.Bytes(8);
                    case "reve": return b.Bytes(16);
                    case "ndwt": return b.Bytes(4);
                    default:
                        {
                            // ContentColourVolumeBox: a flags byte (reserved, cancel, persistence, primaries, min, max, avg
                            // luminance present, reserved), then the present fields
                            var first = b.Bytes(1);
                            int f = first[0];
                            int n = (f & 0x40) != 0 ? 0 : ((f & 0x10) != 0 ? 24 : 0) + ((f & 0x08) != 0 ? 4 : 0) + ((f & 0x04) != 0 ? 4 : 0) + ((f & 0x02) != 0 ? 4 : 0);
                            var rest = b.Bytes(n);
                            var r = new byte[1 + n];
                            r[0] = first[0];
                            rest.CopyTo(r, 1);
                            return r;
                        }
                }
            }

            public static void Hdrs(ref Bits b, Dictionary<string, byte[]> into)
            {
                Span<bool> present = stackalloc bool[6];
                for (int i = 0; i < 6; i++) present[i] = b.Flag();
                for (int i = 0; i < 6; i++) if (present[i]) into[HdrTypes[i]] = HdrBody(ref b, HdrTypes[i]);
            }
        }

        /// <summary>O.3.2: the MinimizedImageBox; <paramref name="codecBrand"/> is the minor_version brand of a 'mif3' file.</summary>
        static Mini Parse(byte[] d, string? codecBrand)
        {
            var m = new Mini();
            var b = new Bits(d);
            if (b.U(2) != 0) throw new ImageNotSupportedException("AVIF: mini box version other than 0");
            bool explicitCodec = b.Flag(), isFloat = b.Flag();
            m.FullRange = b.Flag();
            bool alpha = b.Flag(), explicitCicp = b.Flag(), hdr = b.Flag(), icc = b.Flag(), exif = b.Flag(), xmp = b.Flag();
            m.Sub = (int)b.U(2);
            m.Orientation = (int)b.U(3) + 1;
            bool large = b.Flag();
            int dn = large ? 15 : 7;
            m.Width = (int)b.U(dn) + 1;
            m.Height = (int)b.U(dn) + 1;
            if (m.Sub is 1 or 2) b.U(1);   // chroma_is_horizontally_centered
            if (m.Sub == 1) b.U(1);
            m.Bits = Mini.BitDepth(ref b, isFloat);
            if (alpha) m.Premultiplied = b.Flag();
            if (explicitCicp) { m.Cp = (int)b.U(8); m.Tc = (int)b.U(8); m.Mc = (int)b.U(8); }
            else { m.Cp = icc ? 2 : 1; m.Tc = icc ? 2 : 13; m.Mc = m.Sub == 0 ? 2 : 6; }   // Table O.3
            if (explicitCodec)
            {
                m.InfeType = FourCC(b.U(32));
                m.ConfigType = FourCC(b.U(32));
            }
            else if (codecBrand == "avif") { m.InfeType = "av01"; m.ConfigType = "av1C"; }
            else throw new ImageNotSupportedException($"AVIF: mini box without codec types for brand '{codecBrand}'");
            bool tmapIcc = false;
            if (hdr)
            {
                m.GainMap = b.Flag();
                if (m.GainMap)
                {
                    if (b.Flag()) { m.GmWidth = m.Width; m.GmHeight = m.Height; }
                    else { m.GmWidth = (int)b.U(dn) + 1; m.GmHeight = (int)b.U(dn) + 1; }
                    m.GmMc = (int)b.U(8);
                    m.GmFullRange = b.Flag();
                    m.GmSub = (int)b.U(2);
                    if (m.GmSub is 1 or 2) b.U(1);
                    if (m.GmSub == 1) b.U(1);
                    bool gmFloat = b.Flag();
                    m.GmBits = Mini.BitDepth(ref b, gmFloat);
                    tmapIcc = b.Flag();
                    m.TExplicitCicp = b.Flag();
                    if (m.TExplicitCicp) { m.TCp = (int)b.U(8); m.TTc = (int)b.U(8); m.TMc = (int)b.U(8); m.TFull = b.Flag(); }
                }
                Mini.Hdrs(ref b, m.Hdr);
                if (m.GainMap) Mini.Hdrs(ref b, m.TmapHdr);
            }
            bool largeMeta = (icc || exif || xmp || m.GainMap) && b.Flag();
            bool largeConfig = b.Flag(), largeData = b.Flag();
            int mn = largeMeta ? 20 : 10, cn = largeConfig ? 12 : 3, dn2 = largeData ? 28 : 15;
            int iccSize = icc ? (int)b.U(mn) + 1 : 0;
            int tmapIccSize = m.GainMap && tmapIcc ? (int)b.U(mn) + 1 : 0;
            int gmMetaSize = 0, gmDataSize = 0, gmConfigSize = 0;
            if (m.GainMap)
            {
                gmMetaSize = (int)b.U(mn);
                gmDataSize = (int)b.U(dn2);
                if (gmDataSize > 0) gmConfigSize = (int)b.U(cn);
            }
            int mainConfigSize = (int)b.U(cn);
            long mainDataSize = (long)b.U(dn2) + 1;
            int alphaDataSize = 0, alphaConfigSize = 0;
            if (alpha)
            {
                alphaDataSize = (int)b.U(dn2);
                if (alphaDataSize > 0) alphaConfigSize = (int)b.U(cn);
            }
            if ((exif || xmp)) m.Compressed = b.Flag();
            int exifSize = exif ? (int)b.U(mn) + 1 : 0, xmpSize = xmp ? (int)b.U(mn) + 1 : 0;
            int pos = b.Align();
            byte[] Chunk(long n)
            {
                if (n < 0 || pos + n > d.Length) throw new ImageFormatException("AVIF: mini box chunk outside the box");
                var r = d.AsSpan(pos, (int)n).ToArray();
                pos += (int)n;
                return r;
            }
            m.MainConfig = Chunk(mainConfigSize);
            if (alphaDataSize > 0) m.AlphaConfig = alphaConfigSize == 0 ? m.MainConfig : Chunk(alphaConfigSize);
            if (gmDataSize > 0) m.GainMapConfig = gmConfigSize == 0 ? m.MainConfig : Chunk(gmConfigSize);
            if (icc) m.Icc = Chunk(iccSize);
            if (tmapIccSize > 0) m.TmapIcc = Chunk(tmapIccSize);
            if (m.GainMap) m.GainMapMetadata = Chunk(gmMetaSize);
            if (alphaDataSize > 0) m.AlphaData = Chunk(alphaDataSize);
            if (gmDataSize > 0) m.GainMapData = Chunk(gmDataSize);
            m.MainData = Chunk(mainDataSize);
            if (exif) m.Exif = Chunk(exifSize);
            if (xmp) m.Xmp = Chunk(xmpSize);
            // alpha coded with the colour (codecs with native alpha, entry 30): not for AV1
            if (alpha && alphaDataSize == 0) throw new ImageNotSupportedException("AVIF: mini box with alpha in the main item");
            return m;
        }

        static string FourCC(uint v) => Encoding.ASCII.GetString(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
    }
}
