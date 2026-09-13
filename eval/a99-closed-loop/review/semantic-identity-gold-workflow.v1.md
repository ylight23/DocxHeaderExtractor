# A99 Semantic Identity Gold — Independent Pilot Workflow

Status: `READY_FOR_INDEPENDENT_HUMAN_ANNOTATION`

This lane creates independent Gold for occurrence-to-semantic-node identity. It is separate from DOC-0205 development artifacts and does not authorize a provider benchmark by itself.

## Scope

The pilot is defined by `identity-gold-pilot-manifest.v1.json`:

- 4 documents, 3 source families, 1,567 source occurrences.
- Source and packet hashes are copied from the sealed generalization holdout manifest.
- Selection uses only manifest/source metadata. It does not use model output, candidate output, residual labels, or Gold.

The remaining holdout documents stay reserved and immutable.

## Human annotation contract

Review the complete source-only packet for each selected document. For every packet occurrence, assign:

- `HEADING`, `NOT_HEADING`, or `UNRESOLVED` membership;
- for a heading, a document-local `identityGroupId`;
- `PRIMARY`, `REPEAT`, or `CONTINUATION` occurrence relation;
- for `CONTINUATION`, an explicit backward `continuationOfOccurrenceId`.

The reviewer may use source text, document order, numbering, style, layout, and surrounding parser-owned evidence. Those are evidence for human semantic judgment, not automatic merge rules.

Do not hand-annotate every pair. After annotation is frozen, derive pair relations deterministically:

- different resolved groups → `DISTINCT`;
- explicit continuation pointer → directed `CONTINUATION_OF`;
- same resolved group without a continuation edge → `SAME_SEMANTIC_REPEAT`;
- any unresolved endpoint → `UNKNOWN`, excluded from strict scoring.

`identityGroupId` represents a semantic node. Physical occurrences remain separate rows with their own source identity and provenance.

## Blindness firewall

Annotators and the adjudicator must not see or use:

- model predictions or verifier responses;
- candidate pair sets or promotion decisions;
- DOC-0205 residual/error labels;
- old hierarchy output or current benchmark scores.

The packet must be source-only. No model or provider call is part of annotation.

## Freeze and adjudication

1. Human A reviews all selected documents and freezes one file per document.
2. Human B independently reviews the same source packets and freezes a separate set.
3. Validate document/source/packet hashes, occurrence identity, spans, ordering, group references, and continuation direction.
4. Compare A and B only after both passes are frozen.
5. A human adjudicator resolves disagreements using source evidence only. Do not union annotations automatically.
6. Freeze `identity-gold.v1.json` with hashes for A, B, adjudication, and the final derived relation files.
7. Only after this freeze may candidate generation, pair verification, promotion evaluation, or model output inspection begin.

If human evidence is insufficient, keep the affected row or relation `UNRESOLVED`; do not force a merge. A strict benchmark must exclude unresolved endpoints from its denominator and report the exclusion.

## Evaluation after Gold freeze

Evaluate separately:

- candidate recall;
- verifier positive precision/recall;
- promotion precision/recall;
- false-merge and false-split rates;
- component explosion;
- multiple continuation parents;
- cycle rate.

Any rule that changes `MODEL_PROPOSED` to `ACCEPTED_FOR_IDENTITY_COLLAPSE` must be evaluated on this independent pilot. DOC-0205 remains diagnostic/development-exposed and cannot establish generalization.

Oracle-normalized hierarchy runs, if later requested, must be labeled `GOLD_DERIVED_INPUT=true`, `ORACLE_COUNTERFACTUAL=true`, and `PRODUCTION_CLAIM=false`.
