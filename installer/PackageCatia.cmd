@echo off
REM ─────────────────────────────────────────────────────────────────────
REM Build a portable CATIA plugin distribution zip.
REM
REM Output: installer\dist\AtlasCatiaPlugin.zip
REM
REM On the target machine: unzip, then run InstallCatia.cmd as admin.
REM
REM Run this from a Visual Studio Developer Command Prompt on the dev
REM box (needs msbuild + the CATIA Interop DLLs at C:\CATIA-RefAssemblies
REM or wherever CATIA_CODE_BIN points).
REM ─────────────────────────────────────────────────────────────────────

setlocal
pushd %~dp0

set BIN_DIR=%~dp0..\AtlasCatiaAddin\bin\Release
set DIST_DIR=%~dp0dist\AtlasCatiaPlugin
set ZIP_PATH=%~dp0dist\AtlasCatiaPlugin.zip

if not defined CATIA_CODE_BIN (
    REM Same auto-detect as BuildCatia.cmd
    for /D %%D in ("%ProgramFiles%\Dassault Systemes\B*") do (
        if not defined CATIA_CODE_BIN if exist "%%~fD\win_b64\code\bin\INFITF.dll" set "CATIA_CODE_BIN=%%~fD\win_b64\code\bin"
        if not defined CATIA_CODE_BIN if exist "%%~fD\intel_a\code\bin\INFITF.dll" set "CATIA_CODE_BIN=%%~fD\intel_a\code\bin"
    )
    for /D %%D in ("%ProgramFiles(x86)%\Dassault Systemes\B*") do (
        if not defined CATIA_CODE_BIN if exist "%%~fD\win_b64\code\bin\INFITF.dll" set "CATIA_CODE_BIN=%%~fD\win_b64\code\bin"
        if not defined CATIA_CODE_BIN if exist "%%~fD\intel_a\code\bin\INFITF.dll" set "CATIA_CODE_BIN=%%~fD\intel_a\code\bin"
    )
    REM Fall back to the manually-generated Interop DLL folder we use on
    REM V5R21 testing rigs (which only ships .tlb in code\bin).
    if not defined CATIA_CODE_BIN if exist "C:\CATIA-RefAssemblies\INFITF.dll" set "CATIA_CODE_BIN=C:\CATIA-RefAssemblies"
)
if not defined CATIA_CODE_BIN (
    echo Could not find CATIA interop assemblies.
    echo Set CATIA_CODE_BIN to a folder containing INFITF.dll etc.
    goto :error
)
echo Using CATIA_CODE_BIN=%CATIA_CODE_BIN%

echo === Building CATIA add-in in Release ===
msbuild ..\AtlasCatiaAddin\AtlasCatiaAddin.csproj /p:Configuration=Release "/p:CATIA_CODE_BIN=%CATIA_CODE_BIN%" /t:Rebuild
if errorlevel 1 goto :error

echo === Staging distribution into %DIST_DIR% ===
if exist "%DIST_DIR%" rmdir /S /Q "%DIST_DIR%"
mkdir "%DIST_DIR%"

for %%F in (AtlasCatiaAddin.dll AtlasCadCore.dll Newtonsoft.Json.dll INFITF.dll MECMOD.dll ProductStructureTypeLib.dll KnowledgewareTypeLib.dll) do (
    if not exist "%BIN_DIR%\%%F" (
        echo Build output missing: %BIN_DIR%\%%F
        goto :error
    )
    copy /Y "%BIN_DIR%\%%F" "%DIST_DIR%\" >nul
    if errorlevel 1 goto :error
)
copy /Y "%~dp0..\AtlasCatiaAddin\Atlas.CATScript" "%DIST_DIR%\" >nul
if errorlevel 1 goto :error
copy /Y "%~dp0InstallCatia.cmd" "%DIST_DIR%\" >nul
if errorlevel 1 goto :error

REM Quick README so the user on the target machine knows what to do.
> "%DIST_DIR%\README.txt" (
    echo Atlas CAD Plugin for CATIA — portable install package
    echo.
    echo To install on this machine:
    echo   1. Close CATIA if it's running
    echo   2. Right-click InstallCatia.cmd ^→ "Run as administrator"
    echo   3. Wait for "Done." message
    echo   4. Start CATIA
    echo   5. Tools menu ^→ Macros ^→ Alt+F8 ^→ Atlas.CATScript ^→ Run
    echo.
    echo To uninstall:
    echo   1. regasm /codebase /unregister "%%ProgramFiles%%\Atlas\Catia\AtlasCatiaAddin.dll"
    echo   2. Delete "%%ProgramFiles%%\Atlas\Catia" folder
    echo   3. Delete "%%APPDATA%%\DassaultSystemes\CATEnv\CATStartup\Atlas.CATScript"
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
echo Copy the zip to the target machine, unzip, run InstallCatia.cmd.
echo ─────────────────────────────────────────────────────────────────────
popd
endlocal
exit /b 0

:error
echo *** Package build failed ***
popd
endlocal
exit /b 1
