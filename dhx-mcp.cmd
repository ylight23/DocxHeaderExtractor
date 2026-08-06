@echo off
setlocal
set "DHX_MCP_REPO=%~dp0"
set "DHX_MCP_DLL=%DHX_MCP_REPO%out-mcp\dhx-mcp.dll"

if not defined DHX_MCP_ALLOWED_ROOTS set "DHX_MCP_ALLOWED_ROOTS=%DHX_MCP_REPO%"

if not exist "%DHX_MCP_DLL%" (
  1>&2 echo Chua co ban MCP publish. Chay: dotnet publish src\DocxHeaderExtractor.Mcp -c Release -o out-mcp
  exit /b 2
)

dotnet "%DHX_MCP_DLL%"
