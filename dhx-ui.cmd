@echo off
rem Development entry point: always build from source, then rebuild/restart on changes.
rem Full restarts keep verifier contracts, referenced projects and wwwroot in sync.
rem Keep this file ASCII for cmd.exe codepage compatibility.
setlocal
set "DHX_ROOT=%~dp0"
pushd "%DHX_ROOT%"
echo Building current source; saving code triggers a full rebuild/restart.
echo Stop any older backend on the same port first. Ctrl+C stops this watcher.
dotnet watch --project "%DHX_ROOT%src\DocxHeaderExtractor.Web\DocxHeaderExtractor.Web.csproj" --no-hot-reload run -c Release --no-launch-profile -- %*
set "DHX_EXIT=%ERRORLEVEL%"
popd
exit /b %DHX_EXIT%
