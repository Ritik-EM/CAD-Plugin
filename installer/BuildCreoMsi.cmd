@echo off
REM ---------------------------------------------------------------------
REM Build the Creo plugin MSI (installer\dist\AtlasCreoPlugin.msi).
REM
REM Run from a Visual Studio Developer Command Prompt (needs msbuild) with the
REM WiX v3 toolset on PATH (candle.exe / light.exe). The bundled Interop.pfcls.dll
REM must already exist in AtlasCreoAddin\bin\Release (built on a machine with Creo).
REM
REM   cd installer
REM   BuildCreoMsi.cmd
REM ---------------------------------------------------------------------

setlocal
pushd %~dp0

set BIN_DIR=%~dp0..\AtlasCreoAddin\bin\Release
set DIST_DIR=%~dp0dist
set MSI_PATH=%DIST_DIR%\AtlasCreoPlugin.msi

REM -- Verify WiX is available --
where candle >nul 2>nul || (echo candle.exe not found - install WiX v3 and add it to PATH. & goto :error)
where light  >nul 2>nul || (echo light.exe not found - install WiX v3 and add it to PATH.  & goto :error)

REM -- 1) Build Release. Rebuild AtlasCadCore FIRST so a stale shared core never ships. --
echo === Building AtlasCadCore (Rebuild) ===
msbuild ..\AtlasCadCore\AtlasCadCore.csproj /p:Configuration=Release /t:Rebuild
if errorlevel 1 goto :error

echo === Building Creo add-in (Rebuild) ===
msbuild ..\AtlasCreoAddin\AtlasCreoAddin.csproj /p:Configuration=Release /t:Rebuild
if errorlevel 1 goto :error

REM -- Sanity: every file CreoProduct.wxs references must exist --
for %%F in (AtlasCreoAddin.exe AtlasCadCore.dll Interop.pfcls.dll Newtonsoft.Json.dll) do (
    if not exist "%BIN_DIR%\%%F" (
        echo Build output missing: %BIN_DIR%\%%F
        goto :error
    )
)
if not exist "%BIN_DIR%\text\atlas_creo_msg.txt" ( echo Missing: %BIN_DIR%\text\atlas_creo_msg.txt & goto :error )
if not exist "%~dp0SetupCreoVbApi.ps1"            ( echo Missing: %~dp0SetupCreoVbApi.ps1 & goto :error )

REM -- 2) Compile the MSI (candle -> light). WixUtilExtension for util:CloseApplication
REM       and WixQuietExec64; WixUIExtension for WixUI_Minimal + License.rtf. --
if not exist "%DIST_DIR%" mkdir "%DIST_DIR%"
echo === candle ===
candle -ext WixUtilExtension -out "%DIST_DIR%\CreoProduct.wixobj" CreoProduct.wxs
if errorlevel 1 goto :error

echo === light ===
light -ext WixUtilExtension -ext WixUIExtension "%DIST_DIR%\CreoProduct.wixobj" -out "%MSI_PATH%"
if errorlevel 1 goto :error

echo.
echo ---------------------------------------------------------------------
echo Done.  MSI: %MSI_PATH%
echo.
echo Hand this .msi to users (double-click to install). It:
echo   - installs the add-in to C:\Users\Public\AtlasCreo
echo   - registers the pfcls COM server for the Creo on this machine
echo   - adds Startup + Desktop shortcuts "Atlas for Creo"
echo.
echo Each user still does ONE manual step in Creo (buttons can't be placed in code):
echo   File ^> Options ^> Customize Ribbon ^> add the TOOLKIT Commands ^> Save the
echo   Auxiliary Application User Interface. (Needs tk_enable_ribbon_custom_save = yes.)
echo.
echo Optional code-sign:
echo   signtool sign /a /tr http://timestamp.digicert.com /td sha256 /fd sha256 "%MSI_PATH%"
echo ---------------------------------------------------------------------
popd
endlocal
exit /b 0

:error
echo *** Creo MSI build failed ***
popd
endlocal
exit /b 1
