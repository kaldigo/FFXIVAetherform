# Body-specific flap and underwear-relief cleanup

Follow-up: [4x raw/neutral shape separation](body-shape-separation.md) tests retaining broad shape while removing finer relief, including an explicit inward allowance. The blur below remains the earlier control; the new separation trial does not overwrite these outputs.

28 November 2026. Retain the bra/briefs mesh envelope, remove irrelevant flaps and reduce fabric relief. Do not remove the underwear geometry, infer hidden naked anatomy or apply a thickness offset yet.

## Selected treatment: blurred normal relief

The selected path blurs only the two smallclothes normal maps in linear RG, using an 8-texel Gaussian kernel before the shared reconstruction solve. Material normal strength remains 1.0. Skin normal maps remain unchanged. No post-solve blanket mesh smoothing is applied.

Neutralising the cloth normal-map contribution was tested and rejected for shape preservation. It removes low-frequency shaping as well as weave/stitch detail. The first assessment incorrectly treated its cleaner appearance as preferable, then described the seat as flattened without measuring the change. The actual change is more complex: the neutralised surface can look rounder or fuller while redistributing width, lower-seat projection and the rearward bulge.

`compare_seat_profiles.py` measures plane/mesh intersections with identical coordinate origins. At Y=900 mm, neutralisation widens the complete pelvic cross-section by about **2.55 mm**, but reduces its rearmost projection by **5.12 mm**. At Y=850 mm it projects **1.22 mm further rearward**. These describe different parts of the curve and do not establish total butt volume. The blurred result follows the raw rear outline much more closely: differences at the six reported heights are within about 1 mm. `seat-profile-overlay.png` shows the side and rear outline comparisons directly.

Before root reconstruction, the 4x blurred solution differs from raw geometry by 0.622 mm at the 95th percentile over all vertices, compared with 1.785 mm for neutralisation. Maximum differences are 2.570 and 6.219 mm respectively. The cloth-vertex 95th percentiles are 1.435 and 4.516 mm. Geometry on exposed skin is not hard-frozen by the global solve: its outside-cloth vertex 95th-percentile change is 0.188 mm for blur and 1.048 mm for neutralisation.

The blur preserves the measured cleavage shape more closely too. At Y=1220 mm, the raw gap relative to the average front depth at X=±40 mm is 15.395 mm; blur gives 15.429 mm, while neutralisation gives 11.689 mm. This is why detail suppression must be judged against shape, not just smooth-looking shading.

The 8-texel setting is specific to these 256×512 smallclothes textures. Its physical width varies with UV scale and is not a universal gear-filter setting. Raised edge bands are attenuated rather than fully erased. A later offset can be tested on this retained envelope; no constant cloth thickness has been inferred here.

## Flap-root construction

The original native-source cleanup already removes the reviewed flap faces and detached decorations. This step retains that removal and replaces **all six** provisional root closures, including their immediate folded walls. The medial front root uses an additional native neighbour ring so its boundary admits a consistent exterior chart.

Each replacement is a constrained triangulated disk. A thin-plate interpolant fitted to the surrounding exterior collar supplies the missing surface. Dense outer boundary positions remain exact. Real retained medial vertices constrain the cleavage valley, so the front patch does not simply bridge the breasts with a flat surface. Unsupported cap-only vertices are not anatomy constraints.

All replacement triangles point outward in their local charts, shared-edge winding agrees with the retained mesh, and the shell retains only the five original neck/wrist/ankle openings. No old `_cap` faces remain. This replaces the earlier known folded closures, but does not constitute a global self-intersection or animation/physics proof. Missing root interiors are fitted surfaces supported by their surroundings, not recovered native geometry.

UVs and named bone weights remain exact on retained faces. New root corners interpolate exterior source witnesses; their per-corner native-face/barycentric provenance is explicit. A new triangle may span several native parents, so old single-parent assumptions do not apply there. All 948 point identities and AF-side point data remain. In the 4x result, 27 source points are rebound through the local patch charts; those root correspondences are approximations, not exact old material addresses.

