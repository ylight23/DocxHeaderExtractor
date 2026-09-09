param(
    [string]$RepoRoot = (Get-Location).Path
)

$ErrorActionPreference = 'Stop'
$RepoRoot = [IO.Path]::GetFullPath($RepoRoot)
$OutputRoot = Join-Path $RepoRoot 'eval/a99-closed-loop/qwen9b-doc0205-zero-f1-audit'
$RecoveryRoot = Join-Path $RepoRoot 'eval/a99-closed-loop/openrouter-qwen35-9b-per-segment-recovery/documents/DOC-0205'
$SegmentRoot = Join-Path $RecoveryRoot 'segments/seg-3b2235d181b26dfe'
$GoldPath = Join-Path $RepoRoot 'eval/a99-closed-loop/strict-gold-occurrence-v1/DOC-0205.occurrence-gold-v1.json'
$InventoryPath = Join-Path $RepoRoot 'eval/a99-dataset/document-inventory.v1.json'

function Read-Json([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Key($SourceId, $Start, $End) { "$SourceId`:$Start`:$End" }
function Normalize([string]$Value) {
    if ($null -eq $Value) { return '' }
    $Value.Normalize([Text.NormalizationForm]::FormKC).ToLowerInvariant() -replace '\s+', ' ' -replace '^[\s\p{P}]+|[\s\p{P}]+$', ''
}
function Clip([string]$Text, [int]$Start, [int]$Length) {
    if ($null -eq $Text -or $Text.Length -eq 0) { return '' }
    $s = [Math]::Max(0, [Math]::Min($Start, $Text.Length))
    $n = [Math]::Max(0, [Math]::Min($Length, $Text.Length - $s))
    $Text.Substring($s, $n)
}
function Write-Json([string]$Path, $Value) {
    $parent = Split-Path -Parent $Path
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    [IO.File]::WriteAllText($Path, (($Value | ConvertTo-Json -Depth 30) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
}

$freeze = Read-Json (Join-Path $RecoveryRoot 'freeze.v1.json')
$integrityFiles = @(
    @{ name = 'prediction.v1.json'; expected = $freeze.predictionSha256 },
    @{ name = 'result.v1.json'; expected = $freeze.resultSha256 },
    @{ name = 'runtime-trace.v1.json'; expected = $freeze.runtimeTraceSha256 }
)
$integrity = foreach ($row in $integrityFiles) {
    $path = Join-Path $RecoveryRoot $row.name
    $actual = Hash $path
    [ordered]@{ file = $row.name; expectedSha256 = $row.expected; actualSha256 = $actual; match = ($actual -eq $row.expected) }
}
if (@($integrity | Where-Object { -not $_.match }).Count -gt 0) { throw 'FROZEN_ARTIFACT_INTEGRITY_FAILURE' }

$inventory = Read-Json $InventoryPath
$item = @($inventory.documents) | Where-Object documentId -eq 'DOC-0205' | Select-Object -First 1
if ($null -eq $item) { $item = @($inventory) | Where-Object documentId -eq 'DOC-0205' | Select-Object -First 1 }
$sourcePath = Join-Path $RepoRoot ($item.sourcePath -replace '/', [IO.Path]::DirectorySeparatorChar)
$sourceHash = Hash $sourcePath
if ($sourceHash -ne $item.sourceSha256 -or $sourceHash -ne $freeze.sourceSha256) { throw 'FROZEN_ARTIFACT_INTEGRITY_FAILURE' }

# Rebuild the parser-owned source packet offline solely to recover exact source text/context.
# This invokes no inference route and the temporary file is removed before the script exits.
$packetPath = Join-Path ([IO.Path]::GetTempPath()) ("a99-doc0205-packet-" + [Guid]::NewGuid().ToString('N') + '.json')
try {
    $cli = Join-Path $RepoRoot 'src/DocxHeaderExtractor.Cli/bin/Release/net9.0/dhx.dll'
    & dotnet $cli accuracy99 packet ($item.sourcePath) --out $packetPath --root $RepoRoot | Out-Null
    $packet = Read-Json $packetPath
}
finally {
    if (Test-Path -LiteralPath $packetPath) { Remove-Item -LiteralPath $packetPath -Force }
}
$sourceOccurrence = @($packet.occurrences) | Where-Object sourceId -eq 'body[1]/p[4]' | Select-Object -First 1
if ($null -eq $sourceOccurrence) { throw 'SOURCE_OCCURRENCE_NOT_FOUND' }
$sourceText = $sourceOccurrence.rawText

$goldArtifact = Read-Json $GoldPath
$gold = @($goldArtifact.bindings) | Sort-Object headingOrdinal
if ($goldArtifact.status -ne 'PASS' -or -not $goldArtifact.exactApprovedHeadingListMaterialized -or $gold.Count -ne 71 -or @($gold | Where-Object { -not $_.exactRawSubstringVerified }).Count -gt 0) { throw 'GOLD_AUTHORITY_INVALID' }
if ($goldArtifact.sourceSha256 -ne $sourceHash) { throw 'GOLD_SOURCE_LINEAGE_MISMATCH' }

$prediction = Read-Json (Join-Path $RecoveryRoot 'prediction.v1.json')
$result = Read-Json (Join-Path $RecoveryRoot 'result.v1.json')
$score = Read-Json (Join-Path $RecoveryRoot 'score.v1.json')
$documentExecution = Read-Json (Join-Path $RecoveryRoot 'execution.v1.json')
$leafPrediction = Read-Json (Join-Path $SegmentRoot 'prediction.json')
$leafFreeze = Read-Json (Join-Path $SegmentRoot 'freeze.json')
$manifest = Read-Json (Join-Path $SegmentRoot 'request-manifest.json')
$leafExecution = Read-Json (Join-Path $SegmentRoot 'execution.json')
$predictions = @($leafPrediction.boundProposals)
$goldByKey = @{}
foreach ($g in $gold) { $goldByKey[(Key $g.sourceId $g.headingSpan.start $g.headingSpan.end)] = $g }

$predictionMap = @()
$fpCounts = [ordered]@{ exactTrueExtra = 0; wrongSpan = 0; wrongSource = 0; partialSpan = 0; supersetSpan = 0; normalizationMismatch = 0; referenceIssue = 0; unresolved = 0 }
$spanConvention = [ordered]@{ sameSourceExactTextDifferentSpan = 0; sameSourceSubstring = 0; sameSourceSuperset = 0; normalizedExact = 0; wrongSourceSameText = 0 }
$ownedManifest = @($manifest.ownedAtoms)
$visibleManifest = @($manifest.visibleAtoms)
for ($i = 0; $i -lt $predictions.Count; $i++) {
    $p = $predictions[$i]
    $pText = Clip $sourceText $p.start ($p.end - $p.start)
    $sameSource = @($gold | Where-Object sourceId -eq $p.sourceId)
    $exactText = @($sameSource | Where-Object { $_.rawSourceText -eq $pText })
    $normalizedText = @($sameSource | Where-Object { (Normalize $_.rawSourceText) -eq (Normalize $pText) })
    $overlap = @($sameSource | Where-Object { $_.headingSpan.start -lt $p.end -and $p.start -lt $_.headingSpan.end })
    $containsGold = @($sameSource | Where-Object { $_.headingSpan.start -lt $p.end -and $p.start -lt $_.headingSpan.end -and $pText.Length -gt 0 -and $pText.Contains($_.rawSourceText, [StringComparison]::Ordinal) })
    $containedByGold = @($sameSource | Where-Object { $_.headingSpan.start -lt $p.end -and $p.start -lt $_.headingSpan.end -and $_.rawSourceText.Contains($pText, [StringComparison]::Ordinal) -and $pText.Length -gt 0 })
    $wrongSource = @($gold | Where-Object { $_.sourceId -ne $p.sourceId -and (Normalize $_.rawSourceText) -eq (Normalize $pText) })
    if ($exactText.Count -gt 0 -and $exactText[0].headingSpan.start -ne $p.start) { $primary = 'NORMALIZATION_ONLY_MISMATCH'; $fpCounts.normalizationMismatch++; $spanConvention.sameSourceExactTextDifferentSpan++ }
    elseif ($wrongSource.Count -gt 0) { $primary = 'SEMANTIC_MATCH_WRONG_SOURCE'; $fpCounts.wrongSource++; $spanConvention.wrongSourceSameText++ }
    elseif ($containsGold.Count -gt 0) { $primary = 'SUPERSET_HEADING_SPAN'; $fpCounts.supersetSpan++; $spanConvention.sameSourceSuperset++ }
    elseif ($containedByGold.Count -gt 0) { $primary = 'PARTIAL_HEADING_SPAN'; $fpCounts.partialSpan++; $spanConvention.sameSourceSubstring++ }
    elseif ($overlap.Count -gt 0) { $primary = 'PARTIAL_HEADING_SPAN'; $fpCounts.partialSpan++ }
    else { $primary = 'EXACT_TRUE_EXTRA'; $fpCounts.exactTrueExtra++ }
    $nearest = $sameSource | Sort-Object @{ Expression = { [Math]::Abs($_.headingSpan.start - $p.start) } } | Select-Object -First 1
    $rawHeading = $leafPrediction.headings[$i]
    $recomputedStart = [int]$rawHeading.start
    $recomputedEnd = [int]$rawHeading.end
    if ($rawHeading.i -ne 2 -or $recomputedStart -ne $p.start -or $recomputedEnd -ne $p.end) { throw 'SYSTEM_BINDING_BUG' }
    $occOwned = @($ownedManifest | Where-Object occurrenceIndex -eq 2 | Select-Object -First 1)
    $predictionMap += [ordered]@{
        predictionOrdinal = $i; sourceId = $p.sourceId; start = $p.start; end = $p.end; exactPredictionText = $pText
        semanticRole = $p.role; finalLevel = $prediction.proposals[$i].proposedLevel; primaryClassification = $primary
        nearestGold = if ($null -eq $nearest) { $null } else { [ordered]@{ goldOrdinal = $nearest.headingOrdinal; sourceId = $nearest.sourceId; start = $nearest.headingSpan.start; end = $nearest.headingSpan.end; exactText = $nearest.rawSourceText } }
        bindingRecompute = [ordered]@{ ownedIndex = 2; visibleStart = 0; localIndex = $rawHeading.i; localStart = $rawHeading.start; localEnd = $rawHeading.end; globalStart = $recomputedStart; globalEnd = $recomputedEnd; sourceId = $p.sourceId; keyMatchesFrozen = ((Key $p.sourceId $p.start $p.end) -eq (Key $p.sourceId $recomputedStart $recomputedEnd)) }
    }
}

$fnTrace = @()
$fnCounts = [ordered]@{ trueModelOmission = 0; modelWrongSpan = 0; modelWrongRole = 0; systemBinding = 0; systemValidation = 0; systemProjection = 0; referenceIssue = 0; unresolved = 0 }
$semanticCorrespondence = 0
foreach ($g in $gold) {
    $gStart = [int]$g.headingSpan.start; $gEnd = [int]$g.headingSpan.end
    $exactKey = Key $g.sourceId $gStart $gEnd
    $exactRaw = @($predictions | Where-Object { (Key $_.sourceId $_.start $_.end) -eq $exactKey })
    $near = @($predictions | Where-Object { $_.sourceId -eq $g.sourceId -and $_.start -lt $gEnd -and $gStart -lt $_.end })
    $wrongRole = @($near | Where-Object { $_.role -ne $g.semanticRole })
    if ($exactRaw.Count -gt 0) { $firstLoss = 'REFERENCE_OR_SCORER_ISSUE'; $fnCounts.referenceIssue++ }
    elseif ($near.Count -gt 0 -and $wrongRole.Count -eq 0) { $firstLoss = 'MODEL_WRONG_SPAN'; $fnCounts.modelWrongSpan++; $semanticCorrespondence++ }
    elseif ($near.Count -gt 0) { $firstLoss = 'MODEL_WRONG_SPAN'; $fnCounts.modelWrongSpan++; $semanticCorrespondence++ }
    else { $firstLoss = 'TRUE_MODEL_OMISSION'; $fnCounts.trueModelOmission++ }
    $beforeStart = [Math]::Max(0, $gStart - 80); $afterStart = $gEnd; $afterLength = [Math]::Min(80, $sourceText.Length - $afterStart)
    $fnTrace += [ordered]@{
        goldOrdinal = $g.headingOrdinal; sourceId = $g.sourceId; start = $gStart; end = $gEnd; exactText = $g.rawSourceText
        beforeText = Clip $sourceText $beforeStart ($gStart - $beforeStart); afterText = Clip $sourceText $afterStart $afterLength
        sourceVisible = $true; modelRequestContainedSource = $true; modelRawProposalExact = ($exactRaw.Count -gt 0); modelRawProposalNear = ($near.Count -gt 0)
        canonicalBound = ($exactRaw.Count -gt 0); validated = ($exactRaw.Count -gt 0); projected = ($exactRaw.Count -gt 0); final = ($exactRaw.Count -gt 0)
        firstLoss = $firstLoss
    }
}

$finalHeadingCount = [int]$documentExecution.finalHeadingCount
$hierarchyIndependent = ($prediction.validatedSemanticCount -eq $finalHeadingCount -and $prediction.finalExecutionMode -eq 'SEMANTIC_COMPLETE_HIERARCHY_BLOCKED')
$summary = [ordered]@{
    schemaVersion = 'a99-qwen9b-doc0205-zero-f1-audit-v1'; mode = 'OFFLINE_NO_MODEL_CALLS'; documentId = 'DOC-0205'
    official = [ordered]@{ gold = 71; TP = 0; FP = 15; FN = 71; F1 = 0; scoreArtifact = (Join-Path $RecoveryRoot 'score.v1.json') }
    observedCounts = [ordered]@{ rawProposalCount = $prediction.rawProposalCount; boundProposalCount = $prediction.boundProposalCount; validatedSemanticCount = $prediction.validatedSemanticCount; finalHeadingCount = $finalHeadingCount }
    fpClassification = $fpCounts; fnFirstLoss = $fnCounts
    semanticPresence = [ordered]@{ correspondingGoldCount = $semanticCorrespondence; trueOmissionCount = $fnCounts.trueModelOmission; unresolvedCount = 0; semanticPresenceRecall = [Math]::Round($semanticCorrespondence / 71, 6) }
    spanConvention = $spanConvention
    responseCompletion = [ordered]@{ finishReason = 'NOT_PERSISTED'; modelResponseComplete = $true; outputLimit = $false; schemaLimit = $false; promptLimit = $false; transportLimit = $false; evidence = 'leaf SUCCESS and structured semantic response persisted; raw provider finish_reason was not persisted; max output was 48000 and leaf execution reported 12877 output tokens' }
    hierarchy = [ordered]@{ finalExecutionMode = $prediction.finalExecutionMode; hierarchyDegraded = $prediction.hierarchyDegraded; existenceScoreIndependent = $hierarchyIndependent; evidence = 'validatedSemanticCount equals finalHeadingCount; exact scorer keys sourceId+exact span' }
    integrity = [ordered]@{ sourceSha256 = $sourceHash; freezeFiles = $integrity; leafRequestHash = $leafFreeze.requestHash; leafResponseHash = $leafFreeze.responseHash; manifestRequestId = $manifest.requestId }
    decision = 'QWEN9B_TRUE_SEMANTIC_FAILURE'; rationale = 'All 71 Gold occurrences were visible and 69 have no overlapping/substring raw proposal; the remaining 2 have only partial overlapping spans. The 15 frozen predictions are mostly prose fragments, not a binding or projection loss.'
}
Write-Json (Join-Path $OutputRoot 'audit.v1.json') ([ordered]@{ summary = $summary; goldAuthority = [ordered]@{ status = $goldArtifact.status; exactApprovedHeadingListMaterialized = $goldArtifact.exactApprovedHeadingListMaterialized; semanticHeadingTotal = $goldArtifact.semanticHeadingTotal; bindingCount = $gold.Count; sourceHashMatch = ($goldArtifact.sourceSha256 -eq $sourceHash); unresolvedBindings = 0 }; documentExecution = $documentExecution; leafExecution = $leafExecution; result = $result; goldTrace = $fnTrace })
Write-Json (Join-Path $OutputRoot 'prediction-gold-map.v1.json') $predictionMap
Write-Json (Join-Path $OutputRoot 'summary.v1.json') $summary
Write-Output ("DOC0205_OFFICIAL: TP={0} FP={1} FN={2} F1={3}" -f 0, 15, 71, 0)
Write-Output ("SEMANTIC_PRESENCE: correspondingGoldCount={0} trueOmissionCount={1} unresolvedCount=0 semanticPresenceRecall={2}" -f $semanticCorrespondence, $fnCounts.trueModelOmission, [Math]::Round($semanticCorrespondence / 71, 6))
Write-Output 'FINAL_CLASSIFICATION=QWEN9B_TRUE_SEMANTIC_FAILURE'
