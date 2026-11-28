"""Control the final surface's local quadratic residual, not only its offset."""
import time
import numpy as np
from scipy.sparse import coo_matrix,diags
from scipy.sparse.csgraph import dijkstra
from scipy.optimize import minimize
from clean_reference import graph
from reference_surface import unit
from fit_inner_envelope import orientation_penalty


def curvature_operator(raw,current,direction,t,groups,mass,free,radius,normal_cos=.6,neighbourhood_normals=None,split_sides=False):
    """Linearised signed residual from a local quadratic final-surface fit.

    Tangential coordinates come from current positions; neighbourhood distance
    and area weights stay tied to raw. No neighbours on the other breast side
    or an opposing surface are mixed into a fit.
    """
    _,metric=graph(raw,t);top=np.zeros(len(raw),bool);top[t[groups=='top_smallclothes']]=True
    neighbourhood_normals=direction if neighbourhood_normals is None else neighbourhood_normals
    rows=[];cols=[];values=[];base=[];row_mass=[];row_ids=[];start=time.time()
    for count,i in enumerate(np.flatnonzero(free)):
        rr=radius
        if top[i] and raw[i,2]>40:rr=min(rr,8.+.7*abs(raw[i,0]))
        if not top[i] and raw[i,2]<-35:rr=min(rr,10.+.8*abs(raw[i,0]))
        distance=dijkstra(metric,directed=False,indices=int(i),limit=rr)
        near=(distance<rr)&((neighbourhood_normals@neighbourhood_normals[i])>normal_cos)
        if split_sides or (top[i] and raw[i,2]>40) or (not top[i] and raw[i,2]<-35):
            if abs(raw[i,0])>1e-5:near&=raw[:,0]*raw[i,0]>=-1e-6
            else:near&=abs(raw[:,0])<6.
        ids=np.flatnonzero(near)
        if len(ids)<12:continue
        axis=np.array([0.,1.,0.]) if abs(direction[i,1])<.9 else np.array([1.,0.,0.])
        u=unit(np.cross(direction[i],axis));v=np.cross(direction[i],u)
        delta=(current[ids]-current[i])/rr;x,y=delta@u,delta@v
        design=np.c_[np.ones(len(ids)),x,y,x*x,x*y,y*y]
        weights=mass[ids]*np.exp(-(distance[ids]/(.48*rr))**2)
        root=np.sqrt(weights);weighted=design*root[:,None]
        if np.linalg.matrix_rank(weighted,tol=1e-8)<6:continue
        # Weighted least-squares intercept is a linear functional of height.
        fit=np.linalg.pinv(weighted,rcond=1e-8)[0]*root
        coefficients=-fit;coefficients[np.flatnonzero(ids==i)[0]]+=1.
        row=len(base);rows.extend([row]*len(ids));cols.extend(ids)
        values.extend(coefficients*(direction[ids]@direction[i]))
        base.append(float(np.sum(coefficients*((raw[ids]-raw[i])@direction[i]))))
        row_mass.append(mass[i]);row_ids.append(i)
        if count%1500==0:print('curvature rows',count,'seconds',round(time.time()-start,1),flush=True)
    operator=coo_matrix((values,(rows,cols)),shape=(len(base),len(raw))).tocsr()
    return operator,np.array(base),np.array(row_mass),np.array(row_ids)


def solve_curvature(target,mass,lap,free,length,geometry,groups,strength,radius,iterations,initial=None,history=None):
    raw,t,direction=geometry;ids=np.flatnonzero(free)
    # Continue the inward adjustment gently; actual high-frequency fairness
    # is now applied to final positions by the quadratic residual operator.
    q=(diags(mass)+length**2*lap).tocsr();a=q[ids][:,ids].tocsr();rhs=(q@target)[ids]
    d=np.zeros(len(raw));d[ids]=np.minimum(target[ids],0.) if initial is None else initial[ids]
    relevant=t[np.any(free[t],axis=1)];records=list(history or [])
    for iteration in range(iterations):
        operator,base,weight,row_ids=curvature_operator(raw,raw+d[:,None]*direction,direction,t,groups,mass,free,radius)
        j=operator[:,ids];wt=strength*weight
        diagonal=np.sqrt(a.diagonal()+np.asarray(j.power(2).T@wt).ravel())
        def objective(z):
            x=z/diagonal;residual=base+j@x
            value=.5*x@(a@x)-rhs@x+.5*np.sum(wt*residual**2)
            gradient=a@x-rhs+j.T@(wt*residual)
            full=np.zeros(len(raw));full[ids]=x
            penalty,g=orientation_penalty(raw,relevant,direction,full,weight=10000.)
            return float(value+penalty),(gradient+g[ids])/diagonal
        result=minimize(objective,d[ids]*diagonal,jac=True,method='L-BFGS-B',bounds=[(None,0.)]*len(ids),
            options=dict(maxiter=4000,ftol=1e-13,gtol=1e-8,maxcor=30,maxls=40))
        previous=d.copy();d[ids]=result.x/diagonal
        gradient=objective(result.x)[1]*diagonal
        projected=np.where(d[ids]<-1e-7,gradient,np.maximum(gradient,0))
        relative=float(np.linalg.norm(projected)/max(np.linalg.norm(rhs)+np.linalg.norm(j.T@(wt*base)),1.))
        residual=base+operator@d
        record=dict(iteration=len(records)+1,optimizerIterations=int(result.nit),success=bool(result.success),
            message=str(result.message),relativeProjectedGradient=relative,maximumStepMm=float(abs(d-previous).max()),
            finalCurveResidualRmsMm=float(np.sqrt(np.average(residual**2,weights=weight))))
        records.append(record);print('curvature solve',record,flush=True)
        if not result.success or relative>1e-4:raise ValueError('Final curvature solve did not converge')
        if record['maximumStepMm']<.01:break
    return d,dict(method='Final-surface local quadratic residual with inward bounds and fixed outer skin',
        curvatureWeight=strength,curvatureRadiusMm=radius,shapeLinearisationToleranceMm=.01,
        shapeLinearisationConverged=records[-1]['maximumStepMm']<.01,iterations=records)
