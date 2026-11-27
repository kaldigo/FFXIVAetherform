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
                Changelog="Corrects vanilla clothing motion-weight conversion around covered breast and hip areas. Source motion samples now include the stock bra and briefs instead of projecting onto distant exposed skin, preventing false local weight peaks. Conversion remains enabled and fitted neutral geometry is unchanged. Managed vanilla top and leg fits regenerate as Motion Fit 5; manual priority overrides remain protected.")
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
