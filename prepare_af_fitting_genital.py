"""Unfold connected genital detail into an AF fitting envelope, keeping topology."""
import argparse,hashlib,json,shutil
from pathlib import Path
import numpy as np
from scipy.sparse import coo_matrix,diags
from scipy.sparse.linalg import spsolve
from scipy.sparse.csgraph import connected_components
from clean_reference import normals
from reference_surface import areas,write_obj
from mesh_crossings import crossing_pairs
from transfer_landmarks import read_points
from prepare_af_fitting_chest import connectivity


def signed_area(p):
    a=p[:,1]-p[:,0];b=p[:,2]-p[:,0]
    return a[:,0]*b[:,1]-a[:,1]*b[:,0]


def positive_cotangent_laplacian(v,t):
    """Positive intrinsic cotangent weights retain native triangle spacing."""
    rows=[];cols=[];values=[]
    for k in range(3):
        i,j=(k+1)%3,(k+2)%3;a=v[t[:,i]]-v[t[:,k]];b=v[t[:,j]]-v[t[:,k]]
        cot=np.sum(a*b,axis=1)/np.linalg.norm(np.cross(a,b),axis=1)/2
        rows.extend((t[:,i],t[:,j]));cols.extend((t[:,j],t[:,i]));values.extend((cot,cot))
    A=coo_matrix((np.concatenate(values),(np.concatenate(rows),np.concatenate(cols))),shape=(len(v),len(v))).tocsr()
    A.data=np.maximum(A.data,1e-4)
    return diags(np.asarray(A.sum(1)).ravel())-A


def disk_chart(v,t,selected,frame,origin,rings=2):
    """Positive harmonic disk embedding, with the original outer collar fixed."""
    faces=np.flatnonzero(selected);global_t=t[faces];ids=np.unique(global_t)
    remap=np.full(len(v),-1,int);remap[ids]=np.arange(len(ids));lt=remap[global_t]
    edges,count=np.unique(np.sort(lt[:,[[0,1],[1,2],[2,0]]].reshape(-1,2),axis=1),axis=0,return_counts=True)
    boundary_edges=edges[count==1];boundary=np.unique(boundary_edges)
    degree=np.bincount(boundary_edges.ravel(),minlength=len(ids))
    A=coo_matrix((np.ones(len(edges)*2),(np.r_[edges[:,0],edges[:,1]],np.r_[edges[:,1],edges[:,0]])),shape=(len(ids),len(ids))).tocsr()
    components,_=connected_components(A)
    if components!=1 or len(ids)-len(edges)+len(lt)!=1 or np.any(degree[boundary]!=2):raise ValueError('Genital selection is not a single disk')
    fixed=boundary
    for _ in range(rings):fixed=np.unique(np.r_[fixed,A[fixed].indices])
    free=np.setdiff1d(np.arange(len(ids)),fixed);L=positive_cotangent_laplacian(v[ids],lt)
    local=(v[ids]-origin)@frame;uv=local[:,:2].copy()
    uv[free]=spsolve(L[free][:,free],-(L[free][:,fixed]@uv[fixed]))
    before=signed_area(local[lt,:2]);after=signed_area(uv[lt])
    if after.min()<=1e-8:raise ValueError('Fixed-collar chart contains reversed or collapsed triangles')
    return dict(faces=faces,vertices=ids,triangles=lt,boundary=boundary,fixed=fixed,free=free,L=L,local=local,uv=uv,
        originalProjectedArea=before,projectedArea=after)


