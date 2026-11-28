"""Extract raw 4x underwear relief and fit a separate shell over the 6 mm body."""
import argparse,json,hashlib
from pathlib import Path
import numpy as np
from reference_surface import write_obj,areas,boundary_edges,unit
from clean_reference import normals
from repair_surface import adjacency,boundaries
from compare_af_bases import closest_surface
from mesh_crossings import crossing_pairs


def components(t):
    adj=adjacency(t);remain=set(range(len(t)));result=[]
    while remain:
        seen=set();todo=[next(iter(remain))]
        while todo:
            i=todo.pop()
            if i in seen:continue
            seen.add(i);todo.extend(adj[i]-seen)
        remain-=seen;result.append(np.array(sorted(seen)))
    return sorted(result,key=len,reverse=True)


def sample_surface(v,t):
    p=v[t]
    return np.r_[v,p.mean(1),(p[:,0]+p[:,1])/2,(p[:,1]+p[:,2])/2,(p[:,2]+p[:,0])/2]


def signed_nearest(points,body_p,body_n):
    distance,face,bary=closest_surface(points,body_p)
    contact=np.einsum('ni,nij->nj',bary,body_p[face]);n=unit(np.einsum('ni,nij->nj',bary,body_n[face]))
    return np.sum((points-contact)*n,axis=1),distance


def make_shell(outer,inner,t,uv,weights,name):
    n=len(outer);v=np.r_[outer,inner];tt=[t,t[:,::-1]+n]
    uu=[uv,uv[:,::-1]];ww=[weights,weights[:,::-1]]
    nn=[normals(outer,t)[t],-normals(inner,t)[t[:,::-1]]]
    group=[np.full(len(t),name+'_outer'),np.full(len(t),name+'_lining')]
    vertex_weights=np.zeros((n,weights.shape[-1]));count=np.zeros(n)
    for k in range(3):np.add.at(vertex_weights,t[:,k],weights[:,k]);np.add.at(count,t[:,k],1)
    vertex_weights/=count[:,None]
    walls=[];wall_uv=[]
    for a,b,*_ in boundary_edges(t):
        walls.extend(((b,a,a+n),(b,a+n,b+n)))
        length=np.linalg.norm(outer[b]-outer[a]);height=(np.linalg.norm(outer[a]-inner[a])+np.linalg.norm(outer[b]-inner[b]))/2
        wall_uv.extend(([[length,0],[0,0],[0,height]],[[length,0],[0,height],[length,height]]))
    walls=np.array(walls);tt.append(walls);uu.append(np.array(wall_uv));ww.append(vertex_weights[walls%n])
    q=v[walls];wn=unit(np.cross(q[:,1]-q[:,0],q[:,2]-q[:,0]));nn.append(np.repeat(wn[:,None],3,axis=1));group.append(np.full(len(walls),name+'_edge'))
    result=dict(position=v,triangles=np.concatenate(tt),normal=np.concatenate(nn),uv=np.concatenate(uu),weights=np.concatenate(ww),group=np.concatenate(group))
    audit=boundaries(result['triangles'])
    if audit['openBoundaryLoops']!=0:raise ValueError('Garment shell has open edges')
    if areas(v[result['triangles']]).min()<=1e-10:raise ValueError('Degenerate shell face')
    return result


def combine(meshes,bones):
    arrays={key:[] for key in ('position','triangles','normal','uv','weights','group')};offset=0
    for d in meshes:
        for key in arrays:arrays[key].append(d[key]+offset if key=='triangles' else d[key])
        offset+=len(d['position'])
    result={key:np.concatenate(value) for key,value in arrays.items()};result['boneNames']=bones
    return result


