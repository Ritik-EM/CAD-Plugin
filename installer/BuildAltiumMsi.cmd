@echo off
REM ─────────────────────────────────────────────────────────────────────
REM Build the Altium plugin MSI (installer\dist\AtlasAltiumPlugin.msi).
REM
REM Run from a Visual Studio Developer Command Prompt (needs msbuild) with the
REM WiX v3.11 toolset on PATH (candle.exe / light.exe). No CATIA/SW interop
REM needed — the Altium bridge only references AtlasCadCore + Newtonsoft.Json.
REM
REM   cd installer
REM   BuildAltiumMsi.cmd
REM ─────────────────────────────────────────────────────────────────────

setlocal
pushd %~dp0

set BIN_DIR=%~dp0..\AtlasAltium\AtlasAltiumBridge\bin\Release
set SCRIPT_SRC=%~dp0..\AtlasAltium\Script
set DIST_DIR=%~dp0dist
set MSI_PATH=%DIST_DIR%\AtlasAltiumPlugin.msi

REM ── Verify WiX is available ──
where candle >nul 2>nul || (echo candle.exe not found - install WiX v3.11 and add it to PATH. & goto :error)
where light  >nul 2>nul || (echo light.exe not found - install WiX v3.11 and add it to PATH.  & goto :error)

REM ── 1) Build in Release. Rebuild AtlasCadCore FIRST so a stale shared core
REM        never ships (same gotcha BuildCatia.cmd guards against). ──
echo === Building AtlasCadCore (Rebuild) ===
msbuild ..\AtlasCadCore\AtlasCadCore.csproj /p:Configuration=Release /t:Rebuild
if errorlevel 1 goto :error

echo === Building Altium bridge (Rebuild) ===
msbuild ..\AtlasAltium\AtlasAltiumBridge\AtlasAltiumBridge.csproj /p:Configuration=Release /t:Rebuild
if errorlevel 1 goto :error

REM ── Sanity: every file AltiumProduct.wxs references must exist ──
for %%F in (AtlasAltiumBridge.exe AtlasCadCore.dll Newtonsoft.Json.dll) do (
    if not exist "%BIN_DIR%\%%F" (
        echo Build output missing: %BIN_DIR%\%%F
        goto :error
    )
)
for %%F in (AtlasCheckin.pas AtlasAltium.PrjScr) do (
    if not exist "%SCRIPT_SRC%\%%F" (
        echo Script source missing: %SCRIPT_SRC%\%%F
        goto :error
    )
)

REM ── 2) Compile the MSI (candle -> light). WixUtilExtension for
REM        util:CloseApplication; WixUIExtension for WixUI_Minimal + License.rtf. ──
if not exist "%DIST_DIR%" mkdir "%DIST_DIR%"
echo === candle ===
candle -ext WixUtilExtension -out "%DIST_DIR%\AltiumProduct.wixobj" AltiumProduct.wxs
if errorlevel 1 goto :error

echo === light ===
light -ext WixUtilExtension -ext WixUIExtension "%DIST_DIR%\AltiumProduct.wixobj" -out "%MSI_PATH%"
if errorlevel 1 goto :error

echo.
echo ─────────────────────────────────────────────────────────────────────
echo Done.  MSI: %MSI_PATH%
echo.
echo Hand this .msi to other users (double-click to install). It:
echo   - installs the bridge to C:\Users\Public\AtlasAltium
echo   - installs the script to %%LOCALAPPDATA%%\Atlas\Altium
echo   - adds a Startup shortcut for the watcher (and offers to start it now)
echo.
echo Each user still does ONE manual step in Altium (no script install hook):
echo   Preferences ^> Scripting System ^> Global Projects ^> Add
echo     %%LOCALAPPDATA%%\Atlas\Altium\AtlasAltium.PrjScr
echo   then bind AtlasCheckin to a toolbar button.
echo.
echo Optional:
echo   - code-sign:  signtool sign /a /tr http://timestamp.digicert.com /td sha256 /fd sha256 "%MSI_PATH%"
echo ─────────────────────────────────────────────────────────────────────
popd
endlocal
exit /b 0

:error
echo *** Altium MSI build failed ***
popd
endlocal
exit /b 1
