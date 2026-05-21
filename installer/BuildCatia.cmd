@echo off
REM Build + install the CATIA V5 Atlas add-in on the current machine.
REM
REM Requires:
REM   - CATIA V5 installed
REM   - Visual Studio 2022 with .NET Framework 4.8 SDK
REM   - Run from a Visual Studio Developer Command Prompt AS ADMINISTRATOR
REM     (regasm needs HKLM write access)
REM
REM Steps:
REM   1. Build AtlasCatiaAddin in Release
REM   2. Copy DLLs to %ProgramFiles%\Atlas\Catia\
REM   3. regasm /codebase so CATIA's CreateObject can resolve the COM class
REM   4. Copy Atlas.CATScript to the user's CATIA CATStartup folder so the
REM      Atlas macro auto-runs on the next CATIA launch.

setlocal
pushd %~dp0

set INSTALL_DIR=%ProgramFiles%\Atlas\Catia
set CATIA_STARTUP=%APPDATA%\DassaultSystemes\CATEnv\CATStartup
set BIN_DIR=%~dp0..\AtlasCatiaAddin\bin\Release

echo === Building CATIA add-in in Release ===
msbuild ..\AtlasCatiaAddin\AtlasCatiaAddin.csproj /p:Configuration=Release /t:Rebuild
if errorlevel 1 goto :error

if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"
if not exist "%CATIA_STARTUP%" mkdir "%CATIA_STARTUP%"

echo === Copying DLLs to %INSTALL_DIR% ===
copy /Y "%BIN_DIR%\AtlasCatiaAddin.dll" "%INSTALL_DIR%\"
if errorlevel 1 goto :error
copy /Y "%BIN_DIR%\AtlasCadCore.dll" "%INSTALL_DIR%\"
if errorlevel 1 goto :error
copy /Y "%BIN_DIR%\Newtonsoft.Json.dll" "%INSTALL_DIR%\"
if errorlevel 1 goto :error

echo === Registering COM (regasm /codebase) ===
regasm /codebase "%INSTALL_DIR%\AtlasCatiaAddin.dll"
if errorlevel 1 goto :error

echo === Copying macro ===
copy /Y "%~dp0..\AtlasCatiaAddin\Atlas.CATScript" "%CATIA_STARTUP%\"
if errorlevel 1 goto :error

echo === Done. Restart CATIA. The Atlas macro will auto-run on startup. ===
popd
endlocal
exit /b 0

:error
echo *** Install failed ***
popd
endlocal
exit /b 1
