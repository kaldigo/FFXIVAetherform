"""Audit the repaired candidate against its immutable controls."""
import argparse
import hashlib
import json
from pathlib import Path
import numpy as np
from reference_surface import areas
from repair_surface import boundaries


def audit(folder,previous,original,landmarks):
    folder=Path(folder);d=np.load(folder/'vanilla-repaired-reference.npz')
    old=np.load(previous);base=np.load(original);lm=np.load(landmarks)
    points=np.load(folder/'paired-landmarks-candidate.npz')
    v,t=d['position'],d['triangles'];v0=d['remeshedPosition'];keep=d['originalDenseFace']>=0
    def cross(p):return np.cross(p[:,1]-p[:,0],p[:,2]-p[:,0])
    assert all(np.isfinite(d[k]).all() for k in ('position','normal','uv','weights','cornerNativeBary'))
    assert areas(v[t]).min()>1e-10
    assert np.all(np.sum(cross(v[t])*cross(v0[t]),axis=1)>0),'Cumulative element reversal'
    for key in ('uv','weights'):np.testing.assert_array_equal(d[key][keep],old[key][d['originalDenseFace'][keep]])
    np.testing.assert_allclose(d['weights'].sum(-1),1,atol=1e-12)
    assert d['weights'].min()>=-1e-12
    np.testing.assert_array_equal(d['boneNames'],old['boneNames'])
    bc=d['cornerNativeBary'];assert bc.min()>=-1e-10
    np.testing.assert_allclose(bc.sum(-1),1,atol=1e-10)
    assert d['cornerNativeFace'].min()>=0 and d['cornerNativeFace'].max()<len(base['sourcePosition'])
    np.testing.assert_array_equal(points['names'],lm['names'])
    for key in ('targetFace','targetBary','targetPosition'):np.testing.assert_array_equal(points[key],lm[key])
    np.testing.assert_allclose(points['sourcePosition'],np.einsum('ni,nij->nj',points['sourceBary'],v[t[points['sourceFace']]]),atol=1e-10)
    np.testing.assert_array_equal(v[d['repairProtectedVertex']],v0[d['repairProtectedVertex']])
    result=dict(status='Passed',connectivity=boundaries(t),minimumTriangleAreaMm2=float(areas(v[t]).min()),
                cumulativeReversedTriangles=0,landmarks=len(points['names']),unchangedAFPoints=True,
                originalSha256=hashlib.sha256(Path(original).read_bytes()).hexdigest(),
                previousSha256=hashlib.sha256(Path(previous).read_bytes()).hexdigest(),
                retainedFacesWithExactUVsAndWeights=int(keep.sum()),
                note='Discrete geometry and attribute checks. Does not prove no global self-intersections or validate animation/physics.')
    (folder/'repair-audit.json').write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(result,indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    for k in ('folder','previous','original','landmarks'):p.add_argument('--'+k,type=Path,required=True)
    a=p.parse_args();audit(a.folder,a.previous,a.original,a.landmarks)
