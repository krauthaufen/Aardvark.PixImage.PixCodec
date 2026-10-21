using System;
using System.Buffers.Binary;

namespace Aardvark.ImageCodecs.Compression
{
    /// <summary>
    /// The "branch/call/jump" filters of the .xz format: they turn absolute branch targets in machine code back into
    /// the relative ones of the original file. Each works on a whole block; <c>pos</c> is the stream position of the
    /// first byte (the filter's start offset). Bytes that do not form a complete instruction at the end stay as is.
    /// </summary>
    internal static class BcjFilters
    {
        public const int X86 = 4, PowerPc = 5, Ia64 = 6, Arm = 7, ArmThumb = 8, Sparc = 9, Arm64 = 0xA, RiscV = 0xB;

        public static bool IsBcj(ulong id) => id >= X86 && id <= RiscV;

        /// <summary>Required alignment of the start offset.</summary>
        public static int Alignment(int id) => id switch { X86 => 1, Ia64 => 16, ArmThumb => 2, RiscV => 2, _ => 4 };

        public static void Decode(int id, Span<byte> data, uint pos)
        {
            switch (id)
            {
                case X86: DecodeX86(data, pos); break;
                case PowerPc: DecodePowerPc(data, pos); break;
                case Ia64: DecodeIa64(data, pos); break;
                case Arm: DecodeArm(data, pos); break;
                case ArmThumb: DecodeArmThumb(data, pos); break;
                case Sparc: DecodeSparc(data, pos); break;
                case Arm64: DecodeArm64(data, pos); break;
                case RiscV: DecodeRiscV(data, pos); break;
                default: throw new ImageNotSupportedException("unsupported xz filter 0x" + id.ToString("X"));
            }
        }

        static bool IsSignByte(byte b) => b == 0x00 || b == 0xFF;

        // CALL (E8) and JMP (E9) with a 32 bit displacement whose top byte looks like a sign extension. The
        // encoder remembers which of the last bytes were opcode candidates (prevMask) to avoid converting
        // displacements that overlap an earlier candidate; the decoder has to replay exactly the same decisions.
        static void DecodeX86(Span<byte> b, uint nowPos)
        {
            if (b.Length < 5) return;
            ReadOnlySpan<bool> allowed = stackalloc bool[] { true, true, true, false, true, false, false, false };
            ReadOnlySpan<byte> bitNumber = stackalloc byte[] { 0, 1, 2, 2, 3, 3, 3, 3 };
            uint prevMask = 0, prevPos = nowPos - 5;
            int limit = b.Length - 5;
            int i = 0;
            while (i <= limit)
            {
                if ((b[i] & 0xFE) != 0xE8) { i++; continue; }
                uint offset = nowPos + (uint)i - prevPos;
                prevPos = nowPos + (uint)i;
                if (offset > 5) prevMask = 0;
                else for (uint k = 0; k < offset; k++) prevMask = (prevMask & 0x77) << 1;
                byte top = b[i + 4];
                if (IsSignByte(top) && allowed[(int)((prevMask >> 1) & 7)] && (prevMask >> 1) < 0x10)
                {
                    uint src = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(i + 1));
                    uint dest;
                    for (; ; )
                    {
                        dest = src - (nowPos + (uint)i + 5);
                        if (prevMask == 0) break;
                        int n = bitNumber[(int)(prevMask >> 1)];
                        if (!IsSignByte((byte)(dest >> (24 - n * 8)))) break;
                        src = dest ^ ((1u << (32 - n * 8)) - 1);
                    }
                    b[i + 4] = (byte)~(((dest >> 24) & 1) - 1);
                    b[i + 3] = (byte)(dest >> 16); b[i + 2] = (byte)(dest >> 8); b[i + 1] = (byte)dest;
                    i += 5;
                    prevMask = 0;
                }
                else
                {
                    i++;
                    prevMask |= 1;
                    if (IsSignByte(top)) prevMask |= 0x10;
                }
            }
        }

