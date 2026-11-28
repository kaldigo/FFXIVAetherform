# Standalone underwear extraction trial

**Interpretation after review:** the lifts below compensate for an invalid inner-body estimate. Passing the adjusted-shell crossing checks is not a successful recovery of natural garment thickness or the core body. Keep this as a diagnostic trial. The [skin-anchored inner-envelope experiment](inner-envelope-notes.md) instead keeps the raw underwear fixed and estimates the body underneath it.

This trial uses the raw reconstructed 4x body and the preferred 6 mm shape-separated 4x body. The latter is unchanged. The normal-map relief becomes actual surface geometry on separate bra and briefs objects; no normal texture is applied in the review renders.

The existing smallclothes material regions supply the outline. Normal maps alone cannot recover garment boundaries, hidden surfaces, physical thickness or fastening. This is an extraction and static fitting experiment, not a finished wearable garment.

## Method

`extract_underclothes.py` selects the raw, already flap-free smallclothes regions. It exports their unadjusted relief surfaces as a control. Four disconnected triangles inferred during bra-root closure are excluded; no native garment triangles are discarded by that component filter.

Retained source vertex identities locate the corresponding separated body vertices. New root vertices project only onto the corresponding reconstructed root patch. The lining follows that body with an explicit outward allowance. The outer surface retains the raw relief with a uniform lift along the body contact normal field. This preserves the signed relief variations along that field, although changing normals across the surface means the lift can also change local slopes and curvature.

The lining allowance starts at 0.15 mm and increases until sampled clearance and triangle-crossing checks pass. The outer lift ensures at least 0.2 mm separation from the lining along the contact normal at vertices. This is **not** a constant physical thickness or a guaranteed minimum Euclidean wall thickness. Boundary rims join the two surfaces into closed shells.

UVs and named bone weights from the raw garment faces are retained on the outer surface. Lining UVs/weights follow reversed outer faces; rim UVs are generated and their weights use boundary vertex averages. OBJ files contain geometry and UVs; NPZ files also retain weights and provenance. Movement and physics have not been validated.

## Result

| Measurement | Bra | Briefs |
|---|---:|---:|
| Extracted outer triangles | 5,315 | 3,951 |
| Closed shell triangles | 11,168 | 8,440 |
| Lining vertex normal allowance | 1.405 mm | 0.300 mm |
| Outer normal lift from raw | 2.857 mm | 1.822 mm |
| Minimum sampled lining clearance | 0.106 mm | 0.165 mm |
| Body triangle crossings | 0 | 0 |
| Non-adjacent self-crossings | 0 | 0 |

The combined shells have 9,804 vertices and 19,608 triangles. Both have consistent winding, no nonmanifold edges, no open boundary edges and positive signed volume. Waist/leg openings are retained through the joined inner/outer rim geometry.

The raw surface sometimes falls inside the separated body: minimum signed contact gaps are -1.252 mm for the bra and -1.322 mm for the briefs. Thus the raw-minus-separated difference cannot simply be treated as a positive cloth-thickness field. The fairly large bra lift also reflects triangulation and the difficult medial root region. It is a conservative test fit, not recovered garment thickness.

Visual review shows recognisable standalone cups, lower band and briefs, with relief present under geometry-only lighting. Material-region boundaries remain visibly jagged, especially at the medial bra edge and rear waistband. Those outlines need separate finishing if this becomes an actual garment; they have not been smoothed or redesigned in this trial.

`audit_underclothes.py` checks all shell triangles against the full body and checks self-crossings excluding pairs sharing vertices. Its spatial search is complete for overlapping triangle AABBs and includes coplanar overlap checks. These static checks do not certify animation, nearby folds between adjacent faces, physics or sewing/fastening construction.

## Reproduce

After generating the raw and 6 mm separated 4x bodies:

```powershell
C:/Python310/python.exe extract_underclothes.py
C:/Python310/python.exe audit_underclothes.py outputs/underclothes-extraction-4x
C:/Python310/python.exe render_underclothes.py outputs/underclothes-extraction-4x
C:/Python310/python.exe -m unittest -v
```

Local outputs under `outputs/underclothes-extraction-4x/` include separate bra/briefs shell OBJ and NPZ files, combined shells, unadjusted relief surfaces, extraction and crossing reports, and side-by-side geometry-only renders. Game-derived outputs remain ignored by Git.
