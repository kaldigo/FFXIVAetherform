"""Factor the legacy AF physics bank into reusable simulator sources and selection rules."""
import argparse
import gzip
import hashlib
import itertools
import json
import struct
from collections import defaultdict
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED


def u32(data, pos):
    return struct.unpack_from("<I", data, pos)[0]


def decode(data):
    start = u32(data, 12)
    base = start + 4
    tail = data[-struct.unpack_from("<Q", data, len(data)-8)[0]:] if data[-24:-20] == b"PACK" else b""
    frame = (data[:12], data[16:start], tail, len(data) % 8 if tail else 0)
    slots = []
    values = {}
    for index in range(u32(data, start)):
        header = data[base+72*index:base+72*(index+1)]
        contents = [header[:40]]
        bone = ""
        for k, size in enumerate((36, 36, 48, 32, 52, 48, 16, 36)):
            at = base + u32(header, 40+4*k)
            if k != 2:
                contents.append(data[at:at+header[k]*size])
                continue
            for j in range(header[k]):
                chain = data[at+48*j:at+48*(j+1)]
                contents.append(chain[:40])
                for pos, count, stride in zip((40,44), struct.unpack_from("<HH",chain), (36,84)):
                    off = base + u32(chain,pos)
                    contents.append(data[off:off+count*stride])
                if j == 0:
                    bone = contents[-1][:32].split(b"\0")[0].decode()
        key = bone
        while key in values:
            key += "#"
        values[key] = hashlib.sha256(b"".join(contents)).digest()
        slots.append(key)
    return frame, tuple(slots), values


def compile_bank(src, recipe, compact):
    with ZipFile(src) as bank:
        index = json.loads(bank.read("index.json"))
        heights = json.loads(bank.read("heights.json"))
        dimensions = [len(a["values"]) for a in index["areas"]]
        picks = list(itertools.product(*map(range, dimensions)))
        nodes, refs, orders, examples, parsed = [], {}, {}, {}, {}

        def leaf(key, value):
            if key not in refs:
                refs[key] = len(nodes)
                nodes.append(value)
            return refs[key]

        def rule(values, depth=0):
            if min(values) == max(values):
                return values[0]
            width = len(values) // dimensions[depth]
            children = tuple(rule(values[i:i+width], depth+1) for i in range(0,len(values),width))
            if len(set(children)) == 1:
                return children[0]
            return leaf(("rule",depth,children), [depth,*children])

        roots = []
        empty = leaf(("empty",), [-1])
        def source(asset):
            if asset not in parsed:
                frame, order, values = decode(bank.read("bank/"+asset+".phyb"))
                frame_id = leaf(("frame",frame), [-2])
                ex = examples.setdefault(asset,{})
                ex[frame_id] = -1
                sims = {}
                for i,(key,value) in enumerate(values.items()):
                    sid = leaf(("sim",value),[-2])
                    ex[sid] = i
                    sims[key] = sid
                oid = orders.setdefault(order,len(orders))
                parsed[asset] = frame_id, leaf(("order",oid),[-3,oid]), sims
            return parsed[asset]

        for race in range(len(index["races"])):
            rows = []
            for p in picks:
                row = index["rows"]["".join(str(v) for i,v in enumerate(p) if i!=1)]
                rows.append(source(heights[row[race]][p[1]]))
            keys = sorted(set().union(*(r[2] for r in rows)))
            roots.append(dict(Frame=rule([r[0] for r in rows]),Order=rule([r[1] for r in rows]),
                              Slots={k:rule([r[2].get(k,empty) for r in rows]) for k in keys}))

        needed = {i for i,node in enumerate(nodes) if node == [-2]}
        files = []
        with ZipFile(compact,"w",ZIP_DEFLATED) as out:
            while needed:
                best = max(examples,key=lambda a: len(needed.intersection(examples[a])))
                selected = needed.intersection(examples[best])
                assert selected
                for n in selected:
                    nodes[n] = [-2,len(files),examples[best][n]]
                needed -= selected
                files.append(best)
                path = "bank/"+best+".phyb"
                out.writestr(path, bank.read(path))
        value = dict(Areas=index["areas"],Races=index["races"],Rules=roots,Nodes=nodes,
                     Orders=list(orders),Files=files)
        recipe.write_bytes(gzip.compress(json.dumps(value,separators=(",",":")).encode(),mtime=0))
        print(f"{len(parsed)} legacy files reduced to {len(files)} sources; {compact.stat().st_size:,} bytes")


if __name__ == "__main__":
    p=argparse.ArgumentParser(description=__doc__)
    for arg in ("bank","recipe","compact"):
        p.add_argument(arg,type=Path)
    args=p.parse_args()
    compile_bank(args.bank,args.recipe,args.compact)
