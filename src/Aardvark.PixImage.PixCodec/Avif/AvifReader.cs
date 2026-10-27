using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Aardvark.ImageCodecs.Av1;

namespace Aardvark.ImageCodecs.Heif
{
    /// <summary>The colour description of an item: nclx values and / or an ICC profile.</summary>
    internal sealed class HeifColour
    {
        public bool HasNclx;
        public int Primaries = 2, Transfer = 2, Matrix = 2;
        public bool FullRange;
        public byte[]? Icc;
    }

    /// <summary>A geometric transform of an item (clap, irot, imir).</summary>
    internal sealed class HeifTransform
    {
        public readonly char Kind;   // 'c' clap, 'r' irot, 'm' imir
        public readonly int A;
        public readonly int[]? Clap;  // clap: widthN, widthD, heightN, heightD, horizOffN, horizOffD, vertOffN, vertOffD
        public HeifTransform(char kind, int a, int[]? clap = null) { Kind = kind; A = a; Clap = clap; }
    }

    /// <summary>AV1 configuration record (av1C, AV1 ISOBMFF binding 2.3).</summary>
    internal sealed class Av1Config
    {
        public int Profile, Level, Tier, BitDepth, SubX, SubY, ChromaSamplePosition;
        public bool Monochrome;
        public byte[] ConfigObus = Array.Empty<byte>();

        public static Av1Config? Parse(ReadOnlySpan<byte> d, long payload, long end)
        {
            if (end - payload < 4) return null;
            var b = d.Slice((int)payload, (int)(end - payload));
            if ((b[0] & 0x80) == 0) return null;
            var c = new Av1Config
            {
                Profile = b[1] >> 5,
                Level = b[1] & 31,
                Tier = b[2] >> 7,
            };
            bool high = (b[2] & 0x40) != 0, twelve = (b[2] & 0x20) != 0;
            c.BitDepth = c.Profile == 2 && high ? (twelve ? 12 : 10) : high ? 10 : 8;
            c.Monochrome = (b[2] & 0x10) != 0;
            c.SubX = (b[2] >> 3) & 1;
            c.SubY = (b[2] >> 2) & 1;
            c.ChromaSamplePosition = b[2] & 3;
            c.ConfigObus = b.Slice(4).ToArray();
            return c;
        }
    }

    /// <summary>Reads AVIF images: item decoding (AV1, grid, overlay, identity), alpha, colour, transforms, sequences.</summary>
    internal sealed class AvifReader
    {
        public const string AlphaUrn = "urn:mpeg:mpegB:cicp:systems:auxiliary:alpha";
        public const string HevcAlphaUrn = "urn:mpeg:hevc:2015:auxid:1";
        public const string DepthUrn = "urn:mpeg:mpegB:cicp:systems:auxiliary:depth";

        public readonly byte[] Data;
        public readonly HeifFile File;
        readonly AvifDecodeOptions opt;

        public AvifReader(ReadOnlySpan<byte> data, AvifDecodeOptions opt, bool copy = true)
        {
            Data = data.ToArray();
            File = HeifFile.Parse(Data);
            this.opt = opt;
        }

        /// <summary>Whether clean apertures are applied (<see cref="AvifDecodeOptions.ApplyCrop"/>).</summary>
        public bool ApplyCrop => opt.ApplyCrop;

        public HeifItem? Primary
        {
            get
            {
                if (!File.HasMeta) return null;
                if (!File.Items.TryGetValue(File.PrimaryItemId, out var p)) return null;
                if (CanDecode(p)) return p;
                // an undecodable primary item (another codec): the first decodable alternative of its 'altr' group (MIAF
                // 7.3.12.2, HEIF 10.2.6.3; avifdec 1.4.2 gives up instead)
                foreach (var (type, _, entities) in File.Groups)
                    if (type == "altr" && Array.IndexOf(entities, p.Id) >= 0)
                        foreach (var id in entities)
                            if (id != p.Id && File.Items.TryGetValue(id, out var a) && CanDecode(a)) return a;
                return p;
            }
        }

        /// <summary>Whether the item is an image this reader decodes: AV1, or a derivation of such images.</summary>
        public bool CanDecode(HeifItem it, int depth = 0)
        {
            if (depth > 8) return false;
            foreach (var p in it.Properties)
                if (p.Essential && !knownEssential.Contains(File.Properties[p.Index].Type)) return false;
            switch (it.Type)
            {
                case "av01": return File.Prop(it, "av1C") != null;
                case "grid": case "iden": case "iovl": case "tmap":
                    {
                        var refs = it.Refs("dimg");
                        if (refs.Length == 0) return false;
                        // a tmap item needs its base image only (Decode returns that)
                        foreach (var r in it.Type == "tmap" ? refs[..1] : refs)
                            if (!File.Items.TryGetValue(r, out var s) || !CanDecode(s, depth + 1)) return false;
                        return true;
                    }
                default: return false;
            }
        }

