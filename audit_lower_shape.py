"""Review lower-body shape changes beyond the fixed rear and side views."""
import argparse,json
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont
from reference_surface import areas
from surface_diagnostics import raster


def run(render=True):
    folder=Path('outputs/body-curvature-envelope-4x-w1000')
    data=[np.load(Path(f)/'body-fit-reference.npz') for f in ('outputs/body-cleanup-4x-raw','outputs/body-inner-envelope-4x-collar12',str(folder))]
    raw,old,new=data;v,t=raw['position'],raw['triangles'];lower=np.char.startswith(raw['group'],'legs_')
    cross=np.cross(v[t[:,1]]-v[t[:,0]],v[t[:,2]]-v[t[:,0]]);p=new['position'][t]
    ratio=np.sum(cross*np.cross(p[:,1]-p[:,0],p[:,2]-p[:,0]),axis=1)/np.sum(cross**2,axis=1)
    mass=np.zeros(len(v))
    for k in range(3):np.add.at(mass,t[:,k],areas(v[t])/3)
    ids=np.unique(t[lower]);vertex_lower=np.zeros(len(v),bool);vertex_lower[ids]=True
    regions=dict(crotch=vertex_lower&(abs(v[:,0])<25)&(v[:,1]>830)&(v[:,1]<890)&(v[:,2]>-30)&(v[:,2]<60),
        innerThigh=vertex_lower&(abs(v[:,0])>=25)&(abs(v[:,0])<85)&(v[:,1]>790)&(v[:,1]<880)&(v[:,2]>0),
        seat=vertex_lower&(v[:,1]>875)&(v[:,1]<985)&(v[:,2]<-45),
        hips=vertex_lower&(abs(v[:,0])>105)&(v[:,1]>900)&(v[:,1]<995))
    rows=[]
    for name,mask in regions.items():
        d=new['position'][mask]-raw['position'][mask];extra=new['position'][mask]-old['position'][mask]
        rows.append(dict(region=name,vertices=int(mask.sum()),meanRawMovementMm=float(np.average(np.linalg.norm(d,axis=1),weights=mass[mask])),
            maxRawMovementMm=float(np.linalg.norm(d,axis=1).max()),maxAddedMovementMm=float(np.linalg.norm(extra,axis=1).max()),
            maxUpwardRawMm=float(d[:,1].max()),maxUpwardAddedMm=float(extra[:,1].max())))
    report=dict(status='Seat appearance reviewed; crotch shape is flagged before lower-body acceptance',regions=rows,
        minimumLowerProjectedAreaRatio=float(ratio[lower].min()),lowerFacesBelowTwentyPercentArea=int(np.sum(lower&(ratio<.2))),
        finding='The crotch centre rises by roughly 6.8 mm from raw and 5.1 mm from the previous envelope. This is a substantial shape change even though lower-body triangles remain healthy and the existing crossing audit passes. Do not treat the whole lower body as accepted from the rear views alone.',
        scope='Static shape review, not proof of anatomy, animation or physics. The chest-only trial must leave the current lower body unchanged for comparison, while the crotch remains an open issue.')
    (folder/'lower-shape-audit.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report,indent=2),flush=True)
    if not render:return
    im=Image.new('RGB',(1740,2220),'#18202a');draw=ImageDraw.Draw(im);font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',22)
    for col,label in enumerate(('Raw 4x','Previous inward envelope','Current curvature envelope')):draw.text((col*580+10,15),label,font=font,fill='white')
    views=[('Front / crotch','front',(-160,160,795,1010),0),('Rear / seat','back',(-160,160,815,1010),0),
        ('Front three-quarter','front',(-180,180,795,1010),45),('Rear three-quarter','back',(-180,180,795,1010),45),
        ('Underside, legs cut at Y=825','under',(-160,160,-110,110),0)]
    for row,(title,view,bounds,angle) in enumerate(views):
        draw.text((12,48+row*430),title,font=font,fill='white')
        for col,d in enumerate(data):
            keep=lower&(v[t].mean(1)[:,1]>=(825 if view=='under' else 790))
            q=d['position'][t[keep]];n=d['normal'][keep]
            if angle:
                theta=np.deg2rad(angle);rotation=np.array([[np.cos(theta),0,np.sin(theta)],[0,1,0],[-np.sin(theta),0,np.cos(theta)]])
                q=q@rotation.T;n=n@rotation.T
            img,_,_=raster(q,view,bounds,size=(580,390),vertex_normals=n);im.paste(img,(580*col,80+row*430))
    im.save(folder/'lower-multiview-review.png')


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--metrics-only',action='store_true');a=p.parse_args();run(not a.metrics_only)
