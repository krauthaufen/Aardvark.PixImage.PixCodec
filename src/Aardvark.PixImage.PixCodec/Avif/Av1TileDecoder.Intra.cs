using System;
using T = Aardvark.ImageCodecs.Av1.Av1Tables;

namespace Aardvark.ImageCodecs.Av1
{
    internal sealed partial class Av1TileDecoder
    {
        const int EdgeOff = 16;
        int[] aboveRow = null!, leftCol = null!, edgeBuf = null!;
        int[] pred = null!;
        int[] cflL = null!;

        void InitIntraScratch()
        {
            aboveRow = new int[EdgeOff + 288];
            leftCol = new int[EdgeOff + 288];
            edgeBuf = new int[300];
            pred = new int[64 * 64];
            cflL = new int[32 * 32];
        }

        // ------------------------------------------------------------------ intra prediction (7.11.2)

        void PredictIntra(int plane, int x, int y, bool haveLeft, bool haveAbove, bool haveAboveRight, bool haveBelowLeft,
            int mode, int log2W, int log2H)
        {
            int w = 1 << log2W, hh = 1 << log2H;
            int sx = plane > 0 ? subX : 0, sy = plane > 0 ? subY : 0;
            int maxX = ((fs.MiCols * Av1C.MiSize) >> sx) - 1;
            int maxY = ((fs.MiRows * Av1C.MiSize) >> sy) - 1;
            var cur = fs.Cur.Planes[plane];
            int stride = fs.Cur.Stride[plane];
            int n = w + hh;
            int baseVal = 1 << (bitDepth - 1);
            var A = aboveRow;
            var Lc = leftCol;
            const int o = EdgeOff;
            if (!haveAbove && haveLeft)
            {
                int v = cur[y * stride + x - 1];
                for (int i = 0; i < n; i++) A[o + i] = v;
            }
            else if (!haveAbove && !haveLeft)
            {
                for (int i = 0; i < n; i++) A[o + i] = baseVal - 1;
            }
            else
            {
                int aboveLimit = Math.Min(maxX, x + (haveAboveRight ? 2 * w : w) - 1);
                int rowOff = (y - 1) * stride;
                for (int i = 0; i < n; i++) A[o + i] = cur[rowOff + Math.Min(aboveLimit, x + i)];
            }
            if (!haveLeft && haveAbove)
            {
                int v = cur[(y - 1) * stride + x];
                for (int i = 0; i < n; i++) Lc[o + i] = v;
            }
            else if (!haveLeft && !haveAbove)
            {
                for (int i = 0; i < n; i++) Lc[o + i] = baseVal + 1;
            }
            else
            {
                int leftLimit = Math.Min(maxY, y + (haveBelowLeft ? 2 * hh : hh) - 1);
                for (int i = 0; i < n; i++) Lc[o + i] = cur[Math.Min(leftLimit, y + i) * stride + x - 1];
            }
            int corner;
            if (haveAbove && haveLeft) corner = cur[(y - 1) * stride + x - 1];
            else if (haveAbove) corner = cur[(y - 1) * stride + x];
            else if (haveLeft) corner = cur[y * stride + x - 1];
            else corner = baseVal;
            A[o - 1] = corner;
            Lc[o - 1] = corner;

            if (plane == 0 && useFilterIntra) RecursivePredict(w, hh);
            else if (IsDirectionalMode(mode)) DirectionalPredict(plane, x, y, haveLeft, haveAbove, mode, w, hh, maxX, maxY);
            else if (mode is Av1C.SmoothPred or Av1C.SmoothVPred or Av1C.SmoothHPred) SmoothPredict(mode, log2W, log2H, w, hh);
            else if (mode == Av1C.DcPred) DcPredict(haveLeft, haveAbove, log2W, log2H, w, hh);
            else PaethPredict(w, hh);

            for (int i = 0; i < hh; i++)
            {
                int rowOff = (y + i) * stride + x;
                for (int j = 0; j < w; j++) cur[rowOff + j] = (ushort)pred[i * w + j];
            }
        }

