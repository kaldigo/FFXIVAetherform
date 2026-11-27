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

Use the base mesh, mesh normals and decoded normal-map information as shape evidence. Combine this with material, connectivity, skinning and author-defined behaviour. Normals inform shape; classifications define permitted deformation.

- Rigid plates and attached gems: preserve dimensions and shape while following their supporting region by translation and rotation.
- Curved armour shells: preserve intended curvature and sharp boundaries.
- Flexible cloth: allow smooth bending with enough geometry for body curvature and articulation.
- Directional details such as flowers: allow bending in specified directions while protecting the profile and attachment offset.
- Seams, openings, thickness and layered attachments: retain their construction relationships.

Simplification must preserve both surface shape and deformation. A flat area may still need vertices where bone influences change. Preserve intended movement and physics, transfer weights deliberately, and respect each model's supported influence count.

## Initial evidence

On 27 November 2026, the user reported trying this method on their current refit with promising results. The garment, assets and exact steps are to be captured from that experiment; no measured outcome has been recorded here yet.

## Next work

- Capture the current successful experiment, including garment, source body, AF target and authoring steps.
- Establish the paired body references and verify their correspondence.
- Compare original and rebuilt garments in the unchanged source shape before applying the target morph.
- Evaluate constraints, silhouette, contacts, folding and intersections across intended AF presets.
- Validate animation and physics, including bone-weight transitions and rigid attachments.
- Prepare reusable meshes, bindings and constraints offline; measure runtime costs before deciding what belongs in automatic refits.

