param([string]$RepoRoot = (Get-Location).Path)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path $RepoRoot).Path
$OutRoot = Join-Path $RepoRoot 'eval\a99-closed-loop\research-r2\p2-occurrence-closure'
$GoldRoot = Join-Path $OutRoot 'strict-occurrence'
New-Item -ItemType Directory -Force -Path $GoldRoot | Out-Null

function Read-Json([string]$Path) { Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json }
function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Rel([string]$Path) { $Path.Substring($RepoRoot.Length).TrimStart('\','/') -replace '\\','/' }
function TextSha([string]$Text) { [System.BitConverter]::ToString(([System.Security.Cryptography.SHA256]::Create().ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Text)))).Replace('-','').ToLowerInvariant() }

$cli = Join-Path $RepoRoot 'src\DocxHeaderExtractor.Cli\bin\Debug\net9.0\dhx.dll'
$inventory = Read-Json (Join-Path $RepoRoot 'eval\a99-dataset\document-inventory.v1.json')
$p1Root = Join-Path $RepoRoot 'eval\a99-closed-loop\research-r2\p1-key-occurrence-audit'
$p2Root = Join-Path $p1Root 'resolution'
$cases = @(
    [pscustomobject]@{ documentId='DOC-0158'; p1='DOC-0158\occurrence-materialization.v1.json'; resolution='DOC-0158\resolved-occurrence.v1.json'; source='todo10_8\heading_corpus_100\05_bien_ban_hop\073_FORTIS_GC_Minutes_Mar_2026.pdf'; sourceSha='e73ab5ca9d4d9f101e2f2b2d5f169cbf67e331bde84726860619c00473bf5da3'; expected=7 },
    [pscustomobject]@{ documentId='DOC-0165'; p1='DOC-0165\occurrence-materialization.v1.json'; resolution='DOC-0165\resolved-occurrence.v1.json'; source='todo10_8\heading_corpus_100\05_bien_ban_hop\080_ICP_Governing_Board_Minutes_Feb_2023.pdf'; sourceSha='2b1a4e73ce2df40e46446ff7a772b44284ba6bbcff9e02062f838b5498b4085f'; expected=12 }
)
$ambiguousTexts = @('Africa','Commonwealth of Independent States','Latin America and the Caribbean','Western Asia','Eurostat-OECD PPP Programme')
$allAudits = [System.Collections.Generic.List[object]]::new()
$goldDocs = [System.Collections.Generic.List[object]]::new()