        void PaethPredict(int w, int hh)
        {
            const int o = EdgeOff;
            int tl = aboveRow[o - 1];
            for (int i = 0; i < hh; i++)
            {
                int left = leftCol[o + i];
                for (int j = 0; j < w; j++)
                {
                    int top = aboveRow[o + j];
                    int bas = top + left - tl;
                    int pLeft = Math.Abs(bas - left), pTop = Math.Abs(bas - top), pTopLeft = Math.Abs(bas - tl);
                    int v;
                    if (pLeft <= pTop && pLeft <= pTopLeft) v = left;
                    else if (pTop <= pTopLeft) v = top;
                    else v = tl;
                    pred[i * w + j] = v;
                }
            }
        }

        void RecursivePredict(int w, int hh)
        {
            const int o = EdgeOff;
            int w4 = w >> 2, h2 = hh >> 1;
            int maxV = (1 << bitDepth) - 1;
            Span<int> p = stackalloc int[7];
            var taps = T.IntraFilterTaps;
            int tapBase = filterIntraMode * 56;
            for (int i2 = 0; i2 < h2; i2++)
                for (int j4 = 0; j4 < w4; j4++)
                {
                    for (int i = 0; i < 7; i++)
                    {
                        if (i < 5)
                        {
                            if (i2 == 0) p[i] = aboveRow[o + (j4 << 2) + i - 1];
                            else if (j4 == 0 && i == 0) p[i] = leftCol[o + (i2 << 1) - 1];
                            else p[i] = pred[((i2 << 1) - 1) * w + (j4 << 2) + i - 1];
                        }
                        else
                        {
                            if (j4 == 0) p[i] = leftCol[o + (i2 << 1) + i - 5];
                            else p[i] = pred[((i2 << 1) + i - 5) * w + (j4 << 2) - 1];
                        }
                    }
                    for (int i1 = 0; i1 < 2; i1++)
                        for (int j1 = 0; j1 < 4; j1++)
                        {
                            int pr = 0;
                            int tb = tapBase + ((i1 << 2) + j1) * 7;
                            for (int i = 0; i < 7; i++) pr += taps[tb + i] * p[i];
                            pred[((i2 << 1) + i1) * w + (j4 << 2) + j1] = Math.Clamp(Round2Signed(pr, Av1C.IntraFilterScaleBits), 0, maxV);
                        }
                }
        }

        static int Round2Signed(int x, int n) => x >= 0 ? (x + (1 << (n - 1))) >> n : -((-x + (1 << (n - 1))) >> n);
        static long Round2Signed(long x, int n) => x >= 0 ? (x + (1L << (n - 1))) >> n : -((-x + (1L << (n - 1))) >> n);

