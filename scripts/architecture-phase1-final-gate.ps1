[CmdletBinding()]
param(
    [string] $Root
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($Root)) { $Root = Join-Path $PSScriptRoot ".." }
$rootPath = (Resolve-Path $Root).Path
$checks = [System.Collections.Generic.List[object]]::new()

function Add-Check([string] $name, [bool] $passed, [string] $evidence) {
    $checks.Add([ordered]@{
        check = $name
        status = if ($passed) { "PASS" } else { "BLOCKED" }
        evidence = $evidence
    })
}

$coreProject = Get-Content -Raw (Join-Path $rootPath "src\DocxHeaderExtractor.Core\DocxHeaderExtractor.Core.csproj")
$corePackages = @("DocumentFormat.OpenXml", "PdfPig", "PDFtoImage") |
    Where-Object { $coreProject -match [regex]::Escape($_) }
Add-Check "core-is-package-pure" ($corePackages.Count -eq 0) `
    ($(if ($corePackages.Count -eq 0) { "Core has no document/rendering package" } else { "Core still owns: " + ($corePackages -join ", ") }))

$feedbackInCore = Test-Path (Join-Path $rootPath "src\DocxHeaderExtractor.Core\Learning\CorrectionMemory.cs")
$feedbackPort = Test-Path (Join-Path $rootPath "src\DocxHeaderExtractor.Application\Feedback\FeedbackContracts.cs")
Add-Check "feedback-owned-by-infrastructure" (!$feedbackInCore -and $feedbackPort) `
    ($(if (!$feedbackInCore -and $feedbackPort) { "feedback port and Infrastructure implementation are the only runtime owners" } else { "Core CorrectionMemory remains; port=" + $feedbackPort }))

$authority = Get-Content -Raw (Join-Path $rootPath "src\DocxHeaderExtractor.Core\Pipeline\AuthorityExtractionPipeline.cs")
$normalLegacyAdapter = $authority -match "LegacyDocConverter\.EnsureDocx"
Add-Check "legacy-normal-route-is-zero" (!$normalLegacyAdapter) `
    ($(if (!$normalLegacyAdapter) { "normal authority pipeline has no legacy converter call" } else { "AuthorityExtractionPipeline still calls LegacyDocConverter for compatibility input" }))

$genericContracts = (Test-Path (Join-Path $rootPath "src\DocxHeaderExtractor.Application\Tasks\ResourceContracts.cs")) -and
    (Test-Path (Join-Path $rootPath "src\DocxHeaderExtractor.Application\Tasks\TaskContracts.cs"))
Add-Check "generic-task-and-projection-seams" $genericContracts `
    ($(if ($genericContracts) { "generic resources, plans, and task result projection are present" } else { "generic task contract files are incomplete" }))

$planText = Get-Content -Raw (Join-Path $rootPath "docs\architecture\auto-harness-phase1-plan.md")
$phase2Recorded = $planText -match "Phase 2" -and $planText -match "deferred"
Add-Check "phase2-seams-recorded" $phase2Recorded `
    ($(if ($phase2Recorded) { "deferred Phase 2 work is recorded" } else { "deferred Phase 2 work is not recorded" }))

$head = (& git -C $rootPath rev-parse HEAD).Trim()
$main = (& git -C $rootPath rev-parse origin/main).Trim()
& git -C $rootPath merge-base --is-ancestor $head $main *> $null
$merged = $LASTEXITCODE -eq 0
Add-Check "published-into-main" $merged `
    ($(if ($merged) { "HEAD $head is contained in origin/main" } else { "HEAD $head is not contained in origin/main $main" }))

$blocked = @($checks | Where-Object { $_.status -eq "BLOCKED" })
[ordered]@{
    artifactKind = "auto_harness_phase1_final_gate"
    root = $rootPath
    status = if ($blocked.Count -eq 0) { "PASS" } else { "BLOCKED" }
    head = $head
    originMain = $main
    checks = $checks
} | ConvertTo-Json -Depth 5

if ($blocked.Count -gt 0) { exit 2 }
