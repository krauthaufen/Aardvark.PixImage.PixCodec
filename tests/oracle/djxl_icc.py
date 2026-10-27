"""Write the ICC profile libjxl's djxl reads from a JPEG XL codestream (djxl --icc_out).

usage: djxl_icc.py <in.jxl> <out.icc>

Exit status 77 when djxl is missing (on the PATH or in .tmp/tools, see gen_png.tool).
"""
import os
import subprocess
import sys
import tempfile
from pxref import missing
from gen_png import tool


def main(src, dst):
    djxl = tool('djxl')
    if djxl is None:
        missing('libjxl', 'djxl (libjxl) on the PATH or in .tmp/tools')
    with tempfile.TemporaryDirectory() as d:
        subprocess.run([*djxl, src, os.path.join(d, 'out.png'), '--icc_out=' + dst, '--quiet'], check=True, capture_output=True)


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
