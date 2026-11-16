"""Prepare the three-tab body and verified per-race physics bank."""
import argparse
import hashlib
import json
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

RACES = ["0201", "0401", "0601", "0801", "1001", "1401", "1601", "1801"]


def references(value):
    if isinstance(value, dict):
        yield from value.get("Files", {}).values()
        for child in value.values():
            yield from references(child)
    elif isinstance(value, list):
        for child in value:
            yield from references(child)


def prepare(source, physics_source, output):
    output.mkdir(parents=True, exist_ok=True)
    with ZipFile(source) as archive:
        meta = json.loads(archive.read("meta.json"))
        if (meta["Name"], meta["Version"]) != ("Aetherform", "0.1.6"):
            raise ValueError("Expected the Aetherform 0.1.6 input package.")
        physics = {g["Name"]: g for g in meta["Groups"] if g["Type"] == "Combining" and g["Name"] != "Leg Options"}
        primary = physics["Body physics"]
        meta["DefaultData"]["Files"].update(primary["Containers"][219]["Files"])
        groups = []
        for group in meta["Groups"]:
            name = group["Name"]
            if name in physics:
                continue
            if group["Type"] == "Imc" and "nail Options" in name:
                identifier = group["Identifier"]
                for variant in ([0, 1] if group.get("AllVariants") else [identifier["Variant"]]):
                    entry = dict(ObjectType="Equipment", PrimaryId=identifier["PrimaryId"],
                                 EquipSlot=identifier["EquipSlot"], Variant=variant, Entry=group["DefaultEntry"])
                    meta["DefaultData"]["Manipulations"].append(dict(Type="Imc", Manipulation=entry))
                continue
            if name.startswith("Hands - "):
                group["Options"] = [o for o in group["Options"] if o["Name"] in ("None", "Short Nails", "Yiggle - Short Nails")]
                group["DefaultSettings"] = 2
            group["Page"] = 2 if "pubes" in name.lower() else int(group.get("Page", 0) > 0)
            groups.append(group)
        meta.update(Version="0.3.0", Groups=groups)
        with ZipFile(output / "Aetherform.pmp", "w", ZIP_DEFLATED, compresslevel=4) as body:
            body.writestr("meta.json", json.dumps(meta, ensure_ascii=False).encode())
            for path in sorted(set(references(meta))):
                body.writestr(path, archive.read(path))
    with ZipFile(physics_source) as src, ZipFile(output / "physics.zip", "w", ZIP_DEFLATED) as bank:
        index = json.loads(src.read("index.json"))
        heights = json.loads(src.read("heights.json"))
        if index["races"] != RACES or len(index["rows"]) != 576:
            raise ValueError("Unexpected physics bank.")
        bank.writestr("index.json", src.read("index.json"))
        bank.writestr("heights.json", src.read("heights.json"))
        for name in sorted({asset for row in index["rows"].values() for source in row for asset in heights[source]}):
            path = "bank/" + name + ".phyb"
            data = src.read(path)
            if hashlib.sha256(data).hexdigest()[:24] != name:
                raise ValueError("Invalid physics asset " + path)
            bank.writestr(path, data)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("physics_source", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    prepare(args.source, args.physics_source, args.output)