def side_slope_model(local,normals_local,mass):
    """Moving quadratic Hermite fits to the skin on both sides of the slit."""
    good=(abs(local[:,0])>=9)&(abs(local[:,0])<=24)&(normals_local[:,2]>.3)
    good&=(local[:,1]>-45)&(local[:,1]<110)&(local[:,2]>-40)&(local[:,2]<40)
    sample=np.flatnonzero(good);p=local[sample];g=-normals_local[sample,:2]/normals_local[sample,2,None]
    fit_residual=[]
    def evaluate(uv):
        result=[]
        for x,s in uv:
            dx=p[:,0]/12;ds=(p[:,1]-s)/14
            weight=mass[sample]*np.exp(-.5*ds*ds)*np.exp(-.5*((abs(p[:,0])-12)/6)**2)
            selected=weight>weight.max()*1e-4;root=np.sqrt(weight[selected]/weight[selected].max())
            a=np.column_stack((np.ones(len(p)),dx,ds,dx*dx,dx*ds,ds*ds))
            ax=np.column_stack((np.zeros(len(p)),np.ones(len(p)),np.zeros(len(p)),2*dx,ds,np.zeros(len(p))))/12
            az=np.column_stack((np.zeros(len(p)),np.zeros(len(p)),np.ones(len(p)),np.zeros(len(p)),dx,2*ds))/14
            matrix=np.r_[a[selected]*root[:,None],4*ax[selected]*root[:,None],4*az[selected]*root[:,None]]
            rhs=np.r_[p[selected,2]*root,4*g[selected,0]*root,4*g[selected,1]*root]
            coef=np.linalg.lstsq(matrix,rhs,rcond=None)[0];xx=x/12
            result.append(coef@np.array([1,xx,0,xx*xx,0,0]))
            fit_residual.append(float(np.sqrt(np.average((a[selected]@coef-p[selected,2])**2,weights=weight[selected]))))
        return np.array(result)
    return evaluate,sample,fit_residual


def fit_envelope(v,t,chart,frame,origin):
    """Continue the undetailed side/collar surface across the unfolded disk."""
    ids=chart['vertices'];fixed=chart['fixed'];free=chart['free'];local=(v-origin)@frame
    n=normals(v,t)
    mass=np.zeros(len(v))
    for k in range(3):np.add.at(mass,t[:,k],areas(v[t])/3)
    normals_local=n@frame
    # Exclude all internal folds and the rear anatomical detail from the
    # donor strips; continuation is determined by the flank angles.
    allowed=(v[:,1]>835)&(v[:,1]<950)&(v[:,2]>-14)&(v[:,2]<105)
    normals_local=normals_local.copy();normals_local[~allowed,2]=-1
    model,sample,residuals=side_slope_model(local,normals_local,mass)
    height=model(chart['uv']);fit_rms=float(np.median(residuals))
    residual=chart['local'][fixed,2]-height[fixed]
    correction=np.zeros(len(ids));correction[fixed]=residual
    # Biharmonic residual extension matches the two fixed collar rings
    # without the slope discontinuity of a first-order harmonic residual.
    uv=chart['uv'];lt=chart['triangles'];e=uv[lt];a=e[:,1]-e[:,0];b=e[:,2]-e[:,0];double=abs(a[:,0]*b[:,1]-a[:,1]*b[:,0])
    rows=[];cols=[];values=[];vmass=np.zeros(len(uv))
    for k in range(3):
        i,j=(k+1)%3,(k+2)%3;u=e[:,i]-e[:,k];w=e[:,j]-e[:,k];cot=np.sum(u*w,axis=1)/double/2
        rows.extend((lt[:,i],lt[:,j],lt[:,i],lt[:,j]));cols.extend((lt[:,i],lt[:,j],lt[:,j],lt[:,i]));values.extend((cot,cot,-cot,-cot));np.add.at(vmass,lt[:,k],double/6)
    C=coo_matrix((np.concatenate(values),(np.concatenate(rows),np.concatenate(cols))),shape=(len(uv),len(uv))).tocsr()
    L=C.T@diags(1/np.maximum(vmass,1e-12))@C
    correction[free]=spsolve(L[free][:,free],-(L[free][:,fixed]@correction[fixed]))
    height+=correction
    target=np.column_stack((chart['uv'],height));result=v.copy();result[ids[free]]=target[free]@frame.T+origin
    np.testing.assert_array_equal(result[ids[fixed]],v[ids[fixed]])
    return result,dict(collarSampleVertices=sample.tolist(),collarSamples=len(sample),fixedCollarResidualRmsMm=float(np.sqrt(np.mean(residual**2))),
        medianSideFitRmsMm=fit_rms,method='Side-slope moving quadratic Hermite fit',transverseCoordinateScaleMm=12,
        sideSampleAbsXmm=[9,24],longitudinalWindowMm=14,slopeWeightLengthMm=4,residualFairing='Fixed two-ring collar; biharmonic residual extension',
        maximumTangentialRedistributionMm=float(np.linalg.norm(chart['uv'][free]-chart['local'][free,:2],axis=1).max()))


