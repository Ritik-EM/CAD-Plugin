using System.Reflection;

namespace AtlasCadCore.Utility
{
    public static class PluginVersion
    {
        private static readonly System.Version _version =
            typeof(PluginVersion).Assembly.GetName().Version;

        public static System.Version Current => _version;

        public static string Display =>
            _version == null ? "(dev)" : $"v{_version.ToString(3)}";
    }
}
