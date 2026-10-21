using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Aardvark.ImageCodecs.JpegXrImpl
{
    /// <summary>An AdaptiveVLC data structure instance (5.5.5).</summary>
    internal struct AdaptiveVlc
    {
        public int Disc1, Disc2, Table, Delta, Delta2;

        public void Init1() { Table = 0; Delta = 0; Disc1 = 0; }
        public void Init2() { Table = 1; Delta = 0; Delta2 = 1; Disc1 = 0; Disc2 = 0; }

        /// <summary>AdaptVLCTable1( ) (Table 101).</summary>
        public void Adapt1()
        {
            if (Disc1 < -8 && Table != 0) { Table--; Disc1 = 0; }
            else if (Disc1 > 8 && Table != 1) { Table++; Disc1 = 0; }
            else Disc1 = Math.Clamp(Disc1, -64, 64);
        }

        /// <summary>AdaptVLCTable2( ) (Table 102).</summary>
        public void Adapt2(int maxTable)
        {
            bool change = false;
            if (Disc1 < -8 && Table != 0) { Table--; change = true; }
            else if (Disc2 > 8 && Table != maxTable) { Table++; change = true; }
            if (change)
            {
                Disc1 = Disc2 = 0;
                if (Table == maxTable) { Delta = Table - 1; Delta2 = Table - 1; }
                else if (Table == 0) { Delta = 0; Delta2 = 0; }
                else { Delta = Table - 1; Delta2 = Table; }
            }
            else
            {
                Disc1 = Math.Clamp(Disc1, -64, 64);
                Disc2 = Math.Clamp(Disc2, -64, 64);
            }
        }
    }

    /// <summary>A Model data structure instance for adaptive coefficient normalization (5.5.6).</summary>
    internal struct JxrModel
    {
        public int State0, State1, Bits0, Bits1;

        public void Init(int band)
        {
            State0 = State1 = 0;
            Bits0 = Bits1 = (2 - band) * 4;
        }

        /// <summary>UpdateModelMB( ) (Table 116).</summary>
        public void Update(int lap0, int lap1, int band, JxrInternalFormat fmt, int nc)
        {
            lap0 *= JxrTables.Weight0[band];
            if (fmt == JxrInternalFormat.Yuv420) lap1 *= JxrTables.Weight2[band];
            else if (fmt == JxrInternalFormat.Yuv422) lap1 *= JxrTables.Weight2[3 + band];
            else
            {
                lap1 *= JxrTables.Weight1[band][Math.Min(nc - 1, 15)];
                if (band == 2) lap1 >>= 4;
            }
            Step(ref State0, ref Bits0, lap0);
            if (fmt != JxrInternalFormat.YOnly) Step(ref State1, ref Bits1, lap1);
        }

        static void Step(ref int ms, ref int bits, int lap)
        {
            int delta = (lap - 70) >> 2;
            if (delta <= -8)
            {
                delta += 4;
                if (delta < -16) delta = -16;
                ms += delta;
                if (ms < -8)
                {
                    if (bits == 0) ms = -8;
                    else { ms = 0; bits--; }
                }
            }
            else if (delta >= 8)
            {
                delta -= 4;
                if (delta > 15) delta = 15;
                ms += delta;
                if (ms > 8)
                {
                    if (bits >= 15) { bits = 15; ms = 8; }
                    else { ms = 0; bits++; }
                }
            }
        }
    }

    /// <summary>
    /// One image plane (primary or alpha) of a JPEG XR codestream: its parsing state (adaptive VLC tables, models, scan
    /// orders), the per macroblock prediction state, and the coefficient arrays that become the samples. The parsing follows
    /// clause 8, the coefficient decoding 9.4 .. 9.8, the sample reconstruction 9.9.
    /// </summary>
    internal sealed partial class JxrPlane
    {
        public readonly JxrImageHeader Ih;
        public readonly JxrPlaneHeader Ph;
        public readonly JxrInternalFormat Fmt;
        public readonly int Nc;
        public readonly JxrBands Bands;
        public readonly bool Is42x, Is420, Is422;
        /// <summary>The alpha plane of a codestream with ALPHA_IMAGE_PLANE_FLAG.</summary>
        public readonly bool IsAlpha;
        readonly int _mbw, _mbh;

        /// <summary>
        /// The samples of each component (extended size), coefficients until reconstruction is done; block-major (see
        /// <see cref="JxrTransform.At"/>), with DcW blocks per row.
        /// </summary>
        public readonly int[][] Px;
        public readonly int[] PxW, PxH;
        /// <summary>The DC-LP arrays (one coefficient per 4x4 block).</summary>
        public readonly int[][] Dc;
        public readonly int[] DcW, DcH;
        /// <summary>Width and height of a macroblock of a component in samples (16 or 8).</summary>
        readonly int[] _mbPxW, _mbPxH;

        // per macroblock state of the whole plane
        readonly int[] _pred;          // PredDCLP: 7 values per component per macroblock
        readonly int[] _cbphp;         // MBCBPHP per component per macroblock
        readonly byte[] _qpLp, _qpHp;  // MBQPIndexLP / MBQPIndexHP
        readonly byte[] _modelBitsHp;  // ModelBitsMBHP: 2 per macroblock
        readonly byte[] _hpMode;       // MBHPMode per macroblock
        readonly int[] _dcIn;          // DCInput per macroblock and component (frequency mode: DC parsed before LP)

        // tile QP state
        byte[] _dcQ = Array.Empty<byte>();
        readonly byte[][] _lpQ = new byte[16][], _hpQ = new byte[16][];
        int _numLp = 1, _numHp = 1;
        bool _useDcQp, _useLpQp;
        public int TrimFlexbits;
        // dequantization factors of the current tile, [qp index][component]
        readonly int[] _dcFactor;
        readonly int[][] _lpFactor = new int[16][], _hpFactor = new int[16][];

        // adaptive VLC state (5.5.5)
        AdaptiveVlc _absDcLum, _absDcChr;
        AdaptiveVlc _firstLpLum, _indLpLum0, _indLpLum1, _firstLpChr, _indLpChr0, _indLpChr1, _absLp0, _absLp1;
        AdaptiveVlc _firstHpLum, _indHpLum0, _indHpLum1, _firstHpChr, _indHpChr0, _indHpChr1, _absHp0, _absHp1;
        AdaptiveVlc _numCbphp, _numBlkCbphp;
        JxrModel _modelDc, _modelLp, _modelHp;
        int _cbphpState0, _cbphpState1, _countOnes0, _countOnes1, _countZeroes0, _countZeroes1;
        int _countZeroCbplp, _countMaxCbplp;
        readonly int[] _lpScan = new int[16], _lpTotals = new int[16];
        readonly int[] _hpHor = new int[16], _hpHorTotals = new int[16], _hpVer = new int[16], _hpVerTotals = new int[16];

        // per macroblock work arrays
        readonly int[] _lpIn;     // LPInput[component][16]
        readonly int[] _mb;       // MbDCLP[component][16] of the current macroblock
        readonly int[] _rl = new int[32];
        readonly int[] _diffCbphp;

        public JxrPlane(JxrImageHeader ih, JxrPlaneHeader ph, bool alpha)
        {
            IsAlpha = alpha;
            Ih = ih;
            Ph = ph;
            Fmt = ph.Format;
            Nc = ph.NumComponents;
            Bands = ph.Bands;
            Is420 = Fmt == JxrInternalFormat.Yuv420;
            Is422 = Fmt == JxrInternalFormat.Yuv422;
            Is42x = Is420 || Is422;
            _mbw = ih.MbWidth;
            _mbh = ih.MbHeight;
            int mbs = checked(_mbw * _mbh);
            Px = new int[Nc][];
            PxW = new int[Nc];
            PxH = new int[Nc];
            Dc = new int[Nc][];
            DcW = new int[Nc];
            DcH = new int[Nc];
            _mbPxW = new int[Nc];
            _mbPxH = new int[Nc];
            for (int c = 0; c < Nc; c++)
            {
                bool sub = c > 0 && Is42x;
                _mbPxW[c] = sub ? 8 : 16;
                _mbPxH[c] = c > 0 && Is420 ? 8 : 16;
                PxW[c] = _mbw * _mbPxW[c];
                PxH[c] = _mbh * _mbPxH[c];
                Px[c] = new int[checked(PxW[c] * PxH[c])];
                DcW[c] = PxW[c] / 4;
                DcH[c] = PxH[c] / 4;
                Dc[c] = new int[DcW[c] * DcH[c]];
            }
            _pred = new int[checked(mbs * Nc * 7)];
            bool hp = Bands is JxrBands.All or JxrBands.NoFlexbits;
            _cbphp = new int[hp ? mbs * Nc : 0];
            _qpLp = new byte[mbs];
            _qpHp = new byte[mbs];
            _modelBitsHp = new byte[hp ? mbs * 2 : 0];
            _hpMode = new byte[mbs];
            _dcIn = new int[ih.Frequency ? mbs * Nc : 0];
            _lpIn = new int[Nc * 16];
            _mb = new int[Nc * 16];
            _diffCbphp = new int[Nc];
            _dcFactor = new int[Nc];
        }

        // ------------------------------------------------------------------ tile headers and QPs (8.7.4, 8.7.6, 8.7.8, 9.7)

        public void ReadTileHeaderDc(JxrBitReader r)
        {
            _dcQ = Ph.DcUniform ? Ph.DcQp! : JxrQp.Read(r, Nc);
            for (int c = 0; c < Nc; c++) _dcFactor[c] = JxrQp.Map(_dcQ[c], c == 0 ? 1 : 0, Ph.Scaled);
        }

        public void ReadTileHeaderLp(JxrBitReader r)
        {
            _useDcQp = false;
            if (Ph.LpUniform) { _numLp = 1; _lpQ[0] = Ph.LpQp!; }
            else
            {
                _useDcQp = r.Bit();
                if (_useDcQp) { _numLp = 1; _lpQ[0] = _dcQ; }
                else
                {
                    _numLp = r.Bits(4) + 1;
                    for (int q = 0; q < _numLp; q++) _lpQ[q] = JxrQp.Read(r, Nc);
                }
            }
            for (int q = 0; q < _numLp; q++)
            {
                var f = _lpFactor[q] ??= new int[Nc];
                for (int c = 0; c < Nc; c++) f[c] = JxrQp.Map(_lpQ[q][c], c == 0 ? 1 : 0, Ph.Scaled);
            }
        }

        public void ReadTileHeaderHp(JxrBitReader r)
        {
            _useLpQp = false;
            if (Ph.HpUniform) { _numHp = 1; _hpQ[0] = Ph.HpQp!; }
            else
            {
                _useLpQp = r.Bit();
                if (_useLpQp)
                {
                    _numHp = _numLp;
                    for (int q = 0; q < _numLp; q++) _hpQ[q] = _lpQ[q];
                }
                else
                {
                    _numHp = r.Bits(4) + 1;
                    for (int q = 0; q < _numHp; q++) _hpQ[q] = JxrQp.Read(r, Nc);
                }
            }
            for (int q = 0; q < _numHp; q++)
            {
                var f = _hpFactor[q] ??= new int[Nc];
                for (int c = 0; c < Nc; c++) f[c] = JxrQp.Map(_hpQ[q][c], 1, Ph.Scaled);
            }
        }

        /// <summary>The DC-only plane (BANDS_PRESENT DCONLY) has no LP tile header: LP QPs then do not matter.</summary>
        public void NoLpHeader() { _numLp = 1; _useDcQp = false; _lpQ[0] = _dcQ; _lpFactor[0] ??= new int[Nc]; }

        static int DecodeQpIndex(JxrBitReader r, int numQp)
        {
            if (!r.Bit()) return 0;
            int idx = r.Bits(JxrTables.BitsQpIndex[numQp]) + 1;
            if (idx >= numQp) throw new ImageFormatException("JPEG XR: QP index out of range");
            return idx;
        }

        /// <summary>LP_QP_INDEX of a macroblock, read where the LP band starts (spatial mode: before MB_DC).</summary>
        public void ReadLpQpIndex(JxrBitReader r, int mb)
        {
            _qpLp[mb] = (byte)(_numLp > 1 && !_useDcQp ? DecodeQpIndex(r, _numLp) : 0);
            if (_useLpQp) _qpHp[mb] = _qpLp[mb];
        }

        public void ReadHpQpIndex(JxrBitReader r, int mb)
        {
            if (_numHp > 1 && !_useLpQp) _qpHp[mb] = (byte)DecodeQpIndex(r, _numHp);
            else if (!_useLpQp) _qpHp[mb] = 0;
            else _qpHp[mb] = _qpLp[mb];
        }

        // ------------------------------------------------------------------ macroblock context

        int _mbx, _mby, _mbi, _tileLeft, _tileRight, _tileTop;
        bool _leftEdge, _topEdge, _tileStart, _resetContext, _resetTotals;

        public void SetMacroblock(int mbx, int mby, int tileLeft, int tileRight, int tileTop)
        {
            _mbx = mbx;
            _mby = mby;
            _mbi = mby * _mbw + mbx;
            _tileLeft = tileLeft;
            _tileRight = tileRight;
            _tileTop = tileTop;
            _leftEdge = mbx == tileLeft;
            _topEdge = mby == tileTop;
            _tileStart = _leftEdge && _topEdge;
            _resetTotals = (mbx - tileLeft) % 16 == 0;
            _resetContext = mbx == tileRight - 1 || _resetTotals;
        }

        public int MbIndex => _mbi;

        // ------------------------------------------------------------------ DC band (8.7.11 .. 8.7.14)

        public void ParseMbDc(JxrBitReader r, Span<int> dcIn)
        {
            if (_tileStart)
            {
                _absDcLum.Init1();
                _absDcChr.Init1();
                _modelDc.Init(0);
            }
            int lap0 = 0, lap1 = 0;
            if (Fmt is JxrInternalFormat.YOnly or JxrInternalFormat.Yuvk or JxrInternalFormat.NComponent)
            {
                for (int n = 0; n < Nc; n++)
                {
                    bool abs = r.Bit();
                    if (abs) { if (n == 0) lap0++; else lap1++; }
                    dcIn[n] = DecodeDc(r, n == 0 ? _modelDc.Bits0 : _modelDc.Bits1, false, abs);
                }
            }
            else
            {
                int v = r.Decode(JxrTables.ValDcYuv);
                bool a0 = (v & 4) != 0, a1 = (v & 2) != 0, a2 = (v & 1) != 0;
                if (a0) lap0++;
                dcIn[0] = DecodeDc(r, _modelDc.Bits0, false, a0);
                if (a1) lap1++;
                dcIn[1] = DecodeDc(r, _modelDc.Bits1, true, a1);
                if (a2) lap1++;
                dcIn[2] = DecodeDc(r, _modelDc.Bits1, true, a2);
            }
            _modelDc.Update(lap0, lap1, 0, Fmt, Nc);
            if (_resetContext)
            {
                _absDcLum.Adapt1();
                _absDcChr.Adapt1();
            }
        }

        public Span<int> DcInputOf(int mb) => _dcIn.AsSpan(mb * Nc, Nc);

        int DecodeDc(JxrBitReader r, int modelBits, bool chroma, bool absLevel)
        {
            int dc = 0;
            if (absLevel) dc = DecodeAbsLevel(r, ref chroma ? ref _absDcChr : ref _absDcLum) - 1;
            if (modelBits != 0) dc = (dc << modelBits) | r.Bits(modelBits);
            if (dc != 0 && r.Bit()) dc = -dc;
            return dc;
        }

        static int DecodeAbsLevel(JxrBitReader r, ref AdaptiveVlc a)
        {
            int idx = r.Decode(JxrTables.AbsLevelIndex[a.Table]);
            a.Disc1 += JxrTables.AbsLevelIndexDelta[idx];
            if (idx < 6)
            {
                int level = JxrTables.AbsRemap[idx];
                int fixedLen = JxrTables.AbsFixedLen[idx];
                if (fixedLen > 0) level += r.Bits(fixedLen);
                return level;
            }
            int f = r.Bits(4) + 4;
            if (f == 19)
            {
                f += r.Bits(2);
                if (f == 22) f += r.Bits(3);
            }
            if (f > 30) throw new ImageFormatException("JPEG XR: coefficient level out of range");
            return 2 + (1 << f) + r.Bits(f);
        }

        // ------------------------------------------------------------------ block coding (8.7.18.5 .. 8.7.18.9)

        ref AdaptiveVlc AbsVlc(int band, int ctx)
            => ref band == 1 ? ref (ctx != 0 ? ref _absLp1 : ref _absLp0) : ref (ctx != 0 ? ref _absHp1 : ref _absHp0);

        /// <summary>DECODE_BLOCK( ): run / level pairs into coeff[0..], returns the number of non-zero coefficients.</summary>
        int DecodeBlock(JxrBitReader r, bool chroma, int[] coeff, int band, int location)
        {
            int nz = 1;
            ref AdaptiveVlc fv = ref band == 1 ? ref (chroma ? ref _firstLpChr : ref _firstLpLum) : ref (chroma ? ref _firstHpChr : ref _firstHpLum);
            int first = r.Decode(JxrTables.FirstIndex[fv.Table]);
            fv.Disc1 += JxrTables.FirstIndexDelta[fv.Delta][first];
            fv.Disc2 += JxrTables.FirstIndexDelta[fv.Delta2][first];
            bool sign = r.Bit();
            int sr = first & 1, srn = first >> 2;
            int ctx = sr & srn;
            int level = (first & 2) != 0 ? DecodeAbsLevel(r, ref AbsVlc(band, ctx)) : 1;
            coeff[1] = sign ? -level : level;
            coeff[0] = sr == 0 ? DecodeRun(r, 15 - location) : 0;
            location += coeff[0] + 1;
            while (srn != 0)
            {
                sr = srn & 1;
                int run = sr == 0 ? DecodeRun(r, 15 - location) : 0;
                coeff[nz * 2] = run;
                location += run + 1;
                if (location > 16) throw new ImageFormatException("JPEG XR: run beyond the end of a block");
                int idx;
                if (location < 15)
                {
                    ref AdaptiveVlc iv = ref band == 1
                        ? ref (chroma ? ref (ctx != 0 ? ref _indLpChr1 : ref _indLpChr0) : ref (ctx != 0 ? ref _indLpLum1 : ref _indLpLum0))
                        : ref (chroma ? ref (ctx != 0 ? ref _indHpChr1 : ref _indHpChr0) : ref (ctx != 0 ? ref _indHpLum1 : ref _indHpLum0));
                    idx = r.Decode(JxrTables.IndexA[iv.Table]);
                    iv.Disc1 += JxrTables.Index1Delta[iv.Delta][idx];
                    iv.Disc2 += JxrTables.Index1Delta[iv.Delta2][idx];
                }
                else if (location == 15) idx = r.Decode(JxrTables.IndexB);
                else idx = r.Bit() ? 1 : 0;
                srn = idx >> 1;
                ctx &= srn;
                sign = r.Bit();
                level = (idx & 1) != 0 ? DecodeAbsLevel(r, ref AbsVlc(band, ctx)) : 1;
                coeff[nz * 2 + 1] = sign ? -level : level;
                nz++;
                if (nz > 16) throw new ImageFormatException("JPEG XR: too many coefficients in a block");
            }
            return nz;
        }

        static int DecodeRun(JxrBitReader r, int maxRun)
        {
            if (maxRun < 5)
            {
                if (maxRun <= 1) return 1;
                return r.Decode(JxrTables.RunValue[maxRun]) + 1;
            }
            int idx = r.Decode(JxrTables.RunIndex) + 5 * JxrTables.RunBin[maxRun];
            int run = JxrTables.RunRemap[idx];
            int f = JxrTables.RunFixedLength[idx];
            if (f > 0) run += r.Bits(f);
            return run;
        }

        // ------------------------------------------------------------------ LP band (8.7.16)

        static readonly int[] RemapArr = { 4, 1, 2, 3, 5, 6, 7 };

        public void ParseMbLp(JxrBitReader r)
        {
            Span<int> temp = stackalloc int[14];
            if (_tileStart)
            {
                _countZeroCbplp = _countMaxCbplp = 1;
                _firstLpLum.Init2(); _indLpLum0.Init2(); _indLpLum1.Init2();
                _firstLpChr.Init2(); _indLpChr0.Init2(); _indLpChr1.Init2();
                _absLp0.Init1(); _absLp1.Init1();
                for (int i = 1; i <= 15; i++) { _lpScan[i] = JxrTables.ScanOrder0[i]; _lpTotals[i] = JxrTables.ScanTotals[i]; }
                _modelLp.Init(1);
            }
            if (_resetTotals)
                for (int i = 1; i <= 15; i++) _lpTotals[i] = JxrTables.ScanTotals[i];
            int lap0 = 0, lap1 = 0;
            int fullPlanes = Is42x ? 2 : Nc;
            int cbplp;
            if (Fmt is JxrInternalFormat.Yuv420 or JxrInternalFormat.Yuv422 or JxrInternalFormat.Yuv444)
            {
                int max = fullPlanes * 4 - 5;
                if (_countZeroCbplp <= 0 || _countMaxCbplp < 0)
                {
                    int v = r.Decode(Fmt == JxrInternalFormat.Yuv444 ? JxrTables.CbplpYuv444 : JxrTables.CbplpYuv42x);
                    cbplp = _countMaxCbplp < _countZeroCbplp ? max - v : v;
                }
                else cbplp = r.Bits(fullPlanes);
                _countZeroCbplp = Math.Clamp(_countZeroCbplp + 1 - (cbplp == 0 ? 4 : 0), -8, 7);
                _countMaxCbplp = Math.Clamp(_countMaxCbplp + 1 - (cbplp == max ? 4 : 0), -8, 7);
            }
            else
            {
                cbplp = 0;
                for (int n = 0; n < Nc; n++) if (r.Bit()) cbplp |= 1 << n;
            }
            Array.Clear(_lpIn);
            var rl = _rl;
            for (int n = 0; n < fullPlanes; n++)
            {
                int index = n == 0 ? 0 : 1;
                int numNz = 0;
                if (((cbplp >> n) & 1) != 0)
                {
                    Array.Clear(rl);
                    int location = Is420 && n != 0 ? 10 : Is422 && n != 0 ? 2 : 1;
                    numNz = DecodeBlock(r, index != 0, rl, 1, location);
                    if (Is42x && n != 0)
                    {
                        temp.Clear();
                        int i = 0;
                        for (int k = 0; k < numNz; k++)
                        {
                            i += rl[k * 2];
                            if (i >= 14) throw new ImageFormatException("JPEG XR: chroma LP run out of range");
                            temp[i] = rl[k * 2 + 1];
                            i++;
                        }
                        int remapOffset = Is420 ? 1 : 0, count = Is422 ? 14 : 6;
                        var tr = Is420 ? JxrTables.Transpose420 : JxrTables.Transpose422;
                        for (int k = 0; k < count; k++)
                            _lpIn[((k & 1) + 1) * 16 + tr[RemapArr[(k >> 1) + remapOffset]]] = temp[k];
                    }
                    else
                    {
                        int i = 1;
                        for (int k = 0; k < numNz; k++)
                        {
                            i += rl[k * 2];
                            if (i > 15) throw new ImageFormatException("JPEG XR: LP run out of range");
                            // AdaptiveLPScan( )
                            _lpIn[n * 16 + _lpScan[i]] = rl[k * 2 + 1];
                            _lpTotals[i]++;
                            if (i > 1 && _lpTotals[i] > _lpTotals[i - 1])
                            {
                                (_lpTotals[i], _lpTotals[i - 1]) = (_lpTotals[i - 1], _lpTotals[i]);
                                (_lpScan[i], _lpScan[i - 1]) = (_lpScan[i - 1], _lpScan[i]);
                            }
                            i++;
                        }
                    }
                }
                int bits = index == 0 ? _modelLp.Bits0 : _modelLp.Bits1;
                if (index == 0) lap0 += numNz; else lap1 += numNz;
                if (bits != 0)
                {
                    if (Is420 && n != 0)
                        for (int k = 1; k < 4; k++)
                        {
                            int t = JxrTables.Transpose420[k];
                            _lpIn[16 + t] = RefineLp(r, _lpIn[16 + t], bits);
                            _lpIn[32 + t] = RefineLp(r, _lpIn[32 + t], bits);
                        }
                    else if (Is422 && n != 0)
                        for (int k = 1; k < 8; k++)
                        {
                            int t = JxrTables.Transpose422[k];
                            _lpIn[16 + t] = RefineLp(r, _lpIn[16 + t], bits);
                            _lpIn[32 + t] = RefineLp(r, _lpIn[32 + t], bits);
                        }
                    else
                        for (int k = 1; k < 16; k++)
                        {
                            int t = n * 16 + JxrTables.Transpose444[k];
                            _lpIn[t] = RefineLp(r, _lpIn[t], bits);
                        }
                }
            }
            _modelLp.Update(lap0, lap1, 1, Fmt, Nc);
            if (_resetContext)
            {
                _firstLpLum.Adapt2(4); _indLpLum0.Adapt2(3); _indLpLum1.Adapt2(3);
                _firstLpChr.Adapt2(4); _indLpChr0.Adapt2(3); _indLpChr1.Adapt2(3);
                _absLp0.Adapt1(); _absLp1.Adapt1();
            }
        }

        static int RefineLp(JxrBitReader r, int c, int bits)
        {
            int refine = r.Bits(bits);
            if (c > 0) return (c << bits) + refine;
            if (c < 0) return (c << bits) - refine;
            if (refine != 0 && r.Bit()) return -refine;
            return refine;
        }

        // ------------------------------------------------------------------ CBPHP (8.7.17)

        public void ParseMbCbphp(JxrBitReader r)
        {
            if (_tileStart) { _numCbphp.Init1(); _numBlkCbphp.Init1(); }
            var diff = _diffCbphp;
            Array.Clear(diff);
            bool yuv = Fmt is JxrInternalFormat.Yuv420 or JxrInternalFormat.Yuv422 or JxrInternalFormat.Yuv444;
            int comps = Fmt is JxrInternalFormat.Yuvk or JxrInternalFormat.NComponent ? Nc : 1;
            for (int i = 0; i < comps; i++)
            {
                int num = r.Decode(JxrTables.NumCbphp[_numCbphp.Table]);
                _numCbphp.Disc1 += JxrTables.NumCbphpDelta[num];
                int cbphp = RefineCbphp(r, num);
                for (int blk = 0; blk < 4; blk++)
                {
                    if ((cbphp & (1 << blk)) == 0) continue;
                    int nb;
                    if (yuv)
                    {
                        nb = r.Decode(JxrTables.NumBlkCbphpYuv[_numBlkCbphp.Table]);
                        _numBlkCbphp.Disc1 += JxrTables.NumBlkCbphpDeltaYuv[nb];
                    }
                    else
                    {
                        nb = r.Decode(JxrTables.NumCbphp[_numBlkCbphp.Table]);
                        _numBlkCbphp.Disc1 += JxrTables.NumCbphpDelta[nb];
                    }
                    int val = nb + 1, blkCbphp = 0;
                    if (val >= 6)
                    {
                        int chr = r.Decode(JxrTables.ChrCbphp);
                        blkCbphp = 0x10 * (chr + 1);
                        if (val >= 9) val += r.Decode(JxrTables.ChrCbphp);
                        val -= 6;
                    }
                    int code = JxrTables.CbphpOff[val];
                    int flc = JxrTables.CbphpFlc[val];
                    if (flc != 0) code += r.Bits(flc);
                    blkCbphp += JxrTables.CbphpOut[code];
                    switch (Fmt)
                    {
                        case JxrInternalFormat.Yuv444:
                            diff[0] |= (blkCbphp & 0xF) << (blk * 4);
                            for (int k = 0; k < 2; k++)
                                if (((blkCbphp >> (k + 4)) & 1) != 0)
                                {
                                    int nch = r.Decode(JxrTables.NumChBlk);
                                    diff[k + 1] |= RefineCbphp(r, nch + 1) << (blk * 4);
                                }
                            break;
                        case JxrInternalFormat.Yuv422:
                            {
                                diff[0] |= (blkCbphp & 0xF) << (blk * 4);
                                ReadOnlySpan<int> shift = stackalloc int[] { 0, 1, 4, 5 };
                                for (int k = 0; k < 2; k++)
                                    if (((blkCbphp >> (k + 4)) & 1) != 0)
                                    {
                                        int v = r.Decode(JxrTables.ChrCbphp);
                                        diff[k + 1] |= shift[v + 1] << shift[blk];
                                    }
                                break;
                            }
                        case JxrInternalFormat.Yuv420:
                            diff[0] |= (blkCbphp & 0xF) << (blk * 4);
                            diff[1] |= ((blkCbphp >> 4) & 1) << blk;
                            diff[2] |= ((blkCbphp >> 5) & 1) << blk;
                            break;
                        default:
                            diff[i] |= blkCbphp << (blk * 4);
                            break;
                    }
                }
            }
            PredCbphp(diff);
        }

        static int RefineCbphp(JxrBitReader r, int num) => num switch
        {
            2 => JxrTables.RefCbphp1Values[r.Decode(JxrTables.RefCbphp1)],
            1 => 1 << r.Bits(2),
            3 => 0xF ^ (1 << r.Bits(2)),
            4 => 0xF,
            _ => 0,
        };

        void PredCbphp(int[] diff)
        {
            if (_tileStart)
            {
                _cbphpState0 = _cbphpState1 = 0;
                _countOnes0 = _countOnes1 = -4;
                _countZeroes0 = _countZeroes1 = 4;
            }
            int baseIdx = _mbi * Nc;
            int comps = Is42x ? 1 : Nc;
            for (int i = 0; i < comps; i++)
            {
                int c1 = i > 0 ? 1 : 0;
                int cb = diff[i];
                if ((c1 == 0 ? _cbphpState0 : _cbphpState1) == 0)
                {
                    if (_leftEdge)
                    {
                        if (_topEdge) cb ^= 1;
                        else cb ^= (_cbphp[baseIdx - _mbw * Nc + i] >> 10) & 1;
                    }
                    else cb ^= (_cbphp[baseIdx - Nc + i] >> 5) & 1;
                    cb ^= 0x02 & (cb << 1);
                    cb ^= 0x10 & (cb << 3);
                    cb ^= 0x20 & (cb << 1);
                    cb ^= (cb & 0x33) << 2;
                    cb ^= (cb & 0x00CC) << 6;
                    cb ^= (cb & 0x3300) << 2;
                }
                else if ((c1 == 0 ? _cbphpState0 : _cbphpState1) == 2) cb ^= 0xFFFF;
                UpdateCbphpModel(c1, BitOperations.PopCount((uint)cb));
                _cbphp[baseIdx + i] = cb;
            }
            if (Is42x)
                for (int i = 1; i <= 2; i++)
                {
                    int cb = diff[i];
                    if (_cbphpState1 == 0)
                    {
                        if (_leftEdge)
                        {
                            if (_topEdge) cb ^= 1;
                            else cb ^= (_cbphp[baseIdx - _mbw * Nc + i] >> (Is422 ? 6 : 2)) & 1;
                        }
                        else cb ^= (_cbphp[baseIdx - Nc + i] >> 1) & 1;
                        if (Is422)
                        {
                            cb ^= (cb & 0x01) << 1;
                            cb ^= (cb & 0x03) << 2;
                            cb ^= (cb & 0x0C) << 2;
                            cb ^= (cb & 0x30) << 2;
                        }
                        else
                        {
                            cb ^= 0x02 & (cb << 1);
                            cb ^= (cb & 0x3) << 2;
                        }
                    }
                    else if (_cbphpState1 == 2) cb ^= Is422 ? 0xFF : 0x0F;
                    UpdateCbphpModel(1, BitOperations.PopCount((uint)cb) * (Is422 ? 2 : 4));
                    _cbphp[baseIdx + i] = cb;
                }
        }

        void UpdateCbphpModel(int i, int nOrig)
        {
            ref int ones = ref i == 0 ? ref _countOnes0 : ref _countOnes1;
            ref int zeroes = ref i == 0 ? ref _countZeroes0 : ref _countZeroes1;
            ref int state = ref i == 0 ? ref _cbphpState0 : ref _cbphpState1;
            ones = Math.Clamp(ones + nOrig - 3, -16, 15);
            zeroes = Math.Clamp(zeroes + 16 - nOrig - 3, -16, 15);
            if (ones < 0) state = ones < zeroes ? 1 : 2;
            else if (zeroes < 0) state = 2;
            else state = 0;
        }

        // ------------------------------------------------------------------ HP band (8.7.18, 8.7.19)

        /// <summary>Offset of block <paramref name="blk"/> (raster index in the macroblock) of component c in Px.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int BlockOffset(int c, int blk)
        {
            int lb = _mbPxW[c] == 16 ? 2 : 1, bh = _mbPxH[c] >> 2;   // 4 or 2 blocks per row
            return ((_mby * bh + (blk >> lb)) * DcW[c] + (_mbx << lb) + (blk & ((1 << lb) - 1))) << 4;
        }

        /// <summary>
        /// MB_HP( ) / MB_HP_FLEX( ): the VLC coded HP coefficients go to Px; with <paramref name="flex"/> the flexbits follow
        /// each block and are merged (spatial mode); otherwise Px holds the VLC values until <see cref="ParseMbFlexbits"/>.
        /// </summary>
        public void ParseMbHp(JxrBitReader r, bool flex)
        {
            if (_tileStart)
            {
                _firstHpLum.Init2(); _indHpLum0.Init2(); _indHpLum1.Init2();
                _firstHpChr.Init2(); _indHpChr0.Init2(); _indHpChr1.Init2();
                _absHp0.Init1(); _absHp1.Init1();
                for (int i = 1; i <= 15; i++)
                {
                    _hpHor[i] = JxrTables.ScanOrder0[i];
                    _hpVer[i] = JxrTables.ScanOrder1[i];
                    _hpHorTotals[i] = _hpVerTotals[i] = JxrTables.ScanTotals[i];
                }
                _modelHp.Init(2);
            }
            if (_resetTotals)
                for (int i = 1; i <= 15; i++) _hpHorTotals[i] = _hpVerTotals[i] = JxrTables.ScanTotals[i];
            int lap0 = 0, lap1 = 0;
            bool vertical = _hpMode[_mbi] == 1;
            var scan = vertical ? _hpVer : _hpHor;
            var totals = vertical ? _hpVerTotals : _hpHorTotals;
            var rl = _rl;
            int trim = TrimFlexbits;
            for (int c = 0; c < Nc; c++)
            {
                bool chroma = c > 0;
                int nBlocks = chroma && Is420 ? 1 : chroma && Is422 ? 2 : 4;
                int bits = chroma ? _modelHp.Bits1 : _modelHp.Bits0;
                int cb = _cbphp[_mbi * Nc + c];
                var px = Px[c];
                for (int b = 0; b < nBlocks * 4; b++, cb >>= 1)
                {
                    int map = nBlocks == 4 ? JxrTables.HierScanOrder[b] : b;
                    int o = BlockOffset(c, map);
                    int numNz = 0;
                    if ((cb & 1) != 0)
                    {
                        numNz = DecodeBlock(r, chroma, rl, 2, 1);
                        int k = 1;
                        for (int kk = 0; kk < numNz; kk++)
                        {
                            k += rl[kk * 2];
                            if (k > 15) throw new ImageFormatException("JPEG XR: HP run out of range");
                            // AdaptiveHPScan( )
                            px[o + scan[k]] = rl[kk * 2 + 1];
                            totals[k]++;
                            if (k > 1 && totals[k] > totals[k - 1])
                            {
                                (totals[k], totals[k - 1]) = (totals[k - 1], totals[k]);
                                (scan[k], scan[k - 1]) = (scan[k - 1], scan[k]);
                            }
                            k++;
                        }
                    }
                    if (flex) BlockFlexbits(r, px, o, bits, trim);
                    if (chroma) lap1 += numNz; else lap0 += numNz;
                }
            }
            _modelBitsHp[_mbi * 2] = (byte)_modelHp.Bits0;
            _modelBitsHp[_mbi * 2 + 1] = (byte)_modelHp.Bits1;
            _modelHp.Update(lap0, lap1, 2, Fmt, Nc);
            if (_resetContext)
            {
                _firstHpLum.Adapt2(4); _indHpLum0.Adapt2(3); _indHpLum1.Adapt2(3);
                _firstHpChr.Adapt2(4); _indHpChr0.Adapt2(3); _indHpChr1.Adapt2(3);
                _absHp0.Adapt1(); _absHp1.Adapt1();
                _numCbphp.Adapt1(); _numBlkCbphp.Adapt1();
            }
        }

        /// <summary>
        /// BLOCK_FLEXBITS( ) merged with the HP remapping (9.5.4): the 15 HP coefficients of a block become
        /// (VLC part &lt;&lt; model bits) + flexbits. Without flexbits (bits == trim, or NOFLEXBITS) only the shift remains.
        /// </summary>
        static void BlockFlexbits(JxrBitReader r, int[] px, int o, int bits, int trim)
        {
            int left = Math.Max(bits - trim, 0);
            if (left == 0)
            {
                ShiftBlock(px, o, bits);
                return;
            }
            var t = JxrTables.Transpose444;
            for (int n = 1; n < 16; n++)
            {
                ref int v = ref px[o + t[n]];
                int flex = 0;
                if (left != 0)
                {
                    int f = r.Bits(left);
                    if (v > 0) flex = f;
                    else if (v < 0) flex = -f;
                    else
                    {
                        flex = f;
                        if (f != 0 && r.Bit()) flex = -f;
                    }
                    flex <<= trim;
                }
                v = (v << bits) + flex;
            }
        }

        /// <summary>MB_FLEXBITS( ) of the frequency mode flexbits packet (or the shift alone when it is absent).</summary>
        public void ParseMbFlexbits(JxrBitReader? r)
        {
            for (int c = 0; c < Nc; c++)
            {
                bool chroma = c > 0;
                int nBlocks = chroma && Is420 ? 1 : chroma && Is422 ? 2 : 4;
                int bits = _modelBitsHp[_mbi * 2 + (chroma ? 1 : 0)];
                var px = Px[c];
                for (int b = 0; b < nBlocks * 4; b++)
                {
                    int map = nBlocks == 4 ? JxrTables.HierScanOrder[b] : b;
                    int o = BlockOffset(c, map);
                    if (r != null) BlockFlexbits(r, px, o, bits, TrimFlexbits);
                    else ShiftBlock(px, o, bits);
                }
            }
        }

        static void ShiftBlock(int[] px, int o, int bits)
        {
            if (bits == 0) return;
            var b = px.AsSpan(o + 1, 15);
            for (int k = 0; k < 15; k++) b[k] <<= bits;
        }
    }
}
