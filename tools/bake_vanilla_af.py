"""Maintainer-only bake. Inputs are four local LOD0 MDLs; see README."""
import argparse
import gzip
import hashlib
import struct
from pathlib import Path

import cv2
import numpy as np

N = 256

def morph(path):
    """Apply the active LOD0 Hip Dips and Soft Butt index replacements."""
    data = bytearray(path.read_bytes())
    def u16(offset):
        return struct.unpack_from('<H', data, offset)[0]
    def u32(offset):
        return struct.unpack_from('<I', data, offset)[0]

    declaration_end = 68 + u16(12) * 136
    strings = declaration_end + 8
    header = strings + u32(declaration_end + 4)
    meshes, attrs, submeshes, materials, bones, tables, shapes, shape_meshes, _ = struct.unpack_from('<9H', data, header+4)
    lod = header + 56 + u16(header+24)*32
    mesh_start = lod + 180 + (120 if data[header+27] & 16 else 0)
    table_start = (mesh_start + 36*meshes + 4*attrs + 20*data[header+26] + 16*submeshes
                   + 12*u16(header+38) + 4*(materials+bones))
    shape_start = table_start + (tables*4 + 2*u16(header+44) if u32(0)>=0x01000006 else tables*132)
    entries = {}
    for i in range(shapes):
        offset = shape_start + 16*i
        start = strings + u32(offset)
        name = data[start:data.index(0,start)].decode()
        entries[name] = (u16(offset+4), u16(offset+10))

    mesh_records = shape_start + 16*shapes
    replacements = mesh_records + 12*shape_meshes
    for name in ('shpx_yab_hip','shpx_yam_softbutt','shpx_yam_c_softwaist','shpx_yab_c_softwaist','shpx_yab_c_hipsoft'):
        first, count = entries.get(name,(0,0))
        for j in range(first,first+count):
            mesh_index, size, start = struct.unpack_from('<III',data,mesh_records+12*j)
            for k in range(start,start+size):
                corner, vertex = struct.unpack_from('<HH',data,replacements+4*k)
                struct.pack_into('<H',data,u32(28)+2*(mesh_index+corner),vertex)
    return data


def skin(path, apply_leg_shapes=False):
    b = morph(path) if apply_leg_shapes else path.read_bytes()
    u16 = lambda i: struct.unpack_from('<H', b, i)[0]
    u32 = lambda i: struct.unpack_from('<I', b, i)[0]
    decl_end = 68 + u16(12)*136
    header = decl_end + 8 + u32(decl_end+4)
    lod = header + 56 + u16(header+24)*32
    first = u16(lod)
    mesh = lod + 180 + (120 if b[header+27]&16 else 0) + first*36
    fields = {}
    for i in range(17):
        off = 68 + 136*first + 8*i
        stream,offset,kind,usage,index = b[off:off+5]
        if stream == 255: break
        if index == 0 and usage in (0,4):
            dtype = np.dtype({1:'<f4',2:'<f4',3:'<f4',13:'<f2',14:'<f2'}[kind])
            start = u32(16)+u32(mesh+20+4*stream)+offset
            fields[usage] = np.ndarray((u16(mesh),3 if usage==0 else 2),dtype,b,start,
                                        (b[mesh+32+stream],dtype.itemsize)).astype(float)
    corners = np.frombuffer(b,'<u2',u32(mesh+4),u32(28)+2*u32(mesh+16))
    used,tri = np.unique(corners,return_inverse=True)
    return fields[0][used],tri.reshape(-1,3),fields[4][used]

def raster(v, t, uv, vanilla=False):
    grid = np.full((N,N,3),np.nan)
    for face in t:
        xyz = v[face]
        p = uv[face].copy()
        if vanilla:
            p[:,0] = .5 + .5*(p[:,0]-1)*np.sign(xyz[:,0].mean())
        a,b,c = p
        mat = np.column_stack((b-a,c-a))
        if abs(np.linalg.det(mat)) < 1e-12: continue
        lo = np.maximum(0,np.ceil(p.min(0)*N-.5).astype(int))
        hi = np.minimum(N-1,np.floor(p.max(0)*N-.5).astype(int))
        i,j = np.mgrid[lo[0]:hi[0]+1,lo[1]:hi[1]+1].reshape(2,-1)
        s,h = np.linalg.inv(mat) @ ((np.array([i,j])+.5)/N-a[:,None])
        ok = (s>=0)&(h>=0)&(s+h<=1)
        grid[i[ok],j[ok]] = xyz[0]+s[ok,None]*(xyz[1]-xyz[0])+h[ok,None]*(xyz[2]-xyz[0])
    return grid

def knn(data, query, count):
    tree = cv2.flann_Index(np.ascontiguousarray(data,np.float32),dict(algorithm=1,trees=4))
    return tree.knnSearch(np.ascontiguousarray(query,np.float32),count,params=dict(checks=128))

def registration(data,vanilla_grid):
    p,t,uv=data
    grid=raster(p,t,uv)
    valid=np.isfinite(grid[:,:,0])&np.isfinite(vanilla_grid[:,:,0])
    coords=(np.argwhere(valid)+.5)/N
    cv2.setRNGSeed(7)
    idx,sq=knn(np.column_stack((grid[valid],coords*.35)),np.column_stack((p,uv*.35)),12)
    w=(sq+1e-6)**-1.5
    movement=np.einsum('ij,ijk->ik',w/w.sum(1,keepdims=True),(vanilla_grid[valid]-grid[valid])[idx])
    _,weld=np.unique(p.round(6),axis=0,return_inverse=True)
    avg=np.zeros((weld.max()+1,3))
    np.add.at(avg,weld,p+movement)
    return (avg/np.bincount(weld)[:,None])[weld][t]

if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    for arg in ('vanilla_top','vanilla_legs','af_top','af_legs','output'):
        parser.add_argument(arg,type=Path)
    args=parser.parse_args()
    payload=bytearray(b'AFM1')
    for source,target,legs in [(args.vanilla_top,args.af_top,False),(args.vanilla_legs,args.af_legs,True)]:
        ref=skin(target,legs)
        corners=ref[2][ref[1]].astype('<f4')
        positions=registration(ref,raster(*skin(source),True)).astype('<f4')
        if not np.isfinite(positions).all(): raise ValueError('Invalid wrapped surface')
        payload.extend(struct.pack('<I',len(positions)*3))
        payload.extend(hashlib.sha256(corners.tobytes()).digest())
        payload.extend(positions.tobytes())
    args.output.write_bytes(gzip.compress(payload,mtime=0))
