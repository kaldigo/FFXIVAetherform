# Protect the broad under-breast curve; audit the lower body

The user requested a chest-only trial preserving the broad underside curve, comparison with raw and the current fit, and a lower-body review before treating that region as locked. The butt appearance was acceptable, but fixed rear renders were not sufficient evidence for the whole lower body. Cleavage and bra strap/clasp reconstruction are deferred to a later post-process; the user has ideas for that stage. No new geometry is invented in those areas here.

## Chest method

`protect_underbreast.py` starts from `outputs/body-curvature-envelope-4x-w1000`. Only 1,154 vertices in the front underside patch can change. The patch fades within abs(X)=20–120, Y=1165–1240, Z>40 mm and retains the original outer skin anchors. All 46,750 remaining vertex positions stay exactly at the current snapshot, including the entire lower body, central cleavage and rear strap/clasp region.

The solve adds protection for the robust broad quadratic fit's slope and curvature coefficients, using 30 mm raw geodesic neighbourhoods. The constant coefficient is not protected, allowing an inward positional allowance instead of forcing the outer garment height. Fine-scale final-surface quadratic residuals are filtered separately. A smooth incremental correction joins the patch to its unchanged surroundings. Absolute displacement remains inward from raw, although some vertices move outward relative to the over-inset current fit.

The selected trial uses broad weight 1,000, fine weight 10,000 and a 20 mm fine-fit radius. Three chart updates converge with a maximum final update of 0.00054 mm. There is no fixed thickness value or garment lift. Broad and fine signals overlap in scale, so this remains an experimental tradeoff rather than an exact separation of body and cloth.

The initial 10 mm fine-radius candidate preserved the curve more strongly but restored visibly excessive fabric ridges; its 20 mm diagnostic residual was worse than raw. A 20 mm / 3,000-weight trial improved that, and 20 mm / 10,000 was selected for a stronger reduction in detail. All trials remain separate local artifacts.

## Chest comparison

On the same 562 under-breast vertices used in the previous review, excluding the central cleavage:

| Measurement relative to raw | Current fit | Protected trial |
|---|---:|---:|
| Mean inward displacement | 1.449 mm | **0.670 mm** |
| Maximum inward displacement | 3.381 mm | **2.502 mm** |
| Mean upward displacement | 0.749 mm | **0.307 mm** |
| Maximum upward displacement | 2.411 mm | **1.039 mm** |
| Normal-shift variation after a constant inset is removed | 0.858 mm RMS | **0.412 mm RMS** |
| Broad slope/curvature coefficient difference | 1.670 mm RMS | **0.411 mm RMS** |

The broad coefficient metric uses the defined local normalized quadratic basis; it is not a physical surface-distance error. The protected candidate restores more of the underside profile while retaining an inward allowance. Exact unshaded X=40/60/80 mm sections compare raw, current and protected geometry.

Across the defined chest patch, the 20 mm local quadratic residual is 0.294 mm RMS raw, 0.029 mm current and 0.097 mm protected. The protected trial reduces this diagnostic by about **67% from raw**, while retaining more detail than the current heavily filtered result. This is a defined residual measure, not proof that 67% of every fabric feature is removed. Some band/seam traces remain visible and need further review/post-processing. The result is not an accepted final body.

## Lower-body audit before acceptance

`audit_lower_shape.py` compares raw, the earlier inward envelope and the current curvature fit from front, rear, both three-quarter angles and underneath. The underside render cuts the legs at Y=825 mm to expose the pelvic surface. This is a diagnostic cut in the render only.

The significant concern is the **crotch centre**, which moves upward by **6.823 mm from raw**, including **5.142 mm added by the current curvature correction**. The total three-dimensional movement there is 6.838 mm from raw and 5.154 mm from the previous envelope. This changes the arch between the legs even though it is less obvious in fixed rear views. It is a flagged shape change, not measured material thickness or a confirmed hidden-body shape.

Other inspected regions have area-weighted mean raw movement of 0.450 mm at the rear seat, 0.651 mm at the hips and 0.065 mm on the defined inner-thigh sides excluding the central crotch. Local maxima are 3.986, 3.906 and 3.842 mm respectively, concentrated in the garment/contact regions. These masks are geometric diagnostics, not anatomical segmentation or proof that all changes are desirable.

Lower-body triangles have positive projected area, a minimum raw-relative ratio of 0.551, and no faces below 20% of raw projected area. The existing whole-body crossing checks pass. Thus the principal newly identified issue is a substantial crotch-shape change, not a collapsed lower-body mesh. The butt can be retained as the comparison snapshot while the crotch remains unresolved. **The whole lower body has not been accepted or locked as final.**

## Validation and remaining work

The selected chest candidate has zero detected proper transverse raw-cloth crossings, zero non-adjacent self-crossings and zero signed outward samples above 0.00001 mm. The existing thin cleavage-root triangles remain unchanged and unresolved. These static checks do not establish complete containment, hidden anatomy, animation or physics correctness.

Lower-body positions **and normals** match the current snapshot exactly. All positions outside the chest patch are exact. Topology, UVs, named weights and corner provenance remain unchanged. All 948 source point addresses and AF-side values are exact; updated source positions replay without error. Independent geometric normal recalculation matches both NPZ and OBJ exports. Review renders apply no normal texture.

Thirty-three unit tests pass, including new checks that broad-shape protection permits a constant inset but detects a curvature change, and that the protection tapers to zero outside its patch. The observed tradeoff between curve preservation and residual band detail remains visible in the reports and renders.

## Reproduce

With the existing raw and current 4x candidates available:

```powershell
C:/Python310/python.exe audit_lower_shape.py
C:/Python310/python.exe protect_underbreast.py
C:/Python310/python.exe review_inner_envelope.py outputs/body-protected-underbreast-4x-f20-w10000 --previous outputs/body-curvature-envelope-4x-w1000 --previous-label "Current curvature fit" --label "Protected under-breast curve"
C:/Python310/python.exe measure_underbreast.py outputs/body-protected-underbreast-4x-f20-w10000 --previous outputs/body-curvature-envelope-4x-w1000 --previous-label "Current curvature fit" --label "Protected under-breast"
C:/Python310/python.exe audit_envelope_normals.py outputs/body-protected-underbreast-4x-f20-w10000
C:/Python310/python.exe -m unittest -v
```

The selected output folder is `outputs/body-protected-underbreast-4x-f20-w10000/`, containing OBJ/NPZ geometry, matching points, protected-fit and preservation reports, unshaded underside profiles, raw/current/protected renders and normal checks. The current curvature folder contains `lower-shape-audit.json` and `lower-multiview-review.png`. Generated files remain outside Git.

For the trial screen, also generate the original 10 mm / 1,000-weight and 20 mm / 3,000-weight candidates in their separate folders, then run `review_protected_chest.py`. That script compares all three with raw/current using the same diagnostic and produces `protection-trials.json/png` in the initial trial folder.
