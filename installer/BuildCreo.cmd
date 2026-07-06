@echo off
REM Build the Creo Parametric Atlas add-in on the current machine.
REM
REM Unlike the SolidWorks/CATIA/NX add-ins (in-process DLLs the CAD loads), the
REM Creo VB API is ASYNC: AtlasCreoAddin is a standalone .exe that attaches to a
REM RUNNING Creo session over COM and drives the shared Atlas WinForms flows.
REM
REM Requires (one-time):
REM   - Creo Parametric installed WITH the VB API feature (ships pfclscom.exe).
REM   - lib\Interop.pfcls.dll generated + pfcls COM registered + PRO_COMM_MSG_EXE
REM     set. All of that is done by installer\SetupCreoVbApi.ps1 — run it once,
REM     ELEVATED, before the first build:
REM         powershell -ExecutionPolicy Bypass -File SetupCreoVbApi.ps1
REM   - Visual Studio 2022+ / .NET Framework 4.8 SDK (run from a Developer prompt).
REM
REM Steps:
REM   1. Ensure the interop + COM registration exist (runs SetupCreoVbApi if the
REM      interop is missing).
REM   2. Build AtlasCreoAddin in Release.
REM
REM To run: open Creo with an assembly, then launch
REM   ..\AtlasCreoAddin\bin\Release\AtlasCreoAddin.exe

setlocal
pushd %~dp0

if not exist "%~dp0..\lib\Interop.pfcls.dll" (
    echo === Interop/COM not set up yet — running SetupCreoVbApi.ps1 ===
    echo     (needs an elevated shell; re-run this from "Run as Administrator" if it fails)
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0SetupCreoVbApi.ps1"
    if errorlevel 1 goto :error
)

echo === Building Creo add-in in Release ===
msbuild ..\AtlasCreoAddin\AtlasCreoAddin.csproj /p:Configuration=Release /t:Rebuild
if errorlevel 1 goto :error

echo.
echo === Done. ===
echo Open Creo Parametric with your assembly, then run:
echo   %~dp0..\AtlasCreoAddin\bin\Release\AtlasCreoAddin.exe
popd
endlocal
exit /b 0

:error
echo *** Build failed ***
popd
endlocal
exit /b 1
