"""Inspect native DXBC normal decoding without distributing shader assets."""
import ctypes as c
from pathlib import Path
import struct


def disassemble(data):
    dll=c.WinDLL('d3dcompiler_47.dll');f=dll.D3DDisassemble
    f.argtypes=[c.c_void_p,c.c_size_t,c.c_uint,c.c_char_p,c.POINTER(c.c_void_p)];f.restype=c.c_long
    buf=c.create_string_buffer(data);blob=c.c_void_p()
    hr=f(buf,len(data),0,None,c.byref(blob))
    if hr<0:raise RuntimeError(hex(hr&0xffffffff))
    table=c.cast(blob,c.POINTER(c.POINTER(c.c_void_p))).contents
    get=c.WINFUNCTYPE(c.c_void_p,c.c_void_p)(table[3]);size=c.WINFUNCTYPE(c.c_size_t,c.c_void_p)(table[4])
    result=c.string_at(get(blob),size(blob)).decode('utf-8').rstrip('\0')
    c.WINFUNCTYPE(c.c_ulong,c.c_void_p)(table[2])(blob)
    return result


def run(folder):
    folder=Path(folder)
    for name in ('skin','character'):
        raw=(folder/(name+'.shpk')).read_bytes();offset=0;counts={'vs':0,'ps':0}
        while True:
            offset=raw.find(b'DXBC',offset)
            if offset<0:break
            length=struct.unpack_from('<I',raw,offset+24)[0]
            code=disassemble(raw[offset:offset+length]);offset+=length
            stage='vs' if '\nvs_5_0' in code else 'ps' if '\nps_5_0' in code else None
            if stage=='ps' and 'g_SamplerNormal' not in code:continue
            if stage=='vs' and 'BINORMAL' not in code:continue
            if stage and counts[stage]<3:
                (folder/f'{name}-{stage}-{counts[stage]}.asm').write_text(code)
                counts[stage]+=1
            if min(counts.values())>=3:break
        print(name,counts)


if __name__=='__main__':
    import sys
    run(sys.argv[1])
