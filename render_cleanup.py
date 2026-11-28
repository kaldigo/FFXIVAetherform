"""Matched untextured geometry views of the cloth-only cleanup candidate."""
import argparse
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

from render_af_comparison import panel, colors, BG


def render(folder):
    folder=Path(folder);data=np.load(folder/'cleanup-comparison.npz')
    report=json.loads((folder/'cleanup-report.json').read_text())
    control,clean=data['controlPosition'],data['cleanedPosition'];t=data['triangles']
    movement=np.linalg.norm(clean-control,axis=1)[t].mean(axis=1)
    font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',24)
    small=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',19)
    views=[('Chest front','front',(-135,135,1155,1320)),
           ('Chest side','side',(-85,150,1155,1320)),
           ('Briefs front','front',(-160,160,840,1025)),
           ('Briefs rear','back',(-160,160,840,1025))]
    im=Image.new('RGB',(1890,2130),BG);draw=ImageDraw.Draw(im)
    for col,title in enumerate(('Current upscale','Cleaned candidate','Movement from control')):
        draw.text((col*630+20,15),title,font=font,fill='white')
    for row,(title,view,bounds) in enumerate(views):
        for col,p in enumerate((control,clean,clean)):
            panel(im,p[t],(col*630+10,row*485+75,610,420),view,bounds,
                  distance=movement*12 if col==2 else None)
            draw.text((col*630+20,row*485+52),title,font=small,fill='white')
    transition=report['protection'].get('skinTransitionMm',0)
    protection='Skin, joins and medial cleavage fixed.' if not transition else f'Skin outside {transition:g} mm join blend and medial cleavage fixed.'
    draw.text((25,2020),'Identical cameras, topology and geometry-derived lighting. No textures. '+protection,font=small,fill='white')
    ramp=colors(np.linspace(0,30,500))
    for i,c in enumerate(ramp):draw.line([(25+i,2060),(25+i,2082)],fill=tuple(c.astype(int)))
    draw.text((25,2088),'Movement: 0                                  1.25                                 2.5 mm',font=small,fill='white')
    draw.text((650,2060),'Selected: '+report['selected']+' '+report['method']+' candidate',font=font,fill='white')
    im.save(folder/'cleanup-comparison.png')
    # Trial grid exposes strength choices without replacing the accepted mesh.
    im=Image.new('RGB',(2240,1140),BG);draw=ImageDraw.Draw(im)
    for col,(key,title) in enumerate((('controlPosition','Current'),('mildPosition','Mild'),('mediumPosition','Medium'),('strongPosition','Strong'))):
        draw.text((col*560+20,15),title,font=font,fill='white')
        for row,(label,view,bounds) in enumerate(views[:2]):
            panel(im,data[key][t],(col*560+10,row*540+65,540,480),view,bounds)
    im.save(folder/'cleanup-strengths.png')
    print(folder/'cleanup-comparison.png')


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('folder',type=Path)
    render(p.parse_args().folder)
