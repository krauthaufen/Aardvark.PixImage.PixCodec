using System;
using System.Collections.Generic;
using System.IO;

namespace Aardvark.ImageCodecs
{
    /// <summary>Format independent entry points: sniff the format, then dispatch to the codec.</summary>
    public static partial class ImageCodec
    {
        public static ImageFormat DetectFormat(ReadOnlySpan<byte> header)
        {
            if (Png.IsMatch(header)) return ImageFormat.Png;
            if (JpegLs.IsMatch(header)) return ImageFormat.JpegLs;   // FF D8 like JPEG: look for its SOF55 first
            if (Jpeg.IsMatch(header)) return ImageFormat.Jpeg;
            if (Gif.IsMatch(header)) return ImageFormat.Gif;
            if (Tiff.IsMatch(header)) return Dng.IsMatch(header) ? ImageFormat.Dng : ImageFormat.Tiff;
            if (JpegXr.IsMatch(header)) return ImageFormat.JpegXr;
            if (Exr.IsMatch(header)) return ImageFormat.Exr;
            if (Webp.IsMatch(header)) return ImageFormat.Webp;
            if (Jp2.IsMatch(header)) return ImageFormat.Jp2;
            if (Jxl.IsMatch(header)) return ImageFormat.Jxl;
            if (Avif.IsMatch(header)) return ImageFormat.Avif;
            if (Bmp.IsMatch(header)) return ImageFormat.Bmp;
            if (Dds.IsMatch(header)) return ImageFormat.Dds;
            if (Ktx.IsMatch(header)) return ImageFormat.Ktx;
            if (Nrrd.IsMatch(header)) return ImageFormat.Nrrd;
            if (Fits.IsMatch(header)) return ImageFormat.Fits;
            if (Nifti.IsMatch(header)) return ImageFormat.Nifti;
            if (Icns.IsMatch(header)) return ImageFormat.Icns;
            if (Ani.IsMatch(header)) return ImageFormat.Ani;
            if (Astc.IsMatch(header)) return ImageFormat.Astc;
            if (Pvr.IsMatch(header)) return ImageFormat.Pvr;
            if (Qoi.IsMatch(header)) return ImageFormat.Qoi;
            if (Sgi.IsMatch(header)) return ImageFormat.Sgi;
            if (SunRaster.IsMatch(header)) return ImageFormat.SunRaster;
            if (Pcx.IsDcx(header)) return ImageFormat.Dcx;
            if (Dpx.IsMatch(header)) return ImageFormat.Dpx;
            if (Cineon.IsMatch(header)) return ImageFormat.Cineon;
            if (Hdr.IsMatch(header)) return ImageFormat.Hdr;
            if (Pfm.IsMatch(header)) return ImageFormat.Pfm;
            if (Pnm.IsMatch(header)) return ImageFormat.Pnm;
            if (Ico.IsMatch(header)) return ImageFormat.Ico;
            if (MetaImage.IsMatch(header)) return ImageFormat.MetaImage; // text header without a signature
            if (Nitf.IsMatch(header)) return ImageFormat.Nitf;
            if (Envi.IsMatch(header)) return ImageFormat.Envi;            // the "ENVI" line of a header (.hdr)
            if (Dicom.IsMatch(header)) return ImageFormat.Dicom;            // "DICM" after a free preamble, or a bare data set
            // no signatures, strict plausibility checks
            if (Pcx.IsMatch(header)) return ImageFormat.Pcx;
            if (Xbm.IsMatch(header)) return ImageFormat.Xbm;
            if (Xpm.IsMatch(header)) return ImageFormat.Xpm;
            if (Wbmp.IsMatch(header)) return ImageFormat.Wbmp;   // only complete files: their length must fit the header
            if (Tga.IsMatch(header)) return ImageFormat.Tga;   // no signature: last
            return ImageFormat.Unknown;
        }

        public static ImageFormat FormatFromExtension(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".gz")
                return Path.GetExtension(Path.GetFileNameWithoutExtension(path)).ToLowerInvariant() switch
                {
                    ".nii" => ImageFormat.Nifti,
                    ".fits" or ".fit" or ".fts" => ImageFormat.Fits,
                    _ => ImageFormat.Unknown,
                };
            return ext switch
            {
                ".png" => ImageFormat.Png,
                ".jpg" or ".jpeg" or ".jpe" or ".jfif" or ".mpo" => ImageFormat.Jpeg,
                ".tif" or ".tiff" => ImageFormat.Tiff,
                ".dng" => ImageFormat.Dng,
                ".gif" => ImageFormat.Gif,
                ".exr" => ImageFormat.Exr,
                ".webp" => ImageFormat.Webp,
                ".jp2" or ".j2k" or ".j2c" or ".jpc" or ".jpf" or ".jpx" or ".jph" or ".jhc" => ImageFormat.Jp2,
                ".bmp" or ".dib" => ImageFormat.Bmp,
                ".dds" => ImageFormat.Dds,
                ".tga" or ".targa" or ".icb" or ".vda" or ".vst" => ImageFormat.Tga,
                ".hdr" or ".rgbe" or ".pic" => ImageFormat.Hdr,
                ".pbm" or ".pgm" or ".ppm" or ".pnm" or ".pam" => ImageFormat.Pnm,
                ".pfm" => ImageFormat.Pfm,
                ".qoi" => ImageFormat.Qoi,
                ".ico" or ".cur" => ImageFormat.Ico,
                ".ktx" or ".ktx2" => ImageFormat.Ktx,
                ".pcx" or ".pcc" => ImageFormat.Pcx,
                ".dcx" => ImageFormat.Dcx,
                ".sgi" or ".rgb" or ".rgba" or ".bw" or ".int" or ".inta" => ImageFormat.Sgi,
                ".ras" or ".sun" or ".rs" or ".im1" or ".im8" or ".im24" or ".im32" => ImageFormat.SunRaster,
                ".xbm" or ".bm" => ImageFormat.Xbm,
                ".xpm" or ".pm" => ImageFormat.Xpm,
                ".wbmp" or ".wbm" => ImageFormat.Wbmp,
                ".nrrd" or ".nhdr" => ImageFormat.Nrrd,
                ".nii" => ImageFormat.Nifti,
                ".mha" or ".mhd" => ImageFormat.MetaImage,
                ".fits" or ".fit" or ".fts" => ImageFormat.Fits,
                ".icns" => ImageFormat.Icns,
                ".ani" => ImageFormat.Ani,
                ".astc" => ImageFormat.Astc,
                ".pvr" => ImageFormat.Pvr,
                ".dpx" => ImageFormat.Dpx,
                ".cin" or ".cineon" => ImageFormat.Cineon,
                ".jls" => ImageFormat.JpegLs,
                ".jxl" => ImageFormat.Jxl,
                ".jxr" or ".wdp" or ".hdp" => ImageFormat.JpegXr,
                ".avif" or ".avifs" => ImageFormat.Avif,
                ".dcm" or ".dicom" or ".dic" => ImageFormat.Dicom,
                ".ntf" or ".nitf" or ".nsf" => ImageFormat.Nitf,
                _ => ImageFormat.Unknown,
            };
        }

