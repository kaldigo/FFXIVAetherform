"""Jointly integrate native normal-map shading into a shared geometric surface."""
import argparse,json
from pathlib import Path
import numpy as np
from scipy.sparse import coo_matrix,diags
from scipy.sparse.linalg import spsolve
from types import SimpleNamespace
from reference_surface import unit,areas,write_obj,boundary_edges
from clean_reference import normals
from normal_field import NormalField


def geometric(v,t):return unit(np.cross(v[t[:,1]]-v[t[:,0]],v[t[:,2]]-v[t[:,0]]))


def error(v,t,target,valid):
    angle=np.degrees(np.arccos(np.clip(np.sum(geometric(v,t)*target,axis=-1),-1,1)))[valid]
    return dict(rmsDegrees=float(np.sqrt(np.mean(angle*angle))),p95Degrees=float(np.percentile(angle,95)),maxDegrees=float(angle.max()))


def integrate_height(v,t,target,valid,directions,position_weight,checkpoint=None):
    count=len(v);mass=np.zeros(count);area=areas(v[t]);rhs=np.zeros(count)
    rows=[];cols=[];vals=[]
    for k in range(3):np.add.at(mass,t[:,k],area/3)
    for a,b in ((0,1),(1,2),(2,0)):
        i,j=t[:,a],t[:,b];edge=v[j]-v[i]
        weight=valid*area/np.maximum(np.sum(edge*edge,axis=-1),1e-12)
        ai=-np.sum(target*directions[i],axis=-1);aj=np.sum(target*directions[j],axis=-1)
        desired=-np.sum(target*edge,axis=-1)
        np.add.at(rhs,i,weight*ai*desired);np.add.at(rhs,j,weight*aj*desired)
        rows.extend((i,j,i,j));cols.extend((i,j,j,i));vals.extend((weight*ai*ai,weight*aj*aj,weight*ai*aj,weight*ai*aj))
    matrix=coo_matrix((np.concatenate(vals),(np.concatenate(rows),np.concatenate(cols))),shape=(count,count)).tocsr()
    anchor=mass*position_weight
    # Preserve open body-part joins exactly; these are positional constraints,
    # not smoothing or fabric suppression.
    boundary=np.unique(np.array(boundary_edges(t))[:,:2]);anchor[boundary]=1e8
    matrix+=diags(anchor)
    raw_height=spsolve(matrix,rhs);raw_height[boundary]=0
    # The linear normal equations cannot distinguish a normal from its
    # opposite. A geometric barrier resolves that ambiguity during the solve,
    # rather than scaling individual vertex moves after solving.
    rest_cross=np.cross(v[t[:,1]]-v[t[:,0]],v[t[:,2]]-v[t[:,0]])
    rest_norm=unit(rest_cross);double_area=2*area
    free=np.ones(count,bool);free[boundary]=False;ids=np.flatnonzero(free)
    scale=1/np.sqrt(matrix.diagonal()[ids]);mu=.1;floor=.2
    def ratios(h):
        q=(v+h[:,None]*directions)[t]
        ratio=np.einsum('ij,ij->i',np.cross(q[:,1]-q[:,0],q[:,2]-q[:,0]),rest_norm)/double_area
        return ratio,q
    height=raw_height.copy()
    for _ in range(200):
        ratio,_=ratios(height)
        bad=valid&(ratio<floor+.05)
        if not bad.any():break
        height[np.unique(t[bad])]*=.5
    else:
        height=np.zeros(count)
    if checkpoint and Path(checkpoint).exists():
        previous=np.load(checkpoint)
        if previous.shape==height.shape and ratios(previous)[0][valid].min()>.05:height=previous
    def objective(x):
        h=np.zeros(count);h[ids]=x*scale
        ratio,q=ratios(h);gap=ratio-floor
        base=float(h@(matrix@h)-2*h@rhs);gradient=2*(matrix@h-rhs)
        z=gap[valid]/(1-floor)
        # A C2 quadratic extension below the barrier threshold gives line
        # searches a usable restoring gradient even for an infeasible trial.
        epsilon=.05;safe=np.maximum(z,epsilon);dz=z-epsilon
        penalty=np.where(z>=epsilon,-np.log(safe)+z-1,
                         -np.log(epsilon)+epsilon-1+(1-1/epsilon)*dz+.5*dz*dz/epsilon**2)
        derivative=np.where(z>=epsilon,1-1/safe,1-1/epsilon+dz/epsilon**2)
        energy=base+mu*np.sum(area[valid]*penalty)
        coeff=np.zeros(len(t));coeff[valid]=mu*area[valid]*derivative/(1-floor)/double_area[valid]
        g1=np.cross(q[:,2]-q[:,0],rest_norm);g2=np.cross(rest_norm,q[:,1]-q[:,0]);g0=-g1-g2
        for k,g in enumerate((g0,g1,g2)):
            np.add.at(gradient,t[:,k],coeff*np.sum(g*directions[t[:,k]],axis=-1))
        return float(energy),gradient[ids]*scale
    success=False;message='Newton iteration limit reached';nit=0
    for nit in range(41):
        x=height[ids]/scale;energy,gx=objective(x)
        gradient=np.zeros(count);gradient[ids]=gx/scale
        if np.max(abs(gradient[ids]))<1e-7:
            success=True;message='Gradient tolerance reached';break
        if nit==40:break
        ratio,q=ratios(height);z=(ratio-floor)/(1-floor);safe=np.maximum(z,.05)
        weight=np.zeros(len(t));weight[valid]=mu*area[valid]/((1-floor)**2*safe[valid]**2)
        g1=np.cross(q[:,2]-q[:,0],rest_norm);g2=np.cross(rest_norm,q[:,1]-q[:,0]);g0=-g1-g2
        jac=np.stack([np.sum(g*directions[t[:,k]],axis=-1)/double_area for k,g in enumerate((g0,g1,g2))],axis=1)
        hr=[];hc=[];hv=[]
        for a in range(3):
            for b in range(3):
                hr.append(t[:,a]);hc.append(t[:,b]);hv.append(weight*jac[:,a]*jac[:,b])
        hessian=2*matrix+coo_matrix((np.concatenate(hv),(np.concatenate(hr),np.concatenate(hc))),shape=matrix.shape).tocsr()
        step=spsolve(hessian,-gradient);step[boundary]=0
        alpha=1.
        for _ in range(40):
            trial=height+alpha*step;trial_energy,_=objective(trial[ids]/scale)
            if trial_energy<=energy+1e-4*alpha*(gradient@step) and ratios(trial)[0][valid].min()>.05:break
            alpha*=.5
        else:message='Line search failed';break
        height=trial
        if checkpoint:np.save(checkpoint,height)
        max_step=float(np.max(abs(alpha*step)))
        print('normal solve Newton',nit+1,'maximum step mm',round(max_step,7),'energy',round(trial_energy,5),flush=True)
        if max_step<.001 and abs(trial_energy-energy)<1e-8*max(abs(energy),1.):
            success=True;message='Position and objective tolerances reached';nit+=1;break
    result=SimpleNamespace(x=height[ids]/scale,success=success,message=message,nit=nit,fun=objective(height[ids]/scale)[0])
    height=np.zeros(count);height[ids]=result.x*scale
    ratio,_=ratios(height)
    print('orientation solve',result.message,'iterations',result.nit,'minimum measured signed-area ratio',ratio[valid].min(),flush=True)
    if checkpoint:np.save(checkpoint,height)
    if checkpoint:
        Path(checkpoint).with_suffix('.json').write_text(json.dumps(dict(success=bool(result.success),message=str(result.message),
                    iterations=int(result.nit),objective=float(result.fun),minimumMeasuredSignedAreaRatio=float(ratio[valid].min())),indent=2)+'\n')
    if not result.success or ratio[valid].min()<=.05:raise ValueError('Orientation-constrained solve did not converge')
    return v+height[:,None]*directions


