using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Aardvark.ImageCodecs
{
    /// <summary>What the boxes of a JP2 / JPH file (T.800 Annex I) say about the codestream they wrap.</summary>
    internal sealed class Jp2Header
    {
        public bool IsJp2;
        public int CodestreamStart, CodestreamLength;
        public bool HasIhdr;
        public int Width, Height, NumComps, Bpc;
        public byte[]? Bpcc;
        /// <summary>0 none, 1 enumerated, 2 ICC profile.</summary>
        public int ColorMethod;
        public int EnumCs = -1;
        public byte[]? Icc;
        public int PaletteEntries, PaletteCols;
        public byte[]? PaletteDepth;         // per column: depth - 1, bit 7 = signed
        public int[]? Palette;               // entries * columns
        public (int Comp, int Type, int Col)[]? Cmap;
        public (int Channel, int Type, int Assoc)[]? Cdef;
        public double DpiX, DpiY;
        public byte[]? Exif, Xmp;
        public int Orientation;

        public const int CsBilevel = 0, CsCmyk = 12, CsSrgb = 16, CsGray = 17, CsSycc = 18, CsEsrgb = 20, CsEsycc = 24;

        static readonly byte[] ExifUuid = { 0x4A, 0x70, 0x67, 0x54, 0x69, 0x66, 0x66, 0x45, 0x78, 0x69, 0x66, 0x2D, 0x3E, 0x4A, 0x50, 0x32 };
        static readonly byte[] XmpUuid = { 0xBE, 0x7A, 0xCF, 0xCB, 0x97, 0xA9, 0x42, 0xE8, 0x9C, 0x71, 0x99, 0x94, 0x91, 0xE3, 0xAF, 0xAC };

        static ImageFormatException Bad(string what) => new ImageFormatException("invalid JP2 file: " + what);

        const uint JP = 0x6A502020, Ftyp = 0x66747970, Jp2h = 0x6A703268, Ihdr = 0x69686472, BpccBox = 0x62706363, Colr = 0x636F6C72,
            Pclr = 0x70636C72, CmapBox = 0x636D6170, CdefBox = 0x63646566, Res = 0x72657320, Resc = 0x72657363, Resd = 0x72657364,
            Jp2c = 0x6A703263, Uuid = 0x75756964, ExifBox = 0x45786966, XmlBox = 0x786D6C20;

        /// <summary>
        /// Parses the boxes. With <paramref name="headerOnly"/> it stops at the codestream box and insists that
        /// everything before it is complete (the data may be a prefix of the file); otherwise metadata boxes behind
        /// the codestream are read too and a cut-off trailing box is ignored.
        /// </summary>
        public static Jp2Header Parse(ReadOnlySpan<byte> data, bool headerOnly)
        {
            var h = new Jp2Header();
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0x4F)
            {
                h.CodestreamStart = 0; h.CodestreamLength = data.Length;
                return h;
            }
            h.IsJp2 = true;
            h.CodestreamStart = -1;
            long pos = 0;
            bool first = true, sawHeader = false;
            double capX = 0, capY = 0, dispX = 0, dispY = 0;
            while (pos + 8 <= data.Length)
            {
                long len = BinaryPrimitives.ReadUInt32BigEndian(data.Slice((int)pos));
                uint type = BinaryPrimitives.ReadUInt32BigEndian(data.Slice((int)pos + 4));
                long hdr = 8;
                if (len == 1)
                {
                    if (pos + 16 > data.Length) { if (headerOnly || h.CodestreamStart < 0) throw Bad("truncated box"); break; }
                    ulong xl = BinaryPrimitives.ReadUInt64BigEndian(data.Slice((int)pos + 8));
                    if (xl < 16 || xl > long.MaxValue) throw Bad("bad box length");
                    len = (long)xl; hdr = 16;
                }
                else if (len == 0) len = data.Length - pos;
                else if (len < 8) throw Bad("bad box length");
                if (first)
                {
                    if (type != JP || len != 12 || data.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8)) != 0x0D0A870A)
                        throw Bad("missing signature box");
                    first = false;
                    pos += 12;
                    continue;
                }
                long end = pos + len;
                if (type == Jp2c)
                {
                    // the codestream may run to the end of a file whose box length is wrong or that was cut
                    h.CodestreamStart = (int)(pos + hdr);
                    h.CodestreamLength = (int)(Math.Min(end, data.Length) - (pos + hdr));
                    if (headerOnly) break;
                    if (end > data.Length) break;
                    pos = end;
                    continue;
                }
                if (end > data.Length)
                {
                    if (headerOnly || h.CodestreamStart < 0) throw Bad("truncated box");
                    break;
                }
                var body = data.Slice((int)(pos + hdr), (int)(len - hdr));
                switch (type)
                {
                    case Jp2h:
                        if (!sawHeader) h.ParseHeaderBox(body, ref capX, ref capY, ref dispX, ref dispY);
                        sawHeader = true;
                        break;
                    case Uuid:
                        if (body.Length >= 16)
                        {
                            if (body.Slice(0, 16).SequenceEqual(ExifUuid) && h.Exif == null) h.SetExif(body.Slice(16));
                            else if (body.Slice(0, 16).SequenceEqual(XmpUuid) && h.Xmp == null) h.Xmp = body.Slice(16).ToArray();
                        }
                        break;
                    case ExifBox:
                        // ISO/IEC 15444-1 Exif box (as in JPEG XL / HEIF): 4 byte offset to the TIFF header, then the data
                        if (body.Length >= 4 && h.Exif == null)
                        {
                            uint off = BinaryPrimitives.ReadUInt32BigEndian(body);
                            if (off <= body.Length - 4) h.SetExif(body.Slice(4 + (int)off));
                        }
                        break;
                    case XmlBox:
                        if (h.Xmp == null && body.IndexOf("adobe:ns:meta"u8) >= 0) h.Xmp = body.ToArray();
                        break;
                }
                pos = end;
            }
            if (first) throw Bad("missing signature box");
            if (h.CodestreamStart < 0) throw Bad("no codestream box");
            if (capX > 0 && capY > 0) { h.DpiX = capX; h.DpiY = capY; }
            else if (dispX > 0 && dispY > 0) { h.DpiX = dispX; h.DpiY = dispY; }
            return h;
        }

        void SetExif(ReadOnlySpan<byte> e)
        {
            if (e.Length >= 6 && e.Slice(0, 6).SequenceEqual("Exif\0\0"u8)) e = e.Slice(6);
            if (e.Length < 8) return;
            Exif = e.ToArray();
            Orientation = ExifOrientation(e);
        }

        /// <summary>Orientation tag (274) of the first IFD of a TIFF structure, 0 if absent or damaged.</summary>
        internal static int ExifOrientation(ReadOnlySpan<byte> t)
        {
            if (t.Length < 8) return 0;
            bool le = t[0] == 'I' && t[1] == 'I';
            if (!le && !(t[0] == 'M' && t[1] == 'M')) return 0;
            ushort U16(ReadOnlySpan<byte> s) => le ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);
            uint U32(ReadOnlySpan<byte> s) => le ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);
            if (U16(t.Slice(2)) != 42) return 0;
            uint ifd = U32(t.Slice(4));
            if (ifd > t.Length - 2) return 0;
            int n = U16(t.Slice((int)ifd));
            for (int i = 0; i < n; i++)
            {
                long e = ifd + 2 + 12L * i;
                if (e + 12 > t.Length) return 0;
                var entry = t.Slice((int)e, 12);
                if (U16(entry) != 274) continue;
                int type = U16(entry.Slice(2));
                uint v = type == 3 ? U16(entry.Slice(8)) : type == 4 ? U32(entry.Slice(8)) : type == 1 ? entry[8] : 0u;
                return v >= 1 && v <= 8 ? (int)v : 0;
            }
            return 0;
        }

        void ParseHeaderBox(ReadOnlySpan<byte> data, ref double capX, ref double capY, ref double dispX, ref double dispY)
        {
            int pos = 0;
            while (pos + 8 <= data.Length)
            {
                long len = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos));
                uint type = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos + 4));
                int hdr = 8;
                if (len == 1)
                {
                    if (pos + 16 > data.Length) throw Bad("truncated box");
                    ulong xl = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(pos + 8));
                    if (xl < 16 || xl > int.MaxValue) throw Bad("bad box length");
                    len = (long)xl; hdr = 16;
                }
                else if (len == 0) len = data.Length - pos;
                if (len < hdr || pos + len > data.Length) throw Bad("bad box length");
                var b = data.Slice(pos + hdr, (int)len - hdr);
                switch (type)
                {
                    case Ihdr:
                        if (b.Length < 14) throw Bad("bad image header box");
                        Height = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(b), int.MaxValue);
                        Width = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4)), int.MaxValue);
                        NumComps = BinaryPrimitives.ReadUInt16BigEndian(b.Slice(8));
                        Bpc = b[10];
                        HasIhdr = true;
                        break;
                    case BpccBox:
                        Bpcc = b.ToArray();
                        break;
                    case Colr:
                        // the first colour specification a reader understands counts
                        if (ColorMethod != 0 || b.Length < 3) break;
                        if (b[0] == 1 && b.Length >= 7) { ColorMethod = 1; EnumCs = (int)BinaryPrimitives.ReadUInt32BigEndian(b.Slice(3)); }
                        else if ((b[0] == 2 || b[0] == 3) && b.Length > 3 + 128) { ColorMethod = 2; Icc = b.Slice(3).ToArray(); }
                        break;
                    case Pclr:
                    {
                        if (b.Length < 3) throw Bad("bad palette box");
                        int ne = BinaryPrimitives.ReadUInt16BigEndian(b), npc = b[2];
                        if (ne < 1 || ne > 1024 || npc < 1 || b.Length < 3 + npc) throw Bad("bad palette box");
                        var depth = b.Slice(3, npc).ToArray();
                        var pal = new int[ne * npc];
                        int q = 3 + npc;
                        for (int i = 0; i < ne; i++)
                            for (int c = 0; c < npc; c++)
                            {
                                int bits = (depth[c] & 0x7F) + 1;
                                if (bits > 32) throw new ImageNotSupportedException("JP2 palette entries wider than 32 bit");
                                int bytes = (bits + 7) >> 3;
                                if (q + bytes > b.Length) throw Bad("bad palette box");
                                long v = 0;
                                for (int k = 0; k < bytes; k++) v = v << 8 | b[q++];
                                if ((depth[c] & 0x80) != 0) v = v << (64 - bits) >> (64 - bits);
                                else v = Math.Min(v, (1L << bits) - 1);
                                pal[i * npc + c] = (int)v;
                            }
                        PaletteEntries = ne; PaletteCols = npc; PaletteDepth = depth; Palette = pal;
                        break;
                    }
                    case CmapBox:
                    {
                        var m = new (int, int, int)[b.Length / 4];
                        for (int i = 0; i < m.Length; i++)
                            m[i] = (BinaryPrimitives.ReadUInt16BigEndian(b.Slice(4 * i)), b[4 * i + 2], b[4 * i + 3]);
                        Cmap = m;
                        break;
                    }
                    case CdefBox:
                    {
                        if (b.Length < 2) throw Bad("bad channel definition box");
                        int n = BinaryPrimitives.ReadUInt16BigEndian(b);
                        if (b.Length < 2 + 6 * n) throw Bad("bad channel definition box");
                        var d = new (int, int, int)[n];
                        for (int i = 0; i < n; i++)
                            d[i] = (BinaryPrimitives.ReadUInt16BigEndian(b.Slice(2 + 6 * i)), BinaryPrimitives.ReadUInt16BigEndian(b.Slice(4 + 6 * i)),
                                BinaryPrimitives.ReadUInt16BigEndian(b.Slice(6 + 6 * i)));
                        Cdef = d;
                        break;
                    }
                    case Res:
                    {
                        int p = 0;
                        while (p + 8 <= b.Length)
                        {
                            long l = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(p));
                            uint t = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(p + 4));
                            if (l < 8 || p + l > b.Length) break;
                            if ((t == Resc || t == Resd) && l >= 18)
                            {
                                var r = b.Slice(p + 8);
                                double vn = BinaryPrimitives.ReadUInt16BigEndian(r), vd = BinaryPrimitives.ReadUInt16BigEndian(r.Slice(2));
                                double hn = BinaryPrimitives.ReadUInt16BigEndian(r.Slice(4)), hd = BinaryPrimitives.ReadUInt16BigEndian(r.Slice(6));
                                if (vd != 0 && hd != 0)
                                {
                                    // pixels per metre
                                    double y = vn / vd * Math.Pow(10, (sbyte)r[8]) * 0.0254, x = hn / hd * Math.Pow(10, (sbyte)r[9]) * 0.0254;
                                    if (t == Resc) { capX = x; capY = y; } else { dispX = x; dispY = y; }
                                }
                            }
                            p += (int)l;
                        }
                        break;
                    }
                }
                pos += (int)len;
            }
        }

        // ---------------- writing ----------------

        static void Box(Stream s, uint type, ReadOnlySpan<byte> body)
        {
            Span<byte> h = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(h, (uint)(8 + body.Length));
            BinaryPrimitives.WriteUInt32BigEndian(h.Slice(4), type);
            s.Write(h);
            s.Write(body);
        }

        /// <summary>Writes the boxes in front of the codestream box and the header of the codestream box itself.</summary>
        /// <param name="s">The output stream.</param>
        /// <param name="width">Image width.</param>
        /// <param name="height">Image height.</param>
        /// <param name="depth">Bit depth per channel.</param>
        /// <param name="signed">Per channel: samples are signed.</param>
        /// <param name="colours">1 gray, 3 RGB (the first channels); 0: the channels have no colour meaning, they are all declared
        /// as unassociated so that readers do not take three of them for red, green and blue.</param>
        /// <param name="alpha">The last channel is an alpha channel.</param>
        /// <param name="ht">High throughput codestream (brand "jph "), else "jp2 ".</param>
        /// <param name="meta">Metadata to write as boxes, or null.</param>
        /// <param name="codestreamLength">Length of the codestream in bytes.</param>
        public static void WriteHeader(Stream s, int width, int height, int[] depth, bool[] signed, int colours, bool alpha, bool ht,
            ImageMetadata? meta, long codestreamLength)
        {
            Box(s, JP, new byte[] { 0x0D, 0x0A, 0x87, 0x0A });
            var brand = ht ? "jph "u8 : "jp2 "u8;
            var ftyp = new byte[12];
            brand.CopyTo(ftyp); brand.CopyTo(ftyp.AsSpan(8));
            Box(s, Ftyp, ftyp);

            var jp2h = new MemoryStream();
            int nc = depth.Length;
            bool same = true;
            for (int i = 1; i < nc; i++) same &= depth[i] == depth[0] && signed[i] == signed[0];
            var ihdr = new byte[14];
            BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)height);
            BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)width);
            BinaryPrimitives.WriteUInt16BigEndian(ihdr.AsSpan(8), (ushort)nc);
            ihdr[10] = same ? (byte)(depth[0] - 1 | (signed[0] ? 0x80 : 0)) : (byte)255;
            ihdr[11] = 7;
            Box(jp2h, Ihdr, ihdr);
            if (!same)
            {
                var bpcc = new byte[nc];
                for (int i = 0; i < nc; i++) bpcc[i] = (byte)(depth[i] - 1 | (signed[i] ? 0x80 : 0));
                Box(jp2h, BpccBox, bpcc);
            }
            var icc = meta?.IccProfile;
            if (icc != null && icc.Length > 128)
            {
                var colr = new byte[3 + icc.Length];
                colr[0] = 2;
                icc.CopyTo(colr, 3);
                Box(jp2h, Colr, colr);
            }
            else
            {
                var colr = new byte[7];
                colr[0] = 1;
                BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(3), colours == 1 || (colours == 0 && nc < 3) ? (uint)CsGray : (uint)CsSrgb);
                Box(jp2h, Colr, colr);
            }
            if (alpha || nc > colours)
            {
                var cdef = new byte[2 + 6 * nc];
                BinaryPrimitives.WriteUInt16BigEndian(cdef, (ushort)nc);
                for (int i = 0; i < nc; i++)
                {
                    var e = cdef.AsSpan(2 + 6 * i);
                    BinaryPrimitives.WriteUInt16BigEndian(e, (ushort)i);
                    bool isAlpha = alpha && i == nc - 1;
                    bool colour = i < colours;
                    BinaryPrimitives.WriteUInt16BigEndian(e.Slice(2), (ushort)(colour ? 0 : isAlpha ? 1 : 65535));
                    BinaryPrimitives.WriteUInt16BigEndian(e.Slice(4), (ushort)(colour ? i + 1 : isAlpha ? 0 : 65535));
                }
                Box(jp2h, CdefBox, cdef);
            }
            if (meta != null && meta.DpiX > 0 && meta.DpiY > 0)
            {
                var r = new byte[10];
                var (vn, ve) = ResFraction(meta.DpiY);
                var (hn, he) = ResFraction(meta.DpiX);
                BinaryPrimitives.WriteUInt16BigEndian(r, vn); BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(2), 127);
                BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(4), hn); BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(6), 127);
                r[8] = (byte)ve; r[9] = (byte)he;
                var inner = new MemoryStream();
                Box(inner, Resc, r);
                Box(jp2h, Res, inner.ToArray());
            }
            Box(s, Jp2h, jp2h.ToArray());

            if (ExifTags.ForWriting(meta) is { Length: >= 8 } exif)
            {
                var u = new byte[16 + exif.Length];
                ExifUuid.CopyTo(u, 0);
                exif.CopyTo(u, 16);
                Box(s, Uuid, u);
            }
            if (ExifTags.XmpForWriting(meta) is { Length: > 0 } xmp)
            {
                // XMP part 3: a uuid box with the XMP UUID
                var u = new byte[16 + xmp.Length];
                XmpUuid.CopyTo(u, 0);
                xmp.CopyTo(u, 16);
                Box(s, Uuid, u);
            }

            Span<byte> h = stackalloc byte[16];
            if (codestreamLength + 8 <= uint.MaxValue)
            {
                BinaryPrimitives.WriteUInt32BigEndian(h, (uint)(8 + codestreamLength));
                BinaryPrimitives.WriteUInt32BigEndian(h.Slice(4), Jp2c);
                s.Write(h.Slice(0, 8));
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(h, 1);
                BinaryPrimitives.WriteUInt32BigEndian(h.Slice(4), Jp2c);
                BinaryPrimitives.WriteUInt64BigEndian(h.Slice(8), (ulong)(16 + codestreamLength));
                s.Write(h);
            }
        }

        /// <summary>
        /// Pixels per inch as the numerator of num / 127 * 10^exp pixels per metre (an inch is 127 / 5000 m, so
        /// whole dpi values come out exact).
        /// </summary>
        static (ushort num, sbyte exp) ResFraction(double dpi)
        {
            double n = dpi * 5000;
            int e = 0;
            while (n > 65535 && e < 100) { n /= 10; e++; }
            while (n * 10 <= 65535 && e > -100 && Math.Abs(n - Math.Round(n)) > 1e-9 * n) { n *= 10; e--; }
            return ((ushort)Math.Clamp(Math.Round(n), 1, 65535), (sbyte)e);
        }
    }
}