        /// <summary>
        /// Reads format, dimensions, channels, sample type, frame count and the descriptive metadata
        /// (<see cref="ImageInfo.Metadata"/>) of an encoded image without decoding its pixels.
        /// </summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => HeaderInfo(data) with { Metadata = TryReadMetadata(data) };

        static ImageInfo HeaderInfo(ReadOnlySpan<byte> data) => DetectFormat(data) switch
        {
            ImageFormat.Png => Png.GetInfo(data),
            ImageFormat.Jpeg => Jpeg.GetInfo(data),
            ImageFormat.Gif => Gif.GetInfo(data),
            ImageFormat.Tiff => Tiff.GetInfo(data),
            ImageFormat.Dng => Dng.GetInfo(data),
            ImageFormat.Exr => Exr.GetInfo(data),
            ImageFormat.Webp => Webp.GetInfo(data),
            ImageFormat.Jp2 => Jp2.GetInfo(data),
            ImageFormat.Bmp => Bmp.GetInfo(data),
            ImageFormat.Tga => Tga.GetInfo(data),
            ImageFormat.Dds => Dds.GetInfo(data),
            ImageFormat.Hdr => Hdr.GetInfo(data),
            ImageFormat.Pnm => Pnm.GetInfo(data),
            ImageFormat.Pfm => Pfm.GetInfo(data),
            ImageFormat.Qoi => Qoi.GetInfo(data),
            ImageFormat.Ico => Ico.GetInfo(data),
            ImageFormat.Ktx => Ktx.GetInfo(data),
            ImageFormat.Pcx or ImageFormat.Dcx => Pcx.GetInfo(data),
            ImageFormat.Sgi => Sgi.GetInfo(data),
            ImageFormat.SunRaster => SunRaster.GetInfo(data),
            ImageFormat.Xbm => Xbm.GetInfo(data),
            ImageFormat.Xpm => Xpm.GetInfo(data),
            ImageFormat.Wbmp => Wbmp.GetInfo(data),
            ImageFormat.Nrrd => Nrrd.GetInfo(data),
            ImageFormat.Nifti => Nifti.GetInfo(data),
            ImageFormat.MetaImage => MetaImage.GetInfo(data),
            ImageFormat.Fits => Fits.GetInfo(data),
            ImageFormat.Envi => Envi.GetInfo(data),
            ImageFormat.Icns => Icns.GetInfo(data),
            ImageFormat.Ani => Ani.GetInfo(data),
            ImageFormat.Astc => Astc.GetInfo(data),
            ImageFormat.Pvr => Pvr.GetInfo(data),
            ImageFormat.Dpx => Dpx.GetInfo(data),
            ImageFormat.Cineon => Cineon.GetInfo(data),
            ImageFormat.JpegLs => JpegLs.GetInfo(data),
            ImageFormat.Jxl => Jxl.GetInfo(data),
            ImageFormat.JpegXr => JpegXr.GetInfo(data),
            ImageFormat.Avif => Avif.GetInfo(data),
            ImageFormat.Dicom => Dicom.GetInfo(data),
            ImageFormat.Nitf => Nitf.GetInfo(data),
            _ => throw new ImageFormatException("unknown image format"),
        };

        /// <summary>Header info from a prefix of the file (stream path): text headers must not be parsed from a cut line.</summary>
        static ImageInfo HeaderInfoPrefix(ReadOnlySpan<byte> data, ImageFormat format) => format switch
        {
            ImageFormat.Nrrd => Nrrd.GetInfo(data, true),
            ImageFormat.MetaImage => MetaImage.GetInfo(data, true),
            _ => HeaderInfo(data),
        };

        /// <summary>Metadata problems never make the header information unreadable.</summary>
        static PhotoMetadata? TryReadMetadata(ReadOnlySpan<byte> data)
        {
            try { return MetadataReader.Read(data); }
            catch (Exception e) when (e is ImageFormatException or ImageNotSupportedException or ArgumentException or IndexOutOfRangeException
                                       or InvalidOperationException or OverflowException or System.Xml.XmlException) { return null; }
        }