## Output roles and validation

| Role | Selected local folder under `outputs/` | Vertices | Triangles | Final AF density, top / legs |
| --- | --- | ---: | ---: | ---: |
| 1x mapping | `body-cleanup-1x-blur8` | 12,032 | 23,978 | 1.020 / 1.023 |
| 2x wrap | `body-cleanup-2x-blur8` | 23,640 | 47,174 | 2.002 / 2.009 |
| 4x wrap | `body-cleanup-4x-blur8` | 47,913 | 95,648 | 4.054 / 4.075 |

Each folder contains `vanilla-body-fit.obj`, `body-fit-reference.npz`, its matching `Vanilla-BodyFit.points.json`, the paired candidate NPZ, construction reports and an audit. OBJ carries review geometry, normals and UVs; the NPZ also carries bone weights and provenance. Use the body-fit files after closure rebuilding, not the intermediate `normal-map-reference.npz`.

The 4x folder additionally contains:

- `body-cleanup-comparison.png`: raw relief, selected blur and neutralisation, with the same root reconstruction procedure and matched cameras/lighting.
- `seat-profile-overlay.png` and `seat-profile-comparison.json`: measured shape comparisons rather than shading impressions.

The `body-cleanup-4x-raw` folder is the root-rebuilt comparison control. `body-cleanup-*-neutral` folders are retained experiments, not selected mapping/wrap outputs. Original raw reconstruction references and accepted point selections are unchanged. No AF mesh is modified and no wrap has been executed.

All three selected audits pass: finite geometry, positive patch chart orientation, consistent manifold connectivity with five boundary loops, exact patch boundary/medial constraints, exact retained UVs/weights, normalized nonnegative weights, valid point replay and unchanged AF point data. The suite has 20 passing tests, including body-only filtering and a medial-valley-preserving closure fixture. No thickness offset is applied: **offset = 0 mm**.

## Reproduce

Run after generating and auditing the 1x/2x/4x raw normal-map reconstruction candidates described in `normal-map-reconstruction.md`.

```powershell
C:/Python310/python.exe body_cleanup.py --raw outputs/normal-map-mapping-1x `
  --mode blur --sigma 8 --output outputs/body-cleanup-1x-blur8
C:/Python310/python.exe body_cleanup.py --raw outputs/normal-map-wrap-2x `
  --mode blur --sigma 8 --output outputs/body-cleanup-2x-blur8
C:/Python310/python.exe body_cleanup.py --raw outputs/normal-map-wrap-4x `
  --mode blur --sigma 8 --output outputs/body-cleanup-4x-blur8
C:/Python310/python.exe audit_body_cleanup.py outputs/body-cleanup-1x-blur8 --raw outputs/normal-map-mapping-1x
C:/Python310/python.exe audit_body_cleanup.py outputs/body-cleanup-2x-blur8 --raw outputs/normal-map-wrap-2x
C:/Python310/python.exe audit_body_cleanup.py outputs/body-cleanup-4x-blur8 --raw outputs/normal-map-wrap-4x
```

To reproduce the diagnostic controls, run `body_cleanup.py` on the 4x raw folder with `--mode raw --output outputs/body-cleanup-4x-raw` and `--mode neutral --output outputs/body-cleanup-4x-neutral`. Then:

```powershell
C:/Python310/python.exe render_body_cleanup.py `
  --raw outputs/body-cleanup-4x-raw --blur outputs/body-cleanup-4x-blur8 `
  --smooth outputs/body-cleanup-4x-neutral --output outputs/body-cleanup-4x-blur8/body-cleanup-comparison.png
C:/Python310/python.exe compare_seat_profiles.py
C:/Python310/python.exe -m unittest -v
```

`body_cleanup.py` is the body-specific layer. `reconstruct_normal_map.run` now accepts an injected normal field so this filtering does not become an implicit rule of the reusable reconstruction core. Generated game-derived data remains local and ignored by Git.
