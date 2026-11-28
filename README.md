# Aetherform Refit Tool

Working area for custom refit authoring using paired AF body surfaces, garment retopology and constrained deformation.

## Repository and publishing

- Repository: https://github.com/kaldigo/FFXIVAetherform
- Publish implementation work to branch: `refit-tool`.
- Existing plugin checkout: `../FFXIVAetherform`. This work uses its own checkout here.
- Start with a fresh branch containing only these notes, with no inherited AF source files or history. Develop the refit tool here.

## Agreed approach

1. Preserve AF body topology and vertex identities while morphing a reference copy to the vanilla source shape.
2. Reconstruct the garment in its original shape against that reference, designing topology and deformation constraints together.
3. Bind garment vertices and supporting regions to the vanilla-shaped AF reference, retaining garment offsets and attachment relationships.
4. Apply the paired AF body morph to produce the target fit, with classifications constraining each region's response.

Well-aligned topology gives constraints suitable regions and degrees of freedom. Place edges along intended bends, plate boundaries, garment seams and important bone-weight transitions. Garment topology should suit the body deformation without requiring identical body and garment connectivity.

## Shape and deformation constraints

The AF fitting mesh must retain the central cleavage recess. Clothing that spans it is handled by garment constraints; the body reference is not flattened to match a bra envelope.

The lower fitting surface is intended to be a smooth contact surface comparable to the inner face of panties, prepared on an AF working copy. The [AF base comparison](af-base-comparison.md) supports Small chest and Medium B legs with Hip Dips and Soft Butt off as the working base. Small and XS are effectively tied by surface proximity; Small has the slightly better current paired-landmark score. The comparison identifies the seat, rear centre, underbody and chest as priority regions. No smoothing or wrap has been applied yet, and 2B fitting remains deferred.

Use the base mesh, mesh normals and decoded normal-map information as shape evidence. Combine this with material, connectivity, skinning and author-defined behaviour. Normals inform shape; classifications define permitted deformation.

- Rigid plates and attached gems: preserve dimensions and shape while following their supporting region by translation and rotation.
- Curved armour shells: preserve intended curvature and sharp boundaries.
- Flexible cloth: allow smooth bending with enough geometry for body curvature and articulation.
- Directional details such as flowers: allow bending in specified directions while protecting the profile and attachment offset.
- Seams, openings, thickness and layered attachments: retain their construction relationships.

Simplification must preserve both surface shape and deformation. A flat area may still need vertices where bone influences change. Preserve intended movement and physics, transfer weights deliberately, and respect each model's supported influence count.

## Initial evidence

On 27 November 2026, the user reported trying this method on their current refit with promising results. The garment, assets and exact steps are to be captured from that experiment; no measured outcome has been recorded here yet.

## Proposed gear reconstruction workflow

Recorded on 27 November 2026 as an idea for later work. Do not implement this stage yet.

1. Take the original piece of gear and increase its mesh density to twice or four times the density ultimately required.
2. Use the gear's normal map to reconstruct actual surface geometry on that denser mesh, rather than merely splitting flat faces.
3. Use the reconstructed high density surface as the reference for creating new garment topology at the body's required density.
4. Transfer the gear's normals and UVs onto the new topology.
5. Use this rebuilt, attributed garment as the starting piece for the body morph and subsequent constrained fitting.

The deliberately denser intermediate surface separates shape reconstruction from the final topology layout. The existing bone-weight and physics requirements still apply when preparing the starting mesh. The exact meaning of twice or four times density and the transfer method remain implementation choices for later review.

## Next work

- Capture the current successful experiment, including garment, source body, AF target and authoring steps.
- Establish the paired body references and verify their correspondence.
- Compare original and rebuilt garments in the unchanged source shape before applying the target morph.
- Evaluate constraints, silhouette, contacts, folding and intersections across intended AF presets.
- Validate animation and physics, including bone-weight transitions and rigid attachments.
- Prepare reusable meshes, bindings and constraints offline; measure runtime costs before deciding what belongs in automatic refits.

## Vanilla body reference prototype

**Reconstruction status:** the [native-normal comparison and diagnosis](reconstruction-diagnosis.md) demonstrates that the PN upscale introduces surface-direction errors absent from vanilla. The cleanup and local-repair candidates below are experimental symptom treatments and are not a validated fitting baseline. Normal-map textures have not yet been sampled.

The [larger-fold and recessed-line repair](surface-repair-notes.md) investigates reconstruction creases and replaces three folded lower flap-root disks. It provides a separate candidate with rebound landmarks; some inner-thigh and seat creases remain. This also qualifies the original orientation checks below: passing orientation against a native face did not rule out a folded generated cap.

The [fabric fold cleanup trial](vanilla-cleanup-trial.md) provides a separate smoothed comparison candidate. It reduces fine ripples with bounded changes and protects the cleavage; larger underwear construction creases remain. The original upscale and accepted landmark files are retained.

`reference_surface.py` builds the vanilla body reference from the stock top and leg skin, using the close-fitting stock smallclothes for the covered regions. It removes the tail flap, reviewed bra and hip flaps, and detached decorative details. Six exposed flap roots are closed with 28 explicitly generated cap triangles. The neck, wrists and ankles remain open for their separate body parts.

