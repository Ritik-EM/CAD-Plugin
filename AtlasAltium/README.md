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

## The OutJob — important

The OutJob **must be created inside Altium** — a hand-authored file fails with *"Unrecognized
OutputJob Document Version"*. Create one per project, named **`Atlas_Template.OutJob`** beside
the `.PrjPcb`, with four enabled (green-lit) outputs — BOM, Schematic Prints (PDF), Gerber +
NC Drill, and Export STEP. Full steps: **`OutJob/HOW_TO_CREATE_OUTJOB.md`**.

The script runs *every enabled container* in that OutJob, then harvests the output folder and
classifies files by extension — so you don't have to keep container names in sync with code.
Until the OutJob exists, check-in still works and just skips artifact generation (REQ 1 only).

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
