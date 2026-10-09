param(
    [Parameter(Mandatory=$true)][string]$CaptureDirectory,
    [Parameter(Mandatory=$true)][string]$ReceiptDirectory,
    [Parameter(Mandatory=$true)][string]$NewOutputDirectory
)
$ErrorActionPreference = 'Stop'
function HashFile([string]$path) { return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Need([bool]$condition, [string]$code) { if (-not $condition) { throw $code } }
$capturePath = Join-Path $ReceiptDirectory 'p7.d3.authorized-f1-capture.v2.json'
$forensicsPath = Join-Path $ReceiptDirectory 'p7.d3.authorized-f1-protocol-forensics.v2.json'
$capture = Get-Content -LiteralPath $capturePath -Raw | ConvertFrom-Json
$forensics = Get-Content -LiteralPath $forensicsPath -Raw | ConvertFrom-Json
Need ($capture.providerExecution -eq 'CLOSED') 'CAPTURE_NOT_CLOSED'
Need ((HashFile $capturePath) -eq $forensics.captureReceiptSha256) 'CAPTURE_RECEIPT_DRIFT'
Need ((HashFile (Join-Path $CaptureDirectory 'capture-freeze.json')) -eq $capture.rawCaptureFreezeSha256) 'CAPTURE_FREEZE_DRIFT'
Need (-not (Test-Path -LiteralPath $NewOutputDirectory)) 'NEW_OUTPUT_REQUIRED'
$directories = @(Get-ChildItem -LiteralPath $CaptureDirectory -Recurse -File -Filter attempt-receipt.json | Sort-Object FullName)
Need ($directories.Count -eq 21 -and $capture.attempts.Count -eq 21) 'ATTEMPT_COUNT_DRIFT'
$files = [Collections.Generic.List[object]]::new()
$attempts = [Collections.Generic.List[object]]::new()
$seen = [Collections.Generic.HashSet[string]]::new()
$secrets = @([Environment]::GetEnvironmentVariable('OPENROUTER_API_KEY')) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
$staged = [Collections.Generic.List[object]]::new()
foreach ($file in $directories) {
    $a = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    Need ($seen.Add($a.callHandle + '|' + $a.attempt)) 'DUPLICATE_ATTEMPT'
    $row = @($forensics.attempts | Where-Object { $_.callHandle -eq $a.callHandle -and $_.attempt -eq $a.attempt })
    Need ($row.Count -eq 1) 'UNREGISTERED_ATTEMPT'
    Need ((HashFile $file.FullName) -eq $row[0].attemptReceiptSha256) 'ATTEMPT_RECEIPT_DRIFT'
    $dir = $file.Directory.FullName
    $relative = [IO.Path]::GetRelativePath([IO.Path]::GetFullPath($CaptureDirectory), $dir).Replace('\','/')
    Need ($relative -match '^\d{2}/attempt-[12]$') 'UNEXPECTED_CAPTURE_PATH'
    $frozen = Get-Content -LiteralPath (Join-Path $dir 'raw-freeze.json') -Raw | ConvertFrom-Json
    Need ($frozen.bodySha256 -eq $a.bodySha256) 'BODY_IDENTITY_DRIFT'
    $hashes = [ordered]@{
        'response.txt' = $row[0].responseSha256
        'response.sse' = $row[0].rawSseSha256
        'observation.json' = $row[0].rawObservationSha256
        'raw-freeze.json' = $row[0].rawFreezeSha256
        'attempt-receipt.json' = $row[0].attemptReceiptSha256
    }
    if ($a.status -eq 'ACCEPTED') { $hashes['parsed-decision.json'] = $a.parsedDecisionSha256 }
    foreach ($name in $hashes.Keys) {
        $source = Join-Path $dir $name
        Need ((HashFile $source) -eq $hashes[$name]) 'RAW_FILE_DRIFT'
        $staged.Add([pscustomobject]@{source=$source;path="$relative/$name";sha256=$hashes[$name]})
    }
    $attempts.Add([ordered]@{ callHandle=$a.callHandle; attempt=$a.attempt; status=$a.status
        validatorCode=$row[0].validatorCode; directory=$relative; bodySha256=$a.bodySha256
        previousAttemptSha256=$a.previousAttemptSha256; finishReason=$a.finishReason })
}
$staged.Add([pscustomobject]@{source=(Join-Path $CaptureDirectory 'capture-freeze.json');path='capture-freeze.json';sha256=$capture.rawCaptureFreezeSha256})
# Only allowlisted raw responses and receipts. Request bodies/headers, API keys, authorization,
# metadata snapshots, local paths, and private exception text are deliberately not exported.
foreach ($item in $staged) {
    $text = [IO.File]::ReadAllText($item.source)
    Need (-not [regex]::IsMatch($text, '(?i)sk-or-v1-[a-z0-9]{16,}|Bearer\s+[a-z0-9._-]{20,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----')) 'CREDENTIAL_PATTERN_DETECTED'
    foreach ($secret in $secrets) { Need (-not $text.Contains($secret, [StringComparison]::Ordinal)) 'ACTIVE_CREDENTIAL_DETECTED' }
}
[IO.Directory]::CreateDirectory($NewOutputDirectory) | Out-Null
foreach ($item in $staged) {
    $destination = Join-Path $NewOutputDirectory $item.path
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    [IO.File]::Copy($item.source, $destination, $false)
    Need ((HashFile $destination) -eq $item.sha256) 'COPY_CHANGED_RAW_BYTES'
    $files.Add([ordered]@{path=$item.path;length=([IO.FileInfo]::new($destination)).Length;sha256=$item.sha256})
}
$manifest = [ordered]@{ version='P7_F1_RAW_PUBLICATION_V1'; sourceResultCommit='80f56b9423267d33fc6cbbbc36ea79bad96f1346'
    captureReceiptSha256=HashFile $capturePath; forensicsReceiptSha256=HashFile $forensicsPath
    rawCaptureFreezeSha256=$capture.rawCaptureFreezeSha256; primaryAttempts=14; retryAttempts=7
    totalAttempts=21; acceptedAttempts=7; rejectedAttempts=14; files=$files; attempts=$attempts
    originalBytesPreserved=$true; redacted=$false; providerCalls=0; productionChanged=$false; goldChanged=$false
    credentialScan='KNOWN_ACTIVE_OPENROUTER_KEY_AND_CREDENTIAL_PATTERNS_NO_MATCH'
    disclosure='Original provider completions, SSE (including provider-generated reasoning when present), observations and receipts; no request bodies/headers or private exceptions'
    semanticComparison='NOT_EVALUABLE_TREATMENT_B_CONTRACT_REJECTED'
}
$manifestPath = Join-Path $NewOutputDirectory 'manifest.json'
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 32 -Compress), [Text.UTF8Encoding]::new($false))
[ordered]@{status='RAW_PUBLISHED_LOCALLY';files=$files.Count;attempts=$attempts.Count;manifestSha256=HashFile $manifestPath;totalBytes=($files | ForEach-Object { $_.length } | Measure-Object -Sum).Sum} | ConvertTo-Json -Compress
