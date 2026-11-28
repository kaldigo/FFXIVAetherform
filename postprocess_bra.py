"""Trial local angle-guided bra seam and cleavage reconstruction."""
import argparse,hashlib,json
from pathlib import Path
import numpy as np
import triangle
from scipy.interpolate import RBFInterpolator,CubicHermiteSpline
from scipy.ndimage import gaussian_filter1d
from scipy.optimize import minimize,LinearConstraint
from scipy.sparse.csgraph import dijkstra
from scipy.sparse import coo_matrix,diags
from scipy.sparse.linalg import spsolve
from shapely.geometry import Polygon,Point,LineString
from repair_surface import loop_of_patch,frame_for,boundaries
from clean_reference import normals,graph
from reference_surface import unit,areas,write_obj
from compare_af_bases import closest_surface
from investigate_coatee import front_depth
from transfer_landmarks import write_points
from separate_body_shape import surface_operator
from fit_curvature_envelope import solve_curvature


def depth(p,x,y):
    mask=(p[:,:,0].min(1)<=x)&(p[:,:,0].max(1)>=x)&(p[:,:,1].min(1)<=y)&(p[:,:,1].max(1)>=y)
    hit=front_depth(p[mask],(x,y))
    if hit is None:raise ValueError('Missing chest constraint')
    return hit['zMm']


def valley_guide(v,t,loop):
    poly=Polygon(v[loop,:2]);p=v[t]
    section=poly.intersection(LineString([(0,poly.bounds[1]-1),(0,poly.bounds[3]+1)]))
    y0,y1=section.bounds[1],section.bounds[3];ys=np.linspace(y0,y1,55)
    z0,z1=depth(p,0,y0),depth(p,0,y1)
    low_y=np.linspace(y0-6,y0,7);high_y=np.linspace(y1,y1+6,7)
    slope0=np.polyfit(low_y-y0,[depth(p,0,y) for y in low_y],1)[0]
    slope1=np.polyfit(high_y-y1,[depth(p,0,y) for y in high_y],1)[0]
    skin_line=CubicHermiteSpline([y0,y1],[z0,z1],[slope0,slope1]);estimated=[];widths=[]
    for y in ys:
        cross=poly.intersection(LineString([(-200,y),(200,y)]))
        width=max(abs(cross.bounds[0]),abs(cross.bounds[2]))+3.
        width=max(width,26.);widths.append(width)
        left_x=np.linspace(-width-2,-width+2,5);right_x=-left_x[::-1]
        ml,al=np.polyfit(left_x,[depth(p,x,y) for x in left_x],1)
        mr,ar=np.polyfit(right_x,[depth(p,x,y) for x in right_x],1)
        if ml<-.02 and mr>.02:
            xc=(ar-al)/(ml-mr);zc=(ml*xc+al+mr*xc+ar)/2
        else:zc=float(skin_line(y))
        estimated.append(zc)
    estimate=gaussian_filter1d(np.array(estimated),1.5,mode='nearest')
    fraction=(ys-y0)/(y1-y0);blend=np.clip(np.minimum(fraction,1-fraction)*5,0,1);blend=blend**2*(3-2*blend)
    target=skin_line(ys)+blend*(estimate-skin_line(ys))
    h=ys[1]-ys[0];identity=np.eye(len(ys));first=np.diff(identity,axis=0)/h;second=np.diff(identity,n=2,axis=0)/h**2
    matrix=identity+8.**4*(second.T@second)
    fixed=np.stack((identity[0],identity[-1],first[0],first[-1]));values=np.array([z0,z1,slope0,slope1])
    constraints=[LinearConstraint(fixed,values,values)]
    if slope0<=0 and slope1<=0 and z1<z0:constraints.append(LinearConstraint(first,-np.inf,0))
    elif slope0>=0 and slope1>=0 and z1>z0:constraints.append(LinearConstraint(first,0,np.inf))
    fit=minimize(lambda z:(.5*z@(matrix@z)-target@z,matrix@z-target),skin_line(ys),jac=True,method='SLSQP',
        constraints=constraints,options=dict(maxiter=500,ftol=1e-10))
    if not fit.success:raise ValueError('Centreline smoothing failed: '+fit.message)
    z=fit.x
    # Keep guide points strictly inside the polygon, away from the join.
    use=np.array([poly.contains(Point(0,y)) and poly.boundary.distance(Point(0,y))>3 for y in ys])
    points=np.c_[np.zeros(use.sum()),ys[use],z[use]]
    return points,dict(endY=[float(y0),float(y1)],endDepth=[z0,z1],endSlopes=[float(slope0),float(slope1)],
        guide=points.tolist(),method='Breast-flank tangent intersections blended into upper/lower skin continuation, with an 8 mm bending scale and consistent endpoint direction.')


