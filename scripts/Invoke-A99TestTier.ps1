param(
    [ValidateSet('CoreDeterministic', 'HistoricalForensic', 'LongRunning', 'ProviderBenchmark', 'All')]
    [string] $Tier = 'CoreDeterministic',
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $NoBuild,
    [switch] $ListOnly
)

$repoRoot = Split-Path -Parent $PSScriptRoot
$testProject = [IO.Path]::Combine($repoRoot, 'tests', 'DocxHeaderExtractor.Tests', 'DocxHeaderExtractor.Tests.csproj')
$manifestPath = [IO.Path]::Combine($repoRoot, 'docs', 'testing', 'a99-test-suite-manifest.v1.json')
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

$categoryNames = @{
    CoreDeterministic = 'CORE_DETERMINISTIC'
    HistoricalForensic = 'HISTORICAL_FORENSIC'
    LongRunning = 'LONG_RUNNING'
    ProviderBenchmark = 'PROVIDER_BENCHMARK'
    All = 'ALL'
}
$tierName = $categoryNames[$Tier]
$tierDefinition = $manifest.tiers.$tierName
$categoryMap = @{}
foreach ($property in $manifest.categories.psobject.Properties) {
    $categoryMap[$property.Name] = @($property.Value.classNames)
}

$filter = $null
if ($Tier -eq 'All') {
    # ALL is intentionally unfiltered: the manifest's category lists are
    # quarantine documentation, not a lossy allow-list for the archive tier.
    $filter = $null
}
elseif ($tierDefinition.excludeCategories) {
    $excludedClasses = @($tierDefinition.excludeCategories | ForEach-Object { $categoryMap[$_] }) | Sort-Object -Unique
    $filterParts = @($excludedClasses | ForEach-Object { "FullyQualifiedName!~$($_)" })
    if ($filterParts.Count -gt 0) { $filter = $filterParts -join '&' }
}
elseif ($tierDefinition.includeCategories) {
    $includedClasses = @($tierDefinition.includeCategories | ForEach-Object { $categoryMap[$_] }) | Sort-Object -Unique
    $filterParts = @($includedClasses | ForEach-Object { "FullyQualifiedName~$($_)" })
    if ($filterParts.Count -gt 0) { $filter = $filterParts -join '|' }
}

# The manifest is the source of suite membership. Do not infer categories from filenames at runtime.
# Provider execution remains separately gated by the experiment tests' explicit environment checks.

$mutexName = "DocxHeaderExtractor.A99.TestTier.$Tier"
$mutex = [Threading.Mutex]::new($false, $mutexName)
$ownsMutex = $false
$exitCode = 1
try {
    if (-not $mutex.WaitOne(0)) {
        throw "A99 test tier '$Tier' is already active; attach/wait instead of starting another run."
    }
    $ownsMutex = $true

    $arguments = @('test', $testProject, '-c', $Configuration, '--logger', 'console;verbosity=minimal')
    if ($filter) { $arguments += @('--filter', $filter) }
    if ($NoBuild) { $arguments += '--no-build' }
    if ($ListOnly) { $arguments += '--list-tests' }
    & dotnet @arguments
    $exitCode = $LASTEXITCODE
}
finally {
    if ($ownsMutex) {
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
}

exit $exitCode
