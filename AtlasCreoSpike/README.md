# AtlasCreoSpike

A throwaway console app that proves the five Creo VB-API primitives the future
`CreoAdapter` needs, **before** we write the adapter:

1. attach to a **running** Creo session (async COM)
2. read the active model (your `TOP.asm`)
3. list every model loaded in session (reliability floor)
4. **recursively walk the assembly tree** (core of `WalkAssembly`)
5. **export one model to STEP** (the neutral artifact)

It's deliberately late-bound (`dynamic` + reflection over the `pfcls` interop):
when a Creo-10 call name/signature differs from our guess, it **logs the error
and dumps the real interface members** instead of crashing. **The log is the
deliverable** — paste it back and we lock the adapter to the real API.

## Prerequisites (one-time)

- Creo 10 installed **with the "VB API for Creo Parametric" feature checked**
  (API Toolkits section of the installer).
- Creo running, with a small assembly open (`TOP.asm`).

## Build & run

1. Open `AtlasCreoSpike.csproj` in **Visual Studio 2022** (double-click it; VS
   opens it in a temp solution — no need to touch `AtlasCadPlugin.sln`).
2. **Add the COM reference (required):** Solution Explorer → right-click the
   project → **Add → COM Reference** → tick **`pfcls`** → OK. (It only appears
   after Creo is installed with the VB API feature.) VS generates the interop.
3. Make sure Creo is running with `TOP.asm` open.
4. Press **F5**.

The console prints each step; a copy is written to
`%TEMP%\creo_spike.log` and any STEP output to `%TEMP%\creo_spike_export.stp`.

## What to send back

The full console output (or `%TEMP%\creo_spike.log`). Whatever fails, its
`--- pfcls interfaces matching [...] ---` dump tells us the exact Creo-10
method/property names to use — that's what graduates into `CreoAdapter`.
