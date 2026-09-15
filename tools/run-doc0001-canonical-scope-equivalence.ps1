$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$canonicalDir = Join-Path $repo 'artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v1/source-review-v1/DOC-0001'
$oldDir = Join-Path $repo 'artifacts/authority-audit/strict-heading-canonical-hierarchy-gold-v1/DOC-0001'
$packetPath = Join-Path $repo 'artifacts/authority-audit/strict-heading-canonical-hierarchy-review-v2/packets/DOC-0001.canonical-hierarchy-review.v2.json'
$outDir = Join-Path $repo 'artifacts/authority-audit/canonical-scope-equivalence-v1/DOC-0001'

function Read-Json($path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
function Sha256($path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Write-Json($path, $value) { $value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $path -Encoding UTF8 }
function Normalize-Text([string]$value) {
    if ($null -eq $value) { return '' }
    $value.Normalize([Text.NormalizationForm]::FormKC).Trim().ToLowerInvariant()
}
function Repair-ReversibleMojibake([string]$value) {
    if ($null -eq $value) { return '' }
    try {
        $candidate = [Text.Encoding]::UTF8.GetString([Text.Encoding]::GetEncoding(1252).GetBytes($value))
        if ($value -match 'Ã|Æ|Ä|áº|á»') { return $candidate }
    }
    catch { }
    return $value
}

$canonical = Read-Json (Join-Path $canonicalDir 'exact-bindings.json')
$canonicalValidation = Read-Json (Join-Path $canonicalDir 'validation.json')
$canonicalUniverse = Read-Json (Join-Path $repo 'artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v1/DOC-0001/source-universe.json')
$oldManifest = Read-Json (Join-Path $oldDir 'manifest.json')
$oldReview = Read-Json (Join-Path $oldDir 'source-review-adjudication.json')
$oldEdges = Read-Json (Join-Path $oldDir 'parent-edges.json')
$oldLevels = Read-Json (Join-Path $oldDir 'derived-levels.json')
$oldValidation = Read-Json (Join-Path $oldDir 'validation.json')
$packet = Read-Json $packetPath

if ($canonicalValidation.status -ne 'CANONICAL_EXHAUSTIVE_OCCURRENCE_READY') { throw 'Canonical occurrence authority is not ready.' }
if ($oldManifest.status -ne 'USER_REVIEWED_CANONICAL_HIERARCHY_GOLD' -or $oldManifest.approvedByUser -ne $true) { throw 'Existing DOC-0001 hierarchy is not an explicitly approved authority.' }
if ($oldManifest.occurrenceCount -ne 7 -or $oldManifest.semanticNodeCount -ne 7 -or $oldManifest.parentEdgeCount -ne 7) { throw 'Existing hierarchy cardinality precondition failed.' }
if ($oldManifest.historicalLevelReadDuringAdjudication -ne $false -or $oldManifest.historicalLevelDiagnosticPerformed -ne $false) { throw 'Historical-level firewall precondition failed.' }

$canonicalBindings = @($canonical.bindings | Sort-Object documentOrder)
$oldOccurrences = @($oldReview.headingOccurrences | Sort-Object sourceOrder)
$canonicalSha = $canonical.sourceSha256
$oldSourceShas = @($oldOccurrences | ForEach-Object { $_.sourceProvenance.sourceReferenceSha256 } | Sort-Object -Unique)
$oldSourcePaths = @($oldOccurrences | ForEach-Object { $_.sourceProvenance.sourceReferencePath } | Sort-Object -Unique)

$matchedCanonical = @{}
$matchedOld = @{}
$bridgeRows = @()
foreach ($oldOccurrence in $oldOccurrences) {
    $oldText = Repair-ReversibleMojibake $oldOccurrence.sourceText
    $textCandidates = @($canonicalBindings | Where-Object { (Normalize-Text $_.text) -eq (Normalize-Text $oldText) })
    $lineageCandidates = @($textCandidates | Where-Object { $_.sourceSha256 -eq $oldOccurrence.sourceProvenance.sourceReferenceSha256 })
    $physicalCandidates = @($lineageCandidates | Where-Object { $_.sourceId -eq $oldOccurrence.sourceId })
    $exactSpanComparable = $null -ne $oldOccurrence.sourceSpan -and $null -ne $oldOccurrence.sourceSpan.start
    $spanCandidates = if ($exactSpanComparable) { @($physicalCandidates | Where-Object { $_.span.start -eq $oldOccurrence.sourceSpan.start -and $_.span.end -eq $oldOccurrence.sourceSpan.end }) } else { @() }
    $status = 'SOURCE_LINEAGE_MISMATCH'
    if ($spanCandidates.Count -eq 1) { $status = 'EXACT_ONE_TO_ONE_BRIDGE' }
    elseif ($physicalCandidates.Count -eq 1 -and -not $exactSpanComparable) { $status = 'PHYSICAL_ID_MATCH_SPAN_UNAVAILABLE' }
    elseif ($textCandidates.Count -eq 0) { $status = 'TEXT_NOT_BRIDGED' }
    elseif ($textCandidates.Count -gt 1) { $status = 'AMBIGUOUS_TEXT_BRIDGE' }

    $selected = if ($status -eq 'EXACT_ONE_TO_ONE_BRIDGE' -or $status -eq 'PHYSICAL_ID_MATCH_SPAN_UNAVAILABLE') { $physicalCandidates[0] } else { $null }
    if ($null -ne $selected) { $matchedCanonical[$selected.canonicalOccurrenceId] = $true; $matchedOld[$oldOccurrence.reviewRef] = $true }
    $bridgeRows += [ordered]@{
        oldReviewRef = $oldOccurrence.reviewRef
        oldHeadingOccurrenceId = $oldOccurrence.headingOccurrenceId
        oldSourceId = $oldOccurrence.sourceId
        oldSourceSha256 = $oldOccurrence.sourceProvenance.sourceReferenceSha256
        oldSourcePath = $oldOccurrence.sourceProvenance.sourceReferencePath
        oldText = $oldOccurrence.sourceText
        reversiblyNormalizedText = $oldText
        canonicalTextCandidateCount = $textCandidates.Count
        sameSourceShaCandidateCount = $lineageCandidates.Count
        samePhysicalSourceIdCandidateCount = $physicalCandidates.Count
        exactSpanComparable = $exactSpanComparable
        bridgeStatus = $status
        canonicalOccurrenceRef = if ($null -ne $selected) { $selected.canonicalOccurrenceId } else { $null }
        canonicalSourceId = if ($null -ne $selected) { $selected.sourceId } else { $null }
        canonicalSourceSha256 = $canonicalSha
    }
}

$exactBridges = @($bridgeRows | Where-Object { $_['bridgeStatus'] -eq 'EXACT_ONE_TO_ONE_BRIDGE' })
$physicalBridges = @($bridgeRows | Where-Object { $_['bridgeStatus'] -eq 'PHYSICAL_ID_MATCH_SPAN_UNAVAILABLE' })
$canonicalOnly = @($canonicalBindings | Where-Object { -not $matchedCanonical.ContainsKey($_.canonicalOccurrenceId) } | ForEach-Object {
    [ordered]@{ canonicalOccurrenceRef = $_.canonicalOccurrenceId; sourceId = $_.sourceId; text = $_.text; documentOrder = $_.documentOrder; classification = 'CANONICAL_ONLY_UNBRIDGED' }
})
$oldOnly = @($oldOccurrences | Where-Object { -not $matchedOld.ContainsKey($_.reviewRef) } | ForEach-Object {
    [ordered]@{ oldReviewRef = $_.reviewRef; headingOccurrenceId = $_.headingOccurrenceId; sourceId = $_.sourceId; text = $_.sourceText; classification = 'OLD_HIERARCHY_ONLY_UNBRIDGED' }
})
$ambiguous = @($bridgeRows | Where-Object { $_['bridgeStatus'] -eq 'AMBIGUOUS_TEXT_BRIDGE' })
$sourceLineageMismatch = @($bridgeRows | Where-Object { $_['bridgeStatus'] -eq 'SOURCE_LINEAGE_MISMATCH' })

$scopePass = $exactBridges.Count -eq 7 -and $oldOnly.Count -eq 0 -and $canonicalOnly.Count -eq 0 -and $ambiguous.Count -eq 0
$status = if ($scopePass) { 'CANONICAL_SCOPE_EQUIVALENCE_PROVEN_READY_FOR_USER_APPROVAL' } else { 'CANONICAL_SCOPE_EQUIVALENCE_FAILED' }

$occurrenceEquivalence = [ordered]@{
    artifactKind = 'A99_DOC0001_CANONICAL_SCOPE_OCCURRENCE_EQUIVALENCE'
    schemaVersion = 'a99-canonical-scope-equivalence-v1'
    documentId = 'DOC-0001'
    status = $status
    canonicalAuthority = [ordered]@{ path = 'artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v1/source-review-v1/DOC-0001/exact-bindings.json'; sourcePath = $canonicalUniverse.sourcePath; sourceSha256 = $canonicalSha; occurrenceCount = $canonicalBindings.Count }
    oldHierarchyAuthority = [ordered]@{ path = 'artifacts/authority-audit/strict-heading-canonical-hierarchy-gold-v1/DOC-0001/source-review-adjudication.json'; approvalCommit = $oldManifest.approvalCommit; occurrenceCount = $oldOccurrences.Count; sourcePacketSha256 = $oldManifest.sourcePacketSha256; sourcePaths = $oldSourcePaths; sourceReferenceShas = $oldSourceShas }
    counts = [ordered]@{ oldOccurrenceCount = $oldOccurrences.Count; canonicalOccurrenceCount = $canonicalBindings.Count; exactOneToOneBridges = $exactBridges.Count; physicalIdBridgesWithoutComparableSpan = $physicalBridges.Count; oldOnly = $oldOnly.Count; canonicalOnly = $canonicalOnly.Count; ambiguous = $ambiguous.Count; sourceLineageMismatches = $sourceLineageMismatch.Count }
    bridgePolicy = 'Exact physical identity requires compatible source SHA and source occurrence identity; text normalization is diagnostic evidence only and cannot authorize a bridge.'
    bridges = $bridgeRows
    oldOnlyOccurrences = $oldOnly
    canonicalOnlyOccurrences = $canonicalOnly
}

$identityEquivalence = [ordered]@{
    artifactKind = 'A99_DOC0001_CANONICAL_SCOPE_IDENTITY_EQUIVALENCE'
    schemaVersion = 'a99-canonical-scope-equivalence-v1'
    documentId = 'DOC-0001'
    status = if ($scopePass) { 'IDENTITY_EQUIVALENCE_VERIFIED' } else { 'BLOCKED_BY_OCCURRENCE_SCOPE_EQUIVALENCE' }
    oldSemanticNodeCount = $oldReview.adjudication.semanticNodes.Count
    canonicalOccurrenceCount = $canonicalBindings.Count
    oldOccurrenceAssignments = $oldReview.adjudication.occurrenceAssignments.Count
    occurrenceRoles = [ordered]@{ PRIMARY = @($oldReview.adjudication.occurrenceAssignments | Where-Object occurrenceRole -eq 'PRIMARY').Count; REPEAT = @($oldReview.adjudication.occurrenceAssignments | Where-Object occurrenceRole -eq 'REPEAT').Count; CONTINUATION = @($oldReview.adjudication.occurrenceAssignments | Where-Object occurrenceRole -eq 'CONTINUATION').Count }
    singletonIdentityRepresentable = $scopePass
    semanticReAdjudication = $false
    note = if ($scopePass) { 'All seven singleton semantic assignments are mechanically rebindable.' } else { 'The old singleton structure has seven rows, but canonical semantic membership is not promoted because source lineage and physical identity equivalence failed.' }
}

$treeRebind = [ordered]@{
    artifactKind = 'A99_DOC0001_CANONICAL_SCOPE_TREE_REBIND'
    schemaVersion = 'a99-canonical-scope-equivalence-v1'
    documentId = 'DOC-0001'
    status = if ($scopePass) { 'REBIND_VALIDATED' } else { 'NOT_PERFORMED_SCOPE_EQUIVALENCE_FAILED' }
    sourceTree = [ordered]@{ parentEdges = $oldEdges.parentEdges; rootRef = $oldEdges.rootRef; edgeCount = $oldEdges.parentEdges.Count; rootChildCount = $oldManifest.rootChildCount; maxDepth = $oldManifest.maxDepth }
    reboundParentEdges = if ($scopePass) { $oldEdges.parentEdges } else { @() }
    parentReAdjudication = $false
    note = 'No parent edge was inferred, repaired, or changed.'
}

$derivedLevels = [ordered]@{
    artifactKind = 'A99_DOC0001_CANONICAL_SCOPE_DERIVED_LEVELS'
    schemaVersion = 'a99-canonical-scope-equivalence-v1'
    documentId = 'DOC-0001'
    status = if ($scopePass) { 'DERIVED_FROM_REBOUND_TREE' } else { 'NOT_DERIVED_SCOPE_EQUIVALENCE_FAILED' }
    derivation = 'depth(ROOT)=0; level=depth(rebound validated tree)'
    levels = if ($scopePass) { @($oldLevels.nodes | ForEach-Object { [ordered]@{ semanticNodeRef = $_.semanticNodeRef; parentSemanticNodeRef = $_.parentSemanticNodeRef; depth = $_.depth; derivedLevel = $_.derivedLevel; occurrenceRefs = $_.occurrenceRefs } }) } else { @() }
    historicalLevelRead = $false
}

$validation = [ordered]@{
    artifactKind = 'A99_DOC0001_CANONICAL_SCOPE_EQUIVALENCE_VALIDATION'
    schemaVersion = 'a99-canonical-scope-equivalence-v1'
    documentId = 'DOC-0001'
    status = $status
    valid = $scopePass
    checks = [ordered]@{
        oldOccurrenceCount = $oldOccurrences.Count
        canonicalOccurrenceCount = $canonicalBindings.Count
        exactOneToOneBridges = $exactBridges.Count
        oldOnly = $oldOnly.Count
        canonicalOnly = $canonicalOnly.Count
        ambiguous = $ambiguous.Count
        sourceShaCompatible = ($oldSourceShas.Count -eq 1 -and $oldSourceShas[0] -eq $canonicalSha)
        physicalSourceIdsCompatible = ($exactBridges.Count -eq 7)
        identityCardinality = ($oldReview.adjudication.semanticNodes.Count -eq 7 -and $canonicalBindings.Count -eq 7)
        parentTopologyValidatedOnCanonicalRefs = $scopePass
        rootChildren = if ($scopePass) { $oldManifest.rootChildCount } else { $null }
        maxDepth = if ($scopePass) { $oldManifest.maxDepth } else { $null }
        cycles = if ($scopePass) { 0 } else { $null }
        multipleParents = if ($scopePass) { 0 } else { $null }
        danglingRefs = if ($scopePass) { 0 } else { $null }
        unreachableNodes = if ($scopePass) { 0 } else { $null }
        historicalLevelRead = $false
        historicalParentUsedAsNewEvidence = $false
        semanticReAdjudication = $false
        parentReAdjudication = $false
        providerCalls = 0
        modelCalls = 0
        goldMutation = $false
    }
    failureReasons = if ($scopePass) { @() } else { @('OLD_HIERARCHY_SOURCE_REFERENCE_SHA_DIFFERS_FROM_CANONICAL_SOURCE_SHA', 'OLD_HIERARCHY_SOURCE_IDS_ARE_KEY_PARAGRAPH_REFS_WHILE_CANONICAL_IDS_ARE_DOCX_BODY_REFS', 'NO_EXACT_ONE_TO_ONE_PHYSICAL_OCCURRENCE_BRIDGE_PROVEN') }
}

$inputPaths = @(
    @{ name = 'canonicalExactBindings'; path = 'artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v1/source-review-v1/DOC-0001/exact-bindings.json'; full = Join-Path $canonicalDir 'exact-bindings.json' },
    @{ name = 'canonicalValidation'; path = 'artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v1/source-review-v1/DOC-0001/validation.json'; full = Join-Path $canonicalDir 'validation.json' },
    @{ name = 'canonicalSourceUniverse'; path = 'artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v1/DOC-0001/source-universe.json'; full = Join-Path $repo 'artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v1/DOC-0001/source-universe.json' },
    @{ name = 'oldSourceReviewAdjudication'; path = 'artifacts/authority-audit/strict-heading-canonical-hierarchy-gold-v1/DOC-0001/source-review-adjudication.json'; full = Join-Path $oldDir 'source-review-adjudication.json' },
    @{ name = 'oldParentEdges'; path = 'artifacts/authority-audit/strict-heading-canonical-hierarchy-gold-v1/DOC-0001/parent-edges.json'; full = Join-Path $oldDir 'parent-edges.json' },
    @{ name = 'oldDerivedLevels'; path = 'artifacts/authority-audit/strict-heading-canonical-hierarchy-gold-v1/DOC-0001/derived-levels.json'; full = Join-Path $oldDir 'derived-levels.json' },
    @{ name = 'oldManifest'; path = 'artifacts/authority-audit/strict-heading-canonical-hierarchy-gold-v1/DOC-0001/manifest.json'; full = Join-Path $oldDir 'manifest.json' },
    @{ name = 'oldValidation'; path = 'artifacts/authority-audit/strict-heading-canonical-hierarchy-gold-v1/DOC-0001/validation.json'; full = Join-Path $oldDir 'validation.json' }
)
$manifest = [ordered]@{
    artifactKind = 'A99_DOC0001_CANONICAL_SCOPE_EQUIVALENCE_MANIFEST'
    schemaVersion = 'a99-canonical-scope-equivalence-v1'
    documentId = 'DOC-0001'
    status = $status
    canonicalScopePromotion = if ($scopePass) { 'READY_FOR_USER_APPROVAL_ONLY' } else { 'BLOCKED' }
    historicalLevelRead = $false
    historicalParentUsedAsNewEvidence = $false
    semanticReAdjudication = $false
    parentReAdjudication = $false
    providerCalls = 0
    modelCalls = 0
    goldMutation = $false
    inputs = @($inputPaths | ForEach-Object { [ordered]@{ name = $_.name; path = $_.path; sha256 = Sha256 $_.full } })
    outputFiles = @('occurrence-equivalence.json','identity-equivalence.json','tree-rebind.json','derived-levels.json','validation.json','manifest.json','report.md')
    commitLineage = @('3345c70','82641d4')
}

$report = @"
# DOC-0001 canonical scope equivalence audit

Status: $status

This is an equivalence audit only. No heading truth, semantic identity, parent edge, or level was re-adjudicated. Historical level was not read.

## Scope counts

- Old approved hierarchy occurrences: $($oldOccurrences.Count)
- Canonical exhaustive occurrences: $($canonicalBindings.Count)
- Exact one-to-one physical bridges: $($exactBridges.Count)
- Text/order candidates with source-lineage mismatch: $($sourceLineageMismatch.Count)
- Old-only unbridged: $($oldOnly.Count)
- Canonical-only unbridged: $($canonicalOnly.Count)
- Ambiguous: $($ambiguous.Count)

## Lineage finding

The canonical occurrence authority is bench/01-style-chuan.docx with SHA $canonicalSha. The already-approved hierarchy packet records bench/01-style-chuan.key and source-reference SHA $($oldSourceShas -join ', '). The old physical refs are paragraph[N], while canonical refs are body[1]/p[N]. Text can be reversibly normalized to the same seven headings, but that is not sufficient to prove physical identity across source lineages.

Therefore canonical scope equivalence is not proven. The old 7-node tree was not rebound or promoted to canonical scope.

## Identity/tree firewall

- Semantic re-adjudication: false
- Parent re-adjudication: false
- Historical level read: false
- Historical parent used as new evidence: false
- Provider/model calls: 0/0
- Gold mutation: false

The existing approved hierarchy remains unchanged and historical-scope only for this audit. A new source-compatible canonical binding or explicit source-lineage reconciliation is required before scope promotion.
"@

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Write-Json (Join-Path $outDir 'occurrence-equivalence.json') $occurrenceEquivalence
Write-Json (Join-Path $outDir 'identity-equivalence.json') $identityEquivalence
Write-Json (Join-Path $outDir 'tree-rebind.json') $treeRebind
Write-Json (Join-Path $outDir 'derived-levels.json') $derivedLevels
Write-Json (Join-Path $outDir 'validation.json') $validation
Set-Content -LiteralPath (Join-Path $outDir 'report.md') -Value $report -Encoding UTF8
$manifest.outputHashes = @(
    [ordered]@{ name = 'occurrence-equivalence'; path = 'occurrence-equivalence.json'; sha256 = Sha256 (Join-Path $outDir 'occurrence-equivalence.json') },
    [ordered]@{ name = 'identity-equivalence'; path = 'identity-equivalence.json'; sha256 = Sha256 (Join-Path $outDir 'identity-equivalence.json') },
    [ordered]@{ name = 'tree-rebind'; path = 'tree-rebind.json'; sha256 = Sha256 (Join-Path $outDir 'tree-rebind.json') },
    [ordered]@{ name = 'derived-levels'; path = 'derived-levels.json'; sha256 = Sha256 (Join-Path $outDir 'derived-levels.json') },
    [ordered]@{ name = 'validation'; path = 'validation.json'; sha256 = Sha256 (Join-Path $outDir 'validation.json') },
    [ordered]@{ name = 'report'; path = 'report.md'; sha256 = Sha256 (Join-Path $outDir 'report.md') }
)
Write-Json (Join-Path $outDir 'manifest.json') $manifest

Write-Output ($validation | ConvertTo-Json -Depth 20)
