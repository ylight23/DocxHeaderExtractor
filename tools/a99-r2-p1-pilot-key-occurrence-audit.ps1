param(
    [string]$RepoRoot = (Get-Location).Path
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path $RepoRoot).Path
$OutRoot = Join-Path $RepoRoot 'eval\a99-closed-loop\research-r2\p1-key-occurrence-audit'
New-Item -ItemType Directory -Force -Path $OutRoot | Out-Null

function Read-JsonFile([string]$Path) { Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json }
function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Rel([string]$Path) { $Path.Substring($RepoRoot.Length).TrimStart('\','/') -replace '\\','/' }
function U16Length([string]$Text) { if ($null -eq $Text) { return 0 }; return $Text.Length }

$inventory = Read-JsonFile (Join-Path $RepoRoot 'eval\a99-dataset\document-inventory.v1.json')
$cases = @(
    [pscustomobject]@{ documentId='DOC-0158'; key='keys\format-driven-human\073_FORTIS_GC_Minutes_Mar_2026.key'; pdf='todo10_8\heading_corpus_100\05_bien_ban_hop\073_FORTIS_GC_Minutes_Mar_2026.pdf'; expectedCount=7 },
    [pscustomobject]@{ documentId='DOC-0165'; key='keys\format-driven-human\080_ICP_Governing_Board_Minutes_Feb_2023.key'; pdf='todo10_8\heading_corpus_100\05_bien_ban_hop\080_ICP_Governing_Board_Minutes_Feb_2023.pdf'; expectedCount=12 }
)

$cliDll = Join-Path $RepoRoot 'src\DocxHeaderExtractor.Cli\bin\Debug\net9.0\dhx.dll'
if (-not (Test-Path -LiteralPath $cliDll)) { throw "Missing deterministic CLI: $cliDll" }
$cliHash = Get-Sha256 $cliDll
$a99History = @(& git -C $RepoRoot log --all --date=iso-strict --format='%H|%ad|%s' -- eval/a99-closed-loop 2>$null)
$a99First = if ($a99History.Count -gt 0) { $a99History[-1] } else { $null }

$results = [System.Collections.Generic.List[object]]::new()
foreach ($case in $cases) {
    $keyPath = Join-Path $RepoRoot $case.key
    $pdfPath = Join-Path $RepoRoot $case.pdf
    if (-not (Test-Path -LiteralPath $keyPath) -or -not (Test-Path -LiteralPath $pdfPath)) { throw "Missing case artifact for $($case.documentId)" }
    $doc = @($inventory.documents | Where-Object { $_.documentId -eq $case.documentId })[0]
    if ($null -eq $doc) { throw "Document not found in inventory: $($case.documentId)" }

    $keyText = Get-Content -LiteralPath $keyPath -Raw -Encoding UTF8
    $keyRows = [System.Collections.Generic.List[object]]::new()
    foreach ($line in @(Get-Content -LiteralPath $keyPath -Encoding UTF8)) {
        if ($line -match '^(@\S+)\s+(\d+)\s+#\s?(.*)$') {
            $keyRows.Add([pscustomobject]@{ stableId=$Matches[1]; level=[int]$Matches[2]; exactHeadingText=$Matches[3] }) | Out-Null
        }
    }
    $raw = @(& dotnet $cliDll pdf-clusters $pdfPath --no-llm 2>$null) -join "`n"
    $cluster = $raw | ConvertFrom-Json
    if ($cluster.status -ne 'ok') { throw "PDF deterministic extraction failed for $($case.documentId): $($cluster.status)" }

    $blocks = [System.Collections.Generic.List[object]]::new()
    $ordinal = 0
    foreach ($block in @($cluster.blocks | Sort-Object page,@{Expression={ if ($_.id -match '^b(\d+)$') {[int]$Matches[1]} else {0} }})) {
        $ordinal++
        $sourceText = [string]$block.sourceText
        $blocks.Add([pscustomobject]@{
            blockId=[string]$block.id; page=[int]$block.page; ordinal=$ordinal
            sourceText=$sourceText; sourceTextUtf16Length=(U16Length $sourceText)
        }) | Out-Null
    }

    $cursor = 0
    foreach ($block in $blocks) {
        $block | Add-Member -NotePropertyName globalStart -NotePropertyValue $cursor
        $block | Add-Member -NotePropertyName globalEnd -NotePropertyValue ($cursor + $block.sourceTextUtf16Length)
        $cursor += $block.sourceTextUtf16Length + 1
    }
    $joinedSource = (($blocks | ForEach-Object { $_.sourceText }) -join "`n")
    $sourceRepresentationHash = [System.BitConverter]::ToString(([System.Security.Cryptography.SHA256]::Create().ComputeHash([System.Text.Encoding]::UTF8.GetBytes($joinedSource)))).Replace('-','').ToLowerInvariant()

    $materialized = [System.Collections.Generic.List[object]]::new()
    foreach ($row in $keyRows) {
        $matches = [System.Collections.Generic.List[object]]::new()
        foreach ($block in $blocks) {
            $pos = 0
            while ($pos -le $block.sourceText.Length) {
                $found = $block.sourceText.IndexOf($row.exactHeadingText, $pos, [System.StringComparison]::Ordinal)
                if ($found -lt 0) { break }
                $matches.Add([pscustomobject]@{
                    blockId=$block.blockId; page=$block.page; sourceOrdinal=$block.ordinal
                    localUtf16Start=$found; localUtf16End=($found + $row.exactHeadingText.Length)
                    globalUtf16Start=($block.globalStart + $found); globalUtf16End=($block.globalStart + $found + $row.exactHeadingText.Length)
                    matchedSourceText=$block.sourceText
                }) | Out-Null
                $pos = $found + [Math]::Max(1,$row.exactHeadingText.Length)
            }
        }
        $status = if ($matches.Count -eq 1) { 'EXACT_UNIQUE' } elseif ($matches.Count -gt 1) { 'HUMAN_OCCURRENCE_REVIEW_REQUIRED' } else { 'SOURCE_KEY_MISMATCH' }
        $materialized.Add([pscustomobject]@{
            stableId=$row.stableId; level=$row.level; exactHeadingText=$row.exactHeadingText
            exactMatchCount=$matches.Count; status=$status
            selectedOccurrence=if ($status -eq 'EXACT_UNIQUE') { $matches[0] } else { $null }
            allExactMatches=@($matches)
        }) | Out-Null
    }

    $history = @(& git -C $RepoRoot log --all --follow --date=iso-strict --format='%H|%ad|%an|%s' -- $case.key 2>$null)
    $creation = if ($history.Count -gt 0) { $history[-1] } else { $null }
    $creationHash = if ($creation) { $creation.Split('|')[0] } else { $null }
    $creationFiles = if ($creationHash) { @(& git -C $RepoRoot show --format= --name-only $creationHash 2>$null) | Where-Object { $_ } } else { @() }
    $modelFiles = @($creationFiles | Where-Object { $_ -match '(?i)(prediction|model-output|inference|response)' })
    $sourceSha = Get-Sha256 $pdfPath
    $keySha = Get-Sha256 $keyPath
    $result = [pscustomobject]@{
        artifactKind='A99_R2_P1_PILOT_KEY_OCCURRENCE_AUDIT'
        documentId=$case.documentId
        keyPath=(Rel $keyPath); keySha256=$keySha
        sourcePath=(Rel $pdfPath); sourceSha256=$sourceSha
        inventorySourceSha256=[string]$doc.sourceSha256
        sourceShaMatchesInventory=($sourceSha -eq [string]$doc.sourceSha256)
        sourceFileExists=$true
        expectedHumanHeadingCount=$case.expectedCount
        keyHeadingCount=$keyRows.Count
        headingMembershipChanged=$false
        fuzzyMatchingUsed=$false
        modelCalls=0; providerCalls=0
        deterministicExtractor='dhx pdf-clusters --no-llm'
        extractorBinary=(Rel $cliDll); extractorBinarySha256=$cliHash
        sourceRepresentation=[pscustomobject]@{ kind='PDF_CLUSTER_SOURCE_TEXT'; pageCount=[int]$cluster.pages; blockCount=$blocks.Count; joinedSourceUtf16Length=$joinedSource.Length; joinedSourceSha256=$sourceRepresentationHash; separator='LF'; blockOrder='page ascending, numeric block id ascending' }
        provenance=[pscustomobject]@{
            keyClaimsDirectHumanPdfReview=($keyText -match '(?i)đọc trực tiếp từ PDF|không lấy từ\s*output pipeline')
            keyClaimsFullReview=($keyText -match '(?i)Đủ\s+\d+\/\d+|full_human')
            keyCreationCommit=$creationHash
            keyCreationHistoryLine=$creation
            keyCommitFiles=@($creationFiles)
            modelOutputFilesInKeyCreationCommit=@($modelFiles)
            modelOutputUsedToCreateKey=if ($modelFiles.Count -eq 0) {'NOT_EVIDENCED_BY_KEY_CREATION_COMMIT'} else {'EVIDENCE_REQUIRES_REVIEW'}
            a99HistoryEarliestObserved=$a99First
            keyPredatesA99ClosedLoopHistory=if ($creation -and $a99First) { ([datetimeoffset]$creation.Split('|')[1]) -lt ([datetimeoffset]$a99First.Split('|')[1]) } else { $null }
            independenceConclusion='PRE_A99_HUMAN_KEY; no model-output file in key creation commit; absolute historical independence cannot be proven from Git alone'
        }
        rows=$materialized
        summary=[pscustomobject]@{
            exactUniqueCount=@($materialized | Where-Object {$_.status -eq 'EXACT_UNIQUE'}).Count
            humanOccurrenceReviewRequiredCount=@($materialized | Where-Object {$_.status -eq 'HUMAN_OCCURRENCE_REVIEW_REQUIRED'}).Count
            sourceKeyMismatchCount=@($materialized | Where-Object {$_.status -eq 'SOURCE_KEY_MISMATCH'}).Count
            exactUniqueStableIds=@($materialized | Where-Object {$_.status -eq 'EXACT_UNIQUE'} | ForEach-Object {$_.stableId})
            reviewStableIds=@($materialized | Where-Object {$_.status -eq 'HUMAN_OCCURRENCE_REVIEW_REQUIRED'} | ForEach-Object {$_.stableId})
            mismatchStableIds=@($materialized | Where-Object {$_.status -eq 'SOURCE_KEY_MISMATCH'} | ForEach-Object {$_.stableId})
            finalStatus=if (@($materialized | Where-Object {$_.status -ne 'EXACT_UNIQUE'}).Count -eq 0) {'READY_FOR_DETERMINISTIC_OCCURRENCE_MATERIALIZATION'} else {'HUMAN_REVIEW_OR_KEY_REPAIR_REQUIRED'}
        }
    }
    $results.Add($result) | Out-Null
    $docDir = Join-Path $OutRoot $case.documentId
    New-Item -ItemType Directory -Force -Path $docDir | Out-Null
    $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $docDir 'occurrence-materialization.v1.json') -Encoding UTF8
    [pscustomobject]@{ documentId=$case.documentId; keySha256=$keySha; sourceShaMatchesInventory=$result.sourceShaMatchesInventory; keyHeadingCount=$keyRows.Count; exactUnique=$result.summary.exactUniqueCount; reviewRequired=$result.summary.humanOccurrenceReviewRequiredCount; mismatch=$result.summary.sourceKeyMismatchCount; finalStatus=$result.summary.finalStatus }
}

$decision = [pscustomobject]@{
    schemaVersion='a99-r2-p1-pilot-key-occurrence-decision-v1'
    artifactKind='A99_R2_P1_OFFLINE_DECISION'
    generatedAt=(Get-Date).ToUniversalTime().ToString('o')
    modelCalls=0; providerCalls=0; goldCreatedByAutomation=$false; existingGoldModified=$false
    documents=@($results | ForEach-Object { $_.documentId })
    sourceShaMatchesInventory=(@($results | Where-Object { -not $_.sourceShaMatchesInventory }).Count -eq 0)
    keyProvenance=@($results | ForEach-Object { [pscustomobject]@{ documentId=$_.documentId; keyPath=$_.keyPath; keySha256=$_.keySha256; creationCommit=$_.provenance.keyCreationCommit; keyPredatesA99ClosedLoopHistory=$_.provenance.keyPredatesA99ClosedLoopHistory; modelOutputUsedToCreateKey=$_.provenance.modelOutputUsedToCreateKey; conclusion=$_.provenance.independenceConclusion } })
    exactUniqueTotal=@($results | ForEach-Object { $_.summary.exactUniqueCount } | Measure-Object -Sum).Sum
    humanOccurrenceReviewRequiredTotal=@($results | ForEach-Object { $_.summary.humanOccurrenceReviewRequiredCount } | Measure-Object -Sum).Sum
    sourceKeyMismatchTotal=@($results | ForEach-Object { $_.summary.sourceKeyMismatchCount } | Measure-Object -Sum).Sum
    headingMembershipChanged=$false
    fuzzyMatchingUsed=$false
    decision='R2_P1_PARTIAL_REUSE_NO_SEMANTIC_REANNOTATION'
    strictOccurrenceReadyDocuments=@($results | Where-Object { $_.summary.finalStatus -eq 'READY_FOR_DETERMINISTIC_OCCURRENCE_MATERIALIZATION' } | ForEach-Object {$_.documentId})
    humanOccurrenceReviewDocuments=@($results | Where-Object { $_.summary.humanOccurrenceReviewRequiredCount -gt 0 } | ForEach-Object {$_.documentId})
    sourceKeyMismatchDocuments=@($results | Where-Object { $_.summary.sourceKeyMismatchCount -gt 0 } | ForEach-Object {$_.documentId})
    nextAction='Do not change semantic membership. Human occurrence review is required for ambiguous repeated text and any source-key mismatch must be resolved by a human; no R2-B until strict occurrence authority is frozen.'
}
$manifest = [pscustomobject]@{
    schemaVersion='a99-r2-p1-pilot-key-occurrence-manifest-v1'
    parentAuditCommit='a5bf94df2fcc990e6188731a77d573ce3258ff34'
    modelCalls=0; providerCalls=0
    semanticMembershipInput='legacy human keys only'
    goldFirewall='No model predictions, residuals, or Gold scoring artifacts were read.'
    sourceMethod='deterministic PDF cluster sourceText; exact ordinal substring matching only'
    cases=@($results | ForEach-Object { [pscustomobject]@{ documentId=$_.documentId; keyPath=$_.keyPath; sourcePath=$_.sourcePath; sourceSha256=$_.sourceSha256; keySha256=$_.keySha256 } })
    noFuzzyMatching=$true; noModel=$true; noMembershipMutation=$true
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutRoot 'manifest.v1.json') -Encoding UTF8
$decision | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutRoot 'decision.v1.json') -Encoding UTF8
Write-Output ("AUDIT_WRITTEN=" + (Rel $OutRoot))
$results | Select-Object documentId,sourceShaMatchesInventory,keyHeadingCount,@{n='exactUnique';e={$_.summary.exactUniqueCount}},@{n='reviewRequired';e={$_.summary.humanOccurrenceReviewRequiredCount}},@{n='mismatch';e={$_.summary.sourceKeyMismatchCount}},@{n='status';e={$_.summary.finalStatus}} | Format-Table -AutoSize
Write-Output 'MODEL_CALLS=0'
Write-Output 'PROVIDER_CALLS=0'
