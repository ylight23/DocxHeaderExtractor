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
