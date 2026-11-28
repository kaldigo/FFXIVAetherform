"""Rebuild folded lower flap-root patches and repair larger surface detail.

All work is an offline candidate. Old references and accepted points are inputs.
New patch topology carries explicit per-corner provenance instead of pretending
that each new triangle belongs to one old native face.
"""
import argparse
import hashlib
import json
from pathlib import Path

import numpy as np
from scipy.sparse import coo_matrix,diags
from scipy.sparse.linalg import spsolve
import triangle

from reference_surface import boundary_edges,weld,write_obj
from compare_af_bases import closest_surface
from clean_reference import normals


def adjacency(t):
    edge={};adj=[set() for _ in t]
    for i,f in enumerate(t):
        for a,b in ((0,1),(1,2),(2,0)):edge.setdefault(tuple(sorted(f[[a,b]])),[]).append(i)
    for rows in edge.values():
        for i in rows:adj[i].update(rows)
    return adj


def loop_of_patch(t,mask):
    rows=boundary_edges(t[mask]);out={int(a):int(b) for a,b,*_ in rows}
    if len(out)!=len(rows):raise ValueError('Patch boundary branches')
    start=next(iter(out));loop=[];i=start
    while i not in loop and i in out:loop.append(i);i=out[i]
    if i!=start or len(loop)!=len(rows):raise ValueError('Patch is not one disk')
    return np.array(loop)


def frame_for(p):
    origin=p.mean(axis=0);_,_,basis=np.linalg.svd(p-origin,full_matrices=False)
    u,w=basis[:2];xy=(p-origin)@np.array([u,w]).T
    signed=sum(a[0]*b[1]-a[1]*b[0] for a,b in zip(xy,np.roll(xy,-1,axis=0)))
    if signed<0:w=-w
    return origin,np.array([u,w,np.cross(u,w)])


def patch_geometry(v,t,mask,target_area):
    loop=loop_of_patch(t,mask);boundary=v[loop]
    origin,basis=frame_for(boundary);xy=(boundary-origin)@basis[:2].T
    # A one-ring native expansion removes the recessed wall at the old root.
    # Keep its dense outer boundary exactly, adding no edge splits.
    segments=np.c_[np.arange(len(loop)),np.roll(np.arange(len(loop)),-1)]
    result=triangle.triangulate(dict(vertices=xy,segments=segments),f'pq20a{target_area:.8f}Y')
    if not np.array_equal(result['vertices'][:len(loop)],xy):raise ValueError('Boundary changed during triangulation')
    uv=result['vertices'];new_t=result['triangles']
    outside=np.flatnonzero(~mask & np.any(np.isin(t,loop),axis=1))
    collar=np.unique(t[outside])
    samples=(v[collar]-origin)@basis.T
    scale=max(np.ptp(xy,axis=0).max(),1.)
    def design(points):
        x,y=(points/scale).T
        return np.c_[np.ones(len(x)),x,y,x*x,x*y,y*y]
    coef=np.linalg.lstsq(design(samples[:,:2]),samples[:,2],rcond=1e-8)[0]
    height=design(uv)@coef
    residual=np.zeros(len(uv));residual[:len(loop)]=(boundary-origin)@basis[2]-height[:len(loop)]
    # Harmonic extension of boundary residuals preserves the exact boundary
    # while leaving the broad quadratic curvature inside the patch.
    edge=np.unique(np.sort(new_t[:,[[0,1],[1,2],[2,0]]].reshape(-1,2),axis=1),axis=0)
    length=np.linalg.norm(uv[edge[:,0]]-uv[edge[:,1]],axis=1)
    a=coo_matrix((np.r_[1/length,1/length],(np.r_[edge[:,0],edge[:,1]],np.r_[edge[:,1],edge[:,0]])),shape=(len(uv),len(uv))).tocsr()
    lap=diags(np.asarray(a.sum(axis=1)).ravel())-a
    fixed=np.arange(len(loop));free=np.arange(len(loop),len(uv))
    if len(free):residual[free]=spsolve(lap[free][:,free],-lap[free][:,fixed]@residual[fixed])
    points=origin+uv@basis[:2]+(height+residual)[:,None]*basis[2]
    points[:len(loop)]=boundary
    n=np.cross(points[new_t[:,1]]-points[new_t[:,0]],points[new_t[:,2]]-points[new_t[:,0]])
    if np.any(n@basis[2]<=0):raise ValueError('Reconstructed patch folds in its chart')
    return points,new_t,loop,origin,basis


def native_cap_regions(data):
    v,t=weld(data['sourcePosition']);adj=adjacency(t)
    ids=set(np.flatnonzero(data['sourceGroups']=='legs_smallclothes_cap'));remaining=set(ids);regions=[]
    while remaining:
        component=set();todo=[next(iter(remaining))]
        while todo:
            i=todo.pop()
            if i in component:continue
            component.add(i);todo.extend((adj[i]&ids)-component)
        remaining-=component
        expanded=component|set().union(*(adj[i] for i in component))
        centre=v[t[list(component)]].mean(axis=(0,1))
        label='left_hip_root' if centre[0]<-50 else 'right_hip_root' if centre[0]>50 else 'rear_root'
        regions.append((label,expanded))
    for i,(_,a) in enumerate(regions):
        for _,b in regions[i+1:]:
            if a&b:raise ValueError('Native repair regions overlap')
    return regions


