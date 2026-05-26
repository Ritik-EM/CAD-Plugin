using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace AtlasCadCore.Utility
{
    public static class FileHashStash
    {
        private static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AtlasCad");
        private static string FilePath => Path.Combine(Dir, "file_hashes.json");

        private static Dictionary<string, Dictionary<string, string>> Load()
        {
            if (!File.Exists(FilePath))
                return new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var json = File.ReadAllText(FilePath);
                return JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(json)
                    ?? new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void Save(Dictionary<string, Dictionary<string, string>> stash)
        {
            if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(stash, Formatting.Indented));
        }

        public static void Store(string rootPartNumber, IDictionary<string, string> partNumberToSha256)
        {
            if (string.IsNullOrEmpty(rootPartNumber) || partNumberToSha256 == null) return;
            var stash = Load();
            stash[rootPartNumber] = new Dictionary<string, string>(
                partNumberToSha256, StringComparer.OrdinalIgnoreCase);
            Save(stash);
        }

        public static IDictionary<string, string> Get(string rootPartNumber)
        {
            if (string.IsNullOrEmpty(rootPartNumber)) return null;
            var stash = Load();
            return stash.TryGetValue(rootPartNumber, out var d) ? d : null;
        }

        public static void Clear(string rootPartNumber)
        {
            if (string.IsNullOrEmpty(rootPartNumber)) return;
            var stash = Load();
            if (stash.Remove(rootPartNumber)) Save(stash);
        }
    }
}
