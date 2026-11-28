# Actual normal-map reconstruction

Follow-up: [body-specific flap-root cleanup and cloth-relief filtering](body-cleanup-notes.md). That stage rebuilds the six provisional closures described below and selects blurred cloth normals after measuring shape changes. The raw-normal reconstruction remains the comparison control.

28 November 2026. Reconstruct from the actual vanilla normal textures and compare with normal-mapped vanilla before smoothing or fabric-fold removal. The user's clarified resolution policy is **1x AF density for mapping; 2x or 4x AF density for wrapping**. The much denser diagnostic is a control only.

## Current outputs

| Role | Folder under `outputs/` | Vertices | Triangles | Reconstructed AF density, top / legs |
| --- | --- | ---: | ---: | ---: |
| Mapping, 1x | `normal-map-mapping-1x` | 12,070 | 24,054 | 1.020 / 1.023 |
| Wrap, 2x | `normal-map-wrap-2x` | 23,721 | 47,336 | 2.002 / 2.009 |
| Wrap, 4x | `normal-map-wrap-4x` | 48,129 | 96,080 | 4.057 / 4.075 |

Each folder contains `vanilla-normal-map.obj`, `normal-map-reference.npz`, `Vanilla-NormalMap.points.json`, `paired-landmarks-candidate.npz`, density/solver reports and an audit. The 4x folder also contains `density-comparison.png`: vanilla with the actual base normal textures, then the 1x mapping, 2x wrap and 4x wrap geometry under matching lighting. The rebuilt columns have **no normal map applied**. Their smooth shading uses normals calculated from their own geometry. `normal-map-comparison.png` separately exposes the 4x geometry with flat face normals as well as smooth vertex normals.

Density means triangles per physical surface area, measured separately against the AF Small top and Medium B legs. It is not a multiplier of edge length, an exact copied triangle count, or a match to AF connectivity. The source body has a different area from AF. Edge-length refinement is calibrated to the target count per slot, then the final density is measured again after reconstruction. The final factors are within about 2.4% of the requested values.

## Inputs and decoding

`normal-assets/NormalAssets.csproj` reads installed game assets through Lumina and parses the materials through Penumbra.GameData. It identifies normal textures by sampler ID `0x0C5EC1F1`, rather than guessing from a filename. Extracted originals remain local and ignored by Git.

| Material region | Shader | Actual base normal texture |
| --- | --- | --- |
| Body skin | `skin.shpk` | `chara/human/c0201/obj/body/b0001/texture/c0201b0001_norm.tex` |
| Stock top smallclothes | `character.shpk` | `chara/equipment/e0000/texture/v01_c0201e0000_top_norm.tex` |
| Stock lower smallclothes | `character.shpk` | `chara/equipment/e0000/texture/v01_c0201e0000_dwn_norm.tex` |

The extractor records texture/material hashes and verifies that the installed stock model hashes match the existing inspection snapshots. Textures are decoded at mip 0 through Lumina to BGRA8, reordered to linear RG, and bilinearly sampled through the original UVs. No colour-space gamma conversion or fabric filter is applied.

The installed DXBC shaders were locally disassembled with `shader_probe.py` to check the convention. Red/green are tangent-plane normal coordinates; blue/alpha carry other material information. In the base-normal path, RG is centred at 0.5 and Z is reconstructed as `sqrt(max(0.25 - dot(RG-0.5, RG-0.5), 0.000001))`. The current material normal scales are 1.0.

The normal direction is transformed through the MDL's stored frame. Usage 6 contains the packed binormal and handedness. After decoding, the tangent is `normalize(cross(B, N)) * sign(w)`. The native shader normalises the interpolated N/T/B directions before combining the sampled normal. This convention matters on mirrored UV islands; a guessed cross-product order or green-channel sign would change the relief direction.

This comparison covers the **actual base normal textures with neutral customization**. It does not reproduce tile-array detail blending, the entire material/colour-table pipeline, or the game's PBR lighting. It therefore is not a claim of pixel-identical rendering in the game.

## Reconstruction

The build starts from retained **native flat geometry**, not the old PN surface, the fabric-cleanup candidate or the local repair experiments. Native barycentric coordinates carry UVs, named bone weights and point identity through each subdivision level.

The new refinement limits physical edge length and chooses the shorter diagonal when dividing a two-edge split. The old area-only rule could leave stretched triangles spanning normal changes. Calibrating the new refinement to AF density improves allocation without silently treating a 400,000-triangle diagnostic as a 2x/4x reference.

The solver jointly fits normal-direction displacements on the shared mesh. Its data term penalises reconstructed triangle edges that are not perpendicular to the combined native/texture normal field. An area-weighted positional prior supplies the otherwise missing depth reference. Neck, wrist and ankle join positions remain fixed. A signed-area barrier prevents retained native triangles folding back during the solve. Sparse Newton steps jointly update the surface; no post-solve smoothing, Taubin pass, crease brush, local resurfacing or fabric classifier runs.

