# Vanilla fabric fold cleanup trial

28 November 2026. A separate, bounded cleanup of the curved vanilla mesh, compared with the existing upscale. The accepted source reference, its existing point files and all AF inputs remain unchanged.

The trial reduces fine ripples while retaining the broad body shape and central cleavage. It is a cleaner candidate, but **larger underwear-shaped construction creases remain**, particularly near the underbust and side attachment regions. This is not yet a fully fabric-free body reference or an automatically accepted wrap target.

## Method and protected shape

The current upscale carries authored mesh geometry and vertex normals, not a sampled normal-map texture. Consequently, texture-image filtering would not remove its existing surface marks.

`clean_reference.py` fits area-weighted local quadratic surfaces in geodesic neighbourhoods. The fit allows broad curvature while attenuating smaller deviations, with robust residual weighting. It moves vertices along a smoothed surface normal and limits movement to 2.5 mm. It keeps the same topology, UVs, skinning weights and native provenance.

Three physical radii were compared: 20, 35 and 50 mm. The 50 mm candidate has the lowest normal-detail residual among these bounded trials. The tool also retains a Taubin trial mode, but this first method produced only modest improvement with the conservative constraints.

Two surface-fitting variants are kept separately:

- `outputs/vanilla-cleanup-quadratic-v3`: cloth-only cleanup. All exposed-skin vertices and skin joins stay fixed.
- `outputs/vanilla-cleanup-blended-v1`: cleanup with an 8 mm geodesic transition into adjacent skin. This reduces the hard transition at cloth joins. Geometry outside that band stays fixed. This is the candidate used for the main comparison.

The medial chest, including the existing cleavage centreline, is protected. Main body extrema and breast-apex constraints remain fixed. The join blend shares the existing geometric vertices, so it cannot open a crack between cloth and skin. Local backtracking reduces displacement only around elements that would reverse or strongly distort; it does not suppress unrelated cleanup across the whole garment.

The candidate preserves the current reference's cleavage shape. It does not silently substitute the Coatee patch or claim that the underwear has revealed the complete hidden body shape.

## Comparison and checks

The selected blended candidate has:

| Check | Result |
| --- | --- |
| Cleavage centreline depth change at the sampled heights | 0 mm |
| Largest sampled change within X ±10 mm of the chest midline | 0.400 mm |
| Maximum bounding-box change | 0.218 mm |
| Maximum vertex movement | 2.5 mm |
| Maximum skin movement outside the allowed transition | 0 mm |
| Reversed or degenerate triangles | 0 |
| Triangle connectivity, UVs and weights | Unchanged |
| Existing point identities and triangle addresses | All 948 retained |
| Point position changes on the candidate | Mean 0.075 mm; 95th percentile 0.462 mm; maximum 2.5 mm |

The local normal-detail residual falls from 12.112 to 11.058 degrees RMS, approximately 8.7 percent. Its 95th percentile falls from 21.389 to 15.050 degrees, approximately 29.6 percent. This is a geometric roughness proxy, not a semantic count of fabric folds. The modest RMS reduction and the rendered views are reasons to keep the result as an experiment rather than declare the larger garment imprint removed.

The trial preserves coarse form and reduces the smaller oscillations. Larger seams and folds overlap the scale of real body curvature and some are encoded at source joins. Removing those reliably needs targeted surface reconstruction or explicit fold-region constraints; broad smoothing alone risks changing body shape. The existing original and cloth-only controls make that next comparison possible.

No continuous global self-intersection proof, animation test or wrapping solve has been performed. The element-orientation checks do not certify those properties.

## Review files

The main candidate folder contains:

- `vanilla-cleaned.obj`: selected mesh for inspection.
- `vanilla-cleaned-reference.npz`: geometry, normals, original provenance, UVs and weights, protected-vertex mask and cleanup displacement.
- `cleanup-comparison.png`: current upscale, cleaned candidate and movement heatmap with matched cameras and geometry-derived lighting. No textures are used.
- `cleanup-strengths.png`: the three strengths beside the original.
- `cleanup-report.json` and `cleanup-audit.json`: parameters, geometry checks and sampled chest profiles.
- `Vanilla-Cleaned.points.json` and `paired-landmarks-candidate.npz`: points evaluated on this candidate, with the same names and triangle addresses. They do not replace the accepted point files.

The current upscale remains at `outputs/vanilla-reference-v1/vanilla-reference.npz`, SHA-256 `c09a5a897f7278cd49fd90e4742e20a3b70762af2f1c1b6d71edc95ce502f632`.

## Reproduce

Run from this folder with the packages in `requirements.txt`. On this workstation:

```powershell
$env:PYTHONPATH = (Resolve-Path '../.work/garment-source/density-python').Path
C:/Python310/python.exe clean_reference.py `
  --reference outputs/vanilla-reference-v1/vanilla-reference.npz `
  --method quadratic --radii 20 35 50 --max-move 2.5 --transition-mm 8 `
  --output outputs/vanilla-cleanup-blended-v1
C:/Python310/python.exe audit_cleanup.py `
  --reference outputs/vanilla-reference-v1/vanilla-reference.npz `
  --candidate outputs/vanilla-cleanup-blended-v1/vanilla-cleaned-reference.npz `
  --landmarks outputs/vanilla-reference-v1/landmarks/paired-landmarks.npz
C:/Python310/python.exe render_cleanup.py outputs/vanilla-cleanup-blended-v1
C:/Python310/python.exe -m unittest -v
```

Use `--transition-mm 0` with a separate output folder for the cloth-only control. Regression tests verify removal of a synthetic oscillation, preservation of a smooth quadratic centre and fixed boundaries, and protection of skin outside the allowed transition. The earlier reconstruction, cap, landmark and distance-search tests remain enabled.
