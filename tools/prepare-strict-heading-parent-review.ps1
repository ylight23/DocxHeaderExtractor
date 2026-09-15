[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$OutputRoot = "artifacts/authority-audit/strict-heading-parent-review-v1"
)

$ErrorActionPreference = "Stop"
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

$packets = foreach ($item in $strictFiles) {
    $j = $item.Json
    $rows = @($j.headings | Sort-Object headingOccurrenceId)
    $packet = [ordered]@{
        artifactKind = 'a99_strict_heading_parent_review_packet'
        schemaVersion = 'a99-strict-heading-parent-review-v1'
        documentId = [string]$j.documentId
        status = 'AWAITING_HUMAN_SOURCE_ONLY_REVIEW'
        reviewScope = 'USER_REVIEWED_PARENT_EDGES_AND_SEMANTIC_NODE_IDENTITY'
        sourceSha256 = [string]$j.sourceSha256
        strictHeadingGoldArtifact = Relative $item.Path
        strictHeadingGoldSha256 = Sha $item.Path
        goldFirewall = [ordered]@{
            modelPredictionsVisible = $false
            candidateShortlistVisible = $false
            oldEvalHierarchyVisible = $false
            historicalLevelVisible = $false
            existingParentAnnotationsVisible = $false
            identityPredictionsVisible = $false
        }
        reviewInstructions = @(
            'Review source semantics only; do not infer parent from historical level or document order alone.'
            'Assign semanticNodeId so repeat/continuation occurrences can share one semantic node.'
            'Assign parentSemanticNodeId between semantic nodes; assign isRoot explicitly.'
            'Use PRIMARY, REPEAT, or CONTINUATION only when source evidence supports it.'
            'Use UNRESOLVED when source evidence is insufficient; do not force a tree.'
            'Validate no parent cycle and at most one parent per resolved semantic node.'
        )
        occurrences = @($rows | ForEach-Object {
            [ordered]@{
                headingOccurrenceId = [string]$_.headingOccurrenceId
                sourceId = [string]$_.sourceId
                exactText = [string]$_.exactText
                role = if ($_.PSObject.Properties.Name -contains 'role') { [string]$_.role } else { $null }
                sourceReferencePath = if ($_.PSObject.Properties.Name -contains 'sourceReferencePath') { [string]$_.sourceReferencePath } else { $null }
                sourceReferenceSha256 = if ($_.PSObject.Properties.Name -contains 'sourceReferenceSha') { [string]$_.sourceReferenceSha } else { $null }
                annotation = [ordered]@{
                    semanticNodeId = $null
                    parentSemanticNodeId = $null
                    isRoot = $null
                    occurrenceRelation = $null
                    reviewNote = $null
                    reviewStatus = 'AWAITING_HUMAN_SOURCE_ONLY_REVIEW'
                }
            }
        })
        validation = [ordered]@{
            expectedOccurrenceCount = $rows.Count
            assignedOccurrenceCount = 0
            resolvedSemanticNodeCount = 0
            parentEdgeCount = 0
            rootNodeCount = 0
            unresolvedOccurrenceCount = 0
            cycles = 0
            multipleStructuralParents = 0
        }
    }
    $name = "$($j.documentId).strict-heading-parent-review.v1.json"
    $packet | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $packetOut $name) -Encoding utf8NoBOM
    [pscustomobject]@{
        documentId = [string]$j.documentId
        packet = "packets/$name"
        packetSha256 = Sha (Join-Path $packetOut $name)
        sourceSha256 = [string]$j.sourceSha256
        strictGoldArtifact = Relative $item.Path
        strictGoldSha256 = Sha $item.Path
        occurrenceCount = $rows.Count
        status = 'AWAITING_HUMAN_SOURCE_ONLY_REVIEW'
    }
}

$manifest = [ordered]@{
    auditKind = 'A99_STRICT_HEADING_PARENT_REVIEW_PREFLIGHT'
    schemaVersion = 'a99-strict-heading-parent-review-preflight-v1'
    generatedAt = '2026-09-15'
    status = 'BLOCKED_ON_HUMAN_SOURCE_ONLY_PARENT_REVIEW'
    providerCalls = 0
    modelCalls = 0
    goldMutation = $false
    historicalLevelUsedToConstructParentEdges = $false
    predictionArtifactsRead = $false
    documentCount = $packets.Count
    occurrenceCount = ($packets | Measure-Object occurrenceCount -Sum).Sum
    packets = @($packets)
    nextAuthority = 'USER_REVIEWED_PARENT_EDGES_AND_VALIDATED_FOREST'
}

$contract = @"
# Strict heading parent/tree review contract

This directory is a source-only review preflight, not hierarchy Gold.

Input authority: the nine materialized `strict-gold-v4` heading lists. Historical
level fields are deliberately omitted from packets to prevent level-to-parent
anchoring. No model predictions, candidate sets, old hierarchy outputs, or
identity evaluation artifacts are exposed.

The reviewer must assign semantic node identity and parent edges from source
meaning/evidence. A parent edge is between semantic nodes, not necessarily
between every physical occurrence. Repeat/continuation occurrences retain
provenance. If evidence is insufficient, mark the occurrence/node UNRESOLVED.

Required freeze validation after review:

- every reviewed occurrence belongs to the frozen source document;
- no duplicate physical occurrence assignment;
- resolved semantic nodes have at most one structural parent;
- exactly one root per resolved tree/scope, unless the reviewer explicitly freezes a forest;
- no parent cycle;
- no parent edge synthesized from level or order alone;
- canonical serialization and SHA256 are stable.

Only after this review is adjudicated and frozen may the lane derive
`level = depth(tree)` and compare it with the historical level annotation.
"@
$contract | Set-Content -LiteralPath (Join-Path $out 'review-contract.md') -Encoding utf8NoBOM
$manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $out 'manifest.json') -Encoding utf8NoBOM
Write-Output "Prepared $($packets.Count) source-only review packets / $($manifest.occurrenceCount) occurrences."
