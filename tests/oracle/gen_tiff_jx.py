"""Generates the JPEG XL and JPEG XR compressed TIFF fixtures (tests/fixtures/tiff/jxl, tests/fixtures/tiff/jxr) and
their PXRF reference sidecars.

usage: python3 gen_tiff_jx.py            (from tests/oracle; deterministic, overwrites both fixture directories)

Needs tifffile + imagecodecs (libjxl, jxrlib). Every file is written by tifffile, every .ref is what tifffile +
imagecodecs read back from it (with associated alpha un-premultiplied, as Aardvark.PixImage.PixCodec documents); for
lossless files that also has to be the array the file was written from. A `.tol` sidecar gives the allowed sample
difference of the lossy JPEG XL files (libjxl's
rounding of the XYB and DCT maths is not part of the standard; the lossy JPEG XR ones are integer exact).

What tifffile / imagecodecs store (found with this script's files and jxlinfo / the JPEG XR IFD):
- JPEG XL (50002; tifffile reads DNG's 52546 the same way): one complete image per strip / tile, a bare codestream
  for 8-bit samples, the ISO BMFF container for 16-bit and float ones. Contiguous samples are Gray, Gray+Alpha, RGB or
  RGB+Alpha (an "unspecified" fourth sample is coded as alpha too); every plane of a planar file is Gray. No predictor.
  Lossless by default, 8 / 16 bit unsigned, half and single floats. (More than four contiguous samples come out as an
  animation of one frame per row, which is imagecodecs' array convention and not something a reader can expect: not
  covered.)
- JPEG XR (34934; NDPI's 22610 holds the same): one complete JPEG XR file (Annex A container) per strip / tile, pixel
  formats 8bppGray, 24bppRGB, 32bppRGBA (jxrlib's, with the WIC GUID F5C7AD2D-...E9), 16bppGray, 48bppRGB, 64bppRGBA,
  16bppGrayHalf, 48bppRGBHalf, 64bppRGBAHalf, 32bppGrayFloat, 96bppRGBFloat (WIC GUID E3FED78F-...27),
  128bppRGBAFloat and n-channel formats for 5..8 contiguous 8 / 16 bit samples; planes are Gray. Lossless for
  integers by default; 32-bit floats keep JPEG XR's precision. Gray + alpha has no pixel format (only planar).
"""
import os
import shutil
import sys
import warnings

import numpy as np

from pxref import write_ref, test_pattern, missing

warnings.filterwarnings('ignore')
try:
    import tifffile
    import imagecodecs
except ImportError as e:
    missing('python:' + e.name)

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', 'fixtures', 'tiff')
W, H = 34, 21


