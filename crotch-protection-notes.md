# Preserve the sharper crotch and inner-thigh transitions

The user accepted the protected under-breast result and current butt appearance as the working base. Bra seam and cleavage cleanup are reserved for post-processing; strap/clasp reconstruction is also deferred. The remaining request is a protected crotch run because broad fairness rounded the sharper transitions between the thighs and raised the centre excessively.

## Local correction

The existing region-protection solver now supports a crotch patch. It starts from `outputs/body-protected-underbreast-4x-f20-w10000` and changes 299 vertices. All 47,605 other vertex positions remain exactly at the accepted snapshot. Both chest and rear-seat positions **and normals** are checked exact.

Neighbourhoods are separated by side and restricted using unfiltered geometric vertex normals: vertices whose normal directions differ by more than 25° are excluded from a local fit. Central fits use a narrow medial strip. This avoids treating opposing thigh surfaces and the flatter crotch bridge as one broad smooth patch. The criterion protects geometric direction changes, not semantically identified anatomy; sharp fabric creases can also survive it.

The local broad slope/curvature protection uses an 18 mm radius and weight 3,000. Fine filtering uses a 12 mm radius and weight 1,000. Positions may move outward relative to the over-smoothed current result, while retaining the inward bound relative to raw. No fixed thickness offset or garment lift is added. Protection fades around the crotch patch and does not extend into the accepted chest or rear seat.

The three chart updates converge with a final maximum movement of 0.000115 mm. Normals are recalculated from the final geometry and checked independently against the NPZ and OBJ exports.

## Result

The tracked centre vertex at raw coordinates approximately X=0, Y=855.951, Z=13.376 mm previously rose **6.823 mm**. In the protected trial its rise is **0.292 mm**. The flatter bridge and sharper turns into the thighs are retained visibly in the front/underneath views and in exact geometry sections.

Across the core region abs(X)<25, 830<Y<890, -30<Z<60 mm, the maximum rise is 0.493 mm and maximum three-dimensional movement from raw is 0.616 mm. Faces touching the modified patch have a minimum raw-relative projected area ratio of 0.918. The lower-body minimum remains 0.551 in unchanged geometry elsewhere.

For underside sections over X=±30 mm:

| Section | Raw-profile RMS difference, prior | Protected | Shape RMS after subtracting constant height shift, prior | Protected |
|---|---:|---:|---:|---:|
| Z=0 mm | 3.530 mm | **0.164 mm** | 2.026 mm | **0.061 mm** |
| Z=20 mm | 4.971 mm | **0.312 mm** | 2.037 mm | **0.085 mm** |
| Z=40 mm | 3.610 mm | **0.367 mm** | 1.898 mm | **0.190 mm** |

Sections are exact bottommost intersections with the pelvis triangles. These metrics compare geometry, not shaded appearance, and the constant-height adjustment is only an analysis tool. Raw remains a garment-derived reference rather than hidden anatomical ground truth.

Some nearby crease detail returns as sharper geometry is protected. The generic all-cloth seam-normal-jump RMS increases from 12.72° to 19.28°; this includes the intended angular restoration and does not distinguish body transitions from garment creases. The result therefore remains a review candidate. It should not be further globally smoothed merely to lower that diagnostic, since that would repeat the failure this trial addresses.

## Checks and preserved work

- Zero detected proper transverse raw-cloth crossings, zero non-adjacent self-crossings and zero signed outward samples above 0.00001 mm.
- No reversed/collapsed triangles; the inherited thin cleavage-root elements remain unchanged for post-processing.
- Exact chest and rear-seat geometry/normals; exact positions everywhere outside the crotch patch.
- Exact topology, UVs, named weights and corner provenance. All 948 point addresses and AF-side values remain unchanged; source positions replay exactly.
- Independent normal recalculation matches NPZ/OBJ exports; no normal map is applied in diagnostic renders.
- 34 unit tests pass. The new feature-neighbourhood regression verifies that fitting one side of a sharp crease does not mix in the opposing side and preserves its planar geometry.

No wrap, animation/physics validation or bra seam/cleavage reconstruction is performed. The accepted under-breast and butt work is retained as requested.

## Reproduce

```powershell
C:/Python310/python.exe protect_underbreast.py --region crotch --current outputs/body-protected-underbreast-4x-f20-w10000 --output outputs/body-protected-crotch-4x --fine-radius 12 --fine-weight 1000 --broad-radius 18 --broad-weight 3000 --feature-angle 25
C:/Python310/python.exe review_crotch_protection.py
C:/Python310/python.exe review_inner_envelope.py outputs/body-protected-crotch-4x --previous outputs/body-protected-underbreast-4x-f20-w10000 --previous-label "Accepted chest / prior crotch" --label "Protected crotch"
C:/Python310/python.exe audit_envelope_normals.py outputs/body-protected-crotch-4x
C:/Python310/python.exe -m unittest -v
```

The output folder `outputs/body-protected-crotch-4x/` contains the combined body OBJ/NPZ, matching points, solver and preservation reports, crossing/normal audits, and raw/current/protected front, underside, oblique and unshaded section comparisons. Game-derived artifacts remain ignored by Git.
