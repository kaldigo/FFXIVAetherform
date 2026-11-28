"""Use the native Summer Tanga as an independent lower-body comparison."""
import argparse
from collections import Counter
import csv
import json
from pathlib import Path

import numpy as np

from compare_chest_topology import match
from investigate_bottoms import prepare, ray_z, render
from investigate_coatee import load_meshes


def run(args):
    ref=np.load(args.reference)
    s,sm,_=load_meshes(args.stock);t,tm,_=load_meshes(args.tanga);b,bm,_=load_meshes(args.two_b)
    items=json.loads(Path(args.items).read_text());item=next(r for r in items if r['id']==6105)
    if item['set']!=49 or item['variant']!=6:
        raise ValueError('Unexpected Black Summer Tanga item definition')
    census=json.loads(Path(args.census).read_text())['modelFiles']
    for model,number in ((t,49),(b,649)):
        row=next(r for r in census if r['set']==number and r['slot']=='dwn' and r['source']==201)
        if row['bytes'].lower()!=model['sha256']:
            raise ValueError('Comparison MDL differs from the native game census')
    skin=next(a[0] for m,a in tm if 'b0001_' in m['material'])
    cloth_mesh,arrays=next((m,a) for m,a in tm if 'e0049_' in m['material'])
    # Keep the main unconditional cloth surface. Attribute-tagged tail and
    # auxiliary geometry are explicit exclusions, not part of the body shape.
    keep=np.zeros(len(arrays[0]),bool)
    for sub in t['tables']['SubMeshes'][cloth_mesh['subMeshStart']:cloth_mesh['subMeshStart']+cloth_mesh['subMeshCount']]:
        if sub['AttributeIndexMask']==0:
            start=(sub['IndexOffset']-cloth_mesh['startIndex'])//3
            keep[start:start+sub['IndexCount']//3]=True
    cloth=arrays[0][keep]
    selected=np.char.startswith(ref['sourceGroups'],'legs_')
    stock=ref['sourcePosition'][selected];groups=ref['sourceGroups'][selected]
    source,target=prepare(stock),prepare(np.concatenate((skin,cloth)))
    distance=np.linalg.norm(source[0][:,None]-target[0][None],axis=2)
    exact=distance.min(axis=1)<.001
    anchors={int(i):int(distance[i].argmin()) for i in np.flatnonzero(exact)}
    seeds=[('upperFront',((0,1031.044,101.187),(54.379,994.667,91.089),(55.497,1035.396,92.242)),
                        ((0,1031.044,101.187),(54.379,994.667,91.089),(55.497,1035.396,92.242))),
           ('crotch',((0,853.147,40.845),(0,854.103,13.002),(17.085,856.138,41.616)),
                     ((0,853.111,42.777),(0,853.886,14.152),(17.619,855.868,40.910)))]
    matches={};data={}
    for name,seed_source,seed_target in seeds:
        mapping,pairs,limits,n=match(source,target,seed_source,seed_target,anchors=anchors);data[name]=(mapping,pairs)
        movement=np.array([np.linalg.norm(source[0][i]-target[0][j]) for i,j in mapping.items()])
        matches[name]=dict(vertices=len(mapping),triangles=n,limitCounts=dict(Counter(r['reason'] for r in limits)),
                           vertexPairs=[dict(sourcePositionMm=source[0][i].tolist(),targetPositionMm=target[0][j].tolist()) for i,j in mapping.items()],
                           displacementMm=dict(max=float(movement.max()),median=float(np.median(movement))),
                           interpretation='A seeded local correspondence verified against all mapped edges and triangles; not a maximal or complete fit map')
    first,second=data['upperFront'][0],data['crotch'][0]
    conflicts=[int(i) for i in set(first)&set(second) if first[i]!=second[i]]
    surfaces=dict(stockSkin=sm[0][1][0],stockBrief=stock[np.char.find(groups,'smallclothes')>=0],
                  tangaSkin=skin,tangaBrief=cloth,twoBSkin=bm[0][1][0],
                  twoBBrief=bm[1][1][0][bm[1][1][0].mean(axis=1)[:,1]>850],
                  curvedReference=ref['position'][ref['triangles']])
    profiles=[]
    for y in (860,880,900,920,940,960,980,1000):
        for x in (0,10,20,40,60,80,100):
            for view,sign in (('front',1),('back',-1)):
                row=dict(view=view,xMm=x,yMm=y)
                row.update({label:ray_z(p,x,y,sign) for label,p in surfaces.items()});profiles.append(row)
    out=Path(args.output);out.mkdir(parents=True,exist_ok=True)
    with (out/'tanga-depth-profiles.csv').open('w',newline='') as f:
        writer=csv.DictWriter(f,fieldnames=list(profiles[0]));writer.writeheader();writer.writerows(profiles)
    report=dict(item=item,nativeHashes=dict(stock=s['sha256'],tanga=t['sha256'],twoB=b['sha256']),
                tangaNativeGameHashVerified=True,twoBNativeGameHashVerified=True,
                tangaNativeMeshes=[dict(id=m['id'],material=m['material'],vertices=m['vertexCount'],triangles=m['indexCount']//3) for m,_ in tm],
                includedMainClothTriangles=len(cloth),excludedAuxiliaryClothTriangles=int((~keep).sum()),
                exactSharedVerticesAbove810mm=len(anchors),localTopology=matches,
                independentMapOverlapsWithConflictingAssignments=conflicts,
                minimumMainClothYmm={label:float(p[:,:,1].min()) for label,p in surfaces.items() if label in ('stockBrief','tangaBrief','twoBBrief')},
                posteriorSample=next(r for r in profiles if r['view']=='back' and r['xMm']==60 and r['yMm']==860),
                profiles=profiles,
                scope='Native female Midlander default meshes. Tanga material variant 6 changes appearance; model shared across Summer Tanga colours. No reference, landmarks, previous assets, live game files or wrapping solve modified.')
    (out/'report.json').write_text(json.dumps(report,indent=2)+'\n')
    render(out,stock,groups,skin,cloth,data['upperFront'],data['crotch'],source,target,label='Summer Tanga')
    print(json.dumps({k:report[k] for k in ('includedMainClothTriangles','excludedAuxiliaryClothTriangles','exactSharedVerticesAbove810mm','minimumMainClothYmm','posteriorSample','independentMapOverlapsWithConflictingAssignments')},indent=2))
    print(json.dumps({k:{f:v[f] for f in ('vertices','triangles','limitCounts','displacementMm')} for k,v in matches.items()},indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    for key in ('reference','stock','tanga','two-b','items','census','output'):p.add_argument('--'+key,required=True,type=Path)
    run(p.parse_args())
