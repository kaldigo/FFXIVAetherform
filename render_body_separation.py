"""Matched raw/blur/neutral/separation body renders, without applied textures."""
import argparse
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont
from surface_diagnostics import raster


def render(scale,trials=False):
    if trials:
        folders=['outputs/body-cleanup-4x-raw']+['outputs/body-separation-4x-s'+str(n) for n in (6,12,20)]
        labels=['Raw reference','Separated / 6 mm','Separated / 12 mm','Separated / 20 mm']
        path=Path('outputs/body-separation-comparison/separation-trials.png')
    else:
        folders=['outputs/body-cleanup-4x-raw','outputs/body-cleanup-4x-blur8','outputs/body-cleanup-4x-neutral','outputs/body-separation-4x-s'+str(scale)]
        labels=['Raw relief','Blurred relief','Neutralised relief',f'Separated / {scale} mm + 0.5 mm inward']
        path=Path('outputs/body-separation-comparison/four-way-comparison.png')
    data=[np.load(Path(f)/'body-fit-reference.npz') for f in folders]
    im=Image.new('RGB',(2320,1690),'#18202a');draw=ImageDraw.Draw(im);font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',21)
    for col,label in enumerate(labels):draw.text((580*col+10,12),label,font=font,fill='white')
    for row,(view,bounds) in enumerate((('front',(-135,135,1155,1320)),('back',(-155,155,825,1010)),('front',(-115,115,790,975)),('side',(-115,115,825,1010)))):
        for col,d in enumerate(data):
            img,_,_=raster(d['position'][d['triangles']],view,bounds,vertex_normals=d['normal']);im.paste(img,(580*col,50+405*row))
    path.parent.mkdir(parents=True,exist_ok=True);im.save(path)


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--scale',type=int,default=6);p.add_argument('--trials',action='store_true');a=p.parse_args();render(a.scale,a.trials)
