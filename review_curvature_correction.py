"""Compare final-curvature control on the reviewed chest and seat regions."""
import argparse,json
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont
from fit_curvature_envelope import curvature_operator
from separate_body_shape import surface_operator
from polish_repair import jump_stats
from surface_diagnostics import raster


def run(folder):
    folder=Path(folder)
    raw=dict(np.load('outputs/body-cleanup-4x-raw/body-fit-reference.npz'))
    old=dict(np.load('outputs/body-inner-envelope-4x-collar12/body-fit-reference.npz'))
    new=dict(np.load(folder/'body-fit-reference.npz'));v,t=raw['position'],raw['triangles']
    n=old['envelopeDirection'];mass,_=surface_operator(v,t)
    chest=(abs(v[:,0])<45)&(v[:,1]>1190)&(v[:,1]<1280)&(v[:,2]>60)&~old['fixedSkin']
    centre=v[t].mean(1);chest_face=(abs(centre[:,0])<45)&(centre[:,1]>1190)&(centre[:,1]<1280)&(centre[:,2]>60)
    cross=np.cross(v[t[:,1]]-v[t[:,0]],v[t[:,2]]-v[t[:,0]]);rows=[]
    for name,d in (('previous',old),('corrected',new)):
        operator,base,w,ids=curvature_operator(v,d['position'],n,t,raw['group'],mass,chest,25.)
        residual=base+operator@d['envelopeNormalShiftMm']
        p=d['position'][t];now=np.cross(p[:,1]-p[:,0],p[:,2]-p[:,0]);ratio=np.sum(now*cross,axis=1)/np.sum(cross**2,axis=1)
        rows.append(dict(name=name,chestQuadraticResidualRmsMm=float(np.sqrt(np.average(residual**2,weights=w))),
            chestQuadraticResidualP95Mm=float(np.percentile(abs(residual),95)),chestQuadraticResidualMaxMm=float(abs(residual).max()),
            chestNormalJumps=jump_stats(d['position'],t,chest_face),
            minimumChestAreaRatio=float(ratio[chest_face].min()),chestFacesBelowTenPercentArea=int(np.sum(chest_face&(ratio<.1)))))
    report=dict(chest=rows,scope='Same raw topology and chest mask. Local quadratic residuals are evaluated on each final geometry with the raw frame and neighbourhood metric; no normal maps. This does not identify hidden anatomy or certify curvature everywhere.')
    (folder/'curvature-correction-review.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report,indent=2),flush=True)
    im=Image.new('RGB',(1740,1700),'#18202a');draw=ImageDraw.Draw(im);font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',21)
    for col,label in enumerate(('Raw 4x','Previous inward envelope','Final-curvature correction')):draw.text((580*col+12,15),label,font=font,fill='white')
    views=(('front',(-135,135,1155,1320)),('front',(-30,30,1190,1275)),('back',(-155,155,930,1000)),('side',(-115,115,825,1010)))
    for row,(view,bounds) in enumerate(views):
        for col,d in enumerate((raw,old,new)):
            img,_,_=raster(d['position'][t],view,bounds,size=(580,405),vertex_normals=d['normal'])
            im.paste(img,(580*col,60+row*410))
    im.save(folder/'curvature-detail-comparison.png')


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('folder',nargs='?',default='outputs/body-curvature-envelope-4x-w1000');run(p.parse_args().folder)
