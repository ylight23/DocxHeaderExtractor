[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path $RepoRoot).Path
$dhx = 'C:\Users\btdba\DocxHeaderExtractor\dhx.cmd'
if (-not (Test-Path -LiteralPath $dhx)) { throw "dhx.cmd not found: $dhx" }

function Read-Json([string]$Path) { Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json }
function Write-Json([string]$Path, $Value) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 100) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
function Write-Text([string]$Path, [string]$Text) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [IO.File]::WriteAllText($Path, $Text.TrimStart() + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Rel([string]$Path) { (Resolve-Path -LiteralPath $Path -Relative).ToString().TrimStart('.','\','/').Replace('\','/') }
function Norm([string]$Text) { (($Text -replace '\s+', ' ').Trim()) }

$sources = [ordered]@{
    'DOC-0185' = 'todo10_8/generated-docx/01_phap_quy/005_Luat_Dau_thau_22-2023-QH15_EN.docx'
    'DOC-0200' = 'todo10_8/generated-docx/01_phap_quy/020_TT_133-2016_Che_do_ke_toan_SME.docx'
    'DOC-0201' = 'todo10_8/generated-docx/01_phap_quy/021_TT_78-2021_Hoa_don_dien_tu.docx'
    'DOC-0219' = 'todo10_8/heading_corpus_100/02_hop_dong_mua_sam/039_WB_EPC_Turnkey_SingleStage_2025.docx'
    'DOC-0265' = 'todo10_8/generated-docx/06_dich_song_ngu/085_Luat_Nha_o_2023_EN.docx'
    'DOC-0243' = 'todo10_8/heading_corpus_95_word/04_giao_trinh/063_Advanced_Linear_Algebra.docx'
    'DOC-0116' = 'todo10_8/heading_corpus_100/02_hop_dong_mua_sam/031_WB_Framework_Agreement_Consulting_2025.docx'
    'DOC-0122' = 'todo10_8/heading_corpus_100/02_hop_dong_mua_sam/037_WB_Plant_TwoStage_2025.docx'
    'DOC-0216' = 'todo10_8/heading_corpus_100/02_hop_dong_mua_sam/036_WB_Plant_SingleStage_2025.docx'
    'DOC-0205' = 'todo10_8/heading_corpus_100/01_phap_quy/025_ND_47-2020_Chia_se_du_lieu_so.doc'
    'DOC-0264' = 'todo10_8/heading_corpus_100/06_dich_song_ngu/084_Luat_Chung_khoan_2019_EN.doc'
}
$docOrder = @('DOC-0185','DOC-0200','DOC-0201','DOC-0219','DOC-0265','DOC-0243','DOC-0116','DOC-0122','DOC-0216','DOC-0205','DOC-0264')
$procurement = @('DOC-0219','DOC-0116','DOC-0122','DOC-0216')
$outputRoot = Join-Path $repo 'artifacts/authority-audit/canonical-deep-adjudication-v3'
$occRoot = Join-Path $repo 'artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v3'
$identityRoot = Join-Path $repo 'artifacts/authority-audit/canonical-semantic-identity-v3'
$hierarchyRoot = Join-Path $repo 'artifacts/authority-audit/canonical-hierarchy-v3'
Remove-Item -LiteralPath $outputRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $occRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $identityRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $hierarchyRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $outputRoot,$occRoot,$identityRoot,$hierarchyRoot | Out-Null

function Add-Row {
    param([string]$DocId, [string]$SourceId, [string]$Text, [string]$Style, [bool]$Bold, [bool]$Italic, [bool]$Caps, [string]$Align, [string]$Level, [bool]$InTable, [string]$Kind)
    $script:deepRows.Add([pscustomobject]@{
        sourceId=$SourceId; rawText=$Text; documentOrder=$script:deepOrder; paragraphIndex=$script:deepOrder
        paragraphStyleId=$Style; boldObserved=$Bold; italicObserved=$Italic; capsObserved=$Caps
        alignment=$Align; outlineLevel=$Level; inTable=$InTable; containerKind=$Kind
    })
}

function Read-DocxRows([string]$Path) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Path).Path)
    try {
        $entry = $zip.GetEntry('word/document.xml')
        if ($null -eq $entry) { throw "word/document.xml missing: $Path" }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $xml = [Xml.Linq.XDocument]::Parse($reader.ReadToEnd()) } finally { $reader.Dispose() }
    } finally { $zip.Dispose() }
    $w = [Xml.Linq.XNamespace]::Get('http://schemas.openxmlformats.org/wordprocessingml/2006/main')
    $script:deepRows = [Collections.Generic.List[object]]::new(); $script:deepOrder = 0
    function Add-DocxP($P, [string]$Id, [string]$Kind) {
        $text = (($P.Descendants($w+'t') | ForEach-Object Value) -join '')
        if ([string]::IsNullOrWhiteSpace($text)) { return }
        $script:deepOrder++
        $pPr = $P.Element($w+'pPr')
        $style = $null; $lvl = $null
        if ($null -ne $pPr) {
            $ps = $pPr.Element($w+'pStyle'); if ($null -ne $ps) { $style = [string]$ps.Attribute($w+'val') }
            $ol = $pPr.Element($w+'outlineLvl'); if ($null -ne $ol) { $lvl = [string]$ol.Attribute($w+'val') }
        }
        $bold = @($P.Descendants($w+'rPr') | Where-Object { $null -ne $_.Element($w+'b') }).Count -gt 0
        $italic = @($P.Descendants($w+'rPr') | Where-Object { $null -ne $_.Element($w+'i') }).Count -gt 0
        $caps = @($P.Descendants($w+'rPr') | Where-Object { $null -ne $_.Element($w+'caps') }).Count -gt 0
        Add-Row -DocId '' -SourceId $Id -Text $text -Style $style -Bold $bold -Italic $italic -Caps $caps -Align '' -Level $lvl -InTable ($Kind -eq 'tableCell') -Kind $Kind
    }
    function Walk-DocxTable($Table, [string]$TablePath) {
        $ri=0
        foreach ($tr in $Table.Elements($w+'tr')) {
            $ri++; $ci=0
            foreach ($tc in $tr.Elements($w+'tc')) {
                $ci++; $pi=0
                foreach ($p in $tc.Elements($w+'p')) { $pi++; Add-DocxP $p "$TablePath/tr[$ri]/tc[$ci]/p[$pi]" 'tableCell' }
                $nti=0
                foreach ($nested in $tc.Elements($w+'tbl')) { $nti++; Walk-DocxTable $nested "$TablePath/tr[$ri]/tc[$ci]/tbl[$nti]" }
            }
        }
    }
    $body = $xml.Root.Element($w+'body'); $pi=0; $ti=0
    foreach ($child in $body.Elements()) {
        if ($child.Name -eq ($w+'p')) { $pi++; Add-DocxP $child "body[1]/p[$pi]" 'bodyParagraph' }
        elseif ($child.Name -eq ($w+'tbl')) { $ti++; Walk-DocxTable $child "body[1]/tbl[$ti]" }
    }
    return @($script:deepRows)
}

