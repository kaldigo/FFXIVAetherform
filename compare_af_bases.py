"""Rank native AF bases and inspect their differences from the curved vanilla reference.

No registration, rescaling, wrapping, smoothing or installed-asset writes.
Landmark correspondence and unsigned surface proximity are separate measures.
"""
import argparse
import csv
import hashlib
import json
from pathlib import Path

import numpy as np
from scipy.spatial import cKDTree

from reference_surface import areas, field, load_snapshot, unit


def stats(values, weights=None):
    values=np.asarray(values)
    if not len(values):return None
    if weights is None:weights=np.ones(len(values))
    weights=np.asarray(weights);order=np.argsort(values)
    cumulative=(np.cumsum(weights[order])-.5*weights[order])/weights.sum()
    return dict(count=len(values),mean=float(np.average(values,weights=weights)),
                rms=float(np.sqrt(np.average(values*values,weights=weights))),
                median=float(np.interp(.5,cumulative,values[order])),
                p95=float(np.interp(.95,cumulative,values[order])),max=float(values.max()))


def load_af(path,hip=False,soft=False):
    s,data,meshes=load_snapshot(path);mesh=next(m for m in meshes if m['id']==0)
    base=np.frombuffer(data,'<u2',mesh['indexCount'],mesh['indexOffset']).reshape(-1,3).copy()
    ix=base.copy();active=[]
    if hip:active.append('shpx_yab_hip')
    if soft:active.extend(('shpx_yam_softbutt','shpx_yam_c_softwaist','shpx_yab_c_softwaist'))
    if hip and soft:active.append('shpx_yab_c_hipsoft')
    shapes={r['ShapeName']:r for r in s['tables']['Shapes']}
    applied=[]
    for name in active:
        if name not in shapes:continue
        applied.append(name);shape=shapes[name]
        start=shape['ShapeMeshStartIndex'][0];count=shape['ShapeMeshCount'][0]
        for entry in s['tables']['ShapeMeshes'][start:start+count]:
            if entry['MeshIndexOffset']!=mesh['startIndex']:continue
            offset=entry['ShapeValueOffset'];size=entry['ShapeValueCount']
            for value in s['tables']['ShapeValues'][offset:offset+size]:
                ix.ravel()[value['BaseIndicesIndex']]=value['ReplacingVertexIndex']
    positions=field(data,mesh,0)[:,:3]*1000
    normals=field(data,mesh,3)[:,:3]
    uv=field(data,mesh,4)[:,:2]
    return dict(position=positions[ix],normal=normals[ix],uv=uv[ix],indices=ix,
                baseIndices=base,vertices=positions,vertexUV=uv,sha256=s['sha256'],activeShapes=applied)


def compatible_occurrences(base,candidate):
    """Verify face/corner identity while allowing coincident render aliases."""
    a,b=base['baseIndices'],candidate['baseIndices']
    if a.shape!=b.shape:return False
    if not np.allclose(base['vertexUV'][a],candidate['vertexUV'][b],rtol=0,atol=1e-7):return False
    pairs=np.unique(np.c_[a.ravel(),b.ravel()],axis=0)
    for source,target,column,other in ((base,candidate,0,1),(candidate,base,1,0)):
        labels,counts=np.unique(pairs[:,column],return_counts=True)
        for label in labels[counts>1]:
            ids=pairs[pairs[:,column]==label,other]
            # One occurrence on this model may represent several coincident
            # UV-identical aliases on the other model; no real weld is inferred.
            if np.linalg.norm(target['vertices'][ids]-target['vertices'][ids[0]],axis=1).max()>1e-5:return False
            if np.abs(target['vertexUV'][ids]-target['vertexUV'][ids[0]]).max()>1e-7:return False
    return True


def region(p,slot):
    x,y,z=p.T
    labels=np.full(len(p),'torso' if slot=='top' else 'outer_hip_waist',dtype='<U40')
    if slot=='top':
        labels[(z<0)&(abs(x)<165)]='back_torso'
        labels[(y<1180)&(z>=0)&(abs(x)<165)]='abdomen_waist'
        labels[(y>=1180)&(z>=0)&(abs(x)<140)]='front_chest'
        labels[(y>=1280)&(abs(x)<85)]='upper_chest_neck'
        labels[(abs(x)>=85)&(abs(x)<175)&(y>=1260)]='shoulder_armpit'
        labels[abs(x)>=175]='arms'
    else:
        labels[y<600]='calf_ankle'
        labels[(y>=600)&(y<780)]='thigh_knee'
        labels[(y>=780)&(y<900)]='upper_thigh_underseat'
        labels[(y>=850)&(z<0)]='seat'
        labels[(y>=780)&(y<950)&(abs(x)<60)]='inner_thigh_underbody'
        labels[(abs(x)<25)&(y>=750)&(y<900)&(z>=-35)]='central_detail_region'
    return labels


