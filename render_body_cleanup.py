"""Matched body-envelope comparison after the same flap-root reconstruction."""
import argparse
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont
from surface_diagnostics import raster


def render(folders,output):
    datasets=[np.load(Path(p)/'body-fit-reference.npz') for p in folders]
    im=Image.new('RGB',(1740,1690),'#18202a');draw=ImageDraw.Draw(im);font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',21)
    for col,label in enumerate(('Raw cloth relief / roots rebuilt','Blurred relief / selected','Neutralised relief / shape changed')):
        draw.text((580*col+10,12),label,font=font,fill='white')
    views=[('front',(-135,135,1155,1320)),('back',(-155,155,825,1010)),
           ('front',(-115,115,790,975)),('side',(-115,115,825,1010))]
    for row,(view,bounds) in enumerate(views):
        for col,d in enumerate(datasets):
            img,_,_=raster(d['position'][d['triangles']],view,bounds,vertex_normals=d['normal']);im.paste(img,(580*col,50+405*row))
    im.save(output)


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--raw',required=True);p.add_argument('--blur',required=True);p.add_argument('--smooth',required=True);p.add_argument('--output',required=True)
    a=p.parse_args();render((a.raw,a.blur,a.smooth),a.output)
