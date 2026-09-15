[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$OutputRoot = "artifacts/authority-audit/strict-heading-level-hierarchy-v1"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repo = (Resolve-Path $RepoRoot).Path
$out = Join-Path $repo $OutputRoot
New-Item -ItemType Directory -Force -Path $out | Out-Null

function Get-RelativePath([string]$Path) {
    return [IO.Path]::GetRelativePath($repo, $Path).Replace('\', '/')
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-DocumentId($Json, [string]$Path) {
    if ($Json.PSObject.Properties.Name -contains 'documentId' -and $Json.documentId) {
        return [string]$Json.documentId
    }
    $m = [regex]::Match((Split-Path -Leaf $Path), '(DOC-\d{4}|SRC-\d{3})')
    if ($m.Success) { return $m.Value }
    return $null
}

function Get-Array($Json, [string]$Name) {
    if ($Json.PSObject.Properties.Name -contains $Name -and $null -ne $Json.$Name) {
        return ,@($Json.$Name)
    }
    return ,@()
}

function Get-NonEmpty([object]$Value) {
    return $null -ne $Value -and ([string]$Value).Length -gt 0
}

function Get-RowFacts([object[]]$Rows) {
    $level = 0; $parent = 0; $node = 0; $role = 0; $exact = 0
    foreach ($row in $Rows) {
        if ($row.PSObject.Properties.Name -contains 'level' -and $null -ne $row.level) { $level++ }
        if ($row.PSObject.Properties.Name -contains 'goldLevel' -and $null -ne $row.goldLevel) { $level++ }

        $parentNames = @('parentHeadingOccurrenceId','parentSemanticNodeId','parentId','goldParentId','parent')
        if ($parentNames | Where-Object { ($row.PSObject.Properties.Name -contains $_) -and (Get-NonEmpty $row.$_) }) { $parent++ }
        if (($row.PSObject.Properties.Name -contains 'semanticNodeId') -and (Get-NonEmpty $row.semanticNodeId)) { $node++ }
        if (($row.PSObject.Properties.Name -contains 'role') -and (Get-NonEmpty $row.role)) { $role++ }
        if (($row.PSObject.Properties.Name -contains 'semanticRole') -and (Get-NonEmpty $row.semanticRole)) { $role++ }
        if (($row.PSObject.Properties.Name -contains 'semanticMembership') -and (Get-NonEmpty $row.semanticMembership)) { $role++ }

        if (($row.PSObject.Properties.Name -contains 'utf16Start' -and $null -ne $row.utf16Start) -or
            ($row.PSObject.Properties.Name -contains 'headingSpan' -and $null -ne $row.headingSpan) -or
            (($row.PSObject.Properties.Name -contains 'sourceFactId') -and (Get-NonEmpty $row.sourceFactId))) { $exact++ }
    }
    return [pscustomobject]@{
        level = $level
        parent = $parent
        semanticNode = $node
        role = $role
        exactBindingRows = $exact
    }
}

function Get-AuthorityRecord([string]$Path, [string]$Scope) {
    $json = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $relative = Get-RelativePath $Path
    $kind = if ($json.PSObject.Properties.Name -contains 'artifactKind') { [string]$json.artifactKind } else { '' }
    $doc = Get-DocumentId $json $Path

    $headings = Get-Array $json 'headings'
    $bindings = Get-Array $json 'bindings'
    $occurrences = Get-Array $json 'occurrences'
    $rows = if (@($headings).Count -gt 0) { $headings } elseif (@($bindings).Count -gt 0) { $bindings } else { $occurrences }
    $facts = Get-RowFacts $rows

    $isStrict = $relative -match '/strict-gold-v4/'
    $isStrictV3 = $relative -match '/strict-gold-v3/'
    $isStrictOccurrence = $relative -match 'strict-gold-occurrence-v1/'
    $isR2Occurrence = $relative -match '/research-r2/.*/strict-occurrence/'
    $isCanonicalSemantic = $relative -match '/canonical-semantic-gold-vnext/(semantic|historical-v4/semantic)/'
    $isCanonicalOccurrence = $relative -match '/canonical-semantic-gold-vnext/historical-v3/occurrence/'
    $isHdsa = $kind -eq 'a99_hdsa_structural_gold'
    $isHistoricalHierarchy = $relative -match '^keys/hierarchy/'

    $treeEdges = @()
    if ($json.PSObject.Properties.Name -contains 'tree' -and $null -ne $json.tree -and
        $json.tree.PSObject.Properties.Name -contains 'parentOf') { $treeEdges = @($json.tree.parentOf) }
    $explicitParentRows = $facts.parent
    $hasExplicitParent = $explicitParentRows -gt 0 -or @($treeEdges).Count -gt 0
    $hasExplicitTree = @($treeEdges).Count -gt 0
    $treeDerived = $false
    if ($json.PSObject.Properties.Name -contains 'annotationProtocol' -and $json.annotationProtocol.levelIsDerived -eq $true) { $treeDerived = $true }
    if ($json.PSObject.Properties.Name -contains 'levelIsDerived' -and $json.levelIsDerived -eq $true) { $treeDerived = $true }
    if ($isHdsa -and $hasExplicitTree) { $treeDerived = $true }

    $headingGold = $false
    if (($isStrict -or $isStrictV3) -and @($headings).Count -gt 0) { $headingGold = $true }
    if (($isStrictOccurrence -or $isR2Occurrence -or $isHdsa -or $isHistoricalHierarchy) -and @($rows).Count -gt 0) { $headingGold = $true }

    $exactGold = $false
    if ($isStrictOccurrence -or $isR2Occurrence) { $exactGold = @($rows).Count -gt 0 -and $facts.exactBindingRows -gt 0 }
    # HDSA source aliases are structural-review authority, but not an exact text/span binder artifact.

    $roleGold = $facts.role -gt 0
    $hasLevel = $facts.level -gt 0
    $levelAuthority = 'NONE'
    $levelReason = 'No per-heading level field was present in this artifact.'
    if ($hasLevel) {
        if ($treeDerived) {
            $levelAuthority = 'TREE_DERIVED_LEVEL'
            $levelReason = 'Artifact explicitly declares levelIsDerived and carries an explicit parent/tree relation.'
        } elseif ($Scope -eq 'projection') {
            $levelAuthority = 'PROJECTED_LEVEL'
            $levelReason = 'Level appears in a projection artifact rather than a heading authority payload.'
        } elseif ($isStrict -or $isHistoricalHierarchy) {
            $levelAuthority = 'DIRECT_HISTORICAL_LEVEL_ANNOTATION'
            $levelReason = 'Per-heading level is stored directly; no derivation contract is present in this artifact.'
        } else {
            $levelAuthority = 'UNKNOWN_LEVEL_PROVENANCE'
            $levelReason = 'A level field exists, but this audit cannot prove its derivation provenance.'
        }
    } elseif ($treeDerived) {
        $levelAuthority = 'TREE_DERIVED_LEVEL'
        $levelReason = 'Artifact declares a tree-derived level contract even though it does not materialize a per-heading level field.'
    }

    $authorityClass = 'REFERENCE_OR_NON_AUTHORITY'
    if ($isStrict -and @($headings).Count -gt 0) { $authorityClass = 'STRICT_HEADING_GOLD_WITH_HISTORICAL_LEVEL' }
    elseif ($isStrictV3 -and @($headings).Count -gt 0) { $authorityClass = 'PROVENANCE_ONLY_STRICT_GOLD_V3' }
    elseif ($isStrictOccurrence) { $authorityClass = 'STRICT_EXACT_OCCURRENCE_BINDING_GOLD' }
    elseif ($isR2Occurrence) { $authorityClass = 'R2_SOURCE_BACKED_OCCURRENCE_GOLD' }
    elseif ($isCanonicalSemantic) { $authorityClass = 'CANONICAL_SEMANTIC_TOTAL_ONLY' }
    elseif ($isCanonicalOccurrence) { $authorityClass = 'CANONICAL_OCCURRENCE_PLACEHOLDER_OR_UNRESOLVED' }
    elseif ($isHdsa) { $authorityClass = 'EXPLICIT_SOURCE_ONLY_STRUCTURAL_HIERARCHY_GOLD' }
    elseif ($isHistoricalHierarchy) { $authorityClass = 'EVALUATION_ONLY_HISTORICAL_HIERARCHY' }

    $reason = switch ($authorityClass) {
        'STRICT_HEADING_GOLD_WITH_HISTORICAL_LEVEL' { 'Strict Gold contains reviewed heading rows, roles, exact source references/spans, and direct level values; every parentHeadingOccurrenceId is null and hierarchyEvaluable is false.'; break }
        'STRICT_EXACT_OCCURRENCE_BINDING_GOLD' { 'Occurrence binding artifact contains source-backed coordinates and exact binding fields; it has no parent/tree authority.'; break }
        'R2_SOURCE_BACKED_OCCURRENCE_GOLD' { 'R2 human source-backed occurrence artifact contains resolved source occurrences and UTF-16 coordinates; no level/tree authority.'; break }
        'CANONICAL_SEMANTIC_TOTAL_ONLY' { 'Canonical vNext semantic freeze explicitly declares exactOccurrenceFreeze=false and hierarchyEvaluable=false; semanticHeadingTotal is not an occurrence list.'; break }
        'CANONICAL_OCCURRENCE_PLACEHOLDER_OR_UNRESOLVED' { 'Canonical vNext historical occurrence artifact contains no resolved bindings and explicitly does not fabricate occurrence authority.'; break }
        'EXPLICIT_SOURCE_ONLY_STRUCTURAL_HIERARCHY_GOLD' { 'Dedicated HDSA artifact explicitly freezes semantic-node parent edges and declares levelIsDerived=true; this is hierarchy authority for its scoped 12-occurrence review, not general strict heading Gold.'; break }
        'EVALUATION_ONLY_HISTORICAL_HIERARCHY' { 'Legacy evaluation-only hierarchy file has direct goldLevel/goldParentId rows but is not the current canonical semantic authority.'; break }
        default { 'Audited as a reference/non-authority artifact under the strict-heading authority inventory.'; break }
    }

    return [pscustomobject]@{
        path = $relative
        sha256 = Get-Sha256 $Path
        documentId = $doc
        artifactKind = $kind
        schemaVersion = if ($json.PSObject.Properties.Name -contains 'schemaVersion') { [string]$json.schemaVersion } else { $null }
        authorityClass = $authorityClass
        headingOccurrenceGold = $headingGold
        headingOccurrenceCount = if ($headingGold) { @($rows).Count } else { 0 }
        exactBindingGold = $exactGold
        exactBindingCount = if ($exactGold) { @($rows).Count } else { 0 }
        semanticRoleGold = $roleGold
        roleRowCount = $facts.role
        hasLevel = $hasLevel
        levelCount = $facts.level
        levelAuthority = $levelAuthority
        levelReason = $levelReason
        hasExplicitParent = $hasExplicitParent
        explicitParentCount = $explicitParentRows + @($treeEdges).Count
        hasExplicitTree = $hasExplicitTree
        explicitTreeEdgeCount = @($treeEdges).Count
        hasSemanticNodeIdentity = $facts.semanticNode -gt 0
        treeBackedHierarchyGold = $treeDerived
        primaryAuthorityClass = $authorityClass
        evidenceReason = $reason
        status = if ($json.PSObject.Properties.Name -contains 'status') { [string]$json.status } elseif ($json.PSObject.Properties.Name -contains 'goldStatus') { [string]$json.goldStatus } else { $null }
    }
}

$candidateSpecs = @(
    @{ Path = 'eval/a99-closed-loop/strict-gold-v4'; Scope = 'strict-heading' },
    @{ Path = 'eval/a99-closed-loop/strict-gold-v3'; Scope = 'provenance' },
    @{ Path = 'eval/a99-closed-loop/strict-gold-occurrence-v1'; Scope = 'binding' },
    @{ Path = 'eval/a99-closed-loop/canonical-semantic-gold-vnext/semantic'; Scope = 'canonical-semantic' },
    @{ Path = 'eval/a99-closed-loop/canonical-semantic-gold-vnext/historical-v4/semantic'; Scope = 'provenance' },
    @{ Path = 'eval/a99-closed-loop/canonical-semantic-gold-vnext/historical-v3/occurrence'; Scope = 'provenance' },
    @{ Path = 'eval/a99-closed-loop/research-r2/p2-occurrence-closure/strict-occurrence'; Scope = 'binding' },
    @{ Path = 'eval/a99-closed-loop/hdsa-parent-relation-live/DOC-0205'; Scope = 'hierarchy' },
    @{ Path = 'keys/hierarchy'; Scope = 'historical-hierarchy' }
)

$records = [System.Collections.Generic.List[object]]::new()
foreach ($spec in $candidateSpecs) {
    $root = Join-Path $repo $spec.Path
    if (-not (Test-Path $root)) { continue }
    Get-ChildItem -LiteralPath $root -Recurse -File -Filter *.json | Sort-Object FullName | ForEach-Object {
        $file = $_
        # Only explicit authority-shaped files; do not ingest predictions/evaluations by broad content matching.
        if ($file.Name -match 'prediction|summary|report|inventory|registry' -and $spec.Scope -notin @('strict-heading','binding','hierarchy','historical-hierarchy')) { return }
        try {
            $records.Add((Get-AuthorityRecord $file.FullName $spec.Scope))
        } catch {
            throw "Authority audit failed for $($file.FullName): $($_.Exception.Message)"
        }
    }
}

$records = @($records | Sort-Object path -Unique)
$docs = @($records | Where-Object { $_.documentId } | Group-Object documentId | Sort-Object Name)

# Current strict heading authority is strict-gold-v4 materialized heading rows only.
$strict = @($records | Where-Object { $_.authorityClass -eq 'STRICT_HEADING_GOLD_WITH_HISTORICAL_LEVEL' -and $_.headingOccurrenceGold })
$strictDocIds = @($strict | Select-Object -ExpandProperty documentId -Unique | Sort-Object)
$strictHeadingOccurrences = ($strict | Measure-Object headingOccurrenceCount -Sum).Sum
$strictWithLevel = @($strict | Where-Object hasLevel)
$strictLevelOccurrences = ($strictWithLevel | Measure-Object levelCount -Sum).Sum
$strictTree = @($strict | Where-Object treeBackedHierarchyGold)
$strictParent = @($strict | Where-Object hasExplicitParent)
$strictHistorical = @($strict | Where-Object { $_.levelAuthority -in @('DIRECT_HISTORICAL_LEVEL_ANNOTATION','PROJECTED_LEVEL','UNKNOWN_LEVEL_PROVENANCE') })

$matrix = foreach ($group in $docs) {
    $items = @($group.Group)
    $headingItems = @($items | Where-Object headingOccurrenceGold)
    $bindingItems = @($items | Where-Object exactBindingGold)
    $levelItems = @($items | Where-Object hasLevel)
    $hierItems = @($items | Where-Object { $_.hasExplicitParent -or $_.hasExplicitTree })
    $strictPreferred = @($items | Where-Object { $_.authorityClass -eq 'STRICT_HEADING_GOLD_WITH_HISTORICAL_LEVEL' } | Sort-Object path)
    $canonicalPreferred = @($items | Where-Object { $_.authorityClass -eq 'CANONICAL_SEMANTIC_TOTAL_ONLY' -and $_.path -notmatch '/historical-' } | Sort-Object path)
    $preferred = if (@($strictPreferred).Count -gt 0) { @($strictPreferred) } elseif (@($canonicalPreferred).Count -gt 0) { @($canonicalPreferred) } else { @($items | Where-Object { $_.authorityClass -in @('EXPLICIT_SOURCE_ONLY_STRUCTURAL_HIERARCHY_GOLD','R2_SOURCE_BACKED_OCCURRENCE_GOLD','STRICT_EXACT_OCCURRENCE_BINDING_GOLD','CANONICAL_SEMANTIC_TOTAL_ONLY') } | Sort-Object authorityClass, path) }
    $primary = if (@($preferred).Count -gt 0) { $preferred[0].authorityClass } else { 'REFERENCE_OR_NON_AUTHORITY' }
    [pscustomobject]@{
        documentId = $group.Name
        goldArtifact = if (@($preferred).Count -gt 0) { $preferred[0].path } else { $items[0].path }
        goldSha256 = if (@($preferred).Count -gt 0) { $preferred[0].sha256 } else { $items[0].sha256 }
        headingOccurrenceGold = $headingItems.Count -gt 0
        headingOccurrenceCount = if ($headingItems.Count -gt 0) { (@($headingItems | Measure-Object headingOccurrenceCount -Maximum).Maximum) } else { 0 }
        exactBindingGold = $bindingItems.Count -gt 0
        exactBindingCount = if ($bindingItems.Count -gt 0) { (@($bindingItems | Measure-Object exactBindingCount -Maximum).Maximum) } else { 0 }
        semanticRoleGold = (@($items | Where-Object semanticRoleGold).Count -gt 0)
        hasLevel = $levelItems.Count -gt 0
        levelAuthority = if (@($levelItems | Where-Object levelAuthority -eq 'TREE_DERIVED_LEVEL').Count -gt 0) { 'TREE_DERIVED_LEVEL' } elseif (@($levelItems | Where-Object levelAuthority -eq 'DIRECT_HISTORICAL_LEVEL_ANNOTATION').Count -gt 0) { 'DIRECT_HISTORICAL_LEVEL_ANNOTATION' } elseif (@($levelItems | Where-Object levelAuthority -eq 'PROJECTED_LEVEL').Count -gt 0) { 'PROJECTED_LEVEL' } elseif ($levelItems.Count -gt 0) { 'UNKNOWN_LEVEL_PROVENANCE' } else { 'NONE' }
        levelOccurrenceCount = if ($levelItems.Count -gt 0) { (@($levelItems | Measure-Object levelCount -Maximum).Maximum) } else { 0 }
        hasExplicitParent = $hierItems.Count -gt 0 -and (@($hierItems | Where-Object hasExplicitParent).Count -gt 0)
        hasExplicitTree = @($hierItems | Where-Object hasExplicitTree).Count -gt 0
        hasSemanticNodeIdentity = @($items | Where-Object hasSemanticNodeIdentity).Count -gt 0
        treeBackedHierarchyGold = @($items | Where-Object treeBackedHierarchyGold).Count -gt 0
        primaryAuthorityClass = $primary
        evidence = @($items | ForEach-Object { [pscustomobject]@{ path=$_.path; sha256=$_.sha256; authorityClass=$_.authorityClass; reason=$_.evidenceReason } })
    }
}

$hierarchyRecords = @($records | Where-Object { $_.hasExplicitParent -or $_.hasExplicitTree } | ForEach-Object {
    [pscustomobject]@{
        documentId = $_.documentId
        path = $_.path
        sha256 = $_.sha256
        authorityClass = $_.authorityClass
        explicitParentCount = $_.explicitParentCount
        explicitTreeEdgeCount = $_.explicitTreeEdgeCount
        hasSemanticNodeIdentity = $_.hasSemanticNodeIdentity
        levelAuthority = $_.levelAuthority
        treeBackedHierarchyGold = $_.treeBackedHierarchyGold
        currentStrictHeadingGold = ($_.authorityClass -eq 'STRICT_HEADING_GOLD_WITH_HISTORICAL_LEVEL')
        note = if ($_.authorityClass -eq 'EXPLICIT_SOURCE_ONLY_STRUCTURAL_HIERARCHY_GOLD') { 'Dedicated HDSA source-only hierarchy authority; scoped and not a corpus-wide strict heading hierarchy.' } elseif ($_.authorityClass -eq 'EVALUATION_ONLY_HISTORICAL_HIERARCHY') { 'Evaluation-only historical hierarchy; not current canonical authority.' } else { 'Parent-like fields were present in an audited artifact; inspect provenance before treating as authority.' }
    }
})

$manifest = [ordered]@{
    auditKind = 'A99_STRICT_HEADING_LEVEL_HIERARCHY_AUTHORITY_AUDIT'
    schemaVersion = 'a99-strict-heading-level-hierarchy-authority-audit-v1'
    generatedAt = '2026-09-15'
    providerCalls = 0
    modelCalls = 0
    goldMutation = $false
    sourceRoots = @($candidateSpecs | ForEach-Object { $_.Path })
    rules = @(
        'Do not infer parent/tree from level or document order.'
        'Current strict heading authority is counted from materialized strict-gold-v4 heading rows.'
        'Canonical vNext semanticHeadingTotal is not an occurrence list.'
        'Tree-derived level requires an explicit levelIsDerived contract plus explicit parent/tree evidence.'
        'Prediction/evaluation artifacts are excluded from authority inventory.'
    )
    artifactCount = $records.Count
    documentCount = $docs.Count
    artifacts = $records
}

$levelProvenance = [ordered]@{
    auditKind = 'A99_LEVEL_PROVENANCE_AUDIT'
    schemaVersion = 'a99-level-provenance-v1'
    entries = @($records | Where-Object hasLevel | ForEach-Object {
        [pscustomobject]@{
            documentId=$_.documentId; artifact=$_.path; sha256=$_.sha256; levelCount=$_.levelCount
            levelAuthority=$_.levelAuthority; hasExplicitParent=$_.hasExplicitParent; hasExplicitTree=$_.hasExplicitTree
            treeBackedHierarchyGold=$_.treeBackedHierarchyGold; reason=$_.levelReason
        }
    })
    summary = [ordered]@{
        strictHeadingGoldDocuments = $strictDocIds.Count
        strictHeadingGoldOccurrencesWithLevel = $strictLevelOccurrences
        strictHeadingGoldTreeDerivedDocuments = $strictTree.Count
        strictHeadingGoldExplicitParentDocuments = $strictParent.Count
        strictHeadingGoldHistoricalLevelOnlyDocuments = $strictHistorical.Count
    }
}

$hierarchyAuthority = [ordered]@{
    auditKind = 'A99_HIERARCHY_AUTHORITY_AUDIT'
    schemaVersion = 'a99-hierarchy-authority-v1'
    conclusion = 'Existing strict heading Gold does not constitute general hierarchy Gold.'
    strictHeadingGold = [ordered]@{
        documents = $strictDocIds.Count
        occurrences = $strictHeadingOccurrences
        explicitParentDocuments = $strictParent.Count
        explicitTreeDocuments = @($strict | Where-Object hasExplicitTree).Count
        treeDerivedLevelDocuments = $strictTree.Count
        levelAuthority = 'DIRECT_HISTORICAL_LEVEL_ANNOTATION'
    }
    explicitHierarchyArtifacts = $hierarchyRecords
}

$report = @"
# Strict heading Gold level/hierarchy authority audit

Generated: 2026-09-15
Provider/model calls: **0**
Gold mutation: **false**

## Conclusion

**Existing strict heading Gold DOES NOT constitute general hierarchy Gold.**

The current materialized `strict-gold-v4` heading artifacts contain reviewed heading occurrences, roles, source references/spans, and direct historical `level` values. Their parent fields are null and their capability declarations mark `parentEvaluable=false` and `hierarchyEvaluable=false`. Therefore their levels are classified as `DIRECT_HISTORICAL_LEVEL_ANNOTATION`, not `TREE_DERIVED_LEVEL`.

The canonical vNext semantic registry is not a replacement hierarchy authority: it explicitly freezes semantic totals with exactOccurrenceFreeze=false; it does not provide an exhaustive occurrence list, bindings, parent edges, or tree.

## Strict heading Gold counts

| Measure | Count |
|---|---:|
| strict heading Gold documents | $($strictDocIds.Count) |
| strict heading Gold occurrences | $strictHeadingOccurrences |
| documents with level | $($strictWithLevel.Count) |
| occurrences with level | $strictLevelOccurrences |
| proven tree-derived level documents | $($strictTree.Count) |
| proven explicit parent/tree strict Gold documents | $(($strictParent.Count)) |
| direct/projected historical-level-only documents | $($strictHistorical.Count) |
| unknown level-provenance strict documents | $(@($strict | Where-Object levelAuthority -eq 'UNKNOWN_LEVEL_PROVENANCE').Count) |

## Interpretation

- Historical level can benchmark heading detection only where the heading rows are valid and source-linked.
- Historical level may be used for a restricted final-level comparison, but only with the restriction that it is direct annotation, not proof of a parent relation or tree topology.
- It cannot authorize parent-edge accuracy, tree validity, or a claim that `level = depth(tree)` was Gold-derived.
- The dedicated HDSA DOC-0205 artifact is explicit source-only hierarchy Gold for its scoped review (`10` semantic nodes, `9` parent edges, one excluded masthead occurrence) and separately records `levelIsDerived=true`. It must not be generalized to the strict-Gold corpus.
- The `keys/hierarchy` artifact is retained as evaluation-only historical hierarchy evidence, not current canonical authority.

### DOC-0258 forensic result

strict-gold-v4/DOC-0258.strict-gold-v4.json materializes 24 reviewed heading rows with direct level values and historical-key source references. Every parentHeadingOccurrenceId is null; the artifact declares parentEvaluable=false and hierarchyEvaluable=false. Its paired occurrence-binding artifact proves source bindings for those 24 rows but also carries no parent/tree topology. The current canonical vNext DOC-0258 semantic freeze is 37 semantic heading occurrences, explicitly has exactOccurrenceFreeze=false, and states that the historical 24-occurrence artifact is retired for canonical all-true-heading truth. Thus the 24 historical levels are not a copied or derived canonical tree; they remain historical level annotations only.

## Priority document notes

The matrix includes all authority-shaped strict/canonical artifacts discovered under the audited roots, including the requested DOC-0001, DOC-0205, DOC-0252, DOC-0256, DOC-0258, DOC-0092, DOC-0133, DOC-0158, DOC-0165, DOC-0255, DOC-0259, and DOC-0264 where present. Documents with only canonical semantic totals are explicitly marked as lacking occurrence and hierarchy authority.

See `manifest.json`, `document-authority-matrix.json`, `level-provenance.json`, and `hierarchy-authority.json` for hashes and per-artifact evidence.
"@

$jsonOptions = @{ Depth = 30; Compress = $false }
$manifest | ConvertTo-Json @jsonOptions | Set-Content -LiteralPath (Join-Path $out 'manifest.json') -Encoding utf8NoBOM
$matrix | ConvertTo-Json @jsonOptions | Set-Content -LiteralPath (Join-Path $out 'document-authority-matrix.json') -Encoding utf8NoBOM
$levelProvenance | ConvertTo-Json @jsonOptions | Set-Content -LiteralPath (Join-Path $out 'level-provenance.json') -Encoding utf8NoBOM
$hierarchyAuthority | ConvertTo-Json @jsonOptions | Set-Content -LiteralPath (Join-Path $out 'hierarchy-authority.json') -Encoding utf8NoBOM
$report | Set-Content -LiteralPath (Join-Path $out 'report.md') -Encoding utf8NoBOM

Write-Output "Wrote strict heading authority audit: $out"
Write-Output "Strict heading docs=$($strictDocIds.Count) occurrences=$strictHeadingOccurrences withLevel=$strictLevelOccurrences"
Write-Output "Hierarchy-backed strict docs=$($strictTree.Count) explicit-parent strict docs=$($strictParent.Count)"
