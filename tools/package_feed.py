"""Build the plugin-only Dalamud release. Body data stays in pluginConfigs."""
from solver_package import add_solver
import hashlib
import json
import sys
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

tag = sys.argv[1]
root = Path(__file__).resolve().parents[1]
out = root / "artifacts"
out.mkdir(exist_ok=True)
build = root / "src/bin/Release/net10.0-windows"
manifest = json.loads((build / "Aetherform.json").read_text(encoding="utf-8"))
repo = manifest["RepoUrl"]
url = f"{repo}/releases/download/{tag}/Aetherform.zip"
manifest.update(AssemblyVersion=tag.removeprefix("v"),
                IconUrl=f"https://raw.githubusercontent.com/kaldigo/FFXIVAetherform/{tag}/assets/icon.png",
                DownloadLinkInstall=url, DownloadLinkUpdate=url, DownloadLinkTesting=url,
                Changelog="New left-facing waist-up silhouette for the installer and window header. Includes persistent, Proteus-editable garment fits.")
package = out / "Aetherform.zip"
with ZipFile(package, "w", ZIP_DEFLATED) as archive:
    add_solver(archive, root, build)
    archive.writestr("Aetherform.json", json.dumps(manifest))
    archive.write(root / "assets/icon.png", "images/icon.png")
    for name in ("Aetherform.dll", "Aetherform.deps.json", "AetherglassUI.dll"):
        archive.write(build / name, name)
with ZipFile(package) as archive:
    assert archive.testzip() is None
(out / "pluginmaster.json").write_text(json.dumps([manifest], indent=2) + "\n", encoding="utf-8")
(out / "SHA256SUMS.txt").write_text(f"{hashlib.sha256(package.read_bytes()).hexdigest()}  Aetherform.zip\n")
