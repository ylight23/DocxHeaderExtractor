[CmdletBinding()]
param(
    [string]$OccurrenceRoot = "artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v1/DOC-0256",
    [string]$IdentityRoot = "artifacts/authority-audit/canonical-semantic-identity-v1/DOC-0256",
    [string]$HierarchyRoot = "artifacts/authority-audit/canonical-hierarchy-v1/DOC-0256",
    [string]$OutputRoot = "artifacts/authority-audit/canonical-authority-freeze-v1/DOC-0256",
    [string]$FreezeCommit = "THIS_FREEZE_COMMIT"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Get-Location).Path
$documentId = "DOC-0256"
$sourcePath = "todo10_8/generated-docx/05_bien_ban_hop/076_ICP_IACG08_Minutes_2023.docx"
$expectedSourceSha256 = "06aec4f8e9847544e61be3a8a44261bd6796ff7af024f482039cdcff662a1a89"
$occRoot = $OccurrenceRoot -replace '/', '\\'
$idRoot = $IdentityRoot -replace '/', '\\'
$hierRoot = $HierarchyRoot -replace '/', '\\'
$outRoot = $OutputRoot -replace '/', '\\'
$outputPath = Join-Path $repoRoot $outRoot

function Read-JsonFile { param([string]$Path) return (Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json) }
function Write-JsonFile {
    param([string]$Path, $Value)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 60) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
