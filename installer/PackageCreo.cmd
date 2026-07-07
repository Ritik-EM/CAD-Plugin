@echo off
REM ─────────────────────────────────────────────────────────────────────
REM Build a portable Creo Parametric plugin distribution zip.
REM
REM Output: installer\dist\AtlasCreo.zip
REM
REM On the target machine: unzip, then double-click InstallCreo.cmd.
REM
REM Run this from a Visual Studio Developer Command Prompt on the dev box
REM (needs msbuild + a working Creo VB API setup — see SetupCreoVbApi.ps1).
REM
REM NOTE ON CREO VERSION: the bundled Interop.pfcls.dll is generated from the
REM Creo installed on THIS dev box. It works on a target running a different
REM Creo version ONLY if PTC kept the pfcls COM GUIDs stable across versions.
REM Test on one target machine first (see InstallCreo.cmd). If it fails with
REM REGDB_E_CLASSNOTREG, regenerate the interop on a machine with that Creo
REM version and rebuild before packaging.
REM ─────────────────────────────────────────────────────────────────────

setlocal
pushd %~dp0

set BIN_DIR=%~dp0..\AtlasCreoAddin\bin\Release
set DIST_DIR=%~dp0dist\AtlasCreo
set ZIP_PATH=%~dp0dist\AtlasCreo.zip

echo === Building Creo add-in in Release ===
msbuild ..\AtlasCreoAddin\AtlasCreoAddin.csproj /p:Configuration=Release /t:Rebuild
if errorlevel 1 goto :error

echo === Staging distribution into %DIST_DIR% ===
if exist "%DIST_DIR%" rmdir /S /Q "%DIST_DIR%"
mkdir "%DIST_DIR%"
mkdir "%DIST_DIR%\text"

REM Runtime files the exe needs next to it.
for %%F in (AtlasCreoAddin.exe AtlasCadCore.dll Interop.pfcls.dll Newtonsoft.Json.dll) do (
    if not exist "%BIN_DIR%\%%F" (
        echo Build output missing: %BIN_DIR%\%%F
        goto :error
    )
    copy /Y "%BIN_DIR%\%%F" "%DIST_DIR%\" >nul
    if errorlevel 1 goto :error
)

REM Button label/tooltip message file (must stay under text\).
if not exist "%BIN_DIR%\text\atlas_creo_msg.txt" (
    echo Build output missing: %BIN_DIR%\text\atlas_creo_msg.txt
    goto :error
)
copy /Y "%BIN_DIR%\text\atlas_creo_msg.txt" "%DIST_DIR%\text\" >nul
if errorlevel 1 goto :error

REM Machine-setup script (COM registration) + target-side installer.
copy /Y "%~dp0SetupCreoVbApi.ps1" "%DIST_DIR%\" >nul
if errorlevel 1 goto :error
copy /Y "%~dp0InstallCreo.cmd" "%DIST_DIR%\" >nul
if errorlevel 1 goto :error

REM Short README for the person on the target machine.
> "%DIST_DIR%\README.txt" (
    echo Atlas CAD Plugin for Creo Parametric — portable install package
    echo.
    echo Prerequisites on this machine:
    echo   - Creo Parametric, COMMERCIAL seat, with the VB API feature.
    echo     ^(Will NOT work on the Educational Edition.^)
    echo   - .NET Framework 4.8 ^(Windows 10/11 ship with it^).
    echo.
    echo To install:
    echo   1. Close Creo if it is running.
    echo   2. Double-click InstallCreo.cmd  ^(do NOT "Run as administrator" —
    echo      it will prompt for admin only for the one-time COM registration^).
    echo   3. Wait for the "Done." message.
    echo   4. Start Creo and open an assembly.
    echo   5. A shortcut "Atlas for Creo" was placed on your Desktop and in
    echo      Startup. Launch it AFTER Creo is open ^(an Atlas tray icon appears^).
    echo   6. First time only — put the buttons on the ribbon:
    echo      File ^> Options ^> Customize Ribbon ^> add the 5 "TOOLKIT Commands"
    echo      ^(Upload to Atlas, Browse / Check Out, Check In, Release Part Code,
    echo      Sign Out^) to a new tab ^> Export ^> "Save the Auxiliary Application
    echo      User Interface". Needs config option tk_enable_ribbon_custom_save = yes.
    echo.
    echo To uninstall:
    echo   1. From this folder, run:  SetupCreoVbApi.ps1 -Unregister  ^(as admin^)
    echo   2. Delete "%%LOCALAPPDATA%%\Atlas\Creo".
    echo   3. Delete the "Atlas for Creo" shortcuts from Desktop and Startup.
)

echo === Zipping to %ZIP_PATH% ===
if exist "%ZIP_PATH%" del /Q "%ZIP_PATH%"
powershell -NoProfile -Command "Compress-Archive -Path '%DIST_DIR%\*' -DestinationPath '%ZIP_PATH%' -CompressionLevel Optimal"
if errorlevel 1 goto :error

echo.
echo ─────────────────────────────────────────────────────────────────────
echo Done.
echo   Folder: %DIST_DIR%
echo   Zip:    %ZIP_PATH%
echo Copy the zip to the target machine, unzip, run InstallCreo.cmd.
echo ─────────────────────────────────────────────────────────────────────
popd
endlocal
exit /b 0

:error
echo *** Package build failed ***
popd
endlocal
exit /b 1
