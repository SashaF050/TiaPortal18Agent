@echo off
chcp 65001 > nul
echo ===================================================
echo   Building TiaPortal18Agent v2.5.0
echo ===================================================

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set REF="C:\Program Files\Siemens\Automation\Portal V18\PublicAPI\V18\Siemens.Engineering.dll"
set SRC=%~dp0TiaPortal18Agent.cs
set OUT=%~dp0TiaPortal18Agent.exe

echo 1. Rotating locked executable if running...
powershell -NoProfile -Command "try { if (Test-Path '%~dp0TiaPortal18Agent.old.exe') { Remove-Item '%~dp0TiaPortal18Agent.old.exe' -Force -ErrorAction SilentlyContinue }; if (Test-Path '%OUT%') { Move-Item '%OUT%' '%~dp0TiaPortal18Agent.old.exe' -Force -ErrorAction SilentlyContinue } } catch {}"

echo 2. Compiling C# source code...
%CSC% /nologo /t:exe /codepage:65001 /utf8output /out:%OUT% /r:%REF% %SRC%
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Compilation failed!
    exit /b %ERRORLEVEL%
)

echo 3. Registering Openness whitelist...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0register_whitelist.ps1" -ExePath "%OUT%"

echo ===================================================
echo   Success! TiaPortal18Agent v2.5.0 compiled!
echo ===================================================
