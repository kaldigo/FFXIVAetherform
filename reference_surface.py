"""Normal-guided vanilla body reference; positions and areas are in millimetres.

Source inputs are schema-1 MDL inspection snapshots plus their original MDLs.
No plugin checkout imports, game writes, body registration or fitting.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

import numpy as np


def unit(a):
    lengths = np.linalg.norm(a, axis=-1, keepdims=True)
    if np.any(lengths < 1e-12):
        raise ValueError("Zero-length normal or derivative")
    return a / lengths


def areas(p):
    return np.linalg.norm(np.cross(p[:, 1] - p[:, 0], p[:, 2] - p[:, 0]), axis=1) / 2


def load_snapshot(path):
    path = Path(path)
    s = json.loads(path.read_text(encoding="utf-8"))
    if s.get("schema") != 1 or s.get("modelToMm") != 1000:
        raise ValueError("Expected schema 1 with modelToMm=1000")
    source = Path(s["sourcePath"])
    if not source.is_absolute():
        source = path.parent / source
    data = source.read_bytes()
    if hashlib.sha256(data).hexdigest() != s["sha256"]:
        raise ValueError(f"Source hash mismatch: {source}")
    lod = next(x for x in s["levels"] if x["id"] == 0)
    return s, data, lod["meshes"]


def field(data, mesh, usage):
    f = next(x for x in mesh["fields"] if x["usage"] == usage and x["usageIndex"] == 0)
    codecs = {1: ("<f4", 2), 2: ("<f4", 3), 3: ("<f4", 4),
              5: ("u1", 4), 8: ("u1", 4), 13: ("<f2", 2), 14: ("<f2", 4), 17: ("u1", 8)}
    dtype, count = codecs[f["type"]]
    a = np.ndarray((mesh["vertexCount"], count), dtype, data, f["offset"],
                   strides=(f["stride"], np.dtype(dtype).itemsize)).astype(float)
    if f["type"] == 8:
        a /= 255
    return a


def mesh_arrays(s, data, mesh, bone_names):
    ids = np.frombuffer(data, "<u2", mesh["indexCount"], mesh["indexOffset"]).reshape(-1, 3)
    p = field(data, mesh, 0)[ids, :3] * 1000
    n = unit(field(data, mesh, 3)[ids, :3])
    uv = field(data, mesh, 4)[ids, :2]
    weights = field(data, mesh, 1)
    # Weight and index codecs use the same four/eight byte influence layout.
    weights /= weights.sum(axis=1, keepdims=True)
    bone_ids = field(data, mesh, 2).astype(int)
    w = np.zeros((mesh["vertexCount"], len(bone_names)))
    for i, bone in enumerate(mesh["palette"]):
        w[:, bone_names.index(bone)] += (weights * (bone_ids == i)).sum(axis=1)
    return p, n, uv, w[ids]


def read_sources(top, legs, cleanup=None):
    loaded = [load_snapshot(p) for p in (top, legs)]
    bones = sorted({name for s, _, _ in loaded for name in s["tables"]["Bones"]})
    all_p, all_n, all_uv, all_w, groups, identities, removed = [], [], [], [], [], [], []
    hashes = []
    cleanup_data = json.loads(Path(cleanup).read_text()) if cleanup else None
    cleanup_faces = {(x["slot"], x["mesh"], x["firstIndex"]) for x in cleanup_data["faces"]} if cleanup_data else set()
    found_cleanup = set()
    baseline = []
    for slot, (s, data, meshes) in zip(("top", "legs"), loaded):
        if cleanup_data and cleanup_data["sourceHashes"][slot] != s["sha256"]:
            raise ValueError("Flap face selection belongs to a different source hash")
        hashes.append(s["sha256"])
        for mid in (0, 1):
            mesh = next(m for m in meshes if m["id"] == mid)
            p, n, uv, w = mesh_arrays(s, data, mesh, bones)
            keep = np.ones(len(p), bool)
            if slot == "legs" and mid == 1:
                # Remove only explicitly tagged tail-flap submeshes, not the
                # two-face LOD closure or the primary close-fitting briefs.
                attributes = s["tables"]["Attributes"]
                flap_bits = sum(1 << i for i, name in enumerate(attributes)
                                if name in ("atr_tls", "atr_kod"))
                if not flap_bits:
                    raise ValueError("No recognised tail-flap attributes; inspect this source")
                for sub in s["tables"]["SubMeshes"][mesh["subMeshStart"]:
                                                     mesh["subMeshStart"] + mesh["subMeshCount"]]:
                    if sub["AttributeIndexMask"] & flap_bits:
                        first = (sub["IndexOffset"] - mesh["startIndex"]) // 3
                        keep[first:first + sub["IndexCount"] // 3] = False
            baseline.append(p[keep])
            for i in range(len(p)):
                identity = slot, mid, i * 3
                if identity in cleanup_faces:
                    keep[i] = False
                    found_cleanup.add(identity)
            label = slot + ("_skin" if mid == 0 else "_smallclothes")
            for i in np.flatnonzero(~keep):
                removed.append({"slot": slot, "mesh": mid, "firstIndex": int(i * 3)})
            all_p.append(p[keep]); all_n.append(n[keep]); all_uv.append(uv[keep]); all_w.append(w[keep])
            groups.extend([label] * int(keep.sum()))
            identities.extend((slot, mid, int(i * 3)) for i in np.flatnonzero(keep))
    if found_cleanup != cleanup_faces:
        raise ValueError("Flap selection contains unresolvable source faces")
    result = dict(p=np.concatenate(all_p), n=np.concatenate(all_n), uv=np.concatenate(all_uv),
                weights=np.concatenate(all_w), groups=np.array(groups), bones=bones,
                identities=identities, removed=removed, hashes=hashes)
    result["generatedCaps"] = 0
    if cleanup_data:
        close_flap_roots(result, np.concatenate(baseline))
        retain_body_connected_faces(result)
    return result


def retain_body_connected_faces(source):
    """Discard detached bows/ties, retaining every component containing skin."""
    _, tri = weld(source["p"])
    edges = {}
    for f, face in enumerate(tri):
        for i, j in ((0, 1), (1, 2), (2, 0)):
            edges.setdefault(tuple(sorted(face[[i, j]])), []).append(f)
    adjacent = [set() for _ in tri]
    for rows in edges.values():
        for f in rows:
            adjacent[f].update(rows)
    todo = np.flatnonzero(np.char.endswith(source["groups"], "_skin")).tolist()
    retained = set()
    while todo:
        f = todo.pop()
        if f in retained:
            continue
        retained.add(f); todo.extend(adjacent[f]-retained)
    keep = np.array([i in retained for i in range(len(tri))])
    for i in np.flatnonzero(~keep):
        slot, mid, first = source["identities"][i]
        if mid == -1:
            raise ValueError("A generated cap is detached from the body")
        source["removed"].append(dict(slot=slot, mesh=mid, firstIndex=first,
                                      reason="Detached smallclothes detail"))
    source["detachedDetailFacesRemoved"] = int((~keep).sum())
    for key in ("p", "n", "uv", "weights", "groups"):
        source[key] = source[key][keep]
    source["identities"] = [x for i, x in enumerate(source["identities"]) if keep[i]]


def boundary_edges(tri):
    incidence = {}
    for f, face in enumerate(tri):
        for i, j in ((0, 1), (1, 2), (2, 0)):
            a, b = face[i], face[j]
            incidence.setdefault(tuple(sorted((a, b))), []).append((a, b, f, i, j))
    return [rows[0] for rows in incidence.values() if len(rows) == 1]


def triangulate_loop(points):
    """Ear clipping on the best fit plane; retains every boundary vertex."""
    _, _, basis = np.linalg.svd(points-points.mean(0), full_matrices=False)
    q = (points-points.mean(0)) @ basis[:2].T
    cross = lambda a, b: a[0]*b[1]-a[1]*b[0]
    orientation = np.sign(sum(cross(a, b) for a, b in zip(q, np.roll(q, -1, axis=0))))
    if not orientation:
        raise ValueError("Degenerate flap-root polygon")
    remaining = list(range(len(q))); triangles = []
    while len(remaining) > 3:
        for k, b in enumerate(remaining):
            a, c = remaining[k-1], remaining[(k+1) % len(remaining)]
            if orientation*cross(q[b]-q[a], q[c]-q[b]) <= 1e-9:
                continue
            inside = False
            for other in remaining:
                if other in (a, b, c):
                    continue
                signs = [orientation*cross(q[j]-q[i], q[other]-q[i]) for i, j in ((a,b),(b,c),(c,a))]
                if min(signs) >= -1e-9:
                    inside = True; break
            if not inside:
                triangles.append([a, b, c]); remaining.pop(k); break
        else:
            raise ValueError("Flap-root polygon could not be triangulated without moving its boundary")
    triangles.append(remaining)
    return np.array(triangles)


def close_flap_roots(source, baseline):
    """Close only newly exposed flap roots; preserve neck/wrist/ankle openings."""
    original_v, original_t = weld(baseline)
    position_key = lambda p: tuple(p.round(3))
    original_open = {tuple(sorted((position_key(original_v[a]), position_key(original_v[b]))))
                     for a, b, *_ in boundary_edges(original_t)}
    v, tri = weld(source["p"])
    edges = boundary_edges(tri)
    new_edges = [row for row in edges if tuple(sorted((position_key(v[row[0]]), position_key(v[row[1]])))) not in original_open]
    outgoing = {}
    for row in new_edges:
        if row[0] in outgoing:
            raise ValueError("Nonmanifold flap-root boundary")
        outgoing[row[0]] = row
    cap_p, cap_n, cap_uv, cap_w, cap_groups, cap_ids = [], [], [], [], [], []
    loops = 0
    while outgoing:
        start = next(iter(outgoing)); vertex = start; loop = []; rows = []
        while True:
            if vertex not in outgoing:
                raise ValueError("Flap root is not a closed loop")
            row = outgoing.pop(vertex); loop.append(vertex); rows.append(row)
            vertex = row[1]
            if vertex == start:
                break
        # Reverse the retained faces' boundary orientation to sew in a cap.
        order = np.arange(len(loop))[::-1]
        loop = np.array(loop)[order]
        rows = [rows[i] for i in order]
        p = v[loop]
        n = np.array([source["n"][f, i] for _, _, f, i, _ in rows])
        uv = np.array([source["uv"][f, i] for _, _, f, i, _ in rows])
        w = np.array([source["weights"][f, i] for _, _, f, i, _ in rows])
        slot = source["groups"][rows[0][2]].split("_")[0]
        t = triangulate_loop(p)
        cap_p.extend(p[t]); cap_n.extend(n[t]); cap_uv.extend(uv[t]); cap_w.extend(w[t])
        cap_groups.extend([slot+"_smallclothes_cap"]*len(t))
        cap_start = len(cap_ids)
        cap_ids.extend((slot, -1, cap_start+i) for i in range(len(t)))
        loops += 1
    if cap_p:
        for key, extra in (("p", cap_p), ("n", cap_n), ("uv", cap_uv), ("weights", cap_w)):
            source[key] = np.concatenate((source[key], np.array(extra)))
        source["groups"] = np.concatenate((source["groups"], np.array(cap_groups)))
        source["identities"].extend(cap_ids)
    source["generatedCaps"] = len(cap_p)
    source["closedFlapRootLoops"] = loops


def density(path):
    s, data, meshes = load_snapshot(path)
    mesh = next(m for m in meshes if m["id"] == 0)
    ids = np.frombuffer(data, "<u2", mesh["indexCount"], mesh["indexOffset"]).reshape(-1, 3)
    p = field(data, mesh, 0)[ids, :3] * 1000
    a = areas(p)
    a = a[a > 1e-8]
    return dict(sha256=s["sha256"], triangles=len(a), areaMm2=float(a.sum()),
                meanTriangleAreaMm2=float(a.mean()), medianTriangleAreaMm2=float(np.median(a)))


def weld(p):
    # Resolve export aliases, including the stock skin/smallclothes joins.
    # A 0.001 mm key tolerance is recorded; original coordinates remain in p.
    _, inverse = np.unique(p.reshape(-1, 3).round(3), axis=0, return_inverse=True)
    v = np.zeros((inverse.max() + 1, 3))
    np.add.at(v, inverse, p.reshape(-1, 3))
    v /= np.bincount(inverse)[:, None]
    return v, inverse.reshape(-1, 3)


def patches(p, n, tri, flat_faces=None):
    """Cubic PN position patches with shared edge controls and fixed open edges.

    Curved PN Triangles, Vlachos et al. (2001), section 3.
    https://www.cs.jhu.edu/~misha/ReadingSeminar/Papers/Vlachos01.pdf
    """
    controls = {(3, 0, 0): p[:, 0], (0, 3, 0): p[:, 1], (0, 0, 3): p[:, 2]}
    if flat_faces is None:
        flat_faces = np.zeros(len(p), bool)
    edge_controls = {}
    for i, j in ((0, 1), (1, 2), (2, 0)):
        for a, b in ((i, j), (j, i)):
            exponent = tuple(2 if k == a else 1 if k == b else 0 for k in range(3))
            delta = p[:, b] - p[:, a]
            control = (2 * p[:, a] + p[:, b] - np.sum(delta * n[:, a], axis=1)[:, None] * n[:, a]) / 3
            controls[exponent] = control.copy()
        for f, (a, b) in enumerate(tri[:, [i, j]]):
            key = (min(a, b), max(a, b))
            edge_controls.setdefault(key, []).append((f, i, j))
    fixed = 0
    for rows in edge_controls.values():
        # A boundary or nonmanifold edge stays on its original straight line.
        # Interior edge aliases share one curve, including skin/cloth joins.
        straight = len(rows) != 2 or any(flat_faces[f] for f, _, _ in rows)
        fixed += straight
        for endpoint in (0, 1):
            refs, values = [], []
            for f, i, j in rows:
                a, b = sorted((i, j), key=lambda k: tri[f, k])
                if endpoint:
                    a, b = b, a
                e = tuple(2 if k == a else 1 if k == b else 0 for k in range(3))
                refs.append((f, e))
                values.append((2 * p[f, a] + p[f, b]) / 3 if straight else controls[e][f])
            common = np.mean(values, axis=0)
            for f, e in refs:
                controls[e][f] = common
    edges = [v for e, v in controls.items() if max(e) == 2]
    average = np.mean(edges, axis=0)
    controls[1, 1, 1] = average + (average - p.mean(axis=1)) / 2
    return controls, fixed


def evaluate(controls, parent, bary, derivatives=False):
    result = np.zeros((*bary.shape[:-1], 3))
    du, dv = np.zeros_like(result), np.zeros_like(result)
    coefficients = {3: 1, 2: 3, 1: 6}
    for exponent, values in controls.items():
        e = np.array(exponent)
        factor = coefficients[int(e.max())]
        value = values[parent]
        result += factor * np.prod(bary ** e, axis=-1)[..., None] * value
        if derivatives:
            for axis, derivative in ((0, du), (1, dv)):
                term = np.zeros(bary.shape[:-1])
                for k, sign in ((axis, 1), (2, -1)):
                    if e[k]:
                        power = e.copy(); power[k] -= 1
                        term += sign * e[k] * np.prod(bary ** power, axis=-1)
                derivative += factor * term[..., None] * value
    if derivatives:
        return result, unit(np.cross(du, dv))
    return result


def refine(v, tri, target_areas, max_rounds=24):
    """Conforming shared-edge bisection in the original triangle domain."""
    parent = np.arange(len(tri))
    bary = np.repeat(np.eye(3)[None], len(tri), axis=0)
    history = []
    for iteration in range(max_rounds):
        a = areas(v[tri])
        bad = a > target_areas[parent] * 1.25
        history.append(dict(round=iteration, faces=len(tri), overTarget=int(bad.sum())))
        if not bad.any():
            return v, tri, parent, bary, history
        edge = np.stack((tri[:, [0, 1]], tri[:, [1, 2]], tri[:, [2, 0]]), axis=1)
        lengths = np.linalg.norm(v[edge[:, :, 0]] - v[edge[:, :, 1]], axis=-1)
        longest = lengths.argmax(axis=1)
        marked = sorted({tuple(sorted(e)) for e in edge[np.flatnonzero(bad), longest[bad]]})
        mid = {e: len(v) + i for i, e in enumerate(marked)}
        v = np.concatenate((v, np.array([(v[a] + v[b]) / 2 for a, b in marked])))
        new_tri, new_parent, new_bary = [], [], []
        for t, owner, bc in zip(tri, parent, bary):
            cells = [(t.tolist(), bc)]
            for i, j in ((0, 1), (1, 2), (2, 0)):
                key = tuple(sorted((t[i], t[j])))
                if key not in mid:
                    continue
                replacement = []
                for vertices, coords in cells:
                    if t[i] not in vertices or t[j] not in vertices:
                        replacement.append((vertices, coords)); continue
                    x, y = vertices.index(t[i]), vertices.index(t[j])
                    if (x + 1) % 3 != y:
                        x, y = y, x
                    z = 3 - x - y
                    m = (coords[x] + coords[y]) / 2
                    replacement.extend([([vertices[x], mid[key], vertices[z]], np.array([coords[x], m, coords[z]])),
                                        ([mid[key], vertices[y], vertices[z]], np.array([m, coords[y], coords[z]]))])
                cells = replacement
            for vertices, coords in cells:
                new_tri.append(vertices); new_parent.append(owner); new_bary.append(coords)
        tri = np.array(new_tri); parent = np.array(new_parent); bary = np.array(new_bary)
        if len(tri) > 500000:
            raise ValueError("Refinement exceeded 500000 faces")
    raise ValueError("Density refinement did not converge")


def write_obj(path, positions, tri, normals, uv, groups):
    with Path(path).open("w", encoding="utf-8") as f:
        f.write("# Normal-guided vanilla reference, millimetres; skin and smallclothes are separate groups\n")
        for p in positions:
            f.write("v %.9f %.9f %.9f\n" % tuple(p))
        for p in uv.reshape(-1, 2):
            f.write("vt %.9f %.9f\n" % tuple(p))
        for n in normals.reshape(-1, 3):
            f.write("vn %.9f %.9f %.9f\n" % tuple(n))
        group = None
        for i, (t, label) in enumerate(zip(tri, groups)):
            if label != group:
                f.write("g " + label + "\n"); group = label
            f.write("f " + " ".join(f"{v + 1}/{3*i+j+1}/{3*i+j+1}" for j, v in enumerate(t)) + "\n")


def build(args):
    source = read_sources(args.vanilla_top, args.vanilla_legs, args.cleanup)
    reference = dict(top=density(args.af_top), legs=density(args.af_legs))
    p = source["p"]
    v, tri = weld(p)
    welded_p = v[tri]
    controls, fixed_edges = patches(welded_p, source["n"], tri,
                                    np.char.endswith(source["groups"], "_cap"))
    target = np.array([reference[g.split("_")[0]]["meanTriangleAreaMm2"] for g in source["groups"]])
    flat_v, out_tri, parent, bc, history = refine(v, tri, target)
    curved, normals = evaluate(controls, parent[:, None], bc, True)
    sums = np.zeros_like(flat_v); counts = np.bincount(out_tri.ravel(), minlength=len(flat_v))
    np.add.at(sums, out_tri.ravel(), curved.reshape(-1, 3))
    curved_v = sums / counts[:, None]
    gap = np.linalg.norm(curved - curved_v[out_tri], axis=-1)
    if gap.max() > 1e-6:
        raise ValueError(f"Patch crack: {gap.max()} mm")
    flat = flat_v[out_tri]; final = curved_v[out_tri]
    dots = np.sum(np.cross(final[:, 1]-final[:, 0], final[:, 2]-final[:, 0]) *
                  np.cross(flat[:, 1]-flat[:, 0], flat[:, 2]-flat[:, 0]), axis=-1)
    if np.any(dots <= 0) or np.any(areas(final) < 1e-10):
        raise ValueError("Normal reconstruction produced a reversed or degenerate triangle")
    edge_counts = {}
    for t in out_tri:
        for i, j in ((0, 1), (1, 2), (2, 0)):
            key = tuple(sorted(t[[i, j]]))
            edge_counts[key] = edge_counts.get(key, 0)+1
    nonmanifold = sum(count > 2 for count in edge_counts.values())
    if nonmanifold:
        raise ValueError(f"Reference contains {nonmanifold} nonmanifold edges")
    uv = np.einsum("fci,fij->fcj", bc, source["uv"][parent])
    weights = np.einsum("fci,fij->fcj", bc, source["weights"][parent])
    groups = source["groups"][parent]
    out = Path(args.output); out.mkdir(parents=True, exist_ok=True)
    write_obj(out/"vanilla-curved.obj", curved_v, out_tri, normals, uv, groups)
    flat_n = np.repeat(unit(np.cross(flat[:, 1]-flat[:, 0], flat[:, 2]-flat[:, 0]))[:, None], 3, axis=1)
    write_obj(out/"vanilla-flat-control.obj", flat_v, out_tri, flat_n, uv, groups)
    # Original geometry is preserved separately, before alias welding.
    original_tri = np.arange(len(p)*3).reshape(-1, 3)
    write_obj(out/"vanilla-source.obj", p.reshape(-1, 3), original_tri, source["n"], source["uv"], source["groups"])
    np.savez_compressed(out/"vanilla-reference.npz", position=curved_v, triangles=out_tri,
                        flatPosition=flat_v, normal=normals, uv=uv, weights=weights,
                        boneNames=np.array(source["bones"]), group=groups, sourceFace=parent,
                        sourceBarycentric=bc, sourcePosition=p, sourceNormal=source["n"],
                        sourceGroups=source["groups"], sourceIdentity=np.array(source["identities"], dtype=str))
    distance = np.linalg.norm(curved_v-flat_v, axis=1)
    report = dict(status="candidate for visual review", units="millimetres",
                  method="Cubic PN position patches from authored mesh normals; no normal-map texture sampling",
                  densityPolicy="Per-slot AF mean physical triangle area; conforming longest-edge refinement to 1.25x mean. This matches average surface density, not AF connectivity or local anatomical density.",
                  sourceHashes=source["hashes"], afReferences=reference,
                  sourceFaces=len(p), outputFaces=len(out_tri), outputVertices=len(curved_v),
                  removedFlapFaces=source["removed"], fixedOpenOrNonmanifoldEdges=fixed_edges,
                  generatedRootCapFaces=source["generatedCaps"], closedFlapRootLoops=source.get("closedFlapRootLoops", 0),
                  detachedDetailFacesRemoved=source.get("detachedDetailFacesRemoved", 0),
                  openEdges=sum(count == 1 for count in edge_counts.values()), nonmanifoldEdges=nonmanifold,
                  boundaryPolicy="Only the original neck, two wrists and two ankles remain open; flap-root caps use linear patches and fixed shared boundary curves.",
                  aliasWeldMaxMovementMm=float(np.linalg.norm(welded_p-p, axis=-1).max()),
                  sharedPatchMaxGapMm=float(gap.max()), reversedTriangles=int((dots<=0).sum()),
                  displacementFromFlatMm=dict(max=float(distance.max()), p95=float(np.percentile(distance,95))),
                  weightSumMaxError=float(np.abs(weights.sum(axis=-1)-1).max()),
                  densityByGroup={g: dict(faces=int((groups==g).sum()), meanTriangleAreaMm2=float(areas(final[groups==g]).mean()),
                                         targetMeanAreaMm2=reference[g.split('_')[0]]["meanTriangleAreaMm2"])
                                  for g in np.unique(groups)}, history=history,
                  scope="Stock top and leg skin plus close-fitting smallclothes envelope and explicit flap-root caps. Covered areas remain clothing, not reconstructed naked anatomy. No body morph, texture-normal reconstruction or motion validation yet.")
    (out/"report.json").write_text(json.dumps(report, indent=2)+"\n", encoding="utf-8")
    print(json.dumps({k: report[k] for k in ("sourceFaces", "outputFaces", "outputVertices", "sharedPatchMaxGapMm", "reversedTriangles", "displacementFromFlatMm")}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--vanilla-top", required=True, type=Path)
    parser.add_argument("--vanilla-legs", required=True, type=Path)
    parser.add_argument("--af-top", required=True, type=Path)
    parser.add_argument("--af-legs", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--cleanup", type=Path, default=Path(__file__).with_name("source_cleanup.json"))
    build(parser.parse_args())
