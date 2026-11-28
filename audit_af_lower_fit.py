"""Verify lower fitting copy against accepted breasts and its serialized mesh."""
import argparse,json
from pathlib import Path
import numpy as np
from audit_envelope_normals import independently_recalculate,read_export,angular
from reference_surface import unit
from transfer_landmarks import read_points
from mesh_crossings import crossing_pairs


def run(folder='outputs/af-fitting-genital-v4'):
    out=Path(folder);d=dict(np.load(out/'af-fitting-reference.npz'));parent=dict(np.load('outputs/af-fitting-chest-v2/af-fitting-reference.npz'))
    v,t=d['position'],d['triangles'];before=d['parentPosition'];changed=d['genitalModifiedVertex']
    for key in ('triangles','uv','weights','boneNames','nativeVertexIndices','sourcePosition'):np.testing.assert_array_equal(d[key],parent[key])
    np.testing.assert_array_equal(before,parent['position']);np.testing.assert_array_equal(v[~changed],before[~changed])
    np.testing.assert_array_equal(v[before[:,1]>950],before[before[:,1]>950]);np.testing.assert_array_equal(v[before[:,1]<840],before[before[:,1]<840]);np.testing.assert_array_equal(v[before[:,2]<-14],before[before[:,2]<-14])
    assert v[changed,1].min()>840 and v[changed,2].min()>-14
    top=(before[t][...,1].min(1)>1050);np.testing.assert_array_equal(d['normal'][top],parent['normal'][top])
    fresh=independently_recalculate(v,t);np.testing.assert_allclose(d['normal'],fresh,atol=1e-10,rtol=0)
    ov,ot,on=read_export(out/'AF-Fitting-Small-MediumB.obj');np.testing.assert_array_equal(ot,t);np.testing.assert_allclose(ov,v,atol=5.1e-10,rtol=0);np.testing.assert_allclose(on,fresh,atol=5.1e-10,rtol=0)
    names,face,bary=read_points(out/'AF-Fitting.points.json');landmarks=np.load(out/'af-fitting-landmarks.npz')
    np.testing.assert_array_equal(names,landmarks['names']);np.testing.assert_allclose(np.einsum('ni,nij->nj',bary,ov[ot[face]]),landmarks['position'],atol=1e-8,rtol=0)
    pairs=crossing_pairs(v[t],v[t],self_faces=t);centres=v[t[pairs]].mean((1,2));patch=t[d['genitalPatchFace']];p=v[patch];angles=[]
    for k in range(3):
        a=p[:,(k+1)%3]-p[:,k];b=p[:,(k+2)%3]-p[:,k];angles.append(np.degrees(np.arctan2(np.linalg.norm(np.cross(a,b),axis=1),np.sum(a*b,axis=1))))
    audit=dict(normalsIndependentlyVerified=True,maximumStoredNormalVectorError=float(np.linalg.norm(d['normal']-fresh,axis=-1).max()),
        maximumObjNormalAngleErrorDegrees=float(angular(unit(on),independently_recalculate(ov,ot)).max()),nativeTopologyUVsWeightsAndSourcePositionsExact=True,
        acceptedBreastsAndTheirNormalsExact=True,unchangedVerticesExact=True,thighGapBelow840Exact=True,buttAndRearRegionExact=True,
        minimumGenitalTriangleAngleDegrees=float(np.min(angles)),minimumGenitalEdgeLengthMm=float(np.linalg.norm(p-np.roll(p,1,axis=1),axis=-1).min()),
        editedVertexMinXYZ=v[changed].min(0).tolist(),editedVertexMaxXYZ=v[changed].max(0).tolist(),landmarkReplayVerified=len(names),
        remainingOriginalSeamContacts=dict(waist=int((centres[:,1]>900).sum()),knees=int((centres[:,1]<900).sum())))
    (out/'af-lower-independent-audit.json').write_text(json.dumps(audit,indent=2)+'\n');print(json.dumps(audit,indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('folder',nargs='?',default='outputs/af-fitting-genital-v4');run(p.parse_args().folder)
