[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string] $SourceDirectory,

    [string] $HoldoutDirectory = (Join-Path $PSScriptRoot '..\bench\holdout'),

    [switch] $RunCalibration,

    [string] $CalibrationOutput = (Join-Path $PSScriptRoot '..\bench\precision-calibration.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceRoot = [IO.Path]::GetFullPath($SourceDirectory)
$holdoutRoot = [IO.Path]::GetFullPath($HoldoutDirectory)
$supportedExtensions = @('.docx', '.docm', '.doc', '.rtf', '.odt')

if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
    throw "Không tìm thấy thư mục nguồn: $sourceRoot"
}

if (-not (Test-Path -LiteralPath $holdoutRoot -PathType Container)) {
    New-Item -ItemType Directory -Path $holdoutRoot | Out-Null
}

# Chặn leakage: cùng nội dung DOCX đã nằm ở bất kỳ nhánh bench nào ngoài holdout thì không được
# nhập lại làm holdout dưới tên khác.
$knownDevelopmentHashes = @{}
$benchRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'bench'))
$holdoutPrefix = $holdoutRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (Test-Path -LiteralPath $benchRoot -PathType Container) {
    Get-ChildItem -LiteralPath $benchRoot -Recurse -File | Where-Object {
        $supportedExtensions -contains $_.Extension.ToLowerInvariant() -and
        -not $_.FullName.StartsWith($holdoutPrefix, [StringComparison]::OrdinalIgnoreCase)
    } | ForEach-Object {
        $knownDevelopmentHashes[(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash] = $_.FullName
    }
}

$reviews = @(Get-ChildItem -LiteralPath $sourceRoot -Filter '*.review.json' -File)
if ($reviews.Count -eq 0) {
    throw "Không tìm thấy *.review.json trong $sourceRoot"
}

$added = 0
$skipped = 0
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$manifestPath = Join-Path $holdoutRoot 'manifest.jsonl'

foreach ($reviewFile in $reviews) {
    $review = Get-Content -LiteralPath $reviewFile.FullName -Raw | ConvertFrom-Json
    if ($review.formatVersion -ne 'dhx-review/v1') {
        throw "Sai format trong $($reviewFile.Name): cần dhx-review/v1."
    }
    if ([string]::IsNullOrWhiteSpace([string]$review.sourceFile)) {
        throw "Review thiếu sourceFile: $($reviewFile.Name)"
    }
    if ($null -eq $review.rows -or @($review.rows).Count -eq 0) {
        throw "Review không có paragraph: $($reviewFile.Name)"
    }

    $unreviewed = @($review.rows | Where-Object { $null -eq $_.correctedLevel })
    if ($unreviewed.Count -gt 0) {
        $preview = ($unreviewed | Select-Object -First 6 | ForEach-Object { $_.stableId }) -join ', '
        throw "Review chưa hoàn tất $($reviewFile.Name): còn $($unreviewed.Count) dòng ($preview)."
    }

    $seenStableIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($row in $review.rows) {
        $level = [int]$row.correctedLevel
        if ($level -lt 0 -or $level -gt 9) {
            throw "correctedLevel ngoài 0..9 tại $($row.stableId) trong $($reviewFile.Name)."
        }
        if ([string]::IsNullOrWhiteSpace([string]$row.stableId) -or
            -not $seenStableIds.Add([string]$row.stableId)) {
            throw "stableId rỗng hoặc trùng trong $($reviewFile.Name): $($row.stableId)"
        }
    }

    $sourceName = [IO.Path]::GetFileName([string]$review.sourceFile)
    $documentPath = Join-Path $sourceRoot $sourceName
    if (-not (Test-Path -LiteralPath $documentPath -PathType Leaf)) {
        $reviewStem = $reviewFile.Name.Substring(0, $reviewFile.Name.Length - '.review.json'.Length)
        $matches = @(Get-ChildItem -LiteralPath $sourceRoot -File | Where-Object {
            [IO.Path]::GetFileNameWithoutExtension($_.Name) -eq $reviewStem -and
            $supportedExtensions -contains $_.Extension.ToLowerInvariant()
        })
        if ($matches.Count -ne 1) {
            throw "Không tìm thấy đúng một tài liệu cho $($reviewFile.Name); sourceFile=$sourceName."
        }
        $documentPath = $matches[0].FullName
    }

    $extension = [IO.Path]::GetExtension($documentPath).ToLowerInvariant()
    if ($supportedExtensions -notcontains $extension) {
        throw "Định dạng không hỗ trợ: $documentPath"
    }

    $documentHash = (Get-FileHash -LiteralPath $documentPath -Algorithm SHA256).Hash
    if ($knownDevelopmentHashes.ContainsKey($documentHash)) {
        throw "Leakage: $documentPath trùng nội dung với tập development: $($knownDevelopmentHashes[$documentHash])"
    }

    $stem = [IO.Path]::GetFileNameWithoutExtension($documentPath)
    $targetDocument = Join-Path $holdoutRoot ($stem + $extension)
    $targetKey = Join-Path $holdoutRoot ($stem + '.key')

    if ((Test-Path -LiteralPath $targetDocument) -or (Test-Path -LiteralPath $targetKey)) {
        if ((Test-Path -LiteralPath $targetDocument) -and (Test-Path -LiteralPath $targetKey) -and
            (Get-FileHash -LiteralPath $targetDocument -Algorithm SHA256).Hash -eq $documentHash) {
            Write-Host "Bỏ qua, đã có đúng cặp: $stem"
            $skipped++
            continue
        }
        throw "Đích đã tồn tại nhưng không phải cùng một cặp: $stem"
    }

    $keyLines = [Collections.Generic.List[string]]::new()
    $keyLines.Add("# $stem — nhãn holdout đã duyệt")
    $keyLines.Add('# @<stable-id> <level>; non-heading không ghi vào key')
    foreach ($row in @($review.rows | Sort-Object index)) {
        $level = [int]$row.correctedLevel
        if ($level -le 0) { continue }
        $comment = ([string]$row.text).Replace("`r", ' ').Replace("`n", ' ')
        $keyLines.Add("@$($row.stableId) $level   # $comment")
    }
    $keyText = [string]::Join([Environment]::NewLine, $keyLines) + [Environment]::NewLine

    if ($PSCmdlet.ShouldProcess($stem, "Thêm DOCX + key vào $holdoutRoot")) {
        try {
            Copy-Item -LiteralPath $documentPath -Destination $targetDocument
            [IO.File]::WriteAllText($targetKey, $keyText, $utf8NoBom)
        }
        catch {
            # Chỉ dọn đúng hai đích mới của cặp hiện tại; không đụng file nguồn hay thư mục rộng.
            if (Test-Path -LiteralPath $targetDocument) { Remove-Item -LiteralPath $targetDocument }
            if (Test-Path -LiteralPath $targetKey) { Remove-Item -LiteralPath $targetKey }
            throw
        }

        $manifestItem = [ordered]@{
            addedUtc = [DateTimeOffset]::UtcNow.ToString('O')
            document = [IO.Path]::GetFileName($targetDocument)
            key = [IO.Path]::GetFileName($targetKey)
            documentSha256 = $documentHash
            reviewSha256 = (Get-FileHash -LiteralPath $reviewFile.FullName -Algorithm SHA256).Hash
            reviewedRows = @($review.rows).Count
            headingLabels = @($review.rows | Where-Object { [int]$_.correctedLevel -gt 0 }).Count
        }
        [IO.File]::AppendAllText(
            $manifestPath,
            ($manifestItem | ConvertTo-Json -Compress) + [Environment]::NewLine,
            $utf8NoBom)
        Write-Host "Đã thêm: $([IO.Path]::GetFileName($targetDocument)) + $([IO.Path]::GetFileName($targetKey))"
        $added++
    }
}

Write-Host "Hoàn tất: thêm $added, bỏ qua $skipped, holdout=$holdoutRoot"

if ($RunCalibration -and -not $WhatIfPreference) {
    if ([string]::IsNullOrWhiteSpace($env:OPENROUTER_API_KEY)) {
        throw 'RunCalibration cần OPENROUTER_API_KEY trong tiến trình PowerShell hiện tại.'
    }
    # Chạy source CLI hiện tại; dhx.cmd có thể ưu tiên một out-vulkan cũ chưa biết option mới.
    $cliProject = Join-Path $repoRoot 'src\DocxHeaderExtractor.Cli\DocxHeaderExtractor.Cli.csproj'
    & dotnet run --project $cliProject -c Release --no-launch-profile -- `
        eval $holdoutRoot --openrouter --two-pass --calibration-out $CalibrationOutput
    if ($LASTEXITCODE -notin 0, 1) {
        throw "dhx eval thất bại với exit code $LASTEXITCODE"
    }
    Write-Host "Đã cập nhật calibration profile: $([IO.Path]::GetFullPath($CalibrationOutput))"
}
