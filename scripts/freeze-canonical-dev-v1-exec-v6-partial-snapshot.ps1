$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$v6 = Join-Path $repo 'artifacts/level-accuracy/canonical-dev-v1-exec-v6'
$snapshot = Join-Path $v6 'partial-forensic-snapshot-v2'
$payload = Join-Path $snapshot 'payload'

if (-not (Test-Path $v6)) { throw "V6 output root not found: $v6" }
if (Test-Path $snapshot) {
    if (Test-Path (Join-Path $snapshot 'artifact-manifest.json')) {
        throw "Snapshot already exists; refusing to overwrite: $snapshot"
    }
    Get-ChildItem $snapshot -Recurse -Force -File | ForEach-Object { $_.IsReadOnly = $false }
    Remove-Item -LiteralPath $snapshot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $payload | Out-Null

$relativeFiles = [System.Collections.Generic.List[string]]::new()
$rootFiles = @('preflight.v1.json', 'run-configuration.json', 'production-run-manifest.json')
foreach ($name in $rootFiles) {
    $path = Join-Path $v6 $name
    if (Test-Path $path) { $relativeFiles.Add($name) }
}
foreach ($doc in @('DOC-0001', 'DOC-0116', 'DOC-0122')) {
    foreach ($docRoot in @((Join-Path $v6 $doc), (Join-Path $v6 "work/$doc"))) {
        if (-not (Test-Path $docRoot)) { continue }
        $prefix = if ($docRoot -like (Join-Path $v6 'work/*')) { "work/$doc" } else { $doc }
        Get-ChildItem $docRoot -Recurse -File | ForEach-Object {
            $relativeFiles.Add((Join-Path $prefix $_.FullName.Substring($docRoot.Length + 1)))
        }
    }
}
$lifecycle = Join-Path $repo 'artifacts/execution-integrity/canonical-worker-launch-v1/orchestration-lifecycle/summary.json'
if (Test-Path $lifecycle) { $relativeFiles.Add('execution-integrity/canonical-worker-launch-v1/orchestration-lifecycle/summary.json') }

$entries = foreach ($relative in ($relativeFiles | Sort-Object -Unique)) {
    $source = if ($relative -like 'execution-integrity/*') {
        Join-Path $repo ('artifacts/' + $relative)
    } else {
        Join-Path $v6 $relative
    }
    if (-not (Test-Path $source -PathType Leaf)) { throw "Frozen artifact disappeared: $source" }
    $destination = Join-Path $payload $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Force
    $hash = Get-FileHash -LiteralPath $destination -Algorithm SHA256
    [pscustomobject]@{
        relativePath = $relative.Replace('\', '/')
        bytes = (Get-Item -LiteralPath $destination).Length
        sha256 = $hash.Hash.ToLowerInvariant()
    }
}

$summary = [ordered]@{
    schemaVersion = 'a99-canonical-dev-v1-exec-v6-partial-forensic-snapshot-v2'
    status = 'STOPPED_FOR_AUTHORIZED_PRODUCTION_OPTIMIZATION'
    campaignId = 'CANONICAL_DEV_V1_EXEC_V6'
    scorable = $false
    goldReads = 0
    historicalLevelReads = 0
    historicalParentReads = 0
    predictionFrozen = $false
    providerCalls = $null
    modelCalls = $null
    completeDocuments = @('DOC-0001', 'DOC-0116')
    partialDocuments = @('DOC-0122')
    stoppedBy = 'USER_AUTHORIZED_STOP'
    stoppedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    preservedByteForByte = $true
    artifactCount = @($entries).Count
    artifacts = $entries
}

$summaryPath = Join-Path $snapshot 'partial-forensic-snapshot.json'
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
$manifest = [ordered]@{
    schemaVersion = 'a99-canonical-dev-v1-exec-v6-partial-forensic-manifest-v2'
    snapshotStatus = 'IMMUTABLE'
    snapshotSha256 = (Get-FileHash -LiteralPath $summaryPath -Algorithm SHA256).Hash.ToLowerInvariant()
    payload = $entries
}
$manifestPath = Join-Path $snapshot 'artifact-manifest.json'
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Get-ChildItem $snapshot -Recurse -File | ForEach-Object { $_.IsReadOnly = $true }
Write-Output ("SNAPSHOT_CREATED {0} artifacts={1}" -f $snapshot, @($entries).Count)
