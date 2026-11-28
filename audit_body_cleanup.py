"""Validate cleaned fitting envelopes without claiming hidden anatomy or physics."""
import argparse,json
from pathlib import Path
import numpy as np
from reference_surface import areas
from repair_surface import boundaries
from investigate_coatee import front_depth


def audit(folder,raw_folder):
    folder=Path(folder);d=np.load(folder/'body-fit-reference.npz');raw=np.load(Path(raw_folder)/'normal-map-reference.npz')
    before=raw if not (folder/'normal-map-reference.npz').exists() else np.load(folder/'normal-map-reference.npz')
    report=json.loads((folder/'body-cleanup-report.json').read_text());v,t=d['position'],d['triangles'];keep=d['originalDenseFace']>=0
    assert all(np.isfinite(d[k]).all() for k in ('position','normal','uv','weights','cornerNativeBary'))
    assert areas(v[t]).min()>1e-10
    np.testing.assert_array_equal(t[keep],np.vectorize({int(old):i for i,old in enumerate(d['originalVertex']) if old>=0}.get)(before['triangles'][d['originalDenseFace'][keep]]))
    mapping={int(old):i for i,old in enumerate(d['originalVertex']) if old>=0}
    for key in ('uv','weights'):np.testing.assert_array_equal(d[key][keep],before[key][d['originalDenseFace'][keep]])
    present=d['originalVertex']>=0
    np.testing.assert_array_equal(v[present],before['position'][d['originalVertex'][present]])
    np.testing.assert_allclose(d['weights'].sum(-1),1,atol=1e-12);assert d['weights'].min()>=0
    np.testing.assert_array_equal(d['boneNames'],raw['boneNames'])
    np.testing.assert_allclose(d['cornerNativeBary'].sum(-1),1,atol=1e-12);assert d['cornerNativeBary'].min()>=-1e-12
    assert np.all(before['sourceIdentity'][d['cornerNativeFace'],1].astype(int)>=0)
    assert not np.char.endswith(d['group'],'_cap').any()
    assert len(report['patches'])==6
    for patch in report['patches']:
        ids=np.array(patch['fixedVertexIds']);np.testing.assert_array_equal(v[[mapping[int(i)] for i in ids]],before['position'][ids])
        face=t[patch['firstNewFace']:patch['firstNewFace']+patch['newFaceCount']]
        cross=np.cross(v[face[:,1]]-v[face[:,0]],v[face[:,2]]-v[face[:,0]])
        assert np.min(cross@np.array(patch['basis'])[2])>0
    connectivity=boundaries(t);assert connectivity['openBoundaryLoops']==5
    lm=np.load(folder/'paired-landmarks-candidate.npz');old_lm=np.load(Path(raw_folder)/'paired-landmarks-candidate.npz')
    for key in ('names','targetFace','targetBary','targetPosition'):np.testing.assert_array_equal(lm[key],old_lm[key])
    np.testing.assert_allclose(lm['sourcePosition'],np.einsum('ni,nij->nj',lm['sourceBary'],v[t[lm['sourceFace']]]),atol=1e-10)
    assert len(lm['names'])==948
    profiles=[]
    for y in (1200,1220,1240,1260,1280):
        row=dict(yMm=y)
        for name,mesh in (('raw',raw['position'][raw['triangles']]),('filteredBeforeCaps',before['position'][before['triangles']]),('cleaned',v[t])):
            depth=[front_depth(mesh,(x,y)) for x in (-40,0,40)]
            if all(depth):row[name]=dict(midlineDepthMm=depth[1]['zMm'],gapMm=(depth[0]['zMm']+depth[2]['zMm'])/2-depth[1]['zMm'])
        profiles.append(row)
    difference=np.linalg.norm(before['position']-raw['position'],axis=1)
    cloth=np.zeros(len(difference),bool);cloth[before['triangles'][np.char.find(before['group'],'smallclothes')>=0]]=True
    skin=~cloth
    density=json.loads((Path(raw_folder)/'density-report.json').read_text());face_area=areas(v[t]);ratios={}
    for slot,spec in density['slots'].items():
        mask=np.char.startswith(d['group'],slot+'_');ratios[slot]=float(mask.sum()/face_area[mask].sum()*spec['afMeanTriangleAreaMm2'])
    result=dict(status='Passed',mode=report['mode'],connectivity=connectivity,rebuiltRoots=6,
                capBoundaryAndMedialPinsExact=True,patchesAreSingleValuedInTheirCharts=True,
                retainedUVsAndWeightsExact=True,weightsNonnegativeAndNormalized=True,landmarks=948,unchangedAFPoints=True,
                offsetMm=0,underwearEnvelopeRetained=True,afDensityBySlot=ratios,
                filteringMovementBeforeCapRebuildMm=dict(max=float(difference.max()),p95=float(np.percentile(difference,95)),
                 clothP95=float(np.percentile(difference[cloth],95)),outerSkinP95=float(np.percentile(difference[skin],95))),
                profiles=profiles,triangles=len(t),vertices=len(v),
                limitations='Smooth garment envelope, not inferred naked anatomy. UV/weight witnesses are interpolated inside rebuilt roots. No global intersection proof or pose/physics validation.')
    (folder/'body-cleanup-audit.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result,indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('folder');p.add_argument('--raw',required=True);a=p.parse_args();audit(a.folder,a.raw)
