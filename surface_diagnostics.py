"""Orthographic depth-buffered geometry diagnostics and visible face picking."""
import numpy as np
from PIL import Image
from render_af_comparison import BG,camera
from reference_surface import unit


def raster(p,view,bounds,size=(580,405),vertex_normals=None,normal_sampler=None,face_colors=None):
    width,height=size;h,v,depth,sign,vertical_sign=camera(view)
    xmin,xmax,ymin,ymax=bounds
    cross=np.cross(p[:,1]-p[:,0],p[:,2]-p[:,0]);n=unit(cross)
    scale=min(width/(xmax-xmin),height/(ymax-ymin))
    xy=p[...,[h,v]].copy();xy[...,0]=width/2+(( -1 if view=='back' else 1)*xy[...,0]-(xmin+xmax)/2)*scale
    xy[...,1]=height/2-(vertical_sign*xy[...,1]-(ymin+ymax)/2)*scale
    zbuffer=np.full((height,width),-np.inf);face=np.full((height,width),-1,int)
    bc_image=np.zeros((height,width,3));rgb=np.zeros((height,width,3),np.uint8);rgb[:]=tuple(bytes.fromhex(BG[1:]))
    lower=np.maximum(np.floor(xy.min(axis=1)).astype(int),0)
    upper=np.minimum(np.ceil(xy.max(axis=1)).astype(int),[width-1,height-1])
    visible=(cross[:,depth]*sign>1e-10)&np.all(lower<=upper,axis=1)
    for i in np.flatnonzero(visible):
        q=xy[i];lo,hi=lower[i],upper[i]
        matrix=(q[:2]-q[2]).T
        if abs(np.linalg.det(matrix))<1e-12:continue
        xx,yy=np.meshgrid(np.arange(lo[0],hi[0]+1),np.arange(lo[1],hi[1]+1))
        b=(np.stack((xx+.5,yy+.5),axis=-1)-q[2])@np.linalg.inv(matrix).T
        b=np.concatenate((b,1-b.sum(-1,keepdims=True)),axis=-1)
        zz=b@(p[i,:,depth]*sign);inside=(b.min(-1)>=-1e-9)&(zz>zbuffer[yy,xx])
        ys,xs=yy[inside],xx[inside];face[ys,xs]=i;bc_image[ys,xs]=b[inside];zbuffer[ys,xs]=zz[inside]
    selected=face>=0;ids=face[selected]
    nn=n[ids] if vertex_normals is None else unit(np.einsum('ni,nij->nj',bc_image[selected],vertex_normals[ids]))
    if normal_sampler is not None:nn=normal_sampler(ids,bc_image[selected])
    light=np.zeros(3);light[depth]=sign*.85;light[v]=.5;light[h]=-.25
    shade=.5+.5*np.maximum(0,nn@unit(light))
    base=np.array([111,173,195]) if face_colors is None else np.asarray(face_colors)[ids]
    rgb[selected]=np.clip(base*shade[:,None],0,255).astype(np.uint8)
    return Image.fromarray(rgb),face,bc_image


def compare(previous,repaired,output):
    from PIL import ImageDraw,ImageFont
    from pathlib import Path
    im=Image.new('RGB',(2320,1690),BG);draw=ImageDraw.Draw(im)
    font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',23)
    views=[('front',(-135,135,1155,1320)),('back',(-155,155,825,1010)),
           ('front',(-115,115,790,975)),('side',(-115,115,825,1010))]
    for k,path in enumerate((previous,repaired)):
        d=np.load(Path(path)/'vanilla-repaired-reference.npz');tri=d['position'][d['triangles']]
        for j,norm in enumerate((None,d['normal'])):
            col=2*k+j
            draw.text((580*col+12,10),('Previous' if k==0 else 'Marked repair')+(' / flat' if j==0 else ' / smooth'),font=font,fill='white')
            for row,(view,bounds) in enumerate(views):
                img,_,_=raster(tri,view,bounds,vertex_normals=norm);im.paste(img,(580*col,50+405*row))
    im.save(output)


if __name__=='__main__':
    import argparse
    p=argparse.ArgumentParser(description=__doc__)
    for key in ('previous','repaired','output'):p.add_argument('--'+key,required=True)
    a=p.parse_args();compare(a.previous,a.repaired,a.output)
