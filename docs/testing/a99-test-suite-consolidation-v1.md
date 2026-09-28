# A99 test-suite consolidation v1

## Baseline census

The Release test project currently contains 366 C# test files and 346 test
class declarations. Release discovery lists 1,647 test cases (1,382 `Fact`
methods and 64 `Theory` methods before data expansion). No xUnit category
traits were present, so the old wrapper's `SuiteTier` filter did not separate
the archive from the default run.

The explicit manifest now quarantines 73 classes into five primary categories:

| Primary category | Classes | Default tier |
| --- | ---: | --- |
| `PROVIDER_BENCHMARK` | 19 | excluded |
| `LONG_RUNNING` | 6 | excluded |
| `HISTORICAL_FORENSIC` | 14 | excluded |
| `EXPERIMENT_ONLY` | 25 | excluded |
| `DIAGNOSTIC_PROBE` | 9 | excluded |

The lists are class-level ownership decisions; each class has exactly one
primary quarantine category. Unlisted classes remain in the canonical default
tier until a behavior-backed review proves otherwise.

## Tier measurement

`-ListOnly` against the Release assembly reports:

| Tier | Discovered tests |
| --- | ---: |
| `CORE_DETERMINISTIC` | 1,234 |
| `ALL` (full archive) | 1,647 |

The reduction is quarantine only. No test file, Gold artifact, provider ledger,
or frozen experiment output was deleted or rewritten.

## Safety and deletion decision

No deletion is justified by this pass. A test may be retired only after all of
the following are independently established: symbol reachability is dead,
there is no frozen/public compatibility dependency, and no unique invariant or
regression coverage would be lost. The manifest records quarantine without
making that stronger claim.

The protected invariant families remain explicit in the manifest: source-faithful
extraction; source alias/hash identity; fail-closed semantic request versions;
`semanticFunction` and derived membership; exact source-part binding and
ownership; identity, hierarchy, materialization and projection; provider and
budget gates; and Gold/frozen-authority integrity.

## Validation commands

```powershell
dotnet build -c Release --no-restore
pwsh -File scripts/Invoke-A99TestTier.ps1 -Tier CoreDeterministic -Configuration Release -NoBuild
pwsh -File scripts/Invoke-A99TestTier.ps1 -Tier HistoricalForensic -Configuration Release -NoBuild
pwsh -File scripts/Invoke-A99TestTier.ps1 -Tier LongRunning -Configuration Release -NoBuild
pwsh -File scripts/Invoke-A99TestTier.ps1 -Tier ProviderBenchmark -Configuration Release -NoBuild
pwsh -File scripts/Invoke-A99TestTier.ps1 -Tier All -Configuration Release -NoBuild
```

Provider execution remains gated by the individual experiment tests and is not
authorized by selecting a tier. The wrapper's named mutex prevents overlapping
runs of the same tier; a timeout must be handled by inspecting the existing
run rather than starting a duplicate.

## Validation recorded in this workspace

`dotnet build -c Release --no-restore` passed with zero warnings and zero
errors. Release list-only discovery passed for both the 1,234-test core tier
and the 1,647-test archive tier. `git diff --check` is clean.

The full CoreDeterministic execution was started once. It reached a concrete
MCP stdio timing failure (`Stdio_server_advertises_only_the_three_read_only_tools`:
expected `Completed`, observed `Running`) and then made no further terminal
progress while an older testhost for this same project (started before this
cleanup) continued consuming resources. The new run was interrupted with
Ctrl+C after the hang audit; it is therefore recorded as incomplete, not as a
pass. No retry was started and the pre-existing testhost was not killed.
