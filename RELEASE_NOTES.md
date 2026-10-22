### 1.0.3
* Encoding allocates next to nothing: JPEG is converted per MCU row with compact blocks between the two passes, deflate stream buffers are reused, TIFF strips are pooled.

### 1.0.2
* Decoding allocates about the size of the output: streamed JPEG rows and rotation, pooled working buffers for JPEG 2000, progressive JPEG, WebP and EXR.
* Files and streams are decoded without copying them into a new array first.
* Fixed all build warnings.

### 1.0.1
* Loader: EXR saves float32 images as FLOAT, so they round-trip exactly (were rounded to half).
* Loader: EXR luminance/chroma falls back to plain storage for gray images and odd sizes instead of throwing.

### 1.0.0
* AVIF decoder: full AV1 (bit exact with dav1d, passes Argon and the libaom vectors), HEIF grids, overlays, alpha, depth, thumbnails, sequences, gain maps.
* JPEG XR decode and encode, every pixel format of the standard, bit exact with jxrlib.
* DNG decode (raw access and rendering with opcodes, colour calibration, demosaicing) and encode; `PixCodec.LoadDng`.
* JPEG HDR gain maps (Ultra HDR, ISO 21496-1) read and written, MPO / MPF; `PixCodec.LoadHdr`.
* TIFF: DNG lossy JPEG and JPEG XL tiles, X2 / X4 predictors.
* JPEG XL: fixes from a check against ISO/IEC 18181-1:2024 and -2:2026, gain map box, HDR encoding.

### 1.0.0-prerelease0002
* JPEG XL: decoder (all conformance files pass, within 2x of libjxl) and encoder (lossless, lossy VarDCT, metadata, animation).
* JPEG-LS decode and encode.
* New formats: PCX/DCX, SGI, Sun Raster, XBM, XPM, WBMP, ICNS, ANI, .astc, PVR, DPX, Cineon.
* Volume formats NRRD, NIfTI, MetaImage and FITS with physical metadata, via `DecodeVolume` / `LoadVolume`.
* `GetInfo` on streams of unknown format no longer reads the whole stream.
* CI runs every test with all reference tools on Windows, macOS and Linux.

### 1.0.0-prerelease0001
* First release: pure managed decoders and encoders for PNG, JPEG, TIFF, GIF, OpenEXR, WebP, JPEG 2000, JBIG, BMP, TGA, DDS, KTX, Radiance HDR, PNM, QOI and ICO, LERC decode only.
* Aardvark loader `Aardvark.Data.PixCodec` with PixImage, mip map, cube map and volume loading and saving.
* Metadata (EXIF, XMP, GPS, ICC) readable without decoding the image, lossless metadata writing, ICC colour management, resizing and other image operations.
