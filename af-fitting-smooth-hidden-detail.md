# Smooth genital curve with permitted hidden clipping

The user identified the small anterior bump introduced by v4's requirement to clear every original crest. They explicitly permitted that hidden skin detail to clip through the fitting surface. The current **v5** revision removes the local coverage requirement so the envelope can continue smoothly through the protrusion.

`af-fitting-mesh.json` identifies v5, under `outputs/af-fitting-genital-v5/`. The previous v4 mesh and `af-fitting-mesh-genital-v4.json` remain intact. The accepted breast stage and original AF movement mesh are unchanged.

## Change

The same unfolded chart, fixed collar, flatter transverse prior and curvature-fairing solve are retained. Original-surface coverage is relaxed only in a small anterior strip: local transverse X within ±4.5 mm and longitudinal S between 10 and 32 mm. The original detail in that covered region no longer forces a raised fitting bump. The curve is driven by its surrounding surface instead.

The largest change from v4 is **1.010 mm**. The permitted original-skin penetration reaches **0.896 mm along the local outward direction**. The observed clipping is narrower than the permitted strip: approximately X ±2.66 mm and S 12.28–20.13 mm. This is intentional clipping against the separate original skin, not a self-intersection in the fitting mesh.

The already corrected low point stays effectively unchanged at Y 847.954 mm. The revised centre-profile review shows the local kink removed while retaining the broader flat curve. No normal map is applied to the review; geometry normals are recalculated.

## Checks and retained data

- The 12,289 vertices, 24,290 ordered faces, UVs, named weights, native indices and point addresses remain unchanged.
- The accepted breasts and their normals, butt/rear geometry, fixed genital collar and thigh-gap region remain exact.
- Required original-shape coverage outside the exemption remains verified. The triangle-overlay audit additionally partitions along the exemption boundaries, so permitted clipping is not mistaken for full coverage or allowed to conceal uncovered required regions.
- There are no new local self-intersections. The 30 original genital self-intersection pairs remain removed; the 17 original waist/knee seam contacts are untouched.
- All 948 fitting points retain their names and triangle/barycentric addresses, with coordinates updated on the new surface.
- Independent geometry-normal and OBJ replay checks pass. Seventeen focused tests pass, including a test that exempting the hidden source peak allows a smooth surface instead of forcing a bump.

## Files and reproduction

Use the mesh and matching point file in `outputs/af-fitting-genital-v5/`:

- `AF-Fitting-Small-MediumB.obj` and `AF-Fitting.points.json`.
- `af-fitting-reference.npz`, `af-fitting-landmarks.npz`, `af-tight-cover-report.json` and `af-lower-independent-audit.json`.
- `af-genital-fitting-overview.png`: original, previous v4 and revised v5 views.
- `af-genital-centre-profile.png`: exact world-space centre sections, original orange, v4 blue and v5 green.
- `coverage-audit.npz` and `overlay-coverage-audit.npz`: required coverage and intentionally permitted hidden intersections.

```powershell
C:/Python310/python.exe tighten_af_genital.py --ignore-peak
C:/Python310/python.exe review_af_fitting_genital.py
C:/Python310/python.exe audit_af_lower_fit.py
C:/Python310/python.exe -m unittest test_af_tight_cover test_af_fitting_genital test_af_fitting_chest test_af_comparison test_reference_surface -v
```

With `--ignore-peak`, the default output is v5; the v4 comparison input is protected from overwrite. Review and audit tools follow the current fitting manifest by default, or accept an explicit folder. Rear cleft and anal preparation remain separate.
