[CmdletBinding()]
param(
    [string]$OutputRoot = "artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v1"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Get-Location).Path
$outputPath = Join-Path $repoRoot $OutputRoot

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] $Value
    )

    $parent = Split-Path -Parent $Path
    if ($parent) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    $json = $Value | ConvertTo-Json -Depth 40
    [System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function Read-JsonFile {
    param([Parameter(Mandatory = $true)] [string]$Path)
    return (Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json)
}

function Get-ParagraphRecord {
    param(
        [Parameter(Mandatory = $true)] $Paragraph,
        [Parameter(Mandatory = $true)] [string]$SourceId,
        [Parameter(Mandatory = $true)] [int]$DocumentOrder,
        [Parameter(Mandatory = $true)] $WordNamespace
    )

    $text = (($Paragraph.Descendants($WordNamespace + "t") | ForEach-Object { $_.Value }) -join "")
    $pPr = $Paragraph.Element($WordNamespace + "pPr")
    $style = if ($null -ne $pPr) { $pPr.Element($WordNamespace + "pStyle") } else { $null }
    $bold = $false
    foreach ($run in $Paragraph.Descendants($WordNamespace + "r")) {
        $rPr = $run.Element($WordNamespace + "rPr")
        if ($null -ne $rPr -and $null -ne $rPr.Element($WordNamespace + "b")) {
            $bold = $true
            break
        }
    }

    [pscustomobject]@{
        sourceOccurrenceId = $SourceId
        sourceId = $SourceId
        documentOrder = $DocumentOrder
        rawText = $text
        sourceSpan = [pscustomobject]@{ start = 0; end = $text.Length }
        sourceEvidence = [pscustomobject]@{
            paragraphStyleId = if ($null -ne $style) { [string]$style.Attribute($WordNamespace + "val") } else { $null }
            boldObserved = $bold
            parserOwnedEvidenceOnly = $true
        }
    }
}

function Read-DocxSourceUniverse {
    param([Parameter(Mandatory = $true)] [string]$Path)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    $zip = [System.IO.Compression.ZipFile]::OpenRead($resolved)
    try {
        $entry = $zip.GetEntry("word/document.xml")
        if ($null -eq $entry) { throw "word/document.xml not found in $Path" }
        $reader = [System.IO.StreamReader]::new($entry.Open())
        try { $xmlText = $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally {
        $zip.Dispose()
    }

    $word = [System.Xml.Linq.XNamespace]::Get("http://schemas.openxmlformats.org/wordprocessingml/2006/main")
    $document = [System.Xml.Linq.XDocument]::Parse($xmlText)
    $body = $document.Root.Element($word + "body")
    if ($null -eq $body) { throw "document body not found in $Path" }

    $records = [System.Collections.Generic.List[object]]::new()
    $rawParagraphCount = 0
    $nonEmptyParagraphCount = 0
    $documentOrder = 0
    $bodyParagraphIndex = 0
    $tableIndex = 0

    function Add-ParagraphNode {
        param($Paragraph, [string]$SourceId, [ref]$RawCount, [ref]$DocumentOrderRef, [ref]$NonEmptyCount)
        $RawCount.Value++
        $record = Get-ParagraphRecord -Paragraph $Paragraph -SourceId $SourceId -DocumentOrder 0 -WordNamespace $word
        if (-not [string]::IsNullOrWhiteSpace($record.rawText)) {
            $DocumentOrderRef.Value++
            $NonEmptyCount.Value++
            $record.documentOrder = $DocumentOrderRef.Value
            $records.Add($record)
        }
    }

    function Walk-Table {
        param($Table, [string]$TablePath, [ref]$RawCount, [ref]$DocumentOrderRef, [ref]$NonEmptyCount)
        $rowIndex = 0
        foreach ($row in $Table.Elements($word + "tr")) {
            $rowIndex++
            $cellIndex = 0
            foreach ($cell in $row.Elements($word + "tc")) {
                $cellIndex++
                $paragraphIndex = 0
                foreach ($paragraph in $cell.Elements($word + "p")) {
                    $paragraphIndex++
                    Add-ParagraphNode -Paragraph $paragraph -SourceId ("$TablePath/tr[$rowIndex]/tc[$cellIndex]/p[$paragraphIndex]") -RawCount $RawCount -DocumentOrderRef $DocumentOrderRef -NonEmptyCount $NonEmptyCount
                }
                $nestedIndex = 0
                foreach ($nested in $cell.Elements($word + "tbl")) {
                    $nestedIndex++
                    Walk-Table -Table $nested -TablePath ("$TablePath/tr[$rowIndex]/tc[$cellIndex]/tbl[$nestedIndex]") -RawCount $RawCount -DocumentOrderRef $DocumentOrderRef -NonEmptyCount $NonEmptyCount
                }
            }
        }
    }

    foreach ($child in $body.Elements()) {
        if ($child.Name -eq ($word + "p")) {
            $bodyParagraphIndex++
            Add-ParagraphNode -Paragraph $child -SourceId "body[1]/p[$bodyParagraphIndex]" -RawCount ([ref]$rawParagraphCount) -DocumentOrderRef ([ref]$documentOrder) -NonEmptyCount ([ref]$nonEmptyParagraphCount)
        } elseif ($child.Name -eq ($word + "tbl")) {
            $tableIndex++
            Walk-Table -Table $child -TablePath "body[1]/tbl[$tableIndex]" -RawCount ([ref]$rawParagraphCount) -DocumentOrderRef ([ref]$documentOrder) -NonEmptyCount ([ref]$nonEmptyParagraphCount)
        }
    }

    [pscustomobject]@{
        rawParagraphCount = $rawParagraphCount
        nonEmptyOccurrenceCount = $nonEmptyParagraphCount
        occurrences = @($records)
    }
}

function Get-HistoricalBridge {
    param(
        [Parameter(Mandatory = $true)] [string]$DocumentId,
        [Parameter(Mandatory = $true)] [string]$PacketPath
    )

    if (-not (Test-Path -LiteralPath $PacketPath)) {
        return [pscustomobject]@{ packetPath = $PacketPath; packetExists = $false; records = @() }
    }

    $packet = Read-JsonFile -Path $PacketPath
    $records = @($packet.headingOccurrences | ForEach-Object {
        [pscustomobject]@{
            historicalHeadingOccurrenceId = $_.headingOccurrenceId
            sourceId = $_.sourceId
            sourceText = $_.sourceText
            sourceOrder = $_.sourceOrder
            sourceSpan = $_.sourceSpan
            sourceProvenance = $_.sourceProvenance
            bridgeAuthority = "HISTORICAL_POSITIVE_ONLY"
            canonicalAccepted = $false
        }
    })

    [pscustomobject]@{
        packetPath = $PacketPath
        packetExists = $true
        documentId = $DocumentId
        historicalPositiveCount = $records.Count
        records = $records
    }
}

function Write-Report {
    param([string]$Path, [string]$Text)
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    [System.IO.File]::WriteAllText($Path, $Text.TrimStart() + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

Remove-Item -LiteralPath $outputPath -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

$docs = @(
    [pscustomobject]@{
        documentId = "DOC-0001"
        sourcePath = "bench/01-style-chuan.docx"
        sourceSha256 = "0ea894862a91f94623fab1de4a0c67b27d4a32fcbd0f41299a1568058d048d14"
        expectedSemanticTotal = 7
        sourceClassification = "MISSING_CANONICAL_SOURCE"
    },
    [pscustomobject]@{
        documentId = "DOC-0205"
        sourcePath = "todo10_8/heading_corpus_95_word/01_phap_quy/025_ND_47-2020_Chia_se_du_lieu_so.docx"
        sourceSha256 = "b145e31a58e76cad1c77967c639884d1c566020d344d725ca145fafb5de86878"
        expectedSemanticTotal = 72
        sourceClassification = "PLACEHOLDER_CONVERTED_DOCX"
    },
    [pscustomobject]@{
        documentId = "DOC-0258"
        sourcePath = "todo10_8/heading_corpus_95_word/05_bien_ban_hop/078_ICP_IACG07_Minutes_May_2023.docx"
        sourceSha256 = "7d4661e4f40cfb80cf3d1bb01d465842862d8a8ba54a6476d3e8fab8d8d95e30"
        expectedSemanticTotal = 37
        sourceClassification = "CANONICAL_DOCX_AVAILABLE"
    }
)

$docResults = [System.Collections.Generic.List[object]]::new()
foreach ($doc in $docs) {
    $docDir = Join-Path $outputPath $doc.documentId
    New-Item -ItemType Directory -Force -Path $docDir | Out-Null
    $absoluteSource = Join-Path $repoRoot ($doc.sourcePath -replace '/', '\')
    $sourceExists = Test-Path -LiteralPath $absoluteSource
    $actualSha = if ($sourceExists) { (Get-FileHash -LiteralPath $absoluteSource -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
    $shaMatches = $sourceExists -and ($actualSha -eq $doc.sourceSha256)
    $sourceData = $null
    if ($sourceExists -and $shaMatches) {
        $sourceData = Read-DocxSourceUniverse -Path $absoluteSource
    }

    $occurrences = if ($null -ne $sourceData) { @($sourceData.occurrences) } else { @() }
    $placeholder = $false
    if ($doc.documentId -eq "DOC-0205" -and $occurrences.Count -gt 0) {
        $placeholder = (@($occurrences.rawText) -join "`n") -match "Converted to DOCX from legacy DOC text extraction"
    }

    $packetPath = Join-Path $repoRoot ("artifacts/authority-audit/strict-heading-canonical-hierarchy-review-v2/packets/{0}.canonical-hierarchy-review.v2.json" -f $doc.documentId)
    $bridge = Get-HistoricalBridge -DocumentId $doc.documentId -PacketPath $packetPath

    $status = if (-not $sourceExists -or -not $shaMatches) {
        "SOURCE_BINDING_BLOCKED"
    } elseif ($placeholder) {
        "SOURCE_BINDING_BLOCKED"
    } elseif ($bridge.historicalPositiveCount -lt $doc.expectedSemanticTotal) {
        "CANONICAL_SEMANTIC_TOTAL_CONFLICT_REVIEW_REQUIRED"
    } else {
        "CANONICAL_OCCURRENCE_REVIEW_REQUIRED"
    }

    $sourceUniverse = [pscustomobject]@{
        artifactKind = "A99_CANONICAL_EXHAUSTIVE_HEADING_SOURCE_UNIVERSE"
        schemaVersion = "a99-canonical-exhaustive-heading-occurrence-v1"
        documentId = $doc.documentId
        sourcePath = $doc.sourcePath
        expectedSourceSha256 = $doc.sourceSha256
        actualSourceSha256 = $actualSha
        sourceExists = $sourceExists
        sourceShaMatches = $shaMatches
        sourceClassification = if ($placeholder) { "PLACEHOLDER_CONVERTED_DOCX" } elseif ($sourceExists) { $doc.sourceClassification } else { "MISSING_CANONICAL_SOURCE" }
        rawParagraphCount = if ($null -ne $sourceData) { $sourceData.rawParagraphCount } else { 0 }
        nonEmptySourceOccurrenceCount = $occurrences.Count
        occurrenceTruthStatus = "NOT_ADJUDICATED"
        candidatesAreAttentionOnly = $true
        occurrences = $occurrences
    }
    Write-JsonFile -Path (Join-Path $docDir "source-universe.json") -Value $sourceUniverse

    $bridgeOutput = [pscustomobject]@{
        artifactKind = "A99_HISTORICAL_POSITIVE_OCCURRENCE_BRIDGE"
        schemaVersion = "a99-canonical-exhaustive-heading-occurrence-v1"
        documentId = $doc.documentId
        authority = "NOT_CANONICAL_EXHAUSTIVE"
        historicalLevelRead = $false
        records = $bridge.records
    }
    Write-JsonFile -Path (Join-Path $docDir "historical-positive-bridge.json") -Value $bridgeOutput

    $occurrenceAdjudication = [pscustomobject]@{
        artifactKind = "A99_CANONICAL_OCCURRENCE_ADJUDICATION"
        schemaVersion = "a99-canonical-exhaustive-heading-occurrence-v1"
        documentId = $doc.documentId
        status = $status
        reviewScope = "HEADING_OCCURRENCE_TRUTH_ONLY"
        reviewCompleted = $false
        acceptedCanonicalOccurrences = @()
        unresolvedSourceRegions = @()
        semanticIdentityAssigned = $false
        hierarchyAssigned = $false
        levelEntered = $false
        notes = @(
            "Historical rows are positive bridges only and are not exhaustive authority.",
            "No semanticNodeId, parent, tree, level, PRIMARY, REPEAT, or CONTINUATION was assigned."
        )
    }
    Write-JsonFile -Path (Join-Path $docDir "occurrence-adjudication.json") -Value $occurrenceAdjudication

    $bindings = [pscustomobject]@{
        artifactKind = "A99_CANONICAL_OCCURRENCE_EXACT_BINDINGS"
        schemaVersion = "a99-canonical-exhaustive-heading-occurrence-v1"
        documentId = $doc.documentId
        authority = "NONE_FROZEN"
        acceptedCanonicalBindings = @()
        historicalPositiveBindingsAreNotCanonical = $true
        sourceSha256 = $actualSha
    }
    Write-JsonFile -Path (Join-Path $docDir "exact-bindings.json") -Value $bindings

    $validation = [pscustomobject]@{
        artifactKind = "A99_CANONICAL_OCCURRENCE_VALIDATION"
        schemaVersion = "a99-canonical-exhaustive-heading-occurrence-v1"
        documentId = $doc.documentId
        status = $status
        checks = [ordered]@{
            sourceExists = $sourceExists
            sourceShaMatchesAuthority = $shaMatches
            sourceReadable = $null -ne $sourceData
            sourceUniverseMaterialized = $null -ne $sourceData
            placeholderSourceBlocked = $placeholder
            historicalBridgeIsNonExhaustive = $true
            exhaustiveHeadingReviewCompleted = $false
            canonicalOccurrenceAuthorityFrozen = $false
            noSemanticIdentityFields = $true
            noHierarchyFields = $true
            noLevelField = $true
            noProviderCalls = $true
            noGoldMutation = $true
        }
        expectedSemanticTotal = $doc.expectedSemanticTotal
        historicalPositiveCount = $bridge.historicalPositiveCount
        sourceOccurrenceCount = $occurrences.Count
    }
    Write-JsonFile -Path (Join-Path $docDir "validation.json") -Value $validation

    $report = @"
# $($doc.documentId) — canonical exhaustive occurrence authority

Status: **$status**

This Phase 1 artifact is restricted to source occurrence truth. It does not assign semantic identity, parent, tree, level, PRIMARY, REPEAT, or CONTINUATION.

## Source

- Path: `$($doc.sourcePath)`
- Expected SHA-256: `$($doc.sourceSha256)`
- Actual SHA-256: `$(if ($null -eq $actualSha) { 'MISSING' } else { $actualSha })`
- Source exists: `$sourceExists`
- Source SHA matches: `$shaMatches`
- Raw paragraph count: `$(if ($null -ne $sourceData) { $sourceData.rawParagraphCount } else { 0 })`
- Non-empty source occurrence count: `$($occurrences.Count)`

## Authority boundary

Historical strict rows are retained only as a positive bridge. They are not treated as exhaustive heading truth. Candidate hints and parser evidence are attention evidence only and are never used as a recall gate.

No canonical occurrence adjudication was silently synthesized from the expected semantic total.

## Freeze result

- Accepted canonical bindings: `0`
- Provider calls: `0`
- Historical level read: `false`
- Parent/tree mutation: `false`
- Gold mutation: `false`

The phase stops here; hierarchy review must not be opened automatically.
"@
    Write-Report -Path (Join-Path $docDir "report.md") -Text $report

    $docResults.Add([pscustomobject]@{
        documentId = $doc.documentId
        status = $status
        sourcePath = $doc.sourcePath
        expectedSourceSha256 = $doc.sourceSha256
        actualSourceSha256 = $actualSha
        sourceExists = $sourceExists
        sourceShaMatches = $shaMatches
        sourceOccurrenceCount = $occurrences.Count
        historicalPositiveCount = $bridge.historicalPositiveCount
        expectedSemanticTotal = $doc.expectedSemanticTotal
        acceptedCanonicalOccurrenceCount = 0
        providerCalls = 0
        historicalLevelRead = $false
    })
}

$manifest = [pscustomobject]@{
    artifactKind = "A99_CANONICAL_EXHAUSTIVE_HEADING_OCCURRENCE_PHASE1"
    schemaVersion = "a99-canonical-exhaustive-heading-occurrence-v1"
    phase = "MATERIALIZE_CANONICAL_EXHAUSTIVE_HEADING_OCCURRENCE_AUTHORITY_PHASE1"
    authorityBoundary = "SOURCE_OCCURRENCE_TRUTH_ONLY"
    documents = $docResults
    historicalLevelRead = $false
    providerCalls = 0
    goldMutation = $false
    hierarchyMutation = $false
    semanticIdentityMutation = $false
    stopAfterOccurrenceAuthority = $true
}
Write-JsonFile -Path (Join-Path $outputPath "manifest.json") -Value $manifest

$summary = [pscustomobject]@{
    artifactKind = "A99_CANONICAL_EXHAUSTIVE_HEADING_OCCURRENCE_AUTHORITY_SUMMARY"
    schemaVersion = "a99-canonical-exhaustive-heading-occurrence-v1"
    status = "PHASE1_SOURCE_OCCURRENCE_AUTHORITY_NOT_FROZEN"
    documentCount = $docResults.Count
    readyCount = @($docResults | Where-Object { $_.status -eq "CANONICAL_EXHAUSTIVE_OCCURRENCE_READY" }).Count
    reviewRequiredCount = @($docResults | Where-Object { $_.status -eq "CANONICAL_OCCURRENCE_REVIEW_REQUIRED" -or $_.status -eq "CANONICAL_SEMANTIC_TOTAL_CONFLICT_REVIEW_REQUIRED" }).Count
    sourceBindingBlockedCount = @($docResults | Where-Object { $_.status -eq "SOURCE_BINDING_BLOCKED" }).Count
    statuses = $docResults
    providerCalls = 0
    goldReads = 0
    historicalLevelRead = $false
    hierarchyReviewed = $false
    nextGate = "SOURCE_ONLY_OCCURRENCE_REVIEW_BEFORE_HIERARCHY"
}
Write-JsonFile -Path (Join-Path $outputPath "authority-summary.json") -Value $summary

Write-Output ($summary | ConvertTo-Json -Depth 10)
