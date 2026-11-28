"""Bounded cloth-region fairing trial on a copy of the curved vanilla reference.

The front cleavage, coarse shape extrema and skin outside an optional join
blend are fixed. Quadratic fits retain local broad curvature while attenuating
detail. No hidden anatomy is reconstructed and no accepted reference replaced.
"""
import argparse
import hashlib
import json
from pathlib import Path

import numpy as np
from scipy.sparse import coo_matrix, diags
from scipy.sparse.csgraph import dijkstra

from reference_surface import areas, unit, write_obj


def normals(v,t):
    p=v[t];cross=np.cross(p[:,1]-p[:,0],p[:,2]-p[:,0]);face=unit(cross)
    result=np.zeros_like(v)
    for k in range(3):
        a=unit(p[:,(k+1)%3]-p[:,k]);b=unit(p[:,(k+2)%3]-p[:,k])
        angle=np.arccos(np.clip(np.sum(a*b,axis=1),-1,1))
        np.add.at(result,t[:,k],face*angle[:,None])
    return unit(result)


def graph(v,t):
    edges=np.unique(np.sort(t[:,[[0,1],[1,2],[2,0]]].reshape(-1,2),axis=1),axis=0)
    length=np.linalg.norm(v[edges[:,0]]-v[edges[:,1]],axis=1)
    i=np.r_[edges[:,0],edges[:,1]];j=np.r_[edges[:,1],edges[:,0]]
    weight=np.r_[1/length,1/length]
    a=coo_matrix((weight,(i,j)),shape=(len(v),len(v))).tocsr()
    average=diags(1/np.asarray(a.sum(axis=1)).ravel())@a
    metric=coo_matrix((np.r_[length,length],(i,j)),shape=a.shape).tocsr()
    return average,metric


def constraints(v,t,groups,metric,dense_shape_pins=False,transition_mm=0):
    cloth_face=np.char.find(groups,'smallclothes')>=0
    cloth_vertex=np.zeros(len(v),bool);cloth_vertex[t[cloth_face].ravel()]=True
    skin_vertex=np.zeros(len(v),bool);skin_vertex[t[~cloth_face].ravel()]=True
    if transition_mm>0:
        distance_to_cloth=dijkstra(metric,directed=False,indices=np.flatnonzero(cloth_vertex),min_only=True)
        pinned=~(cloth_vertex|(distance_to_cloth<transition_mm))
    else:pinned=~cloth_vertex|skin_vertex
    reasons=dict(initialFixedVertices=int(pinned.sum()),skinTransitionMm=transition_mm)
    top_vertex=np.zeros(len(v),bool);top_vertex[t[np.char.startswith(groups,'top_smallclothes')].ravel()]=True
    if transition_mm>0:
        top_distance=dijkstra(metric,directed=False,indices=np.flatnonzero(top_vertex),min_only=True)
        top_vertex|=top_distance<transition_mm
    cleavage=top_vertex&(abs(v[:,0])<=14)&(v[:,1]>=1190)&(v[:,2]>60)
    pinned|=cleavage
    reasons['cleavageVertices']=int(cleavage.sum())
    shape_anchors=set()
    # Preserve coarse projected shape samples; fitting still removes detail
    # between them. These constraints do not freeze every native cloth vertex.
    for prefix in ('top','legs'):
        ids=np.unique(t[np.char.startswith(groups,prefix+'_smallclothes')])
        if not len(ids):continue
        bins=np.floor(v[ids,1]/12).astype(int) if dense_shape_pins else np.zeros(len(ids),int)
        for level in np.unique(bins):
            band=ids[bins==level]
            for side in (-1,1):
                half=band[v[band,0]*side>=0]
                if not len(half):continue
                shape_anchors.add(int(half[np.argmax(v[half,0]*side)]))
                for depth in (-1,1):
                    sector=half[v[half,2]*depth>0]
                    if len(sector):shape_anchors.add(int(sector[np.argmax(v[sector,2]*depth)]))
        for axis in range(3):
            shape_anchors.add(int(ids[np.argmin(v[ids,axis])]))
            shape_anchors.add(int(ids[np.argmax(v[ids,axis])]))
    pinned[list(shape_anchors)]=True
    reasons['coarseShapeAnchors']=len(shape_anchors)
    distance=dijkstra(metric,directed=False,indices=np.flatnonzero(pinned),min_only=True)
    taper=np.clip(distance/5.,0,1);taper=taper*taper*(3-2*taper)
    taper[pinned]=0
    return pinned,taper,cloth_face,reasons


