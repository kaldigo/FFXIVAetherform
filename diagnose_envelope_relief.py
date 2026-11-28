"""Measure local dip/bump changes in the reviewed upper outer seat patches."""
import json
from pathlib import Path
import numpy as np
from scipy.sparse.csgraph import dijkstra
from clean_reference import graph
from separate_body_shape import surface_operator
from fit_inner_envelope import quadratic_intercept
from reference_surface import unit


def run():
    folder=Path('outputs/body-inner-envelope-4x-collar12')
    raw=np.load('outputs/body-cleanup-4x-raw/body-fit-reference.npz');new=np.load(folder/'body-fit-reference.npz')
    v,p,t=raw['position'],new['position'],raw['triangles'];n=new['envelopeDirection']
    mass,_=surface_operator(v,t);_,metric=graph(v,t)
    region=(abs(v[:,0])>100)&(abs(v[:,0])<145)&(v[:,1]>940)&(v[:,1]<980)&(v[:,2]<-8)&(v[:,2]>-70)
    records=[]
    for i in np.flatnonzero(region):
        distance=dijkstra(metric,directed=False,indices=int(i),limit=25.)
        ids=np.flatnonzero((distance<25)&((n@n[i])>.6))
        if len(ids)<12:continue
        u=unit(np.cross(n[i],[0.,1.,0.]));w=np.cross(n[i],u);frame=np.array([u,w,n[i]]).T
        weights=mass[ids]*np.exp(-(distance[ids]/12.)**2);height=[]
        for mesh in (v,p):
            intercept,_=quadratic_intercept((mesh[ids]-mesh[i])@frame/25.,weights)
            height.append(float(intercept*25))
        records.append(dict(vertex=int(i),rawPositionMm=v[i].tolist(),rawDipMm=height[0],newDipMm=height[1],
                            inwardDisplacementMm=float(new['envelopeNormalShiftMm'][i])))
    flips=[r for r in records if r['rawDipMm']>.05 and r['newDipMm']<-.05]
    report=dict(region='Upper outer rear seat: 100<abs(X)<145, 940<Y<980, -70<Z<-8 mm',
        definition='Positive dip means the vertex is below its locally fitted quadratic. Raw/new use the same vertex neighbourhood, raw geodesic weights and fitting frame; each fits its own quadratic. Radius 25 mm, weight Gaussian 12 mm. These are local trend diagnostics, not measured hidden anatomy.',
        vertices=len(records),dipToBumpThresholdMm=.05,dipToBumpVertices=len(flips),
        flips=flips,allSamples=records,
        conclusion='Nonpositive displacement relative to raw does not prevent fabric-scale curvature reversal relative to the surrounding surface. The smooth correction objective does not directly constrain final surface curvature.')
    (folder/'seat-relief-diagnosis.json').write_text(json.dumps(report,indent=2)+'\n')
    print(json.dumps({k:value for k,value in report.items() if k not in ('allSamples','flips')},indent=2))


if __name__=='__main__':run()
