param(
    [Parameter(Mandatory=$true)][string]$CaptureDirectory,
    [Parameter(Mandatory=$true)][string]$CaptureReceipt,
    [Parameter(Mandatory=$true)][string]$NewReport
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $NewReport) { throw 'NEW_REPORT_REQUIRED' }
function HashBytes([byte[]]$bytes) { return [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($bytes)) }
function HashFile([string]$path) { return HashBytes ([IO.File]::ReadAllBytes($path)) }
$receiptBytes = [IO.File]::ReadAllBytes($CaptureReceipt)
$receipt = [Text.Encoding]::UTF8.GetString($receiptBytes) | ConvertFrom-Json
$freeze = Join-Path $CaptureDirectory 'capture-freeze.json'
if ($receipt.providerExecution -ne 'CLOSED' -or (HashFile $freeze) -ne $receipt.rawCaptureFreezeSha256) { throw 'RAW_FREEZE_REQUIRED' }
$attemptDirectories = @(Get-ChildItem -LiteralPath $CaptureDirectory -Filter attempt-receipt.json -File -Recurse | Sort-Object FullName)
$rows = foreach ($file in $attemptDirectories) {
    $a = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    $dir = $file.Directory.FullName
    $body = Get-Content -LiteralPath (Join-Path $dir 'provider-body.json') -Raw | ConvertFrom-Json
    if ((HashFile (Join-Path $dir 'provider-body.json')) -ne $a.bodySha256) { throw 'BODY_DRIFT' }
    $inputJson = $body.messages[1].content | ConvertFrom-Json
    $isTreatment = $inputJson.protocolVersion -eq 'P7_B_INTERPRETATION_RECORD_V2'
    $expected = if ($isTreatment) { @($inputJson.decisionSubjects) } else { @($inputJson.occurrences | ForEach-Object id) }
    $response = $null; $marker = $null; $parseable = $false
    $responseFile = Join-Path $dir 'response.txt'
    if (Test-Path -LiteralPath $responseFile) {
        $marker = Get-Content -LiteralPath (Join-Path $dir 'raw-freeze.json') -Raw | ConvertFrom-Json
        if ((HashFile (Join-Path $dir 'raw-freeze.json')) -ne $a.rawFreezeSha256 -or
            (HashFile $responseFile) -ne $marker.responseSha256 -or (HashFile (Join-Path $dir 'response.sse')) -ne $marker.sseSha256 -or
            (HashFile (Join-Path $dir 'observation.json')) -ne $a.observationSha256) { throw 'RAW_DRIFT' }
        try { $response = Get-Content -LiteralPath $responseFile -Raw | ConvertFrom-Json -ErrorAction Stop; $parseable = $true } catch { $response = $null }
    }
    $stage = if ($isTreatment) { $response.stageDecision } else { $response }
    $decisions = @($stage.decisions | Where-Object { $null -ne $_ })
    $analysis = @($response.analysis | Where-Object { $null -ne $_ })
    $seen = @($analysis | ForEach-Object subject)
    $errorFile = Join-Path $dir 'private-error.txt'
    $code = $null
    if (Test-Path -LiteralPath $errorFile) {
        # Only fixed validator codes. Never publish arbitrary exception text, model interpretation, or source excerpts.
        $match = [regex]::Match((Get-Content -LiteralPath $errorFile -First 1), 'interpretation-[a-z-]+|NON_STOP_FINISH|RESPONSE_CAP_EXCEEDED')
        if ($match.Success) { $code = $match.Value }
    }
    [ordered]@{
        callHandle = $a.callHandle; attempt = $a.attempt; status = $a.status; failureClass = $a.errorCode; validatorCode = $code
        attemptReceiptSha256 = HashFile $file.FullName; bodySha256 = $a.bodySha256; rawFreezeSha256 = $a.rawFreezeSha256
        rawObservationSha256 = $a.observationSha256; previousAttemptSha256 = $a.previousAttemptSha256
        responseSha256 = $marker.responseSha256; rawSseSha256 = $marker.sseSha256
        jsonParseable = $parseable; expectedDecisions = $expected.Count; returnedDecisions = $decisions.Count
        missingDecisionIds = @($expected | Where-Object { $_ -notin @($decisions | ForEach-Object occurrence) })
        expectedAnalysis = if ($isTreatment) { $expected.Count } else { 0 }
        returnedAnalysis = $analysis.Count; missingAnalysisSubjects = if ($isTreatment) { @($expected | Where-Object { $_ -notin $seen }) } else { @() }
        unissuedAnalysisSubjects = @($seen | Where-Object { $_ -notin $expected })
        duplicateAnalysisSubjects = @($seen | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name)
        finishReason = $a.finishReason; usage = $a.usage; semanticTruthVerified = $false
        rejectedStageDecisionsAdopted = $false; responseTextPublished = $false
    }
}
$report = [ordered]@{ version='P7_F1_ATTEMPT_PROTOCOL_FORENSICS_V1'; captureReceiptSha256=HashBytes $receiptBytes
    rawCaptureFreezeSha256=$receipt.rawCaptureFreezeSha256; attempts=$rows
    failedAttempts=@($rows | Where-Object status -ne ACCEPTED).Count
    failureCounts=@($rows | Where-Object status -ne ACCEPTED | Group-Object validatorCode | ForEach-Object { @{code=$_.Name; count=$_.Count} })
    providerCalls=0; goldRead=$false; parserRelaxed=$false; productionChanged=$false; frozenBodiesChanged=$false }
$bytes = [Text.Encoding]::UTF8.GetBytes(($report | ConvertTo-Json -Depth 32 -Compress))
$stream = [IO.FileStream]::new($NewReport,[IO.FileMode]::CreateNew)
try { $stream.Write($bytes); $stream.Flush($true) } finally { $stream.Dispose() }
@{status='FORENSICS_COMPLETE';reportSha256=HashBytes $bytes;failedAttempts=$report.failedAttempts} | ConvertTo-Json -Compress
