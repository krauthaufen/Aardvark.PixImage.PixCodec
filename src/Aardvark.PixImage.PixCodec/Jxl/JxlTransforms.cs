using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>
    /// Inverse transforms of VarDCT. The DCT is scaled so that coefficient 0 is the mean:
    /// x[n] = c[0] + sqrt(2) * sum_{k>0} c[k] cos(pi (2n + 1) k / 2N).
    /// </summary>
    internal static unsafe class JxlTransforms
    {
        static readonly float[][] idct = new float[9][];      // [log2 N] -> N x N matrix, M[n * N + k]
        static readonly double[][] llfScale = new double[6][];  // [log2 n] for n = 1..32 blocks

        static JxlTransforms()
        {
            for (int l = 0; l <= 8; l++)
            {
                int n = 1 << l;
                var m = new float[n * n];
                for (int i = 0; i < n; i++)
                    for (int k = 0; k < n; k++)
                        m[i * n + k] = (float)((k == 0 ? 1.0 : Math.Sqrt(2)) * Math.Cos(Math.PI * (2 * i + 1) * k / (2.0 * n)));
                idct[l] = m;
            }
            for (int l = 0; l <= 5; l++)
            {
                int n = 1 << l;
                var s = new double[n];
                for (int k = 0; k < n; k++)
                    s[k] = k == 0 ? 1.0 : 8 * Math.Sin(Math.PI * k / (16.0 * n)) / Math.Sin(Math.PI * k / (2.0 * n));
                llfScale[l] = s;
            }
        }

        static int Log2(int n) => BitOperations.Log2((uint)n);

        public static float[] IdctMatrix(int n) => idct[Log2(n)];

        /// <summary>
        /// 2D inverse DCT of a rows x cols coefficient matrix (row major, rows = vertical frequencies) into
        /// out[y * stride + x]. tmp needs 2 * rows * cols floats.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Idct2D(float* coef, int rows, int cols, float* outp, int stride, float* tmp)
        {
            if (JxlFastIdct.Idct2D(coef, rows, cols, outp, stride, tmp)) return;
            var mr = idct[Log2(rows)];
            var mc = idct[Log2(cols)];
            int vc = Vector<float>.Count;
            // vertical: tmp[y][kx] = sum_ky M_rows[y][ky] * coef[ky][kx]
            fixed (float* pmr = mr, pmc = mc)
            {
                if (cols % vc == 0)
                {
                    for (int y = 0; y < rows; y++)
                    {
                        float* trow = tmp + y * cols;
                        for (int x = 0; x < cols; x += vc)
                        {
                            var acc = Vector<float>.Zero;
                            for (int k = 0; k < rows; k++)
                                acc += new Vector<float>(pmr[y * rows + k]) * Unsafe.ReadUnaligned<Vector<float>>(coef + k * cols + x);
                            Unsafe.WriteUnaligned(trow + x, acc);
                        }
                    }
                }
                else
                {
                    for (int y = 0; y < rows; y++)
                        for (int x = 0; x < cols; x++)
                        {
                            float acc = 0;
                            for (int k = 0; k < rows; k++) acc += pmr[y * rows + k] * coef[k * cols + x];
                            tmp[y * cols + x] = acc;
                        }
                }
                // horizontal, on the transpose: v[x][y] = sum_kx M_cols[x][kx] * tmp[y][kx], vectorized over y
                float* u = tmp + rows * cols;
                for (int y = 0; y < rows; y++)
                    for (int x = 0; x < cols; x++) u[x * rows + y] = tmp[y * cols + x];
                if (rows % vc == 0)
                {
                    float* v = tmp;   // reuse
                    for (int x = 0; x < cols; x++)
                    {
                        float* mrow = pmc + x * cols;
                        for (int y = 0; y < rows; y += vc)
                        {
                            var acc = Vector<float>.Zero;
                            for (int k = 0; k < cols; k++)
                                acc += new Vector<float>(mrow[k]) * Unsafe.ReadUnaligned<Vector<float>>(u + k * rows + y);
                            Unsafe.WriteUnaligned(v + x * rows + y, acc);
                        }
                    }
                    for (int y = 0; y < rows; y++)
                    {
                        float* orow = outp + y * stride;
                        for (int x = 0; x < cols; x++) orow[x] = v[x * rows + y];
                    }
                }
                else
                {
                    for (int y = 0; y < rows; y++)
                    {
                        float* orow = outp + y * stride;
                        for (int x = 0; x < cols; x++)
                        {
                            float* mrow = pmc + x * cols;
                            float s = 0;
                            for (int k = 0; k < cols; k++) s += mrow[k] * u[k * rows + y];
                            orow[x] = s;
                        }
                    }
                }
            }
        }

        /// <summary>The LLF coefficients (cy x cx) of a varblock from its DC values dc[by * dcStride + bx].</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void LlfFromDc(float* dc, int dcStride, int cx, int cy, float* llf /* cy x cx row major */)
        {
            // forward scaled DCT: F[k] = (1/n) sum x[i] M[i][k]
            var mx = idct[Log2(cx)];
            var my = idct[Log2(cy)];
            var sx = llfScale[Log2(cx)];
            var sy = llfScale[Log2(cy)];
            Span<double> t = stackalloc double[cx * cy];
            for (int by = 0; by < cy; by++)
                for (int kx = 0; kx < cx; kx++)
                {
                    double a = 0;
                    for (int bx = 0; bx < cx; bx++) a += dc[by * dcStride + bx] * (double)mx[bx * cx + kx];
                    t[by * cx + kx] = a / cx;
                }
            for (int ky = 0; ky < cy; ky++)
                for (int kx = 0; kx < cx; kx++)
                {
                    double a = 0;
                    for (int by = 0; by < cy; by++) a += t[by * cx + kx] * my[by * cy + ky];
                    llf[ky * cx + kx] = (float)(a / cy * sy[ky] * sx[kx]);
                }
        }

        // ------------------------------------------------------------------ special 8x8 transforms

        /// <summary>The 2x2 step of the hierarchical DCT2: the top-left s x s of block (stride 8) into out (stride 8).</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void Idct2Top(float* block, int s, float* outp)
        {
            float* temp = stackalloc float[64];
            int h = s / 2;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < h; x++)
                {
                    float c00 = block[y * 8 + x], c01 = block[y * 8 + h + x], c10 = block[(y + h) * 8 + x], c11 = block[(y + h) * 8 + h + x];
                    temp[y * 2 * 8 + x * 2] = c00 + c01 + c10 + c11;
                    temp[y * 2 * 8 + x * 2 + 1] = c00 + c01 - c10 - c11;
                    temp[(y * 2 + 1) * 8 + x * 2] = c00 - c01 + c10 - c11;
                    temp[(y * 2 + 1) * 8 + x * 2 + 1] = c00 - c01 - c10 + c11;
                }
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++) outp[y * 8 + x] = temp[y * 8 + x];
        }

        static readonly float[] AfvBasis =
        {
            0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f,
            0.876902929799142f, 0.2206518106944235f, -0.10140050393753763f, -0.1014005039375375f, 0.2206518106944236f, -0.10140050393753777f, -0.10140050393753772f, -0.10140050393753763f, -0.10140050393753758f, -0.10140050393753769f, -0.1014005039375375f, -0.10140050393753768f, -0.10140050393753768f, -0.10140050393753759f, -0.10140050393753763f, -0.10140050393753741f,
            0.0f, 0.0f, 0.40670075830260755f, 0.44444816619734445f, 0.0f, 0.0f, 0.19574399372042936f, 0.2929100136981264f, -0.40670075830260716f, -0.19574399372042872f, 0.0f, 0.11379074460448091f, -0.44444816619734384f, -0.29291001369812636f, -0.1137907446044814f, 0.0f,
            0.0f, 0.0f, -0.21255748058288748f, 0.3085497062849767f, 0.0f, 0.4706702258572536f, -0.1621205195722993f, 0.0f, -0.21255748058287047f, -0.16212051957228327f, -0.47067022585725277f, -0.1464291867126764f, 0.3085497062849487f, 0.0f, -0.14642918671266536f, 0.4251149611657548f,
            0.0f, -0.7071067811865474f, 0.0f, 0.0f, 0.7071067811865476f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f,
            -0.4105377591765233f, 0.6235485373547691f, -0.06435071657946274f, -0.06435071657946266f, 0.6235485373547694f, -0.06435071657946284f, -0.0643507165794628f, -0.06435071657946274f, -0.06435071657946272f, -0.06435071657946279f, -0.06435071657946266f, -0.06435071657946277f, -0.06435071657946277f, -0.06435071657946273f, -0.06435071657946274f, -0.0643507165794626f,
            0.0f, 0.0f, -0.4517556589999482f, 0.15854503551840063f, 0.0f, -0.04038515160822202f, 0.0074182263792423875f, 0.39351034269210167f, -0.45175565899994635f, 0.007418226379244351f, 0.1107416575309343f, 0.08298163094882051f, 0.15854503551839705f, 0.3935103426921022f, 0.0829816309488214f, -0.45175565899994796f,
            0.0f, 0.0f, -0.304684750724869f, 0.5112616136591823f, 0.0f, 0.0f, -0.290480129728998f, -0.06578701549142804f, 0.304684750724884f, 0.2904801297290076f, 0.0f, -0.23889773523344604f, -0.5112616136592012f, 0.06578701549142545f, 0.23889773523345467f, 0.0f,
            0.0f, 0.0f, 0.3017929516615495f, 0.25792362796341184f, 0.0f, 0.16272340142866204f, 0.09520022653475037f, 0.0f, 0.3017929516615503f, 0.09520022653475055f, -0.16272340142866173f, -0.35312385449816297f, 0.25792362796341295f, 0.0f, -0.3531238544981624f, -0.6035859033230976f,
            0.0f, 0.0f, 0.40824829046386274f, 0.0f, 0.0f, 0.0f, 0.0f, -0.4082482904638628f, -0.4082482904638635f, 0.0f, 0.0f, -0.40824829046386296f, 0.0f, 0.4082482904638634f, 0.408248290463863f, 0.0f,
            0.0f, 0.0f, 0.1747866975480809f, 0.0812611176717539f, 0.0f, 0.0f, -0.3675398009862027f, -0.307882213957909f, -0.17478669754808135f, 0.3675398009862011f, 0.0f, 0.4826689115059883f, -0.08126111767175039f, 0.30788221395790305f, -0.48266891150598584f, 0.0f,
            0.0f, 0.0f, -0.21105601049335784f, 0.18567180916109802f, 0.0f, 0.0f, 0.49215859013738733f, -0.38525013709251915f, 0.21105601049335806f, -0.49215859013738905f, 0.0f, 0.17419412659916217f, -0.18567180916109904f, 0.3852501370925211f, -0.1741941265991621f, 0.0f,
            0.0f, 0.0f, -0.14266084808807264f, -0.3416446842253372f, 0.0f, 0.7367497537172237f, 0.24627107722075148f, -0.08574019035519306f, -0.14266084808807344f, 0.24627107722075137f, 0.14883399227113567f, -0.04768680350229251f, -0.3416446842253373f, -0.08574019035519267f, -0.047686803502292804f, -0.14266084808807242f,
            0.0f, 0.0f, -0.13813540350758585f, 0.3302282550303788f, 0.0f, 0.08755115000587084f, -0.07946706605909573f, -0.4613374887461511f, -0.13813540350758294f, -0.07946706605910261f, 0.49724647109535086f, 0.12538059448563663f, 0.3302282550303805f, -0.4613374887461554f, 0.12538059448564315f, -0.13813540350758452f,
            0.0f, 0.0f, -0.17437602599651067f, 0.0702790691196284f, 0.0f, -0.2921026642334881f, 0.3623817333531167f, 0.0f, -0.1743760259965108f, 0.36238173335311646f, 0.29210266423348785f, -0.4326608024727445f, 0.07027906911962818f, 0.0f, -0.4326608024727457f, 0.34875205199302267f,
            0.0f, 0.0f, 0.11354987314994337f, -0.07417504595810355f, 0.0f, 0.19402893032594343f, -0.435190496523228f, 0.21918684838857466f, 0.11354987314994257f, -0.4351904965232251f, 0.5550443808910661f, -0.25468277124066463f, -0.07417504595810233f, 0.2191868483885728f, -0.25468277124066413f, 0.1135498731499429f,
        };

        /// <summary>
        /// Inverse transform of one varblock. coef: the coefficients in the wide layout (rows = min, cols = max of the block
        /// dimensions); out: the pixels, out[y * stride + x], height 8 * CoveredY, width 8 * CoveredX. scratch: 3 * size floats.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Inverse(int strategy, float* coef, float* outp, int stride, float* scratch)
        {
            int cx = JxlAcStrategy.CoveredX[strategy], cy = JxlAcStrategy.CoveredY[strategy];
            switch (strategy)
            {
                case JxlAcStrategy.Identity: Identity(coef, outp, stride); return;
                case JxlAcStrategy.Dct2x2: Dct2x2(coef, outp, stride); return;
                case JxlAcStrategy.Dct4x4: Dct4x4(coef, outp, stride, scratch); return;
                case JxlAcStrategy.Dct4x8: Dct4x8(coef, outp, stride, scratch, false); return;
                case JxlAcStrategy.Dct8x4: Dct4x8(coef, outp, stride, scratch, true); return;
                case JxlAcStrategy.Afv0: case JxlAcStrategy.Afv1: case JxlAcStrategy.Afv2: case JxlAcStrategy.Afv3:
                    Afv(coef, outp, stride, strategy - JxlAcStrategy.Afv0, scratch); return;
            }
            IdctStored(coef, cy * 8, cx * 8, outp, stride, scratch);
        }

        /// <summary>
        /// Inverse DCT of an H x W block from its stored coefficients: wide blocks (W &gt; H) store the vertical frequency
        /// in rows (c[ky * W + kx]), square and tall blocks the horizontal one (c[kx * H + ky]). scratch: 3 H W floats.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void IdctStored(float* coef, int H, int W, float* outp, int stride, float* scratch)
        {
            if (W > H) { Idct2D(coef, H, W, outp, stride, scratch); return; }
            // the stored matrix is the transpose: transform it as it is and write the result transposed
            if (JxlFastIdct.Idct2D(coef, W, H, outp, stride, scratch, transposeOut: true)) return;
            float* t = scratch + 2 * H * W;
            for (int ky = 0; ky < H; ky++)
                for (int kx = 0; kx < W; kx++) t[ky * W + kx] = coef[kx * H + ky];
            Idct2D(t, H, W, outp, stride, scratch);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void Identity(float* coef, float* outp, int stride)
        {
            float* dcs = stackalloc float[64];
            Idct2Top(coef, 2, dcs);
            for (int y = 0; y < 2; y++)
                for (int x = 0; x < 2; x++)
                {
                    float dc = dcs[y * 8 + x];
                    float sum = 0;
                    for (int iy = 0; iy < 4; iy++)
                        for (int ix = 0; ix < 4; ix++)
                        {
                            if (ix == 0 && iy == 0) continue;
                            sum += coef[(y + iy * 2) * 8 + x + ix * 2];
                        }
                    // the sample at (1, 1) of each 4x4 is the reference; the coefficient at (1, 1) gives the sample at (0, 0)
                    float p11 = dc - sum * (1.0f / 16);
                    outp[(4 * y + 1) * stride + 4 * x + 1] = p11;
                    for (int iy = 0; iy < 4; iy++)
                        for (int ix = 0; ix < 4; ix++)
                        {
                            if (ix == 1 && iy == 1) continue;
                            outp[(4 * y + iy) * stride + 4 * x + ix] = coef[(y + iy * 2) * 8 + x + ix * 2] + p11;
                        }
                    outp[(4 * y) * stride + 4 * x] = coef[(y + 2) * 8 + x + 2] + p11;
                }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void Dct2x2(float* coef, float* outp, int stride)
        {
            float* b = stackalloc float[64];
            for (int i = 0; i < 64; i++) b[i] = coef[i];
            Idct2Top(b, 2, b);
            Idct2Top(b, 4, b);
            Idct2Top(b, 8, b);
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++) outp[y * stride + x] = b[y * 8 + x];
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void Dct4x4(float* coef, float* outp, int stride, float* scratch)
        {
            float* dcs = stackalloc float[64];
            float* block = stackalloc float[16];
            Idct2Top(coef, 2, dcs);
            for (int y = 0; y < 2; y++)
                for (int x = 0; x < 2; x++)
                {
                    block[0] = dcs[y * 8 + x];
                    for (int iy = 0; iy < 4; iy++)
                        for (int ix = 0; ix < 4; ix++)
                        {
                            if (ix == 0 && iy == 0) continue;
                            block[iy * 4 + ix] = coef[(iy * 2 + y) * 8 + ix * 2 + x];
                        }
                    IdctStored(block, 4, 4, outp + 4 * y * stride + 4 * x, stride, scratch);
                }
        }

        /// <summary>DCT4X8: two 4 rows x 8 columns halves stacked; DCT8X4 (transposed): two 8 x 4 halves side by side.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void Dct4x8(float* coef, float* outp, int stride, float* scratch, bool transposed)
        {
            float* block = stackalloc float[32];
            float d0 = coef[0] + coef[8], d1 = coef[0] - coef[8];
            for (int i = 0; i < 2; i++)
            {
                block[0] = i == 0 ? d0 : d1;
                for (int iy = 0; iy < 4; iy++)
                    for (int ix = 0; ix < 8; ix++)
                    {
                        if (ix == 0 && iy == 0) continue;
                        block[iy * 8 + ix] = coef[(iy * 2 + i) * 8 + ix];
                    }
                if (!transposed) IdctStored(block, 4, 8, outp + 4 * i * stride, stride, scratch);
                else IdctStored(block, 8, 4, outp + 4 * i, stride, scratch);   // tall: stored transposed
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void Afv(float* coef, float* outp, int stride, int kind, float* scratch)
        {
            int afvX = kind & 1, afvY = kind / 2;
            float b00 = coef[0], b01 = coef[1], b10 = coef[8];
            float dc0 = (b00 + b10 + b01) * 4.0f, dc1 = b00 + b10 - b01, dc2 = b00 - b10;
            float* c = stackalloc float[16];
            float* pix = stackalloc float[16];
            c[0] = dc0;
            for (int iy = 0; iy < 4; iy++)
                for (int ix = 0; ix < 4; ix++)
                {
                    if (ix == 0 && iy == 0) continue;
                    c[iy * 4 + ix] = coef[iy * 2 * 8 + ix * 2];
                }
            fixed (float* basis = AfvBasis)
            {
                for (int i = 0; i < 16; i++)
                {
                    float s = 0;
                    for (int j = 0; j < 16; j++) s += c[j] * basis[j * 16 + i];
                    pix[i] = s;
                }
            }
            for (int iy = 0; iy < 4; iy++)
                for (int ix = 0; ix < 4; ix++)
                {
                    int p = (afvY == 1 ? 3 - iy : iy) * 4 + (afvX == 1 ? 3 - ix : ix);
                    outp[(iy + afvY * 4) * stride + afvX * 4 + ix] = pix[p];
                }
            // 4x4 DCT next to the AFV corner
            float* block = stackalloc float[32];
            block[0] = dc1;
            for (int iy = 0; iy < 4; iy++)
                for (int ix = 0; ix < 4; ix++)
                {
                    if (ix == 0 && iy == 0) continue;
                    block[iy * 4 + ix] = coef[iy * 2 * 8 + ix * 2 + 1];
                }
            IdctStored(block, 4, 4, outp + afvY * 4 * stride + (afvX == 1 ? 0 : 4), stride, scratch);
            // 4x8 DCT in the other half
            block[0] = dc2;
            for (int iy = 0; iy < 4; iy++)
                for (int ix = 0; ix < 8; ix++)
                {
                    if (ix == 0 && iy == 0) continue;
                    block[iy * 8 + ix] = coef[(iy * 2 + 1) * 8 + ix];
                }
            IdctStored(block, 4, 8, outp + (afvY == 1 ? 0 : 4) * stride, stride, scratch);
        }
    }
}
