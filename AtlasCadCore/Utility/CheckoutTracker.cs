using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace AtlasCadCore.Utility
{
    public static class CheckoutTracker
    {
        private static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AtlasCad");
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
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void Save(Dictionary<string, string> map)
        {
            if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(map, Formatting.Indented));
        }

        public static void Track(string rootFilePath, string partNumber)
        {
            var map = Load();
            map[rootFilePath] = partNumber;
            Save(map);
        }

        public static string GetPartNumber(string rootFilePath)
        {
            var map = Load();
            return map.TryGetValue(rootFilePath, out var pn) ? pn : null;
        }

        public static string ResolvePartNumberForPath(string activeFilePath)
        {
            if (string.IsNullOrEmpty(activeFilePath)) return null;
            var map = Load();

            if (map.TryGetValue(activeFilePath, out var pn)) return pn;

            string activeDir = Path.GetDirectoryName(activeFilePath);
            if (string.IsNullOrEmpty(activeDir)) return null;
            activeDir = activeDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            foreach (var kv in map)
            {
                string trackedDir = Path.GetDirectoryName(kv.Key);
                if (string.IsNullOrEmpty(trackedDir)) continue;
                trackedDir = trackedDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(trackedDir, activeDir, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            }
            return null;
        }

        public static IReadOnlyDictionary<string, string> Snapshot() => Load();

        public static void Untrack(string rootFilePath)
        {
            var map = Load();
            if (map.Remove(rootFilePath)) Save(map);
        }

        public static void UntrackByPartNumber(string partNumber)
        {
            if (string.IsNullOrEmpty(partNumber)) return;
            var map = Load();
            var toRemove = new List<string>();
            foreach (var kv in map)
            {
                if (string.Equals(kv.Value, partNumber, StringComparison.OrdinalIgnoreCase))
                    toRemove.Add(kv.Key);
            }
            if (toRemove.Count == 0) return;
            foreach (var key in toRemove) map.Remove(key);
            Save(map);
        }
    }
}
