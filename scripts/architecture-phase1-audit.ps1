[CmdletBinding()]
param(
    [string] $Root
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($Root)) { $Root = Join-Path $PSScriptRoot ".." }
$rootPath = (Resolve-Path $Root).Path
$findings = [System.Collections.Generic.List[object]]::new()

function Add-Finding([string] $check, [bool] $passed, [string] $evidence) {
    $findings.Add([ordered]@{
        check = $check
        status = if ($passed) { "PASS" } else { "BLOCKED" }
        evidence = $evidence
    })
}

$requiredProjects = @(
    "DocxHeaderExtractor.Core",
    "DocxHeaderExtractor.Application",
    "DocxHeaderExtractor.DocumentProcessing",
    "DocxHeaderExtractor.Infrastructure",
    "DocxHeaderExtractor.AgentHarness",
    "DocxHeaderExtractor.Web",
    "DocxHeaderExtractor.Cli",
    "DocxHeaderExtractor.Mcp",
    "DocxHeaderExtractor.Eval"
)
$solution = Join-Path $rootPath "DocxHeaderExtractor.sln"
$solutionText = Get-Content -Raw $solution
$missingProjects = @($requiredProjects | Where-Object { $solutionText -notmatch [regex]::Escape($_) })
Add-Finding "required-projects-in-solution" ($missingProjects.Count -eq 0) `
    ($(if ($missingProjects.Count -eq 0) { "all required projects present" } else { $missingProjects -join ", " }))

$centralPackages = Join-Path $rootPath "Directory.Packages.props"
Add-Finding "central-package-version-file" (Test-Path $centralPackages) "Directory.Packages.props"

$projectFiles = @(Get-ChildItem (Join-Path $rootPath "src"), (Join-Path $rootPath "tests") `
    -Recurse -Filter *.csproj)
$versionedReferences = @($projectFiles | Select-String -Pattern '<PackageReference\b[^>]*\bVersion=' |
    ForEach-Object { $_.Path + ":" + $_.LineNumber })
Add-Finding "no-inline-package-versions" ($versionedReferences.Count -eq 0) `
    ($(if ($versionedReferences.Count -eq 0) { "all package versions are central" } else { $versionedReferences -join "; " }))

$coreProject = Join-Path $rootPath "src\DocxHeaderExtractor.Core\DocxHeaderExtractor.Core.csproj"
$coreProjectText = Get-Content -Raw $coreProject
$coreProjectReferences = @([regex]::Matches($coreProjectText, '<ProjectReference\b[^>]*Include="([^"]+)"') |
    ForEach-Object { $_.Groups[1].Value })
Add-Finding "core-has-no-project-dependencies" ($coreProjectReferences.Count -eq 0) `
    ($(if ($coreProjectReferences.Count -eq 0) { "Core has no ProjectReference" } else { $coreProjectReferences -join ", " }))

