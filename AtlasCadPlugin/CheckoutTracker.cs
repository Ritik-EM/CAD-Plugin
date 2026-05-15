using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace AtlasCadPlugin
{
    /// <summary>
    /// Maps locally-checked-out file paths back to their assembly_id, so when
    /// the user clicks "Check In" we know which Atlas record to upload to.
    /// Persisted at %APPDATA%\AtlasCad\active_checkouts.json.
    /// </summary>
    public static class CheckoutTracker
    {
        private static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AtlasCad"
        );
        private static string FilePath => Path.Combine(Dir, "active_checkouts.json");

        private static Dictionary<string, string> Load()
        {
            if (!File.Exists(FilePath))
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var json = File.ReadAllText(FilePath);
                return JsonConvert.DeserializeObject<Dictionary<string, string>>(json)
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                // Corrupt file — start fresh rather than crash the plugin.
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void Save(Dictionary<string, string> map)
        {
            if (!Directory.Exists(Dir))
                Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(map, Formatting.Indented));
        }

        public static void Track(string rootFilePath, string assemblyId)
        {
            var map = Load();
            map[rootFilePath] = assemblyId;
            Save(map);
        }

        public static string GetAssemblyId(string rootFilePath)
        {
            var map = Load();
            return map.TryGetValue(rootFilePath, out var id) ? id : null;
        }

        public static void Untrack(string rootFilePath)
        {
            var map = Load();
            if (map.Remove(rootFilePath))
                Save(map);
        }
    }
}
