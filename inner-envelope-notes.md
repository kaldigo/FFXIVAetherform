# Skin-anchored average curves on the 4x mesh

The subsequent [final-curvature correction](curvature-envelope-notes.md) addresses the seat dip-to-bump failure described below and partly improves the chest. This original candidate is retained for comparison.

This is a separate core-body estimation experiment. The raw reconstructed 4x geometry is the outer evidence. Local averaged curvature supplies a smooth shape trend, while exposed skin outside a narrow collar anchors its position. There is **no fixed thickness allowance and no garment lift**. Existing mapping/wrap references remain unchanged.

## Method

`fit_inner_envelope.py` operates on the already flap-free, root-rebuilt raw mesh, retaining its 47,904 vertices and 95,630 triangles. A robust area-weighted quadratic fit averages peaks and troughs in local geodesic neighbourhoods. The default radius is 30 mm, reduced near the medial chest and seat. Neighbourhoods follow connectivity, reject opposing surface directions and avoid mixing the two breast sides. This retains quadratic curvature instead of pulling the surface towards a local plane. Exceptional residuals are downweighted; there is no semantic seam classifier yet, so broad hems can still influence the fit.

The average gives a target normal displacement `a` from the raw surface. Solve for the actual normal displacement `d`, subject to `d <= 0`, while minimising a smooth correction `r = d - a`:

```text
energy = 0.5 rᵀ (M + length² L + length⁴ L M⁻¹ L) r
d = 0 on fixed surrounding skin
d <= 0 in the covered region and seam collar
```

Here `M` is vertex area mass, `L` is the cotangent surface operator and the continuation length is 12 mm. This length controls how smoothly the average's adjustment spreads; it is not cloth thickness. The bound is part of the solve, rather than a later per-vertex clamp. A soft projected-area penalty discourages local triangle collapse; a positive-area check rejects invalid final elements.

Fixing every skin vertex in the first trial retained the garment seam on the skin side. The reviewed version therefore allows a 12 mm geodesic collar beyond the material edge to move inward, anchoring to the skin beyond it. There are 6,193 free vertices and 41,711 exact fixed vertices. Gradient and bending penalties encourage a smooth join; exact continuous tangent matching is not guaranteed on this triangulated surface.

The raw XYZ positions are never globally smoothed. Only scalar displacements along a stabilised surface-normal field are solved. UVs, named bone weights, per-corner provenance and topology remain exact. All 948 landmark face/barycentric addresses and AF-side values remain unchanged; vanilla landmark positions follow the new surface.

## Result and limitations

The covered surface moves inward by an area-weighted mean **0.534 mm**, up to **3.278 mm** locally. Its raw cloth surface can remain at its original coordinates: there are zero detected proper transverse crossings against that surface and zero detected non-adjacent body self-crossings. Four signed nearest-surface samples, representing two distinct medial-root edge positions, are outward by at most **0.0087 mm**. Thus this is a reviewed approximation, not a strict containment proof.

Fabric ripples and edge ridges reduce, with the broad seat and breast profiles retained visibly. In the defined same-mask normal-variation diagnostic, RMS changes from 7.07° raw to 3.76°. Across original material-boundary edges, normal-jump RMS changes from 32.90° to 13.79°; the 95th percentile changes from 63.14° to 22.68°. Broad seam traces remain at the lower bra band and briefs edges. These diagnostics measure surface smoothness, not anatomical correctness.

Sampled raw-to-candidate profile changes, without alignment or compensating offsets:

| Profile | RMS change | Largest change |
|---|---:|---:|
| Seat rear outline, Y=840–990 mm | 0.517 mm | 1.937 mm inward |
| Breast cross-section at Y=1240 mm | 0.572 mm | 1.627 mm inward |
| Centre cleavage, Y=1190–1300 mm | 1.257 mm | 6.741 mm inward |

The large cleavage change is concentrated around the previously generated root infill. A skin-labelled vertex there moves **7.580 mm** along its fitting direction. This is not evidence of garment thickness. Two triangles in that region approach collapse; the minimum projected area ratio is 0.0572, although it remains positive and no non-adjacent crossing is detected. The maximum seam-boundary jump is still about 107°. That reconstructed root needs a separate, better-supported patch or local remeshing before accepting a final body. The root was already inferred geometry rather than measured body evidence.

