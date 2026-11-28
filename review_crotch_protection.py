"""Check protected crotch shape, fixed accepted regions and underside profiles."""
import json
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont
from surface_diagnostics import raster


def underside(p,x,z):
    mask=(p[:,:,0].min(1)<=x)&(p[:,:,0].max(1)>=x)&(p[:,:,2].min(1)<=z)&(p[:,:,2].max(1)>=z)
    q=p[mask];a=q[:,0][:,[0,2]]
    edge=np.stack((q[:,1][:,[0,2]]-a,q[:,2][:,[0,2]]-a),axis=2)
    valid=abs(np.linalg.det(edge))>1e-10
    if not valid.any():return np.nan
    bc=np.linalg.solve(edge[valid],(np.array([x,z])-a[valid])[...,None])[...,0]
    bc=np.c_[1-bc.sum(1),bc];inside=(bc.min(1)>=-1e-8)&(bc.max(1)<=1+1e-8)
    if not inside.any():return np.nan
    return float(np.einsum('ni,ni->n',bc[inside],q[valid][inside,:,1]).min())


def run(folder='outputs/body-protected-crotch-4x'):
    folder=Path(folder)
    data=[dict(np.load(Path(f)/'body-fit-reference.npz')) for f in ('outputs/body-cleanup-4x-raw','outputs/body-protected-underbreast-4x-f20-w10000',str(folder))]
    raw,current,new=data;v,t=raw['position'],raw['triangles'];free=new['crotchFreeVertex'];lower=np.char.startswith(raw['group'],'legs_')
    top=~lower;rear=lower&(v[t].mean(1)[:,1]>875)&(v[t].mean(1)[:,2]<-45)
    np.testing.assert_array_equal(current['position'][~free],new['position'][~free])
    for mask in (top,rear):
        ids=np.unique(t[mask]);np.testing.assert_array_equal(current['position'][ids],new['position'][ids])
        np.testing.assert_array_equal(current['normal'][mask],new['normal'][mask])
    for k in ('triangles','uv','weights','boneNames','group','originalDenseFace','originalVertex','cornerNativeFace','cornerNativeBary'):
        np.testing.assert_array_equal(current[k],new[k])
    lm=np.load(folder/'paired-landmarks-candidate.npz');old=np.load('outputs/body-protected-underbreast-4x-f20-w10000/paired-landmarks-candidate.npz')
    for k in ('names','sourceFace','sourceBary','targetFace','targetBary','targetPosition'):np.testing.assert_array_equal(lm[k],old[k])
    np.testing.assert_array_equal(lm['sourcePosition'],np.einsum('ni,nij->nj',lm['sourceBary'],new['position'][t[lm['sourceFace']]]))
    np.testing.assert_allclose(new['weights'].sum(-1),1,atol=1e-12)
    centre=362;changes=[]
    mask=lower&(v[t].mean(1)[:,1]>790)&(v[t].mean(1)[:,1]<1010)
    p=[d['position'][t[mask]] for d in data];xs=np.arange(-45.,45.1,1.);profiles=[];archive=dict(xMm=xs)
    for z in (0.,20.,40.):
        curves=[np.array([underside(q,x,z) for x in xs]) for q in p];profiles.append(curves)
        row=dict(zMm=z)
        for k,label in ((1,'current'),(2,'protected')):
            valid=(abs(xs)<=30)&np.isfinite(curves[0])&np.isfinite(curves[k]);delta=curves[k][valid]-curves[0][valid]
            row[label]=dict(rmsRawProfileDifferenceMm=float(np.sqrt(np.mean(delta**2))),
                meanVerticalShiftMm=float(delta.mean()),rmsAfterConstantVerticalShiftMm=float(np.sqrt(np.mean((delta-delta.mean())**2))))
        changes.append(row)
        for k,curve in enumerate(curves):archive[f'z{int(z)}_variant{k}_heightMm']=curve
    for label,d in (('current',current),('protected',new)):
        print(label,'centre raw',v[centre].round(4),'now',d['position'][centre].round(4),'rise',d['position'][centre,1]-v[centre,1],flush=True)
    core=(abs(v[:,0])<25)&(v[:,1]>830)&(v[:,1]<890)&(v[:,2]>-30)&(v[:,2]<60)
    cross=np.cross(v[t[:,1]]-v[t[:,0]],v[t[:,2]]-v[t[:,0]])
    pnew=new['position'][t];now=np.cross(pnew[:,1]-pnew[:,0],pnew[:,2]-pnew[:,0])
    ratio=np.sum(cross*now,axis=1)/np.sum(cross**2,axis=1)
    report=dict(chestAndRearSeatPositionsAndNormalsExact=True,allOutsidePatchPositionsExact=True,
        topologyUVWeightsAndProvenanceExact=True,landmarkAddressesAndAFValuesExact=948,
        originalCentreVertex=centre,currentCentreRiseMm=float(current['position'][centre,1]-v[centre,1]),
        protectedCentreRiseMm=float(new['position'][centre,1]-v[centre,1]),
        maximumCoreRiseMm=float((new['position'][core,1]-v[core,1]).max()),
        maximumCoreMovementMm=float(np.linalg.norm(new['position'][core]-v[core],axis=1).max()),
        minimumModifiedAreaRatio=float(ratio[np.any(free[t],axis=1)].min()),profiles=changes,
        scope='Exact bottommost pelvis intersections at fixed X/Z. Profiles exclude legs below Y=790; shape-after-offset metrics are diagnostics, not anatomical truth. Inspect sharp transitions and residual fabric separately.')
    (folder/'crotch-shape-review.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report,indent=2),flush=True)
    np.savez_compressed(folder/'crotch-profiles.npz',**archive)
    im=Image.new('RGB',(1740,940),'#18202a');draw=ImageDraw.Draw(im);font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',22)
    colors=['#65ccf5','#ffbf62','#83df97'];labels=['Raw','Current / rounded crotch','Protected crotch']
    for col,(z,curves) in enumerate(zip((0,20,40),profiles)):
        left=75+580*col;top_y=95;width=450;height=650;finite=np.concatenate([a[np.isfinite(a)] for a in curves]);lo=float(np.floor(finite.min()-2));hi=float(np.ceil(finite.max()+2))
        def point(x,y):return left+(x+45)/90*width,top_y+height-(y-lo)/(hi-lo)*height
        draw.text((left-20,20),f'Crotch cross-section at Z = {z} mm',font=font,fill='white')
        for x in (-45,-30,-15,0,15,30,45):
            a,b=point(x,lo),point(x,hi);draw.line([a,b],fill='#36434f');draw.text((a[0]-12,a[1]+12),str(x),font=font,fill='white')
        for y in np.linspace(lo,hi,6):
            a,b=point(-45,y),point(45,y);draw.line([a,b],fill='#36434f');draw.text((a[0]-60,a[1]-10),f'{y:.1f}',font=font,fill='white')
        for curve,color in zip(curves,colors):
            finite=np.isfinite(curve);draw.line([point(x,y) for x,y in zip(xs[finite],curve[finite])],fill=color,width=3)
    for i,(label,color) in enumerate(zip(labels,colors)):draw.text((90+580*i,820),label,font=font,fill=color)
    draw.text((55,880),'Geometry profiles, no lighting. Horizontal: left/right X. Vertical: height Y. Higher line = higher crotch.',font=font,fill='white')
    im.save(folder/'crotch-profile-comparison.png')
    im=Image.new('RGB',(1740,1390),'#18202a');draw=ImageDraw.Draw(im)
    for col,label in enumerate(labels):draw.text((col*580+12,15),label,font=font,fill='white')
    for row,(view,bounds,angle) in enumerate((('front',(-90,90,810,910),0),('under',(-100,100,-45,85),0),('front',(-100,120,810,910),45))):
        for col,d in enumerate(data):
            keep=lower&(v[t].mean(1)[:,1]>=(825 if view=='under' else 800))
            q=d['position'][t[keep]];n=d['normal'][keep]
            if angle:
                th=np.deg2rad(angle);rotation=np.array([[np.cos(th),0,np.sin(th)],[0,1,0],[-np.sin(th),0,np.cos(th)]])
                q=q@rotation.T;n=n@rotation.T
            img,_,_=raster(q,view,bounds,size=(580,420),vertex_normals=n);im.paste(img,(col*580,65+row*440))
    im.save(folder/'crotch-geometry-comparison.png')


if __name__=='__main__':run()
