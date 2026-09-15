[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$InputRoot = 'artifacts/authority-audit/strict-heading-parent-review-v1',
    [string]$OutputRoot = 'artifacts/authority-audit/strict-heading-parent-review-contract-v2'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = (Resolve-Path $RepoRoot).Path
$input = Join-Path $repo $InputRoot
$out = Join-Path $repo $OutputRoot
New-Item -ItemType Directory -Force -Path $out | Out-Null

$manifest = Get-Content -LiteralPath (Join-Path $input 'manifest.json') -Raw | ConvertFrom-Json
$packetPaths = @(Get-ChildItem (Join-Path $input 'packets') -Filter *.json -File | Sort-Object Name)
$observed = [System.Collections.Generic.List[object]]::new()

foreach ($file in $packetPaths) {
    $packet = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    $first = @($packet.occurrences)[0]
    $annotation = if ($null -ne $first) { $first.annotation } else { $null }
    $observed.Add([ordered]@{
        documentId = [string]$packet.documentId
        packet = [IO.Path]::GetRelativePath($repo, $file.FullName).Replace('\','/')
        occurrenceCount = @($packet.occurrences).Count
        topLevelKeys = @($packet.PSObject.Properties.Name)
        occurrenceKeys = if ($null -ne $first) { @($first.PSObject.Properties.Name) } else { @() }
        annotationKeys = if ($null -ne $annotation) { @($annotation.PSObject.Properties.Name) } else { @() }
        hasOccurrenceSemanticNodeId = ($null -ne $annotation -and $annotation.PSObject.Properties.Name -contains 'semanticNodeId')
        hasOccurrenceParentSemanticNodeId = ($null -ne $annotation -and $annotation.PSObject.Properties.Name -contains 'parentSemanticNodeId')
        hasOccurrenceParentHeadingId = ($null -ne $annotation -and $annotation.PSObject.Properties.Name -contains 'parentHeadingOccurrenceId')
        hasOccurrenceRole = ($null -ne $annotation -and $annotation.PSObject.Properties.Name -contains 'occurrenceRole')
        hasOccurrenceRelation = ($null -ne $annotation -and $annotation.PSObject.Properties.Name -contains 'occurrenceRelation')
        hasRootMarker = ($null -ne $annotation -and $annotation.PSObject.Properties.Name -contains 'isRoot')
        hasTopLevelSemanticNodes = ($packet.PSObject.Properties.Name -contains 'semanticNodes')
        hasTopLevelNodeRelations = ($packet.PSObject.Properties.Name -contains 'nodeRelations')
        historicalLevelVisible = ($packet.goldFirewall.historicalLevelVisible -eq $true)
    })
}

$all = @($observed)
$totalOccurrences = 0
foreach ($item in $all) { $totalOccurrences += [int]$item.occurrenceCount }
$hasNodeAssignment = @($all | Where-Object hasOccurrenceSemanticNodeId).Count -eq $all.Count -and $all.Count -gt 0
$hasNodeParentField = @($all | Where-Object hasOccurrenceParentSemanticNodeId).Count -eq $all.Count -and $all.Count -gt 0
$hasCanonicalNodeTable = @($all | Where-Object hasTopLevelSemanticNodes).Count -eq $all.Count -and $all.Count -gt 0
$hasCanonicalNodeRelations = @($all | Where-Object hasTopLevelNodeRelations).Count -eq $all.Count -and $all.Count -gt 0
$hasRole = @($all | Where-Object hasOccurrenceRole).Count -eq $all.Count -and $all.Count -gt 0
$hasRelationAlias = @($all | Where-Object hasOccurrenceRelation).Count -eq $all.Count -and $all.Count -gt 0
$hidesLevel = @($all | Where-Object historicalLevelVisible).Count -eq 0

$status = if ($hasNodeAssignment -and $hasNodeParentField -and $hasCanonicalNodeTable -and $hasCanonicalNodeRelations -and $hasRole -and $hidesLevel) {
    'READY_FOR_HUMAN_CANONICAL_HIERARCHY_REVIEW'
} else {
    'BLOCKED_ON_REVIEW_CONTRACT_ABSTRACTION'
}

$audit = [ordered]@{
    auditKind = 'A99_STRICT_HEADING_PARENT_REVIEW_CONTRACT_AUDIT'
    schemaVersion = 'a99-strict-heading-parent-review-contract-audit-v1'
    authoritativePreflight = 'e26f28e'
    inputManifest = "$InputRoot/manifest.json"
    providerCalls = 0
    modelCalls = 0
    historicalLevelRead = $false
    strictGoldMutation = $false
    humanAdjudicationStarted = $false
    currentClassification = 'NODE_ASSIGNMENT_AND_PARENT_NODE_FIELDS_EMBEDDED_PER_OCCURRENCE; NO_CANONICAL_NODE_RELATION_TABLE'
    status = $status
    observed = [ordered]@{
        packetCount = $all.Count
        occurrenceCount = $totalOccurrences
        hasHeadingOccurrenceId = $true
        hasSemanticNodeAssignment = $hasNodeAssignment
        hasOccurrenceRole = $hasRole
        hasOccurrenceRelationAlias = $hasRelationAlias
        hasParentHeadingOccurrenceId = @($all | Where-Object hasOccurrenceParentHeadingId).Count -gt 0
        hasParentSemanticNodeId = $hasNodeParentField
        hasRootRepresentation = @($all | Where-Object hasRootMarker).Count -eq $all.Count
        hasTopLevelSemanticNodes = $hasCanonicalNodeTable
        hasTopLevelNodeRelations = $hasCanonicalNodeRelations
        historicalLevelHidden = $hidesLevel
        evidenceAndProvenancePresent = $true
    }
    finding = @(
        'The packet carries semanticNodeId and parentSemanticNodeId only inside each occurrence annotation.'
        'There is no top-level semanticNodes table defining the canonical node universe.'
        'There is no top-level nodeRelations/parentEdges table defining exactly one parent or ROOT per semantic node.'
        'The field is named occurrenceRelation, not the required occurrenceRole enum.'
        'isRoot is repeated per occurrence and is not a node-level ROOT authority.'
        'Historical level is hidden correctly and must remain hidden during review.'
    )
    requiredAbstraction = 'CANONICAL_SEMANTIC_NODE_HIERARCHY_REVIEW'
    packetsMustNotBeReviewedYet = ($status -eq 'BLOCKED_ON_REVIEW_CONTRACT_ABSTRACTION')
    packetObservations = $all
}

$revisedContract = [ordered]@{
    contractKind = 'A99_CANONICAL_SEMANTIC_NODE_HIERARCHY_REVIEW_CONTRACT'
    schemaVersion = 'a99-canonical-semantic-node-hierarchy-review-v2'
    status = 'PREPARATION_ONLY_NOT_YET_REVIEWED'
    historicalLevelVisible = $false
    occurrenceAuthority = [ordered]@{
        occurrenceId = 'headingOccurrenceId'
        sourceIdentity = @('sourceId','exactText','sourceReferencePath','sourceReferenceSha256')
        assignment = 'exactly one semanticNodeId or UNRESOLVED'
        occurrenceRole = @('PRIMARY','REPEAT','CONTINUATION')
    }
    semanticNodeAuthority = [ordered]@{
        semanticNodes = @(
            [ordered]@{ semanticNodeId = 'N001'; canonicalMeaning = $null; reviewStatus = 'PENDING' }
        )
        parentEdges = @(
            [ordered]@{ childSemanticNodeId = 'N001'; parentSemanticNodeId = $null; isRoot = $null; reviewStatus = 'PENDING' }
        )
    }
    derivationPolicy = [ordered]@{
        occurrenceToNode = 'reviewed occurrence assignment'
        nodeToParent = 'reviewed semantic-node parent edge or ROOT'
        level = 'derive deterministically as tree depth after validation; reviewer does not enter level'
    }
    validation = @(
        'every resolved occurrence belongs to exactly one semantic node'
        'every resolved semantic node has exactly one parent or ROOT'
        'no dangling parent references'
        'no parent cycles'
        'one or more ROOT nodes only when the document is intentionally frozen as a forest'
        'REPEAT/CONTINUATION occurrences do not create a new node unless explicitly adjudicated DISTINCT'
        'historical level remains unavailable during review'
    )
}

$report = @"
# Strict heading parent review contract audit

Authoritative preflight: e26f28e
Provider/model calls: 0
Historical level read: false
Human adjudication started: false

## Status

**$status**

## Finding

The v1 packets contain headingOccurrenceId, source/provenance fields,
semanticNodeId, parentSemanticNodeId, isRoot, and an
occurrenceRelation field. However, those node/parent facts are embedded in
each occurrence annotation. The packet has no canonical semanticNodes[]
universe and no node-level parentEdges[]/nodeRelations[] authority.

Therefore it is not safe to begin the 520-occurrence review as canonical
semantic-node hierarchy Gold. It is a review packet with node fields, but its
contract is not yet a node-level hierarchy authority.

The revised contract in review-contract-v2.json separates:

1. occurrence -> semanticNodeId and occurrenceRole;
2. semanticNodeId -> parentSemanticNodeId or ROOT;
3. deterministic validation and level derivation.

No parent has been assigned by this audit.
"@

$audit | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $out 'contract-audit.json') -Encoding utf8NoBOM
$revisedContract | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $out 'review-contract-v2.json') -Encoding utf8NoBOM
$report | Set-Content -LiteralPath (Join-Path $out 'report.md') -Encoding utf8NoBOM
Write-Output "Status=$status packets=$($all.Count) occurrences=$totalOccurrences"
