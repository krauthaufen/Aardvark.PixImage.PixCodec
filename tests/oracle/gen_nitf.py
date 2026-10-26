"""NITF fixtures and what a reference reader makes of them.

usage: python3 tests/oracle/gen_nitf.py   (needs rasterio (GDAL with its NITF driver) and numpy)

Every fixture F.ntf gets F.ntf.json with what GDAL reports when it reads the file: samples (F.ntf.gdal.ref), data type,
band count, geotransform, GCPs, CRS, the NITF_* metadata items and the TREs. F.ntf.ref holds the samples the C# reader
must return: GDAL's samples where GDAL reads the file the way MIL-STD-2500C describes it, otherwise the samples the
standard defines, with the clause and the disagreement written into the JSON ('spec' / 'gdalDiffers').

Two writers:
  * GDAL (rasterio's NITF driver) for what it can write: band interleaved by block, 1 to 64 bit samples, blocking,
    ICORDS G / D / N / S, TREs, comments, look-up tables from a palette, JPEG (C3 / M3) and JPEG 2000 (C8).
  * pack() below, a direct transcription of MIL-STD-2500C table A-1 (file header), table A-3 (image subheader),
    table A-3(A) (image data mask), table A-8 (DES) and 5.4.3.3 (uncompressed image data), for IMODE P / R / S, packed
    samples in every band order, justified and signed samples, masks with missing blocks, several image segments,
    TRE overflow, NITF 2.0 headers (MIL-STD-2500A table I / III) and corner sets GDAL does not write.
"""
import json
import os
import shutil
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pxref

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'nitf')


# ---------------------------------------------------------------------------------------------------- spec packer

def f(v, n, num=False):
    """A BCS field of n characters: numbers right justified with zeros, text left justified with spaces."""
    s = str(v)
    s = s.rjust(n, '0') if num else s.ljust(n)
    assert len(s) == n, (v, n)
    return s.encode('latin1')


SEC21 = b'U' + b' ' * 166                      # ISCLAS .. ISCTLN, table A-3: 1+2+11+2+20+2+8+4+1+8+43+1+40+1+8+15
SEC20 = b'U' + b' ' * 160 + b'      '          # ISCLAS ISCODE ISCTLH ISREL ISCAUT ISCTLN, then ISDWNG (6), MIL-STD-2500A


def tres_field(tres):
    """UDIDL / IXSHDL style: the length (data + 3), the overflow field, the TREs (table A-7: CETAG 6, CEL 5, CEDATA)."""
    data = b''.join(t.ljust(6).encode() + f'{len(d):05d}'.encode() + d for t, d in tres)
    return f(len(data) + 3, 5, True) + b'000' + data if data else b'00000'


class Bits:
    def __init__(self):
        self.out = bytearray()
        self.acc = 0
        self.n = 0

    def put(self, v, n):
        for i in range(n - 1, -1, -1):
            self.acc = self.acc << 1 | ((int(v) >> i) & 1)
            self.n += 1
            if self.n == 8:
                self.out.append(self.acc)
                self.acc = self.n = 0

    def pad(self):
        # "the last byte of each block's data is zero-filled to the next byte boundary" (5.4.3.3.1.1)
        if self.n:
            self.out.append(self.acc << (8 - self.n))
            self.acc = self.n = 0
        return bytes(self.out)


def stored(v, nbpp, abpp, pjust, pvtype):
    """The NBPP bits that hold value v (table A-3 ABPP / PJUST / PVTYPE)."""
    v = int(v)
    if pvtype == 'SI':
        v &= (1 << abpp) - 1                   # two's complement in ABPP bits
    return v << (nbpp - abpp) if pjust == 'L' else v


