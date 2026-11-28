"""Show standalone geometric underwear and the same shells over the fitting body."""
import argparse,json
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont
from surface_diagnostics import raster


def run(folder):
    folder=Path(folder);d=np.load(folder/'underclothes-shell.npz');report=json.loads((folder/'extraction-report.json').read_text());body=np.load(report['sourceBody'])
    p=d['position'][d['triangles']];bp=body['position'][body['triangles']]
    color=np.where(np.char.startswith(d['group'],'bra')[:,None],np.array([235,179,106]),np.array([223,143,99]))
    allp=np.r_[bp,p];alln=np.r_[body['normal'],d['normal']];allc=np.r_[np.tile([111,173,195],(len(bp),1)),color]
    im=Image.new('RGB',(1400,1750),'#18202a');draw=ImageDraw.Draw(im);font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',25)
    for col,title in enumerate(('Standalone extracted shells','Same shells over 6 mm fitting body')):draw.text((700*col+15,15),title,font=font,fill='white')
    for row,view in enumerate(('front','back','side')):
        bounds=(-180,180,790,1335) if view!='side' else (-130,150,790,1335)
        for col in range(2):
            img,_,_=raster(p if col==0 else allp,view,bounds,size=(670,520),vertex_normals=d['normal'] if col==0 else alln,face_colors=color if col==0 else allc)
            im.paste(img,(700*col+15,75+row*550));draw.text((700*col+20,50+row*550),view,font=font,fill='white')
    im.save(folder/'underclothes-over-body.png')
    im=Image.new('RGB',(1800,1060),'#18202a');draw=ImageDraw.Draw(im)
    for col,(title,view,bounds) in enumerate((('Bra front','front',(-145,145,1160,1315)),('Briefs front','front',(-165,165,825,1010)),('Briefs rear','back',(-165,165,825,1010)))):
        draw.text((col*600+15,15),title,font=font,fill='white')
        for row,scene in enumerate((False,True)):
            img,_,_=raster(allp if scene else p,view,bounds,size=(580,440),vertex_normals=alln if scene else d['normal'],face_colors=allc if scene else color)
            im.paste(img,(col*600+10,55+row*490))
    draw.text((20,1015),'Geometry-derived lighting only. No normal maps applied. Clearances/thickness are explicit trial assumptions.',font=font,fill='white')
    im.save(folder/'underclothes-closeups.png')


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('folder');run(p.parse_args().folder)
