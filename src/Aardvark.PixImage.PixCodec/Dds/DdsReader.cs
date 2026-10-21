using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Aardvark.ImageCodecs.DdsImpl
{
    /// <summary>How the stored pixels of one surface are laid out and turned into samples.</summary>
    internal sealed class DdsFormat
    {
        public string Name = "";
        public int Dxgi;
        public int BlockBytes;          // > 0: 4x4 blocks of this size
        public int PixelBits;           // uncompressed: bits per pixel
        public ColorModel Color;
        public int Channels;
        public SampleType Type;
        public bool Srgb;
        public uint[]? Masks;           // legacy bit masks R, G, B, A (luminance: R only)
        public bool Luminance;          // legacy luminance (R mask), result gray
        public bool AlphaOnly;          // legacy alpha-only / A8: the alpha mask, result gray

        public long SurfaceBytes(int w, int h)
            => BlockBytes > 0 ? (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * BlockBytes : ((long)w * PixelBits + 7) / 8 * h;
    }

    internal sealed class DdsParsed
    {
        public required DdsTextureInfo Info;
        public required DdsFormat Format;
        public int DataOffset;
        public int Items, FaceCount;
        public bool Premultiplied;
    }

    internal static class DdsReader
    {
        const uint Magic = 0x20534444;   // "DDS "

        static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(o));

        public static bool IsMatch(ReadOnlySpan<byte> h) => h.Length >= 4 && U32(h, 0) == Magic;

        static uint FourCC(string s) => (uint)(s[0] | s[1] << 8 | s[2] << 16 | s[3] << 24);

        public static DdsParsed Parse(ReadOnlySpan<byte> d)
        {
            if (!IsMatch(d)) throw new ImageFormatException("DDS: missing signature");
            if (d.Length < 128) throw new ImageFormatException("DDS: header truncated");
            uint flags = U32(d, 8);
            int height = (int)Math.Min(U32(d, 12), int.MaxValue), width = (int)Math.Min(U32(d, 16), int.MaxValue);
            int depth = (int)Math.Min(U32(d, 24), int.MaxValue), mips = (int)Math.Min(U32(d, 28), 64u);
            uint pfFlags = U32(d, 80), fourCC = U32(d, 84), bitCount = U32(d, 88);
            uint rMask = U32(d, 92), gMask = U32(d, 96), bMask = U32(d, 100), aMask = U32(d, 104);
            uint caps2 = U32(d, 112);
            var info = new DdsTextureInfo();
            int offset = 128, items = 1, faceCount = 1;
            bool premultiplied = false;
            DdsFormat format;
            List<int> faces = new();

            if ((pfFlags & 4) != 0 && fourCC == FourCC("DX10"))
            {
                if (d.Length < 148) throw new ImageFormatException("DDS: DX10 header truncated");
                int dxgi = (int)U32(d, 128);
                uint dim = U32(d, 132), misc = U32(d, 136), arraySize = U32(d, 140), misc2 = U32(d, 144);
                offset = 148;
                format = FromDxgi(dxgi) ?? throw new ImageNotSupportedException($"DDS: DXGI format {dxgi} is not supported");
                items = (int)Math.Max(1, Math.Min(arraySize, 1u << 16));
                info.AlphaMode = (DdsAlphaMode)(misc2 & 7) is var am && am <= DdsAlphaMode.Custom ? am : DdsAlphaMode.Unknown;
                premultiplied = info.AlphaMode == DdsAlphaMode.Premultiplied;
                switch (dim)
                {
                    case 2: info.Kind = DdsTextureKind.Texture1D; height = 1; depth = 1; break;
                    case 3:
                        info.Kind = (misc & 4) != 0 ? DdsTextureKind.CubeMap : DdsTextureKind.Texture2D;
                        depth = 1;
                        break;
                    case 4: info.Kind = DdsTextureKind.Texture3D; items = 1; break;
                    default: throw new ImageFormatException($"DDS: resource dimension {dim}");
                }
                if (info.Kind == DdsTextureKind.CubeMap) { faceCount = 6; faces.AddRange(new[] { 0, 1, 2, 3, 4, 5 }); }
            }
            else
            {
                format = FromLegacy(pfFlags, fourCC, bitCount, rMask, gMask, bMask, aMask, out premultiplied);
                if ((caps2 & 0x200) != 0)
                {
                    info.Kind = DdsTextureKind.CubeMap;
                    for (int f = 0; f < 6; f++)
                        if ((caps2 & (0x400u << f)) != 0) faces.Add(f);
                    if (faces.Count == 0) faces.AddRange(new[] { 0, 1, 2, 3, 4, 5 });   // flag without faces: take all
                    faceCount = faces.Count;
                }
                else if ((caps2 & 0x200000) != 0 && depth > 1) info.Kind = DdsTextureKind.Texture3D;
                else info.Kind = DdsTextureKind.Texture2D;
                info.AlphaMode = premultiplied ? DdsAlphaMode.Premultiplied : DdsAlphaMode.Unknown;
            }
            if (width <= 0 || height <= 0) throw new ImageFormatException("DDS: image dimensions must be positive");
            if (info.Kind != DdsTextureKind.Texture3D) depth = 1;
            else if (depth <= 0) throw new ImageFormatException("DDS: volume depth must be positive");
            int maxLevels = 1 + BitOperations.Log2((uint)Math.Max(Math.Max(width, height), depth));
            // (the count is used also when the flag is missing, as some writers forget it)
            mips = Math.Clamp(mips, 1, maxLevels);

            info.Width = width; info.Height = height; info.Depth = depth; info.MipLevels = mips;
            info.ArraySize = items; info.Faces = faces;
            info.Format = format.Name; info.DxgiFormat = format.Dxgi; info.IsSrgb = format.Srgb; info.IsBlockCompressed = format.BlockBytes > 0;
            info.Color = format.Color; info.Channels = format.Channels; info.SampleType = format.Type;
            return new DdsParsed { Info = info, Format = format, DataOffset = offset, Items = items, FaceCount = faceCount, Premultiplied = premultiplied };
        }

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data)
        {
            var p = Parse(data);
            var i = p.Info;
            return new ImageInfo(ImageFormat.Dds, i.Width, i.Height, i.Channels, i.SampleType, i.Color, p.Items * p.FaceCount)
            {
                BitsPerSample = RawImage.BytesPerSample(i.SampleType) * 8,
                MipLevels = i.MipLevels,
                Transfer = i.IsSrgb ? TransferFunction.Srgb : TransferFunction.Unknown,
                Depth = i.Depth,
            };
        }

        // ------------------------------------------------------------------ decoding

        /// <summary>Every surface in file order: array element, face, mip level, slice, size and data offset.</summary>
        static List<(int item, int face, int mip, int slice, int w, int h, long pos)> Layout(DdsParsed p)
        {
            var i = p.Info;
            var r = new List<(int, int, int, int, int, int, long)>();
            long pos = p.DataOffset;
            for (int item = 0; item < p.Items; item++)
                for (int face = 0; face < p.FaceCount; face++)
                    for (int m = 0; m < i.MipLevels; m++)
                    {
                        int w = Math.Max(1, i.Width >> m), h = Math.Max(1, i.Height >> m), slices = Math.Max(1, i.Depth >> m);
                        for (int s = 0; s < slices; s++)
                        {
                            r.Add((item, face, m, s, w, h, pos));
                            pos += p.Format.SurfaceBytes(w, h);
                        }
                    }
            return r;
        }

        /// <summary>
        /// Rejects what would allocate too much: checked arithmetically before anything is laid out, for the whole texture
        /// (<paramref name="which"/> = 2) or the top surface / first mip chain.
        /// </summary>
        static void CheckSize(DdsParsed p, DdsDecodeOptions opt, int which, int dataLength)
        {
            var i = p.Info;
            double pixels = 0, bytes = 0, chainPixels = 0, chainBytes = 0;
            for (int m = 0; m < i.MipLevels; m++)
            {
                int w = Math.Max(1, i.Width >> m), h = Math.Max(1, i.Height >> m);
                double slices = Math.Max(1, i.Depth >> m);
                pixels += (double)w * h * slices;
                bytes += p.Format.SurfaceBytes(w, h) * slices;
                if (m == 0 || which == 1) { chainPixels += (double)w * h; chainBytes += p.Format.SurfaceBytes(w, h); }
            }
            double n = (double)p.Items * p.FaceCount;
            double wanted = which == 2 ? pixels * n : chainPixels, stored = which == 2 ? bytes * n : chainBytes;
            if (wanted > opt.MaxPixels) throw new ImageNotSupportedException($"DDS: {wanted} pixels exceed DdsDecodeOptions.MaxPixels");
            if (wanted > opt.MaxUnbackedPixels && p.DataOffset + stored > dataLength)
                throw new ImageFormatException($"DDS: {wanted} pixels announced, but the file holds only {dataLength} bytes");
            if ((long)i.Width * i.Height * i.Channels > Array.MaxLength) throw new ImageNotSupportedException("DDS: surface too large for a single array");
        }

        public static RawImage DecodeTop(ReadOnlySpan<byte> data, DdsDecodeOptions opt)
        {
            var p = Parse(data);
            CheckSize(p, opt, 0, data.Length);
            return Surface(p, data, p.DataOffset, p.Info.Width, p.Info.Height, opt);
        }

        public static RawImage[] DecodeChain(ReadOnlySpan<byte> data, DdsDecodeOptions opt)
        {
            var p = Parse(data);
            CheckSize(p, opt, 1, data.Length);
            var chain = Layout(p).FindAll(s => s.item == 0 && s.face == 0 && s.slice == 0);
            var r = new RawImage[chain.Count];
            for (int k = 0; k < r.Length; k++) r[k] = Surface(p, data, chain[k].pos, chain[k].w, chain[k].h, opt);
            return r;
        }

        public static DdsTexture DecodeAll(ReadOnlySpan<byte> data, DdsDecodeOptions opt)
        {
            var p = Parse(data);
            CheckSize(p, opt, 2, data.Length);
            var all = Layout(p);
            var i = p.Info;
            var t = new DdsTexture
            {
                Kind = i.Kind, Width = i.Width, Height = i.Height, Depth = i.Depth, MipLevels = i.MipLevels, ArraySize = i.ArraySize,
                Faces = i.Faces, Format = i.Format, DxgiFormat = i.DxgiFormat, IsSrgb = i.IsSrgb, IsBlockCompressed = i.IsBlockCompressed,
                AlphaMode = i.AlphaMode, Color = i.Color, SampleType = i.SampleType, Channels = i.Channels,
            };
            var s = new RawImage[p.Items][][][];
            for (int it = 0; it < p.Items; it++)
            {
                s[it] = new RawImage[p.FaceCount][][];
                for (int f = 0; f < p.FaceCount; f++)
                {
                    s[it][f] = new RawImage[i.MipLevels][];
                    for (int m = 0; m < i.MipLevels; m++) s[it][f][m] = new RawImage[Math.Max(1, i.Depth >> m)];
                }
            }
            foreach (var x in all) s[x.item][x.face][x.mip][x.slice] = Surface(p, data, x.pos, x.w, x.h, opt);
            t.Surfaces = s;
            return t;
        }

        /// <summary>One surface; data missing at the end of the file decodes as zeros.</summary>
        static RawImage Surface(DdsParsed p, ReadOnlySpan<byte> data, long pos, int w, int h, DdsDecodeOptions opt)
        {
            var f = p.Format;
            long size = f.SurfaceBytes(w, h);
            ReadOnlySpan<byte> src;
            if (pos + size <= data.Length) src = data.Slice((int)pos, (int)size);
            else
            {
                var padded = new byte[size];
                if (pos < data.Length) data.Slice((int)pos).CopyTo(padded);
                src = padded;
            }
            var img = f.BlockBytes > 0 ? DdsSurfaces.Blocks(f, src, w, h) : DdsSurfaces.Pixels(f, src, w, h);
            if (p.Premultiplied && opt.Unpremultiply && img.HasAlpha) DdsSurfaces.Unpremultiply(img);
            return img;
        }

        // ------------------------------------------------------------------ formats

        static DdsFormat F(string name, int dxgi, int bits, ColorModel color, SampleType type, int channels = 0, bool srgb = false)
            => new() { Name = name, Dxgi = dxgi, PixelBits = bits, Color = color, Type = type, Channels = channels == 0 ? RawImage.ChannelCount(color) : channels, Srgb = srgb };

        static DdsFormat B(string name, int dxgi, int blockBytes, ColorModel color, SampleType type, int channels = 0, bool srgb = false)
            => new() { Name = name, Dxgi = dxgi, BlockBytes = blockBytes, Color = color, Type = type, Channels = channels == 0 ? RawImage.ChannelCount(color) : channels, Srgb = srgb };

        public static DdsFormat? FromDxgi(int dxgi) => dxgi switch
        {
            1 or 2 => F("R32G32B32A32_FLOAT", 2, 128, ColorModel.Rgba, SampleType.Float32),
            3 => F("R32G32B32A32_UINT", 3, 128, ColorModel.Rgba, SampleType.UInt32),
            4 => F("R32G32B32A32_SINT", 4, 128, ColorModel.Rgba, SampleType.Int32),
            5 or 6 => F("R32G32B32_FLOAT", 6, 96, ColorModel.Rgb, SampleType.Float32),
            7 => F("R32G32B32_UINT", 7, 96, ColorModel.Rgb, SampleType.UInt32),
            8 => F("R32G32B32_SINT", 8, 96, ColorModel.Rgb, SampleType.Int32),
            9 or 10 => F("R16G16B16A16_FLOAT", 10, 64, ColorModel.Rgba, SampleType.Float16),
            11 => F("R16G16B16A16_UNORM", 11, 64, ColorModel.Rgba, SampleType.UInt16),
            12 => F("R16G16B16A16_UINT", 12, 64, ColorModel.Rgba, SampleType.UInt16),
            13 => F("R16G16B16A16_SNORM", 13, 64, ColorModel.Rgba, SampleType.Int16),
            14 => F("R16G16B16A16_SINT", 14, 64, ColorModel.Rgba, SampleType.Int16),
            15 or 16 => F("R32G32_FLOAT", 16, 64, ColorModel.Other, SampleType.Float32, 2),
            17 => F("R32G32_UINT", 17, 64, ColorModel.Other, SampleType.UInt32, 2),
            18 => F("R32G32_SINT", 18, 64, ColorModel.Other, SampleType.Int32, 2),
            23 or 24 => F("R10G10B10A2_UNORM", 24, 32, ColorModel.Rgba, SampleType.UInt16),
            25 => F("R10G10B10A2_UINT", 25, 32, ColorModel.Rgba, SampleType.UInt16),
            26 => F("R11G11B10_FLOAT", 26, 32, ColorModel.Rgb, SampleType.Float32),
            27 or 28 => F("R8G8B8A8_UNORM", 28, 32, ColorModel.Rgba, SampleType.UInt8),
            29 => F("R8G8B8A8_UNORM_SRGB", 29, 32, ColorModel.Rgba, SampleType.UInt8, srgb: true),
            30 => F("R8G8B8A8_UINT", 30, 32, ColorModel.Rgba, SampleType.UInt8),
            31 => F("R8G8B8A8_SNORM", 31, 32, ColorModel.Rgba, SampleType.Int8),
            32 => F("R8G8B8A8_SINT", 32, 32, ColorModel.Rgba, SampleType.Int8),
            33 or 34 => F("R16G16_FLOAT", 34, 32, ColorModel.Other, SampleType.Float16, 2),
            35 => F("R16G16_UNORM", 35, 32, ColorModel.Other, SampleType.UInt16, 2),
            36 => F("R16G16_UINT", 36, 32, ColorModel.Other, SampleType.UInt16, 2),
            37 => F("R16G16_SNORM", 37, 32, ColorModel.Other, SampleType.Int16, 2),
            38 => F("R16G16_SINT", 38, 32, ColorModel.Other, SampleType.Int16, 2),
            39 or 41 => F("R32_FLOAT", 41, 32, ColorModel.Gray, SampleType.Float32),
            42 => F("R32_UINT", 42, 32, ColorModel.Gray, SampleType.UInt32),
            43 => F("R32_SINT", 43, 32, ColorModel.Gray, SampleType.Int32),
            48 or 49 => F("R8G8_UNORM", 49, 16, ColorModel.Other, SampleType.UInt8, 2),
            50 => F("R8G8_UINT", 50, 16, ColorModel.Other, SampleType.UInt8, 2),
            51 => F("R8G8_SNORM", 51, 16, ColorModel.Other, SampleType.Int8, 2),
            52 => F("R8G8_SINT", 52, 16, ColorModel.Other, SampleType.Int8, 2),
            53 or 54 => F("R16_FLOAT", 54, 16, ColorModel.Gray, SampleType.Float16),
            56 => F("R16_UNORM", 56, 16, ColorModel.Gray, SampleType.UInt16),
            57 => F("R16_UINT", 57, 16, ColorModel.Gray, SampleType.UInt16),
            58 => F("R16_SNORM", 58, 16, ColorModel.Gray, SampleType.Int16),
            59 => F("R16_SINT", 59, 16, ColorModel.Gray, SampleType.Int16),
            60 or 61 => F("R8_UNORM", 61, 8, ColorModel.Gray, SampleType.UInt8),
            62 => F("R8_UINT", 62, 8, ColorModel.Gray, SampleType.UInt8),
            63 => F("R8_SNORM", 63, 8, ColorModel.Gray, SampleType.Int8),
            64 => F("R8_SINT", 64, 8, ColorModel.Gray, SampleType.Int8),
            65 => F("A8_UNORM", 65, 8, ColorModel.Gray, SampleType.UInt8),
            67 => F("R9G9B9E5_SHAREDEXP", 67, 32, ColorModel.Rgb, SampleType.Float32),
            70 or 71 => B("BC1_UNORM", 71, 8, ColorModel.Rgba, SampleType.UInt8),
            72 => B("BC1_UNORM_SRGB", 72, 8, ColorModel.Rgba, SampleType.UInt8, srgb: true),
            73 or 74 => B("BC2_UNORM", 74, 16, ColorModel.Rgba, SampleType.UInt8),
            75 => B("BC2_UNORM_SRGB", 75, 16, ColorModel.Rgba, SampleType.UInt8, srgb: true),
            76 or 77 => B("BC3_UNORM", 77, 16, ColorModel.Rgba, SampleType.UInt8),
            78 => B("BC3_UNORM_SRGB", 78, 16, ColorModel.Rgba, SampleType.UInt8, srgb: true),
            79 or 80 => B("BC4_UNORM", 80, 8, ColorModel.Gray, SampleType.UInt8),
            81 => B("BC4_SNORM", 81, 8, ColorModel.Gray, SampleType.Int8),
            82 or 83 => B("BC5_UNORM", 83, 16, ColorModel.Other, SampleType.UInt8, 2),
            84 => B("BC5_SNORM", 84, 16, ColorModel.Other, SampleType.Int8, 2),
            85 => F("B5G6R5_UNORM", 85, 16, ColorModel.Rgb, SampleType.UInt8),
            86 => F("B5G5R5A1_UNORM", 86, 16, ColorModel.Rgba, SampleType.UInt8),
            87 or 90 => F("B8G8R8A8_UNORM", 87, 32, ColorModel.Rgba, SampleType.UInt8),
            91 => F("B8G8R8A8_UNORM_SRGB", 91, 32, ColorModel.Rgba, SampleType.UInt8, srgb: true),
            88 or 92 => F("B8G8R8X8_UNORM", 88, 32, ColorModel.Rgb, SampleType.UInt8),
            93 => F("B8G8R8X8_UNORM_SRGB", 93, 32, ColorModel.Rgb, SampleType.UInt8, srgb: true),
            94 or 95 => B("BC6H_UF16", 95, 16, ColorModel.Rgb, SampleType.Float16),
            96 => B("BC6H_SF16", 96, 16, ColorModel.Rgb, SampleType.Float16),
            97 or 98 => B("BC7_UNORM", 98, 16, ColorModel.Rgba, SampleType.UInt8),
            99 => B("BC7_UNORM_SRGB", 99, 16, ColorModel.Rgba, SampleType.UInt8, srgb: true),
            115 => F("B4G4R4A4_UNORM", 115, 16, ColorModel.Rgba, SampleType.UInt8),
            _ => null,
        };

        static DdsFormat FromLegacy(uint pf, uint fourCC, uint bits, uint r, uint g, uint b, uint a, out bool premultiplied)
        {
            premultiplied = false;
            if ((pf & 4) != 0)
            {
                int dxgi = fourCC switch
                {
                    _ when fourCC == FourCC("DXT1") => 71,
                    _ when fourCC == FourCC("DXT2") => -74,
                    _ when fourCC == FourCC("DXT3") => 74,
                    _ when fourCC == FourCC("DXT4") => -77,
                    _ when fourCC == FourCC("DXT5") => 77,
                    _ when fourCC == FourCC("ATI1") || fourCC == FourCC("BC4U") => 80,
                    _ when fourCC == FourCC("BC4S") => 81,
                    _ when fourCC == FourCC("ATI2") || fourCC == FourCC("BC5U") => 83,
                    _ when fourCC == FourCC("BC5S") => 84,
                    36 => 11, 110 => 13, 111 => 54, 112 => 34, 113 => 10, 114 => 41, 115 => 16, 116 => 2,
                    _ => 0,
                };
                if (dxgi == 0)
                {
                    string cc = fourCC is >= 0x20202020 ? System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(fourCC)) : fourCC.ToString();
                    throw new ImageNotSupportedException($"DDS: format '{cc}' is not supported");
                }
                premultiplied = dxgi < 0;
                return FromDxgi(Math.Abs(dxgi))!;
            }
            if (bits is not (8 or 16 or 24 or 32)) throw new ImageFormatException($"DDS: {bits} bits per pixel");
            if ((pf & 0x80000) != 0)   // signed bump map formats
            {
                if (bits == 16 && r == 0xFF && g == 0xFF00) return FromDxgi(51)!;
                if (bits == 32 && r == 0xFF && g == 0xFF00 && b == 0xFF0000) return FromDxgi(31)!;
                if (bits == 32 && r == 0xFFFF && g == 0xFFFF0000) return FromDxgi(37)!;
                throw new ImageNotSupportedException("DDS: this bump map format is not supported");
            }
            if ((pf & 0x200) != 0) throw new ImageNotSupportedException("DDS: YUV formats are not supported");
            bool alpha = (pf & 1) != 0 && a != 0;
            bool wide = Width(r) > 8 || Width(g) > 8 || Width(b) > 8 || Width(alpha ? a : 0) > 8;
            var type = wide ? SampleType.UInt16 : SampleType.UInt8;
            foreach (var m in new[] { r, g, b, a })
                if (m != 0 && (m >> BitOperations.TrailingZeroCount(m) & (m >> BitOperations.TrailingZeroCount(m)) + 1) != 0)
                    throw new ImageFormatException("DDS: bit mask is not contiguous");
            if ((pf & 0x20000) != 0)   // luminance
                return new DdsFormat { Name = alpha ? "L/A (legacy)" : "L (legacy)", PixelBits = (int)bits, Color = alpha ? ColorModel.GrayAlpha : ColorModel.Gray,
                    Channels = alpha ? 2 : 1, Type = type, Masks = new[] { r, 0u, 0u, alpha ? a : 0u }, Luminance = true };
            if ((pf & 0x40) == 0 && (pf & 2) != 0)   // alpha only
                return new DdsFormat { Name = "A (legacy)", Dxgi = bits == 8 && a == 0xFF ? 65 : 0, PixelBits = (int)bits, Color = ColorModel.Gray, Channels = 1,
                    Type = type, Masks = new[] { 0u, 0u, 0u, a }, AlphaOnly = true };
            if ((pf & 0x40) == 0) throw new ImageFormatException("DDS: unknown pixel format");
            int known = (bits, r, g, b, alpha ? a : 0u) switch
            {
                (32, 0xFF, 0xFF00, 0xFF0000, 0xFF000000) => 28,
                (32, 0xFF0000, 0xFF00, 0xFF, 0xFF000000) => 87,
                (32, 0xFF0000, 0xFF00, 0xFF, 0) => 88,
                (32, 0x3FF, 0xFFC00, 0x3FF00000, 0xC0000000) => 24,
                (16, 0xF800, 0x7E0, 0x1F, 0) => 85,
                (16, 0x7C00, 0x3E0, 0x1F, 0x8000) => 86,
                (16, 0xF00, 0xF0, 0xF, 0xF000) => 115,
                _ => 0,
            };
            string name = known != 0 ? FromDxgi(known)!.Name : $"RGB{(alpha ? "A" : "")} {bits} bit (legacy)";
            return new DdsFormat { Name = name, Dxgi = known, PixelBits = (int)bits, Color = alpha ? ColorModel.Rgba : ColorModel.Rgb, Channels = alpha ? 4 : 3,
                Type = type, Masks = new[] { r, g, b, alpha ? a : 0u } };
        }

        static int Width(uint mask) => BitOperations.PopCount(mask);
    }
}
