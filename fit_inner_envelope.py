"""Average local surface curves, then fit an inward envelope anchored to skin."""
import argparse,hashlib,json,time
from pathlib import Path
import numpy as np
from scipy.sparse import diags
from scipy.sparse.csgraph import dijkstra
from scipy.sparse.linalg import spsolve
from scipy.optimize import minimize
from clean_reference import graph,normals
from separate_body_shape import surface_operator
from reference_surface import unit,write_obj
from repair_surface import boundaries
from transfer_landmarks import write_points


def quadratic_intercept(q,weights):
    x,y,z=q.T
    design=np.c_[np.ones(len(x)),x,y,x*x,x*y,y*y]
    root=np.sqrt(weights)
    coef,_,rank,_=np.linalg.lstsq(design*root[:,None],z*root,rcond=1e-8)
    if rank<6:return 0.,np.zeros(len(q))
    return coef[0],z-design@coef


def averaged_curve(v,t,groups,radius):
    average,metric=graph(v,t);direction=normals(v,t)
    # Stabilise the fitting frame only; do not smooth the vertex positions.
    for _ in range(12):direction=unit(.5*direction+.5*(average@direction))
    mass,lap=surface_operator(v,t)
    cloth=np.zeros(len(v),bool);skin=cloth.copy();top=cloth.copy()
    cloth[t[np.char.find(groups,'smallclothes')>=0]]=True
    skin[t[np.char.endswith(groups,'_skin')]]=True
    top[t[groups=='top_smallclothes']]=True
    distance=dijkstra(metric,directed=False,indices=np.flatnonzero(cloth),min_only=True)
    # The skin collar supplies positional and directional continuation data.
    ids=np.flatnonzero(distance<radius);delta=np.zeros(len(v));residual=np.zeros(len(v))
    start=time.time()
    for count,i in enumerate(ids):
        rr=radius
        if top[i] and v[i,2]>40:rr=min(rr,8.+.7*abs(v[i,0]))
        if not top[i] and v[i,2]<-35:rr=min(rr,10.+.8*abs(v[i,0]))
        dd=dijkstra(metric,directed=False,indices=int(i),limit=rr)
        near=(dd<rr)&((direction@direction[i])>.6)
        if (top[i] and v[i,2]>40) or (not top[i] and v[i,2]<-35):
            if abs(v[i,0])>1e-5:near&=v[:,0]*v[i,0]>=-1e-6
            else:near&=abs(v[:,0])<6.
        jj=np.flatnonzero(near)
        if len(jj)<12:continue
        axis=np.array([0.,1.,0.]) if abs(direction[i,1])<.9 else np.array([1.,0.,0.])
        u=unit(np.cross(direction[i],axis));w=np.cross(direction[i],u)
        q=(v[jj]-v[i])@np.array([u,w,direction[i]]).T/rr
        weights=mass[jj]*np.exp(-(dd[jj]/(.48*rr))**2)
        intercept,res=quadratic_intercept(q,weights)
        # Downweight exceptional seam/fold excursions. Broad curvature stays
        # in the quadratic model rather than being averaged towards a plane.
        for _ in range(2):
            scale=max(.15/rr,1.4826*np.median(abs(res-np.median(res))))
            robust=np.minimum(1.,1.5*scale/np.maximum(abs(res),1e-20))
            intercept,res=quadratic_intercept(q,weights*robust)
        delta[i]=intercept*rr;residual[i]=np.sqrt(np.average(res**2,weights=weights))*rr
        if count%1000==0:print('curve samples',count,'/',len(ids),'seconds',round(time.time()-start,1),flush=True)
    return delta,direction,mass,lap,cloth,skin,residual


def orientation_penalty(v,t,direction,d,weight=1000.,floor=.2):
    p=v[t];cross=np.cross(p[:,1]-p[:,0],p[:,2]-p[:,0]);double=np.linalg.norm(cross,axis=1);rest=cross/double[:,None]
    q=(v+d[:,None]*direction)[t]
    ratio=np.sum(np.cross(q[:,1]-q[:,0],q[:,2]-q[:,0])*rest,axis=1)/double
    gap=np.minimum(ratio-floor,0.);energy=.5*weight*np.sum(.5*double*gap**2)
    g1=np.cross(q[:,2]-q[:,0],rest);g2=np.cross(rest,q[:,1]-q[:,0]);gradient=np.zeros(len(v))
    for k,g in enumerate((-g1-g2,g1,g2)):
        np.add.at(gradient,t[:,k],.5*weight*gap*np.sum(g*direction[t[:,k]],axis=1))
    return energy,gradient


