"""Sample controlled vanilla-rig animations and compare body deformation."""
import argparse,json,time,hashlib
from pathlib import Path
import numpy as np
from scipy.spatial import cKDTree
from body_motion import load_rig,vertex_weights,deformation_metrics,CASES
from mesh_crossings import paired_intersections,crossing_pairs


def subset_crossings(p,t,selected):
    centre=p.mean(1);radius=np.linalg.norm(p-centre[:,None],axis=-1).max(1);tree=cKDTree(centre)
    lower,upper=p.min(1),p.max(1);selected=np.flatnonzero(selected);result=[]
    for start in range(0,len(selected),300):
        ids=selected[start:start+300];neighbors=tree.query_ball_point(centre[ids],radius[ids]+radius.max()+1e-8)
        a=np.repeat(ids,[len(x) for x in neighbors]);b=np.concatenate(neighbors).astype(int)
        keep=np.all(lower[a]<=upper[b]+1e-8,axis=1)&np.all(lower[b]<=upper[a]+1e-8,axis=1)
        keep&=~np.any(t[a,:,None]==t[b,None,:],axis=(1,2));a,b=a[keep],b[keep]
        if len(a):
            hit=paired_intersections(p[a],p[b]);result.extend(zip(a[hit],b[hit]))
    return np.unique(np.sort(np.array(result,dtype=int).reshape(-1,2),axis=1),axis=0)


def region_metrics(metrics,mask):
    return dict(foldedTriangles=int(np.sum(metrics['signedArea'][mask]<-1e-6)),
        nearCollapsedTriangles=int(np.sum(metrics['minStretch'][mask]<.05)),
        minimumSignedAreaRatio=float(metrics['signedArea'][mask].min()),
        minimumStretch=float(metrics['minStretch'][mask].min()),maximumStretch=float(metrics['maxStretch'][mask].max()),
        p99Stretch=float(np.percentile(metrics['maxStretch'][mask],99)))


