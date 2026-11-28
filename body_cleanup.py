"""Body-only cloth-relief filtering and reconstruction of removed flap roots.

The smallclothes envelope remains. No thickness offset or naked-body inference.
"""
import argparse,json
from pathlib import Path
from types import SimpleNamespace
import numpy as np
from scipy.interpolate import RBFInterpolator
from scipy.ndimage import gaussian_filter
import triangle
from normal_field import NormalField
from reconstruct_normal_map import run as reconstruct
from reference_surface import weld,areas,write_obj
from repair_surface import adjacency,loop_of_patch,frame_for,boundaries
from compare_af_bases import closest_surface
from clean_reference import normals
from polish_repair import bind_points,jump_stats
from transfer_landmarks import write_points


def filter_cloth_field(field,mode,sigma=8.):
    if mode=='neutral':field.strength[1:]=[0.,0.]
    elif mode=='blur':
        if sigma<=0:raise ValueError('Blur sigma must be positive')
        for i in (1,2):field.maps[i]=gaussian_filter(field.maps[i],sigma=(sigma,sigma,0),mode='wrap')
    elif mode!='raw':raise ValueError('Unknown cloth filter')
    field.settings=dict(bodyOnly=True,clothMode=mode,blurSigmaPixels=sigma if mode=='blur' else 0,
                        skinNormalMapsUnchanged=True,underwearEnvelopeRetained=True,offsetMm=0)
    return field


def cap_regions(data):
    _,t=weld(data['sourcePosition']);adj=adjacency(t)
    remaining=set(np.flatnonzero(np.char.endswith(data['sourceGroups'],'_cap')));all_caps=set(remaining)
    result=[]
    while remaining:
        component=set();todo=[min(remaining)]
        while todo:
            i=todo.pop()
            if i in component:continue
            component.add(i);todo.extend((adj[i]&all_caps)-component)
        remaining-=component
        region=component|set().union(*(adj[i] for i in component))
        centre=data['sourcePosition'][list(component)].mean(axis=(0,1))
        if abs(centre[0])<20 and centre[1]>1150 and centre[2]>60:
            region|=set().union(*(adj[i] for i in list(region)))
        result.append((f'flap_root_{min(component)}',region))
    for i,(_,region) in enumerate(result):
        if any(region&r for _,r in result[i+1:]):raise ValueError('Cap cleanup regions overlap')
    return result


def build_disk(v,t,mask,target_area,pins=None):
    loop=loop_of_patch(t,mask);origin,basis=frame_for(v[loop])
    pins=np.array([] if pins is None else [p for p in pins if p not in set(loop)],dtype=int)
    fixed=np.r_[loop,pins];xy=(v[fixed]-origin)@basis[:2].T
    segments=np.c_[np.arange(len(loop)),np.roll(np.arange(len(loop)),-1)]
    mesh=triangle.triangulate(dict(vertices=xy,segments=segments),f'pq20a{target_area:.8f}Y')
    np.testing.assert_array_equal(mesh['vertices'][:len(fixed)],xy)
    q=mesh['vertices'];nt=mesh['triangles']
    if not np.isin(np.arange(len(fixed)),nt).all():raise ValueError('A protected patch point lies outside its chart')
    outside=np.flatnonzero(~mask & np.any(np.isin(t,loop),axis=1))
    collar=np.unique(np.r_[t[outside].ravel(),fixed])
    sample=(v[collar]-origin)@basis.T
    scale=max(np.ptp(xy,axis=0).max(),1.)
    # Interpolate the exterior collar, not the folded cap walls. A smooth
    # thin-plate field supplies the missing interior. Boundary/medial pins
    # remain exact, including the existing cleavage valley through this patch.
    model=RBFInterpolator(sample[:,:2]/scale,sample[:,2],kernel='thin_plate_spline',degree=1,smoothing=0.)
    height=model(q/scale)
    p=origin+q@basis[:2]+height[:,None]*basis[2];p[:len(fixed)]=v[fixed]
    cross=np.cross(p[nt[:,1]]-p[nt[:,0]],p[nt[:,2]]-p[nt[:,0]])
    if np.any(cross@basis[2]<=0):raise ValueError('Patch folds in its exterior chart')
    return p,nt,fixed,loop,origin,basis


