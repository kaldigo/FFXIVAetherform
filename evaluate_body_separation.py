"""Compare fabric suppression with shape errors after the same inward allowance."""
import json
from pathlib import Path
import numpy as np
from scipy.sparse import diags
from scipy.sparse.linalg import factorized
from body_cleanup import close_roots,cap_regions
from compare_seat_profiles import section
from investigate_coatee import front_depth
from clean_reference import normals
from separate_body_shape import surface_operator


def stats(x):
    x=np.asarray(x)
    return dict(meanMm=float(x.mean()),rmsMm=float(np.sqrt(np.mean(x*x))),p95AbsMm=float(np.percentile(abs(x),95)),maxAbsMm=float(abs(x).max()))


def curves(d):
    p=d['position'][d['triangles']];ys=np.arange(850.,981.,2.)
    outline=np.array([[q[:,2].min(),abs(q[:,0]).max()] for q in (section(p,y) for y in ys)])
    breast=[]
    for y in (1200,1220,1240,1260,1280):
        depth=[front_depth(p,(x,y)) for x in (-80,-60,-40,-20,0,20,40,60,80)]
        if not all(depth):raise ValueError('Missing breast profile intersection')
        breast.append([a['zMm'] for a in depth])
    return dict(y=ys,outline=outline,breast=np.array(breast))


def run():
    out=Path('outputs/body-separation-comparison');out.mkdir(parents=True,exist_ok=True)
    raw=dict(np.load('outputs/normal-map-wrap-4x/normal-map-reference.npz'))
    reference=np.load('outputs/body-separation-4x-s6/normal-map-reference.npz')
    directions,taper=reference['separationDirection'],reference['shapeTransition'];allowance=float(reference['inwardAllowanceMm'])
    offset=dict(raw);offset['position']=raw['position']-allowance*taper[:,None]*directions
    offset['normal']=normals(offset['position'],offset['triangles'])[offset['triangles']]
    fitted,_=close_roots(offset);np.savez_compressed(out/'offset-only-reference.npz',**fitted)
    baseline=curves(fitted)
    datasets={name:np.load(Path(folder)/'body-fit-reference.npz') for name,folder in (
        ('raw','outputs/body-cleanup-4x-raw'),('blurred','outputs/body-cleanup-4x-blur8'),('neutralised','outputs/body-cleanup-4x-neutral'),
        ('s6','outputs/body-separation-4x-s6'),('s12','outputs/body-separation-4x-s12'),('s20','outputs/body-separation-4x-s20'))}
    valid=~np.char.endswith(raw['group'],'_cap');mass,lap=surface_operator(raw['flatPosition'],raw['triangles'],valid)
    smooth=factorized((diags(mass)+9*lap).tocsc())
    cloth=np.zeros(len(taper),bool);cloth[raw['triangles'][valid&(np.char.find(raw['group'],'smallclothes')>=0)]]=True
    root_ids=set().union(*(region for _,region in cap_regions(raw)))
    cloth[np.unique(raw['triangles'][np.isin(raw['sourceFace'],list(root_ids))])]=False
    neutral=np.load('outputs/body-cleanup-4x-neutral/normal-map-reference.npz')['position']
    result={};archive=dict(offsetOutline=baseline['outline'],offsetBreast=baseline['breast'],profileY=baseline['y'])
    for name,d in datasets.items():
        profile=curves(d);archive[name+'Outline']=profile['outline'];archive[name+'Breast']=profile['breast']
        if name in ('raw','blurred','neutralised'):
            folder={'raw':'outputs/normal-map-wrap-4x','blurred':'outputs/body-cleanup-4x-blur8','neutralised':'outputs/body-cleanup-4x-neutral'}[name]
        else:folder='outputs/body-separation-4x-'+name
        before=np.load(Path(folder)/'normal-map-reference.npz');signal=np.sum((before['position']-neutral)*directions,axis=-1)
        if name.startswith('s'):signal+=allowance*taper
        high=signal-smooth(mass*signal)
        normal_detail=float(np.sqrt(np.average(high[cloth]**2,weights=mass[cloth])))
        outline_error=profile['outline']-baseline['outline'];breast_error=profile['breast']-baseline['breast']
        gap=profile['breast'][:,[2,6]].mean(1)-profile['breast'][:,4]
        gap0=baseline['breast'][:,[2,6]].mean(1)-baseline['breast'][:,4]
        result[name]=dict(rearCurveVsOffsetReference=stats(outline_error[:,0]),
            halfWidthCurveVsOffsetReference=stats(outline_error[:,1]),breastDepthVsOffsetReference=stats(breast_error),
            cleavageGapVsOffsetReference=stats(gap-gap0),cleavageGapMm=gap.tolist(),
            garmentHighBandRmsMm=normal_detail)
        # The displayed controls intentionally have no added inset. For a
        # fair shape-only comparison, also give every control the same trial
        # allowance before comparing it with the offset-only raw reference.
        equal=profile
        if name=='raw':equal=baseline
        elif name in ('blurred','neutralised'):
            inset=dict(before);inset['position']=before['position']-allowance*taper[:,None]*directions
            closed,_=close_roots(inset);equal=curves(closed)
        equal_gap=equal['breast'][:,[2,6]].mean(1)-equal['breast'][:,4]
        result[name]['equalAllowanceShapeError']=dict(
            rear=stats(equal['outline'][:,0]-baseline['outline'][:,0]),
            halfWidth=stats(equal['outline'][:,1]-baseline['outline'][:,1]),
            breast=stats(equal['breast']-baseline['breast']),cleavage=stats(equal_gap-gap0))
    for name,row in result.items():row['highBandReductionRelativeRaw']=1-row['garmentHighBandRmsMm']/result['raw']['garmentHighBandRmsMm']
    report=dict(inwardAllowanceMm=allowance,comparison='Raw geometry with the same inward allowance is the shape reference; this allows an inward shift without mislabelling it as a changed curve.',
                highBandDefinition='Signal minus a 3 mm surface Helmholtz low pass, on measured cloth vertices away from reconstructed roots. Explicit offset removed before this diagnostic.',
                variants=result)
    np.savez_compressed(out/'profile-data.npz',**archive)
    (out/'evaluation.json').write_text(json.dumps(report,indent=2)+'\n')
    for name,row in result.items():
        fair=row['equalAllowanceShapeError']
        print(name,'equal-allowance rear RMS',round(fair['rear']['rmsMm'],4),'breast RMS',round(fair['breast']['rmsMm'],4),
          'gap max',round(fair['cleavage']['maxAbsMm'],4),'detail reduction',round(row['highBandReductionRelativeRaw'],4))


if __name__=='__main__':run()
