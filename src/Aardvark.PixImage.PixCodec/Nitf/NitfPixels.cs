using System;
using System.Buffers.Binary;
using System.Threading;

namespace Aardvark.ImageCodecs
{
    /// <summary>The image data of a NITF image segment (MIL-STD-2500C 5.4.3.3 and table A-3(A)).</summary>
    internal static class NitfPixels
    {
        // ---------------------------------------------------------------- image data mask (IC NM, M3, M8)

        /// <summary>
        /// Where the blocked image data starts, the offset of each block (each block of each band for IMODE S) from there,
        /// -1 for a block that is not recorded, and the pad pixel code. Without a mask the offsets are null.
        /// </summary>
        static (long Start, long[]? Offsets, ulong? Pad) Mask(ReadOnlySpan<byte> d, NitfImageSegment s, int units)
        {
            long end = s.DataOffset + s.DataLength;
            // NM, M3, M8 ...: a mask table in front of the blocked image data
            if (s.Compression[0] != 'M' && s.Compression != "NM") return (s.DataOffset, null, null);
            long p = s.DataOffset;
            if (p + 10 > end) throw new ImageFormatException("NITF: the image data mask is cut off");
            uint imdatoff = BinaryPrimitives.ReadUInt32BigEndian(d.Slice((int)p));
            int bmrlnth = BinaryPrimitives.ReadUInt16BigEndian(d.Slice((int)p + 4));
            int tmrlnth = BinaryPrimitives.ReadUInt16BigEndian(d.Slice((int)p + 6));
            int tpxcdlnth = BinaryPrimitives.ReadUInt16BigEndian(d.Slice((int)p + 8));
            p += 10;
            ulong? pad = null;
            if (tpxcdlnth > 0)
            {
                int n = (tpxcdlnth + 7) / 8;
                if (n > 8 || p + n > end) throw new ImageFormatException($"NITF: the pad pixel code of {tpxcdlnth} bits is not usable");
                ulong v = 0;
                for (int i = 0; i < n; i++) v = v << 8 | d[(int)p + i];
                pad = v;
                p += n;
            }
            long[]? offsets = null;
            if (bmrlnth == 4)
            {
                if (p + 4L * units > end) throw new ImageFormatException("NITF: the block mask records are cut off");
                offsets = new long[units];
                for (int i = 0; i < units; i++)
                {
                    uint o = BinaryPrimitives.ReadUInt32BigEndian(d.Slice((int)p + 4 * i));
                    offsets[i] = o == 0xFFFFFFFF ? -1 : o;
                }
            }
            else if (bmrlnth != 0) throw new ImageFormatException($"NITF: BMRLNTH is {bmrlnth}, not 0 or 4");
            if (tmrlnth is not (0 or 4)) throw new ImageFormatException($"NITF: TMRLNTH is {tmrlnth}, not 0 or 4");
            long start = s.DataOffset + imdatoff;
            if (start > end) throw new ImageFormatException("NITF: IMDATOFF points past the image data");
            return (start, offsets, pad);
        }

        // ---------------------------------------------------------------- uncompressed