        // big endian "bl": opcode 18 with AA = 0, LK = 1
        static void DecodePowerPc(Span<byte> b, uint pos)
        {
            for (int i = 0; i + 4 <= b.Length; i += 4)
            {
                if ((b[i] >> 2) != 0x12 || (b[i + 3] & 3) != 1) continue;
                uint src = (uint)((b[i] & 3) << 24 | b[i + 1] << 16 | b[i + 2] << 8 | (b[i + 3] & 0xFC));
                uint dest = src - (pos + (uint)i);
                b[i] = (byte)(0x48 | ((dest >> 24) & 3));
                b[i + 1] = (byte)(dest >> 16); b[i + 2] = (byte)(dest >> 8);
                b[i + 3] = (byte)((b[i + 3] & 3) | (int)(dest & 0xFC));
            }
        }

        // little endian "bl" (condition always): 24 bit word offset relative to the instruction address + 8
        static void DecodeArm(Span<byte> b, uint pos)
        {
            for (int i = 0; i + 4 <= b.Length; i += 4)
            {
                if (b[i + 3] != 0xEB) continue;
                uint src = (uint)(b[i + 2] << 16 | b[i + 1] << 8 | b[i]) << 2;
                uint dest = (src - (pos + (uint)i + 8)) >> 2;
                b[i + 2] = (byte)(dest >> 16); b[i + 1] = (byte)(dest >> 8); b[i] = (byte)dest;
            }
        }

        // Thumb "bl": two 16 bit halves holding the high and low 11 bits of a half-word offset
        static void DecodeArmThumb(Span<byte> b, uint pos)
        {
            for (int i = 0; i + 4 <= b.Length; i += 2)
            {
                if ((b[i + 1] & 0xF8) != 0xF0 || (b[i + 3] & 0xF8) != 0xF8) continue;
                uint src = (uint)((b[i + 1] & 7) << 19 | b[i] << 11 | (b[i + 3] & 7) << 8 | b[i + 2]) << 1;
                uint dest = (src - (pos + (uint)i + 4)) >> 1;
                b[i + 1] = (byte)(0xF0 | ((dest >> 19) & 7));
                b[i] = (byte)(dest >> 11);
                b[i + 3] = (byte)(0xF8 | ((dest >> 8) & 7));
                b[i + 2] = (byte)dest;
                i += 2;
            }
        }

