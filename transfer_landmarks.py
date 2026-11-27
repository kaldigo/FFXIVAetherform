"""Transfer accepted wrap landmarks through native triangle lineage.

The AF point addresses are preserved. Vanilla points retain their material
locations, evaluated on the new curved, piecewise-linear exported surface.
No nearest-surface projection, extra controls or wrapping solve.
"""
import argparse
import csv
import hashlib
import json
from pathlib import Path

import numpy as np

from reference_surface import evaluate, patches, weld


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def triangle_key(triangle):
    return tuple(sorted(tuple(p) for p in triangle.round(6)))


def barycentric(point, triangle):
    edges = (triangle[:2]-triangle[2]).T
    bc = np.linalg.lstsq(edges, point-triangle[2], rcond=None)[0]
    return np.r_[bc, 1-bc.sum()]


def child_address(owner, material_bary, child_bary, by_parent):
    """Locate the same native material address in its new child triangle."""
    ids = by_parent.get(owner, [])
    if not len(ids):
        raise ValueError("Native parent has no output triangles")
    # Child barycentric vertices are columns in the interpolation system.
    b = np.linalg.solve(child_bary[ids].transpose(0, 2, 1),
                        np.broadcast_to(material_bary, (len(ids), 3))[..., None])[..., 0]
    valid = (b.min(axis=1) >= -1e-8) & (b.max(axis=1) <= 1+1e-8)
    if not valid.any():
        raise ValueError("Material point has no containing child triangle")
    candidates = np.flatnonzero(valid)
    # On an edge prefer the largest child-domain triangle, deterministically.
    domains = np.abs(np.linalg.det(child_bary[ids]))
    chosen = candidates[np.argmax(domains[candidates])]
    result = np.clip(b[chosen], 0, 1); result /= result.sum()
    return int(ids[chosen]), result


def write_points(path, names, face, bary):
    rows = {str(name): [int(f), float(b[0]), float(b[1])] for name, f, b in zip(names, face, bary)}
    Path(path).write_text(json.dumps(rows, indent=2)+"\n", encoding="utf-8")


def read_points(path):
    data = json.loads(Path(path).read_text())
    rows = np.array(list(data.values()))
    return list(data), rows[:, 0].astype(int), np.c_[rows[:, 1:], 1-rows[:, 1:].sum(axis=1)]


def read_obj(path):
    vertices, faces = [], []
    for line in Path(path).read_text().splitlines():
        words = line.split()
        if words and words[0] == "v":
            vertices.append([float(x) for x in words[1:4]])
        elif words and words[0] == "f":
            if len(words) != 4:
                raise ValueError("Expected triangle OBJ")
            faces.append([int(x.split('/')[0])-1 for x in words[1:]])
    return np.array(vertices), np.array(faces)


def write_target(path, vertices, faces):
    with Path(path).open("w", encoding="ascii") as f:
        f.write("# Unchanged AF Small / Medium B target, millimetres\n")
        for p in vertices:
            f.write("v "+" ".join(format(float(x), ".17g") for x in p)+"\n")
        for face in faces:
            f.write("f "+" ".join(str(int(x)+1) for x in face)+"\n")


