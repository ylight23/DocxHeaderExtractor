param([string]$RepoRoot=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path)
$ErrorActionPreference='Stop'; $out=Join-Path $RepoRoot 'eval\a99-dataset'; $names=@('document-inventory.v1.json','document-groups.v1.json','structural-families.v1.json','family-coverage.v1.json','evaluation-splits.v1.json','dataset-freeze.v1.json')
foreach($n in $names){if(!(Test-Path (Join-Path $out $n))){throw "missing $n"}}
$inv=Get-Content (Join-Path $out $names[0])|ConvertFrom-Json; $groups=Get-Content (Join-Path $out $names[1])|ConvertFrom-Json; $fam=Get-Content (Join-Path $out $names[2])|ConvertFrom-Json; $splits=Get-Content (Join-Path $out $names[4])|ConvertFrom-Json
if($inv.metadata.providerCalls -ne 0 -or $inv.metadata.trueBlindAvailable){throw 'provider/blind invariant failed'}
if($groups.metadata.uniqueDocumentGroups -ne @($groups.groups).Count){throw 'group count mismatch'}
$joined=@($groups.groups|ForEach-Object {$id=$_.documentGroupId; @($splits.splits|Where-Object documentGroupId -eq $id).split|Sort-Object -Unique})
if(@($joined|Where-Object { @($_).Count -ne 1 }).Count){throw 'group crosses split'}
if(@($fam.documents|Where-Object { $_.PSObject.Properties.Name -match 'prediction|gold|accuracy' }).Count){throw 'model/gold field in fingerprint'}
if(@($fam.documents|Where-Object {$_.familyAssignmentAuthority -notin @('PATH_HINT','SOURCE_FACTS','MIXED','UNKNOWN')}).Count){throw 'invalid family authority'}
$policy=Get-Content (Join-Path $out 'reference-authority-policy.v1.json') -Raw; if($policy -match 'MODEL_ASSISTED_SILVER[^\]]*HUMAN_GOLD'){throw 'silver promoted to gold'}
$freeze=Get-Content (Join-Path $out 'dataset-freeze.v1.json')|ConvertFrom-Json; if($freeze.crossSplitLeakage -ne 0 -or $freeze.providerCalls -ne 0){throw 'freeze invariant failed'}
Write-Host "A99 focused tests PASS ($($names.Count) artifact checks; groups=$($groups.metadata.uniqueDocumentGroups); leakage=0; providers=0)"
