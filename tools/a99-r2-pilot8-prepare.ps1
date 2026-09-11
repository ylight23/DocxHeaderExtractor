param([string]$RepoRoot = (Get-Location).Path)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $RepoRoot

$r2Root = Join-Path $RepoRoot 'eval/a99-closed-loop/research-r2'
$pilotRoot = Join-Path $r2Root 'pilot-8'
$annotationRoot = Join-Path $pilotRoot 'annotation'
New-Item -ItemType Directory -Force -Path $pilotRoot, $annotationRoot, (Join-Path $annotationRoot 'annotator-a'), (Join-Path $annotationRoot 'annotator-b'), (Join-Path $annotationRoot 'adjudication') | Out-Null

function Read-Json([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
function Write-Json([string]$Path, $Value) { $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Path -Encoding UTF8 }
function Sha256Text([string]$Value) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
        return ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    } finally { $sha.Dispose() }
}

$freezePath = Join-Path $r2Root 'cohort-freeze.v2.json'
$freeze = Read-Json $freezePath
$preparedFromCheckpoint = (git rev-parse HEAD).Trim()
$canonicalB0 = '76c4e01a10577b1c6069255646843cbaef729a1f'
$reserveFreezeCommit = '99f66b1ff5d73fb2e8d8d6f4753707c59f5b8dd9'
$reserve = @($freeze.selectedDocuments)
if ($reserve.Count -ne 24) { throw "RESERVE_COHORT_SIZE=$($reserve.Count), expected 24" }

$seedInput = 'A99-R2-PILOT-8' + $canonicalB0 + $reserveFreezeCommit
$seed = Sha256Text $seedInput
$algorithm = 'SHA256(seed + documentId + sourceSha256); sort ascending within the frozen 24-document familyId stratum; select the pre-registered quota'
$algorithmHash = Sha256Text $algorithm
$quota = [ordered]@{
    DOCX_NATIVE_STRUCTURED = 2
    PDF_NATIVE_LAYOUT = 2
    PDF_CONVERTED = 1
    VN_LEGAL_MARKER = 1
    VN_ADMIN_TYPED = 2
}
$selected = @()
foreach ($family in $quota.Keys) {
    $ranked = @($reserve | Where-Object { $_.familyId -eq $family } | ForEach-Object {
        [pscustomobject]@{ doc = $_; rank = (Sha256Text ($seed + $_.documentId + $_.sourceSha256.ToLowerInvariant())) }
    } | Sort-Object rank)
    if ($ranked.Count -lt $quota[$family]) { throw "PILOT_STRATUM_TOO_SMALL $family" }
    $selected += @($ranked | Select-Object -First $quota[$family] | ForEach-Object { $_.doc })
}
$selected = @($selected | Sort-Object documentId)
if ($selected.Count -ne 8) { throw "PILOT_SIZE=$($selected.Count), expected 8" }

$reserveKeys = @($reserve | ForEach-Object { $_.documentId + '|' + $_.sourceSha256.ToLowerInvariant() } | Sort-Object)
$selectedKeys = @($selected | ForEach-Object { $_.documentId + '|' + $_.sourceSha256.ToLowerInvariant() } | Sort-Object)
if (@($selectedKeys | Where-Object { $_ -notin $reserveKeys }).Count -ne 0) { throw 'PILOT_NOT_SUBSET_OF_RESERVE' }

$selectedPayload = @($selected | ForEach-Object {
    [ordered]@{
        documentId = $_.documentId
        sourceSha256 = $_.sourceSha256.ToLowerInvariant()
        sourcePath = $_.sourcePath
        familyId = $_.familyId
        sourceArtifact = ('../annotation/sources/' + $_.documentId + '.source.v1.json')
        rank = (Sha256Text ($seed + $_.documentId + $_.sourceSha256.ToLowerInvariant()))
    }
})

