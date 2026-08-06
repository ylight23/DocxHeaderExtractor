param(
    [string]$Output = "out-mcp",
    [string]$Model = "",
    [ValidateRange(4096, 1048576)]
    [int]$ContextSize = 4096
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$outputPath = [System.IO.Path]::GetFullPath((Join-Path $repo $Output))
$project = Join-Path $repo "src\DocxHeaderExtractor.Mcp\DocxHeaderExtractor.Mcp.csproj"

dotnet publish $project -c Release -o $outputPath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$mcpEnv = @{
    DHX_MCP_ALLOWED_ROOTS = $repo
    LMSTUDIO_ENDPOINT = "http://127.0.0.1:1234/v1/chat/completions"
    # MCP host và pipeline cùng gọi một LM Studio instance. Mặc định chỉ cấp 4K cho
    # mỗi request pipeline để còn KV cache cho lượt chat gọi/poll tool song song.
    LMSTUDIO_CONTEXT_SIZE = $ContextSize.ToString([Globalization.CultureInfo]::InvariantCulture)
}
if (-not [string]::IsNullOrWhiteSpace($Model)) {
    $mcpEnv.LMSTUDIO_MODEL = $Model.Trim()
}

$config = @{
    mcpServers = @{
        "docx-header-extractor" = @{
            command = "dotnet"
            args = @(Join-Path $outputPath "dhx-mcp.dll")
            env = $mcpEnv
        }
    }
} | ConvertTo-Json -Depth 6

$snippet = Join-Path $outputPath "lmstudio-mcp.json"
[System.IO.File]::WriteAllText($snippet, $config, [System.Text.UTF8Encoding]::new($false))

Write-Host "MCP đã publish: $outputPath"
Write-Host "Cấu hình LM Studio đã sinh: $snippet"
Write-Host "Mở LM Studio > Program > Install > Edit mcp.json, rồi chép cấu hình này vào."
