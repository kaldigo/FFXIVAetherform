"""Validate cleanup constraints and quantify changes against the current upscale."""
import argparse
import hashlib
import json
from pathlib import Path

import numpy as np
from scipy.sparse.csgraph import dijkstra

from clean_reference import graph
from investigate_coatee import front_depth
from reference_surface import areas
from transfer_landmarks import read_obj, write_points


def run(args):
    original=np.load(args.reference);candidate=np.load(args.candidate)
    v0,v,t=original['position'],candidate['position'],original['triangles']
    folder=Path(args.candidate).parent
    for key in ('triangles','uv','weights','boneNames','group','sourceFace','sourceBarycentric'):
        if not np.array_equal(original[key],candidate[key]):raise ValueError('Changed attribute: '+key)
    pinned=candidate['cleanupProtectedVertex']
    np.testing.assert_array_equal(v[pinned],v0[pinned])
    p,q=v0[t],v[t]
    cross=np.cross(p[:,1]-p[:,0],p[:,2]-p[:,0]);new=np.cross(q[:,1]-q[:,0],q[:,2]-q[:,0])
    reverse=int((np.sum(cross*new,axis=1)<=0).sum())
    if reverse or areas(q).min()<=1e-10:raise ValueError('Reversed or degenerate element')
    groups=original['group'];cloth=np.char.find(groups,'smallclothes')>=0
    skin_v=np.zeros(len(v),bool);skin_v[t[~cloth].ravel()]=True
    movement=np.linalg.norm(v-v0,axis=1)
    report=json.loads((folder/'cleanup-report.json').read_text())
    transition=report['protection']['skinTransitionMm']
    _,metric=graph(v0,t);cloth_v=np.unique(t[cloth])
    distance=dijkstra(metric,directed=False,indices=cloth_v,min_only=True)
    outside=skin_v&(distance>=transition) if transition else skin_v
    if movement[outside].max()>1e-10:raise ValueError('Skin moved outside allowed transition')
    profile=[]
    for y in (1195,1200,1210,1220,1230,1240,1250,1260,1270,1280):
        for x in (0,5,-5,10,-10,20,-20,40,-40,60,-60,80,-80):
            a,b=front_depth(p,(x,y)),front_depth(q,(x,y))
            if a is not None and b is not None:
                profile.append(dict(xMm=x,yMm=y,controlZmm=a['zMm'],cleanedZmm=b['zMm'],changeMm=b['zMm']-a['zMm']))
    midline=max(abs(row['changeMm']) for row in profile if row['xMm']==0)
    medial=max(abs(row['changeMm']) for row in profile if abs(row['xMm'])<=10)
    bounds=float(np.max(np.abs(np.r_[v.min(axis=0)-v0.min(axis=0),v.max(axis=0)-v0.max(axis=0)])))
    ov,ot=read_obj(folder/'vanilla-cleaned.obj')
    if not np.array_equal(ot,t) or not np.allclose(ov,v,atol=1e-8,rtol=0):raise ValueError('OBJ replay failed')
    landmarks=np.load(args.landmarks)
    source=np.einsum('ni,nij->nj',landmarks['sourceBary'],v[t[landmarks['sourceFace']]])
    shift=np.linalg.norm(source-landmarks['sourcePosition'],axis=1)
    write_points(folder/'Vanilla-Cleaned.points.json',landmarks['names'],landmarks['sourceFace'],landmarks['sourceBary'])
    updated=dict(landmarks);updated['sourcePosition']=source
    np.savez_compressed(folder/'paired-landmarks-candidate.npz',**updated)
    aperture_volume=lambda triangles: np.einsum('ni,ni->n',triangles[:,0],np.cross(triangles[:,1],triangles[:,2])).sum()/6
    audit=dict(sourceSha256=hashlib.sha256(Path(args.reference).read_bytes()).hexdigest(),
               selected=report['selected'],protectedVertexCount=int(pinned.sum()),protectedMaxMovementMm=float(movement[pinned].max()),
               skinOutsideTransitionMaxMovementMm=float(movement[outside].max()),
               skinMaxMovementMm=float(movement[skin_v].max()),skinTransitionMm=transition,
               cleavageMidlineMaxDepthChangeMm=midline,cleavageMedial10mmMaxDepthChangeMm=medial,
               globalBoundingBoxMaxChangeMm=bounds,maximumMovementMm=float(movement.max()),
               reversedTriangles=reverse,minimumTriangleAreaMm2=float(areas(q).min()),
               signedVolumeProxyChangeMl=float((aperture_volume(q)-aperture_volume(p))/1000),
               landmarkCount=len(source),landmarkAddressChanges=0,
               landmarkPositionChangeMm=dict(max=float(shift.max()),mean=float(shift.mean()),p95=float(np.percentile(shift,95))),
               profiles=profile,
               unchanged=['Original reference file','Current accepted point files','Connectivity','UVs','Skinning weights','AF assets'],
               limitations='Local normal-detail residual is a roughness proxy, not a semantic fabric detector. No global self-intersection or animation certification. The signed volume difference is a proxy with the same unchanged open boundaries, not a closed-body measurement.')
    (folder/'cleanup-audit.json').write_text(json.dumps(audit,indent=2)+'\n')
    print(json.dumps({k:v for k,v in audit.items() if k not in ('profiles','limitations','unchanged')},indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    for key in ('reference','candidate','landmarks'):p.add_argument('--'+key,required=True,type=Path)
    run(p.parse_args())
