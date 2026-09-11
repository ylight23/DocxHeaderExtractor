param([string]$RepoRoot = (Get-Location).Path)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $RepoRoot

$root = Join-Path $RepoRoot 'eval/a99-closed-loop/research-r2'
$annotationRoot = Join-Path $root 'annotation'
New-Item -ItemType Directory -Force -Path $root, $annotationRoot, (Join-Path $annotationRoot 'sources'), (Join-Path $annotationRoot 'annotator-a'), (Join-Path $annotationRoot 'annotator-b'), (Join-Path $annotationRoot 'adjudication') | Out-Null

function Read-Json([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
function Write-Json([string]$Path, $Value) {
    $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Path -Encoding UTF8
}
function Sha256Text([string]$Value) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
        return ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    } finally { $sha.Dispose() }
}
function Sha256File([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant() }
function AsArray($Value) { if ($null -eq $Value) { return @() }; return @($Value) }

$inventoryPath = 'eval/a99-dataset/document-inventory.v1.json'
$inventory = Read-Json $inventoryPath
$b0Commit = (git rev-parse 76c4e01).Trim()
$c1Commit = (git rev-parse HEAD).Trim()
$studyId = 'A99-R2'
$devDocs = @('DOC-0001','DOC-0205','DOC-0252','DOC-0256','DOC-0258')

# These are provenance exclusions only. No heading Gold or model prediction payload is read.
$referenceArtifacts = @{}
foreach ($path in @(
    'eval/a99-closed-loop/strict-gold-authority.v1.json',
    'eval/a99-closed-loop/strict-gold-manifest.v4.json',
    'eval/a99-closed-loop/review/holdout-manifest.v1.json')) {
    $obj = Read-Json $path
    foreach ($property in @('documents','entries','globalStrictGoldOutsideActiveCohort')) {
        if ($obj.PSObject.Properties.Name -contains $property) {
            foreach ($entry in (AsArray $obj.$property)) {
                if ($entry.documentId) { $referenceArtifacts[$entry.documentId] = $path }
            }
        }
    }
}

$seedInput = 'A99-R2-INDEPENDENT-COHORT' + $b0Commit + $c1Commit
$seed = Sha256Text $seedInput
$algorithm = 'SHA256(seed + documentId + sourceSha256); sort ascending within familyId stratum; allocate floor(24/strataCount) then distribute remainder by descending eligible stratum count and familyId; select quota per stratum'
$algorithmHash = Sha256Text $algorithm
$allDocs = @(AsArray $inventory.documents)
$frame = @()
$eligible = @()

foreach ($doc in $allDocs) {
    $reason = $null
    if ($referenceArtifacts.ContainsKey($doc.documentId)) {
        if ($devDocs -contains $doc.documentId) { $reason = 'CURRENT_DEV_COHORT' }
        else { $reason = 'A99_REFERENCE_OR_HOLDOUT_ARTIFACT_HISTORY' }
    } elseif ($doc.duplicateKind -ne 'UNIQUE') { $reason = 'NON_UNIQUE_SOURCE_GROUP' }
    elseif ($doc.familyId -eq 'UNKNOWN') { $reason = 'UNKNOWN_FAMILY_STRATUM' }
    elseif ($doc.referenceAuthority -ne 'UNLABELED' -or $doc.referenceKind -ne 'NONE') { $reason = 'REFERENCE_METADATA_PRESENT' }
    elseif (-not (Test-Path -LiteralPath $doc.sourcePath)) { $reason = 'SOURCE_UNAVAILABLE' }

    $record = [ordered]@{
        documentId = $doc.documentId
        documentGroupId = $doc.documentGroupId
        sourcePath = $doc.sourcePath
        mediaType = $doc.mediaType
        sourceSha256 = $doc.sourceSha256.ToLowerInvariant()
        familyId = $doc.familyId
        duplicateKind = $doc.duplicateKind
        familyConfidence = $doc.familyConfidence
        referenceAuthority = $doc.referenceAuthority
        referenceKind = $doc.referenceKind
        sourceAvailable = ($null -eq $reason -or $reason -ne 'SOURCE_UNAVAILABLE') -and (Test-Path -LiteralPath $doc.sourcePath)
        eligible = ($null -eq $reason)
    }
    if ($reason) { $record.exclusionReason = $reason } else { $eligible += $doc }
    $frame += [pscustomobject]$record
}

$strataGroups = @($eligible | Group-Object familyId | Sort-Object Name)
$baseQuota = [math]::Floor(24 / [double]$strataGroups.Count)
$remainder = 24 - ($baseQuota * $strataGroups.Count)
$quotaByStratum = @{}
foreach ($group in $strataGroups) { $quotaByStratum[$group.Name] = [int]$baseQuota }
foreach ($group in @($strataGroups | Sort-Object @{Expression='Count';Descending=$true}, Name | Select-Object -First $remainder)) {
    $quotaByStratum[$group.Name]++
}

$ranked = @()
foreach ($doc in $eligible) {
    $rank = Sha256Text ($seed + $doc.documentId + $doc.sourceSha256.ToLowerInvariant())
    $ranked += [pscustomobject]@{ doc = $doc; rank = $rank }
}
$selected = @()
foreach ($group in $strataGroups) {
    $selected += @($ranked | Where-Object { $_.doc.familyId -eq $group.Name } | Sort-Object rank | Select-Object -First $quotaByStratum[$group.Name] | ForEach-Object { $_.doc })
}
$selected = @($selected | Sort-Object documentId)
if ($selected.Count -ne 24) { throw "Expected 24 selected documents, got $($selected.Count)" }

# Verify the inventory hash is stable before freezing the cohort.
foreach ($doc in $selected) {
    $actual = Sha256File $doc.sourcePath
    if ($actual -ne $doc.sourceSha256.ToLowerInvariant()) { throw "SOURCE_HASH_MISMATCH $($doc.documentId)" }
}

$framePath = Join-Path $root 'sampling-frame.v1.json'
Write-Json $framePath ([ordered]@{
    schemaVersion = 'a99-r2a-sampling-frame-v1'
    studyId = $studyId
    sourceInventory = $inventoryPath
    sourceInventoryCreatedFromCodeSha = $inventory.metadata.createdFromCodeSha
    labelsInspectedBeforeSelection = $false
    modelOutputsInspectedBeforeSelection = $false
    metadataFieldsUsed = @('documentId','documentGroupId','sourcePath','mediaType','sourceSha256','familyId','duplicateKind','familyConfidence','referenceAuthority','referenceKind')
    eligibilityRule = 'exclude current DEV, A99 reference/holdout history, non-unique sources, unknown family stratum, reference metadata, or unavailable sources; require stable inventory SHA and repository source'
    eligibleCount = $eligible.Count
    excludedCount = $allDocs.Count - $eligible.Count
    strata = @($strataGroups | ForEach-Object { [ordered]@{ familyId = $_.Name; eligibleCount = $_.Count; quota = $quotaByStratum[$_.Name] } })
    documents = $frame
})

$selectionPath = Join-Path $root 'cohort-selection.v1.json'
Write-Json $selectionPath ([ordered]@{
    schemaVersion = 'a99-r2a-cohort-selection-v1'
    studyId = $studyId
    behavioralParent = "B0@$b0Commit"
    c1Commit = $c1Commit
    preferredSampleSize = 24
    selectedCount = $selected.Count
    seedSource = 'SHA256("A99-R2-INDEPENDENT-COHORT" + canonical B0 commit + C1 commit)'
    seedInput = $seedInput
    seed = $seed
    algorithm = $algorithm
    algorithmHash = $algorithmHash
    knownResidualStringSelection = $false
    goldInspectedBeforeSelection = $false
    modelOutputsInspectedBeforeSelection = $false
    currentDevOverlap = 0
    quotaByStratum = $quotaByStratum
    selectedDocuments = @($selected | ForEach-Object { [ordered]@{ documentId = $_.documentId; sourceSha256 = $_.sourceSha256.ToLowerInvariant(); familyId = $_.familyId; documentGroupId = $_.documentGroupId; rank = (Sha256Text ($seed + $_.documentId + $_.sourceSha256.ToLowerInvariant())) } })
    excludedDocuments = @($frame | Where-Object { -not $_.eligible } | ForEach-Object { [ordered]@{ documentId = $_.documentId; reason = $_.exclusionReason } })
})

$freezePath = Join-Path $root 'cohort-freeze.v1.json'
Write-Json $freezePath ([ordered]@{
    schemaVersion = 'a99-r2a-cohort-freeze-v1'
    studyId = $studyId
    status = 'COHORT_FROZEN_AWAITING_INDEPENDENT_HUMAN_GOLD'
    behavioralParent = "B0@$b0Commit"
    freezeCommit = $c1Commit
    selectionAlgorithmHash = $algorithmHash
    selectedDocuments = @($selected | ForEach-Object { [ordered]@{ documentId = $_.documentId; sourceSha256 = $_.sourceSha256.ToLowerInvariant(); sourcePath = $_.sourcePath; familyId = $_.familyId } })
    labelsInspectedBeforeSelection = $false
    modelOutputsInspectedBeforeSelection = $false
    knownResidualStringSelection = $false
    currentDevOverlap = 0
    membershipImmutableAfterCommit = $true
})

$instructions = @'
# A99-R2 independent human annotation instructions

This package is for independent offline Gold creation. It is not an inference or optimization
task. Annotators must use only the source document identified by each source artifact and must
record exact source-backed occurrences.

## Blindness and independence

Annotator A and Annotator B work independently. Do not open model predictions, A99 reports,
current residual lists, prompt files, or another annotator's output. Do not search for historically
reported strings or structures. Do not infer a heading merely because a style, numbering marker,
table position, or model output suggests it.

## Required row

For each semantically plausible heading occurrence, record `sourceOccurrenceId`, exact heading text,
UTF-16 `start` and exclusive `end`, `headingPresence`, and `semanticFamily`. Record `role` only
if independently determinable; otherwise use null. The source occurrence plus exact UTF-16 span is
the identity authority. Do not silently union disagreements.

## Families

Use one of: DOCUMENT_TITLE, PART_CHAPTER, SECTION, SUBSECTION,
REGION_OR_GEOGRAPHIC_LABEL, PROGRAM_OR_TOPIC_LABEL, TABLE_OR_LOCAL_LABEL,
NAVIGATION_OR_AGENDA, ANNEX, CAPTION, OTHER_STRUCTURAL_LABEL, UNKNOWN.

For semantically plausible heading-like negatives, optionally record one of:
NON_HEADING_TITLE_LIKE, NON_HEADING_REGION_TOPIC_MENTION, NON_HEADING_PARTICIPANT_LABEL,
NON_HEADING_NAVIGATION, OTHER_HARD_NEGATIVE.
Do not exhaustively label arbitrary prose.

## Procedure

Review the complete source, record source-backed spans in document order, and leave uncertainty
explicit. After both annotations are complete, compute exact-span, heading-presence, and family
agreement. Every disagreement goes to adjudication with both original rows preserved.

These annotations are offline diagnostic Gold. They must never become runtime rules.
'@
Set-Content -LiteralPath (Join-Path $annotationRoot 'instructions.md') -Value $instructions -Encoding UTF8

$schema = [ordered]@{
    schemaVersion = 'a99-r2a-annotation-schema-v1'
    type = 'object'
    required = @('studyId','documentId','sourceSha256','annotatorId','rows')
    properties = [ordered]@{
        studyId = [ordered]@{ const = $studyId }
        documentId = [ordered]@{ type = 'string' }
        sourceSha256 = [ordered]@{ type = 'string'; pattern = '^[0-9a-f]{64}$' }
        annotatorId = [ordered]@{ enum = @('A','B','ADJUDICATED') }
        rows = [ordered]@{
            type = 'array'
            items = [ordered]@{
                type = 'object'
                required = @('sourceOccurrenceId','exactHeadingText','utf16Start','utf16End','headingPresence','semanticFamily')
                properties = [ordered]@{
                    sourceOccurrenceId = [ordered]@{ type = 'string' }
                    exactHeadingText = [ordered]@{ type = 'string' }
                    utf16Start = [ordered]@{ type = 'integer'; minimum = 0 }
                    utf16End = [ordered]@{ type = 'integer'; minimum = 0 }
                    headingPresence = [ordered]@{ type = 'boolean' }
                    semanticFamily = [ordered]@{ enum = @('DOCUMENT_TITLE','PART_CHAPTER','SECTION','SUBSECTION','REGION_OR_GEOGRAPHIC_LABEL','PROGRAM_OR_TOPIC_LABEL','TABLE_OR_LOCAL_LABEL','NAVIGATION_OR_AGENDA','ANNEX','CAPTION','OTHER_STRUCTURAL_LABEL','UNKNOWN') }
                    role = [ordered]@{ type = @('string','null') }
                    negativeClass = [ordered]@{ type = @('string','null') }
                    note = [ordered]@{ type = @('string','null') }
                }
            }
        }
    }
    exactSpanEnd = 'exclusive UTF-16 code-unit offset'
    runtimeUse = 'PROHIBITED'
}
Write-Json (Join-Path $annotationRoot 'schema.v1.json') $schema

foreach ($label in @('annotator-a','annotator-b','adjudication')) {
    Set-Content -LiteralPath (Join-Path $annotationRoot $label 'README.md') -Value "Reserved for independent R2 human annotation. No model output or fabricated Gold is stored here before human review." -Encoding UTF8
}

foreach ($doc in $selected) {
    $artifact = [ordered]@{
        schemaVersion = 'a99-r2a-annotation-source-v1'
        studyId = $studyId
        documentId = $doc.documentId
        sourcePath = $doc.sourcePath
        sourceSha256 = $doc.sourceSha256.ToLowerInvariant()
        mediaType = $doc.mediaType
        familyId = $doc.familyId
        documentGroupId = $doc.documentGroupId
        sourceAvailable = $true
        sourceIntegrityVerified = $true
        contentPurpose = 'complete source access for human annotation only'
        containsModelPredictions = $false
        containsGoldLabels = $false
        containsResidualHints = $false
    }
    Write-Json (Join-Path $annotationRoot ('sources/' + $doc.documentId + '.source.v1.json')) $artifact
}

$manifestPath = Join-Path $root 'study-manifest.v2.json'
Write-Json $manifestPath ([ordered]@{
    schemaVersion = 'a99-r2-study-manifest-v2'
    studyId = $studyId
    status = 'R2_AWAITING_INDEPENDENT_HUMAN_GOLD'
    parentGeneration = 'A99-C1'
    behavioralParent = "B0@$b0Commit"
    preparedAtCommit = $c1Commit
    modelCalls = 0
    providerCalls = 0
    runtimeChanges = 0
    currentDevDocuments = $devDocs
    currentDevOverlap = 0
    purpose = 'Test whether residual semantic failure patterns generalize to an independently selected document cohort.'
    notAnIntervention = $true
    goldInspectedBeforeSelection = $false
    modelOutputsInspectedBeforeSelection = $false
    knownResidualStringSelection = $false
    cohort = [ordered]@{
        selectedCount = $selected.Count
        cohortFreeze = 'cohort-freeze.v1.json'
        selection = 'cohort-selection.v1.json'
        samplingFrame = 'sampling-frame.v1.json'
        membershipImmutableAfterCommit = $true
    }
    annotation = [ordered]@{
        instructions = 'annotation/instructions.md'
        schema = 'annotation/schema.v1.json'
        sourceArtifacts = 'annotation/sources/'
        annotatorA = 'annotation/annotator-a/'
        annotatorB = 'annotation/annotator-b/'
        adjudication = 'annotation/adjudication/'
        twoIndependentAnnotators = $true
        modelOutputsVisible = $false
        currentResidualsVisible = $false
        finalGoldRule = 'Annotator A + Annotator B + explicit adjudication; never silent union'
    }
    r2BInferenceAuthorized = $false
    authorizationRequires = @('cohort-freeze.v1.json','gold-authority.v1.json','study-manifest.v2.json','completed independent human annotation and adjudication')
    terminalReason = 'No valid independent human Gold exists in repository; actual independent annotation is required.'
})

$authorityAudit = [ordered]@{
    schemaVersion = 'a99-r2a-authority-audit-v1'
    studyId = $studyId
    auditCommit = $c1Commit
    modelCalls = 0
    providerCalls = 0
    goldPayloadsInspected = $false
    candidates = @(
        [ordered]@{ artifact = $inventoryPath; commit = (git log -1 --format=%H -- $inventoryPath).Trim(); documents = $allDocs.Count; annotationOrigin = 'deterministic inventory metadata'; annotationDate = $null; modelOutputsVisibleToAnnotators = $false; participatedInI1I6 = $false; labelsInfluencedRuntime = $false; classification = 'INSUFFICIENT_PROVENANCE'; reason = 'inventory has no human heading labels and cannot itself establish independent Gold' }
        [ordered]@{ artifact = 'eval/a99-closed-loop/review/holdout-manifest.v1.json'; commit = (git log -1 --format=%H -- eval/a99-closed-loop/review/holdout-manifest.v1.json).Trim(); documents = 23; annotationOrigin = 'sealed source review packets, no final exhaustive heading Gold'; annotationDate = $null; modelOutputsVisibleToAnnotators = $false; participatedInI1I6 = $false; labelsInfluencedRuntime = $false; classification = 'INSUFFICIENT_PROVENANCE'; reason = 'holdout-result remains TRUE_BLIND_REQUIRED and no independent Gold authority is frozen' }
        [ordered]@{ artifact = 'eval/a99-closed-loop/strict-gold-authority.v1.json'; commit = (git log -1 --format=%H -- eval/a99-closed-loop/strict-gold-authority.v1.json).Trim(); documents = 19; annotationOrigin = 'mixed pending human review and user-finalized model-assisted references'; annotationDate = $null; modelOutputsVisibleToAnnotators = $true; participatedInI1I6 = $true; labelsInfluencedRuntime = $true; classification = 'CONTAMINATED_BY_DEV'; reason = 'contains current DEV/history and model-assisted references; not an independent blind Gold set' }
        [ordered]@{ artifact = 'eval/a99-closed-loop/strict-gold-manifest.v4.json'; commit = (git log -1 --format=%H -- eval/a99-closed-loop/strict-gold-manifest.v4.json).Trim(); documents = 19; annotationOrigin = 'user-finalized strict references, including model-assisted and current cohort history'; annotationDate = $null; modelOutputsVisibleToAnnotators = $true; participatedInI1I6 = $true; labelsInfluencedRuntime = $true; classification = 'CONTAMINATED_BY_TUNING'; reason = 'not an independently selected blind cohort for R2' }
        [ordered]@{ artifact = 'eval/accuracy/hierarchy-human-pilot-annotation.v1.json'; commit = (git log -1 --format=%H -- eval/accuracy/hierarchy-human-pilot-annotation.v1.json).Trim(); documents = 24; annotationOrigin = 'pilot schema ready for blind annotation, zero reviewed'; annotationDate = $null; modelOutputsVisibleToAnnotators = $false; participatedInI1I6 = $false; labelsInfluencedRuntime = $false; classification = 'INSUFFICIENT_PROVENANCE'; reason = 'no completed annotation and no second annotator/agreement' }
        [ordered]@{ artifact = 'eval/benchmark-n0 and eval/benchmark-n3 source-packets'; commit = $c1Commit; documents = $null; annotationOrigin = 'blind source review packets'; annotationDate = $null; modelOutputsVisibleToAnnotators = $false; participatedInI1I6 = $false; labelsInfluencedRuntime = $false; classification = 'INSUFFICIENT_PROVENANCE'; reason = 'source packets are not independent human heading Gold authority' }
    )
    validIndependentGoldExists = $false
    terminalStatus = 'R2_AWAITING_INDEPENDENT_HUMAN_GOLD'
}
Write-Json (Join-Path $root 'authority-audit.v1.json') $authorityAudit

Write-Output "R2A_MODEL_CALLS=0"
Write-Output "R2A_PROVIDER_CALLS=0"
Write-Output "R2A_EXISTING_INDEPENDENT_GOLD=NO"
Write-Output "R2A_ELIGIBLE=$($eligible.Count)"
Write-Output "R2A_SELECTED=$($selected.Count)"
Write-Output "R2A_STATUS=R2_AWAITING_INDEPENDENT_HUMAN_GOLD"
