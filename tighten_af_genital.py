"""Make the unfolded AF genital sheet a flatter, close-covering envelope."""
import argparse,hashlib,json,shutil
from pathlib import Path
import numpy as np
from scipy.spatial import cKDTree
from scipy.sparse import coo_matrix,diags,eye,vstack
from scipy.sparse.linalg import splu
from scipy.optimize import linprog
from clean_reference import normals
from reference_surface import areas,write_obj
from prepare_af_fitting_genital import side_slope_model,signed_area
from transfer_landmarks import read_points
from mesh_crossings import crossing_pairs


def coverage_rows(uv,t,points):
    """Locate source material samples on the actual retained chart triangles."""
    p=uv[t];centre=p.mean(1);radius=np.linalg.norm(p-centre[:,None],axis=-1).max(1);tree=cKDTree(centre)
    rows=[];cols=[];values=[];missing=[];owners=[];barys=[]
    for start in range(0,len(points),256):
        q=points[start:start+256];near=tree.query_ball_point(q,radius.max()+1e-6)
        ri=np.repeat(np.arange(len(q)),[len(x) for x in near]);fi=np.concatenate(near).astype(int)
        a=p[fi,1]-p[fi,0];b=p[fi,2]-p[fi,0];d=q[ri]-p[fi,0];det=a[:,0]*b[:,1]-a[:,1]*b[:,0]
        u=(d[:,0]*b[:,1]-d[:,1]*b[:,0])/det;v=(a[:,0]*d[:,1]-a[:,1]*d[:,0])/det
        bc=np.c_[1-u-v,u,v];valid=bc.min(1)>=-1e-7
        for k in range(len(q)):
            hits=np.flatnonzero(valid&(ri==k));index=start+k
            if not len(hits):missing.append(index);continue
            hit=hits[np.argmax(bc[hits].min(1))];face=int(fi[hit]);bary=np.maximum(bc[hit],0);bary/=bary.sum()
            rows.extend([index]*3);cols.extend(t[face]);values.extend(bary);owners.append(index);barys.append((face,bary))
    matrix=coo_matrix((values,(rows,cols)),shape=(len(points),len(uv))).tocsr()
    return matrix,np.array(missing,int)


def source_samples(p,t,subdivisions=4):
    edges=np.unique(np.sort(t[:,[[0,1],[1,2],[2,0]]].reshape(-1,2),axis=1),axis=0)
    bary=np.array([[i/subdivisions,j/subdivisions,1-(i+j)/subdivisions] for i in range(subdivisions+1) for j in range(subdivisions+1-i)])
    samples=np.r_[p,np.einsum('ki,fij->fkj',bary,p[t]).reshape(-1,3),p[t].mean(1)]
    _,keep=np.unique(samples.round(8),axis=0,return_index=True)
    return samples[keep]


def crest_heights(p,t,s_values,half_width=6.):
    """Exact maximum of original triangle sections inside the genital strip."""
    tri=p[t];result=[]
    for s in s_values:
        heights=[];edge_hits=[];edge_valid=[]
        for i,j in ((0,1),(1,2),(2,0)):
            a,b=tri[:,i],tri[:,j];span=b[:,1]-a[:,1]
            selected=((a[:,1]-s)*(b[:,1]-s)<=1e-9)&(abs(span)>1e-12)
            hit=np.zeros_like(a);f=np.zeros(len(a));f[selected]=(s-a[selected,1])/span[selected];hit[selected]=a[selected]+f[selected,None]*(b[selected]-a[selected])
            heights.extend(hit[selected&(abs(hit[:,0])<=half_width+1e-8),2]);edge_hits.append(hit);edge_valid.append(selected)
        for i,j in ((0,1),(1,2),(2,0)):
            a,b=edge_hits[i],edge_hits[j];span=b[:,0]-a[:,0]
            for x in (-half_width,half_width):
                selected=edge_valid[i]&edge_valid[j]&((a[:,0]-x)*(b[:,0]-x)<=0)&(abs(span)>1e-12)
                f=(x-a[selected,0])/span[selected];heights.extend(a[selected,2]+f*(b[selected,2]-a[selected,2]))
        on_plane=tri.reshape(-1,3);heights.extend(on_plane[(abs(on_plane[:,1]-s)<1e-9)&(abs(on_plane[:,0])<=half_width),2])
        if not heights:raise ValueError('No original crest section at requested longitudinal location')
        result.append(max(heights))
    return np.array(result)


