"""Matched-camera body geometry and sampled-distance review images."""
import argparse
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

from reference_surface import unit, weld


BG='#18202a'


def colors(values):
    knots=np.array([0,3,7,15,30.])
    palette=np.array([[77,151,176],[91,191,174],[226,211,110],[231,139,78],[207,65,88]])
    return np.stack([np.interp(values,knots,palette[:,i]) for i in range(3)],axis=-1)


def camera(view):
    if view=='front':return 0,1,2,1,1
    if view=='back':return 0,1,2,-1,1
    if view=='side':return 2,1,0,1,1
    if view=='under':return 0,2,1,-1,1
    raise ValueError(view)


def silhouette(p,view):
    h,v,depth,sign,_=camera(view)
    vertices,tri=weld(p)
    face=np.cross(p[:,1]-p[:,0],p[:,2]-p[:,0])[:,depth]*sign>0
    edge={}
    for i,t in enumerate(tri):
        for a,b in ((0,1),(1,2),(2,0)):
            edge.setdefault(tuple(sorted(t[[a,b]])),[]).append(i)
    result=[]
    for key,rows in edge.items():
        if len(rows)==1 or any(face[i]!=face[rows[0]] for i in rows):
            result.append(vertices[list(key)])
    return np.array(result)


def panel(image,p,rect,view,bounds,distance=None,outline=None,mask=None):
    left,top,width,height=rect
    layer=Image.new('RGB',(width,height),BG);draw=ImageDraw.Draw(layer)
    h,v,depth,sign,vertical_sign=camera(view)
    xmin,xmax,ymin,ymax=bounds
    select=np.ones(len(p),bool) if mask is None else mask.copy()
    cross=np.cross(p[:,1]-p[:,0],p[:,2]-p[:,0])
    select&=cross[:,depth]*sign>1e-10
    select&=(p[:,:,v].max(axis=1)>=ymin)&(p[:,:,v].min(axis=1)<=ymax)
    select&=(p[:,:,h].max(axis=1)>=xmin)&(p[:,:,h].min(axis=1)<=xmax)
    p,cross=p[select],cross[select]
    normal=unit(cross)
    light=np.zeros(3);light[depth]=sign*.85;light[v]=.5;light[h]=-.25
    shade=.5+.5*np.maximum(0,normal@unit(light))
    base=np.broadcast_to(np.array([111,173,195]),(len(p),3)) if distance is None else colors(distance[select])
    color=np.clip(base*shade[:,None],0,255).astype(int)
    scale=min(width/(xmax-xmin),height/(ymax-ymin))
    # Back horizontal flips like a rear camera; underside keeps left/right.
    horizontal_sign=-1 if view=='back' else 1
    def project(points):
        result=points[...,[h,v]].copy()
        result[...,0]=width/2+(horizontal_sign*result[...,0]-(xmin+xmax)/2)*scale
        result[...,1]=height/2-(vertical_sign*result[...,1]-(ymin+ymax)/2)*scale
        return result
    xy=project(p)
    for i in np.argsort(p[:,:,depth].mean(axis=1)*sign):
        draw.polygon([tuple(q) for q in xy[i]],fill=tuple(color[i]))
    if outline is not None and len(outline):
        for edge in project(outline):
            draw.line([tuple(q) for q in edge],fill='#93d8f2',width=2)
    image.paste(layer,(left,top))


def render(folder):
    folder=Path(folder);data=np.load(folder/'comparison.npz')
    af,vanilla,distance=data['afPosition'],data['vanillaPosition'],data['afDistance']
    font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',25)
    small=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',20)
    im=Image.new('RGB',(2100,3000),BG);draw=ImageDraw.Draw(im)
    for col,title in enumerate(('Curved vanilla reference','AF Small and Medium B','AF distance from vanilla')):
        draw.text((col*700+25,18),title,font=font,fill='white')
    for row,view in enumerate(('front','back','side')):
        for col in range(3):
            mesh=vanilla if col==0 else af
            panel(im,mesh,(col*700+15,row*940+80,670,870),view,(-490,490,135,1460),
                  distance=distance if col==2 else None)
            draw.text((col*700+25,row*940+60),view,font=small,fill='white')
    legend(draw,(35,2890),font,small)
    im.save(folder/'body-comparison.png')
    im=Image.new('RGB',(2100,1680),BG);draw=ImageDraw.Draw(im)
    panels=[('Front chest','front',(-160,160,1140,1360),'top'),
            ('Side chest','side',(-105,160,1140,1360),'top'),
            ('Shoulder and armpit','front',(75,235,1200,1420),'top'),
            ('Rear seat','back',(-180,180,790,1050),'bottom'),
            ('Side hip and underseat','side',(-145,125,780,1050),'bottom'),
            ('Underside of pelvis','under',(-150,150,-125,105),'under')]
    for i,(title,view,bounds,part) in enumerate(panels):
        col,row=i%3,i//3
        af_c=af.mean(axis=1);v_c=vanilla.mean(axis=1)
        if part=='top':
            am=np.ones(len(af),bool);vm=np.ones(len(vanilla),bool)
        elif part=='bottom':
            am=np.ones(len(af),bool);vm=np.ones(len(vanilla),bool)
        else:
            am=(af_c[:,1]>750)&(af_c[:,1]<950);vm=(v_c[:,1]>750)&(v_c[:,1]<950)
        outline=silhouette(vanilla[vm],view)
        panel(im,af,(col*700+15,row*745+70,670,650),view,bounds,distance,outline,am)
        draw.text((col*700+25,row*745+18),title,font=font,fill='white')
    legend(draw,(35,1510),font,small)
    draw.text((35,1610),'Cyan: projected vanilla contours. Underside is a pelvic cutaway. AF lower surface has not been smoothed.',font=small,fill='#c2cbd4')
    im.save(folder/'wrap-risk-closeups.png')
    print(folder/'wrap-risk-closeups.png')


def legend(draw,origin,font,small):
    x,y=origin
    draw.text((x,y),'Unsigned nearest-surface distance at AF triangle centres (mm)',font=font,fill='white')
    ramp=colors(np.linspace(0,30,600))
    for i,c in enumerate(ramp):draw.line([(x+i,y+42),(x+i,y+63)],fill=tuple(c.astype(int)))
    for value in (0,5,10,20,30):draw.text((x+value*20,y+67),str(value),font=small,fill='white')
    draw.text((x+720,y+44),'0-3 close | 7-15 moderate | 15-30 large',font=small,fill='white')


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('folder',type=Path)
    render(p.parse_args().folder)