def remesh(data):
    v=data['position'].copy();t=data['triangles'];groups=data['group']
    retained=np.ones(len(t),bool);patches=[];new_positions=[];new_tri=[];new_uv=[];new_w=[];new_groups=[]
    corner_face=[];corner_bary=[];old_face_ids=[]
    next_vertex=len(v)
    for label,region in native_cap_regions(data):
        mask=np.isin(data['sourceFace'],list(region));retained&=~mask
        points,tri,loop,origin,basis=patch_geometry(v,t,mask,35.)
        vertex_ids=np.r_[loop,np.arange(next_vertex,next_vertex+len(points)-len(loop))]
        next_vertex+=len(points)-len(loop)
        new_positions.extend(points[len(loop):]);new_tri.extend(vertex_ids[tri])
        old=np.flatnonzero(mask)
        # Exclude the generated caps and inward-facing root walls from the
        # attribute witnesses. Their exterior neighbours supply the attributes.
        cross=np.cross(v[t[old,1]]-v[t[old,0]],v[t[old,2]]-v[t[old,0]])
        good=(cross@basis[2]>0)&~np.char.endswith(groups[old],'_cap')
        witness=old[good]
        if not len(witness):raise ValueError('No exterior attribute witnesses')
        _,fi,bc=closest_surface(points[tri].reshape(-1,3),v[t[witness]])
        fi=witness[fi];bc=bc.reshape(-1,3,3);fi=fi.reshape(-1,3)
        new_uv.extend(np.einsum('fci,fcij->fcj',bc,data['uv'][fi]))
        new_w.extend(np.einsum('fci,fcij->fcj',bc,data['weights'][fi]))
        native=data['sourceFace'][fi]
        parent_bary=np.einsum('fci,fcij->fcj',bc,data['sourceBarycentric'][fi])
        corner_face.extend(native);corner_bary.extend(parent_bary)
        _,centre_face,_=closest_surface(points[tri].mean(axis=1),v[t[witness]])
        new_groups.extend(groups[witness[centre_face]])
        old_face_ids.extend([-1]*len(tri))
        patches.append(dict(name=label,oldFaces=old,loop=loop,origin=origin,basis=basis,
                            firstNewFace=len(new_tri)-len(tri),newFaceCount=len(tri),
                            maxBoundaryErrorMm=float(np.linalg.norm(points[:len(loop)]-v[loop],axis=1).max())))
    keep=np.flatnonzero(retained)
    nt=np.concatenate((t[keep],np.array(new_tri)))
    nv=np.concatenate((v,np.array(new_positions)))
    used,inverse=np.unique(nt,return_inverse=True);nt=inverse.reshape(-1,3);nv=nv[used]
    result=dict(position=nv,triangles=nt,
                uv=np.concatenate((data['uv'][keep],np.array(new_uv))),
                weights=np.concatenate((data['weights'][keep],np.array(new_w))),boneNames=data['boneNames'],
                group=np.r_[groups[keep],np.array(new_groups)],
                cornerNativeFace=np.concatenate((np.repeat(data['sourceFace'][keep,None],3,axis=1),np.array(corner_face))),
                cornerNativeBary=np.concatenate((data['sourceBarycentric'][keep],np.array(corner_bary))),
                originalDenseFace=np.r_[keep,np.array(old_face_ids)],originalVertex=np.where(used<len(v),used,-1))
    for p in patches:p['firstNewFace']+=len(keep)
    return result,patches


def boundaries(t):
    edges={}
    for i,f in enumerate(t):
        for a,b in ((0,1),(1,2),(2,0)):
            x,y=f[a],f[b];edges.setdefault(tuple(sorted((x,y))),[]).append((int(x),int(y)))
    nonmanifold=sum(len(r)>2 for r in edges.values())
    inconsistent=sum(len(r)==2 and r[0]==r[1] for r in edges.values())
    adjacent={}
    for (a,b),rows in edges.items():
        if len(rows)==1:adjacent.setdefault(a,set()).add(b);adjacent.setdefault(b,set()).add(a)
    remaining=set(adjacent);loops=0
    while remaining:
        seen=set();todo=[next(iter(remaining))]
        while todo:
            i=todo.pop()
            if i in seen:continue
            seen.add(i);todo.extend(adjacent[i]-seen)
        remaining-=seen;loops+=1
    if nonmanifold or inconsistent or any(len(r)!=2 for r in adjacent.values()):raise ValueError('Invalid shell connectivity')
    return dict(nonmanifoldEdges=nonmanifold,inconsistentEdgeWindings=inconsistent,openBoundaryLoops=loops)


def run(args):
    path=Path(args.reference);data=dict(np.load(path));out=Path(args.output);out.mkdir(parents=True,exist_ok=True)
    result,patches=remesh(data)
    audit=boundaries(result['triangles'])
    if audit['openBoundaryLoops']!=5:raise ValueError('Unexpected shell openings')
    for p in patches:
        p['oldFaces']=p['oldFaces'].tolist();p['loop']=p['loop'].tolist();p['origin']=p['origin'].tolist();p['basis']=p['basis'].tolist()
    np.savez_compressed(out/'remeshed-reference.npz',**result)
    write_obj(out/'vanilla-remeshed.obj',result['position'],result['triangles'],normals(result['position'],result['triangles'])[result['triangles']],result['uv'],result['group'])
    report=dict(sourceSha256=hashlib.sha256(path.read_bytes()).hexdigest(),patches=patches,
                oldVertices=len(data['position']),newVertices=len(result['position']),oldTriangles=len(data['triangles']),
                newTriangles=len(result['triangles']),connectivity=audit,weightsMaxSumError=float(abs(result['weights'].sum(axis=-1)-1).max()),
                scope='Three local lower flap-root disks rebuilt; exterior boundary positions fixed. No AF or accepted-reference edits.')
    (out/'repair-report.json').write_text(json.dumps(report,indent=2)+'\n')
    print(json.dumps({k:v for k,v in report.items() if k!='patches'},indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--reference',type=Path,required=True);p.add_argument('--output',type=Path,required=True)
    run(p.parse_args())
