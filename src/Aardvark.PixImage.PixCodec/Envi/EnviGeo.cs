using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// ENVI's "map info" field and "coordinate system string". Map info lists: projection name, reference pixel x and y
    /// (1-based image coordinates, 1, 1 being the upper-left corner of the upper-left pixel), map x and y of that point,
    /// x and y pixel size, then for UTM the zone and North / South, then the datum and "units=" / "rotation=" entries.
    /// </summary>
    internal static class EnviGeo
    {
        public static GeoReference? Read(EnviHeader h)
        {
            string? mapInfo = h.MapInfo, wkt = h.CoordinateSystemString;
            if (mapInfo == null && wkt == null) return null;
            var geo = new GeoReference();
            var crs = new GeoCrs();
            var w = wkt == null ? null : Wkt.TryParse(wkt);
            if (wkt != null) crs.Wkt = wkt;

            string? proj = null, datum = null;
            int zone = 0, unit = 0;
            bool? south = null;
            if (mapInfo != null)
            {
                var t = EnviHeader.List(mapInfo)!;
                if (t.Length < 7) throw new ImageFormatException($"ENVI: map info needs a projection, a reference pixel, its map position and the pixel sizes: '{mapInfo}'");
                proj = t[0];
                double rx = Num(t[1]), ry = Num(t[2]), mx = Num(t[3]), my = Num(t[4]), xs = Num(t[5]), ys = Num(t[6]);
                double rotation = 0;
                bool utm = proj.Equals("UTM", StringComparison.OrdinalIgnoreCase);
                for (int i = 7; i < t.Length; i++)
                {
                    string e = t[i];
                    int eq = e.IndexOf('=');
                    if (eq > 0)
                    {
                        string k = e.Substring(0, eq).Trim().ToLowerInvariant(), v = e.Substring(eq + 1).Trim();
                        if (k == "units") unit = UnitCode(v);
                        else if (k == "rotation") rotation = Num(v);
                        continue;
                    }
                    if (utm && i == 7 && int.TryParse(e, NumberStyles.Integer, CultureInfo.InvariantCulture, out int z)) { zone = z; continue; }
                    if (utm && i == 8 && e.Equals("North", StringComparison.OrdinalIgnoreCase)) { south = false; continue; }
                    if (utm && i == 8 && e.Equals("South", StringComparison.OrdinalIgnoreCase)) { south = true; continue; }
                    if (!utm && i == 7 && double.TryParse(e, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) continue;   // a zone code (State Plane)
                    datum ??= e;
                }
                // the affine of a rotated grid as GDAL reads it: x along the row by (xs cos r, ys sin r), down the column
                // by (xs sin r, -ys cos r); without rotation simply (xs, 0) and (0, -ys). Confirmed on an ENVI-written
                // AVIRIS-NG ortho product (rotation=-80, equal pixel sizes) against its per-pixel ground positions
                // (EnviTests.RotatedMapInfoPlacesAvirisNgCellsOnTheirGroundPositions); unequal sizes are untested
                double rad = rotation * Math.PI / 180, c = Math.Cos(rad), s = Math.Sin(rad);
                double a = xs * c, b = xs * s, d = ys * s, e2 = -ys * c;
                if (rotation == 0) { a = xs; b = 0; d = 0; e2 = -ys; }
                // reference pixel (rx, ry) is 1-based. GDAL steps from it to the corner with the unrotated pixel sizes, also
                // in a rotated grid; that is followed here, so that both read the same placement (only a rotated grid
                // with a tie point away from 1, 1 is affected)
                double x0 = mx - (rx - 1) * xs, y0 = my + (ry - 1) * ys;
                geo.Affine = new[] { a, b, x0, d, e2, y0 };
            }

            // what kind of coordinates, the units and the CRS name: the WKT where there is one, else map info
            bool geographic = w?.Keyword is "GEOGCS" or "GEOGCRS" || w == null && proj != null && proj.StartsWith("Geographic", StringComparison.OrdinalIgnoreCase);
            bool projected = w?.Keyword is "PROJCS" or "PROJCRS" || w == null && proj != null && !geographic && !IsArbitrary(proj);
            geo.ModelType = geographic ? GeoModelType.Geographic : projected ? GeoModelType.Projected
                : w?.Keyword is "GEOCCS" ? GeoModelType.Geocentric : GeoModelType.Unknown;
            crs.Name = w?.Name ?? proj;
            crs.Datum = datum ?? w?.Datum;
            if (geographic)
            {
                int u = w?.Unit is double f ? CodeOf(f, true) : unit != 0 && GeoUnits.IsAngular(unit) == true ? unit : GeoUnits.Degree;
                crs.AngularUnit = u;
            }
            else if (projected)
            {
                int u = w?.Unit is double f ? CodeOf(f, false)
                    : unit != 0 && GeoUnits.IsAngular(unit) == false ? unit
                    : proj != null && proj.StartsWith("State Plane", StringComparison.OrdinalIgnoreCase) ? 0   // ENVI's State Plane default is feet, of which kind it does not say
                    : GeoUnits.Metre;                                                                          // "meters for all other projections"
                crs.LinearUnit = u;
            }

            // EPSG only where certain: an EPSG authority in the WKT, or WGS 84 named outright with UTM zone and hemisphere
            // or geographic degrees (and a WKT, if any, saying the same)
            if (w?.Epsg is int ew) crs.Epsg = ew;
            else if (IsWgs84(datum))
            {
                if (proj != null && proj.Equals("UTM", StringComparison.OrdinalIgnoreCase) && zone is >= 1 and <= 60 && south != null
                    && crs.LinearUnit == GeoUnits.Metre && (w == null || w.IsWgs84Utm(zone, south.Value)))
                    crs.Epsg = (south.Value ? 32700 : 32600) + zone;
                else if (geographic && crs.AngularUnit is GeoUnits.Degree or 9122 && (w == null || w.IsWgs84Geographic()))
                    crs.Epsg = 4326;
            }
            geo.Crs = crs;
            geo.NoData = h.DataIgnoreValue;
            return geo;
        }

        static bool IsArbitrary(string proj) => proj.Equals("Arbitrary", StringComparison.OrdinalIgnoreCase) || proj.Equals("Pixel", StringComparison.OrdinalIgnoreCase);

        static double Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v : throw new ImageFormatException($"ENVI: '{s}' in map info is not a number");

        internal static string Plain(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        static bool IsWgs84(string? datum) => datum != null && Plain(datum) is "wgs84" or "wgs1984";

        // map info "units=" names (ENVI's unit names)
        static int UnitCode(string name) => Plain(name) switch
        {
            "meters" or "meter" or "metres" or "metre" => 9001,
            "feet" or "foot" => 9002,
            "ussurveyfeet" or "usfeet" or "ussurveyfoot" => 9003,
            "kilometers" or "kilometres" or "km" => 9036,
            "yards" or "yard" => 9096,
            "miles" or "mile" => 9093,
            "nauticalmiles" => 9030,
            "degrees" or "degree" => 9102,
            "radians" or "radian" => 9101,
            "seconds" => 9104,
            _ => 0,
        };

        static string? UnitName(int code) => code switch
        {
            9001 => "Meters", 9002 => "Feet", 9003 => "US Survey Feet", 9036 => "Kilometers", 9096 => "Yards",
            9093 => "Miles", 9030 => "Nautical Miles", 9102 or 9122 => "Degrees", 9101 => "Radians", 9104 => "Seconds",
            _ => null,
        };

        // the EPSG unit with this factor (metres or radians per unit), 0 when none listed has it
        static int CodeOf(double factor, bool angle)
        {
            foreach (int code in new[] { 9001, 9002, 9003, 9036, 9096, 9093, 9030, 1025, 1033, 9102, 9101, 9104, 9105 })
                if (GeoUnits.IsAngular(code) == angle && GeoUnits.Factor(code) is double f && Math.Abs(f - factor) <= 1e-9 * f) return code;
            return 0;
        }

        // ------------------------------------------------------------------ writing

        /// <summary>The map info line and coordinate system string of a placement.</summary>
        public static (string MapInfo, string? Wkt) Write(GeoReference g)
        {
            var a = g.Affine;
            double xs, ys, rotation;
            if (a[1] == 0 && a[3] == 0) { xs = a[0]; ys = -a[4]; rotation = 0; }
            else
            {
                // GDAL's form (see Read): a = xs cos r, b = xs sin r, d = ys sin r, e = -ys cos r
                rotation = Math.Atan2(a[1], a[0]);
                xs = Math.Sqrt(a[0] * a[0] + a[1] * a[1]);
                ys = Math.Sqrt(a[3] * a[3] + a[4] * a[4]);
                double c = Math.Cos(rotation), s = Math.Sin(rotation);
                if (Math.Abs(ys * s - a[3]) > 1e-9 * ys || Math.Abs(-ys * c - a[4]) > 1e-9 * ys)
                    throw new ImageNotSupportedException("ENVI: map info holds two pixel sizes and one rotation; this affine is skewed and cannot be written");
                rotation *= 180 / Math.PI;
            }
            var crs = g.Crs;
            string tail = rotation == 0 ? "" : ", rotation=" + Envi.Num(rotation);
            string head(string proj) => $"{proj}, 1, 1, {Envi.Num(a[2])}, {Envi.Num(a[5])}, {Envi.Num(xs)}, {Envi.Num(ys)}";
            if (crs == null) return (head("Arbitrary") + tail, null);

            int epsg = crs.Epsg ?? 0;
            if (epsg == 4326) return (head("Geographic Lat/Lon") + ", WGS-84, units=Degrees" + tail, crs.Wkt);
            if (epsg is >= 32601 and <= 32660 or >= 32701 and <= 32760)
                return (head("UTM") + $", {epsg % 100}, {(epsg > 32700 ? "South" : "North")}, WGS-84, units=Meters" + tail, crs.Wkt);
            if (crs.Wkt == null)
                throw new ImageNotSupportedException($"ENVI: map info cannot name the coordinate system {crs}; give its WKT in GeoCrs.Wkt (written as the coordinate system string)");
            var w = Wkt.Parse(crs.Wkt);
            bool geographic = w.Keyword is "GEOGCS" or "GEOGCRS";
            string proj = geographic ? "Geographic Lat/Lon" : Clean(crs.Name ?? w.Name) ?? "Arbitrary";
            var sb = new StringBuilder(head(proj));
            if (Clean(crs.Datum ?? w.Datum) is string dn) sb.Append(", ").Append(dn);
            int unit = geographic ? crs.AngularUnit : crs.LinearUnit;
            if (w.Unit is double f) unit = CodeOf(f, geographic);
            if (UnitName(unit) is string un) sb.Append(", units=").Append(un);
            sb.Append(tail);
            return (sb.ToString(), crs.Wkt);
        }

        // a name usable as one entry of map info (no list separators), null when there is none
        static string? Clean(string? s)
        {
            if (s == null) return null;
            s = s.Replace(',', ' ').Replace('{', ' ').Replace('}', ' ').Replace('=', ' ').Trim();
            return s.Length == 0 ? null : s;
        }
    }

    /// <summary>The few things read from a WKT1 / WKT2 string: root keyword and name, EPSG authority, datum and unit.</summary>
    internal sealed class Wkt
    {
        public string Keyword = "";
        public string? Name, Datum;
        public int? Epsg;
        /// <summary>The root's own UNIT factor (metres or radians per unit).</summary>
        public double? Unit;

        sealed class Node
        {
            public string Key = "";
            public List<object> Items = new();   // string (quoted), string (bare token as "#..."), Node
        }

        /// <summary>The parts of a WKT this reader understands, null when the text is not WKT.</summary>
        public static Wkt? TryParse(string text)
        {
            try { return Parse(text); }
            catch (ImageFormatException) { return null; }
        }

        public static Wkt Parse(string text)
        {
            int i = 0;
            var root = ParseNode(text, ref i) ?? throw new ImageFormatException("ENVI: the coordinate system string is not WKT");
            var w = new Wkt { Keyword = root.Key.ToUpperInvariant(), Name = FirstString(root) };
            foreach (var it in root.Items)
            {
                if (it is not Node n) continue;
                string k = n.Key.ToUpperInvariant();
                if ((k is "AUTHORITY" or "ID") && n.Items.Count >= 2 && n.Items[0] is string auth && auth.Equals("EPSG", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(Bare(n.Items[1]), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
                    w.Epsg = code;
                if ((k is "UNIT" or "LENGTHUNIT" or "ANGLEUNIT") && n.Items.Count >= 2
                    && double.TryParse(Bare(n.Items[1]), NumberStyles.Float, CultureInfo.InvariantCulture, out double f))
                    w.Unit = f;
            }
            w.Datum = FindDatum(root);
            return w;
        }

        static string? FirstString(Node n) => n.Items.Count > 0 && n.Items[0] is string s && !s.StartsWith('#') ? s : null;
        static string Bare(object o) => o is string s ? s.TrimStart('#') : "";

        static string? FindDatum(Node n)
        {
            foreach (var it in n.Items)
                if (it is Node c)
                {
                    if (c.Key.Equals("DATUM", StringComparison.OrdinalIgnoreCase)) return FirstString(c);
                    if (FindDatum(c) is string d) return d;
                }
            return null;
        }

        static Node? ParseNode(string t, ref int i)
        {
            Ws(t, ref i);
            int k0 = i;
            while (i < t.Length && (char.IsLetterOrDigit(t[i]) || t[i] == '_')) i++;
            if (i == k0) return null;
            var n = new Node { Key = t.Substring(k0, i - k0) };
            Ws(t, ref i);
            if (i >= t.Length || (t[i] != '[' && t[i] != '(')) return n;
            char close = t[i] == '[' ? ']' : ')';
            i++;
            while (true)
            {
                Ws(t, ref i);
                if (i >= t.Length) throw new ImageFormatException("ENVI: the coordinate system string ends inside a bracket");
                char ch = t[i];
                if (ch == close) { i++; return n; }
                if (ch == ',') { i++; continue; }
                if (ch == '"')
                {
                    int e = i + 1;
                    var sb = new StringBuilder();
                    while (e < t.Length)
                    {
                        if (t[e] == '"') { if (e + 1 < t.Length && t[e + 1] == '"') { sb.Append('"'); e += 2; continue; } break; }
                        sb.Append(t[e++]);
                    }
                    n.Items.Add(sb.ToString());
                    i = e + 1;
                    continue;
                }
                if (char.IsLetter(ch))
                {
                    var c = ParseNode(t, ref i);
                    if (c != null) n.Items.Add(c);
                    continue;
                }
                int s0 = i;
                while (i < t.Length && t[i] != ',' && t[i] != close && !char.IsWhiteSpace(t[i])) i++;
                n.Items.Add("#" + t.Substring(s0, i - s0));
            }
        }

        static void Ws(string t, ref int i) { while (i < t.Length && char.IsWhiteSpace(t[i])) i++; }

        public bool IsWgs84Geographic() => Keyword is "GEOGCS" or "GEOGCRS" && IsWgs84Datum();

        public bool IsWgs84Utm(int zone, bool south)
            => Keyword is "PROJCS" or "PROJCRS" && IsWgs84Datum() && Name != null
               && EnviGeo.Plain(Name).Contains("utmzone" + zone.ToString(CultureInfo.InvariantCulture) + (south ? "s" : "n"), StringComparison.Ordinal);

        bool IsWgs84Datum() => Datum != null && EnviGeo.Plain(Datum) is var d && (d.Contains("wgs84") || d.Contains("wgs1984"));
    }
}
