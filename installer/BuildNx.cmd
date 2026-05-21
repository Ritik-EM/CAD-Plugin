@echo off
REM Build + install the Siemens NX Atlas add-in on the current machine.
REM
REM Requires:
REM   - NX installed
REM   - UGII_USER_DIR environment variable set (NX command prompt sets this,
REM     or set it manually to e.g. %APPDATA%\Siemens\NX2306\UGII)
REM   - Visual Studio 2022 with .NET Framework 4.8 SDK
REM   - Run from a Visual Studio Developer Command Prompt
REM
REM Steps:
REM   1. Build AtlasNxAddin in Release
REM   2. Copy DLLs + atlas.men to %UGII_USER_DIR%\startup\
REM      NX scans that folder on launch and auto-loads the menu + DLL.

setlocal
pushd %~dp0

if "%UGII_USER_DIR%"=="" (
    echo *** UGII_USER_DIR is not set. Open the NX command prompt or set it manually. ***
    echo     Example:  set UGII_USER_DIR=%%APPDATA%%\Siemens\NX2306\UGII
    popd
    endlocal
    exit /b 1
)

set TARGET=%UGII_USER_DIR%\startup
set BIN_DIR=%~dp0..\AtlasNxAddin\bin\Release

echo === Building NX add-in in Release ===
msbuild ..\AtlasNxAddin\AtlasNxAddin.csproj /p:Configuration=Release /t:Rebuild
if errorlevel 1 goto :error

if not exist "%TARGET%" mkdir "%TARGET%"

echo === Copying to %TARGET% ===
copy /Y "%BIN_DIR%\AtlasNxAddin.dll" "%TARGET%\"
if errorlevel 1 goto :error
copy /Y "%BIN_DIR%\AtlasCadCore.dll" "%TARGET%\"
if errorlevel 1 goto :error
copy /Y "%BIN_DIR%\Newtonsoft.Json.dll" "%TARGET%\"
if errorlevel 1 goto :error
copy /Y "%~dp0..\AtlasNxAddin\atlas.men" "%TARGET%\"
if errorlevel 1 goto :error

echo === Done. Restart NX. The Atlas menu will appear in the menu bar. ===
popd
endlocal
exit /b 0

:error
echo *** Install failed ***
popd
endlocal
exit /b 1
