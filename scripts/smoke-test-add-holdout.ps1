[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$testRoot = Join-Path $env:TEMP ("dhx-holdout-e2e-" + [guid]::NewGuid().ToString('N'))
$resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
$tempPrefix = [IO.Path]::GetFullPath($env:TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar
if (-not $resolvedTestRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Đích test không nằm trong TEMP: $resolvedTestRoot"
}

$source = Join-Path $resolvedTestRoot 'source'
$holdout = Join-Path $resolvedTestRoot 'holdout'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repoRoot 'src\DocxHeaderExtractor.Cli\DocxHeaderExtractor.Cli.csproj'
$document = Join-Path $source 'holdout-moi.docx'
$reviewPath = Join-Path $source 'holdout-moi.review.json'

try {
    New-Item -ItemType Directory -Path $source, $holdout | Out-Null

    & dotnet run --project $project -c Release --no-launch-profile -- sample $document *> $null
    if ($LASTEXITCODE -ne 0) { throw "sample thất bại: $LASTEXITCODE" }

    & dotnet run --project $project -c Release --no-launch-profile -- `
        review $document --no-llm -o $reviewPath -q *> $null
    if ($LASTEXITCODE -ne 0) { throw "review thất bại: $LASTEXITCODE" }

    # Chỉ là fixture smoke test: gán prediction vào correctedLevel để tạo bundle hoàn tất.
    $review = Get-Content -LiteralPath $reviewPath -Raw | ConvertFrom-Json
    foreach ($row in $review.rows) { $row.correctedLevel = [int]$row.predictedLevel }
    [IO.File]::WriteAllText(
        $reviewPath,
        ($review | ConvertTo-Json -Depth 12),
        [Text.UTF8Encoding]::new($false))

    & (Join-Path $repoRoot 'scripts\add-holdout.ps1') `
        -SourceDirectory $source -HoldoutDirectory $holdout -WhatIf *> $null
    if (@(Get-ChildItem -LiteralPath $holdout -File).Count -ne 0) {
        throw 'WhatIf đã ghi file ngoài dự kiến.'
    }

    & (Join-Path $repoRoot 'scripts\add-holdout.ps1') `
        -SourceDirectory $source -HoldoutDirectory $holdout *> $null
    foreach ($name in @('holdout-moi.docx', 'holdout-moi.key', 'manifest.jsonl')) {
        if (-not (Test-Path -LiteralPath (Join-Path $holdout $name) -PathType Leaf)) {
            throw "Thiếu output: $name"
        }
    }

    $manifest = Join-Path $holdout 'manifest.jsonl'
    $before = @(Get-Content -LiteralPath $manifest).Count
    & (Join-Path $repoRoot 'scripts\add-holdout.ps1') `
        -SourceDirectory $source -HoldoutDirectory $holdout *> $null
    $after = @(Get-Content -LiteralPath $manifest).Count
    if ($before -ne $after) { throw 'Chạy lại đã ghi trùng manifest.' }

    # Key sai stable ID phải làm eval trả exit 2 và tuyệt đối không sinh profile.
    $badKey = Join-Path $holdout 'holdout-moi.key'
    [IO.File]::WriteAllText($badKey, "@body[999]/p[999] 1`n", [Text.UTF8Encoding]::new($false))
    $profile = Join-Path $resolvedTestRoot 'must-not-exist.json'
    & dotnet run --project $project -c Release --no-launch-profile -- `
        eval $holdout --no-llm --calibration-out $profile -q *> $null
    if ($LASTEXITCODE -ne 2) { throw "Key lỗi phải trả exit 2, thực tế $LASTEXITCODE." }
    if (Test-Path -LiteralPath $profile) { throw 'Key lỗi vẫn sinh calibration profile.' }

    Write-Output 'E2E OK: WhatIf sạch; nhập DOCX+key+manifest; chạy lại idempotent; key lỗi bị chặn.'
}
finally {
    if (Test-Path -LiteralPath $resolvedTestRoot) {
        # Target đã được resolve và xác nhận nằm dưới TEMP ở đầu script.
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
