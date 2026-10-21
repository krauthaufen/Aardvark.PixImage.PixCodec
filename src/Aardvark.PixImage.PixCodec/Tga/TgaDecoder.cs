using System;
using System.Buffers.Binary;

namespace Aardvark.ImageCodecs.TgaImpl
{
    internal sealed class TgaHeader
    {
        public int IdLength, ColorMapType, ImageType, MapFirst, MapLength, MapEntryBits, Width, Height, Bpp, Descriptor;
        public int AlphaBits => Descriptor & 15;
        public bool RightToLeft => (Descriptor & 0x10) != 0;
        public bool TopDown => (Descriptor & 0x20) != 0;
        public bool Rle => ImageType >= 9;
        public int Kind => ImageType & 7;          // 1 colour-mapped, 2 true-colour, 3 gray
        public int AlphaType = -1;                 // TGA 2.0 extension area attributes type, -1 if absent

        static ushort U16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(o));

        public static bool IsMatch(ReadOnlySpan<byte> h)
        {
            if (h.Length < 18) return false;
            int cmapType = h[1], type = h[2], entry = h[7], bpp = h[16], desc = h[17];
            if (cmapType > 1 || (desc & 0xC0) != 0) return false;
            if (U16(h, 12) == 0 || U16(h, 14) == 0) return false;
            return (type & 7) switch
            {
                1 when type is 1 or 9 => cmapType == 1 && entry is 15 or 16 or 24 or 32 && bpp is 8 or 16 && U16(h, 5) > 0,
                2 when type is 2 or 10 => bpp is 15 or 16 or 24 or 32 && (cmapType == 0 || entry is 15 or 16 or 24 or 32),
                3 when type is 3 or 11 => bpp is 8 or 16 && (cmapType == 0 || entry is 15 or 16 or 24 or 32),
                _ => false,
            };
        }

