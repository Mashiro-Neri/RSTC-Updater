@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
set "PS_SCRIPT=%SCRIPT_DIR%update_modpack.ps1"

if not exist "%PS_SCRIPT%" (
    powershell -NoProfile -Command "Write-Host 'ERROR: update_modpack.ps1 not found' -ForegroundColor Red; Read-Host 'Press Enter to exit'"
    exit /b 1
)

where pwsh >nul 2>nul
if %errorlevel%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%PS_SCRIPT%" %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%PS_SCRIPT%" %*
)
