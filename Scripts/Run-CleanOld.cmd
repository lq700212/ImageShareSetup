@echo off
setlocal
cd /d "%~dp0"
title Image Share Setup - Clean Old Images
echo ============================================================
echo  image share cleanup by date folders
echo ============================================================
echo.
set "ROOT="
set /p ROOT=Image root path (e.g. \\192.168.10.50\VisionImages): 
if "%ROOT%"=="" (
    echo [ERROR] Root path is required.
    pause
    exit /b 1
)
set "DAYS="
set /p DAYS=Keep days [30]: 
if "%DAYS%"=="" set "DAYS=30"
set "EXEC="
set /p EXEC=Delete for real? (y/N): 
set "EXECARG="
if /i "%EXEC%"=="y" set "EXECARG=-Execute"
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Clear-OldImages.ps1" -Root "%ROOT%" -Days %DAYS% %EXECARG%
set "RC=%ERRORLEVEL%"
echo.
echo Exit code: %RC%
echo.
pause
exit /b %RC%
