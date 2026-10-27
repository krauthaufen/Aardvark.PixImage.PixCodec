using System;
using static Aardvark.ImageCodecs.JpegXl.JxlBitReader;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>Frame geometry: groups, LF groups and blocks.</summary>
    internal sealed class JxlFrameDims
    {
        public int Width, Height;          // frame size in pixels (upsampled)
        public int XSize, YSize;           // size of the colour channels as coded (before upsampling)
        public int Upsampling;
        public int GroupDim, LfGroupDim;
        public int GroupsX, GroupsY, NumGroups;
        public int LfGroupsX, LfGroupsY, NumLfGroups;
        public int XBlocks, YBlocks;       // 8x8 blocks covering XSize x YSize
        public int NumPasses;

        public static JxlFrameDims Of(JxlFrameHeader f)
        {
            int mhs = 0, mvs = 0;
            if (f.DoYCbCr)
                for (int i = 0; i < 3; i++)
                {
                    int m = f.JpegUpsampling[i];
                    mhs = Math.Max(mhs, m is 1 or 2 ? 1 : 0);
                    mvs = Math.Max(mvs, m is 1 or 3 ? 1 : 0);
                }
            var d = new JxlFrameDims { Width = f.Width, Height = f.Height, Upsampling = f.Upsampling, NumPasses = f.NumPasses };
            d.XSize = (int)(((long)f.Width + f.Upsampling - 1) / f.Upsampling);
            d.YSize = (int)(((long)f.Height + f.Upsampling - 1) / f.Upsampling);
            d.GroupDim = f.Modular ? f.GroupDim : 256;
            d.LfGroupDim = d.GroupDim * 8;
            d.GroupsX = (d.XSize + d.GroupDim - 1) / d.GroupDim;
            d.GroupsY = (d.YSize + d.GroupDim - 1) / d.GroupDim;
            d.NumGroups = d.GroupsX * d.GroupsY;
            d.LfGroupsX = (d.XSize + d.LfGroupDim - 1) / d.LfGroupDim;
            d.LfGroupsY = (d.YSize + d.LfGroupDim - 1) / d.LfGroupDim;
            d.NumLfGroups = d.LfGroupsX * d.LfGroupsY;
            d.XBlocks = ((d.XSize + (8 << mhs) - 1) / (8 << mhs)) << mhs;
            d.YBlocks = ((d.YSize + (8 << mvs) - 1) / (8 << mvs)) << mvs;
            return d;
        }
    }

    /// <summary>Table of contents of a frame: byte offset and size of each section.</summary>
    internal sealed class JxlToc
    {
        public int[] Offset = Array.Empty<int>();   // relative to the end of the TOC, logical order
        public int[] Size = Array.Empty<int>();
        public bool Single;
        public int TotalSize;

        public static int[] DecodePermutation(JxlBitReader br, JxlSymbolReader r, int size, int skip)
        {
            uint end = r.ReadUint(br, Ctx((uint)size));
            if (end > size - skip) throw new ImageFormatException("JPEG XL: bad permutation length");
            var lehmer = new int[size];
            for (int i = skip; i < skip + end; i++)
            {
                uint v = r.ReadUint(br, Ctx(i > skip ? (uint)lehmer[i - 1] : 0));
                if (v >= size - i) throw new ImageFormatException("JPEG XL: bad permutation entry");
                lehmer[i] = (int)v;
            }
            return LehmerToPermutation(lehmer);
        }

        /// <summary>
        /// The permutation of a Lehmer code (lehmer[i] &lt; length - i): entry i is the lehmer[i]-th of the values not yet
        /// taken, found in O(log n) with a Fenwick tree of the remaining values.
        /// </summary>
        internal static int[] LehmerToPermutation(int[] lehmer)
        {
            int n = lehmer.Length;
            // fen[j] (1 based) counts the remaining values in (j - lowbit(j), j]; all present at the start
            var fen = new int[n + 1];
            for (int j = 1; j <= n; j++) fen[j] = j & -j;
            int top = n == 0 ? 0 : 1 << (31 - System.Numerics.BitOperations.LeadingZeroCount((uint)n));
            var perm = new int[n];
            for (int i = 0; i < n; i++)
            {
                // the largest pos with fewer than k + 1 remaining values in [1, pos]: value pos is the k-th (0 based)
                int k = lehmer[i], pos = 0;
                for (int step = top; step > 0; step >>= 1)
                {
                    int next = pos + step;
                    if (next <= n && fen[next] <= k) { pos = next; k -= fen[next]; }
                }
                perm[i] = pos;
                for (int j = pos + 1; j <= n; j += j & -j) fen[j]--;
            }
            return perm;
        }

        static int Ctx(uint x)
        {
            int c = 0;
            while (c < 7 && (1u << c) <= x) c++;   // ceil(log2(x + 1))
            return Math.Min(7, c);
        }

        public static JxlToc Read(JxlBitReader br, JxlFrameDims d)
        {
            int n = d.NumGroups == 1 && d.NumPasses == 1 ? 1 : 1 + d.NumLfGroups + 1 + d.NumGroups * d.NumPasses;
            var t = new JxlToc { Single = n == 1 };
            int[]? perm = null;
            if (br.ReadBool())
            {
                var code = JxlEntropyCode.Read(br, 8);
                var r = new JxlSymbolReader(code, br, 0);
                perm = DecodePermutation(br, r, n, 0);
                r.CheckFinalState();
            }
            br.ZeroPadToByte();
            var sizes = new int[n];
            long total = 0;
            for (int i = 0; i < n; i++)
            {
                uint s = br.ReadU32(Bits(10), BitsOffset(14, 1024), BitsOffset(22, 17408), BitsOffset(30, 4211712));
                sizes[i] = (int)Math.Min(s, int.MaxValue);
                total += s;
            }
            br.ZeroPadToByte();
            br.CheckEnd();
            if (total > int.MaxValue) throw new ImageFormatException("JPEG XL: frame too large");
            var offs = new int[n];
            int o = 0;
            for (int i = 0; i < n; i++) { offs[i] = o; o += sizes[i]; }
            t.TotalSize = o;
            if (perm != null)
            {
                t.Offset = new int[n]; t.Size = new int[n];
                for (int i = 0; i < n; i++) { t.Offset[i] = offs[perm[i]]; t.Size[i] = sizes[perm[i]]; }
            }
            else { t.Offset = offs; t.Size = sizes; }
            return t;
        }

        public int LfGlobalIndex => 0;
        public int LfGroupIndex(int i) => 1 + i;
        public int HfGlobalIndex(JxlFrameDims d) => 1 + d.NumLfGroups;
        public int PassGroupIndex(JxlFrameDims d, int pass, int group) => 2 + d.NumLfGroups + pass * d.NumGroups + group;
    }
}
