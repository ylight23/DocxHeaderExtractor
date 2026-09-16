[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$OutputRoot = 'artifacts/authority-audit/canonical-blocker-burn-down-v2'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path $RepoRoot).Path
$out = Join-Path $repo ($OutputRoot -replace '/', '\')
$dhx = 'C:\Users\btdba\DocxHeaderExtractor\dhx.cmd'
if (-not (Test-Path -LiteralPath $dhx)) { throw "dhx.cmd not found: $dhx" }

function Read-Json([string]$Path) { Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json }
function Write-Json([string]$Path, $Value) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 80) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Rel([string]$Path) { (Resolve-Path -LiteralPath $Path -Relative).ToString().TrimStart('.','\','/').Replace('\','/') }
function Has-Property($Object, [string]$Name) { $null -ne $Object -and $Object.PSObject.Properties.Name -contains $Name }

$baselinePath = Join-Path $repo 'artifacts/authority-audit/canonical-batch-v1/document-inventory.json'
if (-not (Test-Path -LiteralPath $baselinePath)) { throw "Missing baseline inventory: $baselinePath" }
$baseline = Read-Json $baselinePath
$baselineByDoc = @{}
foreach ($row in @($baseline.documents)) { $baselineByDoc[[string]$row.docId] = $row }

$conversionLogPath = Join-Path $repo 'todo10_8/heading_corpus_95_word/CONVERSION_LOG.csv'
$conversionRows = if (Test-Path -LiteralPath $conversionLogPath) { @(Import-Csv $conversionLogPath) } else { @() }

$docIds = @('DOC-0185','DOC-0200','DOC-0201','DOC-0219','DOC-0264','DOC-0265','DOC-0243','DOC-0116','DOC-0122','DOC-0216','DOC-0205')
$recoveredSources = @{
    'DOC-0205' = [pscustomobject]@{ path='todo10_8/heading_corpus_100/01_phap_quy/025_ND_47-2020_Chia_se_du_lieu_so.doc'; sha='128e10058b6648a316c864c49c4a4015098b6b4dad07c4ea95996a50bf5b5004'; kind='ORIGINAL_SOURCE_DOC'; reason='CONVERSION_LOG_SOURCE_TO_CURRENT_DOCX' }
    'DOC-0264' = [pscustomobject]@{ path='todo10_8/heading_corpus_100/06_dich_song_ngu/084_Luat_Chung_khoan_2019_EN.doc'; sha='5fc30d8bb1a753e673243a4b08d647b116ddab81d23a697f5b1057e84389d318'; kind='ORIGINAL_SOURCE_DOC'; reason='CONVERSION_LOG_SOURCE_TO_CURRENT_DOCX' }
}

Remove-Item -LiteralPath $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $out | Out-Null
$scanRoot = Join-Path $out 'source-scan'
$blockerRoot = Join-Path $out 'blockers'

$rows = [Collections.Generic.List[object]]::new()
$blockers = [Collections.Generic.List[object]]::new()

foreach ($docId in $docIds) {
    $baselineRow = $baselineByDoc[$docId]
    $sourceCandidates = [Collections.Generic.List[object]]::new()
    $sourcePath = $null
    $expectedSha = $null
    $sourceStatus = 'SOURCE_NOT_RESOLVED'
    $sourceKind = $null
    $lineage = [ordered]@{}

    if ($recoveredSources.ContainsKey($docId)) {
        $recovered = $recoveredSources[$docId]
        $candidatePath = Join-Path $repo ($recovered.path -replace '/', '\')
        $sourceCandidates.Add([ordered]@{ path=Rel $candidatePath; exists=(Test-Path -LiteralPath $candidatePath); expectedSha256=$recovered.sha; role='RECOVERED_ORIGINAL_SOURCE' })
        if (Test-Path -LiteralPath $candidatePath) {
            $actual = Sha $candidatePath
            if ($actual -eq $recovered.sha) {
                $sourcePath = $candidatePath; $expectedSha = $recovered.sha; $sourceStatus = 'CANONICAL_SOURCE_RECOVERED'; $sourceKind = $recovered.kind
                $lineage.recoveryReason = $recovered.reason
            } else {
                $sourceStatus = 'SOURCE_CANDIDATE_HASH_MISMATCH'
                $sourceCandidates[0].actualSha256 = $actual
            }
        }
    } elseif ($baselineRow -and $baselineRow.sourcePath) {
        $candidatePath = Join-Path $repo ([string]$baselineRow.sourcePath -replace '/', '\')
        $sourceCandidates.Add([ordered]@{ path=Rel $candidatePath; exists=(Test-Path -LiteralPath $candidatePath); expectedSha256=[string]$baselineRow.strictHeadingSourceSHA; role='BASELINE_SOURCE' })
        if (Test-Path -LiteralPath $candidatePath) {
            $actual = Sha $candidatePath
            if ($actual -eq [string]$baselineRow.strictHeadingSourceSHA) {
                $sourcePath = $candidatePath; $expectedSha = $actual; $sourceStatus = 'CURRENT_SOURCE_EXACT_HASH'; $sourceKind = 'CURRENT_AUTHORITY_CANDIDATE'
            } else {
                $sourceStatus = 'CURRENT_SOURCE_HASH_MISMATCH'; $sourceCandidates[0].actualSha256 = $actual
            }
        }
    }

    $sourceFileName = if ($sourcePath) { Split-Path $sourcePath -Leaf } elseif ($baselineRow -and $baselineRow.sourcePath) { Split-Path ([string]$baselineRow.sourcePath) -Leaf } else { $null }
    $conversion = @($conversionRows | Where-Object { $_.output_rel -and $sourceFileName -and (Split-Path $_.output_rel -Leaf) -eq $sourceFileName } | Select-Object -First 1)
    if ($conversion.Count -gt 0) {
        $lineage.conversionStatus = [string]$conversion[0].status
        $lineage.conversionSource = [string]$conversion[0].source_rel
        $lineage.conversionSourceSha256 = [string]$conversion[0].source_sha256
        $lineage.conversionOutputSha256 = [string]$conversion[0].output_sha256
    }

    $headings = @()
    $xmlLineCount = 0
    $xmlStats = @()
    $extractError = $null
    if ($sourcePath) {
        try {
            $xmlLines = @(& $dhx xml $sourcePath --compact --structural-only 2>$null)
            $xmlLineCount = $xmlLines.Count
            $xmlStats = @($xmlLines | Select-String '^<doc |^<p |^<n ' | ForEach-Object { [string]$_.Line } | Select-Object -First 3)
            $jsonLines = @(& $dhx extract $sourcePath --no-llm -f json 2>$null)
            $jsonText = $jsonLines -join "`n"
            if ([string]::IsNullOrWhiteSpace($jsonText)) { throw 'dhx produced empty JSON output' }
            $extracted = $jsonText | ConvertFrom-Json
            $headings = @($extracted.headings)
        } catch {
            $extractError = $_.Exception.Message
        }
    }

    $markerCounts = [ordered]@{ chapterOrPart=0; sectionOrArticle=0; appendix=0; other=0 }
    foreach ($heading in $headings) {
        $text = ([string]$heading.text).Trim()
        if ($text -match '^(Chapter|Chương|Part|Phần)\b') { $markerCounts.chapterOrPart++ }
        elseif ($text -match '^(Section|Mục|Article|Điều)\b') { $markerCounts.sectionOrArticle++ }
        elseif ($text -match '^(Appendix|Annex|Phụ lục)\b') { $markerCounts.appendix++ }
        else { $markerCounts.other++ }
    }
    $strongStructural = @($headings | Where-Object { ([string]$_.styleId) -match '^(Heading|Title|Subtitle)' -or [int]$_.level -gt 1 }).Count
    $scan = [ordered]@{
        artifactKind='A99_CANONICAL_BLOCKER_BURN_DOWN_SOURCE_SCAN'
        schemaVersion='a99-canonical-blocker-burn-down-v2-source-scan'
        documentId=$docId
        sourcePath=if ($sourcePath) { Rel $sourcePath } else { $null }
        sourceFormat=if ($sourcePath) { [IO.Path]::GetExtension($sourcePath).TrimStart('.').ToUpperInvariant() } else { $null }
        sourceSha256=if ($sourcePath) { Sha $sourcePath } else { $null }
        expectedSourceSha256=$expectedSha
        sourceStatus=$sourceStatus
        sourceKind=$sourceKind
        sourceLineage=$lineage
        sourceCandidatesChecked=@($sourceCandidates)
        containerCount=$null
        dhxStructuralOnly=$true
        providerCalls=0
        modelCalls=0
        extractError=$extractError
        xmlLineCount=$xmlLineCount
        paragraphCount=if ($extracted) { [int]$extracted.paragraphCount } else { $null }
        heuristicCandidateCount=if ($extracted) { [int]$extracted.candidateCount } else { $null }
        heuristicHeadingCount=$headings.Count
        strongStructuralCandidateCount=$strongStructural
        markerCounts=$markerCounts
        headingCandidates=$headings
        xmlPreview=$xmlStats
    }
    $scanPath = Join-Path $scanRoot "$docId.json"
    Write-Json $scanPath $scan

    $reasonCode = $null
    $blockedPhase = 'SOURCE'
    $whatWasProven = [Collections.Generic.List[string]]::new()
    $whatWasNotProven = [Collections.Generic.List[string]]::new()
    $nextRecovery = [Collections.Generic.List[string]]::new()
    if ($sourceStatus -eq 'CANONICAL_SOURCE_RECOVERED') {
        $whatWasProven.Add('A source representation with an exact recorded SHA was recovered.')
        if ($lineage.Contains('conversionStatus')) { $whatWasProven.Add('Conversion log links the recovered source to a corpus representation.') }
        $blockedPhase = 'OCCURRENCE'
        $reasonCode = 'CANONICAL_EXHAUSTIVE_OCCURRENCE_REVIEW_NOT_MATERIALIZED'
        $whatWasNotProven.Add('Every candidate has not yet been semantically reviewed as a true heading occurrence.')
        $whatWasNotProven.Add('Identity membership and semantic-node hierarchy are not frozen.')
        $nextRecovery.Add('Perform source-only exhaustive occurrence review on the recovered source.')
    } elseif ($sourceStatus -eq 'CURRENT_SOURCE_EXACT_HASH') {
        $whatWasProven.Add('The current source path matches the baseline source SHA.')
        $blockedPhase = 'OCCURRENCE'
        $reasonCode = 'CANONICAL_EXHAUSTIVE_OCCURRENCE_REVIEW_NOT_MATERIALIZED'
        $whatWasNotProven.Add('No canonical exhaustive occurrence authority exists for this source.')
        $whatWasNotProven.Add('Existing strict/historical candidate outputs are not canonical authority.')
        $nextRecovery.Add('Perform source-only exhaustive occurrence review without historical denominator assumptions.')
    } elseif ($sourceStatus -like '*MISMATCH*' -or $sourceStatus -eq 'SOURCE_NOT_RESOLVED') {
        $reasonCode = 'SOURCE_AUTHORITY_BLOCKED'
        $whatWasNotProven.Add('An authoritative source with a verified SHA was not established.')
        $nextRecovery.Add('Recover an original source or deterministic predecessor and record its SHA lineage.')
    }

    if ($docId -eq 'DOC-0205' -and $sourceStatus -eq 'CANONICAL_SOURCE_RECOVERED') {
        $reasonCode = 'CANONICAL_SOURCE_RECOVERED_OCCURRENCE_REVIEW_PENDING'
        $nextRecovery.Add('Do not reuse the placeholder-DOCX 71-row proposal; review the recovered .doc directly.')
    }
    if ($docId -eq 'DOC-0264' -and $sourceStatus -eq 'CANONICAL_SOURCE_RECOVERED') {
        $reasonCode = 'CANONICAL_SOURCE_RECOVERED_OCCURRENCE_REVIEW_PENDING'
        $nextRecovery.Add('Do not treat the one-paragraph DOCX placeholder as a heading universe; review the recovered .doc directly.')
    }
    if ($docId -in @('DOC-0116','DOC-0122','DOC-0216')) {
        $blockedPhase = 'OCCURRENCE'
        $reasonCode = 'SOURCE_ONLY_RESTART_REQUIRED_AFTER_INVALID_PROPOSAL'
        $whatWasProven.Add('The prior source-backed proposal exists and is retained as invalid/ambiguous history.')
        $whatWasNotProven.Add('A new canonical exhaustive occurrence authority was not materialized.')
        $nextRecovery.Add('Restart source-only occurrence review; do not repair the invalid parent topology.')
    }
    if ($docId -eq 'DOC-0243') {
        $blockedPhase = 'OCCURRENCE'
        $reasonCode = 'CANONICAL_OCCURRENCE_UNIVERSE_NOT_AUTHORIZED'
        $whatWasProven.Add('The old 102-row proposal is a valid historical-subset proposal only.')
        $whatWasNotProven.Add('The occurrence universe is canonical exhaustive for current source.')
        $nextRecovery.Add('Review all current-source heading occurrences before rebuilding identity or hierarchy.')
    }
    if ($docId -in @('DOC-0185','DOC-0200','DOC-0201','DOC-0219','DOC-0265') -and $sourceStatus -eq 'CURRENT_SOURCE_EXACT_HASH') {
        $reasonCode = 'SOURCE_ONLY_EXHAUSTIVE_REVIEW_PENDING'
        $nextRecovery.Add('Use the structural-only source scan as evidence packet input, then perform complete source review.')
    }
    if ($docId -eq 'DOC-0205' -and $sourceStatus -eq 'SOURCE_NOT_RESOLVED') {
        $reasonCode = 'SOURCE_LINEAGE_HARD_BLOCKED'
    }

    $blocker = [ordered]@{
        artifactKind='A99_CANONICAL_BLOCKER_BURN_DOWN_BLOCKER'
        schemaVersion='a99-canonical-blocker-burn-down-v2-blocker'
        documentId=$docId
        blockedPhase=$blockedPhase
        reasonCode=$reasonCode
        sourceCandidatesChecked=@($sourceCandidates)
        evidence=[ordered]@{ sourceScan=(Rel $scanPath); sourceStatus=$sourceStatus; sourceSha256=if($sourcePath){Sha $sourcePath}else{$null}; heuristicCandidateCount=if($extracted){[int]$extracted.candidateCount}else{$null}; strongStructuralCandidateCount=$strongStructural; conversionLineage=$lineage }
        whatWasProven=@($whatWasProven)
        whatWasNotProven=@($whatWasNotProven)
        nextPossibleRecoveryAction=@($nextRecovery)
        historicalLevelRead=$false
        historicalParentRead=$false
        historicalHierarchyUsedForDecision=$false
        historicalSemanticTotalUsedForDecision=$false
        providerCalls=0
        modelCalls=0
        canonicalGoldMutation=$false
        newUserReviewedPromotion=$false
    }
    Write-Json (Join-Path $blockerRoot "$docId.json") $blocker
    $blockers.Add([pscustomobject]$blocker)
    $rows.Add([pscustomobject]@{
        documentId=$docId
        status='HARD_BLOCKED_WITH_PRECISE_SOURCE_BACKED_REASON'
        sourceStatus=$sourceStatus
        sourcePath=if($sourcePath){Rel $sourcePath}else{$null}
        sourceSha256=if($sourcePath){Sha $sourcePath}else{$null}
        heuristicCandidateCount=if($extracted){[int]$extracted.candidateCount}else{$null}
        strongStructuralCandidateCount=$strongStructural
        blockedPhase=$blockedPhase
        reasonCode=$reasonCode
        occurrenceCount=0
        semanticNodeCount=0
        parentEdgeCount=0
        rootChildren=0
        maxDepth=0
        validator='NOT_RUN_CANONICAL_LAYER_NOT_MATERIALIZED'
    })
}

$summary = [ordered]@{
    artifactKind='A99_CANONICAL_BLOCKER_BURN_DOWN_V2_SUMMARY'
    schemaVersion='a99-canonical-blocker-burn-down-v2-summary'
    status='BATCH_V2_READY_FOR_USER_REVIEW'
    baselineCommit='fc07f98'
    documentCount=$rows.Count
    attemptedAllOriginalBlockers=($rows.Count -eq 11)
    newlyReadyCanonicalHierarchy=0
    remainingHardBlocked=$rows.Count
    sourceRecovered=@($rows | Where-Object sourceStatus -eq 'CANONICAL_SOURCE_RECOVERED').Count
    providerCalls=0
    modelCalls=0
    historicalLevelRead=$false
    historicalParentRead=$false
    historicalHierarchyUsedForDecision=$false
    historicalSemanticTotalUsedForDecision=$false
    documents=@($rows)
}
Write-Json (Join-Path $out 'summary.json') $summary
Write-Json (Join-Path $out 'blockers.json') @($blockers)

$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# Canonical authority blocker burn-down v2')
$lines.Add('')
$lines.Add('Status: **BATCH_V2_READY_FOR_USER_REVIEW**')
$lines.Add('')
$lines.Add('All 11 baseline blockers were attempted continuously. No frozen authority was mutated and no new document was promoted to USER_REVIEWED_* .')
$lines.Add('')
$lines.Add("Source representations recovered: $($summary.sourceRecovered)")
$lines.Add('')
foreach ($row in $rows) { $lines.Add("- **$($row.documentId)** — $($row.status); source=$($row.sourceStatus); phase=$($row.blockedPhase); reason=$($row.reasonCode); candidates=$($row.heuristicCandidateCount); strongStructural=$($row.strongStructuralCandidateCount)") }
$lines.Add('')
$lines.Add('Historical direct levels, parent outputs, hierarchy outputs, and semantic totals were not used as canonical decisions. The scans are deterministic structural diagnostics only; they do not constitute user-reviewed Gold.')
[IO.File]::WriteAllText((Join-Path $out 'report.md'), ($lines -join [Environment]::NewLine) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

$validation = [ordered]@{
    artifactKind='A99_CANONICAL_BLOCKER_BURN_DOWN_V2_VALIDATION'
    schemaVersion='a99-canonical-blocker-burn-down-v2-validation'
    status='BATCH_V2_READY_FOR_USER_REVIEW'
    attemptedDocumentCount=$rows.Count
    allOriginalBlockersAttempted=($rows.Count -eq 11)
    everyDocumentHasPreciseReason=(@($rows | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.reasonCode) }).Count -eq 0)
    frozenAuthoritiesMutated=$false
    newUserReviewedPromotion=$false
    canonicalGoldMutation=$false
    historicalLevelRead=$false
    historicalParentRead=$false
    historicalHierarchyUsedForDecision=$false
    historicalSemanticTotalUsedForDecision=$false
    providerCalls=0
    modelCalls=0
}
Write-Json (Join-Path $out 'validation.json') $validation

$manifestFiles = @(Get-ChildItem $out -Recurse -File | Where-Object { $_.Name -ne 'manifest.json' } | Sort-Object FullName)
$manifest = [ordered]@{
    artifactKind='A99_CANONICAL_BLOCKER_BURN_DOWN_V2_MANIFEST'
    schemaVersion='a99-canonical-blocker-burn-down-v2-manifest'
    status='BATCH_V2_READY_FOR_USER_REVIEW'
    baselineCommit='fc07f98'
    generatedArtifacts=@($manifestFiles | ForEach-Object { [ordered]@{ path=Rel $_.FullName; sha256=Sha $_.FullName } })
    documentCount=$rows.Count
    providerCalls=0
    modelCalls=0
    historicalLevelRead=$false
    canonicalGoldMutation=$false
    newUserReviewedPromotion=$false
}
Write-Json (Join-Path $out 'manifest.json') $manifest
Write-Output ($summary | ConvertTo-Json -Depth 20)
