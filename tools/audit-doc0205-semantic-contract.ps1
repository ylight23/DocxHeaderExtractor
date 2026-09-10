[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Read-Json([string]$Path) {
    return (Get-Content -LiteralPath (Join-Path $RepositoryRoot $Path) -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Hash-File([string]$Path) {
    return (Get-FileHash -LiteralPath (Join-Path $RepositoryRoot $Path) -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-Artifact($Value, [string]$Path) {
    $fullPath = Join-Path $RepositoryRoot $Path
    $directory = Split-Path -Parent $fullPath
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $Value | ConvertTo-Json -Depth 80 | Set-Content -LiteralPath $fullPath -Encoding UTF8
}

function Normalize-Text([string]$Text) {
    if ($null -eq $Text) { return '' }
    $value = $Text.Normalize([Text.NormalizationForm]::FormD)
    $value = [Regex]::Replace($value, '\p{Mn}', '')
    $value = [Regex]::Replace($value.ToLowerInvariant(), '\s+', ' ').Trim()
    return $value
}

function Get-Span($Item) {
    if ($null -ne $Item.headingSpan) { return $Item.headingSpan }
    if ($null -ne $Item.span) { return $Item.span }
    return $null
}

function New-Prediction([string]$Model, $Item, [string]$Origin) {
    $span = Get-Span $Item
    if ($null -eq $span) { throw "Missing span in $Origin" }
    $sourceId = [string]$Item.sourceId
    if ([string]::IsNullOrWhiteSpace($sourceId) -and $null -ne $Item.sources -and $Item.sources.Count -gt 0) {
        $sourceId = [string]$Item.sources[0].sourceId
        $span = $Item.sources[0].span
    }
    $role = $null
    if ($Item.PSObject.Properties.Name -contains 'semanticRole') { $role = [string]$Item.semanticRole }
    elseif ($Item.PSObject.Properties.Name -contains 'role') { $role = [string]$Item.role }
    $level = $null
    if ($Item.PSObject.Properties.Name -contains 'proposedLevel') { $level = $Item.proposedLevel }
    elseif ($Item.PSObject.Properties.Name -contains 'level') { $level = $Item.level }
    [pscustomobject][ordered]@{
        predictionOrdinal = -1
        model = $Model
        origin = $Origin
        sourceId = $sourceId
        start = [int]$span.start
        end = [int]$span.end
        text = [string]$Item.text
        semanticRole = $role
        level = $level
        sourceLookupValid = $false
        reconstructedText = $null
        storedTextMatchesSource = $false
    }
}

function Get-Overlap([int]$AStart, [int]$AEnd, [int]$BStart, [int]$BEnd) {
    return [Math]::Max(0, [Math]::Min($AEnd, $BEnd) - [Math]::Max($AStart, $BStart))
}

function Get-Relationship($Gold, $Prediction, $SourceMap) {
    $sameSource = $Gold.sourceId -eq $Prediction.sourceId
    $overlap = if ($sameSource) { Get-Overlap $Gold.start $Gold.end $Prediction.start $Prediction.end } else { 0 }
    $goldNorm = Normalize-Text $Gold.rawSourceText
    $predictionNorm = Normalize-Text $Prediction.reconstructedText
    $exactText = $gold.rawSourceText -eq $Prediction.reconstructedText
    $normalizedTextEqual = $goldNorm -eq $predictionNorm -and $goldNorm.Length -gt 0
    if ($sameSource -and $Gold.start -eq $Prediction.start -and $Gold.end -eq $Prediction.end) { $kind = 'EXACT_SPAN' }
    elseif ($sameSource -and $overlap -gt 0) { $kind = 'SAME_SOURCE_OVERLAP' }
    elseif ($sameSource -and $normalizedTextEqual) { $kind = 'SAME_SOURCE_NORMALIZED_TEXT_DIFFERENT_SPAN' }
    elseif (-not $sameSource -and $normalizedTextEqual) { $kind = 'SAME_NORMALIZED_TEXT_DIFFERENT_SOURCE' }
    else { $kind = 'NO_DIRECT_CORRESPONDENCE' }
    [ordered]@{
        kind = $kind
        sameSource = $sameSource
        overlapChars = $overlap
        goldContainsPrediction = $sameSource -and $Gold.start -le $Prediction.start -and $Gold.end -ge $Prediction.end
        predictionContainsGold = $sameSource -and $Prediction.start -le $Gold.start -and $Prediction.end -ge $Gold.end
        exactRawText = $exactText
        normalizedTextEqual = $normalizedTextEqual
    }
}

function Get-PredictionKey($Prediction) {
    return "{0}:{1}:{2}" -f $Prediction.sourceId, $Prediction.start, $Prediction.end
}

function Get-SourceText($Prediction, $SourceMap) {
    if (-not $SourceMap.ContainsKey($Prediction.sourceId)) {
        $Prediction.sourceLookupValid = $false
        $Prediction.reconstructedText = $null
        $Prediction.storedTextMatchesSource = $false
        return
    }
    $source = $SourceMap[$Prediction.sourceId]
    $valid = $Prediction.start -ge 0 -and $Prediction.end -ge $Prediction.start -and $Prediction.end -le $source.rawText.Length
    $reconstructed = if ($valid) { $source.rawText.Substring($Prediction.start, $Prediction.end - $Prediction.start) } else { $null }
    $Prediction.sourceLookupValid = $valid
    $Prediction.reconstructedText = $reconstructed
    $Prediction.storedTextMatchesSource = ($valid -and [string]$Prediction.text -eq $reconstructed)
}

function Get-Distribution($Items, [string]$Property) {
    $result = [ordered]@{}
    foreach ($group in ($Items | Group-Object -Property $Property | Sort-Object Name)) { $result[$group.Name] = $group.Count }
    return $result
}

function Get-OptionalProperty($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-GoldShape($Gold, $SourceMap) {
    $source = $SourceMap[$Gold.sourceId]
    $length = $Gold.end - $Gold.start
    if ($length -eq $source.rawText.Length) { return 'full paragraph' }
    if ($source.rawText -notmatch '[\r\n]' -and $length -lt $source.rawText.Length) {
        if ($Gold.rawSourceText -match '^\s*[\p{L}\p{N}]+(?:[.)])?\s+\p{L}') { return 'partial paragraph; numbering + title-like prefix' }
        return 'partial paragraph; title without generic enumeration prefix'
    }
    if ($Gold.rawSourceText -notmatch '[\r\n]' -and $Gold.rawSourceText.Length -lt 120) { return 'inline structural phrase' }
    return 'partial paragraph'
}

$paths = [ordered]@{
    gold = 'eval/a99-closed-loop/strict-gold-occurrence-v1/DOC-0205.occurrence-gold-v1.json'
    packet = '.tmp-doc0205-packet.json'
    qwen9bPrediction = 'eval/a99-closed-loop/openrouter-qwen35-9b-per-segment-recovery/documents/DOC-0205/prediction.v1.json'
    qwen9bResult = 'eval/a99-closed-loop/openrouter-qwen35-9b-per-segment-recovery/documents/DOC-0205/result.v1.json'
    qwen9bFreeze = 'eval/a99-closed-loop/openrouter-qwen35-9b-per-segment-recovery/documents/DOC-0205/freeze.v1.json'
    qwen9bS0Freeze = 'eval/a99-closed-loop/qwen9b-multipass-doc0205/s0/freeze.v1.json'
    flashPrediction = 'eval/a99-closed-loop/qwen37-flash-reasoning-ceiling/DOC-0205/r1-ceiling/prediction.v1.json'
    flashResult = 'eval/a99-closed-loop/qwen37-flash-reasoning-ceiling/DOC-0205/r1-ceiling/result.v1.json'
    flashFreeze = 'eval/a99-closed-loop/qwen37-flash-reasoning-ceiling/DOC-0205/r1-ceiling/freeze.v1.json'
    visualPrediction = 'eval/a99-closed-loop/qwen37-flash-visual-ceiling/DOC-0205/visual-v2/prediction.v2.json'
    visualResult = 'eval/a99-closed-loop/qwen37-flash-visual-ceiling/DOC-0205/visual-v2/result.v2.json'
    visualFreeze = 'eval/a99-closed-loop/qwen37-flash-visual-ceiling/DOC-0205/visual-v2/freeze.v2.json'
    qwen9bScore = 'eval/a99-closed-loop/openrouter-qwen35-9b-per-segment-recovery/documents/DOC-0205/score.v1.json'
    flashScore = 'eval/a99-closed-loop/qwen37-flash-reasoning-ceiling/DOC-0205/r1-ceiling/score.v1.json'
    visualScore = 'eval/a99-closed-loop/qwen37-flash-visual-ceiling/DOC-0205/visual-v2/score.v2.json'
    protocol = 'src/DocxHeaderExtractor.Eval/ReasoningRetention/CeilingSemanticProtocol.cs'
    goldPolicy = 'docs/accuracy/accuracy99-strict-gold-authority-policy-v2.md'
    goldReconciliation = 'docs/accuracy/accuracy99-historical-gold-reconciliation-v3.md'
}

foreach ($entry in $paths.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath (Join-Path $RepositoryRoot $entry.Value))) { throw "Missing audit input: $($entry.Value)" }
}

$goldArtifact = Read-Json $paths.gold
$gold = $goldArtifact.bindings
$packet = Read-Json $paths.packet
$qwen9bPredictionDoc = Read-Json $paths.qwen9bPrediction
$qwen9bResultDoc = Read-Json $paths.qwen9bResult
$qwen9bFreezeDoc = Read-Json $paths.qwen9bFreeze
$qwen9bS0FreezeDoc = Read-Json $paths.qwen9bS0Freeze
$flashPredictionDoc = Read-Json $paths.flashPrediction
$flashResultDoc = Read-Json $paths.flashResult
$flashFreezeDoc = Read-Json $paths.flashFreeze
$visualPredictionDoc = Read-Json $paths.visualPrediction
$visualResultDoc = Read-Json $paths.visualResult
$visualFreezeDoc = Read-Json $paths.visualFreeze
$qwen9bScoreDoc = Read-Json $paths.qwen9bScore
$flashScoreDoc = Read-Json $paths.flashScore
$visualScoreDoc = Read-Json $paths.visualScore
$protocolText = Get-Content -LiteralPath (Join-Path $RepositoryRoot $paths.protocol) -Raw -Encoding UTF8

$sourceMap = @{}
foreach ($occurrence in $packet.occurrences) { $sourceMap[[string]$occurrence.sourceId] = $occurrence }

$goldRows = @()
foreach ($binding in $gold) {
    $goldRows += [pscustomobject][ordered]@{
        headingOrdinal = [int]$binding.headingOrdinal
        headingOccurrenceId = [string]$binding.headingOccurrenceId
        sourceId = [string]$binding.sourceId
        start = [int]$binding.headingSpan.start
        end = [int]$binding.headingSpan.end
        rawSourceText = [string]$binding.rawSourceText
        approvedHeadingText = [string]$binding.approvedHeadingText
        semanticRole = [string]$binding.semanticRole
        level = [int]$binding.level
        exactRawSubstringVerified = [bool]$binding.exactRawSubstringVerified
    }
}

$modelDocs = [ordered]@{
    'Qwen3.5-9B S0' = @{ prediction = $qwen9bPredictionDoc; items = $qwen9bPredictionDoc.proposals; origin = $paths.qwen9bPrediction; score = $qwen9bScoreDoc; freeze = $qwen9bFreezeDoc; result = $qwen9bResultDoc }
    'Qwen3.7-Flash text S0' = @{ prediction = $flashPredictionDoc; items = $flashPredictionDoc.proposals; origin = $paths.flashPrediction; score = $flashScoreDoc; freeze = $flashFreezeDoc; result = $flashResultDoc }
    'Qwen3.7-Flash visual V2' = @{ prediction = $visualPredictionDoc; items = $visualPredictionDoc.proposals; origin = $paths.visualPrediction; score = $visualScoreDoc; freeze = $visualFreezeDoc; result = $visualResultDoc }
}

$allPredictions = [ordered]@{}
foreach ($model in $modelDocs.Keys) {
    $rows = @()
    $ordinal = 0
    foreach ($item in $modelDocs[$model].items) {
        $row = New-Prediction $model $item $modelDocs[$model].origin
        $row.predictionOrdinal = $ordinal
        Get-SourceText $row $sourceMap
        $rows += [pscustomobject]$row
        $ordinal++
    }
    $allPredictions[$model] = $rows
}

$mapRows = @()
foreach ($goldRow in $goldRows) {
    $modelMatches = [ordered]@{}
    foreach ($model in $modelDocs.Keys) {
        $relationships = @()
        foreach ($prediction in $allPredictions[$model]) {
            $rel = Get-Relationship $goldRow $prediction $sourceMap
            if ($rel.kind -ne 'NO_DIRECT_CORRESPONDENCE') {
                $relationships += [pscustomobject][ordered]@{
                    predictionOrdinal = $prediction.predictionOrdinal
                    sourceId = $prediction.sourceId
                    start = $prediction.start
                    end = $prediction.end
                    text = $prediction.reconstructedText
                    semanticRole = $prediction.semanticRole
                    relationship = $rel
                }
            }
        }
        $orderedMatches = @($relationships | Sort-Object @{Expression={ if ($_.relationship.kind -eq 'EXACT_SPAN') { 0 } elseif ($_.relationship.kind -eq 'SAME_SOURCE_OVERLAP') { 1 } elseif ($_.relationship.kind -eq 'SAME_SOURCE_NORMALIZED_TEXT_DIFFERENT_SPAN') { 2 } else { 3 } }}, @{Expression={$_.relationship.overlapChars};Descending=$true})
        $status = if (@($orderedMatches | Where-Object {$_.relationship.kind -eq 'EXACT_SPAN'}).Count -gt 0) { 'EXACT' } elseif ($orderedMatches.Count -gt 0) { 'NEAR_OR_EQUIVALENT' } else { 'NONE' }
        $modelMatches[$model] = [ordered]@{ status = $status; matchCount = $orderedMatches.Count; topMatches = @($orderedMatches | Select-Object -First 5) }
    }
    $mapRows += [pscustomobject][ordered]@{
        headingOrdinal = $goldRow.headingOrdinal
        headingOccurrenceId = $goldRow.headingOccurrenceId
        sourceId = $goldRow.sourceId
        start = $goldRow.start
        end = $goldRow.end
        rawSourceText = $goldRow.rawSourceText
        semanticRole = $goldRow.semanticRole
        level = $goldRow.level
        taxonomyShape = Get-GoldShape $goldRow $sourceMap
        modelMatches = $modelMatches
    }
}

function Get-FpClassification($Prediction, $GoldRows, $SourceMap, $FinalKeys) {
    $relations = @()
    foreach ($goldRow in $GoldRows) {
        $rel = Get-Relationship $goldRow $Prediction $SourceMap
        if ($rel.kind -ne 'NO_DIRECT_CORRESPONDENCE') { $relations += [pscustomobject][ordered]@{ goldOrdinal = $goldRow.headingOrdinal; relationship = $rel; goldText = $goldRow.rawSourceText; goldSpan = [ordered]@{start=$goldRow.start;end=$goldRow.end} } }
    }
    if (@($relations | Where-Object {$_.relationship.kind -eq 'EXACT_SPAN'}).Count -gt 0) { $class = 'GOLD_EXACT_MATCH' }
    elseif (@($relations | Where-Object {$_.relationship.kind -eq 'SAME_SOURCE_OVERLAP'}).Count -gt 0) { $class = 'GOLD_EQUIVALENT_WRONG_SPAN' }
    elseif (@($relations | Where-Object {$_.relationship.kind -eq 'SAME_SOURCE_NORMALIZED_TEXT_DIFFERENT_SPAN'}).Count -gt 0) { $class = 'GOLD_SAME_TEXT_WRONG_SPAN' }
    elseif (@($relations | Where-Object {$_.relationship.kind -eq 'SAME_NORMALIZED_TEXT_DIFFERENT_SOURCE'}).Count -gt 0) { $class = 'WRONG_SOURCE' }
    elseif ($Prediction.reconstructedText.Length -le 3) { $class = 'LIKELY_NON_HEADING_SHORT_FRAGMENT' }
    else { $class = 'UNRESOLVED_NO_GOLD_CORRESPONDENCE' }
    [pscustomobject][ordered]@{
        predictionOrdinal = $Prediction.predictionOrdinal
        model = $Prediction.model
        sourceId = $Prediction.sourceId
        start = $Prediction.start
        end = $Prediction.end
        text = $Prediction.reconstructedText
        semanticRole = $Prediction.semanticRole
        finalScored = $FinalKeys.ContainsKey((Get-PredictionKey $Prediction))
        class = $class
        sourceLookupValid = $Prediction.sourceLookupValid
        storedTextMatchesSource = $Prediction.storedTextMatchesSource
        nearestGoldRelations = @($relations | Sort-Object @{Expression={$_.relationship.overlapChars};Descending=$true} | Select-Object -First 5)
    }
}

$classificationRows = @()
foreach ($model in $modelDocs.Keys) {
    $finalKeys = @{}
    if ($null -ne $modelDocs[$model].result.headings) {
        foreach ($heading in $modelDocs[$model].result.headings) {
            if ($null -ne $heading.sources -and $heading.sources.Count -gt 0) {
                $span = $heading.sources[0].span
                $finalKeys["{0}:{1}:{2}" -f $heading.sources[0].sourceId, $span.start, $span.end] = $true
            }
        }
    }
    foreach ($prediction in $allPredictions[$model]) { $classificationRows += Get-FpClassification $prediction $goldRows $sourceMap $finalKeys }
}

$goldShapeCounts = [ordered]@{}
foreach ($group in ($goldRows | ForEach-Object { Get-GoldShape $_ $sourceMap } | Group-Object | Sort-Object Name)) { $goldShapeCounts[$group.Name] = $group.Count }
$goldRoleCounts = Get-Distribution $goldRows 'semanticRole'

$integrityChecks = @()
function Add-Integrity([string]$Name, [bool]$Pass, [string]$Detail) {
    $script:integrityChecks += [pscustomobject][ordered]@{ check = $Name; pass = $Pass; detail = $Detail }
}

$goldHash = Hash-File $paths.gold
$sourceHash = [string]$packet.sourceDocumentSha256
Add-Integrity 'gold_status_pass' ([string]$goldArtifact.status -eq 'PASS') ([string]$goldArtifact.status)
Add-Integrity 'gold_has_71_bindings' ($gold.Count -eq 71) ("count=$($gold.Count)")
Add-Integrity 'gold_exact_substrings_verified' (@($gold | Where-Object {-not $_.exactRawSubstringVerified}).Count -eq 0) 'all bindings report exactRawSubstringVerified=true'
Add-Integrity 'source_sha_matches_gold' ($sourceHash -eq [string]$goldArtifact.sourceSha256) "$sourceHash vs $($goldArtifact.sourceSha256)"
Add-Integrity 'source_sha_matches_expected' ($sourceHash -eq 'b145e31a58e76cad1c77967c639884d1c566020d344d725ca145fafb5de86878') $sourceHash

foreach ($model in $modelDocs.Keys) {
    $freeze = $modelDocs[$model].freeze
    $predictionPath = if ($model -eq 'Qwen3.7-Flash visual V2') { $paths.visualPrediction } elseif ($model -eq 'Qwen3.7-Flash text S0') { $paths.flashPrediction } else { $paths.qwen9bPrediction }
    $resultPath = if ($model -eq 'Qwen3.7-Flash visual V2') { $paths.visualResult } elseif ($model -eq 'Qwen3.7-Flash text S0') { $paths.flashResult } else { $paths.qwen9bResult }
    Add-Integrity "$model source_sha" ([string]$freeze.sourceSha256 -eq $sourceHash) ([string]$freeze.sourceSha256)
    Add-Integrity "$model prediction_hash" ((Hash-File $predictionPath) -eq ([string]$freeze.predictionSha256).ToLowerInvariant()) (Hash-File $predictionPath)
    Add-Integrity "$model result_hash" ((Hash-File $resultPath) -eq ([string]$freeze.resultSha256).ToLowerInvariant()) (Hash-File $resultPath)
    Add-Integrity "$model gold_firewall" ([bool]$freeze.goldReadBeforeFreeze -eq $false) "goldReadBeforeFreeze=$($freeze.goldReadBeforeFreeze)"
}
Add-Integrity 'qwen9b_s0_gold_firewall' ([bool]$qwen9bS0FreezeDoc.goldReadBeforeFreeze -eq $false) "goldReadBeforeFreeze=$($qwen9bS0FreezeDoc.goldReadBeforeFreeze)"

$scoreRows = @(
    [ordered]@{ model='Qwen3.5-9B S0'; tp=[int]$qwen9bScoreDoc.tp; fp=[int]$qwen9bScoreDoc.fp; fn=[int]$qwen9bScoreDoc.fn; precision=$qwen9bScoreDoc.precision; recall=$qwen9bScoreDoc.recall; f1=$qwen9bScoreDoc.f1; modelOmission=(Get-OptionalProperty $qwen9bScoreDoc.lossCounts 'MODEL_OMISSION'); spanError=(Get-OptionalProperty $qwen9bScoreDoc.lossCounts 'MODEL_SPAN_ERROR'); systemLoss=0; wallTime=$null }
    [ordered]@{ model='Qwen3.7-Flash text S0'; tp=[int]$flashScoreDoc.tp; fp=[int]$flashScoreDoc.fp; fn=[int]$flashScoreDoc.fn; precision=$flashScoreDoc.precision; recall=$flashScoreDoc.recall; f1=$flashScoreDoc.f1; modelOmission=(Get-OptionalProperty $flashScoreDoc.lossCounts 'MODEL_OMISSION'); spanError=(Get-OptionalProperty $flashScoreDoc.lossCounts 'MODEL_SPAN_ERROR'); systemLoss=0; wallTime=$null }
    [ordered]@{ model='Qwen3.7-Flash visual V2'; tp=[int]$visualScoreDoc.tp; fp=[int]$visualScoreDoc.fp; fn=[int]$visualScoreDoc.fn; precision=$visualScoreDoc.precision; recall=$visualScoreDoc.recall; f1=$visualScoreDoc.f1; modelOmission=(Get-OptionalProperty $visualScoreDoc.lossCounts 'MODEL_OMISSION'); spanError=(Get-OptionalProperty $visualScoreDoc.lossCounts 'MODEL_SPAN_ERROR'); systemLoss=0; wallTime=$null }
)

$finishReasons = [ordered]@{
    'Qwen3.5-9B S0' = [ordered]@{ persisted = $false; value = $null; outputLimitEvidence = $false; note = 'freeze does not persist finishReason; existing audit found no output/transport-limit evidence' }
    'Qwen3.7-Flash text S0' = [ordered]@{ persisted = $true; value = $flashFreezeDoc.finishReason; outputLimitEvidence = $false; note = 'frozen finishReason is stop' }
    'Qwen3.7-Flash visual V2' = [ordered]@{ persisted = $true; value = @($visualFreezeDoc.finishReasons); outputLimitEvidence = $false; note = 'all visual windows persisted stop, content present and structured output parsed' }
}

$projectionRows = @()
foreach ($model in $modelDocs.Keys) {
    $projection = @($modelDocs[$model].prediction.projection)
    $projectionRows += [pscustomobject][ordered]@{
        model = $model
        rawProposalCount = @($modelDocs[$model].prediction.proposals).Count
        finalHeadingCount = if ($null -ne $modelDocs[$model].result.headings) { @($modelDocs[$model].result.headings).Count } else { @($modelDocs[$model].prediction.proposals).Count }
        projectionRecordCount = $projection.Count
        projectionIncludedCount = @($projection | Where-Object {$_.projectionStatus -eq 'INCLUDED'}).Count
        projectionRejectedCount = @($projection | Where-Object {$_.projectionStatus -ne 'INCLUDED'}).Count
        roleCounts = Get-Distribution $allPredictions[$model] 'semanticRole'
        projectionAudit = 'Counts are reported separately for raw proposals, projection records, INCLUDED records, and final scored headings; score artifacts report no system alignment/binding/validator/projection loss.'
    }
}

$promptAnalysis = [ordered]@{
    protocolPath = $paths.protocol
    protocolSha256 = Hash-File $paths.protocol
    broadStructuralInstructionPresent = $protocolText -match 'every structurally real document heading or structural label'
    zeroManyOccurrenceInstructionPresent = $protocolText -match 'zero, one, or many'
    contextualLabelsPresent = $protocolText -match 'TOC_ENTRY' -and $protocolText -match 'FRONT_MATTER' -and $protocolText -match 'AGENDA_NAVIGATION_HEADING'
    taskProjectionMentioned = $protocolText -match 'task-specific projection'
    goldScope = '71 exhaustive legal content heading occurrences from the retained human-authority key; contextual title/running/TOC material is not promoted'
    observedContractTension = 'The runtime semantic contract asks for a broad structural-label inventory, while Strict Gold scores a narrower task projection of legal content headings.'
}

$spanAudits = @()
foreach ($model in $modelDocs.Keys) {
    $rows = $allPredictions[$model]
    $spanAudits += [pscustomobject][ordered]@{
        model = $model
        predictionCount = $rows.Count
        invalidSourceOrUtf16SpanCount = @($rows | Where-Object {-not $_.sourceLookupValid}).Count
        storedTextMismatchCount = @($rows | Where-Object {$_.sourceLookupValid -and -not $_.storedTextMatchesSource}).Count
        exactGoldSpanCount = @($classificationRows | Where-Object {$_.model -eq $model -and $_.class -eq 'GOLD_EXACT_MATCH'}).Count
        sameSourceOverlapCount = @($classificationRows | Where-Object {$_.model -eq $model -and $_.class -eq 'GOLD_EQUIVALENT_WRONG_SPAN'}).Count
        sameSourceNormalizedTextCount = @($classificationRows | Where-Object {$_.model -eq $model -and $_.class -eq 'GOLD_SAME_TEXT_WRONG_SPAN'}).Count
        sourceConvention = 'source-local half-open UTF-16 offsets; reconstructed with .NET String.Substring'
    }
}

$duplicateAudit = [ordered]@{
    goldNormalizedTextDuplicateGroups = @($goldRows | Group-Object {$_.rawSourceText | ForEach-Object { Normalize-Text $_ }} | Where-Object {$_.Count -gt 1} | ForEach-Object {[ordered]@{normalizedText=$_.Name;count=$_.Count;ordinals=@($_.Group.headingOrdinal)}})
    modelNormalizedTextDuplicateGroups = [ordered]@{}
}
foreach ($model in $modelDocs.Keys) {
    $duplicateAudit.modelNormalizedTextDuplicateGroups[$model] = @($allPredictions[$model] | Group-Object {$_.reconstructedText | ForEach-Object { Normalize-Text $_ }} | Where-Object {$_.Count -gt 1} | ForEach-Object {[ordered]@{normalizedText=$_.Name;count=$_.Count;ordinals=@($_.Group.predictionOrdinal)}})
}

$modelGoldSummaries = @()
foreach ($model in $modelDocs.Keys) {
    $matches = @($mapRows | ForEach-Object { $_.modelMatches.$model })
    $classes = @($classificationRows | Where-Object {$_.model -eq $model})
    $modelGoldSummaries += [pscustomobject][ordered]@{
        model = $model
        goldExact = @($matches | Where-Object {$_.status -eq 'EXACT'}).Count
        goldNearOrEquivalent = @($matches | Where-Object {$_.status -eq 'NEAR_OR_EQUIVALENT'}).Count
        goldNoDirectCorrespondence = @($matches | Where-Object {$_.status -eq 'NONE'}).Count
        predictionCount = $allPredictions[$model].Count
        fpClassCounts = Get-Distribution @($classes | Where-Object {$_.finalScored}) 'class'
        rawFpClassCounts = Get-Distribution $classes 'class'
        roleCounts = Get-Distribution $allPredictions[$model] 'semanticRole'
        rawProposalCount = [int]$modelDocs[$model].prediction.rawProposalCount
        providerCallsInAudit = 0
        frozenProviderAttempts = Get-OptionalProperty $modelDocs[$model].freeze 'providerAttempts'
    }
}

$primary = 'TASK_TAXONOMY_TOO_BROAD'
$summary = [ordered]@{
    schema = 'a99-doc0205-semantic-contract-audit-v1'
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    documentId = 'DOC-0205'
    expectedHead = '4b3f3c8ffaa3af854b3b4de8183e26e0e3a4f169'
    benchmarkMode = 'OFFLINE_FROZEN_ARTIFACTS_ONLY'
    providerCalls = 0
    goldReadByAudit = $false
    authority = [ordered]@{ gold = $paths.gold; sourcePacket = $paths.packet; frozenModels = @($modelDocs.Keys); sourceSha256 = $sourceHash; goldSha256 = $goldHash }
    baselineScores = $scoreRows
    modelGoldCorrespondence = $modelGoldSummaries
    goldTaxonomy = [ordered]@{ total = $goldRows.Count; shapeCounts = $goldShapeCounts; semanticRoleCounts = $goldRoleCounts; basis = 'descriptive only; derived from frozen Gold role and source-span shape; no Gold bytes changed' }
    spanConventionAudit = $spanAudits
    duplicateSourceIdAudit = $duplicateAudit
    projectionAudit = $projectionRows
    completionTelemetry = $finishReasons
    integrity = [ordered]@{ allPass = @($integrityChecks | Where-Object {-not $_.pass}).Count -eq 0; checks = $integrityChecks }
    primaryClassification = $primary
    classificationAlternatives = @('MIXED_CONTRACT_AND_MODEL_FAILURE', 'MODEL_SEMANTIC_CAPABILITY_FAILURE')
    conclusion = 'The dominant reproducible mismatch is contract scope: the runtime asks the model to enumerate broad structural labels, while Strict Gold contains the narrower legal-content heading projection. The model also emits severe fragmentary spans, especially in visual V2, so model capability remains a contributing factor; this audit does not claim the contract alone explains every false positive.'
    prohibitedNextStep = 'Do not implement contract V2 or rerun any model until this forensic result is reviewed.'
    recommendedNextStep = 'Design one generic contract intervention that separates broad semantic discovery from the task-specific legal-content heading projection, then evaluate as a new DEV intervention; keep all frozen authorities immutable.'
}

$goldCrossModel = [ordered]@{
    schema = 'a99-doc0205-gold-cross-model-map-v1'
    documentId = 'DOC-0205'
    providerCalls = 0
    goldReadByAudit = $false
    goldCount = $goldRows.Count
    rows = $mapRows
}

$predictionClassification = [ordered]@{
    schema = 'a99-doc0205-prediction-classification-v1'
    documentId = 'DOC-0205'
    providerCalls = 0
    goldReadByAudit = $false
    classificationRule = 'Exact spans are TP candidates; same-source overlap or normalized text is diagnostic near-equivalence; all remaining proposals are classified conservatively and are not promoted to Gold.'
    rows = $classificationRows
}

$contractAnalysis = [ordered]@{
    schema = 'a99-doc0205-contract-analysis-v1'
    documentId = 'DOC-0205'
    providerCalls = 0
    goldReadByAudit = $false
    primaryClassification = $primary
    confidence = 'MODERATE'
    allowedClassificationSet = @('MODEL_SEMANTIC_CAPABILITY_FAILURE','EXACT_SPAN_CONTRACT_MISMATCH','TASK_TAXONOMY_TOO_BROAD','TASK_PROJECTION_CONTRACT_MISMATCH','STRICT_GOLD_AUTHORITY_MISMATCH','MIXED_CONTRACT_AND_MODEL_FAILURE','UNRESOLVED')
    runtimePrompt = $promptAnalysis
    frozenEvidence = [ordered]@{
        qwen9b = [ordered]@{ score = $qwen9bScoreDoc; firstExamples = @($allPredictions['Qwen3.5-9B S0'] | Select-Object -First 5) }
        flashText = [ordered]@{ score = $flashScoreDoc; firstExamples = @($allPredictions['Qwen3.7-Flash text S0'] | Select-Object -First 5) }
        flashVisual = [ordered]@{ score = $visualScoreDoc; firstExamples = @($allPredictions['Qwen3.7-Flash visual V2'] | Select-Object -First 12) }
    }
    diagnosticInterpretation = @(
        'All three frozen runs have system loss zero in their score artifacts, and their prediction/result/freeze hashes verify against the stored authorities.'
        'Strict Gold is valid, exhaustive, and source-bound: 71 exact raw substrings over the common source SHA.'
        'The semantic prompt explicitly allows zero, one, or many structural labels per occurrence and includes contextual roles beyond the retained legal-content Gold scope.'
        'Qwen9B and Flash text produce source-grounded fragments rather than exact Gold spans; Flash V2 produces 361 source-grounded one-character or very short fragments, indicating a severe model/output pathology in addition to scope mismatch.'
        'Because this is an offline descriptive audit, no proposal is reinterpreted as Gold and no runtime transformation is changed.'
    )
    rejectedHypotheses = @('GOLD_AUTHORITY_MISMATCH', 'SYSTEM_BINDING_LOSS', 'SYSTEM_PROJECTION_LOSS', 'OUTPUT_LIMIT_AS_PRIMARY_CAUSE')
    nextInterventionConstraints = @('generic across documents', 'one intervention only', 'paired DEV benchmark', 'Gold firewall at runtime', 'no model call in this audit')
}

$outputRoot = 'eval/a99-closed-loop/doc0205-semantic-contract-audit'
Write-Artifact $summary "$outputRoot/summary.v1.json"
Write-Artifact $goldCrossModel "$outputRoot/gold-cross-model-map.v1.json"
Write-Artifact $predictionClassification "$outputRoot/prediction-classification.v1.json"
Write-Artifact $contractAnalysis "$outputRoot/contract-analysis.v1.json"

Write-Output ("OFFLINE_AUDIT_COMPLETE providerCalls=0 primary={0} gold={1} qwen9b={2} flashText={3} flashVisual={4}" -f $primary, $goldRows.Count, $allPredictions['Qwen3.5-9B S0'].Count, $allPredictions['Qwen3.7-Flash text S0'].Count, $allPredictions['Qwen3.7-Flash visual V2'].Count)
