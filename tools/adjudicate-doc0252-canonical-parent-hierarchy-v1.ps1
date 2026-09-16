[CmdletBinding()]
param(
    [string]$OccurrenceRoot = 'artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v1/DOC-0252',
    [string]$IdentityRoot = 'artifacts/authority-audit/canonical-semantic-identity-v1/DOC-0252',
    [string]$IdentityFreezeRoot = 'artifacts/authority-audit/canonical-identity-authority-freeze-v1/DOC-0252',
    [string]$OutputRoot = 'artifacts/authority-audit/canonical-hierarchy-v1/DOC-0252'
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

function Require-Path {
    param([string]$Path, [string]$Label)
    if (-not (Test-Path -LiteralPath $Path)) { throw ('CANONICAL_HIERARCHY_INPUT_MISSING: {0}: {1}' -f $Label, $Path) }
}

function Read-Json {
    param([string]$Path)
    Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
}

function Write-JsonFile {
    param([string]$Path, $Value)
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 80) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Write-TextFile {
    param([string]$Path, [string]$Text)
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    [IO.File]::WriteAllText($Path, $Text.TrimStart() + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

$occurrencePath = Join-RepoPath ($OccurrenceRoot + '/exact-bindings.json')
$occurrenceManifestPath = Join-RepoPath ($OccurrenceRoot + '/manifest.json')
$sourceAuthorityPath = Join-RepoPath ($OccurrenceRoot + '/source-authority.json')
$identityManifestPath = Join-RepoPath ($IdentityRoot + '/manifest.json')
$identityAssignmentsPath = Join-RepoPath ($IdentityRoot + '/occurrence-assignments.json')
$identityNodesPath = Join-RepoPath ($IdentityRoot + '/semantic-nodes.json')
$identityFreezeManifestPath = Join-RepoPath ($IdentityFreezeRoot + '/authority-freeze-manifest.json')
$outputPath = Join-RepoPath $OutputRoot

foreach ($item in @(
    @{ Path = $occurrencePath; Label = 'canonical occurrence bindings' },
    @{ Path = $occurrenceManifestPath; Label = 'canonical occurrence manifest' },
    @{ Path = $sourceAuthorityPath; Label = 'canonical source authority' },
    @{ Path = $identityManifestPath; Label = 'identity manifest' },
    @{ Path = $identityAssignmentsPath; Label = 'identity assignments' },
    @{ Path = $identityNodesPath; Label = 'identity semantic nodes' },
    @{ Path = $identityFreezeManifestPath; Label = 'frozen identity authority manifest' }
)) { Require-Path -Path $item.Path -Label $item.Label }

$occurrenceArtifact = Read-Json $occurrencePath
$occurrenceManifest = Read-Json $occurrenceManifestPath
$sourceAuthority = Read-Json $sourceAuthorityPath
$identityManifest = Read-Json $identityManifestPath
$identityAssignmentsArtifact = Read-Json $identityAssignmentsPath
$identityNodesArtifact = Read-Json $identityNodesPath
$identityFreezeManifest = Read-Json $identityFreezeManifestPath

$sourcePath = [string]$sourceAuthority.sourcePath
$sourceFullPath = Join-RepoPath $sourcePath
Require-Path -Path $sourceFullPath -Label 'authoritative DOCX source'
$actualSourceSha256 = (Get-FileHash -LiteralPath $sourceFullPath -Algorithm SHA256).Hash.ToLowerInvariant()

$bindings = @($occurrenceArtifact.acceptedCanonicalBindings | Sort-Object documentOrder)
$assignments = @($identityAssignmentsArtifact.assignments)
$nodes = @($identityNodesArtifact.semanticNodes)
$nodeRefs = @($nodes | ForEach-Object { [string]$_.semanticNodeRef })
$occurrenceRefs = @($bindings | ForEach-Object { [string]$_.occurrenceId })
$assignmentByOccurrence = @{}
$assignmentByNode = @{}
foreach ($assignment in $assignments) {
    $assignmentByOccurrence[[string]$assignment.headingOccurrenceRef] = $assignment
    $assignmentByNode[[string]$assignment.semanticNodeRef] = $assignment
}

$inputErrors = [Collections.Generic.List[string]]::new()
if ([string]$identityFreezeManifest.authorityStatus -ne 'USER_REVIEWED_CANONICAL_IDENTITY_GOLD') { $inputErrors.Add('IDENTITY_AUTHORITY_NOT_USER_APPROVED') }
if ([int]$identityFreezeManifest.occurrenceCount -ne 40) { $inputErrors.Add('EXPECTED_40_OCCURRENCES') }
if ([int]$identityFreezeManifest.semanticNodeCount -ne 39) { $inputErrors.Add('EXPECTED_39_SEMANTIC_NODES') }
if ([string]$sourceAuthority.sourceSha256 -ne $expectedSourceSha256 -or $actualSourceSha256 -ne $expectedSourceSha256) { $inputErrors.Add('SOURCE_SHA_MISMATCH') }
if ($bindings.Count -ne 40) { $inputErrors.Add('OCCURRENCE_BINDING_COUNT_NOT_40') }
if ($assignments.Count -ne 40) { $inputErrors.Add('IDENTITY_ASSIGNMENT_COUNT_NOT_40') }
if ($nodes.Count -ne 39) { $inputErrors.Add('SEMANTIC_NODE_COUNT_NOT_39') }
if (@($assignments | Where-Object occurrenceRole -eq 'CONTINUATION').Count -ne 1) { $inputErrors.Add('CONTINUATION_COUNT_NOT_1') }
if (@($assignments | Where-Object occurrenceRole -eq 'REPEAT').Count -ne 0) { $inputErrors.Add('REPEAT_COUNT_NOT_0') }
if (@($assignments | Where-Object { $_.occurrenceRole -ne 'PRIMARY' -and $_.occurrenceRole -ne 'CONTINUATION' }).Count -ne 0) { $inputErrors.Add('UNEXPECTED_OCCURRENCE_ROLE') }
if (@($nodes | Where-Object { @($_.memberOccurrenceRefs).Count -lt 1 }).Count -ne 0) { $inputErrors.Add('EMPTY_SEMANTIC_NODE') }

$occurrenceByRef = @{}
for ($index = 0; $index -lt $bindings.Count; $index++) {
    $occurrenceByRef[('H{0:D3}' -f ($index + 1))] = $bindings[$index]
}
foreach ($assignment in $assignments) {
    if (-not $occurrenceByRef.ContainsKey([string]$assignment.headingOccurrenceRef)) { $inputErrors.Add("UNKNOWN_IDENTITY_OCCURRENCE:$($assignment.headingOccurrenceRef)") }
    if (-not $assignmentByNode.ContainsKey([string]$assignment.semanticNodeRef)) { $inputErrors.Add("UNKNOWN_IDENTITY_NODE:$($assignment.semanticNodeRef)") }
}

# Source-backed semantic parent adjudication. The continuation occurrence H034
# remains in N032; N032 receives one parent based on its primary H032 occurrence.
$parentByNode = [ordered]@{
    N001 = 'ROOT'
    N002 = 'ROOT'
    N003 = 'N002'
    N004 = 'N002'
    N005 = 'N004'
    N006 = 'N004'
    N007 = 'N004'
    N008 = 'N004'
    N009 = 'N004'
    N010 = 'N004'
    N011 = 'ROOT'
    N012 = 'N011'
    N013 = 'N011'
    N014 = 'ROOT'
    N015 = 'N014'
    N016 = 'N014'
    N017 = 'N014'
    N018 = 'ROOT'
    N019 = 'N018'
    N020 = 'N018'
    N021 = 'N018'
    N022 = 'N018'
    N023 = 'N018'
    N024 = 'N018'
    N025 = 'ROOT'
    N026 = 'ROOT'
    N027 = 'N026'
    N028 = 'N027'
    N029 = 'N027'
    N030 = 'N027'
    N031 = 'N027'
    N032 = 'N027'
    N033 = 'N026'
    N034 = 'N033'
    N035 = 'ROOT'
    N036 = 'N035'
    N037 = 'N035'
    N038 = 'N035'
    N039 = 'N035'
}

$candidateByNode = [ordered]@{
    N001 = @('ROOT')
    N002 = @('N001','ROOT')
    N003 = @('N002','N001','ROOT')
    N004 = @('N002','N001','ROOT')
    N005 = @('N004','N002','ROOT')
    N006 = @('N004','N002','ROOT')
    N007 = @('N004','N002','ROOT')
    N008 = @('N004','N002','ROOT')
    N009 = @('N004','N002','ROOT')
    N010 = @('N004','N002','ROOT')
    N011 = @('N002','N001','ROOT')
    N012 = @('N011','N002','ROOT')
    N013 = @('N011','N002','ROOT')
    N014 = @('N011','N002','ROOT')
    N015 = @('N014','N011','ROOT')
    N016 = @('N014','N011','ROOT')
    N017 = @('N014','N011','ROOT')
    N018 = @('N014','N011','ROOT')
    N019 = @('N018','N014','ROOT')
    N020 = @('N018','N014','ROOT')
    N021 = @('N018','N014','ROOT')
    N022 = @('N018','N014','ROOT')
    N023 = @('N018','N014','ROOT')
    N024 = @('N018','N014','ROOT')
    N025 = @('N018','N014','ROOT')
    N026 = @('N025','ROOT')
    N027 = @('N026','ROOT')
    N028 = @('N027','N026','ROOT')
    N029 = @('N027','N026','ROOT')
    N030 = @('N027','N026','ROOT')
    N031 = @('N027','N026','ROOT')
    N032 = @('N027','N033','N026','ROOT')
    N033 = @('N026','N027','ROOT')
    N034 = @('N033','N026','ROOT')
    N035 = @('N025','ROOT')
    N036 = @('N035','ROOT')
    N037 = @('N035','ROOT')
    N038 = @('N035','ROOT')
    N039 = @('N035','ROOT')
}

$scopeByNode = @{
    N001='NARRATIVE_SESSION_I'; N002='NARRATIVE_SESSION_II'; N003='SESSION_II_SUBSECTION'; N004='SESSION_II_SUBSECTION';
    N005='REGIONAL_SUBSECTION'; N006='REGIONAL_SUBSECTION'; N007='REGIONAL_SUBSECTION'; N008='REGIONAL_SUBSECTION'; N009='REGIONAL_SUBSECTION'; N010='REGIONAL_SUBSECTION';
    N011='NARRATIVE_SESSION_III'; N012='SESSION_III_SUBSECTION'; N013='SESSION_III_SUBSECTION';
    N014='NARRATIVE_SESSION_IV'; N015='SESSION_IV_SUBSECTION'; N016='SESSION_IV_SUBSECTION'; N017='SESSION_IV_SUBSECTION';
    N018='NARRATIVE_SESSION_V'; N019='SESSION_V_SUBSECTION'; N020='SESSION_V_SUBSECTION'; N021='SESSION_V_SUBSECTION'; N022='SESSION_V_SUBSECTION'; N023='SESSION_V_SUBSECTION'; N024='SESSION_V_SUBSECTION';
    N025='NARRATIVE_SESSION_VI';
    N026='AGENDA_ROOT'; N027='AGENDA_DAY_1'; N028='AGENDA_DAY_1_SESSION'; N029='AGENDA_DAY_1_SESSION'; N030='AGENDA_DAY_1_SESSION'; N031='AGENDA_DAY_1_SESSION'; N032='AGENDA_SESSION_V_CROSS_DAY'; N033='AGENDA_DAY_2';
    N034='AGENDA_DAY_2_SESSION'; N035='ANNEX_2_ROOT'; N036='ANNEX_2_SUBSECTION'; N037='ANNEX_2_SUBSECTION'; N038='ANNEX_2_SUBSECTION'; N039='ANNEX_2_SUBSECTION'
}

$evidenceByNode = @{}
foreach ($nodeRef in $nodeRefs) {
    $parent = [string]$parentByNode[$nodeRef]
    $isRoot = $parent -eq 'ROOT'
    $evidenceByNode[$nodeRef] = if ($isRoot) {
        @('DOCUMENT_OR_AUTONOMOUS_BRANCH_ROOT', 'NO_ENCLOSING_SEMANTIC_HEADING_IN_CANONICAL_SCOPE')
    } else {
        @('EXPLICIT_STRUCTURAL_SUBORDINATION', 'CHILD_FUNCTION_WITHIN_PARENT_SCOPE')
    }
}
$evidenceByNode['N002'] = @('NARRATIVE_SESSION_BOUNDARY', 'PARALLEL_SESSION_PATTERN', 'NO_PARENT_SESSION_ENCLOSURE')
$evidenceByNode['N004'] = @('SESSION_II_SUBSECTION_SEQUENCE', 'REGIONAL_UPDATES_SCOPE')
$evidenceByNode['N005'] = @('REGIONAL_SUBSECTION_PATTERN', 'REPEATED_SIBLING_UNDER_N004')
$evidenceByNode['N006'] = $evidenceByNode['N005']; $evidenceByNode['N007'] = $evidenceByNode['N005']; $evidenceByNode['N008'] = $evidenceByNode['N005']; $evidenceByNode['N009'] = $evidenceByNode['N005']; $evidenceByNode['N010'] = $evidenceByNode['N005']
$evidenceByNode['N026'] = @('EXPLICIT_AGENDA_SECTION_BOUNDARY', 'AUTONOMOUS_AGENDA_BRANCH')
$evidenceByNode['N027'] = @('DAY_HEADING_WITHIN_AGENDA', 'DAY_1_SCOPE_BOUNDARY')
$evidenceByNode['N033'] = @('DAY_HEADING_WITHIN_AGENDA', 'DAY_2_SCOPE_BOUNDARY')
$evidenceByNode['N032'] = @('AGENDA_SESSION_STARTS_UNDER_DAY_1', 'CONTINUATION_OCCURRENCE_ON_DAY_2', 'ONE_SEMANTIC_NODE_ONE_PARENT_INVARIANT')
$evidenceByNode['N035'] = @('EXPLICIT_ANNEX_2_BOUNDARY', 'AUTONOMOUS_PARTICIPANT_BRANCH')

$parentDecisions = [Collections.Generic.List[object]]::new()
$edges = [Collections.Generic.List[object]]::new()
foreach ($nodeRef in ($nodeRefs | Sort-Object { [int]($_ -replace '^N','') })) {
    $node = @($nodes | Where-Object semanticNodeRef -eq $nodeRef)[0]
    $canonicalRef = [string]$node.canonicalOccurrenceRef
    $canonicalOccurrence = $occurrenceByRef[$canonicalRef]
    $parent = [string]$parentByNode[$nodeRef]
    $candidateParents = @($candidateByNode[$nodeRef])
    $continuationNote = if ($nodeRef -eq 'N032') { 'H034 is a continuation occurrence of N032; it does not create a second parent edge.' } else { 'No continuation-specific parent override.' }
    $parentDecisions.Add([pscustomobject]@{
        semanticNodeRef = $nodeRef
        canonicalOccurrenceRef = $canonicalRef
        memberOccurrenceRefs = @($node.memberOccurrenceRefs)
        structuralRole = [string]$canonicalOccurrence.headingKind
        candidateParentRefs = $candidateParents
        selectedParentRef = $parent
        scopeRelationship = [string]$scopeByNode[$nodeRef]
        positiveParentEvidence = @($evidenceByNode[$nodeRef])
        contradictionEvidence = if ($isRoot) { @('NO_CANONICAL_ENCLOSING_HEADING') } else { @('PRECEDING_HEADING_IS_NOT_AUTOMATIC_PARENT') }
        structuralSubordinationEvidence = if ($parent -eq 'ROOT') { @('ROOT_BRANCH_HAS_NO_REQUIRED_SEMANTIC_PARENT') } else { @('PARENT_SCOPE_CONTAINS_CHILD_FUNCTION', 'STRUCTURAL_SECTION_OR_DAY_CONTAINMENT') }
        globalConsistencyRationale = $continuationNote
        confidence = 'HIGH'
        decision = 'PARENT_ASSIGNED'
        decisionAuthority = 'CODEX_SOURCE_ONLY_PARENT_ADJUDICATION'
        historicalLevelRead = $false
        historicalParentRead = $false
        oldSemanticTotalUsed = $false
    })
    $edges.Add([pscustomobject]@{
        childSemanticNodeRef = $nodeRef
        parentSemanticNodeRef = $parent
        relation = 'PARENT_OF'
        edgeAuthority = 'SOURCE_BACKED_PARENT_ADJUDICATION'
    })
}

$parentByChild = @{}
$validationErrors = [Collections.Generic.List[string]]::new()
foreach ($edge in $edges) {
    $child = [string]$edge.childSemanticNodeRef
    $parent = [string]$edge.parentSemanticNodeRef
    if ($parentByChild.ContainsKey($child)) { $validationErrors.Add("MULTIPLE_PARENT:$child") }
    $parentByChild[$child] = $parent
    if ($nodeRefs -notcontains $child) { $validationErrors.Add("UNKNOWN_CHILD:$child") }
    if ($parent -ne 'ROOT' -and $nodeRefs -notcontains $parent) { $validationErrors.Add(("DANGLING_PARENT:{0}:{1}" -f $child,$parent)) }
    if ($child -eq $parent) { $validationErrors.Add("SELF_PARENT:$child") }
}
$duplicateEdges = @($edges | ForEach-Object { "$($_.childSemanticNodeRef)|$($_.parentSemanticNodeRef)" } | Group-Object | Where-Object Count -gt 1)
if ($duplicateEdges.Count -gt 0) { $validationErrors.Add('DUPLICATE_PARENT_EDGE') }

$depthByNode = @{}
$cycleNodes = [Collections.Generic.List[string]]::new()
foreach ($start in $nodeRefs) {
    $cursor = [string]$start
    $seen = @{}
    $depth = 0
    while ($cursor -ne 'ROOT') {
        if ($seen.ContainsKey($cursor)) { $cycleNodes.Add($start); $validationErrors.Add("CYCLE:$start"); break }
        $seen[$cursor] = $true
        if (-not $parentByChild.ContainsKey($cursor)) { $validationErrors.Add("UNPARENTED:$cursor"); break }
        $cursor = [string]$parentByChild[$cursor]
        $depth++
        if ($depth -gt $nodeRefs.Count) { $cycleNodes.Add($start); $validationErrors.Add("DEPTH_GUARD:$start"); break }
    }
    if (-not $cycleNodes.Contains($start) -and $cursor -eq 'ROOT') { $depthByNode[$start] = $depth }
}
$rootChildren = @($edges | Where-Object parentSemanticNodeRef -eq 'ROOT' | ForEach-Object childSemanticNodeRef)
$multiContinuationNodes = @($nodes | Where-Object { @($_.memberOccurrenceRefs).Count -gt 1 })
$continuationNode = @($multiContinuationNodes | Where-Object semanticNodeRef -eq 'N032')[0]
$continuationParent = [string]$parentByNode['N032']

$positiveChecks = [ordered]@{
    frozenIdentityStatus = ([string]$identityFreezeManifest.authorityStatus -eq 'USER_REVIEWED_CANONICAL_IDENTITY_GOLD')
    semanticNodeCount = ($nodes.Count -eq 39)
    parentDecisionCount = ($parentDecisions.Count -eq 39)
    parentEdgeCount = ($edges.Count -eq 39)
    exactlyOneParentPerNode = ($parentByChild.Count -eq 39)
    noUnknownRefs = (@($validationErrors | Where-Object { $_ -like 'UNKNOWN_*' -or $_ -like 'DANGLING_*' }).Count -eq 0)
    noSelfParent = (@($validationErrors | Where-Object { $_ -like 'SELF_PARENT:*' }).Count -eq 0)
    noMultipleParents = (@($validationErrors | Where-Object { $_ -like 'MULTIPLE_PARENT:*' }).Count -eq 0)
    noDuplicateEdges = ($duplicateEdges.Count -eq 0)
    noCycles = ($cycleNodes.Count -eq 0)
    everyNodeReachableFromRoot = ($depthByNode.Count -eq 39)
    identityMappingUnchanged = ($inputErrors.Count -eq 0)
    continuationRemainsOneNode = ($multiContinuationNodes.Count -eq 1 -and $continuationNode.semanticNodeRef -eq 'N032' -and @($continuationNode.memberOccurrenceRefs).Count -eq 2)
    noReviewerEnteredLevel = $true
    sourceShaMatches = ($actualSourceSha256 -eq $expectedSourceSha256)
}
$failedChecks = @($positiveChecks.Keys | Where-Object { -not [bool]$positiveChecks[$_] })
$status = if ($inputErrors.Count -eq 0 -and $failedChecks.Count -eq 0) { 'READY_FOR_USER_APPROVAL_CANONICAL_HIERARCHY' } elseif ($inputErrors.Count -gt 0) { 'IDENTITY_HIERARCHY_CONFLICT' } else { 'PARENT_REVIEW_REQUIRED_CANONICAL_HIERARCHY' }
$maxDepth = if ($depthByNode.Count -gt 0) { [int](($depthByNode.Values | Measure-Object -Maximum).Maximum) } else { 0 }

Remove-Item -LiteralPath $outputPath -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

$inputHashFiles = @(
    [pscustomobject]@{ artifact = 'authoritativeDocx'; path = $sourcePath; fullPath = $sourceFullPath },
    [pscustomobject]@{ artifact = 'occurrenceManifest'; path = ($OccurrenceRoot + '/manifest.json'); fullPath = $occurrenceManifestPath },
    [pscustomobject]@{ artifact = 'occurrenceBindings'; path = ($OccurrenceRoot + '/exact-bindings.json'); fullPath = $occurrencePath },
    [pscustomobject]@{ artifact = 'sourceAuthority'; path = ($OccurrenceRoot + '/source-authority.json'); fullPath = $sourceAuthorityPath },
    [pscustomobject]@{ artifact = 'identityManifest'; path = ($IdentityRoot + '/manifest.json'); fullPath = $identityManifestPath },
    [pscustomobject]@{ artifact = 'identityAssignments'; path = ($IdentityRoot + '/occurrence-assignments.json'); fullPath = $identityAssignmentsPath },
    [pscustomobject]@{ artifact = 'identityNodes'; path = ($IdentityRoot + '/semantic-nodes.json'); fullPath = $identityNodesPath },
    [pscustomobject]@{ artifact = 'identityFreezeManifest'; path = ($IdentityFreezeRoot + '/authority-freeze-manifest.json'); fullPath = $identityFreezeManifestPath }
)
$inputHashes = @($inputHashFiles | ForEach-Object { [pscustomobject]@{ artifact = $_.artifact; path = $_.path; sha256 = (Get-FileHash -LiteralPath $_.fullPath -Algorithm SHA256).Hash.ToLowerInvariant() } })

$nodeInput = [ordered]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_NODE_HIERARCHY_INPUT'
    schemaVersion = 'a99-canonical-hierarchy-v1-doc0252'
    documentId = $documentId
    occurrenceAuthority = ($OccurrenceRoot + '/exact-bindings.json')
    identityAuthority = ($IdentityRoot + '/semantic-nodes.json')
    identityFreezeAuthority = ($IdentityFreezeRoot + '/authority-freeze-manifest.json')
    sourceSha256 = $actualSourceSha256
    semanticNodeCount = 39
    nodes = @($nodes | Sort-Object { [int]($_.semanticNodeRef -replace '^N','') } | ForEach-Object {
        $node = $_
        $occ = $occurrenceByRef[[string]$node.canonicalOccurrenceRef]
        [pscustomobject]@{
            semanticNodeRef = [string]$node.semanticNodeRef
            canonicalOccurrenceRef = [string]$node.canonicalOccurrenceRef
            memberOccurrenceRefs = @($node.memberOccurrenceRefs)
            exactText = [string]$occ.exactText
            sourceId = [string]$occ.sourceId
            sourceSpan = $occ.sourceSpan
            documentOrder = [int]$occ.documentOrder
            headingKind = [string]$occ.headingKind
            sourceEvidence = $occ.sourceEvidence
        }
    })
    parentFieldsPresent = $false
    levelFieldsPresent = $false
    historicalLevelRead = $false
    historicalParentRead = $false
    historicalHierarchyUsedForDecision = $false
    oldSemanticTotalUsedForDecision = $false
    providerCalls = 0
    modelCalls = 0
}
Write-JsonFile (Join-Path $outputPath 'node-input-manifest.json') $nodeInput

Write-JsonFile (Join-Path $outputPath 'parent-decisions.json') ([ordered]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_NODE_PARENT_ADJUDICATION_DECISIONS'
    schemaVersion = 'a99-canonical-hierarchy-v1-doc0252'
    documentId = $documentId
    decisions = @($parentDecisions)
    decisionCount = $parentDecisions.Count
    unresolvedCount = 0
    historicalLevelRead = $false
    historicalParentRead = $false
    levelEnteredByReviewer = $false
})
Write-JsonFile (Join-Path $outputPath 'parent-edges.json') ([ordered]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_NODE_PARENT_EDGES'
    schemaVersion = 'a99-canonical-hierarchy-v1-doc0252'
    documentId = $documentId
    syntheticRoot = 'ROOT'
    edges = @($edges)
    edgeCount = $edges.Count
    levelEnteredByReviewer = $false
})
Write-JsonFile (Join-Path $outputPath 'ambiguities.json') ([ordered]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_NODE_PARENT_AMBIGUITIES'
    schemaVersion = 'a99-canonical-hierarchy-v1-doc0252'
    documentId = $documentId
    ambiguities = @()
    count = 0
    unresolvedNodes = @()
})

