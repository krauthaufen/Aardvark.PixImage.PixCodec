"""Generates tests/fixtures/avif (AVIF files) and tests/fixtures/av1 (AV1 IVF streams) from synthetic images.

  * name.avif: written by libavif's avifenc (codecs aom and rav1e) or avifgainmaputil.
  * name.avif.json: what the tests check: size, channels, bit depth, orientation, frame count, and the SHA-256 of the
    decoded AV1 planes of every frame ("yuv": dav1d through avifdec's y4m output, as raw planes Y, U, V with 16 bit
    little-endian samples above 8 bit) - the AV1 layer has to match bit for bit.
  * name.avif.ref (name.avif.N.ref for frame N of sequences): avifdec's RGB(A) output with bilinear chroma
    upsampling (8 bit, or 16 bit for deeper streams), which the tests compare within 1 (in the stream's bit depth).
  * av1/name.ivf + name.ivf.json: aomenc streams covering the inter tools with the SHA-256 of dav1d's raw output of
    all frames.

Tools: avifenc, avifdec, avifgainmaputil, aomenc, dav1d from PATH (tests/oracle/ci/install.sh avif) or
.tmp/tools/usr/bin (the Debian trixie packages libavif-bin 1.2.1, aom-tools 3.12.1 and dav1d 1.5.1 unpacked there, with
their libraries in .tmp/tools/usr/lib/x86_64-linux-gnu); the fixtures were made with those versions (the tonemap ones with
the libavif 1.4.2 release binaries).

usage: python3 gen_avif.py [avif] [gainmap] [tonemap] [overlay] [container] [mini] [av1]
"""
import hashlib
import json
import math
import os
import shutil
import struct
import subprocess
import sys
import numpy as np
from PIL import Image, ImageDraw, ImageFilter
from pxref import write_ref

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..', '..'))
OUT = os.path.join(HERE, '..', 'fixtures', 'avif')
OUT_AV1 = os.path.join(HERE, '..', 'fixtures', 'av1')
TMP = os.path.join(OUT, '_tmp')
LOCAL = os.path.join(ROOT, '.tmp', 'tools', 'usr')
ENV = dict(os.environ)
if os.path.isdir(LOCAL):
    ENV['LD_LIBRARY_PATH'] = os.path.join(LOCAL, 'lib', 'x86_64-linux-gnu') + os.pathsep + ENV.get('LD_LIBRARY_PATH', '')


def tool(name):
    p = os.path.join(LOCAL, 'bin', name)
    if os.path.exists(p): return p
    p = shutil.which(name)
    if not p: sys.exit(f'{name} not found (PATH or .tmp/tools/usr/bin)')
    return p


def run(*args):
    r = subprocess.run([str(a) for a in args], env=ENV, capture_output=True, text=True)
    if r.returncode != 0:
        sys.exit(f'failed: {" ".join(map(str, args))}\n{r.stdout[-2000:]}\n{r.stderr[-2000:]}')
    return r.stdout


# ------------------------------------------------------------------ synthetic content

