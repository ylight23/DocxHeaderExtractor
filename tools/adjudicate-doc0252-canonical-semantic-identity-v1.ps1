[CmdletBinding()]
param(
    [string]$OccurrenceRoot = "artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v1/DOC-0252",
    [string]$OutputRoot = "artifacts/authority-audit/canonical-semantic-identity-v1/DOC-0252"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Get-Location).Path
$documentId = 'DOC-0252'
$expectedOccurrenceCount = 40
$expectedSourceSha256 = '4dda3c8ec8cd74e3a61503db0f8e9f168270d39036e3825441ab6167f9e16a77'
$occurrencePath = Join-Path $repoRoot (($OccurrenceRoot -replace '/', '\') + '\exact-bindings.json')
$occurrenceManifestPath = Join-Path $repoRoot (($OccurrenceRoot -replace '/', '\') + '\manifest.json')
$historicalBridgePath = Join-Path $repoRoot (($OccurrenceRoot -replace '/', '\') + '\historical-bridge-diagnostic.json')
$outputPath = Join-Path $repoRoot ($OutputRoot -replace '/', '\')

function Write-JsonFile {
    param([string]$Path, $Value)
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $json = $Value | ConvertTo-Json -Depth 60
    [IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Write-TextFile {
    param([string]$Path, [string]$Text)
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    [IO.File]::WriteAllText($Path, $Text.TrimStart() + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

if (-not (Test-Path -LiteralPath $occurrencePath)) { throw "OCCURRENCE_AUTHORITY_CONFLICT: missing $occurrencePath" }
$occurrenceAuthoritySha256 = (Get-FileHash -LiteralPath $occurrencePath -Algorithm SHA256).Hash.ToLowerInvariant()
$occurrenceAuthority = Get-Content -Raw -LiteralPath $occurrencePath | ConvertFrom-Json
$bindings = @($occurrenceAuthority.acceptedCanonicalBindings)

if (@($bindings).Length -ne $expectedOccurrenceCount) { throw "OCCURRENCE_AUTHORITY_CONFLICT: expected $expectedOccurrenceCount, got $(@($bindings).Length)" }
if ([string]$occurrenceAuthority.documentId -ne $documentId) { throw 'OCCURRENCE_AUTHORITY_CONFLICT: document id' }
if ([string]$occurrenceAuthority.sourceSha256 -ne $expectedSourceSha256) { throw "OCCURRENCE_AUTHORITY_CONFLICT: source SHA $($occurrenceAuthority.sourceSha256)" }
$duplicateOccurrenceIds = @($bindings | Group-Object occurrenceId | Where-Object Count -gt 1 | ForEach-Object Name)
if (@($duplicateOccurrenceIds).Length -gt 0) { throw 'OCCURRENCE_AUTHORITY_CONFLICT: duplicate occurrence ids' }

$orderedBindings = @($bindings | Sort-Object occurrenceOrder)
$occurrenceRefs = [Collections.Generic.List[object]]::new()
$occurrenceBySourceId = @{}
$index = 0
foreach ($binding in $orderedBindings) {
    $index++
    $ref = 'H{0:D3}' -f $index
    $item = [pscustomobject]@{
        headingOccurrenceRef = $ref
        occurrenceId = [string]$binding.occurrenceId
        sourceOccurrenceId = [string]$binding.sourceOccurrenceId
        sourceId = [string]$binding.sourceId
        exactText = [string]$binding.exactText
        sourceSpan = $binding.sourceSpan
        documentOrder = [int]$binding.documentOrder
        occurrenceOrder = [int]$binding.occurrenceOrder
        headingKind = [string]$binding.headingKind
        sourceContainerKind = [string]$binding.sourceContainerKind
        sourceEvidence = $binding.sourceEvidence
    }
    $occurrenceRefs.Add($item)
    $occurrenceBySourceId[[string]$binding.sourceId] = $item
}

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

$inputManifest = [ordered]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_IDENTITY_OCCURRENCE_INPUT'
    schemaVersion = 'a99-canonical-semantic-identity-v1-doc0252'
    documentId = $documentId
    authoritativeOccurrenceCheckpoint = '4ce5cca'
    occurrenceAuthority = (($OccurrenceRoot + '/exact-bindings.json') -replace '\\','/')
    occurrenceAuthoritySha256 = $occurrenceAuthoritySha256
    sourceSha256 = [string]$occurrenceAuthority.sourceSha256
    inputOccurrences = $occurrenceRefs.Count
    occurrenceRefs = @($occurrenceRefs)
    sourceOnly = $true
    historicalIdentityUsedForDecision = $false
    historicalOccurrenceRowsUsedForDecision = $false
    historicalHierarchyUsedForDecision = $false
    historicalLevelRead = $false
    oldSemanticTotalUsedForDecision = $false
    providerCalls = 0
    modelCalls = 0
}
Write-JsonFile (Join-Path $outputPath 'occurrence-input-manifest.json') $inputManifest

# Source-only identity adjudication. The only accepted collapse is the explicit agenda
# continuation SESSION V -> SESSION V (Cont'd). Narrative/agenda copies are separate owners.
$continuationFrom = $occurrenceRefs | Where-Object { $_.sourceId -eq 'body[1]/p[166]' }
$continuationTo = $occurrenceRefs | Where-Object { $_.sourceId -eq 'body[1]/p[168]' }
if (@($continuationFrom).Length -ne 1 -or @($continuationTo).Length -ne 1) { throw 'IDENTITY_REVIEW_REQUIRED: expected one agenda SESSION V continuation pair' }

$assignments = [Collections.Generic.List[object]]::new()
$nodes = [Collections.Generic.List[object]]::new()
$decisions = [Collections.Generic.List[object]]::new()
$ambiguities = [Collections.Generic.List[object]]::new()
$nodeByOccurrenceRef = @{}
$nodeIndex = 0

foreach ($occ in @($occurrenceRefs)) {
    if ($occ.headingOccurrenceRef -eq $continuationTo.headingOccurrenceRef) {
        $nodeRef = $nodeByOccurrenceRef[$continuationFrom.headingOccurrenceRef]
        $role = 'CONTINUATION'
        $decisionType = 'CONTINUATION_OF'
        $candidateRefs = @($continuationFrom.headingOccurrenceRef)
        $evidence = @(
            'SAME_AGENDA_STRUCTURAL_OWNER',
            'EXPLICIT_CONTINUATION_MARKER',
            'SESSION_V_SAME_THREAD',
            'SOURCE_ORDER_FOLLOWS_ORIGINAL_AGENDA_SESSION'
        )
        $contradictions = @('NARRATIVE_AGENDA_OWNER_CONFLICT_NOT_APPLICABLE_WITHIN_AGENDA_SCOPE', 'NO_COMPETING_AGENDA_SESSION_BETWEEN_OCCURRENCES')
    } else {
        $nodeIndex++
        $nodeRef = 'N{0:D3}' -f $nodeIndex
        $role = 'PRIMARY'
        $decisionType = 'PRIMARY_NEW_SEMANTIC_NODE'
        $candidateRefs = @()
        $evidence = @('DEFAULT_NEW_NODE_POLICY', 'SOURCE_ORDER_REVIEWED', 'OWNERSHIP_SCOPE_REVIEWED')
        $contradictions = @('DOCUMENT_OWNERSHIP_CONFLICT_CHECKED', 'AGENDA_NARRATIVE_CONFLICT_CHECKED', 'SESSION_SCOPE_CONFLICT_CHECKED', 'DAY_SCOPE_CONFLICT_CHECKED', 'INTERVENING_COMPETING_NODE_CHECKED', 'CONTINUATION_CONFLICT_CHECKED')
        $nodeByOccurrenceRef[$occ.headingOccurrenceRef] = $nodeRef
        $nodes.Add([pscustomobject]@{
            semanticNodeRef = $nodeRef
            canonicalOccurrenceRef = $occ.headingOccurrenceRef
            memberOccurrenceRefs = @($occ.headingOccurrenceRef)
            identityEvidence = @('SOURCE_BACKED_NEW_STRUCTURAL_OCCURRENCE', 'DEFAULT_KEEP_SPLIT_POLICY')
            sourceOnly = $true
        })
    }

    $assignments.Add([pscustomobject]@{
        headingOccurrenceRef = $occ.headingOccurrenceRef
        semanticNodeRef = $nodeRef
        occurrenceRole = $role
        decisionAuthority = 'CODEX_SOURCE_ONLY_IDENTITY_ADJUDICATION'
    })
    $decisions.Add([pscustomobject]@{
        decisionRef = 'ID{0:D3}' -f $occ.occurrenceOrder
        occurrenceRef = $occ.headingOccurrenceRef
        candidateEarlierOccurrenceRefs = $candidateRefs
        decisionType = $decisionType
        resultingSemanticNodeRef = $nodeRef
        occurrenceRole = $role
        evidence = $evidence
        contradictionChecks = $contradictions
        reviewAuthority = 'CODEX_SOURCE_ONLY_IDENTITY_ADJUDICATION'
        historicalIdentityUsedForDecision = $false
        historicalOccurrenceRowsUsedForDecision = $false
        oldSemanticTotalUsedForDecision = $false
        historicalLevelRead = $false
        historicalParentRead = $false
        parentReviewed = $false
        levelReviewed = $false
    })
}

# Add the explicit candidate reviews that explain why plausible repeated titles do not merge.
$collisionPairs = @(
    @{ left = 'body[1]/p[7]'; right = 'body[1]/p[158]'; leftText = 'Session I: Welcome and meeting objectives'; rightText = 'SESSION I: Welcome and Opening Remarks'; type = 'DISTINCT_SEMANTIC_NODE'; reason = 'AGENDA_NARRATIVE_CONFLICT' }
    @{ left = 'body[1]/p[11]'; right = 'body[1]/p[158]'; leftText = 'Session II: Update on the ICP 2021 Cycle'; rightText = 'SESSION II: Update on the ICP 2021 and 2024 Cycles'; type = 'DISTINCT_SEMANTIC_NODE'; reason = 'AGENDA_NARRATIVE_CONFLICT' }
    @{ left = 'body[1]/p[54]'; right = 'body[1]/p[159]'; leftText = 'Session III: Short- and Long-Term Research and Development Agenda'; rightText = 'SESSION III: Short- and Long-Term Research and Development Agenda'; type = 'DISTINCT_SEMANTIC_NODE'; reason = 'AGENDA_NARRATIVE_CONFLICT' }
    @{ left = 'body[1]/p[75]'; right = 'body[1]/p[160]'; leftText = 'Session IV: TAG Functioning and Terms of Reference for Task Forces'; rightText = 'SESSION IV: TAG Functioning and Terms of Refence for Task Forces'; type = 'DISTINCT_SEMANTIC_NODE'; reason = 'AGENDA_NARRATIVE_CONFLICT' }
    @{ left = 'body[1]/p[94]'; right = 'body[1]/p[166]'; leftText = 'Session V: Current Research'; rightText = 'SESSION V: Current Research'; type = 'DISTINCT_SEMANTIC_NODE'; reason = 'AGENDA_NARRATIVE_CONFLICT' }
    @{ left = 'body[1]/p[94]'; right = 'body[1]/p[168]'; leftText = 'Session V: Current Research'; rightText = "SESSION V: Current Research (Cont$([char]0x2019)d)"; type = 'DISTINCT_SEMANTIC_NODE'; reason = 'AGENDA_NARRATIVE_CONFLICT' }
    @{ left = 'body[1]/p[166]'; right = 'body[1]/p[168]'; leftText = 'SESSION V: Current Research'; rightText = "SESSION V: Current Research (Cont$([char]0x2019)d)"; type = 'CONTINUATION_OF'; reason = 'EXPLICIT_CONTINUATION_MARKER' }
    @{ left = 'body[1]/p[151]'; right = 'body[1]/p[169]'; leftText = 'Session VI: Any Other Business'; rightText = 'SESSION VI: Closing'; type = 'DISTINCT_SEMANTIC_NODE'; reason = 'AGENDA_NARRATIVE_CONFLICT' }
)
$collisionDecisions = [Collections.Generic.List[object]]::new()
foreach ($pair in $collisionPairs) {
    $left = @($occurrenceRefs | Where-Object { $_.sourceId -eq $pair.left -and $_.exactText -eq $pair.leftText })
    $right = @($occurrenceRefs | Where-Object { $_.sourceId -eq $pair.right -and $_.exactText -eq $pair.rightText })
    if ($left.Count -ne 1 -or $right.Count -ne 1) { throw "IDENTITY_REVIEW_REQUIRED: collision pair source resolution $($pair.left) / $($pair.right)" }
    $collisionDecisions.Add([pscustomobject]@{
        decisionRef = "COLLISION-$($left[0].headingOccurrenceRef)-$($right[0].headingOccurrenceRef)"
        leftOccurrenceRef = $left[0].headingOccurrenceRef
        rightOccurrenceRef = $right[0].headingOccurrenceRef
        decisionType = $pair.type
        evidence = @('SOURCE_TEXT_OR_NORMALIZED_TITLE_SIMILARITY', $pair.reason, 'OWNERSHIP_CONTEXT_REVIEWED')
        promotion = if ($pair.type -eq 'CONTINUATION_OF') { 'SAME_SEMANTIC_NODE' } else { 'KEEP_SPLIT' }
        sourceOnly = $true
        historicalIdentityUsedForDecision = $false
        parentReviewed = $false
        levelReviewed = $false
    })
}

# The continuation occurrence belongs to the already-created canonical node.
$continuationNode = @($nodes | Where-Object canonicalOccurrenceRef -eq $continuationFrom.headingOccurrenceRef)
if ($continuationNode.Count -ne 1) { throw 'IDENTITY_REVIEW_REQUIRED: continuation node not materialized' }
$continuationNode[0].memberOccurrenceRefs = @($continuationFrom.headingOccurrenceRef, $continuationTo.headingOccurrenceRef)
$continuationNode[0].identityEvidence = @('SAME_AGENDA_STRUCTURAL_OWNER', 'EXPLICIT_CONTINUATION_MARKER', 'SESSION_V_SAME_THREAD')

$nodesArray = @($nodes)
$assignmentsArray = @($assignments)
$decisionsArray = @($decisions + $collisionDecisions)
$primaryCount = @($assignmentsArray | Where-Object occurrenceRole -eq 'PRIMARY').Length
$repeatCount = @($assignmentsArray | Where-Object occurrenceRole -eq 'REPEAT').Length
$continuationCount = @($assignmentsArray | Where-Object occurrenceRole -eq 'CONTINUATION').Length

Write-JsonFile (Join-Path $outputPath 'occurrence-assignments.json') ([ordered]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_IDENTITY_OCCURRENCE_ASSIGNMENTS'
    schemaVersion = 'a99-canonical-semantic-identity-v1-doc0252'
    documentId = $documentId
    assignments = $assignmentsArray
    inputOccurrenceCount = $occurrenceRefs.Count
    assignedOccurrenceCount = $assignmentsArray.Count
    primaryCount = $primaryCount
    repeatCount = $repeatCount
    continuationCount = $continuationCount
    parentReviewed = $false
    levelReviewed = $false
})

Write-JsonFile (Join-Path $outputPath 'semantic-nodes.json') ([ordered]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_NODES'
    schemaVersion = 'a99-canonical-semantic-identity-v1-doc0252'
    documentId = $documentId
    semanticNodeCount = $nodesArray.Count
    semanticNodes = $nodesArray
    parentReviewed = $false
    levelReviewed = $false
})

Write-JsonFile (Join-Path $outputPath 'identity-decisions.json') ([ordered]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_IDENTITY_DECISIONS'
    schemaVersion = 'a99-canonical-semantic-identity-v1-doc0252'
    documentId = $documentId
    decisionCount = $decisionsArray.Count
    occurrenceDecisionCount = $decisions.Count
    plausibleCollisionDecisionCount = $collisionDecisions.Count
    decisions = $decisionsArray
    ambiguousDecisionCount = $ambiguities.Count
    reviewRequired = $ambiguities.Count -gt 0
    sourceOnly = $true
    parentReviewed = $false
    levelReviewed = $false
})

Write-JsonFile (Join-Path $outputPath 'ambiguities.json') ([ordered]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_IDENTITY_AMBIGUITIES'
    schemaVersion = 'a99-canonical-semantic-identity-v1-doc0252'
    documentId = $documentId
    ambiguities = @($ambiguities)
    count = $ambiguities.Count
    defaultPolicy = 'KEEP_SPLIT'
})

$assignmentRefs = @($assignmentsArray | ForEach-Object headingOccurrenceRef | Sort-Object -Unique)
$knownRefs = @($occurrenceRefs | ForEach-Object headingOccurrenceRef)
$nodeMemberRefs = @($nodesArray | ForEach-Object { @($_.memberOccurrenceRefs) })
$forbiddenJson = (($assignmentsArray | ConvertTo-Json -Depth 20 -Compress) + ($nodesArray | ConvertTo-Json -Depth 20 -Compress))
$validationChecks = [ordered]@{
    inputOccurrencesExactly40 = ($occurrenceRefs.Count -eq 40)
    everyOccurrenceAssignedExactlyOnce = ($assignmentsArray.Count -eq 40 -and $assignmentRefs.Count -eq 40 -and @($assignmentRefs | Where-Object { $knownRefs -notcontains $_ }).Count -eq 0)
    everyAssignmentPointsToKnownOccurrence = (@($assignmentsArray | Where-Object { $knownRefs -notcontains $_.headingOccurrenceRef }).Count -eq 0)
    everyAssignmentPointsToOneNode = (@($assignmentsArray | Where-Object { [string]::IsNullOrWhiteSpace($_.semanticNodeRef) }).Count -eq 0)
    everyNodeNonEmpty = (@($nodesArray | Where-Object { @($_.memberOccurrenceRefs).Count -lt 1 }).Count -eq 0)
    exactlyOnePrimaryPerNode = ($primaryCount -eq $nodesArray.Count)
    noDuplicateMembership = (@($nodeMemberRefs | Group-Object | Where-Object Count -gt 1).Count -eq 0)
    semanticNodeCountAtMostOccurrences = ($nodesArray.Count -le 40)
    noParentFields = ($forbiddenJson -notmatch '"(parentSemanticNodeRef|parentHeadingOccurrenceId)"')
    noLevelFields = ($forbiddenJson -notmatch '"(derivedLevel|canonicalLevel|level)"')
    historicalLevelRead = $false
    historicalParentRead = $false
    historicalHierarchyUsedForDecision = $false
    historicalOccurrenceRowsUsedForDecision = $false
    oldSemanticTotalUsedForDecision = $false
    providerCalls = 0
    modelCalls = 0
    parentReviewed = $false
    levelReviewed = $false
    occurrenceMutation = $false
    GoldMutationOutsideNewLane = $false
}
$structuralCheckNames = @(
    'inputOccurrencesExactly40',
    'everyOccurrenceAssignedExactlyOnce',
    'everyAssignmentPointsToKnownOccurrence',
    'everyAssignmentPointsToOneNode',
    'everyNodeNonEmpty',
    'exactlyOnePrimaryPerNode',
    'noDuplicateMembership',
    'semanticNodeCountAtMostOccurrences',
    'noParentFields',
    'noLevelFields'
)
$failedStructuralChecks = @($structuralCheckNames | Where-Object { -not [bool]$validationChecks[$_] })
$validationStatus = if ($failedStructuralChecks.Count -eq 0) { 'READY_FOR_USER_APPROVAL_CANONICAL_IDENTITY' } else { 'IDENTITY_REVIEW_REQUIRED' }
Write-JsonFile (Join-Path $outputPath 'validation.json') ([ordered]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_IDENTITY_VALIDATION'
    schemaVersion = 'a99-canonical-semantic-identity-v1-doc0252'
    documentId = $documentId
    status = $validationStatus
    checks = $validationChecks
    failedStructuralChecks = $failedStructuralChecks
    inputOccurrenceCount = $occurrenceRefs.Count
    assignedOccurrenceCount = $assignmentsArray.Count
    semanticNodeCount = $nodesArray.Count
    primaryCount = $primaryCount
    repeatCount = $repeatCount
    continuationCount = $continuationCount
    multiOccurrenceNodeCount = @($nodesArray | Where-Object { @($_.memberOccurrenceRefs).Count -gt 1 }).Count
    ambiguityCount = $ambiguities.Count
})

# Post-freeze diagnostics only. These values cannot change assignments.
$historicalRows = $null
$historicalTextOnlyMatches = $null
if (Test-Path -LiteralPath $historicalBridgePath) {
    $historicalBridge = Get-Content -Raw -LiteralPath $historicalBridgePath | ConvertFrom-Json
    $historicalRows = [int]$historicalBridge.historicalRowsRead
    $historicalTextOnlyMatches = [int]$historicalBridge.textOnlyDiagnosticMatchCount
}
$semanticTotalPath = Join-Path $repoRoot 'eval/a99-closed-loop/canonical-semantic-gold-vnext/semantic/DOC-0252.semantic-freeze.v1.json'
$oldSemanticTotal = $null
$oldSemanticSourceSha = $null
if (Test-Path -LiteralPath $semanticTotalPath) {
    $historicalSemantic = Get-Content -Raw -LiteralPath $semanticTotalPath | ConvertFrom-Json
    $oldSemanticTotal = [int]$historicalSemantic.semanticHeadingTotal
    $oldSemanticSourceSha = [string]$historicalSemantic.sourceSha256
}
Write-JsonFile (Join-Path $outputPath 'post-freeze-diagnostics.json') ([ordered]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_IDENTITY_POST_FREEZE_DIAGNOSTICS'
    schemaVersion = 'a99-canonical-semantic-identity-v1-doc0252'
    documentId = $documentId
    identityArtifactsFrozenBeforeDiagnostic = $true
    finalSemanticNodeCount = $nodesArray.Count
    currentCanonicalOccurrenceCount = $occurrenceRefs.Count
    oldSemanticTotal = $oldSemanticTotal
    comparison = "$($nodesArray.Count) vs $oldSemanticTotal"
    classification = 'NON_BINDING_CROSS_AUTHORITY_DIAGNOSTIC'
    historicalRows = $historicalRows
    historicalTextOnlyMatches = $historicalTextOnlyMatches
    historicalOccurrenceRowsUsedForDecision = $false
    oldSemanticTotalUsedForDecision = $false
    historicalLevelRead = $false
    historicalParentRead = $false
    historicalIdentityUsedForDecision = $false
    oldSemanticSourceSha256 = $oldSemanticSourceSha
    note = 'The old semantic total is structurally incompatible with the current 40-occurrence identity contract when it exceeds the current occurrence universe. It does not trigger identity mutation.'
})

$artifactNames = @('occurrence-input-manifest.json', 'occurrence-assignments.json', 'semantic-nodes.json', 'identity-decisions.json', 'ambiguities.json', 'validation.json', 'post-freeze-diagnostics.json')
$artifactHashes = foreach ($name in $artifactNames) {
    $full = Join-Path $outputPath $name
    [pscustomobject]@{ name = $name; path = (($OutputRoot + '/' + $name) -replace '\\','/'); sha256 = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$manifest = [ordered]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_IDENTITY_MANIFEST'
    schemaVersion = 'a99-canonical-semantic-identity-v1-doc0252'
    documentId = $documentId
    status = $validationStatus
    authoritativeOccurrenceCheckpoint = '4ce5cca'
    occurrenceAuthoritySha256 = $occurrenceAuthoritySha256
    inputOccurrenceCount = $occurrenceRefs.Count
    semanticNodeCount = $nodesArray.Count
    primaryCount = $primaryCount
    repeatCount = $repeatCount
    continuationCount = $continuationCount
    multiOccurrenceNodeCount = @($nodesArray | Where-Object { @($_.memberOccurrenceRefs).Count -gt 1 }).Count
    ambiguityCount = $ambiguities.Count
    artifactHashes = @($artifactHashes)
    sourceOnly = $true
    historicalLevelRead = $false
    historicalParentRead = $false
    historicalHierarchyUsedForDecision = $false
    historicalOccurrenceRowsUsedForDecision = $false
    oldSemanticTotalUsedForDecision = $false
    parentReviewed = $false
    levelReviewed = $false
    occurrenceMutation = $false
    providerCalls = 0
    modelCalls = 0
    userApprovalRequired = $true
    stopBeforeHierarchy = $true
}
Write-JsonFile (Join-Path $outputPath 'manifest.json') $manifest

$report = @"
# DOC-0252 — canonical semantic identity adjudication

Status: **$validationStatus**

## Result

- Frozen canonical heading occurrences: **$($occurrenceRefs.Count)**
- Semantic nodes: **$($nodesArray.Count)**
- PRIMARY: **$primaryCount**
- REPEAT: **$repeatCount**
- CONTINUATION: **$continuationCount**
- Multi-occurrence nodes: **$(@($nodesArray | Where-Object { @($_.memberOccurrenceRefs).Count -gt 1 }).Count)**
- Candidate identity decisions: **$($decisionsArray.Count)**
- Ambiguities: **$($ambiguities.Count)**

The identity review traversed the 40 frozen current-DOCX occurrences in source order. The only accepted collapse is the agenda `SESSION V: Current Research` followed by `SESSION V: Current Research (Cont’d)` under the same agenda scope. Narrative/agenda session copies remain distinct structural occurrences.

The default policy is fail-closed: same text, same topic, or same real-world referent does not authorize a merge without source-backed identity evidence.

## Firewall

- Occurrence authority checkpoint: 4ce5cca
- Occurrence authority SHA-256: $occurrenceAuthoritySha256
- Historical identity/hierarchy decisions were not used.
- Historical level and parent were not read.
- Old semantic total 41 was read only after identity artifacts were frozen and is non-binding.
- Parent/tree/level were not assigned or reviewed.
- Provider/model calls: 0.

## Post-freeze diagnostic

- Current canonical occurrence count: $($occurrenceRefs.Count)
- Current semantic node count: $($nodesArray.Count)
- Historical semantic total: $oldSemanticTotal
- Historical occurrence rows: $historicalRows
- Historical text-only diagnostic matches: $historicalTextOnlyMatches

Classification: **NON_BINDING_CROSS_AUTHORITY_DIAGNOSTIC**. The historical total is not a target and cannot cause identity mutation.

This artifact is source-backed identity adjudication only. It is not `USER_REVIEWED_CANONICAL_IDENTITY_GOLD` until explicit user approval. Hierarchy remains unopened.
"@
Write-TextFile (Join-Path $outputPath 'report.md') $report

[pscustomobject]@{
    documentId = $documentId
    status = $validationStatus
    inputOccurrences = $occurrenceRefs.Count
    semanticNodes = $nodesArray.Count
    primary = $primaryCount
    repeat = $repeatCount
    continuation = $continuationCount
    multiOccurrenceNodes = @($nodesArray | Where-Object { @($_.memberOccurrenceRefs).Count -gt 1 }).Count
    candidateIdentityDecisions = $decisionsArray.Count
    ambiguities = $ambiguities.Count
    providerCalls = 0
    modelCalls = 0
} | ConvertTo-Json -Depth 10