$coreLlmFiles = @(Get-ChildItem (Join-Path $rootPath "src\DocxHeaderExtractor.DocumentProcessing\Inference") `
    -Filter *.cs -File -ErrorAction SilentlyContinue)
$coreProviderFiles = @($coreLlmFiles | Where-Object {
    (Get-Content -Raw $_.FullName) -match '\b(class|record)\s+\w*(HeaderExtractor|Provider|Runner)\b'
})
Add-Finding "provider-implementations-isolated-from-core" ($coreProviderFiles.Count -eq 0) `
    ($(if ($coreProviderFiles.Count -eq 0) { "Core contains only provider-neutral contracts/options" } else { "Core provider implementations: " + ($coreProviderFiles.Name -join ", ") }))

$coreProviderPackages = @("LLamaSharp", "Sglang") | Where-Object { $coreProjectText -match [regex]::Escape($_) }
Add-Finding "core-has-no-llm-provider-packages" ($coreProviderPackages.Count -eq 0) `
    ($(if ($coreProviderPackages.Count -eq 0) { "Core has no direct LLM provider package" } else { "Core provider packages: " + ($coreProviderPackages -join ", ") }))

$cliProject = Get-Content -Raw (Join-Path $rootPath "src\DocxHeaderExtractor.Cli\DocxHeaderExtractor.Cli.csproj")
$cliEvalReference = $cliProject -match 'DocxHeaderExtractor\.Eval'
$cliBridgeForReference = $cliEvalReference -and (Test-Path (Join-Path $rootPath "src\DocxHeaderExtractor.Cli\EvaluationProjectionBridge.cs"))
Add-Finding "cli-eval-reference-is-explicit" (!$cliEvalReference -or $cliBridgeForReference) `
    ($(if (!$cliEvalReference) { "CLI has no Eval project reference" } elseif ($cliBridgeForReference) {
        "CLI Eval reference is paired with the explicit evaluation projection bridge"
    } else { "CLI Eval reference has no explicit evaluation projection bridge" }))

$cliBridge = Join-Path $rootPath "src\DocxHeaderExtractor.Cli\EvaluationProjectionBridge.cs"
$bridgeText = if (Test-Path $cliBridge) { Get-Content -Raw $cliBridge } else { "" }
$bridgeIsExplicit = $bridgeText -match 'DHX_EVAL_ASSEMBLY' -and
    $bridgeText -match 'normal CLI extraction path never'
Add-Finding "cli-eval-access-is-explicit-only" $bridgeIsExplicit `
    ($(if ($bridgeIsExplicit) { "Eval is optional and loaded only by the explicit evaluation bridge" } else { "CLI Eval bridge is missing explicit-only guard" }))

$cliComposition = Join-Path $rootPath "src\DocxHeaderExtractor.Cli\CliHarnessComposition.cs"
$cliCompositionText = if (Test-Path $cliComposition) { Get-Content -Raw $cliComposition } else { "" }
$cliSourceBoundary = $cliCompositionText -match 'FileInputResourceResolver' -and
    $cliCompositionText -match 'SemanticRegistryDefaults\.Create'
Add-Finding "cli-uses-common-source-and-semantic-boundary" $cliSourceBoundary `
    ($(if ($cliSourceBoundary) { "CLI normal/review/eval paths compose the allowlisted source resolver and trusted semantic registry" } else { "CLI composition boundary is missing source or semantic wiring" }))

$mcpWorker = Join-Path $rootPath "src\DocxHeaderExtractor.Mcp\McpExtractionWorker.cs"
$mcpWorkerText = if (Test-Path $mcpWorker) { Get-Content -Raw $mcpWorker } else { "" }
$mcpWorkerComposition = $mcpWorkerText -match 'FileInputResourceResolver' -and
    $mcpWorkerText -match 'JsonFileTaskRunStore' -and
    $mcpWorkerText -match 'SemanticRegistryDefaults\.Create'
Add-Finding "mcp-worker-uses-common-composition" $mcpWorkerComposition `
    ($(if ($mcpWorkerComposition) { "MCP worker composes source, semantic, persistence and telemetry boundaries" } else { "MCP worker still bypasses common composition" }))

$webProgram = Join-Path $rootPath "src\DocxHeaderExtractor.Web\Program.cs"
$webText = if (Test-Path $webProgram) { Get-Content -Raw $webProgram } else { "" }
$feedbackPort = $webText -match 'IHumanFeedbackStore' -and
    $webText -match 'CorrectionMemoryFeedbackStore'
Add-Finding "web-uses-feedback-port" $feedbackPort `
    ($(if ($feedbackPort) { "Web correction endpoint uses the Application feedback port and Infrastructure adapter" } else { "Web correction endpoint still bypasses the feedback port" }))

$blocked = @($findings | Where-Object { $_.status -eq "BLOCKED" })
[ordered]@{
    artifactKind = "auto_harness_phase1_mechanical_audit"
    root = $rootPath
    status = if ($blocked.Count -eq 0) { "PASS" } else { "BLOCKED" }
    findings = $findings
} | ConvertTo-Json -Depth 5

if ($blocked.Count -gt 0) { exit 2 }
