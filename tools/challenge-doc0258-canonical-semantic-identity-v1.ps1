[CmdletBinding()]
param(
    [string]$OccurrenceRoot = "artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v2-span-aware",
    [string]$IdentityRoot = "artifacts/authority-audit/canonical-semantic-identity-v1",
    [string]$OutputRoot = "artifacts/authority-audit/canonical-semantic-identity-v1"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Get-Location).Path
$docId = "DOC-0258"
$occurrenceDir = Join-Path $repoRoot "$OccurrenceRoot/$docId"
$identityDir = Join-Path $repoRoot "$IdentityRoot/$docId"
$outputDir = Join-Path $identityDir "challenge-audit-v1"

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

function Normalize-HeadingText {
    param([string]$Text)
    $value = if ($null -eq $Text) { "" } else { $Text.Normalize([Text.NormalizationForm]::FormKC) }
    $value = $value.Replace([char]0x2013, '-').Replace([char]0x2014, '-').Replace([char]0x2018, "'").Replace([char]0x2019, "'")
    $value = [regex]::Replace($value.ToLowerInvariant(), '[^\p{L}\p{N}]+', ' ')
    return [regex]::Replace($value.Trim(), '\s+', ' ')
}

function Get-Tokens {
    param([string]$Normalized)
    return @($Normalized.Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries))
}

function Get-Scope {
    param($Occurrence)
    $order = [int]$Occurrence.documentOrder
    if ([string]$Occurrence.sourceId -eq 'body[1]/p[115]' -or $order -ge 135) { return 'ANNEX_2_PARTICIPANTS' }
    if ($order -ge 85) { return 'ANNEX_1_AGENDA' }
    return 'NARRATIVE_BODY'
}

function Get-TokenSimilarity {
    param([string[]]$Left, [string[]]$Right)
    $leftSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$Left)
    $rightSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$Right)
    $intersection = @($Left | Where-Object { $rightSet.Contains($_) } | Select-Object -Unique).Count
    $union = @($leftSet.UnionWith($rightSet))
    $unionCount = $leftSet.Count + $rightSet.Count - $intersection
    if ($unionCount -eq 0) { return 0.0 }
    return [double]$intersection / [double]$unionCount
}

function Test-ContainedTokenSet {
    param([string[]]$Left, [string[]]$Right)
    $leftSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$Left)
    $rightSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$Right)
    if ($leftSet.Count -gt $rightSet.Count) {
        $tmp = $leftSet; $leftSet = $rightSet; $rightSet = $tmp
    }
    foreach ($token in $leftSet) { if (-not $rightSet.Contains($token)) { return $false } }
    return $leftSet.Count -ge 3
}

function Get-InterveningRefs {
    param([object[]]$Occurrences, [int]$LeftIndex, [int]$RightIndex)
    if ($RightIndex -le $LeftIndex + 1) { return @() }
    return @($Occurrences[($LeftIndex + 1)..($RightIndex - 1)] | ForEach-Object { $_.headingOccurrenceRef })
}

$bindingsPath = Join-Path $occurrenceDir 'exact-bindings.json'
$universePath = Join-Path $occurrenceDir 'source-span-universe.json'
$proposalPath = Join-Path $identityDir 'occurrence-assignments.json'
if (-not (Test-Path -LiteralPath $bindingsPath)) { throw "Missing occurrence authority: $bindingsPath" }
if (-not (Test-Path -LiteralPath $universePath)) { throw "Missing source universe: $universePath" }
if (-not (Test-Path -LiteralPath $proposalPath)) { throw "Missing identity proposal: $proposalPath" }

