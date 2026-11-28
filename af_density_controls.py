"""Calibrate mapping/wrap controls to measured AF triangles per surface area."""
import argparse,json
from pathlib import Path
import numpy as np
from reference_surface import weld,areas
from normal_tessellation import refine


def build(factor,output):
    r=np.load('outputs/vanilla-reference-v1/vanilla-reference.npz')
    af=json.loads(Path('outputs/vanilla-reference-v1/report.json').read_text())['afReferences']
    groups=r['sourceGroups'];v,t=weld(r['sourcePosition']);native_area=areas(v[t])
    slots=np.array([str(g).split('_')[0] for g in groups]);slot_names=('top','legs')
    mean={slot:af[slot]['meanTriangleAreaMm2'] for slot in slot_names}
    desired={slot:native_area[slots==slot].sum()/mean[slot]*factor for slot in slot_names}
    low={s:np.sqrt(mean[s]/factor) for s in slot_names}
    high={s:6*np.sqrt(mean[s]/factor) for s in slot_names}
    lengths={s:np.sqrt(mean[s]/factor/.15) for s in slot_names}
    best=None
    for iteration in range(12):
        limit=np.array([lengths[s] for s in slots]);nv,nt,parent,bc=refine(v,t,limit)
        count={s:int((slots[parent]==s).sum()) for s in slot_names}
        error=max(abs(count[s]/desired[s]-1) for s in slot_names)
        if best is None or error<best[0]:best=(error,nv,nt,parent,bc,dict(lengths),count)
        print('AF density',factor,'iteration',iteration+1,'counts',count,'maximum relative target error',round(error,5),flush=True)
        if error<.015:break
        for s in slot_names:
            if abs(count[s]/desired[s]-1)<.01:continue
            if count[s]>desired[s]:low[s]=lengths[s]
            else:high[s]=lengths[s]
            lengths[s]=(low[s]+high[s])/2
    error,nv,nt,parent,bc,lengths,count=best
    if error>.03:raise ValueError('Could not calibrate AF density within 3 percent')
    folder=Path(output);folder.mkdir(parents=True,exist_ok=True)
    np.savez_compressed(folder/'control.npz',position=nv,triangles=nt,sourceFace=parent,sourceBarycentric=bc)
    report=dict(role='mapping' if factor==1 else 'wrap',targetAFDensityFactor=factor,
                definition='Per-slot triangles per mm squared, relative to AF Small top and Medium B legs. This is not a topology match or an edge-length multiplier.',
                vertices=len(nv),triangles=len(nt),maximumRelativeDensityError=error,
                slots={s:dict(targetTriangleCount=desired[s],actualTriangleCount=count[s],
                              sourceSurfaceAreaMm2=float(native_area[slots==s].sum()),afMeanTriangleAreaMm2=mean[s],
                              measuredFlatAFDensityFactor=count[s]/native_area[slots==s].sum()*mean[s],maxEdgeMm=lengths[s]) for s in slot_names})
    (folder/'density-report.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report,indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--factor',type=float,choices=(1.,2.,4.),required=True);p.add_argument('--output',required=True)
    a=p.parse_args();build(a.factor,a.output)
