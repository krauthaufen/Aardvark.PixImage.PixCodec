using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>
    /// Bit reader of the JPEG XL codestream (ISO/IEC 18181-1): bits are read least significant first. Reading past the end
    /// of the section yields zero bits; <see cref="JxlBitReader.CheckEnd"/> turns that into an <see cref="ImageFormatException"/>.
    /// </summary>
    /// <summary>
    /// The state of a <see cref="JxlBitReader"/> as a value (pointer into the pinned codestream) for hot loops (see
    /// <see cref="JxlAnsFast"/>, which reads with the same semantics: bits past the end read as zero, more than 64 bytes
    /// past the end throw).
    /// </summary>
    internal unsafe struct JxlBitState
    {
        public byte* P, End;
        public ulong Buf;
        public int NBits;

        /// <summary>More bits consumed than the section holds.</summary>
        public bool Overrun => (P - End) * 8 > NBits;
    }

    internal sealed class JxlBitReader
    {
        readonly byte[] data;
        readonly int start, end;
        int pos;           // next byte to load
        ulong buf;         // unread bits, LSB first
        int nbits;         // valid bits in buf

        public JxlBitReader(byte[] data) : this(data, 0, data.Length) { }

        public JxlBitReader(byte[] data, int offset, int length)
        {
            if (offset < 0 || length < 0 || offset + length > data.Length) throw new ImageFormatException("JPEG XL: section outside of the codestream");
            this.data = data; start = offset; end = offset + length; pos = offset;
        }

        /// <summary>Bits consumed since the start of the section.</summary>
        public long BitPosition => (long)(pos - start) * 8 - nbits;

        public long TotalBits => (long)(end - start) * 8;

        public bool Overrun => BitPosition > TotalBits;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Refill()
        {
            if (pos + 8 <= end)
            {
                buf |= BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos)) << nbits;
                int take = (63 - nbits) >> 3;
                pos += take;
                nbits += take << 3;
            }
            else RefillSlow();
        }

        void RefillSlow()
        {
            while (nbits <= 56)
            {
                if (pos < end) buf |= (ulong)data[pos] << nbits;
                pos++;
                nbits += 8;
                if (pos > end + 64) { pos = end + 64; throw new ImageFormatException("JPEG XL: unexpected end of data"); }
            }
        }

        /// <summary>Reads n bits, 0 &lt;= n &lt;= 32.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint ReadBits(int n)
        {
            if (nbits < n) Refill();
            uint v = (uint)(buf & ((1UL << n) - 1));
            buf >>= n;
            nbits -= n;
            return v;
        }

        /// <summary>Peeks up to 32 bits without consuming them.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint PeekBits(int n)
        {
            if (nbits < n) Refill();
            return (uint)(buf & ((1UL << n) - 1));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SkipBits(int n)
        {
            // n <= nbits after a PeekBits(n)
            buf >>= n;
            nbits -= n;
        }

        public bool ReadBool() => ReadBits(1) != 0;

        public ulong ReadBits64(int n)
        {
            if (n <= 32) return ReadBits(n);
            ulong lo = ReadBits(32);
            return lo | (ulong)ReadBits(n - 32) << 32;
        }

        /// <summary>Skips a number of bits that may be large (extensions).</summary>
        public void SkipLong(ulong n)
        {
            if (n > (ulong)TotalBits) throw new ImageFormatException("JPEG XL: extension exceeds the data");
            while (n > 0)
            {
                int k = (int)Math.Min(n, 32UL);
                ReadBits(k);
                n -= (ulong)k;
                if (Overrun) throw new ImageFormatException("JPEG XL: unexpected end of data");
            }
        }

        /// <summary>U32(d0, d1, d2, d3): each distribution is packed as (bits &lt;&lt; 32 | offset) where bits = 0 means a constant.</summary>
        public uint ReadU32(ulong d0, ulong d1, ulong d2, ulong d3)
        {
            ulong d = ReadBits(2) switch { 0 => d0, 1 => d1, 2 => d2, _ => d3 };
            int bits = (int)(d >> 32);
            uint off = (uint)d;
            return bits == 0 ? off : unchecked(off + ReadBits(bits));
        }

        /// <summary>Distribution helpers for <see cref="ReadU32"/>.</summary>
        public static ulong Val(uint v) => v;
        public static ulong Bits(int n) => (ulong)n << 32;
        public static ulong BitsOffset(int n, uint off) => (ulong)n << 32 | off;

        public ulong ReadU64()
        {
            switch (ReadBits(2))
            {
                case 0: return 0;
                case 1: return 1 + ReadBits(4);
                case 2: return 17 + ReadBits(8);
                default:
                    {
                        ulong value = ReadBits(12);
                        int shift = 12;
                        while (ReadBool())
                        {
                            if (shift == 60)
                            {
                                value |= (ulong)ReadBits(4) << 60;
                                break;
                            }
                            value |= (ulong)ReadBits(8) << shift;
                            shift += 8;
                        }
                        return value;
                    }
            }
        }

        public float ReadF16()
        {
            uint bits16 = ReadBits(16);
            uint sign = bits16 >> 15, biasedExp = (bits16 >> 10) & 0x1F, mantissa = bits16 & 0x3FF;
            if (biasedExp == 31) throw new ImageFormatException("JPEG XL: infinite or NaN half float in header");
            if (biasedExp == 0)
            {
                float sub = mantissa * (1.0f / 16777216.0f); // 2^-24
                return sign != 0 ? -sub : sub;
            }
            uint f = sign << 31 | (biasedExp + 112) << 23 | mantissa << 13;
            return BitConverter.Int32BitsToSingle((int)f);
        }

        /// <summary>Enum(): U32(Val(0), Val(1), BitsOffset(4, 2), BitsOffset(6, 18)), at most 63.</summary>
        public int ReadEnum()
        {
            uint v = ReadU32(Val(0), Val(1), BitsOffset(4, 2), BitsOffset(6, 18));
            if (v > 63) throw new ImageFormatException("JPEG XL: enum value out of range");
            return (int)v;
        }

        public void ZeroPadToByte()
        {
            int r = (int)(BitPosition & 7);
            if (r != 0) ReadBits(8 - r);
        }

        /// <summary>Byte offset (from the section start) of the next unread byte; only valid at a byte boundary.</summary>
        public int BytePosition => (int)(BitPosition >> 3);

        public void CheckEnd()
        {
            if (Overrun) throw new ImageFormatException("JPEG XL: unexpected end of data");
        }

        /// <summary>Reads the extensions field of a bundle and skips the extension bits.</summary>
        public void SkipExtensions()
        {
            ulong ext = ReadU64();
            if (ext == 0) return;
            ulong total = 0;
            for (int i = 0; i < 64; i++)
                if ((ext >> i & 1) != 0)
                {
                    ulong n = ReadU64();
                    total += n;
                    if (total > (ulong)TotalBits) throw new ImageFormatException("JPEG XL: extension exceeds the data");
                }
            SkipLong(total);
        }

        public static int UnpackSigned(uint v) => (int)(v >> 1) ^ -(int)(v & 1);

        /// <summary>The codestream the reader reads from (pin it while a <see cref="JxlBitState"/> is in use).</summary>
        public byte[] Data => data;

        /// <summary>The reader's position as a pointer based state; basePtr points at Data[0].</summary>
        public unsafe JxlBitState Export(byte* basePtr) => new() { P = basePtr + pos, End = basePtr + end, Buf = buf, NBits = nbits };

        /// <summary>Continues from a state obtained by <see cref="Export"/>.</summary>
        public unsafe void Import(in JxlBitState s, byte* basePtr)
        {
            pos = (int)(s.P - basePtr); buf = s.Buf; nbits = s.NBits;
        }
        public static long UnpackSigned64(ulong v) => (long)(v >> 1) ^ -(long)(v & 1);
    }
}
