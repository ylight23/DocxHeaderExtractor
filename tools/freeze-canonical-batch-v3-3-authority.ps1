[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$ProposalCheckpoint = 'f6833b6',
    [string]$FreezeCommit = 'PENDING_FREEZE_COMMIT'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path -LiteralPath $RepoRoot).Path
$newDocs = @('DOC-0185','DOC-0200','DOC-0201','DOC-0219','DOC-0265','DOC-0243','DOC-0116','DOC-0122','DOC-0216','DOC-0205','DOC-0264')
$existingDocs = @('DOC-0001','DOC-0252','DOC-0256','DOC-0258')
$allDocs = @($newDocs + $existingDocs)
$expectedNew = [ordered]@{ documents = 11; occurrences = 1793; semanticNodes = 1773; parentEdges = 1773 }
$expectedCorpus = [ordered]@{ documents = 15; occurrences = 1908; semanticNodes = 1887; parentEdges = 1887 }
$batchRoot = Join-Path $repo 'artifacts/authority-audit/canonical-batch-v3.3'
$freezeRoot = Join-Path $repo 'artifacts/authority-audit/canonical-authority-freeze-v1'

function Read-Json([string]$Path) { Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json }
function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Rel([string]$Path) { [IO.Path]::GetRelativePath($repo, (Resolve-Path -LiteralPath $Path).Path).Replace('\','/') }
function Write-Json([string]$Path, $Value) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 80) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
function Write-Text([string]$Path, [string]$Text) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [IO.File]::WriteAllText($Path, $Text.TrimStart() + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
function Assert-Equal($Actual, $Expected, [string]$Name) {
    if ($Actual -ne $Expected) { throw "FREEZE_AGGREGATE_MISMATCH: $Name expected $Expected, found $Actual" }
}
function Assert-Path([string]$Path, [string]$Name) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "Missing freeze input '$Name': $Path" }
}
function Get-Array($Value) { if ($null -eq $Value) { return @() }; return @($Value) }
function Get-OptionalProperty($Object, [string]$Name, $Default = $null) {
    if ($null -ne $Object -and $Object.PSObject.Properties.Name -contains $Name) { return $Object.$Name }
    return $Default
}

git cat-file -e "$ProposalCheckpoint^{commit}" 2>$null
if ($LASTEXITCODE -ne 0) { throw "Missing approved proposal checkpoint: $ProposalCheckpoint" }

$batchManifestPath = Join-Path $batchRoot 'manifest.json'
Assert-Path $batchManifestPath 'V3.3 manifest'
$batchManifest = Read-Json $batchManifestPath
if ([string]$batchManifest.status -ne 'BATCH_V3_3_READY_FOR_USER_SEMANTIC_REVIEW') { throw 'V3.3 proposal status is not the approved review boundary.' }
$batchValidation = Read-Json (Join-Path $batchRoot 'validation.json')
$batchRows = @(Get-Array $batchValidation.documents)
$batchValidationOccurrences = [int](($batchRows | Measure-Object occurrences -Sum).Sum)
$batchValidationNodes = [int](($batchRows | Measure-Object semanticNodes -Sum).Sum)
$batchValidationEdges = [int](($batchRows | Measure-Object parentEdges -Sum).Sum)
if ($batchValidationOccurrences -ne $expectedNew.occurrences -or $batchValidationNodes -ne $expectedNew.semanticNodes -or $batchValidationEdges -ne $expectedNew.parentEdges) {
    throw 'FREEZE_AGGREGATE_MISMATCH: V3.3 validation totals do not match the approved expected totals.'
}

$records = [Collections.Generic.List[object]]::new()
$newRecords = [Collections.Generic.List[object]]::new()

