"""Compare native stock smallclothes and No.2 Type B lower-body surfaces."""
import argparse
from collections import Counter
import csv
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

from compare_chest_topology import match
from investigate_coatee import load_meshes
from reference_surface import weld, unit


def prepare(p):
    ids=np.flatnonzero(p.mean(axis=1)[:,1]>810)
    v,t=weld(p[ids]);adjacency={};edges=set()
    for i,f in enumerate(t):
        for a,b in ((0,1),(1,2),(2,0)):
            key=tuple(sorted(f[[a,b]]));adjacency.setdefault(key,[]).append(i);edges.add(key)
    return v,t,adjacency,edges,ids


def ray_z(triangles,x,y,sign):
    a=triangles[:,0,:2]
    edges=np.stack((triangles[:,1,:2]-a,triangles[:,2,:2]-a),axis=2)
    good=abs(np.linalg.det(edges))>1e-10
    ids=np.flatnonzero(good)
    uv=np.linalg.solve(edges[good],(np.array([x,y])-a[good])[...,None])[...,0]
    b=np.c_[1-uv.sum(axis=1),uv]
    inside=(b.min(axis=1)>=-1e-8)&(b.max(axis=1)<=1+1e-8)
    if not inside.any():return None
    z=np.einsum('ni,ni->n',b[inside],triangles[ids[inside],:,2])
    value=float(z[np.argmax(sign*z)])
    return value if sign*value>0 else None


def draw_panel(image,p,groups,rect,view):
    left,top,width,height=rect
    im=Image.new('RGB',(width,height),'#18202a');draw=ImageDraw.Draw(im)
    axis=2 if view=='side' else 0
    depth=0 if view=='side' else 2
    sign=-1 if view=='back' else 1
    xmin,xmax=(-180,180) if view!='side' else (-145,135)
    ymin,ymax=775,1060
    select=(p[:,:,1].max(axis=1)>=ymin)&(p[:,:,1].min(axis=1)<=ymax)
    p,groups=p[select],groups[select]
    cross=np.cross(p[:,1]-p[:,0],p[:,2]-p[:,0])
    facing=cross[:,depth]*sign>1e-8
    p,groups,cross=p[facing],groups[facing],cross[facing]
    normal=unit(cross)
    light=np.array([.4,.6,sign*.8]) if depth==2 else np.array([.8,.6,.4])
    brightness=.35+.65*np.maximum(0,normal@unit(light))
    scale=min(width/(xmax-xmin),height/(ymax-ymin))
    xy=p[:,:,[axis,1]].copy()
    xy[:,:,0]=width/2+(xy[:,:,0]*sign-(xmin+xmax)/2)*scale
    xy[:,:,1]=height/2-(xy[:,:,1]-(ymin+ymax)/2)*scale
    for i in np.argsort(p[:,:,depth].mean(axis=1)*sign):
        base=np.array([99,177,200]) if 'skin' in groups[i] else np.array([215,177,108])
        color=tuple((base*brightness[i]).astype(int))
        poly=[tuple(q) for q in xy[i]];draw.polygon(poly,fill=color)
        draw.line(poly+[poly[0]],fill=tuple(int(c*.6) for c in color),width=1)
    image.paste(im,(left,top))


def render(out,stock,stock_groups,skin,panty,waist_data,crotch_data,source,target,label='2B'):
    im=Image.new('RGB',(1800,1820),'#18202a');draw=ImageDraw.Draw(im)
    font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',22)
    variants=[(stock,stock_groups,'Stock body and cleaned briefs'),
              (np.concatenate((skin,panty)),np.array(['skin']*len(skin)+['briefs']*len(panty)),label+' skin and briefs'),
              (skin,np.array(['skin']*len(skin)),label+' exposed skin only')]
    for col,(p,g,title) in enumerate(variants):
        draw.text((col*600+20,15),title,fill='white',font=font)
        for row,view in enumerate(('front','back','side')):
            draw_panel(im,p,g,(col*600+10,row*570+60,580,540),view)
            draw.text((col*600+20,row*570+65),view,fill='white',font=font)
    draw.text((20,1780),'Native triangles | Blue: observed skin | Gold: clothing envelope and explicit stock flap-root caps',fill='white',font=font)
    im.save(out/'bottoms-coverage.png')
    # Actual front/back shape for two matching triangle patches.
    im=Image.new('RGB',(1600,1300),'#18202a');draw=ImageDraw.Draw(im)
    for row,(name,data,view) in enumerate((('Upper front',waist_data,'front'),('Crotch underside',crotch_data,'under'))):
        mapping,pairs=data
        for col,(mesh,ids,faces,title) in enumerate(((source,list(mapping),list(pairs),'Stock'),
                                                  (target,list(mapping.values()),list(pairs.values()),label))):
            v,t,_,_,_=mesh
            xy=v[:,[0,1 if view=='front' else 2]].copy()
            selected=xy[ids];lo,hi=selected.min(axis=0),selected.max(axis=0)
            scale=min(650/max(hi[0]-lo[0],1),450/max(hi[1]-lo[1],1))
            xy[:,0]=col*800+400+(xy[:,0]-(lo[0]+hi[0])/2)*scale
            xy[:,1]=row*600+330-(xy[:,1]-(lo[1]+hi[1])/2)*scale
            draw.text((col*800+25,row*600+25),name+' shared patch - '+title,fill='white',font=font)
            for face in t[faces]:
                poly=[tuple(x) for x in xy[face]];draw.polygon(poly,fill='#326b72');draw.line(poly+[poly[0]],fill='#94d0ce',width=2)
            for point_label,index in enumerate(ids):
                x,y=xy[index];draw.ellipse((x-4,y-4,x+4,y+4),fill='#f2cb82')
                draw.text((x+5,y-10),str(point_label),fill='#ffdda5',font=font,stroke_width=2,stroke_fill='#18202a')
    draw.text((25,1240),'Same numbered connections in each local patch; deformation and coverage differ. Separate candidate maps.',fill='white',font=font)
    im.save(out/'bottoms-shared-topology.png')


