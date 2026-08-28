# INT-4B Clean Fingerprint Reproduction

Status: `REPRODUCED_BASELINE_SEMANTIC_FAILURE`

Execution revision: `952e3ceadc5079c8f46c8afff0c8366f5ec0490b`

Worktree: `C:\DocxHeaderExtractor-int4b-clean-fingerprint`

Target FQN:

```text
DocxHeaderExtractor.Tests.PdfC1CrossDocumentRegressionInventoryProbe.IndependentPartialTimeoutEvidenceNowExistsAndWasConsumedByN35
```

## Clean State

- Fresh detached worktree at the Round-2 execution revision.
- Pre-build inherited runtime directories found: `0`.
- `.verify-build` was absent.
- `bin/`, `obj/`, and `TestResults/` were absent before the clean Release build.
- Release build passed with existing warnings only.
- Provider calls: `0`.

## Single-FQN Run

Command:

```text
dotnet test tests\DocxHeaderExtractor.Tests\DocxHeaderExtractor.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName=DocxHeaderExtractor.Tests.PdfC1CrossDocumentRegressionInventoryProbe.IndependentPartialTimeoutEvidenceNowExistsAndWasConsumedByN35 --logger "trx;LogFileName=int4b-clean-fingerprint.trx" --logger "console;verbosity=detailed"
```

Result: `Failed`

Raw TRX:

```text
tests/DocxHeaderExtractor.Tests/TestResults/int4b-clean-fingerprint.trx
```

Raw TRX SHA-256:

```text
9fddb62fef801de2b3eef0c3addbe38e991275859f7f5602967b846d7707be66
```

## Reproduced Failure

Assertion line: `PdfC1CrossDocumentRegressionInventoryProbe.cs:71`

Normalized failure:

```text
Assert.Contains() Failure: Item not found in collection Collection: ["003", "004", "030", "043", "057", ...] Not found: "001"
```

This is the baseline semantic failure, not the INT-4 changed fingerprint failure at line `78`.

## Runtime Inventory

Before and after the single-FQN run, the equivalent scan found `7` artifacts under `eval` and `keys`. `.verify-build` was not present.

Documents found:

```text
003, 004, 030, 043, 057, 058
```

Partial-timeout documents found:

```text
003, 004, 030, 043, 058
```

Artifacts scanned:

```text
eval/accuracy-round4/k1024-semantic-run.v1.json
eval/accuracy-round4/k640-semantic-run.v1.json
eval/benchmark-n0/n2-s/invalid-runs/concurrency-1/003-n2s-run.v1.json
eval/benchmark-n0/n2-s/invalid-runs/concurrency-1/057-n2s-run.v1.json
eval/benchmark-n0/n2-s/runs/003-n2-s-run.v1.json
eval/benchmark-n0/n2-s/runs/057-n2-s-run.v1.json
eval/benchmark-n3/n3.4/runs/004-n3.4-canonical-run.v1.json
```

## Adjudication

`INT-4B` does not reproduce the INT-4 changed fingerprint from a clean worktree. The single-FQN clean run returns the old line-71 semantic failure, so the INT-4 line-78 drift is best classified as an execution-environment/runtime-inventory contamination issue unless contradicted by a preserved INT-4 raw runtime package.

- `PRODUCTION_REGRESSION_PROVEN = false`
- `FINGERPRINT_AUTHORITY_UPDATE_AUTHORIZED = false`
- `PRODUCTION_CODE_CHANGED = false`
- `TEST_CODE_CHANGED = false`
- `PROVIDER_CALLS = 0`