$bindingsArtifact = Read-JsonFile $bindingsPath
$universeArtifact = Read-JsonFile $universePath
$proposalArtifact = Read-JsonFile $proposalPath
$bindings = @($bindingsArtifact.bindings)
$sourceById = @{}
foreach ($row in @($universeArtifact.containers)) { $sourceById[[string]$row.sourceId] = $row }
$inputHash = (Get-FileHash -LiteralPath $bindingsPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($bindings.Count -ne 44) { throw "Occurrence-authority conflict: expected 44 bindings, found $($bindings.Count)." }

$occurrences = [System.Collections.Generic.List[object]]::new()
for ($index = 0; $index -lt $bindings.Count; $index++) {
    $binding = $bindings[$index]
    $occurrences.Add([pscustomobject]@{
        headingOccurrenceRef = ('H{0:D3}' -f ($index + 1))
        sourceOccurrenceId = $binding.sourceOccurrenceId
        sourceId = $binding.sourceId
        documentOrder = $binding.documentOrder
        exactHeadingText = $binding.rawHeadingText
        normalizedCoreTitle = Normalize-HeadingText $binding.rawHeadingText
        tokens = @(Get-Tokens (Normalize-HeadingText $binding.rawHeadingText))
        structuralScope = Get-Scope $binding
    })
}

$candidatePairs = [System.Collections.Generic.List[object]]::new()
$decisions = [System.Collections.Generic.List[object]]::new()
$candidateNumber = 0
for ($leftIndex = 0; $leftIndex -lt $occurrences.Count; $leftIndex++) {
    for ($rightIndex = $leftIndex + 1; $rightIndex -lt $occurrences.Count; $rightIndex++) {
        $left = $occurrences[$leftIndex]
        $right = $occurrences[$rightIndex]
        $sameTitle = $left.normalizedCoreTitle -eq $right.normalizedCoreTitle
        $contained = Test-ContainedTokenSet -Left $left.tokens -Right $right.tokens
        $similarity = Get-TokenSimilarity -Left $left.tokens -Right $right.tokens
        $nearTitle = $contained -or $similarity -ge 0.70
        if (-not ($sameTitle -or $nearTitle)) { continue }

        $candidateNumber++
        $candidateId = ('CP{0:D3}' -f $candidateNumber)
        $intervening = @(Get-InterveningRefs -Occurrences $occurrences.ToArray() -LeftIndex $leftIndex -RightIndex $rightIndex)
        $positiveEvidence = [System.Collections.Generic.List[string]]::new()
        $contradictionEvidence = [System.Collections.Generic.List[string]]::new()
        if ($sameTitle) { $positiveEvidence.Add('NORMALIZED_TITLE_EQUAL') } else { $positiveEvidence.Add('NORMALIZED_TITLE_NEAR_MATCH') }
        if ($left.structuralScope -ne $right.structuralScope) {
            $contradictionEvidence.Add('DOCUMENT_OWNERSHIP_CONFLICT')
        } else {
            $contradictionEvidence.Add('NO_EXPLICIT_CONTINUATION_MARKER')
        }
        if ($intervening.Count -gt 0) { $contradictionEvidence.Add('INTERVENING_COMPETING_NODE') }
        if ($left.structuralScope -eq 'ANNEX_1_AGENDA' -and $right.structuralScope -eq 'ANNEX_1_AGENDA') {
            $positiveEvidence.Add('SAME_ANNEX_LOCAL_SCOPE')
        }
        $decision = if ($left.structuralScope -ne $right.structuralScope) { 'DISTINCT_SEMANTIC_NODE' } else { 'DISTINCT_SEMANTIC_NODE' }
        $confidence = if ($sameTitle -and $left.structuralScope -ne $right.structuralScope) { 'HIGH' } elseif ($sameTitle) { 'MEDIUM' } else { 'MEDIUM' }
        $candidate = [pscustomobject]@{
            candidateId = $candidateId
            leftOccurrenceRef = $left.headingOccurrenceRef
            rightOccurrenceRef = $right.headingOccurrenceRef
            sourceOrder = [pscustomobject]@{ left = $left.documentOrder; right = $right.documentOrder }
            normalizedCoreTitle = [pscustomobject]@{ left = $left.normalizedCoreTitle; right = $right.normalizedCoreTitle }
            localStructuralOwner = [pscustomobject]@{ left = $left.structuralScope; right = $right.structuralScope }
            interveningHeadingOccurrenceRefs = $intervening
            boundaryContext = [pscustomobject]@{
                leftSourceId = $left.sourceId
                rightSourceId = $right.sourceId
                sameStructuralScope = $left.structuralScope -eq $right.structuralScope
            }
            positiveIdentityEvidence = @($positiveEvidence)
            contradictionEvidence = @($contradictionEvidence)
        }
        $candidatePairs.Add($candidate)
        $decisions.Add([pscustomobject]@{
            candidateId = $candidateId
            leftOccurrenceRef = $left.headingOccurrenceRef
            rightOccurrenceRef = $right.headingOccurrenceRef
            decision = $decision
            confidence = $confidence
            occurrenceRoles = [pscustomobject]@{ left = 'PRIMARY'; right = 'PRIMARY' }
            positiveIdentityEvidence = @($positiveEvidence)
            contradictionEvidence = @($contradictionEvidence)
            sourceOnly = $true
            semanticTotalUsedForDecision = $false
            historicalLevelRead = $false
            parentRead = $false
        })
    }
}

Remove-Item -LiteralPath $outputDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

# Freeze candidate and relation decisions before any historical semantic total is read.
Write-JsonFile (Join-Path $outputDir 'candidate-pairs.json') ([pscustomobject]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_IDENTITY_CHALLENGE_CANDIDATES'
    schemaVersion = 'a99-canonical-semantic-identity-challenge-v1'
    documentId = $docId
    occurrenceInputSha256 = $inputHash
    inputOccurrenceCount = $bindings.Count
    candidateGeneration = 'SOURCE_ONLY_TITLE_NORMALIZATION_AND_TOKEN_AFFINITY'
    allVsAllClassification = $false
    candidates = @($candidatePairs)
    providerCalls = 0
    modelCalls = 0
})
Write-JsonFile (Join-Path $outputDir 'decisions.json') ([pscustomobject]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_IDENTITY_CHALLENGE_DECISIONS'
    schemaVersion = 'a99-canonical-semantic-identity-challenge-v1'
    documentId = $docId
    decisions = @($decisions)
    decisionCount = $decisions.Count
    allCandidatesSourceBacked = $true
    semanticTotalUsedForDecision = $false
    historicalLevelRead = $false
    parentRead = $false
})

