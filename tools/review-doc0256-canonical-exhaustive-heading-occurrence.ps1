[CmdletBinding()]
param(
    [string]$OutputRoot = "artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v1/DOC-0256"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Get-Location).Path
$documentId = "DOC-0256"
$sourcePath = "todo10_8/generated-docx/05_bien_ban_hop/076_ICP_IACG08_Minutes_2023.docx"
$sourceFullPath = Join-Path $repoRoot ($sourcePath -replace '/', '\\')
$expectedSourceSha256 = "06aec4f8e9847544e61be3a8a44261bd6796ff7af024f482039cdcff662a1a89"
$outputPath = Join-Path $repoRoot ($OutputRoot -replace '/', '\\')

function Write-JsonFile {
    param([string]$Path, $Value)
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $json = $Value | ConvertTo-Json -Depth 50
    [IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Write-TextFile {
    param([string]$Path, [string]$Text)
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    [IO.File]::WriteAllText($Path, $Text.TrimStart() + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Get-SourceParagraphRecord {
    param(
        [Parameter(Mandatory = $true)] $Paragraph,
        [Parameter(Mandatory = $true)] [string]$SourceId,
        [Parameter(Mandatory = $true)] [int]$DocumentOrder,
        [Parameter(Mandatory = $true)] $WordNamespace,
        [Parameter(Mandatory = $true)] [string]$ContainerKind
    )

    $text = (($Paragraph.Descendants($WordNamespace + "t") | ForEach-Object { $_.Value }) -join "")
    $pPr = $Paragraph.Element($WordNamespace + "pPr")
    $style = if ($null -ne $pPr) { $Paragraph.Element($WordNamespace + "pPr").Element($WordNamespace + "pStyle") } else { $null }
    $styleValue = $null
    if ($null -ne $style) {
        $attr = $style.Attribute($WordNamespace + "val")
        if ($null -eq $attr) { $attr = $style.Attribute("val") }
        if ($null -ne $attr) { $styleValue = [string]$attr.Value }
    }

    $bold = $false
    $italic = $false
    foreach ($run in $Paragraph.Descendants($WordNamespace + "r")) {
        $rPr = $run.Element($WordNamespace + "rPr")
        if ($null -eq $rPr) { continue }
        if ($null -ne $rPr.Element($WordNamespace + "b")) { $bold = $true }
        if ($null -ne $rPr.Element($WordNamespace + "i")) { $italic = $true }
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
            boldObserved = $bold
            italicObserved = $italic
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
        $entry = $zip.GetEntry("word/document.xml")
        if ($null -eq $entry) { throw "word/document.xml not found: $Path" }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $xmlText = $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $zip.Dispose() }

    $word = [Xml.Linq.XNamespace]::Get("http://schemas.openxmlformats.org/wordprocessingml/2006/main")
    $document = [Xml.Linq.XDocument]::Parse($xmlText)
    $body = $document.Root.Element($word + "body")
    if ($null -eq $body) { throw "document body not found: $Path" }

    $records = [Collections.Generic.List[object]]::new()
    $rawParagraphCount = 0
    $nonEmptyParagraphCount = 0
    $documentOrder = 0
    $bodyParagraphIndex = 0
    $tableIndex = 0
    $script:rawParagraphCount = 0
    $script:nonEmptyParagraphCount = 0
    $script:documentOrder = 0

    function Add-ParagraphNode {
        param($Paragraph, [string]$SourceId, [string]$Kind)
        $script:rawParagraphCount++
        $text = (($Paragraph.Descendants($word + "t") | ForEach-Object { $_.Value }) -join "")
        if ([string]::IsNullOrWhiteSpace($text)) { return }
        $script:documentOrder++
        $script:nonEmptyParagraphCount++
        $records.Add((Get-SourceParagraphRecord -Paragraph $Paragraph -SourceId $SourceId -DocumentOrder $script:documentOrder -WordNamespace $word -ContainerKind $Kind))
    }

    function Walk-Table {
        param($Table, [string]$TablePath)
        $rowIndex = 0
        foreach ($row in $Table.Elements($word + "tr")) {
            $rowIndex++
            $cellIndex = 0
            foreach ($cell in $row.Elements($word + "tc")) {
                $cellIndex++
                $paragraphIndex = 0
                foreach ($paragraph in $cell.Elements($word + "p")) {
                    $paragraphIndex++
                    Add-ParagraphNode -Paragraph $paragraph -SourceId ("$TablePath/tr[$rowIndex]/tc[$cellIndex]/p[$paragraphIndex]") -Kind "tableCell"
                }
                $nestedIndex = 0
                foreach ($nested in $cell.Elements($word + "tbl")) {
                    $nestedIndex++
                    Walk-Table -Table $nested -TablePath ("$TablePath/tr[$rowIndex]/tc[$cellIndex]/tbl[$nestedIndex]")
                }
            }
        }
    }

    foreach ($child in $body.Elements()) {
        if ($child.Name -eq ($word + "p")) {
            $bodyParagraphIndex++
            Add-ParagraphNode -Paragraph $child -SourceId "body[1]/p[$bodyParagraphIndex]" -Kind "bodyParagraph"
        } elseif ($child.Name -eq ($word + "tbl")) {
            $tableIndex++
            Walk-Table -Table $child -TablePath "body[1]/tbl[$tableIndex]"
        }
    }

    [pscustomobject]@{
        rawParagraphCount = $rawParagraphCount
        nonEmptySourceContainerCount = $nonEmptyParagraphCount
        containers = @($records)
    }
}

function Normalize-HeadingText {
    param([string]$Text)
    return (($Text -replace "\s+", " ").Trim())
}

function Normalize-HeadingBridgeText {
    param([string]$Text)
    return (Normalize-HeadingText $Text).Replace([char]0x2013, '-').Replace([char]0x2014, '-').Replace([char]0x2012, '-')
}

if (-not (Test-Path -LiteralPath $sourceFullPath)) { throw "Canonical source missing: $sourceFullPath" }
$actualSourceSha256 = (Get-FileHash -LiteralPath $sourceFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualSourceSha256 -ne $expectedSourceSha256) { throw "Canonical source SHA mismatch: $actualSourceSha256" }
$sourceData = Read-DocxSourceContainers -Path $sourceFullPath
$containers = @($sourceData.containers)
$bySourceId = @{}
foreach ($container in $containers) { $bySourceId[$container.sourceId] = $container }

# Source-only review decisions. These are exact source containers/spans reviewed from the
# current DOCX. Historical strict rows are not used to generate this set.
$acceptedSpans = [ordered]@{
    "body[1]/p[6]" = @(@{ start = 0; text = "Welcome and meeting objectives"; evidence = @("SOURCE_STANDALONE_HEADING_CONTAINER", "SOURCE_RUN_BOUNDARY") })
    "body[1]/p[10]" = @(@{ start = 0; text = "Regional updates on the ICP 2021 cycle implementation"; evidence = @("SOURCE_STANDALONE_HEADING_CONTAINER", "SOURCE_RUN_BOUNDARY") })
    "body[1]/p[11]" = @(@{ start = 0; text = "Africa"; evidence = @("SOURCE_INLINE_HEADING_RUN_PREFIX", "SOURCE_RUN_BOUNDARY") })
    "body[1]/p[17]" = @(@{ start = 0; text = "Asia and the Pacific"; evidence = @("SOURCE_INLINE_HEADING_RUN_PREFIX", "SOURCE_RUN_BOUNDARY") })
    "body[1]/p[20]" = @(@{ start = 0; text = "Commonwealth of Independent States"; evidence = @("SOURCE_STANDALONE_HEADING_CONTAINER", "SOURCE_RUN_BOUNDARY") })
    "body[1]/p[26]" = @(@{ start = 0; text = "Eurostat–OECD PPP Program"; evidence = @("SOURCE_INLINE_HEADING_RUN_PREFIX", "SOURCE_RUN_BOUNDARY") })
    "body[1]/p[32]" = @(@{ start = 0; text = "Latin America and the Caribbean"; evidence = @("SOURCE_STANDALONE_HEADING_CONTAINER", "SOURCE_RUN_BOUNDARY") })
    "body[1]/p[35]" = @(@{ start = 0; text = "Western Asia"; evidence = @("SOURCE_STANDALONE_HEADING_CONTAINER", "SOURCE_RUN_BOUNDARY") })
    "body[1]/p[42]" = @(@{ start = 0; text = "Global updates on the ICP 2021 cycle implementation"; evidence = @("SOURCE_STANDALONE_HEADING_CONTAINER", "SOURCE_RUN_BOUNDARY") })
    "body[1]/p[55]" = @(@{ start = 0; text = "Data review: Household consumption price and importance data"; evidence = @("SOURCE_STANDALONE_HEADING_CONTAINER", "SOURCE_RUN_BOUNDARY") })
    "body[1]/p[61]" = @(@{ start = 0; text = "Data review: Housing prices and volumes"; evidence = @("SOURCE_STANDALONE_HEADING_CONTAINER", "SOURCE_RUN_BOUNDARY") })
    "body[1]/p[67]" = @(@{ start = 0; text = "Data review: Private Education"; evidence = @("SOURCE_STANDALONE_HEADING_CONTAINER", "SOURCE_RUN_BOUNDARY") })
    "body[1]/p[73]" = @(@{ start = 0; text = "Data review: Government compensation and productivity adjustment"; evidence = @("SOURCE_STANDALONE_HEADING_CONTAINER", "SOURCE_RUN_BOUNDARY") })
    "body[1]/p[76]" = @(@{ start = 0; text = "Data review: Machinery and equipment and construction"; evidence = @("SOURCE_STANDALONE_HEADING_CONTAINER", "SOURCE_RUN_BOUNDARY") })
    "body[1]/p[80]" = @(@{ start = 0; text = "Data review: 2017-2021 National Accounts expenditures"; evidence = @("SOURCE_STANDALONE_HEADING_CONTAINER", "SOURCE_RUN_BOUNDARY") })
    "body[1]/p[88]" = @(@{ start = 0; text = "Data review: 2017-2021 Population and market exchange rates"; evidence = @("SOURCE_STANDALONE_HEADING_CONTAINER", "SOURCE_RUN_BOUNDARY") })
    "body[1]/p[90]" = @(@{ start = 0; text = "Planning for the 2023/4 governance activities and ICP 2021 cycle release at regional and global levels"; evidence = @("SOURCE_STANDALONE_HEADING_CONTAINER", "SOURCE_RUN_BOUNDARY") })
    "body[1]/p[99]" = @(@{ start = 0; text = "Planning for the ICP 2024 cycle"; evidence = @("SOURCE_STANDALONE_HEADING_CONTAINER", "SOURCE_RUN_BOUNDARY") })
    "body[1]/p[107]" = @(@{ start = 0; text = "Annex 1: Meeting Agenda"; evidence = @("SOURCE_STANDALONE_HEADING_CONTAINER", "SOURCE_RUN_BOUNDARY") })
    "body[1]/tbl[1]/tr[1]/tc[1]/p[1]" = @(@{ start = 0; text = "DAY 1: TUESDAY, OCTOBER 31, 2023"; evidence = @("SOURCE_TABLE_SECTION_CONTAINER", "SOURCE_RUN_BOUNDARY") })
    "body[1]/tbl[2]/tr[1]/tc[1]/p[1]" = @(@{ start = 0; text = "DAY 2: WEDNESDAY, NOVEMBER 1, 2023"; evidence = @("SOURCE_TABLE_SECTION_CONTAINER", "SOURCE_RUN_BOUNDARY") })
    "body[1]/tbl[3]/tr[1]/tc[1]/p[1]" = @(@{ start = 0; text = "DAY 3: THURSDAY, NOVEMBER 2, 2023"; evidence = @("SOURCE_TABLE_SECTION_CONTAINER", "SOURCE_RUN_BOUNDARY") })
    "body[1]/tbl[4]/tr[1]/tc[1]/p[1]" = @(@{ start = 0; text = "DAY 4: FRIDAY, NOVEMBER 3, 2023"; evidence = @("SOURCE_TABLE_SECTION_CONTAINER", "SOURCE_RUN_BOUNDARY") })
    "body[1]/p[124]" = @(@{ start = 0; text = "Annex 2: List of participants"; evidence = @("SOURCE_STANDALONE_HEADING_CONTAINER", "SOURCE_RUN_BOUNDARY") })
}

$decisions = [Collections.Generic.List[object]]::new()
$bindings = [Collections.Generic.List[object]]::new()
$acceptedBySource = @{}
$occurrenceOrdinal = 0
foreach ($container in $containers) {
    $spans = @()
    $decision = "NO_HEADING_SPAN"
    $evidence = @("SOURCE_CONTAINER_REVIEWED")
    if ($acceptedSpans.Contains($container.sourceId)) {
        $decision = "TRUE_HEADING_SPAN"
        foreach ($spec in @($acceptedSpans[$container.sourceId])) {
            $start = [int]$spec.start
            $text = [string]$spec.text
            $end = $start + $text.Length
            $actual = $container.rawText.Substring($start, [Math]::Min($text.Length, $container.rawText.Length - $start)).TrimEnd()
            if ((Normalize-HeadingText $actual) -ne (Normalize-HeadingText $text)) { throw "Heading span mismatch at $($container.sourceId): '$actual' vs '$text'" }
            $occurrenceOrdinal++
            $occurrenceId = "$documentId`:$($container.sourceId)#heading-span-$start-$end"
            $spans += [pscustomobject]@{
                occurrenceId = $occurrenceId
                start = $start
                end = $end
                text = $text
                decision = "TRUE_HEADING_SPAN"
                evidence = @($spec.evidence)
            }
            $acceptedBySource[$container.sourceId] = $true
            $bindings.Add([pscustomobject]@{
                occurrenceId = $occurrenceId
                documentId = $documentId
                sourceOccurrenceId = $container.sourceOccurrenceId
                sourceId = $container.sourceId
                sourceSpan = [pscustomobject]@{ start = $start; end = $end }
                exactText = $text
                documentOrder = $container.documentOrder
                occurrenceOrder = $occurrenceOrdinal
                sourceContainerText = $container.rawText
                sourceContainerKind = $container.containerKind
                sourceEvidence = $container.sourceEvidence
                bindingAuthority = "CURRENT_DOCX_SOURCE_ONLY"
            })
        }
        $evidence += @("EXACT_RUN_BOUNDARY")
    } else {
        if ($container.containerKind -eq "tableCell") { $evidence += "TABLE_DATA_OR_NON_HEADING_ROW" }
        elseif ($container.rawText -match '^\s*\d+\s*$') { $evidence += "PAGE_NUMBER_OR_FOOTER_ARTIFACT" }
        elseif ($container.sourceId -match '/p\[(12[5-9]|1[3-6]\d)\]$' -and $container.rawText -match '^[−-]') { $evidence += "PARTICIPANT_DATA" }
        elseif ($container.sourceId -match '/p\[(12[5-9]|1[3-6]\d)\]$') { $evidence += "PARTICIPANT_LIST_LABEL_OR_DATA" }
        elseif ($container.sourceId -match 'tbl\[') { $evidence += "AGENDA_TABLE_DATA" }
        elseif ($container.rawText -match 'MINUTES OF|OCTOBER 31|Hybrid Meeting') { $evidence += "DOCUMENT_METADATA" }
        else { $evidence += "PROSE_OR_NON_HEADING_SOURCE_CONTAINER" }
    }
    $decisions.Add([pscustomobject]@{
        sourceOccurrenceId = $container.sourceOccurrenceId
        sourceId = $container.sourceId
        documentOrder = $container.documentOrder
        sourceContainerText = $container.rawText
        decision = $decision
        headingSpans = @($spans)
        evidence = @($evidence)
        reviewAuthority = "CODEX_SOURCE_ONLY_OCCURRENCE_REVIEW"
        historicalMembershipUsedForDecision = $false
        semanticTotalUsedForDecision = $false
        historicalLevelRead = $false
    })
}

$strictPath = Join-Path $repoRoot "eval/a99-closed-loop/strict-gold-v4/DOC-0256.strict-gold-v4.json"
$historicalRows = @()
if (Test-Path -LiteralPath $strictPath) {
    # This bridge is computed only after the source-only occurrence freeze. Historical level
    # fields are deliberately not accessed.
    $strict = Get-Content -Raw -LiteralPath $strictPath | ConvertFrom-Json
    foreach ($row in @($strict.headings)) {
        $exactMatch = @($bindings | Where-Object { (Normalize-HeadingText $_.exactText) -eq (Normalize-HeadingText ([string]$row.exactText)) })
        $punctuationMatch = @($bindings | Where-Object { (Normalize-HeadingBridgeText $_.exactText) -eq (Normalize-HeadingBridgeText ([string]$row.exactText)) })
        $match = if ($exactMatch.Count -gt 0) { $exactMatch } else { $punctuationMatch }
        $historicalRows += [pscustomobject]@{
            historicalHeadingOccurrenceId = [string]$row.headingOccurrenceId
            historicalSourceId = [string]$row.sourceId
            historicalExactText = [string]$row.exactText
            canonicalMatches = @($match | ForEach-Object { $_.occurrenceId })
            bridgeStatus = if (@($exactMatch).Count -eq 1) { "EXACT_TEXT_BRIDGE_ONLY" } elseif (@($exactMatch).Count -eq 0 -and @($punctuationMatch).Count -eq 1) { "PUNCTUATION_NORMALIZED_BRIDGE_ONLY" } elseif (@($match).Count -eq 0) { "NO_CANONICAL_TEXT_MATCH" } else { "AMBIGUOUS_TEXT_BRIDGE" }
            authority = "HISTORICAL_POSITIVE_DIAGNOSTIC_ONLY"
        }
    }
}

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

$sourceAuthority = [ordered]@{
    artifactKind = "A99_CANONICAL_EXHAUSTIVE_HEADING_SOURCE_AUTHORITY"
    schemaVersion = "a99-canonical-exhaustive-heading-occurrence-v1-doc0256"
    documentId = $documentId
    sourcePath = $sourcePath
    sourceSha256 = $actualSourceSha256
    sourceClassification = "GENERATED_DOCX_CURRENT_SOURCE_AUTHORITY"
    sourceReviewAuthority = "CODEX_SOURCE_ONLY_OCCURRENCE_REVIEW"
    sourceTruthScope = "TRUE_HEADING_OCCURRENCE_ONLY"
    rawParagraphCount = $sourceData.rawParagraphCount
    nonEmptySourceContainerCount = $sourceData.nonEmptySourceContainerCount
    acceptedCanonicalOccurrenceCount = $bindings.Count
    historicalLevelRead = $false
    historicalHierarchyRead = $false
    providerCalls = 0
    modelCalls = 0
    semanticIdentityAssigned = $false
    hierarchyAssigned = $false
}
Write-JsonFile (Join-Path $outputPath "source-authority.json") $sourceAuthority

$sourceContainers = [ordered]@{
    artifactKind = "A99_CANONICAL_EXHAUSTIVE_HEADING_SOURCE_CONTAINERS"
    schemaVersion = "a99-canonical-exhaustive-heading-occurrence-v1-doc0256"
    documentId = $documentId
    sourceSha256 = $actualSourceSha256
    containerCount = $containers.Count
    rawParagraphCount = $sourceData.rawParagraphCount
    nonEmptySourceContainerCount = $sourceData.nonEmptySourceContainerCount
    containers = $containers
}
Write-JsonFile (Join-Path $outputPath "source-containers.json") $sourceContainers

Write-JsonFile (Join-Path $outputPath "occurrence-decisions.json") ([ordered]@{
    artifactKind = "A99_CANONICAL_EXHAUSTIVE_HEADING_OCCURRENCE_DECISIONS"
    schemaVersion = "a99-canonical-exhaustive-heading-occurrence-v1-doc0256"
    documentId = $documentId
    sourceSha256 = $actualSourceSha256
    containerCount = $containers.Count
    decisionCount = $decisions.Count
    acceptedSpanCount = $bindings.Count
    decisions = $decisions
    sourceOnly = $true
    historicalLevelRead = $false
    hierarchyUsed = $false
    semanticTotalUsed = $false
})

Write-JsonFile (Join-Path $outputPath "exact-bindings.json") ([ordered]@{
    artifactKind = "A99_CANONICAL_EXHAUSTIVE_HEADING_OCCURRENCE_EXACT_BINDINGS"
    schemaVersion = "a99-canonical-exhaustive-heading-occurrence-v1-doc0256"
    documentId = $documentId
    authority = "CANONICAL_EXHAUSTIVE_OCCURRENCE_READY"
    sourceSha256 = $actualSourceSha256
    bindingUnit = "SOURCE_CONTAINER_PLUS_EXACT_HEADING_SPAN"
    acceptedCanonicalBindings = $bindings
    semanticNodeAssignments = $false
    parentAssignments = $false
    levelAssignments = $false
})

Write-JsonFile (Join-Path $outputPath "historical-bridge-diagnostic.json") ([ordered]@{
    artifactKind = "A99_HISTORICAL_STRICT_OCCURRENCE_BRIDGE_DIAGNOSTIC"
    schemaVersion = "a99-canonical-exhaustive-heading-occurrence-v1-doc0256"
    documentId = $documentId
    canonicalAuthorityFrozenBeforeBridge = $true
    historicalLevelRead = $false
    historicalParentRead = $false
    historicalSemanticTotalUsedAsTarget = $false
    historicalRowsRead = @($historicalRows).Count
    exactTextBridgeCount = @($historicalRows | Where-Object bridgeStatus -eq "EXACT_TEXT_BRIDGE_ONLY").Count
    punctuationNormalizedBridgeCount = @($historicalRows | Where-Object bridgeStatus -eq "PUNCTUATION_NORMALIZED_BRIDGE_ONLY").Count
    ambiguousTextBridgeCount = @($historicalRows | Where-Object bridgeStatus -eq "AMBIGUOUS_TEXT_BRIDGE").Count
    noCanonicalTextMatchCount = @($historicalRows | Where-Object bridgeStatus -eq "NO_CANONICAL_TEXT_MATCH").Count
    records = $historicalRows
    note = "Diagnostic only. Historical strict rows are not canonical occurrence authority and did not determine the accepted source spans."
})

$validationStatus = if ($bindings.Count -eq 24 -and $decisions.Count -eq $containers.Count -and $actualSourceSha256 -eq $expectedSourceSha256) { "CANONICAL_EXHAUSTIVE_OCCURRENCE_READY" } else { "CANONICAL_OCCURRENCE_SPAN_REVIEW_REQUIRED" }
$validation = [ordered]@{
    artifactKind = "A99_CANONICAL_EXHAUSTIVE_HEADING_OCCURRENCE_VALIDATION"
    schemaVersion = "a99-canonical-exhaustive-heading-occurrence-v1-doc0256"
    documentId = $documentId
    status = $validationStatus
    checks = [ordered]@{
        sourceExists = $true
        sourceShaMatchesExpected = ($actualSourceSha256 -eq $expectedSourceSha256)
        sourceContainersMaterialized = ($containers.Count -gt 0)
        everyAcceptedSpanWithinContainer = $true
        everyAcceptedSpanTextMatchesSource = $true
        noDuplicateOccurrenceIds = (@($bindings.occurrenceId | Select-Object -Unique).Count -eq $bindings.Count)
        allBindingsReferenceKnownContainers = (@($bindings | Where-Object { -not $bySourceId.ContainsKey($_.sourceId) }).Count -eq 0)
        allContainersDecisionCovered = ($decisions.Count -eq $containers.Count)
        semanticIdentityAssigned = $false
        hierarchyAssigned = $false
        levelAssigned = $false
        historicalLevelRead = $false
        historicalHierarchyRead = $false
        providerCalls = 0
        modelCalls = 0
        goldMutation = $false
    }
    rawParagraphCount = $sourceData.rawParagraphCount
    sourceContainerCount = $containers.Count
    acceptedCanonicalOccurrenceCount = $bindings.Count
    rejectedOrNonHeadingContainerCount = @($decisions | Where-Object decision -eq "NO_HEADING_SPAN").Count
    statusMeaning = "Source-only canonical occurrence authority; no semantic identity, parent, tree, or level authority."
}
Write-JsonFile (Join-Path $outputPath "validation.json") $validation

$manifestFiles = @("source-authority.json", "source-containers.json", "occurrence-decisions.json", "exact-bindings.json", "historical-bridge-diagnostic.json", "validation.json")
$manifestEntries = @($manifestFiles | ForEach-Object {
    $full = Join-Path $outputPath $_
    [pscustomobject]@{ name = $_; path = (Join-Path $OutputRoot $_) -replace '\\','/'; sha256 = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$manifest = [ordered]@{
    artifactKind = "A99_CANONICAL_EXHAUSTIVE_HEADING_OCCURRENCE_MANIFEST"
    schemaVersion = "a99-canonical-exhaustive-heading-occurrence-v1-doc0256"
    documentId = $documentId
    status = $validationStatus
    sourcePath = $sourcePath
    sourceSha256 = $actualSourceSha256
    files = $manifestEntries
    acceptedCanonicalOccurrenceCount = $bindings.Count
    sourceContainerCount = $containers.Count
    historicalLevelRead = $false
    historicalHierarchyRead = $false
    providerCalls = 0
    modelCalls = 0
    semanticIdentityMutation = $false
    hierarchyMutation = $false
    levelMutation = $false
    goldMutation = $false
    stopAfterOccurrenceAuthority = $true
}
Write-JsonFile (Join-Path $outputPath "manifest.json") $manifest

$report = @"
# DOC-0256 — canonical exhaustive heading occurrence authority

Status: **$validationStatus**

## Source authority

- Current source: `$sourcePath`
- SHA-256: `$actualSourceSha256`
- Raw paragraph containers: $($sourceData.rawParagraphCount)
- Non-empty source containers reviewed: $($containers.Count)
- Accepted TRUE_HEADING spans: $($bindings.Count)
- Binding unit: `SOURCE CONTAINER + EXACT HEADING SPAN`

The source-only review accepts the narrative section headings, the two annex headings, and the four agenda-day section headings. It rejects document metadata, prose, page-number artifacts, agenda time/topic rows, and participant-list data as non-heading containers. Inline narrative headings are bound to their exact prefix span rather than to the full paragraph.

## Authority firewall

- Historical strict heading rows were not used to generate the accepted set.
- Historical semantic total `34` was not used as a target.
- Historical level and historical parent were not read.
- No semantic node, identity, parent, tree, or level was assigned.
- Provider/model calls: `0`.

## Historical bridge

The historical bridge is post-freeze diagnostic only. It does not promote historical rows to canonical authority and does not alter the source-only decisions.

## Stop condition

This artifact stops after the occurrence lane. Identity and hierarchy require separate phases.
"@
Write-TextFile (Join-Path $outputPath "report.md") $report

Write-Output ($manifest | ConvertTo-Json -Depth 10)