def point_triangle(query,triangles):
    """Closest points for paired queries and triangles, including edges."""
    a=triangles[...,0,:];v=triangles[...,1,:]-a;w=triangles[...,2,:]-a;d=query-a
    vv=np.sum(v*v,axis=-1);vw=np.sum(v*w,axis=-1);ww=np.sum(w*w,axis=-1)
    dv=np.sum(d*v,axis=-1);dw=np.sum(d*w,axis=-1);den=vv*ww-vw*vw
    u=(dv*ww-dw*vw)/np.maximum(den,1e-30);z=(dw*vv-dv*vw)/np.maximum(den,1e-30)
    bc=np.stack((1-u-z,u,z),axis=-1)
    inside=(bc.min(axis=-1)>=0)&(den>1e-20)
    best=np.sum((query-np.einsum('...i,...ij->...j',bc,triangles))**2,axis=-1)
    best=np.where(inside,best,np.inf)
    for i,j in ((0,1),(1,2),(2,0)):
        delta=triangles[...,j,:]-triangles[...,i,:]
        fraction=np.clip(np.sum((query-triangles[...,i,:])*delta,axis=-1)/np.maximum(np.sum(delta*delta,axis=-1),1e-30),0,1)
        p=triangles[...,i,:]+fraction[...,None]*delta
        ds=np.sum((query-p)**2,axis=-1);take=ds<best
        alternative=np.zeros_like(bc);alternative[...,i]=1-fraction;alternative[...,j]=fraction
        bc=np.where(take[...,None],alternative,bc);best=np.minimum(best,ds)
    return np.sqrt(np.maximum(best,0)),bc


def closest_surface(points,triangles,chunk=128):
    """Exact point-to-triangle minima with a complete bounding-radius search.

    Initial nearest-centroid candidates only bound the search. A triangle able
    to beat that bound must have centroid distance <= bound + its radius.
    No fixed-k nearest-centroid approximation is reported as a surface minimum.
    """
    centre=triangles.mean(axis=1)
    radius=np.linalg.norm(triangles-centre[:,None],axis=2).max(axis=1)
    tree=cKDTree(centre);low=triangles.min(axis=1);high=triangles.max(axis=1)
    distances=[];faces=[];bary=[]
    for start in range(0,len(points),chunk):
        p=points[start:start+chunk]
        _,ids=tree.query(p,k=min(16,len(triangles)))
        if ids.ndim==1:ids=ids[:,None]
        d,b=point_triangle(p[:,None],triangles[ids]);which=d.argmin(axis=1)
        best=d[np.arange(len(p)),which];face=ids[np.arange(len(p)),which].copy()
        bc=b[np.arange(len(p)),which].copy()
        candidates=tree.query_ball_point(p,best+radius.max()+1e-8)
        rows=np.repeat(np.arange(len(p)),[len(r) for r in candidates]);fs=np.concatenate(candidates).astype(int)
        keep=np.linalg.norm(p[rows]-centre[fs],axis=1)<=best[rows]+radius[fs]+1e-8
        rows,fs=rows[keep],fs[keep]
        aabb=np.maximum(np.maximum(low[fs]-p[rows],p[rows]-high[fs]),0)
        keep=np.linalg.norm(aabb,axis=1)<=best[rows]+1e-8
        rows,fs=rows[keep],fs[keep]
        ds,bs=point_triangle(p[rows],triangles[fs])
        for i in range(len(p)):
            hits=np.flatnonzero(rows==i)
            if not len(hits):continue
            j=hits[ds[hits].argmin()]
            if ds[j]<best[i]:best[i]=ds[j];face[i]=fs[j];bc[i]=bs[j]
        distances.append(best);faces.append(face);bary.append(bc)
    return np.concatenate(distances),np.concatenate(faces),np.concatenate(bary)


