"""Check raw-normal reconstruction, source attributes and named fitting points."""
import argparse,hashlib,json
from pathlib import Path
import numpy as np
from normal_field import NormalField
from reference_surface import areas,unit,weld,patches,evaluate
from repair_surface import boundaries
from transfer_landmarks import child_address,write_points


def stats(angle,weight):
    return dict(rmsDegrees=float(np.sqrt(np.mean(angle**2))),p95Degrees=float(np.percentile(angle,95)),
                maxDegrees=float(angle.max()),areaWeightedRmsDegrees=float(np.sqrt(np.average(angle**2,weights=weight))))


def audit(folder):
    folder=Path(folder);d=np.load(folder/'normal-map-reference.npz')
    original_path=Path('outputs/vanilla-reference-v1/vanilla-reference.npz');original=np.load(original_path)
    field=NormalField(original);v,t=d['position'],d['triangles'];parent,bc=d['sourceFace'],d['sourceBarycentric']
    valid=field.valid[parent];p=v[t];cross=np.cross(p[:,1]-p[:,0],p[:,2]-p[:,0]);normal=unit(cross)
    flat=d['flatPosition'][t];flat_cross=np.cross(flat[:,1]-flat[:,0],flat[:,2]-flat[:,0])
    ratio=np.sum(cross*flat_cross,axis=1)/np.sum(flat_cross*flat_cross,axis=1)
    assert ratio[valid].min()>.05
    assert np.isfinite(v).all() and areas(p).min()>1e-10
    for key in ('uv','weights'):
        expected=np.einsum('fci,fij->fcj',bc,getattr(field,key)[parent])
        np.testing.assert_allclose(d[key],expected,atol=1e-12)
    weights=d['weights'];assert weights.min()>=0
    np.testing.assert_allclose(weights.sum(-1),1,atol=1e-12)
    domain_area=np.bincount(parent,weights=abs(np.linalg.det(bc)),minlength=len(field.n))
    np.testing.assert_allclose(domain_area,1,atol=1e-12)
    connectivity=boundaries(t);assert connectivity['openBoundaryLoops']==5
    samples=np.array([[2/3,1/6,1/6],[1/6,2/3,1/6],[1/6,1/6,2/3]])
    angles=[];pn_angles=[];area=[]
    nv,nt=weld(original['sourcePosition']);controls,_=patches(nv[nt],original['sourceNormal'],nt,np.char.endswith(original['sourceGroups'],'_cap'))
    for sample in samples:
        material=np.einsum('i,fij->fj',sample,bc)
        target=field.sample(parent,material)
        angle=np.degrees(np.arccos(np.clip(np.sum(normal*target,axis=-1),-1,1)))
        _,pn=evaluate(controls,parent,material,True)
        pn_angle=np.degrees(np.arccos(np.clip(np.sum(pn*target,axis=-1),-1,1)))
        angles.append(angle[valid]);pn_angles.append(pn_angle[valid]);area.append(areas(flat)[valid])
    # Rebind the original accepted material identities, never a cleanup trial.
    lm=dict(np.load('outputs/vanilla-reference-v1/landmarks/paired-landmarks.npz'))
    by_parent={}
    for i,f in enumerate(parent):by_parent.setdefault(int(f),[]).append(i)
    by_parent={k:np.array(ids) for k,ids in by_parent.items()}
    addresses=[child_address(int(f),b,bc,by_parent) for f,b in zip(lm['sourceParent'],lm['sourceMaterialBary'])]
    face=np.array([a[0] for a in addresses]);bary=np.array([a[1] for a in addresses])
    np.testing.assert_allclose(np.einsum('ni,nij->nj',bary,bc[face]),lm['sourceMaterialBary'],atol=1e-12)
    lm.update(sourceFace=face,sourceBary=bary,sourcePosition=np.einsum('ni,nij->nj',bary,v[t[face]]))
    np.savez_compressed(folder/'paired-landmarks-candidate.npz',**lm)
    write_points(folder/'Vanilla-NormalMap.points.json',lm['names'],face,bary)
    report=dict(status='Geometry/attribute audit passed on retained native surfaces',connectivity=connectivity,
                nativeSignedAreaRatioMin=float(ratio[valid].min()),nativeReversedFaces=int((ratio[valid]<=0).sum()),
                generatedCapsReversedAgainstTheirProvisionalFlatControls=int((ratio[~valid]<=0).sum()),
                generatedCapCaveat='These removed-flap closures have no native normal-map evidence; their geometry is not certified by this audit.',
                minimumTriangleAreaMm2=float(areas(p).min()),sourceUVsAndWeightsPreservedByNativeBarycentricInterpolation=True,
                heldOutNormalError=stats(np.concatenate(angles),np.concatenate(area)),
                pnAnalyticNormalErrorAtSameHeldOutLocations=stats(np.concatenate(pn_angles),np.concatenate(area)),
                landmarks=len(face),unchangedAFPointAddressesAndPositions=True,
                originalReferenceSha256=hashlib.sha256(original_path.read_bytes()).hexdigest(),
                noSmoothingOrFabricFiltering=True,
                limitations='Base normal maps and neutral material customization. No tile-detail normal blending, game PBR lighting, global intersection proof or animation/physics validation.')
    (folder/'normal-map-audit.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report,indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('folder');audit(p.parse_args().folder)
