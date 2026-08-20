param(
    [string]$Corpus = "todo10_8\heading_corpus_95_word",
    [string]$OutDir = ".verify-build\deterministic-audit",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $root
try {
    $cli = Join-Path $root "src\DocxHeaderExtractor.Cli\bin\Debug\net9.0\dhx.dll"
    if (!$NoBuild -or !(Test-Path $cli)) {
        dotnet build DocxHeaderExtractor.sln --no-restore | Out-Host
    }

    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $files = Get-ChildItem -Path $Corpus -Recurse -Filter *.docx | Sort-Object FullName
    $rows = New-Object System.Collections.Generic.List[object]
    $idx = 0

    foreach ($file in $files) {
        $idx++
        Write-Host "[$idx/$($files.Count)] $($file.Name)"
        $sw = [Diagnostics.Stopwatch]::StartNew()
        try {
            $json = & dotnet $cli extract $file.FullName --no-llm -f json -q 2>$null
            $outline = ($json -join "`n") | ConvertFrom-Json
            $diag = $outline.diagnostics
            $accepted = @($diag.candidates | Where-Object { $_.accepted }).Count
            $best = $diag.candidates |
                Sort-Object -Property @{ Expression = "accepted"; Descending = $true },
                                      @{ Expression = "headingCount"; Descending = $true } |
                Select-Object -First 1
            $rfc = $diag.candidates | Where-Object { $_.route -eq "auto:rfc-toc-dictionary" } | Select-Object -First 1

            $rows.Add([pscustomobject]@{
                file = $file.Name
                relPath = Resolve-Path -Relative $file.FullName
                group = Split-Path (Split-Path $file.FullName -Parent) -Leaf
                paragraphs = [int]$outline.paragraphCount
                candidates = [int]$outline.candidateCount
                headings = @($outline.headings).Count
                deterministicRoute = [string]$outline.deterministicRoute
                mode = [string]$outline.documentMode.mode
                diagStatus = [string]$diag.status
                diagReason = [string]$diag.reason
                acceptedCandidates = $accepted
                bestCandidate = [string]$best.route
                bestAccepted = [bool]$best.accepted
                bestReason = [string]$best.reason
                styleMixed = [bool]$diag.style.mixed
                styleSelectionTrusted = [bool]$diag.style.selectionTrusted
                styleLevelTrusted = [bool]$diag.style.levelTrusted
                mergedParagraphs = [int]$diag.layout.mergedParagraphs
                mergedMarkers = [int]$diag.layout.mergedMarkers
                rfcAccepted = [bool]($rfc -and $rfc.accepted)
                rfcBodyAnchorRatio = if ($rfc -and $null -ne $rfc.bodyAnchorRatio) { [double]$rfc.bodyAnchorRatio } else { $null }
                elapsedMs = [int64]$outline.elapsedMs
                auditMs = [int64]$sw.ElapsedMilliseconds
                error = ""
            })
        }
        catch {
            $rows.Add([pscustomobject]@{
                file = $file.Name
                relPath = Resolve-Path -Relative $file.FullName
                group = Split-Path (Split-Path $file.FullName -Parent) -Leaf
                paragraphs = 0
                candidates = 0
                headings = 0
                deterministicRoute = ""
                mode = ""
                diagStatus = "error"
                diagReason = $_.Exception.Message
                acceptedCandidates = 0
                bestCandidate = ""
                bestAccepted = $false
                bestReason = ""
                styleMixed = $false
                styleSelectionTrusted = $false
                styleLevelTrusted = $false
                mergedParagraphs = 0
                mergedMarkers = 0
                rfcAccepted = $false
                rfcBodyAnchorRatio = $null
                elapsedMs = 0
                auditMs = [int64]$sw.ElapsedMilliseconds
                error = $_.Exception.Message
            })
        }
    }

    $csv = Join-Path $OutDir "deterministic-audit.csv"
    $jsonOut = Join-Path $OutDir "deterministic-audit.json"
    $rows | Export-Csv -NoTypeInformation -Encoding UTF8 $csv
    $rows | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 $jsonOut

    Write-Host "WROTE $csv"
    Write-Host "WROTE $jsonOut"
    Write-Host ""
    Write-Host "Routes:"
    $rows | Group-Object deterministicRoute | Sort-Object Count -Descending | Format-Table Count, Name -AutoSize
    Write-Host "Diagnostics:"
    $rows | Group-Object diagStatus, diagReason | Sort-Object Count -Descending | Format-Table Count, Name -AutoSize
}
finally {
    Pop-Location
}
