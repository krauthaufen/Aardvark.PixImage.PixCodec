using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace Aardvark.ImageCodecs
{
    /// <summary>Reads the DNG tags of a TIFF structure into a <see cref="DngInfo"/> (DNG 1.7.1 chapters 3 and 4).</summary>
    internal static class DngReader
    {
        public const int TagDngVersion = 50706;

        /// <summary>A directory of the file: where it sits and its entries.</summary>
        internal readonly record struct Dir(long Offset, int ImageIndex, int[] Path, TiffEntry[] Entries);

        public static bool IsDng(TiffFile f)
        {
            try { return TiffFile.Find(f.ReadIfd(f.FirstIfd), TagDngVersion, out _); }
            catch (ImageFormatException) { return false; }
        }

        /// <summary>Every directory: the main chain and the SubIFDs below each (two levels), in file order.</summary>
        public static List<Dir> Directories(TiffFile f)
        {
            var list = new List<Dir>();
            var seen = new HashSet<long>();
            var chain = f.IfdOffsets(64);
            for (int i = 0; i < chain.Count; i++) Walk(f, chain[i], i, Array.Empty<int>(), 0, list, seen);
            return list;
        }

        static void Walk(TiffFile f, long off, int index, int[] path, int depth, List<Dir> list, HashSet<long> seen)
        {
            if (!seen.Add(off)) return;
            TiffEntry[] e;
            try { e = f.ReadIfd(off); }
            catch (ImageFormatException) { return; }
            list.Add(new Dir(off, index, path, e));
            if (depth >= 2) return;
            long[]? subs;
            try { subs = f.Integers(e, 330); }
            catch (ImageFormatException) { return; }
            if (subs == null) return;
            for (int k = 0; k < subs.Length && k < 64; k++)
                if (subs[k] >= 8 && subs[k] < f.Src.Length) Walk(f, subs[k], index, path.Append(k).ToArray(), depth + 1, list, seen);
        }

        static string? Text(TiffFile f, TiffEntry[] e, int tag)
        {
            if (!TiffFile.Find(e, tag, out var en)) return null;
            try
            {
                var v = f.ReadValue(en);
                var s = v switch
                {
                    string str => str,
                    byte[] b => System.Text.Encoding.UTF8.GetString(b).TrimEnd('\0'),
                    _ => null,
                };
                return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            }
            catch (ImageFormatException) { return null; }
        }

        static double[]? D(TiffFile f, TiffEntry[] e, int tag)
        {
            try { return f.Doubles(e, tag); }
            catch (ImageFormatException) { return null; }
        }

        static double[]? Matrix(TiffFile f, TiffEntry[] e, int tag, int len)
        {
            var v = D(f, e, tag);
            return v != null && v.Length >= len ? v.Take(len).ToArray() : null;
        }

        /// <summary>Hue / saturation / value divisions of a table (hue &gt;= 1, saturation &gt;= 2, value &gt;= 1), null when absent or invalid.</summary>
        static int[]? Dims(TiffFile f, TiffEntry[] e, int tag)
        {
            long[]? d;
            try { d = f.Integers(e, tag); }
            catch (ImageFormatException) { return null; }
            if (d == null || d.Length < 3 || d[0] < 1 || d[1] < 2 || d[2] < 1 || d[0] * d[1] * d[2] > 1 << 20) return null;
            return new[] { (int)d[0], (int)d[1], (int)d[2] };
        }

        static float[]? Floats(TiffFile f, TiffEntry[] e, int tag, int len)
        {
            var v = D(f, e, tag);
            return v != null && v.Length >= len ? v.Take(len).Select(x => (float)x).ToArray() : null;
        }

        static (double, double)? IlluminantXY(TiffFile f, TiffEntry[] e, int tag)
        {
            var b = f.Bytes(e, tag);
            if (b == null || b.Length < 18) return null;
            ushort type = f.U16(b);
            if (type != 0) return null;   // spectral data: needs the CIE observer, not supported
            double R(int o) { uint n = f.U32(b.AsSpan(o)), d = f.U32(b.AsSpan(o + 4)); return d == 0 ? double.NaN : (double)n / d; }
            double x = R(2), y = R(10);
            return x > 0 && y > 0 && x + y < 1 ? (x, y) : null;
        }

        public static IReadOnlyList<DngOpcode> Opcodes(byte[]? list)
        {
            if (list == null || list.Length < 4) return Array.Empty<DngOpcode>();
            var r = new List<DngOpcode>();
            uint count = BinaryPrimitives.ReadUInt32BigEndian(list);
            int p = 4;
            for (uint i = 0; i < count; i++)
            {
                if (p + 16 > list.Length) throw new ImageFormatException("DNG: truncated opcode list");
                int id = (int)BinaryPrimitives.ReadUInt32BigEndian(list.AsSpan(p));
                uint ver = BinaryPrimitives.ReadUInt32BigEndian(list.AsSpan(p + 4));
                uint flags = BinaryPrimitives.ReadUInt32BigEndian(list.AsSpan(p + 8));
                uint size = BinaryPrimitives.ReadUInt32BigEndian(list.AsSpan(p + 12));
                p += 16;
                if (size > list.Length - p) throw new ImageFormatException("DNG: truncated opcode list");
                r.Add(new DngOpcode
                {
                    Id = id, Name = DngOpcodes.NameOf(id), Version = ver,
                    Optional = (flags & 1) != 0, PreviewSkippable = (flags & 2) != 0,
                    Parameters = list.AsSpan(p, (int)size).ToArray(),
                });
                p += (int)size;
            }
            return r;
        }

        static (int X, int Y) Trimmed(int w, int h, IReadOnlyList<DngOpcode> l2, IReadOnlyList<DngOpcode> l3)
        {
            var (x, y, _, _) = DngOpcodes.TrimmedArea(w, h, l2, l3);
            return (x, y);
        }

        static bool IsPreview(TiffFile f, TiffEntry[] e)
        {
            long sub = f.Integer(e, 254, 0), pm = f.Integer(e, 262, -1);
            return (sub == 1 || sub == 0x10001) && pm is 1 or 2 or 6;
        }

        public static DngInfo Read(TiffFile f, out long rawOffset)
        {
            var dirs = Directories(f);
            if (dirs.Count == 0) throw new ImageFormatException("DNG: no image directory");
            var ifd0 = dirs[0].Entries;
            if (!TiffFile.Find(ifd0, TagDngVersion, out _)) throw new ImageFormatException("DNG: DNGVersion tag missing (not a DNG file)");

            // the raw directory: full resolution (NewSubFileType 0) CFA or LinearRaw data, IFD 0 or a SubIFD
            Dir? raw = null;
            foreach (var d in dirs)
            {
                long pm = f.Integer(d.Entries, 262, -1), sub = f.Integer(d.Entries, 254, 0);
                if (sub == 0 && pm is 32803 or 34892) { raw = d; break; }
            }
            if (raw == null) throw new ImageNotSupportedException("DNG: no raw image directory (CFA or LinearRaw with NewSubFileType 0)");
            var r = raw.Value;
            var e = r.Entries;
            rawOffset = r.Offset;

            int w = (int)Math.Clamp(f.Integer(e, 256, 0), 0, int.MaxValue), h = (int)Math.Clamp(f.Integer(e, 257, 0), 0, int.MaxValue);
            if (w <= 0 || h <= 0) throw new ImageFormatException("DNG: invalid raw image size");
            int spp = (int)Math.Clamp(f.Integer(e, 277, 1), 1, 16);
            var bitsArr = f.Integers(e, 258);
            int bits = bitsArr != null && bitsArr.Length > 0 ? (int)bitsArr[0] : 1;
            bool isFloat = f.Integer(e, 339, 1) == 3;
            bool cfa = f.Integer(e, 262, 0) == 32803;

            var aa = f.Integers(e, 50829);
            var active = aa != null && aa.Length >= 4 && aa[0] >= 0 && aa[1] >= 0 && aa[2] > aa[0] && aa[3] > aa[1] && aa[2] <= h && aa[3] <= w
                ? new DngRect((int)aa[0], (int)aa[1], (int)aa[2], (int)aa[3]) : new DngRect(0, 0, h, w);
            var masked = new List<DngRect>();
            var ma = f.Integers(e, 50830);
            if (ma != null)
                for (int i = 0; i + 3 < ma.Length; i += 4) masked.Add(new DngRect((int)ma[i], (int)ma[i + 1], (int)ma[i + 2], (int)ma[i + 3]));

            var co = D(f, e, 50719); var cs = D(f, e, 50720);
            double cx = co != null && co.Length >= 2 ? co[0] : 0, cy = co != null && co.Length >= 2 ? co[1] : 0;
            cx = Math.Clamp(cx, 0, active.Width - 1); cy = Math.Clamp(cy, 0, active.Height - 1);
            double cw = cs != null && cs.Length >= 2 && cs[0] > 0 ? cs[0] : active.Width, ch = cs != null && cs.Length >= 2 && cs[1] > 0 ? cs[1] : active.Height;
            cw = Math.Min(cw, active.Width - cx); ch = Math.Min(ch, active.Height - cy);
            var ds = D(f, e, 50718);
            double sh = ds != null && ds.Length >= 2 && ds[0] > 0 ? ds[0] : 1, sv = ds != null && ds.Length >= 2 && ds[1] > 0 ? ds[1] : 1;

            // CFA
            int rr = 0, rc = 0;
            byte[] pattern = Array.Empty<byte>();
            byte[] planeColor = f.Bytes(e, 50710) ?? new byte[] { 0, 1, 2 };
            int colorPlanes;
            if (cfa)
            {
                var dim = f.Integers(e, 33421);
                pattern = f.Bytes(e, 33422) ?? Array.Empty<byte>();
                if (dim == null || dim.Length < 2 || dim[0] <= 0 || dim[1] <= 0 || dim[0] * dim[1] > 256 || pattern.Length < dim[0] * dim[1])
                    throw new ImageFormatException("DNG: CFA image without a valid CFARepeatPatternDim / CFAPattern");
                rr = (int)dim[0]; rc = (int)dim[1];
                pattern = pattern.Take(rr * rc).ToArray();
                colorPlanes = planeColor.Length;
            }
            else colorPlanes = spp;

            // black / white
            var bdim = f.Integers(e, 50713);
            int brr = bdim != null && bdim.Length >= 2 && bdim[0] > 0 ? (int)Math.Min(bdim[0], 256) : 1, brc = bdim != null && bdim.Length >= 2 && bdim[1] > 0 ? (int)Math.Min(bdim[1], 256) : 1;
            var black = D(f, e, 50714) ?? new double[] { 0 };
            var white = D(f, e, 50717);
            double defWhite = isFloat ? 1 : bits >= 32 ? uint.MaxValue : (1L << bits) - 1;
            var whites = new double[spp];
            for (int i = 0; i < spp; i++) whites[i] = white != null && white.Length > 0 ? white[Math.Min(i, white.Length - 1)] : defWhite;
            var lin = f.Integers(e, 50712);

            // colour calibrations (camera profile in IFD 0)
            var cals = new List<DngCalibration>();
            int[] illTags = { 50778, 50779, 52529 }, cmTags = { 50721, 50722, 52531 }, ccTags = { 50723, 50724, 52530 },
                  rmTags = { 50725, 50726, 52538 }, fmTags = { 50964, 50965, 52532 }, idTags = { 52533, 52534, 52535 },
                  hsTags = { 50938, 50939, 52537 };
            var hsDims = Dims(f, ifd0, 50937);
            int hsLen = hsDims == null ? 0 : hsDims[0] * hsDims[1] * hsDims[2] * 3;
            for (int k = 0; k < 3; k++)
            {
                var cm = Matrix(f, ifd0, cmTags[k], colorPlanes * 3);
                if (cm == null) continue;
                int ill = (int)f.Integer(ifd0, illTags[k], 0);
                var ixy = ill == 255 ? IlluminantXY(f, ifd0, idTags[k]) : null;
                double t = ixy is { } p ? DngColor.Temperature(p.Item1, p.Item2) : DngColor.IlluminantTemperature(ill);
                cals.Add(new DngCalibration
                {
                    Illuminant = ill, IlluminantXY = ixy, Temperature = t, ColorMatrix = cm,
                    CameraCalibration = Matrix(f, ifd0, ccTags[k], colorPlanes * colorPlanes),
                    ReductionMatrix = colorPlanes > 3 ? Matrix(f, ifd0, rmTags[k], 3 * colorPlanes) : null,
                    ForwardMatrix = Matrix(f, ifd0, fmTags[k], 3 * colorPlanes),
                    HueSatMap = hsLen > 0 ? Floats(f, ifd0, hsTags[k], hsLen) : null,
                });
            }
            var asn = D(f, ifd0, 50728);
            var awxy = D(f, ifd0, 50729);
            var ab = D(f, ifd0, 50727);

            var previews = new List<DngPreviewInfo>();
            foreach (var d in dirs)
            {
                if (d.Offset == r.Offset || !IsPreview(f, d.Entries)) continue;
                var pe = d.Entries;
                int comp = (int)f.Integer(pe, 259, 1);
                long jo = 0, jl = 0;
                bool jpeg = false;
                try
                {
                    var offs = f.Integers(pe, 273) ?? f.Integers(pe, 324);
                    var cnts = f.Integers(pe, 279) ?? f.Integers(pe, 325);
                    if (comp is 7 or 34892 && offs is { Length: 1 } && cnts is { Length: 1 } && !TiffFile.Find(pe, 347, out _)) { jo = offs[0]; jl = cnts[0]; }
                    else if (comp == 6 && f.Integer(pe, 513, 0) > 0) { jo = f.Integer(pe, 513, 0); jl = f.Integer(pe, 514, 0); }
                    if (jo > 0 && jl > 4 && jo + jl <= f.Src.Length)
                    {
                        var sig = new byte[2];
                        f.Src.Read(jo, sig);
                        jpeg = sig[0] == 0xFF && sig[1] == 0xD8;
                    }
                }
                catch (ImageFormatException) { }
                previews.Add(new DngPreviewInfo
                {
                    ImageIndex = d.ImageIndex, SubImagePath = d.Path,
                    Width = (int)Math.Clamp(f.Integer(pe, 256, 0), 0, int.MaxValue), Height = (int)Math.Clamp(f.Integer(pe, 257, 0), 0, int.MaxValue),
                    SubfileType = (int)f.Integer(pe, 254, 0), Compression = comp, Photometric = (int)f.Integer(pe, 262, 0),
                    ColorSpace = (int)f.Integer(pe, 50970, f.Integer(pe, 262, 0) == 1 ? 1 : 2),
                    IsJpegFile = jpeg, JpegOffset = jpeg ? jo : 0, JpegLength = jpeg ? jl : 0, Offset = d.Offset,
                });
            }
            previews.Sort((a, b) => ((long)a.Width * a.Height).CompareTo((long)b.Width * b.Height));

            var stb = f.Integers(e, 50974);
            long orient = f.Integer(ifd0, 274, 1);
            byte[] B4(byte[]? b) => b != null && b.Length >= 4 ? b.Take(4).ToArray() : Array.Empty<byte>();
            var ver = B4(f.Bytes(ifd0, TagDngVersion));
            var bver = f.Bytes(ifd0, 50707) is { } bv ? B4(bv) : ver.Length == 4 ? new byte[] { ver[0], ver[1], 0, 0 } : Array.Empty<byte>();
            var dh = D(f, e, 50715); var dv = D(f, e, 50716);
            var ops1 = Opcodes(f.Bytes(e, 51008)); var ops2 = Opcodes(f.Bytes(e, 51009)); var ops3 = Opcodes(f.Bytes(e, 51022));
            var (_, _, tw, th) = DngOpcodes.TrimmedArea(active.Width, active.Height, ops2, ops3);
            var crop = DngOpcodes.CropRect(cx, cy, cw, ch, Trimmed(active.Width, active.Height, ops2, ops3), tw, th);
            return new DngInfo
            {
                Version = ver, BackwardVersion = bver,
                Make = Text(f, ifd0, 271), Model = Text(f, ifd0, 272), Software = Text(f, ifd0, 305),
                UniqueCameraModel = Text(f, ifd0, 50708), LocalizedCameraModel = Text(f, ifd0, 50709), CameraSerialNumber = Text(f, ifd0, 50735),
                RawImageIndex = r.ImageIndex, RawSubImagePath = r.Path,
                Layout = cfa ? DngRawLayout.ColorFilterArray : DngRawLayout.LinearRaw,
                RawWidth = w, RawHeight = h, SamplesPerPixel = spp, BitsPerSample = bits, IsFloat = isFloat,
                Compression = (int)f.Integer(e, 259, 1),
                ActiveArea = active, MaskedAreas = masked.ToArray(),
                DefaultCropX = cx, DefaultCropY = cy, DefaultCropWidth = cw, DefaultCropHeight = ch,
                DefaultScaleH = sh, DefaultScaleV = sv,
                BestQualityScale = D(f, e, 50780) is { Length: > 0 } bq && bq[0] > 0 ? bq[0] : 1,
                DefaultUserCrop = D(f, e, 51125) is { Length: >= 4 } uc ? uc.Take(4).ToArray() : null,
                FinalWidth = Math.Max(1, (int)Math.Round(crop.W * sh)), FinalHeight = Math.Max(1, (int)Math.Round(crop.H * sv)),
                Orientation = orient is >= 1 and <= 8 ? (int)orient : 1,
                CfaRepeatRows = rr, CfaRepeatCols = rc, CfaPattern = pattern, CfaPlaneColor = planeColor,
                CfaLayout = (int)f.Integer(e, 50711, 1), ColorPlanes = colorPlanes,
                LinearizationTable = lin != null && lin.Length > 0 ? Array.ConvertAll(lin, x => (ushort)Math.Clamp(x, 0, 65535)) : null,
                BlackLevelRepeatRows = brr, BlackLevelRepeatCols = brc, BlackLevel = black,
                BlackLevelDeltaH = dh is { Length: > 0 } ? dh : null, BlackLevelDeltaV = dv is { Length: > 0 } ? dv : null,
                WhiteLevel = whites,
                RowInterleaveFactor = (int)Math.Clamp(f.Integer(e, 50975, 1), 1, 1 << 16),
                ColumnInterleaveFactor = (int)Math.Clamp(f.Integer(e, 52547, 1), 1, 1 << 16),
                SubTileBlockRows = stb is { Length: >= 2 } ? (int)Math.Max(1, stb[0]) : 1,
                SubTileBlockCols = stb is { Length: >= 2 } ? (int)Math.Max(1, stb[1]) : 1,
                Calibrations = cals,
                AnalogBalance = ab is { Length: > 0 } ? ab : null,
                AsShotNeutral = asn != null && asn.Length >= colorPlanes && asn.Take(colorPlanes).All(x => x > 0) ? asn.Take(colorPlanes).ToArray() : null,
                AsShotWhiteXY = awxy is { Length: >= 2 } && awxy[0] > 0 && awxy[1] > 0 ? (awxy[0], awxy[1]) : null,
                BaselineExposure = D(f, ifd0, 50730) is { Length: > 0 } be ? be[0] : 0,
                BaselineExposureOffset = D(f, ifd0, 51109) is { Length: > 0 } bo ? bo[0] : 0,
                BaselineNoise = D(f, ifd0, 50731) is { Length: > 0 } bn ? bn[0] : 1,
                BaselineSharpness = D(f, ifd0, 50732) is { Length: > 0 } bs ? bs[0] : 1,
                LinearResponseLimit = D(f, ifd0, 50734) is { Length: > 0 } lr ? lr[0] : 1,
                NoiseProfile = D(f, e, 51041) ?? D(f, ifd0, 51041),
                CameraCalibrationSignature = Text(f, ifd0, 50931), ProfileCalibrationSignature = Text(f, ifd0, 50932),
                ProfileName = Text(f, ifd0, 50936),
                DefaultBlackRender = (int)f.Integer(ifd0, 51110, 0),
                HueSatMapDims = cals.Any(c => c.HueSatMap != null) ? hsDims : null,
                HueSatMapEncoding = (int)f.Integer(ifd0, 51107, 0),
                LookTableDims = Dims(f, ifd0, 50981) is { } ld && Floats(f, ifd0, 50982, ld[0] * ld[1] * ld[2] * 3) != null ? ld : null,
                LookTable = Dims(f, ifd0, 50981) is { } ld2 ? Floats(f, ifd0, 50982, ld2[0] * ld2[1] * ld2[2] * 3) : null,
                LookTableEncoding = (int)f.Integer(ifd0, 51108, 0),
                OpcodeList1 = ops1, OpcodeList2 = ops2, OpcodeList3 = ops3,
                Previews = previews,
            };
        }
    }
}
