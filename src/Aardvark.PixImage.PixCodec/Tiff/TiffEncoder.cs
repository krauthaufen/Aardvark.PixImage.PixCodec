using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Aardvark.ImageCodecs.Compression;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// TIFF writer. File layout: header, then per image its directory (with the out-of-line values and EXIF
    /// directories) followed by its strip/tile data. A directory needs the sizes of the compressed strips, so
    /// compressed output to a non-seekable stream is buffered one image at a time; a seekable stream gets a
    /// placeholder directory that is rewritten once the data is out, and uncompressed images have a layout that is
    /// known up front. All three ways produce the same bytes.
    /// </summary>
    internal static unsafe class TiffEncoder
    {
        struct Chunk
        {
            public byte[] Buf;
            public int Len;
            public bool Pooled;
        }

        const string TooBig = "TIFF: the output exceeds 4 GB, which needs BigTiff (leave BigTiff unset or set it to true)";
        const int None = 1, CcittRle = 2, Ccitt3 = 3, Ccitt4 = 4, Lzw = 5, JpegComp = 7, Deflate = 8, PackBits = 32773,
            ZstdComp = 50000, LzmaComp = 34925, WebpComp = 50001, Jp2Comp = 34712, JbigComp = 34661;

        /// <summary>Everything derived from one image and its options before a byte is written.</summary>
        sealed class Layout
        {
            public RawImage Image = null!;
            public TiffEncodeOptions Options = null!;
            public int W, H, Spp, Bps, Bits, Comp, Pred, Cw, Ch, Across, Down, PerPlane, N, Photometric;
            public bool Tiled, Bilevel, Packed, Planar, Invert, YccPacked, Direct, Streamed, Signed, IsFloat;
            public long RowBytes, ChunkRowBytes, RawTotal;
            public int SubH = 1, SubV = 1;
            public int[] Extra = Array.Empty<int>();
            public ushort[]? ColorMap;
            public JpegEncodeOptions? Jpeg;
            public long[] Offsets = Array.Empty<long>(), Counts = Array.Empty<long>();

            public bool IsCcitt => Comp is CcittRle or Ccitt3 or Ccitt4;
            /// <summary>Samples per pixel inside one strip / tile.</summary>
            public int Ps => Planar ? 1 : Spp;

            public Layout(RawImage image, TiffEncodeOptions o, bool bigEndian)
            {
                Image = image ?? throw new ArgumentNullException(nameof(image));
                Options = o;
                W = image.Width; H = image.Height; Spp = image.Channels;
                var type = image.SampleType;
                Bps = RawImage.BytesPerSample(type);
                IsFloat = type is SampleType.Float16 or SampleType.Float32 or SampleType.Float64;
                Signed = type is SampleType.Int8 or SampleType.Int16 or SampleType.Int32 or SampleType.Int64;
                Comp = o.Compression switch
                {
                    TiffCompression.None => None,
                    TiffCompression.Lzw => Lzw,
                    TiffCompression.Deflate => Deflate,
                    TiffCompression.PackBits => PackBits,
                    TiffCompression.Jpeg => JpegComp,
                    TiffCompression.CcittGroup4 => Ccitt4,
                    TiffCompression.CcittGroup3 => Ccitt3,
                    TiffCompression.CcittRle => CcittRle,
                    TiffCompression.Zstd => ZstdComp,
                    TiffCompression.Lzma => LzmaComp,
                    TiffCompression.Webp => WebpComp,
                    TiffCompression.Jpeg2000 => Jp2Comp,
                    TiffCompression.Jbig => JbigComp,
                    _ => throw new ArgumentException("unknown TIFF compression"),
                };
                Planar = o.Planar && Spp > 1;

                // ---- bit depth
                Bilevel = o.Bilevel || IsCcitt || Comp == JbigComp;
                if (Bilevel && (type != SampleType.UInt8 || Spp != 1))
                    throw new ArgumentException("bilevel TIFF output needs an 8-bit single channel image");
                Bits = Bilevel ? 1 : o.BitsPerSample > 0 ? o.BitsPerSample : Bps * 8;
                if (Bits != Bps * 8)
                {
                    if (IsFloat || Bits < 1 || Bits > 32 || Bits > Bps * 8)
                        throw new ArgumentException($"TIFF: {Bits} bits per sample cannot be packed from {type} samples");
                    Packed = true;
                }

                // ---- colour
                var pm = o.Photometric;
                if (pm == TiffPhotometric.Auto && o.Palette != null) pm = TiffPhotometric.Palette;
                int colorSamples;
                switch (pm)
                {
                    case TiffPhotometric.Auto:
                        Photometric = image.Color is ColorModel.Rgb or ColorModel.Rgba ? 2 : 1;
                        colorSamples = Photometric == 2 ? 3 : 1;
                        if (IsCcitt) Photometric = 0; // fax data is MinIsWhite by convention
                        break;
                    case TiffPhotometric.MinIsBlack: Photometric = 1; colorSamples = 1; break;
                    case TiffPhotometric.MinIsWhite: Photometric = 0; colorSamples = 1; break;
                    case TiffPhotometric.TransparencyMask: Photometric = 4; colorSamples = 1; break;
                    case TiffPhotometric.Rgb: Photometric = 2; colorSamples = 3; break;
                    case TiffPhotometric.Palette:
                        Photometric = 3; colorSamples = 1;
                        if (o.Palette == null || IsFloat || Signed || Bits > 16) throw new ArgumentException("TIFF: a palette image needs a Palette and unsigned indices of up to 16 bits");
                        // TIFF keeps the map as all reds, all greens, all blues
                        int entries = 1 << Bits;
                        ColorMap = new ushort[3 * entries];
                        for (int i = 0; i < Math.Min(entries, o.Palette.Length / 3); i++)
                            for (int c = 0; c < 3; c++) ColorMap[c * entries + i] = o.Palette[i * 3 + c];
                        break;
                    case TiffPhotometric.Cmyk:
                        Photometric = 5; colorSamples = 4;
                        if (!(type is SampleType.UInt8 or SampleType.UInt16) || Packed) throw new ArgumentException("TIFF: CMYK output needs 8 or 16 bit unsigned samples");
                        break;
                    case TiffPhotometric.CieLab: case TiffPhotometric.IccLab: case TiffPhotometric.ItuLab:
                        Photometric = pm == TiffPhotometric.CieLab ? 8 : pm == TiffPhotometric.IccLab ? 9 : 10; colorSamples = 3;
                        if (!(type is SampleType.UInt8 or SampleType.UInt16) || Packed) throw new ArgumentException("TIFF: Lab output needs 8 or 16 bit unsigned samples");
                        break;
                    case TiffPhotometric.YCbCr:
                        Photometric = 6; colorSamples = 3;
                        if (type != SampleType.UInt8 && Comp != JpegComp || Spp != 3 || Packed) throw new ArgumentException("TIFF: YCbCr output needs an 8-bit Rgb image");
                        break;
                    default: throw new ArgumentException("unknown TIFF photometric interpretation");
                }
                if (Spp < colorSamples) throw new ArgumentException($"TIFF: photometric {pm} needs at least {colorSamples} samples per pixel, the image has {Spp}");
                // MinIsWhite on purpose: store the complement, so that readers (which invert) see the image
                Invert = Photometric == 0 && !IsFloat && !Signed && !IsCcitt && !Bilevel;
                int extras = Spp - colorSamples;
                if (o.ExtraSamples != null)
                {
                    if (o.ExtraSamples.Length != extras) throw new ArgumentException($"TIFF: {o.ExtraSamples.Length} ExtraSamples given for {extras} extra sample(s)");
                    Extra = Array.ConvertAll(o.ExtraSamples, x => (int)x);
                }
                else
                {
                    Extra = new int[extras];
                    if (extras == 1 && image.HasAlpha) Extra[0] = 2;
                }

                // ---- codecs with their own idea of the samples
                if (Comp == JpegComp)
                {
                    if (!(type is SampleType.UInt8 or SampleType.UInt16) || !(image.Color is ColorModel.Gray or ColorModel.Rgb) || Packed || Planar
                        || !(Photometric is 1 or 2 or 6))
                        throw new ArgumentException("JPEG compressed TIFF output needs an 8-bit (or 12-bit in UInt16) Gray or Rgb image");
                    if (type == SampleType.UInt16) Bits = 12;
                    Jpeg = new JpegEncodeOptions { Quality = o.JpegQuality, Subsampling = o.JpegSubsampling, OptimizeHuffman = true };
                    if (Spp == 3)
                    {
                        // RGB is stored as YCbCr unless the caller wants RGB components
                        if (o.JpegRgb && pm != TiffPhotometric.YCbCr) Photometric = 2;
                        else { Photometric = 6; (SubH, SubV) = Aardvark.ImageCodecs.Jpeg.ResolveSubsampling(Jpeg); }
                    }
                }
                else if (Photometric == 6)
                {
                    // TIFF 6.0 YCbCr without JPEG: blocks of SubH * SubV luma samples followed by Cb and Cr
                    if (Planar || Comp is WebpComp or Jp2Comp) throw new ArgumentException("TIFF: YCbCr output is contiguous and not available with this compression");
                    YccPacked = true;
                    (SubH, SubV) = o.JpegSubsampling switch { JpegSubsampling.Chroma444 => (1, 1), JpegSubsampling.Chroma422 => (2, 1), _ => (2, 2) };
                }
                if (Comp == WebpComp && (type != SampleType.UInt8 || !(image.Color is ColorModel.Rgb or ColorModel.Rgba) || Packed || Planar || Photometric != 2))
                    throw new ArgumentException("WebP compressed TIFF output needs an 8-bit Rgb or Rgba image");
                if (Comp == Jp2Comp && (Packed || YccPacked || Bilevel))
                    throw new ArgumentException("TIFF: this compression needs whole-byte samples");
                if (Comp == Jp2Comp && (IsFloat || Bps > 2)) throw new ArgumentException("JPEG 2000 compressed TIFF output needs 8 or 16 bit integer samples");

                if (o.Predictor == TiffPredictor.FloatingPoint && !IsFloat)
                    throw new ArgumentException("the floating point predictor needs float samples");
                // libtiff implements predictors only for LZW, Deflate, Zstd and LZMA and ignores the tag otherwise, so a
                // predictor combined with PackBits or no compression would make files that most readers show as
                // garbage. Packed samples and YCbCr blocks get none either.
                Pred = !(Comp is Lzw or Deflate or ZstdComp or LzmaComp) || Bilevel || Packed || YccPacked ? 1 : o.Predictor switch
                {
                    TiffPredictor.None => 1,
                    TiffPredictor.Horizontal => 2,
                    TiffPredictor.FloatingPoint => 3,
                    _ => IsFloat ? 3 : 2,
                };

                // ---- strips / tiles
                Tiled = o.TileWidth > 0 && o.TileHeight > 0;
                RowBytes = StoredRowBytes(W);
                if (Tiled)
                {
                    Cw = (o.TileWidth + 15) / 16 * 16; Ch = (o.TileHeight + 15) / 16 * 16;
                }
                else
                {
                    Cw = W;
                    long rps = o.RowsPerStrip > 0 ? o.RowsPerStrip : Math.Max(1, (65536 + RowBytes - 1) / Math.Max(1, RowBytes));
                    // libtiff reads JBIG files of one strip only
                    if (Comp == JbigComp && o.RowsPerStrip <= 0) rps = H;
                    // JPEG strips have to hold whole MCU rows, YCbCr strips whole blocks
                    if (Comp == JpegComp) rps = Math.Max(16, (rps + 15) / 16 * 16);
                    if (YccPacked) rps = (rps + SubV - 1) / SubV * SubV;
                    Ch = (int)Math.Min(rps, H);
                    if (YccPacked && Ch % SubV != 0 && Ch < H) Ch = Math.Max(SubV, Ch / SubV * SubV);
                }
                ChunkRowBytes = StoredRowBytes(Cw);
                // the pixel array already is the file content
                Direct = Comp == None && !Tiled && !Packed && !Planar && !Invert && !YccPacked && (!bigEndian || Bps == 1);
                // Strips written straight from the pixel array may be of any size; LZW, Deflate and PackBits strips beyond
                // 1 GB are compressed row by row into the (then necessarily seekable) output.
                bool plain = !Tiled && !Packed && !Planar && !Invert && !YccPacked;
                Streamed = !Direct && plain && ChunkRowBytes * Ch > int.MaxValue / 2 && Comp is Lzw or Deflate or PackBits;
                if (!Direct && !Streamed && (long)Cw * Ch * Spp * Bps > int.MaxValue / 2)
                    throw new ArgumentException("TIFF: strip/tile size too large, use smaller strips or tiles");
                Across = (W + Cw - 1) / Cw; Down = (H + Ch - 1) / Ch;
                PerPlane = checked(Across * Down);
                N = checked(PerPlane * (Planar ? Spp : 1));
                RawTotal = RowBytes * StoredRows(H) * (Planar ? Spp : 1);
                Offsets = new long[N]; Counts = new long[N];
            }

            /// <summary>Stored bytes of one row (one block row for YCbCr) of the given width, for one plane.</summary>
            public long StoredRowBytes(int width)
                => YccPacked ? (long)((width + SubH - 1) / SubH) * (SubH * SubV + 2)
                 : Packed ? ((long)width * Ps * Bits + 7) / 8
                 : (long)width * Ps * Bps;

            /// <summary>Stored rows for a number of image rows (block rows for YCbCr).</summary>
            public int StoredRows(int rows) => YccPacked ? (rows + SubV - 1) / SubV : rows;

            /// <summary>Upper bound of what this image adds to the file.</summary>
            public long WorstCase()
            {
                long worst = Tiled ? (long)Cw * Ch * Spp * Bps * PerPlane : (long)W * H * Spp * Bps;
                if (Comp != None) worst += worst / 2 + 4096L * N;
                var m = Image.Metadata;
                worst += 8192 + 24L * N + (m?.IccProfile?.Length ?? 0) + (m?.Exif?.Length ?? 0) + (m?.Xmp?.Length ?? 0) + (ColorMap?.Length ?? 0) * 2;
                foreach (var t in Options.ExtraTags) worst += 64 + (long)t.Count * 16;
                return worst;
            }
        }

        public static void Encode(IEnumerable<TiffPage> pages, Stream output, TiffEncodeOptions o)
        {
            if (pages == null) throw new ArgumentNullException(nameof(pages));
            if (output == null) throw new ArgumentNullException(nameof(output));
            bool be = o.BigEndian;
            bool big;
            pages = Expand(pages, o);
            if (o.CloudOptimized)
            {
                WriteCloudOptimized(pages.ToList(), output, o);
                return;
            }
            if (o.BigTiff.HasValue) big = o.BigTiff.Value;
            else
            {
                // the choice between classic and BigTIFF needs the total size: look at all pages first
                var list = pages as IReadOnlyCollection<TiffPage> ?? pages.ToList();
                pages = list;
                double worst = 64 + o.TestPadding;
                foreach (var p in list) worst += new Layout(p.Image, p.Options ?? o, be).WorstCase();
                big = worst > uint.MaxValue - 65536.0;
            }

            using var e = pages.GetEnumerator();
            if (!e.MoveNext()) throw new ArgumentException("TIFF: no image to write");
            long origin = output.CanSeek ? output.Position : 0;
            long pos = big ? 16 : 8;
            long first = pos + o.TestPadding + (o.TestPadding & 1);
            if (!big && first > uint.MaxValue) throw new InvalidOperationException(TooBig);
            output.Write(Header(big, be, first));
            if (first > pos)
            {
                var zeros = new byte[1 << 20];
                for (long left = first - pos; left > 0;) { int n = (int)Math.Min(left, zeros.Length); output.Write(zeros, 0, n); left -= n; }
                pos = first;
            }
            var cur = e.Current;
            while (true)
            {
                bool more = e.MoveNext();
                pos = WritePage(cur, cur.Options ?? o, output, origin, pos, big, be, more);
                if (!big && pos > uint.MaxValue) throw new InvalidOperationException(TooBig);
                if (!more) break;
                cur = e.Current;
            }
        }

        /// <summary>
        /// The directories to write, in order: every image followed by its mask, then its overviews (each with its
        /// mask). For cloud optimized output the tiling is switched on as well.
        /// </summary>
        static IEnumerable<TiffPage> Expand(IEnumerable<TiffPage> pages, TiffEncodeOptions o)
        {
            foreach (var page in pages)
            {
                if (page?.Image == null) throw new ArgumentException("TIFF: page without an image");
                var po = page.Options ?? o;
                bool cog = o.CloudOptimized;
                if (cog && (po.TileWidth <= 0 || po.TileHeight <= 0))
                {
                    po = po.Clone();
                    po.TileWidth = po.TileHeight = 512;
                }
                if (page.Mask != null && (page.Mask.Width != page.Image.Width || page.Mask.Height != page.Image.Height || page.Mask.SampleType != SampleType.UInt8 || page.Mask.Channels != 1))
                    throw new ArgumentException("TIFF: a mask has to be an 8-bit single channel image of the size of its image");
                // masks: 1 bit, deflate, same strips / tiles; NewSubfileType 4 (+ 1 on overviews)
                var mo = po.Clone();
                mo.ExtraTags = new List<TiffTag>();
                mo.Compression = TiffCompression.Deflate; mo.Bilevel = true; mo.Photometric = TiffPhotometric.TransparencyMask;
                mo.Palette = null; mo.BitsPerSample = 0; mo.Planar = false; mo.ExtraSamples = null;

                yield return new TiffPage(page.Image, po) { ReducedResolution = page.ReducedResolution };
                if (page.Mask != null) yield return new TiffPage(Binary(page.Mask), mo) { SubfileType = page.ReducedResolution ? 5 : 4 };
                if (!cog && !po.BuildOverviews) continue;
                // overviews carry the pixel layout options but not the tags that describe the full resolution image
                var oo = po.Clone();
                oo.ExtraTags = new List<TiffTag>();
                int min = Math.Max(1, po.OverviewMinSize);
                if (cog) min = Math.Max(min, Math.Min((po.TileWidth + 15) / 16 * 16, (po.TileHeight + 15) / 16 * 16));
                RawImage img = page.Image;
                RawImage? mask = page.Mask == null ? null : Binary(page.Mask);
                while (img.Width > min || img.Height > min)
                {
                    img = po.Palette != null || po.Photometric == TiffPhotometric.Palette ? TiffOverview.Decimate(img) : TiffOverview.Halve(img);
                    yield return new TiffPage(img, oo) { ReducedResolution = true };
                    if (mask != null)
                    {
                        mask = TiffOverview.Halve(mask); // a pixel of the overview is valid when most of its sources are
                        yield return new TiffPage(mask, mo) { SubfileType = 5 };
                    }
                }
            }
        }

        /// <summary>A mask as 0 / 255.</summary>
        static RawImage Binary(RawImage mask)
        {
            var s = mask.GetData<byte>();
            var d = new byte[s.Length];
            for (int i = 0; i < d.Length; i++) d[i] = s[i] != 0 ? (byte)255 : (byte)0;
            return new RawImage(mask.Width, mask.Height, ColorModel.Gray, d);
        }

        /// <summary>
        /// Cloud optimized layout: header, GDAL's structural metadata block, the directories of all images (image, mask,
        /// overviews ...) in order, then the tile data from the last directory to the first, i.e. the smallest overview
        /// first and the full resolution image last. Every tile is preceded by its size (4 bytes) and followed by a
        /// copy of its last 4 bytes, as the metadata block announces - readers like GDAL use that to fetch a tile and
        /// check it in a single range request. Everything is compressed before the first byte goes out, so no seeking
        /// is needed.
        /// </summary>
        static void WriteCloudOptimized(List<TiffPage> pages, Stream output, TiffEncodeOptions o)
        {
            if (pages.Count == 0) throw new ArgumentException("TIFF: no image to write");
            bool be = o.BigEndian;
            int n = pages.Count;
            var layouts = new Layout[n];
            var data = new Chunk[n][];
            double worst = 1024;
            for (int i = 0; i < n; i++)
            {
                layouts[i] = new Layout(pages[i].Image, pages[i].Options ?? o, be);
                if (!layouts[i].Tiled) throw new ArgumentException("TIFF: cloud optimized output is tiled");
                worst += layouts[i].WorstCase();
            }
            bool big = o.BigTiff ?? worst > uint.MaxValue - 65536.0;
            // (the wording, the order and the trailing space are what GDAL writes; the first line gives the size of the rest)
            const string body = "LAYOUT=IFDS_BEFORE_DATA\nBLOCK_ORDER=ROW_MAJOR\nBLOCK_LEADER=SIZE_AS_UINT4\nBLOCK_TRAILER=LAST_4_BYTES_REPEATED\nKNOWN_INCOMPATIBLE_EDITION=NO\n ";
            var ghost = System.Text.Encoding.ASCII.GetBytes($"GDAL_STRUCTURAL_METADATA_SIZE={body.Length:D6} bytes\n" + body);
            var nodes = new TiffIfdNode[n];
            var dirAt = new long[n];
            long pos = (big ? 16 : 8) + ghost.Length;
            pos += pos & 1;
            for (int i = 0; i < n; i++)
            {
                var l = layouts[i];
                var pin = GCHandle.Alloc(l.Image.Data, GCHandleType.Pinned);
                try
                {
                    var ctx = new Context { L = l, Pixels = (byte*)pin.AddrOfPinnedObject(), BigEndian = be };
                    var chunks = data[i] = new Chunk[l.N];
                    if (l.Options.Parallel && l.N > 1) Parallel.For(0, l.N, new ParallelOptions { CancellationToken = l.Options.CancellationToken }, k => chunks[k] = ctx.Compress(k));
                    else for (int k = 0; k < l.N; k++) chunks[k] = ctx.Compress(k);
                }
                finally { pin.Free(); }
                nodes[i] = BuildDirectory(l, pages[i], big);
                dirAt[i] = pos;
                pos += nodes[i].Size(big);
            }
            for (int i = n - 1; i >= 0; i--)
                for (int k = 0; k < data[i].Length; k++)
                {
                    layouts[i].Offsets[k] = pos + 4; layouts[i].Counts[k] = data[i][k].Len;
                    pos += 4 + data[i][k].Len + 4;
                }
            if (!big && pos > uint.MaxValue) throw new InvalidOperationException(TooBig);
            output.Write(Header(big, be, dirAt[0]));
            output.Write(ghost);
            if ((ghost.Length & 1) != 0) output.WriteByte(0);
            for (int i = 0; i < n; i++) output.Write(Finish(nodes[i], layouts[i], dirAt[i], i + 1 < n ? dirAt[i + 1] : 0, big, be));
            Span<byte> four = stackalloc byte[4];
            for (int i = n - 1; i >= 0; i--)
                foreach (var c in data[i])
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(four, (uint)c.Len);
                    output.Write(four);
                    output.Write(c.Buf, 0, c.Len);
                    four.Clear();
                    c.Buf.AsSpan(Math.Max(0, c.Len - 4), Math.Min(4, c.Len)).CopyTo(four.Slice(4 - Math.Min(4, c.Len)));
                    output.Write(four);
                    if (c.Pooled) ArrayPool<byte>.Shared.Return(c.Buf);
                }
        }

        static byte[] Header(bool big, bool be, long ifdOffset)
        {
            var b = new byte[big ? 16 : 8];
            b[0] = b[1] = (byte)(be ? 'M' : 'I');
            void U16(int p, int v) { if (be) BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(p), (ushort)v); else BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p), (ushort)v); }
            if (big)
            {
                U16(2, 43); U16(4, 8);
                if (be) BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(8), ifdOffset); else BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(8), ifdOffset);
            }
            else
            {
                U16(2, 42);
                if (be) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)ifdOffset); else BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)ifdOffset);
            }
            return b;
        }

        /// <summary>Writes one image at the (even) position <paramref name="pos"/> and returns the position after it.</summary>
        static long WritePage(TiffPage page, TiffEncodeOptions o, Stream output, long origin, long pos, bool big, bool be, bool more)
        {
            var l = new Layout(page.Image, o, be);
            var node = BuildDirectory(l, page, big);
            long start = pos;
            long dirSize = node.Size(big);
            long dataStart = start + dirSize;

            var pin = GCHandle.Alloc(l.Image.Data, GCHandleType.Pinned);
            try
            {
                var ctx = new Context { L = l, Pixels = (byte*)pin.AddrOfPinnedObject(), BigEndian = be };
                if (l.Comp == None)
                {
                    // sizes are known: directory first, then the data, no seeking
                    long p = dataStart;
                    for (int i = 0; i < l.N; i++)
                    {
                        int row = i % l.PerPlane / l.Across;
                        l.Offsets[i] = p;
                        l.Counts[i] = l.ChunkRowBytes * l.StoredRows(l.Tiled ? l.Ch : Math.Min(l.Ch, l.H - row * l.Ch));
                        p += l.Counts[i];
                    }
                    long end = p + (p & 1);
                    if (!big && end > uint.MaxValue) throw new InvalidOperationException(TooBig);
                    output.Write(Finish(node, l, start, more ? end : 0, big, be));
                    if (l.Direct)
                    {
                        for (long done = 0; done < l.RawTotal;)
                        {
                            int len = (int)Math.Min(l.RawTotal - done, 1 << 26);
                            output.Write(new ReadOnlySpan<byte>(ctx.Pixels + done, len));
                            done += len;
                        }
                    }
                    else
                        for (int i = 0; i < l.N; i++)
                        {
                            var c = ctx.Compress(i);
                            if (c.Len != l.Counts[i]) throw new InvalidOperationException("TIFF encoder: strip size mismatch");
                            output.Write(c.Buf, 0, c.Len);
                            if (c.Pooled) ArrayPool<byte>.Shared.Return(c.Buf);
                        }
                    if ((p & 1) != 0) output.WriteByte(0);
                    return end;
                }

                if (l.Streamed)
                {
                    if (!output.CanSeek) throw new ArgumentException("TIFF: strips larger than 1 GB can only be compressed into a seekable stream");
                    output.Write(new byte[dirSize]);
                    long at = dataStart;
                    for (int i = 0; i < l.N; i++)
                    {
                        long before = output.Position;
                        ctx.CompressStreamed(i, output);
                        l.Offsets[i] = at; l.Counts[i] = output.Position - before;
                        at += l.Counts[i];
                    }
                    long last = at + (at & 1);
                    if (!big && last > uint.MaxValue) throw new InvalidOperationException(TooBig);
                    if ((at & 1) != 0) output.WriteByte(0);
                    output.Position = origin + start;
                    output.Write(Finish(node, l, start, more ? last : 0, big, be));
                    output.Position = origin + last;
                    return last;
                }

                bool buffered = !output.CanSeek;
                var held = buffered ? new List<Chunk>(l.N) : null;
                if (!buffered) output.Write(new byte[dirSize]); // placeholder, rewritten below
                bool par = o.Parallel && l.N > 1 && l.RawTotal >= 256 * 1024;
                int batch = par ? Math.Min(l.N, Environment.ProcessorCount * 4) : 1;
                var results = new Chunk[batch];
                long q = dataStart;
                for (int s = 0; s < l.N; s += batch)
                {
                    int cnt = Math.Min(batch, l.N - s);
                    if (cnt > 1)
                    {
                        int s0 = s;
                        Parallel.For(0, cnt, new ParallelOptions { CancellationToken = o.CancellationToken }, i => results[i] = ctx.Compress(s0 + i));
                    }
                    else results[0] = ctx.Compress(s);
                    for (int i = 0; i < cnt; i++)
                    {
                        var c = results[i];
                        l.Offsets[s + i] = q; l.Counts[s + i] = c.Len;
                        q += c.Len;
                        if (buffered) held!.Add(c);
                        else
                        {
                            output.Write(c.Buf, 0, c.Len);
                            if (c.Pooled) ArrayPool<byte>.Shared.Return(c.Buf);
                        }
                    }
                }
                long stop = q + (q & 1);
                if (!big && stop > uint.MaxValue) throw new InvalidOperationException(TooBig);
                var dir = Finish(node, l, start, more ? stop : 0, big, be);
                if (buffered)
                {
                    output.Write(dir);
                    foreach (var c in held!)
                    {
                        output.Write(c.Buf, 0, c.Len);
                        if (c.Pooled) ArrayPool<byte>.Shared.Return(c.Buf);
                    }
                    if ((q & 1) != 0) output.WriteByte(0);
                }
                else
                {
                    if ((q & 1) != 0) output.WriteByte(0);
                    output.Position = origin + start;
                    output.Write(dir);
                    output.Position = origin + stop;
                }
                return stop;
            }
            finally
            {
                pin.Free();
            }
        }

        static byte[] Finish(TiffIfdNode node, Layout l, long at, long next, bool big, bool be)
        {
            node.Set(TiffField.Longs(l.Tiled ? 324 : 273, big, l.Offsets));
            node.Set(TiffField.Longs(l.Tiled ? 325 : 279, big, l.Counts));
            return node.Serialize(at, next, big, be);
        }

        static int[] Repeat(int value, int n)
        {
            var r = new int[n];
            Array.Fill(r, value);
            return r;
        }

        // tags the encoder always writes itself; callers may not override them through ExtraTags
        static bool IsStructural(int tag) => tag is 254 or 256 or 257 or 258 or 259 or 262 or 266 or 273 or 277 or 278 or 279 or 284 or 292 or 293 or 317
            or 320 or 322 or 323 or 324 or 325 or 338 or 339 or 347 or 530 or 34665 or 34853;

        static TiffIfdNode BuildDirectory(Layout l, TiffPage page, bool big)
        {
            var image = l.Image;
            var o = l.Options;
            int spp = l.Spp;
            int format = l.IsFloat ? 3 : l.Signed ? 2 : 1;
            var meta = image.Metadata;
            var n = new TiffIfdNode();
            int subfile = page.SubfileType >= 0 ? page.SubfileType : page.ReducedResolution ? 1 : 0;
            if (subfile != 0) n.Set(TiffField.Longs(254, false, subfile));
            n.Set(TiffField.Longs(256, false, l.W));
            n.Set(TiffField.Longs(257, false, l.H));
            n.Set(TiffField.Shorts(258, Repeat(l.Bits, spp)));
            n.Set(TiffField.Shorts(259, l.Comp));
            n.Set(TiffField.Shorts(262, l.Photometric));
            n.Set(TiffField.Shorts(277, spp));
            n.Set(TiffField.Shorts(284, l.Planar ? 2 : 1));
            if (l.Tiled)
            {
                n.Set(TiffField.Longs(322, false, l.Cw));
                n.Set(TiffField.Longs(323, false, l.Ch));
            }
            else n.Set(TiffField.Longs(278, false, l.Ch));
            // placeholders with the final sizes; the values are filled in once the data is laid out
            n.Set(TiffField.Longs(l.Tiled ? 324 : 273, big, l.Offsets));
            n.Set(TiffField.Longs(l.Tiled ? 325 : 279, big, l.Counts));
            if (l.Pred != 1) n.Set(TiffField.Shorts(317, l.Pred));
            if (l.Extra.Length > 0) n.Set(TiffField.Shorts(338, l.Extra));
            if (l.Bits != 1 || l.Signed) n.Set(TiffField.Shorts(339, Repeat(format, spp)));
            if (l.ColorMap != null) n.Set(TiffField.Shorts(320, Array.ConvertAll(l.ColorMap, x => (int)x)));
            if (l.Comp == Ccitt3) n.Set(TiffField.Longs(292, false, o.Ccitt2D ? 1 : 0));
            if (l.Comp == Ccitt4) n.Set(TiffField.Longs(293, false, 0));
            if (l.Photometric == 6)
            {
                n.Set(TiffField.Shorts(530, l.SubH, l.SubV));
                n.Set(TiffField.FromTag(TiffTag.Rationals(532, 0, 255, 128, 255, 128, 255), big));
            }

            // EXIF: descriptive IFD0 tags and the Exif / GPS directories travel along
            TiffIfdNode? exif = meta?.Exif is { Length: > 8 } blob ? TiffExif.Parse(blob) : null;
            if (exif != null)
                foreach (var f in exif.Fields)
                    if (f.Child != null || TiffExif.IsDescriptive(f.Tag)) n.Set(f);

            if (meta != null && meta.Orientation >= 1 && meta.Orientation <= 8) n.Set(TiffField.Shorts(274, meta.Orientation));
            else if (exif?.Find(274) is { } eo) n.Set(eo);
            if (meta != null && meta.DpiX > 0 && meta.DpiY > 0 && double.IsFinite(meta.DpiX) && double.IsFinite(meta.DpiY))
            {
                n.Set(TiffField.Rational(282, meta.DpiX));
                n.Set(TiffField.Rational(283, meta.DpiY));
                n.Set(TiffField.Shorts(296, 2));
            }
            else if (exif?.Find(282) is { } ex && exif.Find(283) is { } ey)
            {
                n.Set(ex); n.Set(ey);
                if (exif.Find(296) is { } eu) n.Set(eu);
            }
            if (meta?.IccProfile is { Length: > 0 } icc) n.Set(new TiffField { Tag = 34675, Type = 7, Count = icc.Length, Data = icc });
            if (ExifTags.XmpForWriting(meta) is { Length: > 0 } xmp) n.Set(new TiffField { Tag = 700, Type = 1, Count = xmp.Length, Data = xmp });

            foreach (var t in o.ExtraTags)
            {
                if (t == null) continue;
                if (IsStructural(t.Id)) throw new ArgumentException($"TIFF: tag {t.Id} is written by the encoder and cannot be set through ExtraTags");
                n.Set(TiffField.FromTag(t, big));
            }
            return n;
        }

        sealed class Context
        {
            public Layout L = null!;
            public byte* Pixels;
            public bool BigEndian;

            /// <summary>Gathers, converts, predicts and compresses one strip or tile.</summary>
            public Chunk Compress(int index)
            {
                L.Options.CancellationToken.ThrowIfCancellationRequested();
                var l = L;
                int plane = l.Planar ? index / l.PerPlane : -1, ci = index % l.PerPlane;
                int x0 = ci % l.Across * l.Cw, y0 = ci / l.Across * l.Ch;
                int rows = l.Tiled ? l.Ch : Math.Min(l.Ch, l.H - y0);
                int copyCols = Math.Min(l.Cw, l.W - x0), copyRows = Math.Min(rows, l.H - y0);
                if (l.Comp is JpegComp or WebpComp or Jp2Comp) return CompressImage(plane, x0, y0, rows, copyCols, copyRows);

                int ps = l.Ps, pix = ps * l.Bps;
                int nativeRow = l.Cw * pix;
                int native = nativeRow * rows;
                var raw = ArrayPool<byte>.Shared.Rent(Math.Max(native, 1));
                byte[]? packed = null;
                bool keep = false;
                try
                {
                    byte[] bytes = raw;
                    int size = native, rowBytes = nativeRow;
                    fixed (byte* r = raw)
                    {
                        Gather(r, plane, x0, y0, rows, copyCols, copyRows, nativeRow, l.Bilevel);
                        if (l.Invert && plane <= 0) InvertSamples(r, (long)l.Cw * rows * ps, ps);
                        if (l.YccPacked)
                        {
                            rowBytes = (int)l.ChunkRowBytes;
                            size = rowBytes * l.StoredRows(rows);
                            packed = ArrayPool<byte>.Shared.Rent(Math.Max(size, 1));
                            fixed (byte* p = packed) PackYcc(r, p, rows, copyCols, copyRows);
                            bytes = packed;
                        }
                        else if (l.Packed)
                        {
                            rowBytes = (int)l.ChunkRowBytes;
                            size = rowBytes * rows;
                            packed = ArrayPool<byte>.Shared.Rent(Math.Max(size, 1));
                            fixed (byte* p = packed) PackBitsOf(r, p, rows, ps, rowBytes);
                            bytes = packed;
                        }
                        else
                        {
                            if (l.Pred == 2)
                            {
                                for (int y = 0; y < rows; y++) TiffPredict.ApplyHorizontal(r + (long)y * nativeRow, l.Cw * ps, ps, l.Bps);
                            }
                            else if (l.Pred == 3)
                            {
                                var tmp = ArrayPool<byte>.Shared.Rent(nativeRow);
                                fixed (byte* t = tmp)
                                    for (int y = 0; y < rows; y++) TiffPredict.ApplyFloat(r + (long)y * nativeRow, l.Cw * ps, ps, l.Bps, t);
                                ArrayPool<byte>.Shared.Return(tmp);
                            }
                            // predictor 3 output is byte planes, the same for both byte orders
                            if (BigEndian && l.Bps > 1 && l.Pred != 3) TiffPredict.SwapBytes(r, native, l.Bps);
                        }
                        if (l.IsCcitt)
                        {
                            fixed (byte* p = bytes)
                            {
                                var fax = TiffCcitt.Encode(p, l.Cw, rows, rowBytes, l.Comp, l.Options.Ccitt2D, out int len);
                                return new Chunk { Buf = fax, Len = len };
                            }
                        }
                    }
                    var src = bytes.AsSpan(0, size);
                    switch (l.Comp)
                    {
                        case JbigComp:
                        {
                            var ms = new MemoryStream();
                            Jbig.EncodeForTiff(src, l.Cw, rows, new JbigEncodeOptions(), ms);
                            // libtiff's convention: with FillOrder 1 the bytes of the entity are stored bit-reversed
                            var bie = ms.GetBuffer().AsSpan(0, (int)ms.Length);
                            TiffPredict.ReverseBits(bie, bie);
                            return new Chunk { Buf = ms.GetBuffer(), Len = (int)ms.Length };
                        }
                        case Lzw:
                        {
                            var dst = ArrayPool<byte>.Shared.Rent(TiffLzw.MaxEncodedSize(size));
                            return new Chunk { Buf = dst, Len = TiffLzw.Encode(src, dst), Pooled = true };
                        }
                        case PackBits:
                        {
                            var dst = ArrayPool<byte>.Shared.Rent(TiffPackBits.MaxEncodedSize(size, rowBytes));
                            return new Chunk { Buf = dst, Len = TiffPackBits.Encode(src, rowBytes, dst), Pooled = true };
                        }
                        case Deflate:
                        {
                            var z = Zlib.CompressRented(src, l.Options.CompressionLevel, out int len);
                            return new Chunk { Buf = z, Len = len, Pooled = true };
                        }
                        case ZstdComp:
                        {
                            var z = Zstd.Compress(src);
                            return new Chunk { Buf = z, Len = z.Length };
                        }
                        case LzmaComp:
                        {
                            var z = Xz.Compress(src, l.Options.CompressionLevel);
                            return new Chunk { Buf = z, Len = z.Length };
                        }
                        default:
                            keep = true;
                            if (bytes == raw) { raw = null!; }
                            else packed = null;
                            return new Chunk { Buf = bytes, Len = size, Pooled = true };
                    }
                }
                finally
                {
                    if (raw != null) ArrayPool<byte>.Shared.Return(raw);
                    if (packed != null) ArrayPool<byte>.Shared.Return(packed);
                    _ = keep;
                }
            }

            /// <summary>
            /// Copies the pixels of a strip / tile (one sample of them for a plane) into r, rows of Cw pixels. The area
            /// beyond the image is zero - or, for bilevel data and when asked to, a repetition of the edge pixels.
            /// </summary>
            void Gather(byte* r, int plane, int x0, int y0, int rows, int copyCols, int copyRows, int nativeRow, bool replicate)
            {
                var l = L;
                int bps = l.Bps, spp = l.Spp, ps = plane < 0 ? spp : 1, pix = ps * bps;
                long srcRow = (long)l.W * spp * bps;
                if (copyCols < l.Cw || copyRows < rows) new Span<byte>(r, nativeRow * rows).Clear();
                for (int y = 0; y < rows; y++)
                {
                    if (y >= copyRows && !replicate) break;
                    byte* s = Pixels + (y0 + Math.Min(y, copyRows - 1)) * srcRow + (long)x0 * spp * bps;
                    byte* d = r + (long)y * nativeRow;
                    if (plane < 0) Buffer.MemoryCopy(s, d, (long)copyCols * pix, (long)copyCols * pix);
                    else
                    {
                        s += plane * bps;
                        int step = spp * bps;
                        switch (bps)
                        {
                            case 1: for (int x = 0; x < copyCols; x++) d[x] = s[(long)x * step]; break;
                            case 2: for (int x = 0; x < copyCols; x++) ((ushort*)d)[x] = *(ushort*)(s + (long)x * step); break;
                            case 4: for (int x = 0; x < copyCols; x++) ((uint*)d)[x] = *(uint*)(s + (long)x * step); break;
                            default: for (int x = 0; x < copyCols; x++) ((ulong*)d)[x] = *(ulong*)(s + (long)x * step); break;
                        }
                    }
                    if (replicate)
                        for (int x = copyCols; x < l.Cw; x++)
                            Buffer.MemoryCopy(d + (long)(copyCols - 1) * pix, d + (long)x * pix, pix, pix);
                }
            }

            /// <summary>MinIsWhite: flips the colour sample (the first of each pixel); extra samples stay as they are.</summary>
            void InvertSamples(byte* r, long count, int step)
            {
                var l = L;
                ulong max = l.Packed ? (1UL << l.Bits) - 1 : l.Bps >= 8 ? ulong.MaxValue : (1UL << (l.Bps * 8)) - 1;
                switch (l.Bps)
                {
                    case 1: for (long i = 0; i < count; i += step) r[i] = (byte)(max - r[i]); break;
                    case 2: for (long i = 0; i < count; i += step) ((ushort*)r)[i] = (ushort)(max - ((ushort*)r)[i]); break;
                    case 4: for (long i = 0; i < count; i += step) ((uint*)r)[i] = (uint)(max - ((uint*)r)[i]); break;
                    default: for (long i = 0; i < count; i += step) ((ulong*)r)[i] = max - ((ulong*)r)[i]; break;
                }
            }

            /// <summary>Packs samples MSB first into Bits bits each, rows padded to a byte. Bilevel: 8-bit samples thresholded at 128.</summary>
            void PackBitsOf(byte* r, byte* p, int rows, int ps, int rowBytes)
            {
                var l = L;
                int bits = l.Bits, bps = l.Bps, n = l.Cw * ps;
                // CCITT: 1 = black (MinIsWhite); other bilevel data: 1 = the brighter value
                bool blackIsOne = l.IsCcitt;
                new Span<byte>(p, rowBytes * rows).Clear();
                ulong mask = (1UL << bits) - 1;
                for (int y = 0; y < rows; y++)
                {
                    byte* s = r + (long)y * n * bps;
                    byte* d = p + (long)y * rowBytes;
                    if (l.Bilevel)
                    {
                        for (int x = 0; x < n; x++)
                            if ((s[x] >= 128) != blackIsOne) d[x >> 3] |= (byte)(0x80 >> (x & 7));
                        continue;
                    }
                    long pos = 0;
                    for (int x = 0; x < n; x++, pos += bits)
                    {
                        ulong v = (bps == 1 ? s[x] : bps == 2 ? ((ushort*)s)[x] : ((uint*)s)[x]) & mask;
                        // place the value so that its last bit ends at pos + bits
                        int end = (int)(pos & 7) + bits, nb = (end + 7) >> 3;
                        ulong shifted = v << (nb * 8 - end);
                        byte* o = d + (pos >> 3);
                        for (int k = nb - 1; k >= 0; k--, shifted >>= 8) o[k] |= (byte)shifted;
                    }
                }
            }

            /// <summary>RGB rows to blocks of (SubH * SubV luma samples, Cb, Cr), full range BT.601, chroma averaged over the block.</summary>
            void PackYcc(byte* r, byte* p, int rows, int copyCols, int copyRows)
            {
                var l = L;
                int sh = l.SubH, sv = l.SubV, w = l.Cw;
                int across = (w + sh - 1) / sh, down = (rows + sv - 1) / sv;
                byte* o = p;
                for (int by = 0; by < down; by++)
                    for (int bx = 0; bx < across; bx++)
                    {
                        double cb = 0, cr = 0;
                        for (int j = 0; j < sv; j++)
                            for (int i = 0; i < sh; i++)
                            {
                                // beyond the image: the nearest pixel
                                int x = Math.Min(bx * sh + i, Math.Max(copyCols, 1) - 1), y = Math.Min(by * sv + j, Math.Max(copyRows, 1) - 1);
                                byte* s = r + ((long)y * w + x) * 3;
                                double yy = 0.299 * s[0] + 0.587 * s[1] + 0.114 * s[2];
                                *o++ = (byte)Math.Clamp(yy + 0.5, 0, 255);
                                cb += (s[2] - yy) / 1.772; cr += (s[0] - yy) / 1.402;
                            }
                        *o++ = (byte)Math.Clamp(cb / (sh * sv) + 128.5, 0, 255);
                        *o++ = (byte)Math.Clamp(cr / (sh * sv) + 128.5, 0, 255);
                    }
            }

            /// <summary>Compresses one (huge) strip row by row straight into the output.</summary>
            public void CompressStreamed(int index, Stream output)
            {
                var l = L;
                int y0 = index * l.Ch, rows = Math.Min(l.Ch, l.H - y0);
                int rowBytes = (int)l.RowBytes;
                var row = new byte[rowBytes];
                var tmp = l.Pred == 3 ? new byte[rowBytes] : null;
                var packed = l.Comp == PackBits ? new byte[TiffPackBits.MaxEncodedSize(rowBytes, rowBytes)] : null;
                TiffLzwWriter? lzw = l.Comp == Lzw ? new TiffLzwWriter(output) : null;
                using Stream? z = l.Comp == Deflate ? Zlib.CreateCompressor(output, l.Options.CompressionLevel) : null;
                fixed (byte* r = row)
                fixed (byte* t = tmp)
                    for (int y = 0; y < rows; y++)
                    {
                        Buffer.MemoryCopy(Pixels + (long)(y0 + y) * rowBytes, r, rowBytes, rowBytes);
                        if (l.Pred == 2) TiffPredict.ApplyHorizontal(r, l.W * l.Spp, l.Spp, l.Bps);
                        else if (l.Pred == 3) TiffPredict.ApplyFloat(r, l.W * l.Spp, l.Spp, l.Bps, t);
                        if (BigEndian && l.Bps > 1 && l.Pred != 3) TiffPredict.SwapBytes(r, rowBytes, l.Bps);
                        if (lzw != null) lzw.Write(row);
                        else if (z != null) z.Write(row);
                        else output.Write(packed!, 0, TiffPackBits.Encode(row, rowBytes, packed));
                    }
                lzw?.Finish();
            }

            /// <summary>
            /// Codecs that take an image: JPEG, WebP, JPEG 2000 - one strip / tile as a complete stream of its
            /// own. Tiles are padded by repeating the edge pixels.
            /// </summary>
            Chunk CompressImage(int plane, int x0, int y0, int rows, int copyCols, int copyRows)
            {
                var l = L;
                int ps = l.Ps, pix = ps * l.Bps;
                var px = new byte[(long)l.Cw * rows * pix];
                fixed (byte* d0 = px) Gather(d0, plane, x0, y0, rows, copyCols, copyRows, l.Cw * pix, true);
                var ms = new MemoryStream();
                switch (l.Comp)
                {
                    case JpegComp:
                        if (l.Bps == 1) Jpeg.EncodeForTiff(px, l.Cw, rows, l.Spp, l.Jpeg!, l.Photometric == 6, ms);
                        else Jpeg.EncodeForTiff(MemoryMarshal.Cast<byte, ushort>(px), l.Cw, rows, l.Spp, l.Jpeg!, l.Photometric == 6, ms);
                        break;
                    case WebpComp:
                        Webp.Encode(new RawImage(l.Cw, rows, l.Spp == 4 ? ColorModel.Rgba : ColorModel.Rgb, px), ms,
                            new WebpEncodeOptions { Lossless = l.Options.WebpLossless, Quality = l.Options.WebpQuality });
                        break;
                    default:
                        TiffForeignCodecs.Encode(l.Comp, px, l.Cw, rows, ps, l.Image.SampleType, l.Photometric == 2 && plane < 0, l.Options, ms);
                        break;
                }
                return new Chunk { Buf = ms.GetBuffer(), Len = (int)ms.Length };
            }
        }
    }
}
