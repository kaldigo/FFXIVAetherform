# Larger folds and recessed-line repair

Local experiment, 28 November 2026. The accepted vanilla reference, first cleanup, AF meshes and accepted landmark selections remain unchanged. The new candidate is `outputs/vanilla-repair-v1/vanilla-repaired.obj`, with its matching `Vanilla-Repaired.points.json`. The NPZ retains skinning and provenance; OBJ does not carry bone weights.

## Findings

The butt and front/inner-thigh marks include real geometric creases. They are visible with textures disabled. The reference currently reconstructs curvature from authored mesh vertex normals, **not a sampled normal-map texture**, so these particular artifacts cannot be attributed to fabric texture alone. Cubic PN patches share positions along edges but do not guarantee continuous surface direction there. Native skin/smallclothes joins and folds in the source envelope also remain relevant.

Three generated lower flap-root caps were especially bad: face-normal jumps reached 177–179 degrees at the hips and 108 degrees at the rear. These are artifacts of our flap-removal construction, not evidence of the intended body shape. The previous per-native-face orientation checks were insufficient to identify them: a cap can retain its own triangle orientation while doubling back against its neighbours.

The first cleanup protected most exposed skin and could not reach several reported lines. This experiment releases the local pelvis and widens the skin transition around smallclothes to 24 mm. It keeps the medial cleavage and broad cloth extrema fixed.

## Candidate construction

1. Replace each lower cap and one ring of native neighbours with a constrained triangulated disk. The initial replacement preserves every dense outer boundary position exactly and introduces no boundary edge splits. A quadratic fitted to the exterior collar supplies broad curvature; a harmonic boundary correction joins it to the retained surface. New patch faces must point outward in their local chart.
2. Compare geodesic quadratic fits at radii 35, 55 and 75 mm. Each fit allows at most 4 mm movement from the remeshed surface. Choose 75 mm for its lowest measured normal-detail residual. This is a bounded trial, not an automatic fabric/anatomy classifier.
3. Apply an additional 90-cycle Taubin relaxation only in the seat/crotch region, limited to 2 mm per vertex. Both stages reject reversed, severely collapsed or excessively enlarged triangles. The shared patch boundaries may move during these coherent fairing stages; only the initial disk replacement boundary is position-exact. The two movement limits accumulate and do not bound the initial cap reconstruction.
4. Transfer new patch UVs and named bone weights from exterior source witnesses, excluding inward root walls and generated caps. Retained faces keep their UVs and weights exactly. Geometry-derived normals are recomputed throughout the candidate.

The new topology has 15,408 vertices and 30,725 triangles, compared with 15,383 and 30,675 previously. Of the old faces, 30,517 survive unchanged in connectivity and attributes. Local topology changes are confined to the three lower disks. Smoothing can move surrounding geometry.

## Results and limits

| Measurement | Before | Candidate |
| --- | ---: | ---: |
| Left hip repair maximum face-normal jump | 177.1° | 18.1° |
| Right hip repair maximum face-normal jump | 178.9° | 28.6° |
| Rear repair maximum face-normal jump | 108.0° | 6.8° |
| Butt-cheek region 95th-percentile jump | 17.7° | 14.7° |
| Front/inner-thigh region 95th-percentile jump | 22.8° | 20.0° |
| Movable-region normal-detail RMS | 7.02° | 6.07° |

Cap comparisons use the corresponding old and new patch regions. The other metrics compare the remeshed first-cleanup control with the final candidate; they use fixed spatial masks and are diagnostic measures rather than anatomical accuracy scores. Butt/inner-thigh maximum jumps remain approximately 52°/74°. Larger folds and narrow lines are reduced, not eliminated. The comparison PNG uses matched cameras, flat geometry shading and no textures, so remaining facets and recesses are visible.

Protected vertices have zero movement. The sampled cleavage centreline has zero depth change from the original upscale. The body's bounding-box coordinates change by at most 1.055 mm, which does not certify unchanged local body shape. Sampled triangle-centre distance from the first cleanup is 0.716 mm at the 95th percentile and 4.715 mm maximum. Broad underbust profiles can change by up to about 7 mm relative to the original upscale; the medial dip is preserved, but the whole chest is not position-fixed.

The audit finds no nonmanifold edges, inconsistent windings, degenerate elements or cumulative element reversals relative to the remeshed control. The original five neck/wrist/ankle openings remain. Weights are nonnegative and normalized within floating-point precision. This does not establish animation/physics quality or prove absence of global self-intersections. Covered regions remain a smoothed smallclothes envelope, not a reconstruction of hidden naked anatomy. No 2B fitting or AF wrap is performed.

## Landmarks and provenance

All 948 named pairs are retained with the AF side unchanged. Surviving source triangles retain their barycentric point addresses under the new face numbering. Fourteen points in replaced patches are rebound through each patch's original 2D chart and then follow the final fairing. Their identities are preserved, but the chart projection is an approximation of the repaired surface correspondence. Maximum point movement from the first cleanup is 3.026 mm, and its 95th percentile is 1.780 mm.

New triangles can cross old native faces. The repaired NPZ therefore uses per-corner `cornerNativeFace` and `cornerNativeBary` witnesses rather than claiming one native parent for every triangle. `originalDenseFace` and `originalVertex` identify surviving elements; -1 denotes newly constructed elements. Obsolete single-parent landmark lineage is removed from the candidate pair file. Old tools that require `sourceFace`/`sourceBarycentric` mesh lineage need adapting before they can consume this candidate.

## Reproduce

Install `requirements.txt`, including the `triangle` package. Generate the original and blended cleanup using the earlier notes, then run from this folder:

```powershell
C:/Python310/python.exe repair_surface.py `
  --reference outputs/vanilla-cleanup-blended-v1/vanilla-cleaned-reference.npz `
  --output outputs/vanilla-repair-v1
C:/Python310/python.exe polish_repair.py `
  --remeshed outputs/vanilla-repair-v1/remeshed-reference.npz `
  --previous outputs/vanilla-cleanup-blended-v1/vanilla-cleaned-reference.npz `
  --original outputs/vanilla-reference-v1/vanilla-reference.npz `
  --patch-report outputs/vanilla-repair-v1/repair-report.json `
  --landmarks outputs/vanilla-cleanup-blended-v1/paired-landmarks-candidate.npz `
  --output outputs/vanilla-repair-v1
C:/Python310/python.exe audit_repair.py `
  --folder outputs/vanilla-repair-v1 `
  --previous outputs/vanilla-cleanup-blended-v1/vanilla-cleaned-reference.npz `
  --original outputs/vanilla-reference-v1/vanilla-reference.npz `
  --landmarks outputs/vanilla-cleanup-blended-v1/paired-landmarks-candidate.npz
C:/Python310/python.exe render_repair.py `
  --original outputs/vanilla-reference-v1/vanilla-reference.npz `
  --previous outputs/vanilla-cleanup-blended-v1/vanilla-cleaned-reference.npz `
  --repaired outputs/vanilla-repair-v1/vanilla-repaired-reference.npz `
  --output outputs/vanilla-repair-v1/repair-comparison.png
C:/Python310/python.exe -m unittest -v
```

All 12 tests pass, including boundary-preserving quadratic reconstruction, winding rejection and landmark rebinding after deformation. The data audit verifies retained UVs/weights, normalized new weights, valid native witnesses, fixed protected positions and unchanged AF points. Input hashes match the earlier recorded original and cleanup hashes. Local generated assets remain ignored by Git; the branch contains code and findings only.
