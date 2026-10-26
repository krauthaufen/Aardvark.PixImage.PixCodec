"""Expectations for the NITFS Technical Board sample files (test-data repository, fixtures/nitf/jitc, fetched by its
fetch/fetch_nitf_samples.sh): writes fixtures/nitf/jitc/refs.txt there.

usage: python3 tests/oracle/gen_nitf_samples.py   (needs rasterio (GDAL), imagecodecs and numpy)

One line per image segment:  FILE IMAGE WIDTH HEIGHT CHANNELS DTYPE SHA256 SOURCE GDAL_SHA256
SHA256 hashes the samples in our layout (rows, columns, channels, little endian). SOURCE says where they come from:
  gdal       GDAL's samples; for a band with look-up tables GDAL's colour map applied to its indices (5.4.3.4)
  standard   GDAL reads the file differently from MIL-STD-2500C; the samples come from an independent reading of the
             standard written here, and GDAL_SHA256 records what GDAL returns:
               * 12 bit packed samples: one continuous bit stream, most significant bit first (5.1.9.1c, 5.4.3.3.1.1).
                 i_3430a confirms it: read this way its 250 distinct values lie 16 or 17 apart, an 8 bit picture
                 rescaled to 12 bits; GDAL's nibble order scatters them into clumps.
               * JPEG with IREP RGB: the components are R, G and B, no YCbCr conversion (table A-3 IREP, YCbCr601 only
                 for that case); decoded with libjpeg (imagecodecs) told the stream is RGB. GDAL converts them as YCbCr,
                 which turns the colour patches of U_2001d / U_2005a magenta and green.
Files the reader refuses, with the reason taken from the file's own header fields, are listed as
  FILE IMAGE - - - - - unsupported:EXCEPTION REASON
"""
import hashlib
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pxref

# files whose samples GDAL reads differently from the standard (see above)
STANDARD = {'i_3430a.ntf': 'packed12', 'U_2001d.ntf': 'jpeg-rgb', 'U_2005a.ntf': 'jpeg-rgb'}
# bands with look-up tables: GDAL hands out the indices and the tables as a colour map
LUT = {'NITF21_CGM_ANNO_Uncompressed_unmasked.ntf', 'U_1034a.ntf'}
# segments the reader refuses: (file, image) -> (exception, reason)
REFUSED = {
    ('bug3337.ntf', 0): ('ImageNotSupportedException', 'IC M4: vector quantization'),
    ('U_3058b.ntf', 0): ('ImageNotSupportedException', 'IC M4: vector quantization'),
    ('U_1036a.ntf', 0): ('ImageNotSupportedException', 'IC C1: bi-level ITU-T T.4'),
    ('U_1050a.ntf', 0): ('ImageNotSupportedException', 'IC C1: bi-level ITU-T T.4'),
    ('U_4003b.ntf', 0): ('ImageNotSupportedException', 'IC C1: bi-level ITU-T T.4'),
    ('U_4004b.ntf', 0): ('ImageNotSupportedException', 'IC C1: bi-level ITU-T T.4'),
    ('U_1123a.ntf', 1): ('ImageNotSupportedException', 'IC C1: bi-level ITU-T T.4'),
    ('U_1005a.ntf', 0): ('ImageNotSupportedException', 'JPEG without DQT: the default tables of MIL-STD-188-198A'),
    ('U_1025a.ntf', 0): ('ImageNotSupportedException', 'JPEG without DHT: the default tables of MIL-STD-188-198A'),
    ('U_1033a.ntf', 0): ('ImageNotSupportedException', 'JPEG without DQT: the default tables of MIL-STD-188-198A'),
    ('U_1092c.ntf', 0): ('ImageNotSupportedException', 'JPEG without DQT: the default tables of MIL-STD-188-198A'),
    ('U_4001b.ntf', 0): ('ImageNotSupportedException', 'JPEG without DHT: the default tables of MIL-STD-188-198A'),
    ('U_1123a.ntf', 4): ('ImageNotSupportedException', 'JPEG without DHT: the default tables of MIL-STD-188-198A'),
}


def sha(a):
    return hashlib.sha256(np.ascontiguousarray(a).astype(a.dtype.newbyteorder('<')).tobytes()).hexdigest()


def gdal_read(path, image, lut):
    import rasterio
    with rasterio.open(f'NITF_IM:{image}:{path}') as s:
        a = np.moveaxis(s.read(), 0, -1)
        if lut:
            cm = s.colormap(1)
            table = np.array([cm[i][:3] for i in range(max(cm) + 1)], dtype=np.uint8)
            a = table[a[..., 0]]
            if s.tags().get('NITF_IREP') != 'RGB/LUT':
                a = a[..., :1]
    return a


