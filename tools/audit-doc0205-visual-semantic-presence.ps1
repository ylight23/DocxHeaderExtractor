[CmdletBinding()]
param(
    [string]$Root = (Join-Path (Get-Location) 'eval/a99-closed-loop/doc0205-visual-semantic-audit')
)

$ErrorActionPreference = 'Stop'

function Read-Json([string]$Path) { Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json }
function Hash-File([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Tokens([string]$Text) { if ($null -eq $Text) { return @() }; @([regex]::Matches($Text.Normalize([Text.NormalizationForm]::FormC).ToLowerInvariant(), '[\p{L}\p{N}]+') | ForEach-Object { $_.Value }) }
function Contains-TokenSequence([string[]]$Haystack, [string[]]$Needle) {
    if ($Needle.Count -lt 2 -or $Needle.Count -gt $Haystack.Count) { return $false }
    for ($i = 0; $i -le $Haystack.Count - $Needle.Count; $i++) {
        $same = $true
        for ($j = 0; $j -lt $Needle.Count; $j++) { if ($Haystack[$i + $j] -ne $Needle[$j]) { $same = $false; break } }
        if ($same) { return $true }
    }
    return $false
}
function SpanStart($x) { [int]$x.headingSpan.start }
function SpanEnd($x) { [int]$x.headingSpan.end }
function Key([string]$sourceId, [int]$start, [int]$end) { "$sourceId`:$start`:$end" }

$repo = (Get-Location).Path
$contractRoot = 'eval/a99-closed-loop/canonical-heading-contract-v2/DOC-0205'
$visualRoot = 'eval/a99-closed-loop/qwen37-flash-visual-ceiling/DOC-0205/visual-v2'
$inventory = Read-Json 'eval/a99-dataset/document-inventory.v1.json'
$inventoryDoc = @($inventory.documents | Where-Object documentId -eq 'DOC-0205')[0]
$sourcePath = Join-Path $repo ($inventoryDoc.sourcePath -replace '\\','\')
$textPredictionPath = Join-Path $contractRoot 'prediction.v2.json'
$textResultPath = Join-Path $contractRoot 'result.v2.json'
$textFreezePath = Join-Path $contractRoot 'freeze.v2.json'
$visualPredictionPath = Join-Path $visualRoot 'prediction.v2.json'
$visualResultPath = Join-Path $visualRoot 'result.v2.json'
$visualFreezePath = Join-Path $visualRoot 'freeze.v2.json'
$pageManifestPath = Join-Path $visualRoot 'page-manifest.v2.json'
$alignmentPath = Join-Path $visualRoot 'visual-source-alignment.v2.json'
$packetPath = '.tmp-doc0205-packet.json'
$goldPath = 'eval/a99-closed-loop/strict-gold-occurrence-v1/DOC-0205.occurrence-gold-v1.json'
$strictGoldPath = 'eval/a99-closed-loop/strict-gold-v4/DOC-0205.strict-gold-v4.json'

# Gold firewall: frozen predictions, results, render/alignment, source and hashes are checked
# before the canonical Gold file is read.
$textPrediction = Read-Json $textPredictionPath
$textResult = Read-Json $textResultPath
$textFreeze = Read-Json $textFreezePath
$visualPrediction = Read-Json $visualPredictionPath
$visualResult = Read-Json $visualResultPath
$visualFreeze = Read-Json $visualFreezePath
$pageManifest = Read-Json $pageManifestPath
$alignment = Read-Json $alignmentPath
$packet = Read-Json $packetPath
$sourceSha = Hash-File $sourcePath
$integrityChecks = [ordered]@{
    sourceDocxHash = $sourceSha -eq $inventoryDoc.sourceSha256 -and $sourceSha -eq $textFreeze.sourceSha256 -and $sourceSha -eq $visualFreeze.sourceSha256 -and $sourceSha -eq $pageManifest.sourceDocxSha256
    textPredictionHash = $textFreeze.predictionSha256 -eq (Hash-File $textPredictionPath)
    textResultHash = $textFreeze.resultSha256 -eq (Hash-File $textResultPath)
    visualPredictionHash = $visualFreeze.predictionSha256 -eq (Hash-File $visualPredictionPath)
    visualResultHash = $visualFreeze.resultSha256 -eq (Hash-File $visualResultPath)
    visualPageManifestHash = $visualFreeze.pageRenderManifestSha256 -eq (Hash-File $pageManifestPath)
    visualAlignmentHash = $visualFreeze.mappingSha256 -eq (Hash-File $alignmentPath)
    textFreezeGoldFirewall = $textFreeze.goldReadBeforeFreeze -eq $false
    textPredictionGoldFirewall = $textPrediction.goldReadBeforeFreeze -eq $false
    textResultGoldFirewall = $textResult.goldReadBeforeFreeze -eq $false
    visualFreezeGoldFirewall = $visualFreeze.goldReadBeforeFreeze -eq $false
    visualPredictionGoldFirewall = $visualPrediction.goldReadBeforeFreeze -eq $false
    visualResultGoldFirewall = $visualResult.goldReadBeforeFreeze -eq $false
    renderDeterministic = $pageManifest.deterministic -eq $true -and $pageManifest.coverage -eq 1 -and $pageManifest.pageCount -eq 19
    alignmentGate = $alignment.gatePass -eq $true -and $alignment.roundTripValid -eq $true -and $alignment.sourceCharacterCoverage -eq 1
}
if (@($integrityChecks.GetEnumerator() | Where-Object { -not $_.Value }).Count -gt 0) { throw ('ARTIFACT_INTEGRITY_FAILURE ' + (($integrityChecks.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object Key) -join ',')) }

# Only after all frozen authority checks pass may Gold be loaded.
$gold = Read-Json $goldPath
$strictGold = Read-Json $strictGoldPath
if ((Hash-File $strictGoldPath) -ne $gold.strictGoldArtifactSha256) { throw 'ARTIFACT_INTEGRITY_FAILURE strict-gold-v4-hash' }
if ($gold.sourceSha256 -ne $sourceSha -or $gold.semanticHeadingTotal -ne 71 -or @($gold.bindings).Count -ne 71) { throw 'ARTIFACT_INTEGRITY_FAILURE strict-gold-occurrence-content' }

$sourceById = @{}
foreach ($occurrence in $packet.occurrences) { $sourceById[$occurrence.sourceId] = $occurrence }
$aliasByName = @{}
foreach ($alias in $alignment.aliases) { $aliasByName[$alias.alias] = $alias }
$finalKeys = @($visualPrediction.proposals | ForEach-Object { Key $_.sourceId $_.headingSpan.start $_.headingSpan.end })
$finalKeySet = [Collections.Generic.HashSet[string]]::new([string[]]$finalKeys, [StringComparer]::Ordinal)

$rawRows = @()
$windowFiles = Get-ChildItem (Join-Path $repo $visualRoot) -Recurse -Filter '*.v2.json' | Where-Object { $_.FullName -match '\\windows\\' } | Sort-Object Name
foreach ($file in $windowFiles) {
    $window = Read-Json $file.FullName
    foreach ($heading in @($window.headings)) {
        $sourceId = $null; $globalStart = $null; $globalEnd = $null; $reconstructed = $null; $reconstructable = $false; $bindingReason = 'RAW_ALIAS_NOT_IN_SOURCE_ALIAS_MAP'
        if ($aliasByName.ContainsKey([string]$heading.alias)) {
            $alias = $aliasByName[[string]$heading.alias]
            $sourceId = [string]$alias.sourceId
            $source = $sourceById[$sourceId]
            $localStart = [int]$heading.start; $localEnd = [int]$heading.end
            if ($localEnd -le [int]$alias.aliasTextLength) { $globalStart = [int]$alias.visibleStartCharacter + $localStart; $globalEnd = [int]$alias.visibleStartCharacter + $localEnd; $bindingReason = 'SOURCE_ALIAS_PAGE_LOCAL_OFFSET' }
            else { $globalStart = $localStart; $globalEnd = $localEnd; $bindingReason = 'SOURCE_ALIAS_FULL_OCCURRENCE_OFFSET' }
            if ($null -ne $source -and $globalStart -ge 0 -and $globalEnd -le $source.rawText.Length -and $globalStart -lt $globalEnd) {
                $reconstructed = $source.rawText.Substring($globalStart, $globalEnd - $globalStart); $reconstructable = $true
            }
        }
        $rawKey = if ($null -ne $sourceId -and $null -ne $globalStart) { Key $sourceId $globalStart $globalEnd } else { $null }
        $rawRows += [ordered]@{
            windowFile=$file.Name; pageIndices=@($window.pageIndices); pageCount=$window.pageIndices.Count; visualAlias=[string]$heading.alias
            rawStart=[int]$heading.start; rawEnd=[int]$heading.end; rawText=$reconstructed; rawTextReconstructable=$reconstructable
            semanticRole=[string]$heading.role; sourceId=$sourceId; reconstructedGlobalSpan=if($null -ne $globalStart){[ordered]@{start=$globalStart;end=$globalEnd}}else{$null}
            bindingReason=$bindingReason; boundToFrozenProposal=($null -ne $rawKey -and $finalKeySet.Contains($rawKey))
        }
    }
}
$rawCount = $rawRows.Count
$boundCount = @($rawRows | Where-Object boundToFrozenProposal).Count
$validatedCount = [int]$visualPrediction.validatedSemanticCount
$projectedCount = @($visualPrediction.projection | Where-Object projectionStatus -eq 'INCLUDED').Count
$finalCount = @($visualPrediction.headings).Count
$boundWindowCount = [int]((Get-ChildItem (Join-Path $repo $visualRoot) -Recurse -Filter '*.v2.json' | Where-Object { $_.FullName -match '\\windows\\' } | ForEach-Object { (Read-Json $_.FullName).boundCount } | Measure-Object -Sum).Sum)
$rawLossFirstStage = $rawCount - $boundWindowCount
$rawAliasMismatch = @($rawRows | Where-Object { $_.bindingReason -eq 'RAW_ALIAS_NOT_IN_SOURCE_ALIAS_MAP' }).Count

function Get-GoldPages($g) {
    @($alignment.aliases | Where-Object { $_.sourceId -eq $g.sourceId -and [int]$g.headingSpan.start -lt [int]$_.visibleEndCharacter -and [int]$g.headingSpan.end -gt [int]$_.visibleStartCharacter } | ForEach-Object pageIndex | Sort-Object -Unique)
}

$goldRows = @()
foreach ($g in @($gold.bindings)) {
    $pages = @(Get-GoldPages $g)
    $samePage = @($rawRows | Where-Object { @($_.pageIndices | Where-Object { $pages -contains $_ }).Count -gt 0 })
    $semanticCandidates = @($samePage | Where-Object {
        $label = [string]$_.visualAlias
        $labelTokens = @(Tokens $label)
        $goldTokens = @(Tokens $g.rawSourceText)
        $_.semanticRole -notin @('BODY_FRAGMENT','FRONT_MATTER','RUNNING_HEADER','TOC_ENTRY','CAPTION','LIST_ITEM','TABLE_LABEL','DECORATIVE_TEXT') -and (Contains-TokenSequence $goldTokens $labelTokens)
    })
    $boundSemantic = @($semanticCandidates | Where-Object boundToFrozenProposal)
    $exactSemantic = @($boundSemantic | Where-Object { $_.sourceId -eq $g.sourceId -and $_.reconstructedGlobalSpan.start -eq $g.headingSpan.start -and $_.reconstructedGlobalSpan.end -eq $g.headingSpan.end })
    $sameSourceWrong = @($boundSemantic | Where-Object { $_.sourceId -eq $g.sourceId -and $_.reconstructedGlobalSpan.start -ne $g.headingSpan.start -or $_.reconstructedGlobalSpan.end -ne $g.headingSpan.end })
    $wrongSource = @($semanticCandidates | Where-Object { $_.boundToFrozenProposal -and $_.sourceId -ne $g.sourceId })
    if ($exactSemantic.Count -gt 0) { $classification='EXACT_PRESENT' }
    elseif ($wrongSource.Count -gt 0) { $classification='SEMANTIC_PRESENT_WRONG_SOURCE' }
    elseif ($boundSemantic.Count -gt 0) { $classification='SEMANTIC_PRESENT_WRONG_SPAN' }
    elseif ($semanticCandidates.Count -gt 0) { $classification='VISUALLY_DETECTED_SOURCE_MAPPING_FAILED' }
    else { $classification='TRUE_VLM_OMISSION' }
    $goldRows += [ordered]@{
        headingOrdinal=$g.headingOrdinal; goldText=$g.rawSourceText; sourceId=$g.sourceId; start=[int]$g.headingSpan.start; end=[int]$g.headingSpan.end
        pageIndex=$pages; visibleOnRenderedPage=($pages.Count -gt 0); sourceMappingAvailable=($pages.Count -gt 0)
        vlmProposalOnSamePage=$semanticCandidates.Count; semanticCandidateCount=$semanticCandidates.Count
        semanticCorrespondence=@($semanticCandidates | ForEach-Object { [ordered]@{windowFile=$_.windowFile;pages=$_.pageIndices;visualAlias=$_.visualAlias;role=$_.semanticRole;rawStart=$_.rawStart;rawEnd=$_.rawEnd;bound=$_.boundToFrozenProposal;bindingReason=$_.bindingReason} })
        classification=$classification
        note=if($classification -eq 'VISUALLY_DETECTED_SOURCE_MAPPING_FAILED'){'Semantic alias occurs in the Gold label and its rendered page window, but no canonical source binding survived raw-to-bound.'}else{'Diagnostic only; official exact score is unchanged.'}
    }
}
$semanticCounts = [ordered]@{}
foreach ($row in $goldRows) { if (-not $semanticCounts.Contains($row.classification)) { $semanticCounts[$row.classification]=0 }; $semanticCounts[$row.classification]++ }
$exactPresent = [int]$semanticCounts['EXACT_PRESENT']; $wrongSpan = [int]$semanticCounts['SEMANTIC_PRESENT_WRONG_SPAN']; $wrongSource = [int]$semanticCounts['SEMANTIC_PRESENT_WRONG_SOURCE']; $mappingFailure = [int]$semanticCounts['VISUALLY_DETECTED_SOURCE_MAPPING_FAILED']; $trueOmission = [int]$semanticCounts['TRUE_VLM_OMISSION']; $unresolved = [int]$semanticCounts['UNRESOLVED']
$partialSpan = 0; $supersetSpan = 0
if ($exactPresent + $wrongSpan + $wrongSource + $mappingFailure + $trueOmission + $unresolved -ne 71) { throw 'CLASSIFICATION_ARITHMETIC_FAILURE' }

# Objective OOXML audit of the dominant paragraph. This is source evidence, not runtime logic.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $sourcePath)); $entry = $zip.GetEntry('word/document.xml'); $reader = [IO.StreamReader]::new($entry.Open()); $xmlText = $reader.ReadToEnd(); $reader.Dispose(); $zip.Dispose()
$xml = [xml]$xmlText; $ns = [Xml.XmlNamespaceManager]::new($xml.NameTable); $ns.AddNamespace('w','http://schemas.openxmlformats.org/wordprocessingml/2006/main'); $paragraphs=@($xml.SelectNodes('//w:body/w:p',$ns)); $dominant=$paragraphs[3]
$brs=@($dominant.SelectNodes('.//w:br',$ns)); $brTypes=[ordered]@{}; foreach($br in $brs){$type=$br.GetAttribute('type','http://schemas.openxmlformats.org/wordprocessingml/2006/main');if([string]::IsNullOrWhiteSpace($type)){$type='text'};if(!$brTypes.Contains($type)){$brTypes[$type]=0};$brTypes[$type]++}
$xmlFacts=[ordered]@{
    paragraphCount=$paragraphs.Count; dominantSourceId='body[1]/p[4]'; dominantParagraphChildElements=@($dominant.ChildNodes|ForEach-Object LocalName)
    runCount=@($dominant.SelectNodes('.//w:r',$ns)).Count; textNodeCount=@($dominant.SelectNodes('.//w:t',$ns)).Count; xmlTextNodeCharacters=((@($dominant.SelectNodes('.//w:t',$ns)|ForEach-Object InnerText) -join '').Length)
    breakElementCount=$brs.Count; breakTypes=$brTypes; crCount=@($dominant.SelectNodes('.//w:cr',$ns)).Count; tabCount=@($dominant.SelectNodes('.//w:tab',$ns)).Count
    lastRenderedPageBreakCount=@($dominant.SelectNodes('.//w:lastRenderedPageBreak',$ns)).Count; fieldElementCount=@($dominant.SelectNodes('.//w:fldChar|.//w:instrText',$ns)).Count
    drawingCount=@($dominant.SelectNodes('.//w:drawing',$ns)).Count; textBoxCount=@($dominant.SelectNodes('.//w:txbxContent',$ns)).Count; sectionBreakCount=@($dominant.SelectNodes('.//w:sectPr',$ns)).Count
    pageBreakBeforeCount=@($dominant.SelectNodes('.//w:pageBreakBefore',$ns)).Count; sourceFactRawCharacters=$sourceById['body[1]/p[4]'].rawText.Length
    sourceFactNewlineCount=([regex]::Matches($sourceById['body[1]/p[4]'].rawText,"`r?`n")).Count
}
$signalAudit=@(
    [ordered]@{signal='run_boundaries';ooXml='PRESENT_IN_OOXML (1 run; 1057 text children inside it)';sourceFacts='LOST_DURING_NORMALIZATION';textLlm='VISIBLE_TO_TEXT_LLM only as concatenated text';vlm='VISIBLE_TO_VLM as page layout, not XML boundaries';status='LOST_DURING_NORMALIZATION'},
    [ordered]@{signal='manual_line_breaks_w_br';ooXml='PRESENT_IN_OOXML (1512 type=text w:br)';sourceFacts='LOST_DURING_NORMALIZATION';textLlm='NOT_VISIBLE_AS_BREAKS';vlm='VISIBLE_TO_VLM_AS_LAYOUT';status='LOST_DURING_NORMALIZATION'},
    [ordered]@{signal='tabs';ooXml='NOT_PRESENT_IN_OOXML';sourceFacts='NOT_APPLICABLE';textLlm='NOT_VISIBLE';vlm='NOT_VISIBLE';status='NOT_PRESENT'},
    [ordered]@{signal='page_break_elements';ooXml='NOT_PRESENT_IN_DOMINANT_PARAGRAPH (0 lastRenderedPageBreak, 0 pageBreakBefore, 0 type=page w:br)';sourceFacts='NOT_PRESENT';textLlm='NOT_VISIBLE';vlm='NOT_VISIBLE_AS_EXPLICIT_ELEMENT';status='NOT_PRESENT'},
    [ordered]@{signal='fields_drawings_textboxes';ooXml='NOT_PRESENT_IN_DOMINANT_PARAGRAPH';sourceFacts='NOT_PRESENT';textLlm='NOT_VISIBLE';vlm='NOT_VISIBLE';status='NOT_PRESENT'}
)

$textScore=Read-Json (Join-Path $contractRoot 'score.v2.json'); $visualScore=Read-Json (Join-Path $visualRoot 'score.v2.json')
$table=[ordered]@{
    textV2=[ordered]@{exactTP=$textScore.tp;exactFP=$textScore.fp;exactFN=$textScore.fn;f1=$textScore.f1;semanticPresent=20;wrongSpan=20;trueOmission=51;systemBindingLoss=0;visualAlignmentLoss=0}
    vlm=[ordered]@{exactTP=$visualScore.tp;exactFP=$visualScore.fp;exactFN=$visualScore.fn;f1=$visualScore.f1;semanticPresent=($exactPresent+$wrongSpan+$wrongSource+$mappingFailure);wrongSpan=$wrongSpan;trueOmission=$trueOmission;systemBindingLoss=$visualScore.systemBindingLoss;visualAlignmentLoss=$mappingFailure}
}
$visualRootClassification = if ($mappingFailure -gt 0 -and $trueOmission -gt 0 -and $xmlFacts.breakElementCount -gt 0) { 'MIXED_REPRESENTATION_AND_MODEL_FAILURE' } elseif ($mappingFailure -gt 0) { 'VISUAL_TO_SOURCE_ALIGNMENT_FAILURE' } elseif ($xmlFacts.breakElementCount -gt 0) { 'SOURCE_REPRESENTATION_FLATTENING' } elseif ($trueOmission -gt 35) { 'VLM_TRUE_SEMANTIC_OMISSION' } else { 'UNRESOLVED' }

New-Item -ItemType Directory -Force -Path $Root | Out-Null
$audit=[ordered]@{
    schemaVersion='a99-doc0205-visual-semantic-presence-audit-v1';documentId='DOC-0205';offlineOnly=$true;providerCalls=0;startHead=$textFreeze.gitSha
    frozenIntegrity=[ordered]@{checks=$integrityChecks;sourceDocx=$sourcePath;strictGoldOccurrence=$goldPath;strictGoldV4=$strictGoldPath}
    sourceStructure=[ordered]@{sourceOccurrenceCount=$packet.occurrences.Count;goldSourceIdCount=@($gold.bindings.sourceId|Sort-Object -Unique).Count;goldOccurrencesPerSourceId=@($gold.bindings|Group-Object sourceId|ForEach-Object{[ordered]@{sourceId=$_.Name;count=$_.Count}});dominantOccurrence=[ordered]@{sourceId='body[1]/p[4]';styleName=$sourceById['body[1]/p[4]'].style.styleName;rawCharacterLength=$sourceById['body[1]/p[4]'].rawText.Length;numbering=$sourceById['body[1]/p[4]'].numbering;outline='NOT_PRESENT_IN_SOURCE_FACTS';paragraphRunStructure=$xmlFacts};ooxmlFacts=$xmlFacts}
    vlmPipeline=[ordered]@{rawVlmProposalCount=$rawCount;boundCount=$boundWindowCount;validatedCount=$validatedCount;projectedCount=$projectedCount;finalCount=$finalCount;rawToBoundLoss=$rawLossFirstStage;rawAliasMismatch=$rawAliasMismatch;rawInventory=$rawRows;cardinalityExplanation='Raw headings with semantic aliases are not source aliases; only 5 raw entries bind, 2 of those fail validation, and 3 project/freeze.'}
    exact=[ordered]@{tp=$visualScore.tp;fp=$visualScore.fp;fn=$visualScore.fn;f1=$visualScore.f1;trueModelOmission=$trueOmission;semanticMatchWrongSpan=$wrongSpan;partialSpan=$partialSpan;supersetSpan=$supersetSpan;wrongSource=$wrongSource;unresolved=$unresolved;arithmeticCheck=($exactPresent+$wrongSpan+$wrongSource+$mappingFailure+$trueOmission+$unresolved -eq 71);officialSpanErrorCount=$visualScore.lossCounts.MODEL_SPAN_ERROR;officialSpanErrorInterpretation='Not promoted to semantic span error unless a bound proposal demonstrably corresponds to the same Gold heading.'}
    semantic=[ordered]@{exactPresent=$exactPresent;wrongSpan=$wrongSpan;wrongSource=$wrongSource;mappingFailure=$mappingFailure;trueOmission=$trueOmission;unresolved=$unresolved;counts=$semanticCounts;goldCorrespondence=$goldRows}
    pageLevel=[ordered]@{pageCount=$pageManifest.pageCount;pageCoverage=$pageManifest.coverage;sourceMappingAvailable=$alignment.gatePass;goldRows=$goldRows}
    sourceRepresentation=[ordered]@{signals=$signalAudit;importantBoundaryConclusion='1512 manual line breaks and 1057 w:t node boundaries exist in OOXML but are not represented in the packet SourceFacts; no tabs, fields, drawings, textboxes, or explicit page-break elements occur in the dominant paragraph.'}
    textVsVlm=$table
    finalClassification=$visualRootClassification
    nextArchitecturalAction='Do not change runtime in this audit. If a new campaign is authorized, introduce a rich source IR that preserves w:t/run/br boundaries and page/layout anchors while retaining exact UTF-16 offsets, then rerun the same Contract V2 visual benchmark.'
    goldFirewall=[ordered]@{goldReadBeforeFreeze=$false;providerCallsDuringAudit=0;goldMutation=$false}
}
$map=[ordered]@{schemaVersion='a99-doc0205-visual-semantic-correspondence-v1';documentId='DOC-0205';offlineOnly=$true;goldCount=$goldRows.Count;rawVlmProposalCount=$rawCount;rows=$goldRows;classificationCounts=$semanticCounts;arithmeticCheck=($goldRows.Count -eq 71)}
$summary=[ordered]@{schemaVersion='a99-doc0205-visual-semantic-summary-v1';documentId='DOC-0205';startHead=$textFreeze.gitSha;sourceOccurrenceCount=$packet.occurrences.Count;dominantOccurrenceCharacters=$sourceById['body[1]/p[4]'].rawText.Length;ooxmlInternalBoundaries=$xmlFacts;vlmPipeline=[ordered]@{raw=$rawCount;bound=$boundWindowCount;validated=$validatedCount;projected=$projectedCount;final=$finalCount};exact=$audit.exact;semantic=$audit.semantic;textVsVlm=$table;lostSourceSignals=@($signalAudit|Where-Object status -eq 'LOST_DURING_NORMALIZATION');finalClassification=$visualRootClassification;nextArchitecturalAction=$audit.nextArchitecturalAction;modelCalls=0;goldReadBeforeFreeze=$false}
$audit | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $Root 'audit.v1.json') -Encoding UTF8
$map | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $Root 'gold-correspondence.v1.json') -Encoding UTF8
$summary | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $Root 'summary.v1.json') -Encoding UTF8
Write-Output ('RAW=' + $rawCount + ' BOUND=' + $boundWindowCount + ' VALIDATED=' + $validatedCount + ' PROJECTED=' + $projectedCount + ' FINAL=' + $finalCount)
Write-Output ('SEMANTIC=' + (($semanticCounts.GetEnumerator() | ForEach-Object { "$($_.Key):$($_.Value)" }) -join ';'))
Write-Output ('OOXML_BR=' + $xmlFacts.breakElementCount + ' XML_TEXT_NODES=' + $xmlFacts.textNodeCount + ' SOURCE_FACT_CHARS=' + $xmlFacts.sourceFactRawCharacters)
Write-Output ('FINAL_CLASSIFICATION=' + $visualRootClassification)
Write-Output ('ARTIFACT_ROOT=' + $Root)
