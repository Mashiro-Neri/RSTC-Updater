@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
set "PS_SCRIPT=%SCRIPT_DIR%publish.ps1"

if not exist "%PS_SCRIPT%" (
    echo ERROR: publish.ps1 not found
    pause
    exit /b 1
)

echo RSTC Updater - Publish Tool
echo.
set /p VER="Version [e.g. 4.2]: "
set /p MSG="Message: "

if "%VER%"=="" (
    echo Version is required.
    pause
    exit /b 1
)

where pwsh >nul 2>nul
if %errorlevel%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%PS_SCRIPT%" %VER% -Message "%MSG%"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%PS_SCRIPT%" %VER% -Message "%MSG%"
)

pause
