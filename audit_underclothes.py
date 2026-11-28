"""Audit separate garment shells and their static fit over the 6 mm body."""
import argparse,json
from pathlib import Path
import numpy as np
from mesh_crossings import crossing_pairs
from repair_surface import boundaries


def run(folder):
    folder=Path(folder);report=json.loads((folder/'extraction-report.json').read_text());body=np.load(report['sourceBody'])
    b=body['position'][body['triangles']];result=[]
    for name in ('bra','briefs'):
        d=np.load(folder/(name+'-shell.npz'));t=d['triangles'];p=d['position'][t]
        assert np.isfinite(p).all() and np.isfinite(d['weights']).all()
        np.testing.assert_allclose(d['weights'].sum(-1),1,atol=1e-12);assert d['weights'].min()>=0
        connectivity=boundaries(t);assert connectivity['openBoundaryLoops']==0
        against_body=crossing_pairs(p,b);self_cross=crossing_pairs(p,p,self_faces=t)
        np.savez_compressed(folder/(name+'-crossings.npz'),bodyPairs=against_body,selfPairs=self_cross)
        row=dict(name=name,bodyTriangleCrossings=len(against_body),nonAdjacentSelfCrossings=len(self_cross),connectivity=connectivity)
        result.append(row);print(json.dumps(row),flush=True)
    audit=dict(status='Passed' if all(r['bodyTriangleCrossings']==0 and r['nonAdjacentSelfCrossings']==0 for r in result) else 'Crossings found; fitted-shell test not accepted',components=result,
      scope='Static AABB-complete segment/triangle and coplanar triangle checks with adjacent faces excluded for self checks. Does not validate animation, physics, fastening or real-world cloth thickness.')
    (folder/'shell-audit.json').write_text(json.dumps(audit,indent=2)+'\n');print(json.dumps(audit,indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('folder');run(p.parse_args().folder)
