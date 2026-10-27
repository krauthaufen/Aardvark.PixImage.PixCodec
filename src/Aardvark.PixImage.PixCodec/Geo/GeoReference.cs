using System;
using System.Collections.Generic;
using System.Globalization;

namespace Aardvark.ImageCodecs
{
    /// <summary>Kind of world coordinates a georeferenced grid uses (the GeoTIFF GTModelTypeGeoKey codes).</summary>
    public enum GeoModelType
    {
        /// <summary>The file does not say.</summary>
        Unknown = 0,
        /// <summary>Map projection plane coordinates (UTM, Lambert, State Plane, ...).</summary>
        Projected = 1,
        /// <summary>Longitude and latitude.</summary>
        Geographic = 2,
        /// <summary>Earth-centred, earth-fixed x, y, z.</summary>
        Geocentric = 3,
    }

    /// <summary>
    /// What the file says a sample stands for (the GeoTIFF GTRasterTypeGeoKey codes). Information only:
    /// <see cref="GeoReference.Affine"/> maps grid positions to cell corners whatever this says, so the value of sample
    /// (col, row) belongs to world position (col + 0.5, row + 0.5); a reader converts a point grid to that, a writer back.
    /// </summary>
    public enum GeoRasterType
    {
        /// <summary>The sample is the value of its whole cell. The default.</summary>
        PixelIsArea = 1,
        /// <summary>The sample is a point measurement at the centre of its cell (grids, DEMs).</summary>
        PixelIsPoint = 2,
    }

    /// <summary>
    /// EPSG units of measure that georeferenced files name (GeoTIFF ProjLinearUnitsGeoKey, GeogAngularUnitsGeoKey).
    /// Names and factors are those of the EPSG registry; unknown codes are reported by number.
    /// </summary>
    public static class GeoUnits
    {
        public const int Metre = 9001, Foot = 9002, UsSurveyFoot = 9003, Radian = 9101, Degree = 9102;

        // code, name, metres (length) or radians (angle) per unit; from the EPSG registry (see THIRD-PARTY-NOTICES.md)
        static readonly Dictionary<int, (string Name, double Factor, bool Angle)> Known = new()
        {
            [1025] = ("millimetre", 0.001, false),
            [1033] = ("centimetre", 0.01, false),
            [9001] = ("metre", 1, false),
            [9002] = ("foot", 0.3048, false),
            [9003] = ("US survey foot", 1200.0 / 3937, false),
            [9030] = ("nautical mile", 1852, false),
            [9036] = ("kilometre", 1000, false),
            [9093] = ("statute mile", 1609.344, false),
            [9096] = ("yard", 0.9144, false),
            [9101] = ("radian", 1, true),
            [9102] = ("degree", Math.PI / 180, true),
            [9104] = ("arc-second", Math.PI / 648000, true),
            [9105] = ("grad", Math.PI / 200, true),
            [9122] = ("degree", Math.PI / 180, true),
        };

        /// <summary>The unit's registry name, null when the code is not one listed here.</summary>
        public static string? Name(int code) => Known.TryGetValue(code, out var u) ? u.Name : null;

        /// <summary>Metres per unit for a length, radians per unit for an angle; null when unknown.</summary>
        public static double? Factor(int code) => Known.TryGetValue(code, out var u) ? u.Factor : null;

        /// <summary>True for a known angular unit, false for a known length, null when unknown.</summary>
        public static bool? IsAngular(int code) => Known.TryGetValue(code, out var u) ? u.Angle : null;
    }

    /// <summary>
    /// The coordinate reference system a georeferenced grid names. Decoders report what the file says and never
    /// reproject; fields the file does not state stay null or 0.
    /// </summary>
    public sealed class GeoCrs
    {
        /// <summary>EPSG code of the CRS, when the file states one (never a user-defined placeholder such as 32767).</summary>
        public int? Epsg { get; set; }

        /// <summary>The file's name or citation of the CRS ("WGS 84 / UTM zone 33N"), null when none.</summary>
        public string? Name { get; set; }

        /// <summary>The datum, as an EPSG datum code when the file gives one.</summary>
        public int? DatumEpsg { get; set; }

        /// <summary>The datum name as the file writes it, null when none.</summary>
        public string? Datum { get; set; }

        /// <summary>WKT of the CRS when the file holds one (ENVI's coordinate system string, NITF's none).</summary>
        public string? Wkt { get; set; }

        /// <summary>EPSG unit code of projected coordinates (9001 metre), 0 when the file does not say.</summary>
        public int LinearUnit { get; set; }

        /// <summary>EPSG unit code of geographic coordinates (9102 degree), 0 when the file does not say.</summary>
        public int AngularUnit { get; set; }

        public GeoCrs Clone() => (GeoCrs)MemberwiseClone();

