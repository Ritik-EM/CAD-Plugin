using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("AtlasCreoAddin")]
[assembly: AssemblyDescription("Atlas PLM integration for PTC Creo Parametric (VB API, async COM)")]
[assembly: AssemblyProduct("AtlasCreoAddin")]
[assembly: ComVisible(false)]

// Independent version track (per-host, via PluginVersion.SetHost). Do NOT couple
// to AtlasCadCore or the other add-ins — bump only this + the Creo MSI +
// _LATEST_BY_SOURCE["CREO"] when shipping a Creo update.
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