def build_patch(v,t,mask,kind):
    loop=loop_of_patch(t,mask)
    if kind=='cleavage':origin=np.zeros(3);basis=np.eye(3);guides,guide_report=valley_guide(v,t,loop)
    else:origin,basis=frame_for(v[loop]);guides=np.empty((0,3));guide_report=None
    xy=(v[loop]-origin)@basis[:2].T;poly=Polygon(xy)
    if not poly.is_valid:raise ValueError('Patch chart boundary is not simple')
    if poly.exterior.is_ccw is False:raise ValueError('Patch chart winding is reversed')
    outside=np.flatnonzero(~mask&np.any(np.isin(t,loop),axis=1))
    collar=np.unique(t[outside]);outside2=np.flatnonzero(~mask&np.any(np.isin(t,collar),axis=1));collar=np.unique(t[outside2])
    samples=(v[collar]-origin)@basis.T
    scale=max(float(np.ptp(xy,axis=0).max()),1.)
    sample_xy=list(samples[:,:2]);sample_z=list(samples[:,2])
    # Exterior one-ring normals provide inward virtual samples for the join.
    face=v[t[outside]];fn=unit(np.cross(face[:,1]-face[:,0],face[:,2]-face[:,0]));lookup={}
    for tri,normal in zip(t[outside],fn):
        for vertex in tri:lookup.setdefault(int(vertex),[]).append(normal)
    added=0
    for k,vertex in enumerate(loop if kind=='cleavage' else []):
        normal=unit(np.mean(lookup[int(vertex)],axis=0))@basis.T
        if normal[2]<.15:continue
        tangent=xy[(k+1)%len(loop)]-xy[(k-1)%len(loop)];tangent/=np.linalg.norm(tangent)
        delta=1.5*np.array([-tangent[1],tangent[0]]);point=xy[k]+delta
        if poly.contains(Point(point)):
            height=float((v[vertex]-origin)@basis[2]-normal[:2]@delta/normal[2])
            sample_xy.append(point);sample_z.append(height);added+=1
    if len(guides):
        local=(guides-origin)@basis.T;sample_xy.extend(local[:,:2]);sample_z.extend(local[:,2])
    sample_xy=np.array(sample_xy);sample_z=np.array(sample_z)
    _,unique=np.unique(np.round(sample_xy,7),axis=0,return_index=True)
    model=RBFInterpolator(sample_xy[unique]/scale,sample_z[unique],kernel='thin_plate_spline',degree=1,smoothing=0.)
    guide_xy=(guides-origin)@basis[:2].T
    vertices=np.r_[xy,guide_xy];segments=np.c_[np.arange(len(loop)),np.roll(np.arange(len(loop)),-1)]
    if len(guides)>1:segments=np.r_[segments,np.c_[len(loop)+np.arange(len(guides)-1),len(loop)+np.arange(1,len(guides))]]
    chart=(v[t[mask]]-origin)@basis[:2].T
    target=float(np.mean(areas(np.dstack((chart,np.zeros(chart.shape[:2]))))))
    result=triangle.triangulate(dict(vertices=vertices,segments=segments),f'pq20a{max(target,.15):.8f}Y')
    q,nt=result['vertices'],result['triangles'];height=model(q/scale)
    if kind!='cleavage':
        def design(xy):
            x,y=(xy/scale).T
            return np.c_[np.ones(len(x)),x,y,x*x,x*y,y*y]
        matrix=design(samples[:,:2]);robust=np.ones(len(samples))
        for _ in range(3):
            root=np.sqrt(robust);coef=np.linalg.lstsq(matrix*root[:,None],samples[:,2]*root,rcond=1e-8)[0]
            error=samples[:,2]-matrix@coef;deviation=max(.15,1.4826*np.median(abs(error-np.median(error))))
            robust=np.minimum(1.,1.5*deviation/np.maximum(abs(error),1e-20))
        height=design(q)@coef
        residual=np.zeros(len(q));residual[:len(loop)]=(v[loop]-origin)@basis[2]-height[:len(loop)]
        edges=np.unique(np.sort(nt[:,[[0,1],[1,2],[2,0]]].reshape(-1,2),axis=1),axis=0)
        length=np.linalg.norm(q[edges[:,0]]-q[edges[:,1]],axis=1)
        i=np.r_[edges[:,0],edges[:,1]];j=np.r_[edges[:,1],edges[:,0]]
        adjacency=coo_matrix((np.r_[1/length,1/length],(i,j)),shape=(len(q),len(q))).tocsr()
        lap=diags(np.asarray(adjacency.sum(1)).ravel())-adjacency;inside=np.arange(len(loop),len(q))
        residual[inside]=spsolve(lap[inside][:,inside],-lap[inside][:,:len(loop)]@residual[:len(loop)])
        height+=residual
    p=origin+q@basis[:2]+height[:,None]*basis[2];p[:len(loop)]=v[loop]
    if len(guides):p[len(loop):len(loop)+len(guides)]=guides
    cross=np.cross(p[nt[:,1]]-p[nt[:,0]],p[nt[:,2]]-p[nt[:,0]])
    if (cross@basis[2]).min()<=0:raise ValueError('Reconstructed patch folds in its chart')
    return p,nt,loop,origin,basis,dict(kind=kind,oldFaces=np.flatnonzero(mask).tolist(),boundaryVertices=loop.tolist(),
        origin=origin.tolist(),basis=basis.tolist(),oldFaceCount=int(mask.sum()),newFaceCount=len(nt),
        generatedVertices=len(p)-len(loop),inwardAngleSamples=added,valleyGuide=guide_report)


