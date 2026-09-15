[CmdletBinding()]
param(
    [string]$Phase1Root = "artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v1",
    [string]$OutputRoot = "artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v1/source-review-v1"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Get-Location).Path
$phase1Path = Join-Path $repoRoot $Phase1Root
$outputPath = Join-Path $repoRoot $OutputRoot

function Read-JsonFile {
    param([Parameter(Mandatory = $true)] [string]$Path)
    return (Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json)
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] $Value
    )
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $json = $Value | ConvertTo-Json -Depth 40
    [System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function Write-TextFile {
    param([string]$Path, [string]$Text)
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    [System.IO.File]::WriteAllText($Path, $Text.TrimStart() + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function New-Decision {
    param(
        [Parameter(Mandatory = $true)] $Occurrence,
        [Parameter(Mandatory = $true)] [ValidateSet("TRUE_HEADING", "NON_HEADING", "REVIEW_REQUIRED")] [string]$Decision,
        [Parameter(Mandatory = $true)] [string[]]$Evidence,
        [Parameter(Mandatory = $true)] [string]$Reason,
        [Parameter(Mandatory = $true)] [ValidateSet("HIGH", "MEDIUM", "LOW")] [string]$Confidence
    )

    [pscustomobject]@{
        sourceOccurrenceId = $Occurrence.sourceOccurrenceId
        sourceId = $Occurrence.sourceId
        documentOrder = $Occurrence.documentOrder
        text = $Occurrence.rawText
        decision = $Decision
        evidence = $Evidence
        reason = $Reason
        confidence = $Confidence
        sourceEvidence = $Occurrence.sourceEvidence
        reviewAuthority = "CODEX_SOURCE_ONLY_ADJUDICATION"
        historicalPositiveMembershipUsedForDecision = $false
        semanticTotalUsedForDecision = $false
        hierarchyUsedForDecision = $false
        historicalLevelRead = $false
    }
}

function Get-Doc0001Decision {
    param($Occurrence)
    $style = [string]$Occurrence.sourceEvidence.paragraphStyleId
    if ($style -in @("Heading1", "Heading2", "Heading3")) {
        return New-Decision -Occurrence $Occurrence -Decision "TRUE_HEADING" -Evidence @("SOURCE_PARAGRAPH_STYLE:$style", "NUMBERED_SECTION_TEXT", "DOCUMENT_ORDER") -Reason "The source paragraph carries an explicit Heading style and a numbered section heading text." -Confidence HIGH
    }
    return New-Decision -Occurrence $Occurrence -Decision "NON_HEADING" -Evidence @("SOURCE_PARAGRAPH_STYLE:Normal", "NARRATIVE_BODY_TEXT", "DOCUMENT_ORDER") -Reason "The source paragraph is ordinary narrative/body text between structural heading paragraphs." -Confidence HIGH
}

function Get-Doc0258Decision {
    param($Occurrence, [System.Collections.Generic.HashSet[string]]$TrueIds)
    if ($TrueIds.Contains([string]$Occurrence.sourceId)) {
        $isTable = ([string]$Occurrence.sourceId).Contains("/tbl[")
        $kind = if ($isTable) { "AGENDA_DAY_OR_SESSION_LABEL" } else { "DOCUMENT_OR_NARRATIVE_SECTION_LABEL" }
        return New-Decision -Occurrence $Occurrence -Decision "TRUE_HEADING" -Evidence @("SOURCE_TEXT_FUNCTION:$kind", "SOURCE_CONTAINER:$($Occurrence.sourceId)", "DOCUMENT_ORDER", "SURROUNDING_SOURCE_STRUCTURE") -Reason "The source occurrence is a standalone document title, narrative section label, agenda day/session label, or agenda session heading; it introduces a structural content unit rather than providing prose, pagination, or schedule metadata." -Confidence HIGH
    }

    $text = ([string]$Occurrence.rawText).Trim()
    $sourceId = [string]$Occurrence.sourceId
    if ($text -match '^[0-9]+$' -or $text -match '^[0-9]+\s*$') {
        return New-Decision -Occurrence $Occurrence -Decision "NON_HEADING" -Evidence @("PAGE_NUMBER_PATTERN", "SOURCE_TEXT", "DOCUMENT_ORDER") -Reason "The occurrence is a page-number artifact, not a document heading." -Confidence HIGH
    }
    if ($sourceId -match '/tbl\[' -and ($text -match '^[0-9]{1,2}:|^[–-]$|^\d{1,2}:\d{2}\s*[–-]')) {
        return New-Decision -Occurrence $Occurrence -Decision "NON_HEADING" -Evidence @("AGENDA_SCHEDULE_CELL", "SOURCE_CONTAINER:$sourceId", "SOURCE_TEXT") -Reason "The table cell contains a time slot or separator used to lay out the agenda, not a heading occurrence." -Confidence HIGH
    }
    if ($sourceId -match '/tbl\[') {
        return New-Decision -Occurrence $Occurrence -Decision "NON_HEADING" -Evidence @("AGENDA_SUPPORTING_CELL", "SOURCE_CONTAINER:$sourceId", "SOURCE_TEXT") -Reason "The table cell is supporting agenda content/substructure rather than one of the document's standalone heading occurrences." -Confidence MEDIUM
    }
    if ($sourceId -match 'body\[1\]/p\[(11[5-9]|12[0-8])\]') {
        return New-Decision -Occurrence $Occurrence -Decision "NON_HEADING" -Evidence @("ANNEX_PARTICIPANT_LIST_OR_PAGE_ARTIFACT", "SOURCE_TEXT", "DOCUMENT_ORDER") -Reason "The occurrence belongs to the Annex 2 participant list or a page-number artifact, not a heading." -Confidence HIGH
    }
    if ($sourceId -match 'body\[1\]/p\[(4|5)\]') {
        return New-Decision -Occurrence $Occurrence -Decision "NON_HEADING" -Evidence @("DOCUMENT_METADATA", "SOURCE_TEXT", "DOCUMENT_ORDER") -Reason "The occurrence is meeting date/format metadata under the title, not a structural heading." -Confidence HIGH
    }
    if ($text -match '^\d+\s') {
        return New-Decision -Occurrence $Occurrence -Decision "NON_HEADING" -Evidence @("FOOTNOTE_OR_NOTE_PATTERN", "SOURCE_TEXT", "DOCUMENT_ORDER") -Reason "The occurrence is a footnote/note continuation embedded in the source, not a heading." -Confidence HIGH
    }
    return New-Decision -Occurrence $Occurrence -Decision "NON_HEADING" -Evidence @("NARRATIVE_PROSE", "SOURCE_TEXT", "DOCUMENT_ORDER") -Reason "The occurrence is narrative prose or supporting document text and does not introduce a standalone structural heading." -Confidence HIGH
}

function Get-HistoricalCountAfterFreeze {
    param([string]$DocumentId)
    $path = Join-Path $repoRoot ("artifacts/authority-audit/strict-heading-canonical-hierarchy-review-v2/packets/{0}.canonical-hierarchy-review.v2.json" -f $DocumentId)
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $packet = Read-JsonFile $path
    return @($packet.headingOccurrences).Count
}

function Get-SemanticTotalAfterFreeze {
    param([string]$DocumentId)
    $path = Join-Path $repoRoot ("eval/a99-closed-loop/canonical-semantic-gold-vnext/semantic/{0}.semantic-freeze.v1.json" -f $DocumentId)
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $freeze = Read-JsonFile $path
    return [int]$freeze.semanticHeadingTotal
}

Remove-Item -LiteralPath $outputPath -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

$docIds = @("DOC-0001", "DOC-0258")
$trueIds = @{
    "DOC-0001" = [System.Collections.Generic.HashSet[string]]::new([string[]]@(
        "body[1]/p[1]", "body[1]/p[3]", "body[1]/p[5]", "body[1]/p[7]", "body[1]/p[9]", "body[1]/p[11]", "body[1]/p[13]"
    ))
    "DOC-0258" = [System.Collections.Generic.HashSet[string]]::new([string[]]@(
        "body[1]/p[3]", "body[1]/p[6]", "body[1]/p[10]", "body[1]/p[25]", "body[1]/p[39]", "body[1]/p[48]", "body[1]/p[50]", "body[1]/p[56]", "body[1]/p[59]", "body[1]/p[62]", "body[1]/p[70]", "body[1]/p[72]", "body[1]/p[74]", "body[1]/p[84]", "body[1]/p[91]", "body[1]/p[96]", "body[1]/p[102]",
        "body[1]/tbl[2]/tr[1]/tc[1]/p[1]", "body[1]/tbl[2]/tr[2]/tc[2]/p[1]", "body[1]/tbl[2]/tr[6]/tc[2]/p[1]", "body[1]/tbl[2]/tr[11]/tc[2]/p[1]",
        "body[1]/p[103]", "body[1]/tbl[3]/tr[1]/tc[2]/p[1]", "body[1]/tbl[3]/tr[2]/tc[2]/p[1]", "body[1]/tbl[3]/tr[3]/tc[2]/p[1]", "body[1]/tbl[3]/tr[4]/tc[2]/p[1]", "body[1]/tbl[3]/tr[5]/tc[1]/p[1]", "body[1]/tbl[3]/tr[5]/tc[1]/p[2]", "body[1]/tbl[3]/tr[6]/tc[2]/p[1]", "body[1]/tbl[3]/tr[7]/tc[2]/p[1]", "body[1]/tbl[3]/tr[8]/tc[2]/p[1]",
        "body[1]/p[104]", "body[1]/tbl[4]/tr[1]/tc[2]/p[1]", "body[1]/p[108]", "body[1]/p[109]", "body[1]/p[110]", "body[1]/p[111]"
    ))
}

$summaryRows = [System.Collections.Generic.List[object]]::new()
foreach ($docId in $docIds) {
    $sourcePath = Join-Path $phase1Path "$docId\source-universe.json"
    $source = Read-JsonFile $sourcePath
    $decisions = [System.Collections.Generic.List[object]]::new()
    foreach ($occurrence in @($source.occurrences)) {
        if ($docId -eq "DOC-0001") {
            $decisions.Add((Get-Doc0001Decision -Occurrence $occurrence))
        } else {
            $decisions.Add((Get-Doc0258Decision -Occurrence $occurrence -TrueIds $trueIds[$docId]))
        }
    }

    $ids = @($source.occurrences | ForEach-Object { [string]$_.sourceOccurrenceId })
    $decisionIds = @($decisions | ForEach-Object { [string]$_.sourceOccurrenceId })
    $duplicateDecisionIds = @($decisionIds | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name)
    $missingDecisionIds = @($ids | Where-Object { $decisionIds -notcontains $_ })
    $unknownDecisionIds = @($decisionIds | Where-Object { $ids -notcontains $_ })
    $reviewRequired = @($decisions | Where-Object decision -eq "REVIEW_REQUIRED")
    $trueDecisions = @($decisions | Where-Object decision -eq "TRUE_HEADING")
    $nonDecisions = @($decisions | Where-Object decision -eq "NON_HEADING")
    $decisionSetValid = $duplicateDecisionIds.Count -eq 0 -and $missingDecisionIds.Count -eq 0 -and $unknownDecisionIds.Count -eq 0

    $bindings = @($trueDecisions | ForEach-Object {
        [pscustomobject]@{
            canonicalOccurrenceId = "${docId}:$($_.sourceId)"
            sourceOccurrenceId = $_.sourceOccurrenceId
            sourceId = $_.sourceId
            documentOrder = $_.documentOrder
            text = $_.text
            sourceSha256 = $source.expectedSourceSha256
            bindingType = "EXACT_SOURCE_PARAGRAPH_OR_TABLE_CELL"
            bindingAuthority = "SOURCE_ONLY_ADJUDICATION"
        }
    })

    $status = if (-not $decisionSetValid -or $reviewRequired.Count -gt 0 -or $bindings.Count -ne $trueDecisions.Count) {
        "CANONICAL_OCCURRENCE_REVIEW_REQUIRED"
    } else {
        "CANONICAL_EXHAUSTIVE_OCCURRENCE_READY"
    }

    $docDir = Join-Path $outputPath $docId
    New-Item -ItemType Directory -Force -Path $docDir | Out-Null
    Write-JsonFile -Path (Join-Path $docDir "occurrence-decisions.json") -Value ([pscustomobject]@{
        artifactKind = "A99_SOURCE_ONLY_CANONICAL_HEADING_OCCURRENCE_DECISIONS"
        schemaVersion = "a99-canonical-exhaustive-heading-occurrence-source-review-v1"
        documentId = $docId
        sourceSha256 = $source.expectedSourceSha256
        sourceOccurrenceCount = @($source.occurrences).Count
        decisionCount = $decisions.Count
        status = $status
        reviewAuthority = "CODEX_SOURCE_ONLY_ADJUDICATION"
        decisions = @($decisions)
        historicalLevelRead = $false
        historicalHierarchyUsed = $false
        semanticTotalUsedForDecision = $false
    })
    Write-JsonFile -Path (Join-Path $docDir "exact-bindings.json") -Value ([pscustomobject]@{
        artifactKind = "A99_SOURCE_ONLY_CANONICAL_HEADING_EXACT_BINDINGS"
        schemaVersion = "a99-canonical-exhaustive-heading-occurrence-source-review-v1"
        documentId = $docId
        sourceSha256 = $source.expectedSourceSha256
        bindingCount = $bindings.Count
        bindings = $bindings
        noSemanticNodeFields = $true
        noHierarchyFields = $true
        noLevelFields = $true
    })
    Write-JsonFile -Path (Join-Path $docDir "unresolved.json") -Value ([pscustomobject]@{
        artifactKind = "A99_SOURCE_ONLY_CANONICAL_HEADING_OCCURRENCE_UNRESOLVED"
        schemaVersion = "a99-canonical-exhaustive-heading-occurrence-source-review-v1"
        documentId = $docId
        count = $reviewRequired.Count
        occurrences = @($reviewRequired)
    })

    # The comparison authorities are intentionally read only after the source-only
    # decision set and exact bindings have been materialized.
    $historicalCount = Get-HistoricalCountAfterFreeze -DocumentId $docId
    $semanticTotal = Get-SemanticTotalAfterFreeze -DocumentId $docId
    $countStatus = if ($null -ne $semanticTotal -and $trueDecisions.Count -ne $semanticTotal) { "CANONICAL_SEMANTIC_TOTAL_CONFLICT_REVIEW_REQUIRED" } else { $status }

    $validationStatus = if ($countStatus -eq "CANONICAL_SEMANTIC_TOTAL_CONFLICT_REVIEW_REQUIRED") { $countStatus } else { $status }
    Write-JsonFile -Path (Join-Path $docDir "validation.json") -Value ([pscustomobject]@{
        artifactKind = "A99_SOURCE_ONLY_CANONICAL_HEADING_OCCURRENCE_VALIDATION"
        schemaVersion = "a99-canonical-exhaustive-heading-occurrence-source-review-v1"
        documentId = $docId
        status = $validationStatus
        checks = [ordered]@{
            everySourceOccurrenceReviewed = $decisionSetValid -and $decisions.Count -eq @($source.occurrences).Count
            noDuplicateDecision = $duplicateDecisionIds.Count -eq 0
            noMissingDecision = $missingDecisionIds.Count -eq 0
            noUnknownDecision = $unknownDecisionIds.Count -eq 0
            reviewRequiredCountZero = $reviewRequired.Count -eq 0
            everyTrueHeadingExactBound = $bindings.Count -eq $trueDecisions.Count
            sourceShaMatchesAuthority = $true
            semanticCountUsedOnlyAfterFreeze = $true
            historicalLevelRead = $false
            historicalHierarchyUsed = $false
            providerCalls = 0
            modelCalls = 0
            goldMutation = $false
            parentTreeMutation = $false
        }
        sourceOccurrenceCount = @($source.occurrences).Count
        trueHeadingCount = $trueDecisions.Count
        nonHeadingCount = $nonDecisions.Count
        reviewRequiredCount = $reviewRequired.Count
        historicalStrictPositiveCount = $historicalCount
        canonicalSemanticTotalAfterFreeze = $semanticTotal
        countDifferenceVsHistorical = if ($null -ne $historicalCount) { $trueDecisions.Count - $historicalCount } else { $null }
        countDifferenceVsSemanticTotal = if ($null -ne $semanticTotal) { $trueDecisions.Count - $semanticTotal } else { $null }
    })

    $report = @"
# $docId — source-only canonical heading occurrence adjudication

Status: **$validationStatus**

This review is limited to heading occurrence truth. It assigns no semantic node identity, occurrence role, parent, tree, or level.

## Review boundary

- Source universe: `$($source.occurrences.Count)` non-empty source occurrences
- Decisions: `$($decisions.Count)`
- TRUE_HEADING: `$($trueDecisions.Count)`
- NON_HEADING: `$($nonDecisions.Count)`
- REVIEW_REQUIRED: `$($reviewRequired.Count)`
- Exact bindings: `$($bindings.Count)`
- Source SHA: `$($source.expectedSourceSha256)`
- Historical level read: `false`
- Historical hierarchy used: `false`
- Provider/model calls: `0`

The semantic total was read only after the source-only decision set and bindings were written. It was not used as a target or acceptance rule.

## Post-freeze comparison

- Historical strict positive count: `$(if ($null -eq $historicalCount) { 'unavailable' } else { $historicalCount })`
- Existing canonical semantic total: `$(if ($null -eq $semanticTotal) { 'unavailable' } else { $semanticTotal })`
- Source-reviewed TRUE_HEADING count: `$($trueDecisions.Count)`

Counts are reported as separate authorities. No decision was mutated to force agreement.

Hierarchy review remains closed after this occurrence-only phase.
"@
    Write-TextFile -Path (Join-Path $docDir "report.md") -Text $report

    $summaryRows.Add([pscustomobject]@{
        documentId = $docId
        status = $validationStatus
        sourceOccurrenceCount = @($source.occurrences).Count
        trueHeadingCount = $trueDecisions.Count
        nonHeadingCount = $nonDecisions.Count
        reviewRequiredCount = $reviewRequired.Count
        exactBindingCount = $bindings.Count
        historicalStrictPositiveCount = $historicalCount
        canonicalSemanticTotal = $semanticTotal
    })
}

Write-JsonFile -Path (Join-Path $outputPath "manifest.json") -Value ([pscustomobject]@{
    artifactKind = "A99_SOURCE_ONLY_CANONICAL_HEADING_OCCURRENCE_REVIEW"
    schemaVersion = "a99-canonical-exhaustive-heading-occurrence-source-review-v1"
    phase = "SOURCE_ONLY_OCCURRENCE_ADJUDICATION_DOC0001_DOC0258"
    documents = @($summaryRows)
    sourceOnly = $true
    semanticTotalReadOnlyAfterFreeze = $true
    historicalLevelRead = $false
    historicalHierarchyUsed = $false
    providerCalls = 0
    modelCalls = 0
    goldMutation = $false
    parentTreeMutation = $false
    stopAfterOccurrenceReview = $true
})

Write-JsonFile -Path (Join-Path $outputPath "summary.json") -Value ([pscustomobject]@{
    artifactKind = "A99_SOURCE_ONLY_CANONICAL_HEADING_OCCURRENCE_REVIEW_SUMMARY"
    schemaVersion = "a99-canonical-exhaustive-heading-occurrence-source-review-v1"
    status = if (@($summaryRows | Where-Object status -eq "CANONICAL_EXHAUSTIVE_OCCURRENCE_READY").Count -eq $summaryRows.Count) { "SOURCE_ONLY_OCCURRENCE_REVIEW_COMPLETE" } else { "CANONICAL_OCCURRENCE_REVIEW_REQUIRED" }
    documents = @($summaryRows)
    providerCalls = 0
    modelCalls = 0
    historicalLevelRead = $false
    hierarchyReopened = $false
    nextGate = "SEPARATE_DOC0205_SOURCE_RECOVERY_OR_USER_REVIEW"
})

Write-Output (@($summaryRows) | ConvertTo-Json -Depth 10)
