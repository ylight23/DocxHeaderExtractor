# INT-4C Clean Canonical Full Suite

Status: `PASS`

Execution revision: `952e3ceadc5079c8f46c8afff0c8366f5ec0490b`

## Context

The initial INT-4 full-suite run is retained as historical evidence but is not canonical for the gate:

```ini
previousFullSuite.status = NON_CANONICAL_ENVIRONMENT_CONTAMINATED
previousFullSuite.results = 1338 / 1308 / 30 / 0
previousFullSuite.changedFingerprints = 1
previousFullSuite.regressionProven = false
```

INT-4A and INT-4B resolved the changed fingerprint as runtime inventory contamination:

```ini
INT-4A = RESOLVED_RUNTIME_INVENTORY_CONTAMINATION
INT-4B = REPRODUCED_BASELINE_SEMANTIC_FAILURE
REGRESSION_PROVEN = false
FINGERPRINT_REBASE_AUTHORIZED = false
PRODUCTION_CHANGE = none
TEST_EXPECTED_CHANGE = none
```

## Clean Run Contract

- HEAD matched `952e3ceadc5079c8f46c8afff0c8366f5ec0490b`.
- `.verify-build` was absent before and after the full suite.
- Runtime inventory was snapshotted before and after the run.
- No inherited `TestResults` were present before execution.
- Release build passed before test execution.
- Raw TRX was retained locally and must not be pushed.
- No remediation was performed.
- Provider calls: `0`.

## Result

```ini
TOTAL = 1338
PASS = 1308
FAIL = 30
SKIP = 0

NEW_FAILURES = 0
CHANGED_FINGERPRINTS = 0
UNJOINED = 0
RESOLVED = 0
```

The Round-2 failure universe is frozen by this clean run.

```ini
FULL_SUITE_VALID_FOR_FREEZE = true
FAILURE_UNIVERSE_FROZEN = true
INT-4 = PASS
```

## C1 Probe

The investigated FQN joined back to the baseline semantic failure:

```text
DocxHeaderExtractor.Tests.PdfC1CrossDocumentRegressionInventoryProbe.IndependentPartialTimeoutEvidenceNowExistsAndWasConsumedByN35
```

Assertion line:

```text
PdfC1CrossDocumentRegressionInventoryProbe.cs:71
```

Normalized failure:

```text
Assert.Contains() Failure: Item not found in collection Collection: ["003", "004", "030", "043", "057", ...] Not found: "001"
```

The line-78 changed fingerprint from the initial INT-4 run was not reproduced in the clean canonical full suite.

## Evidence

Publishable:

```text
eval/integration/int4c-clean-canonical-full-suite/int4c-clean-canonical-full-suite.v1.json
eval/integration/int4c-clean-canonical-full-suite/int4c-clean-canonical-full-suite.md
eval/integration/int4c-clean-canonical-full-suite/runtime-inventory.before.v1.json
eval/integration/int4c-clean-canonical-full-suite/runtime-inventory.after.v1.json
```

Local-only:

```text
tests/DocxHeaderExtractor.Tests/TestResults/int4c-clean-canonical-full-suite.trx
eval/integration/int4c-clean-canonical-full-suite/full-suite.console.txt
eval/integration/int4c-clean-canonical-full-suite/build-release.console.txt
```

Raw TRX SHA-256:

```text
c5cf48585bbbd0d86bc8ac01fb27284660193d5bda955acbf06932cccc91cd1c
```
