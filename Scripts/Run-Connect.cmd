@echo off
setlocal
cd /d "%~dp0"
title Image Share Setup - Source PC Connect Test
echo ============================================================
echo  image share setup connect test (source PC)
echo  No administrator required.
echo ============================================================
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Connect-ImageShare.ps1" %*
set "RC=%ERRORLEVEL%"
echo.
echo Exit code: %RC%
echo.
pause
exit /b %RC%
