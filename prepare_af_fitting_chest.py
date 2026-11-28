"""Make an AF fitting copy by continuing breast curvature through the nipple."""
import argparse,hashlib,json,shutil
from pathlib import Path
import numpy as np
from scipy.interpolate import RBFInterpolator
from reference_surface import areas,unit,load_snapshot,mesh_arrays,write_obj
from transfer_landmarks import read_obj,read_points
from compare_af_bases import load_af
from clean_reference import normals
from mesh_crossings import crossing_pairs


def basis(uv):
    x,y=uv.T
    return np.column_stack((np.ones(len(x)),x,y,x*x,x*y,y*y,x*x*x,x*x*y,x*y*y,y*y*y))


def connectivity(t):
    directed=t[:,[[0,1],[1,2],[2,0]]].reshape(-1,2);edges,inverse,count=np.unique(np.sort(directed,axis=1),axis=0,return_inverse=True,return_counts=True)
    sign=np.where(directed[:,0]<directed[:,1],1,-1);balance=np.bincount(inverse,weights=sign)
    boundary=edges[count==1];degree=np.bincount(boundary.ravel(),minlength=t.max()+1)
    return dict(nonmanifoldEdges=int((count>2).sum()),inconsistentTwoFaceEdgeWindings=int(((count==2)&(balance!=0)).sum()),
        boundaryEdges=len(boundary),boundaryJunctionVertices=int(((degree>0)&(degree!=2)).sum()))


def continue_curve(q,mass,support,core=9.,outer=18.,collar=(14.,26.)):
    """Cubic graph extrapolated from an undetailed collar, with a C2 join.

    q has local tangent coordinates and outward height. The collar supplies
    the slope and curvature; the nipple interior supplies no shape samples.
    Only height changes. Vertex identities and tangent coordinates are fixed.
    """
    radius=np.linalg.norm(q[:,:2],axis=1);sample=support&(radius>=collar[0])&(radius<=collar[1])
    if sample.sum()<30:raise ValueError('Insufficient surrounding breast samples')
    scale=20.;A=basis(q[:,:2]/scale);weight=mass[sample].copy();weight/=np.median(weight)
    robust=np.ones(sample.sum())
    for _ in range(8):
        root=np.sqrt(weight*robust);coef=np.linalg.lstsq(A[sample]*root[:,None],q[sample,2]*root,rcond=None)[0]
        residual=A[sample]@coef-q[sample,2];sigma=max(1.4826*np.median(abs(residual-np.median(residual))),.03)
        robust=np.minimum(1.,1.5*sigma/np.maximum(abs(residual),1e-12))
    t=np.clip((radius-core)/(outer-core),0,1);blend=1-(6*t**5-15*t**4+10*t**3)
    blend[~support]=0;delta=blend*(A@coef-q[:,2]);delta[radius>=outer]=0
    return delta,dict(coreRadiusMm=core,outerRadiusMm=outer,collarRadiusMm=list(collar),polynomialScaleMm=scale,
        coefficients=coef.tolist(),collarSamples=int(sample.sum()),collarResidualRmsMm=float(np.sqrt(np.average(residual*residual,weights=weight))),
        centrePrincipalGraphCurvaturesPerMm=np.linalg.eigvalsh(np.array([[2*coef[3],coef[4]],[coef[4],2*coef[5]]])/scale**2).tolist())


def continue_collar_surface(q,mass,support,outer=18.,collar=(14.,26.)):
    """Fill the missing breast surface from a sampled surrounding collar.

    A quadratic-reproducing thin-plate interpolant follows the actual collar,
    rather than blending a global cubic with the old nipple skirt. The small
    remaining collar residual tapers to exactly zero at the unchanged exterior.
    """
    r=np.linalg.norm(q[:,:2],axis=1);sample=support&(r>=collar[0])&(r<=collar[1]);scale=20.
    if sample.sum()<30:raise ValueError('Insufficient surrounding breast samples')
    smoothing=1e-5*np.median(mass[sample])/mass[sample]
    model=RBFInterpolator(q[sample,:2]/scale,q[sample,2],kernel='thin_plate_spline',degree=2,smoothing=smoothing)
    changed=support&(r<outer);height=q[:,2].copy();height[changed]=model(q[changed,:2]/scale)
    t=np.clip((r-collar[0])/(outer-collar[0]),0,1);blend=1-(6*t**5-15*t**4+10*t**3)
    delta=np.where(changed,blend*(height-q[:,2]),0.)
    residual=model(q[sample,:2]/scale)-q[sample,2]
    h=.1;probes=np.array([[0,0],[h,0],[-h,0],[0,h],[0,-h],[h,h],[h,-h],[-h,h],[-h,-h]])
    z=model(probes/scale);hessian=np.array([[(z[1]+z[2]-2*z[0])/h**2,(z[5]-z[6]-z[7]+z[8])/(4*h*h)],[(z[5]-z[6]-z[7]+z[8])/(4*h*h),(z[3]+z[4]-2*z[0])/h**2]])
    return delta,dict(method='Quadratic-reproducing thin-plate collar infill',outerRadiusMm=outer,collarRadiusMm=list(collar),
        collarSamples=int(sample.sum()),coordinateScaleMm=scale,smoothingCoefficient=1e-5,centreHeightMm=float(z[0]),
        collarResidualRmsMm=float(np.sqrt(np.average(residual*residual,weights=mass[sample]))),centrePrincipalGraphCurvaturesPerMm=np.linalg.eigvalsh(hessian).tolist())


