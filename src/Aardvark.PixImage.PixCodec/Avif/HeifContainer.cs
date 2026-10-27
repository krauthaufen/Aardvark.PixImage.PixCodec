using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Aardvark.ImageCodecs.Heif
{
    /// <summary>A box of an ISO base media file (ISO/IEC 14496-12 4.2): type and payload range in the file.</summary>
    internal readonly struct IsoBox
    {
        public readonly string Type;
        public readonly long Start;      // first byte of the box header
        public readonly long Payload;    // first byte after the header (and extended type)
        public readonly long End;        // first byte after the box
        public IsoBox(string type, long start, long payload, long end) { Type = type; Start = start; Payload = payload; End = end; }
        public long Size => End - Payload;
    }

    /// <summary>Big endian reader over a span with bounds checks that throw <see cref="ImageFormatException"/>.</summary>
    internal ref struct IsoReader
    {
        readonly ReadOnlySpan<byte> d;
        public long Pos;
        public readonly long End;

        public IsoReader(ReadOnlySpan<byte> data, long pos, long end)
        {
            d = data; Pos = pos; End = Math.Min(end, data.Length);
        }

        void Need(long n)
        {
            if (n < 0 || Pos + n > End) throw new ImageFormatException("HEIF: truncated box");
        }

        public byte U8() { Need(1); return d[(int)Pos++]; }
        public ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16BigEndian(d.Slice((int)Pos)); Pos += 2; return v; }
        public uint U32() { Need(4); var v = BinaryPrimitives.ReadUInt32BigEndian(d.Slice((int)Pos)); Pos += 4; return v; }
        public ulong U64() { Need(8); var v = BinaryPrimitives.ReadUInt64BigEndian(d.Slice((int)Pos)); Pos += 8; return v; }
        public int I32() => (int)U32();
        public long UN(int bytes) => bytes switch
        {
            0 => 0,
            1 => U8(),
            2 => U16(),
            4 => U32(),
            8 => (long)U64(),
            _ => throw new ImageFormatException("HEIF: bad field size"),
        };
        public string FourCC() { Need(4); var s = Encoding.ASCII.GetString(d.Slice((int)Pos, 4)); Pos += 4; return s; }
        public (int version, int flags) FullBox() { uint v = U32(); return ((int)(v >> 24), (int)(v & 0xFFFFFF)); }
        public string CString()
        {
            long start = Pos;
            while (Pos < End && d[(int)Pos] != 0) Pos++;
            var s = Encoding.UTF8.GetString(d.Slice((int)start, (int)(Pos - start)));
            if (Pos < End) Pos++;
            return s;
        }
        public byte[] Bytes(long n) { Need(n); var b = d.Slice((int)Pos, (int)n).ToArray(); Pos += n; return b; }
        public void Skip(long n) { Need(n); Pos += n; }
        public bool AtEnd => Pos >= End;
    }

    internal static class IsoBoxes
    {
        /// <summary>The boxes between <paramref name="start"/> and <paramref name="end"/>; a malformed tail is ignored.</summary>
        public static List<IsoBox> List(ReadOnlySpan<byte> d, long start, long end)
        {
            var r = new List<IsoBox>();
            long pos = start;
            end = Math.Min(end, d.Length);
            while (pos + 8 <= end)
            {
                long size = BinaryPrimitives.ReadUInt32BigEndian(d.Slice((int)pos));
                string type = Encoding.ASCII.GetString(d.Slice((int)pos + 4, 4));
                long header = 8;
                if (size == 1)
                {
                    if (pos + 16 > end) break;
                    size = (long)BinaryPrimitives.ReadUInt64BigEndian(d.Slice((int)pos + 8));
                    header = 16;
                }
                else if (size == 0) size = end - pos;
                if (type == "uuid") header += 16;
                if (size < header || pos + size > end) break;
                r.Add(new IsoBox(type, pos, pos + header, pos + size));
                pos += size;
            }
            return r;
        }

        public static IsoBox? Find(List<IsoBox> boxes, string type)
        {
            foreach (var b in boxes) if (b.Type == type) return b;
            return null;
        }
    }

    /// <summary>An association of an item with a property (ISO/IEC 14496-12 8.11.14).</summary>
    internal readonly struct PropertyRef
    {
        public readonly int Index;   // into HeifFile.Properties
        public readonly bool Essential;
        public PropertyRef(int index, bool essential) { Index = index; Essential = essential; }
    }

    internal sealed class HeifExtent
    {
        public long Offset, Length, ItemRef;
    }

    /// <summary>An item of the MetaBox with its location, properties and references.</summary>
    internal sealed class HeifItem
    {
        public uint Id;
        public string Type = "";
        public string Name = "";
        public string ContentType = "";
        public string ContentEncoding = "";
        public bool Hidden;
        /// <summary>
        /// 1 based index into ipro: a protected item. It is decoded as if it were not, as avifdec 1.4.2 does (HEIF 10.2.2.2:
        /// readers should fail on items protected by a scheme they do not know).
        /// </summary>
        public int ProtectionIndex;
        public int ConstructionMethod;
        public int DataReferenceIndex;
        public long BaseOffset;
        public readonly List<HeifExtent> Extents = new();
        public bool HasLocation;
        public readonly List<PropertyRef> Properties = new();
        /// <summary>References from this item: (type, to_item_ID list).</summary>
        public readonly List<(string type, uint[] to)> References = new();

        public uint[] Refs(string type)
        {
            foreach (var r in References) if (r.type == type) return r.to;
            return Array.Empty<uint>();
        }
    }

    /// <summary>A property box of the ItemPropertyContainerBox.</summary>
    internal sealed class HeifProperty
    {
        public string Type = "";
        public long Payload, End;
    }

    /// <summary>One sample of a track: file range, duration, sync flag.</summary>
    internal readonly struct TrackSample
    {
        public readonly long Offset;
        public readonly int Size;
        public readonly uint Duration;
        public readonly bool Sync;
        public TrackSample(long offset, int size, uint duration, bool sync) { Offset = offset; Size = size; Duration = duration; Sync = sync; }
    }

    /// <summary>A track of an image sequence (ISO/IEC 14496-12 8.3 - 8.7).</summary>
    internal sealed class HeifTrack
    {
        public uint TrackId;
        public string Handler = "";
        public uint Timescale;
        public ulong Duration;
        public int Width, Height;              // tkhd, 16.16 truncated
        public string SampleEntryType = "";
        public IsoBox? Av1C, Colr, ColrIcc, Ccst, Auxi, Pixi;
        public readonly List<IsoBox> SampleEntryBoxes = new();
        public string AuxType = "";
        public readonly List<TrackSample> Samples = new();
        public readonly List<(string type, uint[] ids)> References = new();
        public bool HasEditList;
        public int LoopCount = 1;              // times to play, 0 = forever (MIAF Amd 1 Table C.1)
        /// <summary>The tkhd matrix. Not applied, as avifdec 1.4.2 does not (MIAF 7.4.3 allows rotations and mirroring in it).</summary>
        public int[] Matrix = { 0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x40000000 };
        /// <summary>The clap of the sample entry (MIAF 7.4.4.1), as in <see cref="HeifTransform.Clap"/>.</summary>
        public int[]? Clap;
    }

    /// <summary>
    /// The structure of a HEIF / AVIF file: brands, MetaBox items and properties (ISO/IEC 23008-12 clause 9, ISO/IEC
    /// 14496-12 8.11), and the tracks of image sequences. Unknown boxes are skipped.
    /// </summary>
    internal sealed class HeifFile
    {
        public string MajorBrand = "";
        public readonly List<string> Brands = new();
        public readonly Dictionary<uint, HeifItem> Items = new();
        public readonly List<uint> ItemOrder = new();
        public readonly List<HeifProperty> Properties = new();
        public uint PrimaryItemId;
        public bool HasMeta;
        public IsoBox? Idat;
        public readonly List<HeifTrack> Tracks = new();
        public readonly List<(string type, uint groupId, uint[] entities)> Groups = new();

        public static bool IsMatch(ReadOnlySpan<byte> h)
        {
            if (h.Length < 12) return false;
            if (!h.Slice(4, 4).SequenceEqual("ftyp"u8)) return false;
            long size = BinaryPrimitives.ReadUInt32BigEndian(h);
            if (size < 16) return false;
            long end = Math.Min(size, h.Length);
            bool Avif(ReadOnlySpan<byte> b) => b.SequenceEqual("avif"u8) || b.SequenceEqual("avis"u8) || b.SequenceEqual("avio"u8);
            if (Avif(h.Slice(8, 4))) return true;
            // a low-overhead file (HEIF Amd 2 O.2.1): major brand 'mif3', the codec brand in minor_version
            if (h.Slice(8, 4).SequenceEqual("mif3"u8) && Avif(h.Slice(12, 4))) return true;
            for (long p = 16; p + 4 <= end; p += 4)
                if (Avif(h.Slice((int)p, 4))) return true;
            return false;
        }

        public static HeifFile Parse(ReadOnlySpan<byte> d)
        {
            var f = new HeifFile();
            var top = IsoBoxes.List(d, 0, d.Length);
            if (top.Count == 0 || top[0].Type != "ftyp") throw new ImageFormatException("HEIF: no ftyp box");
            {
                var b = top[0];
                var r = new IsoReader(d, b.Payload, b.End);
                f.MajorBrand = r.FourCC();
                r.U32();
                while (r.Pos + 4 <= r.End) f.Brands.Add(r.FourCC());
            }
            foreach (var b in top)
            {
                if (b.Type == "meta" && !f.HasMeta) ParseMeta(d, b, f);
                else if (b.Type == "moov") ParseMoov(d, b, f);
            }
            return f;
        }

        static void ParseMeta(ReadOnlySpan<byte> d, IsoBox meta, HeifFile f)
        {
            f.HasMeta = true;
            // FullBox header (version, flags) before the children
            var children = IsoBoxes.List(d, meta.Payload + 4, meta.End);
            var hdlr = IsoBoxes.Find(children, "hdlr");
            if (hdlr is IsoBox hb)
            {
                var r = new IsoReader(d, hb.Payload, hb.End);
                r.FullBox();
                r.U32();
                string handler = r.FourCC();
                if (handler != "pict") { f.HasMeta = false; return; }
            }
            foreach (var b in children)
            {
                var r = new IsoReader(d, b.Payload, b.End);
                switch (b.Type)
                {
                    case "pitm":
                        {
                            var (v, _) = r.FullBox();
                            f.PrimaryItemId = v == 0 ? r.U16() : r.U32();
                            break;
                        }
                    case "iinf":
                        {
                            var (v, _) = r.FullBox();
                            long count = v == 0 ? r.U16() : r.U32();
                            var entries = IsoBoxes.List(d, r.Pos, b.End);
                            foreach (var e in entries)
                            {
                                if (e.Type != "infe") continue;
                                var it = ParseInfe(d, e);
                                if (it != null && !f.Items.ContainsKey(it.Id))
                                {
                                    f.Items[it.Id] = it;
                                    f.ItemOrder.Add(it.Id);
                                }
                            }
                            break;
                        }
                    case "idat":
                        f.Idat = b;
                        break;
                }
            }
            foreach (var b in children)
            {
                var r = new IsoReader(d, b.Payload, b.End);
                switch (b.Type)
                {
                    case "iloc": ParseIloc(ref r, f); break;
                    case "iref": ParseIref(d, b, f); break;
                    case "iprp": ParseIprp(d, b, f); break;
                    case "grpl": ParseGroups(d, b, f); break;
                }
            }
        }

        static HeifItem? ParseInfe(ReadOnlySpan<byte> d, IsoBox e)
        {
            var r = new IsoReader(d, e.Payload, e.End);
            var (v, flags) = r.FullBox();
            var it = new HeifItem();
            if (v == 0 || v == 1)
            {
                it.Id = r.U16();
                it.ProtectionIndex = r.U16();
                it.Name = r.CString();
                it.ContentType = r.CString();
                it.Type = "";
                return it;
            }
            it.Id = v == 2 ? r.U16() : r.U32();
            it.ProtectionIndex = r.U16();
            it.Type = r.FourCC();
            it.Name = r.CString();
            if (it.Type == "mime")
            {
                it.ContentType = r.AtEnd ? "" : r.CString();
                it.ContentEncoding = r.AtEnd ? "" : r.CString();
            }
            it.Hidden = (flags & 1) != 0;
            return it;
        }

        static void ParseIloc(ref IsoReader r, HeifFile f)
        {
            var (v, _) = r.FullBox();
            int a = r.U8(), b = r.U8();
            int offsetSize = a >> 4, lengthSize = a & 15, baseOffsetSize = b >> 4;
            int indexSize = v == 1 || v == 2 ? b & 15 : 0;
            long count = v < 2 ? r.U16() : r.U32();
            for (long i = 0; i < count; i++)
            {
                uint id = v < 2 ? r.U16() : r.U32();
                int cm = 0;
                if (v == 1 || v == 2) cm = r.U16() & 15;
                int dri = r.U16();
                long baseOffset = r.UN(baseOffsetSize);
                int extentCount = r.U16();
                var exts = new List<HeifExtent>();
                for (int j = 0; j < extentCount; j++)
                {
                    var x = new HeifExtent();
                    if ((v == 1 || v == 2) && indexSize > 0) x.ItemRef = r.UN(indexSize);
                    x.Offset = r.UN(offsetSize);
                    x.Length = r.UN(lengthSize);
                    exts.Add(x);
                }
                if (f.Items.TryGetValue(id, out var it) && !it.HasLocation)
                {
                    it.HasLocation = true;
                    it.ConstructionMethod = cm;
                    it.DataReferenceIndex = dri;
                    it.BaseOffset = baseOffset;
                    it.Extents.AddRange(exts);
                }
            }
        }

        static void ParseIref(ReadOnlySpan<byte> d, IsoBox b, HeifFile f)
        {
            var r = new IsoReader(d, b.Payload, b.End);
            var (v, _) = r.FullBox();
            foreach (var rb in IsoBoxes.List(d, r.Pos, b.End))
            {
                var rr = new IsoReader(d, rb.Payload, rb.End);
                uint from = v == 0 ? rr.U16() : rr.U32();
                int n = rr.U16();
                var to = new uint[n];
                for (int i = 0; i < n; i++) to[i] = v == 0 ? rr.U16() : rr.U32();
                if (f.Items.TryGetValue(from, out var it)) it.References.Add((rb.Type, to));
            }
        }

        static void ParseIprp(ReadOnlySpan<byte> d, IsoBox b, HeifFile f)
        {
            var children = IsoBoxes.List(d, b.Payload, b.End);
            var ipco = IsoBoxes.Find(children, "ipco");
            if (ipco is IsoBox pc)
                foreach (var p in IsoBoxes.List(d, pc.Payload, pc.End))
                    f.Properties.Add(new HeifProperty { Type = p.Type, Payload = p.Payload, End = p.End });
            foreach (var c in children)
            {
                if (c.Type != "ipma") continue;
                var r = new IsoReader(d, c.Payload, c.End);
                var (v, flags) = r.FullBox();
                uint count = r.U32();
                for (uint i = 0; i < count; i++)
                {
                    uint id = v < 1 ? r.U16() : r.U32();
                    int n = r.U8();
                    f.Items.TryGetValue(id, out var it);
                    for (int j = 0; j < n; j++)
                    {
                        int idx;
                        bool essential;
                        if ((flags & 1) != 0)
                        {
                            int x = r.U16();
                            essential = (x & 0x8000) != 0;
                            idx = x & 0x7FFF;
                        }
                        else
                        {
                            int x = r.U8();
                            essential = (x & 0x80) != 0;
                            idx = x & 0x7F;
                        }
                        if (idx == 0 || idx > f.Properties.Count) continue;
                        it?.Properties.Add(new PropertyRef(idx - 1, essential));
                    }
                }
            }
        }

        static void ParseGroups(ReadOnlySpan<byte> d, IsoBox b, HeifFile f)
        {
            foreach (var g in IsoBoxes.List(d, b.Payload, b.End))
            {
                var r = new IsoReader(d, g.Payload, g.End);
                r.FullBox();
                uint groupId = r.U32();
                uint n = r.U32();
                if (n > 1 << 16) continue;
                var ids = new uint[n];
                for (int i = 0; i < n; i++) ids[i] = r.U32();
                f.Groups.Add((g.Type, groupId, ids));
            }
        }

        /// <summary>The bytes of an item (all extents concatenated), from the file, the idat box or another item.</summary>
        public byte[] ItemData(ReadOnlySpan<byte> d, HeifItem it, int depth = 0)
        {
            if (!it.HasLocation) return Array.Empty<byte>();
            if (it.DataReferenceIndex != 0) throw new ImageNotSupportedException("HEIF: item data in an external file");
            if (depth > 8) throw new ImageFormatException("HEIF: item construction loop");
            long total = 0;
            foreach (var x in it.Extents) total += x.Length;
            byte[]? src = null;
            ReadOnlySpan<byte> srcSpan = d;
            long srcBase = 0, srcEnd = d.Length;
            switch (it.ConstructionMethod)
            {
                case 0: break;
                case 1:
                    if (Idat is not IsoBox idat) throw new ImageFormatException("HEIF: item in a missing idat box");
                    srcBase = idat.Payload; srcEnd = idat.End;
                    break;
                case 2: break;
                default: throw new ImageNotSupportedException($"HEIF: construction method {it.ConstructionMethod}");
            }
            if (it.ConstructionMethod == 2)
            {
                var parts = new List<byte[]>();
                var ilocRefs = it.Refs("iloc");
                foreach (var x in it.Extents)
                {
                    long ri = x.ItemRef == 0 ? 1 : x.ItemRef;
                    if (ri < 1 || ri > ilocRefs.Length || !Items.TryGetValue(ilocRefs[ri - 1], out var other))
                        throw new ImageFormatException("HEIF: bad item reference of an extent");
                    var od = ItemData(d, other, depth + 1);
                    long off = it.BaseOffset + x.Offset;
                    long len = x.Length == 0 ? od.Length - off : x.Length;
                    if (off < 0 || len < 0 || off + len > od.Length) throw new ImageFormatException("HEIF: extent outside its item");
                    parts.Add(od.AsSpan((int)off, (int)len).ToArray());
                }
                return Concat(parts);
            }
            if (total > int.MaxValue) throw new ImageNotSupportedException("HEIF: item larger than 2 GB");
            var result = new List<byte[]>();
            foreach (var x in it.Extents)
            {
                long off = srcBase + it.BaseOffset + x.Offset;
                long len = x.Length;
                if (len == 0) len = srcEnd - off;   // a length of 0 means the rest of the data
                if (off < srcBase || len < 0 || off + len > srcEnd) throw new ImageFormatException("HEIF: item extent outside the file");
                result.Add(srcSpan.Slice((int)off, (int)len).ToArray());
            }
            _ = src;
            return Concat(result);
        }

        static byte[] Concat(List<byte[]> parts)
        {
            if (parts.Count == 1) return parts[0];
            long n = 0;
            foreach (var p in parts) n += p.Length;
            var r = new byte[n];
            int o = 0;
            foreach (var p in parts) { p.CopyTo(r, o); o += p.Length; }
            return r;
        }

        public HeifProperty? Prop(HeifItem it, string type)
        {
            foreach (var p in it.Properties)
                if (Properties[p.Index].Type == type) return Properties[p.Index];
            return null;
        }

        /// <summary>Items that reference <paramref name="target"/> with a reference of the type.</summary>
        public IEnumerable<HeifItem> Referencing(uint target, string type)
        {
            foreach (var id in ItemOrder)
            {
                var it = Items[id];
                foreach (var r in it.References)
                    if (r.type == type && Array.IndexOf(r.to, target) >= 0) { yield return it; break; }
            }
        }

        // ------------------------------------------------------------------ tracks

        static void ParseMoov(ReadOnlySpan<byte> d, IsoBox moov, HeifFile f)
        {
            foreach (var trak in IsoBoxes.List(d, moov.Payload, moov.End))
            {
                if (trak.Type != "trak") continue;
                try
                {
                    var t = ParseTrak(d, trak);
                    if (t != null) f.Tracks.Add(t);
                }
                catch (ImageFormatException) { }   // a damaged track does not hide the items
            }
        }

        static HeifTrack? ParseTrak(ReadOnlySpan<byte> d, IsoBox trak)
        {
            var t = new HeifTrack();
            var children = IsoBoxes.List(d, trak.Payload, trak.End);
            ulong tkhdDuration = ulong.MaxValue;   // all 1s: indefinite
            if (IsoBoxes.Find(children, "tkhd") is IsoBox tkhd)
            {
                var r = new IsoReader(d, tkhd.Payload, tkhd.End);
                var (v, _) = r.FullBox();
                if (v == 1) { r.U64(); r.U64(); t.TrackId = r.U32(); r.U32(); tkhdDuration = r.U64(); }
                else { r.U32(); r.U32(); t.TrackId = r.U32(); r.U32(); uint du = r.U32(); tkhdDuration = du == uint.MaxValue ? ulong.MaxValue : du; }
                r.Skip(8 + 2 + 2 + 2 + 2);
                for (int i = 0; i < 9; i++) t.Matrix[i] = r.I32();
                t.Width = (int)(r.U32() >> 16);
                t.Height = (int)(r.U32() >> 16);
            }
            if (IsoBoxes.Find(children, "tref") is IsoBox tref)
                foreach (var rb in IsoBoxes.List(d, tref.Payload, tref.End))
                {
                    var r = new IsoReader(d, rb.Payload, rb.End);
                    var ids = new List<uint>();
                    while (r.Pos + 4 <= r.End) ids.Add(r.U32());
                    t.References.Add((rb.Type, ids.ToArray()));
                }
            if (IsoBoxes.Find(children, "edts") is IsoBox edts && IsoBoxes.Find(IsoBoxes.List(d, edts.Payload, edts.End), "elst") is IsoBox elst)
            {
                t.HasEditList = true;
                var r = new IsoReader(d, elst.Payload, elst.End);
                var (v, flags) = r.FullBox();
                // MIAF Amd 1 A.2, Table C.1: RepeatEdits (flags & 1) repeats the edit list R = tkhd duration / edit list
                // duration times, played ceil(R) times; forever with an indefinite tkhd duration (avifdec 1.4.2 agrees)
                ulong total = 0;
                uint n = r.U32();
                for (uint i = 0; i < n && !r.AtEnd; i++)
                {
                    total += v == 1 ? r.U64() : r.U32();
                    r.Skip(v == 1 ? 8 + 4 : 4 + 4);
                }
                if ((flags & 1) == 0) t.LoopCount = 1;
                else if (tkhdDuration == ulong.MaxValue || total == 0) t.LoopCount = 0;
                else t.LoopCount = (int)Math.Min(int.MaxValue, (tkhdDuration + total - 1) / total);
            }
            else t.LoopCount = 0;   // no edit list: forever (Table C.1; avifdec 1.4.2 reports an unknown count)
            var mdia = IsoBoxes.Find(children, "mdia");
            if (mdia is not IsoBox md) return null;
            var mc = IsoBoxes.List(d, md.Payload, md.End);
            if (IsoBoxes.Find(mc, "mdhd") is IsoBox mdhd)
            {
                var r = new IsoReader(d, mdhd.Payload, mdhd.End);
                var (v, _) = r.FullBox();
                if (v == 1) { r.U64(); r.U64(); t.Timescale = r.U32(); t.Duration = r.U64(); }
                else { r.U32(); r.U32(); t.Timescale = r.U32(); t.Duration = r.U32(); }
            }
            if (IsoBoxes.Find(mc, "hdlr") is IsoBox hdlr)
            {
                var r = new IsoReader(d, hdlr.Payload, hdlr.End);
                r.FullBox();
                r.U32();
                t.Handler = r.FourCC();
            }
            var minf = IsoBoxes.Find(mc, "minf");
            if (minf is not IsoBox mi) return null;
            var stblB = IsoBoxes.Find(IsoBoxes.List(d, mi.Payload, mi.End), "stbl");
            if (stblB is not IsoBox stbl) return null;
            var sc = IsoBoxes.List(d, stbl.Payload, stbl.End);
            if (IsoBoxes.Find(sc, "stsd") is IsoBox stsd)
            {
                var r = new IsoReader(d, stsd.Payload, stsd.End);
                r.FullBox();
                uint n = r.U32();
                var entries = IsoBoxes.List(d, r.Pos, stsd.End);
                if (entries.Count > 0)
                {
                    var e = entries[0];
                    t.SampleEntryType = e.Type;
                    // VisualSampleEntry: 6 reserved, data_reference_index, 16 predefined, width, height, ... 78 bytes in all
                    long childStart = e.Payload + 78;
                    if (childStart <= e.End)
                        foreach (var cb in IsoBoxes.List(d, childStart, e.End))
                        {
                            t.SampleEntryBoxes.Add(cb);
                            switch (cb.Type)
                            {
                                case "av1C": t.Av1C = cb; break;
                                case "colr":
                                    {
                                        var cr = new IsoReader(d, cb.Payload, cb.End);
                                        string ct = cr.FourCC();
                                        if (ct == "nclx") t.Colr ??= cb;
                                        else if (ct == "prof" || ct == "rICC") t.ColrIcc ??= cb;
                                        break;
                                    }
                                case "ccst": t.Ccst = cb; break;
                                case "auxi":
                                    {
                                        t.Auxi = cb;
                                        var ar = new IsoReader(d, cb.Payload, cb.End);
                                        ar.FullBox();
                                        t.AuxType = ar.CString();
                                        break;
                                    }
                                case "pixi": t.Pixi = cb; break;
                                case "clap":
                                    {
                                        var cr = new IsoReader(d, cb.Payload, cb.End);
                                        var f = new int[8];
                                        for (int i = 0; i < 8; i++) f[i] = cr.I32();
                                        t.Clap = f;
                                        break;
                                    }
                            }
                        }
                }
            }
            ParseSamples(d, sc, t);
            return t;
        }

        static void ParseSamples(ReadOnlySpan<byte> d, List<IsoBox> sc, HeifTrack t)
        {
            // sample sizes
            var sizes = new List<int>();
            if (IsoBoxes.Find(sc, "stsz") is IsoBox stsz)
            {
                var r = new IsoReader(d, stsz.Payload, stsz.End);
                r.FullBox();
                uint sampleSize = r.U32();
                uint count = r.U32();
                if (count > 1 << 24) throw new ImageNotSupportedException("HEIF: too many samples");
                for (uint i = 0; i < count; i++) sizes.Add(sampleSize != 0 ? (int)sampleSize : (int)r.U32());
            }
            else if (IsoBoxes.Find(sc, "stz2") is IsoBox stz2)
            {
                var r = new IsoReader(d, stz2.Payload, stz2.End);
                r.FullBox();
                r.Skip(3);
                int fieldSize = r.U8();
                uint count = r.U32();
                if (count > 1 << 24) throw new ImageNotSupportedException("HEIF: too many samples");
                for (uint i = 0; i < count; i++)
                {
                    if (fieldSize == 4)
                    {
                        int b = r.U8();
                        sizes.Add(b >> 4);
                        if (++i < count) sizes.Add(b & 15);
                    }
                    else sizes.Add((int)r.UN(fieldSize / 8));
                }
            }
            // chunk offsets
            var chunks = new List<long>();
            if (IsoBoxes.Find(sc, "stco") is IsoBox stco)
            {
                var r = new IsoReader(d, stco.Payload, stco.End);
                r.FullBox();
                uint n = r.U32();
                for (uint i = 0; i < n && !r.AtEnd; i++) chunks.Add(r.U32());
            }
            else if (IsoBoxes.Find(sc, "co64") is IsoBox co64)
            {
                var r = new IsoReader(d, co64.Payload, co64.End);
                r.FullBox();
                uint n = r.U32();
                for (uint i = 0; i < n && !r.AtEnd; i++) chunks.Add((long)r.U64());
            }
            // sample to chunk
            var stscEntries = new List<(uint first, uint perChunk)>();
            if (IsoBoxes.Find(sc, "stsc") is IsoBox stsc)
            {
                var r = new IsoReader(d, stsc.Payload, stsc.End);
                r.FullBox();
                uint n = r.U32();
                for (uint i = 0; i < n && !r.AtEnd; i++)
                {
                    uint first = r.U32(), per = r.U32();
                    r.U32();
                    stscEntries.Add((first, per));
                }
            }
            // durations
            var durations = new List<(uint count, uint delta)>();
            if (IsoBoxes.Find(sc, "stts") is IsoBox stts)
            {
                var r = new IsoReader(d, stts.Payload, stts.End);
                r.FullBox();
                uint n = r.U32();
                for (uint i = 0; i < n && !r.AtEnd; i++) durations.Add((r.U32(), r.U32()));
            }
            HashSet<uint>? sync = null;
            if (IsoBoxes.Find(sc, "stss") is IsoBox stss)
            {
                sync = new HashSet<uint>();
                var r = new IsoReader(d, stss.Payload, stss.End);
                r.FullBox();
                uint n = r.U32();
                for (uint i = 0; i < n && !r.AtEnd; i++) sync.Add(r.U32());
            }
            int sample = 0;
            int durIdx = 0;
            uint durLeft = durations.Count > 0 ? durations[0].count : 0;
            for (int c = 0; c < chunks.Count && sample < sizes.Count; c++)
            {
                uint chunkNo = (uint)c + 1;
                uint per = 0;
                foreach (var e in stscEntries) if (e.first <= chunkNo) per = e.perChunk;
                long off = chunks[c];
                for (uint k = 0; k < per && sample < sizes.Count; k++)
                {
                    uint dur = 0;
                    while (durIdx < durations.Count && durLeft == 0) { durIdx++; durLeft = durIdx < durations.Count ? durations[durIdx].count : 0; }
                    if (durIdx < durations.Count) { dur = durations[durIdx].delta; durLeft--; }
                    bool isSync = sync == null || sync.Contains((uint)sample + 1);
                    t.Samples.Add(new TrackSample(off, sizes[sample], dur, isSync));
                    off += sizes[sample];
                    sample++;
                }
            }
        }
    }
}
