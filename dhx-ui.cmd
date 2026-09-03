@echo off
rem Luon cap nhat ban Vulkan truoc khi chay de wwwroot va backend khong bi lech phien ban.
rem Giao dien tu do backend; mac dinh 20 lop cho GPU 4 GB. Chi dat 99 khi VRAM du.
rem Chu y: file .cmd chi dung ASCII de tranh loi codepage cua cmd.exe.
setlocal
set "DHX_ROOT=%~dp0"
set "DHX_UI_DLL=%DHX_ROOT%out-vulkan-ui\dhx-ui.dll"
rem Listen on the local network so a phone on the same private LAN can connect.
set "DHX_UI_URL=http://0.0.0.0:5099"
echo Dang cap nhat dhx-ui Vulkan...
dotnet publish "%DHX_ROOT%src\DocxHeaderExtractor.Web\DocxHeaderExtractor.Web.csproj" -c Release -p:UseVulkan=true -o "%DHX_ROOT%out-vulkan-ui" --nologo -v q || exit /b 1

rem Permit inbound TCP 5099 only on the Windows Private profile.
netsh advfirewall firewall show rule name="DHX UI 5099 LAN" >nul 2>&1
if errorlevel 1 (
    netsh advfirewall firewall add rule name="DHX UI 5099 LAN" dir=in action=allow protocol=TCP localport=5099 profile=Private >nul 2>&1
    if errorlevel 1 echo Firewall rule not added. Run this script as Administrator once.
)

rem Chay tu goc repo de ModelCatalog tim thay thu muc models\
pushd "%DHX_ROOT%"
start "" http://localhost:5099
echo Desktop URL: http://localhost:5099
powershell -NoProfile -Command "$ip = Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' }; foreach ($item in $ip) { Write-Host ('Mobile LAN URL: http://' + $item.IPAddress + ':5099') }"
dotnet "%DHX_UI_DLL%" %*
popd