        /// <summary>The colour image sequence track (handler pict / vide, not an auxiliary track), if any.</summary>
        public HeifTrack? ColorTrack
        {
            get
            {
                foreach (var t in File.Tracks)
                    if ((t.Handler == "pict" || t.Handler == "vide") && t.SampleEntryType == "av01" && t.Auxi == null && !IsAuxTrack(t)) return t;
                return null;
            }
        }

        static bool IsAuxTrack(HeifTrack t)
        {
            foreach (var r in t.References) if (r.type == "auxl") return true;
            return t.Handler == "auxv";
        }

        public HeifTrack? AlphaTrack(HeifTrack color)
        {
            foreach (var t in File.Tracks)
            {
                if (t == color || t.SampleEntryType != "av01") continue;
                foreach (var r in t.References)
                    if (r.type == "auxl" && Array.IndexOf(r.ids, color.TrackId) >= 0 && (t.AuxType == AlphaUrn || t.AuxType == HevcAlphaUrn || t.Auxi == null))
                        return t;
            }
            return null;
        }

        // ------------------------------------------------------------------ properties

        public string AuxType(HeifItem it)
        {
            if (File.Prop(it, "auxC") is not HeifProperty p) return "";
            var r = new IsoReader(Data, p.Payload, p.End);
            r.FullBox();
            return r.CString();
        }

        public (int w, int h)? Ispe(HeifItem it)
        {
            if (File.Prop(it, "ispe") is not HeifProperty p) return null;
            var r = new IsoReader(Data, p.Payload, p.End);
            r.FullBox();
            return ((int)r.U32(), (int)r.U32());
        }

        public int[]? Pixi(HeifItem it)
        {
            if (File.Prop(it, "pixi") is not HeifProperty p) return null;
            var r = new IsoReader(Data, p.Payload, p.End);
            r.FullBox();
            int n = r.U8();
            var bits = new int[n];
            for (int i = 0; i < n; i++) bits[i] = r.U8();
            return bits;
        }

        public Av1Config? Av1C(HeifItem it) => File.Prop(it, "av1C") is HeifProperty p ? Av1Config.Parse(Data, p.Payload, p.End) : null;

        public HeifColour Colour(HeifItem it)
        {
            var c = new HeifColour();
            foreach (var pr in it.Properties)
            {
                var p = File.Properties[pr.Index];
                if (p.Type != "colr") continue;
                ReadColr(Data, p.Payload, p.End, c);
            }
            return c;
        }

        public static void ReadColr(ReadOnlySpan<byte> d, long payload, long end, HeifColour c)
        {
            var r = new IsoReader(d, payload, end);
            string type = r.FourCC();
            if (type == "nclx" && !c.HasNclx)
            {
                c.HasNclx = true;
                c.Primaries = r.U16();
                c.Transfer = r.U16();
                c.Matrix = r.U16();
                c.FullRange = (r.U8() & 0x80) != 0;
            }
            else if ((type == "prof" || type == "rICC") && c.Icc == null)
                c.Icc = r.Bytes(r.End - r.Pos);
        }

        public List<HeifTransform> Transforms(HeifItem it)
        {
            var list = new List<HeifTransform>();
            foreach (var pr in it.Properties)
            {
                var p = File.Properties[pr.Index];
                var r = new IsoReader(Data, p.Payload, p.End);
                switch (p.Type)
                {
                    case "clap":
                        {
                            var f = new int[8];
                            for (int i = 0; i < 8; i++) f[i] = r.I32();
                            list.Add(new HeifTransform('c', 0, f));
                            break;
                        }
                    case "irot": list.Add(new HeifTransform('r', r.U8() & 3)); break;
                    case "imir": list.Add(new HeifTransform('m', r.U8() & 1)); break;
                }
            }
            // clap, then irot, then imir whatever the association order, as avifdec 1.4.2 does (and MIAF 7.3.6.7 requires
            // of files; HEIF 6.5.1 applies them in association order): the clap is on the stored image
            static int Rank(HeifTransform t) => t.Kind == 'c' ? 0 : t.Kind == 'r' ? 1 : 2;
            var ordered = new List<HeifTransform>(list.Count);
            for (int k = 0; k < 3; k++) foreach (var t in list) if (Rank(t) == k) ordered.Add(t);
            return ordered;
        }

