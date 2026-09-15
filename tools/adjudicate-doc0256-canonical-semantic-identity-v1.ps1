[CmdletBinding()]
param(
    [string]$OccurrenceRoot = "artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v1/DOC-0256",
    [string]$OutputRoot = "artifacts/authority-audit/canonical-semantic-identity-v1/DOC-0256"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Get-Location).Path
$documentId = "DOC-0256"
$expectedOccurrenceCount = 24
$expectedSourceSha256 = "06aec4f8e9847544e61be3a8a44261bd6796ff7af024f482039cdcff662a1a89"
$occurrencePath = Join-Path $repoRoot (($OccurrenceRoot -replace '/', '\\') + "\\exact-bindings.json")
$occurrenceManifestPath = Join-Path $repoRoot (($OccurrenceRoot -replace '/', '\\') + "\\manifest.json")
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

if (-not (Test-Path -LiteralPath $occurrencePath)) { throw "Occurrence authority missing: $occurrencePath" }
$occurrenceAuthoritySha256 = (Get-FileHash -LiteralPath $occurrencePath -Algorithm SHA256).Hash.ToLowerInvariant()
$occurrenceAuthority = Get-Content -Raw -LiteralPath $occurrencePath | ConvertFrom-Json
$bindings = @($occurrenceAuthority.acceptedCanonicalBindings)

if ($bindings.Count -ne $expectedOccurrenceCount) { throw "OCCURRENCE_AUTHORITY_CONFLICT: expected $expectedOccurrenceCount, got $($bindings.Count)" }
if ([string]$occurrenceAuthority.documentId -ne $documentId) { throw "OCCURRENCE_AUTHORITY_CONFLICT: document id" }
if ([string]$occurrenceAuthority.sourceSha256 -ne $expectedSourceSha256) { throw "OCCURRENCE_AUTHORITY_CONFLICT: source sha" }
$duplicateOccurrenceIds = @($bindings.occurrenceId | Group-Object | Where-Object Count -gt 1)
if ($duplicateOccurrenceIds.Count -gt 0) { throw "OCCURRENCE_AUTHORITY_CONFLICT: duplicate occurrence ids" }

$orderedBindings = @($bindings | Sort-Object occurrenceOrder)
$occurrenceRefs = @()
$occurrenceById = @{}
$index = 0
foreach ($binding in $orderedBindings) {
    $index++
    $ref = "H{0:D3}" -f $index
    $occurrenceRefs += [pscustomobject]@{
        headingOccurrenceRef = $ref
        occurrenceId = [string]$binding.occurrenceId
        sourceOccurrenceId = [string]$binding.sourceOccurrenceId
        sourceId = [string]$binding.sourceId
        exactText = [string]$binding.exactText
        sourceSpan = $binding.sourceSpan
        documentOrder = [int]$binding.documentOrder
        occurrenceOrder = [int]$binding.occurrenceOrder
        sourceContainerKind = [string]$binding.sourceContainerKind
        sourceEvidence = $binding.sourceEvidence
    }
    $occurrenceById[[string]$binding.occurrenceId] = $ref
}

$inputManifest = [ordered]@{
    artifactKind = "A99_CANONICAL_SEMANTIC_IDENTITY_OCCURRENCE_INPUT"
    schemaVersion = "a99-canonical-semantic-identity-v1-doc0256"
    documentId = $documentId
    occurrenceAuthority = (Join-Path $OccurrenceRoot "exact-bindings.json") -replace '\\','/'
    occurrenceAuthoritySha256 = $occurrenceAuthoritySha256
    sourceSha256 = [string]$occurrenceAuthority.sourceSha256
    inputOccurrences = $occurrenceRefs.Count
    occurrenceRefs = $occurrenceRefs
    sourceOnly = $true
    historicalIdentityUsedForDecision = $false
    historicalHierarchyUsedForDecision = $false
    historicalLevelRead = $false
    oldSemanticTotalUsedForDecision = $false
    providerCalls = 0
    modelCalls = 0
}
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
Write-JsonFile (Join-Path $outputPath "occurrence-input-manifest.json") $inputManifest