def unpremultiply(a):
    """c = round(c * max / alpha) clamped; alpha 0 leaves the colour as stored (unsigned integers)."""
    m = np.iinfo(a.dtype).max
    al = a[:, :, -1:].astype(np.int64)
    col = a[:, :, :-1].astype(np.int64)
    v = np.minimum((col * m + al // 2) // np.maximum(al, 1), m)
    out = a.copy()
    out[:, :, :-1] = np.where(al == 0, col, v).astype(a.dtype)
    return out


def ref_tifffile(path):
    with tifffile.TiffFile(path) as t:
        p = t.pages[0]
        a = p.asarray()
        if p.planarconfig == 2 and p.samplesperpixel > 1:
            a = np.moveaxis(a, 0, -1)
        if a.ndim == 2:
            a = a[:, :, None]
        return a, p


def pattern(c, dt, seed=1):
    if np.dtype(dt).kind == 'f':
        # floats in 0..1 (lossy JPEG XL takes the nominal range), from the integer pattern
        return (test_pattern(W, H, c, np.uint16, seed).astype(np.float64) / 65535).astype(dt)
    return test_pattern(W, H, c, dt, seed)


def emit(sub, name, arr, photometric, comp, args=None, extrasamples=None, planar=False, tile=None, rps=None,
         byteorder='<', code=None, tol=0, lossy=False):
    d = os.path.join(OUT, sub)
    os.makedirs(d, exist_ok=True)
    path = os.path.join(d, name + '.tif')
    data = np.moveaxis(arr, -1, 0) if planar else (arr[:, :, 0] if arr.shape[2] == 1 else arr)
    kw = dict(photometric=photometric, compression=code or comp, compressionargs=args or {}, byteorder=byteorder,
              planarconfig='separate' if planar else 'contig', metadata=None)
    if extrasamples:
        kw['extrasamples'] = extrasamples
    if tile:
        kw['tile'] = tile
    if rps:
        kw['rowsperstrip'] = rps
    tifffile.imwrite(path, data, **kw)
    got, p = ref_tifffile(path)
    want = code or {'jpegxl': 50002, 'jpegxr': 34934}[comp]
    assert p.compression.value == want, (name, p.compression)
    lossy = lossy or tol > 0
    if not lossy and not np.array_equal(got, arr):
        diff = np.abs(got.astype(np.float64) - arr.astype(np.float64)).max()
        if not (arr.dtype == np.float32 and comp == 'jpegxr' and diff < 1e-4):
            raise AssertionError(f'{name}: tifffile reads other samples than written (max diff {diff})')
    if lossy:
        diff = np.abs(got.astype(np.float64) - arr.astype(np.float64)).max()
        print(f'  {name}: lossy, max difference to the source {diff:g}')
    if extrasamples and extrasamples[0] == 'assocalpha':
        got = unpremultiply(got)
    write_ref(path + '.ref', got)
    if tol:
        with open(path + '.tol', 'w') as f:
            f.write(repr(tol))
    print(f'{sub}/{name}.tif')


def gen_jxl():
    s, lossless = 'jxl', {'lossless': True}
    shutil.rmtree(os.path.join(OUT, s), ignore_errors=True)
    for dt, tag in [('uint8', 'u8'), ('uint16', 'u16'), ('float16', 'f16'), ('float32', 'f32')]:
        emit(s, f'{tag}_gray_strips', pattern(1, dt), 'minisblack', 'jpegxl', lossless, rps=8)
        emit(s, f'{tag}_rgb_tiled', pattern(3, dt), 'rgb', 'jpegxl', lossless, tile=(16, 32))
        emit(s, f'{tag}_rgba_tiled', pattern(4, dt), 'rgb', 'jpegxl', lossless, extrasamples=['unassalpha'], tile=(16, 16))
        emit(s, f'{tag}_rgb_planar', pattern(3, dt), 'rgb', 'jpegxl', lossless, planar=True, rps=16)
    emit(s, 'u8_graya_strips', pattern(2, 'uint8'), 'minisblack', 'jpegxl', lossless, extrasamples=['unassalpha'], rps=10)
    emit(s, 'u16_graya_tiled', pattern(2, 'uint16'), 'minisblack', 'jpegxl', lossless, extrasamples=['unassalpha'], tile=(16, 16))
    emit(s, 'u8_rgba_assoc_tiled', pattern(4, 'uint8'), 'rgb', 'jpegxl', lossless, extrasamples=['assocalpha'], tile=(16, 16))
    emit(s, 'u8_rgbx_unspecified', pattern(4, 'uint8'), 'rgb', 'jpegxl', lossless, extrasamples=['unspecified'], rps=21)
    emit(s, 'u8_5ch_planar', pattern(5, 'uint8'), 'minisblack', 'jpegxl', lossless, planar=True, tile=(16, 16))
    emit(s, 'u16_rgb_be', pattern(3, 'uint16'), 'rgb', 'jpegxl', lossless, byteorder='>', tile=(16, 16))
    emit(s, 'f32_gray_be', pattern(1, 'float32'), 'minisblack', 'jpegxl', lossless, byteorder='>', rps=21)
    # DNG's code outside of DNG
    emit(s, 'u8_rgb_52546', pattern(3, 'uint8'), 'rgb', 'jpegxl', lossless, code=52546, rps=12)
    # lossy (VarDCT, XYB): libjxl's rendering of the image
    emit(s, 'u8_rgb_lossy', pattern(3, 'uint8'), 'rgb', 'jpegxl', {'level': 85}, tile=(16, 32), tol=1)
    emit(s, 'u8_gray_lossy', pattern(1, 'uint8'), 'minisblack', 'jpegxl', {'distance': 1.5}, rps=16, tol=1)
    emit(s, 'u8_rgba_lossy', pattern(4, 'uint8'), 'rgb', 'jpegxl', {'level': 85}, extrasamples=['unassalpha'], rps=21, tol=1)
    emit(s, 'u16_rgb_lossy', pattern(3, 'uint16'), 'rgb', 'jpegxl', {'level': 85}, rps=21, tol=4)
    emit(s, 'f32_rgb_lossy', pattern(3, 'float32'), 'rgb', 'jpegxl', {'level': 85}, tile=(16, 16), tol=2e-5)
    emit(s, 'f16_gray_lossy', pattern(1, 'float16'), 'minisblack', 'jpegxl', {'level': 85}, rps=21, tol=1e-3)


def gen_jxr():
    s = 'jxr'
    shutil.rmtree(os.path.join(OUT, s), ignore_errors=True)
    for dt, tag in [('uint8', 'u8'), ('uint16', 'u16'), ('float16', 'f16'), ('float32', 'f32')]:
        emit(s, f'{tag}_gray_strips', pattern(1, dt), 'minisblack', 'jpegxr', rps=8)
        emit(s, f'{tag}_rgb_tiled', pattern(3, dt), 'rgb', 'jpegxr', tile=(16, 32))
        emit(s, f'{tag}_rgba_tiled', pattern(4, dt), 'rgb', 'jpegxr', extrasamples=['unassalpha'], tile=(16, 16))
        emit(s, f'{tag}_rgb_planar', pattern(3, dt), 'rgb', 'jpegxr', planar=True, rps=16)
    emit(s, 'u8_graya_planar', pattern(2, 'uint8'), 'minisblack', 'jpegxr', extrasamples=['unassalpha'], planar=True, rps=10)
    emit(s, 'u8_rgba_assoc_tiled', pattern(4, 'uint8'), 'rgb', 'jpegxr', extrasamples=['assocalpha'], tile=(16, 16))
    emit(s, 'u8_5ch_contig', pattern(5, 'uint8'), 'minisblack', 'jpegxr', rps=21)
    emit(s, 'u16_6ch_contig', pattern(6, 'uint16'), 'minisblack', 'jpegxr', tile=(16, 16))
    # Hamamatsu NDPI's code
    emit(s, 'u8_rgb_22610', pattern(3, 'uint8'), 'rgb', 'jpegxr', code=22610, rps=12)
    emit(s, 'u8_rgb_lossy', pattern(3, 'uint8'), 'rgb', 'jpegxr', {'level': 0.8}, tile=(16, 32), lossy=True)
    emit(s, 'u16_gray_lossy', pattern(1, 'uint16'), 'minisblack', 'jpegxr', {'level': 0.8}, rps=16, lossy=True)
    emit(s, 'f16_rgb_lossy', pattern(3, 'float16'), 'rgb', 'jpegxr', {'level': 0.8}, rps=21, lossy=True)


if __name__ == '__main__':
    gen_jxl()
    gen_jxr()
