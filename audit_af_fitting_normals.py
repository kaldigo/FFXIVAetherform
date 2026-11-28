"""Independently recalculate AF fitting normals without changing its geometry."""
import json
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont
from scipy.spatial.transform import Rotation
from audit_envelope_normals import independently_recalculate,read_export,angular
from reference_surface import write_obj,unit
from surface_diagnostics import raster


def run():
    out=Path('outputs/af-fitting-chest-v1');d=dict(np.load(out/'af-fitting-reference.npz'));v,t=d['position'],d['triangles']
    fresh=independently_recalculate(v,t);ov,ot,on=read_export(out/'AF-Fitting-Small-MediumB.obj')
    np.testing.assert_array_equal(ot,t);np.testing.assert_allclose(ov,v,atol=5.1e-10,rtol=0)
    np.testing.assert_allclose(d['normal'],fresh,atol=1e-10,rtol=0);np.testing.assert_allclose(on,fresh,atol=5.1e-10,rtol=0)
    write_obj(out/'AF-Fitting-Fresh-Normals.obj',v,t,fresh,d['uv'],d['group'])
    patches=json.loads((out/'af-fitting-chest-report.json').read_text())['patches'];variation=[]
    for patch in patches:
        local=(v-np.array(patch['originMm']))@np.array(patch['frame']);r=np.linalg.norm(local[:,:2],axis=1)
        core=(r<6)&(abs(local[:,2])<15);ids=np.flatnonzero(core);n=np.zeros_like(v);n[t.ravel()]=fresh.reshape(-1,3)
        span=angular(n[ids,None,:],n[None,ids,:]).max()
        variation.append(dict(side=patch['side'],coreRadiusMm=6,maximumCoreNormalSeparationDegrees=float(span)))
    report=dict(normalsAlreadyUpdated=True,maximumStoredNormalVectorError=float(np.linalg.norm(d['normal']-fresh,axis=-1).max()),
        maximumObjNormalAngleErrorDegrees=float(angular(unit(on),independently_recalculate(ov,ot)).max()),geometryChanged=False,normalMapApplied=False,patchVariation=variation,
        conclusion='Stored and exported normals already match independent atan2-based geometric recalculation. A fresh export does not change the surface or its shading. The inner region has varying normals; it is not a planar cap. Remaining flat-looking appearance cannot be attributed to stale normals.')
    (out/'af-normal-verification.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report,indent=2))
    im=Image.new('RGB',(1500,1050),'#182129');draw=ImageDraw.Draw(im);font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',19)
    # Inspect the breast close up under two orientations of the same light.
    pivot=np.array([72.,1244.,105.]);bounds=(42,102,1215,1275)
    for row,angle in enumerate((0,-35)):
        rotation=Rotation.from_euler('y',angle,degrees=True).as_matrix();q=(v-pivot)@rotation.T+pivot
        for col,(label,norm) in enumerate((('Saved smooth normals',d['normal']),('Independent fresh normals',fresh),('Flat geometric normals',None))):
            rn=None if norm is None else norm@rotation.T
            img=raster(q[t],'front',bounds,(500,470),vertex_normals=rn)[0];im.paste(img,(col*500,55+row*520))
            draw.text((col*500+10,row*520+10),label+(' / oblique' if row else ''),font=font,fill='white')
    im.save(out/'af-fitting-normal-verification.png')


if __name__=='__main__':run()
