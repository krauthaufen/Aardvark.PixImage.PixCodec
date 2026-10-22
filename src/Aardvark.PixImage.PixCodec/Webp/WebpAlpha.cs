using System;

namespace Aardvark.ImageCodecs
{
    /// <summary>The ALPH chunk: an 8 bit alpha plane, raw or compressed as the green channel of a lossless image stream,
    /// optionally run through one of three prediction filters.</summary>
    internal static class WebpAlpha
    {
        /// <summary>The alpha plane, in an array from <see cref="System.Buffers.ArrayPool{T}.Shared"/> that may be longer.</summary>
        public static byte[] Decode(ReadOnlySpan<byte> chunk, int width, int height)
        {
            if (chunk.Length < 1) throw new ImageFormatException("empty WebP alpha chunk");
            int method = chunk[0] & 3, filter = (chunk[0] >> 2) & 3;     // the pre-processing bits are informative only
            int n = width * height;
            var alpha = System.Buffers.ArrayPool<byte>.Shared.Rent(n);
            if (method == 0)
            {
                if (chunk.Length - 1 < n) throw new ImageFormatException("WebP alpha data is too short");
                chunk.Slice(1, n).CopyTo(alpha);
            }
            else if (method == 1)
            {
                var argb = Vp8lDecoder.DecodeHeaderless(chunk.Slice(1), width, height, out bool rented);
                for (int i = 0; i < n; i++) alpha[i] = (byte)(argb[i] >> 8);
                if (rented) System.Buffers.ArrayPool<uint>.Shared.Return(argb);
            }
            else throw new ImageFormatException("unknown WebP alpha compression method");
            Unfilter(alpha, width, height, filter);
            return alpha;
        }

        static void Unfilter(byte[] a, int w, int h, int filter)
        {
            if (filter == 0) return;
            // first row: every filter predicts from the left
            for (int x = 1; x < w; x++) a[x] += a[x - 1];
            for (int y = 1; y < h; y++)
            {
                var cur = a.AsSpan(y * w, w);
                var up = a.AsSpan((y - 1) * w, w);
                cur[0] += up[0];
                switch (filter)
                {
                    case 1:
                        for (int x = 1; x < w; x++) cur[x] += cur[x - 1];
                        break;
                    case 2:
                        for (int x = 1; x < w; x++) cur[x] += up[x];
                        break;
                    default:
                        for (int x = 1; x < w; x++)
                            cur[x] += (byte)Math.Clamp(cur[x - 1] + up[x] - up[x - 1], 0, 255);
                        break;
                }
            }
        }

        /// <summary>Applies a prediction filter (1 horizontal, 2 vertical, 3 gradient); src and dst must not overlap.</summary>
        public static void Filter(ReadOnlySpan<byte> src, Span<byte> dst, int w, int h, int filter)
        {
            dst[0] = src[0];
            for (int x = 1; x < w; x++) dst[x] = (byte)(src[x] - src[x - 1]);
            for (int y = 1; y < h; y++)
            {
                var cur = src.Slice(y * w, w);
                var up = src.Slice((y - 1) * w, w);
                var o = dst.Slice(y * w, w);
                o[0] = (byte)(cur[0] - up[0]);
                switch (filter)
                {
                    case 1:
                        for (int x = 1; x < w; x++) o[x] = (byte)(cur[x] - cur[x - 1]);
                        break;
                    case 2:
                        for (int x = 1; x < w; x++) o[x] = (byte)(cur[x] - up[x]);
                        break;
                    default:
                        for (int x = 1; x < w; x++)
                            o[x] = (byte)(cur[x] - Math.Clamp(cur[x - 1] + up[x] - up[x - 1], 0, 255));
                        break;
                }
            }
        }
    }
}
