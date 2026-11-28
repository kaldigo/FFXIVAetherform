# Flatter genital fitting envelope

The user found the first genital envelope too low and too rounded. The current **v4** revision raises and flattens that centre while only just covering the existing AF shape. `af-fitting-mesh.json` identifies this revision. `af-fitting-mesh-genital-v3.json` and `outputs/af-fitting-genital-v3/` retain the previous envelope for comparison.

The lowest edited point rises from **Y 844.582 mm to 847.954 mm**, about **3.371 mm higher**. The corresponding minimum on the original genital geometry is Y 848.198 mm. The revised surface therefore removes the former downward dome while still lying just outside the original shape. Its largest change from the previous envelope is 4.852 mm, measured over all edited vertices.

## Close coverage instead of an extrapolated dome

The unfolded chart and native AF topology from v3 remain fixed. The transverse prior is flattened: it follows the longitudinal curve and left/right height trend, without inventing central transverse curvature from the side slopes. A screened biharmonic fit joins that prior to the fixed outer collar.

Actual original geometry supplies lower coverage constraints. The solve checks 15,797 original surface samples, including vertices, edge subdivisions and triangle interiors. The nominal cover allowance is **0.1 mm along the local outward direction**, fading to zero at the fixed collar. It is a fitting tolerance, not an assumed garment thickness or a uniform body lift.

An additional centre limit prevents fairing from recreating the excessive dome. Within X ±5 mm, vertex heights stay within a target 0.35 mm of the original cross-section crest, measured from the original genital strip X ±6 mm. The achieved maximum excess is 0.350161 mm, including numerical tolerance. A linear feasibility check verifies that this limit and the coverage constraints can coexist on the retained triangles.

The quadratic solve converges in 1,548 iterations. A monotone roundoff cleanup raises free heights by at most 0.0000571 mm to satisfy the coverage rows. The final minimum sampled coverage gap is zero within floating-point precision at the fixed collar.

An independent triangle-overlay check verifies the complete piecewise-linear surfaces: original vertices, retained-chart vertices, and every crossing of original and fitting edges form the overlay cell corners. Height difference is affine within each cell. All **8,606 overlay samples** are covered, with no missing chart locations and a minimum gap of zero. This check includes inward source layers as well as the outer surface. Large gaps reported against buried layers describe the filled recess, not extra clearance above the visible crests.

## Preserved geometry and checks

- All 12,289 AF vertices and 24,290 ordered faces remain. UVs, named weights, native indices and original source positions are exact.
- The accepted breast geometry and normals, butt/rear region and thigh geometry outside the edit remain exact. No edited vertex enters the protected thigh-gap region below Y 840 mm.
- The existing chart coordinates and fixed collar are unchanged; only free vertex heights are revised. All projected triangle areas stay positive.
- There are no local intersections or newly introduced intersection pairs. The 30 original genital intersection pairs remain removed. The 17 existing waist/knee seam contacts remain untouched.
- Fresh geometric normals are independently verified in the NPZ and serialized OBJ. Maximum OBJ normal-angle discrepancy is below 0.000002 degrees. No texture normal map is used in the review.
- All 948 point identities and triangle/barycentric addresses remain exact, with updated positions on the revised surface.
- Six new tests check reducing inflated height to a coverage bound, respecting the outermost source layer, actual triangle interpolation, a flat symmetric prior, crossing-edge overlay samples and clipped crest sections. Together with the existing genital, breast and correspondence checks, **16 focused tests pass**.

## Review and reproduction

Current local artifacts are under `outputs/af-fitting-genital-v4/`:

- `AF-Fitting-Small-MediumB.obj`, `AF-Fitting.points.json`, `af-fitting-reference.npz` and `af-fitting-landmarks.npz`.
- `af-tight-cover-report.json`, `coverage-audit.npz`, `overlay-coverage-audit.npz` and `af-lower-independent-audit.json`.
- `af-genital-fitting-overview.png`: original AF, previous envelope and revised envelope under matched front/underside views.
- `af-genital-fitting-comparison.png`: additional wide and cutaway views.
- `af-genital-envelope-sections.png`: exact along/across surface sections.
- `af-genital-centre-profile.png`: world-space centre profile; orange original, blue previous and green revised. Higher lines have less downward projection.

```powershell
C:/Python310/python.exe tighten_af_genital.py
C:/Python310/python.exe review_af_fitting_genital.py
C:/Python310/python.exe audit_af_lower_fit.py
C:/Python310/python.exe -m unittest test_af_tight_cover test_af_fitting_genital test_af_fitting_chest test_af_comparison test_reference_surface -v
```

The v3 input mesh is hash-verified and unchanged. Use `--no-manifest` and a separate `--output` for comparison trials. The accepted breasts and the original AF movement mesh are not edited. Rear cleft and anal preparation remain separate.