        /// <summary>
        /// Reads format, dimensions, channels, sample type, frame count and the descriptive metadata of an image file without
        /// decoding it. The file is memory mapped, so only the parts holding headers and metadata are read from disk (GIF frame
        /// counting and run length coded TGA / BMP walk all data).
        /// </summary>
        public static unsafe ImageInfo GetInfo(string path)
        {
            long length = new FileInfo(path).Length;
            if (length == 0) throw new ImageFormatException("empty file");
            if (SniffFile(path) == ImageFormat.Envi) return Envi.GetInfo(path);   // header and samples are two files
            if (length > int.MaxValue)
            {
                // beyond what a span can address: headers through the stream, metadata from the first 2 GB
                ImageInfo info;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.None))
                    info = GetInfo(fs, 8192, false);
                return info with { Metadata = Mapped(path, int.MaxValue, d => TryReadMetadata(d)) };
            }
            return Mapped(path, length, d => GetInfo(d));
        }

        internal delegate T SpanFunc<T>(ReadOnlySpan<byte> data);

        static unsafe T Mapped<T>(string path, long length, SpanFunc<T> f)
        {
            using var mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
            using var view = mmf.CreateViewAccessor(0, length, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            try { return f(new ReadOnlySpan<byte>(p + view.PointerOffset, (int)length)); }
            finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
        }

        /// <summary>
        /// Like <see cref="GetInfo(string)"/> for a stream positioned at the start of the image (the position afterwards is
        /// unspecified). Works on non-seekable streams. Pixel data is not read: file streams are memory mapped, other
        /// seekable streams are walked by seeking past the image data (PNG, WebP, JPEG 2000 chunks, TIFF directories). From
        /// non-seekable streams the metadata comes from the part read for the headers (for JPEG, up to the scan); metadata
        /// stored after the pixels (possible in PNG, WebP) is then missed.
        /// </summary>
        public static ImageInfo GetInfo(Stream stream) => GetInfo(stream, 8192, true);

        /// <summary>
        /// Reads the descriptive metadata (EXIF, XMP, IPTC, ICC, comments and text chunks, OpenEXR attributes) and the
        /// common values taken from them: camera, capture settings, dates, GPS, drone attitude, description. Pixels are not
        /// decoded. Damaged blocks are skipped.
        /// </summary>
        public static PhotoMetadata ReadMetadata(ReadOnlySpan<byte> data) => MetadataReader.Read(data);

        /// <inheritdoc cref="ReadMetadata(ReadOnlySpan{byte})"/>
        /// <remarks>The file is memory mapped: only the parts holding metadata are read from disk.</remarks>
        public static PhotoMetadata ReadMetadata(string path)
        {
            long length = new FileInfo(path).Length;
            if (length == 0) throw new ImageFormatException("empty file");
            return Mapped(path, Math.Min(length, int.MaxValue), d => MetadataReader.Read(d));   // (metadata sits in the first 2 GB)
        }

        /// <inheritdoc cref="ReadMetadata(ReadOnlySpan{byte})"/>
        public static PhotoMetadata ReadMetadata(Stream stream) => WithData(stream, d => ReadMetadata(d));

        /// <summary>Returns false instead of throwing when the file is missing, unreadable or not a supported image.</summary>
        public static bool TryGetInfo(string path, out ImageInfo info)
        {
            try { info = GetInfo(path); return true; }
            catch (Exception e) when (e is ImageFormatException or ImageNotSupportedException or IOException or UnauthorizedAccessException)
            {
                info = default;
                return false;
            }
        }

        internal static ImageInfo GetInfo(Stream stream, int initialSize) => GetInfo(stream, initialSize, true);

        static ImageInfo GetInfo(Stream stream, int initialSize, bool metadata)
        {
            long start = stream.CanSeek ? stream.Position : -1;
            var info = HeaderInfo(stream, initialSize, out var prefix);
            if (!metadata) return info;
            try
            {
                if (stream is FileStream fs && start >= 0 && fs.Length - start > 0)
                {
                    // memory mapped: only the pages holding metadata are read
                    long len = Math.Min(fs.Length - start, int.MaxValue);
                    using var mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(fs, null, 0,
                        System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
                    using var view = mmf.CreateViewAccessor(start, len, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
                    unsafe
                    {
                        byte* p = null;
                        view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                        try { return info with { Metadata = TryReadMetadata(new ReadOnlySpan<byte>(p + view.PointerOffset, (int)len)) }; }
                        finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
                    }
                }
                if (stream is MemoryStream ms && start >= 0 && ms.TryGetBuffer(out var seg))
                    return info with { Metadata = TryReadMetadata(seg.AsSpan((int)start)) };
                return info with { Metadata = TryReadMetadata(StreamMetadata.Collect(stream, start, info.Format, prefix)) };
            }
            catch (IOException) { return info with { Metadata = TryReadMetadata(prefix) }; }
            catch (Exception e) when (e is NotSupportedException or ImageNotSupportedException) { return info with { Metadata = TryReadMetadata(prefix) }; }
        }

        static ImageInfo HeaderInfo(Stream stream, int initialSize, out byte[] prefix)
        {
            prefix = Array.Empty<byte>();
            var buf = new byte[Math.Max(initialSize, 64)];
            int n = Fill(stream, buf, 0);
            bool eof = n < buf.Length;
            var format = DetectFormat(buf.AsSpan(0, n));
            if (format == ImageFormat.Unknown && !eof)
            {
                // WBMP is only recognized from the complete file; give up on streams too long for a plausible one
                // (a WBMP header starts with two zero bytes) instead of reading arbitrary data to the end
                if (buf[0] != 0 || buf[1] != 0) throw new ImageFormatException("unknown image format");
                const int maxWbmp = 64 << 20;
                while (!eof && buf.Length < maxWbmp)
                {
                    Array.Resize(ref buf, Math.Min(buf.Length * 4, maxWbmp));
                    int got = Fill(stream, buf, n);
                    eof = n + got < buf.Length;
                    n += got;
                }
                if (!eof) throw new ImageFormatException("unknown image format");
                format = DetectFormat(buf.AsSpan(0, n));
            }
            if (format == ImageFormat.Unknown) throw new ImageFormatException("unknown image format");
            if (format is ImageFormat.Tiff or ImageFormat.Dng && stream.CanSeek)
            {
                prefix = buf.AsSpan(0, n).ToArray();
                stream.Seek(-n, SeekOrigin.Current);
                // (the prefix may end before the first directory, so DNG is checked on the stream)
                return Dng.IsMatch(stream) ? Dng.GetInfo(stream) : Tiff.GetInfo(stream);
            }
            // TIFF directories can be anywhere, GIF frames have to be counted, the TGA footer and the PCX palette are at the
            // end and BMP run codes have to be walked: these need all the data. The others are parsed from a growing prefix; their
            // header parsers throw ImageFormatException when the prefix ends inside the headers, which is the signal
            // to read more.
            bool needAll = format is ImageFormat.Envi or ImageFormat.Tiff or ImageFormat.Dng or ImageFormat.Gif or ImageFormat.Tga or ImageFormat.Ico or ImageFormat.Pcx or ImageFormat.Dcx or ImageFormat.Icns or ImageFormat.Ani or ImageFormat.Astc
                || format == ImageFormat.Bmp && Bmp.InfoNeedsAllData(buf.AsSpan(0, n));
            while (true)
            {
                if (eof) { prefix = buf.AsSpan(0, n).ToArray(); return HeaderInfo(buf.AsSpan(0, n)); }
                if (!needAll && (format != ImageFormat.Png || PngHeadersComplete(buf.AsSpan(0, n)))
                    && (format != ImageFormat.Bmp || n >= Math.Min(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(10)), 1u << 20)))
                {
                    try { var r = HeaderInfoPrefix(buf.AsSpan(0, n), format); prefix = buf.AsSpan(0, n).ToArray(); return r; }
                    catch (ImageFormatException) { }
                }
                Array.Resize(ref buf, (int)Math.Min((long)buf.Length * 4, Array.MaxLength));
                int got = Fill(stream, buf, n);
                eof = n + got < buf.Length;
                n += got;
                if (!eof && n == Array.MaxLength) throw new ImageNotSupportedException("image headers exceed 2 GB");
            }
        }

        /// <summary>
        /// PNG's header parser stops quietly at the end of its input, so decide here whether the prefix reaches the
        /// first IDAT (everything that affects the info - tRNS, acTL - has to precede it).
        /// </summary>
        static bool PngHeadersComplete(ReadOnlySpan<byte> prefix)
        {
            long pos = 8;
            while (pos + 8 <= prefix.Length)
            {
                var head = prefix.Slice((int)pos, 8);
                uint length = (uint)(head[0] << 24 | head[1] << 16 | head[2] << 8 | head[3]);
                if (head.Slice(4).SequenceEqual("IDAT"u8) || head.Slice(4).SequenceEqual("IEND"u8)) return true;
                pos += 12L + length;
            }
            return false;
        }

        static int Fill(Stream stream, byte[] buffer, int offset)
        {
            int total = 0;
            while (offset + total < buffer.Length)
            {
                int r = stream.Read(buffer, offset + total, buffer.Length - offset - total);
                if (r <= 0) break;
                total += r;
            }
            return total;
        }

        public static RawImage Decode(ReadOnlySpan<byte> data) => ColorTags.Tag(DecodeCore(data), data, DetectFormat(data));

        /// <summary>
        /// The HDR rendition of an image with a gain map (JPEG: Ultra HDR / ISO 21496-1, see <see cref="Jpeg.DecodeHdr"/>;
        /// JPEG XL: jhgm box, see <see cref="Jxl.DecodeHdr"/>) as linear Float32 RGB(A) where 1.0 is SDR white, for a display
        /// with HDR headroom <paramref name="headroom"/> (log2 of peak over SDR white; null = the full alternate rendition).
        /// Every other image (or one without a usable gain map) is returned converted to linear light in the same form.
        /// </summary>
        public static RawImage DecodeHdr(ReadOnlySpan<byte> data, double? headroom = null) => DetectFormat(data) switch
        {
            ImageFormat.Jpeg => Jpeg.DecodeHdr(data, headroom),
            ImageFormat.Jxl => Jxl.DecodeHdr(data, headroom),
            _ => GainMap.LinearRgb(Decode(data)),
        };

        /// <inheritdoc cref="DecodeHdr(ReadOnlySpan{byte}, double?)"/>
        public static RawImage DecodeHdr(Stream stream, double? headroom = null) => WithData(stream, d => DecodeHdr(d, headroom));

        /// <inheritdoc cref="DecodeHdr(ReadOnlySpan{byte}, double?)"/>
        public static RawImage DecodeHdr(string path, double? headroom = null) => WithFile(path, d => DecodeHdr(d, headroom));

        static RawImage DecodeCore(ReadOnlySpan<byte> data) => DetectFormat(data) switch
        {
            ImageFormat.Png => Png.Decode(data),
            ImageFormat.Jpeg => Jpeg.Decode(data),
            ImageFormat.Gif => Gif.Decode(data),
            ImageFormat.Tiff => Tiff.Decode(data),
            ImageFormat.Dng => Dng.Decode(data),
            ImageFormat.Exr => Exr.Decode(data),
            ImageFormat.Webp => Webp.Decode(data),
            ImageFormat.Jp2 => Jp2.Decode(data),
            ImageFormat.Bmp => Bmp.Decode(data),
            ImageFormat.Tga => Tga.Decode(data),
            ImageFormat.Dds => Dds.Decode(data),
            ImageFormat.Hdr => Hdr.Decode(data),
            ImageFormat.Pnm => Pnm.Decode(data),
            ImageFormat.Pfm => Pfm.Decode(data),
            ImageFormat.Qoi => Qoi.Decode(data),
            ImageFormat.Ico => Ico.Decode(data),
            ImageFormat.Ktx => Ktx.Decode(data),
            ImageFormat.Pcx or ImageFormat.Dcx => Pcx.Decode(data),
            ImageFormat.Sgi => Sgi.Decode(data),
            ImageFormat.SunRaster => SunRaster.Decode(data),
            ImageFormat.Xbm => Xbm.Decode(data),
            ImageFormat.Xpm => Xpm.Decode(data),
            ImageFormat.Wbmp => Wbmp.Decode(data),
            ImageFormat.Nrrd => Nrrd.Decode(data),
            ImageFormat.Nifti => Nifti.Decode(data),
            ImageFormat.MetaImage => MetaImage.Decode(data),
            ImageFormat.Fits => Fits.Decode(data),
            ImageFormat.Envi => Envi.Decode(data),
            ImageFormat.Icns => Icns.Decode(data),
            ImageFormat.Ani => Ani.Decode(data),
            ImageFormat.Astc => Astc.Decode(data),
            ImageFormat.Pvr => Pvr.Decode(data),
            ImageFormat.Dpx => Dpx.Decode(data),
            ImageFormat.Cineon => Cineon.Decode(data),
            ImageFormat.JpegLs => JpegLs.Decode(data),
            ImageFormat.Jxl => Jxl.Decode(data),
            ImageFormat.JpegXr => JpegXr.Decode(data),
            ImageFormat.Avif => Avif.Decode(data),
            ImageFormat.Dicom => Dicom.Decode(data),
            ImageFormat.Nitf => Nitf.Decode(data),
            _ => throw new ImageFormatException("unknown image format"),
        };

        public static RawImage Decode(Stream stream) => WithData(stream, d => Decode(d));

        /// <summary>
        /// Decodes an image file. For volumes (NRRD, NIfTI, MetaImage, FITS cubes, DDS / KTX volume textures) this is the first z
        /// slice; detached NRRD / MetaImage / NIfTI data files are found next to the header.
        /// </summary>
        public static RawImage Decode(string path)
        {
            var f = SniffFile(path);
            if (IsVolumeFormat(f)) return ColorTags.Tag(DecodeVolume(path).GetSlice(0), default, f);
            if (f == ImageFormat.Envi) return ColorTags.Tag(Envi.Decode(path), default, f);
            return WithFile(path, d => Decode(d));
        }

        static bool IsVolumeFormat(ImageFormat f) => f is ImageFormat.Nrrd or ImageFormat.Nifti or ImageFormat.MetaImage or ImageFormat.Fits;

        /// <summary>
        /// The format of a file from its content; a file with an ENVI header next to it is ENVI unless that header names
        /// another file type (ENVI also writes headers for TIFF and other files).
        /// </summary>
        internal static ImageFormat SniffFile(string path)
        {
            ImageFormat f;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1))
            {
                var b = new byte[8192];
                int n = Fill(fs, b, 0);
                f = DetectFormat(b.AsSpan(0, n));
            }
            if (f != ImageFormat.Envi && Envi.FindHeaderFile(path) is string hdr)
            {
                try
                {
                    var type = Envi.ReadHeader(hdr).FileType;
                    if (type == null || type.StartsWith("ENVI", StringComparison.OrdinalIgnoreCase)) return ImageFormat.Envi;
                }
                catch (ImageFormatException) { }
            }
            return f;
        }

        // ------------------------------------------------------------------ volumes

        /// <summary>
        /// Decodes the whole sample grid of a volume file: NRRD (.nrrd, .nhdr with its data files), NIfTI (.nii, .nii.gz, .hdr +
        /// .img), MetaImage (.mha, .mhd with its data files), FITS (the first image HDU), DICOM (all frames of a file; for a
        /// DICOMDIR or a folder the largest series, see <see cref="Dicom.FindSeries"/>), or a DDS / KTX volume texture (top mip
        /// level). 2D files give a grid with one slice. Physical metadata is in <see cref="RawVolume.Metadata"/>.
        /// </summary>
        public static RawVolume DecodeVolume(string path, VolumeDecodeOptions? options = null)
        {
            // a folder: the largest DICOM series in it (a DICOMDIR, or the files below it)
            if (Directory.Exists(path)) return Dicom.DecodeVolume(path, options);
            switch (SniffFile(path))
            {
                case ImageFormat.Nrrd: return Nrrd.DecodeVolume(path, options);
                case ImageFormat.Nifti: return Nifti.DecodeVolume(path, options);
                case ImageFormat.MetaImage: return MetaImage.DecodeVolume(path, options);
                case ImageFormat.Fits: return Fits.DecodeVolume(path, options);
                case ImageFormat.Envi: return Envi.DecodeVolume(path, new EnviDecodeOptions { MaxSamples = (options ?? VolumeDecodeOptions.Default).MaxSamples });
                case ImageFormat.Dicom: return Dicom.DecodeVolume(path, options);
                default: return WithFile(path, d => DecodeVolume(d, options));
            }
        }

        /// <summary>
        /// <see cref="DecodeVolume(string, VolumeDecodeOptions?)"/> from memory (detached data files need
        /// <see cref="VolumeDecodeOptions.DataDirectory"/> or <see cref="VolumeDecodeOptions.OpenDataFile"/>).
        /// </summary>
        public static RawVolume DecodeVolume(ReadOnlySpan<byte> data, VolumeDecodeOptions? options = null)
        {
            switch (DetectFormat(data))
            {
                case ImageFormat.Nrrd: return Nrrd.DecodeVolume(data, options);
                case ImageFormat.Nifti: return Nifti.DecodeVolume(data, options);
                case ImageFormat.MetaImage: return MetaImage.DecodeVolume(data, options);
                case ImageFormat.Fits: return Fits.DecodeVolume(data, options);
                case ImageFormat.Envi: return Envi.DecodeVolume(data, new EnviDecodeOptions { MaxSamples = (options ?? VolumeDecodeOptions.Default).MaxSamples });
                case ImageFormat.Dicom: return Dicom.DecodeVolume(data, options);
                case ImageFormat.Dds:
                    {
                        var t = Dds.DecodeTexture(data);
                        return RawVolume.FromSlices(t.Kind == DdsTextureKind.Texture3D ? t.GetSlices() : new[] { t.GetImage() });
                    }
                case ImageFormat.Ktx:
                    {
                        var t = Ktx.DecodeTexture(data);
                        return RawVolume.FromSlices(t.Kind == KtxTextureKind.Texture3D ? t.GetSlices() : new[] { Ktx.Decode(data) });
                    }
                case ImageFormat.Pvr:
                    {
                        var t = Pvr.DecodeTexture(data);
                        return RawVolume.FromSlices(t.Depth > 1 ? t.GetSlices(0, 0) : new[] { t.GetImage() });
                    }
                case ImageFormat.Astc: return RawVolume.FromSlices(Astc.DecodeVolume(data));
                case ImageFormat.Unknown: throw new ImageFormatException("unknown image format");
                default: return RawVolume.FromImage(Decode(data));
            }
        }

        /// <inheritdoc cref="DecodeVolume(ReadOnlySpan{byte}, VolumeDecodeOptions?)"/>
        public static RawVolume DecodeVolume(Stream stream, VolumeDecodeOptions? options = null) => WithData(stream, d => DecodeVolume(d, options));

        /// <summary>
        /// Writes a sample grid in the format given by the extension: .nrrd (gzip), .nhdr (+ .raw.gz), .nii, .nii.gz, .mha,
        /// .mhd (+ .zraw), .fits / .fit / .fts (also .gz), .dcm (one multi-frame DICOM file; see <see cref="Dicom.EncodeSeries"/>
        /// for a series of slices), .dds and .ktx2 (volume textures of the first frame).
        /// </summary>
        public static void EncodeVolume(RawVolume volume, string path)
        {
            ArgumentNullException.ThrowIfNull(volume);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".nhdr": Nrrd.EncodeDetached(volume, path); return;
                case ".mhd": MetaImage.EncodeDetached(volume, path); return;
            }
            var format = FormatFromExtension(path);
            if (format == ImageFormat.Unknown) throw new ArgumentException($"cannot infer the volume format from '{path}'");
            using var fs = File.Create(path);
            EncodeVolume(volume, fs, format, ext == ".gz");
        }

        /// <summary>Writes a sample grid as an attached file of the given format (NRRD, NIfTI-1, MetaImage .mha, FITS, DICOM, DDS, KTX).</summary>
        public static void EncodeVolume(RawVolume volume, Stream output, ImageFormat format) => EncodeVolume(volume, output, format, false);

        static void EncodeVolume(RawVolume volume, Stream output, ImageFormat format, bool gz)
        {
            switch (format)
            {
                case ImageFormat.Nrrd: Nrrd.Encode(volume, output); break;
                case ImageFormat.Nifti: Nifti.Encode(volume, output, new NiftiEncodeOptions { Gzip = gz }); break;
                case ImageFormat.MetaImage: MetaImage.Encode(volume, output); break;
                case ImageFormat.Fits:
                    if (gz)
                    {
                        using var g = Compression.Gzip.CreateCompressor(output);
                        Fits.Encode(volume, g);
                    }
                    else Fits.Encode(volume, output);
                    break;
                case ImageFormat.Dicom: Dicom.Encode(volume, output); break;
                case ImageFormat.Dds: Dds.EncodeVolume(volume.GetFrame(0).GetSlices(), output); break;
                case ImageFormat.Ktx: Ktx.EncodeVolume(volume.GetFrame(0).GetSlices(), output); break;
                default: throw new ArgumentException($"cannot write a volume as {format}", nameof(format));
            }
        }

        /// <summary>
        /// Decodes every image in the file: the pages of a TIFF or DCX, the frames of an animated GIF, PNG, WebP or JPEG XL (each the whole
        /// canvas as a viewer shows it), the array elements / cube faces of a DDS texture (top mip level), the image elements of a
        /// DPX file, or else the single image.
        /// </summary>
        public static RawImage[] DecodeAll(ReadOnlySpan<byte> data) => ColorTags.Tag(DecodeAllCore(data), data, DetectFormat(data));

        static RawImage[] DecodeAllCore(ReadOnlySpan<byte> data)
        {
            switch (DetectFormat(data))
            {
                case ImageFormat.Tiff:
                    {
                        int n = Tiff.GetImageCount(data);
                        var r = new RawImage[n];
                        for (int i = 0; i < n; i++) r[i] = Tiff.Decode(data, new TiffDecodeOptions { ImageIndex = i });
                        return r;
                    }
                case ImageFormat.Gif:
                    return Frames(Gif.DecodeAnimation(data).Frames, f => f.Image!); // composited mode always sets it
                case ImageFormat.Png when GetInfo(data).IsAnimated:
                    return Frames(Png.DecodeAnimation(data).Frames, f => f.Image);
                case ImageFormat.Webp when GetInfo(data).IsAnimated:
                    return Frames(Webp.DecodeAnimation(data).Frames, f => f.Image);
                case ImageFormat.Dds:
                    {
                        var t = Dds.DecodeTexture(data);
                        var r = new List<RawImage>();
                        for (int item = 0; item < t.ArraySize; item++)
                            for (int face = 0; face < Math.Max(1, t.Faces.Count); face++) r.Add(t.GetImage(0, item, face));
                        return r.ToArray();
                    }
                case ImageFormat.Ico:
                    return Ico.DecodeAll(data);
                case ImageFormat.Ktx:
                    return Ktx.DecodeAll(data);
                case ImageFormat.Dcx:
                    return Pcx.DecodeAll(data);
                case ImageFormat.Fits:
                    {
                        var r = new List<RawImage>();
                        foreach (var v in Fits.DecodeAllVolumes(data)) AddSlices(r, v);
                        return r.ToArray();
                    }
                case ImageFormat.Nrrd:
                case ImageFormat.Nifti:
                case ImageFormat.MetaImage:
                case ImageFormat.Dicom:
                    {
                        var r = new List<RawImage>();
                        AddSlices(r, DecodeVolume(data));
                        return r.ToArray();
                    }
                case ImageFormat.Nitf:
                    return Nitf.DecodeAll(data);
                case ImageFormat.Icns:
                    return Icns.DecodeAll(data);
                case ImageFormat.Ani:
                    return Ani.DecodeAll(data);
                case ImageFormat.Astc:
                    return Astc.DecodeVolume(data);
                case ImageFormat.Pvr:
                    return Pvr.DecodeAll(data);
                case ImageFormat.Dpx:
                    return Dpx.DecodeAll(data);
                case ImageFormat.Jxl:
                    return Jxl.DecodeAll(data);
                case ImageFormat.Jpeg:
                    return Jpeg.DecodeAll(data);
                case ImageFormat.Avif:
                    return Avif.DecodeAll(data);
                default:
                    return new[] { Decode(data) };
            }
        }

        static void AddSlices(List<RawImage> r, RawVolume v)
        {
            for (int f = 0; f < v.FrameCount; f++)
                for (int z = 0; z < v.Depth; z++) r.Add(v.GetSlice(z, f));
        }

        public static RawImage[] DecodeAll(Stream stream) => WithData(stream, d => DecodeAll(d));

        /// <summary>
        /// <see cref="DecodeAll(ReadOnlySpan{byte})"/> of a file; for NRRD, NIfTI, MetaImage and FITS every z slice of every frame
        /// (of every image HDU for FITS), detached data files included.
        /// </summary>
        public static RawImage[] DecodeAll(string path)
        {
            var f = SniffFile(path);
            if (f is ImageFormat.Nrrd or ImageFormat.Nifti or ImageFormat.MetaImage)
            {
                var r = new List<RawImage>();
                AddSlices(r, DecodeVolume(path));
                return ColorTags.Tag(r.ToArray(), default, f);
            }
            if (f == ImageFormat.Envi) return new[] { Decode(path) };   // one raster: its bands are its channels
            return WithFile(path, d => DecodeAll(d));
        }

        /// <summary>
        /// The image with its stored mip levels, largest first: the mip chain of a DDS texture (first array element / face) or
        /// the mip levels of an OpenEXR file (first part). Formats without stored levels give just the image; use
        /// <see cref="ImageOps.Resize(RawImage, int, int, ResizeOptions?)"/> to compute levels.
        /// </summary>
        public static RawImage[] DecodeMipmaps(ReadOnlySpan<byte> data) => ColorTags.Tag(DecodeMipmapsCore(data), data, DetectFormat(data));

        static RawImage[] DecodeMipmapsCore(ReadOnlySpan<byte> data) => DetectFormat(data) switch
        {
            ImageFormat.Dds => Dds.DecodeMipmaps(data),
            ImageFormat.Exr => Exr.DecodeMipmaps(data),
            ImageFormat.Ktx => Ktx.DecodeMipmaps(data),
            ImageFormat.Pvr => Pvr.DecodeMipmaps(data),
            _ => new[] { Decode(data) },
        };

        public static RawImage[] DecodeMipmaps(Stream stream) => WithData(stream, d => DecodeMipmaps(d));
        public static RawImage[] DecodeMipmaps(string path) => WithFile(path, d => DecodeMipmaps(d));

        static RawImage[] Frames<TF>(IReadOnlyList<TF> frames, Func<TF, RawImage> image)
        {
            var r = new RawImage[frames.Count];
            for (int i = 0; i < r.Length; i++) r[i] = image(frames[i]);
            return r;
        }

        /// <summary>Encodes with default options for the format.</summary>
        public static void Encode(RawImage image, Stream output, ImageFormat format)
        {
            switch (format)
            {
                case ImageFormat.Png: Png.Encode(image, output); break;
                case ImageFormat.Jpeg: Jpeg.Encode(image, output); break;
                case ImageFormat.Gif: Gif.Encode(image, output); break;
                case ImageFormat.Tiff: Tiff.Encode(image, output); break;
                case ImageFormat.Exr: Exr.Encode(image, output); break;
                case ImageFormat.Webp: Webp.Encode(image, output); break;
                case ImageFormat.Jp2: Jp2.Encode(image, output); break;
                case ImageFormat.Bmp: Bmp.Encode(image, output); break;
                case ImageFormat.Tga: Tga.Encode(image, output); break;
                case ImageFormat.Dds: Dds.Encode(image, output); break;
                case ImageFormat.Hdr: Hdr.Encode(image, output); break;
                case ImageFormat.Pnm: Pnm.Encode(image, output); break;
                case ImageFormat.Pfm: Pfm.Encode(image, output); break;
                case ImageFormat.Qoi: Qoi.Encode(image, output); break;
                case ImageFormat.Ico: Ico.Encode(new[] { image }, output); break;
                case ImageFormat.Ktx: Ktx.Encode(image, output); break;
                case ImageFormat.Pcx: Pcx.Encode(image, output); break;
                case ImageFormat.Dcx: Pcx.EncodeDcx(new[] { image }, output); break;
                case ImageFormat.Sgi: Sgi.Encode(image, output); break;
                case ImageFormat.SunRaster: SunRaster.Encode(image, output); break;
                case ImageFormat.Xbm: Xbm.Encode(image, output); break;
                case ImageFormat.Xpm: Xpm.Encode(image, output); break;
                case ImageFormat.Wbmp: Wbmp.Encode(image, output); break;
                case ImageFormat.Nrrd: Nrrd.Encode(image, output); break;
                case ImageFormat.Nifti: Nifti.Encode(image, output); break;
                case ImageFormat.MetaImage: MetaImage.Encode(image, output); break;
                case ImageFormat.Fits: Fits.Encode(image, output); break;
                case ImageFormat.Icns: Icns.Encode(new[] { image }, output); break;
                case ImageFormat.Ani: Ani.Encode(new[] { new AniInputFrame(image) }, output); break;
                case ImageFormat.Astc: Astc.Encode(image, output); break;
                case ImageFormat.Pvr: Pvr.Encode(image, output); break;
                case ImageFormat.Dpx: Dpx.Encode(image, output); break;
                case ImageFormat.Cineon: Cineon.Encode(image, output); break;
                case ImageFormat.JpegLs: JpegLs.Encode(image, output); break;
                case ImageFormat.Jxl: Jxl.Encode(image, output); break;
                case ImageFormat.JpegXr: JpegXr.Encode(image, output); break;
                case ImageFormat.Dicom: Dicom.Encode(image, output); break;
                case ImageFormat.Nitf: Nitf.Encode(image, output); break;
                case ImageFormat.Envi: throw new ImageNotSupportedException("ENVI keeps header and samples in two files: use Envi.Encode(image, samples, header) or Envi.Encode(image, path)");
                default: throw new ArgumentException("unknown image format", nameof(format));
            }
        }

        public static void Encode(RawImage image, string path)
        {
            var format = FormatFromExtension(path);
            if (format == ImageFormat.Unknown) throw new ArgumentException($"cannot infer image format from '{path}'");
            if (IsVolumeFormat(format)) { EncodeVolume(RawVolume.FromImage(image), path); return; }
            using var fs = File.Create(path);
            Encode(image, fs, format);
        }

        public static byte[] Encode(RawImage image, ImageFormat format)
        {
            using var ms = new MemoryStream();
            Encode(image, ms, format);
            return ms.ToArray();
        }

        /// <summary>
        /// Runs <paramref name="f"/> over the remainder of the stream without keeping a copy of it: a MemoryStream's own
        /// buffer, else the data read into a pooled buffer that is handed back afterwards (decoders only read the span
        /// while they run). Leaves the stream at its end.
        /// </summary>
        internal static T WithData<T>(Stream stream, SpanFunc<T> f)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (stream is MemoryStream m && m.TryGetBuffer(out var seg))
            {
                int pos = (int)Math.Min(m.Position, seg.Count);
                m.Position = m.Length;
                return f(seg.AsSpan(pos));
            }
            if (!stream.CanSeek) return f(ReadAll(stream));
            long len = Math.Max(stream.Length - stream.Position, 0);
            if (len > Array.MaxLength) throw new IOException("the data is too large for one array");
            var buf = System.Buffers.ArrayPool<byte>.Shared.Rent((int)len);
            try
            {
                stream.ReadExactly(buf, 0, (int)len);
                return f(buf.AsSpan(0, (int)len));
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(buf); }
        }

        /// <summary><see cref="WithData"/> over a whole file.</summary>
        internal static T WithFile<T>(string path, SpanFunc<T> f)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            return WithData(fs, f);
        }

        /// <summary>Reads the remainder of a stream into an array.</summary>
        public static byte[] ReadAll(Stream stream)
        {
            if (stream is MemoryStream m && m.TryGetBuffer(out var seg) && m.Position == 0 && seg.Offset == 0 && seg.Count == seg.Array!.Length)
            {
                m.Position = m.Length;
                return seg.Array;
            }
            if (stream.CanSeek)
            {
                long len = stream.Length - stream.Position;
                var buf = new byte[len];
                stream.ReadExactly(buf);
                return buf;
            }
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
