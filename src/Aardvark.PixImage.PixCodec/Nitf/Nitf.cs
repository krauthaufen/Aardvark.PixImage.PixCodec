using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Aardvark.ImageCodecs
{
    public sealed class NitfDecodeOptions
    {
        /// <summary>Which image segment to decode (0 is the first).</summary>
        public int ImageIndex { get; set; }

        /// <summary>
        /// Map a single band through its look-up tables: three tables give RGB, one gives grey, two give 16 bit grey (most
        /// and least significant byte, A-3 NLUTSn). Off returns the stored indices; the tables stay in
        /// <see cref="NitfBand.LookUpTables"/>. Tables of multi-band images are never applied.
        /// </summary>
        public bool ApplyLookUpTables { get; set; } = true;

        /// <summary>Upper bound for the image in pixels; larger segments are rejected with <see cref="ImageNotSupportedException"/>.</summary>
        public long MaxPixels { get; set; } = 1L << 30;

        public CancellationToken CancellationToken { get; set; }

        internal static readonly NitfDecodeOptions Default = new();
    }

    /// <summary>
    /// NITF 2.1 / NSIF 1.0 (MIL-STD-2500C, STANAG 4545) and NITF 2.0 (MIL-STD-2500A) image files.
    /// <para>
    /// Reads every image segment (<see cref="NitfDecodeOptions.ImageIndex"/> picks one) uncompressed (IC NC, and NM with
    /// its block mask), JPEG 2000 compressed (C8 / M8) and JPEG compressed (C3 / M3); all four band orders (IMODE B, P, R,
    /// S), blocking with fill, 1 to 64 storage bits per sample with the significant bits left or right justified, integer,
    /// bi-level, signed, real and complex samples (complex: two channels, real then imaginary, per band), look-up tables,
    /// up to 99999 bands, and the TREs of the file and of each image, overflow extensions included
    /// (<see cref="ReadFile(ReadOnlySpan{byte})"/>).
    /// </para>
    /// <para>
    /// Channels are the bands in file order, except that an RGB image comes back as R, G, B whatever order its IREPBANDn
    /// give. One band is <see cref="ColorModel.Gray"/>, RGB and RGB/LUT images <see cref="ColorModel.Rgb"/>, anything else
    /// <see cref="ColorModel.Other"/>.
    /// </para>
    /// <para>
    /// The image corners in IGEOLO become <see cref="ImageMetadata.Geo"/> (see <see cref="ReadGeo"/>).
    /// </para>
    /// </summary>
    public static class Nitf
    {
        /// <summary>Whether the data starts like a NITF 2.0 / 2.1 or NSIF 1.0 file header.</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header)
            => header.Length >= 9 && (header.StartsWith("NITF02.10"u8) || header.StartsWith("NSIF01.00"u8) || header.StartsWith("NITF02.00"u8));

        /// <summary>The header structure of a file: fields, TREs, every segment and where its data lies. No pixels are read.</summary>
        public static NitfFile ReadFile(ReadOnlySpan<byte> data) => NitfParser.Parse(data);

        public static NitfFile ReadFile(string path) => ReadFile(File.ReadAllBytes(path));

        public static NitfFile ReadFile(Stream stream) => ReadFile(ReadAll(stream));

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data, NitfDecodeOptions? options = null)
        {
            var o = options ?? NitfDecodeOptions.Default;
            var seg = Pick(ReadFile(data), o.ImageIndex);
            var (type, channels, color, bits) = Output(seg, o);
            return new ImageInfo(ImageFormat.Nitf, seg.Columns, seg.Rows, channels, type, color) { BitsPerSample = bits };
        }

        public static RawImage Decode(ReadOnlySpan<byte> data, NitfDecodeOptions? options = null)
        {
            var o = options ?? NitfDecodeOptions.Default;
            var file = ReadFile(data);
            return Decode(data, Pick(file, o.ImageIndex), o);
        }

        public static RawImage Decode(Stream stream, NitfDecodeOptions? options = null) => Decode(ReadAll(stream), options);

        public static RawImage Decode(string path, NitfDecodeOptions? options = null) => Decode(File.ReadAllBytes(path), options);

        /// <summary>Decodes one image segment of a file read with <see cref="ReadFile(ReadOnlySpan{byte})"/> from the same data.</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, NitfImageSegment segment, NitfDecodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(segment);
            var o = options ?? NitfDecodeOptions.Default;
            Check(segment, o);
            RawImage raw = segment.Compression switch
            {
                "NC" or "NM" => NitfPixels.Uncompressed(data, segment, o.CancellationToken),
                "C8" or "M8" => NitfPixels.Jpeg2000(data, segment, o),
                "C3" or "M3" => NitfPixels.Jpeg(data, segment, o.CancellationToken),
                _ => throw new ImageNotSupportedException($"NITF: image compression {segment.Compression} ({CompressionName(segment.Compression)}) is not supported"),
            };
            var img = Finish(raw, segment, o);
            img.Metadata = new ImageMetadata { Geo = ReadGeo(segment) };
            return img;
        }

        /// <summary>Every image segment of the file, in file order.</summary>
        public static RawImage[] DecodeAll(ReadOnlySpan<byte> data, NitfDecodeOptions? options = null)
        {
            var o = options ?? NitfDecodeOptions.Default;
            var file = ReadFile(data);
            var r = new RawImage[file.Images.Length];
            for (int i = 0; i < r.Length; i++) r[i] = Decode(data, file.Images[i], o);
            return r;
        }

        static byte[] ReadAll(Stream s)
        {
            ArgumentNullException.ThrowIfNull(s);
            using var m = new MemoryStream();
            s.CopyTo(m);
            return m.ToArray();
        }

        static NitfImageSegment Pick(NitfFile f, int index)
        {
            if (f.Images.Length == 0) throw new ImageFormatException("NITF: the file holds no image segment");
            if (index < 0 || index >= f.Images.Length)
                throw new ArgumentOutOfRangeException(nameof(index), $"NITF: image {index} asked for, the file holds {f.Images.Length}");
            return f.Images[index];
        }

        static string CompressionName(string ic) => ic switch
        {
            "C1" or "M1" => "bi-level, ITU-T T.4",
            "C4" or "M4" => "vector quantization",
            "C5" or "M5" or "I1" => "lossless / downsampled JPEG",
            "C6" or "M6" or "C7" or "M7" => "reserved",
            _ => "unknown",
        };

        // ---------------------------------------------------------------- sample layout

        /// <summary>The sample type of one stored band value, and how many values (2 for complex) make one band sample.</summary>
        internal static (SampleType Type, int Parts) StoredType(NitfImageSegment s)
        {
            int n = s.BitsPerPixel;
            return s.PixelValueType switch
            {
                "INT" or "B" => (n <= 8 ? SampleType.UInt8 : n <= 16 ? SampleType.UInt16 : n <= 32 ? SampleType.UInt32 : SampleType.UInt64, 1),
                "SI" => (n <= 8 ? SampleType.Int8 : n <= 16 ? SampleType.Int16 : n <= 32 ? SampleType.Int32 : SampleType.Int64, 1),
                "R" when n == 32 => (SampleType.Float32, 1),
                "R" when n == 64 => (SampleType.Float64, 1),
                "C" when n == 64 => (SampleType.Float32, 2),
                _ => throw new ImageNotSupportedException($"NITF: PVTYPE {s.PixelValueType} with NBPP {n} is not supported"),
            };
        }

        static void Check(NitfImageSegment s, NitfDecodeOptions o)
        {
            if (s.Encrypted) throw new ImageNotSupportedException("NITF: the image segment is encrypted");
            if (s.Rows < 1 || s.Columns < 1) throw new ImageFormatException($"NITF: the image is {s.Columns} x {s.Rows}");
            if (s.Bands.Length == 0) throw new ImageFormatException("NITF: the image has no bands");
            if (s.BitsPerPixel < 1 || s.BitsPerPixel > 64) throw new ImageNotSupportedException($"NITF: NBPP {s.BitsPerPixel} is not supported");
            if (s.ActualBitsPerPixel > s.BitsPerPixel) throw new ImageFormatException($"NITF: ABPP {s.ActualBitsPerPixel} exceeds NBPP {s.BitsPerPixel}");
            if (s.BlocksPerRow < 1 || s.BlocksPerColumn < 1 || s.BlockWidth < 1 || s.BlockHeight < 1)
                throw new ImageFormatException("NITF: the blocking fields NBPR, NBPC, NPPBH, NPPBV are not usable");
            if ((long)s.BlocksPerRow * s.BlockWidth < s.Columns || (long)s.BlocksPerColumn * s.BlockHeight < s.Rows)
                throw new ImageFormatException("NITF: the blocks do not cover the image (NBPR * NPPBH < NCOLS or NBPC * NPPBV < NROWS)");
            if (s.Mode is not ('B' or 'P' or 'R' or 'S')) throw new ImageFormatException($"NITF: IMODE '{s.Mode}'");
            long pixels = (long)s.Rows * s.Columns;
            if (pixels > o.MaxPixels) throw new ImageNotSupportedException($"NITF: {s.Columns} x {s.Rows} exceeds MaxPixels");
            StoredType(s);
        }

        // ---------------------------------------------------------------- look-up tables and colour

        enum Lut { None, Grey, Grey16, Rgb }

        static Lut LutMode(NitfImageSegment s, NitfDecodeOptions o)
        {
            if (!o.ApplyLookUpTables || s.Bands.Length != 1 || s.PixelValueType is not ("INT" or "B")) return Lut.None;
            int n = s.Bands[0].LookUpTables.Length;
            return n switch { 1 => Lut.Grey, 2 => Lut.Grey16, 3 => Lut.Rgb, _ => Lut.None };
        }

        // the R, G and B bands of an RGB image, in that order; null when the image is not one
        static int[]? RgbOrder(NitfImageSegment s)
        {
            if (s.Representation != "RGB" || s.Bands.Length != 3) return null;
            int r = -1, g = -1, b = -1;
            for (int i = 0; i < 3; i++)
                switch (s.Bands[i].Representation)
                {
                    case "R": r = i; break;
                    case "G": g = i; break;
                    case "B": b = i; break;
                }
            return r < 0 || g < 0 || b < 0 ? new[] { 0, 1, 2 } : new[] { r, g, b };
        }

        static (SampleType Type, int Channels, ColorModel Color, int Bits) Output(NitfImageSegment s, NitfDecodeOptions o)
        {
            Check(s, o);
            var (t, parts) = StoredType(s);
            int bits = s.PixelValueType is "INT" or "SI" or "B" ? s.ActualBitsPerPixel : RawImage.BytesPerSample(t) * 8;
            switch (LutMode(s, o))
            {
                case Lut.Rgb: return (SampleType.UInt8, 3, ColorModel.Rgb, 8);
                case Lut.Grey: return (SampleType.UInt8, 1, ColorModel.Gray, 8);
                case Lut.Grey16: return (SampleType.UInt16, 1, ColorModel.Gray, 16);
            }
            int ch = s.Bands.Length * parts;
            var color = ch == 1 ? ColorModel.Gray
                : RgbOrder(s) != null || s.Representation == "YCbCr601" && s.Bands.Length == 3 ? ColorModel.Rgb
                : ColorModel.Other;
            return (t, ch, color, bits);
        }

        static RawImage Finish(RawImage raw, NitfImageSegment s, NitfDecodeOptions o)
        {
            var lut = LutMode(s, o);
            if (lut != Lut.None) return ApplyLut(raw, s.Bands[0].LookUpTables, lut);
            if (raw.Channels == 1) return new RawImage(raw.Width, raw.Height, ColorModel.Gray, raw.Data, 1);
            if (s.Representation == "YCbCr601" && raw.Channels == 3) return new RawImage(raw.Width, raw.Height, ColorModel.Rgb, raw.Data, 3);
            var order = RgbOrder(s);
            if (order == null) return new RawImage(raw.Width, raw.Height, ColorModel.Other, raw.Data, raw.Channels);
            if (order[0] == 0 && order[1] == 1 && order[2] == 2) return new RawImage(raw.Width, raw.Height, ColorModel.Rgb, raw.Data, 3);
            var dst = RawImage.Create(raw.Width, raw.Height, ColorModel.Rgb, raw.SampleType, 3);
            int bps = RawImage.BytesPerSample(raw.SampleType);
            var src = raw.GetBytes();
            var d = dst.GetBytes();
            long n = (long)raw.Width * raw.Height;
            for (long p = 0; p < n; p++)
                for (int c = 0; c < 3; c++)
                    src.Slice((int)((p * 3 + order[c]) * bps), bps).CopyTo(d.Slice((int)((p * 3 + c) * bps), bps));
            return dst;
        }

        static RawImage ApplyLut(RawImage raw, byte[][] luts, Lut mode)
        {
            int w = raw.Width, h = raw.Height, n = luts[0].Length;
            long count = (long)w * h;
            int Index(long i)
            {
                ulong v = raw.SampleType switch
                {
                    SampleType.UInt8 => ((byte[])raw.Data)[i],
                    SampleType.UInt16 => ((ushort[])raw.Data)[i],
                    SampleType.UInt32 => ((uint[])raw.Data)[i],
                    _ => ((ulong[])raw.Data)[i],
                };
                if (v >= (ulong)n)
                    throw new ImageFormatException($"NITF: sample value {v} lies outside the look-up table of {n} entries (decode with ApplyLookUpTables off for the indices)");
                return (int)v;
            }
            switch (mode)
            {
                case Lut.Rgb:
                {
                    var d = new byte[count * 3];
                    for (long i = 0; i < count; i++) { int k = Index(i); d[i * 3] = luts[0][k]; d[i * 3 + 1] = luts[1][k]; d[i * 3 + 2] = luts[2][k]; }
                    return new RawImage(w, h, ColorModel.Rgb, d, 3);
                }
                case Lut.Grey16:
                {
                    var d = new ushort[count];
                    for (long i = 0; i < count; i++) { int k = Index(i); d[i] = (ushort)(luts[0][k] << 8 | luts[1][k]); }
                    return new RawImage(w, h, ColorModel.Gray, d, 1);
                }
                default:
                {
                    var d = new byte[count];
                    for (long i = 0; i < count; i++) d[i] = luts[0][Index(i)];
                    return new RawImage(w, h, ColorModel.Gray, d, 1);
                }
            }
        }

        // ---------------------------------------------------------------- placement

        /// <summary>
        /// The placement IGEOLO gives an image segment, null when it gives none that is a grid.
        /// <para>
        /// IGEOLO holds the locations of the image corners "in image coordinate order: (0,0), (0, MaxCol), (MaxRow, MaxCol),
        /// (MaxRow, 0)" with MaxCol = NCOLS - 1, MaxRow = NROWS - 1 (MIL-STD-2500C table A-3): the positions of the four
        /// corner pixels, not the outer corners of the image. The affine therefore maps grid position (col, row) to the
        /// position of pixel (col, row), and <see cref="GeoReference.RasterType"/> is
        /// <see cref="GeoRasterType.PixelIsPoint"/>; the outer corner of the image lies half a pixel further out. GDAL
        /// reports the same placement as an area grid shifted by that half pixel.
        /// </para>
        /// <para>
        /// ICORDS G and D (degrees: x is longitude, y latitude) are WGS 84, EPSG:4326, the standard's default geodetic
        /// system; N and S are WGS 84 UTM zones (EPSG:326zz / 327zz). The corners must form a parallelogram within the
        /// precision IGEOLO is written with (1 arc second, 0.001 degree or 1 metre, doubled for the rounding of the
        /// three corners the fourth is predicted from) and lie in one UTM zone; otherwise, and for MGRS (U), geocentric
        /// (NITF 2.0 C) or corners left blank, the result is null and the corners stay in
        /// <see cref="NitfImageSegment.GeoLocation"/>. IGEOLO is approximate by definition ("not intended for analytical
        /// purposes"); exact placement lives in TREs such as RPC00B or the GEOxxA set, which are kept but not interpreted.
        /// </para>
        /// </summary>
        public static GeoReference? ReadGeo(NitfImageSegment segment)
        {
            ArgumentNullException.ThrowIfNull(segment);
            var corners = ReadCorners(segment);
            if (corners == null) return null;
            var (xy, zone, kind) = corners.Value;
            int maxCol = segment.Columns - 1, maxRow = segment.Rows - 1;
            if (maxCol < 1 || maxRow < 1) return null;
            double precision = kind switch { 'G' => 1.0 / 3600, 'D' => 0.001, _ => 1 };
            // c0 = (0,0), c1 = (0,MaxCol), c2 = (MaxRow,MaxCol), c3 = (MaxRow,0) as (row, col)
            var (x0, y0) = xy[0]; var (x1, y1) = xy[1]; var (x2, y2) = xy[2]; var (x3, y3) = xy[3];
            double tol = 2 * precision + 1e-9;
            if (Math.Abs(x1 + x3 - x0 - x2) > tol || Math.Abs(y1 + y3 - y0 - y2) > tol) return null;
            var g = new GeoReference
            {
                Affine = new[] { (x1 - x0) / maxCol, (x3 - x0) / maxRow, x0, (y1 - y0) / maxCol, (y3 - y0) / maxRow, y0 },
                RasterType = GeoRasterType.PixelIsPoint,
            };
            if (kind is 'G' or 'D')
            {
                g.ModelType = GeoModelType.Geographic;
                g.Crs = new GeoCrs { Epsg = 4326, Name = "WGS 84", DatumEpsg = 6326, Datum = "WGS 84", AngularUnit = GeoUnits.Degree };
            }
            else
            {
                bool south = kind == 'S';
                g.ModelType = GeoModelType.Projected;
                g.Crs = new GeoCrs
                {
                    Epsg = (south ? 32700 : 32600) + zone,
                    Name = $"WGS 84 / UTM zone {zone}{(south ? 'S' : 'N')}",
                    DatumEpsg = 6326, Datum = "WGS 84", LinearUnit = GeoUnits.Metre,
                };
            }
            return g;
        }

        /// <summary>
        /// The four corners IGEOLO holds, as (x, y): longitude / latitude in degrees for ICORDS G and D, easting / northing
        /// for N and S, in the order (0,0), (0,MaxCol), (MaxRow,MaxCol), (MaxRow,0); with the UTM zone (0 for degrees).
        /// Null when the segment has no corners this reader understands.
        /// </summary>
        public static ((double X, double Y)[] Corners, int Zone, char Kind)? ReadCorners(NitfImageSegment segment)
        {
            ArgumentNullException.ThrowIfNull(segment);
            var s = segment.GeoLocation;
            if (s == null || s.Length != 60 || s.Trim().Length == 0) return null;
            char k = segment.CoordinateSystem;
            if (k is not ('G' or 'D' or 'N' or 'S')) return null;
            var xy = new (double, double)[4];
            int zone = 0;
            for (int i = 0; i < 4; i++)
            {
                var c = s.Substring(i * 15, 15);
                if (c.Trim().Length == 0) return null;
                switch (k)
                {
                    case 'G':
                    {
                        // ddmmssXdddmmssY, non-significant digits replaced by spaces
                        double? lat = Dms(c.Substring(0, 6), 2, c[6], 'N', 'S'), lon = Dms(c.Substring(7, 7), 3, c[14], 'E', 'W');
                        if (lat == null || lon == null) return null;
                        xy[i] = (lon.Value, lat.Value);
                        break;
                    }
                    case 'D':
                    {
                        // ±dd.ddd±ddd.ddd
                        if (!TryNum(c.Substring(0, 7), out double lat) || !TryNum(c.Substring(7, 8), out double lon)) return null;
                        xy[i] = (lon, lat);
                        break;
                    }
                    default:
                    {
                        // zzeeeeeennnnnnn
                        if (!int.TryParse(c.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int z) || z < 1 || z > 60) return null;
                        if (!TryNum(c.Substring(2, 6), out double e) || !TryNum(c.Substring(8, 7), out double n)) return null;
                        if (i > 0 && z != zone) return null;
                        zone = z;
                        xy[i] = (e, n);
                        break;
                    }
                }
            }
            if (k is 'G' or 'D')
            {
                // a grid across the antimeridian: keep the longitudes continuous with the first corner
                for (int i = 1; i < 4; i++)
                {
                    double d = xy[i].Item1 - xy[0].Item1;
                    if (d > 180) xy[i].Item1 -= 360;
                    else if (d < -180) xy[i].Item1 += 360;
                }
            }
            return (xy, zone, k);
        }

        static bool TryNum(string s, out double v)
            => double.TryParse(s.Replace(" ", ""), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out v);

        // degrees, minutes, seconds with blanks for the digits not given
        static double? Dms(string s, int degreeDigits, char hemisphere, char positive, char negative)
        {
            double Part(string p) => p.Trim().Length == 0 ? 0 : double.Parse(p, CultureInfo.InvariantCulture);
            if (hemisphere != positive && hemisphere != negative) return null;
            foreach (char ch in s) if (ch != ' ' && !char.IsAsciiDigit(ch)) return null;
            if (s.Substring(0, degreeDigits).Trim().Length == 0) return null;
            double v = Part(s.Substring(0, degreeDigits)) + Part(s.Substring(degreeDigits, 2)) / 60 + Part(s.Substring(degreeDigits + 2, 2)) / 3600;
            return hemisphere == negative ? -v : v;
        }
    }
}
