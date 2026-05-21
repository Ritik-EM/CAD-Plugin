using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AtlasCadCore.Utility
{
    public static class FileHashing
    {
        public static string Sha256Hex(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = OpenSharedRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // Files that are open in a CAD host (any of SW/CATIA/NX) have an
        // exclusive read lock. FileShare.ReadWrite | Delete tells Windows
        // we're fine reading while the host holds the file.
        private static Stream OpenSharedRead(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }
    }
}
