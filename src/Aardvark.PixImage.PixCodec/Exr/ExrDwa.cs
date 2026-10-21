using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Aardvark.ImageCodecs.Compression;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// DWAA / DWAB decoding. Channels are classified by rules stored in each chunk: R,G,B style triples are
    /// converted to Y'CbCr and stored as quantized 8x8 DCT coefficients (half floats: DC values zlib packed, AC values
    /// run-length + Huffman or deflate), alpha style channels are byte-plane RLE + zlib, everything else plain zlib.
    /// The inverse DCT is done in float32, so results can differ from other decoders in the last half float bit.
    /// </summary>
    internal static unsafe class ExrDwa
    {
        const int SchemeUnknown = 0, SchemeDct = 1, SchemeRle = 2;

        static readonly byte[] ZigZag =
        {
            0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
        };

        static readonly float[] Cos = BuildCos();
        static ushort[]? _toLinear;

        static float[] BuildCos()
        {
            // orthonormal DCT basis: Cos[u * 8 + x] = c(u) / 2 * cos((2x + 1) u pi / 16)
            var t = new float[64];
            for (int u = 0; u < 8; u++)
                for (int x = 0; x < 8; x++)
                    t[u * 8 + x] = (float)((u == 0 ? Math.Sqrt(0.5) : 1.0) * 0.5 * Math.Cos((2 * x + 1) * u * Math.PI / 16));
            return t;
        }

        /// <summary>The DCT works on perceptually encoded values; this maps them (as half bits) back to linear.</summary>
        static ushort[] ToLinear => _toLinear ??= BuildToLinear();

        static ushort[] BuildToLinear()
        {
            var t = new ushort[65536];
            float logBase = MathF.Pow(2.7182818f, 2.2f);
            for (int i = 0; i < 65536; i++)
            {
                float x = BitConverter.UInt32BitsToSingle(ExrHalf.ToFloatBits[i]);
                if (!float.IsFinite(x)) continue;
                float a = MathF.Abs(x);
                float p = a <= 1 ? MathF.Pow(a, 2.2f) : MathF.Pow(logBase, a - 1);
                t[i] = ExrHalf.FromFloatIeee(x < 0 ? -p : p);
            }
            return t;
        }

        static ImageFormatException Corrupt(string what) => new("EXR DWA data is corrupt: " + what);

        /// <summary>How the channels of a chunk are coded, shared by decoder and encoder.</summary>
        sealed class Layout
        {
            public int Count;
            public int[] Scheme = Array.Empty<int>(), Csc = Array.Empty<int>(), W = Array.Empty<int>(), H = Array.Empty<int>(), Bytes = Array.Empty<int>();
            public long[] PlaneOffset = Array.Empty<long>();     // of each channel in the channel-planar copy of the chunk
            public List<int[]> DctSets = new();                  // decoding order: colour triples (R,G,B) first, then single channels
            public long DcCount, RleBytes, UnknownBytes;

            public static Layout Create(in ExrChunk c, int[] scheme, int[] csc)
            {
                int nc = c.Chans.Length;
                var l = new Layout { Count = nc, Scheme = scheme, Csc = csc, W = new int[nc], H = new int[nc], Bytes = new int[nc], PlaneOffset = new long[nc] };
                long off = 0;
                for (int i = 0; i < nc; i++)
                {
                    l.W[i] = c.Nx[i]; l.H[i] = c.Ny[i]; l.Bytes[i] = c.Chans[i].Bytes;
                    l.PlaneOffset[i] = off;
                    long size = (long)l.W[i] * l.H[i] * l.Bytes[i];
                    off += size;
                    if (scheme[i] == SchemeDct && c.Chans[i].Type == 0) throw Corrupt("DCT on an integer channel");
                    if (scheme[i] == SchemeDct) l.DcCount += Blocks(l.W[i], l.H[i]);
                    else if (scheme[i] == SchemeRle) l.RleBytes += size;
                    else if (scheme[i] == SchemeUnknown) l.UnknownBytes += size;
                    else throw Corrupt("channel scheme");
                }
                // colour triples: same layer prefix, csc index 0,1,2, same type and size
                var done = new bool[nc];
                for (int i = 0; i < nc; i++)
                {
                    if (scheme[i] != SchemeDct || csc[i] < 0 || done[i]) continue;
                    string prefix = Prefix(c.Chans[i].Name);
                    var set = new[] { -1, -1, -1 };
                    for (int j = 0; j < nc; j++)
                        if (scheme[j] == SchemeDct && csc[j] >= 0 && csc[j] < 3 && !done[j] && Prefix(c.Chans[j].Name) == prefix && set[csc[j]] < 0) set[csc[j]] = j;
                    if (set[0] < 0 || set[1] < 0 || set[2] < 0) continue;
                    bool same = true;
                    for (int k = 1; k < 3; k++)
                        same &= c.Chans[set[k]].Type == c.Chans[set[0]].Type && l.W[set[k]] == l.W[set[0]] && l.H[set[k]] == l.H[set[0]];
                    if (!same) continue;
                    done[set[0]] = done[set[1]] = done[set[2]] = true;
                    l.DctSets.Add(set);
                }
                for (int i = 0; i < nc; i++)
                    if (scheme[i] == SchemeDct && !done[i]) l.DctSets.Add(new[] { i });
                return l;
            }
        }

        static long Blocks(int w, int h) => (long)((w + 7) / 8) * ((h + 7) / 8);

        /// <summary>Copies between the line layout of a chunk and one contiguous plane per channel.</summary>
        static void CopyPlanes(byte* lines, byte* planes, in ExrChunk c, Layout l, bool toPlanes)
        {
            var cur = new long[l.Count];
            byte* p = lines;
            for (int y = c.Y0; y <= c.Y1; y++)
                for (int i = 0; i < l.Count; i++)
                {
                    if (!ExrChunk.IsSampled(y, c.Chans[i].YS)) continue;
                    long n = (long)l.W[i] * l.Bytes[i];
                    byte* q = planes + l.PlaneOffset[i] + cur[i];
                    if (toPlanes) Buffer.MemoryCopy(p, q, n, n); else Buffer.MemoryCopy(q, p, n, n);
                    cur[i] += n;
                    p += n;
                }
        }

        public static void Decode(ReadOnlySpan<byte> src, Span<byte> dst, in ExrChunk c, ExrScratch s)
        {
            int nc = c.Chans.Length;
            if (src.Length < 88) throw Corrupt("truncated header");
            Span<long> hdr = stackalloc long[11];
            for (int i = 0; i < 11; i++) hdr[i] = BinaryPrimitives.ReadInt64LittleEndian(src.Slice(i * 8));
            long version = hdr[0], unknownSize = hdr[1], unknownPacked = hdr[2], acPacked = hdr[3], dcPacked = hdr[4],
                 rlePacked = hdr[5], rleSize = hdr[6], rleRaw = hdr[7], acCount = hdr[8], dcCount = hdr[9], acCompression = hdr[10];
            if (version < 0 || version > 2) throw new ImageNotSupportedException($"EXR DWA version {version} is not supported");
            int p = 88;

            // classification rules: suffix, flags (csc index + 1, scheme, case insensitive), pixel type
            var scheme = new int[nc];
            var csc = new int[nc];
            Array.Fill(csc, -1);
            if (version >= 2)
            {
                if (src.Length - p < 2) throw Corrupt("truncated rules");
                int ruleSize = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(p));
                if (ruleSize < 2 || ruleSize > src.Length - p) throw Corrupt("rule size");
                var rules = src.Slice(p + 2, ruleSize - 2);
                p += ruleSize;
                var matched = new bool[nc];
                while (rules.Length > 0)
                {
                    int e = rules.IndexOf((byte)0);
                    if (e < 0 || rules.Length - e < 3) throw Corrupt("rules");
                    string suffix = System.Text.Encoding.UTF8.GetString(rules.Slice(0, e));
                    int flags = rules[e + 1], type = rules[e + 2];
                    rules = rules.Slice(e + 3);
                    for (int i = 0; i < nc; i++)
                    {
                        if (matched[i] || c.Chans[i].Type != type) continue;
                        if (!string.Equals(Suffix(c.Chans[i].Name), suffix, (flags & 1) != 0 ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
                        matched[i] = true;
                        scheme[i] = (flags >> 2) & 3;
                        csc[i] = (flags >> 4) - 1;
                    }
                }
            }
            else LegacyRules(c.Chans, scheme, csc);

            // section sizes must agree with the channel layout before anything is allocated from them
            var l = Layout.Create(c, scheme, csc);
            if (dcCount != l.DcCount || acCount < 0 || acCount > l.DcCount * 64 || (l.RleBytes != 0 && rleRaw != l.RleBytes)
                || (l.UnknownBytes != 0 && unknownSize != l.UnknownBytes)
                || unknownPacked < 0 || acPacked < 0 || dcPacked < 0 || rlePacked < 0 || rleSize < 0 || rleSize > 2 * l.RleBytes + 64
                || unknownPacked + acPacked + dcPacked + rlePacked > src.Length - p
                || (acCompression != 0 && acCompression != 1))
                throw Corrupt("section sizes");

            var unknown = src.Slice(p, (int)unknownPacked); p += (int)unknownPacked;
            var acData = src.Slice(p, (int)acPacked); p += (int)acPacked;
            var dcData = src.Slice(p, (int)dcPacked); p += (int)dcPacked;
            var rleData = src.Slice(p, (int)rlePacked);

            // the sections in buffers kept for the next chunk (slots 0..2 + 3.. for the planes)
            var ac = s.Reuse<ushort>(0, (int)acCount);
            if (acCount > 0)
            {
                if (acCompression == 0) ExrPiz.HufDecode(acData, ac.AsSpan(0, (int)acCount), s.Piz ??= new ExrPiz.Tables(), s);
                else ExrCodecs.Inflate(acData, MemoryMarshal.AsBytes(ac.AsSpan(0, (int)acCount)));
            }
            var dc = s.Reuse<ushort>(1, (int)l.DcCount);
            if (l.DcCount > 0)
            {
                var tmp = s.Reuse<byte>(2, (int)l.DcCount * 2).AsSpan(0, (int)l.DcCount * 2);
                ExrCodecs.Inflate(dcData, tmp);
                ExrCodecs.Reconstruct(tmp, MemoryMarshal.AsBytes(dc.AsSpan(0, (int)l.DcCount)));
            }
            var rle = new byte[l.RleBytes];
            if (l.RleBytes > 0)
            {
                var tmp = s.Reuse<byte>(2, (int)rleSize).AsSpan(0, (int)rleSize);
                ExrCodecs.Inflate(rleData, tmp);
                ExrCodecs.RleDecode(tmp, rle);
            }
            var unk = new byte[l.UnknownBytes];
            if (l.UnknownBytes > 0) ExrCodecs.Inflate(unknown, unk);

            // build the channel planes, then interleave them into lines
            var planes = s.Bytes(0, dst.Length);
            int rlePos = 0, unkPos = 0;
            fixed (byte* pp = planes)
            fixed (byte* pd = dst)
            fixed (uint* h2f = ExrHalf.ToFloatBits)
            {
                // DCT channels decode to half bits right in their planes (float ones into the upper half, widened below);
                // DC planes and the AC stream follow the decoding order
                ushort** halves = stackalloc ushort*[nc];
                for (int i = 0; i < nc; i++)
                    halves[i] = (ushort*)(pp + l.PlaneOffset[i] + (l.Bytes[i] == 4 ? 2L * l.W[i] * l.H[i] : 0));
                int acPos = 0, dcPos = 0;
                var dcStart = new int[nc];
                foreach (var set in l.DctSets)
                {
                    int blocks = (int)Blocks(l.W[set[0]], l.H[set[0]]);
                    foreach (int ch in set) { dcStart[ch] = dcPos; dcPos += blocks; }
                    DecodeDct(set.Length, set, halves, dc, dcStart, ac, (int)acCount, ref acPos, l.W[set[0]], l.H[set[0]], c.Chans);
                }

                for (int i = 0; i < nc; i++)
                {
                    int n = l.W[i] * l.H[i], bytes = l.Bytes[i];
                    byte* plane = pp + l.PlaneOffset[i];
                    if (scheme[i] == SchemeDct)
                    {
                        // in place, front to back: each float lands on halves that are already read
                        ushort* hp = halves[i];
                        if (bytes == 4) for (int k = 0; k < n; k++) { uint v = h2f[hp[k]]; ((uint*)plane)[k] = v; }
                    }
                    else if (scheme[i] == SchemeRle)
                    {
                        // one plane per byte of the sample type, each covering the whole chunk
                        for (int b = 0; b < bytes; b++)
                        {
                            for (int k = 0; k < n; k++) plane[k * bytes + b] = rle[rlePos + k];
                            rlePos += n;
                        }
                    }
                    else
                    {
                        unk.AsSpan(unkPos, n * bytes).CopyTo(new Span<byte>(plane, n * bytes));
                        unkPos += n * bytes;
                    }
                }
                CopyPlanes(pd, pp, c, l, false);
            }
        }

        // files from before the rules were stored in the chunk (versions 0 and 1): fixed, case insensitive names
        static void LegacyRules(ExrChan[] chans, int[] scheme, int[] csc)
        {
            for (int i = 0; i < chans.Length; i++)
            {
                string sfx = Suffix(chans[i].Name).ToLowerInvariant();
                bool fp = chans[i].Type != 0;
                if (fp && sfx is "r" or "g" or "b" && fp) { scheme[i] = SchemeDct; csc[i] = sfx == "r" ? 0 : sfx == "g" ? 1 : 2; }
                else if (fp && sfx is "y" or "by" or "ry" && fp) scheme[i] = SchemeDct;
                else if (sfx == "a") scheme[i] = SchemeRle;
            }
        }

        static string Suffix(string name) { int d = name.LastIndexOf('.'); return d < 0 ? name : name.Substring(d + 1); }
        static string Prefix(string name) { int d = name.LastIndexOf('.'); return d < 0 ? "" : name.Substring(0, d); }

        // The inverse DCT result is rounded to a half and sent through a steep lookup table, so it has to reproduce the
        // reference decoder's float32 results to the last bit: same constants (7 digit literals, not exact cosines)
        // and the same order of additions. The reference has several SIMD variants that differ in exactly these
        // details; this is the order of its AVX code path (the one x86-64 machines with AVX run), established by
        // comparing against the library on millions of samples: no other association of the sums matches.
        const float Ca = 3.535536e-01f, Cb = 4.903927e-01f, Cc = 4.619398e-01f, Cd = 4.157349e-01f,
                    Ce = 2.777855e-01f, Cf = 1.913422e-01f, Cg = 9.754573e-02f;

        /// <summary>8x8 inverse DCT in place (row major): rows first, then columns.</summary>
        static void InverseDct(float* data)
        {
            const float a = Ca, b = Cb, c = Cc, d = Cd, e = Ce, f = Cf, g = Cg;
            for (int i = 0; i < 8; i++)
            {
                float* p = data + i * 8;
                float p0 = p[0], p1 = p[1], p2 = p[2], p3 = p[3], p4 = p[4], p5 = p[5], p6 = p[6], p7 = p[7];
                // every output is (pair) + (pair) of products
                float e0 = (a * p0 + c * p2) + (a * p4 + f * p6), o0 = (b * p1 + d * p3) + (e * p5 + g * p7);
                float e1 = (a * p0 + f * p2) + (-a * p4 + -c * p6), o1 = (d * p1 + -g * p3) + (-b * p5 + -e * p7);
                float e2 = (a * p0 + -f * p2) + (-a * p4 + c * p6), o2 = (e * p1 + -b * p3) + (g * p5 + d * p7);
                float e3 = (a * p0 + -c * p2) + (a * p4 + -f * p6), o3 = (g * p1 + -e * p3) + (d * p5 + -b * p7);
                p[0] = e0 + o0; p[7] = e0 - o0;
                p[1] = e1 + o1; p[6] = e1 - o1;
                p[2] = e2 + o2; p[5] = e2 - o2;
                p[3] = e3 + o3; p[4] = e3 - o3;
            }
            for (int i = 0; i < 8; i++)
            {
                float* p = data + i;
                float p0 = p[0], p1 = p[8], p2 = p[16], p3 = p[24], p4 = p[32], p5 = p[40], p6 = p[48], p7 = p[56];
                float sum = a * p0 + a * p4, dif = a * p0 - a * p4;
                float t1 = c * p2 + f * p6, t2 = f * p2 - c * p6;
                float e0 = sum + t1, e1 = dif + t2, e2 = dif - t2, e3 = sum - t1;
                // the odd sums are associated differently for every output
                float o0 = (b * p1 + d * p3) + (e * p5 + g * p7);
                float o1 = (d * p1 + (-g * p3 + -b * p5)) + -e * p7;
                float o2 = ((e * p1 + -b * p3) + g * p5) + d * p7;
                float o3 = (g * p1 + d * p5) + (-e * p3 + -b * p7);
                p[0] = e0 + o0; p[56] = e0 - o0;
                p[8] = e1 + o1; p[48] = e1 - o1;
                p[16] = e2 + o2; p[40] = e2 - o2;
                p[24] = e3 + o3; p[32] = e3 - o3;
            }
        }

        static void DecodeDct(int ncomp, int[] chan, ushort** planes, ushort[] dc, int[] dcStart, ushort[] ac, int acCount, ref int acPos, int w, int h, ExrChan[] chans)
        {
            int nbx = (w + 7) / 8, nby = (h + 7) / 8;
            float* f = stackalloc float[3 * 64];
            float* tmp = stackalloc float[64];
            ushort* zig = stackalloc ushort[64];
            var toLinear = ToLinear;
            fixed (uint* h2f = ExrHalf.ToFloatBits)
            fixed (float* cos = Cos)
            fixed (byte* zz = ZigZag)
                for (int by = 0; by < nby; by++)
                    for (int bx = 0; bx < nbx; bx++)
                    {
                        for (int comp = 0; comp < ncomp; comp++)
                        {
                            float* blk = f + comp * 64;
                            new Span<ushort>(zig, 64).Clear();
                            zig[0] = dc[dcStart[chan[comp]] + by * nbx + bx];
                            int k = 1, last = 0;
                            while (k < 64)
                            {
                                if (acPos >= acCount) throw Corrupt("AC data is truncated");
                                ushort v = ac[acPos++];
                                if (v == 0xff00) break;                       // end of block
                                if ((v >> 8) == 0xff) k += v & 0xff;          // run of zeros
                                else { zig[k] = v; last = k; k++; }
                            }
                            if (last == 0)
                            {
                                float v = *(float*)(h2f + zig[0]) * 3.535536e-01f * 3.535536e-01f;
                                for (int i = 0; i < 64; i++) blk[i] = v;
                                continue;
                            }
                            for (int i = 0; i < 64; i++) blk[zz[i]] = *(float*)(h2f + zig[i]);
                            InverseDct(blk);
                        }
                        if (ncomp == 3)
                            for (int i = 0; i < 64; i++)
                            {
                                // Rec. 709 Y'CbCr -> R'G'B'
                                float y = f[i], cb = f[64 + i], cr = f[128 + i];
                                f[i] = y + 1.5747f * cr;
                                f[64 + i] = y - 0.1873f * cb - 0.4682f * cr;
                                f[128 + i] = y + 1.8556f * cb;
                            }
                        int x0 = bx * 8, y0 = by * 8, bw = Math.Min(8, w - x0), bh = Math.Min(8, h - y0);
                        for (int comp = 0; comp < ncomp; comp++)
                        {
                            ushort* plane = planes[chan[comp]];
                            float* blk = f + comp * 64;
                            // channels flagged pLinear (RY / BY of luminance/chroma files) are stored as they are
                            bool linear = chans[chan[comp]].PLinear;
                            for (int y = 0; y < bh; y++)
                                for (int x = 0; x < bw; x++)
                                {
                                    ushort hv = ExrHalf.FromFloatIeee(blk[y * 8 + x]);
                                    plane[(y0 + y) * w + x0 + x] = linear ? hv : toLinear[hv];
                                }
                        }
                    }
        }

        // ------------------------------------------------------------------ encode

        // JPEG example quantization tables (luminance, chrominance), divided by their smallest entry: the allowed
        // error of a DCT coefficient is (compression level / 100000) times its table entry
        static readonly float[] QuantY = Normalised(new[]
        {
            16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
            18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
        });
        static readonly float[] QuantC = Normalised(new[]
        {
            17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
        });

        static float[] Normalised(int[] t)
        {
            int min = int.MaxValue;
            foreach (int v in t) min = Math.Min(min, v);
            return Array.ConvertAll(t, v => (float)v / min);
        }

        static ushort[]? _toNonlinear;

        /// <summary>Inverse of <see cref="ToLinear"/>: linear half bits to the perceptual encoding the DCT works on.</summary>
        static ushort[] ToNonlinear => _toNonlinear ??= BuildToNonlinear();

        static ushort[] BuildToNonlinear()
        {
            var t = new ushort[65536];
            for (int i = 0; i < 65536; i++)
            {
                float x = BitConverter.UInt32BitsToSingle(ExrHalf.ToFloatBits[i]);
                if (!float.IsFinite(x)) continue;
                float a = MathF.Abs(x);
                float p = a <= 1 ? MathF.Pow(a, 1 / 2.2f) : MathF.Log(a) / 2.2f + 1;
                t[i] = ExrHalf.FromFloatIeee(x < 0 ? -p : p);
            }
            return t;
        }

        /// <summary>
        /// Lossy encoder. R,G,B triples of one layer are transformed to Y'CbCr and DCT coded, Y / RY / BY and lone
        /// R / G / B channels are DCT coded on their own (HALF and FLOAT; FLOAT is reduced to half precision), A is
        /// stored losslessly with byte plane RLE + zlib and every other channel losslessly with zlib.
        /// </summary>
        public static byte[] Encode(ReadOnlySpan<byte> raw, in ExrChunk c, ExrScratch s, float level)
        {
            int nc = c.Chans.Length;
            var scheme = new int[nc];
            var csc = new int[nc];
            var rules = new System.IO.MemoryStream();
            var seen = new HashSet<(string, int)>();
            for (int i = 0; i < nc; i++)
            {
                string sfx = Suffix(c.Chans[i].Name);
                int type = c.Chans[i].Type;
                csc[i] = -1;
                if (type != 0 && sfx is "R" or "G" or "B" && type != 0) { scheme[i] = SchemeDct; csc[i] = sfx == "R" ? 0 : sfx == "G" ? 1 : 2; }
                else if (type != 0 && sfx is "Y" or "BY" or "RY" && type != 0) scheme[i] = SchemeDct;
                else if (sfx == "A") scheme[i] = SchemeRle;
                else continue;
                if (!seen.Add((sfx, type))) continue;
                rules.Write(System.Text.Encoding.ASCII.GetBytes(sfx));
                rules.WriteByte(0);
                rules.WriteByte((byte)(((csc[i] + 1) << 4) | (scheme[i] << 2)));
                rules.WriteByte((byte)type);
            }
            var l = Layout.Create(c, scheme, csc);
            if (l.DcCount * 64 > int.MaxValue / 2) throw new ImageNotSupportedException("EXR chunk is too large");

            var planes = s.Bytes(0, raw.Length);
            var ac = new ushort[l.DcCount * 64];
            var dc = new ushort[l.DcCount];
            var rle = new byte[l.RleBytes];
            var unk = new byte[l.UnknownBytes];
            int acCount = 0;
            fixed (byte* pr = raw)
            fixed (byte* pp = planes)
            {
                CopyPlanes(pr, pp, c, l, true);
                int dcPos = 0;
                var dcStart = new int[nc];
                float baseError = Math.Max(level, 0) / 100000f;
                foreach (var set in l.DctSets)
                {
                    int blocks = (int)Blocks(l.W[set[0]], l.H[set[0]]);
                    foreach (int ch in set) { dcStart[ch] = dcPos; dcPos += blocks; }
                    EncodeDct(set, pp, l, dc, dcStart, ac, ref acCount, baseError, c.Chans);
                }
                int rlePos = 0, unkPos = 0;
                for (int i = 0; i < nc; i++)
                {
                    int n = l.W[i] * l.H[i], bytes = l.Bytes[i];
                    byte* plane = pp + l.PlaneOffset[i];
                    if (scheme[i] == SchemeRle)
                        for (int b = 0; b < bytes; b++)
                        {
                            for (int k = 0; k < n; k++) rle[rlePos + k] = plane[k * bytes + b];
                            rlePos += n;
                        }
                    else if (scheme[i] == SchemeUnknown)
                    {
                        new ReadOnlySpan<byte>(plane, n * bytes).CopyTo(unk.AsSpan(unkPos));
                        unkPos += n * bytes;
                    }
                }
            }

            byte[] unkPacked = unk.Length > 0 ? Zlib.Compress(unk, 6) : Array.Empty<byte>();
            byte[] acPacked = acCount > 0 ? ExrPiz.HufEncodeBlock(ac.AsSpan(0, acCount), s) : Array.Empty<byte>();
            byte[] dcPacked = Array.Empty<byte>();
            if (dc.Length > 0)
            {
                var tmp = new byte[dc.Length * 2];
                ExrCodecs.Predict(MemoryMarshal.AsBytes<ushort>(dc), tmp);
                dcPacked = Zlib.Compress(tmp, 6);
            }
            byte[] rlePacked = Array.Empty<byte>();
            int rleSize = 0;
            if (rle.Length > 0)
            {
                var tmp = new byte[rle.Length + rle.Length / 100 + 16];
                rleSize = ExrCodecs.RleEncode(rle, tmp);
                rlePacked = Zlib.Compress(tmp.AsSpan(0, rleSize), 6);
            }

            var ruleBytes = rules.ToArray();
            var o = new byte[88 + 2 + ruleBytes.Length + unkPacked.Length + acPacked.Length + dcPacked.Length + rlePacked.Length];
            long[] hdr = { 2, unk.Length, unkPacked.Length, acPacked.Length, dcPacked.Length, rlePacked.Length, rleSize, rle.Length, acCount, dc.Length, 0 };
            for (int i = 0; i < 11; i++) BinaryPrimitives.WriteInt64LittleEndian(o.AsSpan(i * 8), hdr[i]);
            int p = 88;
            BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(p), (ushort)(2 + ruleBytes.Length));
            p += 2;
            foreach (var part in new[] { ruleBytes, unkPacked, acPacked, dcPacked, rlePacked })
            {
                part.CopyTo(o, p);
                p += part.Length;
            }
            return o;
        }

        /// <summary>The half with the most trailing zero mantissa bits within tol of h (fewer distinct bit patterns compress better).</summary>
        static ushort Quantize(ushort h, float tol)
        {
            if ((h & 0x7c00) == 0x7c00) return 0;          // infinities and NaNs cannot be coded (they would read as run codes)
            float v = BitConverter.UInt32BitsToSingle(ExrHalf.ToFloatBits[h]);
            if (MathF.Abs(v) <= tol || (h & 0x7fff) == 0) return 0;
            if (tol <= 0) return h;
            int mag = h & 0x7fff;
            for (int k = 10; k >= 1; k--)
            {
                int r = (mag + (1 << (k - 1))) & ~((1 << k) - 1);
                if (r >= 0x7c00) continue;
                ushort cand = (ushort)((h & 0x8000) | r);
                if (MathF.Abs(BitConverter.UInt32BitsToSingle(ExrHalf.ToFloatBits[cand]) - v) <= tol) return cand;
            }
            return h;
        }

        static void EncodeDct(int[] set, byte* planes, Layout l, ushort[] dc, int[] dcStart, ushort[] ac, ref int acCount, float baseError, ExrChan[] chans)
        {
            int ncomp = set.Length, w = l.W[set[0]], h = l.H[set[0]];
            int nbx = (w + 7) / 8, nby = (h + 7) / 8;
            float* f = stackalloc float[3 * 64];
            float* tmp = stackalloc float[64];
            ushort* zig = stackalloc ushort[64];
            var nonlinear = ToNonlinear;
            fixed (uint* h2f = ExrHalf.ToFloatBits)
            fixed (float* cos = Cos)
            fixed (byte* zz = ZigZag)
                for (int by = 0; by < nby; by++)
                    for (int bx = 0; bx < nbx; bx++)
                    {
                        for (int comp = 0; comp < ncomp; comp++)
                        {
                            int ch = set[comp], bytes = l.Bytes[ch];
                            bool linear = chans[ch].PLinear;
                            byte* plane = planes + l.PlaneOffset[ch];
                            float* blk = f + comp * 64;
                            for (int y = 0; y < 8; y++)
                            {
                                long row = (long)Math.Min(by * 8 + y, h - 1) * w;        // edge blocks repeat the last row / column
                                for (int x = 0; x < 8; x++)
                                {
                                    long i = row + Math.Min(bx * 8 + x, w - 1);
                                    ushort hv = bytes == 2 ? ((ushort*)plane)[i] : ExrHalf.FromFloat(((float*)plane)[i]);
                                    blk[y * 8 + x] = *(float*)(h2f + (linear ? hv : nonlinear[hv]));
                                }
                            }
                        }
                        if (ncomp == 3)
                            for (int i = 0; i < 64; i++)
                            {
                                // R'G'B' -> Rec. 709 Y'CbCr
                                float r = f[i], g = f[64 + i], b = f[128 + i];
                                f[i] = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                                f[64 + i] = -0.1146f * r - 0.3854f * g + 0.5f * b;
                                f[128 + i] = 0.5f * r - 0.4542f * g - 0.0458f * b;
                            }
                        for (int comp = 0; comp < ncomp; comp++)
                        {
                            float* blk = f + comp * 64;
                            // forward DCT: rows, then columns
                            for (int y = 0; y < 8; y++)
                                for (int u = 0; u < 8; u++)
                                {
                                    float sum = 0;
                                    for (int x = 0; x < 8; x++) sum += blk[y * 8 + x] * cos[u * 8 + x];
                                    tmp[y * 8 + u] = sum;
                                }
                            for (int u = 0; u < 8; u++)
                                for (int v = 0; v < 8; v++)
                                {
                                    float sum = 0;
                                    for (int y = 0; y < 8; y++) sum += tmp[y * 8 + u] * cos[v * 8 + y];
                                    blk[v * 8 + u] = sum;
                                }
                            var quant = ncomp == 3 && comp > 0 ? QuantC : QuantY;
                            for (int i = 0; i < 64; i++)
                            {
                                float v = blk[zz[i]];
                                zig[i] = Quantize(float.IsFinite(v) ? ExrHalf.FromFloatIeee(v) : (ushort)0, baseError * quant[zz[i]]);
                            }
                            dc[dcStart[set[comp]] + by * nbx + bx] = zig[0];
                            // AC coefficients: values, 0xff00 | n for a run of n zeros, 0xff00 for "rest of the block is zero"
                            int k = 1;
                            while (k < 64)
                            {
                                if (zig[k] != 0) { ac[acCount++] = zig[k++]; continue; }
                                int run = 1;
                                while (k + run < 64 && zig[k + run] == 0) run++;
                                if (k + run == 64) { ac[acCount++] = 0xff00; break; }
                                ac[acCount++] = (ushort)(0xff00 | run);
                                k += run;
                            }
                        }
                    }
        }
    }
}
