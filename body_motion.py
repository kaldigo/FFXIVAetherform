"""Vanilla-rig linear skinning and explicitly controlled animation stress clips."""
import json
from pathlib import Path
import numpy as np
from scipy.spatial.transform import Rotation


class Rig:
    def __init__(self,data):
        self.data=data;self.names=data['Names'];self.parents=np.array(data['Parents']);self.pose=np.array(data['Pose']).reshape(-1,12)
        self.local=[];world=[]
        for p,parent in zip(self.pose,self.parents):
            m=np.eye(4);m[:3,:3]=Rotation.from_quat(p[4:8]).as_matrix()@np.diag(p[8:11]);m[:3,3]=p[:3]*1000
            self.local.append(m);world.append(m if parent<0 else world[parent]@m)
        self.rest=np.array(world);self.inverse=np.linalg.inv(self.rest)

    def world(self,case,phase):
        pulse=.5-.5*np.cos(2*np.pi*phase);wave=np.sin(2*np.pi*phase);out=[]
        for i,parent in enumerate(self.parents):
            m=self.local[i].copy();name=self.names[i];reference=np.eye(3) if parent<0 else self.rest[parent,:3,:3]
            for kind,amount in (('rotations',pulse),('oscillatingRotations',wave)):
                if name in case.get(kind,{}):
                    r=Rotation.from_euler('xyz',np.array(case[kind][name])*amount,degrees=True).as_matrix()
                    m[:3,:3]=np.linalg.solve(reference,r@reference)@m[:3,:3]
            for kind,amount in (('translations',pulse),('oscillatingTranslations',wave)):
                if name in case.get(kind,{}):m[:3,3]+=np.linalg.solve(reference,np.array(case[kind][name])*amount)
            out.append(m if parent<0 else out[parent]@m)
        return np.array(out)

    def skin(self,v,weights,bone_names,world):
        ids=np.array([self.names.index(str(name)) for name in bone_names]);mat=(world@self.inverse)[ids]
        blend=(weights@mat[:,:3,:].reshape(len(ids),12)).reshape(-1,3,4)
        return np.einsum('nij,nj->ni',blend[:,:,:3],v)+blend[:,:,3],blend[:,:,:3]


def vertex_weights(data):
    t=data['triangles'];w=data['weights'];result=np.zeros((len(data['position']),w.shape[-1]));result[t.ravel()]=w.reshape(-1,w.shape[-1])
    np.testing.assert_allclose(result[t],w,atol=1e-12);np.testing.assert_allclose(result.sum(1),1,atol=1e-12)
    return result


def packed_weights(weights,influences=8):
    order=np.argsort(-weights,axis=1,kind='stable')[:,:influences]
    selected=np.take_along_axis(weights,order,axis=1);lost=1-selected.sum(1);selected/=selected.sum(1)[:,None]
    scaled=selected*255;packed=np.floor(scaled).astype(int);remaining=255-packed.sum(1)
    priority=np.argsort(-(scaled-packed),axis=1,kind='stable')
    for k in range(influences):
        take=remaining>k;packed[np.flatnonzero(take),priority[take,k]]+=1
    result=np.zeros_like(weights);np.put_along_axis(result,order,packed/255,axis=1)
    np.testing.assert_allclose(result.sum(1),1,atol=1e-12)
    return result,lost


