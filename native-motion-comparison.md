# Original vanilla versus reconstructed motion

28 November 2026. This follow-up distinguishes the actual retained low-density vanilla triangles from the raw *reconstruction* used as the control in the first motion review. Those are different controls. All use identical installed c0201 skeleton transforms, native named weights, and the same sitting, crouching and lunging stress clips, sampled at 17 frames each.

The result is **a mixture of existing pose contacts and additional dense-surface deformation**. Normal-map reconstruction is not the sole cause. Increasing density and interpolating weights already changes the joint motion, even without reconstructed relief.

## Original control and visual comparison

The source contains 1,504 retained original triangles plus 28 generated flap-root closures. Source identity distinguishes them. Generated closures and faces depending on them are excluded from original-lineage regional comparisons. Flaps and detached details remain removed; the retained native triangles in the knee and hip regions keep their original coordinates and weights. This is not a newly simplified version of the reconstructed body.

The original column samples the actual normal textures through the original UVs. Its authored per-corner N/T/B directions are transformed by the same blended bone linear matrices, normalized and interpolated before decoding tangent normals. The other columns use geometry-derived normals, recalculated after posing. Lighting is the shared diagnostic renderer, not the full game shader, PBR pipeline or live gameplay. Normals change shading; they cannot prevent intersecting original geometry.

Local images under `outputs/native-motion-comparison-v1/` show four columns: original geometry with normal maps, flat 4x subdivision with interpolated weights, raw reconstructed geometry with interpolated weights, and the current working candidate. They share each row's pose, camera, framing and light. Sitting, crouching and lunging each have a hip/crotch row and a knee row.

The original triangles already intersect at the sitting hip/crotch and behind the knees in crouching/lunging. The original-only standard check finds 14, 20 and 10 nonadjacent pairs respectively. A supplementary check shrinks every original triangle inward by 1e-5 about its centroid to suppress ordinary shared boundaries, then also checks adjacent original faces: 20, 24 and 12 interior-intersection pairs remain. These numbers exclude every generated closure. They establish a limitation of these synthetic poses with the original geometry/weights, rather than proving a defect in actual game animations or physics.

Triangle-pair counts cannot be compared directly across densities. A single original intersection can become many child-triangle intersections, and the original shared-vertex exclusion can hide contacts that become nonadjacent after subdivision. `diagnose_native_motion.py` therefore also maps dense collisions back to their native face identities and records extra material-pair locations. In deep crouching, 20 of the raw reconstruction's 30 distinct native knee face pairs also occur in the original interior check; 10 are additional pairs. In lunging the corresponding counts are 10 of 15, with five additional pairs. These are material-location diagnostics, not penetration depths or area measurements. Summed areas of affected whole triangles are resolution-dependent and are not contact-area estimates.

## Density changes movement before relief is added

The decisive control compares a flat subdivision skinned with interpolated weights against the same barycentric material points evaluated *after* skinning the original triangles:

| Peak pose | Hip/crotch maximum difference | Knee maximum difference | Knee 95th percentile difference |
| --- | ---: | ---: | ---: |
| Sit | 14.04 mm | 27.11 mm | 13.84 mm |
| Crouch | 9.59 mm | 30.75 mm | 15.69 mm |
| Lunge | 11.19 mm | 30.75 mm | 12.34 mm |

Each regional sample uses unique dense vertices incident to that region. The rest-pose material locations agree; no relief or smoothing enters this comparison. The differences arise because linear blend skinning multiplies both a vertex position and its weights by bone transforms. Interpolating the two before skinning introduces cross terms. Interpolating the already posed original endpoints does not. Thus barycentrically correct weight transfer alone does not guarantee the same deformed surface as the original mesh.

A synthetic hinge regression verifies this independently: two original endpoints remain at 0 and 2 mm during a 90-degree hinge rotation about the second endpoint. Their original edge midpoint remains at 1 mm. A new midpoint with interpolated weights moves to (1.5, -0.5) mm instead.

## Surface-carry experiments

Two separate diagnostic deformations use the original posed triangles as the underlying reference. Neither changes the saved mesh or weights, and neither is selected as a replacement motion model:

1. **Triangle affine carry:** move the source triangle, transform reconstructed tangential offsets with its affine tangent deformation, and retain signed unit-normal offset height. Predictions from incident parents are averaged at shared dense vertices. It preserves rigid movement and the rest mesh, but creates new knee crossings in sitting and worsens crouch/lunge contacts. It is not adequate on its own.
2. **Native base plus skinned relief:** evaluate the native posed barycentric base point, then add the reconstructed rest offset transformed by the dense vertex's blended bone linear matrix. It preserves the native base without the subdivision cross term and avoids the affine trial's new sitting-knee contacts. Crouch/lunge pair counts fall from 296/148 to 284/142, and their unique native knee face pairs fall from 30/15 to 28/14. Sitting hip contacts increase from 180 to 282 dense pairs, despite involving fewer distinct native face pairs (30 to 22). It also is not an overall fix.

These trials reinforce the proposed direction: separate the motion of the original geometric base from the reconstructed shape/normal detail. Original material coordinates and the posed authored normal frame are useful constraints. A joint-aware detail reconstruction or corrective deformation must also handle compression, contact and transitions between source triangles; blindly carrying offsets is insufficient. Reintegrating a posed normal field is a possible next experiment, not an implemented or validated solution. It would need fixed broad-shape anchors and local orientation/contact constraints, and it cannot make an already intersecting original pose collision-free merely by matching its normals.

For an offline wrapping/reference tool, source-face bindings can explicitly carry the underlying base. A final game mesh using ordinary bone weights cannot directly encode that arbitrary surface binding; matching its movement may need fitted weights, pose corrections, or an intentionally different joint topology. These three diagnostics do not establish which export strategy is sufficient.

## Reproduction and validation

```powershell
C:/Python310/python.exe compare_native_motion.py
C:/Python310/python.exe diagnose_native_motion.py
C:/Python310/python.exe -m unittest -q
```

Run the first two commands sequentially: the diagnosis reads the comparison's saved peak meshes. Inputs are the existing vanilla source, 4x normal-map reference, raw body-specific cleanup and postprocessed working candidate. The JSON report records source hashes and every controlled clip definition. Generated game-derived files remain ignored. The full unit suite passes 42 tests, including rigid relief transport, zero-relief native material-point preservation, and the hinge counterexample.

The working mesh and authoritative manifest remain unchanged. The first motion review's chest checks still stand; its lower-body results now have a genuine original-vanilla control. Native game animations and live physics remain untested.
