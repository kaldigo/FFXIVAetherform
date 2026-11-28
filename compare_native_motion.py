"""Separate native pose limitations, subdivision skinning, and reconstructed relief."""
import argparse,copy,hashlib,json
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont
from reference_surface import weld,unit,areas
from normal_field import NormalField
from body_motion import load_rig,vertex_weights,deformation_metrics,CASES
from evaluate_body_motion import region_metrics
from mesh_crossings import crossing_pairs
from clean_reference import normals
from surface_diagnostics import raster


def carry_surface(rest,posed,parents,bary,position,triangles):
    """Move a surface using source-triangle affine tangent motion and unit normal height.

    Native triangles are posed first. Normal offsets retain their signed height;
    there is no claim of volume preservation. Adjacent-face predictions are
    averaged at shared dense vertices, so the output has no duplicate seams.
    """
    def frame(p):
        a,b=p[:,1]-p[:,0],p[:,2]-p[:,0]
        return np.stack((a,b,unit(np.cross(a,b))),axis=-1)
    affine=frame(posed)@np.linalg.inv(frame(rest))
    base=np.einsum('fci,fcij->fcj',bary,rest[parents])
    moved=np.einsum('fci,fcij->fcj',bary,posed[parents])
    prediction=moved+np.einsum('fcij,fcj->fci',affine[parents],position[triangles]-base)
    out=np.zeros_like(position);count=np.bincount(triangles.ravel(),minlength=len(position))
    np.add.at(out,triangles.ravel(),prediction.reshape(-1,3));out/=count[:,None]
    disagreement=np.linalg.norm(prediction-out[triangles],axis=-1)
    return out,disagreement


def native_sample(posed,parents,bary,triangles,count):
    values=np.einsum('fci,fcij->fcj',bary,posed[parents]);out=np.zeros((count,3))
    np.add.at(out,triangles.ravel(),values.reshape(-1,3))
    out/=np.bincount(triangles.ravel(),minlength=count)[:,None]
    return out


def masks(d):
    c=d['position'][d['triangles']].mean(1)
    valid=~np.char.endswith(d['group'],'_cap')
    if 'nativeFaceValid' in d:valid&=d['nativeFaceValid']
    return dict(hip_crotch=valid&(c[:,1]>800)&(c[:,1]<980),
                knees=valid&(c[:,1]>440)&(c[:,1]<680))


