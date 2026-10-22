using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// Destination for the common case that the components are the channels of the result: the tiles are written
    /// straight into the interleaved 8 or 16 bit image instead of into component planes.
    /// </summary>
    internal sealed class J2kSink
    {
        public SampleType Type;              // UInt8 or UInt16
        public int Channels;
        public int[] ChannelOf = Array.Empty<int>();   // component -> channel
        public Array? Data;                  // allocated by the decoder once the size is accepted
    }

    /// <summary>Decodes a codestream into component planes: tile by tile packets, code-blocks, wavelet, colour transform.</summary>
    internal sealed unsafe class J2kDecoder
    {
        readonly J2kSink? _sink;
        readonly J2kCodestream _cs;
        readonly int _reduce, _maxLayers;
        readonly bool _parallel, _strict;
        J2kRect _region;                     // wanted area on the reference grid
        readonly bool[] _needed;
        readonly J2kPlane[] _planes;
        readonly long[] _px0, _py0;          // plane origin in component coordinates of the output resolution
        List<byte[]>? _ppm;
        readonly ConcurrentBag<J2kBlockCoder> _coders = new ConcurrentBag<J2kBlockCoder>();
        public bool Truncated;

        struct Job { public J2kTileComp Tc; public J2kBand Band; public J2kResolution Res; public J2kPrecinctBand Pb; public int Block; }

        J2kDecoder(J2kCodestream cs, Jp2DecodeOptions o, int[]? components, J2kSink? sink)
        {
            _cs = cs; _sink = sink;
            _reduce = o.Reduce; _maxLayers = o.MaxLayers > 0 ? o.MaxLayers : int.MaxValue;
            _parallel = o.Parallel; _strict = !o.AllowTruncated;
            var siz = cs.Siz;
            if (_reduce < 0 || _reduce > 32) throw new ArgumentOutOfRangeException(nameof(o.Reduce));
            _region = new J2kRect(siz.X0, siz.Y0, siz.X1, siz.Y1);
            if (o.Region is { } rg)
            {
                if (rg.X < 0 || rg.Y < 0 || rg.Width <= 0 || rg.Height <= 0) throw new ArgumentException("bad region");
                _region = new J2kRect(siz.X0 + rg.X, siz.Y0 + rg.Y, siz.X0 + (long)rg.X + rg.Width, siz.Y0 + (long)rg.Y + rg.Height).Intersect(_region);
                if (_region.Empty) throw new ArgumentException("the region lies outside the image");
            }
            int nc = siz.NumComps;
            _needed = new bool[nc];
            if (components == null) Array.Fill(_needed, true);
            else foreach (int c in components)
            {
                if (c < 0 || c >= nc) throw new ArgumentException("component index out of range");
                _needed[c] = true;
            }
            _planes = new J2kPlane[nc];
            _px0 = new long[nc]; _py0 = new long[nc];
            for (int c = 0; c < nc; c++)
            {
                Range(_region.X0, _region.X1, siz.X0, siz.Dx[c], out _px0[c], out long x1);
                Range(_region.Y0, _region.Y1, siz.Y0, siz.Dy[c], out _py0[c], out long y1);
                _planes[c] = new J2kPlane
                {
                    Width = (int)(x1 - _px0[c]), Height = (int)(y1 - _py0[c]), Dx = siz.Dx[c], Dy = siz.Dy[c],
                    Precision = siz.Prec[c], Signed = siz.Signed[c],
                };
            }
        }

        /// <summary>Samples of a component (spacing d on the reference grid) that the area [r0, r1) needs, at the output resolution.</summary>
        void Range(long r0, long r1, long image0, int d, out long p0, out long p1)
        {
            // the sample left of the area is replicated into it when no sample sits on its first column
            long a0 = Math.Max(r0 / d, J2kGeometry.CeilDiv(image0, d)), a1 = J2kGeometry.CeilDiv(r1, d);
            p0 = J2kGeometry.CeilShift(a0, _reduce); p1 = J2kGeometry.CeilShift(a1, _reduce);
            if (p1 <= p0)
            {
                p0 = Math.Max(a0 >> _reduce, J2kGeometry.CeilShift(J2kGeometry.CeilDiv(image0, d), _reduce));
                p1 = p0 + 1;
            }
        }

        /// <summary>Decodes a parsed codestream; with a sink the planes come back without data.</summary>
        public static J2kPlanes Decode(J2kCodestream cs, Jp2DecodeOptions o, int[]? components = null, J2kSink? sink = null)
            => new J2kDecoder(cs, o, components, sink).Run(o);

        J2kPlanes Run(Jp2DecodeOptions o)
        {
            var siz = _cs.Siz;
            long outW = Math.Max(1, J2kGeometry.CeilShift(_region.X1, _reduce) - J2kGeometry.CeilShift(_region.X0, _reduce));
            long outH = Math.Max(1, J2kGeometry.CeilShift(_region.Y1, _reduce) - J2kGeometry.CeilShift(_region.Y0, _reduce));
            if (outW * outH > o.MaxPixels || outW > int.MaxValue || outH > int.MaxValue)
                throw new ImageNotSupportedException($"JPEG 2000 image area of {outW} x {outH} pixels exceeds Jp2DecodeOptions.MaxPixels");
            if (_strict && _cs.Truncated) throw J2kCodestream.Bad("truncated codestream");
            _ppm = _cs.SplitPpm();
            if (_strict && _cs.Truncated) throw J2kCodestream.Bad("truncated packed packet headers");

            // tiles touching the area
            int tx0 = (int)((_region.X0 - siz.TileX0) / siz.TileW), tx1 = (int)((_region.X1 - 1 - siz.TileX0) / siz.TileW);
            int ty0 = (int)((_region.Y0 - siz.TileY0) / siz.TileH), ty1 = (int)((_region.Y1 - 1 - siz.TileY0) / siz.TileH);
            var tiles = new List<int>();
            for (int ty = ty0; ty <= ty1; ty++)
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    int t = ty * siz.TilesX + tx;
                    if (_cs.Tiles[t] == null || _cs.Tiles[t]!.Parts.Count == 0)
                    {
                        // A codestream that is properly closed may leave tiles out (T.803 has such streams); they stay 0.
                        if (!_cs.SawEoc)
                        {
                            if (_strict) throw J2kCodestream.Bad("missing tile");
                            Truncated = true;
                        }
                        continue;
                    }
                    tiles.Add(t);
                }

            // The first tile is parsed before anything large is allocated, so that a header describing a huge
            // image over a few bytes of data fails here.
            J2kTile? first = tiles.Count > 0 ? Parse(tiles[0]) : null;
            if (_sink != null)
            {
                long n = outW * outH * _sink.Channels;
                if (n > Array.MaxLength) throw new ImageNotSupportedException("JPEG 2000 image too large for one array");
                _sink.Data = _sink.Type == SampleType.UInt8 ? new byte[n] : new ushort[n];
            }
            else
                for (int c = 0; c < _planes.Length; c++)
                {
                    if (!_needed[c]) continue;
                    var p = _planes[c];
                    long n = (long)p.Width * p.Height;
                    if (n > o.MaxPixels || n > Array.MaxLength) throw new ImageNotSupportedException("JPEG 2000 component exceeds Jp2DecodeOptions.MaxPixels");
                    if (p.Precision > 32) p.Data64 = new long[n]; else p.Data = new int[n];
                }
            if (_parallel && tiles.Count > 1)
            {
                Parallel.For(0, tiles.Count, i => DecodeTile(i == 0 ? first! : Parse(tiles[i])));
            }
            else
            {
                for (int i = 0; i < tiles.Count; i++) DecodeTile(i == 0 ? first! : Parse(tiles[i]));
            }
            bool mct = false;
            for (int t = 0; t < _cs.Tiles.Length && !mct; t++)
                mct = (_cs.Tiles[t]?.Params is { HasCod: true } tp ? tp.Mct : _cs.Main.Mct) != 0;
            for (int c = 0; c < _planes.Length; c++) { _planes[c].X0 = _px0[c]; _planes[c].Y0 = _py0[c]; }
            return new J2kPlanes
            {
                Width = (int)outW, Height = (int)outH, Planes = _planes, UsedColorTransform = mct, Truncated = Truncated,
                X0 = J2kGeometry.CeilShift(_region.X0, _reduce), Y0 = J2kGeometry.CeilShift(_region.Y0, _reduce),
                NltSignMagnitude = _cs.NltSignMagnitude,
            };
        }

        /// <summary>Builds the tile, decides what is decoded and reads its packets.</summary>
        J2kTile Parse(int index)
        {
            var tile = J2kGeometry.BuildTile(_cs, index);
            int nc = tile.Comps.Length;
            bool mct = tile.Mct != 0 && nc >= 3;
            for (int c = 0; c < nc; c++)
            {
                var tc = tile.Comps[c];
                tc.Needed = _needed[c] || (mct && c < 3 && (_needed[0] || _needed[1] || _needed[2]));
                if (tc.Needed && tc.Cod.Levels < _reduce)
                    throw new ArgumentException($"Reduce = {_reduce} but the image has only {tc.Cod.Levels} decomposition levels");
                tc.OutRes = Math.Max(tc.Cod.Levels - _reduce, 0);
                var want = new J2kRect(_px0[c], _py0[c], _px0[c] + _planes[c].Width, _py0[c] + _planes[c].Height);
                J2kGeometry.SetupWindows(tc, want);
            }
            if (mct)
            {
                var a = tile.Comps[0]; var b = tile.Comps[1]; var d = tile.Comps[2];
                bool same = a.Rect.Equals(b.Rect) && a.Rect.Equals(d.Rect) && a.Cod.Reversible == b.Cod.Reversible && a.Cod.Reversible == d.Cod.Reversible;
                if (!same) throw J2kCodestream.Bad("multi-component transform over components of different geometry");
                if (a.Wide || b.Wide || d.Wide) a.Wide = b.Wide = d.Wide = true;
            }
            var reader = new J2kPacketReader(_cs, tile, _ppm, _maxLayers, _strict);
            reader.Run();
            if (tile.Truncated)
            {
                if (_strict) throw J2kCodestream.Bad("truncated tile data");
                Truncated = true;
            }
            return tile;
        }

        J2kBlockCoder RentCoder() => _coders.TryTake(out var c) ? c : new J2kBlockCoder();

        /// <summary>The zeroed buffer of the output level, from the shared pool.</summary>
        static void RentTop(J2kTileComp tc)
        {
            var top = tc.Res[tc.OutRes];
            int n = checked(top.W * top.H);
            Array buf;
            if (!tc.Cod.Reversible) { var b = ArrayPool<float>.Shared.Rent(n); b.AsSpan(0, n).Clear(); buf = b; }
            else if (tc.Wide) { var b = ArrayPool<long>.Shared.Rent(n); b.AsSpan(0, n).Clear(); buf = b; }
            else { var b = ArrayPool<int>.Shared.Rent(n); b.AsSpan(0, n).Clear(); buf = b; }
            top.Buffer = tc.Pooled = buf;
            top.Off = 0; top.Stride = top.W;
        }

        static void ReturnTop(J2kTileComp tc)
        {
            switch (tc.Pooled)
            {
                case float[] f: ArrayPool<float>.Shared.Return(f); break;
                case long[] l: ArrayPool<long>.Shared.Return(l); break;
                case int[] i: ArrayPool<int>.Shared.Return(i); break;
            }
            tc.Pooled = null;
            foreach (var res in tc.Res) res.Buffer = null;
        }

        void DecodeTile(J2kTile tile)
        {
            // code-blocks with data inside the windows
            var jobs = new List<Job>();
            foreach (var tc in tile.Comps)
            {
                if (!tc.Needed) continue;
                int before = jobs.Count;
                for (int r = 0; r <= tc.OutRes; r++)
                {
                    var res = tc.Res[r];
                    if (res.Precincts == null || res.W == 0 || res.H == 0) continue;
                    foreach (var prec in res.Precincts)
                    {
                        if (prec == null) continue;
                        for (int b = 0; b < prec.Bands.Length; b++)
                        {
                            var pb = prec.Bands[b];
                            var band = res.Bands[b];
                            if (pb.Blocks.Length == 0 || pb.Region.Intersect(band.Win).Empty) continue;
                            for (int i = 0; i < pb.Blocks.Length; i++)
                            {
                                if (pb.Blocks[i].FirstChunk == 0) continue;
                                if (BlockRect(res, pb, i).Intersect(band.Win).Empty) continue;
                                jobs.Add(new Job { Tc = tc, Band = band, Res = res, Pb = pb, Block = i });
                            }
                        }
                    }
                }
                if (jobs.Count == before) continue;
                tc.HasData = true;
            }
            // zeroed level buffers (blocks without data stay zero); clearing them is worth a thread per component
            void Allocate(int c)
            {
                var tc = tile.Comps[c];
                if (!tc.HasData) return;
                var top = tc.Res[tc.OutRes];
                if ((long)top.W * top.H == 0) return;
                RentTop(tc);
                // Each level lives in the even rows and low columns of the level above (where the synthesis wants
                // it), so that one buffer holds them all; a window that does not line up gets a buffer of its own.
                for (int r = tc.OutRes; r > 0; r--)
                {
                    var res = tc.Res[r]; var low = tc.Res[r - 1];
                    long n = (long)low.W * low.H;
                    if (n == 0) continue;
                    if (res.Buffer != null && low.C.X0 == (res.C.X0 + 1) >> 1 && low.C.Y0 == (res.C.Y0 + 1) >> 1
                        && low.W == res.NLow && low.H == ((res.C.Y1 + 1) >> 1) - ((res.C.Y0 + 1) >> 1))
                    {
                        low.Buffer = res.Buffer;
                        low.Off = res.Off + (int)(res.C.Y0 & 1) * res.Stride;
                        low.Stride = 2 * res.Stride;
                    }
                    else
                    {
                        low.Buffer = !tc.Cod.Reversible ? new float[n] : tc.Wide ? new long[n] : new int[n];
                        low.Off = 0; low.Stride = low.W;
                    }
                }
            }
            if (_parallel && tile.Comps.Length > 1) Parallel.For(0, tile.Comps.Length, Allocate);
            else for (int c = 0; c < tile.Comps.Length; c++) Allocate(c);

            if (_parallel && jobs.Count > 1)
            {
                Parallel.For(0, jobs.Count, RentCoder, (i, _, coder) => { DecodeBlock(tile, jobs[i], coder); return coder; }, c => _coders.Add(c));
            }
            else
            {
                var coder = RentCoder();
                for (int i = 0; i < jobs.Count; i++) DecodeBlock(tile, jobs[i], coder);
                _coders.Add(coder);
            }

            void Wavelet(int c)
            {
                var tc = tile.Comps[c];
                if (!tc.Needed || !tc.HasData) return;
                for (int r = 1; r <= tc.OutRes; r++)
                {
                    J2kDwt.Synthesize(tc, r, _parallel);
                    tc.Res[r - 1].Buffer = null;
                }
            }
            if (_parallel && tile.Comps.Length > 1) Parallel.For(0, tile.Comps.Length, Wavelet);
            else for (int c = 0; c < tile.Comps.Length; c++) Wavelet(c);

            Finish(tile);
        }

        static J2kRect BlockRect(J2kResolution res, J2kPrecinctBand pb, int i)
        {
            long gx = pb.Cbx0 + i % pb.Cw, gy = pb.Cby0 + i / pb.Cw;
            return new J2kRect(gx << res.CbW, gy << res.CbH, (gx + 1) << res.CbW, (gy + 1) << res.CbH).Intersect(pb.Region);
        }

        void DecodeBlock(J2kTile tile, in Job job, J2kBlockCoder coder)
        {
            var tc = job.Tc; var band = job.Band; var res = job.Res;
            ref var blk = ref job.Pb.Blocks[job.Block];
            var rect = BlockRect(res, job.Pb, job.Block);
            int w = (int)rect.W, h = (int)rect.H;
            int style = tc.Cod.Style;
            bool ht = (style & J2kCod.Ht) != 0 && blk.Phld != 255;
            int numBps = band.Mb + tc.Roi - blk.Zbp;

            // gather the codeword segments, each followed by two 0xFF bytes
            Span<int> segStart = stackalloc int[192], segLen = stackalloc int[192], segPasses = stackalloc int[192];
            int nseg = 0, pos = 1, pass = 0, segEnd = 0;
            var chunks = tile.Chunks;
            long total = 0;
            for (int id = blk.FirstChunk; id != 0; id = chunks[id - 1].Next) total += chunks[id - 1].Len;
            if (total > int.MaxValue / 2) throw J2kCodestream.Bad("bad code-block length");
            coder.EnsureBuf((int)total + 2 * 192 + 16);
            fixed (byte* buf = coder.Buf)
            {
                for (int id = blk.FirstChunk; id != 0; id = chunks[id - 1].Next)
                {
                    ref var ch = ref chunks[id - 1];
                    if (pass >= segEnd)
                    {
                        if (nseg == 192) break;
                        if (nseg > 0) { buf[pos++] = 0xFF; buf[pos++] = 0xFF; }
                        segStart[nseg] = pos; segLen[nseg] = 0; segPasses[nseg] = 0;
                        segEnd = pass + (ht ? J2kPacketReader.HtSegmentPasses(blk.Phld, pass) : J2kBlockCoder.SegmentPasses(style & 63, pass));
                        nseg++;
                    }
                    Buffer.MemoryCopy(_cs.Data + ch.Pos, buf + pos, ch.Len, ch.Len);
                    pos += ch.Len;
                    segLen[nseg - 1] += ch.Len;
                    segPasses[nseg - 1] += ch.Passes;
                    pass += ch.Passes;
                }
                buf[pos++] = 0xFF; buf[pos++] = 0xFF;
            }

            bool wide = tc.Wide;
            if (wide && (coder.Data64 == null || coder.Data64.Length < w * h)) coder.Data64 = new ulong[Math.Max(w * h, 4096)];
            if (coder.Data32.Length < w * h) coder.Data32 = new uint[w * h];
            fixed (uint* d32 = coder.Data32)
            fixed (ulong* d64 = coder.Data64)
            {
                if (ht)
                {
                    // the last HT set that has a cleanup segment
                    int set = -1;
                    for (int s = 0; 2 * s < nseg; s++) if (segLen[2 * s] > 0) set = s;
                    bool ok = set < 0;   // nothing but placeholder passes: the block is zero
                    if (ok)
                    {
                        if (wide) new Span<ulong>(d64, w * h).Clear(); else new Span<uint>(d32, w * h).Clear();
                    }
                    else
                    {
                        int cup = 2 * set, refi = cup + 1;
                        int lref = refi < nseg ? segLen[refi] : 0;
                        int zblk = 1 + (refi < nseg && lref > 0 ? segPasses[refi] : 0);
                        int p = band.Mb + tc.Roi - 1 - (blk.Zbp + blk.Phld + set);
                        if (p >= 0)
                        {
                            coder.Ht ??= new J2kHtScratch();
                            ok = wide
                                ? J2kHtDecoder.Decode(coder, d64, w, h, segStart[cup], segLen[cup], refi < nseg ? segStart[refi] : 0, lref, zblk, p, (style & J2kCod.Causal) != 0)
                                : J2kHtDecoder.Decode(coder, d32, w, h, segStart[cup], segLen[cup], refi < nseg ? segStart[refi] : 0, lref, zblk, p, (style & J2kCod.Causal) != 0);
                        }
                    }
                    if (!ok)
                    {
                        if (_strict) throw J2kCodestream.Bad("damaged HT code-block");
                        Truncated = true;
                        if (wide) new Span<ulong>(d64, w * h).Clear(); else new Span<uint>(d32, w * h).Clear();
                    }
                }
                else if (wide) coder.Decode(d64, w, h, band.Orient, numBps, pass, style, segStart.Slice(0, nseg), segLen.Slice(0, nseg));
                else coder.Decode(d32, w, h, band.Orient, numBps, pass, style, segStart.Slice(0, nseg), segLen.Slice(0, nseg));

                // dequantize and place the part inside the window
                var win = rect.Intersect(band.Win);
                int cw = (int)win.W, chh = (int)win.H;
                int sx = (int)(win.X0 - rect.X0), sy = (int)(win.Y0 - rect.Y0);
                int col = band.ColOff + (int)(win.X0 - band.Win.X0);
                int roi = tc.Roi;
                int stride = res.Stride;
                for (int y = 0; y < chh; y++)
                {
                    long row = band.RowOff + (win.Y0 + y - band.Win.Y0) * band.RowStep;
                    long di = res.Off + row * stride + col;
                    int si = (sy + y) * w + sx;
                    switch (res.Buffer)
                    {
                        case int[] ib:
                            fixed (int* dst = &ib[di])
                            {
                                uint* src = d32 + si;
                                if (roi == 0)
                                {
                                    int x = 0;
                                    if (Vector128.IsHardwareAccelerated)
                                        for (; x + 4 <= cw; x += 4)
                                        {
                                            var v = Vector128.Load(src + x);
                                            var m = Vector128.ShiftRightLogical(v & Vector128.Create(0x7FFFFFFFu), 1).AsInt32();
                                            var sg = Vector128.ShiftRightArithmetic(v.AsInt32(), 31);
                                            ((m ^ sg) - sg).Store(dst + x);
                                        }
                                    for (; x < cw; x++)
                                    {
                                        uint v = src[x];
                                        int m = (int)((v & 0x7FFFFFFF) >> 1);
                                        int s = (int)v >> 31;
                                        dst[x] = (m ^ s) - s;
                                    }
                                }
                                else
                                    for (int x = 0; x < cw; x++)
                                    {
                                        uint v = src[x];
                                        uint m = v & 0x7FFFFFFF;
                                        if ((m >> 1) >> roi != 0) m >>= roi;
                                        int s = (int)v >> 31;
                                        dst[x] = ((int)(m >> 1) ^ s) - s;
                                    }
                            }
                            break;
                        case long[] lb:
                            fixed (long* dst = &lb[di])
                            {
                                ulong* src = d64 + si;
                                for (int x = 0; x < cw; x++)
                                {
                                    ulong v = src[x];
                                    ulong m = v & 0x7FFFFFFFFFFFFFFF;
                                    if (roi != 0 && (m >> 1) >> roi != 0) m >>= roi;
                                    long s = (long)v >> 63;
                                    dst[x] = ((long)(m >> 1) ^ s) - s;
                                }
                            }
                            break;
                        case float[] fb:
                            fixed (float* dst = &fb[di])
                            {
                                float step = band.Step * 0.5f;
                                if (wide)
                                {
                                    ulong* src = d64 + si;
                                    for (int x = 0; x < cw; x++)
                                    {
                                        ulong v = src[x];
                                        ulong m = v & 0x7FFFFFFFFFFFFFFF;
                                        if (roi != 0 && (m >> 1) >> roi != 0) m >>= roi;
                                        float f = m * step;
                                        dst[x] = (long)v < 0 ? -f : f;
                                    }
                                }
                                else
                                {
                                    uint* src = d32 + si;
                                    int x = 0;
                                    if (roi == 0 && Vector128.IsHardwareAccelerated)
                                        for (; x + 4 <= cw; x += 4)
                                        {
                                            // the scalar loop below: the magnitude times step, the sign bit carried over
                                            var v = Vector128.Load(src + x);
                                            var f = Vector128.ConvertToSingle((v & Vector128.Create(0x7FFFFFFFu)).AsInt32()) * Vector128.Create(step);
                                            (f.AsUInt32() ^ (v & Vector128.Create(0x80000000u))).AsSingle().Store(dst + x);
                                        }
                                    for (; x < cw; x++)
                                    {
                                        uint v = src[x];
                                        uint m = v & 0x7FFFFFFF;
                                        if (roi != 0 && (m >> 1) >> roi != 0) m >>= roi;
                                        float f = (int)m * step;
                                        dst[x] = (int)v < 0 ? -f : f;
                                    }
                                }
                            }
                            break;
                    }
                }
            }
        }

        // ---------------- colour transform, level shift, output ----------------

        void Finish(J2kTile tile)
        {
            int nc = tile.Comps.Length;
            bool mct = tile.Mct != 0 && nc >= 3 && tile.Comps[0].Needed;
            if (mct)
            {
                var a = tile.Comps[0]; var b = tile.Comps[1]; var c = tile.Comps[2];
                if (a.HasData || b.HasData || c.HasData)
                {
                    var v = a.Res[a.OutRes].V;
                    if (!v.Equals(b.Res[b.OutRes].V) || !v.Equals(c.Res[c.OutRes].V)) throw J2kCodestream.Bad("multi-component transform over components of different geometry");
                    foreach (var tc in new[] { a, b, c })
                    {
                        var res = tc.Res[tc.OutRes];
                        if (res.Buffer == null && (long)res.W * res.H > 0) RentTop(tc);
                        tc.HasData = true;
                    }
                    InverseMct(a.Res[a.OutRes], b.Res[b.OutRes], c.Res[c.OutRes], v);
                }
            }
            for (int ci = 0; ci < nc; ci++)
            {
                if (!_needed[ci]) continue;
                var tc = tile.Comps[ci];
                var res = tc.Res[tc.OutRes];
                var v = res.V;
                if (v.Empty) continue;
                var plane = _planes[ci];
                int w = (int)v.W, h = (int)v.H;
                int prec = tc.Prec;
                long shift = tc.Signed ? 0 : 1L << (prec - 1);
                long lo = tc.Signed ? -(1L << (prec - 1)) : 0, hi = tc.Signed ? (1L << (prec - 1)) - 1 : (1L << prec) - 1;
                int ox = (int)(v.X0 - res.C.X0), oy = (int)(v.Y0 - res.C.Y0);
                long px = v.X0 - _px0[ci], py = v.Y0 - _py0[ci];
                if (_sink != null)
                {
                    WriteToSink(res, ci, w, h, ox, oy, px, py, plane.Width, (int)shift, (int)hi);
                    continue;
                }
                for (int y = 0; y < h; y++)
                {
                    long di = (py + y) * plane.Width + px;
                    long si = (long)(oy + y) * res.W + ox;
                    if (plane.Data64 != null)
                    {
                        var dst = plane.Data64.AsSpan((int)di, w);
                        switch (res.Buffer)
                        {
                            case long[] lb: for (int x = 0; x < w; x++) dst[x] = Math.Clamp(lb[si + x] + shift, lo, hi); break;
                            case int[] ib: for (int x = 0; x < w; x++) dst[x] = Math.Clamp(ib[si + x] + shift, lo, hi); break;
                            case float[] fb: for (int x = 0; x < w; x++) dst[x] = Math.Clamp((long)MathF.Round(fb[si + x]) + shift, lo, hi); break;
                            default: dst.Fill(shift); break;
                        }
                        continue;
                    }
                    fixed (int* dst = &plane.Data![di])
                    {
                        switch (res.Buffer)
                        {
                            case int[] ib:
                                fixed (int* src = &ib[si]) ShiftClamp(src, dst, w, (int)shift, (int)lo, (int)hi);
                                break;
                            case long[] lb:
                                for (int x = 0; x < w; x++) dst[x] = (int)Math.Clamp(lb[si + x] + shift, lo, hi);
                                break;
                            case float[] fb:
                                fixed (float* src = &fb[si]) RoundShiftClamp(src, dst, w, (int)shift, (int)lo, (int)hi);
                                break;
                            default:
                                new Span<int>(dst, w).Fill((int)shift);
                                break;
                        }
                    }
                }
            }
            foreach (var tc in tile.Comps) ReturnTop(tc);
        }

        /// <summary>One component of a tile into its channel of the interleaved image: level shift, clamp, narrow.</summary>
        void WriteToSink(J2kResolution res, int comp, int w, int h, int ox, int oy, long px, long py, int imageWidth, int shift, int max)
        {
            int n = _sink!.Channels, ch = _sink.ChannelOf[comp];
            void Rows(int y0, int y1)
            {
                // (interleaved integer output is clamped a row at a time first, then spread out)
                var tmp = res.Buffer is float[] || res.Buffer is int[] && n != 1 ? new int[w] : null;
                fixed (int* t = tmp)
                    for (int y = y0; y < y1; y++)
                    {
                        long si = (long)(oy + y) * res.W + ox;
                        long di = ((py + y) * imageWidth + px) * n + ch;
                        int* src = null;
                        if (res.Buffer is float[] fb)
                        {
                            fixed (float* f = &fb[si]) RoundShiftClamp(f, t, w, shift, 0, max);
                            src = t;
                        }
                        if (_sink.Data is byte[] bytes)
                            fixed (byte* d = &bytes[di])
                            {
                                if (src != null) { if (n == 1) Narrow(src, d, w); else for (int x = 0; x < w; x++) d[(long)x * n] = (byte)src[x]; }
                                else if (res.Buffer is int[] ib)
                                    fixed (int* s = &ib[si])
                                    {
                                        if (n == 1) { ShiftClampNarrow(s, d, w, shift, max); }
                                        else
                                        {
                                            ShiftClamp(s, t, w, shift, 0, max);
                                            for (int x = 0; x < w; x++) d[(long)x * n] = (byte)t[x];
                                        }
                                    }
                                else for (int x = 0; x < w; x++) d[(long)x * n] = (byte)shift;
                            }
                        else
                            fixed (ushort* d = &((ushort[])_sink.Data!)[di])
                            {
                                if (src != null) for (int x = 0; x < w; x++) d[(long)x * n] = (ushort)src[x];
                                else if (res.Buffer is int[] ib)
                                    fixed (int* s = &ib[si])
                                    {
                                        int* c = t;
                                        if (c == null)
                                            for (int x = 0; x < w; x++)
                                            {
                                                int v = s[x] + shift;
                                                d[(long)x * n] = (ushort)((uint)v > (uint)max ? (v < 0 ? 0 : max) : v);
                                            }
                                        else
                                        {
                                            ShiftClamp(s, c, w, shift, 0, max);
                                            for (int x = 0; x < w; x++) d[(long)x * n] = (ushort)c[x];
                                        }
                                    }
                                else for (int x = 0; x < w; x++) d[(long)x * n] = (ushort)shift;
                            }
                    }
            }
            if (_parallel && (long)w * h > 1 << 18)
            {
                int parts = Math.Min(Environment.ProcessorCount, h / 64 + 1);
                Parallel.For(0, parts, i => Rows((int)((long)h * i / parts), (int)((long)h * (i + 1) / parts)));
            }
            else Rows(0, h);
        }

        static void Narrow(int* src, byte* dst, int n)
        {
            for (int i = 0; i < n; i++) dst[i] = (byte)src[i];
        }

        static void ShiftClampNarrow(int* src, byte* dst, int n, int shift, int max)
        {
            for (int i = 0; i < n; i++)
            {
                int v = src[i] + shift;
                dst[i] = (byte)((uint)v > (uint)max ? (v < 0 ? 0 : max) : v);
            }
        }

        static void ShiftClamp(int* src, int* dst, int n, int shift, int lo, int hi)
        {
            int i = 0;
            if (Vector.IsHardwareAccelerated)
            {
                var vs = new Vector<int>(shift); var vlo = new Vector<int>(lo); var vhi = new Vector<int>(hi);
                for (; i <= n - Vector<int>.Count; i += Vector<int>.Count)
                    Unsafe.WriteUnaligned(dst + i, Vector.Min(Vector.Max(Unsafe.ReadUnaligned<Vector<int>>(src + i) + vs, vlo), vhi));
            }
            for (; i < n; i++) dst[i] = Math.Clamp(src[i] + shift, lo, hi);
        }

        static void RoundShiftClamp(float* src, int* dst, int n, int shift, int lo, int hi)
        {
            // round half to even (as lrintf does in the reference decoder), then level shift and clamp
            int i = 0;
            float flo = lo, fhi = hi;
            if (System.Runtime.Intrinsics.X86.Sse2.IsSupported)
            {
                // values far outside the range are clamped as floats first so that the conversion cannot overflow
                var vmin = System.Runtime.Intrinsics.Vector128.Create(flo - shift - 1f);
                var vmax = System.Runtime.Intrinsics.Vector128.Create(fhi - shift + 1f);
                var vs = System.Runtime.Intrinsics.Vector128.Create(shift);
                var vlo = System.Runtime.Intrinsics.Vector128.Create(lo);
                var vhi = System.Runtime.Intrinsics.Vector128.Create(hi);
                for (; i <= n - 4; i += 4)
                {
                    var f = System.Runtime.Intrinsics.X86.Sse.LoadVector128(src + i);
                    f = System.Runtime.Intrinsics.X86.Sse.Min(System.Runtime.Intrinsics.X86.Sse.Max(f, vmin), vmax);
                    var v = System.Runtime.Intrinsics.X86.Sse2.Add(System.Runtime.Intrinsics.X86.Sse2.ConvertToVector128Int32(f), vs);
                    v = System.Runtime.Intrinsics.Vector128.Min(System.Runtime.Intrinsics.Vector128.Max(v, vlo), vhi);
                    System.Runtime.Intrinsics.X86.Sse2.Store(dst + i, v);
                }
            }
            else if (System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
            {
                var vmin = System.Runtime.Intrinsics.Vector128.Create(flo - shift - 1f);
                var vmax = System.Runtime.Intrinsics.Vector128.Create(fhi - shift + 1f);
                var vs = System.Runtime.Intrinsics.Vector128.Create(shift);
                var vlo = System.Runtime.Intrinsics.Vector128.Create(lo);
                var vhi = System.Runtime.Intrinsics.Vector128.Create(hi);
                for (; i <= n - 4; i += 4)
                {
                    var f = System.Runtime.Intrinsics.Arm.AdvSimd.LoadVector128(src + i);
                    f = System.Runtime.Intrinsics.Arm.AdvSimd.Min(System.Runtime.Intrinsics.Arm.AdvSimd.Max(f, vmin), vmax);
                    var v = System.Runtime.Intrinsics.Arm.AdvSimd.Add(System.Runtime.Intrinsics.Arm.AdvSimd.ConvertToInt32RoundToEven(f), vs);
                    v = System.Runtime.Intrinsics.Vector128.Min(System.Runtime.Intrinsics.Vector128.Max(v, vlo), vhi);
                    System.Runtime.Intrinsics.Arm.AdvSimd.Store(dst + i, v);
                }
            }
            for (; i < n; i++)
            {
                float f = MathF.Round(src[i]) + shift;
                dst[i] = f >= fhi ? hi : f > flo ? (int)f : lo;
            }
        }

        /// <summary>Inverse RCT (G.2) or ICT (G.3) over the valid window of the first three components, in place.</summary>
        static void InverseMct(J2kResolution r0, J2kResolution r1, J2kResolution r2, J2kRect v)
        {
            int w = (int)v.W, h = (int)v.H;
            for (int y = 0; y < h; y++)
            {
                long i0 = (v.Y0 - r0.C.Y0 + y) * r0.W + (v.X0 - r0.C.X0);
                long i1 = (v.Y0 - r1.C.Y0 + y) * r1.W + (v.X0 - r1.C.X0);
                long i2 = (v.Y0 - r2.C.Y0 + y) * r2.W + (v.X0 - r2.C.X0);
                if (r0.Buffer is int[] a0 && r1.Buffer is int[] a1 && r2.Buffer is int[] a2)
                {
                    fixed (int* p0 = &a0[i0], p1 = &a1[i1], p2 = &a2[i2])
                    {
                        int x = 0;
                        if (Vector.IsHardwareAccelerated)
                            for (; x <= w - Vector<int>.Count; x += Vector<int>.Count)
                            {
                                var yy = Unsafe.ReadUnaligned<Vector<int>>(p0 + x); var u = Unsafe.ReadUnaligned<Vector<int>>(p1 + x); var vv = Unsafe.ReadUnaligned<Vector<int>>(p2 + x);
                                var g = yy - Vector.ShiftRightArithmetic(u + vv, 2);
                                Unsafe.WriteUnaligned(p0 + x, vv + g); Unsafe.WriteUnaligned(p1 + x, g); Unsafe.WriteUnaligned(p2 + x, u + g);
                            }
                        for (; x < w; x++)
                        {
                            int yy = p0[x], u = p1[x], vv = p2[x];
                            int g = yy - ((u + vv) >> 2);
                            p0[x] = vv + g; p1[x] = g; p2[x] = u + g;
                        }
                    }
                }
                else if (r0.Buffer is long[] l0 && r1.Buffer is long[] l1 && r2.Buffer is long[] l2)
                {
                    for (int x = 0; x < w; x++)
                    {
                        long yy = l0[i0 + x], u = l1[i1 + x], vv = l2[i2 + x];
                        long g = yy - ((u + vv) >> 2);
                        l0[i0 + x] = vv + g; l1[i1 + x] = g; l2[i2 + x] = u + g;
                    }
                }
                else if (r0.Buffer is float[] f0 && r1.Buffer is float[] f1 && r2.Buffer is float[] f2)
                {
                    fixed (float* p0 = &f0[i0], p1 = &f1[i1], p2 = &f2[i2])
                    {
                        int x = 0;
                        if (Vector.IsHardwareAccelerated)
                        {
                            var c0 = new Vector<float>(1.402f); var c1 = new Vector<float>(0.34413f); var c2 = new Vector<float>(0.71414f); var c3 = new Vector<float>(1.772f);
                            for (; x <= w - Vector<float>.Count; x += Vector<float>.Count)
                            {
                                var yy = Unsafe.ReadUnaligned<Vector<float>>(p0 + x); var cb = Unsafe.ReadUnaligned<Vector<float>>(p1 + x); var cr = Unsafe.ReadUnaligned<Vector<float>>(p2 + x);
                                Unsafe.WriteUnaligned(p0 + x, yy + c0 * cr);
                                Unsafe.WriteUnaligned(p1 + x, yy - c1 * cb - c2 * cr);
                                Unsafe.WriteUnaligned(p2 + x, yy + c3 * cb);
                            }
                        }
                        for (; x < w; x++)
                        {
                            float yy = p0[x], cb = p1[x], cr = p2[x];
                            p0[x] = yy + 1.402f * cr;
                            p1[x] = yy - 0.34413f * cb - 0.71414f * cr;
                            p2[x] = yy + 1.772f * cb;
                        }
                    }
                }
                else throw J2kCodestream.Bad("multi-component transform over components of different kind");
            }
        }
    }
}
