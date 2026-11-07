@echo off
rem ============================================================
rem  Build ImageShareSetup (net472 / WPF)
rem  Usage: double-click, or run from a terminal.
rem  Output: bin\Debug\ImageShareSetup.exe
rem ============================================================
setlocal
cd /d "%~dp0"

call :find_msbuild
if not defined MSBUILD (
    echo [ERROR] MSBuild not found.
    echo Install Visual Studio with the ".NET desktop development" workload,
    echo or set MSBUILD environment variable to the full MSBuild.exe path.
    pause
    exit /b 1
)

echo MSBuild: %MSBUILD%
echo.

"%MSBUILD%" "ImageShareSetup.csproj" /p:Configuration=Debug /p:Platform=AnyCPU /t:Restore /nologo /v:m
if errorlevel 1 goto :fail

"%MSBUILD%" "ImageShareSetup.csproj" /p:Configuration=Debug /p:Platform=AnyCPU /t:Build /nologo /v:m
if errorlevel 1 goto :fail

echo.
echo Build OK: bin\Debug\ImageShareSetup.exe
pause
exit /b 0

:fail
echo.
echo Build FAILED. Close any running ImageShareSetup.exe and try again.
pause
exit /b 1

rem ------------------------------------------------------------
rem Locate MSBuild: explicit env var > vswhere > common paths
rem ------------------------------------------------------------
:find_msbuild
if defined MSBUILD if exist "%MSBUILD%" goto :eof
set "MSBUILD="

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" (
    for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe 2^>nul`) do set "MSBUILD=%%i"
)
if defined MSBUILD goto :eof

for %%p in (
    "%ProgramFiles%\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
    "%ProgramFiles%\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"
    "%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
    "D:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
    "C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
) do (
    if exist "%%~p" if not defined MSBUILD set "MSBUILD=%%~p"
)
goto :eof