def analyze(args):
    out=Path(args.output);out.mkdir(parents=True,exist_ok=True)
    reference=np.load(args.reference);points=np.load(args.landmarks)
    catalogue=json.loads(Path(args.catalogue).read_text());bodies=Path(args.bodies)
    bases={slot:load_af(bodies/(key+'.json')) for slot,key in (('top','af_top_01'),('dwn','af_dwn_04'))}
    split=len(bases['top']['position'])
    replay=np.concatenate((bases['top']['position'],bases['dwn']['position']))
    baseline=np.einsum('ni,nij->nj',points['targetBary'],replay[points['targetFace']])
    if np.linalg.norm(baseline-points['targetPosition'],axis=1).max()>1e-7:
        raise ValueError('Small / Medium B native body does not replay accepted target addresses')
    source_points=points['sourcePosition'];source_groups=reference['sourceGroups'][points['sourceParent']]
    candidate_rows=[];cache={};regional_rows=[]
    for entry in catalogue:
        slot=entry['slot'][1:];base=load_af(bodies/(entry['id']+'.json'))
        if not compatible_occurrences(bases[slot],base):
            raise ValueError('Unverified face/corner correspondence: '+entry['id'])
        states=((False,False),(True,False),(False,True),(True,True)) if slot=='dwn' else ((False,False),)
        for hip,soft in states:
            candidate=base if not hip and not soft else load_af(bodies/(entry['id']+'.json'),hip,soft)
            mask=points['targetFace']<split if slot=='top' else points['targetFace']>=split
            fi=points['targetFace'][mask]-(0 if slot=='top' else split)
            target_points=np.einsum('ni,nij->nj',points['targetBary'][mask],candidate['position'][fi])
            if not np.allclose(candidate['uv'],base['uv'],rtol=0,atol=1e-7):
                raise ValueError('Shape state changed the face/corner UV identity')
            delta=target_points-source_points[mask];distance=np.linalg.norm(delta,axis=1)
            observed=np.char.endswith(source_groups[mask],'_skin')
            labels=region(source_points[mask],slot)
            key=entry['id']+f'-hip{int(hip)}-soft{int(soft)}'
            cache[key]=candidate
            row=dict(key=key,id=entry['id'],preset=entry['preset'],slot=slot,hipDips=hip,softButt=soft,
                     sha256=candidate['sha256'],geometryHash=hashlib.sha256(candidate['position'].tobytes()).hexdigest(),
                     activeShapes=candidate['activeShapes'],landmarks=stats(distance),
                     observedSkinLandmarks=stats(distance[observed]),coverageEnvelopeLandmarks=stats(distance[~observed]),
                     excludingCentralDetail=stats(distance[labels!='central_detail_region']))
            candidate_rows.append(row)
            for label in np.unique(labels):
                take=labels==label
                regional_rows.append(dict(key=key,region=label,**stats(distance[take])))
    with (out/'candidate-ranking.csv').open('w',newline='') as f:
        fields=['key','preset','slot','hipDips','softButt','rmsMm','p95Mm','maxMm','skinRmsMm','envelopeRmsMm']
        writer=csv.DictWriter(f,fieldnames=fields);writer.writeheader()
        for r in sorted(candidate_rows,key=lambda r:(r['slot'],r['landmarks']['rms'])):
            writer.writerow(dict(key=r['key'],preset=r['preset'],slot=r['slot'],hipDips=r['hipDips'],softButt=r['softButt'],
                                 rmsMm=r['landmarks']['rms'],p95Mm=r['landmarks']['p95'],maxMm=r['landmarks']['max'],
                                 skinRmsMm=r['observedSkinLandmarks']['rms'],envelopeRmsMm=r['coverageEnvelopeLandmarks']['rms']))
    best={slot:min((r for r in candidate_rows if r['slot']==slot),key=lambda r:r['landmarks']['rms'])['key'] for slot in ('top','dwn')}
    # Inspect the user's proposed baseline even if a modifier slightly wins.
    chosen=('af_top_01-hip0-soft0','af_dwn_04-hip0-soft0')
    af=np.concatenate([cache[k]['position'] for k in chosen])
    vanilla=reference['position'][reference['triangles']]
    af_centres=af.mean(axis=1);vanilla_centres=vanilla.mean(axis=1)
    print(json.dumps(dict(candidateStates=len(candidate_rows),bestByLandmarkRms=best,stage='measuring sampled surface distances')),flush=True)
    distances,faces,bary=closest_surface(af_centres,vanilla)
    reverse,reverse_face,reverse_bary=closest_surface(vanilla_centres,af)
    af_normal=unit(np.cross(af[:,1]-af[:,0],af[:,2]-af[:,0]))
    closest=np.einsum('ni,nij->nj',bary,vanilla[faces])
    signed=np.sum((af_centres-closest)*af_normal,axis=1)
    af_area,vanilla_area=areas(af),areas(vanilla)
    af_regions=np.r_[region(af_centres[:split],'top'),region(af_centres[split:],'dwn')]
    source_regions=np.array([region(vanilla_centres[i:i+1],'top' if g.startswith('top') else 'dwn')[0]
                             for i,g in enumerate(reference['group'])])
    proximity_by_region={label:stats(distances[af_regions==label],af_area[af_regions==label]) for label in np.unique(af_regions)}
    reverse_by_region={label:stats(reverse[source_regions==label],vanilla_area[source_regions==label]) for label in np.unique(source_regions)}
    landmark_labels=np.where(points['targetFace']<split,'top','dwn')
    landmark_regions=np.array([region(p[None],s)[0] for p,s in zip(source_points,landmark_labels)])
    selected_points=np.einsum('ni,nij->nj',points['targetBary'],af[points['targetFace']])
    shifts=selected_points-source_points;lengths=np.linalg.norm(shifts,axis=1)
    landmark_by_region={label:stats(lengths[landmark_regions==label]) for label in np.unique(landmark_regions)}
    hotspot_ids=np.argsort(lengths)[-30:][::-1]
    hotspots=[dict(name=str(points['names'][i]),region=str(landmark_regions[i]),provenance=str(source_groups[i]),
                   distanceMm=float(lengths[i]),vanillaMm=source_points[i].tolist(),afMm=selected_points[i].tolist(),
                   afMinusVanillaMm=shifts[i].tolist()) for i in hotspot_ids]
    np.savez_compressed(out/'comparison.npz',afPosition=af,vanillaPosition=vanilla,afDistance=distances,
                        afSignedNormalDistance=signed,afClosestVanillaFace=faces,afClosestVanillaBary=bary,
                        vanillaDistance=reverse,vanillaClosestAfFace=reverse_face,vanillaClosestAfBary=reverse_bary,
                        afRegion=af_regions,vanillaRegion=source_regions,sourceGroup=reference['group'],
                        pointNames=points['names'],pointVanilla=source_points,pointAf=selected_points,pointDistance=lengths,
                        pointRegion=landmark_regions,pointSourceGroup=source_groups,split=split)
    report=dict(inputs={key:dict(path=str(Path(getattr(args,key)).resolve()),sha256=hashlib.sha256(Path(getattr(args,key)).read_bytes()).hexdigest())
                        for key in ('reference','landmarks','catalogue')},
                candidateStates=candidate_rows,bestByLandmarkRms=best,selectedForDetailedReview=list(chosen),
                landmarkStatistics=stats(lengths),landmarkByRegion=landmark_by_region,
                landmarkHotspots=hotspots,proximityAfToVanilla=stats(distances,af_area),
                proximityVanillaToAf=stats(reverse,vanilla_area),proximityByRegion=proximity_by_region,
                reverseProximityByRegion=reverse_by_region,
                methods=dict(frame='Native millimetres, no alignment or rescaling; source and target share bind-space coordinates',
                             landmarks='948 existing semantic point pairs, with source locations evaluated on curved vanilla triangles',
                             bodyCorrespondence='Native ordered face/corner identity verified with UV signatures and coincident render-alias checks; authored shape replacement tables preserve triangle occurrences',
                             proximity='Exact closest triangle distance for triangle-centroid samples, with complete bounding-radius candidate search; area weighted statistics',
                             limits='Surface proximity is not semantic correspondence or a continuous surface bound. Landmark RMS depends on the existing selection and its coverage envelopes. These are not post-wrap errors or a fold certification.'),
                unchanged='Body references, landmark files, previous work, installed body and live plugin are read-only inputs')
    (out/'report.json').write_text(json.dumps(report,indent=2)+'\n')
    (out/'regional-rankings.json').write_text(json.dumps(regional_rows,indent=2)+'\n')
    print(json.dumps(dict(best=best,selectedLandmarkMm=report['landmarkStatistics'],afSurfaceMm=report['proximityAfToVanilla'],
                          vanillaSurfaceMm=report['proximityVanillaToAf'],hotspots=hotspots[:5]),indent=2),flush=True)


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    for key in ('reference','landmarks','catalogue','bodies','output'):parser.add_argument('--'+key,required=True,type=Path)
    analyze(parser.parse_args())