def local_quadratic(v0,t,average,metric,pinned,taper,radius,max_move,groups):
    """Area-weighted moving quadratic fit in geodesic neighbourhoods.

    A quadratic retains broad curvature instead of pulling every neighbourhood
    toward a plane. Only normal displacement is applied; positional pins and a
    geodesic transition protect the joins and the medial chest.
    """
    n=normals(v0,t)
    for _ in range(16):n=unit(.5*n+.5*(average@n))
    mass=np.zeros(len(v0))
    for k in range(3):np.add.at(mass,t[:,k],areas(v0[t])/3)
    top=np.zeros(len(v0),bool);top[t[np.char.startswith(groups,'top_smallclothes')].ravel()]=True
    delta=np.zeros_like(v0)
    for i in np.flatnonzero(~pinned):
        distances=dijkstra(metric,directed=False,indices=int(i),limit=radius)
        mask=distances<radius
        mask&=(n@n[i])>.4
        if top[i] and v0[i,2]>40:
            mask&=v0[:,0]*v0[i,0]>=-1e-6
        ids=np.flatnonzero(mask)
        if len(ids)<12:continue
        axis=np.array([0.,1.,0.]) if abs(n[i,1])<.9 else np.array([1.,0.,0.])
        u=unit(np.cross(n[i],axis));w=np.cross(n[i],u)
        q=(v0[ids]-v0[i])/radius
        x,y,z=q@u,q@w,q@n[i]
        design=np.c_[np.ones(len(x)),x,y,x*x,x*y,y*y]
        weights=np.exp(-(distances[ids]/(.45*radius))**2)*mass[ids]
        robust=np.ones(len(ids))
        for _ in range(3):
            root=np.sqrt(weights*robust)
            coef,_,rank,_=np.linalg.lstsq(design*root[:,None],z*root,rcond=1e-7)
            if rank<6:break
            residual=z-design@coef
            scale=max(.2/radius,1.4826*np.median(abs(residual-np.median(residual))))
            robust=np.minimum(1,1.5*scale/np.maximum(abs(residual),1e-20))
        if rank<6:continue
        offset=np.clip(coef[0]*radius,-max_move,max_move)*taper[i]
        delta[i]=offset*n[i]
    # Reuse the geometric acceptance rules without a smoothing iteration.
    return accept_displacement(v0,t,delta)


def roughness(v,t,average,mask):
    n=normals(v,t);low=n.copy()
    for _ in range(12):low=.5*low+.5*(average@low)
    low=unit(low)
    angle=np.degrees(np.arccos(np.clip(np.sum(n*low,axis=1),-1,1)))
    return dict(rmsDegrees=float(np.sqrt(np.mean(angle[mask]**2))),p95Degrees=float(np.percentile(angle[mask],95)))


def fair(v0,t,average,pinned,taper,cycles,max_move):
    v=v0.copy()
    for _ in range(cycles):
        first=v+.5*taper[:,None]*(average@v-v)
        first[pinned]=v0[pinned]
        v=first-.53*taper[:,None]*(average@first-first)
        delta=v-v0;length=np.linalg.norm(delta,axis=1)
        scale=np.minimum(1,max_move/np.maximum(length,1e-20))
        v=v0+delta*scale[:,None];v[pinned]=v0[pinned]
    # Reject inverted/strongly collapsed elements on the shared vertex array.
    return accept_displacement(v0,t,v-v0)


def accept_displacement(v0,t,delta):
    p=v0[t];old_cross=np.cross(p[:,1]-p[:,0],p[:,2]-p[:,0]);old_area=areas(p)
    scale=np.ones(len(v0))
    for _ in range(40):
        candidate=v0+scale[:,None]*delta;q=candidate[t]
        new_cross=np.cross(q[:,1]-q[:,0],q[:,2]-q[:,0])
        ratio=areas(q)/old_area
        bad=(np.sum(old_cross*new_cross,axis=1)<=0)|(ratio<=.35)|(ratio>=2.5)
        if not bad.any():
            moving=np.linalg.norm(delta,axis=1)>1e-12
            return candidate,float(scale[moving].min()) if moving.any() else 1.,ratio
        # Thin authored triangles near a fixed join must not force every
        # unrelated cloth region to retain the same unwanted detail.
        scale[np.unique(t[bad])]*=.5
    raise ValueError('Could not obtain a non-reversing cleanup candidate')


