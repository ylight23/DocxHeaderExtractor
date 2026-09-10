[CmdletBinding()]
param(
    [string]$Root = (Join-Path (Get-Location) 'eval/a99-closed-loop/doc0205-contract-v2-forensic')
)

$ErrorActionPreference = 'Stop'

function Read-Json([string]$Path) {
    return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Hash-File([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Normalize-Text([string]$Value) {
    if ($null -eq $Value) { return '' }
    return (($Value.Normalize([Text.NormalizationForm]::FormC) -replace '[\s\p{P}\p{S}]', '').ToLowerInvariant())
}

function Tokenize([string]$Value) {
    if ($null -eq $Value) { return @() }
    return @([regex]::Matches($Value.ToLowerInvariant(), '[\p{L}\p{N}]+') | ForEach-Object { $_.Value })
}

function Span-Start($Item) { return [int]$Item.headingSpan.start }
function Span-End($Item) { return [int]$Item.headingSpan.end }
function Overlaps($A, $B) {
    return ($A.sourceId -eq $B.sourceId -and (Span-Start $A) -lt (Span-End $B) -and (Span-End $A) -gt (Span-Start $B))
}

New-Item -ItemType Directory -Force -Path $Root | Out-Null
$contractRoot = 'eval/a99-closed-loop/canonical-heading-contract-v2'
$docRoot = Join-Path $contractRoot 'DOC-0205'
$predictionPath = Join-Path $docRoot 'prediction.v2.json'
$resultPath = Join-Path $docRoot 'result.v2.json'
$freezePath = Join-Path $docRoot 'freeze.v2.json'
$executionPath = Join-Path $docRoot 'execution.v2.json'
$packetPath = '.tmp-doc0205-packet.json'
$goldPath = 'eval/a99-closed-loop/strict-gold-occurrence-v1/DOC-0205.occurrence-gold-v1.json'
$visualRoot = 'eval/a99-closed-loop/qwen37-flash-visual-ceiling/DOC-0205/visual-v2'

# Gold firewall: load only frozen artifacts and blind source packet first.
$prediction = Read-Json $predictionPath
$result = Read-Json $resultPath
$freeze = Read-Json $freezePath
$execution = Read-Json $executionPath
$packet = Read-Json $packetPath

$integrityChecks = [ordered]@{
    sourceSha256 = ($freeze.sourceSha256 -eq $prediction.sourceSha256 -and $prediction.sourceSha256 -eq $packet.sourceDocumentSha256)
    predictionSha256 = ($freeze.predictionSha256 -eq (Hash-File $predictionPath))
    resultSha256 = ($freeze.resultSha256 -eq (Hash-File $resultPath))
    goldReadBeforeFreezePrediction = ($prediction.goldReadBeforeFreeze -eq $false)
    goldReadBeforeFreezeResult = ($result.goldReadBeforeFreeze -eq $false)
    goldReadBeforeFreezeFreeze = ($freeze.goldReadBeforeFreeze -eq $false)
    goldReadBeforeFreezeExecution = ($execution.goldReadBeforeFreeze -eq $false)
    outputComplete = ($freeze.finishReason -eq 'stop' -and $freeze.providerAttempts -eq 1 -and $execution.telemetry[0].responseContentPresent -eq $true -and $execution.telemetry[0].structuredOutputParsed -eq $true)
}
if (@($integrityChecks.GetEnumerator() | Where-Object { -not $_.Value }).Count -gt 0) {
    throw ('CONTRACT_V2_INTEGRITY_FAIL ' + (($integrityChecks.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object { $_.Key }) -join ','))
}

# Only this point may read canonical Strict Gold.
$gold = Read-Json $goldPath
$sourceById = @{}
foreach ($occurrence in $packet.occurrences) { $sourceById[$occurrence.sourceId] = $occurrence }
$proposals = @($prediction.proposals)
$headings = @($prediction.headings)
$goldRows = @($gold.bindings)

$bindingRows = @()
$predictionRows = @()
$resultKeys = @($result.headings | ForEach-Object { '{0}:{1}:{2}' -f $_.sources[0].sourceId, $_.sources[0].span.start, $_.sources[0].span.end })
foreach ($proposal in $proposals) {
    $source = $sourceById[$proposal.sourceId]
    $start = [int]$proposal.headingSpan.start
    $end = [int]$proposal.headingSpan.end
    $validSource = $null -ne $source
    $validSpan = $validSource -and $start -ge [int]$source.fullSpan.start -and $end -le [int]$source.fullSpan.end -and $start -lt $end
    $reconstructed = if ($validSpan) { $source.rawText.Substring($start - [int]$source.fullSpan.start, $end - $start) } else { $null }
    $key = '{0}:{1}:{2}' -f $proposal.sourceId, $start, $end
    $bindingRows += [ordered]@{
        sourceId = $proposal.sourceId
        localSpan = [ordered]@{ start = $start; end = $end }
        visibleSpan = [ordered]@{ start = $start; end = $end }
        ownedSpan = [ordered]@{ start = $start; end = $end }
        recomputedGlobalKey = $key
        modelText = $proposal.text
        reconstructedSourceText = $reconstructed
        sourcePresent = $validSource
        spanWithinOwnedOccurrence = $validSpan
        bindingEqualToFrozenResult = ($resultKeys -contains $key)
    }

    $related = @($goldRows | Where-Object { Overlaps $proposal $_ })
    $sameTextOtherSource = @($goldRows | Where-Object { $_.sourceId -ne $proposal.sourceId -and (Normalize-Text $_.rawSourceText) -eq (Normalize-Text $proposal.text) })
    $category = 'UNRESOLVED'
    $basis = 'No deterministic Gold relationship; prediction remains diagnostic-only.'
    if (-not $validSource -or -not $validSpan) {
        $category = 'REFERENCE_OR_BINDING_SUSPECT'; $basis = 'Source occurrence or half-open span is not reconstructible from the blind packet.'
    } elseif ($sameTextOtherSource.Count -gt 0) {
        $category = 'CORRECT_TEXT_WRONG_SOURCE'; $basis = 'Normalized text matches a Gold heading only under another source occurrence.'
    } elseif ($related.Count -gt 0) {
        $same = @($related | Where-Object { (Normalize-Text $_.rawSourceText) -eq (Normalize-Text $proposal.text) -and (Span-Start $_) -eq $start -and (Span-End $_) -eq $end })
        if ($same.Count -gt 0) {
            $category = 'UNRESOLVED'; $basis = 'Exact occurrence match exists but frozen scorer did not count it; requires scorer-contract review.'
        } elseif (@($related | Where-Object { (Normalize-Text $proposal.text).Contains((Normalize-Text $_.rawSourceText)) }).Count -gt 0) {
            $category = 'SUPERSET_GOLD_HEADING'; $basis = 'Prediction text contains a Gold heading while its span extends beyond it.'
        } elseif (@($related | Where-Object { (Normalize-Text $_.rawSourceText).Contains((Normalize-Text $proposal.text)) }).Count -gt 0) {
            $category = 'PARTIAL_GOLD_HEADING'; $basis = 'Prediction text is a strict substring of an overlapping Gold heading.'
        } else {
            $category = 'CORRECT_STRUCTURE_WRONG_SPAN'; $basis = 'Prediction overlaps a Gold heading but has a different boundary/text.'
        }
    } elseif ($proposal.semanticRole -in @('TOC_ENTRY','AGENDA_NAVIGATION_HEADING','LOCAL_INDEX_TITLE','RUNNING_HEADER','FRONT_MATTER','CAPTION','LIST_ITEM','TABLE_LABEL','DECORATIVE_TEXT','BODY_FRAGMENT','OTHER_NON_TASK_STRUCTURAL','OTHER_STRUCTURAL_LABEL')) {
        $category = 'NON_TASK_STRUCTURAL_ELEMENT'; $basis = 'Raw semantic role is explicitly outside the task projection.'
    } elseif ($proposal.text.Length -ge 40 -and $proposal.text -match '[.;:!?]') {
        $category = 'PROSE_FALSE_POSITIVE'; $basis = 'Long punctuation-bearing fragment inside the flattened paragraph; generic diagnostic heuristic only.'
    } else {
        $category = 'UNRESOLVED'; $basis = 'No deterministic Gold relationship; cannot assert true extra structure from offline evidence alone.'
    }
    $predictionRows += [ordered]@{
        sourceId = $proposal.sourceId; span = [ordered]@{start=$start;end=$end}; text=$proposal.text; semanticRole=$proposal.semanticRole
        diagnosticCategory=$category; classificationBasis=$basis; overlappingGoldOrdinals=@($related | ForEach-Object { $_.headingOrdinal })
    }
}

$goldAuditRows = @()
foreach ($g in $goldRows) {
    $source = $sourceById[$g.sourceId]
    $exact = @($proposals | Where-Object { $_.sourceId -eq $g.sourceId -and [int]$_.headingSpan.start -eq [int]$g.headingSpan.start -and [int]$_.headingSpan.end -eq [int]$g.headingSpan.end -and (Normalize-Text $_.text) -eq (Normalize-Text $g.rawSourceText) })
    $overlap = @($proposals | Where-Object { Overlaps $_ $g })
    $sameText = @($proposals | Where-Object { (Normalize-Text $_.text) -eq (Normalize-Text $g.rawSourceText) })
    $near = @()
    $goldTokens = @(Tokenize $g.rawSourceText)
    foreach ($candidate in $proposals) {
        $candidateTokens = @(Tokenize $candidate.text)
        if ($goldTokens.Count -gt 0 -and $candidateTokens.Count -gt 0) {
            $intersection = @($goldTokens | Where-Object { $candidateTokens -contains $_ } | Select-Object -Unique).Count
            $union = @($goldTokens + $candidateTokens | Select-Object -Unique).Count
            if ($union -gt 0 -and ($intersection / $union) -ge 0.5) { $near += [ordered]@{sourceId=$candidate.sourceId;span=$candidate.headingSpan;similarity=[math]::Round($intersection/$union,4)} }
        }
    }
    if ($exact.Count -gt 0) { $category='SOURCE_PRESENT'; $basis='Exact frozen occurrence proposal.' }
    elseif ($overlap.Count -gt 0) { $category='MODEL_SPAN_ERROR'; $basis='A proposal occupies the Gold source occurrence but not the exact approved boundary.' }
    elseif ($sameText.Count -gt 0) { $category='MODEL_SPAN_ERROR'; $basis='Approved text appears at a different occurrence span.' }
    elseif ($null -eq $source) { $category='REFERENCE_CONTRACT_MISMATCH'; $basis='Gold source occurrence is absent from the blind packet.' }
    elseif (@($near).Count -gt 0) { $category='TRUE_MODEL_OMISSION'; $basis='Semantic-near candidates exist, but no exact/overlap binding; omission remains model-side diagnostic.' }
    else { $category='TRUE_MODEL_OMISSION'; $basis='Source occurrence is present with no exact, overlap, or semantic-near proposal.' }
    $goldAuditRows += [ordered]@{
        headingOrdinal=$g.headingOrdinal; sourceId=$g.sourceId; span=$g.headingSpan; rawSourceText=$g.rawSourceText; semanticRole=$g.semanticRole
        sourcePresent=($null -ne $source); exactProposalCount=$exact.Count; overlapProposalCount=$overlap.Count; sameTextProposalCount=$sameText.Count
        semanticNearProposals=$near; diagnosticCategory=$category; classificationBasis=$basis
    }
}

$docSource = $sourceById['body[1]/p[4]']
$styleStrong = @($packet.occurrences | Where-Object { $_.style.styleId -ne 'Normal' -or $_.style.bold -or $_.style.italic -or $null -ne $_.style.alignment }).Count
$numberingStrong = @($packet.occurrences | Where-Object { $null -ne $_.numbering.numberingId -or $null -ne $_.numbering.numberLabel }).Count
$layoutStrong = @($packet.occurrences | Where-Object { $_.layout.keepNext -or $_.layout.pageBreakBefore -or $_.layout.tableDepth -gt 0 }).Count
$targetStyleStrong = [int]($docSource.style.styleId -ne 'Normal' -or $docSource.style.bold -or $docSource.style.italic -or $null -ne $docSource.style.alignment)
$targetNumberingStrong = [int]($null -ne $docSource.numbering.numberingId -or $null -ne $docSource.numbering.numberLabel)
$targetLayoutStrong = [int]($docSource.layout.keepNext -or $docSource.layout.pageBreakBefore -or $docSource.layout.tableDepth -gt 0)
$visualManifestPath = Join-Path $visualRoot 'page-manifest.v2.json'
$visualManifest = if (Test-Path $visualManifestPath) { Read-Json $visualManifestPath } else { $null }
$visualAvailable = $null -ne $visualManifest -and $visualManifest.sourceDocxSha256 -eq $prediction.sourceSha256 -and $visualManifest.deterministic -eq $true -and $visualManifest.coverage -eq 1
$visualRows = @()
foreach ($g in $goldRows) {
    $signals = @()
    if ($g.rawSourceText -match '^(Chương|Phần|Mục|Điều|Phụ lục|Chương\s|Điều\s)') { $signals += 'TEXT_SEMANTICS_STRONG' }
    if ($targetStyleStrong -gt 0 -or $targetNumberingStrong -gt 0 -or $targetLayoutStrong -gt 0) { $signals += 'XML_STRUCTURE_STRONG' }
    if ($visualAvailable -and $targetStyleStrong -eq 0 -and $targetNumberingStrong -eq 0 -and $targetLayoutStrong -eq 0) { $signals += 'VISUAL_LAYOUT_POTENTIALLY_DECISIVE' }
    if ($signals.Count -eq 0) { $signals += 'NO_CLEAR_SIGNAL' }
    $visualRows += [ordered]@{ headingOrdinal=$g.headingOrdinal; signals=$signals; observedFacts='Gold text is in one flattened Normal paragraph; no per-heading visual claim is made.' }
}

function Get-Counts($Rows, [string]$Property) {
    $out = [ordered]@{}
    foreach ($row in $Rows) { $key = [string]$row[$Property]; if (-not $out.Contains($key)) { $out[$key] = 0 }; $out[$key]++ }
    return $out
}
$predictionCounts = Get-Counts $predictionRows 'diagnosticCategory'
$goldCounts = Get-Counts $goldAuditRows 'diagnosticCategory'
$visualCounts = [ordered]@{}
foreach ($row in $visualRows) { foreach ($signal in $row.signals) { if (-not $visualCounts.Contains($signal)) { $visualCounts[$signal]=0 }; $visualCounts[$signal]++ } }
$bindingMismatch = @($bindingRows | Where-Object { -not $_.bindingEqualToFrozenResult }).Count
$bindingSound = $bindingMismatch -eq 0 -and @($bindingRows | Where-Object { -not $_.spanWithinOwnedOccurrence }).Count -eq 0
$exactMismatch = @($goldAuditRows | Where-Object { $_.exactProposalCount -eq 0 }).Count
$weakPacket = $targetStyleStrong -eq 0 -and $targetNumberingStrong -eq 0 -and $targetLayoutStrong -eq 0 -and $docSource.rawText.Length -gt 50000
$largeMismatch = $exactMismatch -ge 50
$decision = if ($bindingSound -and $largeMismatch -and $weakPacket -and $visualAvailable) { 'VLM_VISUAL_EVIDENCE_JUSTIFIED' } else { 'VLM_NOT_YET_JUSTIFIED_CONTRACT_OR_BINDING_ISSUE' }
$visualScorePath = Join-Path $visualRoot 'score.v2.json'
$visualFreezePath = Join-Path $visualRoot 'freeze.v2.json'
$visualOutcome = if (Test-Path $visualScorePath) {
    $visualScore = Read-Json $visualScorePath
    $visualFreeze = Read-Json $visualFreezePath
    [ordered]@{ status='COMPLETED'; tp=$visualScore.tp; fp=$visualScore.fp; fn=$visualScore.fn; f1=$visualScore.f1; systemLossCount=$visualScore.systemLossCount; providerAttempts=$visualFreeze.providerAttempts; finishReasons=$visualFreeze.finishReasons; goldReadBeforeFreeze=$visualFreeze.goldReadBeforeFreeze }
} else { [ordered]@{ status='NOT_RUN' } }

$audit = [ordered]@{
    schemaVersion='a99-doc0205-contract-v2-forensic-audit-v1'; documentId='DOC-0205'; offlineOnly=$true; providerCalls=0
    startHead=$freeze.gitSha; contractVersion=$prediction.semanticContractVersion; model=$prediction.model
    frozenIntegrity=[ordered]@{ checks=$integrityChecks; predictionPath=$predictionPath; resultPath=$resultPath; freezePath=$freezePath }
    bindingAudit=[ordered]@{ proposalCount=$proposals.Count; resultHeadingCount=$headings.Count; recomputedCount=$bindingRows.Count; mismatchCount=$bindingMismatch; allEqual=$bindingSound; rows=$bindingRows }
    objectiveSourceFacts=[ordered]@{
        sourcePacketPath=$packetPath; sourceDocumentSha256=$packet.sourceDocumentSha256; occurrenceCount=$packet.occurrences.Count
        targetOccurrence=[ordered]@{sourceId=$docSource.sourceId; rawTextCharacters=$docSource.rawText.Length; fullSpan=$docSource.fullSpan; style=$docSource.style; numbering=$docSource.numbering; layout=$docSource.layout; newlineCount=([regex]::Matches($docSource.rawText, "`r?`n")).Count; delimiterCount=([regex]::Matches($docSource.rawText, '\|')).Count}
        nonDefaultStyleOccurrenceCount=$styleStrong; numberedOccurrenceCount=$numberingStrong; layoutSignalOccurrenceCount=$layoutStrong
        targetOccurrenceStyleSignal=$targetStyleStrong; targetOccurrenceNumberingSignal=$targetNumberingStrong; targetOccurrenceLayoutSignal=$targetLayoutStrong
    }
    predictionClassification=[ordered]@{ total=$predictionRows.Count; counts=$predictionCounts; rows=$predictionRows }
    goldClassification=[ordered]@{ total=$goldAuditRows.Count; counts=$goldCounts; rows=$goldAuditRows }
    visualNecessity=[ordered]@{ rendererArtifactPath=$visualRoot; faithfulRendererArtifactAvailable=$visualAvailable; pageCount=if($null -ne $visualManifest){$visualManifest.pageCount}else{0}; counts=$visualCounts; rows=$visualRows }
    vlmOutcome=$visualOutcome
    doc0258Comparison=[ordered]@{
        contractVersion=(Read-Json (Join-Path $contractRoot 'DOC-0258/prediction.v2.json')).semanticContractVersion
        doc0205=[ordered]@{sourceCharacters=$prediction.sourceCharacters; packetCharacters=$prediction.packetCharacters; ownedOccurrences=$execution.telemetry[0].ownedOccurrences; visibleOccurrences=$execution.telemetry[0].visibleOccurrences; outputCount=$headings.Count}
        doc0258=(Read-Json (Join-Path $contractRoot 'DOC-0258/execution.v2.json')).metric
        genericInterpretation='DOC-0205 is a flattened legal instrument with one very large Normal occurrence; DOC-0258 has many smaller owned occurrences. This is a structural workload comparison, not a Gold-derived runtime rule.'
    }
    decision=[ordered]@{ classification=$decision; bindingSound=$bindingSound; exactGoldMismatchCount=$exactMismatch; weakTextXmlPacket=$weakPacket; rationale='A is selected only when frozen binding is sound, exact mismatch is large, and deterministic render evidence exists while packet-level XML structure is weak.' }
    goldFirewall=[ordered]@{ goldReadBeforeFreeze=$false; providerCallsDuringAudit=0; goldPath=$goldPath }
}

$auditPath = Join-Path $Root 'audit.v1.json'
$map = [ordered]@{ schemaVersion='a99-doc0205-contract-v2-prediction-gold-map-v1'; documentId='DOC-0205'; offlineOnly=$true; predictionCount=$predictionRows.Count; goldCount=$goldAuditRows.Count; predictions=$predictionRows; gold=$goldAuditRows; bindingEquality=$bindingSound }
$summary = [ordered]@{ schemaVersion='a99-doc0205-contract-v2-forensic-summary-v1'; documentId='DOC-0205'; startHead=$freeze.gitSha; model=$prediction.model; exactScore=[ordered]@{tp=0;fp=$predictionRows.Count;fn=$goldRows.Count;precision=0;recall=0;f1=0}; frozenContractV2Metric=(Read-Json (Join-Path $docRoot 'score.v2.json')); predictionClassificationCounts=$predictionCounts; goldClassificationCounts=$goldCounts; visualSignalCounts=$visualCounts; bindingSound=$bindingSound; decision=$decision; vlmOutcome=$visualOutcome; modelCalls=0; goldReadBeforeFreeze=$false }
$audit | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $auditPath -Encoding UTF8
$map | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $Root 'prediction-gold-map.v1.json') -Encoding UTF8
$summary | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $Root 'summary.v1.json') -Encoding UTF8
Write-Output ('FORENSIC_DECISION=' + $decision)
Write-Output ('BINDING_SOUND=' + $bindingSound + ' GOLD_EXACT_MISMATCH=' + $exactMismatch + ' WEAK_PACKET=' + $weakPacket + ' VISUAL_RENDER=' + $visualAvailable)
Write-Output ('PREDICTION_COUNTS=' + (($predictionCounts.GetEnumerator() | ForEach-Object { "$($_.Key):$($_.Value)" }) -join ';'))
Write-Output ('GOLD_COUNTS=' + (($goldCounts.GetEnumerator() | ForEach-Object { "$($_.Key):$($_.Value)" }) -join ';'))
Write-Output ('VISUAL_COUNTS=' + (($visualCounts.GetEnumerator() | ForEach-Object { "$($_.Key):$($_.Value)" }) -join ';'))
Write-Output ('ARTIFACT_ROOT=' + $Root)
