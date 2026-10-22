using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// Random access to the bytes of a file: memory (the caller keeps it pinned) or a seekable stream, from which only
    /// the headers, offset tables and the chunks that are needed get read.
    /// </summary>
    internal abstract class ExrInput
    {
        public long Length { get; protected set; }

        /// <summary>The bytes [pos, pos + count); for streams they are read into a buffer of the scratch (slot) or a new array.</summary>
        public abstract ReadOnlySpan<byte> Read(long pos, int count, ExrScratch? scratch, int slot);

        protected void Check(long pos, int count)
        {
            if (pos < 0 || count < 0 || pos > Length - count) throw new ImageFormatException("EXR file is truncated");
        }

        /// <summary>Parses the headers, reading as much of the start of the file as they need.</summary>
        public ExrFileStructure ParseStructure()
        {
            for (long size = 64 * 1024; ; size *= 4)
            {
                int n = (int)Math.Min(Math.Min(size, Length), int.MaxValue - 64);
                try { return ExrFileStructure.Parse(Read(0, n, null, 0)); }
                catch (ImageFormatException) when (n < Length && n < int.MaxValue - 64) { }
            }
        }
    }

    internal sealed unsafe class ExrMemoryInput : ExrInput
    {
        readonly byte* _data;
        public ExrMemoryInput(byte* data, int length) { _data = data; Length = length; }

        public override ReadOnlySpan<byte> Read(long pos, int count, ExrScratch? scratch, int slot)
        {
            Check(pos, count);
            return new ReadOnlySpan<byte>(_data + pos, count);
        }
    }

    internal sealed class ExrStreamInput : ExrInput
    {
        readonly Stream _stream;
        readonly long _origin;
        readonly object _lock = new();

        public ExrStreamInput(Stream stream)
        {
            _stream = stream;
            _origin = stream.Position;
            Length = stream.Length - _origin;
        }

        public override ReadOnlySpan<byte> Read(long pos, int count, ExrScratch? scratch, int slot)
        {
            Check(pos, count);
            var buffer = scratch != null ? scratch.Bytes(slot, count) : new byte[count];
            lock (_lock)
            {
                _stream.Position = _origin + pos;
                int done = 0;
                while (done < count)
                {
                    int n = _stream.Read(buffer, done, count - done);
                    if (n <= 0) throw new ImageFormatException("EXR file is truncated");
                    done += n;
                }
            }
            return buffer.AsSpan(0, count);
        }
    }

    internal sealed unsafe class ExrDecoder
    {
        const int MaxArrayLength = 0x7FFFFFC7;
        // No EXR compression scheme can exceed this ratio (zlib tops out near 1032:1, PXR24 adds 4/3).
        internal const long MaxRatio = 2048;
        const int ParallelThreshold = 512 * 1024;

        ExrInput _input = null!;
        bool _multiPart;
        int _partIndex;
        ExrHeader _part = null!;
        ExrChan[] _chans = null!;
        int[] _slot = null!;          // file channel -> output slot or -1
        int _nSel;
        int _outType;                 // ExrPixelType of the output samples
        byte* _out;
        long[] _offsets = null!;
        int _firstIndex;              // chunk index (within the level) of _offsets[0]
        int _linesPerChunk, _tilesX, _levelX, _levelY;
        int _minX, _minY, _maxX, _maxY;          // data window of the level
        int _rx0, _ry0, _rx1, _ry1, _rw;         // output region (may reach outside the data window)

        internal static InvalidOperationException Unreachable() => new("unreachable");

        /// <summary>The message for results that do not fit one array.</summary>
        internal static ImageNotSupportedException TooLarge(long w, long h, int channels)
            => new($"EXR image {w}x{h}x{channels} has more than 2^31 samples and does not fit a single array; decode it in parts with ExrDecodeOptions.Region");

        /// <summary>The window to return: an explicit region, the display window, or the data window of the level.</summary>
        internal static ExrBox2i OutputWindow(ExrHeader part, ExrDecodeOptions opt, ExrBox2i levelWindow)
        {
            var r = opt.Region ?? (opt.Window == ExrWindow.Display ? part.DisplayWindow : levelWindow);
            if (r.MaxX < r.MinX || r.MaxY < r.MinY) throw new ArgumentException("the requested EXR region is empty");
            if (r.Width > int.MaxValue || r.Height > int.MaxValue) throw new ArgumentException("the requested EXR region is too large");
            return r;
        }

        public static RawImage Decode(ExrInput input, ExrFileStructure file, int partIndex, int[] sel, ColorModel color, ExrDecodeOptions opt)
        {
            var part = file.Parts[partIndex];
            if (part.IsDeep) throw new ImageNotSupportedException("EXR deep parts have no flat channels; use Exr.Decode (flattened) or Exr.DecodeDeep");
            if (part.Compression > ExrCompression.Zstd)
                throw new ImageNotSupportedException($"EXR compression {(int)part.Compression} is not supported");
            var chans = part.Chans;
            if (part.IsTiled)
                foreach (var c in chans)
                    if (c.XS != 1 || c.YS != 1) throw new ImageFormatException("EXR tiled part has subsampled channels");

            int lx = opt.LevelX, ly = opt.LevelY;
            if (!part.HasLevel(lx, ly)) throw new ArgumentException($"EXR part has no level ({lx}, {ly})");
            var (w, h) = part.GetLevelSize(lx, ly);
            // a level keeps the origin of the data window
            var dw = new ExrBox2i(part.DataWindow.MinX, part.DataWindow.MinY, part.DataWindow.MinX + w - 1, part.DataWindow.MinY + h - 1);
            var region = OutputWindow(part, opt, dw);

            bool subsampled = false;
            foreach (int i in sel) subsampled |= chans[i].XS != 1 || chans[i].YS != 1;
            if (subsampled && region != dw)
            {
                // samples of subsampled channels are replicated from positions outside a region: decode all, then cut
                var full = Decode(input, file, partIndex, sel, color, new ExrDecodeOptions { HalfAsFloat = opt.HalfAsFloat, Parallel = opt.Parallel });
                return Crop(full, dw, region);
            }
            int rw = (int)region.Width, rh = (int)region.Height;
            if ((long)rw * rh * sel.Length > MaxArrayLength) throw TooLarge(rw, rh, sel.Length);

            // the part of the region that has data, and the chunks covering it
            int ix0 = Math.Max(region.MinX, dw.MinX), ix1 = Math.Min(region.MaxX, dw.MaxX);
            int iy0 = Math.Max(region.MinY, dw.MinY), iy1 = Math.Min(region.MaxY, dw.MaxY);
            bool empty = ix0 > ix1 || iy0 > iy1;
            bool covered = !empty && ix0 == region.MinX && ix1 == region.MaxX && iy0 == region.MinY && iy1 == region.MaxY;

            // A header can claim any size; pixel data that cannot possibly be in the file is rejected before allocating.
            long stored = 0;
            if (!empty)
                foreach (var c in chans)
                    stored += (long)ExrChunk.NumSamples(c.XS, ix0, ix1) * ExrChunk.NumSamples(c.YS, iy0, iy1) * c.Bytes;
            if (stored > input.Length * (part.Compression == ExrCompression.None ? 1 : MaxRatio))
                throw new ImageFormatException("EXR data window is too large for the file size");

            var outType = Exr.OutputType(part, sel, opt.HalfAsFloat);
            var d = new ExrDecoder
            {
                _input = input, _multiPart = file.MultiPart, _partIndex = partIndex, _part = part, _chans = chans,
                _nSel = sel.Length, _minX = dw.MinX, _minY = dw.MinY, _maxX = dw.MaxX, _maxY = dw.MaxY,
                _rx0 = region.MinX, _ry0 = region.MinY, _rx1 = region.MaxX, _ry1 = region.MaxY, _rw = rw,
                _outType = outType == SampleType.UInt32 ? 0 : outType == SampleType.Float16 ? 1 : 2,
                _slot = new int[chans.Length], _levelX = lx, _levelY = ly,
            };
            Array.Fill(d._slot, -1);
            for (int i = 0; i < sel.Length; i++) d._slot[sel[i]] = i;

            // chunk indices within the level: a contiguous range of scan line blocks, or the tiles of a rectangle
            int[] needed = Array.Empty<int>();
            if (!empty)
            {
                if (part.IsTiled)
                {
                    d._tilesX = (w + part.TileWidth - 1) / part.TileWidth;
                    int tx0 = (ix0 - dw.MinX) / part.TileWidth, tx1 = (ix1 - dw.MinX) / part.TileWidth;
                    int ty0 = (iy0 - dw.MinY) / part.TileHeight, ty1 = (iy1 - dw.MinY) / part.TileHeight;
                    needed = new int[checked((tx1 - tx0 + 1) * (ty1 - ty0 + 1))];
                    int k = 0;
                    for (int ty = ty0; ty <= ty1; ty++)
                        for (int tx = tx0; tx <= tx1; tx++) needed[k++] = checked(ty * d._tilesX + tx);
                }
                else
                {
                    d._linesPerChunk = ExrCodecs.LinesPerChunk(part.Compression);
                    int b0 = (iy0 - dw.MinY) / d._linesPerChunk, b1 = (iy1 - dw.MinY) / d._linesPerChunk;
                    needed = new int[b1 - b0 + 1];
                    for (int k = 0; k < needed.Length; k++) needed[k] = b0 + k;
                }
                d._firstIndex = needed[0];
                d._offsets = ExrChunkTable.Locate(input, file, partIndex, part.FirstChunkOfLevel(lx, ly) + d._firstIndex, needed[^1] - d._firstIndex + 1, lx, ly, d._firstIndex);
            }

            // every sample is overwritten when the region lies inside the data window and no subsampled channel
            // leaves gaps, so the (large) array need not be zeroed then
            int count = rw * rh * sel.Length;
            Array samples = subsampled || !covered ? RawImage.CreateArray(outType, count)
                : outType == SampleType.UInt32 ? GC.AllocateUninitializedArray<uint>(count)
                : outType == SampleType.Float16 ? GC.AllocateUninitializedArray<Half>(count)
                : GC.AllocateUninitializedArray<float>(count);
            var result = new RawImage(rw, rh, color, samples, sel.Length);
            fixed (byte* po = &MemoryMarshal.GetArrayDataReference(result.Data))
            {
                d._out = po;
                if (opt.Parallel && needed.Length > 1 && stored >= ParallelThreshold && Environment.ProcessorCount > 1)
                {
                    try
                    {
                        // Parallel.For starts a worker replica (and its local state) many times: their scratch is passed on
                        Parallel.For(0, needed.Length, ExrScratch.Rent, (i, _, s) => { d.DecodeChunk(needed[i], s); return s; }, ExrScratch.Return);
                    }
                    catch (AggregateException e) when (e.InnerException != null)
                    {
                        ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                    }
                }
                else
                {
                    var s = ExrScratch.Rent();
                    foreach (int i in needed) d.DecodeChunk(i, s);
                    ExrScratch.Return(s);
                }
                if (subsampled) d.FillSubsampled(h);
            }
            return result;
        }

        /// <summary>Cuts (and zero pads) an image that covers the window src to the window dst.</summary>
        internal static RawImage Crop(RawImage image, ExrBox2i src, ExrBox2i dst)
        {
            if (src == dst) return image;
            int w = (int)dst.Width, h = (int)dst.Height, nc = image.Channels;
            if ((long)w * h * nc > MaxArrayLength) throw TooLarge(w, h, nc);
            var result = RawImage.Create(w, h, image.Color, image.SampleType, nc);
            int es = RawImage.BytesPerSample(image.SampleType);
            int x0 = Math.Max(src.MinX, dst.MinX), x1 = Math.Min(src.MaxX, dst.MaxX);
            var from = image.GetBytes();
            var to = result.GetBytes();
            if (x0 <= x1)
                for (int y = Math.Max(src.MinY, dst.MinY); y <= Math.Min(src.MaxY, dst.MaxY); y++)
                {
                    int n = (x1 - x0 + 1) * nc * es;
                    from.Slice((int)((((long)(y - src.MinY)) * image.Width + (x0 - src.MinX)) * nc * es), n)
                        .CopyTo(to.Slice((int)((((long)(y - dst.MinY)) * w + (x0 - dst.MinX)) * nc * es), n));
                }
            result.Metadata = image.Metadata;
            return result;
        }

        // ------------------------------------------------------------------ chunks

        void DecodeChunk(int index, ExrScratch s)
        {
            long pos = _offsets[index - _firstIndex];
            if (pos <= 0) throw new ImageFormatException("EXR file is incomplete: a chunk is missing");
            int hdr = (_multiPart ? 4 : 0) + (_part.IsTiled ? 20 : 8);
            if (pos > _input.Length - hdr) throw new ImageFormatException("EXR chunk offset is out of range");
            var h = _input.Read(pos, hdr, s, 5);
            if (_multiPart)
            {
                if (BinaryPrimitives.ReadInt32LittleEndian(h) != _partIndex) throw new ImageFormatException("EXR chunk belongs to another part");
                h = h.Slice(4);
            }
            int x0 = _minX, x1 = _maxX, y0, y1;
            if (_part.IsTiled)
            {
                int tx = index % _tilesX, ty = index / _tilesX;
                if (BinaryPrimitives.ReadInt32LittleEndian(h) != tx || BinaryPrimitives.ReadInt32LittleEndian(h.Slice(4)) != ty
                    || BinaryPrimitives.ReadInt32LittleEndian(h.Slice(8)) != _levelX || BinaryPrimitives.ReadInt32LittleEndian(h.Slice(12)) != _levelY)
                    throw new ImageFormatException("EXR tile coordinates do not match the offset table");
                x0 = (int)Math.Min(_minX + (long)tx * _part.TileWidth, int.MaxValue);
                x1 = (int)Math.Min(x0 + (long)_part.TileWidth - 1, _maxX);
                y0 = (int)Math.Min(_minY + (long)ty * _part.TileHeight, int.MaxValue);
                y1 = (int)Math.Min(y0 + (long)_part.TileHeight - 1, _maxY);
            }
            else
            {
                y0 = _minY + index * _linesPerChunk;
                y1 = Math.Min(y0 + _linesPerChunk - 1, _maxY);
                if (BinaryPrimitives.ReadInt32LittleEndian(h) != y0) throw new ImageFormatException("EXR scan line chunk does not match the offset table");
            }
            int size = BinaryPrimitives.ReadInt32LittleEndian(h.Slice(h.Length - 4));
            pos += hdr;
            if (size < 0 || size > _input.Length - pos) throw new ImageFormatException("EXR chunk is truncated");

            var chunk = ExrChunk.Create(_chans, x0, x1, y0, y1, s);
            if (chunk.Size > int.MaxValue) throw new ImageNotSupportedException("EXR chunk is too large");
            int rawSize = (int)chunk.Size;
            int rowLength = x1 - x0 + 1;
            var row = s.RowBuffer(rowLength);
            if (size > rawSize || (size != rawSize && (_part.Compression == ExrCompression.None || rawSize > size * MaxRatio)))
                throw new ImageFormatException("EXR chunk has an impossible size");
            var packed = _input.Read(pos, size, s, 4);
            if (size == rawSize)
            {
                // stored uncompressed (the writer does this whenever compression does not shrink a chunk)
                fixed (byte* pp = packed) Unpack(pp, chunk, row);
                return;
            }
            var raw = s.Bytes(2, rawSize);
            ExrCodecs.Decode(_part.Compression, packed, raw.AsSpan(0, rawSize), chunk, s);
            fixed (byte* pr = raw) Unpack(pr, chunk, row);
        }

        /// <summary>Converts the selected channels of an uncompressed chunk into the interleaved output, clipped to the region.</summary>
        void Unpack(byte* p, in ExrChunk c, float[] rowBuffer)
        {
            int nSel = _nSel;
            fixed (float* tmp = rowBuffer)
                for (int y = c.Y0; y <= c.Y1; y++)
                {
                    bool rowWanted = y >= _ry0 && y <= _ry1;
                    long rowBase = (long)(y - _ry0) * _rw * nSel;
                    for (int ci = 0; ci < _chans.Length; ci++)
                    {
                        ref readonly ExrChan ch = ref _chans[ci];
                        if (!ExrChunk.IsSampled(y, ch.YS)) continue;
                        int n = c.Nx[ci], k = _slot[ci];
                        if (k >= 0 && n > 0 && rowWanted)
                        {
                            // first sample position and source pointer; full resolution channels are clipped to the
                            // region, subsampled ones are only decoded with the region equal to the data window
                            int fx = ch.XS == 1 ? c.X0 : ExrChunk.FloorDiv(c.X0 + ch.XS - 1, ch.XS) * ch.XS;
                            byte* q = p;
                            int m = n;
                            if (ch.XS == 1)
                            {
                                int skip = Math.Max(_rx0 - c.X0, 0);
                                m = Math.Min(c.X1, _rx1) - (c.X0 + skip) + 1;
                                fx += skip;
                                q += (long)skip * ch.Bytes;
                            }
                            if (m > 0)
                            {
                                long idx = rowBase + (long)(fx - _rx0) * nSel + k;
                                int step = nSel * ch.XS;
                                if (_outType == 2)
                                {
                                    float* o = (float*)_out + idx;
                                    if (ch.Type == 1)
                                    {
                                        if (step == 1) ExrHalf.ToFloat((ushort*)q, o, m);
                                        else
                                        {
                                            ExrHalf.ToFloat((ushort*)q, tmp, m);
                                            for (int j = 0; j < m; j++) o[(long)j * step] = tmp[j];
                                        }
                                    }
                                    else if (ch.Type == 2)
                                    {
                                        if (step == 1) Buffer.MemoryCopy(q, o, 4L * m, 4L * m);
                                        else for (int j = 0; j < m; j++) *(uint*)(o + (long)j * step) = Unsafe.ReadUnaligned<uint>(q + 4 * j);
                                    }
                                    else
                                        for (int j = 0; j < m; j++) o[(long)j * step] = Unsafe.ReadUnaligned<uint>(q + 4 * j);
                                }
                                else if (_outType == 1)
                                {
                                    ushort* o = (ushort*)_out + idx;
                                    if (step == 1) Buffer.MemoryCopy(q, o, 2L * m, 2L * m);
                                    else for (int j = 0; j < m; j++) o[(long)j * step] = Unsafe.ReadUnaligned<ushort>(q + 2 * j);
                                }
                                else
                                {
                                    uint* o = (uint*)_out + idx;
                                    if (step == 1) Buffer.MemoryCopy(q, o, 4L * m, 4L * m);
                                    else for (int j = 0; j < m; j++) o[(long)j * step] = Unsafe.ReadUnaligned<uint>(q + 4 * j);
                                }
                            }
                        }
                        p += (long)n * ch.Bytes;
                    }
                }
        }

        /// <summary>Expands subsampled channels to full resolution by replicating each sample over its xSampling x ySampling cell.</summary>
        void FillSubsampled(int height)
        {
            int es = _outType == 1 ? 2 : 4;
            for (int ci = 0; ci < _chans.Length; ci++)
            {
                int k = _slot[ci], xs = _chans[ci].XS, ys = _chans[ci].YS;
                if (k < 0 || (xs == 1 && ys == 1)) continue;
                long rowBytes = (long)_rw * _nSel * es;
                for (int pass = 0; pass < 2; pass++)
                    for (int y = _minY; y <= _maxY; y++)
                    {
                        byte* row = _out + (y - _minY) * rowBytes + (long)k * es;
                        bool sampled = ExrChunk.IsSampled(y, ys);
                        if (pass == 0)
                        {
                            if (!sampled || xs == 1) continue;
                            for (int x = _minX; x <= _maxX; x++)
                            {
                                long sx = (long)ExrChunk.FloorDiv(x, xs) * xs;
                                if (sx < _minX) sx += xs;
                                if (sx == x || sx > _maxX) continue;
                                Buffer.MemoryCopy(row + (sx - _minX) * _nSel * es, row + (long)(x - _minX) * _nSel * es, es, es);
                            }
                        }
                        else
                        {
                            if (sampled) continue;
                            long sy = (long)ExrChunk.FloorDiv(y, ys) * ys;
                            if (sy < _minY) sy += ys;
                            if (sy > _maxY) continue;
                            byte* src = _out + (sy - _minY) * rowBytes + (long)k * es;
                            for (long x = 0; x < _rw; x++)
                                Buffer.MemoryCopy(src + x * _nSel * es, row + x * _nSel * es, es, es);
                        }
                    }
            }
        }
    }

    /// <summary>Finds the chunks of one level of one part: from the offset table, or by scanning the file if the table is damaged.</summary>
    internal static class ExrChunkTable
    {
        /// <summary>Bytes of a chunk header before the pixel data (without the part number of multi-part files).</summary>
        static int HeaderSize(ExrHeader p) => (p.IsTiled ? 16 : 4) + (p.IsDeep ? 24 : 4);

        /// <summary>
        /// Offsets of count chunks starting at table entry first. levelFirst is the index within the level of the
        /// first requested chunk (for rebuilding a damaged table).
        /// </summary>
        public static long[] Locate(ExrInput input, ExrFileStructure file, int partIndex, long first, int count, int lx, int ly, int levelFirst = 0)
        {
            // table sizes of all parts (ours starts after those of the preceding parts)
            long tablePos = file.TablesStart, tablesEnd = file.TablesStart;
            for (int i = 0; i < file.Parts.Length; i++)
            {
                var p = file.Parts[i];
                long n = p.ChunkCountAttribute;
                if (n < 0 || (i == partIndex && !file.MultiPart)) n = p.ComputeChunkCount();
                if (i == partIndex)
                {
                    tablePos = tablesEnd;
                    if (n < first + count) throw new ImageFormatException("EXR chunkCount does not match the image size");
                }
                tablesEnd += 8 * n;
                if (tablesEnd > input.Length) throw new ImageFormatException("EXR offset table is truncated");
            }

            var offsets = new long[count];
            bool valid = true;
            int minChunk = (file.MultiPart ? 4 : 0) + HeaderSize(file.Parts[partIndex]);
            // entries are read in blocks so that a table of any size works with streams
            const int Block = 8192;
            for (int done = 0; done < count && valid; done += Block)
            {
                int n = Math.Min(Block, count - done);
                var entries = input.Read(tablePos + 8 * (first + done), 8 * n, null, 0);
                for (int i = 0; i < n; i++)
                {
                    ulong o = BinaryPrimitives.ReadUInt64LittleEndian(entries.Slice(8 * i));
                    if (o < (ulong)tablesEnd || o > (ulong)(input.Length - minChunk)) { valid = false; break; }
                    offsets[done + i] = (long)o;
                }
            }
            if (!valid) Scan(input, file, partIndex, tablesEnd, offsets, lx, ly, levelFirst);
            return offsets;
        }

        /// <summary>
        /// Rebuilds the offsets by walking the chunks in file order (for files with a missing or incomplete offset
        /// table, as left behind by an interrupted writer).
        /// </summary>
        static void Scan(ExrInput input, ExrFileStructure file, int partIndex, long start, long[] offsets, int levelX, int levelY, int levelFirst)
        {
            Array.Clear(offsets);
            var our = file.Parts[partIndex];
            int lpc = our.IsTiled ? 0 : ExrCodecs.LinesPerChunk(our.Compression);
            int tilesX = our.IsTiled ? (our.GetLevelSize(levelX, levelY).Width + our.TileWidth - 1) / our.TileWidth : 0;
            var scratch = new ExrScratch();
            long pos = start;
            Span<byte> head = stackalloc byte[4 + 40];
            while (true)
            {
                long chunkStart = pos;
                var part = our;
                if (file.MultiPart)
                {
                    if (input.Length - pos < 4) break;
                    int pn = BinaryPrimitives.ReadInt32LittleEndian(input.Read(pos, 4, scratch, 5));
                    if (pn < 0 || pn >= file.Parts.Length) break;
                    part = file.Parts[pn];
                    pos += 4;
                }
                int hdr = HeaderSize(part);
                if (input.Length - pos < hdr) break;
                input.Read(pos, hdr, scratch, 5).CopyTo(head);
                var h = head.Slice(0, hdr);
                long size;
                if (part.IsDeep)
                {
                    long table = BinaryPrimitives.ReadInt64LittleEndian(h.Slice(hdr - 24)), packed = BinaryPrimitives.ReadInt64LittleEndian(h.Slice(hdr - 16));
                    if (table < 0 || packed < 0 || table > input.Length || packed > input.Length) break;
                    size = table + packed;
                }
                else size = BinaryPrimitives.ReadInt32LittleEndian(h.Slice(hdr - 4));
                pos += hdr;
                if (size < 0 || size > input.Length - pos) break;
                pos += size;
                if (part != our) continue;
                long index;
                if (part.IsTiled)
                {
                    int tx = BinaryPrimitives.ReadInt32LittleEndian(h), ty = BinaryPrimitives.ReadInt32LittleEndian(h.Slice(4));
                    int lx = BinaryPrimitives.ReadInt32LittleEndian(h.Slice(8)), ly = BinaryPrimitives.ReadInt32LittleEndian(h.Slice(12));
                    if (lx != levelX || ly != levelY || tx < 0 || tx >= tilesX || ty < 0) continue;
                    index = (long)ty * tilesX + tx;
                }
                else
                {
                    long dy = (long)BinaryPrimitives.ReadInt32LittleEndian(h) - our.DataWindow.MinY;
                    if (dy < 0 || dy % lpc != 0) continue;
                    index = dy / lpc;
                }
                index -= levelFirst;
                if (index >= 0 && index < offsets.Length) offsets[index] = chunkStart;
            }
        }
    }
}
