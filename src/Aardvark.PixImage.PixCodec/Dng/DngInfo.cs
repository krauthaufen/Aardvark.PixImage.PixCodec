using System;
using System.Collections.Generic;
using System.Linq;

namespace Aardvark.ImageCodecs
{
    /// <summary>How the raw image of a DNG holds its colours.</summary>
    public enum DngRawLayout
    {
        /// <summary>One sample per pixel behind a colour filter array (PhotometricInterpretation 32803): needs demosaicing.</summary>
        ColorFilterArray,
        /// <summary>All colour planes at every pixel (PhotometricInterpretation 34892), e.g. already demosaiced or monochrome.</summary>
        LinearRaw,
    }

    /// <summary>A rectangle in pixels: Top / Left inclusive, Bottom / Right exclusive (the DNG order top, left, bottom, right).</summary>
    public readonly record struct DngRect(int Top, int Left, int Bottom, int Right)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    /// <summary>
    /// One colour calibration of the camera profile (DNG 1.7.1 chapter 6): the matrices for one calibration illuminant.
    /// Matrices are row-major; n = <see cref="DngInfo.ColorPlanes"/>.
    /// </summary>
    public sealed class DngCalibration
    {
        /// <summary>CalibrationIlluminantN: an EXIF LightSource code (17 = Standard A, 21 = D65, 255 = custom, ...).</summary>
        public int Illuminant { get; init; }
        /// <summary>IlluminantDataN as x-y chromaticity, for <see cref="Illuminant"/> 255; null otherwise or for spectral data.</summary>
        public (double X, double Y)? IlluminantXY { get; init; }
        /// <summary>Correlated colour temperature of the illuminant in kelvin, NaN when unknown (it then takes no part in interpolation).</summary>
        public double Temperature { get; init; } = double.NaN;
        /// <summary>ColorMatrixN: XYZ to reference camera space, n x 3.</summary>
        public double[] ColorMatrix { get; init; } = Array.Empty<double>();
        /// <summary>CameraCalibrationN, n x n; null = identity.</summary>
        public double[]? CameraCalibration { get; init; }
        /// <summary>ReductionMatrixN, 3 x n (only for n &gt; 3); null when absent.</summary>
        public double[]? ReductionMatrix { get; init; }
        /// <summary>ForwardMatrixN: white balanced camera space to XYZ D50, 3 x n; null when absent.</summary>
        public double[]? ForwardMatrix { get; init; }
        /// <summary>
        /// ProfileHueSatMapDataN: hue shift (degrees), saturation scale, value scale per table entry (value divisions outer,
        /// hue middle, saturation inner; see <see cref="DngInfo.HueSatMapDims"/>); null when absent.
        /// </summary>
        public float[]? HueSatMap { get; init; }
    }

    /// <summary>One opcode of an opcode list (DNG 1.7.1 chapter 7).</summary>
    public sealed class DngOpcode
    {
        public int Id { get; init; }
        /// <summary>The opcode's name (WarpRectilinear, GainMap, ...), "Unknown" for ids this reader does not know.</summary>
        public string Name { get; init; } = "";
        /// <summary>DNG version the opcode was defined in, as 0xAABBCCDD for version A.B.C.D.</summary>
        public uint Version { get; init; }
        /// <summary>Flag bit 0: a reader may skip the opcode.</summary>
        public bool Optional { get; init; }
        /// <summary>Flag bit 1: the opcode may be skipped for preview quality processing.</summary>
        public bool PreviewSkippable { get; init; }
        /// <summary>The parameter area as stored (big endian).</summary>
        public byte[] Parameters { get; init; } = Array.Empty<byte>();
        /// <summary>Whether this decoder can apply the opcode.</summary>
        public bool Supported => DngOpcodes.IsSupported(Id);

        public override string ToString() => $"{Name}{(Optional ? " (optional)" : "")}";
    }

    /// <summary>A preview / thumbnail directory of a DNG: rendered images the camera or converter stored next to the raw data.</summary>
    public sealed class DngPreviewInfo
    {
        /// <summary>Directory: index in the main IFD chain and the path of SubIFD indices below it (empty for the chain directory itself).</summary>
        public int ImageIndex { get; init; }
        public int[] SubImagePath { get; init; } = Array.Empty<int>();
        public int Width { get; init; }
        public int Height { get; init; }
        /// <summary>NewSubFileType (1 = preview, 0x10001 = alternative preview).</summary>
        public int SubfileType { get; init; }
        public int Compression { get; init; }
        public int Photometric { get; init; }
        /// <summary>PreviewColorSpace: 0 unknown, 1 gray gamma 2.2, 2 sRGB, 3 Adobe RGB, 4 ProPhoto RGB.</summary>
        public int ColorSpace { get; init; }
        /// <summary>The directory holds one complete baseline JPEG stream that <see cref="Dng.ExtractPreviewJpeg(ReadOnlySpan{byte}, int)"/> can return as is.</summary>
        public bool IsJpegFile { get; init; }
        internal long JpegOffset, JpegLength, Offset;
    }

