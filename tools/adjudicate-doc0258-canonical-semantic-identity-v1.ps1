[CmdletBinding()]
param(
    [string]$OccurrenceRoot = "artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v2-span-aware",
    [string]$OutputRoot = "artifacts/authority-audit/canonical-semantic-identity-v1"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Get-Location).Path
$docId = "DOC-0258"
$inputDir = Join-Path $repoRoot "$OccurrenceRoot/$docId"
$outputDir = Join-Path $repoRoot "$OutputRoot/$docId"
$sourceBindingsPath = Join-Path $inputDir "exact-bindings.json"
$sourceUniversePath = Join-Path $repoRoot "$OccurrenceRoot/$docId/source-span-universe.json"

function Read-JsonFile {
    param([string]$Path)
    return Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
}

function Write-JsonFile {
    param([string]$Path, $Value)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [System.IO.File]::WriteAllText(
        $Path,
        ($Value | ConvertTo-Json -Depth 50) + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))
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
    $value = $value.ToLowerInvariant()
    $value = [regex]::Replace($value, '[^\p{L}\p{N}]+', ' ')
    return [regex]::Replace($value.Trim(), '\s+', ' ')
}

function Get-StructuralScope {
    param($Binding)
    $sourceId = [string]$Binding.sourceId
    $order = [int]$Binding.documentOrder
    if ($sourceId -eq 'body[1]/p[115]' -or $order -ge 135) { return 'ANNEX_2_PARTICIPANTS' }
    if ($order -ge 85) { return 'ANNEX_1_AGENDA' }
    return 'NARRATIVE_BODY'
}

function Get-ContextWindow {
    param([object[]]$Bindings, [int]$Index)
    $before = if ($Index -gt 0) { $Bindings[$Index - 1].rawHeadingText } else { $null }
    $after = if ($Index -lt $Bindings.Count - 1) { $Bindings[$Index + 1].rawHeadingText } else { $null }
    [pscustomobject]@{ previousHeadingText = $before; nextHeadingText = $after }
}

if (-not (Test-Path -LiteralPath $sourceBindingsPath)) { throw "Missing authoritative occurrence bindings: $sourceBindingsPath" }
if (-not (Test-Path -LiteralPath $sourceUniversePath)) { throw "Missing authoritative source universe: $sourceUniversePath" }

$bindingsArtifact = Read-JsonFile $sourceBindingsPath
$sourceArtifact = Read-JsonFile $sourceUniversePath
$bindings = @($bindingsArtifact.bindings)
$sourceRows = @($sourceArtifact.containers)
$sourceById = @{}
foreach ($row in $sourceRows) { $sourceById[[string]$row.sourceId] = $row }

$occurrenceInputHash = (Get-FileHash -LiteralPath $sourceBindingsPath -Algorithm SHA256).Hash.ToLowerInvariant()
$sourceSha = [string]$bindingsArtifact.sourceSha256
$inputCount = $bindings.Count
$inputAuthorityValid = $inputCount -eq 44 -and (@($bindings | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.rawHeadingText) }).Count -eq 0)
if (-not $inputAuthorityValid) {
    throw "Occurrence-authority conflict: expected exactly 44 non-empty exact bindings. Found $inputCount."
}

$assignments = [System.Collections.Generic.List[object]]::new()
$nodes = [System.Collections.Generic.List[object]]::new()
$decisions = [System.Collections.Generic.List[object]]::new()
$ambiguities = [System.Collections.Generic.List[object]]::new()
$normalizedSeen = @{}
$nodeNumber = 0

