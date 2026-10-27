"""ENVI fixtures: rasters written by GDAL and headers written by hand, each with what GDAL reads from it.

usage: python3 tests/oracle/gen_envi.py   (needs rasterio (GDAL) and numpy)

Every fixture is a sample file NAME.img (or another name a header can point to), its header, and NAME.json + NAME.ref:
what GDAL reads back (envi_read.py), never what this script meant to write. The hand-written headers cover what GDAL does
not write itself; each one cites the ENVI documentation ("ENVI Header Files", NV5 Geospatial) for the feature it uses.
"""
import json
import os
import shutil
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import envi_read
import pxref

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'envi')


def pattern(bands, h, w, dtype, seed):
    rng = np.random.default_rng(seed)
    dt = np.dtype(dtype)
    if dt.kind == 'c':
        part = np.float32 if dt == np.complex64 else np.float64
        return (rng.standard_normal((bands, h, w)).astype(part) * 100 + 1j * rng.standard_normal((bands, h, w)).astype(part)).astype(dt)
    if dt.kind == 'f':
        return (rng.standard_normal((bands, h, w)) * 1000).astype(dt)
    info = np.iinfo(dt)
    return rng.integers(info.min, info.max, size=(bands, h, w), dtype=dt, endpoint=True)


def oracle(name, data_path, data_file=None, header_file=None):
    """NAME.json and NAME.ref from GDAL reading data_path; the JSON also names the fixture's own two files."""
    out = os.path.join(OUT, name + '.json')
    envi_read.write(data_path, out, os.path.join(OUT, name + '.ref'))
    with open(out, encoding='utf-8') as f:
        j = json.load(f)
    j['dataFile'] = data_file or os.path.basename(data_path)
    j['headerFile'] = header_file
    with open(out, 'w', encoding='utf-8') as f:
        json.dump(j, f, indent=1, ensure_ascii=False)


def gdal(name, bands=3, h=7, w=9, dtype='uint16', crs=None, transform=None, interleave='bsq', nodata=None, names=None, seed=1):
    import rasterio
    path = os.path.join(OUT, name + '.img')
    a = pattern(bands, h, w, dtype, seed)
    opts = dict(driver='ENVI', width=w, height=h, count=bands, dtype=dtype, interleave=interleave)
    if crs is not None:
        opts['crs'] = crs
    if transform is not None:
        opts['transform'] = transform
    if nodata is not None:
        opts['nodata'] = nodata
    with rasterio.open(path, 'w', **opts) as s:
        s.write(a)
        for i, n in enumerate(names or []):
            s.set_band_description(i + 1, n)
    oracle(name, path, header_file=name + '.hdr')


def raw(name, header, samples, data_name=None, header_name=None):
    """A hand-written header next to the samples (numpy array, written as its bytes)."""
    data_path = os.path.join(OUT, data_name or name + '.img')
    with open(data_path, 'wb') as f:
        f.write(samples if isinstance(samples, bytes) else samples.tobytes())
    with open(os.path.join(OUT, header_name or name + '.hdr'), 'w', encoding='utf-8', newline='') as f:
        f.write(header)
    oracle(name, data_path, header_file=header_name or name + '.hdr')


def head(samples, lines, bands, data_type, interleave='bsq', byte_order=0, extra=''):
    return (f'ENVI\nsamples = {samples}\nlines = {lines}\nbands = {bands}\nheader offset = 0\nfile type = ENVI Standard\n'
            f'data type = {data_type}\ninterleave = {interleave}\nbyte order = {byte_order}\n' + extra)


def order(a, interleave):
    """(bands, H, W) samples in the file order of an interleave."""
    return {'bsq': a, 'bil': np.transpose(a, (1, 0, 2)), 'bip': np.transpose(a, (1, 2, 0))}[interleave].copy()


