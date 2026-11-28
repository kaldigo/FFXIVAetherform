"""Cross-check size selection with area-weighted bidirectional surface samples."""
import argparse
import json
from pathlib import Path

import numpy as np

from compare_af_bases import closest_surface, load_af, stats
from reference_surface import areas


def run(args):
    ref=np.load(args.reference);p=ref['position'][ref['triangles']]
    out=Path(args.output);out.mkdir(parents=True,exist_ok=True)
    rows=[]
    for key,label,slot in [('af_top_00','XS','top'),('af_top_01','Small','top'),
                           ('af_top_02','Medium','top'),('af_top_03','Large','top'),
                           ('af_dwn_04','Medium B','legs'),('af_dwn_01','Large B','legs')]:
        asset=load_af(Path(args.bodies)/(key+'.json'));mesh=asset['position']
        source=p[np.char.startswith(ref['group'],slot)]
        a,_,_=closest_surface(mesh.mean(axis=1),source)
        b,_,_=closest_surface(source.mean(axis=1),mesh)
        forward,reverse=stats(a,areas(mesh)),stats(b,areas(source))
        skin=np.char.endswith(ref['group'][np.char.startswith(ref['group'],slot)],'_skin')
        row=dict(id=key,preset=label,slot=slot,sha256=asset['sha256'],afToVanilla=forward,vanillaToAf=reverse,
                 observedVanillaSkinToAf=stats(b[skin],areas(source)[skin]),
                 symmetricRms=float(np.sqrt((forward['rms']**2+reverse['rms']**2)/2)))
        rows.append(row);print(json.dumps(dict(preset=label,slot=slot,symmetricRms=row['symmetricRms'],forwardRms=forward['rms'],reverseRms=reverse['rms'])),flush=True)
    report=dict(method='Area-weighted exact closest-triangle distances at native and reference triangle centres. No registration. Separate from semantic landmark correspondence.',
                limits='Sampled proximity only; a near surface is not necessarily the correct material or anatomical correspondence.',candidates=rows)
    (out/'surface-ranking.json').write_text(json.dumps(report,indent=2)+'\n')


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    for key in ('reference','bodies','output'):p.add_argument('--'+key,required=True,type=Path)
    run(p.parse_args())
