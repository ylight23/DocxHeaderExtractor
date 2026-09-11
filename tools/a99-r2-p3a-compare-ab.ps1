param(
    [Parameter(Mandatory = $true)][string]$DirectoryA,
    [Parameter(Mandatory = $true)][string]$DirectoryB,
    [string]$PacketDirectory = 'eval/a99-closed-loop/research-r2/p3-human-gold/packets',
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath (git rev-parse --show-toplevel)
function Read-Json([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
function AsArray($Value) { if ($null -eq $Value) { return @() }; return @($Value) }

$aById = @{}
$bById = @{}
foreach ($file in Get-ChildItem -LiteralPath $DirectoryA -Filter '*.annotation.v1.json' -File) { $aById[[string](Read-Json $file.FullName).documentId] = Read-Json $file.FullName }
foreach ($file in Get-ChildItem -LiteralPath $DirectoryB -Filter '*.annotation.v1.json' -File) { $bById[[string](Read-Json $file.FullName).documentId] = Read-Json $file.FullName }
$ids = @($aById.Keys + $bById.Keys | Sort-Object -Unique)
$rows = @()
foreach ($id in $ids) {
    $aRows = if ($aById.ContainsKey($id)) { @(AsArray $aById[$id].rows) } else { @() }
    $bRows = if ($bById.ContainsKey($id)) { @(AsArray $bById[$id].rows) } else { @() }
    $aByOrdinal = @{}; foreach ($row in $aRows) { $aByOrdinal[[int]$row.humanHeadingOrdinal] = $row }
    $bByOrdinal = @{}; foreach ($row in $bRows) { $bByOrdinal[[int]$row.humanHeadingOrdinal] = $row }
    $ordinals = @($aByOrdinal.Keys + $bByOrdinal.Keys | Sort-Object -Unique)
    foreach ($ordinal in $ordinals) {
        $hasA = $aByOrdinal.ContainsKey($ordinal); $hasB = $bByOrdinal.ContainsKey($ordinal)
        if (-not $hasA) { $class = 'B_ONLY' }
        elseif (-not $hasB) { $class = 'A_ONLY' }
        else {
            $a = $aByOrdinal[$ordinal]; $b = $bByOrdinal[$ordinal]
            $sameText = [string]$a.documentTitleText -ceq [string]$b.documentTitleText
            $sameHint = [string]$a.sourceOccurrenceHint -ceq [string]$b.sourceOccurrenceHint
            $sameFamily = [string]$a.semanticFamily -ceq [string]$b.semanticFamily
            if (-not $sameFamily) { $class = 'SEMANTIC_DISAGREEMENT' }
            elseif (-not $sameText) { $class = 'TEXT_VARIANCE' }
            elseif (-not $sameHint) { $class = 'OCCURRENCE_VARIANCE' }
            else { $class = 'AGREE' }
        }
        $rows += [ordered]@{ documentId = $id; humanHeadingOrdinal = [int]$ordinal; classification = $class; requiresHumanAdjudication = ($class -ne 'AGREE') }
    }
}
$result = [ordered]@{
    schemaVersion = 'a99-r2-p3a-ab-comparison-v1'
    directoryA = $DirectoryA
    directoryB = $DirectoryB
    rows = $rows
    status = if ($rows.Count -eq 0) { 'NO_HUMAN_ROWS_YET' } else { 'COMPARISON_ONLY_ADJUDICATION_REQUIRED' }
    semanticDecisionMadeByTool = $false
    silentUnion = $false
    fuzzyMatching = $false
    llmCalls = 0
    providerCalls = 0
}
$json = $result | ConvertTo-Json -Depth 20
if ($OutputPath) { $json | Set-Content -LiteralPath $OutputPath -Encoding UTF8 }
else { $json }
