import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

import custom_fits


class CatalogTests(unittest.TestCase):
    def test_authoring_and_checksum_validation(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, mesh = root / "source.mdl", root / "edited.mdl"
            source.write_bytes(b"source reference")
            mesh.write_bytes(b"changed topology")
            catalog = root / "catalog"
            subprocess.run([sys.executable, str(Path(custom_fits.__file__)), "new", str(catalog),
                            "--id", "coat", "--kind", "family", "--source", str(source), "--mesh", str(mesh),
                            "--game-path", "chara/equipment/e0717/model/c0201e0717_top.mdl"], check=True)
            custom_fits.validate(catalog)
            self.assertFalse((catalog / "source.mdl").exists())
            recipe = json.loads((catalog / "recipes/coat.json").read_text())
            self.assertEqual(recipe["SourceHash"], custom_fits.digest(source.read_bytes()))
            asset = catalog / recipe["Default"]["Mesh"]["Path"]
            asset.write_bytes(b"corrupted")
            with self.assertRaises(AssertionError):
                custom_fits.validate(catalog)

    def test_finished_meshes_are_hashed_and_unambiguous(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "recipes").mkdir()
            (root / "meshes").mkdir()
            (root / "meshes/fit.mdl").write_bytes(b"finished size mesh")
            fit = dict(Chest="XS", Legs="Large", LegOptions=["Hip Dips", "Soft Butt"],
                       Targets={"_top": ["a" * 64], "_dwn": ["b" * 64]},
                       Mesh=dict(Path="meshes/fit.mdl", Sha256=custom_fits.digest(b"finished size mesh")))
            recipe = dict(Schema=2, Id="fit", GamePath="chara/equipment/e6092/model/c0201e6092_top.mdl",
                          SourceKind="vanilla", SourceHash="c" * 64,
                          Default=dict(Id="default", Name="Draped", Baked=[fit]))
            path = root / "recipes/fit.json"
            path.write_text(json.dumps(recipe))
            custom_fits.validate(root, build=True)
            custom_fits.validate(root)
            recipe["Default"]["Baked"].append(fit)
            path.write_text(json.dumps(recipe))
            with self.assertRaises(AssertionError):
                custom_fits.validate(root, build=True)
            recipe["Default"]["Baked"].pop()
            path.write_text(json.dumps(recipe))
            (root / "meshes/fit.mdl").write_bytes(b"corrupt")
            with self.assertRaises(AssertionError):
                custom_fits.validate(root, build=True)

    def test_paths_and_stale_index(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for path in ("../escape", "/absolute", "assets/../../escape", "assets\\escape", "https://example.com/a"):
                with self.assertRaises(AssertionError):
                    custom_fits.asset_path(root, path)
            (root / "index.json").write_text('{"Schema":1,"Entries":[{}]}')
            with self.assertRaises(AssertionError):
                custom_fits.validate(root)


if __name__ == "__main__":
    unittest.main()
