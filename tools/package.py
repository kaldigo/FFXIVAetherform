"""Validate all physics selections and package a local Dalamud development install."""
import argparse
import hashlib
import itertools
import json
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED

from prepare import references


def package(project, output):
    payload = project / "payload"
    with ZipFile(payload / "Aetherform.pmp") as body, ZipFile(payload / "physics.zip") as bank:
        meta = json.loads(body.read("meta.json"))
        index = json.loads(bank.read("index.json"))
        heights = json.loads(bank.read("heights.json"))
        assert body.testzip() is None
        assert set(references(meta)) <= set(body.namelist())
        assert {group["Page"] for group in meta["Groups"]} == {0, 1, 2}
        areas = index["areas"]
        paths = [f"chara/human/c{r}/skeleton/base/b0001/phy_c{r}b0001.phyb" for r in index["races"]]
        defaults = tuple(area["defaultIndex"] for area in areas)
        used = set()
        count = 0
        for picks in itertools.product(*(range(len(area["values"])) for area in areas)):
            row = index["rows"]["".join(str(v) for i, v in enumerate(picks) if i != 1)]
            assert len(row) == len(paths) == 8
            assets = [heights[source][picks[1]] for source in row]
            used.update(assets)
            if picks == defaults:
                for path, asset in zip(paths, assets):
                    assert body.read(meta["DefaultData"]["Files"][path]) == bank.read(f"bank/{asset}.phyb")
            count += 1
        for asset in used:
            assert hashlib.sha256(bank.read(f"bank/{asset}.phyb")).hexdigest()[:24] == asset
        assert count == 3456
    binaries = project / "src/bin/Release/net10.0-windows"
    output.parent.mkdir(parents=True, exist_ok=True)
    with ZipFile(output, "w", ZIP_DEFLATED, compresslevel=1) as archive:
        for name in ("Aetherform.dll", "AetherglassUI.dll", "Aetherform.json", "Aetherform.deps.json"):
            archive.write(binaries / name, name)
        for name in ("Aetherform.pmp", "physics.zip"):
            archive.write(payload / name, name)
    with ZipFile(output) as archive:
        assert archive.testzip() is None
    print(f"Verified {count} selections for eight races and {len(used)} unique physics files. Created {output}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("output", type=Path)
    package(Path(__file__).resolve().parents[1], parser.parse_args().output)
