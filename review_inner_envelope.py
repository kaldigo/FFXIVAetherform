"""Audit and compare the skin-anchored envelope with raw and the earlier trial."""
import argparse,json
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont
from surface_diagnostics import raster
from clean_reference import graph,roughness
from mesh_crossings import crossing_pairs
from extract_underclothes import sample_surface,signed_nearest
from compare_seat_profiles import section
from investigate_coatee import front_depth
from reference_surface import unit


def seam_jumps(v,t,cloth):
    edges={}
    for i,tri in enumerate(t):
        for a,b in ((0,1),(1,2),(2,0)):edges.setdefault(tuple(sorted(tri[[a,b]])),[]).append(i)
    pairs=np.array([pair for pair in edges.values() if len(pair)==2 and cloth[pair[0]]!=cloth[pair[1]]])
    p=v[t];n=unit(np.cross(p[:,1]-p[:,0],p[:,2]-p[:,0]))
    angle=np.degrees(np.arccos(np.clip(np.sum(n[pairs[:,0]]*n[pairs[:,1]],axis=1),-1,1)))
    return dict(edges=len(pairs),rmsDegrees=float(np.sqrt(np.mean(angle**2))),p95Degrees=float(np.percentile(angle,95)),maxDegrees=float(angle.max()))


def proper_crossings(a,b,tolerance=1e-7):
    hit=np.zeros(len(a),bool)
    for p,q in ((a,b),(b,a)):
        e1=q[:,1]-q[:,0];e2=q[:,2]-q[:,0]
        for i,j in ((0,1),(1,2),(2,0)):
            direction=p[:,j]-p[:,i];h=np.cross(direction,e2);det=np.sum(e1*h,axis=1)
            good=abs(det)>1e-10;inv=np.zeros(len(det));inv[good]=1/det[good]
            s=p[:,i]-q[:,0];u=np.sum(s*h,axis=1)*inv;r=np.cross(s,e1)
            v=np.sum(direction*r,axis=1)*inv;f=np.sum(e2*r,axis=1)*inv
            hit|=good&(u>tolerance)&(v>tolerance)&(u+v<1-tolerance)&(f>tolerance)&(f<1-tolerance)
    return hit


def profiles(d):
    p=d['position'][d['triangles']];ys=np.arange(840.,991.,2.)
    rear=np.array([section(p,y)[:,2].min() for y in ys])
    x=np.arange(-90.,91.,2.);breast=np.array([front_depth(p,(a,1240.))['zMm'] for a in x])
    cy=np.arange(1190.,1301.,2.);cleavage=np.array([front_depth(p,(0.,y))['zMm'] for y in cy])
    return dict(seatY=ys,rearZ=rear,breastX=x,breastZ=breast,cleavageY=cy,cleavageZ=cleavage)


