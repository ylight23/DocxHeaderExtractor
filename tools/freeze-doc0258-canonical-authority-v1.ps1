[CmdletBinding()]
param(
    [string]$OccurrenceRoot = "artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v2-span-aware/DOC-0258",
    [string]$IdentityRoot = "artifacts/authority-audit/canonical-semantic-identity-v1/DOC-0258",
    [string]$HierarchyRoot = "artifacts/authority-audit/canonical-hierarchy-v1/DOC-0258",
    [string]$OutputRoot = "artifacts/authority-audit/canonical-authority-freeze-v1/DOC-0258"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Get-Location).Path
$docId = 'DOC-0258'
$outputDir = Join-Path $repoRoot $OutputRoot

function Read-JsonFile {
    param([string]$Path)
    return Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
}

function Write-JsonFile {
    param([string]$Path, $Value)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [System.IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 60) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function Write-TextFile {
    param([string]$Path, [string]$Text)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [System.IO.File]::WriteAllText($Path, $Text.TrimStart() + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function Assert-Equal {
    param($Actual, $Expected, [string]$Name)
    if ($Actual -ne $Expected) { throw "Freeze invariant failed: $Name expected $Expected, found $Actual." }
}

function Get-RelativePath {
    param([string]$Path)
    return [System.IO.Path]::GetRelativePath($repoRoot, (Resolve-Path -LiteralPath $Path).Path).Replace('\', '/')
}

$occurrenceBindingsPath = Join-Path $repoRoot "$OccurrenceRoot/exact-bindings.json"
$occurrenceUniversePath = Join-Path $repoRoot "$OccurrenceRoot/source-span-universe.json"
$occurrenceValidationPath = Join-Path $repoRoot "$OccurrenceRoot/validation.json"
$identityAssignmentsPath = Join-Path $repoRoot "$IdentityRoot/occurrence-assignments.json"
$identityNodesPath = Join-Path $repoRoot "$IdentityRoot/semantic-nodes.json"
$identityManifestPath = Join-Path $repoRoot "$IdentityRoot/manifest.json"
$identityValidationPath = Join-Path $repoRoot "$IdentityRoot/validation.json"
$challengeValidationPath = Join-Path $repoRoot "$IdentityRoot/challenge-audit-v1/validation.json"
$parentEdgesPath = Join-Path $repoRoot "$HierarchyRoot/parent-edges.json"
$parentDecisionsPath = Join-Path $repoRoot "$HierarchyRoot/parent-decisions.json"
$derivedLevelsPath = Join-Path $repoRoot "$HierarchyRoot/derived-levels.json"
$hierarchyManifestPath = Join-Path $repoRoot "$HierarchyRoot/manifest.json"
$hierarchyValidationPath = Join-Path $repoRoot "$HierarchyRoot/validation.json"

$requiredPaths = @(
    $occurrenceBindingsPath, $occurrenceUniversePath, $occurrenceValidationPath,
    $identityAssignmentsPath, $identityNodesPath, $identityManifestPath, $identityValidationPath, $challengeValidationPath,
    $parentEdgesPath, $parentDecisionsPath, $derivedLevelsPath, $hierarchyManifestPath, $hierarchyValidationPath
)
foreach ($path in $requiredPaths) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing freeze input: $path" }
}

$occurrenceBindings = Read-JsonFile $occurrenceBindingsPath
$occurrenceUniverse = Read-JsonFile $occurrenceUniversePath
$occurrenceValidation = Read-JsonFile $occurrenceValidationPath
$identityAssignments = Read-JsonFile $identityAssignmentsPath
$identityNodes = Read-JsonFile $identityNodesPath
$identityManifest = Read-JsonFile $identityManifestPath
$identityValidation = Read-JsonFile $identityValidationPath
$challengeValidation = Read-JsonFile $challengeValidationPath
$parentEdges = Read-JsonFile $parentEdgesPath
$parentDecisions = Read-JsonFile $parentDecisionsPath
$derivedLevels = Read-JsonFile $derivedLevelsPath
$hierarchyManifest = Read-JsonFile $hierarchyManifestPath
$hierarchyValidation = Read-JsonFile $hierarchyValidationPath

$bindings = @($occurrenceBindings.bindings)
$assignments = @($identityAssignments.assignments)
$nodes = @($identityNodes.semanticNodes)
$edges = @($parentEdges.edges)
$decisions = @($parentDecisions.decisions)
$levels = @($derivedLevels.levels)
$sourceSha = [string]$occurrenceBindings.sourceSha256

Assert-Equal $bindings.Count 44 'canonical occurrence count'
Assert-Equal $assignments.Count 44 'identity assignment count'
Assert-Equal $nodes.Count 44 'semantic node count'
Assert-Equal $edges.Count 44 'parent edge count'
Assert-Equal $decisions.Count 44 'parent decision count'
Assert-Equal $levels.Count 44 'derived level count'
Assert-Equal @($assignments | Where-Object occurrenceRole -ne 'PRIMARY').Count 0 'non-primary occurrence count'
Assert-Equal @($identityNodes.semanticNodes | Where-Object { @($_.memberOccurrenceRefs).Count -ne 1 }).Count 0 'multi-occurrence semantic nodes'
Assert-Equal @($edges | Where-Object { $_.parentSemanticNodeRef -eq 'ROOT' }).Count 3 'ROOT child count'

if ([string]$occurrenceValidation.status -ne 'CANONICAL_EXHAUSTIVE_OCCURRENCE_READY') { throw 'Occurrence authority is not ready.' }
if ([string]$identityManifest.status -ne 'READY_FOR_USER_APPROVAL_CANONICAL_IDENTITY') { throw 'Identity authority is not ready for approval.' }
if ([string]$challengeValidation.status -ne 'IDENTITY_PROPOSAL_CORROBORATED_44_NODES') { throw 'Identity challenge did not corroborate the proposal.' }
if ([string]$hierarchyValidation.status -ne 'READY_FOR_USER_APPROVAL_CANONICAL_HIERARCHY') { throw 'Hierarchy authority is not ready for approval.' }
if ([int]$challengeValidation.mergeRelationsFound -ne 0) { throw 'Identity challenge contains a merge relation.' }
if ([int]$hierarchyValidation.cycleNodeCount -ne 0) { throw 'Hierarchy contains a cycle.' }
if ([int]$hierarchyValidation.rootChildCount -ne 3) { throw 'Hierarchy ROOT child count changed.' }

$occurrenceRefs = @($bindings | ForEach-Object -Begin { $i = 0 } -Process { $i++; 'H{0:D3}' -f $i })
$assignmentRefs = @($assignments | Select-Object -ExpandProperty headingOccurrenceRef | Sort-Object)
$nodeRefs = @($nodes | Select-Object -ExpandProperty semanticNodeRef | Sort-Object)
$edgeChildRefs = @($edges | Select-Object -ExpandProperty childSemanticNodeRef | Sort-Object)
if (@(Compare-Object $occurrenceRefs $assignmentRefs).Count -ne 0) { throw 'Occurrence refs do not match identity assignments.' }
if ($nodeRefs.Count -ne 44 -or @($nodeRefs | Select-Object -Unique).Count -ne 44) { throw 'Semantic node refs are not unique.' }
if (@(Compare-Object $nodeRefs $edgeChildRefs).Count -ne 0) { throw 'Semantic node refs do not match parent edge children.' }

$edgeByChild = @{}
foreach ($edge in $edges) {
    if ($edgeByChild.ContainsKey([string]$edge.childSemanticNodeRef)) { throw 'Multiple parent edge detected.' }
    if ($edge.parentSemanticNodeRef -ne 'ROOT' -and $nodeRefs -notcontains [string]$edge.parentSemanticNodeRef) { throw 'Dangling parent edge detected.' }
    if ($edge.childSemanticNodeRef -eq $edge.parentSemanticNodeRef) { throw 'Self-parent edge detected.' }
    $edgeByChild[[string]$edge.childSemanticNodeRef] = [string]$edge.parentSemanticNodeRef
}

$depthByNode = @{}
foreach ($node in $nodeRefs) {
    $current = [string]$node
    $seen = [System.Collections.Generic.HashSet[string]]::new()
    $depth = 0
    while ($current -ne 'ROOT') {
        if (-not $seen.Add($current)) { throw "Cycle detected from $node." }
        if (-not $edgeByChild.ContainsKey($current)) { throw "Unreachable node $current." }
        $current = $edgeByChild[$current]
        $depth++
        if ($depth -gt 44) { throw "Invalid depth while resolving $node." }
    }
    $depthByNode[$node] = $depth
}

$derivedByNode = @{}
foreach ($level in $levels) { $derivedByNode[[string]$level.semanticNodeRef] = [int]$level.derivedLevel }
if ($derivedByNode.Count -ne 44) { throw 'Derived level refs are incomplete.' }
foreach ($node in $nodeRefs) {
    if ($derivedByNode[$node] -ne $depthByNode[$node]) { throw "Derived level mismatch for $node." }
}

$sourceShaValues = @(@(
    [string]$occurrenceBindings.sourceSha256,
    [string]$occurrenceUniverse.sourceSha256,
    [string]$identityManifest.sourceSha256,
    [string]$hierarchyManifest.sourceSha256
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
if ($sourceShaValues.Count -ne 1 -or $sourceShaValues[0] -ne $sourceSha) { throw 'Source SHA is not consistent across authority layers.' }

$artifactPaths = [ordered]@{
    occurrenceAuthority = $occurrenceBindingsPath
    occurrenceUniverse = $occurrenceUniversePath
    occurrenceValidation = $occurrenceValidationPath
    identityAssignments = $identityAssignmentsPath
    semanticNodes = $identityNodesPath
    identityManifest = $identityManifestPath
    identityValidation = $identityValidationPath
    identityChallengeValidation = $challengeValidationPath
    parentEdges = $parentEdgesPath
    parentDecisions = $parentDecisionsPath
    derivedLevels = $derivedLevelsPath
    hierarchyManifest = $hierarchyManifestPath
    hierarchyValidation = $hierarchyValidationPath
}
$artifactHashes = [System.Collections.Generic.List[object]]::new()
foreach ($key in $artifactPaths.Keys) {
    $path = $artifactPaths[$key]
    $artifactHashes.Add([pscustomobject]@{
        artifact = $key
        path = Get-RelativePath $path
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    })
}

$commitLineage = @('78af9bf', '3ffe8f1', '09af8f2', '569b528')
foreach ($commit in $commitLineage) {
    git cat-file -e "$commit^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) { throw "Missing commit in lineage: $commit" }
}

Remove-Item -LiteralPath $outputDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

$authoritySummary = [pscustomobject]@{
    authorityStatus = 'USER_REVIEWED_CANONICAL_HIERARCHY_GOLD'
    documentId = $docId
    occurrenceCount = 44
    semanticNodeCount = 44
    occurrenceRoles = [pscustomobject]@{ PRIMARY = 44; REPEAT = 0; CONTINUATION = 0 }
    parentEdgeCount = 44
    rootChildCount = 3
    maxDepth = [int](($depthByNode.Values | Measure-Object -Maximum).Maximum)
    levelDerivation = 'DEPTH_FROM_VALIDATED_TREE_ONLY'
    legacySemanticTotal = 37
    legacySemanticTotalStatus = 'SUPERSEDED_CONTRADICTED_FOR_DOC0258_CANONICAL_AUTHORITY'
    sourceSha256 = $sourceSha
    identityProposalStatus = [string]$identityManifest.status
    identityChallengeStatus = [string]$challengeValidation.status
    hierarchyProposalStatus = [string]$hierarchyValidation.status
    provenance = [pscustomobject]@{
        occurrenceReview = 'CODEX_SOURCE_ONLY_ADJUDICATION'
        identityProposal = 'CODEX_SOURCE_BACKED'
        identityChallenge = 'CODEX_SOURCE_BACKED_CHALLENGE_CORROBORATED'
        hierarchyProposal = 'CODEX_SOURCE_BACKED'
        finalPromotion = 'EXPLICIT_USER_APPROVAL'
        historicalCompatibilityTree = 'NOT_USED_OR_MODIFIED'
    }
}

$freezeManifest = [pscustomobject]@{
    artifactKind = 'A99_USER_REVIEWED_CANONICAL_AUTHORITY_FREEZE'
    schemaVersion = 'a99-canonical-authority-freeze-v1'
    documentId = $docId
    authorityStatus = 'USER_REVIEWED_CANONICAL_HIERARCHY_GOLD'
    userApproval = 'User approved DOC-0258 at checkpoints 78af9bf, 3ffe8f1, 09af8f2, 569b528.'
    commitLineage = $commitLineage
    sourceSha256 = $sourceSha
    legacySemanticTotal = 37
    legacySemanticTotalStatus = 'SUPERSEDED_CONTRADICTED_FOR_DOC0258_CANONICAL_AUTHORITY'
    artifactHashes = @($artifactHashes)
    summary = $authoritySummary
    invariants = [ordered]@{
        occurrenceRefs = 44
        occurrenceAssignments = 44
        semanticNodes = 44
        parentEdges = 44
        rootChildren = 3
        allNodesReachableFromRoot = $true
        noCycles = $true
        noMultipleParents = $true
        noDanglingRefs = $true
        derivedLevelsReproduceTree = $true
        historicalLevelRead = $false
        historicalParentRead = $false
        providerCalls = 0
        modelCalls = 0
        goldMutationOutsideNewFreeze = $false
        legacySemanticTotalUsedForDecision = $false
    }
}

$freezeValidation = [pscustomobject]@{
    artifactKind = 'A99_USER_REVIEWED_CANONICAL_AUTHORITY_FREEZE_VALIDATION'
    schemaVersion = 'a99-canonical-authority-freeze-v1'
    documentId = $docId
    status = 'USER_REVIEWED_CANONICAL_HIERARCHY_GOLD'
    checks = [ordered]@{
        occurrenceCount = 44
        assignmentCount = 44
        semanticNodeCount = 44
        parentEdgeCount = 44
        primaryCount = 44
        repeatCount = 0
        continuationCount = 0
        rootChildCount = 3
        maxDepth = $authoritySummary.maxDepth
        sourceShaConsistent = $true
        identityMappingUnchanged = $true
        treeReachable = $true
        acyclic = $true
        singleParent = $true
        noDanglingRefs = $true
        derivedLevelsMatchDepth = $true
        historicalLevelRead = $false
        historicalParentRead = $false
        providerCalls = 0
        modelCalls = 0
        goldMutationOutsideNewFreeze = $false
    }
}

Write-JsonFile (Join-Path $outputDir 'authority-freeze-manifest.json') $freezeManifest
Write-JsonFile (Join-Path $outputDir 'frozen-authority-summary.json') $authoritySummary
Write-JsonFile (Join-Path $outputDir 'validation.json') $freezeValidation
$maxDepth = $authoritySummary.maxDepth
Write-TextFile (Join-Path $outputDir 'report.md') @"
# DOC-0258 — user-reviewed canonical authority freeze

Status: **USER_REVIEWED_CANONICAL_HIERARCHY_GOLD**

This freeze promotes the source-backed chain at commits 78af9bf, 3ffe8f1, 09af8f2, and 569b528 after explicit user approval. Existing occurrence, identity, challenge, and hierarchy artifacts are unchanged.

## Frozen authority

- Canonical heading occurrences: 44
- Semantic nodes: 44
- PRIMARY: 44
- REPEAT: 0
- CONTINUATION: 0
- Parent edges: 44
- ROOT children: 3
- Max depth: $maxDepth
- Level: derived exclusively from validated tree depth

## Provenance

- Occurrence review: Codex source-only adjudication
- Identity proposal: Codex source-backed
- Identity challenge: 18/18 plausible candidates corroborated as distinct
- Hierarchy proposal: Codex source-backed
- Final promotion: explicit user approval
- Historical compatibility results: not read or modified
- Legacy semantic total 37: superseded/contradicted for DOC-0258 canonical authority; not used as a target

All artifact SHA256 values and cross-layer invariants are recorded in authority-freeze-manifest.json.
"@

Write-Output ([pscustomobject]@{
    documentId = $docId
    status = 'USER_REVIEWED_CANONICAL_HIERARCHY_GOLD'
    occurrences = 44
    semanticNodes = 44
    parentEdges = 44
    rootChildren = 3
    maxDepth = $maxDepth
    providerCalls = 0
    modelCalls = 0
} | ConvertTo-Json -Depth 10)
