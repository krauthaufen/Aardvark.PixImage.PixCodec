using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// One entry of a GeoKeyDirectory as stored: the key id, its name in GeoTIFF 1.1 (null for keys the standard does not
    /// name), where the value lives (<see cref="Location"/>: 0 inline, 34735 GeoKeyDirectory, 34736 GeoDoubleParams,
    /// 34737 GeoAsciiParams) and the value itself in <see cref="Shorts"/>, <see cref="Doubles"/> or <see cref="Text"/>.
    /// </summary>
    public sealed record GeoKey(int Id, string? Name, int Location, int Count, ushort[]? Shorts, double[]? Doubles, string? Text)
    {
        /// <summary>The first short value (the code of a SHORT key), null for a double or text key.</summary>
        public int? Code => Shorts is { Length: > 0 } s ? s[0] : null;

        public override string ToString()
        {
            string n = Name ?? "#" + Id.ToString(CultureInfo.InvariantCulture);
            if (Text != null) return n + " = \"" + Text + "\"";
            if (Doubles != null) return n + " = " + string.Join(", ", Array.ConvertAll(Doubles, d => d.ToString("R", CultureInfo.InvariantCulture)));
            return n + " = " + (Shorts == null ? "?" : string.Join(", ", Shorts));
        }
    }

    /// <summary>
    /// The GeoTIFF content of one image directory: the raw tags and GeoKeys as stored, and the placement they describe in
    /// <see cref="Reference"/>. Built by <see cref="Tiff.ReadGeo(ReadOnlySpan{byte}, int)"/> or <see cref="GeoTiff.FromTags"/>.
    /// </summary>
    public sealed class GeoTiffInfo
    {
        /// <summary>
        /// Placement, CRS, raster type and no-data value. Its affine is meaningful only when <see cref="HasTransform"/> is
        /// true; otherwise it is the identity (a file with GeoKeys but no model tags, or with tiepoints alone, which are
        /// ground control points rather than an affine).
        /// </summary>
        public GeoReference Reference { get; } = new GeoReference();

        /// <summary>Whether the file defines the affine: ModelTransformation, or ModelTiepoint with ModelPixelScale.</summary>
        public bool HasTransform { get; internal set; }

        /// <summary>ModelPixelScale (33550) as stored, null when absent.</summary>
        public double[]? PixelScale { get; internal set; }

        /// <summary>ModelTiepoint (33922) entries (I, J, K, X, Y, Z), empty when absent.</summary>
        public IReadOnlyList<double[]> TiePoints { get; internal set; } = Array.Empty<double[]>();

        /// <summary>ModelTransformation (34264) as stored, null when absent.</summary>
        public double[]? ModelTransformation { get; internal set; }

        /// <summary>The GeoKeys in file order.</summary>
        public IReadOnlyList<GeoKey> Keys { get; internal set; } = Array.Empty<GeoKey>();

        /// <summary>KeyDirectoryVersion, KeyRevision and MinorRevision of the GeoKeyDirectory, zeros when absent.</summary>
        public (int Version, int Revision, int Minor) KeyDirectoryVersion { get; internal set; }

        /// <summary>GDAL_NODATA (42113) as stored, null when absent.</summary>
        public string? NoDataText { get; internal set; }

        /// <summary>The key with this id, null when the file has none.</summary>
        public GeoKey? Key(int id)
        {
            foreach (var k in Keys) if (k.Id == id) return k;
            return null;
        }

        public override string ToString() => (HasTransform ? Reference.ToString() : Reference.Crs?.ToString() ?? "") + $" ({Keys.Count} GeoKeys)";
    }

    /// <summary>
    /// GeoTIFF 1.1 (OGC 19-008r4) georeferencing: ModelPixelScale, ModelTiepoint and ModelTransformation place the grid,
    /// the GeoKeyDirectory with GeoDoubleParams and GeoAsciiParams names the CRS, and GDAL_NODATA (GDAL's convention,
    /// not part of GeoTIFF) gives the no-data value. Read with <see cref="Tiff.ReadGeo(ReadOnlySpan{byte}, int)"/>, written
    /// through <see cref="TiffEncodeOptions.Geo"/>; <see cref="Tiff.Decode(ReadOnlySpan{byte}, TiffDecodeOptions?)"/> puts
    /// the placement into <see cref="ImageMetadata.Geo"/>.
    /// </summary>
    /// <remarks>
    /// A PixelIsPoint grid ties its tiepoint to the sample centre; it is read half a cell up and left, so that the affine
    /// gives cell corners as for every grid (as GDAL reads it by default), and written back the same way. The sign of
    /// ScaleY is ignored, as GDAL does by default (requirement 10.5 would flip on a negative one, but writers put
    /// negative values on north-up data). The GeoTIFF placement refers to the
    /// raster as stored (B.6: it is independent of the TIFF Orientation tag), so when a decoder turns the pixels upright
    /// the affine is turned with them.
    /// </remarks>
    public static class GeoTiff
    {
        public const int TagModelPixelScale = 33550, TagModelTiepoint = 33922, TagModelTransformation = 34264;
        public const int TagGeoKeyDirectory = 34735, TagGeoDoubleParams = 34736, TagGeoAsciiParams = 34737;
        /// <summary>GDAL's no-data tag: ASCII text of the value.</summary>
        public const int TagGdalNoData = 42113;

        public const int KeyModelType = 1024, KeyRasterType = 1025, KeyCitation = 1026;
        public const int KeyGeodeticCrs = 2048, KeyGeodeticCitation = 2049, KeyGeodeticDatum = 2050, KeyGeogAngularUnits = 2054;
        public const int KeyProjectedCrs = 3072, KeyProjectedCitation = 3073, KeyProjLinearUnits = 3076;
        public const int KeyVerticalCrs = 4096, KeyVerticalUnits = 4099;

        /// <summary>Code meaning "user-defined" in every GeoKey (requirement 3.2).</summary>
        public const int UserDefined = 32767;

        // the keys OGC 19-008r4 defines, with their 1.1 names (requirement classes 7, 8 and 12 to 27)
        static readonly Dictionary<int, string> Names = new()
        {
            [1024] = "GTModelTypeGeoKey", [1025] = "GTRasterTypeGeoKey", [1026] = "GTCitationGeoKey",
            [2048] = "GeodeticCRSGeoKey", [2049] = "GeodeticCitationGeoKey", [2050] = "GeodeticDatumGeoKey",
            [2051] = "PrimeMeridianGeoKey", [2052] = "GeogLinearUnitsGeoKey", [2053] = "GeogLinearUnitSizeGeoKey",
            [2054] = "GeogAngularUnitsGeoKey", [2055] = "GeogAngularUnitSizeGeoKey", [2056] = "EllipsoidGeoKey",
            [2057] = "EllipsoidSemiMajorAxisGeoKey", [2058] = "EllipsoidSemiMinorAxisGeoKey",
            [2059] = "EllipsoidInvFlatteningGeoKey", [2060] = "GeogAzimuthUnitsGeoKey", [2061] = "PrimeMeridianLongitudeGeoKey",
            [3072] = "ProjectedCRSGeoKey", [3073] = "ProjectedCitationGeoKey", [3074] = "ProjectionGeoKey",
            [3075] = "ProjMethodGeoKey", [3076] = "ProjLinearUnitsGeoKey", [3077] = "ProjLinearUnitSizeGeoKey",
            [3078] = "ProjStdParallel1GeoKey", [3079] = "ProjStdParallel2GeoKey",
            [3080] = "ProjNatOriginLongGeoKey", [3081] = "ProjNatOriginLatGeoKey", [3082] = "ProjFalseEastingGeoKey",
            [3083] = "ProjFalseNorthingGeoKey", [3084] = "ProjFalseOriginLongGeoKey", [3085] = "ProjFalseOriginLatGeoKey",
            [3086] = "ProjFalseOriginEastingGeoKey", [3087] = "ProjFalseOriginNorthingGeoKey", [3088] = "ProjCenterLongGeoKey",
            [3089] = "ProjCenterLatGeoKey", [3090] = "ProjCenterEastingGeoKey", [3091] = "ProjCenterNorthingGeoKey",
            [3092] = "ProjScaleAtNatOriginGeoKey", [3093] = "ProjScaleAtCenterGeoKey", [3094] = "ProjAzimuthAngleGeoKey",
            [3095] = "ProjStraightVertPoleLongGeoKey",
            [4096] = "VerticalGeoKey", [4097] = "VerticalCitationGeoKey", [4098] = "VerticalDatumGeoKey", [4099] = "VerticalUnitsGeoKey",
        };

        /// <summary>The GeoTIFF 1.1 name of a key id, null when the standard defines no such key.</summary>
        public static string? KeyName(int id) => Names.TryGetValue(id, out var n) ? n : null;

        /// <summary>The ids of all keys the standard defines.</summary>
        public static IEnumerable<int> KnownKeys => Names.Keys;

        // codes 1024..32766 are register (EPSG) codes; 0 is "omitted", 1..1023 reserved, 32767 user-defined, above private
        static int? Registered(int? code) => code is >= 1024 and <= 32766 ? code : null;

        static bool IsGeoTag(int id) => id is TagModelPixelScale or TagModelTiepoint or TagModelTransformation
            or TagGeoKeyDirectory or TagGeoDoubleParams or TagGeoAsciiParams or TagGdalNoData;

        /// <summary>
        /// The GeoTIFF content of one directory's tags (from <see cref="Tiff.ReadTags(ReadOnlySpan{byte}, int)"/> or one of
        /// its overloads), null when none of the GeoTIFF tags is there.
        /// </summary>
        public static GeoTiffInfo? FromTags(IReadOnlyList<TiffTag> tags)
        {
            ArgumentNullException.ThrowIfNull(tags);
            TiffTag? Find(int id)
            {
                foreach (var t in tags) if (t.Id == id) return t;
                return null;
            }
            bool any = false;
            foreach (var t in tags) any |= IsGeoTag(t.Id);
            if (!any) return null;

            var info = new GeoTiffInfo();
            var g = info.Reference;
            info.PixelScale = Find(TagModelPixelScale)?.AsDoubles();
            if (Find(TagModelTiepoint)?.AsDoubles() is { } tp)
            {
                var list = new List<double[]>();
                for (int i = 0; i + 6 <= tp.Length; i += 6) list.Add(tp[i..(i + 6)]);
                info.TiePoints = list;
            }
            info.ModelTransformation = Find(TagModelTransformation)?.AsDoubles();

            if (info.ModelTransformation is { Length: 16 } m)
            {
                // B.6: (X, Y, Z, 1) = M (I, J, K, 1), row major; K = 0 for a 2D raster
                g.Affine = new[] { m[0], m[1], m[3], m[4], m[5], m[7] };
                info.HasTransform = AllFinite(g.Affine);
            }
            else if (info.ModelTransformation == null && info.PixelScale is { Length: >= 2 } s && info.TiePoints.Count > 0)
            {
                // B.6: the first tiepoint ties raster (I, J) to model (X, Y); Y decreases as J grows. Requirement 10.5
                // lets a negative ScaleY reverse that, but writers put negative values on north-up data, so the sign is
                // ignored as GDAL does by default; a flipped grid needs ModelTransformation
                var t = info.TiePoints[0];
                double sx = s[0], sy = Math.Abs(s[1]);
                g.Affine = new[] { sx, 0, t[3] - t[0] * sx, 0, -sy, t[4] + t[1] * sy };
                info.HasTransform = AllFinite(g.Affine) && sx != 0 && sy != 0;
            }
            if (!info.HasTransform) g.Affine = new double[] { 1, 0, 0, 0, 1, 0 };

            if (Find(TagGeoKeyDirectory) is { } dir)
                ReadKeys(info, dir.AsIntegers(), Find(TagGeoDoubleParams)?.AsDoubles(), Find(TagGeoAsciiParams)?.AsString());

            if (Find(TagGdalNoData) is { } nd)
            {
                info.NoDataText = nd.AsString() ?? string.Join(" ", nd.AsDoubles());
                g.NoData = ParseNoData(info.NoDataText);
            }

            g.ModelType = info.Key(KeyModelType)?.Code is int mt and >= 1 and <= 3 ? (GeoModelType)mt : GeoModelType.Unknown;
            g.RasterType = info.Key(KeyRasterType)?.Code == 2 ? GeoRasterType.PixelIsPoint : GeoRasterType.PixelIsArea;
            g.Crs = Crs(info, g.ModelType);
            // the affine maps to cell corners; a point grid's positions are the cell centres
            if (info.HasTransform && g.RasterType == GeoRasterType.PixelIsPoint) g.Affine = AtCell(g.Affine, -0.5);
            return info;
        }

        // the same affine with grid position (0, 0) moved to (d, d)
        static double[] AtCell(double[] a, double d)
            => new[] { a[0], a[1], a[2] + d * (a[0] + a[1]), a[3], a[4], a[5] + d * (a[3] + a[4]) };

        static bool AllFinite(double[] a) => Array.TrueForAll(a, double.IsFinite);

        static double? ParseNoData(string text)
        {
            var s = text.Trim().TrimEnd('\0').Trim();
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
            return s.ToLowerInvariant() switch
            {
                "nan" or "-nan" => double.NaN,
                "inf" or "+inf" or "infinity" => double.PositiveInfinity,
                "-inf" or "-infinity" => double.NegativeInfinity,
                _ => null,
            };
        }

        static void ReadKeys(GeoTiffInfo info, long[] d, double[]? doubles, string? ascii)
        {
            if (d.Length < 4) return;
            info.KeyDirectoryVersion = ((int)d[0], (int)d[1], (int)d[2]);
            // requirement 2.11: NumberOfKeys entries follow the header. A directory announcing more keys than it holds is
            // broken, and none of its keys can be trusted (GDAL drops such a directory too)
            if (d[3] > (d.Length - 4) / 4) return;
            int n = (int)d[3];
            var keys = new List<GeoKey>(n);
            for (int e = 0; e < n; e++)
            {
                int id = (int)d[4 + 4 * e], loc = (int)d[5 + 4 * e], count = (int)d[6 + 4 * e], at = (int)d[7 + 4 * e];
                ushort[]? shorts = null; double[]? dbl = null; string? text = null;
                switch (loc)
                {
                    case 0:
                        shorts = new[] { (ushort)at };       // requirement 2.16: the value itself, Count = 1 implied
                        count = 1;
                        break;
                    case TagGeoKeyDirectory:                // requirement 4.2: further shorts after the last entry
                        if (at >= 0 && count >= 0 && (long)at + count <= d.Length)
                            shorts = Array.ConvertAll(d[at..(at + count)], v => (ushort)v);
                        break;
                    case TagGeoDoubleParams:
                        if (doubles != null && at >= 0 && count >= 0 && (long)at + count <= doubles.Length)
                            dbl = doubles[at..(at + count)];
                        break;
                    case TagGeoAsciiParams:
                        if (ascii != null && at >= 0 && at < ascii.Length)
                        {
                            // requirement 6.3: '|' terminates each string; Count includes it
                            int len = Math.Clamp(count, 0, ascii.Length - at);
                            text = ascii.Substring(at, len);
                            int bar = text.IndexOf('|');
                            if (bar >= 0) text = text.Substring(0, bar);
                        }
                        break;
                }
                keys.Add(new GeoKey(id, KeyName(id), loc, count, shorts, dbl, text));
            }
            info.Keys = keys;
        }

        static GeoCrs? Crs(GeoTiffInfo info, GeoModelType model)
        {
            int? projected = Registered(info.Key(KeyProjectedCrs)?.Code), geodetic = Registered(info.Key(KeyGeodeticCrs)?.Code);
            var c = new GeoCrs
            {
                // requirements 8.7 to 8.9: a projected model names its CRS in ProjectedCRSGeoKey, the others in GeodeticCRSGeoKey
                Epsg = model switch
                {
                    GeoModelType.Projected => projected,
                    GeoModelType.Geographic or GeoModelType.Geocentric => geodetic,
                    _ => projected ?? geodetic,
                },
                DatumEpsg = Registered(info.Key(KeyGeodeticDatum)?.Code),
                LinearUnit = Registered(info.Key(KeyProjLinearUnits)?.Code) ?? 0,
                AngularUnit = Registered(info.Key(KeyGeogAngularUnits)?.Code) ?? 0,
            };
            string? Text(int id) => info.Key(id)?.Text is { Length: > 0 } t ? t : null;
            c.Name = model switch
            {
                GeoModelType.Projected => Text(KeyProjectedCitation) ?? Text(KeyCitation),
                GeoModelType.Geographic or GeoModelType.Geocentric => Text(KeyGeodeticCitation) ?? Text(KeyCitation),
                _ => Text(KeyCitation) ?? Text(KeyProjectedCitation) ?? Text(KeyGeodeticCitation),
            };
            return c.Epsg == null && c.DatumEpsg == null && c.Name == null && c.LinearUnit == 0 && c.AngularUnit == 0 ? null : c;
        }

        /// <summary>
        /// The GeoTIFF tags that describe <paramref name="geo"/>: ModelPixelScale and ModelTiepoint for a north-up grid
        /// (positive x step, y decreasing down the rows), ModelTransformation for any other grid, so that a flipped or
        /// rotated grid reads back the same in every reader; a GeoKeyDirectory with the model type, raster type, the EPSG
        /// code, the citation and the units, GeoAsciiParams for the citation, and GDAL_NODATA when a no-data value is set.
        /// </summary>
        public static List<TiffTag> BuildTags(GeoReference geo)
        {
            ArgumentNullException.ThrowIfNull(geo);
            // a point grid states the positions of its sample centres
            var a = geo.RasterType == GeoRasterType.PixelIsPoint ? AtCell(geo.Affine, 0.5) : geo.Affine;
            if (!AllFinite(a)) throw new ArgumentException("GeoTIFF: the affine has non-finite coefficients", nameof(geo));
            var tags = new List<TiffTag>();
            if (a[1] == 0 && a[3] == 0 && a[0] > 0 && a[4] < 0)
            {
                tags.Add(TiffTag.Doubles(TagModelPixelScale, a[0], -a[4], 0));
                tags.Add(TiffTag.Doubles(TagModelTiepoint, 0, 0, 0, a[2], a[5], 0));
            }
            else
                tags.Add(TiffTag.Doubles(TagModelTransformation, a[0], a[1], 0, a[2], a[3], a[4], 0, a[5], 0, 0, 0, 0, 0, 0, 0, 1));

            var crs = geo.Crs;
            var model = geo.ModelType;
            if (model == GeoModelType.Unknown && crs?.Epsg != null)
            {
                if (crs.AngularUnit != 0 && crs.LinearUnit == 0) model = GeoModelType.Geographic;
                else if (crs.LinearUnit != 0) model = GeoModelType.Projected;
                else throw new ArgumentException("GeoTIFF: an EPSG code needs GeoReference.ModelType (projected or geographic) to choose its GeoKey", nameof(geo));
            }

            var keys = new SortedDictionary<int, (int Loc, int Count, int Value)>();   // requirement 1.6: ascending ids
            var ascii = new StringBuilder();
            void Text(int id, string? s)
            {
                if (string.IsNullOrEmpty(s)) return;
                if (s.Contains('|') || s.Contains('\0')) throw new ArgumentException("GeoTIFF: a citation cannot hold '|' or NUL (requirements 6.3, 6.4)", nameof(geo));
                if (Encoding.UTF8.GetByteCount(s) != s.Length) throw new ArgumentException("GeoTIFF: a citation has to be ASCII", nameof(geo));
                keys[id] = (TagGeoAsciiParams, s.Length + 1, ascii.Length);
                ascii.Append(s).Append('|');
            }
            void Code(int id, int v)
            {
                if (v < 0 || v > 65535) throw new ArgumentException($"GeoTIFF: {v} does not fit GeoKey {id}", nameof(geo));
                keys[id] = (0, 1, v);
            }

            if (model != GeoModelType.Unknown) Code(KeyModelType, (int)model);
            Code(KeyRasterType, (int)geo.RasterType);
            if (crs != null)
            {
                if (crs.Epsg is int e) Code(model == GeoModelType.Projected ? KeyProjectedCrs : KeyGeodeticCrs, e);
                Text(KeyCitation, crs.Name);
                if (crs.DatumEpsg is int de) Code(KeyGeodeticDatum, de);
                if (crs.LinearUnit != 0) Code(KeyProjLinearUnits, crs.LinearUnit);
                if (crs.AngularUnit != 0) Code(KeyGeogAngularUnits, crs.AngularUnit);
            }

            var dir = new List<ushort> { 1, 1, 1, (ushort)keys.Count };   // version 1, revision 1, minor 1 (requirements 2.5 to 2.9)
            foreach (var (id, k) in keys) { dir.Add((ushort)id); dir.Add((ushort)k.Loc); dir.Add((ushort)k.Count); dir.Add((ushort)k.Value); }
            tags.Add(TiffTag.Shorts(TagGeoKeyDirectory, dir.ToArray()));
            if (ascii.Length > 0) tags.Add(TiffTag.Ascii(TagGeoAsciiParams, ascii.ToString()));
            if (geo.NoData is double nd) tags.Add(TiffTag.Ascii(TagGdalNoData, NoDataText(nd)));
            return tags;
        }

        static string NoDataText(double v)
            => double.IsNaN(v) ? "nan" : double.IsPositiveInfinity(v) ? "inf" : double.IsNegativeInfinity(v) ? "-inf"
             : v.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>
        /// The placement of a decoded region: grid position (0, 0) of the result is stored position (x, y).
        /// </summary>
        internal static GeoReference Shifted(GeoReference g, int x, int y)
        {
            if (x == 0 && y == 0) return g;
            var r = g.Clone();
            var a = g.Affine;
            r.Affine = new[] { a[0], a[1], a[0] * x + a[1] * y + a[2], a[3], a[4], a[3] * x + a[4] * y + a[5] };
            return r;
        }

        /// <summary>
        /// The placement of an image turned upright from TIFF orientation <paramref name="orientation"/> (stored size
        /// <paramref name="width"/> x <paramref name="height"/>), as <see cref="ImageOps.ApplyOrientation"/> turns it: grid
        /// corners of the upright image are mapped back to stored ones, then through the stored affine.
        /// </summary>
        internal static GeoReference Oriented(GeoReference g, int orientation, int width, int height)
        {
            if (orientation <= 1 || orientation > 8) return g;
            double w = width, h = height;
            // stored (s, t) = (p * u + q * v + r0, p2 * u + q2 * v + r1) for upright (u, v)
            (double p, double q, double r0, double p2, double q2, double r1) = orientation switch
            {
                2 => (-1.0, 0.0, w, 0.0, 1.0, 0.0),
                3 => (-1, 0, w, 0, -1, h),
                4 => (1, 0, 0, 0, -1, h),
                5 => (0, 1, 0, 1, 0, 0),
                6 => (0, 1, 0, -1, 0, h),
                7 => (0, -1, w, -1, 0, h),
                _ => (0, -1, w, 1, 0, 0),
            };
            var a = g.Affine;
            var r = g.Clone();
            r.Affine = new[]
            {
                a[0] * p + a[1] * p2, a[0] * q + a[1] * q2, a[0] * r0 + a[1] * r1 + a[2],
                a[3] * p + a[4] * p2, a[3] * q + a[4] * q2, a[3] * r0 + a[4] * r1 + a[5],
            };
            return r;
        }
    }
}
