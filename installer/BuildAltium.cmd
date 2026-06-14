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

REM Stop any running watcher FIRST — it locks AtlasAltiumBridge.exe (so the copy below
REM would fail) and holds the single-instance mutex (so a fresh start would just exit).
REM Without this, a rebuild silently keeps running the OLD bridge (e.g. a stale base URL).
echo === Stopping any running watcher ===
taskkill /F /IM AtlasAltiumBridge.exe >nul 2>nul
REM tiny pause so the file lock is released before we copy
ping -n 2 127.0.0.1 >nul 2>nul

echo === Copying bridge EXE + dependencies to %BRIDGE_DIR% ===
copy /Y "%BIN_DIR%\AtlasAltiumBridge.exe" "%BRIDGE_DIR%\"
if errorlevel 1 goto :error
copy /Y "%BIN_DIR%\AtlasCadCore.dll" "%BRIDGE_DIR%\"
if errorlevel 1 goto :error
copy /Y "%BIN_DIR%\Newtonsoft.Json.dll" "%BRIDGE_DIR%\"
if errorlevel 1 goto :error

echo === Copying DelphiScript to %SCRIPT_DIR% ===
copy /Y "%SRC_DIR%\Script\AtlasCheckin.pas" "%SCRIPT_DIR%\"
if errorlevel 1 goto :error
copy /Y "%SRC_DIR%\Script\AtlasAltium.PrjScr" "%SCRIPT_DIR%\"
if errorlevel 1 goto :error

echo === Installing the watcher autostart shortcut ===
REM Create a Startup-folder shortcut that runs the bridge in --watch mode at login, so
REM check-in is one click in Altium (the script signals the always-running watcher).
set STARTUP=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup
powershell -NoProfile -Command ^
  "$s=(New-Object -ComObject WScript.Shell).CreateShortcut('%STARTUP%\Atlas Altium Watcher.lnk');" ^
  "$s.TargetPath='%BRIDGE_DIR%\AtlasAltiumBridge.exe'; $s.Arguments='--watch';" ^
  "$s.WorkingDirectory='%BRIDGE_DIR%'; $s.Description='Atlas Altium check-in watcher'; $s.Save()"
if errorlevel 1 echo *** WARNING: could not create the Startup shortcut (create it manually). ***

echo === Starting the watcher now (a second instance just exits) ===
start "" "%BRIDGE_DIR%\AtlasAltiumBridge.exe" --watch

echo.
echo === Done. ===
echo Bridge installed to: %BRIDGE_DIR%
echo Script installed to: %SCRIPT_DIR%
echo Watcher: started now + set to auto-start at login (Startup shortcut "Atlas Altium Watcher").
echo Next, in Altium Designer (first time only):
echo   1. Preferences ^> Scripting System ^> Global Projects ^> Add  %SCRIPT_DIR%\AtlasAltium.PrjScr
echo      (then bind AtlasCheckin to a toolbar button, or run it via File ^> Run Script).
echo   2. For REQ 2 STEP: add an "Export STEP" output to an OutJob and enable it
echo      (see AtlasAltium\OutJob\HOW_TO_CREATE_OUTJOB.md). REQ 1 works without it.
echo   To stop the watcher: end "AtlasAltiumBridge.exe" in Task Manager.
popd
endlocal
exit /b 0

:error
echo *** Install failed ***
popd
endlocal
exit /b 1