def run(args):
    out=Path(args.output);out.mkdir(parents=True,exist_ok=True);rig=load_rig()
    source=dict(np.load('outputs/vanilla-reference-v1/vanilla-reference.npz'));field=NormalField(source)
    nv,nt=weld(source['sourcePosition'])
    native=dict(position=nv,triangles=nt,weights=field.weights,boneNames=source['boneNames'],group=source['sourceGroups'])
    control=dict(np.load('outputs/normal-map-wrap-4x/normal-map-reference.npz'))
    flat=dict(control,position=control['controlPosition'])
    raw=dict(np.load('outputs/body-cleanup-4x-raw/body-fit-reference.npz'))
    working=dict(np.load('outputs/bra-postprocess-v5/body-fit-reference.npz'))
    original_valid=source['sourceIdentity'][:,1].astype(int)>=0
    native['nativeFaceValid']=original_valid
    for d in (control,flat):d['nativeFaceValid']=original_valid[d['sourceFace']]
    raw['nativeFaceValid']=original_valid[raw['cornerNativeFace']].all(1)
    working['nativeFaceValid']=np.ones(len(working['triangles']),bool)
    retained=working['originalWorkingFace']>=0
    working['nativeFaceValid'][retained]=raw['nativeFaceValid'][working['originalWorkingFace'][retained]]
    datasets=dict(native=native,flat4x=flat,reconstructed4x=control,root_cleaned4x=raw,working4x=working)
    weights={k:vertex_weights(d) for k,d in datasets.items()};regions={k:masks(d) for k,d in datasets.items()}
    # Both mappings are exact material coordinates in the same native source.
    raw_parents=raw['cornerNativeFace'];raw_bary=raw['cornerNativeBary']
    flat_parents=np.repeat(control['sourceFace'][:,None],3,axis=1);flat_bary=control['sourceBarycentric']
    rest_carry,_=carry_surface(source['sourcePosition'],source['sourcePosition'],raw_parents,raw_bary,raw['position'],raw['triangles'])
    np.testing.assert_allclose(rest_carry,raw['position'],atol=1e-9)
    report=dict(scope='Identical controlled native-rig transforms, original retained source geometry and weights. Generated caps excluded from regional metrics. Not game animation/physics playback.',
        nativeVertices=len(nv),sourceTrianglesIncludingClosures=len(nt),retainedOriginalTriangles=int(original_valid.sum()),framesPerClip=17,clips=[],
        sourceHashes={p:hashlib.sha256(Path(p).read_bytes()).hexdigest() for p in ['outputs/vanilla-reference-v1/vanilla-reference.npz','outputs/normal-map-wrap-4x/normal-map-reference.npz','outputs/body-cleanup-4x-raw/body-fit-reference.npz','outputs/bra-postprocess-v5/body-fit-reference.npz']})
    for name in args.clips.split(','):
        case=next(c for c in CASES if c['id']==name);rows=[];peak=.25 if any(k.startswith('oscillating') for k in case) else .5
        for phase in np.linspace(0,1,17):
            world=rig.world(case,float(phase));posed={};blends={};row=dict(phase=float(phase),meshes={})
            for k,d in datasets.items():
                q,blend=rig.skin(d['position'],weights[k],d['boneNames'],world);posed[k]=q;blends[k]=blend
                metrics=deformation_metrics(d['position'],q,d['triangles'],blend)
                row['meshes'][k]={reg:region_metrics(metrics,mask) for reg,mask in regions[k].items()}
            nq=posed['native'][nt]
            inherited=native_sample(nq,flat_parents,flat_bary,flat['triangles'],len(flat['position']))
            carried,disagreement=carry_surface(source['sourcePosition'],nq,raw_parents,raw_bary,raw['position'],raw['triangles'])
            posed['native_carried4x']=carried
            metrics=deformation_metrics(raw['position'],carried,raw['triangles'],blends['root_cleaned4x'])
            row['meshes']['native_carried4x']={reg:region_metrics(metrics,mask) for reg,mask in regions['root_cleaned4x'].items()}
            error=np.linalg.norm(posed['flat4x']-inherited,axis=1)
            row['subdivisionMotionDifferenceMm']={}
            for reg,mask in regions['flat4x'].items():
                ids=np.unique(flat['triangles'][mask]);e=error[ids]
                row['subdivisionMotionDifferenceMm'][reg]=dict(maximum=float(e.max()),p95=float(np.percentile(e,95)),rms=float(np.sqrt(np.mean(e*e))))
            if abs(phase-peak)<1e-8:
                peak_info={}
                for k,q in posed.items():
                    d=raw if k=='native_carried4x' else datasets[k];t=d['triangles']
                    pairs=crossing_pairs(q[t],q[t],self_faces=t)
                    valid=d['nativeFaceValid'];eligible=pairs[valid[pairs].all(1)]
                    reginfo={}
                    for reg,mask in regions['root_cleaned4x' if k=='native_carried4x' else k].items():
                        hit=np.unique(eligible[np.any(mask[eligible],axis=1)])
                        affected=hit[mask[hit]];rest_area=areas(d['position'][t])
                        reginfo[reg]=dict(intersectingPairs=int(np.any(mask[eligible],axis=1).sum()),affectedRestAreaMm2=float(rest_area[affected].sum()),regionRestAreaMm2=float(rest_area[mask].sum()))
                    peak_info[k]=dict(crossingPairs=len(pairs),originalLineageOnlyCrossingPairs=len(eligible),regions=reginfo)
                    np.savez_compressed(out/(name+'-'+k+'-peak.npz'),position=q,selfPairs=pairs)
                row['peak']=peak_info
                # Original normal map through posed authored N/T/B, for a native-shading control.
                pf=copy.copy(field);bm=blends['native'][nt]
                for attr in ('n','t','b'):
                    original=getattr(field,attr);value=np.einsum('fcij,fcj->fci',bm,original)
                    length=np.linalg.norm(value,axis=-1,keepdims=True);value/=np.maximum(length,1e-12);setattr(pf,attr,value)
                render(name,out,datasets,posed,pf,raw)
            rows.append(row)
        report['clips'].append(dict(case=case,frames=rows));(out/'native-motion-report.json').write_text(json.dumps(report,indent=2)+'\n')
        peak_result=next(r['peak'] for r in rows if 'peak'in r)
        print('Completed',name,{k:v['originalLineageOnlyCrossingPairs'] for k,v in peak_result.items()},flush=True)


def render(name,out,datasets,posed,field,raw):
    columns=[('native','Original + actual normals'),('flat4x','Flat subdivision / skinning'),('root_cleaned4x','Raw reconstruction / skinning'),('working4x','Current working candidate')]
    # Framing follows the actual posed native surface region, rather than fixed rest bounds.
    sheet=Image.new('RGB',(1600,910),'#182129');draw=ImageDraw.Draw(sheet);font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',17)
    for row,(region,label) in enumerate((('hip_crotch','Hip / crotch'),('knees','Knees'))):
        nd=datasets['native'];mask=masks(nd)[region];pts=posed['native'][np.unique(nd['triangles'][mask])]
        lo=pts[:,[2,1]].min(0)-25;hi=pts[:,[2,1]].max(0)+25
        centre=(lo+hi)/2;extent=max(hi-lo)*1.1;bounds=(centre[0]-extent/2,centre[0]+extent/2,centre[1]-extent/2,centre[1]+extent/2)
        for col,(key,title) in enumerate(columns):
            d=raw if key=='native_carried4x' else datasets[key];q=posed[key];t=d['triangles']
            tile=raster(q[t],'side',bounds,(400,400),normal_sampler=field.sample if key=='native' else None,
                vertex_normals=None if key=='native' else normals(q,t)[t])[0]
            x=col*400;y=row*455;sheet.paste(tile,(x,y+52));draw.text((x+8,y+5),title,font=font,fill='white');draw.text((x+8,y+28),name+' / '+label,font=font,fill='white')
    sheet.save(out/(name+'-comparison.png'))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--output',default='outputs/native-motion-comparison-v1');p.add_argument('--clips',default='sit,crouch,lunge');run(p.parse_args())
