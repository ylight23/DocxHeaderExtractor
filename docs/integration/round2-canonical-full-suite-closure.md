# Round-2 Canonical Full Suite Closure

INT-4 is closed after a clean canonical rerun. The initial full-suite result is retained as historical evidence but is not canonical because its environment was contaminated by runtime inventory state.

| Gate | Status |
|---|---|
| Initial INT-4 | `NON_CANONICAL_ENVIRONMENT_CONTAMINATED` |
| INT-4A | `RESOLVED_RUNTIME_INVENTORY_CONTAMINATION` |
| INT-4B | `REPRODUCED_BASELINE_SEMANTIC_FAILURE` |
| INT-4C | `PASS` |

Canonical execution revision: `952e3ceadc5079c8f46c8afff0c8366f5ec0490b`

Canonical full suite: `1338 total / 1308 passed / 30 failed / 0 skipped`

Reconciliation: `newFailures = 0`, `changedFingerprints = 0`, `unjoined = 0`.

The failure universe is frozen. The clean C1 reproduction joined the baseline semantic failure at assertion line 71; no production or expected-test change was made, and no provider calls occurred. Fingerprint rebase is not authorized.

Raw TRX and execution logs remain local per evidence policy. Their SHA256 values are recorded in the INT-4B and INT-4C JSON evidence summaries.

Status: `INT-4 = PASS`.
