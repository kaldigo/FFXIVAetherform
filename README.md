# Custom fit catalog

The `custom-fits` branch is a data-only catalog, separate from plugin releases.
The plugin checks its small `index.json` in the background at startup and at most
every ten minutes, using ETags. It fetches recipes/assets only for encountered
equipment (and models in a standalone source mod being converted). Cached fits
remain usable offline. Invalid downloads retain the last good recipe, including
across restarts. Downloads do not block an ordinary refit. Once a compatible
recipe arrives, only affected managed outputs regenerate. User overrides above
the automatic priority remain protected, including a priority change during a job.

## Two authoring formats

Each plan can contain vertex/weight rules, a complete replacement `.mdl`, or
both. A replacement is a **reference-space garment**, not a finished AF-size
output. Author it in the exact source model's pose, scale, body size and rig.
Its topology may change extensively. Preserve source material paths, option
attributes and equipment shape names; textures and other assets continue to
come from the original mod (or the game). Additional materials/textures and
arbitrary executable scripts are not part of this format.

Every recipe matches one exact source SHA-256, game model path and source kind
(`vanilla` or `family`). Race is part of the game path. A changed source needs a
new recipe/hash. For family default corrections the resolver first chooses the
closest size, then applies a matching recipe and fits the corrected reference
to AF. Explicit custom variants use their authored source size, then refit it;
they do not silently substitute another source for a sculpted reference mesh.

Rules address mesh-local vertex IDs **before subdivision**. `DeltaMm` is an
additive displacement in millimetres. Referenced shape vertices move with their
base vertices and shading is updated. `Weights` replaces a vertex's influences
using bones already in the reference mesh's palette; weights are normalized and
must fit its authored four/eight-influence format. Put rules after a replacement
mesh if their vertex IDs refer to its new topology.

`Default` is the automatically applied correction. `Variants` are complete
alternative plans against the original source, not patches applied after
`Default`. Each has a stable `Id` and a display `Name` (keep names stable too).
For vanilla fits they appear in an additional **Aetherform Custom Fits** tab;
its Automatic option uses the normal/default recipe. For standalone mods they
append to compatible source Single groups without moving original options or
tabs. Default-model/combining variants get additional groups on a custom tab;
existing combining containers and their flag indices remain unchanged. Option
selections survive regeneration. Retired vanilla variants keep their indices
but stop redirecting obsolete geometry.

A vanilla plan may also provide a `Classification` asset in the existing schema.
Its source hash must match the prepared model after reference edits. Without
that, an incompatible old classification is discarded by the standard resolver.

## Catalog layout

```
index.json
recipes/<stable-recipe-id>.json
meshes/<sha256>.mdl
classifications/<sha256>.json
```

An index entry contains `Id`, `GamePath`, `SourceKind`, `SourceHash`, `Recipe`
(relative path) and `Sha256` (recipe file checksum). `Schema` is currently 1.
Recipe and asset checksums pin each conversion to one immutable downloaded
snapshot. Unsupported schemas, ambiguous source entries and unsafe paths are
rejected. Downloads have size limits; partial updates never replace good data.

Example recipe (replace placeholder hashes with real checksums):

```json
{
  "Schema": 1,
  "Id": "example-coat-medium",
  "GamePath": "chara/equipment/e0717/model/c0201e0717_top.mdl",
  "SourceKind": "family",
  "SourceHash": "<64 lowercase hex characters>",
  "Default": {
    "Id": "default", "Name": "Corrected",
    "Edits": [{"Mesh": 1, "Vertices": [100, 101], "DeltaMm": [0, 0, 1]}]
  },
  "Variants": [{
    "Id": "open", "Name": "Open coat",
    "Mesh": {"Path": "meshes/<sha256>.mdl", "Sha256": "<mesh checksum>"},
    "Edits": []
  }]
}
```

## Authoring workflow

Check out the data branch in a separate directory. From the main code checkout:

```
python tools/custom_fits.py new <catalog> --id coat-medium --game-path chara/equipment/e0717/model/c0201e0717_top.mdl --kind family --source <original.mdl> --mesh <edited-reference.mdl>
python tools/custom_fits.py index <catalog>
python tools/custom_fits.py check <catalog>
```

