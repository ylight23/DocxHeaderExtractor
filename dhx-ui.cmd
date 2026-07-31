@echo off
setlocal
set "DHX_ROOT=%~dp0"
set "DHX_UI_DLL=%DHX_ROOT%src\DocxHeaderExtractor.Web\bin\Release\net9.0\dhx-ui.dll"

if not exist "%DHX_UI_DLL%" (
  echo Chua build. Dang build ban Release...
  dotnet build "%DHX_ROOT%DocxHeaderExtractor.sln" -c Release --nologo -v q || exit /b 1
)

rem Chay tu goc repo de ModelCatalog tim thay thu muc models\
pushd "%DHX_ROOT%"
start "" http://localhost:5099
dotnet "%DHX_UI_DLL%" %*
popd
