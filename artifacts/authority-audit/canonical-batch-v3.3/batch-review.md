# Canonical Batch V3.3 — Corrective Adjudication

Status: **BATCH_V3_3_READY_FOR_USER_SEMANTIC_REVIEW**

Baseline: **6b6637e**.

No frozen Gold was mutated. No provider/model calls. Historical level/parent/semantic totals were not read.

## Corrections

- Marker matching tolerates source-faithful spacing loss without rewriting source text.
- Every residual body-text risk is emitted with exact accepted spans and excluded body ranges.
- Procurement style alone never creates a true heading; structural hierarchy uses explicit PART/SECTION/subsection evidence.
- Composite identity merges preserve/rebind parent structure and actual edge diffs are recorded.

## Per-document review

### DOC-0185

- 119 -> 119 occurrences
- 119 -> 119 semantic nodes
- 119 -> 119 parent edges
- ROOT children: 11; max depth: 3
- identity challenges: 32
- validator: {"cycles":0,"dangling":0,"parentEdgesEqualSemanticNodes":true,"semanticNodesNotGreaterThanOccurrences":true}

### DOC-0200

- 102 -> 105 occurrences
- 102 -> 105 semantic nodes
- 102 -> 105 parent edges
- ROOT children: 7; max depth: 3
- identity challenges: 17
- validator: {"cycles":0,"dangling":0,"parentEdgesEqualSemanticNodes":true,"semanticNodesNotGreaterThanOccurrences":true}

### DOC-0201

- 11 -> 12 occurrences
- 11 -> 12 semantic nodes
- 11 -> 12 parent edges
- ROOT children: 12; max depth: 1
- identity challenges: 1
- validator: {"cycles":0,"dangling":0,"parentEdgesEqualSemanticNodes":true,"semanticNodesNotGreaterThanOccurrences":true}

### DOC-0219

- 0 -> 243 occurrences
- 0 -> 243 semantic nodes
- 0 -> 243 parent edges
- ROOT children: 180; max depth: 2
- identity challenges: 139
- validator: {"cycles":0,"dangling":0,"parentEdgesEqualSemanticNodes":true,"semanticNodesNotGreaterThanOccurrences":true}

### DOC-0265

- 245 -> 249 occurrences
- 245 -> 249 semantic nodes
- 245 -> 249 parent edges
- ROOT children: 14; max depth: 3
- identity challenges: 174
- validator: {"cycles":0,"dangling":0,"parentEdgesEqualSemanticNodes":true,"semanticNodesNotGreaterThanOccurrences":true}

### DOC-0243

- 121 -> 129 occurrences
- 121 -> 109 semantic nodes
- 121 -> 109 parent edges
- ROOT children: 13; max depth: 3
- identity challenges: 83
- validator: {"cycles":0,"dangling":0,"parentEdgesEqualSemanticNodes":true,"semanticNodesNotGreaterThanOccurrences":true}

### DOC-0116

- 0 -> 120 occurrences
- 0 -> 120 semantic nodes
- 0 -> 120 parent edges
- ROOT children: 86; max depth: 2
- identity challenges: 74
- validator: {"cycles":0,"dangling":0,"parentEdgesEqualSemanticNodes":true,"semanticNodesNotGreaterThanOccurrences":true}

### DOC-0122

- 0 -> 294 occurrences
- 0 -> 294 semantic nodes
- 0 -> 294 parent edges
- ROOT children: 172; max depth: 3
- identity challenges: 176
- validator: {"cycles":0,"dangling":0,"parentEdgesEqualSemanticNodes":true,"semanticNodesNotGreaterThanOccurrences":true}

### DOC-0216

- 0 -> 295 occurrences
- 0 -> 295 semantic nodes
- 0 -> 295 parent edges
- ROOT children: 172; max depth: 3
- identity challenges: 183
- validator: {"cycles":0,"dangling":0,"parentEdgesEqualSemanticNodes":true,"semanticNodesNotGreaterThanOccurrences":true}

### DOC-0205

- 72 -> 72 occurrences
- 72 -> 72 semantic nodes
- 72 -> 72 parent edges
- ROOT children: 6; max depth: 3
- identity challenges: 70
- validator: {"cycles":0,"dangling":0,"parentEdgesEqualSemanticNodes":true,"semanticNodesNotGreaterThanOccurrences":true}

### DOC-0264

- 155 -> 155 occurrences
- 155 -> 155 semantic nodes
- 155 -> 155 parent edges
- ROOT children: 11; max depth: 3
- identity challenges: 167
- validator: {"cycles":0,"dangling":0,"parentEdgesEqualSemanticNodes":true,"semanticNodesNotGreaterThanOccurrences":true}


## V3.3 hard gates

Status: **BATCH_V3_3_READY_FOR_USER_SEMANTIC_REVIEW**

``json
{"unresolvedBodyTextRiskCount":0,"knownFalseHeadingRegressionFailures":0,"missingKnownHeadingRegressionFailures":0,"allRootWithExplicitHierarchyViolations":0,"compositeMergeParentLoss":0,"structuralResetViolations":0,"cycles":0,"multipleParents":0,"dangling":0,"unreachable":0}
``
