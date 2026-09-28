# A99 test-suite tiers

The default validation tier is the production-focused deterministic Release
suite. Membership comes from the explicit class manifest at
`docs/testing/a99-test-suite-manifest.v1.json`; it is not inferred from
filenames or from missing xUnit traits.

```powershell
pwsh -File scripts/Invoke-A99TestTier.ps1 -Tier CoreDeterministic -Configuration Release -NoBuild
```

The exclusions are quarantine filters, not deletions: historical probes,
experiments, diagnostics, provider harnesses, and slow checks remain available
on demand. The manifest is deliberately conservative; no test is retired by
this change.

The wrapper fails closed when a testhost, vstest process, or another test run for
this project is already active. A UI timeout must therefore be followed by
attach/wait, not a second invocation.

The N13, N14, and N15 classes are currently the only unambiguous quarantine
targets. They reproduce historical census/forensic/diagnosis artifacts and do
not define the current production semantic contract. N15 remains unchanged and
is still run explicitly as a historical diagnostic.

Tier commands:

```powershell
pwsh -File scripts/Invoke-A99TestTier.ps1 -Tier CoreDeterministic -Configuration Release
pwsh -File scripts/Invoke-A99TestTier.ps1 -Tier HistoricalForensic -Configuration Release
pwsh -File scripts/Invoke-A99TestTier.ps1 -Tier LongRunning -Configuration Release
pwsh -File scripts/Invoke-A99TestTier.ps1 -Tier ProviderBenchmark -Configuration Release
pwsh -File scripts/Invoke-A99TestTier.ps1 -Tier All -Configuration Release
```

Add `-ListOnly` to measure discovery without executing tests. `All` is the
full archive and intentionally has no filter. Provider tests still require
their own explicit environment gates; tier selection never authorizes a
provider call.

The protected invariant families are listed in the manifest and include
source-faithful extraction, identity, semantic-function/membership authority,
exact source-part binding, ownership, hierarchy, materialization, projection,
provider/budget gates, and frozen Gold integrity.
