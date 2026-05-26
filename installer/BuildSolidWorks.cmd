@echo off
REM Build the SolidWorks Atlas add-in MSI installer.
REM
REM Requires:
REM   - Visual Studio 2022 with .NET Framework 4.8 SDK
REM   - WiX Toolset v3.11+ on PATH (candle, light)
REM   - Run from a Visual Studio Developer Command Prompt so msbuild + signtool
REM     are available
REM
REM Optional:
REM   set SIGN_CERT=1    code-sign the MSI with the default cert from the user store
REM
REM Output: installer\AtlasCadPlugin-SolidWorks.msi

setlocal
pushd %~dp0

echo === Building SolidWorks add-in in Release ===
msbuild ..\AtlasSolidWorksAddin\AtlasSolidWorksAddin.csproj /p:Configuration=Release /t:Rebuild
if errorlevel 1 goto :error

echo === Compiling WiX source ===
REM -ext WixUtilExtension is for util:CloseApplication (block install while SW is running).
candle -ext WixUtilExtension Product.wxs -out Product.wixobj
if errorlevel 1 goto :error

echo === Linking MSI ===
light -ext WixUIExtension -ext WixUtilExtension Product.wixobj -out AtlasCadPlugin-SolidWorks.msi
if errorlevel 1 goto :error

if defined SIGN_CERT (
    echo === Code-signing ===
    signtool sign /a /tr http://timestamp.digicert.com /td sha256 /fd sha256 AtlasCadPlugin-SolidWorks.msi
    if errorlevel 1 goto :error
) else (
    echo SIGN_CERT not set - skipping code-signing.
)

echo === Done: AtlasCadPlugin-SolidWorks.msi ===
echo Distribute the MSI directly, or upload to S3 (atlas-app-docs/cad/installers/)
echo and bump plugin_latest_version in atlas-api settings to trigger auto-update.
popd
endlocal
exit /b 0

:error
echo *** Build failed ***
popd
endlocal
exit /b 1
