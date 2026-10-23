"""Fetches pydicom's DICOM test files for DicomTests (the Samples tests) and writes their references.

usage: python3 fetch_dicom_samples.py       (from tests/oracle; needs network access once)

The files are pydicom's test data: the ones pydicom 3.0.1 ships (pydicom/data/test_files, MIT licensed package; its
README says the images were obtained without restrictions and anonymised) and the larger ones it downloads on demand
from https://github.com/pydicom/pydicom-data at the commit pydicom pins (verified against pydicom's hashes.json). They
go - not committed - into downloads/dicom-samples of the test-data repository (git-ignored there; see
pxref.testdata_dir). Together they cover every uncompressed transfer syntax (implicit / explicit VR little endian,
big endian, deflated, files without preamble or meta information), RLE, JPEG baseline / extended / lossless, JPEG-LS,
JPEG 2000, HTJ2K, 1 to 32 bit samples, signed data, float pixel data, multi-frame files, palettes and YBR color.

References per file, as pydicom 3.0.1 with pylibjpeg (libjpeg, OpenJPEG, RLE) and pyjpegls decodes it:
  name.dcm.json      transfer syntax, size, frames, samples per pixel, photometric interpretation, dtype, rescale,
                     geometry (Image Position / Orientation, Pixel Spacing); "error" when pydicom cannot decode the
                     pixels, "image": false for files without them
  (the .vref files gzip compressed: name.dcm.vref.gz, ...)
  name.dcm.vref      Dataset.pixel_array: the stored values of all frames (YBR converted to RGB, no palette, no rescale)
  name.dcm.pal.vref  apply_color_lut of a PALETTE COLOR image
  name.dcm.mod.vref  apply_modality_lut (rescale slope / intercept or a modality LUT) when it changes the values
  name.dcm.gdcm.vref where pydicom fails: GDCM's decode through SimpleITK (rescale and palette applied), "gdcm": true
"""
import gzip
import hashlib
import json
import os
import shutil
import sys
import urllib.request
import warnings

import numpy as np

warnings.filterwarnings('ignore')
from pxref import write_vref, testdata_dir, missing  # noqa: E402

PYDICOM = '3.0.1'


def sha(b):
    return hashlib.sha256(b).hexdigest()


def write_ref(path, arr, channels):
    """pxref.write_vref, gzip compressed (the references of the larger multi-frame files are hundreds of MB raw)."""
    write_vref(path, arr, channels)
    with open(path, 'rb') as f, gzip.open(path + '.gz', 'wb', compresslevel=6) as g:
        shutil.copyfileobj(f, g)
    os.remove(path)


def floats(v):
    return None if v is None else [float(x) for x in (v if isinstance(v, (list, tuple)) or hasattr(v, '__iter__') else [v])]


def to_ours(arr, frames, spp):
    """pixel_array (frames?, rows, cols, spp?) is already in our layout (x fastest, channels interleaved)."""
    return np.ascontiguousarray(arr)


