@echo off
REM ─────────────────────────────────────────────────────────────────────
REM AtlasCadPlugin (CATIA) portable installer.
REM
REM Run this from inside the unzipped dist\AtlasCatiaPlugin\ folder on
REM the target machine. Must be run as administrator (regasm writes HKLM).
REM
REM What it does:
REM   1. Copies the precompiled DLLs to %ProgramFiles%\Atlas\Catia
REM   2. Runs regasm /codebase to register the COM class
REM   3. Drops Atlas.CATScript into the user's %APPDATA%\DassaultSystemes\
REM      CATEnv\CATStartup so it appears in Tools → Macros next time
REM      CATIA starts
REM
REM Prerequisites on the target machine:
REM   - CATIA V5R21 or newer installed (DLLs are R21-Interop-compiled but
REM     dual-version safe via reflection — P7.53)
REM   - .NET Framework 4.8 runtime (Windows 10/11 ship with it)
REM   - regasm.exe on PATH (ships with .NET Framework)
REM ─────────────────────────────────────────────────────────────────────

setlocal
pushd %~dp0

set INSTALL_DIR=%ProgramFiles%\Atlas\Catia
set CATIA_STARTUP=%APPDATA%\DassaultSystemes\CATEnv\CATStartup

REM Resolve regasm — try common locations if not on PATH.
set REGASM=regasm.exe
where regasm >nul 2>nul
if errorlevel 1 (
    if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\regasm.exe" (
        set REGASM=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\regasm.exe
    ) else if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\regasm.exe" (
        set REGASM=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\regasm.exe
    ) else (
        echo regasm.exe not found. Install .NET Framework 4.x and retry.
        goto :error
    )
)

REM Admin check — regasm /codebase writes HKLM.
net session >nul 2>&1
if errorlevel 1 (
    echo This script must be run as Administrator.
    echo Right-click InstallCatia.cmd and pick "Run as administrator".
    goto :error
)

echo === Copying DLLs to %INSTALL_DIR% ===
if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"
for %%F in (AtlasCatiaAddin.dll AtlasCadCore.dll Newtonsoft.Json.dll INFITF.dll MECMOD.dll ProductStructureTypeLib.dll KnowledgewareTypeLib.dll) do (
    if not exist "%~dp0%%F" (
        echo Missing required file: %%F
        echo Did you unzip the full distribution?
        goto :error
    )
    copy /Y "%~dp0%%F" "%INSTALL_DIR%\" >nul
    if errorlevel 1 goto :error
)
echo   Done.

echo === Registering COM (regasm /codebase) ===
"%REGASM%" /codebase "%INSTALL_DIR%\AtlasCatiaAddin.dll"
if errorlevel 1 goto :error

echo === Installing macro ===
if not exist "%CATIA_STARTUP%" mkdir "%CATIA_STARTUP%"
copy /Y "%~dp0Atlas.CATScript" "%CATIA_STARTUP%\" >nul
if errorlevel 1 goto :error

echo.
echo ─────────────────────────────────────────────────────────────────────
echo Done. Restart CATIA. Run the Atlas macro via:
echo   Tools menu  →  Macros  →  Alt+F8  →  Atlas.CATScript  →  Run
echo ─────────────────────────────────────────────────────────────────────
popd
endlocal
exit /b 0

:error
echo *** Install failed ***
popd
endlocal
exit /b 1
