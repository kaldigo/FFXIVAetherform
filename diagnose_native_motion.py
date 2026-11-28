"""Compare collision material locations and a native-base/skinned-relief trial."""
import json
from pathlib import Path
import numpy as np
from body_motion import load_rig,vertex_weights,CASES,deformation_metrics
from reference_surface import weld
from normal_field import NormalField
from compare_native_motion import native_sample,masks
from evaluate_body_motion import region_metrics
from mesh_crossings import crossing_pairs


def interior_pairs(p):
    centre=p.mean(1,keepdims=True);shrunk=centre+(p-centre)*(1-1e-5)
    # Unique corner IDs allow adjacent original faces to be checked, while
    # inward shrinking excludes their ordinary shared edge/vertex contact.
    return crossing_pairs(shrunk,shrunk,self_faces=np.arange(len(p)*3).reshape(-1,3))


def material_pairs(pairs,parents,homogeneous,mask):
    selected=pairs[np.any(mask[pairs],axis=1)]
    eligible=homogeneous[selected].all(1);selected=selected[eligible]
    return sorted(set(tuple(sorted(map(int,p))) for p in parents[selected])),int((~eligible).sum())


def run():
    out=Path('outputs/native-motion-comparison-v1');rig=load_rig()
    s=dict(np.load('outputs/vanilla-reference-v1/vanilla-reference.npz'));field=NormalField(s);nv,nt=weld(s['sourcePosition'])
    native=dict(position=nv,triangles=nt,weights=field.weights,boneNames=s['boneNames'],group=s['sourceGroups']);nw=vertex_weights(native)
    flat=dict(np.load('outputs/normal-map-wrap-4x/normal-map-reference.npz'));raw=dict(np.load('outputs/body-cleanup-4x-raw/body-fit-reference.npz'));rw=vertex_weights(raw)
    rt=raw['triangles'];parents=raw['cornerNativeFace'];bary=raw['cornerNativeBary']
    base=native_sample(s['sourcePosition'],parents,bary,rt,len(raw['position']));offset=raw['position']-base
    original_valid=s['sourceIdentity'][:,1].astype(int)>=0
    native['nativeFaceValid']=original_valid;raw['nativeFaceValid']=original_valid[parents].all(1)
    flat['nativeFaceValid']=original_valid[flat['sourceFace']]
    regions=masks(raw);report=[]
    for name in ('sit','crouch','lunge'):
        case=next(c for c in CASES if c['id']==name);frames=[]
        for phase in np.linspace(0,1,17):
            world=rig.world(case,float(phase));q,_=rig.skin(nv,nw,native['boneNames'],world)
            direct,blend=rig.skin(raw['position'],rw,raw['boneNames'],world)
            inherited=native_sample(q[nt],parents,bary,rt,len(raw['position']))
            trial=inherited+np.einsum('nij,nj->ni',blend,offset)
            if phase==0:np.testing.assert_allclose(trial,raw['position'],atol=1e-9)
            metrics=deformation_metrics(raw['position'],trial,rt,blend)
            frame=dict(phase=float(phase),regions={k:region_metrics(metrics,m) for k,m in regions.items()})
            if phase==.5:
                native_hits=interior_pairs(q[nt]);native_hits=native_hits[original_valid[native_hits].all(1)]
                original={k:set(map(tuple,native_hits[np.any(masks(native)[k][native_hits],axis=1)])) for k in regions}
                strict_region={k:len(v) for k,v in original.items()}
                adjacent=np.any(nt[native_hits[:,0],:,None]==nt[native_hits[:,1],None,:],axis=(1,2))
                frame['nativeInteriorCheck']=dict(pairs=len(native_hits),adjacentOriginalFaces=int(adjacent.sum()),regionPairs=strict_region)
                candidates={}
                trial_hits=crossing_pairs(trial[rt],trial[rt],self_faces=rt)
                for key in ('flat4x','root_cleaned4x','native_carried4x','native_rotated_relief4x'):
                    if key=='native_rotated_relief4x':hits=trial_hits
                    else:hits=np.load(out/(name+'-'+key+'-peak.npz'))['selfPairs']
                    if key=='flat4x':
                        owner=flat['sourceFace'];homogeneous=original_valid[owner];maskset=masks(flat)
                    else:owner=parents[:,0];homogeneous=np.all(parents==parents[:,:1],axis=1)&raw['nativeFaceValid'];maskset=regions
                    comparisons={}
                    for reg,mask in maskset.items():
                        pairs,excluded=material_pairs(hits,owner,homogeneous,mask);ps=set(pairs)
                        comparisons[reg]=dict(uniqueNativeFacePairs=len(ps),withinSingleNativeFace=sum(a==b for a,b in ps),
                            alsoInNativeInteriorCheck=len(ps&original[reg]),additionalNativeFacePairs=[list(p) for p in sorted(ps-original[reg])],excludedMixedLineagePairs=excluded)
                    candidates[key]=dict(crossingPairs=len(hits),regions=comparisons)
                frame['materialComparison']=candidates
                np.savez_compressed(out/(name+'-native_rotated_relief4x-peak.npz'),position=trial,selfPairs=trial_hits)
            frames.append(frame)
        row=dict(clip=name,frames=frames);report.append(row)
        (out/'native-motion-diagnosis.json').write_text(json.dumps(dict(scope='Additional material-pair comparisons exclude triangles with mixed native parents. Native interior check shrinks each triangle by 1e-5 about its centroid; it also checks original adjacent faces. Trial preserves native posed base points and applies the dense blended rotation to relief, with no geometry/weight edits.',clips=report),indent=2)+'\n')
        peak=next(f for f in frames if 'materialComparison'in f)
        print(name,peak['nativeInteriorCheck'],flush=True)
        for k,d in peak['materialComparison'].items():
            print(k,'dense pairs',d['crossingPairs'],'native material pairs',
                  {reg:(v['uniqueNativeFacePairs'],v['alsoInNativeInteriorCheck']) for reg,v in d['regions'].items()},flush=True)


if __name__=='__main__':run()
