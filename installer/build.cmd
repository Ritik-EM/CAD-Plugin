@echo off
REM Build the AtlasCadPlugin MSI installer. Requires WiX v3.11+ on PATH.
REM Run from a Visual Studio Developer Command Prompt so signtool is available.

setlocal
pushd %~dp0

echo === Building plugin in Release ===
msbuild ..\AtlasCadPlugin.sln /p:Configuration=Release /t:Rebuild
if errorlevel 1 goto :error

echo === Compiling WiX source ===
candle Product.wxs -out Product.wixobj
if errorlevel 1 goto :error

echo === Linking MSI ===
light -ext WixUIExtension Product.wixobj -out AtlasCadPlugin.msi
if errorlevel 1 goto :error

if defined SIGN_CERT (
    echo === Code-signing ===
    signtool sign /a /tr http://timestamp.digicert.com /td sha256 /fd sha256 AtlasCadPlugin.msi
    if errorlevel 1 goto :error
) else (
    echo SIGN_CERT not set - skipping code-signing.
)

echo === Done: AtlasCadPlugin.msi ===
popd
endlocal
exit /b 0

:error
echo *** Build failed ***
popd
endlocal
exit /b 1
