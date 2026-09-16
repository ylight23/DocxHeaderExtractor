# Canonical Batch V3.2 — Corrective Adjudication

Status: **BATCH_V3_2_READY_FOR_USER_SEMANTIC_REVIEW**

Baseline: **a5ac90b**; review evidence: **1d87d50**; prior proposal: **fb60411**.

No frozen Gold was mutated. No provider/model calls. Historical level/parent/semantic totals were not read.

## Corrections

- Exact heading spans are re-materialized from source containers; body-clause tails are audited.
- Parent selection uses an explicit structural scope stack and records NO_PARENT_MAY_CROSS_AN_INTERVENING_STRUCTURAL_RESET.
- DOC-0265 and DOC-0201 source-window checks are recorded in document-review.json.
- DOC-0243 reviews lexical and structural-adjacency/composite candidates; chapter/part label-title pairs are source-backed continuation relations.
- Four former blockers run under WHOLE_DOCUMENT_HEADING_SCOPE_V1; scope labels are metadata, not authority.

## Per-document review

### DOC-0185

- 119 -> 119 occurrences
- 119 -> 119 semantic nodes
- 119 -> 119 parent edges
- ROOT children: 11; max depth: 3
- identity challenges: 32
- validator: {"cycles":0,"dangling":0,"parentEdgesEqualSemanticNodes":true,"semanticNodesNotGreaterThanOccurrences":true}

### DOC-0200

- 102 -> 102 occurrences
- 102 -> 102 semantic nodes
- 102 -> 102 parent edges
- ROOT children: 7; max depth: 3
- identity challenges: 14
- validator: {"cycles":0,"dangling":0,"parentEdgesEqualSemanticNodes":true,"semanticNodesNotGreaterThanOccurrences":true}

### DOC-0201

- 11 -> 11 occurrences
- 11 -> 11 semantic nodes
- 11 -> 11 parent edges
- ROOT children: 11; max depth: 1
- identity challenges: 1
- validator: {"cycles":0,"dangling":0,"parentEdgesEqualSemanticNodes":true,"semanticNodesNotGreaterThanOccurrences":true}

### DOC-0219

- 0 -> 337 occurrences
- 0 -> 337 semantic nodes
- 0 -> 337 parent edges
- ROOT children: 337; max depth: 1
- identity challenges: 206
- validator: {"cycles":0,"dangling":0,"parentEdgesEqualSemanticNodes":true,"semanticNodesNotGreaterThanOccurrences":true}

### DOC-0265

- 245 -> 248 occurrences
- 245 -> 248 semantic nodes
- 245 -> 248 parent edges
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

- 0 -> 174 occurrences
- 0 -> 174 semantic nodes
- 0 -> 174 parent edges
- ROOT children: 166; max depth: 2
- identity challenges: 125
- validator: {"cycles":0,"dangling":0,"parentEdgesEqualSemanticNodes":true,"semanticNodesNotGreaterThanOccurrences":true}

### DOC-0122

- 0 -> 345 occurrences
- 0 -> 345 semantic nodes
- 0 -> 345 parent edges
- ROOT children: 345; max depth: 1
- identity challenges: 215
- validator: {"cycles":0,"dangling":0,"parentEdgesEqualSemanticNodes":true,"semanticNodesNotGreaterThanOccurrences":true}

### DOC-0216

- 0 -> 343 occurrences
- 0 -> 343 semantic nodes
- 0 -> 343 parent edges
- ROOT children: 343; max depth: 1
- identity challenges: 220
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
