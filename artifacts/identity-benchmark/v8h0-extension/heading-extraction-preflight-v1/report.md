# V8H0 — upstream heading extraction + binding preflight

Status: **READY_FOR_V8H0_HEADING_PROVIDER_AUTHORIZATION**.

This artifact is source-only and provider-free. It reuses the existing PDF production role/span contract and freezes a complete parser line universe plus deterministic role/pointer request shards. No Gold, identity candidates, historical predictions, or evaluation artifacts were read.

- Documents: **3**
- Parser source lines preserved: **10,034**
- Production candidate blocks before budget: **1,094**
- Selected analyst blocks: **120**
- Role requests: **15**
- Conditional pointer-span request upper bound: **30**
- Provider/model calls: **0/0**
- Gold reads: **0**

## Reused detector

`PdfLineExtraction → PdfLineBlockFilter → PdfSemanticBlockGrouper → PdfStyleClusterProfile → PdfCandidateContextBuilder → PdfCandidateRanker → PdfLayoutEvidenceOutline.BuildBroadCandidates/SelectRankedCandidates → PdfBlockAnalyst role/span contract`. The model sees only local opaque block refs (`b1`, `b2`, ...); canonical source line identities remain harness-owned handle-map data.

## Binding boundary

Role output may select a supplied block ref. Pointer output may select only parser-owned UTF-16 offsets. The harness owns exact source text, source line IDs, and provenance. Unknown/fabricated/missing/duplicate refs are fail-closed. Level, parent, semantic identity, and identity pair labels are outside this lane.

## Offline contract tests

13/13 synthetic binding checks passed.

## Next

Provider authorization may be considered for the frozen role request set. Pointer-span requests are conditional upper-bound shards and must only run for role-selected blocks; no V8H0 prediction is present yet.
