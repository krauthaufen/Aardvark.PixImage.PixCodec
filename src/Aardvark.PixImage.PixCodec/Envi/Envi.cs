using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Aardvark.ImageCodecs
{
    /// <summary>How <see cref="Envi"/> reads a raster.</summary>
    public sealed class EnviDecodeOptions
    {
        /// <summary>The sample file, when it is not next to the header under a name <see cref="Envi.FindDataFile"/> finds.</summary>
        public string? DataPath { get; set; }

        /// <summary>Upper bound on the number of samples (pixels x bands x parts); larger rasters throw <see cref="ImageNotSupportedException"/>.</summary>
        public long MaxSamples { get; set; } = Array.MaxLength;

        internal static readonly EnviDecodeOptions Default = new();
    }

    /// <summary>How <see cref="Envi"/> writes a raster.</summary>
    public sealed class EnviEncodeOptions
    {
        public EnviInterleave Interleave { get; set; } = EnviInterleave.Bsq;

        /// <summary>Write the samples most significant byte first ("byte order = 1").</summary>
        public bool BigEndian { get; set; }

        /// <summary>
        /// Write float samples as complex numbers: channel pairs (real, imaginary) become one band of data type 6 (Float32)
        /// or 9 (Float64); a volume needs two channels.
        /// </summary>
        public bool Complex { get; set; }

        /// <summary>
        /// Placement, written as "map info" (and "coordinate system string" from <see cref="GeoCrs.Wkt"/>). A CRS that map
        /// info cannot name (anything but WGS 84 geographic and WGS 84 UTM) needs its WKT, else encoding throws; a
        /// grid whose affine is not a scaled rotation throws too.
        /// </summary>
        public GeoReference? Geo { get; set; }

        public string? Description { get; set; }
        /// <summary>One name per band; names cannot contain commas or braces (the list separators).</summary>
        public string[]? BandNames { get; set; }
        /// <summary>Centre wavelength per band, in <see cref="WavelengthUnits"/>.</summary>
        public double[]? Wavelengths { get; set; }
        /// <summary>"Micrometers", "Nanometers", "Wavenumber", "GHz", "MHz" or "Unknown".</summary>
        public string? WavelengthUnits { get; set; }
        /// <summary>Full width at half maximum per band, in <see cref="WavelengthUnits"/>.</summary>
        public double[]? Fwhm { get; set; }
        /// <summary>"data ignore value": the sample value that marks no data.</summary>
        public double? DataIgnoreValue { get; set; }

        /// <summary>Further fields, written as given after the ones above (a field of the same name replaces it).</summary>
        public List<KeyValuePair<string, string>> ExtraFields { get; } = new();

        internal static readonly EnviEncodeOptions Default = new();
    }

    /// <summary>
    /// ENVI raster files (NV5 / L3Harris ENVI "ENVI Header Files"): a text header "foo.hdr" (or "foo.img.hdr") and the raw
    /// samples in a file next to it ("foo", "foo.img", "foo.dat", ...). Any number of bands, in band sequential, band
    /// interleaved by line or by pixel order, data types 1 (uint8), 2 (int16), 3 (int32), 4 (float), 5 (double), 6 and 9
    /// (complex: two channels per band), 12 (uint16), 13 (uint32), 14 (int64) and 15 (uint64), either byte order, "header
    /// offset" bytes skipped at the start of the sample file (also a header written into the sample file itself), "map
    /// info" placement and the "coordinate system string" WKT.
    /// <para>
    /// <see cref="Decode(string, EnviDecodeOptions?)"/> returns the bands as channels (<see cref="ColorModel.Other"/>
    /// unless there is one band), <see cref="DecodeVolume(string, EnviDecodeOptions?)"/> a grid of samples x lines x bands.
    /// The placement is in <see cref="ImageMetadata.Geo"/> and <see cref="RawVolume.Metadata"/>; nothing is reprojected.
    /// An EPSG code is reported only where the header makes it certain: WGS 84 named explicitly with a UTM zone and
    /// hemisphere or geographic coordinates (ENVI's default datum for UTM is NAD27), or an EPSG authority in the WKT.
    /// </para>
    /// </summary>
    public static class Envi
    {
        const string Fmt = "ENVI";

        /// <summary>Whether the data starts with the "ENVI" line of a header.</summary>
        public static bool IsMatch(ReadOnlySpan<byte> data)
        {
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) data = data.Slice(3);
            if (data.Length < 4 || !data.StartsWith("ENVI"u8)) return false;
            return data.Length == 4 || data[4] is (byte)'\n' or (byte)'\r' or (byte)' ' or (byte)'\t';
        }

        // ------------------------------------------------------------------ the pair of files

        static readonly string[] DataExtensions = { ".img", ".dat", ".raw", ".bin", ".bsq", ".bil", ".bip", ".envi" };

        /// <summary>The ENVI header next to a sample file ("foo.img.hdr" or "foo.hdr"), null when there is none.</summary>
        public static string? FindHeaderFile(string dataPath)
        {
            var candidates = new List<string> { dataPath + ".hdr", dataPath + ".HDR" };
            if (Path.HasExtension(dataPath))
            {
                candidates.Add(Path.ChangeExtension(dataPath, ".hdr"));
                candidates.Add(Path.ChangeExtension(dataPath, ".HDR"));
            }
            foreach (var c in candidates)
                if (!string.Equals(c, dataPath, StringComparison.Ordinal) && File.Exists(c) && StartsWithEnvi(c)) return c;
            return null;
        }

        /// <summary>The sample file of a header ("foo.img.hdr" gives "foo.img"; "foo.hdr" gives "foo", "foo.img", "foo.dat", ...), null when there is none.</summary>
        public static string? FindDataFile(string headerPath)
        {
            string ext = Path.GetExtension(headerPath);
            if (!ext.Equals(".hdr", StringComparison.OrdinalIgnoreCase)) return null;
            string stem = headerPath.Substring(0, headerPath.Length - ext.Length);
            if (File.Exists(stem) && !Directory.Exists(stem)) return stem;
            foreach (var e in DataExtensions)
                foreach (var c in new[] { stem + e, stem + e.ToUpperInvariant() })
                    if (File.Exists(c)) return c;
            return null;
        }

        static bool StartsWithEnvi(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
                Span<byte> b = stackalloc byte[8];
                int n = fs.Read(b);
                return IsMatch(b.Slice(0, n));
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>Whether <paramref name="path"/> is an ENVI header, or a sample file with an ENVI header next to it.</summary>
        public static bool IsEnviFile(string path) => StartsWithEnvi(path) || FindHeaderFile(path) != null;

        // ------------------------------------------------------------------ header

        /// <summary>Reads the header of an ENVI raster; <paramref name="path"/> is the header or the sample file.</summary>
        public static EnviHeader ReadHeader(string path) => Resolve(path, null, false).Header;

        /// <summary>
        /// Reads a header from its bytes. When the bytes go on past the header (a header written into the sample file),
        /// "header offset" says where the text ends.
        /// </summary>
        public static EnviHeader ReadHeader(ReadOnlySpan<byte> data)
        {
            long offset = OffsetOfPrefix(data);
            return EnviHeader.Parse(offset > 0 && offset <= data.Length ? data.Slice(0, (int)offset) : data);
        }

        /// <inheritdoc cref="ReadHeader(ReadOnlySpan{byte})"/>
        public static EnviHeader ReadHeader(Stream stream) => ImageCodec.WithData(stream, d => ReadHeader(d));

        // The "header offset" of a header that may be followed by binary samples: read from the text up to the first NUL
        // byte (which no header text holds), 0 when it says none.
        static long OffsetOfPrefix(ReadOnlySpan<byte> data)
        {
            int nul = data.IndexOf((byte)0);
            var text = EnviHeader.Text(nul < 0 ? data : data.Slice(0, nul));
            foreach (var raw in text.Split('\n'))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                var name = string.Join(' ', raw.Substring(0, eq).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
                if (name == "header offset"
                    && long.TryParse(raw.Substring(eq + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) && v > 0)
                    return v;
            }
            return 0;
        }

        // header path and sample path of a raster given by either of its files
        sealed record Pair(EnviHeader Header, string HeaderPath, string? DataPath);

        static Pair Resolve(string path, EnviDecodeOptions? o, bool needData)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("ENVI: file not found", path);
            string headerPath; string? dataPath;
            if (StartsWithEnvi(path))
            {
                headerPath = path;
                dataPath = o?.DataPath ?? FindDataFile(path);
            }
            else
            {
                headerPath = FindHeaderFile(path) ?? throw new ImageFormatException($"ENVI: no header next to '{path}' (looked for '{path}.hdr' and the same name with .hdr)");
                dataPath = o?.DataPath ?? path;
            }
            EnviHeader h;
            if (dataPath != null) h = EnviHeader.Parse(File.ReadAllBytes(headerPath));
            else
            {
                // no separate sample file: the samples, if any, follow the header in its own file
                using var fs = new FileStream(headerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                var prefix = new byte[(int)Math.Min(fs.Length, 1 << 20)];
                fs.ReadExactly(prefix);
                long offset = OffsetOfPrefix(prefix);
                if (offset > 0 && offset < fs.Length)
                {
                    if (offset > Array.MaxLength) throw new ImageFormatException("ENVI: the header offset is beyond any header text");
                    var head = new byte[offset];
                    fs.Position = 0;
                    fs.ReadExactly(head);
                    h = EnviHeader.Parse(head);
                    dataPath = headerPath;
                }
                else
                {
                    if (fs.Length > 64 << 20) throw new ImageFormatException("ENVI: a 64 MB header without samples is not a header");
                    h = EnviHeader.Parse(File.ReadAllBytes(headerPath));
                    if (needData) throw new ImageFormatException($"ENVI: no sample file next to the header '{headerPath}'");
                }
            }
            return new Pair(h, headerPath, dataPath);
        }

        // ------------------------------------------------------------------ types

        /// <summary>The sample type of an ENVI data type code, and how many values one sample has (2 for complex).</summary>
        public static (SampleType Type, int Parts) SampleTypeOf(int dataType) => dataType switch
        {
            1 => (SampleType.UInt8, 1),
            2 => (SampleType.Int16, 1),
            3 => (SampleType.Int32, 1),
            4 => (SampleType.Float32, 1),
            5 => (SampleType.Float64, 1),
            6 => (SampleType.Float32, 2),
            9 => (SampleType.Float64, 2),
            12 => (SampleType.UInt16, 1),
            13 => (SampleType.UInt32, 1),
            14 => (SampleType.Int64, 1),
            15 => (SampleType.UInt64, 1),
            _ => throw new ImageNotSupportedException($"ENVI: data type {dataType} is not a raster sample type"),
        };

        static int DataTypeOf(SampleType t, bool complex) => (t, complex) switch
        {
            (SampleType.UInt8, false) => 1,
            (SampleType.Int16, false) => 2,
            (SampleType.Int32, false) => 3,
            (SampleType.Float32, false) => 4,
            (SampleType.Float64, false) => 5,
            (SampleType.Float32, true) => 6,
            (SampleType.Float64, true) => 9,
            (SampleType.UInt16, false) => 12,
            (SampleType.UInt32, false) => 13,
            (SampleType.Int64, false) => 14,
            (SampleType.UInt64, false) => 15,
            (_, true) => throw new ImageNotSupportedException($"ENVI: complex samples are Float32 or Float64, not {t}"),
            _ => throw new ImageNotSupportedException($"ENVI has no data type for {t} samples"),
        };

        static ColorModel ColorOf(int channels) => channels == 1 ? ColorModel.Gray : ColorModel.Other;

        // ------------------------------------------------------------------ info

        /// <summary>Size, channels (bands, twice that for complex bands) and sample type; <paramref name="path"/> is either file.</summary>
        public static ImageInfo GetInfo(string path) => Info(ReadHeader(path));

        /// <summary>The same from the header's bytes.</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> header) => Info(ReadHeader(header));

        static ImageInfo Info(EnviHeader h)
        {
            var (t, parts) = SampleTypeOf(h.DataType);
            int ch = Channels(h, parts);
            return new ImageInfo(ImageFormat.Envi, h.Samples, h.Lines, ch, t, ColorOf(ch)) { BitsPerSample = RawImage.BytesPerSample(t) * 8 };
        }

        static int Channels(EnviHeader h, int parts)
        {
            long ch = (long)h.Bands * parts;
            if (ch > int.MaxValue) throw new ImageNotSupportedException("ENVI: too many bands");
            return (int)ch;
        }

        // ------------------------------------------------------------------ decode

        /// <summary>Decodes an ENVI raster with its bands as channels; <paramref name="path"/> is the header or the sample file.</summary>
        public static RawImage Decode(string path, EnviDecodeOptions? options = null)
        {
            var o = options ?? EnviDecodeOptions.Default;
            var p = Resolve(path, o, true);
            using var s = new FileStream(p.DataPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            return Decode(p.Header, s, o);
        }

        /// <summary>Decodes the samples of <paramref name="header"/> from <paramref name="samples"/>, positioned at the start of the sample file.</summary>
        public static RawImage Decode(EnviHeader header, Stream samples, EnviDecodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(header);
            ArgumentNullException.ThrowIfNull(samples);
            var o = options ?? EnviDecodeOptions.Default;
            var (t, parts) = SampleTypeOf(header.DataType);
            int ch = Channels(header, parts);
            var data = Read(header, samples, o, false);
            return new RawImage(header.Samples, header.Lines, ColorOf(ch), data, ch, Metadata(header));
        }

        /// <summary>
        /// Decodes a header followed by its samples in one block of bytes (a header written into its sample file, with a
        /// "header offset" that covers it).
        /// </summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, EnviDecodeOptions? options = null)
        {
            var h = Attached(data);
            return VolumeIO.WithStream(data, s => Decode(h, s, options));
        }

        /// <summary>Decodes an ENVI raster as a grid of samples x lines x bands (two channels for complex bands).</summary>
        public static RawVolume DecodeVolume(string path, EnviDecodeOptions? options = null)
        {
            var o = options ?? EnviDecodeOptions.Default;
            var p = Resolve(path, o, true);
            using var s = new FileStream(p.DataPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            return DecodeVolume(p.Header, s, o);
        }

        /// <inheritdoc cref="DecodeVolume(string, EnviDecodeOptions?)"/>
        public static RawVolume DecodeVolume(EnviHeader header, Stream samples, EnviDecodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(header);
            ArgumentNullException.ThrowIfNull(samples);
            var o = options ?? EnviDecodeOptions.Default;
            var (_, parts) = SampleTypeOf(header.DataType);
            var data = Read(header, samples, o, true);
            return new RawVolume(new[] { header.Samples, header.Lines, header.Bands }, parts == 1 ? ColorModel.Gray : ColorModel.Other, data, parts,
                Physical(header, ReadGeo(header), true));
        }

        /// <inheritdoc cref="Decode(ReadOnlySpan{byte}, EnviDecodeOptions?)"/>
        public static RawVolume DecodeVolume(ReadOnlySpan<byte> data, EnviDecodeOptions? options = null)
        {
            var h = Attached(data);
            return VolumeIO.WithStream(data, s => DecodeVolume(h, s, options));
        }

        static EnviHeader Attached(ReadOnlySpan<byte> data)
        {
            var h = ReadHeader(data);
            if (h.HeaderOffset <= 0 || h.HeaderOffset >= data.Length)
                throw new ImageFormatException("ENVI: these bytes are a header without samples; decode from the file path (the samples are in the file next to it)");
            return h;
        }

        // Samples into one array: channel fastest (pixel interleaved) for an image, band planes for a volume. The file is read
        // in batches of whole file rows; each row is copied or spread into place, so nothing but the result is image sized.
        static Array Read(EnviHeader h, Stream s, EnviDecodeOptions o, bool planes)
        {
            var (t, parts) = SampleTypeOf(h.DataType);
            int bps = RawImage.BytesPerSample(t);
            long total = (long)h.Samples * h.Lines * h.Bands * parts;
            var dst = VolumeIO.Allocate(t, total, Math.Min(o.MaxSamples, Array.MaxLength), Fmt);
            long bytes = total * bps;
            if (s.CanSeek)
            {
                long have = s.Length - s.Position - h.HeaderOffset;
                if (have < bytes)
                    throw new ImageFormatException($"ENVI: the sample file holds {Math.Max(0, have)} bytes after the header offset, the header describes {bytes}");
                s.Seek(h.HeaderOffset, SeekOrigin.Current);
            }
            else Skip(s, h.HeaderOffset);
            var g = new Grid(h.Samples, h.Lines, h.Bands, h.Interleave, planes);
            bool swap = h.BigEndian != !BitConverter.IsLittleEndian && bps > 1;
            try
            {
                switch (bps * parts)
                {
                    case 1: Rows<byte>(s, dst, g, bps, swap, true); break;
                    case 2: Rows<ushort>(s, dst, g, bps, swap, true); break;
                    case 4: Rows<uint>(s, dst, g, bps, swap, true); break;
                    case 8: Rows<ulong>(s, dst, g, bps, swap, true); break;
                    default: Rows<Int128>(s, dst, g, bps, swap, true); break;
                }
            }
            catch (EndOfStreamException) { throw new ImageFormatException($"ENVI: the samples end before the {bytes} bytes the header describes"); }
            return dst;
        }

        static void Skip(Stream s, long n)
        {
            var b = new byte[(int)Math.Min(n, 1 << 16)];
            while (n > 0)
            {
                int r = s.Read(b, 0, (int)Math.Min(n, b.Length));
                if (r <= 0) throw new ImageFormatException("ENVI: the sample file ends inside its header offset");
                n -= r;
            }
        }

        /// <summary>
        /// The order of the samples in the file and in memory. A file row is a run of samples that are adjacent in the file:
        /// one line of one band (BSQ, BIL) or one line of every band (BIP). Memory is pixel interleaved (an image) or band
        /// planes (a volume).
        /// </summary>
        readonly record struct Grid(int W, int H, int Bands, EnviInterleave File, bool Planes)
        {
            public long Rows => File == EnviInterleave.Bip ? H : (long)H * Bands;
            public int RowLength => File == EnviInterleave.Bip ? checked(W * Bands) : W;
        }

        static Span<U> Units<U>(Array a, long count) where U : unmanaged
            => MemoryMarshal.CreateSpan(ref Unsafe.As<byte, U>(ref MemoryMarshal.GetArrayDataReference(a)), checked((int)count));

        // copies between the file rows of a stream and the array, in batches of whole rows (read: file to array)
        static void Rows<U>(Stream s, Array arr, Grid g, int bps, bool swap, bool read) where U : unmanaged
        {
            int unit = Unsafe.SizeOf<U>();
            var mem = Units<U>(arr, (long)g.W * g.H * g.Bands);
            int rowLen = g.RowLength;
            long rowBytesL = (long)rowLen * unit;
            if (rowBytesL > Array.MaxLength) throw new ImageNotSupportedException("ENVI: one line of the raster is larger than an array");
            int rowBytes = (int)rowBytesL;
            int batch = (int)Math.Clamp((4L << 20) / Math.Max(1, rowBytes), 1, g.Rows);
            var buf = new byte[(long)batch * rowBytes];
            long w = g.W, h = g.H, nb = g.Bands;
            for (long r0 = 0; r0 < g.Rows; r0 += batch)
            {
                int n = (int)Math.Min(batch, g.Rows - r0);
                var bytes = buf.AsSpan(0, n * rowBytes);
                if (read)
                {
                    s.ReadExactly(bytes);
                    if (swap) VolumeIO.Swap(bytes, bps);
                }
                var rows = MemoryMarshal.Cast<byte, U>(bytes);
                for (int i = 0; i < n; i++)
                {
                    long r = r0 + i;
                    var row = rows.Slice(i * rowLen, rowLen);
                    if (g.File == EnviInterleave.Bip)
                    {
                        long y = r;
                        if (!g.Planes) Copy(row, mem.Slice((int)(y * w * nb), rowLen), read);
                        else
                            for (int x = 0; x < w; x++)
                                for (int b = 0; b < nb; b++)
                                {
                                    ref U m = ref mem[(int)((b * h + y) * w + x)];
                                    if (read) m = row[(int)(x * nb + b)]; else row[(int)(x * nb + b)] = m;
                                }
                    }
                    else
                    {
                        long b = g.File == EnviInterleave.Bsq ? r / h : r % nb;
                        long y = g.File == EnviInterleave.Bsq ? r % h : r / nb;
                        if (g.Planes) Copy(row, mem.Slice((int)((b * h + y) * w), (int)w), read);
                        else
                        {
                            int at = (int)(y * w * nb + b), step = (int)nb;
                            if (read) for (int x = 0; x < row.Length; x++, at += step) mem[at] = row[x];
                            else for (int x = 0; x < row.Length; x++, at += step) row[x] = mem[at];
                        }
                    }
                }
                if (!read)
                {
                    if (swap) VolumeIO.Swap(bytes, bps);
                    s.Write(bytes);
                }
            }
        }

        static void Copy<U>(Span<U> row, Span<U> mem, bool read) where U : unmanaged
        {
            if (read) row.CopyTo(mem); else mem.CopyTo(row);
        }

        // ------------------------------------------------------------------ placement

        /// <summary>The placement the header gives ("map info", "coordinate system string"), null when it gives none.</summary>
        public static GeoReference? ReadGeo(string path) => ReadGeo(ReadHeader(path));

        /// <inheritdoc cref="ReadGeo(string)"/>
        public static GeoReference? ReadGeo(EnviHeader header) => EnviGeo.Read(header);

        static ImageMetadata Metadata(EnviHeader h)
        {
            var geo = ReadGeo(h);
            return new ImageMetadata { Geo = geo, Physical = Physical(h, geo, false) };
        }

        static PhysicalMetadata Physical(EnviHeader h, GeoReference? geo, bool volume)
        {
            var m = new PhysicalMetadata { SourceFormat = ImageFormat.Envi, Blank = h.DataIgnoreValue };
            foreach (var (k, v) in h.Fields) m.Header.Add(new KeyValuePair<string, string>(k, v));
            int axes = volume ? 3 : 2;
            if (geo == null)
            {
                m.Spacing = volume ? new[] { double.NaN, double.NaN, double.NaN } : new[] { double.NaN, double.NaN };
                m.Units = new string?[axes];
                return m;
            }
            var a = geo.Affine;
            double sx = Math.Sqrt(a[0] * a[0] + a[3] * a[3]), sy = Math.Sqrt(a[1] * a[1] + a[4] * a[4]);
            m.Spacing = volume ? new[] { sx, sy, double.NaN } : new[] { sx, sy };
            m.Origin = new[] { a[2], a[5], 0.0 };
            var d = new double[3, axes];
            d[0, 0] = sx == 0 ? 1 : a[0] / sx; d[1, 0] = sx == 0 ? 0 : a[3] / sx;
            d[0, 1] = sy == 0 ? 0 : a[1] / sy; d[1, 1] = sy == 0 ? 1 : a[4] / sy;
            if (volume) d[2, 2] = 1;
            m.Direction = d;
            int unit = geo.ModelType == GeoModelType.Geographic ? geo.Crs?.AngularUnit ?? 0 : geo.Crs?.LinearUnit ?? 0;
            var un = unit == 0 ? null : GeoUnits.Name(unit);
            m.Units = volume ? new[] { un, un, null } : new[] { un, un };
            m.Space = geo.Crs?.Epsg is int e ? "EPSG:" + e.ToString(CultureInfo.InvariantCulture) : null;
            return m;
        }

        // ------------------------------------------------------------------ encode

        /// <summary>
        /// Writes <paramref name="image"/> to <paramref name="dataPath"/> ("foo.img", or "foo") with its channels as bands,
        /// and the header next to it ("foo.hdr").
        /// </summary>
        public static void Encode(RawImage image, string dataPath, EnviEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            var hp = HeaderPathFor(dataPath);
            var h = Header(image.Width, image.Height, image.Channels, image.SampleType, options ?? EnviEncodeOptions.Default);
            using (var s = new FileStream(dataPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16)) Write(h, image.Data, s, false);
            File.WriteAllBytes(hp, Encoding.UTF8.GetBytes(h.ToText()));
        }

        /// <summary>Writes the samples to one stream and the header text (UTF-8) to another.</summary>
        public static void Encode(RawImage image, Stream samples, Stream header, EnviEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(samples);
            ArgumentNullException.ThrowIfNull(header);
            var h = Header(image.Width, image.Height, image.Channels, image.SampleType, options ?? EnviEncodeOptions.Default);
            Write(h, image.Data, samples, false);
            header.Write(Encoding.UTF8.GetBytes(h.ToText()));
        }

        /// <summary>Writes a grid of samples x lines x bands (one channel, two for complex) and its header next to it.</summary>
        public static void EncodeVolume(RawVolume volume, string dataPath, EnviEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(volume);
            var hp = HeaderPathFor(dataPath);
            var h = VolumeHeader(volume, options ?? EnviEncodeOptions.Default);
            using (var s = new FileStream(dataPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16)) Write(h, volume.Data, s, true);
            File.WriteAllBytes(hp, Encoding.UTF8.GetBytes(h.ToText()));
        }

        /// <inheritdoc cref="EncodeVolume(RawVolume, string, EnviEncodeOptions?)"/>
        public static void EncodeVolume(RawVolume volume, Stream samples, Stream header, EnviEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(volume);
            ArgumentNullException.ThrowIfNull(samples);
            ArgumentNullException.ThrowIfNull(header);
            var h = VolumeHeader(volume, options ?? EnviEncodeOptions.Default);
            Write(h, volume.Data, samples, true);
            header.Write(Encoding.UTF8.GetBytes(h.ToText()));
        }

        /// <summary>The header path that belongs to a sample path: "foo.img" gives "foo.hdr", "foo" gives "foo.hdr".</summary>
        public static string HeaderPathFor(string dataPath)
        {
            if (Path.GetExtension(dataPath).Equals(".hdr", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("ENVI: give the path of the sample file; the header is written next to it", nameof(dataPath));
            return Path.HasExtension(dataPath) ? Path.ChangeExtension(dataPath, ".hdr") : dataPath + ".hdr";
        }

        static EnviHeader VolumeHeader(RawVolume v, EnviEncodeOptions o)
        {
            if (v.Sizes.Length > 3) throw new ImageNotSupportedException("ENVI: a grid has at most three axes (samples, lines, bands)");
            int parts = o.Complex ? 2 : 1;
            if (v.Channels != parts)
                throw new ImageNotSupportedException($"ENVI: a grid needs {parts} channel(s) per sample{(o.Complex ? " (real, imaginary)" : "; its bands are the third axis")}, not {v.Channels}");
            int bands = v.Sizes.Length > 2 ? v.Sizes[2] : 1;
            return Header(v.Sizes[0], v.Sizes.Length > 1 ? v.Sizes[1] : 1, bands * parts, v.SampleType, o);
        }

        static EnviHeader Header(int w, int hgt, int channels, SampleType t, EnviEncodeOptions o)
        {
            int parts = o.Complex ? 2 : 1;
            if (channels % parts != 0) throw new ImageNotSupportedException("ENVI: complex bands need an even number of channels (real, imaginary pairs)");
            int bands = channels / parts;
            var h = new EnviHeader
            {
                Samples = w, Lines = hgt, Bands = bands, DataType = DataTypeOf(t, o.Complex),
                Interleave = o.Interleave, BigEndian = o.BigEndian,
            };
            if (o.Description != null) h.Set("description", o.Description);
            h.Set("file type", "ENVI Standard");
            if (o.Geo != null)
            {
                var (mapInfo, wkt) = EnviGeo.Write(o.Geo);
                h.Set("map info", mapInfo);
                if (wkt != null) h.Set("coordinate system string", wkt);
            }
            if (o.BandNames != null)
            {
                if (o.BandNames.Length != bands) throw new ArgumentException($"ENVI: {o.BandNames.Length} band names for {bands} bands");
                foreach (var n in o.BandNames)
                    if (n == null || n.IndexOfAny(new[] { ',', '{', '}', '\n', '\r' }) >= 0 || n.Trim().Length == 0)
                        throw new ArgumentException($"ENVI: the band name '{n}' cannot be written: names are a comma separated list in braces, so they cannot be empty or hold commas, braces or line breaks");
                h.Set("band names", string.Join(", ", o.BandNames));
            }
            if (o.WavelengthUnits != null) h.Set("wavelength units", o.WavelengthUnits);
            if (o.Wavelengths != null) h.Set("wavelength", PerBand(o.Wavelengths, bands, "wavelengths"));
            if (o.Fwhm != null) h.Set("fwhm", PerBand(o.Fwhm, bands, "fwhm values"));
            if (o.DataIgnoreValue is double d) h.Set("data ignore value", Num(d));
            foreach (var (k, v) in o.ExtraFields) h.Set(k, v);
            return h;
        }

        static string PerBand(double[] v, int bands, string what)
        {
            if (v.Length != bands) throw new ArgumentException($"ENVI: {v.Length} {what} for {bands} bands");
            return string.Join(", ", Array.ConvertAll(v, Num));
        }

        internal static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        static void Write(EnviHeader h, Array data, Stream s, bool planes)
        {
            var (t, parts) = SampleTypeOf(h.DataType);
            int bps = RawImage.BytesPerSample(t);
            var g = new Grid(h.Samples, h.Lines, h.Bands, h.Interleave, planes);
            bool swap = h.BigEndian != !BitConverter.IsLittleEndian && bps > 1;
            switch (bps * parts)
            {
                case 1: Rows<byte>(s, data, g, bps, swap, false); break;
                case 2: Rows<ushort>(s, data, g, bps, swap, false); break;
                case 4: Rows<uint>(s, data, g, bps, swap, false); break;
                case 8: Rows<ulong>(s, data, g, bps, swap, false); break;
                default: Rows<Int128>(s, data, g, bps, swap, false); break;
            }
        }
    }
}
