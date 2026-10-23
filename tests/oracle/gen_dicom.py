"""DICOM fixtures for DicomTests: series, DICOMDIR media, multi-frame geometry, encapsulated frame layouts.

usage: python3 gen_dicom.py      (needs numpy, pydicom 3, imagecodecs)

Everything is synthetic (our own samples, written with pydicom), so it lives in tests/fixtures/dicom. Each case X has
X.vref (the expected samples, see pxref.write_vref: CT values in Hounsfield units, the slices sorted along the normal)
and X.json (expected geometry in LPS: spacing, origin, direction with the axes as columns; slope / intercept; sizes).

  series_ct/     11 CT slices, oblique, file names in no particular order, plus a localizer of another size in the same
                 series and a structured report without pixels: the scan must stack the 10 axial-ish slices
  cd/            a DICOMDIR (pydicom FileSet) with three series: the CT, a 4D MR (5 positions x 3 time points) and a PET
                 series whose slices have their own rescale slopes
  enhanced.dcm   enhanced CT multi-frame: per-frame plane positions, shared orientation / spacing / rescale
  rtdose.dcm     RT dose grid: Grid Frame Offset Vector running backwards, Dose Grid Scaling
  encap_*.dcm    3 frame JPEG-LS: basic offset table, no table with two fragments per frame, extended offset table
"""
import json
import os
import shutil
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pxref import write_vref  # noqa: E402

import pydicom  # noqa: E402
from pydicom.dataset import Dataset, FileMetaDataset  # noqa: E402
from pydicom.encaps import encapsulate, encapsulate_extended  # noqa: E402
from pydicom.uid import ExplicitVRLittleEndian, JPEGLSLossless  # noqa: E402

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'dicom')
CT = '1.2.840.10008.5.1.4.1.1.2'
MR = '1.2.840.10008.5.1.4.1.1.4'
PET = '1.2.840.10008.5.1.4.1.1.128'
ENH_CT = '1.2.840.10008.5.1.4.1.1.2.1'
RTDOSE = '1.2.840.10008.5.1.4.1.1.481.2'
SR = '1.2.840.10008.5.1.4.1.1.88.11'
ROOT = '1.2.826.0.1.3680043.10.1555.'   # (the fixtures' UIDs: this prefix and a counter, so that they are the same every run)
_next = [0]


def uid():
    _next[0] += 1
    return f'{ROOT}{_next[0]}'


def base(sop_class, study, series, modality, number, ts=ExplicitVRLittleEndian):
    ds = Dataset()
    ds.file_meta = FileMetaDataset()
    ds.file_meta.MediaStorageSOPClassUID = sop_class
    ds.file_meta.MediaStorageSOPInstanceUID = uid()
    ds.file_meta.TransferSyntaxUID = ts
    ds.SOPClassUID = sop_class
    ds.SOPInstanceUID = ds.file_meta.MediaStorageSOPInstanceUID
    ds.PatientName = 'Test^Patient'
    ds.PatientID = 'PX0001'
    ds.PatientBirthDate = '19700101'
    ds.PatientSex = 'O'
    ds.StudyInstanceUID = study
    ds.StudyDate = '20260101'
    ds.StudyTime = '120000'
    ds.StudyID = '1'
    ds.AccessionNumber = 'A1'
    ds.ReferringPhysicianName = ''
    ds.StudyDescription = 'Phantom'
    ds.SeriesInstanceUID = series
    ds.SeriesNumber = number
    ds.Modality = modality
    ds.SeriesDescription = f'{modality} series {number}'
    ds.FrameOfReferenceUID = study + '.9'
    return ds


