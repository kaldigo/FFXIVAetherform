"""Larger-scale cleanup after local cap repair, with explicit point rebinding."""
import argparse
import json
from pathlib import Path

import numpy as np
from scipy.sparse.csgraph import dijkstra

from clean_reference import constraints,graph,local_quadratic,normals,roughness,fair
from compare_af_bases import closest_surface
from investigate_coatee import front_depth
from reference_surface import write_obj
from repair_surface import boundaries
from transfer_landmarks import write_points


def repair_constraints(v,t,groups,metric):
    pinned,_,_,_=constraints(v,t,groups,metric,transition_mm=24.)
    pelvis=(v[:,1]>805)&(v[:,1]<1000)&(abs(v[:,0])<175)
    pinned[pelvis]=False
    # The source's medial chest and broad support extrema remain exact.
    pinned[(abs(v[:,0])<=14)&(v[:,1]>=1190)&(v[:,2]>60)]=True
    for prefix in ('top','legs'):
        ids=np.unique(t[np.char.startswith(groups,prefix+'_smallclothes')])
        for axis in range(3):
            pinned[ids[np.argmin(v[ids,axis])]]=True;pinned[ids[np.argmax(v[ids,axis])]]=True
        for side in (-1,1):
            half=ids[v[ids,0]*side>=0]
            pinned[half[np.argmax(v[half,2])]]=True;pinned[half[np.argmin(v[half,2])]]=True
    distance=dijkstra(metric,directed=False,indices=np.flatnonzero(pinned),min_only=True)
    taper=np.clip(distance/10.,0,1);taper=taper*taper*(3-2*taper)
    taper[pinned]=0
    return pinned,taper,pelvis


def bind_points(old,data,patches,landmarks,v):
    old_t=old['triangles'];old_v=old['position'];t=data['triangles']
    lookup={int(old_id):i for i,old_id in enumerate(data['originalDenseFace']) if old_id>=0}
    faces=[];bary=[];rebuilt=[]
    for name,fi,bc in zip(landmarks['names'],landmarks['sourceFace'],landmarks['sourceBary']):
        if int(fi) in lookup:
            faces.append(lookup[int(fi)]);bary.append(bc);continue
        patch=next((p for p in patches if int(fi) in p['oldFaces']),None)
        if patch is None:raise ValueError('Lost point face: '+str(name))
        start=patch['firstNewFace'];ids=np.arange(start,start+patch['newFaceCount'])
        origin=np.array(patch['origin']);basis=np.array(patch['basis'])
        point=bc@old_v[old_t[fi]];xy=(point-origin)@basis[:2].T
        # Use the original remeshing chart for identity; final fairing changes
        # shape, not these material addresses.
        triangles=(data['position'][t[ids]]-origin)@basis[:2].T
        matrices=(triangles[:,:2]-triangles[:,2,None]).transpose(0,2,1)
        b=np.linalg.solve(matrices,(xy-triangles[:,2])[...,None])[...,0]
        b=np.c_[b,1-b.sum(axis=1)]
        good=(b.min(axis=1)>=-1e-7)&(b.max(axis=1)<=1+1e-7)
        if not good.any():raise ValueError('Point is outside repaired chart: '+str(name))
        j=int(np.flatnonzero(good)[0]);b=np.clip(b[j],0,1);b/=b.sum()
        faces.append(int(ids[j]));bary.append(b);rebuilt.append(str(name))
    faces,bary=np.array(faces),np.array(bary)
    positions=np.einsum('ni,nij->nj',bary,v[t[faces]])
    result=dict(landmarks)
    # A chart-rebound point can span several native faces. Remove the old
    # single-native-parent addresses rather than retaining stale provenance.
    for key in ('sourceParent','sourceMaterialBary'):
        result.pop(key,None)
    result.update(sourceFace=faces,sourceBary=bary,sourcePosition=positions)
    return result,rebuilt


def jump_stats(v,t,selected):
    cross=np.cross(v[t[:,1]]-v[t[:,0]],v[t[:,2]]-v[t[:,0]])
    n=cross/np.linalg.norm(cross,axis=1)[:,None]
    edge={}
    for i,f in enumerate(t):
        for a,b in ((0,1),(1,2),(2,0)):edge.setdefault(tuple(sorted(f[[a,b]])),[]).append(i)
    values=[]
    for rows in edge.values():
        if len(rows)==2 and (selected[rows[0]] or selected[rows[1]]):
            values.append(np.degrees(np.arccos(np.clip(n[rows[0]]@n[rows[1]],-1,1))))
    return dict(maxDegrees=float(np.max(values)),p95Degrees=float(np.percentile(values,95)),edgeCount=len(values))


