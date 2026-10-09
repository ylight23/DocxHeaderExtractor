# P7-D2: Page Evidence Coverage Audit

Provider-free measurement of actual frozen control and B V2 provider-body JSON. Does not
change any request, schema, parser, store, Gold, or production code. Does not implement B-Spatial.
The script pins the existing preflight SHA, verifies all 16 provider-body hashes, compares
control JSON with B's stageInput, and checks every projected B source entry against the
rebuilt parser-owned evidence store. Output contains hashes, aliases and counts, not source text.

## Measured fixture scope

One previously reviewed development PDF, source SHA
`f427233dcdcd8fc9724c4133c6ef5082bc5105474d4f074442b2c63f76922318`.
Page 1 has 38 canonical occurrences; page 2 has 41. Not a multi-document qualification.

| Evidence | Parser/source retains | Store retains | Control F1 / G2A / H2-C | B F1 / G2A / H2-C |
| --- | --- | --- | --- | --- |
| Text + source identity | 79 occurrences | 79 | 79 / 19 / anchor tail | Same identities/scope as control |
| Native bbox | 79 | 79 | None / none / none (vertical summary is not bbox) | 79 / 19 / anchor tail |
| Same row / distinct columns | Derivable from physical bounds, not semantic truth | Raw bbox, no relation ontology | Insufficient horizontal geometry | Comparisons possible only between visible entries |
| Whole-page text context | All 79 canonical occurrences across two pages | All 79, no page grouping | Full owned pack / local neighbors / tail only | No scope expansion beyond control |
| Cross-page text context | Both source pages | Page + ordinal retained | Depends on issued pack or tail; not a semantic relation | Same visible scope, additional raw bbox |
| Glyph provenance | 79 lines; 2,114 span-map entries | NOT_AVAILABLE for all 79 | Absent | Explicitly NOT_AVAILABLE |
| Native layout block identity | LayoutBlockByAtom retained | Not projected | Absent | Absent |
| Page dimensions, raster/vector content | PDF reader has page access; adapter does not retain these | Absent | Absent | Absent |

All-source store hash does **not** mean the model receives the entire store.
All canonical text on a page does **not** mean complete visual-page evidence: images,
rules, whitespace regions, dimensions, and discarded parser content are not measured here.
Physical pair counts measure availability of raw evidence for comparisons, not assertions
that two occurrences actually occupy the same row or have different semantic functions.

| Request | Page 1 visible / 38 | Page 2 visible / 41 | B bbox count |
| --- | --- | --- | --- |
| F1 | 38 | 41 | 79 |
| G2A | 12 | 7 | 19 |
| H2C O1 | 38 | 41 | 79 |
| H2C O5 | 34 | 41 | 75 |
| H2C O39 | 0 | 41 | 41 |
| H2C O45 | 0 | 35 | 35 |
| H2C O46 | 0 | 34 | 34 |
| H2C O47 | 0 | 33 | 33 |

In particular, O45/O46/O47 have the relevant table-label bbox entries in B, but lack
the preceding page-level context. This does not prove that extra page context would
improve semantic admission or exact extent. Neither was scored in this audit.

## Reproduce

Run `dotnet run --project scripts/P7PageEvidenceCoverage/P7PageEvidenceCoverage.csproj -c Release`
with five positional arguments: repository root, reference PDF, private frozen control
directory, private B V2 request directory, new sanitized artifact path. Use an isolated
`--artifacts-path` inside the ignored `.verify-build` directory to avoid live Web DLL locks.
Private control/request files must remain locally available with their original hashes.
The tool rejects existing output files; reproducibility writes a separate output.

Gates remain closed: no provider calls, no Gold read/mutation, no new semantic authority,
no overlap pruning, no F1-OTHER veto, no runtime promotion. Freeze a multi-document cohort
and scoring policy only after reviewing these gaps; B-Spatial would be a separately
versioned treatment, not a silent addition to the existing B requests.