        void DirectionalPredict(int plane, int x, int y, bool haveLeft, bool haveAbove, int mode, int w, int hh, int maxX, int maxY)
        {
            const int o = EdgeOff;
            var A = aboveRow;
            var Lc = leftCol;
            int angleDelta = plane == 0 ? AngleDeltaY : AngleDeltaUV;
            int pAngle = T.ModeToAngle[mode] + angleDelta * Av1C.AngleStep;
            int upsampleAbove = 0, upsampleLeft = 0;
            if (seq.EnableIntraEdgeFilter)
            {
                if (pAngle != 90 && pAngle != 180)
                {
                    if (pAngle > 90 && pAngle < 180 && w + hh >= 24)
                    {
                        int s = Lc[o] * 5 + A[o - 1] * 6 + A[o] * 5;
                        int v = (s + 8) >> 4;
                        Lc[o - 1] = v;
                        A[o - 1] = v;
                    }
                    int filterType = GetFilterType(plane) ? 1 : 0;
                    if (haveAbove)
                    {
                        int strength = EdgeFilterStrength(w, hh, filterType, pAngle - 90);
                        int numPx = Math.Min(w, maxX - x + 1) + (pAngle < 90 ? hh : 0) + 1;
                        EdgeFilter(A, numPx, strength);
                    }
                    if (haveLeft)
                    {
                        int strength = EdgeFilterStrength(w, hh, filterType, pAngle - 180);
                        int numPx = Math.Min(hh, maxY - y + 1) + (pAngle > 180 ? w : 0) + 1;
                        EdgeFilter(Lc, numPx, strength);
                    }
                }
                int ft = GetFilterTypeCached(plane);
                upsampleAbove = UseUpsample(w, hh, ft, pAngle - 90) ? 1 : 0;
                int numPxA = w + (pAngle < 90 ? hh : 0);
                if (upsampleAbove != 0) EdgeUpsample(A, numPxA);
                upsampleLeft = UseUpsample(w, hh, ft, pAngle - 180) ? 1 : 0;
                int numPxL = hh + (pAngle > 180 ? w : 0);
                if (upsampleLeft != 0) EdgeUpsample(Lc, numPxL);
            }
            int dx = 0, dy = 0;
            var der = T.DrIntraDerivative;
            if (pAngle < 90) dx = der[pAngle];
            else if (pAngle > 90 && pAngle < 180) dx = der[180 - pAngle];
            if (pAngle > 90 && pAngle < 180) dy = der[pAngle - 90];
            else if (pAngle > 180) dy = der[270 - pAngle];
            if (pAngle < 90)
            {
                int maxBaseX = (w + hh - 1) << upsampleAbove;
                for (int i = 0; i < hh; i++)
                {
                    int idx = (i + 1) * dx;
                    int shift = ((idx << upsampleAbove) >> 1) & 0x1F;
                    int b0 = idx >> (6 - upsampleAbove);
                    for (int j = 0; j < w; j++)
                    {
                        int bs = b0 + (j << upsampleAbove);
                        int v;
                        if (bs < maxBaseX) v = (A[o + bs] * (32 - shift) + A[o + bs + 1] * shift + 16) >> 5;
                        else v = A[o + maxBaseX];
                        pred[i * w + j] = v;
                    }
                }
            }
            else if (pAngle > 90 && pAngle < 180)
            {
                for (int i = 0; i < hh; i++)
                    for (int j = 0; j < w; j++)
                    {
                        int idx = (j << 6) - (i + 1) * dx;
                        int bs = idx >> (6 - upsampleAbove);
                        int v;
                        if (bs >= -(1 << upsampleAbove))
                        {
                            int shift = ((idx << upsampleAbove) >> 1) & 0x1F;
                            v = (A[o + bs] * (32 - shift) + A[o + bs + 1] * shift + 16) >> 5;
                        }
                        else
                        {
                            idx = (i << 6) - (j + 1) * dy;
                            bs = idx >> (6 - upsampleLeft);
                            int shift = ((idx << upsampleLeft) >> 1) & 0x1F;
                            v = (Lc[o + bs] * (32 - shift) + Lc[o + bs + 1] * shift + 16) >> 5;
                        }
                        pred[i * w + j] = v;
                    }
            }
            else if (pAngle > 180)
            {
                for (int j = 0; j < w; j++)
                {
                    int idx = (j + 1) * dy;
                    int b0 = idx >> (6 - upsampleLeft);
                    int shift = ((idx << upsampleLeft) >> 1) & 0x1F;
                    for (int i = 0; i < hh; i++)
                    {
                        int bs = b0 + (i << upsampleLeft);
                        pred[i * w + j] = (Lc[o + bs] * (32 - shift) + Lc[o + bs + 1] * shift + 16) >> 5;
                    }
                }
            }
            else if (pAngle == 90)
            {
                for (int i = 0; i < hh; i++)
                    for (int j = 0; j < w; j++) pred[i * w + j] = A[o + j];
            }
            else
            {
                for (int i = 0; i < hh; i++)
                    for (int j = 0; j < w; j++) pred[i * w + j] = Lc[o + i];
            }
        }

        int GetFilterTypeCached(int plane) => GetFilterType(plane) ? 1 : 0;

        bool GetFilterType(int plane)
        {
            bool aboveSmooth = false, leftSmooth = false;
            if (plane == 0 ? AvailU : AvailUChroma)
            {
                int r = MiRow - 1, c = MiCol;
                if (plane > 0)
                {
                    if (subX != 0 && (MiCol & 1) == 0) c++;
                    if (subY != 0 && (MiRow & 1) != 0) r--;
                }
                aboveSmooth = IsSmooth(r, c, plane);
            }
            if (plane == 0 ? AvailL : AvailLChroma)
            {
                int r = MiRow, c = MiCol - 1;
                if (plane > 0)
                {
                    if (subX != 0 && (MiCol & 1) != 0) c--;
                    if (subY != 0 && (MiRow & 1) == 0) r++;
                }
                leftSmooth = IsSmooth(r, c, plane);
            }
            return aboveSmooth || leftSmooth;
        }

