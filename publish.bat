@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
set "PS_SCRIPT=%SCRIPT_DIR%publish.ps1"

if not exist "%PS_SCRIPT%" (
    echo ERROR: publish.ps1 not found
    pause
    exit /b 1
)

REM Detect pwsh
set "PWSH=powershell -NoProfile"
where pwsh >nul 2>nul && set "PWSH=pwsh -NoProfile"

REM Get current version
for /f "delims=" %%i in ('%PWSH% -ExecutionPolicy Bypass -File "%PS_SCRIPT%" --current') do set "CURVER=%%i"

echo  RSTC Updater - Publish Tool
echo  Current version: v%CURVER%
echo.

set /p VER="New version [Enter = auto v%CURVER% + 0.1]: "
set /p MSG="Message   : "

if "%VER%"=="" (
    echo.
    echo Auto-incrementing...
    %PWSH% -ExecutionPolicy Bypass -File "%PS_SCRIPT%" -Minor -Message "%MSG%"
) else (
    %PWSH% -ExecutionPolicy Bypass -File "%PS_SCRIPT%" %VER% -Message "%MSG%"
)

pause
