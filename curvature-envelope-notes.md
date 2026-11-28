# Control final curvature in the 4x inner-envelope fit

This experiment corrects the relative dip-to-bump changes identified in the upper outer seat and tests the same change on the chest. It retains the raw 4x topology, skin anchors, inward-only displacement bounds, UVs, named weights and all 948 landmark addresses. No garment lift or constant thickness allowance is introduced. The earlier [inward-envelope experiment](inner-envelope-notes.md) remains a separate comparison.

## Change to the objective

The earlier solve penalised derivatives of the scalar correction `d-a`, where `a` is the averaged-curve target and `d` is normal displacement from raw. A spatially smooth correction does not guarantee that the final surface has smooth local curvature. Neighbouring vertices can move inward by different amounts and leave a formerly recessed patch standing proud.

`fit_curvature_envelope.py` now fits a local quadratic to the **final positions** in a tangent chart and penalises their signed residual from that curve. The neighbourhood uses raw geodesic distance and area weights. Fitting coordinates are updated between solves as the surface moves. Breast-side separation, opposing-normal exclusion and reduced medial fitting radii remain in place.

For a fixed local chart, the residual has the affine form `c + J d`: the source position contribution plus the effect of each vertex's own displacement direction. This accounts for direction changes across the surface instead of treating all offsets as parallel.

```text
r = d - averagedTarget
energy = 0.5 rᵀ (M + continuationLength² L) r
       + 0.5 curvatureWeight Σ areaᵢ (cᵢ + Jᵢ d)²
       + projected-area collapse penalty
```

The old scalar biharmonic correction penalty is replaced by final-surface fairness. A genuine quadratic curved surface has zero residual in this model; the objective does not try to make it planar. The tested final-surface weight is 1,000 and the maximum fitting radius is 25 mm, reduced near the medial chest and seat. The projected-area penalty is strengthened tenfold relative to the earlier trial. The exposed-skin collar remains 12 mm and the broad curve fit remains 30 mm.

Each bounded subproblem is solved with L-BFGS-B and a projected-gradient check. Rebuild the fitting charts until the largest displacement change falls below 0.01 mm or the requested pass limit is reached. The report distinguishes convergence of each bounded subproblem from convergence of the chart updates. These are local fairness controls, not a global guarantee against every new curvature feature.

## Reproduce

Generate the existing raw 4x body and the previous inward-envelope candidate first, then:

```powershell
C:/Python310/python.exe fit_inner_envelope.py --output outputs/body-curvature-envelope-4x-w1000 --curvature-weight 1000 --initial outputs/body-inner-envelope-4x-collar12
C:/Python310/python.exe diagnose_envelope_relief.py outputs/body-curvature-envelope-4x-w1000
C:/Python310/python.exe review_inner_envelope.py outputs/body-curvature-envelope-4x-w1000 --previous outputs/body-inner-envelope-4x-collar12
C:/Python310/python.exe review_curvature_correction.py
C:/Python310/python.exe audit_envelope_normals.py outputs/body-curvature-envelope-4x-w1000
C:/Python310/python.exe -m unittest -v
```

The default chart-update limit is eight passes. Continuing a run by using its own output as `--initial` retains matching prior pass records. The local generated folder contains the mesh, matching points, solver and geometric reports, seat probes, profile comparisons, chest close-ups and independent normal verification. Game-derived outputs stay ignored by Git.

## Measured result

Eight fitting-chart updates converge to a maximum last-pass movement of **0.0045 mm**, below the 0.01 mm criterion. All bounded solves satisfy their projected-gradient checks.

