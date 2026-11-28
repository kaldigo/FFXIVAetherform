"""Verify and compare local bra reconstruction against the accepted snapshot."""
import argparse,json,hashlib
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont
from mesh_crossings import crossing_pairs
from polish_repair import jump_stats
from surface_diagnostics import raster
from postprocess_bra import depth
from audit_envelope_normals import independently_recalculate,read_export


def run(folder='outputs/bra-postprocess-v5'):
    folder=Path(folder);manifest=json.loads(Path('working-mesh.json').read_text())
    old=np.load(manifest['artifacts']['body-fit-reference.npz']['path']);new=np.load(folder/'body-fit-reference.npz')
    report=json.loads((folder/'postprocess-report.json').read_text());v,t=new['position'],new['triangles'];keep=new['originalWorkingFace']>=0
    oldface=new['originalWorkingFace'][keep];oldvertex=new['originalWorkingVertex']
    for k in ('uv','weights','group'):np.testing.assert_array_equal(new[k][keep],old[k][oldface])
    present=oldvertex>=0
    fixed=present&~np.isin(oldvertex,report.get('displacedWorkingVertices',[]))
    np.testing.assert_array_equal(v[fixed],old['position'][oldvertex[fixed]])
    lower=keep&np.char.startswith(new['group'],'legs_');np.testing.assert_array_equal(new['normal'][lower],old['normal'][new['originalWorkingFace'][lower]])
    np.testing.assert_allclose(new['weights'].sum(-1),1,atol=1e-12);assert new['weights'].min()>=0
    np.testing.assert_allclose(new['normal'],independently_recalculate(v,t),atol=1e-10,rtol=0)
    ov,ot,on=read_export(folder/'vanilla-body-fit.obj');np.testing.assert_array_equal(ot,t);np.testing.assert_allclose(on,new['normal'],atol=5.1e-10,rtol=0)
    reconstructed_uv=np.einsum('fci,fcij->fcj',new['sourceWorkingBary'],old['uv'][new['sourceWorkingFace']])
    np.testing.assert_allclose(reconstructed_uv,new['uv'],atol=1e-12)
    np.testing.assert_allclose(np.einsum('fci,fcij->fcj',new['sourceWorkingBary'],old['weights'][new['sourceWorkingFace']]),new['weights'],atol=1e-12)
    lm=np.load(folder/'paired-landmarks-candidate.npz');oldlm=np.load(manifest['artifacts']['paired-landmarks-candidate.npz']['path'])
    for k in ('names','targetFace','targetBary','targetPosition'):np.testing.assert_array_equal(lm[k],oldlm[k])
    np.testing.assert_array_equal(lm['sourcePosition'],np.einsum('ni,nij->nj',lm['sourceBary'],v[t[lm['sourceFace']]]))
    cross=crossing_pairs(v[t],v[t],self_faces=t);np.savez_compressed(folder/'self-crossings.npz',pairs=cross)
    patches=[]
    for patch in report['patches']:
        before=np.zeros(len(old['triangles']),bool);before[patch['oldFaces']]=True
        after=np.zeros(len(t),bool);after[patch['firstNewFace']:patch['firstNewFace']+patch['newFaceCount']]=True
        patches.append(dict(kind=patch['kind'],before=jump_stats(old['position'],old['triangles'],before),after=jump_stats(v,t,after)))
    for patch in report.get('seamRegions',[]):
        before=np.zeros(len(old['triangles']),bool);before[patch['oldFaces']]=True
        after=np.isin(new['originalWorkingFace'],patch['oldFaces'])
        patches.append(dict(kind=patch['kind'],before=jump_stats(old['position'],old['triangles'],before),after=jump_stats(v,t,after)))
    po=old['position'][old['triangles']];pn=v[t];centre=[]
    for y in (1200,1220,1240,1250,1260,1280,1300):
        centre.append(dict(yMm=y,workingDepthMm=depth(po,0,y),candidateDepthMm=depth(pn,0,y),
            flankDepthMm=[depth(pn,x,y) for x in (-30,30)]))
    shifts=np.linalg.norm(lm['sourcePosition']-oldlm['sourcePosition'],axis=1)
    audit=dict(status='Review candidate' if not len(cross) else 'Self crossings found; reject candidate',
        selfCrossings=len(cross),acceptedWorkingSnapshotUnchanged=hashlib.sha256(Path(manifest['artifacts']['body-fit-reference.npz']['path']).read_bytes()).hexdigest()==manifest['artifacts']['body-fit-reference.npz']['sha256'],
        lowerBodyGeometryAndNormalsExact=True,positionsOutsideLocalPatchesExact=True,retainedUVWeightsExact=True,updatedNormalsVerified=True,
        landmarks=948,unchangedAFSide=True,reboundLandmarks=report['reboundLandmarks'],maximumLandmarkMovementMm=float(shifts.max()),patches=patches,centreline=centre,
        limitations='Estimated local anatomy; exact boundary positions and collar/virtual-angle samples do not guarantee exact tangent matching. No pose/physics validation. Candidate may move outside the old bra envelope where the inferred medial shape changes.')
    (folder/'postprocess-audit.json').write_text(json.dumps(audit,indent=2)+'\n');print(json.dumps(audit,indent=2),flush=True)
    im=Image.new('RGB',(1160,1700),'#18202a');draw=ImageDraw.Draw(im);font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',23)
    for col,label in enumerate(('Accepted working mesh','Local angle-guided trial')):draw.text((col*580+10,15),label,font=font,fill='white')
    views=(('front',(-135,135,1165,1320)),('front',(-40,40,1200,1305)),('front',(-115,115,1170,1220)),('front',(-145,145,1170,1320)))
    for row,(view,bounds) in enumerate(views):
        for col,d in enumerate((old,new)):
            p=d['position'][d['triangles']];n=d['normal']
            if row==3:
                th=np.deg2rad(25);rotation=np.array([[np.cos(th),0,np.sin(th)],[0,1,0],[-np.sin(th),0,np.cos(th)]])
                p=p@rotation.T;n=n@rotation.T
            img,_,_=raster(p,view,bounds,vertex_normals=n);im.paste(img,(580*col,60+row*410))
    im.save(folder/'bra-postprocess-comparison.png')


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('folder',nargs='?',default='outputs/bra-postprocess-v5');run(p.parse_args().folder)