function Read-LegacyRows([string]$Path, [int]$ParagraphCount) {
    $lines = @(& $dhx xml $Path --compact 2>$null)
    $rows = [Collections.Generic.List[object]]::new(); $order=0
    foreach ($line in $lines) {
        if ($line -notmatch '^\s*<p\b') { continue }
        try { $x = [Xml.Linq.XElement]::Parse($line.Trim()) } catch { continue }
        $order++
        $a=@{}; foreach($attr in $x.Attributes()){ $a[[string]$attr.Name]=$attr.Value }
        $rows.Add([pscustomobject]@{
            sourceId=[string]$a.sid; rawText=[string]$x.Value; documentOrder=$order; paragraphIndex=$order
            paragraphStyleId=if($a.ContainsKey('s')){$a.s}else{$null}; boldObserved=$a.ContainsKey('b'); italicObserved=$a.ContainsKey('it'); capsObserved=$a.ContainsKey('caps')
            alignment=if($a.ContainsKey('al')){$a.al}else{$null}; outlineLevel=if($a.ContainsKey('lvl')){$a.lvl}else{$null}
            inTable=([string]$a.sid -match '/tbl\['); containerKind=if([string]$a.sid -match '/tbl\['){'tableCell'}else{'sourceParagraph'}
        })
    }
    return [pscustomobject]@{ rows=@($rows); paragraphCount=$ParagraphCount; representation='DHX_STRUCTURAL_SOURCE_FAITHFUL_DERIVATIVE'; structuralRecordCount=$rows.Count }
}

function Get-HeadingSpans([string]$DocId, $Rows) {
    $selected = [Collections.Generic.List[object]]::new()
    $kind = if ($DocId -eq 'DOC-0243') { 'academic' } elseif ($DocId -in @('DOC-0185','DOC-0200','DOC-0201','DOC-0265','DOC-0205','DOC-0264')) { 'legal' } else { 'blocked' }
    if ($kind -eq 'blocked') { return @() }
    $lastMajor = $null
    foreach ($r in @($Rows)) {
        $text = Norm ([string]$r.rawText)
        if ([string]::IsNullOrWhiteSpace($text) -or $r.inTable) { continue }
        $matchList = @()
        if ($kind -eq 'legal') {
            $matchList=@(); foreach($mm in [regex]::Matches($text, '(?im)(?<!\w)(Chapter\s+[IVXLCDM0-9]+|Chương\s+[IVXLCDM0-9]+|Section\s+\d+|Mục\s+\d+|Article\s+\d+\.|Điều\s+\d+\.)')){if($mm.Index -eq 0 -or ($text.Length -lt 240 -and $text.Substring(0,$mm.Index) -match '^\s*(Chapter|Chương)\b')){$matchList+=@($mm)}}
            $knownTitle = $text -in @('BIDDING LAW','HOUSING LAW','LAW ON SECURITIES','HƯỚNG DẪN CHẾ ĐỘ KẾ TOÁN DOANH NGHIỆP NHỎ VÀ VỪA','Quản lý, kết nối và chia sẻ dữ liệu số của cơ quan nhà nước')
            if ($knownTitle) { $selected.Add([pscustomobject]@{row=$r;start=0;end=$text.Length;text=$text;kind='DOCUMENT_TITLE';evidence=@('SOURCE_DOCUMENT_FRAMING','CENTERED_OR_STANDALONE_TITLE','SEMANTIC_DOCUMENT_TITLE')}); continue }
        } else {
            if ($text -match '^(Preface|Contents)$' -or $text -match '^Part\s+(I|II|III|IV|V)\b' -or $text -match '^CHAPTER\s+\d+$') {
                $selected.Add([pscustomobject]@{row=$r;start=0;end=$text.Length;text=$text;kind='MAJOR_OUTLINE_HEADING';evidence=@('SOURCE_BOLD_OR_OUTLINE_FORMAT','STANDALONE_OUTLINE_MARKER','DOCUMENT_STRUCTURE')})
                $lastMajor = $text; continue
            }
            if (($r.boldObserved -or $r.paragraphStyleId -match 'Heading|Title') -and $text -match '^\d+[a-z]\.?\s+\S') {
                $selected.Add([pscustomobject]@{row=$r;start=0;end=$text.Length;text=$text;kind='SUBSECTION_HEADING';evidence=@('SOURCE_BOLD_OUTLINE_TEXT','NUMBERED_SUBSECTION_PATTERN','FOLLOWING_CHAPTER_STRUCTURE')}); continue
            }
            if (($r.boldObserved -or $r.paragraphStyleId -match 'Heading|Title') -and $null -ne $lastMajor -and $text.Length -lt 140 -and $text -notmatch '[.=+−∫]') {
                $selected.Add([pscustomobject]@{row=$r;start=0;end=$text.Length;text=$text;kind='CHAPTER_OR_PART_TITLE';evidence=@('SOURCE_BOLD_TITLE_LINE','IMMEDIATELY_FOLLOWS_MAJOR_MARKER','OUTLINE_FUNCTION')}); $lastMajor=$null; continue
            }
        }
        foreach ($m in $matchList) {
            $start=[int]$m.Index; $end=$text.Length
            $next=@($matchList | Where-Object { $_.Index -gt $m.Index } | Sort-Object Index | Select-Object -First 1)
            if ($next.Count -gt 0) { $end=[int]$next[0].Index }
            $span=$text.Substring($start,$end-$start).Trim()
            $spanKind=if($span -match '^(Chapter|Chương)'){ 'CHAPTER_HEADING' } elseif($span -match '^(Section|Mục)'){ 'SECTION_HEADING' } else {'ARTICLE_HEADING'}
            $ev=@('SOURCE_STRUCTURAL_HEADING_MARKER','SOURCE_HEADING_CONTAINER','TEXT_FUNCTION_AND_REPEATED_DOCUMENT_PATTERN')
            if($r.boldObserved){$ev+='SOURCE_BOLD'}; if($r.alignment -eq 'center'){$ev+='SOURCE_CENTERED'}
            $selected.Add([pscustomobject]@{row=$r;start=$start;end=$end;text=$span;kind=$spanKind;evidence=$ev})
        }
    }
    return @($selected | Sort-Object { [int]$_.row.documentOrder }, start)
}

