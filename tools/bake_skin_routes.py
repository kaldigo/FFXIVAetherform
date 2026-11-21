"""Store UV-island routes as pairs of source and AF triangle addresses."""
import argparse
import gzip
import hashlib
import json
import struct
from pathlib import Path

import numpy as np


def bake(folder, model, output):
    with np.load(folder/'registration.npz') as reg, np.load(folder/'adjusted-native.npz') as native:
        parent = reg['sourceNativeFace']
        source_tri = native['morphed'][native['faces'][parent]]
        sample = reg['source'][reg['corner']]
        target_faces = reg['face'][reg['corner']]
        target_bary = reg['bary'][reg['corner']]
        source_bary = []
        for tri, points in zip(source_tri, sample):
            ab = (tri[1:] - tri[0]).T
            bc = np.linalg.lstsq(ab,(points-tri[0]).T,rcond=None)[0].T
            bary = np.c_[1-bc.sum(1),bc]
            if np.linalg.norm(bary@tri-points,axis=1).max()>.001:
                raise ValueError('Routed source leaves its source triangle.')
            source_bary.append(bary)
        mesh = native['materialMesh'][parent]
        first = native['nativeFirst'][parent]
        curves = json.loads((folder/'curves.json').read_text())
        with gzip.GzipFile(filename=str(output),mode='wb',mtime=0) as out:
            out.write(b'AFR1'+hashlib.sha256(model.read_bytes()).digest())
            out.write(struct.pack('<i',len(parent)))
            for m, offset, sb, faces, tb in zip(mesh, first, source_bary, target_faces, target_bary):
                out.write(struct.pack('<ii',m,offset))
                for b, fi, w in zip(sb,faces,tb):
                    out.write(struct.pack('<ddidd',*b[1:],fi,*w[1:]))
            samples=[(c['sourceJoin']['cloth'],s) for c in curves for s in c['samples']]
            out.write(struct.pack('<i',len(samples)))
            for c,s in samples:
                out.write(struct.pack('<iiiddidd',c['mesh'],c['first'],s['variable'],*s['sourceBary'][1:],s['face'],*s['bary'][1:]))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    for name in ('folder','model','output'):
        p.add_argument(name,type=Path)
    bake(**vars(p.parse_args()))
