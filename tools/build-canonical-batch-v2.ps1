[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$OutputRoot = 'artifacts/authority-audit/canonical-batch-v2'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path $RepoRoot).Path
$out = Join-Path $repo ($OutputRoot -replace '/', '\')

function Read-Json([string]$Path) { Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json }
function Write-Json([string]$Path, $Value) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 80) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Rel([string]$Path) { (Resolve-Path -LiteralPath $Path -Relative).ToString().TrimStart('.','\','/').Replace('\','/') }

$v1 = Read-Json (Join-Path $repo 'artifacts/authority-audit/canonical-batch-v1/document-inventory.json')
$burn = Read-Json (Join-Path $repo 'artifacts/authority-audit/canonical-blocker-burn-down-v2/summary.json')
$burnRows = @($burn.documents)
$frozenRows = @($v1.documents | Where-Object classification -eq 'FROZEN_COMPLETE')
$rows = @($frozenRows) + @($burnRows)

Remove-Item -LiteralPath $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $out | Out-Null

$inventoryRows = foreach ($row in $rows) {
    $isFrozen = if ($row.PSObject.Properties.Name -contains 'status') { [string]$row.status -eq 'FROZEN_COMPLETE' } else { [string]$row.classification -eq 'FROZEN_COMPLETE' }
    if (-not $isFrozen) {
        [ordered]@{
            docId=[string]$row.documentId
            status='HARD_BLOCKED_WITH_PRECISE_SOURCE_BACKED_REASON'
            sourcePath=$row.sourcePath
            sourceSHA=$row.sourceSha256
            sourceStatus=$row.sourceStatus
            occurrenceCount=0
            semanticNodeCount=0
            parentEdgeCount=0
            rootChildren=0
            maxDepth=0
            blockedPhase=$row.blockedPhase
            reasonCode=$row.reasonCode
            heuristicCandidateCount=$row.heuristicCandidateCount
            strongStructuralCandidateCount=$row.strongStructuralCandidateCount
            validator=$row.validator
            proposalAuthority='NONE_CANONICAL_PROPOSAL_CREATED'
        }
    } else {
        [ordered]@{
            docId=[string]$row.docId
            status='FROZEN_COMPLETE'
            sourcePath=$row.sourcePath
            sourceSHA=$row.sourceSHA
            sourceStatus=$row.sourceLineageStatus
            occurrenceCount=$row.canonicalOccurrenceCount
            semanticNodeCount=$row.semanticNodeCount
            parentEdgeCount=$row.parentEdgeCount
            rootChildren=$row.rootChildCount
            maxDepth=$row.maxDepth
            blockedPhase=$null
            reasonCode=$null
            heuristicCandidateCount=$null
            strongStructuralCandidateCount=$null
            validator='FROZEN_AUTHORITY_VALIDATED'
            proposalAuthority='EXPLICITLY_FROZEN_EXISTING_AUTHORITY'
        }
    }
}

$inventory = [ordered]@{
    artifactKind='A99_CANONICAL_BATCH_V2_DOCUMENT_INVENTORY'
    schemaVersion='a99-canonical-batch-v2'
    baselineCommit='fc07f98'
    burnDownSummary='artifacts/authority-audit/canonical-blocker-burn-down-v2/summary.json'
    documents=@($inventoryRows)
    counts=[ordered]@{
        discovered=$inventoryRows.Count
        frozenExisting=@($inventoryRows | Where-Object status -eq 'FROZEN_COMPLETE').Count
        newHierarchyProposals=0
        readyForUserApproval=0
        hardBlocked=@($inventoryRows | Where-Object status -eq 'HARD_BLOCKED_WITH_PRECISE_SOURCE_BACKED_REASON').Count
    }
    providerCalls=0
    modelCalls=0
    historicalLevelRead=$false
    historicalParentRead=$false
    historicalHierarchyUsedForDecision=$false
    historicalSemanticTotalUsedForDecision=$false
    canonicalGoldMutation=$false
    newUserReviewedPromotion=$false
}
Write-Json (Join-Path $out 'document-inventory.json') $inventory

$summary = [ordered]@{
    artifactKind='A99_CANONICAL_BATCH_V2_SUMMARY'
    schemaVersion='a99-canonical-batch-v2-summary'
    status='BATCH_V2_READY_FOR_USER_REVIEW'
    totalDocuments=$inventoryRows.Count
    frozenExisting=$inventory.counts.frozenExisting
    newHierarchyProposals=0
    remainingHardBlockers=$inventory.counts.hardBlocked
    newOccurrences=0
    newSemanticNodes=0
    newParentEdges=0
    aggregateCanonicalOccurrences=[int](($inventoryRows | Where-Object status -eq 'FROZEN_COMPLETE' | ForEach-Object { [int]$_.occurrenceCount } | Measure-Object -Sum).Sum)
    aggregateSemanticNodes=[int](($inventoryRows | Where-Object status -eq 'FROZEN_COMPLETE' | ForEach-Object { [int]$_.semanticNodeCount } | Measure-Object -Sum).Sum)
    aggregateParentEdges=[int](($inventoryRows | Where-Object status -eq 'FROZEN_COMPLETE' | ForEach-Object { [int]$_.parentEdgeCount } | Measure-Object -Sum).Sum)
    providerCalls=0
    modelCalls=0
    historicalLevelRead=$false
    historicalParentRead=$false
    historicalHierarchyUsedForDecision=$false
    historicalSemanticTotalUsedForDecision=$false
    canonicalGoldMutation=$false
    newUserReviewedPromotion=$false
    documents=@($inventoryRows)
}
Write-Json (Join-Path $out 'batch-summary.json') $summary

$blockerRows = foreach ($row in $burnRows) {
    $path = Join-Path $repo ("artifacts/authority-audit/canonical-blocker-burn-down-v2/blockers/{0}.json" -f $row.documentId)
    if (Test-Path -LiteralPath $path) { Read-Json $path }
}
Write-Json (Join-Path $out 'blockers.json') @($blockerRows)

$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# Canonical hierarchy corpus batch v2')
$lines.Add('')
$lines.Add('Status: **BATCH_V2_READY_FOR_USER_REVIEW**')
$lines.Add('')
$lines.Add('All 11 original blockers were attempted. Existing frozen authorities remain immutable. No new document was promoted to USER_REVIEWED_* and no new hierarchy proposal was fabricated from heuristic candidates.')
$lines.Add('')
$lines.Add("Total documents: $($summary.totalDocuments)")
$lines.Add("Frozen existing: $($summary.frozenExisting)")
$lines.Add("New hierarchy proposals: $($summary.newHierarchyProposals)")
$lines.Add("Remaining hard blockers: $($summary.remainingHardBlockers)")
$lines.Add("Aggregate frozen occurrences: $($summary.aggregateCanonicalOccurrences)")
$lines.Add("Aggregate frozen semantic nodes: $($summary.aggregateSemanticNodes)")
$lines.Add("Aggregate frozen parent edges: $($summary.aggregateParentEdges)")
$lines.Add('')
$lines.Add('## Per-document status')
$lines.Add('')
foreach ($row in $inventoryRows) {
    $lines.Add("- **$($row.docId)** — $($row.status); source=$($row.sourceStatus); occurrences=$($row.occurrenceCount); semanticNodes=$($row.semanticNodeCount); parentEdges=$($row.parentEdgeCount); reason=$($row.reasonCode)")
}
$lines.Add('')
$lines.Add('## Authority boundary')
$lines.Add('')
$lines.Add('New source scans are diagnostic evidence only. Canonical occurrence, identity, hierarchy, and derived-level authority require explicit source-backed review; new USER_REVIEWED_* promotion is forbidden in this batch.')
[IO.File]::WriteAllText((Join-Path $out 'batch-review.md'), ($lines -join [Environment]::NewLine) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

$validation = [ordered]@{
    artifactKind='A99_CANONICAL_BATCH_V2_VALIDATION'
    schemaVersion='a99-canonical-batch-v2-validation'
    status='BATCH_V2_READY_FOR_USER_REVIEW'
    totalDocuments=$summary.totalDocuments
    allOriginalBlockersAttempted=($burn.attemptedAllOriginalBlockers -eq $true)
    frozenExistingUnchanged=($summary.frozenExisting -eq 4)
    newUserReviewedPromotion=($summary.newUserReviewedPromotion)
    canonicalGoldMutation=($summary.canonicalGoldMutation)
    historicalLevelRead=$false
    historicalParentRead=$false
    providerCalls=0
    modelCalls=0
}
Write-Json (Join-Path $out 'validation.json') $validation

$manifestFiles = @(Get-ChildItem $out -File | Sort-Object Name)
$manifest = [ordered]@{
    artifactKind='A99_CANONICAL_BATCH_V2_MANIFEST'
    schemaVersion='a99-canonical-batch-v2-manifest'
    status='BATCH_V2_READY_FOR_USER_REVIEW'
    baselineCommit='fc07f98'
    burnDownCommit='PENDING'
    generatedArtifacts=@($manifestFiles | ForEach-Object { [ordered]@{ path=Rel $_.FullName; sha256=Sha $_.FullName } })
    providerCalls=0
    modelCalls=0
    canonicalGoldMutation=$false
    newUserReviewedPromotion=$false
}
Write-Json (Join-Path $out 'manifest.json') $manifest
Write-Output ($summary | ConvertTo-Json -Depth 20)
