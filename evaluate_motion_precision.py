"""Check normalized byte-weight export precision through controlled motion."""
import json
from pathlib import Path
import numpy as np
from body_motion import load_rig,vertex_weights,packed_weights,deformation_metrics,CASES
from evaluate_body_motion import subset_crossings,region_metrics


def run():
    folder=Path('outputs/body-motion-review-v1');d=np.load('outputs/bra-postprocess-v5/body-fit-reference.npz');rig=load_rig()
    weights=vertex_weights(d);packed,lost=packed_weights(weights)
    mask=d['originalWorkingFace']<0;results=[]
    for case in CASES:
        maximum=0.;folds=0;collapsed=0;peak_crossings=0
        peak=.25 if any(k.startswith('oscillating') for k in case) else .5
        for phase in np.linspace(0,1,17):
            world=rig.world(case,float(phase));full,_=rig.skin(d['position'],weights,d['boneNames'],world)
            q,blend=rig.skin(d['position'],packed,d['boneNames'],world);maximum=max(maximum,float(np.linalg.norm(q-full,axis=1).max()))
            metrics=deformation_metrics(d['position'],q,d['triangles'],blend);r=region_metrics(metrics,mask)
            folds=max(folds,r['foldedTriangles']);collapsed=max(collapsed,r['nearCollapsedTriangles'])
            if abs(phase-peak)<1e-8:peak_crossings=len(subset_crossings(q[d['triangles']],d['triangles'],mask))
        row=dict(clip=case['id'],maximumByteWeightPositionDifferenceMm=maximum,foldedNewPatchTriangles=folds,collapsedNewPatchTriangles=collapsed,peakNewPatchCrossings=peak_crossings)
        results.append(row);print(row,flush=True)
    report=dict(maximumInfluences=int((weights>1e-8).sum(1).max()),maximumTopEightDiscardedWeight=float(lost.max()),clips=results,
        scope='Largest-remainder 8-slot weights packed to sum 255. Full animation-frame geometry metrics; patch crossing check at the declared peak. Does not constitute a native MDL/in-game test.')
    (folder/'byte-weight-motion-report.json').write_text(json.dumps(report,indent=2)+'\n')


if __name__=='__main__':run()
