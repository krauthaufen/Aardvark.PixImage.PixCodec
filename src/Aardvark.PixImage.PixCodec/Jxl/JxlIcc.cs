using System;
using System.Collections.Generic;
using System.Text;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>
    /// The compressed ICC profile of the image header: an entropy coded byte stream (41 contexts chosen from the two
    /// previous bytes) holding the ICC encoding (predicted header, tag list commands, content commands with prediction).
    /// </summary>
    internal static class JxlIcc
    {
        const int HeaderSize = 128;
        const long MaxSize = 1 << 28;

        static int Kind1(int b)
        {
            if (b >= 'a' && b <= 'z' || b >= 'A' && b <= 'Z') return 0;
            if (b >= '0' && b <= '9' || b == '.' || b == ',') return 1;
            if (b == 0) return 2;
            if (b == 1) return 3;
            if (b < 16) return 4;
            if (b == 255) return 6;
            if (b > 240) return 5;
            return 7;
        }

        static int Kind2(int b)
        {
            if (b >= 'a' && b <= 'z' || b >= 'A' && b <= 'Z') return 0;
            if (b >= '0' && b <= '9' || b == '.' || b == ',') return 1;
            if (b < 16) return 2;
            if (b > 240) return 3;
            return 4;
        }

        public static byte[] Read(JxlBitReader br)
        {
            ulong encSize = br.ReadU64();
            if (encSize > MaxSize) throw new ImageNotSupportedException("JPEG XL: ICC profile too large");
            var code = JxlEntropyCode.Read(br, 41);
            var r = new JxlSymbolReader(code, br, 0);
            var enc = new byte[encSize];
            for (int i = 0; i < enc.Length; i++)
            {
                int ctx = i <= 128 ? 0 : 1 + Kind1(enc[i - 1]) + Kind2(enc[i - 2]) * 8;
                uint v = r.ReadUint(br, ctx);
                if (v > 255) throw new ImageFormatException("JPEG XL: bad ICC byte");
                enc[i] = (byte)v;
                if ((i & 4095) == 0 && br.Overrun) throw new ImageFormatException("JPEG XL: truncated ICC profile");
            }
            r.CheckFinalState();
            br.CheckEnd();
            return Unpredict(enc);
        }

        static ulong VarInt(byte[] d, ref int pos, int end)
        {
            ulong v = 0;
            for (int shift = 0; shift < 63; shift += 7)
            {
                if (pos >= end) throw new ImageFormatException("JPEG XL: truncated ICC varint");
                byte b = d[pos++];
                v |= (ulong)(b & 127) << shift;
                if ((b & 128) == 0) return v;
            }
            throw new ImageFormatException("JPEG XL: ICC varint too long");
        }

        static readonly byte[] InitialHeader =
        {
            0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, (byte)'m', (byte)'n', (byte)'t', (byte)'r',
            (byte)'R', (byte)'G', (byte)'B', (byte)' ', (byte)'X', (byte)'Y', (byte)'Z', (byte)' ', 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, (byte)'a', (byte)'c', (byte)'s', (byte)'p', 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 246, 214, 0, 1, 0, 0, 0, 0, 211, 45,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        };

        static readonly string[] TagStrings = { "cprt", "wtpt", "bkpt", "rXYZ", "gXYZ", "bXYZ", "kXYZ", "rTRC", "gTRC", "bTRC", "kTRC", "chad", "desc", "chrm", "dmnd", "dmdd", "lumi" };
        static readonly string[] TypeStrings = { "XYZ ", "desc", "text", "mluc", "para", "curv", "sf32", "gbd " };

        static void PredictHeader(List<byte> icc, byte[] header, int pos)
        {
            int size = icc.Count;
            if (pos == 8 && size >= 8) { header[80] = icc[4]; header[81] = icc[5]; header[82] = icc[6]; header[83] = icc[7]; }
            if (pos == 41 && size >= 41)
            {
                if (icc[40] == 'A') { header[41] = (byte)'P'; header[42] = (byte)'P'; header[43] = (byte)'L'; }
                if (icc[40] == 'M') { header[41] = (byte)'S'; header[42] = (byte)'F'; header[43] = (byte)'T'; }
            }
            if (pos == 42 && size >= 42)
            {
                if (icc[40] == 'S' && icc[41] == 'G') { header[42] = (byte)'I'; header[43] = (byte)' '; }
                if (icc[40] == 'S' && icc[41] == 'U') { header[42] = (byte)'N'; header[43] = (byte)'W'; }
            }
        }

        static void AppendU32(List<byte> o, ulong v)
        {
            if (v > uint.MaxValue) throw new ImageFormatException("JPEG XL: ICC value out of range");
            o.Add((byte)(v >> 24)); o.Add((byte)(v >> 16)); o.Add((byte)(v >> 8)); o.Add((byte)v);
        }

        static void AppendTag(List<byte> o, string t) { foreach (char ch in t) o.Add((byte)ch); }

        static ulong PredictValue(ulong p1, ulong p2, ulong p3, int order) => order switch
        {
            0 => p1,
            1 => 2 * p1 - p2,
            _ => 3 * p1 - 3 * p2 + p3,
        };

        static byte LinearPredict(List<byte> data, int start, int i, int stride, int width, int order)
        {
            int pos = start + i;
            if (width == 1)
            {
                ulong p1 = pos >= stride ? data[pos - stride] : 0u;
                ulong p2 = pos >= 2 * stride ? data[pos - 2 * stride] : 0u;
                ulong p3 = pos >= 3 * stride ? data[pos - 3 * stride] : 0u;
                return (byte)PredictValue(p1, p2, p3, order);
            }
            pos = start + (i & ~(width - 1));
            ulong q1 = pos >= stride ? BigEndian(data, pos - stride, width) : 0;
            ulong q2 = pos >= 2 * stride ? BigEndian(data, pos - 2 * stride, width) : 0;
            ulong q3 = pos >= 3 * stride ? BigEndian(data, pos - 3 * stride, width) : 0;
            ulong pred = PredictValue(q1, q2, q3, order);
            int shiftBytes = width - 1 - (i & (width - 1));
            return (byte)(pred >> (shiftBytes * 8));
        }

        static ulong BigEndian(List<byte> d, int pos, int width)
        {
            ulong v = 0;
            for (int k = 0; k < width; k++) v = v << 8 | d[pos + k];
            return v;
        }

        /// <summary>
        /// Shuffle of E.4.5: the input fills `width` rows of ceil(size / width) bytes each (the last rows shorter), the
        /// output reads the matrix column by column. This is what libjxl (djxl 0.12) does. The text of 18181-1:2024 has
        /// only the last column short (rows of ceil and ceil - 1 bytes); the two differ when two or more elements are
        /// missing (size % width of 1 or 2 with width 4) and agree on the example there.
        /// </summary>
        static byte[] Unshuffle(byte[] d, int start, int size, int width)
        {
            var r = new byte[size];
            int height = (size + width - 1) / width;
            int s = 0, j = 0;
            for (int i = 0; i < size; i++)
            {
                r[i] = d[start + j];
                j += height;
                if (j >= size) j = ++s;
            }
            return r;
        }

        /// <summary>
        /// E.4.2: the decoder reads both streams to their ends and the profile has exactly output_size bytes (data left
        /// over is not copied: no command produces it).
        /// </summary>
        static byte[] Finish(List<byte> o, ulong osize, int pos, int end, int cpos = 0, int cend = 0)
        {
            if ((ulong)o.Count != osize) throw new ImageFormatException("JPEG XL: ICC profile has the wrong size");
            if (pos != end || cpos != cend) throw new ImageFormatException("JPEG XL: ICC stream has unused bytes");
            return o.ToArray();
        }

        /// <summary>Turns the ICC encoding into the profile.</summary>
        public static byte[] Unpredict(byte[] enc)
        {
            int end = enc.Length;
            int cpos = 0;
            ulong osize = VarInt(enc, ref cpos, end);
            ulong csize = VarInt(enc, ref cpos, end);
            if (osize > MaxSize || csize > (ulong)(end - cpos)) throw new ImageFormatException("JPEG XL: bad ICC sizes");
            int cend = cpos + (int)csize;
            int pos = cend;   // data stream
            var o = new List<byte>((int)osize);
            var header = (byte[])InitialHeader.Clone();
            header[0] = (byte)(osize >> 24); header[1] = (byte)(osize >> 16); header[2] = (byte)(osize >> 8); header[3] = (byte)osize;
            for (int i = 0; i < HeaderSize && (ulong)o.Count < osize; i++)
            {
                PredictHeader(o, header, i);
                if (pos >= end) throw new ImageFormatException("JPEG XL: truncated ICC header");
                o.Add((byte)(enc[pos++] + header[i]));
            }
            if ((ulong)o.Count == osize) return Finish(o, osize, pos, end, cpos, cend);

            // tag list (none when the command stream is already used up); the start predicted for a tag is the previous
            // tag's start plus its size, after the three tags of an XYZ triple too (18181-1:2024 E.4.4)
            ulong numTags = cpos < cend ? VarInt(enc, ref cpos, cend) : 0;
            if (numTags != 0)
            {
                numTags--;
                if (numTags > osize / 12) throw new ImageFormatException("JPEG XL: bad ICC tag count");
                AppendU32(o, numTags);
                ulong prevStart = HeaderSize + numTags * 12, prevSize = 0;
                while (true)
                {
                    if ((ulong)o.Count > osize) throw new ImageFormatException("JPEG XL: ICC profile overflows");
                    if (cpos > cend) throw new ImageFormatException("JPEG XL: bad ICC commands");
                    if (cpos == cend) break;
                    byte command = enc[cpos++];
                    int tagcode = command & 63;
                    if (tagcode == 0) break;
                    string tag;
                    if (tagcode == 1)
                    {
                        if (pos + 4 > end) throw new ImageFormatException("JPEG XL: truncated ICC tag");
                        tag = Encoding.Latin1.GetString(enc, pos, 4);
                        pos += 4;
                    }
                    else if (tagcode == 2) tag = "rTRC";
                    else if (tagcode == 3) tag = "rXYZ";
                    else
                    {
                        if (tagcode - 4 >= TagStrings.Length) throw new ImageFormatException("JPEG XL: bad ICC tag code");
                        tag = TagStrings[tagcode - 4];
                    }
                    AppendTag(o, tag);
                    ulong tagSize = prevSize;
                    if (tag is "rXYZ" or "gXYZ" or "bXYZ" or "kXYZ" or "wtpt" or "bkpt" or "lumi") tagSize = 20;
                    ulong tagStart = (command & 64) != 0 ? VarInt(enc, ref cpos, cend) : prevStart + prevSize;
                    AppendU32(o, tagStart);
                    if ((command & 128) != 0) tagSize = VarInt(enc, ref cpos, cend);
                    AppendU32(o, tagSize);
                    prevStart = tagStart; prevSize = tagSize;
                    if (tagcode == 2)
                    {
                        AppendTag(o, "gTRC"); AppendU32(o, tagStart); AppendU32(o, tagSize);
                        AppendTag(o, "bTRC"); AppendU32(o, tagStart); AppendU32(o, tagSize);
                    }
                    if (tagcode == 3)
                    {
                        AppendTag(o, "gXYZ"); AppendU32(o, tagStart + tagSize); AppendU32(o, tagSize);
                        AppendTag(o, "bXYZ"); AppendU32(o, tagStart + tagSize * 2); AppendU32(o, tagSize);
                    }
                }
            }

            // main content
            while (cpos < cend)
            {
                if ((ulong)o.Count > osize) throw new ImageFormatException("JPEG XL: ICC profile overflows");
                byte command = enc[cpos++];
                if (command == 1 || command == 2 || command == 3)
                {
                    ulong num = VarInt(enc, ref cpos, cend);
                    if (num == 0) throw new ImageFormatException("JPEG XL: empty ICC data command");
                    if (num > (ulong)(end - pos)) throw new ImageFormatException("JPEG XL: truncated ICC data");
                    if (command == 1) for (int i = 0; i < (int)num; i++) o.Add(enc[pos + i]);
                    else o.AddRange(Unshuffle(enc, pos, (int)num, command == 2 ? 2 : 4));
                    pos += (int)num;
                }
                else if (command == 4)
                {
                    if (cpos >= cend) throw new ImageFormatException("JPEG XL: truncated ICC command");
                    byte flags = enc[cpos++];
                    int width = (flags & 3) + 1;
                    if (width == 3) throw new ImageFormatException("JPEG XL: bad ICC prediction width");
                    int order = (flags & 12) >> 2;
                    if (order == 3) throw new ImageFormatException("JPEG XL: bad ICC prediction order");
                    ulong stride = (ulong)width;
                    if ((flags & 16) != 0)
                    {
                        stride = VarInt(enc, ref cpos, cend);
                        if (stride < (ulong)width) throw new ImageFormatException("JPEG XL: bad ICC stride");
                    }
                    if (o.Count == 0 || (ulong)((o.Count - 1) >> 2) < stride) throw new ImageFormatException("JPEG XL: bad ICC prediction stride");
                    ulong num = VarInt(enc, ref cpos, cend);
                    if (num == 0) throw new ImageFormatException("JPEG XL: empty ICC data command");
                    if (num > (ulong)(end - pos) || stride > int.MaxValue) throw new ImageFormatException("JPEG XL: truncated ICC data");
                    byte[] shuffled;
                    if (width > 1) shuffled = Unshuffle(enc, pos, (int)num, width);
                    else { shuffled = new byte[num]; Array.Copy(enc, pos, shuffled, 0, (int)num); }
                    pos += (int)num;
                    int start = o.Count;
                    for (int i = 0; i < (int)num; i++)
                        o.Add((byte)(shuffled[i] + LinearPredict(o, start, i, (int)stride, width, order)));
                }
                else if (command == 10)
                {
                    AppendTag(o, "XYZ ");
                    o.Add(0); o.Add(0); o.Add(0); o.Add(0);
                    if (pos + 12 > end) throw new ImageFormatException("JPEG XL: truncated ICC data");
                    for (int i = 0; i < 12; i++) o.Add(enc[pos++]);
                }
                else if (command >= 16 && command < 16 + TypeStrings.Length)
                {
                    AppendTag(o, TypeStrings[command - 16]);
                    o.Add(0); o.Add(0); o.Add(0); o.Add(0);
                }
                else throw new ImageFormatException("JPEG XL: bad ICC command");
            }
            return Finish(o, osize, pos, end);
        }
    }
}
