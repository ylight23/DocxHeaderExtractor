[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$InputRoot = 'artifacts/authority-audit/strict-heading-canonical-hierarchy-review-v2/packets',
    [string]$OutputRoot = 'artifacts/authority-audit/strict-heading-canonical-hierarchy-proposals-v1'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = (Resolve-Path $RepoRoot).Path
$inputDir = Join-Path $repo $InputRoot
$out = Join-Path $repo $OutputRoot
New-Item -ItemType Directory -Force -Path $out | Out-Null

function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Write-Json([object]$Value, [string]$Path, [int]$Depth = 40) {
    $Value | ConvertTo-Json -Depth $Depth | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}
function Set-Children([hashtable]$Map, [string]$Parent, [int[]]$Children) {
    foreach ($child in $Children) { $Map[('H{0:D4}' -f $child)] = $Parent }
}
function Parent-Map([string]$DocumentId, [int]$Count) {
    $map = @{}
    switch ($DocumentId) {
        'DOC-0252' {
            Set-Children $map 'ROOT' @(1,2,11,14,18,25,26,27)
            Set-Children $map 'H0002' @(3,4)
            Set-Children $map 'H0004' @(5,6,7,8,9,10)
            Set-Children $map 'H0011' @(12,13)
            Set-Children $map 'H0014' @(15,16,17)
            Set-Children $map 'H0018' @(19,20,21,22,23,24)
        }
        'DOC-0256' {
            Set-Children $map 'ROOT' @(1,2,9,17,18,19,24)
            Set-Children $map 'H0002' @(3,4,5,6,7,8)
            Set-Children $map 'H0009' @(10,11,12,13,14,15,16)
            Set-Children $map 'H0019' @(20,21,22,23)
        }
        'DOC-0258' {
            Set-Children $map 'ROOT' @(1)
            Set-Children $map 'H0001' @(2,10,18,19,22,23,24)
            Set-Children $map 'H0002' @(3,4,5,6,7,8,9)
            Set-Children $map 'H0010' @(11,12,13,14,15,16,17)
            Set-Children $map 'H0019' @(20,21)
        }
        'DOC-0205' {
            Set-Children $map 'ROOT' @(1,10,37,63,68)
            Set-Children $map 'H0001' @(2,3,4,5,6,7,8,9)
            Set-Children $map 'H0010' @(11,17,21,27)
            Set-Children $map 'H0011' @(12,13,14,15,16)
            Set-Children $map 'H0017' @(18,19,20)
            Set-Children $map 'H0021' @(22,23,24,25,26)
            Set-Children $map 'H0027' @(28,29,30,31,32,33,34,35,36)
            Set-Children $map 'H0037' @(38,41,45,50,58)
            Set-Children $map 'H0038' @(39,40)
            Set-Children $map 'H0041' @(42,43,44)
            Set-Children $map 'H0045' @(46,47,48,49)
            Set-Children $map 'H0050' @(51,52,53,54,55,56,57)
            Set-Children $map 'H0058' @(59,60,61,62)
            Set-Children $map 'H0063' @(64,65,66,67)
            Set-Children $map 'H0068' @(69,70,71)
        }
        'DOC-0243' {
            Set-Children $map 'ROOT' @(1,26,51,76,101,102)
            Set-Children $map 'H0001' @(2,8,14,20)
            Set-Children $map 'H0002' @(3,4,5,6,7)
            Set-Children $map 'H0008' @(9,10,11,12,13)
            Set-Children $map 'H0014' @(15,16,17,18,19)
            Set-Children $map 'H0020' @(21,22,23,24,25)
            Set-Children $map 'H0026' @(27,33,39,45)
            Set-Children $map 'H0027' @(28,29,30,31,32)
            Set-Children $map 'H0033' @(34,35,36,37,38)
            Set-Children $map 'H0039' @(40,41,42,43,44)
            Set-Children $map 'H0045' @(46,47,48,49,50)
            Set-Children $map 'H0051' @(52,58,64,70)
            Set-Children $map 'H0052' @(53,54,55,56,57)
            Set-Children $map 'H0058' @(59,60,61,62,63)
            Set-Children $map 'H0064' @(65,66,67,68,69)
            Set-Children $map 'H0070' @(71,72,73,74,75)
            Set-Children $map 'H0076' @(77,83,89,95)
            Set-Children $map 'H0077' @(78,79,80,81,82)
            Set-Children $map 'H0083' @(84,85,86,87,88)
            Set-Children $map 'H0089' @(90,91,92,93,94)
            Set-Children $map 'H0095' @(96,97,98,99,100)
        }
    }
    return $map
}

function New-DocumentProposal([object]$Packet, [string]$PacketPath) {
    $doc = [string]$Packet.documentId
    $occurrences = @($Packet.headingOccurrences)
    $count = $occurrences.Count
    $parentByHeading = Parent-Map $doc $count
    $completeParentMap = ($parentByHeading.Count -eq $count -and @($parentByHeading.Keys).Count -eq $count)
    $needsReview = -not $completeParentMap
    $status = if ($needsReview) { 'REVIEW_REQUIRED_CANONICAL_HIERARCHY' } else { 'HISTORICAL_STRICT_HIERARCHY_PROPOSAL' }

    $assignments = @()
    $nodes = @()
    for ($i = 0; $i -lt $count; $i++) {
        $h = 'H{0:D4}' -f ($i + 1)
        $n = 'N{0:D3}' -f ($i + 1)
        $assignments += [ordered]@{ headingOccurrenceRef=$h; semanticNodeRef=$n; occurrenceRole='PRIMARY' }
        $nodes += [ordered]@{ semanticNodeRef=$n; canonicalOccurrenceRef=$h }
    }
    $edges = @()
    if ($completeParentMap) {
        for ($i = 0; $i -lt $count; $i++) {
            $h = 'H{0:D4}' -f ($i + 1)
            $n = 'N{0:D3}' -f ($i + 1)
            $parentH = [string]$parentByHeading[$h]
            $parentN = if ($parentH -eq 'ROOT') { 'ROOT' } else { 'N{0:D3}' -f ([int]$parentH.Substring(1)) }
            $edges += [ordered]@{ childSemanticNodeRef=$n; parentSemanticNodeRef=$parentN }
        }
    }

    $ambiguities = @()
    if ($needsReview) {
        $ambiguities += [ordered]@{
            ambiguityId='A-PARENT-STRUCTURE-001'
            type='PARENT_STRUCTURE_UNRESOLVED'
            documentId=$doc
            occurrenceRefs=@($occurrences | ForEach-Object { $_.reviewRef })
            reason='The frozen packet exposes source-backed heading text/order and nearby heading context, but does not provide enough non-historical structural evidence to resolve the complete semantic-node parent tree without additional source inspection.'
            requiredAction='USER_SOURCE_REVIEW_REQUIRED'
        }
    }

    $sourceSha = Sha $PacketPath
    $reviewed = [ordered]@{
        artifactKind='a99_strict_heading_canonical_hierarchy_source_backed_proposal'
        schemaVersion='a99-strict-heading-canonical-hierarchy-proposal-v1'
        documentId=$doc
        status=$status
        reviewAuthority='CODEX_SOURCE_BACKED_PROPOSAL_NOT_HUMAN_REVIEWED'
        approvalRequired=$true
        approvedByUser=$false
        sourcePacket=(Join-Path $InputRoot (Split-Path $PacketPath -Leaf)).Replace('\','/')
        sourcePacketSha256=$sourceSha
        strictHeadingGoldArtifact=[string]$Packet.strictHeadingGoldArtifact
        strictHeadingGoldSha256=[string]$Packet.strictHeadingGoldSha256
        firewall=[ordered]@{
            historicalLevelReadDuringAdjudication=$false
            historicalParentRead=$false
            identityPredictionRead=$false
            hierarchyPredictionRead=$false
            providerCalls=0
            modelCalls=0
            strictHeadingGoldMutation=$false
            reviewPacketMutation=$false
        }
        headingOccurrences=$occurrences
        adjudication=[ordered]@{ occurrenceAssignments=$assignments; semanticNodes=$nodes; parentEdges=$edges }
    }

    $docOut = Join-Path $out $doc
    New-Item -ItemType Directory -Force -Path $docOut | Out-Null
    Write-Json $reviewed (Join-Path $docOut 'source-review-adjudication.proposal.json')
    Write-Json ([ordered]@{
        artifactKind='a99_canonical_semantic_nodes_proposal'
        schemaVersion='a99-canonical-semantic-nodes-proposal-v1'
        documentId=$doc; status=$status; sourcePacketSha256=$sourceSha
        semanticNodes=$nodes; occurrenceAssignments=$assignments
    }) (Join-Path $docOut 'semantic-nodes.proposal.json')
    Write-Json ([ordered]@{
        artifactKind='a99_canonical_parent_edges_proposal'
        schemaVersion='a99-canonical-parent-edges-proposal-v1'
        documentId=$doc; status=$status; rootRef='ROOT'; sourcePacketSha256=$sourceSha
        parentEdges=$edges
    }) (Join-Path $docOut 'parent-edges.proposal.json')

    $derivedRows = @()
    $maxDepth = 0
    if ($completeParentMap) {
        $parentByNode = @{}
        foreach ($edge in $edges) { $parentByNode[[string]$edge.childSemanticNodeRef] = [string]$edge.parentSemanticNodeRef }
        foreach ($node in $nodes) {
            $nodeRef = [string]$node.semanticNodeRef; $current = $nodeRef; $depth = 0; $seen=@{}
            while ($parentByNode[$current] -ne 'ROOT') {
                if ($seen.ContainsKey($current)) { throw "Cycle while deriving $doc/$current" }
                $seen[$current]=$true; $depth++; $current=$parentByNode[$current]
            }
            $depth++
            if ($depth -gt $maxDepth) { $maxDepth=$depth }
            $derivedRows += [ordered]@{ semanticNodeRef=$nodeRef; parentSemanticNodeRef=$parentByNode[$nodeRef]; depth=$depth; derivedLevel=$depth; occurrenceRefs=@($assignments | Where-Object semanticNodeRef -eq $nodeRef | ForEach-Object headingOccurrenceRef) }
        }
    }
    Write-Json ([ordered]@{
        artifactKind='a99_canonical_derived_levels_proposal'
        schemaVersion='a99-canonical-derived-levels-proposal-v1'
        documentId=$doc; status=$status; sourcePacketSha256=$sourceSha
        derivation='depth(ROOT)=0; level is derived only after a valid semantic-node tree'
        nodes=$derivedRows; maxDepth=$maxDepth
    }) (Join-Path $docOut 'derived-levels.proposal.json')
    Write-Json $ambiguities (Join-Path $docOut 'ambiguities.json')

    $validator = Join-Path $repo 'tools/validate-strict-heading-canonical-hierarchy-review-v2.ps1'
    $validationPath = Join-Path $docOut 'validation.json'
    & pwsh -NoProfile -File $validator -PacketPath (Join-Path $docOut 'source-review-adjudication.proposal.json') -OutputPath $validationPath | Out-Null
    $validation = Get-Content -LiteralPath $validationPath -Raw | ConvertFrom-Json
    if (-not $needsReview -and [string]$validation.status -ne 'VALID') { throw "Unexpected validation failure for $doc." }

    $manifest = [ordered]@{
        artifactKind='A99_STRICT_HEADING_CANONICAL_HIERARCHY_PROPOSAL_V1'
        schemaVersion='a99-strict-heading-canonical-hierarchy-proposal-v1-manifest'
        documentId=$doc; status=$status
        sourcePacketSha256=$sourceSha; strictHeadingGoldSha256=[string]$Packet.strictHeadingGoldSha256
        occurrenceCount=$count; semanticNodeCount=$nodes.Count
        primaryCount=@($assignments | Where-Object occurrenceRole -eq 'PRIMARY').Count
        repeatCount=0; continuationCount=0; collapsedOccurrenceCount=0
        parentEdgeCount=$edges.Count; rootChildCount=@($edges | Where-Object parentSemanticNodeRef -eq 'ROOT').Count
        maxDepth=$maxDepth; validatorStatus=[string]$validation.status
        ambiguityCount=$ambiguities.Count; proposalStatus=$status
        reviewAuthority='CODEX_SOURCE_BACKED_PROPOSAL_NOT_HUMAN_REVIEWED'
        providerCalls=0; modelCalls=0; historicalLevelRead=$false
    }
    Write-Json $manifest (Join-Path $docOut 'manifest.json')
    $report = "# $doc canonical hierarchy proposal`n`nStatus: **$status**`n`nOccurrences: $count`nSemantic nodes: $($nodes.Count)`nPRIMARY: $($manifest.primaryCount)`nREPEAT: 0`nCONTINUATION: 0`nParent edges: $($edges.Count)`nROOT children: $($manifest.rootChildCount)`nMax derived depth: $maxDepth`nValidator: $($validation.status)`nAmbiguities: $($ambiguities.Count)`n`nThis is a Codex source-backed proposal, not human-reviewed Gold. Historical level and prior hierarchy predictions were not read.`n"
    $report | Set-Content -LiteralPath (Join-Path $docOut 'report.md') -Encoding utf8NoBOM
    return [pscustomobject]@{ documentId=$doc; packetPath=(Join-Path $InputRoot (Split-Path $PacketPath -Leaf)).Replace('\','/'); sourcePacketSha256=$sourceSha; status=$status; occurrenceCount=$count; semanticNodeCount=$nodes.Count; primaryCount=$manifest.primaryCount; repeatCount=0; continuationCount=0; collapsedOccurrenceCount=0; parentEdgeCount=$edges.Count; rootChildCount=$manifest.rootChildCount; maxDepth=$maxDepth; validatorStatus=[string]$validation.status; ambiguityCount=$ambiguities.Count; proposalStatus=$status; needsReview=$needsReview }
}

$packets = @(Get-ChildItem $inputDir -Filter 'DOC-*.json' -File | ForEach-Object { $j=Get-Content -LiteralPath $_.FullName -Raw|ConvertFrom-Json; [pscustomobject]@{ Path=$_.FullName; Json=$j; Count=@($j.headingOccurrences).Count } } | Sort-Object Count, @{Expression={$_.Json.documentId}})
$remaining = @($packets | Where-Object { $_.Json.documentId -ne 'DOC-0001' })
$rows = [System.Collections.Generic.List[object]]::new()
foreach ($packet in $remaining) { $rows.Add((New-DocumentProposal -Packet $packet.Json -PacketPath $packet.Path)) }

$pilotManifestPath = Join-Path $repo 'artifacts/authority-audit/strict-heading-canonical-hierarchy-gold-v1/DOC-0001/manifest.json'
$pilot = Get-Content -LiteralPath $pilotManifestPath -Raw | ConvertFrom-Json
$allRows = @([pscustomobject]@{ documentId='DOC-0001'; packetPath='artifacts/authority-audit/strict-heading-canonical-hierarchy-review-v2/packets/DOC-0001.canonical-hierarchy-review.v2.json'; sourcePacketSha256=[string]$pilot.sourcePacketSha256; status=[string]$pilot.status; occurrenceCount=[int]$pilot.occurrenceCount; semanticNodeCount=[int]$pilot.semanticNodeCount; primaryCount=[int]$pilot.primaryCount; repeatCount=[int]$pilot.repeatCount; continuationCount=[int]$pilot.continuationCount; collapsedOccurrenceCount=0; parentEdgeCount=[int]$pilot.parentEdgeCount; rootChildCount=[int]$pilot.rootChildCount; maxDepth=[int]$pilot.maxDepth; validatorStatus=[string]$pilot.validatorStatus; ambiguityCount=0; proposalStatus=[string]$pilot.status; needsReview=$false }) + @($rows)
$batchStatus = if (@($rows | Where-Object needsReview).Count -eq 0) { 'READY_FOR_BATCH_USER_APPROVAL' } else { 'READY_FOR_PARTIAL_USER_APPROVAL_WITH_REVIEW_QUEUE' }
$summary = [ordered]@{
    artifactKind='A99_STRICT_HEADING_CANONICAL_HIERARCHY_PROPOSALS_V1_BATCH'
    schemaVersion='a99-strict-heading-canonical-hierarchy-proposals-v1-batch-summary'
    status=$batchStatus
    reviewAuthority='CODEX_SOURCE_BACKED_PROPOSALS_NOT_HUMAN_REVIEWED'
    documentCount=9; headingOccurrences=[int](($allRows | Measure-Object -Property occurrenceCount -Sum).Sum)
    semanticNodes=[int](($allRows | Measure-Object -Property semanticNodeCount -Sum).Sum)
    primary=[int](($allRows | Measure-Object -Property primaryCount -Sum).Sum)
    repeat=[int](($allRows | Measure-Object -Property repeatCount -Sum).Sum)
    continuation=[int](($allRows | Measure-Object -Property continuationCount -Sum).Sum)
    parentEdges=[int](($allRows | Measure-Object -Property parentEdgeCount -Sum).Sum)
    ambiguousDocuments=@($rows | Where-Object needsReview).Count
    ambiguousDecisions=[int](($rows | Measure-Object -Property ambiguityCount -Sum).Sum)
    providerCalls=0; modelCalls=0; historicalLevelRead=$false
    doc0001ImportedByReference=$true; doc0001ManifestSha256=Sha $pilotManifestPath
    documents=$allRows
}
Write-Json $summary (Join-Path $out 'batch-summary.json')

$queue = [System.Collections.Generic.List[object]]::new()
foreach ($row in $allRows) {
    if ($row.needsReview) { $queue.Add([ordered]@{ type='AMBIGUOUS_PARENT_STRUCTURE'; documentId=$row.documentId; reason='Complete semantic-node parent tree requires additional source review.' }) }
    if ([int]$row.rootChildCount -gt 1) { $queue.Add([ordered]@{ type='MULTI_ROOT_TOP_LEVEL_STRUCTURE'; documentId=$row.documentId; rootChildCount=$row.rootChildCount; reason='Multiple semantic nodes attach directly to the synthetic ROOT; confirm document scope.' }) }
}
Write-Json @($queue) (Join-Path $out 'user-review-queue.json')
$perDocumentReport = ($allRows | ForEach-Object {
    "- $($_.documentId): occurrences=$($_.occurrenceCount), semanticNodes=$($_.semanticNodeCount), PRIMARY=$($_.primaryCount), REPEAT=$($_.repeatCount), CONTINUATION=$($_.continuationCount), parentEdges=$($_.parentEdgeCount), ROOTChildren=$($_.rootChildCount), maxDepth=$($_.maxDepth), validator=$($_.validatorStatus), ambiguities=$($_.ambiguityCount), status=$($_.proposalStatus)"
}) -join "`n"
$report = @"
# Canonical hierarchy source-backed proposal batch

Status: **$batchStatus**

This lane contains source-backed Codex proposals only. It is not human-reviewed
Gold. DOC-0001 is imported by manifest/hash reference and remains governed by
its existing approval status; it was not overwritten.

Documents: 9
Heading occurrences: $($summary.headingOccurrences)
Semantic nodes proposed: $($summary.semanticNodes)
PRIMARY: $($summary.primary)
REPEAT: $($summary.repeat)
CONTINUATION: $($summary.continuation)
Parent edges proposed: $($summary.parentEdges)
Documents requiring review: $($summary.ambiguousDocuments)
Ambiguous decisions: $($summary.ambiguousDecisions)
Review queue entries: $($queue.Count)

## Per-document summary

$perDocumentReport

Historical level, historical hierarchy, model/provider outputs, and identity
evaluation labels were not read. No provider calls were made. No strict Gold or
v2 source packet was mutated.

User approval is required before any proposal is promoted to canonical
hierarchy Gold. Historical-level compatibility comparison remains blocked until
that approval.
"@
$report | Set-Content -LiteralPath (Join-Path $out 'batch-report.md') -Encoding utf8NoBOM
Write-Output "Proposal batch prepared: documents=$($allRows.Count) occurrences=$($summary.headingOccurrences) status=$batchStatus queue=$($queue.Count)"
