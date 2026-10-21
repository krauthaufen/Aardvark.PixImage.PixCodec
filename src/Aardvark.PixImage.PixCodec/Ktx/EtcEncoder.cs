using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Aardvark.ImageCodecs.DdsImpl;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// ETC1, ETC2 and EAC block encoding, written from the Khronos Data Format Specification 1.3 (sections 21 and 22) and
    /// the published descriptions of the formats (Ström and Akenine-Möller, "iPACKMAN"; Ström and Pettersson, "ETC2:
    /// texture compression using invalid combinations"). The blocks decode with <see cref="EtcBlocks"/>; every error the
    /// searches compare is computed on the exact values that decoder produces.
    /// <para>
    /// Input texels are row-major (texel (x, y) at index y * 4 + x), as the decoders write them: 64 bytes of RGBA8 for the
    /// colour formats, 16 values (with a stride) for EAC. <see cref="BcQuality"/> selects the search effort:
    /// <list type="bullet">
    /// <item>Fast: one flip (the one whose sub-blocks are tighter around their means), the sub-blocks' mean colours as a
    /// differential pair (individual colours when out of range) with every table ranked by the offset model (see
    /// <c>BestTable</c>), the least squares plane unless its residual already loses, T / H from one two-cluster split
    /// only for blocks the other modes leave poor. EAC: per table the two multipliers around the values' span, the
    /// centred base and its least squares refit. Vectorized (AVX2 where available).</item>
    /// <item>Normal: least squares refits of the sub-block colours for every table, exact errors where tables clamp, the
    /// best three splits for T / H with refits, a local search of the plane; EAC adds a scan of nearby bases.</item>
    /// <item>High: neighbouring base colours of every candidate, every split, greedy single channel steps for T / H, wider
    /// plane search; EAC scans bases and multipliers far enough to be near the exhaustive optimum.</item>
    /// </list>
    /// <c>perceptual</c> weights the colour error by the BT.709 luma coefficients (for sRGB content), otherwise the error is
    /// the plain sum of squared differences (best PSNR). Single blocks are encoded on the calling thread; the colour search
    /// keeps per-thread scratch state, so blocks can be encoded in parallel.
    /// </para>
    /// </summary>
    internal static class EtcEncoder
    {
        // ------------------------------------------------------------------ tables (Khronos Data Format Specification)

        // Table 129: intensity modifiers of the 'individual' and 'differential' modes, the small and large value (a, b)
        static readonly int[] ModSmall = { 2, 5, 9, 13, 18, 24, 33, 47 };
        static readonly int[] ModLarge = { 8, 17, 29, 42, 60, 80, 106, 183 };

        // Table 131: distances of the 'T' and 'H' modes
        static readonly int[] Distance = { 3, 6, 11, 16, 23, 32, 41, 64 };

        // Table 133: EAC modifiers, 8 per table
        static readonly sbyte[] EacMod =
        {
            -3, -6, -9, -15, 2, 5, 8, 14,
            -3, -7, -10, -13, 2, 6, 9, 12,
            -2, -5, -8, -13, 1, 4, 7, 12,
            -2, -4, -6, -13, 1, 3, 5, 12,
            -3, -6, -8, -12, 2, 5, 7, 11,
            -3, -7, -9, -11, 2, 6, 8, 10,
            -4, -7, -8, -11, 3, 6, 7, 10,
            -3, -5, -8, -11, 2, 4, 7, 10,
            -2, -6, -8, -10, 1, 5, 7, 9,
            -2, -5, -8, -10, 1, 4, 7, 9,
            -2, -4, -8, -10, 1, 3, 7, 9,
            -2, -5, -7, -10, 1, 4, 6, 9,
            -3, -4, -7, -10, 2, 3, 6, 9,
            -1, -2, -3, -10, 0, 1, 2, 9,
            -4, -6, -8, -9, 3, 5, 7, 8,
            -3, -5, -7, -9, 2, 4, 6, 8,
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Clamp255(int v) => (uint)v <= 255 ? v : v < 0 ? 0 : 255;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Ext4(int v) => v * 17;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Ext5(int v) => v << 3 | v >> 2;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Ext6(int v) => v << 2 | v >> 4;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Ext7(int v) => v << 1 | v >> 6;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Ext(int v, int bits) => bits switch { 4 => Ext4(v), 5 => Ext5(v), 6 => Ext6(v), _ => Ext7(v) };
        static int Quant(float v, int bits)
        {
            int max = (1 << bits) - 1;
            int q = (int)MathF.Round(v * max / 255f);
            return q < 0 ? 0 : q > max ? max : q;
        }
        static int Signed3(int v) => (v ^ 4) - 4;

        /// <summary>A colour that never wins the nearest paint search (used for unavailable entries).</summary>
        const int Never = 2000;

        // texels of the sub-blocks: flip 0 left / right (x &lt; 2), flip 1 top / bottom (y &lt; 2); row-major indices
        static readonly int[][] Halves =
        {
            new[] { 0, 1, 4, 5, 8, 9, 12, 13 }, new[] { 2, 3, 6, 7, 10, 11, 14, 15 },
            new[] { 0, 1, 2, 3, 4, 5, 6, 7 }, new[] { 8, 9, 10, 11, 12, 13, 14, 15 },
        };
        static readonly int[] AllTexels = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

        internal enum ColorFormat { Etc1, Etc2, PunchThrough }

        // ------------------------------------------------------------------ public block API (mirrors EtcBlocks)

        /// <summary>ETC1 (8 bytes) from 16 RGBA texels (alpha ignored): individual and differential modes.</summary>
        public static void Etc1(ReadOnlySpan<byte> rgba, Span<byte> block, BcQuality q = BcQuality.Normal, bool perceptual = false)
            => Color(rgba, block, ColorFormat.Etc1, q, perceptual);

        /// <summary>ETC2 RGB / sRGB (8 bytes) from 16 RGBA texels (alpha ignored): every mode (individual, differential, T,
        /// H, planar).</summary>
        public static void Etc2Rgb(ReadOnlySpan<byte> rgba, Span<byte> block, BcQuality q = BcQuality.Normal, bool perceptual = false)
            => Color(rgba, block, ColorFormat.Etc2, q, perceptual);

        /// <summary>ETC2 RGB8A1 (8 bytes): texels with alpha &lt; 128 become transparent, the others opaque.</summary>
        public static void Etc2PunchThrough(ReadOnlySpan<byte> rgba, Span<byte> block, BcQuality q = BcQuality.Normal, bool perceptual = false)
            => Color(rgba, block, ColorFormat.PunchThrough, q, perceptual);

        /// <summary>ETC2 RGBA8 (16 bytes: EAC alpha, then ETC2 colour).</summary>
        public static void Etc2Rgba(ReadOnlySpan<byte> rgba, Span<byte> block, BcQuality q = BcQuality.Normal, bool perceptual = false)
        {
            EacAlpha8(rgba.Slice(3), 4, block, q);
            Color(rgba, block.Slice(8), ColorFormat.Etc2, q, perceptual);
        }

        /// <summary>The EAC alpha block of ETC2 RGBA8 (8 bytes) from 16 values at values[i * stride], i row-major.</summary>
        public static void EacAlpha8(ReadOnlySpan<byte> values, int stride, Span<byte> block, BcQuality q = BcQuality.Normal)
        {
            Span<int> t = stackalloc int[16];
            for (int i = 0; i < 16; i++) t[i] = values[i * stride];
            WriteEac(block, EacSearch(t, EacKind.Alpha8, q));
        }

        /// <summary>Unsigned R11 EAC (8 bytes) from 16 UNORM16 values at values[i * stride]. For RG11 call it per channel
        /// (or use <see cref="EacRg11(ReadOnlySpan{ushort}, Span{byte}, BcQuality)"/>).</summary>
        public static void EacR11(ReadOnlySpan<ushort> values, int stride, Span<byte> block, BcQuality q = BcQuality.Normal)
        {
            Span<int> t = stackalloc int[16];
            for (int i = 0; i < 16; i++) t[i] = values[i * stride];
            WriteEac(block, EacSearch(t, EacKind.Unsigned11, q));
        }

        /// <summary>Signed R11 EAC (8 bytes) from 16 SNORM16 values at values[i * stride] (-32768 is read as -32767).</summary>
        public static void EacR11Signed(ReadOnlySpan<short> values, int stride, Span<byte> block, BcQuality q = BcQuality.Normal)
        {
            Span<int> t = stackalloc int[16];
            for (int i = 0; i < 16; i++) t[i] = Math.Max((int)values[i * stride], -32767);
            WriteEac(block, EacSearch(t, EacKind.Signed11, q));
        }

        /// <summary>R11 EAC (8 bytes) from 16 floats at values[i * stride]: 0..1 (unsigned) or -1..1 (<paramref name="signed"/>),
        /// clamped.</summary>
        public static void EacR11(ReadOnlySpan<float> values, int stride, bool signed, Span<byte> block, BcQuality q = BcQuality.Normal)
        {
            Span<int> t = stackalloc int[16];
            for (int i = 0; i < 16; i++)
            {
                float f = values[i * stride];
                if (float.IsNaN(f)) f = 0;
                t[i] = signed ? (int)MathF.Round(Math.Clamp(f, -1f, 1f) * 32767f) : (int)MathF.Round(Math.Clamp(f, 0f, 1f) * 65535f);
            }
            WriteEac(block, EacSearch(t, signed ? EacKind.Signed11 : EacKind.Unsigned11, q));
        }

        /// <summary>Unsigned RG11 EAC (16 bytes) from 16 interleaved RG UNORM16 texels (32 values).</summary>
        public static void EacRg11(ReadOnlySpan<ushort> rg, Span<byte> block, BcQuality q = BcQuality.Normal)
        {
            EacR11(rg, 2, block, q);
            EacR11(rg.Slice(1), 2, block.Slice(8), q);
        }

        /// <summary>Signed RG11 EAC (16 bytes) from 16 interleaved RG SNORM16 texels (32 values).</summary>
        public static void EacRg11Signed(ReadOnlySpan<short> rg, Span<byte> block, BcQuality q = BcQuality.Normal)
        {
            EacR11Signed(rg, 2, block, q);
            EacR11Signed(rg.Slice(1), 2, block.Slice(8), q);
        }

        /// <summary>RG11 EAC (16 bytes) from 16 interleaved RG float texels (32 values), see
        /// <see cref="EacR11(ReadOnlySpan{float}, int, bool, Span{byte}, BcQuality)"/>.</summary>
        public static void EacRg11(ReadOnlySpan<float> rg, bool signed, Span<byte> block, BcQuality q = BcQuality.Normal)
        {
            EacR11(rg, 2, signed, block, q);
            EacR11(rg.Slice(1), 2, signed, block.Slice(8), q);
        }

        // ------------------------------------------------------------------ colour: shared state

        sealed class HalfCands
        {
            public int N;
            public bool Free;   // no opaque texel: any colour has error 0
            public readonly int[] Q = new int[512], T = new int[512], E = new int[512];
            public int BestIndex()
            {
                int b = 0;
                for (int i = 1; i < N; i++) if (E[i] < E[b]) b = i;
                return b;
            }
        }

        sealed class Ctx
        {
            public readonly int[] Px = new int[48];      // RGB per texel, row-major
            // the same per channel (R, G, B, opaque mask -1 / 0), row-major (0..15) and column-major (16..31): the
            // sub-blocks are then 8 consecutive texels (left / right at 16 / 24, top / bottom at 0 / 8)
            public readonly int[] Rs = new int[32], Gs = new int[32], Bs = new int[32], Ms = new int[32];
            public bool Uniform;
            public bool PxReady;
            public int OpaqueK;                          // the opaque mask in index order (k = x * 4 + y)
            public bool Exact;                           // evaluate clamping candidates exactly (Normal, High)
            public bool Full;                            // ... all of them (High)
            public int Wr, Wg, Wb;
            public int Opaque;                           // texels that count (punch-through: alpha >= 128)
            public bool Transparent;                     // RGB8A1 block with transparent texels (opaque bit 0)
            public readonly int[] Paint = new int[12];
            public readonly byte[] Sel = new byte[16];
            public readonly HalfCands A = new HalfCands(), B = new HalfCands();
            public readonly int[] Stamp = new int[32768];
            public int Gen;
            public ulong BestWord;
            public int BestErr;

            public void Offer(int err, ulong word)
            {
                if (err < BestErr) { BestErr = err; BestWord = word; }
            }

            public int NextGen()
            {
                if (++Gen == int.MaxValue) { Array.Clear(Stamp); Gen = 1; }
                return Gen;
            }
        }

        [ThreadStatic] static Ctx? t_ctx;

        /// <summary>
        /// Search effort per <see cref="BcQuality"/> (Fast, Normal, High):
        /// </summary>
        sealed class Effort
        {
            public int Radius;      // neighbourhood of the sub-block base colour candidates (per channel)
            public int Splits;      // two-cluster partitions tried for T / H
            public int Keep;        // T / H candidates refined, per mode
            public int Refits;      // T / H least squares refits
            public int Rounds;      // T / H rounds of single channel steps
            public int ThSkip;      // skip T / H when the error so far is at most this much per texel (per unit weight)
            public bool FlipGuess;  // Fast: one flip, chosen by how tight the sub-blocks are
        }

        static readonly Effort[] Efforts =
        {
            new Effort { Radius = 0, Splits = 1, Keep = 1, Refits = 1, Rounds = 0, ThSkip = 60, FlipGuess = true },
            new Effort { Radius = 0, Splits = 3, Keep = 2, Refits = 2, Rounds = 0, ThSkip = 0 },
            new Effort { Radius = 1, Splits = 15, Keep = 4, Refits = 4, Rounds = 8, ThSkip = 0 },
        };

        /// <summary>Encodes a colour block; returns the (weighted) squared error the search measured.</summary>
        internal static int Color(ReadOnlySpan<byte> rgba, Span<byte> o, ColorFormat fmt, BcQuality q, bool perceptual)
        {
            var c = t_ctx ??= new Ctx();
            if (perceptual) { c.Wr = 14; c.Wg = 45; c.Wb = 5; }   // BT.709 luma weights, x 64
            else { c.Wr = c.Wg = c.Wb = 1; }
            int opaque = Load(c, rgba, fmt == ColorFormat.PunchThrough);
            c.Opaque = opaque;
            c.Transparent = opaque != 0xFFFF;
            c.Uniform = !perceptual;
            c.Exact = q != BcQuality.Fast;
            c.Full = q == BcQuality.High;
            c.BestErr = int.MaxValue; c.BestWord = 0;
            if (opaque == 0)
            {
                // all transparent: differential mode, opaque bit 0, every index 2
                BinaryPrimitives.WriteUInt64BigEndian(o, 0xFFFF0000UL);
                return 0;
            }
            // RGB8A1: bit 33 is the opaque bit; the other formats: the differential bit
            uint bit33 = c.Transparent ? 0u : 2u;

            if (IsSolid(c))
                Solid(c, fmt, bit33);
            else
            {
                SubBlockModes(c, q, fmt != ColorFormat.PunchThrough, bit33);
                if (fmt != ColorFormat.Etc1 && c.BestErr > 0)
                {
                    if (!c.Transparent && c.BestErr > 0) Planar(c, q);
                    if (c.BestErr > Efforts[(int)q].ThSkip * BitCount(c.Opaque) * (c.Wr + c.Wg + c.Wb)) TAndH(c, q, bit33);
                }
            }
            BinaryPrimitives.WriteUInt64BigEndian(o, c.BestWord);
            return c.BestErr;
        }

        /// <summary>Fills the SoA arrays from 16 RGBA texels (4 per vector); returns the opaque texel mask. <c>Px</c> is
        /// filled on demand (<see cref="EnsurePx"/>).</summary>
        static int Load(Ctx c, ReadOnlySpan<byte> rgba, bool punch)
        {
            if (rgba.Length < 64) throw new ArgumentException("64 bytes needed", nameof(rgba));
            ref byte src = ref MemoryMarshal.GetReference(rgba);
            var ff = Vector128.Create(255u);
            int opaque = 0;
            for (int y = 0; y < 4; y++)
            {
                var v = Vector128.LoadUnsafe(ref src, (nuint)(y * 16)).AsUInt32();
                if (!BitConverter.IsLittleEndian)
                    v = Vector128.Create(BinaryPrimitives.ReverseEndianness(v[0]), BinaryPrimitives.ReverseEndianness(v[1]), BinaryPrimitives.ReverseEndianness(v[2]), BinaryPrimitives.ReverseEndianness(v[3]));
                (v & ff).AsInt32().StoreUnsafe(ref c.Rs[0], (nuint)(y * 4));
                ((v >> 8) & ff).AsInt32().StoreUnsafe(ref c.Gs[0], (nuint)(y * 4));
                ((v >> 16) & ff).AsInt32().StoreUnsafe(ref c.Bs[0], (nuint)(y * 4));
                var m = punch ? Vector128.GreaterThan(v >> 24, Vector128.Create(127u)).AsInt32() : Vector128<int>.AllBitsSet;
                m.StoreUnsafe(ref c.Ms[0], (nuint)(y * 4));
                opaque |= (int)m.ExtractMostSignificantBits() << (4 * y);
            }
            // column-major copies: k = x * 4 + y
            int[] rs = c.Rs, gs = c.Gs, bs = c.Bs, ms = c.Ms;
            for (int x = 0; x < 4; x++)
            {
                Vector128.Create(rs[x], rs[4 + x], rs[8 + x], rs[12 + x]).StoreUnsafe(ref rs[0], (nuint)(16 + 4 * x));
                Vector128.Create(gs[x], gs[4 + x], gs[8 + x], gs[12 + x]).StoreUnsafe(ref gs[0], (nuint)(16 + 4 * x));
                Vector128.Create(bs[x], bs[4 + x], bs[8 + x], bs[12 + x]).StoreUnsafe(ref bs[0], (nuint)(16 + 4 * x));
                Vector128.Create(ms[x], ms[4 + x], ms[8 + x], ms[12 + x]).StoreUnsafe(ref ms[0], (nuint)(16 + 4 * x));
            }
            c.PxReady = false;
            int ok = 0;
            for (int y = 0; y < 4; y++) ok |= Spread4[opaque >> (4 * y) & 15] << y;
            c.OpaqueK = ok;
            return opaque;
        }

        /// <summary>The interleaved RGB copy the scalar searches use.</summary>
        static void EnsurePx(Ctx c)
        {
            if (c.PxReady) return;
            for (int i = 0; i < 16; i++) { c.Px[i * 3] = c.Rs[i]; c.Px[i * 3 + 1] = c.Gs[i]; c.Px[i * 3 + 2] = c.Bs[i]; }
            c.PxReady = true;
        }

        static bool IsSolid(Ctx c)
        {
            int first = System.Numerics.BitOperations.TrailingZeroCount(c.Opaque);
            var r = Vector128.Create(c.Rs[first]); var g = Vector128.Create(c.Gs[first]); var b = Vector128.Create(c.Bs[first]);
            var diff = Vector128<int>.Zero;
            for (int i = 0; i < 16; i += 4)
                diff |= ((Load(c.Rs, i) ^ r) | (Load(c.Gs, i) ^ g) | (Load(c.Bs, i) ^ b)) & Load(c.Ms, i);
            return diff == Vector128<int>.Zero;
        }

        // first SoA index of each sub-block (Halves order)
        static readonly int[] HalfOffset = { 16, 24, 0, 8 };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<int> Load(int[] a, int i) => Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(a), (nuint)i);

        /// <summary>The vector form of <see cref="PaintError"/> (no choices, no bound) over count (8 or 16) SoA texels
        /// from <paramref name="off"/>.</summary>
        static int PaintErrorV(Ctx c, int off, int count, int[] p)
        {
            var r0 = Vector128.Create(p[0]); var g0 = Vector128.Create(p[1]); var b0 = Vector128.Create(p[2]);
            var r1 = Vector128.Create(p[3]); var g1 = Vector128.Create(p[4]); var b1 = Vector128.Create(p[5]);
            var r2 = Vector128.Create(p[6]); var g2 = Vector128.Create(p[7]); var b2 = Vector128.Create(p[8]);
            var r3 = Vector128.Create(p[9]); var g3 = Vector128.Create(p[10]); var b3 = Vector128.Create(p[11]);
            var acc = Vector128<int>.Zero;
            if (c.Uniform)
            {
                for (int i = off; i < off + count; i += 4)
                {
                    var r = Load(c.Rs, i); var g = Load(c.Gs, i); var b = Load(c.Bs, i);
                    Vector128<int> d, e0, e1, e2, e3;
                    d = r - r0; e0 = d * d; d = g - g0; e0 += d * d; d = b - b0; e0 += d * d;
                    d = r - r1; e1 = d * d; d = g - g1; e1 += d * d; d = b - b1; e1 += d * d;
                    d = r - r2; e2 = d * d; d = g - g2; e2 += d * d; d = b - b2; e2 += d * d;
                    d = r - r3; e3 = d * d; d = g - g3; e3 += d * d; d = b - b3; e3 += d * d;
                    acc += Vector128.Min(Vector128.Min(e0, e1), Vector128.Min(e2, e3)) & Load(c.Ms, i);
                }
            }
            else
            {
                var wr = Vector128.Create(c.Wr); var wg = Vector128.Create(c.Wg); var wb = Vector128.Create(c.Wb);
                for (int i = off; i < off + count; i += 4)
                {
                    var r = Load(c.Rs, i); var g = Load(c.Gs, i); var b = Load(c.Bs, i);
                    Vector128<int> d, e0, e1, e2, e3;
                    d = r - r0; e0 = wr * d * d; d = g - g0; e0 += wg * d * d; d = b - b0; e0 += wb * d * d;
                    d = r - r1; e1 = wr * d * d; d = g - g1; e1 += wg * d * d; d = b - b1; e1 += wb * d * d;
                    d = r - r2; e2 = wr * d * d; d = g - g2; e2 += wg * d * d; d = b - b2; e2 += wb * d * d;
                    d = r - r3; e3 = wr * d * d; d = g - g3; e3 += wg * d * d; d = b - b3; e3 += wb * d * d;
                    acc += Vector128.Min(Vector128.Min(e0, e1), Vector128.Min(e2, e3)) & Load(c.Ms, i);
                }
            }
            return Vector128.Sum(acc);
        }

        // texel (row-major index) of each SoA position
        static readonly int[] PosTexel = MakePosTexel();

        static int[] MakePosTexel()
        {
            var t = new int[32];
            for (int i = 0; i < 16; i++) { t[i] = i; t[16 + i] = (i & 3) * 4 + (i >> 2); }
            return t;
        }

        /// <summary>The vector form of <see cref="PaintError"/> with choices: count (8 or 16) SoA texels from
        /// <paramref name="off"/>, each taking its nearest paint (the first on ties, as the scalar form); the choices go to
        /// <c>Sel</c> by texel, the exact error is returned.</summary>
        static int SelectV(Ctx c, int off, int count, int[] p)
        {
            var r0 = Vector128.Create(p[0]); var g0 = Vector128.Create(p[1]); var b0 = Vector128.Create(p[2]);
            var r1 = Vector128.Create(p[3]); var g1 = Vector128.Create(p[4]); var b1 = Vector128.Create(p[5]);
            var r2 = Vector128.Create(p[6]); var g2 = Vector128.Create(p[7]); var b2 = Vector128.Create(p[8]);
            var r3 = Vector128.Create(p[9]); var g3 = Vector128.Create(p[10]); var b3 = Vector128.Create(p[11]);
            var wr = Vector128.Create(c.Wr); var wg = Vector128.Create(c.Wg); var wb = Vector128.Create(c.Wb);
            Vector128<int> one = Vector128.Create(1), two = Vector128.Create(2), three = Vector128.Create(3);
            var acc = Vector128<int>.Zero;
            Span<int> lanes = stackalloc int[4];
            for (int i = off; i < off + count; i += 4)
            {
                var r = Load(c.Rs, i); var g = Load(c.Gs, i); var b = Load(c.Bs, i);
                Vector128<int> d, e, best, idx, lt;
                d = r - r0; best = wr * d * d; d = g - g0; best += wg * d * d; d = b - b0; best += wb * d * d; idx = Vector128<int>.Zero;
                d = r - r1; e = wr * d * d; d = g - g1; e += wg * d * d; d = b - b1; e += wb * d * d;
                lt = Vector128.LessThan(e, best); best = Vector128.Min(best, e); idx = Vector128.ConditionalSelect(lt, one, idx);
                d = r - r2; e = wr * d * d; d = g - g2; e += wg * d * d; d = b - b2; e += wb * d * d;
                lt = Vector128.LessThan(e, best); best = Vector128.Min(best, e); idx = Vector128.ConditionalSelect(lt, two, idx);
                d = r - r3; e = wr * d * d; d = g - g3; e += wg * d * d; d = b - b3; e += wb * d * d;
                lt = Vector128.LessThan(e, best); best = Vector128.Min(best, e); idx = Vector128.ConditionalSelect(lt, three, idx);
                acc += best & Load(c.Ms, i);
                idx.CopyTo(lanes);
                for (int j = 0; j < 4; j++) c.Sel[PosTexel[i + j]] = (byte)lanes[j];
            }
            return Vector128.Sum(acc);
        }

        // 4 bits spread to every 4th bit (row-major lane x to index bit position x * 4)
        static readonly int[] Spread4 = { 0x0000, 0x0001, 0x0010, 0x0011, 0x0100, 0x0101, 0x0110, 0x0111, 0x1000, 0x1001, 0x1010, 0x1011, 0x1100, 0x1101, 0x1110, 0x1111 };

        /// <summary>
        /// As <see cref="SelectV"/>, but the choices go straight into the index word's bits (pixel k = x * 4 + y):
        /// <paramref name="lsb"/> / <paramref name="msb"/> receive the low / high bits of the texels from off.
        /// </summary>
        static int SelectBits(Ctx c, int off, int count, int[] p, ref int lsb, ref int msb)
        {
            var r0 = Vector128.Create(p[0]); var g0 = Vector128.Create(p[1]); var b0 = Vector128.Create(p[2]);
            var r1 = Vector128.Create(p[3]); var g1 = Vector128.Create(p[4]); var b1 = Vector128.Create(p[5]);
            var r2 = Vector128.Create(p[6]); var g2 = Vector128.Create(p[7]); var b2 = Vector128.Create(p[8]);
            var r3 = Vector128.Create(p[9]); var g3 = Vector128.Create(p[10]); var b3 = Vector128.Create(p[11]);
            var wr = Vector128.Create(c.Wr); var wg = Vector128.Create(c.Wg); var wb = Vector128.Create(c.Wb);
            Vector128<int> one = Vector128.Create(1), two = Vector128.Create(2), three = Vector128.Create(3);
            var acc = Vector128<int>.Zero;
            for (int i = off; i < off + count; i += 4)
            {
                var r = Load(c.Rs, i); var g = Load(c.Gs, i); var b = Load(c.Bs, i);
                Vector128<int> d, e, best, idx, lt;
                d = r - r0; best = wr * d * d; d = g - g0; best += wg * d * d; d = b - b0; best += wb * d * d; idx = Vector128<int>.Zero;
                d = r - r1; e = wr * d * d; d = g - g1; e += wg * d * d; d = b - b1; e += wb * d * d;
                lt = Vector128.LessThan(e, best); best = Vector128.Min(best, e); idx = Vector128.ConditionalSelect(lt, one, idx);
                d = r - r2; e = wr * d * d; d = g - g2; e += wg * d * d; d = b - b2; e += wb * d * d;
                lt = Vector128.LessThan(e, best); best = Vector128.Min(best, e); idx = Vector128.ConditionalSelect(lt, two, idx);
                d = r - r3; e = wr * d * d; d = g - g3; e += wg * d * d; d = b - b3; e += wb * d * d;
                lt = Vector128.LessThan(e, best); best = Vector128.Min(best, e); idx = Vector128.ConditionalSelect(lt, three, idx);
                acc += best & Load(c.Ms, i);
                int lo = (int)(idx << 31).ExtractMostSignificantBits(), hi = (int)(idx << 30).ExtractMostSignificantBits();
                if (i >= 16) { lsb |= lo << (i - 16); msb |= hi << (i - 16); }        // column-major: lane order is k order
                else { lsb |= Spread4[lo] << (i >> 2); msb |= Spread4[hi] << (i >> 2); }   // row y = i / 4, lane x
            }
            return Vector128.Sum(acc);
        }

        /// <summary>Per texel (vectors of 4, count texels from off): weighted squared distance E to (r, g, b) and the
        /// weighted sum D of the channel differences (texel - colour); masked texels give 0.</summary>
        static void Terms(Ctx c, int off, int count, int r, int g, int b, Span<Vector128<int>> e, Span<Vector128<int>> d)
        {
            var vr = Vector128.Create(r); var vg = Vector128.Create(g); var vb = Vector128.Create(b);
            var wr = Vector128.Create(c.Wr); var wg = Vector128.Create(c.Wg); var wb = Vector128.Create(c.Wb);
            for (int k = 0; k < count / 4; k++)
            {
                int i = off + k * 4;
                var m = Load(c.Ms, i);
                var dr = Load(c.Rs, i) - vr; var dg = Load(c.Gs, i) - vg; var db = Load(c.Bs, i) - vb;
                if (c.Uniform)
                {
                    e[k] = (dr * dr + dg * dg + db * db) & m;
                    d[k] = (dr + dg + db) & m;
                }
                else
                {
                    e[k] = (wr * dr * dr + wg * dg * dg + wb * db * db) & m;
                    d[k] = (wr * dr + wg * dg + wb * db) & m;
                }
            }
        }

        /// <summary>
        /// Error of the texels against up to four paint colours (12 ints, clamped; unavailable ones <see cref="Never"/>),
        /// each texel taking its nearest. Stops once the sum reaches <paramref name="bound"/>. <paramref name="sel"/>
        /// receives the choices (only complete when the result is below the bound).
        /// </summary>
        static int PaintError(Ctx c, int[] texels, int[] paint, int bound, byte[]? sel)
        {
            EnsurePx(c);
            var px = c.Px;
            int wr = c.Wr, wg = c.Wg, wb = c.Wb, opaque = c.Opaque;
            int r0 = paint[0], g0 = paint[1], b0 = paint[2], r1 = paint[3], g1 = paint[4], b1 = paint[5];
            int r2 = paint[6], g2 = paint[7], b2 = paint[8], r3 = paint[9], g3 = paint[10], b3 = paint[11];
            int err = 0;
            foreach (int t in texels)
            {
                if ((opaque >> t & 1) == 0) continue;
                int pr = px[t * 3], pg = px[t * 3 + 1], pb = px[t * 3 + 2], d, e, best, bi;
                d = r0 - pr; e = wr * d * d; d = g0 - pg; e += wg * d * d; d = b0 - pb; e += wb * d * d; best = e; bi = 0;
                d = r1 - pr; e = wr * d * d; d = g1 - pg; e += wg * d * d; d = b1 - pb; e += wb * d * d; if (e < best) { best = e; bi = 1; }
                d = r2 - pr; e = wr * d * d; d = g2 - pg; e += wg * d * d; d = b2 - pb; e += wb * d * d; if (e < best) { best = e; bi = 2; }
                d = r3 - pr; e = wr * d * d; d = g3 - pg; e += wg * d * d; d = b3 - pb; e += wb * d * d; if (e < best) { best = e; bi = 3; }
                err += best;
                if (sel != null) sel[t] = (byte)bi;
                if (err >= bound) return err;
            }
            return err;
        }

        static void SetPaint(int[] p, int i, int r, int g, int b)
        {
            p[i * 3] = Clamp255(r); p[i * 3 + 1] = Clamp255(g); p[i * 3 + 2] = Clamp255(b);
        }

        static void NeverPaint(int[] p, int i) => p[i * 3] = p[i * 3 + 1] = p[i * 3 + 2] = Never;

        /// <summary>The 32 bit index word: texel t's index (transparent texels 2), pixels a..p run down the columns.</summary>
        static uint Indices(Ctx c, byte[] sel)
        {
            uint lo = 0;
            for (int t = 0; t < 16; t++)
            {
                int i = (c.Opaque >> t & 1) != 0 ? sel[t] : 2;
                int k = (t & 3) * 4 + (t >> 2);
                lo |= (uint)(i & 1) << k | (uint)(i >> 1) << (k + 16);
            }
            return lo;
        }

        // ------------------------------------------------------------------ individual / differential modes

        /// <summary>Modifier of index k of table t (Table 130); <paramref name="transparent"/>: punch-through without the
        /// opaque bit, where index 0 is 0 and index 2 transparent.</summary>
        static int Modifier(int t, int k, bool transparent) => k switch
        {
            0 => transparent ? 0 : ModSmall[t],
            1 => ModLarge[t],
            2 => -ModSmall[t],
            _ => -ModLarge[t],
        };

        static void SubPaints(int[] p, int r, int g, int b, int t, bool transparent)
        {
            int a = transparent ? 0 : ModSmall[t], l = ModLarge[t];
            SetPaint(p, 0, r + a, g + a, b + a);
            SetPaint(p, 1, r + l, g + l, b + l);
            if (transparent) NeverPaint(p, 2); else SetPaint(p, 2, r - a, g - a, b - a);
            SetPaint(p, 3, r - l, g - l, b - l);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Pack(int r, int g, int b) => r << 10 | g << 5 | b;

        /// <summary>
        /// Best table for one quantized base colour of a sub-block, and its error. A modifier m moves every channel by m, so
        /// a texel's error is E + W m^2 - 2 m D (E its weighted squared distance from the base, D the weighted sum of its
        /// channel differences, W the weight sum): the best modifier of each table follows from |D| alone. That is exact
        /// unless the table clamps; the model then only overestimates and the table is evaluated exactly as
        /// <see cref="Recheck"/> decides.
        /// </summary>
        static int BestTable(Ctx c, int half, int qc, int bits, out int table)
        {
            int r = Ext(qc >> 10, bits), g = Ext(qc >> 5 & 31, bits), b = Ext(qc & 31, bits);
            int off = HalfOffset[half], w = c.Wr + c.Wg + c.Wb;
            int lo = Math.Min(r, Math.Min(g, b)), hi = Math.Max(r, Math.Max(g, b));
            bool tr = c.Transparent;
            Span<int> sums = stackalloc int[8];
            if (Avx2.IsSupported) TableSums256(c, off, r, g, b, w, tr, sums);
            else
            {
                Span<Vector128<int>> ev = stackalloc Vector128<int>[2], dv = stackalloc Vector128<int>[2];
                Terms(c, off, 8, r, g, b, ev, dv);
                var esum = ev[0] + ev[1];
                var a0 = Vector128.Abs(dv[0]); var a1 = Vector128.Abs(dv[1]);
                var m0 = Load(c.Ms, off); var m1 = Load(c.Ms, off + 4);
                Span<Vector128<int>> xs = stackalloc Vector128<int>[8];
                for (int t = 0; t < 8; t++)
                {
                    int sa = tr ? 0 : ModSmall[t], la = ModLarge[t];
                    var ks = Vector128.Create(w * sa * sa); var kl = Vector128.Create(w * la * la);
                    var s2 = Vector128.Create(2 * sa); var l2 = Vector128.Create(2 * la);
                    xs[t] = esum + (Vector128.Min(ks - s2 * a0, kl - l2 * a0) & m0) + (Vector128.Min(ks - s2 * a1, kl - l2 * a1) & m1);
                }
                HorizontalSums(xs, sums);
            }
            int best = int.MaxValue;
            table = 0;
            for (int t = 0; t < 8; t++)
            {
                int la = ModLarge[t], e = sums[t];
                if ((lo - la < 0 || hi + la > 255) && Recheck(c, e, best))
                {
                    SubPaints(c.Paint, r, g, b, t, tr);
                    e = PaintErrorV(c, off, 8, c.Paint);
                }
                if (e < best) { best = e; table = t; }
            }
            return best;
        }

        /// <summary>The offset model's error of all eight tables for one sub-block (8 texels in one 256 bit vector).</summary>
        static void TableSums256(Ctx c, int off, int r, int g, int b, int w, bool tr, Span<int> sums)
        {
            var m = Vector256.LoadUnsafe(ref c.Ms[0], (nuint)off);
            var dr = Vector256.LoadUnsafe(ref c.Rs[0], (nuint)off) - Vector256.Create(r);
            var dg = Vector256.LoadUnsafe(ref c.Gs[0], (nuint)off) - Vector256.Create(g);
            var db = Vector256.LoadUnsafe(ref c.Bs[0], (nuint)off) - Vector256.Create(b);
            Vector256<int> e, d;
            if (c.Uniform) { e = dr * dr + dg * dg + db * db; d = dr + dg + db; }
            else
            {
                var wr = Vector256.Create(c.Wr); var wg = Vector256.Create(c.Wg); var wb = Vector256.Create(c.Wb);
                e = wr * dr * dr + wg * dg * dg + wb * db * db; d = wr * dr + wg * dg + wb * db;
            }
            var a = Vector256.Abs(d);
            Span<Vector256<int>> x = stackalloc Vector256<int>[8];
            for (int t = 0; t < 8; t++)
            {
                int sa = tr ? 0 : ModSmall[t], la = ModLarge[t];
                x[t] = (e + Vector256.Min(Vector256.Create(w * sa * sa) - Vector256.Create(2 * sa) * a,
                                          Vector256.Create(w * la * la) - Vector256.Create(2 * la) * a)) & m;
            }
            var g0 = Avx2.HorizontalAdd(Avx2.HorizontalAdd(x[0], x[1]), Avx2.HorizontalAdd(x[2], x[3]));
            var g1 = Avx2.HorizontalAdd(Avx2.HorizontalAdd(x[4], x[5]), Avx2.HorizontalAdd(x[6], x[7]));
            (g0.GetLower() + g0.GetUpper()).CopyTo(sums);
            (g1.GetLower() + g1.GetUpper()).CopyTo(sums.Slice(4));
        }

        /// <summary>The lane sums of 8 vectors (pairwise horizontal adds where available).</summary>
        static void HorizontalSums(ReadOnlySpan<Vector128<int>> x, Span<int> sums)
        {
            if (Ssse3.IsSupported)
            {
                var s0 = Ssse3.HorizontalAdd(Ssse3.HorizontalAdd(x[0], x[1]), Ssse3.HorizontalAdd(x[2], x[3]));
                var s1 = Ssse3.HorizontalAdd(Ssse3.HorizontalAdd(x[4], x[5]), Ssse3.HorizontalAdd(x[6], x[7]));
                s0.CopyTo(sums); s1.CopyTo(sums.Slice(4));
            }
            else
                for (int t = 0; t < 8; t++) sums[t] = Vector128.Sum(x[t]);
        }

        /// <summary>Whether a candidate whose paints clamp (its offset model error <paramref name="model"/> only an upper
        /// bound) is worth an exact evaluation against the best error so far: High always, Normal when the model is within
        /// half again of the best, Fast never.</summary>
        static bool Recheck(Ctx c, int model, int best)
            => c.Exact && (c.Full || best == int.MaxValue || model < best + (best >> 1));

        /// <summary>
        /// Candidate base colours of one sub-block at 4 or 5 bits per channel, each with its best table: the quantized mean,
        /// (Normal, High) the least squares refits of the mean for every table, and (High) their neighbours in a 3x3x3 box.
        /// </summary>
        static void HalfSearch(Ctx c, int half, int bits, BcQuality q, HalfCands o)
        {
            EnsurePx(c);
            var tex = Halves[half];
            var px = c.Px;
            int n = 0, sr = 0, sg = 0, sb = 0;
            foreach (int t in tex)
                if ((c.Opaque >> t & 1) != 0) { sr += px[t * 3]; sg += px[t * 3 + 1]; sb += px[t * 3 + 2]; n++; }
            o.N = 0;
            o.Free = n == 0;
            if (n == 0) return;
            int max = (1 << bits) - 1;
            Span<int> centres = stackalloc int[9];
            int nc = 0;
            float mr = (float)sr / n, mg = (float)sg / n, mb = (float)sb / n;
            centres[nc++] = Pack(Quant(mr, bits), Quant(mg, bits), Quant(mb, bits));
            if (q >= BcQuality.Normal)
            {
                int c0 = centres[0];
                int br = Ext(c0 >> 10, bits), bg = Ext(c0 >> 5 & 31, bits), bb = Ext(c0 & 31, bits);
                int w = c.Wr + c.Wg + c.Wb;
                Span<int> dd = stackalloc int[8];
                int nd = 0;
                foreach (int x in tex)
                    if ((c.Opaque >> x & 1) != 0)
                        dd[nd++] = c.Wr * (px[x * 3] - br) + c.Wg * (px[x * 3 + 1] - bg) + c.Wb * (px[x * 3 + 2] - bb);
                for (int t = 0; t < 8; t++)
                {
                    // each texel's best modifier by the offset model (see BestTable), then the mean of texel - modifier
                    int sa = c.Transparent ? 0 : ModSmall[t], la = ModLarge[t], ks = w * sa * sa, kl = w * la * la;
                    int msum = 0;
                    for (int i = 0; i < nd; i++)
                    {
                        int a = Math.Abs(dd[i]);
                        int m = ks - 2 * sa * a <= kl - 2 * la * a ? sa : la;
                        msum += dd[i] >= 0 ? m : -m;
                    }
                    int ar = sr - msum, ag = sg - msum, ab = sb - msum;
                    int qc = Pack(Quant((float)ar / n, bits), Quant((float)ag / n, bits), Quant((float)ab / n, bits));
                    bool seen = false;
                    for (int k = 0; k < nc; k++) seen |= centres[k] == qc;
                    if (!seen) centres[nc++] = qc;
                }
            }
            int gen = c.NextGen();
            int radius = Efforts[(int)q].Radius;
            for (int k = 0; k < nc; k++)
            {
                int cr = centres[k] >> 10, cg = centres[k] >> 5 & 31, cb = centres[k] & 31;
                for (int dr = -radius; dr <= radius; dr++)
                    for (int dg = -radius; dg <= radius; dg++)
                        for (int db = -radius; db <= radius; db++)
                        {
                            int r = cr + dr, g = cg + dg, b = cb + db;
                            if ((uint)r > (uint)max || (uint)g > (uint)max || (uint)b > (uint)max) continue;
                            int qc = Pack(r, g, b);
                            if (c.Stamp[qc] == gen) continue;
                            c.Stamp[qc] = gen;
                            int e = BestTable(c, half, qc, bits, out int table);
                            o.Q[o.N] = qc; o.T[o.N] = table; o.E[o.N] = e; o.N++;
                        }
            }
        }

        static bool DiffFeasible(int a, int b)
        {
            int dr = (b >> 10) - (a >> 10), dg = (b >> 5 & 31) - (a >> 5 & 31), db = (b & 31) - (a & 31);
            return dr >= -4 && dr <= 3 && dg >= -4 && dg <= 3 && db >= -4 && db <= 3;
        }

        /// <summary>v moved into the range a differential pair allows: v - other in [lo, lo + 7] per channel (the second colour
        /// against the first: lo = -4; the first against the second: lo = -3).</summary>
        static int Project(int v, int other, int lo)
        {
            int r = Math.Clamp(v >> 10, Math.Max(0, (other >> 10) + lo), Math.Min(31, (other >> 10) + lo + 7));
            int g = Math.Clamp(v >> 5 & 31, Math.Max(0, (other >> 5 & 31) + lo), Math.Min(31, (other >> 5 & 31) + lo + 7));
            int b = Math.Clamp(v & 31, Math.Max(0, (other & 31) + lo), Math.Min(31, (other & 31) + lo + 7));
            return Pack(r, g, b);
        }

        /// <summary>Weighted-free sums of the opaque texels of a sub-block and their count.</summary>
        static int HalfSums(Ctx c, int half, out int sr, out int sg, out int sb)
        {
            int off = HalfOffset[half];
            var m0 = Load(c.Ms, off); var m1 = Load(c.Ms, off + 4);
            sr = Vector128.Sum((Load(c.Rs, off) & m0) + (Load(c.Rs, off + 4) & m1));
            sg = Vector128.Sum((Load(c.Gs, off) & m0) + (Load(c.Gs, off + 4) & m1));
            sb = Vector128.Sum((Load(c.Bs, off) & m0) + (Load(c.Bs, off + 4) & m1));
            return -Vector128.Sum(m0 + m1);
        }

        static int MeanColor(int sr, int sg, int sb, int n, int bits)
        {
            float f = 1f / n;
            return Pack(Quant(sr * f, bits), Quant(sg * f, bits), Quant(sb * f, bits));
        }

        /// <summary>
        /// Fast individual / differential search: per flip the quantized mean colours of the sub-blocks, as a differential
        /// pair when they are in range, else as individual colours (punch-through: the second moved into range), each with
        /// its best table by the offset model; the better flip is evaluated exactly.
        /// </summary>
        static void FastSubBlocks(Ctx c, bool allowIndividual, uint bit33)
        {
            int bestErr = int.MaxValue, bFlip = 0, bqa = 0, bqb = 0, bta = 0, btb = 0;
            bool bDiff = true;
            Span<int> sums = stackalloc int[16];   // per sub-block: count, r, g, b
            for (int h = 0; h < 4; h++) sums[h * 4] = HalfSums(c, h, out sums[h * 4 + 1], out sums[h * 4 + 2], out sums[h * 4 + 3]);
            int only = -1;
            if (Efforts[0].FlipGuess)
            {
                // the flip whose sub-blocks are tighter around their means (the larger sum of |S|^2 / n; sum |p|^2 is common)
                Span<double> score = stackalloc double[2];
                for (int h = 0; h < 4; h++)
                {
                    int n = sums[h * 4];
                    if (n == 0) continue;
                    double sr = sums[h * 4 + 1], sg = sums[h * 4 + 2], sb = sums[h * 4 + 3];
                    score[h >> 1] += (c.Wr * sr * sr + c.Wg * sg * sg + c.Wb * sb * sb) / n;
                }
                only = score[1] > score[0] ? 1 : 0;
            }
            for (int flip = 0; flip < 2; flip++)
            {
                if (only >= 0 && flip != only) continue;
                int ha = flip * 2, hb = ha + 1;
                int na = sums[ha * 4], ar = sums[ha * 4 + 1], ag = sums[ha * 4 + 2], ab = sums[ha * 4 + 3];
                int nb = sums[hb * 4], br = sums[hb * 4 + 1], bg = sums[hb * 4 + 2], bbl = sums[hb * 4 + 3];
                if (na == 0) { na = nb; ar = br; ag = bg; ab = bbl; }
                if (nb == 0) { nb = na; br = ar; bg = ag; bbl = ab; }
                int qa = MeanColor(ar, ag, ab, na, 5), qb = MeanColor(br, bg, bbl, nb, 5);
                bool diff = true;
                if (!DiffFeasible(qa, qb))
                {
                    if (allowIndividual)
                    {
                        diff = false;
                        qa = MeanColor(ar, ag, ab, na, 4); qb = MeanColor(br, bg, bbl, nb, 4);
                    }
                    else qb = Project(qb, qa, -4);
                }
                int bits = diff ? 5 : 4;
                int e = BestTable(c, ha, qa, bits, out int ta) + BestTable(c, hb, qb, bits, out int tb);
                if (e < bestErr) { bestErr = e; bFlip = flip; bDiff = diff; bqa = qa; bqb = qb; bta = ta; btb = tb; }
            }
            OfferSub(c, bDiff, bFlip, bqa, bqb, bta, btb, bit33);
        }

        static void SubBlockModes(Ctx c, BcQuality q, bool allowIndividual, uint bit33)
        {
            if (q == BcQuality.Fast)
            {
                FastSubBlocks(c, allowIndividual, bit33);
                return;
            }
            var A = c.A; var B = c.B;
            // Fast measures candidates by the offset model (an upper bound where tables clamp) and evaluates only the best
            int lazyErr = int.MaxValue, lFlip = 0, lqa = 0, lqb = 0, lta = 0, ltb = 0;
            bool lDiff = true;
            for (int flip = 0; flip < 2; flip++)
            {
                int ha = flip * 2, hb = ha + 1;
                // differential
                HalfSearch(c, ha, 5, q, A);
                HalfSearch(c, hb, 5, q, B);
                int qa = 0, qb = 0, ta = 0, tb = 0, best = int.MaxValue;
                bool constrained = false;   // the sub-blocks' best colours are out of differential range
                if (A.Free && B.Free) best = 0;
                else if (A.Free) { int j = B.BestIndex(); qa = qb = B.Q[j]; tb = B.T[j]; best = B.E[j]; }
                else if (B.Free) { int i = A.BestIndex(); qa = qb = A.Q[i]; ta = A.T[i]; best = A.E[i]; }
                else
                {
                    for (int i = 0; i < A.N; i++)
                        for (int j = 0; j < B.N; j++)
                        {
                            int e = A.E[i] + B.E[j];
                            if (e < best && DiffFeasible(A.Q[i], B.Q[j])) { best = e; qa = A.Q[i]; qb = B.Q[j]; ta = A.T[i]; tb = B.T[j]; }
                        }
                    int ia = A.BestIndex(), ib = B.BestIndex();
                    if (best > A.E[ia] + B.E[ib])
                    {
                        constrained = true;
                        // the unconstrained best pair is out of range: move one colour into the other's range
                        int p = Project(B.Q[ib], A.Q[ia], -4);
                        int e = A.E[ia] + BestTable(c, hb, p, 5, out int t);
                        if (e < best) { best = e; qa = A.Q[ia]; ta = A.T[ia]; qb = p; tb = t; }
                        p = Project(A.Q[ia], B.Q[ib], -3);
                        e = B.E[ib] + BestTable(c, ha, p, 5, out t);
                        if (e < best) { best = e; qb = B.Q[ib]; tb = B.T[ib]; qa = p; ta = t; }
                    }
                }
                if (!c.Exact)
                {
                    if (best < lazyErr) { lazyErr = best; lDiff = true; lFlip = flip; lqa = qa; lqb = qb; lta = ta; ltb = tb; }
                }
                else if (best < c.BestErr) OfferSub(c, true, flip, qa, qb, ta, tb, bit33);
                if (c.BestErr == 0) return;

                if (allowIndividual && (q != BcQuality.Fast || constrained))
                {
                    HalfSearch(c, ha, 4, q, A);
                    HalfSearch(c, hb, 4, q, B);
                    int ia = A.BestIndex(), ib = B.BestIndex();
                    int e = A.E[ia] + B.E[ib];
                    if (!c.Exact)
                    {
                        if (e < lazyErr) { lazyErr = e; lDiff = false; lFlip = flip; lqa = A.Q[ia]; lqb = B.Q[ib]; lta = A.T[ia]; ltb = B.T[ib]; }
                    }
                    else if (e < c.BestErr) OfferSub(c, false, flip, A.Q[ia], B.Q[ib], A.T[ia], B.T[ib], bit33);
                }
            }
            if (!c.Exact) OfferSub(c, lDiff, lFlip, lqa, lqb, lta, ltb, bit33);
        }

        /// <summary>Individual (4 bit colours) or differential (5 bit colours) block word, with its indices.</summary>
        static void OfferSub(Ctx c, bool diff, int flip, int qa, int qb, int ta, int tb, uint bit33)
        {
            int bits = diff ? 5 : 4, err = 0, lsb = 0, msb = 0;
            for (int h = 0; h < 2; h++)
            {
                int qc = h == 0 ? qa : qb;
                SubPaints(c.Paint, Ext(qc >> 10, bits), Ext(qc >> 5 & 31, bits), Ext(qc & 31, bits), h == 0 ? ta : tb, c.Transparent);
                err += SelectBits(c, HalfOffset[flip * 2 + h], 8, c.Paint, ref lsb, ref msb);
            }
            // transparent texels: index 2
            lsb &= c.OpaqueK; msb |= ~c.OpaqueK & 0xFFFF;
            if (err < c.BestErr) c.Offer(err, SubHeader(diff, qa, qb, ta, tb, bit33) | (ulong)(uint)flip << 32 | (uint)lsb | (uint)msb << 16);
        }

        // ------------------------------------------------------------------ T and H modes

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Pack4(int r, int g, int b) => r << 8 | g << 4 | b;

        static void TPaints(int[] p, int c1, int c2, int d, bool transparent)
        {
            int dist = Distance[d];
            int r2 = Ext4(c2 >> 8), g2 = Ext4(c2 >> 4 & 15), b2 = Ext4(c2 & 15);
            SetPaint(p, 0, Ext4(c1 >> 8), Ext4(c1 >> 4 & 15), Ext4(c1 & 15));
            SetPaint(p, 1, r2 + dist, g2 + dist, b2 + dist);
            if (transparent) NeverPaint(p, 2); else SetPaint(p, 2, r2, g2, b2);
            SetPaint(p, 3, r2 - dist, g2 - dist, b2 - dist);
        }

        /// <summary>H mode: the distance's low bit is the order of the two colours. Opaque blocks can swap them (and their
        /// indices), so only equal colours with an even distance index are impossible; with transparent texels the order
        /// is fixed (index 2 is the transparent one).</summary>
        static bool HValid(int c1, int c2, int d, bool transparent)
            => transparent ? (c1 >= c2 ? 1 : 0) == (d & 1) : c1 != c2 || (d & 1) == 1;

        static void HPaints(int[] p, int c1, int c2, int d, bool transparent)
        {
            int dist = Distance[d];
            int r1 = Ext4(c1 >> 8), g1 = Ext4(c1 >> 4 & 15), b1 = Ext4(c1 & 15);
            int r2 = Ext4(c2 >> 8), g2 = Ext4(c2 >> 4 & 15), b2 = Ext4(c2 & 15);
            SetPaint(p, 0, r1 + dist, g1 + dist, b1 + dist);
            SetPaint(p, 1, r1 - dist, g1 - dist, b1 - dist);
            if (transparent) NeverPaint(p, 2); else SetPaint(p, 2, r2 + dist, g2 + dist, b2 + dist);
            SetPaint(p, 3, r2 - dist, g2 - dist, b2 - dist);
        }

        /// <summary>Error of a T (h = false) or H (h = true) mode candidate; int.MaxValue for an impossible one.</summary>
        static int ThError(Ctx c, bool h, int c1, int c2, int d, int bound, byte[]? sel)
        {
            if (h)
            {
                if (!HValid(c1, c2, d, c.Transparent)) return int.MaxValue;
                HPaints(c.Paint, c1, c2, d, c.Transparent);
            }
            else TPaints(c.Paint, c1, c2, d, c.Transparent);
            return sel == null ? PaintErrorV(c, 0, 16, c.Paint) : SelectV(c, 0, 16, c.Paint);
        }

        /// <summary>
        /// Best distance of a T (h = false) or H (h = true) candidate and its exact error (int.MaxValue if none is possible
        /// or below <paramref name="bound"/>). The paint colours are base colours moved along the grey axis, so the offset
        /// model of <see cref="BestTable"/> gives every distance's error from a few sums per texel; distances whose paints
        /// clamp are evaluated exactly.
        /// </summary>
        static int ThBest(Ctx c, bool h, int c1, int c2, int bound, out int bestD)
        {
            int w = c.Wr + c.Wg + c.Wb;
            bool tr = c.Transparent;
            int r1 = Ext4(c1 >> 8), g1 = Ext4(c1 >> 4 & 15), b1 = Ext4(c1 & 15);
            int r2 = Ext4(c2 >> 8), g2 = Ext4(c2 >> 4 & 15), b2 = Ext4(c2 & 15);
            Span<Vector128<int>> e1 = stackalloc Vector128<int>[4], d1 = stackalloc Vector128<int>[4];
            Span<Vector128<int>> e2 = stackalloc Vector128<int>[4], d2 = stackalloc Vector128<int>[4];
            Terms(c, 0, 16, r1, g1, b1, e1, d1);
            Terms(c, 0, 16, r2, g2, b2, e2, d2);
            int lo1 = Math.Min(r1, Math.Min(g1, b1)), hi1 = Math.Max(r1, Math.Max(g1, b1));
            int lo2 = Math.Min(r2, Math.Min(g2, b2)), hi2 = Math.Max(r2, Math.Max(g2, b2));
            int best = bound;
            bestD = -1;
            for (int d = 0; d < 8; d++)
            {
                int dist = Distance[d], e;
                if (h && !HValid(c1, c2, d, tr)) continue;
                bool clamps = lo2 - dist < 0 || hi2 + dist > 255 || h && (lo1 - dist < 0 || hi1 + dist > 255);
                {
                    // masked texels: E and D are 0, so every option below is k >= 0 or 0: mask the sum
                    var k = Vector128.Create(w * dist * dist); var d2v = Vector128.Create(2 * dist);
                    var acc = Vector128<int>.Zero;
                    for (int i = 0; i < 4; i++)
                    {
                        Vector128<int> x, y;
                        if (h)
                        {
                            x = e1[i] + k - d2v * Vector128.Abs(d1[i]);                                         // c1 +- d
                            y = tr ? e2[i] + k + d2v * d2[i] : e2[i] + k - d2v * Vector128.Abs(d2[i]);           // c2 (-) d
                        }
                        else
                        {
                            x = e1[i];                                                                          // c1
                            y = e2[i] + k - d2v * Vector128.Abs(d2[i]);                                         // c2 +- d
                            if (!tr) y = Vector128.Min(y, e2[i]);                                               // c2
                        }
                        acc += Vector128.Min(x, y) & Load(c.Ms, i * 4);
                    }
                    e = Vector128.Sum(acc);
                }
                if (clamps && Recheck(c, e, best))
                {
                    if (h) HPaints(c.Paint, c1, c2, d, tr); else TPaints(c.Paint, c1, c2, d, tr);
                    e = PaintErrorV(c, 0, 16, c.Paint);
                }
                if (e < best) { best = e; bestD = d; }
            }
            return bestD < 0 ? int.MaxValue : best;
        }

        struct ThCand
        {
            public int C1, C2, D, Err;
        }

        static void Keep(Span<ThCand> keep, ThCand x)
        {
            int worst = 0;
            for (int k = 1; k < keep.Length; k++) if (keep[k].Err > keep[worst].Err) worst = k;
            if (x.Err < keep[worst].Err) keep[worst] = x;
        }

        static int Q4(float r, float g, float b) => Pack4(Quant(r, 4), Quant(g, 4), Quant(b, 4));

        static void TAndH(Ctx c, BcQuality q, uint bit33)
        {
            EnsurePx(c);
            var px = c.Px;
            Span<int> tex = stackalloc int[16];
            int n = 0;
            for (int t = 0; t < 16; t++) if ((c.Opaque >> t & 1) != 0) tex[n++] = t;
            if (n < 2) return;

            // texels ordered along the principal axis; two-cluster splits of that order, ranked by their squared error
            Span<float> pts = stackalloc float[48];
            for (int i = 0; i < n; i++) { pts[i * 3] = px[tex[i] * 3]; pts[i * 3 + 1] = px[tex[i] * 3 + 1]; pts[i * 3 + 2] = px[tex[i] * 3 + 2]; }
            Span<float> mean = stackalloc float[3], axis = stackalloc float[3];
            BcEncoder.PrincipalAxis(pts.Slice(0, n * 3), n, 3, mean, axis);
            Span<float> proj = stackalloc float[16];
            Span<int> order = stackalloc int[16];
            for (int i = 0; i < n; i++)
            {
                proj[i] = (pts[i * 3] - mean[0]) * axis[0] + (pts[i * 3 + 1] - mean[1]) * axis[1] + (pts[i * 3 + 2] - mean[2]) * axis[2];
                order[i] = i;
            }
            for (int i = 1; i < n; i++)
            {
                int k = order[i]; float v = proj[k]; int j = i - 1;
                while (j >= 0 && proj[order[j]] > v) { order[j + 1] = order[j]; j--; }
                order[j + 1] = k;
            }
            Span<float> pre = stackalloc float[17 * 3];
            pre.Slice(0, 3).Clear();
            for (int i = 0; i < n; i++)
                for (int ch = 0; ch < 3; ch++) pre[(i + 1) * 3 + ch] = pre[i * 3 + ch] + pts[order[i] * 3 + ch];

            int nSplits = Math.Min(n - 1, Efforts[(int)q].Splits);
            Span<int> splits = stackalloc int[15];
            Span<float> splitCost = stackalloc float[15];
            int ns = 0;
            for (int s = 1; s < n; s++)
            {
                // sum |p|^2 is common: maximize |S_a|^2 / n_a + |S_b|^2 / n_b
                float ea = 0, eb = 0;
                for (int ch = 0; ch < 3; ch++)
                {
                    float sa = pre[s * 3 + ch], sb = pre[n * 3 + ch] - sa;
                    ea += sa * sa; eb += sb * sb;
                }
                float cost = -(ea / s + eb / (n - s));
                if (ns < nSplits) { splits[ns] = s; splitCost[ns] = cost; ns++; }
                else
                {
                    int worst = 0;
                    for (int k = 1; k < ns; k++) if (splitCost[k] > splitCost[worst]) worst = k;
                    if (cost < splitCost[worst]) { splits[worst] = s; splitCost[worst] = cost; }
                }
            }

            int keepN = Efforts[(int)q].Keep;
            Span<ThCand> keep = stackalloc ThCand[4];
            keep = keep.Slice(0, keepN);
            for (int mode = 0; mode < 2; mode++)
            {
                bool h = mode == 1;
                for (int k = 0; k < keepN; k++) keep[k].Err = int.MaxValue;
                for (int si = 0; si < ns; si++)
                {
                    int s = splits[si];
                    float ia = 1f / s, ib = 1f / (n - s);
                    int ca = Q4(pre[s * 3] * ia, pre[s * 3 + 1] * ia, pre[s * 3 + 2] * ia);
                    int cb = Q4((pre[n * 3] - pre[s * 3]) * ib, (pre[n * 3 + 1] - pre[s * 3 + 1]) * ib, (pre[n * 3 + 2] - pre[s * 3 + 2]) * ib);
                    for (int role = 0; role < 2; role++)
                    {
                        // T: either cluster can be the single colour; H: the order matters only with transparent texels
                        if (h && role == 1 && !c.Transparent) break;
                        int c1 = role == 0 ? ca : cb, c2 = role == 0 ? cb : ca;
                        int worst = 0;
                        for (int k = 1; k < keepN; k++) if (keep[k].Err > keep[worst].Err) worst = k;
                        int e = ThBest(c, h, c1, c2, keep[worst].Err, out int d);
                        if (d >= 0) Keep(keep, new ThCand { C1 = c1, C2 = c2, D = d, Err = e });
                    }
                }
                for (int k = 0; k < keepN; k++)
                {
                    if (keep[k].Err == int.MaxValue) continue;
                    var r = RefineTh(c, h, keep[k], q);
                    if (r.Err < c.BestErr || !c.Exact)
                    {
                        int e = ThError(c, h, r.C1, r.C2, r.D, int.MaxValue, c.Sel);
                        if (e < c.BestErr) c.Offer(e, h ? HWord(c, r.C1, r.C2, r.D, bit33) : TWord(c, r.C1, r.C2, r.D, bit33));
                    }
                }
            }
        }

        /// <summary>Least squares refits of the two colours to the texels' choices, then (Normal, High) a greedy search of
        /// single channel steps.</summary>
        static ThCand RefineTh(Ctx c, bool h, ThCand cur, BcQuality q)
        {
            EnsurePx(c);
            var px = c.Px;
            int iterations = Efforts[(int)q].Refits;
            Span<int> s = stackalloc int[8];   // sums r, g, b, count for colour 1 and 2
            for (int it = 0; it < iterations; it++)
            {
                ThError(c, h, cur.C1, cur.C2, cur.D, int.MaxValue, c.Sel);
                int dist = Distance[cur.D];
                s.Clear();
                for (int t = 0; t < 16; t++)
                {
                    if ((c.Opaque >> t & 1) == 0) continue;
                    int k = c.Sel[t], which, off;
                    if (h) { which = k >> 1; off = (k & 1) == 0 ? dist : -dist; }
                    else { which = k == 0 ? 0 : 1; off = k == 1 ? dist : k == 3 ? -dist : 0; }
                    s[which * 4] += px[t * 3] - off; s[which * 4 + 1] += px[t * 3 + 1] - off; s[which * 4 + 2] += px[t * 3 + 2] - off;
                    s[which * 4 + 3]++;
                }
                int n1 = s[3], n2 = s[7];
                int c1 = n1 > 0 ? Q4((float)s[0] / n1, (float)s[1] / n1, (float)s[2] / n1) : cur.C1;
                int c2 = n2 > 0 ? Q4((float)s[4] / n2, (float)s[5] / n2, (float)s[6] / n2) : cur.C2;
                if (c1 == cur.C1 && c2 == cur.C2) break;
                int e = ThBest(c, h, c1, c2, cur.Err, out int d);
                if (d < 0) break;
                cur = new ThCand { C1 = c1, C2 = c2, D = d, Err = e };
            }
            int rounds = Efforts[(int)q].Rounds;
            for (int round = 0; round < rounds; round++)
            {
                var start = cur;
                for (int comp = 0; comp < 6; comp++)
                    for (int dlt = -1; dlt <= 1; dlt += 2)
                    {
                        int which = comp / 3, shift = 8 - 4 * (comp % 3);
                        int col = which == 0 ? start.C1 : start.C2;
                        int v = (col >> shift & 15) + dlt;
                        if ((uint)v > 15) continue;
                        int nc = col & ~(15 << shift) | v << shift;
                        int c1 = which == 0 ? nc : start.C1, c2 = which == 0 ? start.C2 : nc;
                        int e = ThBest(c, h, c1, c2, cur.Err, out int d);
                        if (d >= 0) cur = new ThCand { C1 = c1, C2 = c2, D = d, Err = e };
                    }
                if (cur.Err == start.Err) break;
            }
            return cur;
        }

        // bits of hi the T, H and planar layouts leave free
        static readonly int[] TFree = { 31, 30, 29, 26 }, HFree = { 31, 23, 22, 21, 18 }, PlanarFree = { 31, 23, 15, 14, 13, 10 };

        /// <summary>The 'invalid combination' bits: the free bits of hi (in <paramref name="free"/>) are set so that the
        /// differential colour overflows in red (T), green (H) or blue (planar), as the decoder detects the modes.</summary>
        static uint FixFree(uint hi, int[] free, int mode)
        {
            // the channels are independent: red decides T (overflow), green H, blue planar; the channels before the
            // mode's must not overflow. The settings of each channel's free bits come from a table per mode and channel.
            var t = FreeTables[mode - 1];
            for (int ch = 0; ch < mode; ch++)
            {
                int shift = 24 - 8 * ch;
                hi = hi & ~(0xFFu << shift) | (uint)t[ch * 256 + (int)(hi >> shift & 0xFF)] << shift;
            }
            return hi;
        }

        // per mode (T, H, planar), channel and channel byte (free bits as given): the byte with the free bits set
        static readonly byte[][] FreeTables = { MakeFreeTable(TFree, 1), MakeFreeTable(HFree, 2), MakeFreeTable(PlanarFree, 3) };

        static byte[] MakeFreeTable(int[] free, int mode)
        {
            var table = new byte[mode * 256];
            for (int ch = 0; ch < mode; ch++)
            {
                int top = 31 - 8 * ch;
                bool want = ch == mode - 1;
                int fm = 0;
                foreach (int f in free) if (f <= top && f > top - 8) fm |= 1 << (f - (top - 7));
                for (int v = 0; v < 256; v++)
                {
                    int bestV = -1;
                    for (int m = 0; m < 256 && bestV < 0; m++)
                    {
                        if ((m & ~fm) != 0) continue;
                        int x = v & ~fm | m, b5 = x >> 3, d = Signed3(x & 7);
                        if (((uint)(b5 + d) > 31) == want) bestV = x;
                    }
                    table[ch * 256 + v] = (byte)(bestV < 0 ? v : bestV);
                }
            }
            return table;
        }

        /// <summary>0 differential, 1 T, 2 H, 3 planar.</summary>
        static int ModeOf(uint hi)
        {
            int r = (int)(hi >> 27) & 31, dr = Signed3((int)(hi >> 24) & 7);
            int g = (int)(hi >> 19) & 31, dg = Signed3((int)(hi >> 16) & 7);
            int b = (int)(hi >> 11) & 31, db = Signed3((int)(hi >> 8) & 7);
            if ((uint)(r + dr) > 31) return 1;
            if ((uint)(g + dg) > 31) return 2;
            if ((uint)(b + db) > 31) return 3;
            return 0;
        }

        static ulong TWord(Ctx c, int c1, int c2, int d, uint bit33)
        {
            int r1 = c1 >> 8;
            uint hi = (uint)(r1 >> 2) << 27 | (uint)(r1 & 3) << 24 | (uint)(c1 >> 4 & 15) << 20 | (uint)(c1 & 15) << 16
                      | (uint)(c2 >> 8) << 12 | (uint)(c2 >> 4 & 15) << 8 | (uint)(c2 & 15) << 4
                      | (uint)(d >> 1) << 2 | (uint)(d & 1) | bit33;
            hi = FixFree(hi, TFree, 1);
            return (ulong)hi << 32 | Indices(c, c.Sel);
        }

        static ulong HWord(Ctx c, int c1, int c2, int d, uint bit33)
        {
            if (!c.Transparent && (c1 >= c2 ? 1 : 0) != (d & 1))
            {
                // the order selects the distance's low bit: swap the colours and their indices
                (c1, c2) = (c2, c1);
                for (int t = 0; t < 16; t++) c.Sel[t] ^= 2;
            }
            int g1 = c1 >> 4 & 15, b1 = c1 & 15;
            uint hi = (uint)(c1 >> 8) << 27 | (uint)(g1 >> 1) << 24 | (uint)(g1 & 1) << 20 | (uint)(b1 >> 3) << 19 | (uint)(b1 & 7) << 15
                      | (uint)(c2 >> 8) << 11 | (uint)(c2 >> 4 & 15) << 7 | (uint)(c2 & 15) << 3
                      | (uint)(d >> 2) << 2 | (uint)(d >> 1 & 1) | bit33;
            hi = FixFree(hi, HFree, 2);
            return (ulong)hi << 32 | Indices(c, c.Sel);
        }

        // ------------------------------------------------------------------ planar mode

        static int PlanarChannel(Span<int> v, int o, int h, int vv)
        {
            int err = 0;
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                {
                    int d = Clamp255((x * (h - o) + y * (vv - o) + 4 * o + 2) >> 2) - v[y * 4 + x];
                    err += d * d;
                }
            return err;
        }

        /// <summary>Planar mode, Fast: the least squares plane of each channel, quantized, evaluated exactly (vectors of
        /// one row).</summary>
        static void PlanarFast(Ctx c)
        {
            // per channel: sum v, sum (2x - 3) v, sum (2y - 3) v; the least squares residual of the three planes bounds what
            // the quantized plane can reach (up to the decoder's rounding): blocks it cannot win are left at once
            var w2 = Vector128.Create(-3, -1, 1, 3);
            Span<long> fit = stackalloc long[9];
            long resid = 0;
            for (int ch = 0; ch < 3; ch++)
            {
                int[] a = ch == 0 ? c.Rs : ch == 1 ? c.Gs : c.Bs;
                Vector128<int> q0 = Load(a, 0), q1 = Load(a, 4), q2 = Load(a, 8), q3 = Load(a, 12);
                var rows = q0 + q1 + q2 + q3;
                long sv = Vector128.Sum(rows), sx = Vector128.Sum(rows * w2), sy = 3L * Vector128.Sum(q3 - q0) + Vector128.Sum(q2 - q1);
                long s2 = Vector128.Sum(q0 * q0 + q1 * q1 + q2 * q2 + q3 * q3);
                resid += (80 * s2 - 5 * sv * sv - sx * sx - sy * sy) * (ch == 0 ? c.Wr : ch == 1 ? c.Wg : c.Wb);
                fit[ch * 3] = sv; fit[ch * 3 + 1] = sx; fit[ch * 3 + 2] = sy;
            }
            if (resid >= 80L * c.BestErr) return;
            Span<int> ohv = stackalloc int[9];
            int total = 0;
            var x4 = Vector128.Create(0, 1, 2, 3);
            Vector128<int> zero = Vector128<int>.Zero, max = Vector128.Create(255);
            for (int ch = 0; ch < 3; ch++)
            {
                // plane o + x bx + y by (x, y centred): bx = sum (x - 1.5) v / 20 = sx / 40
                float bx = fit[ch * 3 + 1] / 40f, by = fit[ch * 3 + 2] / 40f, o = fit[ch * 3] / 16f - 1.5f * (bx + by);
                int bits = ch == 1 ? 7 : 6;
                int qo = Quant(o, bits), qh = Quant(o + 4 * bx, bits), qv = Quant(o + 4 * by, bits);
                int eo = Ext(qo, bits), eh = Ext(qh, bits), ev = Ext(qv, bits);
                // row y: (x (h - o) + y (v - o) + 4 o + 2) >> 2
                int[] a = ch == 0 ? c.Rs : ch == 1 ? c.Gs : c.Bs;
                var row = Vector128.Create(4 * eo + 2) + x4 * Vector128.Create(eh - eo);
                var step = Vector128.Create(ev - eo);
                var err = Vector128<int>.Zero;
                for (int y = 0; y < 4; y++)
                {
                    var d = Vector128.Min(Vector128.Max(row >> 2, zero), max) - Load(a, y * 4);
                    err += d * d;
                    row += step;
                }
                ohv[ch * 3] = qo; ohv[ch * 3 + 1] = qh; ohv[ch * 3 + 2] = qv;
                total += Vector128.Sum(err) * (ch == 0 ? c.Wr : ch == 1 ? c.Wg : c.Wb);
                if (total >= c.BestErr) return;
            }
            c.Offer(total, PlanarWord(ohv));
        }

        static void Planar(Ctx c, BcQuality q)
        {
            if (q == BcQuality.Fast)
            {
                PlanarFast(c);
                return;
            }
            EnsurePx(c);
            Span<int> v = stackalloc int[16];
            Span<int> ohv = stackalloc int[9];
            int total = 0;
            for (int ch = 0; ch < 3; ch++)
            {
                float sum = 0, sx = 0, sy = 0;
                for (int i = 0; i < 16; i++)
                {
                    v[i] = c.Px[i * 3 + ch];
                    sum += v[i]; sx += ((i & 3) - 1.5f) * v[i]; sy += ((i >> 2) - 1.5f) * v[i];
                }
                float bx = sx / 20, by = sy / 20, a = sum / 16 - 1.5f * (bx + by);
                int bits = ch == 1 ? 7 : 6, max = (1 << bits) - 1;
                int qo = Quant(a, bits), qh = Quant(a + 4 * bx, bits), qv = Quant(a + 4 * by, bits);
                int best = PlanarChannel(v, Ext(qo, bits), Ext(qh, bits), Ext(qv, bits));
                if (q >= BcQuality.Normal && best > 0)
                {
                    int radius = q == BcQuality.High ? 2 : 1;
                    for (int round = 0; round < 4; round++)
                    {
                        int so = qo, sh = qh, sv = qv, before = best;
                        for (int dO = -radius; dO <= radius; dO++)
                            for (int dH = -radius; dH <= radius; dH++)
                                for (int dV = -radius; dV <= radius; dV++)
                                {
                                    int xo = so + dO, xh = sh + dH, xv = sv + dV;
                                    if ((uint)xo > (uint)max || (uint)xh > (uint)max || (uint)xv > (uint)max) continue;
                                    int e = PlanarChannel(v, Ext(xo, bits), Ext(xh, bits), Ext(xv, bits));
                                    if (e < best) { best = e; qo = xo; qh = xh; qv = xv; }
                                }
                        if (best == before || best == 0) break;
                    }
                }
                ohv[ch * 3] = qo; ohv[ch * 3 + 1] = qh; ohv[ch * 3 + 2] = qv;
                total += best * (ch == 0 ? c.Wr : ch == 1 ? c.Wg : c.Wb);
                if (total >= c.BestErr) return;
            }
            c.Offer(total, PlanarWord(ohv));
        }

        /// <summary>ohv: (O, H, V) for red, green, blue; 6, 7, 6 bits.</summary>
        static ulong PlanarWord(ReadOnlySpan<int> ohv)
        {
            ulong ro = (ulong)ohv[0], rh = (ulong)ohv[1], rv = (ulong)ohv[2];
            ulong go = (ulong)ohv[3], gh = (ulong)ohv[4], gv = (ulong)ohv[5];
            ulong bo = (ulong)ohv[6], bh = (ulong)ohv[7], bv = (ulong)ohv[8];
            ulong v = ro << 57 | (go >> 6) << 56 | (go & 63) << 49 | (bo >> 5) << 48 | (bo >> 3 & 3) << 43 | (bo & 7) << 39
                      | (rh >> 1) << 34 | (rh & 1) << 32 | gh << 25 | bh << 19 | rv << 13 | gv << 6 | bv | 1UL << 33;
            uint hi = FixFree((uint)(v >> 32), PlanarFree, 3);
            return (ulong)hi << 32 | (uint)v;
        }

        // ------------------------------------------------------------------ single colour blocks

        /// <summary>
        /// Blocks of one colour: every mode's way of producing a constant (a base colour plus one modifier, a T or H paint
        /// colour, a constant plane) is searched exhaustively, channel by channel, for the closest colour.
        /// </summary>
        static void Solid(Ctx c, ColorFormat fmt, uint bit33)
        {
            EnsurePx(c);
            int first = 0;
            while ((c.Opaque >> first & 1) == 0) first++;
            int vr = c.Px[first * 3], vg = c.Px[first * 3 + 1], vb = c.Px[first * 3 + 2];
            bool tr = c.Transparent;
            var sel = c.Sel;

            // individual / differential: both halves the same base colour and table, every texel the same index
            for (int bits = 5; bits >= 4; bits--)
            {
                if (bits == 4 && fmt == ColorFormat.PunchThrough) continue;
                for (int t = 0; t < 8; t++)
                    for (int k = 0; k < 4; k++)
                    {
                        if (tr && k == 2) continue;
                        int m = Modifier(t, k, tr);
                        int e = SolidFit(c, vr, vg, vb, bits, m, out int qr, out int qg, out int qb);
                        if (e >= c.BestErr) continue;
                        Array.Fill(sel, (byte)k);
                        int qc = Pack(qr, qg, qb);
                        ulong w = SubHeader(bits == 5, qc, qc, t, t, bit33);
                        c.Offer(e, w | Indices(c, sel));
                    }
            }
            if (fmt == ColorFormat.Etc1 || c.BestErr == 0) return;

            if (!tr)
            {
                Span<int> ohv = stackalloc int[9];
                // a constant plane: 6, 7, 6 bits
                SolidChannel(vr, 6, 0, out int qr, out int er);
                SolidChannel(vg, 7, 0, out int qg7, out int eg);
                SolidChannel(vb, 6, 0, out int qb, out int eb);
                int e = 16 * (c.Wr * er + c.Wg * eg + c.Wb * eb);
                if (e < c.BestErr)
                {
                    ohv[0] = ohv[1] = ohv[2] = qr; ohv[3] = ohv[4] = ohv[5] = qg7; ohv[6] = ohv[7] = ohv[8] = qb;
                    c.Offer(e, PlanarWord(ohv));
                }
            }
            for (int d = 0; d < 8 && c.BestErr > 0; d++)
            {
                int dist = Distance[d];
                // T: paint 0 (c1), paint 1 / 3 (c2 +- d)
                for (int k = 0; k < 4; k++)
                {
                    if (k == 2) continue;
                    int m = k == 0 ? 0 : k == 1 ? dist : -dist;
                    int e = SolidFit(c, vr, vg, vb, 4, m, out int qr, out int qg, out int qb);
                    if (e >= c.BestErr) continue;
                    int col = Pack4(qr, qg, qb);
                    Array.Fill(sel, (byte)k);
                    c.Offer(e, TWord(c, k == 0 ? col : 0, k == 0 ? 0 : col, d, bit33));
                }
                // H: paint 0 / 1 (c1 +- d), the other colour chosen so that the order gives the distance's low bit
                for (int k = 0; k < 2; k++)
                {
                    int e = SolidFit(c, vr, vg, vb, 4, k == 0 ? dist : -dist, out int qr, out int qg, out int qb);
                    if (e >= c.BestErr) continue;
                    int c1 = Pack4(qr, qg, qb), c2 = (d & 1) == 1 ? 0 : 0xFFF;
                    if (!HValid(c1, c2, d, true)) continue;
                    Array.Fill(sel, (byte)k);
                    c.Offer(e, HWord(c, c1, c2, d, bit33));
                }
            }
        }

        /// <summary>Best value of one channel: clamp(Ext(q) + m) closest to v.</summary>
        static void SolidChannel(int v, int bits, int m, out int q, out int err)
        {
            int max = (1 << bits) - 1;
            q = 0; err = int.MaxValue;
            for (int x = 0; x <= max; x++)
            {
                int d = Clamp255(Ext(x, bits) + m) - v;
                if (d * d < err) { err = d * d; q = x; }
            }
        }

        static int SolidFit(Ctx c, int vr, int vg, int vb, int bits, int m, out int qr, out int qg, out int qb)
        {
            SolidChannel(vr, bits, m, out qr, out int er);
            SolidChannel(vg, bits, m, out qg, out int eg);
            SolidChannel(vb, bits, m, out qb, out int eb);
            return (c.Wr * er + c.Wg * eg + c.Wb * eb) * BitCount(c.Opaque);
        }

        static int BitCount(int v) => System.Numerics.BitOperations.PopCount((uint)v);

        static ulong SubHeader(bool diff, int qa, int qb, int ta, int tb, uint bit33)
        {
            uint hi;
            if (diff)
            {
                int dr = (qb >> 10) - (qa >> 10), dg = (qb >> 5 & 31) - (qa >> 5 & 31), db = (qb & 31) - (qa & 31);
                hi = (uint)(qa >> 10) << 27 | (uint)(dr & 7) << 24 | (uint)(qa >> 5 & 31) << 19 | (uint)(dg & 7) << 16
                     | (uint)(qa & 31) << 11 | (uint)(db & 7) << 8 | bit33;
            }
            else
            {
                hi = (uint)(qa >> 10) << 28 | (uint)(qb >> 10) << 24 | (uint)(qa >> 5 & 15) << 20 | (uint)(qb >> 5 & 15) << 16
                     | (uint)(qa & 15) << 12 | (uint)(qb & 15) << 8;
            }
            hi |= (uint)ta << 5 | (uint)tb << 2;
            return (ulong)hi << 32;
        }

        // ------------------------------------------------------------------ EAC

        enum EacKind { Alpha8, Unsigned11, Signed11 }

        static void WriteEac(Span<byte> o, ulong v) => BinaryPrimitives.WriteUInt64BigEndian(o, v);

        // modifier k of each table in ascending order: the four negative ones are stored largest first
        static readonly int[] EacOrder = { 3, 2, 1, 0, 4, 5, 6, 7 };

        // the modifiers of each table in that order
        static readonly int[] EacSorted = MakeSorted();
        static readonly float[] EacSortedF = Array.ConvertAll(EacSorted, x => (float)x);

        static int[] MakeSorted()
        {
            var m = new int[128];
            for (int t = 0; t < 16; t++)
                for (int j = 0; j < 8; j++) m[t * 8 + j] = EacMod[t * 8 + EacOrder[j]];
            return m;
        }

        /// <summary>The 8 decoded values of (base, multiplier, table) in ascending order (<see cref="EacOrder"/>), in the
        /// target's domain (8 bit alpha, UNORM16 or SNORM16 as <see cref="EtcBlocks"/> extends the 11 bit values).</summary>
        static void EacPalette(EacKind kind, int b, int mul, int tab, Span<int> pal)
        {
            ReadOnlySpan<int> m = EacSorted.AsSpan(tab * 8, 8);
            switch (kind)
            {
                case EacKind.Alpha8:
                    for (int j = 0; j < 8; j++) pal[j] = Clamp255(b + m[j] * mul);
                    break;
                case EacKind.Unsigned11:
                    {
                        int s = mul != 0 ? mul * 8 : 1, x0 = b * 8 + 4;
                        for (int j = 0; j < 8; j++)
                        {
                            int x = Math.Clamp(x0 + m[j] * s, 0, 2047);
                            pal[j] = x << 5 | x >> 6;
                        }
                        break;
                    }
                default:
                    {
                        int s = mul != 0 ? mul * 8 : 1, x0 = b * 8;
                        for (int j = 0; j < 8; j++)
                        {
                            int x = Math.Clamp(x0 + m[j] * s, -1023, 1023), a = Math.Abs(x);
                            pal[j] = Math.Sign(x) * (a << 5 | a >> 5);
                        }
                        break;
                    }
            }
        }

        /// <summary>Squared error of the 16 values against an ascending palette (nearest entry by binary search over the
        /// midpoints); stops at <paramref name="bound"/>. <paramref name="sel"/>: the chosen positions in the palette.</summary>
        static long EacError(ReadOnlySpan<int> t, ReadOnlySpan<int> pal, long bound, Span<byte> sel)
        {
            int m1 = pal[0] + pal[1], m2 = pal[1] + pal[2], m3 = pal[2] + pal[3], m4 = pal[3] + pal[4];
            int m5 = pal[4] + pal[5], m6 = pal[5] + pal[6], m7 = pal[6] + pal[7];
            long err = 0;
            for (int i = 0; i < 16; i++)
            {
                int v = t[i], v2 = v * 2, j;
                if (v2 > m4) j = v2 > m6 ? (v2 > m7 ? 7 : 6) : (v2 > m5 ? 5 : 4);
                else j = v2 > m2 ? (v2 > m3 ? 3 : 2) : (v2 > m1 ? 1 : 0);
                long d = pal[j] - v;
                err += d * d;
                if (!sel.IsEmpty) sel[i] = (byte)j;
                else if (err >= bound) return err;
            }
            return err;
        }

        /// <summary>
        /// Squared error of the 16 values (as 4 vectors) against a palette, each value taking its nearest entry, and the sum
        /// of the chosen entries' modifiers (<paramref name="mods"/>, in palette order) for the least squares base. Single
        /// precision: the values and entries are integers below 2^17, so only the squares round (relative 2^-24).
        /// </summary>
        static float EacErrorV(ReadOnlySpan<Vector128<float>> tv, ReadOnlySpan<int> pal, ReadOnlySpan<float> mods, out float msum)
        {
            var p0 = Vector128.Create((float)pal[0]); var p1 = Vector128.Create((float)pal[1]);
            var p2 = Vector128.Create((float)pal[2]); var p3 = Vector128.Create((float)pal[3]);
            var p4 = Vector128.Create((float)pal[4]); var p5 = Vector128.Create((float)pal[5]);
            var p6 = Vector128.Create((float)pal[6]); var p7 = Vector128.Create((float)pal[7]);
            var acc = Vector128<float>.Zero; var ms = Vector128<float>.Zero;
            for (int i = 0; i < 4; i++)
            {
                var v = tv[i];
                var d = v - p0; var best = d * d; var m = Vector128.Create(mods[0]);
                d = v - p1; var e = d * d; var lt = Vector128.LessThan(e, best); best = Vector128.Min(best, e); m = Vector128.ConditionalSelect(lt, Vector128.Create(mods[1]), m);
                d = v - p2; e = d * d; lt = Vector128.LessThan(e, best); best = Vector128.Min(best, e); m = Vector128.ConditionalSelect(lt, Vector128.Create(mods[2]), m);
                d = v - p3; e = d * d; lt = Vector128.LessThan(e, best); best = Vector128.Min(best, e); m = Vector128.ConditionalSelect(lt, Vector128.Create(mods[3]), m);
                d = v - p4; e = d * d; lt = Vector128.LessThan(e, best); best = Vector128.Min(best, e); m = Vector128.ConditionalSelect(lt, Vector128.Create(mods[4]), m);
                d = v - p5; e = d * d; lt = Vector128.LessThan(e, best); best = Vector128.Min(best, e); m = Vector128.ConditionalSelect(lt, Vector128.Create(mods[5]), m);
                d = v - p6; e = d * d; lt = Vector128.LessThan(e, best); best = Vector128.Min(best, e); m = Vector128.ConditionalSelect(lt, Vector128.Create(mods[6]), m);
                d = v - p7; e = d * d; lt = Vector128.LessThan(e, best); best = Vector128.Min(best, e); m = Vector128.ConditionalSelect(lt, Vector128.Create(mods[7]), m);
                acc += best; ms += m;
            }
            msum = Vector128.Sum(ms);
            return Vector128.Sum(acc);
        }

        /// <summary><see cref="EacPalette"/> as one vector of 8 floats (AVX2).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector256<float> EacPaletteV(EacKind kind, int b, int mul, int tab)
        {
            var m = Vector256.LoadUnsafe(ref EacSorted[tab * 8]);
            Vector256<int> x;
            switch (kind)
            {
                case EacKind.Alpha8:
                    x = Vector256.Min(Vector256.Max(Vector256.Create(b) + m * Vector256.Create(mul), Vector256<int>.Zero), Vector256.Create(255));
                    break;
                case EacKind.Unsigned11:
                    x = Vector256.Min(Vector256.Max(Vector256.Create(b * 8 + 4) + m * Vector256.Create(mul != 0 ? mul * 8 : 1), Vector256<int>.Zero), Vector256.Create(2047));
                    x = x << 5 | x >>> 6;
                    break;
                default:
                    {
                        x = Vector256.Min(Vector256.Max(Vector256.Create(b * 8) + m * Vector256.Create(mul != 0 ? mul * 8 : 1), Vector256.Create(-1023)), Vector256.Create(1023));
                        var sign = x >> 31; var a = Vector256.Abs(x);
                        x = ((a << 5 | a >> 5) ^ sign) - sign;
                        break;
                    }
            }
            return Vector256.ConvertToSingle(x);
        }

        static readonly Vector256<int>[] Lane = { Vector256.Create(0), Vector256.Create(1), Vector256.Create(2), Vector256.Create(3), Vector256.Create(4), Vector256.Create(5), Vector256.Create(6), Vector256.Create(7) };

        /// <summary><see cref="EacErrorV256"/> with the palette in a vector (its entries broadcast by lane permutes).</summary>
        static float EacErrorP(Vector256<float> t0, Vector256<float> t1, Vector256<float> pal, ReadOnlySpan<float> mods, out float msum)
        {
            var p = Avx2.PermuteVar8x32(pal, Lane[0]);
            var d0 = t0 - p; var d1 = t1 - p;
            var b0 = d0 * d0; var b1 = d1 * d1;
            var m = Vector256.Create(mods[0]);
            var ms0 = m; var ms1 = m;
            for (int j = 1; j < 8; j++)
            {
                p = Avx2.PermuteVar8x32(pal, Lane[j]); m = Vector256.Create(mods[j]);
                d0 = t0 - p; d1 = t1 - p;
                var e0 = d0 * d0; var e1 = d1 * d1;
                var l0 = Vector256.LessThan(e0, b0); var l1 = Vector256.LessThan(e1, b1);
                b0 = Vector256.Min(b0, e0); b1 = Vector256.Min(b1, e1);
                ms0 = Vector256.ConditionalSelect(l0, m, ms0); ms1 = Vector256.ConditionalSelect(l1, m, ms1);
            }
            msum = Vector256.Sum(ms0 + ms1);
            return Vector256.Sum(b0 + b1);
        }

        /// <summary>The 256 bit form of <see cref="EacErrorV"/> (values 0..7 and 8..15 in two vectors).</summary>
        static float EacErrorV256(Vector256<float> t0, Vector256<float> t1, ReadOnlySpan<int> pal, ReadOnlySpan<float> mods, out float msum)
        {
            var p = Vector256.Create((float)pal[0]);
            var d0 = t0 - p; var d1 = t1 - p;
            var b0 = d0 * d0; var b1 = d1 * d1;
            var m = Vector256.Create(mods[0]);
            var ms0 = m; var ms1 = m;
            for (int j = 1; j < 8; j++)
            {
                p = Vector256.Create((float)pal[j]); m = Vector256.Create(mods[j]);
                d0 = t0 - p; d1 = t1 - p;
                var e0 = d0 * d0; var e1 = d1 * d1;
                var l0 = Vector256.LessThan(e0, b0); var l1 = Vector256.LessThan(e1, b1);
                b0 = Vector256.Min(b0, e0); b1 = Vector256.Min(b1, e1);
                ms0 = Vector256.ConditionalSelect(l0, m, ms0); ms1 = Vector256.ConditionalSelect(l1, m, ms1);
            }
            msum = Vector256.Sum(ms0 + ms1);
            return Vector256.Sum(b0 + b1);
        }

        /// <summary>
        /// EAC block search. A first pass takes, for every table, the multipliers either side of the one whose palette
        /// spans the values, the base centring it on them and that base's least squares refit to the chosen modifiers
        /// (Fast stops there). Normal then scans the bases around the centre for one more multiplier below, High for two
        /// more either side and as far as half the values' range.
        /// </summary>
        static ulong EacSearch(ReadOnlySpan<int> t, EacKind kind, BcQuality q)
        {
            if (Avx2.IsSupported) return EacSearchWide(t, kind, q);
            // the 'x' domain of the specification: alpha 0..255, 11 bit 0..2047 / -1023..1023; x = c0 + unit * base + m * step
            double scale = kind == EacKind.Alpha8 ? 1 : kind == EacKind.Unsigned11 ? 2047.0 / 65535 : 1023.0 / 32767;
            double c0 = kind == EacKind.Unsigned11 ? 4 : 0, unit = kind == EacKind.Alpha8 ? 1 : 8;
            int baseLo = kind == EacKind.Signed11 ? -127 : 0, baseHi = kind == EacKind.Signed11 ? 127 : 255;
            bool small = kind != EacKind.Alpha8;   // 11 bit: multiplier 0 means step 1
            int tmin = int.MaxValue, tmax = int.MinValue;
            long tsum = 0;
            for (int i = 0; i < 16; i++)
            {
                tmin = Math.Min(tmin, t[i]); tmax = Math.Max(tmax, t[i]);
                tsum += t[i];
            }
            double xmin = tmin * scale, xmax = tmax * scale, xsum = tsum * scale;
            Span<int> pal = stackalloc int[8];
            Span<byte> sel = stackalloc byte[16];
            Span<Vector128<float>> tv = stackalloc Vector128<float>[4];
            for (int i = 0; i < 4; i++) tv[i] = Vector128.Create((float)t[i * 4], t[i * 4 + 1], t[i * 4 + 2], t[i * 4 + 3]);
            bool wide = Vector256.IsHardwareAccelerated;
            var t0 = Vector256.Create(tv[0], tv[1]); var t1 = Vector256.Create(tv[2], tv[3]);
            float bestErr = float.MaxValue;
            int bb = 0, bm = 0, bt = 0;

            if (tmin == tmax && kind == EacKind.Alpha8)
            {
                bb = tmin;   // multiplier 0: every entry is the base
                goto done;
            }

            for (int pass = 0; pass < (q == BcQuality.Fast ? 1 : 2); pass++)
                for (int tab = 0; tab < 16; tab++)
                {
                    int mmin = EacMod[tab * 8 + 3], mmax = EacMod[tab * 8 + 7];
                    ReadOnlySpan<float> mods = EacSortedF.AsSpan(tab * 8, 8);
                    double ideal = (xmax - xmin) / ((mmax - mmin) * unit);
                    int lo, hi;
                    if (pass == 0) { lo = (int)Math.Floor(ideal); hi = (int)Math.Ceiling(ideal); }
                    else if (q == BcQuality.Normal) { lo = (int)Math.Floor(ideal) - 1; hi = (int)Math.Ceiling(ideal); }
                    else { lo = (int)Math.Floor(ideal) - 2; hi = (int)Math.Ceiling(ideal) + 2; }
                    lo = Math.Clamp(lo, small ? 0 : 1, 15); hi = Math.Clamp(hi, lo, 15);
                    for (int mul = lo; mul <= hi; mul++)
                    {
                        double step = small && mul == 0 ? 1 : mul * unit;
                        int b0 = Math.Clamp((int)Math.Round(((xmin + xmax) / 2 - (mmin + mmax) / 2.0 * step - c0) / unit), baseLo, baseHi);
                        if (pass == 0)
                        {
                            // the centred base, then the least squares base for its choices
                            EacPalette(kind, b0, mul, tab, pal);
                            float e = (wide ? EacErrorV256(t0, t1, pal, mods, out float sm) : EacErrorV(tv, pal, mods, out sm));
                            if (e < bestErr) { bestErr = e; bb = b0; bm = mul; bt = tab; if (e == 0) goto done; }
                            int b1 = Math.Clamp((int)Math.Round(((xsum - sm * step) / 16 - c0) / unit), baseLo, baseHi);
                            if (b1 == b0) continue;
                            EacPalette(kind, b1, mul, tab, pal);
                            e = (wide ? EacErrorV256(t0, t1, pal, mods, out _) : EacErrorV(tv, pal, mods, out _));
                            if (e < bestErr) { bestErr = e; bb = b1; bm = mul; bt = tab; if (e == 0) goto done; }
                            continue;
                        }
                        int reach = q == BcQuality.High ? Math.Max(mul + 2, (int)Math.Ceiling((xmax - xmin) / unit / 2)) : mul + 2;
                        for (int b = Math.Max(baseLo, b0 - reach); b <= Math.Min(baseHi, b0 + reach); b++)
                        {
                            EacPalette(kind, b, mul, tab, pal);
                            float e = (wide ? EacErrorV256(t0, t1, pal, mods, out _) : EacErrorV(tv, pal, mods, out _));
                            if (e < bestErr) { bestErr = e; bb = b; bm = mul; bt = tab; if (e == 0) goto done; }
                        }
                    }
                }
        done:
            EacPalette(kind, bb, bm, bt, pal);
            EacError(t, pal, long.MaxValue, sel);
            ulong v = (ulong)(bb & 255) << 56 | (ulong)bm << 52 | (ulong)bt << 48;   // signed base: two's complement
            for (int i = 0; i < 16; i++)
            {
                int k = (i & 3) * 4 + (i >> 2);
                v |= (ulong)EacOrder[sel[i]] << (45 - 3 * k);
            }
            return v;
        }

        /// <summary><see cref="EacSearch"/> with vector palettes (AVX2).</summary>
        static ulong EacSearchWide(ReadOnlySpan<int> t, EacKind kind, BcQuality q)
        {
            // the 'x' domain of the specification: alpha 0..255, 11 bit 0..2047 / -1023..1023; x = c0 + unit * base + m * step
            double scale = kind == EacKind.Alpha8 ? 1 : kind == EacKind.Unsigned11 ? 2047.0 / 65535 : 1023.0 / 32767;
            double c0 = kind == EacKind.Unsigned11 ? 4 : 0, unit = kind == EacKind.Alpha8 ? 1 : 8;
            int baseLo = kind == EacKind.Signed11 ? -127 : 0, baseHi = kind == EacKind.Signed11 ? 127 : 255;
            bool small = kind != EacKind.Alpha8;   // 11 bit: multiplier 0 means step 1
            int tmin = int.MaxValue, tmax = int.MinValue;
            long tsum = 0;
            for (int i = 0; i < 16; i++)
            {
                tmin = Math.Min(tmin, t[i]); tmax = Math.Max(tmax, t[i]);
                tsum += t[i];
            }
            double xmin = tmin * scale, xmax = tmax * scale, xsum = tsum * scale;
            Span<int> pal = stackalloc int[8];
            Span<byte> sel = stackalloc byte[16];
            Span<Vector128<float>> tv = stackalloc Vector128<float>[4];
            for (int i = 0; i < 4; i++) tv[i] = Vector128.Create((float)t[i * 4], t[i * 4 + 1], t[i * 4 + 2], t[i * 4 + 3]);
            bool wide = Vector256.IsHardwareAccelerated;
            var t0 = Vector256.Create(tv[0], tv[1]); var t1 = Vector256.Create(tv[2], tv[3]);
            float bestErr = float.MaxValue;
            int bb = 0, bm = 0, bt = 0;

            if (tmin == tmax && kind == EacKind.Alpha8)
            {
                bb = tmin;   // multiplier 0: every entry is the base
                goto done;
            }

            for (int pass = 0; pass < (q == BcQuality.Fast ? 1 : 2); pass++)
                for (int tab = 0; tab < 16; tab++)
                {
                    int mmin = EacMod[tab * 8 + 3], mmax = EacMod[tab * 8 + 7];
                    ReadOnlySpan<float> mods = EacSortedF.AsSpan(tab * 8, 8);
                    double ideal = (xmax - xmin) / ((mmax - mmin) * unit);
                    int lo, hi;
                    if (pass == 0) { lo = (int)Math.Floor(ideal); hi = (int)Math.Ceiling(ideal); }
                    else if (q == BcQuality.Normal) { lo = (int)Math.Floor(ideal) - 1; hi = (int)Math.Ceiling(ideal); }
                    else { lo = (int)Math.Floor(ideal) - 2; hi = (int)Math.Ceiling(ideal) + 2; }
                    lo = Math.Clamp(lo, small ? 0 : 1, 15); hi = Math.Clamp(hi, lo, 15);
                    for (int mul = lo; mul <= hi; mul++)
                    {
                        double step = small && mul == 0 ? 1 : mul * unit;
                        int b0 = Math.Clamp((int)Math.Round(((xmin + xmax) / 2 - (mmin + mmax) / 2.0 * step - c0) / unit), baseLo, baseHi);
                        if (pass == 0)
                        {
                            // the centred base, then the least squares base for its choices
                            float e = EacErrorP(t0, t1, EacPaletteV(kind, b0, mul, tab), mods, out float sm);
                            if (e < bestErr) { bestErr = e; bb = b0; bm = mul; bt = tab; if (e == 0) goto done; }
                            int b1 = Math.Clamp((int)Math.Round(((xsum - sm * step) / 16 - c0) / unit), baseLo, baseHi);
                            if (b1 == b0) continue;
                            e = EacErrorP(t0, t1, EacPaletteV(kind, b1, mul, tab), mods, out _);
                            if (e < bestErr) { bestErr = e; bb = b1; bm = mul; bt = tab; if (e == 0) goto done; }
                            continue;
                        }
                        int reach = q == BcQuality.High ? Math.Max(mul + 2, (int)Math.Ceiling((xmax - xmin) / unit / 2)) : mul + 2;
                        for (int b = Math.Max(baseLo, b0 - reach); b <= Math.Min(baseHi, b0 + reach); b++)
                        {
                            float e = EacErrorP(t0, t1, EacPaletteV(kind, b, mul, tab), mods, out _);
                            if (e < bestErr) { bestErr = e; bb = b; bm = mul; bt = tab; if (e == 0) goto done; }
                        }
                    }
                }
        done:
            EacPalette(kind, bb, bm, bt, pal);
            EacError(t, pal, long.MaxValue, sel);
            ulong v = (ulong)(bb & 255) << 56 | (ulong)bm << 52 | (ulong)bt << 48;   // signed base: two's complement
            for (int i = 0; i < 16; i++)
            {
                int k = (i & 3) * 4 + (i >> 2);
                v |= (ulong)EacOrder[sel[i]] << (45 - 3 * k);
            }
            return v;
        }
    }
}
