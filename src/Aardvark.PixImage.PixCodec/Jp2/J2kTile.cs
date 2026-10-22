using System;
using System.Collections.Generic;

namespace Aardvark.ImageCodecs
{
    internal struct J2kRect
    {
        public long X0, Y0, X1, Y1;
        public J2kRect(long x0, long y0, long x1, long y1) { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; }
        public long W => X1 - X0;
        public long H => Y1 - Y0;
        public bool Empty => X1 <= X0 || Y1 <= Y0;
        public J2kRect Intersect(J2kRect o) => new J2kRect(Math.Max(X0, o.X0), Math.Max(Y0, o.Y0), Math.Min(X1, o.X1), Math.Min(Y1, o.Y1));
        public J2kRect Grow(long m) => new J2kRect(X0 - m, Y0 - m, X1 + m, Y1 + m);
    }

    /// <summary>State of a code-block while packets are read.</summary>
    internal struct J2kBlock
    {
        public int FirstChunk, LastChunk;    // list of codeword pieces in J2kTile.Chunks, 0 = none (index + 1)
        public int Passes;                   // coding passes signalled so far
        public byte Lblock, Zbp;
        public byte Phld;                    // HT: placeholder pass sets P0 (255: not an HT block, mixed mode)
        public bool Included;
        public bool HtStarted;               // HT: a non-empty cleanup segment was seen, P0 is final
    }

    /// <summary>The part of a codeword segment that one packet contributes.</summary>
    internal struct J2kChunk
    {
        public int Pos, Len, Next;
        public int Passes;
    }

    internal sealed class J2kTagTree
    {
        readonly int[] _low, _val, _off, _lw;
        readonly int _levels;

        public static long NodeCount(int w, int h)
        {
            long n = 0;
            while (true) { n += (long)w * h; if (w <= 1 && h <= 1) break; w = (w + 1) >> 1; h = (h + 1) >> 1; }
            return n;
        }

        public J2kTagTree(int w, int h)
        {
            int levels = 1;
            for (int a = w, b = h; a > 1 || b > 1; a = (a + 1) >> 1, b = (b + 1) >> 1) levels++;
            _levels = levels;
            _off = new int[levels]; _lw = new int[levels];
            int n = 0;
            for (int k = 0; k < levels; k++) { _off[k] = n; _lw[k] = w; n += w * h; w = (w + 1) >> 1; h = (h + 1) >> 1; }
            _low = new int[n]; _val = new int[n];
            Array.Fill(_val, int.MaxValue);
        }

        /// <summary>Reads as much of the leaf's value as needed to tell whether it is below the threshold.</summary>
        public bool Decode(ref J2kBitReader r, int x, int y, int threshold)
        {
            int low = 0, i = 0;
            for (int k = _levels - 1; k >= 0; k--)
            {
                i = _off[k] + (y >> k) * _lw[k] + (x >> k);
                if (low > _low[i]) _low[i] = low; else low = _low[i];
                while (low < threshold && low < _val[i])
                {
                    if (r.Bit() != 0) _val[i] = low; else low++;
                }
                _low[i] = low;
            }
            return _val[i] < threshold;
        }
    }

    /// <summary>Signals the end of the packet data to the tile parser.</summary>
    internal sealed class J2kEndOfData : Exception
    {
        public static readonly J2kEndOfData Instance = new J2kEndOfData();
    }

    /// <summary>Packet header bit reader: MSB first, the byte after 0xFF carries 7 bits.</summary>
    internal unsafe struct J2kBitReader
    {
        public byte* P, End;
        uint _cur;
        int _ct;

        public J2kBitReader(byte* p, byte* end) { P = p; End = end; _cur = 0; _ct = 0; }

        public int Bit()
        {
            if (_ct == 0)
            {
                if (P >= End) throw J2kEndOfData.Instance;
                uint prev = _cur;
                _cur = *P++;
                _ct = prev == 0xFF ? 7 : 8;
            }
            _ct--;
            return (int)(_cur >> _ct) & 1;
        }

        public int Bits(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++) v = v << 1 | Bit();
            return v;
        }

