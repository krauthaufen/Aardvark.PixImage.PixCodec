"""GeoTIFF fixtures (tests/fixtures/geotiff): georeferenced TIFFs and what the reference readers make of them.

usage: python3 gen_geotiff.py   (from tests/oracle; needs rasterio (GDAL) and tifffile, see requirements.txt)

Writers: GDAL (rasterio) for the files GIS software writes, tifffile for the variants GDAL does not write (tags put in
by hand). Every fixture F.tif gets
  F.tif.ref   the stored samples as GDAL reads them (GDAL ignores the TIFF Orientation tag, so these are stored order)
  F.tif.json  what the references report:
    gdal        GDAL's geotransform as rasterio's Affine (a, b, c, d, e, f: x = a*col + b*row + c, y = d*col + e*row + f)
                read twice: with GDAL's defaults ("default"), and with GTIFF_HONOUR_NEGATIVE_SCALEY=YES ("standard"),
                where a negative ScaleY flips as requirement 10.5 says. Both give cell corners for every grid (a
                PixelIsPoint grid moved half a cell from its sample centres). Also GDAL's EPSG code, no-data value and the
                number of ground control points
    raw         the tags and the GeoKeyDirectory as tifffile lists them, each key's value taken from where the
                directory points (the inline short, GeoDoubleParams or GeoAsciiParams), with no interpretation
Nothing in the expectations is computed by this script; it only records what GDAL and tifffile return.
"""
import json
import os
import sys
import warnings

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pxref  # noqa: E402

warnings.filterwarnings('ignore')
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'geotiff')

PIXEL_SCALE, TIEPOINT, TRANSFORMATION = 33550, 33922, 34264
KEY_DIRECTORY, DOUBLE_PARAMS, ASCII_PARAMS, GDAL_NODATA = 34735, 34736, 34737, 42113


def gdal_reading(path):
    import rasterio
    from rasterio.env import Env

    def read(**env):
        with Env(**env):
            with rasterio.open(path) as ds:
                t = ds.transform
                # a file with tiepoints alone has its CRS on the ground control points
                crs = ds.crs or ds.gcps[1]
                return dict(transform=[t.a, t.b, t.c, t.d, t.e, t.f],
                            epsg=crs.to_epsg() if crs else None,
                            nodata=None if ds.nodata is None else ('nan' if np.isnan(ds.nodata) else ds.nodata),
                            gcps=len(ds.gcps[0]), width=ds.width, height=ds.height)
    with rasterio.open(path) as ds:
        samples = np.moveaxis(ds.read(), 0, -1)
    return dict(default=read(), standard=read(GTIFF_HONOUR_NEGATIVE_SCALEY='YES')), samples