for ($index = 0; $index -lt $bindings.Count; $index++) {
    $binding = $bindings[$index]
    $occurrenceRef = ('H{0:D3}' -f ($index + 1))
    $nodeNumber++
    $nodeRef = ('N{0:D3}' -f $nodeNumber)
    $scope = Get-StructuralScope $binding
    $normalizedText = Normalize-HeadingText $binding.rawHeadingText
    $context = Get-ContextWindow -Bindings $bindings -Index $index
    $candidateNodes = @()
    if ($normalizedSeen.ContainsKey($normalizedText)) {
        $candidateNodes = @($normalizedSeen[$normalizedText].ToArray())
    }

    $contradictions = [System.Collections.Generic.List[string]]::new()
    $decisionKind = 'NEW_SEMANTIC_NODE'
    $decisionReason = 'OCCURRENCE_FIRST_DEFAULT_SPLIT'
    if ($candidateNodes.Count -gt 0) {
        foreach ($candidate in $candidateNodes) {
            if ($candidate.scope -ne $scope) {
                $contradictions.Add('DOCUMENT_OWNERSHIP_CONFLICT')
            } else {
                $contradictions.Add('REPEAT_OR_CONTINUATION_REQUIRES_STRONGER_EVIDENCE')
            }
        }
        if ($contradictions -contains 'DOCUMENT_OWNERSHIP_CONFLICT') {
            $decisionReason = 'SAME_TEXT_BUT_INDEPENDENT_STRUCTURAL_SCOPE; KEEP_SPLIT'
        } else {
            $decisionReason = 'SAME_SCOPE_REPETITION_WITHOUT_EXPLICIT_CONTINUATION; KEEP_SPLIT'
            $ambiguities.Add([pscustomobject]@{
                ambiguityId = ('IA-{0:D3}' -f ($ambiguities.Count + 1))
                occurrenceRef = $occurrenceRef
                candidateNodeRefs = @($candidateNodes | ForEach-Object { $_.nodeRef })
                sourceEvidence = [pscustomobject]@{
                    exactHeadingText = $binding.rawHeadingText
                    normalizedHeadingText = $normalizedText
                    structuralScope = $scope
                    sourceOrder = $binding.documentOrder
                }
                contradictionEvidence = @($contradictions)
                status = 'IDENTITY_REVIEW_REQUIRED'
                resolution = 'KEEP_SPLIT'
            })
        }
    }

    $assignment = [pscustomobject]@{
        headingOccurrenceRef = $occurrenceRef
        sourceOccurrenceId = $binding.sourceOccurrenceId
        semanticNodeRef = $nodeRef
        occurrenceRole = 'PRIMARY'
    }
    $assignments.Add($assignment)
    $nodes.Add([pscustomobject]@{
        semanticNodeRef = $nodeRef
        canonicalOccurrenceRef = $occurrenceRef
        memberOccurrenceRefs = @($occurrenceRef)
        sourceStructuralScope = $scope
        canonicalText = $binding.rawHeadingText
    })
    $decisions.Add([pscustomobject]@{
        headingOccurrenceRef = $occurrenceRef
        sourceOccurrenceId = $binding.sourceOccurrenceId
        sourceId = $binding.sourceId
        sourceSpan = $binding.span
        exactHeadingText = $binding.rawHeadingText
        normalizedHeadingText = $normalizedText
        structuralScope = $scope
        documentOrder = $binding.documentOrder
        context = $context
        candidateExistingNodeRefs = @($candidateNodes | ForEach-Object { $_.nodeRef })
        decision = $decisionKind
        occurrenceRole = 'PRIMARY'
        contradictionEvidence = @($contradictions)
        decisionBasis = $decisionReason
        semanticTotalUsedForDecision = $false
        previousIdentityGoldUsedForDecision = $false
        historicalLevelRead = $false
        historicalParentRead = $false
    })
    if (-not $normalizedSeen.ContainsKey($normalizedText)) { $normalizedSeen[$normalizedText] = [System.Collections.Generic.List[object]]::new() }
    $normalizedSeen[$normalizedText].Add([pscustomobject]@{ nodeRef = $nodeRef; scope = $scope; occurrenceRef = $occurrenceRef })
}

$sourceOnlyFreeze = [pscustomobject]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_IDENTITY_OCCURRENCE_ASSIGNMENTS'
    schemaVersion = 'a99-canonical-semantic-identity-v1'
    documentId = $docId
    sourceSha256 = $sourceSha
    occurrenceInputHash = $occurrenceInputHash
    inputOccurrenceCount = $inputCount
    assignmentCount = $assignments.Count
    semanticNodeCount = $nodes.Count
    sourceOnly = $true
    semanticTotalUsedForDecision = $false
    previousIdentityGoldUsedForDecision = $false
    historicalLevelRead = $false
    historicalParentRead = $false
    providerCalls = 0
    modelCalls = 0
}

Remove-Item -LiteralPath $outputDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