The result is a more consistent direction than lifting the garment over the earlier 6 mm surface. It is **not yet a finished clean body**, and the hidden anatomy, exact material thickness, animation and physics remain unverified. No clothes are deleted from a source asset and no existing accepted fitting reference is replaced.

## Reproduce and review

With the raw 4x body and earlier 6 mm comparison already generated, and dependencies from `requirements.txt` available:

```powershell
C:/Python310/python.exe fit_inner_envelope.py
C:/Python310/python.exe review_inner_envelope.py
C:/Python310/python.exe audit_envelope_normals.py
C:/Python310/python.exe -m unittest -v
```

The default output is `outputs/body-inner-envelope-4x-collar12/`. It contains the OBJ/NPZ body candidate, matching point selection, fitted-curve cache, numerical reports, three-way geometry renders, profile overlays and `raw-underclothes-no-lift.png`. The local first trial with all skin fixed is retained separately in `outputs/body-inner-envelope-4x/`. Generated game-derived files stay outside Git.

The curve cache is tied to the raw input hash and radius; remove it when changing the curve-fit implementation. Displacement direction, average trend and solved offset are retained in the NPZ for inspection.

Validation: 29 unit tests pass, including balanced-texture averaging over a known quadratic, coupled inward bounds with fixed skin, and the projected-area penalty's analytical gradient. All 948 point addresses and AF-side values were checked unchanged; updated source positions replay exactly. Geometry is finite and weights remain normalized and nonnegative.

## Normal-update verification

`audit_envelope_normals.py` independently recalculates angle-weighted normals from the final surface, using `atan2` corner angles rather than the production `arccos` implementation. It also parses the actual OBJ and verifies its vertex/normal indices and exported values. Stored normals differ from the independent result by at most 4.88e-16 in vector length; the OBJ differs by at most 8.60e-10 due to decimal serialization. Recalculating from the serialized OBJ gives a maximum angular difference of 6.13e-8 degrees. Normals are finite and unit length.

Of 9,840 corners in the inspected rear-seat region, 8,426 have different normals from the raw reference. Their mean angular change is 1.45 degrees, with a 5.55 degree 95th percentile. The final normals are updated from the fitted geometry; raw or normal-map shading is not reused.

`seat-normal-verification.png` compares raw smooth shading, exported candidate shading, independently recalculated candidate shading and flat geometric shading for the rear and side. Exported and independently recalculated shading agree. The remaining lower garment-band/crease shape is also visible with flat triangle normals, so stale normals do not explain that residual. Smooth interpolation affects its appearance, and irregular triangle slopes can still produce uneven shading. This audit changes no mesh geometry and does not add cosmetic normal smoothing to hide those features.

## Circled upper outer seat patches: relative relief reversal

The user's annotated comparison identifies additional patches high on the outer seat, separate from the lower band discussed above. `diagnose_envelope_relief.py` probes that region using 25 mm local quadratic fits. Raw and candidate use the same raw geodesic neighbourhoods, area weights and fitting frames; each surface fits its own local trend.

Ten of 138 inspected vertices change from a dip greater than 0.05 mm below their fitted trend to a bump greater than 0.05 mm above it. One mirrored pair near X=±116.53, Y=975.14, Z=-36.50 mm changes from a 0.732 mm dip to a roughly 0.054 mm bump, despite moving inward by about 0.471 mm. Another pair near X=±110.52, Y=970.25, Z=-51.55 changes from a 0.070 mm dip to a 0.137 mm bump while moving inward by about 1.001 mm. These values describe local trend residuals, not absolute outward motion or anatomical ground truth.

Thus the candidate has a second failure beyond the cleavage root: the inward bound and smooth correction do not prevent relative dips from becoming convex patches as neighbouring points move by different amounts. In the current objective, smoothness is imposed on `d-a`, not directly on the final surface's residual curvature. Passing the containment checks does not establish a smooth body curve. A subsequent fit must control final surface curvature relative to the broad averaged body shape and use a coherently varying inward adjustment; increasing generic smoothing or recalculating normals alone does not address that missing condition. This candidate remains a diagnostic, not an accepted fitting body.