def run(args):
    ref=np.load(args.reference)
    s,sm,_=load_meshes(args.stock);b,bm,_=load_meshes(args.bottoms)
    groups=ref['sourceGroups'];selected=np.char.startswith(groups,'legs_')
    stock=ref['sourcePosition'][selected];stock_groups=groups[selected]
    skin=bm[0][1][0]
    # Mesh 1 contains two lower thigh bands and one central briefs component.
    panty=bm[1][1][0][bm[1][1][0].mean(axis=1)[:,1]>850]
    if len(panty)!=116:
        raise ValueError('Unexpected 2B briefs selection; inspect component layout')
    source,target=prepare(stock),prepare(np.concatenate((skin,panty)))
    distance=np.linalg.norm(source[0][:,None]-target[0][None],axis=2)
    exact=distance.min(axis=1)<.001
    anchors={int(i):int(distance[i].argmin()) for i in np.flatnonzero(exact)}
    seeds=[('upperFront',((0,1031.044,101.187),(54.379,994.667,91.089),(55.497,1035.396,92.242)),
                        ((0,1031.044,101.187),(33.358,990.576,95.118),(55.497,1035.396,92.242))),
           ('crotch',((0,853.147,40.845),(0,854.103,13.002),(17.085,856.138,41.616)),
                     ((0,861.351,32.806),(0,860.422,15.378),(6.690,862.669,31.869)))]
    matches={};data={}
    for name,left,right in seeds:
        mapping,pairs,limits,n=match(source,target,left,right,anchors=anchors);data[name]=(mapping,pairs)
        matches[name]=dict(vertices=len(mapping),triangles=n,limitCounts=dict(Counter(r['reason'] for r in limits)),
                           vertexPairs=[dict(sourcePositionMm=source[0][a].tolist(),targetPositionMm=target[0][b].tolist()) for a,b in mapping.items()],
                           interpretation='Verified local edge and triangle correspondence from anatomical seeds; not a full mesh or maximal match')
    # The two independently seeded maps are not silently merged.
    first,second=data['upperFront'][0],data['crotch'][0]
    conflicts=[int(i) for i in set(first)&set(second) if first[i]!=second[i]]
    surfaces=dict(stockSkin=sm[0][1][0],stockBrief=stock[np.char.find(stock_groups,'smallclothes')>=0],
                  twoBSkin=skin,twoBBrief=panty,curvedReference=ref['position'][ref['triangles']])
    profiles=[]
    for y in (860,880,900,920,940,960,980,1000):
        for x in (0,10,20,40,60,80,100):
            for view,sign in (('front',1),('back',-1)):
                row=dict(view=view,xMm=x,yMm=y)
                row.update({label:ray_z(p,x,y,sign) for label,p in surfaces.items()});profiles.append(row)
    out=Path(args.output);out.mkdir(parents=True,exist_ok=True)
    with (out/'bottoms-depth-profiles.csv').open('w',newline='') as f:
        writer=csv.DictWriter(f,fieldnames=list(profiles[0]));writer.writeheader();writer.writerows(profiles)
    report=dict(item='No.2 Type B Leggings',itemRow=28712,equipmentSet=649,variant=1,imcMask=1023,
                nativeHashes=dict(stock=s['sha256'],twoB=b['sha256']),
                nativeMeshes=[dict(id=m['id'],material=m['material'],vertices=m['vertexCount'],triangles=m['indexCount']//3) for m,_ in bm],
                twoBMesh1Components=dict(briefTriangles=116,thighBandTriangles=144),
                exactCommonUpperBodyVertices=int(exact.sum()),
                commonPositionMm=source[0][exact].tolist(),localTopology=matches,
                independentMapOverlapsWithConflictingAssignments=conflicts,
                minimumClothYmm=dict(stock=float(surfaces['stockBrief'][:,:,1].min()),twoB=float(panty[:,:,1].min())),
                twoBSkinMidlineVertices=int((abs(skin.reshape(-1,3)[:,0])<.001).sum()),
                profiles=profiles,
                scope='Native female Midlander default shape. Stock cleanup and its generated caps are explicit. 2B thigh bands and stockings are excluded from the central briefs topology comparison. No fitted outputs, reference edits, landmark edits or physics solve.')
    (out/'report.json').write_text(json.dumps(report,indent=2)+'\n')
    render(out,stock,stock_groups,skin,panty,data['upperFront'],data['crotch'],source,target)
    print(json.dumps(dict(localTopology={k:dict(vertices=v['vertices'],triangles=v['triangles'],limits=v['limitCounts']) for k,v in matches.items()},
                          exactCommonVertices=int(exact.sum()),minimumClothYmm=report['minimumClothYmm'],
                          backProfilesY940=[r for r in profiles if r['view']=='back' and r['yMm']==940 and r['xMm'] in (0,20,40)]),indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    for key in ('stock','bottoms','reference','output'):p.add_argument('--'+key,required=True,type=Path)
    run(p.parse_args())
