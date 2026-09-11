param(
    [string]$RepoRoot = (Get-Location).Path,
    [switch]$Validate,
    [string]$AnnotationPath
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $RepoRoot

$r2Root = Join-Path $RepoRoot 'eval/a99-closed-loop/research-r2'
$freezeV1Path = Join-Path $r2Root 'cohort-freeze.v1.json'
$freezeV2Path = Join-Path $r2Root 'cohort-freeze.v2.json'
$sourceRoot = Join-Path $r2Root 'annotation/sources'

function Read-Json([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
function Write-Json([string]$Path, $Value) { $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Path -Encoding UTF8 }
function Sha256Text([string]$Value) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
        return ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    } finally { $sha.Dispose() }
}

$freezeV1 = Read-Json $freezeV1Path
$b0Commit = '76c4e01a10577b1c6069255646843cbaef729a1f'
$selectionParentCheckpoint = '6a72a4ceb3c6ef9465a4d4d651d3f4ef09edade6'
$cohortFreezeArtifactCommit = '99f66b1ff5d73fb2e8d8d6f4753707c59f5b8dd9'
$expected = @($freezeV1.selectedDocuments | ForEach-Object { $_.documentId + '|' + $_.sourceSha256.ToLowerInvariant() })
$expectedSorted = @($expected | Sort-Object)

$v2 = [ordered]@{
    schemaVersion = 'a99-r2a-cohort-freeze-v2'
    studyId = $freezeV1.studyId
    status = 'COHORT_FROZEN_AWAITING_INDEPENDENT_HUMAN_GOLD'
    behavioralParent = "B0@$b0Commit"
    selectionParentCheckpoint = $selectionParentCheckpoint
    cohortFreezeArtifactCommit = $cohortFreezeArtifactCommit
    historicalArtifact = 'cohort-freeze.v1.json'
    selectionAlgorithmHash = $freezeV1.selectionAlgorithmHash
    selectedDocuments = @($freezeV1.selectedDocuments | ForEach-Object { [ordered]@{ documentId = $_.documentId; sourceSha256 = $_.sourceSha256.ToLowerInvariant(); sourcePath = $_.sourcePath; familyId = $_.familyId } })
    labelsInspectedBeforeSelection = $false
    modelOutputsInspectedBeforeSelection = $false
    knownResidualStringSelection = $false
    currentDevOverlap = 0
    membershipImmutableAfterCommit = $true
    metadataCorrection = 'v1 freezeCommit was historically the selection parent checkpoint; v2 names the two lineage meanings explicitly'
}

if ($expectedSorted.Count -ne 24) { throw "COHORT_SIZE_MISMATCH_V1=$($expectedSorted.Count)" }
if (($expectedSorted | Select-Object -Unique).Count -ne 24) { throw 'COHORT_DUPLICATE_V1' }
Write-Json $freezeV2Path $v2

if (-not $Validate) {
    Write-Output 'R2A1_MODEL_CALLS=0'
    Write-Output 'R2A1_PROVIDER_CALLS=0'
    Write-Output 'COHORT_MEMBERSHIP_DELTA=0'
    Write-Output 'SOURCE_HASH_DELTA=0'
    Write-Output 'R2A1_STATUS=R2_AWAITING_INDEPENDENT_HUMAN_GOLD'
    exit 0
}

$errors = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()
function Add-Error([string]$Message) { $script:errors.Add($Message) }
function Add-Warning([string]$Message) { $script:warnings.Add($Message) }

$freeze = Read-Json $freezeV2Path
$cohort = @{}
foreach ($doc in @($freeze.selectedDocuments)) { $cohort[$doc.documentId] = $doc }
if ($cohort.Count -ne 24) { Add-Error "COHORT_SIZE=$($cohort.Count), expected 24" }
if ($freeze.selectionParentCheckpoint -ne $selectionParentCheckpoint) { Add-Error 'SELECTION_PARENT_CHECKPOINT_MISMATCH' }
if ($freeze.cohortFreezeArtifactCommit -ne $cohortFreezeArtifactCommit) { Add-Error 'COHORT_FREEZE_ARTIFACT_COMMIT_MISMATCH' }

$annotationFiles = @()
if ($AnnotationPath) { $annotationFiles = @(Get-Item -LiteralPath $AnnotationPath) }
else {
    foreach ($folder in @('annotator-a','annotator-b','adjudication')) {
        $path = Join-Path $r2Root "annotation/$folder"
        if (Test-Path $path) { $annotationFiles += @(Get-ChildItem -LiteralPath $path -File -Filter '*.json') }
    }
}

$families = @('DOCUMENT_TITLE','PART_CHAPTER','SECTION','SUBSECTION','REGION_OR_GEOGRAPHIC_LABEL','PROGRAM_OR_TOPIC_LABEL','TABLE_OR_LOCAL_LABEL','NAVIGATION_OR_AGENDA','ANNEX','CAPTION','OTHER_STRUCTURAL_LABEL','UNKNOWN')
$roles = @('ARTICLE','CHAPTER','SECTION','SUBSECTION','OTHER_STRUCTURAL_LABEL')
$negativeClasses = @('NON_HEADING_TITLE_LIKE','NON_HEADING_REGION_TOPIC_MENTION','NON_HEADING_PARTICIPANT_LABEL','NON_HEADING_NAVIGATION','OTHER_HARD_NEGATIVE')

foreach ($file in $annotationFiles) {
    try { $annotation = Read-Json $file.FullName } catch { Add-Error "$($file.Name): INVALID_JSON"; continue }
    $docId = [string]$annotation.documentId
    if (-not $cohort.ContainsKey($docId)) { Add-Error "$($file.Name): DOCUMENT_NOT_IN_FROZEN_COHORT"; continue }
    $expectedDoc = $cohort[$docId]
    if ([string]$annotation.sourceSha256 -ne [string]$expectedDoc.sourceSha256) { Add-Error "$($file.Name): SOURCE_SHA_MISMATCH" }

    $folder = Split-Path $file.DirectoryName -Leaf
    $expectedAnnotator = switch ($folder) { 'annotator-a' { 'A' } 'annotator-b' { 'B' } 'adjudication' { 'ADJUDICATED' } default { $null } }
    if ($null -eq $expectedAnnotator -or [string]$annotation.annotatorId -ne $expectedAnnotator) { Add-Error "$($file.Name): ANNOTATOR_ID_MISMATCH" }
    if ([string]$annotation.annotatorId -notin @('A','B','ADJUDICATED')) { Add-Error "$($file.Name): INVALID_ANNOTATOR_ID" }

    $rows = @($annotation.rows)
    $seen = @{}
    $sortKeys = @()
    $sourceText = $null
    if ($annotation.PSObject.Properties.Name -contains 'sourceText') { $sourceText = [string]$annotation.sourceText }
    elseif ($annotation.PSObject.Properties.Name -contains 'sourceTextPath' -and $annotation.sourceTextPath) {
        $sourcePath = Join-Path $RepoRoot ([string]$annotation.sourceTextPath)
        if (Test-Path -LiteralPath $sourcePath) { $sourceText = Get-Content -LiteralPath $sourcePath -Raw } else { Add-Error "$($file.Name): SOURCE_TEXT_PATH_NOT_FOUND" }
    }
    if ($null -eq $sourceText) { Add-Warning "$($file.Name): SOURCE_SUBSTRING_CHECK_DEFERRED_NO_TEXT_PAYLOAD" }

    foreach ($row in $rows) {
        foreach ($required in @('sourceOccurrenceId','exactHeadingText','utf16Start','utf16End','headingPresence','semanticFamily')) {
            if (-not ($row.PSObject.Properties.Name -contains $required)) { Add-Error "$($file.Name): ROW_MISSING_$required" }
        }
        $start = 0L; $end = 0L
        try { $start = [int64]$row.utf16Start; $end = [int64]$row.utf16End } catch { Add-Error "$($file.Name): INVALID_UTF16_NUMBER"; continue }
        if ($start -lt 0 -or $end -lt $start) { Add-Error "$($file.Name): INVALID_UTF16_RANGE" }
        if ($row.headingPresence -isnot [bool]) { Add-Error "$($file.Name): INVALID_HEADING_PRESENCE" }
        if ([string]$row.semanticFamily -notin $families) { Add-Error "$($file.Name): INVALID_SEMANTIC_FAMILY" }
        if ($row.PSObject.Properties.Name -contains 'role' -and $null -ne $row.role -and [string]$row.role -notin $roles) { Add-Error "$($file.Name): INVALID_ROLE" }
        if ($row.PSObject.Properties.Name -contains 'negativeClass' -and $null -ne $row.negativeClass -and [string]$row.negativeClass -notin $negativeClasses) { Add-Error "$($file.Name): INVALID_NEGATIVE_CLASS" }
        $key = "$start|$end"
        if ($seen.ContainsKey($key)) { Add-Error "$($file.Name): DUPLICATE_SPAN_$key" } else { $seen[$key] = $true }
        $sortKeys += ('{0:D12}|{1:D12}|{2}|{3}' -f $start,$end,[string]$row.sourceOccurrenceId,[string]$row.exactHeadingText)
        if ($null -ne $sourceText -and $start -ge 0 -and $end -ge $start -and $end -le $sourceText.Length) {
            $actual = $sourceText.Substring([int]$start, [int]($end - $start))
            if ($actual -cne [string]$row.exactHeadingText) { Add-Error "$($file.Name): EXACT_TEXT_NOT_SOURCE_SUBSTRING_$key" }
        } elseif ($null -ne $sourceText -and $end -gt $sourceText.Length) { Add-Error "$($file.Name): UTF16_END_OUT_OF_SOURCE_RANGE_$key" }
    }
    $sortedKeys = @($sortKeys | Sort-Object)
    if (($sortKeys -join "`n") -cne ($sortedKeys -join "`n")) { Add-Error "$($file.Name): ROWS_NOT_DETERMINISTICALLY_SORTED" }
}

Write-Output "R2A1_MODEL_CALLS=0"
Write-Output "R2A1_PROVIDER_CALLS=0"
Write-Output "ANNOTATION_FILES=$($annotationFiles.Count)"
Write-Output "VALIDATION_ERRORS=$($errors.Count)"
Write-Output "VALIDATION_WARNINGS=$($warnings.Count)"
foreach ($warning in $warnings) { Write-Output "WARNING=$warning" }
if ($errors.Count -gt 0) { foreach ($error in $errors) { Write-Output "ERROR=$error" }; exit 2 }
Write-Output 'R2A1_VALIDATION=PASS'
