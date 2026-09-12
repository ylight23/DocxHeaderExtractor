[CmdletBinding()]
param(
    [string]$RepoRoot = (Get-Location).Path,
    [string]$RegistryPath = 'C:\Users\btdba\Downloads\freeze-registry.v3.final.json'
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path $RepoRoot).Path
$registry = Get-Content -Raw $RegistryPath | ConvertFrom-Json
$target = Join-Path $RepoRoot 'eval\a99-closed-loop\canonical-semantic-gold-vnext'
$dirs = @('semantic', 'occurrence', 'bindings', 'projections')
New-Item -ItemType Directory -Force -Path $target | Out-Null
$dirs | ForEach-Object { New-Item -ItemType Directory -Force -Path (Join-Path $target $_) | Out-Null }

function Write-Json([string]$Path, $Value) {
    $json = $Value | ConvertTo-Json -Depth 30
    [System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function Find-Source($entry) {
    $matches = @(Get-ChildItem -Path $RepoRoot -Recurse -File -Filter $entry.fileName |
        Where-Object { $_.FullName -notlike '*\.claude\worktrees\*' })
    if ($matches.Count -eq 0) { return $null }
    $preferred = $matches | Where-Object {
        $_.FullName -like '*\heading_corpus_100\*' -or $_.FullName -like '*\heading_corpus_95_word\*'
    } | Select-Object -First 1
    return ($preferred ?? ($matches | Select-Object -First 1))
}

function Find-HistoricalGold([string]$documentId) {
    $path = Join-Path $RepoRoot "eval\a99-closed-loop\strict-gold-v4\$documentId.strict-gold-v4.json"
    if (-not (Test-Path $path)) { return $null }
    return [pscustomobject]@{ path = $path; hash = (Get-FileHash -Algorithm SHA256 $path).Hash.ToLowerInvariant(); data = (Get-Content -Raw $path | ConvertFrom-Json) }
}

$resolved = @()
foreach ($entry in $registry.entries) {
    $source = Find-Source $entry
    if ($null -eq $source) { throw "SOURCE_NOT_FOUND:$($entry.key):$($entry.fileName)" }
    $sourcePath = $source.FullName.Substring($RepoRoot.Length).TrimStart('\').Replace('\', '/')
    $sourceHash = (Get-FileHash -Algorithm SHA256 $source.FullName).Hash.ToLowerInvariant()
    $historical = Find-HistoricalGold $entry.key
    $headings = @()
    $materialized = $false
    if ($null -ne $historical -and $historical.data.semanticHeadingTotal -eq $entry.semanticHeadingTotal) {
        $headings = @($historical.data.headings | ForEach-Object {
            [ordered]@{
                sourceId = $_.sourceId
                exactText = $_.exactText
                semanticRole = $_.role
                semanticMembership = 'HEADING'
                sourceOccurrenceAuthority = 'HISTORICAL_V4_REFERENCE'
            }
        })
        $materialized = $true
    }
    $semantic = [ordered]@{
        artifactKind = 'a99_canonical_semantic_gold'
        schemaVersion = 'a99-canonical-semantic-gold-vnext-v1'
        policyVersion = $registry.policyVersion
        documentId = $entry.key
        fileName = $entry.fileName
        sourcePath = $sourcePath
        sourceSha256 = $sourceHash
        registrySourceSha256 = $entry.sourceSha256
        semanticHeadingTotal = [int]$entry.semanticHeadingTotal
        status = 'FROZEN_SEMANTIC_VNEXT'
        finalAuthority = 'USER_APPROVED_SOURCE_ONLY_REVIEW'
        semanticMembership = 'HEADING'
        truthDefinition = 'ALL_TRUE_HEADING_OCCURRENCES'
        promptAffectsSemanticTruth = $false
        repeatedOrContinuationMayBeHeading = $true
        semanticNodePerOccurrence = $false
        exactOccurrenceFreeze = $false
        sourceAliasPolicy = 'PARSER_OWNED_DETERMINISTIC_S0001_ORDERED_SOURCE_CATALOG'
        headingsMaterializedFromHistoricalReference = $materialized
        headings = $headings
        historicalProvenance = if ($null -ne $historical) {
            [ordered]@{ path = $historical.path.Substring($RepoRoot.Length).TrimStart('\').Replace('\', '/'); sha256 = $historical.hash; status = 'NON_AUTHORITATIVE_HISTORICAL_REFERENCE' }
        } else { $null }
        migrationAuthority = 'freeze-registry.v3.final.json'
        migrationNote = if ($materialized) { 'Semantic rows copied without numeric coordinates; exact occurrence binding remains a separate unresolved freeze.' } else { 'Registry-approved semantic total migrated; row-level semantic source review is represented by the registry and exact occurrence remains REVIEW_REQUIRED.' }
    }
    Write-Json (Join-Path $target "semantic\$($entry.key).semantic-gold.v1.json") $semantic

    $occurrence = [ordered]@{
        artifactKind = 'a99_canonical_occurrence_gold'
        schemaVersion = 'a99-canonical-occurrence-gold-vnext-v1'
        documentId = $entry.key
        sourcePath = $sourcePath
        sourceSha256 = $sourceHash
        semanticHeadingTotal = [int]$entry.semanticHeadingTotal
        exactOccurrenceFreeze = $false
        status = 'REVIEW_REQUIRED'
        bindings = @()
        unresolvedExpectedBindings = [int]$entry.semanticHeadingTotal
        bindingCoordinateSystem = 'UTF-16'
        reason = 'Semantic freeze is authoritative; no exact occurrence/span freeze is implied or fabricated by migration.'
    }
    Write-Json (Join-Path $target "occurrence\$($entry.key).occurrence-gold.v1.json") $occurrence

    $binding = [ordered]@{
        artifactKind = 'a99-canonical-exact-bindings'
        schemaVersion = 'a99-canonical-exact-bindings-vnext-v1'
        documentId = $entry.key
        sourcePath = $sourcePath
        sourceSha256 = $sourceHash
        status = 'REVIEW_REQUIRED'
        coordinateSystem = 'UTF-16'
        binder = 'CanonicalSemanticExactBinder'
        bindingCount = 0
        expectedSemanticHeadingCount = [int]$entry.semanticHeadingTotal
        bindings = @()
        note = 'Generated as an explicit non-frozen placeholder; deterministic binding must be materialized from aliases and verbatim model/source text before exact evaluation.'
    }
    Write-Json (Join-Path $target "bindings\$($entry.key).exact-bindings.v1.json") $binding
    $resolved += [ordered]@{
        documentId = $entry.key
        fileName = $entry.fileName
        sourcePath = $sourcePath
        sourceSha256 = $sourceHash
        registrySourceSha256 = $entry.sourceSha256
        semanticHeadingTotal = [int]$entry.semanticHeadingTotal
        semanticArtifact = "semantic/$($entry.key).semantic-gold.v1.json"
        occurrenceArtifact = "occurrence/$($entry.key).occurrence-gold.v1.json"
        bindingArtifact = "bindings/$($entry.key).exact-bindings.v1.json"
    }
}

Copy-Item -LiteralPath $RegistryPath -Destination (Join-Path $target 'freeze-registry.v1.json') -Force
$taskPath = Join-Path (Split-Path $RegistryPath -Parent) 'TASK-a99-vnext-EXECUTE-final.v3.md'
Write-Json (Join-Path $target 'migration-manifest.v1.json') ([ordered]@{
    artifactKind = 'a99_canonical_semantic_gold_vnext_migration'
    schemaVersion = 'a99-canonical-semantic-gold-vnext-migration-v1'
    authorityRegistry = 'freeze-registry.v3.final.json'
    authorityRegistrySha256 = (Get-FileHash -Algorithm SHA256 $RegistryPath).Hash.ToLowerInvariant()
    executionTask = if (Test-Path $taskPath) { 'TASK-a99-vnext-EXECUTE-final.v3.md' } else { $null }
    executionTaskSha256 = if (Test-Path $taskPath) { (Get-FileHash -Algorithm SHA256 $taskPath).Hash.ToLowerInvariant() } else { $null }
    providerCalls = 0
    modelCalls = 0
    totalTracked = $resolved.Count
    frozenSemanticVnext = $resolved.Count
    exactOccurrenceFrozen = 0
    goldRuntimeInputs = $false
})
Write-Json (Join-Path $target 'inventory.v1.json') ([ordered]@{
    artifactKind = 'a99_canonical_semantic_gold_vnext_inventory'
    schemaVersion = 'a99-canonical-semantic-gold-vnext-inventory-v1'
    authority = 'freeze-registry.v3.final.json'
    totalTracked = $resolved.Count
    frozenSemanticVnext = @($resolved | Where-Object { $_.semanticHeadingTotal -ge 0 }).Count
    exactOccurrenceFrozen = 0
    documents = $resolved
})
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
    $absoluteRoot = Join-Path $RepoRoot ($lineageRoot.Replace('/', '\'))
    if (-not (Test-Path $absoluteRoot)) { continue }
    $lineageFiles += @(Get-ChildItem $absoluteRoot -Recurse -File | ForEach-Object {
        [ordered]@{
            path = $_.FullName.Substring($RepoRoot.Length).TrimStart('\').Replace('\', '/')
            sha256 = (Get-FileHash -Algorithm SHA256 $_.FullName).Hash.ToLowerInvariant()
            byteLength = $_.Length
            authority = if ($lineageRoot -like '*strict-gold-v4*') { 'HISTORICAL_NON_AUTHORITATIVE' } else { 'PROVENANCE_ONLY' }
        }
    })
}
Write-Json (Join-Path $target 'lineage-inventory.v1.json') ([ordered]@{
    artifactKind = 'a99_canonical_semantic_gold_vnext_lineage_inventory'
    schemaVersion = 'a99-canonical-semantic-gold-vnext-lineage-v1'
    sourceInventory = 'eval/a99-dataset/document-inventory.v1.json'
    sourceInventorySha256 = (Get-FileHash -Algorithm SHA256 (Join-Path $RepoRoot 'eval/a99-dataset/document-inventory.v1.json')).Hash.ToLowerInvariant()
    roots = $lineageRoots
    fileCount = $lineageFiles.Count
    files = $lineageFiles
    note = 'Historical Gold, occurrence Gold, review packets and blind source packets are provenance only; registry v3 is the sole current semantic authority.'
})
Write-Json (Join-Path $target 'projections\outline-collapse-repeats.v1.json') ([ordered]@{
    artifactKind = 'a99_canonical_projection_policy'
    schemaVersion = 'a99-canonical-projection-policy-vnext-v1'
    policyId = 'OUTLINE_COLLAPSE_REPEATS_AFTER_CANONICAL_GRAPH'
    input = 'canonical semantic graph occurrences'
    preserve = @('every true heading occurrence', 'PRIMARY', 'REPEAT', 'CONTINUATION')
    collapseKey = 'semanticNodeId'
    semanticTruthMutation = $false
    taskMembershipMutation = $false
})
$readmeLines = @(
    '# A99 canonical semantic Gold vNext',
    '',
    'Canonical semantic authority migrated from freeze-registry.v3.final.json.',
    'Registry authority: 13 tracked sources, 13 FROZEN_SEMANTIC_VNEXT, 0 pending approval.',
    '',
    'CanonicalGold = ALL TRUE HEADING OCCURRENCES. Semantic freeze and exact occurrence/span',
    'freeze are separate. Occurrence and binding files are REVIEW_REQUIRED until parser-owned',
    'aliases plus verbatim exact UTF-16 binding are materialized; migration never fabricates',
    'coordinates or imports model output into Gold.',
    '',
    'Runtime contract:',
    'source -> evidence -> aliases -> semantic reasoning -> exact binder -> hard validator -> global structural resolution -> canonical semantic graph -> task projection -> output',
    '',
    'The model supplies meaning (sourceAlias, isHeading, verbatim text/parts, semantic role/type,',
    'scope and relation hints). The harness supplies coordinates. Repeated/continuation display',
    'titles remain occurrences; only the final outline projection may collapse a repeated node.',
    '',
    'Historical v4 artifacts remain immutable and are referenced as non-authoritative provenance.'
)
[System.IO.File]::WriteAllText((Join-Path $target 'README.md'), ($readmeLines -join [Environment]::NewLine) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
Write-Output "MIGRATED=$($resolved.Count) TARGET=$target"