def run(args):
    output=Path(args.output);output.mkdir(parents=True,exist_ok=True);rig=load_rig(args.rig)
    report_path=Path(args.candidate)/'postprocess-report.json';post_report=json.loads(report_path.read_text())
    old_edited=np.zeros(95630,bool)
    for record in post_report['patches']+post_report['seamRegions']:old_edited[record['oldFaces']]=True
    names=['raw','working','postprocessed'];folders=['outputs/body-cleanup-4x-raw','outputs/working-body-4x',args.candidate]
    meshes=[]
    for name,folder in zip(names,folders):
        path=Path(folder)/'body-fit-reference.npz';d=dict(np.load(path));weights=vertex_weights(d)
        if name=='postprocessed':
            retained=d['originalWorkingFace']>=0;edited=~retained;edited[retained]=old_edited[d['originalWorkingFace'][retained]]
        else:edited=old_edited.copy()
        c=d['position'][d['triangles']].mean(1);crotch=(abs(c[:,0])<40)&(c[:,1]>800)&(c[:,1]<900)&(c[:,2]>-40)&(c[:,2]<80)
        rest,_=rig.skin(d['position'],weights,d['boneNames'],rig.rest)
        np.testing.assert_allclose(rest,d['position'],atol=1e-9,rtol=0)
        meshes.append(dict(name=name,data=d,weights=weights,edited=edited,crotch=crotch,sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
    phases=np.linspace(0,1,args.frames);all_rows=[];start=time.time()
    if args.resume and (output/'motion-report.json').exists():
        previous=json.loads((output/'motion-report.json').read_text())
        if previous['skeletonSha256']!=rig.data.get('sha256') or previous['framesPerClip']!=args.frames:raise ValueError('Resume source mismatch')
        if previous['sources']!=[dict(name=m['name'],sha256=m['sha256']) for m in meshes]:raise ValueError('Resume mesh mismatch')
        all_rows=previous['clips']
    cases=CASES if not args.clips else [c for c in CASES if c['id'] in args.clips.split(',')]
    if args.resume:cases=[c for c in cases if c['id'] not in {r['case']['id'] for r in all_rows}]
    for case in cases:
        case_rows=[];peak_phase=.25 if any(k.startswith('oscillating') for k in case) else .5
        full_peak={}
        for frame,phase in enumerate(phases):
            world=rig.world(case,float(phase));rows=[];posed={}
            for mesh in meshes:
                d=mesh['data'];q,blend=rig.skin(d['position'],mesh['weights'],d['boneNames'],world);posed[mesh['name']]=q
                metrics=deformation_metrics(d['position'],q,d['triangles'],blend)
                rows.append(dict(mesh=mesh['name'],body=region_metrics(metrics,np.ones(len(d['triangles']),bool)),
                    editedChest=region_metrics(metrics,mesh['edited']),crotch=region_metrics(metrics,mesh['crotch'])))
            mesh=meshes[-1];d=mesh['data'];pairs=subset_crossings(posed['postprocessed'][d['triangles']],d['triangles'],mesh['edited'])
            baseline=[]
            if len(pairs):
                for mesh in meshes[:-1]:
                    dd=mesh['data'];pp=subset_crossings(posed[mesh['name']][dd['triangles']],dd['triangles'],mesh['edited'])
                    baseline.append(dict(mesh=mesh['name'],crossings=len(pp)))
                np.savez_compressed(output/(case['id']+f'-frame{frame:02d}-crossings.npz'),pairs=pairs)
            row=dict(frame=frame,phase=float(phase),meshes=rows,editedPatchCrossings=len(pairs),baselineEditedCrossings=baseline)
            case_rows.append(row)
            if abs(phase-peak_phase)<1e-8:
                for mesh in (meshes[0],meshes[-1]):
                    dd=mesh['data'];pp=crossing_pairs(posed[mesh['name']][dd['triangles']],posed[mesh['name']][dd['triangles']],self_faces=dd['triangles'])
                    full_peak[mesh['name']]=len(pp)
                    np.savez_compressed(output/(case['id']+'-'+mesh['name']+'-peak.npz'),position=posed[mesh['name']],selfPairs=pp,phase=phase)
            if frame%4==0:print(case['id'],'frame',frame,'/',args.frames-1,'patch crossings',len(pairs),'seconds',round(time.time()-start,1),flush=True)
        summary=dict(clip=case['id'],maximumEditedCrossings=max(r['editedPatchCrossings'] for r in case_rows),fullBodyPeakCrossings=full_peak,
            maximumPostprocessedFoldedChest=max(r['meshes'][-1]['editedChest']['foldedTriangles'] for r in case_rows),
            maximumPostprocessedCollapsedChest=max(r['meshes'][-1]['editedChest']['nearCollapsedTriangles'] for r in case_rows))
        all_rows.append(dict(case=case,summary=summary,frames=case_rows));print('CLIP',summary,flush=True)
        result=dict(scope='Controlled procedural animations on installed vanilla c0201 rig, full named-weight linear skinning. Not extracted game clips, live gameplay, Havok physics, racial PBD or Customize+ validation.',
            skeletonSource=rig.data.get('source'),skeletonSha256=rig.data.get('sha256'),framesPerClip=args.frames,
            sources=[dict(name=m['name'],sha256=m['sha256']) for m in meshes],clips=all_rows,
            definitions='Fold compares actual normal with the inverse-transpose blended-bind normal, not a fixed rest normal. Near-collapse means minimum local tangent stretch <0.05. Edited patch crossings exclude triangles sharing vertices. Full-body self checks are at the declared peak only.')
        (output/'motion-report.json').write_text(json.dumps(result,indent=2)+'\n')
    print('Completed',len(cases),'controlled clips in',round(time.time()-start,1),'seconds',flush=True)


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--candidate',default='outputs/bra-postprocess-v5')
    p.add_argument('--rig',default='outputs/motion-assets/vanilla-c0201-rig.json');p.add_argument('--output',default='outputs/body-motion-review-v1')
    p.add_argument('--frames',type=int,default=17);p.add_argument('--clips');p.add_argument('--resume',action='store_true');run(p.parse_args())
