@echo off
chcp 65001 > nul
echo ===================================================
echo   Building TiaPortalAgent v2.6.0
echo ===================================================

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe

:: Dynamic Siemens Openness PublicAPI discovery
set REF=
if exist "C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\Siemens.Engineering.dll" (
    set "REF=C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\Siemens.Engineering.dll"
) else if exist "C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V20\Siemens.Engineering.dll" (
    set "REF=C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V20\Siemens.Engineering.dll"
) else if exist "C:\Program Files\Siemens\Automation\Portal V19\PublicAPI\V19\Siemens.Engineering.dll" (
    set "REF=C:\Program Files\Siemens\Automation\Portal V19\PublicAPI\V19\Siemens.Engineering.dll"
) else if exist "C:\Program Files\Siemens\Automation\Portal V18\PublicAPI\V18\Siemens.Engineering.dll" (
    set "REF=C:\Program Files\Siemens\Automation\Portal V18\PublicAPI\V18\Siemens.Engineering.dll"
) else (
    echo [ERROR] Siemens.Engineering.dll not found in standard Portal directories!
    exit /b 1
)
echo Found Openness DLL: "%REF%"

set SRC=%~dp0TiaPortalAgent.cs
set OUT=%~dp0TiaPortalAgent.exe

echo 1. Rotating locked executable if running...
powershell -NoProfile -Command "try { if (Test-Path '%~dp0TiaPortalAgent.old.exe') { Remove-Item '%~dp0TiaPortalAgent.old.exe' -Force -ErrorAction SilentlyContinue }; if (Test-Path '%OUT%') { Move-Item '%OUT%' '%~dp0TiaPortalAgent.old.exe' -Force -ErrorAction SilentlyContinue } } catch {}"

echo 2. Compiling C# source code...
"%CSC%" /nologo /t:exe /platform:x64 /codepage:65001 /utf8output /out:"%OUT%" /r:"%REF%" "%SRC%"
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Compilation failed!
    exit /b %ERRORLEVEL%
)

echo 3. Registering Openness whitelist...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0register_whitelist.ps1" -ExePath "%OUT%"

echo 4. Syncing to Desktop folder if present...
powershell -NoProfile -Command "try { if (Test-Path 'C:\Users\aa.fedin\Desktop\TiaPortalAgent') { Copy-Item '%OUT%' 'C:\Users\aa.fedin\Desktop\TiaPortalAgent\TiaPortalAgent.exe' -Force; if (Test-Path '%~dp0TiaPortalAgent.exe.config') { Copy-Item '%~dp0TiaPortalAgent.exe.config' 'C:\Users\aa.fedin\Desktop\TiaPortalAgent\TiaPortalAgent.exe.config' -Force }; Write-Host 'Synced TiaPortalAgent.exe to Desktop\TiaPortalAgent' } } catch {}"

echo ===================================================
echo   Success! TiaPortalAgent v2.6.0 compiled!
echo ===================================================
