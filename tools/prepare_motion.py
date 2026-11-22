"""Extract independent motion choices from a locally owned Tiramisu2 2110 archive."""
import argparse
import gzip
import hashlib
import io
import json
from pathlib import Path
import re
from zipfile import ZipFile, ZIP_DEFLATED

FAMILIES = {"Tiramisu": "Classic bounce", "Pound Cake": "Vertical bounce",
            "Petit Four": "Small bust", "Hitomi": "Low sway", "Squish": "Compression",
            "Sunrise": "Lift", "Sunset": "Curved lift", '"Absolute Realism"': "Exaggerated motion"}
HEIGHT = ["Lowest", "Lower", "Low", "Vanilla", "High", "Highest"]
RACES = ["0201", "0401", "0601", "0801", "1001", "1401", "1601", "1801"]


def chest_name(family, name):
    height = re.search(r"(?:^| - )(Normal Height|Vanilla(?: Height)?|Lowest|Lower|Low|Highest|High)(?: Height)?(?= - | \(|$)", name)
    level = 3
    if height:
        word = height[1].split()[0]
        level = 3 if word in ("Normal", "Vanilla") else HEIGHT.index(word)
        name = name[:height.start()] + name[height.end():]
    for old, new in [("Pound Cake Transition", "Vertical blend"), ("[Small Collisions]", "Small collision radius"),
                     ("Anime Squish", "Deep compression"), ("Squish", ""), ("Some Other Physics Mod", "Style 1"),
                     ("You know where this is going", "Style 2"), ("Jell-o", "Gel"),
                     ("Reduced Motion x", "reduced motion "), ("RM x", "reduced motion "), ("Flat x", "flattening "),
                     ("Push-Up x", "lift "), ("Normal", "Standard")]:
        name = name.replace(old, new)
    name = name.strip(" -")
    return FAMILIES[family] + (" / " + name if name else ""), level


def build(source, dest, source_bank):
    entries = {str(i): {} for i in (0, 3, 4, 5, 6, 7, 8)}
    assets = {}
    with ZipFile(source) as outer:
        for variant in ("Miqo Ear Jiggle + Tail V1", "Miqo Ear Jiggle + Tail V2"):
            with ZipFile(io.BytesIO(outer.read(f"tiramisu2 [{variant}].pmp"))) as donor:
                lookup = {p.lower().replace(chr(92), "/"): p for p in donor.namelist()}
                def add(area, label, option, height=3):
                    files = {key.split("/")[2][1:]: p for key, p in option["Files"].items()}
                    row = []
                    for race in RACES:
                        raw = donor.read(lookup[files[race].lower().replace(chr(92), "/")])
                        key = hashlib.sha256(raw).hexdigest()
                        if key not in assets:
                            assets[key] = [f"tiramisu2 [{variant}].pmp", lookup[files[race].lower().replace(chr(92), "/")]]
                        row.append(key)
                    levels = entries[str(area)].setdefault(label, {})
                    if height in levels:
                        raise ValueError(f"Duplicate {area}/{label}/{height}")
                    levels[height] = row

                for path in sorted(p for p in donor.namelist() if p.startswith("group_")):
                    g = json.loads(donor.read(path).decode("utf-8-sig"))
                    if not g["Name"].startswith("["):
                        continue
                    body, family = g["Name"][1:].split("] ", 1)
                    opts = [o for o in g["Options"] if o["Files"]]
                    if family == "Tiramisu":
                        standard = next(o for o in opts if o["Name"] == "Normal - Vanilla Height")
                        if body == "Default Booty Type 0":
                            if variant.endswith("V2"):
                                add(8, "Alternate tail sway", standard)
                                break
                            add(7, "Full ear bounce", standard)
                            add(6, "Extended motion", standard)
                            add(4, "Alternate motion", next(o for o in opts if o["Name"] == "Clothed - Stronger - High"))
                        if not variant.endswith("V2"):
                            if body.startswith(("Booty Type", "Default")):
                                n = int(re.search(r"Type (\d+)", body)[1])
                                label = {0:"Standard bounce", 14:"Legacy bounce", 15:"Amplified bounce", 16:"Reduced bounce"}.get(n, f"Bounce style {n}")
                                add(3, label, standard)
                            if body.startswith(("Type 0", "Default")):
                                label = "Reduced motion" if "Reduced" in body else "Enhanced motion" if "Enhanced" in body else "Standard motion"
                                for area in [4, 5]:
                                    add(area, label, standard)
                    if body == "Default Booty Type 0" and not variant.endswith("V2"):
                        for o in opts:
                            label, level = chest_name(family, o["Name"])
                            add(0, label, o, level)
        for options in entries.values():
            for name, levels in options.items():
                if 3 not in levels:
                    raise ValueError("Missing reference height for " + name)
        dest.write_bytes(gzip.compress(json.dumps({"Areas": entries, "Sources": assets}, separators=(",", ":")).encode(), mtime=0))
    with ZipFile(source) as original, ZipFile(source_bank, "w", ZIP_DEFLATED) as output:
        for package in sorted({path[0] for path in assets.values()}):
            with ZipFile(io.BytesIO(original.read(package))) as donor:
                for key, path in assets.items():
                    if path[0] != package:
                        continue
                    data = donor.read(path[1])
                    if hashlib.sha256(data).hexdigest() != key:
                        raise ValueError("Source hash mismatch")
                    output.writestr(key, data)
    print({area: len(options) for area, options in entries.items()})


if __name__ == "__main__":
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("source", type=Path)
    p.add_argument("destination", type=Path)
    p.add_argument("source_bank", type=Path)
    args = p.parse_args()
    pending = args.destination.with_suffix(".building")
    build(args.source, pending, args.source_bank)
    pending.replace(args.destination)