def run(args):
    out=Path(args.output);out.mkdir(parents=True,exist_ok=True)
    raw_path=Path(args.raw)/'body-fit-reference.npz';body_path=Path(args.body)/'body-fit-reference.npz'
    raw=dict(np.load(raw_path));body=dict(np.load(body_path))
    np.testing.assert_array_equal(raw['boneNames'],body['boneNames'])
    body_v,body_t=body['position'],body['triangles'];body_n=normals(body_v,body_t)
    body_lookup={int(old):i for i,old in enumerate(body['originalVertex']) if old>=0}
    raw_patches=json.loads((Path(args.raw)/'body-cleanup-report.json').read_text())['patches']
    body_patches={p['name']:p for p in json.loads((Path(args.body)/'body-cleanup-report.json').read_text())['patches']}
    surfaces=[];shells=[];records=[]
    for slot,name in (('top','bra'),('legs','briefs')):
        chosen=np.flatnonzero(raw['group']==slot+'_smallclothes');parts=components(raw['triangles'][chosen])
        discarded=[]
        for part in parts[1:]:
            faces=chosen[part]
            if len(part)>8 or np.any(raw['originalDenseFace'][faces]>=0):raise ValueError('Unexpected native garment component; inspect before discarding')
            discarded.extend(faces.tolist())
        faces=chosen[parts[0]];old_ids,inverse=np.unique(raw['triangles'][faces],return_inverse=True);t=inverse.reshape(-1,3)
        outer_raw=raw['position'][old_ids];bound=boundaries(t)
        contact=np.zeros_like(outer_raw);normal=np.zeros_like(outer_raw);mapped=np.zeros(len(old_ids),bool)
        old_native=raw['originalVertex'][old_ids]
        for i,old in enumerate(old_native):
            if old>=0:
                j=body_lookup[int(old)];contact[i]=body_v[j];normal[i]=body_n[j];mapped[i]=True
        # Steiner vertices in reconstructed roots have different topology.
        # Limit their projection to the corresponding root, never another body part.
        for patch in raw_patches:
            patch_faces=np.arange(patch['firstNewFace'],patch['firstNewFace']+patch['newFaceCount'])
            vertices=set(raw['triangles'][patch_faces].ravel());ids=np.array([i for i,g in enumerate(old_ids) if not mapped[i] and g in vertices],dtype=int)
            if not len(ids):continue
            target=body_patches[patch['name']];ff=np.arange(target['firstNewFace'],target['firstNewFace']+target['newFaceCount'])
            _,fi,bc=closest_surface(outer_raw[ids],body_v[body_t[ff]])
            contact[ids]=np.einsum('ni,nij->nj',bc,body_v[body_t[ff[fi]]]);normal[ids]=unit(np.einsum('ni,nij->nj',bc,body_n[body_t[ff[fi]]]))
            mapped[ids]=True
        if not mapped.all():raise ValueError('Unmapped garment vertices')
        uv,weights=raw['uv'][faces],raw['weights'][faces]
        surface=dict(position=outer_raw,triangles=t,normal=normals(outer_raw,t)[t],uv=uv,weights=weights,group=np.full(len(t),name+'_unadjusted_relief'))
        surfaces.append(surface)
        body_mask=np.char.startswith(body['group'],slot+'_');bp=body_v[body_t[body_mask]];bn=body_n[body_t[body_mask]]
        raw_gap=np.sum((outer_raw-contact)*normal,axis=1)
        clearance=args.clearance
        sampled_minimum=args.clearance*.5
        for attempt in range(24):
            inner=contact+clearance*normal
            signed,_=signed_nearest(sample_surface(inner,t),bp,bn)
            print(name,'lining attempt',attempt,'allowance',clearance,'minimum',float(signed.min()),flush=True)
            crossings=crossing_pairs(inner[t],bp) if signed.min()>=sampled_minimum else None
            if crossings is not None and len(crossings)==0:break
            if crossings is not None:print(name,'lining triangle crossings',len(crossings),flush=True)
            clearance+=max(.15,(sampled_minimum-float(signed.min()))*1.5)
        else:raise ValueError('Could not fit a clear lining')
        lining_signed_min=float(signed.min())
        # A uniform normal lift per component retains the raw signed relief
        # variations; a pointwise clamp would flatten the deepest grooves.
        lift=max(0.,clearance+args.wall-float(raw_gap.min()))
        for attempt in range(8):
            outer=outer_raw+lift*normal
            signed,dist=signed_nearest(sample_surface(outer,t),bp,bn)
            gap=np.sum((outer-inner)*normal,axis=1)
            if signed.min()>=sampled_minimum and gap.min()>=args.wall-1e-10:break
            lift+=max(.05,(sampled_minimum-float(signed.min()))*1.5)
        else:raise ValueError('Could not fit outer garment surface')
        fitted=make_shell(outer,inner,t,uv,weights,name);fitted['boneNames']=raw['boneNames'];shells.append(fitted)
        np.savez_compressed(out/(name+'-shell.npz'),**fitted,bodyContact=contact,contactNormal=normal,rawReliefPosition=outer_raw,
                            rawBodyVertex=old_ids,rawFace=faces,uniformNormalLiftMm=np.array(lift))
        write_obj(out/(name+'-shell.obj'),fitted['position'],fitted['triangles'],fitted['normal'],fitted['uv'],fitted['group'])
        vertices=np.vstack((outer,inner));tri=fitted['triangles'];p=vertices[tri]
        volume=np.einsum('ij,ij->i',p[:,0],np.cross(p[:,1],p[:,2])).sum()/6
        if volume<=0:raise ValueError('Shell orientation/volume is invalid')
        records.append(dict(name=name,outerVertices=len(outer),outerTriangles=len(t),shellTriangles=len(tri),nativeSurfaceOpenings=bound['openBoundaryLoops'],
            excludedInferredIslandFaces=discarded,rawGapMm=dict(min=float(raw_gap.min()),mean=float(raw_gap.mean()),max=float(raw_gap.max())),
            liningVertexNormalOffsetMm=clearance,minimumSampledLiningClearanceMm=lining_signed_min,
            minimumNormalWallMm=float(gap.min()),uniformOuterNormalLiftMm=lift,
            minimumSampledOuterClearanceMm=float(signed.min()),minimumSampledOuterDistanceMm=float(dist.min()),volumeMm3=float(volume),
            shellConnectivity=boundaries(tri)))
    combined=combine(shells,raw['boneNames']);surface=combine(surfaces,raw['boneNames'])
    np.savez_compressed(out/'underclothes-shell.npz',**combined)
    np.savez_compressed(out/'underclothes-relief-surface.npz',**surface)
    write_obj(out/'underclothes-shell.obj',combined['position'],combined['triangles'],combined['normal'],combined['uv'],combined['group'])
    write_obj(out/'underclothes-relief-surface.obj',surface['position'],surface['triangles'],surface['normal'],surface['uv'],surface['group'])
    np.testing.assert_allclose(combined['weights'].sum(-1),1,atol=1e-12)
    report=dict(sourceRawSha256=hashlib.sha256(raw_path.read_bytes()).hexdigest(),sourceBodySha256=hashlib.sha256(body_path.read_bytes()).hexdigest(),
         sourceBody=str(body_path.resolve()),components=records,totalShellVertices=len(combined['position']),totalShellTriangles=len(combined['triangles']),
         initialLiningOffsetMm=args.clearance,requiredSampledBodyClearanceMm=args.clearance*.5,assumedMinimumWallMm=args.wall,
         method='Existing smallclothes material regions supply the outline; raw 4x geometry supplies real relief. Matching source vertices and root-restricted projection align the lining to the 6 mm body.',
         limitations='Clearance and wall thickness are trial assumptions, not recovered physical garment thickness. Preliminary fit is sampled; run the crossing audit. No new straps, fastening, animation/physics validation or game-ready export.')
    (out/'extraction-report.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report,indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--raw',default='outputs/body-cleanup-4x-raw')
    p.add_argument('--body',default='outputs/body-separation-4x-s6');p.add_argument('--output',default='outputs/underclothes-extraction-4x')
    p.add_argument('--clearance',type=float,default=.15);p.add_argument('--wall',type=float,default=.2);a=p.parse_args()
    if a.clearance<=0 or a.wall<=0:p.error('Clearance and wall thickness must be positive')
    run(a)