    /// <summary>
    /// The parameters of a DNG file that describe its raw image and how to render it, read without decoding pixel data
    /// (<see cref="Dng.ReadInfo(ReadOnlySpan{byte})"/>). Coordinates are those of the stored raw image (before orientation).
    /// </summary>
    public sealed class DngInfo
    {
        /// <summary>DNGVersion, e.g. {1, 6, 0, 0}.</summary>
        public byte[] Version { get; init; } = Array.Empty<byte>();
        public byte[] BackwardVersion { get; init; } = Array.Empty<byte>();
        public string? Make { get; init; }
        public string? Model { get; init; }
        public string? UniqueCameraModel { get; init; }
        public string? LocalizedCameraModel { get; init; }
        public string? CameraSerialNumber { get; init; }
        public string? Software { get; init; }

        /// <summary>Location of the raw directory: index in the main IFD chain, then SubIFD indices (empty: the chain directory itself).</summary>
        public int RawImageIndex { get; init; }
        public int[] RawSubImagePath { get; init; } = Array.Empty<int>();

        public DngRawLayout Layout { get; init; }
        /// <summary>Stored size of the raw image, masked areas included.</summary>
        public int RawWidth { get; init; }
        public int RawHeight { get; init; }
        public int SamplesPerPixel { get; init; }
        public int BitsPerSample { get; init; }
        public bool IsFloat { get; init; }
        public int Compression { get; init; }

        /// <summary>ActiveArea: the non-masked sensor pixels. Black levels, the CFA pattern, the default crop and opcode lists 2 / 3 refer to its top-left corner.</summary>
        public DngRect ActiveArea { get; init; }
        /// <summary>MaskedAreas: fully masked rectangles (optional, for measuring black).</summary>
        public DngRect[] MaskedAreas { get; init; } = Array.Empty<DngRect>();
        /// <summary>DefaultCropOrigin / DefaultCropSize relative to the active area, in raw pixels (may be fractional).</summary>
        public double DefaultCropX { get; init; }
        public double DefaultCropY { get; init; }
        public double DefaultCropWidth { get; init; }
        public double DefaultCropHeight { get; init; }
        /// <summary>DefaultScale (non-square pixels), 1 / 1 for square pixels.</summary>
        public double DefaultScaleH { get; init; } = 1;
        public double DefaultScaleV { get; init; } = 1;
        public double BestQualityScale { get; init; } = 1;
        /// <summary>DefaultUserCrop (top, left, bottom, right) relative to the default crop, null when absent.</summary>
        public double[]? DefaultUserCrop { get; init; }
        /// <summary>Size of the rendered image (default crop times default scale), before orientation.</summary>
        public int FinalWidth { get; init; }
        public int FinalHeight { get; init; }
        /// <summary>TIFF / EXIF orientation 1..8 (1 when absent).</summary>
        public int Orientation { get; init; } = 1;

        // ---- colour filter array
        public int CfaRepeatRows { get; init; }
        public int CfaRepeatCols { get; init; }
        /// <summary>CFAPattern: colour codes (0 red, 1 green, 2 blue, 3 cyan, 4 magenta, 5 yellow, 6 white) in row scan order, origin at the active area's top-left.</summary>
        public byte[] CfaPattern { get; init; } = Array.Empty<byte>();
        /// <summary>CFAPlaneColor: the colour code of each colour plane (default red, green, blue).</summary>
        public byte[] CfaPlaneColor { get; init; } = new byte[] { 0, 1, 2 };
        /// <summary>CFALayout: 1 rectangular, 2..9 staggered (not supported for rendering).</summary>
        public int CfaLayout { get; init; } = 1;
        /// <summary>Number of colour planes (camera channels): the CFAPlaneColor count for CFA data, SamplesPerPixel for LinearRaw.</summary>
        public int ColorPlanes { get; init; }

        // ---- mapping to linear values (chapter 5)
        public ushort[]? LinearizationTable { get; init; }
        public int BlackLevelRepeatRows { get; init; } = 1;
        public int BlackLevelRepeatCols { get; init; } = 1;
        /// <summary>BlackLevel in row-column-sample order (BlackLevelRepeatRows * Cols * SamplesPerPixel values).</summary>
        public double[] BlackLevel { get; init; } = Array.Empty<double>();
        /// <summary>BlackLevelDeltaH (one per active area column) / BlackLevelDeltaV (one per row), null when absent.</summary>
        public double[]? BlackLevelDeltaH { get; init; }
        public double[]? BlackLevelDeltaV { get; init; }
        /// <summary>WhiteLevel per sample.</summary>
        public double[] WhiteLevel { get; init; } = Array.Empty<double>();
        /// <summary>RowInterleaveFactor / ColumnInterleaveFactor of the stored rows / columns.</summary>
        public int RowInterleaveFactor { get; init; } = 1;
        public int ColumnInterleaveFactor { get; init; } = 1;
        internal int SubTileBlockRows = 1, SubTileBlockCols = 1;

