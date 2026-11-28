# AF fitting surface: breast curvature continuation

28 November 2026. The user-accepted breast preparation is **v2**, based on standard Small chest (`af_top_01`) and Medium B legs (`af_dwn_04`), with Hip Dips and Soft Butt off. It revises the first preparation's flat-looking nipple-region transition. `af-fitting-mesh-chest-v2.json` retains this accepted stage; `af-fitting-mesh-v1.json` retains the first version. The subsequent [genital fitting pass](af-fitting-genital.md), identified by `af-fitting-mesh.json`, keeps the accepted breasts exact.

This is the fitting copy. The original AF source assets and their movement data remain unchanged. The vanilla upscale is a static fitting reference; animating or rebuilding it in posed states is not a requirement for this pipeline. No further pose reconstruction is part of this preparation.

## Current revision: surrounding-surface continuation

The first version's global cubic fit and radial blend left a shallow shoulder around the edited region. The revision uses a quadratic-reproducing thin-plate interpolant fitted to the actual surrounding breast samples, in the same 14–26 mm collar. This continues the collar's varying shape through the excluded nipple region. Only the very small collar interpolation residual blends to zero between 14 and 18 mm; the original nipple skirt is no longer part of that blend.

The revised centre is approximately 0.948 mm fuller along the local breast direction than v1, while removing up to 3.292 mm of original nipple projection. Its central graph curvatures are approximately -0.04066 and -0.02714 per mm, versus v1's -0.02845 and -0.02599. The stronger curvature through the centre follows the surrounding slope without restoring a nipple tip. The collar residual is approximately 0.000014 mm RMS.

The output retains all 12,289 vertices and 24,290 ordered faces, UVs, named weights and native vertex addresses. There are 1,022 moved vertices, 511 per breast. Positions outside the same local 18 mm support remain exact. Cleavage, under-breast region and legs remain exact. There are no reversed edited triangles or new local intersections; all 47 pre-existing global intersection pairs remain identical. All 948 point addresses remain exact, with at most 0.303 mm positional movement from the original AF surface.

Normals are recalculated from the revised geometry and independently checked in both the NPZ and OBJ. The matched review uses geometry-derived shading with no normal map. It compares **original AF, previous fitting copy, and revised fitting copy**, with the same cameras and lighting. Exact horizontal/vertical triangular-mesh sections show the revised curve against both controls.

Current artifacts are under `outputs/af-fitting-chest-v2/`, with the same mesh/point filenames listed below. The previous `outputs/af-fitting-chest-v1/` files remain intact. Seven focused tests pass; both continuation methods recover a known curved surface below a synthetic bump and exclude the discarded feature from the fitted shape.

```powershell
C:/Python310/python.exe prepare_af_fitting_chest.py --output outputs/af-fitting-chest-check --no-manifest
C:/Python310/python.exe review_af_fitting_chest.py outputs/af-fitting-chest-check
C:/Python310/python.exe audit_af_fitting_normals.py outputs/af-fitting-chest-check
C:/Python310/python.exe -m unittest test_af_fitting_chest test_af_comparison test_reference_surface -v
```

## Historical v1 preparation and verification

### Surface treatment

Each breast uses its own outward tangent frame, estimated from the surrounding breast. An area-weighted robust cubic graph fits the surface in a 14–26 mm collar around the nipple. The central nipple vertices do not supply samples to that fit. The resulting model supplies the slope and curvature beneath the removed feature.

The inner 9 mm follows this curved surface. From 9 to 18 mm, a quintic falloff joins the displacement to zero with matching first and second derivatives. Outside the 18 mm support, every position stays exact. All original tangent-plane coordinates remain fixed: this treatment needs no face removal, triangulation, planar cap, vertex redistribution or general breast smoothing.

The fitted central graph curvatures are approximately -0.02845 and -0.02599 per mm on both breasts. Both directions retain curvature through the centre. The collar fit residual is 0.1505 mm RMS. The profile review cuts the actual exported triangular surfaces through identical planes; it does not merely plot the fitted polynomial.

### Result and verification

- 12,289 vertices and 24,290 triangles, with the original ordered vertex and face identities.
- 1,032 moved vertices, 516 per breast. Maximum movement is 4.400 mm inward along the local breast direction; the largest outward adjustment is 0.0102 mm.
- Cleavage, under-breast region and legs stay exact. UVs and named bone weights retain the original per-corner values.
- Geometry normals are recalculated. Original authored normals are retained separately in the NPZ. Review images use geometric shading, with no normal map or nipple texture applied.
- No reversed edited triangles or local nonadjacent intersections. Minimum affected projected area ratio is 0.4155. The original 47 global intersection pairs outside the edited region remain exactly the same.
- Connectivity stays exact, including its original boundary structure: zero nonmanifold edges, zero inconsistent two-face windings and five boundary junction vertices. This preparation does not alter unrelated original boundaries.
- All 948 AF point names and triangle/barycentric addresses remain exact. Their positions are evaluated on the fitting copy; maximum movement is 0.8013 mm.

Seven focused tests pass, covering native AF correspondence, the underlying reference operations and two curvature-continuation tests. The new tests recover a known curved surface beneath a synthetic nipple bump and verify that changing the discarded inner feature does not change the fitted replacement.

Follow-up normal verification: `audit_af_fitting_normals.py` independently recalculates corner normals using atan2 angle weights. The saved NPZ matches within 1.51e-15 vector error; serialized OBJ normals match within 0.000000257 degrees. Normals across each inner 6 mm region vary by 18.84 degrees. The fresh export has the same surface and shading; any remaining flat-looking transition is not caused by stale normals. Geometry and the authoritative fitting manifest remain unchanged. `AF-Fitting-Fresh-Normals.obj` and `af-fitting-normal-verification.png` provide a fresh export and saved/fresh/flat-normal views.

### Files and reproduction

Generated geometry stays in ignored `outputs/af-fitting-chest-v1/`:

- `AF-Fitting-Small-MediumB.obj`: fitting copy, millimetres, with per-corner UVs and recomputed normals.
- `AF-Fitting.points.json`: matching named AF point addresses for that OBJ.
- `af-fitting-reference.npz`: current/source positions, original topology, UVs, named weights, normals and native indices.
- `af-fitting-landmarks.npz`: original and fitting-copy point coordinates.
- `AF-Original-Small-MediumB.obj`: matched original comparison.
- `af-fitting-chest-report.json`: input hashes, local frames, fit coefficients and validation.
- `af-fitting-chest-comparison.png`: front, profile and oblique before/after geometry views.
- `af-fitting-breast-profiles.png`: exact horizontal and vertical mesh sections, original orange and fitting copy green.

The tracked `af-fitting-mesh.json` identifies the candidate and hashes its mesh/data/point artifacts. It is separate from the vanilla `working-mesh.json`.

```powershell
C:/Python310/python.exe prepare_af_fitting_chest.py --method cubic-v1 --output outputs/af-fitting-chest-v1-check --no-manifest
C:/Python310/python.exe review_af_fitting_chest.py outputs/af-fitting-chest-v1-check
C:/Python310/python.exe -m unittest test_af_fitting_chest test_af_comparison test_reference_surface -v
```

Use the existing Python/SciPy dependencies in `requirements.txt` and the verified local AF snapshots. No installed game or AF asset is written. Lower-body fitting-envelope preparation remains a separate next step.
