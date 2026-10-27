param([Parameter(Mandatory=$true)][string]$SourceRun, [Parameter(Mandatory=$true)][string]$Output)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $Output) { throw 'REFUSE_OVERWRITING_RECOVERY' }
$null = New-Item -ItemType Directory -Path (Join-Path $Output 'luna-responses')
$records = @()
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $SourceRun 'luna-responses') -Filter '*.response.json') {
    $requestHash = $file.Name.Replace('.response.json','')
    $sse = Join-Path $SourceRun ('luna-sse-responses/' + $requestHash + '.sse')
    $original = Get-Content -Raw -LiteralPath $file.FullName | ConvertFrom-Json -Depth 100
    $reply = $original
    $basis = $file.FullName
    if (Test-Path -LiteralPath $sse) {
        # Local bridge writes one delta event. Prefer what the native runner actually consumed.
        $events = @(Get-Content -LiteralPath $sse | Where-Object { $_.StartsWith('data: ') -and $_ -ne 'data: [DONE]' })
        if ($events.Count -ne 1) { throw 'UNEXPECTED_LOCAL_SSE_SHAPE' }
        $evt = $events[0].Substring(6) | ConvertFrom-Json -Depth 100
        $delta = $evt.choices[0].delta
        $calls = @($delta.tool_calls | Where-Object { $null -ne $_ } | ForEach-Object {
            @{id=$_.id; name=$_.function.name; arguments=$_.function.arguments}
        })
        $reply = @{ requestSha256=$requestHash; content=$delta.content; finishReason=$evt.choices[0].finish_reason; toolCalls=$calls }
        $basis = $sse
    }
    if ($reply.requestSha256 -cne $requestHash) { throw 'RESPONSE_SHA_MISMATCH' }
    # Re-encode only JSON syntax. No semantic field/value, decision or source alias is changed.
    $encoded = ConvertTo-Json -InputObject $reply -Depth 100 -Compress
    $strict = [System.Text.Json.JsonDocument]::Parse($encoded, [System.Text.Json.JsonDocumentOptions]::new())
    $strict.Dispose()
    $dest = Join-Path $Output ('luna-responses/' + $file.Name)
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($dest), $encoded, [Text.UTF8Encoding]::new($false))
    $records += @{requestSha256=$requestHash; sourcePath=$basis; sourceSha256=(Get-FileHash -LiteralPath $basis -Algorithm SHA256).Hash.ToLowerInvariant();
        outputSha256=(Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.ToLowerInvariant();
        responseEnvelopeDifferedFromConsumedSse=($basis -eq $sse -and $original.content -cne $reply.content)}
}
$manifest = @{sourceRun=[IO.Path]::GetFullPath($SourceRun); mode='TRANSPORT_RECOVERY_NOT_FRESH_INFERENCE'; records=$records; count=$records.Count; goldRead=$false}
[IO.File]::WriteAllText([IO.Path]::GetFullPath((Join-Path $Output 'recovery-manifest.json')), ($manifest|ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
$manifest | Select-Object count,mode,goldRead | ConvertTo-Json
