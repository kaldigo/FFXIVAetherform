"""Independently verify exported normals and compare smooth/flat seat shading."""
import argparse,json
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont
from surface_diagnostics import raster
from reference_surface import unit


def independently_recalculate(v,t):
    p=v[t];face=unit(np.cross(p[:,1]-p[:,0],p[:,2]-p[:,0]));total=np.zeros_like(v)
    for k in range(3):
        a=p[:,(k+1)%3]-p[:,k];b=p[:,(k+2)%3]-p[:,k]
        angle=np.arctan2(np.linalg.norm(np.cross(a,b),axis=1),np.sum(a*b,axis=1))
        np.add.at(total,t[:,k],angle[:,None]*face)
    return unit(total)[t]


def read_export(path):
    positions=[];normals=[];faces=[];normal_ids=[]
    for line in Path(path).read_text().splitlines():
        a=line.split()
        if not a:continue
        if a[0]=='v':positions.append([float(x) for x in a[1:]])
        elif a[0]=='vn':normals.append([float(x) for x in a[1:]])
        elif a[0]=='f':
            corners=[s.split('/') for s in a[1:]]
            faces.append([int(s[0])-1 for s in corners]);normal_ids.append([int(s[2])-1 for s in corners])
    return np.array(positions),np.array(faces),np.array(normals)[np.array(normal_ids)]


def angular(a,b):
    # atan2 retains precision for the near-identical stored/recomputed normals.
    return np.degrees(np.arctan2(np.linalg.norm(np.cross(a,b),axis=-1),np.sum(a*b,axis=-1)))


def stats(values):
    return dict(meanDegrees=float(values.mean()),p95Degrees=float(np.percentile(values,95)),maxDegrees=float(values.max()))


def run(folder):
    folder=Path(folder);d=np.load(folder/'body-fit-reference.npz');raw=np.load('outputs/body-cleanup-4x-raw/body-fit-reference.npz')
    v,t=d['position'],d['triangles'];fresh=independently_recalculate(v,t)
    ov,ot,on=read_export(folder/'vanilla-body-fit.obj');np.testing.assert_array_equal(t,ot)
    np.testing.assert_allclose(ov,v,atol=5.1e-10,rtol=0)
    np.testing.assert_allclose(d['normal'],fresh,atol=1e-10,rtol=0)
    np.testing.assert_allclose(on,fresh,atol=5.1e-10,rtol=0)
    exported_fresh=independently_recalculate(ov,ot)
    np.testing.assert_allclose(np.linalg.norm(d['normal'],axis=-1),1,atol=1e-12)
    assert np.isfinite(d['normal']).all()
    p=v[t];centre=p.mean(1);seat=(centre[:,1]>825)&(centre[:,1]<1010)&(centre[:,2]<-40)
    changed=np.linalg.norm(d['normal']-raw['normal'],axis=-1)>1e-8
    face=unit(np.cross(p[:,1]-p[:,0],p[:,2]-p[:,0]))
    report=dict(status='Stored and OBJ normals match independently recomputed geometry normals',
        geometryUpdated=True,normalMapAppliedToReview=False,
        maxStoredVectorError=float(np.linalg.norm(d['normal']-fresh,axis=-1).max()),
        maxObjVectorError=float(np.linalg.norm(on-fresh,axis=-1).max()),
        maxObjAngleErrorDegrees=float(angular(unit(on),exported_fresh).max()),
        changedSeatCorners=int(changed[seat].sum()),seatCorners=int(3*seat.sum()),
        seatNormalChangeFromRaw=stats(angular(raw['normal'][seat],fresh[seat])),
        seatSmoothVsFaceNormal=stats(angular(fresh[seat],np.repeat(face[seat,None],3,axis=1))),
        explanation='Normals are angle-weighted and shared by geometric vertex identity across material borders. The fresh calculation uses atan2 rather than the production arccos angle calculation. Flat shading uses triangle cross products and no stored normals. Remaining features visible under flat shading arise from surface slopes/triangulation, not stale normal-map or pre-fit normals.')
    (folder/'normal-update-audit.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report,indent=2),flush=True)
    labels=['Raw / original smooth normals','New / exported smooth normals','New / independently recalculated','New / flat geometric normals']
    im=Image.new('RGB',(2320,1050),'#18202a');draw=ImageDraw.Draw(im);font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',21)
    for col,label in enumerate(labels):draw.text((col*580+10,14),label,font=font,fill='white')
    for row,(view,bounds) in enumerate((('back',(-160,160,815,1020)),('side',(-120,125,815,1020)))):
        for col in range(4):
            q=raw['position'][raw['triangles']] if col==0 else p
            n=raw['normal'] if col==0 else d['normal'] if col==1 else fresh if col==2 else None
            img,_,_=raster(q,view,bounds,size=(580,470),vertex_normals=n)
            im.paste(img,(col*580,60+row*485))
    im.save(folder/'seat-normal-verification.png')


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('folder',nargs='?',default='outputs/body-inner-envelope-4x-collar12');run(p.parse_args().folder)
