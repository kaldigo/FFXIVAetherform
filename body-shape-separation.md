# Separate broad body shape from fabric relief on the 4x reference

28 November 2026. Test the raw-minus-neutral difference on the 4x mesh and compare with raw, blurred and neutralised references. Preserve the butt curve, breast shape and cleavage while allowing an inward movement that could later represent garment thickness. Only the 4x level is tested here; 1x mapping and existing 2x/4x references remain unchanged.

## Method

Work **before** flap-root rebuilding, where raw and neutralised meshes have identical vertices, triangles and native barycentric lineage. Assert those correspondences before subtracting them. Both reconstruction results move along the same authored-normal directions, so their difference is represented as one signed displacement per vertex, rather than blurring XYZ positions independently.

For raw `R`, neutralised `N` and unit displacement direction `d`, define `delta = dot(R-N, d)`. A cotangent surface operator with vertex area masses separates its broad component:

```text
(M + scale² L) broad = M delta
candidate = R + taper * (broad - delta - inwardAllowance) * d
```

Inside the cloth region this is neutralised geometry plus the broad shape difference, with an explicit inward allowance. A 15 mm surface-distance transition returns to the unchanged raw skin outside the cloth region. The operator excludes unsupported cap faces and follows mesh connectivity; it does not average across nearby disconnected surfaces in space. Its length is measured using the common native flat control. This is a gradual spectral filter, not a hard wavelength cutoff.

Test scales are 6, 12 and 20 mm. All use a **0.5 mm inward trial allowance**, along the reconstruction's authored-normal directions. That is a configurable experimental value, not a measurement of vanilla garment thickness. Local fabric depressions can move outward as they are filled; the allowance is not an inward-only clamp at every point. The 6 mm result has an area-weighted mean cloth movement of **0.533 mm inward**, with local signed movements ranging from 2.430 mm inward to 1.364 mm outward before root rebuilding.

The six roots are rebuilt afterwards using the same body-specific routine as the controls. Boundary and real medial constraints follow the separated surface. Original UVs and named weights remain exact on retained faces; new root corners retain interpolated exterior witnesses and explicit per-corner provenance.

## Four-way comparison and shape reference

`outputs/body-separation-comparison/four-way-comparison.png` shows raw, blurred, neutralised and the 6 mm separated result, with matched cameras and geometry-derived lighting. No normal texture is applied to the rebuilt columns. The controls are displayed in their existing states; the separated column is explicitly labelled with its additional 0.5 mm inset.

For **numerical shape comparisons**, every control is also given the same inward allowance before root rebuilding. The resulting inset raw surface is the reference. This avoids penalising the intended inward shift as a shape error or favouring a candidate merely because only it was inset. Display-state measurements and equal-allowance measurements are both retained in `evaluation.json`.

Rear outlines are sampled at 66 heights from Y=850 to 980 mm. Breast depth uses 45 front-ray samples at five heights and nine X positions. Cleavage depth is the average front depth at X=±40 minus the centre depth, measured at five heights. These are sampled shape checks, not complete volume or curvature proofs.

| Variant | Rear outline RMS error | Breast depth RMS error | Maximum cleavage-gap error | Fine-band residual reduction |
| --- | ---: | ---: | ---: | ---: |
| Raw, equally inset reference | 0 | 0 | 0 | 0% |
| Blurred, equally inset | 0.890 mm | 0.458 mm | 0.706 mm | 43.9% |
| Neutralised, equally inset | 3.231 mm | 3.133 mm | 3.700 mm | 100% |
| Separated, 6 mm | **0.346 mm** | **0.405 mm** | **0.483 mm** | **70.1%** |
| Separated, 12 mm | 0.728 mm | 0.770 mm | 0.961 mm | 85.7% |
| Separated, 20 mm | 1.153 mm | 1.225 mm | 1.756 mm | 90.3% |

