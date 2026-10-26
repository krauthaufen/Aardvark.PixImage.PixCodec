# Aardvark.PixImage.PixCodec

[![NuGet](https://img.shields.io/nuget/vpre/Aardvark.PixImage.PixCodec)](https://www.nuget.org/packages/Aardvark.PixImage.PixCodec)
[![Build](https://github.com/krauthaufen/Aardvark.PixImage.PixCodec/actions/workflows/build.yml/badge.svg)](https://github.com/krauthaufen/Aardvark.PixImage.PixCodec/actions/workflows/build.yml)

Pure managed image loading and saving for .NET 8 and .NET 10. No native binaries, no dependencies besides
Aardvark.Base.Tensors, MIT licensed. Little-endian hosts only; every change is tested on Linux and Windows (x64) and
macOS (ARM64).

```
dotnet add package Aardvark.PixImage.PixCodec --prerelease
```

or with Paket: `nuget Aardvark.PixImage.PixCodec prerelease`.

One package, **Aardvark.PixImage.PixCodec**, with two namespaces:

- `Aardvark.ImageCodecs`: the codecs, metadata, colour management and image operations on a plain `RawImage`.
- `Aardvark.Data`: `PixCodec`, `PixCodecLoader` and `PixCodecProcessor`, which load and save Aardvark `PixImage`s, mip
  maps, cube maps and volumes, next to the other loaders such as `PixImageSharp`.

The code namespace is deliberately not `Aardvark.PixImage.*`: a namespace of that name would hide the `PixImage` class in
code living in any `Aardvark.*` namespace.

| Format | Decode | Encode |
|---|---|---|
| PNG | everything in the spec, interlaced, 16 bit, APNG, all standard ancillary chunks | 8/16 bit gray, gray+alpha, RGB, RGBA, palette, 1/2/4 bit, interlaced, APNG |
| JPEG | baseline, progressive, lossless, hierarchical, arithmetic coding, 8 and 12 bit, CMYK/YCCK, scaled decode; HDR gain maps (Ultra HDR v1.1 XMP and ISO 21496-1, HDR rendition for any display headroom) | baseline, progressive, lossless (Huffman and arithmetic), arithmetic, 8 and 12 bit; own tuned quantization tables (about 13% smaller than libjpeg-turbo at equal SSIMULACRA2), optional trellis quantization (about 16%); Ultra HDR / ISO 21496-1 gain map JPEGs from HDR images |
| MPO / MPF | CIPA DC-007 Multi-Picture Format: MP index and attributes, every individual image (stereo / multi-view MPO, large thumbnails, gain map images); damaged MP data falls back to the primary image | Extended MP files (.mpo: disparity, multi-angle, panorama views), MPF of gain map JPEGs |
| TIFF | classic + BigTIFF, strips + tiles, 1..64 bit int, 16/24/32/64 bit float, LZW, Deflate, PackBits, CCITT, JPEG (incl. old-style, 12 bit, lossless), ZSTD, LZMA, WebP, JPEG 2000 (incl. Aperio), JBIG, LERC (decode only), PixarLog, LogLuv, Lab, multi-page, raw tags, GeoTIFF, regions, files > 4 GB | every sample type, strips or tiles, None / LZW / Deflate / PackBits / ZSTD / LZMA / JPEG / WebP / lossless JPEG 2000 / JBIG / CCITT G3 and G4, multi-page, overviews, cloud-optimized layout, GeoTIFF, extra tags |
| DNG | 1.0 .. 1.7.1: Bayer, X-Trans and other CFAs, LinearRaw and monochrome; uncompressed 8..32 bit int and 16/24/32 bit float, lossless JPEG (incl. reshaped frames), Deflate with the X2 / X4 predictors, lossy JPEG, JPEG XL tiles, row / column interleave; raw access (stored or linear values) with every parameter (`DngInfo`); rendering after the spec: linearization, black / white levels incl. deltas, opcode lists 1..3 (warps, vignette, gain maps, bad pixels, maps, per row / column ops, trim), white balance, edge directed demosaicing (Menon et al. 2007; bilinear optional), calibrations interpolated by temperature, forward matrices, baseline exposure, default crop / scale, orientation; to 8 / 16 bit sRGB or linear float (sRGB, XYZ D50 or camera); embedded previews | DNG 1.4 from raw samples and a `DngInfo` (every parameter it holds, EXIF / XMP, optional preview): lossless JPEG, uncompressed or (float) Deflate tiles; read back by tifffile and LibRaw |
| GIF | 87a/89a, interlace, transparency, animations (compositing verified against Chromium) | single images and animations, quantizer + dithering |
| OpenEXR | scanline, tiled, multi-part, deep, mip/rip levels, half/float/uint, luminance/chroma, all compressions incl. PIZ, B44, DWA, ZSTD, HTJ2K, region decode from a stream | scanline, tiled, multi-part, deep, luminance/chroma, all of those compressions |
| WebP | lossy, lossless, alpha, animation | lossy, lossless, alpha, animation |
| BMP | all header versions (OS/2 1.x/2.x, V1..V5), 1/2/4/8/16/24/32 bit, palettes, bit fields of any width (>8 bit fields give 16 bit samples), alpha masks, RLE4/RLE8/RLE24, embedded JPEG/PNG, ICC; checked against the BMP Suite | 8 bit gray, RGB, RGBA (gray+alpha as RGBA), resolution, ICC |
| DDS | 1D, 2D, volume and cube map textures, arrays, mip chains, legacy and DX10 headers; BC1..BC7 (BC6H as half float, signed BC4/BC5), legacy bit mask formats, the common uncompressed DXGI formats (8/16/32 bit int and float, 10:10:10:2, 5:6:5, 5:5:5:1, 4:4:4:4, R11G11B10F, RGB9E5) | uncompressed 8/16 bit, half and float, or block compressed BC1..BC7 (incl. BC6H, signed BC4/BC5; three quality levels); mip chains (given or computed), cube maps, volume textures |
| TGA | colour-mapped, true-colour (15/16/24/32 bit), gray (+alpha), RLE, every origin, TGA 2.0 alpha type | gray, gray+alpha, RGB, RGBA, optional RLE |
| LERC | LERC1 and LERC2 v2..v6 (bands, depth, masks, no-data, lossless float) | decode only |
| JBIG1 | the whole of T.82 (layers, all stripe orders, bit planes) | single layer, 1..8 bit planes |
| JPEG 2000 | Part 1 and HTJ2K (.j2k / .jph codestreams, JP2 / JPH container), 1..38 bit, region / reduced resolution / layer limited decode | Part 1 and HTJ2K, lossless and lossy (up to 16 bit), target bitrate, tiles, progression orders |
| JPEG XL | ISO/IEC 18181-1 codestreams and the 18181-2 container: Modular (every predictor incl. the weighted one, MA trees, RCT, palette and delta palette, squeeze) and VarDCT (every transform up to 256x256, AFV, chroma from luma, custom quantization tables), ANS / prefix codes with LZ77, groups, passes, LF frames, patches, splines, noise, Gaborish, EPF, upsampling, XYB, extra channels (alpha incl. premultiplied, depth, spot colours rendered, CMYK through its profile to sRGB), blending, layers and animation, JPEG recompressions (as pixels), 1..32 bit integer, half and float; EXIF / XMP / JUMBF boxes incl. Brotli compressed ones, HDR gain map boxes (18181-2:2026). Encode: lossless modular (8 / 16 bit, half, float; RCT, palettes, learned MA trees, weighted predictor, LZ77, effort 1..9) and lossy VarDCT in XYB (distance / quality like cjxl, DCT 4x4 .. 64x64, chroma from luma, Gaborish, EPF), alpha, ICC, EXIF / XMP boxes, orientation, animation | decode, encode |
| JPEG XR | ITU-T T.832 \| ISO/IEC 29199-2 (HD Photo): the Annex A container (EXIF / GPS / XMP / ICC, resolution, orientation) and bare codestreams; every pixel format of Table A.6 (gray, RGB / BGR 8 / 16 bit, fixed point, half, float, RGBE, 555 / 565 / 101010, BlackWhite, CMYK and CMYK direct (+ alpha), 3..8 channels (+ alpha), premultiplied alpha, YCC 4:2:0 / 4:2:2 / 4:4:4); spatial and frequency order, soft and hard tiles, overlap 0 / 1 / 2, windowing, dropped subbands and trimmed flexbits, quantization per tile / band / component with several QP sets, internal 4:2:0 / 4:2:2 / 4:4:4 / Y only / YUVK / n-component, interleaved and planar alpha; bit exact with jxrlib | lossless and lossy (quality or QP), those pixel formats from gray / RGB(A) / n-channel images of 8 / 16 bit, half and float, interleaved or planar alpha (own quality), overlap 0 / 1 / 2, 4:4:4 / 4:2:2 / 4:2:0 / Y only coding, tiles (soft or hard, encoded in parallel), spatial or frequency order, dropped subbands, flexbits trimming; ICC, XMP, EXIF, resolution, orientation |
| AVIF | AV1 Image File Format (.avif, .avifs): the whole AV1 decoding process (all intra and inter tools, palette, intra block copy, every transform, quantizer matrices, segmentation, loop filter, CDEF, loop restoration, superres, film grain, scaled references, 8 / 10 / 12 bit, 4:0:0 / 4:2:0 / 4:2:2 / 4:4:4, tiles in parallel, operating points and layers; bit exact with dav1d, passes the libaom test vectors and the Argon conformance streams), HEIF items (grids, overlays, identity derivations, alpha incl. premultiplied, depth and other auxiliary images, clap / irot / imir, nclx and ICC, Exif / XMP, layered images), image sequences with alpha, HDR gain maps (tmap, ISO 21496-1 metadata and the gain map image) | decode only |
| KTX / KTX2 | KTX 1.1 and 2.0: 1D, 2D, 3D, cube map and array textures, mip chains; every uncompressed Vulkan / OpenGL format with 8..64 bit components and the packed ones; BC1..BC7, ETC1 / ETC2 / EAC, ASTC (LDR and HDR, all 2D and 3D footprints), Basis Universal ETC1S (BasisLZ), UASTC LDR 4x4 and UASTC HDR 4x4; Zstandard and zlib supercompression; KTX 1 big endian files and orientation | KTX 2 with a full data format descriptor (passes `ktx validate`): uncompressed 8/16/32 bit int, half and float, BC1..BC7, ETC2 / EAC, ASTC (LDR and HDR, every 2D footprint), or Basis Universal ETC1S (BasisLZ, codebooks shared by all images, quality 1..255) and UASTC LDR 4x4; mip chains (given or computed), cube maps, arrays, volumes with 3D mip chains; Zstandard or zlib supercompression |
| Radiance HDR | RGBE and XYZE, new and old RLE and flat scanlines, all eight scan orders, EXPOSURE / PRIMARIES | RLE RGBE from float (or 8/16 bit converted to linear light) |
| PNM / PAM / PFM | P1..P7, any maxval up to 65535 (scaled to the full range), PAM tuple types, PFM gray / RGB in both byte orders | P1..P7 (plain or binary, bilevel), PFM |
| QOI | everything (sRGB / linear header) | RGB, RGBA |
| JPEG-LS | ITU-T T.87: 2..16 bit, 1..255 components, all interleave modes, near-lossless, preset parameters, mapping tables (palettes), oversize dimensions, restart markers, DNL, SPIFF, HP colour transforms, subsampled components (upsampled); as fast as CharLS | lossless and near-lossless, 2..16 bit, any channel count, all interleave modes, preset parameters, HP colour transforms, restart intervals, SPIFF; scan data byte identical to CharLS |
| ICO / CUR | PNG and DIB entries of every bit depth with AND mask or alpha, cursor hotspots | PNG or 32 bit DIB entries, icons and cursors (with hotspots) |
| ANI | animated cursors / icons: anih, rate, seq, LIST fram of ICO / CUR frames, INFO title and artist; steps with durations (jiffies) and hotspots | animated cursors and icons, shared frames through seq, per step durations |
| ICNS | every element type: 1 bit (ICON, ICN#, ics#, icm#, ich#), 4 / 8 bit with the Mac OS palettes, 24 bit RLE (is32, il32, ih32, it32) with 8 bit or 1 bit masks, ARGB (ic04, ic05, icsb), PNG and JPEG 2000 (icp4..icp6, ic07..ic14) | ARGB (16, 32) and PNG elements as iconutil writes them, optionally the RLE types with masks |
| PVR | PVR v3 and legacy v2: 2D, volume, cube map and array textures, mip chains, orientation and cube order metadata; every uncompressed channel layout (r, g, b, a, l, i, x of 1..32 bits, integer / normalized / float), PVRTC1 2 and 4 bpp, ETC1, ETC2 / EAC, BC1..BC7, ASTC (LDR / HDR, 2D and 3D footprints), RGB9E5, 1 bpp | uncompressed, BC1..BC7, ETC2 / EAC, ASTC, given or computed mip chains, cube maps, arrays, volumes |
| ASTC (.astc) | astcenc's container: 2D and 3D footprints, images and volumes, LDR / sRGB / HDR (found from the blocks or chosen) | 2D footprints from 4x4 to 12x12, LDR and HDR, volumes slice by slice |
| PCX / DCX | versions 0..5, RLE and uncompressed, 1/2/4/8 bit in 1..4 planes (mono, CGA, EGA, 16 colour, VGA palette), 24 bit (3 planes), 32 bit (4 planes), DCX pages | 1 bit, 8 bit palette (exact or quantized), 24 bit, 32 bit, DCX |
| SGI | 1..n channels, 8 and 16 bit, verbatim and RLE, 1/2/3 dimensional, DITHERED colour map | 8 and 16 bit, any channel count, RLE or verbatim |
| Sun Raster | 1/8/24/32 bit, RT_OLD, RT_STANDARD, RT_BYTE_ENCODED, RT_FORMAT_RGB, RGB and raw colour maps | 1 bit, 8 bit gray or colour mapped, 24 bit, 32 bit with alpha, optional RLE |
| XBM | X10 and X11 C source, hotspot | X10 or X11, hotspot |
| XPM | XPM 3 and XPM 2, any characters per pixel, #hex and X11 colour names, None, c / g / g4 / m visuals | XPM 3, None for transparent |
| WBMP | type 0 | type 0 |
| NRRD | .nrrd and detached .nhdr; every scalar type, raw / ascii / hex / gzip / bzip2, both byte orders, data file / LIST / printf pattern (with subdim), line skip, byte skip (also -1), kinds (RGB / vector axes as channels), space, directions, origin, spacings, key:=value pairs | .nrrd and .nhdr + data file; raw, gzip, ascii, hex; space, directions, origin from the metadata |
| NIfTI | NIfTI-1 and -2, .nii, .nii.gz, .hdr + .img (also gzipped), Analyze 7.5 headers; every data type but float128 / complex256 (RGB24 / RGBA32 as RGB(A), complex as two channels, vector intents as channels), scl_slope / scl_inter, sform / qform, dim_info, intents, extensions | NIfTI-1 .nii and .nii.gz: every integer and float type, RGB24 / RGBA32, vectors; qform and sform |
| MetaImage | .mha and .mhd; every MET_ type, ElementNumberOfChannels, zlib (CompressedData), both byte orders, HeaderSize (also -1), ElementDataFile LOCAL / file / LIST / pattern; TransformMatrix, Offset, ElementSpacing | .mha and .mhd + .raw / .zraw, compressed or not, every type, channels |
| FITS | primary array, IMAGE extensions, tile compressed images (RICE_1, GZIP_1, GZIP_2, PLIO_1, NOCOMPRESS; quantized floats with NO_DITHER / SUBTRACTIVE_DITHER_1 / 2), BITPIX 8 / 16 / 32 / 64 / -32 / -64, BSCALE / BZERO (unsigned conventions exact), BLANK, CONTINUE, linear WCS, .fits.gz | primary image of every BITPIX (unsigned via BZERO), optionally RICE_1 / GZIP_1 / GZIP_2 tile compressed, WCS from the metadata |
| ENVI | .hdr + sample file (found from either), data types 1 to 6, 9, 12 to 15 (complex as two channels), BSQ / BIL / BIP, both byte orders, header offset (also a header inside the sample file), bands as channels or as z, map info (reference pixel, rotation, units) and coordinate system string to `Metadata.Geo`, EPSG only when the header makes it certain, band names, wavelengths, fwhm, data ignore value | the pair of files or two streams, every type but int8 / half, complex, the three interleaves, both byte orders, map info + WKT, band names, wavelengths, fwhm, data ignore value |
| DICOM | PS3.10 files (also without preamble or file meta), DICOMDIR media and folders; implicit / explicit VR little endian, big endian, deflated; JPEG baseline / extended / lossless, JPEG-LS, JPEG 2000, HTJ2K, JPEG XL and RLE; 1..32 bit integer, float and double pixel data, overlays masked off, MONOCHROME1 / 2, RGB, YBR_FULL / 422 and PALETTE COLOR (also segmented); rescale, modality LUT and RT dose scaling; multi-frame and enhanced files, series stacked by position (4D, per-slice rescale) with LPS geometry; checked against pydicom and GDCM | Secondary Capture, single or multi-frame (per-frame positions) or one file per slice: 8..32 bit integer gray, RGB, float; uncompressed, RLE, lossless JPEG, JPEG-LS, JPEG 2000 and HTJ2K lossless, baseline JPEG; geometry and rescale; read back by pydicom and GDCM |
| DPX | SMPTE 268M 1.0 / 2.0, both byte orders, 1/8/10/12/16 bit int and 32/64 bit float, packed / filled method A / B, RLE, several image elements, luma, R/G/B/A/depth, RGB, RGBA, ABGR, Cb Y Cr (Y) 4:2:2 / 4:4:4 (+ alpha) to RGB, user defined 2..8 components, orientation, padding, all header fields (time code, film, television) | gray, RGB, RGBA, 2..8 components at 8, 10, 12, 16 bit and 32/64 bit float, packed / filled A / B, either byte order, header fields |
| NITF | NITF 2.1 / NSIF 1.0 (MIL-STD-2500C) and NITF 2.0 (MIL-STD-2500A): every image segment, IMODE B / P / R / S, blocks with fill and block masks, 1..64 bit INT / SI / B, ABPP left or right justified, 32 / 64 bit real and complex, look-up tables, XBANDS, JPEG (C3 / M3, also 12 bit) and JPEG 2000 (C8 / M8); all header fields, file and image TREs (also from TRE_OVERFLOW), IGEOLO corners as `Metadata.Geo`; checked against GDAL | NITF 2.1, one uncompressed image segment: 8..64 bit INT / SI, 32 / 64 bit real, any band count, IMODE B / P / R / S, blocking, ABPP, comments, TREs, placement as IGEOLO (WGS 84 degrees or UTM); read back by GDAL |
| Cineon | Kodak 4.5 draft: 1..16 and 32 bit, all packings, pixel / line / channel interleave, both byte orders, orientation, header fields | gray, RGB at 8, 10 (default for 8 bit images), 16 bit |

Not supported: OS/2 Huffman BMP, 64 bit BMP, JPEG XL (other than DNG's compression 52546) / JPEG XR inside TIFF, lossless JPEG recompression into JPEG XL (jbrd), lossy HTJ2K (LJ2K) EXR, JPEG 2000 Part 2 / JPX extensions,
PVRTC2, YUV and Basis Universal PVR formats, writing 3D ASTC footprints, ANI frames that are raw bitmaps (no AF_ICON),
FITS HCOMPRESS_1 tiles (the convention does not specify its bit stream), FITS tables and random groups, NRRD's block type,
NIfTI float128 / complex256, ASCII MetaImage data.
LERC encoding is not offered because Esri holds patents on LERC compression.

## Core library

```csharp
using Aardvark.ImageCodecs;

RawImage img = ImageCodec.Decode("photo.jpg");      // format is sniffed from the content, upright (EXIF orientation applied)
RawImage stored = ImageCodec.Decode("photo.jpg", new ImageDecodeOptions
{
    ApplyOrientation = false,                       // pixels as stored: what calibrations and GeoTIFF tiepoints refer to
    MaxPixels = 100_000_000,                        // refuse larger images before allocating
    Channels = ImageChannels.Rgba, SampleType = SampleType.UInt8,
    CancellationToken = token,
});
ImageCodec.Encode(img, "out.png");                  // format from the extension
Jpeg.Encode(img, stream, new JpegEncodeOptions { Quality = 85, Progressive = true });

Jpeg.DecodeBands(bytes, 64, (band, y) => Consume(band, y));   // rows 64 at a time, the image is never whole (JPEG, PNG)

GeoReference? geo = Tiff.Decode(bytes).Metadata?.Geo;          // GeoTIFF placement, CRS and no-data, fitted to the upright image
Tiff.Encode(img, stream, new TiffEncodeOptions { Geo = GeoReference.FromOrigin(600000, 5300000, 10, 10,
    new GeoCrs { Epsg = 32633, LinearUnit = GeoUnits.Metre }) });   // a projected CRS: UTM 33N, 10 m cells
Jpeg.Decode(bytes, new JpegDecodeOptions { ScaleEighths = 5, Rgba = true });   // 5/8 size by reduced IDCTs, straight to Rgba
ImageInfo info = ImageCodec.GetInfo("scan.tif");    // headers and metadata, no pixels
// info.Format, Width, Height, Channels, SampleType, Color, FrameCount,
// BitsPerSample, Orientation (+ StoredWidth/Height), DpiX, DpiY, HasIccProfile, IsAnimated, MipLevels, Metadata
PhotoMetadata m = info.Metadata!;                   // or ImageCodec.ReadMetadata(path)
// m.Make, Model, LensModel, SerialNumber, DateTaken (+ DateTakenOffset), ExposureTime, FNumber, Iso, FocalLength, ...
// m.Gps: Latitude, Longitude, Altitude, TimeUtc, Speed, ImageDirection, HorizontalError, ...
// m.Pose: camera / gimbal and vehicle yaw, pitch, roll, relative / absolute altitude, RTK (DJI, Parrot, Pix4D camera XMP)
// m.Title, Description, Artist, Copyright, Keywords, Rating, City, ...; m.Panorama (GPano)
// raw: m.Exif (every tag), m.Xmp (every property), m.Iptc, m.Comments, m.Text (PNG), m.ExrAttributes, m.Thumbnail
if (ImageCodec.TryGetInfo(path, out var i)) { /* ... */ }
RawImage[] levels = ImageCodec.DecodeMipmaps("tex.dds");   // stored mip levels (DDS, KTX, PVR, OpenEXR), else just the image
KtxTexture kt = Ktx.DecodeTexture(bytes);               // kt.GetImage(mip, layer, face, slice), kt.Format, kt.KeyValues
Ktx.Encode(img, stream, new KtxEncodeOptions { Compression = TextureCompression.BC7, GenerateMipmaps = true });
Ktx.EncodeVolume(slices, stream, new KtxEncodeOptions { GenerateMipmaps = true });   // 3D texture with a 3D mip chain
Ktx.Encode(img, stream, new KtxEncodeOptions { Compression = TextureCompression.Etc1s, Etc1sQuality = 128 });   // or Uastc
DdsTexture tex = Dds.DecodeTexture(bytes);              // every surface: tex.GetImage(mip, item, face, slice)
Dds.Encode(img, stream, new DdsEncodeOptions { Compression = DdsCompression.BC7, Mipmaps = DdsMipmaps.Generate });
Dds.EncodeVolume(slices, stream, new DdsEncodeOptions { Compression = DdsCompression.BC4 });   // 3D texture
PvrTexture pt = Pvr.DecodeTexture(bytes);               // pt.GetImage(mip, surface, face, slice), pt.Format ("PVRTC1_4", "r5g6b5", ...)
Pvr.EncodeCube(faces, stream, new PvrEncodeOptions { Compression = TextureCompression.Etc2, GenerateMipmaps = true });
RawImage[] slices3 = Astc.DecodeVolume(astcBytes);      // .astc files; AstcDecodeOptions.Profile: Auto, Ldr, LdrSrgb, Hdr
Astc.Encode(img, stream, new AstcEncodeOptions { BlockWidth = 6, BlockHeight = 6 });
AniAnimation ani = Ani.DecodeAnimation(bytes);          // ani.Steps[i].Image, .Jiffies, .DelayMs, .HotspotX / Y
Ani.Encode(new[] { new AniInputFrame(a, 6, 3, 4), new AniInputFrame(b, 6, 3, 4) }, stream);
Icns.Encode(new[] { img16, img32, img128, img512 }, stream);   // Icns.ReadEntries / DecodeAll for every element
```

`RawImage` is a tightly packed, top-down, channel-interleaved typed array (`byte[]`, `ushort[]`, `float[]`, ...)
in R,G,B,A order with straight alpha. Images come back upright: the EXIF/TIFF orientation (JPEG, PNG, WebP, TIFF, DNG, JPEG 2000, JPEG XL, JPEG XR) and AVIF's irot / imir
are applied while decoding, the metadata then says orientation 1 (also inside a kept EXIF blob, so a re-saved image is not
turned again), and `ImageInfo` reports the upright size. Every format's options have `ApplyOrientation = false` for the
stored pixels, and so has `ImageDecodeOptions` for `ImageCodec.Decode` and `PixCodec.Load`; with it, `ImageCodec.GetInfo`
describes the stored size. Each format has its own static class (`Png`, `Jpeg`, `Tiff`, `Gif`, `Exr`, `Webp`, `Jp2`, `Bmp`, `Tga`,
`Dds`, `Ktx`, `Pvr`, `Astc`, `Hdr`, `Pnm`, `Pfm`, `Qoi`, `Ico`, `Ani`, `Icns`, `Lerc`, `Jbig`, `Jxl`, `JpegXr`, `Avif`, `JpegLs`, `Dng`, `Pcx`, `Sgi`, `SunRaster`, `Xbm`, `Xpm`, `Wbmp`, `Nrrd`, `Nifti`, `MetaImage`, `Fits`, `Dicom`, `Dpx`, `Cineon`) with format specific options and extras: animations (GIF, APNG, WebP, JPEG XL, AVIF), TIFF pages / tags / regions / overviews,
EXR headers, named channels, levels and deep data, DPX / Cineon headers (`Dpx.GetHeader`, `Cineon.GetHeader`) and DPX image
elements. Bit depths that do not fill a sample type keep their stored values: 10 bit DPX and Cineon decode to UInt16 0..1023,
12 bit to 0..4095 (as 12 bit TIFF and JPEG); 1, 2 and 4 bit samples are stretched to 0..255.

Malformed files throw `ImageFormatException`, valid files using an unimplemented feature throw `ImageNotSupportedException`;
both derive from `ImageCodecException`. Truncated files throw too, JPEG included (`JpegDecodeOptions.AllowTruncated`
decodes what is there and sets `ImageMetadata.Truncated`).

### HDR gain maps and multi-picture JPEG

`Jpeg.Decode` of an Ultra HDR / ISO 21496-1 JPEG returns the SDR baseline as before. `Jpeg.GetGainMap` gives the gain
map (`GainMap`: ISO 21496-1 metadata as `GainMapMetadata`, read from the binary APP2 payload or mapped from the Ultra HDR
/ Adobe `hdrgm` XMP, the decoded gain map image, the alternate colour space), found through the MPF index, the GContainer
directory or the MPF images' headers; unusable metadata means no gain map. `GainMap.Apply` reconstructs the alternate
rendition as ISO 21496-1 clause 6 says (linear Float32 RGB, 1.0 = SDR white) for a display headroom (log2; full by
default), and `Jpeg.DecodeHdr` / `Jxl.DecodeHdr` / `ImageCodec.DecodeHdr` / `PixCodec.LoadHdr` do it in one call; JPEG
XL `jhgm` boxes go through the same code (`JxlGainMap.ToGainMap`). `Jpeg.EncodeUltraHdr` writes a gain map JPEG from an
HDR image (tone mapped baseline) or an SDR + HDR pair, with MPF, Ultra HDR XMP and ISO 21496-1 metadata; libultrahdr
decodes the files to within 0.04 stops of our own reconstruction. `ImageInfo.HasGainMap` and `ImageInfo.MultiPicture`
(the MPF index) come from the headers; for MPO files `FrameCount` is the number of views, `Jpeg.DecodeAll` /
`ImageCodec.DecodeAll` return them and `Jpeg.DecodePicture` / `ExtractPicture` any individual image. `Jpeg.EncodeMpo`
writes stereo and multi-view MPO files.

### JPEG XL

`Jxl.Decode` returns the image in the colour space of its header: XYB images are converted to the encoding the header
asks for (for an embedded ICC profile through linear sRGB and that profile), enumerated encodings other than sRGB come
with an ICC profile built from them, and the samples are 8 / 16 / 32 bit integer, half or float as the header's bit depth
says. Premultiplied alpha is returned straight. CMYK images (three colour channels plus a black extra channel under a
CMYK profile) are converted to sRGB through the profile, since `RawImage` has no CMYK model; spot colours are rendered
onto the colour (`RenderSpotColours = false` keeps them out). `Jxl.DecodeExtraChannels` returns every extra channel
(depth, thermal, selection masks, spot colours, black, ...) with its type and name. `Jxl.DecodeAnimation` gives every displayed frame with durations, `Jxl.GetMetadataBoxes` the raw EXIF / XMP /
JUMBF, `Jxl.GetGainMap` the HDR gain map of an 18181-2:2026 `jhgm` box (ISO 21496-1 metadata, the alternate image's
colour space, the gain map image; `PhotoMetadata.HasGainMap` tells it is there). Groups and colour conversion run in parallel (`Multithreaded = false` for one thread). On 8 cores a 13 MP photo
takes about 0.13 s (lossy, distance 1) and 0.5 s (lossless, effort 3), libjxl 0.08 s and 0.29 s; on one thread 0.5 s and
2.4 s, libjxl 0.3 s and 1.6 s.

`Jxl.Encode` (also `ImageCodec.Encode`, `PixCodec.Save` / `SaveJxl` for `.jxl`) writes lossy VarDCT by default
(`Distance = 1`, or `Quality` with cjxl's -q mapping) and lossless modular with `Lossless = true` (or distance 0 /
quality 100); `Effort` 1..9 trades speed for size like cjxl -e (default 7). Lossless output decodes bit exactly with
libjxl; on a 12 MP photo it is 10 % smaller than cjxl at effort 1, 1.4 % at 3 and 2.4 % at 7. Lossy output at effort 7
needs about 4 % fewer bytes than cjxl -e 7 for the same SSIMULACRA2 score (at effort 3 about 4 % more than cjxl -e 3),
and 16 .. 32 % fewer than our JPEG encoder. Alpha is stored losslessly, ICC profiles, EXIF / XMP boxes (in the container,
used only when needed; a `jxll` box declares level 10 when the image goes beyond level 5 of 18181-1 Annex M, e.g. 16 bit
lossless), orientation and intensity target are kept; `Jxl.EncodeAnimation` writes animations. Timings on
a 12 MP photo, 8 threads / 1 thread (busy machine): lossless effort 1 / 3 / 7 0.57 / 0.88 / 3.9 s and 1.7 / 3.5 / 11.5 s
(cjxl 0.14 / 1.4 / 3.6 s and 0.11 / 2.1 / 13.5 s), lossy effort 3 / 7 0.77 / 2.2 s and 2.2 / 10 s (cjxl 0.63 / 1.5 s and
0.45 / 4.5 s).

### DNG

`Dng.Decode` (also `ImageCodec.Decode`, the loader for `.dng`) renders the raw image the way the DNG 1.7.1 specification
describes it: OpcodeList1 on the stored values, LinearizationTable, black levels (pattern, BlackLevelDeltaH / V), white
level, OpcodeList2, white balance (AsShotNeutral or AsShotWhiteXY, or `WhiteBalanceNeutral` / `WhiteBalanceXY`),
highlight clipping, demosaicing, OpcodeList3, camera to XYZ D50 with the colour calibrations interpolated in inverse
correlated colour temperature (forward matrices when present, else the inverted colour matrices with linear Bradford
adaptation; colour matrices are used normalized so that D50 maps to a neutral with largest component 1), the profile's
hue / saturation map (and with `ApplyLookTable` its look table), BaselineExposure, default crop and scale, orientation.
Output is 16 bit (default) or 8 bit sRGB, or linear Float32 (sRGB, XYZ D50 or white balanced camera colours). Demosaicing
of Bayer data follows Menon, Andriani and Calvagno, "Demosaicing With Directional Filtering and a Posteriori Decision"
(IEEE TIP 2007) without its refinement step; other patterns (X-Trans, four colour CFAs) use colour difference
interpolation; `DngDemosaicAlgorithm.Bilinear` is the fast alternative. Opcodes: WarpRectilinear (1 and 2), WarpFisheye,
FixVignetteRadial, FixBadPixelsConstant / List, TrimBounds, MapTable, MapPolynomial, GainMap, Delta / ScalePerRow /
Column; unknown optional opcodes are skipped, unknown required ones throw `ImageNotSupportedException`.

`Dng.ReadInfo` returns every parameter without decoding (`DngInfo`: camera, raw layout and size, active area, crop, CFA,
levels, linearization table, calibrations, white balance, exposure, opcode lists, previews); `Dng.DecodeRaw` the stored
samples (`DngRawStage.Stored`, as in the file, masked areas included) or the linear reference values of the active area
(`DngRawStage.Linear`, Float32); `Dng.DecodePreview` / `Dng.ExtractPreviewJpeg` the embedded previews (fast).
`Dng.Encode` writes a DNG from stored samples and a `DngInfo` (e.g. what `DecodeRaw` returned): lossless JPEG,
uncompressed or Deflate tiles, every parameter, EXIF / XMP, optionally a preview.

Checked against tifffile (stored samples of all test files, bit exact), LibRaw through rawpy (raw values bit exact; colours of
flat patches within 0.002; camera files after box averaging within 0.012 mean / 0.08 99th percentile in sRGB encoded
values, with LibRaw's exposure convention normalized) and a numpy model of the specification for what no reader
implements (opcode lists, interpolated calibrations with forward matrices, hue / saturation maps). Rendering takes about
0.5 s for an 18 MP Bayer file or a 22 MP lossy LinearRaw file and 1.4 s for a 16 MP X-Trans file with a lens warp and a
hue / saturation map (8 threads of an i7-9700E that was busy with other work).
### AVIF

`Avif.Decode` returns the primary image (for a sequence without one, its first frame) as RGB(A) or gray(+alpha):
UInt8 for 8 bit streams, UInt16 holding the stored range for 10 and 12 bit (0..1023, 0..4095). YUV is converted with
the matrix and range of the nclx box or the AV1 sequence header (BT.601 / 709 / 2020, identity, YCgCo), chroma
upsampled bilinearly (within one step of avifdec); premultiplied alpha comes back straight. The colour space is described
by the ICC profile of the file or one built from the nclx primaries and transfer; PQ and HLG are reported as the
transfer function, not applied. The clean aperture is always applied, rotation and mirroring unless
`ApplyOrientation = false`. `Avif.GetInfo` reads everything from the container without decoding.
`Avif.DecodeAnimation` gives the frames of an image sequence with durations and sync flags, `Avif.GetGainMap` the tone
map item's ISO 21496-1 metadata, the alternate colour space and the gain map image, `Avif.GetMetadataItems` the Exif /
XMP items, `Avif.DecodeAuxiliaryImages` depth maps and other auxiliary images and `Avif.DecodeThumbnail` the thumbnail item. Tiles, loop filters and the colour
conversion run in parallel (`Multithreaded = false` for one thread). The AV1 layer matches dav1d bit for bit (libaom
test vectors, Argon conformance streams). On a Ryzen 7 7700X a 12.5 MP 4:2:0 photo (avifenc speed 6) decodes to RGB in
0.29 s on one thread and 0.05 s on 16 (dav1d alone, without colour conversion: 0.054 s / 0.016 s); 1080p inter frames
take about 15 ms each (dav1d 1.5 ms).

### Volumes and scientific images

NRRD, NIfTI, MetaImage and FITS hold 2D images, volumes and more dimensions, with physical units. 2D files go through
the normal `Decode` / `GetInfo`; for a volume `Decode` gives its first z slice and `ImageInfo.Depth` the number of slices
(`FrameCount` the product of the axes beyond the third). The whole grid comes from the volume path:

```csharp
RawVolume v = ImageCodec.DecodeVolume("brain.nii.gz");   // or .nrrd, .nhdr, .mha, .mhd, .hdr, .fits, DDS / KTX volume textures
// v.Sizes (x, y, z, then further axes), v.Width / Height / Depth, v.FrameCount, v.Channels, v.SampleType, v.Color
// v.Data: dense, channel fastest, then x, y, z, further axes; v.GetIndex(x, y, z, t), v.GetSlice(z, frame), v.GetFrame(t)
PhysicalMetadata m = v.Metadata!;
// m.Space ("RAS", "LPS", "LAS", NRRD space names, "FITS-WCS"), m.Spacing, m.Origin, m.Direction (columns = axis directions),
// m.Units, m.ValueUnit, m.Slope / Intercept / ScalingApplied, m.Blank, m.Header (every header entry), m.Get("descrip")
double[,] toLps = m.GetVoxelToLps()!;                    // 4x4 index -> world affine (also GetVoxelToRas, GetVoxelToWorld)
VolumeInfo vi = Nifti.GetVolumeInfo(bytes);              // the same from the header only
NiftiHeader h = Nifti.ReadHeader(bytes);                  // every NIfTI field: intent, dim_info, qform / sform, extensions
IReadOnlyList<FitsHdu> hdus = Fits.ReadHdus(bytes);       // every HDU with its cards; Fits.DecodeAllVolumes(bytes)
ImageCodec.EncodeVolume(v, "out.nrrd");                   // .nrrd .nhdr .nii .nii.gz .mha .mhd .fits .dds .ktx2
Fits.Encode(v, stream, new FitsEncodeOptions { Compression = FitsCompression.Rice });
```

Detached data (NRRD `data file`, MetaImage `ElementDataFile`, the .img of a NIfTI .hdr) is found next to the header when
decoding from a path; from memory set `VolumeDecodeOptions.DataDirectory` or `OpenDataFile`. Path based decoding streams
the samples, so volumes above 2 GB work (up to 2^31 samples per volume).

- Axes: x is the fastest axis of the file. NRRD, NIfTI and MetaImage are not flipped (row y = 0 first, as ITK, nibabel
  and 3D Slicer index them; viewers place voxels by the affine). FITS stores y = 1 at the bottom, as ds9 shows it: rows
  are reversed so that the top row comes first like in every other image (`FitsDecodeOptions.ApplyOrientation = false`
  keeps the file order); the WCS in the metadata is adjusted to that. Interleaved channels: NRRD's leading non-spatial
  axis (RGB-color, vector, ...; a single trailing one is moved to the front), MetaImage ElementNumberOfChannels, NIfTI
  RGB24 / RGBA32, complex (real, imaginary) and vector intents (dim 5). FITS has no channels: RGB images are written as
  three planes.
- Orientation: NRRD space directions / origin in the header's space (LPS for Slicer and ITK files); NIfTI sform when
  sform_code > 0, else qform, else the voxel sizes (nibabel's choice), in RAS; MetaImage TransformMatrix / Offset in LPS;
  FITS the linear part of the WCS (CRPIX, CRVAL, CDELT, PC / CD, CROTA2) as "FITS-WCS", celestial projections are left to
  the raw keywords. Encoders convert between RAS and LPS as needed.
- Value scaling (NIfTI scl_slope / scl_inter, FITS BSCALE / BZERO) is applied by default (`DataScaling.Physical`)
  without losing integer precision: integer slope and intercept keep integers in the smallest type that holds every
  value (FITS' unsigned conventions give exact UInt16 / UInt32 / UInt64 / Int8, an intercept of -1024 on Int16 gives
  Int32); otherwise Float32 for stored types up to 16 bit, Float64 for 32 / 64 bit integers. FITS BLANK samples become
  NaN in float results. `DataScaling.Raw` keeps the stored values (slope and intercept are reported, and written back by
  the NIfTI encoder), `Float32` / `Float64` force a float type.

### DICOM

`ImageCodec.Decode` of a DICOM file gives its first frame, `DecodeVolume` all frames; for a folder or a DICOMDIR (the CD or
USB stick a hospital hands out) it decodes the largest image series:

```csharp
foreach (var s in Dicom.FindSeries("/media/usb"))          // DICOMDIR records, else every DICOM file below the folder
    Console.WriteLine($"{s.PatientName} {s.StudyDate} {s.Modality} {s.SeriesDescription}: {s.Files.Count} files");
RawVolume ct = Dicom.FindSeries("/media/usb")[0].Decode(); // slices sorted along the normal, Hounsfield units
RawVolume same = Dicom.DecodeSeries("/media/usb/DICOM/IM000123"); // the series of one of its files
DicomDataset ds = Dicom.ReadDataset(bytes);                // every element: ds.GetString(0x00100010), sequences, ...
Dicom.EncodeSeries(volume, "out", new DicomEncodeOptions { Compression = DicomCompression.JpegLsLossless });
```

- Values: bits above Bits Stored (overlays) are masked off, signed samples sign extended. The rescale slope / intercept,
  a modality LUT or an RT dose grid scaling are applied like the volume formats' scaling (`DataScaling`): CT comes out as
  Int32 Hounsfield units, PET with per-slice slopes as Float32; `DataScaling.Raw` keeps the stored values. MONOCHROME1 is
  left as stored unless `DicomDecodeOptions.InvertMonochrome1`; PALETTE COLOR becomes RGB unless `ApplyPalette = false`.
- Geometry in LPS: x along the rows (the first vector of Image Orientation), y down the columns, origin at the centre of
  the first pixel. Series are stacked along row x column direction (as GDCM and ITK do), the slice step taken from the
  first and last position; positions that repeat give a fourth axis (time points), slices of another size or orientation
  (localizers) are left out, uneven gaps are reported in the header (`SliceSpacingIrregular`).
- Writing: Secondary Capture files with the geometry and an unapplied rescale; one multi-frame file
  (`Dicom.Encode`, positions in the per-frame functional groups) or one file per slice (`Dicom.EncodeSeries`, what most
  tools expect). No DICOMDIR is written.

### sRGB and gamma

Decoders never convert colour values: an 8 bit JPEG gives the stored sRGB values, an EXR the stored linear floats.
`ImageInfo.Transfer` (and `ImageMetadata.Transfer` of decoded images) says what they are: what the file declares (PNG sRGB,
gAMA and cICP chunks, _SRGB texture formats, the KTX data format descriptor, the QOI header) or else what the format implies
(`Linear` for OpenEXR, Radiance HDR, PFM and float samples; `Srgb` for 8 / 16 bit colour; `Unknown` for DDS without an
_SRGB format, PVR in the linear colour space, LDR .astc files and non-colour data). DPX reports its transfer characteristic (linear, the video curves as `Gamma`;
printing density / logarithmic and user defined as `Unknown`, the raw code is in `Dpx.GetHeader`), Cineon `Unknown` for its
printing density log data. `Gamma` is a pure power law (`ImageMetadata.Gamma`), `Pq` and `Hlg` are the HDR
video curves. An ICC profile (`HasIccProfile`, `ImageMetadata.IccProfile`) overrides them; decoders keep it, the colour
management below applies it.

```csharp
RawImage lin  = ImageOps.ToLinear(img);                 // Float32 linear light per img's transfer function
RawImage srgb = ImageOps.LinearToSrgb(lin);              // back to 8 bit sRGB (or UInt16, Float16, Float32)
```

### ICC colour management

`IccProfile.Parse` reads ICC v2 and v4 profiles: header, tag directory, matrix / TRC (`rXYZ`.., `rTRC`.., `curv` gammas and
tables, `para` types 0..4), gray `kTRC`, `lut8` / `lut16` (v2) and `lutAToB` / `lutBToA` (v4: B / M / A curves, matrix
with offsets, CLUTs of any grid and 8 / 16 bit precision) per rendering intent, Lab and XYZ PCS, `wtpt`, `chad`, `desc` /
`mluc` texts. Malformed profiles throw `ImageFormatException`; sizes are checked against the data before anything is
allocated. `IccProfile.Srgb`, `LinearSrgb`, `DisplayP3`, `Rec2020`, `LinearRec2020`, `ProPhotoRgb` (ROMM), `A98Rgb` (the
primaries, white and gamma of Adobe RGB (1998); not Adobe's profile), `GrayGamma22`, `GraySrgb`, `GrayLinear` and
`CreateRgb` / `CreateGray` build profiles from chromaticities and curves (Bradford adapted to D50) and write them as v4
(or v2) profiles with `ToBytes()`, ready for `ImageMetadata.IccProfile`.

```csharp
RawImage srgb = ImageOps.ToSrgb(img);                   // through img's embedded profile; Icc dropped, Transfer = Srgb
RawImage lin  = ImageOps.ToLinearSrgb(img);              // Float32, linear sRGB primaries, out of gamut values kept
RawImage p3   = ImageOps.ConvertColor(img, IccProfile.Srgb, IccProfile.DisplayP3);      // embeds Display P3
RawImage rgb  = ImageOps.ConvertColor(cmyk, IccProfile.Parse(iccBytes), IccProfile.Srgb,
    new ColorConvertOptions { Intent = IccRenderingIntent.RelativeColorimetric, BlackPointCompensation = true });
var t = new IccTransform(src, dst, IccRenderingIntent.Perceptual);   // reusable, thread safe; t.Transform(float[])
```

Transforms support the perceptual, relative colorimetric, saturation (falling back to the perceptual tables) and absolute
colorimetric intents, black point compensation (ISO 18619; applied automatically for perceptual / saturation into v4
profiles, as LittleCMS does), Gray, RGB, CMYK and n-colour data. Matrix / TRC pairs fold into curves, one matrix and exact
integer rounding of the inverse curves; LUT profiles run in single precision with tetrahedral CLUT interpolation
(trilinear for Lab indexed tables). 8 and 16 bit inputs go through cached curve tables, conversions run multi-threaded,
alpha is kept. CMYK of Adobe JPEGs (`JpegDecodeOptions.RawCmyk`) is inverted: convert it with
`ColorConvertOptions.InvertedInput`. Results agree with LittleCMS 2.16 float transforms within 1 code for 8 bit output.
Not supported: device link, abstract chains, named colour profiles, `D2Bx` / `B2Dx` floating point (iccMAX style) tags,
PQ / HLG images without a profile.

Encoders record it where the format can: PNG writes gAMA 1.0 for linear images, QOI its colour space byte, KTX and DDS
choose _SRGB formats for sRGB data (KTX by default from `Transfer`, DDS with `Srgb = true`). Mip levels of sRGB images are
computed in linear light, as is `ResizeOptions.LinearLight` resizing.

`GetInfo` never reads pixel data: files are memory mapped (only the pages holding headers and metadata are read),
streams are walked by seeking past image chunks (PNG, WebP, JPEG 2000) or by reading just the TIFF directories. GIF
frame counting, run length coded TGA / BMP and PCX (its palette is at the end) walk all data; WBMP, which has no
signature, is only recognized from the whole file. Metadata comes from EXIF, XMP (also JPEG extended XMP),
IPTC (JPEG APP13, TIFF), ICC descriptions, JPEG / GIF / JPEG 2000 comments, PNG text chunks (incl. ImageMagick raw
profiles) and OpenEXR attributes; where several hold a value, EXIF wins over XMP over IPTC. XMP is kept when decoding
and written back by the JPEG, PNG, TIFF, WebP and JPEG 2000 encoders (its tiff:Orientation follows the applied
orientation; JPEG splits packets beyond 64 KB into standard and extended XMP).

### Writing metadata

```csharp
var edit = new MetadataEdit
{
    Make = "DJI", Model = "FC6310R", SerialNumber = "0K8TF7R0012345",
    DateTaken = new DateTime(2024, 6, 15, 14, 23, 45, 120), DateTakenOffset = TimeSpan.FromHours(2),
    Title = "Pier 3", Artist = "Jane Pilot", Copyright = "(c) 2024", Keywords = new[] { "bridge", "pier" }, Rating = 4,
    Gps = new GpsEdit { Latitude = 47.0707123, Longitude = 15.4395456, Altitude = 412.35, TimeUtc = utc, ImageDirection = 123.4, HorizontalError = 0.02 },
};
edit.Remove(MetadataField.UserComment);                           // a field, wherever it is stored
edit.SetExif(ExifIfd.Exif, 0xA420, ExifType.Ascii, "0123...");    // any tag (not the structure ones)
edit.SetXmpNamespace("my", "http://example.com/ns/").SetXmp("my:Flight", "17").SetXmp("dc:subject", kws, XmpArrayType.Bag);
edit.RemoveXmp("crs:*");  edit.SetIptc(2, 105, "Headline");

byte[] tagged = ImageCodec.WriteMetadata(bytes, edit);                    // or (Stream, Stream, edit), (path, path, edit)
ImageCodec.WriteMetadata("scan.tif", "scan.tif", new MetadataEdit { RemoveGps = true });   // in place
new MetadataEdit { RemoveAll = true }.ApplyTo(img);                       // ImageMetadata before encoding
```

`WriteMetadata` rewrites JPEG (APP1 EXIF / XMP / extended XMP, APP13 IPTC), PNG (eXIf, iTXt XMP, existing text chunks,
CRCs), TIFF and BigTIFF (the changed directories are appended, the header points to the new IFD0, unchanged entries keep
their values where they are), WebP (EXIF / XMP chunks, VP8X flags, a VP8X header added to simple files) and JPEG 2000
JP2 / JPX (uuid boxes) without touching the compressed image data, which stays byte for byte as it is. EXIF values go to
EXIF (IFD0, Exif, GPS directories; required tags are added to directories made new, ExifVersion / GPSVersionID are raised
when a newer tag such as OffsetTimeOriginal is added, EXIF 3.0 ImageTitle is only written into 3.0 directories); title,
description, creator, rights, keywords, rating and location always go to XMP too; XMP properties that mirror EXIF values
are updated where they exist; IPTC datasets only where the file already has IPTC (switched to UTF-8 when needed, the
Photoshop IPTC digest kept in step). Removing values zeroes their bytes (old directories, values, thumbnails) unless
something else still refers to them.

EXIF blocks with a maker note, sub directories or data no known field refers to (previews that maker notes point to)
keep their layout: everything stays at its offset, the directories are written in their old place when they fit,
otherwise after the block. Others are laid out anew. Limits: TIFF edits concern the first page (IFD0 and its EXIF / GPS
directories); EXIF in JPEG must fit into one 64 KB segment (drop the thumbnail otherwise); multi-picture (MPF) offsets
are corrected, AFCP trailers are not; JPEG 2000 codestream comments and raw codestreams (.j2k) are not touched, JPX
files with fragment tables are refused; BMP, TGA, PCX, SGI, Sun Raster, XBM, XPM and WBMP have no standard place for EXIF / XMP, GIF, OpenEXR and DDS are not
supported (`ImageNotSupportedException`). `RemoveAll` keeps the ICC profile, resolution and orientation and, in TIFF,
every structural and GeoTIFF tag.

## Compression

`Aardvark.ImageCodecs.Compression` holds fully managed deflate/inflate (zlib, levels 0..9, streaming `Inflater`), Zstandard
(decode and encode, both with dictionaries), XZ / LZMA (decode incl. BCJ filters, encode up to an optimal parser,
multi-threaded), Brotli (decode, RFC 7932, for JPEG XL `brob` boxes), gzip (`Gzip`: multi-member decode, encode, streaming) and bzip2 decoding (`Bzip2`, written from the
format description) and vectorised CRC-32 / Adler-32. The managed deflate is the default; `Zlib.Backend = ZlibBackend.Bcl` (or `PIXCODEC_ZLIB=bcl`) switches to
`System.IO.Compression`.

## Image operations

`ImageOps` works on any `RawImage` (every sample type, any channel count) and returns new images:

```csharp
var thumb = ImageOps.Resize(img, 320, 240);                          // Lanczos3
var small = ImageOps.Resize(img, 800, 600, ResampleFilter.Mitchell);
var fit   = ImageOps.ResizeToFit(img, 1024, 1024, new ResizeOptions { LinearLight = true });
var turn  = ImageOps.Rotate90(img);                                  // lossless, also Flip*, Transpose, ApplyOrientation
var rot   = ImageOps.Rotate(img, 12.5, expand: true, new WarpOptions { Filter = ResampleFilter.Bicubic });
var w     = ImageOps.WarpPerspective(img, 800, 600, homography, new WarpOptions { Border = BorderMode.Mirror });
```

- Filters: `Nearest`, `Box`, `Bilinear`, `Hermite`, `Bicubic` (Catmull-Rom), `Mitchell`, `BSpline`, `Hamming`, `Lanczos2`,
  `Lanczos3`, `Gaussian`, plus `ResampleFilter.Cubic(b, c)`, `Lanczos(n)` and custom kernels.
- Resizing is separable and widens the filter when downscaling (no aliasing), with pixel areas aligned
  (the same conventions as Pillow, whose float results it reproduces). Alpha is premultiplied while filtering;
  `LinearLight` filters 8 and 16 bit sRGB in linear light. Integer results are rounded and clamped, float results are not.
- The workspace is a small ring of filtered rows per thread, independent of the image size. The box filter at power of two
  factors on 8 and 16 bit samples sums in integers, with the same bytes as the float path. `ResizeOptions.CancellationToken`
  cancels between rows.
- `Downsample(img, 2)` (or `Downsample(img, fx, fy, alphaWeighted: true)`) averages factor x factor blocks; the size is
  rounded up, the last blocks are cut. At multiples it equals `Resize` with `Box`.
- Samples and channels: `Retype` (UInt8 / UInt16 / Float16 / Float32 / Float64, rescaled and rounded), `ConvertChannels`
  (gray, gray + alpha, RGB, RGBA; Rec. 601 luma for gray), `Premultiply` / `Unpremultiply`.
- Lossless: `Crop`, `FlipHorizontal`, `FlipVertical`, `Rotate90`, `Rotate180`, `Rotate270`, `Transpose`, `Transverse`,
  `ApplyOrientation`.
- Warps (`Rotate`, `WarpAffine`, `WarpPerspective`, `Remap`) interpolate with any filter and border mode
  (`Constant`, `Clamp`, `Wrap`, `Mirror`, `MirrorRepeat`).
- Multi-threaded and vectorised: 4000x3000 RGB to 1000x750 with Lanczos3 takes about 30 ms on 8 cores (Pillow: 125 ms,
  one thread).

`ImageCodec.DecodeAll` returns every page of a TIFF, every frame of an animated GIF, PNG, WebP, JPEG XL or AVIF sequence, every layer / face of a
DDS, KTX or PVR texture (every slice of a KTX / PVR / .astc volume), every image of an ICO / ICNS icon and every step of
an animated cursor.

## Aardvark

`Aardvark.PixImage.PixCodec` provides an `IPixLoader`. It registers itself on `Aardvark.Init()`,
or call `PixCodec.Init()`.

> **Global registration.** `Aardvark.Init()` runs `PixCodec.Init()` (`[OnAardvarkInit]`), which installs the loader and
> `PixCodecProcessor` with the highest priority. `PixImage.Load`, `Resized` and so on then use PixCodec in processes that
> call `Aardvark.Init()`, and whatever else is registered in those that do not (command-line tools, tests). Code that
> must behave the same everywhere calls `PixCodec.Load` and `ImageOps` directly.

```csharp
using Aardvark.Data;
PixCodec.Init();
var pi = PixImage.Load("dem.tif");                  // PixImage<float>, wraps the decoded array without copying
pi.Save("dem.exr");
```

Without registering anything, `PixCodec` loads straight into PixImages. Like the registered loader it always returns
upright images (orientation applied) whose volume has the dense image layout: channels innermost, then x, then y, top
row first, starting at index 0 (no y-flip; the same convention as the other Aardvark loaders).

```csharp
PixImage img       = PixCodec.Load("photo.jpg");                         // PixImage<byte>, upright
PixImage<float> f  = PixCodec.Load<float>("scan.tif");                    // converted if the file is not float
PixImage[] pages   = PixCodec.LoadAll("stack.tif");                       // pages or animation frames
ImageInfo info     = PixCodec.GetInfo("dem.tif");
PixCodec.Save(img, "out.webp");
PixImage p = rawImage.ToPixImage();   RawImage r = pixImage.ToRawImage();
PixImage<byte> b   = PixCodec.ToPixImage<byte>(rawImage);                 // no copy when the sample type matches
PixImage asStored  = PixCodec.Load("photo.jpg", new ImageDecodeOptions { ApplyOrientation = false });
PixImageMipMap mm  = PixCodec.LoadMipmap("tex.dds");                     // also PixImageMipMap.Load (IPixMipmapLoader)
PixCube cube       = PixCodec.LoadCube("sky.dds");                        // DDS, KTX or PVR cube map, every side with its mip chain
PixCodec.SaveDds(cube, stream);
PixCodec.SaveDds(volume, stream, new DdsEncodeOptions { Compression = DdsCompression.BC1 });   // PixVolume
PixCodec.SaveKtx(cube, stream, new KtxEncodeOptions { Compression = TextureCompression.BC7 });
PixVolume vol      = PixCodec.LoadVolume("ct.ktx2");                      // DDS, KTX, PVR or .astc volume, or NRRD / NIfTI / MHA / FITS
PixVolume ct       = PixCodec.LoadVolume("ct.nrrd", out PhysicalMetadata? meta);   // + spacing, origin, direction, header
PixVolume<float> t = PixCodec.LoadVolume<float>("fmri.nii.gz", frame: 7);  // one volume of a 4D series, converted
PixImage sky       = PixCodec.Load("m31.fits", out var wcs);             // 2D, top row first, with the FITS keywords
PixCodec.SaveVolume(ct, "ct.nii.gz", meta);                              // .nrrd .nhdr .nii .nii.gz .mha .mhd .fits .dds .ktx2
RawVolume rv = ct.ToRawVolume(meta);  PixVolume pv = rv.ToPixVolume();
PixCodec.SavePvr(volume, stream);  PixCodec.SaveAstc(volume, stream);  PixCodec.SaveIcns(images, stream);  PixCodec.SaveAni(steps, stream);
PixCodec.WriteMetadata("a.jpg", "a.jpg", new MetadataEdit { Gps = new GpsEdit { Latitude = 47.07, Longitude = 15.44 } });
PixImage srgb      = PixCodec.LoadSrgb("adobe-rgb.jpg");                 // converted through the embedded ICC profile
PixCodec.Save(img, "out.qoi");                                           // .hdr .pfm .ppm .pgm .pbm .pam .ico .ani .icns .qoi .ktx2 .pvr .astc .nrrd .nii .mha .fits .dpx .cin .jls too
PixCodec.SaveDpx(img, stream, new DpxEncodeOptions { BitDepth = 10 });   // 10 bit filled method A, big endian
```

`PixImage.Save` of Aardvark.Base only accepts the file extensions Aardvark.Base knows; `PixCodec.Save` takes every format
above.

`Init()` also registers `PixCodecProcessor`, so `PixImage.Scaled`, `Resized`, `Rotated`, `Remapped` and
`PixImageMipMap.Create` use PixCodec for every sample type (ImageInterpolation `Near`, `Linear`, `Cubic`, `Lanczos`,
`SuperSample` map to nearest, bilinear, bicubic, Lanczos3 and box). Extension methods add the other filters and options:
`pi.Resized(w, h, ResampleFilter.Mitchell)`, `pi.Resized(w, h, new ResizeOptions { LinearLight = true })`,
`pi.Rotated(angle, true, new WarpOptions { ... })`, `pi.WarpedAffine(...)`, `pi.WarpedPerspective(...)`,
`pi.Oriented(orientation)`. The alpha channel is taken from the PixImage format (BGRA works as is, premultiplied
RGBP/BGRP are not premultiplied again). Two channel images without colour meaning (BC5 and RG textures, two band
rasters) load as `Col.Format.RG`.

Half float EXR channels load as `PixImage<float>`. EXR is saved as FLOAT, so float images round-trip exactly; 8 bit and
half images as HALF. Luminance / chroma applies to RGB(A) of even size; gray and odd sized images are saved without it.
BGR(A) images are saved as RGB(A). The existing `PixSaveParams`
classes (JPEG quality, PNG level, TIFF / EXR compression, WebP quality / lossless, JPEG 2000) are honoured.

## Building and releasing

Dependencies are managed with Paket: `./build.sh` (or `build.cmd`) restores the tools and packages and builds; it needs
the .NET 10 SDK (and the .NET 8 runtime for the net8.0 tests). A release is a new section on top of
`RELEASE_NOTES.md` pushed to `main`: the Publish workflow tests, packs with aardpack and pushes the package to nuget.org
and GitHub Packages. Notes for coding agents are in `AGENTS.md`.

## Tests

`./build.sh`, then `dotnet test -c Release`. Decoders are checked against reference decoders (libjpeg-turbo, libpng,
libtiff, libwebp, the OpenEXR library, Chromium, ...) through `.ref` sidecars generated by `tests/oracle/gen_*.py`;
encoders are checked by decoding their output with the same reference tools. Running the encoder cross-checks needs
`python3` with Pillow, numpy, tifffile, imagecodecs, opencv and OpenEXR; without python they are skipped. Some extra
checks use tools fetched by `tests/oracle/gen_png.py --fetch-tools`. Written KTX files are checked with KTX-Software's
`ktx validate` when `ktx` is on the PATH or `PIXCODEC_KTX` points to it; ETC1S / UASTC files written by PixCodec are also decoded by basisu
(`PIXCODEC_BASISU` or `basisu` on the PATH) and compared with PixCodec's decoders. PVR fixtures come from Imagination's
PVRTexToolCLI (`tests/oracle/gen_pvr.py`, which runs it locally or on a Windows machine over ssh) and its own decodes; .astc
fixtures and the live checks of written .astc files use ARM's astcenc (`ASTCENC`); ICNS fixtures are cross checked with
Pillow and, for the ARGB elements, macOS (`iconutil` / `sips` over ssh, `ICNS_MAC_HOST`). ICC colour conversions are checked
against LittleCMS 2 float transforms (`tests/oracle/gen_icc.py` runs liblcms2 through ctypes) on profiles LittleCMS made
itself, on PixCodec's own profiles read back by LittleCMS, and on the ICC's sRGB v4 and the CC0 Compact-ICC-Profiles of
the test-data repository.

The volume formats are checked against pynrrd, nibabel, SimpleITK and astropy: `tests/oracle/gen_volume.py` writes the
fixtures with them (and hand-written variants none of them writes), and the encoder tests read PixCodec's NRRD, NIfTI,
MetaImage and FITS files back with them (`tests/oracle/decode_volume.py`). They are looked for in a virtual environment
`.tmp/venv` of the repository (`python3 -m venv .tmp/venv && .tmp/venv/bin/pip install numpy pynrrd nibabel SimpleITK
astropy`) or the python given by `PIXCODEC_PYTHON`; without them those checks are skipped.

Metadata is checked against ExifTool (run, never linked): `tests/oracle/gen_meta.py` writes the reading fixtures,
`tests/oracle/gen_meta_write.py` stores golden `WriteMetadata` outputs with ExifTool's reading and validation of them
(`tests/fixtures/metadata-write`). With ExifTool installed (`$EXIFTOOL` or `~/.local/share/exiftool/exiftool`) the
writing tests also run it live, including a tag by tag comparison of rewritten camera files with maker notes.

Everything in `tests/fixtures` is generated by `tests/oracle/gen_*.py` from synthetic images. Third-party test files
(the PngSuite, openexr-images samples, the lists of the libwebp vectors, the JPEG 2000 and JPEG XL conformance suites, the libaom AV1 test vectors and Argon streams, the
libtiff / GDAL / OpenSlide samples and ICC profiles) are in a separate test-data repository,
https://github.com/krauthaufen/Aardvark.PixImage.PixCodec.TestData, with their licences (clone it next to this one).
The tests look for it in `PIXCODEC_TESTDATA` or next to this repository (`../Aardvark.PixImage.PixCodec.TestData`);
without it the tests that need it are skipped. CI checks it out and runs the whole suite on Linux, Windows and macOS.

## Licence

MIT, see `LICENSE`. The codecs are written from the format specifications; no code of other implementations is used.
Third-party data tables in the library are listed in `THIRD-PARTY-NOTICES.md`.
