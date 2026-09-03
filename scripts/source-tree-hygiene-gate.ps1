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
    $checks.Add([ordered]@{
        check = $name
        status = if ($passed) { "PASS" } else { "BLOCKED" }
        evidence = $evidence
    })
}

function SourceFiles([string] $projectPath) {
    if (!(Test-Path -LiteralPath $projectPath)) { return @() }
    return @(Get-ChildItem -LiteralPath $projectPath -Recurse -File -Filter *.cs |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
}

$processingRoot = Join-Path $rootPath "src\DocxHeaderExtractor.DocumentProcessing"
$processingFiles = SourceFiles $processingRoot
$legacyApplication = Test-Path -LiteralPath (Join-Path $processingRoot "Application")
$legacyEval = Test-Path -LiteralPath (Join-Path $processingRoot "Eval")
Add-Check "document-processing-application-absent" (!$legacyApplication) `
    ($(if (!$legacyApplication) { "DocumentProcessing/Application is absent" } else { "legacy Application directory still exists" }))
Add-Check "document-processing-eval-absent" (!$legacyEval) `
    ($(if (!$legacyEval) { "DocumentProcessing/Eval is absent" } else { "legacy Eval directory still exists" }))

$providerPattern = '(?i)OPENROUTER_|LMSTUDIO_|SGLANG_|\bApiKey\s*(?:\{|=|\b)|openrouter\.ai|\b(?:InferenceBackend|OpenRouter|LmStudio|Sglang)\b|\b(?:RemoteInferenceOptions|LocalModelOptions)\b|GGUF.{0,80}(?:runtime|load|model|context)|(?:runtime|load|model|context).{0,80}GGUF|LLamaSharp'
$providerHits = @($processingFiles | Select-String -Pattern $providerPattern | ForEach-Object { "$($_.Path):$($_.LineNumber):$($_.Line.Trim())" })
Add-Check "provider-specific-types-absent-from-document-processing" ($providerHits.Count -eq 0) `
    ($(if ($providerHits.Count -eq 0) { "DocumentProcessing contains no provider secrets, endpoints, vendor enums, or runtime configuration" } else { $providerHits -join "; " }))

$evalNamespaceHits = @($processingFiles | Select-String -Pattern '^namespace\s+DocxHeaderExtractor\.Eval\b' |
    ForEach-Object { "$($_.Path):$($_.LineNumber)" })
Add-Check "eval-namespace-absent-from-production" ($evalNamespaceHits.Count -eq 0) `
    ($(if ($evalNamespaceHits.Count -eq 0) { "no production source declares DocxHeaderExtractor.Eval" } else { $evalNamespaceHits -join "; " }))

$coreRoot = Join-Path $rootPath "src\DocxHeaderExtractor.Core"
$coreNamespaceHits = @()
foreach ($file in Get-ChildItem (Join-Path $rootPath "src") -Recurse -File -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.FullName -notlike "$coreRoot\*" }) {
    $coreNamespaceHits += @(Select-String -LiteralPath $file.FullName -Pattern '^namespace\s+DocxHeaderExtractor\.Core\.' |
        ForEach-Object { "$($_.Path):$($_.LineNumber)" })
}
Add-Check "core-namespace-isolated" ($coreNamespaceHits.Count -eq 0) `
    ($(if ($coreNamespaceHits.Count -eq 0) { "no DocxHeaderExtractor.Core namespace outside Core" } else { $coreNamespaceHits -join "; " }))

$evalOnlyNamePattern = '(?i)(^|\\)(Eval|Replay|Gold|Calibration|Benchmarks)(\\|$)|(^|\\)(AnswerKey|Evaluator|.*ReplayEvaluator)\.cs$'
$evalOnlyProductionHits = @($processingFiles | Where-Object { $_.FullName -match $evalOnlyNamePattern } |
    ForEach-Object { $_.FullName })
Add-Check "eval-only-implementations-absent-from-production" ($evalOnlyProductionHits.Count -eq 0) `
    ($(if ($evalOnlyProductionHits.Count -eq 0) { "evaluation/replay/gold implementations live in DocxHeaderExtractor.Eval" } else { $evalOnlyProductionHits -join "; " }))

$projectFiles = @(Get-ChildItem (Join-Path $rootPath "src") -Recurse -File -Filter *.csproj)
$evalReferences = @($projectFiles | Where-Object { $_.FullName -notlike '*\DocxHeaderExtractor.Cli\*' -and $_.FullName -notlike '*\DocxHeaderExtractor.Eval\*' } |
    Select-String -Pattern '<ProjectReference\b[^>]*DocxHeaderExtractor\.Eval' | ForEach-Object { "$($_.Path):$($_.LineNumber)" })