        internal static RawImage Uncompressed(ReadOnlySpan<byte> d, NitfImageSegment s, CancellationToken cancel)
        {
            var (type, parts) = Nitf.StoredType(s);
            int w = s.Columns, h = s.Rows, nb = s.Bands.Length, ch = nb * parts;
            int bw = s.BlockWidth, bh = s.BlockHeight, nbpr = s.BlocksPerRow, nbpc = s.BlocksPerColumn;
            int bpp = s.BitsPerPixel, partBits = bpp / parts;
            char mode = s.Mode;
            int blocks = nbpr * nbpc;
            bool sequential = mode == 'S';
            int units = sequential ? blocks * nb : blocks;
            // 5.4.3.3.1: the data of a block is one bit stream, zero-filled at its end only; band sequential stores
            // each band "as if it were a single band image", so there each block of each band is padded on its own
            long unitBits = (long)bw * bh * bpp * (sequential ? 1 : nb);
            long unitBytes = (unitBits + 7) / 8;

            var (start, offsets, pad) = Mask(d, s, units);
            long end = s.DataOffset + s.DataLength;
            var unitStart = new long[units];
            for (int u = 0; u < units; u++)
            {
                long o = offsets == null ? u * unitBytes : offsets[u];
                if (o < 0) { unitStart[u] = -1; continue; }
                if (start + o + unitBytes > end)
                    throw new ImageFormatException($"NITF: block {u} needs {start + o + unitBytes - s.DataOffset} bytes of image data, the segment has {s.DataLength}");
                unitStart[u] = start + o;
            }

            var img = RawImage.Create(w, h, ch == 1 ? ColorModel.Gray : ColorModel.Other, type, ch);
            var row = new ulong[(long)w * ch];
            var conv = new Converter(s, partBits);
            for (int by = 0; by < nbpc; by++)
            {
                cancel.ThrowIfCancellationRequested();
                for (int y = 0; y < bh; y++)
                {
                    int gy = by * bh + y;
                    if (gy >= h) break;
                    for (int bx = 0; bx < nbpr; bx++)
                    {
                        int gx0 = bx * bw, cols = Math.Min(bw, w - gx0);
                        if (cols <= 0) break;
                        int blk = by * nbpr + bx;
                        for (int b = 0; b < nb; b++)
                        {
                            long us = unitStart[sequential ? b * blocks + blk : blk];
                            if (us < 0)
                            {
                                ulong fill = pad ?? 0;
                                for (int x = 0; x < cols; x++)
                                    for (int q = 0; q < parts; q++) row[(long)(gx0 + x) * ch + b * parts + q] = fill;
                                continue;
                            }
                            long bit0 = us * 8 + mode switch
                            {
                                'P' => ((long)y * bw * nb + b) * bpp,
                                'B' => ((long)b * bh + y) * bw * bpp,
                                'R' => ((long)y * nb + b) * bw * bpp,
                                _ => (long)y * bw * bpp,
                            };
                            long step = mode == 'P' ? (long)nb * bpp : bpp;
                            for (int x = 0; x < cols; x++)
                            {
                                long bit = bit0 + x * step;
                                for (int q = 0; q < parts; q++)
                                    row[(long)(gx0 + x) * ch + b * parts + q] = Bits(d, bit + (long)q * partBits, partBits);
                            }
                        }
                    }
                    conv.Store(row, img.Data, (long)gy * w * ch);
                }
            }
            return img;
        }

        /// <summary>n (1 to 64) bits at a bit position, most significant bit first (5.1.9.1).</summary>
        static ulong Bits(ReadOnlySpan<byte> d, long bitPos, int n)
        {
            long i = bitPos >> 3;
            int bit = (int)(bitPos & 7);
            ulong v = 0;
            if (bit == 0 && (n & 7) == 0)
            {
                for (int k = 0; k < n >> 3; k++) v = v << 8 | d[(int)(i + k)];
                return v;
            }
            int left = n;
            while (left > 0)
            {
                int avail = 8 - bit, take = Math.Min(avail, left);
                v = v << take | (uint)((d[(int)i] >> (avail - take)) & ((1 << take) - 1));
                left -= take;
                bit = 0;
                i++;
            }
            return v;
        }

        /// <summary>Turns stored values into samples: the significant bits by PJUST, sign extension, IEEE floats.</summary>
        sealed class Converter
        {
            readonly SampleType _type;
            readonly int _shift, _abpp;
            readonly ulong _mask;
            readonly bool _signed, _integer;

            public Converter(NitfImageSegment s, int partBits)
            {
                _type = Nitf.StoredType(s).Type;
                _integer = s.PixelValueType is "INT" or "B" or "SI";
                _signed = s.PixelValueType == "SI";
                _abpp = _integer ? s.ActualBitsPerPixel : partBits;
                // table A-3 ABPP / PJUST: the ABPP "representation bits" sit left or right justified in the NBPP bits
                _shift = _integer && s.Justification == 'L' ? partBits - _abpp : 0;
                _mask = _abpp >= 64 ? ulong.MaxValue : (1UL << _abpp) - 1;
            }

            ulong Value(ulong v)
            {
                if (!_integer) return v;
                v = v >> _shift & _mask;
                if (_signed && _abpp < 64 && (v >> (_abpp - 1) & 1) != 0) v |= ~_mask;
                return v;
            }

