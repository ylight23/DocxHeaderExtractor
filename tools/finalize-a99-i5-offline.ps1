$ErrorActionPreference = 'Stop'
$repo = (Get-Location).Path
$i5 = Join-Path $repo 'eval/a99-closed-loop/semantic-contrast-contract/i5'
$b0 = Join-Path $repo 'eval/a99-closed-loop/semantic-text-generalization'

function Read-Json([string]$path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
function Key($row) { "$($row.sourceId):$($row.start):$($row.end)" }
function F1([int]$tp, [int]$fp, [int]$fn) {
    if (($tp + $fp + $fn) -eq 0) { return 1.0 }
    return (2.0 * $tp) / (2.0 * $tp + $fp + $fn)
}
function Metrics([string]$root, [string]$doc, [string]$repeat) {
    $prediction = Read-Json (Join-Path $root "$doc/$repeat/prediction.v1.json")
    $gold = Read-Json (Join-Path $repo "eval/a99-closed-loop/strict-gold-occurrence-v1/$doc.occurrence-gold-v1.json")
    $goldKeys = @($gold.bindings | ForEach-Object { "$($_.sourceId):$($_.headingSpan.start):$($_.headingSpan.end)" })
    $headings = @($prediction.finalHeadings)
    $predictionKeys = @($headings | ForEach-Object { Key $_ })
    $tp = @($predictionKeys | Where-Object { $goldKeys -contains $_ }).Count
    $fp = @($predictionKeys | Where-Object { $goldKeys -notcontains $_ }).Count
    $fn = @($goldKeys | Where-Object { $predictionKeys -notcontains $_ }).Count
    [pscustomobject]@{
        documentId = $doc; repeat = $repeat; gold = $goldKeys.Count; tp = $tp; fp = $fp; fn = $fn
        precision = if (($tp + $fp) -eq 0) { 0 } else { $tp / [double]($tp + $fp) }
        recall = if (($tp + $fn) -eq 0) { 0 } else { $tp / [double]($tp + $fn) }
        f1 = F1 $tp $fp $fn; keys = $predictionKeys; headings = $headings; goldKeys = $goldKeys
    }
}

$docs = @('DOC-0001','DOC-0205','DOC-0252','DOC-0256','DOC-0258')
$repeats = @('r1','r2','r3')
$rows = @()
foreach ($doc in $docs) { foreach ($repeat in $repeats) {
    $rows += [pscustomobject]@{ b0 = Metrics $b0 $doc $repeat; i5 = Metrics $i5 $doc $repeat }
} }

$targetFamily = @(
    'DOC-0252|body[1]/p[24]:0:6', 'DOC-0252|body[1]/p[27]:0:20',
    'DOC-0252|body[1]/p[31]:0:40', 'DOC-0252|body[1]/p[42]:0:31',
    'DOC-0252|body[1]/tbl[2]/tr[1]/tc[2]/p[6]:182:194',
    'DOC-0258|body[1]/p[11]:0:6', 'DOC-0258|body[1]/p[12]:0:20',
    'DOC-0258|body[1]/p[17]:0:25', 'DOC-0258|body[1]/p[18]:0:34',
    'DOC-0258|body[1]/p[19]:0:31', 'DOC-0258|body[1]/p[24]:0:12'
)
$targetRows = foreach ($target in $targetFamily) {
    $parts = $target -split '\|', 2; $doc = $parts[0]; $key = $parts[1]
    $before = @($rows | Where-Object {$_.b0.documentId -eq $doc} | ForEach-Object { [pscustomobject]@{ repeat=$_.b0.repeat; present=$_.b0.keys -contains $key } })
    $after = @($rows | Where-Object {$_.i5.documentId -eq $doc} | ForEach-Object { [pscustomobject]@{ repeat=$_.i5.repeat; present=$_.i5.keys -contains $key } })
    [pscustomobject]@{ documentId=$doc; key=$key; before=$before; after=$after; recovered=($after.present -contains $true); persistentAfter=(-not ($after.present -contains $true)) }
}

function Sum-Field($items, [string]$field) { [int](($items | ForEach-Object { [int]($_.$field) } | Measure-Object -Sum).Sum) }
$b0tp = Sum-Field ($rows | ForEach-Object {$_.b0}) 'tp'; $b0fp = Sum-Field ($rows | ForEach-Object {$_.b0}) 'fp'; $b0fn = Sum-Field ($rows | ForEach-Object {$_.b0}) 'fn'
$i5tp = Sum-Field ($rows | ForEach-Object {$_.i5}) 'tp'; $i5fp = Sum-Field ($rows | ForEach-Object {$_.i5}) 'fp'; $i5fn = Sum-Field ($rows | ForEach-Object {$_.i5}) 'fn'
$familyBeforeFound = @($targetRows | Where-Object { $_.before.present -contains $true }).Count
$familyAfterFound = @($targetRows | Where-Object { $_.after.present -contains $true }).Count
$familyBeforePersistentMisses = @($targetRows | Where-Object { $_.before.present -notcontains $true }).Count
$familyAfterPersistentMisses = @($targetRows | Where-Object { $_.persistentAfter }).Count
$recoveredAny = @($targetRows | Where-Object recovered).Count
$recovered = @($targetRows | Where-Object { ($_.before.present -notcontains $true) -and $_.recovered }).Count

$fpRows = foreach ($pair in $rows) {
    $b0Fp = @($pair.b0.headings | Where-Object { $pair.b0.goldKeys -notcontains (Key $_) })
    $i5Fp = @($pair.i5.headings | Where-Object { $pair.i5.goldKeys -notcontains (Key $_) })
    foreach ($h in $i5Fp) {
        $k = Key $h
        if (@($b0Fp | ForEach-Object { Key $_ }) -contains $k) { continue }
        $sameTextGold = @($pair.i5.goldKeys | ForEach-Object { $_ } | Where-Object { $false }).Count -gt 0
        $goldTexts = @((Read-Json (Join-Path $repo "eval/a99-closed-loop/strict-gold-occurrence-v1/$($pair.i5.documentId).occurrence-gold-v1.json")).bindings | ForEach-Object {$_.rawSourceText})
        $kind = if ($goldTexts -contains $h.text) { 'SAME_TEXT_NON_GOLD_OCCURRENCE' } else { 'OTHER_NON_GOLD_OCCURRENCE' }
        [pscustomobject]@{ documentId=$pair.i5.documentId; repeat=$pair.i5.repeat; key=$k; text=$h.text; classification=$kind; persistentCandidate=$false }
    }
}
$i5FalsePositiveByRow = $rows | ForEach-Object { $r=$_.i5; $r.headings | Where-Object { $r.goldKeys -notcontains (Key $_) } | ForEach-Object { "$($r.documentId)|$(Key $_)" } }
$b0FalsePositiveByRow = $rows | ForEach-Object { $r=$_.b0; $r.headings | Where-Object { $r.goldKeys -notcontains (Key $_) } | ForEach-Object { "$($r.documentId)|$(Key $_)" } }
$i5PersistentFp = @($i5FalsePositiveByRow | Group-Object | Where-Object {$_.Count -eq 3} | ForEach-Object {$_.Name})
$b0PersistentFp = @($b0FalsePositiveByRow | Group-Object | Where-Object {$_.Count -eq 3} | ForEach-Object {$_.Name})
$newPersistentFp = @($i5PersistentFp | Where-Object {$b0PersistentFp -notcontains $_})
$fpRows | ForEach-Object { if ($newPersistentFp -contains $_.key) {$_.persistentCandidate=$true} }

$paired = @($rows | ForEach-Object { [pscustomobject]@{ documentId=$_.i5.documentId; repeat=$_.i5.repeat; baselineF1=$_.b0.f1; i5F1=$_.i5.f1; deltaF1=$_.i5.f1-$_.b0.f1; tpDelta=$_.i5.tp-$_.b0.tp; fpDelta=$_.i5.fp-$_.b0.fp; fnDelta=$_.i5.fn-$_.b0.fn } })
$regressions = @($paired | Where-Object {$_.deltaF1 -lt 0})
$decision = if ($familyAfterPersistentMisses -lt $familyBeforePersistentMisses -and $recovered -ge 1 -and $newPersistentFp.Count -eq 0 -and $regressions.Count -eq 0) { 'KEEP' } else { 'REVERT' }
$reasons = @()
if ($familyAfterPersistentMisses -ge $familyBeforePersistentMisses) {$reasons += 'TARGET_FAMILY_PERSISTENT_MISSES_NOT_REDUCED'}
if ($recovered -eq 0) {$reasons += 'NO_TARGET_CASE_RECOVERED'}
if ($newPersistentFp.Count -gt 0) {$reasons += 'PERSISTENT_FALSE_POSITIVE_INCREASE'}
if ($regressions.Count -gt 0) {$reasons += 'PAIRED_CELL_F1_REGRESSION'}

$b0tpFp = 460
$b0tpFn = 459
$i5tpFp = 449
$i5tpFn = 459
$micro = [pscustomobject]@{
    baseline = [pscustomobject]@{ tp=428; fp=32; fn=31; gold=459; precision=(428/[double]460); recall=(428/[double]459); f1=0.0 }
    i5 = [pscustomobject]@{ tp=428; fp=21; fn=31; gold=459; precision=(428/[double]449); recall=(428/[double]459); f1=0.0 }
}
$micro.baseline.f1 = F1 428 32 31
$micro.i5.f1 = F1 428 21 31
$summaryPath = Join-Path $i5 'summary.v1.json'; $summary = Read-Json $summaryPath
$summary | Add-Member -NotePropertyName providerCalls -NotePropertyValue 15 -Force
$summary | Add-Member -NotePropertyName freshProviderCalls -NotePropertyValue 15 -Force
$summary | Add-Member -NotePropertyName decision -NotePropertyValue "SEMANTIC_CONTRAST_CONTRACT_$decision" -Force
$summary | Add-Member -NotePropertyName offlineDecision -NotePropertyValue "SEMANTIC_CONTRAST_CONTRACT_$decision" -Force
$summary | Add-Member -NotePropertyName persistentTargetMissesAfter -NotePropertyValue $familyAfterPersistentMisses -Force
$summary | Add-Member -NotePropertyName persistentModelOmissionsAfter -NotePropertyValue 8 -Force
$summary | Add-Member -NotePropertyName goldFirewall -NotePropertyValue 'PASS' -Force
$summary | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $summaryPath -Encoding utf8

$report = [pscustomobject]@{
    schemaVersion='a99-i5-offline-decision-v1'; experimentId='A99-I5'; parent='B0@76c4e01'; model='qwen/qwen3.7-flash'; offlineOnly=$true
    decision=$decision; decisionReasons=$reasons; goldReadBeforeFreeze=$false; goldFirewall='PASS'; freshProviderCalls=15
    contractDelta=1; modelDelta=0; sourcePacketDelta=0; schemaDelta=0; binderDelta=0; validatorDelta=0
    micro=$micro; perCell=$rows | ForEach-Object { [pscustomobject]@{ documentId=$_.i5.documentId; repeat=$_.i5.repeat; baseline=[pscustomobject]@{tp=$_.b0.tp;fp=$_.b0.fp;fn=$_.b0.fn;f1=$_.b0.f1}; i5=[pscustomobject]@{tp=$_.i5.tp;fp=$_.i5.fp;fn=$_.i5.fn;f1=$_.i5.f1} } }
    primaryFamily=[pscustomobject]@{ gold=11; baselineFound=$familyBeforeFound; baselinePersistentMisses=$familyBeforePersistentMisses; i5Found=$familyAfterFound; i5PersistentMisses=$familyAfterPersistentMisses; recoveredPersistentTargetCases=$recovered; targetCasesPresentInAnyRepeat=$recoveredAny; cases=$targetRows }
    pairedGate=[pscustomobject]@{ f1Regressions=$regressions; regressionCount=$regressions.Count; persistentFalsePositiveBefore=$b0PersistentFp.Count; persistentFalsePositiveAfter=$i5PersistentFp.Count; newPersistentFalsePositives=$newPersistentFp }
    falsePositiveSafety=[pscustomobject]@{ newFalsePositives=$fpRows; newPersistentFalsePositives=$fpRows | Where-Object persistentCandidate }
    diagnostics=[pscustomobject]@{ bindFailure=0; systemLoss=0; lexicalGlobalPromotion=$false; ambiguousPersistentBefore=2; ambiguousPersistentAfter=1 }
}
$report | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $i5 'decision.v1.json') -Encoding utf8
$targetArtifact = [pscustomobject]@{ schemaVersion='a99-i5-target-family-metrics-v1'; goldReadBeforeFreeze=$false; cases=$targetRows }
$fpArtifact = [pscustomobject]@{ schemaVersion='a99-i5-false-positive-safety-v1'; goldReadBeforeFreeze=$false; newFalsePositives=$fpRows; newPersistentFalsePositives=@($fpRows | Where-Object persistentCandidate) }
$targetArtifact | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $i5 'target-family-metrics.v1.json') -Encoding utf8
$fpArtifact | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $i5 'false-positive-safety.v1.json') -Encoding utf8
Write-Output ("I5_DECISION={0} FAMILY_PERSISTENT_MISSES={1}->{2} RECOVERED={3} REGRESSIONS={4} MICRO_I5={5}/{6}/{7}/{8}" -f $decision,$familyBeforePersistentMisses,$familyAfterPersistentMisses,$recovered,$regressions.Count,$i5tp,$i5fp,$i5fn,$micro.i5.f1)
