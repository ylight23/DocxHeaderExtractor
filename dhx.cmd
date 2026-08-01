@echo off
rem Wrapper: goi dhx tu bat ky thu muc nao.
rem Thu tu uu tien: ban GPU da publish (out-vulkan / out-cuda) roi moi den Release, cuoi cung Debug.
rem Luu y: ban GPU chi tang toc khi truyen --gpu-layers 99; khong co co do thi van chay CPU.
rem Chu y: file .cmd chi dung ASCII de tranh loi codepage cua cmd.exe.
setlocal
set "DHX_ROOT=%~dp0"
set "DHX_EXE=%DHX_ROOT%out-vulkan\dhx.exe"
if not exist "%DHX_EXE%" set "DHX_EXE=%DHX_ROOT%out-cuda\dhx.exe"
if not exist "%DHX_EXE%" set "DHX_EXE=%DHX_ROOT%src\DocxHeaderExtractor.Cli\bin\Release\net9.0\dhx.exe"
if not exist "%DHX_EXE%" set "DHX_EXE=%DHX_ROOT%src\DocxHeaderExtractor.Cli\bin\Debug\net9.0\dhx.exe"
if not exist "%DHX_EXE%" (
    echo Chua build. Chay: dotnet build "%DHX_ROOT%DocxHeaderExtractor.sln" -c Release
    exit /b 2
)
"%DHX_EXE%" %*
