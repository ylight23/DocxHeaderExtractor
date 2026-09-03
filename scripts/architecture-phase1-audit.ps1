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

$coreProviderFiles = @(Get-ChildItem (Join-Path $rootPath "src\DocxHeaderExtractor.Core\Llm") `
    -Filter *.cs -File -ErrorAction SilentlyContinue)
Add-Finding "provider-implementations-isolated-from-core" ($coreProviderFiles.Count -eq 0) `
    ($(if ($coreProviderFiles.Count -eq 0) { "no Core/Llm implementation files" } else { "Core/Llm contains " + ($coreProviderFiles.Name -join ", ") }))

$cliProject = Get-Content -Raw (Join-Path $rootPath "src\DocxHeaderExtractor.Cli\DocxHeaderExtractor.Cli.csproj")
Add-Finding "cli-does-not-reference-eval" ($cliProject -notmatch 'DocxHeaderExtractor\.Eval') `
    ($(if ($cliProject -notmatch 'DocxHeaderExtractor\.Eval') { "CLI has no Eval project reference" } else { "CLI still references DocxHeaderExtractor.Eval" }))

$blocked = @($findings | Where-Object { $_.status -eq "BLOCKED" })
[ordered]@{
    artifactKind = "auto_harness_phase1_mechanical_audit"
    root = $rootPath
    status = if ($blocked.Count -eq 0) { "PASS" } else { "BLOCKED" }
    findings = $findings
} | ConvertTo-Json -Depth 5

if ($blocked.Count -gt 0) { exit 2 }
