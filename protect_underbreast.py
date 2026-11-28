"""Protect broad lower-breast curvature while filtering fine fabric relief."""
import argparse,json,hashlib
from pathlib import Path
import numpy as np
from scipy.sparse import coo_matrix,diags
from scipy.sparse.csgraph import dijkstra
from scipy.optimize import minimize
from fit_curvature_envelope import curvature_operator
from fit_inner_envelope import orientation_penalty
from clean_reference import graph,normals
from separate_body_shape import surface_operator
from reference_surface import unit,write_obj
from transfer_landmarks import write_points
from repair_surface import boundaries


def smooth_gate(x,outer_lo,inner_lo,inner_hi,outer_hi):
    a=np.clip((x-outer_lo)/(inner_lo-outer_lo),0,1)
    b=np.clip((outer_hi-x)/(outer_hi-inner_hi),0,1)
    return (a*a*(3-2*a))*(b*b*(3-2*b))


def broad_operator(v,t,n,mass,free,protection,radius=30.):
    """Preserve robust broad slope/quadratic coefficients; allow height inset.

    Coefficients use a fixed raw tangent chart. Omitting the constant term
    allows garment thickness removal while penalising changes to broad shape.
    """
    _,metric=graph(v,t);rows=[];cols=[];values=[];weights=[]
    for i in np.flatnonzero(free&(protection>0)):
        dd=dijkstra(metric,directed=False,indices=int(i),limit=radius)
        near=(dd<radius)&((n@n[i])>.6)&(v[:,0]*v[i,0]>=0)
        ids=np.flatnonzero(near)
        if len(ids)<12:continue
        axis=np.array([0.,1.,0.]) if abs(n[i,1])<.9 else np.array([1.,0.,0.])
        u=unit(np.cross(n[i],axis));w=np.cross(n[i],u)
        q=(v[ids]-v[i])/radius;x,y,z=q@u,q@w,q@n[i]
        design=np.c_[np.ones(len(x)),x,y,x*x,x*y,y*y]
        base=mass[ids]*np.exp(-(dd[ids]/(.48*radius))**2);robust=np.ones(len(ids))
        for _ in range(3):
            root=np.sqrt(base*robust);weighted=design*root[:,None]
            if np.linalg.matrix_rank(weighted,tol=1e-8)<6:break
            inverse=np.linalg.pinv(weighted,rcond=1e-8)*root[None]
            residual=z-design@(inverse@z)
            scale=max(.15/radius,1.4826*np.median(abs(residual-np.median(residual))))
            robust=np.minimum(1.,1.5*scale/np.maximum(abs(residual),1e-20))
        else:
            for k in range(1,6):
                row=len(weights);rows.extend([row]*len(ids));cols.extend(ids)
                values.extend(inverse[k]*(n[ids]@n[i]));weights.append(mass[i]*protection[i]/5)
    return coo_matrix((values,(rows,cols)),shape=(len(weights),len(v))).tocsr(),np.array(weights)


