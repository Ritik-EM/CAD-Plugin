using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using AtlasCadCore.ApiClient;

namespace AtlasCadCore.Utility
{
    public static class AutoUpdater
    {
        public static async Task CheckAsync(AtlasApiClient api)
        {
            try
            {
                var info = await api.LatestVersionAsync();
                if (info == null || string.IsNullOrEmpty(info.version)) return;

                var current = Assembly.GetExecutingAssembly().GetName().Version;
                var latest = TryParse(info.version);
                if (latest == null || current == null) return;
                if (latest <= current) return;

                if (string.IsNullOrEmpty(info.download_url))
                {
                    NotifyAvailable(info.version, msiPath: null);
                    return;
                }

                string targetPath = Path.Combine(Path.GetTempPath(),
                    $"AtlasCadPlugin-{info.version}.msi");
                await api.DownloadFileAsync(info.download_url, targetPath);

                NotifyAvailable(info.version, targetPath);
            }
            catch { /* best effort */ }
        }

        private static Version TryParse(string s)
        {
            try { return new Version(s); }
            catch { return null; }
        }

        private static void NotifyAvailable(string version, string msiPath)
        {
            string body = msiPath != null
                ? $"A new version ({version}) is ready at:\n\n{msiPath}\n\n" +
                  "Quit your CAD application, then double-click the MSI to install."
                : $"A new version ({version}) is available. Contact your admin for the installer.";

            new System.Threading.Thread(() =>
            {
                MessageBox.Show(body, "Atlas — Update Available",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (msiPath != null && File.Exists(msiPath))
                {
                    try { Process.Start("explorer.exe", $"/select,\"{msiPath}\""); } catch { }
                }
            }) { IsBackground = true }.Start();
        }
    }
}
