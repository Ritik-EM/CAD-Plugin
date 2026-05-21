using System.Reflection;

namespace AtlasCadCore.Utility
{
    /// <summary>
    /// Single source of truth for the running plugin's version, surfaced in
    /// UI footers + auto-update comparisons. Reads from AtlasCadCore's
    /// AssemblyInfo so a single version bump (kept in lockstep with
    /// AtlasSolidWorksAddin's AssemblyInfo and installer/Product.wxs) shows
    /// up everywhere — login form, browse status bar, and the version sent
    /// to the auto-updater.
    /// </summary>
    public static class PluginVersion
    {
        private static readonly System.Version _version =
            typeof(PluginVersion).Assembly.GetName().Version;

        public static System.Version Current => _version;

        /// <summary>
        /// Three-part display string (major.minor.patch) — drops the build
        /// number so users don't see "1.0.0.0" with an irrelevant trailing
        /// zero in the UI. Falls back to "(dev)" if assembly metadata is
        /// somehow missing.
        /// </summary>
        public static string Display =>
            _version == null ? "(dev)" : $"v{_version.ToString(3)}";
    }
}