def pixels(ds, arr, signed):
    rows, cols = arr.shape[-2:]
    ds.SamplesPerPixel = 1
    ds.PhotometricInterpretation = 'MONOCHROME2'
    ds.Rows, ds.Columns = rows, cols
    bits = arr.dtype.itemsize * 8
    ds.BitsAllocated = bits
    ds.BitsStored = 12 if bits == 16 else bits
    ds.HighBit = ds.BitsStored - 1
    ds.PixelRepresentation = 1 if signed else 0
    ds.PixelData = np.ascontiguousarray(arr).tobytes()


def save(ds, path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    ds.save_as(path, enforce_file_format=True)


def unit(v):
    v = np.asarray(v, dtype=np.float64)
    return v / np.linalg.norm(v)


def geometry(row, col, origin, step, spacing):
    """The json geometry: axes as columns (row direction, column direction, slice step)."""
    dz = float(np.linalg.norm(step))
    cols = [unit(row), unit(col), np.asarray(step) / dz]
    return dict(space='LPS', spacing=[spacing[1], spacing[0], dz], origin=[float(x) for x in origin],
                direction=[[float(cols[c][r]) for c in range(3)] for r in range(3)])


def dump(name, data, meta):
    write_vref(os.path.join(OUT, name + '.vref'), data)
    with open(os.path.join(OUT, name + '.json'), 'w') as f:
        json.dump(meta, f, indent=1)


def ct_slices(study, series, n, rng, number=2):
    """n oblique CT slices (stored Int16 with 12 bits, intercept -1024) and the expected HU volume in slice order."""
    row = unit([0.9, 0.1, 0.0]); col = unit(np.cross([0, 0, 1], row) * -1 + [0, 0, 0.2])
    col = unit(col - row * np.dot(col, row))
    normal = np.cross(row, col)
    origin = np.array([-120.5, -80.25, 33.0])
    spacing = (0.75, 0.6)   # rows, columns
    dz = 2.5
    vols, files = [], []
    for k in range(n):
        stored = (rng.integers(-2048, 2032, size=(24, 32)) // 16 * 16 + k).astype(np.int16)   # 12 bit signed
        ds = base(CT, study, series, 'CT', number)
        ds.InstanceNumber = n - k                # instance numbers run against the positions
        ds.ImagePositionPatient = [float(x) for x in origin + k * dz * normal]
        ds.ImageOrientationPatient = [float(x) for x in list(row) + list(col)]
        ds.PixelSpacing = list(spacing)
        ds.SliceThickness = dz
        ds.RescaleIntercept = -1024
        ds.RescaleSlope = 1
        ds.RescaleType = 'HU'
        pixels(ds, stored, True)
        files.append(ds)
        vols.append(stored.astype(np.int32) - 1024)
    meta = geometry(row, col, origin, dz * normal, spacing)
    meta.update(intercept=-1024.0, value_unit='HU', units=['mm', 'mm', 'mm'])
    return files, np.stack(vols), meta


def localizer(study, series):
    ds = base(CT, study, series, 'CT', 2)
    ds.InstanceNumber = 99
    ds.ImagePositionPatient = [0.0, 0.0, 0.0]
    ds.ImageOrientationPatient = [1.0, 0, 0, 0, 0, -1.0]
    ds.PixelSpacing = [1.0, 1.0]
    ds.RescaleIntercept = -1024
    ds.RescaleSlope = 1
    pixels(ds, np.zeros((16, 20), np.int16), True)
    return ds


def report(study):
    ds = base(SR, study, uid(), 'SR', 9)
    ds.InstanceNumber = 1
    ds.ValueType = 'CONTAINER'
    return ds


def mr4d(study, rng):
    series = uid()
    files, vols = [], {}
    origin = np.array([10.0, 20.0, -30.0])
    for t in range(3):
        for z in range(5):
            v = rng.integers(0, 1000, size=(12, 16)).astype(np.uint16)
            ds = base(MR, study, series, 'MR', 5)
            ds.InstanceNumber = 1 + z * 3 + t
            ds.TemporalPositionIdentifier = t + 1
            ds.ImagePositionPatient = [float(origin[0]), float(origin[1]), float(origin[2] + 4 * z)]
            ds.ImageOrientationPatient = [1.0, 0, 0, 0, 1.0, 0]
            ds.PixelSpacing = [1.5, 1.25]
            pixels(ds, v, False)
            files.append(ds)
            vols[(t, z)] = v
    data = np.stack([np.stack([vols[(t, z)] for z in range(5)]) for t in range(3)])
    meta = dict(space='LPS', spacing=[1.25, 1.5, 4.0], origin=list(origin), direction=[[1, 0, 0], [0, 1, 0], [0, 0, 1]])
    return files, data, meta


def pet(study, rng):
    series = uid()
    files, vols = [], []
    for z in range(4):
        v = rng.integers(0, 30000, size=(10, 10)).astype(np.uint16)
        slope = 0.5 + 0.25 * z
        ds = base(PET, study, series, 'PT', 7)
        ds.InstanceNumber = z + 1
        ds.ImagePositionPatient = [0.0, 0.0, float(-3 * z)]   # descending z: the normal points up, the order is reversed
        ds.ImageOrientationPatient = [1.0, 0, 0, 0, 1.0, 0]
        ds.PixelSpacing = [2.0, 2.0]
        ds.RescaleSlope = slope
        ds.RescaleIntercept = 0
        ds.Units = 'BQML'
        pixels(ds, v, False)
        ds.BitsStored = 16
        ds.HighBit = 15
        files.append(ds)
        vols.append((v.astype(np.float64) * slope).astype(np.float32))
    data = np.stack(vols[::-1])
    meta = dict(space='LPS', spacing=[2.0, 2.0, 3.0], origin=[0.0, 0.0, -9.0], direction=[[1, 0, 0], [0, 1, 0], [0, 0, 1]])
    return files, data, meta


def enhanced(rng):
    study = uid()
    ds = base(ENH_CT, study, uid(), 'CT', 3)
    ds.InstanceNumber = 1
    frames = 6
    stored = rng.integers(0, 2000, size=(frames, 8, 10)).astype(np.uint16)
    ds.NumberOfFrames = frames
    pixels(ds, stored, False)
    shared = Dataset()
    pm = Dataset(); pm.PixelSpacing = [0.5, 0.4]; pm.SliceThickness = 1.0
    po = Dataset(); po.ImageOrientationPatient = [0.0, 1.0, 0, 0, 0, -1.0]   # sagittal
    pv = Dataset(); pv.RescaleSlope = 2; pv.RescaleIntercept = -1000; pv.RescaleType = 'HU'
    shared.PixelMeasuresSequence = [pm]
    shared.PlaneOrientationSequence = [po]
    shared.PixelValueTransformationSequence = [pv]
    ds.SharedFunctionalGroupsSequence = [shared]
    per = []
    for f in range(frames):
        g = Dataset()
        pp = Dataset(); pp.ImagePositionPatient = [5.0 - 1.5 * f, 0.0, 0.0]
        g.PlanePositionSequence = [pp]
        per.append(g)
    ds.PerFrameFunctionalGroupsSequence = per
    save(ds, os.path.join(OUT, 'enhanced.dcm'))
    meta = dict(space='LPS', spacing=[0.4, 0.5, 1.5], origin=[5.0, 0.0, 0.0],
                direction=[[0, 0, -1], [1, 0, 0], [0, -1, 0]], slope_value=2.0, intercept=-1000.0, value_unit='HU')
    dump('enhanced.dcm', stored.astype(np.int32) * 2 - 1000, meta)


def rtdose(rng):
    ds = base(RTDOSE, uid(), uid(), 'RTDOSE', 4)
    ds.InstanceNumber = 1
    frames = 4
    stored = rng.integers(0, 1 << 20, size=(frames, 6, 7)).astype(np.uint32)
    ds.NumberOfFrames = frames
    pixels(ds, stored, False)
    ds.BitsStored = 32
    ds.HighBit = 31
    ds.ImagePositionPatient = [1.0, 2.0, 3.0]
    ds.ImageOrientationPatient = [1.0, 0, 0, 0, 1.0, 0]
    ds.PixelSpacing = [2.5, 2.5]
    ds.GridFrameOffsetVector = [0, -3, -6, -9]
    ds.DoseGridScaling = 1e-4
    ds.DoseUnits = 'GY'
    ds.DoseType = 'PHYSICAL'
    ds.DoseSummationType = 'PLAN'
    ds.FrameIncrementPointer = 0x3004000C
    save(ds, os.path.join(OUT, 'rtdose.dcm'))
    meta = dict(space='LPS', spacing=[2.5, 2.5, 3.0], origin=[1.0, 2.0, 3.0], direction=[[1, 0, 0], [0, 1, 0], [0, 0, -1]],
                slope_value=1e-4, value_unit='GY')
    dump('rtdose.dcm', (stored.astype(np.float64) * 1e-4), meta)


def encapsulated(rng):
    import imagecodecs
    frames = [rng.integers(0, 4096, size=(20, 24)).astype(np.uint16) for _ in range(3)]
    streams = [imagecodecs.jpegls_encode(f) for f in frames]
    for name, how in (('bot', 'bot'), ('frag', 'frag'), ('eot', 'eot')):
        ds = base('1.2.840.10008.5.1.4.1.1.7.3', uid(), uid(), 'OT', 6, ts=JPEGLSLossless)
        ds.InstanceNumber = 1
        ds.NumberOfFrames = 3
        pixels(ds, frames[0], False)
        if how == 'eot':
            data, offsets, lengths = encapsulate_extended(streams)
            ds.PixelData = data
            ds.ExtendedOffsetTable = offsets
            ds.ExtendedOffsetTableLengths = lengths
        else:
            ds.PixelData = encapsulate(streams, fragments_per_frame=1 if how == 'bot' else 2, has_bot=how == 'bot')
        ds['PixelData'].VR = 'OB'
        save(ds, os.path.join(OUT, f'encap_{name}.dcm'))
        dump(f'encap_{name}.dcm', np.stack(frames), {})


def main():
    if os.path.isdir(OUT):
        shutil.rmtree(OUT)
    os.makedirs(OUT)
    rng = np.random.default_rng(1555)

    # a plain folder: shuffled names, a localizer and a report in the same study
    study = uid()
    series = uid()
    files, vol, meta = ct_slices(study, series, 10, rng)
    names = [f'IM{(k * 7) % 10:04d}' for k in range(10)]
    for ds, nm in zip(files, names):
        save(ds, os.path.join(OUT, 'series_ct', nm))
    save(localizer(study, series), os.path.join(OUT, 'series_ct', 'IM0099'))
    save(report(study), os.path.join(OUT, 'series_ct', 'SR0001'))
    dump('series_ct', vol, meta)

    # a DICOMDIR with three series
    from pydicom.fileset import FileSet
    study = uid()
    fs = FileSet()
    ct, ctvol, ctmeta = ct_slices(study, uid(), 6, rng)
    mr, mrvol, mrmeta = mr4d(study, rng)
    pt, ptvol, ptmeta = pet(study, rng)
    for ds in ct + mr + pt:
        fs.add(ds)
    fs.write(os.path.join(OUT, 'cd'))
    dump('cd_ct', ctvol, ctmeta)
    dump('cd_mr', mrvol, mrmeta)
    dump('cd_pt', ptvol, ptmeta)
    # the series numbers tell them apart in the test
    with open(os.path.join(OUT, 'cd.json'), 'w') as f:
        json.dump({'2': 'cd_ct', '5': 'cd_mr', '7': 'cd_pt'}, f)

    enhanced(rng)
    rtdose(rng)
    encapsulated(rng)
    print('wrote', OUT)


if __name__ == '__main__':
    main()
