# R4-13R Canonical Full Suite Reconciliation

Status: `PASS`

## Revisions

- Round-3 canonical execution: `32bb000343361516078f37da63943e5073b678fe`
- R4-13R execution: `4b11d7b51c56edf963b5b249f12425732188af95`
- R4-13 blocked history: `c778fbf533731c68a9589c7acd3349da36171173`
- Raw TRX: retained outside the repository and not published.

## 054 First-Loss Adjudication

The direct native PDF TOC producer was measured on both revisions:

```text
Accepted             = true
Reason               = pdf=054_IBRD_Information_Statement_FY25.pdf, toc=24, pageAnchors=24, docxAligned=24
Probe.Entries        = 24
Probe.RelaxedAnchors = 24
Headings.Count       = 24
```

The heading texts, levels, spans, stable IDs, and `pdf_toc_dictionary` basis were identical between Round-3 direct producer and current direct producer. The normal pipeline measured `0` headings on both revisions (`pdf-first-authority-v1` on Round-3 and `docx-authority-v1` currently).

This is case `C`: the old normal-route expectation of 24 headings was never proven by the baseline. The route-specific test was migrated to a direct producer oracle; no PDF TOC producer was resurrected as an authority route.

## Current Execution

```text
TOTAL   = 807
PASSED  = 805
FAILED  = 2
SKIPPED = 0
```

The remaining failures are the known `C1` and `N15` failures with unchanged fingerprints. No provider was called.

## Inventory

The Round-3 TRX contains `1338` execution cases and `1328` unique test-definition names. The current TRX contains `807` execution cases and `801` unique names. The measured inventory is reconciled by exact FQN first, then explicit retirement or migration evidence:

```text
UNCHANGED                         = 795
RETIRED_BY_DELETED_FILE           = 514
RETIRED_IN_RETAINED_TEST_FILES   = 14
MIGRATED_OR_RENAMED              = 5
ADDED                             = 6
INVENTORY_UNACCOUNTED             = 0
UNPROVEN_MIGRATIONS                = 0
```

Retired entries have an owner revision and explicit retirement/coverage evidence. They are not counted as passed tests.

## Failure Reconciliation

```text
BASELINE_FAILURE_UNIVERSE          = 30
STILL_FAILING_SAME_FINGERPRINT     = 2
CHANGED_FINGERPRINTS               = 0
RESOLVED_BY_BEHAVIOR               = 0
RESOLVED_BY_AUTHORIZED_TEST_MIGRATION = 1
RETIRED_FAILURE_CASES_ACCOUNTED    = 27
NEW_FAILURES                       = 0
FAILURE_UNJOINED                   = 0
FAILURE_UNIVERSE_FROZEN            = true
```

## Gates

- Full current suite executed without filters or exclusions: `PASS`
- R4-12 diagnostic parity carried forward: `3/3`, delta `0`
- R4-12 PDF parity carried forward: `3/3`, delta `0`
- R4-12 host fingerprint unchanged
- Static banned legacy references: `0`
- Expected changes: `false`
- Provider calls: `0`

`R4-13 = PASS` and `R4-14 = AUTHORIZED`.
