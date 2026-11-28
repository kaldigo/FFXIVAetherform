# AF fitting surface: breast curvature continuation

28 November 2026. Start the AF fitting copy from standard Small chest (`af_top_01`) and Medium B legs (`af_dwn_04`), with Hip Dips and Soft Butt off. The first preparation removes nipple projection by continuing the surrounding breast slope and curvature through that region. It is ready for visual review, recorded in `af-fitting-mesh.json`.

This is the fitting copy. The original AF source assets and their movement data remain unchanged. The vanilla upscale is a static fitting reference; animating or rebuilding it in posed states is not a requirement for this pipeline. No further pose reconstruction is part of this preparation.

## Surface treatment

Each breast uses its own outward tangent frame, estimated from the surrounding breast. An area-weighted robust cubic graph fits the surface in a 14–26 mm collar around the nipple. The central nipple vertices do not supply samples to that fit. The resulting model supplies the slope and curvature beneath the removed feature.

The inner 9 mm follows this curved surface. From 9 to 18 mm, a quintic falloff joins the displacement to zero with matching first and second derivatives. Outside the 18 mm support, every position stays exact. All original tangent-plane coordinates remain fixed: this treatment needs no face removal, triangulation, planar cap, vertex redistribution or general breast smoothing.

The fitted central graph curvatures are approximately -0.02845 and -0.02599 per mm on both breasts. Both directions retain curvature through the centre. The collar fit residual is 0.1505 mm RMS. The profile review cuts the actual exported triangular surfaces through identical planes; it does not merely plot the fitted polynomial.

## Result and verification

- 12,289 vertices and 24,290 triangles, with the original ordered vertex and face identities.
- 1,032 moved vertices, 516 per breast. Maximum movement is 4.400 mm inward along the local breast direction; the largest outward adjustment is 0.0102 mm.
- Cleavage, under-breast region and legs stay exact. UVs and named bone weights retain the original per-corner values.
- Geometry normals are recalculated. Original authored normals are retained separately in the NPZ. Review images use geometric shading, with no normal map or nipple texture applied.
- No reversed edited triangles or local nonadjacent intersections. Minimum affected projected area ratio is 0.4155. The original 47 global intersection pairs outside the edited region remain exactly the same.
- Connectivity stays exact, including its original boundary structure: zero nonmanifold edges, zero inconsistent two-face windings and five boundary junction vertices. This preparation does not alter unrelated original boundaries.
- All 948 AF point names and triangle/barycentric addresses remain exact. Their positions are evaluated on the fitting copy; maximum movement is 0.8013 mm.

Seven focused tests pass, covering native AF correspondence, the underlying reference operations and two curvature-continuation tests. The new tests recover a known curved surface beneath a synthetic nipple bump and verify that changing the discarded inner feature does not change the fitted replacement.

Follow-up normal verification: `audit_af_fitting_normals.py` independently recalculates corner normals using atan2 angle weights. The saved NPZ matches within 1.51e-15 vector error; serialized OBJ normals match within 0.000000257 degrees. Normals across each inner 6 mm region vary by 18.84 degrees. The fresh export has the same surface and shading; any remaining flat-looking transition is not caused by stale normals. Geometry and the authoritative fitting manifest remain unchanged. `AF-Fitting-Fresh-Normals.obj` and `af-fitting-normal-verification.png` provide a fresh export and saved/fresh/flat-normal views.

## Files and reproduction

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
C:/Python310/python.exe prepare_af_fitting_chest.py
C:/Python310/python.exe review_af_fitting_chest.py
C:/Python310/python.exe -m unittest test_af_fitting_chest test_af_comparison test_reference_surface -v
```

Use the existing Python/SciPy dependencies in `requirements.txt` and the verified local AF snapshots. No installed game or AF asset is written. Lower-body fitting-envelope preparation remains a separate next step.