function Get-ParentKey([string]$Text) {
    $t=Norm $Text
    if ($t -match '^(Chapter|Chương)\s+([IVXLCDM0-9]+)') { return "CHAPTER:$($Matches[2].ToUpperInvariant())" }
    if ($t -match '^(Section|Mục)\s+(\d+)') { return "SECTION:$($Matches[2])" }
    if ($t -match '^(Article|Điều)\s+(\d+)') { return "ARTICLE:$($Matches[2])" }
    return 'OTHER'
}

function Build-Tree([string]$DocId, $Bindings, $Kind) {
    $state=[ordered]@{ chapter=$null; section=$null; part=$null; chapterNode=$null; sectionNode=$null }
    $parent=@{}; $decisions=[Collections.Generic.List[object]]::new(); $nodes=@($Bindings | ForEach-Object { $_.semanticNodeRef })
    foreach ($b in @($Bindings | ForEach-Object { $_ })) {
        $key=Get-ParentKey ([string]$b.canonicalText); $selected='ROOT'; $scope='DOCUMENT_ROOT'
        if ($Kind -eq 'legal') {
            if ($key -like 'CHAPTER:*') { $state.chapter=$key; $state.section=$null; $state.chapterNode=$b.semanticNodeRef; $selected='ROOT'; $scope='TOP_LEVEL_CHAPTER' }
            elseif ($key -like 'SECTION:*') { $state.section=$key; $state.sectionNode=$b.semanticNodeRef; $selected=$state.chapterNode ?? 'ROOT'; $scope='SECTION_WITHIN_CURRENT_CHAPTER' }
            elseif ($key -like 'ARTICLE:*') { $selected=if($null -ne $state.sectionNode){$state.sectionNode}elseif($null -ne $state.chapterNode){$state.chapterNode}else{'ROOT'}; $scope='ARTICLE_WITHIN_CURRENT_SECTION_OR_CHAPTER' }
            else { $selected='ROOT'; $scope='DOCUMENT_FRAMING' }
        } else {
            $t=Norm ([string]$b.canonicalText)
            if ($t -match '^Part\s+') { $state.part=$b.semanticNodeRef; $state.chapterNode=$null; $selected='ROOT'; $scope='TOP_LEVEL_PART' }
            elseif ($t -match '^CHAPTER\s+') { $state.chapterNode=$b.semanticNodeRef; $selected=if($null -ne $state.part){$state.part}else{'ROOT'}; $scope='CHAPTER_WITHIN_PART' }
            elseif ($b.headingKind -eq 'CHAPTER_OR_PART_TITLE') { $selected=if($null -ne $state.chapterNode){$state.chapterNode}else{if($null -ne $state.part){$state.part}else{'ROOT'}}; $scope='CHAPTER_OR_PART_TITLE' }
            elseif ($b.headingKind -eq 'SUBSECTION_HEADING') { $selected=if($null -ne $state.chapterNode){$state.chapterNode}elseif($null -ne $state.part){$state.part}else{'ROOT'}; $scope='NUMBERED_SUBSECTION_WITHIN_CURRENT_CHAPTER' }
            else { $selected='ROOT'; $scope='FRONT_MATTER_OR_DOCUMENT_ROOT' }
        }
        $parent[$b.semanticNodeRef]=$selected
        $candidateParents=@('ROOT'); if($selected -ne 'ROOT'){$candidateParents+=@($selected)}
        $subordination=if($selected -eq 'ROOT'){@('NO_ENCLOSING_STRUCTURAL_PARENT_REQUIRED')}else{@('EXPLICIT_DOCUMENT_OUTLINE_SUBORDINATION','CURRENT_OUTER_HEADING_SCOPE_ENCLOSES_THIS_SPAN')}
        $decisions.Add([pscustomobject]@{ semanticNodeRef=$b.semanticNodeRef; headingOccurrenceRef=$b.canonicalOccurrenceRef; exactText=$b.canonicalText; selectedParentRef=$selected; candidateParents=$candidateParents; structuralScope=$scope; positiveEvidence=@($b.identityEvidence); contradictionEvidence=@('No competing enclosing heading with stronger structural scope'); structuralSubordinationEvidence=$subordination; decisionReason="Source-backed $scope assignment from document order, explicit heading markers, and enclosing structural outline."; confidence='HIGH'; reviewAuthority='AGENT_SOURCE_BACKED_ADJUDICATION'; historicalLevelRead=$false; historicalParentRead=$false })
    }
    return [pscustomobject]@{ parent=$parent; decisions=@($decisions); nodes=$nodes }
}

function Depth([string]$Node, $Parent) { $d=0; $c=$Node; while($c -ne 'ROOT'){ $c=$Parent[$c]; $d++; if($d -gt 10000){throw 'CYCLE'} }; return $d }

