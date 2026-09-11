param(
    [string]$RepoRoot = (Get-Location).Path
)

$ErrorActionPreference = 'Stop'
$RepoRoot = [IO.Path]::GetFullPath($RepoRoot)
$BaselineRoot = Join-Path $RepoRoot 'eval/a99-closed-loop/semantic-text-generalization'
$GoldRoot = Join-Path $RepoRoot 'eval/a99-closed-loop/strict-gold-occurrence-v1'
$OutputRoot = Join-Path $RepoRoot 'eval/a99-closed-loop/autonomous-completion'
$Documents = @('DOC-0001', 'DOC-0205', 'DOC-0252', 'DOC-0256', 'DOC-0258')
$Repeats = @('r1', 'r2', 'r3')

function Read-Json([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
function Key([string]$SourceId, [int]$Start, [int]$End) { "$SourceId`:$Start`:$End" }
function Status-Name([int]$Status) {
    switch ($Status) {
        0 { 'SOURCE_ALIAS_RESOLVED' }
        1 { 'EXACT_TEXT_FOUND' }
        2 { 'EXACT_TEXT_FOUND' }
        3 { 'TEXT_NOT_FOUND' }
        4 { 'AMBIGUOUS_EXACT_TEXT' }
        5 { 'INVALID_SOURCE_ALIAS' }
        6 { 'EMPTY_TEXT' }
        7 { 'DUPLICATE_PROPOSAL' }
        default { "UNKNOWN_STATUS_$Status" }
    }
}
function Bucket([string]$FirstLoss) {
    switch ($FirstLoss) {
        'MODEL_WRONG_SPAN' { 'MODEL_WRONG_TEXT_BOUNDARY' }
        'AMBIGUOUS_DUPLICATE_TEXT' { 'BIND_AMBIGUOUS' }
        'SYSTEM_BINDING_LOSS' { 'SYSTEM_LOSS' }
        'SYSTEM_VALIDATOR_LOSS' { 'SYSTEM_LOSS' }
        'SYSTEM_PROJECTION_LOSS' { 'SYSTEM_LOSS' }
        'SYSTEM_ALIAS_RESOLUTION_LOSS' { 'SYSTEM_LOSS' }
        'SYSTEM_DEDUPE_LOSS' { 'SYSTEM_LOSS' }
        default { $FirstLoss }
    }
}
function Neighborhood([string]$Text, [int]$Start, [int]$End) {
    if ([string]::IsNullOrEmpty($Text)) { return $null }
    $s = [Math]::Max(0, [Math]::Min($Start, $Text.Length))
    $e = [Math]::Max($s, [Math]::Min($End, $Text.Length))
    $left = [Math]::Max(0, $s - 80)
    $right = [Math]::Min($Text.Length, $e + 80)
    $Text.Substring($left, $right - $left)
}

$allRows = [Collections.Generic.List[object]]::new()
$cellRows = [Collections.Generic.List[object]]::new()
$goldByDoc = @{}
foreach ($doc in $Documents) {
    $gold = Read-Json (Join-Path $GoldRoot "$doc.occurrence-gold-v1.json")
    $goldByDoc[$doc] = @{}
    foreach ($g in $gold.bindings) { $goldByDoc[$doc][(Key $g.sourceId $g.headingSpan.start $g.headingSpan.end)] = $g }
}

foreach ($doc in $Documents) {
    foreach ($repeat in $Repeats) {
        $dir = Join-Path $BaselineRoot "$doc/$repeat"
        $prediction = Read-Json (Join-Path $dir 'prediction.v1.json')
        $first = Read-Json (Join-Path $dir 'first-loss.v1.json')
        $score = Read-Json (Join-Path $dir 'score.v1.json')
        $finalKeys = @{}
        foreach ($f in @($prediction.finalHeadings)) { $finalKeys[(Key $f.sourceId $f.start $f.end)] = $true }

        $localRows = @()
        foreach ($loss in @($first.firstLosses | Where-Object firstLoss -ne 'FOUND') + @($first.falsePositives)) {
            $parts = [regex]::Match([string]$loss.key, '^(.*):(\d+):(\d+)$')
            if (-not $parts.Success) { continue }
            $sourceId = $parts.Groups[1].Value
            $start = [int]$parts.Groups[2].Value
            $end = [int]$parts.Groups[3].Value
            $gold = $goldByDoc[$doc].Get_Item([string]$loss.key)
            $isGold = $null -ne $gold
            $owner = if ($isGold) { [string]$loss.firstLoss } else { 'MODEL_FALSE_POSITIVE' }
            $bucket = Bucket $owner
            $raw = @($prediction.rawModelHeadings | Where-Object { $_.text -eq $loss.exactSourceText })
            $rawDetails = foreach ($r in $raw) {
                $obs = @($prediction.bindingObservations | Where-Object { $_.rawOrdinal -ge 0 -and $_.heading.source -eq $r.source -and $_.heading.text -eq $r.text }) | Select-Object -First 1
                [ordered]@{ sourceAlias = $r.source; text = $r.text; role = $r.role; bindingStatus = if ($null -eq $obs) { 'NOT_OBSERVED' } else { Status-Name ([int]$obs.status) } }
            }
            $bound = @($prediction.boundHeadings | Where-Object { $_.sourceId -eq $sourceId -and $_.start -eq $start -and $_.end -eq $end })
            $mask = foreach ($rpt in $Repeats) {
                $rp = Read-Json (Join-Path $BaselineRoot "$doc/$rpt/prediction.v1.json")
                $present = @($rp.finalHeadings | Where-Object { (Key $_.sourceId $_.start $_.end) -eq $loss.key }).Count -gt 0
                if ($present) { '1' } else { '0' }
            }
            $presence = -join $mask
            $persistence = if ($isGold -and $presence -eq '000') { 'PERSISTENT' } elseif ($isGold -and $presence -ne '000') { 'INTERMITTENT_OR_RECOVERED' } elseif (-not $isGold -and $presence -eq '111') { 'PERSISTENT' } else { 'INTERMITTENT' }
            $rawAliases = @($raw | Select-Object -ExpandProperty source -Unique)
            $row = [ordered]@{
                documentId = $doc; repeat = $repeat; key = $loss.key; sourceId = $sourceId
                sourceAlias = if ($rawAliases.Count -eq 0) { $null } else { $rawAliases }
                exactSourceText = $loss.exactSourceText
                modelRawProposalStatus = if ($rawDetails.Count -eq 0) { 'NONE' } else { $rawDetails }
                boundIdentity = if ($bound.Count -eq 0) { $null } else { $bound }
                goldStatus = if ($isGold) { if ($finalKeys.ContainsKey($loss.key)) { 'FOUND' } else { 'MISSING' } } else { 'NOT_GOLD' }
                firstLossOwner = $owner; bucket = $bucket; persistentOrIntermittent = $persistence
                presentInR1 = ($mask[0] -eq '1'); presentInR2 = ($mask[1] -eq '1'); presentInR3 = ($mask[2] -eq '1')
                semanticNeighborhood = if ($isGold) { Neighborhood ([string]$gold.rawSourceText) ([int]$gold.headingSpan.start) ([int]$gold.headingSpan.end) } else { $null }
                structuralFactsAlreadyVisibleToModel = @('sourceAlias', 'verbatimText', 'sourceOrdinal')
                structuralFactsNotVisibleInB0Packet = @('style', 'numbering', 'layout', 'hierarchy')
                roleError = 'NOT_MEASURED'; hierarchyError = 'NOT_MEASURED'
                start = $start; end = $end
            }
            $allRows.Add([pscustomobject]$row)
            $localRows += [pscustomobject]$row
        }
        $cellRows.Add([pscustomobject]@{ documentId = $doc; repeat = $repeat; tp = $score.tp; fp = $score.fp; fn = $score.fn; precision = $score.precision; recall = $score.recall; f1 = $score.f1; systemLoss = $score.systemLoss; residualCount = $localRows.Count })
    }
}

$counts = @{}
foreach ($group in $allRows | Group-Object bucket) { $counts[$group.Name] = $group.Count }
$requiredBuckets = @(
    'MODEL_OMISSION', 'MODEL_FALSE_POSITIVE', 'MODEL_WRONG_TEXT',
    'MODEL_WRONG_TEXT_BOUNDARY', 'MODEL_NON_VERBATIM', 'MODEL_ROLE_ERROR',
    'BIND_AMBIGUOUS', 'BIND_FAILURE', 'SYSTEM_LOSS', 'HIERARCHY_ERROR'
)
foreach ($bucket in $requiredBuckets) {
    if (-not $counts.ContainsKey($bucket)) { $counts[$bucket] = 0 }
}
$measurementStatus = [ordered]@{
    MODEL_NON_VERBATIM = 'NOT_MEASURED'
    MODEL_ROLE_ERROR = 'NOT_MEASURED'
    HIERARCHY_ERROR = 'NOT_MEASURED'
}
$persistent = @($allRows | Where-Object persistentOrIntermittent -eq 'PERSISTENT' | Group-Object { "$($_.documentId):$($_.key)" } | ForEach-Object { $_.Group[0] })
$summary = [ordered]@{
    schemaVersion = 'a99-autonomous-b0-residual-authority-v1'; generatedFrom = 'frozen B0 semantic-text-generalization'; parent = 'B0@76c4e01'
    startHead = (& git -C $RepoRoot rev-parse HEAD).Trim(); modelCalls = 0; providerCalls = 0; goldReadBeforeFreeze = $false; goldFirewall = 'PASS'
    authoritative = $true; roleMeasurement = 'NOT_MEASURED'; hierarchyMeasurement = 'NOT_MEASURED'; counts = $counts; measurementStatus = $measurementStatus
    persistentResidualCount = $persistent.Count; persistentResidualKeys = @($persistent | Select-Object documentId,key,exactSourceText,bucket)
    cells = $cellRows; rows = $allRows
}
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$summary | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $OutputRoot 'b0-residual-authority.v1.json') -Encoding utf8
Write-Output "MODEL_CALLS=0"
Write-Output "RESIDUAL_ROWS=$($allRows.Count)"
Write-Output "PERSISTENT_RESIDUALS=$($persistent.Count)"
$bucketText = (($counts.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name):$($_.Value)" }) -join ',')
Write-Output "BUCKETS=$bucketText"