def run(args):
    raw_path=Path(args.raw)/'body-fit-reference.npz';current_path=Path(args.current)/'body-fit-reference.npz'
    raw=dict(np.load(raw_path));current=dict(np.load(current_path));v,t=raw['position'],raw['triangles']
    np.testing.assert_array_equal(current['rawPosition'],v)
    for k in ('triangles','uv','weights','boneNames','group'):np.testing.assert_array_equal(raw[k],current[k])
    n=current['envelopeDirection'];mass,lap=surface_operator(v,t)
    protection=smooth_gate(abs(v[:,0]),20,30,100,120)*smooth_gate(v[:,1],1165,1180,1220,1240)*np.clip((v[:,2]-40)/15,0,1)
    free=(protection>0)&~current['fixedSkin'];ids=np.flatnonzero(free)
    broad,bw=broad_operator(v,t,n,mass,free,protection)
    bj=broad[:,ids];bwt=args.broad_weight*bw
    # Changes taper back to the current version at the patch perimeter.
    q=(diags(mass)+16*lap).tocsr()[ids][:,ids]
    start=current['envelopeNormalShiftMm'].copy();d=start.copy();records=[];relevant=t[np.any(free[t],axis=1)]
    for iteration in range(args.iterations):
        fine,base,fw,row_ids=curvature_operator(v,v+d[:,None]*n,n,t,raw['group'],mass,free,args.fine_radius)
        fj=fine[:,ids];fwt=args.fine_weight*fw
        fine_base=base+fine@start;broad_base=broad@start
        diagonal=np.sqrt(q.diagonal()+np.asarray(fj.power(2).T@fwt+bj.power(2).T@bwt).ravel())
        def objective(z):
            change=z/diagonal;fr=fine_base+fj@change;br=broad_base+bj@change
            value=.5*change@(q@change)+.5*np.sum(fwt*fr**2)+.5*np.sum(bwt*br**2)
            gradient=q@change+fj.T@(fwt*fr)+bj.T@(bwt*br)
            absolute=start.copy();absolute[ids]+=change
            penalty,g=orientation_penalty(v,relevant,n,absolute,weight=10000.)
            return float(value+penalty),(gradient+g[ids])/diagonal
        upper=-start[ids]*diagonal
        fit=minimize(objective,(d[ids]-start[ids])*diagonal,jac=True,method='L-BFGS-B',bounds=list(zip(np.full(len(ids),-np.inf),upper)),
            options=dict(maxiter=3000,ftol=1e-13,gtol=1e-8,maxcor=30,maxls=40))
        previous=d.copy();d[ids]=start[ids]+fit.x/diagonal
        gradient=objective(fit.x)[1]*diagonal
        projected=np.where(d[ids]<-1e-7,gradient,np.maximum(gradient,0))
        normalizer=max(float(np.linalg.norm(fj.T@(fwt*fine_base))+np.linalg.norm(bj.T@(bwt*broad_base))),1.)
        relative=float(np.linalg.norm(projected)/normalizer)
        record=dict(iteration=iteration+1,optimizerIterations=int(fit.nit),relativeProjectedGradient=relative,
            maximumStepMm=float(abs(d-previous).max()),fineResidualRmsMm=float(np.sqrt(np.average((base+fine@d)**2,weights=fw))),
            broadCoefficientDifferenceRmsMm=float(np.sqrt(np.average((broad@d)**2,weights=bw))))
        records.append(record);print(record,flush=True)
        if not fit.success or relative>1e-4:raise ValueError('Protected chest solve did not converge')
        if record['maximumStepMm']<.01:break
    p=v+d[:,None]*n
    # Retain exact snapshot coordinates everywhere outside this chest patch.
    p[~free]=current['position'][~free]
    np.testing.assert_array_equal(p[~free],current['position'][~free]);assert d.max()<1e-8
    old_cross=np.cross(v[t[:,1]]-v[t[:,0]],v[t[:,2]]-v[t[:,0]])
    new_cross=np.cross(p[t[:,1]]-p[t[:,0]],p[t[:,2]]-p[t[:,0]])
    ratio=np.sum(old_cross*new_cross,axis=1)/np.sum(old_cross**2,axis=1)
    if ratio.min()<=.05:raise ValueError('Protected chest contains collapsed/reversed triangles')
    result=dict(current);result.update(position=p,normal=normals(p,t)[t],envelopeNormalShiftMm=d,
        broadCurveProtection=protection,chestFreeVertex=free,fixedToCurrent=~free)
    out=Path(args.output);out.mkdir(parents=True,exist_ok=True)
    np.savez_compressed(out/'body-fit-reference.npz',**result)
    write_obj(out/'vanilla-body-fit.obj',p,t,result['normal'],raw['uv'],raw['group'])
    lm=dict(np.load(Path(args.current)/'paired-landmarks-candidate.npz'))
    lm['sourcePosition']=np.einsum('ni,nij->nj',lm['sourceBary'],p[t[lm['sourceFace']]])
    np.savez_compressed(out/'paired-landmarks-candidate.npz',**lm)
    write_points(out/'Vanilla-BodyFit.points.json',lm['names'],lm['sourceFace'],lm['sourceBary'])
    report=dict(rawSha256=hashlib.sha256(raw_path.read_bytes()).hexdigest(),currentSha256=hashlib.sha256(current_path.read_bytes()).hexdigest(),
        freeChestVertices=len(ids),unchangedVertices=int((~free).sum()),fineRadiusMm=args.fine_radius,fineWeight=args.fine_weight,
        broadWeight=args.broad_weight,broadRadiusMm=30,iterations=records,connectivity=boundaries(t),
        minimumProjectedAreaRatio=float(ratio.min()),
        scope='Chest-only experiment: protect robust broad slope/curvature coefficients and filter finer relief. Current lower-body coordinates remain exact but the crotch is flagged by the lower-body audit. Cleavage and strap/clasp post-processing is deferred.')
    (out/'protected-chest-report.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report,indent=2),flush=True)


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--raw',default='outputs/body-cleanup-4x-raw')
    p.add_argument('--current',default='outputs/body-curvature-envelope-4x-w1000');p.add_argument('--output',default='outputs/body-protected-underbreast-4x-f20-w10000')
    p.add_argument('--broad-weight',type=float,default=1000.);p.add_argument('--fine-weight',type=float,default=10000.)
    p.add_argument('--fine-radius',type=float,default=20.);p.add_argument('--iterations',type=int,default=8);a=p.parse_args()
    if a.broad_weight<=0 or a.fine_weight<=0 or a.fine_radius<=0 or a.iterations<1:p.error('Positive weights/radius and at least one iteration required')
    run(a)
