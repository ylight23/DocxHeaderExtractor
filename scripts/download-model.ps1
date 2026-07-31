<#
.SYNOPSIS
    Tải mô hình GGUF về thư mục models.

.EXAMPLE
    .\scripts\download-model.ps1
    .\scripts\download-model.ps1 -Quant Q5_K_M
#>
[CmdletBinding()]
param(
    [ValidateSet('Q3_K_M', 'Q4_K_M', 'Q5_K_M', 'Q6_K', 'Q8_0')]
    [string]$Quant = 'Q4_K_M',

    [string]$Repo = 'bartowski/Llama-3.2-3B-Instruct-GGUF',

    [string]$OutDir = (Join-Path $PSScriptRoot '..\models')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'Continue'

$fileName = "Llama-3.2-3B-Instruct-$Quant.gguf"
$url = "https://huggingface.co/$Repo/resolve/main/$fileName?download=true"
$outDir = (Resolve-Path $OutDir).Path
$target = Join-Path $outDir $fileName

if (Test-Path $target) {
    $sizeGb = [math]::Round((Get-Item $target).Length / 1GB, 2)
    Write-Host "Đã có sẵn: $target ($sizeGb GB)" -ForegroundColor Green
    exit 0
}

Write-Host "Tải $fileName từ $Repo …" -ForegroundColor Cyan
Write-Host "Đích: $target"

$tmp = "$target.part"
try {
    # Ưu tiên curl (hiển thị tiến trình, hỗ trợ resume) rồi mới tới Invoke-WebRequest.
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) {
        & $curl.Source -L --fail --retry 3 --continue-at - -o $tmp $url
        if ($LASTEXITCODE -ne 0) { throw "curl trả về mã $LASTEXITCODE" }
    }
    else {
        Invoke-WebRequest -Uri $url -OutFile $tmp -MaximumRedirection 5
    }

    Move-Item $tmp $target -Force
    $sizeGb = [math]::Round((Get-Item $target).Length / 1GB, 2)
    Write-Host "Xong: $target ($sizeGb GB)" -ForegroundColor Green
    Write-Host "Thử ngay: dhx extract samples\mau.docx -m `"$target`" -f md"
}
catch {
    if (Test-Path $tmp) { Write-Host "Giữ lại file dở dang để chạy lại resume: $tmp" -ForegroundColor Yellow }
    Write-Error "Tải thất bại: $_"
    Write-Host @"

Nếu kho yêu cầu đăng nhập, đặt token rồi chạy lại bằng curl:
  curl -L -H "Authorization: Bearer <HF_TOKEN>" -o "$target" "$url"
"@
    exit 1
}