        /// <summary>The crop rectangle of a clap on an image of the given size (ISO/IEC 14496-12 12.1.4), null if it is invalid.</summary>
        public static (int x, int y, int w, int h)? Clap(int[] f, int imageW, int imageH)
        {
            int wn = f[0], wd = f[1], hn = f[2], hd = f[3], hon = f[4], hod = f[5], von = f[6], vod = f[7];
            if (wd <= 0 || hd <= 0 || hod <= 0 || vod <= 0 || wn <= 0 || hn <= 0) return null;
            if (wn % wd != 0 || hn % hd != 0) return null;
            long cw = wn / wd, ch = hn / hd;
            // left = horizOff + (imageW - 1) / 2 - (cw - 1) / 2, computed as an exact fraction
            // 2 * left = 2 * horizOff + imageW - cw
            long numX = 2L * hon * 1 + (long)(imageW - cw) * hod;   // over 2 * hod
            long numY = 2L * von * 1 + (long)(imageH - ch) * vod;
            if (numX % (2L * hod) != 0 || numY % (2L * vod) != 0) return null;
            long x = numX / (2L * hod), y = numY / (2L * vod);
            if (x < 0 || y < 0 || x + cw > imageW || y + ch > imageH || cw <= 0 || ch <= 0) return null;
            return ((int)x, (int)y, (int)cw, (int)ch);
        }

        // ------------------------------------------------------------------ decoding

        public static bool IsAlpha(string auxType) => auxType == AlphaUrn || auxType == HevcAlphaUrn;

        public HeifItem? AlphaItem(HeifItem master)
        {
            foreach (var it in File.Referencing(master.Id, "auxl"))
                if (IsAlpha(AuxType(it))) return it;
            return null;
        }

        /// <summary>
        /// The alpha of a grid without an alpha item of its own: an alpha item on every tile (in tile order), laid out like
        /// the colour tiles; null when a tile has none.
        /// </summary>
        public HeifItem[]? AlphaTiles(HeifItem grid)
        {
            if (grid.Type != "grid") return null;
            var refs = grid.Refs("dimg");
            if (refs.Length == 0) return null;
            var r = new HeifItem[refs.Length];
            for (int i = 0; i < refs.Length; i++)
                if (!File.Items.TryGetValue(refs[i], out var t) || AlphaItem(t) is not HeifItem a) return null;
                else r[i] = a;
            return r;
        }

        /// <summary>Whether the item's output image has alpha: an alpha item of its own, or one on each tile of a grid.</summary>
        public bool HasAlpha(HeifItem it) => AlphaItem(it) != null || AlphaTiles(it) != null;

        public bool IsPremultiplied(HeifItem master, HeifItem alpha)
        {
            var refs = master.Refs("prem");
            return Array.IndexOf(refs, alpha.Id) >= 0;
        }

        /// <summary>Decodes an image item to planes (AV1 or grid of AV1 items).</summary>
        public YuvImage DecodeYuv(HeifItem it, int depth = 0)
        {
            if (depth > 8) throw new ImageFormatException("HEIF: derivation chain too deep");
            CheckEssential(it);
            switch (it.Type)
            {
                case "av01": return DecodeAv1Item(it);
                case "grid": return DecodeGrid(it, depth);
                default: throw new ImageNotSupportedException($"AVIF: items of type '{it.Type}' are not supported");
            }
        }

        static readonly HashSet<string> knownEssential = new() { "av1C", "ispe", "pixi", "colr", "auxC", "irot", "imir", "clap", "lsel", "a1op", "a1lx", "clli", "mdcv", "cclv", "pasp", "hdlp", "rloc" };

        void CheckEssential(HeifItem it)
        {
            foreach (var p in it.Properties)
                if (p.Essential && !knownEssential.Contains(File.Properties[p.Index].Type))
                    throw new ImageNotSupportedException($"AVIF: essential property '{File.Properties[p.Index].Type}' is not supported");
        }

        YuvImage DecodeAv1Item(HeifItem it)
        {
            var data = File.ItemData(Data, it);
            int op = 0;
            if (File.Prop(it, "a1op") is HeifProperty a1op) op = new IsoReader(Data, a1op.Payload, a1op.End).U8();
            int layer = 0xFFFF;
            if (File.Prop(it, "lsel") is HeifProperty lsel) layer = new IsoReader(Data, lsel.Payload, lsel.End).U16();
            var dec = new Av1Decoder(new Av1DecoderOptions
            {
                OperatingPoint = op, OutputAllLayers = layer != 0xFFFF, ApplyFilmGrain = opt.ApplyFilmGrain,
                Multithreaded = opt.Multithreaded, MaxPixels = opt.MaxPixels,
            });
            dec.Decode(data, 0, data.Length);
            if (dec.Output.Count == 0) throw new ImageFormatException("AVIF: the AV1 item has no shown frame");
            Av1Picture pic = dec.Output[^1];
            if (layer != 0xFFFF)
            {
                Av1Picture? sel = null;
                foreach (var o in dec.Output) if (o.SpatialId == layer) sel = o;
                pic = sel ?? throw new ImageFormatException($"AVIF: layer {layer} produced no frame");
            }
            var y = FromPicture(pic);
            var c = Colour(it);
            // without an nclx property the AV1 sequence header's colour description stays, as avifdec 1.4.2 does (MIAF
            // 7.3.6.4 defaults to nclx 1 / 13 / 6, full range, and ignores the bitstream's)
            if (c.HasNclx)
            {
                y.ColorPrimaries = c.Primaries; y.TransferCharacteristics = c.Transfer; y.MatrixCoefficients = c.Matrix; y.FullRange = c.FullRange;
            }
            return y;
        }

