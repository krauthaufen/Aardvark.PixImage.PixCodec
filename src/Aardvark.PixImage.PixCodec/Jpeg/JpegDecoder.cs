using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aardvark.ImageCodecs.JpegImpl
{
    /// <summary>How a component plane is brought to output resolution.</summary>
    internal enum JpegUpsample { Direct, Replicate, Fractional, H2V1, H2V2, H1V2 }

    internal sealed unsafe class JpegComponent
    {
        public int Id, H, V, Tq;
        /// <summary>Allocated size in data units (blocks, or samples for lossless): whole MCUs of an interleaved scan.</summary>
        public int BlocksW, BlocksH;
        /// <summary>Size in data units of a non-interleaved scan of this component.</summary>
        public int ScanBlocksW, ScanBlocksH;
        /// <summary>Output samples per data unit edge: 8, or 4 / 2 / 1 with DCT domain downscaling (1 for lossless).</summary>
        public int S = 8;
        /// <summary>Samples that carry image content (the rest of the plane is padding).</summary>
        public int SampledW, SampledH;
        public int Stride;
        /// <summary>
        /// Streaming decode: the plane is a ring of <see cref="RingBlocks"/> block rows (<see cref="RingRows"/> sample rows)
        /// instead of the whole component; 0 for a full plane. <see cref="UnitRows"/> sample rows complete per decoded unit row.
        /// </summary>
        public int RingBlocks, RingRows, UnitRows;
        public byte[]? PlaneArr;
        public byte* Plane;
        public ushort[]? Plane16Arr;
        public ushort* Plane16;
        public short[]? CoefArr;
        public GCHandle CoefPin;
        public short* Coefs;
        public int[]? QuantArr;
        public int* Quant;
        public int DcPred;
        public HuffDecodeTable? Dc, Ac;
        // arithmetic coding: conditioning table numbers and the DC context carried from block to block
        public int DcTableId, AcTableId, DcContext;
        /// <summary>Lossless point transform of the scan that coded this component.</summary>
        public int PointTransform;
        public bool Decoded;
        /// <summary>Arithmetic lossless coding: the differences of the scan, which condition the coding of their neighbours.</summary>
        public short[]? Differences;
        public JpegUpsample Upsample;
        public int HExp = 1, VExp = 1;
        // fractional ratios: source step per output sample as HNum / HDen
        public int HNum = 1, HDen = 1, VNum = 1, VDen = 1;
    }

    internal enum JpegColorSpace
    {
        Gray, YCbCr, Rgb, Cmyk, Ycck,
        /// <summary>All components as stored, interleaved.</summary>
        Raw,
        /// <summary>YCCK with only the YCbCr part converted: 4 channels in the stored (Adobe: inverted) CMYK convention.</summary>
        YcckToCmyk,
    }

    internal sealed unsafe class JpegDecoder
    {
        const int MaxScans = 1000;

        readonly byte* _d;
        readonly int _len;

        /// <summary>Frame size as coded.</summary>
        public int Width, Height;
        /// <summary>Size after DCT domain downscaling.</summary>
        public int OutWidth, OutHeight;
        public int Precision = 8;
        public bool Progressive, Lossless, Arithmetic;
        /// <summary>1, 2, 4 or 8; set before parsing. Ignored by lossless frames.</summary>
        public int ScaleDenominator = 1;
        public JpegComponent[] Comps = Array.Empty<JpegComponent>();
        public int HMax, VMax;
        int _minS = 8;
        int _mcusX, _mcusY;
        bool _geometry, _allocated, _headerOnly;
        int _scans;
        int _restartInterval;

        readonly int[]?[] _quant = new int[4][];
        readonly HuffDecodeTable?[] _dc = new HuffDecodeTable[4];
        readonly HuffDecodeTable?[] _ac = new HuffDecodeTable[4];
        JpegArith? _arith;

        // hierarchical mode (T.81 Annex J): a DHP segment announces the final image, then a sequence of frames follows,
        // each (after the first) coding the difference to the - possibly 2x expanded - result of the ones before
        public bool Hierarchical;
        bool _differential, _firstFrameLossless, _expandH, _expandV, _frameExpandH, _frameExpandV;
        int _frames, _frameScans;
        int _finalWidth, _finalHeight;
        (int Id, int H, int V)[] _finalComps = Array.Empty<(int, int, int)>();
        ushort[][]? _reference;
        int[] _referenceW = new int[4], _referenceH = new int[4];

        bool _jfif, _adobe;
        int _adobeTransform;
        public readonly ImageMetadata Metadata = new ImageMetadata();
        bool _hasMetadata;
        List<(int seq, int count, byte[] data)>? _icc;

        /// <summary>
        /// Set before parsing to decode 8 bit sequential (all components in one scan) and progressive frames in bands:
        /// called once the planes are about to be allocated, it returns the assembler that consumes finished rows, or
        /// null to decode into full planes. The planes are then small rings and <see cref="Streamed"/> tells that the
        /// output is complete.
        /// </summary>
        public Func<JpegDecoder, JpegPost.Assembler?>? OnStream;
        JpegPost.Assembler? _sink;
        bool _streamDone;
        public bool Streamed => _streamDone;

        public JpegDecoder(byte* data, int length)
        {
            _d = data; _len = length;
        }

        public bool HasMetadata => _hasMetadata;

        bool _sawEoi;

        /// <summary>Checked per MCU row (JpegDecodeOptions.CancellationToken).</summary>
        public System.Threading.CancellationToken Cancel;

        /// <summary>Upper bound for OutWidth x OutHeight, 0 for none (JpegDecodeOptions.MaxPixels).</summary>
        public long MaxPixels;

        void CheckMaxPixels()
        {
            if (MaxPixels > 0 && (long)OutWidth * OutHeight > MaxPixels)
                throw new ImageNotSupportedException($"JPEG image of {OutWidth}x{OutHeight} exceeds JpegDecodeOptions.MaxPixels ({MaxPixels})");
        }
        /// <summary>After a full parse: the data ended before the EOI marker (cut short somewhere in or after a scan).</summary>
        public bool Truncated => !_sawEoi;

        /// <summary>Samples wider than 8 bit, or lossless: planes are 16 bit.</summary>
        public bool Wide => Precision > 8 || Lossless || Hierarchical;

        /// <summary>Set for a tables-only stream (TIFF JPEGTables): no frame or scan is expected.</summary>
        public bool TablesOnly;

        /// <summary>Starts out with tables defined elsewhere (TIFF JPEGTables). The table objects are immutable, so sharing is safe.</summary>
        public void ImportTables(JpegTiffTables t)
        {
            for (int i = 0; i < 4; i++)
            {
                _quant[i] = t.Quant[i];
                _dc[i] = t.Dc[i];
                _ac[i] = t.Ac[i];
            }
            _restartInterval = t.RestartInterval;
            if (t.Conditioning != null)
            {
                _arith = new JpegArith();
                t.Conditioning.AsSpan(0, 4).CopyTo(_arith.DcL);
                t.Conditioning.AsSpan(4, 4).CopyTo(_arith.DcU);
                t.Conditioning.AsSpan(8, 4).CopyTo(_arith.AcK);
            }
        }

        public void ExportTables(JpegTiffTables t)
        {
            for (int i = 0; i < 4; i++)
            {
                t.Quant[i] = _quant[i];
                t.Dc[i] = _dc[i];
                t.Ac[i] = _ac[i];
            }
            t.RestartInterval = _restartInterval;
            if (_arith != null) t.Conditioning = [.. _arith.DcL, .. _arith.DcU, .. _arith.AcK];
        }

        /// <summary>The color space libjpeg would assume from the markers and component ids.</summary>
        public JpegColorSpace ColorSpace
        {
            get
            {
                switch (Comps.Length)
                {
                    case 1: return JpegColorSpace.Gray;
                    case 3:
                        if (_jfif) return JpegColorSpace.YCbCr;
                        if (_adobe) return _adobeTransform == 0 ? JpegColorSpace.Rgb : JpegColorSpace.YCbCr;
                        if (Comps[0].Id == 'R' && Comps[1].Id == 'G' && Comps[2].Id == 'B') return JpegColorSpace.Rgb;
                        return JpegColorSpace.YCbCr;
                    case 4:
                        return _adobe && _adobeTransform != 0 ? JpegColorSpace.Ycck : JpegColorSpace.Cmyk;
                    default:
                        return JpegColorSpace.Raw;
                }
            }
        }

        static ImageFormatException Bad(string what) => new ImageFormatException("invalid JPEG: " + what);

        int U16(int pos) => _d[pos] << 8 | _d[pos + 1];

        /// <summary>Advances to the next marker (skipping garbage and fill bytes); returns its code or -1 at the end of the data.</summary>
        int NextMarker(ref int pos)
        {
            while (true)
            {
                while (pos < _len && _d[pos] != 0xFF) pos++;
                while (pos < _len && _d[pos] == 0xFF) pos++;
                if (pos >= _len) return -1;
                int code = _d[pos++];
                if (code != 0) return code;
            }
        }

        /// <summary>Walks the marker segments. With <paramref name="headerOnly"/> it stops once the frame geometry is known.</summary>
        public void Parse(bool headerOnly)
        {
            try { ParseSegments(headerOnly); }
            finally { ReleaseCoefficients(); }
        }

        void ReleaseCoefficients()
        {
            foreach (var c in Comps)
            {
                if (c.CoefArr == null) continue;
                c.CoefPin.Free();
                ArrayPool<short>.Shared.Return(c.CoefArr);
                c.CoefArr = null; c.Coefs = null;
            }
        }

        void ParseSegments(bool headerOnly)
        {
            _headerOnly = headerOnly;
            if (_len < 4 || _d[0] != 0xFF || _d[1] != 0xD8) throw Bad("missing SOI marker");
            int pos = 2;
            while (true)
            {
                int m = NextMarker(ref pos);
                if (m == 0xD9) { _sawEoi = true; break; }
                if (m < 0) break;
                if (m == 0xD8 || m == 0x01 || (m >= 0xD0 && m <= 0xD7)) continue; // parameterless
                if (pos + 2 > _len) { if (_scans > 0) break; throw Bad("truncated header"); }
                int len = U16(pos);
                if (len < 2) throw Bad("bad segment length");
                if (pos + len > _len) { if (_scans > 0) break; throw Bad("truncated header"); }
                int p = pos + 2, n = len - 2;
                pos += len;
                switch (m)
                {
                    case 0xC0: case 0xC1: case 0xC2: case 0xC3: case 0xC9: case 0xCA: case 0xCB:
                    case 0xC5: case 0xC6: case 0xC7: case 0xCD: case 0xCE: case 0xCF:
                        ReadFrame(m, p, n);
                        if (headerOnly && Height != 0) { SetupGeometry(); return; }
                        break;
                    case 0xF7: case 0xF9:
                        throw new ImageNotSupportedException("JPEG-LS frame (SOF55 / SOF57): decode it with JpegLs (ImageCodec does that automatically)");
                    case 0xDE:
                        ReadHierarchy(p, n);
                        if (headerOnly && Height != 0) { InstallFinalGeometry(); return; }
                        break;
                    case 0xDF:
                        if (n != 1 || !Hierarchical) throw Bad("bad EXP segment");
                        _expandH = _d[p] >> 4 != 0;
                        _expandV = (_d[p] & 15) != 0;
                        break;
                    case 0xC4: ReadHuffmanTables(p, n); break;
                    case 0xCC: ReadConditioning(p, n); break;
                    case 0xDB: ReadQuantTables(p, n); break;
                    case 0xDD:
                        if (n != 2) throw Bad("bad DRI segment");
                        _restartInterval = U16(p);
                        break;
                    case 0xDA:
                        if (TablesOnly) break;
                        if (Comps.Length == 0) throw Bad("scan before frame header");
                        if (Height == 0) FindNumberOfLines(pos);
                        if (headerOnly) { SetupGeometry(); return; }
                        pos = ReadScan(p, n);
                        break;
                    case 0xE0: ReadApp0(p, n); break;
                    case 0xE1: ReadApp1(p, n); break;
                    case 0xE2: ReadApp2(p, n); break;
                    case 0xEE: ReadApp14(p, n); break;
                }
            }
            if (TablesOnly) return;
            if (Comps.Length == 0) throw Bad("no frame header");
            if (headerOnly)
            {
                if (Height == 0) throw Bad("image height is not defined");
                SetupGeometry();
                return;
            }
            if (_scans == 0) throw Bad("no image data");
            FinishFrame();
            if (Hierarchical) InstallFinalGeometry();
            FinishIcc();
        }

        // ---------------------------------------------------------------- headers

        void ReadFrame(int marker, int p, int n)
        {
            if (TablesOnly) return;
            bool differential = marker is 0xC5 or 0xC6 or 0xC7 or 0xCD or 0xCE or 0xCF;
            if (Comps.Length != 0 && _frames > 0)
            {
                if (!Hierarchical) throw Bad("multiple frame headers");
                if (_headerOnly) return;
                FinishFrame();
            }
            if (differential && (!Hierarchical || _reference == null)) throw Bad("differential frame without a reference");
            if (!differential && _reference != null) throw Bad("second non-differential frame");
            if (n < 6) throw Bad("bad frame header");
            int precision = _d[p], height = U16(p + 1), width = U16(p + 3);
            int nc = _d[p + 5];
            if (n != 6 + 3 * nc) throw Bad("bad frame header");
            Lossless = marker is 0xC3 or 0xC7 or 0xCB or 0xCF;
            Progressive = marker is 0xC2 or 0xC6 or 0xCA or 0xCE;
            Arithmetic = marker >= 0xC9;
            bool precisionOk = Lossless ? precision >= 2 && precision <= 16 : precision == 8 || (precision == 12 && marker != 0xC0);
            if (!precisionOk) throw Bad("unsupported sample precision " + precision);
            if (width == 0) throw Bad("zero width");
            if (nc == 0) throw Bad("no components");
            if (nc > 4) throw new ImageNotSupportedException($"JPEG with {nc} components is not supported");
            if (Hierarchical)
            {
                if (precision != Precision || nc != _finalComps.Length || height == 0 || width > _finalWidth || height > _finalHeight)
                    throw Bad("frame does not fit the hierarchical progression");
                if (_frames == 0) _firstFrameLossless = Lossless;
            }
            Precision = precision; Height = height; Width = width;
            _differential = differential;
            // the EXP segment in front of this frame says how its reference is to be enlarged
            _frameExpandH = _expandH; _frameExpandV = _expandV;
            _expandH = _expandV = false;
            _frames++;
            _frameScans = 0;
            _geometry = _allocated = false;
            HMax = VMax = 0;

            var comps = new JpegComponent[nc];
            for (int i = 0; i < nc; i++)
            {
                var c = new JpegComponent { Id = _d[p + 6 + i * 3], H = _d[p + 7 + i * 3] >> 4, V = _d[p + 7 + i * 3] & 15, Tq = _d[p + 8 + i * 3] };
                if (c.H < 1 || c.H > 4 || c.V < 1 || c.V > 4) throw Bad("bad sampling factor");
                if (c.Tq > 3) throw Bad("bad quantization table index");
                for (int j = 0; j < i; j++) if (comps[j].Id == c.Id) throw Bad("duplicate component id");
                if (Hierarchical && (c.Id, c.H, c.V) != _finalComps[i]) throw Bad("frame components differ from the hierarchical progression");
                HMax = Math.Max(HMax, c.H);
                VMax = Math.Max(VMax, c.V);
                comps[i] = c;
            }
            Comps = comps;
        }

        /// <summary>DHP: the parameters of the completed image of a hierarchical progression (same layout as a frame header).</summary>
        void ReadHierarchy(int p, int n)
        {
            if (TablesOnly) return;
            if (Comps.Length != 0 || Hierarchical) throw Bad("misplaced DHP segment");
            if (n < 6) throw Bad("bad DHP segment");
            Precision = _d[p];
            _finalHeight = U16(p + 1);
            _finalWidth = U16(p + 3);
            int nc = _d[p + 5];
            if (n != 6 + 3 * nc || nc == 0 || _finalWidth == 0 || _finalHeight == 0 || Precision < 2 || Precision > 16) throw Bad("bad DHP segment");
            if (nc > 4) throw new ImageNotSupportedException($"JPEG with {nc} components is not supported");
            _finalComps = new (int, int, int)[nc];
            for (int i = 0; i < nc; i++)
            {
                int h = _d[p + 7 + i * 3] >> 4, v = _d[p + 7 + i * 3] & 15;
                if (h < 1 || h > 4 || v < 1 || v > 4) throw Bad("bad sampling factor");
                _finalComps[i] = (_d[p + 6 + i * 3], h, v);
            }
            Hierarchical = true;
            Width = _finalWidth; Height = _finalHeight;
        }

        /// <summary>Completes the frame in progress; in a hierarchical progression its result becomes (part of) the reference for the next one.</summary>
        void FinishFrame()
        {
            if (Comps.Length == 0 || !_allocated)
            {
                if (Hierarchical && _frames > 0) throw Bad("frame without image data");
                return;
            }
            if (Progressive)
            {
                IdctAll();
                ReleaseCoefficients();
            }
            if (Lossless) FinishLossless();
            if (!Hierarchical) return;

            int mask = (1 << Precision) - 1;
            var merged = new ushort[Comps.Length][];
            for (int i = 0; i < Comps.Length; i++)
            {
                var c = Comps[i];
                int w = c.SampledW, h = c.SampledH;
                var dst = merged[i] = new ushort[w * h];
                var plane = c.Plane16Arr!;
                if (!_differential)
                {
                    for (int y = 0; y < h; y++) plane.AsSpan(y * c.Stride, w).CopyTo(dst.AsSpan(y * w));
                }
                else
                {
                    var up = Expand(_reference![i], _referenceW[i], _referenceH[i], w, h);
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            // lossless differences wrap around, DCT based ones are an approximation and get clamped
                            int v = up[y * w + x] + (short)plane[y * c.Stride + x];
                            dst[y * w + x] = (ushort)(Lossless ? v & mask : Math.Clamp(v, 0, mask));
                        }
                }
                _referenceW[i] = w; _referenceH[i] = h;
            }
            _reference = merged;
        }

        /// <summary>
        /// Brings a reference component to the size of the next frame: doubled where the EXP segment asked for it
        /// (J.1.1.2: samples in between are the truncated mean of their two neighbours, the edge is repeated).
        /// </summary>
        ushort[] Expand(ushort[] src, int sw, int sh, int w, int h)
        {
            if ((_frameExpandH ? (w + 1) / 2 : w) != sw || (_frameExpandV ? (h + 1) / 2 : h) != sh) throw Bad("frame size does not match its reference");
            if (!_frameExpandH && !_frameExpandV) return src;
            // output sample (x, y) is the truncated mean of the up to four reference samples around it
            var dst = new ushort[w * h];
            for (int y = 0; y < h; y++)
            {
                int y0 = _frameExpandV ? y >> 1 : y, y1 = _frameExpandV ? Math.Min(y0 + (y & 1), sh - 1) : y0;
                for (int x = 0; x < w; x++)
                {
                    int x0 = _frameExpandH ? x >> 1 : x, x1 = _frameExpandH ? Math.Min(x0 + (x & 1), sw - 1) : x0;
                    dst[y * w + x] = (ushort)((src[y0 * sw + x0] + src[y0 * sw + x1] + src[y1 * sw + x0] + src[y1 * sw + x1]) >> 2);
                }
            }
            return dst;
        }

        /// <summary>Sets the decoder up to present the completed hierarchical image (or, header only, just its geometry).</summary>
        void InstallFinalGeometry()
        {
            Width = _finalWidth; Height = _finalHeight;
            var comps = new JpegComponent[_finalComps.Length];
            HMax = VMax = 0;
            for (int i = 0; i < comps.Length; i++)
            {
                comps[i] = new JpegComponent { Id = _finalComps[i].Id, H = _finalComps[i].H, V = _finalComps[i].V };
                HMax = Math.Max(HMax, comps[i].H);
                VMax = Math.Max(VMax, comps[i].V);
            }
            Comps = comps;
            // sample planes at component resolution, like a lossless frame
            bool lossless = _headerOnly ? Lossless : _firstFrameLossless;
            Lossless = true;
            _geometry = false;
            SetupGeometry();
            Lossless = lossless;
            if (_reference == null) return;
            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (_referenceW[i] != c.SampledW || _referenceH[i] != c.SampledH) throw Bad("hierarchical progression ends before the full size");
                c.Plane16Arr = GC.AllocateUninitializedArray<ushort>(c.Stride * c.BlocksH, pinned: true);
                fixed (ushort* q = c.Plane16Arr) c.Plane16 = q;
                for (int y = 0; y < c.BlocksH; y++)
                {
                    var row = c.Plane16Arr.AsSpan(y * c.Stride, c.Stride);
                    _reference[i].AsSpan(Math.Min(y, c.SampledH - 1) * c.SampledW, c.SampledW).CopyTo(row);
                    row.Slice(c.SampledW).Fill(row[c.SampledW - 1]);
                }
            }
        }

        /// <summary>
        /// A frame header with height 0 leaves the number of lines to a DNL segment after the first scan (T.81 B.2.5).
        /// Looks ahead over the entropy coded data starting at <paramref name="pos"/> for it.
        /// </summary>
        void FindNumberOfLines(int pos)
        {
            for (; pos + 1 < _len; pos++)
            {
                if (_d[pos] != 0xFF) continue;
                int m = _d[pos + 1];
                if (m == 0 || m == 0xFF || (m >= 0xD0 && m <= 0xD7)) continue;
                if (m == 0xDC && pos + 6 <= _len && U16(pos + 2) == 4) Height = U16(pos + 4);
                break;
            }
            if (Height == 0) throw Bad("image height is not defined");
        }

        /// <summary>Derives all sizes from the frame header and the requested scale.</summary>
        void SetupGeometry()
        {
            if (_geometry) return;
            _geometry = true;
            int unit = Lossless ? 1 : 8;
            _minS = Lossless ? 1 : Hierarchical ? 8 : 8 / ScaleDenominator;
            _mcusX = (Width + unit * HMax - 1) / (unit * HMax);
            _mcusY = (Height + unit * VMax - 1) / (unit * VMax);
            OutWidth = (Width * _minS + unit - 1) / unit;
            OutHeight = (Height * _minS + unit - 1) / unit;
            foreach (var c in Comps)
            {
                int s = _minS;
                // a subsampled component can make up (part of) its deficit by using a larger inverse DCT than the
                // others instead of being upsampled afterwards; only while that keeps both ratios whole
                if (!Lossless)
                    while (s < 8 && HMax * _minS % (c.H * s * 2) == 0 && VMax * _minS % (c.V * s * 2) == 0) s *= 2;
                c.S = s;
                c.ScanBlocksW = ((Width * c.H + HMax - 1) / HMax + unit - 1) / unit;
                c.ScanBlocksH = ((Height * c.V + VMax - 1) / VMax + unit - 1) / unit;
                c.SampledW = (int)(((long)Width * c.H * s + (long)HMax * unit - 1) / ((long)HMax * unit));
                c.SampledH = (int)(((long)Height * c.V * s + (long)VMax * unit - 1) / ((long)VMax * unit));
                c.BlocksW = _mcusX * c.H;
                c.BlocksH = _mcusY * c.V;
                c.Stride = c.BlocksW * s;

                c.HNum = c.H * s; c.HDen = HMax * _minS;
                c.VNum = c.V * s; c.VDen = VMax * _minS;
                if (c.HDen % c.HNum != 0 || c.VDen % c.VNum != 0) { c.Upsample = JpegUpsample.Fractional; continue; }
                c.HExp = c.HDen / c.HNum;
                c.VExp = c.VDen / c.VNum;
                // triangle filters for the 2:1 cases, like libjpeg's "fancy upsampling" (which needs 3+ columns for
                // the horizontal filter and is not used at all at 1/8 scale); plain replication otherwise
                if (c.HExp == 1 && c.VExp == 1) c.Upsample = JpegUpsample.Direct;
                else if (_minS == 1) c.Upsample = JpegUpsample.Replicate;
                else if (c.HExp == 2 && c.VExp == 1 && c.SampledW > 2) c.Upsample = JpegUpsample.H2V1;
                else if (c.HExp == 2 && c.VExp == 2 && c.SampledW > 2) c.Upsample = JpegUpsample.H2V2;
                else if (c.HExp == 1 && c.VExp == 2) c.Upsample = JpegUpsample.H1V2;
                else c.Upsample = JpegUpsample.Replicate;
            }
        }

        void Allocate(int remainingBytes, int scanComponents)
        {
            SetupGeometry();
            long pixels = (long)Width * Height;
            // Even a flat image needs a bit per block (512 pixels per byte at best) resp. per sample; refuse headers
            // that promise far more than the data can hold. Small images are exempt so that heavily truncated files still decode.
            if (pixels > 1 << 16 && pixels / (Lossless ? 16 : 1024) > remainingBytes) throw Bad("image dimensions do not fit the amount of data");
            if ((long)OutWidth * OutHeight * Comps.Length > Array.MaxLength) throw new ImageNotSupportedException("JPEG image is too large");
            CheckMaxPixels();
            _sink = OnStream != null && !Wide && (Progressive || scanComponents == Comps.Length) ? OnStream(this) : null;
            foreach (var c in Comps)
            {
                c.RingBlocks = c.RingRows = 0;
                if (_sink != null)
                {
                    // a few unit rows: the upsampling filters look one sample row back and ahead
                    int unit = Comps.Length == 1 ? 1 : c.V;
                    c.UnitRows = unit * c.S;
                    c.RingBlocks = Math.Min(unit * Math.Max(4, (16 + c.UnitRows - 1) / c.UnitRows), c.BlocksH);
                    c.RingRows = c.RingBlocks * c.S;
                }
                long size = (long)c.Stride * (c.RingBlocks != 0 ? c.RingBlocks : c.BlocksH) * c.S;
                long coefs = (long)c.BlocksW * c.BlocksH * 64;
                if (size > Array.MaxLength || (Progressive && coefs > Array.MaxLength)) throw new ImageNotSupportedException("JPEG image is too large");
                // data units that never get decoded (truncated file) come out mid gray
                if (Wide)
                {
                    c.Plane16Arr = GC.AllocateUninitializedArray<ushort>((int)size, pinned: true);
                    fixed (ushort* q = c.Plane16Arr) c.Plane16 = q;
                    c.Plane16Arr.AsSpan().Fill(_differential ? (ushort)0 : (ushort)(1 << (Precision - 1)));
                }
                else
                {
                    c.PlaneArr = c.RingBlocks != 0 ? RentRing(Array.IndexOf(Comps, c), (int)size) : GC.AllocateUninitializedArray<byte>((int)size, pinned: true);
                    fixed (byte* q = c.PlaneArr) c.Plane = q;
                    c.PlaneArr.AsSpan().Fill(128);
                }
                if (Progressive)
                {
                    // from the shared pool, pinned until the frame is done (FinishFrame, or Parse on an error)
                    c.CoefArr = ArrayPool<short>.Shared.Rent((int)coefs);
                    c.CoefArr.AsSpan(0, (int)coefs).Clear();
                    c.CoefPin = GCHandle.Alloc(c.CoefArr, GCHandleType.Pinned);
                    c.Coefs = (short*)c.CoefPin.AddrOfPinnedObject();
                }
            }
            _allocated = true;
        }

        void ReadConditioning(int p, int n)
        {
            if ((n & 1) != 0) throw Bad("bad DAC segment");
            _arith ??= new JpegArith();
            for (int end = p + n; p < end; p += 2)
            {
                int tc = _d[p] >> 4, tb = _d[p] & 15, cs = _d[p + 1];
                if (tc > 1 || tb > 3) throw Bad("bad arithmetic conditioning table id");
                if (tc == 0)
                {
                    if ((cs & 15) > cs >> 4) throw Bad("bad DC conditioning");
                    _arith.DcL[tb] = (byte)(cs & 15);
                    _arith.DcU[tb] = (byte)(cs >> 4);
                }
                else
                {
                    if (cs < 1 || cs > 63) throw Bad("bad AC conditioning");
                    _arith.AcK[tb] = (byte)cs;
                }
            }
        }

        void ReadQuantTables(int p, int n)
        {
            int end = p + n;
            while (p < end)
            {
                int pq = _d[p] >> 4, tq = _d[p] & 15;
                p++;
                if (pq > 1 || tq > 3) throw Bad("bad quantization table");
                if (p + 64 * (pq + 1) > end) throw Bad("truncated quantization table");
                var t = new int[64];
                for (int i = 0; i < 64; i++)
                {
                    t[JpegTables.ZigZag[i]] = pq == 0 ? _d[p] : U16(p);
                    p += pq + 1;
                }
                _quant[tq] = t;
            }
        }

        void ReadHuffmanTables(int p, int n)
        {
            int end = p + n;
            while (p < end)
            {
                int tc = _d[p] >> 4, th = _d[p] & 15;
                p++;
                if (tc > 1 || th > 3) throw Bad("bad Huffman table id");
                if (p + 16 > end) throw Bad("truncated Huffman table");
                var counts = new ReadOnlySpan<byte>(_d + p, 16);
                int total = 0;
                for (int i = 0; i < 16; i++) total += counts[i];
                p += 16;
                if (total > 256 || p + total > end) throw Bad("bad Huffman table");
                var table = CachedTable(new ReadOnlySpan<byte>(_d + p - 16, 16 + total));
                p += total;
                if (tc == 0) _dc[th] = table; else _ac[th] = table;
            }
        }

        // Writers that repeat the same tables in every TIFF strip or tile (or every frame) get them built once per
        // thread: the tables are immutable and depend only on the counts and symbols.
        [ThreadStatic] static (byte[] Key, HuffDecodeTable Table)[]? t_tables;
        [ThreadStatic] static int t_tableNext;

        static HuffDecodeTable CachedTable(ReadOnlySpan<byte> spec)
        {
            var cache = t_tables ??= new (byte[], HuffDecodeTable)[8];
            foreach (var (key, t) in cache)
                if (key != null && spec.SequenceEqual(key)) return t;
            var table = new HuffDecodeTable(spec.Slice(0, 16), spec.Slice(16));
            cache[t_tableNext] = (spec.ToArray(), table);
            t_tableNext = (t_tableNext + 1) % cache.Length;
            return table;
        }

        void ReadApp0(int p, int n)
        {
            if (n < 12 || !new ReadOnlySpan<byte>(_d + p, 5).SequenceEqual("JFIF\0"u8)) return;
            _jfif = true;
            int units = _d[p + 7], xd = U16(p + 8), yd = U16(p + 10);
            double scale = units == 1 ? 1 : units == 2 ? 2.54 : 0;
            if (scale != 0 && xd != 0 && yd != 0)
            {
                Metadata.DpiX = xd * scale;
                Metadata.DpiY = yd * scale;
                _hasMetadata = true;
            }
        }

        void ReadApp1(int p, int n)
        {
            var xmpId = "http://ns.adobe.com/xap/1.0/\0"u8;
            if (n > xmpId.Length && Metadata.Xmp == null && new ReadOnlySpan<byte>(_d + p, xmpId.Length).SequenceEqual(xmpId))
            {
                Metadata.Xmp = new ReadOnlySpan<byte>(_d + p + xmpId.Length, n - xmpId.Length).ToArray();
                _hasMetadata = true;
                return;
            }
            if (n < 6 || Metadata.Exif != null || !new ReadOnlySpan<byte>(_d + p, 6).SequenceEqual("Exif\0\0"u8)) return;
            var exif = new ReadOnlySpan<byte>(_d + p + 6, n - 6).ToArray();
            Metadata.Exif = exif;
            Metadata.Orientation = JpegMeta.ExifOrientation(exif);
            _hasMetadata = true;
        }

        void ReadApp2(int p, int n)
        {
            if (n < 14 || !new ReadOnlySpan<byte>(_d + p, 12).SequenceEqual("ICC_PROFILE\0"u8)) return;
            // a header-only pass just needs to know which chunks exist
            (_icc ??= new()).Add((_d[p + 12], _d[p + 13], _headerOnly ? Array.Empty<byte>() : new ReadOnlySpan<byte>(_d + p + 14, n - 14).ToArray()));
        }

        /// <summary>Whether the ICC chunks seen so far form a complete profile (what a header-only pass can tell).</summary>
        public bool HasCompleteIcc => IccParts() != null;

        byte[][]? IccParts()
        {
            if (_icc == null) return null;
            // chunks are numbered 1..count; only accept a complete, consistent set
            int count = _icc[0].count;
            if (count != _icc.Count) return null;
            var parts = new byte[count][];
            foreach (var (seq, cnt, data) in _icc)
            {
                if (cnt != count || seq < 1 || seq > count || parts[seq - 1] != null) return null;
                parts[seq - 1] = data;
            }
            return parts;
        }

        void FinishIcc()
        {
            var parts = IccParts();
            if (parts == null) return;
            int total = 0;
            foreach (var part in parts) total += part.Length;
            var icc = new byte[total];
            int o = 0;
            foreach (var part in parts) { part.CopyTo(icc, o); o += part.Length; }
            Metadata.IccProfile = icc;
            _hasMetadata = true;
        }

        void ReadApp14(int p, int n)
        {
            if (n < 12 || !new ReadOnlySpan<byte>(_d + p, 5).SequenceEqual("Adobe"u8)) return;
            _adobe = true;
            _adobeTransform = _d[p + 11];
        }

        // ---------------------------------------------------------------- scans

        /// <summary>Parses the scan header at p and decodes the entropy coded data after it. Returns the position to resume marker parsing.</summary>
        int ReadScan(int p, int n)
        {
            if (n < 1) throw Bad("bad scan header");
            int ns = _d[p];
            if (ns < 1 || ns > 4 || n != 4 + 2 * ns) throw Bad("bad scan header");
            if (++_scans > MaxScans) throw Bad("too many scans");
            _frameScans++;
            var sc = new JpegComponent[ns];
            int ss = _d[p + 1 + 2 * ns], se = _d[p + 2 + 2 * ns], ah = _d[p + 3 + 2 * ns] >> 4, al = _d[p + 3 + 2 * ns] & 15;
            if (Lossless)
            {
                // ss selects the predictor, al is the point transform
                if ((_differential ? ss != 0 : ss < 1 || ss > 7) || al >= Precision) throw Bad("bad lossless scan parameters");
            }
            else if (!Progressive) { ss = 0; se = 63; ah = 0; al = 0; }
            else if (ss > se || se > 63 || al > 13 || ah > 13 || (ss == 0 && se != 0) || (ss != 0 && ns != 1))
                throw Bad("bad progressive scan parameters");
            // a streamed sequential frame is complete after its scan; further scans of it are invalid and ignored
            if (_streamDone && !Progressive) return p + n;
            if (!_allocated) Allocate(_len - p, ns);
            if (Arithmetic) _arith ??= new JpegArith();

            for (int i = 0; i < ns; i++)
            {
                int id = _d[p + 1 + 2 * i], tables = _d[p + 2 + 2 * i];
                JpegComponent? c = null;
                foreach (var cand in Comps) if (cand.Id == id) c = cand;
                if (c == null) throw Bad("scan refers to an unknown component");
                for (int j = 0; j < i; j++) if (sc[j] == c) throw Bad("component listed twice in a scan");
                int td = tables >> 4, ta = tables & 15;
                if (td > 3 || ta > 3) throw Bad("bad entropy table selector");
                c.DcTableId = td;
                c.AcTableId = ta;
                if (!Arithmetic)
                {
                    c.Dc = _dc[td];
                    c.Ac = _ac[ta];
                    if ((Lossless || (ss == 0 && ah == 0)) && c.Dc == null) throw Bad("missing DC Huffman table");
                    if (!Lossless && se != 0 && c.Ac == null) throw Bad("missing AC Huffman table");
                }
                if (!Lossless && c.Quant == null)
                {
                    // the table in effect at the component's first scan is the one that applies
                    var q = _quant[c.Tq] ?? throw Bad("missing quantization table");
                    c.QuantArr = GC.AllocateUninitializedArray<int>(64, pinned: true);
                    q.CopyTo(c.QuantArr, 0);
                    fixed (int* qp = c.QuantArr) c.Quant = qp;
                }
                c.DcPred = 0;
                c.DcContext = 0;
                c.Decoded = true;
                if (Lossless) c.PointTransform = al;
                sc[i] = c;
            }

            var br = new JpegBitReader { End = _d + _len };
            br.Reset(_d + p + n);
            if (Arithmetic) _arith!.Start(_d + p + n, _d + _len);
            if (Lossless) DecodeLosslessScan(sc, ss, al, ref br);
            else if (Progressive) DecodeProgressiveScan(sc, ss, se, ah, al, ref br);
            else DecodeSequentialScan(sc, ref br);
            return (int)((Arithmetic ? _arith!.Position : br.P) - _d);
        }

        /// <summary>
        /// Called at a restart boundary with the current read position: finds the next marker and decides how to
        /// continue. Returns false when the coming interval has no data (lost marker or end of scan) and its MCUs
        /// have to be skipped.
        /// </summary>
        static bool NextRestart(ref byte* q, byte* end, ref int expected)
        {
            int want = expected;
            expected = (expected + 1) & 7;
            while (true)
            {
                while (end - q >= 2 && !(q[0] == 0xFF && q[1] != 0 && q[1] != 0xFF)) q++;
                if (end - q < 2) { q = end; return false; }
                int m = q[1];
                if (m < 0xD0 || m > 0xD7) return false; // some other marker: the scan ended early
                int d = (m - 0xD0 - want) & 7;
                if (d == 1 || d == 2) return false; // a marker got lost; this interval is gone
                q += 2;
                if (d >= 6) continue; // stale marker, keep looking
                return true;
            }
        }

        /// <summary>Restart boundary handling shared by all scan types. Returns true if the next interval must be skipped.</summary>
        bool Restart(JpegComponent[] sc, ref JpegBitReader br, ref int expected)
        {
            byte* q = Arithmetic ? _arith!.Position : br.P;
            bool ok = NextRestart(ref q, _d + _len, ref expected);
            if (Arithmetic) _arith!.Start(q, _d + _len); else br.Reset(q);
            foreach (var c in sc) { c.DcPred = 0; c.DcContext = 0; }
            return !ok;
        }

        /// <summary>Inverse transforms a natural order block into its place in the component plane.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void PutBlock(JpegComponent c, short* block, bool dcOnly, int col, int row)
        {
            if (c.RingBlocks != 0) row %= c.RingBlocks;
            if (c.Plane != null && c.S == 8)
            {
                byte* dst = c.Plane + ((nint)row * c.Stride + col) * 8;
                if (dcOnly) JpegDct.IdctDc(block[0] * c.Quant[0], dst, c.Stride);
                else JpegDct.Idct(block, c.Quant, dst, c.Stride);
            }
            else if (c.Plane != null && c.S == 4 && !dcOnly && JpegDct.HasIdct4)
                JpegDct.Idct4(block, c.Quant, c.Plane + ((nint)row * c.Stride + col) * 4, c.Stride);
            else PutBlockGeneric(c, block, dcOnly, col, row);
        }

        [SkipLocalsInit]
        void PutBlockGeneric(JpegComponent c, short* block, bool dcOnly, int col, int row)
        {
            int* v = stackalloc int[64];
            int s = c.S;
            if (!dcOnly && s == 8 && !_differential && c.Plane16 != null && JpegDct.HasWide)
            {
                JpegDct.Idct8Wide(block, c.Quant, c.Plane16 + ((nint)row * c.Stride + col) * 8, c.Stride, Precision == 8 ? 2 : 1, 1 << (Precision - 1), (1 << Precision) - 1);
                return;
            }
            // a lone DC coefficient gives a flat block at every size
            if (dcOnly) new Span<int>(v, s * s).Fill((block[0] * c.Quant[0] + 4) >> 3);
            else JpegDct.IdctGeneric(block, c.Quant, s, Precision == 8 ? 2 : 1, v);
            int center = 1 << (Precision - 1), max = (1 << Precision) - 1;
            if (_differential)
            {
                // a difference image: signed, no level shift
                ushort* dst = c.Plane16 + ((nint)row * c.Stride + col) * s;
                for (int y = 0; y < s; y++, dst += c.Stride)
                    for (int x = 0; x < s; x++) dst[x] = (ushort)(short)Math.Clamp(v[y * s + x], short.MinValue, short.MaxValue);
            }
            else if (c.Plane != null)
            {
                byte* dst = c.Plane + ((nint)row * c.Stride + col) * s;
                for (int y = 0; y < s; y++, dst += c.Stride)
                    for (int x = 0; x < s; x++) dst[x] = (byte)Math.Clamp(v[y * s + x] + center, 0, max);
            }
            else
            {
                ushort* dst = c.Plane16 + ((nint)row * c.Stride + col) * s;
                for (int y = 0; y < s; y++, dst += c.Stride)
                    for (int x = 0; x < s; x++) dst[x] = (ushort)Math.Clamp(v[y * s + x] + center, 0, max);
            }
        }

        void DecodeSequentialScan(JpegComponent[] sc, ref JpegBitReader br)
        {
            int ns = sc.Length;
            bool single = ns == 1, arith = Arithmetic;
            int mcusX = single ? sc[0].ScanBlocksW : _mcusX, mcusY = single ? sc[0].ScanBlocksH : _mcusY;
            short* block = stackalloc short[64];
            int ri = _restartInterval, todo = ri, expected = 0;
            bool skip = false, dead = false;
            var sink = _sink;
            for (int my = 0; my < mcusY; my++)
            {
                Cancel.ThrowIfCancellationRequested();
                if (sink != null)
                {
                    // emit what the rows so far complete, then reuse the oldest slot; data units that never get decoded come out mid gray
                    if (my > 0) sink.Advance(my);
                    foreach (var c in sc) ClearRingUnit(c, my);
                }
                for (int mx = 0; mx < mcusX && !dead; mx++)
                {
                    if (ri != 0)
                    {
                        if (todo == 0)
                        {
                            skip = Restart(sc, ref br, ref expected);
                            todo = ri;
                        }
                        todo--;
                    }
                    if (skip) continue;
                    foreach (var c in sc)
                    {
                        int h = single ? 1 : c.H, v = single ? 1 : c.V;
                        for (int by = 0; by < v; by++)
                        {
                            for (int bx = 0; bx < h; bx++)
                            {
                                Unsafe.InitBlockUnaligned(block, 0, 128);
                                if (_differential) c.DcPred = 0; // differences are coded without DC prediction
                                int last = arith ? _arith!.DecodeBlock(c, block) : DecodeBlock(ref br, c.Dc!, c.Ac!, block, ref c.DcPred);
                                PutBlock(c, block, last == 0, mx * h + bx, my * v + by);
                            }
                        }
                    }
                    if (arith ? _arith!.Corrupt : br.Overrun)
                    {
                        // ran past the end of the data: nothing more to decode until the next restart marker
                        if (ri == 0)
                        {
                            if (sink == null) return;
                            dead = true;
                        }
                        skip = true;
                    }
                }
            }
            if (sink != null)
            {
                sink.Advance(int.MaxValue);
                _streamDone = true;
                ReturnRings();
            }
        }

        // The ring planes of a finished streaming decode serve the next one on the same thread: TIFF strips and tiles
        // are many small JPEG streams. Only small rings are kept.
        [ThreadStatic] static byte[]?[]? t_rings;
        const int MaxCachedRing = 1 << 18;

        static byte[] RentRing(int i, int size)
        {
            var cache = t_rings;
            if (cache?[i] is { } a && a.Length >= size) { cache[i] = null; return a; }
            return GC.AllocateUninitializedArray<byte>(size, pinned: true);
        }

        void ReturnRings()
        {
            for (int i = 0; i < Comps.Length; i++)
            {
                var a = Comps[i].PlaneArr;
                if (a == null || a.Length > MaxCachedRing) continue;
                var cache = t_rings ??= new byte[4][];
                if (cache[i] == null || cache[i]!.Length < a.Length) cache[i] = a;
            }
        }

        /// <summary>Resets the ring slot of unit row <paramref name="unit"/> to mid gray before it is decoded.</summary>
        static void ClearRingUnit(JpegComponent c, int unit)
        {
            int rows = c.UnitRows;
            int first = unit * rows % c.RingRows;
            new Span<byte>(c.Plane + (nint)first * c.Stride, rows * c.Stride).Fill(128);
        }

        /// <summary>Decodes one sequential block into natural order; returns the zigzag index of the last coefficient written.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static int DecodeBlock(ref JpegBitReader br, HuffDecodeTable dcTable, HuffDecodeTable acTable, short* block, ref int dcPred)
        {
            // the bit reader state lives in locals here so the JIT keeps it in registers
            ulong buf = br.Buf;
            int cnt = br.Cnt;
            byte* p = br.P, end = br.End;
            byte* zz = JpegTables.ZigZag;
            int* fast = dcTable.Fast;
            int e, s, v, last = 0;

            if (cnt < 32)
            {
                br.Buf = buf; br.Cnt = cnt; br.P = p;
                br.Refill();
                buf = br.Buf; cnt = br.Cnt; p = br.P;
            }
            e = fast[(int)(buf >> (64 - HuffDecodeTable.LookBits))];
            if (e != 0)
            {
                int n = e & 31;
                buf <<= n; cnt -= n;
                s = (e >> 9) & 15;
                v = e >> 16;
            }
            else
            {
                int peek = (int)(buf >> 48), len = HuffDecodeTable.LookBits + 1;
                int* limit = dcTable.Limit;
                while (peek >= limit[len]) len++;
                if (len > 16) { len = 16; s = 0; }
                else s = dcTable.Values[((peek >> (16 - len)) + dcTable.Offset[len]) & 255] & 15;
                buf <<= len; cnt -= len;
                v = 0;
            }
            if (s != 0)
            {
                v = (int)(buf >> (64 - s));
                buf <<= s; cnt -= s;
                v += ((v - (1 << (s - 1))) >> 31) & (1 - (1 << s));
            }
            dcPred += v;
            block[0] = (short)dcPred;

            fast = acTable.Fast;
            int k = 1;
            do
            {
                if (cnt < 32)
                {
                    if (end - p >= 8)
                    {
                        ulong w = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(p));
                        if (((~w - 0x0101010101010101UL) & w & 0x8080808080808080UL) == 0)
                        {
                            buf |= w >> cnt;
                            p += (63 - cnt) >> 3;
                            cnt |= 56;
                            goto filled;
                        }
                    }
                    br.Buf = buf; br.Cnt = cnt; br.P = p;
                    br.RefillSlow();
                    buf = br.Buf; cnt = br.Cnt; p = br.P;
                }
            filled:
                e = fast[(int)(buf >> (64 - HuffDecodeTable.LookBits))];
                if (e != 0)
                {
                    int n = e & 31;
                    buf <<= n; cnt -= n;
                    if ((e & HuffDecodeTable.NoValueFlag) != 0)
                    {
                        if ((e & 0x1E0) != 0x1E0) break; // EOB
                        k += 16; // ZRL
                        continue;
                    }
                    k += (e >> 5) & 15;
                    s = (e >> 9) & 15;
                    v = e >> 16;
                }
                else
                {
                    int peek = (int)(buf >> 48), len = HuffDecodeTable.LookBits + 1;
                    int* limit = acTable.Limit;
                    while (peek >= limit[len]) len++;
                    int sym;
                    if (len > 16) { len = 16; sym = 0; }
                    else sym = acTable.Values[((peek >> (16 - len)) + acTable.Offset[len]) & 255];
                    buf <<= len; cnt -= len;
                    s = sym & 15;
                    if (s == 0)
                    {
                        if (sym != 0xF0) break;
                        k += 16;
                        continue;
                    }
                    k += sym >> 4;
                    v = 0;
                }
                if (s != 0)
                {
                    v = (int)(buf >> (64 - s));
                    buf <<= s; cnt -= s;
                    v += ((v - (1 << (s - 1))) >> 31) & (1 - (1 << s));
                }
                block[zz[k]] = (short)v;
                last = k;
                k++;
            } while (k < 64);

            br.Buf = buf; br.Cnt = cnt; br.P = p;
            return last;
        }

        void DecodeProgressiveScan(JpegComponent[] sc, int ss, int se, int ah, int al, ref JpegBitReader br)
        {
            int ns = sc.Length;
            bool single = ns == 1, arith = Arithmetic;
            int mcusX = single ? sc[0].ScanBlocksW : _mcusX, mcusY = single ? sc[0].ScanBlocksH : _mcusY;
            int ri = _restartInterval, todo = ri, expected = 0, eobrun = 0;
            bool skip = false;
            for (int my = 0; my < mcusY; my++)
            {
                Cancel.ThrowIfCancellationRequested();
                for (int mx = 0; mx < mcusX; mx++)
                {
                    if (ri != 0)
                    {
                        if (todo == 0)
                        {
                            skip = Restart(sc, ref br, ref expected);
                            todo = ri;
                            eobrun = 0;
                        }
                        todo--;
                    }
                    if (skip) continue;
                    foreach (var c in sc)
                    {
                        int h = single ? 1 : c.H, v = single ? 1 : c.V;
                        for (int by = 0; by < v; by++)
                        {
                            short* blk = c.Coefs + ((nint)(my * v + by) * c.BlocksW + mx * h) * 64;
                            for (int bx = 0; bx < h; bx++, blk += 64)
                            {
                                if (arith)
                                {
                                    if (ss == 0)
                                    {
                                        if (_differential) c.DcPred = 0;
                                        if (ah == 0) _arith!.DecodeDcFirst(c, blk, al);
                                        else _arith!.DecodeDcRefine(blk, al);
                                    }
                                    else if (ah == 0) _arith!.DecodeAcFirst(c, blk, ss, se, al);
                                    else _arith!.DecodeAcRefine(c, blk, ss, se, al);
                                }
                                else if (ss == 0)
                                {
                                    if (ah == 0)
                                    {
                                        if (_differential) c.DcPred = 0;
                                        int s = br.DecodeSymbol(c.Dc!) & 15;
                                        if (s != 0) c.DcPred += br.ReceiveExtend(s);
                                        blk[0] = (short)(c.DcPred << al);
                                    }
                                    else if (br.GetBit() != 0) blk[0] |= (short)(1 << al);
                                }
                                else if (ah == 0)
                                {
                                    if (eobrun > 0) eobrun--;
                                    else DecodeAcFirst(ref br, c.Ac!, blk, ss, se, al, ref eobrun);
                                }
                                else DecodeAcRefine(ref br, c.Ac!, blk, ss, se, al, ref eobrun);
                            }
                        }
                    }
                    if (arith ? _arith!.Corrupt : br.Overrun)
                    {
                        if (ri == 0) return;
                        skip = true;
                    }
                }
            }
        }

        /// <summary>First pass over an AC band (progressive coefficients are stored in zigzag order). The caller handles a pending EOB run.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void DecodeAcFirst(ref JpegBitReader br, HuffDecodeTable table, short* blk, int ss, int se, int al, ref int eobrun)
        {
            // same structure as DecodeBlock: bit reader state in locals
            ulong buf = br.Buf;
            int cnt = br.Cnt;
            byte* p = br.P, end = br.End;
            int* fast = table.Fast;
            int k = ss;
            do
            {
                if (cnt < 32)
                {
                    if (end - p >= 8)
                    {
                        ulong w = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(p));
                        if (((~w - 0x0101010101010101UL) & w & 0x8080808080808080UL) == 0)
                        {
                            buf |= w >> cnt;
                            p += (63 - cnt) >> 3;
                            cnt |= 56;
                            goto filled;
                        }
                    }
                    br.Buf = buf; br.Cnt = cnt; br.P = p;
                    br.RefillSlow();
                    buf = br.Buf; cnt = br.Cnt; p = br.P;
                }
            filled:
                int e = fast[(int)(buf >> (64 - HuffDecodeTable.LookBits))];
                int r, s, v;
                if (e != 0)
                {
                    int n = e & 31;
                    buf <<= n; cnt -= n;
                    r = (e >> 5) & 15;
                    s = (e >> 9) & 15;
                    v = e >> 16;
                    if ((e & HuffDecodeTable.NoValueFlag) != 0) goto noValue;
                }
                else
                {
                    int peek = (int)(buf >> 48), len = HuffDecodeTable.LookBits + 1;
                    int* limit = table.Limit;
                    while (peek >= limit[len]) len++;
                    int sym;
                    if (len > 16) { len = 16; sym = 0; }
                    else sym = table.Values[((peek >> (16 - len)) + table.Offset[len]) & 255];
                    buf <<= len; cnt -= len;
                    r = sym >> 4;
                    s = sym & 15;
                    v = 0;
                    if (s == 0) goto noValue;
                }
                k += r;
                if (s != 0)
                {
                    v = (int)(buf >> (64 - s));
                    buf <<= s; cnt -= s;
                    v += ((v - (1 << (s - 1))) >> 31) & (1 - (1 << s));
                }
                blk[k & 63] = (short)(v << al);
                k++;
                continue;

            noValue:
                if (r == 15) { k += 16; continue; } // ZRL
                // EOBn: this band is finished here and in the next eobrun blocks
                eobrun = (1 << r) - 1;
                if (r != 0)
                {
                    eobrun += (int)(buf >> (64 - r));
                    buf <<= r; cnt -= r;
                }
                break;
            } while (k <= se);
            br.Buf = buf; br.Cnt = cnt; br.P = p;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Refine(ref JpegBitReader br, short* c, int plus, int minus)
        {
            // one more magnitude bit for a coefficient that is already nonzero
            if (br.GetBit() != 0 && (*c & plus) == 0) *c += (short)(*c >= 0 ? plus : minus);
        }

        static void DecodeAcRefine(ref JpegBitReader br, HuffDecodeTable table, short* blk, int ss, int se, int al, ref int eobrun)
        {
            int plus = 1 << al, minus = -1 << al;
            // coefficients of the band that were nonzero before this scan; everything else in it is still zero
            ulong old = JpegTables.NonZeroMask(blk) & JpegTables.BandMask(ss, se);
            int k = ss;
            if (eobrun == 0)
            {
                while (k <= se)
                {
                    int sym = br.DecodeSymbol(table), r = sym >> 4, s = sym & 15, value = 0;
                    if (s == 0)
                    {
                        if (r < 15)
                        {
                            eobrun = 1 << r;
                            if (r != 0) eobrun += br.GetBits(r);
                            break;
                        }
                        // ZRL: skip 16 zero coefficients (r stays 15)
                    }
                    else value = br.GetBit() != 0 ? plus : minus; // a new coefficient always has magnitude 1

                    // step over r still-zero coefficients, stopping at the one after them; every old nonzero
                    // coefficient on the way gets a correction bit
                    while (k <= se)
                    {
                        ulong ahead = old >> k;
                        int gap = ahead == 0 ? 64 : System.Numerics.BitOperations.TrailingZeroCount(ahead);
                        if (gap > r) { k += r; break; }
                        r -= gap;
                        k += gap;
                        Refine(ref br, blk + k, plus, minus);
                        k++;
                    }
                    if (k > se) break;
                    if (value != 0) blk[k] = (short)value;
                    k++;
                }
            }
            if (eobrun > 0)
            {
                // inside an end-of-band run only correction bits remain
                for (ulong rest = k > 63 ? 0 : old >> k << k; rest != 0; rest &= rest - 1)
                    Refine(ref br, blk + System.Numerics.BitOperations.TrailingZeroCount(rest), plus, minus);
                eobrun--;
            }
        }

        /// <summary>Progressive: turn the accumulated coefficients into samples once all scans are in.</summary>
        void IdctAll()
        {
            short* nat = stackalloc short[64];
            if (_sink == null)
            {
                foreach (var c in Comps) IdctRows(c, 0, c.ScanBlocksH, nat);
                return;
            }
            // streaming: one unit row of every component at a time, handed on as soon as it is done
            int units = Comps.Length == 1 ? Comps[0].ScanBlocksH : _mcusY;
            for (int u = 0; u < units; u++)
            {
                Cancel.ThrowIfCancellationRequested();
                if (u > 0) _sink.Advance(u);
                foreach (var c in Comps)
                {
                    ClearRingUnit(c, u);
                    int unit = c.UnitRows / c.S;
                    IdctRows(c, u * unit, Math.Min((u + 1) * unit, c.ScanBlocksH), nat);
                }
            }
            _sink.Advance(int.MaxValue);
            _streamDone = true;
            ReturnRings();
        }

        void IdctRows(JpegComponent c, int by0, int by1, short* nat)
        {
            if (c.Quant == null) return; // never appeared in a scan: stays gray
            byte* zz = JpegTables.ZigZag;
            for (int by = by0; by < by1; by++)
            {
                short* blk = c.Coefs + (nint)by * c.BlocksW * 64;
                for (int bx = 0; bx < c.ScanBlocksW; bx++, blk += 64)
                {
                    ulong mask = JpegTables.NonZeroMask(blk) & ~1UL;
                    // zigzag -> natural order
                    Unsafe.InitBlockUnaligned(nat, 0, 128);
                    nat[0] = blk[0];
                    for (ulong m = mask; m != 0; m &= m - 1)
                    {
                        int k = System.Numerics.BitOperations.TrailingZeroCount(m);
                        nat[zz[k]] = blk[k];
                    }
                    PutBlock(c, nat, mask == 0, bx, by);
                }
            }
        }

        // ---------------------------------------------------------------- lossless (T.81 Annex H)

        /// <summary>One prediction difference: Huffman coded magnitude category 0..16, then that many bits (none for 16 = 32768).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int DecodeDifference(ref JpegBitReader br, HuffDecodeTable table)
        {
            if (br.Cnt < 32) br.Refill();
            int e = table.Fast[(int)(br.Buf >> (64 - HuffDecodeTable.LookBits))], s;
            if (e != 0)
            {
                int n = e & 31;
                br.Buf <<= n; br.Cnt -= n;
                // the table was built for run/size symbols: category 16 looks like "run 1, size 0"
                if ((e & HuffDecodeTable.NoValueFlag) != 0) return (e & 0x1E0) != 0 ? 32768 : 0;
                s = (e >> 9) & 15;
                if (s == 0) return e >> 16;
            }
            else
            {
                s = br.DecodeSymbol(table);
                if (s == 0) return 0;
                if (s >= 16) return 32768;
            }
            int v = (int)(br.Buf >> (64 - s));
            br.Buf <<= s; br.Cnt -= s;
            return v + (((v - (1 << (s - 1))) >> 31) & (1 - (1 << s)));
        }

        void DecodeLosslessScan(JpegComponent[] sc, int predictor, int pt, ref JpegBitReader br)
        {
            int ns = sc.Length;
            bool single = ns == 1;
            int mcusX = single ? sc[0].ScanBlocksW : _mcusX, mcusY = single ? sc[0].ScanBlocksH : _mcusY;
            int initial = 1 << (Precision - pt - 1);
            int ri = _restartInterval, todo = ri, expected = 0;
            // prediction restarts at the top of the scan and after every restart marker
            int topRow = 0, firstCol = 0;
            bool skip = false;

            // the usual layout - one sample of every component per MCU, restarts (if any) at line starts - goes line by line
            if (_differential) predictor = NoPrediction;
            if (Arithmetic)
            {
                foreach (var c in sc) c.Differences = new short[c.Stride * c.BlocksH];
            }
            bool simple = ri % mcusX == 0 && !Arithmetic;
            foreach (var c in sc) simple &= single || (c.H == 1 && c.V == 1);
            if (simple)
            {
                ushort** cur = stackalloc ushort*[4];
                int** fast = stackalloc int*[4];
                for (int my = 0; my < mcusY; my++)
                {
                    Cancel.ThrowIfCancellationRequested();
                    bool top = my == 0;
                    if (ri != 0 && my != 0 && (long)my * mcusX % ri == 0)
                    {
                        skip = Restart(sc, ref br, ref expected);
                        top = true;
                    }
                    if (skip) continue;
                    for (int i = 0; i < ns; i++)
                    {
                        cur[i] = sc[i].Plane16 + (nint)my * sc[i].Stride;
                        fast[i] = sc[i].Dc!.Fast;
                    }
                    DecodeLosslessLine(ref br, sc, cur, fast, mcusX, top && !_differential ? 0 : predictor, initial);
                    if (br.Overrun)
                    {
                        if (ri == 0) return;
                        skip = true;
                    }
                }
                return;
            }

            for (int my = 0; my < mcusY; my++)
            {
                Cancel.ThrowIfCancellationRequested();
                for (int mx = 0; mx < mcusX; mx++)
                {
                    if (ri != 0)
                    {
                        if (todo == 0)
                        {
                            skip = Restart(sc, ref br, ref expected);
                            todo = ri;
                            topRow = my; firstCol = mx;
                        }
                        todo--;
                    }
                    if (skip) continue;
                    foreach (var c in sc)
                    {
                        int h = single ? 1 : c.H, v = single ? 1 : c.V;
                        nint stride = c.Stride;
                        var table = c.Dc!;
                        fixed (short* diffs = c.Differences)
                        for (int by = 0; by < v; by++)
                        {
                            ushort* row = c.Plane16 + (nint)(my * v + by) * stride;
                            bool top = my == topRow && by == 0;
                            for (int bx = 0; bx < h; bx++)
                            {
                                int x = mx * h + bx, pred;
                                if (predictor == NoPrediction) pred = 0;
                                else if (top) pred = mx == firstCol && bx == 0 ? initial : row[x - 1];
                                else if (x == 0) pred = row[-stride];
                                else
                                {
                                    int ra = row[x - 1], rb = row[x - stride], rc = row[x - stride - 1];
                                    pred = predictor switch
                                    {
                                        1 => ra,
                                        2 => rb,
                                        3 => rc,
                                        4 => ra + rb - rc,
                                        5 => ra + ((rb - rc) >> 1),
                                        6 => rb + ((ra - rc) >> 1),
                                        _ => (ra + rb) >> 1,
                                    };
                                }
                                int diff;
                                if (Arithmetic)
                                {
                                    // conditioned on the differences coded to the left and above (none at the start of a line / interval)
                                    short* dr = diffs + (nint)(my * v + by) * stride;
                                    int da = x == 0 || (my == topRow && mx == firstCol && bx == 0) ? 0 : dr[x - 1];
                                    int db = top ? 0 : dr[x - stride];
                                    diff = _arith!.DecodeLosslessDifference(c.DcTableId, da, db);
                                    dr[x] = (short)diff;
                                }
                                else diff = DecodeDifference(ref br, table);
                                row[x] = (ushort)(pred + diff);
                            }
                        }
                    }
                    if (Arithmetic ? _arith!.Corrupt : br.Overrun)
                    {
                        if (ri == 0) return;
                        skip = true;
                    }
                }
            }
        }

        /// <summary>Predictor selection value used for differential frames: the sample is the difference itself.</summary>
        const int NoPrediction = 8;

        /// <summary>
        /// One line of a lossless scan whose components are all sampled 1 x 1. <paramref name="predictor"/> 0 marks the
        /// first line (of the scan or after a restart), which predicts from the left only.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void DecodeLosslessLine(ref JpegBitReader br, JpegComponent[] sc, ushort** cur, int** fast, int width, int predictor, int initial)
        {
            // bit reader state in locals, as in DecodeBlock
            ulong buf = br.Buf;
            int cnt = br.Cnt;
            byte* p = br.P, end = br.End;
            int ns = sc.Length;
            for (int x = 0; x < width; x++)
            {
                for (int i = 0; i < ns; i++)
                {
                    if (cnt < 32)
                    {
                        if (end - p >= 8)
                        {
                            ulong w = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(p));
                            if (((~w - 0x0101010101010101UL) & w & 0x8080808080808080UL) == 0)
                            {
                                buf |= w >> cnt;
                                p += (63 - cnt) >> 3;
                                cnt |= 56;
                                goto filled;
                            }
                        }
                        br.Buf = buf; br.Cnt = cnt; br.P = p;
                        br.RefillSlow();
                        buf = br.Buf; cnt = br.Cnt; p = br.P;
                    }
                filled:
                    int e = fast[i][(int)(buf >> (64 - HuffDecodeTable.LookBits))], s, d;
                    if (e != 0)
                    {
                        int n = e & 31;
                        buf <<= n; cnt -= n;
                        s = (e >> 9) & 15;
                        d = e >> 16;
                        // category 16 (a difference of 32768, no extra bits) looks like "run 1, size 0" to the table builder
                        if ((e & HuffDecodeTable.NoValueFlag) != 0) d = (e & 0x1E0) != 0 ? 32768 : 0;
                    }
                    else
                    {
                        br.Buf = buf; br.Cnt = cnt; br.P = p;
                        s = br.DecodeSymbol(sc[i].Dc!);
                        buf = br.Buf; cnt = br.Cnt; p = br.P;
                        d = 0;
                        if (s >= 16) { s = 0; d = 32768; }
                    }
                    if (s != 0)
                    {
                        d = (int)(buf >> (64 - s));
                        buf <<= s; cnt -= s;
                        d += ((d - (1 << (s - 1))) >> 31) & (1 - (1 << s));
                    }

                    ushort* row = cur[i];
                    int pred;
                    if (predictor == 0) pred = x == 0 ? initial : row[x - 1];
                    else if (predictor == NoPrediction) pred = 0;
                    else
                    {
                        ushort* above = row - sc[i].Stride;
                        if (x == 0) pred = above[0];
                        else
                        {
                            int ra = row[x - 1], rb = above[x], rc = above[x - 1];
                            pred = predictor switch
                            {
                                1 => ra,
                                2 => rb,
                                3 => rc,
                                4 => ra + rb - rc,
                                5 => ra + ((rb - rc) >> 1),
                                6 => rb + ((ra - rc) >> 1),
                                _ => (ra + rb) >> 1,
                            };
                        }
                    }
                    row[x] = (ushort)(pred + d);
                }
            }
            br.Buf = buf; br.Cnt = cnt; br.P = p;
        }

        /// <summary>Undoes the point transform and blanks components no scan delivered.</summary>
        void FinishLossless()
        {
            foreach (var c in Comps)
            {
                if (c.PointTransform == 0) continue;
                var plane = c.Plane16Arr.AsSpan();
                for (int i = 0; i < plane.Length; i++) plane[i] = (ushort)(plane[i] << c.PointTransform);
            }
        }
    }

    internal static class JpegMeta
    {
        /// <summary>Extracts the Orientation tag (0x0112) from IFD0 of an EXIF blob; 0 if absent or malformed.</summary>
        public static int ExifOrientation(ReadOnlySpan<byte> e)
        {
            if (e.Length < 8) return 0;
            bool le;
            if (e[0] == 'I' && e[1] == 'I') le = true;
            else if (e[0] == 'M' && e[1] == 'M') le = false;
            else return 0;
            if (R16(e, 2, le) != 42) return 0;
            long ifd = R32(e, 4, le);
            if (ifd + 2 > e.Length) return 0;
            int count = R16(e, (int)ifd, le);
            for (int i = 0; i < count; i++)
            {
                long entry = ifd + 2 + i * 12L;
                if (entry + 12 > e.Length) return 0;
                if (R16(e, (int)entry, le) != 0x0112) continue;
                int type = R16(e, (int)entry + 2, le);
                int v = type == 3 ? R16(e, (int)entry + 8, le) : type == 4 || type == 9 ? (int)R32(e, (int)entry + 8, le) : type == 1 ? e[(int)entry + 8] : 0;
                return v >= 1 && v <= 8 ? v : 0;
            }
            return 0;
        }

        static int R16(ReadOnlySpan<byte> e, int o, bool le) => le ? e[o] | e[o + 1] << 8 : e[o] << 8 | e[o + 1];
        static uint R32(ReadOnlySpan<byte> e, int o, bool le)
            => le ? (uint)(e[o] | e[o + 1] << 8 | e[o + 2] << 16 | e[o + 3] << 24) : (uint)(e[o] << 24 | e[o + 1] << 16 | e[o + 2] << 8 | e[o + 3]);
    }
}
