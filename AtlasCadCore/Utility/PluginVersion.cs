using System.Reflection;

namespace AtlasCadCore.Utility
{
    /// <summary>
    /// Reports the version of the HOST add-in (CATIA / SolidWorks / NX / Altium),
    /// NOT of AtlasCadCore. Every add-in loads the same AtlasCadCore.dll, so reading
    /// Core's own version would make all four report an identical number and rise/fall
    /// together. Each host therefore calls <see cref="SetHost"/> ONCE at startup with
    /// its own assembly, so the version shown in title bars, sent in the User-Agent
    /// header, and compared by the AutoUpdater is that host's alone — fully independent
    /// of the other add-ins. If a host forgets to call it, we fall back to Core's
    /// version (old behaviour) rather than crash.
    /// </summary>
    public static class PluginVersion
    {
        private static System.Version _hostVersion;

        /// <summary>
        /// Pin the version to the calling add-in's own assembly. MUST run before the
        /// first <c>new AtlasApiClient(...)</c> / use of <c>AuthService</c> — both bake
        /// the version into a static HttpClient User-Agent on first touch, so a later
        /// call here would not change an already-created client.
        /// </summary>
        public static void SetHost(Assembly hostAssembly)
        {
            if (hostAssembly != null)
                _hostVersion = hostAssembly.GetName().Version;
        }

        public static System.Version Current =>
            _hostVersion ?? typeof(PluginVersion).Assembly.GetName().Version;

        public static string Display =>
            Current == null ? "(dev)" : $"v{Current.ToString(3)}";
    }
}