function Write-TextFile {
    param([string]$Path, [string]$Text)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [IO.File]::WriteAllText($Path, $Text.TrimStart() + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
function Assert-Equal {
    param($Actual, $Expected, [string]$Name)
    if ($Actual -ne $Expected) { throw "Freeze invariant failed: $Name expected $Expected, found $Actual" }
}
function Get-RelativePath {
    param([string]$Path)
    return [IO.Path]::GetRelativePath($repoRoot, (Resolve-Path -LiteralPath $Path).Path).Replace('\', '/')
}

$paths = [ordered]@{
    sourceDocument = Join-Path $repoRoot ($sourcePath -replace '/', '\\')
    sourceAuthority = Join-Path $repoRoot "$occRoot/source-authority.json"
    occurrenceManifest = Join-Path $repoRoot "$occRoot/manifest.json"
    occurrenceDecisions = Join-Path $repoRoot "$occRoot/occurrence-decisions.json"
    exactBindings = Join-Path $repoRoot "$occRoot/exact-bindings.json"
    occurrenceValidation = Join-Path $repoRoot "$occRoot/validation.json"
    identityManifest = Join-Path $repoRoot "$idRoot/manifest.json"
    identityAssignments = Join-Path $repoRoot "$idRoot/occurrence-assignments.json"
    semanticNodes = Join-Path $repoRoot "$idRoot/semantic-nodes.json"
    identityDecisions = Join-Path $repoRoot "$idRoot/identity-decisions.json"
    identityValidation = Join-Path $repoRoot "$idRoot/validation.json"
    hierarchyManifest = Join-Path $repoRoot "$hierRoot/manifest.json"
    parentDecisions = Join-Path $repoRoot "$hierRoot/parent-decisions.json"
    parentEdges = Join-Path $repoRoot "$hierRoot/parent-edges.json"
    globalConsistencyAudit = Join-Path $repoRoot "$hierRoot/global-consistency-audit.json"
    derivedLevels = Join-Path $repoRoot "$hierRoot/derived-levels.json"
    hierarchyValidation = Join-Path $repoRoot "$hierRoot/validation.json"
}
foreach ($key in $paths.Keys) {
    if (-not (Test-Path -LiteralPath $paths[$key])) { throw "Missing freeze input '$key': $($paths[$key])" }
}

$sourceSha256 = (Get-FileHash -LiteralPath $paths.sourceDocument -Algorithm SHA256).Hash.ToLowerInvariant()
if ($sourceSha256 -ne $expectedSourceSha256) { throw "Source SHA mismatch: $sourceSha256" }
$sourceAuthority = Read-JsonFile $paths.sourceAuthority
$occurrenceManifest = Read-JsonFile $paths.occurrenceManifest
$occurrenceDecisions = Read-JsonFile $paths.occurrenceDecisions
$bindingsArtifact = Read-JsonFile $paths.exactBindings
$occurrenceValidation = Read-JsonFile $paths.occurrenceValidation
$identityManifest = Read-JsonFile $paths.identityManifest
$identityAssignmentsArtifact = Read-JsonFile $paths.identityAssignments
$semanticNodesArtifact = Read-JsonFile $paths.semanticNodes
$identityDecisions = Read-JsonFile $paths.identityDecisions
$identityValidation = Read-JsonFile $paths.identityValidation
$hierarchyManifest = Read-JsonFile $paths.hierarchyManifest
$parentDecisionsArtifact = Read-JsonFile $paths.parentDecisions
$parentEdgesArtifact = Read-JsonFile $paths.parentEdges
$consistencyAudit = Read-JsonFile $paths.globalConsistencyAudit
$derivedLevelsArtifact = Read-JsonFile $paths.derivedLevels
$hierarchyValidation = Read-JsonFile $paths.hierarchyValidation

$bindings = @($bindingsArtifact.acceptedCanonicalBindings)
$assignments = @($identityAssignmentsArtifact.assignments)
$nodes = @($semanticNodesArtifact.semanticNodes)
$identityDecisionRows = @($identityDecisions.decisions)
$parentDecisions = @($parentDecisionsArtifact.decisions)
$edges = @($parentEdgesArtifact.edges)
$levels = @($derivedLevelsArtifact.levels)

Assert-Equal $bindings.Count 24 "canonical occurrences"
Assert-Equal $assignments.Count 24 "occurrence assignments"
Assert-Equal $nodes.Count 24 "semantic nodes"
Assert-Equal $parentDecisions.Count 24 "parent decisions"
Assert-Equal $edges.Count 24 "parent edges"
Assert-Equal $levels.Count 24 "derived levels"
Assert-Equal @($assignments | Where-Object occurrenceRole -ne "PRIMARY").Count 0 "non-primary roles"
Assert-Equal @($nodes | Where-Object { @($_.memberOccurrenceRefs).Count -ne 1 }).Count 0 "multi-occurrence nodes"
Assert-Equal @($edges | Where-Object parentSemanticNodeRef -eq "ROOT").Count 7 "ROOT children"
Assert-Equal ([string]$occurrenceValidation.status) "CANONICAL_EXHAUSTIVE_OCCURRENCE_READY" "occurrence status"
Assert-Equal ([string]$identityManifest.status) "READY_FOR_USER_APPROVAL_CANONICAL_IDENTITY" "identity status"
Assert-Equal ([string]$hierarchyValidation.status) "READY_FOR_USER_APPROVAL_CANONICAL_HIERARCHY" "hierarchy status"
Assert-Equal ([int]$hierarchyValidation.semanticNodeCount) 24 "hierarchy semantic node count"
Assert-Equal ([int]$hierarchyValidation.parentEdgeCount) 24 "hierarchy parent edge count"
Assert-Equal ([int]$hierarchyValidation.rootChildrenCount) 7 "hierarchy ROOT count"
if ([bool]$identityValidation.checks.parentReviewed -or [bool]$identityValidation.checks.levelReviewed) { throw "Identity lane unexpectedly reviewed hierarchy/level" }
if ([bool]$hierarchyValidation.checks.historicalLevelRead -or [bool]$hierarchyValidation.checks.historicalParentRead) { throw "Hierarchy lane read historical authority" }
if ([int]$consistencyAudit.violationCount -ne 0) { throw "Global consistency violations present" }

$nodeRefs = @($nodes | ForEach-Object { [string]$_.semanticNodeRef } | Sort-Object)
$assignmentOccurrenceRefs = @($assignments | ForEach-Object { [string]$_.headingOccurrenceRef } | Sort-Object)
$expectedOccurrenceRefs = @($bindings | ForEach-Object -Begin { $i = 0 } -Process { $i++; "H{0:D3}" -f $i } | Sort-Object)
$edgeChildRefs = @($edges | ForEach-Object { [string]$_.childSemanticNodeRef } | Sort-Object)
if (@(Compare-Object $expectedOccurrenceRefs $assignmentOccurrenceRefs).Count -ne 0) { throw "Unknown/missing occurrence assignment refs" }
if ($nodeRefs.Count -ne 24 -or @($nodeRefs | Select-Object -Unique).Count -ne 24) { throw "Semantic node refs are not unique" }
if (@(Compare-Object $nodeRefs $edgeChildRefs).Count -ne 0) { throw "Parent edge children do not match semantic nodes" }

$edgeByChild = @{}
foreach ($edge in $edges) {
    $child = [string]$edge.childSemanticNodeRef
    $parent = [string]$edge.parentSemanticNodeRef
    if ($edgeByChild.ContainsKey($child)) { throw "Multiple parent: $child" }
    if ($child -eq $parent) { throw "Self-parent: $child" }
    if ($parent -ne "ROOT" -and $nodeRefs -notcontains $parent) { throw "Dangling parent: $child -> $parent" }
    $edgeByChild[$child] = $parent
}

$depthByNode = @{}
foreach ($node in $nodeRefs) {
    $cursor = $node
    $depth = 0
    $seen = [Collections.Generic.HashSet[string]]::new()
    while ($cursor -ne "ROOT") {
        if (-not $seen.Add($cursor)) { throw "Cycle detected from $node" }
        if (-not $edgeByChild.ContainsKey($cursor)) { throw "Unreachable node: $cursor" }
        $cursor = $edgeByChild[$cursor]
        $depth++
        if ($depth -gt 24) { throw "Invalid depth from $node" }
    }
    $depthByNode[$node] = $depth
}
Assert-Equal $depthByNode.Count 24 "reachable semantic nodes"
Assert-Equal (@($depthByNode.Values | Measure-Object -Maximum).Maximum) 2 "max depth"

$frozenDerivedByNode = @{}
foreach ($row in $levels) {
    $ref = [string]$row.semanticNodeRef
    if ($frozenDerivedByNode.ContainsKey($ref)) { throw "Duplicate frozen derived level: $ref" }
    $frozenDerivedByNode[$ref] = [pscustomobject]@{ parent = [string]$row.parentSemanticNodeRef; depth = [int]$row.depth; level = [int]$row.level }
}
if ($frozenDerivedByNode.Count -ne 24) { throw "Incomplete frozen derived levels" }
$derivedLevelsExact = $true
foreach ($node in $nodeRefs) {
    $expected = $frozenDerivedByNode[$node]
    if ($expected.parent -ne $edgeByChild[$node] -or $expected.depth -ne $depthByNode[$node] -or $expected.level -ne $depthByNode[$node]) { $derivedLevelsExact = $false }
}
if (-not $derivedLevelsExact) { throw "Frozen derived levels do not reproduce validated tree" }

$artifactHashes = [Collections.Generic.List[object]]::new()
foreach ($key in $paths.Keys) {
    $artifactHashes.Add([pscustomobject]@{
        artifact = $key
        path = Get-RelativePath $paths[$key]
        sha256 = (Get-FileHash -LiteralPath $paths[$key] -Algorithm SHA256).Hash.ToLowerInvariant()
    })
}

$commitLineage = @(
    [pscustomobject]@{ commit = "54aadec"; role = "canonical occurrence authority" },
    [pscustomobject]@{ commit = "9d6bba7"; role = "canonical identity proposal" },
    [pscustomobject]@{ commit = "81965a3"; role = "canonical hierarchy proposal" },
    [pscustomobject]@{ commit = $FreezeCommit; role = "user-approved authority freeze" }
)
foreach ($commit in @("54aadec", "9d6bba7", "81965a3")) {
    git cat-file -e "$commit^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) { throw "Missing approved commit: $commit" }
}

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
$freezeManifest = [ordered]@{
    artifactKind = "A99_USER_REVIEWED_CANONICAL_AUTHORITY_FREEZE"
    schemaVersion = "a99-canonical-authority-freeze-v1-doc0256"
    documentId = $documentId
    authorityStatus = "USER_REVIEWED_CANONICAL_HIERARCHY_GOLD"
    explicitUserApproval = $true
    approvedCheckpoints = @("54aadec", "9d6bba7", "81965a3")
    commitLineage = $commitLineage
    sourcePath = $sourcePath
    sourceSha256 = $sourceSha256
    occurrenceCount = 24
    semanticNodeCount = 24
    occurrenceRoles = [ordered]@{ PRIMARY = 24; REPEAT = 0; CONTINUATION = 0 }
    parentEdgeCount = 24
    rootChildrenCount = 7
    maxDepth = 2
    levelDerivation = "depth(validated canonical parent tree)"
    provenance = [ordered]@{
        occurrenceAuthority = "SOURCE_ONLY_CANONICAL_EXHAUSTIVE_REVIEW"
        identityAuthorityBeforeApproval = "SOURCE_BACKED_IDENTITY_PROPOSAL"
        hierarchyAuthorityBeforeApproval = "SOURCE_BACKED_HIERARCHY_PROPOSAL"
        finalPromotion = "EXPLICIT_USER_APPROVAL"
        historicalCompatibility = "NOT_OPENED_IN_FREEZE"
    }
    artifactHashes = @($artifactHashes)
    invariants = [ordered]@{
        occurrencesEqualAssignmentsEqualSemanticNodesEqualParentEdges = $true
        allNodesReachableFromRoot = $true
        noCycles = $true
        noMultipleParents = $true
        noDanglingRefs = $true
        identityMappingUnchanged = $true
        occurrenceMappingUnchanged = $true
        treeMappingUnchanged = $true
        regeneratedDerivedLevelsEqualFrozenArtifact = $true
        historicalLevelRead = $false
        historicalParentRead = $false
        oldSemanticTotalUsed = $false
        providerCalls = 0
        modelCalls = 0
        occurrenceMutation = $false
        identityMutation = $false
        parentMutation = $false
        GoldMutationOutsideFreezeLane = $false
    }
}
Write-JsonFile (Join-Path $outputPath "authority-freeze-manifest.json") $freezeManifest

$freezeValidation = [ordered]@{
    artifactKind = "A99_USER_REVIEWED_CANONICAL_AUTHORITY_FREEZE_VALIDATION"
    schemaVersion = "a99-canonical-authority-freeze-v1-doc0256"
    documentId = $documentId
    status = "USER_REVIEWED_CANONICAL_HIERARCHY_GOLD"
    checks = [ordered]@{
        sourceShaMatches = ($sourceSha256 -eq $expectedSourceSha256)
        canonicalOccurrences = 24
        occurrenceAssignments = 24
        semanticNodes = 24
        parentEdges = 24
        primaryCount = 24
        repeatCount = 0
        continuationCount = 0
        rootChildren = 7
        maxDepth = 2
        noCycles = $true
        noMultipleParents = $true
        noDanglingRefs = $true
        noUnreachableNodes = $true
        noUnknownOccurrenceRefs = $true
        treeMutation = $false
        derivedLevelsReproduceExactly = $true
        historicalLevelRead = $false
        historicalParentRead = $false
        oldSemanticTotalUsed = $false
        providerCalls = 0
        modelCalls = 0
        occurrenceMutation = $false
        identityMutation = $false
        parentMutation = $false
        GoldMutationOutsideFreezeLane = $false
    }
    hashCount = $artifactHashes.Count
    commitLineage = $commitLineage
    validator = "PASS"
}
Write-JsonFile (Join-Path $outputPath "validation.json") $freezeValidation

$report = @"
# DOC-0256 — user-approved canonical authority freeze

Status: **USER_REVIEWED_CANONICAL_HIERARCHY_GOLD**

This freeze promotes the already-created source-backed chain after explicit user approval. No occurrence, identity, parent, or level decision was re-adjudicated.

## Frozen authority

- Canonical heading occurrences: **24**
- Semantic nodes: **24**
- `PRIMARY`: **24**
- `REPEAT`: **0**
- `CONTINUATION`: **0**
- Parent edges: **24**
- ROOT children: **7**
- Max depth: **2**
- Level: `depth(validated canonical parent tree)`

## Provenance

- Occurrence authority: source-only canonical exhaustive review
- Identity authority before approval: source-backed identity proposal
- Hierarchy authority before approval: source-backed hierarchy proposal
- Final promotion: explicit user approval
- Approved checkpoints: `54aadec → 9d6bba7 → 81965a3`
- Freeze commit: $FreezeCommit

## Integrity

- Source SHA-256: $sourceSha256
- Hashed input artifacts: $($artifactHashes.Count)
- Cross-layer counts: `24 = 24 = 24 = 24`
- Graph validator: `PASS`
- Derived levels reproduced exactly: true
- Historical compatibility diagnostics: not opened
- Provider/model calls: 0

The historical level must not be used to modify this canonical tree. Any later compatibility comparison is diagnostic only.
"@
Write-TextFile (Join-Path $outputPath "report.md") $report

Write-Output ([ordered]@{
    documentId = $documentId
    status = "USER_REVIEWED_CANONICAL_HIERARCHY_GOLD"
    sourceSha256 = $sourceSha256
    occurrences = 24
    semanticNodes = 24
    parentEdges = 24
    rootChildren = 7
    maxDepth = 2
    hashCount = $artifactHashes.Count
    validator = "PASS"
    commitLineage = $commitLineage
    providerCalls = 0
    modelCalls = 0
} | ConvertTo-Json -Depth 10)
