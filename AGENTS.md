# AI Agent Guide

Repository rules for coding agents working on Aardvark.PixImage.PixCodec: pure managed (no native code) image codecs for
.NET 8 and .NET 10, MIT licensed, with an Aardvark `PixImage` loader on top.

## Licence rule (most important)

- Everything in this repository is MIT. Write codecs **from the specifications only**. Never port, copy or read the
  code of other implementations (libjpeg, libpng, libtiff, libwebp, OpenEXR, astcenc, ...), whatever their licence.
- Third-party **data** tables only from permissive sources, each with an entry in `THIRD-PARTY-NOTICES.md`.
- Other tools (GPL ones included) may only be **run** as test oracles, never linked or shipped.
- Third-party test files never go into this repository: they live in the separate test-data repository
  (see Tests).
- LERC is decode only (Esri patents claim the compression). Never add a LERC encoder or encoder helpers.

## Commands

| Task | Command |
|------|---------|
| Restore + build | `./build.sh` or `build.cmd` (tool restore, `dotnet paket restore`, Release build of the solution) |
| Test all | `dotnet test Aardvark.PixImage.PixCodec.slnx -c Release` (about 7 minutes, 15k tests per framework) |
| Test some | `dotnet test tests/Aardvark.PixImage.PixCodec.Codecs.Tests -c Release -f net10.0 --filter "FullyQualifiedName~Png"` |
| Pack locally | `dotnet aardpack Aardvark.PixImage.PixCodec.slnx --notag` (writes `bin/pack`; does not publish without a token) |

The solution file is `Aardvark.PixImage.PixCodec.slnx`. `global.json` needs a .NET 10 SDK; the .NET 8 runtime is needed
to run the net8.0 tests.

## Dependencies (Paket)

- Paket, not `dotnet add package`. Change `paket.dependencies`, then `dotnet paket install`. Never edit `paket.lock`
  by hand.
- The library depends only on `Aardvark.Base.Tensors`. Do not add further runtime dependencies.
- Test packages are in the `Test` group.

## Layout

| Path | Contents |
|------|----------|
| `src/Aardvark.PixImage.PixCodec/Core` | `RawImage`, `RawVolume` / `PhysicalMetadata` (volumes), `ImageCodec` (format detection, `Decode`, `GetInfo`, `DecodeVolume`), managed zlib / zstd / xz / gzip / bzip2 / Brotli (decode) |
| `src/Aardvark.PixImage.PixCodec/<Format>` | one folder per format: `Png`, `Jpeg`, `JpegLs`, `Tiff`, `Gif`, `Exr`, `Webp`, `Jp2`, `Jxl` (JPEG XL; encoder in the JxlEncoder*.cs files), `JpegXr` (JPEG XR), `Jbig`, `Bmp`, `Tga`, `Dds`, `Ktx`, `Pvr`, `Astc`, `Hdr`, `Pnm`, `Qoi`, `Ico` (with ANI), `Icns`, `Lerc`, `Pcx` (+ DCX), `Sgi`, `SunRaster`, `Xbm`, `Xpm`, `Wbmp`, `Nrrd`, `Nifti`, `MetaImage`, `Fits`, `Envi` (.hdr + sample file), `Dicom` (files, DICOMDIR, series), `Dpx`, `Cineon` |
| `src/Aardvark.PixImage.PixCodec/Metadata` | EXIF / XMP / IPTC / GPS reading without decoding pixels, lossless metadata writing |
| `src/Aardvark.PixImage.PixCodec/Color` | ICC profiles and colour management |
| `src/Aardvark.PixImage.PixCodec/Processing` | `ImageOps`: resize filters, warps, orientation, transfer functions |
| `src/Aardvark.PixImage.PixCodec/PixCodecLoader.cs` | Aardvark adapter: `PixCodec`, `PixCodecLoader` (`IPixLoader`) |
| `src/Aardvark.PixImage.PixCodec/PixCodecProcessor.cs` | `PixCodecProcessor` (`IPixProcessor`) |
| `tests/Aardvark.PixImage.PixCodec.Codecs.Tests` | codec tests (namespace `Aardvark.ImageCodecs.Tests`) |
| `tests/Aardvark.PixImage.PixCodec.Tests` | loader tests against Aardvark `PixImage` |
| `tests/fixtures` | synthetic fixtures with reference decodes (`.ref` and other sidecars) |
| `tests/oracle` | `gen_*.py` scripts that create fixtures and run the reference tools |

## Names and conventions

- Package and repository: `Aardvark.PixImage.PixCodec`. Loader classes are in namespace `Aardvark.Data` (`PixCodec`,
  `PixCodecLoader`, `PixCodecProcessor`), like the other Aardvark loaders; codec code is in `Aardvark.ImageCodecs`.
- Never create a namespace `Aardvark.PixImage.*`: it hides the `PixImage` class for code in any `Aardvark.*` namespace
  (CS0118).
