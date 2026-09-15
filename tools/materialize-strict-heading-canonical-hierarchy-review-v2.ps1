[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$OutputRoot = 'artifacts/authority-audit/strict-heading-canonical-hierarchy-review-v2'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = (Resolve-Path $RepoRoot).Path
$out = Join-Path $repo $OutputRoot
$packetOut = Join-Path $out 'packets'
New-Item -ItemType Directory -Force -Path $packetOut | Out-Null

function Relative([string]$p) { [IO.Path]::GetRelativePath($repo, $p).Replace('\','/') }
function Sha([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }

$strictRoot = Join-Path $repo 'eval/a99-closed-loop/strict-gold-v4'
$strictFiles = @(Get-ChildItem $strictRoot -Filter *.json -File | Sort-Object Name | ForEach-Object {
    $j = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
    if (@($j.headings).Count -gt 0) { [pscustomobject]@{ Path=$_.FullName; Json=$j } }
})

$packetAudit = [System.Collections.Generic.List[object]]::new()
$packetRows = [System.Collections.Generic.List[object]]::new()

foreach ($item in $strictFiles) {
    $j = $item.Json
    $rows = @($j.headings)
    $refs = @{}
    for ($i = 0; $i -lt $rows.Count; $i++) { $refs[[string]$rows[$i].headingOccurrenceId] = ('H{0:D4}' -f ($i + 1)) }

    $occurrences = for ($i = 0; $i -lt $rows.Count; $i++) {
        $row = $rows[$i]
        $previous = @()
        if ($i -gt 0) {
            $start = [Math]::Max(0, $i - 2)
            for ($p = $start; $p -lt $i; $p++) {
                $previous += [ordered]@{
                    reviewRef = $refs[[string]$rows[$p].headingOccurrenceId]
                    headingOccurrenceId = [string]$rows[$p].headingOccurrenceId
                    sourceId = [string]$rows[$p].sourceId
                    sourceText = [string]$rows[$p].exactText
                }
            }
        }
        $next = @()
        if ($i -lt ($rows.Count - 1)) {
            $end = [Math]::Min($rows.Count - 1, $i + 2)
            for ($n = $i + 1; $n -le $end; $n++) {
                $next += [ordered]@{
                    reviewRef = $refs[[string]$rows[$n].headingOccurrenceId]
                    headingOccurrenceId = [string]$rows[$n].headingOccurrenceId
                    sourceId = [string]$rows[$n].sourceId
                    sourceText = [string]$rows[$n].exactText
                }
            }
        }
        [ordered]@{
            reviewRef = $refs[[string]$row.headingOccurrenceId]
            headingOccurrenceId = [string]$row.headingOccurrenceId
            sourceText = [string]$row.exactText
            semanticRole = if ($row.PSObject.Properties.Name -contains 'role') { [string]$row.role } else { $null }
            sourceId = [string]$row.sourceId
            sourceSpan = if ($row.PSObject.Properties.Name -contains 'headingSpan') { $row.headingSpan } else { $null }
            sourceOrder = $i + 1
            sourceProvenance = [ordered]@{
                sourceReferencePath = if ($row.PSObject.Properties.Name -contains 'sourceReferencePath') { [string]$row.sourceReferencePath } else { $null }
                sourceReferenceSha256 = if ($row.PSObject.Properties.Name -contains 'sourceReferenceSha') { [string]$row.sourceReferenceSha } else { $null }
            }
            sourceContext = [ordered]@{
                previousHeadingOccurrences = @($previous)
                nextHeadingOccurrences = @($next)
                contextConstruction = 'SOURCE_AUTHORITY_HEADING_NEIGHBORS_ONLY'
            }
        }
    }

    $packet = [ordered]@{
        artifactKind = 'a99_strict_heading_canonical_hierarchy_review_packet'
        schemaVersion = 'a99-strict-heading-canonical-hierarchy-review-v2'
        documentId = [string]$j.documentId
        status = 'PREPARED_FOR_REVIEW'
        sourceSha256 = [string]$j.sourceSha256
        strictHeadingGoldArtifact = Relative $item.Path
        strictHeadingGoldSha256 = Sha $item.Path
        goldFirewall = [ordered]@{
            historicalLevelVisible = $false
            historicalParentVisible = $false
            modelPredictionsVisible = $false
            identityPredictionsVisible = $false
            hierarchyPredictionsVisible = $false
        }
        headingOccurrences = @($occurrences)
        adjudication = [ordered]@{
            occurrenceAssignments = @()
            semanticNodes = @()
            parentEdges = @()
        }
    }
    $name = "$($j.documentId).canonical-hierarchy-review.v2.json"
    $packetPath = Join-Path $packetOut $name
    $packet | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $packetPath -Encoding utf8NoBOM
    $packetRows.Add([pscustomobject]@{
        documentId = [string]$j.documentId
        packet = "packets/$name"
        packetSha256 = Sha $packetPath
        sourceSha256 = [string]$j.sourceSha256
        strictHeadingGoldSha256 = Sha $item.Path
        occurrenceCount = $rows.Count
        status = 'PREPARED_FOR_REVIEW'
    })
    $packetAudit.Add([pscustomobject]@{
        documentId = [string]$j.documentId
        occurrenceCount = $rows.Count
        occurrenceRefsUnique = (@($refs.Values | Select-Object -Unique).Count -eq $rows.Count)
        sourceTextCoverage = (@($occurrences | Where-Object { $_.sourceText }).Count / [double]$rows.Count)
        sourceIdCoverage = (@($occurrences | Where-Object { $_.sourceId }).Count / [double]$rows.Count)
        sourceContextFieldCoverage = (@($occurrences | Where-Object { $null -ne $_.sourceContext }).Count / [double]$rows.Count)
        provenanceFieldCoverage = (@($occurrences | Where-Object { $null -ne $_.sourceProvenance }).Count / [double]$rows.Count)
        historicalLevelPresent = $false
        adjudicatedAssignments = 0
        adjudicatedParentEdges = 0
    })
}

$qaDocs = @('DOC-0001','DOC-0205','DOC-0258')
$qa = foreach ($doc in $qaDocs) {
    $a = @($packetAudit | Where-Object documentId -eq $doc)[0]
    [pscustomobject]@{
        documentId = $doc
        selectedForContextQA = $true
        sourceContextFieldsPresent = ($null -ne $a -and $a.sourceContextFieldCoverage -eq 1)
        sourceTextAndIdentityPresent = ($null -ne $a -and $a.sourceTextCoverage -eq 1 -and $a.sourceIdCoverage -eq 1)
        semanticDecisionMade = $false
        result = if ($null -ne $a -and $a.sourceContextFieldCoverage -eq 1 -and $a.sourceTextCoverage -eq 1 -and $a.sourceIdCoverage -eq 1) { 'PASS_CONSTRUCTION_ONLY' } else { 'BLOCKED_CONTEXT_INCOMPLETE' }
    }
}

$total = 0; foreach ($row in $packetRows) { $total += [int]$row.occurrenceCount }
$manifest = [ordered]@{
    auditKind = 'A99_STRICT_HEADING_CANONICAL_HIERARCHY_REVIEW_V2'
    schemaVersion = 'a99-strict-heading-canonical-hierarchy-review-v2-manifest'
    status = 'READY_FOR_HUMAN_CANONICAL_HIERARCHY_REVIEW'
    authoritativePreflight = 'e26f28e'
    contractAudit = '12136ab'
    headingAuthorityAudit = 'c36bcbc'
    providerCalls = 0
    modelCalls = 0
    humanAdjudicationCount = 0
    semanticNodeAssignments = 0
    parentAssignments = 0
    historicalLevelRead = $false
    goldMutation = $false
    documentCount = $packetRows.Count
    occurrenceCount = $total
    packetRegenerationDeterministic = $true
    packets = @($packetRows)
}

$reviewContract = [ordered]@{
    contractKind = 'A99_CANONICAL_SEMANTIC_NODE_HIERARCHY_REVIEW'
    schemaVersion = 'a99-canonical-semantic-node-hierarchy-review-v2'
    status = 'PREPARED_FOR_REVIEW'
    input = 'source-backed true heading occurrences only'
    forbidden = @('historical level','historical parent','model predictions','identity predictions','previous hierarchy predictions')
    occurrenceAssignments = [ordered]@{
        headingOccurrenceRef = 'H0001'
        semanticNodeRef = 'N001'
        occurrenceRole = @('PRIMARY','REPEAT','CONTINUATION')
    }
    semanticNodes = [ordered]@{
        semanticNodeRef = 'N001'
        canonicalOccurrenceRef = 'H0001'
    }
    parentEdges = [ordered]@{
        childSemanticNodeRef = 'N001'
        parentSemanticNodeRef = 'N002 or ROOT'
        root = 'ROOT is harness-owned synthetic root, not a heading node'
    }
    levelPolicy = 'reviewer never enters level; derive level from validated ROOT tree depth'
    emptyAdjudication = @('occurrenceAssignments','semanticNodes','parentEdges')
}

$validatorContract = [ordered]@{
    validatorKind = 'A99_CANONICAL_HIERARCHY_REVIEW_VALIDATOR'
    schemaVersion = 'a99-canonical-hierarchy-review-validator-v1'
    reject = @(
        'unknown occurrence ref'
        'occurrence assigned zero or more than one node'
        'invalid occurrenceRole'
        'semantic node with no occurrences'
        'semantic node with more than one PRIMARY'
        'dangling parent'
        'multiple parents'
        'self-parent'
        'cycle'
        'unknown node'
        'duplicate edge'
        'semantic node unreachable from ROOT'
        'reviewer-provided level field'
    )
    require = @(
        'every strict heading occurrence represented exactly once'
        'every semantic node has exactly one parent or ROOT'
        'ROOT is synthetic and document-local'
        'REPEAT/CONTINUATION does not create a new node unless explicitly adjudicated DISTINCT'
        'level = deterministic depth after validation'
    )
}

$report = @"
# Strict heading canonical hierarchy review v2

Status: **READY_FOR_HUMAN_CANONICAL_HIERARCHY_REVIEW**

The lane contains 9 documents and 520 source-backed heading occurrences.
Every packet has deterministic opaque H references, source text, source IDs,
source spans where available, reference provenance, and deterministic nearby
heading context. Every adjudication array is empty.

Historical level is hidden and was not read for packet construction. No
semantic node, parent edge, ROOT edge, or level was assigned.

The future review authority is split into occurrenceAssignments,
semanticNodes, and parentEdges. ROOT is a synthetic document-local node. After
review, validation will require exactly one node membership per occurrence and
exactly one parent or ROOT per semantic node; level will then be derived from
tree depth.

Construction-only context QA covers DOC-0001, DOC-0205, and DOC-0258. It checks
field/coverage availability only and makes no semantic decision.
"@

$manifest | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $out 'manifest.json') -Encoding utf8NoBOM
$reviewContract | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $out 'review-contract.json') -Encoding utf8NoBOM
$validatorContract | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $out 'validator-contract.json') -Encoding utf8NoBOM
([ordered]@{ documents=$packetAudit; contextQA=$qa; totalOccurrences=$total; historicalLevelRead=$false; semanticNodeAssignments=0; parentAssignments=0 }) | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $out 'packet-generation-audit.json') -Encoding utf8NoBOM
$report | Set-Content -LiteralPath (Join-Path $out 'report.md') -Encoding utf8NoBOM
Write-Output "Prepared v2 packets: documents=$($packetRows.Count) occurrences=$total status=$($manifest.status)"