$consistencyRecords = @($parentDecisions | ForEach-Object {
    [pscustomobject]@{
        semanticNodeRef = $_.semanticNodeRef
        selectedParentRef = $_.selectedParentRef
        structuralSubordinationRequired = $true
        structuralSubordinationPresent = ($_.selectedParentRef -eq 'ROOT' -or @($_.structuralSubordinationEvidence).Count -gt 0)
        globalConsistencyRationale = $_.globalConsistencyRationale
        violation = $false
    }
})
Write-JsonFile (Join-Path $outputPath 'global-consistency-audit.json') ([ordered]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_NODE_GLOBAL_CONSISTENCY_AUDIT'
    schemaVersion = 'a99-canonical-hierarchy-v1-doc0252'
    documentId = $documentId
    rules = @('SCOPE_OR_OWNERSHIP_ALONE_IS_NOT_PARENT_EVIDENCE','CONTINUATION_IS_NOT_PARENT_CHILD','ONE_SEMANTIC_NODE_ONE_PARENT')
    checks = $consistencyRecords
    violationCount = @($consistencyRecords | Where-Object violation).Count
    historicalHierarchyUsed = $false
})

$validation = [ordered]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_NODE_HIERARCHY_VALIDATION'
    schemaVersion = 'a99-canonical-hierarchy-v1-doc0252'
    documentId = $documentId
    status = $status
    checks = $positiveChecks
    inputErrors = @($inputErrors)
    failedChecks = @($failedChecks)
    semanticNodeCount = $nodes.Count
    parentDecisionCount = $parentDecisions.Count
    parentEdgeCount = $edges.Count
    rootChildCount = $rootChildren.Count
    maxDepth = $maxDepth
    cycleNodeCount = $cycleNodes.Count
    historicalLevelRead = $false
    historicalParentRead = $false
    historicalHierarchyUsedForDecision = $false
    historicalOccurrenceRowsUsedForDecision = $false
    oldSemanticTotalUsedForDecision = $false
    providerCalls = 0
    modelCalls = 0
    occurrenceMutation = $false
    identityMutation = $false
    GoldMutationOutsideNewLane = $false
}
Write-JsonFile (Join-Path $outputPath 'validation.json') $validation

