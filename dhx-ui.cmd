@echo off
rem Luon cap nhat ban Vulkan truoc khi chay de wwwroot va backend khong bi lech phien ban.
rem Giao dien tu do backend; mac dinh 20 lop cho GPU 4 GB. Chi dat 99 khi VRAM du.
rem Chu y: file .cmd chi dung ASCII de tranh loi codepage cua cmd.exe.
setlocal
set "DHX_ROOT=%~dp0"
set "DHX_UI_DLL=%DHX_ROOT%out-vulkan-ui\dhx-ui.dll"
echo Dang cap nhat dhx-ui Vulkan...
dotnet publish "%DHX_ROOT%src\DocxHeaderExtractor.Web\DocxHeaderExtractor.Web.csproj" -c Release -p:UseVulkan=true -o "%DHX_ROOT%out-vulkan-ui" --nologo -v q || exit /b 1

rem Chay tu goc repo de ModelCatalog tim thay thu muc models\
pushd "%DHX_ROOT%"
start "" http://localhost:5099
dotnet "%DHX_UI_DLL%" %*
popd
