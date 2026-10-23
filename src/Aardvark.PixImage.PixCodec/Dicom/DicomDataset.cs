using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Aardvark.ImageCodecs.Compression;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// One element of a <see cref="DicomDataset"/>: its tag, value representation and value, which is either bytes, the
    /// items of a sequence, or (encapsulated pixel data) the fragments of the compressed frames.
    /// </summary>
    public sealed class DicomElement
    {
        /// <summary>Group &lt;&lt; 16 | element.</summary>
        public uint Tag { get; }
        /// <summary>The two letter value representation; for implicit VR files the data dictionary's, or "UN".</summary>
        public string VR { get; }
        public ushort Group => (ushort)(Tag >> 16);
        public ushort Element => (ushort)Tag;
        /// <summary>The items of a sequence (SQ, or UN of undefined length), else null.</summary>
        public IReadOnlyList<DicomDataset>? Items { get; }
        /// <summary>Encapsulated pixel data: the basic offset table (first item) and then the fragments, else null.</summary>
        public IReadOnlyList<ReadOnlyMemory<byte>>? Fragments { get; }

        readonly ReadOnlyMemory<byte> _value;
        internal readonly bool BigEndian;
        internal readonly DicomDataset Owner;

        internal DicomElement(DicomDataset owner, uint tag, string vr, ReadOnlyMemory<byte> value, bool bigEndian, List<DicomDataset>? items = null, List<ReadOnlyMemory<byte>>? fragments = null)
        {
            Owner = owner; Tag = tag; VR = vr; _value = value; BigEndian = bigEndian; Items = items; Fragments = fragments;
        }

        /// <summary>The value bytes as stored (in the file's byte order).</summary>
        public ReadOnlySpan<byte> Bytes => _value.Span;
        public ReadOnlyMemory<byte> Memory => _value;
        public int Length => _value.Length;

        public override string ToString() => $"({Group:X4},{Element:X4}) {VR} {(Items != null ? $"{Items.Count} items" : Fragments != null ? $"{Fragments.Count} fragments" : Owner.GetString(Tag) ?? $"{Length} bytes")}";
    }

    /// <summary>
    /// A parsed DICOM data set (PS3.5): the elements in tag order, with sequences as nested data sets. Values are views
    /// of the file's bytes; the accessors decode them by value representation (text with the data set's character
    /// set, binary numbers in the file's byte order).
    /// </summary>
    public sealed class DicomDataset
    {
        readonly List<DicomElement> _elements = new();
        readonly Dictionary<uint, int> _index = new();

        /// <summary>The data set this one is an item of (null at the top level).</summary>
        public DicomDataset? Parent { get; internal set; }
        /// <summary>File offset of the item (for directory records, which point at each other by offset); -1 at the top level.</summary>
        public long Offset { get; internal set; } = -1;
        /// <summary>The transfer syntax of the data set (top level; items inherit it).</summary>
        public string TransferSyntax { get; internal set; } = DicomUids.ExplicitVrLittleEndian;

        internal Encoding? CharacterSet;
        /// <summary>A header-only parse stopped at the pixel data (so every header element was seen).</summary>
        internal bool ReachedPixels;

        public IReadOnlyList<DicomElement> Elements => _elements;

        public bool Contains(uint tag) => _index.ContainsKey(tag);

        public DicomElement? this[uint tag] => _index.TryGetValue(tag, out int i) ? _elements[i] : null;

        internal void Add(DicomElement e)
        {
            if (_index.TryGetValue(e.Tag, out int i)) { _elements[i] = e; return; }
            // elements are in ascending tag order in a valid file; keep that order even for damaged ones
            _index[e.Tag] = _elements.Count;
            _elements.Add(e);
        }

        Encoding Text
        {
            get
            {
                for (var d = this; d != null; d = d.Parent) if (d.CharacterSet != null) return d.CharacterSet;
                return Encoding.Latin1;
            }
        }

        static bool IsText(string vr) => vr is "AE" or "AS" or "CS" or "DA" or "DS" or "DT" or "IS" or "LO" or "LT" or "PN" or "SH" or "ST" or "TM" or "UC" or "UI" or "UR" or "UT" or "UN";

        /// <summary>The whole value as text (multiple values separated by '\'), trailing padding removed; null if absent.</summary>
        public string? GetString(uint tag)
        {
            var e = this[tag];
            if (e == null || e.Items != null || e.Fragments != null) return null;
            if (!IsText(e.VR))
            {
                var nums = GetDoubles(tag);
                return nums == null ? null : string.Join('\\', Array.ConvertAll(nums, v => v.ToString("R", CultureInfo.InvariantCulture)));
            }
            var b = e.Bytes;
            // UI values are padded with NUL, the others with spaces
            var s = (e.VR is "UI" or "CS" or "DS" or "IS" or "DA" or "TM" or "DT" or "AS" or "AE" ? Encoding.ASCII : Text).GetString(b);
            return s.TrimEnd(' ', '\0');
        }

        /// <summary>The values of a multi-valued text element, each trimmed.</summary>
        public string[]? GetStrings(uint tag)
        {
            var s = GetString(tag);
            if (s == null) return null;
            var parts = s.Split('\\');
            for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim(' ', '\0');
            return parts;
        }

        /// <summary>
        /// The numeric values of an element: binary ones (US, SS, UL, SL, UV, SV, FL, FD, and OW / OB / UN of tags the
        /// dictionary knows as numbers) or decimal / integer strings (DS, IS). Null if absent or not numeric.
        /// </summary>
        public double[]? GetDoubles(uint tag)
        {
            var e = this[tag];
            if (e == null || e.Items != null || e.Fragments != null) return null;
            string vr = e.VR;
            if (vr is "UN" or "OB" or "OW") vr = DicomDictionary.VR(tag) ?? vr;
            var b = e.Bytes;
            bool be = e.BigEndian;
            switch (vr)
            {
                case "DS" or "IS":
                {
                    var parts = Encoding.ASCII.GetString(b).Split('\\');
                    var r = new List<double>(parts.Length);
                    foreach (var p in parts)
                    {
                        var t = p.Trim(' ', '\0');
                        if (t.Length == 0) continue;
                        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
                        r.Add(v);
                    }
                    return r.ToArray();
                }
                case "US" or "SS" or "UL" or "SL" or "UV" or "SV" or "FL" or "OF" or "FD" or "OD":
                {
                    int size = vr is "US" or "SS" ? 2 : vr is "UV" or "SV" or "FD" or "OD" ? 8 : 4;
                    var r = new double[b.Length / size];
                    for (int i = 0; i < r.Length; i++)
                    {
                        var x = b.Slice(i * size, size);
                        r[i] = vr switch
                        {
                            "US" => be ? BinaryPrimitives.ReadUInt16BigEndian(x) : BinaryPrimitives.ReadUInt16LittleEndian(x),
                            "SS" => be ? BinaryPrimitives.ReadInt16BigEndian(x) : BinaryPrimitives.ReadInt16LittleEndian(x),
                            "UL" => be ? BinaryPrimitives.ReadUInt32BigEndian(x) : BinaryPrimitives.ReadUInt32LittleEndian(x),
                            "SL" => be ? BinaryPrimitives.ReadInt32BigEndian(x) : BinaryPrimitives.ReadInt32LittleEndian(x),
                            "UV" => be ? BinaryPrimitives.ReadUInt64BigEndian(x) : BinaryPrimitives.ReadUInt64LittleEndian(x),
                            "SV" => be ? BinaryPrimitives.ReadInt64BigEndian(x) : BinaryPrimitives.ReadInt64LittleEndian(x),
                            "FL" or "OF" => be ? BinaryPrimitives.ReadSingleBigEndian(x) : BinaryPrimitives.ReadSingleLittleEndian(x),
                            _ => be ? BinaryPrimitives.ReadDoubleBigEndian(x) : BinaryPrimitives.ReadDoubleLittleEndian(x),
                        };
                    }
                    return r;
                }
                default: return null;
            }
        }

        public double? GetDouble(uint tag, int index = 0)
        {
            var v = GetDoubles(tag);
            return v != null && index < v.Length ? v[index] : null;
        }

        public int? GetInt(uint tag, int index = 0)
        {
            var v = GetDouble(tag, index);
            return v is double d && d >= int.MinValue && d <= int.MaxValue ? (int)Math.Round(d) : null;
        }

        /// <summary>The items of a sequence element (empty if absent).</summary>
        public IReadOnlyList<DicomDataset> GetSequence(uint tag) => this[tag]?.Items ?? Array.Empty<DicomDataset>();

        /// <summary>Looks for an element in this data set, else in the first item of the given sequences (functional groups).</summary>
        internal DicomDataset? Find(uint tag, params uint[] path)
        {
            var d = this;
            foreach (var s in path)
            {
                var items = d.GetSequence(s);
                if (items.Count == 0) return null;
                d = items[0];
            }
            return d.Contains(tag) ? d : null;
        }

        public override string ToString() => $"{_elements.Count} elements";
    }

    /// <summary>UIDs of the transfer syntaxes and SOP classes the codec knows.</summary>
    public static class DicomUids
    {
        public const string ImplicitVrLittleEndian = "1.2.840.10008.1.2";
        public const string ExplicitVrLittleEndian = "1.2.840.10008.1.2.1";
        public const string EncapsulatedUncompressed = "1.2.840.10008.1.2.1.98";
        public const string DeflatedExplicitVrLittleEndian = "1.2.840.10008.1.2.1.99";
        public const string ExplicitVrBigEndian = "1.2.840.10008.1.2.2";
        public const string JpegBaseline = "1.2.840.10008.1.2.4.50";
        public const string JpegExtended = "1.2.840.10008.1.2.4.51";
        public const string JpegLossless = "1.2.840.10008.1.2.4.57";
        public const string JpegLosslessSV1 = "1.2.840.10008.1.2.4.70";
        public const string JpegLsLossless = "1.2.840.10008.1.2.4.80";
        public const string JpegLsNearLossless = "1.2.840.10008.1.2.4.81";
        public const string Jpeg2000Lossless = "1.2.840.10008.1.2.4.90";
        public const string Jpeg2000 = "1.2.840.10008.1.2.4.91";
        public const string Jpeg2000Part2Lossless = "1.2.840.10008.1.2.4.92";
        public const string Jpeg2000Part2 = "1.2.840.10008.1.2.4.93";
        public const string JpegXlLossless = "1.2.840.10008.1.2.4.110";
        public const string JpegXlJpegRecompression = "1.2.840.10008.1.2.4.111";
        public const string JpegXl = "1.2.840.10008.1.2.4.112";
        public const string HtJ2kLossless = "1.2.840.10008.1.2.4.201";
        public const string HtJ2kLosslessRpcl = "1.2.840.10008.1.2.4.202";
        public const string HtJ2k = "1.2.840.10008.1.2.4.203";
        public const string RleLossless = "1.2.840.10008.1.2.5";

        public const string SecondaryCapture = "1.2.840.10008.5.1.4.1.1.7";
        public const string MultiFrameGrayscaleByteSecondaryCapture = "1.2.840.10008.5.1.4.1.1.7.2";
        public const string MultiFrameGrayscaleWordSecondaryCapture = "1.2.840.10008.5.1.4.1.1.7.3";
        public const string MultiFrameTrueColorSecondaryCapture = "1.2.840.10008.5.1.4.1.1.7.4";
        public const string MediaStorageDirectory = "1.2.840.10008.1.3.10";

        internal static bool IsEncapsulated(string ts) => ts != ImplicitVrLittleEndian && ts != ExplicitVrLittleEndian && ts != DeflatedExplicitVrLittleEndian && ts != ExplicitVrBigEndian;
    }

    /// <summary>Tags the codec reads (PS3.6), and the value representations of those it needs in implicit VR files.</summary>
    internal static class DicomTags
    {
        public const uint FileMetaGroupLength = 0x00020000, MediaStorageSopClass = 0x00020002, MediaStorageSopInstance = 0x00020003, TransferSyntax = 0x00020010;
        public const uint ImplementationClassUid = 0x00020012, ImplementationVersionName = 0x00020013;
        public const uint SpecificCharacterSet = 0x00080005, ImageType = 0x00080008, SopClassUid = 0x00080016, SopInstanceUid = 0x00080018;
        public const uint StudyDate = 0x00080020, SeriesDate = 0x00080021, StudyTime = 0x00080030, Modality = 0x00080060, Manufacturer = 0x00080070;
        public const uint StudyDescription = 0x00081030, SeriesDescription = 0x0008103E;
        public const uint PatientName = 0x00100010, PatientId = 0x00100020, PatientBirthDate = 0x00100030, PatientSex = 0x00100040;
        public const uint SliceThickness = 0x00180050, SpacingBetweenSlices = 0x00180088;
        public const uint StudyInstanceUid = 0x0020000D, SeriesInstanceUid = 0x0020000E, StudyId = 0x00200010, SeriesNumber = 0x00200011;
        public const uint AcquisitionNumber = 0x00200012, InstanceNumber = 0x00200013, ImagePositionPatient = 0x00200032, ImageOrientationPatient = 0x00200037;
        public const uint FrameOfReferenceUid = 0x00200052, TemporalPositionIdentifier = 0x00200100, SliceLocation = 0x00201041;
        public const uint SamplesPerPixel = 0x00280002, PhotometricInterpretation = 0x00280004, PlanarConfiguration = 0x00280006, NumberOfFrames = 0x00280008;
        public const uint Rows = 0x00280010, Columns = 0x00280011, PixelSpacing = 0x00280030;
        public const uint BitsAllocated = 0x00280100, BitsStored = 0x00280101, HighBit = 0x00280102, PixelRepresentation = 0x00280103;
        public const uint WindowCenter = 0x00281050, WindowWidth = 0x00281051, RescaleIntercept = 0x00281052, RescaleSlope = 0x00281053, RescaleType = 0x00281054;
        public const uint RedPaletteDescriptor = 0x00281101, GreenPaletteDescriptor = 0x00281102, BluePaletteDescriptor = 0x00281103;
        public const uint RedPaletteData = 0x00281201, GreenPaletteData = 0x00281202, BluePaletteData = 0x00281203;
        public const uint SegmentedRedPalette = 0x00281221;
        public const uint ModalityLutSequence = 0x00283000, LutDescriptor = 0x00283002, LutData = 0x00283006;
        public const uint PixelMeasuresSequence = 0x00289110, PixelValueTransformationSequence = 0x00289145;
        public const uint PlanePositionSequence = 0x00209113, PlaneOrientationSequence = 0x00209116;
        public const uint SharedFunctionalGroups = 0x52009229, PerFrameFunctionalGroups = 0x52009230;
        public const uint GridFrameOffsetVector = 0x3004000C, DoseGridScaling = 0x3004000E, DoseUnits = 0x30040002;
        public const uint DirectoryRecordSequence = 0x00041220, FirstRootRecordOffset = 0x00041200, NextRecordOffset = 0x00041400;
        public const uint LowerLevelRecordOffset = 0x00041420, DirectoryRecordType = 0x00041430, ReferencedFileId = 0x00041500;
        public const uint ExtendedOffsetTable = 0x7FE00001, ExtendedOffsetTableLengths = 0x7FE00002;
        public const uint FloatPixelData = 0x7FE00008, DoubleFloatPixelData = 0x7FE00009, PixelData = 0x7FE00010;
        public const uint Item = 0xFFFEE000, ItemDelimitation = 0xFFFEE00D, SequenceDelimitation = 0xFFFEE0DD;
    }

    /// <summary>A small part of the PS3.6 data dictionary: keyword and VR of common tags (implicit VR files and header listings).</summary>
    internal static class DicomDictionary
    {
        static readonly Dictionary<uint, (string Keyword, string VR)> s_entries = new()
        {
            [0x00020000] = ("FileMetaInformationGroupLength", "UL"), [0x00020001] = ("FileMetaInformationVersion", "OB"),
            [0x00020002] = ("MediaStorageSOPClassUID", "UI"), [0x00020003] = ("MediaStorageSOPInstanceUID", "UI"),
            [0x00020010] = ("TransferSyntaxUID", "UI"), [0x00020012] = ("ImplementationClassUID", "UI"), [0x00020013] = ("ImplementationVersionName", "SH"),
            [0x00041200] = ("OffsetOfTheFirstDirectoryRecordOfTheRootDirectoryEntity", "UL"), [0x00041202] = ("OffsetOfTheLastDirectoryRecordOfTheRootDirectoryEntity", "UL"),
            [0x00041220] = ("DirectoryRecordSequence", "SQ"), [0x00041400] = ("OffsetOfTheNextDirectoryRecord", "UL"), [0x00041410] = ("RecordInUseFlag", "US"),
            [0x00041420] = ("OffsetOfReferencedLowerLevelDirectoryEntity", "UL"), [0x00041430] = ("DirectoryRecordType", "CS"), [0x00041500] = ("ReferencedFileID", "CS"),
            [0x00041510] = ("ReferencedSOPClassUIDInFile", "UI"), [0x00041511] = ("ReferencedSOPInstanceUIDInFile", "UI"), [0x00041512] = ("ReferencedTransferSyntaxUIDInFile", "UI"),
            [0x00080005] = ("SpecificCharacterSet", "CS"), [0x00080008] = ("ImageType", "CS"), [0x00080012] = ("InstanceCreationDate", "DA"), [0x00080013] = ("InstanceCreationTime", "TM"),
            [0x00080016] = ("SOPClassUID", "UI"), [0x00080018] = ("SOPInstanceUID", "UI"), [0x00080020] = ("StudyDate", "DA"), [0x00080021] = ("SeriesDate", "DA"),
            [0x00080022] = ("AcquisitionDate", "DA"), [0x00080023] = ("ContentDate", "DA"), [0x00080030] = ("StudyTime", "TM"), [0x00080031] = ("SeriesTime", "TM"),
            [0x00080032] = ("AcquisitionTime", "TM"), [0x00080033] = ("ContentTime", "TM"), [0x00080050] = ("AccessionNumber", "SH"), [0x00080060] = ("Modality", "CS"),
            [0x00080064] = ("ConversionType", "CS"), [0x00080070] = ("Manufacturer", "LO"), [0x00080080] = ("InstitutionName", "LO"), [0x00080090] = ("ReferringPhysicianName", "PN"),
            [0x00081010] = ("StationName", "SH"), [0x00081030] = ("StudyDescription", "LO"), [0x0008103E] = ("SeriesDescription", "LO"), [0x00081090] = ("ManufacturerModelName", "LO"),
            [0x00100010] = ("PatientName", "PN"), [0x00100020] = ("PatientID", "LO"), [0x00100030] = ("PatientBirthDate", "DA"), [0x00100040] = ("PatientSex", "CS"),
            [0x00101010] = ("PatientAge", "AS"), [0x00101030] = ("PatientWeight", "DS"),
            [0x00180015] = ("BodyPartExamined", "CS"), [0x00180050] = ("SliceThickness", "DS"), [0x00180060] = ("KVP", "DS"), [0x00180088] = ("SpacingBetweenSlices", "DS"),
            [0x00181020] = ("SoftwareVersions", "LO"), [0x00181030] = ("ProtocolName", "LO"), [0x00181150] = ("ExposureTime", "IS"), [0x00181151] = ("XRayTubeCurrent", "IS"),
            [0x00181152] = ("Exposure", "IS"), [0x00181210] = ("ConvolutionKernel", "SH"), [0x00185100] = ("PatientPosition", "CS"),
            [0x00180080] = ("RepetitionTime", "DS"), [0x00180081] = ("EchoTime", "DS"), [0x00180087] = ("MagneticFieldStrength", "DS"),
            [0x0020000D] = ("StudyInstanceUID", "UI"), [0x0020000E] = ("SeriesInstanceUID", "UI"), [0x00200010] = ("StudyID", "SH"), [0x00200011] = ("SeriesNumber", "IS"),
            [0x00200012] = ("AcquisitionNumber", "IS"), [0x00200013] = ("InstanceNumber", "IS"), [0x00200020] = ("PatientOrientation", "CS"),
            [0x00200032] = ("ImagePositionPatient", "DS"), [0x00200037] = ("ImageOrientationPatient", "DS"), [0x00200052] = ("FrameOfReferenceUID", "UI"),
            [0x00200100] = ("TemporalPositionIdentifier", "IS"), [0x00200105] = ("NumberOfTemporalPositions", "IS"), [0x00201040] = ("PositionReferenceIndicator", "LO"),
            [0x00201041] = ("SliceLocation", "DS"), [0x00209113] = ("PlanePositionSequence", "SQ"), [0x00209116] = ("PlaneOrientationSequence", "SQ"),
            [0x00280002] = ("SamplesPerPixel", "US"), [0x00280004] = ("PhotometricInterpretation", "CS"), [0x00280006] = ("PlanarConfiguration", "US"),
            [0x00280008] = ("NumberOfFrames", "IS"), [0x00280009] = ("FrameIncrementPointer", "AT"), [0x00280010] = ("Rows", "US"), [0x00280011] = ("Columns", "US"),
            [0x00280030] = ("PixelSpacing", "DS"), [0x00280034] = ("PixelAspectRatio", "IS"), [0x00280100] = ("BitsAllocated", "US"), [0x00280101] = ("BitsStored", "US"),
            [0x00280102] = ("HighBit", "US"), [0x00280103] = ("PixelRepresentation", "US"), [0x00280106] = ("SmallestImagePixelValue", "US"),
            [0x00280107] = ("LargestImagePixelValue", "US"), [0x00280120] = ("PixelPaddingValue", "US"), [0x00280301] = ("BurnedInAnnotation", "CS"),
            [0x00281050] = ("WindowCenter", "DS"), [0x00281051] = ("WindowWidth", "DS"), [0x00281052] = ("RescaleIntercept", "DS"), [0x00281053] = ("RescaleSlope", "DS"),
            [0x00281054] = ("RescaleType", "LO"), [0x00281101] = ("RedPaletteColorLookupTableDescriptor", "US"), [0x00281102] = ("GreenPaletteColorLookupTableDescriptor", "US"),
            [0x00281103] = ("BluePaletteColorLookupTableDescriptor", "US"), [0x00281201] = ("RedPaletteColorLookupTableData", "OW"),
            [0x00281202] = ("GreenPaletteColorLookupTableData", "OW"), [0x00281203] = ("BluePaletteColorLookupTableData", "OW"),
            [0x00281221] = ("SegmentedRedPaletteColorLookupTableData", "OW"), [0x00281222] = ("SegmentedGreenPaletteColorLookupTableData", "OW"),
            [0x00281223] = ("SegmentedBluePaletteColorLookupTableData", "OW"), [0x00282110] = ("LossyImageCompression", "CS"),
            [0x00282112] = ("LossyImageCompressionRatio", "DS"), [0x00282114] = ("LossyImageCompressionMethod", "CS"),
            [0x00283000] = ("ModalityLUTSequence", "SQ"), [0x00283002] = ("LUTDescriptor", "US"), [0x00283003] = ("LUTExplanation", "LO"),
            [0x00283004] = ("ModalityLUTType", "LO"), [0x00283006] = ("LUTData", "US"),
            [0x00289110] = ("PixelMeasuresSequence", "SQ"), [0x00289145] = ("PixelValueTransformationSequence", "SQ"),
            [0x30040002] = ("DoseUnits", "CS"), [0x30040004] = ("DoseType", "CS"), [0x3004000A] = ("DoseSummationType", "CS"),
            [0x3004000C] = ("GridFrameOffsetVector", "DS"), [0x3004000E] = ("DoseGridScaling", "DS"),
            [0x52009229] = ("SharedFunctionalGroupsSequence", "SQ"), [0x52009230] = ("PerFrameFunctionalGroupsSequence", "SQ"),
            [0x7FE00001] = ("ExtendedOffsetTable", "OV"), [0x7FE00002] = ("ExtendedOffsetTableLengths", "OV"),
            [0x7FE00008] = ("FloatPixelData", "OF"), [0x7FE00009] = ("DoubleFloatPixelData", "OD"), [0x7FE00010] = ("PixelData", "OW"),
        };

        public static string? VR(uint tag)
        {
            if (s_entries.TryGetValue(tag, out var e)) return e.VR;
            if ((tag & 0xFFFF) == 0) return "UL"; // group lengths
            return null;
        }

        public static string? Keyword(uint tag) => s_entries.TryGetValue(tag, out var e) ? e.Keyword : null;
    }

    /// <summary>Parses data sets in any of the uncompressed transfer syntaxes (PS3.5 section 7).</summary>
    internal static class DicomParser
    {
        const string Fmt = "DICOM";

        static readonly HashSet<string> s_longVRs = new() { "OB", "OD", "OF", "OL", "OV", "OW", "SQ", "UC", "UN", "UR", "UT" };

        static bool IsVR(byte a, byte b) => a >= 'A' && a <= 'Z' && b >= 'A' && b <= 'Z';

        /// <summary>
        /// Whether the bytes are a DICOM file: the "DICM" prefix after the 128 byte preamble, or (files without preamble or
        /// file meta information, as on old media) a first element of group 0002 or 0008 that parses plausibly.
        /// </summary>
        public static bool IsMatch(ReadOnlySpan<byte> d)
        {
            if (d.Length >= 132 && d[128] == 'D' && d[129] == 'I' && d[130] == 'C' && d[131] == 'M') return true;
            if (d.Length < 12) return false;
            ushort gl = BinaryPrimitives.ReadUInt16LittleEndian(d), gb = BinaryPrimitives.ReadUInt16BigEndian(d);
            bool le = gl is 0x0002 or 0x0008, be = gb == 0x0008;
            if (!le && !be) return false;
            ushort el = le ? BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(2)) : BinaryPrimitives.ReadUInt16BigEndian(d.Slice(2));
            if (el > 0x0100) return false;
            if (IsVR(d[4], d[5]))
            {
                string vr = Encoding.ASCII.GetString(d.Slice(4, 2));
                return s_longVRs.Contains(vr) || vr is "AE" or "AS" or "AT" or "CS" or "DA" or "DS" or "DT" or "FL" or "FD" or "IS" or "LO" or "LT" or "PN" or "SH" or "SL" or "SS" or "ST" or "TM" or "UI" or "UL" or "US";
            }
            // implicit VR little endian: a short, even length
            if (!le) return false;
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(4));
            return len <= 1024 && (len & 1) == 0;
        }

        /// <summary>
        /// Parses a whole file: file meta information (if any), then the data set in its transfer syntax. With
        /// <paramref name="headerOnly"/> it stops at the pixel data.
        /// </summary>
        public static DicomDataset Parse(byte[] file, bool headerOnly)
        {
            var mem = new ReadOnlyMemory<byte>(file);
            var ds = new DicomDataset();
            int pos = 0;
            string? ts = null;
            if (file.Length >= 132 && file[128] == 'D' && file[129] == 'I' && file[130] == 'C' && file[131] == 'M') pos = 132;
            if (file.Length >= pos + 8 && BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(pos)) == 0x0002)
            {
                // file meta information: explicit VR little endian, group 0002 only
                var r = new Reader(mem, pos, file.Length, true, false, ds);
                pos = r.ParseInto(ds, stopGroupAfter: 0x0002, headerOnly: false);
                ts = ds.GetString(DicomTags.TransferSyntax);
            }
            ReadOnlyMemory<byte> body = mem;
            int end = file.Length;
            bool expl, big;
            if (ts == DicomUids.DeflatedExplicitVrLittleEndian)
            {
                // the rest of the file is a raw deflate stream of an explicit VR little endian data set
                var inflated = Zlib.DecompressRaw(file.AsSpan(pos), Math.Max(1 << 16, (file.Length - pos) * 4));
                body = inflated; pos = 0; end = inflated.Length;
                expl = true; big = false;
            }
            else if (ts == null || ts.Length == 0)
            {
                // no (usable) transfer syntax: tell from the first element
                ts = Guess(file.AsSpan(pos, end - pos), out expl, out big);
            }
            else
            {
                expl = ts != DicomUids.ImplicitVrLittleEndian;
                big = ts == DicomUids.ExplicitVrBigEndian;
            }
            ds.TransferSyntax = ts;
            var reader = new Reader(body, pos, end, expl, big, ds);
            reader.ParseInto(ds, stopGroupAfter: -1, headerOnly);
            return ds;
        }

        static string Guess(ReadOnlySpan<byte> d, out bool expl, out bool big)
        {
            big = d.Length >= 2 && BinaryPrimitives.ReadUInt16LittleEndian(d) > 0x00FF && BinaryPrimitives.ReadUInt16BigEndian(d) <= 0x00FF;
            expl = d.Length >= 6 && IsVR(d[4], d[5]);
            return big ? DicomUids.ExplicitVrBigEndian : expl ? DicomUids.ExplicitVrLittleEndian : DicomUids.ImplicitVrLittleEndian;
        }

        /// <summary>Element reader over one byte range with a fixed transfer syntax.</summary>
        sealed class Reader
        {
            readonly ReadOnlyMemory<byte> _mem;
            readonly bool _explicit, _big;
            readonly DicomDataset _root;
            int _pos;
            readonly int _end;

            public Reader(ReadOnlyMemory<byte> mem, int pos, int end, bool expl, bool big, DicomDataset root)
            {
                _mem = mem; _pos = pos; _end = end; _explicit = expl; _big = big; _root = root;
            }

            ReadOnlySpan<byte> Span => _mem.Span;

            ushort U16(int at) => _big ? BinaryPrimitives.ReadUInt16BigEndian(Span.Slice(at)) : BinaryPrimitives.ReadUInt16LittleEndian(Span.Slice(at));
            uint U32(int at) => _big ? BinaryPrimitives.ReadUInt32BigEndian(Span.Slice(at)) : BinaryPrimitives.ReadUInt32LittleEndian(Span.Slice(at));

            void Need(int n)
            {
                if (_pos + n > _end) throw new ImageFormatException($"{Fmt}: data ends early");
            }

            /// <summary>
            /// Reads elements into <paramref name="ds"/> until the end of the range or an item delimiter; with a stop group,
            /// before the first element of a larger group. Returns the position after the last element read.
            /// </summary>
            public int ParseInto(DicomDataset ds, int stopGroupAfter, bool headerOnly)
            {
                while (_pos < _end)
                {
                    if (_pos + 8 > _end)
                    {
                        // trailing padding shorter than an element header
                        if (Span.Slice(_pos, _end - _pos).IndexOfAnyExcept((byte)0) < 0) { _pos = _end; break; }
                        throw new ImageFormatException($"{Fmt}: data ends early");
                    }
                    ushort group = U16(_pos);
                    if (stopGroupAfter >= 0 && group > stopGroupAfter) break;
                    uint tag = (uint)group << 16 | U16(_pos + 2);
                    if (tag == DicomTags.ItemDelimitation) { _pos += 8; return _pos; }
                    if (tag == DicomTags.SequenceDelimitation) { _pos += 8; continue; } // stray delimiter
                    if (headerOnly && tag >= DicomTags.FloatPixelData && tag <= DicomTags.PixelData) { _root.ReachedPixels = true; break; }
                    ReadElement(ds, tag);
                    if (tag == DicomTags.SpecificCharacterSet) ds.CharacterSet = CharacterSet(ds.GetStrings(tag));
                }
                return _pos;
            }

            void ReadElement(DicomDataset ds, uint tag)
            {
                string vr;
                uint len;
                bool explicitHere = _explicit && IsVR(Span[_pos + 4], Span[_pos + 5]);
                if (explicitHere)
                {
                    vr = Encoding.ASCII.GetString(Span.Slice(_pos + 4, 2));
                    if (s_longVRs.Contains(vr))
                    {
                        Need(12);
                        len = U32(_pos + 8);
                        _pos += 12;
                    }
                    else
                    {
                        len = U16(_pos + 6);
                        _pos += 8;
                    }
                }
                else
                {
                    vr = DicomDictionary.VR(tag) ?? "UN";
                    len = U32(_pos + 4);
                    _pos += 8;
                }
                if (len == 0xFFFFFFFF)
                {
                    if (tag == DicomTags.PixelData) { ReadFragments(ds, tag, vr); return; }
                    // a sequence; UN of undefined length holds implicit VR little endian items
                    bool asImplicit = vr == "UN";
                    var items = asImplicit ? new Reader(_mem, _pos, _end, false, false, _root).ReadItems(ds, -1, out int p) : ReadItems(ds, -1, out p);
                    _pos = p;
                    ds.Add(new DicomElement(ds, tag, vr == "UN" ? "SQ" : vr, default, _big, items));
                    return;
                }
                if (_pos + len > _end)
                {
                    // a truncated last element (cut files): keep what is there
                    if (tag >= DicomTags.FloatPixelData && tag <= DicomTags.PixelData) len = (uint)(_end - _pos);
                    else throw new ImageFormatException($"{Fmt}: element ({tag >> 16:X4},{tag & 0xFFFF:X4}) runs past the end");
                }
                int vlen = (int)len;
                if (vr == "SQ" || (!explicitHere && LooksLikeItems(vlen)))
                {
                    int end = _pos + vlen;
                    var items = ReadItems(ds, end, out _);
                    _pos = end;
                    ds.Add(new DicomElement(ds, tag, "SQ", default, _big, items));
                    return;
                }
                ds.Add(new DicomElement(ds, tag, vr, _mem.Slice(_pos, vlen), _big));
                _pos += vlen;
            }

            // implicit VR: a defined length value that starts with an item tag is a sequence
            bool LooksLikeItems(int len)
                => len >= 8 && U16(_pos) == 0xFFFE && U16(_pos + 2) == 0xE000 && (U32(_pos + 4) == 0xFFFFFFFF || U32(_pos + 4) <= len - 8);

            /// <summary>Items up to <paramref name="end"/> (defined length) or a sequence delimiter (-1).</summary>
            List<DicomDataset> ReadItems(DicomDataset parent, int end, out int after)
            {
                var items = new List<DicomDataset>();
                int limit = end < 0 ? _end : end;
                while (_pos + 8 <= limit)
                {
                    uint tag = (uint)U16(_pos) << 16 | U16(_pos + 2);
                    uint len = U32(_pos + 4);
                    if (tag == DicomTags.SequenceDelimitation) { _pos += 8; break; }
                    if (tag != DicomTags.Item) throw new ImageFormatException($"{Fmt}: expected a sequence item");
                    var item = new DicomDataset { Parent = parent, Offset = _pos, TransferSyntax = _root.TransferSyntax };
                    _pos += 8;
                    if (len == 0xFFFFFFFF)
                    {
                        var r = new Reader(_mem, _pos, limit, _explicit, _big, _root);
                        _pos = r.ParseInto(item, -1, false);
                    }
                    else
                    {
                        if (_pos + len > limit) throw new ImageFormatException($"{Fmt}: sequence item runs past its end");
                        var r = new Reader(_mem, _pos, _pos + (int)len, _explicit, _big, _root);
                        r.ParseInto(item, -1, false);
                        _pos += (int)len;
                    }
                    items.Add(item);
                }
                after = end < 0 ? _pos : end;
                return items;
            }

            void ReadFragments(DicomDataset ds, uint tag, string vr)
            {
                // encapsulated pixel data: always little endian items (basic offset table first), then a sequence delimiter
                var frags = new List<ReadOnlyMemory<byte>>();
                var s = Span;
                while (_pos + 8 <= _end)
                {
                    uint t = (uint)BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(_pos)) << 16 | BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(_pos + 2));
                    uint len = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(_pos + 4));
                    _pos += 8;
                    if (t == DicomTags.SequenceDelimitation) break;
                    if (t != DicomTags.Item) throw new ImageFormatException($"{Fmt}: bad encapsulated pixel data");
                    if (len == 0xFFFFFFFF || _pos + len > _end)
                    {
                        // a cut last fragment
                        frags.Add(_mem.Slice(_pos, _end - _pos));
                        _pos = _end;
                        break;
                    }
                    frags.Add(_mem.Slice(_pos, (int)len));
                    _pos += (int)len;
                }
                ds.Add(new DicomElement(ds, tag, vr, default, false, fragments: frags));
            }
        }

        /// <summary>The text encoding of a Specific Character Set value (the first single byte one wins; ISO 2022 extensions read as Latin-1).</summary>
        static Encoding? CharacterSet(string[]? terms)
        {
            if (terms == null) return null;
            foreach (var t in terms)
                switch (t.Trim().ToUpperInvariant())
                {
                    case "ISO_IR 192": return Encoding.UTF8;
                    case "ISO_IR 100" or "ISO 2022 IR 100": return Encoding.Latin1;
                    case "" or "ISO_IR 6" or "ISO 2022 IR 6": continue;
                }
            return Encoding.Latin1;
        }
    }
}
