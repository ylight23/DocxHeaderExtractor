# R4-13 Canonical Full Suite

Status: `BLOCKED`

## Revisions

- Round-3 canonical execution: `32bb000343361516078f37da63943e5073b678fe`
- R4-13 execution: `eb517ab11f427ff60f109573047609809963709b`
- Raw TRX: kept outside the repository; raw runtime artifacts are not published.

## Execution

The suite was run without a filter or exclusion in Release configuration.

```text
TOTAL   = 807
PASSED  = 804
FAILED  = 3
SKIPPED = 0
```

The isolated stale-contract test failed before the remaining assertions. A deterministic normal CLI run of the same fixture produced:

```text
route           = docx-authority-v1
heading count   = 0
old expectation = auto:pdf-toc-dictionary, 24 headings
```

Therefore this is not proven to be a route-only stale contract. The test expectation was not changed.

## Inventory Reconciliation

The Round-3 execution recorded `1338` test cases and `1328` unique test-definition names in its TRX. The current execution recorded `807` test cases and `801` unique test-definition names. The six-case and ten-case differences are parameterized/runtime case expansion, not an assumption that a removed test passed.

Exact FQN comparison:

```text
UNCHANGED                  = 796
RETIRED_BY_DELETED_FILE    = 514
MIGRATED_OR_RENAMED_PROVEN = 0
UNRESOLVED_RENAME CANDIDATES = 18
ADDED                      = 5
INVENTORY_UNACCOUNTED      = 18
```

The 514 deleted-file entries have retirement evidence in the R4-8/R4-9/R4-10 retirement commits. The 18 entries in retained or modified test files do not yet have an exact replacement FQN and coverage mapping, so they remain unresolved rather than being counted as migrated.

## Failure Reconciliation

All three current failures join by FQN to the Round-3 failure universe. Two retain the same normalized fingerprint (`C1` and `N15`). The PDF 054 test has a changed fingerprint because the current route is `docx-authority-v1`; its semantic output equivalence is not established.

```text
BASELINE_FAILURE_UNIVERSE       = 30
STILL_FAILING_SAME_FINGERPRINT  = 2
CHANGED_FINGERPRINTS            = 1
RETIRED_OR_UNRESOLVED_FAILURES  = 27
RESOLVED_BY_BEHAVIOR            = 0
NEW_FAILURES                    = 0
FAILURE_UNJOINED                = 27
```

The 27 absent baseline failure FQNs are not called resolved. Their retirement/replacement coverage must be mapped before the failure universe can be frozen for R4-13.

## Other Gates

- Static banned legacy references: `0`
- R4-12 diagnostic parity carried forward: `3/3`, delta `0`
- R4-12 PDF parity carried forward: `3/3`, delta `0`
- R4-12 host fingerprint carried forward unchanged
- Provider calls: `0` observed by the deterministic host/focused gates
- Expected changes: `false`
- Full suite: executed; process exit code was `1` because of the three failures

## Decision

`R4-13 = BLOCKED`.

`R4-14` is not authorized. Do not migrate the PDF 054 expected route, freeze the reduced failure universe, or merge `main` until the semantic contract and inventory replacement coverage are adjudicated.
