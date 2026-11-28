"""Inspect the native Coatee skin against stock skin and its bra envelope."""
import argparse
import csv
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

from reference_surface import load_snapshot, mesh_arrays
from render_review import panel


def load_meshes(path, imc_mask=None):
    snapshot, data, meshes = load_snapshot(path)
    bones = snapshot["tables"]["Bones"]
    result=[]
    for m in meshes:
        arrays=mesh_arrays(snapshot,data,m,bones)
        keep=np.ones(len(arrays[0]),bool)
        if imc_mask is not None:
            disabled={i for i,name in enumerate(snapshot["tables"]["Attributes"])
                      if name.startswith("atr_tv_") and len(name)==8 and
                      not imc_mask & (1<<(ord(name[-1])-ord('a')))}
            for sub in snapshot["tables"]["SubMeshes"][m["subMeshStart"]:m["subMeshStart"]+m["subMeshCount"]]:
                if any(sub["AttributeIndexMask"] & (1<<i) for i in disabled):
                    first=(sub["IndexOffset"]-m["startIndex"])//3
                    keep[first:first+sub["IndexCount"]//3]=False
        result.append((m,tuple(a[keep] for a in arrays)))
    return snapshot,result,bones


def front_depth(triangles, xy):
    """Exact +Z-most triangle intersection of an XY-addressed depth ray."""
    xy = np.asarray(xy, float)
    a = triangles[:, 0, :2]
    edges = np.stack((triangles[:, 1, :2]-a, triangles[:, 2, :2]-a), axis=2)
    valid = np.abs(np.linalg.det(edges)) > 1e-10
    ids = np.flatnonzero(valid)
    uv = np.linalg.solve(edges[valid], (xy-a[valid])[..., None])[..., 0]
    bary = np.c_[1-uv.sum(axis=1), uv]
    inside = (bary.min(axis=1) >= -1e-8) & (bary.max(axis=1) <= 1+1e-8)
    if not inside.any():
        return None
    ids, bary = ids[inside], bary[inside]
    z = np.einsum("ni,ni->n", bary, triangles[ids, :, 2])
    i = z.argmax()
    if z[i] <= 0:
        return None  # A back-surface hit does not supply the missing front chest.
    return dict(zMm=float(z[i]), face=int(ids[i]), bary=bary[i].tolist())


def run(args):
    out = Path(args.output); out.mkdir(parents=True, exist_ok=True)
    stock, stock_m, stock_bones = load_meshes(args.stock)
    item=next(r for r in json.loads(Path(args.items).read_text()) if r['id']==3092)
    if item['set']!=38 or item['variant']!=6:
        raise ValueError("Unexpected item model; inspect current item data")
    garment, garment_m, garment_bones = load_meshes(args.coatee,item['mask'])
    stock_skin = next(a for m,a in stock_m if "b0001_" in m["material"])
    bra = next(a for m,a in stock_m if "e0000_" in m["material"])
    skin = next(a for m,a in garment_m if "b0001_" in m["material"])
    cloth = next(a for m,a in garment_m if "e0038_" in m["material"])
    surfaces = dict(stockSkin=stock_skin[0], stockBra=bra[0], coateeSkin=skin[0], coateeCloth=cloth[0])
    current=np.load(args.reference)
    surfaces['curvedReference']=current['position'][current['triangles']]
    profiles = []
    for y in (1190., 1200., 1210., 1220., 1230., 1240., 1250., 1260., 1270., 1280.):
        for x in (-80., -60., -40., -20., -15., -10., -5., 0., 5., 10., 15., 20., 40., 60., 80.):
            row = dict(xMm=x, yMm=y)
            for label, p in surfaces.items():
                hit = front_depth(p, (x,y))
                row[label] = hit["zMm"] if hit else None
            profiles.append(row)
    with (out/"chest-depth-profiles.csv").open("w", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=list(profiles[0])); writer.writeheader(); writer.writerows(profiles)
    covered = []
    for i,p in enumerate(skin[0]):
        if not (1160 < p.mean(axis=0)[1] < 1320 and abs(p.mean(axis=0)[0]) < 130 and p.mean(axis=0)[2] > 0):
            continue
        for k, q in enumerate(p):
            nearest_stock = np.linalg.norm(stock_skin[0].reshape(-1,3)-q, axis=1).min()
            if nearest_stock > 1e-3:
                covered.append(dict(face=i, corner=k, positionMm=q.tolist(),
                                    nearestStockSkinVertexMm=float(nearest_stock),
                                    breastWeight=float(sum(skin[3][i,k,garment_bones.index(b)]
                                                           for b in ("j_mune_l","j_mune_r")))))
    report = dict(item=item,
                  coatee=dict(snapshot=str(Path(args.coatee).resolve()), source=garment["sourcePath"],
                              sha256=garment["sha256"], meshes=[dict(id=m["id"],material=m["material"],
                                                                   vertices=m["vertexCount"],triangles=m["indexCount"]//3,
                                                                   visibleTriangles=len(a[0])) for m,a in garment_m]),
                  stock=dict(source=stock["sourcePath"],sha256=stock["sha256"]),
                  scope="Native female Midlander c0201 rest pose. Mesh geometry only; normal textures and breast-size skeleton transforms not evaluated. Equipment variants filtered by the actual gathering item IMC mask; measured central chest skin is in the unconditional submesh.",
                  profiles=profiles, coateeChestCornersWithoutMatchingStockSkinVertex=covered)
    centre=next(r for r in profiles if r['xMm']==0 and r['yMm']==1250)
    sides=[next(r for r in profiles if r['xMm']==x and r['yMm']==1250) for x in (-10,10)]
    report['cleavageSectionY1250mm']=dict(centreDepthMm=centre['coateeSkin'],
                                        sideDepthAt10mm=[r['coateeSkin'] for r in sides],
                                        centreRecessVsSidesMm=[r['coateeSkin']-centre['coateeSkin'] for r in sides],
                                        curvedReferenceCentreOffsetMm=centre['curvedReference']-centre['coateeSkin'],
                                        clothOccludesTheseThreeSamples=any(r['coateeCloth'] is not None and r['coateeCloth']>r['coateeSkin'] for r in [centre]+sides))
    (out/"report.json").write_text(json.dumps(report,indent=2)+"\n")
    im = Image.new("RGB", (1800,1100), "#18202a"); draw=ImageDraw.Draw(im)
    font = ImageFont.truetype("C:/Windows/Fonts/segoeui.ttf",22)
    variants=[(stock_skin[0],np.array(["skin"]*len(stock_skin[0])),"Stock exposed skin only"),
              (np.concatenate((stock_skin[0],bra[0])),np.array(["skin"]*len(stock_skin[0])+["bra"]*len(bra[0])),"Stock skin plus bra envelope"),
              (np.concatenate((skin[0],cloth[0])),np.array(["skin"]*len(skin[0])+["cloth"]*len(cloth[0])),"Coatee owns its exposed skin")]
    for col,(p,g,title) in enumerate(variants):
        facing=np.cross(p[:,1]-p[:,0],p[:,2]-p[:,0])[:,2]>0
        p,g=p[facing],g[facing]
        draw.text((col*600+20,16),title,fill="white",font=font)
        panel(im,p,g,(col*600+10,60,580,650),"front",(-145,145,1140,1340),True)
    draw.text((20,735),"Coatee skin alone - the chest patch exists in the equipment MDL",fill="white",font=font)
    p=skin[0];p=p[np.cross(p[:,1]-p[:,0],p[:,2]-p[:,0])[:,2]>0]
    panel(im,p,np.array(["skin"]*len(p)),(20,775,550,295),"front",(-120,120,1160,1320),True)
    # Depth sections are actual triangle-ray intersections, without shading normals.
    for index,y in enumerate((1220.,1240.)):
        left,top,width,height=620+index*580,800,540,245
        draw.text((left,765),f"Front surface depth at Y {int(y)} mm",fill="white",font=font)
        colors=dict(stockSkin="#69bfd1",stockBra="#e4bc70",coateeSkin="#ee8780")
        for name,color in colors.items():
            line=[]
            for x in np.linspace(-100,100,201):
                hit=front_depth(surfaces[name],(x,y))
                if hit is None:
                    if len(line)>1: draw.line(line,fill=color,width=3)
                    line=[];continue
                z=hit["zMm"]
                if z<0:
                    if len(line)>1: draw.line(line,fill=color,width=3)
                    line=[];continue
                line.append((left+(x+100)/200*width,top+(150-z)/150*height))
            if len(line)>1:draw.line(line,fill=color,width=3)
        draw.text((left,1048),"Cyan skin | Gold bra | Red coatee skin",fill="white",font=ImageFont.truetype("C:/Windows/Fonts/segoeui.ttf",17))
    im.save(out/"chest-coverage.png")
    print(json.dumps(dict(meshes=report["coatee"]["meshes"], centralProfiles=[r for r in profiles if r['xMm']==0],
                          inspectedNewChestCorners=len(covered)),indent=2))


if __name__ == "__main__":
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument("--stock",type=Path,required=True); p.add_argument("--coatee",type=Path,required=True)
    p.add_argument("--items",type=Path,required=True); p.add_argument("--reference",type=Path,required=True)
    p.add_argument("--output",type=Path,required=True)
    run(p.parse_args())
