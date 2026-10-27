using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Aardvark.ImageCodecs
{
    /// <summary>How the bands of an ENVI sample file follow each other.</summary>
    public enum EnviInterleave
    {
        /// <summary>Band sequential: all lines of band 0, then band 1, ...</summary>
        Bsq,
        /// <summary>Band interleaved by line: line 0 of every band, then line 1, ...</summary>
        Bil,
        /// <summary>Band interleaved by pixel: every band of pixel 0, then pixel 1, ...</summary>
        Bip,
    }

    /// <summary>
    /// The text header of an ENVI raster (the ".hdr" file): "ENVI" on the first line, then "field = value" lines, a value in
    /// braces may run over several lines, lines starting with ";" are comments. Every field is kept in
    /// <see cref="Fields"/> in file order; the properties are the fields this library interprets.
    /// </summary>
    public sealed class EnviHeader
    {
        /// <summary>Every field as written: the name, and the value without its braces.</summary>
        public List<KeyValuePair<string, string>> Fields { get; } = new();

        /// <summary>Samples per line (the width).</summary>
        public int Samples { get; set; }
        /// <summary>Lines per band (the height).</summary>
        public int Lines { get; set; }
        public int Bands { get; set; } = 1;
        /// <summary>The ENVI data type code (1 byte, 2 int16, 3 int32, 4 float, 5 double, 6 complex, 9 double complex, 12 uint16, 13 uint32, 14 int64, 15 uint64).</summary>
        public int DataType { get; set; }
        public EnviInterleave Interleave { get; set; }
        /// <summary>"byte order = 1": samples are most significant byte first.</summary>
        public bool BigEndian { get; set; }
        /// <summary>Bytes to skip at the start of the sample file.</summary>
        public long HeaderOffset { get; set; }

        /// <summary>The value of a field (case and spacing of the name ignored), null when absent.</summary>
        public string? this[string name]
        {
            get
            {
                string n = Normalize(name);
                for (int i = Fields.Count - 1; i >= 0; i--)
                    if (Normalize(Fields[i].Key) == n) return Fields[i].Value;
                return null;
            }
        }

        public string? FileType => this["file type"];
        public string? Description => this["description"];
        public string? MapInfo => this["map info"];
        public string? CoordinateSystemString => this["coordinate system string"];
        public string? WavelengthUnits => this["wavelength units"];
        public string[]? BandNames => List(this["band names"]);
        public double[]? Wavelengths => Numbers(this["wavelength"]);
        public double[]? Fwhm => Numbers(this["fwhm"]);
        /// <summary>"bbl": 1 for a good band, 0 for a bad one.</summary>
        public double[]? BadBandList => Numbers(this["bbl"]);
        public string[]? ClassNames => List(this["class names"]);
        public double? DataIgnoreValue => Numbers(this["data ignore value"]) is { Length: > 0 } v ? v[0] : null;

        static string Normalize(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var part in name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(part.ToLowerInvariant());
            }
            return sb.ToString();
        }

        /// <summary>The comma separated entries of a list value, trimmed; null for an absent field.</summary>
        public static string[]? List(string? value)
        {
            if (value == null) return null;
            var parts = value.Split(',');
            var r = new List<string>(parts.Length);
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length > 0) r.Add(t);
            }
            return r.ToArray();
        }

        /// <summary>A list value as numbers; null when absent or when an entry is not a number.</summary>
        public static double[]? Numbers(string? value)
        {
            var l = List(value);
            if (l == null) return null;
            var r = new double[l.Length];
            for (int i = 0; i < l.Length; i++)
                if (!double.TryParse(l[i], NumberStyles.Float, CultureInfo.InvariantCulture, out r[i])) return null;
            return r;
        }

        // ------------------------------------------------------------------ text

        /// <summary>Header text: UTF-8 when the bytes are valid UTF-8, else Latin-1 (older headers are 8 bit text).</summary>
        internal static string Text(ReadOnlySpan<byte> data)
        {
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) data = data.Slice(3);
            try { return new UTF8Encoding(false, true).GetString(data); }
            catch (DecoderFallbackException) { return Encoding.Latin1.GetString(data); }
        }

        /// <summary>Parses a whole header. <paramref name="data"/> holds the header text only (any length).</summary>
        public static EnviHeader Parse(ReadOnlySpan<byte> data)
        {
            if (!Envi.IsMatch(data)) throw new ImageFormatException("ENVI: a header starts with the line 'ENVI'");
            string text = Text(data);
            var h = new EnviHeader();
            int pos = text.IndexOf('\n');
            pos = pos < 0 ? text.Length : pos + 1;   // past the "ENVI" line
            while (pos < text.Length)
            {
                int start = pos;
                int end = text.IndexOf('\n', pos);
                if (end < 0) end = text.Length;
                string line = text.Substring(pos, end - pos).Trim();
                pos = end + 1;
                if (line.Length == 0 || line[0] == ';') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;   // not a field
                string name = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (value.StartsWith('{'))
                {
                    // the value runs to the closing brace, possibly lines later
                    int open = text.IndexOf('{', text.IndexOf('=', start));
                    int close = text.IndexOf('}', open + 1);
                    if (close < 0) throw new ImageFormatException($"ENVI: the value of '{name}' has no closing brace");
                    value = text.Substring(open + 1, close - open - 1).Replace("\r", "").Trim();
                    int next = text.IndexOf('\n', close);
                    pos = next < 0 ? text.Length : next + 1;
                }
                h.Fields.Add(new KeyValuePair<string, string>(name, value));
            }
            h.Interpret();
            return h;
        }

        void Interpret()
        {
            Samples = Int("samples") ?? throw new ImageFormatException("ENVI: the header has no 'samples'");
            Lines = Int("lines") ?? throw new ImageFormatException("ENVI: the header has no 'lines'");
            Bands = Int("bands") ?? 1;
            DataType = Int("data type") ?? throw new ImageFormatException("ENVI: the header has no 'data type'");
            HeaderOffset = this["header offset"] is string ho
                ? long.TryParse(ho, NumberStyles.Integer, CultureInfo.InvariantCulture, out long o) && o >= 0 ? o
                    : throw new ImageFormatException($"ENVI: header offset '{ho}' is not a byte count")
                : 0;
            BigEndian = (Int("byte order") ?? 0) == 1;
            Interleave = (this["interleave"] ?? "bsq").Trim().ToLowerInvariant() switch
            {
                "bsq" => EnviInterleave.Bsq,
                "bil" => EnviInterleave.Bil,
                "bip" => EnviInterleave.Bip,
                var s => throw new ImageFormatException($"ENVI: unknown interleave '{s}'"),
            };
            if (Samples <= 0 || Lines <= 0 || Bands <= 0) throw new ImageFormatException("ENVI: samples, lines and bands have to be positive");
        }

        int? Int(string name)
        {
            var v = this[name];
            if (v == null) return null;
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i
                : throw new ImageFormatException($"ENVI: '{name} = {v}' is not a whole number");
        }

        /// <summary>
        /// The header as text: "ENVI", the layout fields from the properties, then every other field of <see cref="Fields"/>.
        /// Values with commas or line breaks are written in braces.
        /// </summary>
        public string ToText()
        {
            var sb = new StringBuilder("ENVI\n");
            void Line(string k, string v) => sb.Append(k).Append(" = ").Append(v).Append('\n');
            Line("samples", Num(Samples));
            Line("lines", Num(Lines));
            Line("bands", Num(Bands));
            Line("header offset", HeaderOffset.ToString(CultureInfo.InvariantCulture));
            Line("data type", Num(DataType));
            Line("interleave", Interleave.ToString().ToLowerInvariant());
            Line("byte order", BigEndian ? "1" : "0");
            foreach (var (k, v) in Fields)
            {
                if (Normalize(k) is "samples" or "lines" or "bands" or "header offset" or "data type" or "interleave" or "byte order") continue;
                if (v.Contains('}')) throw new ArgumentException($"ENVI: the value of '{k}' contains '}}', which ends a braced value");
                bool braces = v.Contains(',') || v.Contains('\n') || v.Contains('{') || IsBraced(k);
                Line(k, braces ? "{" + v + "}" : v);
            }
            return sb.ToString();
        }

        // fields ENVI writes in braces even with a single entry
        static bool IsBraced(string k) => Normalize(k) is "description" or "band names" or "wavelength" or "fwhm" or "bbl"
            or "map info" or "coordinate system string" or "class names";

        static string Num(int v) => v.ToString(CultureInfo.InvariantCulture);

        /// <summary>Sets a field (replacing one of the same name), or removes it when <paramref name="value"/> is null.</summary>
        public void Set(string name, string? value)
        {
            string n = Normalize(name);
            int at = Fields.FindIndex(f => Normalize(f.Key) == n);
            if (value == null) { if (at >= 0) Fields.RemoveAt(at); return; }
            var kv = new KeyValuePair<string, string>(name, value);
            if (at >= 0) Fields[at] = kv; else Fields.Add(kv);
        }
    }
}
