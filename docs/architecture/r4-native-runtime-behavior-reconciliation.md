# R4 Native Runtime Behavior Reconciliation

Status: `BLOCKED`

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

The repository has no committed comparator or replay manifest that produces,
for both revisions, the required normalized fields for StyleSignal,
LayoutSignal, CandidateDiagnostics, PDF retrieval, grounding, visual mapping,
validation, and product output. The focused test counts therefore cannot prove
`DIAGNOSTIC_RESULT_DELTA = 0` or any PDF delta is zero.

## Gate result

```text
DIAGNOSTIC_RESULT_DELTA = BLOCKED
PDF_CANDIDATE_SELECTION_DELTA = BLOCKED
PDF_ALIGNMENT_TARGET_DELTA = BLOCKED
PDF_HEADING_SPAN_DELTA = BLOCKED
PDF_VISUAL_MAPPING_DELTA = BLOCKED
PDF_VALIDATED_STRUCTURE_DELTA = BLOCKED
PDF_OUTPUT_DELTA = BLOCKED

EXPECTED_CHANGED = false
BENCHMARK_EXPECTED_CHANGED = false
PROVIDER_CALLS = 0
```

Classification: `MIGRATION_BUG` / reconciliation infrastructure gap. The
native migration changed the diagnostic and PDF call surfaces, but no shared
corpus serializer/replay comparator exists to establish semantic identity.
Expected outputs must not be rebased and no `DELTA = 0` claim is authorized.

## Decision

```text
R4-6_STRUCTURAL = PASS
R4-6_BEHAVIOR_RECONCILIATION = BLOCKED
R4-6 = BLOCKED_FOR_BEHAVIORAL_CLOSURE
R4-7 = NOT_AUTHORIZED
```

Before R4-7, add or run one deterministic comparator against a pinned input
corpus and identical options. It must serialize normalized diagnostics and PDF
observables, report the first divergence stage per input, and classify every
delta as `MIGRATION_BUG`, `PREEXISTING_NONDETERMINISM`, or
`INTENTIONAL_CORRECTION`.