def overlay_samples(p,t,uv,extra_queries=None):
    """Vertices of the original/retained-chart piecewise-linear overlay.

    Height difference is affine within each overlay cell. Checking original
    vertices, retained-chart vertices and every crossing edge checks the full
    triangle surfaces, including multiple inward-facing original layers.
    """
    tri=p[t];centre=tri[:,:,:2].mean(1);radius=np.linalg.norm(tri[:,:,:2]-centre[:,None],axis=-1).max(1);tree=cKDTree(centre);extra=[]
    queries=uv if extra_queries is None else np.r_[uv,extra_queries]
    for start in range(0,len(queries),256):
        q=queries[start:start+256];near=tree.query_ball_point(q,radius.max()+1e-7);ri=np.repeat(np.arange(len(q)),[len(x) for x in near]);fi=np.concatenate(near).astype(int)
        a=tri[fi,1,:2]-tri[fi,0,:2];b=tri[fi,2,:2]-tri[fi,0,:2];d=q[ri]-tri[fi,0,:2]
        det=a[:,0]*b[:,1]-a[:,1]*b[:,0];safe=np.where(abs(det)>1e-12,det,1.)
        x=(d[:,0]*b[:,1]-d[:,1]*b[:,0])/safe;y=(a[:,0]*d[:,1]-a[:,1]*d[:,0])/safe;bc=np.c_[1-x-y,x,y]
        valid=(abs(det)>1e-12)&(bc.min(1)>=-1e-8);height=np.einsum('ni,ni->n',bc,tri[fi,:,2]);highest=np.full(len(q),-np.inf);np.maximum.at(highest,ri[valid],height[valid]);found=np.isfinite(highest)
        extra.append(np.c_[q[found],highest[found]])
    edges=np.unique(np.sort(t[:,[[0,1],[1,2],[2,0]]].reshape(-1,2),axis=1),axis=0)
    original=p[edges];new=uv[edges];centre=new.mean(1);radius=np.linalg.norm(new[:,1]-new[:,0],axis=1)/2;tree=cKDTree(centre)
    def cross(a,b):return a[:,0]*b[:,1]-a[:,1]*b[:,0]
    for start in range(0,len(edges),256):
        old=original[start:start+256];c=old[:,:,:2].mean(1);r=np.linalg.norm(old[:,1,:2]-old[:,0,:2],axis=1)/2
        near=tree.query_ball_point(c,r+radius.max()+1e-7);ia=np.repeat(np.arange(len(old)),[len(x) for x in near]);ib=np.concatenate(near).astype(int)
        a=old[ia,1,:2]-old[ia,0,:2];b=new[ib,1]-new[ib,0];delta=new[ib,0]-old[ia,0,:2];det=cross(a,b);safe=np.where(abs(det)>1e-12,det,1.)
        u=cross(delta,b)/safe;v=cross(delta,a)/safe;keep=(abs(det)>1e-12)&(u>=-1e-9)&(u<=1+1e-9)&(v>=-1e-9)&(v<=1+1e-9)
        extra.append(old[ia[keep],0]+u[keep,None]*(old[ia[keep],1]-old[ia[keep],0]))
    points=np.r_[p,np.concatenate(extra)];_,keep=np.unique(points.round(8),axis=0,return_index=True)
    return points[keep]