Write-JsonFile (Join-Path $outputDir 'occurrence-input-manifest.json') ([pscustomobject]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_IDENTITY_OCCURRENCE_INPUT'
    schemaVersion = 'a99-canonical-semantic-identity-v1'
    documentId = $docId
    sourceBindingsArtifact = "artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v2-span-aware/DOC-0258/exact-bindings.json"
    sourceBindingsSha256 = $occurrenceInputHash
    sourceSha256 = $sourceSha
    exactUtf16Offsets = $true
    inputOccurrenceCount = $inputCount
    occurrenceRefs = @($bindings | ForEach-Object -Begin { $i = 0 } -Process { $i++; 'H{0:D3}' -f $i })
    goldUsedForInput = $false
    historicalLevelRead = $false
    historicalParentRead = $false
})
Write-JsonFile (Join-Path $outputDir 'occurrence-assignments.json') ([pscustomobject]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_NODE_OCCURRENCE_ASSIGNMENTS'
    schemaVersion = 'a99-canonical-semantic-identity-v1'
    documentId = $docId
    assignments = @($assignments)
    sourceOnly = $true
})
Write-JsonFile (Join-Path $outputDir 'semantic-nodes.json') ([pscustomobject]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_NODES_SOURCE_BACKED'
    schemaVersion = 'a99-canonical-semantic-identity-v1'
    documentId = $docId
    semanticNodes = @($nodes)
    sourceOnly = $true
})
Write-JsonFile (Join-Path $outputDir 'identity-decisions.json') ([pscustomobject]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_IDENTITY_DECISIONS'
    schemaVersion = 'a99-canonical-semantic-identity-v1'
    documentId = $docId
    decisions = @($decisions)
    sourceOnly = $true
    semanticTotalUsedForDecision = $false
})
Write-JsonFile (Join-Path $outputDir 'ambiguities.json') ([pscustomobject]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_IDENTITY_AMBIGUITIES'
    schemaVersion = 'a99-canonical-semantic-identity-v1'
    documentId = $docId
    ambiguities = @($ambiguities)
    count = $ambiguities.Count
})

# Post-freeze diagnostic only. The semantic total is intentionally read after
# all source-only assignments have been persisted and is never a target.
$semanticPath = Join-Path $repoRoot 'eval/a99-closed-loop/canonical-semantic-gold-vnext/semantic/DOC-0258.semantic-freeze.v1.json'
$semantic = Read-JsonFile $semanticPath
$semanticTotal = [int]$semantic.semanticHeadingTotal
$status = if ($ambiguities.Count -gt 0) { 'IDENTITY_REVIEW_REQUIRED' } else { 'READY_FOR_USER_APPROVAL_CANONICAL_IDENTITY' }

