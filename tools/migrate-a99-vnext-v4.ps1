[CmdletBinding()]
param(
    [string]$RepoRoot = (Get-Location).Path,
    [string]$RegistryPath = 'C:\Users\btdba\Downloads\freeze-registry.v4.checked.json',
    [string]$TaskPath = 'C:\Users\btdba\Downloads\TASK-a99-vnext-EXECUTE-final.v4.checked.md'
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
$registry = Get-Content -LiteralPath $RegistryPath -Raw | ConvertFrom-Json
if ($registry.schemaVersion -ne 'a99-canonical-semantic-gold-vnext-v1' -or $registry.registryRevision -ne 4) {
    throw 'REGISTRY_NOT_V4_CHECKED'
}
if ($registry.summary.totalTracked -ne 13 -or $registry.summary.frozenSemanticVnext -ne 13 -or
    $registry.summary.semanticHeadingTotalAcrossTrackedSources -ne 1503) {
    throw 'REGISTRY_SUMMARY_MISMATCH'
}

$target = Join-Path $RepoRoot 'eval\a99-closed-loop\canonical-semantic-gold-vnext'
New-Item -ItemType Directory -Force -Path $target, (Join-Path $target 'semantic'), (Join-Path $target 'projections') | Out-Null

function Write-Json([string]$Path, $Value) {
    $Value | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Find-Source($entry) {
    $matches = @(Get-ChildItem -Path $RepoRoot -Recurse -File -Filter $entry.fileName |
        Where-Object { $_.FullName -notlike '*\.claude\worktrees\*' -and $_.FullName -notlike '*\.git\*' })
    if ($matches.Count -eq 0) { throw "SOURCE_NOT_FOUND:$($entry.key):$($entry.fileName)" }
    $preferred = @($matches | Where-Object {
        $_.FullName -like '*\heading_corpus_100\*' -or $_.FullName -like '*\heading_corpus_95_word\*'
    })
    if ($preferred.Count -gt 0) { return $preferred[0] }
    return $matches[0]
}

$resolved = @()
foreach ($entry in $registry.entries) {
    if ($entry.status -ne 'FROZEN_SEMANTIC_VNEXT' -or -not $entry.userFinalApproval -or $entry.exactOccurrenceFreeze) {
        throw "ENTRY_NOT_SEMANTIC_ONLY_FROZEN:$($entry.key)"
    }
    $source = Find-Source $entry
    $relative = $source.FullName.Substring($RepoRoot.Length).TrimStart('\').Replace('\', '/')
    $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $source.FullName).Hash.ToLowerInvariant()
    if ($entry.sourceSha256 -and $sourceHash -ne $entry.sourceSha256.ToLowerInvariant()) {
        throw "SOURCE_HASH_MISMATCH:$($entry.key):$sourceHash"
    }
    $semantic = [ordered]@{
        artifactKind = 'a99_canonical_semantic_freeze'
        schemaVersion = 'a99-canonical-semantic-freeze-vnext-v1'
        authorityKey = $entry.key
        fileName = $entry.fileName
        sourcePath = $relative
        sourceSha256 = $sourceHash
        registrySourceSha256 = $entry.sourceSha256
        semanticHeadingTotal = [int]$entry.semanticHeadingTotal
        status = 'FROZEN_SEMANTIC_VNEXT'
        finalAuthority = 'USER_APPROVED_SOURCE_ONLY_REVIEW'
        truthDefinition = 'ALL_TRUE_HEADING_OCCURRENCES'
        semanticMembership = 'HEADING'
        promptAffectsSemanticTruth = $false
        repeatedOrContinuationMayBeHeading = $true
        newSemanticNodeRequiredForHeading = $false
        exactOccurrenceFreeze = $false
        capabilities = [ordered]@{
            semanticEvaluable = $true
            occurrenceEvaluable = $false
            characterSpanEvaluable = $false
            hierarchyEvaluable = $false
        }
        sourceAliasPolicy = 'PARSER_OWNED_DETERMINISTIC_S0001_ORDERED_SOURCE_CATALOG'
        occurrenceArtifact = $null
        bindingArtifact = $null
        historicalArtifactsAreProvenanceOnly = $true
        notes = @('Registry is authoritative for semantic total only.', 'No heading rows, spans, parentage or semantic nodes synthesized from the total.')
    }
    Write-Json (Join-Path $target "semantic\$($entry.key).semantic-freeze.v1.json") $semantic
    $resolved += [ordered]@{
        authorityKey = $entry.key
        fileName = $entry.fileName
        sourcePath = $relative
        sourceSha256 = $sourceHash
        registrySourceSha256 = $entry.sourceSha256
        semanticHeadingTotal = [int]$entry.semanticHeadingTotal
        semanticArtifact = "semantic/$($entry.key).semantic-freeze.v1.json"
        occurrenceEvaluable = $false
        characterSpanEvaluable = $false
        hierarchyEvaluable = $false
    }
}

$aggregate = [int](($resolved | ForEach-Object { [int]$_.semanticHeadingTotal } | Measure-Object -Sum).Sum)
if ($resolved.Count -ne 13 -or $aggregate -ne 1503) { throw "MATERIALIZED_INVENTORY_MISMATCH:$($resolved.Count):$aggregate" }
Copy-Item -LiteralPath $RegistryPath -Destination (Join-Path $target 'freeze-registry.v4.checked.json') -Force

$lineageRoots = @(
    'eval/a99-closed-loop/strict-gold-v3',
    'eval/a99-closed-loop/strict-gold-v4',
    'eval/a99-closed-loop/strict-gold-occurrence-v1',
    'eval/harness-lift/review-packets',
    'eval/harness-lift/review-packets-v2',
    'eval/benchmark-n0/source-packets',
    'eval/a99-closed-loop/research-r2'
)
$lineageFiles = @()
foreach ($lineageRoot in $lineageRoots) {
    $absolute = Join-Path $RepoRoot ($lineageRoot.Replace('/', '\'))
    if (Test-Path -LiteralPath $absolute) {
        $lineageFiles += @(Get-ChildItem -LiteralPath $absolute -Recurse -File | ForEach-Object {
            [ordered]@{ path = $_.FullName.Substring($RepoRoot.Length).TrimStart('\').Replace('\', '/'); sha256 = (Get-FileHash -Algorithm SHA256 $_.FullName).Hash.ToLowerInvariant(); byteLength = $_.Length; authority = 'PROVENANCE_ONLY' }
        })
    }
}

Write-Json (Join-Path $target 'inventory.v1.json') ([ordered]@{
    artifactKind = 'a99_canonical_semantic_gold_vnext_inventory'
    schemaVersion = 'a99-canonical-semantic-gold-vnext-inventory-v2'
    authority = 'freeze-registry.v4.checked.json'
    totalTracked = 13
    frozenSemanticVnext = 13
    exactOccurrenceFrozen = 0
    aggregateSemanticHeadingTotal = 1503
    documents = $resolved
})
Write-Json (Join-Path $target 'lineage-inventory.v1.json') ([ordered]@{
    artifactKind = 'a99_canonical_semantic_gold_vnext_lineage_inventory'
    schemaVersion = 'a99-canonical-semantic-gold-vnext-lineage-v2'
    authorityRegistry = 'freeze-registry.v4.checked.json'
    authorityRegistrySha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $RegistryPath).Hash.ToLowerInvariant()
    sourceInventory = 'eval/a99-dataset/document-inventory.v1.json'
    sourceInventorySha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $RepoRoot 'eval/a99-dataset/document-inventory.v1.json')).Hash.ToLowerInvariant()
    roots = $lineageRoots
    fileCount = $lineageFiles.Count
    files = $lineageFiles
    note = 'Legacy Gold and review artifacts are provenance only; the v4 checked registry is the current semantic authority.'
})
Write-Json (Join-Path $target 'migration-manifest.v1.json') ([ordered]@{
    artifactKind = 'a99_canonical_semantic_gold_vnext_migration'
    schemaVersion = 'a99-canonical-semantic-gold-vnext-migration-v2'
    authorityRegistry = 'freeze-registry.v4.checked.json'
    authorityRegistrySha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $RegistryPath).Hash.ToLowerInvariant()
    executionTask = if (Test-Path -LiteralPath $TaskPath) { Split-Path -Leaf $TaskPath } else { $null }
    executionTaskSha256 = if (Test-Path -LiteralPath $TaskPath) { (Get-FileHash -Algorithm SHA256 -LiteralPath $TaskPath).Hash.ToLowerInvariant() } else { $null }
    providerCalls = 0
    modelCalls = 0
    totalTracked = 13
    frozenSemanticVnext = 13
    aggregateSemanticHeadingTotal = 1503
    exactOccurrenceFrozen = 0
    occurrenceArtifactsCreated = 0
    bindingArtifactsCreated = 0
    goldRuntimeInputs = $false
})

$projectionCommon = [ordered]@{
    artifactKind = 'a99_canonical_projection_policy'
    schemaVersion = 'a99-canonical-projection-policy-vnext-v2'
    input = 'canonical semantic graph'
    semanticTruthMutation = $false
    taskMembershipMutation = $false
    preserve = @('every true heading occurrence', 'PRIMARY', 'REPEAT', 'CONTINUATION')
}
Write-Json (Join-Path $target 'projections\all-true-headings.v1.json') ($projectionCommon + [ordered]@{ policyId = 'ALL_TRUE_HEADINGS'; output = 'all canonical occurrences'; collapseKey = $null })
Write-Json (Join-Path $target 'projections\main-document-outline.v1.json') ($projectionCommon + [ordered]@{ policyId = 'MAIN_DOCUMENT_OUTLINE'; output = 'one occurrence per semanticNodeId'; collapseKey = 'semanticNodeId' })
Write-Json (Join-Path $target 'projections\chapter-section-article.v1.json') ($projectionCommon + [ordered]@{ policyId = 'CHAPTER_SECTION_ARTICLE'; output = 'filtered task view after canonical semantic boundary'; collapseKey = 'semanticNodeId'; allowedStructuralTypes = @('Chapter', 'Section', 'Article') })

$readme = @'
# A99 canonical semantic Gold vNext

Current authority: `freeze-registry.v4.checked.json`.

The registry freezes semantic document totals for 13 sources (aggregate 1503). It does not
freeze an exhaustive occurrence list or character spans: every source currently has
`exactOccurrenceFreeze=false`. Therefore this directory intentionally contains no occurrence
or binding artifact. Coordinates may be materialized later only from authoritative source-backed
lists through the exact UTF-16 binder.

Runtime boundary:

`source -> source-faithful evidence -> stable aliases -> candidate attention hints -> route/context packing -> semantic proposal -> semantic validation -> exact UTF-16 binding -> hard binding validation -> global graph -> semantic boundary -> intent normalization -> deterministic projection`

The model supplies meaning (`sourceAlias/sourceAliases`, `isHeading`, exact verbatim text/parts,
role/type/scope and optional relation hints). The harness supplies coordinates. Repeated and
continuation headings remain canonical occurrences; projection may collapse them without changing
canonical truth. Legacy strict Gold and review artifacts are provenance only and are preserved.
'@
Set-Content -LiteralPath (Join-Path $target 'README.md') -Value ($readme.TrimEnd() + [Environment]::NewLine) -Encoding utf8NoBOM
Write-Output "MIGRATED_V4=$($resolved.Count) AGGREGATE=1503 TARGET=$target"
