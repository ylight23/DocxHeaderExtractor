# Accuracy-99 General Heading Observability

Status: NOT_YET_MEASURABLE

This campaign measures heading detection and structure against reviewed Human Gold. The evaluator
does not promote .key, model output, or other silver artifacts to gold.

The structural profile is deterministic and records providerCalls = 0. The product profile is
reported as NOT_MEASURED until an explicitly configured provider run is authorized.

## Workflow

1. dhx accuracy99 packet <file.docx> --out <packet.json> exports parser-owned source facts only.
2. A reviewer annotates the packet into a human_gold artifact with exhaustive source labels.
3. dhx accuracy99 inventory <dataset-root> --out <manifest.json> classifies sources and freezes
   document-level DEV and BLIND_HOLDOUT membership without assigning unlabeled files.
4. dhx accuracy99 baseline <file.docx> --profile structural --out <baseline.json> records the
   current deterministic production result.
5. dhx accuracy99 evaluate <file.docx> --accuracy-gold <gold.json> --prediction <outline.json>
   measures source-joined detection, span, level, parent, and hierarchy metrics.

Accuracy is claimable only when reviewed Human Gold exists and the unrounded precision and recall
meet the configured threshold. Until then the only valid status is NOT_MEASURED.
