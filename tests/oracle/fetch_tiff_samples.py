"""Fetches real-world TIFF samples for the optional TiffTests.RealWorldSamples test and writes third-party references.

usage: python3 fetch_tiff_samples.py       (from tests/oracle; needs network access once)

The files are libtiff's classic sample pictures (pics-3.8.0.tar.gz from download.osgeo.org). They come without an
explicit licence, so they are not committed anywhere: this script downloads the archive, checks its SHA-256 and
unpacks it into downloads/tiff-samples of the test-data repository (git-ignored there; see pxref.testdata_dir). Among them are the only files from actual old
writers we know for some layouts: old-style JPEG in tiles (NeXT, with studio range ReferenceBlackWhite) and strips
(HP), SGI LogL / LogLuv 24 / LogLuv 32 from Greg Ward Larson, ThunderScan, Group 3 2D fax, planar LZW.

References, per file: libtiff via OpenCV where it returns the stored samples (BGR swapped), libtiff via Pillow or
ImageMagick where it has to interpret (palette, bilevel, YCbCr, associated alpha), libtiff floats (ctypes) for the
log encodings. `.tol` = allowed difference per sample, `.mean` = allowed mean difference.

With --aperio it also fetches JP2K-33003-1.svs (64 MB, CC0) from the OpenSlide test data: an Aperio slide whose tiles
are JPEG 2000 codestreams with YCbCr 4:2:2 components (compression 33003), for TiffTests.AperioJpeg2000Sample.
References: tifffile + OpenJPEG (which converts the subsampled YCbCr to RGB), for directory 3 and a region of directory 2.

With --gdal it fetches the GDAL 3.11.0 autotest archive (18 MB, download.osgeo.org) and unpacks the 363 TIFF files of
gcore/data into downloads/gdal-samples of the test-data repository, for TiffTests.GdalAutotestSamples: files GDAL
itself writes (LERC with all its variants and masks, ZSTD, LZMA, WebP, JPEG, sparse and cloud optimized layouts, complex
and odd sample types) and its collection of broken files. References: the installed GDAL (rasterio),
stored samples band by band.
Files where GDAL interprets the samples differently from Aardvark.PixImage.PixCodec's raw decoding get no reference (see GDAL_SKIP).
"""
import hashlib
import io
import os
import subprocess
import sys
import tarfile
import urllib.request
import warnings

import numpy as np

warnings.filterwarnings('ignore')
os.environ['OPENCV_LOG_LEVEL'] = 'SILENT'
import cv2  # noqa: E402
from PIL import Image  # noqa: E402

import gen_tiff as g  # noqa: E402
from pxref import write_ref, testdata_dir  # noqa: E402

URL = 'https://download.osgeo.org/libtiff/pics-3.8.0.tar.gz'
SHA256 = 'e0e34732b61e1ce49eff2c7a079994c856d2a5f772f5228c84678272bc6829a9'
SVS_URL = 'https://openslide.cs.cmu.edu/download/openslide-testdata/Aperio/JP2K-33003-1.svs'
SVS_SHA256 = '6205ccf75a8fa6c32df7c5c04b7377398971a490fb6b320d50d91f7ba6a0e6fd'
SVS_REGION = (1024, 768, 700, 500)   # x, y, width, height in directory 2
GDAL_URL = 'https://download.osgeo.org/gdal/3.11.0/gdalautotest-3.11.0.tar.gz'
GDAL_SHA256 = '121cc8c0e4b1223f511b0e0ed0fd9706d55656e5b15e75cdf5d5d36913f26c45'
# files GDAL reads, but not as the samples Aardvark.PixImage.PixCodec returns with ConvertToRgb = false and BitScaling = None
GDAL_SKIP = {
    'bug4468.tif': 'associated alpha: Aardvark.PixImage.PixCodec un-premultiplies',
    'cielab.tif': 'GDAL converts Lab to RGBA',
    'rgbsmall_cmyk.tif': 'GDAL converts CMYK to RGBA',
    'slim_g4.tif': 'palette: GDAL returns indices',
    'test_average_palette.tif': 'palette: GDAL returns indices',
    'vrtmisc16_tile1.tif': 'palette: GDAL returns indices',
    'vrtmisc16_tile2.tif': 'palette: GDAL returns indices',
    'uint16_sgilog.tif': 'GDAL returns the 16-bit log values, Aardvark.PixImage.PixCodec linear floats',
    'int24.tif': 'signed 24-bit samples: GDAL returns them as UInt32 without sign extension',
    'zackthecat.tif': 'old-style JPEG: libtiff upsamples the chroma differently (see the libtiff samples)',
    'size_of_stripbytecount_at_1_and_lower_than_stripcount.tif': 'too few StripByteCounts: GDAL reads the strips without one as empty, Aardvark.PixImage.PixCodec recovers them',
    'size_of_stripbytecount_lower_than_stripcount.tif': 'too few StripByteCounts: GDAL reads the strips without one as empty, Aardvark.PixImage.PixCodec recovers them',
}
OUT = testdata_dir('downloads', 'tiff-samples')