            public void Store(ulong[] row, Array dst, long at)
            {
                switch (_type)
                {
                    case SampleType.UInt8: { var a = (byte[])dst; for (int i = 0; i < row.Length; i++) a[at + i] = (byte)Value(row[i]); break; }
                    case SampleType.UInt16: { var a = (ushort[])dst; for (int i = 0; i < row.Length; i++) a[at + i] = (ushort)Value(row[i]); break; }
                    case SampleType.UInt32: { var a = (uint[])dst; for (int i = 0; i < row.Length; i++) a[at + i] = (uint)Value(row[i]); break; }
                    case SampleType.UInt64: { var a = (ulong[])dst; for (int i = 0; i < row.Length; i++) a[at + i] = Value(row[i]); break; }
                    case SampleType.Int8: { var a = (sbyte[])dst; for (int i = 0; i < row.Length; i++) a[at + i] = (sbyte)Value(row[i]); break; }
                    case SampleType.Int16: { var a = (short[])dst; for (int i = 0; i < row.Length; i++) a[at + i] = (short)Value(row[i]); break; }
                    case SampleType.Int32: { var a = (int[])dst; for (int i = 0; i < row.Length; i++) a[at + i] = (int)Value(row[i]); break; }
                    case SampleType.Int64: { var a = (long[])dst; for (int i = 0; i < row.Length; i++) a[at + i] = (long)Value(row[i]); break; }
                    case SampleType.Float32: { var a = (float[])dst; for (int i = 0; i < row.Length; i++) a[at + i] = BitConverter.UInt32BitsToSingle((uint)row[i]); break; }
                    case SampleType.Float64: { var a = (double[])dst; for (int i = 0; i < row.Length; i++) a[at + i] = BitConverter.UInt64BitsToDouble(row[i]); break; }
                    default: throw new ImageNotSupportedException($"NITF: {_type} samples");
                }
            }
        }

        // ---------------------------------------------------------------- JPEG 2000 (IC C8 / M8)

        internal static RawImage Jpeg2000(ReadOnlySpan<byte> d, NitfImageSegment s, NitfDecodeOptions o)
        {
            // the blocks are the tiles of one codestream (BIIF profile BPJ2K); a mask only adds an offset table in front
            var (start, _, _) = Mask(d, s, s.Mode == 'S' ? s.BlocksPerRow * s.BlocksPerColumn * s.Bands.Length : s.BlocksPerRow * s.BlocksPerColumn);
            long end = s.DataOffset + s.DataLength;
            var cs = d.Slice((int)start, (int)(end - start));
            var img = Jp2.Decode(cs, new Jp2DecodeOptions { RawComponents = true, ApplyOrientation = false, MaxPixels = Math.Max(o.MaxPixels, 1) });
            if (img.Channels != s.Bands.Length)
                throw new ImageFormatException($"NITF: the JPEG 2000 codestream has {img.Channels} components, the subheader {s.Bands.Length} bands");
            if (img.Width < s.Columns || img.Height < s.Rows)
                throw new ImageFormatException($"NITF: the JPEG 2000 codestream is {img.Width} x {img.Height}, the subheader says {s.Columns} x {s.Rows}");
            return Crop(img, s.Columns, s.Rows);
        }

        static RawImage Crop(RawImage img, int w, int h)
        {
            if (img.Width == w && img.Height == h) return img;
            var r = RawImage.Create(w, h, img.Color, img.SampleType, img.Channels);
            int bps = RawImage.BytesPerSample(img.SampleType), srcRow = img.Width * img.Channels * bps, dstRow = w * img.Channels * bps;
            var src = img.GetBytes();
            var dst = r.GetBytes();
            for (int y = 0; y < h; y++) src.Slice(y * srcRow, dstRow).CopyTo(dst.Slice(y * dstRow, dstRow));
            return r;
        }

        // ---------------------------------------------------------------- JPEG (IC C3 / M3)

