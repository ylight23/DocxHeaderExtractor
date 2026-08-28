# INT-4 Round-2 Canonical Full Suite

Status: `FAIL_CHANGED_FINGERPRINT`

Execution revision: `952e3ceadc5079c8f46c8afff0c8366f5ec0490b`

Raw TRX: `tests/DocxHeaderExtractor.Tests/TestResults/dungp_USER_2026-08-28_23_12_36.trx`

Raw TRX SHA-256: `bbc834a5029fb678ebe236863b8bd400d1ea201da0f8dc62c8c5f6d8f6613383`

## Preflight

- HEAD matched expected revision: `true`
- Artifact gate: `PASS` using Git object authority
- 683/683 artifacts present and readable
- Git object mismatches: `0`
- Raw working-tree SHA mismatches: `53`; classified as checkout-filter/line-ending view because HEAD Git objects match authority objects
- Disk gate: `PASS`
- Release build: `PASS` with `0` errors and existing warnings

## Execution

Command:

```text
dotnet test tests\DocxHeaderExtractor.Tests\DocxHeaderExtractor.Tests.csproj -c Release --no-build --no-restore --logger trx --logger "console;verbosity=minimal"
```

Result: `1338 total / 1308 passed / 30 failed / 0 skipped`.

## Baseline Join

Baseline: Round-1 canonical VERIFY-6B at `92cd2d6d3cba29986858d30a91d5da0468044cff`.

- Baseline failures: `30`
- Still failing with same normalized failure: `29`
- Resolved: `0`
- New failures: `0`
- Changed fingerprints: `1`
- Unjoined: `0`

## Changed Fingerprint

One baseline FQN still fails with changed assertion semantics:

```text
DocxHeaderExtractor.Tests.PdfC1CrossDocumentRegressionInventoryProbe.IndependentPartialTimeoutEvidenceNowExistsAndWasConsumedByN35
Current:  Assert.Equal() Failure: Collections differ Expected: ["004"] Actual: ["004", "030", "043", "058"] ↑ (pos 1)
Baseline: Assert.Contains() Failure: Item not found in collection Collection: ["003", "004", "030", "043", "057", ···] Not found: "001"
```

## Closure

Because `CHANGED_FINGERPRINTS = 1`, INT-4 does not freeze the Round-2 failure universe as PASS.

- `FULL_SUITE_VALID_FOR_FREEZE = false`
- `FAILURE_UNIVERSE_FROZEN = false`
- `FREEZE_BLOCKER = CHANGED_FINGERPRINTS`
- `FULL_SUITE_EXECUTED = true`
- `PROVIDER_CALLS = 0`
- `PRODUCTION_CODE_CHANGED = false`
- `TEST_EXPECTATIONS_CHANGED = false`

No remediation was performed in INT-4.