New vertices sample cubic PN patches built from the authored **mesh vertex normals**, so the dense surface gains real curvature between the original vertices. Original retained vertices remain fixed. Shared patch edges use common control points. Generated root caps stay linear, with shared boundary curves fixed, because their missing interior normals are not authored shape evidence. This implementation follows [Curved PN Triangles by Vlachos and colleagues](https://www.cs.jhu.edu/~misha/ReadingSeminar/Papers/Vlachos01.pdf).

The current density target is the mean physical triangle area of the AF Small chest and Medium B legs, independently for each slot. Conforming edge splits produce a conservative density: the generated reference is about 25 percent denser than this average target. This is an initial average density match; local AF density and AF connectivity are not matched yet. No AF shape displacement is applied to the vanilla reference.

This stage does not yet sample a normal-map texture. UVs, bone names and interpolated weights are retained alongside each triangle's source identity and barycentric coordinates. The OBJ is for geometry review; the NPZ also contains skinning and provenance. Covered regions retain the smallclothes envelope rather than inventing hidden naked anatomy.

### Reproduce locally

Requires Python and the packages in `requirements.txt`. Inputs are schema 1 MDL inspection snapshots with original MDL paths and SHA-256 hashes. The cleanup manifest is specific to the verified vanilla inputs and rejects other hashes. No game files are distributed in this branch.

Run from this folder in PowerShell:

```powershell
C:/Python310/python.exe reference_surface.py `
  --vanilla-top '../.work/uv-stock-lines-20261119/decoded/stock-top.json' `
  --vanilla-legs '../.work/uv-stock-lines-20261119/decoded/stock-dwn.json' `
  --af-top '../.work/resolver-preparation-20261118/bodies/af_top_01.json' `
  --af-legs '../.work/resolver-preparation-20261118/bodies/af_dwn_04.json' `
  --output outputs/vanilla-reference-v1
C:/Python310/python.exe render_review.py outputs/vanilla-reference-v1
C:/Python310/python.exe -m unittest -v
```

The local output folder contains the curved OBJ, a flat subdivision control with identical connectivity, a cleaned source OBJ, an NPZ with UVs and weights, comparison renders and a full numerical report. Generated meshes and game assets stay outside Git.

### Initial validation

The first cleaned reference contains 30,675 triangles and 15,383 geometric vertices. It has no reversed or degenerate triangles under the per-face orientation checks, no nonmanifold edges, and no cracks between shared PN patch edges within numerical precision. Its five open boundary loops are the neck, wrists and ankles. No retained original vertex moved during alias welding. Maximum curvature displacement from the flat control is 7.16 mm.

Regression tests cover planar preservation, curved shared-edge continuity, conforming density refinement and concave cap boundaries. Visual review compares the cleaned source, flat control and curved reference with geometry-derived shading. Global self-intersection, animation and physics validation remain future checks; triangle orientation checks do not certify these.

## Reusing the accepted wrap landmarks

The [Coatee chest investigation](coatee-chest-investigation.md) identifies equipment-owned breast skin missing from the stock smallclothes skin. It records a sharper geometric cleavage dip than the bra-based reference, and a proposed local reference update. That update has not been applied to the reference or these landmarks.

The [bottoms investigation](bottoms-investigation.md) compares stock briefs, Black Summer Tanga and No.2 Type B Leggings. The tanga is a close standard-vanilla comparison with verified local shared topology; 2B has substantially different seat and crotch geometry. Those findings are inspection results and do not modify the current body or point pairs.

`transfer_landmarks.py` reuses the 948 retained named point pairs from the earlier flap-free body-wrap work. The previously excluded flap points remain excluded: Point085, Point089, Point094 and Point134. AF Small chest and Medium B leg coordinates and triangle addresses stay unchanged, with Hip Dips and Soft Butt off.

Vanilla points are transferred through the old subdivision lineage to their native triangle material locations, then rebound to the new curved mesh's child triangles. Their positions follow the new curved geometry. Their corresponding material locations remain unchanged within numerical precision; no nearest-surface projection is used. Five points formerly addressed on removed faces are rebound at the exact same shared root boundaries: Point004, Point009, Point014, Point049 and Point054.

Run from this folder:

```powershell
C:/Python310/python.exe transfer_landmarks.py `
  --accepted-controls '../.work/flap-free-atlas-full-body-20261121/accepted-controls.npz' `
  --old-topology '../exports/Aetherform-ZWrap-inputs-20261119/ExportTopology.npz' `
  --old-lineage '../.work/density-only-body-grid-20261119/dense-source.npz' `
  --reference outputs/vanilla-reference-v1/vanilla-reference.npz `
  --output outputs/vanilla-reference-v1/landmarks
```

The output includes paired named point files, the unchanged AF target OBJ, a CSV, an NPZ and a transfer audit with input hashes. `Vanilla-Curved.points.json` belongs to `../vanilla-curved.obj`; `AF-Small-MediumB.points.json` belongs to `AF-Small-MediumB.obj`. Both selections are checked against their serialized OBJ triangle layouts. Point format is `name: [zero-based triangle index, barycentric A, barycentric B]`, with C equal to 1 minus A minus B.

For the planned **AF to vanilla** wrap, use AF as the floating mesh and the curved vanilla reference as the fixed mesh, with each mesh's matching point selection. This transfer stage prepares the correspondence; it does not run a wrap or change an existing Wrap project.

All 948 pairs transfer successfully. Maximum material-location error is below 0.000001 mm. Source points move a median 1.11 mm and a maximum 5.93 mm as they follow the reconstructed curvature. They lie on the exported mesh, whose chord approximation differs from the continuous PN patches by up to 1.10 mm at these landmarks. The four omitted identities are still omitted, and no new point pairs are introduced.

