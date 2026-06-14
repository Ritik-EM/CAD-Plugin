# AtlasCadPlugin — installer

Three sibling build scripts, one per supported CAD product. Each builds the
relevant addin in Release and produces (or installs) a ready-to-use artifact:

| Script | Target CAD | Output |
|---|---|---|
| `BuildSolidWorks.cmd` | SolidWorks | `AtlasCadPlugin-SolidWorks.msi` — distributable MSI (from `Product.wxs`) |
| `BuildCatiaMsi.cmd` | CATIA V5 | `dist\AtlasCatiaPlugin.msi` — distributable MSI (from `CatiaProduct.wxs`) |
| `BuildCatia.cmd` | CATIA V5 | Registered COM addin under `%ProgramFiles%\Atlas\Catia\` + macro under CATStartup (installs locally) |
| `BuildNx.cmd` | Siemens NX | DLLs + `atlas.men` dropped into `%UGII_USER_DIR%\startup\` (installs locally) |
| `BuildAltium.cmd` | Altium Designer | Builds + installs locally (binaries + script + watcher autostart) |
| `BuildAltiumMsi.cmd` | Altium Designer | `dist\AtlasAltiumPlugin.msi` — distributable MSI (from `AltiumProduct.wxs`) |

All MSIs share `License.rtf` for the install license page (`<WixVariable Id="WixUILicenseRtf">`),
so none of them shows WiX's built-in *lorem ipsum* placeholder.

SW has a real MSI installer because end users install it on their own machines.
CATIA and NX use direct local-install scripts because their typical deployment
is "build on the engineer's box, register, restart CAD" — no MSI in the mix.

## Prerequisites (Windows build machine)

1. **Visual Studio 2022** with .NET Framework 4.8 SDK.
2. **Run from a Visual Studio Developer Command Prompt** so `msbuild`, `regasm`, `signtool` are on PATH.
3. **WiX Toolset v3.11+** — https://wixtoolset.org/ — only needed for `BuildSolidWorks.cmd`. Add its `bin\` to PATH.
4. **CATIA / NX SDKs** — only needed for the corresponding `Build*.cmd`. The CATIA addin needs the CATIA V5 interop assemblies; the NX addin needs `UGII_USER_DIR` set (or open the NX command prompt).
5. (Optional) Euler's EV code-signing certificate installed under the current user — only used by `BuildSolidWorks.cmd` when `SIGN_CERT=1`.

## Build + ship — SolidWorks

```cmd
cd installer
BuildSolidWorks.cmd
```

Produces `AtlasCadPlugin-SolidWorks.msi`.

To code-sign (recommended for releases — avoids Windows SmartScreen blocking new users):

```cmd
set SIGN_CERT=1
BuildSolidWorks.cmd
```

The `signtool sign /a` flag picks the best available certificate from the user store; pin a specific one with `/n "Euler Motors EV Cert"` if you have multiple.

## Build + install — CATIA

Run from an **Administrator** Developer Command Prompt (regasm writes to HKLM):

```cmd
cd installer
BuildCatia.cmd
```

The script builds Release, copies DLLs to `%ProgramFiles%\Atlas\Catia\`, runs `regasm /codebase`, and drops `Atlas.CATScript` into your CATStartup folder. Restart CATIA — the macro auto-runs.

## Build + install — NX

NX needs `UGII_USER_DIR` set. Either open the *NX Command Prompt* (which sets it automatically), or set it manually first:

```cmd
set UGII_USER_DIR=%APPDATA%\Siemens\NX2306\UGII
cd installer
BuildNx.cmd
```

Copies the addin DLL + `atlas.men` into `%UGII_USER_DIR%\startup\`. Restart NX — the Atlas menu appears.

## Build + ship — Altium (MSI)

```cmd
cd installer
BuildAltiumMsi.cmd
```

Produces `dist\AtlasAltiumPlugin.msi`. Unlike CATIA/SW, Altium has **no COM add-in**, so the
MSI just lays down files + a Startup shortcut (no `regasm`). It:

- installs the bridge (`AtlasAltiumBridge.exe` + 2 DLLs) to `C:\Users\Public\AtlasAltium`,
- installs the DelphiScript to `%LOCALAPPDATA%\Atlas\Altium`,
- adds a Startup shortcut for the watcher (`AtlasAltiumBridge.exe --watch`) and offers to start
  it now (an ExitDialog checkbox).

**One manual step remains per user** (Altium has no script-install hook): in Altium,
*Preferences → Scripting System → Global Projects → Add* `…\Atlas\Altium\AtlasAltium.PrjScr`,
then bind `AtlasCheckin` to a toolbar button. After that, check-in is one button click — the
script signals the resident watcher, which uploads in the background.

## Auto-update

`AutoUpdater.CheckAsync` polls `GET /api/v1/cad/version/latest`; if the backend reports a
newer version than the loaded `AtlasCadCore` assembly, it downloads the MSI and a non-blocking
dialog tells the user to quit their CAD app and run it. The endpoint is **source-aware** — it
returns the right MSI per `X-Atlas-Cad-Source` header via `_LATEST_BY_SOURCE` in
`atlas-api app/api/cad/v1/resource.py`. **SolidWorks, CATIA, and Altium** all participate
(NX returns `version: null`, so its updater no-ops).

- **SolidWorks/CATIA** check on add-in startup; **Altium** checks once per watcher session
  (on the first check-in, after auth) — same `AutoUpdater`, same flow.

To ship a new version (e.g. Altium):

1. Bump `Version` in the `.wxs` (e.g. `AltiumProduct.wxs`) **and** `AssemblyVersion`/`AssemblyFileVersion`
   in **both** the addin's `Properties/AssemblyInfo.cs` and `AtlasCadCore/Properties/AssemblyInfo.cs`
   — `AutoUpdater` compares against `AtlasCadCore`'s version, so it must match.
2. Build the MSI (`BuildAltiumMsi.cmd`).
3. Upload to S3 at the key the backend expects:
   `aws s3 cp dist\AtlasAltiumPlugin.msi s3://atlas-app-docs/cad/installers/AtlasAltiumPlugin.msi`
4. Bump the `version` for that source in `_LATEST_BY_SOURCE` (`resource.py`) and **deploy `atlas-api`**.

Engineers pick up the new MSI on their next session (SW/CATIA: CAD restart; Altium: next check-in).

## Test installation

After `BuildSolidWorks.cmd`:

```cmd
msiexec /i AtlasCadPlugin-SolidWorks.msi /l*v install.log
```

Then start SolidWorks. *Tools → Add-Ins* should list **Atlas** — tick it.

Uninstall via *Settings → Apps → Atlas CAD Plugin → Uninstall*, or:

```cmd
msiexec /x AtlasCadPlugin-SolidWorks.msi
```
