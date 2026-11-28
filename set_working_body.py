"""Snapshot the user-accepted 4x working body and record artifact hashes."""
import argparse,hashlib,json,shutil
from pathlib import Path
import numpy as np


def digest(path):return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def run(args):
    source=Path(args.source);destination=Path(args.output)
    data=np.load(source/'body-fit-reference.npz');points=np.load(source/'paired-landmarks-candidate.npz')
    assert len(data['position'])==47904 and len(data['triangles'])==95630 and len(points['names'])==948
    np.testing.assert_array_equal(points['sourcePosition'],np.einsum('ni,nij->nj',points['sourceBary'],data['position'][data['triangles'][points['sourceFace']]]))
    names=['body-fit-reference.npz','vanilla-body-fit.obj','paired-landmarks-candidate.npz','Vanilla-BodyFit.points.json',
        'protected-crotch-report.json','envelope-audit.json','normal-update-audit.json','crotch-shape-review.json']
    for name in names:
        src,dst=source/name,destination/name
        if not src.exists():raise ValueError('Missing accepted artifact: '+str(src))
        if dst.exists() and digest(dst)!=digest(src):raise ValueError('Working snapshot already differs: '+str(dst))
    destination.mkdir(parents=True,exist_ok=True);artifacts={}
    for name in names:
        src,dst=source/name,destination/name
        if not dst.exists():shutil.copyfile(src,dst)
        artifacts[name]=dict(path=dst.as_posix(),sha256=digest(dst))
    manifest=dict(schema=1,status='User-accepted working mesh before bra-seam/cleavage post-processing',acceptedDate='2026-11-28',
        sourceFolder=source.as_posix(),workingFolder=destination.as_posix(),
        densityRole='4x AF-density wrap working surface; existing 1x mapping surface is unchanged',
        vertices=len(data['position']),triangles=len(data['triangles']),pairedLandmarks=len(points['names']),artifacts=artifacts,
        pendingPostProcessing=['Bra band/seam reconstruction','Cleavage and generated medial-root reconstruction','Bra strap/clasp review'],
        preservation='Use separate output folders for post-processing trials. Keep this accepted snapshot and the lower-body/under-breast shape available for exact comparison.')
    Path(args.manifest).write_text(json.dumps(manifest,indent=2)+'\n');print(json.dumps(manifest,indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--source',default='outputs/body-protected-crotch-4x')
    p.add_argument('--output',default='outputs/working-body-4x');p.add_argument('--manifest',default='working-mesh.json');run(p.parse_args())
