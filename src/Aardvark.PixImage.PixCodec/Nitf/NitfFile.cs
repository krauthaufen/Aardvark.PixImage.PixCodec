using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Aardvark.ImageCodecs
{
    /// <summary>A tagged record extension (TRE): a six character tag and its data, kept as stored (MIL-STD-2500C 5.8.1).</summary>
    public sealed class NitfTre
    {
        public NitfTre(string tag, byte[] data)
        {
            ArgumentNullException.ThrowIfNull(tag);
            ArgumentNullException.ThrowIfNull(data);
            if (tag.Length == 0 || tag.Length > 6) throw new ArgumentException("a TRE tag has one to six characters", nameof(tag));
            Tag = tag;
            Data = data;
        }

        /// <summary>CETAG, without trailing spaces.</summary>
        public string Tag { get; }

        /// <summary>CEDATA as stored.</summary>
        public byte[] Data { get; }

        /// <summary>The data read as 8 bit text (TREs are mostly BCS fields).</summary>
        public string Text => Encoding.Latin1.GetString(Data);

        public override string ToString() => Tag + " (" + Data.Length.ToString(CultureInfo.InvariantCulture) + " bytes)";
    }

    /// <summary>The header fields of a NITF file, segment subheader or DES, in file order, each as its stored text.</summary>
    public sealed class NitfFields
    {
        readonly List<KeyValuePair<string, string>> _list = new();

        /// <summary>Every field read, in file order (repeating fields such as ICOMn carry their number).</summary>
        public IReadOnlyList<KeyValuePair<string, string>> All => _list;

        /// <summary>A field's stored text (spaces kept), null when the file does not hold the field.</summary>
        public string? this[string name]
        {
            get
            {
                foreach (var kv in _list) if (kv.Key == name) return kv.Value;
                return null;
            }
        }

        internal void Add(string name, string value) => _list.Add(new KeyValuePair<string, string>(name, value));
    }

    /// <summary>Kinds of NITF segments besides images.</summary>
    public enum NitfSegmentKind
    {
        /// <summary>Graphic segment (NITF 2.1; the symbols of NITF 2.0).</summary>
        Graphic,
        /// <summary>Label segment (NITF 2.0 only).</summary>
        Label,
        Text,
        DataExtension,
        ReservedExtension,
    }

    /// <summary>Where a segment's subheader and data lie in the file. Only data extensions have their subheader read.</summary>
    public sealed class NitfSegment
    {
        public NitfSegmentKind Kind { get; internal set; }
        public long SubheaderOffset { get; internal set; }
        public int SubheaderLength { get; internal set; }
        public long DataOffset { get; internal set; }
        public long DataLength { get; internal set; }

        /// <summary>The subheader fields of a data extension (DESID, DESVER, security fields, ...); empty for other kinds.</summary>
        public NitfFields Fields { get; } = new();

        /// <summary>DESID without trailing spaces, null for other kinds.</summary>
        public string? DesId { get; internal set; }
    }

    /// <summary>One band of an image segment.</summary>
    public sealed class NitfBand
    {
        /// <summary>IREPBANDn, trimmed: R, G, B, M, LU, Y, Cb, Cr or empty.</summary>
        public string Representation { get; internal set; } = "";

        /// <summary>ISUBCATn, trimmed (a wavelength in nanometres for multispectral imagery, I / Q for SAR, ...).</summary>
        public string Subcategory { get; internal set; } = "";

        /// <summary>The band's look-up tables (NLUTSn of them, NELUTn entries each), empty when there are none.</summary>
        public byte[][] LookUpTables { get; internal set; } = Array.Empty<byte[]>();
    }

    /// <summary>An image segment: its subheader, read completely, and where its data lies.</summary>
    public sealed class NitfImageSegment
    {
        /// <summary>Every subheader field as stored, in file order.</summary>
        public NitfFields Fields { get; } = new();

        public long SubheaderOffset { get; internal set; }
        public int SubheaderLength { get; internal set; }
        public long DataOffset { get; internal set; }
        public long DataLength { get; internal set; }

        /// <summary>IID1 (IID in NITF 2.0), trimmed.</summary>
        public string Id { get; internal set; } = "";

        /// <summary>IID2 (ITITLE in NITF 2.0), trimmed.</summary>
        public string Title { get; internal set; } = "";

        /// <summary>ISCLAS: T, S, C, R or U.</summary>
        public char Classification { get; internal set; }

        /// <summary>ENCRYP is not 0: the data cannot be decoded.</summary>
        public bool Encrypted { get; internal set; }

        /// <summary>NROWS / NCOLS: the significant samples.</summary>
        public int Rows { get; internal set; }
        public int Columns { get; internal set; }

        /// <summary>PVTYPE, trimmed: INT, B, SI, R or C.</summary>
        public string PixelValueType { get; internal set; } = "";

        /// <summary>IREP, trimmed: MONO, RGB, RGB/LUT, MULTI, NODISPLY, YCbCr601, ...</summary>
        public string Representation { get; internal set; } = "";

        /// <summary>ICAT, trimmed: VIS, MS, SAR, ...</summary>
        public string Category { get; internal set; } = "";

        /// <summary>ABPP: significant bits of each sample (NBPP when the field is 0).</summary>
        public int ActualBitsPerPixel { get; internal set; }

        /// <summary>PJUST: where the significant bits sit when ABPP is less than NBPP, 'L' or 'R'.</summary>
        public char Justification { get; internal set; }

        /// <summary>ICORDS: ' ' (none; 'N' in NITF 2.0), 'G', 'D', 'N', 'S', 'U' (and 'C' in NITF 2.0).</summary>
        public char CoordinateSystem { get; internal set; }

        /// <summary>IGEOLO as stored, null when the subheader has none.</summary>
        public string? GeoLocation { get; internal set; }

        /// <summary>ICOMn, trailing spaces removed.</summary>
        public string[] Comments { get; internal set; } = Array.Empty<string>();

        /// <summary>IC: NC, NM, C3, M3, C8, M8, ...</summary>
        public string Compression { get; internal set; } = "";

        /// <summary>COMRAT, trimmed, null when the segment is uncompressed.</summary>
        public string? CompressionRate { get; internal set; }

        public NitfBand[] Bands { get; internal set; } = Array.Empty<NitfBand>();

        /// <summary>IMODE: 'B' interleaved by block, 'P' by pixel, 'R' by row, 'S' band sequential.</summary>
        public char Mode { get; internal set; }

        /// <summary>NBPR / NBPC: blocks per row of blocks and per column of blocks.</summary>
        public int BlocksPerRow { get; internal set; }
        public int BlocksPerColumn { get; internal set; }

        /// <summary>NPPBH / NPPBV, with 0 resolved to NCOLS / NROWS.</summary>
        public int BlockWidth { get; internal set; }
        public int BlockHeight { get; internal set; }

        /// <summary>NBPP: storage bits of each sample.</summary>
        public int BitsPerPixel { get; internal set; }

        /// <summary>IDLVL / IALVL.</summary>
        public int DisplayLevel { get; internal set; }
        public int AttachmentLevel { get; internal set; }

        /// <summary>ILOC: row and column offset of the first pixel.</summary>
        public (int Row, int Column) Location { get; internal set; }

        /// <summary>IMAG, trimmed.</summary>
        public string Magnification { get; internal set; } = "";

        /// <summary>The TREs of UDID and IXSHD, plus those a TRE_OVERFLOW extension adds, in that order.</summary>
        public List<NitfTre> Tres { get; } = new();

        public override string ToString()
            => $"{Id} {Columns}x{Rows}x{Bands.Length} {PixelValueType}{BitsPerPixel} {Representation} IC={Compression} IMODE={Mode}";
    }

    /// <summary>A parsed NITF / NSIF file: header, segments and their places. Built by <see cref="Nitf.ReadFile(ReadOnlySpan{byte})"/>.</summary>
    public sealed class NitfFile
    {
        /// <summary>FHDR + FVER: "NITF02.10", "NSIF01.00" or "NITF02.00".</summary>
        public string Version { get; internal set; } = "";

        /// <summary>Whether this is a NITF 2.0 file (its own header layout, MIL-STD-2500A).</summary>
        public bool IsVersion20 => Version == "NITF02.00";

        /// <summary>Every file header field as stored, in file order.</summary>
        public NitfFields Fields { get; } = new();

        /// <summary>FTITLE, trimmed.</summary>
        public string Title { get; internal set; } = "";

        /// <summary>FSCLAS: T, S, C, R or U.</summary>
        public char Classification { get; internal set; }

        /// <summary>The TREs of UDHD and XHD, plus those a TRE_OVERFLOW extension adds.</summary>
        public List<NitfTre> Tres { get; } = new();

        public NitfImageSegment[] Images { get; internal set; } = Array.Empty<NitfImageSegment>();

        /// <summary>Graphic, label, text, data and reserved extension segments, in file order.</summary>
        public NitfSegment[] OtherSegments { get; internal set; } = Array.Empty<NitfSegment>();

        /// <summary>Problems the reader stepped over: malformed TREs, an overflow pointing nowhere, a wrong file length.</summary>
        public List<string> Warnings { get; } = new();
    }

    /// <summary>Reads the header structure of a NITF file (MIL-STD-2500C for 2.1 and NSIF 1.0, MIL-STD-2500A for 2.0).</summary>
    internal ref struct NitfParser
    {
        readonly ReadOnlySpan<byte> _d;
        long _p;
        NitfFields _fields = new();
        readonly NitfFile _file;

        // the TRE areas whose overflow can go into a DES, keyed (area, item)
        readonly Dictionary<(string, int), List<NitfTre>> _overflowTargets;

        NitfParser(ReadOnlySpan<byte> data)
        {
            _d = data;
            _p = 0;
            _file = new NitfFile();
            _fields = _file.Fields;
            _overflowTargets = new();
        }

        internal static NitfFile Parse(ReadOnlySpan<byte> data) => new NitfParser(data).Run();

        string Field(string name, int length)
        {
            if (_p + length > _d.Length) throw new ImageFormatException($"NITF: the file ends inside the {name} field");
            var s = Encoding.Latin1.GetString(_d.Slice((int)_p, length));
            _p += length;
            _fields.Add(name, s);
            return s;
        }

        long Number(string name, int length)
        {
            var s = Field(name, length).Trim();
            if (s.Length == 0) return 0;
            if (!long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v))
                throw new ImageFormatException($"NITF: the {name} field holds '{s}', not a number");
            return v;
        }

        int Int(string name, int length) => checked((int)Number(name, length));

        NitfFile Run()
        {
            var f = _file;
            _fields = f.Fields;
            if (_d.Length < 9) throw new ImageFormatException("NITF: the file is too short for a header");
            f.Version = Encoding.Latin1.GetString(_d.Slice(0, 9));
            if (f.Version is not ("NITF02.10" or "NSIF01.00" or "NITF02.00"))
                throw new ImageFormatException($"NITF: '{f.Version}' is not a NITF 2.0 / 2.1 or NSIF 1.0 file");
            bool v20 = f.IsVersion20;

            Field("FHDR", 4); Field("FVER", 5);
            Field("CLEVEL", 2); Field("STYPE", 4); Field("OSTAID", 10); Field("FDT", 14);
            f.Title = Field("FTITLE", 80).Trim();
            f.Classification = Security(v20, "FS", "FSCLAS");
            Field("FSCOP", 5); Field("FSCPYS", 5); Field("ENCRYP", 1);
            if (!v20) Field("FBKGC", 3);
            Field("ONAME", v20 ? 27 : 24); Field("OPHONE", 18);
            long fl = Number("FL", 12);
            int hl = Int("HL", 6);

            var images = new List<(int Sub, long Data)>();
            int numi = Int("NUMI", 3);
            for (int i = 1; i <= numi; i++) images.Add((Int($"LISH{i:000}", 6), Number($"LI{i:000}", 10)));
            var others = new List<(NitfSegmentKind Kind, int Sub, long Data)>();
            Others(others, NitfSegmentKind.Graphic, "NUMS", "LSSH", 4, "LS", 6);
            if (v20) Others(others, NitfSegmentKind.Label, "NUML", "LLSH", 4, "LL", 3);
            else Field("NUMX", 3);
            Others(others, NitfSegmentKind.Text, "NUMT", "LTSH", 4, "LT", 5);
            Others(others, NitfSegmentKind.DataExtension, "NUMDES", "LDSH", 4, "LD", 9);
            Others(others, NitfSegmentKind.ReservedExtension, "NUMRES", "LRESH", 4, "LRE", 7);
            Tres("UDHDL", "UDHOFL", "UDHD", f.Tres, "UDHD", 0);
            Tres("XHDL", "XHDLOFL", "XHD", f.Tres, "XHD", 0);
            if (_p != hl) f.Warnings.Add($"NITF: the file header is {_p} bytes long, but HL says {hl}");

            // segments follow the header back to back, in the order the header lists them
            long at = hl;
            var segs = new List<NitfImageSegment>();
            for (int i = 0; i < images.Count; i++)
            {
                var (sub, len) = images[i];
                Need(at, sub + len, $"image segment {i + 1}");
                var img = new NitfImageSegment { SubheaderOffset = at, SubheaderLength = sub, DataOffset = at + sub, DataLength = len };
                ImageSubheader(img, v20, i + 1);
                segs.Add(img);
                at += sub + len;
            }
            f.Images = segs.ToArray();
            var rest = new List<NitfSegment>();
            foreach (var (kind, sub, len) in others)
            {
                Need(at, sub + len, kind.ToString() + " segment");
                rest.Add(new NitfSegment { Kind = kind, SubheaderOffset = at, SubheaderLength = sub, DataOffset = at + sub, DataLength = len });
                at += sub + len;
            }
            f.OtherSegments = rest.ToArray();
            if (fl != at && fl != 999999999999) f.Warnings.Add($"NITF: FL says {fl} bytes, the segments end at {at}");

            int desIndex = 0;
            foreach (var s in f.OtherSegments)
                if (s.Kind == NitfSegmentKind.DataExtension) DataExtension(s, v20, ++desIndex);
            return f;
        }

        void Others(List<(NitfSegmentKind, int, long)> into, NitfSegmentKind kind, string count, string sub, int subLen, string data, int dataLen)
        {
            int n = Int(count, 3);
            for (int i = 1; i <= n; i++) into.Add((kind, Int($"{sub}{i:000}", subLen), Number($"{data}{i:000}", dataLen)));
        }

        void Need(long at, long length, string what)
        {
            if (length < 0 || at + length > _d.Length)
                throw new ImageFormatException($"NITF: the file ends inside {what} (it needs {at + length} bytes, the file has {_d.Length})");
        }

        // the security block; returns the classification letter
        char Security(bool v20, string p, string clas)
        {
            string c = Field(clas, 1);
            if (v20)
            {
                Field(p + "CODE", 40); Field(p + "CTLH", 40); Field(p + "REL", 40); Field(p + "CAUT", 20); Field(p + "CTLN", 20);
                if (Field(p + "DWNG", 6) == "999998") Field(p + "DEVT", 40);
            }
            else
            {
                Field(p + "CLSY", 2); Field(p + "CODE", 11); Field(p + "CTLH", 2); Field(p + "REL", 20);
                Field(p + "DCTP", 2); Field(p + "DCDT", 8); Field(p + "DCXM", 4); Field(p + "DG", 1); Field(p + "DGDT", 8);
                Field(p + "CLTX", 43); Field(p + "CATP", 1); Field(p + "CAUT", 40); Field(p + "CRSN", 1); Field(p + "SRDT", 8);
                Field(p + "CTLN", 15);
            }
            return c[0];
        }

        // a length field, its overflow field and the TREs after them (UDHD, XHD, UDID, IXSHD)
        void Tres(string lengthName, string overflowName, string area, List<NitfTre> into, string overflowArea, int item)
        {
            int len = Int(lengthName, 5);
            _overflowTargets[(overflowArea, item)] = into;
            if (len == 0) return;
            if (len < 3) { _file.Warnings.Add($"NITF: {lengthName} is {len}, less than its overflow field"); return; }
            Field(overflowName, 3);
            long start = _p, end = _p + len - 3;
            if (end > _d.Length) throw new ImageFormatException($"NITF: the file ends inside the {area} field");
            _p = end;
            ParseTres(start, end, into, area);
        }

        void ParseTres(long start, long end, List<NitfTre> into, string area)
        {
            long p = start;
            while (p < end)
            {
                if (end - p < 11) { _file.Warnings.Add($"NITF: {end - p} stray bytes at the end of {area}"); return; }
                string tag = Encoding.Latin1.GetString(_d.Slice((int)p, 6)).TrimEnd();
                string ls = Encoding.Latin1.GetString(_d.Slice((int)p + 6, 5));
                if (!int.TryParse(ls, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || p + 11 + n > end)
                {
                    _file.Warnings.Add($"NITF: the TRE '{tag}' in {area} has the length '{ls}', which does not fit; the rest of {area} is skipped");
                    return;
                }
                var data = _d.Slice((int)p + 11, n).ToArray();
                into.Add(new NitfTre(tag.Length == 0 ? "?" : tag, data));
                p += 11 + n;
            }
        }

        void ImageSubheader(NitfImageSegment img, bool v20, int item)
        {
            _fields = img.Fields;
            _p = img.SubheaderOffset;
            if (Field("IM", 2) != "IM") throw new ImageFormatException($"NITF: image segment {item} does not start with 'IM'");
            img.Id = Field(v20 ? "IID" : "IID1", 10).Trim();
            Field("IDATIM", 14); Field("TGTID", 17);
            img.Title = Field(v20 ? "ITITLE" : "IID2", 80).Trim();
            img.Classification = Security(v20, "IS", "ISCLAS");
            img.Encrypted = Field("ENCRYP", 1) != "0";
            Field("ISORCE", 42);
            img.Rows = Int("NROWS", 8);
            img.Columns = Int("NCOLS", 8);
            img.PixelValueType = Field("PVTYPE", 3).Trim();
            img.Representation = Field("IREP", 8).Trim();
            img.Category = Field("ICAT", 8).Trim();
            int abpp = Int("ABPP", 2);
            string pj = Field("PJUST", 1);
            // 2.0: "any value other than L in this field shall indicate right justified"; 2.1 allows only L and R
            img.Justification = pj == "L" ? 'L' : 'R';
            char icords = Field("ICORDS", 1)[0];
            img.CoordinateSystem = icords;
            bool hasGeo = v20 ? icords != 'N' && icords != ' ' : icords != ' ';
            if (hasGeo) img.GeoLocation = Field("IGEOLO", 60);
            int nicom = Int("NICOM", 1);
            var comments = new string[nicom];
            for (int i = 0; i < nicom; i++) comments[i] = Field($"ICOM{i + 1}", 80).TrimEnd();
            img.Comments = comments;
            img.Compression = Field("IC", 2);
            if (img.Compression is not ("NC" or "NM")) img.CompressionRate = Field("COMRAT", 4).Trim();
            int nbands = Int("NBANDS", 1);
            if (nbands == 0)
            {
                if (v20) throw new ImageFormatException("NITF: NBANDS is 0 in a NITF 2.0 file");
                nbands = Int("XBANDS", 5);
            }
            var bands = new NitfBand[nbands];
            for (int b = 0; b < nbands; b++)
            {
                var band = bands[b] = new NitfBand();
                band.Representation = Field($"IREPBAND{b + 1}", 2).Trim();
                band.Subcategory = Field($"ISUBCAT{b + 1}", 6).Trim();
                Field($"IFC{b + 1}", 1); Field($"IMFLT{b + 1}", 3);
                int nluts = Int($"NLUTS{b + 1}", 1);
                if (nluts > 0)
                {
                    int nelut = Int($"NELUT{b + 1}", 5);
                    if (nelut < 1 || nelut > 65536) throw new ImageFormatException($"NITF: NELUT{b + 1} is {nelut}");
                    var luts = new byte[nluts][];
                    for (int m = 0; m < nluts; m++)
                    {
                        if (_p + nelut > _d.Length) throw new ImageFormatException("NITF: the file ends inside a look-up table");
                        luts[m] = _d.Slice((int)_p, nelut).ToArray();
                        _p += nelut;
                    }
                    band.LookUpTables = luts;
                }
            }
            img.Bands = bands;
            Field("ISYNC", 1);
            img.Mode = Field("IMODE", 1)[0];
            img.BlocksPerRow = Int("NBPR", 4);
            img.BlocksPerColumn = Int("NBPC", 4);
            int nppbh = Int("NPPBH", 4), nppbv = Int("NPPBV", 4);
            img.BlockWidth = nppbh == 0 && img.BlocksPerRow == 1 ? img.Columns : nppbh;
            img.BlockHeight = nppbv == 0 && img.BlocksPerColumn == 1 ? img.Rows : nppbv;
            img.BitsPerPixel = Int("NBPP", 2);
            img.ActualBitsPerPixel = abpp == 0 ? img.BitsPerPixel : abpp;
            img.DisplayLevel = Int("IDLVL", 3);
            img.AttachmentLevel = Int("IALVL", 3);
            string iloc = Field("ILOC", 10);
            img.Location = (ParseInt(iloc.Substring(0, 5)), ParseInt(iloc.Substring(5, 5)));
            img.Magnification = Field("IMAG", 4).Trim();
            Tres("UDIDL", "UDOFL", "UDID", img.Tres, "UDID", item);
            Tres("IXSHDL", "IXSOFL", "IXSHD", img.Tres, "IXSHD", item);
            // the data starts where LISH says, whatever the fields added up to
            if (_p != img.SubheaderOffset + img.SubheaderLength)
                _file.Warnings.Add($"NITF: image subheader {item} is {_p - img.SubheaderOffset} bytes long, but LISH says {img.SubheaderLength}");
        }

        static int ParseInt(string s)
            => int.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v) ? v : 0;

        void DataExtension(NitfSegment s, bool v20, int item)
        {
            _fields = s.Fields;
            _p = s.SubheaderOffset;
            try
            {
                if (Field("DE", 2) != "DE") { _file.Warnings.Add($"NITF: data extension {item} does not start with 'DE'"); return; }
                s.DesId = Field("DESID", 25).TrimEnd();
                Field("DESVER", 2);
                Security(v20, "DES", "DECLAS");
                bool overflow = v20 ? s.DesId is "Registered Extensions" or "Controlled Extensions" : s.DesId == "TRE_OVERFLOW";
                if (!overflow) return;
                string area = Field("DESOFLW", 6).Trim();
                int target = Int("DESITEM", 3);
                // the DES data holds the TREs that did not fit into the named area of the named segment (5.8.3.1)
                int index = area is "UDHD" or "XHD" ? 0 : target;
                if (_overflowTargets.TryGetValue((area, index), out var into))
                    ParseTres(s.DataOffset, s.DataOffset + s.DataLength, into, $"the overflow extension {item}");
                else _file.Warnings.Add($"NITF: data extension {item} overflows {area} of item {target}, which this reader does not keep");
            }
            catch (ImageFormatException e) { _file.Warnings.Add(e.Message); }
        }
    }
}
