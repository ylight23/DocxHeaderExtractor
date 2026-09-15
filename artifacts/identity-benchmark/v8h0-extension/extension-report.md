# V8H0-EXT — new external source heading preflight

Status: **READY_FOR_V8H0_EXTENSION_HEADING_PROVIDER_AUTHORIZATION**.

This is a source-only, provider-free extension cohort. Three documents from three new public source families passed the existing provenance, readability, and duplicate firewall. The existing six-document V8H0 cohort was not mutated; its two excluded documents remain excluded.

- New eligible documents: **3**
- Existing valid bound-heading documents: **4**
- Potential combined document count after valid extension binding: **7**
- Current combined holdout status: **BLOCKED_ON_EXTENSION_HEADING_EXECUTION**
- Parser source lines: **10,034**
- Production candidate blocks: **1,094**
- Selected analyst blocks: **120**
- Role requests: **15**
- Conditional pointer-span request upper bound: **30**
- Exact request bytes: **306,198**
- Estimated input tokens: **76,565**
- Maximum request: **14,709 bytes / 3,678 estimated tokens**
- Provider/model calls: **0/0**
- Gold/evaluation reads: **0**

The same production detector and binding contract are reused: `PdfLineExtraction → PdfLineBlockFilter → PdfSemanticBlockGrouper → PdfStyleClusterProfile → PdfCandidateContextBuilder → PdfCandidateRanker → PdfLayoutEvidenceOutline.BuildBroadCandidates/SelectRankedCandidates → PdfBlockAnalyst role/span`. No identity provider or identity candidate generation was run.

The existing four-document bound-heading authority remains the only downstream authority until this extension receives a separately authorized heading execution and document-level fail-closed binding.