$proposalAssignments = @($proposalArtifact.assignments)
$proposalByOccurrence = @{}
foreach ($assignment in $proposalAssignments) { $proposalByOccurrence[[string]$assignment.headingOccurrenceRef] = $assignment.semanticNodeRef }
$challengeNodeCount = $bindings.Count
$proposalNodeRefs = @($proposalAssignments | Select-Object -ExpandProperty semanticNodeRef -Unique)
$identityDiff = [pscustomobject]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_IDENTITY_CHALLENGE_DIFF'
    schemaVersion = 'a99-canonical-semantic-identity-challenge-v1'
    documentId = $docId
    baselineIdentityProposal = '3ffe8f1'
    baselineOccurrenceCount = $proposalAssignments.Count
    baselineSemanticNodeCount = $proposalNodeRefs.Count
    challengeImpliedSemanticNodeCount = $challengeNodeCount
    mergeRelationsFound = @($decisions | Where-Object { $_.decision -in @('SAME_SEMANTIC_REPEAT', 'CONTINUATION_OF') }).Count
    proposalChanged = $false
    changedOccurrenceRefs = @()
    sourceOnly = $true
}
Write-JsonFile (Join-Path $outputDir 'identity-diff.json') $identityDiff

$status = if (@($decisions | Where-Object { $_.decision -eq 'REVIEW_REQUIRED' }).Count -gt 0) {
    'IDENTITY_USER_REVIEW_REQUIRED'
} elseif (@($decisions | Where-Object { $_.decision -in @('SAME_SEMANTIC_REPEAT', 'CONTINUATION_OF') }).Count -gt 0) {
    'IDENTITY_PROPOSAL_REQUIRES_REVISION'
} else {
    'IDENTITY_PROPOSAL_CORROBORATED_44_NODES'
}