def magick(path, fmt, shape):
    r = subprocess.run(['magick', path, '-depth', '8', fmt + ':-'], capture_output=True)
    assert r.returncode == 0 and len(r.stdout) == int(np.prod(shape)), (path, r.stderr[:200])
    return np.frombuffer(r.stdout, np.uint8).reshape(shape)


def pillow(path, mode):
    a = np.asarray(Image.open(path).convert(mode))
    return a if a.ndim == 3 else a[:, :, None]


def aperio():
    import tifffile
    path = os.path.join(OUT, 'JP2K-33003-1.svs')
    if not os.path.exists(path):
        data = urllib.request.urlopen(SVS_URL, timeout=600).read()
        assert hashlib.sha256(data).hexdigest() == SVS_SHA256, 'unexpected slide content'
        with open(path, 'wb') as f:
            f.write(data)
    x, y, w, h = SVS_REGION
    with tifffile.TiffFile(path) as t:
        assert t.pages[3].compression == 33003 and t.pages[2].compression == 33003
        write_ref(path + '.3.ref', t.pages[3].asarray())
        write_ref(path + '.2.region.ref', t.pages[2].asarray()[y:y + h, x:x + w])
    print('JP2K-33003-1.svs   tifffile (OpenJPEG)')


def gdal():
    import logging
    import rasterio
    import tifffile
    logging.disable(logging.CRITICAL)
    out = os.path.join(OUT, '..', 'gdal-samples')
    os.makedirs(out, exist_ok=True)
    data = urllib.request.urlopen(GDAL_URL, timeout=600).read()
    assert hashlib.sha256(data).hexdigest() == GDAL_SHA256, 'unexpected archive content'
    names = []
    with tarfile.open(fileobj=io.BytesIO(data)) as t:
        for m in t.getmembers():
            parts = m.name.split('/')
            if m.isfile() and m.name.endswith('.tif') and parts[1:3] == ['gcore', 'data'] and (len(parts) == 4 or parts[3] == 'gtiff'):
                name = '_'.join(parts[3:])
                with open(os.path.join(out, name), 'wb') as f:
                    f.write(t.extractfile(m).read())
                names.append(name)
    written = 0
    for name in sorted(names):
        path = os.path.join(out, name)
        if os.path.exists(path + '.ref'):
            os.remove(path + '.ref')
        if name in GDAL_SKIP:
            continue
        try:
            with tifffile.TiffFile(path) as t:
                pg = t.pages[0]
                pm, bits, fmt = int(pg.photometric) if 262 in pg.tags else 1, pg.bitspersample, int(pg.sampleformat)
            with rasterio.open(path) as ds:
                a = ds.read()
        except Exception:
            # GDAL does not read it (most of these are its broken-file tests); JPEG XL (compression 50002, or 52546 as
            # DNG 1.7 stores it) needs a GDAL built with libjxl: tifffile + imagecodecs read it instead
            try:
                with tifffile.TiffFile(path) as t:
                    pg = t.pages[0]
                    if int(pg.compression) not in (50002, 52546):
                        continue
                    a = pg.asarray()
                    if pg.planarconfig == 2 and pg.samplesperpixel > 1:
                        a = np.moveaxis(a, 0, -1)
                write_ref(path + '.ref', np.ascontiguousarray(a))
                written += 1
            except Exception:
                pass
            continue
        if a.dtype.kind == 'c':
            # complex samples: real and imaginary part as two channels
            # (numpy has no complex integers; the TIFF tags say what the parts are)
            part = (np.int16 if bits == 32 else np.int32) if fmt == 5 else (np.float32 if bits == 64 else np.float64)
            a = np.stack([a.real, a.imag], -1)
            a = np.moveaxis(a, 0, 2).reshape(a.shape[1], a.shape[2], -1).astype(part)
        else:
            a = np.moveaxis(a, 0, -1)
        if a.size == 0 or a.size > 50_000_000:
            continue
        if pm == 0 and a.dtype.kind == 'u':
            # MinIsWhite: Aardvark.PixImage.PixCodec inverts the colour sample of unsigned data, GDAL returns it as stored
            a = a.copy()
            a[..., 0] = ((1 << bits) - 1) - a[..., 0]
        write_ref(path + '.ref', np.ascontiguousarray(a))
        written += 1
    print(f'{len(names)} GDAL autotest files, {written} with a reference from GDAL {rasterio.__gdal_version__}')