def run(args):
    out=Path(args.output);out.mkdir(parents=True,exist_ok=True)
    original_path=Path('outputs/vanilla-reference-v1/landmarks/AF-Small-MediumB.obj')
    v,t=read_obj(original_path);q=v.copy();native_normals=normals(v,t);mass=np.zeros(len(v))
    for k in range(3):np.add.at(mass,t[:,k],areas(v[t])/3)
    records=[]
    for side in (-1,1):
        search=(v[:,0]*side>40)&(v[:,0]*side<100)&(v[:,1]>1210)&(v[:,1]<1270)&(v[:,2]>90)
        candidates=np.flatnonzero(search);tip=int(candidates[np.argmax(v[candidates,2])]);origin=v[tip]
        distance=np.linalg.norm(v-origin,axis=1);collar=(distance>14)&(distance<24)&(v[:,2]>95)
        n=unit(np.average(native_normals[collar],axis=0,weights=mass[collar]));u=np.array([1.,0,0]);u=unit(u-n*n[0]);w=np.cross(n,u);frame=np.stack((u,w,n),axis=1)
        local=(v-origin)@frame
        support=(distance<38)&(v[:,2]>90)&((native_normals@n)>.3)
        delta,record=(continue_curve(local,mass,support) if args.method=='cubic-v1' else continue_collar_surface(local,mass,support))
        q+=delta[:,None]*n
        record.update(side='left' if side>0 else 'right',originMm=origin.tolist(),frame=frame.tolist(),tipVertex=tip,
            modifiedVertices=int((abs(delta)>1e-9).sum()),maximumInwardMovementMm=float(-delta.min()),maximumOutwardMovementMm=float(delta.max()))
        records.append(record)
    modified=np.linalg.norm(q-v,axis=1)>1e-9;affected=modified[t].any(1)
    oldcross=np.cross(v[t[:,1]]-v[t[:,0]],v[t[:,2]]-v[t[:,0]]);newcross=np.cross(q[t[:,1]]-q[t[:,0]],q[t[:,2]]-q[t[:,0]])
    ratio=np.sum(newcross*unit(oldcross),axis=1)/np.linalg.norm(oldcross,axis=1)
    if ratio[affected].min()<=0:raise ValueError('Curve continuation reversed a triangle')
    source_top=Path(args.bodies)/'af_top_01.json';source_legs=Path(args.bodies)/'af_dwn_04.json'
    sources=[load_snapshot(p) for p in (source_top,source_legs)]
    bones=sorted({b for s,_,_ in sources for b in s['tables']['Bones']});uvs=[];weights=[];authored=[];native_indices=[];groups=[];position=[]
    for slot,(s,data,meshes),path in zip(('top','legs'),sources,(source_top,source_legs)):
        mesh=next(m for m in meshes if m['id']==0);p,n,uv,weight=mesh_arrays(s,data,mesh,bones)
        af=load_af(path);position.append(p);uvs.append(uv);weights.append(weight);authored.append(n);native_indices.append(af['indices']);groups.extend([slot]*len(p))
    np.testing.assert_array_equal(np.concatenate(position),v[t])
    output_normals=normals(q,t)[t];uv=np.concatenate(uvs);weights=np.concatenate(weights);group=np.array(groups)
    crossings=crossing_pairs(q[t],q[t],self_faces=t);new_local=crossings[affected[crossings].any(1)]
    if len(new_local):raise ValueError('Fitting chest has nonadjacent local intersections')
    original_crossings=crossing_pairs(v[t],v[t],self_faces=t)
    np.testing.assert_array_equal(crossings,original_crossings)
    protected=(abs(v[:,0])<35)|(v[:,1]<1215)
    np.testing.assert_array_equal(q[protected],v[protected])
    topology=connectivity(t)
    data=dict(position=q,triangles=t,normal=output_normals,uv=uv,weights=weights,boneNames=np.array(bones),group=group,
        sourcePosition=v,sourceNormal=np.concatenate(authored),nativeVertexIndices=np.concatenate(native_indices),modifiedVertex=modified)
    np.savez_compressed(out/'af-fitting-reference.npz',**data)
    write_obj(out/'AF-Fitting-Small-MediumB.obj',q,t,output_normals,uv,group)
    write_obj(out/'AF-Original-Small-MediumB.obj',v,t,native_normals[t],uv,group)
    points=Path('outputs/vanilla-reference-v1/landmarks/AF-Small-MediumB.points.json')
    shutil.copyfile(points,out/'AF-Fitting.points.json');names,face,bary=read_points(points)
    old_point=np.einsum('ni,nij->nj',bary,v[t[face]]);new_point=np.einsum('ni,nij->nj',bary,q[t[face]])
    np.savez_compressed(out/'af-fitting-landmarks.npz',names=np.array(names),face=face,bary=bary,position=new_point,sourcePosition=old_point)
    description='Original cubic continuation with radial blend' if args.method=='cubic-v1' else 'Thin-plate surface continuation constrained by the surrounding breast collar, reproducing quadratic curvature; only the small collar-fit residual tapers to the unchanged exterior'
    report=dict(role='AF static fitting copy, Small chest / Medium B legs, Hip Dips and Soft Butt off',method=description,
        inputObjSha256=hashlib.sha256(original_path.read_bytes()).hexdigest(),sourceHashes={slot:s['sha256'] for slot,(s,_,_) in zip(('top','legs'),sources)},
        vertices=len(q),triangles=len(t),modifiedVertices=int(modified.sum()),affectedTriangles=int(affected.sum()),patches=records,
        maximumMovementMm=float(np.linalg.norm(q-v,axis=1).max()),minimumAffectedProjectedAreaRatio=float(ratio[affected].min()),minimumTriangleAreaMm2=float(areas(q[t]).min()),
        localNonadjacentIntersections=len(new_local),globalNonadjacentIntersections=len(crossings),originalGlobalNonadjacentIntersections=len(original_crossings),globalIntersectionPairsUnchanged=True,topology=topology,
        topologyUnchanged=True,uvsUnchanged=True,namedWeightsUnchanged=True,cleavageAndUnderbreastUnchanged=True,legsUnchanged=bool(np.array_equal(q[np.unique(t[len(position[0]):])],v[np.unique(t[len(position[0]):])])),
        landmarkCount=len(names),maximumLandmarkMovementMm=float(np.linalg.norm(new_point-old_point,axis=1).max()),scope='Only nipple-region positions edited on a separate AF fitting copy. The selected AF source assets and vanilla working reference are unchanged. No texture normal map is applied to review shading.')
    (out/'af-fitting-chest-report.json').write_text(json.dumps(report,indent=2)+'\n')
    artifacts={p.name:dict(path=p.as_posix(),sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in [out/'af-fitting-reference.npz',out/'AF-Fitting-Small-MediumB.obj',out/'AF-Fitting.points.json',out/'af-fitting-landmarks.npz']}
    manifest=dict(role='AF fitting copy',stage='Chest surface continuation, '+args.method,status='Prepared for user review',
        base=dict(chest='af_top_01 / Small',legs='af_dwn_04 / Medium B',hipDips=False,softButt=False,sourceHashes=report['sourceHashes']),
        vertices=len(q),triangles=len(t),artifacts=artifacts,report=(out/'af-fitting-chest-report.json').as_posix(),
        note='Same AF native vertex/face order and point addresses; only localized nipple-region positions changed. The vanilla working-mesh.json is a separate reference and is unchanged.')
    if not args.no_manifest:
        old=Path('af-fitting-mesh.json');archive=Path('af-fitting-mesh-v1.json')
        if old.exists() and not archive.exists():archive.write_bytes(old.read_bytes())
        old.write_text(json.dumps(manifest,indent=2)+'\n')
    print(json.dumps(report,indent=2),flush=True)


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--bodies',default='../.work/resolver-preparation-20261118/bodies');p.add_argument('--output',default='outputs/af-fitting-chest-v2');p.add_argument('--method',choices=['cubic-v1','collar-v2'],default='collar-v2');p.add_argument('--no-manifest',action='store_true');run(p.parse_args())
