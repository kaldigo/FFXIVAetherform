"""Add the pinned refit engine and its runtime dependencies to the plugin archive."""
from pathlib import Path


def add_solver(archive, root, build):
    lib = root / "out/solver/lib"
    archive.write(build / "Proteus.Refit.dll", "Proteus.Refit.dll")
    for path in sorted(lib.glob("*.dll")):
        archive.write(path, path.name)
    for name in ("LICENSE", "THIRD_PARTY_NOTICES.txt"):
        archive.write(lib / name, "licenses/Proteus/" + name)
    archive.write(root / "solver/NOTICE.md", "licenses/Proteus/SOURCE.md")