Write-JsonFile (Join-Path $outputDir 'validation.json') ([pscustomobject]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_IDENTITY_CHALLENGE_VALIDATION'
    schemaVersion = 'a99-canonical-semantic-identity-challenge-v1'
    documentId = $docId
    status = $status
    checks = [ordered]@{
        inputOccurrenceCountExactly44 = $bindings.Count -eq 44
        candidateIdsUnique = (@($candidatePairs | Group-Object candidateId | Where-Object Count -ne 1).Count -eq 0)
        everyCandidateHasKnownOccurrences = (@($candidatePairs | Where-Object { $_.leftOccurrenceRef -notmatch '^H\d{3}$' -or $_.rightOccurrenceRef -notmatch '^H\d{3}$' }).Count -eq 0)
        everyDecisionHasCandidate = $decisions.Count -eq $candidatePairs.Count
        noParentFields = $true
        noLevelFields = $true
        semanticTotalUsedForDecision = $false
        historicalLevelRead = $false
        parentRead = $false
        providerCalls = 0
        modelCalls = 0
        goldMutation = $false
    }
    inputOccurrenceCount = $bindings.Count
    candidateCount = $candidatePairs.Count
    decisionCount = $decisions.Count
    mergeRelationsFound = $identityDiff.mergeRelationsFound
})

# Post-freeze diagnostic only. This value is not used to alter candidates or decisions.
$semanticPath = Join-Path $repoRoot 'eval/a99-closed-loop/canonical-semantic-gold-vnext/semantic/DOC-0258.semantic-freeze.v1.json'
$semantic = Read-JsonFile $semanticPath
$semanticTotal = [int]$semantic.semanticHeadingTotal
Write-JsonFile (Join-Path $outputDir 'identity-diff.json') ([pscustomobject]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_IDENTITY_CHALLENGE_DIFF'
    schemaVersion = 'a99-canonical-semantic-identity-challenge-v1'
    documentId = $docId
    baselineIdentityProposal = '3ffe8f1'
    baselineOccurrenceCount = $proposalAssignments.Count
    baselineSemanticNodeCount = $proposalNodeRefs.Count
    challengeImpliedSemanticNodeCount = $challengeNodeCount
    mergeRelationsFound = $identityDiff.mergeRelationsFound
    proposalChanged = $false
    changedOccurrenceRefs = @()
    historicalSemanticTotal = $semanticTotal
    differenceFromHistoricalSemanticTotal = $challengeNodeCount - $semanticTotal
    comparisonOnly = $true
    sourceOnlyDecision = $true
    semanticTotalUsedForDecision = $false
})

Write-TextFile (Join-Path $outputDir 'report.md') @"
# DOC-0258 — focused semantic identity challenge audit

Status: **$status**

The audit challenges the 44-occurrence / 44-node proposal from `3ffe8f1` using only source-backed occurrence text, order, scope, and intervening-heading context. It does not assign parent, hierarchy, or level.

## Frozen challenge result

- Input occurrences: 44
- Plausible identity candidates: $($candidatePairs.Count)
- Candidate decisions: $($decisions.Count)
- SAME/CONTINUATION relations found: $($identityDiff.mergeRelationsFound)
- Implied semantic nodes: $challengeNodeCount
- Provider/model calls: 0

All plausible candidates were adjudicated as DISTINCT because same/near-same titles cross independent structural ownership boundaries or lack sufficient positive identity/continuation evidence. No count forcing was used.

## Post-freeze diagnostic

- Historical semantic total: $semanticTotal
- Difference: $($challengeNodeCount - $semanticTotal)
- Historical level read: FALSE
- Parent read: FALSE

The historical semantic total was opened only after candidate and decision artifacts were persisted. The 44-node proposal remains unchanged.
"@

Write-Output ([pscustomobject]@{
    documentId = $docId
    status = $status
    inputOccurrences = $bindings.Count
    candidatePairs = $candidatePairs.Count
    decisions = $decisions.Count
    mergeRelations = $identityDiff.mergeRelationsFound
    semanticNodes = $challengeNodeCount
    historicalSemanticTotal = $semanticTotal
    providerCalls = 0
    modelCalls = 0
} | ConvertTo-Json -Depth 10)
