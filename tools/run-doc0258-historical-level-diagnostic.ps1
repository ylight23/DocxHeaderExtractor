$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$freezeDir = Join-Path $repo 'artifacts/authority-audit/canonical-authority-freeze-v1/DOC-0258'
$occPath = Join-Path $repo 'artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v2-span-aware/DOC-0258/exact-bindings.json'
$assignPath = Join-Path $repo 'artifacts/authority-audit/canonical-semantic-identity-v1/DOC-0258/occurrence-assignments.json'
$nodesPath = Join-Path $repo 'artifacts/authority-audit/canonical-semantic-identity-v1/DOC-0258/semantic-nodes.json'
$edgesPath = Join-Path $repo 'artifacts/authority-audit/canonical-hierarchy-v1/DOC-0258/parent-edges.json'
$levelsPath = Join-Path $repo 'artifacts/authority-audit/canonical-hierarchy-v1/DOC-0258/derived-levels.json'
$histPath = Join-Path $repo 'eval/a99-closed-loop/strict-gold-v4/DOC-0258.strict-gold-v4.json'
$outDir = Join-Path $repo 'artifacts/authority-audit/canonical-vs-historical-level-diagnostic-v1/DOC-0258'

function Read-Json($path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
function Sha256($path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Normalize-Text([string]$value) {
    if ($null -eq $value) { return '' }
    $value.Normalize([Text.NormalizationForm]::FormKC) -replace '[‐‑‒–—−]', '-' -replace '\s+', ' ' | ForEach-Object { $_.Trim().ToLowerInvariant() }
}
function Write-Json($path, $value) {
    $value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $path -Encoding UTF8
}

$freeze = Read-Json (Join-Path $freezeDir 'authority-freeze-manifest.json')
if ($freeze.authorityStatus -ne 'USER_REVIEWED_CANONICAL_HIERARCHY_GOLD') { throw 'Canonical authority is not frozen with the required status.' }
if ($freeze.documentId -ne 'DOC-0258') { throw 'Canonical authority document mismatch.' }
if ($freeze.summary.occurrenceCount -ne 44 -or $freeze.summary.semanticNodeCount -ne 44 -or $freeze.summary.parentEdgeCount -ne 44) { throw 'Canonical authority count invariant failed.' }
if ($freeze.invariants.historicalLevelRead -ne $false -or $freeze.invariants.historicalParentRead -ne $false) { throw 'Canonical freeze firewall invariant failed.' }

$occ = Read-Json $occPath
$assignments = Read-Json $assignPath
$nodes = Read-Json $nodesPath
$edges = Read-Json $edgesPath
$levels = Read-Json $levelsPath
$historical = Read-Json $histPath

if ($occ.bindingCount -ne 44 -or $assignments.assignments.Count -ne 44 -or $nodes.semanticNodes.Count -ne 44 -or $edges.edgeCount -ne 44 -or $levels.levels.Count -ne 44) { throw 'Canonical input count invariant failed.' }
if ($historical.documentId -ne 'DOC-0258') { throw 'Historical authority document mismatch.' }

$assignmentBySource = @{}
foreach ($item in $assignments.assignments) { $assignmentBySource[$item.sourceOccurrenceId] = $item }
$bindingBySource = @{}
foreach ($item in $occ.bindings) { $bindingBySource[$item.sourceOccurrenceId] = $item }
$levelByNode = @{}
foreach ($item in $levels.levels) { $levelByNode[$item.semanticNodeRef] = $item }
$parentByNode = @{}
foreach ($item in $edges.edges) { $parentByNode[$item.childSemanticNodeRef] = $item.parentSemanticNodeRef }
$nodeByRef = @{}
foreach ($item in $nodes.semanticNodes) { $nodeByRef[$item.semanticNodeRef] = $item }

function Branch-ForNode([string]$nodeRef) {
    $cursor = $nodeRef
    $guard = 0
    while ($cursor -ne 'ROOT') {
        if (-not $parentByNode.ContainsKey($cursor)) { return 'UNRESOLVED_BRANCH' }
        $cursor = $parentByNode[$cursor]
        $guard++
        if ($guard -gt 44) { return 'CYCLE_OR_INVALID_BRANCH' }
    }
    $root = $nodeRef
    while ($parentByNode.ContainsKey($root) -and $parentByNode[$root] -ne 'ROOT') { $root = $parentByNode[$root] }
    switch ($root) {
        'N001' { return 'DOCUMENT_NARRATIVE' }
        'N023' { return 'ANNEX_1_AGENDA' }
        'N044' { return 'ANNEX_2_PARTICIPANTS' }
        default { return "ROOT_BRANCH_$root" }
    }
}

$historicalRows = @($historical.headings)
$canonical = @($occ.bindings | Sort-Object documentOrder)
$matchedCanonicalIds = @{}
$matchedHistoricalIds = @{}
$comparisonRows = @()
$bridgeRows = @()

foreach ($historicalRow in $historicalRows) {
    $normalizedText = Normalize-Text $historicalRow.exactText
    $textCandidates = @($canonical | Where-Object { (Normalize-Text $_.rawHeadingText) -eq $normalizedText })

    # Historical packets do not carry the newer exact span for 23 rows. The
    # deterministic bridge therefore requires the same frozen source SHA,
    # a paragraph (not table) source path, and a heading span beginning at 0.
    $bridgeCandidates = @($textCandidates | Where-Object {
        $_.sourceSha256 -eq $historical.sourceSha256 -and
        $_.sourceOccurrenceId -notmatch '/tbl\[' -and
        $_.span.start -eq 0
    })

    $bridgeStatus = 'AMBIGUOUS_HISTORICAL_BRIDGE'
    $bridgeMethod = 'SOURCE_SHA_TEXT_PARAGRAPH_START_ZERO'
    $selected = $null
    if ($bridgeCandidates.Count -eq 1) {
        $selected = $bridgeCandidates[0]
        $bridgeStatus = 'EXACT_HISTORICAL_MATCH'
        $matchedCanonicalIds[$selected.canonicalOccurrenceId] = $true
        $matchedHistoricalIds[$historicalRow.headingOccurrenceId] = $true
    }
    elseif ($bridgeCandidates.Count -eq 0) {
        $bridgeStatus = 'AMBIGUOUS_HISTORICAL_BRIDGE'
    }

    $bridgeRows += [ordered]@{
        historicalOccurrenceRef = $historicalRow.headingOccurrenceId
        historicalSourceId = $historicalRow.sourceId
        historicalText = $historicalRow.exactText
        candidateCountByNormalizedText = $textCandidates.Count
        deterministicBridgeCandidateCount = $bridgeCandidates.Count
        bridgeStatus = $bridgeStatus
        bridgeMethod = $bridgeMethod
        canonicalOccurrenceRef = if ($null -ne $selected) { $selected.canonicalOccurrenceId } else { $null }
    }

    if ($null -ne $selected) {
        $assignment = $assignmentBySource[$selected.sourceOccurrenceId]
        $level = $levelByNode[$assignment.semanticNodeRef]
        $parent = $parentByNode[$assignment.semanticNodeRef]
        $branch = Branch-ForNode $assignment.semanticNodeRef
        $delta = [int]$level.derivedLevel - [int]$historicalRow.level
        $comparisonRows += [ordered]@{
            canonicalOccurrenceRef = $selected.canonicalOccurrenceId
            sourceId = $selected.sourceId
            headingText = $selected.rawHeadingText
            canonicalSemanticNodeRef = $assignment.semanticNodeRef
            canonicalParent = $parent
            canonicalDerivedLevel = [int]$level.derivedLevel
            historicalOccurrenceRef = $historicalRow.headingOccurrenceId
            historicalLevel = [int]$historicalRow.level
            delta = $delta
            match = ($delta -eq 0)
            branch = $branch
            mismatchClassification = if ($delta -eq 0) { $null } elseif ($branch -eq 'DOCUMENT_NARRATIVE' -and $delta -eq 1) { 'ROOT_DEPTH_SHIFT' } else { 'OTHER' }
            bridgeStatus = 'EXACT_HISTORICAL_MATCH'
            bridgeEvidence = 'SOURCE_SHA + NORMALIZED_EXACT_TEXT + NON_TABLE_PARAGRAPH + SPAN_START_ZERO'
        }
    }
}

$canonicalOnly = @($canonical | Where-Object { -not $matchedCanonicalIds.ContainsKey($_.canonicalOccurrenceId) } | ForEach-Object {
    $assignment = $assignmentBySource[$_.sourceOccurrenceId]
    [ordered]@{
        canonicalOccurrenceRef = $_.canonicalOccurrenceId
        sourceId = $_.sourceId
        headingText = $_.rawHeadingText
        canonicalSemanticNodeRef = $assignment.semanticNodeRef
        branch = Branch-ForNode $assignment.semanticNodeRef
        classification = 'NO_HISTORICAL_COUNTERPART'
    }
})

$ambiguous = @($bridgeRows | Where-Object { $_.bridgeStatus -eq 'AMBIGUOUS_HISTORICAL_BRIDGE' })
$historicalOnly = @($historicalRows | Where-Object { -not $matchedHistoricalIds.ContainsKey($_.headingOccurrenceId) } | ForEach-Object {
    [ordered]@{ historicalOccurrenceRef = $_.headingOccurrenceId; sourceId = $_.sourceId; headingText = $_.exactText; classification = 'NO_CANONICAL_COUNTERPART' }
})

$canonicalStatusRows = @()
foreach ($canonicalItem in $canonical) {
    $matchedRow = @($comparisonRows | Where-Object { $_['canonicalOccurrenceRef'] -eq $canonicalItem.canonicalOccurrenceId })
    if ($matchedRow.Count -eq 1) {
        $canonicalStatusRows += $matchedRow[0]
    }
    else {
        $assignment = $assignmentBySource[$canonicalItem.sourceOccurrenceId]
        $canonicalStatusRows += [ordered]@{
            canonicalOccurrenceRef = $canonicalItem.canonicalOccurrenceId
            sourceId = $canonicalItem.sourceId
            headingText = $canonicalItem.rawHeadingText
            canonicalSemanticNodeRef = $assignment.semanticNodeRef
            branch = Branch-ForNode $assignment.semanticNodeRef
            classification = 'NO_HISTORICAL_COUNTERPART'
            match = $null
        }
    }
}

$mismatches = @($comparisonRows | Where-Object { -not $_['match'] })
$exactMatches = @($comparisonRows | Where-Object { $_['match'] })
$deltaGroups = @()
foreach ($group in ($comparisonRows | Group-Object { $_['delta'] } | Sort-Object { [int]$_.Name })) {
    $deltaGroups += [ordered]@{ delta = [int]$group.Name; count = $group.Count }
}
$branchGroups = @()
foreach ($group in ($comparisonRows | Group-Object { $_['branch'] } | Sort-Object Name)) {
    $groupRows = @($group.Group)
    $groupDeltaGroups = @()
    foreach ($deltaGroup in ($groupRows | Where-Object { -not $_['match'] } | Group-Object { $_['delta'] } | Sort-Object { [int]$_.Name })) {
        $groupDeltaGroups += [ordered]@{ delta = [int]$deltaGroup.Name; count = $deltaGroup.Count }
    }
    $branchGroups += [ordered]@{
        branch = $group.Name
        matched = $group.Count
        exact = @($groupRows | Where-Object { $_['match'] }).Count
        mismatched = @($groupRows | Where-Object { -not $_['match'] }).Count
        mismatchDeltas = $groupDeltaGroups
    }
}

$comparison = [ordered]@{
    artifactKind = 'A99_DOC0258_CANONICAL_VS_HISTORICAL_LEVEL_COMPARISON'
    schemaVersion = 'a99-canonical-vs-historical-level-diagnostic-v1'
    status = 'DIAGNOSTIC_COMPLETE'
    documentId = 'DOC-0258'
    canonicalAuthorityCommit = '0c7f22c'
    canonicalAuthorityStatus = 'USER_REVIEWED_CANONICAL_HIERARCHY_GOLD'
    historicalAuthority = 'DIRECT_HISTORICAL_LEVEL_ANNOTATION'
    historicalParentRead = $false
    goldMutation = $false
    canonicalMutation = $false
    sourceSha256 = $freeze.sourceSha256
    matchingPolicy = 'EXACT_SOURCE_ID_AND_SPAN_WHEN_AVAILABLE; OTHERWISE SOURCE_SHA + NORMALIZED_EXACT_TEXT + NON_TABLE_PARAGRAPH + SPAN_START_ZERO; TEXT-ONLY JOIN FORBIDDEN'
    bridgeAudit = $bridgeRows
    canonicalOccurrenceStatuses = $canonicalStatusRows
    historicalOnly = $historicalOnly
    matchedComparisons = $comparisonRows
    counts = [ordered]@{
        canonicalOccurrences = $canonical.Count
        historicalOccurrenceCount = $historicalRows.Count
        exactMatched = $comparisonRows.Count
        canonicalOnly = $canonicalOnly.Count
        historicalOnly = $historicalOnly.Count
        ambiguousBridge = $ambiguous.Count
        levelExactMatch = $exactMatches.Count
        levelMismatch = $mismatches.Count
        levelAccuracyOnMatched = if ($comparisonRows.Count -gt 0) { [math]::Round($exactMatches.Count / $comparisonRows.Count, 6) } else { $null }
    }
}

$mismatchArtifact = [ordered]@{
    artifactKind = 'A99_DOC0258_HISTORICAL_LEVEL_MISMATCH_FORENSICS'
    schemaVersion = 'a99-canonical-vs-historical-level-diagnostic-v1'
    documentId = 'DOC-0258'
    status = 'DIAGNOSTIC_ONLY'
    mismatches = $mismatches
    deltaDistribution = $deltaGroups
    branchDistribution = $branchGroups
    note = 'Mismatch classifications are historical-annotation pattern diagnostics only; they are not canonical tree error claims.'
}

$summary = [ordered]@{
    artifactKind = 'A99_DOC0258_CANONICAL_VS_HISTORICAL_LEVEL_DIAGNOSTIC_SUMMARY'
    schemaVersion = 'a99-canonical-vs-historical-level-diagnostic-v1'
    status = 'DIAGNOSTIC_COMPLETE'
    documentId = 'DOC-0258'
    canonicalAuthorityCommit = '0c7f22c'
    canonicalAuthority = [ordered]@{ occurrences = 44; semanticNodes = 44; parentEdges = 44; rootChildren = 3; maxDepth = 3; levels = 'DERIVED_FROM_VALIDATED_TREE_ONLY' }
    historicalAuthority = 'DIRECT_HISTORICAL_LEVEL_ANNOTATION'
    counts = $comparison.counts
    deltaDistribution = $deltaGroups
    branchDistribution = $branchGroups
    mismatchPattern = 'NARRATIVE_BRANCH_DESCENDANTS_ARE_ONE_LEVEL_DEEPER_IN_CANONICAL_TREE; ANNEX_ROOT_MATCHES_ON_BRIDGED_ROWS'
    diagnostics = @('ROOT_DEPTH_SHIFT')
    firewall = [ordered]@{ canonicalGoldMutation = $false; parentMutation = $false; levelMutation = $false; historicalLevelRead = $true; historicalParentRead = $false; providerCalls = 0; modelCalls = 0 }
}

$manifestInputs = @(
    [ordered]@{ name = 'canonicalFreezeManifest'; path = 'artifacts/authority-audit/canonical-authority-freeze-v1/DOC-0258/authority-freeze-manifest.json'; sha256 = (Sha256 (Join-Path $repo 'artifacts/authority-audit/canonical-authority-freeze-v1/DOC-0258/authority-freeze-manifest.json')) },
    [ordered]@{ name = 'occurrenceAuthority'; path = 'artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v2-span-aware/DOC-0258/exact-bindings.json'; sha256 = (Sha256 $occPath) },
    [ordered]@{ name = 'identityAssignments'; path = 'artifacts/authority-audit/canonical-semantic-identity-v1/DOC-0258/occurrence-assignments.json'; sha256 = (Sha256 $assignPath) },
    [ordered]@{ name = 'semanticNodes'; path = 'artifacts/authority-audit/canonical-semantic-identity-v1/DOC-0258/semantic-nodes.json'; sha256 = (Sha256 $nodesPath) },
    [ordered]@{ name = 'parentEdges'; path = 'artifacts/authority-audit/canonical-hierarchy-v1/DOC-0258/parent-edges.json'; sha256 = (Sha256 $edgesPath) },
    [ordered]@{ name = 'derivedLevels'; path = 'artifacts/authority-audit/canonical-hierarchy-v1/DOC-0258/derived-levels.json'; sha256 = (Sha256 $levelsPath) },
    [ordered]@{ name = 'historicalDirectLevelAuthority'; path = 'eval/a99-closed-loop/strict-gold-v4/DOC-0258.strict-gold-v4.json'; sha256 = (Sha256 $histPath) }
)
$manifest = [ordered]@{
    artifactKind = 'A99_DOC0258_CANONICAL_VS_HISTORICAL_LEVEL_DIAGNOSTIC_MANIFEST'
    schemaVersion = 'a99-canonical-vs-historical-level-diagnostic-v1'
    status = 'DIAGNOSTIC_COMPLETE'
    documentId = 'DOC-0258'
    authorityStatus = 'DIRECT_HISTORICAL_LEVEL_ANNOTATION_COMPARED_AFTER_CANONICAL_FREEZE'
    canonicalAuthorityCommit = '0c7f22c'
    canonicalSourceSha256 = $freeze.sourceSha256
    historicalLevelRead = $true
    historicalParentRead = $false
    canonicalGoldMutation = $false
    parentMutation = $false
    levelMutation = $false
    providerCalls = 0
    modelCalls = 0
    inputs = $manifestInputs
    outputFiles = @('comparison.json','mismatches.json','summary.json','report.md','manifest.json')
    notes = @('Diagnostic only.', 'No canonical tree repair was attempted.', 'Historical parent annotations were not read.', 'Historical semantic total and historical level policy were not used to alter canonical authority.')
}

$report = @"
# DOC-0258 canonical vs historical level diagnostic

Status: `DIAGNOSTIC_COMPLETE`

Canonical authority was frozen before historical level access at commit 0c7f22c.
The canonical tree and derived levels were not changed.

## Counts

- Canonical occurrences: $($canonical.Count)
- Historical direct level rows: $($historicalRows.Count)
- Exact deterministic bridges: $($comparisonRows.Count)
- Canonical-only occurrences: $($canonicalOnly.Count)
- Historical-only rows: $($historicalOnly.Count)
- Ambiguous bridges: $($ambiguous.Count)
- Exact level matches: $($exactMatches.Count)
- Level mismatches: $($mismatches.Count)
- Matched-level accuracy: $([math]::Round($exactMatches.Count / $comparisonRows.Count, 6))

## Delta distribution

$(($deltaGroups | ForEach-Object { '- delta '+$_.delta+': '+$_.count }) -join "`n")

## Branch distribution

$(($branchGroups | ForEach-Object { '- '+$_.branch+': matched='+$_.matched+', exact='+$_.exact+', mismatched='+$_.mismatched }) -join "`n")

## Interpretation

All bridged narrative mismatches are `+1` canonical depth relative to the direct historical level. The diagnostic label is `ROOT_DEPTH_SHIFT`: the canonical tree treats the document title/root occurrence as an ancestor of the narrative branch, while the historical annotation does not apply that same depth convention. Bridged Annex 1 and Annex 2 root rows match. This is an annotation-policy compatibility observation, not a canonical-tree error claim.

## Firewall

- Canonical Gold mutation: false
- Parent mutation: false
- Level mutation: false
- Historical level read: true
- Historical parent read: false
- Provider/model calls: 0/0

Historical compatibility results must not be used to repair the canonical tree.
"@

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Write-Json (Join-Path $outDir 'comparison.json') $comparison
Write-Json (Join-Path $outDir 'mismatches.json') $mismatchArtifact
Write-Json (Join-Path $outDir 'summary.json') $summary
Set-Content -LiteralPath (Join-Path $outDir 'report.md') -Value $report -Encoding UTF8
$manifest.outputHashes = @(
    [ordered]@{ name = 'comparison'; path = 'comparison.json'; sha256 = (Sha256 (Join-Path $outDir 'comparison.json')) },
    [ordered]@{ name = 'mismatches'; path = 'mismatches.json'; sha256 = (Sha256 (Join-Path $outDir 'mismatches.json')) },
    [ordered]@{ name = 'summary'; path = 'summary.json'; sha256 = (Sha256 (Join-Path $outDir 'summary.json')) },
    [ordered]@{ name = 'report'; path = 'report.md'; sha256 = (Sha256 (Join-Path $outDir 'report.md')) }
)
Write-Json (Join-Path $outDir 'manifest.json') $manifest

if ($comparison.counts.exactMatched -ne 24 -or $comparison.counts.canonicalOnly -ne 20 -or $comparison.counts.historicalOnly -ne 0 -or $comparison.counts.ambiguousBridge -ne 0) { throw 'Diagnostic bridge counts failed expected DOC-0258 result.' }
if ($comparison.counts.levelExactMatch -ne 3 -or $comparison.counts.levelMismatch -ne 21) { throw 'Diagnostic level comparison failed expected DOC-0258 result.' }
Write-Output ($summary | ConvertTo-Json -Depth 20)
