"""Original material normal maps transformed through the stored MDL frame."""
import hashlib
import json
from pathlib import Path
import numpy as np
from reference_surface import load_snapshot,field,unit,mesh_arrays


def bilinear(image,uv):
    h,w=image.shape[:2];q=uv*np.array([w,h])-.5;i=np.floor(q).astype(int);f=q-i
    x,y=i[...,0]%w,i[...,1]%h;xx,yy=(x+1)%w,(y+1)%h
    return ((image[y,x]*(1-f[...,0,None])+image[y,xx]*f[...,0,None])*(1-f[...,1,None])+
            (image[yy,x]*(1-f[...,0,None])+image[yy,xx]*f[...,0,None])*f[...,1,None])


def tangent_normal(rg,strength=1.):
    xy=rg-.5;z=np.sqrt(np.maximum(.25-np.sum(xy*xy,axis=-1),1e-6))
    return unit(np.concatenate((xy*np.asarray(strength)[...,None],z[...,None]),axis=-1))


def stored_frame(normal,packed_binormal):
    n=unit(normal);b=unit(packed_binormal[...,:3]*2-1)
    hand=np.sign(packed_binormal[...,3]*2-1)
    t=unit(np.cross(b,n))*hand[...,None]
    return n,t,b


class NormalField:
    def __init__(self,reference,assets='outputs/normal-map-assets-v1'):
        self.reference=reference;folder=Path(assets)
        materials=json.loads((folder/'materials.json').read_text());self.maps=[];self.textures=[]
        self.strength=[]
        for m in materials:
            sampler=next(s for s in m['material']['ShaderPackage']['Samplers'] if s['SamplerId']==0x0c5ec1f1)
            tex=m['textures'][sampler['TextureIndex']]
            path=folder/(Path(tex['decoded']).stem+'.tex')
            if hashlib.sha256(path.read_bytes()).hexdigest()!=tex['sha256']:raise ValueError('Texture hash mismatch')
            img=np.fromfile(folder/tex['decoded'],np.uint8).reshape(tex['height'],tex['width'],4)[...,[2,1]].astype(float)/255
            self.maps.append(img);self.textures.append(tex)
            constants=np.fromfile(folder/(Path(m['path']).stem+'.constants'),dtype='<f4')
            self.strength.append(float(constants[11*4+1]) if m['material']['ShaderPackage']['Name']=='character.shpk' else 1.)
        p=reference['sourcePosition'];self.n=reference['sourceNormal'];self.uv=np.zeros((len(p),3,2))
        self.b=np.zeros_like(p);self.t=np.zeros_like(p);self.material=np.full(len(p),-1,int)
        self.weights=np.zeros((len(p),3,len(reference['boneNames'])))
        loaded={}
        model_hashes=json.loads((folder/'model-hashes.json').read_text())
        for slot,file in (('top','stock-top.json'),('legs','stock-dwn.json')):
            s,raw,meshes=load_snapshot(Path('../.work/uv-stock-lines-20261119/decoded')/file)
            if s['sha256']!=model_hashes['top' if slot=='top' else 'dwn']:raise ValueError('Installed model and snapshot versions differ')
            for mesh in meshes:
                ids=np.frombuffer(raw,'<u2',mesh['indexCount'],mesh['indexOffset']).reshape(-1,3)
                normal,tangent,binormal=stored_frame(field(raw,mesh,3)[ids,:3],field(raw,mesh,6)[ids])
                uv=field(raw,mesh,4)[ids,:2]
                _,_,_,weights=mesh_arrays(s,raw,mesh,list(reference['boneNames']))
                loaded[slot,mesh['id']]=(normal,tangent,binormal,uv,weights)
        self.valid=np.zeros(len(p),bool)
        for i,(slot,mid,first) in enumerate(reference['sourceIdentity']):
            mid,first=int(mid),int(first)
            if mid<0:continue
            n,t,b,uv,weights=loaded[str(slot),mid];j=first//3
            np.testing.assert_allclose(self.n[i],n[j],atol=1e-12)
            self.t[i],self.b[i],self.uv[i]=t[j],b[j],uv[j]
            self.weights[i]=weights[j]
            self.material[i]=0 if mid==0 else 1 if slot=='top' else 2;self.valid[i]=True
        # Generated caps have no native material identity. Recover their
        # provisional corner attributes from the old exact barycentric lineage.
        for i in np.flatnonzero(~self.valid):
            faces=np.flatnonzero(reference['sourceFace']==i)
            bc=reference['sourceBarycentric'][faces].reshape(-1,3)
            for k in range(3):
                row=int(np.argmin(np.linalg.norm(bc-np.eye(3)[k],axis=1)))
                self.weights[i,k]=reference['weights'][faces].reshape(-1,len(reference['boneNames']))[row]
                self.uv[i,k]=reference['uv'][faces].reshape(-1,2)[row]

    def sample(self,parents,bary,texture=True):
        parents=np.asarray(parents);bary=np.asarray(bary)
        shape=parents.shape;flat=parents.ravel();bc=bary.reshape(-1,3)
        n=unit(np.einsum('ni,nij->nj',bc,self.n[flat]));out=n.copy()
        if not texture:return out.reshape(*shape,3)
        for mi,img in enumerate(self.maps):
            mask=self.material[flat]==mi
            if not mask.any():continue
            f=flat[mask];bs=bc[mask]
            uv=np.einsum('ni,nij->nj',bs,self.uv[f])
            tn=tangent_normal(bilinear(img,uv),self.strength[mi])
            t=unit(np.einsum('ni,nij->nj',bs,self.t[f]));b=unit(np.einsum('ni,nij->nj',bs,self.b[f]))
            out[mask]=unit(t*tn[:,:1]+b*tn[:,1:2]+n[mask]*tn[:,2:])
        return out.reshape(*shape,3)
