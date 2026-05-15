using System;
using System.IO;

namespace AtlasCadPlugin
{
    /// <summary>
    /// Persists the current user's identity in %APPDATA%\AtlasCad\identity.txt.
    /// Demo-only stub — replaces real auth until M1 ships.
    /// </summary>
    public static class IdentityStore
    {
        private static string IdentityDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AtlasCad"
        );
        private static string IdentityFile => Path.Combine(IdentityDir, "identity.txt");

        public static string GetUserName()
        {
            if (!File.Exists(IdentityFile))
                return null;

            string value = File.ReadAllText(IdentityFile).Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        public static void SetUserName(string name)
        {
            if (!Directory.Exists(IdentityDir))
                Directory.CreateDirectory(IdentityDir);
            File.WriteAllText(IdentityFile, name.Trim());
        }
    }
}