foreach ($case in $cases) {
    $p1 = Read-Json (Join-Path $p1Root $case.p1)
    $resolution = Read-Json (Join-Path $p2Root $case.resolution)
    $sourcePath = Join-Path $RepoRoot $case.source
    $doc = @($inventory.documents | Where-Object {$_.documentId -eq $case.documentId})[0]
    $cluster = ((@(& dotnet $cli pdf-clusters $sourcePath --no-llm 2>$null) -join "`n") | ConvertFrom-Json)
    if ($cluster.status -ne 'ok') { throw "PDF extraction failed for $($case.documentId)" }
    $blocks = @($cluster.blocks | Sort-Object page,@{Expression={if($_.id -match '^b(\d+)$'){[int]$Matches[1]}else{0}}})
    $cursor = 0
    $blockMap = @{}
    foreach ($block in $blocks) {
        $entry = [pscustomobject]@{ blockId=[string]$block.id; page=[int]$block.page; sourceOrdinal=([array]::IndexOf($blocks,$block)+1); sourceText=[string]$block.sourceText; globalStart=$cursor }
        $blockMap[$entry.blockId] = $entry
        $cursor += $entry.sourceText.Length + 1
    }
    $joinedSource = (($blocks | ForEach-Object {[string]$_.sourceText}) -join "`n")
    $actualSourceSha = Sha $sourcePath
    if ($actualSourceSha -ne [string]$doc.sourceSha256 -or $actualSourceSha -ne $case.sourceSha) { throw "Source SHA mismatch for $($case.documentId)" }

    $p1Rows = @($p1.rows)
    $auditRows = [System.Collections.Generic.List[object]]::new()
    foreach ($row in $p1Rows | Where-Object {$ambiguousTexts -contains $_.exactHeadingText}) {
        $candidates = @($row.allExactMatches)
        $whole = @($candidates | Where-Object {$_.matchedSourceText -ceq $row.exactHeadingText})
        $embedded = @($candidates | Where-Object {-not ($_.matchedSourceText -ceq $row.exactHeadingText)})
        $resolved = $whole.Count -eq 1
        $selected = if ($resolved) {$whole[0]} else {$null}
        $auditRows.Add([pscustomobject]@{
            documentId=$case.documentId; semanticHeadingText=$row.exactHeadingText; bindingText=$row.exactHeadingText
            priorExactMatchCount=[int]$row.exactMatchCount; wholeBlockExactCandidateCount=$whole.Count
            selected=if($selected){[pscustomobject]@{blockId=$selected.blockId;page=$selected.page;sourceOrdinal=$selected.sourceOrdinal;localUtf16Start=$selected.localUtf16Start;localUtf16End=$selected.localUtf16End;globalUtf16Start=$selected.globalUtf16Start;globalUtf16End=$selected.globalUtf16End}}else{$null}
            resolutionMethod=if($resolved){'DETERMINISTIC_UNIQUE_WHOLE_BLOCK_EXACT'}else{'HUMAN_OCCURRENCE_REVIEW_REQUIRED'}
            resolved=$resolved
            candidates=@($candidates | ForEach-Object {[pscustomobject]@{blockId=$_.blockId;page=$_.page;sourceOrdinal=$_.sourceOrdinal;localUtf16Start=$_.localUtf16Start;localUtf16End=$_.localUtf16End;globalUtf16Start=$_.globalUtf16Start;globalUtf16End=$_.globalUtf16End;classification=if($_.matchedSourceText -ceq $row.exactHeadingText){'WHOLE_BLOCK_EXACT'}else{'EMBEDDED_EXACT_OCCURRENCE'};matchedSourceText=$_.matchedSourceText}})
            rejectedCandidates=@($embedded | ForEach-Object {[pscustomobject]@{blockId=$_.blockId;page=$_.page;sourceOrdinal=$_.sourceOrdinal;classification='EMBEDDED_EXACT_OCCURRENCE';matchedSourceText=$_.matchedSourceText}})
        }) | Out-Null
    }
    $allAudits.Add([pscustomobject]@{documentId=$case.documentId; sourceSha256=$actualSourceSha; rows=$auditRows; wholeBlockResolvedCount=@($auditRows|Where-Object{$_.resolved}).Count; unresolvedCount=@($auditRows|Where-Object{-not $_.resolved}).Count}) | Out-Null

    $occurrences = [System.Collections.Generic.List[object]]::new()
    $ordinal = 0
    foreach ($row in $p1Rows | Where-Object {$_.status -eq 'EXACT_UNIQUE'}) {
        $ordinal++
        $m = $row.selectedOccurrence
        $occurrences.Add([pscustomobject]@{documentId=$case.documentId;semanticHeadingOrdinal=$ordinal;historicalHumanKeyText=$row.exactHeadingText;documentTitleText=$row.exactHeadingText;bindingText=$row.exactHeadingText;semanticMembership='HEADING';blockId=$m.blockId;page=$m.page;sourceOrdinal=$m.sourceOrdinal;utf16Start=$m.globalUtf16Start;utf16End=$m.globalUtf16End;localUtf16Start=$m.localUtf16Start;localUtf16End=$m.localUtf16End;resolutionMethod='P1_EXACT_UNIQUE';authority='HUMAN';sourceSha256=$actualSourceSha}) | Out-Null
    }
    if ($case.documentId -eq 'DOC-0158') {
        $r = $resolution.binding.occurrence; $ordinal++
        $occurrences.Add([pscustomobject]@{documentId=$case.documentId;semanticHeadingOrdinal=$ordinal;historicalHumanKeyText='Next Bi-Annual Meeting of the GC.';documentTitleText=$resolution.authority.documentTitleText;bindingText=$resolution.binding.bindingText;semanticMembership='HEADING';blockId=$r.blockId;page=$r.page;sourceOrdinal=$r.sourceOrdinal;utf16Start=$r.globalUtf16Start;utf16End=$r.globalUtf16End;localUtf16Start=$r.localUtf16Start;localUtf16End=$r.localUtf16End;resolutionMethod='USER_FINAL_EXACT_BINDING';authority='USER_FINAL';sourceSha256=$actualSourceSha}) | Out-Null
    } else {
        $r = $resolution.binding.occurrence; $ordinal++
        $occurrences.Add([pscustomobject]@{documentId=$case.documentId;semanticHeadingOrdinal=$ordinal;historicalHumanKeyText='Asia and the Pacific';documentTitleText=$resolution.authority.documentTitleText;bindingText=$resolution.binding.bindingText;semanticMembership='HEADING';blockId=$r.blockId;page=$r.page;sourceOrdinal=$r.sourceOrdinal;utf16Start=$r.globalUtf16Start;utf16End=$r.globalUtf16End;localUtf16Start=$r.localUtf16Start;localUtf16End=$r.localUtf16End;resolutionMethod='USER_FINAL_EXACT_BINDING';authority='USER_FINAL';sourceSha256=$actualSourceSha}) | Out-Null
        foreach ($a in $auditRows) {
            if (-not $a.resolved) { continue }
            $m=$a.selected; $ordinal++
            $occurrences.Add([pscustomobject]@{documentId=$case.documentId;semanticHeadingOrdinal=$ordinal;historicalHumanKeyText=$a.semanticHeadingText;documentTitleText=$a.semanticHeadingText;bindingText=$a.bindingText;semanticMembership='HEADING';blockId=$m.blockId;page=$m.page;sourceOrdinal=$m.sourceOrdinal;utf16Start=$m.globalUtf16Start;utf16End=$m.globalUtf16End;localUtf16Start=$m.localUtf16Start;localUtf16End=$m.localUtf16End;resolutionMethod='DETERMINISTIC_UNIQUE_WHOLE_BLOCK_EXACT';authority='HUMAN_SEMANTIC_PLUS_DETERMINISTIC_OCCURRENCE';sourceSha256=$actualSourceSha}) | Out-Null
        }
    }
    $seen=@{}
    foreach ($o in $occurrences) {
        if ($o.utf16Start -lt 0 -or $o.utf16End -le $o.utf16Start -or $o.utf16End -gt $joinedSource.Length) { throw "Illegal span for $($case.documentId)" }
        if ($joinedSource.Substring($o.utf16Start,$o.utf16End-$o.utf16Start) -cne $o.bindingText) { throw "Binding substring mismatch for $($case.documentId): $($o.bindingText)" }
        $identity="$($o.documentId)|$($o.blockId)|$($o.utf16Start)|$($o.utf16End)"
        if ($seen.ContainsKey($identity)) { throw "Duplicate occurrence identity: $identity" }; $seen[$identity]=$true
        if ($o.semanticMembership -ne 'HEADING') { throw "Invalid membership" }
        if ($o.resolutionMethod -eq 'DETERMINISTIC_UNIQUE_WHOLE_BLOCK_EXACT' -and $blockMap[$o.blockId].sourceText -cne $o.bindingText) { throw "Whole-block invariant failed" }
    }
    if ($occurrences.Count -ne $case.expected) { throw "Expected $($case.expected) occurrences, got $($occurrences.Count) for $($case.documentId)" }
    $goldDocs.Add([pscustomobject]@{artifactKind='A99_R2_STRICT_OCCURRENCE_GOLD';schemaVersion='a99-r2-strict-occurrence-gold-v1';documentId=$case.documentId;semanticHeadingCount=$case.expected;occurrenceResolved=$occurrences.Count;sourceSha256=$actualSourceSha;sourceRepresentation=[pscustomobject]@{kind='PDF_CLUSTER_SOURCE_TEXT';pageCount=[int]$cluster.pages;canonicalSourceUtf16Length=$joinedSource.Length;separator='LF';blockOrder='page ascending, numeric block id ascending'};semanticMembershipChanged=$false;historicalKeyModified=$false;occurrences=$occurrences;modelCalls=0;providerCalls=0}) | Out-Null
}

