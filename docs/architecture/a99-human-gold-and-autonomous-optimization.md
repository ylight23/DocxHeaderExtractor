# A99 Human Gold And Optimization

This campaign is source-first and evaluation-only. The production extraction route is not changed
by packet generation, human review, gold validation, or gold import.

## Reference authority

The frozen split in `eval/a99-dataset/evaluation-splits.v1.json` is authoritative. The campaign
selects every eligible DOCX in `todo10_8/heading_corpus_95_word/` assigned to `DEV` or
`GENERALIZATION_HOLDOUT`; reserved groups are excluded without using predictions or correctness.

Each review packet contains one row for every parser-owned source paragraph, its exact full span,
text hash, neighboring text, and source style/numbering/layout facts. It contains no candidate,
prediction, confidence, validation, or final-output fields.

Raw packets and human gold live outside the repository:

- `C:\A99-Gold\packets\dev`
- `C:\A99-Gold\packets\holdout-sealed`
- `C:\A99-Gold\dev`
- `C:\A99-Gold\holdout-sealed`

The repository stores campaign manifests, packet hashes, schemas, and validation summaries.

## Gold contract

Human Gold is accepted only when the packet SHA and source SHA match, every source occurrence has
exactly one explicit `YES`, `NO`, or `UNSURE` row, spans remain source-bounded, and known parents
form an acyclic hierarchy. `UNSURE` is explicit but excluded from the corresponding denominator.
Silver labels, human keys, predictions, and source-derived references are never promoted silently.

The DEV importer never opens `holdout-sealed`. Holdout gold is validated only after a release
candidate is frozen.

## Measurement and optimization

No accuracy number is reported until exhaustive DEV Human Gold contains explicit negatives and
the required fields. Baselines use three independent repeats; accepted optimization iterations
must retain their provenance and improve the supported primary error population without hiding
family regressions. Production changes require focused tests, observability checks, Release build,
and the full-suite frozen-failure reconciliation before publication.

Current campaign status is recorded in `eval/a99-closed-loop/reference-sufficiency.v2.json` and
the v2 baseline/decision artifacts. The expected initial state is `HUMAN_REFERENCE_REQUIRED`,
not an accuracy claim.

## Early DEV positive-set v2

The early review campaign uses the same exhaustive source-first packets, but its HUMAN_GOLD
artifact stores only the positive heading set. A reviewer must explicitly certify that the entire
document was reviewed, that the heading set is exhaustive, and that no model or system prediction
was used. An optional `unsureSourceIds` list is retained for audit; any unresolved UNSURE prevents
final certification. Body paragraphs therefore do not need individual NO rows.

`accuracy99 early-dev-campaign` freezes a deterministic stratified DEV subset of 12-20 documents
using only family, source kind, and occurrence-count quantiles. It never consults predictions,
confidence, errors, or historical labels. The v2 reviewer writes files to `C:\A99-Gold\dev-v2`,
and `gold import-dev-v2` validates only the selected DEV documents. The 23 holdout packets remain
sealed until a release candidate is frozen.

For a validated positive set `G` and autonomous prediction set `P`, source-identity joins define
TP, FP, and FN. Role, level, exact span, parent, and hierarchy are measured separately so a
correct heading existence cannot hide an incorrect structural field. No metric is reported before
the early DEV gold certificate is complete.
