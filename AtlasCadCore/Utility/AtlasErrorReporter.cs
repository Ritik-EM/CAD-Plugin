using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AtlasCadCore.Utility
{
    public static class AtlasErrorReporter
    {
        private static readonly string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AtlasCad");
        private static readonly string LogPath = Path.Combine(LogDir, "errors.log");

        public static string Log(string context, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllText(LogPath,
                    $"--- {DateTime.Now:O} [{context}]\n{ex}\n\n");
            }
            catch { /* logging is best-effort */ }
            return LogPath;
        }

        public static void Show(string title, string context, Exception ex)
        {
            string logPath = Log(context, ex);

            string hresult = "";
            if (ex is COMException com)
                hresult = $"\nHRESULT: 0x{com.HResult:X8}";

            string body =
                $"{ex.Message}\n\n" +
                $"Type: {ex.GetType().FullName}{hresult}\n\n" +
                $"Full details written to:\n  {logPath}";

            MessageBox.Show(body, "Atlas — " + title,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