        bool IsSmooth(int row, int col, int plane)
        {
            ref var b = ref Nb(row, col);
            int mode;
            if (plane == 0) mode = b.YMode;
            else
            {
                if (b.RefFrame0 > Av1C.IntraFrame) return false;
                mode = b.UVMode;
            }
            return mode is Av1C.SmoothPred or Av1C.SmoothVPred or Av1C.SmoothHPred;
        }

        static int EdgeFilterStrength(int w, int hh, int filterType, int delta)
        {
            int d = Math.Abs(delta);
            int blkWh = w + hh;
            int strength = 0;
            if (filterType == 0)
            {
                if (blkWh <= 8) { if (d >= 56) strength = 1; }
                else if (blkWh <= 12) { if (d >= 40) strength = 1; }
                else if (blkWh <= 16) { if (d >= 40) strength = 1; }
                else if (blkWh <= 24)
                {
                    if (d >= 8) strength = 1;
                    if (d >= 16) strength = 2;
                    if (d >= 32) strength = 3;
                }
                else if (blkWh <= 32)
                {
                    strength = 1;
                    if (d >= 4) strength = 2;
                    if (d >= 32) strength = 3;
                }
                else strength = 3;
            }
            else
            {
                if (blkWh <= 8)
                {
                    if (d >= 40) strength = 1;
                    if (d >= 64) strength = 2;
                }
                else if (blkWh <= 16)
                {
                    if (d >= 20) strength = 1;
                    if (d >= 48) strength = 2;
                }
                else if (blkWh <= 24) { if (d >= 4) strength = 3; }
                else strength = 3;
            }
            return strength;
        }

        static bool UseUpsample(int w, int hh, int filterType, int delta)
        {
            int d = Math.Abs(delta);
            int blkWh = w + hh;
            if (d <= 0 || d >= 40) return false;
            return filterType == 0 ? blkWh <= 16 : blkWh <= 8;
        }

        void EdgeFilter(int[] buf, int sz, int strength)
        {
            if (strength == 0) return;
            const int o = EdgeOff;
            var e = edgeBuf;
            for (int i = 0; i < sz; i++) e[i] = buf[o + i - 1];
            var kernel = T.IntraEdgeKernel;
            int kb = (strength - 1) * 5;
            for (int i = 1; i < sz; i++)
            {
                int s = 0;
                for (int j = 0; j < 5; j++)
                {
                    int k = Math.Clamp(i - 2 + j, 0, sz - 1);
                    s += kernel[kb + j] * e[k];
                }
                buf[o + i - 1] = (s + 8) >> 4;
            }
        }

        void EdgeUpsample(int[] buf, int numPx)
        {
            const int o = EdgeOff;
            var dup = edgeBuf;
            dup[0] = buf[o - 1];
            for (int i = -1; i < numPx; i++) dup[i + 2] = buf[o + i];
            dup[numPx + 2] = buf[o + numPx - 1];
            int maxV = (1 << bitDepth) - 1;
            buf[o - 2] = dup[0];
            for (int i = 0; i < numPx; i++)
            {
                int s = -dup[i] + 9 * dup[i + 1] + 9 * dup[i + 2] - dup[i + 3];
                s = Math.Clamp((s + 8) >> 4, 0, maxV);
                buf[o + 2 * i - 1] = s;
                buf[o + 2 * i] = dup[i + 2];
            }
        }

        void DcPredict(bool haveLeft, bool haveAbove, int log2W, int log2H, int w, int hh)
        {
            const int o = EdgeOff;
            int avg;
            int maxV = (1 << bitDepth) - 1;
            if (haveLeft && haveAbove)
            {
                int sum = 0;
                for (int k = 0; k < hh; k++) sum += leftCol[o + k];
                for (int k = 0; k < w; k++) sum += aboveRow[o + k];
                sum += (w + hh) >> 1;
                avg = sum / (w + hh);
            }
            else if (haveLeft)
            {
                int sum = 0;
                for (int k = 0; k < hh; k++) sum += leftCol[o + k];
                avg = Math.Clamp((sum + (hh >> 1)) >> log2H, 0, maxV);
            }
            else if (haveAbove)
            {
                int sum = 0;
                for (int k = 0; k < w; k++) sum += aboveRow[o + k];
                avg = Math.Clamp((sum + (w >> 1)) >> log2W, 0, maxV);
            }
            else avg = 1 << (bitDepth - 1);
            pred.AsSpan(0, w * hh).Fill(avg);
        }