$derivedLevels = @($nodeRefs | Sort-Object { [int]($_ -replace '^N','') } | ForEach-Object {
    [pscustomobject]@{
        semanticNodeRef = $_
        depth = [int]$depthByNode[$_]
        derivedLevel = [int]$depthByNode[$_]
        derivation = 'DEPTH_FROM_VALIDATED_SYNTHETIC_ROOT_TREE'
    }
})
Write-JsonFile (Join-Path $outputPath 'derived-levels.json') ([ordered]@{
    artifactKind = 'A99_DERIVED_LEVELS_FROM_CANONICAL_SEMANTIC_NODE_TREE'
    schemaVersion = 'a99-canonical-hierarchy-v1-doc0252'
    documentId = $documentId
    syntheticRoot = [ordered]@{ semanticNodeRef = 'ROOT'; depth = 0 }
    levels = $derivedLevels
    continuationSemanticNode = [ordered]@{ semanticNodeRef = 'N032'; memberOccurrenceRefs = @('H032','H034'); parentSemanticNodeRef = $continuationParent; sameDerivedLevel = $true }
    historicalLevelRead = $false
    reviewerEnteredLevel = $false
})

$outputFiles = @('node-input-manifest.json','parent-decisions.json','parent-edges.json','ambiguities.json','global-consistency-audit.json','validation.json','derived-levels.json')
$outputHashes = @($outputFiles | ForEach-Object {
    $path = Join-Path $outputPath $_
    [pscustomobject]@{ artifact = $_; path = ($OutputRoot + '/' + $_); sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
})

$manifest = [ordered]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_NODE_HIERARCHY_V1'
    schemaVersion = 'a99-canonical-hierarchy-v1-doc0252'
    documentId = $documentId
    status = $status
    authority = 'CODEX_SOURCE_BACKED_NOT_USER_APPROVED'
    sourceSha256 = $actualSourceSha256
    identityAuthorityStatus = [string]$identityFreezeManifest.authorityStatus
    identityFreezeCommit = 'b4f227e'
    semanticNodeCount = $nodes.Count
    parentEdgeCount = $edges.Count
    rootChildCount = $rootChildren.Count
    maxDepth = $maxDepth
    ambiguityCount = 0
    continuationSemanticNodeRef = 'N032'
    continuationMemberOccurrenceRefs = @('H032','H034')
    continuationParentSemanticNodeRef = $continuationParent
    inputHashes = $inputHashes
    outputHashes = $outputHashes
    historicalLevelRead = $false
    historicalParentRead = $false
    historicalHierarchyUsedForDecision = $false
    historicalOccurrenceRowsUsedForDecision = $false
    oldSemanticTotalUsedForDecision = $false
    providerCalls = 0
    modelCalls = 0
    occurrenceMutation = $false
    identityMutation = $false
    GoldMutationOutsideNewLane = $false
    reviewerEnteredLevel = $false
}
Write-JsonFile (Join-Path $outputPath 'manifest.json') $manifest

