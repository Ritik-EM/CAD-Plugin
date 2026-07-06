# AtlasCreoAddin

Creo Parametric integration for Atlas. Unlike the SolidWorks / CATIA / NX add-ins
(in-process DLLs the CAD loads), the Creo **VB API is asynchronous**: this project
builds a **standalone `.exe`** that attaches to a *running* Creo session over COM
and drives the same shared Atlas WinForms flows (`UploadToPartMasterForm`,
`BrowsePartMasterForm`, `CheckinFlow`, `ReleasePartNumberForm`) as every other CAD.

It runs in **full asynchronous mode**: it registers Atlas commands in Creo's UI,
pumps Creo's event loop so ribbon-button clicks call back into this process, and
stays **resident** (with a system-tray icon) while Creo is open. The same actions
are on the tray menu — which works even before the one-time ribbon customization
and gives a clean **Quit** (important: killing the process without quitting leaves
Creo's async channel half-open and the next launch faults with `RPC_E_SERVERFAULT`).

```
AtlasCreoAddin/
├── CreoAdapter.cs   ICadAdapter over pfcls (IpfcBaseSession / IpfcModel / IpfcSolid …)
├── CreoAddin.cs     [STAThread] Main: Connect(), register ribbon commands, event loop
├── text\atlas_creo_msg.txt   button label/tooltip message file (copied next to exe)
└── AtlasCreoAddin.csproj   references ..\lib\Interop.pfcls.dll (generated, see below)
```

## One-time machine setup (required)

The VB API talks to Creo through an out-of-process COM server, `pfclscom.exe`. A
stock Creo install ships that server but does **not** register it for COM, and the
build needs a generated interop assembly. `installer\SetupCreoVbApi.ps1` does all
of it. Run it **once, from an elevated PowerShell**:

```powershell
cd installer
powershell -ExecutionPolicy Bypass -File .\SetupCreoVbApi.ps1
```

It:
1. Generates `lib\Interop.pfcls.dll` from the installed Creo via `TlbImp`
   (this is git-ignored — it is machine/version-specific).
2. Registers the `pfcls` **type library** (embedded in `pfclscom.exe`) for COM
   interface marshaling.
3. Registers `CLSID\LocalServer32 → pfclscom.exe` for every `pfcls` coclass, so
   `new CCpfc*()` activates.
4. Sets the `PRO_COMM_MSG_EXE` machine environment variable (path to
   `pro_comm_msg.exe`) that the VB API requires.

Re-run it whenever the Creo version changes. Undo COM registration with
`-Unregister`.

> Why this is needed: without it, `new CCpfcAsyncConnection()` throws
> `REGDB_E_CLASSNOTREG` (0x80040154), and after LocalServer32 is set but the
> typelib isn't, `TYPE_E_LIBNOTREGISTERED` (0x8002801D). With both in place,
> connecting with no Creo running yields `pfcExceptions::XToolkitNotFound` —
> the expected "no live session" result.

## Build

From a Visual Studio Developer Command Prompt:

```
installer\BuildCreo.cmd        REM builds AtlasCreoAddin in Release (runs setup if interop missing)
```

or just build `AtlasCreoAddin` from `AtlasCadPlugin.sln` in Visual Studio.

## Run / test

1. Start **Creo Parametric** (a commercial seat with the VB API) and open your
   assembly.
2. Launch `AtlasCreoAddin\bin\Release\AtlasCreoAddin.exe`. It connects to the
   running Creo session, registers the Atlas commands, and drops an **Atlas — Creo**
   icon in the system tray. Right-click it for: Upload to Atlas · Browse / Check Out ·
   Check In · Release Part Code · Sign Out · **Quit Atlas**.
3. Sign in with your Atlas (Octopus) credentials the first time you run a flow.
4. Quit via the tray's **Quit Atlas** (not Task Manager) so Creo disconnects cleanly.

### One-time: put the buttons on the Creo ribbon

The VB API can't place buttons on the ribbon programmatically — it's a one-time
Creo customization (per mode, e.g. Assembly):

1. Add `tk_enable_ribbon_custom_save yes` to your `config.pro` and restart Creo.
2. With `AtlasCreoAddin.exe` **running**, in Creo: **File > Options > Customize
   Ribbon**.
3. In **Choose commands from**, pick **TOOLKIT Commands** — the Atlas commands
   (Upload to Atlas, Browse / Check Out, …) appear.
4. Create a tab/group and **Add** the Atlas commands to it.
5. **Import/Export > Save the Auxiliary Application User Interface** — writes
   `text\toolkitribbonui.rbn` next to the exe.

On later runs (with the exe running) the app auto-loads that `.rbn`, so the Atlas
buttons appear on your ribbon tab. Until then, use the tray menu.

In production the `.exe` is auto-started with Creo (e.g. a Windows startup task or
a Creo start-command script) so the ribbon buttons are always live.

### Smoke-testing the raw VB API (`AtlasCreoSpike`)

`..\AtlasCreoSpike` is a throwaway console app that exercises the five primitives
the adapter depends on (connect, active model, list models, recursive walk, STEP
export) and writes `%TEMP%\creo_spike.log`. Build it, open an assembly in Creo,
run it, and read the log to confirm the API behaves before driving the full flows.

## Diagnostics

`CreoAdapter` logs every walk/save/export/import step to
`%APPDATA%\AtlasCad\walk_assembly.log`, stamped with `WalkAssemblyVersion`.

## Notes / limitations

- Won't run on the Creo **Educational Edition** (no `pfcls` COM server).
- `CreoAdapter` was authored against the Creo docs + shipped VB examples and then
  compiled and corrected against the **real** `Interop.pfcls.dll` from Creo 12.
  The runtime flows (walk / STEP export / check-in) still want a pass on a live
  commercial seat with a representative assembly.