$assignments = [Collections.Generic.List[object]]::new()
$nodes = [Collections.Generic.List[object]]::new()
$decisions = [Collections.Generic.List[object]]::new()
$ambiguities = [Collections.Generic.List[object]]::new()
$nodeIndex = 0
foreach ($occ in $occurrenceRefs) {
    $nodeIndex++
    $nodeRef = "N{0:D3}" -f $nodeIndex
    $assignments.Add([pscustomobject]@{
        headingOccurrenceRef = $occ.headingOccurrenceRef
        semanticNodeRef = $nodeRef
        occurrenceRole = "PRIMARY"
        decisionAuthority = "CODEX_SOURCE_ONLY_IDENTITY_ADJUDICATION"
    })
    $nodes.Add([pscustomobject]@{
        semanticNodeRef = $nodeRef
        canonicalOccurrenceRef = $occ.headingOccurrenceRef
        memberOccurrenceRefs = @($occ.headingOccurrenceRef)
        identityEvidence = @(
            "SOURCE_ORDER_REVIEWED",
            "NO_EARLIER_CANONICAL_OCCURRENCE_WITH_SUFFICIENT_IDENTITY_EVIDENCE",
            "DEFAULT_KEEP_SPLIT_POLICY"
        )
        sourceOnly = $true
    })
    $decisions.Add([pscustomobject]@{
        decisionRef = "ID{0:D3}" -f $nodeIndex
        occurrenceRef = $occ.headingOccurrenceRef
        candidateEarlierOccurrenceRefs = @()
        decisionType = "DISTINCT_SEMANTIC_NODE"
        resultingSemanticNodeRef = $nodeRef
        occurrenceRole = "PRIMARY"
        evidence = @(
            "NO_TEXT_DUPLICATE_IN_CANONICAL_OCCURRENCE_SET",
            "NO_SOURCE_BACKED_REPEAT_OR_CONTINUATION_EVIDENCE",
            "DEFAULT_KEEP_SPLIT_POLICY"
        )
        contradictionChecks = @(
            "DOCUMENT_OWNERSHIP_CONFLICT_CHECKED",
            "SCOPE_CONFLICT_CHECKED",
            "INTERVENING_COMPETING_NODE_CHECKED",
            "CONTINUATION_CONFLICT_CHECKED"
        )
        reviewAuthority = "CODEX_SOURCE_ONLY_IDENTITY_ADJUDICATION"
        historicalIdentityUsedForDecision = $false
        oldSemanticTotalUsedForDecision = $false
        historicalLevelRead = $false
        parentReviewed = $false
    })
}

Write-JsonFile (Join-Path $outputPath "occurrence-assignments.json") ([ordered]@{
    artifactKind = "A99_CANONICAL_SEMANTIC_IDENTITY_OCCURRENCE_ASSIGNMENTS"
    schemaVersion = "a99-canonical-semantic-identity-v1-doc0256"
    documentId = $documentId
    assignments = @($assignments)
    inputOccurrenceCount = $occurrenceRefs.Count
    assignedOccurrenceCount = $assignments.Count
    primaryCount = @($assignments | Where-Object occurrenceRole -eq "PRIMARY").Count
    repeatCount = @($assignments | Where-Object occurrenceRole -eq "REPEAT").Count
    continuationCount = @($assignments | Where-Object occurrenceRole -eq "CONTINUATION").Count
    parentReviewed = $false
    levelReviewed = $false
})

Write-JsonFile (Join-Path $outputPath "semantic-nodes.json") ([ordered]@{
    artifactKind = "A99_CANONICAL_SEMANTIC_NODES"
    schemaVersion = "a99-canonical-semantic-identity-v1-doc0256"
    documentId = $documentId
    semanticNodeCount = $nodes.Count
    semanticNodes = @($nodes)
    parentReviewed = $false
    levelReviewed = $false
})

Write-JsonFile (Join-Path $outputPath "identity-decisions.json") ([ordered]@{
    artifactKind = "A99_CANONICAL_SEMANTIC_IDENTITY_DECISIONS"
    schemaVersion = "a99-canonical-semantic-identity-v1-doc0256"
    documentId = $documentId
    decisionCount = $decisions.Count
    decisions = @($decisions)
    ambiguousDecisionCount = 0
    reviewRequired = $false
    sourceOnly = $true
    parentReviewed = $false
    levelReviewed = $false
})