def main():
    if OUT is None:
        return 1
    os.makedirs(OUT, exist_ok=True)
    if '--aperio' in sys.argv:
        aperio()
    if '--gdal' in sys.argv:
        gdal()
    if '--aperio' in sys.argv or '--gdal' in sys.argv:
        return
    data = urllib.request.urlopen(URL, timeout=120).read()
    assert hashlib.sha256(data).hexdigest() == SHA256, 'unexpected archive content'
    with tarfile.open(fileobj=io.BytesIO(data)) as t:
        for m in t.getmembers():
            if m.isfile() and m.name.endswith('.tif'):
                with open(os.path.join(OUT, os.path.basename(m.name)), 'wb') as f:
                    f.write(t.extractfile(m).read())
    lib = g.LibTiff()
    p = lambda n: os.path.join(OUT, n)

    def ref(name, arr, who, tol=0, mean=None):
        write_ref(p(name) + '.ref', arr)
        if tol:
            g.write_tol(p(name), tol)
        if mean is not None:
            with open(p(name) + '.mean', 'w') as f:
                f.write(repr(float(mean)))
        print(f'{name:18s} {who}')

    # stored samples as they are
    for n in ('caspian.tif', 'ladoga.tif', 'oxford.tif', 'pc260001.tif', 'quad-lzw.tif', 'quad-tile.tif', 'jim___cg.tif'):
        ref(n, g.ref_tifffile(p(n)), 'tifffile')
    # libtiff's interpretation: MinIsWhite, bilevel, palette, fax, YCbCr, JPEG
    for n, mode in (('cramps.tif', 'L'), ('fax2d.tif', 'L'), ('g3test.tif', 'L'), ('jim___ah.tif', 'L'), ('jim___dg.tif', 'L'),
                    ('jim___gg.tif', 'L'), ('jello.tif', 'RGB'), ('quad-jpeg.tif', 'RGB'), ('ycbcr-cat.tif', 'RGB')):
        ref(n, pillow(p(n), mode), 'libtiff (Pillow)', tol=2 if n == 'ycbcr-cat.tif' else 0)
    ref('cramps-tile.tif', magick(p('cramps-tile.tif'), 'gray', (607, 800, 1)), 'libtiff (ImageMagick)')
    ref('dscf0013.tif', g.ref_cv2(p('dscf0013.tif')), 'libtiff RGBA (OpenCV)', tol=2)
    ref('strike.tif', pillow(p('strike.tif'), 'RGBA'), 'libtiff (Pillow), associated alpha undone', tol=2)
    # old-style JPEG: libtiff box-upsamples the chroma, Aardvark.PixImage.PixCodec uses libjpeg's smooth upsampling
    ref('smallliz.tif', magick(p('smallliz.tif'), 'rgb', (160, 160, 3)), 'libtiff (ImageMagick), chroma upsampling differs', tol=48, mean=2.5)
    ref('zackthecat.tif', magick(p('zackthecat.tif'), 'rgb', (213, 234, 3)), 'libtiff (ImageMagick), chroma upsampling differs', tol=48, mean=2.5)
    # log encodings as floats
    fmt = [(lib.SGILOGDATAFMT, 0)]
    ref('off_l16.tif', lib.read(p('off_l16.tif'), np.float32, (225, 333, 1), pseudo=fmt), 'libtiff float', tol=1e-4)
    for n in ('off_luv24.tif', 'off_luv32.tif'):
        ref(n, cv2.cvtColor(lib.read(p(n), np.float32, (225, 333, 3), pseudo=fmt), cv2.COLOR_XYZ2RGB), 'libtiff float XYZ + cv2 XYZ2RGB', tol=2e-3)
    # ThunderScan: the last strip of this file holds 36 of its 39 rows and libtiff 4.7 gives up on it; the first
    # five strips (320 rows) are its reading, the rest of the reference is the white Aardvark.PixImage.PixCodec fills in
    t = lib.lt.TIFFOpen(p('text.tif').encode(), b'r')
    raw = np.zeros((320, 756), np.uint8)
    for i in range(5):
        assert lib.lt.TIFFReadEncodedStrip(t, i, raw.ctypes.data + i * 64 * 756, 64 * 756) == 64 * 756
    lib.lt.TIFFClose(t)
    px = np.stack([raw >> 4, raw & 15], -1).reshape(320, 1512)
    full = np.full((359, 1512, 1), 255, np.uint8)
    full[:320, :, 0] = 255 - px * 17
    write_ref(p('text.tif') + '.ref', full)
    with open(p('text.tif') + '.rows', 'w') as f:
        f.write('320')
    print(f'{"text.tif":18s} libtiff strips (first 320 rows only)')


if __name__ == '__main__':
    sys.exit(main())
