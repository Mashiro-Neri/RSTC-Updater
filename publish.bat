@echo off
setlocal enabledelayedexpansion
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

REM Get local and remote versions
for /f "tokens=1,2 delims=:" %%a in ('%PWSH% -ExecutionPolicy Bypass -File "%PS_SCRIPT%" --remote 2^>nul') do (
    if "%%a"=="LOCAL"  set "LOCALVER=v%%b"
    if "%%a"=="REMOTE" set "REMOTEVER=%%b"
)
if "%LOCALVER%"=="v" set "LOCALVER=???"
if "%REMOTEVER%"==""   set "REMOTEVER=N/A"
if not "%REMOTEVER%"=="N/A" set "REMOTEVER=v%REMOTEVER%"

echo.
echo  ================================================
echo        RSTC Updater - Publish Tool
echo  ================================================
echo.
echo    Remote (GitHub) : %REMOTEVER%
echo    Local  (current): %LOCALVER%
echo  ------------------------------------------------
echo.

set /p VER="  New version [Enter = auto +Minor]: "
set /p MSG="  Message      : "

echo.
if "%VER%"=="" (
    echo  Auto-incrementing minor version...
    %PWSH% -ExecutionPolicy Bypass -File "%PS_SCRIPT%" -Minor -Message "%MSG%"
) else (
    %PWSH% -ExecutionPolicy Bypass -File "%PS_SCRIPT%" %VER% -Message "%MSG%"
)

pause