def constrained_offset(target,mass,lap,free,length,geometry=None):
    """Minimise a smooth correction to the averaged curve, with d <= 0.

    d is displacement from raw, so fixed skin has d=0. The correction
    d-target is continued through the fixed exterior collar. Its gradient
    and bending energies prevent independent clamping from copying folds.
    """
    m=diags(mass);q=(m+length**2*lap+length**4*(lap@diags(1/mass)@lap)).tocsr()
    ids=np.flatnonzero(free);a=q[ids][:,ids].tocsc();rhs=(q@target)[ids]
    diagonal=np.sqrt(a.diagonal());b=rhs/diagonal
    def objective(z):
        d=z/diagonal;ad=(a@d)/diagonal
        value=.5*z@ad-b@z;gradient=ad-b
        if geometry is not None:
            full=np.zeros(len(target));full[ids]=d
            v,t,n=geometry;penalty,g=orientation_penalty(v,t,n,full)
            value+=penalty;gradient+=g[ids]/diagonal
        return value,gradient
    unconstrained=spsolve(a,rhs);initial=np.minimum(unconstrained,0)*diagonal
    fit=minimize(objective,initial,jac=True,bounds=[(None,0.)]*len(ids),method='L-BFGS-B',
                 options=dict(maxiter=4000,ftol=1e-13,gtol=1e-8,maxcor=30,maxls=40))
    d=np.zeros(len(target));d[ids]=fit.x/diagonal
    gradient=objective(fit.x)[1]*diagonal
    projected=np.where(d[ids]<-1e-7,gradient,np.maximum(gradient,0))
    relative=float(np.linalg.norm(projected)/max(np.linalg.norm(rhs),1.))
    print('offset solve',fit.message,'iterations',fit.nit,'KKT relative',relative,flush=True)
    if not fit.success or relative>1e-4:raise ValueError('Envelope bound solve did not converge')
    return d,dict(iterations=int(fit.nit),message=str(fit.message),relativeProjectedGradient=relative)