def run(args):
    folder=Path(args.output);folder.mkdir(parents=True,exist_ok=True)
    data=dict(np.load(args.remeshed));old=dict(np.load(args.previous));original=dict(np.load(args.original))
    patches=json.loads(Path(args.patch_report).read_text())['patches']
    landmarks=dict(np.load(args.landmarks))
    v0,t,groups=data['position'],data['triangles'],data['group']
    average,metric=graph(v0,t);pinned,taper,pelvis=repair_constraints(v0,t,groups,metric)
    sample=(~pinned)&(taper>.5)
    before=roughness(v0,t,average,sample);trials=[];variants={}
    for radius in (35.,55.,75.):
        v,scale,ratio=local_quadratic(v0,t,average,metric,pinned,taper,radius,4.,groups)
        detail=roughness(v,t,average,sample)
        trials.append(dict(radiusMm=radius,normalDetailResidual=detail,minimumLocalScale=scale,
                           maximumMoveMm=float(np.linalg.norm(v-v0,axis=1).max()),
                           areaRatioMin=float(ratio.min()),areaRatioMax=float(ratio.max())))
        variants[radius]=v
    chosen=min(trials,key=lambda r:r['normalDetailResidual']['rmsDegrees']);v=variants[chosen['radiusMm']]
    # Remove narrow reconstructed creases after fitting the broad surface.
    # Restrict this extra relaxation to the reported seat/crotch region.
    a2,m2=graph(v,t);local_pins=pinned|~pelvis
    distance=dijkstra(m2,directed=False,indices=np.flatnonzero(local_pins),min_only=True)
    local_taper=np.clip(distance/10.,0,1);local_taper=local_taper**2*(3-2*local_taper)
    v,relax_scale,relax_ratio=fair(v,t,a2,local_pins,local_taper,90,2.)
    regions={
        'buttCheeks':(v0[t].mean(1)[:,2]<-60)&(v0[t].mean(1)[:,1]>875)&(v0[t].mean(1)[:,1]<960),
        'frontInnerThigh':(abs(v0[t].mean(1)[:,0])<80)&(v0[t].mean(1)[:,1]>815)&(v0[t].mean(1)[:,1]<900)&(v0[t].mean(1)[:,2]>0)}
    audit=boundaries(t)
    if audit['openBoundaryLoops']!=5:raise ValueError('Open boundaries changed')
    n=normals(v,t)[t]
    result=dict(data);result.update(position=v,normal=n,repairProtectedVertex=pinned,remeshedPosition=v0,
                                   repairDisplacement=v-v0)
    np.savez_compressed(folder/'vanilla-repaired-reference.npz',**result)
    write_obj(folder/'vanilla-repaired.obj',v,t,n,data['uv'],groups)
    paired,rebound=bind_points(old,data,patches,landmarks,v)
    np.savez_compressed(folder/'paired-landmarks-candidate.npz',**paired)
    write_points(folder/'Vanilla-Repaired.points.json',paired['names'],paired['sourceFace'],paired['sourceBary'])
    profile=[]
    for y in (1200,1220,1240,1250,1260,1280):
        for x in (0,10,-10,40,-40,60,-60,80,-80):
            a=front_depth(original['position'][original['triangles']],(x,y));b=front_depth(v[t],(x,y))
            if a and b:profile.append(dict(xMm=x,yMm=y,changeMm=b['zMm']-a['zMm']))
    patches_audit=[]
    for p in patches:
        mask=np.zeros(len(old['triangles']),bool);mask[p['oldFaces']]=True
        new_mask=np.zeros(len(t),bool);new_mask[p['firstNewFace']:p['firstNewFace']+p['newFaceCount']]=True
        patches_audit.append(dict(name=p['name'],before=jump_stats(old['position'],old['triangles'],mask),
                                  after=jump_stats(v,t,new_mask),patchMaxMovementAfterFairingMm=float(np.linalg.norm(v-v0,axis=1)[np.unique(t[new_mask])].max())))
    dist,_,_=closest_surface(v[t].mean(axis=1),old['position'][old['triangles']])
    shifts=np.linalg.norm(paired['sourcePosition']-landmarks['sourcePosition'],axis=1)
    report=dict(status='Second repair candidate; accepted reference not replaced',selectedRadiusMm=chosen['radiusMm'],
                beforeNormalDetailResidual=before,trials=trials,capRepairs=patches_audit,
                finalNormalDetailResidual=roughness(v,t,average,sample),
                localRelaxation=dict(cycles=90,maxAllowedMovementMm=2.,minimumLocalScale=relax_scale,
                                     areaRatioMin=float(relax_ratio.min()),areaRatioMax=float(relax_ratio.max())),
                reportedRegions={name:dict(before=jump_stats(v0,t,mask),after=jump_stats(v,t,mask)) for name,mask in regions.items()},
                vertices=len(v),triangles=len(t),connectivity=audit,
                normalsRecomputedFromGeometry=True,weightsMaxSumError=float(abs(data['weights'].sum(axis=-1)-1).max()),
                changedTopology='Only the three lower flap-root disks. Per-corner native-face/barycentric provenance replaces the single-parent triangle assumption there.',
                protectedMaxMovementMm=float(np.linalg.norm(v-v0,axis=1)[pinned].max()),
                boundingBoxChangeFromOriginalMm=float(abs(np.r_[v.min(0)-original['position'].min(0),v.max(0)-original['position'].max(0)]).max()),
                cleavageMidlineMaxDepthChangeMm=max(abs(r['changeMm']) for r in profile if r['xMm']==0),
                profiles=profile,landmarks=len(shifts),pointsReboundThroughLocalCharts=rebound,
                landmarkChangeFromPreviousCandidateMm=dict(max=float(shifts.max()),p95=float(np.percentile(shifts,95)),mean=float(shifts.mean())),
                sampledDistanceFromPreviousCandidateMm=dict(max=float(dist.max()),p95=float(np.percentile(dist,95))),
                limitations='Local shape repair, not naked anatomy reconstruction. No continuous global intersection proof or animation/physics validation. Native references, AF assets and accepted point files remain unchanged.')
    (folder/'polish-report.json').write_text(json.dumps(report,indent=2)+'\n')
    print(json.dumps({k:r for k,r in report.items() if k not in ('profiles','limitations')},indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    for key in ('remeshed','previous','original','patch-report','landmarks','output'):p.add_argument('--'+key,required=True,type=Path)
    run(p.parse_args())
