using System;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>One quantization table encoding (QuantEncoding) with its parameters.</summary>
    internal sealed class JxlQuantEncoding
    {
        public const int Library = 0, Id = 1, Dct2 = 2, Dct4 = 3, Dct4x8 = 4, Afv = 5, Dct = 6, Raw = 7;
        public int Mode;
        /// <summary>Distance bands per channel (DCT, DCT4, DCT4X8, AFV: the 4x8 part).</summary>
        public double[][]? Bands;
        /// <summary>The 4x4 bands of AFV.</summary>
        public double[][]? Bands4x4;
        /// <summary>ID: 3 per channel; DCT2: 6; DCT4: 2 multipliers; DCT4X8: 1 multiplier; AFV: 9.</summary>
        public double[][]? Params;
        public float RawDen;
        public int[][]? RawTable;

        static double[][] B(params double[][] a) => a;

        // ------------------------------------------------------------------ defaults (ISO/IEC 18181-1 default quantization tables)

        static readonly double[] SeqA = { -1.025, -0.78, -0.65012, -0.19041574084286472, -0.20819395464, -0.421064, -0.32733845535848671 };
        static readonly double[] SeqB = { -0.3041958212306401, -0.3633036457487539, -0.35660379990111464, -0.3443074455424403, -0.33699592683512467, -0.30180866526242109, -0.27321683125358037 };
        static readonly double[] SeqC = { -1.2, -1.2, -0.8, -0.7, -0.7, -0.4, -0.5 };

        static double[] Cat(double first, double[] rest)
        {
            var r = new double[rest.Length + 1];
            r[0] = first;
            Array.Copy(rest, 0, r, 1, rest.Length);
            return r;
        }

        static readonly double[][] Dct4x4Bands = B(new[] { 2200.0, 0.0, 0.0, 0.0 }, new[] { 392.0, 0.0, 0.0, 0.0 }, new[] { 112.0, -0.25, -0.25, -0.5 });
        static readonly double[][] Dct4x8Bands = B(
            new[] { 2198.050556016380522, -0.96269623020744692, -0.76194253026666783, -0.6551140670773547 },
            new[] { 764.3655248643528689, -0.92630200888366945, -0.9675229603596517, -0.27845290869168118 },
            new[] { 527.107573587542228, -1.4594385811273854, -1.450082094097871593, -1.5843722511996204 });

        public static JxlQuantEncoding Default(int table)
        {
            switch (table)
            {
                case 0:
                    return new JxlQuantEncoding
                    {
                        Mode = Dct,
                        Bands = B(new[] { 3150.0, 0.0, -0.4, -0.4, -0.4, -2.0 }, new[] { 560.0, 0.0, -0.3, -0.3, -0.3, -0.3 }, new[] { 512.0, -2.0, -1.0, 0.0, -1.0, -2.0 }),
                    };
                case 1:
                    return new JxlQuantEncoding { Mode = Id, Params = B(new[] { 280.0, 3160.0, 3160.0 }, new[] { 60.0, 864.0, 864.0 }, new[] { 18.0, 200.0, 200.0 }) };
                case 2:
                    return new JxlQuantEncoding
                    {
                        Mode = Dct2,
                        Params = B(new[] { 3840.0, 2560.0, 1280.0, 640.0, 480.0, 300.0 }, new[] { 960.0, 640.0, 320.0, 180.0, 140.0, 120.0 }, new[] { 640.0, 320.0, 128.0, 64.0, 32.0, 16.0 }),
                    };
                case 3:
                    return new JxlQuantEncoding { Mode = Dct4, Params = B(new[] { 1.0, 1.0 }, new[] { 1.0, 1.0 }, new[] { 1.0, 1.0 }), Bands = Dct4x4Bands };
                case 4:
                    return new JxlQuantEncoding
                    {
                        Mode = Dct,
                        Bands = B(
                            new[] { 8996.8725711814115328, -1.3000777393353804, -0.49424529824571225, -0.439093774457103443, -0.6350101832695744, -0.90177264050827612, -1.6162099239887414 },
                            new[] { 3191.48366296844234752, -0.67424582104194355, -0.80745813428471001, -0.44925837484843441, -0.35865440981033403, -0.31322389111877305, -0.37615025315725483 },
                            new[] { 1157.50408145487200256, -2.0531423165804414, -1.4, -0.50687130033378396, -0.42708730624733904, -1.4856834539296244, -4.9209142884401604 }),
                    };
                case 5:
                    return new JxlQuantEncoding
                    {
                        Mode = Dct,
                        Bands = B(
                            new[] { 15718.40830982518931456, -1.025, -0.98, -0.9012, -0.4, -0.48819395464, -0.421064, -0.27 },
                            new[] { 7305.7636810695983104, -0.8041958212306401, -0.7633036457487539, -0.55660379990111464, -0.49785304658857626, -0.43699592683512467, -0.40180866526242109, -0.27321683125358037 },
                            new[] { 3803.53173721215041536, -3.060733579805728, -2.0413270132490346, -2.0235650159727417, -0.5495389509954993, -0.4, -0.4, -0.3 }),
                    };
                case 6:
                    return new JxlQuantEncoding
                    {
                        Mode = Dct,
                        Bands = B(
                            new[] { 7240.7734393502, -0.7, -0.7, -0.2, -0.2, -0.2, -0.5 },
                            new[] { 1448.15468787004, -0.5, -0.5, -0.5, -0.2, -0.2, -0.2 },
                            new[] { 506.854140754517, -1.4, -0.2, -0.5, -0.5, -1.5, -3.6 }),
                    };
                case 7:
                    return new JxlQuantEncoding
                    {
                        Mode = Dct,
                        Bands = B(
                            new[] { 16283.2494710648897, -1.7812845336559429, -1.6309059012653515, -1.0382179034313539, -0.85, -0.7, -0.9, -1.2360638576849587 },
                            new[] { 5089.15750884921511936, -0.320049391452786891, -0.35362849922161446, -0.30340000000000003, -0.61, -0.5, -0.5, -0.6 },
                            new[] { 3397.77603275308720128, -0.321327362693153371, -0.34507619223117997, -0.70340000000000003, -0.9, -1.0, -1.0, -1.1754605576265209 }),
                    };
                case 8:
                    return new JxlQuantEncoding
                    {
                        Mode = Dct,
                        Bands = B(
                            new[] { 13844.97076442300573, -0.97113799999999995, -0.658, -0.42026, -0.22712, -0.2206, -0.226, -0.6 },
                            new[] { 4798.964084220744293, -0.61125308982767057, -0.83770786552491361, -0.79014862079498627, -0.2692727459704829, -0.38272769465388551, -0.22924222653091453, -0.20719098826199578 },
                            new[] { 1807.236946760964614, -1.2, -1.2, -0.7, -0.7, -0.7, -0.4, -0.5 }),
                    };
                case 9:
                    return new JxlQuantEncoding { Mode = Dct4x8, Params = B(new[] { 1.0 }, new[] { 1.0 }, new[] { 1.0 }), Bands = Dct4x8Bands };
                case 10:
                    return new JxlQuantEncoding
                    {
                        Mode = Afv,
                        Params = B(new[] { 3072.0, 3072.0, 256.0, 256.0, 256.0, 414.0, 0.0, 0.0, 0.0 },
                                   new[] { 1024.0, 1024.0, 50.0, 50.0, 50.0, 58.0, 0.0, 0.0, 0.0 },
                                   new[] { 384.0, 384.0, 12.0, 12.0, 12.0, 22.0, -0.25, -0.25, -0.25 }),
                        Bands = Dct4x8Bands, Bands4x4 = Dct4x4Bands,
                    };
                case 11: return Big(23966.1665298448605, 8380.19148390090414, 4493.02378009847706);
                case 12: return Big(15358.89804933239925, 5597.360516150652990, 2919.961618960011210);
                case 13: return Big(47932.3330596897210, 16760.38296780180828, 8986.04756019695412);
                case 14: return Big(30717.796098664792, 11194.72103230130598, 5839.92323792002242);
                case 15: return Big(95864.6661193794420, 33520.76593560361656, 17972.09512039390824);
                default: return Big(61435.5921973295970, 24209.44206460261196, 12979.84647584004484);
            }
        }

        static JxlQuantEncoding Big(double x, double y, double b)
            => new JxlQuantEncoding { Mode = Dct, Bands = B(Cat(x, SeqA), Cat(y, SeqB), Cat(b, SeqC)) };

        // ------------------------------------------------------------------ reading

        static double[][] ReadDctParams(JxlBitReader br)
        {
            int n = (int)br.ReadBits(4) + 1;
            var p = new double[3][];
            for (int c = 0; c < 3; c++)
            {
                p[c] = new double[n];
                for (int i = 0; i < n; i++) p[c][i] = br.ReadF16();
                p[c][0] *= 64;
            }
            return p;
        }

        static double[][] ReadParams(JxlBitReader br, int n, bool times64)
        {
            var p = new double[3][];
            for (int c = 0; c < 3; c++)
            {
                p[c] = new double[n];
                for (int i = 0; i < n; i++) p[c][i] = br.ReadF16() * (times64 ? 64 : 1);
            }
            return p;
        }

        /// <summary>Reads the encoding of quantization table <paramref name="table"/>. RAW tables need the modular decoder.</summary>
        /// <summary>The Hornuss, DCT2, DCT4, DCT4x8 and AFV encodings describe any of the 8 x 8 tables (18181-1:2024 Table I.5).</summary>
        static void Check8x8(int table)
        {
            if (table is not (0 or 1 or 2 or 3 or 9 or 10)) throw new ImageFormatException("JPEG XL: 8x8 quantization encoding for a larger table");
        }

        public static JxlQuantEncoding Read(JxlBitReader br, int table, Func<int, int, int, int[][]> readRaw)
        {
            int mode = (int)br.ReadBits(3);
            var q = new JxlQuantEncoding { Mode = mode };
            switch (mode)
            {
                case Library: return Default(table);
                case Id:
                    Check8x8(table);
                    q.Params = ReadParams(br, 3, true); break;
                case Dct2:
                    Check8x8(table);
                    q.Params = ReadParams(br, 6, true); break;
                case Dct4:
                    Check8x8(table);
                    // I.2.4 says to multiply these by 64 as well; libjxl does not (an erratum: the default params of
                    // Table I.6 are {1, 1}, unscaled). JxlSpecTests.EightByEightEncodingsOnOtherTables(0, 3) settles it.
                    q.Params = ReadParams(br, 2, false); q.Bands = ReadDctParams(br); break;
                case Dct4x8:
                    Check8x8(table);
                    q.Params = ReadParams(br, 1, false); q.Bands = ReadDctParams(br); break;
                case Afv:
                    Check8x8(table);
                    q.Params = ReadParams(br, 9, false);
                    for (int c = 0; c < 3; c++) for (int i = 0; i < 6; i++) q.Params[c][i] *= 64;
                    q.Bands = ReadDctParams(br); q.Bands4x4 = ReadDctParams(br);
                    break;
                case Dct: q.Bands = ReadDctParams(br); break;
                default:
                    {
                        q.RawDen = br.ReadF16();
                        if (!(q.RawDen > 0)) throw new ImageFormatException("JPEG XL: bad raw quantization table");
                        q.RawTable = readRaw(table, JxlAcStrategy.TableCols[table], JxlAcStrategy.TableRows[table]);
                        break;
                    }
            }
            return q;
        }

        // ------------------------------------------------------------------ table computation

        static double Mult(double v) => v > 0 ? 1 + v : 1 / (1 - v);

        static double Interpolate(double pos, double max, double[] array, int len)
        {
            double scaled = pos * (len - 1) / max;
            int idx = (int)scaled;
            if (idx >= len - 1) return array[len - 1];
            double frac = scaled - idx;
            double a = array[idx], b = array[idx + 1];
            return a * Math.Pow(b / a, frac);
        }

        static double[] BandValues(double[] p)
        {
            var bands = new double[p.Length];
            bands[0] = p[0];
            for (int i = 1; i < p.Length; i++) bands[i] = bands[i - 1] * Mult(p[i]);
            for (int i = 0; i < bands.Length; i++) if (!(bands[i] > 0)) throw new ImageFormatException("JPEG XL: bad quantization bands");
            return bands;
        }

        /// <summary>Weights of a rows x cols DCT from distance bands.</summary>
        static double[] Weights(int rows, int cols, double[] p)
        {
            var bands = BandValues(p);
            var w = new double[rows * cols];
            for (int y = 0; y < rows; y++)
            {
                double dy = rows > 1 ? y / (double)(rows - 1) : 0;
                for (int x = 0; x < cols; x++)
                {
                    double dx = cols > 1 ? x / (double)(cols - 1) : 0;
                    double distance = Math.Sqrt(dx * dx + dy * dy);
                    w[y * cols + x] = Interpolate(distance, Math.Sqrt(2) + 1e-6, bands, bands.Length);
                }
            }
            return w;
        }

        /// <summary>
        /// The dequantization matrix (multipliers, the inverse of the weights) of channel c, rows x cols in the wide layout.
        /// </summary>
        public float[] Matrix(int table, int c)
        {
            int rows = JxlAcStrategy.TableRows[table], cols = JxlAcStrategy.TableCols[table];
            double[] w;
            switch (Mode)
            {
                case Dct: w = Weights(rows, cols, Bands![c]); break;
                case Id:
                    {
                        var p = Params![c];
                        w = new double[64];
                        for (int i = 0; i < 64; i++) w[i] = p[0];
                        w[0] = 1;
                        w[1] = p[1]; w[8] = p[1]; w[9] = p[2];
                        break;
                    }
                case Dct2:
                    {
                        var p = Params![c];
                        w = new double[64];
                        w[0] = 1;
                        w[1] = w[8] = p[0];
                        w[9] = p[1];
                        for (int y = 0; y < 2; y++)
                            for (int x = 0; x < 2; x++) { w[y * 8 + x + 2] = p[2]; w[(y + 2) * 8 + x] = p[2]; }
                        for (int y = 0; y < 2; y++)
                            for (int x = 0; x < 2; x++) w[(y + 2) * 8 + x + 2] = p[3];
                        for (int y = 0; y < 4; y++)
                            for (int x = 0; x < 4; x++) { w[y * 8 + x + 4] = p[4]; w[(y + 4) * 8 + x] = p[4]; }
                        for (int y = 0; y < 4; y++)
                            for (int x = 0; x < 4; x++) w[(y + 4) * 8 + x + 4] = p[5];
                        break;
                    }
                case Dct4:
                    {
                        var w4 = Weights(4, 4, Bands![c]);
                        var m = Params![c];
                        w = new double[64];
                        for (int y = 0; y < 8; y++)
                            for (int x = 0; x < 8; x++) w[y * 8 + x] = w4[(y / 2) * 4 + x / 2];
                        w[1] /= m[0]; w[8] /= m[0]; w[9] /= m[1];
                        break;
                    }
                case Dct4x8:
                    {
                        var w48 = Weights(4, 8, Bands![c]);
                        var m = Params![c];
                        w = new double[64];
                        for (int y = 0; y < 8; y++)
                            for (int x = 0; x < 8; x++) w[y * 8 + x] = w48[(y / 2) * 8 + x];
                        w[8] /= m[0];
                        break;
                    }
                case Afv:
                    {
                        var p = Params![c];
                        var w48 = Weights(4, 8, Bands![c]);
                        var w44 = Weights(4, 4, Bands4x4![c]);
                        const double lo = 0.8517778890324296, hi = 12.97166202570235 - lo + 1e-6;
                        double[] freqs =
                        {
                            0, 0, 0.8517778890324296, 5.37778436506804, 0, 0, 4.734747904497923, 5.449245381693219,
                            1.6598270267479331, 4, 7.275749096817861, 10.423227632456525, 2.662932286148962, 7.630657783650829,
                            8.962388608184032, 12.97166202570235,
                        };
                        var bands = BandValues(new[] { p[5], p[6], p[7], p[8] });
                        w = new double[64];
                        w[0] = 1;
                        w[1] = p[0]; w[8] = p[1];
                        w[2] = p[2]; w[16] = p[3]; w[18] = p[4];
                        for (int y = 0; y < 4; y++)
                            for (int x = 0; x < 4; x++)
                            {
                                if (x < 2 && y < 2) continue;
                                w[2 * x + y * 16] = Interpolate(freqs[y * 4 + x] - lo, hi, bands, 4);
                            }
                        for (int y = 0; y < 4; y++)
                            for (int x = 0; x < 8; x++)
                            {
                                if (x == 0 && y == 0) continue;
                                w[(1 + 2 * y) * 8 + x] = w48[y * 8 + x];
                            }
                        for (int y = 0; y < 4; y++)
                            for (int x = 0; x < 4; x++)
                            {
                                if (x == 0 && y == 0) continue;
                                w[(2 * y) * 8 + 2 * x + 1] = w44[y * 4 + x];
                            }
                        break;
                    }
                default:
                    {
                        var t = RawTable![c];
                        var m = new float[rows * cols];
                        for (int i = 0; i < m.Length; i++)
                        {
                            double v = (double)RawDen * t[i];
                            if (!(v > 0)) throw new ImageFormatException("JPEG XL: raw quantization value not positive");
                            m[i] = (float)v;
                        }
                        return m;
                    }
            }
            var r = new float[w.Length];
            for (int i = 0; i < w.Length; i++)
            {
                if (!(w[i] > 0) || double.IsInfinity(w[i])) throw new ImageFormatException("JPEG XL: bad quantization weight");
                r[i] = (float)(1.0 / w[i]);
            }
            return r;
        }
    }
}
