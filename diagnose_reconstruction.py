"""Compare native shading with flat refinement and PN geometry, without repairs."""
import json
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont
from reference_surface import unit,weld,patches,evaluate,areas
from surface_diagnostics import raster


def angle(a,b):return np.degrees(np.arccos(np.clip(np.sum(unit(a)*unit(b),axis=-1),-1,1)))


def stats(x):return dict(rmsDegrees=float(np.sqrt(np.mean(x*x))),p95Degrees=float(np.percentile(x,95)),maxDegrees=float(np.max(x)))


def run():
    d=np.load('outputs/vanilla-reference-v1/vanilla-reference.npz');out=Path('outputs/vanilla-reconstruction-diagnosis-v1');out.mkdir(parents=True,exist_ok=True)
    p,n=d['sourcePosition'],d['sourceNormal'];v,t=weld(p)
    controls,_=patches(v[t],n,t,np.char.endswith(d['sourceGroups'],'_cap'))
    parent=d['sourceFace'];bc=d['sourceBarycentric']
    authored=np.einsum('fci,fij->fcj',bc,n[parent])
    # Keep linear normal vectors unnormalised until pixel interpolation.
    # Normalising at newly inserted vertices changes the native shading field.
    views=[('front',(-135,135,1155,1320)),('back',(-155,155,825,1010)),
           ('front',(-115,115,790,975)),('side',(-115,115,825,1010))]
    datasets=[(p,n),(d['flatPosition'][d['triangles']],authored),
              (d['position'][d['triangles']],d['normal']),
              (d['position'][d['triangles']],authored)]
    titles=['Native / authored normals','Flat dense / same normal field','PN curved / patch normals','PN curved / authored normals']
    im=Image.new('RGB',(2320,1690),'#18202a');draw=ImageDraw.Draw(im);font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',21)
    pixel_error=[]
    for col,((pos,norm),title) in enumerate(zip(datasets,titles)):
        draw.text((580*col+10,10),title,font=font,fill='white')
        for row,(view,bounds) in enumerate(views):
            img,fi,b=raster(pos,view,bounds,vertex_normals=norm);im.paste(img,(580*col,50+405*row))
            if col==0:pixel_error.append(np.array(img))
            if col==1:
                delta=abs(np.array(img).astype(float)-pixel_error[row].astype(float))
                pixel_error[row]=dict(meanChannelError=float(delta.mean()),p99ChannelError=float(np.percentile(delta,99)),maxChannelError=float(delta.max()))
    im.save(out/'native-flat-pn-comparison.png')
    # Sample the actual continuous PN surface. Fine tessellation cannot remove
    # a disagreement already present in its analytic derivatives.
    sample=np.array([[1/3]*3,[.5,.5,0],[0,.5,.5],[.5,0,.5]])
    _,pn=evaluate(controls,np.arange(len(p))[:,None],np.broadcast_to(sample,(len(p),4,3)),True)
    target=np.einsum('si,fij->fsj',sample,n)
    mismatch=angle(pn,target)
    retained=~np.char.endswith(d['sourceGroups'],'_cap')
    cross=np.cross(p[:,1]-p[:,0],p[:,2]-p[:,0]);nativecorner=angle(cross[:,None],n)
    edge={}
    for f,tri in enumerate(t):
        for i,j in ((0,1),(1,2),(2,0)):edge.setdefault(tuple(sorted(tri[[i,j]])),[]).append((f,i,j))
    jumps=[];nativejumps=[];pairs=[]
    for rows in edge.values():
        if len(rows)!=2:continue
        values=[];targets=[]
        for f,i,j in rows:
            b=np.zeros(3);b[[i,j]]=.5
            _,normal=evaluate(controls,np.array([f]),b[None],True)
            values.append(normal[0]);targets.append(unit((n[f,i]+n[f,j])[None])[0])
        jumps.append(float(angle(np.array(values[:1]),np.array(values[1:]))[0]))
        nativejumps.append(float(angle(np.array(targets[:1]),np.array(targets[1:]))[0]));pairs.append([r[0] for r in rows])
    pairs=np.array(pairs);jumps=np.array(jumps);nativejumps=np.array(nativejumps)
    valid=retained[pairs].all(1)
    continuous=valid&(nativejumps<.1)
    seeds=[1410,1479,1478,947,1056,972,1083,1052,840,846,847,849,850,852,264,265,267]
    rows=[]
    for f in seeds:
        touching=np.any(pairs==f,axis=1)
        lengths=np.linalg.norm(p[f]-np.roll(p[f],1,axis=0),axis=1)
        rows.append(dict(nativeFace=f,identity=d['sourceIdentity'][f].tolist(),group=str(d['sourceGroups'][f]),
                         centreMm=p[f].mean(0).tolist(),aspect=float(lengths.max()**2/areas(p[f:f+1])[0]),
                         pnVsAuthoredNormals=stats(mismatch[f]),
                         maxPNJoinJumpDegrees=float(jumps[touching].max()),maxNativeJoinJumpDegrees=float(nativejumps[touching].max())))
    report=dict(scope='Original reference only. Mesh-normal shading comparison; game material normal-map texture is not sampled.',
                normalMapTextureSampled=False,pnVsAuthoredNormals=stats(mismatch[retained].ravel()),
                pnJoinJumpsNativeFaces=stats(jumps[valid]),nativeJoinJumps=stats(nativejumps[valid]),
                continuousNativeEdges=int(continuous.sum()),
                continuousNativeEdgesWithPNJumpOver5Degrees=int((continuous&(jumps>5)).sum()),
                continuousNativeEdgesPNJump=stats(jumps[continuous]),
                sourceCornerNormalsOpposingFace=int((nativecorner[retained]>90).sum()),
                flatRefinementPixelError=pixel_error,annotatedNativeFaces=rows,
                conclusion='PN position interpolation is not a normal-field integration or appearance-preserving reconstruction. Continuous edge positions do not ensure continuous derivative normals. Native shading was replaced by PN derivative normals; this disagreement exists before tessellation and before cleanup.')
    (out/'diagnosis.json').write_text(json.dumps(report,indent=2)+'\n')
    print(json.dumps({k:v for k,v in report.items() if k not in ('annotatedNativeFaces','conclusion')},indent=2))


if __name__=='__main__':run()
