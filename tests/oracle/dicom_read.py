"""Reads DICOM files written by the encoder with independent readers, for DicomTests.

usage: dicom_read.py pydicom <file> <out>      pixel_array -> <out>.vref; transfer syntax, photometric, per-frame
                                               positions -> <out>.json
       dicom_read.py sitk <folder> <out>       the series in the folder through GDCM (SimpleITK's series reader) ->
                                               <out>.vref (values with the rescale applied) and <out>.json (spacing,
                                               origin, direction with the axes as columns, LPS)
Exit status 77 when the reader is missing (see pxref.missing).
"""
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pxref import write_vref, missing  # noqa: E402


def main():
    tool, path, out = sys.argv[1:4]
    if tool == 'pydicom':
        try:
            import pydicom
        except ImportError:
            missing('python:pydicom')
        ds = pydicom.dcmread(path)
        a = ds.pixel_array
        spp = int(ds.get('SamplesPerPixel', 1))
        write_vref(out + '.vref', np.ascontiguousarray(a), spp if spp > 1 else 1)
        meta = {'ts': str(ds.file_meta.TransferSyntaxUID), 'photometric': str(ds.PhotometricInterpretation)}
        if 'PerFrameFunctionalGroupsSequence' in ds:
            meta['positions'] = [[float(x) for x in g.PlanePositionSequence[0].ImagePositionPatient] for g in ds.PerFrameFunctionalGroupsSequence]
        if 'ImagePositionPatient' in ds:
            meta['position'] = [float(x) for x in ds.ImagePositionPatient]
        with open(out + '.json', 'w') as f:
            json.dump(meta, f)
    elif tool == 'sitk':
        try:
            import SimpleITK as sitk
        except ImportError:
            missing('python:SimpleITK')
        r = sitk.ImageSeriesReader()
        names = r.GetGDCMSeriesFileNames(path)
        r.SetFileNames(names)
        im = r.Execute()
        a = sitk.GetArrayFromImage(im)
        write_vref(out + '.vref', np.ascontiguousarray(a), im.GetNumberOfComponentsPerPixel())
        d = np.array(im.GetDirection()).reshape(3, 3)
        with open(out + '.json', 'w') as f:
            json.dump({'space': 'LPS', 'spacing': list(im.GetSpacing()), 'origin': list(im.GetOrigin()), 'direction': d.tolist()}, f)


if __name__ == '__main__':
    main()