def transfer(args):
    paths = {key: Path(getattr(args, key)) for key in
             ("accepted_controls", "old_topology", "old_lineage", "reference")}
    controls, old, lineage, reference = [np.load(paths[key], allow_pickle=False) for key in paths]
    names = controls["names"]
    if len(set(names)) != len(names):
        raise ValueError("Accepted point identities are not unique")
    old_triangles = old["source_vertices"][old["source_faces"]]
    if not np.array_equal(old_triangles, lineage["position"]):
        raise ValueError("Old topology and subdivision lineage disagree")
    old_face, old_bary = controls["sourceFace"], controls["sourceBary"]
    replay = np.einsum("ni,nij->nj", old_bary, old_triangles[old_face])
    if np.linalg.norm(replay-controls["source"], axis=1).max() > 1e-7:
        raise ValueError("Accepted source positions disagree with old point addresses")
    target_triangles = old["original_vertices"][old["original_faces"]]
    target_replay = np.einsum("ni,nij->nj", controls["targetBary"], target_triangles[controls["targetFace"]])
    if np.linalg.norm(target_replay-controls["original"], axis=1).max() > 1e-7:
        raise ValueError("Accepted AF positions disagree with target addresses")

    old_parent = lineage["parentFace"][old_face]
    native_bary = np.einsum("ni,nij->nj", old_bary, lineage["parentBary"][old_face])
    native = {}
    for owner in np.unique(old_parent):
        selected = lineage["parentFace"] == owner
        native[int(owner)] = np.linalg.lstsq(lineage["parentBary"][selected].reshape(-1, 3),
                                            old_triangles[selected].reshape(-1, 3), rcond=None)[0]
    source = reference["sourcePosition"]
    source_obj = paths["reference"].parent/"vanilla-curved.obj"
    serialized_v, serialized_t = read_obj(source_obj)
    if not np.array_equal(serialized_t, reference["triangles"]) or not np.allclose(serialized_v, reference["position"], rtol=0, atol=1e-8):
        raise ValueError("Curved OBJ does not match reference topology and positions")
    lookup = {}
    for i, tri in enumerate(source):
        # Generated caps have no native MDL identity.
        if int(reference["sourceIdentity"][i, 1]) >= 0:
            lookup.setdefault(triangle_key(tri), []).append(i)
    by_parent = {}
    for i, owner in enumerate(reference["sourceFace"]):
        by_parent.setdefault(int(owner), []).append(i)
    by_parent = {k: np.array(v) for k, v in by_parent.items()}
    new_face, new_bary, new_parent, new_material = [], [], [], []
    rebound, unresolved, material_errors = [], [], []
    for name, owner, bc, point in zip(names, old_parent, native_bary, replay):
        triangle = native[int(owner)]
        if np.linalg.norm(bc@triangle-point) > 1e-6:
            raise ValueError("Old native barycentric lineage failed replay")
        matches = lookup.get(triangle_key(triangle), [])
        options = []
        for i in matches:
            # Solve the corner permutation on the exact native triangle.
            b = barycentric(point, source[i])
            error = np.linalg.norm(b@source[i]-point)
            if error < 1e-6 and b.min() >= -1e-7:
                options.append((error, i, b))
        route = "native triangle"
        if not options:
            # A point on a removed face can survive at its exact shared root.
            # Accept only a retained native boundary address at the same XYZ.
            for i, tri in enumerate(source):
                if np.any(point < tri.min(axis=0)-1e-6) or np.any(point > tri.max(axis=0)+1e-6):
                    continue
                b = barycentric(point, tri)
                error = np.linalg.norm(b@tri-point)
                if error < 1e-6 and b.min() >= -1e-7 and np.min(np.abs(b)) < 1e-7:
                    options.append((error, i, b))
            route = "exact shared root boundary"
        if not options:
            unresolved.append(dict(name=str(name), oldNativeFace=int(owner), oldPositionMm=point.tolist()))
            continue
        error, owner, material = min(options, key=lambda row: (row[0], row[1]))
        face, b = child_address(owner, material, reference["sourceBarycentric"], by_parent)
        if route != "native triangle":
            rebound.append(dict(name=str(name), route=route, materialErrorMm=float(error)))
        new_face.append(face); new_bary.append(b); new_parent.append(owner); new_material.append(material)
        material_errors.append(error)
    out = Path(args.output); out.mkdir(parents=True, exist_ok=True)
    if unresolved:
        (out/"unresolved-landmarks.json").write_text(json.dumps(unresolved, indent=2)+"\n")
        raise ValueError(f"{len(unresolved)} landmarks have no retained material location; inspect unresolved-landmarks.json")
    new_face, new_bary = np.array(new_face), np.array(new_bary)
    new_parent, new_material = np.array(new_parent), np.array(new_material)
    new_positions = np.einsum("ni,nij->nj", new_bary, reference["position"][reference["triangles"][new_face]])
    flat_positions = np.einsum("ni,nij->nj", new_bary, reference["flatPosition"][reference["triangles"][new_face]])
    material_error = np.linalg.norm(flat_positions-replay, axis=1)
    if material_error.max() > 1e-6:
        raise ValueError("Material identity changed during child-triangle binding")
    v, t = weld(source)
    cp, _ = patches(v[t], reference["sourceNormal"], t,
                     np.char.endswith(reference["sourceGroups"], "_cap"))
    exact = evaluate(cp, new_parent, new_material)
    movement = np.linalg.norm(new_positions-replay, axis=1)
    tessellation = np.linalg.norm(exact-new_positions, axis=1)
    write_points(out/"Vanilla-Curved.points.json", names, new_face, new_bary)
    write_points(out/"AF-Small-MediumB.points.json", names, controls["targetFace"], controls["targetBary"])
    write_target(out/"AF-Small-MediumB.obj", old["original_vertices"], old["original_faces"])
    target_v, target_t = read_obj(out/"AF-Small-MediumB.obj")
    if not np.array_equal(target_t, old["original_faces"]) or not np.array_equal(target_v, old["original_vertices"]):
        raise ValueError("Serialized AF target changed")
    for filename, triangles, expected in (("Vanilla-Curved.points.json", serialized_v[serialized_t], new_positions),
                                          ("AF-Small-MediumB.points.json", target_v[target_t], target_replay)):
        saved_names, fi, b = read_points(out/filename)
        if saved_names != names.tolist() or np.linalg.norm(np.einsum("ni,nij->nj", b, triangles[fi])-expected, axis=1).max() > 1e-8:
            raise ValueError("Serialized landmark replay failed")
    np.savez_compressed(out/"paired-landmarks.npz", names=names, sourceFace=new_face, sourceBary=new_bary,
                        sourcePosition=new_positions, sourceParent=new_parent, sourceMaterialBary=new_material,
                        oldSourcePosition=replay, targetFace=controls["targetFace"], targetBary=controls["targetBary"],
                        targetPosition=target_replay)
    with (out/"PointPairs.csv").open("w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow(["Name", "VanillaXmm", "VanillaYmm", "VanillaZmm", "AFXmm", "AFYmm", "AFZmm", "VanillaMovementMm"])
        writer.writerows([str(name), *a, *b, float(m)] for name,a,b,m in zip(names,new_positions,target_replay,movement))
    report = dict(pointPairs=len(names), inputs={k: dict(path=str(p.resolve()), sha256=sha(p)) for k,p in paths.items()},
                  sourceMesh=str(source_obj.resolve()), sourceMeshSha256=sha(source_obj),
                  targetMeshSha256=sha(out/"AF-Small-MediumB.obj"),
                  target="Unchanged AF Small chest / Medium B legs, Hip Dips off, Soft Butt off",
                  targetAddressesUnchanged=True, pointNamesAndOrderUnchanged=True,
                  maxMaterialLocationErrorMm=float(material_error.max()),
                  sourceMovementFromOldFlatSurfaceMm=dict(max=float(movement.max()), median=float(np.median(movement)), p95=float(np.percentile(movement,95))),
                  sampledSurfaceVsExactPatchMm=dict(max=float(tessellation.max()), p95=float(np.percentile(tessellation,95))),
                  exactRootRebindings=rebound, unresolvedPoints=0,
                  scope="Re-addressed points on the curved vanilla reference; no wrapping solve, new pairs, or changes to previous work")
    (out/"landmark-transfer-report.json").write_text(json.dumps(report, indent=2)+"\n")
    print(json.dumps({k:v for k,v in report.items() if k not in ("inputs", "exactRootRebindings")}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ("accepted-controls", "old-topology", "old-lineage", "reference", "output"):
        parser.add_argument("--"+key, required=True, type=Path)
    transfer(parser.parse_args())
