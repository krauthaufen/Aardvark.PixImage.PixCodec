using System;
using System.Buffers.Binary;
using Aardvark.ImageCodecs.JpegXl;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// The HDR gain map of a JPEG XL file (ISO/IEC 18181-2:2026 jhgm box): ISO 21496-1 metadata, the colour space of the
    /// alternate image and the gain map itself, a JPEG XL file of its own. The image <see cref="Jxl.Decode(ReadOnlySpan{byte}, JxlDecodeOptions)"/> returns is
    /// the baseline; the alternate image is reconstructed from it, the gain map and the metadata as ISO 21496-1 says.
    /// </summary>
    public sealed class JxlGainMap
    {
        /// <summary>Version of the box (0).</summary>
        public int Version { get; internal set; }
        /// <summary>The ISO 21496-1 gain map metadata, as stored.</summary>
        public byte[] Metadata { get; internal set; } = Array.Empty<byte>();
        /// <summary>Whether the alternate image has a colour space of its own (otherwise it is the baseline's).</summary>
        public bool HasAlternateColourSpace { get; internal set; }
        /// <summary>Transfer function of the alternate image's colour encoding, when it has one and it is not an ICC profile.</summary>
        public TransferFunction AlternateTransfer { get; internal set; } = TransferFunction.Unknown;
        /// <summary>
        /// ICC profile of the alternate image: the stored one, or one built from its colour encoding where that needs one
        /// (as for <see cref="ImageMetadata.IccProfile"/> of decoded images); null otherwise.
        /// </summary>
        public byte[]? AlternateIcc { get; internal set; }
        /// <summary>The gain map, a JPEG XL file (codestream or container).</summary>
        public byte[] Image { get; internal set; } = Array.Empty<byte>();

        /// <summary>Decodes the gain map image.</summary>
        public RawImage DecodeImage(JxlDecodeOptions? options = null) => Jxl.Decode(Image, options);

        /// <summary>
        /// <see cref="Metadata"/> parsed (ISO 21496-1 C.2); null for an unknown minimum_version, a truncated payload or
        /// values clause 5 does not allow.
        /// </summary>
        public GainMapMetadata? ParsedMetadata
        {
            get
            {
                try { return GainMapMetadata.FromIso21496(Metadata); }
                catch (ImageFormatException) { return null; }
            }
        }

        /// <summary>
        /// The gain map in the format independent form that <see cref="GainMap.Apply(RawImage, GainMap, double?)"/> takes
        /// (the same one JPEG files give): metadata, decoded image, alternate colour space. Null when the metadata is unusable.
        /// </summary>
        public GainMap? ToGainMap(JxlDecodeOptions? options = null)
        {
            var m = ParsedMetadata;
            if (m == null) return null;
            var img = DecodeImage(options);
            img.Metadata = null;
            return new GainMap { Metadata = m, Image = img, AlternateIcc = HasAlternateColourSpace ? AlternateIcc : null, Source = GainMapSource.Iso21496 };
        }

        /// <summary>Parses the content of a jhgm box; null for a version this reader does not know.</summary>
        internal static JxlGainMap? Parse(byte[] box)
        {
            if (box.Length < 1) throw new ImageFormatException("JPEG XL: empty gain map box");
            if (box[0] != 0) return null;
            int p = 1;
            void Need(long n) { if (p + n > box.Length) throw new ImageFormatException("JPEG XL: truncated gain map box"); }
            Need(2);
            int metaSize = BinaryPrimitives.ReadUInt16BigEndian(box.AsSpan(p));
            p += 2;
            Need(metaSize);
            var g = new JxlGainMap { Version = 0, Metadata = box.AsSpan(p, metaSize).ToArray() };
            p += metaSize;
            Need(1);
            int ceSize = box[p++];
            JxlColourEncoding? ce = null;
            if (ceSize > 0)
            {
                Need(ceSize);
                var br = new JxlBitReader(box.AsSpan(p, ceSize).ToArray());
                ce = JxlColourEncoding.Read(br);
                br.CheckEnd();
                p += ceSize;
            }
            Need(4);
            uint iccSize = BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(p));
            p += 4;
            byte[]? icc = null;
            if (iccSize > 0)
            {
                Need(iccSize);
                icc = JxlIcc.Read(new JxlBitReader(box.AsSpan(p, (int)iccSize).ToArray()));
                p += (int)iccSize;
            }
            if (ce != null)
            {
                g.HasAlternateColourSpace = true;
                g.AlternateTransfer = ce.WantIcc ? TransferFunction.Unknown : Jxl.TransferOf(ce, out _);
                g.AlternateIcc = icc ?? JxlColour.DescribingIcc(ce);
            }
            else if (icc != null)
            {
                g.HasAlternateColourSpace = true;
                g.AlternateIcc = icc;
            }
            g.Image = box.AsSpan(p).ToArray();
            if (!JxlContainer.IsMatch(g.Image)) throw new ImageFormatException("JPEG XL: gain map box without a JPEG XL image");
            return g;
        }
    }
}
