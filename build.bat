@echo off
chcp 65001 > nul
echo ===================================================
echo   Building and Syncing TiaPortal18Agent v2.4.0
echo ===================================================

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set REF="C:\Program Files\Siemens\Automation\Portal V18\PublicAPI\V18\Siemens.Engineering.dll"
set SRC=%~dp0TiaPortal18Agent.cs
set OUT=%~dp0TiaPortal18Agent.exe
set DESKTOP_DIR=C:\Users\aa.fedin\Desktop\Tia_18_Agent

echo 1. Compiling C# source code...
%CSC% /nologo /t:exe /codepage:65001 /utf8output /out:%OUT% /r:%REF% %SRC%
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Compilation failed!
    exit /b %ERRORLEVEL%
)

echo 2. Registering Openness whitelist for Favorites folder...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0register_whitelist.ps1" -ExePath "%OUT%"

echo 3. Syncing project to Desktop folder (%DESKTOP_DIR%)...
if not exist "%DESKTOP_DIR%" mkdir "%DESKTOP_DIR%"
copy /Y "%~dp0TiaPortal18Agent.cs" "%DESKTOP_DIR%\" > nul
powershell -NoProfile -Command "try { if (Test-Path '%DESKTOP_DIR%\TiaPortal18Agent.old.exe') { Remove-Item '%DESKTOP_DIR%\TiaPortal18Agent.old.exe' -Force -ErrorAction SilentlyContinue }; Move-Item '%DESKTOP_DIR%\TiaPortal18Agent.exe' '%DESKTOP_DIR%\TiaPortal18Agent.old.exe' -Force -ErrorAction SilentlyContinue } catch {}; Copy-Item '%OUT%' '%DESKTOP_DIR%\TiaPortal18Agent.exe' -Force"
copy /Y "%~dp0TiaPortal18Agent.exe.config" "%DESKTOP_DIR%\" > nul
copy /Y "%~dp0build.bat" "%DESKTOP_DIR%\" > nul
copy /Y "%~dp0register_whitelist.ps1" "%DESKTOP_DIR%\" > nul
if exist "%~dp0agent_settings.json" copy /Y "%~dp0agent_settings.json" "%DESKTOP_DIR%\" > nul
if exist "%~dp0call_tree.json" copy /Y "%~dp0call_tree.json" "%DESKTOP_DIR%\" > nul
if exist "%~dp0README.md" copy /Y "%~dp0README.md" "%DESKTOP_DIR%\" > nul
if exist "%~dp0tia.ps1" copy /Y "%~dp0tia.ps1" "%DESKTOP_DIR%\" > nul
if exist "%~dp0crash_history.log" copy /Y "%~dp0crash_history.log" "%DESKTOP_DIR%\" > nul
if exist "%~dp0.agents" xcopy /E /I /Y "%~dp0.agents" "%DESKTOP_DIR%\.agents" > nul

echo 4. Registering Openness whitelist for Desktop folder...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0register_whitelist.ps1" -ExePath "%DESKTOP_DIR%\TiaPortal18Agent.exe"

echo ===================================================
echo   Success! TiaPortal18Agent compiled and mirrored!
echo ===================================================
