# Why the upscale diverges from vanilla

Follow-up: [actual normal-map reconstruction and calibrated mapping/wrap levels](normal-map-reconstruction.md). The diagnosis below records the old PN method; the new candidates sample the installed normal textures and jointly solve the surface before any fabric filtering.

Investigation, 28 November 2026, following the user's annotated butt, thigh and breast images. Stop treating those artifacts as individual dents to remove. First establish faithful reconstruction from the vanilla shading field. The original reference and accepted fitting points remain unchanged.

## Confirmed implementation mismatch

The current prototype samples **authored mesh vertex normals** from MDL usage 3. It does not load or sample the tangent-space normal-map texture. `reference_surface.patches` uses these mesh normals to build cubic PN position patches; `evaluate` exports the normals of those patches' derivatives. It therefore substitutes a new surface-normal field for the original shading field.

PN interpolation is a local shape estimate, not an integration of the input normal field. Adjacent patches share edge position controls, which prevents positional cracks, but their cross-edge derivatives can differ. Fixing a shared edge curve does not enforce a smooth surface direction across that edge. More subdivisions sample this disagreement more finely; they do not remove it. The preceding tests checked positional continuity and local triangle orientation, which did not establish appearance fidelity.

Thin triangles can make the resulting streaks more conspicuous, but the measured disagreement exists in the continuous patches before density refinement. The earlier explanation concentrating on stretched triangles and generated caps was incomplete. There are also genuine construction faults in the generated caps; those are separate from the PN disagreement on retained native faces.

## Controlled comparison

`diagnose_reconstruction.py` renders four matched cameras with the same directional diffuse lighting, no textures, and a per-pixel depth buffer. The four columns are:

1. Native source geometry with its authored mesh normals.
2. Flat dense subdivision with the same linearly interpolated normal vectors.
3. Original curved PN upscale with its analytic patch normals.
4. The same curved PN positions, shaded using the original authored normal field.

The native and flat-dense renders match **pixel for pixel** in all four views (mean and maximum channel differences zero at the tested resolution). The PN derivative-normal column introduces the breast distortion and seat/thigh streaks; applying the original shading field to those same positions largely conceals them. This establishes that the field replacement is responsible for prominent appearance errors; it does **not** establish that retaining the old shading normals would repair the geometry. The altered silhouette and underlying patch geometry would remain.

Linear normal vectors must remain unnormalised until interpolation at the shaded point in this control. Normalising them at newly inserted vertices first changes the original interpolation. The diagnostic preserves the raw interpolated vectors and normalises per pixel.

This is a **mesh-normal shading** comparison, not a complete reproduction of the game renderer or a comparison with the game's normal-map texture enabled. Texture-normal decoding, tangent frames, UV seam handling and material conventions remain unverified because this pipeline has not loaded those assets.

## Measurements on the original PN reference

Generated caps are excluded from the native-face statistics below. Four sample locations per native face are its centre and edge midpoints.

| Test | Result |
| --- | ---: |
| PN derivative normal vs authored normal field, RMS | 9.73° |
| Same, 95th percentile | 20.34° |
| Same, maximum sampled disagreement | 43.29° |
| Edges with continuous native midpoint normals (<0.1° jump) | 2,207 |
| Those edges with a PN midpoint jump >5° | 616 |
| PN jump on those continuous-native edges, 95th percentile | 14.44° |
| Same, maximum | 39.10° |
| Retained native corner normals opposing their face | 0 |

These are sampled diagnostics, not continuous error bounds. Some native edges intentionally have discontinuous normals; those are excluded from the continuous-native-edge counts. No evidence here supports blaming flipped decoded normals on the retained native surface. The report also records the source identities and errors around the annotated areas.

## Required direction

The PN upscale and its smoothed derivatives are not accepted as a faithful fitting reference. Keep them as comparison experiments. Do not promote the recent local resurfacing trials or use restored shading normals to hide geometry errors.

The next reconstruction method must be evaluated against the original normal field, not merely against a flat face or the previous upscale. A global or jointly constrained surface solve needs positional anchors, appropriate seam handling, continuity constraints and a penalty for disagreement between geometric normals and the intended shading-normal field. Normal vectors constrain local orientation; they do not uniquely determine absolute depth or every detail of the shape. The existing vanilla geometry supplies the necessary positional reference.

When normal-map textures are included, first verify their decoding and transformation through the actual mesh tangent frame. Compare the unchanged vanilla mesh with that combined normal field before attempting to reconstruct geometry from it. Exact agreement may be impossible for parts of an authored shading field, so residuals and deviations must be measured rather than assumed away. Cleavage, body silhouette, UVs and skinning remain constraints, not reasons to freeze reconstruction artifacts.

## Reproduce and outputs

```powershell
C:/Python310/python.exe diagnose_reconstruction.py
```

Input: `outputs/vanilla-reference-v1/vanilla-reference.npz`.

Outputs: `outputs/vanilla-reconstruction-diagnosis-v1/native-flat-pn-comparison.png` and `diagnosis.json`. No mesh is modified. The new `surface_diagnostics.py` depth-buffered renderer supplies both visible face picking and matched flat/smooth comparisons. Generated images and game-derived data remain local and ignored by Git.