The geometry still approximates an authored shading field. Normal vectors alone do not uniquely determine shape, and not every authored field is exactly integrable. Displacements from the flat source reach about 12.1 mm maximum and 5.3–5.5 mm at the 95th percentile for the wrap candidates. These are reconstruction changes, not claims of unchanged native positions or independently verified naked anatomy.

## Comparison and checks

The audit samples three held-out material positions per output triangle, distinct from the centroid used by the normal-fit equations. Caps without native evidence are excluded from normal-fit statistics. Area weighting makes comparisons across resolutions less sensitive to how many small faces occur in a particular region.

| Candidate | Held-out normal RMS | Area-weighted RMS | PN analytic area-weighted RMS at the same samples |
| --- | ---: | ---: | ---: |
| 1x mapping | 6.11° | 6.03° | 8.51° |
| 2x wrap | 5.20° | 5.22° | 8.62° |
| 4x wrap | 4.62° | 4.59° | 8.61° |

The 4x wrap candidate reproduces more of the mapped relief than the 2x candidate. Fine stitching and weave still exceed what these meshes can reproduce exactly. The comparison also separates remaining layout/resolution errors from normal-map decoding. This does not establish that AF's vertex count itself is intrinsically inadequate; different topology or adaptive allocation remains a separate possibility.

All three normal solves converge. The audit finds zero reversed retained-native triangles, finite geometry, no nonmanifold edges, consistent connectivity and the original five open neck/wrist/ankle loops. Source UVs and named bone weights are preserved by native barycentric interpolation; weights stay nonnegative and normalized. All 948 fitting-point identities retain their native material addresses. AF point addresses and positions are unchanged. Each point selection belongs to the OBJ in its own folder; selections cannot be interchanged across resolutions.

**The generated flap-root closures remain provisional.** They have no native normal-map samples and are excluded from the orientation barrier and normal-field accuracy claims. Respectively 16, 26 and 54 cap triangles reverse relative to their provisional flat controls at 1x, 2x and 4x. Those closures need independent construction/validation before any candidate is treated as a finished fitting baseline. Connectivity alone does not validate them. They have not been silently smoothed or declared corrected by the normal solve.

There is also no global self-intersection proof or animation/physics validation. Existing source references and accepted selections remain unchanged. No AF mesh is edited and no wrap is executed. The candidates share native-face/barycentric lineage, so the same material location can be addressed between the low mapping and higher wrap levels without guessing a nearest surface; automatic deformation transfer between levels is not implemented in this step.

Seventeen tests pass, covering existing reconstruction/correspondence checks plus RG decoding, texel-centre/repeat sampling, mirrored tangent handedness, edge-refinement lineage and recovery of unfiltered synthetic relief. The extractor also builds with zero warnings/errors.

## Reproduce

Requires the packages in `requirements.txt`, .NET 10, and the locally installed Lumina/Penumbra.GameData libraries referenced by the extractor project. Run from this repository folder; the stock inspection snapshots and original reference are the verified inputs described in the earlier notes.

```powershell
dotnet run --project normal-assets/NormalAssets.csproj -- `
  'D:/Games/SquareEnix/FINAL FANTASY XIV - A Realm Reborn/game/sqpack' `
  outputs/normal-map-assets-v1
C:/Python310/python.exe af_density_controls.py --factor 1 --output outputs/normal-map-mapping-1x
C:/Python310/python.exe af_density_controls.py --factor 2 --output outputs/normal-map-wrap-2x
C:/Python310/python.exe af_density_controls.py --factor 4 --output outputs/normal-map-wrap-4x
C:/Python310/python.exe reconstruct_normal_map.py `
  --control outputs/normal-map-mapping-1x/control.npz --output outputs/normal-map-mapping-1x
C:/Python310/python.exe reconstruct_normal_map.py `
  --control outputs/normal-map-wrap-2x/control.npz --output outputs/normal-map-wrap-2x
C:/Python310/python.exe reconstruct_normal_map.py `
  --control outputs/normal-map-wrap-4x/control.npz --output outputs/normal-map-wrap-4x
C:/Python310/python.exe audit_normal_map.py outputs/normal-map-mapping-1x
C:/Python310/python.exe audit_normal_map.py outputs/normal-map-wrap-2x
C:/Python310/python.exe audit_normal_map.py outputs/normal-map-wrap-4x
C:/Python310/python.exe render_normal_map.py outputs/normal-map-wrap-4x `
  --mapping outputs/normal-map-mapping-1x --wrap2 outputs/normal-map-wrap-2x
C:/Python310/python.exe -m unittest -v
```

`normal_tessellation.py` can separately build an unconstrained-density diagnostic with 5 mm skin and 2 mm cloth edge limits. The resulting `vanilla-normal-map-validation-v1` control has 416,958 triangles and is **not** one of the user's mapping/wrap resolution levels.
