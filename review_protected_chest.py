"""Compare broad-curve protection and fine-filter choices on fixed chest data."""
import json
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont
from fit_curvature_envelope import curvature_operator
from protect_underbreast import broad_operator
from separate_body_shape import surface_operator
from surface_diagnostics import raster


def run():
    folders=['body-cleanup-4x-raw','body-curvature-envelope-4x-w1000','body-protected-underbreast-4x',
        'body-protected-underbreast-4x-f20-w3000','body-protected-underbreast-4x-f20-w10000']
    labels=['Raw','Current / 25 mm fairness','Protect + 10 mm / 1000','Protect + 20 mm / 3000','Protect + 20 mm / 10000']
    data=[dict(np.load(Path('outputs')/f/'body-fit-reference.npz')) for f in folders]
    raw,current,first=data[:3];v,t=raw['position'],raw['triangles'];n=current['envelopeDirection'];mass,_=surface_operator(v,t)
    free=first['chestFreeVertex'];broad,bw=broad_operator(v,t,n,mass,free,first['broadCurveProtection'])
    region=first['clothVertex']&(abs(v[:,0])>=25)&(abs(v[:,0])<=100)&(v[:,1]>=1180)&(v[:,1]<=1220)&(v[:,2]>50)
    lower_ids=np.unique(t[np.char.startswith(raw['group'],'legs_')]);records=[]
    for k,(label,d) in enumerate(zip(labels,data)):
        shift=np.zeros(len(v)) if k==0 else d['envelopeNormalShiftMm']
        op,base,w,ids=curvature_operator(v,d['position'],n,t,raw['group'],mass,free,20.)
        fine=base+op@shift
        record=dict(name=label,folder=folders[k],underbreastMeanInwardMm=float(-np.average(shift[region],weights=mass[region])),
            underbreastMaxInwardMm=float(-shift[region].min()),
            broadCoefficientDifferenceRmsMm=float(np.sqrt(np.average((broad@shift)**2,weights=bw))),
            localTwentyMmResidualRmsMm=float(np.sqrt(np.average(fine**2,weights=w))),
            maximumLowerBodyChangeFromCurrentMm=float(np.linalg.norm(d['position'][lower_ids]-current['position'][lower_ids],axis=1).max()))
        records.append(record);print(record,flush=True)
    out=Path('outputs/body-protected-underbreast-4x')
    (out/'protection-trials.json').write_text(json.dumps(records,indent=2)+'\n')
    im=Image.new('RGB',(2900,900),'#18202a');draw=ImageDraw.Draw(im);font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',21)
    for col,label in enumerate(labels):draw.text((col*580+10,15),label,font=font,fill='white')
    for row,(view,bounds) in enumerate((('front',(-135,135,1155,1320)),('front',(-105,105,1170,1240)))):
        for col,d in enumerate(data):
            img,_,_=raster(d['position'][t],view,bounds,size=(580,400),vertex_normals=d['normal'])
            im.paste(img,(580*col,60+row*420))
    im.save(out/'protection-trials.png')


if __name__=='__main__':run()
