# Atlas CAD Plugin

SolidWorks add-in that connects SolidWorks to Atlas (Euler's PLM backend).

## What this is (current state)

A `.NET Framework 4.8` add-in DLL with these ribbon buttons:

| Button | What it does |
|---|---|
| **Ping Atlas** | Smoke test — calls `/api/v1/cad/ping` and shows the response. |
| **Upload Assembly** | Walks the open assembly tree, uploads root + all child files to Atlas as a new entry. |
| **Browse Atlas** | Opens a dialog listing all assemblies in Atlas with lock status. Allows Check Out / Cancel Checkout. |
| **Check In** | Saves the current assembly, walks the tree, uploads as the next version of the checked-out item. Releases the lock. |
| **Switch Identity** | Changes the user identity the plugin acts as (replaces real auth for the demo). |

## Repository layout

Multi-CAD architecture: one shared library plus three thin per-CAD plugins. Each plugin implements a single `ICadAdapter` interface; everything else (auth, API client, all dialogs, business logic) lives in `AtlasCadCore` and is reused.

```
atlas-cad-plugin/
├── AtlasCadPlugin.sln               (4 projects)
│
├── AtlasCadCore/                    ← shared, no CAD references
│   ├── Adapter/                       ICadAdapter, AssemblyFileRef, CadDocument
│   ├── Auth/                          TokenStore (DPAPI), AuthService (octopus JWT)
│   ├── ApiClient/                     AtlasApiClient + DTOs
│   ├── Forms/                         all WinForms dialogs (CAD-neutral via ICadAdapter)
│   └── Utility/                       PartNumberParser, FileHashing, CheckoutTracker, AutoUpdater
│
├── AtlasSolidWorksAddin/            ← thin SW plugin
│   ├── SolidWorksAdapter.cs           ICadAdapter via IModelDoc2 / AssemblyDoc
│   └── SolidWorksAddin.cs             ISwAddin, ribbon, COM registration
│
├── AtlasCatiaAddin/                 ← thin CATIA V5 plugin
│   ├── CatiaAdapter.cs                ICadAdapter via INFITF / MECMOD / ProductStructureTypeLib
│   ├── CatiaAddin.cs                  COM-visible add-in class
│   └── Atlas.CATScript                CATIA startup macro
│
├── AtlasNxAddin/                    ← thin NX plugin
│   ├── NxAdapter.cs                   ICadAdapter via NXOpen.* (Part / Component / ComponentAssembly)
│   ├── NxAddin.cs                     Menu_Startup entry point
│   └── atlas.men                      NX menu file
│
└── installer/
    ├── Product.wxs                    WiX MSI for SolidWorks
    ├── build.cmd                      builds + signs the SW MSI
    ├── CatiaInstaller.cmd             copies + regasms CATIA DLL
    └── NxInstaller.cmd                copies NX DLL + menu to %UGII_USER_DIR%\startup\
```

Adding a new CAD package is roughly: new project, new csproj with ProjectReference to AtlasCadCore, one Adapter.cs (~300 lines) implementing ICadAdapter, one Addin.cs (~150 lines) of host-specific menu wiring. ~2 days of focused work plus access to that CAD for testing.

## Development workflow

Code is written on macOS, built and tested on a Windows laptop with SolidWorks installed.

1. Edit on Mac.
2. `git push` from Mac.
3. `git pull` on Windows.
4. Open `AtlasCadPlugin.sln` in Visual Studio 2022 **as Administrator** (COM registration writes to HKLM).
5. `Build → Build Solution`. NuGet restores Newtonsoft.Json automatically.
6. Restart SolidWorks (or unload/reload Atlas in `Tools → Add-Ins`).
7. Test.

## Configuration

**Backend URL** — edit `AtlasBaseUrl` constant in `AtlasAddin.cs`:

```csharp
private const string AtlasBaseUrl = "http://192.168.1.100:8000";  // ← your Mac LAN IP
```

Find your Mac IP with `ipconfig getifaddr en0` (Wi-Fi). Backend on Mac must be started with `--host 0.0.0.0` to accept non-localhost connections.

## Demo flow

### Setup
1. **On Mac (backend):** start with `uv run uvicorn main:app --host 0.0.0.0 --port 8000 --reload`. Make sure `cad_s3_bucket` env var points at a real S3 bucket and AWS credentials are configured (see backend repo).
2. **On Windows (plugin):** build, install, restart SolidWorks. On first launch, plugin asks for your name — enter "Alice" for the first run.

### Demonstrate the flow

1. **Alice uploads an assembly.** Open `INSTRUMENT_CLUSTER_ASSY.sldasm` in SolidWorks → click **Upload Assembly**. Wait for "Uploaded N files" message. Now in Atlas.

2. **Switch to Bob.** Click **Switch Identity** → enter "Bob".

3. **Bob checks out the assembly.** Click **Browse Atlas** → select INSTRUMENT_CLUSTER_ASSY → **Check Out**. Files download to `%TEMP%\AtlasCad\<id>\` and the assembly opens in SolidWorks. Lock acquired in DB.

4. **Bob makes a change.** Move a part, change a feature — anything visible. **Save** (Ctrl+S).

5. **Bob checks in.** Click **Check In** → enter optional comment → wait for confirmation. Lock released, version 2 saved.

6. **Switch back to Alice.** Click **Switch Identity** → "Alice".

7. **Alice checks out the same assembly.** Click **Browse Atlas** → select it → **Check Out**. The downloaded version is v2 (Bob's edits) — Alice can see the change Bob made.

### What this demonstrates

- Multi-user collaboration on CAD files via a central server.
- File locking prevents simultaneous edits (try checking out as Alice while Bob has it locked — you'll get rejected).
- Version history — every check-in creates a new version, all preserved in S3.
- Native CAD files preserved (no lossy STEP conversion in this demo).

## What's intentionally not built (demo scope cuts)

- **Real auth** — identity is just a name in a file. M1 will add JWT login.
- **STEP/GLB conversion** — native files only for now. M5 adds auto STEP export.
- **Permissions** — anyone can check out anything.
- **Version history viewer** — only current version is downloadable in the UI. All versions are stored in DB+S3 and can be added later.
- **Conflict resolution** — if you try to upload an assembly that's already in Atlas, you get a duplicate. M-future will add "match by name → upload as new version of existing."
- **Bulk part-code wizard, Browse-and-insert, Custom properties read** — separate milestones.

## Troubleshooting

- **"Atlas" not in Tools → Add-Ins:** COM registration didn't run. Ensure VS is running as Administrator. Check Build Output for `regasm.exe` errors.
- **Ping shows "Could not reach Atlas":** wrong IP / firewall. Confirm Mac uvicorn is bound to `0.0.0.0`, Mac firewall allows incoming, and you can `curl http://<mac-ip>:8000/api/v1/cad/ping` from another machine.
- **Upload fails with S3 error:** the `cad_s3_bucket` doesn't exist or AWS credentials are missing on the Mac. See backend repo for setup.
- **DLL is locked / can't rebuild:** SolidWorks is still loaded with the add-in. Quit SolidWorks before rebuilding.
- **Newtonsoft.Json not found:** open NuGet Package Manager in VS → Restore.

## Roadmap

| Milestone | Status |
|-----------|--------|
| M0 — Ping + ribbon button | ✅ Done |
| Demo today — Upload + Check Out / Check In | ✅ Done |
| M1 — Real authentication (JWT) | Pending |
| M4 — Read Atlas custom properties from CAD | Pending |
| M5 — Auto STEP export on upload | Pending |
| M6 — Bulk part-code wizard | Pending |
| M8 — Browse Atlas → insert into design | Pending |
| M9 — WiX installer + pilot rollout | Pending |
| M10 — Pilot-feedback hardening | Pending |