        // ---- colour (chapter 6)
        /// <summary>The colour calibrations (1 to 3) of the primary camera profile in tag order (CalibrationIlluminant1..3).</summary>
        public IReadOnlyList<DngCalibration> Calibrations { get; init; } = Array.Empty<DngCalibration>();
        /// <summary>AnalogBalance per plane, null = all 1.</summary>
        public double[]? AnalogBalance { get; init; }
        /// <summary>AsShotNeutral per plane, null when absent.</summary>
        public double[]? AsShotNeutral { get; init; }
        /// <summary>AsShotWhiteXY, null when absent.</summary>
        public (double X, double Y)? AsShotWhiteXY { get; init; }
        /// <summary>BaselineExposure in EV (and the profile's BaselineExposureOffset, added when rendering).</summary>
        public double BaselineExposure { get; init; }
        public double BaselineExposureOffset { get; init; }
        public double BaselineNoise { get; init; } = 1;
        public double BaselineSharpness { get; init; } = 1;
        public double LinearResponseLimit { get; init; } = 1;
        public double[]? NoiseProfile { get; init; }
        public string? CameraCalibrationSignature { get; init; }
        public string? ProfileCalibrationSignature { get; init; }
        public string? ProfileName { get; init; }
        /// <summary>ProfileHueSatMapDims (hue, saturation, value divisions) and ProfileHueSatMapEncoding (0 linear, 1 sRGB); null without tables.</summary>
        public int[]? HueSatMapDims { get; init; }
        public int HueSatMapEncoding { get; init; }
        /// <summary>ProfileLookTableDims / Data / Encoding: the profile's default "look", same layout as the hue / saturation maps.</summary>
        public int[]? LookTableDims { get; init; }
        public float[]? LookTable { get; init; }
        public int LookTableEncoding { get; init; }
        /// <summary>DefaultBlackRender: 0 auto, 1 none.</summary>
        public int DefaultBlackRender { get; init; }

        // ---- opcodes (chapter 7)
        /// <summary>OpcodeList1 (applied to the stored values), OpcodeList2 (linear values), OpcodeList3 (after demosaicing).</summary>
        public IReadOnlyList<DngOpcode> OpcodeList1 { get; init; } = Array.Empty<DngOpcode>();
        public IReadOnlyList<DngOpcode> OpcodeList2 { get; init; } = Array.Empty<DngOpcode>();
        public IReadOnlyList<DngOpcode> OpcodeList3 { get; init; } = Array.Empty<DngOpcode>();

        /// <summary>The rendered previews / thumbnails stored in the file, smallest first.</summary>
        public IReadOnlyList<DngPreviewInfo> Previews { get; init; } = Array.Empty<DngPreviewInfo>();

        /// <summary>Whether the CFA is a 2 x 2 Bayer pattern (one red, two diagonal greens, one blue plane).</summary>
        public bool IsBayer
        {
            get
            {
                if (Layout != DngRawLayout.ColorFilterArray || CfaRepeatRows != 2 || CfaRepeatCols != 2 || ColorPlanes != 3) return false;
                int[] p = Enumerable.Range(0, 4).Select(i => CfaPlane(i / 2, i % 2)).ToArray();
                return p.All(x => x >= 0) && p.Distinct().Count() == 3 && (p[0] == p[3] ? p[1] != p[2] : p[1] == p[2]);
            }
        }

        /// <summary>The colour plane at an active area position (row, column), -1 if the pattern names a colour without plane.</summary>
        public int CfaPlane(int row, int col)
        {
            if (CfaRepeatRows <= 0 || CfaRepeatCols <= 0) return 0;
            byte code = CfaPattern[(row % CfaRepeatRows) * CfaRepeatCols + col % CfaRepeatCols];
            return Array.IndexOf(CfaPlaneColor, code);
        }

        /// <summary>Black level of an active area position and sample, BlackLevelDeltaH / V included.</summary>
        public double BlackAt(int row, int col, int sample)
        {
            double b = BlackLevel.Length == 0 ? 0
                : BlackLevel[Math.Min(BlackLevel.Length - 1, ((row % BlackLevelRepeatRows) * BlackLevelRepeatCols + col % BlackLevelRepeatCols) * SamplesPerPixel + sample)];
            if (BlackLevelDeltaH != null && col < BlackLevelDeltaH.Length) b += BlackLevelDeltaH[col];
            if (BlackLevelDeltaV != null && row < BlackLevelDeltaV.Length) b += BlackLevelDeltaV[row];
            return b;
        }
    }
}