        public override string ToString()
            => Epsg is int e ? "EPSG:" + e.ToString(CultureInfo.InvariantCulture) : Name ?? Datum ?? (Wkt != null ? "WKT" : "");
    }

    /// <summary>
    /// Placement of a raster grid in world coordinates: <c>x = A[0]*col + A[1]*row + A[2]</c>,
    /// <c>y = A[3]*col + A[4]*row + A[5]</c> for grid position (col, row), plus the CRS those coordinates refer to.
    /// A north-up grid is <c>[dx, 0, x0, 0, -dy, y0]</c>. This is rasterio's Affine; GDAL's geotransform holds the same
    /// numbers in the order <c>[A2, A0, A1, A5, A3, A4]</c> (<see cref="GetGeoTransform"/>, <see cref="FromGeoTransform"/>).
    /// Grid position (col, row) is always the outer (upper left) corner of cell (col, row), so the value of that sample sits
    /// at (col + 0.5, row + 0.5), for every <see cref="RasterType"/>: the convention of GDAL's geotransform. A format that
    /// places point samples on the grid positions themselves (GeoTIFF PixelIsPoint) is shifted by half a cell when read
    /// and back when written.
    /// </summary>
    public sealed class GeoReference
    {
        double[] _affine = { 1, 0, 0, 0, 1, 0 };

        /// <summary>The six affine coefficients (see the type summary).</summary>
        public double[] Affine
        {
            get => _affine;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                if (value.Length != 6) throw new ArgumentException("an affine has six coefficients", nameof(value));
                _affine = value;
            }
        }

        public GeoModelType ModelType { get; set; }
        public GeoRasterType RasterType { get; set; } = GeoRasterType.PixelIsArea;

        /// <summary>The coordinate system, null when the file names none.</summary>
        public GeoCrs? Crs { get; set; }

        /// <summary>The sample value meaning "no data", null when the file names none. Samples are never altered.</summary>
        public double? NoData { get; set; }

        /// <summary>Whether the grid is north-up: no rotation or shear terms.</summary>
        public bool IsAxisAligned => _affine[1] == 0 && _affine[3] == 0;

        public (double X, double Y) ToWorld(double col, double row)
            => (_affine[0] * col + _affine[1] * row + _affine[2], _affine[3] * col + _affine[4] * row + _affine[5]);

        public (double Col, double Row) ToGrid(double x, double y)
        {
            var a = _affine;
            double det = a[0] * a[4] - a[1] * a[3];
            if (det == 0 || !double.IsFinite(det)) throw new InvalidOperationException("the affine is singular");
            double u = x - a[2], v = y - a[5];
            return ((a[4] * u - a[1] * v) / det, (a[0] * v - a[3] * u) / det);
        }

        /// <summary>(minX, minY, maxX, maxY) over the four outer corners of a width x height grid.</summary>
        public (double MinX, double MinY, double MaxX, double MaxY) GetBounds(int width, int height)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var (c, r) in new (double, double)[] { (0, 0), (width, 0), (0, height), (width, height) })
            {
                var (x, y) = ToWorld(c, r);
                x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
            }
            return (x0, y0, x1, y1);
        }

        /// <summary>GDAL's geotransform order: [x0, A0, A1, y0, A3, A4].</summary>
        public double[] GetGeoTransform() => new[] { _affine[2], _affine[0], _affine[1], _affine[5], _affine[3], _affine[4] };

        public static GeoReference FromGeoTransform(ReadOnlySpan<double> gt)
        {
            if (gt.Length != 6) throw new ArgumentException("a geotransform has six coefficients", nameof(gt));
            return new GeoReference { Affine = new[] { gt[1], gt[2], gt[0], gt[4], gt[5], gt[3] } };
        }

        /// <summary>A north-up grid whose upper-left corner is (x0, y0), with positive cell sizes dx and dy.</summary>
        public static GeoReference FromOrigin(double x0, double y0, double dx, double dy, GeoCrs? crs = null)
            => new GeoReference { Affine = new[] { dx, 0, x0, 0, -dy, y0 }, Crs = crs };

        /// <summary>
        /// The same placement for a grid with <paramref name="factor"/> times fewer samples per axis (an overview):
        /// cell sizes grow by the factor, the outer corner stays.
        /// </summary>
        public GeoReference Reduced(double factor)
        {
            var g = Clone();
            var a = _affine;
            g.Affine = new[] { a[0] * factor, a[1] * factor, a[2], a[3] * factor, a[4] * factor, a[5] };
            return g;
        }

        public GeoReference Clone() => new GeoReference
        {
            Affine = (double[])_affine.Clone(), ModelType = ModelType, RasterType = RasterType, Crs = Crs?.Clone(), NoData = NoData,
        };

        public override string ToString()
            => $"{Crs} [{string.Join(", ", Array.ConvertAll(_affine, v => v.ToString("R", CultureInfo.InvariantCulture)))}]".Trim();
    }
}
