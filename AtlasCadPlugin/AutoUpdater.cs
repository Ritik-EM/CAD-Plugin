using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AtlasCadPlugin
{
    /// <summary>
    /// Checks atlas-api for a newer plugin version on startup. If the server
    /// reports a version &gt; the one embedded in this assembly, downloads the
    /// MSI to %TEMP% and shows the user a notification with a button to
    /// "Run Installer" (which closes the plugin's hook on the file by
    /// instructing the user to quit SolidWorks first — MSI can't replace
    /// a loaded DLL otherwise).
    /// </summary>
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
            catch (Exception)
            {
                // Auto-update is best-effort — never block plugin startup
                // because the version check failed.
            }
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
                  "Quit SolidWorks, then double-click the MSI to install."
                : $"A new version ({version}) is available. Contact your admin for the installer.";

            // Show on a background thread so the modal MessageBox doesn't block
            // the SolidWorks UI thread during ConnectToSW.
            new System.Threading.Thread(() =>
            {
                MessageBox.Show(body, "Atlas — Update Available",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (msiPath != null && File.Exists(msiPath))
                {
                    try { Process.Start("explorer.exe", $"/select,\"{msiPath}\""); }
                    catch { /* explorer.exe is best-effort */ }
                }
            }) { IsBackground = true }.Start();
        }
    }
}
