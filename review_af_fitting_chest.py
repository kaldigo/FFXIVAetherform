"""Matched geometry shading and exact nipple-region mesh cross sections."""
import argparse,json
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont
from scipy.spatial.transform import Rotation
from clean_reference import normals
from surface_diagnostics import raster
from compare_seat_profiles import section


def run(folder='outputs/af-fitting-chest-v2'):
    out=Path(folder);d=dict(np.load(out/'af-fitting-reference.npz'));report=json.loads((out/'af-fitting-chest-report.json').read_text());t=d['triangles']
    previous=np.load('outputs/af-fitting-chest-v1/af-fitting-reference.npz')['position']
    versions=[('Original AF',d['sourcePosition']),('Previous fitting copy',previous),('Revised fitting copy',d['position'])]
    im=Image.new('RGB',(1800,1400),'#182129');draw=ImageDraw.Draw(im);font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',22)
    views=[('Front chest','front',(-130,130,1160,1345),None),('Breast profile','side',(65,145,1200,1280),None),
           ('Oblique breast curve','front',(30,110,1200,1280),Rotation.from_euler('y',-35,degrees=True).as_matrix())]
    pivot=np.array([72.,1244.,105.])
    for col,(name,v) in enumerate(versions):
        for row,(label,view,bounds,rotation) in enumerate(views):
            q=v if rotation is None else (v-pivot)@rotation.T+pivot
            img=raster(q[t],view,bounds,(600,405),vertex_normals=normals(q,t)[t])[0]
            im.paste(img,(col*600,row*465+55));draw.text((col*600+12,row*465+8),name+' / '+label,font=font,fill='white')
    im.save(out/'af-fitting-chest-comparison.png')
    im=Image.new('RGB',(1400,570),'#182129');draw=ImageDraw.Draw(im);small=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',18)
    patch=next(r for r in report['patches'] if r['side']=='left');origin=np.array(patch['originMm']);frame=np.array(patch['frame'])
    for col,axis in enumerate((0,1)):
        left=60+col*700;top=65;width=570;height=400
        draw.text((left,12),'Breast cross section / '+('horizontal' if axis==0 else 'vertical'),font=font,fill='white')
        def point(x,h):return left+(x+27)/54*width,top+(2-h)/19*height
        for x in range(-20,21,10):
            a,b=point(x,-17),point(x,2);draw.line((a,b),fill='#354552');draw.text((a[0]-12,top+height+8),str(x),font=small,fill='white')
        for h in (-15,-10,-5,0):
            a,b=point(-27,h),point(27,h);draw.line((a,b),fill='#354552');draw.text((left-35,a[1]-10),str(h),font=small,fill='white')
        for v,color in ((d['sourcePosition'],'#ffbc70'),(previous,'#72bbff'),(d['position'],'#7ce3ba')):
            local=(v-origin)@frame
            # section() cuts its second coordinate; place the chosen tangent
            # axis first and the complementary tangent coordinate second.
            p=local[t][...,[axis,1-axis,2]];points=section(p,0)
            mask=(abs(points[:,0])<27)&(points[:,2]>-17)&(points[:,2]<2)
            points=points[mask];points=points[np.argsort(points[:,0])]
            draw.line([point(x,h) for x,_,h in points],fill=color,width=3)
    draw.text((70,520),'Orange: original    Blue: previous    Green: revised    Same section planes; mm.',font=font,fill='white')
    im.save(out/'af-fitting-breast-profiles.png')
    print('Saved chest comparison and exact cross-section overlays')


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('folder',nargs='?',default='outputs/af-fitting-chest-v2');run(p.parse_args().folder)