Use `--rules edits.json` for a rules-only recipe, or edit the generated JSON to
add variants. The tool reads the source to calculate its hash; it does not copy
the unedited source into the catalog. Keep recipe and asset updates in the same
commit. The data branch CI verifies its index and checksums. Geometry should
also be checked against the local source using the headless regression harness:
set `customRecipe` and `customAssets` on a local fixture. Never put copyrighted
game source files in test fixtures or use placeholder examples as live entries.

## Finished size-specific fits (schema 2)

A vanilla plan can provide `Baked` entries containing `Chest` (XS, Small, Medium, Large), `Legs` (Medium, Large), exact `LegOptions`, a `Targets` map of `_top` / `_dwn` SHA-256 allowlists, and a checksum-pinned `Mesh` asset. These are finished AF meshes: the resolver selects and validates the matching reviewed result before calling the geometric solver. It never treats them as source reference meshes or fits them twice. Exact source model, native target files and shape flags must match; otherwise the normal reference/rule/automatic path remains available. Genital A/B/C labels do not create separate clothing sizes; explicitly reviewed native file hashes can share an entry. Buff or other unreviewed body/rig files do not match by label alone.

Recipe schema 2 requires Aetherform 0.4.0.37 or later. Schema 1 remains valid for rules and source-reference replacements. Finished meshes currently apply to vanilla item recipes; family source recipes retain the reference-space authoring workflow. The download/cache checks include all finished meshes. A matching item downloads its complete small size set once in the background, preserving offline size changes and retaining the previous verified snapshot if an update fails.

The first catalog entries are White Moonfire Happi and Summer Sunset Beach Cover-up: eight reviewed draped fits each, with Yiggle, Hip Dips and Soft Butt. The default uses the draped fit when compatible; an additional Original automatic fit option keeps the standard resolver available. Static comparison sheets used vanilla legs as a consistent reference; rear clearance was additionally sampled against matching native AF bottoms. Animation testing is separate.

## Approved review sheets

| Garment | Front | Rear | Angled | Rear angled |
|---|---|---|---|---|
| White Moonfire Happi | [View](previews/6092-front.png) | [View](previews/6092-back.png) | [View](previews/6092-three-quarter.png) | [View](previews/6092-rear-three-quarter.png) |
| Summer Sunset Beach Cover-up | [View](previews/6137-front.png) | [View](previews/6137-back.png) | [View](previews/6137-three-quarter.png) | [View](previews/6137-rear-three-quarter.png) |

## No.2 Type B Leggings: weighted review

The default now includes the current custom 2B leggings review for AF Medium and Large lower bodies, across the four supported chest selections. It is pinned to the original vanilla `c0201e0649_dwn.mdl`, exact native Yiggle body files, and Hip Dips + Soft Butt. Genital A/B/C hashes share the same covered fit. Existing manual overrides remain protected. Other body/shape settings keep the automatic fallback; Original automatic fit is available as an alternative.

The accepted outer thigh includes the extra 0.8 mm fullness. The inner contour is blended and the skin/panty boundary has matching geometry and weights. Skin uses eight influence lanes; clothing retains its four-influence shader format. Stocking boot-hiding attributes are preserved. The source equipment corrective is transferred to the new topology; sub-0.3 mm corrections are smoothly faded to fit the MDL shape-value budget. The reviewed default surface is unchanged by that compatibility work.

This is a **review**, not movement-cleared final geometry. Controlled leg swings and deeper sitting/crouching tests still report crotch contacts. Prescribed thigh/butt physics-bone sweeps do not replace live in-game physics validation. [Movement review](previews/0649-weighted-review.png). Static fit, normalized weights, matching contact weights, shader limits and source/target recipe selection are checked before publication.

The 2B review export was corrected after in-game missing-skin reports: the second skin draw now has a complete submesh bone map, including equipment-shape vertices. Regression checks reject the original broken export and accept both corrected sizes; default surface, normals, UVs and weights remain byte-identical. Live in-game confirmation is pending.