def run(args):
    out=Path(args.output);out.mkdir(parents=True,exist_ok=True);parent=Path('outputs/af-fitting-chest-v2')
    source_path=parent/'af-fitting-reference.npz';source_hash=hashlib.sha256(source_path.read_bytes()).hexdigest()
    d=dict(np.load(source_path));v,t=d['position'],d['triangles']
    theta=np.radians(35.);frame=np.array([[1.,0,0],[0,np.sin(theta),-np.cos(theta)],[0,np.cos(theta),np.sin(theta)]]);origin=np.array([0.,855.,30.])
    box=(abs(v[:,0])<18)&(v[:,1]>845)&(v[:,1]<925)&(v[:,2]>-10)&(v[:,2]<88)
    selected=box[t].all(1);chart=disk_chart(v,t,selected,frame,origin)
    q,fit=fit_envelope(v,t,chart,frame,origin);modified=np.linalg.norm(q-v,axis=1)>1e-9;affected=modified[t].any(1)
    old_pairs=crossing_pairs(v[t],v[t],self_faces=t);new_pairs=crossing_pairs(q[t],q[t],self_faces=t)
    local_pairs=new_pairs[affected[new_pairs].any(1)]
    if len(local_pairs):raise ValueError('Genital envelope intersects the surrounding AF mesh')
    old_set=set(map(tuple,old_pairs));new_set=set(map(tuple,new_pairs))
    if new_set-old_set:raise ValueError('Genital preparation introduced new intersection pairs')
    protected=(v[:,1]<840)|(v[:,2]<-14)|(v[:,1]>950)
    np.testing.assert_array_equal(q[protected],v[protected])
    output=dict(d);output.update(position=q,normal=normals(q,t)[t],parentPosition=v,genitalModifiedVertex=modified,
        modifiedVertex=d['modifiedVertex']|modified,genitalPatchFace=chart['faces'],genitalPatchVertex=chart['vertices'],genitalBoundaryVertex=chart['vertices'][chart['boundary']],
        genitalFixedVertex=chart['vertices'][chart['fixed']],genitalChartFrame=frame,genitalChartOrigin=origin,genitalChart=chart['uv'])
    np.savez_compressed(out/'af-fitting-reference.npz',**output)
    write_obj(out/'AF-Fitting-Small-MediumB.obj',q,t,output['normal'],d['uv'],d['group'])
    write_obj(out/'AF-Before-Genital-Fitting.obj',v,t,d['normal'],d['uv'],d['group'])
    shutil.copyfile(parent/'AF-Fitting.points.json',out/'AF-Fitting.points.json');names,face,bary=read_points(out/'AF-Fitting.points.json')
    before_points=np.einsum('ni,nij->nj',bary,v[t[face]]);after_points=np.einsum('ni,nij->nj',bary,q[t[face]])
    np.savez_compressed(out/'af-fitting-landmarks.npz',names=np.array(names),face=face,bary=bary,position=after_points,parentPosition=before_points,
        sourcePosition=np.einsum('ni,nij->nj',bary,d['sourcePosition'][t[face]]))
    post=(q[chart['vertices']]-origin)@frame;projected=signed_area(post[chart['triangles'],:2])
    report=dict(role='AF genital fitting-envelope candidate; accepted breast v2 retained',parentPath=source_path.as_posix(),parentSha256=source_hash,
        method='Connected disk unfolded using fixed-collar positive harmonic coordinates; side-slope continuation and fixed-collar fairing; no faces or vertices added, removed or reordered',
        patch=dict(vertices=len(chart['vertices']),faces=len(chart['faces']),eulerCharacteristic=1,boundaryVertices=len(chart['boundary']),fixedCollarVertices=len(chart['fixed']),freeVertices=len(chart['free']),
            inwardOrFoldedOriginalProjectedFaces=int((chart['originalProjectedArea']<=0).sum()),foldedFinalProjectedFaces=int((projected<=0).sum()),minimumFinalDoubleProjectedAreaMm2=float(projected.min()),
            minimumOriginalSpatialAreaMm2=float(areas(v[t[chart['faces']]]).min()),minimumFinalSpatialAreaMm2=float(areas(q[t[chart['faces']]]).min())),
        chartMapping='Positive intrinsic cotangent coordinates from the native 3D triangles; two fixed outer collar rings',
        internalGeometry='Genital detail belongs to a connected topological disk with no local open boundary or detached shell. Inward-folding surfaces are redistributed into the outer fitting sheet, not left buried underneath.',fit=fit,
        vertices=len(q),faces=len(t),movedVertices=int(modified.sum()),maximumMovementMm=float(np.linalg.norm(q-v,axis=1).max()),
        acceptedBreastsUnchanged=True,buttUnchanged=True,thighGapBelow840MmUnchanged=True,topology=connectivity(t),topologyUnchanged=True,uvsAndWeightsUnchanged=True,
        localIntersections=len(local_pairs),originalLocalIntersectionPairs=int(affected[old_pairs].any(1).sum()),originalGlobalIntersectionPairs=len(old_pairs),finalGlobalIntersectionPairs=len(new_pairs),removedIntersectionPairs=len(old_set-new_set),newIntersectionPairs=len(new_set-old_set),
        minimumEditedVertexYmm=float(q[modified,1].min()),
        landmarks=len(names),maximumLandmarkMovementMm=float(np.linalg.norm(after_points-before_points,axis=1).max()),
        scope='Genital area only. Rear cleft, anus, seat curve, thigh-gap contour below Y840 and accepted breast geometry stay exact. This starts the lower fitting envelope; it does not complete the posterior panty envelope or alter the native AF movement mesh.')
    (out/'af-fitting-genital-report.json').write_text(json.dumps(report,indent=2)+'\n')
    np.savez_compressed(out/'genital-diagnostics.npz',faces=chart['faces'],vertices=chart['vertices'],boundary=chart['vertices'][chart['boundary']],fixed=chart['vertices'][chart['fixed']],
        originalProjectedArea=chart['originalProjectedArea'],finalProjectedArea=projected,frame=frame,origin=origin,collarSamples=np.array(fit['collarSampleVertices']))
    assert hashlib.sha256(source_path.read_bytes()).hexdigest()==source_hash
    if not args.no_manifest:
        old=Path('af-fitting-mesh.json');archive=Path('af-fitting-mesh-chest-v2.json')
        if not archive.exists():
            snapshot=json.loads(old.read_text());snapshot['status']='Breast v2 accepted by user';archive.write_text(json.dumps(snapshot,indent=2)+'\n')
        base=json.loads(archive.read_text())['base']
        artifacts={p.name:dict(path=p.as_posix(),sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in [out/'af-fitting-reference.npz',out/'AF-Fitting-Small-MediumB.obj',out/'AF-Fitting.points.json',out/'af-fitting-landmarks.npz']}
        manifest=dict(role='AF fitting copy',stage='Accepted breast v2 plus genital envelope',status='Lower-body candidate prepared for review',base=base,vertices=len(q),triangles=len(t),
            artifacts=artifacts,report=(out/'af-fitting-genital-report.json').as_posix(),acceptedBreastManifest=archive.as_posix(),note='Original topology, UVs, weights and point addresses retained. Butt curve and thigh gap protected. Rear cleft/anal fitting envelope remains pending.')
        old.write_text(json.dumps(manifest,indent=2)+'\n')
    print(json.dumps({k:val for k,val in report.items() if k!='fit'},indent=2),flush=True)


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--output',default='outputs/af-fitting-genital-v3');p.add_argument('--no-manifest',action='store_true');run(p.parse_args())
