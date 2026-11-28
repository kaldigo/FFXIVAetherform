"""Locate post-process regions on the accepted body without changing geometry."""
import hashlib,json
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont
from surface_diagnostics import raster


def run():
    manifest=json.loads(Path('working-mesh.json').read_text())
    for record in manifest['artifacts'].values():
        if hashlib.sha256(Path(record['path']).read_bytes()).hexdigest()!=record['sha256']:raise ValueError('Accepted snapshot hash mismatch')
    d=np.load(manifest['artifacts']['body-fit-reference.npz']['path']);raw=np.load('outputs/body-cleanup-4x-raw/body-fit-reference.npz')
    v,t=d['position'],d['triangles'];np.testing.assert_array_equal(t,raw['triangles']);p=v[t];centre=p.mean(1)
    band=(centre[:,1]>1170)&(centre[:,1]<1205)&(abs(centre[:,0])>20)&(abs(centre[:,0])<120)&(centre[:,2]>45)
    medial=(abs(centre[:,0])<20)&(centre[:,1]>1190)&(centre[:,1]<1280)&(centre[:,2]>60)
    generated=d['originalDenseFace']<0
    a=raw['position'][t];cross=np.cross(a[:,1]-a[:,0],a[:,2]-a[:,0]);now=np.cross(p[:,1]-p[:,0],p[:,2]-p[:,0])
    ratio=np.sum(cross*now,axis=1)/np.sum(cross**2,axis=1);thin=medial&(ratio<.1)
    report=dict(workingMeshSha256=manifest['artifacts']['body-fit-reference.npz']['sha256'],geometryModified=False,
        creaseClarification='The earlier remark referred to shallow diagonal traces along the front leg openings toward the groin, as marked in the first panel. It did not refer to the restored flat crotch bridge or its sharper inner-thigh corners. These traces are not established defects or an instruction to remove them.',
        braSeam=dict(inspectedFaces=int(band.sum()),generatedFaces=int(np.sum(band&generated)),
            issue='Existing garment seam/band geometry remains in measured source triangles.',
            candidateApproach='Exclude the seam strip from shape evidence; reconstruct a narrow joining surface using the accepted breast curve above and torso below, matching boundary position and surface direction. Protect the broad under-breast profile.'),
        cleavage=dict(inspectedFaces=int(medial.sum()),generatedFaces=int(np.sum(medial&generated)),
            thinFaceIds=np.flatnonzero(thin).tolist(),minimumProjectedAreaRatio=float(ratio[medial].min()),
            issue='Part of the medial surface is prior generated flap-root infill. Further smoothing cannot supply missing anatomical evidence.',
            candidateApproach='Reconstruct a separate medial patch from explicit valley/flank curves or observed skin reference, then remesh locally if required. Preserve the cleavage recess rather than bridging between breast peaks.',
            availableReference='The earlier Coatee investigation verified local bra/skin topology correspondence on 29 vertices/30 triangles. Its skin is a reference candidate, not a direct full-chest replacement.'),
        nextStep='Review user post-processing ideas before selecting a reconstruction method. All trials use separate folders and the accepted working mesh remains intact.')
    out=Path('outputs/bra-postprocess-inspection');out.mkdir(parents=True,exist_ok=True)
    (out/'inspection.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report,indent=2),flush=True)
    im=Image.new('RGB',(2280,640),'#18202a');draw=ImageDraw.Draw(im)
    font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',23);small=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',19)
    specs=[('1. Leg-opening traces',(-100,100,820,920),'Visible diagonal traces; no change proposed.'),
        ('2. Bra seam region',(-130,130,1165,1240),'Amber: seam inspection strip on existing geometry.'),
        ('3. Cleavage evidence',(-35,35,1190,1285),'Amber: generated infill. Red: thin elements.')]
    for col,(title,bounds,caption) in enumerate(specs):
        colors=None
        if col==1:colors=np.where(band[:,None],[235,179,106],[111,173,195])
        if col==2:colors=np.where((medial&generated)[:,None],[235,179,106],[111,173,195]);colors[thin]=[240,90,110]
        img,_,_=raster(p,'front',bounds,size=(740,520),vertex_normals=d['normal'],face_colors=colors)
        if col==0:
            mark=ImageDraw.Draw(img);xmin,xmax,ymin,ymax=bounds;scale=min(740/(xmax-xmin),520/(ymax-ymin))
            for x,y in ((-40.,883.),(40.,883.)):
                px=370+(x-(xmin+xmax)/2)*scale;py=260-(y-(ymin+ymax)/2)*scale
                mark.ellipse((px-23,py-23,px+23,py+23),outline='#f45596',width=4)
        im.paste(img,(col*760+10,65));draw.text((col*760+15,16),title,font=font,fill='white');draw.text((col*760+15,600),caption,font=small,fill='white')
    im.save(out/'postprocess-region-review.png')


if __name__=='__main__':run()
