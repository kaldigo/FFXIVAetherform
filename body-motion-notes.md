# Controlled pose and animation review

Tested the `bra-postprocess-v5` 4x candidate against both the raw normal-map reconstruction and the accepted pre-bra snapshot on 28 November 2026. The accepted snapshot and `working-mesh.json` remain intact. These results support the local chest edits; they do not certify the whole body for game animation.

Follow-up: the [original-vanilla comparison](native-motion-comparison.md) now tests the actual low-density source geometry. It confirms both original pose contacts and additional deformation caused by skinning interpolated dense vertices. The raw reconstruction in this initial review is not the original vanilla mesh.

## Rig and coverage

The installed vanilla c0201 skeleton supplies 106 bones, their actual hierarchy and reference transforms. All 29 named mesh influences resolve. Rest-pose skinning reproduces each input within 1e-9 mm. Per-corner weights agree at shared geometric vertices. All influences are retained, with at most five nonzero weights per vertex.

Skeleton source: `chara/human/c0201/skeleton/base/b0001/skl_c0201b0001.sklb`, SHA-256 `440b978804362e49b52ed055353509232d756a1dd6b3ae7b96e50606379be30c`.

Fifteen procedural clips each sample 17 frames (255 frames per mesh): arm raise, forward reach, torso bend/twist, side bend, walk-like leg swing, wide step, sit, crouch, lunge, breast bounce/alternation/spread/rotation, and combined upper-body movement. `body_motion.py` records every angle and translation. Each clip lasts four seconds in the GLB. The leg swing is a stress waveform, including reverse knee rotation, rather than a biomechanically authored walking animation.

These are controlled linear skinning tests on the native skeleton. No native game animation tracks, Havok dynamics, live clothing physics, racial PBD, or Customize+ deformation were played. Breast-bone motion tests weight response, rather than simulating physics. Intermediate GLB playback uses linear translation and quaternion interpolation; numerical checks cover the saved keyframes.

## Findings

Across all sampled frames the combined edited cleavage/seam region has **zero detected nonadjacent crossings, folded triangles, or near-collapsed triangles**. Crossings are checked against the entire posed body. Shared-vertex triangle pairs are excluded. A fold means reversal against the inverse-transpose blended deformation normal; a near-collapse means minimum tangent stretch below 0.05. These are finite sampled diagnostics, not a proof of collision freedom between frames.

Passing these checks does not mean zero strain. In the deliberate opposing breast-rotation test, the postprocessed chest reaches a maximum tangent stretch of 1.464 and minimum of 0.574, compared with 1.490 and 0.551 before postprocessing. Its worst-frame 99th percentile maximum stretch is 1.228 versus 1.247 before. The patch modestly improves this severe test, but the remaining strain should be judged against actual breast-bone motion before using it as a garment deformation constraint.

Full-body intersections are checked at the declared peak of each clip. Twelve clips have no detected peak intersection. Three deep bends expose issues also present in the raw reconstruction:

| Clip | Raw intersection pairs | Postprocessed pairs | Location in rest coordinates |
| --- | ---: | ---: | --- |
| Sit | 180 | 170 | Hip/crotch transition, Y 835–954 mm |
| Crouch | 296 | 296 | Behind both knees, Y 467–652 mm |
| Lunge | 148 | 148 | Behind the bent left knee, Y 467–652 mm |

The maximum frame counts of folded / near-collapsed body triangles are respectively 182 / 32 raw versus 168 / 29 postprocessed for sitting, 92 / 4 for both meshes in crouching, and 46 / 2 for both in lunging. Sitting includes 40 raw versus 38 postprocessed folded triangles in the crotch diagnostic region. The pre-bra working mesh and postprocessed mesh have identical lower-body counts. These are existing lower-body skinning limitations under these synthetic poses, not failures introduced by the chest patch. Actual game clips and their corrective deformation must be checked before deciding whether to change geometry or weights.

A separate normalized-byte packing test discards no influence. Its largest full-body positional difference from floating weights is 0.601 mm, in sitting. The new cleavage triangles have no folds/collapse across its sampled frames and no intersections at the declared peaks. This packing check uses the new cleavage triangles only; the main floating-weight check above includes both cleavage and seam regions. The GLB retains floating weights.

Re-reading the exported GLB and replaying all 255 saved keyframes matches CPU skinning to a maximum 0.000235 mm. The complete unit suite passes 39 tests, including bind-pivot motion, rigid half-turn normal handling, and collapse detection.

## Local outputs and reproduction

Generated game-derived data stays in ignored `outputs/`:

- `body-motion-review-v1/motion-report.json`: mesh/skeleton hashes, clip definitions and per-frame metrics.
- `body-motion-review-v1/byte-weight-motion-report.json`: export precision comparison.
- `body-motion-review-v1/body-controlled-motion.glb`: rigged body and 15 named stress animations.
- `body-motion-review-v1/glb-validation.json`: exported-buffer hierarchy/skinning replay compared to CPU keyframes.
- `body-motion-review-v1/chest-motion-comparison.gif`: pre-bra versus postprocessed chest motion with freshly computed geometry normals.
- `body-motion-review-v1/pose-contact-review.png`: peak pose overview; intersecting faces highlighted red.

Run from this checkout with Python dependencies available:

```powershell
dotnet run --project motion-assets -- 'D:/Games/SquareEnix/FINAL FANTASY XIV - A Realm Reborn/game/sqpack' outputs/motion-assets
C:/Python310/python.exe evaluate_body_motion.py
C:/Python310/python.exe evaluate_motion_precision.py
C:/Python310/python.exe export_body_motion.py
C:/Python310/python.exe review_body_motion.py
C:/Python310/python.exe -m unittest -q
```

The extractor references the local Dalamud Lumina DLL and needs .NET 10. `motion-assets/HavokSkeleton.cs` is copied from the existing local XivLiveMesh skeleton reader (`../.work/2b-rigged-review-v8/Skeleton/Skeleton.cs`), preserving its AGPL-3.0-or-later notice. No skeleton or mesh assets are committed.

The remaining validation is native game animation and live physics playback, particularly the sit hip/crotch transition and deep knee flexion. No geometry or weights were changed during this review.
