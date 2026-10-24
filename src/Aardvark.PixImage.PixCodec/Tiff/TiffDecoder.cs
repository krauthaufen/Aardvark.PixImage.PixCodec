using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Aardvark.ImageCodecs.Compression;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// Decodes one strip or tile of a JPEG-compressed TIFF (compression 7).
    /// <paramref name="jpegTables"/> is the content of the JPEGTables tag (an abbreviated "tables only" JPEG stream, empty
    /// when the tag is absent), <paramref name="data"/> the strip/tile (an abbreviated or complete JPEG stream).
    /// The decoder must write the top-left <paramref name="width"/> x <paramref name="height"/> pixels as 8-bit samples,
    /// interleaved, upsampled to full resolution, <paramref name="components"/> samples per pixel, into
    /// <paramref name="destination"/> (width * height * components bytes). No colour transform may be guessed from the
    /// stream: convert YCbCr to RGB exactly when <paramref name="convertYCbCrToRgb"/> is set, otherwise return the
    /// components as stored. Must be thread safe (strips/tiles are decoded in parallel) and throw
    /// ImageFormatException on corrupt data.
    /// </summary>
    internal delegate void TiffJpegDecoder(ReadOnlySpan<byte> jpegTables, ReadOnlySpan<byte> data, int width, int height,
        int components, bool convertYCbCrToRgb, Span<byte> destination);

    /// <summary>Thrown for bad caller arguments so the "corrupt file" safety net does not relabel them.</summary>
    internal sealed class TiffArgumentException : ArgumentOutOfRangeException
    {
        public TiffArgumentException(string name, string message) : base(name, message) { }
    }

    /// <summary>How the samples of a strip/tile are laid out once it is decompressed.</summary>
    internal enum TiffChunkKind
    {
        /// <summary>Whole 1/2/4/8 byte samples in file byte order.</summary>
        Samples,
        /// <summary>Samples of 1..32 bits packed MSB first, rows padded to a byte.</summary>
        Packed,
        /// <summary>24-bit floats, three bytes per sample.</summary>
        Float24,
        /// <summary>Subsampled YCbCr stored as blocks of (SubH*SubV luma, Cb, Cr).</summary>
        YccPacked,
        Jpeg,
        Webp,
        Jp2,
        Lerc,
        /// <summary>JPEG XL codestream per strip/tile (DNG 1.7, compression 52546).</summary>
        Jxl,
        /// <summary>SGI LogL / LogLuv, converted to floats while decoding.</summary>
        LogLuv,
        /// <summary>Pixar 11-bit log samples (deflate compressed, horizontally differenced), mapped to linear values while decoding.</summary>
        PixarLog,
        /// <summary>Whole-byte samples whose SampleFormat differs from sample to sample, converted to Float64.</summary>
        Mixed,
    }

    /// <summary>One IFD: parsed layout, and the strip/tile decoder for it.</summary>
    internal sealed unsafe class TiffImage
    {
        public readonly TiffFile File;
        public readonly TiffEntry[] Entries;
        public readonly int Width, Height, Spp, Bits, Format, Compression, Photometric, Planar, Predictor, FillOrder;
        /// <summary>Stored bits of every sample; <see cref="Bits"/> is the largest.</summary>
        public readonly int[] BitsPer;
        public readonly bool UniformBits;
        public readonly bool Tiled;
        public readonly int ChunkW, ChunkH, Across, Down, ChunksPerPlane;
        public readonly int[] Extra;
        public readonly int SubH = 1, SubV = 1;
        public readonly TiffChunkKind Kind;

        // raw = samples as stored (after unpacking), out = what the caller gets
        public readonly SampleType RawType, OutType;
        public readonly int Bps, ColorSamples, RawChannels, OutChannels;
        public readonly ColorModel OutColor;
        public readonly bool HasAlpha, AssociatedAlpha, IsJpeg, IsCcitt, IsJp2, IsLab, IsLogLuv, ConvertToRgb;
        /// <summary>Largest value of an unsigned sample as it ends up in the raw image (after optional scaling).</summary>
        public readonly ulong SampleMax;
        /// <summary>Planar YCbCr: the planes are assembled as full resolution YCbCr and converted at the end.</summary>
        public readonly bool YccPlanar;

        readonly bool[] _scale;      // per sample: stretch to the full range of the output type
        readonly ulong[] _inMax;
        readonly ulong _outMax;
        readonly int _pixelBits;     // stored bits of one contiguous pixel
        readonly bool _oldJpeg, _xyzPost, _complex;
        readonly int[]? _formats;
        readonly int _storedStrips;
        readonly long _ifdOffset;

        long[] _offsets = Array.Empty<long>(), _counts = Array.Empty<long>();
        ushort[]? _colorMap;
        byte[] _jpegTables = Array.Empty<byte>();
        JpegTiffTables? _jpegPrepared;
        TiffOldJpeg? _ojpeg;
        bool _ojpegTiffYcc;
        long _t4Options;
        float[]? _yccY, _yccCrR, _yccCbB, _yccCrG, _yccCbG;
        int _lercSecond;
        double[]? _ycc16;

        // per decode call
        int _rx, _ry, _rw, _rh;
        byte* _dst;
        long _dstRowBytes;

        public TiffImage(TiffFile file, long ifdOffset, TiffDecodeOptions? options = null)
        {
            options ??= TiffDecodeOptions.Default;
            ConvertToRgb = options.ConvertToRgb;
            File = file;
            _ifdOffset = ifdOffset;
            var e = Entries = file.ReadIfd(ifdOffset);
            long w = file.Integer(e, 256, 0), h = file.Integer(e, 257, 0);
            if (w <= 0 || h <= 0 || w > int.MaxValue || h > int.MaxValue) throw new ImageFormatException("TIFF: invalid image dimensions");
            Width = (int)w; Height = (int)h;
            long spp = file.Integer(e, 277, 1);
            if (spp < 1 || spp > 65535) throw new ImageFormatException("TIFF: invalid SamplesPerPixel");
            // SampleFormat: complex data (5: integer pairs, 6: float pairs) is handled as two samples of half the
            // width - real part, imaginary part - per stored sample; formats that differ between the samples
            // are kept in _formats and converted to Float64.
            var formats = file.Integers(e, 339) ?? Array.Empty<long>();
            long format0 = formats.Length > 0 ? formats[0] : 1;
            bool sameFormat = true;
            for (int i = 1; i < Math.Min(formats.Length, (int)spp); i++) sameFormat &= formats[i] == format0;
            bool complex = sameFormat && (format0 == 5 || format0 == 6);
            if (complex)
            {
                if (spp > 1 && file.Integer(e, 284, 1) == 2) throw new ImageNotSupportedException("TIFF: planar complex samples are not supported");
                spp *= 2;
            }
            Spp = (int)spp;
            _complex = complex;
            Compression = (int)file.Integer(e, 259, 1);
            _oldJpeg = Compression == 6;
            IsJpeg = Compression == 7 || _oldJpeg || Compression == 34892; // 34892: DNG lossy JPEG (baseline DCT)
            IsCcitt = Compression is 2 or 3 or 4;
            IsJp2 = Compression is 34712 or 33003 or 33004 or 33005;
            long pm = file.Integer(e, 262, -1);
            if (pm < 0) pm = IsCcitt ? 0 : Spp >= 3 ? 2 : 1; // required tag, but be lenient
            if (complex) pm = 1; // no colour interpretation: all parts are returned as ColorModel.Other
            Photometric = (int)pm;
            IsLab = Photometric is 8 or 9 or 10;
            IsLogLuv = Photometric is 32844 or 32845;

            BitsPer = new int[Spp];
            var bits = file.Integers(e, 258);
            UniformBits = true;
            for (int i = 0; i < Spp; i++)
            {
                long b = bits == null || bits.Length == 0 ? 1 : bits[Math.Min(complex ? i / 2 : i, bits.Length - 1)];
                if (complex)
                {
                    if (b != 32 && b != 64 && b != 128) throw new ImageNotSupportedException($"TIFF: complex samples of {b} bits are not supported");
                    b /= 2;
                }
                if (b < 1 || b > 64) throw new ImageFormatException("TIFF: invalid BitsPerSample");
                BitsPer[i] = (int)b;
                Bits = Math.Max(Bits, (int)b);
                _pixelBits += (int)b;
                if (b != BitsPer[0]) UniformBits = false;
            }
            Format = complex ? (format0 == 5 ? 2 : 3) : (int)format0;
            if (Format == 4) Format = 1; // "undefined" data is treated as unsigned integers
            if (!sameFormat)
            {
                _formats = new int[Spp];
                for (int i = 0; i < Spp; i++)
                {
                    long f = formats[Math.Min(i, formats.Length - 1)];
                    if (f == 4) f = 1;
                    if (f < 1 || f > 3) throw new ImageNotSupportedException($"TIFF: sample format {f} mixed with others is not supported");
                    _formats[i] = (int)f;
                }
                Format = 3;
            }

            Planar = Spp == 1 ? 1 : (int)file.Integer(e, 284, 1);
            if (Planar != 1 && Planar != 2) throw new ImageFormatException("TIFF: invalid PlanarConfiguration");
            Predictor = (int)file.Integer(e, 317, 1);
            FillOrder = (int)file.Integer(e, 266, 1);
            Extra = complex ? Array.Empty<int>() : Array.ConvertAll(file.Integers(e, 338) ?? Array.Empty<long>(), x => (int)x);

            if (TiffFile.Find(e, 322, out _))
            {
                long tw = file.Integer(e, 322, 0), th = file.Integer(e, 323, 0);
                if (tw <= 0 || th <= 0 || tw > int.MaxValue || th > int.MaxValue) throw new ImageFormatException("TIFF: invalid tile size");
                Tiled = true; ChunkW = (int)tw; ChunkH = (int)th;
            }
            else
            {
                long rps = file.Integer(e, 278, uint.MaxValue);
                if (rps <= 0 || rps > Height) rps = Height;
                ChunkW = Width; ChunkH = (int)rps;
            }
            if (_oldJpeg)
            {
                // the strips of an old-style JPEG file are pieces of ONE frame (per plane): treat them as a single chunk
                if (!Tiled)
                {
                    _storedStrips = (int)(((long)Height + ChunkH - 1) / ChunkH);
                    ChunkH = Height;
                }
            }
            Across = (int)(((long)Width + ChunkW - 1) / ChunkW);
            Down = (int)(((long)Height + ChunkH - 1) / ChunkH);
            if ((long)Across * Down > int.MaxValue / Spp) throw new ImageFormatException("TIFF: too many strips or tiles");
            ChunksPerPlane = Across * Down;

            ColorSamples = Photometric switch
            {
                0 or 1 or 3 or 4 or 32844 => 1,
                2 or 6 or 8 or 9 or 10 or 32845 => 3,
                5 => 4,
                32803 => 1,                      // colour filter array (raw sensor data): returned as it is stored
                34892 => Spp >= 3 ? 3 : 1,       // DNG LinearRaw
                51177 or 52527 => 1,             // DNG depth map, semantic mask
                _ => throw new ImageNotSupportedException($"TIFF: photometric interpretation {Photometric} is not supported"),
            };
            if (Spp < ColorSamples) throw new ImageFormatException("TIFF: SamplesPerPixel too small for the photometric interpretation");

            if (IsLogLuv)
            {
                // The tags describe the caller side format of the writer; the stored data always is 16-bit log
                // luminance (LogL) or a 32-bit LogLuv word per pixel.
                RawType = SampleType.Float32;
                if (Compression is 34676 or 34677)
                {
                    if (Spp != ColorSamples || Planar != 1) throw new ImageNotSupportedException("TIFF: SGILOG compressed data cannot have extra samples or planes");
                    Kind = TiffChunkKind.LogLuv;
                }
                else
                {
                    // without the SGILOG codecs the samples are plain floats: luminance, or CIE XYZ
                    if (Format != 3 || !UniformBits || Bits != 32)
                        throw new ImageNotSupportedException("TIFF: LogLuv data without SGILOG compression needs 32-bit float samples");
                    Kind = TiffChunkKind.Samples;
                    _xyzPost = Photometric == 32845;
                }
            }
            else if (_formats != null)
            {
                if (!UniformBits || !(Bits is 8 or 16 or 32 or 64)) throw new ImageNotSupportedException("TIFF: differing sample formats need samples of 8, 16, 32 or 64 bits");
                for (int i = 0; i < Spp; i++)
                    if (_formats[i] == 3 && Bits == 8) throw new ImageFormatException("TIFF: 8-bit floating point samples");
                if (Compression is 32909 or 50001 or 34661 or 34887 or 52546 || IsJpeg || IsCcitt || IsJp2) throw new ImageNotSupportedException("TIFF: differing sample formats are not supported with this compression");
                RawType = SampleType.Float64;
                Kind = TiffChunkKind.Mixed;
            }
            else
            {
                RawType = Format switch
                {
                    1 when Bits <= 8 => SampleType.UInt8,
                    1 when Bits <= 16 => SampleType.UInt16,
                    1 when Bits <= 32 => SampleType.UInt32,
                    1 when Bits <= 64 => SampleType.UInt64,
                    2 when Bits <= 8 => SampleType.Int8,
                    2 when Bits <= 16 => SampleType.Int16,
                    2 when Bits <= 32 => SampleType.Int32,
                    2 when Bits <= 64 => SampleType.Int64,
                    3 when Bits == 16 && UniformBits => SampleType.Float16,
                    3 when (Bits == 24 || Bits == 32) && UniformBits => SampleType.Float32,
                    3 when Bits == 64 && UniformBits => SampleType.Float64,
                    _ => throw new ImageNotSupportedException($"TIFF: {Bits}-bit samples of format {Format} are not supported"),
                };
                bool whole = UniformBits && Bits is 8 or 16 or 32 or 64;
                if (Compression == 32909)
                {
                    // PixarLog stores 11-bit log values whatever the tags say (libtiff even rewrites the tags to
                    // "8 bit" when it closes a file); the tags, or the caller, pick the linear output format.
                    Kind = TiffChunkKind.PixarLog;
                    RawType = options.PixarLogOutput switch
                    {
                        TiffPixarLogOutput.UInt8 => SampleType.UInt8,
                        TiffPixarLogOutput.UInt16 => SampleType.UInt16,
                        TiffPixarLogOutput.Float32 => SampleType.Float32,
                        _ => RawType switch
                        {
                            SampleType.UInt8 or SampleType.UInt16 or SampleType.Float32 when whole => RawType,
                            SampleType.Int16 when whole => SampleType.UInt16,
                            _ => throw new ImageNotSupportedException($"TIFF: PixarLog data tagged as {Bits}-bit samples of format {Format} is not supported"),
                        },
                    };
                }
                else if (IsJpeg) Kind = TiffChunkKind.Jpeg;
                else if (Compression == 50001) Kind = TiffChunkKind.Webp;
                else if (IsJp2) Kind = TiffChunkKind.Jp2;
                else if (Compression == 34887) Kind = TiffChunkKind.Lerc;
                else if (Compression == 52546) Kind = TiffChunkKind.Jxl;
                else if (Format == 3 && Bits == 24) Kind = TiffChunkKind.Float24;
                else Kind = whole ? TiffChunkKind.Samples : TiffChunkKind.Packed;
            }
            Bps = RawImage.BytesPerSample(RawType);
            RawChannels = Kind == TiffChunkKind.LogLuv ? ColorSamples : Spp;
            OutType = RawType;
            int extra = Spp - ColorSamples;
            int kind = extra >= 1 && Extra.Length >= 1 ? Extra[0] : 0;
            HasAlpha = extra == 1 && (kind == 1 || kind == 2);
            AssociatedAlpha = HasAlpha && kind == 1;

            // Bit depth rule: samples keep their stored values in the next larger type. The one exception (kept from
            // the common viewer behaviour) are 1, 2 and 4 bit images, which are stretched to 0..255 by default.
            _scale = new bool[Spp];
            _inMax = new ulong[Spp];
            _outMax = Bps >= 8 ? ulong.MaxValue : (1UL << (Bps * 8)) - 1;
            bool anyScale = false;
            for (int i = 0; i < Spp; i++)
            {
                int b = BitsPer[i];
                _inMax[i] = b >= 64 ? ulong.MaxValue : (1UL << b) - 1;
                bool odd = !(b is 8 or 16 or 32 or 64);
                _scale[i] = Format == 1 && Photometric != 3 && odd && Kind == TiffChunkKind.Packed && options.BitScaling switch
                {
                    TiffBitScaling.FullRange => true,
                    TiffBitScaling.None => false,
                    _ => UniformBits && b is 1 or 2 or 4,
                };
                anyScale |= _scale[i];
            }
            SampleMax = anyScale || !UniformBits ? _outMax : _inMax[0];

            switch (Photometric)
            {
                case 3:
                    if (Format != 1 || Bits > 16) throw new ImageNotSupportedException("TIFF: palettes need unsigned indices of up to 16 bits");
                    OutType = SampleType.UInt8;
                    OutColor = HasAlpha ? ColorModel.Rgba : ColorModel.Rgb;
                    break;
                case 5:
                    if (Format != 1 || !UniformBits || (Bits != 8 && Bits != 16)) throw new ImageNotSupportedException("TIFF: CMYK needs 8 or 16 bit unsigned samples");
                    OutColor = !ConvertToRgb ? ColorModel.Other : HasAlpha ? ColorModel.Rgba : ColorModel.Rgb;
                    break;
                case 6:
                    if (Format != 1 || !UniformBits || Spp != 3 || (Bits != 8 && Bits != 16 && !(IsJpeg && Planar == 1) && !IsJp2))
                        throw new ImageNotSupportedException("TIFF: YCbCr needs three 8 or 16 bit samples (or JPEG compression)");
                    OutColor = ColorModel.Rgb;
                    break;
                case 8: case 9: case 10:
                    if (Format != 1 || !UniformBits || (Bits != 8 && Bits != 16)) throw new ImageNotSupportedException("TIFF: Lab needs 8 or 16 bit samples");
                    // (with extra samples that are not alpha: converted colour followed by the extras, as ColorModel.Other)
                    OutColor = !ConvertToRgb ? ColorModel.Other : HasAlpha ? ColorModel.Rgba : extra == 0 ? ColorModel.Rgb : ColorModel.Other;
                    break;
                case 32844:
                    OutColor = extra == 0 ? ColorModel.Gray : HasAlpha ? ColorModel.GrayAlpha : ColorModel.Other;
                    break;
                case 32845:
                    OutColor = !ConvertToRgb ? ColorModel.Other : extra == 0 ? ColorModel.Rgb : HasAlpha ? ColorModel.Rgba : ColorModel.Other;
                    break;
                default:
                    // Extra samples rule: exactly one extra sample tagged as alpha gives GrayAlpha/Rgba, any other
                    // surplus (unspecified extras, several extras) keeps ALL samples untouched as ColorModel.Other.
                    if (extra == 0) OutColor = ColorSamples == 1 ? ColorModel.Gray : ColorModel.Rgb;
                    else if (HasAlpha) OutColor = ColorSamples == 1 ? ColorModel.GrayAlpha : ColorModel.Rgba;
                    else OutColor = ColorModel.Other;
                    break;
            }
            OutChannels = OutColor == ColorModel.Other ? RawChannels : RawImage.ChannelCount(OutColor);

            if (Photometric == 6)
            {
                var sub = file.Integers(e, 530);
                if (sub != null && sub.Length >= 2) { SubH = (int)sub[0]; SubV = (int)sub[1]; }
                else { SubH = 2; SubV = 2; }
                if (!(SubH is 1 or 2 or 4) || !(SubV is 1 or 2 or 4)) throw new ImageFormatException("TIFF: invalid YCbCr subsampling");
                if (IsJp2)
                {
                    // the codestream carries its own component geometry; the samples are converted after decoding
                    if (Planar == 2 || Bits != 8) throw new ImageNotSupportedException("TIFF: JPEG 2000 compressed YCbCr needs 8-bit contiguous samples");
                    SubH = SubV = 1;
                }
                else if (Planar == 2) YccPlanar = true;
                else if (!IsJpeg)
                {
                    // Without JPEG, contiguous YCbCr is stored as packed blocks of (SubH*SubV luma, Cb, Cr).
                    if (Kind != TiffChunkKind.Samples) throw new ImageNotSupportedException("TIFF: YCbCr with this compression is not supported");
                    Kind = TiffChunkKind.YccPacked;
                }
            }
            if (IsCcitt && (Bits != 1 || Spp != 1)) throw new ImageFormatException("TIFF: CCITT compression needs bilevel data");
            if (Compression == 34661 && (Bits != 1 || Spp != 1 || Format != 1)) throw new ImageNotSupportedException("TIFF: JBIG compression needs bilevel data");
            if (Kind == TiffChunkKind.Lerc)
            {
                if (_complex || !UniformBits || !(Bits is 8 or 16 or 32 || (Bits == 64 && Format == 3)) || Photometric == 6)
                    throw new ImageNotSupportedException("TIFF: LERC compression needs 8, 16 or 32 bit integers or 32 / 64 bit floats");
                // LercParameters: LERC version, additional compression (0 none, 1 deflate, 2 zstd)
                var lp = file.Integers(e, 50674);
                _lercSecond = lp != null && lp.Length >= 2 ? (int)lp[1] : 0;
                if (_lercSecond < 0 || _lercSecond > 2) throw new ImageNotSupportedException($"TIFF: LERC with additional compression {_lercSecond} is not supported");
            }
            if (IsJp2 && (Format == 3 || !UniformBits || _complex)) throw new ImageNotSupportedException("TIFF: JPEG 2000 compression needs integer samples of one depth");
            if (IsJpeg && (Format != 1 || !UniformBits || Bits > 16)) throw new ImageNotSupportedException("TIFF: JPEG compression needs unsigned samples of up to 16 bits");
            if (Kind == TiffChunkKind.Webp && (Format != 1 || !UniformBits || Bits != 8 || Planar != 1 || Spp < 3 || Spp > 4))
                throw new ImageNotSupportedException("TIFF: WebP compression needs 8-bit RGB or RGBA");
        }

        long Uniform(long[]? v, long def, string name)
        {
            if (v == null || v.Length == 0) return def;
            int n = Math.Min(v.Length, Spp);
            for (int i = 1; i < n; i++)
                if (v[i] != v[0]) throw new ImageNotSupportedException($"TIFF: differing {name} per sample is not supported");
            return v[0];
        }

        /// <summary>
        /// Header level description of the default decode (turned upright). Orientation is the stored tag, resolution and
        /// the ICC flag are what Decode reports in the metadata; BitsPerSample is the stored depth (the largest one if
        /// the samples differ), 8 for palette images.
        /// </summary>
        public ImageInfo Info(int frameCount)
        {
            var m = ReadMetadata(false);
            return ExifTags.Upright(new ImageInfo(ImageFormat.Tiff, Width, Height, OutChannels, OutType, OutColor, frameCount)
            {
                BitsPerSample = Photometric == 3 ? 8 : _complex ? Bits * 2 : Bits,   // (a complex sample counts as one)
                Orientation = m.Orientation,
                DpiX = m.DpiX,
                DpiY = m.DpiY,
                HasIccProfile = m.IccProfile != null,
            });
        }

        /// <summary>The Orientation tag, 0 if absent or invalid.</summary>
        public int Orientation => ReadMetadata(false).Orientation;

        int PlaneSamples => Planar == 2 ? 1 : Spp;

        /// <summary>Bytes of one stored row of a chunk that is <paramref name="cw"/> samples wide (Samples / Packed / Float24 kinds).</summary>
        long RowBytes(int cw, int plane) => Kind switch
        {
            TiffChunkKind.Samples => (long)cw * PlaneSamples * Bps,
            TiffChunkKind.Float24 => (long)cw * PlaneSamples * 3,
            TiffChunkKind.Mixed => (long)cw * PlaneSamples * (Bits / 8),
            _ => ((long)cw * (Planar == 2 ? BitsPer[plane] : _pixelBits) + 7) / 8,
        };

        long YccBlockRowBytes => (long)((ChunkW + SubH - 1) / SubH) * (SubH * SubV + 2) * Bps;

        /// <summary>Upper bound for the decompressed size of one chunk.</summary>
        long MaxChunkBytes() => Kind switch
        {
            TiffChunkKind.YccPacked => YccBlockRowBytes * ((ChunkH + SubV - 1) / SubV),
            TiffChunkKind.LogLuv => (long)ChunkW * ChunkH * 4,
            TiffChunkKind.PixarLog => (long)ChunkW * ChunkH * Spp * Math.Max(2, Bps),
            TiffChunkKind.Jpeg or TiffChunkKind.Webp or TiffChunkKind.Jp2 or TiffChunkKind.Lerc or TiffChunkKind.Jxl => (long)ChunkW * ChunkH * Spp * Bps,
            _ => RowBytes(ChunkW, 0) * ChunkH,
        };

        void EnsureDecodable()
        {
            switch (Compression)
            {
                case 1: case 5: case 8: case 32946: case 32773: case 2: case 3: case 4: case 6: case 7:
                case 50000: case 34925: case 50001: case 32909:
                case 34661: case 34712: case 33003: case 33004: case 33005: case 34887: case 34892: case 52546:
                    break;
                case 32809:
                    if (Bits != 4 || Spp != 1) throw new ImageFormatException("TIFF: ThunderScan compression needs 4-bit single sample data");
                    break;
                case 34676:
                    if (!IsLogLuv) throw new ImageFormatException("TIFF: SGILOG compression needs a LogL / LogLuv photometric interpretation");
                    break;
                case 34677:
                    if (Photometric != 32845) throw new ImageFormatException("TIFF: SGILOG24 compression needs the LogLuv photometric interpretation");
                    break;
                default: throw new ImageNotSupportedException($"TIFF: compression {Compression} is not supported");
            }
            bool plain = Kind is TiffChunkKind.Samples or TiffChunkKind.Float24 && !IsCcitt && Kind != TiffChunkKind.YccPacked;
            if (Predictor == 1) { }
            else if (!plain || YccPlanar)
            {
                // integers of odd depths: differences modulo 2^bits, undone while unpacking
                if (!(Kind == TiffChunkKind.Packed && !IsCcitt && Predictor == 2))
                    throw new ImageNotSupportedException("TIFF: predictor not supported with this compression");
            }
            else if (Predictor is 2 or 34892 or 34893) { }
            else if (Predictor is 3 or 34894 or 34895)
            {
                if (Format != 3) throw new ImageFormatException("TIFF: floating point predictor on integer data");
            }
            else throw new ImageNotSupportedException($"TIFF: predictor {Predictor} is not supported");

            if (YccPlanar && Kind != TiffChunkKind.Samples && Kind != TiffChunkKind.Jpeg)
                throw new ImageNotSupportedException("TIFF: planar YCbCr with this compression is not supported");
            // Kinds that go through intermediate buffers or codecs with 32-bit sizes
            if (!(Kind is TiffChunkKind.Samples or TiffChunkKind.Packed or TiffChunkKind.Float24 or TiffChunkKind.Mixed) && MaxChunkBytes() > int.MaxValue - 64)
                throw new ImageNotSupportedException("TIFF: strips or tiles larger than 2 GB are not supported with this compression");
        }

        void LoadChunks()
        {
            var e = Entries;
            int total = (_oldJpeg && !Tiled ? _storedStrips : ChunksPerPlane) * (Planar == 2 ? Spp : 1);
            var offs = File.Integers(e, Tiled ? 324 : 273) ?? (Tiled ? File.Integers(e, 273) : null);
            if (offs == null) throw new ImageFormatException("TIFF: missing strip/tile offsets");
            if (offs.Length < total) throw new ImageFormatException("TIFF: too few strip/tile offsets");
            var cnts = File.Integers(e, Tiled ? 325 : 279) ?? (Tiled ? File.Integers(e, 279) : null);
            long len = File.Src.Length;
            bool noCounts = cnts == null || cnts.Length < total;
            if (cnts == null || noCounts) cnts = new long[total];
            long[]? sorted = null;
            long sum = 0; int empty = 0;
            long maxChunk = MaxChunkBytes();
            for (int i = 0; i < total; i++)
            {
                long c = cnts[i], o = offs[i];
                if (o == 0 && !noCounts) { cnts[i] = 0; empty++; continue; } // sparse file (GDAL): the chunk is not stored
                // a tile without bytes is a tile that is not there, wherever its offset points (GDAL reads its own
                // files that way); only strips get the recovery below, as in libtiff
                if (c == 0 && Tiled && !noCounts) { empty++; continue; }
                if (o <= 0 || o > len || c < 0) throw new ImageFormatException("TIFF: strip/tile data outside of the file");
                if (c == 0)
                {
                    // StripByteCounts missing or zero (old writers; libtiff recovers the same way):
                    // the chunk extends to the next chunk or the end of the file.
                    if (sorted == null) { sorted = (long[])offs.Clone(); Array.Sort(sorted); }
                    int k = Array.BinarySearch(sorted, o);
                    while (k >= 0 && k < sorted.Length && sorted[k] <= o) k++;
                    long end = k >= 0 && k < sorted.Length ? Math.Min(sorted[k], len) : len;
                    c = end - o;
                    if (Compression == 1) c = Math.Min(c, maxChunk);
                    cnts[i] = c;
                    if (c == 0) { empty++; continue; }
                }
                if (c > len - o) throw new ImageFormatException("TIFF: strip/tile data outside of the file");
                sum += c;
            }
            _offsets = offs; _counts = cnts;

            // A header can claim any size. Refuse images that cannot possibly come out of the stored bytes
            // before allocating for them.
            double raw = Math.Ceiling((double)Width * _pixelBits / 8) * Height; // double: a lying header may overflow long
            if (_oldJpeg)
            {
                // old-style JPEG may keep the whole stream outside of the strips; checked when the stream is located
            }
            else if (IsCcitt)
            {
                // every coded row needs at least one bit
                if (Height > sum * 8 + (long)empty * ChunkH) throw new ImageFormatException("TIFF: image size inconsistent with the data size");
            }
            else
            {
                long ratio = Compression switch { 1 => 1, 32773 => 64, 8 or 32946 => 1040, 50000 or 34925 => 1 << 17, _ => 4096 };
                long allowed = sum > long.MaxValue / (1 << 18) ? long.MaxValue : sum * ratio + 1024;
                if (empty > 0) allowed += (long)Math.Min((double)empty * maxChunk, 256L << 20);
                if (Kind == TiffChunkKind.YccPacked || YccPlanar) raw /= 3; // at least one luma byte per pixel
                if (raw > allowed) throw new ImageFormatException("TIFF: image size inconsistent with the data size");
            }

            if (Photometric == 3)
            {
                var cm = File.Integers(e, 320);
                if (cm == null || cm.Length < 3 << Bits) throw new ImageFormatException("TIFF: missing or short ColorMap");
                _colorMap = Array.ConvertAll(cm, x => (ushort)x);
            }
            if (Compression is 7 or 34892)
            {
                _jpegTables = File.Bytes(e, 347) ?? Array.Empty<byte>();
                _jpegPrepared = TiffJpegBridge.Prepare(_jpegTables);
            }
            if (_oldJpeg) _ojpeg = new TiffOldJpeg(this, offs, cnts!, _storedStrips);
            if (IsCcitt) _t4Options = File.Integer(e, Compression == 3 ? 292 : 293, 0);
            if (_oldJpeg && Photometric == 6 && !YccPlanar)
            {
                // Old-style JPEG data is YCbCr as the TIFF tags describe it. With the default (full range) reference
                // values that is what the JPEG decoder converts; anything else - studio range data from old scanners
                // and NeXT software - goes through the TIFF conversion.
                var rb = File.Doubles(e, 532);
                var co = File.Doubles(e, 529);
                bool fullRange = rb == null || rb.Length < 6 || (rb[0] == 0 && rb[1] == 255 && rb[2] == 128 && rb[3] == 255 && rb[4] == 128 && rb[5] == 255);
                bool rec601 = co == null || co.Length < 3 || (Math.Abs(co[0] - 0.299) < 1e-3 && Math.Abs(co[1] - 0.587) < 1e-3 && Math.Abs(co[2] - 0.114) < 1e-3);
                _ojpegTiffYcc = !fullRange || !rec601;
            }
            if (Photometric == 6 && (Kind == TiffChunkKind.YccPacked || YccPlanar || _ojpegTiffYcc)) BuildYccTables();
            if (IsJp2 && (Photometric == 6 || Compression == 33003) && Bps == 1 && Spp >= 3) BuildYccTables();
        }

        void BuildYccTables()
        {
            var co = File.Doubles(Entries, 529);
            double lr = 0.299, lg = 0.587, lb = 0.114;
            if (co != null && co.Length >= 3 && co[1] > 0) { lr = co[0]; lg = co[1]; lb = co[2]; }
            var rb = File.Doubles(Entries, 532);
            if (rb == null || rb.Length < 6 || rb[1] == rb[0] || rb[3] == rb[2] || rb[5] == rb[4])
                rb = Bps == 2 ? new double[] { 0, 65535, 32768, 65535, 32768, 65535 } : new double[] { 0, 255, 128, 255, 128, 255 };
            if (Bps == 2)
            {
                // 16 bit: no tables, the same formulas per pixel (scale and offset per component, then the matrix)
                _ycc16 = new[]
                {
                    rb[0], 65535.0 / (rb[1] - rb[0]), rb[2], 32767.0 / (rb[3] - rb[2]), rb[4], 32767.0 / (rb[5] - rb[4]),
                    2 - 2 * lr, 2 - 2 * lb, (2 - 2 * lr) * lr / lg, (2 - 2 * lb) * lb / lg,
                };
                return;
            }
            _yccY = new float[256]; _yccCrR = new float[256]; _yccCbB = new float[256]; _yccCrG = new float[256]; _yccCbG = new float[256];
            for (int i = 0; i < 256; i++)
            {
                double y = (i - rb[0]) * 255.0 / (rb[1] - rb[0]);
                double cb = (i - rb[2]) * 127.0 / (rb[3] - rb[2]);
                double cr = (i - rb[4]) * 127.0 / (rb[5] - rb[4]);
                _yccY[i] = (float)y;
                _yccCrR[i] = (float)(cr * (2 - 2 * lr));
                _yccCbB[i] = (float)(cb * (2 - 2 * lb));
                _yccCrG[i] = (float)(-cr * (2 - 2 * lr) * lr / lg);
                _yccCbG[i] = (float)(-cb * (2 - 2 * lb) * lb / lg);
            }
        }

        /// <summary>
        /// Chunks that are not stored (sparse files, a GDAL convention) read as GDAL's no-data value where the file
        /// names one (tag 42113), as zero otherwise.
        /// </summary>
        void FillSparse(Array raw, int[] list, int n)
        {
            if (Kind != TiffChunkKind.Samples || _oldJpeg) return;
            bool sparse = false;
            for (int i = 0; i < n && !sparse; i++) sparse = _counts[list[i]] == 0;
            if (!sparse) return;
            string? text = null;
            foreach (var en in Entries)
                if (en.Tag == 42113 && en.Type == 2 && en.Count <= 64) text = File.ReadValue(en) as string;
            if (text == null || !double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v))
            {
                if (text == null || !text.Trim().Equals("nan", StringComparison.OrdinalIgnoreCase)) return;
                v = double.NaN;
            }
            if (v == 0) return;
            bool whole = v == Math.Floor(v);
            switch (RawType)
            {
                case SampleType.UInt8: if (whole && v >= 0 && v <= byte.MaxValue) Array.Fill((byte[])raw, (byte)v); break;
                case SampleType.Int8: if (whole && v >= sbyte.MinValue && v <= sbyte.MaxValue) Array.Fill((sbyte[])raw, (sbyte)v); break;
                case SampleType.UInt16: if (whole && v >= 0 && v <= ushort.MaxValue) Array.Fill((ushort[])raw, (ushort)v); break;
                case SampleType.Int16: if (whole && v >= short.MinValue && v <= short.MaxValue) Array.Fill((short[])raw, (short)v); break;
                case SampleType.UInt32: if (whole && v >= 0 && v <= uint.MaxValue) Array.Fill((uint[])raw, (uint)v); break;
                case SampleType.Int32: if (whole && v >= int.MinValue && v <= int.MaxValue) Array.Fill((int[])raw, (int)v); break;
                case SampleType.Float32: Array.Fill((float[])raw, (float)v); break;
                case SampleType.Float64: Array.Fill((double[])raw, v); break;
            }
        }

        public RawImage Decode(int rx, int ry, int rw, int rh, TiffDecodeOptions opts)
        {
            if (rx < 0 || ry < 0 || rw <= 0 || rh <= 0 || rx > Width - rw || ry > Height - rh)
                throw new TiffArgumentException("region", "TIFF: region lies outside of the image");
            EnsureDecodable();
            if ((long)rw * rh * RawChannels > Array.MaxLength || (long)rw * rh * OutChannels > Array.MaxLength)
                throw new ImageNotSupportedException("TIFF: image too large to decode into a single array");
            LoadChunks();

            _rx = rx; _ry = ry; _rw = rw; _rh = rh;
            _dstRowBytes = (long)rw * RawChannels * Bps;
            var raw = RawImage.CreateArray(RawType, rw * rh * RawChannels);

            // chunks intersecting the region
            int cx0 = rx / ChunkW, cx1 = (rx + rw - 1) / ChunkW, cy0 = ry / ChunkH, cy1 = (ry + rh - 1) / ChunkH;
            int planes = Planar == 2 ? Spp : 1;
            var list = new int[(cx1 - cx0 + 1) * (cy1 - cy0 + 1) * planes];
            int n = 0;
            for (int p = 0; p < planes; p++)
                for (int cy = cy0; cy <= cy1; cy++)
                    for (int cx = cx0; cx <= cx1; cx++)
                        list[n++] = p * ChunksPerPlane + cy * Across + cx;

            var pin = GCHandle.Alloc(raw, GCHandleType.Pinned);
            try
            {
                _dst = (byte*)pin.AddrOfPinnedObject();
                FillSparse(raw, list, n);
                if (opts.Parallel && n > 1 && _dstRowBytes * rh >= opts.ParallelThreshold)
                {
                    try
                    {
                        Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = opts.MaxDegreeOfParallelism > 0 ? opts.MaxDegreeOfParallelism : -1, CancellationToken = opts.CancellationToken },
                            i => { opts.CancellationToken.ThrowIfCancellationRequested(); DecodeChunk(list[i]); });
                    }
                    catch (AggregateException ae)
                    {
                        foreach (var inner in ae.Flatten().InnerExceptions)
                            if (inner is ImageFormatException || inner is ImageNotSupportedException || inner is OperationCanceledException)
                                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();
                        throw new ImageFormatException("TIFF: corrupt image data", ae.InnerException ?? ae);
                    }
                }
                else
                    for (int i = 0; i < n; i++) { opts.CancellationToken.ThrowIfCancellationRequested(); DecodeChunk(list[i]); }
                if (YccPlanar)
                {
                    if (Bps == 2) YccInPlace16((ushort*)_dst, (long)rw * rh);
                    else YccInPlace(_dst, (long)rw * rh);
                }
                if (_xyzPost && ConvertToRgb)
                {
                    float* f = (float*)_dst;
                    for (long i = 0, np = (long)rw * rh; i < np; i++, f += RawChannels) TiffColor.XyzToLinearRgb(ref f[0], ref f[1], ref f[2]);
                }
            }
            finally
            {
                _dst = null;
                pin.Free();
            }

            var data = TiffColor.Finish(this, raw, rw, rh, _colorMap);
            return new RawImage(rw, rh, OutColor, data, OutChannels, ReadMetadata());
        }

        /// <param name="full">false: header level only (no EXIF blob, the ICC profile only as a non-null marker).</param>
        ImageMetadata ReadMetadata(bool full = true)
        {
            var m = new ImageMetadata();
            var e = Entries;
            try
            {
                long o = File.Integer(e, 274, 0);
                if (o >= 1 && o <= 8) m.Orientation = (int)o;
                long unit = File.Integer(e, 296, 2);
                double f = unit == 2 ? 1 : unit == 3 ? 2.54 : 0;
                var xr = File.Doubles(e, 282); var yr = File.Doubles(e, 283);
                if (xr != null && xr.Length > 0 && double.IsFinite(xr[0]) && xr[0] > 0) m.DpiX = xr[0] * f;
                if (yr != null && yr.Length > 0 && double.IsFinite(yr[0]) && yr[0] > 0) m.DpiY = yr[0] * f;
                if (!full)
                {
                    // same test as below without reading the profile: a Byte/Undefined tag with content inside the file
                    if (TiffFile.Find(e, 34675, out var ie) && (ie.Type == 1 || ie.Type == 7) && ie.Count > 0
                        && ie.ValueOffset >= 0 && ie.ValueOffset <= File.Src.Length && ie.Count <= File.Src.Length - ie.ValueOffset)
                        m.IccProfile = Array.Empty<byte>();
                    return m;
                }
                var icc = File.Bytes(e, 34675);
                if (icc != null && icc.Length > 0) m.IccProfile = icc;
                var xmp = File.Bytes(e, 700);
                if (xmp != null && xmp.Length > 0) m.Xmp = xmp;
                // EXIF / GPS directories, repackaged as the standalone EXIF blob other formats carry
                if (TiffFile.Find(e, 34665, out _) || TiffFile.Find(e, 34853, out _)) m.Exif = TiffExif.Extract(File, _ifdOffset);
            }
            catch (ImageFormatException) { } // broken metadata must not make the pixels unreadable
            return m;
        }

        // ------------------------------------------------------------------------------------------ chunks

        void DecodeChunk(int index)
        {
            int plane = index / ChunksPerPlane, ci = index % ChunksPerPlane;
            int x0 = ci % Across * ChunkW, y0 = ci / Across * ChunkH;
            int rows = Tiled ? ChunkH : Math.Min(ChunkH, Height - y0);
            int ix0 = Math.Max(x0, _rx), ix1 = (int)Math.Min(Math.Min((long)x0 + ChunkW, Width), (long)_rx + _rw);
            int iy0 = Math.Max(y0, _ry), iy1 = (int)Math.Min(Math.Min((long)y0 + rows, Height), (long)_ry + _rh);
            if (ix0 >= ix1 || iy0 >= iy1) return;
            long count = _counts[index], offset = _offsets[index];
            if (count == 0 && !_oldJpeg) return; // sparse chunk: stays zero
            int ps = PlaneSamples;
            // the chroma planes of planar YCbCr are stored subsampled
            int sh = 1, sv = 1;
            if (YccPlanar && plane > 0) { sh = SubH; sv = SubV; }
            int cw = (ChunkW + sh - 1) / sh, crows = (rows + sv - 1) / sv;
            // stored rows the region needs: [r0, r1)
            int r0 = (iy0 - y0) / sv, r1 = (iy1 - y0 + sv - 1) / sv;
            var g = new Geometry { Plane = plane, X0 = x0, Y0 = y0, Ix0 = ix0, Ix1 = ix1, Iy0 = iy0, Iy1 = iy1, Sh = sh, Sv = sv, Ps = ps };

            switch (Kind)
            {
                case TiffChunkKind.Samples:
                case TiffChunkKind.Packed:
                case TiffChunkKind.Float24:
                case TiffChunkKind.Mixed:
                {
                    long rowBytes = RowBytes(cw, plane);
                    bool fullRows = Kind == TiffChunkKind.Samples && sh == 1 && !Tiled && Planar == 1 && _rx == 0 && _rw == Width;
                    if (Compression == 1)
                    {
                        // uncompressed: only the rows the region needs are read at all
                        long skip = r0 * rowBytes, size = (r1 - r0) * rowBytes;
                        if (count < skip + size) throw new ImageFormatException("TIFF: strip/tile data is too short");
                        if (fullRows)
                        {
                            byte* d = _dst + (iy0 - _ry) * _dstRowBytes;
                            ReadRaw(offset + skip, d, size);
                            Unpredict(d, r1 - r0, rowBytes, cw, ps);
                            return;
                        }
                        byte* t = (byte*)NativeMemory.Alloc((nuint)size);
                        try
                        {
                            ReadRaw(offset + skip, t, size);
                            Unpredict(t, r1 - r0, rowBytes, cw, ps);
                            g.BufRow0 = r0;
                            Transfer(t, rowBytes, g);
                        }
                        finally { NativeMemory.Free(t); }
                        return;
                    }
                    // compressed: decode from the top of the chunk, but no further than the last needed row
                    long need = r1 * rowBytes;
                    CheckPlausible(need, count, r1);
                    if (fullRows && y0 >= _ry)
                    {
                        byte* d = _dst + (y0 - _ry) * _dstRowBytes;
                        Decompress(offset, count, d, need, rowBytes);
                        Unpredict(d, r1, rowBytes, cw, ps);
                        return;
                    }
                    byte* b = (byte*)NativeMemory.Alloc((nuint)need);
                    try
                    {
                        Decompress(offset, count, b, need, rowBytes);
                        Unpredict(b, r1, rowBytes, cw, ps);
                        Transfer(b, rowBytes, g);
                    }
                    finally { NativeMemory.Free(b); }
                    return;
                }
                case TiffChunkKind.YccPacked:
                {
                    int blockRows = (iy1 - y0 + SubV - 1) / SubV;
                    int decoded = Math.Min(rows, blockRows * SubV);
                    long need = YccBlockRowBytes * blockRows;
                    if (Compression != 1) CheckPlausible(need, count, decoded);
                    byte* b = (byte*)NativeMemory.Alloc((nuint)(need + (long)ChunkW * decoded * 3 * Bps));
                    try
                    {
                        byte* rgb = b + need;
                        Decompress(offset, count, b, need, YccBlockRowBytes);
                        if (Bps == 2)
                        {
                            if (File.BigEndian) TiffPredict.SwapBytes(b, need, 2);
                            YccToRgb16((ushort*)b, (ushort*)rgb, decoded);
                        }
                        else YccToRgb(b, rgb, decoded);
                        g.Ps = 3;
                        Transfer(rgb, (long)ChunkW * 3 * Bps, g, TiffChunkKind.Samples);
                    }
                    finally { NativeMemory.Free(b); }
                    return;
                }
                case TiffChunkKind.Jpeg:
                {
                    long size = (long)cw * crows * ps * Bps;
                    if (!_oldJpeg) CheckPlausible(size, count, crows);
                    byte[]? srcRent = null;
                    byte* b = (byte*)NativeMemory.Alloc((nuint)size);
                    try
                    {
                        ReadOnlySpan<byte> src = _oldJpeg ? _ojpeg!.BuildStream(plane, Tiled ? index : -1, cw, crows)
                            : count > int.MaxValue ? throw new ImageNotSupportedException("TIFF: strips or tiles larger than 2 GB are not supported with this compression")
                            : File.Src.Get(offset, (int)count, ref srcRent);
                        // DNG lossy JPEG holds colour as baseline JPEG does: YCbCr
                        bool ycc = (Photometric == 6 || (Compression == 34892 && ps == 3)) && !YccPlanar && !_ojpegTiffYcc;
                        if (Bps == 1) TiffJpegBridge.Decode8(_jpegPrepared, _jpegTables, src, cw, crows, ps, ycc, new Span<byte>(b, (int)size));
                        else TiffJpegBridge.Decode16(_jpegPrepared, src, cw, crows, ps, ycc, new Span<ushort>(b, (int)(size / 2)));
                        if (_ojpegTiffYcc && Bps == 1) YccInPlace(b, (long)cw * crows);
                        Transfer(b, (long)cw * ps * Bps, g, TiffChunkKind.Samples);
                    }
                    finally
                    {
                        NativeMemory.Free(b);
                        if (srcRent != null) ArrayPool<byte>.Shared.Return(srcRent);
                    }
                    return;
                }
                case TiffChunkKind.Webp:
                {
                    if (count > int.MaxValue) throw new ImageNotSupportedException("TIFF: strips or tiles larger than 2 GB are not supported with this compression");
                    byte[]? srcRent = null;
                    try
                    {
                        var img = Webp.Decode(File.Src.Get(offset, (int)count, ref srcRent));
                        if (img.SampleType != SampleType.UInt8 || img.Width < ChunkW || img.Height < rows || img.Channels < 3)
                            throw new ImageFormatException("TIFF: WebP strip/tile does not match the image layout");
                        var px = img.GetData<byte>();
                        int ic = img.Channels;
                        long size = (long)ChunkW * rows * Spp;
                        byte* b = (byte*)NativeMemory.Alloc((nuint)size);
                        try
                        {
                            for (int y = 0; y < rows; y++)
                            {
                                byte* d = b + (long)y * ChunkW * Spp;
                                int si = y * img.Width * ic;
                                for (int x = 0; x < ChunkW; x++, si += ic, d += Spp)
                                {
                                    d[0] = px[si]; d[1] = px[si + 1]; d[2] = px[si + 2];
                                    if (Spp == 4) d[3] = ic == 4 ? px[si + 3] : (byte)255;
                                }
                            }
                            Transfer(b, (long)ChunkW * Spp, g, TiffChunkKind.Samples);
                        }
                        finally { NativeMemory.Free(b); }
                    }
                    finally
                    {
                        if (srcRent != null) ArrayPool<byte>.Shared.Return(srcRent);
                    }
                    return;
                }
                case TiffChunkKind.Jp2:
                {
                    if (count > int.MaxValue) throw new ImageNotSupportedException("TIFF: strips or tiles larger than 2 GB are not supported with this compression");
                    byte[]? srcRent = null;
                    long rowBytes = (long)cw * ps * Bps, size = rowBytes * rows;
                    byte* b = (byte*)NativeMemory.Alloc((nuint)Math.Max(size, 1));
                    try
                    {
                        // (decoding runs in parallel over the strips / tiles already)
                        var planes = Jp2.DecodeCodestreamPlanes(File.Src.Get(offset, (int)count, ref srcRent), new Jp2DecodeOptions { Parallel = false, MaxPixels = Math.Max((long)cw * ChunkH, 1L << 20) * 4 });
                        Jp2ToSamples(planes, b, cw, rows, ps);
                        Transfer(b, rowBytes, g, TiffChunkKind.Samples);
                    }
                    finally
                    {
                        NativeMemory.Free(b);
                        if (srcRent != null) ArrayPool<byte>.Shared.Return(srcRent);
                    }
                    return;
                }
                case TiffChunkKind.Lerc:
                {
                    if (count > int.MaxValue) throw new ImageNotSupportedException("TIFF: strips or tiles larger than 2 GB are not supported with this compression");
                    byte[]? srcRent = null;
                    long rowBytes = (long)cw * ps * Bps, size = rowBytes * rows;
                    byte* b = (byte*)NativeMemory.AllocZeroed((nuint)Math.Max(size, 1));
                    try
                    {
                        var blob = File.Src.Get(offset, (int)count, ref srcRent);
                        try
                        {
                            // GDAL's LERC_DEFLATE / LERC_ZSTD: the blob compressed once more
                            if (_lercSecond == 1) blob = Zlib.Decompress(blob, (int)Math.Min(size, 1 << 28));
                            else if (_lercSecond == 2) blob = Zstd.Decompress(blob);
                        }
                        catch (Exception ex) when (ex is InvalidDataException || ex is IOException)
                        {
                            throw new ImageFormatException("TIFF: corrupt deflate data", ex);
                        }
                        LercToSamples(blob, b, cw, rows, ps);
                        Transfer(b, rowBytes, g, TiffChunkKind.Samples);
                    }
                    finally
                    {
                        NativeMemory.Free(b);
                        if (srcRent != null) ArrayPool<byte>.Shared.Return(srcRent);
                    }
                    return;
                }
                case TiffChunkKind.Jxl:
                {
                    if (count > int.MaxValue) throw new ImageNotSupportedException("TIFF: strips or tiles larger than 2 GB are not supported with this compression");
                    byte[]? srcRent = null;
                    long rowBytes = (long)cw * ps * Bps, size = rowBytes * rows;
                    byte* b = (byte*)NativeMemory.AllocZeroed((nuint)Math.Max(size, 1));
                    try
                    {
                        // (decoding runs in parallel over the strips / tiles already)
                        var img = Jxl.Decode(File.Src.Get(offset, (int)count, ref srcRent),
                            new JxlDecodeOptions { Multithreaded = false, ApplyOrientation = false, MaxPixels = Math.Max((long)cw * ChunkH, 1L << 20) * 4 });
                        // (some writers store the edge tiles cropped to the image)
                        JxlToSamples(img, b, cw, rows, ps, Math.Min(cw, Width - x0), Math.Min(rows, Height - y0));
                        Transfer(b, rowBytes, g, TiffChunkKind.Samples);
                    }
                    finally
                    {
                        NativeMemory.Free(b);
                        if (srcRent != null) ArrayPool<byte>.Shared.Return(srcRent);
                    }
                    return;
                }
                case TiffChunkKind.LogLuv:
                {
                    if (count > int.MaxValue) throw new ImageNotSupportedException("TIFF: strips or tiles larger than 2 GB are not supported with this compression");
                    bool luv24 = Compression == 34677;
                    int bpp = luv24 ? 3 : Photometric == 32845 ? 4 : 2, rc = RawChannels;
                    int need = iy1 - y0;
                    long enc = (long)ChunkW * need * bpp;
                    CheckPlausible(enc, count, need);
                    byte[]? srcRent = null;
                    byte* b = (byte*)NativeMemory.Alloc((nuint)(enc + (long)ChunkW * need * rc * 4));
                    try
                    {
                        var src = File.Src.Get(offset, (int)count, ref srcRent);
                        if (luv24)
                        {
                            // 24-bit LogLuv is stored as it is, three bytes per pixel
                            if (src.Length < enc) throw new ImageFormatException("TIFF: LogLuv data is truncated");
                            src.Slice(0, (int)enc).CopyTo(new Span<byte>(b, (int)enc));
                        }
                        else TiffSgiLog.Decode(src, b, ChunkW, need, bpp);
                        float* f = (float*)(b + enc);
                        long pixels = (long)ChunkW * need;
                        if (bpp == 2)
                            for (long i = 0; i < pixels; i++) f[i] = TiffSgiLog.LogL16ToY(b[i * 2] << 8 | b[i * 2 + 1]);
                        else
                            for (long i = 0; i < pixels; i++)
                            {
                                float x, y, z;
                                if (luv24) TiffSgiLog.LogLuv24ToXyz((uint)(b[i * 3] << 16 | b[i * 3 + 1] << 8 | b[i * 3 + 2]), out x, out y, out z);
                                else TiffSgiLog.LogLuv32ToXyz((uint)(b[i * 4] << 24 | b[i * 4 + 1] << 16 | b[i * 4 + 2] << 8 | b[i * 4 + 3]), out x, out y, out z);
                                if (ConvertToRgb) TiffColor.XyzToLinearRgb(ref x, ref y, ref z);
                                f[i * 3] = x; f[i * 3 + 1] = y; f[i * 3 + 2] = z;
                            }
                        g.Ps = rc;
                        Transfer((byte*)f, (long)ChunkW * rc * 4, g, TiffChunkKind.Samples);
                    }
                    finally
                    {
                        NativeMemory.Free(b);
                        if (srcRent != null) ArrayPool<byte>.Shared.Return(srcRent);
                    }
                    return;
                }
                case TiffChunkKind.PixarLog:
                {
                    if (count > int.MaxValue) throw new ImageNotSupportedException("TIFF: strips or tiles larger than 2 GB are not supported with this compression");
                    int need = iy1 - y0, perRow = ChunkW * ps;
                    long enc = (long)need * perRow * 2, outRow = (long)perRow * Bps;
                    if (enc > int.MaxValue) throw new ImageNotSupportedException("TIFF: strips or tiles larger than 2 GB are not supported with this compression");
                    CheckPlausible(enc, count, need);
                    byte[]? srcRent = null;
                    byte* b = (byte*)NativeMemory.Alloc((nuint)(enc + need * outRow));
                    try
                    {
                        int got;
                        try { got = Zlib.Decompress(File.Src.Get(offset, (int)count, ref srcRent), new Span<byte>(b, (int)enc)); }
                        catch (Exception ex) when (ex is InvalidDataException || ex is IOException)
                        {
                            throw new ImageFormatException("TIFF: corrupt PixarLog data", ex);
                        }
                        if (got < enc) throw new ImageFormatException("TIFF: PixarLog data is truncated");
                        if (File.BigEndian) TiffPredict.SwapBytes(b, enc, 2);
                        byte* o = b + enc;
                        for (int y = 0; y < need; y++)
                            TiffPixarLog.Row((ushort*)(b + (long)y * perRow * 2), o + y * outRow, perRow, ps, RawType);
                        Transfer(o, outRow, g, TiffChunkKind.Samples);
                    }
                    finally
                    {
                        NativeMemory.Free(b);
                        if (srcRent != null) ArrayPool<byte>.Shared.Return(srcRent);
                    }
                    return;
                }
            }
        }

        /// <summary>
        /// A strip/tile cannot decompress to more than the best ratio of its codec allows. Checked before memory is
        /// allocated for it, so that a header claiming huge tiles over a few bytes of data is refused cheaply.
        /// </summary>
        void CheckPlausible(long decoded, long count, int rows)
        {
            if (IsCcitt)
            {
                // every coded row takes at least one bit; and nobody scans pages millions of pixels wide
                if (rows > count * 8 + 8 || ChunkW > 1 << 22) throw new ImageFormatException("TIFF: strip/tile size inconsistent with its data size");
                return;
            }
            long ratio = Compression switch { 32773 => 64, 8 or 32946 or 32909 => 1040, 50000 or 34925 => 1 << 17, 34677 => 1, _ => 4096 };
            if (count < long.MaxValue / (1 << 18) && decoded > count * ratio + 4096)
                throw new ImageFormatException("TIFF: strip/tile size inconsistent with its data size");
        }

        /// <summary>Reads <paramref name="size"/> bytes (possibly more than 2 GB) of uncompressed data and applies FillOrder 2.</summary>
        void ReadRaw(long offset, byte* dst, long size)
        {
            for (long done = 0; done < size;)
            {
                int n = (int)Math.Min(size - done, 1 << 28);
                var d = new Span<byte>(dst + done, n);
                File.Src.Read(offset + done, d);
                if (FillOrder == 2) TiffPredict.ReverseBits(d, d);
                done += n;
            }
        }

        /// <summary>
        /// Copies a decoded JPEG XL strip / tile into cw x rows pixels of ps native samples. The codestream holds the
        /// samples as stored (integers with their coded values, or half floats); 8-bit codestreams may fill wider samples.
        /// </summary>
        void JxlToSamples(RawImage img, byte* dst, int cw, int rows, int ps, int needW, int needH)
        {
            if (img.Width < needW || img.Height < needH || img.Channels != ps)
                throw new ImageFormatException($"TIFF: JPEG XL strip/tile {img.Width}x{img.Height}x{img.Channels} does not match the {cw}x{rows}x{ps} layout");
            int w = img.Width, n = Math.Min(cw, w) * ps;
            for (int y = 0; y < Math.Min(rows, img.Height); y++)
            {
                int si = y * w * ps;
                long rowStart = (long)y * cw * ps;
                switch ((img.SampleType, RawType))
                {
                    case (SampleType.UInt8, SampleType.UInt8):
                        img.GetData<byte>().AsSpan(si, n).CopyTo(new Span<byte>(dst + rowStart, n)); break;
                    case (SampleType.UInt16, SampleType.UInt16):
                        img.GetData<ushort>().AsSpan(si, n).CopyTo(new Span<ushort>(dst + rowStart * 2, n)); break;
                    case (SampleType.UInt8, SampleType.UInt16):
                    {
                        var s = img.GetData<byte>(); var d = (ushort*)dst + rowStart;
                        for (int i = 0; i < n; i++) d[i] = s[si + i];
                        break;
                    }
                    case (SampleType.Float16, SampleType.Float16):
                        img.GetData<Half>().AsSpan(si, n).CopyTo(new Span<Half>(dst + rowStart * 2, n)); break;
                    case (SampleType.Float16, SampleType.Float32):
                    {
                        var s = img.GetData<Half>(); var d = (float*)dst + rowStart;
                        for (int i = 0; i < n; i++) d[i] = (float)s[si + i];
                        break;
                    }
                    case (SampleType.Float32, SampleType.Float32):
                        img.GetData<float>().AsSpan(si, n).CopyTo(new Span<float>(dst + rowStart * 4, n)); break;
                    default: throw new ImageNotSupportedException($"TIFF: JPEG XL strip/tile with {img.SampleType} samples in a {RawType} image");
                }
            }
        }

        /// <summary>
        /// Decodes one LERC blob into cw x rows pixels of ps native samples and applies its validity masks the way
        /// libtiff / GDAL do: invalid float samples become NaN; a blob holding one value less per pixel than the image
        /// has samples, in an 8-bit image whose last extra sample is unassociated alpha, gives that alpha (255 = valid).
        /// Invalid integer samples stay 0.
        /// </summary>
        void LercToSamples(ReadOnlySpan<byte> blob, byte* dst, int cw, int rows, int ps)
        {
            int n = cw * rows, bps = Bps;
            var masks = new byte[(long)n * ps];
            TiffForeignCodecs.LercDecode(blob, RawType, cw, rows, new Span<byte>(dst, n * ps * bps), ps, masks, out int bands, out int depth, out int maskCount);
            int channels = bands * depth;
            if (channels == ps)
            {
                if (maskCount == 0 || Format != 3) goto swap;
                if (maskCount != 1 && maskCount != bands) throw new ImageFormatException("TIFF: LERC blob with an unexpected number of masks");
                for (int c = 0; c < ps; c++)
                {
                    // one mask for all samples, or one per band (band b = samples b * depth ..)
                    var m = masks.AsSpan(maskCount == 1 ? 0 : c / depth * n, n);
                    if (bps == 4)
                    {
                        float* d = (float*)dst + c;
                        for (int i = 0; i < n; i++, d += ps) if (m[i] == 0) *(uint*)d = 0x7FC00000u;   // (the quiet NaN of C, as libtiff stores it)
                    }
                    else
                    {
                        double* d = (double*)dst + c;
                        for (int i = 0; i < n; i++, d += ps) if (m[i] == 0) *(ulong*)d = 0x7FF8000000000000ul;
                    }
                }
            }
            else if (channels == ps - 1 && bands == 1 && Planar == 1 && RawType == SampleType.UInt8 && Extra.Length > 0 && Extra[^1] == 2)
            {
                byte* d = dst + ps - 1;
                if (maskCount == 0) for (int i = 0; i < n; i++, d += ps) *d = 255;
                else for (int i = 0; i < n; i++, d += ps) *d = (byte)(masks[i] != 0 ? 255 : 0);
            }
            else throw new ImageFormatException("TIFF: LERC strip/tile does not match the image layout");
        swap:
            // libtiff swaps the samples of a big endian file before its codecs see them and after they return them,
            // so there a blob holds byte-swapped numbers (and what it fills in for invalid floats is swapped as well:
            // such samples come back as the swapped NaN pattern, as they do from libtiff)
            if (File.BigEndian && bps > 1) TiffPredict.SwapBytes(dst, (long)n * ps * bps, bps);
        }

        /// <summary>
        /// libtiff stores the bytes of a JBIG entity bit-reversed when FillOrder is 1 (the default) and as they are
        /// when it is 2 - the opposite of what the tag suggests, but its files are what exists. The entity's header
        /// tells whether a strip follows that convention: a single plane as wide as the image.
        /// </summary>
        bool JbigIsReversed(ReadOnlySpan<byte> bie)
        {
            if (bie.Length < 20) return FillOrder != 2;
            Span<byte> h = stackalloc byte[8], r = stackalloc byte[8];
            bie.Slice(0, 8).CopyTo(h);
            TiffPredict.ReverseBits(h, r);
            int w = ChunkW;
            static bool Fits(Span<byte> h, int w) => h[2] == 1 && h[3] == 0 && h[0] <= h[1] && (h[4] << 24 | h[5] << 16 | h[6] << 8 | h[7]) == w;
            bool byConvention = FillOrder != 2;
            return Fits(byConvention ? r : h, w) || !Fits(byConvention ? h : r, w) ? byConvention : !byConvention;
        }

        /// <summary>
        /// Interleaves the components of a JPEG 2000 codestream into native samples of the image's type: cw x rows
        /// pixels of ps samples. Subsampled components are replicated. Component values are taken as they are; only
        /// YCbCr data (Aperio's 33003, or the YCbCr photometric) that the codestream did not already convert itself
        /// becomes RGB here.
        /// </summary>
        void Jp2ToSamples(J2kPlanes planes, byte* dst, int cw, int rows, int ps)
        {
            var pl = planes.Planes;
            if (pl.Length < ps || planes.Width < cw || planes.Height < rows)
                throw new ImageFormatException("TIFF: JPEG 2000 strip/tile does not match the image layout");
            int bps = Bps;
            for (int c = 0; c < ps; c++)
            {
                var p = pl[c];
                if (p.Dx < 1 || p.Dy < 1 || (p.Data == null && p.Data64 == null)) throw new ImageFormatException("TIFF: JPEG 2000 strip/tile lacks a component");
                if (p.Precision > bps * 8) throw new ImageFormatException("TIFF: JPEG 2000 strip/tile is deeper than the image");
                if ((long)(p.Width - 1) * p.Dx + 1 < cw - p.Dx + 1 || (long)(p.Height - 1) * p.Dy + 1 < rows - p.Dy + 1)
                    throw new ImageFormatException("TIFF: JPEG 2000 strip/tile does not match the image layout");
                for (int y = 0; y < rows; y++)
                {
                    long row = (long)Math.Min(y / p.Dy, p.Height - 1) * p.Width;
                    byte* d = dst + ((long)y * cw * ps + c) * bps;
                    int step = ps * bps, dx = p.Dx, last = p.Width - 1;
                    if (p.Data64 != null)
                    {
                        fixed (long* sp = p.Data64)
                            for (int x = 0; x < cw; x++, d += step) *(long*)d = sp[row + Math.Min(x / dx, last)];
                        continue;
                    }
                    fixed (int* sp = p.Data)
                    {
                        int* sr = sp + row;
                        if (dx == 1)
                            switch (bps)
                            {
                                case 1: for (int x = 0; x < cw; x++, d += step) *d = (byte)sr[x]; break;
                                case 2: for (int x = 0; x < cw; x++, d += step) *(ushort*)d = (ushort)sr[x]; break;
                                case 4: for (int x = 0; x < cw; x++, d += step) *(int*)d = sr[x]; break;
                                default: for (int x = 0; x < cw; x++, d += step) *(long*)d = p.Signed ? sr[x] : (uint)sr[x]; break;
                            }
                        else
                            for (int x = 0; x < cw; x++, d += step)
                            {
                                int v = sr[Math.Min(x / dx, last)];
                                switch (bps)
                                {
                                    case 1: *d = (byte)v; break;
                                    case 2: *(ushort*)d = (ushort)v; break;
                                    case 4: *(int*)d = v; break;
                                    default: *(long*)d = p.Signed ? v : (uint)v; break;
                                }
                            }
                    }
                }
            }
            if (_yccY != null && !planes.UsedColorTransform && ps >= 3)
                YccInPlace(dst, (long)cw * rows, ps);
        }

        /// <summary>
        /// Decompresses the first <paramref name="expected"/> bytes of a chunk (whole rows of <paramref name="rowBytes"/>).
        /// </summary>
        void Decompress(long offset, long count, byte* dst, long expected, long rowBytes)
        {
            if (Compression == 1)
            {
                if (count < expected) throw new ImageFormatException("TIFF: strip/tile data is too short");
                ReadRaw(offset, dst, expected);
                return;
            }
            if (count > int.MaxValue)
            {
                // more than 2 GB of compressed data (stream sources only): decoded piece by piece
                if (!(Compression is 5 or 32773 or 8 or 32946) || FillOrder == 2)
                    throw new ImageNotSupportedException("TIFF: compressed strips or tiles larger than 2 GB are not supported with this compression");
                if (TiffLargeChunk.Decode(File.Src, offset, count, Compression, dst, expected) < expected)
                    throw new ImageFormatException("TIFF: compressed strip/tile data is truncated");
                return;
            }
            byte[]? srcRent = null, rev = null;
            try
            {
                var src = File.Src.Get(offset, (int)count, ref srcRent);
                // FillOrder 2 only makes sense for bit oriented data; byte oriented deflate-like streams are left alone.
                if ((FillOrder == 2 && (Compression is 5 or 32773 || IsCcitt)) || (Compression == 34661 && JbigIsReversed(src)))
                {
                    rev = ArrayPool<byte>.Shared.Rent((int)count);
                    TiffPredict.ReverseBits(src, rev);
                    src = rev.AsSpan(0, (int)count);
                }
                long got;
                switch (Compression)
                {
                    case 5: got = TiffLzw.Decode(src, dst, expected); break;
                    case 32809: got = TiffThunder.Decode(src, dst, ChunkW, (int)(expected / Math.Max(1, rowBytes))); break;
                    case 32773: got = TiffPackBits.Decode(src, dst, expected); break;
                    case 8 or 32946 when expected > int.MaxValue:
                    {
                        // more than 2 GB out of one deflate stream: pull it through the incremental decoder
                        var inflater = Zlib.CreateInflater();
                        int consumed = 0;
                        got = 0;
                        while (got < expected)
                        {
                            var status = inflater.Inflate(src.Slice(consumed), new Span<byte>(dst + got, (int)Math.Min(expected - got, 1 << 30)), out int c, out int w);
                            consumed += c; got += w;
                            if (status == InflateStatus.Done || (c == 0 && w == 0)) break;
                        }
                        break;
                    }
                    case 8: case 32946: case 50000: case 34925:
                        if (expected > int.MaxValue)
                            throw new ImageNotSupportedException("TIFF: strips or tiles larger than 2 GB are not supported with this compression");
                        var d = new Span<byte>(dst, (int)expected);
                        try
                        {
                            if (Compression is 8 or 32946) got = Zlib.Decompress(src, d);
                            else
                            {
                                // These decoders want room for everything the stream holds, which can be more than
                                // asked for: a region stops early, and a last strip may be stored with all its rows.
                                long capacity = rowBytes * ((Kind == TiffChunkKind.YccPacked ? (ChunkH + SubV - 1) / SubV : ChunkH) + 1L);
                                capacity = Math.Max(expected, Math.Min(capacity, count * (1L << 17) + 4096)); // no more than the data can expand to
                                if (capacity > int.MaxValue)
                                    throw new ImageNotSupportedException("TIFF: strips or tiles larger than 2 GB are not supported with this compression");
                                byte* t = (byte*)NativeMemory.Alloc((nuint)capacity);
                                try
                                {
                                    var all = new Span<byte>(t, (int)capacity);
                                    got = Compression == 50000 ? Zstd.Decompress(src, all) : Xz.Decompress(src, all);
                                    all.Slice(0, (int)Math.Min(got, expected)).CopyTo(d);
                                }
                                finally { NativeMemory.Free(t); }
                            }
                        }
                        catch (Exception ex) when (ex is InvalidDataException || ex is IOException)
                        {
                            throw new ImageFormatException("TIFF: corrupt deflate data", ex);
                        }
                        break;
                    case 2: case 3: case 4:
                        if (expected > int.MaxValue)
                            throw new ImageNotSupportedException("TIFF: strips or tiles larger than 2 GB are not supported with this compression");
                        TiffCcitt.Decode(src, dst, ChunkW, (int)(expected / Math.Max(1, rowBytes)), Compression, _t4Options);
                        got = expected;
                        break;
                    case 34661:
                        // one single-plane bi-level image entity per strip; its bits are the stored bits
                        if (expected > int.MaxValue)
                            throw new ImageNotSupportedException("TIFF: strips or tiles larger than 2 GB are not supported with this compression");
                        Jbig.DecodeForTiff(src, ChunkW, (int)(expected / Math.Max(1, rowBytes)), new Span<byte>(dst, (int)expected));
                        got = expected;
                        break;
                    default: throw new ImageNotSupportedException($"TIFF: compression {Compression} is not supported");
                }
                if (got < expected) throw new ImageFormatException("TIFF: compressed strip/tile data is truncated");
            }
            finally
            {
                if (srcRent != null) ArrayPool<byte>.Shared.Return(srcRent);
                if (rev != null) ArrayPool<byte>.Shared.Return(rev);
            }
        }

        /// <summary>Brings decompressed rows into machine byte order and undoes the predictor.</summary>
        void Unpredict(byte* b, int rows, long rowBytes, int cw, int ps)
        {
            int samples = cw * ps;
            if (Kind == TiffChunkKind.Packed) return;
            int width = Kind == TiffChunkKind.Float24 ? 3 : Bps;
            // DNG's predictors 34892..34895: horizontal / floating point differencing with the pixel 2 or 4 to the left
            int stride = ps * (Predictor is 34892 or 34894 ? 2 : Predictor is 34893 or 34895 ? 4 : 1);
            if (Predictor is 3 or 34894 or 34895)
            {
                byte* t = (byte*)NativeMemory.Alloc((nuint)rowBytes);
                try
                {
                    for (int y = 0; y < rows; y++) TiffPredict.UndoFloat(b + y * rowBytes, samples, stride, width, t);
                }
                finally { NativeMemory.Free(t); }
                return;
            }
            if (Kind == TiffChunkKind.Mixed) return;   // read sample by sample in Transfer
            if (Kind == TiffChunkKind.Float24)
            {
                // stays in file byte order (converted in Transfer); the horizontal predictor adds 24-bit integers
                if (Predictor is 2 or 34892 or 34893)
                    for (int y = 0; y < rows; y++) TiffPredict.UndoHorizontal24(b + y * rowBytes, samples, stride, File.BigEndian);
                return;
            }
            if (File.BigEndian && Bps > 1) TiffPredict.SwapBytes(b, rowBytes * rows, Bps);
            if (Predictor is 2 or 34892 or 34893)
                for (int y = 0; y < rows; y++) TiffPredict.UndoHorizontal(b + y * rowBytes, samples, stride, Bps);
        }

        struct Geometry
        {
            public int Plane, X0, Y0, Ix0, Ix1, Iy0, Iy1, Sh, Sv, Ps;
            /// <summary>Stored row of the chunk that row 0 of the buffer holds.</summary>
            public int BufRow0;
        }

        void Transfer(byte* buf, long rowBytes, in Geometry g) => Transfer(buf, rowBytes, g, Kind);

        /// <summary>Copies the part of a decoded chunk that lies inside the requested region into the raw image.</summary>
        void Transfer(byte* buf, long rowBytes, in Geometry g, TiffChunkKind kind)
        {
            int n = g.Ix1 - g.Ix0, sx = g.Ix0 - g.X0, rc = RawChannels, bps = Bps, ps = g.Ps, plane = g.Plane;
            for (int y = g.Iy0; y < g.Iy1; y++)
            {
                byte* s = buf + ((y - g.Y0) / g.Sv - g.BufRow0) * rowBytes;
                byte* d = _dst + (y - _ry) * _dstRowBytes + (long)(g.Ix0 - _rx) * rc * bps;
                switch (kind)
                {
                    case TiffChunkKind.Samples:
                        if (g.Sh > 1 || g.Sv > 1)
                        {
                            // subsampled chroma plane: replicate
                            if (bps == 2)
                            {
                                ushort* d2 = (ushort*)d + plane, s2 = (ushort*)s;
                                for (int i = 0; i < n; i++) d2[(long)i * rc] = s2[(sx + i) / g.Sh];
                            }
                            else
                            {
                                d += plane;
                                for (int i = 0; i < n; i++) d[(long)i * rc] = s[(sx + i) / g.Sh];
                            }
                        }
                        else if (ps == rc)
                        {
                            long len = (long)n * ps * bps;
                            Buffer.MemoryCopy(s + (long)sx * ps * bps, d, len, len);
                        }
                        else
                        {
                            s += (long)sx * bps; d += plane * bps;
                            long step = (long)rc * bps;
                            switch (bps)
                            {
                                case 1: for (int i = 0; i < n; i++) d[i * step] = s[i]; break;
                                case 2: for (int i = 0; i < n; i++) *(ushort*)(d + i * step) = ((ushort*)s)[i]; break;
                                case 4: for (int i = 0; i < n; i++) *(uint*)(d + i * step) = ((uint*)s)[i]; break;
                                default: for (int i = 0; i < n; i++) *(ulong*)(d + i * step) = ((ulong*)s)[i]; break;
                            }
                        }
                        break;
                    case TiffChunkKind.Float24:
                    {
                        bool be = Predictor is not (3 or 34894 or 34895) && File.BigEndian;
                        s += (long)sx * ps * 3;
                        float* f = (float*)d;
                        long total = (long)n * ps;
                        long step = ps == rc ? 1 : rc;
                        if (ps != rc) f += plane;
                        for (long i = 0; i < total; i++, s += 3)
                            f[i * step] = Float24ToSingle(be ? (uint)(s[0] << 16 | s[1] << 8 | s[2]) : (uint)(s[2] << 16 | s[1] << 8 | s[0]));
                        break;
                    }
                    case TiffChunkKind.Mixed:
                    {
                        int sb = Bits / 8;
                        s += (long)sx * ps * sb;
                        double* o = (double*)d;
                        bool contig = ps == rc;
                        for (int x = 0; x < n; x++)
                            for (int c = 0; c < ps; c++, s += sb)
                            {
                                int ch = contig ? c : plane;
                                ulong v = 0;
                                if (File.BigEndian) for (int k = 0; k < sb; k++) v = v << 8 | s[k];
                                else for (int k = sb - 1; k >= 0; k--) v = v << 8 | s[k];
                                o[(long)x * rc + ch] = _formats![ch] switch
                                {
                                    // (correctly rounded for values above 2^63, where the direct conversion may round twice)
                                    1 => v <= long.MaxValue ? (double)(long)v : (double)(long)(v >> 1 | (v & 1)) * 2.0,
                                    2 => sb == 8 ? (long)v : (double)((long)(v << (64 - sb * 8)) >> (64 - sb * 8)),
                                    _ => sb == 2 ? (double)BitConverter.UInt16BitsToHalf((ushort)v) : sb == 4 ? BitConverter.UInt32BitsToSingle((uint)v) : BitConverter.UInt64BitsToDouble(v),
                                };
                            }
                        break;
                    }
                    default:
                        UnpackRow(s, d, sx, n, ps, plane);
                        break;
                }
            }
        }

        /// <summary>24-bit float: sign, 7 exponent bits (bias 63), 16 mantissa bits.</summary>
        static float Float24ToSingle(uint v)
        {
            uint sign = v >> 23 & 1, e = v >> 16 & 0x7F, m = v & 0xFFFF;
            if (e == 0)
            {
                float sub = MathF.ScaleB(m, -78); // subnormal: m * 2^(1 - 63 - 16)
                return sign != 0 ? -sub : sub;
            }
            uint bits = sign << 31 | (e == 127 ? 255u : e - 63 + 127) << 23 | m << 7;
            return BitConverter.UInt32BitsToSingle(bits);
        }

        /// <summary>Unpacks n pixels of bit packed samples starting at pixel sx of a stored row.</summary>
        void UnpackRow(byte* s, byte* d, int sx, int n, int ps, int plane)
        {
            int rc = RawChannels, bps = Bps;
            bool contig = ps == rc;
            if (contig && UniformBits && Bits == 1 && rc == 1 && Predictor != 2)
            {
                // bilevel rows: whole bytes at a time
                byte on = (byte)(_scale[0] ? 255 : 1);
                int i = 0;
                long bit = sx;
                for (; (bit & 7) != 0 && i < n; i++, bit++) d[i] = (s[bit >> 3] >> (7 - (int)(bit & 7)) & 1) != 0 ? on : (byte)0;
                byte* sp = s + (bit >> 3);
                for (; i + 8 <= n; i += 8)
                {
                    int v = *sp++;
                    d[i] = (v & 0x80) != 0 ? on : (byte)0; d[i + 1] = (v & 0x40) != 0 ? on : (byte)0;
                    d[i + 2] = (v & 0x20) != 0 ? on : (byte)0; d[i + 3] = (v & 0x10) != 0 ? on : (byte)0;
                    d[i + 4] = (v & 0x08) != 0 ? on : (byte)0; d[i + 5] = (v & 0x04) != 0 ? on : (byte)0;
                    d[i + 6] = (v & 0x02) != 0 ? on : (byte)0; d[i + 7] = (v & 0x01) != 0 ? on : (byte)0;
                }
                if (i < n)
                {
                    int v = *sp;
                    for (int k = 0; i < n; i++, k++) d[i] = (v & (0x80 >> k)) != 0 ? on : (byte)0;
                }
                return;
            }
            bool signed = Format == 2;
            // With the horizontal predictor (differences modulo 2^bits between neighbouring pixels) the row has to be
            // walked from its first pixel even when only a part of it is wanted.
            bool predict = Predictor == 2;
            int start = predict ? 0 : sx;
            long pos = contig ? (long)start * _pixelBits : (long)start * BitsPer[plane];
            ulong* acc = stackalloc ulong[predict ? Math.Min(ps, 64) : 1];
            if (predict && ps > 64) throw new ImageNotSupportedException("TIFF: predictor on packed samples with more than 64 samples per pixel is not supported");
            for (int c = 0; predict && c < ps; c++) acc[c] = 0;
            for (int x = start; x < sx + n; x++)
                for (int c = 0; c < ps; c++)
                {
                    int ch = contig ? c : plane;
                    int b = BitsPer[ch];
                    // gather the bytes the sample touches (up to 9 for 64 bits at an odd offset)
                    long first = pos >> 3;
                    int end = (int)(pos & 7) + b, nb = (end + 7) >> 3;
                    ulong v;
                    if (nb <= 8)
                    {
                        v = 0;
                        for (int k = 0; k < nb; k++) v = v << 8 | s[first + k];
                        v = v >> (nb * 8 - end) & _inMax[ch];
                    }
                    else
                    {
                        UInt128 w = 0;
                        for (int k = 0; k < nb; k++) w = w << 8 | s[first + k];
                        v = (ulong)(w >> (nb * 8 - end)) & _inMax[ch];
                    }
                    pos += b;
                    if (predict) v = acc[c] = (acc[c] + v) & _inMax[ch];
                    if (x < sx) continue;
                    if (signed)
                    {
                        if (b < 64)
                        {
                            ulong sb = 1UL << (b - 1);
                            v = (v ^ sb) - sb; // sign extend
                        }
                    }
                    else if (_scale[ch])
                        v = b <= 32 && bps <= 4 ? (v * _outMax + _inMax[ch] / 2) / _inMax[ch] : (ulong)(((UInt128)v * _outMax + _inMax[ch] / 2) / _inMax[ch]);
                    byte* o = d + ((long)(x - sx) * rc + ch) * bps;
                    switch (bps)
                    {
                        case 1: *o = (byte)v; break;
                        case 2: *(ushort*)o = (ushort)v; break;
                        case 4: *(uint*)o = (uint)v; break;
                        default: *(ulong*)o = v; break;
                    }
                }
        }

        void YccToRgb(byte* src, byte* rgb, int rows)
        {
            int w = ChunkW, sh = SubH, sv = SubV;
            int across = (w + sh - 1) / sh, down = (rows + sv - 1) / sv;
            float[] ty = _yccY!, crR = _yccCrR!, cbB = _yccCbB!, crG = _yccCrG!, cbG = _yccCbG!;
            byte* p = src;
            for (int by = 0; by < down; by++)
                for (int bx = 0; bx < across; bx++)
                {
                    int cb = p[sh * sv], cr = p[sh * sv + 1];
                    float fr = crR[cr], fg = crG[cr] + cbG[cb], fb = cbB[cb];
                    for (int j = 0; j < sv; j++)
                    {
                        int y = by * sv + j;
                        if (y >= rows) break;
                        for (int i = 0; i < sh; i++)
                        {
                            int x = bx * sh + i;
                            if (x >= w) break;
                            float l = ty[p[j * sh + i]];
                            byte* o = rgb + ((long)y * w + x) * 3;
                            o[0] = Clamp(l + fr); o[1] = Clamp(l + fg); o[2] = Clamp(l + fb);
                        }
                    }
                    p += sh * sv + 2;
                }
        }

        void Ycc16(double y, double cb, double cr, ushort* o)
        {
            var k = _ycc16!;
            y = (y - k[0]) * k[1]; cb = (cb - k[2]) * k[3]; cr = (cr - k[4]) * k[5];
            double r = y + cr * k[6], bl = y + cb * k[7], gr = y - cr * k[8] - cb * k[9];
            o[0] = (ushort)Math.Clamp(r + 0.5, 0, 65535); o[1] = (ushort)Math.Clamp(gr + 0.5, 0, 65535); o[2] = (ushort)Math.Clamp(bl + 0.5, 0, 65535);
        }

        void YccToRgb16(ushort* src, ushort* rgb, int rows)
        {
            int w = ChunkW, sh = SubH, sv = SubV;
            int across = (w + sh - 1) / sh, down = (rows + sv - 1) / sv;
            ushort* p = src;
            for (int by = 0; by < down; by++)
                for (int bx = 0; bx < across; bx++, p += sh * sv + 2)
                    for (int j = 0; j < sv && by * sv + j < rows; j++)
                        for (int i = 0; i < sh && bx * sh + i < w; i++)
                            Ycc16(p[j * sh + i], p[sh * sv], p[sh * sv + 1], rgb + ((long)(by * sv + j) * w + bx * sh + i) * 3);
        }

        void YccInPlace16(ushort* p, long pixels)
        {
            for (long i = 0; i < pixels; i++, p += 3) Ycc16(p[0], p[1], p[2], p);
        }

        /// <summary>Converts interleaved full resolution YCbCr pixels to RGB in place.</summary>
        void YccInPlace(byte* p, long pixels, int step = 3)
        {
            float[] ty = _yccY!, crR = _yccCrR!, cbB = _yccCbB!, crG = _yccCrG!, cbG = _yccCbG!;
            for (long i = 0; i < pixels; i++, p += step)
            {
                float l = ty[p[0]];
                int cb = p[1], cr = p[2];
                p[0] = Clamp(l + crR[cr]); p[1] = Clamp(l + crG[cr] + cbG[cb]); p[2] = Clamp(l + cbB[cb]);
            }
        }

        static byte Clamp(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);
    }
}
