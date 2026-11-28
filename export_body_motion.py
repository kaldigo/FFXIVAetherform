"""Export a rigged GLB with clearly labelled controlled stress animations."""
import json,struct
from pathlib import Path
import numpy as np
from scipy.spatial.transform import Rotation
from body_motion import load_rig,vertex_weights,CASES
from clean_reference import normals


def run():
    folder=Path('outputs/body-motion-review-v1');folder.mkdir(parents=True,exist_ok=True)
    d=np.load('outputs/bra-postprocess-v5/body-fit-reference.npz');rig=load_rig();weights=vertex_weights(d)
    binary=bytearray();views=[];accessors=[]
    def accessor(data,kind,component=5126,target=None,bounds=False):
        dtype={5126:'<f4',5123:'<u2',5125:'<u4'}[component];a=np.asarray(data,dtype=dtype)
        while len(binary)%4:binary.append(0)
        view=dict(buffer=0,byteOffset=len(binary),byteLength=a.nbytes)
        if target:view['target']=target
        views.append(view);binary.extend(a.tobytes())
        record=dict(bufferView=len(views)-1,componentType=component,count=len(a),type=kind)
        if bounds:
            record.update(min=np.min(a,axis=0).tolist() if a.ndim>1 else [float(a.min())],max=np.max(a,axis=0).tolist() if a.ndim>1 else [float(a.max())])
        accessors.append(record);return len(accessors)-1
    nodes=[dict(name=name,translation=p[:3].tolist(),rotation=(p[4:8]/np.linalg.norm(p[4:8])).tolist(),scale=p[8:11].tolist()) for name,p in zip(rig.names,rig.pose)]
    roots=[]
    for i,parent in enumerate(rig.parents):
        if parent<0:roots.append(i)
        else:nodes[parent].setdefault('children',[]).append(i)
    bind=rig.rest.copy();bind[:,:3,3]/=1000;inverse=np.linalg.inv(bind)
    skin=dict(name='Installed vanilla c0201 skeleton',joints=list(range(len(nodes))),skeleton=roots[0],inverseBindMatrices=accessor(inverse.transpose(0,2,1).reshape(-1,16),'MAT4'))
    attrs=dict(POSITION=accessor(d['position']/1000,'VEC3',target=34962,bounds=True),NORMAL=accessor(normals(d['position'],d['triangles']),'VEC3',target=34962))
    order=np.argsort(-weights,axis=1,kind='stable')[:,:8];selected=np.take_along_axis(weights,order,axis=1)
    np.testing.assert_allclose(selected.sum(1),1,atol=1e-12)
    indices=np.array([rig.names.index(str(name)) for name in d['boneNames']])[order]
    for lane in range(2):
        attrs['JOINTS_'+str(lane)]=accessor(indices[:,4*lane:4*lane+4],'VEC4',5123,34962)
        attrs['WEIGHTS_'+str(lane)]=accessor(selected[:,4*lane:4*lane+4],'VEC4',5126,34962)
    mesh=dict(name='Postprocessed 4x fitting body',primitives=[dict(attributes=attrs,indices=accessor(d['triangles'].ravel(),'SCALAR',5125,34963),material=0)])
    roots.append(len(nodes));nodes.append(dict(name='Fitting body',mesh=0,skin=0))
    phases=np.linspace(0,1,17);animations=[]
    for case in CASES:
        times=accessor(phases*4,'SCALAR',bounds=True);worlds=np.array([rig.world(case,float(t)) for t in phases]);channels=[];samplers=[]
        names=set().union(*(case.get(k,{}).keys() for k in ('rotations','oscillatingRotations','translations','oscillatingTranslations')))
        for name in sorted(names):
            i=rig.names.index(name);parent=rig.parents[i]
            local=worlds[:,i] if parent<0 else np.linalg.inv(worlds[:,parent])@worlds[:,i]
            if name in case.get('rotations',{}) or name in case.get('oscillatingRotations',{}):
                rotation=local[:,:3,:3]@np.diag(1/rig.pose[i,8:11]);q=Rotation.from_matrix(rotation).as_quat()
                for k in range(1,len(q)):
                    if q[k]@q[k-1]<0:q[k]*=-1
                samplers.append(dict(input=times,output=accessor(q,'VEC4'),interpolation='LINEAR'));channels.append(dict(sampler=len(samplers)-1,target=dict(node=i,path='rotation')))
            if name in case.get('translations',{}) or name in case.get('oscillatingTranslations',{}):
                samplers.append(dict(input=times,output=accessor(local[:,:3,3]/1000,'VEC3'),interpolation='LINEAR'));channels.append(dict(sampler=len(samplers)-1,target=dict(node=i,path='translation')))
        animations.append(dict(name='Controlled stress - '+case['id'],channels=channels,samplers=samplers))
    model=dict(asset=dict(version='2.0',generator='AF refit-tool controlled motion review'),scene=0,scenes=[dict(nodes=roots)],nodes=nodes,meshes=[mesh],skins=[skin],animations=animations,
        materials=[dict(name='Geometry review',pbrMetallicRoughness=dict(baseColorFactor=[.435,.678,.765,1],metallicFactor=0,roughnessFactor=.85))],
        buffers=[dict(byteLength=len(binary))],bufferViews=views,accessors=accessors,
        extras=dict(scope='Procedural stress clips on the actual vanilla skeleton. Not native game animations or live physics.',meshSource='bra-postprocess-v5'))
    encoded=json.dumps(model,separators=(',',':')).encode();encoded+=b' '*((-len(encoded))%4);binary+=b'\0'*((-len(binary))%4)
    total=12+8+len(encoded)+8+len(binary)
    path=folder/'body-controlled-motion.glb';path.write_bytes(struct.pack('<4sII',b'glTF',2,total)+struct.pack('<I4s',len(encoded),b'JSON')+encoded+struct.pack('<I4s',len(binary),b'BIN\0')+binary)
    for a in accessors:
        view=views[a['bufferView']];assert view['byteOffset']+view['byteLength']<=len(binary)
    print('Exported',len(animations),'controlled clips,',len(rig.names),'bones and',len(d['position']),'vertices to',path)


if __name__=='__main__':run()
