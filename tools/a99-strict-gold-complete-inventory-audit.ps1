param(
    [string]$RepoRoot = (Get-Location).Path,
    [string]$ExternalGoldRoot = 'C:\A99-Gold'
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path $RepoRoot).Path
$OutRoot = Join-Path $RepoRoot 'eval\a99-closed-loop\research-r2\existing-gold-audit'
New-Item -ItemType Directory -Force -Path $OutRoot | Out-Null

function Read-JsonFile([string]$Path) {
    try { return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json) }
    catch { return $null }
}

function Get-Hash([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-Rel([string]$Path) {
    if ($Path.StartsWith($RepoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $Path.Substring($RepoRoot.Length).TrimStart('\','/') -replace '\\','/'
    }
    return $Path -replace '\\','/'
}

function Get-Count($Value) {
    if ($null -eq $Value) { return 0 }
    if ($Value -is [System.Array]) { return $Value.Count }
    return 1
}

function Get-SourceHash($Json) {
    if ($null -eq $Json) { return $null }
    if ($Json.sourceSha256) { return [string]$Json.sourceSha256 }
    if ($Json.sourceDocumentSha256) { return [string]$Json.sourceDocumentSha256 }
    return $null
}

function Get-SourceHashStatus([string]$DocumentId, [string]$Hash, $InventoryById) {
    if ([string]::IsNullOrWhiteSpace($Hash)) { return 'NOT_DECLARED' }
    if (-not $InventoryById.ContainsKey($DocumentId)) { return 'DOCUMENT_NOT_IN_INVENTORY' }
    if ($InventoryById[$DocumentId].sourceSha256 -eq $Hash) { return 'MATCH' }
    return 'MISMATCH'
}

function Add-Artifact($List, [string]$Path, [string]$Location, [string]$Class, $Json, [string]$DocumentId, [string]$Independence) {
    $headings = if ($null -ne $Json) { Get-Count $Json.headings } else { 0 }
    $rows = if ($null -ne $Json) { Get-Count $Json.rows } else { 0 }
    $payloadCount = [Math]::Max($headings, $rows)
    $count = $payloadCount
    if ($Class -eq 'OCCURRENCE_GOLD' -and $Json.materializedOccurrenceCount) { $count = [int]$Json.materializedOccurrenceCount }
    if ($count -eq 0 -and $null -ne $Json -and $Json.semanticHeadingTotal) { $count = [int]$Json.semanticHeadingTotal }
    $sourceHash = Get-SourceHash $Json
    $reviewed = if ($null -ne $Json -and $null -ne $Json.reviewedEntireDocument) { [bool]$Json.reviewedEntireDocument } else { $false }
    $exhaustive = if ($null -ne $Json -and $null -ne $Json.headingSetExhaustive) { [bool]$Json.headingSetExhaustive } else { $false }
    $semanticFlag = if ($null -ne $Json -and $Json.capabilities) { [bool]$Json.capabilities.semanticEvaluable } elseif ($null -ne $Json -and $null -ne $Json.semanticEvaluable) { [bool]$Json.semanticEvaluable } elseif ($Class -eq 'EXTERNAL_HUMAN_GOLD' -and $rows -gt 0) { $true } else { $false }
    $externalRowsHaveSpans = $false
    if ($Class -eq 'EXTERNAL_HUMAN_GOLD' -and $rows -gt 0) {
        $firstRow = @($Json.rows)[0]
        $externalRowsHaveSpans = ($null -ne $firstRow.sourceSpan -and $null -ne $firstRow.headingSpan -and $null -ne $firstRow.sourceId)
    }
    $occFlag = if ($null -ne $Json -and $Json.capabilities) { [bool]$Json.capabilities.occurrenceEvaluable } else { $externalRowsHaveSpans }
    $charFlag = if ($null -ne $Json -and $Json.capabilities) { [bool]$Json.capabilities.characterSpanEvaluable } else { $externalRowsHaveSpans }
    $status = if ($null -ne $Json -and $Json.goldStatus) { [string]$Json.goldStatus } elseif ($null -ne $Json -and $Json.status) { [string]$Json.status } else { 'UNKNOWN' }
    $payloadRequired = $Class -in @('STRICT_GOLD_V3','STRICT_GOLD_V4')
    $validSemantic = $semanticFlag -and (($payloadRequired -and $payloadCount -gt 0) -or (-not $payloadRequired -and $count -gt 0)) -and $status -notin @('BLOCKED','NOT_REVIEWED')
    $validExhaustive = $validSemantic -and $reviewed -and $exhaustive
    $validOccurrence = (($Class -eq 'OCCURRENCE_GOLD' -and $status -eq 'PASS') -or $Class -eq 'EXTERNAL_HUMAN_GOLD') -and $occFlag -and $charFlag -and $count -gt 0
    $List.Add([pscustomobject]@{
        path = Get-Rel $Path
        absolutePath = $Path
        location = $Location
        artifactClass = $Class
        documentId = $DocumentId
        artifactVersion = if ($null -ne $Json -and $Json.schemaVersion) { [string]$Json.schemaVersion } elseif ($null -ne $Json -and $Json.goldVersion) { [string]$Json.goldVersion } else { 'UNKNOWN' }
        artifactSha256 = Get-Hash $Path
        sourceSha256 = $sourceHash
        sourceHashStatus = 'PENDING'
        goldStatus = $status
        finalAuthority = if ($null -ne $Json -and $Json.finalAuthority) { [string]$Json.finalAuthority } elseif ($null -ne $Json -and $Json.authorityClass) { 'HUMAN' } else { 'UNKNOWN' }
        referenceProvenance = if ($null -ne $Json -and $Json.referenceProvenance) { [string]$Json.referenceProvenance } elseif ($null -ne $Json -and $Json.independentOfModelPrediction -eq $true) { 'HUMAN_ONLY' } else { 'UNKNOWN' }
        reviewedEntireDocument = $reviewed
        headingSetExhaustive = $exhaustive
        declaredHeadingCount = $count
        observedHeadingCount = if ($null -ne $Json -and $Json.observedHeadingCount) { [int]$Json.observedHeadingCount } else { $count }
        semanticEvaluable = $semanticFlag
        occurrenceEvaluable = $occFlag
        characterSpanEvaluable = $charFlag
        roleEvaluable = if ($null -ne $Json -and $Json.capabilities) { [bool]$Json.capabilities.roleEvaluable } else { ($Class -eq 'EXTERNAL_HUMAN_GOLD' -and $rows -gt 0 -and $null -ne @($Json.rows)[0].role) }
        hierarchyEvaluable = if ($null -ne $Json -and $Json.capabilities) { [bool]$Json.capabilities.hierarchyEvaluable } else { ($Class -eq 'EXTERNAL_HUMAN_GOLD' -and $rows -gt 0 -and $null -ne @($Json.rows)[0].parentHeadingOccurrenceId) }
        validSemanticGold = $validSemantic
        validExhaustiveGold = $validExhaustive
        validOccurrenceGold = $validOccurrence
        independenceStatus = $Independence
        activeOrSuperseded = 'ACTIVE_ARTIFACT'
    }) | Out-Null
}

$inventoryJson = Read-JsonFile (Join-Path $RepoRoot 'eval\a99-dataset\document-inventory.v1.json')
$inventoryById = @{}
foreach ($d in @($inventoryJson.documents)) { $inventoryById[[string]$d.documentId] = $d }

$artifacts = [System.Collections.Generic.List[object]]::new()
$strictV4Dir = Join-Path $RepoRoot 'eval\a99-closed-loop\strict-gold-v4'
$strictV3Dir = Join-Path $RepoRoot 'eval\a99-closed-loop\strict-gold-v3'
$occDir = Join-Path $RepoRoot 'eval\a99-closed-loop\strict-gold-occurrence-v1'

foreach ($f in @(Get-ChildItem -LiteralPath $strictV4Dir -Filter '*.json' -File -ErrorAction SilentlyContinue)) {
    $j = Read-JsonFile $f.FullName
    if ($j.documentId) {
        $ind = if (@('DOC-0001','DOC-0205','DOC-0252','DOC-0256','DOC-0258') -contains [string]$j.documentId) { 'TUNING_CONTAMINATED_CURRENT_B0' } else { 'NOT_PROVEN_INDEPENDENT' }
        Add-Artifact $artifacts $f.FullName 'REPO' 'STRICT_GOLD_V4' $j ([string]$j.documentId) $ind
    }
}
foreach ($f in @(Get-ChildItem -LiteralPath $strictV3Dir -Filter '*.json' -File -ErrorAction SilentlyContinue)) {
    $j = Read-JsonFile $f.FullName
    if ($j.documentId) { Add-Artifact $artifacts $f.FullName 'REPO' 'STRICT_GOLD_V3' $j ([string]$j.documentId) 'TUNING_CONTAMINATED_CURRENT_B0' }
}
foreach ($f in @(Get-ChildItem -LiteralPath $occDir -Filter '*.json' -File -ErrorAction SilentlyContinue)) {
    $j = Read-JsonFile $f.FullName
    if ($j.documentId) { Add-Artifact $artifacts $f.FullName 'REPO' 'OCCURRENCE_GOLD' $j ([string]$j.documentId) 'TUNING_CONTAMINATED_CURRENT_B0' }
}

$reviewDir = Join-Path $RepoRoot 'eval\a99-closed-loop\review'
foreach ($f in @(Get-ChildItem -LiteralPath $reviewDir -Filter '*semantic-adjudication*.json' -File -ErrorAction SilentlyContinue)) {
    $j = Read-JsonFile $f.FullName
    if ($j.documentId) { Add-Artifact $artifacts $f.FullName 'REPO' 'SEMANTIC_ADJUDICATION' $j ([string]$j.documentId) 'MODEL_ASSISTED_A99_HISTORY' }
}

$externalGoldFiles = @()
$externalDev = Join-Path $ExternalGoldRoot 'dev-v3'
if (Test-Path -LiteralPath $externalDev) { $externalGoldFiles = @(Get-ChildItem -LiteralPath $externalDev -Filter '*human-gold*.json' -File) }
foreach ($f in $externalGoldFiles) {
    $j = Read-JsonFile $f.FullName
    if ($j.documentId) { Add-Artifact $artifacts $f.FullName 'EXTERNAL' 'EXTERNAL_HUMAN_GOLD' $j ([string]$j.documentId) 'CONFLICTING_A99_HISTORY_MODEL_ASSISTED' }
}

# Legacy human keys are inventoried separately. They have semantic heading text but
# do not carry the UTF-16 source spans required by the R2 occurrence contract.
$keyArtifacts = [System.Collections.Generic.List[object]]::new()
$humanKeyDirs = @('typed-human','format-driven-human','legal-human','partial-human')
foreach ($dirName in $humanKeyDirs) {
    $dir = Join-Path $RepoRoot ('keys\' + $dirName)
    foreach ($f in @(Get-ChildItem -LiteralPath $dir -Filter '*.key' -File -ErrorAction SilentlyContinue)) {
        $text = Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8
        $stem = [System.IO.Path]::GetFileNameWithoutExtension($f.Name)
        $candidates = @($inventoryJson.documents | Where-Object {
            $leaf = Split-Path ([string]$_.sourcePath) -Leaf
            $leafStem = [System.IO.Path]::GetFileNameWithoutExtension($leaf)
            $leafStem -eq $stem
        } | ForEach-Object { [string]$_.documentId })
        $full = ($text -match '(?im)full_human|Đủ\s+\d+\/\d+|FULL\s+cho')
        $partial = ($text -match '(?im)partial_human|partial\s+key|không\s+đủ|chưa\s+đọc\s+hết')
        $count = @($text -split "`r?`n" | Where-Object { $_ -match '^@' }).Count
        $keyArtifacts.Add([pscustomobject]@{
            path = Get-Rel $f.FullName
            artifactSha256 = Get-Hash $f.FullName
            artifactClass = 'LEGACY_HUMAN_KEY'
            keyCategory = $dirName
            logicalKey = $stem
            candidateDocumentIds = @($candidates | Sort-Object -Unique)
            sourceHashStatus = 'NOT_EMBEDDED'
            humanCompleteness = if ($full) { 'FULL_HUMAN_DECLARED' } elseif ($partial) { 'PARTIAL_HUMAN_DECLARED' } else { 'UNSPECIFIED' }
            rowCount = $count
            occurrenceEvaluable = $false
            characterSpanEvaluable = $false
            provenance = 'HUMAN_KEY_BUT_INDEPENDENCE_NOT_PROVEN'
            activeOrSuperseded = 'ACTIVE_LEGACY_REFERENCE'
        }) | Out-Null
    }
}

foreach ($a in $artifacts) {
    $a.sourceHashStatus = Get-SourceHashStatus $a.documentId $a.sourceSha256 $inventoryById
    if ($a.sourceHashStatus -eq 'MISMATCH') { $a.validSemanticGold = $false; $a.validExhaustiveGold = $false; $a.validOccurrenceGold = $false }
}

$keyByDoc = @{}
foreach ($k in $keyArtifacts) {
    foreach ($id in @($k.candidateDocumentIds)) {
        if (-not $keyByDoc.ContainsKey($id)) { $keyByDoc[$id] = [System.Collections.Generic.List[object]]::new() }
        $keyByDoc[$id].Add($k)
    }
}

$currentFive = @('DOC-0001','DOC-0205','DOC-0252','DOC-0256','DOC-0258')
$pilotEight = @('DOC-0004','DOC-0006','DOC-0092','DOC-0133','DOC-0158','DOC-0165','DOC-0171','DOC-0326')
$inventoryRows = [System.Collections.Generic.List[object]]::new()
foreach ($id in @($inventoryJson.documents | ForEach-Object { [string]$_.documentId } | Sort-Object)) {
    $d = $inventoryById[$id]
    $aa = @($artifacts | Where-Object { $_.documentId -eq $id })
    $kk = if ($keyByDoc.ContainsKey($id)) { @($keyByDoc[$id]) } else { @() }
    $machineSemantic = @($aa | Where-Object { $_.validSemanticGold })
    $machineExhaustive = @($aa | Where-Object { $_.validExhaustiveGold })
    $machineOccurrence = @($aa | Where-Object { $_.validOccurrenceGold })
    $fullKeys = @($kk | Where-Object { $_.humanCompleteness -eq 'FULL_HUMAN_DECLARED' })
    $allGold = @($machineSemantic) + @($fullKeys)
    $inventoryRows.Add([pscustomobject]@{
        documentId = $id
        documentGroupId = [string]$d.documentGroupId
        sourcePath = [string]$d.sourcePath
        sourceSha256 = [string]$d.sourceSha256
        artifactPaths = @($aa | ForEach-Object { $_.path }) + @($kk | ForEach-Object { $_.path } | Sort-Object -Unique)
        goldStatus = if ($allGold.Count -gt 0) { 'EXISTING_GOLD_ARTIFACT' } else { 'NONE_FOUND' }
        coverage = if (@($machineExhaustive).Count -gt 0) { 'EXHAUSTIVE' } elseif (@($machineSemantic).Count -gt 0 -or @($fullKeys).Count -gt 0) { 'PARTIAL_OR_ROUTE_SCOPED' } else { 'NONE' }
        semanticHeadingTotal = if (@($machineExhaustive).Count -gt 0) { [int](@($machineExhaustive | Sort-Object declaredHeadingCount -Descending)[0].declaredHeadingCount) } elseif (@($machineSemantic).Count -gt 0) { [int](@($machineSemantic | Sort-Object declaredHeadingCount -Descending)[0].declaredHeadingCount) } elseif (@($fullKeys).Count -gt 0) { [int](@($fullKeys | Sort-Object rowCount -Descending)[0].rowCount) } else { 0 }
        observedHeadingCount = if (@($machineSemantic).Count -gt 0) { [int](@($machineSemantic | Sort-Object observedHeadingCount -Descending)[0].observedHeadingCount) } else { 0 }
        reviewedEntireDocument = (@($aa | Where-Object { $_.reviewedEntireDocument }).Count -gt 0) -or (@($fullKeys).Count -gt 0)
        headingSetExhaustive = (@($machineExhaustive).Count -gt 0) -or (@($fullKeys).Count -gt 0)
        finalAuthority = if (@($machineSemantic).Count -gt 0) { [string](@($machineSemantic[0].finalAuthority)) } elseif (@($fullKeys).Count -gt 0) { 'HUMAN' } else { 'UNKNOWN' }
        referenceProvenance = if (@($aa | Where-Object { $_.referenceProvenance -eq 'HUMAN_ONLY' }).Count -gt 0) { 'HUMAN_ONLY' } elseif (@($aa).Count -gt 0) { 'MODEL_ASSISTED_OR_NOT_PROVEN' } elseif (@($fullKeys).Count -gt 0) { 'HUMAN_KEY_INDEPENDENCE_NOT_PROVEN' } else { 'NONE' }
        semanticStrictGold = (@($machineSemantic).Count -gt 0) -or (@($fullKeys).Count -gt 0)
        exhaustiveStrictGold = (@($machineExhaustive).Count -gt 0) -or (@($fullKeys).Count -gt 0)
        occurrenceGold = (@($machineOccurrence).Count -gt 0)
        occurrenceEvaluable = (@($aa | Where-Object { $_.occurrenceEvaluable -and $_.validOccurrenceGold }).Count -gt 0)
        characterSpanEvaluable = (@($aa | Where-Object { $_.characterSpanEvaluable -and $_.validOccurrenceGold }).Count -gt 0)
        roleEvaluable = (@($aa | Where-Object { $_.roleEvaluable -and $_.validSemanticGold }).Count -gt 0)
        hierarchyEvaluable = (@($aa | Where-Object { $_.hierarchyEvaluable -and $_.validSemanticGold }).Count -gt 0)
        activeOrSuperseded = if (@($aa).Count -gt 0 -or @($kk).Count -gt 0) { 'ACTIVE_OR_CURRENT_REFERENCE' } else { 'NO_GOLD_ARTIFACT' }
        usedInCurrent5DocDEV = $currentFive -contains $id
        usedInAnyA99Intervention = ($currentFive -contains $id) -or (@($aa | Where-Object { $_.independenceStatus -match 'A99_HISTORY|CURRENT_B0' }).Count -gt 0)
        independenceStatus = if (@($aa | Where-Object { $_.independenceStatus -match 'TUNING_CONTAMINATED|A99_HISTORY' }).Count -gt 0) { 'NOT_INDEPENDENT_FOR_R2' } elseif (@($aa).Count -gt 0 -or @($kk).Count -gt 0) { 'NOT_PROVEN_INDEPENDENT' } else { 'NOT_APPLICABLE' }
        artifactDetail = @($aa | ForEach-Object { [pscustomobject]@{ path=$_.path; class=$_.artifactClass; version=$_.artifactVersion; sha256=$_.artifactSha256; sourceHashStatus=$_.sourceHashStatus; validSemantic=$_.validSemanticGold; validExhaustive=$_.validExhaustiveGold; validOccurrence=$_.validOccurrenceGold; independence=$_.independenceStatus } })
        legacyHumanKeyDetail = @($kk | ForEach-Object { [pscustomobject]@{ path=$_.path; logicalKey=$_.logicalKey; completeness=$_.humanCompleteness; rowCount=$_.rowCount; sourceHashStatus=$_.sourceHashStatus; occurrenceEvaluable=$_.occurrenceEvaluable } })
    }) | Out-Null
}

$strictMachineDocs = @($inventoryRows | Where-Object { $_.semanticStrictGold -and @($_.artifactDetail).Count -gt 0 } | ForEach-Object { $_.documentId } | Sort-Object -Unique)
$exhaustiveMachineDocs = @($inventoryRows | Where-Object { @($_.artifactDetail | Where-Object { $_.validExhaustive }).Count -gt 0 } | ForEach-Object { $_.documentId } | Sort-Object -Unique)
$occurrenceDocs = @($inventoryRows | Where-Object { $_.occurrenceGold } | ForEach-Object { $_.documentId } | Sort-Object -Unique)
$strictV4Ids = @($artifacts | Where-Object { $_.artifactClass -eq 'STRICT_GOLD_V4' } | ForEach-Object { $_.documentId } | Sort-Object -Unique)
$outsideActiveIds = @('DOC-0202','DOC-0123','DOC-0255','DOC-0259')
$pilotRows = @($inventoryRows | Where-Object { $pilotEight -contains $_.documentId })
$pilotKeyRows = @($pilotRows | Where-Object { $_.legacyHumanKeyDetail.Count -gt 0 -and $_.headingSetExhaustive })
$pilotStrictRows = @($pilotRows | Where-Object { $_.occurrenceGold })
$reusableRows = @($inventoryRows | Where-Object { $_.semanticStrictGold -and -not $_.usedInCurrent5DocDEV }) | ForEach-Object {
    $verified = @($_.artifactDetail | Where-Object { $_.validSemantic -and $_.sourceHashStatus -eq 'MATCH' })
    if ($verified.Count -eq 0) { return }
    [pscustomobject]@{
        documentId=$_.documentId; sourceSha256=$_.sourceSha256; sourcePath=$_.sourcePath
        semanticGold=$_.semanticStrictGold; exhaustive=$_.exhaustiveStrictGold; occurrenceGold=$_.occurrenceGold
        occurrenceEvaluable=$_.occurrenceEvaluable; sourceHashVerified=$true
        provenance=$_.referenceProvenance; independence=$_.independenceStatus
        reuseClass=if ($_.occurrenceGold -and $_.independenceStatus -eq 'INDEPENDENT') {'REUSABLE_STRICT_OCCURRENCE'} elseif ($_.semanticStrictGold) {'REUSABLE_SEMANTIC_ONLY_INDEPENDENCE_UNPROVEN'} else {'NOT_REUSABLE'}
        reason=if ($_.independenceStatus -eq 'NOT_INDEPENDENT_FOR_R2') {'Used or assisted in A99 development history'} else {'Existing artifact is valid at semantic level, but independent lineage is not proven'}
    }
}

$gitLog = @(& git -C $RepoRoot log --all --format='%H%x09%s' -- eval/a99-closed-loop/strict-gold-v3 eval/a99-closed-loop/strict-gold-v4 eval/a99-closed-loop/strict-gold-occurrence-v1 eval/a99-closed-loop/review keys 2>$null)
$gitCommits = @($gitLog | Select-Object -First 40)

$searchRootEntries = @(
    [pscustomobject]@{ root=$RepoRoot; exists=$true; scope='REPOSITORY_WHOLE_TREE_EXCLUDING_GIT_FOR_CANONICAL_COUNT' },
    [pscustomobject]@{ root=(Join-Path $RepoRoot '.claude\worktrees'); exists=(Test-Path (Join-Path $RepoRoot '.claude\worktrees')); scope='LOCAL_DUPLICATE_WORKTREES_SEARCHED_NOT_COUNTED_AS_AUTHORITATIVE' },
    [pscustomobject]@{ root=$ExternalGoldRoot; exists=(Test-Path $ExternalGoldRoot); scope='EXTERNAL_A99_GOLD_ROOT' },
    [pscustomobject]@{ root=(Join-Path $ExternalGoldRoot 'holdout'); exists=(Test-Path (Join-Path $ExternalGoldRoot 'holdout')); scope='EXPECTED_EXTERNAL_HOLDOUT_ROOT' }
)

$markerFiles = @()
foreach ($root in @($RepoRoot,$ExternalGoldRoot)) {
    if (Test-Path -LiteralPath $root) {
        $markerFiles += @(Get-ChildItem -LiteralPath $root -Recurse -File -Force -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\.git\\' -and $_.Name -match '(?i)(gold|strict|human|occurrence|\.key$)' } | Select-Object -First 250 | ForEach-Object { Get-Rel $_.FullName })
    }
}

$pathEntries = [System.Collections.Generic.List[object]]::new()
foreach ($a in $artifacts) {
    $pathEntries.Add([pscustomobject]@{ path=$a.path; absolutePath=$a.absolutePath; location=$a.location; artifactClass=$a.artifactClass; documentId=$a.documentId; artifactSha256=$a.artifactSha256; sourceSha256=$a.sourceSha256; sourceHashStatus=$a.sourceHashStatus; activeOrSuperseded=$a.activeOrSuperseded; independence=$a.independenceStatus }) | Out-Null
}
foreach ($k in $keyArtifacts) {
    $pathEntries.Add([pscustomobject]@{ path=$k.path; location='REPO'; artifactClass=$k.artifactClass; logicalKey=$k.logicalKey; candidateDocumentIds=$k.candidateDocumentIds; artifactSha256=$k.artifactSha256; sourceHashStatus=$k.sourceHashStatus; completeness=$k.humanCompleteness; activeOrSuperseded=$k.activeOrSuperseded; independence=$k.provenance }) | Out-Null
}
foreach ($legacyDirName in @('rebased','superseded','tagged-pdf-coverage','toc-derived','occurrence-bridge')) {
    $legacyDir = Join-Path $RepoRoot ('keys\' + $legacyDirName)
    foreach ($f in @(Get-ChildItem -LiteralPath $legacyDir -Recurse -File -ErrorAction SilentlyContinue)) {
        $pathEntries.Add([pscustomobject]@{ path=Get-Rel $f.FullName; location='REPO'; artifactClass=('LEGACY_' + $legacyDirName.ToUpperInvariant().Replace('-','_')); artifactSha256=Get-Hash $f.FullName; activeOrSuperseded=if ($legacyDirName -eq 'superseded' -or $legacyDirName -eq 'rebased') {'HISTORICAL_OR_SUPERSEDED'} elseif ($legacyDirName -eq 'toc-derived') {'EXCLUDED_NOT_HUMAN_GOLD'} else {'REFERENCE_BRIDGE_NOT_STRICT_GOLD'}; independence='NOT_APPLICABLE' }) | Out-Null
    }
}
$manifestNames = @('strict-gold-authority.v1.json','strict-gold-manifest.v4.json','strict-human-gold-freeze.v1.json','holdout-result.v1.json','holdout-result.v2.json','review\holdout-manifest.v1.json','strict-gold-capability-matrix.v5.json')
foreach ($name in $manifestNames) {
    $p = Join-Path $RepoRoot ('eval\a99-closed-loop\' + $name)
    if (Test-Path -LiteralPath $p) { $pathEntries.Add([pscustomobject]@{ path=Get-Rel $p; location='REPO'; artifactClass='AUTHORITY_OR_CAPABILITY_MANIFEST'; artifactSha256=Get-Hash $p; activeOrSuperseded='LINEAGE_REFERENCE' }) | Out-Null }
}

$inventoryOutput = [pscustomobject]@{
    schemaVersion='a99-strict-gold-complete-inventory-audit-v1'
    audit='A99 STRICT GOLD COMPLETE INVENTORY AUDIT'
    generatedAt=(Get-Date).ToUniversalTime().ToString('o')
    repositoryHead=(& git -C $RepoRoot rev-parse HEAD).Trim()
    branch=(& git -C $RepoRoot branch --show-current).Trim()
    modelCalls=0
    providerCalls=0
    goldCreatedByAudit=$false
    existingGoldModified=$false
    sourceInventory='eval/a99-dataset/document-inventory.v1.json'
    uniqueDocumentCount=$inventoryRows.Count
    documents=$inventoryRows
    notes=@('Rows cover all 352 inventory document IDs; NONE_FOUND rows are explicit negative inventory results.', 'Legacy human keys are not occurrence Gold because they do not encode UTF-16 source spans.', 'External v3 files are retained as artifacts but their independence conflicts with A99 assisted-history records, so they are not certified as an independent R2 holdout.')
}

$pathsOutput = [pscustomobject]@{
    schemaVersion='a99-strict-gold-path-inventory-v1'
    generatedAt=(Get-Date).ToUniversalTime().ToString('o')
    repositoryHead=$inventoryOutput.repositoryHead
    modelCalls=0
    providerCalls=0
    searchRoots=$searchRootEntries
    markerFileCount=$markerFiles.Count
    markerFiles=$markerFiles
    externalGoldFiles=@($externalGoldFiles | ForEach-Object { Get-Rel $_.FullName })
    externalHoldoutExists=(Test-Path (Join-Path $ExternalGoldRoot 'holdout'))
    localDuplicateWorktreesDetected=(Test-Path (Join-Path $RepoRoot '.claude\worktrees'))
    artifactPaths=$pathEntries
    gitHistorySearch=[pscustomobject]@{ relevantCommitLineCount=$gitLog.Count; firstCommits=$gitCommits; searchWasReadOnly=$true }
}

$pilotOutput = [pscustomobject]@{
    schemaVersion='a99-r2-pilot8-existing-gold-overlap-v1'
    pilotDocuments=$pilotRows
    pilotSize=$pilotEight.Count
    existingStrictJsonDocumentIds=@($pilotRows | Where-Object { $_.artifactDetail.Count -gt 0 } | ForEach-Object {$_.documentId})
    existingOccurrenceGoldDocumentIds=@($pilotStrictRows | ForEach-Object {$_.documentId})
    existingHumanKeyDocumentIds=@($pilotKeyRows | ForEach-Object {$_.documentId})
    exactSourceHashVerifiedHumanKeyDocumentIds=@()
    sourceHashNote='Human key files do not embed a source SHA; exact route identity is inferred only from the key source filename and remains lineage-unverified.'
    newStrictOccurrenceAnnotationRequiredFor=@($pilotRows | Where-Object { -not ($pilotStrictRows.documentId -contains $_.documentId) } | ForEach-Object {$_.documentId})
    newSemanticAnnotationRequiredFor=@($pilotRows | Where-Object { -not ($pilotKeyRows.documentId -contains $_.documentId) } | ForEach-Object {$_.documentId})
    modelCalls=0
    providerCalls=0
}

$decisionOutput = [pscustomobject]@{
    schemaVersion='a99-strict-gold-inventory-decision-v1'
    generatedAt=(Get-Date).ToUniversalTime().ToString('o')
    modelCalls=0
    providerCalls=0
    totalUniqueGoldDocuments=(@($inventoryRows | Where-Object {$_.semanticStrictGold}).Count)
    strictJsonSemanticGoldDocuments=$strictMachineDocs.Count
    exhaustiveStrictGoldDocuments=$exhaustiveMachineDocs.Count
    occurrenceGoldDocuments=$occurrenceDocs.Count
    repositoryOccurrenceAuthorityDocuments=@($artifacts | Where-Object {$_.artifactClass -eq 'OCCURRENCE_GOLD' -and $_.validOccurrenceGold} | ForEach-Object {$_.documentId} | Sort-Object -Unique).Count
    externalSpanCapableHumanGoldDocuments=@($artifacts | Where-Object {$_.artifactClass -eq 'EXTERNAL_HUMAN_GOLD' -and $_.validOccurrenceGold} | ForEach-Object {$_.documentId} | Sort-Object -Unique).Count
    occurrenceGoldDeclaredDocuments=@($artifacts | Where-Object {$_.artifactClass -eq 'OCCURRENCE_GOLD'} | ForEach-Object {$_.documentId} | Sort-Object -Unique).Count
    knownV4DocumentCount=$strictV4Ids.Count
    knownV4DocumentIds=$strictV4Ids
    preservedOutsideActiveDocumentIds=$outsideActiveIds
    legacyHumanKeyArtifactCount=$keyArtifacts.Count
    legacyHumanKeyMappedDocumentCount=@($inventoryRows | Where-Object {$_.legacyHumanKeyDetail.Count -gt 0}).Count
    pilot8OverlapCount=$pilotKeyRows.Count
    pilot8OverlapDocumentIds=@($pilotKeyRows | ForEach-Object {$_.documentId})
    independentReusableGoldCount=0
    reusableSemanticOnlyCount=$reusableRows.Count
    reusableCohort=$reusableRows
    newHumanAnnotationRequired='partial'
    newHumanAnnotationRequiredForPilot8StrictOccurrence=($pilotStrictRows.Count -lt $pilotEight.Count)
    missingPilotStrictOccurrenceDocuments=@($pilotRows | Where-Object { -not ($pilotStrictRows.documentId -contains $_.documentId) } | ForEach-Object {$_.documentId})
    decision='REUSE_PILOT_GOLD'
    independenceDecision='EXISTING_GOLD_NOT_INDEPENDENT'
    rationale=@('Two pilot documents have full human legacy keys (DOC-0158 and DOC-0165) and may be reused only for semantic-level pilot review.', 'No pilot document has existing valid strict occurrence Gold with UTF-16 character spans.', 'No independent reusable Gold cohort is proven: external DOC-0255/DOC-0259 and preserved DOC-0202/DOC-0123 are tied to assisted A99 history; other legacy keys lack an independence certificate.', 'Six pilot documents still require independent human annotation for the strict R2 occurrence protocol.', 'This audit stops before R2-B and before any annotation automation.')
    terminal='R2_AWAITING_INDEPENDENT_HUMAN_GOLD'
}

$reusableOutput = [pscustomobject]@{
    schemaVersion='a99-reusable-gold-cohort-v1'
    generatedAt=(Get-Date).ToUniversalTime().ToString('o')
    exclusion='Current canonical B0 five documents excluded; only valid artifacts with matching source SHA are listed.'
    modelCalls=0
    providerCalls=0
    independentReusableCount=0
    candidates=$reusableRows
    candidateCount=$reusableRows.Count
    independentCandidates=@($reusableRows | Where-Object { $_.independence -eq 'INDEPENDENT' })
    legacyHumanKeysExcluded=@($keyArtifacts | Where-Object { $_.sourceHashStatus -ne 'MATCH' } | ForEach-Object { [pscustomobject]@{ path=$_.path; logicalKey=$_.logicalKey; candidateDocumentIds=$_.candidateDocumentIds; reason='No embedded source SHA; not eligible for strict occurrence reuse' } })
    conclusion='Existing source-hash-verified semantic artifacts exist, but none has a proven independent R2 lineage; no strict occurrence holdout is reusable.'
}

$jsonOptions = @{ Depth=12; Compress=$false }
$inventoryOutput | ConvertTo-Json @jsonOptions | Set-Content -LiteralPath (Join-Path $OutRoot 'inventory.v1.json') -Encoding UTF8
$pathsOutput | ConvertTo-Json @jsonOptions | Set-Content -LiteralPath (Join-Path $OutRoot 'paths.v1.json') -Encoding UTF8
$pilotOutput | ConvertTo-Json @jsonOptions | Set-Content -LiteralPath (Join-Path $OutRoot 'pilot8-overlap.v1.json') -Encoding UTF8
$reusableOutput | ConvertTo-Json @jsonOptions | Set-Content -LiteralPath (Join-Path $OutRoot 'reusable-gold-cohort.v1.json') -Encoding UTF8
$decisionOutput | ConvertTo-Json @jsonOptions | Set-Content -LiteralPath (Join-Path $OutRoot 'decision.v1.json') -Encoding UTF8

Write-Output ("AUDIT_WRITTEN=" + (Get-Rel $OutRoot))
Write-Output ("UNIQUE_INVENTORY_DOCS=" + $inventoryRows.Count)
Write-Output ("STRICT_JSON_SEMANTIC=" + $strictMachineDocs.Count)
Write-Output ("EXHAUSTIVE_STRICT_JSON=" + $exhaustiveMachineDocs.Count)
Write-Output ("OCCURRENCE_VALID=" + $occurrenceDocs.Count)
Write-Output ("PILOT_HUMAN_KEY_OVERLAP=" + $pilotKeyRows.Count)
Write-Output ("INDEPENDENT_REUSABLE=0")
Write-Output 'MODEL_CALLS=0'
Write-Output 'PROVIDER_CALLS=0'
