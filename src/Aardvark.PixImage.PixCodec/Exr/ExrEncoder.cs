using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs
{
    /// <summary>Writes one or more flat parts (scan line or tiled, optionally with generated mip / rip levels).</summary>
    internal sealed unsafe class ExrEncoder
    {
        const int ParallelThreshold = 512 * 1024;

        struct Source
        {
            public Array Array;
            public byte* Data;     // first element of the pinned array
            public int Kind;       // element type: 0 uint, 1 Half, 2 float
            public long Offset, Stride;
            public int PlaneWidth; // samples per row of this channel's plane
        }

        sealed class Level
        {
            public int LevelX, LevelY, Width, Height, TilesX, TilesY;
            public ExrDeepImage? Deep;        // samples of this level of a deep part
            public Source[] Sources = Array.Empty<Source>();
            public int FirstChunk;
        }

        /// <summary>Everything needed to produce the chunks of one part.</summary>
        sealed class Part
        {
            public int Index, Width, Height, X0, Y0, LinesPerChunk, TileWidth, TileHeight;
            public bool Tiled;
            public ExrLevelMode LevelMode;
            public ExrLineOrder LineOrder;
            public ExrEncodeOptions Options = null!;
            public ExrChan[] Chans = Array.Empty<ExrChan>();
            public List<ExrChannelData> Channels = null!;
            public List<Level> Levels = new();
            public byte[][] Chunks = Array.Empty<byte[]>();
            public byte[] Header = Array.Empty<byte>();
            public string? Name;
            public ExrDeepImage? Deep;
            public int[] DeepOrder = Array.Empty<int>();    // channel of the deep image for each (sorted) file channel
            public bool RoundUp;
            public ExrCompression DeepCompression;
        }

        /// <summary>Attributes that describe the storage layout; the encoder always writes its own.</summary>
        internal static bool IsStructural(string name)
            => name is "channels" or "compression" or "dataWindow" or "lineOrder" or "tiles" or "type" or "chunkCount" or "version"
                or "dwaCompressionLevel" or "maxSamplesPerPixel";

        public static void Encode(IReadOnlyList<ExrPart> parts, Stream output, bool multiPart)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (parts == null || parts.Count == 0) throw new ArgumentException("no parts given");
            bool longNames = false;
            var plans = new List<Part>();
            var pins = new List<GCHandle>();
            try
            {
                var names = new HashSet<string>();
                for (int i = 0; i < parts.Count; i++)
                {
                    var p = parts[i] ?? throw new ArgumentException("null part");
                    if (multiPart)
                    {
                        if (string.IsNullOrEmpty(p.Name)) throw new ArgumentException("every part of a multi-part file needs a name");
                        if (!names.Add(p.Name)) throw new ArgumentException($"part name '{p.Name}' is used twice");
                    }
                    plans.Add(Plan(p, i, multiPart, pins, ref longNames));
                }
                if (multiPart)
                    for (int i = 1; i < plans.Count; i++)
                        if (DisplayWindow(plans[i]) != DisplayWindow(plans[0]) || PixelAspect(plans[i]) != PixelAspect(plans[0]))
                            throw new ArgumentException("all parts of a multi-part file must have the same display window and pixel aspect ratio");

                // magic, version, headers
                var head = new MemoryStream();
                var w = new BinaryWriter(head);
                bool tiledSingle = !multiPart && plans[0].Tiled && plans[0].Deep == null;
                bool anyDeep = plans.Exists(p => p.Deep != null);
                w.Write(0x01312f76);
                w.Write(2 | (longNames ? ExrFileStructure.FlagLongNames : 0) | (tiledSingle ? ExrFileStructure.FlagTiled : 0)
                          | (multiPart ? ExrFileStructure.FlagMultiPart : 0) | (anyDeep ? ExrFileStructure.FlagNonImage : 0));
                foreach (var plan in plans) w.Write(plan.Header);
                if (multiPart) w.Write((byte)0);
                w.Flush();

                // all chunks in the order they are written: part by part, in line order
                var order = new List<(Part part, int chunk)>();
                long bytes = 0, tableBytes = 0;
                foreach (var plan in plans)
                {
                    foreach (int c in FileOrder(plan)) order.Add((plan, c));
                    foreach (var pc in plan.Chans) bytes += (long)plan.Width * plan.Height * pc.Bytes / (pc.XS * pc.YS);
                    if (plan.Deep != null) foreach (var l in plan.Levels) bytes += 4L * l.Deep!.TotalSamples * plan.Chans.Length;
                    tableBytes += 8L * plan.Chunks.Length;
                }
                bool parallel = true;
                foreach (var p in parts) parallel &= p.Options.Parallel;
                parallel &= order.Count > 1 && bytes >= ParallelThreshold && Environment.ProcessorCount > 1;

                void Compress(int from, int to)
                {
                    if (parallel && to - from > 1)
                    {
                        try
                        {
                            var spare = new System.Collections.Concurrent.ConcurrentBag<ExrScratch>();
                            Parallel.For(from, to, () => spare.TryTake(out var s) ? s : new ExrScratch(), (i, _, s) => { EncodeChunk(order[i].part, order[i].chunk, s); return s; }, spare.Add);
                        }
                        catch (AggregateException ex) when (ex.InnerException != null)
                        {
                            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                        }
                    }
                    else
                    {
                        var s = new ExrScratch();
                        for (int i = from; i < to; i++) EncodeChunk(order[i].part, order[i].chunk, s);
                    }
                }

                // The offset tables precede the chunks. A seekable output gets placeholder tables that are filled in
                // at the end, so that only a batch of compressed chunks is in memory at any time; otherwise everything
                // is compressed first. Both ways produce the same file.
                bool backPatch = output.CanSeek;
                int batch = backPatch ? Math.Max(16, 4 * Environment.ProcessorCount) : order.Count;
                long origin = backPatch ? output.Position : 0;
                var offsets = new Dictionary<Part, long[]>();
                foreach (var plan in plans) offsets[plan] = new long[plan.Chunks.Length];
                long pos = head.Length + tableBytes;
                int partNumberBytes = multiPart ? 4 : 0;
                Span<byte> ch = stackalloc byte[24];

                void WriteTables(Stream to)
                {
                    Span<byte> e = stackalloc byte[8];
                    foreach (var plan in plans)
                        foreach (long o in offsets[plan])
                        {
                            BinaryPrimitives.WriteInt64LittleEndian(e, o);
                            to.Write(e);
                        }
                }

                if (backPatch)
                {
                    head.Position = 0;
                    head.CopyTo(output);
                    WriteTables(output);       // zeros for now
                }
                else
                {
                    Compress(0, order.Count);
                    long q = pos;
                    foreach (var (plan, c) in order)
                    {
                        offsets[plan][c] = q;
                        q += partNumberBytes + (plan.Tiled ? 16 : 4) + (plan.Deep != null ? 0 : 4) + plan.Chunks[c].Length;
                    }
                    head.Position = 0;
                    head.CopyTo(output);
                    WriteTables(output);
                }
                try
                {
                for (int from = 0; from < order.Count; from += batch)
                {
                    int to = Math.Min(order.Count, from + batch);
                    if (backPatch) Compress(from, to);
                    for (int i = from; i < to; i++)
                    {
                        var (plan, c) = order[i];
                        offsets[plan][c] = pos;
                        int n = 0;
                        if (multiPart) { BinaryPrimitives.WriteInt32LittleEndian(ch, plan.Index); n = 4; }
                        if (plan.Tiled)
                        {
                            var level = LevelOf(plan, c);
                            int t = c - level.FirstChunk;
                            BinaryPrimitives.WriteInt32LittleEndian(ch.Slice(n), t % level.TilesX);
                            BinaryPrimitives.WriteInt32LittleEndian(ch.Slice(n + 4), t / level.TilesX);
                            BinaryPrimitives.WriteInt32LittleEndian(ch.Slice(n + 8), level.LevelX);
                            BinaryPrimitives.WriteInt32LittleEndian(ch.Slice(n + 12), level.LevelY);
                            n += 16;
                        }
                        else
                        {
                            BinaryPrimitives.WriteInt32LittleEndian(ch.Slice(n), plan.Y0 + c * plan.LinesPerChunk);
                            n += 4;
                        }
                        // deep chunks start with their own three size fields instead of the single data size
                        if (plan.Deep == null) { BinaryPrimitives.WriteInt32LittleEndian(ch.Slice(n), plan.Chunks[c].Length); n += 4; }
                        output.Write(ch.Slice(0, n));
                        output.Write(plan.Chunks[c]);
                        pos += n + plan.Chunks[c].Length;
                        plan.Chunks[c] = null!;     // not needed any more
                    }
                }
                if (backPatch)
                {
                    output.Position = origin + head.Length;
                    WriteTables(output);
                    output.Position = origin + pos;
                }
                }
                catch when (backPatch)
                {
                    // leave no half written file behind
                    try { output.Position = origin; output.SetLength(origin); } catch (Exception e) when (e is NotSupportedException or IOException) { }
                    throw;
                }
            }
            finally
            {
                foreach (var h in pins) if (h.IsAllocated) h.Free();
            }
        }

        static ExrBox2i DisplayWindow(Part p)
        {
            foreach (var a in p.Options.Attributes) if (a.Name == "displayWindow") return a.AsBox2i();
            return p.Options.DisplayWindow ?? new ExrBox2i(0, 0, p.Width - 1, p.Height - 1);
        }

        static float PixelAspect(Part p)
        {
            foreach (var a in p.Options.Attributes) if (a.Name == "pixelAspectRatio") return a.AsFloat();
            return 1f;
        }

        static Level LevelOf(Part p, int chunk)
        {
            for (int i = p.Levels.Count - 1; i > 0; i--) if (chunk >= p.Levels[i].FirstChunk) return p.Levels[i];
            return p.Levels[0];
        }

        /// <summary>Chunk numbers in the order they are written.</summary>
        static IEnumerable<int> FileOrder(Part p)
        {
            bool dec = p.LineOrder == ExrLineOrder.DecreasingY;
            if (!p.Tiled)
            {
                for (int k = 0; k < p.Chunks.Length; k++) yield return dec ? p.Chunks.Length - 1 - k : k;
                yield break;
            }
            foreach (var l in p.Levels)
                for (int row = 0; row < l.TilesY; row++)
                {
                    int ty = dec ? l.TilesY - 1 - row : row;
                    for (int tx = 0; tx < l.TilesX; tx++) yield return l.FirstChunk + ty * l.TilesX + tx;
                }
        }

        static void CheckName(string name, string what, ref bool longNames)
        {
            int n = Encoding.UTF8.GetByteCount(name);
            if (n == 0 || n > 255 || name.Contains('\0')) throw new ArgumentException($"invalid {what} name '{name}' (1..255 bytes)");
            if (n > 31) longNames = true;
        }

        static Part Plan(ExrPart src, int index, bool multiPart, List<GCHandle> pins, ref bool longNames)
        {
            var opt = src.Options;
            int width = src.Width, height = src.Height;
            if (src.Deep != null) return PlanDeep(src, index, multiPart, ref longNames);
            if (src.Channels == null || src.Channels.Count == 0) throw new ArgumentException("no channels given");
            if (width <= 0 || height <= 0) throw new ArgumentException("image dimensions must be positive");
            if (opt.Compression < ExrCompression.None || opt.Compression > ExrCompression.Zstd || opt.Compression == ExrCompression.Lj2k)
                throw new ImageNotSupportedException($"EXR compression {opt.Compression} is not supported for encoding");
            const int lim = 1 << 30;
            long maxX = (long)opt.DataWindowX + width - 1, maxY = (long)opt.DataWindowY + height - 1;
            if (opt.DataWindowX <= -lim || opt.DataWindowY <= -lim || maxX >= lim || maxY >= lim)
                throw new ArgumentException("data window is out of range");
            bool tiled = opt.TileWidth > 0 || opt.TileHeight > 0;
            if (tiled && (opt.TileWidth <= 0 || opt.TileHeight <= 0)) throw new ArgumentException("TileWidth and TileHeight must both be set");
            if (!tiled && opt.LevelMode != ExrLevelMode.OneLevel) throw new ArgumentException("mip / rip levels need a tiled part (set TileWidth and TileHeight)");

            // channels are stored sorted by name
            var sorted = new List<ExrChannelData>(src.Channels);
            sorted.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            var p = new Part
            {
                Index = index, Width = width, Height = height, X0 = opt.DataWindowX, Y0 = opt.DataWindowY, Options = opt, Name = src.Name,
                Tiled = tiled, TileWidth = opt.TileWidth, TileHeight = opt.TileHeight, LevelMode = opt.LevelMode, Channels = sorted,
                LineOrder = tiled ? opt.LineOrder : opt.LineOrder == ExrLineOrder.DecreasingY ? ExrLineOrder.DecreasingY : ExrLineOrder.IncreasingY,
                LinesPerChunk = ExrCodecs.LinesPerChunk(opt.Compression),
                Chans = new ExrChan[sorted.Count], RoundUp = opt.LevelRoundUp,
            };
            var level0 = new Level { Width = width, Height = height, Sources = new Source[sorted.Count] };
            long lineBytes = 0;
            for (int i = 0; i < sorted.Count; i++)
            {
                var c = sorted[i];
                CheckName(c.Name, "channel", ref longNames);
                if (i > 0 && sorted[i - 1].Name == c.Name) throw new ArgumentException($"channel '{c.Name}' is given twice");
                var et = c.Data.GetType().GetElementType();
                int kind = et == typeof(uint) ? 0 : et == typeof(Half) ? 1 : et == typeof(float) ? 2
                    : throw new ImageNotSupportedException($"OpenEXR cannot store {et} samples; use float, Half or uint");
                var type = c.PixelType ?? (kind == 0 ? ExrPixelType.UInt : ExrPixelType.Half);
                if ((kind == 0) != (type == ExrPixelType.UInt))
                    throw new ImageNotSupportedException($"channel '{c.Name}': cannot store {(kind == 0 ? "uint" : "floating point")} samples as {type}");
                int xs = c.XSampling, ys = c.YSampling;
                if (xs < 1 || ys < 1) throw new ArgumentException($"channel '{c.Name}' has invalid sampling");
                if (xs != 1 || ys != 1)
                {
                    if (tiled) throw new ArgumentException("tiled parts cannot have subsampled channels");
                    // the format requires the data window to start and end on sample positions
                    if (ExrChunk.FloorDiv(opt.DataWindowX, xs) * xs != opt.DataWindowX || ExrChunk.FloorDiv(opt.DataWindowY, ys) * ys != opt.DataWindowY
                        || width % xs != 0 || height % ys != 0)
                        throw new ArgumentException($"channel '{c.Name}': data window position and size must be multiples of the sampling {xs}x{ys}");
                }
                int pw = width / xs, ph = height / ys;
                if (c.Data.LongLength < c.Offset + ((long)pw * ph - 1) * c.Stride + 1)
                    throw new ArgumentException($"channel '{c.Name}' has too few samples for {pw}x{ph}");
                p.Chans[i] = new ExrChan { Name = c.Name, Type = (int)type, XS = xs, YS = ys, PLinear = c.PLinear, Bytes = type == ExrPixelType.Half ? 2 : 4 };
                level0.Sources[i] = new Source { Array = c.Data, Kind = kind, Offset = c.Offset, Stride = c.Stride, PlaneWidth = pw };
                lineBytes += (long)pw * p.Chans[i].Bytes;
            }
            if (lineBytes * (tiled ? Math.Min(height, opt.TileHeight) : p.LinesPerChunk) > int.MaxValue) throw new ImageNotSupportedException("EXR chunks are too large");

            // levels
            p.Levels.Add(level0);
            if (opt.LevelMode == ExrLevelMode.MipMap)
            {
                var prev = level0;
                for (int l = 1; prev.Width > 1 || prev.Height > 1; l++)
                {
                    prev = Reduce(prev, prev.Width > 1, prev.Height > 1, p.RoundUp, opt.LevelFilter);
                    prev.LevelX = prev.LevelY = l;
                    p.Levels.Add(prev);
                }
            }
            else if (opt.LevelMode == ExrLevelMode.RipMap)
            {
                // order of the offset table: all x levels for y level 0, then y level 1, ...
                var rowStart = level0;
                for (int ly = 0; ; ly++)
                {
                    var prev = rowStart;
                    for (int lx = 1; prev.Width > 1; lx++)
                    {
                        prev = Reduce(prev, true, false, p.RoundUp, opt.LevelFilter);
                        prev.LevelX = lx; prev.LevelY = ly;
                        p.Levels.Add(prev);
                    }
                    if (rowStart.Height <= 1) break;
                    rowStart = Reduce(rowStart, false, true, p.RoundUp, opt.LevelFilter);
                    rowStart.LevelX = 0; rowStart.LevelY = ly + 1;
                    p.Levels.Add(rowStart);
                }
            }
            int chunkCount = 0;
            foreach (var l in p.Levels)
            {
                l.FirstChunk = chunkCount;
                if (tiled)
                {
                    l.TilesX = (l.Width + opt.TileWidth - 1) / opt.TileWidth;
                    l.TilesY = (l.Height + opt.TileHeight - 1) / opt.TileHeight;
                    chunkCount = checked(chunkCount + l.TilesX * l.TilesY);
                }
                else chunkCount = (height + p.LinesPerChunk - 1) / p.LinesPerChunk;
                for (int i = 0; i < l.Sources.Length; i++)
                {
                    var h = GCHandle.Alloc(l.Sources[i].Array, GCHandleType.Pinned);
                    pins.Add(h);
                    l.Sources[i].Data = (byte*)h.AddrOfPinnedObject();
                }
            }
            p.Chunks = new byte[chunkCount][];
            p.Header = BuildHeader(p, multiPart, chunkCount, ref longNames);
            return p;
        }

        /// <summary>
        /// Next coarser level: floating point channels are averaged over the 2 (one direction) or 2x2 samples a pixel
        /// covers, UINT channels take the top left sample because ids cannot be averaged. With ROUND_UP sizes the last
        /// pixel of an odd sized level covers a single sample in that direction.
        /// </summary>
        static readonly float[] Tent = { 0.125f, 0.375f, 0.375f, 0.125f }, One = { 1f };

        static Level Reduce(Level src, bool inX, bool inY, bool roundUp, ExrLevelFilter filter)
        {
            int Half(int n) => Math.Max(1, roundUp ? (n + 1) / 2 : n / 2);
            int w = inX ? Half(src.Width) : src.Width, h = inY ? Half(src.Height) : src.Height;
            int sx = inX && src.Width > 1 ? 2 : 1, sy = inY && src.Height > 1 ? 2 : 1;
            var l = new Level { Width = w, Height = h, Sources = new Source[src.Sources.Length] };
            for (int i = 0; i < src.Sources.Length; i++)
            {
                var s = src.Sources[i];
                long At(int x, int y) => s.Offset + ((long)y * src.Width + x) * s.Stride;
                Array result;
                if (s.Kind == 0)
                {
                    var a = (uint[])s.Array; var r = new uint[w * h];
                    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) r[y * w + x] = a[At(x * sx, y * sy)];
                    result = r;
                }
                else
                {
                    var r = new float[w * h];
                    var fa = s.Array as float[];
                    var ha = s.Array as Half[];
                    if (filter == ExrLevelFilter.Triangle)
                    {
                        // separable (1, 3, 3, 1) / 8 around each 2x2 group, edge samples repeated
                        float Get(int x, int y)
                        {
                            long k = At(Math.Clamp(x, 0, src.Width - 1), Math.Clamp(y, 0, src.Height - 1));
                            return fa != null ? fa[k] : (float)ha![k];
                        }
                        float[] wx = sx == 2 ? Tent : One, wy = sy == 2 ? Tent : One;
                        for (int y = 0; y < h; y++)
                            for (int x = 0; x < w; x++)
                            {
                                float sum = 0;
                                for (int j = 0; j < wy.Length; j++)
                                {
                                    float line = 0;
                                    for (int ix = 0; ix < wx.Length; ix++) line += wx[ix] * Get(x * sx + ix - (sx == 2 ? 1 : 0), y * sy + j - (sy == 2 ? 1 : 0));
                                    sum += wy[j] * line;
                                }
                                r[y * w + x] = sum;
                            }
                    }
                    else
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            float sum = 0;
                            int n = 0;
                            for (int dy = 0; dy < sy && y * sy + dy < src.Height; dy++)
                                for (int dx = 0; dx < sx && x * sx + dx < src.Width; dx++, n++)
                                {
                                    long k = At(x * sx + dx, y * sy + dy);
                                    sum += fa != null ? fa[k] : (float)ha![k];
                                }
                            r[y * w + x] = sum * (1f / n);
                        }
                    result = r;
                }
                l.Sources[i] = new Source { Array = result, Kind = s.Kind == 0 ? 0 : 2, Offset = 0, Stride = 1, PlaneWidth = w };
            }
            return l;
        }

        static Part PlanDeep(ExrPart src, int index, bool multiPart, ref bool longNames)
        {
            var opt = src.Options;
            var deep = src.Deep!;
            var comp = opt.Compression == ExrCompression.Zip ? ExrCompression.Zips : opt.Compression;
            if (comp != ExrCompression.None && comp != ExrCompression.Rle && comp != ExrCompression.Zips && comp != ExrCompression.Zstd)
                throw new ImageNotSupportedException($"deep EXR data can only be stored with None, Rle, Zips or Zstd compression, not {opt.Compression}");
            const int lim = 1 << 30;
            if (opt.DataWindowX <= -lim || opt.DataWindowY <= -lim || (long)opt.DataWindowX + deep.Width >= lim || (long)opt.DataWindowY + deep.Height >= lim)
                throw new ArgumentException("data window is out of range");
            bool tiled = opt.TileWidth > 0 || opt.TileHeight > 0;
            if (tiled && (opt.TileWidth <= 0 || opt.TileHeight <= 0)) throw new ArgumentException("TileWidth and TileHeight must both be set");
            var deepLevels = src.DeepLevels!;
            if ((opt.LevelMode != ExrLevelMode.OneLevel) != (deepLevels.Count > 1) && !(opt.LevelMode != ExrLevelMode.OneLevel && deep.Width == 1 && deep.Height == 1))
                throw new ArgumentException(opt.LevelMode == ExrLevelMode.OneLevel
                    ? "several deep levels need LevelMode MipMap or RipMap"
                    : "deep levels cannot be generated (there is no defined way to reduce deep samples): supply every level");
            if (opt.LevelMode != ExrLevelMode.OneLevel && !tiled) throw new ArgumentException("mip / rip levels need a tiled part (set TileWidth and TileHeight)");
            if (opt.LuminanceChroma) throw new ImageNotSupportedException("deep EXR output cannot be luminance/chroma");

            int nc = deep.ChannelNames.Count;
            var order = new int[nc];
            for (int i = 0; i < nc; i++) order[i] = i;
            Array.Sort(order, (a, b) => string.CompareOrdinal(deep.ChannelNames[a], deep.ChannelNames[b]));
            var p = new Part
            {
                Index = index, Width = deep.Width, Height = deep.Height, X0 = opt.DataWindowX, Y0 = opt.DataWindowY, Options = opt, Name = src.Name,
                Tiled = tiled, TileWidth = opt.TileWidth, TileHeight = opt.TileHeight,
                LineOrder = tiled ? opt.LineOrder : opt.LineOrder == ExrLineOrder.DecreasingY ? ExrLineOrder.DecreasingY : ExrLineOrder.IncreasingY,
                LinesPerChunk = 1, Chans = new ExrChan[nc], Deep = deep, DeepOrder = order, DeepCompression = comp, LevelMode = opt.LevelMode,
                Channels = new List<ExrChannelData>(),
            };
            for (int i = 0; i < nc; i++)
            {
                string name = deep.ChannelNames[order[i]];
                CheckName(name, "channel", ref longNames);
                if (i > 0 && deep.ChannelNames[order[i - 1]] == name) throw new ArgumentException($"channel '{name}' is given twice");
                var type = deep.ChannelTypes[order[i]];
                p.Chans[i] = new ExrChan { Name = name, Type = (int)type, XS = 1, YS = 1, Bytes = type == ExrPixelType.Half ? 2 : 4 };
            }
            // level sizes in the order of the offset table; every level has its own, caller supplied, samples
            p.RoundUp = opt.LevelRoundUp;
            int Half(int n) => Math.Max(1, opt.LevelRoundUp ? (n + 1) / 2 : n / 2);
            var sizes = new List<(int lx, int ly, int w, int h)> { (0, 0, deep.Width, deep.Height) };
            if (opt.LevelMode == ExrLevelMode.MipMap)
                for (int l = 1, lw = deep.Width, lh = deep.Height; lw > 1 || lh > 1; l++) { lw = Half(lw); lh = Half(lh); sizes.Add((l, l, lw, lh)); }
            else if (opt.LevelMode == ExrLevelMode.RipMap)
            {
                sizes.Clear();
                for (int ly = 0, lh = deep.Height; ; ly++, lh = Half(lh))
                {
                    for (int lx = 0, lw = deep.Width; ; lx++, lw = Half(lw))
                    {
                        sizes.Add((lx, ly, lw, lh));
                        if (lw == 1) break;
                    }
                    if (lh == 1) break;
                }
            }
            if (sizes.Count != deepLevels.Count)
                throw new ArgumentException($"the part needs {sizes.Count} deep levels ({opt.LevelMode}, in offset table order), {deepLevels.Count} were given");
            int chunkCount = 0;
            for (int i = 0; i < sizes.Count; i++)
            {
                var d = deepLevels[i] ?? throw new ArgumentException("null deep level");
                if (d.Width != sizes[i].w || d.Height != sizes[i].h)
                    throw new ArgumentException($"deep level ({sizes[i].lx}, {sizes[i].ly}) must be {sizes[i].w}x{sizes[i].h}, not {d.Width}x{d.Height}");
                if (d.ChannelNames.Count != nc) throw new ArgumentException("all deep levels need the same channels");
                for (int k = 0; k < nc; k++)
                    if (d.ChannelNames[k] != deep.ChannelNames[k] || d.ChannelTypes[k] != deep.ChannelTypes[k]) throw new ArgumentException("all deep levels need the same channels");
                var level = new Level { LevelX = sizes[i].lx, LevelY = sizes[i].ly, Width = d.Width, Height = d.Height, Deep = d, FirstChunk = chunkCount };
                if (tiled)
                {
                    level.TilesX = (d.Width + opt.TileWidth - 1) / opt.TileWidth;
                    level.TilesY = (d.Height + opt.TileHeight - 1) / opt.TileHeight;
                    chunkCount = checked(chunkCount + level.TilesX * level.TilesY);
                }
                else chunkCount = d.Height;
                p.Levels.Add(level);
            }
            p.Chunks = new byte[chunkCount][];
            p.Header = BuildHeader(p, multiPart, chunkCount, ref longNames);
            return p;
        }

        /// <summary>
        /// A deep chunk: packed size of the sample count table, packed and unpacked size of the sample data (int64
        /// each), then the table (cumulative counts along each row of the chunk) and the data (per row, per channel,
        /// the samples of the row's pixels). Either block is stored raw when compression does not shrink it; a raw
        /// table always has the size of a full tile, which is how readers recognise it.
        /// </summary>
        static void EncodeDeepChunk(Part p, int index, ExrScratch s)
        {
            var level = LevelOf(p, index);
            var deep = level.Deep!;
            int w = level.Width, x0 = 0, y0 = index, cw = w, ch = 1;
            long nominal = (long)w * 4;
            if (p.Tiled)
            {
                int t = index - level.FirstChunk;
                x0 = t % level.TilesX * p.TileWidth; y0 = t / level.TilesX * p.TileHeight;
                cw = Math.Min(p.TileWidth, w - x0); ch = Math.Min(p.TileHeight, level.Height - y0);
                nominal = (long)p.TileWidth * p.TileHeight * 4;
            }
            if (nominal > int.MaxValue) throw new ImageNotSupportedException("EXR tiles are too large");
            var table = new byte[cw * ch * 4];
            long bytes = 0;
            int bytesPerSample = 0;
            foreach (var c in p.Chans) bytesPerSample += c.Bytes;
            for (int y = 0; y < ch; y++)
            {
                int first = (y0 + y) * w + x0, start = deep.SampleOffsets[first];
                for (int x = 0; x < cw; x++)
                    BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan((y * cw + x) * 4), deep.SampleOffsets[first + x + 1] - start);
                bytes += (long)(deep.SampleOffsets[first + cw] - start) * bytesPerSample;
            }
            if (bytes > int.MaxValue) throw new ImageNotSupportedException("EXR deep chunk is too large");
            var data = new byte[bytes];
            var rowSamples = new int[ch];
            int o = 0;
            for (int y = 0; y < ch; y++)
            {
                int firstPixel = (y0 + y) * w + x0, first = deep.SampleOffsets[firstPixel], n = deep.SampleOffsets[firstPixel + cw] - first;
                rowSamples[y] = n;
                for (int ci = 0; ci < p.Chans.Length; ci++)
                {
                    var arr = deep.ChannelData[p.DeepOrder[ci]];
                    int type = p.Chans[ci].Type;
                    if (type == 0) MemoryMarshal.AsBytes(((uint[])arr).AsSpan(first, n)).CopyTo(data.AsSpan(o));
                    else if (type == 2) MemoryMarshal.AsBytes(((float[])arr).AsSpan(first, n)).CopyTo(data.AsSpan(o));
                    else
                    {
                        var f = ((float[])arr).AsSpan(first, n);
                        var d = MemoryMarshal.Cast<byte, ushort>(data.AsSpan(o, n * 2));
                        for (int j = 0; j < n; j++) d[j] = ExrHalf.FromFloat(f[j]);
                    }
                    o += n * p.Chans[ci].Bytes;
                }
            }

            byte[] packedTable = table, packedData = data;
            if (p.DeepCompression == ExrCompression.Zstd)
            {
                packedTable = ExrZstd.EncodeTable(table);
                if (data.Length > 0)
                {
                    ExrZstd.DeepData(default, data, p.Chans, rowSamples, ch, s, false, out var z);
                    if (z!.Length < data.Length) packedData = z;
                }
            }
            else if (p.DeepCompression != ExrCompression.None)
            {
                packedTable = ExrCodecs.Encode(p.DeepCompression, table, default, s, p.Options.ZipLevel, 0);
                if (data.Length > 0) packedData = ExrCodecs.Encode(p.DeepCompression, data, default, s, p.Options.ZipLevel, 0);
            }
            // not smaller (Encode then returns the input), or by coincidence as long as a raw table: store raw
            if (p.DeepCompression == ExrCompression.Zstd)
            {
                // ZSTD readers take a table of exactly the (clipped) table size as raw
                if (packedTable.Length >= table.Length) packedTable = table;
            }
            else if (packedTable.Length >= table.Length || packedTable.Length == nominal)
            {
                packedTable = new byte[nominal];
                table.CopyTo(packedTable, 0);
            }
            var chunk = new byte[24 + packedTable.Length + packedData.Length];
            BinaryPrimitives.WriteInt64LittleEndian(chunk, packedTable.Length);
            BinaryPrimitives.WriteInt64LittleEndian(chunk.AsSpan(8), packedData.Length);
            BinaryPrimitives.WriteInt64LittleEndian(chunk.AsSpan(16), data.Length);
            packedTable.CopyTo(chunk, 24);
            packedData.CopyTo(chunk, 24 + packedTable.Length);
            p.Chunks[index] = chunk;
        }

        static byte[] BuildHeader(Part p, bool multiPart, int chunkCount, ref bool longNames)
        {
            var opt = p.Options;
            var hdr = new MemoryStream();
            var w = new BinaryWriter(hdr);
            var custom = new List<ExrAttribute>(opt.Attributes);
            foreach (var a in custom)
            {
                if (a == null) throw new ArgumentException("null attribute");
                CheckName(a.Name, "attribute", ref longNames);
                CheckName(a.TypeName, "attribute type", ref longNames);
                if (IsStructural(a.Name) || a.Name == "name")
                    throw new ArgumentException($"attribute '{a.Name}' is written by the encoder and cannot be supplied");
            }
            bool Has(string name) => custom.Exists(a => a.Name == name);
            void Attr(ExrAttribute a)
            {
                w.Write(Encoding.UTF8.GetBytes(a.Name)); w.Write((byte)0);
                w.Write(Encoding.UTF8.GetBytes(a.TypeName)); w.Write((byte)0);
                w.Write(a.Value.Length);
                w.Write(a.Value);
            }

            var chl = new MemoryStream();
            Span<byte> rec = stackalloc byte[17];
            foreach (var c in p.Chans)
            {
                chl.Write(Encoding.UTF8.GetBytes(c.Name));
                rec.Clear();
                BinaryPrimitives.WriteInt32LittleEndian(rec.Slice(1), c.Type);
                rec[5] = (byte)(c.PLinear ? 1 : 0);
                BinaryPrimitives.WriteInt32LittleEndian(rec.Slice(9), c.XS);
                BinaryPrimitives.WriteInt32LittleEndian(rec.Slice(13), c.YS);
                chl.Write(rec);
            }
            chl.WriteByte(0);

            Attr(new ExrAttribute("channels", "chlist", chl.ToArray()));
            Attr(new ExrAttribute("compression", "compression", new[] { (byte)(p.Deep != null ? p.DeepCompression : opt.Compression) }));
            Attr(ExrAttribute.Box2i("dataWindow", new ExrBox2i(p.X0, p.Y0, p.X0 + p.Width - 1, p.Y0 + p.Height - 1)));
            if (!Has("displayWindow")) Attr(ExrAttribute.Box2i("displayWindow", opt.DisplayWindow ?? new ExrBox2i(0, 0, p.Width - 1, p.Height - 1)));
            Attr(new ExrAttribute("lineOrder", "lineOrder", new[] { (byte)p.LineOrder }));
            if (!Has("pixelAspectRatio")) Attr(ExrAttribute.Float("pixelAspectRatio", 1f));
            if (!Has("screenWindowCenter")) Attr(ExrAttribute.V2f("screenWindowCenter", 0f, 0f));
            if (!Has("screenWindowWidth")) Attr(ExrAttribute.Float("screenWindowWidth", 1f));
            if (opt.Compression is ExrCompression.Dwaa or ExrCompression.Dwab) Attr(ExrAttribute.Float("dwaCompressionLevel", opt.DwaCompressionLevel));
            if (p.Tiled)
            {
                var t = new byte[9];
                BinaryPrimitives.WriteInt32LittleEndian(t, p.TileWidth);
                BinaryPrimitives.WriteInt32LittleEndian(t.AsSpan(4), p.TileHeight);
                t[8] = (byte)((int)p.LevelMode | (p.RoundUp ? 0x10 : 0));      // level mode, rounding mode in the high nibble
                Attr(new ExrAttribute("tiles", "tiledesc", t));
            }
            if (multiPart || p.Tiled || p.Deep != null)
            {
                Attr(ExrAttribute.String("type", p.Deep != null ? (p.Tiled ? "deeptile" : "deepscanline") : p.Tiled ? "tiledimage" : "scanlineimage"));
                Attr(ExrAttribute.Int("chunkCount", chunkCount));
            }
            if (p.Deep != null)
            {
                int max = 0;
                foreach (var l in p.Levels)
                    foreach (int n in l.Deep!.SampleCounts) max = Math.Max(max, n);
                Attr(ExrAttribute.Int("version", 1));
                Attr(ExrAttribute.Int("maxSamplesPerPixel", max));
            }
            if (multiPart) Attr(ExrAttribute.String("name", p.Name!));
            foreach (var a in custom) Attr(a);
            w.Write((byte)0);
            w.Flush();
            return hdr.ToArray();
        }

        static void EncodeChunk(Part p, int index, ExrScratch s)
        {
            if (p.Deep != null) { EncodeDeepChunk(p, index, s); return; }
            Level level;
            int x0, x1, y0, y1;
            if (p.Tiled)
            {
                level = LevelOf(p, index);
                int t = index - level.FirstChunk, tx = t % level.TilesX, ty = t / level.TilesX;
                x0 = tx * p.TileWidth; x1 = Math.Min(x0 + p.TileWidth, level.Width) - 1;
                y0 = ty * p.TileHeight; y1 = Math.Min(y0 + p.TileHeight, level.Height) - 1;
            }
            else
            {
                level = p.Levels[0];
                x0 = 0; x1 = p.Width - 1;
                y0 = index * p.LinesPerChunk; y1 = Math.Min(y0 + p.LinesPerChunk, p.Height) - 1;
            }
            // chunk geometry is in data window coordinates; x0.. above are relative to the level's top left pixel
            var chunk = ExrChunk.Create(p.Chans, p.X0 + x0, p.X0 + x1, p.Y0 + y0, p.Y0 + y1, s);
            int size = checked((int)chunk.Size);
            var raw = s.Bytes(2, size);
            fixed (byte* pr = raw)
            fixed (uint* h2f = ExrHalf.ToFloatBits)
            {
                byte* o = pr;
                for (int row = y0; row <= y1; row++)
                    for (int ci = 0; ci < p.Chans.Length; ci++)
                    {
                        ref readonly ExrChan ch = ref p.Chans[ci];
                        if (row % ch.YS != 0) continue;
                        ref readonly Source src = ref level.Sources[ci];
                        int n = chunk.Nx[ci];
                        long first = src.Offset + ((long)(row / ch.YS) * src.PlaneWidth + x0 / ch.XS) * src.Stride, st = src.Stride;
                        if (src.Kind == 2 && ch.Type == 1)
                        {
                            float* f = (float*)src.Data + first;
                            ushort* d = (ushort*)o;
                            for (int j = 0; j < n; j++) d[j] = ExrHalf.FromFloat(f[j * st]);
                        }
                        else if (src.Kind == 1 && ch.Type == 2)
                        {
                            ushort* hs = (ushort*)src.Data + first;
                            uint* d = (uint*)o;
                            for (int j = 0; j < n; j++) d[j] = h2f[hs[j * st]];
                        }
                        else if (src.Kind == 1)
                        {
                            ushort* hs = (ushort*)src.Data + first;
                            ushort* d = (ushort*)o;
                            if (st == 1) Buffer.MemoryCopy(hs, d, 2L * n, 2L * n);
                            else for (int j = 0; j < n; j++) d[j] = hs[j * st];
                        }
                        else
                        {
                            // float -> FLOAT and uint -> UINT are plain 32 bit copies
                            uint* u = (uint*)src.Data + first;
                            uint* d = (uint*)o;
                            if (st == 1) Buffer.MemoryCopy(u, d, 4L * n, 4L * n);
                            else for (int j = 0; j < n; j++) d[j] = u[j * st];
                        }
                        o += (long)n * ch.Bytes;
                    }
            }
            p.Chunks[index] = ExrCodecs.Encode(p.Options.Compression, raw.AsSpan(0, size), chunk, s, p.Options.ZipLevel, p.Options.DwaCompressionLevel);
        }
    }
}
