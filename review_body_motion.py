"""Render controlled motion review and verify the exported GLB against CPU skinning."""
import json,struct
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont
from scipy.spatial.transform import Rotation
from body_motion import load_rig,vertex_weights,CASES
from clean_reference import normals
from surface_diagnostics import raster

OUT=Path('outputs/body-motion-review-v1')
FONT=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',18)
BG='#182129'


def validate_glb(d,rig):
    blob=(OUT/'body-controlled-motion.glb').read_bytes()
    magic,version,total=struct.unpack_from('<4sII',blob)
    assert magic==b'glTF' and version==2 and total==len(blob)
    length,kind=struct.unpack_from('<I4s',blob,12);assert kind==b'JSON'
    model=json.loads(blob[20:20+length]);offset=20+length
    size,kind=struct.unpack_from('<I4s',blob,offset);assert kind==b'BIN\0'
    binary=blob[offset+8:offset+8+size]
    def read(i):
        a=model['accessors'][i];v=model['bufferViews'][a['bufferView']]
        n={'SCALAR':1,'VEC3':3,'VEC4':4,'MAT4':16}[a['type']]
        dtype={5126:'<f4',5123:'<u2',5125:'<u4'}[a['componentType']]
        return np.frombuffer(binary,dtype,count=a['count']*n,offset=v.get('byteOffset',0)+a.get('byteOffset',0)).reshape(a['count'],n)
    attrs=model['meshes'][0]['primitives'][0]['attributes'];v=read(attrs['POSITION'])
    joints=np.concatenate([read(attrs['JOINTS_'+str(i)]) for i in range(2)],axis=1)
    weights=np.concatenate([read(attrs['WEIGHTS_'+str(i)]) for i in range(2)],axis=1)
    assert joints.max()<len(rig.names)
    np.testing.assert_allclose(weights.sum(1),1,atol=1e-7)
    np.testing.assert_array_equal(read(model['meshes'][0]['primitives'][0]['indices']).ravel(),d['triangles'].ravel())
    ibm=read(model['skins'][0]['inverseBindMatrices']).reshape(-1,4,4).transpose(0,2,1)
    maximum=0.;samples=0;reference_weights=vertex_weights(d)
    for animation in model['animations']:
        case=next(c for c in CASES if animation['name'].endswith(' - '+c['id']))
        for frame in range(17):
            transforms=[]
            for node in model['nodes'][:len(rig.names)]:
                transforms.append({k:np.array(node[k]) for k in ('translation','rotation','scale')})
            for ch in animation['channels']:
                s=animation['samplers'][ch['sampler']];times=read(s['input']).ravel()
                np.testing.assert_allclose(times,np.linspace(0,4,17),atol=1e-7)
                transforms[ch['target']['node']][ch['target']['path']]=read(s['output'])[frame]
            worlds=[]
            for i,tr in enumerate(transforms):
                m=np.eye(4);m[:3,:3]=Rotation.from_quat(tr['rotation']).as_matrix()@np.diag(tr['scale']);m[:3,3]=tr['translation']
                parent=rig.parents[i];worlds.append(m if parent<0 else worlds[parent]@m)
            matrices=np.array(worlds)@ibm
            posed=np.zeros_like(v,dtype=float)
            for lane in range(8):
                m=matrices[joints[:,lane]];posed+=weights[:,lane,None]*(np.einsum('nij,nj->ni',m[:,:3,:3],v)+m[:,:3,3])
            expected,_=rig.skin(d['position'],reference_weights,d['boneNames'],rig.world(case,frame/16))
            maximum=max(maximum,float(np.linalg.norm(posed*1000-expected,axis=1).max()));samples+=1
    assert maximum<.002,(maximum,'mm GLB roundtrip mismatch')
    result=dict(sampledFrames=samples,maximumPositionErrorMm=maximum,scope='Re-read GLB buffers and animation keyframes, rebuilt hierarchy, skinned exported attributes; compared every keyframe with CPU reference. Between-keyframe interpolation not sampled.')
    (OUT/'glb-validation.json').write_text(json.dumps(result,indent=2)+'\n');print(result,flush=True)


def panel(d,q,view,bounds,size):
    t=d['triangles'];centres=q[t].mean(1)
    # Crop conservatively in projected dimensions to avoid rasterising off-screen faces.
    axes=(2,1) if view=='side' else (0,1)
    selected=(centres[:,axes[0]]>bounds[0]-40)&(centres[:,axes[0]]<bounds[1]+40)&(centres[:,axes[1]]>bounds[2]-40)&(centres[:,axes[1]]<bounds[3]+40)
    n=normals(q,t);return raster(q[t[selected]],view,bounds,size,vertex_normals=n[t[selected]])[0]


def run():
    rig=load_rig();post=dict(np.load('outputs/bra-postprocess-v5/body-fit-reference.npz'));old=dict(np.load('outputs/working-body-4x/body-fit-reference.npz'))
    validate_glb(post,rig)
    datasets=[old,post];weights=[vertex_weights(d) for d in datasets];frames=[]
    for name in ('arms_raise','torso_twist','breast_alternating','breast_rotation'):
        case=next(c for c in CASES if c['id']==name)
        for phase in np.linspace(0,1,13):
            im=Image.new('RGB',(840,480),BG);draw=ImageDraw.Draw(im)
            draw.text((12,8),'Controlled native-rig stress: '+name+'   '+f'{phase:.2f}',font=FONT,fill='white')
            for j,(d,w) in enumerate(zip(datasets,weights)):
                q,_=rig.skin(d['position'],w,d['boneNames'],rig.world(case,float(phase)))
                tile=panel(d,q,'front',(-160,160,1140,1390),(420,410));im.paste(tile,(j*420,65))
                draw.text((j*420+12,40),'Pre-bra working mesh' if j==0 else 'Local bra postprocess',font=FONT,fill='white')
            frames.append(im)
        print('Rendered',name,flush=True)
    frames[0].save(OUT/'chest-motion-comparison.gif',save_all=True,append_images=frames[1:],duration=120,loop=0)
    frames[6].save(OUT/'chest-motion-still.png')
    sheet=Image.new('RGB',(1500,1080),BG);draw=ImageDraw.Draw(sheet)
    for k,name in enumerate(('arms_raise','forward_reach','torso_twist','sit','crouch','lunge')):
        a=np.load(OUT/(name+'-postprocessed-peak.npz'));q=a['position'];pairs=a['selfPairs'];t=post['triangles']
        view='front' if k<3 else 'side';bounds=(-560,560,350,1500) if view=='front' else (-450,700,200,1430)
        colors=np.tile([111,173,195],(len(t),1));colors[np.unique(pairs)]=[255,80,70]
        img=raster(q[t],view,bounds,(500,480),vertex_normals=normals(q,t)[t],face_colors=colors)[0]
        x=(k%3)*500;y=(k//3)*540;sheet.paste(img,(x,y+55))
        draw.text((x+12,y+5),name+' / '+view,font=FONT,fill='white');draw.text((x+12,y+30),str(len(pairs))+' intersecting face pairs (red)',font=FONT,fill='white')
    sheet.save(OUT/'pose-contact-review.png');print('Saved GIF and peak contact sheet',flush=True)


if __name__=='__main__':run()
