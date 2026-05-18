using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AtlasCadPlugin
{
    /// <summary>
    /// SHA-256 helpers used to attach file integrity hashes to multipart
    /// uploads (so the backend can detect "this file changed since last
    /// version" without comparing the bytes themselves).
    /// </summary>
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

        // Mirrors AtlasApiClient.OpenSharedRead — SolidWorks holds the file
        // open with an exclusive lock, so we have to ask Windows for a
        // shared-read handle.
        private static Stream OpenSharedRead(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }
    }
}