def pack(a, nbpp, abpp, pjust, pvtype, imode, bw, bh):
    """Uncompressed image data of a (bands, rows, cols) array of integers (5.4.3.3.1), one block after the other.
    Returns the bytes and the per unit byte lengths (units = blocks, or blocks of each band for IMODE S)."""
    nb, h, w = a.shape
    nbpr, nbpc = -(-w // bw), -(-h // bh)
    full = np.zeros((nb, nbpc * bh, nbpr * bw), dtype=np.int64)
    full[:, :h, :w] = a                        # fill pixels: zero
    units = []

    def block(by, bx):
        return full[:, by * bh:(by + 1) * bh, bx * bw:(bx + 1) * bw]

    def put(bits, v):
        bits.put(stored(v, nbpp, abpp, pjust, pvtype), nbpp)

    if imode == 'S':
        for b in range(nb):
            for by in range(nbpc):
                for bx in range(nbpr):
                    bits = Bits()
                    for v in block(by, bx)[b].ravel():
                        put(bits, v)
                    units.append(bits.pad())
    else:
        for by in range(nbpc):
            for bx in range(nbpr):
                k = block(by, bx)
                bits = Bits()
                if imode == 'P':
                    for v in k.transpose(1, 2, 0).ravel():
                        put(bits, v)
                elif imode == 'B':
                    for v in k.ravel():
                        put(bits, v)
                else:                          # R: each row, band after band
                    for y in range(bh):
                        for b in range(nb):
                            for v in k[b, y]:
                                put(bits, v)
                units.append(bits.pad())
    return units


def image_subheader(rows, cols, nb, pvtype, nbpp, abpp, pjust, imode, bw, bh, irep='MONO', icat='VIS', ic='NC',
                    comrat=None, icords=' ', igeolo=None, comments=(), reps=None, luts=None, tres=(), iid='IMAGE',
                    version='2.1', title=''):
    nbpr, nbpc = -(-cols // bw), -(-rows // bh)
    v20 = version == '2.0'
    s = b'IM' + f(iid, 10) + f('20260101000000', 14) + f('', 17) + f(title, 80)
    s += SEC20 if v20 else SEC21
    s += b'0' + f('', 42) + f(rows, 8, True) + f(cols, 8, True) + f(pvtype, 3) + f(irep, 8) + f(icat, 8)
    s += f(abpp, 2, True) + f(pjust, 1) + f(icords, 1)
    if igeolo is not None:
        assert len(igeolo) == 60
        s += igeolo.encode('latin1')
    s += f(len(comments), 1, True) + b''.join(f(c, 80) for c in comments)
    s += f(ic, 2)
    if ic not in ('NC', 'NM'):
        s += f(comrat or '', 4)
    if nb <= 9:
        s += f(nb, 1, True)
    else:
        s += b'0' + f(nb, 5, True)
    for b in range(nb):
        rep = (reps[b] if reps else ('M' if nb == 1 else ''))
        s += f(rep, 2) + f('', 6) + b'N' + b'   '
        lut = luts[b] if luts else None
        if lut:
            s += f(len(lut), 1, True) + f(len(lut[0]), 5, True) + b''.join(bytes(np.asarray(t, dtype=np.uint8)) for t in lut)
        else:
            s += b'0'
    s += b'0' + f(imode, 1) + f(nbpr, 4, True) + f(nbpc, 4, True) + f(bw, 4, True) + f(bh, 4, True)
    s += f(nbpp, 2, True) + b'001' + b'000' + b'0000000000' + b'1.0 '
    s += b'00000' + tres_field(tres)
    return s


def des_overflow(area, item, tres):
    """A TRE_OVERFLOW data extension (table A-8, 5.8.3.1): subheader and data."""
    sub = b'DE' + f('TRE_OVERFLOW', 25) + b'01' + SEC21 + f(area, 6) + f(item, 3, True) + b'0000'
    data = b''.join(t.ljust(6).encode() + f'{len(d):05d}'.encode() + d for t, d in tres)
    return sub, data


def nitf_file(images, file_tres=(), des=(), version='2.1', title='', clevel=3):
    """images: [(subheader, data)], des: [(subheader, data)]."""
    v20 = version == '2.0'

    def header(fl, hl):
        h = (b'NITF02.00' if v20 else b'NSIF01.00' if version == 'nsif' else b'NITF02.10')
        h += f(clevel, 2, True) + b'BF01' + f('PIXCODEC', 10) + f('20260101000000', 14) + f(title, 80)
        if v20:
            h += b'U' + b' ' * 160 + b'      '
        else:
            h += b'U' + b' ' * 166
        h += b'00000' + b'00000' + b'0'
        if not v20:
            h += b'\0\0\0'
        h += f('', 27 if v20 else 24) + f('', 18) + f(fl, 12, True) + f(hl, 6, True)
        h += f(len(images), 3, True) + b''.join(f(len(s), 6, True) + f(len(d), 10, True) for s, d in images)
        h += b'000'                                        # NUMS
        if v20:
            h += b'000'                                    # NUML
        else:
            h += b'000'                                    # NUMX
        h += b'000'                                        # NUMT
        h += f(len(des), 3, True) + b''.join(f(len(s), 4, True) + f(len(d), 9, True) for s, d in des)
        h += b'000'                                        # NUMRES
        h += b'00000'                                      # UDHDL
        h += tres_field(file_tres)                         # XHDL, XHDLOFL, XHD
        return h

    hl = len(header(0, 0))
    body = b''.join(s + d for s, d in images) + b''.join(s + d for s, d in des)
    out = header(hl + len(body), hl) + body
    assert len(header(0, 0)) == hl
    return out


def igeolo_g(corners):
    """ddmmssXdddmmssY four times from (lon, lat) pairs."""
    def dms(v, deg, pos, neg):
        h = pos if v >= 0 else neg
        v = abs(v)
        t = round(v * 3600)
        d, m, s = t // 3600, t // 60 % 60, t % 60
        return f'{d:0{deg}d}{m:02d}{s:02d}{h}'
    return ''.join(dms(lat, 2, 'N', 'S') + dms(lon, 3, 'E', 'W') for lon, lat in corners)


def igeolo_d(corners):
    return ''.join(f'{lat:+07.3f}{lon:+08.3f}' for lon, lat in corners)


def igeolo_utm(zone, corners):
    return ''.join(f'{zone:02d}{round(e):06d}{round(n):07d}' for e, n in corners)


# ---------------------------------------------------------------------------------------------------- GDAL side

def gdal_report(path):
    """What GDAL reads from a file: samples (bands last), metadata, placement, TREs; or why it cannot."""
    import rasterio
    try:
        with rasterio.open(path) as s:
            a = s.read()
            tags = s.tags()
            tre = s.tags(ns='TRE')
            gcps, gcrs = s.gcps
            r = dict(status='reads', count=s.count, dtype=str(a.dtype), width=s.width, height=s.height,
                     transform=list(s.transform)[:6], crs=s.crs.to_string() if s.crs else None,
                     gcps=[[g.col, g.row, g.x, g.y] for g in gcps],
                     colorinterp=[c.name for c in s.colorinterp],
                     tags={k: v for k, v in tags.items() if k.startswith('NITF_')},
                     tre=sorted(tre.keys()), subdatasets=len(s.subdatasets))
            try:
                cm = s.colormap(1)
                r['colormap'] = [list(cm[i][:3]) for i in range(max(cm.keys()) + 1)]
            except ValueError:
                pass
            return r, np.moveaxis(a, 0, -1)
    except Exception as e:
        return dict(status='fails', error=str(e).splitlines()[0][:300]), None


def to_ref(a):
    """(rows, cols, bands) GDAL array -> our layout: complex bands become (real, imaginary) channel pairs."""
    if np.iscomplexobj(a):
        out = np.empty(a.shape[:2] + (a.shape[2] * 2,), dtype=a.real.dtype)
        out[..., 0::2] = a.real
        out[..., 1::2] = a.imag
        return out
    return a


def save(name, what, writer, expected=None, spec=None, extra=None, lut=None):
    """Writes the sidecars of OUT/name. expected: the samples the standard defines (rows, cols, channels), for files
    the generator packed itself; GDAL's samples are the expectation where they agree (or where there is no other).
    lut 'rgb' / 'grey': the file has look-up tables, which GDAL hands out as a colour map next to the indices; the
    expectation is that map applied (5.4.3.4), while F.gdal.ref keeps GDAL's indices."""
    path = os.path.join(OUT, name)
    rep, a = gdal_report(path)
    applied = None
    if a is not None and lut:
        cm = np.asarray(rep['colormap'], dtype=np.uint8)
        applied = cm[a[..., 0]] if lut == 'rgb' else cm[a[..., 0]][..., :1]
    j = dict(what=what, writer=writer, gdal=rep)
    if spec:
        j['spec'] = spec
    if extra:
        j.update(extra)
    if a is not None:
        pxref.write_ref(path + '.gdal.ref', to_ref(a))
    if expected is not None:
        expected = np.asarray(expected)
        pxref.write_ref(path + '.ref', expected)
        if a is not None:
            g = applied if applied is not None else to_ref(a)
            j['gdalAgrees'] = bool(g.shape == expected.shape and g.dtype == expected.dtype and np.array_equal(g, expected))
    elif a is not None and j.get('lossy') is None:
        pxref.write_ref(path + '.ref', applied if applied is not None else to_ref(a))
    with open(path + '.json', 'w') as fh:
        json.dump(j, fh, indent=1, sort_keys=True)
    flag = '' if j.get('gdalAgrees', True) else '  (GDAL differs)'
    print(f'{name}: GDAL {rep["status"]}{flag}')


def pattern(nb, h, w, maxv, seed, signed=False):
    rng = np.random.default_rng(seed)
    lo = -(maxv + 1) if signed else 0
    return rng.integers(lo, maxv + 1, (nb, h, w), dtype=np.int64)


# ---------------------------------------------------------------------------------------------------- fixtures

def main():
    try:
        import rasterio
        import rasterio.shutil
        from rasterio.transform import from_origin
    except ImportError as e:
        pxref.missing('python:' + (e.name or 'rasterio'), 'rasterio (GDAL) writes and reads the NITF fixtures')
    if os.path.isdir(OUT):
        shutil.rmtree(OUT)
    os.makedirs(OUT)
    tmp = os.path.join(OUT, '_src.tif')

    def gdal(name, what, a, dtype, create=None, copy=None, crs=None, transform=None, lossy=False, colormap=None, lut=None):
        """GDAL writes a (bands, rows, cols) array: Create, or CreateCopy from a GeoTIFF when copy options are given."""
        path = os.path.join(OUT, name)
        nb, h, w = a.shape
        prof = dict(width=w, height=h, count=nb, dtype=dtype, crs=crs, transform=transform)
        if copy is None:
            with rasterio.open(path, 'w', driver='NITF', **prof, **(create or {})) as s:
                s.write(a.astype(dtype))
        else:
            with rasterio.open(tmp, 'w', driver='GTiff', **prof) as s:
                s.write(a.astype(dtype))
                if colormap:
                    s.write_colormap(1, colormap)
            rasterio.shutil.copy(tmp, path, driver='NITF', **copy)
            os.remove(tmp)
        for aux in (path + '.aux.xml',):
            if os.path.exists(aux):
                os.remove(aux)
        save(name, what, 'gdal', extra=dict(lossy=True) if lossy else None, lut=lut)

    # ---- GDAL writes
    gdal('g_u8.ntf', 'one 8 bit band', pattern(1, 37, 53, 255, 1), 'uint8')
    gdal('g_rgb.ntf', 'IREP RGB, three 8 bit bands, band interleaved by block', pattern(3, 37, 53, 255, 2), 'uint8',
         copy=dict(IREP='RGB'))
    gdal('g_blocked.ntf', '16 x 16 blocks over a 53 x 37 image: fill on the right and at the bottom',
         pattern(3, 37, 53, 255, 3), 'uint8', create=dict(BLOCKXSIZE=16, BLOCKYSIZE=16))
    gdal('g_u12.ntf', 'NBPP 12: packed samples, two in three bytes', pattern(1, 37, 53, 4095, 4), 'uint16',
         create=dict(NBITS=12))
    gdal('g_u12_blocked.ntf', 'NBPP 12, three bands, 16 x 16 blocks', pattern(3, 37, 53, 4095, 5), 'uint16',
         create=dict(NBITS=12, BLOCKXSIZE=16, BLOCKYSIZE=16))
    gdal('g_u4.ntf', 'NBPP 4, two bands', pattern(2, 37, 53, 15, 6), 'uint8', create=dict(NBITS=4))
    gdal('g_u1.ntf', 'NBPP 1, one band', pattern(1, 37, 53, 1, 7), 'uint8', create=dict(NBITS=1))
    gdal('g_u16.ntf', 'one 16 bit band', pattern(1, 19, 23, 65535, 8), 'uint16')
    gdal('g_u32.ntf', 'two 32 bit bands', pattern(2, 9, 7, 2 ** 32 - 1, 9), 'uint32')
    gdal('g_i16.ntf', 'PVTYPE SI, 16 bit', pattern(1, 19, 23, 32767, 10, signed=True), 'int16')
    gdal('g_f32.ntf', 'PVTYPE R, 32 bit, two bands', np.random.default_rng(11).normal(size=(2, 9, 7)), 'float32')
    gdal('g_f64.ntf', 'PVTYPE R, 64 bit', np.random.default_rng(12).normal(size=(1, 9, 7)), 'float64')
    c = np.random.default_rng(13).normal(size=(2, 9, 7)) + 1j * np.random.default_rng(14).normal(size=(2, 9, 7))
    gdal('g_c64.ntf', 'PVTYPE C, NBPP 64: two complex bands (real, imaginary float32 each)', c, 'complex64')
    gdal('g_xbands.ntf', '12 bands: NBANDS 0 and XBANDS 12', pattern(12, 7, 5, 255, 15), 'uint8')
    gdal('g_geo_g.ntf', 'ICORDS G: corners in degrees, minutes and seconds', pattern(1, 37, 53, 255, 16), 'uint8',
         crs='EPSG:4326', transform=from_origin(10, 50, 0.01, 0.01), create=dict(ICORDS='G'))
    gdal('g_geo_d.ntf', 'ICORDS D: corners in decimal degrees, west and south', pattern(1, 37, 53, 255, 17), 'uint8',
         crs='EPSG:4326', transform=from_origin(-74.5, -20.25, 0.025, 0.025), create=dict(ICORDS='D'))
    gdal('g_utm_n.ntf', 'ICORDS N: UTM zone 33 north', pattern(1, 37, 53, 255, 18), 'uint8',
         crs='EPSG:32633', transform=from_origin(500000, 5000000, 10, 10), create=dict(ICORDS='N'))
    gdal('g_utm_s.ntf', 'ICORDS S: UTM zone 19 south', pattern(1, 37, 53, 255, 19), 'uint8',
         crs='EPSG:32719', transform=from_origin(300000, 7000000, 30, 30), create=dict(ICORDS='S'))
    gdal('g_tre.ntf', 'a file TRE and an image TRE', pattern(1, 9, 7, 255, 20), 'uint8',
         create=dict(FILE_TRE='FILETR=file level data', TRE='IMGTRE=image level data'))
    gdal('g_text.ntf', 'title, image id and a comment', pattern(1, 9, 7, 255, 21), 'uint8',
         create=dict(FTITLE='A file title', IID1='MYIMAGE', ICOM='first comment line'))
    pal = {i: (i * 7 % 256, 255 - i, i * 3 % 256, 255) for i in range(256)}
    gdal('g_lut.ntf', 'a palette: IREP RGB/LUT, one band with three look-up tables', pattern(1, 19, 23, 255, 22),
         'uint8', copy=dict(), colormap=pal, lut='rgb')
    gdal('g_nsif.ntf', 'NSIF 1.0 header (STANAG 4545)', pattern(1, 9, 7, 255, 23), 'uint8', copy=dict(FHDR='NSIF01.00'))
    gdal('g_jpeg_rgb.ntf', 'IC C3: JPEG, YCbCr601, one block', pattern(3, 64, 80, 255, 24) // 8 * 8, 'uint8',
         copy=dict(IC='C3'), lossy=True)
    gdal('g_jpeg_m3.ntf', 'IC M3: JPEG in 64 x 64 blocks with a block mask', pattern(1, 150, 140, 255, 25) // 16 * 16,
         'uint8', copy=dict(IC='M3', BLOCKXSIZE=64, BLOCKYSIZE=64), lossy=True)
    gdal('g_j2k_rgb.ntf', 'IC C8: JPEG 2000 codestream, three bands', pattern(3, 64, 80, 255, 26), 'uint8',
         copy=dict(IC='C8', JPEG2000_DRIVER='JP2OpenJPEG', QUALITY='100', REVERSIBLE='YES'), lossy=True)
    gdal('g_j2k_blocked.ntf', 'IC C8 with 32 x 32 blocks: the blocks are the tiles of one codestream',
         pattern(3, 70, 90, 255, 28), 'uint8', copy=dict(IC='C8', JPEG2000_DRIVER='JP2OpenJPEG', QUALITY='100',
                                                         REVERSIBLE='YES', BLOCKXSIZE=32, BLOCKYSIZE=32), lossy=True)
    gdal('g_jpeg12.ntf', 'IC C3: 12 bit JPEG, one band', pattern(1, 48, 40, 4095, 29) // 64 * 64, 'uint16',
         copy=dict(IC='C3', NBITS='12'), lossy=True)
    gdal('g_textseg.ntf', 'a text segment after the image', pattern(1, 9, 7, 255, 30), 'uint8',
         create=dict(TEXT='DATA_0=hello text segment'))
    gdal('g_j2k_u16.ntf', 'IC C8: JPEG 2000, one 12 bit band', pattern(1, 40, 50, 4095, 27), 'uint16',
         copy=dict(IC='C8', JPEG2000_DRIVER='JP2OpenJPEG', QUALITY='100', REVERSIBLE='YES', NBITS='12'), lossy=True)

    # ---- packed by the spec, read by GDAL
    def hand(name, what, spec, a, nbpp, abpp=None, pjust='R', pvtype='INT', imode='B', bw=None, bh=None,
             expected=None, file_tres=(), note=None, lut=None, **sub):
        """a: (bands, rows, cols) integers; expected: our layout, default a as (rows, cols, bands)."""
        nb, h, w = a.shape
        abpp = abpp or nbpp
        bw, bh = bw or w, bh or h
        units = pack(a, nbpp, abpp, pjust, pvtype, imode, bw, bh)
        s = image_subheader(h, w, nb, pvtype, nbpp, abpp, pjust, imode, bw, bh, **sub)
        data = nitf_file([(s, b''.join(units))], file_tres=file_tres)
        open(os.path.join(OUT, name), 'wb').write(data)
        if expected is None:
            dt = {8: np.uint8, 16: np.uint16, 32: np.uint32, 64: np.uint64}[next(n for n in (8, 16, 32, 64) if n >= nbpp)]
            if pvtype == 'SI':
                dt = {np.uint8: np.int8, np.uint16: np.int16, np.uint32: np.int32, np.uint64: np.int64}[dt]
            expected = np.moveaxis(a, 0, -1).astype(dt)
        save(name, what, 'pack', expected=expected, spec=spec, extra=dict(gdalDiffers=note) if note else None, lut=lut)

    packed = ('GDAL (3.12) reads packed samples (NBPP not a multiple of 8) the standard way only for one band in one '
              'block; for 12 bits it takes the bytes in another order than 5.1.9.1c (most significant bit first), and '
              'several band orders it refuses')
    clause = '5.4.3.3.1: one bit stream per block, zero-filled at the end of the block only'
    hand('p_imode_p.ntf', 'IMODE P, three bands, 2 x 2 blocks with fill', '5.4.3.3.1.2.2 band interleaved by pixel',
         pattern(3, 21, 27, 255, 30), 8, imode='P', bw=16, bh=16)
    hand('p_imode_r.ntf', 'IMODE R, three bands, 2 x 2 blocks with fill', '5.4.3.3.1.2.4 band interleaved by row',
         pattern(3, 21, 27, 255, 31), 8, imode='R', bw=16, bh=16)
    hand('p_imode_s.ntf', 'IMODE S, three bands, 2 x 2 blocks with fill', '5.4.3.3.1.2.1 band sequential',
         pattern(3, 21, 27, 255, 32), 8, imode='S', bw=16, bh=16)
    hand('p_imode_b.ntf', 'IMODE B, three bands, 2 x 2 blocks with fill', '5.4.3.3.1.2.3 band interleaved by block',
         pattern(3, 21, 27, 255, 33), 8, imode='B', bw=16, bh=16)
    for m in 'BPRS':
        hand(f'p_u12_{m.lower()}.ntf', f'NBPP 12, two bands, IMODE {m}, 5 x 3 blocks (odd sample counts per block)',
             clause, pattern(2, 7, 11, 4095, 40 + ord(m)), 12, imode=m, bw=5, bh=3, note=packed)
        hand(f'p_u4_{m.lower()}.ntf', f'NBPP 4, three bands, IMODE {m}, 3 x 3 blocks', clause,
             pattern(3, 7, 8, 15, 50 + ord(m)), 4, imode=m, bw=3, bh=3, note=packed)
    hand('p_bilevel.ntf', 'PVTYPE B, NBPP 1', 'table A-3 PVTYPE B: single bits', pattern(1, 13, 19, 1, 60), 1, pvtype='B')
    hand('p_left.ntf', 'ABPP 11 in NBPP 16, PJUST L', 'table A-3 ABPP / PJUST: the significant bits left justified',
         pattern(1, 9, 13, 2047, 61), 16, abpp=11, pjust='L',
         note='GDAL returns the stored 16 bits, the significant bits still shifted to the left')
    hand('p_right.ntf', 'ABPP 11 in NBPP 16, PJUST R', 'table A-3 ABPP / PJUST: right justified',
         pattern(1, 9, 13, 2047, 62), 16, abpp=11, pjust='R')
    hand('p_si_left.ntf', 'PVTYPE SI, ABPP 12 in NBPP 16, PJUST L, negative values',
         'table A-3 PVTYPE SI: two\'s complement; ABPP / PJUST', pattern(1, 9, 13, 2047, 63, signed=True), 16, abpp=12,
         pjust='L', pvtype='SI', note='GDAL returns the stored 16 bits without shifting them')
    hand('p_si_right.ntf', 'PVTYPE SI, ABPP 12 in NBPP 16, PJUST R, negative values',
         'table A-3 PVTYPE SI: two\'s complement in the ABPP significant bits', pattern(1, 9, 13, 2047, 64, signed=True),
         16, abpp=12, pjust='R', pvtype='SI',
         note='GDAL takes the 16 stored bits as the value, so a negative 12 bit value comes back positive')
    hand('p_si12.ntf', 'PVTYPE SI, NBPP 12 packed', 'table A-3 PVTYPE SI, NBPP 12', pattern(1, 9, 13, 2047, 65, signed=True),
         12, pvtype='SI', note='GDAL returns unsigned 16 bit values, in its own 12 bit byte order')

    # look-up tables, applied: one band, three tables give RGB; one gives grey (5.4.3.4, table A-3 NLUTSn / LUTDnm)
    idx = pattern(1, 11, 13, 39, 66)
    lr, lg, lb = (np.random.default_rng(67 + k).integers(0, 256, 40) for k in range(3))
    hand('p_lut_rgb.ntf', 'IREP RGB/LUT: one band, three tables of 40 entries', 'table A-3 NLUTSn = 3, LUTDnm', idx, 8,
         irep='RGB/LUT', reps=['LU'], luts=[[lr, lg, lb]], lut='rgb',
         expected=np.stack([lr[idx[0]], lg[idx[0]], lb[idx[0]]], -1).astype(np.uint8))
    lgrey = np.random.default_rng(70).integers(0, 256, 40)
    hand('p_lut_grey.ntf', 'IREP MONO: one band, one table of 40 entries', 'table A-3 NLUTSn = 1, 5.4.3.4', idx, 8,
         irep='MONO', reps=['LU'], luts=[[lgrey]], lut='grey', expected=lgrey[idx[0]][..., None].astype(np.uint8))

    # RGB with the bands stored B, G, R: the reader returns R, G, B (table A-3 IREPBANDn)
    a = pattern(3, 9, 11, 255, 71)
    hand('p_rgb_bgr.ntf', 'IREP RGB with IREPBAND B, G, R', 'table A-3 IREPBANDn', a, 8, irep='RGB', reps=['B', 'G', 'R'],
         note='GDAL keeps the file order and names each band by its colour interpretation; we return R, G, B',
         expected=np.moveaxis(a[::-1], 0, -1).astype(np.uint8))

    # corners: a rotated grid (a parallelogram) and four corners that are not one
    a = pattern(1, 21, 31, 255, 72)
    rot = [(10.0, 50.0), (10.3, 50.05), (10.35, 49.75), (10.05, 49.70)]
    hand('p_geo_parallelogram.ntf', 'ICORDS G, a rotated grid', 'table A-3 IGEOLO', a, 8, icords='G', igeolo=igeolo_g(rot))
    bad = [(10.0, 50.0), (10.3, 50.0), (10.4, 49.7), (10.0, 49.75)]
    hand('p_geo_trapezoid.ntf', 'ICORDS D, corners that are no parallelogram: not a grid', 'table A-3 IGEOLO', a, 8,
         icords='D', igeolo=igeolo_d(bad))
    hand('p_geo_mgrs.ntf', 'ICORDS U (MGRS): not interpreted', 'table A-3 ICORDS U', a, 8, icords='U',
         igeolo='33UXP0500050000' * 4)
    hand('p_geo_blank_seconds.ntf', 'ICORDS G with the seconds left blank', 'table A-3 IGEOLO: non-significant digits are spaces',
         a, 8, icords='G', igeolo='5000  N01000  E5000  N01030  E4930  N01030  E4930  N01000  E')

    # image data mask: four blocks, one not recorded, a pad pixel code (table A-3(A))
    a = pattern(1, 20, 20, 254, 73)
    units = pack(a, 8, 8, 'R', 'INT', 'B', 10, 10)
    order = [0, 2, 3]                                # block 1 is not recorded
    bmr = []
    off = 0
    for k in range(4):
        if k in order:
            bmr.append(off)
            off += len(units[k])
        else:
            bmr.append(0xFFFFFFFF)
    mask = (int(10 + 1 + 16).to_bytes(4, 'big') + (4).to_bytes(2, 'big') + (0).to_bytes(2, 'big') + (8).to_bytes(2, 'big')
            + bytes([255]) + b''.join(v.to_bytes(4, 'big') for v in bmr))
    assert len(mask) == 27
    s = image_subheader(20, 20, 1, 'INT', 8, 8, 'R', 'B', 10, 10, ic='NM')
    open(os.path.join(OUT, 'p_mask.ntf'), 'wb').write(nitf_file([(s, mask + b''.join(units[k] for k in order))]))
    e = a.copy()
    e[0, 0:10, 10:20] = 255                          # the missing block reads as the pad pixel code
    save('p_mask.ntf', 'IC NM: block mask with one block not recorded, pad pixel code 255', 'pack',
         expected=np.moveaxis(e, 0, -1).astype(np.uint8), spec='table A-3(A) BMRnBNDm = 0xFFFFFFFF, TPXCD')

    # two image segments, a file TRE, an image TRE and one more through a TRE_OVERFLOW extension
    a1, a2 = pattern(1, 9, 11, 255, 74), pattern(3, 5, 6, 65535, 75)
    s1 = image_subheader(9, 11, 1, 'INT', 8, 8, 'R', 'B', 11, 9, iid='FIRST', tres=[('IMGTR1', b'one')])
    s2 = image_subheader(5, 6, 3, 'INT', 16, 16, 'R', 'P', 6, 5, iid='SECOND', irep='RGB', reps=['R', 'G', 'B'])
    sub, ddata = des_overflow('IXSHD', 1, [('OVERFL', b'from the overflow')])
    data = nitf_file([(s1, b''.join(pack(a1, 8, 8, 'R', 'INT', 'B', 11, 9))), (s2, b''.join(pack(a2, 16, 16, 'R', 'INT', 'P', 6, 5)))],
                     file_tres=[('FILET1', b'file data')], des=[(sub, ddata)])
    open(os.path.join(OUT, 'p_two_images.ntf'), 'wb').write(data)
    save('p_two_images.ntf', 'two image segments, TREs in the header, the first image and a TRE_OVERFLOW extension', 'pack',
         expected=np.moveaxis(a1, 0, -1).astype(np.uint8), spec='table A-1 NUMI, table A-8 DESOFLW / DESITEM, 5.8.3.1',
         extra=dict(second=dict(width=6, height=5, channels=3, sum=int(a2.sum())),
                    imageTres=['IMGTR1', 'OVERFL'], fileTres=['FILET1']))
    pxref.write_ref(os.path.join(OUT, 'p_two_images.ntf.second.ref'), np.moveaxis(a2, 0, -1).astype(np.uint16))

    # NITF 2.0 (MIL-STD-2500A): its own security fields, ONAME of 27, no FBKGC; ICORDS G
    a = pattern(1, 21, 31, 255, 76)
    geo = [(10.0, 50.0), (10.3, 50.0), (10.3, 49.8), (10.0, 49.8)]
    s = image_subheader(21, 31, 1, 'INT', 8, 8, 'R', 'B', 31, 21, version='2.0', icords='G', igeolo=igeolo_g(geo),
                        comments=['a NITF 2.0 comment'], title='ITITLE of a 2.0 file')
    open(os.path.join(OUT, 'p_nitf20.ntf'), 'wb').write(nitf_file([(s, b''.join(pack(a, 8, 8, 'R', 'INT', 'B', 31, 21)))],
                                                                  version='2.0', title='a NITF 2.0 file'))
    save('p_nitf20.ntf', 'NITF 2.0 header and image subheader, ICORDS G', 'pack', expected=np.moveaxis(a, 0, -1).astype(np.uint8),
         spec='MIL-STD-2500A table I, table III')

    names = sorted(n for n in os.listdir(OUT) if n.endswith('.ntf'))
    print(f'{len(names)} NITF fixtures in {OUT}')


if __name__ == '__main__':
    main()