Write-Json (Join-Path $pilotRoot 'pilot-manifest.v1.json') ([ordered]@{
    schemaVersion = 'a99-r2-pilot-8-manifest-v1'
    studyId = 'A99-R2'
    pilotId = 'R2-PILOT-8'
    status = 'PILOT_FROZEN_AWAITING_INDEPENDENT_HUMAN_GOLD'
    preparedFromCheckpoint = $preparedFromCheckpoint
    reserveCohortFreeze = '../cohort-freeze.v2.json'
    reserveCohortFreezeArtifactCommit = $reserveFreezeCommit
    preferredFullCohortSize = 24
    pilotSize = 8
    quotaByStratum = $quota
    selectionSeedSource = 'SHA256("A99-R2-PILOT-8" + canonical B0 commit + reserve cohort freeze artifact commit)'
    seedInput = $seedInput
    seed = $seed
    selectionAlgorithm = $algorithm
    selectionAlgorithmHash = $algorithmHash
    selectedDocuments = $selectedPayload
    reserveCohortUnchanged = $true
    labelsInspectedBeforeSelection = $false
    modelOutputsInspectedBeforeSelection = $false
    goldInspectedBeforeSelection = $false
    knownResidualStringSelection = $false
    currentDevOverlap = 0
    modelCalls = 0
    providerCalls = 0
    r2BInferenceAuthorized = $false
    nextHumanAction = 'independent double annotation of these 8 documents, then human adjudication'
    annotation = [ordered]@{
        instructions = '../annotation/instructions.md'
        schema = '../annotation/schema.v1.json'
        annotatorA = 'annotation/annotator-a/'
        annotatorB = 'annotation/annotator-b/'
        adjudication = 'annotation/adjudication/'
        noAnnotationFilesCreatedByAutomation = $true
    }
})

Write-Json (Join-Path $pilotRoot 'pilot-freeze.v1.json') ([ordered]@{
    schemaVersion = 'a99-r2-pilot-8-freeze-v1'
    studyId = 'A99-R2'
    pilotId = 'R2-PILOT-8'
    status = 'PILOT_MEMBERSHIP_FROZEN_AWAITING_INDEPENDENT_HUMAN_GOLD'
    reserveCohortFreeze = '../cohort-freeze.v2.json'
    reserveCohortFreezeArtifactCommit = $reserveFreezeCommit
    pilotSelectionPreparedFromCheckpoint = $preparedFromCheckpoint
    selectionAlgorithmHash = $algorithmHash
    selectedDocuments = @($selected | ForEach-Object { [ordered]@{ documentId = $_.documentId; sourceSha256 = $_.sourceSha256.ToLowerInvariant(); sourcePath = $_.sourcePath; familyId = $_.familyId } })
    selectedCount = 8
    reserveCohortUnchanged = $true
    membershipImmutableAfterCommit = $true
    labelsInspectedBeforeSelection = $false
    modelOutputsInspectedBeforeSelection = $false
    goldInspectedBeforeSelection = $false
    knownResidualStringSelection = $false
    currentDevOverlap = 0
})

$readme = 'R2-PILOT-8 annotation workspace. Automation created no annotation rows. Human A and Human B must independently annotate only the eight documents listed in pilot-freeze.v1.json. Do not open model outputs or residual artifacts. Adjudication is human-only.'
foreach ($folder in @('annotator-a','annotator-b','adjudication')) {
    Set-Content -LiteralPath (Join-Path $annotationRoot $folder 'README.md') -Value $readme -Encoding UTF8
}

Write-Output 'R2PILOT_MODEL_CALLS=0'
Write-Output 'R2PILOT_PROVIDER_CALLS=0'
Write-Output 'R2PILOT_RESERVE_COHORT_UNCHANGED=true'
Write-Output 'R2PILOT_SELECTED=8'
Write-Output 'R2PILOT_STATUS=PILOT_FROZEN_AWAITING_INDEPENDENT_HUMAN_GOLD'