foreach ($docId in $newDocs) {
    $occDir = Join-Path $batchRoot "canonical-exhaustive-heading-occurrence-v3.3/$docId"
    $idDir = Join-Path $batchRoot "canonical-semantic-identity-v3.3/$docId"
    $hierDir = Join-Path $batchRoot "canonical-hierarchy-v3.3/$docId"
    $paths = [ordered]@{
        sourceDocument = $null
        proposalManifest = $batchManifestPath
        sourceAuthority = Join-Path $occDir 'source-authority.json'
        exactBindings = Join-Path $occDir 'exact-bindings.json'
        occurrenceValidation = Join-Path $occDir 'validation.json'
        identityDecisions = Join-Path $idDir 'identity-decisions.json'
        semanticNodes = Join-Path $idDir 'semantic-nodes.json'
        identityValidation = Join-Path $idDir 'validation.json'
        hierarchyParentDecisions = Join-Path $hierDir 'parent-decisions.json'
        parentEdges = Join-Path $hierDir 'parent-edges.json'
        hierarchyValidation = Join-Path $hierDir 'validation.json'
        derivedLevels = Join-Path $hierDir 'derived-levels.json'
    }
    foreach ($key in @($paths.Keys | Where-Object { $_ -ne 'sourceDocument' })) { Assert-Path $paths[$key] "$docId/$key" }

    $sourceAuthority = Read-Json $paths.sourceAuthority
    $sourceDocument = Join-Path $repo ([string]$sourceAuthority.sourcePath -replace '/', '\')
    Assert-Path $sourceDocument "$docId/source document"
    $paths.sourceDocument = $sourceDocument
    $sourceSha = Sha $sourceDocument
    if ($sourceSha -ne [string]$sourceAuthority.sourceSha256) { throw "Source SHA mismatch for $docId" }

    $bindingsArtifact = Read-Json $paths.exactBindings
    $identityArtifact = Read-Json $paths.identityDecisions
    $nodesArtifact = Read-Json $paths.semanticNodes
    $parentArtifact = Read-Json $paths.parentEdges
    $derivedArtifact = Read-Json $paths.derivedLevels
    $bindings = @(Get-Array $bindingsArtifact.bindings)
    $assignments = @(Get-Array $identityArtifact.assignments)
    $nodes = @(Get-Array $nodesArtifact.nodes)
    $relations = @(Get-Array $identityArtifact.relations)
    $edges = @(Get-Array $parentArtifact.edges)
    $levels = @(Get-Array $derivedArtifact.levels)

    $occCount = $bindings.Count
    $nodeCount = $nodes.Count
    $edgeCount = $edges.Count
    Assert-Equal $occCount ([int](Get-OptionalProperty $identityArtifact 'occurrenceCount' -1)) "$docId occurrence artifact count"
    Assert-Equal $nodeCount ([int](Get-OptionalProperty $identityArtifact 'semanticNodeCount' -1)) "$docId semantic artifact count"
    Assert-Equal $edgeCount $nodeCount "$docId parent edge count"
    Assert-Equal $levels.Count $nodeCount "$docId derived level count"

    $occRefs = @($bindings | ForEach-Object { [string]$_.occurrenceId })
    if (@($occRefs | Sort-Object -Unique).Count -ne $occCount) { throw "$docId has duplicate occurrence refs" }
    $assignmentRefs = @($assignments | ForEach-Object { [string]$_.occurrenceId })
    if (@($assignmentRefs | Sort-Object -Unique).Count -ne $occCount) { throw "$docId has duplicate/missing identity assignments" }
    if (@(Compare-Object (@($occRefs | Sort-Object)) (@($assignmentRefs | Sort-Object))).Count -ne 0) { throw "$docId identity assignments do not cover occurrences exactly" }

    $nodeRefs = @($nodes | ForEach-Object { [string]$_.semanticNodeRef })
    if (@($nodeRefs | Sort-Object -Unique).Count -ne $nodeCount) { throw "$docId has duplicate semantic node refs" }
    $assignmentNodeRefs = @($assignments | ForEach-Object { [string]$_.semanticNodeRef })
    if (@($assignmentNodeRefs | Where-Object { $nodeRefs -notcontains $_ }).Count -ne 0) { throw "$docId has assignment to unknown semantic node" }
    foreach ($node in $nodes) {
        $members = @(Get-Array $node.memberOccurrenceRefs)
        if ($members.Count -eq 0) { throw "$docId has an empty semantic node" }
        foreach ($member in $members) {
            if ($occRefs -notcontains [string]$member) { throw "$docId node $($node.semanticNodeRef) references unknown occurrence" }
            $assigned = @($assignments | Where-Object { [string]$_.occurrenceId -eq [string]$member -and [string]$_.semanticNodeRef -eq [string]$node.semanticNodeRef })
            if ($assigned.Count -ne 1) { throw "$docId membership mismatch for $member" }
        }
    }
    $roleCounts = [ordered]@{ PRIMARY = @($assignments | Where-Object occurrenceRole -eq 'PRIMARY').Count; REPEAT = @($assignments | Where-Object occurrenceRole -eq 'REPEAT').Count; CONTINUATION = @($assignments | Where-Object occurrenceRole -eq 'CONTINUATION').Count }
    if (($roleCounts.PRIMARY + $roleCounts.REPEAT + $roleCounts.CONTINUATION) -ne $occCount) { throw "$docId has unknown occurrence roles" }

    $edgeByChild = @{}
    foreach ($edge in $edges) {
        $child = [string]$edge.childSemanticNodeRef
        $parent = [string]$edge.parentSemanticNodeRef
        if ($edgeByChild.ContainsKey($child)) { throw "$docId has multiple parents for $child" }
        if ($nodeRefs -notcontains $child) { throw "$docId has dangling child $child" }
        if ($parent -ne 'ROOT' -and $nodeRefs -notcontains $parent) { throw "$docId has dangling parent $child -> $parent" }
        if ($child -eq $parent) { throw "$docId has self cycle $child" }
        $edgeByChild[$child] = $parent
    }
    if ($edgeByChild.Count -ne $nodeCount) { throw "$docId parent edge child coverage mismatch" }
    $depthByNode = @{}
    foreach ($nodeRef in $nodeRefs) {
        $cursor = $nodeRef; $depth = 0; $seen = [Collections.Generic.HashSet[string]]::new()
        while ($cursor -ne 'ROOT') {
            if (-not $seen.Add($cursor)) { throw "$docId has parent cycle from $nodeRef" }
            if (-not $edgeByChild.ContainsKey($cursor)) { throw "$docId has unreachable node $cursor" }
            $cursor = $edgeByChild[$cursor]; $depth++
            if ($depth -gt $nodeCount) { throw "$docId invalid tree depth" }
        }
        $depthByNode[$nodeRef] = $depth
    }
    $rootCount = @($edges | Where-Object { [string]$_.parentSemanticNodeRef -eq 'ROOT' }).Count
    $maxDepth = [int](($depthByNode.Values | Measure-Object -Maximum).Maximum)
    $levelByNode = @{}
    foreach ($level in $levels) {
        $ref = [string]$level.semanticNodeRef
        if ($levelByNode.ContainsKey($ref)) { throw "$docId duplicate derived level $ref" }
        $levelByNode[$ref] = $level
    }
    if ($levelByNode.Count -ne $nodeCount) { throw "$docId derived level node coverage mismatch" }
    foreach ($nodeRef in $nodeRefs) {
        $level = $levelByNode[$nodeRef]
        if ([string]$level.parentSemanticNodeRef -ne $edgeByChild[$nodeRef] -or [int]$level.depth -ne $depthByNode[$nodeRef] -or [int]$level.level -ne $depthByNode[$nodeRef]) { throw "$docId derived level mismatch at $nodeRef" }
    }
    if ([int]$identityArtifact.goldUsed -ne 0 -or [bool]$identityArtifact.historicalLevelRead) { throw "$docId identity firewall failed" }
    if ([bool](Read-Json $paths.occurrenceValidation).historicalLevelRead -or [bool](Read-Json $paths.hierarchyValidation).historicalLevelRead) { throw "$docId source/hierarchy historical-level firewall failed" }

    $artifactHashes = [Collections.Generic.List[object]]::new()
    foreach ($key in $paths.Keys) { $artifactHashes.Add([ordered]@{ artifact = $key; path = Rel $paths[$key]; sha256 = Sha $paths[$key] }) }
    $delta = $occCount - $nodeCount
    $relationRows = @($relations | ForEach-Object { [ordered]@{ fromOccurrenceId = [string]$_.fromOccurrenceId; toOccurrenceId = [string]$_.toOccurrenceId; relation = [string]$_.relation } })
    $identityExplanation = [ordered]@{
        occurrenceCount = $occCount
        semanticNodeCount = $nodeCount
        occurrenceToNodeDelta = $delta
        identityRelationCount = $relations.Count
        relationRows = $relationRows
        statement = if ($delta -eq 0) { 'All approved occurrences remain singleton semantic nodes.' } else { "The approved identity artifact collapses $delta occurrence(s) through the explicitly recorded identity relation(s); no membership was changed during freeze." }
    }
    $validation = [ordered]@{
        artifactKind = 'A99_USER_REVIEWED_CANONICAL_AUTHORITY_FREEZE_VALIDATION'
        schemaVersion = 'a99-canonical-authority-freeze-v1'
        documentId = $docId
        status = 'USER_REVIEWED_CANONICAL_HIERARCHY_GOLD'
        checks = [ordered]@{
            sourceShaMatches = $true
            canonicalOccurrences = $occCount
            occurrenceAssignments = $assignments.Count
            semanticNodes = $nodeCount
            parentEdges = $edgeCount
            primaryCount = $roleCounts.PRIMARY
            repeatCount = $roleCounts.REPEAT
            continuationCount = $roleCounts.CONTINUATION
            identityDelta = $delta
            rootChildren = $rootCount
            maxDepth = $maxDepth
            noCycles = $true
            noMultipleParents = $true
            noDanglingRefs = $true
            noUnreachableNodes = $true
            noUnknownOccurrenceRefs = $true
            noUnknownSemanticNodeRefs = $true
            derivedLevelsReproduceExactly = $true
            historicalLevelRead = $false
            historicalParentRead = $false
            providerCalls = 0
            modelCalls = 0
            occurrenceMutation = $false
            identityMutation = $false
            parentMutation = $false
            GoldMutationOutsideFreezeLane = $false
        }
        identityDeltaExplanation = $identityExplanation
        hashCount = $artifactHashes.Count
        validator = 'PASS'
    }
    $manifest = [ordered]@{
        artifactKind = 'A99_USER_REVIEWED_CANONICAL_AUTHORITY_FREEZE'
        schemaVersion = 'a99-canonical-authority-freeze-v1'
        documentId = $docId
        authorityStatus = 'USER_REVIEWED_CANONICAL_HIERARCHY_GOLD'
        explicitUserApproval = $true
        approvedProposalCheckpoint = $ProposalCheckpoint
        approvedDocuments = $newDocs
        commitLineage = @(
            [ordered]@{ commit = $ProposalCheckpoint; role = 'user-approved V3.3 occurrence, identity and hierarchy proposal' },
            [ordered]@{ commit = $FreezeCommit; role = 'canonical authority freeze' }
        )
        sourcePath = Rel $sourceDocument
        sourceSha256 = $sourceSha
        occurrenceCount = $occCount
        semanticNodeCount = $nodeCount
        occurrenceRoles = $roleCounts
        parentEdgeCount = $edgeCount
        rootChildrenCount = $rootCount
        maxDepth = $maxDepth
        levelDerivation = 'depth(validated canonical semantic-node parent tree)'
        provenance = [ordered]@{
            occurrenceAuthority = 'CODEX_SOURCE_BACKED_PROPOSAL'
            identityAuthorityBeforeApproval = 'CODEX_SOURCE_BACKED_PROPOSAL'
            hierarchyAuthorityBeforeApproval = 'CODEX_SOURCE_BACKED_PROPOSAL'
            finalPromotion = 'EXPLICIT_USER_APPROVAL'
            historicalCompatibility = 'NOT_OPENED_IN_FREEZE'
        }
        identityDeltaExplanation = $identityExplanation
        artifactHashes = @($artifactHashes)
        invariants = $validation.checks
    }
    $outDir = Join-Path $freezeRoot $docId
    Write-Json (Join-Path $outDir 'authority-freeze-manifest.json') $manifest
    Write-Json (Join-Path $outDir 'validation.json') $validation
    Write-Text (Join-Path $outDir 'report.md') @"
# $docId — user-approved canonical V3.3 authority freeze

Status: **USER_REVIEWED_CANONICAL_HIERARCHY_GOLD**

This document was explicitly approved at proposal checkpoint `$ProposalCheckpoint`. The V3.3 occurrence, identity, hierarchy, parent, ROOT, and derived-level decisions were frozen exactly as reviewed; no re-adjudication occurred.

## Frozen authority

- Canonical occurrences: **$occCount**
- Semantic nodes: **$nodeCount**
- PRIMARY: **$($roleCounts.PRIMARY)**; REPEAT: **$($roleCounts.REPEAT)**; CONTINUATION: **$($roleCounts.CONTINUATION)**
- Parent edges: **$edgeCount**
- ROOT children: **$rootCount**
- Max depth: **$maxDepth**
- Level: `depth(validated canonical semantic-node parent tree)`

## Identity delta

$($identityExplanation.statement)

## Provenance and firewall

- Occurrence/identity/hierarchy proposal: Codex source-backed V3.3 proposal
- Final promotion: explicit user approval
- Historical level/parent read: false
- Provider/model calls: 0
- Occurrence, identity, parent, and level mutation during freeze: false

Source SHA-256: `$sourceSha`
Input artifact hashes are recorded in `authority-freeze-manifest.json`.
"@
    $record = [pscustomobject]@{ documentId=$docId; sourcePath=(Rel $sourceDocument); sourceSha256=$sourceSha; occurrences=$occCount; semanticNodes=$nodeCount; parentEdges=$edgeCount; primary=$roleCounts.PRIMARY; repeat=$roleCounts.REPEAT; continuation=$roleCounts.CONTINUATION; rootChildren=$rootCount; maxDepth=$maxDepth; authorityStatus='USER_REVIEWED_CANONICAL_HIERARCHY_GOLD'; manifestPath=(Rel (Join-Path $outDir 'authority-freeze-manifest.json')); validationPath=(Rel (Join-Path $outDir 'validation.json')); validation=$validation }
    $records.Add($record); $newRecords.Add($record)
}

function Read-ExistingAuthority([string]$DocId) {
    if ($DocId -eq 'DOC-0001') { return [pscustomobject]@{ path = 'artifacts/authority-audit/strict-heading-canonical-hierarchy-gold-v1/DOC-0001/manifest.json'; full = Join-Path $repo 'artifacts/authority-audit/strict-heading-canonical-hierarchy-gold-v1/DOC-0001/manifest.json' } }
    return [pscustomobject]@{ path = "artifacts/authority-audit/canonical-authority-freeze-v1/$DocId/authority-freeze-manifest.json"; full = Join-Path $freezeRoot "$DocId/authority-freeze-manifest.json" }
}
foreach ($docId in $existingDocs) {
    $ref = Read-ExistingAuthority $docId
    Assert-Path $ref.full "$docId existing authority"
    $j = Read-Json $ref.full
    $status = if ($j.PSObject.Properties.Name -contains 'authorityStatus') { [string]$j.authorityStatus } else { [string]$j.status }
    if ($status -ne 'USER_REVIEWED_CANONICAL_HIERARCHY_GOLD') { throw "$docId is not a frozen user-reviewed canonical hierarchy authority" }
    $summary = Get-OptionalProperty $j 'summary'
    $occValue = if ($j.PSObject.Properties.Name -contains 'occurrenceCount') { $j.occurrenceCount } else { Get-OptionalProperty $summary 'occurrenceCount' 0 }
    $nodeValue = if ($j.PSObject.Properties.Name -contains 'semanticNodeCount') { $j.semanticNodeCount } else { Get-OptionalProperty $summary 'semanticNodeCount' 0 }
    $edgeValue = if ($j.PSObject.Properties.Name -contains 'parentEdgeCount') { $j.parentEdgeCount } else { Get-OptionalProperty $summary 'parentEdgeCount' 0 }
    $occ = [int]$occValue; $nodes = [int]$nodeValue; $edges = [int]$edgeValue
    $roles = if ($j.PSObject.Properties.Name -contains 'occurrenceRoles') { $j.occurrenceRoles } elseif ($summary -and $summary.PSObject.Properties.Name -contains 'occurrenceRoles') { $summary.occurrenceRoles } else { [pscustomobject]@{ PRIMARY=[int](Get-OptionalProperty $j 'primaryCount' 0); REPEAT=[int](Get-OptionalProperty $j 'repeatCount' 0); CONTINUATION=[int](Get-OptionalProperty $j 'continuationCount' 0) } }
    $rootChildrenValue = if ($j.PSObject.Properties.Name -contains 'rootChildrenCount') { $j.rootChildrenCount } elseif ($j.PSObject.Properties.Name -contains 'rootChildCount') { $j.rootChildCount } elseif ($summary -and $summary.PSObject.Properties.Name -contains 'rootChildCount') { $summary.rootChildCount } else { 0 }
    $maxDepthValue = if ($j.PSObject.Properties.Name -contains 'maxDepth') { $j.maxDepth } else { Get-OptionalProperty $summary 'maxDepth' 0 }
    $records.Add([pscustomobject]@{ documentId=$docId; sourcePath=[string](Get-OptionalProperty $j 'sourcePath' ''); sourceSha256=[string](Get-OptionalProperty $j 'sourceSha256' ''); occurrences=$occ; semanticNodes=$nodes; parentEdges=$edges; primary=[int]$roles.PRIMARY; repeat=[int]$roles.REPEAT; continuation=[int]$roles.CONTINUATION; rootChildren=[int]$rootChildrenValue; maxDepth=[int]$maxDepthValue; authorityStatus=$status; manifestPath=$ref.path; validationPath=$null; existingAuthority=$true })
}

$newOcc = [int](($newRecords | Measure-Object occurrences -Sum).Sum)
$newNodes = [int](($newRecords | Measure-Object semanticNodes -Sum).Sum)
$newEdges = [int](($newRecords | Measure-Object parentEdges -Sum).Sum)
Assert-Equal $newRecords.Count $expectedNew.documents 'V3.3 document count'
Assert-Equal $newOcc $expectedNew.occurrences 'V3.3 occurrences'
Assert-Equal $newNodes $expectedNew.semanticNodes 'V3.3 semantic nodes'
Assert-Equal $newEdges $expectedNew.parentEdges 'V3.3 parent edges'

$corpusOcc = [int](($records | Measure-Object occurrences -Sum).Sum)
$corpusNodes = [int](($records | Measure-Object semanticNodes -Sum).Sum)
$corpusEdges = [int](($records | Measure-Object parentEdges -Sum).Sum)
Assert-Equal $records.Count $expectedCorpus.documents 'full corpus document count'
Assert-Equal $corpusOcc $expectedCorpus.occurrences 'full corpus occurrences'
Assert-Equal $corpusNodes $expectedCorpus.semanticNodes 'full corpus semantic nodes'
Assert-Equal $corpusEdges $expectedCorpus.parentEdges 'full corpus parent edges'

$corpusManifest = [ordered]@{
    artifactKind = 'A99_CANONICAL_15_DOCUMENT_CORPUS_AUTHORITY_MANIFEST'
    schemaVersion = 'a99-canonical-authority-freeze-v1-corpus'
    status = 'CANONICAL_15_DOCUMENT_CORPUS_FROZEN'
    approvedProposalCheckpoint = $ProposalCheckpoint
    documents = @($records | Sort-Object documentId | ForEach-Object { [ordered]@{ documentId=$_.documentId; authorityStatus=$_.authorityStatus; occurrences=$_.occurrences; semanticNodes=$_.semanticNodes; parentEdges=$_.parentEdges; rootChildren=$_.rootChildren; maxDepth=$_.maxDepth; manifestPath=$_.manifestPath; sourcePath=$_.sourcePath; sourceSha256=$_.sourceSha256 } })
    totals = [ordered]@{ documents=$records.Count; occurrences=$corpusOcc; semanticNodes=$corpusNodes; parentEdges=$corpusEdges }
    existingAuthoritiesPreserved = $true
    historicalLevelRead = $false
    historicalParentRead = $false
    providerCalls = 0
    productionModelCalls = 0
    GoldMutationOutsideFreezeLane = $false
    commitLineage = @([ordered]@{ commit=$ProposalCheckpoint; role='approved V3.3 proposal checkpoint' }, [ordered]@{ commit=$FreezeCommit; role='15-document corpus authority freeze' })
}
Write-Json (Join-Path $freezeRoot 'corpus-manifest.json') $corpusManifest
$corpusValidation = [ordered]@{
    artifactKind = 'A99_CANONICAL_15_DOCUMENT_CORPUS_VALIDATION'
    schemaVersion = 'a99-canonical-authority-freeze-v1-corpus'
    status = 'CANONICAL_15_DOCUMENT_CORPUS_FROZEN'
    expectedTotals = $expectedCorpus
    actualTotals = $corpusManifest.totals
    documents = @($records | Sort-Object documentId | ForEach-Object { [ordered]@{ documentId=$_.documentId; authorityStatus=$_.authorityStatus; validator='PASS'; occurrenceCount=$_.occurrences; semanticNodeCount=$_.semanticNodes; parentEdgeCount=$_.parentEdges } })
    checks = [ordered]@{
        aggregateTotalsMatch = $true
        allDocumentsUserReviewed = (@($records | Where-Object authorityStatus -ne 'USER_REVIEWED_CANONICAL_HIERARCHY_GOLD').Count -eq 0)
        newV33TotalsMatch = $true
        cycles = 0
        multipleParents = 0
        dangling = 0
        unreachable = 0
        levelDerivedOnlyFromValidatedTreeDepth = $true
        historicalLevelRead = $false
        historicalParentRead = $false
        providerCalls = 0
        productionModelCalls = 0
        historicalN15Rebaselined = $false
        existingFrozenDocumentsMutated = $false
        GoldMutationOutsideFreezeLane = $false
    }
    validator = 'PASS'
    commitLineage = $corpusManifest.commitLineage
}
Write-Json (Join-Path $freezeRoot 'corpus-validation.json') $corpusValidation
Write-Text (Join-Path $freezeRoot 'corpus-report.md') @"
# Canonical 15-document corpus authority freeze

Status: **CANONICAL_15_DOCUMENT_CORPUS_FROZEN**

The 11 V3.3 proposals approved at `$ProposalCheckpoint` were frozen exactly as reviewed. Existing DOC-0001, DOC-0252, DOC-0256, and DOC-0258 authorities were read by manifest reference and not rewritten.

## Totals

- Documents: **$($records.Count)**
- Canonical occurrences: **$corpusOcc**
- Semantic nodes: **$corpusNodes**
- Parent edges: **$corpusEdges**
- Level: `depth(validated semantic tree)` only

Expected totals were `15 / 1908 / 1887 / 1887` and matched exactly.

## Firewall and validation

- Historical level read: false
- Historical parent read: false
- Provider/model calls: 0
- Existing frozen documents mutated: false
- Cycles: 0; multiple parents: 0; dangling refs: 0; unreachable nodes: 0
- Historical N15 rebaselined: false

Per-document manifests and validations contain the source SHA, proposal checkpoint, approval lineage, input artifact hashes, identity-delta explanations, and derived-level reproduction checks.
"@

Write-Output ([ordered]@{ status='CANONICAL_15_DOCUMENT_CORPUS_FROZEN'; documents=$records.Count; occurrences=$corpusOcc; semanticNodes=$corpusNodes; parentEdges=$corpusEdges; providerCalls=0; modelCalls=0; freezeCommit=$FreezeCommit } | ConvertTo-Json -Depth 20)
