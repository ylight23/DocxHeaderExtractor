param(
    [ValidateSet('CoreDeterministic', 'HistoricalForensic', 'LongRunning', 'ProviderBenchmark', 'All')]
    [string] $Tier = 'CoreDeterministic',
    [switch] $NoBuild
)

$repoRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repoRoot 'tests\DocxHeaderExtractor.Tests\DocxHeaderExtractor.Tests.csproj'
$testProjectPattern = [regex]::Escape($testProject)

$active = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object {
        $_.CommandLine -and
        ($_.CommandLine -match $testProjectPattern) -and
        ($_.Name -in @('dotnet.exe', 'testhost.exe', 'vstest.console.exe'))
    }
if ($active) {
    $ids = ($active | Select-Object -ExpandProperty ProcessId) -join ', '
    throw "A99 test run already active for this project (PIDs: $ids). Attach/wait; do not start a second run."
}

$mutexName = "DocxHeaderExtractor.A99.TestTier.$Tier"
$mutex = [Threading.Mutex]::new($false, $mutexName)
$ownsMutex = $false
$exitCode = 1
try {
    if (-not $mutex.WaitOne(0)) {
        throw "A99 test tier '$Tier' is already active; attach/wait instead of starting another run."
    }
    $ownsMutex = $true

    $filter = switch ($Tier) {
        'CoreDeterministic' { 'SuiteTier!=HistoricalForensic&SuiteTier!=LongRunning&SuiteTier!=ProviderBenchmark' }
        'HistoricalForensic' { 'SuiteTier=HistoricalForensic' }
        'LongRunning' { 'SuiteTier=LongRunning' }
        'ProviderBenchmark' { 'SuiteTier=ProviderBenchmark' }
        default { $null }
    }

    $arguments = @('test', $testProject, '--logger', 'console;verbosity=minimal')
    if ($filter) { $arguments += @('--filter', $filter) }
    if ($NoBuild) { $arguments += '--no-build' }
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