        internal static RawImage Jpeg(ReadOnlySpan<byte> d, NitfImageSegment s, CancellationToken cancel)
        {
            int w = s.Columns, h = s.Rows, nb = s.Bands.Length;
            int bw = s.BlockWidth, bh = s.BlockHeight, nbpr = s.BlocksPerRow, nbpc = s.BlocksPerColumn, blocks = nbpr * nbpc;
            // one stream per block with all bands (IMODE P), or one per band of each block (B: block by block, S: band by band)
            bool perBand = nb > 1 && s.Mode != 'P';
            int streams = perBand ? blocks * nb : blocks;
            var (start, offsets, _) = Mask(d, s, streams);
            long end = s.DataOffset + s.DataLength;
            var type = s.BitsPerPixel > 8 ? SampleType.UInt16 : SampleType.UInt8;
            var img = RawImage.Create(w, h, nb == 1 ? ColorModel.Gray : ColorModel.Other, type, nb);
            int bps = RawImage.BytesPerSample(type);
            var dst = img.GetBytes();
            var jo = new JpegDecodeOptions { ApplyOrientation = false, KeepYCbCr = s.Representation != "YCbCr601", CancellationToken = cancel };
            long next = start;
            for (int k = 0; k < streams; k++)
            {
                long at;
                if (offsets != null)
                {
                    if (offsets[k] < 0) continue;
                    at = start + offsets[k];
                }
                else at = next;
                if (at >= end) throw new ImageFormatException($"NITF: JPEG block {k} lies past the image data");
                var (skip, len) = JpegStream(d.Slice((int)at, (int)(end - at)), out bool noDht);
                next = at + skip + len;
                RawImage block;
                try { block = ImageCodecs.Jpeg.Decode(d.Slice((int)at + skip, (int)len), jo); }
                catch (ImageFormatException e) when (noDht)
                {
                    // without DHT the JPEG decoder falls back to the tables of ITU-T T.81 annex K; MIL-STD-188-198A may
                    // mean other defaults, which this reader does not have
                    throw new ImageNotSupportedException($"NITF: a JPEG block without DHT relies on default Huffman tables of MIL-STD-188-198A, which are not supported ({e.Message})");
                }
                int blk, band0, bands;
                if (!perBand) { blk = k; band0 = 0; bands = nb; }
                else if (s.Mode == 'S') { blk = k % blocks; band0 = k / blocks; bands = 1; }
                else { blk = k / nb; band0 = k % nb; bands = 1; }
                if (block.Channels != bands || block.SampleType != type)
                    throw new ImageFormatException($"NITF: JPEG block {k} has {block.Channels} {block.SampleType} components, {bands} {type} expected");
                int gx0 = blk % nbpr * bw, gy0 = blk / nbpr * bh;
                int cols = Math.Min(Math.Min(bw, block.Width), w - gx0), rows = Math.Min(Math.Min(bh, block.Height), h - gy0);
                var src = block.GetBytes();
                for (int y = 0; y < rows; y++)
                    for (int x = 0; x < cols; x++)
                        src.Slice(((y * block.Width + x) * bands) * bps, bands * bps)
                           .CopyTo(dst.Slice((((gy0 + y) * w + gx0 + x) * nb + band0) * bps, bands * bps));
            }
            return img;
        }

        /// <summary>
        /// Where the JPEG stream at the start of the data begins (after fill bytes 0xFF, which ISO/IEC 10918-1 B.1.1.2
        /// allows in front of any marker, SOI included) and how long it is, SOI to EOI, walking the marker segments.
        /// </summary>
        static (int Start, long Length) JpegStream(ReadOnlySpan<byte> d, out bool missingHuffman)
        {
            bool dqt = false, dht = false, huffman = false;
            missingHuffman = false;
            int s = 0;
            while (s + 1 < d.Length && d[s] == 0xFF && d[s + 1] == 0xFF) s++;
            if (s + 4 > d.Length || d[s] != 0xFF || d[s + 1] != 0xD8) throw new ImageFormatException("NITF: a JPEG block does not start with SOI");
            int p = s + 2;
            while (p + 1 < d.Length)
            {
                if (d[p] != 0xFF) throw new ImageFormatException("NITF: a JPEG block has bytes between its marker segments");
                byte m = d[p + 1];
                if (m == 0xFF) { p++; continue; }
                if (m == 0xD9) return (s, p + 2 - s);
                if (m is 0x01 or (>= 0xD0 and <= 0xD7)) { p += 2; continue; }
                if (p + 4 > d.Length) break;
                int len = d[p + 2] << 8 | d[p + 3];
                p += 2 + len;
                if (m == 0xDB) dqt = true;
                else if (m == 0xC4) dht = true;
                else if (m is 0xC0 or 0xC1 or 0xC2 or 0xC3) huffman = true;
                if (m != 0xDA) continue;
                // MIL-STD-188-198A lets a stream leave its quantization tables out and use defaults that COMRAT
                // names; this reader does not have them
                if (!dqt)
                    throw new ImageNotSupportedException("NITF: a JPEG block without DQT relies on the default quantization tables of MIL-STD-188-198A, which are not supported");
                missingHuffman = huffman && !dht;
                // entropy coded data, restart markers and fill bytes in front of them included, up to the next other marker
                while (p + 1 < d.Length)
                {
                    if (d[p] != 0xFF) { p++; continue; }
                    byte n = d[p + 1];
                    if (n == 0x00 || n is >= 0xD0 and <= 0xD7) p += 2;
                    else if (n == 0xFF) p++;
                    else break;
                }
            }
            throw new ImageFormatException("NITF: a JPEG block has no EOI");
        }
    }
}