def close_roots(data):
    v,t=data['position'],data['triangles'];groups=data['group'];parent=data['sourceFace']
    retained=np.ones(len(t),bool);positions=[];triangles=[];uvs=[];weights=[];labels=[];native=[];native_bary=[];patches=[]
    next_vertex=len(v)
    for label,region in cap_regions(data):
        mask=np.isin(parent,list(region));retained&=~mask
        # Only real retained surface vertices can constrain the medial valley;
        # unsupported cap-only vertices are never treated as anatomy evidence.
        measured=mask&~np.char.endswith(groups,'_cap');ids=np.unique(t[measured])
        pins=ids[(abs(v[ids,0])<1e-4)&(v[ids,1]>1230)&(v[ids,2]>60)]
        target=1.5*float(np.mean(areas(v[t[mask]])))
        p,nt,fixed,loop,origin,basis=build_disk(v,t,mask,target,pins)
        mapped=np.r_[fixed,np.arange(next_vertex,next_vertex+len(p)-len(fixed))];next_vertex+=len(p)-len(fixed)
        positions.extend(p[len(fixed):]);triangles.extend(mapped[nt])
        old=np.flatnonzero(mask);cross=np.cross(v[t[old,1]]-v[t[old,0]],v[t[old,2]]-v[t[old,0]])
        witness=old[(cross@basis[2]>0)&~np.char.endswith(groups[old],'_cap')]
        if not len(witness):raise ValueError('No exterior witnesses for flap root')
        _,face,bary=closest_surface(p[nt].reshape(-1,3),v[t[witness]])
        face=witness[face].reshape(-1,3);bary=bary.reshape(-1,3,3)
        uvs.extend(np.einsum('fci,fcij->fcj',bary,data['uv'][face]));weights.extend(np.einsum('fci,fcij->fcj',bary,data['weights'][face]))
        native.extend(parent[face]);native_bary.extend(np.einsum('fci,fcij->fcj',bary,data['sourceBarycentric'][face]))
        _,centre,_=closest_surface(p[nt].mean(1),v[t[witness]]);labels.extend(groups[witness[centre]])
        patches.append(dict(name=label,oldFaces=old,loop=loop,origin=origin,basis=basis,
                            firstNewFace=len(triangles)-len(nt),newFaceCount=len(nt),
                            fixedVertexIds=fixed,cleavagePinCount=len(pins),maxFixedErrorMm=float(np.linalg.norm(p[:len(fixed)]-v[fixed],axis=1).max())))
    keep=np.flatnonzero(retained);nt=np.r_[t[keep],np.array(triangles)];nv=np.r_[v,np.array(positions)]
    used,inverse=np.unique(nt,return_inverse=True);nt=inverse.reshape(-1,3);nv=nv[used]
    result=dict(position=nv,triangles=nt,normal=normals(nv,nt)[nt],boneNames=data['boneNames'],
                uv=np.r_[data['uv'][keep],np.array(uvs)],weights=np.r_[data['weights'][keep],np.array(weights)],
                group=np.r_[groups[keep],np.array(labels)],originalDenseFace=np.r_[keep,np.full(len(triangles),-1)],
                originalVertex=np.where(used<len(v),used,-1),
                cornerNativeFace=np.r_[np.repeat(parent[keep,None],3,axis=1),np.array(native)],
                cornerNativeBary=np.r_[data['sourceBarycentric'][keep],np.array(native_bary)])
    for patch in patches:patch['firstNewFace']+=len(keep)
    return result,patches


def run(args):
    out=Path(args.output);out.mkdir(parents=True,exist_ok=True)
    density_path=Path(args.raw)/'density-report.json'
    if density_path.exists():(out/'density-report.json').write_text(density_path.read_text())
    original=dict(np.load('outputs/vanilla-reference-v1/vanilla-reference.npz'))
    if args.mode=='raw':path=Path(args.raw)/'normal-map-reference.npz'
    else:
        field=filter_cloth_field(NormalField(original),args.mode,args.sigma)
        reconstruct(SimpleNamespace(reference='outputs/vanilla-reference-v1/vanilla-reference.npz',assets='outputs/normal-map-assets-v1',
                                    control=Path(args.raw)/'control.npz',output=out,position_weight=.0001),field)
        path=out/'normal-map-reference.npz'
    before=dict(np.load(path));result,patches=close_roots(before)
    result.update(clothFilterMode=np.array(args.mode),bodyOffsetMm=np.array(0.))
    connectivity=boundaries(result['triangles'])
    if connectivity['openBoundaryLoops']!=5:raise ValueError('Unexpected body openings')
    lm=dict(np.load(Path(args.raw)/'paired-landmarks-candidate.npz'))
    paired,rebound=bind_points(before,result,patches,lm,result['position'])
    np.savez_compressed(out/'body-fit-reference.npz',**result);np.savez_compressed(out/'paired-landmarks-candidate.npz',**paired)
    write_obj(out/'vanilla-body-fit.obj',result['position'],result['triangles'],result['normal'],result['uv'],result['group'])
    write_points(out/'Vanilla-BodyFit.points.json',paired['names'],paired['sourceFace'],paired['sourceBary'])
    for patch in patches:
        mask=np.zeros(len(before['triangles']),bool);mask[patch['oldFaces']]=True
        after=np.zeros(len(result['triangles']),bool);after[patch['firstNewFace']:patch['firstNewFace']+patch['newFaceCount']]=True
        patch['beforeJumps']=jump_stats(before['position'],before['triangles'],mask);patch['afterJumps']=jump_stats(result['position'],result['triangles'],after)
        for key in ('oldFaces','loop','origin','basis','fixedVertexIds'):patch[key]=patch[key].tolist()
    report=dict(mode=args.mode,offsetMm=0,underwearEnvelopeRetained=True,patches=patches,
                connectivity=connectivity,vertices=len(result['position']),triangles=len(result['triangles']),
                landmarks=len(paired['names']),chartReboundPoints=rebound,
                scope='Body-only candidate: existing flap removal retained, all six roots rebuilt; '+('raw relief retained' if args.mode=='raw' else 'cloth relief filtered')+' without underwear removal or thickness offset.')
    (out/'body-cleanup-report.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps({k:v for k,v in report.items() if k!='patches'},indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--raw',required=True);p.add_argument('--output',required=True)
    p.add_argument('--mode',choices=('raw','blur','neutral'),required=True);p.add_argument('--sigma',type=float,default=8.)
    run(p.parse_args())
