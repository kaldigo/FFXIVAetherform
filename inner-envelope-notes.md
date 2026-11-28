# Skin-anchored average curves on the 4x mesh

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
C:/Python310/python.exe -m unittest -v
```

The default output is `outputs/body-inner-envelope-4x-collar12/`. It contains the OBJ/NPZ body candidate, matching point selection, fitted-curve cache, numerical reports, three-way geometry renders, profile overlays and `raw-underclothes-no-lift.png`. The local first trial with all skin fixed is retained separately in `outputs/body-inner-envelope-4x/`. Generated game-derived files stay outside Git.

The curve cache is tied to the raw input hash and radius; remove it when changing the curve-fit implementation. Displacement direction, average trend and solved offset are retained in the NPZ for inspection.

Validation: 29 unit tests pass, including balanced-texture averaging over a known quadratic, coupled inward bounds with fixed skin, and the projected-area penalty's analytical gradient. All 948 point addresses and AF-side values were checked unchanged; updated source positions replay exactly. Geometry is finite and weights remain normalized and nonnegative.
