"""Decode a JPEG XL codestream with libjxl's djxl on one thread (--num_threads=0) to a float32 PXRF sidecar.

usage: djxl_single_thread.py <in.jxl> <out.ref>

For decodes where libjxl's multi-threaded output varies from run to run (VarDCT frames with vertically subsampled
chroma and Gaborish: the rows near group borders and the edge columns differ between runs); on one thread it is
deterministic. Exit status 77 when djxl is missing (on the PATH or in .tmp/tools, see gen_png.tool).
"""
import os
import subprocess
import sys
import tempfile
import numpy as np
from pxref import missing, write_ref
from gen_png import tool


def main(src, dst):
    djxl = tool('djxl')
    if djxl is None:
        missing('libjxl', 'djxl (libjxl) on the PATH or in .tmp/tools')
    with tempfile.TemporaryDirectory() as d:
        out = os.path.join(d, 'ref.npy')
        subprocess.run([*djxl, src, out, '--num_threads=0', '--quiet'], check=True, capture_output=True)
        a = np.load(out)   # frames x h x w x c float32
    a = a.reshape(a.shape[0] * a.shape[1], a.shape[2], a.shape[3])
    write_ref(dst, a.astype(np.float32))


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
