"""Keep broad raw-minus-neutral shape while suppressing finer cloth relief."""
import argparse,json,hashlib
from pathlib import Path
import numpy as np
from scipy.sparse import coo_matrix,diags
from scipy.sparse.linalg import spsolve
from scipy.sparse.csgraph import dijkstra
from normal_field import NormalField
from clean_reference import graph,normals
from reference_surface import areas,unit
from body_cleanup import finalize


def surface_operator(v,t,valid=None):
    if valid is not None:t=t[valid]
    p=v[t];area=areas(p);mass=np.zeros(len(v));rows=[];cols=[];values=[]
    for k in range(3):
        np.add.at(mass,t[:,k],area/3)
        i,j=(k+1)%3,(k+2)%3;u=p[:,i]-p[:,k];w=p[:,j]-p[:,k]
        cot=.5*np.sum(u*w,axis=-1)/np.linalg.norm(np.cross(u,w),axis=-1)
        a,b=t[:,i],t[:,j]
        rows.extend((a,b,a,b));cols.extend((a,b,b,a));values.extend((cot,cot,-cot,-cot))
    lap=coo_matrix((np.concatenate(values),(np.concatenate(rows),np.concatenate(cols))),shape=(len(v),len(v))).tocsr()
    # Unmeasured cap-only nodes are isolated, and will be replaced afterwards.
    mass[mass==0]=1.
    return mass,lap


def low_frequency(signal,mass,lap,scale_mm):
    if scale_mm<=0:raise ValueError('Separation scale must be positive')
    return spsolve(diags(mass)+scale_mm**2*lap,mass*signal)


def compose(raw,neutral,directions,broad,taper,inward_mm):
    delta=np.sum((raw-neutral)*directions,axis=-1)
    np.testing.assert_allclose(raw-neutral,delta[:,None]*directions,atol=1e-8)
    # In cloth: neutral + broad. Outside the transition: raw, unchanged.
    return raw+taper[:,None]*(broad-delta-inward_mm)[:,None]*directions


def run(args):
    raw_folder=Path(args.raw);raw=dict(np.load(raw_folder/'normal-map-reference.npz'))
    neutral=np.load(Path(args.neutral)/'normal-map-reference.npz')
    for key in ('triangles','sourceFace','sourceBarycentric','flatPosition'):
        np.testing.assert_array_equal(raw[key],neutral[key])
    original=np.load('outputs/vanilla-reference-v1/vanilla-reference.npz');field=NormalField(original)
    v,t=raw['flatPosition'],raw['triangles'];parent=raw['sourceFace'];bc=raw['sourceBarycentric']
    authored=field.sample(np.repeat(parent,3),bc.reshape(-1,3),False).reshape(-1,3,3)
    directions=np.zeros_like(v);face_area=areas(v[t])
    for k in range(3):np.add.at(directions,t[:,k],authored[:,k]*face_area[:,None])
    directions=unit(directions)
    delta=np.sum((raw['position']-neutral['position'])*directions,axis=-1)
    valid=~np.char.endswith(raw['group'],'_cap');mass,lap=surface_operator(v,t,valid)
    cloth=np.zeros(len(v),bool)
    cloth[t[valid&(np.char.find(raw['group'],'smallclothes')>=0)]]=True
    _,metric=graph(v,t);distance=dijkstra(metric,directed=False,indices=np.flatnonzero(cloth),min_only=True)
    taper=np.clip(1-distance/args.transition_mm,0,1);taper=taper*taper*(3-2*taper)
    outside=taper==0
    out=Path(args.output);out.mkdir(parents=True,exist_ok=True)
    if (raw_folder/'density-report.json').exists():(out/'density-report.json').write_text((raw_folder/'density-report.json').read_text())
    broad=low_frequency(delta,mass,lap,args.scale)
    p=compose(raw['position'],neutral['position'],directions,broad,taper,args.inward)
    np.testing.assert_array_equal(p[outside],raw['position'][outside])
    # Positive projected area on measured native faces is checked before any
    # root reconstruction. No post-hoc smoothing or individual vertex scaling.
    old=raw['position'][t];q=p[t]
    a=np.cross(old[:,1]-old[:,0],old[:,2]-old[:,0]);b=np.cross(q[:,1]-q[:,0],q[:,2]-q[:,0])
    ratio=np.sum(a*b,axis=1)/np.sum(a*a,axis=1)
    if ratio[valid].min()<=.05:raise ValueError('Separation produced a folded/collapsed native element')
    data=dict(raw);data.update(position=p,normal=normals(p,t)[t],rawPosition=raw['position'],neutralPosition=neutral['position'],
         broadShapeDifference=broad,fullShapeDifference=delta,detailRemoved=delta-broad,shapeTransition=taper,
         separationDirection=directions,inwardAllowanceMm=np.array(args.inward),separationScaleMm=np.array(args.scale),
         method=np.array('raw-neutral spatial-scale separation; raw normalMapTarget retained as comparison provenance'))
    np.savez_compressed(out/'normal-map-reference.npz',**data)
    shift=np.sum((p-raw['position'])*directions,axis=-1)
    spec=dict(scaleMm=args.scale,inwardAllowanceMm=args.inward,skinTransitionMm=args.transition_mm,
       rawInputSha256=hashlib.sha256((raw_folder/'normal-map-reference.npz').read_bytes()).hexdigest(),
       neutralInputSha256=hashlib.sha256((Path(args.neutral)/'normal-map-reference.npz').read_bytes()).hexdigest(),
       offsetIsTrialAllowanceNotMeasuredThickness=True,unmodifiedOuterVertices=int(outside.sum()),
       minimumNativeAreaRatioRelativeRaw=float(ratio[valid].min()),
       broadDifferenceRangeMm=[float(broad[cloth].min()),float(broad[cloth].max())],
       detailRemovedRmsMm=float(np.sqrt(np.average((delta[cloth]-broad[cloth])**2,weights=mass[cloth]))),
       clothNormalShiftMm=dict(areaWeightedMean=float(np.average(shift[cloth],weights=mass[cloth])),
                              minimum=float(shift[cloth].min()),maximum=float(shift[cloth].max())),
       note='Geodesic surface Helmholtz separation of scalar normal displacements; no Euclidean mixing across nearby disconnected surfaces.')
    (out/'separation-report.json').write_text(json.dumps(spec,indent=2)+'\n')
    finalize(data,raw_folder,out,'scale-separation',args.inward,dict(separation=spec))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--raw',default='outputs/normal-map-wrap-4x');p.add_argument('--neutral',default='outputs/body-cleanup-4x-neutral')
    p.add_argument('--scale',type=float,required=True);p.add_argument('--inward',type=float,default=.5)
    p.add_argument('--transition-mm',type=float,default=15.);p.add_argument('--output',required=True)
    a=p.parse_args()
    if a.inward<0 or a.transition_mm<=0:p.error('Inward allowance must be nonnegative and transition must be positive')
    run(a)
