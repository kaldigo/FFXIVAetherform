"""Measure underside motion and plot unshaded fixed-X breast profiles."""
import json
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont
from investigate_coatee import front_depth
from reference_surface import areas


def run():
    folder=Path('outputs/body-curvature-envelope-4x-w1000')
    data=[np.load(Path(f)/'body-fit-reference.npz') for f in ('outputs/body-cleanup-4x-raw','outputs/body-inner-envelope-4x-collar12',str(folder))]
    raw,old,new=data;v,t=raw['position'],raw['triangles'];mass=np.zeros(len(v))
    for k in range(3):np.add.at(mass,t[:,k],areas(v[t])/3)
    mask=new['clothVertex']&(abs(v[:,0])>=25)&(abs(v[:,0])<=100)&(v[:,1]>=1180)&(v[:,1]<=1220)&(v[:,2]>50)
    w=mass[mask];rows=[]
    for name,before in (('raw_to_current',raw),('previous_to_current',old)):
        shift=new['envelopeNormalShiftMm'][mask]-(np.zeros(mask.sum()) if name=='raw_to_current' else old['envelopeNormalShiftMm'][mask])
        delta=new['position'][mask]-before['position'][mask];mean=float(np.average(shift,weights=w))
        rows.append(dict(comparison=name,meanInwardMm=-mean,p95MovementMm=float(np.percentile(abs(shift),95)),maximumInwardMm=float(-shift.min()),
            maximumOutwardMm=float(max(0,shift.max())),normalShiftVariationAfterConstantInsetRmsMm=float(np.sqrt(np.average((shift-mean)**2,weights=w))),
            meanUpwardMm=float(np.average(delta[:,1],weights=w)),maximumUpwardMm=float(delta[:,1].max())))
    report=dict(region='Raw material coordinates: 25<=abs(X)<=100, 1180<=Y<=1220, Z>50 mm, cloth-covered vertices; central cleavage excluded',
        vertices=int(mask.sum()),movement=rows,
        interpretation='Latest correction changes the broad lower-breast curve as well as removing fabric detail. Positive upward movement draws some underside points higher. Shift variation after subtracting a constant inset confirms nonuniform movement; it is not a complete curvature error or anatomical ground truth.',
        profileDefinition='Exact frontmost triangle intersections at fixed X and Y; no lighting, registration or compensating offsets. Depth changes at a fixed height also reflect upward motion of a sloping surface.')
    (folder/'underbreast-measurements.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report,indent=2))
    profiles=[];ys=np.arange(1175.,1241.,1.)
    for x in (40.,60.,80.):
        curves=[]
        for d in data:
            p=d['position'][d['triangles']];p=p[(p[:,:,0].min(1)<=x)&(p[:,:,0].max(1)>=x)]
            values=[]
            for y in ys:
                q=p[(p[:,:,1].min(1)<=y)&(p[:,:,1].max(1)>=y)]
                values.append(front_depth(q,(x,y))['zMm'])
            curves.append(np.array(values))
        profiles.append(curves)
    im=Image.new('RGB',(1740,940),'#18202a');draw=ImageDraw.Draw(im)
    font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',22);small=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',18)
    colors=['#65ccf5','#ffbf62','#83df97'];labels=['Raw','Previous fit','Curvature correction']
    archive=dict(heightMm=ys)
    for col,(x,curves) in enumerate(zip((40,60,80),profiles)):
        left=75+580*col;top=90;width=455;height=660;lo=np.floor(min(c.min() for c in curves)-1);hi=np.ceil(max(c.max() for c in curves)+1)
        def point(z,y):return left+(z-lo)/(hi-lo)*width,top+height-(y-1175)/65*height
        draw.text((left-25,15),f'Underside section at X = {x} mm',font=font,fill='white')
        draw.text((left,49),'Higher = upwards; right = farther forward',font=small,fill='white')
        for y in np.arange(1175,1241,10):
            a,b=point(lo,y),point(hi,y);draw.line([a,b],fill='#36434f');draw.text((a[0]-60,a[1]-10),f'{y:g}',font=small,fill='white')
        for z in np.linspace(lo,hi,5):
            a,b=point(z,1175),point(z,1240);draw.line([a,b],fill='#36434f');draw.text((a[0]-15,a[1]+10),f'{z:.1f}',font=small,fill='white')
        for i,(curve,color) in enumerate(zip(curves,colors)):
            draw.line([point(z,y) for z,y in zip(curve,ys)],fill=color,width=3);archive[f'x{x}_variant{i}_depthMm']=curve
    for i,(label,color) in enumerate(zip(labels,colors)):draw.text((100+580*i,825),label,font=font,fill=color)
    draw.text((60,875),'Unshaded geometry profiles. Fixed-height depth changes include movement upward along the sloping underside.',font=font,fill='white')
    im.save(folder/'underbreast-profiles.png');np.savez_compressed(folder/'underbreast-profiles.npz',**archive)


if __name__=='__main__':run()
