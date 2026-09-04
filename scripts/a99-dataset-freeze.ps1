param([string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path, [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$out = if($OutputDirectory){(Resolve-Path $OutputDirectory -ErrorAction SilentlyContinue).Path}; if(!$out){$out=Join-Path $RepoRoot 'eval\a99-dataset'}
New-Item -ItemType Directory -Force -Path $out | Out-Null

function Sha256([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }
function TextSha256([string]$s) { $h=[Security.Cryptography.SHA256]::Create(); try { ([BitConverter]::ToString($h.ComputeHash([Text.Encoding]::UTF8.GetBytes($s))).Replace('-','')).ToLowerInvariant() } finally {$h.Dispose()} }
function Norm([string]$s) { [regex]::Replace($s.ToLowerInvariant(), '\s+', ' ').Trim() }
function DocxText([string]$p) {
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $z=[IO.Compression.ZipFile]::OpenRead($p); try { $e=$z.GetEntry('word/document.xml'); if(!$e){return ''}; $r=New-Object IO.StreamReader($e.Open()); try { return (Norm ([regex]::Replace($r.ReadToEnd(), '<[^>]+>', ' '))) } finally {$r.Dispose()} } finally {$z.Dispose()}
}
function Authority([string]$p) {
  $n=(Split-Path $p -Leaf).ToLowerInvariant()
  if($p -match '\\keys\\') { if($n -match 'silver|model') {'MODEL_ASSISTED_SILVER'} elseif($n -match 'toc') {'HEURISTIC_REFERENCE'} elseif($n -match 'partial') {'HUMAN_KEY'} else {'HUMAN_KEY'} }
  elseif($p -match 'silver-labels') {'MODEL_ASSISTED_SILVER'} elseif($p -match 'source-first-reference') {'SOURCE_STRUCTURAL_REFERENCE'} else {'UNLABELED'}
}
function SourceKind([string]$p) { if($p -match '\.docx$|\.docm$') {'DOCX'} elseif($p -match '\.pdf$') {'PDF'} elseif($p -match '\.doc$') {'DOC'} else {'OTHER'} }
$files=Get-ChildItem -LiteralPath $RepoRoot -Recurse -File | Where-Object { $_.FullName -notmatch '\\(bin|obj|TestResults|\.git)\\' -and $_.Extension -match '^\.(docx|docm|doc|pdf)$' }
$rows=@(); $i=0
foreach($f in $files) { $i++; $sha=Sha256 $f.FullName; $text=if((SourceKind $f.FullName) -eq 'DOCX'){DocxText $f.FullName}else{''}; $fp=if($text){(Sha256 ([IO.Path]::GetTempFileName()))}else{$sha}; $rel=$f.FullName.Substring($RepoRoot.Length+1); $family='UNKNOWN'; if($rel -match '01_phap_quy|legal'){ $family='VN_LEGAL_MARKER' } elseif($rel -match '05_bien_ban_hop'){ $family='VN_ADMIN_TYPED' } elseif($rel -match 'heading_corpus_100.*\.pdf$'){ $family='PDF_NATIVE_LAYOUT' } elseif($rel -match 'heading_corpus_95_word|generated-docx'){ $family='PDF_CONVERTED' } elseif($rel -match '04_giao_trinh|07_system_generated'){ $family='SEMANTIC_ONLY' } elseif($rel -match 'bench'){ $family='DOCX_NATIVE_STRUCTURED' }; $rows += [ordered]@{documentId=('DOC-{0:D4}' -f $i);sourcePath=$rel;mediaType=SourceKind $f.FullName;sourceSha256=$sha;normalizedContentFingerprint=$fp;referenceArtifactPath=$null;referenceSha256=$null;referenceKind='NONE';referenceAuthority=Authority $f.FullName;validationStatus='NOT_APPLICABLE';documentGroupId=$null;duplicateKind='UNIQUE';familyId=$family;familyEvidence='deterministic path/source-kind rule';familyConfidence=if($family -eq 'UNKNOWN'){'LOW'}else{'MEDIUM'};paragraphCount=$null;tableDensity=$null;outlineLevelRatio=$null;styledHeadingCount=$null;numberingRatio=$null;tocPresence=$false} }
# Family labels are conservative path hints; no production DocumentMode is changed.
foreach($r in $rows){$r['familyAssignmentAuthority']=if($r['familyId'] -eq 'UNKNOWN'){'UNKNOWN'}else{'PATH_HINT'}}
# Exact byte groups are authoritative. Derivative and overlap grouping is intentionally not claimed.
$groups=@(); $bySha=$rows | Group-Object { $_['sourceSha256'] }; $g=0
foreach($grp in $bySha){$g++;$gid=('GROUP-{0:D4}'-f $g);foreach($r in $grp.Group){$r['documentGroupId']=$gid;if($grp.Count -gt 1){$r['duplicateKind']='EXACT_BYTES'}};$groups += [ordered]@{documentGroupId=$gid;documentIds=@($grp.Group|ForEach-Object {$_['documentId']});duplicateKind=if($grp.Count -gt 1){'EXACT_BYTES'}else{'UNIQUE'};evidence='sourceSha256'}}
$meta=[ordered]@{schemaVersion='1.0';createdFromCodeSha=(git -C $RepoRoot rev-parse HEAD).Trim();generationMode='DETERMINISTIC';providerCalls=0;totalFiles=$rows.Count;uniqueDocumentGroups=$groups.Count;trueBlindAvailable=$false}
function WriteJson($name,$obj){$obj | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $out $name) -Encoding utf8}
$policy=[ordered]@{schemaVersion='1.0';generationMode='DETERMINISTIC';providerCalls=0;trueBlindAvailable=$false;tiers=[ordered]@{HUMAN_GOLD=@('official accuracy claim','precision/recall','hierarchy accuracy');HUMAN_KEY=@('engineering regression','role/level diagnostics');SOURCE_STRUCTURAL_REFERENCE=@('strict deterministic structural regression');MODEL_ASSISTED_SILVER=@('development diagnostics','semantic comparison');HEURISTIC_REFERENCE=@('proxy diagnostics only');UNLABELED=@('runtime/profile/family statistics only');INVALID_REFERENCE=@()};prohibitions=@('silver cannot become HUMAN_GOLD','accessible committed references are not true blind','family fingerprints exclude gold, predictions, confidence and benchmark scores')}
WriteJson 'reference-authority-policy.v1.json' $policy
WriteJson 'document-inventory.v1.json' ([ordered]@{metadata=$meta;documents=$rows})
WriteJson 'document-groups.v1.json' ([ordered]@{metadata=$meta;groups=$groups})
$families=$rows | Group-Object familyId | ForEach-Object {[ordered]@{familyId=$_.Name;documents=$_.Count;uniqueGroups=(@($_.Group.documentGroupId)|Sort-Object -Unique).Count;humanBackedGroups=0;sourceStructuralBackedGroups=0;silverOnly=0;unlabeled=$_.Count}}
WriteJson 'structural-families.v1.json' ([ordered]@{metadata=$meta;documents=$rows|ForEach-Object {[ordered]@{documentId=$_.documentId;familyId=$_.familyId;familyEvidence=$_.familyEvidence;familyConfidence=$_.familyConfidence;familyAssignmentAuthority=$_.familyAssignmentAuthority}}})
WriteJson 'family-coverage.v1.json' ([ordered]@{metadata=$meta;families=$families})
$splits=$groups | ForEach-Object -Begin {$n=0} -Process {$n++;$class=if($n%5 -eq 0){'GENERALIZATION_HOLDOUT'}elseif($n%7 -eq 0){'RESERVED_UNLABELED'}else{'DEV'};[ordered]@{documentGroupId=$_.documentGroupId;split=$class;reason='deterministic stable group ordinal; review family balance before freeze'}}
WriteJson 'evaluation-splits.v1.json' ([ordered]@{metadata=$meta;splits=$splits})
WriteJson 'dataset-freeze.v1.json' ([ordered]@{schemaVersion='1.0';createdFromCodeSha=$meta.createdFromCodeSha;generationMode='DETERMINISTIC';providerCalls=0;trueBlindAvailable=$false;crossSplitLeakage=0;sourceInventorySha256=(Sha256 (Join-Path $out 'document-inventory.v1.json'));referenceAuthorityPolicySha256=(Sha256 (Join-Path $out 'reference-authority-policy.v1.json'));artifacts=@('reference-authority-policy.v1.json','document-inventory.v1.json','document-groups.v1.json','structural-families.v1.json','family-coverage.v1.json','evaluation-splits.v1.json')})
Write-Host "TOTAL_FILES=$($rows.Count) UNIQUE_DOCUMENT_GROUPS=$($groups.Count)"
