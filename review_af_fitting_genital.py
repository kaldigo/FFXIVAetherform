"""Matched views and exact sections of the AF genital fitting envelope."""
import argparse,json
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont
from clean_reference import normals
from surface_diagnostics import raster


def cut_segments(p,axis,value):
    segments=[]
    for tri in p:
        hits=[]
        for i,j in ((0,1),(1,2),(2,0)):
            a,b=tri[i],tri[j]
            if (a[axis]-value)*(b[axis]-value)<=0 and abs(a[axis]-b[axis])>1e-10:
                f=(value-a[axis])/(b[axis]-a[axis]);hits.append(a+f*(b-a))
        if len(hits)==2 and np.linalg.norm(hits[1]-hits[0])>1e-9:segments.append(hits)
    return np.array(segments)


def run(folder='outputs/af-fitting-genital-v3'):
    out=Path(folder);d=dict(np.load(out/'af-fitting-reference.npz'));v=d['parentPosition'];q=d['position'];t=d['triangles'];diag=dict(np.load(out/'genital-diagnostics.npz'))
    font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',20)
    im=Image.new('RGB',(1200,1840),'#182129');draw=ImageDraw.Draw(im)
    views=[('Front / thigh gap','front',(-90,90,790,945)),('Under / genital surface','under',(-40,40,-20,95)),
        ('Under / wider fitting surface','under',(-90,90,-105,95)),('Cutaway / inward folds','side',(-20,95,830,925))]
    for col,(label,p) in enumerate((('Before',v),('Fitting envelope',q))):
        ns=normals(p,t)
        for row,(title,view,bounds) in enumerate(views):
            selected=np.ones(len(t),bool)
            if row==3:selected=np.max(p[t][...,0],axis=1)<-.2
            img=raster(p[t[selected]],view,bounds,(600,400),vertex_normals=ns[t[selected]])[0]
            x=col*600;y=row*460;im.paste(img,(x,y+50));draw.text((x+12,y+9),label+' / '+title,font=font,fill='white')
    im.save(out/'af-genital-fitting-comparison.png')
    im.crop((0,0,1200,920)).save(out/'af-genital-fitting-overview.png')
    frame=diag['frame'];origin=diag['origin'];faces=diag['faces']
    im=Image.new('RGB',(1400,620),'#182129');draw=ImageDraw.Draw(im)
    for col,(axis,cut,title,lo,hi) in enumerate(((0,.2,'Along slit, X = 0.2 mm',-35,90),(1,5.,'Across slit, local S = 5 mm',-22,22))):
        left=70+700*col;top=70;width=560;height=450;display_axis=1-axis
        def point(x,y):return left+(x-lo)/(hi-lo)*width,top+(18-y)/43*height
        draw.text((left,15),title,font=font,fill='white')
        for h in (-20,-10,0,10):
            draw.line([point(lo,h),point(hi,h)],fill='#354552');draw.text((left-40,point(lo,h)[1]-10),str(h),font=font,fill='white')
        for x in ([-20,0,20,40,60,80] if col==0 else [-20,-10,0,10,20]):
            a,b=point(x,-25),point(x,18);draw.line([a,b],fill='#354552');draw.text((a[0]-12,top+height+7),str(x),font=font,fill='white')
        for p,color in ((v,'#ffbc70'),(q,'#7ce3ba')):
            local=(p-origin)@frame;segments=cut_segments(local[t[faces]],axis,cut)
            for a,b in segments:
                if max(a[display_axis],b[display_axis])>=lo and min(a[display_axis],b[display_axis])<=hi:
                    draw.line([point(a[display_axis],a[2]),point(b[display_axis],b[2])],fill=color,width=3)
    draw.text((80,565),'Orange: original folds    Green: fitting envelope    Exact triangle sections; coordinates in mm.',font=font,fill='white')
    im.save(out/'af-genital-envelope-sections.png')
    print('Saved before/after, cutaway and exact mesh-section comparisons')


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('folder',nargs='?',default='outputs/af-fitting-genital-v3');run(p.parse_args().folder)
