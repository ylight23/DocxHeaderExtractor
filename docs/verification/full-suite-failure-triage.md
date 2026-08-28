# Full-Suite Failure Triage

Status: `EVIDENCE_INSUFFICIENT`

The reachable repository history proves only that the full suite had 35 failures and that the
failure count was reported as matching the pre-checkpoint baseline. It does not retain the
occurrence-level failure packet required to classify those failures.

The missing authority is the exact test name, test file, assertion, expected/actual values, and
relevant stack frame for each failure. Aggregate counts cannot distinguish a stale expectation from
a real production invariant violation, a diagnostic contract mismatch, or an environment failure.

Accordingly all 35 remain `UNKNOWN` in the ledger. No test expectation was changed, no production
code was changed, and no provider was called. This is deliberately not a claim that all 35 are
historical or stale.

## Required Input

Provide or commit the baseline-matching TRX/JSON failure packet with the 35 fully qualified test
identities. Once present, triage can group identical root causes and classify each failure without
re-running or modifying the benchmark contract.

Artifact: `eval/verification/full-suite-failure-ledger.v1.json`.