def main():
    try:
        import rasterio
        from rasterio.transform import from_origin, Affine
    except ImportError:
        pxref.missing('python:rasterio', 'rasterio (GDAL) writes and reads the ENVI fixtures')
    if os.path.isdir(OUT):
        shutil.rmtree(OUT)
    os.makedirs(OUT)

    # ---------------------------------------------------------------- written by GDAL
    gdal('gdal_u16_utm33n_bsq', crs='EPSG:32633', transform=from_origin(500000, 4000000, 10, 20), nodata=0,
         names=['blue', 'green', 'red'])
    gdal('gdal_f32_geo_bil', dtype='float32', crs='EPSG:4326', transform=from_origin(10, 50, 0.1, 0.05), interleave='bil')
    gdal('gdal_i16_utm17s_bip', bands=2, dtype='int16', crs='EPSG:32717', transform=from_origin(300000, 9000000, 30, 30), interleave='bip')
    gdal('gdal_u8_plain', bands=1, dtype='uint8')
    gdal('gdal_u16_stateplane_ft', bands=1, crs='EPSG:2227', transform=from_origin(6000000, 2000000, 2, 2))
    gdal('gdal_u16_rotated', bands=1, crs='EPSG:32633', transform=Affine(10, 2, 500000, 1, -10, 4000000))
    gdal('gdal_f32_webmercator', bands=2, dtype='float32', crs='EPSG:3857', transform=from_origin(1000000, 6000000, 100, 100))
    gdal('gdal_u8_nad83_geo', bands=1, dtype='uint8', crs='EPSG:4269', transform=from_origin(-120, 40, 0.01, 0.01))
    for i, dt in enumerate(['uint8', 'int16', 'int32', 'float32', 'float64', 'complex64', 'complex128',
                            'uint16', 'uint32', 'int64', 'uint64']):
        gdal(f'gdal_dt_{dt}', bands=2, h=5, w=6, dtype=dt, crs='EPSG:32633', transform=from_origin(500000, 4000000, 1, 1),
             interleave=['bsq', 'bil', 'bip'][i % 3], seed=10 + i)

    # ---------------------------------------------------------------- hand-written headers, read by GDAL
    a = pattern(3, 6, 8, 'int32', 40)
    # "byte order: 1 = Network (IEEE) byte order (most significant byte first)"
    raw('hand_bigendian_i32_bil', head(8, 6, 3, 3, 'bil', 1), order(a, 'bil').astype('>i4'))
    c = pattern(2, 4, 5, 'complex64', 41)
    # "data type 6 = Complex: real-imaginary pair of single-precision floating-point", big endian
    raw('hand_bigendian_c64_bip', head(5, 4, 2, 6, 'bip', 1), order(c, 'bip').astype('>c8'))
    u = pattern(2, 5, 7, 'uint16', 42)
    # "header offset: the number of bytes of embedded header information present in the file ... skips these bytes"
    raw('hand_header_offset', head(7, 5, 2, 12).replace('header offset = 0', 'header offset = 333'),
        bytes(range(256)) + bytes(77) + order(u, 'bsq').tobytes())
    one = pattern(1, 5, 7, 'uint8', 43)
    # "The reference (tie point) pixels in the map info header field are one-based": a tie point inside a pixel
    raw('hand_reference_pixel', head(7, 5, 1, 1, extra='map info = {UTM, 2.5, 3.5, 500000, 4000000, 10, 20, 33, North, WGS-84, units=Meters}\n'), one)
    # rotation (in degrees, from north) together with a tie point away from the corner
    raw('hand_rotation', head(7, 5, 1, 1, extra='map info = {UTM, 3, 2, 500000, 4000000, 10, 20, 33, North, WGS-84, rotation=30}\n'), one)
    raw('hand_negative_y_size', head(7, 5, 1, 1, extra='map info = {UTM, 1, 1, 500000, 4000000, 10, -10, 33, North, WGS-84}\n'), one)
    # no datum: ENVI's default for UTM is NAD27, so no WGS 84 code
    raw('hand_utm_no_datum', head(7, 5, 1, 1, extra='map info = {UTM, 1, 1, 500000, 4000000, 10, 10, 33, North}\n'), one)
    raw('hand_utm_feet', head(7, 5, 1, 1, extra='map info = {UTM, 1, 1, 500000, 4000000, 10, 10, 33, North, WGS-84, units=Feet}\n'), one)
    raw('hand_geographic_no_datum', head(7, 5, 1, 1, extra='map info = {Geographic Lat/Lon, 1, 1, 16.3, 48.2, 0.001, 0.001}\n'), one)
    raw('hand_geographic_wgs84', head(7, 5, 1, 1, extra='map info = {Geographic Lat/Lon, 1, 1, 16.3, 48.2, 0.001, 0.002, WGS-84, units=Degrees}\n'), one)
    # a header far longer than any fixed buffer, with the placement at its very end
    notes = ''.join(f'sensor note {k:05d} = {{calibration constant {k}, gain {k % 7}, offset {k % 13}}}\n' for k in range(1500))
    raw('hand_long_header', head(7, 5, 1, 1, extra=notes + 'map info = {UTM, 1, 1, 400000, 5000000, 5, 5, 32, North, WGS-84}\n'), one)
    # "Field values have no such restrictions" (on characters): UTF-8 text
    raw('hand_utf8_text', head(7, 5, 3, 12, extra='description = {Größe über Wien}\nband names = {Blau, Grün, Rot}\n'), pattern(3, 5, 7, 'uint16', 44))
    # "Comments can appear anywhere within a header file except within keyword/value brackets", CRLF line ends,
    # values over several lines
    crlf = ('ENVI\r\n; written by hand\r\nSamples = 7\r\nlines   = 5\r\n;another comment\r\nbands = 2\r\ndata type = 2\r\n'
            'interleave = BIL\r\nbyte order = 0\r\nband names = {\r\n first band,\r\n second band\r\n}\r\n'
            'map info = {UTM, 1, 1, 500000,\r\n 4000000, 10, 10, 33, South, WGS-84}\r\n')
    raw('hand_crlf_comments', crlf, order(pattern(2, 5, 7, 'int16', 45), 'bil'))
    s3 = pattern(3, 5, 7, 'float32', 46)
    raw('hand_spectral', head(7, 5, 3, 4, 'bip', extra='wavelength units = Nanometers\nwavelength = {450.5, 550, 650.25}\n'
                              'fwhm = {10, 12.5, 15}\nbbl = {1, 0, 1}\ndata ignore value = -9999\nband names = {b1, b2, b3}\n'), order(s3, 'bip'))
    # "foo.img.hdr" naming, and a sample file without extension
    raw('hand_hdr_after_ext', head(7, 5, 1, 1), one, data_name='hand_hdr_after_ext.dat', header_name='hand_hdr_after_ext.dat.hdr')
    raw('hand_no_extension', head(7, 5, 1, 1), one, data_name='hand_no_extension', header_name='hand_no_extension.hdr')

    # ---------------------------------------------------------------- a header written into its own sample file
    # GDAL only opens a separate .hdr, so the oracle reads a copy with the same header next to it; the "header offset"
    # skips the header text ("ENVI skips these bytes when reading the file").
    text = head(7, 5, 2, 12, 'bip').replace('header offset = 0', 'header offset = 1024')
    blob = text.encode() + bytes(1024 - len(text.encode())) + order(u, 'bip').tobytes()
    with open(os.path.join(OUT, 'attached.envi'), 'wb') as f:
        f.write(blob)
    tmp = os.path.join(OUT, '_attached_copy')
    with open(tmp, 'wb') as f:
        f.write(blob)
    with open(tmp + '.hdr', 'w') as f:
        f.write(text)
    oracle('attached', tmp, data_file='attached.envi', header_file='attached.envi')
    os.remove(tmp)
    os.remove(tmp + '.hdr')

    print(f'{len([f for f in os.listdir(OUT) if f.endswith(".json")])} ENVI fixtures in {OUT}')


if __name__ == '__main__':
    main()
