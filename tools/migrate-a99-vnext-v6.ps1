[CmdletBinding()]
param(
    [string]$RepoRoot = (Get-Location).Path,
    [string]$RegistryPath = 'C:\Users\btdba\Downloads\freeze-registry.v6.visual-unified.json',
    [string]$TaskPath = 'C:\Users\btdba\Downloads\TASK-a99-vnext-EXECUTE-final.v6.visual-recovery.md',
    [string]$Doc0123Input = 'C:\Users\btdba\Downloads\DOC-0123.semantic-freeze.v1.json',
    [string]$Doc0202Input = 'C:\Users\btdba\Downloads\DOC-0202.semantic-freeze.v1.json'
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
$registry = Get-Content -LiteralPath $RegistryPath -Raw | ConvertFrom-Json
if ($registry.registryRevision -ne 6 -or $registry.policyVersion -ne 'ALL_TRUE_HEADING_OCCURRENCES_VNEXT_VISUAL_RECOVERY') { throw 'REGISTRY_NOT_V6_VISUAL_UNIFIED' }
$approved = @($registry.entries | Where-Object { $_.status -eq 'FROZEN_SEMANTIC_VNEXT' -and $_.userFinalApproval })
$aggregate = [int](($approved | ForEach-Object { [int]$_.semanticHeadingTotal } | Measure-Object -Sum).Sum)
if ($approved.Count -ne 21 -or $aggregate -ne 3955 -or $registry.summary.deferredOrSkipped -ne 4) { throw "REGISTRY_SUMMARY_MISMATCH:$($approved.Count):$aggregate" }

$target = Join-Path $RepoRoot 'eval\a99-closed-loop\canonical-semantic-gold-vnext'
New-Item -ItemType Directory -Force -Path $target, (Join-Path $target 'semantic'), (Join-Path $target 'projections'), (Join-Path $target 'inputs') | Out-Null

function Write-Json([string]$Path, $Value) {
    $json = $Value | ConvertTo-Json -Depth 50
    [System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function Find-Source($entry) {
    $matches = @(Get-ChildItem -Path $RepoRoot -Recurse -File -Filter $entry.fileName |
        Where-Object { $_.FullName -notlike '*\.claude\worktrees\*' -and $_.FullName -notlike '*\.git\*' })
    if ($matches.Count -eq 0) { throw "SOURCE_NOT_FOUND:$($entry.key):$($entry.fileName)" }
    $preferred = @($matches | Where-Object { $_.FullName -like '*\heading_corpus_100\*' -or $_.FullName -like '*\heading_corpus_95_word\*' })
    if ($preferred.Count -gt 0) { return $preferred[0] }
    return $matches[0]
}

$resolved = @()
$drift = @()
foreach ($entry in $approved) {
    if ($entry.exactOccurrenceFreeze) { throw "UNEXPECTED_EXACT_OCCURRENCE_FREEZE:$($entry.key)" }
    $source = Find-Source $entry
    $sourcePath = $source.FullName.Substring($RepoRoot.Length).TrimStart('\').Replace('\', '/')
    $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $source.FullName).Hash.ToLowerInvariant()
    $hashVerified = [string]::IsNullOrWhiteSpace($entry.sourceSha256) -or $sourceHash -eq $entry.sourceSha256.ToLowerInvariant()
    if (-not $hashVerified) { $drift += [ordered]@{ authorityKey = $entry.key; expected = $entry.sourceSha256; actual = $sourceHash; status = 'SOURCE_DRIFT_BLOCKED' } }
    $semantic = [ordered]@{
        artifactKind = 'a99_canonical_semantic_freeze'
        schemaVersion = 'a99-canonical-semantic-freeze-vnext-visual-unified-v1'
        authorityKey = $entry.key
        fileName = $entry.fileName
        sourcePath = $sourcePath
        sourceSha256 = $sourceHash
        registrySourceSha256 = $entry.sourceSha256
        sourceLineageVerified = $hashVerified
        sourceLineageStatus = if ($hashVerified) { 'VERIFIED' } else { 'SOURCE_DRIFT_BLOCKED' }
        semanticHeadingTotal = [int]$entry.semanticHeadingTotal
        status = 'FROZEN_SEMANTIC_VNEXT'
        finalAuthority = 'USER_APPROVED_SOURCE_ONLY_REVIEW'
        truthDefinition = 'ALL_TRUE_HEADING_OCCURRENCES'
        semanticMembership = 'HEADING'
        promptAffectsSemanticTruth = $false
        repeatedOrContinuationMayBeHeading = $true
        newSemanticNodeRequiredForHeading = $false
        exactOccurrenceFreeze = $false
        modalityHints = [ordered]@{
            visualRecoveryRequired = [bool]($entry.key -eq 'DOC-0202')
            visualAdjudicationAvailable = $true
            machineTextMayBeInsufficient = [bool]($entry.key -eq 'DOC-0202')
        }
        capabilities = [ordered]@{
            semanticEvaluable = $true
            occurrenceEvaluable = $false
            characterSpanEvaluable = $false
            hierarchyEvaluable = if ($entry.key -eq 'DOC-0202') { $true } else { $false }
            visualBindingEvaluable = $false
        }
        sourceAliasPolicy = 'PARSER_OWNED_DETERMINISTIC_TEXT_OR_VISUAL_ALIAS_CATALOG'
        occurrenceArtifact = $null
        bindingArtifact = $null
        historicalArtifactsAreProvenanceOnly = $true
        authorityRecord = $entry
        notes = @('Semantic total is authoritative; no occurrence list or span was synthesized from the total.', 'Visual sources use VISUAL_REGION binding when exact occurrence authority is later materialized.')
    }
    Write-Json (Join-Path $target "semantic\$($entry.key).semantic-freeze.v1.json") $semantic
    $resolved += [ordered]@{ authorityKey = $entry.key; fileName = $entry.fileName; sourcePath = $sourcePath; sourceSha256 = $sourceHash; registrySourceSha256 = $entry.sourceSha256; sourceLineageVerified = $hashVerified; semanticHeadingTotal = [int]$entry.semanticHeadingTotal; semanticArtifact = "semantic/$($entry.key).semantic-freeze.v1.json"; occurrenceEvaluable = $false; characterSpanEvaluable = $false; visualBindingEvaluable = $false }
}

foreach ($input in @(@{ Key = 'DOC-0123'; Path = $Doc0123Input }, @{ Key = 'DOC-0202'; Path = $Doc0202Input })) {
    if (-not (Test-Path -LiteralPath $input.Path)) { throw "SEMANTIC_INPUT_NOT_FOUND:$($input.Key)" }
    $data = Get-Content -LiteralPath $input.Path -Raw | ConvertFrom-Json
    $entry = $approved | Where-Object key -eq $input.Key
    if ($data.key -ne $input.Key -or [int]$data.semanticHeadingTotal -ne [int]$entry.semanticHeadingTotal) { throw "SEMANTIC_INPUT_MISMATCH:$($input.Key)" }
    Copy-Item -LiteralPath $input.Path -Destination (Join-Path $target "inputs\$($input.Key).semantic-freeze.v1.json") -Force
}

if ($resolved.Count -ne 21 -or $aggregate -ne 3955) { throw 'MATERIALIZED_INVENTORY_MISMATCH' }
Copy-Item -LiteralPath $RegistryPath -Destination (Join-Path $target 'freeze-registry.v6.visual-unified.json') -Force

Write-Json (Join-Path $target 'inventory.v1.json') ([ordered]@{
    artifactKind = 'a99_canonical_semantic_gold_vnext_inventory'
    schemaVersion = 'a99-canonical-semantic-gold-vnext-inventory-v3'
    authority = 'freeze-registry.v6.visual-unified.json'
    totalTracked = 21
    frozenSemanticVnext = 21
    pendingUserApproval = 0
    deferredOrSkipped = 4
    exactOccurrenceFrozen = 0
    aggregateSemanticHeadingTotal = 3955
    sourceLineageDrift = $drift
    documents = $resolved
})
Write-Json (Join-Path $target 'migration-manifest.v1.json') ([ordered]@{
    artifactKind = 'a99_canonical_semantic_gold_vnext_migration'
    schemaVersion = 'a99-canonical-semantic-gold-vnext-migration-v3'
    authorityRegistry = 'freeze-registry.v6.visual-unified.json'
    authorityRegistrySha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $RegistryPath).Hash.ToLowerInvariant()
    executionTask = Split-Path -Leaf $TaskPath
    executionTaskSha256 = if (Test-Path -LiteralPath $TaskPath) { (Get-FileHash -Algorithm SHA256 -LiteralPath $TaskPath).Hash.ToLowerInvariant() } else { $null }
    semanticInputArtifacts = @('inputs/DOC-0123.semantic-freeze.v1.json', 'inputs/DOC-0202.semantic-freeze.v1.json')
    providerCalls = 0
    modelCalls = 0
    totalTracked = 21
    frozenSemanticVnext = 21
    aggregateSemanticHeadingTotal = 3955
    exactOccurrenceFrozen = 0
    occurrenceArtifactsCreated = 0
    bindingArtifactsCreated = 0
    sourceLineageDrift = $drift
    goldRuntimeInputs = $false
})
$lineageRoots = @('eval/a99-closed-loop/strict-gold-v3', 'eval/a99-closed-loop/strict-gold-v4', 'eval/a99-closed-loop/strict-gold-occurrence-v1', 'eval/harness-lift/review-packets', 'eval/harness-lift/review-packets-v2', 'eval/benchmark-n0/source-packets', 'eval/a99-closed-loop/research-r2')
$lineageFiles = @()
foreach ($lineageRoot in $lineageRoots) {
    $absolute = Join-Path $RepoRoot ($lineageRoot.Replace('/', '\'))
    if (Test-Path -LiteralPath $absolute) {
        $lineageFiles += @(Get-ChildItem -LiteralPath $absolute -Recurse -File | ForEach-Object {
            [ordered]@{ path = $_.FullName.Substring($RepoRoot.Length).TrimStart('\').Replace('\', '/'); sha256 = (Get-FileHash -Algorithm SHA256 $_.FullName).Hash.ToLowerInvariant(); byteLength = $_.Length; authority = 'PROVENANCE_ONLY' }
        })
    }
}
Write-Json (Join-Path $target 'lineage-inventory.v1.json') ([ordered]@{
    artifactKind = 'a99_canonical_semantic_gold_vnext_lineage_inventory'
    schemaVersion = 'a99-canonical-semantic-gold-vnext-lineage-v3'
    authorityRegistry = 'freeze-registry.v6.visual-unified.json'
    authorityRegistrySha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $RegistryPath).Hash.ToLowerInvariant()
    roots = $lineageRoots
    fileCount = $lineageFiles.Count
    files = $lineageFiles
    note = 'Legacy Gold, occurrence Gold and review artifacts remain provenance only; v6 registry is current semantic authority.'
})
$common = [ordered]@{ artifactKind = 'a99_canonical_projection_policy'; schemaVersion = 'a99-canonical-projection-policy-vnext-visual-unified-v1'; input = 'canonical semantic graph'; semanticTruthMutation = $false; taskMembershipMutation = $false; preserve = @('every true heading occurrence', 'PRIMARY', 'REPEAT', 'CONTINUATION') }
Write-Json (Join-Path $target 'projections\all-true-headings.v1.json') ($common + [ordered]@{ policyId = 'ALL_TRUE_HEADINGS'; output = 'all canonical occurrences'; collapseKey = $null })
Write-Json (Join-Path $target 'projections\main-document-outline.v1.json') ($common + [ordered]@{ policyId = 'MAIN_DOCUMENT_OUTLINE'; output = 'one occurrence per semanticNodeId'; collapseKey = 'semanticNodeId' })
Write-Json (Join-Path $target 'projections\chapter-section-article.v1.json') ($common + [ordered]@{ policyId = 'CHAPTER_SECTION_ARTICLE'; output = 'filtered task view after semantic boundary'; collapseKey = 'semanticNodeId'; allowedStructuralTypes = @('Chapter', 'Section', 'Article') })
$readme = @'
# A99 canonical semantic Gold vNext — visual unified

Current authority: `freeze-registry.v6.visual-unified.json`.

The registry freezes 21 semantic sources with 3,955 approved true heading occurrences and zero
pending approvals. Four records remain explicitly deferred/blocked. This semantic freeze is not
an exact occurrence freeze: no occurrence or binding files are created from totals alone.

Visual recovery is a first-class evidence route for scan/image pages. It uses parser/render-owned
page, image, region and transcript hashes; it never fabricates UTF-16 offsets for pixels. Text and
visual evidence are reconciled before semantic adjudication, while task projection remains after
the canonical semantic boundary.

`SRC-057` has a source-hash drift recorded in `inventory.v1.json`; its approved semantic total is
retained without pretending that the current source is lineage-verified. Resolve that drift before
materializing exact occurrence or binding authority.
'@
[System.IO.File]::WriteAllText((Join-Path $target 'README.md'), $readme.TrimEnd() + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
Write-Output "MIGRATED_V6=$($resolved.Count) AGGREGATE=3955 SOURCE_DRIFT=$($drift.Count) TARGET=$target"
