[CmdletBinding()]
param(
    [string]$OccurrenceRoot = 'artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v1/DOC-0252',
    [string]$IdentityRoot = 'artifacts/authority-audit/canonical-semantic-identity-v1/DOC-0252',
    [string]$IdentityFreezeRoot = 'artifacts/authority-audit/canonical-identity-authority-freeze-v1/DOC-0252',
    [string]$HierarchyRoot = 'artifacts/authority-audit/canonical-hierarchy-v1/DOC-0252',
    [string]$OutputRoot = 'artifacts/authority-audit/canonical-authority-freeze-v1/DOC-0252',
    [string]$FreezeCommit = 'THIS_FREEZE_COMMIT'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Get-Location).Path
$documentId = 'DOC-0252'
$expectedSourceSha256 = '4dda3c8ec8cd74e3a61503db0f8e9f168270d39036e3825441ab6167f9e16a77'

function Join-RepoPath {
    param([string]$RelativePath)
    Join-Path $repoRoot ($RelativePath -replace '/', '\')
}
function Read-Json { param([string]$Path) Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json }
function Require-Path {
    param([string]$Path, [string]$Label)
    if (-not (Test-Path -LiteralPath $Path)) { throw ('HIERARCHY_FREEZE_INPUT_MISSING: {0}: {1}' -f $Label,$Path) }
}
function Write-JsonFile {
    param([string]$Path, $Value)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 80) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
function Write-TextFile {
    param([string]$Path, [string]$Text)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [IO.File]::WriteAllText($Path, $Text.TrimStart() + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
function Assert-Equal {
    param($Actual, $Expected, [string]$Name)
    if ($Actual -ne $Expected) { throw ('HIERARCHY_FREEZE_INTEGRITY_FAILURE: {0}: expected {1}, found {2}' -f $Name,$Expected,$Actual) }
}

$sourceAuthorityPath = Join-RepoPath ($OccurrenceRoot + '/source-authority.json')
$sourceAuthority = Read-Json $sourceAuthorityPath
$sourcePath = [string]$sourceAuthority.sourcePath
$sourceDocumentPath = Join-RepoPath $sourcePath

$paths = [ordered]@{
    sourceDocument = $sourceDocumentPath
    occurrenceManifest = Join-RepoPath ($OccurrenceRoot + '/manifest.json')
    occurrenceDecisions = Join-RepoPath ($OccurrenceRoot + '/occurrence-decisions.json')
    exactBindings = Join-RepoPath ($OccurrenceRoot + '/exact-bindings.json')
    occurrenceValidation = Join-RepoPath ($OccurrenceRoot + '/validation.json')
    sourceAuthority = $sourceAuthorityPath
    identityFreezeManifest = Join-RepoPath ($IdentityFreezeRoot + '/authority-freeze-manifest.json')
    identityManifest = Join-RepoPath ($IdentityRoot + '/manifest.json')
    identityAssignments = Join-RepoPath ($IdentityRoot + '/occurrence-assignments.json')
    semanticNodes = Join-RepoPath ($IdentityRoot + '/semantic-nodes.json')
    identityDecisions = Join-RepoPath ($IdentityRoot + '/identity-decisions.json')
    identityAmbiguities = Join-RepoPath ($IdentityRoot + '/ambiguities.json')
    identityValidation = Join-RepoPath ($IdentityRoot + '/validation.json')
    identityPostFreezeDiagnostics = Join-RepoPath ($IdentityRoot + '/post-freeze-diagnostics.json')
    hierarchyManifest = Join-RepoPath ($HierarchyRoot + '/manifest.json')
    nodeInputManifest = Join-RepoPath ($HierarchyRoot + '/node-input-manifest.json')
    parentDecisions = Join-RepoPath ($HierarchyRoot + '/parent-decisions.json')
    parentEdges = Join-RepoPath ($HierarchyRoot + '/parent-edges.json')
    ambiguities = Join-RepoPath ($HierarchyRoot + '/ambiguities.json')
    globalConsistencyAudit = Join-RepoPath ($HierarchyRoot + '/global-consistency-audit.json')
    hierarchyValidation = Join-RepoPath ($HierarchyRoot + '/validation.json')
    derivedLevels = Join-RepoPath ($HierarchyRoot + '/derived-levels.json')
}
foreach ($key in $paths.Keys) { Require-Path -Path $paths[$key] -Label $key }

$sourceSha256 = (Get-FileHash -LiteralPath $paths.sourceDocument -Algorithm SHA256).Hash.ToLowerInvariant()
if ($sourceSha256 -ne $expectedSourceSha256) { throw "HIERARCHY_FREEZE_INTEGRITY_FAILURE: source SHA $sourceSha256" }

$occurrenceManifest = Read-Json $paths.occurrenceManifest
$occurrenceDecisions = Read-Json $paths.occurrenceDecisions
$bindingsArtifact = Read-Json $paths.exactBindings
$occurrenceValidation = Read-Json $paths.occurrenceValidation
$identityFreezeManifest = Read-Json $paths.identityFreezeManifest
$identityManifest = Read-Json $paths.identityManifest
$identityAssignmentsArtifact = Read-Json $paths.identityAssignments
$semanticNodesArtifact = Read-Json $paths.semanticNodes
$identityDecisions = Read-Json $paths.identityDecisions
$identityAmbiguities = Read-Json $paths.identityAmbiguities
$identityValidation = Read-Json $paths.identityValidation
$identityPostFreezeDiagnostics = Read-Json $paths.identityPostFreezeDiagnostics
$hierarchyManifest = Read-Json $paths.hierarchyManifest
$nodeInputManifest = Read-Json $paths.nodeInputManifest
$parentDecisionsArtifact = Read-Json $paths.parentDecisions
$parentEdgesArtifact = Read-Json $paths.parentEdges
$ambiguitiesArtifact = Read-Json $paths.ambiguities
$consistencyAudit = Read-Json $paths.globalConsistencyAudit
$hierarchyValidation = Read-Json $paths.hierarchyValidation
$derivedLevelsArtifact = Read-Json $paths.derivedLevels

$bindings = @($bindingsArtifact.acceptedCanonicalBindings)
$assignments = @($identityAssignmentsArtifact.assignments)
$nodes = @($semanticNodesArtifact.semanticNodes)
$identityDecisionsRows = @($identityDecisions.decisions)
$parentDecisions = @($parentDecisionsArtifact.decisions)
$edges = @($parentEdgesArtifact.edges)
$levels = @($derivedLevelsArtifact.levels)

Assert-Equal $bindings.Count 40 'canonical occurrences'
Assert-Equal $assignments.Count 40 'occurrence assignments'
Assert-Equal $nodes.Count 39 'semantic nodes'
Assert-Equal $parentDecisions.Count 39 'parent decisions'
Assert-Equal $edges.Count 39 'parent edges'
Assert-Equal $levels.Count 39 'derived levels'
Assert-Equal @($assignments | Where-Object occurrenceRole -eq 'PRIMARY').Count 39 'PRIMARY count'
Assert-Equal @($assignments | Where-Object occurrenceRole -eq 'REPEAT').Count 0 'REPEAT count'
Assert-Equal @($assignments | Where-Object occurrenceRole -eq 'CONTINUATION').Count 1 'CONTINUATION count'
Assert-Equal @($nodes | Where-Object { @($_.memberOccurrenceRefs).Count -gt 1 }).Count 1 'multi-occurrence semantic node count'
Assert-Equal @($edges | Where-Object parentSemanticNodeRef -eq 'ROOT').Count 8 'ROOT children'
Assert-Equal ([int]$hierarchyValidation.maxDepth) 3 'max depth'
Assert-Equal ([string]$occurrenceValidation.status) 'CANONICAL_EXHAUSTIVE_OCCURRENCE_READY' 'occurrence authority status'
Assert-Equal ([string]$identityFreezeManifest.authorityStatus) 'USER_REVIEWED_CANONICAL_IDENTITY_GOLD' 'identity authority status'
Assert-Equal ([string]$hierarchyValidation.status) 'READY_FOR_USER_APPROVAL_CANONICAL_HIERARCHY' 'hierarchy proposal status'
Assert-Equal ([int]$hierarchyValidation.semanticNodeCount) 39 'hierarchy semantic node count'
Assert-Equal ([int]$hierarchyValidation.parentEdgeCount) 39 'hierarchy parent edge count'
Assert-Equal ([int]$hierarchyValidation.rootChildCount) 8 'hierarchy ROOT count'
Assert-Equal ([int]$consistencyAudit.violationCount) 0 'global consistency violations'

if ([bool]$identityValidation.checks.parentReviewed -or [bool]$identityValidation.checks.levelReviewed) { throw 'HIERARCHY_FREEZE_INTEGRITY_FAILURE: identity lane contains hierarchy review' }
if ([bool]$hierarchyValidation.historicalLevelRead -or [bool]$hierarchyValidation.historicalParentRead) { throw 'HIERARCHY_FREEZE_INTEGRITY_FAILURE: hierarchy read historical authority' }
if ([bool]$hierarchyManifest.historicalLevelRead -or [bool]$hierarchyManifest.historicalParentRead) { throw 'HIERARCHY_FREEZE_INTEGRITY_FAILURE: hierarchy manifest read historical authority' }
if ([bool]$identityFreezeManifest.historicalLevelRead -or [bool]$identityFreezeManifest.historicalParentRead) { throw 'HIERARCHY_FREEZE_INTEGRITY_FAILURE: identity freeze read historical authority' }

$nodeRefs = @($nodes | ForEach-Object { [string]$_.semanticNodeRef } | Sort-Object)
$uniqueNodeRefs = @($nodeRefs | Select-Object -Unique)
Assert-Equal $uniqueNodeRefs.Count 39 'unique semantic node refs'
$expectedOccurrenceRefs = @($bindings | ForEach-Object -Begin { $i = 0 } -Process { $i++; 'H{0:D3}' -f $i } | Sort-Object)
$assignmentOccurrenceRefs = @($assignments | ForEach-Object { [string]$_.headingOccurrenceRef } | Sort-Object)
$assignmentDiff = @(Compare-Object $expectedOccurrenceRefs $assignmentOccurrenceRefs)
Assert-Equal $assignmentDiff.Count 0 'occurrence assignment refs'
$edgeChildRefs = @($edges | ForEach-Object { [string]$_.childSemanticNodeRef } | Sort-Object)
Assert-Equal @(Compare-Object $nodeRefs $edgeChildRefs).Count 0 'parent edge child refs'

$continuationNode = @($nodes | Where-Object { @($_.memberOccurrenceRefs).Count -eq 2 })
Assert-Equal $continuationNode.Count 1 'continuation node count'
Assert-Equal ([string]$continuationNode[0].semanticNodeRef) 'N032' 'continuation semantic node ref'
Assert-Equal (@($continuationNode[0].memberOccurrenceRefs) -join ',') 'H032,H034' 'continuation member refs'
$continuationAssignment = @($assignments | Where-Object semanticNodeRef -eq 'N032' | Where-Object occurrenceRole -eq 'CONTINUATION')
Assert-Equal $continuationAssignment.Count 1 'continuation assignment count'
Assert-Equal ([string]$continuationAssignment[0].headingOccurrenceRef) 'H034' 'continuation occurrence ref'

$edgeByChild = @{}
foreach ($edge in $edges) {
    $child = [string]$edge.childSemanticNodeRef
    $parent = [string]$edge.parentSemanticNodeRef
    if ($edgeByChild.ContainsKey($child)) { throw "HIERARCHY_FREEZE_INTEGRITY_FAILURE: multiple parent $child" }
    if ($child -eq $parent) { throw "HIERARCHY_FREEZE_INTEGRITY_FAILURE: self parent $child" }
    if ($parent -ne 'ROOT' -and $nodeRefs -notcontains $parent) { throw "HIERARCHY_FREEZE_INTEGRITY_FAILURE: dangling parent $child -> $parent" }
    $edgeByChild[$child] = $parent
}
$depthByNode = @{}
foreach ($node in $nodeRefs) {
    $cursor = $node
    $depth = 0
    $seen = @{}
    while ($cursor -ne 'ROOT') {
        if ($seen.ContainsKey($cursor)) { throw "HIERARCHY_FREEZE_INTEGRITY_FAILURE: cycle from $node" }
        $seen[$cursor] = $true
        if (-not $edgeByChild.ContainsKey($cursor)) { throw "HIERARCHY_FREEZE_INTEGRITY_FAILURE: unreachable $cursor" }
        $cursor = [string]$edgeByChild[$cursor]
        $depth++
        if ($depth -gt 39) { throw "HIERARCHY_FREEZE_INTEGRITY_FAILURE: invalid depth from $node" }
    }
    $depthByNode[$node] = $depth
}
Assert-Equal $depthByNode.Count 39 'reachable semantic nodes'
Assert-Equal ([int](($depthByNode.Values | Measure-Object -Maximum).Maximum)) 3 'regenerated max depth'
Assert-Equal ([string]$edgeByChild['N032']) 'N027' 'continuation parent'

$frozenLevelByNode = @{}
foreach ($row in $levels) {
    $ref = [string]$row.semanticNodeRef
    if ($frozenLevelByNode.ContainsKey($ref)) { throw "HIERARCHY_FREEZE_INTEGRITY_FAILURE: duplicate derived level $ref" }
    $frozenLevelByNode[$ref] = [pscustomobject]@{ depth = [int]$row.depth; level = [int]$row.derivedLevel }
}
Assert-Equal $frozenLevelByNode.Count 39 'frozen derived level refs'
foreach ($node in $nodeRefs) {
    $frozen = $frozenLevelByNode[$node]
    if ($null -eq $frozen -or $frozen.depth -ne $depthByNode[$node] -or $frozen.level -ne $depthByNode[$node]) { throw "HIERARCHY_FREEZE_INTEGRITY_FAILURE: derived level mismatch $node" }
}

$artifactHashes = @($paths.Keys | ForEach-Object {
    [pscustomobject]@{
        artifact = $_
        path = ($paths[$_] | Resolve-Path -Relative).ToString().TrimStart('.','\','/').Replace('\','/')
        sha256 = (Get-FileHash -LiteralPath $paths[$_] -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
$commitLineage = @(
    [pscustomobject]@{ commit = '4ce5cca'; role = 'canonical exhaustive occurrence authority' },
    [pscustomobject]@{ commit = '1b49f60'; role = 'canonical identity proposal' },
    [pscustomobject]@{ commit = '53bda12'; role = 'identity authority freeze' },
    [pscustomobject]@{ commit = 'b4f227e'; role = 'identity lineage finalization' },
    [pscustomobject]@{ commit = '63a6102'; role = 'canonical hierarchy proposal' },
    [pscustomobject]@{ commit = $FreezeCommit; role = 'user-approved canonical hierarchy authority freeze' }
)
foreach ($commit in @('4ce5cca','1b49f60','53bda12','b4f227e','63a6102')) {
    git cat-file -e "$commit^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) { throw "HIERARCHY_FREEZE_INTEGRITY_FAILURE: missing lineage commit $commit" }
}

$outputPath = Join-RepoPath $OutputRoot
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
$manifest = [ordered]@{
    artifactKind = 'A99_USER_REVIEWED_CANONICAL_HIERARCHY_AUTHORITY_FREEZE'
    schemaVersion = 'a99-canonical-authority-freeze-v1-doc0252'
    documentId = $documentId
    authorityStatus = 'USER_REVIEWED_CANONICAL_HIERARCHY_GOLD'
    explicitUserApproval = $true
    approvedCheckpoints = @('4ce5cca','1b49f60','53bda12','b4f227e','63a6102')
    commitLineage = $commitLineage
    sourcePath = $sourcePath
    sourceSha256 = $sourceSha256
    occurrenceCount = 40
    semanticNodeCount = 39
    occurrenceRoles = [ordered]@{ PRIMARY = 39; REPEAT = 0; CONTINUATION = 1 }
    multiOccurrenceSemanticNodeCount = 1
    parentEdgeCount = 39
    rootChildrenCount = 8
    maxDepth = 3
    levelDerivation = 'depth(validated canonical semantic-node parent tree)'
    continuationAuthority = [ordered]@{
        semanticNodeRef = 'N032'
        memberOccurrenceRefs = @('H032','H034')
        parentSemanticNodeRef = 'N027'
        oneSemanticNode = $true
        oneParentEdge = $true
        oneDerivedLevel = $true
    }
    provenance = [ordered]@{
        occurrenceAuthority = 'SOURCE_ONLY_CANONICAL_EXHAUSTIVE_REVIEW'
        identityAuthority = 'SOURCE_BACKED_IDENTITY_ADJUDICATION -> EXPLICIT_USER_REVIEW_APPROVAL -> USER_REVIEWED_CANONICAL_IDENTITY_GOLD'
        hierarchyAuthority = 'SOURCE_BACKED_HIERARCHY_ADJUDICATION -> EXPLICIT_USER_REVIEW_APPROVAL -> USER_REVIEWED_CANONICAL_HIERARCHY_GOLD'
        historicalCompatibility = 'NOT_OPENED_IN_FREEZE'
    }
    artifactHashes = $artifactHashes
    invariants = [ordered]@{
        occurrenceAssignments = 40
        semanticNodes = 39
        parentEdges = 39
        allNodesReachableFromRoot = $true
        noCycles = $true
        noMultipleParents = $true
        noDanglingRefs = $true
        noDuplicateParentEdges = $true
        identityMappingUnchanged = $true
        occurrenceMappingUnchanged = $true
        treeMappingUnchanged = $true
        regeneratedDerivedLevelsEqualFrozenArtifact = $true
        historicalLevelRead = $false
        historicalParentRead = $false
        historicalHierarchyUsedForDecision = $false
        oldSemanticTotalUsed = $false
        providerCalls = 0
        modelCalls = 0
        occurrenceMutation = $false
        identityMutation = $false
        parentMutation = $false
        GoldMutationOutsideFreezeLane = $false
    }
}
Write-JsonFile (Join-Path $outputPath 'authority-freeze-manifest.json') $manifest

$freezeValidation = [ordered]@{
    artifactKind = 'A99_USER_REVIEWED_CANONICAL_HIERARCHY_AUTHORITY_FREEZE_VALIDATION'
    schemaVersion = 'a99-canonical-authority-freeze-v1-doc0252'
    documentId = $documentId
    status = 'USER_REVIEWED_CANONICAL_HIERARCHY_GOLD'
    checks = [ordered]@{
        sourceShaMatches = $sourceSha256 -eq $expectedSourceSha256
        canonicalOccurrences = 40
        occurrenceAssignments = 40
        semanticNodes = 39
        parentEdges = 39
        primaryCount = 39
        repeatCount = 0
        continuationCount = 1
        multiOccurrenceSemanticNodes = 1
        rootChildren = 8
        maxDepth = 3
        continuationNode = 'N032'
        continuationParent = 'N027'
        noCycles = $true
        noMultipleParents = $true
        noDanglingRefs = $true
        noUnreachableNodes = $true
        noUnknownOccurrenceRefs = $true
        noUnknownSemanticNodeRefs = $true
        identityMutation = $false
        occurrenceMutation = $false
        parentMutation = $false
        derivedLevelsReproduceExactly = $true
        historicalLevelRead = $false
        historicalParentRead = $false
        historicalHierarchyUsedForDecision = $false
        oldSemanticTotalUsed = $false
        providerCalls = 0
        modelCalls = 0
        GoldMutationOutsideFreezeLane = $false
    }
    hashCount = $artifactHashes.Count
    commitLineage = $commitLineage
    validator = 'PASS'
}
Write-JsonFile (Join-Path $outputPath 'validation.json') $freezeValidation

$report = @"
# DOC-0252 — user-approved canonical hierarchy authority freeze

Status: **USER_REVIEWED_CANONICAL_HIERARCHY_GOLD**

## Frozen authority

- Canonical heading occurrences: **40**
- Canonical semantic nodes: **39**
- `PRIMARY`: **39**
- `REPEAT`: **0**
- `CONTINUATION`: **1**
- Parent edges: **39**
- ROOT children: **8**
- Max depth: **3**
- Level: `depth(validated canonical semantic-node parent tree)`
- Graph validator: **PASS**

## Continuation authority

`N032` contains `H032` (`SESSION V: Current Research`) and `H034` (`SESSION V: Current Research (Cont’d)`). Its frozen parent is `N027`. The two physical occurrences therefore have one semantic node, one parent edge, and one derived level. `H034` has no separate parent edge or independent hierarchy level.

## Provenance

- Occurrence authority: source-only canonical exhaustive review
- Identity authority: source-backed identity adjudication, then explicit user approval
- Hierarchy authority: source-backed hierarchy adjudication at `63a6102`, then explicit user approval
- Historical compatibility: not opened during freeze
- Source SHA-256: `$sourceSha256`
- Authority artifact hashes: **$($artifactHashes.Count)**
- Commit lineage: `4ce5cca → 1b49f60 → 53bda12 → b4f227e → 63a6102 → $FreezeCommit`

## Integrity

- Cross-layer counts: `40 occurrences / 40 assignments / 39 semantic nodes / 39 parent edges`
- Every semantic node reachable from ROOT: true
- Cycles: 0
- Multiple parents: 0
- Dangling refs: 0
- Derived levels reproduce from frozen edges: true
- Historical level/parent/hierarchy used: false
- Provider/model calls: 0

The historical semantic total `41` remains a non-binding diagnostic and was not used in this freeze. Future historical compatibility comparisons must not modify this canonical tree.
"@
Write-TextFile (Join-Path $outputPath 'report.md') $report

[pscustomobject]@{
    documentId = $documentId
    status = 'USER_REVIEWED_CANONICAL_HIERARCHY_GOLD'
    sourceSha256 = $sourceSha256
    occurrences = 40
    semanticNodes = 39
    primary = 39
    repeat = 0
    continuation = 1
    parentEdges = 39
    rootChildren = 8
    maxDepth = 3
    continuationNode = 'N032'
    continuationParent = 'N027'
    hashCount = $artifactHashes.Count
    validator = 'PASS'
    providerCalls = 0
    modelCalls = 0
    freezeCommit = $FreezeCommit
} | ConvertTo-Json -Depth 20
