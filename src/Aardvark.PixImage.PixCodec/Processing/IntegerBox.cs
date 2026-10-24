using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs.Processing
{
    /// <summary>
    /// Resizing by blocks of 2^a x 2^b pixels with equal weights (the box filter at power of two reduction factors) for 8 and
    /// 16 bit samples: block sums in integers, rounded once, no float workspace. The result is the float path's
    /// (<see cref="Resampler"/>) to the bit: with power of two weights and sums that fit the 24 bit mantissa every float
    /// operation there is exact, so it computes the exact mean too (and with premultiplied 8 bit alpha the exact means of
    /// colour times alpha and of alpha, which are then divided as there). A last block of another size (from
    /// <see cref="AxisWeights.Blocks"/>) repeats the float path's operations in its order.
    /// </summary>
    internal static class IntegerBox
    {
        /// <summary>
        /// Whether the job can take this path: the source tiled by blocks of equal weights, of one power of two size per axis
        /// (the last may be smaller).
        /// </summary>
        public static bool Applies(Resampler.Job j, SampleType type)
        {
            int bits = type == SampleType.UInt8 ? 8 : 16;
            if (!Blocks(j.AX, out int lx) || !Blocks(j.AY, out int ly)) return false;
            if (j.PremulAlpha < 0) return bits + lx + ly + 1 <= 24;
            // colour times alpha needs 16 bits, plus the fraction bits of the means
            return bits == 8 && lx + ly <= 8;
        }

        static bool Blocks(AxisWeights a, out int log2)
        {
            log2 = -1;
            int n0 = a.Count[0];
            if (!BitOperations.IsPow2(n0)) return false;
            log2 = BitOperations.Log2((uint)n0);
            for (int j = 0; j < a.Start.Length; j++)
            {
                int n = a.Count[j], wp = j * a.MaxTaps;
                if (a.Start[j] != j * n0 || n != n0 && j < a.Start.Length - 1) return false;
                double w = 1.0 / n;
                for (int k = 0; k < n; k++)
                    if (a.Weights[wp + k] != w) return false;
            }
            return true;
        }

        public static void Run<T>(Resampler.Job j, T[] src, T[] dst) where T : unmanaged, IBinaryInteger<T>
            => new Box<T>(j, src, dst).Run();

        /// <summary>A worker's rows: column sums over the source rows of an output row, then block sums.</summary>
        sealed class Sums
        {
            public readonly int[] Columns, Blocks;
            public int Start = -1, Count;
            public Sums(int srcLen, int rowLen)
            {
                Columns = ArrayPool<int>.Shared.Rent(srcLen);
                Blocks = ArrayPool<int>.Shared.Rent(rowLen);
            }
            public void Return()
            {
                ArrayPool<int>.Shared.Return(Columns);
                ArrayPool<int>.Shared.Return(Blocks);
            }
        }

        sealed class Box<T> where T : unmanaged, IBinaryInteger<T>
        {
            readonly Resampler.Job j;
            readonly T[] src, dst;
            readonly int nc, srcLen, rowLen, pa, max, lx, fullX;

            public Box(Resampler.Job j, T[] src, T[] dst)
            {
                this.j = j; this.src = src; this.dst = dst;
                nc = j.NC; srcLen = j.SW * nc; rowLen = j.DW * nc; pa = j.PremulAlpha;
                max = typeof(T) == typeof(byte) ? 255 : 65535;
                lx = BitOperations.Log2((uint)j.AX.Count[0]);
                fullX = j.AX.Count[j.DW - 1] == j.AX.Count[0] ? j.DW : j.DW - 1;
            }

            public void Run()
            {
                var ct = j.Cancel;
                Parallel.For(0, j.DH, Resampler.Par(j.MaxParallel, ct), () => new Sums(srcLen, rowLen), (y, state, sums) =>
                {
                    if (ct.IsCancellationRequested) { state.Stop(); return sums; }
                    Row(y, sums);
                    return sums;
                }, sums => sums.Return());
                ct.ThrowIfCancellationRequested();
            }

            void Row(int y, Sums sums)
            {
                int sy = j.AY.Start[y], cy = j.AY.Count[y];
                var d = dst.AsSpan(y * rowLen, rowLen);
                if (!BitOperations.IsPow2(cy) || cy > j.AY.Count[0])
                {
                    // a last row of blocks of another height
                    for (int x = 0; x < j.DW; x++) Exact(x, y, d);
                    return;
                }
                var cs = sums.Columns.AsSpan(0, srcLen);
                if (sums.Start != sy || sums.Count != cy)
                {
                    ColumnSums(sy, cy, cs);
                    sums.Start = sy; sums.Count = cy;
                }
                var bs = sums.Blocks.AsSpan(0, fullX * nc);
                BlockSums(cs, bs);
                int l = lx + BitOperations.Log2((uint)cy);
                if (pa < 0)
                {
                    int half = (1 << l) >> 1;
                    if (typeof(T) == typeof(byte)) StoreMeans(bs, MemoryMarshal.Cast<T, byte>(d), half, l);
                    else for (int i = 0; i < bs.Length; i++) d[i] = T.CreateTruncating((bs[i] + half) >> l);
                }
                else if (nc == 4 && pa == 3 && Vector128.IsHardwareAccelerated)
                    StorePremultiplied(bs, MemoryMarshal.Cast<T, byte>(d), fullX, 1f / (1 << l));
                else
                {
                    float rn = 1f / (1 << l);
                    for (int o = 0; o < bs.Length; o += nc)
                    {
                        // the means are exact floats; colour times 1 / alpha (all 0 where alpha is), floor(v + 0.5) clamped
                        float a = bs[o + pa] * rn, f = a > 0 ? 1 / a : 0;
                        for (int c = 0; c < nc; c++)
                            d[o + c] = T.CreateTruncating(Round(c == pa ? a : bs[o + c] * rn * f));
                    }
                }
                if (fullX < j.DW) Exact(j.DW - 1, y, d);
            }

            // floor(v + 0.5) clamped, as the float path stores
            int Round(float v)
            {
                float t = v + 0.5f;
                return !(t > 0) ? 0 : t >= max ? max : (int)t;
            }

            // one pixel as the float path computes it: per channel horizontal sums per row (0 + w v + w v ...), then
            // w h + w h ... over the rows, colour premultiplied by alpha; then divided by alpha, rounded
            void Exact(int x, int y, Span<T> d)
            {
                int sx = j.AX.Start[x], cx = j.AX.Count[x], sy = j.AY.Start[y], cy = j.AY.Count[y];
                float wx = (float)j.AX.Weights[x * j.AX.MaxTaps], wy = (float)j.AY.Weights[y * j.AY.MaxTaps];
                Span<float> v = stackalloc float[nc];
                for (int c = 0; c < nc; c++)
                {
                    float a = 0;
                    for (int r = 0; r < cy; r++)
                    {
                        int p = ((sy + r) * j.SW + sx) * nc;
                        float h = 0;
                        for (int k = 0; k < cx; k++, p += nc)
                        {
                            float s = float.CreateTruncating(src[p + c]);
                            if (pa >= 0 && c != pa) s *= float.CreateTruncating(src[p + pa]);
                            h += wx * s;
                        }
                        a = r == 0 ? wy * h : a + wy * h;
                    }
                    v[c] = a;
                }
                if (pa >= 0)
                {
                    float a = v[pa];
                    if (a > 0)
                    {
                        float inv = 1 / a;
                        for (int c = 0; c < nc; c++) if (c != pa) v[c] *= inv;
                    }
                    else v.Clear();
                }
                for (int c = 0; c < nc; c++) d[x * nc + c] = T.CreateTruncating(Round(v[c]));
            }

            // per source column and channel: the sum over the rows of the samples, or of colour times alpha
            void ColumnSums(int sy, int cy, Span<int> cs)
            {
                for (int r = 0; r < cy; r++)
                {
                    var row = src.AsSpan((sy + r) * srcLen, srcLen);
                    if (pa < 0)
                    {
                        if (typeof(T) == typeof(byte)) AddWidened(MemoryMarshal.Cast<T, byte>(row), cs, r == 0);
                        else AddWidened(MemoryMarshal.Cast<T, ushort>(row), cs, r == 0);
                    }
                    else if (nc == 4 && pa == 3 && Vector128.IsHardwareAccelerated)
                        AddPremultiplied(MemoryMarshal.Cast<T, byte>(row), cs, r == 0);
                    else
                    {
                        for (int p = 0; p < row.Length; p += nc)
                        {
                            int a = int.CreateTruncating(row[p + pa]);
                            for (int c = 0; c < nc; c++)
                            {
                                int v = c == pa ? a : int.CreateTruncating(row[p + c]) * a;
                                cs[p + c] = r == 0 ? v : cs[p + c] + v;
                            }
                        }
                    }
                }
            }

            // per output pixel and channel of the full blocks: the sum of the block's column sums
            void BlockSums(Span<int> cs, Span<int> bs)
            {
                int n = 1 << lx;
                if (n == 1) { cs.Slice(0, bs.Length).CopyTo(bs); return; }
                if (n == 2 && nc == 4 && Vector128.IsHardwareAccelerated)
                {
                    // pairs of pixels: lanes 0..3 + lanes 4..7
                    ref int c0 = ref MemoryMarshal.GetReference(cs);
                    ref int b0 = ref MemoryMarshal.GetReference(bs);
                    for (int o = 0; o < bs.Length; o += 4)
                        (Vector128.LoadUnsafe(ref c0, (nuint)(2 * o)) + Vector128.LoadUnsafe(ref c0, (nuint)(2 * o + 4))).StoreUnsafe(ref b0, (nuint)o);
                    return;
                }
                int step = n * nc;
                for (int o = 0, s = 0; o < bs.Length; o += nc, s += step)
                    for (int c = 0; c < nc; c++)
                    {
                        int v = cs[s + c];
                        for (int i = s + nc + c, e = s + step; i < e; i += nc) v += cs[i];
                        bs[o + c] = v;
                    }
            }
        }

        // (sum + half) >> l to bytes
        static void StoreMeans(ReadOnlySpan<int> bs, Span<byte> dst, int half, int l)
        {
            int i = 0;
            if (Vector128.IsHardwareAccelerated)
            {
                ref int b0 = ref MemoryMarshal.GetReference(bs);
                ref byte d0 = ref MemoryMarshal.GetReference(dst);
                var h = Vector128.Create(half);
                for (; i <= bs.Length - 16; i += 16)
                {
                    var a = Vector128.ShiftRightArithmetic(Vector128.LoadUnsafe(ref b0, (nuint)i) + h, l);
                    var b = Vector128.ShiftRightArithmetic(Vector128.LoadUnsafe(ref b0, (nuint)i + 4) + h, l);
                    var c = Vector128.ShiftRightArithmetic(Vector128.LoadUnsafe(ref b0, (nuint)i + 8) + h, l);
                    var d = Vector128.ShiftRightArithmetic(Vector128.LoadUnsafe(ref b0, (nuint)i + 12) + h, l);
                    // the means are 0..255: narrowing truncates nothing
                    Vector128.Narrow(Vector128.Narrow(a, b).AsUInt16(), Vector128.Narrow(c, d).AsUInt16()).StoreUnsafe(ref d0, (nuint)i);
                }
            }
            for (; i < bs.Length; i++) dst[i] = (byte)((bs[i] + half) >> l);
        }

        static void AddWidened(ReadOnlySpan<byte> src, Span<int> dst, bool first)
        {
            int i = 0, n = Vector<byte>.Count;
            if (Vector.IsHardwareAccelerated)
            {
                int q = Vector<int>.Count;
                for (; i <= src.Length - n; i += n)
                {
                    Vector.Widen(new Vector<byte>(src.Slice(i)), out var l, out var h);
                    Vector.Widen(l, out var a, out var b);
                    Vector.Widen(h, out var c, out var d);
                    Store(Vector.AsVectorInt32(a), dst.Slice(i), first);
                    Store(Vector.AsVectorInt32(b), dst.Slice(i + q), first);
                    Store(Vector.AsVectorInt32(c), dst.Slice(i + 2 * q), first);
                    Store(Vector.AsVectorInt32(d), dst.Slice(i + 3 * q), first);
                }
            }
            for (; i < src.Length; i++) dst[i] = first ? src[i] : dst[i] + src[i];
        }

        static void AddWidened(ReadOnlySpan<ushort> src, Span<int> dst, bool first)
        {
            int i = 0, n = Vector<ushort>.Count;
            if (Vector.IsHardwareAccelerated)
            {
                int q = Vector<int>.Count;
                for (; i <= src.Length - n; i += n)
                {
                    Vector.Widen(new Vector<ushort>(src.Slice(i)), out var a, out var b);
                    Store(Vector.AsVectorInt32(a), dst.Slice(i), first);
                    Store(Vector.AsVectorInt32(b), dst.Slice(i + q), first);
                }
            }
            for (; i < src.Length; i++) dst[i] = first ? src[i] : dst[i] + src[i];
        }

        static void Store(Vector<int> v, Span<int> dst, bool first) => (first ? v : v + new Vector<int>(dst)).CopyTo(dst);

        // RGBA bytes: dst (+)= (r a, g a, b a, a) per pixel
        static void AddPremultiplied(ReadOnlySpan<byte> src, Span<int> dst, bool first)
        {
            ref byte s0 = ref MemoryMarshal.GetReference(src);
            ref int d0 = ref MemoryMarshal.GetReference(dst);
            var alpha = Vector128.Create((byte)3, 3, 3, 3, 7, 7, 7, 7, 11, 11, 11, 11, 15, 15, 15, 15);
            var one = Vector128.Create((byte)0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1);
            var keepAlpha8 = Vector128.Create((byte)0, 0, 0, 0xff, 0, 0, 0, 0xff, 0, 0, 0, 0xff, 0, 0, 0, 0xff);
            int i = 0;
            for (; i <= src.Length - 16; i += 16)
            {
                var px = Vector128.LoadUnsafe(ref s0, (nuint)i);
                // 16 bit products of four pixels: (r, g, b, a) * (a, a, a, 1)
                var f = Vector128.ConditionalSelect(keepAlpha8, one, Vector128.Shuffle(px, alpha));
                var (p0, p1) = Vector128.Widen(px);
                var (f0, f1) = Vector128.Widen(f);
                var (m0, m1) = Vector128.Widen(p0 * f0);
                var (m2, m3) = Vector128.Widen(p1 * f1);
                Add(m0.AsInt32(), ref d0, i, first);
                Add(m1.AsInt32(), ref d0, i + 4, first);
                Add(m2.AsInt32(), ref d0, i + 8, first);
                Add(m3.AsInt32(), ref d0, i + 12, first);
            }
            for (; i < src.Length; i += 4)
            {
                int a = src[i + 3];
                for (int c = 0; c < 4; c++)
                {
                    int v = c == 3 ? a : src[i + c] * a;
                    dst[i + c] = first ? v : dst[i + c] + v;
                }
            }

            static void Add(Vector128<int> v, ref int d, int at, bool first)
                => (first ? v : Vector128.LoadUnsafe(ref d, (nuint)at) + v).StoreUnsafe(ref d, (nuint)at);
        }

        // RGBA block sums of colour times alpha and of alpha -> bytes, with the float path's operations: the means
        // (exact here), colour times 1 / alpha (all 0 where alpha is), floor(v + 0.5) clamped
        static void StorePremultiplied(ReadOnlySpan<int> bs, Span<byte> dst, int pixels, float rn)
        {
            ref int b0 = ref MemoryMarshal.GetReference(bs);
            ref byte d0 = ref MemoryMarshal.GetReference(dst);
            var keepA = Vector128.Create(0, 0, 0, -1).AsSingle();
            var vrn = Vector128.Create(rn);
            var half = Vector128.Create(0.5f);
            var max = Vector128.Create(255f);
            for (int o = 0, e = pixels * 4; o < e; o += 4)
            {
                var v = Vector128.ConvertToSingle(Vector128.LoadUnsafe(ref b0, (nuint)o)) * vrn;
                float a = v.GetElement(3);
                var f = a > 0 ? Vector128.ConditionalSelect(keepA, Vector128<float>.One, Vector128.Create(1 / a)) : Vector128<float>.Zero;
                var r = Vector128.ConvertToInt32(Vector128.Min(Vector128.Max(v * f + half, Vector128<float>.Zero), max));
                var b = Vector128.Narrow(Vector128.Narrow(r, r).AsUInt16(), Vector128.Narrow(r, r).AsUInt16());
                System.Runtime.CompilerServices.Unsafe.WriteUnaligned(ref System.Runtime.CompilerServices.Unsafe.Add(ref d0, o), b.AsUInt32().ToScalar());
            }
        }
    }
}
