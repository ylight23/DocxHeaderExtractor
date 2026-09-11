param([string]$RepoRoot = (Get-Location).Path)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path $RepoRoot).Path
$OutRoot = Join-Path $RepoRoot 'eval\a99-closed-loop\research-r2\p1-key-occurrence-audit\resolution'
New-Item -ItemType Directory -Force -Path $OutRoot | Out-Null

function Read-Json([string]$Path) { Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json }
function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Rel([string]$Path) { $Path.Substring($RepoRoot.Length).TrimStart('\','/') -replace '\\','/' }

$cli = Join-Path $RepoRoot 'src\DocxHeaderExtractor.Cli\bin\Debug\net9.0\dhx.dll'
$inventory = Read-Json (Join-Path $RepoRoot 'eval\a99-dataset\document-inventory.v1.json')
$cases = @(
    [pscustomobject]@{ documentId='DOC-0158'; keyPath='keys\format-driven-human\073_FORTIS_GC_Minutes_Mar_2026.key'; sourcePath='todo10_8\heading_corpus_100\05_bien_ban_hop\073_FORTIS_GC_Minutes_Mar_2026.pdf'; oldText='Next Bi-Annual Meeting of the GC.'; documentTitleText='Next Bi-Annual Meeting of the GC'; bindingText='Next Bi-Annual Meeting ofthe GC.'; stableId='@body[1]/p[6]' },
    [pscustomobject]@{ documentId='DOC-0165'; keyPath='keys\format-driven-human\080_ICP_Governing_Board_Minutes_Feb_2023.key'; sourcePath='todo10_8\heading_corpus_100\05_bien_ban_hop\080_ICP_Governing_Board_Minutes_Feb_2023.pdf'; oldText='Asia and the Pacific'; documentTitleText='Asia and Pacific'; bindingText='Asia and Pacific'; stableId='@body[1]/p[8]' }
)

$resolved = [System.Collections.Generic.List[object]]::new()
foreach ($case in $cases) {
    $source = Join-Path $RepoRoot $case.sourcePath
    $key = Join-Path $RepoRoot $case.keyPath
    $doc = @($inventory.documents | Where-Object {$_.documentId -eq $case.documentId})[0]
    if ($null -eq $doc) { throw "Inventory document missing: $($case.documentId)" }
    $raw = @(& dotnet $cli pdf-clusters $source --no-llm 2>$null) -join "`n"
    $cluster = $raw | ConvertFrom-Json
    if ($cluster.status -ne 'ok') { throw "Deterministic PDF extraction failed: $($case.documentId)" }
    $blocks = @($cluster.blocks | Sort-Object page,@{Expression={if($_.id -match '^b(\d+)$'){[int]$Matches[1]}else{0}}})
    $cursor = 0
    $matches = [System.Collections.Generic.List[object]]::new()
    foreach ($b in $blocks) {
        $text = [string]$b.sourceText
        $pos = 0
        while ($pos -le $text.Length) {
            $found = $text.IndexOf($case.bindingText,$pos,[System.StringComparison]::Ordinal)
            if ($found -lt 0) { break }
            $matches.Add([pscustomobject]@{
                blockId=[string]$b.id; page=[int]$b.page; localUtf16Start=$found; localUtf16End=$found+$case.bindingText.Length
                globalUtf16Start=$cursor+$found; globalUtf16End=$cursor+$found+$case.bindingText.Length
                matchedSourceText=$text
            }) | Out-Null
            $pos=$found+[Math]::Max(1,$case.bindingText.Length)
        }
        $cursor += $text.Length + 1
    }
    $sourceSha = Sha $source
    $sourceShaMatches = $sourceSha -eq [string]$doc.sourceSha256
    $status = if (-not $sourceShaMatches) {'SOURCE_HASH_MISMATCH'} elseif ($matches.Count -eq 1) {'RESOLVED_EXACT_UNIQUE'} elseif ($matches.Count -gt 1) {'HUMAN_OCCURRENCE_REVIEW_REQUIRED'} else {'SOURCE_KEY_MISMATCH'}
    $artifact = [pscustomobject]@{
        artifactKind='A99_R2_P2_USER_AUTHORITY_RESOLUTION'
        schemaVersion='a99-r2-p2-user-authority-resolution-v1'
        documentId=$case.documentId
        historicalKey=[pscustomobject]@{ path=(Rel $key); sha256=(Sha $key); originalText=$case.oldText; modified=$false }
        authority=[pscustomobject]@{ documentTitleText=$case.documentTitleText; semanticMembership='HEADING'; resolutionAuthority='USER_FINAL' }
        binding=[pscustomobject]@{ bindingText=$case.bindingText; exactMatchCount=$matches.Count; status=$status; occurrence=if($status -eq 'RESOLVED_EXACT_UNIQUE'){$matches[0]}else{$null}; allExactMatches=@($matches); matchingMethod='ordinal exact substring; UTF-16 code-unit offsets'; fuzzyMatchingUsed=$false }
        source=[pscustomobject]@{ path=(Rel $source); sha256=$sourceSha; inventorySha256=[string]$doc.sourceSha256; shaMatchesInventory=$sourceShaMatches; extractor='dhx pdf-clusters --no-llm'; pageCount=[int]$cluster.pages; sourceRepresentation='PDF_CLUSTER_SOURCE_TEXT' }
        modelCalls=0; providerCalls=0; goldCreatedByAutomation=$false; semanticMembershipChanged=$false
        note='User authority override is stored separately; historical .key remains immutable. bindingText preserves the extracted representation needed for deterministic binding.'
    }
    $resolved.Add($artifact) | Out-Null
    $dir=Join-Path $OutRoot $case.documentId; New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $artifact | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $dir 'resolved-occurrence.v1.json') -Encoding UTF8
}

$resolution = [pscustomobject]@{
    artifactKind='A99_R2_P2_USER_FINAL_AUTHORITY_RESOLUTION'
    schemaVersion='a99-r2-p2-resolution-v1'
    modelCalls=0; providerCalls=0; goldCreatedByAutomation=$false; historicalKeysModified=$false; semanticMembershipChanged=$false
    resolutions=@($resolved | ForEach-Object { [pscustomobject]@{ documentId=$_.documentId; documentTitleText=$_.authority.documentTitleText; semanticMembership=$_.authority.semanticMembership; resolutionAuthority=$_.authority.resolutionAuthority; bindingText=$_.binding.bindingText } })
    resolutionStatuses=@($resolved | ForEach-Object { [pscustomobject]@{ documentId=$_.documentId; bindingStatus=$_.binding.status; exactMatchCount=$_.binding.exactMatchCount; sourceShaMatchesInventory=$_.source.shaMatchesInventory } })
    contract='semanticMembership=HEADING; authority=USER_FINAL'
    historicalKeyPolicy='Historical .key files are immutable and remain the provenance record.'
    decision='USER_AUTHORITY_RESOLUTION_RECORDED_NO_SEMANTIC_REANNOTATION'
    nextAction='Human review only for any remaining non-unique or mismatched binding; do not alter semantic membership and do not run R2-B until occurrence authority is frozen.'
}
$resolution | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutRoot 'resolution.v1.json') -Encoding UTF8
Write-Output ("RESOLUTION_WRITTEN=" + (Rel $OutRoot))
$resolution.resolutionStatuses | Format-Table -AutoSize
Write-Output 'MODEL_CALLS=0'
Write-Output 'PROVIDER_CALLS=0'
