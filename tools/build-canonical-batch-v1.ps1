[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$OutputRoot = 'artifacts/authority-audit/canonical-batch-v1'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path $RepoRoot).Path
$out = Join-Path $repo ($OutputRoot -replace '/', '\')

function Read-Json([string]$Path) { Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json }
function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Rel([string]$Path) { (Resolve-Path -LiteralPath $Path -Relative).ToString().TrimStart('.','\','/').Replace('\','/') }
function Write-Json([string]$Path, $Value) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 80) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
function Find-Existing([string[]]$Candidates) {
    foreach ($candidate in $Candidates) { if ($candidate -and (Test-Path -LiteralPath $candidate)) { return (Resolve-Path -LiteralPath $candidate).Path } }
    return $null
}
function Find-Source([string]$FileName, [string]$ExpectedSha) {
    $matches = @(Get-ChildItem (Join-Path $repo 'todo10_8') -Recurse -File -Filter $FileName -ErrorAction SilentlyContinue)
    foreach ($match in $matches) { if ((Sha $match.FullName) -eq $ExpectedSha) { return $match.FullName } }
    $preferred = @($matches | Where-Object { $_.FullName -like '*generated-docx*' -or $_.FullName -like '*heading_corpus_95_word*' -or $_.FullName -like '*heading_corpus_100*' } | Select-Object -First 1)
    if ($preferred.Count -gt 0) { return $preferred[0].FullName }
    if ($matches.Count -gt 0) { return $matches[0].FullName }
    return $null
}
function Get-Text([object]$Node) { if ($null -eq $Node) { return '' }; return [string]$Node }

$strictDir = Join-Path $repo 'eval/a99-closed-loop/strict-gold-v4'
$strictFiles = @(Get-ChildItem $strictDir -Filter 'DOC-*.json' -File | Sort-Object Name)
$docIds = [Collections.Generic.HashSet[string]]::new()
foreach ($file in $strictFiles) { [void]$docIds.Add(([string](Read-Json $file.FullName).documentId)) }
foreach ($manifestPath in @(Get-ChildItem (Join-Path $repo 'artifacts/authority-audit') -Recurse -Filter 'manifest.json' -File -ErrorAction SilentlyContinue)) {
    try { $j = Read-Json $manifestPath.FullName; if ($j.documentId) { [void]$docIds.Add([string]$j.documentId) } } catch { }
}

$records = [Collections.Generic.List[object]]::new()
$blockers = [Collections.Generic.List[object]]::new()
$frozenDocs = @{}
foreach ($freezePath in @(Get-ChildItem (Join-Path $repo 'artifacts/authority-audit') -Recurse -Filter 'authority-freeze-manifest.json' -File -ErrorAction SilentlyContinue)) {
    try {
        $freeze = Read-Json $freezePath.FullName
        $freezeStatus = if ($freeze.PSObject.Properties.Name -contains 'authorityStatus') { [string]$freeze.authorityStatus } else { [string]$freeze.status }
        if ($freeze.documentId -and $freezeStatus -eq 'USER_REVIEWED_CANONICAL_HIERARCHY_GOLD') {
            $frozenDocs[[string]$freeze.documentId] = [pscustomobject]@{ manifest = $freeze; path = $freezePath.FullName }
        }
    } catch { }
}
# DOC-0001 predates the authority-freeze-v1 filename/schema but is an explicitly
# user-reviewed canonical hierarchy authority. Keep it immutable and include it
# in the batch inventory without treating workflow progression as approval.
$doc0001LegacyFreezePath = Join-Path $repo 'artifacts/authority-audit/strict-heading-canonical-hierarchy-gold-v1/DOC-0001/manifest.json'
if (Test-Path -LiteralPath $doc0001LegacyFreezePath) {
    try {
        $doc0001LegacyFreeze = Read-Json $doc0001LegacyFreezePath
        if ([string]$doc0001LegacyFreeze.status -eq 'USER_REVIEWED_CANONICAL_HIERARCHY_GOLD') {
            $frozenDocs['DOC-0001'] = [pscustomobject]@{ manifest = $doc0001LegacyFreeze; path = $doc0001LegacyFreezePath }
        }
    } catch { }
}

$strictByDoc = @{}
foreach ($file in $strictFiles) { $strictByDoc[[string](Read-Json $file.FullName).documentId] = [pscustomobject]@{ json = Read-Json $file.FullName; path = $file.FullName } }
$knownCanonicalOccurrenceRoot = Join-Path $repo 'artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v1'
$knownCanonicalIdentityRoot = Join-Path $repo 'artifacts/authority-audit/canonical-semantic-identity-v1'
$knownCanonicalHierarchyRoot = Join-Path $repo 'artifacts/authority-audit/canonical-hierarchy-v1'
$proposalRoot = Join-Path $repo 'artifacts/authority-audit/strict-heading-canonical-hierarchy-proposals-v1'

foreach ($docId in ($docIds | Sort-Object)) {
    $strict = $strictByDoc[$docId]
    $strictJson = if ($strict) { $strict.json } else { $null }
    $fileName = if ($strictJson) { [string]$strictJson.fileName } else { '' }
    $strictSha = if ($strictJson) { [string]$strictJson.sourceSha256 } else { '' }
    $sourcePath = $null
    $sourceSha = $null
    $sourceLineageStatus = 'UNKNOWN'
    if ($frozenDocs.ContainsKey($docId)) {
        $frozenManifest = $frozenDocs[$docId].manifest
        $frozenSourcePath = if ($frozenManifest.PSObject.Properties.Name -contains 'sourcePath') { [string]$frozenManifest.sourcePath } else { '' }
        if ($frozenSourcePath) { $sourcePath = Join-Path $repo ($frozenSourcePath -replace '/', '\') }
        if (-not $sourcePath -and $fileName) { $sourcePath = Find-Source -FileName $fileName -ExpectedSha $strictSha }
        if ($sourcePath) { $sourceSha = Sha $sourcePath }
        elseif ($frozenManifest.PSObject.Properties.Name -contains 'sourceSha256') { $sourceSha = [string]$frozenManifest.sourceSha256 }
        $sourceLineageStatus = if ($sourcePath) { 'CANONICAL_FROZEN_SOURCE' } else { 'CANONICAL_FROZEN_SOURCE_PATH_UNRESOLVED' }
    } elseif ($fileName) {
        $sourcePath = Find-Source -FileName $fileName -ExpectedSha $strictSha
        if ($sourcePath) { $sourceSha = Sha $sourcePath; $sourceLineageStatus = if ($sourceSha -eq $strictSha) { 'SOURCE_PRESENT_STRICT_HASH_MATCH' } else { 'SOURCE_PRESENT_HASH_MISMATCH' } }
    }

    $occManifestPath = Join-Path $knownCanonicalOccurrenceRoot "$docId/manifest.json"
    $idManifestPath = Join-Path $knownCanonicalIdentityRoot "$docId/manifest.json"
    $hierManifestPath = Join-Path $knownCanonicalHierarchyRoot "$docId/manifest.json"
    $freeze = if ($frozenDocs.ContainsKey($docId)) { $frozenDocs[$docId].manifest } else { $null }
    $occStatus = if (Test-Path $occManifestPath) { [string](Read-Json $occManifestPath).status } else { 'NOT_CREATED' }
    $idStatus = if (Test-Path $idManifestPath) { [string](Read-Json $idManifestPath).status } else { 'NOT_CREATED' }
    $freezeStatus = if ($freeze -and ($freeze.PSObject.Properties.Name -contains 'authorityStatus')) { [string]$freeze.authorityStatus } elseif ($freeze) { [string]$freeze.status } else { '' }
    $freezeSummary = if ($freeze -and ($freeze.PSObject.Properties.Name -contains 'summary')) { $freeze.summary } else { $null }
    $hierStatus = if ($freeze) { $freezeStatus } elseif (Test-Path $hierManifestPath) { [string](Read-Json $hierManifestPath).status } else { 'NOT_CREATED' }
    $proposalManifestPath = Join-Path $proposalRoot "$docId/manifest.json"
    $proposal = if (Test-Path $proposalManifestPath) { Read-Json $proposalManifestPath } else { $null }
    $proposalStatus = if ($proposal) { [string]$proposal.status } else { 'NOT_CREATED' }
    $proposalValidatorStatus = if ($proposal) { [string]$proposal.validatorStatus } else { 'NOT_CREATED' }
    $strictCount = if ($strictJson) { @($strictJson.headings).Count } else { $null }
    $freezeRoles = if ($freeze -and ($freeze.PSObject.Properties.Name -contains 'occurrenceRoles')) { $freeze.occurrenceRoles } elseif ($freezeSummary -and ($freezeSummary.PSObject.Properties.Name -contains 'occurrenceRoles')) { $freezeSummary.occurrenceRoles } else { $null }
    $freezePrimary = if ($freezeRoles) { [int]$freezeRoles.PRIMARY } elseif ($freeze) { [int]$freeze.primaryCount } else { $null }
    $freezeRepeat = if ($freezeRoles) { [int]$freezeRoles.REPEAT } elseif ($freeze) { [int]$freeze.repeatCount } else { $null }
    $freezeContinuation = if ($freezeRoles) { [int]$freezeRoles.CONTINUATION } elseif ($freeze) { [int]$freeze.continuationCount } else { $null }
    $freezeRootChildren = if ($freeze -and ($freeze.PSObject.Properties.Name -contains 'rootChildrenCount')) { [int]$freeze.rootChildrenCount } elseif ($freezeSummary -and ($freezeSummary.PSObject.Properties.Name -contains 'rootChildCount')) { [int]$freezeSummary.rootChildCount } elseif ($freeze) { [int]$freeze.rootChildCount } else { $null }

    $classification = 'OTHER_HARD_BLOCKER'
    $blockerStatus = 'NO_CANONICAL_OCCURRENCE_AUTHORITY'
    if ($freeze -and $freezeStatus -eq 'USER_REVIEWED_CANONICAL_HIERARCHY_GOLD') {
        $classification = 'FROZEN_COMPLETE'; $blockerStatus = $null
    } elseif ($docId -eq 'DOC-0205') {
        $classification = 'SOURCE_LINEAGE_BLOCKED'; $blockerStatus = 'SOURCE_BINDING_BLOCKED_CURRENT_SOURCE_NOT_CANONICAL'
    } elseif ($proposal -and $proposalStatus -eq 'HISTORICAL_STRICT_HIERARCHY_PROPOSAL') {
        $classification = 'HISTORICAL_SUBSET_PROPOSAL'; $blockerStatus = 'CANONICAL_OCCURRENCE_UNIVERSE_NOT_AUTHORIZED_FOR_CANONICAL_GOLD'
    } elseif ($proposal -and $proposalValidatorStatus -ne 'VALID') {
        $classification = 'SOURCE_BACKED_PROPOSAL_INVALID'; $blockerStatus = 'SOURCE_BACKED_PROPOSAL_VALIDATION_OR_AMBIGUITY_REQUIRES_REVIEW'
    } elseif (-not $sourcePath) {
        $classification = 'SOURCE_MISSING'; $blockerStatus = 'AUTHORITATIVE_SOURCE_NOT_FOUND'
    } elseif ($occStatus -eq 'NOT_CREATED') {
        $classification = 'OTHER_HARD_BLOCKER'; $blockerStatus = 'CANONICAL_EXHAUSTIVE_OCCURRENCE_REVIEW_NOT_MATERIALIZED'
    } elseif ($idStatus -eq 'NOT_CREATED') {
        $classification = 'OCCURRENCE_READY'; $blockerStatus = 'CANONICAL_IDENTITY_NOT_MATERIALIZED'
    } elseif ($hierStatus -eq 'NOT_CREATED') {
        $classification = 'IDENTITY_READY'; $blockerStatus = 'CANONICAL_HIERARCHY_NOT_MATERIALIZED'
    } else {
        $classification = 'HIERARCHY_READY_FOR_USER_APPROVAL'; $blockerStatus = $null
    }
    if ($blockerStatus) { $blockers.Add([ordered]@{ documentId = $docId; classification = $classification; blockerStatus = $blockerStatus; sourcePath = if ($sourcePath) { Rel $sourcePath } else { $null }; sourceLineageStatus = $sourceLineageStatus; strictHeadingCount = $strictCount }) }

    $summarySource = if ($freeze) { $freeze } elseif ($proposal) { $proposal } else { $null }
    $freezeOccurrenceCount = $null
    $freezeSemanticNodeCount = $null
    $freezeParentEdgeCount = $null
    $freezeMaxDepth = $null
    if ($freeze) {
        if ($freezeSummary) {
            $freezeOccurrenceCount = [int]$freezeSummary.occurrenceCount
            $freezeSemanticNodeCount = [int]$freezeSummary.semanticNodeCount
            $freezeParentEdgeCount = [int]$freezeSummary.parentEdgeCount
            $freezeMaxDepth = [int]$freezeSummary.maxDepth
        } else {
            $freezeOccurrenceCount = [int]$freeze.occurrenceCount
            $freezeSemanticNodeCount = [int]$freeze.semanticNodeCount
            $freezeParentEdgeCount = [int]$freeze.parentEdgeCount
            $freezeMaxDepth = [int]$freeze.maxDepth
        }
    }
    $record = [ordered]@{
        docId = $docId
        strictHeadingAuthorityAvailable = ($null -ne $strictJson)
        canonicalSourceAvailable = ($null -ne $sourcePath)
        sourcePath = if ($sourcePath) { Rel $sourcePath } else { $null }
        sourceSHA = $sourceSha
        sourceLineageStatus = $sourceLineageStatus
        strictHeadingSourceSHA = $strictSha
        occurrenceAuthorityStatus = $occStatus
        identityAuthorityStatus = if ($freeze) { 'USER_REVIEWED_CANONICAL_IDENTITY_GOLD' } elseif ($idStatus -ne 'NOT_CREATED') { $idStatus } else { 'NOT_CREATED' }
        hierarchyAuthorityStatus = $hierStatus
        freezeStatus = if ($freeze) { $freezeStatus } else { 'NOT_FROZEN' }
        proposalStatus = $proposalStatus
        proposalValidatorStatus = $proposalValidatorStatus
        proposalAuthority = if ($proposal) { [string]$proposal.reviewAuthority } else { $null }
        blockerStatus = $blockerStatus
        classification = $classification
        strictHeadingCount = $strictCount
        canonicalOccurrenceCount = if ($freeze) { $freezeOccurrenceCount } elseif ($proposal) { [int]$proposal.occurrenceCount } else { $null }
        semanticNodeCount = if ($freeze) { $freezeSemanticNodeCount } elseif ($proposal) { [int]$proposal.semanticNodeCount } else { $null }
        primaryCount = if ($freeze) { $freezePrimary } elseif ($proposal) { [int]$proposal.primaryCount } else { $null }
        repeatCount = if ($freeze) { $freezeRepeat } elseif ($proposal) { [int]$proposal.repeatCount } else { $null }
        continuationCount = if ($freeze) { $freezeContinuation } elseif ($proposal) { [int]$proposal.continuationCount } else { $null }
        parentEdgeCount = if ($freeze) { $freezeParentEdgeCount } elseif ($proposal) { [int]$proposal.parentEdgeCount } else { $null }
        rootChildCount = if ($freeze) { if ($freezeSummary) { [int]$freezeSummary.rootChildCount } else { $freezeRootChildren } } elseif ($proposal) { [int]$proposal.rootChildCount } else { $null }
        maxDepth = if ($freeze) { $freezeMaxDepth } elseif ($proposal) { [int]$proposal.maxDepth } else { $null }
        ambiguityCount = if ($proposal) { [int]$proposal.ambiguityCount } else { 0 }
        proposalCommits = if ($docId -eq 'DOC-0252') { @('63a6102') } else { @() }
        authoritySource = if ($freeze) { Rel $frozenDocs[$docId].path } elseif ($proposal) { Rel $proposalManifestPath } else { $null }
    }
    $records.Add([pscustomobject]$record)
}

Remove-Item -LiteralPath $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $out | Out-Null
$inventory = [ordered]@{
    artifactKind = 'A99_CANONICAL_BATCH_DOCUMENT_INVENTORY'
    schemaVersion = 'a99-canonical-batch-v1'
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    discoverySources = @('eval/a99-closed-loop/strict-gold-v4/*.json','artifacts/authority-audit/**/manifest.json','canonical authority freeze manifests','todo10_8 source corpus')
    historicalArtifactsUsedForCanonicalDecision = $false
    documents = @($records)
    counts = [ordered]@{ discovered = $records.Count; frozenComplete = @($records | Where-Object classification -eq 'FROZEN_COMPLETE').Count; hierarchyReady = @($records | Where-Object classification -eq 'HIERARCHY_READY_FOR_USER_APPROVAL').Count; occurrenceReady = @($records | Where-Object classification -eq 'OCCURRENCE_READY').Count; identityReady = @($records | Where-Object classification -eq 'IDENTITY_READY').Count; historicalSubsetProposal = @($records | Where-Object classification -eq 'HISTORICAL_SUBSET_PROPOSAL').Count; invalidProposal = @($records | Where-Object classification -eq 'SOURCE_BACKED_PROPOSAL_INVALID').Count; hardBlocked = @($records | Where-Object classification -in @('SOURCE_LINEAGE_BLOCKED','SOURCE_MISSING','OTHER_HARD_BLOCKER','HISTORICAL_SUBSET_PROPOSAL','SOURCE_BACKED_PROPOSAL_INVALID')).Count }
}
Write-Json (Join-Path $out 'document-inventory.json') $inventory

$proposalRecords = @($records | Where-Object { $_.classification -in @('FROZEN_COMPLETE','HIERARCHY_READY_FOR_USER_APPROVAL') })
$summary = [ordered]@{
    artifactKind = 'A99_CANONICAL_BATCH_SUMMARY'
    schemaVersion = 'a99-canonical-batch-v1'
    status = 'BATCH_READY_FOR_USER_CANONICAL_APPROVAL'
    reviewAuthority = 'CODEX_SOURCE_BACKED_PROPOSALS_NOT_HUMAN_REVIEWED_EXCEPT_EXISTING_EXPLICITLY_FROZEN_AUTHORITIES'
    discoveredDocuments = $records.Count
    frozenComplete = @($records | Where-Object classification -eq 'FROZEN_COMPLETE').Count
    hierarchyReadyForApproval = @($records | Where-Object classification -eq 'HIERARCHY_READY_FOR_USER_APPROVAL').Count
    hardBlocked = @($records | Where-Object classification -in @('SOURCE_LINEAGE_BLOCKED','SOURCE_MISSING','OTHER_HARD_BLOCKER','HISTORICAL_SUBSET_PROPOSAL','SOURCE_BACKED_PROPOSAL_INVALID')).Count
    totalCanonicalOccurrencesInFrozenAuthority = [int](($records | Where-Object { $_.classification -eq 'FROZEN_COMPLETE' -and $null -ne $_.canonicalOccurrenceCount } | Measure-Object canonicalOccurrenceCount -Sum).Sum)
    totalSemanticNodesInFrozenAuthority = [int](($records | Where-Object { $_.classification -eq 'FROZEN_COMPLETE' -and $null -ne $_.semanticNodeCount } | Measure-Object semanticNodeCount -Sum).Sum)
    totalParentEdgesInFrozenAuthority = [int](($records | Where-Object { $_.classification -eq 'FROZEN_COMPLETE' -and $null -ne $_.parentEdgeCount } | Measure-Object parentEdgeCount -Sum).Sum)
    totalOccurrencesInSourceBackedProposals = [int](($records | Where-Object { $_.classification -ne 'FROZEN_COMPLETE' -and $null -ne $_.canonicalOccurrenceCount } | Measure-Object canonicalOccurrenceCount -Sum).Sum)
    totalSemanticNodesInSourceBackedProposals = [int](($records | Where-Object { $_.classification -ne 'FROZEN_COMPLETE' -and $null -ne $_.semanticNodeCount } | Measure-Object semanticNodeCount -Sum).Sum)
    documentsAwaitingBatchApproval = @($proposalRecords | Where-Object classification -eq 'HIERARCHY_READY_FOR_USER_APPROVAL').Count
    documentsAlreadyFrozen = @($records | Where-Object classification -eq 'FROZEN_COMPLETE').Count
    providerCalls = 0
    modelCalls = 0
    historicalLevelRead = $false
    historicalParentRead = $false
    historicalHierarchyUsedForDecision = $false
    historicalSemanticTotalUsedForDecision = $false
    documents = @($records)
}
Write-Json (Join-Path $out 'batch-summary.json') $summary
Write-Json (Join-Path $out 'blockers.json') @($blockers)

$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# Canonical hierarchy corpus batch review')
$lines.Add('')
$lines.Add('Status: **BATCH_READY_FOR_USER_CANONICAL_APPROVAL**')
$lines.Add('')
$lines.Add('This pack distinguishes already frozen authority from source-backed proposals and explicit blockers. No new document was promoted to USER_REVIEWED_* by this batch.')
$lines.Add('')
$lines.Add("Documents discovered: $($records.Count)")
$lines.Add("Already frozen: $($summary.documentsAlreadyFrozen)")
$lines.Add("Awaiting batch approval: $($summary.documentsAwaitingBatchApproval)")
$lines.Add("Hard blocked: $($summary.hardBlocked)")
$lines.Add("Frozen canonical occurrences: $($summary.totalCanonicalOccurrencesInFrozenAuthority)")
$lines.Add("Frozen semantic nodes: $($summary.totalSemanticNodesInFrozenAuthority)")
$lines.Add("Frozen parent edges: $($summary.totalParentEdgesInFrozenAuthority)")
$lines.Add("Occurrences represented by non-frozen proposals: $($summary.totalOccurrencesInSourceBackedProposals)")
$lines.Add("Semantic nodes represented by non-frozen proposals: $($summary.totalSemanticNodesInSourceBackedProposals)")
$lines.Add('')
$lines.Add('## Per-document review')
$lines.Add('')
foreach ($row in $records) {
    $lines.Add("- **$($row.docId)** — classification=$($row.classification); occurrence=$($row.canonicalOccurrenceCount); semanticNodes=$($row.semanticNodeCount); parentEdges=$($row.parentEdgeCount); roots=$($row.rootChildCount); maxDepth=$($row.maxDepth); blocker=$($row.blockerStatus)")
}
$lines.Add('')
$lines.Add('## Approval boundary')
$lines.Add('')
$lines.Add('Existing frozen authorities remain immutable. New proposals, if any, require explicit user batch approval before hierarchy authority freeze. Historical direct levels and historical hierarchy artifacts were not used to create canonical decisions.')
[IO.File]::WriteAllText((Join-Path $out 'batch-review.md'), ($lines -join [Environment]::NewLine) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

$validation = [ordered]@{
    artifactKind = 'A99_CANONICAL_BATCH_VALIDATION'
    schemaVersion = 'a99-canonical-batch-v1'
    status = 'BATCH_READY_FOR_USER_CANONICAL_APPROVAL'
    discoveredDocumentCount = $records.Count
    allRecordsHaveClassification = (@($records | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.classification) }).Count -eq 0)
    frozenAuthoritiesMutated = $false
    userReviewedPromotionPerformed = $false
    historicalLevelRead = $false
    historicalParentRead = $false
    historicalHierarchyUsedForDecision = $false
    historicalSemanticTotalUsedForDecision = $false
    providerCalls = 0
    modelCalls = 0
    blockersWithExplicitReason = (@($blockers | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.blockerStatus) }).Count -eq 0)
}
Write-Json (Join-Path $out 'validation.json') $validation

$manifestFiles = @(Get-ChildItem $out -File | Sort-Object Name)
$manifest = [ordered]@{
    artifactKind = 'A99_CANONICAL_BATCH_MANIFEST'
    schemaVersion = 'a99-canonical-batch-v1'
    status = 'BATCH_READY_FOR_USER_CANONICAL_APPROVAL'
    documentInventory = Rel (Join-Path $out 'document-inventory.json')
    generatedArtifacts = @($manifestFiles | ForEach-Object { [pscustomobject]@{ path = Rel $_.FullName; sha256 = Sha $_.FullName } })
    sourceDiscovery = 'strict-gold-v4 + authority-audit manifests + current source corpus'
    canonicalGoldMutation = $false
    userReviewedPromotion = $false
    providerCalls = 0
    modelCalls = 0
}
Write-Json (Join-Path $out 'manifest.json') $manifest
Write-Output ([pscustomobject]@{ status = $summary.status; documents = $records.Count; frozen = $summary.documentsAlreadyFrozen; awaitingApproval = $summary.documentsAwaitingBatchApproval; hardBlocked = $summary.hardBlocked; providerCalls = 0; modelCalls = 0 } | ConvertTo-Json -Depth 20)
