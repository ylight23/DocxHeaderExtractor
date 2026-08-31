# R4 Native Runtime Behavior Reconciliation

Status: `BLOCKED_DELTA_UNCLASSIFIED`

R4-6R has now produced all 12 required snapshots from independent clean
worktrees. Corpus provenance is valid, but semantic parity is not yet closed:
diagnostic has 3 first divergences and PDF has 1 first divergence at
`pdf.alignment` for fixture 056. The measured closure is recorded in
`eval/reconciliation/r4-behavior-comparison.v1.json`.

This gate keeps the structural R4-6 result separate from behavioral parity.
The structural result is already closed as PASS:

```text
R4-6_STRUCTURAL = PASS
AUTHORITY_NORMAL_RUNTIME_LEGACY_DEPENDENCY = 0
PDF_PRODUCTION_SLIM_CREATION = 0
PDF_MODULE_RUNTIME_SLIM_REFS = 0
```

## Revision authority

```text
diagnosticBaselineRevision = 3b350543d3f6b88d074553915169d84587fecf00
pdfBaselineRevision = a920b2adfc0a7e6caa8eea3c2d93fb63067b530c
executionRevision = 9b8a91bd75966d86eecbf32648c572d3ff0d57da
```

The diagnostic comparison is `3b35054 -> 9b8a91b`. The PDF comparison is
`a920b2a -> 9b8a91b`. No full suite was run and no provider was called.

## Executed evidence

The current revision focused deterministic superset passed `48/48`. The
diagnostic baseline focused filter passed `2/2`, and the PDF baseline focused
filter passed `46/46`. These are build/test-health checks only. They do not
emit the same corpus-level normalized records required by this gate.

The exporter now produces revision- and corpus-provenance-bound snapshots,
including source-only visual reconciliation with no VLM calls. The comparator
reports the earliest divergence rather than collapsing downstream differences.

## Gate result

```text
DIAGNOSTIC_RESULT_DELTA = BLOCKED_DELTA_UNCLASSIFIED (3 fixtures)
PDF_CANDIDATE_SELECTION_DELTA = 0 (3 fixtures)
PDF_ALIGNMENT_TARGET_DELTA = BLOCKED_DELTA_UNCLASSIFIED (fixture 056)
PDF_HEADING_SPAN_DELTA = BLOCKED_DELTA_UNCLASSIFIED (downstream)
PDF_VISUAL_MAPPING_DELTA = 0 (3 fixtures)
PDF_VALIDATED_STRUCTURE_DELTA = BLOCKED_DELTA_UNCLASSIFIED (downstream)
PDF_OUTPUT_DELTA = BLOCKED_DELTA_UNCLASSIFIED (downstream)

EXPECTED_CHANGED = false
BENCHMARK_EXPECTED_CHANGED = false
PROVIDER_CALLS = 0
```

Classification: `UNCLASSIFIED` pending adjudication of the measured first
divergences. The results are now measurable; expected outputs must not be
rebased and no delta is authorized as zero except where explicitly listed.

## Decision

```text
R4-6_STRUCTURAL = PASS
R4-6_BEHAVIOR_RECONCILIATION = BLOCKED_DELTA_UNCLASSIFIED
R4-6 = BLOCKED_FOR_BEHAVIORAL_CLOSURE
R4-7 = NOT_AUTHORIZED
```

Before R4-7, add or run one deterministic comparator against a pinned input
corpus and identical options. It must serialize normalized diagnostics and PDF
observables, report the first divergence stage per input, and classify every
delta as `MIGRATION_BUG`, `PREEXISTING_NONDETERMINISM`, or
`INTENTIONAL_CORRECTION`.
