"""Find a seeded common chest triangle complex, allowing vertex deformation.

This verifies a local connectivity correspondence, not common authoring history
or a maximal match. The fit reference is not modified.
"""
import argparse
from collections import Counter, deque
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

from investigate_coatee import load_meshes
from reference_surface import weld


def prepare(p):
    centre=p.mean(axis=1)
    selected=np.flatnonzero((centre[:,2]>0)&(centre[:,1]>1180)&(centre[:,1]<1330)&(abs(centre[:,0])<130))
    v,t=weld(p[selected]); adjacency={}; edges=set()
    for i,face in enumerate(t):
        for a,b in ((0,1),(1,2),(2,0)):
            key=tuple(sorted(face[[a,b]]));adjacency.setdefault(key,[]).append(i);edges.add(key)
    return v,t,adjacency,edges,selected


def vertex_at(vertices, point):
    d=np.linalg.norm(vertices-np.array(point),axis=1)
    i=int(d.argmin())
    if d[i]>.01:
        raise ValueError("Seed is not the inspected native vertex")
    return i


def match(source,target,seed_source=None,seed_target=None,anchors=None):
    v,t,ad,ae,_=source;w,u,bd,be,_=target
    # Inspectable lower-midline triangle: centre, next centre row, right inner point.
    # These establish the anatomical seed; connectivity determines propagation.
    if seed_source is None:
        seed_source=((0,1194.596,105.210),(0,1218.558,103.554),(15.33,1218.94,103.14))
    if seed_target is None:
        seed_target=((0,1199.584,104.611),(0,1219.531,101.957),(13.22,1217.23,104.47))
    a=[vertex_at(v,p) for p in seed_source]
    b=[vertex_at(w,p) for p in seed_target]
    anchors={} if anchors is None else anchors
    reserved={value:key for key,value in anchors.items()}
    if any((i in anchors and anchors[i]!=j) or (j in reserved and reserved[j]!=i) for i,j in zip(a,b)):
        raise ValueError('Seed contradicts a fixed correspondence')
    si=next(i for i,f in enumerate(t) if set(f)==set(a))
    ti=next(i for i,f in enumerate(u) if set(f)==set(b))
    mapping=dict(zip(a,b));reverse=dict(zip(b,a));pairs={si:ti};todo=deque([(si,ti)])
    limits=[]
    while todo:
        i,j=todo.popleft()
        for aa,bb in ((0,1),(1,2),(2,0)):
            x,y=t[i,[aa,bb]]
            rows=ad[tuple(sorted((x,y)))];peers=bd.get(tuple(sorted((mapping[x],mapping[y]))),[])
            if len(rows)!=2 or len(peers)!=2:
                continue
            ni=next(k for k in rows if k!=i);nj=next(k for k in peers if k!=j)
            a=next(k for k in t[ni] if k not in (x,y))
            b=next(k for k in u[nj] if k not in (mapping[x],mapping[y]))
            reason=None
            if (a in anchors and anchors[a]!=b) or (b in reserved and reserved[b]!=a):
                reason="conflicts with an unchanged shared position anchor"
            elif (a in mapping and mapping[a]!=b) or (b in reverse and reverse[b]!=a) or (ni in pairs and pairs[ni]!=nj):
                reason="inconsistent vertex or face assignment"
            elif (abs(v[a,0])<.001)!=(abs(w[b,0])<.001):
                reason="centreline would map off centre"
            elif np.linalg.norm(v[a]-w[b])>45 or v[a,0]*w[b,0]<-.01:
                reason="outside spatial or left right guard"
            elif any((tuple(sorted((a,old))) in ae)!=(tuple(sorted((b,new))) in be)
                     for old,new in mapping.items() if old!=a):
                reason="extra or missing edge between mapped vertices"
            if reason:
                limits.append(dict(sourceFace=int(ni),targetFace=int(nj),reason=reason));continue
            mapping[a]=b;reverse[b]=a
            if ni not in pairs:
                pairs[ni]=nj;todo.append((ni,nj))
    # Independent replay over the entire retained vertex/triangle mapping.
    mapped_source={tuple(sorted(mapping[x] for x in face)) for face in t if all(x in mapping for x in face)}
    mapped_target={tuple(sorted(face)) for face in u if all(x in reverse for x in face)}
    if mapped_source!=mapped_target:
        raise ValueError("Mapped triangle complexes disagree")
    for a,aa in mapping.items():
        for b,bb in mapping.items():
            if a!=b and ((tuple(sorted((a,b))) in ae)!=(tuple(sorted((aa,bb))) in be)):
                raise ValueError("Mapped edge graphs disagree")
    return mapping,pairs,limits,len(mapped_source)


