[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Corpus,
    [Parameter(Mandatory)] [string] $BaselineSnapshots,
    [Parameter(Mandatory)] [string] $CurrentSnapshots,
    [ValidateSet('diagnostic', 'pdf')] [string] $Mode,
    [string] $Output = "r4-behavior-comparison.v1.json"
)

$ErrorActionPreference = 'Stop'

function Resolve-RepoPath([string] $Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function Read-Json([string] $Path) {
    return Get-Content -LiteralPath (Resolve-RepoPath $Path) -Raw | ConvertFrom-Json
}

function Assert-String([object] $Value, [string] $Name) {
    if ($null -eq $Value -or $Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value)) {
        throw "$Name must be a non-empty string"
    }
}

function Normalize-Number([object] $Value) {
    if ($Value -is [double] -or $Value -is [single] -or $Value -is [decimal]) {
        return [math]::Round([double]$Value, 9, [MidpointRounding]::ToEven)
    }
    return $Value
}

function Normalize-Value([object] $Value) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [string] -or $Value -is [bool] -or $Value -is [int] -or $Value -is [long] -or
        $Value -is [double] -or $Value -is [single] -or $Value -is [decimal]) {
        return Normalize-Number $Value
    }
    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [pscustomobject]) {
        return @($Value | ForEach-Object { Normalize-Value $_ })
    }
    $ordered = [ordered]@{}
    foreach ($property in ($Value.PSObject.Properties | Sort-Object Name)) {
        $ordered[$property.Name] = Normalize-Value $property.Value
    }
    return [pscustomobject]$ordered
}

function Select-Diagnostic([object] $Snapshot) {
    [ordered]@{
        documentId = $Snapshot.documentId
        status = $Snapshot.status
        reason = $Snapshot.reason
        styleSignal = $Snapshot.styleSignal
        layoutSignal = $Snapshot.layoutSignal
        candidateDiagnostics = @($Snapshot.candidateDiagnostics)
    }
}

function Select-Pdf([object] $Snapshot) {
    [ordered]@{
        documentId = $Snapshot.documentId
        retrieval = $Snapshot.retrieval
        alignment = @($Snapshot.alignment)
        visualMapping = @($Snapshot.visualMapping)
        validatedStructures = @($Snapshot.validatedStructures)
        product = @($Snapshot.product)
    }
}

function Get-StageFields([string] $Kind) {
    if ($Kind -eq 'diagnostic') {
        return @(
            @{ stage = 'diagnostic.style'; field = 'styleSignal' },
            @{ stage = 'diagnostic.layout'; field = 'layoutSignal' },
            @{ stage = 'diagnostic.candidates'; field = 'candidateDiagnostics' }
        )
    }
    return @(
        @{ stage = 'pdf.retrieval'; field = 'retrieval' },
        @{ stage = 'pdf.selection'; field = 'retrieval' },
        @{ stage = 'pdf.alignment'; field = 'alignment' },
        @{ stage = 'pdf.visualMapping'; field = 'visualMapping' },
        @{ stage = 'pdf.validation'; field = 'validatedStructures' },
        @{ stage = 'pdf.product'; field = 'product' }
    )
}

$corpusPath = Resolve-RepoPath $Corpus
$corpusObject = Get-Content -LiteralPath $corpusPath -Raw | ConvertFrom-Json
if ($null -eq $corpusObject.items -or @($corpusObject.items).Count -eq 0) { throw 'Corpus has no items' }

$corpusChecks = @()
foreach ($item in @($corpusObject.items)) {
    Assert-String $item.id "item.id"
    foreach ($kind in @('docx', 'pdf')) {
        $relative = [string]$item.$kind
        $path = Resolve-RepoPath $relative
        $expected = ([string]$item.("${kind}Sha256")).ToLowerInvariant()
        Assert-String $relative "item.$kind"
        Assert-String $expected "item.${kind}Sha256"
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "CORPUS_FILES_MISSING: $relative" }
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected) { throw "CORPUS_HASH_MISMATCH: $relative" }
        $corpusChecks += [ordered]@{ kind = $kind; path = $relative; sha256 = $actual; valid = $true }
    }
}

function Read-Snapshot([string] $Root, [string] $Id) {
    $path = Join-Path (Resolve-RepoPath $Root) "$Id.json"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "UNMEASURED: $path" }
    $snapshot = Read-Json $path
    if ($snapshot.providerCalls -ne 0 -or $snapshot.networkEnabled -eq $true -or $snapshot.liveLlm -eq $true -or $snapshot.liveVlm -eq $true) {
        throw "PROVIDER_CALLS_OR_LIVE_MODEL: $path"
    }
    return $snapshot
}

$rows = @()
$stageFields = Get-StageFields $Mode
foreach ($item in @($corpusObject.items | Where-Object { @($_.enabledFor) -contains $Mode })) {
    $baseline = Read-Snapshot $BaselineSnapshots $item.id
    $current = Read-Snapshot $CurrentSnapshots $item.id
    $baseSelected = if ($Mode -eq 'diagnostic') { Select-Diagnostic $baseline } else { Select-Pdf $baseline }
    $currentSelected = if ($Mode -eq 'diagnostic') { Select-Diagnostic $current } else { Select-Pdf $current }
    $first = $null
    foreach ($stageField in $stageFields) {
        $field = $stageField.field
        $left = Normalize-Value $baseSelected[$field]
        $right = Normalize-Value $currentSelected[$field]
        $leftJson = $left | ConvertTo-Json -Depth 40 -Compress
        $rightJson = $right | ConvertTo-Json -Depth 40 -Compress
        if ($leftJson -cne $rightJson) {
            $first = [ordered]@{ stage = $stageField.stage; field = $field; baseline = $left; current = $right; classification = 'UNCLASSIFIED' }
            break
        }
    }
    $rows += [ordered]@{ documentId = $item.id; equal = ($null -eq $first); firstDivergence = $first }
}

$deltas = @($rows | Where-Object { -not $_.equal })
$result = [ordered]@{
    schemaVersion = 1
    artifactKind = 'r4_behavior_comparison'
    mode = $Mode
    corpus = [IO.Path]::GetRelativePath((Get-Location).Path, $corpusPath).Replace('\', '/')
    corpusFilesMissing = 0
    corpusHashMismatch = 0
    sameCorpusAllRevisions = $true
    baselineSnapshots = (Resolve-RepoPath $BaselineSnapshots)
    currentSnapshots = (Resolve-RepoPath $CurrentSnapshots)
    providerCalls = 0
    unmeasured = 0
    joined = ($rows.Count - $deltas.Count)
    deltas = $deltas
    gate = if ($deltas.Count -eq 0) { 'PASS' } else { 'BLOCKED_DELTA_UNCLASSIFIED' }
}
$outputPath = Resolve-RepoPath $Output
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outputPath) | Out-Null
$result | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $outputPath -Encoding utf8
Write-Output ($result | ConvertTo-Json -Depth 40)
if ($result.gate -ne 'PASS') { exit 2 }
