"""Bake the reviewed Wrap and its AF triangle addresses for the headless morph."""
import argparse
import gzip
import struct
import hashlib
import json
from pathlib import Path

import numpy as np
from scipy.spatial import cKDTree


def skin_triangles(snapshot):
    info = json.loads(snapshot.read_text())
    model = Path(info['sourcePath']).read_bytes()
    if hashlib.sha256(model).hexdigest() != info['sha256']:
        raise ValueError(f'Body source changed for {snapshot}')
    mesh = info['levels'][0]['meshes'][0]
    pos = next(f for f in mesh['fields'] if f['usage'] == 0)
    dtype = {2: '<f4', 3: '<f4', 14: '<f2'}[pos['type']]
    xyz = np.ndarray((mesh['vertexCount'], 3), dtype, model, pos['offset'],
                     (pos['stride'], np.dtype(dtype).itemsize))
    ix = np.frombuffer(model, '<u2', mesh['indexCount'], mesh['indexOffset'])
    return xyz[ix].astype(float).reshape(-1, 3, 3)*info['modelToMm']


def addresses(triangles, points):
    centres = triangles.mean(axis=1)
    tree = cKDTree(centres)
    radius = np.linalg.norm(triangles-centres[:, None], axis=2).max()
    faces, weights, nearest = [], [], []
    for point in points:
        limit, _ = tree.query(point)
        ids = np.sort(tree.query_ball_point(point, limit+radius+1e-8))
        tri = triangles[ids]
        a, b, c = tri.transpose(1, 0, 2)
        ab, ac, ap = b-a, c-a, point-a
        def dot(x, y):
            return (x*y).sum(axis=1)
        vv, vw, ww = dot(ab, ab), dot(ab, ac), dot(ac, ac)
        den = vv*ww-vw*vw
        v = (dot(ap, ab)*ww-dot(ap, ac)*vw)/np.maximum(den, 1e-30)
        w = (dot(ap, ac)*vv-dot(ap, ab)*vw)/np.maximum(den, 1e-30)
        bc = np.stack((1-v-w, v, w), axis=1)
        projected = np.einsum('ij,ijk->ik', bc, tri)
        best = np.where((den>1e-20)&(bc.min(axis=1)>=0), dot(projected-point, projected-point), np.inf)
        for i,j in ((0,1),(1,2),(2,0)):
            edge = tri[:,j]-tri[:,i]
            t = np.clip(dot(point-tri[:,i], edge)/np.maximum(dot(edge,edge),1e-30),0,1)
            diff = tri[:,i]+t[:,None]*edge-point
            distance = dot(diff,diff)
            take = distance<best
            bc[take] = 0
            bc[take,i],bc[take,j] = 1-t[take],t[take]
            best = np.minimum(best,distance)
        winner = best.argmin()
        faces.append(ids[winner]); weights.append(bc[winner]); nearest.append(bc[winner]@tri[winner])
    return np.asarray(faces), np.asarray(weights), np.asarray(nearest)


def bake(reference, top, legs, output):
    with np.load(reference) as data:
        rest, wrapped, faces = (data[k] for k in ('rest', 'vertices', 'faces'))
    skin = [skin_triangles(p) for p in (top, legs)]
    host, bary, nearest = addresses(np.concatenate(skin), wrapped)
    with gzip.open(output, 'wb') as f:
        f.write(struct.pack('<4s4i', b'AFW2', len(rest), len(faces), len(skin[0]), len(skin[1])))
        f.write(np.asarray(rest, '<f8').tobytes())
        f.write(np.asarray(faces, '<i4').tobytes())
        f.write(np.asarray(host, '<i4').tobytes())
        f.write(np.asarray(bary, '<f4').tobytes())
        f.write(np.asarray((wrapped-nearest) / 1000, '<f4').tobytes())


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('reference', 'top', 'legs', 'output'):
        parser.add_argument(name, type=Path)
    bake(**vars(parser.parse_args()))
