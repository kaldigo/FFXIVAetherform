"""Author and validate the data-only custom-fits branch. Uses Python's standard library."""
import argparse
import hashlib
import json
import re
import shutil
from pathlib import Path


def digest(data):
    return hashlib.sha256(data).hexdigest()


def asset_path(root, relative):
    assert isinstance(relative, str) and 0 < len(relative) < 240
    assert all(re.fullmatch(r"[a-zA-Z0-9_.-]+", p) and p not in (".", "..") for p in relative.split("/")), relative
    path = (root / relative).resolve()
    assert path.is_relative_to(root.resolve()), relative
    return path


def validate(root, build=False):
    entries = []
    keys = set()
    ids = set()
    for path in sorted((root / "recipes").rglob("*.json")):
        data = path.read_bytes()
        r = json.loads(data)
        assert r["Schema"] == 1
        assert re.fullmatch(r"[a-z0-9][a-z0-9-]{0,79}", r["Id"])
        match = re.fullmatch(r"chara/equipment/e(\d{4})/model/c(\d{4})e\1_(top|dwn|glv|sho)\.mdl", r["GamePath"])
        assert match and match[1] not in ("0000", "9903")
        assert match[2] in ("0101", "0201", "0401", "0601", "0801", "1001", "1401", "1601", "1801")
        assert r["SourceKind"] in ("vanilla", "family")
        assert re.fullmatch(r"[0-9a-f]{64}", r["SourceHash"])
        key = (r["GamePath"], r["SourceKind"], r["SourceHash"])
        assert key not in keys and r["Id"] not in ids, "Duplicate source/recipe"
        keys.add(key)
        ids.add(r["Id"])
        variants = r.get("Variants", [])
        assert len(variants) <= 32 and len({v["Id"] for v in variants}) == len(variants)
        assert r.get("Default") is not None or variants
        for v in variants + ([r["Default"]] if r.get("Default") is not None else []):
            assert re.fullmatch(r"[a-z0-9][a-z0-9-]{0,79}", v["Id"])
            assert isinstance(v["Name"], str) and v["Name"].strip() and len(v["Name"]) <= 100
            for field in ("Mesh", "Classification"):
                if a := v.get(field):
                    assert field != "Classification" or r["SourceKind"] == "vanilla"
                    b = asset_path(root, a["Path"]).read_bytes()
                    assert re.fullmatch(r"[0-9a-f]{64}", a["Sha256"]) and digest(b) == a["Sha256"]
                    assert len(b) <= 64 * 1024 * 1024
            edits = v.get("Edits", [])
            assert len(edits) <= 4096
            for e in edits:
                assert isinstance(e["Mesh"], int) and e["Mesh"] >= 0
                assert e["Vertices"] and all(isinstance(i, int) and i >= 0 for i in e["Vertices"])
                assert e.get("DeltaMm") is not None or e.get("Weights") is not None
                if (d := e.get("DeltaMm")) is not None:
                    assert len(d) == 3 and all(isinstance(n, (int, float)) and abs(n) < float("inf") for n in d)
                if (w := e.get("Weights")) is not None:
                    assert w and all(n and isinstance(v, (int, float)) and 0 < v < float("inf") for n, v in w.items())
        entries.append(dict(Id=r["Id"], GamePath=r["GamePath"], SourceKind=r["SourceKind"],
                            SourceHash=r["SourceHash"], Recipe=path.relative_to(root).as_posix(), Sha256=digest(data)))
    index = dict(Schema=1, Entries=entries)
    if build:
        (root / "index.json").write_text(json.dumps(index, indent=2) + "\n", encoding="utf-8")
    else:
        assert json.loads((root / "index.json").read_bytes()) == index, "Index is stale; run index"
    print(f"Validated {len(entries)} custom fit recipes and their asset checksums.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("new", "index", "check"))
    parser.add_argument("root", type=Path)
    parser.add_argument("--id")
    parser.add_argument("--game-path")
    parser.add_argument("--kind", choices=("vanilla", "family"))
    parser.add_argument("--source", type=Path)
    parser.add_argument("--mesh", type=Path)
    parser.add_argument("--rules", type=Path)
    args = parser.parse_args()
    root = args.root.resolve()
    root.mkdir(parents=True, exist_ok=True)
    if args.command == "new":
        assert args.id and args.game_path and args.kind and args.source
        assert re.fullmatch(r"[a-z0-9][a-z0-9-]{0,79}", args.id)
        plan = dict(Id="default", Name="Corrected", Edits=json.loads(args.rules.read_bytes()) if args.rules else [])
        if args.mesh:
            data = args.mesh.read_bytes()
            relative = "meshes/" + digest(data) + ".mdl"
            target = asset_path(root, relative)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(args.mesh, target)
            plan["Mesh"] = dict(Path=relative, Sha256=digest(data))
        r = dict(Schema=1, Id=args.id, GamePath=args.game_path, SourceKind=args.kind,
                 SourceHash=digest(args.source.read_bytes()), Default=plan, Variants=[])
        path = root / "recipes" / (args.id + ".json")
        path.parent.mkdir(exist_ok=True)
        with path.open("x", encoding="utf-8") as file:
            file.write(json.dumps(r, indent=2) + "\n")
    validate(root, build=args.command != "check")


if __name__ == "__main__":
    main()
