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

if not defined CATIA_CODE_BIN (
    REM Search both 64-bit (Program Files) and 32-bit (Program Files (x86))
    REM install roots. Older CATIA releases (V5R21 and earlier) are 32-bit
    REM and land in the (x86) tree. Within each, win_b64 / intel_a are the
    REM two arch subdirs CATIA uses across versions.
    for /D %%D in ("%ProgramFiles%\Dassault Systemes\B*") do (
        if not defined CATIA_CODE_BIN if exist "%%~fD\win_b64\code\bin\INFITF.dll" set "CATIA_CODE_BIN=%%~fD\win_b64\code\bin"
        if not defined CATIA_CODE_BIN if exist "%%~fD\intel_a\code\bin\INFITF.dll" set "CATIA_CODE_BIN=%%~fD\intel_a\code\bin"
    )
    for /D %%D in ("%ProgramFiles(x86)%\Dassault Systemes\B*") do (
        if not defined CATIA_CODE_BIN if exist "%%~fD\win_b64\code\bin\INFITF.dll" set "CATIA_CODE_BIN=%%~fD\win_b64\code\bin"
        if not defined CATIA_CODE_BIN if exist "%%~fD\intel_a\code\bin\INFITF.dll" set "CATIA_CODE_BIN=%%~fD\intel_a\code\bin"
    )
)

if not defined CATIA_CODE_BIN (
    echo Could not find CATIA interop assemblies.
    echo Set CATIA_CODE_BIN to your CATIA code\bin folder, for example:
    echo set CATIA_CODE_BIN=C:\Program Files\Dassault Systemes\B30\win_b64\code\bin
    goto :error
)

echo Using CATIA_CODE_BIN=%CATIA_CODE_BIN%

REM Force-rebuild the SHARED core first. /t:Rebuild on the addin project only
REM CLEANS the addin — AtlasCadCore is pulled in as an incremental project
REM reference, and git checkout/pull timestamps routinely fool MSBuild into
REM thinking the old AtlasCadCore.dll is still up-to-date, so it ships a STALE
REM core (addin fixes appear, core fixes like CheckinFlow don't). Rebuilding
REM the core explicitly guarantees a fresh AtlasCadCore.dll every time.
echo === Rebuilding AtlasCadCore (shared core, forced) ===
msbuild ..\AtlasCadCore\AtlasCadCore.csproj /p:Configuration=Release /t:Rebuild
if errorlevel 1 goto :error

echo === Building CATIA add-in in Release ===
msbuild ..\AtlasCatiaAddin\AtlasCatiaAddin.csproj /p:Configuration=Release "/p:CATIA_CODE_BIN=%CATIA_CODE_BIN%" /t:Rebuild
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
REM CATIA Interop assemblies must sit alongside AtlasCatiaAddin.dll so
REM regasm can load them at COM-registration time, and so CATIA's runtime
REM can resolve them when the addin actually loads. Without these, regasm
REM fails with "Could not load file or assembly 'INFITF...'".
copy /Y "%BIN_DIR%\INFITF.dll" "%INSTALL_DIR%\"
if errorlevel 1 goto :error
copy /Y "%BIN_DIR%\MECMOD.dll" "%INSTALL_DIR%\"
if errorlevel 1 goto :error
copy /Y "%BIN_DIR%\ProductStructureTypeLib.dll" "%INSTALL_DIR%\"
if errorlevel 1 goto :error
copy /Y "%BIN_DIR%\KnowledgewareTypeLib.dll" "%INSTALL_DIR%\"
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
