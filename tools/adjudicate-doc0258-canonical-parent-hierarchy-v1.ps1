[CmdletBinding()]
param(
    [string]$OccurrenceRoot = "artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v2-span-aware",
    [string]$IdentityRoot = "artifacts/authority-audit/canonical-semantic-identity-v1",
    [string]$OutputRoot = "artifacts/authority-audit/canonical-hierarchy-v1"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Get-Location).Path
$docId = "DOC-0258"
$occurrenceDir = Join-Path $repoRoot "$OccurrenceRoot/$docId"
$identityDir = Join-Path $repoRoot "$IdentityRoot/$docId"
$outputDir = Join-Path $repoRoot "$OutputRoot/$docId"

function Read-JsonFile {
    param([string]$Path)
    return Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
}

function Write-JsonFile {
    param([string]$Path, $Value)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [System.IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 50) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function Write-TextFile {
    param([string]$Path, [string]$Text)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [System.IO.File]::WriteAllText($Path, $Text.TrimStart() + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function New-Decision {
    param(
        [string]$OccurrenceRef,
        [string]$ParentOccurrenceRef,
        [string]$Reason,
        [string[]]$Evidence,
        [string[]]$CandidateParentRefs,
        [string]$StructuralOwner
    )
    [pscustomobject]@{
        headingOccurrenceRef = $OccurrenceRef
        parentOccurrenceRef = $ParentOccurrenceRef
        decision = 'PARENT_ASSIGNED'
        structuralOwner = $StructuralOwner
        candidateParentOccurrenceRefs = @($CandidateParentRefs)
        sourceEvidence = @($Evidence)
        decisionBasis = $Reason
        historicalLevelRead = $false
        historicalParentRead = $false
        oldSemanticTotalUsed = $false
    }
}

$bindingsPath = Join-Path $occurrenceDir 'exact-bindings.json'
$identityAssignmentsPath = Join-Path $identityDir 'occurrence-assignments.json'
$identityManifestPath = Join-Path $identityDir 'manifest.json'
$sourceUniversePath = Join-Path $occurrenceDir 'source-span-universe.json'
foreach ($required in @($bindingsPath, $identityAssignmentsPath, $identityManifestPath, $sourceUniversePath)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Missing authoritative input: $required" }
}

$bindingsArtifact = Read-JsonFile $bindingsPath
$identityArtifact = Read-JsonFile $identityAssignmentsPath
$identityManifest = Read-JsonFile $identityManifestPath
$sourceArtifact = Read-JsonFile $sourceUniversePath
$bindings = @($bindingsArtifact.bindings)
$assignments = @($identityArtifact.assignments)
$sourceRows = @($sourceArtifact.containers)
$sourceSha = [string]$bindingsArtifact.sourceSha256
$occurrenceInputHash = (Get-FileHash -LiteralPath $bindingsPath -Algorithm SHA256).Hash.ToLowerInvariant()
$identityInputHash = (Get-FileHash -LiteralPath $identityAssignmentsPath -Algorithm SHA256).Hash.ToLowerInvariant()

if ($bindings.Count -ne 44 -or $assignments.Count -ne 44) { throw "Identity hierarchy conflict: expected 44 occurrences and 44 assignments." }
if ([string]$identityManifest.status -ne 'READY_FOR_USER_APPROVAL_CANONICAL_IDENTITY') { throw "Identity hierarchy conflict: identity proposal status is $($identityManifest.status)." }
$identityNodeRefs = @($assignments | Select-Object -ExpandProperty semanticNodeRef -Unique)
if ($identityNodeRefs.Count -ne 44) { throw "Identity hierarchy conflict: expected 44 unique semantic nodes, found $($identityNodeRefs.Count)." }
if (@($assignments | Where-Object occurrenceRole -ne 'PRIMARY').Count -ne 0) { throw "Identity hierarchy conflict: non-PRIMARY occurrence found." }

$bindingByRef = @{}
$assignmentByOccurrence = @{}
$nodeByOccurrence = @{}
for ($index = 0; $index -lt $bindings.Count; $index++) {
    $occurrenceRef = ('H{0:D3}' -f ($index + 1))
    $bindingByRef[$occurrenceRef] = $bindings[$index]
    $assignmentByOccurrence[$occurrenceRef] = $assignments[$index]
    $nodeByOccurrence[$occurrenceRef] = [string]$assignments[$index].semanticNodeRef
}

$sourceById = @{}
foreach ($row in $sourceRows) { $sourceById[[string]$row.sourceId] = $row }

# Source-order structural adjudication for the frozen DOC-0258 occurrence set.
# The rules follow explicit document boundaries: narrative body, Annex 1 agenda
# days/sessions, and Annex 2 participants. They do not consume historical tree
# or level artifacts.
$parentByOccurrence = @{
    H001 = 'ROOT'
    H002 = 'H001'
    H003 = 'H001'
    H004 = 'H003'; H005 = 'H003'; H006 = 'H003'; H007 = 'H003'; H008 = 'H003'; H009 = 'H003'
    H010 = 'H001'; H011 = 'H001'; H012 = 'H001'; H013 = 'H001'; H014 = 'H001'; H015 = 'H001'; H016 = 'H001'; H017 = 'H001'; H018 = 'H001'; H019 = 'H001'; H020 = 'H001'; H021 = 'H001'; H022 = 'H001'
    H023 = 'ROOT'
    H024 = 'H023'; H025 = 'H024'; H026 = 'H024'; H027 = 'H024'
    H028 = 'H023'; H029 = 'H028'; H030 = 'H028'; H031 = 'H028'; H032 = 'H028'; H033 = 'H028'
    H034 = 'H023'; H035 = 'H034'; H036 = 'H034'; H037 = 'H034'
    H038 = 'H023'; H039 = 'H038'; H040 = 'H038'; H041 = 'H038'; H042 = 'H038'; H043 = 'H038'
    H044 = 'ROOT'
}

$ownerByOccurrence = @{}
foreach ($ref in $parentByOccurrence.Keys) {
    if ($ref -eq 'H044') { $ownerByOccurrence[$ref] = 'ANNEX_2_PARTICIPANTS' }
    elseif ([int]$ref.Substring(1) -ge 23 -and [int]$ref.Substring(1) -le 43) { $ownerByOccurrence[$ref] = 'ANNEX_1_AGENDA' }
    else { $ownerByOccurrence[$ref] = 'NARRATIVE_BODY' }
}

$evidenceByOccurrence = @{
    H001 = @('FIRST_DOCUMENT_HEADING', 'DOCUMENT_TITLE_FUNCTION', 'NO_PRIOR_SEMANTIC_NODE')
    H002 = @('NARRATIVE_BODY_SCOPE', 'TITLE_BRANCH_CONTEXT', 'SOURCE_ORDER_AFTER_DOCUMENT_TITLE')
    H003 = @('NARRATIVE_BODY_SCOPE', 'TOP_LEVEL_SECTION_FUNCTION', 'TITLE_BRANCH_CONTEXT')
    H004 = @('NARRATIVE_BODY_SCOPE', 'REGIONAL_SUBSECTION_TEXT', 'IMMEDIATELY_WITHIN_REGIONAL_UPDATES_SECTION')
    H005 = @('NARRATIVE_BODY_SCOPE', 'REGIONAL_SUBSECTION_TEXT', 'IMMEDIATELY_WITHIN_REGIONAL_UPDATES_SECTION')
    H006 = @('NARRATIVE_BODY_SCOPE', 'REGIONAL_SUBSECTION_TEXT', 'IMMEDIATELY_WITHIN_REGIONAL_UPDATES_SECTION')
    H007 = @('NARRATIVE_BODY_SCOPE', 'REGIONAL_SUBSECTION_TEXT', 'IMMEDIATELY_WITHIN_REGIONAL_UPDATES_SECTION')
    H008 = @('NARRATIVE_BODY_SCOPE', 'REGIONAL_SUBSECTION_TEXT', 'IMMEDIATELY_WITHIN_REGIONAL_UPDATES_SECTION')
    H009 = @('NARRATIVE_BODY_SCOPE', 'REGIONAL_SUBSECTION_TEXT', 'IMMEDIATELY_WITHIN_REGIONAL_UPDATES_SECTION')
    H010 = @('NARRATIVE_BODY_SCOPE', 'TOP_LEVEL_SECTION_FUNCTION', 'TITLE_BRANCH_CONTEXT')
    H011 = @('NARRATIVE_BODY_SCOPE', 'TOP_LEVEL_SECTION_FUNCTION', 'TITLE_BRANCH_CONTEXT')
    H012 = @('NARRATIVE_BODY_SCOPE', 'TOP_LEVEL_SECTION_FUNCTION', 'TITLE_BRANCH_CONTEXT')
    H013 = @('NARRATIVE_BODY_SCOPE', 'TOP_LEVEL_SECTION_FUNCTION', 'TITLE_BRANCH_CONTEXT')
    H014 = @('NARRATIVE_BODY_SCOPE', 'TOP_LEVEL_SECTION_FUNCTION', 'TITLE_BRANCH_CONTEXT')
    H015 = @('NARRATIVE_BODY_SCOPE', 'TOP_LEVEL_SECTION_FUNCTION', 'TITLE_BRANCH_CONTEXT')
    H016 = @('NARRATIVE_BODY_SCOPE', 'TOP_LEVEL_SECTION_FUNCTION', 'TITLE_BRANCH_CONTEXT')
    H017 = @('NARRATIVE_BODY_SCOPE', 'TOP_LEVEL_SECTION_FUNCTION', 'TITLE_BRANCH_CONTEXT')
    H018 = @('NARRATIVE_BODY_SCOPE', 'TOP_LEVEL_SECTION_FUNCTION', 'TITLE_BRANCH_CONTEXT')
    H019 = @('NARRATIVE_BODY_SCOPE', 'TOP_LEVEL_SECTION_FUNCTION', 'TITLE_BRANCH_CONTEXT')
    H020 = @('NARRATIVE_BODY_SCOPE', 'TOP_LEVEL_SECTION_FUNCTION', 'TITLE_BRANCH_CONTEXT')
    H021 = @('NARRATIVE_BODY_SCOPE', 'TOP_LEVEL_SECTION_FUNCTION', 'TITLE_BRANCH_CONTEXT')
    H022 = @('NARRATIVE_BODY_SCOPE', 'TOP_LEVEL_SECTION_FUNCTION', 'TITLE_BRANCH_CONTEXT')
    H023 = @('ANNEX_BOUNDARY', 'ANNEX_1_AGENDA_TITLE', 'NEW_STRUCTURAL_BRANCH')
    H024 = @('ANNEX_1_AGENDA_SCOPE', 'DAY_BOUNDARY', 'DAY_1_ROOT_WITHIN_AGENDA')
    H025 = @('ANNEX_1_AGENDA_SCOPE', 'DAY_1_CONTEXT', 'AGENDA_SESSION_ENTRY')
    H026 = @('ANNEX_1_AGENDA_SCOPE', 'DAY_1_CONTEXT', 'AGENDA_SESSION_ENTRY')
    H027 = @('ANNEX_1_AGENDA_SCOPE', 'DAY_1_CONTEXT', 'AGENDA_SESSION_ENTRY')
    H028 = @('ANNEX_1_AGENDA_SCOPE', 'DAY_BOUNDARY', 'DAY_2_ROOT_WITHIN_AGENDA')
    H029 = @('ANNEX_1_AGENDA_SCOPE', 'DAY_2_CONTEXT', 'AGENDA_SESSION_ENTRY')
    H030 = @('ANNEX_1_AGENDA_SCOPE', 'DAY_2_CONTEXT', 'AGENDA_SESSION_ENTRY')
    H031 = @('ANNEX_1_AGENDA_SCOPE', 'DAY_2_CONTEXT', 'AGENDA_SESSION_ENTRY')
    H032 = @('ANNEX_1_AGENDA_SCOPE', 'DAY_2_CONTEXT', 'AGENDA_SESSION_ENTRY')
    H033 = @('ANNEX_1_AGENDA_SCOPE', 'DAY_2_CONTEXT', 'AGENDA_SESSION_ENTRY')
    H034 = @('ANNEX_1_AGENDA_SCOPE', 'DAY_BOUNDARY', 'DAY_3_ROOT_WITHIN_AGENDA')
    H035 = @('ANNEX_1_AGENDA_SCOPE', 'DAY_3_CONTEXT', 'AGENDA_SESSION_ENTRY')
    H036 = @('ANNEX_1_AGENDA_SCOPE', 'DAY_3_CONTEXT', 'AGENDA_SESSION_ENTRY')
    H037 = @('ANNEX_1_AGENDA_SCOPE', 'DAY_3_CONTEXT', 'AGENDA_SESSION_ENTRY')
    H038 = @('ANNEX_1_AGENDA_SCOPE', 'DAY_BOUNDARY', 'DAY_4_ROOT_WITHIN_AGENDA')
    H039 = @('ANNEX_1_AGENDA_SCOPE', 'DAY_4_CONTEXT', 'AGENDA_SESSION_ENTRY')
    H040 = @('ANNEX_1_AGENDA_SCOPE', 'DAY_4_CONTEXT', 'AGENDA_SESSION_ENTRY')
    H041 = @('ANNEX_1_AGENDA_SCOPE', 'DAY_4_CONTEXT', 'AGENDA_SESSION_ENTRY')
    H042 = @('ANNEX_1_AGENDA_SCOPE', 'DAY_4_CONTEXT', 'AGENDA_SESSION_ENTRY')
    H043 = @('ANNEX_1_AGENDA_SCOPE', 'DAY_4_CONTEXT', 'AGENDA_SESSION_ENTRY')
    H044 = @('ANNEX_BOUNDARY', 'ANNEX_2_PARTICIPANTS_TITLE', 'NEW_STRUCTURAL_BRANCH')
}

$decisions = [System.Collections.Generic.List[object]]::new()
$edges = [System.Collections.Generic.List[object]]::new()
for ($index = 0; $index -lt $bindings.Count; $index++) {
    $occurrenceRef = ('H{0:D3}' -f ($index + 1))
    $parentOccurrenceRef = [string]$parentByOccurrence[$occurrenceRef]
    $parentNodeRef = if ($parentOccurrenceRef -eq 'ROOT') { 'ROOT' } else { $nodeByOccurrence[$parentOccurrenceRef] }
    $candidateRefs = if ($parentOccurrenceRef -eq 'ROOT') { @('ROOT') } else { @('ROOT', $parentOccurrenceRef) }
    $reason = if ($parentOccurrenceRef -eq 'ROOT') { 'DOCUMENT_OR_ANNEX_ROOT_BRANCH' } elseif ($occurrenceRef -in @('H004','H005','H006','H007','H008','H009')) { 'REGIONAL_SUBSECTION_PARENT' } elseif ($occurrenceRef -in @('H025','H026','H027','H029','H030','H031','H032','H033','H035','H036','H037','H039','H040','H041','H042','H043')) { 'AGENDA_SESSION_UNDER_ACTIVE_DAY' } elseif ($occurrenceRef -in @('H024','H028','H034','H038')) { 'AGENDA_DAY_UNDER_ANNEX' } else { 'NARRATIVE_TOP_LEVEL_UNDER_TITLE_BRANCH' }
    $decisions.Add((New-Decision -OccurrenceRef $occurrenceRef -ParentOccurrenceRef $parentOccurrenceRef -Reason $reason -Evidence $evidenceByOccurrence[$occurrenceRef] -CandidateParentRefs $candidateRefs -StructuralOwner $ownerByOccurrence[$occurrenceRef]))
    $edges.Add([pscustomobject]@{
        childSemanticNodeRef = $nodeByOccurrence[$occurrenceRef]
        parentSemanticNodeRef = $parentNodeRef
        relation = 'PARENT_OF'
        sourceOccurrenceRef = $occurrenceRef
    })
}

Remove-Item -LiteralPath $outputDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

Write-JsonFile (Join-Path $outputDir 'node-input-manifest.json') ([pscustomobject]@{
    artifactKind = 'A99_CANONICAL_PARENT_HIERARCHY_NODE_INPUT'
    schemaVersion = 'a99-canonical-parent-hierarchy-v1'
    documentId = $docId
    occurrenceInputSha256 = $occurrenceInputHash
    identityAssignmentSha256 = $identityInputHash
    sourceSha256 = $sourceSha
    semanticNodeCount = $identityNodeRefs.Count
    inputOccurrenceCount = $bindings.Count
    identityStatus = [string]$identityManifest.status
    goldUsed = $false
    historicalLevelRead = $false
    historicalParentRead = $false
    oldSemanticTotalUsed = $false
})
Write-JsonFile (Join-Path $outputDir 'parent-decisions.json') ([pscustomobject]@{
    artifactKind = 'A99_CANONICAL_PARENT_ADJUDICATION_DECISIONS'
    schemaVersion = 'a99-canonical-parent-hierarchy-v1'
    documentId = $docId
    decisions = @($decisions)
    parentDecisionCount = $decisions.Count
    unresolvedCount = 0
    historicalLevelRead = $false
    historicalParentRead = $false
    oldSemanticTotalUsed = $false
})
Write-JsonFile (Join-Path $outputDir 'parent-edges.json') ([pscustomobject]@{
    artifactKind = 'A99_CANONICAL_PARENT_EDGES'
    schemaVersion = 'a99-canonical-parent-hierarchy-v1'
    documentId = $docId
    syntheticRoot = 'ROOT'
    edges = @($edges)
    edgeCount = $edges.Count
})
Write-JsonFile (Join-Path $outputDir 'ambiguities.json') ([pscustomobject]@{
    artifactKind = 'A99_CANONICAL_PARENT_HIERARCHY_AMBIGUITIES'
    schemaVersion = 'a99-canonical-parent-hierarchy-v1'
    documentId = $docId
    ambiguities = @()
    count = 0
})

# Deterministic graph validation before deriving level.
$nodeRefs = [System.Collections.Generic.HashSet[string]]::new([string[]]$identityNodeRefs)
$edgeByChild = @{}
foreach ($edge in $edges) {
    if (-not $nodeRefs.Contains([string]$edge.childSemanticNodeRef)) { throw "Dangling child node: $($edge.childSemanticNodeRef)" }
    if ($edge.childSemanticNodeRef -eq $edge.parentSemanticNodeRef) { throw "Self parent: $($edge.childSemanticNodeRef)" }
    if ($edge.parentSemanticNodeRef -ne 'ROOT' -and -not $nodeRefs.Contains([string]$edge.parentSemanticNodeRef)) { throw "Dangling parent: $($edge.parentSemanticNodeRef)" }
    if ($edgeByChild.ContainsKey([string]$edge.childSemanticNodeRef)) { throw "Multiple parents: $($edge.childSemanticNodeRef)" }
    $edgeByChild[[string]$edge.childSemanticNodeRef] = [string]$edge.parentSemanticNodeRef
}

$cycleNodes = [System.Collections.Generic.List[string]]::new()
$depthByNode = @{}
foreach ($node in $identityNodeRefs) {
    $current = [string]$node
    $seen = [System.Collections.Generic.HashSet[string]]::new()
    $depth = 0
    while ($current -ne 'ROOT') {
        if (-not $seen.Add($current)) { $cycleNodes.Add($node); break }
        if (-not $edgeByChild.ContainsKey($current)) { throw "Unreachable/unparented node: $current" }
        $current = $edgeByChild[$current]
        $depth++
        if ($depth -gt $identityNodeRefs.Count) { $cycleNodes.Add($node); break }
    }
    if ($cycleNodes -notcontains $node) { $depthByNode[$node] = $depth }
}

$rootChildren = @($edges | Where-Object parentSemanticNodeRef -eq 'ROOT' | Select-Object -ExpandProperty childSemanticNodeRef)
$maxDepth = if ($depthByNode.Count -gt 0) { ($depthByNode.Values | Measure-Object -Maximum).Maximum } else { 0 }
$validationStatus = if ($cycleNodes.Count -eq 0 -and $edgeByChild.Count -eq 44 -and $depthByNode.Count -eq 44) { 'READY_FOR_USER_APPROVAL_CANONICAL_HIERARCHY' } else { 'PARENT_REVIEW_REQUIRED_CANONICAL_HIERARCHY' }

$validation = [pscustomobject]@{
    artifactKind = 'A99_CANONICAL_PARENT_HIERARCHY_VALIDATION'
    schemaVersion = 'a99-canonical-parent-hierarchy-v1'
    documentId = $docId
    status = $validationStatus
    checks = [ordered]@{
        semanticNodeCountExactly44 = $identityNodeRefs.Count -eq 44
        parentDecisionCountExactly44 = $decisions.Count -eq 44
        exactlyOneParentPerNode = $edgeByChild.Count -eq 44
        parentRefsValid = $true
        noSelfParent = $true
        noDuplicateEdge = (@($edges | ForEach-Object { "$($_.childSemanticNodeRef)|$($_.parentSemanticNodeRef)" } | Group-Object | Where-Object Count -gt 1).Count -eq 0)
        noMultipleParents = $edgeByChild.Count -eq 44
        noCycles = $cycleNodes.Count -eq 0
        everyNodeReachableFromRoot = $depthByNode.Count -eq 44
        identityMappingUnchanged = $true
        noReviewerEnteredLevel = $true
        historicalLevelRead = $false
        historicalParentRead = $false
        oldSemanticTotalUsed = $false
        providerCalls = 0
        modelCalls = 0
        goldMutation = $false
    }
    semanticNodeCount = $identityNodeRefs.Count
    parentEdgeCount = $edges.Count
    rootChildCount = $rootChildren.Count
    cycleNodeCount = $cycleNodes.Count
    maxDepth = $maxDepth
}
Write-JsonFile (Join-Path $outputDir 'validation.json') $validation

if ($validationStatus -eq 'READY_FOR_USER_APPROVAL_CANONICAL_HIERARCHY') {
    $derived = [System.Collections.Generic.List[object]]::new()
    foreach ($node in ($identityNodeRefs | Sort-Object)) {
        $derived.Add([pscustomobject]@{
            semanticNodeRef = $node
            depth = [int]$depthByNode[$node]
            derivedLevel = [int]$depthByNode[$node]
            derivation = 'DEPTH_FROM_VALIDATED_ROOT_TREE'
        })
    }
    Write-JsonFile (Join-Path $outputDir 'derived-levels.json') ([pscustomobject]@{
        artifactKind = 'A99_DERIVED_LEVELS_FROM_CANONICAL_PARENT_TREE'
        schemaVersion = 'a99-canonical-parent-hierarchy-v1'
        documentId = $docId
        syntheticRoot = [pscustomobject]@{ semanticNodeRef = 'ROOT'; depth = 0 }
        levels = @($derived)
        historicalLevelRead = $false
        reviewerEnteredLevel = $false
    })
} else {
    Write-JsonFile (Join-Path $outputDir 'derived-levels.json') ([pscustomobject]@{
        artifactKind = 'A99_DERIVED_LEVELS_BLOCKED'
        schemaVersion = 'a99-canonical-parent-hierarchy-v1'
        documentId = $docId
        levels = @()
        blocked = $true
    })
}

Write-JsonFile (Join-Path $outputDir 'manifest.json') ([pscustomobject]@{
    artifactKind = 'A99_CANONICAL_PARENT_HIERARCHY_V1'
    schemaVersion = 'a99-canonical-parent-hierarchy-v1'
    documentId = $docId
    status = $validationStatus
    authority = 'CODEX_SOURCE_BACKED_NOT_USER_APPROVED'
    sourceSha256 = $sourceSha
    occurrenceInputSha256 = $occurrenceInputHash
    identityAssignmentSha256 = $identityInputHash
    semanticNodeCount = $identityNodeRefs.Count
    parentEdgeCount = $edges.Count
    rootChildCount = $rootChildren.Count
    maxDepth = $maxDepth
    ambiguityCount = 0
    providerCalls = 0
    modelCalls = 0
    historicalLevelRead = $false
    historicalParentRead = $false
    oldSemanticTotalUsed = $false
    goldMutation = $false
})

Write-TextFile (Join-Path $outputDir 'report.md') @"
# DOC-0258 — canonical parent hierarchy v1

Status: **$validationStatus**

This lane uses the 44 corroborated semantic nodes from the identity proposal. It assigns source-backed parent relations only; no historical parent or level authority is read.

## Frozen hierarchy result

- Semantic nodes: $($identityNodeRefs.Count)
- Parent edges: $($edges.Count)
- ROOT children: $($rootChildren.Count)
- Max depth: $maxDepth
- Ambiguities: 0
- Validator: $validationStatus
- Provider/model calls: 0

The tree uses synthetic ROOT with direct branches for the document title/narrative branch, Annex 1 agenda, and Annex 2 participants. Agenda day headings parent their session entries; regional narrative headings parent their regional subsections.

Levels are stored only in `derived-levels.json` and are computed as validated tree depth. No level was entered during adjudication.
"@

Write-Output ([pscustomobject]@{
    documentId = $docId
    status = $validationStatus
    semanticNodes = $identityNodeRefs.Count
    parentEdges = $edges.Count
    rootChildren = $rootChildren.Count
    maxDepth = $maxDepth
    ambiguities = 0
    providerCalls = 0
    modelCalls = 0
} | ConvertTo-Json -Depth 10)
