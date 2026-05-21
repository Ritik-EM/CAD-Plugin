@echo off
REM Installer for the CATIA V5 Atlas add-in. Run from a Visual Studio
REM Developer Command Prompt as Administrator (regasm needs HKLM write).
REM
REM Steps:
REM   1. Copy AtlasCatiaAddin.dll + AtlasCadCore.dll + Newtonsoft.Json.dll
REM      to %ProgramFiles%\Atlas\Catia\
REM   2. regasm /codebase the DLL so CATIA's CreateObject can resolve it
REM   3. Copy Atlas.CATScript to the user's CATIA CATStartup folder

setlocal
set INSTALL_DIR=%ProgramFiles%\Atlas\Catia
set CATIA_STARTUP=%APPDATA%\DassaultSystemes\CATEnv\CATStartup
set BIN_DIR=%~dp0..\AtlasCatiaAddin\bin\Release

if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"
if not exist "%CATIA_STARTUP%" mkdir "%CATIA_STARTUP%"

echo === Copying DLLs to %INSTALL_DIR% ===
copy /Y "%BIN_DIR%\AtlasCatiaAddin.dll" "%INSTALL_DIR%\"
copy /Y "%BIN_DIR%\AtlasCadCore.dll" "%INSTALL_DIR%\"
copy /Y "%BIN_DIR%\Newtonsoft.Json.dll" "%INSTALL_DIR%\"

echo === Registering COM ===
regasm /codebase "%INSTALL_DIR%\AtlasCatiaAddin.dll"
if errorlevel 1 goto :error

echo === Copying macro ===
copy /Y "%~dp0..\AtlasCatiaAddin\Atlas.CATScript" "%CATIA_STARTUP%\"

echo === Done. Restart CATIA. The Atlas macro will auto-run on startup. ===
endlocal
exit /b 0

:error
echo *** Install failed ***
endlocal
exit /b 1