def run(args):
    if args.max_move<=0 or args.transition_mm<0 or any(r<=0 for r in args.radii):
        raise ValueError('Movement and radii must be positive; transition must be nonnegative')
    path=Path(args.reference);before=hashlib.sha256(path.read_bytes()).hexdigest()
    data=dict(np.load(path));v0=data['position'];t=data['triangles'];groups=data['group']
    average,metric=graph(v0,t);pinned,taper,cloth,reasons=constraints(v0,t,groups,metric,args.method=='taubin',args.transition_mm)
    movable=~pinned;rough_mask=movable&(taper>.5)
    original_rough=roughness(v0,t,average,rough_mask)
    out=Path(args.output);out.mkdir(parents=True,exist_ok=True)
    trials=[];candidates={}
    settings=(('mild',30),('medium',90),('strong',180)) if args.method=='taubin' else tuple(zip(('mild','medium','strong'),args.radii))
    for name,parameter in settings:
        if args.method=='taubin':v,alpha,ratio=fair(v0,t,average,pinned,taper,parameter,args.max_move)
        else:v,alpha,ratio=local_quadratic(v0,t,average,metric,pinned,taper,parameter,args.max_move,groups)
        r=roughness(v,t,average,rough_mask);movement=np.linalg.norm(v-v0,axis=1)
        row=dict(name=name,parameter=parameter,parameterUnits='cycles' if args.method=='taubin' else 'geodesic radius mm',minimumLocalDisplacementScale=alpha,
                 movementMm=dict(max=float(movement.max()),p95Movable=float(np.percentile(movement[movable],95)),meanMovable=float(movement[movable].mean())),
                 normalDetailResidual=r,normalDetailRmsReduction=1-r['rmsDegrees']/original_rough['rmsDegrees'],
                 triangleAreaRatio=dict(min=float(ratio.min()),max=float(ratio.max())),
                 pinnedMaxMovementMm=float(movement[pinned].max()))
        trials.append(row);candidates[name]=v
    # Choose the strongest reduction among these bounded trials. This is a
    # candidate for visual review, not a universal fabric classifier.
    selected=min(trials,key=lambda r:r['normalDetailResidual']['rmsDegrees'])
    v=candidates[selected['name']]
    n=normals(v,t)[t]
    moved=np.linalg.norm(v-v0,axis=1)>1e-10
    unchanged_faces=~np.any(moved[t],axis=1)
    n[unchanged_faces]=data['normal'][unchanged_faces]
    write_obj(out/'vanilla-cleaned.obj',v,t,n,data['uv'],groups)
    for name,vertices in candidates.items():
        nn=normals(vertices,t)[t]
        nn[~np.any(np.linalg.norm(vertices-v0,axis=1)[t]>1e-10,axis=1)]=data['normal'][~np.any(np.linalg.norm(vertices-v0,axis=1)[t]>1e-10,axis=1)]
        write_obj(out/f'vanilla-{name}.obj',vertices,t,nn,data['uv'],groups)
    candidate=dict(data);candidate.update(position=v,normal=n,controlPosition=v0,
                                          cleanupDisplacement=v-v0,cleanupProtectedVertex=pinned)
    np.savez_compressed(out/'vanilla-cleaned-reference.npz',**candidate)
    np.savez_compressed(out/'cleanup-comparison.npz',controlPosition=v0,triangles=t,group=groups,
                        mildPosition=candidates['mild'],mediumPosition=candidates['medium'],strongPosition=candidates['strong'],
                        cleanedPosition=v,protected=pinned,taper=taper)
    if hashlib.sha256(path.read_bytes()).hexdigest()!=before:raise ValueError('Original reference changed')
    report=dict(status='Bounded cleanup candidate for visual comparison; original reference not replaced',
                sourcePath=str(path.resolve()),sourceSha256=before,selected=selected['name'],
                method=args.method,methodDetails='Cloth and optional join transition, pinned outer skin/cleavage/shape extrema, 5 mm geodesic transition, bounded movement and local element backtracking. Local quadratic mode uses area-weighted geodesic fits and normal-only displacement.',
                maxAllowedMovementMm=args.max_move,protection=reasons,movableVertices=int(movable.sum()),
                originalNormalDetailResidual=original_rough,trials=trials,
                topologyUnchanged=True,uvsUnchanged=True,weightsUnchanged=True,
                skinVerticesAndJoinsUnchanged=args.transition_mm==0,cleavageProtected=True,
                sharedJoinConnectivityPreserved=True,skinOutsideTransitionFixed=True,
                scope='Small surface detail attenuation only. Cloth vertices and the optional skin transition may move. Existing native lineage arrays remain original provenance, not a recipe that regenerates the cleaned surface. No new anatomical reconstruction, Coatee patch substitution, accepted landmark replacement, AF edits or wrap.')
    (out/'cleanup-report.json').write_text(json.dumps(report,indent=2)+'\n')
    print(json.dumps(report,indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--reference',type=Path,required=True);p.add_argument('--output',type=Path,required=True)
    p.add_argument('--max-move',type=float,default=2.5)
    p.add_argument('--method',choices=('taubin','quadratic'),default='quadratic')
    p.add_argument('--radii',nargs=3,type=float,default=(20,35,50))
    p.add_argument('--transition-mm',type=float,default=0)
    run(p.parse_args())
