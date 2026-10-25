using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Aardvark.ImageCodecs.JpegImpl
{
    /// <summary>
    /// Decoder back end: brings every component plane to full resolution row by row and converts to the output
    /// color model. Upsampling of 2:1 ratios uses the same triangle filters (and rounding) as libjpeg's "fancy"
    /// upsampling, everything else is sample replication.
    /// </summary>
    internal static unsafe class JpegPost
    {
        /// <summary>Test hook: disables the vectorized paths.</summary>
        internal static bool ForceScalar;

        sealed class Upsampler
        {
            public JpegComponent C = null!;
            public byte[] LineArr = null!;
            public ushort[] SumArr = null!;
            public byte* Line;
            public ushort* Sum;
            public int CachedRow = -1;
        }

        public static void Assemble(JpegDecoder d, byte[] output) => Assemble(d, output, d.OutWidth, d.OutHeight, d.ColorSpace);

        /// <summary>Writes the top left w x h pixels (at most the output size of the frame) in the given color handling.</summary>
        public static void Assemble(JpegDecoder d, Span<byte> output, int w, int h, JpegColorSpace cs)
        {
            fixed (byte* outp = output) new Assembler(d, outp, w, h, cs).Advance(int.MaxValue);
        }

        /// <summary>
        /// Converts rows to the output as the component rows they depend on become available: all at once after a full
        /// decode, or band by band while a streaming decode (<see cref="JpegDecoder.OnStream"/>) fills its ring planes.
        /// The output must stay pinned while rows are emitted.
        /// </summary>
        public sealed class Assembler
        {
            readonly JpegDecoder _d;
            readonly byte* _out;
            readonly int _w, _h, _outChannels;
            readonly JpegColorSpace _cs;
            readonly Upsampler[] _ups;
            readonly byte[]? _tmpArr, _rowArr;
            readonly byte* _tmp, _row;
            readonly int _orientation;
            /// <summary>Gray / RGB rows are expanded to RGBA with opaque alpha (from <see cref="_conv"/>, holding _convChannels).</summary>
            readonly bool _rgba;
            readonly int _convChannels;
            readonly byte[]? _convArr;
            readonly byte* _conv;
            int _y;

            /// <summary>
            /// Band mode: the output holds <see cref="BandRows"/> rows, and <see cref="OnBand"/> (first row, row count) is
            /// called whenever they are full and after the last row. Only without orientation.
            /// </summary>
            public int BandRows;
            public Action<int, int>? OnBand;
            int _bandStart;

            /// <summary>
            /// Writes the top left <paramref name="w"/> x <paramref name="h"/> pixels of <paramref name="d"/> to
            /// <paramref name="output"/> in color handling <paramref name="cs"/>. With an EXIF <paramref name="orientation"/>
            /// 2..8 rows go where they belong in the upright image (h x w for 5..8), as <see cref="ImageOps.ApplyOrientation"/> turns them.
            /// </summary>
            public Assembler(JpegDecoder d, byte* output, int w, int h, JpegColorSpace cs, int orientation = 1, bool rgba = false)
            {
                _d = d; _out = output; _w = w; _h = h; _cs = cs; _orientation = orientation;
                int nc = d.Comps.Length;
                _ups = new Upsampler[nc];
                for (int i = 0; i < nc; i++)
                {
                    var c = d.Comps[i];
                    // generous slack: vector loops store whole vectors
                    var u = _ups[i] = new Upsampler { C = c, LineArr = Rent(ref t_lines, i, Math.Max(d.OutWidth, c.SampledW * c.HExp) + 128), SumArr = Rent(ref t_sums, i, c.SampledW + 2 + 64) };
                    fixed (byte* l = u.LineArr) u.Line = l;
                    fixed (ushort* s = u.SumArr) u.Sum = s + 1;
                }
                if (cs is JpegColorSpace.Ycck or JpegColorSpace.YcckToCmyk)
                {
                    _tmpArr = GC.AllocateArray<byte>(w * 3 + 64, pinned: true);
                    fixed (byte* t = _tmpArr) _tmp = t;
                }
                _outChannels = cs == JpegColorSpace.Gray ? 1 : cs is JpegColorSpace.Raw or JpegColorSpace.Rgb ? nc : cs == JpegColorSpace.YcckToCmyk ? 4 : 3;
                if (rgba && _outChannels is 1 or 3)
                {
                    _rgba = true;
                    _convChannels = _outChannels;
                    _outChannels = 4;
                    _convArr = GC.AllocateUninitializedArray<byte>(w * 3 + 64, pinned: true);
                    fixed (byte* t = _convArr) _conv = t;
                }
                if (orientation > 1)
                {
                    _rowArr = GC.AllocateUninitializedArray<byte>(w * _outChannels + 64, pinned: true);
                    fixed (byte* r = _rowArr) _row = r;
                }
            }

            /// <summary>Copies a finished row of the stored image to its place in the upright one.</summary>
            void Place(int y)
            {
                int w = _w, h = _h, ch = _outChannels;
                long dw = _orientation >= 5 ? h : w;
                (long d0, long step) = _orientation switch
                {
                    2 => (y * dw + w - 1, -1L),
                    3 => ((h - 1L - y) * dw + w - 1, -1L),
                    4 => ((h - 1L - y) * dw, 1L),
                    5 => ((long)y, dw),
                    6 => (h - 1L - y, dw),
                    7 => ((w - 1L) * dw + h - 1 - y, -dw),
                    _ => ((w - 1L) * dw + y, -dw),
                };
                byte* src = _row, dst = _out + d0 * ch;
                long st = step * ch;
                switch (ch)
                {
                    case 1: for (int x = 0; x < w; x++, dst += st) dst[0] = src[x]; break;
                    case 3:
                        for (int x = 0; x < w; x++, dst += st, src += 3) { dst[0] = src[0]; dst[1] = src[1]; dst[2] = src[2]; }
                        break;
                    case 4: for (int x = 0; x < w; x++, dst += st) *(uint*)dst = ((uint*)src)[x]; break;
                    default:
                        for (int x = 0; x < w; x++, dst += st, src += ch) new Span<byte>(src, ch).CopyTo(new Span<byte>(dst, ch));
                        break;
                }
            }

            /// <summary>Emits every row that the first <paramref name="units"/> unit rows of each component complete (int.MaxValue: all).</summary>
            public void Advance(int units)
            {
                while (_y < _h)
                {
                    foreach (var u in _ups)
                    {
                        var c = u.C;
                        if (units != int.MaxValue && LastRow(c, _y) >= Math.Min((long)units * c.UnitRows, c.SampledH)) return;
                    }
                    Emit(_y++);
                    if (BandRows > 0 && (_y - _bandStart == BandRows || _y == _h))
                    {
                        OnBand!(_bandStart, _y - _bandStart);
                        _bandStart = _y;
                    }
                }
                if (units == int.MaxValue) Release();
            }

            // The line buffers serve the next assembler on the same thread (TIFF strips and tiles are many small JPEG
            // streams); only small ones are kept. Zeroed when new: vector loops read their slack.
            [ThreadStatic] static byte[]?[]? t_lines;
            [ThreadStatic] static ushort[]?[]? t_sums;
            const int MaxCachedLine = 1 << 16;

            static T[] Rent<T>(ref T[]?[]? cache, int i, int size) where T : unmanaged
            {
                if (cache?[i] is { } a && a.Length >= size) { cache[i] = null; return a; }
                return GC.AllocateArray<T>(size, pinned: true);
            }

            static void Return<T>(ref T[]?[]? cache, int i, T[] a)
            {
                if (a.Length > MaxCachedLine) return;
                cache ??= new T[4][];
                if (cache[i] == null || cache[i]!.Length < a.Length) cache[i] = a;
            }

            bool _released;

            void Release()
            {
                if (_released) return;
                _released = true;
                for (int i = 0; i < _ups.Length; i++)
                {
                    Return(ref t_lines, i, _ups[i].LineArr);
                    Return(ref t_sums, i, _ups[i].SumArr);
                }
            }

            void Emit(int y)
            {
                var ups = _ups;
                int nc = ups.Length, w = _w;
                byte** lines = stackalloc byte*[4];
                for (int i = 0; i < nc; i++) lines[i] = GetLine(ups[i], y, w, ups[i].Line, ups[i].Sum);
                byte* final = _orientation > 1 ? _row : _out + (nint)(BandRows > 0 ? y - _bandStart : y) * w * _outChannels;
                byte* dst = _rgba ? _conv : final;
                byte* tmp = _tmp;
                {
                    switch (_cs)
                    {
                        case JpegColorSpace.Gray:
                            Buffer.MemoryCopy(lines[0], dst, w, w);
                            break;
                        case JpegColorSpace.YCbCr:
                            YccToRgb(lines[0], lines[1], lines[2], dst, w);
                            break;
                        case JpegColorSpace.Rgb:
                        case JpegColorSpace.Raw:
                            for (int i = 0; i < nc; i++)
                            {
                                byte* src = lines[i];
                                for (int x = 0; x < w; x++) dst[x * nc + i] = src[x];
                            }
                            break;
                        case JpegColorSpace.Cmyk:
                            CmykToRgb(lines[0], lines[1], lines[2], lines[3], 1, dst, w);
                            break;
                        case JpegColorSpace.Ycck:
                            // YCC decodes to RGB = 255 - (stored CMY), so its complement is the stored value
                            YccToRgb(lines[0], lines[1], lines[2], tmp, w);
                            for (int x = 0; x < w * 3; x++) tmp[x] = (byte)(255 - tmp[x]);
                            CmykToRgb(tmp, tmp + 1, tmp + 2, lines[3], 3, dst, w);
                            break;
                        case JpegColorSpace.YcckToCmyk:
                            YccToRgb(lines[0], lines[1], lines[2], tmp, w);
                            for (int x = 0; x < w; x++, dst += 4)
                            {
                                dst[0] = (byte)(255 - tmp[3 * x]); dst[1] = (byte)(255 - tmp[3 * x + 1]); dst[2] = (byte)(255 - tmp[3 * x + 2]);
                                dst[3] = lines[3][x];
                            }
                            break;
                    }
                }
                if (_rgba) ExpandToRgba(_conv, _convChannels, final, w);
                if (_orientation > 1) Place(y);
            }
        }

        static void ExpandToRgba(byte* src, int channels, byte* dst, int w)
        {
            int x = 0;
            var opaque = Vector128.Create(0xFF000000u).AsByte();
            if (channels == 1)
            {
                if (Vector128.IsHardwareAccelerated)
                    for (; x + 16 <= w; x += 16)
                    {
                        var g = Vector128.Load(src + x);
                        byte* d = dst + 4 * x;
                        (Vector128.Shuffle(g, Vector128.Create((byte)0, 0, 0, 0x80, 1, 1, 1, 0x80, 2, 2, 2, 0x80, 3, 3, 3, 0x80)) | opaque).Store(d);
                        (Vector128.Shuffle(g, Vector128.Create((byte)4, 4, 4, 0x80, 5, 5, 5, 0x80, 6, 6, 6, 0x80, 7, 7, 7, 0x80)) | opaque).Store(d + 16);
                        (Vector128.Shuffle(g, Vector128.Create((byte)8, 8, 8, 0x80, 9, 9, 9, 0x80, 10, 10, 10, 0x80, 11, 11, 11, 0x80)) | opaque).Store(d + 32);
                        (Vector128.Shuffle(g, Vector128.Create((byte)12, 12, 12, 0x80, 13, 13, 13, 0x80, 14, 14, 14, 0x80, 15, 15, 15, 0x80)) | opaque).Store(d + 48);
                    }
                for (; x < w; x++) ((uint*)dst)[x] = src[x] * 0x010101u | 0xFF000000u;
            }
            else
            {
                // 16 bytes read for the 12 of four pixels: the row buffer has 64 bytes to spare
                if (Vector128.IsHardwareAccelerated)
                    for (; x + 4 <= w; x += 4)
                        (Vector128.Shuffle(Vector128.Load(src + 3 * x), Vector128.Create((byte)0, 1, 2, 0x80, 3, 4, 5, 0x80, 6, 7, 8, 0x80, 9, 10, 11, 0x80)) | opaque).Store(dst + 4 * x);
                for (src += 3 * x; x < w; x++, src += 3) ((uint*)dst)[x] = src[0] | (uint)src[1] << 8 | (uint)src[2] << 16 | 0xFF000000u;
            }
        }

        /// <summary>The last component sample row that output row <paramref name="y"/> reads.</summary>
        static int LastRow(JpegComponent c, int y)
        {
            int last = c.SampledH - 1;
            switch (c.Upsample)
            {
                case JpegUpsample.Direct:
                case JpegUpsample.H2V1:
                    return Math.Min(y, last);
                case JpegUpsample.Replicate:
                    return Math.Min(y / c.VExp, last);
                case JpegUpsample.Fractional:
                {
                    int py = Math.Clamp(((2 * y + 1) * c.VNum * 256 / c.VDen - 256) / 2, 0, last * 256);
                    return Math.Min((py >> 8) + 1, last);
                }
                default: // H2V2, H1V2
                    return Math.Min((y >> 1) + 1, last);
            }
        }

        /// <summary>Sample row <paramref name="sy"/> of a component plane, full or ring.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte* Row(JpegComponent c, int sy) => c.Plane + (nint)(c.RingRows == 0 ? sy : sy % c.RingRows) * c.Stride;

        /// <summary>
        /// 4 component files carry inverted ink values (Adobe convention: 255 = no ink), so the plain
        /// multiplicative model RGB = (1 - C) * (1 - K) becomes stored_c * stored_k / 255, rounded to nearest.
        /// </summary>
        static void CmykToRgb(byte* c, byte* m, byte* y, byte* k, int step, byte* dst, int w)
        {
            for (int x = 0; x < w; x++, dst += 3)
            {
                int kk = k[x];
                dst[0] = (byte)((c[x * step] * kk + 127) / 255);
                dst[1] = (byte)((m[x * step] * kk + 127) / 255);
                dst[2] = (byte)((y[x * step] * kk + 127) / 255);
            }
        }

        // ---------------------------------------------------------------- upsampling

        static byte* GetLine(Upsampler u, int y, int w, byte* line, ushort* sum)
        {
            var c = u.C;
            int n = c.SampledW;
            switch (c.Upsample)
            {
                case JpegUpsample.Direct:
                    return Row(c, y);

                case JpegUpsample.Replicate:
                {
                    int sy = y / c.VExp;
                    byte* src = Row(c, sy);
                    if (c.HExp == 1) return src;
                    if (sy != u.CachedRow)
                    {
                        u.CachedRow = sy;
                        int e = c.HExp;
                        byte* o = line;
                        if (e == 2)
                            for (int x = 0; x < n; x++) { o[2 * x] = src[x]; o[2 * x + 1] = src[x]; }
                        else
                            for (int x = 0; x < n; x++, o += e) new Span<byte>(o, e).Fill(src[x]);
                    }
                    return line;
                }

                case JpegUpsample.Fractional:
                {
                    // Ratios that are not whole numbers (libjpeg rejects such files): bilinear interpolation with
                    // sample centers aligned, positions in 1/256 sample units.
                    int py = Math.Clamp(((2 * y + 1) * c.VNum * 256 / c.VDen - 256) / 2, 0, (c.SampledH - 1) * 256);
                    int y0 = py >> 8, fy = py & 255;
                    byte* r0 = Row(c, y0), r1 = Row(c, Math.Min(y0 + 1, c.SampledH - 1));
                    for (int x = 0; x < w; x++)
                    {
                        int px = Math.Clamp(((2 * x + 1) * c.HNum * 256 / c.HDen - 256) / 2, 0, (n - 1) * 256);
                        int x0 = px >> 8, fx = px & 255, x1 = Math.Min(x0 + 1, n - 1);
                        int top = r0[x0] * (256 - fx) + r0[x1] * fx, bottom = r1[x0] * (256 - fx) + r1[x1] * fx;
                        line[x] = (byte)((top * (256 - fy) + bottom * fy + 32768) >> 16);
                    }
                    return line;
                }

                case JpegUpsample.H2V1:
                    Widen(Row(c, y), sum, n);
                    Horizontal(sum, n, line, 1, 2, 2);
                    return line;

                case JpegUpsample.H2V2:
                {
                    int sy = y >> 1;
                    // the nearer of the two neighbouring rows gets weight 1/4, clamped at the image edge
                    int near = (y & 1) == 0 ? Math.Max(sy - 1, 0) : Math.Min(sy + 1, c.SampledH - 1);
                    Sum3(Row(c, sy), Row(c, near), sum, n);
                    Horizontal(sum, n, line, 8, 7, 4);
                    return line;
                }

                default: // H1V2
                {
                    int sy = y >> 1;
                    int near = (y & 1) == 0 ? Math.Max(sy - 1, 0) : Math.Min(sy + 1, c.SampledH - 1);
                    Blend(Row(c, sy), Row(c, near), line, n, (y & 1) == 0 ? 1 : 2);
                    return line;
                }
            }
        }

        static bool UseVec => Vector.IsHardwareAccelerated && !ForceScalar;

        static void Widen(byte* src, ushort* sum, int n)
        {
            int i = 0;
            if (UseVec)
            {
                int vc = Vector<byte>.Count;
                for (; i + vc <= n; i += vc)
                {
                    Vector.Widen(Vector.Load(src + i), out Vector<ushort> lo, out Vector<ushort> hi);
                    Vector.Store(lo, sum + i);
                    Vector.Store(hi, sum + i + vc / 2);
                }
            }
            for (; i < n; i++) sum[i] = src[i];
            sum[-1] = sum[0];
            sum[n] = sum[n - 1];
        }

        /// <summary>sum = 3 * cur + near (vertical half of the 2x2 triangle filter).</summary>
        static void Sum3(byte* cur, byte* near, ushort* sum, int n)
        {
            int i = 0;
            if (UseVec)
            {
                int vc = Vector<byte>.Count;
                for (; i + vc <= n; i += vc)
                {
                    Vector.Widen(Vector.Load(cur + i), out Vector<ushort> clo, out Vector<ushort> chi);
                    Vector.Widen(Vector.Load(near + i), out Vector<ushort> nlo, out Vector<ushort> nhi);
                    Vector.Store(clo * (ushort)3 + nlo, sum + i);
                    Vector.Store(chi * (ushort)3 + nhi, sum + i + vc / 2);
                }
            }
            for (; i < n; i++) sum[i] = (ushort)(3 * cur[i] + near[i]);
            sum[-1] = sum[0];
            sum[n] = sum[n - 1];
        }

        /// <summary>
        /// Horizontal 2x triangle filter: out[2i] = (3 s[i] + s[i-1] + biasEven) >> shift, out[2i+1] = (3 s[i] + s[i+1] + biasOdd) >> shift.
        /// s[-1] and s[n] must hold the replicated edge samples.
        /// </summary>
        static void Horizontal(ushort* s, int n, byte* o, int biasEven, int biasOdd, int shift)
        {
            int i = 0;
            if (UseVec)
            {
                int vc = Vector<ushort>.Count;
                var be = new Vector<ushort>((ushort)biasEven);
                var bo = new Vector<ushort>((ushort)biasOdd);
                for (; i + vc <= n; i += vc)
                {
                    var c3 = Vector.Load(s + i) * (ushort)3;
                    var even = Vector.ShiftRightLogical(c3 + Vector.Load(s + i - 1) + be, shift);
                    var odd = Vector.ShiftRightLogical(c3 + Vector.Load(s + i + 1) + bo, shift);
                    // little endian: low byte of each 16 bit lane is the even output sample
                    Vector.Store(even | Vector.ShiftLeft(odd, 8), (ushort*)(o + 2 * i));
                }
            }
            for (; i < n; i++)
            {
                int c3 = 3 * s[i];
                o[2 * i] = (byte)((c3 + s[i - 1] + biasEven) >> shift);
                o[2 * i + 1] = (byte)((c3 + s[i + 1] + biasOdd) >> shift);
            }
        }

        /// <summary>out = (3 * cur + near + bias) >> 2 (vertical-only triangle filter).</summary>
        static void Blend(byte* cur, byte* near, byte* o, int n, int bias)
        {
            int i = 0;
            if (UseVec)
            {
                int vc = Vector<byte>.Count;
                var b = new Vector<ushort>((ushort)bias);
                for (; i + vc <= n; i += vc)
                {
                    Vector.Widen(Vector.Load(cur + i), out Vector<ushort> clo, out Vector<ushort> chi);
                    Vector.Widen(Vector.Load(near + i), out Vector<ushort> nlo, out Vector<ushort> nhi);
                    clo = Vector.ShiftRightLogical(clo * (ushort)3 + nlo + b, 2);
                    chi = Vector.ShiftRightLogical(chi * (ushort)3 + nhi + b, 2);
                    Vector.Store(Vector.Narrow(clo, chi), o + i);
                }
            }
            for (; i < n; i++) o[i] = (byte)((3 * cur[i] + near[i] + bias) >> 2);
        }

        // ---------------------------------------------------------------- color

        // ITU-R BT.601 full range, 16 bit fixed point (the JFIF conversion):
        //   R = Y + 1.402 Cr, G = Y - 0.344136 Cb - 0.714136 Cr, B = Y + 1.772 Cb
        const int CrR = 91881, CbG = -22554, CrG = -46802, CbB = 116130;

        public static void YccToRgb(byte* yp, byte* cbp, byte* crp, byte* dst, int w)
        {
            int x = 0;
            if (Avx2.IsSupported && !ForceScalar)
            {
                // 16 bit lanes via pmaddwd on (cb, cr) pairs; coefficients are reduced mod 65536 and the
                // dropped whole multiples of cb / cr are added back afterwards.
                var kR = Vector256.Create(0, CrR - 65536, 0, CrR - 65536, 0, CrR - 65536, 0, CrR - 65536, 0, CrR - 65536, 0, CrR - 65536, 0, CrR - 65536, 0, CrR - 65536).AsInt16();
                var kG = Vector256.Create((short)CbG, (short)(CrG + 65536), (short)CbG, (short)(CrG + 65536), (short)CbG, (short)(CrG + 65536), (short)CbG, (short)(CrG + 65536),
                    (short)CbG, (short)(CrG + 65536), (short)CbG, (short)(CrG + 65536), (short)CbG, (short)(CrG + 65536), (short)CbG, (short)(CrG + 65536));
                var kB = Vector256.Create((short)(CbB - 131072), 0, (short)(CbB - 131072), 0, (short)(CbB - 131072), 0, (short)(CbB - 131072), 0,
                    (short)(CbB - 131072), 0, (short)(CbB - 131072), 0, (short)(CbB - 131072), 0, (short)(CbB - 131072), 0);
                var half = Vector256.Create(32768);
                var c128 = Vector256.Create((short)128);
                var pack = Vector256.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0x80, 0x80, 0x80, 0x80,
                    0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0x80, 0x80, 0x80, 0x80);
                // each iteration stores 52 bytes for 16 pixels, so stay 2 pixels clear of the row end
                for (; x + 18 <= w; x += 16, dst += 48)
                {
                    var yv = Avx2.ConvertToVector256Int16(yp + x);
                    var cb = Avx2.Subtract(Avx2.ConvertToVector256Int16(cbp + x), c128);
                    var cr = Avx2.Subtract(Avx2.ConvertToVector256Int16(crp + x), c128);
                    var lo = Avx2.UnpackLow(cb, cr);
                    var hi = Avx2.UnpackHigh(cb, cr);
                    var r = Avx2.PackSignedSaturate(
                        Avx2.ShiftRightArithmetic(Avx2.Add(Avx2.MultiplyAddAdjacent(lo, kR), half), 16),
                        Avx2.ShiftRightArithmetic(Avx2.Add(Avx2.MultiplyAddAdjacent(hi, kR), half), 16));
                    var g = Avx2.PackSignedSaturate(
                        Avx2.ShiftRightArithmetic(Avx2.Add(Avx2.MultiplyAddAdjacent(lo, kG), half), 16),
                        Avx2.ShiftRightArithmetic(Avx2.Add(Avx2.MultiplyAddAdjacent(hi, kG), half), 16));
                    var b = Avx2.PackSignedSaturate(
                        Avx2.ShiftRightArithmetic(Avx2.Add(Avx2.MultiplyAddAdjacent(lo, kB), half), 16),
                        Avx2.ShiftRightArithmetic(Avx2.Add(Avx2.MultiplyAddAdjacent(hi, kB), half), 16));
                    r = Avx2.Add(Avx2.Add(r, cr), yv);
                    g = Avx2.Add(Avx2.Subtract(g, cr), yv);
                    b = Avx2.Add(Avx2.Add(b, Avx2.Add(cb, cb)), yv);
                    var rb = Avx2.PackUnsignedSaturate(r, r);
                    var gb = Avx2.PackUnsignedSaturate(g, g);
                    var bb = Avx2.PackUnsignedSaturate(b, b);
                    var rg = Avx2.UnpackLow(rb, gb).AsInt16();
                    var bx = Avx2.UnpackLow(bb, Vector256<byte>.Zero).AsInt16();
                    var p0 = Avx2.Shuffle(Avx2.UnpackLow(rg, bx).AsByte(), pack);
                    var p1 = Avx2.Shuffle(Avx2.UnpackHigh(rg, bx).AsByte(), pack);
                    Sse2.Store(dst, p0.GetLower());
                    Sse2.Store(dst + 12, p1.GetLower());
                    Sse2.Store(dst + 24, p0.GetUpper());
                    Sse2.Store(dst + 36, p1.GetUpper());
                }
            }
            else if (Vector128.IsHardwareAccelerated && !ForceScalar)
            {
                // portable variant: 4 pixels per step in 32 bit lanes; each step stores 16 bytes for 12
                var half = Vector128.Create(32768);
                var c128 = Vector128.Create(128);
                var max = Vector128.Create(255);
                for (; x + 6 <= w; x += 4, dst += 12)
                {
                    var yv = Load4(yp + x);
                    var cb = Load4(cbp + x) - c128;
                    var cr = Load4(crp + x) - c128;
                    var r = yv + Vector128.ShiftRightArithmetic(cr * CrR + half, 16);
                    var g = yv + Vector128.ShiftRightArithmetic(cb * CbG + cr * CrG + half, 16);
                    var b = yv + Vector128.ShiftRightArithmetic(cb * CbB + half, 16);
                    r = Vector128.Min(Vector128.Max(r, Vector128<int>.Zero), max);
                    g = Vector128.Min(Vector128.Max(g, Vector128<int>.Zero), max);
                    b = Vector128.Min(Vector128.Max(b, Vector128<int>.Zero), max);
                    var px = (r | Vector128.ShiftLeft(g, 8) | Vector128.ShiftLeft(b, 16)).AsByte();
                    Vector128.Shuffle(px, Vector128.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0x80, 0x80, 0x80, 0x80)).Store(dst);
                }
            }
            for (; x < w; x++, dst += 3)
            {
                int yy = yp[x], cb = cbp[x] - 128, cr = crp[x] - 128;
                dst[0] = Clamp(yy + ((CrR * cr + 32768) >> 16));
                dst[1] = Clamp(yy + ((CbG * cb + CrG * cr + 32768) >> 16));
                dst[2] = Clamp(yy + ((CbB * cb + 32768) >> 16));
            }
        }

        /// <summary>4 bytes zero extended to 32 bit lanes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<int> Load4(byte* p)
            => Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(p)).AsByte())).AsInt32();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte Clamp(int v) => (byte)((uint)v <= 255 ? v : v < 0 ? 0 : 255);
    }
}
