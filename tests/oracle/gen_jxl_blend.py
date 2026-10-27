"""Generates tests/fixtures/jxl-spec/blend_*.jxl: small float RGBA streams whose last frame blends the colour with
kBlend while the alpha channel's ec_blending_info has another mode (kReplace, kAdd, kMul) or another source, with
djxl's float32 decodes (premultiplied colour as stored) as name.jxl.ref. The streams are hand-built by the C# test
JxlAuditRenderTests.BlendFixtures (no encoder writes such frames); djxl is the oracle (18181-1:2024 Table F.7 vs
libjxl, see JxlBlender.Blend).

usage: python3 gen_jxl_blend.py (djxl from PATH, .tmp/libjxl/tools or .tmp/tools/jxl/tools)
"""
import os
import shutil
import subprocess
import sys
import tempfile
import numpy as np
from pxref import write_ref

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(HERE, '..', '..')
OUT = os.path.join(HERE, '..', 'fixtures', 'jxl-spec')
NAMES = ['blend_alpha_replace_full', 'blend_alpha_replace_src2', 'blend_alpha_add', 'blend_alpha_mul_premul']


def djxl():
    p = shutil.which('djxl')
    if p: return p
    for d in (os.path.join(ROOT, '.tmp', 'libjxl', 'tools'), os.path.join(ROOT, '.tmp', 'tools', 'jxl', 'tools')):
        p = os.path.join(d, 'djxl')
        if os.path.exists(p): return p
    sys.exit('djxl not found')


def main():
    tool = djxl()
    env = dict(os.environ, PIXCODEC_JXLBLEND_FIXTURES=os.path.abspath(OUT))
    subprocess.run(['dotnet', 'test', os.path.join(ROOT, 'tests', 'Aardvark.PixImage.PixCodec.Codecs.Tests'), '-c', 'Release',
                    '-f', 'net10.0', '--filter', 'FullyQualifiedName~JxlAuditRenderTests.FrameBlendFixturesAreDjxls'],
                   check=True, env=env)
    with tempfile.TemporaryDirectory() as tmp:
        for n in NAMES:
            path = os.path.join(OUT, n + '.jxl')
            out = os.path.join(tmp, 'ref.npy')
            subprocess.run([tool, path, out, '--quiet'], check=True, capture_output=True)
            ref = np.load(out)   # frames x h x w x c float32
            assert ref.shape[0] == 1 and ref.dtype == np.float32
            write_ref(path + '.ref', ref[0])
            print(n, ref.shape[1:], os.path.getsize(path), 'bytes')


if __name__ == '__main__':
    main()
