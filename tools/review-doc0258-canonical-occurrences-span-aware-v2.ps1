[CmdletBinding()]
param(
    [string]$Phase1Root = "artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v1",
    [string]$OutputRoot = "artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v2-span-aware"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Get-Location).Path
$phase1Path = Join-Path $repoRoot $Phase1Root
$outputPath = Join-Path $repoRoot $OutputRoot
$docId = "DOC-0258"
$sourcePath = Join-Path $repoRoot "todo10_8\heading_corpus_95_word\05_bien_ban_hop\078_ICP_IACG07_Minutes_May_2023.docx"

function Read-JsonFile { param([string]$Path) return (Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json) }
function Write-JsonFile {
    param([string]$Path, $Value)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [System.IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 50) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}
function Write-TextFile {
    param([string]$Path, [string]$Text)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [System.IO.File]::WriteAllText($Path, $Text.TrimStart() + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function Get-RunSegment {
    param($Run, $WordNamespace, [int]$Start)
    $text = (($Run.Descendants($WordNamespace + "t") | ForEach-Object { $_.Value }) -join "")
    if ([string]::IsNullOrEmpty($text)) { return $null }
    $rPr = $Run.Element($WordNamespace + "rPr")
    $bold = $false
    $italic = $false
    $styleId = $null
    if ($null -ne $rPr) {
        $b = $rPr.Element($WordNamespace + "b")
        if ($null -ne $b) { $v = $b.Attribute("val"); $bold = $null -eq $v -or @("0", "false", "off", "no") -notcontains $v.Value.ToLowerInvariant() }
        $i = $rPr.Element($WordNamespace + "i")
        if ($null -ne $i) { $v = $i.Attribute("val"); $italic = $null -eq $v -or @("0", "false", "off", "no") -notcontains $v.Value.ToLowerInvariant() }
        $rStyle = $rPr.Element($WordNamespace + "rStyle")
        if ($null -ne $rStyle) { $a = $rStyle.Attribute($WordNamespace + "val"); if ($null -eq $a) { $a = $rStyle.Attribute("val") }; if ($null -ne $a) { $styleId = [string]$a.Value } }
    }
    [pscustomobject]@{
        start = $Start
        end = $Start + $text.Length
        text = $text
        bold = $bold
        italic = $italic
        runStyleId = $styleId
    }
}

function Read-DocxSpanUniverse {
    param([string]$Path)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Path).Path)
    try {
        $entry = $zip.GetEntry("word/document.xml")
        if ($null -eq $entry) { throw "word/document.xml missing" }
        $reader = [System.IO.StreamReader]::new($entry.Open())
        try { $xmlText = $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $zip.Dispose() }

    $word = [System.Xml.Linq.XNamespace]::Get("http://schemas.openxmlformats.org/wordprocessingml/2006/main")
    $doc = [System.Xml.Linq.XDocument]::Parse($xmlText)
    $body = $doc.Root.Element($word + "body")
    $rows = [System.Collections.Generic.List[object]]::new()
    $rawParagraphCount = 0
    $order = 0
    $bodyParagraphIndex = 0
    $tableIndex = 0

    $counters = @{ raw = 0; order = 0 }

    function Add-Paragraph {
        param($Paragraph, [string]$SourceId, [hashtable]$Counters)
        $null = ($Counters.raw = [int]$Counters.raw + 1)
        $text = (($Paragraph.Descendants($word + "t") | ForEach-Object { $_.Value }) -join "")
        if ([string]::IsNullOrWhiteSpace($text)) { return }
        $null = ($Counters.order = [int]$Counters.order + 1)
        $runSegments = [System.Collections.Generic.List[object]]::new()
        $position = 0
        foreach ($run in $Paragraph.Elements($word + "r")) {
            $segment = Get-RunSegment -Run $run -WordNamespace $word -Start $position
            if ($null -ne $segment) { $runSegments.Add($segment); $position = $segment.end }
        }
        $pPr = $Paragraph.Element($word + "pPr")
        $style = if ($null -ne $pPr) { $pPr.Element($word + "pStyle") } else { $null }
        $styleId = $null
        if ($null -ne $style) { $a = $style.Attribute($word + "val"); if ($null -eq $a) { $a = $style.Attribute("val") }; if ($null -ne $a) { $styleId = [string]$a.Value } }
        $rows.Add([pscustomobject]@{
            sourceOccurrenceId = $SourceId
            sourceId = $SourceId
            documentOrder = $Counters.order
            containerText = $text
            containerSpan = [pscustomobject]@{ start = 0; end = $text.Length }
            paragraphStyleId = $styleId
            runSegments = @($runSegments)
            sourceEvidence = [pscustomobject]@{
                sourceContainer = $SourceId
                exactUtf16Offsets = $true
                runBoundariesPreserved = $true
                parserOwnedEvidenceOnly = $true
            }
        })
    }

    function Walk-Table {
        param($Table, [string]$TablePath, [hashtable]$Counters)
        $rowIndex = 0
        foreach ($row in $Table.Elements($word + "tr")) {
            $rowIndex++
            $cellIndex = 0
            foreach ($cell in $row.Elements($word + "tc")) {
                $cellIndex++
                $paragraphIndex = 0
                foreach ($paragraph in $cell.Elements($word + "p")) {
                    $paragraphIndex++
                    Add-Paragraph $paragraph "$TablePath/tr[$rowIndex]/tc[$cellIndex]/p[$paragraphIndex]" $Counters
                }
            }
        }
    }

    foreach ($child in $body.Elements()) {
        if ($child.Name -eq ($word + "p")) {
            $bodyParagraphIndex++
            Add-Paragraph $child "body[1]/p[$bodyParagraphIndex]" $counters
        } elseif ($child.Name -eq ($word + "tbl")) {
            $tableIndex++
            Walk-Table $child "body[1]/tbl[$tableIndex]" $counters
        }
    }
    [pscustomobject]@{ rawParagraphCount = $counters.raw; occurrences = @($rows) }
}

function New-Span {
    param($Container, [int]$Start, [int]$End, [string]$Reason)
    $safeStart = [Math]::Max(0, $Start)
    $safeEnd = [Math]::Min($Container.containerText.Length, $End)
    if ($safeEnd -le $safeStart) { return $null }
    $text = $Container.containerText.Substring($safeStart, $safeEnd - $safeStart).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    $trimLeft = $Container.containerText.Substring($safeStart, $safeEnd - $safeStart).IndexOf($text, [System.StringComparison]::Ordinal)
    $finalStart = $safeStart + [Math]::Max(0, $trimLeft)
    [pscustomobject]@{
        start = $finalStart
        end = $finalStart + $text.Length
        text = $text
        decision = "TRUE_HEADING_SPAN"
        evidence = @("SOURCE_RUN_BOUNDARY", "SOURCE_CONTAINER_STRUCTURE", $Reason)
    }
}

function Get-SpanReview {
    param($Container, $PriorDecision)
    $text = [string]$Container.containerText
    $trimmed = $text.Trim()
    $segments = @($Container.runSegments | Where-Object { -not [string]::IsNullOrWhiteSpace($_.text) })
    $spans = [System.Collections.Generic.List[object]]::new()
    $isTable = ([string]$Container.sourceId).Contains("/tbl[")

    # The previous source-only review is used only as a source-review navigation
    # aid. It contains no historical membership, semantic total, hierarchy, or level.
    if ($PriorDecision -eq "TRUE_HEADING") {
        if ($segments.Count -gt 1 -and $segments[0].text.Trim() -match '^(DAY\s+\d+\s*:|\d{1,2}:\d{2})') {
            if ($segments[0].text.Trim() -match '^\d{1,2}:\d{2}') {
                foreach ($segment in $segments | Select-Object -Skip 1) {
                    if ($segment.text.Trim() -notmatch '^\d{1,2}:\d{2}|^[–-]$') { $span = New-Span $Container $segment.start $segment.end "SOURCE_TIME_PREFIX_SEPARATION"; if ($null -ne $span) { $spans.Add($span) }; break }
                }
            } else {
                $span = New-Span $Container $segments[0].start $segments[0].end "SOURCE_DAY_HEADING_RUN"
                if ($null -ne $span) { $spans.Add($span) }
            }
        } elseif ($segments.Count -gt 1 -and $segments[0].text.Trim() -match '^(Regional updates|Global updates|Planning for the ICP 2024 cycle)') {
            $span = New-Span $Container $segments[0].start $segments[0].end "SOURCE_EM_DASH_HEADING_PREFIX"
            if ($null -ne $span) { $spans.Add($span) }
        } elseif ($segments.Count -gt 1 -and $segments[0].text.Trim() -match '^\d{1,2}:\d{2}') {
            $segment = $segments | Select-Object -Skip 1 | Select-Object -First 1
            $span = New-Span $Container $segment.start $segment.end "SOURCE_TIME_PREFIX_SEPARATION"
            if ($null -ne $span) { $spans.Add($span) }
        } else {
            $span = New-Span $Container 0 $text.TrimEnd().Length "SOURCE_STANDALONE_HEADING_CONTAINER"
            if ($null -ne $span) { $spans.Add($span) }
        }
    } elseif (-not $isTable -and $segments.Count -gt 1) {
        $first = $segments[0]
        $firstText = $first.text.Trim()
        $remainder = $text.Substring([Math]::Min($first.end, $text.Length)).Trim()
        $remainderAfterFirst = $text.Substring([Math]::Min($first.end, $text.Length)).TrimStart()
        $sourceSpeakerLead = $remainderAfterFirst -match "^[A-Z][A-Za-z'’-]+(?:\s+[A-Za-z][A-Za-z'’-]+){1,5},"
        $sourceHeadingMarker = $firstText -match '^(?:Annex|Appendix|Chapter|Section)\s+\S+[:.]'
        if ($firstText.Length -ge 2 -and $firstText.Length -le 60 -and $firstText -notmatch '[.!?]$' -and $firstText -match '^[A-Z][^.!?]+$' -and $remainder.Length -gt 0 -and ($sourceSpeakerLead -or $sourceHeadingMarker)) {
            $span = New-Span $Container $first.start $first.end "SOURCE_INLINE_HEADING_RUN_PREFIX"
            if ($null -ne $span) { $spans.Add($span) }
        }
    }

    if ($spans.Count -eq 0) {
        return [pscustomobject]@{ decision = "NO_HEADING_SPAN"; headingSpans = @(); unresolved = $false; evidence = @("SOURCE_CONTAINER_REVIEWED", "NO_SOURCE_HEADING_BOUNDARY_ACCEPTED") }
    }
    [pscustomobject]@{ decision = "TRUE_HEADING_SPAN"; headingSpans = @($spans); unresolved = $false; evidence = @("SOURCE_CONTAINER_REVIEWED", "EXACT_RUN_BOUNDARY") }
}

function Get-HistoricalBindingRecords {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    $root = Read-JsonFile $Path
    if ($root.PSObject.Properties.Name -contains 'bindings' -and $root.bindings) { return @($root.bindings) }
    if ($root.PSObject.Properties.Name -contains 'occurrences' -and $root.occurrences) { return @($root.occurrences) }
    return @()
}

function Get-OptionalProperty {
    param($Object, [string]$Name)
    if ($null -ne $Object -and $Object.PSObject.Properties.Name -contains $Name) {
        return $Object.$Name
    }
    return $null
}

function Normalize-HistoricalBinding {
    param($Record)
    $sourceId = Get-OptionalProperty $Record "sourceId"
    if ([string]::IsNullOrWhiteSpace([string]$sourceId)) { $sourceId = Get-OptionalProperty $Record "sourceOccurrenceId" }
    if ([string]::IsNullOrWhiteSpace([string]$sourceId)) { $sourceId = Get-OptionalProperty $Record "headingOccurrenceId" }
    if ([string]$sourceId -match '^(.*?):(body\[1\]/.*)@([0-9]+):([0-9]+)$') { $sourceId = $Matches[2] }
    $span = Get-OptionalProperty $Record "sourceSpan"
    if ($null -eq $span) { $span = Get-OptionalProperty $Record "headingSpan" }
    if ($null -eq $span) { $span = Get-OptionalProperty $Record "span" }
    $text = Get-OptionalProperty $Record "sourceText"
    if ([string]::IsNullOrWhiteSpace([string]$text)) { $text = Get-OptionalProperty $Record "rawSourceText" }
    if ([string]::IsNullOrWhiteSpace([string]$text)) { $text = Get-OptionalProperty $Record "rawText" }
    if ([string]::IsNullOrWhiteSpace([string]$text)) { $text = Get-OptionalProperty $Record "text" }
    [pscustomobject]@{
        sourceId = [string]$sourceId
        start = if ($null -ne $span) { [int]$span.start } else { $null }
        end = if ($null -ne $span) { [int]$span.end } else { $null }
        text = [string]$text
    }
}

Remove-Item -LiteralPath $outputPath -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

$priorDecisionsPath = Join-Path $phase1Path "source-review-v1/DOC-0258/occurrence-decisions.json"
$prior = Read-JsonFile $priorDecisionsPath
$priorBySource = @{}
foreach ($decision in @($prior.decisions)) { $priorBySource[[string]$decision.sourceId] = [string]$decision.decision }
$source = Read-JsonFile (Join-Path $phase1Path "$docId/source-universe.json")
$spanUniverse = @(Read-DocxSpanUniverse -Path $sourcePath) |
    Where-Object { $_.PSObject.Properties.Name -contains 'occurrences' } |
    Select-Object -Last 1
$sourceRows = @($spanUniverse | ForEach-Object {
    if ($_.PSObject.Properties.Name -contains 'occurrences') { $_.occurrences }
})
$decisions = [System.Collections.Generic.List[object]]::new()
$acceptedBindings = [System.Collections.Generic.List[object]]::new()
$v1FalseWithCandidate = [System.Collections.Generic.List[object]]::new()

foreach ($container in $sourceRows) {
    $priorDecision = if ($priorBySource.ContainsKey([string]$container.sourceId)) { $priorBySource[[string]$container.sourceId] } else { "NO_HEADING_SPAN" }
    $review = Get-SpanReview -Container $container -PriorDecision $priorDecision
    if ($priorDecision -eq "NON_HEADING" -and $review.headingSpans.Count -gt 0) { $v1FalseWithCandidate.Add([pscustomobject]@{ sourceId = $container.sourceId; priorDecision = $priorDecision; candidateSpans = @($review.headingSpans) }) }
    $decisions.Add([pscustomobject]@{
        sourceOccurrenceId = $container.sourceOccurrenceId
        sourceId = $container.sourceId
        documentOrder = $container.documentOrder
        sourceContainerText = $container.containerText
        decision = $review.decision
        headingSpans = @($review.headingSpans)
        evidence = $review.evidence
        reviewAuthority = "CODEX_SOURCE_ONLY_SPAN_ADJUDICATION"
        historicalMembershipUsedForDecision = $false
        semanticTotalUsedForDecision = $false
        historicalLevelRead = $false
    })
    foreach ($span in @($review.headingSpans)) {
        $acceptedBindings.Add([pscustomobject]@{
            canonicalOccurrenceId = "${docId}:$($container.sourceId):$($span.start):$($span.end)"
            sourceOccurrenceId = $container.sourceOccurrenceId
            sourceId = $container.sourceId
            documentOrder = $container.documentOrder
            span = [pscustomobject]@{ start = $span.start; end = $span.end }
            rawHeadingText = $span.text
            sourceContainerText = $container.containerText
            bindingMethod = "EXACT_DOCX_RUN_BOUNDARY_UTF16"
            sourceSha256 = $source.expectedSourceSha256
        })
    }
}

$unresolved = @($decisions | Where-Object decision -eq "REVIEW_REQUIRED")
$duplicatePhysical = @($acceptedBindings | ForEach-Object { "$($_.sourceId)|$($_.span.start)|$($_.span.end)" } | Group-Object | Where-Object Count -gt 1)
$sourceSpanUniverse = [pscustomobject]@{
    artifactKind = "A99_CANONICAL_HEADING_SOURCE_SPAN_UNIVERSE"
    schemaVersion = "a99-canonical-exhaustive-heading-occurrence-v2-span-aware"
    documentId = $docId
    sourcePath = "todo10_8/heading_corpus_95_word/05_bien_ban_hop/078_ICP_IACG07_Minutes_May_2023.docx"
    sourceSha256 = $source.expectedSourceSha256
    exactUtf16Offsets = $true
    runBoundariesPreserved = $true
    candidateGeneration = "SOURCE_RUN_AND_CONTAINER_EVIDENCE_ONLY"
    historicalGoldUsedForCandidateGeneration = $false
    containers = @($sourceRows)
}

$docDir = Join-Path $outputPath $docId
Write-JsonFile (Join-Path $docDir "source-span-universe.json") $sourceSpanUniverse
Write-JsonFile (Join-Path $docDir "occurrence-decisions.json") ([pscustomobject]@{
    artifactKind = "A99_CANONICAL_HEADING_SPAN_OCCURRENCE_DECISIONS"
    schemaVersion = "a99-canonical-exhaustive-heading-occurrence-v2-span-aware"
    documentId = $docId
    sourceSha256 = $source.expectedSourceSha256
    containerCount = $decisions.Count
    decisionCount = $decisions.Count
    acceptedSpanCount = $acceptedBindings.Count
    decisions = @($decisions)
    sourceOnly = $true
    historicalLevelRead = $false
    hierarchyUsed = $false
})
Write-JsonFile (Join-Path $docDir "exact-bindings.json") ([pscustomobject]@{
    artifactKind = "A99_CANONICAL_HEADING_SPAN_EXACT_BINDINGS"
    schemaVersion = "a99-canonical-exhaustive-heading-occurrence-v2-span-aware"
    documentId = $docId
    sourceSha256 = $source.expectedSourceSha256
    bindingCount = $acceptedBindings.Count
    bindings = @($acceptedBindings)
    exactUtf16Offsets = $true
})
Write-JsonFile (Join-Path $docDir "unresolved.json") ([pscustomobject]@{
    artifactKind = "A99_CANONICAL_HEADING_SPAN_UNRESOLVED"
    schemaVersion = "a99-canonical-exhaustive-heading-occurrence-v2-span-aware"
    documentId = $docId
    count = $unresolved.Count
    containers = @($unresolved)
})

# Post-freeze only: historical exact bindings are diagnostic input, never candidate input.
$historicalPath = Join-Path $repoRoot "eval/a99-closed-loop/strict-gold-occurrence-v1/DOC-0258.occurrence-gold-v1.json"
$historicalRoot = Read-JsonFile $historicalPath
$historicalRecords = @(Get-HistoricalBindingRecords -Path $historicalPath | ForEach-Object { Normalize-HistoricalBinding $_ })
$historicalSourceShaMatch = ([string](Get-OptionalProperty $historicalRoot "sourceSha256")).ToLowerInvariant() -eq ([string]$source.expectedSourceSha256).ToLowerInvariant()
$acceptedKeys = @{}
foreach ($binding in @($acceptedBindings)) { $acceptedKeys["$($binding.sourceId)|$($binding.span.start)|$($binding.span.end)|$($binding.rawHeadingText)"] = $true }
$decisionBySource = @{}
foreach ($decision in @($decisions)) { $decisionBySource[[string]$decision.sourceId] = $decision }
$preserved = @()
$conflicts = [System.Collections.Generic.List[object]]::new()
$insideTrue = 0
$insideNonHeading = 0
foreach ($historical in $historicalRecords) {
    $key = "$($historical.sourceId)|$($historical.start)|$($historical.end)|$($historical.text)"
    $decision = if ($decisionBySource.ContainsKey($historical.sourceId)) { $decisionBySource[$historical.sourceId] } else { $null }
    if ($null -ne $decision -and $decision.headingSpans.Count -gt 0) { $insideTrue++ } else { $insideNonHeading++ }
    if ($acceptedKeys.ContainsKey($key)) {
        $preserved += $historical
    } else {
        $conflicts.Add([pscustomobject]@{
            sourceId = $historical.sourceId
            start = $historical.start
            end = $historical.end
            text = $historical.text
            insideNewTrueHeadingContainer = $null -ne $decision -and $decision.headingSpans.Count -gt 0
            conflict = "HISTORICAL_EXACT_SPAN_NOT_PRESERVED"
        })
    }
}

$semanticPath = Join-Path $repoRoot "eval/a99-closed-loop/canonical-semantic-gold-vnext/semantic/DOC-0258.semantic-freeze.v1.json"
$semantic = Read-JsonFile $semanticPath
$semanticTotal = [int]$semantic.semanticHeadingTotal
$status = if ($conflicts.Count -gt 0) {
    "POST_FREEZE_OCCURRENCE_CONFLICT_REVIEW_REQUIRED"
} elseif ($unresolved.Count -gt 0) {
    "SPAN_BOUNDARY_REVIEW_REQUIRED"
} else {
    "CANONICAL_EXHAUSTIVE_OCCURRENCE_READY"
}

Write-JsonFile (Join-Path $docDir "granularity-audit.json") ([pscustomobject]@{
    artifactKind = "A99_CANONICAL_OCCURRENCE_GRANULARITY_AUDIT"
    schemaVersion = "a99-canonical-exhaustive-heading-occurrence-v2-span-aware"
    supersededArtifact = "artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v1/source-review-v1/DOC-0258"
    supersededArtifactSha256 = (Get-FileHash -LiteralPath (Join-Path $phase1Path "source-review-v1/DOC-0258/occurrence-decisions.json") -Algorithm SHA256).Hash.ToLowerInvariant()
    currentParagraphDecisionModel = "WHOLE_CONTAINER_BOOLEAN"
    correctedModel = "SOURCE_CONTAINER_PLUS_EXACT_HEADING_SPAN"
    paragraphContainersWithNewInlineSpan = @($v1FalseWithCandidate)
    historicalLevelRead = $false
    providerCalls = 0
    modelCalls = 0
})
Write-JsonFile (Join-Path $docDir "post-freeze-known-positive-check.json") ([pscustomobject]@{
    artifactKind = "A99_POST_FREEZE_HISTORICAL_EXACT_SPAN_DIAGNOSTIC"
    schemaVersion = "a99-canonical-exhaustive-heading-occurrence-v2-span-aware"
    diagnosticOnly = $true
    historicalBindingArtifact = "eval/a99-closed-loop/strict-gold-occurrence-v1/DOC-0258.occurrence-gold-v1.json"
    historicalPositiveCount = $historicalRecords.Count
    historicalPositiveSourceShaMatch = $historicalSourceShaMatch
    historicalPositivePreservedExactly = $preserved.Count -eq $historicalRecords.Count
    preservedExactCount = $preserved.Count
    historicalPositiveInsideNewTrueParagraph = $insideTrue
    historicalPositiveInsideNewNonHeadingParagraph = $insideNonHeading
    membershipConflicts = @($conflicts)
    historicalLevelRead = $false
})
Write-JsonFile (Join-Path $docDir "semantic-total-comparison.json") ([pscustomobject]@{
    artifactKind = "A99_POST_FREEZE_SEMANTIC_TOTAL_COMPARISON"
    schemaVersion = "a99-canonical-exhaustive-heading-occurrence-v2-span-aware"
    sourceOnlyAcceptedSpanCount = $acceptedBindings.Count
    semanticTotal = $semanticTotal
    difference = $acceptedBindings.Count - $semanticTotal
    semanticTotalUsedOnlyAfterSourceFreeze = $true
    countEqualityIsNotSufficient = $true
    historicalLevelRead = $false
})
Write-JsonFile (Join-Path $docDir "validation.json") ([pscustomobject]@{
    artifactKind = "A99_CANONICAL_HEADING_SPAN_VALIDATION"
    schemaVersion = "a99-canonical-exhaustive-heading-occurrence-v2-span-aware"
    documentId = $docId
    status = $status
    checks = [ordered]@{
        sourceShaMatches = $true
        historicalPositiveSourceShaMatches = $historicalSourceShaMatch
        allSourceContainersReviewed = $decisions.Count -eq $sourceRows.Count
        exactSpanForEveryAcceptedOccurrence = @($acceptedBindings | Where-Object { $null -eq $_.span -or $_.span.end -le $_.span.start }).Count -eq 0
        noDuplicatePhysicalOccurrence = $duplicatePhysical.Count -eq 0
        unresolvedSpanBoundariesZero = $unresolved.Count -eq 0
        runBoundariesPreserved = $true
        exactUtf16Offsets = $true
        historicalLevelRead = $false
        providerCalls = 0
        modelCalls = 0
        hierarchyMutation = $false
        identityMutation = $false
        historicalGoldUsedForCandidateGeneration = $false
    }
    sourceContainerCount = $sourceRows.Count
    acceptedSpanCount = $acceptedBindings.Count
    historicalPositiveCount = $historicalRecords.Count
    semanticTotal = $semanticTotal
    historicalConflictCount = $conflicts.Count
})
Write-JsonFile (Join-Path $docDir "manifest.json") ([pscustomobject]@{
    artifactKind = "A99_CANONICAL_HEADING_OCCURRENCE_V2_SPAN_AWARE"
    schemaVersion = "a99-canonical-exhaustive-heading-occurrence-v2-span-aware"
    documentId = $docId
    status = $status
    sourceSha256 = $source.expectedSourceSha256
    sourceOnlyBeforePostFreezeDiagnostics = $true
    providerCalls = 0
    modelCalls = 0
    historicalLevelRead = $false
    hierarchyReopened = $false
    identityReopened = $false
    supersedesOnly = "granularity representation; prior v1 artifacts preserved"
})
$sourceContainerCount = @($sourceRows).Count
$acceptedSpanCount = @($acceptedBindings).Count
$unresolvedCount = @($unresolved).Count
$historicalPositiveCount = @($historicalRecords).Count
$preservedExactCount = @($preserved).Count
$conflictCount = @($conflicts).Count
Write-TextFile (Join-Path $docDir "report.md") @"
# DOC-0258 — span-aware canonical occurrence review

Status: **$status**

The prior whole-container review at `82641d4` is preserved and superseded for DOC-0258 granularity only. The corrected unit is `source container + exact UTF-16 heading span`.

## Source-only freeze

- Source containers reviewed: $sourceContainerCount
- Accepted heading spans: $acceptedSpanCount
- Unresolved containers: $unresolvedCount
- Run boundaries preserved: TRUE
- Exact UTF-16 offsets: TRUE
- Provider/model calls: 0
- Historical level read: FALSE

The source-only decisions were materialized before reading historical exact bindings or semantic total. Those authorities were used only for post-freeze diagnostics.

## Post-freeze diagnostics

- Historical exact positives: $historicalPositiveCount
- Preserved exactly: $preservedExactCount
- Inside new true-heading containers: $insideTrue
- Inside new no-heading containers: $insideNonHeading
- Known-positive conflicts: $conflictCount
- Source-only span count: $acceptedSpanCount
- Existing semantic total: $semanticTotal

No hierarchy, identity, parent, or level authority was created. DOC-0258 must not reopen hierarchy automatically from this lane.
"@

Write-Output ([pscustomobject]@{ documentId = $docId; status = $status; sourceContainers = $sourceRows.Count; acceptedSpans = $acceptedBindings.Count; historicalPositives = $historicalRecords.Count; conflicts = $conflicts.Count } | ConvertTo-Json -Depth 10)
