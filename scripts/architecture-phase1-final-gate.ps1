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

$coreSourceFiles = @(Get-ChildItem (Join-Path $rootPath "src\DocxHeaderExtractor.Core") -Recurse -Filter *.cs -File)
$coreSourcePackageHits = @($coreSourceFiles | Select-String -Pattern "DocumentFormat\.OpenXml|UglyToad\.PdfPig|PDFtoImage|LLamaSharp")
Add-Check "core-source-is-package-free" ($coreSourcePackageHits.Count -eq 0) `
    ($(if ($coreSourcePackageHits.Count -eq 0) { "Core source contains no parser/render/provider package usage" } else {
        "Core source still imports package-owned APIs: " + (($coreSourcePackageHits.Path | Sort-Object -Unique) -join ", ")
    }))

$processingProject = Get-Content -Raw (Join-Path $rootPath "src\DocxHeaderExtractor.DocumentProcessing\DocxHeaderExtractor.DocumentProcessing.csproj")
$processingPackages = @("DocumentFormat.OpenXml", "PdfPig", "PDFtoImage") |
    Where-Object { $processingProject -match [regex]::Escape($_) }
Add-Check "document-processing-owns-parser-packages" ($processingPackages.Count -eq 3) `
    ($(if ($processingPackages.Count -eq 3) { "DocumentProcessing owns OpenXML/PdfPig/PDFtoImage" } else {
        "DocumentProcessing package ownership incomplete: " + ($processingPackages -join ", ")
    }))

function Get-ProjectReferences([string] $path) {
    $text = Get-Content -Raw $path
    return @([regex]::Matches($text, '<ProjectReference\b[^>]*Include="([^"]+)"') |
        ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Groups[1].Value) })
}

$applicationReferences = Get-ProjectReferences (Join-Path $rootPath "src\DocxHeaderExtractor.Application\DocxHeaderExtractor.Application.csproj")
$processingReferences = Get-ProjectReferences (Join-Path $rootPath "src\DocxHeaderExtractor.DocumentProcessing\DocxHeaderExtractor.DocumentProcessing.csproj")
$infrastructureReferences = Get-ProjectReferences (Join-Path $rootPath "src\DocxHeaderExtractor.Infrastructure\DocxHeaderExtractor.Infrastructure.csproj")
$dependencyDirection =
    $applicationReferences.Count -eq 1 -and $applicationReferences -contains "DocxHeaderExtractor.Core" -and
    $processingReferences.Count -eq 2 -and $processingReferences -contains "DocxHeaderExtractor.Application" -and $processingReferences -contains "DocxHeaderExtractor.Core" -and
    $infrastructureReferences.Count -eq 3 -and $infrastructureReferences -contains "DocxHeaderExtractor.Application" -and
    $infrastructureReferences -contains "DocxHeaderExtractor.Core" -and $infrastructureReferences -contains "DocxHeaderExtractor.DocumentProcessing"
Add-Check "dependency-direction-is-explicit" $dependencyDirection `
    ($(if ($dependencyDirection) { "Application -> Core; DocumentProcessing -> Application/Core; Infrastructure -> Application/Core/DocumentProcessing" } else {
        "unexpected project dependency direction"
    }))

$reachabilityPath = Join-Path $rootPath "eval\architecture\legacy-reachability.v1.json"
$reachability = if (Test-Path $reachabilityPath) { Get-Content -Raw $reachabilityPath | ConvertFrom-Json } else { $null }
$reachabilityProof = $null -ne $reachability -and
    [int]$reachability.NORMAL_TO_LEGACY_DEPENDENCIES -eq 0 -and
    [int]$reachability.NORMAL_TO_EVAL_DEPENDENCIES -eq 0 -and
    [int]$reachability.COMPATIBILITY_ONLY_COUNT -ge 1
Add-Check "legacy-reachability-proof-recorded" $reachabilityProof `
    ($(if ($reachabilityProof) { "reachability artifact records zero normal-to-legacy/eval dependencies; compatibility is explicit" } else {
        "legacy reachability artifact is missing or records a normal legacy/eval dependency"
    }))

