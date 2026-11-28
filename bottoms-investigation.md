# Smallclothes and vanilla bottoms comparison

28 November 2026. Native female Midlander bind-pose comparison for the AF fitting reference. The body reference and its 948 landmarks remain unchanged.

Black Summer Tanga is the closer comparison to the standard smallclothes. No.2 Type B Leggings also shares local polygon layouts, but its source geometry has substantially different seat and crotch shaping. Treat those 2B differences as a garment-specific source shape rather than a requirement to reshape the common vanilla reference to match 2B.

## Native models and coverage

The item census identifies Black Summer Tanga as item 6105, set 49, variant 6, material variant 6, IMC mask 1023. Its female Midlander model is `chara/equipment/e0049/model/c0201e0049_dwn.mdl`. The Summer Tanga colours share this model. The source hash matches the independently recorded native-game census: `819a6f39fd1a219c204cb4bed159dcb8600ce4a8c907685498d0d366c2d1531c`.

| Model | Skin triangles | Main briefs triangles used | Additional cloth |
| --- | ---: | ---: | --- |
| Stock smallclothes reference | 474 | 98 plus 8 generated flap-root cap triangles | Previously reviewed flaps and detached details excluded |
| Summer Tanga | 522 | 164 | 184 attribute-tagged auxiliary cloth triangles excluded |
| No.2 Type B Leggings | 400 | 116 | 144 thigh-band triangles and a separate 420-triangle stocking mesh excluded from the central briefs comparison |

The tanga uses `/mt_c0201b0001_a.mtrl` for its skin and `/mt_c0201e0049_dwn_a.mtrl` for the clothing. The main cloth selection is its unconditional first submesh. Its skin mesh includes the legs; the 2B skin mesh ends at the upper thighs above its stockings. Full mesh counts therefore do not establish whether their overlapping pelvic layouts correspond.

2B is item 28712, set 649, variant 1, IMC mask 1023. Its native hash also matches the game census: `270ef5fe16f789bfe478cf9df5c4f894733673bb3fe696ea15cbf7acff1efb13`. The difference is present in the native model, not introduced by an earlier refit. These comparisons use default geometry with no shape replacements or runtime bone transforms applied.

## Shared local topology

The comparison follows geometric connections across skin and cloth material boundaries. It uses the existing cleaned stock reference before density refinement, and native equipment triangles. Coincident positions shared with the target are reserved as fixed correspondence anchors, preventing a topologically regular pattern from sliding onto the wrong row.

| Comparison | Unchanged shared vertices above Y 810 mm | Upper front matched patch | Crotch matched patch |
| --- | ---: | --- | --- |
| Stock to Summer Tanga | 48 | 41 vertices and 48 triangles | 44 vertices and 48 triangles |
| Stock to 2B | 12 at the waist | 14 vertices and 14 triangles | 13 vertices and 12 triangles |

Each patch has an injective vertex assignment, matching edges between all mapped vertices, and matching triangle complexes. Anatomical seeds, the midline, left/right side, a 45 mm displacement bound and the fixed shared-position anchors constrain propagation. The upper-front and crotch maps are independently seeded. Their overlapping vertex assignments do not conflict in these runs; that does not certify a complete combined map.

These are local candidate correspondences, not maximal matches, a proof of common authoring history, or complete body registrations. Propagation flags additional/missing edges, cut transitions and proposed changes that would violate anchors. Such areas require explicit local mapping or common refinement before a fitting morph uses them. Different coverage and additional topology do not by themselves invalidate shared-template fitting.

## Shape differences

Measurements are native millimetres, Y up. At a rear section, more negative Z means farther back. They are direct triangle intersections without texture-normal shading.

| Measurement | Stock | Summer Tanga | 2B |
| --- | ---: | ---: | ---: |
| Lowest main briefs Y | 853.147 | 853.111 | 860.422 |
| Exposed rear skin Z at X 60, Y 860 | -71.761 | -70.628 | -95.822 |