def qp_cover(P,q,A,b,initial,tolerance=.002,maxiter=12000):
    """Sparse ADMM quadratic solve with linear lower coverage constraints."""
    scale=1/np.sqrt(np.maximum(P.diagonal(),1e-15));S=diags(scale);P=(S@P@S).tocsc();q=scale*q
    A=(A@S).tocsr();row_scale=np.sqrt(np.asarray(A.power(2).sum(1)).ravel());row_scale=np.maximum(row_scale,1e-15)
    A=diags(1/row_scale)@A;b=b/row_scale
    rho=1.;sigma=1e-8;AT=A.T.tocsc();ATA=(AT@A).tocsc();y=initial/scale;z=np.maximum(A@y,b);u=np.zeros(len(b));stable=0
    def factor():return splu(P+rho*ATA+sigma*eye(len(q),format='csc'))
    lu=factor();last_step=np.inf;gap=np.inf
    for iteration in range(maxiter):
        previous=y.copy();old_z=z.copy();y=lu.solve(sigma*previous-q+rho*(AT@(z-u)));ay=A@y
        relaxed=1.6*ay-.6*old_z;z=np.maximum(relaxed+u,b);u+=relaxed-z
        primal=ay-z;dual=rho*(AT@(z-old_z));last_step=float(np.max(abs((y-previous)*scale)))
        gap=float(np.maximum((b-ay)*row_scale,0).max(initial=0))
        stable=stable+1 if gap<tolerance and last_step<.0005 else 0
        if stable>=10:break
        if iteration%100==99:
            r=float(np.max(abs(primal),initial=0));s=float(np.max(abs(dual),initial=0));old_rho=rho
            if r>10*s:rho=min(rho*2,1e5)
            elif s>10*r:rho=max(rho/2,1e-5)
            if old_rho!=rho:u*=old_rho/rho;lu=factor()
    return y*scale,dict(converged=stable>=10,iterations=iteration+1,maximumCoverageViolationMm=gap,maximumLastStepMm=last_step,rho=rho)


def diagnostics():
    d=np.load('outputs/af-fitting-genital-v3/af-fitting-reference.npz');ids=d['genitalPatchVertex'];t=d['triangles'][d['genitalPatchFace']]
    remap=np.full(len(d['position']),-1,int);remap[ids]=np.arange(len(ids));lt=remap[t];uv=d['genitalChart'];local=(d['parentPosition'][ids]-d['genitalChartOrigin'])@d['genitalChartFrame']
    points=source_samples(local,lt);A,missing=coverage_rows(uv,lt,points[:,:2]);h=((d['position'][ids]-d['genitalChartOrigin'])@d['genitalChartFrame'])[:,2]
    fixed=remap[d['genitalFixedVertex']];free=np.setdiff1d(np.arange(len(ids)),fixed);fraction=np.asarray(A[:,free].sum(1)).ravel()
    supported=np.ones(len(points),bool);supported[missing]=False
    violation=points[:,2]-A@h
    print(dict(samples=len(points),outsideChart=len(missing),uncoveredSupportedSamples=int(((violation>1e-4)&supported).sum()),worstUnderCoverageMm=float(violation[supported].max()),
        fixedOnlyUncovered=int(((fraction<1e-8)&(violation>1e-4)&supported).sum()),maximumFixedOnlyViolationMm=float(violation[(fraction<1e-8)&supported].max(initial=0))))
    np.savez_compressed('outputs/af-fitting-genital-v3/coverage-diagnostics.npz',points=points,missing=missing,currentGap=(A@h-points[:,2]),freeWeight=fraction)


def bending(uv,t):
    p=uv[t];a=p[:,1]-p[:,0];b=p[:,2]-p[:,0];double=abs(a[:,0]*b[:,1]-a[:,1]*b[:,0]);rows=[];cols=[];vals=[];mass=np.zeros(len(uv))
    for k in range(3):
        i,j=(k+1)%3,(k+2)%3;u=p[:,i]-p[:,k];v=p[:,j]-p[:,k];cot=np.sum(u*v,axis=1)/double/2
        rows.extend((t[:,i],t[:,j],t[:,i],t[:,j]));cols.extend((t[:,i],t[:,j],t[:,j],t[:,i]));vals.extend((cot,cot,-cot,-cot));np.add.at(mass,t[:,k],double/6)
    C=coo_matrix((np.concatenate(vals),(np.concatenate(rows),np.concatenate(cols))),shape=(len(uv),len(uv))).tocsr()
    return C.T@diags(1/mass)@C,mass


