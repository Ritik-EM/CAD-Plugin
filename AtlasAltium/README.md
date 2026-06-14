# Atlas Altium plugin

Altium Designer (ECAD) integration for Atlas PLM. On **check-in**, it:

1. **Syncs the full project** to Atlas against one part code — the `.PrjPcb`, every
   schematic (`.SchDoc`), the PCB (`.PcbDoc`), and the referenced libraries
   (`.SchLib`/`.PcbLib`/`.IntLib`).
2. **Auto-generates and uploads artifacts** — BOM, PDF schematics, Gerbers + NC drill,
   and a whole-board 3D STEP.

Unlike SolidWorks/CATIA/NX, Altium's automation API only runs **inside a live Altium
process** (it is not reachable from out-of-process .NET COM). So this integration is split
in two, bridged by a JSON manifest on disk:

```
  Altium (DelphiScript)                 file IPC                  Windows (.NET)
  ┌───────────────────────────┐   manifest.json   ┌──────────────────────────────┐
  │ Script/AtlasCheckin.pas    │ ───────────────►  │ AtlasAltiumBridge.exe          │
  │  • compile + save-all      │                   │  • reuses AtlasCadCore:        │
  │  • read part code (param)  │                   │      AtlasApiClient, Auth/     │
  │  • enumerate project files │   ◄───────────────│      TokenStore, FileHashing   │
  │  • resolve libraries       │   result.json     │  • builds tree + filePaths     │
  │  • run OutJob → artifacts  │                   │  • api.CheckinAsync / Upload   │
  │  • write manifest.json     │                   │      → S3 (WAF-safe staging)   │
  └───────────────────────────┘                   └──────────────────────────────┘
```

The C# side reuses the **exact same** proven upload pipeline as the other three CADs
(`AtlasApiClient`, S3 presign/PUT staging, `AuthService`/`TokenStore`, `FileHashing`,
the DTOs). Secrets and HTTPS never touch the DelphiScript.

> **Launching the bridge:** this Altium build's DelphiScript does not expose
> `CreateOleObject`/`ShellExecute`, so the script can't start the bridge `.exe` directly. The
> script writes `manifest.json` and the bridge is run separately (manually, or via a Windows
> Startup shortcut / folder-watcher). One-click auto-launch is a TODO — see `AtlasCheckin.pas`
> `LaunchBridge`.

## ECAD model: one project = one part code

In mechanical CAD, one file = one part and the walk is a recursive assembly tree. Altium has
no such tree — a project is a **flat** set of documents plus a component netlist. So we model
**the whole project as a single Atlas part_master** identified by one part code:

| Atlas slot | Altium file |
|---|---|
| canonical native (`filename`) | the `.PrjPcb` |
| `step_filename` | the whole-board `.step` artifact |
| companion files | every `.SchDoc`, `.PcbDoc`, `.SchLib`/`.PcbLib`/`.IntLib`, BOM, PDF, Gerber/NC files |
| `tree_filename` | a `.tree.json` listing all of the above filenames |

This maps cleanly onto Atlas's existing companion-file mechanism (one canonical native +
N companions stored under the same part prefix).

## Layout

```
AtlasAltium/
├── README.md                  ← this file
├── Script/
│   ├── AtlasCheckin.pas        ← the in-Altium check-in script (DelphiScript)
│   └── AtlasAltium.PrjScr      ← script project (install as a Global Project)
├── OutJob/
│   └── HOW_TO_CREATE_OUTJOB.md  ← make the real OutJob inside Altium (BOM/PDF/Gerber/STEP)
└── AtlasAltiumBridge/          ← C# sidecar (.NET 4.8 WinExe), added to AtlasCadPlugin.sln
    ├── AtlasAltiumBridge.csproj
    ├── Program.cs               ← [STAThread] entry: load manifest, auth, run, write result
    ├── AltiumCheckinFlow.cs     ← builds tree + filePaths, calls CheckinAsync/UploadPartMasterAsync
    ├── AltiumManifest.cs        ← manifest DTOs (mirror the JSON the script writes)
    ├── packages.config
    └── Properties/AssemblyInfo.cs
```

## Manifest contract (`manifest.json`)

