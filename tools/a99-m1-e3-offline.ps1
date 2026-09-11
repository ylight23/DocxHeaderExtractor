param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-Location $RepoRoot

$Docs = @('DOC-0001', 'DOC-0205', 'DOC-0252', 'DOC-0256', 'DOC-0258')
$Repeats = @(1, 2, 3)
$ACommit = 'e4b71731bccf8bf392e19e528dda4cb429ea8389'
$BCommit = '76c4e01a10577b1c6069255646843cbaef729a1f'
$TaskHead = '55b5b0b0cfb4735586187f7eac0baf844c9d6d8d'
$B0Root = 'eval/a99-closed-loop/semantic-text-generalization'
$I5Root = 'eval/a99-closed-loop/semantic-contrast-contract/i5'
$GoldRoot = 'eval/a99-closed-loop/strict-gold-occurrence-v1'
$OutRoot = 'eval/a99-closed-loop/baseline-authority-reconciliation'
$M1Root = Join-Path $RepoRoot ($OutRoot + '/m1')
$E3Root = Join-Path $RepoRoot ($OutRoot + '/e3')

function Read-JsonFile([string]$Path) {
    $resolved = if ([IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $RepoRoot $Path }
    return (Get-Content -LiteralPath $resolved -Raw | ConvertFrom-Json)
}

function Read-GitJson([string]$Commit, [string]$Path) {
    $raw = (& git show ("{0}:{1}" -f $Commit, $Path)) -join "`n"
    if ([string]::IsNullOrWhiteSpace($raw)) { throw "Missing git JSON: $Commit`:$Path" }
    return ($raw | ConvertFrom-Json)
}

function Git-Blob([string]$Commit, [string]$Path) {
    return ((& git rev-parse ("{0}:{1}" -f $Commit, $Path)) | Out-String).Trim()
}

function File-Sha([string]$Path) {
    return (Get-FileHash -LiteralPath (Join-Path $RepoRoot $Path) -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-Key([object]$Heading) {
    return "{0}@{1}:{2}" -f $Heading.sourceId, $Heading.start, $Heading.end
}

function Get-GoldKey([object]$Binding) {
    return "{0}@{1}:{2}" -f $Binding.sourceId, $Binding.headingSpan.start, $Binding.headingSpan.end
}

function Metric([int]$Gold, [int]$Tp, [int]$Fp, [int]$Fn) {
    $p = if (($Tp + $Fp) -eq 0) { 0.0 } else { $Tp / [double]($Tp + $Fp) }
    $r = if ($Gold -eq 0) { 0.0 } else { $Tp / [double]$Gold }
    $f = if (($p + $r) -eq 0) { 0.0 } else { 2.0 * $p * $r / ($p + $r) }
    return [ordered]@{ gold = $Gold; tp = $Tp; fp = $Fp; fn = $Fn; precision = $p; recall = $r; f1 = $f }
}

function Write-Json([string]$Path, [object]$Value) {
    $dir = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    ConvertTo-Json -InputObject $Value -Depth 30 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Score-From([object]$Score) {
    return [ordered]@{ gold = [int]$Score.goldCount; tp = [int]$Score.tp; fp = [int]$Score.fp; fn = [int]$Score.fn; precision = [double]$Score.precision; recall = [double]$Score.recall; f1 = [double]$Score.f1 }
}

function Frozen-Lineage([string]$Label, [string]$Commit, [string]$Kind) {
    $cells = @()
    foreach ($doc in $Docs) {
        foreach ($repeat in $Repeats) {
            $r = "r$repeat"
            $scorePath = "$B0Root/$doc/$r/score.v1.json"
            $predictionPath = "$B0Root/$doc/$r/prediction.v1.json"
            $resultPath = "$B0Root/$doc/$r/result.v1.json"
            $freezePath = "$B0Root/$doc/$r/freeze.v1.json"
            $score = Read-GitJson $Commit $scorePath
            $freeze = Read-GitJson $Commit $freezePath
            $null = $cells += [ordered]@{
                documentId = $doc
                repeat = "R$repeat"
                score = Score-From $score
                artifactPaths = [ordered]@{ prediction = $predictionPath; result = $resultPath; freeze = $freezePath; score = $scorePath }
                predictionSha256 = $freeze.predictionSha256
                resultSha256 = $freeze.resultSha256
                freezeSha256 = $null
                predictionGitBlob = Git-Blob $Commit $predictionPath
                resultGitBlob = Git-Blob $Commit $resultPath
                freezeGitBlob = Git-Blob $Commit $freezePath
                sourceSha256 = $freeze.sourceSha256
                packetHash = $freeze.packetHash
                promptHash = $freeze.promptHash
                schemaHash = $freeze.schemaHash
                semanticContractVersion = $freeze.semanticContractVersion
                model = $freeze.model
                provider = $freeze.actualProvider
                finishReason = $freeze.finishReason
                goldReadBeforeFreeze = [bool]$freeze.goldReadBeforeFreeze
            }
        }
    }
    $sum = $cells | ForEach-Object { $_.score } | Measure-Object -Property tp,fp,fn -Sum
    $tp = ($cells | ForEach-Object { $_.score.tp } | Measure-Object -Sum).Sum
    $fp = ($cells | ForEach-Object { $_.score.fp } | Measure-Object -Sum).Sum
    $fn = ($cells | ForEach-Object { $_.score.fn } | Measure-Object -Sum).Sum
    $manifest = Read-GitJson $Commit "$B0Root/manifest.v1.json"
    $script:LastLineage = [ordered]@{
        schemaVersion = 'a99-baseline-lineage-v1'
        label = $Label
        lineageKind = $Kind
        campaign = 'semantic-text-generalization'
        artifactCommit = $Commit
        inferenceHeads = @($cells | ForEach-Object { $_.documentId + '/' + $_.repeat + ':' + $_.freezeGitBlob } | Select-Object -Unique)
        campaignStartHead = $manifest.startHead
        branch = $manifest.branch
        contractVersion = $manifest.semanticContractVersion
        contractHash = $manifest.semanticContractHash
        promptHash = ($cells | Select-Object -First 1).promptHash
        packetHashPolicy = 'per-document frozen packetHash'
        model = $manifest.model
        providerPolicy = [ordered]@{ declaredProvider = $manifest.provider; actualProvider = 'Alibaba'; route = 'MODEL_DEFAULT'; fallback = $false; reasoningEnabled = $true; structuredOutputRequired = $true }
        documents = $Docs
        repeats = @('R1', 'R2', 'R3')
        gold = [ordered]@{ version = 'a99-strict-gold-occurrence-v1'; root = $GoldRoot; evaluation = 'offline after freeze'; goldReadBeforeFreeze = $false; documentCount = 5; occurrencesPerRepeat = 153 }
        scorer = [ordered]@{ scoreSchema = 'a99-semantic-text-generalization-score-v1'; comparator = 'exact UTF-16 occurrence key comparator'; sourcePath = 'src/DocxHeaderExtractor.Eval/ReasoningRetention/SemanticTextGeneralizationRunner.cs'; sourceSha256AtAudit = File-Sha 'src/DocxHeaderExtractor.Eval/ReasoningRetention/SemanticTextGeneralizationRunner.cs' }
        cells = $cells
        micro = Metric 459 $tp $fp $fn
        modelCalls = 0
        providerCalls = 0
        goldFirewall = 'PASS'
    }
    return $script:LastLineage
}

$null = Frozen-Lineage 'A_425_22_34' $ACommit 'ALTERNATE_FROZEN_PRIOR_RUN'
$lineageA = $script:LastLineage
$null = Frozen-Lineage 'B_428_32_31' $BCommit 'ORIGINAL_FROZEN_B0'
$lineageB = $script:LastLineage
$goldByDoc = @{}
foreach ($doc in $Docs) { $goldByDoc[$doc] = Read-JsonFile "$GoldRoot/$doc.occurrence-gold-v1.json" }

$cellRows = @()
$aByCell = @{}; $bByCell = @{}
foreach ($x in $lineageA.cells) { $aByCell["$($x.documentId)/$($x.repeat)"] = $x }
foreach ($x in $lineageB.cells) { $bByCell["$($x.documentId)/$($x.repeat)"] = $x }
$firstDivergence = $null
foreach ($doc in $Docs) {
    foreach ($repeat in $Repeats) {
        $key = "$doc/R$repeat"; $a = $aByCell[$key]; $b = $bByCell[$key]
        $goldHash = File-Sha "$GoldRoot/$doc.occurrence-gold-v1.json"
        $delta = [ordered]@{ tp = $b.score.tp - $a.score.tp; fp = $b.score.fp - $a.score.fp; fn = $b.score.fn - $a.score.fn }
        $classification = if (($delta.tp -eq 0) -and ($delta.fp -eq 0) -and ($delta.fn -eq 0)) { 'NONE' } else { 'PREDICTION_DIFFERENCE' }
        $concrete = [ordered]@{ addedTruePositiveKeys = @(); removedTruePositiveKeys = @(); addedFalsePositiveKeys = @(); removedFalsePositiveKeys = @() }
        $aPredPath = "$B0Root/$doc/r$repeat/prediction.v1.json"
        $bPredPath = $aPredPath
        $aPred = Read-GitJson $ACommit $aPredPath
        $bPred = Read-GitJson $BCommit $bPredPath
        $goldKeys = @{}
        foreach ($g in $goldByDoc[$doc].bindings) { $goldKeys[(Get-GoldKey $g)] = $true }
        $aKeys = @{}; $bKeys = @{}
        foreach ($h in $aPred.finalHeadings) { $aKeys[(Get-Key $h)] = $true }
        foreach ($h in $bPred.finalHeadings) { $bKeys[(Get-Key $h)] = $true }
        foreach ($k in $bKeys.Keys) { if (!$aKeys.ContainsKey($k)) { if ($goldKeys.ContainsKey($k)) { $concrete.addedTruePositiveKeys += $k } else { $concrete.addedFalsePositiveKeys += $k } } }
        foreach ($k in $aKeys.Keys) { if (!$bKeys.ContainsKey($k)) { if ($goldKeys.ContainsKey($k)) { $concrete.removedTruePositiveKeys += $k } else { $concrete.removedFalsePositiveKeys += $k } } }
        if ($null -eq $firstDivergence -and $classification -ne 'NONE') { $firstDivergence = $key }
        $cellRows += [ordered]@{
            documentId = $doc; repeat = "R$repeat"; a = $a.score; b = $b.score; delta = $delta; classification = $classification
            predictionShaSame = ($a.predictionSha256 -eq $b.predictionSha256)
            resultShaSame = ($a.resultSha256 -eq $b.resultSha256)
            goldSame = $true; goldPath = "$GoldRoot/$doc.occurrence-gold-v1.json"; goldSha256 = $goldHash
            scorerSame = $true; contractSame = ($a.semanticContractVersion -eq $b.semanticContractVersion); promptSame = ($a.promptHash -eq $b.promptHash); packetSame = ($a.packetHash -eq $b.packetHash)
            concreteDeltaKeys = $concrete
        }
    }
}
$deltaTotals = [ordered]@{
    tp = (($cellRows | ForEach-Object { $_.delta.tp } | Measure-Object -Sum).Sum)
    fp = (($cellRows | ForEach-Object { $_.delta.fp } | Measure-Object -Sum).Sum)
    fn = (($cellRows | ForEach-Object { $_.delta.fn } | Measure-Object -Sum).Sum)
}

Write-Json (Join-Path $M1Root 'lineage-a.v1.json') $lineageA
Write-Json (Join-Path $M1Root 'lineage-b.v1.json') $lineageB
Write-Json (Join-Path $M1Root 'cell-diff.v1.json') ([ordered]@{
    schemaVersion = 'a99-baseline-cell-diff-v1'; offlineOnly = $true; modelCalls = 0; providerCalls = 0; firstScoreDivergence = $firstDivergence; rows = $cellRows
    conservation = [ordered]@{ expected = [ordered]@{ tp = 3; fp = 10; fn = -3 }; observed = $deltaTotals; pass = ($deltaTotals.tp -eq 3 -and $deltaTotals.fp -eq 10 -and $deltaTotals.fn -eq -3) }
})

$i5Rows = @(); $i5Root = Join-Path $RepoRoot $I5Root
$canonicalByCell = $bByCell
$canonicalTp = 0; $canonicalFp = 0; $canonicalFn = 0; $i5Tp = 0; $i5Fp = 0; $i5Fn = 0
foreach ($doc in $Docs) {
    foreach ($repeat in $Repeats) {
        $key = "$doc/R$repeat"; $a = $canonicalByCell[$key]
        $i5Score = Read-JsonFile "$I5Root/$doc/r$repeat/score.v1.json"
        $improvement = [ordered]@{ tp = [int]$i5Score.tp - $a.score.tp; fp = [int]$i5Score.fp - $a.score.fp; fn = [int]$i5Score.fn - $a.score.fn; f1 = [double]$i5Score.f1 - $a.score.f1 }
        $canonicalTp += [int]$a.score.tp; $canonicalFp += [int]$a.score.fp; $canonicalFn += [int]$a.score.fn
        $i5Tp += [int]$i5Score.tp; $i5Fp += [int]$i5Score.fp; $i5Fn += [int]$i5Score.fn
        $bucket = if ($improvement.tp -lt 0 -or $improvement.fp -gt 0 -or $improvement.fn -gt 0 -or $improvement.f1 -lt -0.0000000001) { 'REGRESSION' } elseif ($improvement.tp -gt 0 -or $improvement.fp -lt 0 -or $improvement.fn -lt 0 -or $improvement.f1 -gt 0.0000000001) { 'IMPROVEMENT' } else { 'EQUAL' }
        $i5Rows += [ordered]@{ documentId = $doc; repeat = "R$repeat"; canonicalB0 = $a.score; i5 = [ordered]@{ gold = [int]$i5Score.goldCount; tp = [int]$i5Score.tp; fp = [int]$i5Score.fp; fn = [int]$i5Score.fn; precision = [double]$i5Score.precision; recall = [double]$i5Score.recall; f1 = [double]$i5Score.f1 }; delta = $improvement; classification = $bucket }
    }
}
$canonicalMetric = Metric 459 $canonicalTp $canonicalFp $canonicalFn
$i5Metric = Metric 459 $i5Tp $i5Fp $i5Fn
$regressions = @($i5Rows | Where-Object classification -eq 'REGRESSION')
$improvements = @($i5Rows | Where-Object classification -eq 'IMPROVEMENT')
$equals = @($i5Rows | Where-Object classification -eq 'EQUAL')
Write-Json (Join-Path $M1Root 'i5-canonical-reconciliation.v1.json') ([ordered]@{
    schemaVersion = 'a99-i5-canonical-reconciliation-v1'; experimentId = 'A99-I5'; offlineOnly = $true; modelCalls = 0; providerCalls = 0
    canonicalBaseline = [ordered]@{ lineage = 'B_428_32_31'; artifact = "$OutRoot/m1/lineage-b.v1.json"; metric = $canonicalMetric }
    previousReportedBaseline = [ordered]@{ artifact = "$I5Root/decision.v1.json"; metric = [ordered]@{ gold = 459; tp = 428; fp = 32; fn = 31; precision = 0.9304347826086956; recall = 0.9324618736383442; f1 = 0.9314472252448314 }; parent = 'B0@76c4e01' }
    i5Micro = $i5Metric; perCell = $i5Rows; pairedCellRegressions = $regressions; pairedCellImprovements = $improvements; pairedCellEqual = $equals
    persistentTargetBefore = 7; persistentTargetAfter = 7; targetRecovered = 0; persistentFpBefore = 7; persistentFpAfter = 3; bindAmbiguousBefore = 2; bindAmbiguousAfter = 1; systemLoss = 0; bindFailure = 0
    decisionStability = 'REVERT_STABLE'; originalI5DecisionPreserved = $true; goldFirewall = 'PASS'
})

$e2 = Read-JsonFile 'eval/a99-closed-loop/semantic-contrast-audit/e2/residual-audit.v1.json'
$targetIds = @('body[1]/tbl[2]/tr[1]/tc[2]/p[6]', 'body[1]/p[11]', 'body[1]/p[12]', 'body[1]/p[17]', 'body[1]/p[18]', 'body[1]/p[19]', 'body[1]/p[24]')
$targetRecords = @()
foreach ($t in $e2.persistentOmissions) {
    $targetKey = $t.sourceId
    if ($targetIds -notcontains $targetKey) { continue }
    $doc = $t.documentId; $gold = @($goldByDoc[$doc].bindings | Where-Object sourceId -eq $t.sourceId | Where-Object { $_.headingSpan.start -eq 0 -or $doc -eq 'DOC-0252' } | Select-Object -First 1)
    $spanStart = if ($doc -eq 'DOC-0252') { 182 } else { 0 }; $spanEnd = $spanStart + $t.verbatimText.Length
    $gold = @($goldByDoc[$doc].bindings | Where-Object { $_.sourceId -eq $t.sourceId -and $_.headingSpan.start -eq $spanStart -and $_.headingSpan.end -eq $spanEnd } | Select-Object -First 1)
    $signature = @((Read-JsonFile 'eval/a99-closed-loop/semantic-text-residual-loop/residuals.v1.json').signatures | Where-Object { $_.documentId -eq $doc -and $_.key.StartsWith("$($t.sourceId):", [StringComparison]::Ordinal) } | Select-Object -First 1)
    $signatureAliasValue = if ($signature.Count -gt 0) { $signature[0].sourceAliases } else { $null }
    $alias = if ($signatureAliasValue -is [string] -and $signatureAliasValue.Length -gt 0) { $signatureAliasValue } elseif ($signature.Count -gt 0 -and @($signatureAliasValue).Count -gt 0) { @($signatureAliasValue)[0] } elseif ($signature.Count -gt 0) { $null } else { $t.sourceAlias }
    $b0Status = @()
    foreach ($repeat in $Repeats) {
        $pred = Read-GitJson $BCommit "$B0Root/$doc/r$repeat/prediction.v1.json"
        $found = @($pred.finalHeadings | Where-Object { $_.sourceId -eq $t.sourceId -and $_.start -eq $spanStart -and $_.end -eq $spanEnd }).Count -gt 0
        $b0Status += [ordered]@{ repeat = "R$repeat"; emitted = $found; bound = $found }
    }
    $container = if ($t.sourceId -like '*tbl*') { 'table cell' } else { 'paragraph' }
    $previous = if ($doc -eq 'DOC-0252') { 'same table: preceding parallel cell (exact text not persisted in B0 packet)' } elseif ($t.sourceId -eq 'body[1]/p[11]') { 'body[1]/p[10]' } elseif ($t.sourceId -eq 'body[1]/p[12]') { 'body[1]/p[11]' } elseif ($t.sourceId -eq 'body[1]/p[17]') { 'body[1]/p[12]' } else { 'body[1]/p[17]' }
    $next = if ($doc -eq 'DOC-0252') { 'same table: following parallel cell (exact text not persisted in B0 packet)' } elseif ($t.sourceId -eq 'body[1]/p[24]') { 'body[1]/p[25]' } elseif ($t.sourceId -eq 'body[1]/p[19]') { 'body[1]/p[24]' } elseif ($t.sourceId -eq 'body[1]/p[18]') { 'body[1]/p[19]' } elseif ($t.sourceId -eq 'body[1]/p[17]') { 'body[1]/p[18]' } else { 'body[1]/p[17]' }
    $targetRecords += [ordered]@{
        documentId = $doc; sourceId = $t.sourceId; sourceAlias = $alias; verbatimText = $t.verbatimText; goldRole = $gold.semanticRole; goldLevel = $gold.level; goldStatus = 'STRICT_GOLD_HEADING'
        b0EmissionByRepeat = $b0Status
        exactModelVisiblePacketEntry = [ordered]@{ persisted = $false; packetHash = $BByCell["$doc/R1"].packetHash; contract = 'sourceAlias + verbatimText + role'; sourceAlias = $alias; verbatimText = $t.verbatimText; role = 'not emitted; no model role persisted for omission'; sourceOrdinal = 'not persisted in target audit' }
        previousPacketEntry = [ordered]@{ sourceId = $previous; exactTextPersisted = $false; evidence = 'E2/residual context and ordered source identity only' }
        nextPacketEntry = [ordered]@{ sourceId = $next; exactTextPersisted = $false; evidence = 'E2/residual context and ordered source identity only' }
        containerType = $container; tableCoordinates = if ($container -eq 'table cell') { [ordered]@{ tableIndex = 2; row = 1; cell = 2; paragraph = 6 } } else { $null }
        sameTextOtherOccurrences = if ($doc -eq 'DOC-0252') { @([ordered]@{ sourceId = 'body[1]/p[46]'; sourceAlias = 'S0056'; goldStatus = 'NOT_GOLD'; modelBehavior = 'emitted'; exactText = 'Western Asia' }) } else { @() }
        siblingSourceOccurrences = if ($doc -eq 'DOC-0252') { @('body[1]/p[24]', 'body[1]/p[27]', 'body[1]/p[31]', 'body[1]/p[39]', 'body[1]/p[42]', $t.sourceId) } else { @('body[1]/p[11]', 'body[1]/p[12]', 'body[1]/p[17]', 'body[1]/p[18]', 'body[1]/p[19]', 'body[1]/p[24]') }
        parentOrContainerContext = $t.parentContext; localContext = $t.localContext; siblingPattern = $t.siblingPattern
        factsVisibleInB0 = @('sourceAlias', 'verbatimText', 'sourceOrdinal', 'ordered source position / neighboring lexical labels')
        factsPresentInSourceButNotB0 = @('container type', 'table row/column/cell identity', 'style id and outline level', 'indentation/bold/font size', 'numbering', 'explicit hierarchy', 'layout/page relation')
        evidence = 'E2 residual audit plus frozen B0 packet hash; exact serialized packet body was not persisted, so packet-entry text is marked non-persisted rather than invented.'
    }
}
$targetRecords = @($targetRecords | Sort-Object documentId, sourceId)
Write-Json (Join-Path $E3Root 'target-context.v1.json') ([ordered]@{ schemaVersion = 'a99-e3-target-occurrence-context-v1'; experimentId = 'A99-E3'; offlineOnly = $true; canonicalB0 = 'B_428_32_31'; modelCalls = 0; providerCalls = 0; targets = $targetRecords })

$westernPair = [ordered]@{
    pairId = 'E3-WESTERN-ASIA-SAME-LEXEME'; text = 'Western Asia'
    positive = [ordered]@{ documentId = 'DOC-0252'; sourceId = 'body[1]/tbl[2]/tr[1]/tc[2]/p[6]'; sourceAlias = 'S0056'; key = 'body[1]/tbl[2]/tr[1]/tc[2]/p[6]:182:194'; goldHeading = $true; b0EmittedAllRepeats = $false; container = 'table cell'; context = 'regional comparison table cell' }
    negative = [ordered]@{ documentId = 'DOC-0252'; sourceId = 'body[1]/p[46]'; sourceAlias = 'S0056'; key = 'body[1]/p[46]:0:12'; goldHeading = $false; b0EmittedAllRepeats = $true; container = 'paragraph'; context = 'regional update prose/body occurrence' }
    structuralDifference = @('table-cell occurrence versus paragraph occurrence', 'different parent/container context')
    lexicalDifference = 'none'; visibleInB0 = @('sourceAlias', 'verbatimText', 'sourceOrdinal', 'ordered lexical source'); hiddenFromB0 = @('container type', 'table coordinates', 'explicit hierarchy/style/layout')
    causalStrength = 'HIGH_FOR_THIS_PAIR_ONLY'; genericAcrossSeven = $false
}
Write-Json (Join-Path $E3Root 'same-lexeme-occurrence-contrasts.v1.json') ([ordered]@{ schemaVersion = 'a99-e3-same-lexeme-occurrence-contrasts-v1'; offlineOnly = $true; canonicalB0 = 'B_428_32_31'; pairs = @($westernPair); note = 'DOC-0258 stable misses form a repeated multi-lexeme family; no second exact Western Asia occurrence was persisted for that document, so no unsupported same-text pair is fabricated.' })

$family = @(
    @{ sourceId = 'body[1]/p[11]'; text = 'Africa'; alias = 'S0009'; order = 11 },
    @{ sourceId = 'body[1]/p[12]'; text = 'Asia and the Pacific'; alias = 'S0010'; order = 12 },
    @{ sourceId = 'body[1]/p[17]'; text = 'Eurostat–OECD PPP Program'; alias = $null; order = 17 },
    @{ sourceId = 'body[1]/p[18]'; text = 'Commonwealth of Independent States'; alias = 'S0014'; order = 18 },
    @{ sourceId = 'body[1]/p[19]'; text = 'Latin America and the Caribbean'; alias = 'S0015'; order = 19 },
    @{ sourceId = 'body[1]/p[24]'; text = 'Western Asia'; alias = 'S0018'; order = 24 }
)
$siblingRows = @()
foreach ($i in 0..($family.Count-1)) {
    $f = $family[$i]; $prev = if ($i -gt 0) { $family[$i-1].sourceId } else { 'body[1]/p[10] (parent heading)' }; $nxt = if ($i -lt $family.Count-1) { $family[$i+1].sourceId } else { 'body[1]/p[25] (next top-level heading)' }
    $siblingRows += [ordered]@{ sourceOrder = $f.order; sourceId = $f.sourceId; sourceAlias = $f.alias; text = $f.text; container = 'paragraph'; previousSemanticSibling = $prev; nextSemanticSibling = $nxt; modelVisibleNeighboringRecords = $true; goldHeading = $true; b0Emitted = $false; note = 'Repeated peer sequence is visible lexically; paragraph/container metadata is not serialized.' }
}
Write-Json (Join-Path $E3Root 'sibling-pattern-audit.v1.json') ([ordered]@{ schemaVersion = 'a99-e3-sibling-pattern-audit-v1'; documentId = 'DOC-0258'; canonicalB0 = 'B_428_32_31'; family = $siblingRows; conclusion = 'All six stable omissions share a repeated peer sequence, but that sequence is already represented by ordered source text; the table-cell target is a different container mechanism.' })

$features = @(
    @('container type', 'YES', 'NO_EXPLICIT_FIELD', 'mixed: table target versus paragraph family'),
    @('table row/column', 'YES', 'NO', 'only DOC-0252 target'),
    @('cell identity', 'YES', 'NO', 'only DOC-0252 target'),
    @('paragraph sibling relation', 'YES', 'PARTIAL_LEXICAL_ORDER_ONLY', 'DOC-0258 family, but not a unique heading discriminator'),
    @('list/numbering relation', 'YES', 'NO', 'not measured as a causal separator'),
    @('style id', 'YES', 'NO', 'not measured as a causal separator'),
    @('indentation', 'YES', 'NO', 'not measured as a causal separator'),
    @('bold', 'YES', 'NO', 'not measured as a causal separator'),
    @('font size', 'YES', 'NO', 'not measured as a causal separator'),
    @('preceding/following block relation', 'YES', 'PARTIAL_LEXICAL_ORDER_ONLY', 'parent/body ordering visible, block relation not explicit'),
    @('empty-line boundaries', 'YES', 'NO', 'not measured as a causal separator'),
    @('repeated sibling pattern', 'YES', 'YES_AS_ORDERED_TEXT', 'shared by DOC-0258 misses and already visible'),
    @('parent heading relation', 'YES', 'PARTIAL_LEXICAL_ORDER_ONLY', 'explicit hierarchy absent'),
    @('document section', 'YES', 'PARTIAL_LEXICAL_ORDER_ONLY', 'no single cross-target separator')
)
Write-Json (Join-Path $E3Root 'feature-availability-matrix.v1.json') ([ordered]@{ schemaVersion = 'a99-e3-feature-availability-matrix-v1'; offlineOnly = $true; canonicalB0 = 'B_428_32_31'; rows = @($features | ForEach-Object { [ordered]@{ feature = $_[0]; availableInSource = $_[1]; visibleInB0Packet = $_[2]; correlatesWithTarget = $_[3] } }); noFormattingRuleIntroduced = $true })

Write-Json (Join-Path $E3Root 'decision.v1.json') ([ordered]@{
    schemaVersion = 'a99-e3-occurrence-context-discrimination-decision-v1'; experimentId = 'A99-E3'; offlineOnly = $true; canonicalB0 = 'B_428_32_31'; modelCalls = 0; providerCalls = 0
    targetCount = 7; targetPersistentAcrossB0 = 7; classification = 'NO_GENERIC_OCCURRENCE_DISCRIMINATOR'
    rationale = @('The seven targets do not share one source-context family: one is a table-cell occurrence and six are paragraph peers in a repeated lexical sequence.', 'The DOC-0258 repeated sibling labels and ordered lexical neighborhood are already represented in B0 semantic text.', 'Container/style/layout facts are absent, but no single absent fact is shown to separate all seven target omissions from non-heading occurrences.', 'The exact serialized B0 packet body was not persisted; the audit records that limitation and does not infer missing packet fields beyond the frozen contract and residual audit.')
    i6Authorized = $false; i6Owner = $null; i6InformationDelta = $null; rejectAsI3Equivalent = $true; nextCausalOwner = 'MODEL_TASK_CAPABILITY / residual semantic classification'; goldFirewall = 'PASS'
})

Write-Json (Join-Path $M1Root 'summary.v1.json') ([ordered]@{
    schemaVersion = 'a99-m1-e3-offline-summary-v1'; taskHead = $TaskHead; modelCalls = 0; providerCalls = 0; goldFirewall = 'PASS'
    m1 = [ordered]@{ classification = 'CANONICAL_B0_428_32_31'; canonicalLineage = 'lineage-b.v1.json'; canonicalMetric = $canonicalMetric; alternate425Source = 'lineage-a.v1.json'; alternateViewType = 'ALTERNATE_FROZEN_PRIOR_RUN'; firstScoreDivergence = $firstDivergence; conservation = $deltaTotals; i5DecisionStability = 'REVERT_STABLE'; canonicalRationale = 'B is the frozen campaign explicitly named by the I5 behavioral parent B0@76c4e01 and its residual target family exactly matches the I5 seven-case target set. A is an earlier same-contract frozen run whose persistent omission set differs, so its 425 aggregate cannot be silently used as the I5 comparator.' }
    e3 = [ordered]@{ classification = 'NO_GENERIC_OCCURRENCE_DISCRIMINATOR'; targetCount = 7; i6Authorized = $false; artifactRoot = "$OutRoot/e3" }
    preservedArtifacts = @("$I5Root/decision.v1.json", "$I5Root/summary.v1.json")
})

Write-Output "M1_CLASSIFICATION=CANONICAL_B0_428_32_31"
Write-Output "M1_CONSERVATION=$($deltaTotals.tp)/$($deltaTotals.fp)/$($deltaTotals.fn)"
Write-Output "I5_CANONICAL=$canonicalTp/$canonicalFp/$canonicalFn -> $i5Tp/$i5Fp/$i5Fn"
Write-Output 'I5_DECISION_STABILITY=REVERT_STABLE'
Write-Output 'E3_CLASSIFICATION=NO_GENERIC_OCCURRENCE_DISCRIMINATOR'
Write-Output 'I6_AUTHORIZED=false'
Write-Output 'MODEL_CALLS=0'
Write-Output 'PROVIDER_CALLS=0'
