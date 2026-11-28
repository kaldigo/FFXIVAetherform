# Vanilla chest geometry in the Velveteen Coatee

28 November 2026. Read-only investigation of native female Midlander geometry for the refit reference. The existing curved reference, point pairs and previous wrap inputs were not changed.

The Velveteen Coatee of Gathering supplies its own exposed chest skin inside its equipment model. The smallclothes top's missing breast skin is not being filled by a complete hidden body mesh. The Coatee includes the skin it needs, including a geometric crease between the breasts, and uses the shared vanilla body material to render that skin.

## Native inputs

The local item census identifies item 3092 as Velveteen Coatee of Gathering: equipment set 38, variant 6, material variant 6, IMC mask 977. Its female Midlander model is `chara/equipment/e0038/model/c0201e0038_top.mdl`.

| Mesh | Material | Native vertices | Native triangles | Triangles enabled by item variant |
| --- | --- | ---: | ---: | ---: |
| Clothing | `/mt_c0201e0038_top_a.mtrl` | 2375 | 1818 | 1200 |
| Exposed skin | `/mt_c0201b0001_a.mtrl` | 296 | 358 | 358 |

These skin counts cover the complete equipment skin mesh, not only its breast region. The sampled central chest triangles are in its unconditional first submesh. For example, the centre sample at Y 1250 mm lies on skin face 57; at Y 1240 mm it lies on skin face 76. These are zero-based faces within mesh 1, in the native default shape state. The model's declared shape replacements `shp_kat` and `shp_ude` address the clothing mesh, not the skin mesh.

The skin mesh has authored weights, including `j_mune_l` and `j_mune_r`. Of 242 inspected chest triangle-corner occurrences without an identical stock-skin vertex, 218 have nonzero breast-bone influence. This is garment-owned skinned geometry, rather than a texture-only substitute for missing triangles.

Verified source hashes:

- Coatee MDL: `6e68b70361433ca31df4c64d551532c9ec520bc9bddbbd156c840221a27f69b4`.
- Stock smallclothes top MDL: `2073b644bd3ebe2e21d84c98c622f9412e200642bad1310de4d20409355a2c6f`.

## The cleavage dip is present in the triangles

Measurements use native bind-space millimetres, Y up and positive Z toward the front chest. They intersect the actual triangles, without any normal texture or shading calculation. A back-surface ray hit does not count as front skin coverage.

At the horizontal section Y 1250 mm:

- The Coatee skin centre is at Z 95.506 mm.
- The skin 10 mm to the right is at Z 112.926 mm, approximately 17.421 mm farther forward. The left side is symmetric to measurement precision.
- None of these three positions is covered by the enabled clothing geometry in this front-depth test.
- Our curved vanilla reference centre is at Z 100.690 mm, approximately 5.184 mm farther forward than the Coatee skin.

At Y 1240 mm, the stock model has no front skin at X 0 or X 20 mm. The stock bra does cover those positions. Its centre is at Z 101.462 mm, while the Coatee skin centre is at Z 98.395 mm. At X 20 mm the bra is at Z 112.060 mm and the Coatee skin is at Z 119.019 mm. The bra and exposed skin therefore describe different cross-sectional shapes; matching only their centres would not fix the difference. Some Coatee skin at this lower section is covered by the clothing and is not claimed as visible skin evidence.

The Coatee skin connects into the upper chest where the stock top already has skin. At X 0, Y 1280 mm, their sampled depths differ by only about 0.046 mm. Thus the useful extra coverage is a local chest patch, not a reason to replace the entire vanilla torso with this garment's skin.

## Consequences for the wrapping reference

The current PN reconstruction follows the available source mesh normals. Over the breasts these are bra-envelope normals, so the reconstruction cannot establish the different exposed-skin cleavage shape by itself. The body-reference workflow needs to distinguish observed skin from the clothing envelope used where skin is absent.

The Coatee supplies a candidate observed-skin patch for the centre chest. A future reference update should use that patch where it supplies exposed skin, check its joins and weights against the surrounding stock skin, and retain explicitly identified coverage envelopes where anatomy remains unobserved. Other revealing garments can provide independent comparisons before choosing a common body reference. This Coatee does not expose a complete naked breast surface.

The AF side already has breast skin; the source correspondence should pair the appropriate breast and sternum landmarks rather than forcing the AF cleavage to follow a smooth bra bridge. The garment solver must still distinguish skin-following areas from fabric that deliberately spans the dip. A closed top should not acquire a cleavage groove merely because the reference body has one.

This investigation does not change the reference or its 948 landmarks. Updating a source patch would require rebinding affected controls to the revised source surface and reviewing their intended body locations before the AF-to-vanilla wrap.

## Reproduce and inspect

Run from this folder:

```powershell
C:/Python310/python.exe investigate_coatee.py `
  --stock '../.work/uv-stock-lines-20261119/decoded/stock-top.json' `
  --coatee '../.work/morph-ten-review-20261120/assets/c0201e0038_top.json' `
  --items '../.work/garment-source/race-audit/items.json' `
  --reference outputs/vanilla-reference-v1/vanilla-reference.npz `
  --output outputs/coatee-chest-investigation
```

The local output contains `chest-coverage.png`, `chest-depth-profiles.csv` and `report.json`. The comparison image shows stock skin alone, stock skin with the bra, the Coatee with its skin, and the isolated Coatee skin patch. The chest diagrams use triangle geometry; they do not claim an in-game lighting or physics reproduction. Normal textures, character breast-size bone transforms and runtime poses were not evaluated.