        public static YuvImage FromPicture(Av1Picture pic)
        {
            var f = pic.Frame;
            var y = new YuvImage
            {
                Width = pic.Width, Height = pic.Height, BitDepth = pic.BitDepth, SubX = pic.SubX, SubY = pic.SubY, Mono = pic.Monochrome,
                ColorPrimaries = pic.ColorPrimaries, TransferCharacteristics = pic.TransferCharacteristics,
                MatrixCoefficients = pic.MatrixCoefficients, FullRange = pic.FullRange,
            };
            for (int p = 0; p < (pic.Monochrome ? 1 : 3); p++)
            {
                y.Planes[p] = f.Planes[p];
                y.Stride[p] = f.Stride[p];
            }
            return y;
        }

        /// <summary>The grid item's rows, columns and output size (HEIF 6.6.2.3).</summary>
        (int rows, int cols, int ow, int oh) GridLayout(HeifItem it)
        {
            var payload = File.ItemData(Data, it);
            if (payload.Length < 8 || payload[0] != 0) throw new ImageFormatException("AVIF: bad grid item");
            int flags = payload[1];
            int rows = payload[2] + 1, cols = payload[3] + 1;
            int ow, oh;
            if ((flags & 1) != 0)
            {
                if (payload.Length < 12) throw new ImageFormatException("AVIF: bad grid item");
                ow = (int)BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(4));
                oh = (int)BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(8));
            }
            else
            {
                ow = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(4));
                oh = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(6));
            }
            if (ow <= 0 || oh <= 0) throw new ImageFormatException("AVIF: empty grid");
            if ((long)ow * oh > opt.MaxPixels) throw new ImageNotSupportedException("AVIF: grid exceeds the pixel limit");
            if (it.Refs("dimg").Length != rows * cols) throw new ImageFormatException("AVIF: grid tile count does not match");
            return (rows, cols, ow, oh);
        }

        /// <summary>
        /// A grid of its dimg tiles, or of the given items in their place (the alpha items of the tiles). The tiles' own
        /// clap / irot / imir are ignored, as avifdec 1.4.2 does: HEIF 6.6.1 takes the output images of the inputs.
        /// </summary>
        YuvImage DecodeGrid(HeifItem it, int depth, HeifItem[]? tiles = null)
        {
            var (rows, cols, ow, oh) = GridLayout(it);
            var refs = it.Refs("dimg");
            YuvImage? outp = null;
            int tw = 0, th = 0;
            for (int i = 0; i < refs.Length; i++)
            {
                HeifItem? ti = tiles?[i];
                if (ti == null && !File.Items.TryGetValue(refs[i], out ti)) throw new ImageFormatException("AVIF: missing grid tile");
                var t = DecodeYuv(ti, depth + 1);
                if (outp == null)
                {
                    tw = t.Width; th = t.Height;
                    if ((long)tw * cols < ow || (long)th * rows < oh) throw new ImageFormatException("AVIF: grid tiles do not cover the canvas");
                    outp = YuvImage.Create(ow, oh, t.BitDepth, t.SubX, t.SubY, t.Mono);
                    outp.ColorPrimaries = t.ColorPrimaries; outp.TransferCharacteristics = t.TransferCharacteristics;
                    outp.MatrixCoefficients = t.MatrixCoefficients; outp.FullRange = t.FullRange;
                }
                else if (t.Width != tw || t.Height != th || t.BitDepth != outp.BitDepth || t.SubX != outp.SubX || t.SubY != outp.SubY || t.Mono != outp.Mono)
                    throw new ImageFormatException("AVIF: grid tiles differ in size or format");
                int gx = (i % cols) * tw, gy = (i / cols) * th;
                for (int p = 0; p < outp.NumPlanes; p++)
                {
                    int sx = p == 0 ? 0 : outp.SubX, sy = p == 0 ? 0 : outp.SubY;
                    int px0 = gx >> sx, py0 = gy >> sy;
                    int pw = Math.Min(t.PlaneWidth(p), outp.PlaneWidth(p) - px0);
                    int ph = Math.Min(t.PlaneHeight(p), outp.PlaneHeight(p) - py0);
                    for (int yy = 0; yy < ph; yy++)
                        Array.Copy(t.Planes[p], yy * t.Stride[p], outp.Planes[p], (py0 + yy) * outp.Stride[p] + px0, Math.Max(0, pw));
                }
            }
            var c = Colour(it);
            if (c.HasNclx && tiles == null)
            {
                outp!.ColorPrimaries = c.Primaries; outp.TransferCharacteristics = c.Transfer; outp.MatrixCoefficients = c.Matrix; outp.FullRange = c.FullRange;
            }
            return outp!;
        }

        bool HasIdenTile(HeifItem grid)
        {
            foreach (var r in grid.Refs("dimg"))
                if (File.Items.TryGetValue(r, out var t) && t.Type == "iden") return true;
            return false;
        }

        /// <summary>
        /// A grid with 'iden' tiles (MIAF 7.3.11.4.1; avifdec 1.4.2 refuses them): the tiles' output images (an iden item's
        /// transforms applied, a coded tile's ignored as in <see cref="DecodeGrid"/>) pasted on the canvas.
        /// </summary>
        RawImage DecodeGridOfOutputs(HeifItem it, int depth)
        {
            var (_, cols, ow, oh) = GridLayout(it);
            var refs = it.Refs("dimg");
            RawImage? outp = null;
            int tw = 0, th = 0;
            for (int i = 0; i < refs.Length; i++)
            {
                if (!File.Items.TryGetValue(refs[i], out var ti)) throw new ImageFormatException("AVIF: missing grid tile");
                var t = DecodeItem(ti, true, depth + 1, stored: ti.Type != "iden");
                if (outp == null)
                {
                    tw = t.Width; th = t.Height;
                    if ((long)tw * cols < ow || (long)th * (refs.Length / cols) < oh) throw new ImageFormatException("AVIF: grid tiles do not cover the canvas");
                    outp = RawImage.Create(ow, oh, t.Color, t.SampleType, t.Channels);
                    outp.Metadata = t.Metadata;
                }
                else if (t.Width != tw || t.Height != th || t.Color != outp.Color || t.SampleType != outp.SampleType)
                    throw new ImageFormatException("AVIF: grid tiles differ in size or format");
                int gx = (i % cols) * tw, gy = (i / cols) * th;
                int bpp = t.GetBytes().Length / (tw * th);
                var src = t.GetBytes();
                var dst = outp.GetBytes();
                int w = Math.Min(tw, ow - gx);
                for (int y = 0; y < th && gy + y < oh; y++)
                    if (w > 0) src.Slice(y * tw * bpp, w * bpp).CopyTo(dst.Slice(((gy + y) * ow + gx) * bpp));
            }
            return outp!;
        }

        /// <summary>
        /// Decodes an item to its output image: colour + alpha, then its transformative properties (none at all with
        /// <paramref name="stored"/>, not even the clean aperture).
        /// </summary>
        public RawImage DecodeItem(HeifItem it, bool applyTransforms, int depth = 0, bool stored = false)
        {
            if (depth > 8) throw new ImageFormatException("HEIF: derivation chain too deep");
            RawImage img;
            switch (it.Type)
            {
                case "iden":
                    {
                        var r = it.Refs("dimg");
                        if (r.Length != 1 || !File.Items.TryGetValue(r[0], out var src)) throw new ImageFormatException("HEIF: bad iden item");
                        img = DecodeItem(src, true, depth + 1);
                        break;
                    }
                case "iovl": img = DecodeOverlay(it, depth); break;
                case "grid" when HasIdenTile(it):
                    CheckEssential(it);
                    if (!opt.IgnoreAlpha && AlphaItem(it) != null) throw new ImageNotSupportedException("AVIF: an alpha item on a grid of iden tiles");
                    img = DecodeGridOfOutputs(it, depth);
                    break;
                case "tmap":
                    {
                        var r = it.Refs("dimg");
                        if (r.Length < 1 || !File.Items.TryGetValue(r[0], out var src)) throw new ImageFormatException("AVIF: bad tmap item");
                        if (stored) return DecodeItem(src, false, depth + 1, stored: true);
                        // the base image's output image, then the tmap item's own transforms (HEIF Amd 1 6.6.2.4, 6.6.1;
                        // avifdec 1.4.2 rejects transforms on a tmap item and a tmap primary item)
                        img = ApplyTransforms(it, DecodeItem(src, true, depth + 1), true);
                        if (!applyTransforms && Orientation(src, it) is int o && o > 1)
                        {
                            var meta = img.Metadata;
                            img = ImageOps.ApplyOrientation(img, InverseOrientation(o));
                            img.Metadata = meta;
                            if (meta != null) meta.Orientation = o;
                        }
                        return img;
                    }
                default:
                    {
                        var y = DecodeYuv(it, depth);
                        YuvImage? a = null;
                        bool prem = false;
                        if (!opt.IgnoreAlpha && AlphaItem(it) is HeifItem ai)
                        {
                            a = DecodeYuv(ai, depth);
                            if (a.Width != y.Width || a.Height != y.Height) a = ResizeNearest(a, y.Width, y.Height);
                            prem = IsPremultiplied(it, ai);
                        }
                        else if (!opt.IgnoreAlpha && AlphaTiles(it) is HeifItem[] at)
                        {
                            // alpha on the tiles only (auxl from each alpha tile to its colour tile): the grid of those
                            a = DecodeGrid(it, depth, at);
                            if (a.Width != y.Width || a.Height != y.Height) a = ResizeNearest(a, y.Width, y.Height);
                            var tiles = it.Refs("dimg");
                            prem = true;
                            for (int i = 0; i < at.Length; i++) prem &= IsPremultiplied(File.Items[tiles[i]], at[i]);
                        }
                        long t0 = Av1Stats.TimeOn ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                        img = AvifColor.ToRaw(y, a, prem, opt.Multithreaded);
                        Av1Stats.Time("time.rgb", t0);
                        if (Av1Stats.TimeOn) Av1Stats.Print();
                        img.Metadata = ColourMetadata(y, Colour(it));
                        break;
                    }
            }
            return stored ? img : ApplyTransforms(it, img, applyTransforms);
        }

        RawImage ApplyTransforms(HeifItem it, RawImage img, bool orientation)
        {
            var meta = img.Metadata;
            foreach (var t in Transforms(it))
            {
                switch (t.Kind)
                {
                    case 'c':
                        if (opt.ApplyCrop && Clap(t.Clap!, img.Width, img.Height) is var (x, y, w, h)) img = ImageOps.Crop(img, x, y, w, h);
                        break;
                    case 'r':
                        if (!orientation) break;
                        img = t.A switch { 1 => ImageOps.Rotate270(img), 2 => ImageOps.Rotate180(img), 3 => ImageOps.Rotate90(img), _ => img };
                        break;
                    case 'm':
                        if (!orientation) break;
                        img = t.A == 0 ? ImageOps.FlipVertical(img) : ImageOps.FlipHorizontal(img);
                        break;
                }
            }
            img.Metadata = meta;
            if (meta != null) meta.Orientation = orientation ? 1 : Orientation(it);
            return img;
        }

        /// <summary>The EXIF orientation that undoes orientation <paramref name="o"/>.</summary>
        public static int InverseOrientation(int o) => o == 6 ? 8 : o == 8 ? 6 : o;

        /// <summary>The EXIF orientation the irot / imir properties of the items, applied one after the other, amount to (1 if none).</summary>
        public int Orientation(params HeifItem[] chain)
        {
            // apply the transforms to a 3x2 marker image and find the EXIF orientation that does the same
            var probe = new RawImage(3, 2, ColorModel.Gray, new byte[] { 0, 1, 2, 3, 4, 5 });
            var t = probe;
            bool any = false;
            foreach (var it in chain)
            foreach (var x in Transforms(it))
            {
                if (x.Kind == 'r') { any = true; t = x.A switch { 1 => ImageOps.Rotate270(t), 2 => ImageOps.Rotate180(t), 3 => ImageOps.Rotate90(t), _ => t }; }
                else if (x.Kind == 'm') { any = true; t = x.A == 0 ? ImageOps.FlipVertical(t) : ImageOps.FlipHorizontal(t); }
            }
            if (!any) return 1;
            for (int o = 1; o <= 8; o++)
            {
                var r = ImageOps.ApplyOrientation(probe, o);
                if (r.Width == t.Width && ((byte[])r.Data).AsSpan().SequenceEqual((byte[])t.Data)) return o;
            }
            return 1;
        }

        static YuvImage ResizeNearest(YuvImage a, int w, int h)
        {
            var r = YuvImage.Create(w, h, a.BitDepth, 1, 1, true);
            r.FullRange = true;
            for (int y = 0; y < h; y++)
            {
                int sy = (int)((long)y * a.Height / h);
                for (int x = 0; x < w; x++) r.Planes[0][y * w + x] = a.Planes[0][sy * a.Stride[0] + (int)((long)x * a.Width / w)];
            }
            return r;
        }

        RawImage DecodeOverlay(HeifItem it, int depth)
        {
            var payload = File.ItemData(Data, it);
            if (payload.Length < 2 || payload[0] != 0) throw new ImageFormatException("HEIF: bad iovl item");
            int flags = payload[1];
            int fl = (flags & 1) != 0 ? 4 : 2;
            var refs = it.Refs("dimg");
            int need = 2 + 8 + 2 * fl + refs.Length * 2 * fl;
            if (payload.Length < need) throw new ImageFormatException("HEIF: truncated iovl item");
            var fill = new int[4];
            for (int j = 0; j < 4; j++) fill[j] = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(2 + 2 * j));
            int p = 10;
            long Field(bool signed)
            {
                long v = fl == 4 ? (signed ? BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(p)) : BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(p)))
                    : (signed ? BinaryPrimitives.ReadInt16BigEndian(payload.AsSpan(p)) : BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(p)));
                p += fl;
                return v;
            }
            int ow = (int)Field(false), oh = (int)Field(false);
            if (ow <= 0 || oh <= 0 || (long)ow * oh > opt.MaxPixels) throw new ImageNotSupportedException("HEIF: overlay canvas size");
            var layers = new List<(RawImage img, long x, long y, int bits)>();
            int bits = 8;
            bool alpha = false;
            foreach (var id in refs)
            {
                long hx = Field(true), vy = Field(true);
                if (!File.Items.TryGetValue(id, out var li)) throw new ImageFormatException("HEIF: missing overlay input");
                var li2 = DecodeItem(li, true, depth + 1);
                int lb = ItemBitDepth(li);
                bits = Math.Max(bits, lb);
                alpha |= li2.HasAlpha;
                layers.Add((li2, hx, vy, lb));
            }
            // compose in RGBA of the deepest input (samples above 8 bit keep their stored range)
            bool wide = bits > 8;
            int maxStored = (1 << bits) - 1;
            var canvas = RawImage.Create(ow, oh, ColorModel.Rgba, wide ? SampleType.UInt16 : SampleType.UInt8);
            var cf = new double[4];
            for (int j = 0; j < 4; j++) cf[j] = fill[j] / 65535.0;
            var acc = new double[(long)ow * oh * 4];
            for (long i = 0; i < (long)ow * oh; i++) for (int j = 0; j < 4; j++) acc[i * 4 + j] = cf[j];
            Span<double> px = stackalloc double[4];
            foreach (var (li, ox, oy, lb) in layers)
            {
                double scale = (1 << lb) - 1;
                for (int y = 0; y < li.Height; y++)
                {
                    long cy = oy + y;
                    if (cy < 0 || cy >= oh) continue;
                    for (int x = 0; x < li.Width; x++)
                    {
                        long cx = ox + x;
                        if (cx < 0 || cx >= ow) continue;
                        Sample(li, x, y, scale, px);
                        long o = (cy * ow + cx) * 4;
                        // later inputs over earlier ones (straight alpha "over")
                        double a = px[3], below = acc[o + 3] * (1 - a), outA = a + below;
                        for (int j = 0; j < 3; j++) acc[o + j] = outA > 0 ? (px[j] * a + acc[o + j] * below) / outA : 0;
                        acc[o + 3] = outA;
                    }
                }
            }
            double outMax = wide ? maxStored : 255;
            for (long i = 0; i < (long)ow * oh; i++)
            {
                for (int j = 0; j < 4; j++)
                {
                    double v = acc[i * 4 + j];
                    int q = (int)Math.Round(Math.Clamp(v, 0, 1) * outMax);
                    if (wide) ((ushort[])canvas.Data)[i * 4 + j] = (ushort)q;
                    else ((byte[])canvas.Data)[i * 4 + j] = (byte)q;
                }
            }
            if (!alpha && fill[3] == 65535) canvas = DropAlpha(canvas);
            canvas.Metadata = layers.Count > 0 ? layers[0].img.Metadata : null;
            return canvas;
        }

        /// <summary>Bit depth of the samples an item decodes to (av1C of the item or of its first input).</summary>
        public int ItemBitDepth(HeifItem it, int depth = 0)
        {
            if (Av1C(it) is Av1Config c) return c.BitDepth;
            if (depth < 8)
                foreach (var r in it.Refs("dimg"))
                    if (File.Items.TryGetValue(r, out var src)) return ItemBitDepth(src, depth + 1);
            if (Pixi(it) is int[] px && px.Length > 0) return px[0];
            return 8;
        }

        static void Sample(RawImage img, int x, int y, double scale, Span<double> px)
        {
            int c = img.Channels;
            long o = ((long)y * img.Width + x) * c;
            double V(int k) => img.SampleType == SampleType.UInt8 ? ((byte[])img.Data)[o + k] / scale : ((ushort[])img.Data)[o + k] / scale;
            switch (img.Color)
            {
                case ColorModel.Gray: px[0] = px[1] = px[2] = V(0); px[3] = 1; break;
                case ColorModel.GrayAlpha: px[0] = px[1] = px[2] = V(0); px[3] = V(1); break;
                case ColorModel.Rgb: px[0] = V(0); px[1] = V(1); px[2] = V(2); px[3] = 1; break;
                default: px[0] = V(0); px[1] = V(1); px[2] = V(2); px[3] = V(3); break;
            }
        }

        static RawImage DropAlpha(RawImage rgba)
        {
            var r = RawImage.Create(rgba.Width, rgba.Height, ColorModel.Rgb, rgba.SampleType);
            long n = (long)rgba.Width * rgba.Height;
            if (rgba.SampleType == SampleType.UInt8)
            {
                var s = (byte[])rgba.Data; var d = (byte[])r.Data;
                for (long i = 0; i < n; i++) { d[i * 3] = s[i * 4]; d[i * 3 + 1] = s[i * 4 + 1]; d[i * 3 + 2] = s[i * 4 + 2]; }
            }
            else
            {
                var s = (ushort[])rgba.Data; var d = (ushort[])r.Data;
                for (long i = 0; i < n; i++) { d[i * 3] = s[i * 4]; d[i * 3 + 1] = s[i * 4 + 1]; d[i * 3 + 2] = s[i * 4 + 2]; }
            }
            return r;
        }

        // ------------------------------------------------------------------ colour metadata

        /// <summary>Transfer function of an H.273 transfer_characteristics value as the library reports it.</summary>
        public static TransferFunction TransferOf(int tc, out double gamma)
        {
            gamma = 0;
            switch (tc)
            {
                case 13: return TransferFunction.Srgb;
                case 8: return TransferFunction.Linear;
                case 16: return TransferFunction.Pq;
                case 18: return TransferFunction.Hlg;
                case 4: gamma = 1 / 2.2; return TransferFunction.Gamma;
                case 5: gamma = 1 / 2.8; return TransferFunction.Gamma;
                default: return TransferFunction.Unknown;
            }
        }

        public static ImageMetadata ColourMetadata(YuvImage y, HeifColour c)
        {
            var m = new ImageMetadata();
            if (c.Icc != null)
            {
                m.IccProfile = c.Icc;
                m.Transfer = TransferOf(y.TransferCharacteristics, out m.Gamma);
                if (m.Transfer == TransferFunction.Unknown) m.Transfer = TransferFunction.Srgb;
                return m;
            }
            int cp = y.ColorPrimaries, tc = y.TransferCharacteristics;
            var tf = TransferOf(tc, out double g);
            if (tc == 2 || tc == 0) tf = TransferFunction.Srgb;   // unspecified: sRGB, as AVIF readers assume
            m.Transfer = tf;
            m.Gamma = g;
            m.IccProfile = DescribingIcc(cp, tc, y.Mono);
            return m;
        }

        /// <summary>
        /// An ICC profile for nclx primaries other than BT.709 / sRGB (or a curve the enum cannot name); null when none is
        /// needed. PQ and HLG get a version 4.4 profile with a cicp tag of the code points (their primaries otherwise lost).
        /// </summary>
        public static byte[]? DescribingIcc(int cp, int tc, bool gray)
        {
            IccCurve trc;
            switch (tc)
            {
                case 16: case 18:
                    {
                        bool known = AvifColor.Primaries(cp is 0 or 2 ? 1 : cp, out var q);
                        if (!known) return null;
                        try
                        {
                            return IccProfile.CreateHdr(tc == 16 ? "AVIF PQ" : "AVIF HLG", new CieXy(q[6], q[7]), new CieXy(q[0], q[1]), new CieXy(q[2], q[3]),
                                new CieXy(q[4], q[5]), gray, gray ? 2 : cp, tc)?.ToBytes();
                        }
                        catch (Exception e) when (e is ArgumentException or ImageFormatException) { return null; }
                    }
                case 13: case 2: case 0: trc = IccCurve.Srgb; break;
                case 8: trc = IccCurve.Identity; break;
                case 4: trc = IccCurve.Gamma(2.2); break;
                case 5: trc = IccCurve.Gamma(2.8); break;
                case 1: case 6: case 14: case 15: trc = IccCurve.Parametric(3, 1 / 0.45, 1 / 1.099, 0.099 / 1.099, 1 / 4.5, 0.081); break;
                default: return null;
            }
            bool plainCurve = tc is 13 or 2 or 0 or 8;
            bool srgbPrimaries = cp is 1 or 2 or 0;
            if (plainCurve && (gray || srgbPrimaries)) return null;
            if (!AvifColor.Primaries(srgbPrimaries ? 1 : cp, out var p)) return null;
            try
            {
                var white = new CieXy(p[6], p[7]);
                if (gray) return IccProfile.CreateGray("AVIF gray", white, trc).ToBytes();
                return IccProfile.CreateRgb("AVIF RGB", white, new CieXy(p[0], p[1]), new CieXy(p[2], p[3]), new CieXy(p[4], p[5]), trc).ToBytes();
            }
            catch (Exception e) when (e is ArgumentException or ImageFormatException) { return null; }
        }

        // ------------------------------------------------------------------ metadata items

        public (byte[]? exif, byte[]? xmp) MetadataItems(HeifItem? primary)
        {
            byte[]? exif = null, xmp = null;
            foreach (var id in File.ItemOrder)
            {
                var it = File.Items[id];
                bool describesPrimary = primary == null || Array.IndexOf(it.Refs("cdsc"), primary.Id) >= 0 || it.Refs("cdsc").Length == 0;
                if (!describesPrimary) continue;
                if (it.Type == "Exif" && exif == null)
                {
                    try
                    {
                        var d = File.ItemData(Data, it);
                        if (d.Length >= 4)
                        {
                            uint off = BinaryPrimitives.ReadUInt32BigEndian(d);
                            if (4 + (long)off < d.Length) exif = d.AsSpan(4 + (int)off).ToArray();
                        }
                    }
                    catch (ImageFormatException) { }
                }
                else if (it.Type == "mime" && xmp == null && it.ContentType.StartsWith("application/rdf+xml", StringComparison.OrdinalIgnoreCase))
                {
                    try { xmp = File.ItemData(Data, it); } catch (ImageFormatException) { }
                }
            }
            return (exif, xmp);
        }
    }
}
