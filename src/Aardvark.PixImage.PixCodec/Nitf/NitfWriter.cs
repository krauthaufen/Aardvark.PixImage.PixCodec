using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Aardvark.ImageCodecs
{
    public sealed class NitfEncodeOptions
    {
        /// <summary>
        /// IMODE: 'B' band interleaved by block (the default), 'P' by pixel, 'R' by row, 'S' band sequential. One band is
        /// always written as B, and S with a single block too: the standard has those cases collapse to B.
        /// </summary>
        public char Mode { get; set; } = 'B';

        /// <summary>
        /// NPPBH / NPPBV, 1 to 8192. 0 (the default) writes one block when both sides are at most 8192 pixels, else
        /// blocks of 1024 (the standard requires blocking beyond 8192).
        /// </summary>
        public int BlockWidth { get; set; }
        public int BlockHeight { get; set; }

        /// <summary>FTITLE (80 characters at most).</summary>
        public string Title { get; set; } = "";

        /// <summary>IID1 (10 characters) and IID2 (80).</summary>
        public string ImageId { get; set; } = "";
        public string ImageTitle { get; set; } = "";

        /// <summary>OSTAID (10 characters).</summary>
        public string OriginatingStation { get; set; } = "PixCodec";

        /// <summary>ICAT: VIS (the default), MS, HS, IR, SAR, DTEM, ...</summary>
        public string Category { get; set; } = "VIS";

        /// <summary>IREP; null derives it from the colour model: MONO for one band, RGB for RGB, MULTI otherwise.</summary>
        public string? Representation { get; set; }

        /// <summary>ISUBCATn of each band (6 characters each; a wavelength in nanometres for multispectral imagery).</summary>
        public string[]? BandSubcategories { get; set; }

        /// <summary>ICOMn: at most 9 comments of at most 80 characters.</summary>
        public IList<string> Comments { get; set; } = new List<string>();

        /// <summary>
        /// ABPP for integer samples: the significant bits (stored right justified), at most the sample width. 0 (the
        /// default) means all bits. Samples that do not fit are refused.
        /// </summary>
        public int ActualBitsPerPixel { get; set; }

        /// <summary>FDT and IDATIM; null writes the current UTC time.</summary>
        public DateTime? DateTime { get; set; }

        /// <summary>
        /// The placement written as ICORDS / IGEOLO; null takes the image's <see cref="ImageMetadata.Geo"/>. It has to
        /// be WGS 84 geographic (EPSG:4326) or a WGS 84 UTM zone (EPSG:326zz / 327zz). IGEOLO holds the four corner
        /// pixels to 1 arc second (or 0.001 degree, see <see cref="DecimalDegrees"/>), or to 1 metre in UTM.
        /// </summary>
        public GeoReference? Geo { get; set; }

        /// <summary>Write geographic corners as ICORDS D (±dd.ddd) instead of G (ddmmss, the more precise of the two).</summary>
        public bool DecimalDegrees { get; set; }

        /// <summary>TREs of the file header (XHD) and of the image subheader (IXSHD).</summary>
        public IList<NitfTre> FileTres { get; set; } = new List<NitfTre>();
        public IList<NitfTre> ImageTres { get; set; } = new List<NitfTre>();

        public CancellationToken CancellationToken { get; set; }
    }

    /// <summary>Writes NITF 2.1 files with one uncompressed image segment (MIL-STD-2500C tables A-1 and A-3, 5.4.3.3).</summary>
    internal static class NitfWriter
    {
        sealed class Fields
        {
            readonly MemoryStream _s = new();
            public long Length => _s.Length;

            public void Text(string v, int n)
            {
                if (v.Length > n) throw new ArgumentException($"NITF: '{v}' is longer than its {n} character field");
                foreach (char c in v)
                    if (c < 0x20 || c > 0x7E) throw new ArgumentException($"NITF: '{v}' holds a character a BCS field cannot");
                _s.Write(Encoding.ASCII.GetBytes(v.PadRight(n)));
            }

            public void Num(long v, int n)
            {
                var s = v.ToString(CultureInfo.InvariantCulture).PadLeft(n, '0');
                if (s.Length > n || v < 0) throw new ImageNotSupportedException($"NITF: {v} does not fit a {n} digit field");
                _s.Write(Encoding.ASCII.GetBytes(s));
            }

            public void Bytes(ReadOnlySpan<byte> b) => _s.Write(b);
            public byte[] ToArray() => _s.ToArray();
        }

        const string Security = "U";   // ISCLAS, the remaining 166 characters of the security fields are spaces

        internal static void Encode(RawImage image, Stream output, NitfEncodeOptions? options)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(output);
            var o = options ?? new NitfEncodeOptions();
            int w = image.Width, h = image.Height, nb = image.Channels;
            var (pvtype, nbpp) = image.SampleType switch
            {
                SampleType.UInt8 => ("INT", 8), SampleType.UInt16 => ("INT", 16), SampleType.UInt32 => ("INT", 32), SampleType.UInt64 => ("INT", 64),
                SampleType.Int8 => ("SI", 8), SampleType.Int16 => ("SI", 16), SampleType.Int32 => ("SI", 32), SampleType.Int64 => ("SI", 64),
                SampleType.Float32 => ("R", 32), SampleType.Float64 => ("R", 64),
                _ => throw new ImageNotSupportedException($"NITF: {image.SampleType} samples cannot be written (PVTYPE knows integers and 32 / 64 bit reals)"),
            };
            if (w > 99999999 || h > 99999999) throw new ImageNotSupportedException("NITF: NROWS / NCOLS hold at most 8 digits");
            if (nb > 99999) throw new ImageNotSupportedException("NITF: at most 99999 bands");
            int abpp = o.ActualBitsPerPixel == 0 ? nbpp : o.ActualBitsPerPixel;
            if (abpp < 1 || abpp > nbpp || pvtype == "R" && abpp != nbpp)
                throw new ArgumentException($"NITF: ActualBitsPerPixel {o.ActualBitsPerPixel} for {nbpp} bit {pvtype} samples");
            if (abpp < nbpp) CheckFits(image, abpp, pvtype == "SI");

            int bw = o.BlockWidth, bh = o.BlockHeight;
            if (bw == 0) bw = w <= 8192 && h <= 8192 ? w : Math.Min(w, 1024);
            if (bh == 0) bh = w <= 8192 && h <= 8192 ? h : Math.Min(h, 1024);
            if (bw < 1 || bh < 1 || bw > 8192 || bh > 8192) throw new ArgumentException("NITF: blocks are 1 to 8192 pixels wide and high");
            int nbpr = (w + bw - 1) / bw, nbpc = (h + bh - 1) / bh;
            if (nbpr > 9999 || nbpc > 9999) throw new ImageNotSupportedException("NITF: at most 9999 blocks per row and column");
            char mode = o.Mode;
            if (mode is not ('B' or 'P' or 'R' or 'S')) throw new ArgumentException($"NITF: IMODE '{mode}'");
            if (nb == 1 || mode == 'S' && nbpr * nbpc == 1) mode = 'B';

            string irep = o.Representation ?? (nb == 1 ? "MONO" : image.Color == ColorModel.Rgb ? "RGB" : "MULTI");
            string[] reps = new string[nb];
            for (int b = 0; b < nb; b++)
                reps[b] = nb == 1 ? "M"
                    : image.Color is ColorModel.Rgb or ColorModel.Rgba && b < 3 && irep is "RGB" or "MULTI" ? "RGB"[b].ToString()
                    : "";
            var (icords, igeolo) = Corners(o.Geo ?? image.Metadata?.Geo, w, h, o.DecimalDegrees);
            var date = (o.DateTime ?? System.DateTime.UtcNow).ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            if (o.Comments.Count > 9) throw new ArgumentException("NITF: at most 9 image comments");

            // ---- image subheader (table A-3)
            var s = new Fields();
            s.Text("IM", 2); s.Text(o.ImageId, 10); s.Text(date, 14); s.Text("", 17); s.Text(o.ImageTitle, 80);
            s.Text(Security, 167); s.Text("0", 1); s.Text("", 42);
            s.Num(h, 8); s.Num(w, 8); s.Text(pvtype, 3); s.Text(irep, 8); s.Text(o.Category, 8);
            s.Num(abpp, 2); s.Text("R", 1); s.Text(icords.ToString(), 1);
            if (igeolo != null) s.Text(igeolo, 60);
            s.Num(o.Comments.Count, 1);
            foreach (var c in o.Comments) s.Text(c, 80);
            s.Text("NC", 2);
            if (nb <= 9) s.Num(nb, 1);
            else { s.Num(0, 1); s.Num(nb, 5); }
            for (int b = 0; b < nb; b++)
            {
                s.Text(reps[b], 2);
                s.Text(o.BandSubcategories != null && b < o.BandSubcategories.Length ? o.BandSubcategories[b] : "", 6);
                s.Text("N", 1); s.Text("", 3); s.Num(0, 1);
            }
            s.Num(0, 1); s.Text(mode.ToString(), 1);
            s.Num(nbpr, 4); s.Num(nbpc, 4); s.Num(bw, 4); s.Num(bh, 4); s.Num(nbpp, 2);
            s.Num(1, 3); s.Num(0, 3); s.Num(0, 10); s.Text("1.0", 4);
            s.Num(0, 5);
            TreField(s, o.ImageTres, "IXSHD");
            var sub = s.ToArray();

            long dataLength = (long)nbpr * nbpc * bw * bh * nb * (nbpp / 8);
            if (dataLength > 9999999999) throw new ImageNotSupportedException("NITF: the image data exceeds the 10 digit LI field");

            // ---- file header (table A-1)
            var fh = new Fields();
            void Header(long fl, long hl)
            {
                fh = new Fields();
                fh.Text("NITF", 4); fh.Text("02.10", 5); fh.Num(Clevel(w, h, bw, bh, nb, irep, nbpp, fl), 2); fh.Text("BF01", 4);
                fh.Text(o.OriginatingStation, 10); fh.Text(date, 14); fh.Text(o.Title, 80);
                fh.Text(Security, 167); fh.Num(0, 5); fh.Num(0, 5); fh.Text("0", 1);
                fh.Bytes(new byte[3]);                                     // FBKGC: black
                fh.Text("", 24); fh.Text("", 18);
                fh.Num(fl, 12); fh.Num(hl, 6);
                fh.Num(1, 3); fh.Num(sub.Length, 6); fh.Num(dataLength, 10);
                fh.Num(0, 3); fh.Num(0, 3); fh.Num(0, 3); fh.Num(0, 3); fh.Num(0, 3);   // NUMS NUMX NUMT NUMDES NUMRES
                fh.Num(0, 5);                                              // UDHDL
                TreField(fh, o.FileTres, "XHD");
            }
            Header(0, 0);
            long hlen = fh.Length;
            Header(hlen + sub.Length + dataLength, hlen);
            output.Write(fh.ToArray());
            output.Write(sub);
            WritePixels(image, output, mode, bw, bh, nbpr, nbpc, nbpp / 8, o.CancellationToken);
        }

        static void TreField(Fields f, IList<NitfTre> tres, string area)
        {
            long n = 0;
            foreach (var t in tres) n += 11 + t.Data.Length;
            if (n == 0) { f.Num(0, 5); return; }
            if (n + 3 > 99999) throw new ImageNotSupportedException($"NITF: the TREs of {area} take {n} bytes, more than the field holds (overflow extensions are not written)");
            f.Num(n + 3, 5); f.Num(0, 3);
            foreach (var t in tres)
            {
                if (t.Data.Length > 99999) throw new ImageNotSupportedException($"NITF: TRE {t.Tag} is longer than 99999 bytes");
                f.Text(t.Tag, 6); f.Num(t.Data.Length, 5); f.Bytes(t.Data);
            }
        }

        // table A-10: the lowest level whose limits the file stays within
        static int Clevel(int w, int h, int bw, int bh, int nb, string irep, int nbpp, long fileLength)
        {
            int side = Math.Max(w, h), block = Math.Max(bw, bh);
            if (side <= 2048 && block <= 2048 && fileLength < 52428800 && nb <= 9 && !(irep == "RGB" && nbpp > 8)) return 3;
            if (side <= 8192 && fileLength < 1073741824 && nb <= 255) return 5;
            if (side <= 65536 && fileLength < 2147483648 && nb <= 999) return 6;
            if (fileLength < 10737418240 && nb <= 999) return 7;
            return 9;
        }

        static void CheckFits(RawImage img, int abpp, bool signed)
        {
            long lo = signed ? -(1L << (abpp - 1)) : 0, hi = signed ? (1L << (abpp - 1)) - 1 : (long)((1UL << abpp) - 1);
            long n = img.Data.LongLength;
            for (long i = 0; i < n; i++)
            {
                long v = img.SampleType switch
                {
                    SampleType.UInt8 => ((byte[])img.Data)[i], SampleType.UInt16 => ((ushort[])img.Data)[i],
                    SampleType.UInt32 => ((uint[])img.Data)[i], SampleType.UInt64 => (long)Math.Min(((ulong[])img.Data)[i], long.MaxValue),
                    SampleType.Int8 => ((sbyte[])img.Data)[i], SampleType.Int16 => ((short[])img.Data)[i],
                    SampleType.Int32 => ((int[])img.Data)[i], _ => ((long[])img.Data)[i],
                };
                if (v < lo || v > hi) throw new ArgumentException($"NITF: sample {v} does not fit ActualBitsPerPixel {abpp}");
            }
        }

        /// <summary>
        /// ICORDS and IGEOLO for a placement: the positions of the corner pixels (0,0), (0,MaxCol), (MaxRow,MaxCol),
        /// (MaxRow,0) (table A-3): the centres of their cells, the affine mapping grid positions to cell corners.
        /// </summary>
        static (char, string?) Corners(GeoReference? g, int w, int h, bool decimalDegrees)
        {
            if (g == null) return (' ', null);
            var crs = g.Crs ?? throw new ImageNotSupportedException("NITF: the placement names no coordinate system; IGEOLO needs WGS 84 degrees or a WGS 84 UTM zone");
            var pts = new (double X, double Y)[4];
            var grid = new (double C, double R)[] { (0, 0), (w - 1, 0), (w - 1, h - 1), (0, h - 1) };
            for (int i = 0; i < 4; i++) pts[i] = g.ToWorld(grid[i].C + 0.5, grid[i].R + 0.5);   // cell centres
            int epsg = crs.Epsg ?? 0;
            var sb = new StringBuilder();
            if (epsg == 4326)
            {
                foreach (var (lon0, lat) in pts)
                {
                    double lon = lon0 > 180 ? lon0 - 360 : lon0 < -180 ? lon0 + 360 : lon0;
                    if (Math.Abs(lat) > 90 || Math.Abs(lon) > 180 || !double.IsFinite(lat + lon))
                        throw new ImageNotSupportedException($"NITF: the corner ({lon}, {lat}) is no longitude / latitude");
                    sb.Append(decimalDegrees ? Dd(lat, lon) : Dms(lat, 2, 'N', 'S') + Dms(lon, 3, 'E', 'W'));
                }
                return (decimalDegrees ? 'D' : 'G', sb.ToString());
            }
            if (epsg is >= 32601 and <= 32660 or >= 32701 and <= 32760)
            {
                int zone = epsg % 100;
                foreach (var (e0, n0) in pts)
                {
                    long e = (long)Math.Round(e0), n = (long)Math.Round(n0);
                    if (e < 0 || e > 999999 || n < 0 || n > 9999999)
                        throw new ImageNotSupportedException($"NITF: the corner ({e0}, {n0}) does not fit IGEOLO's UTM easting / northing");
                    sb.Append(zone.ToString("00", CultureInfo.InvariantCulture)).Append(e.ToString("000000", CultureInfo.InvariantCulture))
                      .Append(n.ToString("0000000", CultureInfo.InvariantCulture));
                }
                return (epsg >= 32701 ? 'S' : 'N', sb.ToString());
            }
            throw new ImageNotSupportedException($"NITF: IGEOLO can hold WGS 84 degrees (EPSG:4326) or a WGS 84 UTM zone, not {crs}");
        }

        static string Dms(double v, int degreeDigits, char pos, char neg)
        {
            char hemi = v < 0 ? neg : pos;
            long t = (long)Math.Round(Math.Abs(v) * 3600);
            long d = t / 3600, m = t / 60 % 60, s = t % 60;
            return d.ToString(new string('0', degreeDigits), CultureInfo.InvariantCulture) + m.ToString("00", CultureInfo.InvariantCulture)
                 + s.ToString("00", CultureInfo.InvariantCulture) + hemi;
        }

        static string Dd(double lat, double lon)
            => lat.ToString("+00.000;-00.000", CultureInfo.InvariantCulture) + lon.ToString("+000.000;-000.000", CultureInfo.InvariantCulture);

        // 5.4.3.3.1, whole octets only: samples big endian, fill pixels zero
        static void WritePixels(RawImage img, Stream output, char mode, int bw, int bh, int nbpr, int nbpc, int bytes, CancellationToken cancel)
        {
            var unit = new byte[(long)bw * bh * (mode == 'S' ? 1 : img.Channels) * bytes];
            int bands = mode == 'S' ? img.Channels : 1;
            for (int b = 0; b < bands; b++)
                for (int by = 0; by < nbpc; by++)
                {
                    cancel.ThrowIfCancellationRequested();
                    for (int bx = 0; bx < nbpr; bx++)
                    {
                        int n = Block(img.GetBytes(), img.Width, img.Height, img.Channels, unit, mode, bx * bw, by * bh, bw, bh, b, bytes);
                        output.Write(unit, 0, n);
                    }
                }
        }

        // the stored bytes of one block (of one band for IMODE S)
        static int Block(ReadOnlySpan<byte> src, int w, int h, int nb, Span<byte> u, char mode, int x0, int y0, int bw, int bh, int band, int bytes)
        {
            long rowBytes = (long)w * nb * bytes;
            int p = 0;
            void Put(ReadOnlySpan<byte> src, Span<byte> u, int x, int y, int b, ref int p)
            {
                var to = u.Slice(p, bytes);
                p += bytes;
                if (x >= w || y >= h) { to.Clear(); return; }
                var from = src.Slice((int)(y * rowBytes + ((long)x * nb + b) * bytes), bytes);
                for (int k = 0; k < bytes; k++) to[k] = from[bytes - 1 - k];   // little endian host to big endian file
            }
            switch (mode)
            {
                case 'P':
                    for (int y = 0; y < bh; y++) for (int x = 0; x < bw; x++) for (int b = 0; b < nb; b++) Put(src, u, x0 + x, y0 + y, b, ref p);
                    break;
                case 'R':
                    for (int y = 0; y < bh; y++) for (int b = 0; b < nb; b++) for (int x = 0; x < bw; x++) Put(src, u, x0 + x, y0 + y, b, ref p);
                    break;
                case 'S':
                    for (int y = 0; y < bh; y++) for (int x = 0; x < bw; x++) Put(src, u, x0 + x, y0 + y, band, ref p);
                    break;
                default:
                    for (int b = 0; b < nb; b++) for (int y = 0; y < bh; y++) for (int x = 0; x < bw; x++) Put(src, u, x0 + x, y0 + y, b, ref p);
                    break;
            }
            return p;
        }
    }
}
