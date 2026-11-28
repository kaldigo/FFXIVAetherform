# AF base selection and wrap differences

28 November 2026. Compare the available native AF bodies with the new curved vanilla reference before preparing the fitting surface. No wrap, smoothing, source-reference edits or landmark edits are performed by this investigation. The 2B fitting work remains deferred.

**Use standard AF Small chest with Medium B legs, Hip Dips off and Soft Butt off as the working base.** Small is narrowly closest by the existing paired landmarks, while XS and Small are effectively tied by sampled surface proximity. Medium legs are consistently closer than Large legs. This supports the proposed base without claiming a uniquely optimal body under every metric.

The intended next lower-body fitting surface is a smooth contact surface comparable to the inner face of panties, derived on a working AF copy. It should keep the useful broad shape without presenting tiny folds or detailed anatomy as cloth-fitting targets. Preserve the breast cleavage recess as already agreed. This comparison uses raw AF geometry; that lower fitting surface has not yet been constructed.

## Inputs and comparison rules

The comparison uses:

- The curved vanilla reference with 30,675 triangles, built from stock skin and smallclothes plus explicit flap-root caps.
- The 948 retained semantic point pairs, whose vanilla addresses were updated to the curved reference.
- The 16 available chest presets and 12 available leg presets in the prepared AF body catalogue.
- Four Hip Dips and Soft Butt combinations for each leg preset, giving 64 evaluated chest or leg states in total. Standard and Yiggle counterparts have identical rest geometry in these inputs; they are not 64 distinct shapes.

All geometry stays in native bind-space millimetres. There is no rescaling, rigid registration or ICP that could hide genuine shape differences. Point transfer across AF presets verifies ordered native triangle/corner identity, UV signatures and compatible coincident render aliases. XS merges some coincident render aliases relative to Small; that alone does not break the geometric correspondence. Authored shape replacement tables supply the leg modifier states in their existing precedence.

Two measures are kept separate:

1. **Semantic landmark movement:** distance between each accepted vanilla point and its corresponding AF point. This measures the demanded deformation at those selected locations, but depends on their distribution and on the current bra/brief envelopes.
2. **Sampled surface proximity:** closest-triangle distances in both directions, sampled at triangle centres and weighted by physical triangle area. The search includes every triangle able to improve the initial bound. This is an exact nearest-triangle result for those samples, not a continuous surface bound or a proof of anatomical correspondence.

## Size comparison

The upper body has 442 retained points and the lower body has 506. Chest and leg landmark RMS values below are measured on their respective point subsets and should not be compared as if their sampling distributions were identical.

| Chest | Landmark RMS movement mm | Bidirectional sampled surface RMS mm |
| --- | ---: | ---: |
| XS | 5.328 | 2.209 |
| Small | 5.272 | 2.218 |
| Medium | 5.980 | 3.124 |
| Large | 7.652 | 4.693 |

The surface RMS difference between XS and Small is only 0.008 mm. Small has slightly lower overall landmark RMS and a lower upper-tail landmark movement, so it is a reasonable working choice. It is not a decisive geometric win over XS. The current bra envelope contributes to this result: if only the observed-skin upper-body landmarks are scored, Medium is lowest at 4.040 mm, followed by XS at 4.157 mm and Small at 4.197 mm. A later source cleavage-patch update may change this close ranking. The smallest size label is not automatically the closest geometric match.

| Legs with both modifiers off | Landmark RMS movement mm | Bidirectional sampled surface RMS mm |
| --- | ---: | ---: |
| Medium B | 7.107 | 5.559 |
| Large B | 8.420 | 6.752 |

Medium A/B/C differ only within a narrow central region. Compared with Medium B, the changed native corner positions lie within X ±19.624 mm. The broader Y/Z extent varies by option. When the defined central-detail region is excluded from the landmark score, Medium A, B and C all have the same RMS, 7.012 mm. B remains a convenient working choice because it is the existing verified AF target; this does not establish B as a superior broad outer-body shape.

On Medium B, Hip Dips alone raises landmark RMS from 7.107 to 7.184 mm, Soft Butt alone to 8.699 mm, and both to 8.763 mm. Both modifiers off is the closest evaluated state by the retained points. The modifier ranking is landmark-based; the bidirectional surface cross-check above compares the unmodified size baselines.

Buff chest variants are farther away by the complete upper-body landmark score. Standard and Yiggle counterparts have matching rest geometry here; geometry alone does not choose their weights or physics behaviour.

## Where the wrap needs the most attention

For Small and Medium B with both modifiers off, the 948 paired points have a median separation of 2.676 mm, RMS 6.318 mm and a maximum of 30.642 mm. These are initial required movements, not errors after a wrap.

| Region | Landmark RMS mm | Largest paired movement mm | AF to vanilla sampled surface 95th percentile mm |
| --- | ---: | ---: | ---: |
| Seat | 13.05 | 28.28 | 26.38 |
| Inner thigh and underbody | 9.49 | 30.64 | 14.25 |
| Upper thigh and under-seat transition | 7.49 | 20.09 | 14.25 |
| Front chest | 10.11 | 15.13 | 7.73 |
| Shoulder and armpit | 5.21 | 12.08 | 3.50 |
| Abdomen and waist | 2.75 | 4.83 | 4.74 |
| Arms | 1.44 | 2.77 | 2.58 |