def raw_tags(path):
    import tifffile
    with tifffile.TiffFile(path) as tf:
        tags = tf.pages[0].tags

        def values(tag):
            return None if tag not in tags else [float(v) for v in np.asarray(tags[tag].value, dtype='f8').ravel()]
        r = dict(pixelScale=values(PIXEL_SCALE), tiepoints=values(TIEPOINT), modelTransformation=values(TRANSFORMATION),
                 orientation=int(tags[274].value) if 274 in tags else None,
                 noData=tags[GDAL_NODATA].value if GDAL_NODATA in tags else None, keys=None)
        if KEY_DIRECTORY in tags:
            d = [int(v) for v in np.asarray(tags[KEY_DIRECTORY].value).ravel()]
            dbl = values(DOUBLE_PARAMS)
            asc = tags[ASCII_PARAMS].value if ASCII_PARAMS in tags else None
            if isinstance(asc, bytes):
                asc = asc.decode('ascii', 'replace')
            keys = []
            for e in range(min(d[3], (len(d) - 4) // 4)):
                kid, loc, count, off = d[4 + 4 * e: 8 + 4 * e]
                k = dict(id=kid, location=loc, count=count)
                if loc == 0:
                    k['shorts'] = [off]
                elif loc == KEY_DIRECTORY:
                    k['shorts'] = d[off:off + count]
                elif loc == DOUBLE_PARAMS and dbl is not None:
                    k['doubles'] = dbl[off:off + count]
                elif loc == ASCII_PARAMS and asc is not None:
                    k['text'] = asc[off:off + count].split('|')[0]
                keys.append(k)
            r['keys'] = keys
            r['keyDirectory'] = d[:4]
        return r


def save(name, what, writer):
    path = os.path.join(OUT, name + '.tif')
    gdal, samples = gdal_reading(path)
    pxref.write_ref(path + '.ref', samples)
    with open(path + '.json', 'w') as f:
        json.dump(dict(what=what, writer=writer, gdal=gdal, raw=raw_tags(path)), f, indent=1)


def pattern(h, w, c=1, dtype='uint16'):
    # unique samples, so that a test can find where one went: the value itself, or (column, row, ...) in the channels
    if c == 1:
        return np.arange(h * w).reshape(h, w).astype(dtype)
    a = np.zeros((h, w, c), dtype=dtype)
    a[..., 0] = np.arange(w)[None, :]
    a[..., 1] = np.arange(h)[:, None]
    a[..., 2:] = 77
    return a


def main():
    try:
        import rasterio
        import tifffile
        from rasterio.transform import Affine, from_origin
    except ImportError as e:
        pxref.missing('python:' + (e.name or 'rasterio'), 'rasterio (GDAL) and tifffile write and read the GeoTIFF fixtures')
    os.makedirs(OUT, exist_ok=True)
    for f in os.listdir(OUT):
        os.remove(os.path.join(OUT, f))

    # ---------------------------------------------------------------- written by GDAL
    def gdal(name, what, h=9, w=11, count=1, dtype='uint16', **profile):
        path = os.path.join(OUT, name + '.tif')
        tags = profile.pop('tags', None)
        data = pattern(h, w, count, dtype)
        with rasterio.open(path, 'w', driver='GTiff', width=w, height=h, count=count, dtype=dtype, **profile) as ds:
            ds.write(np.moveaxis(data.reshape(h, w, count), -1, 0))
            if tags:
                ds.update_tags(**tags)
        aux = path + '.aux.xml'
        if os.path.exists(aux):
            os.remove(aux)
        save(name, what, 'gdal')

    gdal('gdal-utm', 'UTM 33N (EPSG:32633), 10 m, the common case', crs='EPSG:32633', transform=from_origin(600000, 5300000, 10, 10))
    gdal('gdal-geographic', 'WGS 84 (EPSG:4326), degrees', crs='EPSG:4326', transform=from_origin(15.5, 48.25, 0.001, 0.001))
    gdal('gdal-rotated', 'rotated and sheared grid: GDAL writes ModelTransformation', crs='EPSG:32633',
         transform=Affine(10, 2, 600000, 1.5, -10, 5300000))
    gdal('gdal-nodata', 'float32 with GDAL_NODATA -9999', dtype='float32', crs='EPSG:32633',
         transform=from_origin(600000, 5300000, 10, 10), nodata=-9999.0)
    gdal('gdal-nodata-nan', 'float32 with GDAL_NODATA nan', dtype='float32', crs='EPSG:4326',
         transform=from_origin(15.5, 48.25, 0.001, 0.001), nodata=float('nan'))
    gdal('gdal-feet', 'NAD83 / California zone 3 (EPSG:2227): US survey feet', crs='EPSG:2227',
         transform=from_origin(6000000, 2100000, 2, 2))
    gdal('gdal-point', 'AREA_OR_POINT=Point: GDAL writes RasterPixelIsPoint and a tiepoint at the first sample centre',
         crs='EPSG:32633', transform=from_origin(600000, 5300000, 10, 10), tags={'AREA_OR_POINT': 'Point'})
    gdal('gdal-rgb-tiled-bigtiff', 'RGB, tiled 16 x 16, BigTIFF, deflate', h=40, w=36, count=3, dtype='uint8', crs='EPSG:3857',
         transform=from_origin(1700000, 6100000, 0.5, 0.5), tiled=True, blockxsize=16, blockysize=16, BIGTIFF='YES',
         compress='deflate')
    gdal('gdal-south', 'UTM 33S (EPSG:32733)', crs='EPSG:32733', transform=from_origin(500000, 7000000, 30, 30))
    gdal('gdal-plain', 'no georeferencing at all')

    # ---------------------------------------------------------------- written by tifffile (tags by hand)
    def keys(entries, count=None):
        d = [1, 1, 1, len(entries) if count is None else count]
        for e in entries:
            d += list(e)
        return d

    def hand(name, what, tags, h=7, w=9, orientation=None):
        path = os.path.join(OUT, name + '.tif')
        extra = []
        for tag, dt, v in tags:
            if dt == 's':
                extra.append((tag, 's', 0, v, True))
            else:
                v = np.asarray(v, dtype='<' + dt)
                extra.append((tag, dt, v.size, v, True))
        if orientation:
            extra.append((274, 'H', 1, orientation, True))
        tifffile.imwrite(path, pattern(h, w), extratags=extra)
        save(name, what, 'tifffile')

    utm = [(1024, 0, 1, 1), (1025, 0, 1, 1), (3072, 0, 1, 32633), (3076, 0, 1, 9001)]
    north = [(PIXEL_SCALE, 'd', (10, 10, 0)), (TIEPOINT, 'd', (0, 0, 0, 600000, 5300000, 0))]
    hand('tiepoint-offset', 'the tiepoint ties raster (2, 3), not (0, 0)',
         [(PIXEL_SCALE, 'd', (10, 10, 0)), (TIEPOINT, 'd', (2, 3, 0, 600020, 5299970, 0)), (KEY_DIRECTORY, 'H', keys(utm))])
    hand('tiepoints-and-scale', 'two tiepoints with a pixel scale: the first one places the grid',
         [(PIXEL_SCALE, 'd', (10, 10, 0)), (TIEPOINT, 'd', (0, 0, 0, 600000, 5300000, 0, 4, 3, 0, 600040, 5299970, 0)),
          (KEY_DIRECTORY, 'H', keys(utm))])
    hand('gcps', 'three tiepoints and no scale: ground control points, no affine', [
        (TIEPOINT, 'd', (0, 0, 0, 600000, 5300000, 0, 8, 0, 0, 600080, 5300000, 0, 0, 6, 0, 600000, 5299940, 0)),
        (KEY_DIRECTORY, 'H', keys(utm))])
    hand('keys-only', 'GeoKeys without model tags: a CRS but no placement',
         [(KEY_DIRECTORY, 'H', keys([(1024, 0, 1, 2), (1025, 0, 1, 1), (2048, 0, 1, 4326), (2054, 0, 1, 9102)]))])
    hand('user-defined', 'ProjectedCRSGeoKey 32767 (user-defined): no EPSG code (requirement 3.2)',
         north + [(KEY_DIRECTORY, 'H', keys([(1024, 0, 1, 1), (1025, 0, 1, 1), (3072, 0, 1, 32767)]))])
    hand('negative-scale-y', 'ScaleY < 0: a vertical flip (requirement 10.5); GDAL ignores the sign by default',
         [(PIXEL_SCALE, 'd', (10, -10, 0)), (TIEPOINT, 'd', (0, 0, 0, 600000, 5300000, 0)), (KEY_DIRECTORY, 'H', keys(utm))])
    hand('negative-scale-x', 'ScaleX < 0: a horizontal flip',
         [(PIXEL_SCALE, 'd', (-10, 10, 0)), (TIEPOINT, 'd', (0, 0, 0, 600000, 5300000, 0)), (KEY_DIRECTORY, 'H', keys(utm))])
    hand('transformation-z', 'ModelTransformation with a Z row (4 x 4, 16 values, requirement 11.3)',
         [(TRANSFORMATION, 'd', (10, 3, 0, 600000, 0.5, -10, 0, 5300000, 0, 0, 1, 17, 0, 0, 0, 1)), (KEY_DIRECTORY, 'H', keys(utm))])
    hand('transformation-12', 'ModelTransformation of 12 values, which requirement 11.3 does not allow: no affine',
         [(TRANSFORMATION, 'd', (10, 3, 0, 600000, 0.5, -10, 0, 5300000, 0, 0, 1, 17)), (KEY_DIRECTORY, 'H', keys(utm))])
    hand('bad-key-count', 'NumberOfKeys 9 with 4 entries: only what the tag holds is read',
         north + [(KEY_DIRECTORY, 'H', keys(utm, count=9))])
    hand('params', 'GeoKeys in every location: inline, GeoKeyDirectory shorts, GeoDoubleParams, GeoAsciiParams', north + [
        (KEY_DIRECTORY, 'H', keys([(1024, 0, 1, 2), (1025, 0, 1, 1), (1026, ASCII_PARAMS, 14, 0), (2048, 0, 1, 4326),
                                   (2049, ASCII_PARAMS, 7, 14), (2050, 0, 1, 6326), (2054, 0, 1, 9102),
                                   (2057, DOUBLE_PARAMS, 1, 1), (2059, DOUBLE_PARAMS, 1, 0), (4096, KEY_DIRECTORY, 2, 32)])
         + [5773, 5774]),
        (DOUBLE_PARAMS, 'd', (298.257223563, 6378137.0)),
        (ASCII_PARAMS, 's', 'my world file|WGS 84|')])
    hand('point', 'GTRasterTypeGeoKey 2 (PixelIsPoint) written by hand, tiepoint at (0, 0)',
         north + [(KEY_DIRECTORY, 'H', keys([(1024, 0, 1, 1), (1025, 0, 1, 2), (3072, 0, 1, 32633)]))])
    for o in (2, 3, 4, 5, 6, 7, 8):
        hand(f'orientation-{o}', f'TIFF Orientation {o}: the placement refers to the stored raster (B.6)', north + [
            (KEY_DIRECTORY, 'H', keys(utm))], orientation=o)
    hand('orientation-6-point', 'Orientation 6 on a PixelIsPoint grid', north + [
        (KEY_DIRECTORY, 'H', keys([(1024, 0, 1, 1), (1025, 0, 1, 2), (3072, 0, 1, 32633)]))], orientation=6)
    hand('orientation-5-rotated', 'Orientation 5 on a rotated grid', [
        (TRANSFORMATION, 'd', (10, 3, 0, 600000, 0.5, -10, 0, 5300000, 0, 0, 0, 0, 0, 0, 0, 1)), (KEY_DIRECTORY, 'H', keys(utm))],
        orientation=5)

    names = sorted(f[:-4] for f in os.listdir(OUT) if f.endswith('.tif'))
    print(f'{len(names)} GeoTIFF fixtures (GDAL {rasterio.__gdal_version__}, tifffile {tifffile.__version__}): ' + ' '.join(names))


if __name__ == '__main__':
    main()
