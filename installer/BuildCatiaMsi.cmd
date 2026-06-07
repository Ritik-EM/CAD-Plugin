@echo off
REM ─────────────────────────────────────────────────────────────────────
REM Build the CATIA plugin MSI (installer\dist\AtlasCatiaPlugin.msi).
REM
REM Counterpart to PackageCatia.cmd (which builds the portable zip). This
REM produces a proper MSI so auto-update can download + install it, exactly
REM like the SolidWorks MSI.
REM
REM Run from a Visual Studio Developer Command Prompt (needs msbuild) with
REM the WiX v3.11 toolset on PATH (candle.exe / light.exe), on the dev box
REM with the CATIA Interop DLLs (C:\CATIA-RefAssemblies or auto-detected).
REM
REM   cd installer
REM   BuildCatiaMsi.cmd
REM ─────────────────────────────────────────────────────────────────────

setlocal
pushd %~dp0

set BIN_DIR=%~dp0..\AtlasCatiaAddin\bin\Release
set DIST_DIR=%~dp0dist
set MSI_PATH=%DIST_DIR%\AtlasCatiaPlugin.msi

REM ── Locate the CATIA interop assemblies (same auto-detect as PackageCatia) ──
if not defined CATIA_CODE_BIN (
    for /D %%D in ("%ProgramFiles%\Dassault Systemes\B*") do (
        if not defined CATIA_CODE_BIN if exist "%%~fD\win_b64\code\bin\INFITF.dll" set "CATIA_CODE_BIN=%%~fD\win_b64\code\bin"
        if not defined CATIA_CODE_BIN if exist "%%~fD\intel_a\code\bin\INFITF.dll" set "CATIA_CODE_BIN=%%~fD\intel_a\code\bin"
    )
    for /D %%D in ("%ProgramFiles(x86)%\Dassault Systemes\B*") do (
        if not defined CATIA_CODE_BIN if exist "%%~fD\win_b64\code\bin\INFITF.dll" set "CATIA_CODE_BIN=%%~fD\win_b64\code\bin"
        if not defined CATIA_CODE_BIN if exist "%%~fD\intel_a\code\bin\INFITF.dll" set "CATIA_CODE_BIN=%%~fD\intel_a\code\bin"
    )
    if not defined CATIA_CODE_BIN if exist "C:\CATIA-RefAssemblies\INFITF.dll" set "CATIA_CODE_BIN=C:\CATIA-RefAssemblies"
)
if not defined CATIA_CODE_BIN (
    echo Could not find CATIA interop assemblies.
    echo Set CATIA_CODE_BIN to a folder containing INFITF.dll etc.
    goto :error
)
echo Using CATIA_CODE_BIN=%CATIA_CODE_BIN%

REM ── Verify WiX is available ──
where candle >nul 2>nul || (echo candle.exe not found — install WiX v3.11 and add it to PATH. & goto :error)
where light  >nul 2>nul || (echo light.exe not found — install WiX v3.11 and add it to PATH.  & goto :error)

REM ── 1) Build the add-in in Release (Rebuild AtlasCadCore FIRST so a stale
REM        shared core never ships — same gotcha BuildCatia.cmd guards against). ──
echo === Building AtlasCadCore (Rebuild) ===
msbuild ..\AtlasCadCore\AtlasCadCore.csproj /p:Configuration=Release /t:Rebuild
if errorlevel 1 goto :error

echo === Building CATIA add-in (Rebuild) ===
msbuild ..\AtlasCatiaAddin\AtlasCatiaAddin.csproj /p:Configuration=Release "/p:CATIA_CODE_BIN=%CATIA_CODE_BIN%" /t:Rebuild
if errorlevel 1 goto :error

REM ── Sanity: every file CatiaProduct.wxs references must exist ──
for %%F in (AtlasCatiaAddin.dll AtlasCadCore.dll Newtonsoft.Json.dll INFITF.dll MECMOD.dll ProductStructureTypeLib.dll KnowledgewareTypeLib.dll) do (
    if not exist "%BIN_DIR%\%%F" (
        echo Build output missing: %BIN_DIR%\%%F
        goto :error
    )
)

REM ── 2) Compile the MSI (candle -> light). WixUtilExtension is required for
REM        util:CloseApplication and the WixCA/WixQuietExec64 regasm action;
REM        WixUIExtension for WixUI_Minimal. ──
if not exist "%DIST_DIR%" mkdir "%DIST_DIR%"
echo === candle ===
candle -ext WixUtilExtension -out "%DIST_DIR%\CatiaProduct.wixobj" CatiaProduct.wxs
if errorlevel 1 goto :error

echo === light ===
light -ext WixUtilExtension -ext WixUIExtension "%DIST_DIR%\CatiaProduct.wixobj" -out "%MSI_PATH%"
if errorlevel 1 goto :error

echo.
echo ─────────────────────────────────────────────────────────────────────
echo Done.  MSI: %MSI_PATH%
echo.
echo Next:
echo   - (optional) code-sign:
echo       signtool sign /a /tr http://timestamp.digicert.com /td sha256 /fd sha256 "%MSI_PATH%"
echo   - upload to S3 so auto-update serves it to CATIA:
echo       aws s3 cp "%MSI_PATH%" s3://atlas-app-docs/cad/installers/AtlasCatiaPlugin.msi
echo ─────────────────────────────────────────────────────────────────────
popd
endlocal
exit /b 0

:error
echo *** CATIA MSI build failed ***
popd
endlocal
exit /b 1