def deformation_metrics(rest,posed,t,blend):
    p,q=rest[t],posed[t];a,b=p[:,1]-p[:,0],p[:,2]-p[:,0];u,w=q[:,1]-q[:,0],q[:,2]-q[:,0]
    normal=np.cross(a,b);area=np.linalg.norm(normal,axis=1);normal/=area[:,None]
    actual=np.cross(u,w);actual_area=np.linalg.norm(actual,axis=1)
    affine=blend[t].mean(1);expected=np.linalg.solve(affine.transpose(0,2,1),normal[...,None])[...,0]
    expected/=np.linalg.norm(expected,axis=1)[:,None]
    signed=np.sum(actual*expected,axis=1)/area
    g00=(a*a).sum(1);g01=(a*b).sum(1);g11=(b*b).sum(1);det=g00*g11-g01*g01
    h00=(u*u).sum(1);h01=(u*w).sum(1);h11=(w*w).sum(1)
    trace=(g11*h00+g00*h11-2*g01*h01)/det;determinant=np.maximum((h00*h11-h01*h01)/det,0)
    discriminant=np.sqrt(np.maximum(trace*trace-4*determinant,0))
    low=np.sqrt(np.maximum((trace-discriminant)/2,0));high=np.sqrt(np.maximum((trace+discriminant)/2,0))
    return dict(signedArea=signed,areaRatio=actual_area/area,minStretch=low,maxStretch=high)


CASES=[
    dict(id='arms_raise',rotations={'j_ude_a_l':[0,0,70],'j_ude_a_r':[0,0,-70],'j_sako_l':[0,0,10],'j_sako_r':[0,0,-10]}),
    dict(id='forward_reach',rotations={'j_ude_a_l':[0,-65,40],'j_ude_a_r':[0,65,-40],'j_ude_b_l':[0,-35,0],'j_ude_b_r':[0,35,0]}),
    dict(id='torso_bend',rotations={'j_sebo_a':[15,0,0],'j_sebo_b':[15,0,0],'j_sebo_c':[10,0,0]}),
    dict(id='torso_twist',oscillatingRotations={'j_sebo_a':[0,12,0],'j_sebo_b':[0,15,0],'j_sebo_c':[0,10,0]}),
    dict(id='side_bend',oscillatingRotations={'j_sebo_a':[0,0,10],'j_sebo_b':[0,0,12]}),
    dict(id='walk_cycle',oscillatingRotations={'j_asi_a_l':[-30,0,0],'j_asi_a_r':[30,0,0],'j_asi_b_l':[25,0,0],'j_asi_b_r':[-25,0,0]}),
    dict(id='wide_step',rotations={'j_asi_a_l':[0,0,20],'j_asi_a_r':[0,0,-20]}),
    dict(id='sit',rotations={'j_asi_a_l':[-85,0,0],'j_asi_a_r':[-85,0,0],'j_asi_b_l':[85,0,0],'j_asi_b_r':[85,0,0]}),
    dict(id='crouch',rotations={'j_asi_a_l':[-55,0,0],'j_asi_a_r':[-55,0,0],'j_asi_b_l':[100,0,0],'j_asi_b_r':[100,0,0],'j_sebo_a':[12,0,0]}),
    dict(id='lunge',rotations={'j_asi_a_l':[-65,0,5],'j_asi_b_l':[100,0,0],'j_asi_a_r':[15,0,-5],'j_sebo_a':[10,0,0]}),
    dict(id='breast_bounce',oscillatingTranslations={'j_mune_l':[0,3,2],'j_mune_r':[0,3,2]}),
    dict(id='breast_alternating',oscillatingTranslations={'j_mune_l':[0,4,2],'j_mune_r':[0,-4,-2]}),
    dict(id='breast_spread',oscillatingTranslations={'j_mune_l':[3,0,0],'j_mune_r':[-3,0,0]}),
    dict(id='breast_rotation',oscillatingRotations={'j_mune_l':[6,0,4],'j_mune_r':[6,0,-4]}),
    dict(id='combined_upper_body',rotations={'j_ude_a_l':[0,0,60],'j_ude_a_r':[0,0,-60],'j_sebo_b':[10,12,0]},
         oscillatingTranslations={'j_mune_l':[0,3,2],'j_mune_r':[0,-3,-2]})]


def load_rig(path='outputs/motion-assets/vanilla-c0201-rig.json'):return Rig(json.loads(Path(path).read_text()))