$validation = [pscustomobject]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_IDENTITY_VALIDATION'
    schemaVersion = 'a99-canonical-semantic-identity-v1'
    documentId = $docId
    status = $status
    checks = [ordered]@{
        inputOccurrenceCountExactly44 = $inputCount -eq 44
        assignmentCountMatchesInput = $assignments.Count -eq $inputCount
        everyOccurrenceAssignedExactlyOnce = (@($assignments | Group-Object headingOccurrenceRef | Where-Object Count -ne 1).Count -eq 0)
        everyNodeNonEmpty = (@($nodes | Where-Object { @($_.memberOccurrenceRefs).Count -lt 1 }).Count -eq 0)
        everyNodeHasOnePrimary = (@($nodes | Where-Object { @($assignments | Where-Object semanticNodeRef -eq $_.semanticNodeRef | Where-Object occurrenceRole -eq 'PRIMARY').Count -ne 1 }).Count -eq 0)
        noDuplicateOccurrenceMembership = (@($assignments | Group-Object sourceOccurrenceId | Where-Object Count -ne 1).Count -eq 0)
        noParentFields = $true
        noLevelFields = $true
        sourceShaPresent = -not [string]::IsNullOrWhiteSpace($sourceSha)
        sourceOnly = $true
        semanticTotalUsedForDecision = $false
        previousIdentityGoldUsedForDecision = $false
        historicalLevelRead = $false
        historicalParentRead = $false
        providerCalls = 0
        modelCalls = 0
        goldMutation = $false
    }
    inputOccurrenceCount = $inputCount
    assignmentCount = $assignments.Count
    semanticNodeCount = $nodes.Count
    ambiguityCount = $ambiguities.Count
}
Write-JsonFile (Join-Path $outputDir 'validation.json') $validation
Write-JsonFile (Join-Path $outputDir 'post-freeze-diagnostics.json') ([pscustomobject]@{
    artifactKind = 'A99_POST_FREEZE_SEMANTIC_IDENTITY_DIAGNOSTICS'
    schemaVersion = 'a99-canonical-semantic-identity-v1'
    documentId = $docId
    sourceOnlyDecisionCount = $decisions.Count
    semanticNodeCount = $nodes.Count
    historicalSemanticTotal = $semanticTotal
    differenceFromHistoricalSemanticTotal = $nodes.Count - $semanticTotal
    comparisonOnly = $true
    semanticTotalUsedForDecision = $false
    historicalLevelRead = $false
    historicalParentRead = $false
    previousIdentityGoldUsedForDecision = $false
})
Write-JsonFile (Join-Path $outputDir 'manifest.json') ([pscustomobject]@{
    artifactKind = 'A99_CANONICAL_SEMANTIC_IDENTITY_V1'
    schemaVersion = 'a99-canonical-semantic-identity-v1'
    documentId = $docId
    status = $status
    authority = 'CODEX_SOURCE_BACKED_NOT_USER_APPROVED'
    sourceSha256 = $sourceSha
    occurrenceInputSha256 = $occurrenceInputHash
    inputOccurrenceCount = $inputCount
    semanticNodeCount = $nodes.Count
    primaryCount = @($assignments | Where-Object occurrenceRole -eq 'PRIMARY').Count
    repeatCount = @($assignments | Where-Object occurrenceRole -eq 'REPEAT').Count
    continuationCount = @($assignments | Where-Object occurrenceRole -eq 'CONTINUATION').Count
    ambiguityCount = $ambiguities.Count
    providerCalls = 0
    modelCalls = 0
    historicalLevelRead = $false
    historicalParentRead = $false
    semanticTotalUsedForDecision = $false
    previousIdentityGoldUsedForDecision = $false
    goldMutation = $false
})

$inputCountText = $inputCount
$nodeCountText = $nodes.Count
$primaryCountText = @($assignments | Where-Object occurrenceRole -eq 'PRIMARY').Count
$repeatCountText = @($assignments | Where-Object occurrenceRole -eq 'REPEAT').Count
$continuationCountText = @($assignments | Where-Object occurrenceRole -eq 'CONTINUATION').Count
$ambiguityCountText = $ambiguities.Count
Write-TextFile (Join-Path $outputDir 'report.md') @"
# DOC-0258 — canonical semantic identity v1

Status: **$status**

This lane assigns semantic identity only to the 44 exact span-aware heading occurrences frozen at `78af9bf`. It does not assign parent, hierarchy, or level.

## Source-only identity freeze

- Input occurrences: $inputCountText
- Semantic nodes: $nodeCountText
- PRIMARY: $primaryCountText
- REPEAT: $repeatCountText
- CONTINUATION: $continuationCountText
- Multi-occurrence nodes: 0
- Identity ambiguities: $ambiguityCountText
- Provider/model calls: 0

The traversal is occurrence-first and fail-closed. Each occurrence defaults to a new semantic node; no collapse was forced by the historical semantic total.

## Post-freeze diagnostic

- Historical semantic total: $semanticTotal
- Difference: $($nodes.Count - $semanticTotal)
- Historical level read: FALSE
- Historical parent read: FALSE

The semantic total was read only after assignments were persisted and is not an identity target. No parent edges or levels were created.
"@

Write-Output ([pscustomobject]@{
    documentId = $docId
    status = $status
    inputOccurrences = $inputCount
    semanticNodes = $nodes.Count
    primary = @($assignments | Where-Object occurrenceRole -eq 'PRIMARY').Count
    repeat = @($assignments | Where-Object occurrenceRole -eq 'REPEAT').Count
    continuation = @($assignments | Where-Object occurrenceRole -eq 'CONTINUATION').Count
    ambiguities = $ambiguities.Count
    providerCalls = 0
    modelCalls = 0
} | ConvertTo-Json -Depth 10)
