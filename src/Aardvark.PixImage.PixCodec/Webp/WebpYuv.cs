using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// Conversion between the Y'CbCr 4:2:0 planes of the lossy format and RGB: BT.601 limited range in 14 bit fixed
    /// point, chroma upsampled with the 9-3-3-1 bilinear kernel. The rounding follows libwebp so that decoded
    /// pixels are identical to what the reference implementation shows.
    /// </summary>
    internal static unsafe class WebpYuv
    {
        const int Fix2 = 6, Mask2 = (256 << Fix2) - 1;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte Clip8(int v) => (v & ~Mask2) == 0 ? (byte)(v >> Fix2) : v < 0 ? (byte)0 : (byte)255;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Store(int y, int u, int v, byte* dst)
        {
            int yy = (y * 19077) >> 8;
            dst[0] = Clip8(yy + ((v * 26149) >> 8) - 14234);
            dst[1] = Clip8(yy - ((u * 6419) >> 8) - ((v * 13320) >> 8) + 8708);
            dst[2] = Clip8(yy + ((u * 33050) >> 8) - 17685);
        }

        /// <summary>Writes the visible part of the frame as interleaved RGB with <paramref name="step"/> bytes per pixel.
        /// The fourth byte, if any, comes from <paramref name="alpha"/> (width x height, row after row) or is 255.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void ToRgb(Vp8Frame f, byte[] dst, int step, bool threaded = false, byte[]? alpha = null)
        {
            int h = f.Height;
            int bands = threaded && (long)f.Width * h >= 1 << 19 && !OperatingSystem.IsBrowser() ? Math.Min(4, Environment.ProcessorCount) : 1;
            if (bands <= 1) ToRgb(f, dst, step, alpha, 0, h);
            else System.Threading.Tasks.Parallel.For(0, bands, i => ToRgb(f, dst, step, alpha, (int)((long)h * i / bands), (int)((long)h * (i + 1) / bands)));
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void ToRgb(Vp8Frame f, byte[] dst, int step, byte[]? alpha, int y0, int y1)
        {
            int w = f.Width, uvH = (f.Height + 1) >> 1;
            byte* chroma = null;
            byte[]? rented = null;
            int need = 2 * w + 32;
            if (need <= 4096) { byte* b = stackalloc byte[need]; chroma = b; }
            else rented = System.Buffers.ArrayPool<byte>.Shared.Rent(need);
            fixed (byte* py = f.Y, pu = f.U, pv = f.V, pd = dst, pr = rented, pa = alpha)
            {
                byte* u = rented != null ? pr : chroma, v = u + w + 16;
                for (int y = y0; y < y1; y++)
                {
                    // rows 2k-1 and 2k lie between the chroma rows k-1 and k; the nearer one weighs three times as much
                    int near = y >> 1, far = (y & 1) != 0 ? near + 1 : near - 1;
                    if (far < 0 || far >= uvH) far = near;
                    UpsampleLine(pu + (long)near * f.UVStride, pu + (long)far * f.UVStride, u, w);
                    UpsampleLine(pv + (long)near * f.UVStride, pv + (long)far * f.UVStride, v, w);
                    ConvertLine(py + (long)y * f.YStride, u, v, pd + (long)y * w * step, w, step, pa == null ? null : pa + (long)y * w);
                }
            }
            if (rented != null) System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }

        /// <summary>
        /// Chroma of one output row: every sample is 9/16 of the nearest stored one, 3/16 of its two neighbors and 1/16
        /// of the diagonal one, rounded in two steps the way libwebp does.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void UpsampleLine(byte* near, byte* far, byte* dst, int w)
        {
            dst[0] = (byte)((3 * near[0] + far[0] + 2) >> 2);
            int last = (w - 1) >> 1;
            int x = 1;
            if (Vector128.IsHardwareAccelerated)
            {
                var eight = Vector128.Create((ushort)8);
                for (; x + 7 <= last; x += 8)
                {
                    var nl = Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(near + x - 1)).AsByte());
                    var nr = Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(near + x)).AsByte());
                    var fl = Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(far + x - 1)).AsByte());
                    var fr = Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(far + x)).AsByte());
                    var avg = nl + nr + fl + fr + eight;
                    var odd = Vector128.ShiftRightLogical(Vector128.ShiftRightLogical(avg + (nr + fl) * (ushort)2, 3) + nl, 1);
                    var even = Vector128.ShiftRightLogical(Vector128.ShiftRightLogical(avg + (nl + fr) * (ushort)2, 3) + nr, 1);
                    Vp8LoopFilterSimd.ZipLo8(Vector128.Narrow(odd, odd), Vector128.Narrow(even, even)).Store(dst + 2 * x - 1);
                }
            }
            for (; x <= last; x++)
            {
                int nl = near[x - 1], nr = near[x], fl = far[x - 1], fr = far[x];
                int avg = nl + nr + fl + fr + 8;
                dst[2 * x - 1] = (byte)((((avg + 2 * (nr + fl)) >> 3) + nl) >> 1);
                dst[2 * x] = (byte)((((avg + 2 * (nl + fr)) >> 3) + nr) >> 1);
            }
            if ((w & 1) == 0) dst[w - 1] = (byte)((3 * near[last] + far[last] + 2) >> 2);
        }

        /// <summary>(v * c) >> 8 for eight samples in 16 bit lanes: the high and the low byte of c are applied separately.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<ushort> MulHi(Vector128<ushort> v, int c)
            => v * (ushort)(c >> 8) + Vector128.ShiftRightLogical(v * (ushort)(c & 255), 8);

        /// <summary>Eight pixels: full resolution Y, U, V in 16 bit lanes to R, G, B (0..255 each, still 16 bit).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Convert8(Vector128<ushort> y, Vector128<ushort> u, Vector128<ushort> v, out Vector128<ushort> r, out Vector128<ushort> g, out Vector128<ushort> b)
        {
            var yy = MulHi(y, 19077);
            var max = Vector128.Create((short)255);
            // red and green stay inside the signed 16 bit range; blue does not, so it is clamped at zero while unsigned
            var rs = (yy + MulHi(v, 26149)).AsInt16() - Vector128.Create((short)14234);
            var gs = (yy + Vector128.Create((ushort)8708)).AsInt16() - (MulHi(u, 6419) + MulHi(v, 13320)).AsInt16();
            var bu = yy + MulHi(u, 33050);
            bu = Vector128.Max(bu, Vector128.Create((ushort)17685)) - Vector128.Create((ushort)17685);
            r = Vector128.Min(Vector128.ShiftRightArithmetic(Vector128.Max(rs, Vector128<short>.Zero), Fix2), max).AsUInt16();
            g = Vector128.Min(Vector128.ShiftRightArithmetic(Vector128.Max(gs, Vector128<short>.Zero), Fix2), max).AsUInt16();
            b = Vector128.Min(Vector128.ShiftRightLogical(bu, Fix2), max.AsUInt16());
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void ConvertLine(byte* y, byte* u, byte* v, byte* dst, int w, int step, byte* alpha = null)
        {
            int x = 0;
            if (Vector128.IsHardwareAccelerated)
            {
                for (; x + 16 <= w; x += 16)
                {
                    var (yl, yh) = Vector128.Widen(Vector128.Load(y + x));
                    var (ul, uh) = Vector128.Widen(Vector128.Load(u + x));
                    var (vl, vh) = Vector128.Widen(Vector128.Load(v + x));
                    Convert8(yl, ul, vl, out var rl, out var gl, out var bl);
                    Convert8(yh, uh, vh, out var rh, out var gh, out var bh);
                    var r = Vector128.Narrow(rl, rh);
                    var g = Vector128.Narrow(gl, gh);
                    var b = Vector128.Narrow(bl, bh);
                    byte* d = dst + x * step;
                    if (step == 4)
                    {
                        var rg0 = Vp8LoopFilterSimd.ZipLo8(r, g); var rg1 = Vp8LoopFilterSimd.ZipHi8(r, g);
                        var a = alpha == null ? Vector128.Create((byte)255) : Vector128.Load(alpha + x);
                        var ba0 = Vp8LoopFilterSimd.ZipLo8(b, a); var ba1 = Vp8LoopFilterSimd.ZipHi8(b, a);
                        Vp8LoopFilterSimd.ZipLo16(rg0, ba0).Store(d);
                        Vp8LoopFilterSimd.ZipHi16(rg0, ba0).Store(d + 16);
                        Vp8LoopFilterSimd.ZipLo16(rg1, ba1).Store(d + 32);
                        Vp8LoopFilterSimd.ZipHi16(rg1, ba1).Store(d + 48);
                    }
                    else
                    {
                        const byte o = 255;     // selects zero
                        (Vector128.Shuffle(r, Vector128.Create(0, o, o, 1, o, o, 2, o, o, 3, o, o, 4, o, o, 5))
                            | Vector128.Shuffle(g, Vector128.Create(o, 0, o, o, 1, o, o, 2, o, o, 3, o, o, 4, o, o))
                            | Vector128.Shuffle(b, Vector128.Create(o, o, 0, o, o, 1, o, o, 2, o, o, 3, o, o, 4, o))).Store(d);
                        (Vector128.Shuffle(r, Vector128.Create(o, o, 6, o, o, 7, o, o, 8, o, o, 9, o, o, 10, o))
                            | Vector128.Shuffle(g, Vector128.Create(5, o, o, 6, o, o, 7, o, o, 8, o, o, 9, o, o, 10))
                            | Vector128.Shuffle(b, Vector128.Create(o, 5, o, o, 6, o, o, 7, o, o, 8, o, o, 9, o, o))).Store(d + 16);
                        (Vector128.Shuffle(r, Vector128.Create(o, 11, o, o, 12, o, o, 13, o, o, 14, o, o, 15, o, o))
                            | Vector128.Shuffle(g, Vector128.Create(o, o, 11, o, o, 12, o, o, 13, o, o, 14, o, o, 15, o))
                            | Vector128.Shuffle(b, Vector128.Create(10, o, o, 11, o, o, 12, o, o, 13, o, o, 14, o, o, 15))).Store(d + 32);
                    }
                }
            }
            for (; x < w; x++)
            {
                byte* d = dst + x * step;
                Store(y[x], u[x], v[x], d);
                if (step == 4) d[3] = alpha == null ? (byte)255 : alpha[x];
            }
        }

        // ------------------------------------------------------------------ RGB to Y'CbCr (encoder side)

        const int YuvFix = 16, YuvHalf = 1 << 15;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte ToY(int r, int g, int b) => (byte)((16839 * r + 33059 * g + 6420 * b + YuvHalf + (16 << YuvFix)) >> YuvFix);

        // chroma from the sum of four pixels
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte ToUv(int v) => (byte)Math.Clamp((v + (YuvHalf << 2) + (128 << (YuvFix + 2))) >> (YuvFix + 2), 0, 255);

        static readonly double[] ToLinear = BuildToLinear();

        static double[] BuildToLinear()
        {
            var t = new double[256];
            for (int i = 0; i < 256; i++) t[i] = Math.Pow(i / 255.0, 2.2);
            return t;
        }

        /// <summary>Four times the average of four samples (0..1020), taken in linear light when they differ much.</summary>
        static int LinearAverage(uint a, uint b, uint c, uint d)
        {
            int ia = (int)(a & 0xff), ib = (int)(b & 0xff), ic = (int)(c & 0xff), id = (int)(d & 0xff);
            int min = Math.Min(Math.Min(ia, ib), Math.Min(ic, id)), max = Math.Max(Math.Max(ia, ib), Math.Max(ic, id));
            // similar samples: the plain mean is as good, and exact
            if (max - min <= 8) return ia + ib + ic + id;
            double lin = (ToLinear[ia] + ToLinear[ib] + ToLinear[ic] + ToLinear[id]) / 4;
            return (int)Math.Round(Math.Pow(lin, 1 / 2.2) * 1020);
        }

        /// <summary>
        /// Converts ARGB pixels to planes padded to whole macroblocks (edge pixels repeated).
        /// <see cref="WebpChroma.Average"/>: chroma is the plain mean of each 2x2 block.
        /// <see cref="WebpChroma.LinearLight"/>: the mean is taken in linear light.
        /// <see cref="WebpChroma.Sharp"/>: plain mean, then every luma sample is corrected for the chroma the decoder
        /// will actually interpolate at its pixel, so that the decoded RGB comes out as close as luma alone can make it.
        /// </summary>
        public static Vp8Frame FromArgb(uint[] argb, int w, int h, WebpChroma mode = WebpChroma.Average, System.Threading.CancellationToken cancel = default)
        {
            int mbW = (w + 15) >> 4, mbH = (h + 15) >> 4;
            var f = new Vp8Frame
            {
                Width = w, Height = h, YStride = mbW * 16, UVStride = mbW * 8,
                Y = new byte[mbW * 16 * mbH * 16], U = new byte[mbW * 8 * mbH * 8], V = new byte[mbW * 8 * mbH * 8],
            };
            int uvW = (w + 1) >> 1, uvH = (h + 1) >> 1;
            fixed (uint* src = argb)
            fixed (byte* py = f.Y, pu = f.U, pv = f.V)
            {
                for (int y = 0; y < h; y++)
                {
                    if ((y & 15) == 0) cancel.ThrowIfCancellationRequested();
                    uint* s = src + (long)y * w;
                    byte* d = py + (long)y * f.YStride;
                    for (int x = 0; x < w; x++)
                    {
                        uint v = s[x];
                        d[x] = ToY((int)(v >> 16) & 0xff, (int)(v >> 8) & 0xff, (int)v & 0xff);
                    }
                    for (int x = w; x < f.YStride; x++) d[x] = d[w - 1];
                }
                for (int y = h; y < mbH * 16; y++)
                    Buffer.MemoryCopy(py + (long)(h - 1) * f.YStride, py + (long)y * f.YStride, f.YStride, f.YStride);

                for (int y = 0; y < uvH; y++)
                {
                    if ((y & 7) == 0) cancel.ThrowIfCancellationRequested();
                    uint* s0 = src + (long)(2 * y) * w;
                    uint* s1 = 2 * y + 1 < h ? s0 + w : s0;
                    byte* du = pu + (long)y * f.UVStride, dv = pv + (long)y * f.UVStride;
                    for (int x = 0; x < uvW; x++)
                    {
                        int x0 = 2 * x, x1 = 2 * x + 1 < w ? x0 + 1 : x0;
                        uint a = s0[x0], b = s0[x1], c = s1[x0], e = s1[x1];
                        int r, g, bl;
                        if (mode == WebpChroma.LinearLight)
                        {
                            r = LinearAverage(a >> 16, b >> 16, c >> 16, e >> 16);
                            g = LinearAverage(a >> 8, b >> 8, c >> 8, e >> 8);
                            bl = LinearAverage(a, b, c, e);
                        }
                        else
                        {
                            r = (int)(((a >> 16) & 0xff) + ((b >> 16) & 0xff) + ((c >> 16) & 0xff) + ((e >> 16) & 0xff));
                            g = (int)(((a >> 8) & 0xff) + ((b >> 8) & 0xff) + ((c >> 8) & 0xff) + ((e >> 8) & 0xff));
                            bl = (int)((a & 0xff) + (b & 0xff) + (c & 0xff) + (e & 0xff));
                        }
                        du[x] = ToUv(-9719 * r - 19081 * g + 28800 * bl);
                        dv[x] = ToUv(28800 * r - 24116 * g - 4684 * bl);
                    }
                    for (int x = uvW; x < f.UVStride; x++) { du[x] = du[uvW - 1]; dv[x] = dv[uvW - 1]; }
                }
                for (int y = uvH; y < mbH * 8; y++)
                {
                    Buffer.MemoryCopy(pu + (long)(uvH - 1) * f.UVStride, pu + (long)y * f.UVStride, f.UVStride, f.UVStride);
                    Buffer.MemoryCopy(pv + (long)(uvH - 1) * f.UVStride, pv + (long)y * f.UVStride, f.UVStride, f.UVStride);
                }
                if (mode == WebpChroma.Sharp) CompensateLuma(src, f, py, pu, pv, cancel);
            }
            return f;
        }

        /// <summary>
        /// Chroma is shared by four pixels and interpolated by the decoder, so on colored edges a pixel gets chroma
        /// that is not its own. Luma is per pixel: pick the value for which the decoded RGB is closest to the source.
        /// </summary>
        static void CompensateLuma(uint* src, Vp8Frame f, byte* py, byte* pu, byte* pv, System.Threading.CancellationToken cancel)
        {
            int w = f.Width, h = f.Height, uvH = (h + 1) >> 1;
            var chroma = new byte[2 * w + 32];
            var decoded = new byte[3 * w + 16];
            byte* rgb = stackalloc byte[4];
            fixed (byte* pc = chroma, pdec = decoded)
            {
                byte* u = pc, v = pc + w + 16;
                for (int y = 0; y < h; y++)
                {
                    if ((y & 15) == 0) cancel.ThrowIfCancellationRequested();
                    int near = y >> 1, far = (y & 1) != 0 ? near + 1 : near - 1;
                    if (far < 0 || far >= uvH) far = near;
                    UpsampleLine(pu + (long)near * f.UVStride, pu + (long)far * f.UVStride, u, w);
                    UpsampleLine(pv + (long)near * f.UVStride, pv + (long)far * f.UVStride, v, w);
                    uint* s = src + (long)y * w;
                    byte* row = py + (long)y * f.YStride;
                    // what the decoder would show for the row as it is; most pixels are fine already
                    ConvertLine(row, u, v, pdec, w, 3);
                    for (int x = 0; x < w; x++)
                    {
                        int r = (int)(s[x] >> 16) & 0xff, g = (int)(s[x] >> 8) & 0xff, b = (int)s[x] & 0xff;
                        int best = row[x];
                        int er = r - pdec[3 * x], eg = g - pdec[3 * x + 1], eb = b - pdec[3 * x + 2];
                        // every channel follows luma with gain 1.164, so the step is the mean error over that (147 / 512);
                        // below a sum of 2 it rounds to nothing
                        int sum = er + eg + eb;
                        if ((uint)(sum + 1) <= 2) continue;
                        int err = er * er + eg * eg + eb * eb;
                        for (int iter = 0; iter < 2; iter++)
                        {
                            sum = er + eg + eb;
                            int cand = Math.Clamp(best + ((sum * 147 + 256) >> 9), 0, 255);
                            if (cand == best) break;
                            Store(cand, u[x], v[x], rgb);
                            int cr = r - rgb[0], cg = g - rgb[1], cb = b - rgb[2];
                            int ce = cr * cr + cg * cg + cb * cb;
                            if (ce >= err) break;
                            best = cand; err = ce; er = cr; eg = cg; eb = cb;
                        }
                        row[x] = (byte)best;
                    }
                }
            }
            // the padding repeats the corrected edge pixels
            for (int y = 0; y < h; y++)
            {
                byte* row = py + (long)y * f.YStride;
                for (int x = w; x < f.YStride; x++) row[x] = row[w - 1];
            }
            for (int y = h; y < f.Y.Length / f.YStride; y++)
                Buffer.MemoryCopy(py + (long)(h - 1) * f.YStride, py + (long)y * f.YStride, f.YStride, f.YStride);
        }
    }
}
