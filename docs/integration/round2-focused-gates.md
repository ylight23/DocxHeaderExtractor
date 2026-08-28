# INT-2-CLOSE Round-2 Focused Gate Closure

Target branch: `integration/final-authority-clean-architecture`

Verified head: `92ed397e0538de06cbdee9d700f0640f3b4c2bb2`

Status: `PASS`

This closes the Round-2 focused gate ledger needed before artifact-delta
reconciliation. It does not run the full suite, does not change production or
test expectations, and makes no provider calls.

## Evidence

The previously published local focused run on the target head is reused for the
already-covered gates. Its aggregate result was `18/18 PASS`; the raw console
log was not retained, so the current Release assembly was enumerated with
`--list-tests` to make the gate-to-FQN mapping explicit.

The only missing gate was MCP. It was run directly:

```text
dotnet test tests\DocxHeaderExtractor.Tests\DocxHeaderExtractor.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~Mcp" --logger "console;verbosity=minimal"
```

Result: `7/7 PASS`.

## Gate Summary

| Gate | Result | Evidence |
| --- | --- | --- |
| RFC | `5/5 PASS` | reused focused evidence; mapped to `RfcTocDictionaryOutlineTests` |
| RFC-2 | `67/67/0/1.0 PASS` | retained invariant from `eval/verification/canonical-execution-environment-env2.v1.json` |
| MCP | `7/7 PASS` | executed during INT-2-CLOSE |
| F regression | `2/2 PASS` | reused focused evidence; mapped to `PdfAccuracyRegressionHarnessContractProbe` |
| ARCH-4P | `PASS` | reused focused evidence; mapped to `NumberingStyleFeatureBoundaryTests` |
| ARCH-4Q | `PASS` | reused focused evidence plus `builtin-heading-style-ownership.v1.json` |
| ARCH-4R | `PASS` | reused focused evidence; mapped to `DemotionPolicyOwnershipTests` |
| Release | `PASS` | reused publish verification; `0` errors, existing warnings only |

## Notes

GitHub CI status for `92ed397e` remains `NOT_OBSERVABLE`; these are local
execution/publish artifacts, not hosted CI evidence.

`FULL_SUITE_EXECUTED = false`.

`PROVIDER_CALLS = 0`.

`PRODUCTION_BEHAVIOR_CHANGED = false`.

Next gate: `INT-3 -- Round-2 Artifact Delta Reconciliation`.
