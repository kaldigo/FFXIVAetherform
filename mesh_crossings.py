"""AABB-complete triangle crossing checks, including coplanar overlap."""
import numpy as np
from scipy.spatial import cKDTree


def segment_hit(a,b,tri,tolerance):
    direction=b-a;e1=tri[:,1]-tri[:,0];e2=tri[:,2]-tri[:,0]
    h=np.cross(direction,e2);det=np.sum(e1*h,axis=1);good=abs(det)>tolerance
    inv=np.zeros(len(det));inv[good]=1/det[good]
    s=a-tri[:,0];u=np.sum(s*h,axis=1)*inv;q=np.cross(s,e1)
    v=np.sum(direction*q,axis=1)*inv;t=np.sum(e2*q,axis=1)*inv
    return good&(u>=-tolerance)&(v>=-tolerance)&(u+v<=1+tolerance)&(t>=-tolerance)&(t<=1+tolerance)


def paired_intersections(a,b,tolerance=1e-8):
    hit=np.zeros(len(a),bool)
    for i,j in ((0,1),(1,2),(2,0)):
        hit|=segment_hit(a[:,i],a[:,j],b,tolerance)
        hit|=segment_hit(b[:,i],b[:,j],a,tolerance)
    n=np.cross(a[:,1]-a[:,0],a[:,2]-a[:,0]);length=np.linalg.norm(n,axis=1)
    plane=np.max(abs(np.einsum('fij,fj->fi',b-a[:,:1],n)),axis=1)/np.maximum(length,1e-30)
    other=np.cross(b[:,1]-b[:,0],b[:,2]-b[:,0]);parallel=np.linalg.norm(np.cross(n,other),axis=1)<tolerance*length*np.linalg.norm(other,axis=1)
    for k in np.flatnonzero(~hit & parallel & (plane<tolerance)):
        drop=int(np.argmax(abs(n[k])));axis=[i for i in range(3) if i!=drop];p=a[k][:,axis];q=b[k][:,axis];overlap=True
        for tri in (p,q):
            edge=np.roll(tri,-1,axis=0)-tri
            for e in edge:
                v=np.array([-e[1],e[0]]);x,y=p@v,q@v
                if x.max()<y.min()-tolerance or y.max()<x.min()-tolerance:overlap=False;break
            if not overlap:break
        hit[k]=overlap
    return hit


def crossing_pairs(a,b,self_faces=None,tolerance=1e-8):
    centres=b.mean(1);radius=np.linalg.norm(b-centres[:,None],axis=-1).max(1)
    tree=cKDTree(centres);ca=a.mean(1);ra=np.linalg.norm(a-ca[:,None],axis=-1).max(1)
    amin,amax=a.min(1),a.max(1);bmin,bmax=b.min(1),b.max(1)
    result=[]
    for start in range(0,len(a),1000):
        neighbors=tree.query_ball_point(ca[start:start+1000],ra[start:start+1000]+radius.max()+tolerance)
        ia=np.repeat(np.arange(start,start+len(neighbors)),[len(n) for n in neighbors])
        if not len(ia):continue
        ib=np.concatenate(neighbors).astype(int)
        use=np.all(amin[ia]<=bmax[ib]+tolerance,axis=1)&np.all(bmin[ib]<=amax[ia]+tolerance,axis=1)
        if self_faces is not None:
            use&=ia<ib
            use&=~np.any(self_faces[ia,:,None]==self_faces[ib,None,:],axis=(1,2))
        ia,ib=ia[use],ib[use]
        if len(ia):
            hit=paired_intersections(a[ia],b[ib],tolerance);result.extend(zip(ia[hit].tolist(),ib[hit].tolist()))
    return np.array(result,dtype=int).reshape(-1,2)
