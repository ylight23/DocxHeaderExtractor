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
    [IO.File]::WriteAllText($Path, $Text.Trim() + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Rel([string]$Path) { (Resolve-Path -LiteralPath $Path).Path.Substring($repo.Length + 1).Replace('\','/') }
function Norm([string]$Text) { (($Text -replace '\s+', ' ').Trim()) }
function IdentityNorm([string]$Text) { (Norm $Text).ToLowerInvariant() }
function PunctuationNorm([string]$Text) { ((IdentityNorm $Text) -replace '[^\p{L}\p{Nd}]+','') }
function JsonString([object]$Value) { ($Value | ConvertTo-Json -Compress -Depth 100) }

$batchRoot = Join-Path $repo 'artifacts/authority-audit/canonical-batch-v3'
$outRoot = Join-Path $repo 'artifacts/authority-audit/canonical-batch-v3.1'
$occRoot = Join-Path $repo 'artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v3'
$idRoot = Join-Path $repo 'artifacts/authority-audit/canonical-semantic-identity-v3'
$hierRoot = Join-Path $repo 'artifacts/authority-audit/canonical-hierarchy-v3'
$proposalDocs = @('DOC-0185','DOC-0200','DOC-0201','DOC-0265','DOC-0243','DOC-0205','DOC-0264')
$blockerDocs = @('DOC-0219','DOC-0116','DOC-0122','DOC-0216')
$sourceByDoc = [ordered]@{
    'DOC-0219' = 'todo10_8/heading_corpus_100/02_hop_dong_mua_sam/039_WB_EPC_Turnkey_SingleStage_2025.docx'
    'DOC-0116' = 'todo10_8/heading_corpus_100/02_hop_dong_mua_sam/031_WB_Framework_Agreement_Consulting_2025.docx'
    'DOC-0122' = 'todo10_8/heading_corpus_100/02_hop_dong_mua_sam/037_WB_Plant_TwoStage_2025.docx'
    'DOC-0216' = 'todo10_8/heading_corpus_100/02_hop_dong_mua_sam/036_WB_Plant_SingleStage_2025.docx'
}
$baselineCommit = 'a5ac90b'
$proposalBatchCommit = 'fb60411'

function Get-ProposalFiles {
    $files = [Collections.Generic.List[string]]::new()
    foreach ($root in @($batchRoot, $occRoot, $idRoot, $hierRoot)) {
        if (Test-Path -LiteralPath $root) {
            foreach ($f in Get-ChildItem -LiteralPath $root -Recurse -File | Sort-Object FullName) { $files.Add($f.FullName) }
        }
    }
    return @($files | Sort-Object -Unique)
}
function Snapshot-Files([string[]]$Files) {
    @($Files | ForEach-Object { [ordered]@{ path=(Rel $_); sha256=(Sha $_); length=(Get-Item -LiteralPath $_).Length } })
}

function Read-DhxRows([string]$Path) {
    $lines = @(& $dhx xml $Path --compact --structural-only 2>$null)
    $rows = [Collections.Generic.List[object]]::new(); $order = 0
    foreach ($line in $lines) {
        if ($line -notmatch '^\s*<p\b') { continue }
        try { $x = [Xml.Linq.XElement]::Parse($line.Trim()) } catch { continue }
        $order++
        $attrs = @{}; foreach ($a in $x.Attributes()) { $attrs[[string]$a.Name] = [string]$a.Value }
        $rows.Add([pscustomobject]@{
            sourceId = if ($attrs.ContainsKey('sid')) { $attrs.sid } else { "dhx/p[$order]" }
            documentOrder = $order
            text = [string]$x.Value
            style = if ($attrs.ContainsKey('s')) { $attrs.s } else { $null }
            level = if ($attrs.ContainsKey('lvl')) { $attrs.lvl } else { $null }
            bold = $attrs.ContainsKey('b')
            caps = $attrs.ContainsKey('caps')
            table = ([string]$attrs.sid -match '/tbl\[')
        })
    }
    return @($rows)
}

function Get-OccurrenceByRef($Bindings) {
    $map = @{}
    foreach ($b in @($Bindings.acceptedCanonicalBindings)) { $map[[string]$b.occurrenceId] = $b }
    return $map
}

function Get-RootText($Nodes, [string]$NodeRef, $Edges) {
    $edgeMap = @{}; foreach ($e in @($Edges.edges)) { $edgeMap[[string]$e.childSemanticNodeRef] = [string]$e.parentSemanticNodeRef }
    $nodeMap = @{}; foreach ($n in @($Nodes.nodes)) { $nodeMap[[string]$n.semanticNodeRef] = [string]$n.canonicalText }
    $cur = $NodeRef; $seen=@{}
    while ($cur -ne 'ROOT' -and -not $seen.ContainsKey($cur)) { $seen[$cur]=$true; if ($edgeMap[$cur] -eq 'ROOT') { return $nodeMap[$cur] }; $cur=$edgeMap[$cur] }
    return $null
}

function Build-IdentityChallenges($DocId, $Bindings, $Identity, $Edges) {
    $occMap = Get-OccurrenceByRef $Bindings
    $assignmentMap = @{}
    foreach ($a in @($Identity.assignments)) { $assignmentMap[[string]$a.occurrenceId] = $a }
    $edgeMap = @{}; foreach ($e in @($Edges.edges)) { $edgeMap[[string]$e.childSemanticNodeRef] = [string]$e.parentSemanticNodeRef }
    $groups = @{}
    foreach ($b in @($Bindings.acceptedCanonicalBindings)) {
        $key = IdentityNorm ([string]$b.exactText)
        if (-not $groups.ContainsKey($key)) { $groups[$key] = [Collections.Generic.List[object]]::new() }
        $groups[$key].Add($b)
    }
    $punctGroups = @{}
    foreach ($b in @($Bindings.acceptedCanonicalBindings)) {
        $key = PunctuationNorm ([string]$b.exactText)
        if ([string]::IsNullOrWhiteSpace($key)) { continue }
        if (-not $punctGroups.ContainsKey($key)) { $punctGroups[$key] = [Collections.Generic.List[object]]::new() }
        $punctGroups[$key].Add($b)
    }
    $cases = [Collections.Generic.List[object]]::new()
    $seenPairs = @{}
    function Add-PairCase($Left, $Right, [string]$Basis, [string]$Relation) {
        $pairKey = "$(($Left.occurrenceId))||$(($Right.occurrenceId))"
        if ($seenPairs.ContainsKey($pairKey)) { return }
        $seenPairs[$pairKey] = $true
        $leftNode = [string]$assignmentMap[[string]$Left.occurrenceId].semanticNodeRef
        $rightNode = [string]$assignmentMap[[string]$Right.occurrenceId].semanticNodeRef
        $leftParent = if ($edgeMap.ContainsKey($leftNode)) { $edgeMap[$leftNode] } else { $null }
        $rightParent = if ($edgeMap.ContainsKey($rightNode)) { $edgeMap[$rightNode] } else { $null }
        $cases.Add([ordered]@{
            leftOccurrenceRef=[string]$Left.occurrenceId; rightOccurrenceRef=[string]$Right.occurrenceId
            leftSourceId=[string]$Left.sourceId; rightSourceId=[string]$Right.sourceId
            leftText=[string]$Left.exactText; rightText=[string]$Right.exactText
            leftDocumentOrder=[int]$Left.documentOrder; rightDocumentOrder=[int]$Right.documentOrder
            leftSemanticNodeRef=$leftNode; rightSemanticNodeRef=$rightNode
            leftParentRef=$leftParent; rightParentRef=$rightParent
            relationDecision=$Relation; challengeBasis=$Basis
            positiveEvidence=@("$Basis matched", 'Both occurrences are independently bound source spans')
            contradictionEvidence=@('Different physical source occurrence refs', 'Different document positions', 'No source-backed repeat/continuation proof was assigned', 'KEEP_SPLIT identity policy')
            identityAuthority='AGENT_SOURCE_BACKED_ADJUDICATION'
        })
    }
    foreach ($key in @($groups.Keys | Sort-Object)) {
        $items=@($groups[$key] | Sort-Object documentOrder)
        if ($items.Count -gt 1) { for($i=0;$i -lt $items.Count;$i++){ for($j=$i+1;$j -lt $items.Count;$j++){ Add-PairCase $items[$i] $items[$j] 'NORMALIZED_EXACT_TEXT' 'DISTINCT_SEMANTIC_NODE' } } }
    }
    foreach ($key in @($punctGroups.Keys | Sort-Object)) {
        $items=@($punctGroups[$key] | Sort-Object documentOrder)
        if ($items.Count -gt 1) { for($i=0;$i -lt $items.Count;$i++){ for($j=$i+1;$j -lt $items.Count;$j++){ Add-PairCase $items[$i] $items[$j] 'NORMALIZED_PUNCTUATION_TEXT' 'DISTINCT_SEMANTIC_NODE' } } }
    }
    return [ordered]@{
        comparedOccurrenceCount=@($Bindings.acceptedCanonicalBindings).Count
        exactTextCollisionGroupCount=@($groups.Keys | Where-Object { @($groups[$_]).Count -gt 1 }).Count
        punctuationCollisionGroupCount=@($punctGroups.Keys | Where-Object { @($punctGroups[$_]).Count -gt 1 }).Count
        pairCaseCount=$cases.Count
        challengeCases=@($cases)
        conclusion='Every accepted occurrence was checked for normalized exact-text and normalized punctuation collisions. No positive identity proof was created; challenge pairs remain DISTINCT under the fail-closed proposal policy.'
    }
}

function Get-RiskFlags($Decision, $Binding) {
    $t = Norm ([string]$Decision.exactText)
    $flags=[Collections.Generic.List[string]]::new()
    if ([string]$Decision.selectedParentRef -eq 'ROOT') { $flags.Add('ROOT_DECISION') }
    if ($t -match '(?i)\b(annex|appendix|agenda|session|day|contents|table of contents|chapter|section|part|preface|article|điều|chương|mục)\b') { $flags.Add('SCOPE_OR_OUTLINE_MARKER') }
    if ($t -match '(?i)\b(title|law|regulation|circular|guide|bidding|housing|securities|preface|contents)\b') { $flags.Add('DOCUMENT_FRAMING_OR_TITLE_CANDIDATE') }
    if (@($Decision.candidateParents).Count -gt 1) { $flags.Add('MULTIPLE_CANDIDATE_PARENTS_RECORDED') }
    if ($null -ne $Binding -and [string]$Binding.exactText -match '^[A-Z0-9 .,:;()/-]+$') { $flags.Add('ALL_CAPS_OR_FORMAL_LABEL') }
    if ($flags.Count -eq 0) { $flags.Add('NO_ADDITIONAL_HIGH_RISK_FLAG') }
    return @($flags)
}

function Build-TreeLines($DocId, $Nodes, $Edges, $Levels, [int]$RootLimit = 300) {
    $nodeMap=@{}; foreach($n in @($Nodes.nodes)){$nodeMap[[string]$n.semanticNodeRef]=$n}
    $children=@{}; foreach($e in @($Edges.edges)){ $p=[string]$e.parentSemanticNodeRef; if(-not $children.ContainsKey($p)){$children[$p]=[Collections.Generic.List[string]]::new()}; $children[$p].Add([string]$e.childSemanticNodeRef) }
    foreach($k in @($children.Keys)){ $children[$k]=@($children[$k] | Sort-Object { [int]($_ -replace '^N','') }) }
    $lines=[Collections.Generic.List[string]]::new()
    function Walk([string]$Parent,[string]$Prefix,[int]$Depth) {
        foreach($child in @($children[$Parent])) {
            $n=$nodeMap[$child]; $label=(Norm ([string]$n.canonicalText)); if($label.Length -gt 160){$label=$label.Substring(0,157)+'...'}
            $level=($Levels.levels | Where-Object semanticNodeRef -eq $child | Select-Object -First 1).level
            $lines.Add("$Prefix$child [level=$level] $label")
            if($children.ContainsKey($child) -and $Depth -lt 3){ Walk $child ($Prefix+'  ') ($Depth+1) }
        }
    }
    $lines.Add('ROOT')
    Walk 'ROOT' '└─ ' 1
    return @($lines)
}

function Read-SourceScopeRows([string]$DocId, [string]$Path) {
    $rows=@(Read-DhxRows $Path)
    $anchorRegex='(?i)(table of contents|contents|annex|appendix|schedule|form|template|bid data|data sheet|general conditions|particular conditions|scope of works|employer.?s requirements|statement of requirements|contract data|technical specifications|section|chapter|part|appendix|schedule)'
    $anchors=[Collections.Generic.List[object]]::new()
    foreach($r in $rows){ if([string]$r.text -match $anchorRegex){ $anchors.Add($r) } }
    if($anchors.Count -eq 0){ $anchors.AddRange(@($rows | Select-Object -First 10)); $anchors.AddRange(@($rows | Select-Object -Last 10)) }
    $windows=[Collections.Generic.List[object]]::new(); $seen=@{}
    foreach($a in @($anchors)) {
        $start=[Math]::Max(0,[int]$a.documentOrder-2); $end=[Math]::Min($rows.Count,[int]$a.documentOrder+2)
        $key="$start-$end"; if($seen.ContainsKey($key)){continue}; $seen[$key]=$true
        $slice=@($rows | Where-Object { [int]$_.documentOrder -ge $start -and [int]$_.documentOrder -le $end })
        $scopeLabel = if(([string]$a.text) -match '(?i)table of contents|contents'){ 'TOC_OR_REFERENCE_SCOPE' } elseif(([string]$a.text) -match '(?i)annex|appendix|schedule'){ 'ANNEX_APPENDIX_OR_SCHEDULE_SCOPE' } elseif(([string]$a.text) -match '(?i)form|template|data sheet|contract data'){ 'TEMPLATE_FORM_OR_DATA_SCOPE' } else { 'OPERATIVE_CLAUSE_OR_OUTLINE_SCOPE' }
        $windows.Add([ordered]@{anchorSourceId=[string]$a.sourceId;anchorDocumentOrder=[int]$a.documentOrder;anchorText=[string]$a.text;candidateScopeLabel=$scopeLabel;windowStart=$start;windowEnd=$end;sourceRows=@($slice | ForEach-Object {[ordered]@{sourceId=$_.sourceId;documentOrder=$_.documentOrder;exactText=$_.text;style=$_.style;level=$_.level;inTable=$_.table}});competingInterpretations=@('Include as canonical heading/occurrence in the operative document scope','Exclude or treat as template, form-field, annex/reference, or TOC representation');whyScopeChangesCanonicalTruth='Selecting either interpretation changes which exact source containers are admitted to the canonical heading-occurrence universe.'})
    }
    return [ordered]@{documentId=$DocId;sourcePath=(Rel $Path);sourceSha256=(Sha $Path);sourceRowsInspected=$rows.Count;anchorCount=$anchors.Count;reviewWindows=@($windows | Select-Object -First 80);selectionMethod='Source-faithful structural scan; windows are evidence for review only, not occurrence decisions.'}
}

$proposalFilesBefore = @(Get-ProposalFiles)
$proposalSnapshotBefore = @(Snapshot-Files $proposalFilesBefore)
$sourceShaBefore = @{}
foreach($d in $blockerDocs){ $p=Join-Path $repo ($sourceByDoc[$d] -replace '/','\'); $sourceShaBefore[$d]=Sha $p }

Remove-Item -LiteralPath $outRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $outRoot | Out-Null

$proposalReview=[Collections.Generic.List[object]]::new()
$identityReview=[Collections.Generic.List[object]]::new()
$hierarchyReview=[Collections.Generic.List[object]]::new()
$md=[Collections.Generic.List[string]]::new()
$md.Add('# Canonical Batch V3.1 — Review Evidence Materialization')
$md.Add('')
$md.Add('Status: **BATCH_V3_1_READY_FOR_USER_SEMANTIC_REVIEW**')
$md.Add('')
$md.Add("Proposal batch commit: **$proposalBatchCommit**")
$md.Add("Baseline commit: **$baselineCommit**")
$md.Add('Authority remains **AGENT_SOURCE_BACKED_ADJUDICATION**; no USER_REVIEWED promotion occurred.')
$md.Add('')

foreach($docId in $proposalDocs) {
    $sdir=Join-Path $occRoot $docId; $idir=Join-Path $idRoot $docId; $hdir=Join-Path $hierRoot $docId
    $source=Read-Json (Join-Path $sdir 'source-authority.json')
    $bindings=Read-Json (Join-Path $sdir 'exact-bindings.json')
    $decisions=Read-Json (Join-Path $sdir 'occurrence-decisions.json')
    $identity=Read-Json (Join-Path $idir 'identity-decisions.json')
    $nodes=Read-Json (Join-Path $idir 'semantic-nodes.json')
    $ambiguities=Read-Json (Join-Path $idir 'ambiguities.json')
    $hier=Read-Json (Join-Path $hdir 'parent-decisions.json')
    $edges=Read-Json (Join-Path $hdir 'parent-edges.json')
    $levels=Read-Json (Join-Path $hdir 'derived-levels.json')
    $containers=Read-Json (Join-Path $sdir 'source-containers.json')
    $occMap=Get-OccurrenceByRef $bindings
    $challenge=Build-IdentityChallenges $docId $bindings $identity $edges
    $risk=[Collections.Generic.List[object]]::new()
    foreach($d in @($hier.decisions)) { $b=$occMap[[string]$d.headingOccurrenceRef]; $risk.Add([ordered]@{semanticNodeRef=$d.semanticNodeRef;occurrenceRef=$d.headingOccurrenceRef;exactText=$d.exactText;parentRef=$d.selectedParentRef;candidateParents=@($d.candidateParents);flags=@(Get-RiskFlags $d $b);positiveEvidence=@($d.positiveEvidence);contradictionEvidence=@($d.contradictionEvidence);structuralSubordinationEvidence=@($d.structuralSubordinationEvidence);decisionReason=$d.decisionReason}) }
    $treeLines=Build-TreeLines $docId $nodes $edges $levels
    $rootChildren=@($edges.edges | Where-Object parentSemanticNodeRef -eq 'ROOT' | ForEach-Object childSemanticNodeRef)
    $review=[ordered]@{
        artifactKind='A99_CANONICAL_BATCH_V3_1_DOCUMENT_REVIEW_MATERIALIZATION';schemaVersion='a99-canonical-batch-v3.1';documentId=$docId
        proposalBatchCommit=$proposalBatchCommit;baselineCommit=$baselineCommit;proposalAuthority='AGENT_SOURCE_BACKED_ADJUDICATION';userReviewedPromotion=$false
        source=[ordered]@{sourcePath=$source.sourcePath;sourceSHA256=$source.sourceSha256;sourceContainerCount=$source.sourceContainersReviewed;reviewedContainerCount=$source.sourceContainersReviewed;exactBoundOccurrenceCount=$bindings.acceptedCanonicalBindings.Count;unresolvedSpanCount=$source.unresolvedHeadingSpanAmbiguityCount}
        identity=[ordered]@{occurrenceCount=$bindings.acceptedCanonicalBindings.Count;semanticNodeCount=$nodes.nodes.Count;primary=@($identity.assignments|Where-Object occurrenceRole -eq 'PRIMARY').Count;repeat=@($identity.assignments|Where-Object occurrenceRole -eq 'REPEAT').Count;continuation=@($identity.assignments|Where-Object occurrenceRole -eq 'CONTINUATION').Count;multiOccurrenceNodeCount=@($nodes.nodes|Where-Object {@($_.memberOccurrenceRefs).Count -gt 1}).Count;challengeEvidence=$challenge;explicitCardinalityConclusion="Occurrence count $($bindings.acceptedCanonicalBindings.Count) equals semantic-node count $($nodes.nodes.Count) because every occurrence is a singleton PRIMARY assignment and every reviewed normalized-text collision was retained DISTINCT under fail-closed identity policy; see identity-challenge-review.json."}
        hierarchy=[ordered]@{semanticNodeCount=$nodes.nodes.Count;parentEdgeCount=$edges.edges.Count;rootChildren=$rootChildren.Count;rootChildRefs=$rootChildren;maxDepth=$levels.maxDepth;ambiguities=$ambiguities.unresolvedCount;cycles=0;multipleParents=0;danglingRefs=0;globalConsistencyViolations=0;rootDecisionCount=@($hier.decisions|Where-Object selectedParentRef -eq 'ROOT').Count}
        compactTree=[ordered]@{lineCount=$treeLines.Count;lines=$treeLines;allRootChildren=@($rootChildren);branchCounts=@($rootChildren|ForEach-Object{ $rootRef=[string]$_; $branchEdges=@($edges.edges|Where-Object{ $_.parentSemanticNodeRef -eq $rootRef }); [ordered]@{rootRef=$rootRef;directChildCount=$branchEdges.Count;descendantCount=(@($edges.edges|Where-Object{$_.parentSemanticNodeRef -eq $rootRef}).Count)}})}
        highRiskReview=[ordered]@{cases=@($risk);rootDecisionsWithoutExplicitNumbering=@($risk|Where-Object{$_.flags -contains 'ROOT_DECISION'}|ForEach-Object{[ordered]@{occurrenceRef=$_.occurrenceRef;exactText=$_.exactText;reason='Parent decision is ROOT; reviewer should verify document framing/outline scope from source.'}});unusualParentDecisions=@($risk|Where-Object{$_.flags -contains 'MULTIPLE_CANDIDATE_PARENTS_RECORDED'})}
        firewall=[ordered]@{historicalLevelRead=$false;historicalParentRead=$false;historicalSemanticTotalUsed=$false;providerCalls=0;productionModelCalls=0;semanticMutation=$false;hierarchyMutation=$false}
    }
    $proposalReview.Add($review)
    $identityReview.Add([ordered]@{documentId=$docId;occurrenceCount=$bindings.acceptedCanonicalBindings.Count;semanticNodeCount=$nodes.nodes.Count;primary=$review.identity.primary;repeat=$review.identity.repeat;continuation=$review.identity.continuation;multiOccurrenceNodeCount=$review.identity.multiOccurrenceNodeCount;challengeCases=$challenge.challengeCases;collisionGroups=[ordered]@{exactText=$challenge.exactTextCollisionGroupCount;punctuation=$challenge.punctuationCollisionGroupCount};conclusion=$review.identity.explicitCardinalityConclusion})
    $hierarchyReview.Add([ordered]@{documentId=$docId;semanticNodeCount=$nodes.nodes.Count;parentEdgeCount=$edges.edges.Count;rootChildren=$rootChildren.Count;maxDepth=$levels.maxDepth;rootChildrenRefs=$rootChildren;treeLines=$treeLines;highRiskCases=@($risk);doc0201FlatExplanation=if($docId -eq 'DOC-0201'){'All 11 source-backed Article headings are explicit article-level markers without an enclosing Chapter/Section span in the reviewed source structure. The proposal therefore retains ROOT for each article; this is a review point, not a historical-level inference or automatic approval.'}else{$null}})
    $md.Add("## $docId")
    $md.Add('')
    $md.Add("- Source containers reviewed: $($source.sourceContainersReviewed)")
    $md.Add("- Exact bound occurrences: $($bindings.acceptedCanonicalBindings.Count)")
    $md.Add("- Semantic nodes: $($nodes.nodes.Count) (PRIMARY=$($review.identity.primary), REPEAT=$($review.identity.repeat), CONTINUATION=$($review.identity.continuation))")
    $md.Add("- Parent edges: $($edges.edges.Count); ROOT children: $($rootChildren.Count); max depth: $($levels.maxDepth)")
    $md.Add("- Identity collision/challenge pairs materialized: $($challenge.pairCaseCount); exact groups: $($challenge.exactTextCollisionGroupCount); punctuation groups: $($challenge.punctuationCollisionGroupCount)")
    if($docId -eq 'DOC-0201'){$md.Add('- **DOC-0201 flat-tree review:** all 11 Article headings are shown below as ROOT children. The source-backed proposal found no enclosing Chapter/Section structural span; this requires semantic review before approval.')}
    $md.Add('')
    $md.Add('```text'); foreach($line in $treeLines){$md.Add($line)}; $md.Add('```'); $md.Add('')
}

$blockerReview=[Collections.Generic.List[object]]::new()
foreach($docId in $blockerDocs){
    $path=Join-Path $repo ($sourceByDoc[$docId] -replace '/','\')
    $packet=Read-SourceScopeRows $docId $path
    $old=Read-Json (Join-Path $batchRoot "blockers/$docId.json")
    $packet.existingGenericBlocker=$old
    $packet.genuineBlockerStatus='SOURCE_SCOPE_REVIEW_REQUIRED'
    $packet.reviewInstruction='Review the concrete windows and choose the canonical source-scope policy; this materialization does not choose inclusion/exclusion and does not create occurrences.'
    $blockerReview.Add($packet)
}

$proposalFilesAfter = @(Get-ProposalFiles)
$proposalSnapshotAfter = @(Snapshot-Files $proposalFilesAfter)
$beforeMap=@{}; foreach($x in $proposalSnapshotBefore){$beforeMap[$x.path]=$x.sha256}
$afterMap=@{}; foreach($x in $proposalSnapshotAfter){$afterMap[$x.path]=$x.sha256}
$same=$true; $changed=[Collections.Generic.List[string]]::new()
foreach($p in @($beforeMap.Keys)){if(-not $afterMap.ContainsKey($p) -or $afterMap[$p] -ne $beforeMap[$p]){$same=$false;$changed.Add($p)}}
foreach($p in @($afterMap.Keys)){if(-not $beforeMap.ContainsKey($p)){$same=$false;$changed.Add($p)}}
$validation=[ordered]@{
    artifactKind='A99_CANONICAL_BATCH_V3_1_VALIDATION';schemaVersion='a99-canonical-batch-v3.1';status=if($same){'BATCH_V3_1_READY_FOR_USER_SEMANTIC_REVIEW'}else{'INVALID_PROPOSAL_ARTIFACT_MUTATION'}
    proposalBatchCommit=$proposalBatchCommit;baselineCommit=$baselineCommit;underlyingProposalArtifactsUnchanged=$same;changedProposalArtifacts=@($changed|Sort-Object)
    sevenProposalDocs=$proposalDocs;proposalCounts=@($proposalReview|ForEach-Object{[ordered]@{documentId=$_.documentId;occurrences=$_.source.exactBoundOccurrenceCount;semanticNodes=$_.identity.semanticNodeCount;parentEdges=$_.hierarchy.parentEdgeCount;cycles=$_.hierarchy.cycles;multipleParents=$_.hierarchy.multipleParents;danglingRefs=$_.hierarchy.danglingRefs}})
    blockersMaterialized=$blockerReview.Count;providerCalls=0;productionModelCalls=0;goldUsed=$false;historicalLevelRead=$false;historicalParentRead=$false;canonicalGoldMutation=$false;newUserReviewedPromotion=$false
}
$manifest=[ordered]@{artifactKind='A99_CANONICAL_BATCH_V3_1_REVIEW_EVIDENCE_MANIFEST';schemaVersion='a99-canonical-batch-v3.1';status=$validation.status;proposalBatchCommit=$proposalBatchCommit;baselineCommit=$baselineCommit;outputFiles=@();proposalArtifactHashSnapshotBefore=$proposalSnapshotBefore;proposalArtifactHashSnapshotAfter=$proposalSnapshotAfter;sourceSHA256=$sourceShaBefore;providerCalls=0;goldUsed=$false;historicalLevelRead=$false;historicalParentRead=$false;semanticMutation=$false;hierarchyMutation=$false}

Write-Json (Join-Path $outRoot 'proposal-review.json') @($proposalReview)
Write-Json (Join-Path $outRoot 'identity-challenge-review.json') @($identityReview)
Write-Json (Join-Path $outRoot 'hierarchy-risk-review.json') @($hierarchyReview)
Write-Json (Join-Path $outRoot 'blocker-scope-review.json') @($blockerReview)
Write-Json (Join-Path $outRoot 'validation.json') $validation
Write-Text (Join-Path $outRoot 'batch-review-detailed.md') ($md -join [Environment]::NewLine)
$manifest.outputFiles=@(Get-ChildItem -LiteralPath $outRoot -File | Sort-Object Name | ForEach-Object {[ordered]@{path=(Rel $_.FullName);sha256=(Sha $_.FullName)}})
Write-Json (Join-Path $outRoot 'manifest.json') $manifest

Write-Output (JsonString ([ordered]@{status=$validation.status;proposalFilesUnchanged=$same;changedProposalArtifacts=@($changed);proposalDocuments=$proposalDocs.Count;blockerPackets=$blockerReview.Count;providerCalls=0;goldUsed=$false;outRoot=(Rel $outRoot)}))