def reference(path):
    import pydicom
    from pydicom.pixels import apply_color_lut, apply_modality_lut
    ds = pydicom.dcmread(path, force=True)
    meta = getattr(ds, 'file_meta', None)
    info = {'ts': str(meta.TransferSyntaxUID) if meta is not None and 'TransferSyntaxUID' in meta else None}
    if not any(k in ds for k in ('PixelData', 'FloatPixelData', 'DoubleFloatPixelData')) or 'Rows' not in ds:
        info['image'] = False
        return info
    info['image'] = True
    frames = int(ds.get('NumberOfFrames', 1) or 1)
    spp = int(ds.get('SamplesPerPixel', 1))
    info.update(rows=int(ds.Rows), cols=int(ds.Columns), frames=frames, spp=spp,
                photometric=str(ds.get('PhotometricInterpretation', '')),
                bits_allocated=int(ds.get('BitsAllocated', 0)), bits_stored=int(ds.get('BitsStored', 0) or 0),
                signed=int(ds.get('PixelRepresentation', 0)) == 1)
    for k, key in (('ImagePositionPatient', 'position'), ('ImageOrientationPatient', 'orientation'), ('PixelSpacing', 'spacing'),
                   ('RescaleSlope', 'slope'), ('RescaleIntercept', 'intercept')):
        if k in ds and ds[k].value is not None and ds[k].value != '':
            info[key] = floats(ds[k].value)
    try:
        arr = ds.pixel_array
    except Exception as e:  # noqa: BLE001 - every failure is a reference result
        info['error'] = f'{type(e).__name__}: {e}'
        try:
            import SimpleITK as sitk
            im = sitk.ReadImage(path)
            a = sitk.GetArrayFromImage(im)
            c = im.GetNumberOfComponentsPerPixel()
            if a.shape[0] == 1:
                a = a[0]
            write_ref(path + '.gdcm.vref', np.ascontiguousarray(a), c)
            info['gdcm'] = True
        except Exception:  # noqa: BLE001 - neither can read it
            pass
        return info
    info['dtype'] = str(arr.dtype)
    info['shape'] = list(arr.shape)
    write_ref(path + '.vref', to_ours(arr, frames, spp), spp if spp > 1 else 1)
    if info['photometric'] == 'PALETTE COLOR':
        try:
            pal = apply_color_lut(arr, ds)
            write_ref(path + '.pal.vref', np.ascontiguousarray(pal), 3)
            info['palette'] = str(pal.dtype)
        except Exception as e:  # noqa: BLE001
            info['palette_error'] = f'{type(e).__name__}: {e}'
    elif spp == 1:
        try:
            mod = apply_modality_lut(arr, ds)
            if mod.dtype != arr.dtype or not np.array_equal(mod, arr):
                write_ref(path + '.mod.vref', np.ascontiguousarray(mod.astype(np.float64)), 1)
                info['modality'] = True
        except Exception as e:  # noqa: BLE001
            info['modality_error'] = f'{type(e).__name__}: {e}'
    return info


def main():
    try:
        import pydicom
        import pydicom.data
    except ImportError:
        missing('python:pydicom')
    if pydicom.__version__ != PYDICOM:
        print(f'pydicom {PYDICOM} wanted, found {pydicom.__version__}', file=sys.stderr)
        sys.exit(1)
    for mod in ('pylibjpeg', 'libjpeg', 'openjpeg', 'rle', 'jpeg_ls'):
        try:
            __import__(mod)
        except ImportError:
            missing('python:' + mod)
    out = testdata_dir('downloads', 'dicom-samples')
    if out is None:
        sys.exit(1)
    os.makedirs(out, exist_ok=True)
    data = os.path.dirname(pydicom.data.__file__)

    # shipped with pydicom
    src = os.path.join(data, 'test_files')
    for f in sorted(os.listdir(src)):
        if f.endswith('.dcm'):
            shutil.copyfile(os.path.join(src, f), os.path.join(out, f))
    shutil.copyfile(os.path.join(src, 'README.txt'), os.path.join(out, 'README.pydicom.txt'))
    dd = os.path.join(src, 'dicomdirtests')
    if os.path.isdir(dd):
        shutil.copytree(dd, os.path.join(out, 'dicomdirtests'), dirs_exist_ok=True)

    # pydicom-data, pinned and hashed by pydicom
    with open(os.path.join(data, 'urls.json')) as f:
        urls = json.load(f)
    with open(os.path.join(data, 'hashes.json')) as f:
        hashes = json.load(f)
    for name, url in sorted(urls.items()):
        dst = os.path.join(out, name)
        if not (os.path.exists(dst) and sha(open(dst, 'rb').read()) == hashes.get(name)):
            print('fetching', name)
            with urllib.request.urlopen(url) as r:
                b = r.read()
            if hashes.get(name) and sha(b) != hashes[name]:
                raise SystemExit(f'{name}: SHA-256 mismatch')
            with open(dst, 'wb') as f:
                f.write(b)

    n = 0
    for f in sorted(os.listdir(out)):
        if not f.endswith('.dcm'):
            continue
        p = os.path.join(out, f)
        for ext in ('.vref', '.pal.vref', '.mod.vref', '.gdcm.vref', '.vref.gz', '.pal.vref.gz', '.mod.vref.gz', '.gdcm.vref.gz'):
            if os.path.exists(p + ext):
                os.remove(p + ext)
        try:
            info = reference(p)
        except Exception as e:  # noqa: BLE001 - files pydicom cannot even parse
            info = {'parse_error': f'{type(e).__name__}: {e}'}
        info['sha256'] = sha(open(p, 'rb').read())
        with open(p + '.json', 'w') as fj:
            json.dump(info, fj, indent=1)
        n += 1
    print(f'{n} files in {out}')


if __name__ == '__main__':
    main()