        /// <summary>Byte aligns at the end of a packet header (a trailing 0xFF is followed by a stuffed byte).</summary>
        public void Align()
        {
            if (_cur == 0xFF && P < End) P++;
            _ct = 0; _cur = 0;
        }
    }

    internal sealed class J2kPrecinctBand
    {
        public J2kRect Region;               // precinct area in band coordinates
        public int Cw, Ch;                   // code-block grid
        public long CwLong, ChLong;
        public J2kEncBlock[]? Enc;          // encoder side
        public J2kTagTreeEncoder? InclEnc, ZbpEnc;
        public long Cbx0, Cby0;              // grid index of the first code-block
        public J2kTagTree? Incl, Zbp;
        public J2kBlock[] Blocks = Array.Empty<J2kBlock>();
    }

    internal sealed class J2kPrecinct
    {
        public J2kPrecinctBand[] Bands = Array.Empty<J2kPrecinctBand>();
    }

    internal sealed class J2kBand
    {
        public int Orient;                   // 0 LL, 1 HL, 2 LH, 3 HH
        public J2kRect Rect;                 // in band coordinates
        public int Mb;                       // magnitude bit planes without ROI shift
        public float Step;                   // quantization step (irreversible)
        // decoding window (band coordinates) and where its samples go in the level buffer
        public J2kRect Win;
        public int ColOff, RowOff, RowStep;
    }

    internal sealed class J2kResolution
    {
        public J2kRect Rect;
        public int PPx, PPy, Pw, Ph;
        public int CbW, CbH;                 // effective code-block size exponents
        public J2kBand[] Bands = Array.Empty<J2kBand>();
        public J2kPrecinct?[]? Precincts;
        public int[]? LayersDone;
        // decoding window: C is what gets computed, V the part of it that is correct and wanted
        public J2kRect C, V;
        public int W, H, NLow;
        public Array? Buffer;
        public int Off, Stride;              // decoder: where the window starts in Buffer, and its row pitch
    }

    internal sealed class J2kTileComp
    {
        public int Index;
        public J2kRect Rect;
        public J2kCod Cod = null!;
        public J2kQcd Qcd = null!;
        public int Roi;
        public int Prec;
        public bool Signed;
        public J2kResolution[] Res = Array.Empty<J2kResolution>();
        public bool Needed;                  // decoded (not just parsed)
        public int OutRes;                   // highest resolution level to decode
        public bool Wide;                    // 64 bit samples
        public bool HasData;
        public Array? Pooled;                // decoder: the rented level buffer, returned once the tile is written out
    }

    internal sealed class J2kTile
    {
        public int Index;
        public J2kRect Rect;
        public int Order, Layers, Mct;
        public List<J2kPoc>? Poc;
        public J2kTileComp[] Comps = Array.Empty<J2kTileComp>();
        public J2kChunk[] Chunks = new J2kChunk[64];
        public int ChunkCount;
        public bool Truncated;

        public int AddChunk(int pos, int len, int passes)
        {
            if (ChunkCount == Chunks.Length) Array.Resize(ref Chunks, Chunks.Length * 2);
            Chunks[ChunkCount] = new J2kChunk { Pos = pos, Len = len, Passes = passes };
            return ++ChunkCount;
        }
    }

    internal static class J2kGeometry
    {
        public static long CeilDiv(long a, long b) => a >= 0 ? (a + b - 1) / b : -((-a) / b);
        public static long CeilShift(long a, int n) => n >= 62 ? (a > 0 ? 1 : 0) : (a + (1L << n) - 1) >> n;
        public static long FloorShift(long a, int n) => n >= 62 ? 0 : a >> n;

        /// <summary>Builds the tile-component / resolution / subband structure of a tile (B.5 - B.7).</summary>
        public static J2kTile BuildTile(J2kCodestream cs, int index)
        {
            var siz = cs.Siz;
            int p = index % siz.TilesX, q = index / siz.TilesX;
            var tile = new J2kTile
            {
                Index = index,
                Rect = new J2kRect(Math.Max(siz.TileX0 + p * siz.TileW, siz.X0), Math.Max(siz.TileY0 + q * siz.TileH, siz.Y0),
                    Math.Min(siz.TileX0 + (p + 1) * siz.TileW, siz.X1), Math.Min(siz.TileY0 + (q + 1) * siz.TileH, siz.Y1)),
            };
            var th = cs.Tiles[index];
            var tp = th?.Params;
            var global = tp != null && tp.HasCod ? tp : cs.Main;
            tile.Order = global.Order; tile.Layers = global.Layers; tile.Mct = global.Mct;
            tile.Poc = tp?.Poc ?? cs.Main.Poc;
            tile.Comps = new J2kTileComp[siz.NumComps];
            for (int c = 0; c < siz.NumComps; c++)
            {
                var cod = tp?.Coc[c] ?? (tp != null && tp.HasCod ? tp.Cod : null) ?? cs.Main.Coc[c] ?? cs.Main.Cod!;
                var qcd = tp?.Qcc[c] ?? tp?.Qcd ?? cs.Main.Qcc[c] ?? cs.Main.Qcd!;
                int roi = tp != null && tp.Roi[c] >= 0 ? tp.Roi[c] : Math.Max(cs.Main.Roi[c], 0);
                var tc = new J2kTileComp
                {
                    Index = c, Cod = cod, Qcd = qcd, Roi = roi, Prec = siz.Prec[c], Signed = siz.Signed[c],
                    Rect = new J2kRect(CeilDiv(tile.Rect.X0, siz.Dx[c]), CeilDiv(tile.Rect.Y0, siz.Dy[c]),
                        CeilDiv(tile.Rect.X1, siz.Dx[c]), CeilDiv(tile.Rect.Y1, siz.Dy[c])),
                };
                tile.Comps[c] = tc;
                int nl = cod.Levels;
                tc.Res = new J2kResolution[nl + 1];
                for (int r = 0; r <= nl; r++)
                {
                    int n = nl - r;
                    var res = new J2kResolution
                    {
                        Rect = new J2kRect(CeilShift(tc.Rect.X0, n), CeilShift(tc.Rect.Y0, n), CeilShift(tc.Rect.X1, n), CeilShift(tc.Rect.Y1, n)),
                        PPx = cod.PPx[r], PPy = cod.PPy[r],
                    };
                    tc.Res[r] = res;
                    if (!res.Rect.Empty)
                    {
                        long pw = CeilShift(res.Rect.X1, res.PPx) - FloorShift(res.Rect.X0, res.PPx);
                        long ph = CeilShift(res.Rect.Y1, res.PPy) - FloorShift(res.Rect.Y0, res.PPy);
                        if (pw * ph > int.MaxValue) throw J2kCodestream.Bad("too many precincts");
                        res.Pw = (int)pw; res.Ph = (int)ph;
                    }
                    res.CbW = Math.Min(cod.CbW, r == 0 ? res.PPx : res.PPx - 1);
                    res.CbH = Math.Min(cod.CbH, r == 0 ? res.PPy : res.PPy - 1);
                    res.Bands = new J2kBand[r == 0 ? 1 : 3];
                    for (int b = 0; b < res.Bands.Length; b++)
                    {
                        int orient = r == 0 ? 0 : b + 1;
                        var band = new J2kBand { Orient = orient };
                        if (r == 0) band.Rect = res.Rect;
                        else
                        {
                            int nb = nl - r + 1;
                            long xo = (long)(orient & 1) << (nb - 1), yo = (long)(orient >> 1) << (nb - 1);
                            band.Rect = new J2kRect(CeilShift(tc.Rect.X0 - xo, nb), CeilShift(tc.Rect.Y0 - yo, nb),
                                CeilShift(tc.Rect.X1 - xo, nb), CeilShift(tc.Rect.Y1 - yo, nb));
                        }
                        int qi = r == 0 ? 0 : 3 * (r - 1) + orient;
                        int exp, mant;
                        if (qcd.Style == 1) { exp = Math.Max(qcd.Exp[0] - (r == 0 ? 0 : r - 1), 0); mant = qcd.Mant[0]; }
                        else if (qi < qcd.Exp.Length) { exp = qcd.Exp[qi]; mant = qcd.Mant[qi]; }
                        else { exp = 0; mant = 0; }   // not signalled: as OpenJPEG, which decodes such files
                        band.Mb = qcd.Guard + exp - 1;
                        int gain = orient == 0 ? 0 : orient == 3 ? 2 : 1;
                        band.Step = (float)((1.0 + mant / 2048.0) * Math.Pow(2, tc.Prec + gain - exp));
                        if (band.Mb + roi > 30) tc.Wide = true;
                        if (band.Mb + roi > 62) throw new ImageNotSupportedException("JPEG 2000 with more than 62 magnitude bit planes");
                        res.Bands[b] = band;
                    }
                }
            }
            return tile;
        }

        /// <summary>Geometry of precinct k of a resolution level: its area and code-block grid in every subband (B.6, B.7).</summary>
        public static J2kPrecinct CreatePrecinct(J2kResolution res, int rIndex, int k)
        {
            long px = (res.Rect.X0 >> res.PPx) + k % res.Pw, py = (res.Rect.Y0 >> res.PPy) + k / res.Pw;
            var prec = new J2kPrecinct { Bands = new J2kPrecinctBand[res.Bands.Length] };
            for (int b = 0; b < res.Bands.Length; b++)
            {
                var band = res.Bands[b];
                int sh = rIndex == 0 ? 0 : 1;
                var region = new J2kRect((px << res.PPx) >> sh, (py << res.PPy) >> sh, ((px + 1) << res.PPx) >> sh, ((py + 1) << res.PPy) >> sh)
                    .Intersect(band.Rect);
                var pb = new J2kPrecinctBand { Region = region };
                prec.Bands[b] = pb;
                if (region.Empty) continue;
                pb.Cbx0 = region.X0 >> res.CbW; pb.Cby0 = region.Y0 >> res.CbH;
                pb.CwLong = CeilShift(region.X1, res.CbW) - pb.Cbx0;
                pb.ChLong = CeilShift(region.Y1, res.CbH) - pb.Cby0;
            }
            return prec;
        }

        /// <summary>
        /// Sets up, for every resolution level up to OutRes, the window that has to be computed so that
        /// <paramref name="want"/> (in OutRes coordinates) comes out right, and where each subband's samples go.
        /// A level buffer holds its window with the columns split into low | high halves and the rows interleaved.
        /// </summary>
        public static void SetupWindows(J2kTileComp tc, J2kRect want)
        {
            int margin = tc.Cod.Reversible ? 2 : 4;
            var v = want.Intersect(tc.Res[tc.OutRes].Rect);
            for (int r = tc.OutRes; r >= 0; r--)
            {
                var res = tc.Res[r];
                if (v.Empty) v = new J2kRect(res.Rect.X0, res.Rect.Y0, res.Rect.X0, res.Rect.Y0);
                res.V = v;
                var c = r == 0 ? v : v.Grow(margin).Intersect(res.Rect);
                if (v.Empty) c = v;
                res.C = c;
                res.W = (int)c.W; res.H = (int)c.H;
                long lx0 = (c.X0 + 1) >> 1, lx1 = (c.X1 + 1) >> 1, ly0 = (c.Y0 + 1) >> 1, ly1 = (c.Y1 + 1) >> 1;
                long hx0 = c.X0 >> 1, hx1 = c.X1 >> 1, hy0 = c.Y0 >> 1, hy1 = c.Y1 >> 1;
                if (r == 0)
                {
                    res.NLow = res.W;
                    var b = res.Bands[0];
                    b.Win = c; b.ColOff = 0; b.RowOff = 0; b.RowStep = 1;
                }
                else
                {
                    res.NLow = (int)(lx1 - lx0);
                    foreach (var b in res.Bands)
                    {
                        bool xh = (b.Orient & 1) != 0, yh = (b.Orient & 2) != 0;
                        b.Win = new J2kRect(xh ? hx0 : lx0, yh ? hy0 : ly0, xh ? hx1 : lx1, yh ? hy1 : ly1);
                        b.ColOff = xh ? res.NLow : 0;
                        b.RowStep = 2;
                        b.RowOff = (int)(2 * b.Win.Y0 + (yh ? 1 : 0) - c.Y0);
                    }
                    v = new J2kRect(lx0, ly0, lx1, ly1);
                }
            }
        }
    }
}
