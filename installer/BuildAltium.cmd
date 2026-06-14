@echo off
REM Build + install the Altium Atlas bridge on the current machine.
REM
REM Requires:
REM   - Visual Studio 2022 with .NET Framework 4.8 SDK
REM   - Run from a Visual Studio Developer Command Prompt
REM
REM Steps:
REM   1. Build AtlasAltiumBridge in Release (pulls in AtlasCadCore + Newtonsoft.Json).
REM   2. Copy the EXE + its dependencies to %LOCALAPPDATA%\Atlas\Altium\
REM      (AtlasCheckin.pas launches it from there).
REM   3. Copy the DelphiScript + template OutJob next to it for manual install into Altium.
REM
REM After running, in Altium Designer:
REM   - Preferences > Scripting System > Global Projects > Add  AtlasAltium.PrjScr
REM   - DXP > Customize  -> bind the AtlasCheckin procedure to a menu/toolbar button.
REM   - Copy Atlas_Template.OutJob into each project (beside the .PrjPcb) and enable its outputs.

setlocal
pushd %~dp0

set INSTALL_DIR=%LOCALAPPDATA%\Atlas\Altium
set BIN_DIR=%~dp0..\AtlasAltium\AtlasAltiumBridge\bin\Release
set SRC_DIR=%~dp0..\AtlasAltium

echo === Building Altium bridge in Release ===
msbuild ..\AtlasAltium\AtlasAltiumBridge\AtlasAltiumBridge.csproj /p:Configuration=Release /t:Rebuild
if errorlevel 1 goto :error

if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"

echo === Copying bridge EXE + dependencies to %INSTALL_DIR% ===
copy /Y "%BIN_DIR%\AtlasAltiumBridge.exe" "%INSTALL_DIR%\"
if errorlevel 1 goto :error
copy /Y "%BIN_DIR%\AtlasCadCore.dll" "%INSTALL_DIR%\"
if errorlevel 1 goto :error
copy /Y "%BIN_DIR%\Newtonsoft.Json.dll" "%INSTALL_DIR%\"
if errorlevel 1 goto :error

echo === Copying DelphiScript + OutJob template ===
copy /Y "%SRC_DIR%\Script\AtlasCheckin.pas" "%INSTALL_DIR%\"
copy /Y "%SRC_DIR%\Script\AtlasAltium.PrjScr" "%INSTALL_DIR%\"
copy /Y "%SRC_DIR%\OutJob\Atlas_Template.OutJob" "%INSTALL_DIR%\"

echo.
echo === Done. ===
echo Bridge installed to: %INSTALL_DIR%
echo Next, in Altium Designer:
echo   1. Preferences ^> Scripting System ^> Global Projects ^> Add  %INSTALL_DIR%\AtlasAltium.PrjScr
echo   2. DXP ^> Customize  -^> bind AtlasCheckin to a menu/toolbar button.
echo   3. Copy Atlas_Template.OutJob into your project folder and enable its outputs.
popd
endlocal
exit /b 0

:error
echo *** Install failed ***
popd
endlocal
exit /b 1