- Decoders always return upright images: EXIF / TIFF orientation is applied by default (`ApplyOrientation = false` opts
  out), and `ImageInfo` reports the upright size. Users must never have to apply orientation themselves.
- PixImages from the loader always have the dense layout (channel fastest, then x, then y; top row first, no y flip).
- JPEG trellis quantization stays off by default.
- Little-endian hosts only.
- Environment variables use the prefix `PIXCODEC_`.

## Code pitfalls seen here

- `switch` patterns on `byte[]` / `ushort[]` / `uint[]` also match `sbyte[]` / `short[]` / `int[]` (CLR array
  covariance): switch on `SampleType` instead.
- `Vector<T>.Count` is 8 floats with AVX2 but 4 on ARM (NEON) and on x64 without AVX2. Size buffers from the actual
  count, never from the AVX2 width. Several classes use `[SkipLocalsInit]`, so `stackalloc` memory is not zeroed.
- .NET 10 ARM64 JIT bug: in optimized code, `v[k] = x` on a `Vector4` local that started as `Vector4.Zero` / `One`
  writes element 0 whatever `k` is. Write such elements through a ref (see `AstcEncoder.SetElement`).
- Test code must not assume `/` as path separator, or a particular build of an oracle tool (ImageMagick Q16 versus
  HDRI).

## Tests

- Decoders are checked against reference decoders through the `.ref` sidecars that `tests/oracle/gen_*.py` writes;
  encoders by decoding their output with the reference tools. Tests whose tool or python package is missing skip
  themselves (or check less).
- Third-party test files are in https://github.com/krauthaufen/Aardvark.PixImage.PixCodec.TestData. Tests find it via
  `PIXCODEC_TESTDATA` or as `../Aardvark.PixImage.PixCodec.TestData`, and skip without it. Some files there are
  downloaded, not committed: `fetch/*.sh` there and `tests/oracle/fetch_*.py`.
- `PIXCODEC_REQUIRE_ORACLES=1` turns every such skip into a failure that names the missing tool, package or file;
  CI runs with it. `PIXCODEC_ORACLE_EXEMPT` (comma separated gate names, e.g. `pamtopnm,python:lerc`) exempts gates
  a platform cannot provide. Every new oracle gate must go through `Oracles.Have` / `Oracles.Missing`,
  `TestUtil.PythonAvailable`, `MiscFormatTests.ToolAvailable`, `TestUtil.CannotVerify` (exit status 77 of an oracle
  script: `pxref.missing(name)` for a missing tool, `pxref.unverifiable(why)` for a file no reference can read) or
  `[TestDataFact]` / `[TestDataTheory]`.
- Oracle locations: python (`python3`, else `python`) with `tests/oracle/requirements.txt` (pydicom with the pylibjpeg
  plugins and pyjpegls for DICOM, rasterio and pyproj for ENVI: `tests/oracle/envi_read.py` is what GDAL reads); the volume oracles use
  `PIXCODEC_PYTHON` or `.tmp/venv` if present. Tools on the PATH: `magick` (ImageMagick 7), `zstd`, `7z`, `ffmpeg`,
  netpbm (`pamtopnm`, `pngtopam`, `sgitopnm`, `wbmptopbm`), `pngcheck`, `cjxl` / `djxl`. By variable: `ASTCENC`,
  `PIXCODEC_KTX`, `PIXCODEC_BASISU`, `EXIFTOOL`, `PIXCODEC_ULTRAHDR` (libultrahdr's `ultrahdr_app`), `EXR_ORACLE` (`tests/oracle/exr_oracle.cpp`), `PIXCODEC_JBIGKIT` (jbigkit's
  pbmtools folder), `PIXCODEC_ISO_JPEG` (the ISO libjpeg's `jpeg`), `PIXCODEC_JXRLIB` (jxrlib's folder with `JxrEncApp` / `JxrDecApp`; else the PATH), `PIXCODEC_CHARLS` (a CharLS library, where imagecodecs
  has none of its own: Windows).
- CI (`.github/workflows/build.yml`, and the test step of `publish.yml`): the `testdata` job fetches the downloaded test
  data on Linux once and caches it for all OSes (`.github/actions/testdata`, keyed on the fetch scripts);
  `.github/actions/oracles` installs the python packages, `tests/oracle/ci/install.sh` the released tools and
  `tests/oracle/ci/build.sh` the ones built from source (Windows: in MSYS2; netpbm from Cygwin), cached per OS.
- Every change gets a regression test. Run the suite on both frameworks; CI also runs Windows and macOS (ARM64), which
  have caught bugs that x64 Linux does not show.

## Releases

- Version and release notes come from `RELEASE_NOTES.md` (Aardvark.Build). A release is a new section on top of that
  file pushed to `main`; `.github/workflows/publish.yml` then tests, packs with aardpack and pushes to nuget.org and
  GitHub Packages. Never remove or rewrite older release notes entries.
- Package metadata lives in `src/Aardvark.PixImage.PixCodec/paket.template`.
- Commit messages: one short line.