def run(args):
    source=Path(args.raw);out=Path(args.output);out.mkdir(parents=True,exist_ok=True)
    path=source/'body-fit-reference.npz';raw=dict(np.load(path));v,t=raw['position'],raw['triangles']
    cache=out/'curve-fit.npz'
    sha=hashlib.sha256(path.read_bytes()).hexdigest()
    if cache.exists():
        c=np.load(cache)
        if str(c['sourceSha'])!=sha or float(c['radius'])!=args.radius:raise ValueError('Curve cache input mismatch')
        target,direction,cloth,skin,residual=[c[k] for k in ('target','direction','cloth','skin','residual')]
        mass,lap=surface_operator(v,t)
    else:
        target,direction,mass,lap,cloth,skin,residual=averaged_curve(v,t,raw['group'],args.radius)
        np.savez_compressed(cache,target=target,direction=direction,cloth=cloth,skin=skin,residual=residual,sourceSha=sha,radius=args.radius)
    # The material edge can itself contain a raised hem. Anchor to clean skin
    # beyond a narrow geodesic collar, allowing that edge to join the average.
    _,metric=graph(v,t)
    distance=dijkstra(metric,directed=False,indices=np.flatnonzero(cloth),min_only=True)
    free=cloth|(distance<args.skin_collar)
    fixed=~free
    relevant=np.any(free[t],axis=1)
    if args.curvature_weight>0:
        from fit_curvature_envelope import solve_curvature
        initial=None;history=None
        if args.initial:
            previous=np.load(Path(args.initial)/'body-fit-reference.npz')
            np.testing.assert_array_equal(previous['rawPosition'],v)
            initial=previous['envelopeNormalShiftMm']
            previous_report=Path(args.initial)/'envelope-report.json'
            if previous_report.exists():
                previous_solve=json.loads(previous_report.read_text())['solver']
                if previous_solve.get('curvatureWeight')==args.curvature_weight and previous_solve.get('curvatureRadiusMm')==args.curvature_radius:
                    history=previous_solve['iterations']
        d,solve=solve_curvature(target,mass,lap,free,args.continuation,(v,t,direction),raw['group'],
            args.curvature_weight,args.curvature_radius,args.curvature_iterations,initial,history)
    else:d,solve=constrained_offset(target,mass,lap,free,args.continuation,(v,t[relevant],direction))
    p=v+d[:,None]*direction
    a=np.cross(v[t[:,1]]-v[t[:,0]],v[t[:,2]]-v[t[:,0]])
    b=np.cross(p[t[:,1]]-p[t[:,0]],p[t[:,2]]-p[t[:,0]])
    ratio=np.sum(a*b,axis=-1)/np.sum(a*a,axis=-1)
    if ratio.min()<=.05:raise ValueError('Envelope contains a reversed or collapsed triangle')
    np.testing.assert_array_equal(p[fixed],v[fixed]);assert d.max()<=1e-10
    result=dict(raw);result.update(position=p,normal=normals(p,t)[t],rawPosition=v,averagedCurvePosition=v+target[:,None]*direction,
        envelopeDirection=direction,envelopeNormalShiftMm=d,curveFitResidualMm=residual,
        curveFitRadiusMm=np.array(args.radius),continuationLengthMm=np.array(args.continuation),fixedSkin=fixed,
        skinCollarMm=np.array(args.skin_collar),clothVertex=cloth,curvatureWeight=np.array(args.curvature_weight))
    np.savez_compressed(out/'body-fit-reference.npz',**result)
    write_obj(out/'vanilla-body-fit.obj',p,t,result['normal'],raw['uv'],raw['group'])
    lm=dict(np.load(source/'paired-landmarks-candidate.npz'))
    lm['sourcePosition']=np.einsum('ni,nij->nj',lm['sourceBary'],p[t[lm['sourceFace']]])
    np.savez_compressed(out/'paired-landmarks-candidate.npz',**lm)
    write_points(out/'Vanilla-BodyFit.points.json',lm['names'],lm['sourceFace'],lm['sourceBary'])
    report=dict(sourceSha256=sha,curveRadiusMm=args.radius,continuationLengthMm=args.continuation,
        explicitThicknessAllowanceMm=0,skinCollarMm=args.skin_collar,vertices=len(v),triangles=len(t),fixedSkinVertices=int(fixed.sum()),freeVertices=int(free.sum()),
        minimumProjectedAreaRatio=float(ratio.min()),connectivity=boundaries(t),solver=solve,
        clothDisplacementMm=dict(min=float(d[cloth].min()),mean=float(np.average(d[cloth],weights=mass[cloth])),max=float(d[cloth].max())),
        minimumSkinCollarDisplacementMm=float(d[skin&free].min()),
        activeObstacleVertices=int(np.sum(free&(d>-1e-6))),landmarks=len(lm['names']),
        scope=('Final-surface quadratic-residual fairness with inward bounds' if args.curvature_weight>0 else 'Local robust quadratic average plus a bounded smooth correction')+' continued from exposed skin outside a narrow seam collar. Same raw topology/UVs/weights/point addresses. Nonpositive vertex-normal displacement is not a proof of global containment. Hidden anatomy is estimated; inspect geometric audits and profiles.')
    (out/'envelope-report.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report,indent=2),flush=True)


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--raw',default='outputs/body-cleanup-4x-raw')
    p.add_argument('--output',default='outputs/body-inner-envelope-4x-collar12');p.add_argument('--radius',type=float,default=30.)
    p.add_argument('--continuation',type=float,default=12.);p.add_argument('--skin-collar',type=float,default=12.)
    p.add_argument('--curvature-weight',type=float,default=0.);p.add_argument('--curvature-radius',type=float,default=25.)
    p.add_argument('--curvature-iterations',type=int,default=8);p.add_argument('--initial');a=p.parse_args()
    if a.radius<=0 or a.continuation<=0 or a.skin_collar<=0 or a.skin_collar>=a.radius:p.error('Positive lengths required, with skin collar smaller than curve radius')
    if a.curvature_weight<0 or a.curvature_radius<=0 or a.curvature_iterations<1:p.error('Invalid curvature controls')
    run(a)