        static ReadOnlySpan<byte> SmWeights(int log2) => log2 switch
        {
            2 => T.SmWeightsTx4x4,
            3 => T.SmWeightsTx8x8,
            4 => T.SmWeightsTx16x16,
            5 => T.SmWeightsTx32x32,
            _ => T.SmWeightsTx64x64,
        };

        void SmoothPredict(int mode, int log2W, int log2H, int w, int hh)
        {
            const int o = EdgeOff;
            var wx = SmWeights(log2W);
            var wy = SmWeights(log2H);
            int bottomLeft = leftCol[o + hh - 1], topRight = aboveRow[o + w - 1];
            if (mode == Av1C.SmoothPred)
            {
                for (int i = 0; i < hh; i++)
                    for (int j = 0; j < w; j++)
                    {
                        int s = wy[i] * aboveRow[o + j] + (256 - wy[i]) * bottomLeft + wx[j] * leftCol[o + i] + (256 - wx[j]) * topRight;
                        pred[i * w + j] = (s + 256) >> 9;
                    }
            }
            else if (mode == Av1C.SmoothVPred)
            {
                for (int i = 0; i < hh; i++)
                    for (int j = 0; j < w; j++)
                    {
                        int s = wy[i] * aboveRow[o + j] + (256 - wy[i]) * bottomLeft;
                        pred[i * w + j] = (s + 128) >> 8;
                    }
            }
            else
            {
                for (int i = 0; i < hh; i++)
                    for (int j = 0; j < w; j++)
                    {
                        int s = wx[j] * leftCol[o + i] + (256 - wx[j]) * topRight;
                        pred[i * w + j] = (s + 128) >> 8;
                    }
            }
        }

        // ------------------------------------------------------------------ palette prediction (7.11.4)

        void PredictPalette(int plane, int startX, int startY, int x, int y, int txSz)
        {
            int w = T.TxWidth[txSz], hh = T.TxHeight[txSz];
            var palette = plane == 0 ? paletteColorsY : plane == 1 ? paletteColorsU : paletteColorsV;
            var map = plane == 0 ? colorMapY : colorMapUV;
            var cur = fs.Cur.Planes[plane];
            int stride = fs.Cur.Stride[plane];
            for (int i = 0; i < hh; i++)
            {
                int rowOff = (startY + i) * stride + startX;
                int mo = (y * 4 + i) * MapStride + x * 4;
                for (int j = 0; j < w; j++) cur[rowOff + j] = palette[map[mo + j]];
            }
        }

        // ------------------------------------------------------------------ chroma from luma (7.11.5)

        void PredictChromaFromLuma(int plane, int startX, int startY, int txSz)
        {
            int w = T.TxWidth[txSz], hh = T.TxHeight[txSz];
            int alpha = plane == 1 ? CflAlphaU : CflAlphaV;
            var luma = fs.Cur.Planes[0];
            int ls = fs.Cur.Stride[0];
            int lumaAvg = 0;
            for (int i = 0; i < hh; i++)
            {
                int lumaY = Math.Min((startY + i) << subY, MaxLumaH - (1 << subY));
                for (int j = 0; j < w; j++)
                {
                    int lumaX = Math.Min((startX + j) << subX, MaxLumaW - (1 << subX));
                    int t = 0;
                    for (int dy = 0; dy <= subY; dy++)
                        for (int dx = 0; dx <= subX; dx++)
                            t += luma[(lumaY + dy) * ls + lumaX + dx];
                    int v = t << (3 - subX - subY);
                    cflL[i * w + j] = v;
                    lumaAvg += v;
                }
            }
            int sh = T.TxWidthLog2[txSz] + T.TxHeightLog2[txSz];
            lumaAvg = (lumaAvg + (1 << (sh - 1))) >> sh;
            var cur = fs.Cur.Planes[plane];
            int stride = fs.Cur.Stride[plane];
            int maxV = (1 << bitDepth) - 1;
            for (int i = 0; i < hh; i++)
            {
                int rowOff = (startY + i) * stride + startX;
                for (int j = 0; j < w; j++)
                {
                    int dc = cur[rowOff + j];
                    int scaledLuma = Round2Signed(alpha * (cflL[i * w + j] - lumaAvg), 6);
                    cur[rowOff + j] = (ushort)Math.Clamp(dc + scaledLuma, 0, maxV);
                }
            }
        }
    }
}