| Diagnostic | Previous envelope | Final-curvature correction |
|---|---:|---:|
| Seat raw-dip to candidate-bump changes over 0.05 mm, 138 sampled vertices | 10 | **0** |
| Medial chest quadratic residual RMS | 0.0917 mm | **0.0465 mm** |
| Medial chest residual absolute 95th percentile | 0.2067 mm | **0.0973 mm** |
| Cloth normal-variation RMS, same mask | 3.763° | **2.982°** |
| Seam-boundary normal-jump RMS | 13.787° | **11.412°** |
| Minimum medial chest projected area ratio relative to raw | 0.0572 | 0.0612 |
| Medial chest faces below 10% of raw projected area | 2 | **2, unresolved** |

The circled upper seat patches lose their measured dip-to-bump reversals and look smoother under recalculated geometric normals. The chest band is smoother and local chest residuals are roughly halved. The central cleavage root still retains a sharp feature: its maximum local normal jump remains about 144°, so the chest issue is only partly improved. The two very thin triangles remain in the inferred root area and need a separate patch/topology correction. This fairness term cannot supply reliable missing geometry there.

Static geometric checks detect zero proper transverse crossings against the unchanged raw cloth, zero non-adjacent body self-crossings and zero signed outward samples over 0.00001 mm. Tangent/coplanar contact at fixed joins is allowed; this is not a global volume-containment proof. Vertex and triangle index layouts, UVs, named weights and source lineage remain unchanged. There are 41,711 exact fixed vertices and five body-part boundary loops.

The mean covered-region inward movement is 0.894 mm. The largest covered-region displacement is 6.838 mm, and a skin-labelled root/collar vertex moves 7.565 mm. Those large local values are not recovered material thickness and remain part of the root review. Sampled rear outline and breast section differences from raw have RMS values of 0.813 mm and 0.684 mm respectively, including the intended inward shift. The cleavage centre profile still differs by up to 6.555 mm locally. No body-shape ground truth is implied by these comparisons.

All 948 landmark addresses and AF-side values replay unchanged; vanilla positions follow the final surface. Independent recalculation matches saved normals within 3.69e-16 vector error and the serialized OBJ within 8.60e-10. Review renders apply no normal texture. The original underwear remains at its original coordinates and is shown in the no-lift overlay.

## Under-breast shape check

The user's review accepts the seat appearance but questions the underside of the breasts. `measure_underbreast.py` measures 562 covered vertices with raw coordinates 25<=abs(X)<=100, 1180<=Y<=1220, Z>50 mm, excluding the central cleavage. It also plots exact unshaded front-depth sections at X=40, 60 and 80 mm.

| Under-breast movement | From raw | Added by latest correction |
|---|---:|---:|
| Area-weighted mean inward motion | 1.449 mm | 0.574 mm |
| Maximum inward motion | 3.381 mm | 1.907 mm |
| Mean upward motion | 0.749 mm | 0.332 mm |
| Maximum upward motion | 2.411 mm | 1.393 mm |

The curve change is real: the latest fit draws parts of the underside inward and upward, softening the transition to the torso. After removing a best constant normal inset, additional displacement still varies by 0.546 mm RMS over this region. This confirms a nonuniform shape adjustment, though it is not a pure curvature-error metric. At fixed heights the apparent depth change can be larger because the curved underside also moves upward.

This check does not alter the mesh. The broad underside curve should receive stronger protection in a subsequent chest revision while retaining the seat correction. The local outputs `underbreast-measurements.json` and `underbreast-profiles.png` make the result reviewable without lighting. The original raw profile contains garment geometry, so agreement with raw alone does not establish hidden anatomy.

## Validation scope

The regression suite now contains 31 passing tests. New tests verify that known quadratic curvature is preserved and that direct final-surface fairness removes a raised patch caused by uneven inward movement while retaining inward bounds and fixed surrounding skin.

Static checks cover raw-cloth crossings, non-adjacent self-crossings, sampled signed clearance, projected triangle area, normal updates, UVs/weights and landmark replay. They do not establish hidden anatomy, material thickness, animation or physics. The existing reconstructed cleavage infill remains a special concern, and the result is a review candidate rather than an accepted final fitting body.