Add-Check "production-does-not-reference-eval" ($evalReferences.Count -eq 0) `
    ($(if ($evalReferences.Count -eq 0) { "Web/MCP/Infrastructure/DocumentProcessing/AgentHarness/Application have no Eval project reference" } else { $evalReferences -join "; " }))

$projectRoots = @{
    "DocxHeaderExtractor.Core" = "DocxHeaderExtractor.Core"
    "DocxHeaderExtractor.Application" = "DocxHeaderExtractor.Application"
    "DocxHeaderExtractor.DocumentProcessing" = "DocxHeaderExtractor.DocumentProcessing"
    "DocxHeaderExtractor.Infrastructure" = "DocxHeaderExtractor.Infrastructure"
    "DocxHeaderExtractor.AgentHarness" = "DocxHeaderExtractor.AgentHarness"
    "DocxHeaderExtractor.Web" = "DocxHeaderExtractor.Web"
    "DocxHeaderExtractor.Cli" = "DocxHeaderExtractor.Cli"
    "DocxHeaderExtractor.Mcp" = "DocxHeaderExtractor.Mcp"
    "DocxHeaderExtractor.Eval" = "DocxHeaderExtractor.Eval"
}
$namespaceMismatches = [System.Collections.Generic.List[string]]::new()
foreach ($entry in $projectRoots.GetEnumerator()) {
    $projectRoot = Join-Path $rootPath ("src\" + $entry.Key)
    foreach ($file in SourceFiles $projectRoot) {
        $match = Select-String -LiteralPath $file.FullName -Pattern '^namespace\s+([^;]+);' | Select-Object -First 1
        if ($null -eq $match) { continue }
        $namespace = $match.Matches[0].Groups[1].Value
        if (!$namespace.Equals($entry.Value) -and !$namespace.StartsWith($entry.Value + ".", [StringComparison]::Ordinal)) {
            $namespaceMismatches.Add("$($file.FullName):$namespace expected $($entry.Value)")
        }
    }
}
Add-Check "namespace-project-consistency" ($namespaceMismatches.Count -eq 0) `
    ($(if ($namespaceMismatches.Count -eq 0) { "all production namespaces match their owning project" } else { $namespaceMismatches -join "; " }))

$catchAll = @()
foreach ($name in @("Application", "Eval", "Llm", "Output", "Common", "Helpers", "Utils", "Support", "Manager", "Models")) {
    $catchAll += @(Get-ChildItem (Join-Path $rootPath "src") -Directory -Recurse |
        Where-Object { $_.Name -eq $name -and $_.FullName -notlike '*\DocxHeaderExtractor.Core\Models' } |
        ForEach-Object { $_.FullName })
}
Add-Check "catch-all-folders-removed-or-bounded" ($catchAll.Count -eq 0) `
    ($(if ($catchAll.Count -eq 0) { "no prohibited catch-all folder remains outside the bounded Core.Models contract" } else { $catchAll -join "; " }))

$pipelinePath = Join-Path $processingRoot "Pipeline\AuthorityExtractionPipeline.cs"
$pipelineText = if (Test-Path -LiteralPath $pipelinePath) { Get-Content -Raw $pipelinePath } else { "" }
$legacyNormalRoute = $pipelineText -match 'LegacyDocConverter\.EnsureDocx'
Add-Check "legacy-normal-route-zero" (!$legacyNormalRoute) `
    ($(if (!$legacyNormalRoute) { "normal authority pipeline has no legacy converter route" } else { "LegacyDocConverter is reachable from normal authority" }))

$harnessHits = @(Get-ChildItem (Join-Path $rootPath "src") -Recurse -File -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Select-String -Pattern 'class\s+\w*Harness\b' | ForEach-Object { $_.Matches.Value } | Sort-Object -Unique)
Add-Check "duplicate-harness-zero" ($harnessHits.Count -le 2) `
    ($(if ($harnessHits.Count -le 2) { "canonical harness surface is bounded" } else { $harnessHits -join "; " }))

function Invoke-ArchitectureGate([string] $scriptName) {
    $path = Join-Path $PSScriptRoot $scriptName
    $output = & $path -Root $rootPath 2>&1
    $exitCode = $LASTEXITCODE
    $json = $output -join [Environment]::NewLine
    $parsed = $null
    try { $parsed = $json | ConvertFrom-Json } catch { }
    return [pscustomobject]@{ ExitCode = $exitCode; Result = $parsed; Raw = $json }
}

$phase1Audit = Invoke-ArchitectureGate "architecture-phase1-audit.ps1"
Add-Check "phase1-architecture-audit" ($null -ne $phase1Audit.Result -and $phase1Audit.Result.status -eq "PASS") `
    ($(if ($null -ne $phase1Audit.Result -and $phase1Audit.Result.status -eq "PASS") { "architecture-phase1-audit.ps1 PASS" } else { "architecture-phase1-audit.ps1 BLOCKED" }))

$phase1Final = $null
if ($RequirePublication) {
    $phase1Final = Invoke-ArchitectureGate "architecture-phase1-final-gate.ps1"
    Add-Check "phase1-publication-gate" ($phase1Final.ExitCode -eq 0 -and $null -ne $phase1Final.Result -and $phase1Final.Result.status -eq "PASS") `
        ($(if ($phase1Final.ExitCode -eq 0) { "post-merge architecture-phase1-final-gate.ps1 PASS" } else { "post-merge publication gate BLOCKED" }))
} else {
    Add-Check "phase1-candidate-gate" $true "pre-merge candidate mode; publication is intentionally checked after merge"
}

$blocked = @($checks | Where-Object { $_.status -eq "BLOCKED" })
[ordered]@{
    artifactKind = "source_tree_hygiene_gate"
    root = $rootPath
    status = if ($blocked.Count -eq 0) { "PASS" } else { "BLOCKED" }
    checks = $checks
    phase1Audit = $phase1Audit.Result
    phase1FinalGate = $phase1Final.Result
} | ConvertTo-Json -Depth 8

if ($blocked.Count -gt 0) { exit 2 }