### Seat and rear centre

The seat has the largest broad surface difference. At one paired side-seat point, AF is about 11 mm farther sideways and 26 mm farther back than vanilla. Moving AF toward vanilla requires both an inward lateral movement and an inward seat movement.

The rear-centre constraint points in the opposite depth direction: Point087 is at approximately `(0, 903.5, -53.388)` mm on AF, compared with `(0, 902.921, -84.025)` mm on the curved vanilla briefs. Reaching that target would move the recessed AF point approximately 30.6 mm rearward, while neighbouring outer-seat points move forward. This opposing local deformation is a preparation and constraint issue, not merely an oversized butt.

The source here is a panty coverage envelope, while AF is detailed skin. Preparing the smooth lower contact surface before wrapping should address that mismatch deliberately. It should not silently redefine the installed AF body or treat every cloth-envelope point as an observation of hidden anatomy.

### Groin and inner thighs

The covered centre, inner-leg sides and under-seat transitions need separate local support and correspondence. A single nearest-surface mapping can cross to an opposing leg or a nearby recess even when its distance is small. Preserve left/right identity and the space required between the legs while defining the smooth contact surface. Review the A/B/C central details as part of this surface preparation rather than allowing tiny features to decide the broad leg size.

### Chest and cleavage

The main differences follow breast curvature, the underbust and the centre. The vanilla reference still contains the reconstructed bra envelope. The Coatee investigation already established that its exposed cleavage skin has a different local shape. Keep the AF cleavage recess and use the verified local topology to guide the intended correspondence; do not turn the current bra bridge into a flattening requirement for the AF fitting mesh.

### Armpits and shoulders

These regions can be spatially close while their semantic landmarks still require appreciable sliding. That is why their 3.5 mm surface-distance 95th percentile coexists with paired movements up to 12.1 mm. Local edge flow, attachment boundaries and arm/body separation need to guide the morph. A nearest-distance heatmap alone understates this kind of risk.

The abdomen, arms and most remaining limb surfaces require smaller changes. They still need seam and movement checks, but they are not the first areas to use for deciding how to prepare the fitting envelope.

## Working base and next preparation

The selected native input IDs are `af_top_01` and `af_dwn_04`, with both leg modifiers off. Their source hashes are recorded in the numerical report and verified while loading. Keep their native connectivity and provenance on the working copy.

Next, define the smooth lower contact surface around the AF shape, preserving the intended broad seat and leg structure. Review how its centre transitions meet the thigh and waist boundaries before imposing the vanilla point constraints. Keep the chest cleavage treatment separate. Once the source/reference treatment is agreed, re-evaluate affected point bindings and only then run the AF-to-vanilla wrap. The current comparison does not certify folding, animation, physics or a completed fit.

## Outputs and reproduction

Local outputs in `outputs/af-base-comparison` include:

- `report.json` and `regional-rankings.json`: candidate scores, regional measures, signed vectors at the largest point separations, and input hashes.
- `candidate-ranking.csv`: all 64 candidate states.
- `surface-ranking.json`: area-weighted surface cross-check for the standard chest sizes and Medium B versus Large B.
- `comparison.npz`: sampled distances, nearest-triangle addresses and paired point geometry.
- `body-comparison.png`: matched-camera vanilla, AF and distance views.
- `wrap-risk-closeups.png`: chest, shoulder, seat and pelvic closeups. Cyan contours show vanilla projections; the underside is a cutaway and its cut edges are not anatomical boundaries.

Use Python with `requirements.txt`. On this workstation, the existing SciPy installation can be loaded without changing the system environment permanently:

```powershell
$env:PYTHONPATH = (Resolve-Path '../.work/garment-source/density-python').Path
C:/Python310/python.exe compare_af_bases.py `
  --reference outputs/vanilla-reference-v1/vanilla-reference.npz `
  --landmarks outputs/vanilla-reference-v1/landmarks/paired-landmarks.npz `
  --catalogue '../.work/resolver-preparation-20261118/body-inputs.json' `
  --bodies '../.work/resolver-preparation-20261118/bodies' `
  --output outputs/af-base-comparison
C:/Python310/python.exe check_af_surface_ranking.py `
  --reference outputs/vanilla-reference-v1/vanilla-reference.npz `
  --bodies '../.work/resolver-preparation-20261118/bodies' `
  --output outputs/af-base-comparison
C:/Python310/python.exe render_af_comparison.py outputs/af-base-comparison
C:/Python310/python.exe -m unittest -v
```

Six regression tests pass, including the full candidate-search bound on a case where fixed nearest-centroid searches miss the true closest triangle, and native alias checks that reject merging genuinely distinct vertices. Previous planar, subdivision, cap and landmark-transfer tests remain passing. The geometry images were inspected for coverage and readable legends.
