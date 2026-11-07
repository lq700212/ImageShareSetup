@echo off
setlocal
cd /d "%~dp0"

net session >nul 2>&1
if not "%errorlevel%"=="0" (
    echo Requesting administrator privileges...
    powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

title Image Share Setup - Repair After Disk Swap
echo ============================================================
echo  image share repair (after disk replacement)
echo  Running as Administrator.
echo ============================================================
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup-ImageShare.ps1" -Repair %*
set "RC=%ERRORLEVEL%"
echo.
echo Exit code: %RC%
echo.
pause
exit /b %RC%
