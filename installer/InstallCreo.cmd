@echo off
REM ─────────────────────────────────────────────────────────────────────
REM AtlasCadPlugin (Creo Parametric) portable installer.
REM
REM Run this from inside the unzipped AtlasCreo\ folder on the target
REM machine. Just DOUBLE-CLICK it — do NOT "Run as administrator": the app
REM files are installed per-user, and the script elevates on its own only
REM for the one-time COM registration (which writes HKLM).
REM
REM What it does:
REM   1. Copies the app files to %LOCALAPPDATA%\Atlas\Creo (user-writable, so
REM      Creo can save the ribbon layout there later).
REM   2. Creates "Atlas for Creo" shortcuts on the Desktop and in Startup.
REM   3. Runs SetupCreoVbApi.ps1 ELEVATED to register the pfcls COM server for
REM      the Creo installed on THIS machine (auto-detected).
REM
REM Prerequisites:
REM   - Creo Parametric, commercial seat, with the VB API feature.
REM   - .NET Framework 4.8 (ships with Windows 10/11).
REM ─────────────────────────────────────────────────────────────────────

setlocal
pushd %~dp0

set INSTALL_DIR=%LOCALAPPDATA%\Atlas\Creo

REM Per-user install to %LOCALAPPDATA%. Elevated is FINE on a single-user PC:
REM elevation keeps the SAME account, so %LOCALAPPDATA% still points at your own
REM profile. We only warn (not block) in case a DIFFERENT admin account was used.
net session >nul 2>&1
if not errorlevel 1 (
    echo.
    echo NOTE: running as administrator. Installing to THIS account's profile:
    echo   %INSTALL_DIR%
    echo If you used "Run as different user" with ANOTHER admin account, press
    echo Ctrl+C now and re-run as your normal user. Otherwise just continue.
    echo.
    pause
)

echo === Copying app files to %INSTALL_DIR% ===
if not exist "%INSTALL_DIR%\text" mkdir "%INSTALL_DIR%\text"
for %%F in (AtlasCreoAddin.exe AtlasCadCore.dll Interop.pfcls.dll Newtonsoft.Json.dll SetupCreoVbApi.ps1) do (
    if not exist "%~dp0%%F" (
        echo Missing required file: %%F
        echo Did you unzip the FULL distribution?
        goto :error
    )
    copy /Y "%~dp0%%F" "%INSTALL_DIR%\" >nul
    if errorlevel 1 goto :error
)
if not exist "%~dp0text\atlas_creo_msg.txt" (
    echo Missing required file: text\atlas_creo_msg.txt
    goto :error
)
copy /Y "%~dp0text\atlas_creo_msg.txt" "%INSTALL_DIR%\text\" >nul
if errorlevel 1 goto :error
echo   Done.

echo === Creating shortcuts (Desktop + Startup) ===
powershell -NoProfile -Command ^
  "$w = New-Object -ComObject WScript.Shell;" ^
  "foreach ($d in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Startup'))) {" ^
  "  $lnk = $w.CreateShortcut((Join-Path $d 'Atlas for Creo.lnk'));" ^
  "  $lnk.TargetPath = (Join-Path '%INSTALL_DIR%' 'AtlasCreoAddin.exe');" ^
  "  $lnk.WorkingDirectory = '%INSTALL_DIR%';" ^
  "  $lnk.Description = 'Atlas CAD plugin for Creo (launch after Creo is open)';" ^
  "  $lnk.Save() }"
if errorlevel 1 goto :error
echo   Done.

echo === Registering pfcls COM (asks for admin) ===
echo     A UAC prompt will appear; approve it. Watch the elevated window for
echo     "Done." — then close it to continue.
powershell -NoProfile -Command ^
  "Start-Process powershell -Verb RunAs -Wait -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path '%INSTALL_DIR%' 'SetupCreoVbApi.ps1'))"
if errorlevel 1 (
    echo.
    echo COM registration step did not complete. You can re-run it later from an
    echo elevated PowerShell:  "%INSTALL_DIR%\SetupCreoVbApi.ps1"
    goto :error
)

echo.
echo ─────────────────────────────────────────────────────────────────────
echo Done.
echo   Installed to: %INSTALL_DIR%
echo.
echo Next:
echo   1. Start Creo Parametric and open an assembly.
echo   2. Launch "Atlas for Creo" (Desktop shortcut) — an Atlas tray icon appears.
echo   3. First time only: File ^> Options ^> Customize Ribbon ^> add the 5
echo      TOOLKIT Commands to a new tab ^> Export ^> "Save the Auxiliary
echo      Application User Interface".
echo      (Needs config option tk_enable_ribbon_custom_save = yes.)
echo ─────────────────────────────────────────────────────────────────────
popd
endlocal
exit /b 0

:error
echo *** Install failed ***
popd
endlocal
exit /b 1