def subheader_fields(raw, image):
    """NCOLS, NROWS, NPPBH, NPPBV, NBPR, NBPC and the image data of segment `image`, from GDAL's metadata and the
    segment lengths: enough to unpack a single band uncompressed image (only used for i_3430a, a NITF 2.1 file)."""
    import rasterio
    hl = int(raw[354:360])
    numi = int(raw[360:363])
    lens = [(int(raw[363 + 16 * i:369 + 16 * i]), int(raw[369 + 16 * i:379 + 16 * i])) for i in range(numi)]
    at = hl + sum(a + b for a, b in lens[:image])
    return at + lens[image][0], lens[image][1]


def packed12(path, image):
    """MIL-STD-2500C 5.4.3.3.1.1: each block one bit stream of 12 bit values, most significant bit first, rows of
    NPPBH values, padded only at the end of the block; one band, IMODE B."""
    import rasterio
    with rasterio.open(path) as s:
        t = s.tags()
        w, h = s.width, s.height
    raw = open(path, 'rb').read()
    start, length = subheader_fields(raw, image)
    data = np.frombuffer(raw[start:start + length], dtype=np.uint8)
    # block geometry from the subheader text that GDAL reports
    bw, bh = int(t.get('NITF_NPPBH', 0) or 0), int(t.get('NITF_NPPBV', 0) or 0)
    if not bw:
        bw, bh = 512, 512
    nbpr, nbpc = -(-w // bw), -(-h // bh)
    block_bytes = (bw * bh * 12 + 7) // 8
    out = np.zeros((h, w), dtype=np.uint16)
    for k in range(nbpr * nbpc):
        b = data[k * block_bytes:(k + 1) * block_bytes].astype(np.uint16)
        n = bw * bh
        v = np.empty(n + (n & 1), dtype=np.uint16)
        g = b[:(n + 1) // 2 * 3].reshape(-1, 3)
        v[0::2] = g[:, 0] << 4 | g[:, 1] >> 4
        v[1::2] = (g[:, 1] & 15) << 8 | g[:, 2]
        blk = v[:n].reshape(bh, bw)
        by, bx = divmod(k, nbpr)
        y0, x0 = by * bh, bx * bw
        out[y0:min(h, y0 + bh), x0:min(w, x0 + bw)] = blk[:min(bh, h - y0), :min(bw, w - x0)]
    return out[..., None]


def jpeg_rgb(path):
    import imagecodecs
    raw = open(path, 'rb').read()
    return imagecodecs.jpeg8_decode(raw[raw.find(b'\xff\xd8\xff'):], colorspace='RGB', outcolorspace='RGB')


def main():
    try:
        import rasterio
        import imagecodecs  # noqa: F401
    except ImportError as e:
        pxref.missing('python:' + (e.name or 'rasterio'), 'rasterio (GDAL) and imagecodecs read the NITF samples')
    d = pxref.testdata_dir('fixtures', 'nitf', 'jitc')
    if d is None or not os.path.isdir(d):
        sys.exit('fetch the samples first: fetch/fetch_nitf_samples.sh of the test-data repository')
    lines = []
    for name in sorted(l.split()[1] for l in open(os.path.join(d, 'sha256.txt'))):
        path = os.path.join(d, name)
        try:
            with rasterio.open(path) as s:
                count = max(1, len(s.subdatasets))
        except Exception:
            # GDAL reads no image: a NITF 1.1 header, or a file of graphics only
            why = 'not a NITF 2.0 / 2.1 header' if open(path, 'rb').read(9) == b'NITF01.10' else 'no image segment'
            lines.append(f'{name} 0 - - - - - unsupported:ImageFormatException {why}')
            continue
        for i in range(count):
            if (name, i) in REFUSED:
                ex, why = REFUSED[(name, i)]
                lines.append(f'{name} {i} - - - - - unsupported:{ex} {why}')
                continue
            try:
                g = gdal_read(path, i, name in LUT)
            except Exception as e:
                lines.append(f'{name} {i} - - - - - unsupported:ImageFormatException {str(e).splitlines()[0][:60]}')
                continue
            if g.ndim == 2:
                g = g[..., None]
            kind = STANDARD.get(name)
            a, source = g, 'gdal'
            if kind == 'packed12':
                a, source = packed12(path, i), 'standard'
            elif kind == 'jpeg-rgb':
                a, source = jpeg_rgb(path), 'standard'
            h, w, c = a.shape
            lines.append(f'{name} {i} {w} {h} {c} {a.dtype} {sha(a)} {source} {sha(g)}')
    with open(os.path.join(d, 'refs.txt'), 'w') as f:
        f.write('\n'.join(lines) + '\n')
    print(f'{len(lines)} image segments in {d}/refs.txt')


if __name__ == '__main__':
    main()