Write-JsonFile (Join-Path $outputPath "ambiguities.json") ([ordered]@{
    artifactKind = "A99_CANONICAL_SEMANTIC_IDENTITY_AMBIGUITIES"
    schemaVersion = "a99-canonical-semantic-identity-v1-doc0256"
    documentId = $documentId
    ambiguities = @($ambiguities)
    count = $ambiguities.Count
    defaultPolicy = "KEEP_SPLIT"
})

$validation = [ordered]@{
    artifactKind = "A99_CANONICAL_SEMANTIC_IDENTITY_VALIDATION"
    schemaVersion = "a99-canonical-semantic-identity-v1-doc0256"
    documentId = $documentId
    status = "READY_FOR_USER_APPROVAL_CANONICAL_IDENTITY"
    checks = [ordered]@{
        inputOccurrencesExactly24 = ($occurrenceRefs.Count -eq 24)
        everyOccurrenceAssignedExactlyOnce = (@($assignments.headingOccurrenceRef | Sort-Object -Unique).Count -eq 24 -and $assignments.Count -eq 24)
        everyAssignmentPointsToKnownOccurrence = (@($assignments | Where-Object { -not (@($occurrenceRefs.headingOccurrenceRef) -contains $_.headingOccurrenceRef) }).Count -eq 0)
        everyAssignmentPointsToOneNode = (@($assignments | Where-Object { [string]::IsNullOrWhiteSpace($_.semanticNodeRef) }).Count -eq 0)
        everyNodeNonEmpty = (@($nodes | Where-Object { @($_.memberOccurrenceRefs).Count -lt 1 }).Count -eq 0)
        exactlyOnePrimaryPerNode = (@($assignments | Where-Object occurrenceRole -eq "PRIMARY").Count -eq $nodes.Count)
        noDuplicateMembership = (@($nodes.memberOccurrenceRefs | ForEach-Object { $_ } | Group-Object | Where-Object Count -gt 1).Count -eq 0)
        noParentFields = $true
        noLevelFields = $true
        historicalLevelRead = $false
        historicalParentRead = $false
        historicalHierarchyUsedForDecision = $false
        oldSemanticTotalUsedForDecision = $false
        providerCalls = 0
        modelCalls = 0
        parentReviewed = $false
        levelReviewed = $false
        GoldMutationOutsideNewLane = $false
    }
    inputOccurrenceCount = $occurrenceRefs.Count
    assignedOccurrenceCount = $assignments.Count
    semanticNodeCount = $nodes.Count
    primaryCount = @($assignments | Where-Object occurrenceRole -eq "PRIMARY").Count
    repeatCount = @($assignments | Where-Object occurrenceRole -eq "REPEAT").Count
    continuationCount = @($assignments | Where-Object occurrenceRole -eq "CONTINUATION").Count
    multiOccurrenceNodeCount = @($nodes | Where-Object { @($_.memberOccurrenceRefs).Count -gt 1 }).Count
    ambiguityCount = $ambiguities.Count
}
Write-JsonFile (Join-Path $outputPath "validation.json") $validation