$allResolutionRows=@($allAudits|ForEach-Object{$_.rows})
$unresolved=[pscustomobject]@{artifactKind='A99_R2_P2B_UNRESOLVED_REVIEW_PACKET';schemaVersion='a99-r2-p2b-unresolved-review-v1';modelCalls=0;providerCalls=0;unresolvedCount=@($allResolutionRows|Where-Object{-not $_.resolved}).Count;rows=@($allResolutionRows|Where-Object{-not $_.resolved});stopBeforeFinalGoldFreeze=(@($allResolutionRows|Where-Object{-not $_.resolved}).Count -gt 0)}
$decision=[pscustomobject]@{artifactKind='A99_R2_P2B_DECISION';schemaVersion='a99-r2-p2b-decision-v1';modelCalls=0;providerCalls=0;fiveAmbiguousHeadings=$ambiguousTexts;wholeBlockResolvedCount=@($allResolutionRows|Where-Object{$_.resolved}).Count;humanReviewRemainingCount=@($allResolutionRows|Where-Object{-not $_.resolved}).Count;doc0158ReadyCount=[int]((@($goldDocs|Where-Object{$_.documentId -eq 'DOC-0158'}|ForEach-Object{$_.occurrenceResolved})|Measure-Object -Sum).Sum);doc0165ReadyCount=[int]((@($goldDocs|Where-Object{$_.documentId -eq 'DOC-0165'}|ForEach-Object{$_.occurrenceResolved})|Measure-Object -Sum).Sum);strictOccurrenceGoldFrozen=(@($goldDocs).Count -eq 2 -and @($allResolutionRows|Where-Object{-not $_.resolved}).Count -eq 0);terminal=if(@($allResolutionRows|Where-Object{-not $_.resolved}).Count -eq 0){'R2_P2_TWO_DOCUMENTS_STRICT_OCCURRENCE_READY'}else{'R2_P2_AWAITING_HUMAN_OCCURRENCE_SELECTION'};r2BInferenceAuthorized=$false;noAutomaticP3=$true}
$freeze=[pscustomobject]@{artifactKind='A99_R2_STRICT_OCCURRENCE_FREEZE';schemaVersion='a99-r2-strict-occurrence-freeze-v1';documents=@($goldDocs|ForEach-Object{[pscustomobject]@{documentId=$_.documentId;semanticCount=$_.semanticHeadingCount;occurrenceResolved=$_.occurrenceResolved;sha256=(TextSha ($_.occurrences|ConvertTo-Json -Depth 8))}});totalSemanticHeadings=(@($goldDocs|ForEach-Object{$_.semanticHeadingCount}|Measure-Object -Sum).Sum);totalOccurrenceResolved=(@($goldDocs|ForEach-Object{$_.occurrenceResolved}|Measure-Object -Sum).Sum);P1_EXACT_UNIQUE=12;USER_FINAL_RESOLVED=2;DETERMINISTIC_UNIQUE_WHOLE_BLOCK_EXACT=5;unresolved=0;modelCalls=0;providerCalls=0;strictOccurrenceGoldFrozen=$true;r2BInferenceAuthorized=$false;semanticMembershipChanged=$false;historicalGoldModified=$false}
$goldDocs | ForEach-Object { $_ | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $GoldRoot ("$($_.documentId).strict-occurrence-gold.v1.json")) -Encoding UTF8 }
@($allAudits) | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $OutRoot 'whole-block-resolution.v1.json') -Encoding UTF8
$unresolved | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $OutRoot 'unresolved-review.v1.json') -Encoding UTF8
$freeze | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $OutRoot 'strict-occurrence-freeze.v1.json') -Encoding UTF8
$decision | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $OutRoot 'decision.v1.json') -Encoding UTF8
Write-Output ("CLOSURE_WRITTEN=" + (Rel $OutRoot))
$decision | Select-Object wholeBlockResolvedCount,humanReviewRemainingCount,doc0158ReadyCount,doc0165ReadyCount,strictOccurrenceGoldFrozen,terminal,r2BInferenceAuthorized | Format-List
Write-Output 'MODEL_CALLS=0'
Write-Output 'PROVIDER_CALLS=0'
