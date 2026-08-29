# R4-6R Producer Parity Closure

Status: `PASS`

This closure records the P1-P6 native producer migration and the deterministic
same-corpus reconciliation. Docling remains outside the native diagnostic
pipeline and is covered by the separate retire audit.

## Revision authority

```text
diagnosticBaselineRevision = 3b350543d3f6b88d074553915169d84587fecf00
pdfBaselineRevision = a920b2adfc0a7e6caa8eea3c2d93fb63067b530c
initialNativeBehaviorRevision = 9b8a91bd75966d86eecbf32648c572d3ff0d57da
measurementToolRevision = e5bfda4e70976a623a169909ed48a056bb97ad9d
behaviorFixExecutionRevision = dfe9c5ff4f3b96d19cd5d49ee97496e372c50927
```

The initial native revision remains preserved as the revision that exposed the
divergence. It is not relabeled as the fixed revision.

## Implementation closure

P1-P3 use one shared native paragraph core with Slim adapters only at the
legacy boundary. P4 preserves the typed numbering and part-section signal on
the native path. P5 and P6 use shared analyzer cores with native paragraph
inputs; RFC thresholds and diagnostics are unchanged. The diagnostic runner
now orchestrates the exact P1-P6 producers and does not synthesize candidates.

The native producer parity tests and the final focused diagnostic/P4 group
passed. The Release solution build passed with zero errors. No full suite was
run and no provider was called.

## Reconciliation

Diagnostic snapshots for fixtures 028, 056, and 091 were compared from
`3b35054` to the behavior-fix execution revision. PDF snapshots for the same
corpus were compared from `a920b2a` to that same revision. Both comparisons
used identical corpus provenance and deterministic execution.

```text
diagnostic: joined=3, deltas=0, gate=PASS
pdf:        joined=3, deltas=0, gate=PASS

DIAGNOSTIC_RESULT_DELTA = 0
PDF_CANDIDATE_SELECTION_DELTA = 0
PDF_ALIGNMENT_TARGET_DELTA = 0
PDF_HEADING_SPAN_DELTA = 0
PDF_VISUAL_MAPPING_DELTA = 0
PDF_VALIDATED_STRUCTURE_DELTA = 0
PDF_OUTPUT_DELTA = 0

UNMEASURED = 0
EXPECTED_CHANGED = false
BENCHMARK_EXPECTED_CHANGED = false
PROVIDER_CALLS = 0
```

## Decision

```text
R4-6_STRUCTURAL = PASS
R4-6R = PASS
R4-6 = PASS
R4-7 = AUTHORIZED
```

The legacy physical-removal work remains a later migration step. This gate
authorizes compatibility-caller migration; it does not authorize changing
expected outputs or deleting Slim APIs before their remaining callers are
classified.
