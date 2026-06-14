@echo off
REM Build + install the Altium Atlas bridge on the current machine.
REM
REM Requires:
REM   - Visual Studio 2022 with .NET Framework 4.8 SDK
REM   - Run from a Visual Studio Developer Command Prompt
REM
REM Two install targets:
REM   - BRIDGE_DIR (C:\Users\Public\AtlasAltium): the bridge EXE + its DLLs, plus the
REM     manifest/result exchange files. Fixed path because DelphiScript can't read env vars.
REM   - SCRIPT_DIR (%LOCALAPPDATA%\Atlas\Altium): the DelphiScript + OutJob template. This is
REM     where Altium's Global Project points, so we keep it stable (no re-registration needed).
REM
REM After running, in Altium Designer (first time only):
REM   - Preferences > Scripting System > Global Projects > Add  %LOCALAPPDATA%\Atlas\Altium\AtlasAltium.PrjScr
REM   - Copy Atlas_Template.OutJob into each project (beside the .PrjPcb) and enable its outputs.

setlocal
pushd %~dp0

set BRIDGE_DIR=C:\Users\Public\AtlasAltium
set SCRIPT_DIR=%LOCALAPPDATA%\Atlas\Altium
set BIN_DIR=%~dp0..\AtlasAltium\AtlasAltiumBridge\bin\Release
set SRC_DIR=%~dp0..\AtlasAltium

echo === Building Altium bridge in Release ===
msbuild ..\AtlasAltium\AtlasAltiumBridge\AtlasAltiumBridge.csproj /p:Configuration=Release /t:Rebuild
if errorlevel 1 goto :error

if not exist "%BRIDGE_DIR%" mkdir "%BRIDGE_DIR%"
if not exist "%SCRIPT_DIR%" mkdir "%SCRIPT_DIR%"

echo === Copying bridge EXE + dependencies to %BRIDGE_DIR% ===
copy /Y "%BIN_DIR%\AtlasAltiumBridge.exe" "%BRIDGE_DIR%\"
if errorlevel 1 goto :error
copy /Y "%BIN_DIR%\AtlasCadCore.dll" "%BRIDGE_DIR%\"
if errorlevel 1 goto :error
copy /Y "%BIN_DIR%\Newtonsoft.Json.dll" "%BRIDGE_DIR%\"
if errorlevel 1 goto :error

echo === Copying DelphiScript + OutJob template to %SCRIPT_DIR% ===
copy /Y "%SRC_DIR%\Script\AtlasCheckin.pas" "%SCRIPT_DIR%\"
if errorlevel 1 goto :error
copy /Y "%SRC_DIR%\Script\AtlasAltium.PrjScr" "%SCRIPT_DIR%\"
if errorlevel 1 goto :error
copy /Y "%SRC_DIR%\OutJob\Atlas_Template.OutJob" "%SCRIPT_DIR%\"

echo.
echo === Done. ===
echo Bridge installed to: %BRIDGE_DIR%
echo Script installed to: %SCRIPT_DIR%
echo Next, in Altium Designer (first time only):
echo   1. Preferences ^> Scripting System ^> Global Projects ^> Add  %SCRIPT_DIR%\AtlasAltium.PrjScr
echo   2. Copy Atlas_Template.OutJob into your project folder and enable its outputs.
popd
endlocal
exit /b 0

:error
echo *** Install failed ***
popd
endlocal
exit /b 1
