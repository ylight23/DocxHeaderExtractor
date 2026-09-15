[CmdletBinding()]
param(
    [string]$OutputRoot = "artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v1/DOC-0252"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Get-Location).Path
$documentId = "DOC-0252"
$sourcePath = "todo10_8/generated-docx/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.docx"
$expectedSourceSha256 = "4dda3c8ec8cd74e3a61503db0f8e9f168270d39036e3825441ab6167f9e16a77"
$sourceFullPath = Join-Path $repoRoot ($sourcePath -replace '/', '\')
$outputPath = Join-Path $repoRoot ($OutputRoot -replace '/', '\')

function Write-JsonFile {
    param([string]$Path, $Value)
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $json = $Value | ConvertTo-Json -Depth 60
    [IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Write-TextFile {
    param([string]$Path, [string]$Text)
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    [IO.File]::WriteAllText($Path, $Text.TrimStart() + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Get-TextSha256 {
    param([string]$Text)
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text)
    return ([Security.Cryptography.SHA256]::Create().ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') }) -join ''
}

function Normalize-HeadingText {
    param([string]$Text)
    return (($Text -replace '\s+', ' ').Trim())
}

function Normalize-BridgeText {
    param([string]$Text)
    return (Normalize-HeadingText $Text).Replace([char]0x2013, '-').Replace([char]0x2014, '-').Replace([char]0x2012, '-')
}

function Get-SourceParagraphRecord {
    param(
        [Parameter(Mandatory = $true)] $Paragraph,
        [Parameter(Mandatory = $true)] [string]$SourceId,
        [Parameter(Mandatory = $true)] [int]$DocumentOrder,
        [Parameter(Mandatory = $true)] $WordNamespace,
        [Parameter(Mandatory = $true)] [string]$ContainerKind
    )

    $text = (($Paragraph.Descendants($WordNamespace + 't') | ForEach-Object { $_.Value }) -join '')
    $pPr = $Paragraph.Element($WordNamespace + 'pPr')
    $styleValue = $null
    $outlineLevel = $null
    $alignment = $null
    if ($null -ne $pPr) {
        $style = $pPr.Element($WordNamespace + 'pStyle')
        if ($null -ne $style -and $null -ne $style.Attribute($WordNamespace + 'val')) { $styleValue = [string]$style.Attribute($WordNamespace + 'val').Value }
        $outline = $pPr.Element($WordNamespace + 'outlineLvl')
        if ($null -ne $outline -and $null -ne $outline.Attribute($WordNamespace + 'val')) { $outlineLevel = [string]$outline.Attribute($WordNamespace + 'val').Value }
        $jc = $pPr.Element($WordNamespace + 'jc')
        if ($null -ne $jc -and $null -ne $jc.Attribute($WordNamespace + 'val')) { $alignment = [string]$jc.Attribute($WordNamespace + 'val').Value }
    }

    $bold = $false
    $italic = $false
    $underline = $false
    $fontSizes = [Collections.Generic.List[string]]::new()
    foreach ($run in $Paragraph.Descendants($WordNamespace + 'r')) {
        $rPr = $run.Element($WordNamespace + 'rPr')
        if ($null -eq $rPr) { continue }
        if ($null -ne $rPr.Element($WordNamespace + 'b')) { $bold = $true }
        if ($null -ne $rPr.Element($WordNamespace + 'i')) { $italic = $true }
        if ($null -ne $rPr.Element($WordNamespace + 'u')) { $underline = $true }
        $sz = $rPr.Element($WordNamespace + 'sz')
        if ($null -ne $sz -and $null -ne $sz.Attribute($WordNamespace + 'val')) { $fontSizes.Add([string]$sz.Attribute($WordNamespace + 'val').Value) }
    }

    [pscustomobject]@{
        sourceOccurrenceId = $SourceId
        sourceId = $SourceId
        documentOrder = $DocumentOrder
        containerKind = $ContainerKind
        rawText = $text
        sourceSpan = [pscustomobject]@{ start = 0; end = $text.Length }
        sourceEvidence = [pscustomobject]@{
            paragraphStyleId = $styleValue
            outlineLevel = $outlineLevel
            alignment = $alignment
            boldObserved = $bold
            italicObserved = $italic
            underlineObserved = $underline
            fontSizeHalfPointsObserved = @($fontSizes | Select-Object -Unique)
            parserOwnedEvidenceOnly = $true
        }
    }
}

function Read-DocxSourceContainers {
    param([Parameter(Mandatory = $true)] [string]$Path)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    $zip = [IO.Compression.ZipFile]::OpenRead($resolved)
    try {
        $entry = $zip.GetEntry('word/document.xml')
        if ($null -eq $entry) { throw "word/document.xml not found: $Path" }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $xmlText = $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $zip.Dispose() }

    $word = [Xml.Linq.XNamespace]::Get('http://schemas.openxmlformats.org/wordprocessingml/2006/main')
    $document = [Xml.Linq.XDocument]::Parse($xmlText)
    $body = $document.Root.Element($word + 'body')
    if ($null -eq $body) { throw "document body not found: $Path" }

    $records = [Collections.Generic.List[object]]::new()
    $script:rawParagraphCount = 0
    $script:nonEmptySourceContainerCount = 0
    $script:documentOrder = 0

    function Add-ParagraphNode {
        param($Paragraph, [string]$SourceId, [string]$Kind)
        $script:rawParagraphCount++
        $text = (($Paragraph.Descendants($word + 't') | ForEach-Object { $_.Value }) -join '')
        if ([string]::IsNullOrWhiteSpace($text)) { return }
        $script:documentOrder++
        $script:nonEmptySourceContainerCount++
        $records.Add((Get-SourceParagraphRecord -Paragraph $Paragraph -SourceId $SourceId -DocumentOrder $script:documentOrder -WordNamespace $word -ContainerKind $Kind))
    }

    function Walk-Table {
        param($Table, [string]$TablePath)
        $rowIndex = 0
        foreach ($row in $Table.Elements($word + 'tr')) {
            $rowIndex++
            $cellIndex = 0
            foreach ($cell in $row.Elements($word + 'tc')) {
                $cellIndex++
                $paragraphIndex = 0
                foreach ($paragraph in $cell.Elements($word + 'p')) {
                    $paragraphIndex++
                    Add-ParagraphNode -Paragraph $paragraph -SourceId ("$TablePath/tr[$rowIndex]/tc[$cellIndex]/p[$paragraphIndex]") -Kind 'tableCell'
                }
                $nestedIndex = 0
                foreach ($nested in $cell.Elements($word + 'tbl')) {
                    $nestedIndex++
                    Walk-Table -Table $nested -TablePath ("$TablePath/tr[$rowIndex]/tc[$cellIndex]/tbl[$nestedIndex]")
                }
            }
        }
    }

    $bodyParagraphIndex = 0
    $tableIndex = 0
    foreach ($child in $body.Elements()) {
        if ($child.Name -eq ($word + 'p')) {
            $bodyParagraphIndex++
            Add-ParagraphNode -Paragraph $child -SourceId "body[1]/p[$bodyParagraphIndex]" -Kind 'bodyParagraph'
        } elseif ($child.Name -eq ($word + 'tbl')) {
            $tableIndex++
            Walk-Table -Table $child -TablePath "body[1]/tbl[$tableIndex]"
        }
    }

    return [pscustomobject]@{
        rawParagraphCount = $script:rawParagraphCount
        nonEmptySourceContainerCount = $script:nonEmptySourceContainerCount
        containers = @($records)
    }
}

if (-not (Test-Path -LiteralPath $sourceFullPath)) { throw "Canonical source missing: $sourceFullPath" }
$actualSourceSha256 = (Get-FileHash -LiteralPath $sourceFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualSourceSha256 -ne $expectedSourceSha256) { throw "Canonical source SHA mismatch: $actualSourceSha256" }
$sourceData = Read-DocxSourceContainers -Path $sourceFullPath
$containers = @($sourceData.containers)

# Source-only review decisions. This list was created from the current DOCX text, run spans,
# typography/layout evidence, and surrounding prose. Historical rows and semantic totals are
# not read until after these decisions and exact bindings have been materialized.
$acceptedSpecs = @(
    @{ sourceId = 'body[1]/p[7]';   text = 'Session I: Welcome and meeting objectives'; kind = 'NARRATIVE_SECTION'; evidence = @('SOURCE_STANDALONE_HEADING_CONTAINER', 'SOURCE_RUN_BOUNDARY') }
    @{ sourceId = 'body[1]/p[11]';  text = 'Session II: Update on the ICP 2021 Cycle'; kind = 'NARRATIVE_SECTION'; evidence = @('SOURCE_STANDALONE_HEADING_CONTAINER', 'SOURCE_RUN_BOUNDARY') }
    @{ sourceId = 'body[1]/p[12]';  text = '1.Global office update'; kind = 'NARRATIVE_SUBHEADING'; evidence = @('SOURCE_NUMBERED_HEADING', 'SOURCE_RUN_BOUNDARY', 'SOURCE_FOLLOWING_PROSE') }
    @{ sourceId = 'body[1]/p[22]';  text = '2.Regional updates'; kind = 'NARRATIVE_SUBHEADING'; evidence = @('SOURCE_NUMBERED_HEADING', 'SOURCE_RUN_BOUNDARY', 'SOURCE_FOLLOWING_PROSE') }
    @{ sourceId = 'body[1]/p[24]';  text = 'Africa'; kind = 'NARRATIVE_SUBHEADING'; evidence = @('SOURCE_STANDALONE_HEADING_CONTAINER', 'SOURCE_FOLLOWING_PROSE') }
    @{ sourceId = 'body[1]/p[27]';  text = 'Asia and the Pacific'; kind = 'NARRATIVE_SUBHEADING'; evidence = @('SOURCE_STANDALONE_HEADING_CONTAINER', 'SOURCE_FOLLOWING_PROSE') }
    @{ sourceId = 'body[1]/p[31]';  text = 'Commonwealth of Independent States (CIS)'; kind = 'NARRATIVE_SUBHEADING'; evidence = @('SOURCE_STANDALONE_HEADING_CONTAINER', 'SOURCE_FOLLOWING_PROSE') }
    @{ sourceId = 'body[1]/p[39]';  text = 'Eurostat–OECD PPP Program'; kind = 'NARRATIVE_SUBHEADING'; evidence = @('SOURCE_STANDALONE_HEADING_CONTAINER', 'SOURCE_FOLLOWING_PROSE') }
    @{ sourceId = 'body[1]/p[42]';  text = 'Latin America and the Caribbean'; kind = 'NARRATIVE_SUBHEADING'; evidence = @('SOURCE_STANDALONE_HEADING_CONTAINER', 'SOURCE_FOLLOWING_PROSE') }
    @{ sourceId = 'body[1]/p[46]';  text = 'Western Asia'; kind = 'NARRATIVE_SUBHEADING'; evidence = @('SOURCE_STANDALONE_HEADING_CONTAINER', 'SOURCE_FOLLOWING_PROSE') }
    @{ sourceId = 'body[1]/p[54]';  text = 'Session III: Short- and Long-Term Research and Development Agenda'; kind = 'NARRATIVE_SECTION'; evidence = @('SOURCE_STANDALONE_HEADING_CONTAINER', 'SOURCE_RUN_BOUNDARY') }
    @{ sourceId = 'body[1]/p[55]';  text = '1.Research Topics Emerging from the Previous ICP Cycles'; kind = 'NARRATIVE_SUBHEADING'; evidence = @('SOURCE_NUMBERED_HEADING', 'SOURCE_RUN_BOUNDARY', 'SOURCE_FOLLOWING_PROSE') }
    @{ sourceId = 'body[1]/p[62]';  text = '2.Forthcoming Research Topics'; kind = 'NARRATIVE_SUBHEADING'; evidence = @('SOURCE_NUMBERED_HEADING', 'SOURCE_RUN_BOUNDARY', 'SOURCE_FOLLOWING_PROSE') }
    @{ sourceId = 'body[1]/p[75]';  text = 'Session IV: TAG Functioning and Terms of Reference for Task Forces'; kind = 'NARRATIVE_SECTION'; evidence = @('SOURCE_STANDALONE_HEADING_CONTAINER', 'SOURCE_RUN_BOUNDARY') }
    @{ sourceId = 'body[1]/p[76]';  text = '1.TAG Composition and Terms of Reference'; kind = 'NARRATIVE_SUBHEADING'; evidence = @('SOURCE_NUMBERED_HEADING', 'SOURCE_RUN_BOUNDARY', 'SOURCE_FOLLOWING_PROSE') }
    @{ sourceId = 'body[1]/p[80]';  text = '2.Terms of Reference for a Task Force on Annual PPP Production'; kind = 'NARRATIVE_SUBHEADING'; evidence = @('SOURCE_NUMBERED_HEADING', 'SOURCE_RUN_BOUNDARY', 'SOURCE_FOLLOWING_PROSE') }
    @{ sourceId = 'body[1]/p[89]';  text = '3.Terms of Reference for a Task Force on ICP Classification Update'; kind = 'NARRATIVE_SUBHEADING'; evidence = @('SOURCE_NUMBERED_HEADING', 'SOURCE_RUN_BOUNDARY', 'SOURCE_FOLLOWING_PROSE') }
    @{ sourceId = 'body[1]/p[94]';  text = 'Session V: Current Research'; kind = 'NARRATIVE_SECTION'; evidence = @('SOURCE_STANDALONE_HEADING_CONTAINER', 'SOURCE_RUN_BOUNDARY') }
    @{ sourceId = 'body[1]/p[95]';  text = '1.The Treatment of Import and Export Prices in International Comparisons'; kind = 'NARRATIVE_SUBHEADING'; evidence = @('SOURCE_NUMBERED_HEADING', 'SOURCE_RUN_BOUNDARY', 'SOURCE_FOLLOWING_PROSE') }
    @{ sourceId = 'body[1]/p[105]'; text = '2.A Survey Based Approach to Adjustment for Quality Differences in Services in International Price Comparisons'; kind = 'NARRATIVE_SUBHEADING'; evidence = @('SOURCE_NUMBERED_HEADING', 'SOURCE_RUN_BOUNDARY', 'SOURCE_FOLLOWING_PROSE') }
    @{ sourceId = 'body[1]/p[110]'; text = '3.Treatment of Scanner (Transaction) Data in the European Comparison Programme (ECP)'; kind = 'NARRATIVE_SUBHEADING'; evidence = @('SOURCE_NUMBERED_HEADING', 'SOURCE_RUN_BOUNDARY', 'SOURCE_FOLLOWING_PROSE') }
    @{ sourceId = 'body[1]/p[120]'; text = '4.Treatment of Negative Expenditures in ICP'; kind = 'NARRATIVE_SUBHEADING'; evidence = @('SOURCE_NUMBERED_HEADING', 'SOURCE_RUN_BOUNDARY', 'SOURCE_FOLLOWING_PROSE') }
    @{ sourceId = 'body[1]/p[125]'; text = '5.Fine Tuning Global Linking Procedures'; kind = 'NARRATIVE_SUBHEADING'; evidence = @('SOURCE_NUMBERED_HEADING', 'SOURCE_RUN_BOUNDARY', 'SOURCE_FOLLOWING_PROSE') }
    @{ sourceId = 'body[1]/p[142]'; text = '6.Improving Reliability of Price Comparisons'; kind = 'NARRATIVE_SUBHEADING'; evidence = @('SOURCE_NUMBERED_HEADING', 'SOURCE_RUN_BOUNDARY', 'SOURCE_FOLLOWING_PROSE') }
    @{ sourceId = 'body[1]/p[151]'; text = 'Session VI: Any Other Business'; kind = 'NARRATIVE_SECTION'; evidence = @('SOURCE_STANDALONE_HEADING_CONTAINER', 'SOURCE_RUN_BOUNDARY') }
    @{ sourceId = 'body[1]/p[158]'; text = 'Agenda'; kind = 'AGENDA_SECTION'; evidence = @('SOURCE_AGENDA_SECTION_LABEL', 'SOURCE_RUN_BOUNDARY') }
    @{ sourceId = 'body[1]/p[158]'; text = 'DAY 1: Friday, March 7, 2025 | Chair: Paul SCHREYER'; kind = 'AGENDA_DAY'; evidence = @('SOURCE_AGENDA_DAY_BANNER', 'SOURCE_RUN_BOUNDARY', 'SOURCE_LAYOUT_BANNER') }
    @{ sourceId = 'body[1]/p[158]'; text = 'SESSION I: Welcome and Opening Remarks'; kind = 'AGENDA_SESSION'; evidence = @('SOURCE_AGENDA_SESSION_LABEL', 'SOURCE_RUN_BOUNDARY') }
    @{ sourceId = 'body[1]/p[158]'; text = 'SESSION II: Update on the ICP 2021 and 2024 Cycles'; kind = 'AGENDA_SESSION'; evidence = @('SOURCE_AGENDA_SESSION_LABEL', 'SOURCE_RUN_BOUNDARY') }
    @{ sourceId = 'body[1]/p[159]'; text = 'SESSION III: Short- and Long-Term Research and Development Agenda'; kind = 'AGENDA_SESSION'; evidence = @('SOURCE_AGENDA_SESSION_LABEL', 'SOURCE_RUN_BOUNDARY') }
    @{ sourceId = 'body[1]/p[160]'; text = 'SESSION IV: TAG Functioning and Terms of Refence for Task Forces'; kind = 'AGENDA_SESSION'; evidence = @('SOURCE_AGENDA_SESSION_LABEL', 'SOURCE_RUN_BOUNDARY') }
    @{ sourceId = 'body[1]/p[166]'; text = 'SESSION V: Current Research'; kind = 'AGENDA_SESSION'; evidence = @('SOURCE_AGENDA_SESSION_LABEL', 'SOURCE_RUN_BOUNDARY') }
    @{ sourceId = 'body[1]/p[167]'; text = 'DAY 2: Saturday, March 8, 2025 | Chair: Prasada RAO'; kind = 'AGENDA_DAY'; evidence = @('SOURCE_AGENDA_DAY_BANNER', 'SOURCE_RUN_BOUNDARY', 'SOURCE_LAYOUT_BANNER') }
    @{ sourceId = 'body[1]/p[168]'; text = "SESSION V: Current Research (Cont$([char]0x2019)d)"; kind = 'AGENDA_SESSION'; evidence = @('SOURCE_AGENDA_SESSION_LABEL', 'SOURCE_RUN_BOUNDARY', 'SOURCE_CONTINUATION_MARKER') }
    @{ sourceId = 'body[1]/p[169]'; text = 'SESSION VI: Closing'; kind = 'AGENDA_SESSION'; evidence = @('SOURCE_AGENDA_SESSION_LABEL', 'SOURCE_RUN_BOUNDARY') }
    @{ sourceId = 'body[1]/p[174]'; text = 'Annex 2: List of Participants'; kind = 'ANNEX_SECTION'; evidence = @('SOURCE_STANDALONE_HEADING_CONTAINER', 'SOURCE_RUN_BOUNDARY') }
    @{ sourceId = 'body[1]/p[175]'; text = 'ICP Technical Advisory Group members'; kind = 'ANNEX_SUBHEADING'; evidence = @('SOURCE_UNDERLINED_SECTION_LABEL', 'SOURCE_FOLLOWING_LIST') }
    @{ sourceId = 'body[1]/p[176]'; text = 'ICP experts, guest speakers, and observers'; kind = 'ANNEX_SUBHEADING'; evidence = @('SOURCE_UNDERLINED_SECTION_LABEL', 'SOURCE_FOLLOWING_LIST') }
    @{ sourceId = 'body[1]/p[177]'; text = 'ICP Inter-Agency Coordination Group (IACG)'; kind = 'ANNEX_SUBHEADING'; evidence = @('SOURCE_UNDERLINED_SECTION_LABEL', 'SOURCE_FOLLOWING_LIST') }
    @{ sourceId = 'body[1]/p[178]'; text = 'World Bank'; kind = 'ANNEX_SUBHEADING'; evidence = @('SOURCE_UNDERLINED_SECTION_LABEL', 'SOURCE_FOLLOWING_LIST') }
)

$containersBySourceId = @{}
foreach ($container in $containers) { $containersBySourceId[$container.sourceId] = $container }
$specsBySourceId = @{}
foreach ($spec in $acceptedSpecs) {
    if (-not $specsBySourceId.ContainsKey($spec.sourceId)) { $specsBySourceId[$spec.sourceId] = [Collections.Generic.List[object]]::new() }
    $specsBySourceId[$spec.sourceId].Add($spec)
}

$decisions = [Collections.Generic.List[object]]::new()
$bindings = [Collections.Generic.List[object]]::new()
$spanErrors = [Collections.Generic.List[object]]::new()
$occurrenceOrdinal = 0
foreach ($container in $containers) {
    $specs = if ($specsBySourceId.ContainsKey($container.sourceId)) { @($specsBySourceId[$container.sourceId].ToArray()) } else { @() }
    $specCount = @($specs).Length
    $spans = [Collections.Generic.List[object]]::new()
    if ($specCount -gt 0) {
        foreach ($spec in $specs) {
            $headingText = [string]$spec.text
            $start = $container.rawText.IndexOf($headingText, [StringComparison]::Ordinal)
            if ($start -lt 0) {
                $spanErrors.Add([pscustomobject]@{ sourceId = $container.sourceId; text = $headingText; reason = 'EXACT_SOURCE_TEXT_NOT_FOUND' })
                continue
            }
            $end = $start + $headingText.Length
            $occurrenceOrdinal++
            $occurrenceId = "$documentId`:$($container.sourceId)#heading-span-$start-$end"
            $spans.Add([pscustomobject]@{
                occurrenceId = $occurrenceId
                sourceId = $container.sourceId
                start = $start
                end = $end
                exactText = $headingText
                headingKind = [string]$spec.kind
                evidence = @($spec.evidence)
                decision = 'TRUE_HEADING_SPAN'
            })
            $bindings.Add([pscustomobject]@{
                occurrenceId = $occurrenceId
                documentId = $documentId
                sourceOccurrenceId = $container.sourceOccurrenceId
                sourceId = $container.sourceId
                sourceSpan = [pscustomobject]@{ start = $start; end = $end }
                exactText = $headingText
                documentOrder = $container.documentOrder
                occurrenceOrder = $occurrenceOrdinal
                headingKind = [string]$spec.kind
                sourceContainerText = $container.rawText
                sourceContainerKind = $container.containerKind
                sourceEvidence = $container.sourceEvidence
                bindingAuthority = 'CURRENT_DOCX_SOURCE_ONLY'
                reviewProvenance = 'CODEX_SOURCE_ONLY_OCCURRENCE_REVIEW'
            })
        }
    }

    $evidence = [Collections.Generic.List[string]]::new()
    $evidence.Add('SOURCE_CONTAINER_REVIEWED')
    if ($spans.Count -gt 0) { $evidence.Add('ONE_OR_MORE_EXACT_HEADING_SPANS_ACCEPTED') }
    elseif ($container.sourceId -match 'body\[1\]/p\[(3|4|5|6|157)\]') { $evidence.Add('DOCUMENT_OR_EMBEDDED_AGENDA_METADATA') }
    elseif ($container.sourceId -match 'body\[1\]/p\[(158|160|164|165|170)\]') { $evidence.Add('AGENDA_SCHEDULE_ITEM_OR_METADATA_NOT_HEADING') }
    elseif ($container.rawText -match '^\s*\d+\s*$') { $evidence.Add('PAGE_NUMBER_OR_LIST_MARKER') }
    elseif ($container.rawText -match '^[0-9]+\s*$') { $evidence.Add('NUMERIC_SOURCE_FRAGMENT') }
    elseif ($container.sourceId -match 'body\[1\]/p\[(175|176|177|178)\]') { $evidence.Add('ANNEX_LABEL_REVIEWED_AS_NON_HEADING') }
    elseif ($container.sourceId -match 'tbl\[') { $evidence.Add('TABLE_DATA_OR_NON_HEADING_ROW') }
    else { $evidence.Add('PROSE_OR_NON_HEADING_SOURCE_CONTAINER') }

    $decisions.Add([pscustomobject]@{
        sourceOccurrenceId = $container.sourceOccurrenceId
        sourceId = $container.sourceId
        documentOrder = $container.documentOrder
        sourceContainerText = $container.rawText
        decision = if ($spans.Count -gt 0) { 'TRUE_HEADING_SPAN' } else { 'NO_HEADING_SPAN' }
        headingSpans = @($spans)
        evidence = @($evidence)
        reviewAuthority = 'CODEX_SOURCE_ONLY_OCCURRENCE_REVIEW'
        historicalOccurrenceRowsUsedForDecision = $false
        historicalLevelRead = $false
        historicalParentRead = $false
        oldSemanticTotalUsedForDecision = $false
    })
}

$accepted = @($bindings | Sort-Object documentOrder, sourceSpan.start)
$duplicateOccurrenceIds = @($accepted | Group-Object occurrenceId | Where-Object Count -gt 1 | ForEach-Object Name)
$duplicateSourceRefs = @($accepted | Group-Object { "$($_.sourceId):$($_.sourceSpan.start)-$($_.sourceSpan.end)" } | Where-Object Count -gt 1 | ForEach-Object Name)
$overlapErrors = [Collections.Generic.List[object]]::new()
foreach ($group in @($accepted | Group-Object sourceId)) {
    $ordered = @($group.Group | Sort-Object { [int]$_.sourceSpan.start })
    for ($i = 1; $i -lt $ordered.Count; $i++) {
        if ([int]$ordered[$i].sourceSpan.start -lt [int]$ordered[$i - 1].sourceSpan.end) {
            $overlapErrors.Add([pscustomobject]@{ sourceId = $group.Name; left = $ordered[$i - 1].occurrenceId; right = $ordered[$i].occurrenceId })
        }
    }
}
$orderedOccurrences = (@($accepted | ForEach-Object occurrenceId) -join '|')
$rebuildHash = Get-TextSha256 (($accepted | ConvertTo-Json -Depth 30 -Compress) + '|' + ($decisions | ConvertTo-Json -Depth 30 -Compress))

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

$sourceAuthority = [ordered]@{
    artifactKind = 'A99_CANONICAL_EXHAUSTIVE_HEADING_SOURCE_AUTHORITY'
    schemaVersion = 'a99-canonical-exhaustive-heading-occurrence-v1-doc0252'
    documentId = $documentId
    sourcePath = $sourcePath
    sourceFormat = 'DOCX'
    sourceSha256 = $actualSourceSha256
    sourceLineage = 'CURRENT_GENERATED_DOCX_REPO_SOURCE_AUTHORITY'
    representationType = 'HYBRID'
    sourceReviewAuthority = 'CODEX_SOURCE_ONLY_OCCURRENCE_REVIEW'
    sourceTruthScope = 'TRUE_HEADING_OCCURRENCE_ONLY'
    rawParagraphCount = $sourceData.rawParagraphCount
    nonEmptySourceContainerCount = $sourceData.nonEmptySourceContainerCount
    acceptedCanonicalOccurrenceCount = $accepted.Count
    historicalSourceLineageKnownDifferent = $true
    historicalLevelRead = $false
    historicalParentRead = $false
    providerCalls = 0
    modelCalls = 0
    semanticIdentityAssigned = $false
    hierarchyAssigned = $false
    levelAssigned = $false
}
Write-JsonFile (Join-Path $outputPath 'source-authority.json') $sourceAuthority

Write-JsonFile (Join-Path $outputPath 'source-containers.json') ([ordered]@{
    artifactKind = 'A99_CANONICAL_EXHAUSTIVE_HEADING_SOURCE_CONTAINERS'
    schemaVersion = 'a99-canonical-exhaustive-heading-occurrence-v1-doc0252'
    documentId = $documentId
    sourceSha256 = $actualSourceSha256
    containerCount = $containers.Count
    rawParagraphCount = $sourceData.rawParagraphCount
    nonEmptySourceContainerCount = $sourceData.nonEmptySourceContainerCount
    containers = $containers
})

Write-JsonFile (Join-Path $outputPath 'occurrence-decisions.json') ([ordered]@{
    artifactKind = 'A99_CANONICAL_EXHAUSTIVE_HEADING_OCCURRENCE_DECISIONS'
    schemaVersion = 'a99-canonical-exhaustive-heading-occurrence-v1-doc0252'
    documentId = $documentId
    sourceSha256 = $actualSourceSha256
    containerCount = $containers.Count
    decisionCount = $decisions.Count
    acceptedSpanCount = $accepted.Count
    decisions = $decisions
    sourceOnly = $true
    historicalOccurrenceRowsUsedForDecision = $false
    historicalLevelRead = $false
    historicalParentRead = $false
    oldSemanticTotalUsedForDecision = $false
})

Write-JsonFile (Join-Path $outputPath 'exact-bindings.json') ([ordered]@{
    artifactKind = 'A99_CANONICAL_EXHAUSTIVE_HEADING_OCCURRENCE_EXACT_BINDINGS'
    schemaVersion = 'a99-canonical-exhaustive-heading-occurrence-v1-doc0252'
    documentId = $documentId
    authority = 'CANONICAL_EXHAUSTIVE_OCCURRENCE_READY'
    sourceSha256 = $actualSourceSha256
    bindingUnit = 'SOURCE_CONTAINER_PLUS_EXACT_HEADING_SPAN'
    acceptedCanonicalBindings = $accepted
    semanticNodeAssignments = $false
    parentAssignments = $false
    levelAssignments = $false
})

# Historical bridge is intentionally opened only after current-source decisions and bindings
# have been materialized above. Only occurrence text/source identifiers are read; historical
# level and parent fields are never accessed.
$strictPath = Join-Path $repoRoot 'eval/a99-closed-loop/strict-gold-v4/DOC-0252.strict-gold-v4.json'
$historicalRows = [Collections.Generic.List[object]]::new()
$historicalSourceSha = $null
if (Test-Path -LiteralPath $strictPath) {
    $strict = Get-Content -Raw -LiteralPath $strictPath | ConvertFrom-Json
    $historicalSourceSha = [string]$strict.sourceSha256
    foreach ($row in @($strict.headings)) {
        $exact = @($accepted | Where-Object { (Normalize-HeadingText $_.exactText) -eq (Normalize-HeadingText ([string]$row.exactText)) })
        $punct = @($accepted | Where-Object { (Normalize-BridgeText $_.exactText) -eq (Normalize-BridgeText ([string]$row.exactText)) })
        $exactCount = @($exact).Length
        $punctCount = @($punct).Length
        $matches = if ($exactCount -gt 0) { @($exact) } else { @($punct) }
        $matchCount = @($matches).Length
        $bridgeStatus = if ($historicalSourceSha -eq $actualSourceSha256 -and $matchCount -eq 1 -and [string]$row.sourceId -eq [string]$matches[0].sourceId) { 'EXACT_CROSS_SOURCE_BRIDGE' }
            elseif ($exactCount -eq 1) { 'TEXT_ONLY_DIAGNOSTIC_MATCH' }
            elseif ($punctCount -eq 1) { 'TEXT_ONLY_DIAGNOSTIC_MATCH' }
            elseif ($matchCount -eq 0) { 'HISTORICAL_ONLY' }
            else { 'AMBIGUOUS' }
        $historicalRows.Add([pscustomobject]@{
            historicalHeadingOccurrenceId = [string]$row.headingOccurrenceId
            historicalSourceId = [string]$row.sourceId
            historicalExactText = [string]$row.exactText
            canonicalMatches = @($matches | ForEach-Object occurrenceId)
            bridgeStatus = $bridgeStatus
            authority = 'HISTORICAL_DIAGNOSTIC_ONLY'
        })
    }
}

$semanticTotalPath = Join-Path $repoRoot 'eval/a99-closed-loop/canonical-semantic-gold-vnext/semantic/DOC-0252.semantic-freeze.v1.json'
$oldSemanticTotal = $null
$oldSemanticSourceSha = $null
if (Test-Path -LiteralPath $semanticTotalPath) {
    $oldSemantic = Get-Content -Raw -LiteralPath $semanticTotalPath | ConvertFrom-Json
    $oldSemanticTotal = [int]$oldSemantic.semanticHeadingTotal
    $oldSemanticSourceSha = [string]$oldSemantic.sourceSha256
}

$canonicalMatchedIds = [Collections.Generic.HashSet[string]]::new()
foreach ($row in $historicalRows) { foreach ($id in @($row.canonicalMatches)) { [void]$canonicalMatchedIds.Add([string]$id) } }
$exactCrossCount = @($historicalRows | Where-Object bridgeStatus -eq 'EXACT_CROSS_SOURCE_BRIDGE').Count
$textOnlyCount = @($historicalRows | Where-Object bridgeStatus -eq 'TEXT_ONLY_DIAGNOSTIC_MATCH').Count
$historicalOnlyCount = @($historicalRows | Where-Object bridgeStatus -eq 'HISTORICAL_ONLY').Count
$ambiguousCount = @($historicalRows | Where-Object bridgeStatus -eq 'AMBIGUOUS').Count
$canonicalOnlyCount = @($accepted | Where-Object { -not $canonicalMatchedIds.Contains($_.occurrenceId) }).Count

Write-JsonFile (Join-Path $outputPath 'historical-bridge-diagnostic.json') ([ordered]@{
    artifactKind = 'A99_HISTORICAL_STRICT_OCCURRENCE_BRIDGE_DIAGNOSTIC'
    schemaVersion = 'a99-canonical-exhaustive-heading-occurrence-v1-doc0252'
    documentId = $documentId
    canonicalAuthorityMaterializedBeforeBridge = $true
    canonicalSourceSha256 = $actualSourceSha256
    historicalSourceSha256 = $historicalSourceSha
    historicalLevelRead = $false
    historicalParentRead = $false
    historicalOccurrenceRowsUsedForCanonicalDecision = $false
    historicalRowsRead = $historicalRows.Count
    exactCrossSourceBridgeCount = $exactCrossCount
    textOnlyDiagnosticMatchCount = $textOnlyCount
    historicalOnlyCount = $historicalOnlyCount
    canonicalOnlyCount = $canonicalOnlyCount
    ambiguousCount = $ambiguousCount
    oldSemanticTotal = $oldSemanticTotal
    oldSemanticSourceSha256 = $oldSemanticSourceSha
    oldSemanticTotalUsedForDecision = $false
    records = @($historicalRows)
    note = 'Historical source rows and semantic total are non-binding diagnostics only; current DOCX exact spans are the canonical occurrence authority.'
})

$validationStatus = if (
    $actualSourceSha256 -eq $expectedSourceSha256 -and
    $decisions.Count -eq $containers.Count -and
    $accepted.Count -gt 0 -and
    $spanErrors.Count -eq 0 -and
    $duplicateOccurrenceIds.Count -eq 0 -and
    $duplicateSourceRefs.Count -eq 0 -and
    $overlapErrors.Count -eq 0 -and
    $rebuildHash.Length -eq 64
) { 'CANONICAL_EXHAUSTIVE_OCCURRENCE_READY' } else { 'CANONICAL_OCCURRENCE_SPAN_REVIEW_REQUIRED' }

$validation = [ordered]@{
    artifactKind = 'A99_CANONICAL_EXHAUSTIVE_HEADING_OCCURRENCE_VALIDATION'
    schemaVersion = 'a99-canonical-exhaustive-heading-occurrence-v1-doc0252'
    documentId = $documentId
    status = $validationStatus
    sourceShaMatches = ($actualSourceSha256 -eq $expectedSourceSha256)
    sourceContainersReviewed = $decisions.Count
    sourceContainerCount = $containers.Count
    canonicalExactHeadingOccurrences = $accepted.Count
    spanErrors = @($spanErrors)
    duplicateOccurrenceIds = @($duplicateOccurrenceIds)
    duplicateSourceRefs = @($duplicateSourceRefs)
    overlapErrors = @($overlapErrors)
    deterministicOrdering = (($accepted | ForEach-Object occurrenceOrder) -join ',') -eq ((1..$accepted.Count) -join ',')
    deterministicRebuildHash = $rebuildHash
    historicalLevelRead = $false
    historicalParentRead = $false
    historicalHierarchyUsedForDecision = $false
    historicalOccurrenceRowsUsedForDecision = $false
    oldSemanticTotalUsedForDecision = $false
    providerCalls = 0
    modelCalls = 0
    identityReviewed = $false
    parentReviewed = $false
    levelReviewed = $false
    GoldMutationOutsideNewLane = $false
    validator = if ($validationStatus -eq 'CANONICAL_EXHAUSTIVE_OCCURRENCE_READY') { 'PASS' } else { 'FAIL_CLOSED' }
}
Write-JsonFile (Join-Path $outputPath 'validation.json') $validation

$report = @"
# DOC-0252 — canonical exhaustive heading occurrence authority

Status: **$validationStatus**

## Current source authority

- Source: $sourcePath
- Format: DOCX
- Representation: HYBRID
- Source SHA-256: $actualSourceSha256
- Raw source paragraphs: $($sourceData.rawParagraphCount)
- Non-empty source containers reviewed: $($containers.Count)
- Canonical exact heading occurrences: **$($accepted.Count)**

The current generated DOCX is the canonical source for this lane. The prior Key/PDF lineage is not used to decide occurrence truth.

## Source-only review

The accepted unit is **source container + exact heading span**. The review preserves repeated physical displays as separate occurrences and does not assign semantic identity, parentage, or level.

Excluded source material includes document metadata, embedded agenda metadata, agenda schedule item rows, page-number fragments, and prose containers that do not independently introduce a heading.

## Historical bridge — diagnostic only

- Historical rows read after current bindings were materialized: $($historicalRows.Count)
- Exact cross-source bridges: $exactCrossCount
- Text-only diagnostic matches: $textOnlyCount
- Historical-only rows: $historicalOnlyCount
- Canonical-only occurrences: $canonicalOnlyCount
- Ambiguous bridges: $ambiguousCount
- Prior semantic total observed post-freeze: $oldSemanticTotal

The historical occurrence rows and prior semantic total are non-binding diagnostics. They did not determine the accepted current-source spans. Historical level and parent fields were not read.

## Firewall

- historicalLevelRead: false
- historicalParentRead: false
- historicalHierarchyUsedForDecision: false
- historicalOccurrenceRowsUsedForDecision: false
- oldSemanticTotalUsedForDecision: false
- providerCalls: 0
- modelCalls: 0
- identityReviewed: false
- parentReviewed: false
- levelReviewed: false
- GoldMutationOutsideNewLane: false

## Validation

- Source-container coverage: PASS
- Exact span binding: $(if ($spanErrors.Count -eq 0) { 'PASS' } else { 'FAIL' })
- Duplicate occurrence refs: $(if ($duplicateOccurrenceIds.Count -eq 0) { 'PASS' } else { 'FAIL' })
- Duplicate/overlapping source spans: $(if ($duplicateSourceRefs.Count -eq 0 -and $overlapErrors.Count -eq 0) { 'PASS' } else { 'FAIL' })
- Deterministic rebuild hash: $rebuildHash
- Validator: **$($validation.validator)**

No identity, hierarchy, or historical compatibility continuation was performed beyond the post-freeze bridge diagnostic. The next authorized phase is identity only after explicit review of this occurrence authority.
"@
Write-TextFile (Join-Path $outputPath 'report.md') $report

$artifactPaths = @(
    'source-authority.json', 'source-containers.json', 'occurrence-decisions.json',
    'exact-bindings.json', 'historical-bridge-diagnostic.json', 'validation.json', 'report.md'
)
$artifactHashes = foreach ($name in $artifactPaths) {
    $full = Join-Path $outputPath $name
    [pscustomobject]@{ artifact = $name; sha256 = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$manifest = [ordered]@{
    artifactKind = 'A99_CANONICAL_EXHAUSTIVE_HEADING_OCCURRENCE_MANIFEST'
    schemaVersion = 'a99-canonical-exhaustive-heading-occurrence-v1-doc0252'
    documentId = $documentId
    status = $validationStatus
    sourcePath = $sourcePath
    sourceSha256 = $actualSourceSha256
    sourceContainerCount = $containers.Count
    canonicalExactHeadingOccurrenceCount = $accepted.Count
    historicalRows = $historicalRows.Count
    historicalExactCrossSourceBridges = $exactCrossCount
    historicalTextOnlyMatches = $textOnlyCount
    canonicalOnly = $canonicalOnlyCount
    historicalOnly = $historicalOnlyCount
    ambiguousBridges = $ambiguousCount
    sourceOnlyAuthority = $true
    historicalLevelRead = $false
    historicalParentRead = $false
    historicalHierarchyUsedForDecision = $false
    historicalOccurrenceRowsUsedForDecision = $false
    oldSemanticTotalUsedForDecision = $false
    providerCalls = 0
    modelCalls = 0
    identityReviewed = $false
    parentReviewed = $false
    levelReviewed = $false
    artifactHashes = @($artifactHashes)
    deterministicRebuildHash = $rebuildHash
}
Write-JsonFile (Join-Path $outputPath 'manifest.json') $manifest

[pscustomobject]@{
    documentId = $documentId
    status = $validationStatus
    sourceSha256 = $actualSourceSha256
    sourceContainers = $containers.Count
    canonicalExactHeadingOccurrences = $accepted.Count
    historicalExactCrossSourceBridges = $exactCrossCount
    historicalTextOnlyMatches = $textOnlyCount
    canonicalOnly = $canonicalOnlyCount
    historicalOnly = $historicalOnlyCount
    ambiguousBridges = $ambiguousCount
    spanErrors = $spanErrors.Count
    providerCalls = 0
    modelCalls = 0
} | ConvertTo-Json -Depth 8
