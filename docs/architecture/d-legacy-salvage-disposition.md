# D legacy salvage disposition

Base: 5a61dc0
Source worktree: D:\DocxHeaderExtractor

## Legacy probes

| Commit | Classification | Disposition |
|---|---|---|
| 15486f7 | legacy diagnostic probe + evidence | EVIDENCE_SALVAGE; PROBE_SUPERSEDED |
| 004e30b | legacy role reconstruction probe + evidence | EVIDENCE_SALVAGE; PROBE_OBSOLETE |
| 38ff5ab | legacy corrected accounting probe + evidence | EVIDENCE_SALVAGE; PROBE_SUPERSEDED |
| ad6adfc | legacy causal provenance probe + evidence | EVIDENCE_SALVAGE; PROBE_SUPERSEDED_BY_FINAL |
| 9c777f3 | final legacy diagnostic closure + evidence | FINAL_EVIDENCE_SALVAGE; PROBE_NOT_PORTED |

No `DocxHeaderExtractor.Core.Pipeline` compatibility layer will be restored.

The current `DocumentProcessing` pipeline owns candidate lineage, source identity,
semantic/span checkpoints, validator provenance, and closed semantic-role contracts.

## Remaining commits

- 0ee01d3: DOC_EVIDENCE — pending salvage
- 6d4700d: DOC_EVIDENCE — pending salvage
- 917b7b7: DOC_EVIDENCE — pending salvage
- 3d40f73: DOC_EVIDENCE — pending salvage
- c605927: DOC_EVIDENCE — pending salvage
- 6be8d75: EVIDENCE + GENERATOR — generator audit pending
- f70d2fb: EVIDENCE + GENERATOR — generator audit pending

## Final D3-D6 disposition

Source D HEAD: `c6059270dadf47d9a432ab7409a3f1bbbe4585c1`.

Historical artifacts are preserved byte-for-byte under
`archive/d-legacy/original/` and mapped by
`archive/d-legacy/blob-map.tsv`.

| Commit | Final disposition |
| --- | --- |
| 6be8d75 | EVIDENCE_ARCHIVED / GENERATOR_DROPPED_HEURISTIC |
| f70d2fb | EVIDENCE_ARCHIVED / GENERATOR_DROPPED_SUPERSEDED |
| 15486f7 | EVIDENCE_ARCHIVED / PROBE_SUPERSEDED |
| 004e30b | EVIDENCE_ARCHIVED / PROBE_OBSOLETE_ROLE_RECONSTRUCTION |
| 38ff5ab | EVIDENCE_ARCHIVED / PROBE_SUPERSEDED |
| ad6adfc | EVIDENCE_ARCHIVED / PROBE_SUPERSEDED_BY_FINAL |
| 9c777f3 | FINAL_EVIDENCE_ARCHIVED / PROBE_NOT_PORTED |
| 0ee01d3 | HISTORICAL_EVIDENCE_ARCHIVED / NOT_CURRENT_CONTRACT |
| 6d4700d | CANARY_V1_EVIDENCE_ARCHIVED |
| 917b7b7 | HANDOFF_AND_TODO_DROPPED_STALE |
| 3d40f73 | CANARY_V2_EVIDENCE_ARCHIVED |
| c605927 | CANARY_V3_EVIDENCE_ARCHIVED |

All 12 D commits now have a terminal disposition.

No production source from D is imported.
No legacy compatibility layer is restored.