        // big endian "call" whose 30 bit displacement is a sign extended 22 bit value
        static void DecodeSparc(Span<byte> b, uint pos)
        {
            for (int i = 0; i + 4 <= b.Length; i += 4)
            {
                if (!(b[i] == 0x40 && (b[i + 1] & 0xC0) == 0x00) && !(b[i] == 0x7F && (b[i + 1] & 0xC0) == 0xC0)) continue;
                uint src = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(i)) << 2;
                uint dest = (src - (pos + (uint)i)) >> 2;
                dest = (((0 - ((dest >> 22) & 1)) << 22) & 0x3FFFFFFF) | (dest & 0x3FFFFF) | 0x40000000;
                BinaryPrimitives.WriteUInt32BigEndian(b.Slice(i), dest);
            }
        }

        // Itanium: 128 bit bundles of three 41 bit slots; the 5 bit template says which slots can hold a branch
        // (bit n of the entry = slot n). Only IP-relative calls (opcode 5, btype 0) are converted.
        static ReadOnlySpan<byte> Ia64BranchSlots => new byte[]
        {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            4, 4, 6, 6, 0, 0, 7, 7, 4, 4, 0, 0, 4, 4, 0, 0,
        };

        static void DecodeIa64(Span<byte> b, uint pos)
        {
            for (int i = 0; i + 16 <= b.Length; i += 16)
            {
                int mask = Ia64BranchSlots[b[i] & 0x1F];
                for (int slot = 0, bitPos = 5; slot < 3; slot++, bitPos += 41)
                {
                    if (((mask >> slot) & 1) == 0) continue;
                    int bytePos = bitPos >> 3, bitRes = bitPos & 7;
                    ulong instruction = 0;
                    for (int j = 0; j < 6; j++) instruction |= (ulong)b[i + j + bytePos] << (8 * j);
                    ulong norm = instruction >> bitRes;
                    if (((norm >> 37) & 0xF) != 0x5 || ((norm >> 9) & 0x7) != 0) continue;
                    uint src = (uint)((norm >> 13) & 0xFFFFF);
                    src |= ((uint)(norm >> 36) & 1) << 20;
                    src <<= 4;
                    uint dest = (src - (pos + (uint)i)) >> 4;
                    norm &= ~(0x8FFFFFUL << 13);
                    norm |= (ulong)(dest & 0xFFFFF) << 13;
                    norm |= (ulong)(dest & 0x100000) << (36 - 20);
                    instruction &= (1UL << bitRes) - 1;
                    instruction |= norm << bitRes;
                    for (int j = 0; j < 6; j++) b[i + j + bytePos] = (byte)(instruction >> (8 * j));
                }
            }
        }

        // AArch64: BL (26 bit word offset) and ADRP (21 bit page offset, converted only within +-512 MiB)
        static void DecodeArm64(Span<byte> b, uint pos)
        {
            for (int i = 0; i + 4 <= b.Length; i += 4)
            {
                uint pc = pos + (uint)i;
                uint instr = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(i));
                if ((instr & 0xFC000000) == 0x94000000)
                {
                    instr = 0x94000000 | ((instr - (pc >> 2)) & 0x03FFFFFF);
                    BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(i), instr);
                }
                else if ((instr & 0x9F000000) == 0x90000000)
                {
                    uint src = ((instr >> 29) & 3) | ((instr >> 3) & 0x001FFFFC);
                    if (((src + 0x00020000) & 0x001C0000) != 0) continue;
                    instr &= 0x9000001F;
                    uint dest = src - (pc >> 12);
                    instr |= (dest & 3) << 29;
                    instr |= (dest & 0x0003FFFC) << 3;
                    instr |= (0 - (dest & 0x00020000)) & 0x00E00000;
                    BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(i), instr);
                }
            }
        }

        // RISC-V (instructions are little endian, 16 bit granular). Converted are JAL with rd = ra or t0, stored as
        // the absolute target, and AUIPC + 32 bit instruction pairs using the same register, stored as a marker word
        // (AUIPC with rd = x2 carrying the low 20 bits of the second instruction) followed by the big endian
        // absolute address. Input that already looked like the marker form was escaped by the encoder into an
        // ordinary looking pair, which is turned back here.
        static void DecodeRiscV(Span<byte> b, uint pos)
        {
            int i = 0;
            while (i + 8 <= b.Length)
            {
                uint b0 = b[i];
                if (b0 == 0xEF)
                {
                    uint b1 = b[i + 1];
                    if ((b1 & 0x0D) != 0) { i += 2; continue; }
                    uint addr = ((b1 & 0xF0) << 13) | ((uint)b[i + 2] << 9) | ((uint)b[i + 3] << 1);
                    uint off = addr - (pos + (uint)i);
                    // J type immediate order: 20 | 10:1 | 11 | 19:12
                    b[i + 1] = (byte)((b1 & 0x0F) | ((off >> 8) & 0xF0));
                    b[i + 2] = (byte)(((off >> 16) & 0x0F) | ((off >> 7) & 0x10) | ((off << 4) & 0xE0));
                    b[i + 3] = (byte)(((off >> 4) & 0x7F) | ((off >> 13) & 0x80));
                    i += 4;
                }
                else if ((b0 & 0x7F) == 0x17)
                {
                    uint w = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(i));
                    uint w2, v2;
                    if ((w & 0xE80) != 0)
                    {
                        // rd is neither x0 nor x2: an escaped marker if the next word pairs up with it
                        uint v = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(i + 4));
                        if ((v & 3) != 3 || ((v >> 15) & 0x1F) != ((w >> 7) & 0x1F)) { i += 6; continue; }
                        v2 = (w & 0xFFFFF000) + (v >> 20);
                        w2 = 0x17 | 2 << 7 | (v << 12);
                    }
                    else
                    {
                        uint rs1 = w >> 27;
                        if ((w & 0x3FFF) != 0x3117 || (rs1 & 0x1D) == 0) { i += 4; continue; }
                        uint addr = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(i + 4)) - (pos + (uint)i);
                        v2 = (w >> 12) | (addr << 20);
                        w2 = 0x17 | (rs1 << 7) | ((addr + 0x800) & 0xFFFFF000);
                    }
                    BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(i), w2);
                    BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(i + 4), v2);
                    i += 8;
                }
                else i += 2;
            }
        }
    }
}