        public static TgaHeader Parse(ReadOnlySpan<byte> d)
        {
            if (!IsMatch(d)) throw new ImageFormatException("TGA: not a valid TGA header");
            var h = new TgaHeader
            {
                IdLength = d[0], ColorMapType = d[1], ImageType = d[2], MapFirst = U16(d, 3), MapLength = U16(d, 5), MapEntryBits = d[7],
                Width = U16(d, 12), Height = U16(d, 14), Bpp = d[16], Descriptor = d[17],
            };
            // TGA 2.0 footer and extension area
            if (d.Length >= 18 + 26 && d.Slice(d.Length - 18).SequenceEqual("TRUEVISION-XFILE.\0"u8))
            {
                uint ext = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(d.Length - 26));
                if (ext >= 18 && ext + 495 <= d.Length - 26 && U16(d, (int)ext) >= 495) h.AlphaType = d[(int)ext + 494];
            }
            return h;
        }

        /// <summary>Whether the stored pixels (or palette entries) carry alpha that the result keeps.</summary>
        public bool HasAlpha
        {
            get
            {
                bool stored = Kind switch
                {
                    1 => MapEntryBits == 32 || MapEntryBits == 16 && AlphaBits > 0,
                    2 => Bpp == 32 || Bpp == 16 && AlphaBits > 0,
                    _ => Bpp == 16,
                };
                // extension area: 0 no alpha, 1 / 2 undefined (ignored), 3 alpha, 4 premultiplied alpha
                return stored && AlphaType is -1 or 3 or 4;
            }
        }

        public ColorModel Color => Kind == 3 ? (HasAlpha ? ColorModel.GrayAlpha : ColorModel.Gray) : (HasAlpha ? ColorModel.Rgba : ColorModel.Rgb);
    }

    internal static class TgaDecoder
    {
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data)
        {
            var h = TgaHeader.Parse(data);
            var color = h.Color;
            return new ImageInfo(ImageFormat.Tga, h.Width, h.Height, RawImage.ChannelCount(color), SampleType.UInt8, color)
            {
                BitsPerSample = h.Kind == 1 ? 8 : h.Bpp is 15 or 16 && h.Kind == 2 ? 5 : 8,
            };
        }

        public static RawImage Decode(ReadOnlySpan<byte> data, TgaDecodeOptions opt)
        {
            var h = TgaHeader.Parse(data);
            int w = h.Width, hh = h.Height;
            long pixels = (long)w * hh;
            if (pixels > opt.MaxPixels) throw new ImageNotSupportedException($"TGA: {w}x{hh} exceeds TgaDecodeOptions.MaxPixels");
            int pos = 18 + h.IdLength;

            // colour map (also present, and skipped, in files of the other kinds)
            int entryBytes = (h.MapEntryBits + 7) / 8;
            byte[] map = Array.Empty<byte>();   // RGBA per entry
            if (h.ColorMapType == 1)
            {
                int bytes = h.MapLength * entryBytes;
                if (h.Kind == 1)
                {
                    map = new byte[h.MapLength * 4];
                    for (int i = 0; i < h.MapLength && pos + (i + 1) * entryBytes <= data.Length; i++)
                        Pixel(data.Slice(pos + i * entryBytes), h.MapEntryBits, h.AlphaBits, map.AsSpan(i * 4));
                }
                pos += bytes;
            }
            if (pos > data.Length) pos = data.Length;

            int bpp = (h.Bpp + 7) / 8;
            if (!h.Rle && pixels > opt.MaxUnbackedPixels && data.Length - pos < pixels * bpp)
                throw new ImageFormatException($"TGA: {w}x{hh} image with only {data.Length - pos} bytes of pixel data");

            // the stored pixels in file order, a row at a time: straight from the data when it is plain and complete,
            // else decompressed (RLE packets may run across rows) or zero padded into a row buffer
            int rowBytes = w * bpp;
            bool direct = !h.Rle && data.Length - pos >= pixels * bpp;
            var rowArr = direct ? null : new byte[rowBytes];
            int rleLeft = 0;
            bool rleRepeat = false;

            var color = h.Color;
            int ch = RawImage.ChannelCount(color);
            var dst = new byte[pixels * ch];
            Span<byte> rgba = stackalloc byte[4];
            bool alpha = h.HasAlpha, anyAlpha = false;
            for (int y = 0; y < hh; y++)
            {
                ReadOnlySpan<byte> raw;
                if (direct) raw = data.Slice(pos + y * rowBytes, rowBytes);
                else
                {
                    var row = rowArr.AsSpan();
                    int n = 0;
                    if (!h.Rle)
                    {
                        long start = pos + (long)y * rowBytes;
                        if (start < data.Length) { n = (int)Math.Min(rowBytes, data.Length - start); data.Slice((int)start, n).CopyTo(row); }
                    }
                    else
                    {
                        while (n < rowBytes)
                        {
                            if (rleLeft == 0)
                            {
                                if (pos >= data.Length) break;
                                int c = data[pos++];
                                rleLeft = (c & 0x7F) + 1;
                                rleRepeat = (c & 0x80) != 0;
                                if (rleRepeat)
                                {
                                    if (pos + bpp > data.Length) { pos = data.Length; rleLeft = 0; break; }
                                    pos += bpp;
                                }
                            }
                            if (rleRepeat)
                            {
                                var v = data.Slice(pos - bpp, bpp);
                                for (; rleLeft > 0 && n < rowBytes; rleLeft--, n += bpp) v.CopyTo(row.Slice(n));
                            }
                            else
                            {
                                int k = Math.Min(rleLeft, (rowBytes - n) / bpp);
                                int bytes = Math.Min(k * bpp, data.Length - pos);
                                data.Slice(pos, bytes).CopyTo(row.Slice(n));
                                pos += bytes; n += bytes; rleLeft -= k;
                                if (bytes < k * bpp) { rleLeft = 0; break; }
                            }
                        }
                    }
                    row.Slice(n).Clear();
                    raw = row;
                }
                int dy = h.TopDown ? y : hh - 1 - y;
                for (int x = 0; x < w; x++)
                {
                    int dx = h.RightToLeft ? w - 1 - x : x;
                    var s = raw.Slice(x * bpp, bpp);
                    if (h.Kind == 1)
                    {
                        int idx = (bpp == 1 ? s[0] : s[0] | s[1] << 8) - h.MapFirst;
                        if (idx >= 0 && idx < h.MapLength) map.AsSpan(idx * 4, 4).CopyTo(rgba);
                        else { rgba.Clear(); rgba[3] = 255; }   // out of range: opaque black
                    }
                    else if (h.Kind == 2) Pixel(s, h.Bpp, h.AlphaBits, rgba);
                    else { rgba[0] = rgba[1] = rgba[2] = s[0]; rgba[3] = bpp == 2 ? s[1] : (byte)255; }
                    int o = (int)(((long)dy * w + dx) * ch);
                    if (ch <= 2)
                    {
                        dst[o] = rgba[0];
                        if (ch == 2) dst[o + 1] = rgba[3];
                    }
                    else
                    {
                        dst[o] = rgba[0]; dst[o + 1] = rgba[1]; dst[o + 2] = rgba[2];
                        if (ch == 4) dst[o + 3] = rgba[3];
                    }
                    anyAlpha |= alpha && rgba[3] != 0;
                }
            }
            if (alpha)
            {
                if (!anyAlpha)
                    for (long i = ch - 1; i < dst.LongLength; i += ch) dst[i] = 255;   // all zero: not meant as alpha
                else if (h.AlphaType == 4) Unpremultiply(dst, ch);
            }
            return new RawImage(w, hh, color, dst, ch);
        }

        /// <summary>One stored true-colour value (B, G, R[, A] or 5:5:5[:1] little endian) as RGBA.</summary>
        static void Pixel(ReadOnlySpan<byte> s, int bits, int alphaBits, Span<byte> rgba)
        {
            switch (bits)
            {
                case 15:
                case 16:
                    {
                        int v = s[0] | s[1] << 8;
                        rgba[0] = Five(v >> 10); rgba[1] = Five(v >> 5); rgba[2] = Five(v);
                        rgba[3] = bits == 16 && alphaBits > 0 ? (byte)((v & 0x8000) != 0 ? 255 : 0) : (byte)255;
                        break;
                    }
                case 24: rgba[0] = s[2]; rgba[1] = s[1]; rgba[2] = s[0]; rgba[3] = 255; break;
                default: rgba[0] = s[2]; rgba[1] = s[1]; rgba[2] = s[0]; rgba[3] = s[3]; break;
            }
        }

        static byte Five(int v) { v &= 31; return (byte)(v << 3 | v >> 2); }

        static void Unpremultiply(byte[] d, int ch)
        {
            for (long i = 0; i < d.LongLength; i += ch)
            {
                int a = d[i + ch - 1];
                if (a == 0 || a == 255) continue;
                for (int c = 0; c < ch - 1; c++) d[i + c] = (byte)Math.Min(255, (d[i + c] * 255 + a / 2) / a);
            }
        }
    }
}