Written by `AtlasCheckin.pas`, read by `AtlasAltiumBridge.exe`. Exchange dir:
`C:\Users\Public\AtlasAltium\` (override with `ATLAS_ALTIUM_DIR`).

```jsonc
{
  "schema_version": 1,
  "operation": "checkin",            // "checkin" (new revision) | "upload" (first sync)
  "part_code": "AH1203100B",         // 10-char Atlas code, from the AtlasPartCode project parameter
  "project_name": "MICROCAL_MAIN",
  "comment": "fixed silk on R12",    // optional check-in comment
  "source_files": [                  // REQ 1
    { "path": "C:\\...\\MICROCAL_MAIN.PrjPcb", "role": "project",   "bucket": "file" },
    { "path": "C:\\...\\Main.SchDoc",          "role": "schematic", "bucket": "file" },
    { "path": "C:\\...\\Main.PcbDoc",          "role": "pcb",       "bucket": "file" },
    { "path": "R:\\Libs\\My.SchLib",           "role": "library",   "bucket": "file" },
    { "path": "",                              "role": "library",   "bucket": "managed",
      "warning": "Altium-365 managed component; not bundled" }
  ],
  "artifacts": [                     // REQ 2
    { "path": "C:\\...\\Outputs\\BOM.xlsx",        "kind": "bom" },
    { "path": "C:\\...\\Outputs\\Schematics.pdf",  "kind": "pdf" },
    { "path": "C:\\...\\Outputs\\Gerber\\top.gtl", "kind": "gerber" },
    { "path": "C:\\...\\Outputs\\Board.step",      "kind": "step" }
  ],
  "warnings": ["2 managed components not bundled (need Altium 365)"]
}
```

`bucket` values: `file` (copyable, bundled), `managed` (Altium-365 server library — **not**
bundled, warned), `database` (`.DbLib` — bundled but needs the external DB, warned).

## Artifacts (REQ 2) — uses the project's own OutJobs

On check-in the script **fires the OutJobs already in the project** (e.g. `EMS vendor files…`,
`PCB fabrication files…` under *Settings → Output Job Files*) — no hand-authored OutJob needed.
It runs every **enabled** (green-lit) container.

**Altium generates outputs asynchronously**, so the script can't harvest them in time (it would
scan before the files are written). Instead the script records the folder(s) to scan in the
manifest (`artifact_scan_dirs`), and the **bridge** — a separate process that runs after
generation — **waits for the output files to appear (polls until stable, ~timeout 4 min), then
harvests** them, classifying by extension → `.step/.stp`=STEP (→ Atlas `3d`), `.pdf`, `.csv/.xlsx`,
Gerber/drill. STEP rides into the `3d` slot; the rest attach as companions.

- **STEP:** your existing OutJobs likely don't include it. Add an **Export Outputs → Export STEP
  → PCB Document** output to one of them and **enable** it (see `OutJob/HOW_TO_CREATE_OUTJOB.md`).
  Requires the STEP/MBASTEP extension.
- **Risk:** Gerber generation opens a CAMtastic preview that can throw a modal/stall, and
  whole-board STEP on a large board can stall the writer (the same class as the CATIA
  root-assembly STEP hang). The script closes the leftover doc afterward, but **test on your
  real board** and watch for a hang.
- If a project has no OutJob, check-in still works and just skips artifacts (REQ 1 only).

## Revision carry-forward

After a successful check-in the bridge writes the new root revision to
`current_part_code.txt`; on the **next** check-in the script reads it and advances the project's
`AtlasPartCode` parameter — so re-check-ins bump from the latest revision instead of the original
base. (One cycle of lag is inherent: the param catches up at the start of the next run, because
the in-Altium script runs before the out-of-process bridge knows the new revision.)

## Deploy

On the Windows build box (VS 2022 Developer Command Prompt):

```bat
installer\BuildAltium.cmd
```

This builds `AtlasAltiumBridge` in Release and installs to two fixed locations:
- **bridge EXE + DLLs + exchange files** → `C:\Users\Public\AtlasAltium` (fixed path, because
  DelphiScript can't read env vars to locate it).
- **script + OutJob** → `%LOCALAPPDATA%\Atlas\Altium` (where the Global Project points).

Then, in Altium: **Preferences → Scripting System → Global Projects → Add**
`%LOCALAPPDATA%\Atlas\Altium\AtlasAltium.PrjScr`. To run it, use **File → Run Script** (pick
`AtlasCheckin`), or bind it to a button via right-click toolbar → Customize.

## Spikes to run before trusting this (on your real Altium)

These are the parts the research flagged as needing empirical confirmation on your AD version.
See `../.claude` project memory `altium-plugin-feasibility` for the full list. In order:

1. **STEP stall** on your largest board (the analog of the CATIA root-assembly STEP hang).
2. **OutJob container run** — does `WorkspaceManager:GenerateReport`/`Print` produce files
   unattended; does the Gerber CAMtastic window throw a modal despite `DisableDialog=True`.
3. **Library resolution + bucketing** — `IIntegratedLibraryManager.FindComponentLibraryPath`
   returns real paths for file libs, none for managed; read the managed-detection property
   names from `EDPInterfaces.pas` in your AD install.
4. **Project-parameter round-trip** — `AtlasPartCode` survives in the `.PrjPcb`.
5. **CreateOleObject('WScript.Shell')** can launch the bridge from DelphiScript.
6. **End-to-end** — bridge consumes the manifest and check-in lands in Atlas.

Anything not yet verified is marked `SPIKE:` in the code.
