# AtlasCadPlugin — installer

Builds a signed MSI that installs the plugin and registers it as a SolidWorks add-in.

## Prerequisites (Windows build machine)

1. **Visual Studio 2022** with .NET Framework 4.8 SDK.
2. **WiX Toolset v3.11+** — https://wixtoolset.org/. Add the `bin\` to PATH.
3. **signtool** — comes with the Windows SDK. Open a *Visual Studio Developer Command Prompt* so `signtool` is on PATH.
4. (Optional) Euler's EV code-signing certificate installed under the current user.

## Build

```cmd
cd installer
build.cmd
```

Produces `AtlasCadPlugin.msi` in this directory.

To skip code-signing (for dev builds), don't set `SIGN_CERT`. To sign with the default cert:

```cmd
set SIGN_CERT=1
build.cmd
```

The `signtool sign /a` flag picks the best available certificate from the user store; replace with `/n "Euler Motors EV Cert"` to pin a specific cert.

## Auto-update

The plugin checks `/api/v1/cad/version/latest` on startup. If the backend reports a newer version, a non-blocking dialog tells the user to close SolidWorks and run the new MSI. Distribute new builds by:

1. Build the new MSI with an incremented `Version` in `Product.wxs`.
2. Upload to S3 at `cad/installers/AtlasCadPlugin-<version>.msi`.
3. Update `plugin_latest_version` + `plugin_installer_s3_key` in atlas-api's settings (or env vars).
4. Existing plugin instances pick up the new version on next SolidWorks restart.

## Test installation

After `build.cmd`:

```cmd
msiexec /i AtlasCadPlugin.msi /l*v install.log
```

Then start SolidWorks. *Tools → Add-Ins* should list "Atlas". Enable it.

Uninstall via *Settings → Apps → Atlas CAD Plugin → Uninstall*, or:

```cmd
msiexec /x AtlasCadPlugin.msi
```