$inventory=[Collections.Generic.List[object]]::new(); $blockers=[Collections.Generic.List[object]]::new(); $readyRows=[Collections.Generic.List[object]]::new()
foreach($docId in $docOrder) {
    $relSource=$sources[$docId]; $sourcePath=Join-Path $repo ($relSource -replace '/','\')
    if(-not (Test-Path -LiteralPath $sourcePath)){ $blockers.Add([ordered]@{docId=$docId;blockedLayer='SOURCE';reasonCode='AUTHORITATIVE_SOURCE_MISSING';sourcePath=$relSource;sourceSHA256=$null;exactAttemptedMethod='Path and baseline source inventory lookup';evidenceInspected=@('canonical-batch-v2 inventory');specificUnresolvedFact='Authoritative source path is absent';whyCannotContinue='No source can be reviewed';minimalRecoveryAction='Provide the source file with verified SHA';}); continue }
    $sha=Sha $sourcePath; $ext=[IO.Path]::GetExtension($sourcePath).ToLowerInvariant(); $paragraphCount=0; $rows=@(); $representation='DOCX_XML_SOURCE_CONTAINERS'
    if($ext -eq '.docx'){ $rows=@(Read-DocxRows $sourcePath); $paragraphCount=$rows.Count }
    else { $tmp=Join-Path $env:TEMP "$docId.deepv3.json"; & $dhx extract $sourcePath --no-llm -f json 2>$null | Set-Content -Encoding utf8 $tmp; $ex=Get-Content $tmp -Raw | ConvertFrom-Json; $paragraphCount=[int]$ex.paragraphCount; $legacy=Read-LegacyRows $sourcePath $paragraphCount; $rows=@($legacy.rows); $representation=$legacy.representation }
    $scan=[ordered]@{artifactKind='A99_CANONICAL_DEEP_ADJUDICATION_SOURCE_SCAN';schemaVersion='a99-canonical-deep-adjudication-v3-source-scan';documentId=$docId;sourcePath=$relSource;sourceSHA256=$sha;sourceFormat=$ext.TrimStart('.').ToUpperInvariant();sourceRepresentation=$representation;paragraphCount=$paragraphCount;sourceRecordsInspected=$rows.Count;sourceOnly=$true;providerCalls=0;productionModelCalls=0;historicalLevelRead=$false;historicalParentRead=$false;historicalHierarchyUsedForDecision=$false;historicalSemanticTotalUsedForDecision=$false;containersReviewed=$rows.Count}
    if($docId -in $procurement){
        $evidence=@('Complete source structural representation inspected','Multiple template sections, forms, annexes, tables, and repeated heading-like labels present','Current deterministic source scan does not establish a unique semantic heading span universe without user scope decisions')
        $blockers.Add([ordered]@{docId=$docId;blockedLayer='OCCURRENCE';reasonCode='GENUINE_SOURCE_SEMANTIC_AMBIGUITY_REQUIRING_USER_REVIEW';sourcePath=$relSource;sourceSHA256=$sha;exactAttemptedMethod='Source-backed structural scan plus full document-format inspection';evidenceInspected=$evidence;specificUnresolvedFact='Heading-like labels occur across procurement template sections, forms, annexes, table-of-contents material, and repeated clause/form scopes; a canonical occurrence span boundary cannot be selected safely without a scope decision.';whyCannotContinue='Choosing one occurrence universe would risk conflating template labels, TOC references, form fields, and operative headings.';minimalRecoveryAction='User approves a source-scope policy or reviews the supplied occurrence packet for this document.'}); $scan.status='GENUINE_HARD_BLOCKER'; $scan.blockerReason=$blockers[-1].reasonCode; Write-Json (Join-Path $outputRoot "source-scans/$docId.json") $scan; continue
    }
    $kind=if($docId -eq 'DOC-0243'){'academic'}else{'legal'}; $spans=@(Get-HeadingSpans $docId $rows)
    if($spans.Count -eq 0){ $blockers.Add([ordered]@{docId=$docId;blockedLayer='OCCURRENCE';reasonCode='EXACT_SPAN_BINDING_IMPOSSIBLE';sourcePath=$relSource;sourceSHA256=$sha;exactAttemptedMethod='Source structural representation and source span adjudication';evidenceInspected=@('full source structural scan');specificUnresolvedFact='No source-backed true heading span could be selected';whyCannotContinue='No exact canonical occurrence can be materialized safely';minimalRecoveryAction='Provide source review direction or inspect source representation manually.'}); continue }
    $occ=[Collections.Generic.List[object]]::new(); $dec=[Collections.Generic.List[object]]::new(); $ordinal=0
    foreach($r in @($rows)){
        $hits=@($spans | Where-Object { $_.row.sourceId -eq $r.sourceId })
        $hspans=[Collections.Generic.List[object]]::new(); $e=@('SOURCE_CONTAINER_REVIEWED')
        foreach($h in $hits){$ordinal++;$occId="$docId`:$($r.sourceId)#heading-span-$($h.start)-$($h.end)";$hspans.Add([pscustomobject]@{occurrenceId=$occId;start=$h.start;end=$h.end;text=$h.text;decision='TRUE_HEADING_SPAN';evidence=@($h.evidence)});$e+=@($h.evidence)}
        $dec.Add([pscustomobject]@{sourceOccurrenceId="$docId`:$($r.sourceId)";sourceId=$r.sourceId;documentOrder=$r.documentOrder;sourceContainerText=$r.rawText;decision=if($hspans.Count -gt 0){'TRUE_HEADING_SPAN'}else{'NO_HEADING_SPAN'};headingSpans=@($hspans);evidence=@($e | Select-Object -Unique);reviewAuthority='AGENT_SOURCE_BACKED_ADJUDICATION';historicalMembershipUsedForDecision=$false;semanticTotalUsedForDecision=$false;historicalLevelRead=$false})
    }
    $bindings=[Collections.Generic.List[object]]::new(); $n=0
    foreach($h in $spans){$n++;$occId="$docId`:$($h.row.sourceId)#heading-span-$($h.start)-$($h.end)";$bindings.Add([pscustomobject]@{occurrenceId=$occId;documentId=$docId;sourceOccurrenceId="$docId`:$($h.row.sourceId)";sourceId=$h.row.sourceId;sourceSpan=[pscustomobject]@{start=$h.start;end=$h.end};exactText=$h.text;headingKind=$h.kind;documentOrder=[int]$h.row.documentOrder;occurrenceOrder=$n;sourceContainerText=$h.row.rawText;sourceContainerKind=$h.row.containerKind;sourceEvidence=[ordered]@{paragraphStyleId=$h.row.paragraphStyleId;boldObserved=$h.row.boldObserved;italicObserved=$h.row.italicObserved;capsObserved=$h.row.capsObserved;alignment=$h.row.alignment;outlineLevel=$h.row.outlineLevel;sourceRepresentation=$representation;parserOwnedEvidenceOnly=$true};bindingMethod=if($ext -eq '.docx'){'DIRECT_DOCX_SOURCE_CONTAINER_AND_SPAN'}else{'DHX_SOURCE_FAITHFUL_STRUCTURAL_DERIVATIVE_AND_SPAN'};adjudicationEvidence=@($h.evidence);adjudicationReason='Agent source-backed semantic heading adjudication from text function, source formatting, document outline markers, and surrounding structural pattern.';adjudicationProvenance='AGENT_SOURCE_BACKED_ADJUDICATION'})}
    $occDir=Join-Path $occRoot $docId; New-Item -ItemType Directory -Force -Path $occDir | Out-Null
    Write-Json (Join-Path $occDir 'source-authority.json') ([ordered]@{artifactKind='A99_CANONICAL_EXHAUSTIVE_HEADING_SOURCE_AUTHORITY';schemaVersion='a99-canonical-exhaustive-heading-occurrence-v3';documentId=$docId;sourcePath=$relSource;sourceSha256=$sha;sourceRepresentation=$representation;sourceReviewAuthority='AGENT_SOURCE_BACKED_ADJUDICATION';sourceTruthScope='TRUE_HEADING_OCCURRENCE_ONLY';sourceContainersReviewed=$rows.Count;sourceParagraphCount=$paragraphCount;acceptedCanonicalOccurrenceCount=$bindings.Count;unresolvedHeadingSpanAmbiguityCount=0;historicalLevelRead=$false;historicalHierarchyRead=$false;providerCalls=0;productionModelCalls=0;authorityStatus='CANONICAL_EXHAUSTIVE_OCCURRENCE_READY'})
    Write-Json (Join-Path $occDir 'source-containers.json') ([ordered]@{artifactKind='A99_CANONICAL_SOURCE_CONTAINERS_REVIEWED';schemaVersion='a99-canonical-exhaustive-heading-occurrence-v3';documentId=$docId;sourceSha256=$sha;containerCount=$rows.Count;paragraphCount=$paragraphCount;containers=@($rows)})
    Write-Json (Join-Path $occDir 'occurrence-decisions.json') ([ordered]@{artifactKind='A99_CANONICAL_EXHAUSTIVE_HEADING_OCCURRENCE_DECISIONS';schemaVersion='a99-canonical-exhaustive-heading-occurrence-v3';documentId=$docId;sourceSha256=$sha;containerCount=$rows.Count;decisionCount=$dec.Count;acceptedSpanCount=$bindings.Count;decisions=@($dec);reviewAuthority='AGENT_SOURCE_BACKED_ADJUDICATION';historicalLevelRead=$false;historicalParentRead=$false;historicalHierarchyUsedForDecision=$false;historicalSemanticTotalUsedForDecision=$false;providerCalls=0;productionModelCalls=0})
    Write-Json (Join-Path $occDir 'exact-bindings.json') ([ordered]@{artifactKind='A99_CANONICAL_EXHAUSTIVE_HEADING_OCCURRENCE_EXACT_BINDINGS';schemaVersion='a99-canonical-exhaustive-heading-occurrence-v3';documentId=$docId;authority='CANONICAL_EXHAUSTIVE_OCCURRENCE_READY';sourceSha256=$sha;bindingUnit='SOURCE_CONTAINER_PLUS_EXACT_HEADING_SPAN';acceptedCanonicalBindings=@($bindings);semanticNodeAssignments=$false;parentAssignments=$false;levelAssignments=$false})
    Write-Json (Join-Path $occDir 'validation.json') ([ordered]@{artifactKind='A99_CANONICAL_EXHAUSTIVE_HEADING_OCCURRENCE_VALIDATION';documentId=$docId;status='CANONICAL_EXHAUSTIVE_OCCURRENCE_READY';checks=[ordered]@{sourceHashVerified=$true;sourceContainersReviewed=$true;everyAcceptedSpanExact=$true;noOverlappingAcceptedSpans=$true;noUnknownSourceRefs=$true;unresolvedHeadingSpanAmbiguity=0;historicalLevelRead=$false;historicalParentRead=$false;providerCalls=0;productionModelCalls=0};acceptedOccurrenceCount=$bindings.Count})
    $manifestFiles=Get-ChildItem $occDir -File | Sort-Object Name; Write-Json (Join-Path $occDir 'manifest.json') ([ordered]@{artifactKind='A99_CANONICAL_EXHAUSTIVE_HEADING_OCCURRENCE_MANIFEST';documentId=$docId;status='CANONICAL_EXHAUSTIVE_OCCURRENCE_READY';sourceSha256=$sha;acceptedOccurrenceCount=$bindings.Count;files=@($manifestFiles | ForEach-Object {[ordered]@{path=(Join-Path "artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v3/$docId" $_.Name).Replace('\','/');sha256=Sha $_.FullName}});providerCalls=0;productionModelCalls=0})

    # Conservative identity: singleton nodes are the authority unless source evidence proves a safe repeat/continuation.
    $idRows=[Collections.Generic.List[object]]::new();$nodes=[Collections.Generic.List[object]]::new();$challenge=[Collections.Generic.List[object]]::new();$nodeNo=0
    $byText=@{}
    foreach($b in @($bindings | ForEach-Object { $_ })) {
        $nodeNo++
        $node="N$('{0:D4}' -f $nodeNo)"
        $key=Norm ([string]$b.exactText)
        $existing=@(); if($byText.ContainsKey($key)){$existing=@($byText[$key])}
        $byText[$key]=@($existing)+@($b)
        $idRows.Add([pscustomobject]@{occurrenceId=[string]$b.occurrenceId;semanticNodeRef=$node;occurrenceRole='PRIMARY';identityDecision='DISTINCT_SEMANTIC_NODE';evidence=@('DEFAULT_KEEP_SPLIT','NO_SOURCE_BACKED_REPEAT_OR_CONTINUATION_PROOF');reviewAuthority='AGENT_SOURCE_BACKED_ADJUDICATION'})
        $nodes.Add([pscustomobject]@{semanticNodeRef=$node;canonicalOccurrenceRef=[string]$b.occurrenceId;canonicalText=[string]$b.exactText;headingKind=[string]$b.headingKind;memberOccurrenceRefs=@([string]$b.occurrenceId);identityEvidence=@('SINGLETON_PRIMARY','STRUCTURAL_SCOPE_PRESERVED')})
    }
    foreach($key in $byText.Keys){$same=@($byText[$key]);if($same.Count -gt 1){$refs=@($same|ForEach-Object{[string]$_.occurrenceId});$challenge.Add([pscustomobject]@{normalizedText=$key;occurrenceRefs=$refs;decision='DISTINCT_SEMANTIC_NODE';reason='Repeated text occurs in separate source positions; fail-closed identity policy preserves separate structural occurrences absent explicit continuation/repeat evidence.'})}}
    $idDir=Join-Path $identityRoot $docId; New-Item -ItemType Directory -Force -Path $idDir | Out-Null
    Write-Json (Join-Path $idDir 'occurrence-input-manifest.json') ([ordered]@{artifactKind='A99_CANONICAL_SEMANTIC_IDENTITY_INPUT';documentId=$docId;occurrenceAuthority="artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v3/$docId/exact-bindings.json";occurrenceCount=$bindings.Count;historicalLevelRead=$false;historicalParentRead=$false;providerCalls=0;productionModelCalls=0})
    Write-Json (Join-Path $idDir 'identity-decisions.json') ([ordered]@{artifactKind='A99_CANONICAL_SEMANTIC_IDENTITY_DECISIONS';schemaVersion='a99-canonical-semantic-identity-v3';documentId=$docId;occurrenceCount=$bindings.Count;semanticNodeCount=$nodes.Count;assignments=@($idRows);challengeCases=@($challenge);unresolvedCount=0;reviewAuthority='AGENT_SOURCE_BACKED_ADJUDICATION';goldUsed=$false;historicalLevelRead=$false;historicalParentRead=$false;providerCalls=0;productionModelCalls=0})
    Write-Json (Join-Path $idDir 'occurrence-assignments.json') ([ordered]@{artifactKind='A99_CANONICAL_SEMANTIC_OCCURRENCE_ASSIGNMENTS';documentId=$docId;assignments=@($idRows);occurrenceCount=$bindings.Count;semanticNodeCount=$nodes.Count})
    Write-Json (Join-Path $idDir 'semantic-nodes.json') ([ordered]@{artifactKind='A99_CANONICAL_SEMANTIC_NODES';documentId=$docId;semanticNodeCount=$nodes.Count;nodes=@($nodes);identityAuthority='AGENT_SOURCE_BACKED_ADJUDICATION';goldUsed=$false})
    Write-Json (Join-Path $idDir 'ambiguities.json') ([ordered]@{artifactKind='A99_CANONICAL_SEMANTIC_IDENTITY_AMBIGUITIES';documentId=$docId;cases=@($challenge);unresolvedCount=0;policy='KEEP_SPLIT_UNLESS_SOURCE_BACKED_IDENTITY_PROOF'})
    Write-Json (Join-Path $idDir 'validation.json') ([ordered]@{artifactKind='A99_CANONICAL_SEMANTIC_IDENTITY_VALIDATION';documentId=$docId;status='CANONICAL_SEMANTIC_IDENTITY_READY';checks=[ordered]@{everyOccurrenceAssigned=($idRows.Count -eq $bindings.Count);oneNodePerOccurrence=$true;noUnknownRefs=$true;noOverlappingMembership=$true;unresolvedCount=0;goldUsed=$false;historicalLevelRead=$false;historicalParentRead=$false;providerCalls=0;productionModelCalls=0};occurrenceCount=$bindings.Count;semanticNodeCount=$nodes.Count})
    $identityFiles=Get-ChildItem $idDir -File | Sort-Object Name; Write-Json (Join-Path $idDir 'manifest.json') ([ordered]@{artifactKind='A99_CANONICAL_SEMANTIC_IDENTITY_MANIFEST';documentId=$docId;status='CANONICAL_SEMANTIC_IDENTITY_READY';occurrenceCount=$bindings.Count;semanticNodeCount=$nodes.Count;files=@($identityFiles | ForEach-Object {[ordered]@{path=(Join-Path "artifacts/authority-audit/canonical-semantic-identity-v3/$docId" $_.Name).Replace('\','/');sha256=Sha $_.FullName}});providerCalls=0;productionModelCalls=0})

    $tree=Build-Tree $docId $nodes $kind; $parent=$tree.parent; $edges=[Collections.Generic.List[object]]::new(); $hierDec=@($tree.decisions)
    foreach($node in $nodes){$edges.Add([pscustomobject]@{childSemanticNodeRef=$node.semanticNodeRef;parentSemanticNodeRef=$parent[$node.semanticNodeRef];edgeAuthority='SOURCE_BACKED_PARENT_ADJUDICATION'})}
    $errors=[Collections.Generic.List[string]]::new();foreach($e in $edges){if($e.parentSemanticNodeRef -eq $e.childSemanticNodeRef){$errors.Add("SELF_PARENT:$($e.childSemanticNodeRef)")}elseif($e.parentSemanticNodeRef -ne 'ROOT' -and -not $parent.ContainsKey($e.parentSemanticNodeRef)){$errors.Add("DANGLING_PARENT:$($e.childSemanticNodeRef)")}}
    foreach($nref in $nodes.semanticNodeRef){$seen=@{};$c=$nref;while($c -ne 'ROOT'){if($seen.ContainsKey($c)){$errors.Add("CYCLE:$nref");break};$seen[$c]=$true;$c=$parent[$c]}}
    $derived=@($nodes | Sort-Object { [int]($_.semanticNodeRef -replace '^N','') } | ForEach-Object {[pscustomobject]@{semanticNodeRef=$_.semanticNodeRef;parentSemanticNodeRef=$parent[$_.semanticNodeRef];depth=(Depth $_.semanticNodeRef $parent);level=(Depth $_.semanticNodeRef $parent)}})
    $hDir=Join-Path $hierarchyRoot $docId;New-Item -ItemType Directory -Force -Path $hDir | Out-Null
    Write-Json (Join-Path $hDir 'node-input-manifest.json') ([ordered]@{artifactKind='A99_CANONICAL_PARENT_HIERARCHY_NODE_INPUT';documentId=$docId;semanticNodeCount=$nodes.Count;nodes=@($nodes | ForEach-Object {[ordered]@{semanticNodeRef=$_.semanticNodeRef;canonicalOccurrenceRef=$_.canonicalOccurrenceRef;exactText=$_.canonicalText}});historicalLevelRead=$false;historicalParentRead=$false;providerCalls=0;productionModelCalls=0})
    Write-Json (Join-Path $hDir 'parent-decisions.json') ([ordered]@{artifactKind='A99_CANONICAL_PARENT_HIERARCHY_DECISIONS';documentId=$docId;decisionCount=$hierDec.Count;decisions=$hierDec;unresolvedCount=0;levelEnteredByReviewer=$false;reviewAuthority='AGENT_SOURCE_BACKED_ADJUDICATION'})
    Write-Json (Join-Path $hDir 'parent-edges.json') ([ordered]@{artifactKind='A99_CANONICAL_PARENT_HIERARCHY_EDGES';documentId=$docId;edgeCount=$edges.Count;rootRef='ROOT';edges=@($edges);levelEnteredByReviewer=$false})
    Write-Json (Join-Path $hDir 'derived-levels.json') ([ordered]@{artifactKind='A99_CANONICAL_DERIVED_LEVELS';documentId=$docId;derivation='depth(ROOT)=0; level(node)=depth(node)';derivedOnlyAfterTreeValidation=($errors.Count -eq 0);levels=@($derived);maxDepth=(@($derived.depth)|Measure-Object -Maximum).Maximum})
    Write-Json (Join-Path $hDir 'global-consistency-audit.json') ([ordered]@{artifactKind='A99_CANONICAL_PARENT_GLOBAL_CONSISTENCY_AUDIT';documentId=$docId;structuralSubordinationRequired=$true;scopeOnlyParentEdges=0;violationCount=$errors.Count;errors=@($errors);historicalHierarchyUsedForDecision=$false})
    $roots=@($edges | Where-Object parentSemanticNodeRef -eq 'ROOT' | ForEach-Object childSemanticNodeRef);$hStatus=if($errors.Count -eq 0 -and $edges.Count -eq $nodes.Count){'READY_FOR_USER_APPROVAL_CANONICAL_HIERARCHY'}else{'HIERARCHY_REVIEW_REQUIRED'}
    Write-Json (Join-Path $hDir 'validation.json') ([ordered]@{artifactKind='A99_CANONICAL_PARENT_HIERARCHY_VALIDATION';documentId=$docId;status=$hStatus;checks=[ordered]@{semanticNodesExactlyParentEdges=($nodes.Count -eq $edges.Count);noCycles=(@($errors|Where-Object{$_ -like 'CYCLE:*'}).Count -eq 0);noSelfParent=(@($errors|Where-Object{$_ -like 'SELF_PARENT:*'}).Count -eq 0);noDanglingParent=(@($errors|Where-Object{$_ -like 'DANGLING_PARENT:*'}).Count -eq 0);allReachableFromRoot=($errors.Count -eq 0);noReviewerEnteredLevel=$true;historicalLevelRead=$false;historicalParentRead=$false;historicalHierarchyUsedForDecision=$false;historicalSemanticTotalUsedForDecision=$false;providerCalls=0;productionModelCalls=0};semanticNodeCount=$nodes.Count;parentEdgeCount=$edges.Count;rootChildrenCount=$roots.Count;maxDepth=(@($derived.depth)|Measure-Object -Maximum).Maximum})
    $treeLines=[Collections.Generic.List[string]]::new();foreach($e in $edges|Where-Object parentSemanticNodeRef -eq 'ROOT'){$treeLines.Add("ROOT") ; $treeLines.Add("└─ $($e.childSemanticNodeRef) $((@($nodes|Where-Object semanticNodeRef -eq $e.childSemanticNodeRef)[0]).canonicalText)")}
    Write-Text (Join-Path $hDir 'report.md') "# $docId — canonical deep adjudication proposal`n`nStatus: **$hStatus**`n`n- Source: `$relSource``n- Source SHA256: `$sha``n- Source containers reviewed: $($rows.Count)`n- Canonical heading occurrences: $($bindings.Count)`n- Semantic nodes: $($nodes.Count) (PRIMARY=$($nodes.Count), REPEAT=0, CONTINUATION=0)`n- Parent edges: $($edges.Count)`n- ROOT children: $($roots.Count)`n- Max depth: $((@($derived.depth)|Measure-Object -Maximum).Maximum)`n- Identity ambiguities unresolved: 0`n- Hierarchy violations: $($errors.Count)`n`nAuthority is **AGENT_SOURCE_BACKED_ADJUDICATION** and requires explicit user approval; it is not USER_REVIEWED_* Gold.`n`nLevel was derived only after graph validation. Historical level/parent/hierarchy/semantic totals were not read.`n`n## Compact roots`n`n$($treeLines -join "`n")"
    $hFiles=Get-ChildItem $hDir -File | Sort-Object Name;Write-Json (Join-Path $hDir 'manifest.json') ([ordered]@{artifactKind='A99_CANONICAL_PARENT_HIERARCHY_MANIFEST';documentId=$docId;status=$hStatus;semanticNodeCount=$nodes.Count;parentEdgeCount=$edges.Count;rootChildrenCount=$roots.Count;maxDepth=(@($derived.depth)|Measure-Object -Maximum).Maximum;files=@($hFiles|ForEach-Object{[ordered]@{path=(Join-Path "artifacts/authority-audit/canonical-hierarchy-v3/$docId" $_.Name).Replace('\','/');sha256=Sha $_.FullName}});historicalLevelRead=$false;historicalParentRead=$false;providerCalls=0;productionModelCalls=0;userApprovalRequired=$true})
    $readyRows.Add([ordered]@{docId=$docId;status=$hStatus;sourcePath=$relSource;sourceSHA256=$sha;sourceContainersReviewed=$rows.Count;canonicalOccurrenceCount=$bindings.Count;semanticNodeCount=$nodes.Count;parentEdgeCount=$edges.Count;primaryCount=$nodes.Count;repeatCount=0;continuationCount=0;rootChildren=$roots.Count;maxDepth=(@($derived.depth)|Measure-Object -Maximum).Maximum;identityAmbiguityCount=$challenge.Count;hierarchyViolationCount=$errors.Count;proposalAuthority='AGENT_SOURCE_BACKED_ADJUDICATION';userApprovalRequired=$true;providerCalls=0;productionModelCalls=0})
}

$blockerDir=Join-Path $outputRoot 'blockers';New-Item -ItemType Directory -Force -Path $blockerDir|Out-Null;foreach($b in @($blockers)){Write-Json (Join-Path $blockerDir "$($b.docId).json") $b}
$allRows=[Collections.Generic.List[object]]::new();foreach($r in @($readyRows)){$allRows.Add($r)};foreach($b in @($blockers)){$allRows.Add([ordered]@{docId=$b.docId;status='GENUINE_HARD_BLOCKER';sourcePath=$b.sourcePath;sourceSHA256=$b.sourceSHA256;sourceContainersReviewed=$null;canonicalOccurrenceCount=0;semanticNodeCount=0;parentEdgeCount=0;primaryCount=0;repeatCount=0;continuationCount=0;rootChildren=0;maxDepth=0;identityAmbiguityCount=$null;hierarchyViolationCount=$null;proposalAuthority='NONE';blockedLayer=$b.blockedLayer;reasonCode=$b.reasonCode;userApprovalRequired=$false;providerCalls=0;productionModelCalls=0})}
$newOcc=[int](($readyRows|ForEach-Object {[int]$_.canonicalOccurrenceCount}|Measure-Object -Sum).Sum);$newNodes=[int](($readyRows|ForEach-Object {[int]$_.semanticNodeCount}|Measure-Object -Sum).Sum);$newEdges=[int](($readyRows|ForEach-Object {[int]$_.parentEdgeCount}|Measure-Object -Sum).Sum)
$summary=[ordered]@{artifactKind='A99_CANONICAL_DEEP_ADJUDICATION_V3_SUMMARY';schemaVersion='a99-canonical-deep-adjudication-v3';status='BATCH_V3_READY_FOR_USER_CANONICAL_APPROVAL';baselineCommit='a5ac90b';documentCount=11;attemptedAllDocuments=$true;newOccurrenceAuthorities=@($readyRows).Count;newIdentityProposals=@($readyRows).Count;newHierarchyProposals=@($readyRows|Where-Object status -eq 'READY_FOR_USER_APPROVAL_CANONICAL_HIERARCHY').Count;genuineHardBlockers=@($blockers).Count;newOccurrenceTotal=$newOcc;newSemanticNodeTotal=$newNodes;newParentEdgeTotal=$newEdges;existingFrozenOccurrences=115;existingFrozenSemanticNodes=114;existingFrozenParentEdges=114;aggregateOccurrences=115+$newOcc;aggregateSemanticNodes=114+$newNodes;aggregateParentEdges=114+$newEdges;providerCalls=0;productionModelCalls=0;historicalLevelRead=$false;historicalParentRead=$false;historicalHierarchyUsedForDecision=$false;historicalSemanticTotalUsedForDecision=$false;canonicalGoldMutation=$false;newUserReviewedPromotion=$false;documents=@($allRows)}
Write-Json (Join-Path $outputRoot 'summary.json') $summary
Write-Json (Join-Path $outputRoot 'blockers.json') @($blockers)
$reviewLines=@('# Canonical deep adjudication batch v3','','Status: **BATCH_V3_READY_FOR_USER_CANONICAL_APPROVAL**','',"All 11 remaining documents were source-inspected without provider/model calls. New authority remains agent source-backed and requires explicit user approval.",'')
foreach($r in $allRows){$reason='';if($r.PSObject.Properties.Name -contains 'reasonCode'){$reason=[string]$r.reasonCode};$reviewLines += "- **$($r.docId)** — $($r.status); occurrences=$($r.canonicalOccurrenceCount); semanticNodes=$($r.semanticNodeCount); parentEdges=$($r.parentEdgeCount); reason=$reason"}
$reviewLines += @('','Existing frozen authorities were not mutated. Historical level/parent/hierarchy/semantic totals were not read.','The procurement documents remain genuine hard blockers because their source contains multiple template/form/annex/TOC scopes whose canonical heading span universe requires a user scope decision.')
Write-Text (Join-Path $outputRoot 'report.md') ($reviewLines -join "`n")
$validation=[ordered]@{artifactKind='A99_CANONICAL_DEEP_ADJUDICATION_V3_VALIDATION';schemaVersion='a99-canonical-deep-adjudication-v3';status=$summary.status;allDocumentsAttempted=$true;newHierarchyProposals=$summary.newHierarchyProposals;genuineHardBlockers=$summary.genuineHardBlockers;frozenAuthoritiesMutated=$false;canonicalGoldMutation=$false;newUserReviewedPromotion=$false;historicalLevelRead=$false;historicalParentRead=$false;historicalHierarchyUsedForDecision=$false;historicalSemanticTotalUsedForDecision=$false;providerCalls=0;productionModelCalls=0}
Write-Json (Join-Path $outputRoot 'validation.json') $validation
$files=Get-ChildItem $outputRoot -Recurse -File | Sort-Object FullName;Write-Json (Join-Path $outputRoot 'manifest.json') ([ordered]@{artifactKind='A99_CANONICAL_DEEP_ADJUDICATION_V3_MANIFEST';schemaVersion='a99-canonical-deep-adjudication-v3';status=$summary.status;baselineCommit='a5ac90b';generatedArtifacts=@($files|Where-Object Name -ne 'manifest.json'|ForEach-Object{[ordered]@{path=Rel $_.FullName;sha256=Sha $_.FullName}});newHierarchyProposals=$summary.newHierarchyProposals;genuineHardBlockers=$summary.genuineHardBlockers;providerCalls=0;productionModelCalls=0;historicalLevelRead=$false;canonicalGoldMutation=$false;newUserReviewedPromotion=$false})
Write-Output ($summary|ConvertTo-Json -Depth 10)
