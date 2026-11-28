"""Measure seat-curve changes instead of inferring volume from smooth shading."""
import json
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont


def section(p,y):
    points=[]
    for i,j in ((0,1),(1,2),(2,0)):
        a,b=p[:,i],p[:,j];mask=(a[:,1]-y)*(b[:,1]-y)<=0;mask&=abs(a[:,1]-b[:,1])>1e-9
        a,b=a[mask],b[mask];f=(y-a[:,1])/(b[:,1]-a[:,1]);points.append(a+f[:,None]*(b-a))
    return np.concatenate(points)


def run():
    folders=['body-cleanup-4x-raw','body-cleanup-4x-blur8','body-cleanup-4x-neutral']
    names=['Raw relief','Blurred relief','Neutralised relief'];colors=['#65ccf5','#83df97','#ffbf62']
    curves=[];ys=np.arange(840.,991.,2.)
    for folder in folders:
        d=np.load(Path('outputs')/folder/'body-fit-reference.npz');p=d['position'][d['triangles']]
        curves.append(np.array([[q[:,2].min(),abs(q[:,0]).max()] for q in (section(p,y) for y in ys)]))
    im=Image.new('RGB',(1400,1050),'#18202a');draw=ImageDraw.Draw(im)
    font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',24);small=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',19)
    for col,(label,lo,hi,index) in enumerate((('Side outline: further left = further rear',-105,-55,0),('Rear outline: further right = wider seat',130,165,1))):
        left,top=90+col*700,100;width,height=540,800
        draw.text((left,25),label,font=font,fill='white')
        def point(x,y):return left+(x-lo)/(hi-lo)*width,top+(990-y)/150*height
        for y in range(840,991,25):
            a,b=point(lo,y),point(hi,y);draw.line([a,b],fill='#36434f');draw.text((left-48,a[1]-10),str(y),font=small,fill='white')
        for x in np.linspace(lo,hi,6):
            a,b=point(x,840),point(x,990);draw.line([a,b],fill='#36434f');draw.text((a[0]-15,a[1]+8),f'{x:g}',font=small,fill='white')
        for curve,color in zip(curves,colors):draw.line([point(x,y) for x,y in zip(curve[:,index],ys)],fill=color,width=3)
        draw.text((left,950),'Coordinates in mm; same origin, no alignment or scaling.',font=small,fill='white')
    for i,(name,color) in enumerate(zip(names,colors)):draw.text((100+i*420,1000),name,font=font,fill=color)
    folder=Path('outputs/body-cleanup-4x-blur8');im.save(folder/'seat-profile-overlay.png')
    rows=[]
    for y in (850,875,900,925,950,975):
        row={'yMm':y}
        for name,curve in zip(names,curves):row[name]=dict(rearZMm=float(np.interp(y,ys,curve[:,0])),halfWidthMm=float(np.interp(y,ys,curve[:,1])))
        rows.append(row)
    report=dict(profiles=rows,
      interpretation='Neutralisation redistributes the seat curve: it can be wider and rounder-looking while projecting less rearward through the middle seat. Blur tracks the raw profile more closely. Fullness is not a single silhouette coordinate.')
    (folder/'seat-profile-comparison.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report,indent=2))


if __name__=='__main__':run()