def run(args):
    if args.position_weight<=0:raise ValueError('Position weight must be positive')
    d=dict(np.load(args.reference));out=Path(args.output);out.mkdir(parents=True,exist_ok=True)
    field=NormalField(d,args.assets)
    if args.control:
        control=np.load(args.control);v,t=control['position'],control['triangles'];d['sourceFace']=control['sourceFace'];d['sourceBarycentric']=control['sourceBarycentric']
        d['flatPosition']=v;d['triangles']=t
        d['uv']=np.einsum('fci,fij->fcj',d['sourceBarycentric'],field.uv[d['sourceFace']])
        d['weights']=np.einsum('fci,fij->fcj',d['sourceBarycentric'],field.weights[d['sourceFace']])
        d['group']=d['sourceGroups'][d['sourceFace']]
    else:v,t=d['flatPosition'],d['triangles']
    parent=d['sourceFace'];bc=d['sourceBarycentric'].mean(1)
    target=field.sample(parent,bc);valid=field.valid[parent]
    target[~valid]=geometric(v,t)[~valid]
    authored=field.sample(np.repeat(parent,3),d['sourceBarycentric'].reshape(-1,3),False).reshape(-1,3,3)
    directions=np.zeros_like(v)
    for k in range(3):np.add.at(directions,t[:,k],authored[:,k]*areas(v[t])[:,None])
    directions=unit(directions)
    candidate=integrate_height(v,t,target,valid,directions,args.position_weight,out/'solve-height.npy')
    area_ratio=areas(candidate[t])/areas(v[t])
    old=geometric(v,t);new=geometric(candidate,t)
    flips=np.sum(old*new,axis=-1)<=0
    result=dict(d);result.update(position=candidate,normal=normals(candidate,t)[t],
                                normalMapTarget=target,normalMapTargetValid=valid,
                                controlPosition=v,method=np.array('joint normal-map integration'))
    np.savez_compressed(out/'normal-map-reference.npz',**result)
    write_obj(out/'vanilla-normal-map.obj',candidate,t,result['normal'],d['uv'],d['group'])
    move=np.linalg.norm(candidate-v,axis=1)
    density_path=out/'density-report.json'
    density=json.loads(density_path.read_text()) if density_path.exists() else None
    if density:
        density['reconstructedAFDensityBySlot']={}
        face_area=areas(candidate[t])
        for slot,spec in density['slots'].items():
            select=np.char.startswith(d['group'],slot+'_')
            density['reconstructedAFDensityBySlot'][slot]=float(select.sum()/face_area[select].sum()*spec['afMeanTriangleAreaMm2'])
    report=dict(method='Height normal-field integration with position prior and orientation barrier; no smoothing/fabric filtering',
                positionWeight=args.position_weight,solver=json.loads((out/'solve-height.json').read_text()),
                density=density,
                nativeFlatGeometryError=error(v,t,target,valid),
                reconstructedGeometryError=error(candidate,t,target,valid),
                areaRatioMin=float(area_ratio.min()),areaRatioMax=float(area_ratio.max()),
                movementFromNativeFlatMm=dict(max=float(move.max()),p95=float(np.percentile(move,95))),
                reversedAgainstFlat=int(flips.sum()),reversedMeasuredFaces=int((flips&valid).sum()),
                minimumTriangleAreaMm2=float(areas(candidate[t]).min()),vertices=len(v),triangles=len(t),
                texturePaths=[x['path'] for x in field.textures],
                scope='Base normal textures at mip 0, neutral customization, material normal strength; no tile-detail normal blending or game PBR lighting.')
    (out/'normal-map-report.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report,indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--reference',default='outputs/vanilla-reference-v1/vanilla-reference.npz')
    p.add_argument('--assets',default='outputs/normal-map-assets-v1');p.add_argument('--output',required=True)
    p.add_argument('--position-weight',type=float,default=.0001);p.add_argument('--control',required=True)
    run(p.parse_args())
