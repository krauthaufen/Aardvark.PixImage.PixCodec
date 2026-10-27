"""What GDAL (through rasterio) reads from an ENVI raster: the oracle for the ENVI fixtures and for files the library writes.

usage: python3 tests/oracle/envi_read.py DATAFILE OUT.json OUT.ref

OUT.ref holds the samples (pxref format, bands as channels; a complex band as two channels, real then imaginary). OUT.json
holds the size, data type, geotransform (rasterio order: x = a*col + b*row + c, y = d*col + e*row + f; null when GDAL
finds none), EPSG code, unit factor, no-data value, band names, wavelengths and the header fields GDAL parsed (its "ENVI"
metadata domain, braces removed). GDAL's .aux.xml sidecars are removed so nothing but the header is read.
"""
import json
import os
import sys
import warnings

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pxref


def _drop_aux(path):
    d = os.path.dirname(os.path.abspath(path))
    for f in os.listdir(d):
        if f.endswith('.aux.xml'):
            os.remove(os.path.join(d, f))


def read(path):
    """(json dict, samples as an (H, W, C) array) of what GDAL reads."""
    try:
        import rasterio
        from rasterio.errors import NotGeoreferencedWarning
    except ImportError:
        pxref.missing('python:rasterio', 'rasterio (GDAL) reads ENVI rasters')
    _drop_aux(path)
    with warnings.catch_warnings(record=True) as caught:
        warnings.simplefilter('always')
        with rasterio.open(path) as s:
            if s.driver != 'ENVI':
                raise SystemExit(f'GDAL opened {path} with {s.driver}, not ENVI')
            a = s.read()                                    # (bands, H, W)
            placed = not any(issubclass(w.category, NotGeoreferencedWarning) for w in caught)
            t = tuple(s.transform)[:6]
            crs = s.crs
            envi = s.tags(ns='ENVI')
            j = dict(width=s.width, height=s.height, bands=s.count, dtype=s.dtypes[0],
                     transform=[float(v) for v in t] if placed else None,
                     epsg=crs.to_epsg() if crs else None,
                     geographic=bool(crs.is_geographic) if crs else None,
                     projected=bool(crs.is_projected) if crs else None,
                     linearFactor=None, nodata=s.nodata,
                     descriptions=list(s.descriptions),
                     wavelengths=[float(s.tags(i)['wavelength']) if 'wavelength' in s.tags(i) else None for i in range(1, s.count + 1)],
                     wavelengthUnits=s.tags().get('wavelength_units'),
                     fields={k: v.strip()[1:-1].strip() if v.strip().startswith('{') else v.strip() for k, v in envi.items()})
            if crs is not None and crs.is_projected:
                try:
                    j['linearFactor'] = float(crs.linear_units_factor[1])
                except Exception:
                    pass
            if all(w is None for w in j['wavelengths']):
                j['wavelengths'] = None
    _drop_aux(path)
    if np.iscomplexobj(a):
        re, im = a.real, a.imag
        a = np.stack([re, im], axis=1).reshape(a.shape[0] * 2, a.shape[1], a.shape[2])
        j['complex'] = True
    else:
        j['complex'] = False
    return j, np.ascontiguousarray(np.moveaxis(a, 0, -1))


def write(path, out_json, out_ref):
    j, a = read(path)
    with open(out_json, 'w', encoding='utf-8') as f:
        json.dump(j, f, indent=1, ensure_ascii=False)
    pxref.write_ref(out_ref, a)


if __name__ == '__main__':
    write(sys.argv[1], sys.argv[2], sys.argv[3])