$feedbackInCore = Test-Path (Join-Path $rootPath "src\DocxHeaderExtractor.Core\Learning\CorrectionMemory.cs")
$feedbackPort = Test-Path (Join-Path $rootPath "src\DocxHeaderExtractor.Application\Feedback\FeedbackContracts.cs")
$feedbackImplementation = Test-Path (Join-Path $rootPath "src\DocxHeaderExtractor.Infrastructure\Learning\CorrectionMemory.cs")
Add-Check "feedback-owned-by-infrastructure" (!$feedbackInCore -and $feedbackPort -and $feedbackImplementation) `
    ($(if (!$feedbackInCore -and $feedbackPort -and $feedbackImplementation) {
        "Application port plus Infrastructure CorrectionMemory implementation; no Core implementation"
    } else {
        "feedback ownership incomplete; core=" + $feedbackInCore + ", port=" + $feedbackPort + ", infrastructure=" + $feedbackImplementation
    }))

$authority = Get-Content -Raw (Join-Path $rootPath "src\DocxHeaderExtractor.DocumentProcessing\Pipeline\AuthorityExtractionPipeline.cs")
$normalLegacyAdapter = $authority -match "LegacyDocConverter\.EnsureDocx"
Add-Check "legacy-normal-route-is-zero" (!$normalLegacyAdapter) `
    ($(if (!$normalLegacyAdapter) { "normal authority pipeline has no legacy converter call" } else { "AuthorityExtractionPipeline still calls LegacyDocConverter for compatibility input" }))

$normalHostFiles = @(
    (Join-Path $rootPath "src\DocxHeaderExtractor.Web\Program.cs"),
    (Join-Path $rootPath "src\DocxHeaderExtractor.Cli\Program.cs"),
    (Join-Path $rootPath "src\DocxHeaderExtractor.Mcp\McpExtractionService.cs")
)
$normalHostTexts = @($normalHostFiles | Where-Object { Test-Path $_ } | ForEach-Object { Get-Content -Raw $_ })
$hostBypassHits = @($normalHostTexts | Select-String -Pattern "new\s+(AuthorityExtractionPipeline|HeaderExtractionPipeline)|RunDocumentWithCompatibilityAsync")
$hostComposition = $hostBypassHits.Count -eq 0 -and $normalHostTexts.Count -eq 3
Add-Check "normal-host-authority-bypass-free" $hostComposition `
    ($(if ($hostComposition) { "Web/CLI/MCP do not construct or invoke an authority pipeline directly" } else {
        "normal host bypasses found: " + ($hostBypassHits -join "; ")
    }))

$harnessTypes = @(Get-ChildItem (Join-Path $rootPath "src") -Recurse -Filter *.cs -File |
    Select-String -Pattern "class\s+\w*Harness\b" |
    ForEach-Object { $_.Matches.Value } | Sort-Object -Unique)
Add-Check "duplicate-harness-is-zero" ($harnessTypes.Count -le 2) `
    ($(if ($harnessTypes.Count -le 2) { "only the canonical DocumentAgentHarness and its factory are present" } else {
        "multiple harness implementations found: " + ($harnessTypes -join ", ")
    }))

$genericContracts = (Test-Path (Join-Path $rootPath "src\DocxHeaderExtractor.Application\Tasks\ResourceContracts.cs")) -and
    (Test-Path (Join-Path $rootPath "src\DocxHeaderExtractor.Application\Tasks\TaskContracts.cs"))
Add-Check "generic-task-and-projection-seams" $genericContracts `
    ($(if ($genericContracts) { "generic resources, plans, and task result projection are present" } else { "generic task contract files are incomplete" }))

$planText = Get-Content -Raw (Join-Path $rootPath "docs\architecture\auto-harness-phase1-plan.md")
$phase2Doc = Test-Path (Join-Path $rootPath "docs\architecture\phase2-seams.md")
$phase2Recorded = $phase2Doc -and $planText -match "Phase 2" -and $planText -match "deferred"
Add-Check "phase2-seams-recorded" $phase2Recorded `
    ($(if ($phase2Recorded) { "deferred Phase 2 work is recorded" } else { "deferred Phase 2 work is not recorded" }))

$architectureDoc = Get-Content -Raw (Join-Path $rootPath "docs\architecture\auto-harness-phase1.md")
$statusConsistent = $planText -match 'Status: `ACTIVE`' -and $architectureDoc -match 'Status: `IN_PROGRESS`'
Add-Check "architecture-status-is-consistent" $statusConsistent `
    ($(if ($statusConsistent) { "Phase 1 remains ACTIVE/IN_PROGRESS until publication" } else { "architecture status documents disagree" }))

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