def run(args):
    manifest=json.loads(Path('working-mesh.json').read_text());source=Path(manifest['artifacts']['body-fit-reference.npz']['path'])
    if hashlib.sha256(source.read_bytes()).hexdigest()!=manifest['artifacts']['body-fit-reference.npz']['sha256']:raise ValueError('Accepted working mesh changed')
    data=dict(np.load(source));v,t=data['position'],data['triangles'];source_v=v.copy();c=v[t].mean(1)
    root=next(p for p in json.loads(Path('outputs/body-cleanup-4x-raw/body-cleanup-report.json').read_text())['patches'] if p['name']=='flap_root_1506')
    cleavage=np.zeros(len(t),bool);cleavage[root['firstNewFace']:root['firstNewFace']+root['newFaceCount']]=True
    cleavage|=(abs(c[:,0])<24)&(c[:,1]>1195)&(c[:,1]<1308)&(c[:,2]>60)
    masks=[('cleavage',cleavage)];seam_masks=[]
    edges={}
    for i,tri in enumerate(t):
        for a,b in ((0,1),(1,2),(2,0)):edges.setdefault(tuple(sorted(tri[[a,b]])),[]).append(i)
    seam_edges=[]
    for edge,faces in edges.items():
        if len(faces)==2 and set(data['group'][faces])=={'top_skin','top_smallclothes'}:
            middle=v[list(edge)].mean(0)
            if 25<abs(middle[0])<120 and 1165<middle[1]<1210 and middle[2]>40:seam_edges.append(edge)
    _,metric=graph(v,t)
    for side in (-1,1):
        edges_side=np.array([edge for edge in seam_edges if side*v[list(edge),0].mean()>0])
        distance=dijkstra(metric,directed=False,indices=np.unique(edges_side),min_only=True)
        mask=(distance[t].mean(1)<8)&(side*c[:,0]>25)&(side*c[:,0]<120)&(c[:,2]>40)
        mask&=~cleavage;seam_masks.append(('bra_seam_left' if side<0 else 'bra_seam_right',mask))
    seam_free=np.zeros(len(v),bool)
    for _,mask in seam_masks:
        interior=np.setdiff1d(np.unique(t[mask]),loop_of_patch(t,mask));seam_free[interior]=True
    average,_=graph(v,t);direction=normals(v,t)
    for _ in range(12):direction=unit(.5*direction+.5*(average@direction))
    mass,lap=surface_operator(v,t)
    seam_shift,seam_solve=solve_curvature(np.zeros(len(v)),mass,lap,seam_free,4.,(v,t,direction),data['group'],1000.,15.,6)
    v=v+seam_shift[:,None]*direction
    remove=np.logical_or.reduce([m for _,m in masks]);keep=np.flatnonzero(~remove)
    positions=[v];triangles=[t[keep]];uv=[data['uv'][keep]];weights=[data['weights'][keep]];groups=[data['group'][keep]]
    working_face=[np.repeat(keep[:,None],3,axis=1)];working_bary=[np.repeat(np.eye(3)[None],len(keep),axis=0)]
    next_vertex=len(v);next_face=len(keep);records=[]
    for kind,mask in masks:
        p,nt,loop,origin,basis,record=build_patch(v,t,mask,kind)
        mapping=np.r_[loop,np.arange(next_vertex,next_vertex+len(p)-len(loop))];next_vertex+=len(p)-len(loop)
        positions.append(p[len(loop):]);triangles.append(mapping[nt]);old=np.flatnonzero(mask)
        _,fi,bc=closest_surface(p[nt].reshape(-1,3),source_v[t[old]]);fi=old[fi].reshape(-1,3);bc=bc.reshape(-1,3,3)
        uv.append(np.einsum('fci,fcij->fcj',bc,data['uv'][fi]));weights.append(np.einsum('fci,fcij->fcj',bc,data['weights'][fi]))
        working_face.append(fi);working_bary.append(bc)
        _,centre,_=closest_surface(p[nt].mean(1),source_v[t[old]]);groups.append(data['group'][old[centre]])
        record.update(firstNewFace=next_face);next_face+=len(nt);records.append(record)
    positions=np.concatenate(positions);triangles=np.concatenate(triangles);used,inverse=np.unique(triangles,return_inverse=True)
    triangles=inverse.reshape(-1,3);positions=positions[used]
    result=dict(position=positions,triangles=triangles,normal=normals(positions,triangles)[triangles],uv=np.concatenate(uv),
        weights=np.concatenate(weights),group=np.concatenate(groups),boneNames=data['boneNames'],
        sourceWorkingFace=np.concatenate(working_face),sourceWorkingBary=np.concatenate(working_bary),
        originalWorkingVertex=np.where(used<len(v),used,-1),originalWorkingFace=np.r_[keep,np.full(next_face-len(keep),-1)])
    connectivity=boundaries(triangles)
    if connectivity['openBoundaryLoops']!=5:raise ValueError('Body openings changed')
    points=dict(np.load(manifest['artifacts']['paired-landmarks-candidate.npz']['path']));lookup={int(f):i for i,f in enumerate(keep)};rebound=[]
    new_faces=[];new_bary=[]
    for name,face,bc in zip(points['names'],points['sourceFace'],points['sourceBary']):
        if int(face) in lookup:new_faces.append(lookup[int(face)]);new_bary.append(bc);continue
        patch=next(r for r in records if int(face) in r['oldFaces']);o=np.array(patch['origin']);basis=np.array(patch['basis'])
        target=(bc@source_v[t[face]]-o)@basis[:2].T;ids=np.arange(patch['firstNewFace'],patch['firstNewFace']+patch['newFaceCount'])
        q=(positions[triangles[ids]]-o)@basis[:2].T
        bary=np.linalg.solve((q[:,:2]-q[:,2,None]).transpose(0,2,1),(target-q[:,2])[...,None])[...,0];bary=np.c_[bary,1-bary.sum(1)]
        valid=(bary.min(1)>=-1e-7)&(bary.max(1)<=1+1e-7)
        if not valid.any():raise ValueError('Landmark outside new patch chart: '+str(name))
        j=int(np.flatnonzero(valid)[0]);b=np.clip(bary[j],0,1);b/=b.sum();new_faces.append(ids[j]);new_bary.append(b);rebound.append(str(name))
    points.update(sourceFace=np.array(new_faces),sourceBary=np.array(new_bary))
    points['sourcePosition']=np.einsum('ni,nij->nj',points['sourceBary'],positions[triangles[points['sourceFace']]])
    out=Path(args.output);out.mkdir(parents=True,exist_ok=True)
    np.savez_compressed(out/'body-fit-reference.npz',**result);np.savez_compressed(out/'paired-landmarks-candidate.npz',**points)
    write_obj(out/'vanilla-body-fit.obj',positions,triangles,result['normal'],result['uv'],result['group'])
    write_points(out/'Vanilla-BodyFit.points.json',points['names'],points['sourceFace'],points['sourceBary'])
    report=dict(workingMeshSha256=manifest['artifacts']['body-fit-reference.npz']['sha256'],patches=records,
        vertices=len(positions),triangles=len(triangles),connectivity=connectivity,reboundLandmarks=rebound,
        seamSmoothing=seam_solve,seamRegions=[dict(kind=kind,oldFaces=np.flatnonzero(mask).tolist()) for kind,mask in seam_masks],
        displacedWorkingVertices=np.flatnonzero(seam_free).tolist(),maximumSeamMovementMm=float(abs(seam_shift).max()),
        provenance='Exact retained data or interpolated accepted-working face/barycentric witnesses; no invented single native parent for new triangles.',
        scope='Local user-proposed angle/centreline reconstruction trial. Accepted working snapshot is unchanged. Central shape is estimated from surrounding geometry, not recovered hidden anatomy.')
    (out/'postprocess-report.json').write_text(json.dumps(report,indent=2)+'\n')
    print(json.dumps({k:value for k,value in report.items() if k not in ('patches','seamRegions','displacedWorkingVertices')},indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--output',default='outputs/bra-postprocess-v5');run(p.parse_args())