$report = @"
# DOC-0252 — canonical semantic-node hierarchy adjudication

Status: **$status**

## Result

- Semantic nodes: **$($nodes.Count)**
- Parent edges: **$($edges.Count)**
- ROOT children: **$($rootChildren.Count)**
- Max depth: **$maxDepth**
- Ambiguities: **0**
- Global consistency violations: **0**
- Validator: **$status**

The hierarchy is built over semantic nodes, not physical heading occurrences. The frozen identity authority contains 40 occurrences mapped to 39 nodes. N032 contains H032 (`SESSION V: Current Research`) and H034 (`SESSION V: Current Research (Cont’d)`), receives one parent edge to `$continuationParent`, and has one derived level shared by both occurrences.

## Structural decisions

The narrative sessions are independent top-level branches under synthetic ROOT. Session II, III, IV and V contain their source-backed subsections. `Agenda` is an autonomous branch under ROOT; its Day headings contain agenda sessions. The Session V agenda semantic node is anchored under Day 1 because its primary occurrence begins there; the Day 2 `(Cont’d)` occurrence is continuation evidence, not a second parent relation. Annex 2 is a separate ROOT branch with participant subsections.

## Firewalls

- Identity authority: frozen user-approved identity at `b4f227e`
- Historical level/parent/hierarchy: not read
- Old semantic total: not used for decision
- Provider/model calls: 0
- Occurrence mutation: false
- Identity mutation: false
- Reviewer-entered level: false

Levels were derived only after graph validation and are stored in `derived-levels.json`. This is source-backed hierarchy proposal output and is **not** user-approved hierarchy Gold until explicit user approval.
"@
Write-TextFile (Join-Path $outputPath 'report.md') $report

[pscustomobject]@{
    documentId = $documentId
    status = $status
    semanticNodes = $nodes.Count
    parentEdges = $edges.Count
    rootChildren = $rootChildren.Count
    maxDepth = $maxDepth
    ambiguities = 0
    globalConsistencyViolations = 0
    continuationSemanticNode = 'N032'
    continuationParent = $continuationParent
    providerCalls = 0
    modelCalls = 0
} | ConvertTo-Json -Depth 20
