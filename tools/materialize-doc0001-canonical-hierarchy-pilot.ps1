[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$InputPacket = 'artifacts/authority-audit/strict-heading-canonical-hierarchy-review-v2/packets/DOC-0001.canonical-hierarchy-review.v2.json',
    [string]$OutputRoot = 'artifacts/authority-audit/strict-heading-canonical-hierarchy-gold-v1/DOC-0001'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = (Resolve-Path $RepoRoot).Path
$inputPath = Join-Path $repo $InputPacket
$out = Join-Path $repo $OutputRoot
New-Item -ItemType Directory -Force -Path $out | Out-Null

function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Write-Json([object]$Value, [string]$Path, [int]$Depth = 30) {
    $Value | ConvertTo-Json -Depth $Depth | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

$packet = Get-Content -LiteralPath $inputPath -Raw | ConvertFrom-Json
if ([string]$packet.documentId -ne 'DOC-0001') { throw 'Pilot is restricted to DOC-0001.' }
$occurrences = @($packet.headingOccurrences)
if ($occurrences.Count -ne 7) { throw "DOC-0001 packet must contain 7 occurrences; found $($occurrences.Count)." }

# Source-only pilot review. These decisions use the packet's heading text, order,
# and neighboring source context. Historical level and any prior hierarchy output
# are intentionally not read.
$assignments = @(
    [ordered]@{ headingOccurrenceRef='H0001'; semanticNodeRef='N001'; occurrenceRole='PRIMARY' },
    [ordered]@{ headingOccurrenceRef='H0002'; semanticNodeRef='N002'; occurrenceRole='PRIMARY' },
    [ordered]@{ headingOccurrenceRef='H0003'; semanticNodeRef='N003'; occurrenceRole='PRIMARY' },
    [ordered]@{ headingOccurrenceRef='H0004'; semanticNodeRef='N004'; occurrenceRole='PRIMARY' },
    [ordered]@{ headingOccurrenceRef='H0005'; semanticNodeRef='N005'; occurrenceRole='PRIMARY' },
    [ordered]@{ headingOccurrenceRef='H0006'; semanticNodeRef='N006'; occurrenceRole='PRIMARY' },
    [ordered]@{ headingOccurrenceRef='H0007'; semanticNodeRef='N007'; occurrenceRole='PRIMARY' }
)
$semanticNodes = @(
    [ordered]@{ semanticNodeRef='N001'; canonicalOccurrenceRef='H0001' },
    [ordered]@{ semanticNodeRef='N002'; canonicalOccurrenceRef='H0002' },
    [ordered]@{ semanticNodeRef='N003'; canonicalOccurrenceRef='H0003' },
    [ordered]@{ semanticNodeRef='N004'; canonicalOccurrenceRef='H0004' },
    [ordered]@{ semanticNodeRef='N005'; canonicalOccurrenceRef='H0005' },
    [ordered]@{ semanticNodeRef='N006'; canonicalOccurrenceRef='H0006' },
    [ordered]@{ semanticNodeRef='N007'; canonicalOccurrenceRef='H0007' }
)
$parentEdges = @(
    [ordered]@{ childSemanticNodeRef='N001'; parentSemanticNodeRef='ROOT' },
    [ordered]@{ childSemanticNodeRef='N002'; parentSemanticNodeRef='N001' },
    [ordered]@{ childSemanticNodeRef='N003'; parentSemanticNodeRef='N001' },
    [ordered]@{ childSemanticNodeRef='N004'; parentSemanticNodeRef='N003' },
    [ordered]@{ childSemanticNodeRef='N005'; parentSemanticNodeRef='N003' },
    [ordered]@{ childSemanticNodeRef='N006'; parentSemanticNodeRef='ROOT' },
    [ordered]@{ childSemanticNodeRef='N007'; parentSemanticNodeRef='N006' }
)

$sourcePacketSha = Sha $inputPath
$status = 'READY_FOR_USER_APPROVAL_CANONICAL_HIERARCHY'
$reviewedPacket = [ordered]@{
    artifactKind='a99_strict_heading_canonical_hierarchy_pilot_adjudication'
    schemaVersion='a99-strict-heading-canonical-hierarchy-gold-v1-pilot'
    documentId='DOC-0001'
    status=$status
    sourcePacket='artifacts/authority-audit/strict-heading-canonical-hierarchy-review-v2/packets/DOC-0001.canonical-hierarchy-review.v2.json'
    sourcePacketSha256=$sourcePacketSha
    strictHeadingGoldArtifact=[string]$packet.strictHeadingGoldArtifact
    strictHeadingGoldSha256=[string]$packet.strictHeadingGoldSha256
    reviewAuthority='SOURCE_ONLY_PILOT_PROPOSAL_PENDING_USER_APPROVAL'
    approvalRequired=$true
    approvedByUser=$false
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
    adjudication=[ordered]@{
        occurrenceAssignments=$assignments
        semanticNodes=$semanticNodes
        parentEdges=$parentEdges
    }
}

$reviewPath = Join-Path $out 'source-review-adjudication.json'
Write-Json $reviewedPacket $reviewPath

$semanticArtifact = [ordered]@{
    artifactKind='a99_canonical_semantic_nodes_pilot'
    schemaVersion='a99-canonical-semantic-nodes-v1'
    documentId='DOC-0001'
    status=$status
    sourcePacketSha256=$sourcePacketSha
    semanticNodes=$semanticNodes
    occurrenceAssignments=$assignments
    identityDecision='all seven source-backed occurrences are distinct primary semantic nodes'
}
Write-Json $semanticArtifact (Join-Path $out 'semantic-nodes.json')

$parentArtifact = [ordered]@{
    artifactKind='a99_canonical_parent_edges_pilot'
    schemaVersion='a99-canonical-parent-edges-v1'
    documentId='DOC-0001'
    status=$status
    rootRef='ROOT'
    sourcePacketSha256=$sourcePacketSha
    parentEdges=$parentEdges
}
Write-Json $parentArtifact (Join-Path $out 'parent-edges.json')

$parentByChild = @{}
foreach ($edge in $parentEdges) { $parentByChild[[string]$edge.childSemanticNodeRef] = [string]$edge.parentSemanticNodeRef }
$depthByNode = @{}
foreach ($node in $semanticNodes) {
    $current = [string]$node.semanticNodeRef
    $depth = 0
    $seen = @{}
    while ($parentByChild.ContainsKey($current) -and $parentByChild[$current] -ne 'ROOT') {
        if ($seen.ContainsKey($current)) { throw "Cycle while deriving depth at $current." }
        $seen[$current] = $true
        $depth++
        $current = $parentByChild[$current]
    }
    if (-not $parentByChild.ContainsKey($current) -and $current -ne 'ROOT') { throw "Unreachable node while deriving depth: $current." }
    if ($parentByChild.ContainsKey($current) -and $parentByChild[$current] -eq 'ROOT') { $depth++ }
    $depthByNode[[string]$node.semanticNodeRef] = $depth
}
$derivedRows = @(foreach ($node in $semanticNodes) {
    [ordered]@{
        semanticNodeRef=[string]$node.semanticNodeRef
        parentSemanticNodeRef=$parentByChild[[string]$node.semanticNodeRef]
        depth=[int]$depthByNode[[string]$node.semanticNodeRef]
        derivedLevel=[int]$depthByNode[[string]$node.semanticNodeRef]
        occurrenceRefs=@($assignments | Where-Object semanticNodeRef -eq $node.semanticNodeRef | ForEach-Object headingOccurrenceRef)
    }
})
$maxDepth = 0
foreach ($derivedRow in $derivedRows) {
    if ([int]$derivedRow.depth -gt $maxDepth) { $maxDepth = [int]$derivedRow.depth }
}
$derivedArtifact = [ordered]@{
    artifactKind='a99_canonical_derived_levels_pilot'
    schemaVersion='a99-canonical-derived-levels-v1'
    documentId='DOC-0001'
    status=$status
    sourcePacketSha256=$sourcePacketSha
    derivation='depth(ROOT)=0; semantic node level is depth from synthetic ROOT; no historical level input'
    nodes=$derivedRows
    maxDepth=$maxDepth
}
Write-Json $derivedArtifact (Join-Path $out 'derived-levels.json')

$validator = Join-Path $repo 'tools/validate-strict-heading-canonical-hierarchy-review-v2.ps1'
$validationPath = Join-Path $out 'validation.json'
& pwsh -NoProfile -File $validator -PacketPath $reviewPath -OutputPath $validationPath | Out-Null
$validation = Get-Content -LiteralPath $validationPath -Raw | ConvertFrom-Json
if ([string]$validation.status -ne 'VALID') { throw "Pilot validation failed: $($validation | ConvertTo-Json -Compress)" }

$manifest = [ordered]@{
    artifactKind='A99_STRICT_HEADING_CANONICAL_HIERARCHY_GOLD_V1_PILOT'
    schemaVersion='a99-strict-heading-canonical-hierarchy-gold-v1-pilot-manifest'
    status=$status
    documentId='DOC-0001'
    sourcePacketSha256=$sourcePacketSha
    strictHeadingGoldSha256=[string]$packet.strictHeadingGoldSha256
    occurrenceCount=$occurrences.Count
    semanticNodeCount=$semanticNodes.Count
    primaryCount=@($assignments | Where-Object occurrenceRole -eq 'PRIMARY').Count
    repeatCount=@($assignments | Where-Object occurrenceRole -eq 'REPEAT').Count
    continuationCount=@($assignments | Where-Object occurrenceRole -eq 'CONTINUATION').Count
    parentEdgeCount=$parentEdges.Count
    rootChildCount=@($parentEdges | Where-Object parentSemanticNodeRef -eq 'ROOT').Count
    maxDepth=[int]$derivedArtifact.maxDepth
    validatorStatus=[string]$validation.status
    approvalRequired=$true
    approvedByUser=$false
    providerCalls=0
    modelCalls=0
    historicalLevelReadDuringAdjudication=$false
    historicalLevelDiagnosticPerformed=$false
    strictHeadingGoldMutation=$false
    reviewPacketMutation=$false
}
Write-Json $manifest (Join-Path $out 'manifest.json')

$report = @"
# DOC-0001 canonical hierarchy pilot

Status: **$status**

This is a source-only pilot proposal for DOC-0001. It is pending explicit user
approval and is not yet immutable user-approved Gold.

- Heading occurrences: 7
- Semantic nodes: 7
- PRIMARY: 7
- REPEAT: 0
- CONTINUATION: 0
- Parent edges: 7
- Synthetic ROOT children: 2
- Maximum derived depth: $($derivedArtifact.maxDepth)
- Validator: $($validation.status)

The seven source-backed headings are treated as distinct primary semantic
nodes. The parent structure is: H0001 is a ROOT child; H0002 and H0003 are
children of H0001; H0004 and H0005 are children of H0003; H0006 is a ROOT
child; and H0007 is a child of H0006.

Levels in this lane are derived only in `derived-levels.json` from the
validated synthetic-ROOT tree. Historical level was not read and no
historical comparison was performed.

Do not proceed to DOC-0002 or freeze this pilot as
USER_REVIEWED_CANONICAL_HIERARCHY_GOLD until the user explicitly approves it.
"@
$report | Set-Content -LiteralPath (Join-Path $out 'report.md') -Encoding utf8NoBOM
Write-Output "Pilot prepared: DOC-0001 occurrences=$($occurrences.Count) nodes=$($semanticNodes.Count) validation=$($validation.status) status=$status"
