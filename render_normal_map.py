"""Vanilla with the actual normal maps versus reconstructed geometry alone."""
from pathlib import Path
import argparse,json
import numpy as np
from PIL import Image,ImageDraw,ImageFont
from surface_diagnostics import raster
from normal_field import NormalField


def run(folder,mapping=None,wrap2=None):
    folder=Path(folder);source=dict(np.load('outputs/vanilla-reference-v1/vanilla-reference.npz'))
    rebuilt=np.load(folder/'normal-map-reference.npz');field=NormalField(source)
    native=source['sourcePosition'];tri=rebuilt['position'][rebuilt['triangles']]
    views=[('front',(-135,135,1155,1320)),('back',(-155,155,825,1010)),
           ('front',(-115,115,790,975)),('side',(-115,115,825,1010))]
    im=Image.new('RGB',(1740,1690),'#18202a');draw=ImageDraw.Draw(im);font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',21)
    for col,label in enumerate(('Vanilla + actual normal maps','Reconstruction / geometric normals','Reconstruction / smooth normals')):
        draw.text((col*580+10,12),label,font=font,fill='white')
    for row,(view,bounds) in enumerate(views):
        a,_,_=raster(native,view,bounds,normal_sampler=field.sample);im.paste(a,(0,50+405*row))
        b,_,_=raster(tri,view,bounds);im.paste(b,(580,50+405*row))
        c,_,_=raster(tri,view,bounds,vertex_normals=rebuilt['normal']);im.paste(c,(1160,50+405*row))
    im.save(folder/'normal-map-comparison.png')
    if mapping and wrap2:
        levels=[np.load(Path(path)/'normal-map-reference.npz') for path in (mapping,wrap2,folder)]
        im=Image.new('RGB',(2320,1690),'#18202a');draw=ImageDraw.Draw(im)
        for col,label in enumerate(('Vanilla + actual normal maps','1x AF / mapping','2x AF / wrap','4x AF / wrap')):
            draw.text((col*580+10,12),label,font=font,fill='white')
        for row,(view,bounds) in enumerate(views):
            datasets=[(native,None,field.sample)]+[(d['position'][d['triangles']],d['normal'],None) for d in levels]
            for col,(pos,norm,sampler) in enumerate(datasets):
                a,_,_=raster(pos,view,bounds,vertex_normals=norm,normal_sampler=sampler);im.paste(a,(580*col,50+405*row))
        im.save(folder/'density-comparison.png')


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('folder');p.add_argument('--mapping');p.add_argument('--wrap2');a=p.parse_args()
    if bool(a.mapping)!=bool(a.wrap2):p.error('Supply both --mapping and --wrap2 for the density comparison')
    run(a.folder,a.mapping,a.wrap2)
