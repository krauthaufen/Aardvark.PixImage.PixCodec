using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs
{
    public static partial class Dicom
    {
        // ================================================================ finding series

        /// <summary>
        /// The image series of a DICOMDIR (the file, or a folder holding one, as on hospital CDs and USB sticks), or of all
        /// DICOM files below a folder; for a single image file the series of its folder. Ordered by patient, study and
        /// series number.
        /// </summary>
        public static IReadOnlyList<DicomSeries> FindSeries(string path)
        {
            if (Directory.Exists(path))
            {
                var dir = Directory.EnumerateFiles(path).FirstOrDefault(f => Path.GetFileName(f).Equals("DICOMDIR", StringComparison.OrdinalIgnoreCase));
                if (dir != null) return FromDicomDir(dir);
                return Scan(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories));
            }
            var head = ReadHeader(path) ?? throw new ImageFormatException($"{Fmt}: '{path}' is not a DICOM file");
            if (head.Contains(DicomTags.DirectoryRecordSequence)) return FromDicomDir(path);
            return Scan(Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(path))!));
        }

        /// <summary>
        /// Decodes a series: <paramref name="path"/> is a DICOMDIR or a folder (its largest series), or one file of the
        /// series (the files of its folder with the same Series Instance UID).
        /// </summary>
        public static RawVolume DecodeSeries(string path, DicomDecodeOptions? options = null)
        {
            if (Directory.Exists(path)) return Largest(path).Decode(options);
            var head = ReadHeader(path) ?? throw new ImageFormatException($"{Fmt}: '{path}' is not a DICOM file");
            if (head.Contains(DicomTags.DirectoryRecordSequence)) return Largest(path).Decode(options);
            string? uid = head.GetString(DicomTags.SeriesInstanceUid);
            var full = Path.GetFullPath(path);
            var series = Scan(Directory.EnumerateFiles(Path.GetDirectoryName(full)!)).FirstOrDefault(s => s.SeriesInstanceUid == uid && s.Files.Contains(full));
            return DecodeSeries(series?.Files ?? new[] { full }, options);
        }

        /// <summary>
        /// Reads the header of a file (the first 64 KB, more if the elements before the pixel data need it); null if it is
        /// not a DICOM file.
        /// </summary>
        static DicomDataset? ReadHeader(string path)
        {
            byte[] prefix;
            long length;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 12))
            {
                length = fs.Length;
                prefix = new byte[(int)Math.Min(length, 1 << 16)];
                fs.ReadExactly(prefix);
            }
            if (!DicomParser.IsMatch(prefix)) return null;
            if (prefix.Length < length)
            {
                // (a prefix that ends between two elements parses cleanly: only one that reached the pixel data holds the whole header)
                try { var h = DicomParser.Parse(prefix, true); if (h.ReachedPixels) return h; }
                catch (ImageFormatException) { }   // header elements beyond the prefix
            }
            try { return DicomParser.Parse(File.ReadAllBytes(path), true); }
            catch (ImageFormatException) { return null; }
        }

        static IReadOnlyList<DicomSeries> Scan(IEnumerable<string> files)
        {
            var found = new List<(string File, DicomDataset Header)>();
            foreach (var f in files)
            {
                DicomDataset? h;
                try { h = ReadHeader(f); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if (h == null || !HasPixels(h)) continue;
                found.Add((Path.GetFullPath(f), h));
            }
            var result = new List<DicomSeries>();
            foreach (var g in found.GroupBy(x => x.Header.GetString(DicomTags.SeriesInstanceUid) ?? ""))
            {
                var h = g.First().Header;
                result.Add(Describe(h, g.Select(x => x.File).OrderBy(x => x, StringComparer.Ordinal).ToList(), h));
            }
            return Sort(result);
        }

        static DicomSeries Describe(DicomDataset series, List<string> files, DicomDataset patientAndStudy) => new()
        {
            SeriesInstanceUid = series.GetString(DicomTags.SeriesInstanceUid) ?? "",
            StudyInstanceUid = patientAndStudy.GetString(DicomTags.StudyInstanceUid),
            PatientName = patientAndStudy.GetString(DicomTags.PatientName),
            PatientId = patientAndStudy.GetString(DicomTags.PatientId),
            StudyDate = patientAndStudy.GetString(DicomTags.StudyDate),
            StudyDescription = patientAndStudy.GetString(DicomTags.StudyDescription),
            SeriesDescription = series.GetString(DicomTags.SeriesDescription),
            Modality = series.GetString(DicomTags.Modality),
            SeriesNumber = series.GetInt(DicomTags.SeriesNumber),
            Files = files,
        };

        static IReadOnlyList<DicomSeries> Sort(List<DicomSeries> s)
            => s.OrderBy(x => x.PatientId, StringComparer.Ordinal).ThenBy(x => x.StudyDate, StringComparer.Ordinal).ThenBy(x => x.StudyInstanceUid, StringComparer.Ordinal)
                .ThenBy(x => x.SeriesNumber ?? int.MaxValue).ThenBy(x => x.SeriesInstanceUid, StringComparer.Ordinal).ToList();

        /// <summary>The series of a DICOMDIR (PS3.10 media storage directory): PATIENT, STUDY, SERIES and IMAGE records.</summary>
        static IReadOnlyList<DicomSeries> FromDicomDir(string path)
        {
            var ds = DicomParser.Parse(File.ReadAllBytes(path), false);
            var records = ds.GetSequence(DicomTags.DirectoryRecordSequence);
            string root = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var byOffset = new Dictionary<long, DicomDataset>();
            foreach (var r in records) byOffset[r.Offset] = r;
            var result = new List<DicomSeries>();

            void Add(DicomDataset patient, DicomDataset study, DicomDataset series, IEnumerable<DicomDataset> images)
            {
                var files = new List<string>();
                foreach (var im in images)
                {
                    var id = im.GetStrings(DicomTags.ReferencedFileId);
                    if (id == null || id.Length == 0) continue;
                    var f = Resolve(root, id);
                    if (f != null) files.Add(f);
                }
                if (files.Count == 0) return;
                var s = Describe(series, files, study);
                s.PatientName = patient.GetString(DicomTags.PatientName) ?? s.PatientName;
                s.PatientId = patient.GetString(DicomTags.PatientId) ?? s.PatientId;
                result.Add(s);
            }

            // the records point at their siblings and children by file offset; without usable offsets they are in order
            long first = ds.GetInt(DicomTags.FirstRootRecordOffset) ?? 0;
            if (byOffset.ContainsKey(first))
            {
                IEnumerable<DicomDataset> Siblings(long off)
                {
                    var seen = new HashSet<long>();
                    while (off != 0 && byOffset.TryGetValue(off, out var r) && seen.Add(off))
                    {
                        yield return r;
                        off = (long)(r.GetDouble(DicomTags.NextRecordOffset) ?? 0);
                    }
                }
                IEnumerable<DicomDataset> Children(DicomDataset r) => Siblings((long)(r.GetDouble(DicomTags.LowerLevelRecordOffset) ?? 0));
                static string Type(DicomDataset r) => r.GetString(DicomTags.DirectoryRecordType) ?? "";
                foreach (var pat in Siblings(first).Where(r => Type(r) == "PATIENT"))
                    foreach (var st in Children(pat).Where(r => Type(r) == "STUDY"))
                        foreach (var se in Children(st).Where(r => Type(r) == "SERIES"))
                            Add(pat, st, se, Children(se).Where(r => Type(r) == "IMAGE"));
            }
            else
            {
                DicomDataset pat = new(), st = new(), se = new();
                var images = new List<DicomDataset>();
                void Flush() { if (images.Count > 0) Add(pat, st, se, images); images = new List<DicomDataset>(); }
                foreach (var r in records)
                    switch (r.GetString(DicomTags.DirectoryRecordType))
                    {
                        case "PATIENT": Flush(); pat = r; break;
                        case "STUDY": Flush(); st = r; break;
                        case "SERIES": Flush(); se = r; break;
                        case "IMAGE": images.Add(r); break;
                    }
                Flush();
            }
            return Sort(result);
        }

        /// <summary>A Referenced File ID (path components) below the DICOMDIR's folder, matching names case-insensitively (FAT media).</summary>
        static string? Resolve(string root, string[] id)
        {
            var exact = Path.Combine(new[] { root }.Concat(id).ToArray());
            if (File.Exists(exact)) return exact;
            string cur = root;
            foreach (var part in id)
            {
                string? next = null;
                try
                {
                    foreach (var e in Directory.EnumerateFileSystemEntries(cur))
                        if (Path.GetFileName(e).Equals(part, StringComparison.OrdinalIgnoreCase)) { next = e; break; }
                }
                catch (IOException) { return null; }
                if (next == null) return null;
                cur = next;
            }
            return File.Exists(cur) ? cur : null;
        }

        // ================================================================ stacking a series

        sealed class Slice
        {
            public string File = "";
            public DicomDataset Header = null!;
            public double[]? Position, Orientation;
            public double Along;     // position along the slice normal
            public int Instance, Temporal, Frames;
        }

        /// <summary>
        /// Stacks the image files of one series into a volume ordered by position along the slice normal (else by
        /// instance number); several images per position (time points, echoes) give a fourth axis. Slices of another size
        /// or orientation than most (localizers) are left out. Multi-frame files are appended frame by frame.
        /// </summary>
        public static RawVolume DecodeSeries(IEnumerable<string> files, DicomDecodeOptions? options = null)
        {
            var o = DicomDecodeOptions.From(options);
            var slices = new List<Slice>();
            foreach (var f in files)
            {
                var h = ReadHeader(f);
                if (h == null || !HasPixels(h)) continue;
                var pf = h.GetSequence(DicomTags.PerFrameFunctionalGroups);
                var orient = h.GetDoubles(DicomTags.ImageOrientationPatient)
                    ?? h.Find(DicomTags.ImageOrientationPatient, DicomTags.SharedFunctionalGroups, DicomTags.PlaneOrientationSequence)?.GetDoubles(DicomTags.ImageOrientationPatient);
                slices.Add(new Slice
                {
                    File = f, Header = h,
                    Position = h.GetDoubles(DicomTags.ImagePositionPatient),
                    Orientation = orient,
                    Instance = h.GetInt(DicomTags.InstanceNumber) ?? 0,
                    Temporal = h.GetInt(DicomTags.TemporalPositionIdentifier) ?? h.GetInt(DicomTags.AcquisitionNumber) ?? 0,
                    Frames = Math.Max(1, h.GetInt(DicomTags.NumberOfFrames) ?? 1),
                });
            }
            if (slices.Count == 0) throw new ImageFormatException($"{Fmt}: no images in the series");
            if (slices.Count == 1) return DecodeVolume(DicomParser.Parse(File.ReadAllBytes(slices[0].File), false), o);

            // the most common size and orientation (leaves out localizers that share the series)
            string Key(Slice s) => $"{s.Header.GetInt(DicomTags.Columns)}x{s.Header.GetInt(DicomTags.Rows)}:" + (s.Orientation == null ? "" : string.Join(",", s.Orientation.Take(6).Select(v => Math.Round(v, 3))));
            slices = slices.GroupBy(Key).OrderByDescending(g => g.Count()).First().ToList();

            if (slices.Any(s => s.Frames > 1)) return Concatenate(slices.OrderBy(s => s.Instance).ToList(), o);

            bool geometry = slices.All(s => s.Position is { Length: >= 3 } && s.Orientation is { Length: >= 6 });
            double[]? normal = null;
            if (geometry)
            {
                var r = slices[0].Orientation!;
                normal = Unit(r[1] * r[5] - r[2] * r[4], r[2] * r[3] - r[0] * r[5], r[0] * r[4] - r[1] * r[3]);
                foreach (var s in slices) s.Along = s.Position![0] * normal[0] + s.Position[1] * normal[1] + s.Position[2] * normal[2];
                slices = slices.OrderBy(s => s.Along).ThenBy(s => s.Temporal).ThenBy(s => s.Instance).ToList();
            }
            else slices = slices.OrderBy(s => s.Instance).ToList();

            // positions that repeat: a time (or echo) axis
            int nz = slices.Count, nt = 1;
            if (geometry)
            {
                var distinct = new List<double>();
                foreach (var s in slices) if (distinct.Count == 0 || Math.Abs(s.Along - distinct[^1]) > 1e-3) distinct.Add(s.Along);
                if (distinct.Count < slices.Count && slices.Count % distinct.Count == 0)
                {
                    int per = slices.Count / distinct.Count;
                    bool even = true;
                    for (int z = 0; z < distinct.Count && even; z++)
                        for (int t = 0; t < per; t++)
                            if (Math.Abs(slices[z * per + t].Along - distinct[z]) > 1e-3) { even = false; break; }
                    if (even)
                    {
                        nz = distinct.Count; nt = per;
                        // order t-major: all positions of time point 0, then 1, ...
                        var reordered = new List<Slice>(slices.Count);
                        for (int t = 0; t < nt; t++) for (int z = 0; z < nz; z++) reordered.Add(slices[z * nt + t]);
                        slices = reordered;
                    }
                }
            }

            var first = slices[0].Header;
            var p0 = PixelInfo.Of(first, o);
            long frameSamples = (long)p0.Rows * p0.Columns * p0.OutChannels;
            // the rescale may differ per slice (PET): then every slice is scaled on its own
            var scales = slices.Select(s => (Slope: s.Header.GetDouble(DicomTags.RescaleSlope) ?? 1, Intercept: s.Header.GetDouble(DicomTags.RescaleIntercept) ?? 0)).ToArray();
            bool uniform = scales.All(x => x == scales[0]);
            var m = Metadata(first, p0, 1);
            bool perSlice = !uniform && p0.Scalable && o.Scaling != DataScaling.Raw;
            var outType = perSlice ? (o.Scaling == DataScaling.Float64 ? SampleType.Float64 : SampleType.Float32) : p0.OutStored;
            var arr = VolumeIO.Allocate(outType, VolumeIO.Mul(frameSamples, slices.Count, Fmt), o.MaxSamples, Fmt);
            void One(int i)
            {
                var ds = DicomParser.Parse(File.ReadAllBytes(slices[i].File), false);
                var p = PixelInfo.Of(ds, o);
                if (p.Rows != p0.Rows || p.Columns != p0.Columns || p.OutChannels != p0.OutChannels || p.OutStored != p0.OutStored)
                    throw new ImageNotSupportedException($"{Fmt}: the slices of the series differ in size or sample type");
                if (!perSlice) { p.DecodeFrame(FrameSource.Of(ds, p).Get(0), arr, i * frameSamples); return; }
                var one = RawImage.CreateArray(p.OutStored, (int)frameSamples);
                p.DecodeFrame(FrameSource.Of(ds, p).Get(0), one, 0);
                var scaled = VolumeIO.Scale(one, outType, scales[i].Slope, scales[i].Intercept, null);
                Array.Copy(scaled, 0, arr, i * frameSamples, frameSamples);
            }
            if (o.Parallel) System.Threading.Tasks.Parallel.For(0, slices.Count, One);
            else for (int i = 0; i < slices.Count; i++) One(i);

            // geometry of the stack: the step from the first to the last position
            if (geometry && nz > 1 && m.Direction != null)
            {
                var a = slices[0].Position!; var b = slices[nz - 1].Position!;
                var step = new[] { (b[0] - a[0]) / (nz - 1), (b[1] - a[1]) / (nz - 1), (b[2] - a[2]) / (nz - 1) };
                double dz = Math.Sqrt(step[0] * step[0] + step[1] * step[1] + step[2] * step[2]);
                var dir = new double[3, 3];
                for (int k = 0; k < 3; k++) { dir[k, 0] = m.Direction![k, 0]; dir[k, 1] = m.Direction[k, 1]; dir[k, 2] = dz > 0 ? step[k] / dz : normal![k]; }
                m.Direction = dir;
                m.Origin = a.Take(3).ToArray();
                m.Spacing = new[] { m.Spacing[0], m.Spacing[1], dz };
                m.Units = new string?[] { "mm", "mm", "mm" };
                // irregular gaps (missing slices) are reported, the grid is regular all the same
                double worst = 0;
                for (int z = 1; z < nz; z++) worst = Math.Max(worst, Math.Abs(slices[z].Along - slices[z - 1].Along - dz));
                if (dz > 0 && worst > 0.01 * dz) m.Header.Add(new("SliceSpacingIrregular", VolumeIO.Format(worst)));
            }
            else if (nz > 1)
            {
                double dz = first.GetDouble(DicomTags.SpacingBetweenSlices) ?? first.GetDouble(DicomTags.SliceThickness) ?? double.NaN;
                if (m.Spacing.Length >= 2) { m.Spacing = new[] { m.Spacing[0], m.Spacing[1], dz }; m.Units = new string?[] { "mm", "mm", "mm" }; }
            }
            if (nt > 1)
            {
                m.Spacing = m.Spacing.Length == 3 ? m.Spacing.Append(double.NaN).ToArray() : m.Spacing;
                if (m.Units.Length == 3) m.Units = m.Units.Append(null).ToArray();
            }
            var sizes = nt > 1 ? new[] { p0.Columns, p0.Rows, nz, nt } : new[] { p0.Columns, p0.Rows, nz };
            if (perSlice)
            {
                m.Slope = 1; m.Intercept = 0; m.ScalingApplied = true;
                return new RawVolume(sizes, p0.OutColor, arr, p0.OutChannels, m);
            }
            return Finish(arr, p0, m, sizes, o);
        }

        /// <summary>Multi-frame files of one series, one after the other along z (geometry from the first).</summary>
        static RawVolume Concatenate(List<Slice> files, DicomDecodeOptions o)
        {
            var parts = files.Select(s => DecodeVolume(DicomParser.Parse(File.ReadAllBytes(s.File), false), o)).ToList();
            var a = parts[0];
            if (parts.Any(v => v.Width != a.Width || v.Height != a.Height || v.Channels != a.Channels || v.SampleType != a.SampleType))
                throw new ImageNotSupportedException($"{Fmt}: the multi-frame files of the series differ in size or sample type");
            long total = parts.Sum(v => v.Data.LongLength);
            var arr = VolumeIO.Allocate(a.SampleType, total, o.MaxSamples, Fmt);
            long at = 0;
            foreach (var v in parts) { Array.Copy(v.Data, 0, arr, at, v.Data.LongLength); at += v.Data.LongLength; }
            int depth = parts.Sum(v => v.Depth);
            return new RawVolume(new[] { a.Width, a.Height, depth }, a.Color, arr, a.Channels, a.Metadata);
        }
    }
}
