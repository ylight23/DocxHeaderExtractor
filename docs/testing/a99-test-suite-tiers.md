# A99 test-suite tiers

The default validation tier is the production-focused deterministic suite:

```powershell
pwsh -File scripts/Invoke-A99TestTier.ps1 -Tier CoreDeterministic -NoBuild
```

It excludes tests explicitly marked `SuiteTier=HistoricalForensic`,
`SuiteTier=LongRunning`, and `SuiteTier=ProviderBenchmark`. The exclusions are
traits, not deletions: the historical probes remain available on demand.

The wrapper fails closed when a testhost, vstest process, or another test run for
this project is already active. A UI timeout must therefore be followed by
attach/wait, not a second invocation.

The N13, N14, and N15 classes are currently the only unambiguous quarantine
targets. They reproduce historical census/forensic/diagnosis artifacts and do
not define the current production semantic contract. N15 remains unchanged and
is still run explicitly as a historical diagnostic.

Tier policy:

* `UNIT`: pure contracts and deterministic primitives.
* `FOCUSED_REGRESSION`: targeted production and representative-source tests.
* `CORE_DETERMINISTIC`: default production regression tier.
* `LONG_RUNNING`: explicit large-source or expensive deterministic checks.
* `HISTORICAL_FORENSIC`: historical diagnosis and artifact reproduction.
* `PROVIDER_BENCHMARK`: explicit external model/provider executions.