def natural(w, h, seed=1, bits=8):
    """Smooth multi scale noise with soft stripes: looks like natural texture (no flat areas, no screen content)."""
    rng = np.random.default_rng(seed)
    acc = np.zeros((h, w, 3))
    for s, amp in [(64, 0.5), (16, 0.25), (4, 0.12), (1, 0.05)]:
        n = rng.random((max(2, h // s + 2), max(2, w // s + 2), 3))
        im = Image.fromarray((n * 255).astype(np.uint8)).resize((w, h), Image.BICUBIC)
        acc += amp * np.asarray(im, dtype=np.float64) / 255
    y, x = np.mgrid[0:h, 0:w]
    acc += 0.15 * ((x + 2 * y) % 97 < 48)[..., None]
    acc = (acc - acc.min()) / (acc.max() - acc.min() + 1e-9)
    if bits == 8:
        return np.asarray(Image.fromarray(np.round(acc * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(0.7)))
    return np.round(acc * 65535).astype(np.uint16)   # 16 bit PNG; avifenc -d reduces it


def screen(w, h, seed=3):
    """Few colours, glyph like boxes and a repeated tile: triggers palette and intra block copy in aom."""
    img = Image.new('RGB', (w, h), (240, 240, 245))
    d = ImageDraw.Draw(img)
    rng = np.random.default_rng(seed)
    cols = [(20, 20, 20), (200, 30, 30), (30, 90, 200), (250, 200, 0), (0, 150, 70)]
    for i in range(0, h, 12):
        for j in range(0, w, 7):
            if rng.random() < 0.6:
                c = cols[rng.integers(0, len(cols))]
                d.rectangle([j, i, j + 4, i + 8], outline=c)
                d.line([j, i + 4, j + 4, i + 4], fill=c)
    tile = img.crop((0, 0, 32, 24))
    for k in range(4):
        img.paste(tile, (w // 2 + (k * 32) % (w // 2), h // 2))
    return np.asarray(img)


def with_alpha(rgb):
    h, w = rgb.shape[:2]
    y, x = np.mgrid[0:h, 0:w]
    mx = 255 if rgb.dtype == np.uint8 else 65535
    a = np.clip((x / max(1, w - 1)) * 0.8 + ((y // 8) % 2) * 0.2, 0, 1)
    a[: h // 4, : w // 4] = 0
    return np.concatenate([rgb, np.round(a * mx).astype(rgb.dtype)[..., None]], -1)


def save_png(path, a):
    if a.dtype == np.uint16:
        # PIL cannot write 16 bit RGB: go through numpy -> PPM-like path via imagecodecs if needed
        import imagecodecs
        with open(path, 'wb') as f: f.write(imagecodecs.png_encode(a))
    else:
        Image.fromarray(a).save(path)


# ------------------------------------------------------------------ y4m / references

def read_y4m(path):
    d = open(path, 'rb').read()
    hdr_end = d.index(b'\n')
    toks = d[:hdr_end].decode().split()
    w = int(next(t for t in toks if t[0] == 'W')[1:])
    h = int(next(t for t in toks if t[0] == 'H')[1:])
    c = next((t[1:] for t in toks if t[0] == 'C'), '420jpeg')
    bd = 12 if c.endswith('12') else 10 if c.endswith('10') else 8
    sub = None if c.startswith('mono') else (0, 0) if c.startswith('444') else (1, 0) if c.startswith('422') else (1, 1)
    bps = 2 if bd > 8 else 1
    dims = [(w, h)] + ([] if sub is None else [((w + sub[0]) >> sub[0], (h + sub[1]) >> sub[1])] * 2)
    frames, pos = [], hdr_end + 1
    while pos < len(d):
        assert d[pos:pos + 5] == b'FRAME'
        pos = d.index(b'\n', pos) + 1
        n = sum(pw * ph for pw, ph in dims) * bps
        frames.append(d[pos:pos + n])
        pos += n + (w * h * bps if 'alpha' in c else 0)   # (an alpha plane follows: not part of the hash)
    return w, h, bd, sub, frames


def avif_yuv(path, index=0):
    y4m = os.path.join(TMP, 'out.y4m')
    run(tool('avifdec'), '-c', 'dav1d', '--index', index, path, y4m)
    return read_y4m(y4m)


def avif_rgb(path, deep, index=0):
    png = os.path.join(TMP, 'out.png')
    run(tool('avifdec'), '-c', 'dav1d', '-u', 'bilinear', '-d', 16 if deep else 8, '--index', index, path, png)
    if deep:
        import imagecodecs
        return imagecodecs.png_decode(open(png, 'rb').read())
    return np.asarray(Image.open(png))


def avif_frames(path):
    out = run(tool('avifdec'), '--info', path)
    for line in out.splitlines():
        if 'Frames' in line or 'frames' in line:
            try: return int(line.split(':')[-1].split()[0])
            except ValueError: pass
    return 1


# ------------------------------------------------------------------ cases

def still(name, img, args, expect=None, frames_from=None):
    os.makedirs(TMP, exist_ok=True)
    src = os.path.join(TMP, name + '.png')
    if isinstance(img, list):
        srcs = []
        for i, im in enumerate(img):
            p = os.path.join(TMP, f'{name}.{i}.png')
            save_png(p, im)
            srcs.append(p)
    else:
        save_png(src, img)
        srcs = [src]
    out = os.path.join(OUT, name + '.avif')
    run(tool('avifenc'), *args, *srcs, '-o', out)
    finish(name, out, expect, len(srcs) if isinstance(img, list) and '-g' not in args and '--grid' not in args else 1)


def finish(name, out, expect, nframes):
    info = {'frames': nframes}
    info.update(expect or {})
    yuv = []
    for i in range(nframes):
        w, h, bd, sub, fr = avif_yuv(out, i)
        yuv.append(hashlib.sha256(fr[0]).hexdigest())
        ref = avif_rgb(out, bd > 8, i)
        write_ref(out + ('.ref' if nframes == 1 else f'.{i}.ref'), ref)
    info.update({'width': int(ref.shape[1]), 'height': int(ref.shape[0]), 'channels': int(ref.shape[2]) if ref.ndim == 3 else 1,
                 'bitDepth': bd, 'subsampling': 'mono' if sub is None else f'{sub[0]}{sub[1]}', 'yuv': yuv})
    with open(out + '.json', 'w') as f: json.dump(info, f, indent=1)
    print(name, info['width'], info['height'], bd, info['subsampling'], os.path.getsize(out))


def gen_stills():
    nat = natural(160, 112)
    nat_odd = natural(117, 75, 2)
    still('nat_420_s6', nat, ['-y', '420', '-c', 'aom', '-s', '6', '-q', '50'])
    still('nat_420_s0', natural(96, 64, 4), ['-y', '420', '-c', 'aom', '-s', '0', '-q', '45'])
    still('nat_420_10', natural(160, 112, 5, 10), ['-y', '420', '-c', 'aom', '-s', '5', '-q', '50', '-d', '10'])
    still('nat_444_12', natural(96, 80, 6, 12), ['-c', 'aom', '-s', '5', '-q', '60', '-d', '12', '-y', '444'])
    still('nat_422_10', natural(96, 80, 7, 10), ['-c', 'aom', '-s', '5', '-q', '60', '-d', '10', '-y', '422'])
    still('nat_422_12', nat_odd, ['-c', 'aom', '-s', '5', '-q', '60', '-d', '12', '-y', '422'])
    still('nat_444_8', nat, ['-c', 'aom', '-s', '5', '-q', '55', '-y', '444'])
    still('gray_400', natural(96, 64, 8)[..., :1].repeat(3, -1), ['-c', 'aom', '-s', '5', '-q', '50', '-y', '400'])
    still('gray_400_10', natural(80, 64, 9, 10)[..., :1].repeat(3, -1), ['-c', 'aom', '-s', '5', '-q', '50', '-y', '400', '-d', '10'])
    still('lossless_444', natural(64, 48, 10), ['-c', 'aom', '-s', '6', '-l'])
    still('lossless_420', natural(64, 48, 11), ['-c', 'aom', '-s', '6', '-q', '100', '-y', '420'])
    still('odd_size', nat_odd, ['-y', '420', '-c', 'aom', '-s', '6', '-q', '40'])
    still('tiny', natural(7, 5, 12), ['-y', '420', '-c', 'aom', '-s', '6', '-q', '50'])
    still('rav1e_cdef', nat, ['-y', '420', '-c', 'rav1e', '-s', '5', '-q', '50'])
    still('rav1e_444_10', natural(96, 64, 13, 10), ['-c', 'rav1e', '-s', '6', '-q', '50', '-y', '444', '-d', '10'])
    still('tiles_2x2', natural(320, 256, 14), ['-y', '420', '-c', 'aom', '-s', '8', '-q', '40', '--tilerowslog2', '1', '--tilecolslog2', '1'])
    still('screen_444', screen(128, 96), ['-c', 'aom', '-s', '4', '-q', '70', '-y', '444', '-a', 'tune-content=screen'])
    still('screen_420_10', screen(128, 96, 4), ['-y', '420', '-c', 'aom', '-s', '6', '-q', '40', '-d', '10', '-a', 'tune-content=screen'])
    still('filmgrain', nat, ['-y', '420', '-c', 'aom', '-s', '6', '-q', '40', '-a', 'film-grain-test=1'])
    still('sb128_lr', natural(160, 112, 15), ['-y', '420', '-c', 'aom', '-s', '3', '-q', '30', '-a', 'sb-size=128', '-a', 'enable-restoration=1'])
    still('qm', nat, ['-y', '420', '-c', 'aom', '-s', '5', '-q', '40', '-a', 'enable-qm=1', '-a', 'qm-min=0'])
    still('limited_range', nat, ['-y', '420', '-c', 'aom', '-s', '6', '-q', '60', '-r', 'limited'])
    still('bt709', nat, ['-y', '420', '-c', 'aom', '-s', '6', '-q', '60', '--cicp', '1/13/1'])
    still('bt2020_pq_10', natural(96, 64, 16, 10), ['-y', '420', '-c', 'aom', '-s', '6', '-q', '60', '-d', '10', '--cicp', '9/16/9'])
    still('identity_444', nat, ['-c', 'aom', '-s', '6', '-q', '70', '-y', '444', '--cicp', '1/13/0'])
    still('ycgco_444', natural(64, 48, 17), ['-c', 'aom', '-s', '6', '-q', '70', '-y', '444', '--cicp', '2/13/8'])
    # alpha and premultiplied alpha
    still('alpha', with_alpha(nat), ['-y', '420', '-c', 'aom', '-s', '6', '-q', '50', '--qalpha', '60'])
    still('alpha_lossless_10', with_alpha(natural(96, 64, 18, 10)), ['-y', '420', '-c', 'aom', '-s', '6', '-q', '50', '--qalpha', '100', '-d', '10'])
    still('alpha_premultiplied', with_alpha(nat), ['-y', '420', '-c', 'aom', '-s', '6', '-q', '70', '--qalpha', '100', '-p'])
    # grids: one image split into 2x2 tiles
    still('grid_2x2', natural(192, 128, 19), ['-y', '420', '-c', 'aom', '-s', '7', '-q', '50', '-g', '2x2'])
    still('grid_alpha_3x1', with_alpha(natural(192, 64, 20)), ['-y', '420', '-c', 'aom', '-s', '7', '-q', '50', '-g', '3x1'])
    # transforms: avifdec ignores them, so the refs are the stored pixels and expect says what to apply
    still('irot1', nat, ['-y', '420', '-c', 'aom', '-s', '7', '-q', '50', '--irot', '1'], {'irot': 1})
    still('irot3_imir0', nat, ['-y', '420', '-c', 'aom', '-s', '7', '-q', '50', '--irot', '3', '--imir', '0'], {'irot': 3, 'imir': 0})
    still('imir1', nat, ['-y', '420', '-c', 'aom', '-s', '7', '-q', '50', '--imir', '1'], {'imir': 1})
    still('clap', nat, ['-y', '420', '-c', 'aom', '-s', '7', '-q', '50', '--crop', '10,20,100,60'], {'crop': [10, 20, 100, 60]})
    # metadata items
    exif = os.path.join(TMP, 'exif.bin')
    xmp = os.path.join(TMP, 'xmp.xml')
    os.makedirs(TMP, exist_ok=True)
    with open(exif, 'wb') as f: f.write(exif_blob())
    with open(xmp, 'w') as f: f.write(XMP)
    still('exif_xmp', nat, ['-y', '420', '-c', 'aom', '-s', '7', '-q', '50', '--exif', exif, '--xmp', xmp], {'make': 'PixCodecTest', 'xmpTitle': 'AVIF fixture'})
    icc = os.path.join(TMP, 'p3.icc')
    with open(icc, 'wb') as f: f.write(p3_icc())
    still('icc', nat, ['-y', '420', '-c', 'aom', '-s', '7', '-q', '50', '--icc', icc], {'icc': True})
    # image sequences
    seq = sequence_frames(128, 80, 8)
    still('seq', seq, ['-y', '420', '-c', 'aom', '-s', '8', '-q', '50', '--fps', '25', '-k', '4'], {'animated': True, 'durationMs': 40})
    still('seq_alpha', [with_alpha(f) for f in sequence_frames(96, 64, 5)], ['-y', '420', '-c', 'aom', '-s', '8', '-q', '50', '--fps', '10'], {'animated': True, 'durationMs': 100})
    still('seq_10', [f.astype(np.uint16) * 257 for f in sequence_frames(96, 64, 4)], ['-y', '420', '-c', 'aom', '-s', '8', '-q', '60', '-d', '10'], {'animated': True})
    # layered (progressive) image: two layers, the image is the highest one
    still('progressive', nat, ['-y', '420', '-c', 'aom', '-s', '7', '-q', '60', '--progressive'], {'layered': True})


def sequence_frames(w, h, n):
    big = natural(w * 2, h * 2, 21)
    im = Image.fromarray(big)
    frames = []
    for i in range(n):
        t = i / max(1, n - 1)
        a = math.radians(5 * t)
        sc = 1 + 0.2 * t
        cx, cy = w + 20 * t, h + 10 * t
        ca, sa = math.cos(a) / sc, math.sin(a) / sc
        coeffs = (ca, sa, cx - ca * w / 2 - sa * h / 2, -sa, ca, cy + sa * w / 2 - ca * h / 2)
        frames.append(np.asarray(im.transform((w, h), Image.AFFINE, coeffs, Image.BICUBIC)))
    return frames


XMP = '''<?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
<rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title><rdf:Alt><rdf:li xml:lang="x-default">AVIF fixture</rdf:li></rdf:Alt></dc:title></rdf:Description>
</rdf:RDF></x:xmpmeta><?xpacket end="w"?>'''


def exif_blob():
    """A minimal big endian TIFF with Make = PixCodecTest (avifenc stores it after a 4 byte offset)."""
    import struct
    make = b'PixCodecTest\0'
    ifd = struct.pack('>H', 1) + struct.pack('>HHII', 0x010F, 2, len(make), 26) + struct.pack('>I', 0)
    return b'MM\0*' + struct.pack('>I', 8) + ifd + make


def p3_icc():
    sys.path.insert(0, HERE)
    # a Display P3 profile written by LittleCMS through ctypes is overkill: use the tiny one ImageCms makes
    from PIL import ImageCms
    p = ImageCms.createProfile('sRGB')
    return ImageCms.ImageCmsProfile(p).tobytes()


# ------------------------------------------------------------------ AV1 streams (inter tools)

def write_y4m(path, frames, bd=8):
    h, w = frames[0].shape[:2]
    with open(path, 'wb') as f:
        cs = 'C420jpeg' if bd == 8 else f'C420p{bd}'
        f.write(f'YUV4MPEG2 W{w} H{h} F25:1 Ip A1:1 {cs}\n'.encode())
        mx = (1 << bd) - 1
        for fr in frames:
            a = fr.astype(np.float64) / 255
            y = 0.299 * a[..., 0] + 0.587 * a[..., 1] + 0.114 * a[..., 2]
            u = (a[..., 2] - y) / 1.772 + 0.5
            v = (a[..., 0] - y) / 1.402 + 0.5
            u = u.reshape(h // 2, 2, w // 2, 2).mean((1, 3))
            v = v.reshape(h // 2, 2, w // 2, 2).mean((1, 3))
            f.write(b'FRAME\n')
            for p in (y, u, v):
                f.write(np.clip(np.round(p * mx), 0, mx).astype(np.uint16 if bd > 8 else np.uint8).tobytes())


def ivf_case(name, src, args, frames):
    out = os.path.join(OUT_AV1, name + '.ivf')
    run(tool('aomenc'), '-t', '1', '--end-usage=q', f'--limit={frames}', *args, '--ivf', '-o', out, src)
    raw = os.path.join(TMP, 'out.yuv')
    run(tool('dav1d'), '-q', '-i', out, '-o', raw, '--muxer', 'yuv')
    with open(out + '.json', 'w') as f:
        json.dump({'sha256': hashlib.sha256(open(raw, 'rb').read()).hexdigest(), 'bytes': os.path.getsize(raw), 'frames': frames}, f, indent=1)
    print(name, os.path.getsize(out))


def gen_av1():
    os.makedirs(OUT_AV1, exist_ok=True)
    os.makedirs(TMP, exist_ok=True)
    frames = sequence_frames(128, 80, 16)
    y4m = os.path.join(TMP, 'seq.y4m')
    write_y4m(y4m, frames)
    n = 16
    ivf_case('rt', y4m, ['--cpu-used=8', '--rt', '--cq-level=40'], n)
    ivf_case('good', y4m, ['--cpu-used=3', '--cq-level=30'], n)
    ivf_case('altref', y4m, ['--cpu-used=2', '--cq-level=30', '--lag-in-frames=15', '--auto-alt-ref=1'], n)
    ivf_case('warp_global', y4m, ['--cpu-used=1', '--cq-level=25', '--enable-warped-motion=1', '--enable-global-motion=1'], n)
    ivf_case('obmc', y4m, ['--cpu-used=1', '--cq-level=25', '--enable-obmc=1', '--enable-warped-motion=0'], n)
    ivf_case('interintra_masked', y4m, ['--cpu-used=1', '--cq-level=25', '--enable-interintra-comp=1', '--enable-masked-comp=1',
                                        '--enable-smooth-interintra=1', '--enable-interinter-wedge=1', '--enable-interintra-wedge=1', '--lag-in-frames=15'], n)
    ivf_case('dual_filter', y4m, ['--cpu-used=2', '--cq-level=30', '--enable-dual-filter=1'], n)
    ivf_case('dist_wtd', y4m, ['--cpu-used=2', '--cq-level=30', '--enable-dist-wtd-comp=1', '--lag-in-frames=15'], n)
    ivf_case('error_resilient', y4m, ['--cpu-used=4', '--cq-level=30', '--error-resilient=1'], n)
    ivf_case('superres', y4m, ['--cpu-used=4', '--cq-level=30', '--superres-mode=1', '--superres-denominator=12'], n)
    ivf_case('resize', y4m, ['--cpu-used=4', '--cq-level=30', '--resize-mode=1', '--resize-denominator=12'], n)
    ivf_case('filmgrain', y4m, ['--cpu-used=4', '--cq-level=30', '--film-grain-test=1'], n)
    ivf_case('tiles', y4m, ['--cpu-used=4', '--cq-level=30', '--tile-columns=1', '--tile-rows=1'], n)
    ivf_case('sb128', y4m, ['--cpu-used=3', '--cq-level=30', '--sb-size=128'], n)
    y4m10 = os.path.join(TMP, 'seq10.y4m')
    write_y4m(y4m10, frames, 10)
    ivf_case('good_10', y4m10, ['--cpu-used=4', '--cq-level=30', '--bit-depth=10', '--input-bit-depth=10'], n)


# ------------------------------------------------------------------ hand-made HEIF files (overlay)

def boxes(d, pos, end):
    while pos + 8 <= end:
        size, typ = struct.unpack('>I4s', d[pos:pos + 8])
        hdr = 8
        if size == 1: size, hdr = struct.unpack('>Q', d[pos + 8:pos + 16])[0], 16
        elif size == 0: size = end - pos
        yield typ.decode('latin1'), pos, pos + hdr, pos + size
        pos += size


def primary_av1(path):
    """The primary item of a single image avifenc file: ([av1C, ispe, pixi boxes], coded data)."""
    d = open(path, 'rb').read()
    meta = next(b for b in boxes(d, 0, len(d)) if b[0] == 'meta')
    sub = {t: (s, p, e) for t, s, p, e in boxes(d, meta[2] + 4, meta[3])}
    pitm = struct.unpack('>H', d[sub['pitm'][1] + 4:sub['pitm'][1] + 6])[0]
    # iloc (version 0 / 1, as libavif writes it)
    _, p, e = sub['iloc']
    ver = d[p]
    q = p + 4
    osz, lsz = d[q] >> 4, d[q] & 15
    bsz, isz = d[q + 1] >> 4, (d[q + 1] & 15 if ver in (1, 2) else 0)
    q += 2
    count = struct.unpack('>H', d[q:q + 2])[0]; q += 2
    rd = lambda n, at: int.from_bytes(d[at:at + n], 'big') if n else 0
    data = None
    for _ in range(count):
        iid = struct.unpack('>H', d[q:q + 2])[0]; q += 2
        if ver in (1, 2): q += 2
        q += 2   # data_reference_index
        base = rd(bsz, q); q += bsz
        n = struct.unpack('>H', d[q:q + 2])[0]; q += 2
        parts = []
        for _ in range(n):
            q += isz
            off = rd(osz, q); q += osz
            ln = rd(lsz, q); q += lsz
            parts.append(d[base + off:base + off + ln])
        if iid == pitm: data = b''.join(parts)
    iprp = sub['iprp']
    pb = {t: (s, pp, ee) for t, s, pp, ee in boxes(d, iprp[1], iprp[2])}
    props = [d[s:ee] for t, s, pp, ee in boxes(d, pb['ipco'][1], pb['ipco'][2])]
    _, p, e = pb['ipma']
    ver, flags = d[p], int.from_bytes(d[p + 1:p + 4], 'big')
    q = p + 4
    count = struct.unpack('>I', d[q:q + 4])[0]; q += 4
    mine = []
    for _ in range(count):
        if ver < 1: iid = struct.unpack('>H', d[q:q + 2])[0]; q += 2
        else: iid = struct.unpack('>I', d[q:q + 4])[0]; q += 4
        n = d[q]; q += 1
        for _ in range(n):
            if flags & 1: idx = struct.unpack('>H', d[q:q + 2])[0] & 0x7FFF; q += 2
            else: idx = d[q] & 0x7F; q += 1
            if iid == pitm: mine.append(props[idx - 1])
    av1c = next(b for b in mine if b[4:8] == b'av1C')
    ispe = next(b for b in mine if b[4:8] == b'ispe')
    pixi = next(b for b in mine if b[4:8] == b'pixi')
    return [av1c, ispe, pixi], data


def box(t, payload): return struct.pack('>I4s', 8 + len(payload), t.encode()) + payload


def fullbox(t, ver, flags, payload): return box(t, struct.pack('>I', (ver << 24) | flags) + payload)


def heif_file(out, items, refs, primary):
    """
    A HEIF file of the given items: (type, [property boxes, the first one essential], payload); refs: (type, from, [to]),
    1 based item ids; the payloads go into mdat.
    """
    n = len(items)
    props, ipma_entries = b'', b''
    k = 0
    for i, (_, pr, _) in enumerate(items):
        idx = []
        for j, pb in enumerate(pr):
            props += pb
            k += 1
            idx.append((0x80 if j == 0 and pb[4:8] == b'av1C' else 0) | k)
        ipma_entries += struct.pack('>HB', i + 1, len(idx)) + bytes(idx)
    ipma = fullbox('ipma', 0, 0, struct.pack('>I', n) + ipma_entries)
    iinf = fullbox('iinf', 0, 0, struct.pack('>H', n) + b''.join(
        fullbox('infe', 2, 0, struct.pack('>HH4s', i + 1, 0, t.encode()) + b'\0') for i, (t, _, _) in enumerate(items)))
    iref = fullbox('iref', 0, 0, b''.join(box(t, struct.pack('>HH', f, len(to)) + b''.join(struct.pack('>H', x) for x in to)) for t, f, to in refs))
    hdlr = fullbox('hdlr', 0, 0, b'\0' * 4 + b'pict' + b'\0' * 12 + b'\0')
    pitm = fullbox('pitm', 0, 0, struct.pack('>H', primary))
    ftyp = box('ftyp', b'avif' + struct.pack('>I', 0) + b'avifmif1miaf')
    payloads = [pl for _, _, pl in items]

    def build(offsets):
        iloc = fullbox('iloc', 0, 0, bytes([0x44, 0x00]) + struct.pack('>H', n) + b''.join(
            struct.pack('>HHHII', i + 1, 0, 1, offsets[i], len(payloads[i])) for i in range(n)))
        meta = fullbox('meta', 0, 0, hdlr + pitm + iloc + iinf + (iref if refs else b'') + box('iprp', box('ipco', props) + ipma))
        return ftyp + meta
    head = build([0] * n)
    offs, o = [], len(head) + 8
    for pl in payloads: offs.append(o); o += len(pl)
    with open(out, 'wb') as f: f.write(build(offs) + box('mdat', b''.join(payloads)))


def heif_overlay(out, inputs, canvas, fill):
    """An overlay (iovl) primary item of the AV1 items of the given avifenc files at (x, y) on the canvas."""
    srcs = [primary_av1(p) for p, _, _ in inputs]
    n = len(srcs)
    iovl = struct.pack('>BB4HHH', 0, 0, *fill, *canvas) + b''.join(struct.pack('>hh', x, y) for _, x, y in inputs)
    items = [('av01', pr, d) for pr, d in srcs] + [('iovl', [fullbox('ispe', 0, 0, struct.pack('>II', *canvas))], iovl)]
    heif_file(out, items, [('dimg', n + 1, list(range(1, n + 1)))], n + 1)


def heif_items(path):
    """
    The items of a HEIF file: ({id: {'type', 'hidden', 'props': [(box, essential)], 'data'}}, refs [(type, from, [to])],
    primary id). Item data from the file or idat (construction methods 0 and 1).
    """
    d = open(path, 'rb').read()
    meta = next(b for b in boxes(d, 0, len(d)) if b[0] == 'meta')
    sub = {t: (s, p, e) for t, s, p, e in boxes(d, meta[2] + 4, meta[3])}
    u = lambda at, n: int.from_bytes(d[at:at + n], 'big')
    p = sub['pitm'][1]
    primary = u(p + 4, 2) if d[p] == 0 else u(p + 4, 4)
    items = {}
    _, p, e = sub['iinf']
    for t, s, ip, ie in boxes(d, p + (6 if d[p] == 0 else 8), e):
        v, fl = d[ip], u(ip + 1, 3)
        iid, o = (u(ip + 4, 2), ip + 6) if v == 2 else (u(ip + 4, 4), ip + 8)
        items[iid] = {'type': d[o + 2:o + 6].decode('latin1'), 'hidden': fl & 1, 'props': [], 'data': b''}
    _, p, e = sub['iloc']
    ver = d[p]
    q = p + 4
    osz, lsz, bsz, isz = d[q] >> 4, d[q] & 15, d[q + 1] >> 4, (d[q + 1] & 15 if ver in (1, 2) else 0)
    q += 2
    count = u(q, 2 if ver < 2 else 4); q += 2 if ver < 2 else 4
    idat = sub['idat'][1] if 'idat' in sub else 0
    for _ in range(count):
        iid = u(q, 2 if ver < 2 else 4); q += 2 if ver < 2 else 4
        cm = 0
        if ver in (1, 2): cm = u(q, 2) & 15; q += 2
        q += 2
        base = u(q, bsz); q += bsz
        n = u(q, 2); q += 2
        parts = []
        for _ in range(n):
            q += isz
            off = u(q, osz); q += osz
            ln = u(q, lsz); q += lsz
            at = (idat if cm == 1 else 0) + base + off
            parts.append(d[at:at + ln])
        if iid in items: items[iid]['data'] = b''.join(parts)
    refs = []
    if 'iref' in sub:
        _, p, e = sub['iref']
        w = 2 if d[p] == 0 else 4
        for t, s, rp, re in boxes(d, p + 4, e):
            n = u(rp + w, 2)
            refs.append((t, u(rp, w), [u(rp + w + 2 + w * i, w) for i in range(n)]))
    iprp = sub['iprp']
    pb = {t: (s, pp, ee) for t, s, pp, ee in boxes(d, iprp[1], iprp[2])}
    props = [d[s:ee] for t, s, pp, ee in boxes(d, pb['ipco'][1], pb['ipco'][2])]
    _, p, e = pb['ipma']
    ver, flags = d[p], u(p + 1, 3)
    q = p + 4
    count = u(q, 4); q += 4
    for _ in range(count):
        iid = u(q, 2 if ver < 1 else 4); q += 2 if ver < 1 else 4
        n = d[q]; q += 1
        for _ in range(n):
            if flags & 1: x = u(q, 2); q += 2; idx, ess = x & 0x7FFF, bool(x & 0x8000)
            else: x = d[q]; q += 1; idx, ess = x & 0x7F, bool(x & 0x80)
            if iid in items and idx: items[iid]['props'].append((props[idx - 1], ess))
    return items, refs, primary


def heif_write(out, items, refs, primary, major=b'avif', minor=0, compatible=(b'avif', b'mif1', b'miaf'), groups=(), ipro=()):
    """
    A HEIF file of items {id: {'type', 'hidden', 'props': [(box, essential)], 'data', optional 'protection' (1 based
    index into ipro)}}, references [(type, from, [to])], entity groups [(type, id, [entities])] and ipro sinf boxes; the
    item payloads go into mdat.
    """
    ids = sorted(items)
    props, ipma = b'', b''
    k = 0
    for i in ids:
        idx = []
        for pb, ess in items[i]['props']:
            props += pb
            k += 1
            idx.append((0x80 if ess else 0) | k)
        ipma += struct.pack('>HB', i, len(idx)) + bytes(idx)
    assert k < 128
    ipma = fullbox('ipma', 0, 0, struct.pack('>I', len(ids)) + ipma)
    iinf = fullbox('iinf', 0, 0, struct.pack('>H', len(ids)) + b''.join(
        fullbox('infe', 2, items[i].get('hidden', 0), struct.pack('>HH4s', i, items[i].get('protection', 0), items[i]['type'].encode()) + b'\0')
        for i in ids))
    iref = fullbox('iref', 0, 0, b''.join(box(t, struct.pack('>HH', f, len(to)) + b''.join(struct.pack('>H', x) for x in to)) for t, f, to in refs))
    grpl = box('grpl', b''.join(fullbox(t, 0, 0, struct.pack('>II', g, len(e)) + b''.join(struct.pack('>I', x) for x in e)) for t, g, e in groups))
    iprob = fullbox('ipro', 0, 0, struct.pack('>H', len(ipro)) + b''.join(ipro))
    hdlr = fullbox('hdlr', 0, 0, b'\0' * 4 + b'pict' + b'\0' * 12 + b'\0')
    pitm = fullbox('pitm', 0, 0, struct.pack('>H', primary))
    ftyp = box('ftyp', major + struct.pack('>I', minor) + b''.join(compatible))
    payloads = [items[i]['data'] for i in ids]

    def build(offsets):
        iloc = fullbox('iloc', 0, 0, bytes([0x44, 0x00]) + struct.pack('>H', len(ids)) + b''.join(
            struct.pack('>HHHII', i, 0, 1, offsets[j], len(payloads[j])) for j, i in enumerate(ids)))
        meta = fullbox('meta', 0, 0, hdlr + pitm + iloc + (iprob if ipro else b'') + iinf + (iref if refs else b'')
                       + box('iprp', box('ipco', props) + ipma) + (grpl if groups else b''))
        return ftyp + meta
    head = build([0] * len(ids))
    offs, o = [], len(head) + 8
    for pl in payloads: offs.append(o); o += len(pl)
    with open(out, 'wb') as f: f.write(build(offs) + box('mdat', b''.join(payloads)))


def prop(name, payload, full=True): return fullbox(name, 0, 0, payload) if full else box(name, payload)


def irot(a): return box('irot', bytes([a]))


def imir(axis): return box('imir', bytes([axis]))


def clap(w, h, ho=(0, 1), vo=(0, 1)): return box('clap', struct.pack('>IIIIiIiI', w, 1, h, 1, ho[0], ho[1], vo[0], vo[1]))


def gen_overlay():
    """
    iovl: two of the stills on a grey canvas, the second one partly outside it, in avif/overlay. libavif does not read
    overlays: the reference is the composition of the inputs' own .ref files (the test does it).
    """
    ins = [(os.path.join(OUT, 'nat_420_s6.avif'), 10, 5), (os.path.join(OUT, 'odd_size.avif'), 140, 100)]
    os.makedirs(os.path.join(OUT, 'overlay'), exist_ok=True)
    out = os.path.join(OUT, 'overlay', 'overlay.avif')
    canvas, fill = (240, 160), (13107, 26214, 39321, 65535)
    heif_overlay(out, ins, canvas, fill)
    with open(out + '.json', 'w') as f:
        json.dump({'canvas': list(canvas), 'fill': list(fill),
                   'inputs': [{'file': os.path.basename(p), 'x': x, 'y': y} for p, x, y in ins]}, f, indent=1)
    print('overlay', os.path.getsize(out))
    # a thumbnail item (thmb) of the primary image: the image of tiny.avif
    pr, d = primary_av1(os.path.join(OUT, 'nat_420_s6.avif'))
    tpr, td = primary_av1(os.path.join(OUT, 'tiny.avif'))
    heif_file(os.path.join(OUT, 'overlay', 'thumbnail.avif'), [('av01', pr, d), ('av01', tpr, td)], [('thmb', 2, [1])], 1)
    # a depth map (auxiliary item, not alpha): the image of gray_400.avif
    gpr, gd = primary_av1(os.path.join(OUT, 'gray_400.avif'))
    auxc = fullbox('auxC', 0, 0, b'urn:mpeg:hevc:2015:auxid:2\0')
    heif_file(os.path.join(OUT, 'overlay', 'depth.avif'), [('av01', pr, d), ('av01', gpr + [auxc], gd)], [('auxl', 2, [1])], 1)


def gen_gainmap():
    """An SDR base image with a gain map to a brighter BT.2100 PQ alternate (ISO 21496-1 tone map item)."""
    os.makedirs(TMP, exist_ok=True)
    base = natural(128, 96, 30)
    alt = (np.clip(base.astype(np.float64) / 255 * 1.6, 0, 1) ** 1.2 * 65535).astype(np.uint16)
    bp, ap = os.path.join(TMP, 'gm_base.png'), os.path.join(TMP, 'gm_alt.png')
    save_png(bp, base)
    save_png(ap, alt)
    out = os.path.join(OUT, 'gainmap.avif')
    run(tool('avifgainmaputil'), 'combine', bp, ap, out, '--cicp-alternate', '9/16/9', '--downscaling', '2', '-y', '420', '-q', '60')
    y4m = os.path.join(TMP, 'gm.y4m')
    run(tool('avifgainmaputil'), 'extractgainmap', out, y4m)
    w, h, bd, sub, fr = read_y4m(y4m)
    meta = run(tool('avifgainmaputil'), 'printmetadata', out)
    frac = lambda key: [int(x) for x in meta.split(key)[1].split('fraction:')[1].split(')')[0].split('/')]
    finish('gainmap', out, {'gainMap': {'width': w, 'height': h, 'bitDepth': bd, 'yuv': hashlib.sha256(fr[0]).hexdigest(),
                                        'baseHeadroom': frac('Base headroom'), 'alternateHeadroom': frac('Alternate headroom'),
                                        'alternatePrimaries': 9, 'alternateTransfer': 16}}, 1)


def tonemap_ref(src, out, headroom):
    """avifgainmaputil's gain map application at the given headroom as BT.2100 PQ (16 bit samples, stored geometry)."""
    png = os.path.join(TMP, 'tm.png')
    run(tool('avifgainmaputil'), 'tonemap', src, png, '--headroom', headroom, '--cicp-output', '9/16/9', '-d', 12)
    import imagecodecs
    write_ref(out, imagecodecs.png_decode(open(png, 'rb').read()))


def gen_gainmap_tonemap():
    """
    References for applying gain maps (ISO 21496-1 clause 6): avifgainmaputil tonemap of gainmap.avif at headroom 2.5
    (gainmap.avif.tonemap.ref), and in gainmap/ gain map files whose base image has transforms (4.5): irot.avif (irot 1,
    imir 1, with its tonemap reference in the stored geometry) and clap.avif (clap too; the gain map has half the size
    and libavif repeats the clap unscaled on the gain map item, which its strict parsing then rejects); deep.avif has a
    full size 10 bit gain map (with its tonemap reference).
    """
    os.makedirs(TMP, exist_ok=True)
    tonemap_ref(os.path.join(OUT, 'gainmap.avif'), os.path.join(OUT, 'gainmap.avif.tonemap.ref'), 2.5)
    base = natural(64, 48, 31)
    alt = (np.clip(base.astype(np.float64) / 255 * 1.8, 0, 1) ** 1.1 * 65535).astype(np.uint16)
    bp, ap = os.path.join(TMP, 'gt_base.png'), os.path.join(TMP, 'gt_alt.png')
    save_png(bp, base)
    save_png(ap, alt)
    os.makedirs(os.path.join(OUT, 'gainmap'), exist_ok=True)
    for name, extra, gm in [('irot', ['--irot', 1, '--imir', 1], ['--downscaling', 2]), ('clap', ['--crop', '8,4,48,40', '--irot', 1, '--imir', 1], ['--downscaling', 2]),
                            ('deep', [], ['--depth-gain-map', 10])]:
        b = os.path.join(TMP, name + '_base.avif')
        run(tool('avifenc'), '-q', 90, *extra, bp, b)
        out = os.path.join(OUT, 'gainmap', name + '.avif')
        run(tool('avifgainmaputil'), 'combine', b, ap, out, '--cicp-alternate', '9/16/9', *gm, '-q', 90, '--qgain-map', 90)
        if name != 'clap': tonemap_ref(out, out + '.tonemap.ref', 2.5)
        print(name, os.path.getsize(out))


CONTAINER = os.path.join(OUT, 'container')


def avifdec_png(path, index=0):
    """avifdec's 8 bit output (1.4.2 applies clap, irot and imir, and a sequence's sample entry clap)."""
    png = os.path.join(TMP, 'c.png')
    run(tool('avifdec'), '-c', 'dav1d', '-u', 'bilinear', '-d', 8, '--index', index, path, png)
    return np.asarray(Image.open(png))


def item_alone(items, iid, out):
    """A file with the one item (made visible) as the primary item: what avifdec decodes of it."""
    it = dict(items[iid])
    it['hidden'] = 0
    heif_write(out, {iid: it}, [], iid)
    return avifdec_png(out)


class Bits:
    def __init__(self): self.v, self.n = 0, 0

    def put(self, n, x):
        assert 0 <= x < (1 << n), (n, x)
        self.v, self.n = (self.v << n) | x, self.n + n

    def bytes(self):
        pad = -self.n % 8
        return (self.v << pad).to_bytes((self.n + pad) // 8, 'big')


def mini_file(out, w, h, av1c, data, sub=1, depth=8, full_range=1, cicp=None, orientation=1, alpha=None, premultiplied=0,
              icc=None, exif=None, xmp=None, compressed=0, gainmap=None, hdr=None, explicit_codec=False, minor=b'avif'):
    """
    A low-overhead file (HEIF Amd 2 Annex O): ftyp 'mif3' (minor_version: the codec brand) and a MinimizedImageBox of
    version 0. av1c / alpha[0] / gainmap['av1c'] are the av1C record (the property's payload), alpha = (av1c, data),
    gainmap = dict(w, h, av1c, data, metadata (ISO 21496-1, without the tmap version byte), mc, full_range, sub, depth,
    cicp (tmap: cp, tc, mc, full) or None, icc), hdr = dict of 4 byte / 24 byte ... bodies (clli, mdcv, amve, reve, ndwt).
    """
    b = Bits()
    large_dim = w > 128 or h > 128 or (gainmap is not None and (gainmap['w'] > 128 or gainmap['h'] > 128))
    hdr_flag = gainmap is not None or bool(hdr)
    b.put(2, 0)
    for f in (explicit_codec, False, full_range, alpha is not None, cicp is not None, hdr_flag, icc is not None, exif is not None, xmp is not None):
        b.put(1, int(bool(f)))
    b.put(2, sub)
    b.put(3, orientation - 1)
    b.put(1, int(large_dim))
    dn = 15 if large_dim else 7
    b.put(dn, w - 1)
    b.put(dn, h - 1)
    if sub in (1, 2): b.put(1, 0)   # chroma_is_horizontally_centered
    if sub == 1: b.put(1, 0)
    b.put(1, int(depth > 8))
    if depth > 8: b.put(3, depth - 9)
    if alpha is not None: b.put(1, premultiplied)
    if cicp is not None:
        for x in cicp: b.put(8, x)
    if explicit_codec:
        b.put(32, int.from_bytes(b'av01', 'big'))
        b.put(32, int.from_bytes(b'av1C', 'big'))
    hdr = hdr or {}
    if hdr_flag:
        b.put(1, int(gainmap is not None))
        if gainmap is not None:
            g = gainmap
            same = (g['w'], g['h']) == (w, h)
            b.put(1, int(same))
            if not same:
                b.put(dn, g['w'] - 1)
                b.put(dn, g['h'] - 1)
            b.put(8, g.get('mc', 6))
            b.put(1, g.get('full_range', 1))
            gs = g.get('sub', 1)
            b.put(2, gs)
            if gs in (1, 2): b.put(1, 0)
            if gs == 1: b.put(1, 0)
            b.put(1, 0)   # gainmap_float_flag
            gd = g.get('depth', 8)
            b.put(1, int(gd > 8))
            if gd > 8: b.put(3, gd - 9)
            b.put(1, int(g.get('icc') is not None))
            tc = g.get('cicp')
            b.put(1, int(tc is not None))
            if tc is not None:
                for x in tc[:3]: b.put(8, x)
                b.put(1, tc[3])
        names = ['clli', 'mdcv', 'cclv', 'amve', 'reve', 'ndwt']
        for k in names: b.put(1, int(k in hdr))
        for k in names:
            if k in hdr:
                for byte in hdr[k]: b.put(8, byte)
        if gainmap is not None:
            for k in names: b.put(1, 0)   # no tmap_* boxes
    meta_large = 0
    sizes = [len(x) for x in (icc, exif, xmp) if x is not None] + ([len(gainmap['metadata'])] + ([len(gainmap['icc'])] if gainmap.get('icc') else []) if gainmap else [])
    if sizes and max(sizes) > 1024: meta_large = 1
    if icc is not None or exif is not None or xmp is not None or gainmap is not None: b.put(1, meta_large)
    cc = [len(av1c)] + ([len(alpha[0])] if alpha else []) + ([len(gainmap['av1c'])] if gainmap else [])
    cc_large = int(max(cc) > 7)
    dd = [len(data)] + ([len(alpha[1])] if alpha else []) + ([len(gainmap['data'])] if gainmap else [])
    data_large = int(max(dd) > 32767)
    b.put(1, cc_large)
    b.put(1, data_large)
    mn, cn, dn2 = (20 if meta_large else 10), (12 if cc_large else 3), (28 if data_large else 15)
    if icc is not None: b.put(mn, len(icc) - 1)
    if gainmap is not None and gainmap.get('icc'): b.put(mn, len(gainmap['icc']) - 1)
    if gainmap is not None:
        b.put(mn, len(gainmap['metadata']))
        b.put(dn2, len(gainmap['data']))
        b.put(cn, len(gainmap['av1c']))
    b.put(cn, len(av1c))
    b.put(dn2, len(data) - 1)
    if alpha is not None:
        b.put(dn2, len(alpha[1]))
        b.put(cn, len(alpha[0]))
    ex, xm = exif, xmp
    if compressed:
        ex = zlib_raw(exif) if exif is not None else None
        xm = zlib_raw(xmp) if xmp is not None else None
    if exif is not None or xmp is not None: b.put(1, compressed)
    if ex is not None: b.put(mn, len(ex) - 1)
    if xm is not None: b.put(mn, len(xm) - 1)
    chunks = [b.bytes(), av1c]
    if alpha is not None: chunks.append(alpha[0])
    if gainmap is not None: chunks.append(gainmap['av1c'])
    if icc is not None: chunks.append(icc)
    if gainmap is not None:
        if gainmap.get('icc'): chunks.append(gainmap['icc'])
        chunks.append(gainmap['metadata'])
    if alpha is not None: chunks.append(alpha[1])
    if gainmap is not None: chunks.append(gainmap['data'])
    chunks.append(data)
    if ex is not None: chunks.append(ex)
    if xm is not None: chunks.append(xm)
    with open(out, 'wb') as f: f.write(box('ftyp', b'mif3' + minor + b'mif3') + box('mini', b''.join(chunks)))


def mini_equivalent(out, w, h, av1c, data, sub=1, depth=8, full_range=1, cicp=None, orientation=1, alpha=None, premultiplied=0,
                    icc=None, exif=None, xmp=None, gainmap=None, **_):
    """The MetaBox file a mini_file is equivalent to (Annex O.4), uncompressed Exif / XMP, for avifdec to decode."""
    if cicp is None: cicp = (2 if icc else 1, 2 if icc else 13, 2 if sub == 0 else 6)
    nc = 1 if sub == 0 else 3
    pixi = lambda n, d: prop('pixi', bytes([n] + [d] * n))
    ispe = lambda a, b_: prop('ispe', struct.pack('>II', a, b_))
    colr = lambda cp, tc, mc, fr: box('colr', b'nclx' + struct.pack('>HHHB', cp, tc, mc, fr << 7))
    rot = {3: 2, 5: 1, 6: 3, 7: 1, 8: 1}.get(orientation)
    mir = {2: 1, 4: 0, 5: 0, 7: 1}.get(orientation)
    tr = ([(irot(rot), True)] if rot is not None else []) + ([(imir(mir), True)] if mir is not None else [])
    items = {1: {'type': 'av01', 'props': [(box('av1C', av1c), True), (ispe(w, h), False), (pixi(nc, depth), False),
                                           (colr(*cicp, full_range), True)] + ([(box('colr', b'prof' + icc), True)] if icc else []) + tr,
                 'data': data}}
    refs = []
    if alpha:
        items[2] = {'type': 'av01', 'hidden': 1, 'data': alpha[1], 'props': [(box('av1C', alpha[0]), True), (ispe(w, h), False),
                    (prop('auxC', b'urn:mpeg:mpegB:cicp:systems:auxiliary:alpha\0'), True), (pixi(1, depth), False)] + tr}
        refs.append(('auxl', 2, [1]))
        if premultiplied: refs.append(('prem', 1, [2]))
    groups = []
    if gainmap:
        g = gainmap
        tw, th = (h, w) if orientation >= 5 else (w, h)
        tc = g.get('cicp') or (1, 13, 6, 1)
        items[3] = {'type': 'tmap', 'data': b'\0' + g['metadata'], 'props': [(ispe(tw, th), False), (colr(*tc), True)]}
        items[4] = {'type': 'av01', 'hidden': 1, 'data': g['data'], 'props': [(box('av1C', g['av1c']), True), (ispe(g['w'], g['h']), False),
                    (pixi(1 if g.get('sub', 1) == 0 else 3, g.get('depth', 8)), False), (colr(2, 2, g.get('mc', 6), g.get('full_range', 1)), True)] + tr}
        refs.append(('dimg', 3, [1, 4]))
        groups = [('altr', 5, [3, 1])]
    if exif:
        items[6] = {'type': 'Exif', 'hidden': 1, 'data': exif, 'props': []}
        refs.append(('cdsc', 6, [1]))
    heif_write(out, items, refs, 1, compatible=(b'avif', b'mif1', b'miaf') + ((b'tmap',) if gainmap else ()), groups=groups)


def zlib_raw(d):
    import zlib
    c = zlib.compressobj(9, zlib.DEFLATED, -15)
    return c.compress(d) + c.flush()


TRACK_PATH = ['moov', 'trak', 'mdia', 'minf', 'stbl', 'stsd', 'av01']
TRACK_HDR = {'stsd': 8, 'av01': 78}


def with_entry_box(d, extra):
    """The file with a box appended to the first av01 sample entry (the chunk offsets moved along)."""
    def rebuild(s, e, depth):
        t = TRACK_PATH[depth]
        h = TRACK_HDR.get(t, 0)
        kids = b''
        for kt, ks, kp, ke in boxes(d, s + 8 + h, e):
            kids += rebuild(ks, ke, depth + 1) if depth + 1 < len(TRACK_PATH) and kt == TRACK_PATH[depth + 1] else d[ks:ke]
        return box(t, d[s + 8:s + 8 + h] + kids + (extra if depth == len(TRACK_PATH) - 1 else b''))
    r = bytearray(b''.join(rebuild(s, e, 0) if t == 'moov' else d[s:e] for t, s, p, e in boxes(d, 0, len(d))))
    delta = len(r) - len(d)
    i = r.find(b'stco')   # mdat follows moov
    for k in range(struct.unpack('>I', r[i + 8:i + 12])[0]):
        o = i + 12 + 4 * k
        r[o:o + 4] = struct.pack('>I', struct.unpack('>I', r[o:o + 4])[0] + delta)
    return bytes(r)


def patch_track(d, duration=None, repeat=None, no_edts=False, matrix=None, size=None):
    """A sequence (tkhd and elst of version 1, as libavif writes) with the tkhd duration / matrix, the edit list's
    RepeatEdits flag or the tkhd width and height changed, or the edit list renamed to a free box."""
    d = bytearray(d)
    moov = next(b for b in boxes(d, 0, len(d)) if b[0] == 'moov')
    trak = next(b for b in boxes(d, moov[2], moov[3]) if b[0] == 'trak')
    sub = {t: (s, p, e) for t, s, p, e in boxes(d, trak[2], trak[3])}
    tk = sub['tkhd'][1]
    assert d[tk] == 1
    if duration is not None: d[tk + 28:tk + 36] = struct.pack('>Q', duration)
    if matrix is not None: d[tk + 52:tk + 88] = struct.pack('>9i', *matrix)
    if size is not None: d[tk + 88:tk + 96] = struct.pack('>II', size[0] << 16, size[1] << 16)
    s, p, e = sub['edts']
    if no_edts: d[s + 4:s + 8] = b'free'
    el = next(b for b in boxes(d, p, e) if b[0] == 'elst')[2]
    if repeat is not None: d[el + 1:el + 4] = bytes([0, 0, repeat])
    return bytes(d)


def gen_container():
    """
    Hand-made container cases in avif/container from grid_2x2.avif (2x2 tiles of 96x64), with a .ref each:
      * grid_iden.avif: the tiles through 'iden' items with irot 2 (MIAF 7.3.11.4.1; avifdec refuses it): the reference is
        avifdec's decode of each coded tile turned 180 degrees (irot, HEIF 6.5.10) and pasted.
      * grid_tile_irot.avif: irot 2 on the coded tiles themselves: avifdec's output, which ignores them.
    and from nat_420_s6.avif (160x112):
      * transform_order.avif: imir 0, irot 1, then clap 64x48 (in this association order). avifdec takes the clap on the
        stored image, (28, 62), and orders clap, irot, imir (as MIAF 7.3.6.7 requires; HEIF 6.5.1 says association
        order): avifdec's output, which is the stored pixels of nat_420_s6.avif cropped, turned and mirrored so.
    and from gainmap/irot.avif (base item 1 with irot 1, imir 1; tmap item 2; gain map item 3):
      * tmap_primary_irot.avif: the tmap item primary, with irot 2 of its own (avifdec rejects both): avifdec's output of
        gainmap/irot.avif (the base's output image) turned 180 degrees (HEIF Amd 1 6.6.2.4 and 6.6.1).
    and from nat_420_s6.avif:
      * altr_hvc1.avif: an hvc1 primary item (dummy data) in an altr group with the AV1 item (MIAF 7.3.12.2; avifdec
        gives up): the reference is nat_420_s6.avif.ref.
      * protected.avif: the AV1 item with protection_index 1 (ipro, scheme 'cenc', data not encrypted): avifdec's output,
        which ignores the protection.
    and from seq.avif (128x80, 8 frames; tkhd duration indefinite, edit list with RepeatEdits):
      * seq_clap.avif: a clap in the sample entry (MIAF 7.4.4.1): avifdec's frames 0 and 7, cropped (.N.ref).
      * seq_matrix.avif: a tkhd matrix turning 90 degrees and a tkhd size of 200x100 (MIAF 7.4.3): avifdec's frames 0
        and 7, which ignore both.
      * seq_loop_*.avif: edit list cases of MIAF Amd 1 Table C.1 for the loop count (the test has avifdec's counts).
    """
    os.makedirs(CONTAINER, exist_ok=True)
    os.makedirs(TMP, exist_ok=True)
    items, refs, primary = heif_items(os.path.join(OUT, 'grid_2x2.avif'))
    tiles = refs[0][2]
    it = {k: dict(v) for k, v in items.items()}
    for t in tiles: it[t + 10] = {'type': 'iden', 'hidden': 1, 'props': [(irot(2), True)], 'data': b''}
    out = os.path.join(CONTAINER, 'grid_iden.avif')
    heif_write(out, it, [('dimg', primary, [t + 10 for t in tiles])] + [('dimg', t + 10, [t]) for t in tiles], primary)
    parts = [np.rot90(item_alone(items, t, os.path.join(TMP, 'tile.avif')), 2) for t in tiles]
    write_ref(out + '.ref', np.concatenate([np.concatenate(parts[0:2], 1), np.concatenate(parts[2:4], 1)], 0))
    it = {k: dict(v, props=list(v['props'])) for k, v in items.items()}
    for t in tiles: it[t]['props'].append((irot(2), True))
    out = os.path.join(CONTAINER, 'grid_tile_irot.avif')
    heif_write(out, it, refs, primary)
    write_ref(out + '.ref', avifdec_png(out))
    items, refs, primary = heif_items(os.path.join(OUT, 'nat_420_s6.avif'))
    items[primary]['props'] += [(imir(0), True), (irot(1), True), (clap(64, 48, (-20, 1), (30, 1)), True)]
    out = os.path.join(CONTAINER, 'transform_order.avif')
    heif_write(out, items, refs, primary)
    write_ref(out + '.ref', avifdec_png(out))
    src = os.path.join(OUT, 'gainmap', 'irot.avif')
    items, refs, primary = heif_items(src)
    items[2]['props'].append((irot(2), True))
    out = os.path.join(CONTAINER, 'tmap_primary_irot.avif')
    heif_write(out, items, refs, 2, compatible=(b'avif', b'mif1', b'miaf', b'tmap'), groups=[('altr', 4, [2, 1])])
    write_ref(out + '.ref', np.rot90(avifdec_png(src), 2))
    items, refs, primary = heif_items(os.path.join(OUT, 'nat_420_s6.avif'))
    av = items[primary]
    ispe = next(p for p in av['props'] if p[0][4:8] == b'ispe')
    hv = {'type': 'hvc1', 'hidden': 0, 'props': [(box('hvcC', bytes(23)), True), ispe], 'data': bytes(100)}
    heif_write(os.path.join(CONTAINER, 'altr_hvc1.avif'), {1: hv, 2: av}, [], 1, groups=[('altr', 10, [1, 2])])
    sinf = box('sinf', box('frma', b'av01') + fullbox('schm', 0, 0, b'cenc' + struct.pack('>I', 0x10000)) + box('schi', b''))
    out = os.path.join(CONTAINER, 'protected.avif')
    heif_write(out, {1: dict(av, protection=1)}, [], 1, ipro=[sinf])
    write_ref(out + '.ref', avifdec_png(out))
    seq = open(os.path.join(OUT, 'seq.avif'), 'rb').read()
    for name, d in [('seq_clap', with_entry_box(seq, clap(96, 64, (-8, 1), (4, 1)))),
                    ('seq_matrix', patch_track(seq, matrix=(0, 0x10000, 0, -0x10000, 0, 0, 0, 0, 0x40000000), size=(200, 100)))]:
        out = os.path.join(CONTAINER, name + '.avif')
        with open(out, 'wb') as f: f.write(d)
        for i in (0, 7): write_ref(f'{out}.{i}.ref', avifdec_png(out, i))
    for name, kw in [('none', dict(no_edts=True)), ('once', dict(repeat=0)), ('r3_25', dict(duration=26)), ('r0_5', dict(duration=4)),
                     ('r4', dict(duration=32))]:
        with open(os.path.join(CONTAINER, f'seq_loop_{name}.avif'), 'wb') as f: f.write(patch_track(seq, **kw))
    gen_tmap_inputs()
    print('container')


def gen_tmap_inputs():
    """
    Derived items whose input is the tmap item of gainmap.avif (base 1, tmap 2 (hidden here), gain map 3), the derived
    item primary and in an altr group with the base. HEIF Amd 1 6.6.2.4.1: the derivation takes the tmap item's
    reconstructed image with the gain map fully applied, in the tmap item's colour (BT.2100 PQ). avifdec and
    avifgainmaputil 1.4.2 read none of them ("Primary item not found" for iden / iovl, "Not implemented" for the grid),
    so the references are the derivation done on avifgainmaputil's tonemap of gainmap.avif at headroom 2.5 (PQ 16 bit,
    gainmap.avif.tonemap.ref) and on avifdec's decode of gainmap.avif, the base (gainmap.avif.ref); the test derives them
    from those but for the overlay's (.tonemap.ref and .ref):
      * iden_tmap.avif: an iden item with irot 1 (90 degrees anti-clockwise).
      * iovl_tmap.avif: an iovl of 160x120 with the tmap at (16, 12) on fill (13107, 26214, 39321, 65535), which is a PQ
        signal for the HDR rendition (the overlay has the colour of its inputs, MIAF 7.3.11.3), sRGB for the base.
      * grid_tmap.avif: a 2x1 grid of two tmap items (both of base 1 and gain map 3; HEIF 6.6.1 allows derived inputs).
    """
    src = os.path.join(OUT, 'gainmap.avif')
    tonemap_ref(src, os.path.join(TMP, 'tm.ref'), 2.5)
    import imagecodecs
    tm = imagecodecs.png_decode(open(os.path.join(TMP, 'tm.png'), 'rb').read())
    sdr = avifdec_png(src)
    items, refs, primary = heif_items(src)
    assert primary == 1 and refs == [('dimg', 2, [1, 3])]
    ispe = lambda w, h: fullbox('ispe', 0, 0, struct.pack('>II', w, h))
    brands = (b'avif', b'mif1', b'miaf', b'tmap')

    def write(name, extra, derived_refs, hdr=None, base=None):
        it = {k: dict(v) for k, v in items.items()}
        it[2]['hidden'] = 1
        it.update(extra)
        out = os.path.join(CONTAINER, name)
        heif_write(out, it, refs + derived_refs, 4, compatible=brands, groups=[('altr', 10, [4, 1])])
        if hdr is not None: write_ref(out + '.tonemap.ref', hdr)
        if base is not None: write_ref(out + '.ref', base)
        print(name, os.path.getsize(out))
    write('iden_tmap.avif', {4: {'type': 'iden', 'hidden': 0, 'props': [(ispe(128, 96), False), (irot(1), True)], 'data': b''}},
          [('dimg', 4, [2])])
    fill = (13107, 26214, 39321, 65535)
    iovl = struct.pack('>BB4HHH', 0, 0, *fill, 160, 120) + struct.pack('>hh', 16, 12)
    hdr = np.empty((120, 160, 3), np.uint16); hdr[:] = fill[:3]; hdr[12:108, 16:144] = tm
    base = np.empty((120, 160, 3), np.uint8); base[:] = [round(v / 257) for v in fill[:3]]; base[12:108, 16:144] = sdr
    write('iovl_tmap.avif', {4: {'type': 'iovl', 'hidden': 0, 'props': [(ispe(160, 120), False)], 'data': iovl}},
          [('dimg', 4, [2])], hdr, base)
    grid = struct.pack('>BBBBHH', 0, 0, 0, 1, 256, 96)
    write('grid_tmap.avif', {5: dict(items[2], hidden=1), 4: {'type': 'grid', 'hidden': 0, 'props': [(ispe(256, 96), False)], 'data': grid}},
          [('dimg', 5, [1, 3]), ('dimg', 4, [2, 5])])


def gen_mini():
    """
    Low-overhead files (HEIF Amd 2 Annex O, MinimizedImageBox version 0) in avif/mini, repackaged from the stills, each
    with a .ref: avifdec 1.4.2 cannot read 'mini' (built without it), so the reference is its decode of the equivalent
    MetaBox file of O.4 (mini_equivalent, with the Exif / XMP uncompressed). The test also compares the metadata, ICC
    profile and gain map with the source files'.
    """
    d = os.path.join(OUT, 'mini')
    os.makedirs(d, exist_ok=True)
    os.makedirs(TMP, exist_ok=True)

    def src(name):
        items, refs, primary = heif_items(os.path.join(OUT, name))
        return items, primary

    def cfg(it): return next(p for p, _ in it['props'] if p[4:8] == b'av1C')[8:]
    nat, p = src('nat_420_s6.avif')
    al, ap = src('alpha.avif')
    n10, p10 = src('nat_420_10.avif')
    ic, ip = src('icc.avif')
    ex, _ = src('exif_xmp.avif')
    gm, gp = src('gainmap.avif')
    icc = next(p_[12:] for p_, _ in ic[ip]['props'] if p_[4:12] == b'colrprof')
    exif = next(v['data'] for v in ex.values() if v['type'] == 'Exif')
    xmp = next(v['data'] for v in ex.values() if v['type'] == 'mime')
    cases = {
        'basic': dict(w=160, h=112, av1c=cfg(nat[p]), data=nat[p]['data']),
        'alpha': dict(w=160, h=112, av1c=cfg(al[ap]), data=al[ap]['data'], alpha=(cfg(al[2]), al[2]['data']), orientation=6),
        'deep': dict(w=160, h=112, av1c=cfg(n10[p10]), data=n10[p10]['data'], depth=10, cicp=(1, 13, 6), explicit_codec=True, orientation=2),
        'meta': dict(w=160, h=112, av1c=cfg(ic[ip]), data=ic[ip]['data'], icc=icc, exif=exif, xmp=xmp, compressed=1),
        'gainmap': dict(w=128, h=96, av1c=cfg(gm[gp]), data=gm[gp]['data'], cicp=(1, 13, 2),
                        gainmap=dict(w=64, h=48, av1c=cfg(gm[3]), data=gm[3]['data'], metadata=gm[2]['data'][1:], mc=2, sub=3,
                                     cicp=(9, 16, 9, 1))),
    }
    for name, a in cases.items():
        out = os.path.join(d, name + '.avif')
        mini_file(out, **a)
        eq = os.path.join(TMP, 'equivalent.avif')
        mini_equivalent(eq, **a)
        write_ref(out + '.ref', avif_rgb(eq, a.get('depth', 8) > 8))
        print('mini', name, os.path.getsize(out))


if __name__ == '__main__':
    os.makedirs(OUT, exist_ok=True)
    what = sys.argv[1:] or ['avif', 'av1']
    if 'avif' in what: gen_stills()
    if 'gainmap' in what or 'avif' in what: gen_gainmap()
    if 'tonemap' in what or 'gainmap' in what or 'avif' in what: gen_gainmap_tonemap()
    if 'overlay' in what or 'avif' in what: gen_overlay()
    if 'container' in what or 'avif' in what: gen_container()
    if 'mini' in what or 'avif' in what: gen_mini()
    if 'av1' in what: gen_av1()
    shutil.rmtree(TMP, ignore_errors=True)
