"""What GDAL reads from a NITF file: samples to OUT.ref (complex bands as real, imaginary channel pairs) and the rest
(metadata, placement, TREs) to OUT.json, the same report gen_nitf.py records for the fixtures.

usage: python3 tests/oracle/nitf_read.py FILE OUT
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pxref

try:
    import rasterio  # noqa: F401
except ImportError as e:
    pxref.missing('python:' + (e.name or 'rasterio'), 'rasterio (GDAL) reads the NITF files the encoder writes')

import gen_nitf

rep, a = gen_nitf.gdal_report(sys.argv[1])
if a is not None:
    pxref.write_ref(sys.argv[2] + '.ref', gen_nitf.to_ref(a))
with open(sys.argv[2] + '.json', 'w') as f:
    json.dump(rep, f, indent=1, sort_keys=True)
