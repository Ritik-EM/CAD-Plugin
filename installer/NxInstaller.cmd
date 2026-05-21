@echo off
REM Installer for the Siemens NX Atlas add-in.
REM
REM Requires:
REM   - NX installed (UGII_BASE_DIR env var must be set)
REM   - UGII_USER_DIR set (defaults to %APPDATA%\Siemens\NX...)
REM
REM Copies the DLL + atlas.men + dependencies into %UGII_USER_DIR%\startup\.
REM NX scans that folder on startup and discovers .dll/.men files.

setlocal

if "%UGII_USER_DIR%"=="" (
    echo *** UGII_USER_DIR is not set. Open the NX command prompt or set it manually. ***
    exit /b 1
)

set TARGET=%UGII_USER_DIR%\startup
set BIN_DIR=%~dp0..\AtlasNxAddin\bin\Release

if not exist "%TARGET%" mkdir "%TARGET%"

echo === Copying to %TARGET% ===
copy /Y "%BIN_DIR%\AtlasNxAddin.dll" "%TARGET%\"
copy /Y "%BIN_DIR%\AtlasCadCore.dll" "%TARGET%\"
copy /Y "%BIN_DIR%\Newtonsoft.Json.dll" "%TARGET%\"
copy /Y "%~dp0..\AtlasNxAddin\atlas.men" "%TARGET%\"

echo === Done. Restart NX. The Atlas menu will appear in the menu bar. ===
endlocal
exit /b 0
