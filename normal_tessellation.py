"""Conforming edge-length refinement with explicit native barycentric lineage."""
import numpy as np


def refine(v,t,max_edge):
    if np.any(np.asarray(max_edge)<=0):raise ValueError('Edge limits must be positive')
    parent=np.arange(len(t));bary=np.repeat(np.eye(3)[None],len(t),axis=0)
    for _ in range(24):
        edges=np.sort(t[:,[[0,1],[1,2],[2,0]]],axis=-1)
        length=np.linalg.norm(v[edges[...,0]]-v[edges[...,1]],axis=-1)
        limit=max_edge if np.isscalar(max_edge) else np.asarray(max_edge)[parent,None]
        selected=np.unique(edges[length>limit],axis=0)
        if not len(selected):return v,t,parent,bary
        mid={tuple(e):len(v)+i for i,e in enumerate(selected)}
        v=np.concatenate((v,(v[selected[:,0]]+v[selected[:,1]])/2))
        nt=[];nb=[];nf=[]
        for tri,bc,owner in zip(t,bary,parent):
            marks=[k for k,(i,j) in enumerate(((0,1),(1,2),(2,0))) if tuple(sorted(tri[[i,j]])) in mid]
            vertices=list(tri);coords=list(bc)
            mids={}
            for k in marks:
                i,j=((0,1),(1,2),(2,0))[k];mids[k]=len(vertices)
                vertices.append(mid[tuple(sorted(tri[[i,j]]))]);coords.append((bc[i]+bc[j])/2)
            if not marks:cells=[[0,1,2]]
            elif len(marks)==1:
                a=marks[0];b=(a+1)%3;c=(a+2)%3;m=mids[a];cells=[[a,m,c],[m,b,c]]
            elif len(marks)==3:
                a,b,c=mids[0],mids[1],mids[2];cells=[[0,a,c],[a,1,b],[c,b,2],[a,b,c]]
            else:
                # Cyclic order: the two marked edges meet at b.
                a=next(k for k in marks if (k+1)%3 in marks);b=(a+1)%3;c=(a+2)%3
                m,n=mids[a],mids[b];cells=[[m,b,n]]
                # Choose the shorter quad diagonal to avoid stretched slivers.
                if np.linalg.norm(v[vertices[a]]-v[vertices[n]])<=np.linalg.norm(v[vertices[m]]-v[vertices[c]]):cells.extend([[a,m,n],[a,n,c]])
                else:cells.extend([[a,m,c],[m,n,c]])
            for cell in cells:nt.append([vertices[k] for k in cell]);nb.append([coords[k] for k in cell]);nf.append(owner)
        t=np.asarray(nt);bary=np.asarray(nb);parent=np.asarray(nf)
    raise ValueError('Edge refinement did not converge')


if __name__=='__main__':
    import argparse
    from pathlib import Path
    from reference_surface import weld
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--reference',default='outputs/vanilla-reference-v1/vanilla-reference.npz')
    p.add_argument('--skin-edge',type=float,default=5.);p.add_argument('--cloth-edge',type=float,default=2.)
    p.add_argument('--output',required=True);a=p.parse_args()
    r=np.load(a.reference);v,t=weld(r['sourcePosition'])
    limit=np.where(np.char.find(r['sourceGroups'],'smallclothes')>=0,a.cloth_edge,a.skin_edge)
    limit[np.char.endswith(r['sourceGroups'],'_cap')]=a.skin_edge
    v,t,parent,bary=refine(v,t,limit)
    path=Path(a.output);path.parent.mkdir(parents=True,exist_ok=True)
    np.savez_compressed(path,position=v,triangles=t,sourceFace=parent,sourceBarycentric=bary)
    print('Vertices:',len(v),'triangles:',len(t))
