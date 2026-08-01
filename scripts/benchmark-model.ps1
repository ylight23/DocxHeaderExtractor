param(
    [string]$Corpus = "bench\\holdout",
    [string]$Model = "models\\Qwen2.5-7B-Instruct-Q4_K_M.gguf",
    [int]$Context = 8192,
    [int[]]$ChunkCandidates = @(8, 12, 16, 20),
    [int]$GpuLayers = 99,
    [string]$Output = "bench\\benchmark-results.csv"
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location $projectRoot

if (-not (Test-Path $Corpus)) { throw "Không tìm thấy corpus: $Corpus" }
if (-not (Test-Path $Model)) { throw "Không tìm thấy model: $Model" }

$rows = foreach ($count in $ChunkCandidates) {
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $outputText = & dotnet run --project src\DocxHeaderExtractor.Cli -c Release --no-build -- `
        eval $Corpus --model $Model --ctx $Context --gpu-layers $GpuLayers --chunk-candidates $count 2>&1
    $exit = $LASTEXITCODE
    $stopwatch.Stop()

    $metrics = ($outputText | Select-String 'Gộp toàn bộ đoạn:' | Select-Object -Last 1).ToString()
    [PSCustomObject]@{
        timestamp_utc = [DateTime]::UtcNow.ToString('o')
        corpus = (Resolve-Path $Corpus).Path
        model = Split-Path $Model -Leaf
        chunk_candidates = $count
        elapsed_seconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
        exit_code = $exit
        metrics = $metrics
    }
}

$rows | Export-Csv -Path $Output -NoTypeInformation -Encoding utf8
$rows | Format-Table -AutoSize
