[CmdletBinding()]
param(
    [string] $Root,
    [switch] $RequirePublication
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($Root)) { $Root = Join-Path $PSScriptRoot ".." }
$rootPath = (Resolve-Path $Root).Path
$checks = [System.Collections.Generic.List[object]]::new()

function Add-Check([string] $name, [bool] $passed, [string] $evidence) {
    $checks.Add([ordered]@{ check = $name; status = if ($passed) { "PASS" } else { "BLOCKED" }; evidence = $evidence })
}

function Invoke-JsonScript([string] $name, [hashtable] $arguments) {
    $output = & (Join-Path $PSScriptRoot $name) @arguments 2>&1
    $exitCode = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
    $parsed = $null
    try { $parsed = ($output -join [Environment]::NewLine) | ConvertFrom-Json } catch { }
    [pscustomobject]@{ ExitCode = $exitCode; Result = $parsed }
}

$planPath = Join-Path $rootPath "docs\architecture\auto-harness-phase2-plan.md"
$planText = if (Test-Path -LiteralPath $planPath) { Get-Content -Raw $planPath } else { "" }
$planComplete = $planText -match 'Status:\s*`VERIFICATION_COMPLETE`'
Add-Check "phase2-plan-complete" ($planComplete) "plan status is VERIFICATION_COMPLETE"

foreach ($id in 2..16) {
    $pattern = "- \[x\] P2-WS$id\b"
    Add-Check "p2-ws$id" ($planText -match $pattern) "P2-WS$id is checked in the canonical plan"
}

$hygieneArgs = @{ Root = $rootPath }
if ($RequirePublication) { $hygieneArgs.RequirePublication = $true }
$hygiene = Invoke-JsonScript "source-tree-hygiene-gate.ps1" $hygieneArgs
Add-Check "source-tree-hygiene" ($hygiene.ExitCode -eq 0 -and $hygiene.Result.status -eq "PASS") `
    ($(if ($RequirePublication) { "published hygiene gate" } else { "pre-merge candidate hygiene gate" }))

$phase1 = Invoke-JsonScript "architecture-phase1-audit.ps1" @{ Root = $rootPath }
Add-Check "phase1-audit" ($phase1.ExitCode -eq 0 -and $phase1.Result.status -eq "PASS") "Phase-1 architecture audit PASS"

$seamsPath = Join-Path $rootPath "docs\architecture\phase2-seams.md"
$seamsText = if (Test-Path -LiteralPath $seamsPath) { Get-Content -Raw $seamsPath } else { "" }
Add-Check "phase2-seams-verified" ($seamsText -match 'Status:\s*`VERIFIED`') "Phase-2 seams are marked VERIFIED"

$evidencePath = Join-Path $rootPath "eval\verification\phase2-final-evidence.v1.json"
$evidence = $null
if (Test-Path -LiteralPath $evidencePath) { try { $evidence = Get-Content -Raw $evidencePath | ConvertFrom-Json } catch { } }
Add-Check "full-suite-evidence" ($null -ne $evidence -and $evidence.fullSuite.exitCode -eq 0 -and $evidence.fullSuite.failed -eq 0) "full suite evidence has exit 0 and zero failures"
Add-Check "release-build-evidence" ($null -ne $evidence -and $evidence.releaseBuild.exitCode -eq 0 -and $evidence.releaseBuild.errors -eq 0) "Release build evidence has exit 0 and zero errors"

$diff = & git -C $rootPath diff --check 2>&1
Add-Check "git-diff-check" ($LASTEXITCODE -eq 0) "git diff --check PASS"

$accuracyChanges = @(git -C $rootPath diff --name-only origin/main...HEAD | Where-Object {
    $_ -match '(^|/)(eval/accuracy99|eval/human-gold|human-gold)(/|$)'
})
Add-Check "accuracy-boundary-untouched" ($accuracyChanges.Count -eq 0) "no Accuracy-99 or Human Gold path changed in candidate commits"

$blocked = @($checks | Where-Object { $_.status -eq "BLOCKED" })
[ordered]@{
    artifactKind = "auto_harness_phase2_final_gate"
    root = $rootPath
    status = if ($blocked.Count -eq 0) { "PASS" } else { "BLOCKED" }
    publicationMode = if ($RequirePublication) { "post-merge" } else { "pre-merge-candidate" }
    checks = $checks
} | ConvertTo-Json -Depth 8

if ($blocked.Count -gt 0) { exit 2 }
