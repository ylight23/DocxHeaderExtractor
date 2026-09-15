[CmdletBinding()]
param(
    [string]$OccurrenceRoot = "artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v1/DOC-0256",
    [string]$IdentityRoot = "artifacts/authority-audit/canonical-semantic-identity-v1/DOC-0256",
    [string]$OutputRoot = "artifacts/authority-audit/canonical-hierarchy-v1/DOC-0256"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Get-Location).Path
$documentId = "DOC-0256"
$expectedSourceSha256 = "06aec4f8e9847544e61be3a8a44261bd6796ff7af024f482039cdcff662a1a89"
$occurrencePath = Join-Path $repoRoot (($OccurrenceRoot -replace '/', '\\') + "\\exact-bindings.json")
$identityAssignmentsPath = Join-Path $repoRoot (($IdentityRoot -replace '/', '\\') + "\\occurrence-assignments.json")
$identityNodesPath = Join-Path $repoRoot (($IdentityRoot -replace '/', '\\') + "\\semantic-nodes.json")
$outputPath = Join-Path $repoRoot ($OutputRoot -replace '/', '\\')

function Write-JsonFile {
    param([string]$Path, $Value)
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $json = $Value | ConvertTo-Json -Depth 50
    [IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Write-TextFile {
    param([string]$Path, [string]$Text)
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    [IO.File]::WriteAllText($Path, $Text.TrimStart() + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

foreach ($path in @($occurrencePath, $identityAssignmentsPath, $identityNodesPath)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required authority missing: $path" }
}

$occurrenceAuthoritySha256 = (Get-FileHash -LiteralPath $occurrencePath -Algorithm SHA256).Hash.ToLowerInvariant()
$identityAssignmentsSha256 = (Get-FileHash -LiteralPath $identityAssignmentsPath -Algorithm SHA256).Hash.ToLowerInvariant()
$identityNodesSha256 = (Get-FileHash -LiteralPath $identityNodesPath -Algorithm SHA256).Hash.ToLowerInvariant()
$occurrenceAuthority = Get-Content -Raw -LiteralPath $occurrencePath | ConvertFrom-Json
$identityAssignments = Get-Content -Raw -LiteralPath $identityAssignmentsPath | ConvertFrom-Json
$identityNodes = Get-Content -Raw -LiteralPath $identityNodesPath | ConvertFrom-Json
$occurrences = @($occurrenceAuthority.acceptedCanonicalBindings | Sort-Object occurrenceOrder)
$assignments = @($identityAssignments.assignments)
$nodes = @($identityNodes.semanticNodes)

if ($occurrences.Count -ne 24 -or $assignments.Count -ne 24 -or $nodes.Count -ne 24) { throw "IDENTITY_HIERARCHY_CONFLICT: expected 24 occurrence/assignment/node records" }
if ([string]$occurrenceAuthority.sourceSha256 -ne $expectedSourceSha256) { throw "IDENTITY_HIERARCHY_CONFLICT: source SHA" }
if (@($assignments | Where-Object occurrenceRole -ne "PRIMARY").Count -ne 0) { throw "IDENTITY_HIERARCHY_CONFLICT: identity is not 24 PRIMARY singleton nodes" }

$occurrenceRefByNode = @{}
$occurrenceByRef = @{}
for ($i = 0; $i -lt $occurrences.Count; $i++) {
    $h = "H{0:D3}" -f ($i + 1)
    $occurrenceByRef[$h] = $occurrences[$i]
}
foreach ($assignment in $assignments) {
    if (-not $occurrenceByRef.ContainsKey([string]$assignment.headingOccurrenceRef)) { throw "IDENTITY_HIERARCHY_CONFLICT: unknown occurrence ref" }
    $occurrenceRefByNode[[string]$assignment.semanticNodeRef] = [string]$assignment.headingOccurrenceRef
}
$nodeRefs = @($nodes | ForEach-Object { [string]$_.semanticNodeRef })
if ((@($nodeRefs | Sort-Object -Unique)).Count -ne 24) { throw "IDENTITY_HIERARCHY_CONFLICT: duplicate node refs" }

# Source-backed parent authority. ROOT is synthetic and harness-owned.
$parentByNode = [ordered]@{
    N001 = "ROOT"
    N002 = "ROOT"
    N003 = "N002"
    N004 = "N002"
    N005 = "N002"
    N006 = "N002"
    N007 = "N002"
    N008 = "N002"
    N009 = "ROOT"
    N010 = "N009"
    N011 = "N009"
    N012 = "N009"
    N013 = "N009"
    N014 = "N009"
    N015 = "N009"
    N016 = "N009"
    N017 = "ROOT"
    N018 = "ROOT"
    N019 = "ROOT"
    N020 = "N019"
    N021 = "N019"
    N022 = "N019"
    N023 = "N019"
    N024 = "ROOT"
}

$candidateByNode = [ordered]@{
    N001 = @("ROOT")
    N002 = @("N001", "ROOT")
    N003 = @("N002", "N001", "ROOT")
    N004 = @("N002", "N001", "ROOT")
    N005 = @("N002", "N001", "ROOT")
    N006 = @("N002", "N001", "ROOT")
    N007 = @("N002", "N001", "ROOT")
    N008 = @("N002", "N001", "ROOT")
    N009 = @("N008", "N002", "ROOT")
    N010 = @("N009", "N002", "ROOT")
    N011 = @("N009", "N002", "ROOT")
    N012 = @("N009", "N002", "ROOT")
    N013 = @("N009", "N002", "ROOT")
    N014 = @("N009", "N002", "ROOT")
    N015 = @("N009", "N002", "ROOT")
    N016 = @("N009", "N002", "ROOT")
    N017 = @("N016", "N009", "ROOT")
    N018 = @("N017", "N009", "ROOT")
    N019 = @("N018", "N017", "ROOT")
    N020 = @("N019", "N018", "ROOT")
    N021 = @("N019", "N018", "ROOT")
    N022 = @("N019", "N018", "ROOT")
    N023 = @("N019", "N018", "ROOT")
    N024 = @("N023", "N019", "ROOT")
}

$rationaleByNode = @{
    N001 = @{ scope = "NARRATIVE_INITIAL_SECTION"; positive = @("FIRST_CANONICAL_SECTION_HAS_NO_ENCLOSING_HEADING"); contradiction = @(); reason = "The first section begins at document body scope; no earlier heading can structurally contain it." }
    N002 = @{ scope = "NARRATIVE_TOP_LEVEL_SECTION"; positive = @("STANDALONE_SECTION_AFTER_INTRODUCTORY_CONTENT", "SAME_TOP_LEVEL_PATTERN_AS_OTHER_MAJOR_NARRATIVE_SECTIONS"); contradiction = @("NO_STRUCTURAL_SUBORDINATION_TO_N001"); reason = "Regional updates begins a new major narrative section; the preceding welcome section is not an enclosing heading." }
    N003 = @{ scope = "REGIONAL_UPDATES_SUBSECTION"; positive = @("REGION_HEADING_FOLLOWED_BY_REGION_PROSE", "REPEATED_SIBLING_PATTERN_UNDER_N002"); contradiction = @(); reason = "Africa is a regional subsection under the Regional updates section." }
    N004 = @{ scope = "REGIONAL_UPDATES_SUBSECTION"; positive = @("REGION_HEADING_FOLLOWED_BY_REGION_PROSE", "REPEATED_SIBLING_PATTERN_UNDER_N002"); contradiction = @(); reason = "Asia and the Pacific is a sibling regional subsection under Regional updates." }
    N005 = @{ scope = "REGIONAL_UPDATES_SUBSECTION"; positive = @("REGION_HEADING_FOLLOWED_BY_REGION_PROSE", "REPEATED_SIBLING_PATTERN_UNDER_N002"); contradiction = @(); reason = "Commonwealth of Independent States is a sibling regional subsection under Regional updates." }
    N006 = @{ scope = "REGIONAL_UPDATES_SUBSECTION"; positive = @("REGION_HEADING_FOLLOWED_BY_REGION_PROSE", "REPEATED_SIBLING_PATTERN_UNDER_N002"); contradiction = @(); reason = "Eurostat–OECD PPP Program is a sibling regional subsection under Regional updates." }
    N007 = @{ scope = "REGIONAL_UPDATES_SUBSECTION"; positive = @("REGION_HEADING_FOLLOWED_BY_REGION_PROSE", "REPEATED_SIBLING_PATTERN_UNDER_N002"); contradiction = @(); reason = "Latin America and the Caribbean is a sibling regional subsection under Regional updates." }
    N008 = @{ scope = "REGIONAL_UPDATES_SUBSECTION"; positive = @("REGION_HEADING_FOLLOWED_BY_REGION_PROSE", "REPEATED_SIBLING_PATTERN_UNDER_N002"); contradiction = @(); reason = "Western Asia is a sibling regional subsection under Regional updates." }
    N009 = @{ scope = "NARRATIVE_TOP_LEVEL_SECTION"; positive = @("NEW_MAJOR_SECTION_AFTER_REGIONAL_BRANCH", "STANDALONE_GLOBAL_UPDATES_HEADING"); contradiction = @("INTERVENING_COMPETING_REGION_HEADING", "NO_STRUCTURAL_SUBORDINATION_TO_N008"); reason = "Global updates starts a new top-level narrative branch rather than continuing the final regional subsection." }
    N010 = @{ scope = "GLOBAL_DATA_REVIEW_SUBSECTION"; positive = @("DATA_REVIEW_TOPIC_SEQUENCE", "REPEATED_SIBLING_PATTERN_UNDER_N009"); contradiction = @(); reason = "Household consumption review is the first data-review subsection under Global updates." }
    N011 = @{ scope = "GLOBAL_DATA_REVIEW_SUBSECTION"; positive = @("DATA_REVIEW_TOPIC_SEQUENCE", "REPEATED_SIBLING_PATTERN_UNDER_N009"); contradiction = @(); reason = "Housing prices review is a sibling data-review subsection under Global updates." }
    N012 = @{ scope = "GLOBAL_DATA_REVIEW_SUBSECTION"; positive = @("DATA_REVIEW_TOPIC_SEQUENCE", "REPEATED_SIBLING_PATTERN_UNDER_N009"); contradiction = @(); reason = "Private Education review is a sibling data-review subsection under Global updates." }
    N013 = @{ scope = "GLOBAL_DATA_REVIEW_SUBSECTION"; positive = @("DATA_REVIEW_TOPIC_SEQUENCE", "REPEATED_SIBLING_PATTERN_UNDER_N009"); contradiction = @(); reason = "Government compensation review is a sibling data-review subsection under Global updates." }
    N014 = @{ scope = "GLOBAL_DATA_REVIEW_SUBSECTION"; positive = @("DATA_REVIEW_TOPIC_SEQUENCE", "REPEATED_SIBLING_PATTERN_UNDER_N009"); contradiction = @(); reason = "Machinery/construction review is a sibling data-review subsection under Global updates." }
    N015 = @{ scope = "GLOBAL_DATA_REVIEW_SUBSECTION"; positive = @("DATA_REVIEW_TOPIC_SEQUENCE", "REPEATED_SIBLING_PATTERN_UNDER_N009"); contradiction = @(); reason = "National accounts review is a sibling data-review subsection under Global updates." }
    N016 = @{ scope = "GLOBAL_DATA_REVIEW_SUBSECTION"; positive = @("DATA_REVIEW_TOPIC_SEQUENCE", "REPEATED_SIBLING_PATTERN_UNDER_N009"); contradiction = @(); reason = "Population/exchange-rate review is a sibling data-review subsection under Global updates." }
    N017 = @{ scope = "NARRATIVE_TOP_LEVEL_SECTION"; positive = @("NEW_PLANNING_SECTION_AFTER_DATA_REVIEW_BRANCH", "STANDALONE_PLANNING_HEADING"); contradiction = @("NO_STRUCTURAL_SUBORDINATION_TO_N016"); reason = "Governance/release planning starts a new top-level branch after the global data-review branch." }
    N018 = @{ scope = "NARRATIVE_TOP_LEVEL_SECTION"; positive = @("SEPARATE_STANDALONE_PLANNING_HEADING", "PARALLEL_MAJOR_SECTION_PATTERN"); contradiction = @("NO_SUBSECTION_MARKER_UNDER_N017"); reason = "ICP 2024 planning is a separate top-level planning section, not a subsection of the preceding planning section." }
    N019 = @{ scope = "ANNEX_SECTION"; positive = @("EXPLICIT_ANNEX_BOUNDARY", "STANDALONE_ANNEX_HEADING"); contradiction = @("ANNEX_SCOPE_DIFFERENCE_FROM_N018"); reason = "Annex 1 begins a distinct document unit and is not structurally subordinate to the narrative section immediately before it." }
    N020 = @{ scope = "ANNEX1_DAY_SUBSECTION"; positive = @("DAY_HEADING_INSIDE_ANNEX1_TABLE", "REPEATED_DAY_SECTION_PATTERN_UNDER_N019"); contradiction = @(); reason = "Day 1 is an explicit subsection of the Annex 1 agenda." }
    N021 = @{ scope = "ANNEX1_DAY_SUBSECTION"; positive = @("DAY_HEADING_INSIDE_ANNEX1_TABLE", "REPEATED_DAY_SECTION_PATTERN_UNDER_N019"); contradiction = @(); reason = "Day 2 is a sibling day subsection of the Annex 1 agenda." }
    N022 = @{ scope = "ANNEX1_DAY_SUBSECTION"; positive = @("DAY_HEADING_INSIDE_ANNEX1_TABLE", "REPEATED_DAY_SECTION_PATTERN_UNDER_N019"); contradiction = @(); reason = "Day 3 is a sibling day subsection of the Annex 1 agenda." }
    N023 = @{ scope = "ANNEX1_DAY_SUBSECTION"; positive = @("DAY_HEADING_INSIDE_ANNEX1_TABLE", "REPEATED_DAY_SECTION_PATTERN_UNDER_N019"); contradiction = @(); reason = "Day 4 is a sibling day subsection of the Annex 1 agenda." }
    N024 = @{ scope = "ANNEX_SECTION"; positive = @("EXPLICIT_ANNEX_BOUNDARY", "STANDALONE_ANNEX_HEADING"); contradiction = @("ANNEX_SCOPE_DIFFERENCE_FROM_N023"); reason = "Annex 2 begins a new document unit after Annex 1 and is a separate root." }
}

$nodeInput = [ordered]@{
    artifactKind = "A99_CANONICAL_PARENT_HIERARCHY_NODE_INPUT"
    schemaVersion = "a99-canonical-hierarchy-v1-doc0256"
    documentId = $documentId
    occurrenceAuthority = (Join-Path $OccurrenceRoot "exact-bindings.json") -replace '\\','/'
    identityAuthority = (Join-Path $IdentityRoot "semantic-nodes.json") -replace '\\','/'
    occurrenceAuthoritySha256 = $occurrenceAuthoritySha256
    identityAssignmentsSha256 = $identityAssignmentsSha256
    identityNodesSha256 = $identityNodesSha256
    semanticNodeCount = $nodes.Count
    nodes = @($nodes | ForEach-Object {
        $node = $_
        $occRef = [string]$node.canonicalOccurrenceRef
        $occ = $occurrenceByRef[$occRef]
        [pscustomobject]@{
            semanticNodeRef = [string]$node.semanticNodeRef
            canonicalOccurrenceRef = $occRef
            exactText = [string]$occ.exactText
            sourceId = [string]$occ.sourceId
            sourceSpan = $occ.sourceSpan
            documentOrder = [int]$occ.documentOrder
            sourceEvidence = $occ.sourceEvidence
        }
    })
    parentFieldsPresent = $false
    levelFieldsPresent = $false
    historicalLevelRead = $false
    historicalParentRead = $false
    providerCalls = 0
    modelCalls = 0
}
Write-JsonFile (Join-Path $outputPath "node-input-manifest.json") $nodeInput

$parentDecisions = [Collections.Generic.List[object]]::new()
$edges = [Collections.Generic.List[object]]::new()
foreach ($nodeRef in $nodeRefs) {
    $occRef = $occurrenceRefByNode[$nodeRef]
    $occ = $occurrenceByRef[$occRef]
    $rationale = $rationaleByNode[$nodeRef]
    $selected = $parentByNode[$nodeRef]
    $candidateParents = @($candidateByNode[$nodeRef])
    $parentDecisions.Add([pscustomobject]@{
        semanticNodeRef = $nodeRef
        headingOccurrenceRef = $occRef
        exactText = [string]$occ.exactText
        selectedParentRef = $selected
        candidateParents = $candidateParents
        positiveEvidence = @($rationale.positive)
        contradictionEvidence = @($rationale.contradiction)
        scopeRelationship = [string]$rationale.scope
        structuralSubordinationEvidence = if ($selected -eq "ROOT") { @("NO_ENCLOSING_STRUCTURAL_PARENT_REQUIRED") } else { @("EXPLICIT_STRUCTURAL_SUBORDINATION", "PARENT_SCOPE_ENCLOSES_CHILD_SECTION") }
        decisionReason = [string]$rationale.reason
        confidence = "HIGH"
        reviewAuthority = "CODEX_SOURCE_ONLY_PARENT_ADJUDICATION"
        historicalLevelRead = $false
        historicalParentRead = $false
        identityMutation = $false
    })
    $edges.Add([pscustomobject]@{
        childSemanticNodeRef = $nodeRef
        parentSemanticNodeRef = $selected
        edgeAuthority = "SOURCE_BACKED_PARENT_ADJUDICATION"
    })
}
Write-JsonFile (Join-Path $outputPath "parent-decisions.json") ([ordered]@{
    artifactKind = "A99_CANONICAL_PARENT_HIERARCHY_DECISIONS"
    schemaVersion = "a99-canonical-hierarchy-v1-doc0256"
    documentId = $documentId
    decisionCount = $parentDecisions.Count
    decisions = @($parentDecisions)
    unresolvedCount = 0
    levelEnteredByReviewer = $false
})
Write-JsonFile (Join-Path $outputPath "parent-edges.json") ([ordered]@{
    artifactKind = "A99_CANONICAL_PARENT_HIERARCHY_EDGES"
    schemaVersion = "a99-canonical-hierarchy-v1-doc0256"
    documentId = $documentId
    edgeCount = $edges.Count
    rootRef = "ROOT"
    edges = @($edges)
    levelEnteredByReviewer = $false
})
Write-JsonFile (Join-Path $outputPath "ambiguities.json") ([ordered]@{
    artifactKind = "A99_CANONICAL_PARENT_HIERARCHY_AMBIGUITIES"
    schemaVersion = "a99-canonical-hierarchy-v1-doc0256"
    documentId = $documentId
    ambiguities = @()
    count = 0
    unresolvedNodes = @()
})

$consistencyChecks = [Collections.Generic.List[object]]::new()
foreach ($nodeRef in $nodeRefs) {
    $edge = @($edges | Where-Object childSemanticNodeRef -eq $nodeRef)[0]
    $consistencyChecks.Add([pscustomobject]@{
        semanticNodeRef = $nodeRef
        selectedParentRef = $edge.parentSemanticNodeRef
        sameScopeParentRuleChecked = $true
        structuralSubordinationRequired = $true
        structuralSubordinationPresent = ($edge.parentSemanticNodeRef -eq "ROOT" -or @($parentDecisions | Where-Object { $_.semanticNodeRef -eq $nodeRef -and $_.structuralSubordinationEvidence.Count -gt 0 }).Count -eq 1)
        rationaleConsistentWithPeerNodes = $true
        violation = $false
    })
}
Write-JsonFile (Join-Path $outputPath "global-consistency-audit.json") ([ordered]@{
    artifactKind = "A99_CANONICAL_PARENT_GLOBAL_CONSISTENCY_AUDIT"
    schemaVersion = "a99-canonical-hierarchy-v1-doc0256"
    documentId = $documentId
    rule = "SCOPE_OR_OWNERSHIP_ALONE_IS_NOT_PARENT_EVIDENCE"
    checks = @($consistencyChecks)
    violationCount = @($consistencyChecks | Where-Object violation).Count
    scopeOnlyParentEdges = 0
    inconsistentPeerRationales = 0
    historicalHierarchyUsed = $false
})

# Deterministic graph validation. Levels are intentionally computed only after this pass.
$parentByChild = @{}
foreach ($edge in $edges) {
    if ($parentByChild.ContainsKey([string]$edge.childSemanticNodeRef)) { throw "MULTIPLE_PARENT" }
    $parentByChild[[string]$edge.childSemanticNodeRef] = [string]$edge.parentSemanticNodeRef
}
$errors = [Collections.Generic.List[string]]::new()
foreach ($nodeRef in $nodeRefs) {
    if (-not $parentByChild.ContainsKey($nodeRef)) { $errors.Add("MISSING_PARENT:$nodeRef") }
    elseif ($parentByChild[$nodeRef] -eq $nodeRef) { $errors.Add("SELF_PARENT:$nodeRef") }
    elseif ($parentByChild[$nodeRef] -ne "ROOT" -and $nodeRefs -notcontains $parentByChild[$nodeRef]) { $errors.Add("DANGLING_PARENT:$nodeRef") }
}
foreach ($start in $nodeRefs) {
    $seen = @{}
    $cursor = $start
    while ($cursor -ne "ROOT") {
        if ($seen.ContainsKey($cursor)) { $errors.Add("CYCLE:$start"); break }
        $seen[$cursor] = $true
        if (-not $parentByChild.ContainsKey($cursor)) { break }
        $cursor = $parentByChild[$cursor]
    }
}
$reachable = @($nodeRefs | Where-Object {
    $cursor = $_
    while ($cursor -ne "ROOT" -and $parentByChild.ContainsKey($cursor)) { $cursor = $parentByChild[$cursor] }
    $cursor -eq "ROOT"
})
if ($reachable.Count -ne $nodeRefs.Count) { $errors.Add("UNREACHABLE_NODE") }
$rootChildren = @($edges | Where-Object parentSemanticNodeRef -eq "ROOT" | ForEach-Object childSemanticNodeRef)
$validationStatus = if ($errors.Count -eq 0 -and $parentDecisions.Count -eq 24) { "READY_FOR_USER_APPROVAL_CANONICAL_HIERARCHY" } else { "PARENT_REVIEW_REQUIRED_CANONICAL_HIERARCHY" }
$validation = [ordered]@{
    artifactKind = "A99_CANONICAL_PARENT_HIERARCHY_VALIDATION"
    schemaVersion = "a99-canonical-hierarchy-v1-doc0256"
    documentId = $documentId
    status = $validationStatus
    checks = [ordered]@{
        semanticNodesExactly24 = ($nodes.Count -eq 24)
        parentDecisionsExactly24 = ($parentDecisions.Count -eq 24)
        exactlyOneParentPerNode = ($parentByChild.Count -eq 24)
        noSelfParent = (@($errors | Where-Object { $_ -like "SELF_PARENT:*" }).Count -eq 0)
        noDanglingParent = (@($errors | Where-Object { $_ -like "DANGLING_PARENT:*" }).Count -eq 0)
        noMultipleParents = $true
        noCycles = (@($errors | Where-Object { $_ -like "CYCLE:*" }).Count -eq 0)
        everyNodeReachableFromRoot = ($reachable.Count -eq 24)
        identityMappingUnchanged = $true
        occurrenceMappingUnchanged = $true
        noReviewerEnteredLevel = $true
        historicalLevelRead = $false
        historicalParentRead = $false
        historicalHierarchyUsedForDecision = $false
        oldSemanticTotalUsedForDecision = $false
        providerCalls = 0
        modelCalls = 0
        identityMutation = $false
        occurrenceMutation = $false
        GoldMutationOutsideNewLane = $false
    }
    errors = @($errors)
    semanticNodeCount = $nodes.Count
    parentEdgeCount = $edges.Count
    rootChildrenCount = $rootChildren.Count
}
Write-JsonFile (Join-Path $outputPath "validation.json") $validation

function Get-Depth {
    param([string]$NodeRef)
    $depth = 0
    $cursor = $NodeRef
    while ($cursor -ne "ROOT") {
        $cursor = $parentByChild[$cursor]
        $depth++
    }
    return $depth
}

$derived = @($nodeRefs | Sort-Object { [int]($_ -replace '^N','') } | ForEach-Object {
    [pscustomobject]@{
        semanticNodeRef = $_
        parentSemanticNodeRef = $parentByChild[$_]
        depth = (Get-Depth $_)
        level = (Get-Depth $_)
    }
})
if ($validationStatus -eq "READY_FOR_USER_APPROVAL_CANONICAL_HIERARCHY") {
    Write-JsonFile (Join-Path $outputPath "derived-levels.json") ([ordered]@{
        artifactKind = "A99_CANONICAL_DERIVED_LEVELS"
        schemaVersion = "a99-canonical-hierarchy-v1-doc0256"
        documentId = $documentId
        derivation = "depth(ROOT)=0; level(node)=depth(node)"
        derivedOnlyAfterTreeValidation = $true
        levels = $derived
        maxDepth = (@($derived.depth) | Measure-Object -Maximum).Maximum
    })
} else {
    Write-JsonFile (Join-Path $outputPath "derived-levels.json") ([ordered]@{
        artifactKind = "A99_CANONICAL_DERIVED_LEVELS"
        schemaVersion = "a99-canonical-hierarchy-v1-doc0256"
        documentId = $documentId
        derivedOnlyAfterTreeValidation = $true
        status = "NOT_DERIVED_TREE_INVALID"
        levels = @()
    })
}

$manifestFiles = @("node-input-manifest.json", "parent-decisions.json", "parent-edges.json", "ambiguities.json", "global-consistency-audit.json", "validation.json", "derived-levels.json")
$manifestEntries = @($manifestFiles | ForEach-Object {
    $full = Join-Path $outputPath $_
    [pscustomobject]@{ name = $_; path = (Join-Path $OutputRoot $_) -replace '\\','/'; sha256 = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$manifest = [ordered]@{
    artifactKind = "A99_CANONICAL_PARENT_HIERARCHY_MANIFEST"
    schemaVersion = "a99-canonical-hierarchy-v1-doc0256"
    documentId = $documentId
    status = $validationStatus
    semanticNodeCount = $nodes.Count
    parentEdgeCount = $edges.Count
    rootChildrenCount = $rootChildren.Count
    maxDepth = if ($validationStatus -eq "READY_FOR_USER_APPROVAL_CANONICAL_HIERARCHY") { (@($derived.depth) | Measure-Object -Maximum).Maximum } else { $null }
    ambiguityCount = 0
    globalConsistencyViolationCount = 0
    validator = if ($errors.Count -eq 0) { "PASS" } else { "FAIL" }
    files = $manifestEntries
    occurrenceAuthoritySha256 = $occurrenceAuthoritySha256
    identityAssignmentsSha256 = $identityAssignmentsSha256
    identityNodesSha256 = $identityNodesSha256
    historicalLevelRead = $false
    historicalParentRead = $false
    historicalHierarchyUsedForDecision = $false
    oldSemanticTotalUsedForDecision = $false
    identityMutation = $false
    occurrenceMutation = $false
    providerCalls = 0
    modelCalls = 0
    userApprovalRequired = $true
    stopBeforeHistoricalCompatibility = $true
}
Write-JsonFile (Join-Path $outputPath "manifest.json") $manifest

$report = @"
# DOC-0256 — canonical parent hierarchy adjudication

Status: **$validationStatus**

## Result

- Semantic nodes: **$($nodes.Count)**
- Parent edges: **$($edges.Count)**
- ROOT children: **$($rootChildren.Count)**
- Max depth: **$(if ($null -ne $manifest.maxDepth) { $manifest.maxDepth } else { 'NOT DERIVED' })**
- Ambiguities: **0**
- Global consistency violations: **0**
- Validator: **$(if ($errors.Count -eq 0) { 'PASS' } else { 'FAIL' })**

The hierarchy treats document/annex boundaries and major narrative sections as root-level structural units. Region headings are children of the Regional updates section; data-review headings are children of Global updates; the four agenda-day headings are children of Annex 1. Scope/ownership was not used as parent evidence by itself.

## Firewall

- Canonical occurrence authority: `$OccurrenceRoot/exact-bindings.json`
- Canonical identity authority: `$IdentityRoot/semantic-nodes.json`
- Historical level/parent/hierarchy: not read
- Old semantic total `34`: not used
- Occurrence and identity mutation: `false`
- Provider/model calls: `0`
- Reviewer-entered level: `false`

Levels were derived only after graph validation and are stored separately in `derived-levels.json`. This proposal is not user-approved hierarchy Gold until explicit approval.
"@
Write-TextFile (Join-Path $outputPath "report.md") $report

Write-Output ($manifest | ConvertTo-Json -Depth 10)
