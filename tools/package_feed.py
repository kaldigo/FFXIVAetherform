"""Build the plugin-only Dalamud release. Body data stays in pluginConfigs."""
from solver_package import add_solver
import hashlib
import json
import sys
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

tag = sys.argv[1]
root = Path(__file__).resolve().parents[1]
out = Path(sys.argv[3]).resolve() if len(sys.argv) > 3 else root / "artifacts"
out.mkdir(exist_ok=True)
build = Path(sys.argv[2]).resolve() if len(sys.argv) > 2 else root / "src/bin/Release/net10.0-windows"
manifest = json.loads((build / "Aetherform.json").read_text(encoding="utf-8"))
repo = manifest["RepoUrl"]
url = f"{repo}/releases/download/{tag}/Aetherform.zip"
manifest.update(AssemblyVersion=tag.removeprefix("v"),
                IconUrl=f"https://raw.githubusercontent.com/kaldigo/FFXIVAetherform/{tag}/assets/icon.png",
                DownloadLinkInstall=url, DownloadLinkUpdate=url, DownloadLinkTesting=url,
                Changelog="Fixes movement gaps between retained outfit skin and clothing, including the Summer Sunset Beach Cover-up bra. Retained skin now receives the same AF body-motion weight conversion as its clothing, preserving fitted neutral geometry, UVs and options. Automatic vanilla top and leg fits regenerate with the corrected weights; manual overrides remain protected.")
package = out / "Aetherform.zip"
with ZipFile(package, "w", ZIP_DEFLATED) as archive:
    add_solver(archive, root, build)
    archive.writestr("Aetherform.json", json.dumps(manifest))
    archive.write(root / "assets/icon.png", "images/icon.png")
    for name in ("Aetherform.dll", "Aetherform.deps.json", "AetherglassUI.dll"):
        archive.write(build / name, name)
with ZipFile(package) as archive:
    assert archive.testzip() is None
    assert not any(p.startswith("tools/") or p.endswith((".py", ".pyc")) for p in archive.namelist())
(out / "pluginmaster.json").write_text(json.dumps([manifest], indent=2) + "\n", encoding="utf-8")
(out / "SHA256SUMS.txt").write_text(f"{hashlib.sha256(package.read_bytes()).hexdigest()}  Aetherform.zip\n")
