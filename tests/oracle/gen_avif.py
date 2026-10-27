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

usage: python3 gen_avif.py [avif] [gainmap] [tonemap] [overlay] [container] [av1]
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


def avifdec_png(path):
    """avifdec's 8 bit output (the stored pixels: no transform applied)."""
    png = os.path.join(TMP, 'c.png')
    run(tool('avifdec'), '-c', 'dav1d', '-u', 'bilinear', '-d', 8, path, png)
    return np.asarray(Image.open(png))


def item_alone(items, iid, out):
    """A file with the one item (made visible) as the primary item: what avifdec decodes of it."""
    it = dict(items[iid])
    it['hidden'] = 0
    heif_write(out, {iid: it}, [], iid)
    return avifdec_png(out)


def gen_container():
    """
    Hand-made container cases in avif/container from grid_2x2.avif (2x2 tiles of 96x64), with a .ref each:
      * grid_iden.avif: the tiles through 'iden' items with irot 2 (MIAF 7.3.11.4.1; avifdec refuses it): the reference is
        avifdec's decode of each coded tile turned 180 degrees (irot, HEIF 6.5.10) and pasted.
      * grid_tile_irot.avif: irot 2 on the coded tiles themselves: avifdec's output, which ignores them.
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
    print('container')


if __name__ == '__main__':
    os.makedirs(OUT, exist_ok=True)
    what = sys.argv[1:] or ['avif', 'av1']
    if 'avif' in what: gen_stills()
    if 'gainmap' in what or 'avif' in what: gen_gainmap()
    if 'tonemap' in what or 'gainmap' in what or 'avif' in what: gen_gainmap_tonemap()
    if 'overlay' in what or 'avif' in what: gen_overlay()
    if 'container' in what or 'avif' in what: gen_container()
    if 'av1' in what: gen_av1()
    shutil.rmtree(TMP, ignore_errors=True)
