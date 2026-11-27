# Vanilla motion reference coverage

Investigated 27 November 2026 following reports of lowered, pointed or shelf-like breasts in vanilla AF clothing on Small at Vanilla Height, including Critical Hit B-1 Tour Top. The nude body was reported to look normal. The exact shelf may have occurred on another top; it was not reproduced in game during this investigation.

## Cause found

Body-motion conversion introduced in `dc31b67` constructs a source reference from the vanilla bare top and bottom. Those models contain exposed skin plus the stock bra and briefs. The native morph correspondence includes those covered surfaces, but the motion sampler discarded every non-skin material. A breast support located on the stock bra could therefore sample a distant exposed-skin rim instead of its actual source position and weights.

Captured Tour Top motion supports on Small showed breast-reference misses up to 52.117 mm. At one support, a point at approximately `(85.39, 1236.99, 112.61)` mm sampled exposed skin at `(66.55, 1193.29, 91.38)` mm. This contaminates both the source bone masses and the source position moments used by the conversion objective. The resulting error varies across vertices and can cause local weight peaks; it is not a uniform breast-height adjustment.

For example, one native Tour Top vertex with 50.6% authored breast influence received 95.3% before the correction. With the corrected source reference it receives 16.1%. A decrease in breast mass is not itself evidence of a defect: conversion must account for the actual AF target and the garment's authored motion relative to its source reference. The demonstrated defect is sampling the wrong source surface.

Historical generation also ruled out `3bedd52` as a meaningful bust geometry change for the Tour Top: its measured bust positions and weights are identical to the preceding weight-conversion output. This does not rule out separate geometric problems on other garments.

## Correction

The vanilla conversion explicitly includes the stock underwear in its source motion reference. These surfaces provide motion data for covered areas; this does not treat the underwear contour as bare anatomical ground truth or change the fitted geometry.

AF targets and YAB/Rue source bodies remain skin-only, excluding non-skin attachments and piercings. The coefficient optimizer, conversion itself, garment-owned bone protection, retained-skin conversion and shader influence limits remain enabled. Vanilla top/bottom cache keys and option names advance to Motion Fit 5 so managed fits regenerate. Family cache revisions do not change.

## Verification

- `BodyMotionReferenceCheck` fails on the previous solver's missing-underwear coverage, and passes after the correction. It checks 196 covered native top/bottom reference vertices against their authored weights and position moments. Their worst exposed-skin fallback gap is 70.072 mm; with the complete source reference the checked source-vertex gap is zero. In the captured Tour Top breast-support set, the maximum gap falls from 52.117 mm to below 0.001 mm.
- The skin-only reference is separately checked to contain exactly the skin triangles, preserving target and family isolation.
- Full before/after refits passed for Critical Hit B-1 Tour Top, Hempen Camise (canonical female Midlander model), Summer Sunset Beach Cover-up, Exarchic Coat of Healing, Ao Dai and Faire Joi, on both Standard Small and Yiggle Small, with Medium B legs and no leg shape toggles. Existing classification records were used where available.
- For all 12 fits, triangle topology, vertex declarations, every non-weight vertex field (including positions, normals, tangents and UVs), and shape/option tables match their pre-fix outputs. Weights remain normalized and clothing keeps its authored influence capacity.
- Motion coefficient tests, 2,000 independent thigh-envelope samples, standalone/override controls and the full plugin build pass. No installed plugin or live mod files were changed.

The reference regression can be run with locally owned native game model fixtures:

```powershell
dotnet run --project tests/GeometryCheck.csproj -c Release -- --motion-reference <solver-output-directory> <native-c0201e0000_top.mdl> <native-c0201e0000_dwn.mdl>
```

## Remaining validation

Vanilla Height and bounce are runtime PHYB settings; they are not arguments to garment generation. These offline checks establish and fix the source-reference error, but do not run Havok or reproduce a particular character's settled pose. Confirm Small at Vanilla Height on the reported outfits in game. A shelf on another top may still require a separate geometry investigation after its weights use the corrected reference.