The tanga's lowest crotch point differs from stock by only **0.036 mm**. The 2B point is **7.275 mm higher**. The tanga's exposed rear skin at the sampled section is **1.133 mm forward** of stock, while 2B is **24.061 mm farther back**. All three samples hit actual skin, not underwear. This is a fixed spatial section comparison, not a claim that those ray hits are identical material points.

The crotch topology seed gives another useful local comparison. A stock side vertex at approximately `(17.085, 856.138, 41.616)` mm corresponds in the inspected tanga seed to `(17.619, 855.868, 40.910)` mm. In the 2B seed the side vertex is `(6.690, 862.669, 31.869)` mm. Thus the corresponding seed row is much narrower and higher in 2B. This does not imply one uniform width or displacement for the whole crotch.

The tanga still changes coverage, seam shape and nearby positions. For example, at rear X 40, Y 880, its cloth is around Z -85.797 mm compared with stock cloth at -84.149 mm. These are local garment-envelope differences, much smaller at that sample than the 2B skin difference, and should not be confused with a full naked-body reconstruction.

## Work needed for the AF fitting mesh

1. **Use the stock and tanga observations to establish the common vanilla lower-body reference.** The tanga supports a close local correspondence and comparable overall source shape. Review the remaining seams and split/extra edges explicitly rather than treating every material boundary as a different body region.
2. **Preserve AF body structure through the wrap.** Retain the inner-leg separation, under-seat transition and rear centre structure. Decide how cloth bridges or follows these regions through its classification. A broad underwear envelope does not establish the hidden skin shape beneath it.
3. **Keep observed skin and coverage envelopes identified.** The tanga exposes useful additional skin, but its central front and rear are still covered. It does not by itself establish every hidden anatomical surface.
4. **Map 2B from its actual source shape.** Its raised, narrower crotch and fuller seat are significant source-shape differences. A shared topology map can describe that change, but fitting must not assume it started on the unmodified smallclothes envelope. Existing body and garment weights must be included when validating the conversion.
5. **Review lower-body landmarks after choosing any reference changes.** Add or revise source correspondence only after the source surface and local transitions are agreed. The present inspection does not move the reference, modify accepted landmarks or run a wrap.

The same principle already agreed for the chest applies here: the AF fitting mesh retains its cleavage recess. Cloth spanning a body recess is a separate garment behaviour, not a reason to remove the body feature.

## Reproduce

The 2B inspection:

```powershell
C:/Python310/python.exe investigate_bottoms.py `
  --stock '../.work/uv-stock-lines-20261119/decoded/stock-dwn.json' `
  --bottoms '../.work/resolver-representation-20261118/assets/c0201e0649_dwn.json' `
  --reference outputs/vanilla-reference-v1/vanilla-reference.npz `
  --output outputs/two-b-bottoms-investigation
```

The tanga snapshot was decoded losslessly from the already extracted native MDL using the existing `AssetBridge` in the adjacent plugin checkout. Its local receipt is `outputs/tanga-investigation/decoded/bridge-report.json`. The input MDL is `.work/garment-source/race-audit/mdl/c0201e0049_dwn.mdl` in the shared project root. No game assets are committed to this branch.

With that local snapshot available:

```powershell
C:/Python310/python.exe investigate_tanga.py `
  --reference outputs/vanilla-reference-v1/vanilla-reference.npz `
  --stock '../.work/uv-stock-lines-20261119/decoded/stock-dwn.json' `
  --tanga outputs/tanga-investigation/decoded/c0201e0049_dwn.json `
  --two-b '../.work/resolver-representation-20261118/assets/c0201e0649_dwn.json' `
  --items '../.work/garment-source/race-audit/items.json' `
  --census '../.work/garment-source/census/models.json' `
  --output outputs/tanga-investigation
```

Both output folders contain a numerical report, depth profiles, front/back/side coverage renders, and numbered local-topology comparisons. Body-weight behaviour, animation, physics and a continuous global map remain to be validated.
