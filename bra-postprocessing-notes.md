# Local bra seam and cleavage post-processing

The protected-crotch result was accepted as the working mesh on 28 November 2026. The user proposed estimating the cleavage centreline from the breast flanks plus the skin/reference surface above and below, then joining the mesh around that estimate. Bra-edge cleanup should use the earlier fitting method, localised to the join.

## Accepted snapshot

`set_working_body.py` copies the accepted geometry, point files and audit records to `outputs/working-body-4x/`. It checks the 948 point positions and refuses to overwrite an existing snapshot with different bytes. [working-mesh.json](working-mesh.json) records the selection and SHA-256 hashes. The accepted NPZ hash is `139da5e6e7597a8fdf3d13f10498953b92a4d389386e1f3aba58f85a4bc7b1f2`.

The snapshot has 47,904 vertices and 95,630 triangles, retains the accepted under-breast/crotch/butt work, and is the 4x wrap working reference. The existing 1x mapping reference is unchanged. A post-process candidate does not become the working mesh automatically.

## Clarification of the earlier crease remark

The remark referred to the shallow diagonal traces along the **front leg openings toward the groin**, marked in pink by `inspect_bra_postprocess.py`. It did not refer to the restored flat crotch bridge or the sharper inner-thigh corners. The sampled visible faces at X=±40, Y=883 mm are skin-labelled. Their cause and whether they need alteration are not established; they are not declared defects and no lower-body edit is proposed here.

The inspection also separates two different chest problems. The examined bra-band strip contains 1,196 existing source faces, with no generated infill in that strip. The examined medial chest subset contains 579 faces, of which 189 are generated infill. Two thin inherited medial faces, 94462 and 94529, have projected area ratios below 0.1. The subsequent reconstruction region extends beyond this diagnostic subset into cleaner surrounding skin.

## Cleavage trial

`postprocess_bra.py` replaces the old medial infill and a narrow central strip with a new local height-field patch. The patch extends through the damaged upper centre to cleaner skin above it. It keeps the existing outer boundary positions.

The centreline is estimated as follows:

1. Sample the breast-side values and slopes outside the patch at each height. Intersect their tangent extensions to estimate a central valley position.
2. Sample the current centre reference below and the skin above, including their vertical directions. Blend the angle estimate into that endpoint continuation.
3. Fit a smooth one-dimensional curve with an 8 mm bending scale, exact endpoint values/directions, and consistent vertical direction when both endpoint slopes agree. This avoids fitting small reversals from fabric or old infill.
4. Reconstruct the surrounding surface from the exterior collar, the inferred centreline and local angle samples. The centreline becomes a constrained mesh edge chain; a new triangulation removes the old thin/folded elements.

This estimates missing shape from the user's proposed evidence. It is not recovered anatomical ground truth. The earlier Coatee skin/topology comparison remains an available cross-check but is not substituted into this trial.

## Local seam pass

The band strip is selected from the actual skin/smallclothes material boundary, with an 8 mm geodesic neighbourhood on each side. Only its 246 interior vertices can move; the border stays fixed. Existing topology, UVs and weights are retained there.

The pass uses the earlier final-surface local quadratic residual filter, restricted to the join: 15 mm fit radius, weight 1,000 and a 4 mm continuation scale. It moves inward from the accepted surface by at most **0.392 mm**. It converges in two chart updates, with a final update below 0.01 mm. The broad under-breast context outside the strip is unchanged.

Earlier trial interpolants either left an upper-centre dimple or made the seam boundary angles worse. The current candidate separates the jobs: local remeshing where medial geometry is inadequate, and a topology-preserving local filter for the native seam join. Outputs v1–v4 remain historical experiments; v5 is the current comparison candidate.

## Results

Current candidate folder: `outputs/bra-postprocess-v5/`.

| Face-normal jump diagnostic | Accepted working mesh | Local candidate |
|---|---:|---:|
| Cleavage maximum | 144.26° | **23.29°** |
| Cleavage 95th percentile | 9.41° | **6.43°** |
| Left seam maximum | 33.26° | **18.29°** |
| Right seam maximum | 33.33° | **18.24°** |

The cleavage patch replaces 1,226 triangles with 1,886 triangles. The combined candidate has 48,234 vertices and 96,290 triangles, remaining at approximately the same 4x density. Some residual band/side traces remain; these figures measure geometric continuity, not anatomical correctness or complete seam removal.

The centreline is a genuine shape estimate, not merely new shading. At Y=1250 mm its front depth changes from 91.677 to **97.321 mm**; the flanks at X=±30 remain around 114.7 mm, so a recess remains. At Y=1260 the centre changes from 89.684 to 94.990 mm. At Y=1220 it instead moves inward from 101.214 to 98.201 mm. These changes require visual review before promotion of the candidate.

The geometry audit detects zero non-adjacent self-crossings, no nonmanifold edges or inconsistent windings, and retains the five intended body-part openings. Lower-body geometry and normals are exact. Positions outside the seam interiors and replacement patch remain exact. Normals are recomputed and independently verified against both NPZ and OBJ exports.

Retained faces preserve UVs and named weights exactly. New corners carry exact `sourceWorkingFace`/`sourceWorkingBary` interpolation witnesses into the accepted snapshot. The new schema deliberately does not claim a single native parent for triangles spanning prior generated geometry. This working-mesh provenance replaces obsolete native-only lineage for the candidate.

All 948 point identities and AF-side values remain unchanged. Retained faces keep their barycentric coordinates after face-index remapping; 15 points inside the replaced medial region are rebound through the local chart. Their maximum positional change is 4.908 mm. New weights remain nonnegative and normalized and replay from their source witnesses. Pose/physics validation has not been performed.

Thirty-six unit tests pass, including a test that perturbing discarded central infill does not alter the surrounding-angle guide, and a planar boundary-preservation check. The old working snapshot and every recorded hash remain unchanged. This candidate has not been promoted to the accepted working mesh, and posterior strap/clasp reconstruction remains pending.

## Reproduce and inspect

```powershell
C:/Python310/python.exe set_working_body.py
C:/Python310/python.exe inspect_bra_postprocess.py
C:/Python310/python.exe postprocess_bra.py
C:/Python310/python.exe review_bra_postprocess.py
C:/Python310/python.exe -m unittest -q
```

The accepted files are under `outputs/working-body-4x/`. The inspection produces `outputs/bra-postprocess-inspection/postprocess-region-review.png`. The candidate folder contains the body OBJ/NPZ, matching point files, reconstruction and geometry reports, crossing pairs and `bra-postprocess-comparison.png`. All generated game-derived artifacts stay outside Git; the manifest, scripts and notes are published.
