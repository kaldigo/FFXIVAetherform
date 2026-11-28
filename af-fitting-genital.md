# AF lower fitting surface: genital pass

The user accepted breast preparation v2. Its mesh/data remain unchanged under `outputs/af-fitting-chest-v2/`, identified by `af-fitting-mesh-chest-v2.json`. This page records the initial genital unfolding, v3, retained in `af-fitting-mesh-genital-v3.json`. The subsequent [v4 revision](af-fitting-tight-cover.md) raised the low peak. The current `af-fitting-mesh.json` identifies [v5's smooth curve](af-fitting-smooth-hidden-detail.md), which removes the anterior bump by allowing the hidden skin detail to clip.

This starts the lower-body fitting surface. The rear cleft and anal detail are not changed in this pass. No external panties reference or constant thickness offset is used.

## Internal geometry inspection

The genital detail is part of the outer body's connected surface, rather than a detached internal shell. The selected region is a topological disk: 898 vertices, 1,744 existing faces, one 50-vertex boundary loop and Euler characteristic 1. There is no native open boundary inside this region.

It does contain inward folds and recessed geometry. In the local outward chart, 244 source faces turn inward or fold over. The intersection check also finds **30 existing intersecting triangle pairs** involving this region. Simply projecting all these vertices onto a smooth height surface would leave folded triangles; hiding or flattening a separate inner layer would not address the connectivity.

The preparation unfolds these existing vertices into a single outward-facing fitting sheet. No vertex or face is removed, added, welded or reordered. The three tests include rejecting a genuine hole, so the disk check cannot silently seal an opening that would require new topology.

## Surface construction

A local frame tilted 35 degrees follows the front-to-underbody direction. Positive cotangent coordinates, derived from the native 3D triangle angles, unfold the selected disk while holding two outer collar rings fixed. This preserves 136 collar vertices exactly and redistributes 762 interior vertices.

The donor strips lie on both sides of the slit, at absolute X 9–24 mm. Area-weighted moving quadratic fits use both their heights and slopes derived from geometric normals. A 14 mm longitudinal window smooths the estimates along the region. Buried centre detail does not supply donor samples. A biharmonic residual extension joins the estimated surface to the fixed collar, retaining the surrounding curved shape and slope.

This produces a curved skin-contact envelope across the genital detail, rather than following the recess or creating a planar cover. All original face connections remain. The accepted breast positions and normals, the butt/rear region and the thigh geometry below the groin stay exact.

## Results

- The complete copy still has **12,289 vertices and 24,290 ordered triangles**. Native vertex indices, UVs, bone names, weights and all original source positions are retained exactly.
- Only 762 genital-region vertices move. Largest movement is 22.057 mm while unfolding the inward detail; no broad seat or thigh reshaping is performed.
- The final edited vertices lie within X approximately -9.93 to 9.94 mm, Y 844.58–881.09 mm and Z -1.32–78.38 mm. Every original vertex below Y 840 mm remains exact, and no new edited vertex enters that lower thigh-gap region. The genital contact contour itself changes as intended.
- There are **zero final inward/folded chart faces and zero local intersections**. All 30 genital intersection pairs disappear, with no new pairs. The 17 remaining original contacts are at the existing waist (2) and knee (15) seams, outside this edit.
- Every final triangle has positive area. The smallest genital-region spatial area is 0.001491 mm², shortest edge 0.02788 mm and minimum angle 0.998 degrees. The dense topology formerly used for internal detail remains in the fitting sheet; no degenerate collapse is used to hide it.
- The butt curve, rear cleft and anal region are unchanged. All positions above Y 950 mm, including the accepted breasts, remain exact, as do the chest normals.
- All 948 named point identities and triangle/barycentric addresses remain exact. Positions follow the fitting surface; maximum movement from the accepted chest-stage copy is 3.295 mm.
- Normals are recalculated from geometry. An independent atan2-based calculation verifies the NPZ and serialized OBJ; maximum OBJ normal-angle discrepancy against the exported positions is below 0.000001 degrees. Review images have no texture normal map applied.

Ten focused tests pass. They check that a recessed/folded disk unfolds without topology or boundary changes, that a genuine hole is rejected, and that side heights/angles recover a known curved surface independently of deep centre detail. Existing AF correspondence and breast-continuation tests also pass.

## Review and reproduction

Generated assets remain ignored by Git in `outputs/af-fitting-genital-v3/`:

- `AF-Fitting-Small-MediumB.obj` and `AF-Fitting.points.json`: current copy and matching point file.
- `af-fitting-reference.npz`: current, accepted-parent and original-source positions; original topology and attributes; edit masks and chart provenance.
- `af-fitting-landmarks.npz`: updated fitting points and their original/parent coordinates.
- `af-fitting-genital-report.json` and `af-lower-independent-audit.json`: construction and independent checks.
- `genital-diagnostics.npz`: patch/collar identities, original/final projected areas and donor vertices.
- `af-genital-fitting-overview.png`: matched front and underside views before/after.
- `af-genital-fitting-comparison.png`: additional wide views and a half-body cutaway exposing the inward folds.
- `af-genital-envelope-sections.png`: exact along/across mesh sections, original orange and envelope green.

Cutaways omit the near-side faces for inspection. The model-part openings visible in the wider underside views are original AF boundaries; this edit adds no openings.

```powershell
C:/Python310/python.exe prepare_af_fitting_genital.py --output outputs/af-fitting-genital-check --no-manifest
C:/Python310/python.exe review_af_fitting_genital.py outputs/af-fitting-genital-check
C:/Python310/python.exe audit_af_lower_fit.py outputs/af-fitting-genital-check
C:/Python310/python.exe -m unittest test_af_fitting_genital test_af_fitting_chest test_af_comparison test_reference_surface -v
```

Use `--no-manifest` when building an experimental output without selecting it. The accepted chest parent is hash-verified and never written by this process. No AF source asset, vanilla reference, movement mesh or game installation is changed. Posterior fitting-envelope work remains separate.