The 6 mm trial is the recommended comparison candidate when prioritising the requested curves. The 12/20 mm trials remove more fine variation but progressively alter the broad profiles. The 6 mm rear error has a 0.599 mm absolute 95th percentile and 1.472 mm maximum. Breast depth has a 0.783 mm absolute 95th percentile and 0.985 mm maximum. Its sampled cleavage gaps remain approximately 1.82, 15.05, 19.59, 24.23 and 16.12 mm at Y=1200, 1220, 1240, 1260 and 1280 mm respectively.

The fine-band diagnostic is `signal - lowpass3mm(signal)` on measured cloth vertices away from root reconstruction regions. Each signal is its normal displacement relative to neutralised geometry. The explicit inward allowance is removed before this measurement. RMS falls from 0.245 mm raw to 0.073 mm for the 6 mm candidate. This is a defined residual-band measure, **not** proof that 70% of every fabric feature or all fabric geometry has been identified. Some broad seam/edge structure remains. Fabric folds and anatomical shape can overlap in scale.

## Candidate and validation

Recommended 4x trial folder: `outputs/body-separation-4x-s6`.

- `vanilla-body-fit.obj`: review mesh, 47,932 vertices / 95,686 triangles.
- `body-fit-reference.npz`: final positions, UVs, weights, corner provenance and explicit offset metadata.
- `Vanilla-BodyFit.points.json` and `paired-landmarks-candidate.npz`: matching 948 identities; AF-side data unchanged.
- `normal-map-reference.npz`: corresponding pre-root stage, retaining the full and broad differences, removed detail, source directions and transition mask. Its raw `normalMapTarget` is comparison provenance, not a newly solved target field.
- `separation-report.json`, `body-cleanup-report.json`, `body-cleanup-audit.json`: parameters, source hashes, construction and validation.

The 12/20 mm counterparts are under `body-separation-4x-s12` and `body-separation-4x-s20`. `body-separation-comparison` contains the four-way render, the scale-trial render, cached profile samples, equal-allowance evaluation and the offset-only raw reference.

The selected candidate's audit passes. Native elements retain positive projected area relative to raw; the minimum ratio before root replacement is 0.332. All six replacement patches are single-valued in their exterior charts. Connectivity has no nonmanifold edges or inconsistent winding and retains the five body-part openings. All 948 point identities replay correctly, named weights remain normalized/nonnegative, and 41,447 pre-root vertices outside the transition remain exactly at their raw positions. Final average AF-density factors are 4.064 for the top and 4.081 for the legs.

Twenty-three tests pass, including preservation of constant displacement, invariance under physical rescaling, preferential attenuation of fine variation and correct composition of the inward allowance with an unchanged outer region. There is no global intersection proof or pose/physics validation. The output remains a modified garment envelope, not reconstructed hidden naked anatomy. No wrap is executed or accepted reference replaced.

## Reproduce

Generate the existing 4x raw/blur/neutral controls described in the earlier reconstruction and body-cleanup notes first. Then:

```powershell
C:/Python310/python.exe separate_body_shape.py --scale 6 --inward .5 --output outputs/body-separation-4x-s6
C:/Python310/python.exe separate_body_shape.py --scale 12 --inward .5 --output outputs/body-separation-4x-s12
C:/Python310/python.exe separate_body_shape.py --scale 20 --inward .5 --output outputs/body-separation-4x-s20
C:/Python310/python.exe evaluate_body_separation.py
C:/Python310/python.exe render_body_separation.py --trials
C:/Python310/python.exe render_body_separation.py --scale 6
C:/Python310/python.exe audit_body_cleanup.py outputs/body-separation-4x-s6 --raw outputs/normal-map-wrap-4x
C:/Python310/python.exe -m unittest -v
```

Use `--inward 0` to separate relief without the trial allowance. Raw/neutral source references remain read-only inputs; generated meshes, images and game-derived data stay local and ignored by Git.
