#!/usr/bin/env python3
"""Fetches the JPEG XL conformance suite for JxlTests.Conformance.

Source: https://github.com/libjxl/conformance (BSD-3-Clause; the licences of the source images are listed in its
testcases/README.md, all allow reproduction for testing) at a pinned commit. Per test case input.jxl and test.json
are taken from the repository, the reference decodes that test.json lists by SHA-256 (reference_image.npy,
reference.icc, ...) from the suite's storage bucket. About 230 MB, written to
<test data>/downloads/jxl-conformance (test data = $PIXCODEC_TESTDATA or ../Aardvark.PixImage.PixCodec.TestData),
or to the directory given as the first argument. Files already present with the right hash are kept.
"""
import hashlib, io, json, os, sys, tarfile, urllib.request

COMMIT = 'b1d0f990b03e57bf6d137c365cd5dc8b470b9191'
BUCKET = 'https://storage.googleapis.com/jxl-conformance/objects/'


def default_dir():
    td = os.environ.get('PIXCODEC_TESTDATA') or os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', 'Aardvark.PixImage.PixCodec.TestData')
    return os.path.join(td, 'downloads', 'jxl-conformance')


def sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for b in iter(lambda: f.read(1 << 20), b''): h.update(b)
    return h.hexdigest()


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else default_dir()
    os.makedirs(out, exist_ok=True)
    data = urllib.request.urlopen(f'https://codeload.github.com/libjxl/conformance/tar.gz/{COMMIT}').read()
    tar = tarfile.open(fileobj=io.BytesIO(data))
    prefix = f'conformance-{COMMIT}/'
    cases = {}
    members = {m.name: m for m in tar.getmembers()}
    keep = ('input.jxl', 'test.json', 'reference_image.npy', 'reference.icc', 'original.icc')
    for m in tar.getmembers():
        if not m.name.startswith(prefix): continue
        rel = m.name[len(prefix):]
        if rel in ('LICENSE', 'testcases/README.md'):
            dst = os.path.join(out, 'LICENSE.conformance' if rel == 'LICENSE' else 'README.testcases.md')
        elif rel in ('testcases/main_level5.txt', 'testcases/main_level10.txt'):
            dst = os.path.join(out, os.path.basename(rel))   # the test cases of each level (ISO/IEC 18181-3 clause 5)
        elif rel.startswith('testcases/') and rel.count('/') == 2 and os.path.basename(rel) in keep:
            name = rel.split('/')[1]
            cases[name] = True
            dst = os.path.join(out, name, os.path.basename(rel))
        else:
            continue
        # the level 5 variants link to the files of their base case
        while m.issym():
            m = members[os.path.normpath(os.path.join(os.path.dirname(m.name), m.linkname))]
        if not m.isfile(): continue
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        with open(dst, 'wb') as f: f.write(tar.extractfile(m).read())
    for name in sorted(cases):
        desc = json.load(open(os.path.join(out, name, 'test.json')))
        for fname, sha in desc.get('sha256sums', {}).items():
            dst = os.path.join(out, name, fname)
            if os.path.exists(dst) and sha256(dst) == sha: continue
            blob = urllib.request.urlopen(BUCKET + sha).read()
            if hashlib.sha256(blob).hexdigest() != sha: raise SystemExit(f'hash mismatch for {name}/{fname}')
            with open(dst, 'wb') as f: f.write(blob)
        print('fetched', name)
    print('conformance files are in', out)


if __name__ == '__main__':
    main()