def render(out,source,target,mapping,pairs):
    image=Image.new('RGB',(1600,920),'#18202a');draw=ImageDraw.Draw(image)
    font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',24)
    small=ImageFont.truetype('C:/Windows/Fonts/segoeuib.ttf',18)
    for column,(data,title,ids,matched) in enumerate(((source,'Stock bra after flap cleanup',list(mapping),list(pairs)),
                                                   (target,'Coatee skin with remapped vertex identities',list(mapping.values()),list(pairs.values())))):
        v,t,_,_,_=data
        draw.text((column*800+25,20),title,fill='white',font=font)
        xy=v[:,[0,1]].copy();xy[:,0]=xy[:,0]*3+column*800+400;xy[:,1]=(1335-xy[:,1])*4.5+70
        for face in t:
            poly=[tuple(p) for p in xy[face]];draw.line(poly+[poly[0]],fill='#354554',width=1)
        for face in t[matched]:
            poly=[tuple(p) for p in xy[face]];draw.polygon(poly,fill='#326b72');draw.line(poly+[poly[0]],fill='#94d0ce',width=2)
        for label,vertex in enumerate(ids):
            x,y=xy[vertex];draw.ellipse((x-4,y-4,x+4,y+4),fill='#f2cb82');draw.text((x+5,y-10),str(label),font=small,fill='#ffdda5',stroke_width=2,stroke_fill='#18202a')
    draw.text((25,815),'Same numbered vertices and triangle connections; different positions and surrounding cuts.',font=font,fill='white')
    draw.text((25,860),'Seeded local correspondence only. No reference change or fitting operation.',font=font,fill='#b7c5d1')
    image.save(out/'shared-chest-topology.png')


def run(args):
    reference=np.load(args.reference)
    bra=reference['sourcePosition'][reference['sourceGroups']=='top_smallclothes']
    _,meshes,_=load_meshes(args.coatee,977)
    skin=next(a[0] for m,a in meshes if 'b0001_' in m['material'])
    source,target=prepare(bra),prepare(skin)
    mapping,pairs,limits,face_count=match(source,target)
    v,t,_,_,_=source;w,u,_,_,_=target
    out=Path(args.output);out.mkdir(parents=True,exist_ok=True)
    report=dict(status='Verified seeded local connectivity correspondence; not a maximal or full mesh match',
                vertices=len(mapping),triangles=face_count,propagatedTrianglePairs=len(pairs),
                checks=['Injective vertex map','All edges between mapped vertices agree','All triangles on mapped vertices agree'],
                source='Stock bra native faces retained by cleanup; before density refinement',
                target='Female Midlander Coatee skin mesh, IMC 977',
                guards='Inspected anatomical seed; preserve centreline and left/right side; maximum 45 mm displacement',
                vertexPairs=[dict(label=i,sourceVertex=int(a),targetVertex=int(b),sourcePositionMm=v[a].tolist(),targetPositionMm=w[b].tolist())
                             for i,(a,b) in enumerate(mapping.items())],
                facePairs=[dict(sourceFace=int(a),targetFace=int(b)) for a,b in pairs.items()],
                propagationLimits=limits,limitCounts=dict(Counter(r['reason'] for r in limits)),
                interpretation='Matching local polygon layout supports topology-based fitting correspondence. Full mesh counts and shifted vertex positions do not disprove a local morph. Shared authoring origin and extension through flagged areas are not established.')
    (out/'topology-report.json').write_text(json.dumps(report,indent=2)+'\n')
    render(out,source,target,mapping,pairs)
    print(json.dumps({k:report[k] for k in ('status','vertices','triangles','checks','limitCounts')}))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--reference',type=Path,required=True)
    parser.add_argument('--coatee',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    run(parser.parse_args())