def run(folder):
    folder=Path(folder);folders=[Path('outputs/body-cleanup-4x-raw'),Path('outputs/body-separation-4x-s6'),folder]
    names=['Raw 4x relief','Previous 6 mm separation','Skin-anchored average / inward envelope']
    data=[dict(np.load(f/'body-fit-reference.npz')) for f in folders];raw,old,new=data
    t=raw['triangles'];assert np.array_equal(t,new['triangles'])
    for key in ('uv','weights','boneNames','group'):np.testing.assert_array_equal(raw[key],new[key])
    cloth=np.char.find(raw['group'],'smallclothes')>=0;ct=t[cloth];rp=raw['position'][ct];cp=new['position'][ct]
    pair=crossing_pairs(cp,rp);proper=pair[proper_crossings(cp[pair[:,0]],rp[pair[:,1]])]
    selfpair=crossing_pairs(new['position'][t],new['position'][t],self_faces=t)
    from clean_reference import normals
    rn=normals(raw['position'],t)[ct]
    vertex_ids,local=np.unique(ct,return_inverse=True)
    signed,distance=signed_nearest(sample_surface(new['position'][vertex_ids],local.reshape(-1,3)),rp,rn)
    vertex_cloth=np.zeros(len(new['position']),bool);vertex_cloth[ct]=True
    avg,_=graph(raw['position'],t);mask=vertex_cloth&~new['fixedSkin']
    rows=[];profile=[]
    for name,d in zip(names,data):
        profile.append(profiles(d))
        if len(d['position'])==len(raw['position']):
            r=roughness(d['position'],d['triangles'],avg,mask)
        else:
            av,_=graph(d['position'],d['triangles']);cm=np.zeros(len(d['position']),bool);cm[d['triangles'][np.char.find(d['group'],'smallclothes')>=0]]=True
            r=roughness(d['position'],d['triangles'],av,cm)
        rows.append(dict(name=name,clothNormalVariation=r,seamBoundaryNormalJumps=seam_jumps(d['position'],d['triangles'],np.char.find(d['group'],'smallclothes')>=0)))
    profile_changes={}
    for key in ('rearZ','breastZ','cleavageZ'):
        delta=profile[2][key]-profile[0][key]
        profile_changes[key]=dict(rmsMm=float(np.sqrt(np.mean(delta**2))),minimumMm=float(delta.min()),maximumMm=float(delta.max()))
    report=dict(status='Review candidate; residual seams and reconstructed cleavage root need work',
        properRawClothCrossings=int(len(proper)),nonAdjacentBodySelfCrossings=int(len(selfpair)),
        sampledOutwardPoints=int(np.sum(signed>1e-5)),maximumSampledOutwardMm=float(signed.max()),
        minimumSampledSignedMm=float(signed.min()),variants=rows,profileChangesFromRaw=profile_changes,
        caveat='Proper crossing test excludes tangent/coplanar contact expected at fixed joins. Nonpositive displacement alone does not certify containment. Raw outer garment stays fixed. Roughness uses the same raw mask for the new surface; previous topology uses its own cloth mask.')
    np.savez_compressed(folder/'envelope-audit-pairs.npz',properRawClothPairs=proper,selfPairs=selfpair)
    (folder/'envelope-audit.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report,indent=2),flush=True)
    im=Image.new('RGB',(1740,1690),'#18202a');draw=ImageDraw.Draw(im);font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',20)
    for col,name in enumerate(names):draw.text((580*col+10,12),name,font=font,fill='white')
    for row,(view,bounds) in enumerate((('front',(-135,135,1155,1320)),('back',(-155,155,825,1010)),('front',(-115,115,790,975)),('side',(-115,115,825,1010)))):
        for col,d in enumerate(data):
            img,_,_=raster(d['position'][d['triangles']],view,bounds,vertex_normals=d['normal']);im.paste(img,(580*col,50+405*row))
    im.save(folder/'envelope-comparison.png')
    # Original garment relief at its exact original coordinates, with no lift.
    p=np.r_[new['position'][t],rp];nn=np.r_[new['normal'],raw['normal'][cloth]]
    colors=np.r_[np.tile([111,173,195],(len(t),1)),np.tile([235,179,106],(len(ct),1))]
    overlay=Image.new('RGB',(1740,850),'#18202a');od=ImageDraw.Draw(overlay)
    for col,(view,bounds,label) in enumerate((('front',(-135,135,1155,1320),'Raw bra over envelope / no lift'),('back',(-155,155,825,1010),'Raw briefs over envelope / no lift'),('side',(-115,115,825,1010),'Seat profile / no lift'))):
        img,_,_=raster(p,view,bounds,size=(580,760),vertex_normals=nn,face_colors=colors)
        overlay.paste(img,(580*col,70));od.text((580*col+10,20),label,font=font,fill='white')
    overlay.save(folder/'raw-underclothes-no-lift.png')
    im=Image.new('RGB',(1700,920),'#18202a');draw=ImageDraw.Draw(im)
    colors=['#65ccf5','#ffbf62','#83df97'];archive={}
    for col,(title,xkey,ykey) in enumerate((('Seat profile: rear depth against height','rearZ','seatY'),('Breast section at Y = 1240 mm','breastX','breastZ'),('Cleavage centre profile','cleavageZ','cleavageY'))):
        left=70+col*560;top=100;width=450;height=670
        xx=np.concatenate([p[xkey] for p in profile]);yy=np.concatenate([p[ykey] for p in profile]);xmin,xmax=xx.min(),xx.max();ymin,ymax=yy.min(),yy.max()
        xmin-=2;xmax+=2;ymin-=2;ymax+=2
        def point(x,y):return left+(x-xmin)/(xmax-xmin)*width,top+height-(y-ymin)/(ymax-ymin)*height
        draw.text((left-35,25),title,font=font,fill='white')
        for x in np.linspace(xmin,xmax,5):
            a,b=point(x,ymin),point(x,ymax);draw.line([a,b],fill='#36434f');draw.text((a[0]-15,a[1]+8),f'{x:.1f}',font=font,fill='white')
        for y in np.linspace(ymin,ymax,6):
            a,b=point(xmin,y),point(xmax,y);draw.line([a,b],fill='#36434f');draw.text((a[0]-60,a[1]-10),f'{y:.1f}',font=font,fill='white')
        for i,(p,color) in enumerate(zip(profile,colors)):
            draw.line([point(x,y) for x,y in zip(p[xkey],p[ykey])],fill=color,width=3)
            archive[str(i)+'_'+xkey]=p[xkey];archive[str(i)+'_'+ykey]=p[ykey]
    for i,(name,color) in enumerate(zip(names,colors)):draw.text((40+i*560,860),name,font=font,fill=color)
    im.save(folder/'envelope-profiles.png');np.savez_compressed(folder/'envelope-profiles.npz',**archive)


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('folder',nargs='?',default='outputs/body-inner-envelope-4x-collar12');run(p.parse_args().folder)