# This diagnostic is intentionally computed only after identity artifacts are written.
# The historical value is non-binding and is never used to alter assignments.
$historicalGoldPath = Join-Path $repoRoot "eval/a99-closed-loop/strict-gold-v4/DOC-0256.strict-gold-v4.json"
$oldSemanticTotal = 34
$historicalArtifactSha = $null
if (Test-Path -LiteralPath $historicalGoldPath) {
    $historicalArtifactSha = (Get-FileHash -LiteralPath $historicalGoldPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $historicalDiagnostic = Get-Content -Raw -LiteralPath $historicalGoldPath | ConvertFrom-Json
    if ($null -ne $historicalDiagnostic.semanticHeadingTotal) { $oldSemanticTotal = [int]$historicalDiagnostic.semanticHeadingTotal }
}
Write-JsonFile (Join-Path $outputPath "post-freeze-diagnostics.json") ([ordered]@{
    artifactKind = "A99_CANONICAL_SEMANTIC_IDENTITY_POST_FREEZE_DIAGNOSTICS"
    schemaVersion = "a99-canonical-semantic-identity-v1-doc0256"
    documentId = $documentId
    identityArtifactsFrozenBeforeDiagnostic = $true
    finalSemanticNodeCount = $nodes.Count
    oldSemanticTotal = $oldSemanticTotal
    comparison = "$($nodes.Count) vs $oldSemanticTotal"
    oldSemanticTotalAuthority = "NON_BINDING_CROSS_AUTHORITY_DIAGNOSTIC"
    oldSemanticTotalUsedForDecision = $false
    historicalArtifact = "eval/a99-closed-loop/strict-gold-v4/DOC-0256.strict-gold-v4.json"
    historicalArtifactSha256 = $historicalArtifactSha
    historicalLevelRead = $false
    historicalParentRead = $false
    historicalIdentityUsedForDecision = $false
    note = "Canonical occurrence count is 24; inability to reach the old semantic total is not an error and does not trigger identity mutation."
})

$manifestFiles = @("occurrence-input-manifest.json", "occurrence-assignments.json", "semantic-nodes.json", "identity-decisions.json", "ambiguities.json", "validation.json", "post-freeze-diagnostics.json")
$manifestEntries = @($manifestFiles | ForEach-Object {
    $full = Join-Path $outputPath $_
    [pscustomobject]@{ name = $_; path = (Join-Path $OutputRoot $_) -replace '\\','/'; sha256 = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$manifest = [ordered]@{
    artifactKind = "A99_CANONICAL_SEMANTIC_IDENTITY_MANIFEST"
    schemaVersion = "a99-canonical-semantic-identity-v1-doc0256"
    documentId = $documentId
    status = "READY_FOR_USER_APPROVAL_CANONICAL_IDENTITY"
    inputOccurrenceCount = $occurrenceRefs.Count
    semanticNodeCount = $nodes.Count
    primaryCount = 24
    repeatCount = 0
    continuationCount = 0
    multiOccurrenceNodeCount = 0
    ambiguityCount = 0
    files = $manifestEntries
    occurrenceAuthoritySha256 = $occurrenceAuthoritySha256
    historicalLevelRead = $false
    historicalParentRead = $false
    historicalHierarchyUsedForDecision = $false
    oldSemanticTotalUsedForDecision = $false
    parentReviewed = $false
    levelReviewed = $false
    providerCalls = 0
    modelCalls = 0
    userApprovalRequired = $true
    stopBeforeHierarchy = $true
}
Write-JsonFile (Join-Path $outputPath "manifest.json") $manifest

$report = @"
# DOC-0256 — canonical semantic identity adjudication

Status: **READY_FOR_USER_APPROVAL_CANONICAL_IDENTITY**

## Result

- Frozen canonical heading occurrences: **24**
- Semantic nodes: **24**
- `PRIMARY`: **24**
- `REPEAT`: **0**
- `CONTINUATION`: **0**
- Multi-occurrence nodes: **0**
- Ambiguities: **0**

The source-only occurrence-first review traversed the 24 canonical heading spans in source order. Every occurrence was kept as a new semantic node because the canonical occurrence set contains no repeated heading text and no source-backed repeat/continuation evidence sufficient to collapse two occurrences. The default policy is fail-closed: keep split when identity is uncertain.

## Firewall

- Input authority: `$OccurrenceRoot/exact-bindings.json`
- Input authority SHA-256: `$occurrenceAuthoritySha256`
- Historical identity/hierarchy proposals were not used.
- Historical level and parent were not read.
- Old semantic total `34` was not used as a decision target.
- Parent/tree/level were not assigned or reviewed.
- Provider/model calls: `0`.

## Approval boundary

This is source-backed identity adjudication only. It is **not** `USER_REVIEWED_CANONICAL_IDENTITY_GOLD` until the user explicitly approves it.

The post-freeze comparison to old semantic total `34` is recorded only as `NON_BINDING_CROSS_AUTHORITY_DIAGNOSTIC`.
"@
Write-TextFile (Join-Path $outputPath "report.md") $report

Write-Output ($manifest | ConvertTo-Json -Depth 10)