def hidden_peak_mask(points):
    """Small covered anterior detail; the user permits underlying skin clipping."""
    return (abs(points[:,0])<4.5)&(points[:,1]>10)&(points[:,1]<32)


def exemption_boundary_queries(original_uv,uv,t):
    """Add the exemption rectangle to the coverage partition, not just its corners."""
    edges=np.unique(np.sort(t[:,[[0,1],[1,2],[2,0]]].reshape(-1,2),axis=1),axis=0)
    points=[np.array([[-4.5,10],[-4.5,32],[4.5,10],[4.5,32]])]
    for q in (original_uv,uv):
        a,b=q[edges[:,0]],q[edges[:,1]]
        for axis,value in ((0,-4.5),(0,4.5),(1,10),(1,32)):
            span=b[:,axis]-a[:,axis];keep=((a[:,axis]-value)*(b[:,axis]-value)<=0)&(abs(span)>1e-12)
            f=(value-a[keep,axis])/span[keep];hit=a[keep]+f[:,None]*(b[keep]-a[keep]);hit[:,axis]=value;points.append(hit)
    return np.concatenate(points)


def run(args):
    if args.clearance<0 or args.crest_allowance<args.clearance or args.fairing<=0:raise ValueError('Require nonnegative coverage clearance, crest allowance >= clearance, and positive fairing length')
    source=Path('outputs/af-fitting-genital-v3');out=Path(args.output);out.mkdir(parents=True,exist_ok=True)
    if out.resolve()==source.resolve() or (args.ignore_peak and out.resolve()==Path('outputs/af-fitting-genital-v4').resolve()):raise ValueError('Use a separate output; prior fitting copies are immutable inputs')
    source_hash=hashlib.sha256((source/'af-fitting-reference.npz').read_bytes()).hexdigest()
    d=dict(np.load(source/'af-fitting-reference.npz'));v=d['parentPosition'];previous=d['position'];t=d['triangles'];ids=d['genitalPatchVertex'];faces=d['genitalPatchFace'];frame=d['genitalChartFrame'];origin=d['genitalChartOrigin']
    if args.ignore_peak:previous=np.load('outputs/af-fitting-genital-v4/af-fitting-reference.npz')['position']
    remap=np.full(len(v),-1,int);remap[ids]=np.arange(len(ids));lt=remap[t[faces]];fixed=remap[d['genitalFixedVertex']];free=np.setdiff1d(np.arange(len(ids)),fixed);uv=d['genitalChart']
    local=(v-origin)@frame;old_local=(previous[ids]-origin)@frame;samples=source_samples(local[ids],lt)
    A,missing=coverage_rows(uv,lt,samples[:,:2])
    if len(missing):raise ValueError('Original surface samples extend outside the fixed chart')
    fraction=np.asarray(A[:,free].sum(1)).ravel();exempt=hidden_peak_mask(samples) if args.ignore_peak else np.zeros(len(samples),bool);active=(fraction>1e-7)&~exempt
    allowance=args.clearance*fraction
    lower=samples[:,2]+allowance-np.asarray(A[:,fixed]@local[ids[fixed],2]).ravel()
    if np.max(lower[(fraction<=1e-7)&~exempt],initial=0)>1e-6:raise ValueError('Fixed collar cannot cover original geometry')
    n=normals(v,t)@frame;allowed=(v[:,1]>835)&(v[:,1]<950)&(v[:,2]>-14)&(v[:,2]<105);n[~allowed,2]=-1
    mass=np.zeros(len(v))
    for k in range(3):np.add.at(mass,t[:,k],areas(v[t])/3)
    model,donors,_=side_slope_model(local,n,mass,transverse_curvature=False);flat=model(uv)
    H,m=bending(uv,lt);Q=args.fairing**4*H+diags(m)
    P=Q[free][:,free].tocsc();linear=np.asarray(Q[free][:,fixed]@local[ids[fixed],2]-Q[free]@flat).ravel()
    central=np.flatnonzero(abs(uv[free,0])<=5.)
    crest=crest_heights(local[ids],lt,uv[free[central],1])
    cap=crest+args.crest_allowance
    upper=coo_matrix((-np.ones(len(central)),(np.arange(len(central)),central)),shape=(len(central),len(free))).tocsr()
    constraints=vstack((A[active][:,free],upper),format='csr');bounds=np.r_[lower[active],-cap]
    feasible=linprog(np.zeros(len(free)),A_ub=-constraints,b_ub=-bounds,bounds=[(None,None)]*len(free),method='highs')
    if not feasible.success:raise ValueError('Coverage and crest limits are incompatible on the retained triangles: '+feasible.message)
    x,solver=qp_cover(P,linear,constraints,bounds,old_local[free,2])
    if not solver['converged']:raise ValueError('Tight coverage solve did not converge: '+str(solver))
    # Nonnegative barycentric rows permit a monotone roundoff cleanup: raising
    # a free height cannot violate another lower coverage constraint.
    B=A[active][:,free].tocsr();b=lower[active];unprojected=x.copy()
    for _ in range(6):
        violation=b-B@x
        if violation.max(initial=0)<1e-9:break
        for i in np.flatnonzero(violation>1e-9):
            a0,a1=B.indptr[i:i+2];col=B.indices[a0:a1];w=B.data[a0:a1];error=b[i]-w@x[col]
            if error>0:x[col]+=error*w/(w@w)
    height=local[ids,2].copy();height[free]=x
    q=v.copy();target=np.column_stack((uv,height));q[ids[free]]=target[free]@frame.T+origin
    # Topology/chart and all previously protected vertices remain exact.
    changed=np.linalg.norm(q-v,axis=1)>1e-9;np.testing.assert_array_equal(q[~d['genitalModifiedVertex']],v[~d['genitalModifiedVertex']])
    oldpairs=crossing_pairs(v[t],v[t],self_faces=t);pairs=crossing_pairs(q[t],q[t],self_faces=t)
    if set(map(tuple,pairs))-set(map(tuple,oldpairs)):raise ValueError('Tighter envelope introduced intersections')
    affected=changed[t].any(1)
    if affected[pairs].any():raise ValueError('Tighter envelope has local intersections')
    final_gap=np.asarray(A@height).ravel()-samples[:,2]
    extra=exemption_boundary_queries(local[ids,:2],uv,lt) if args.ignore_peak else None
    overlay=overlay_samples(local[ids],lt,uv,extra);OA,omissing=coverage_rows(uv,lt,overlay[:,:2]);overlay_gap=np.asarray(OA@height).ravel()-overlay[:,2]
    overlay_exempt=hidden_peak_mask(overlay) if args.ignore_peak else np.zeros(len(overlay),bool)
    if len(omissing) or overlay_gap[~overlay_exempt].min()<-1e-6:raise ValueError('Triangle overlay verification found uncovered required geometry')
    dense_mask=active&(fraction>.8)
    report=dict(role='Smooth genital curve with permitted hidden clipping' if args.ignore_peak else 'Flatter close-cover genital fitting envelope',source=(source/'af-fitting-reference.npz').as_posix(),sourceSha256=source_hash,clearanceMm=args.clearance,fairingLengthMm=args.fairing,solver=solver,
        centralCrestAllowanceMm=args.crest_allowance,centralCrestBandHalfWidthMm=5,sourceCrestBandHalfWidthMm=6,maximumCentralCrestExcessMm=float(np.max(x[central]-crest)),
        originalSurfaceCoverageSamples=len(samples),minimumCoverageGapMm=float(final_gap.min()),allSourceLayerGapP50Mm=float(np.median(final_gap[dense_mask])),allSourceLayerGapP95Mm=float(np.percentile(final_gap[dense_mask],95)),
        triangleOverlayCoverageSamples=len(overlay),minimumTriangleOverlayGapMm=float(overlay_gap.min()),triangleOverlayCoverageVerified=not args.ignore_peak,requiredTriangleOverlayCoverageVerified=True,
        hiddenPeakClippingAllowed=args.ignore_peak,exemptedCoverageSamples=int(exempt.sum()),minimumRequiredOverlayGapMm=float(overlay_gap[~overlay_exempt].min()),
        maximumAllowedHiddenClipDepthMm=float(np.maximum(-overlay_gap[overlay_exempt],0).max(initial=0)),
        allowanceTapersToZeroAtFixedCollar=True,maximumRoundoffCorrectionMm=float(abs(x-unprojected).max()),
        previousLowestEditedYmm=float(previous[d['genitalModifiedVertex'],1].min()),revisedLowestEditedYmm=float(q[changed,1].min()),
        maximumChangeFromPreviousMm=float(np.linalg.norm(q-previous,axis=1).max()),localIntersections=0,globalIntersectionPairs=len(pairs),removedOriginalIntersectionPairs=len(oldpairs)-len(pairs),
        topologyUVsAndWeightsUnchanged=True,acceptedBreastsButtAndThighsUnchanged=True,normalMapApplied=False,
        scope='Original geometry constrains the retained fitting sheet except the small covered anterior protrusion when explicitly exempted. Fixed collar and chart coordinates remain exact; no new self-intersections are permitted.')
    output=dict(d);output.update(position=q,normal=normals(q,t)[t],genitalModifiedVertex=changed,modifiedVertex=d['modifiedVertex']|changed,previousGenitalPosition=previous)
    np.savez_compressed(out/'af-fitting-reference.npz',**output);write_obj(out/'AF-Fitting-Small-MediumB.obj',q,t,output['normal'],d['uv'],d['group'])
    shutil.copyfile(source/'AF-Fitting.points.json',out/'AF-Fitting.points.json');names,face,bary=read_points(out/'AF-Fitting.points.json')
    np.savez_compressed(out/'af-fitting-landmarks.npz',names=np.array(names),face=face,bary=bary,position=np.einsum('ni,nij->nj',bary,q[t[face]]),parentPosition=np.einsum('ni,nij->nj',bary,v[t[face]]),sourcePosition=np.einsum('ni,nij->nj',bary,d['sourcePosition'][t[face]]))
    shutil.copyfile(source/'genital-diagnostics.npz',out/'genital-diagnostics.npz');np.savez_compressed(out/'coverage-audit.npz',samples=samples,finalGap=final_gap,previousGap=np.asarray(A@old_local[:,2]).ravel()-samples[:,2],freeWeight=fraction)
    np.savez_compressed(out/'overlay-coverage-audit.npz',points=overlay,gap=overlay_gap)
    (out/'af-tight-cover-report.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report,indent=2),flush=True)
    assert hashlib.sha256((source/'af-fitting-reference.npz').read_bytes()).hexdigest()==source_hash
    if not args.no_manifest:
        manifest_path=Path('af-fitting-mesh.json');archive=Path('af-fitting-mesh-genital-v4.json' if args.ignore_peak else 'af-fitting-mesh-genital-v3.json')
        if not archive.exists():archive.write_bytes(manifest_path.read_bytes())
        manifest=json.loads(manifest_path.read_text());manifest.update(stage='Accepted breast v2 plus flatter close-cover genital envelope',status='Lower-body revision prepared for review',report=(out/'af-tight-cover-report.json').as_posix())
        if args.ignore_peak:manifest['stage']='Accepted breast v2 plus smooth genital curve; hidden anterior clipping allowed'
        manifest['artifacts']={p.name:dict(path=p.as_posix(),sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in [out/'af-fitting-reference.npz',out/'AF-Fitting-Small-MediumB.obj',out/'AF-Fitting.points.json',out/'af-fitting-landmarks.npz']}
        manifest_path.write_text(json.dumps(manifest,indent=2)+'\n')


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--output',default='outputs/af-fitting-genital-v4');p.add_argument('--clearance',type=float,default=.1);p.add_argument('--crest-allowance',type=float,default=.35);p.add_argument('--fairing',type=float,default=6.);p.add_argument('--ignore-peak',action='store_true');p.add_argument('--no-manifest',action='store_true');p.add_argument('--diagnostics',action='store_true');args=p.parse_args()
    if args.ignore_peak and args.output=='outputs/af-fitting-genital-v4':args.output='outputs/af-fitting-genital-v5'
    diagnostics() if args.diagnostics else run(args)
