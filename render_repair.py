"""Matched geometry-only views of the original, first cleanup and local repair."""
import argparse
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont
from render_af_comparison import panel,BG


def render(original,previous,repaired,output):
    datasets=[np.load(p) for p in (original,previous,repaired)]
    views=[('Chest folds','front',(-135,135,1155,1320)),
           ('Seat / rear creases','back',(-155,155,825,1010)),
           ('Front / inner thighs','front',(-115,115,790,975)),
           ('Hip and seat profile','side',(-115,115,825,1010))]
    im=Image.new('RGB',(1800,1940),BG);draw=ImageDraw.Draw(im)
    font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',23)
    for col,(data,title) in enumerate(zip(datasets,('Original upscale','First cleanup','Larger-fold repair'))):
        draw.text((col*600+18,12),title,font=font,fill='white')
        for row,(label,view,bounds) in enumerate(views):
            draw.text((col*600+18,row*460+52),label,font=font,fill='white')
            panel(im,data['position'][data['triangles']],(col*600+10,row*460+90,580,405),view,bounds)
    draw.text((20,1900),'Identical cameras and geometry-derived lighting. No textures. Candidate only.',font=font,fill='white')
    im.save(output)


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    for key in ('original','previous','repaired','output'):p.add_argument('--'+key,type=Path,required=True)
    a=p.parse_args();render(a.original,a.previous,a.repaired,a.output)
