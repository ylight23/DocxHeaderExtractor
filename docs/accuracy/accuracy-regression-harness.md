# Accuracy Regression Harness V1

The harness contract defines a deterministic, source-occurrence-first comparison for accuracy
changes. It does not call a provider and does not modify production extraction behavior.

## Stage Ledger

Every reviewed occurrence is traced through:

`SOURCE -> GENERATED -> SELECTED -> ROLE -> POST_CONFLICT -> SPAN -> VALIDATED -> GROUNDED -> EMITTED`

The evaluator assigns exactly one first loss. A missing stage observation is recorded as
`NOT_OBSERVABLE`; it is not converted into a failure claim.

## Authority

The stable identity is:

`documentSha256 + sourceLineIds + occurrenceId`

`candidateId` is run-local diagnostic linkage only. Duplicate heading text therefore remains
separate when its source occurrence identity differs.

Unreviewed candidates are not treated as false positives. False-positive claims require a reviewed
non-heading denominator.

## Comparison

The report keeps generation, selection, role, conflict, span, validation, grounding, and output
recall deltas separate. Recovered and displaced reviewed occurrences are reported independently;
the net gain is only their difference. Candidate population and selected population changes are
reported separately from reviewed recall.

The test-layer contract probe includes deterministic fixtures for duplicate text, candidate ID
renaming, `NOT_OBSERVABLE`, zero-delta identity comparisons, and recovered/displaced accounting.

Artifact: `eval/regression/accuracy-regression-contract.v1.json`.

Current verification note: the full test project is presently blocked by pre-existing compile
errors in `PdfEMarkerOnlyPolicyAuditProbe.cs`, outside this task. No provider calls or production
changes were made by F.
