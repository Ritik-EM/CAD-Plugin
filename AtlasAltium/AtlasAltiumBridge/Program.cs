using System;
using System.IO;
using System.Net;
using System.Windows.Forms;
using AtlasCadCore.ApiClient;
using AtlasCadCore.Auth;
using AtlasCadCore.Forms;
using AtlasCadCore.Utility;
using Newtonsoft.Json;

namespace AtlasCadPlugin.Altium
{
    /// <summary>
    /// AtlasAltiumBridge.exe — the out-of-process .NET half of the Altium integration.
    /// Launched per check-in by the DelphiScript (AtlasCheckin.pas) with --manifest &lt;path&gt;.
    /// Reads the manifest, ensures the user is signed in (reusing the shared LoginForm),
    /// runs AltiumCheckinFlow against AtlasCadCore's AtlasApiClient, and writes result.json.
    ///
    /// Same backend URLs and "source" pattern as the SolidWorks/CATIA/NX add-ins.
    /// </summary>
    internal static class Program
    {
        private const string AtlasBaseUrl = "https://atlas.myeuler.in/";
        private const string OctopusBaseUrl = "https://octopus.eulerlogistics.com";

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;

            string manifestPath = GetArg(args, "--manifest");
            if (string.IsNullOrEmpty(manifestPath))
                manifestPath = Path.Combine(DefaultExchangeDir(), "manifest.json");

            string exchangeDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath));

            if (!File.Exists(manifestPath))
            {
                MessageBox.Show("Manifest not found:\n" + manifestPath,
                    "Atlas Altium", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }

            AltiumManifest manifest;
            try
            {
                manifest = JsonConvert.DeserializeObject<AltiumManifest>(File.ReadAllText(manifestPath));
            }
            catch (Exception ex)
            {
                AtlasErrorReporter.Show("Atlas Altium — bad manifest", "AltiumBridge.Parse", ex);
                return 2;
            }

            try
            {
                if (!EnsureAuthenticated())
                {
                    WriteResult(exchangeDir, manifest, false, "Sign-in cancelled — nothing uploaded.");
                    return 3;
                }

                var api = new AtlasApiClient(AtlasBaseUrl, "Altium");
                var result = AltiumCheckinFlow.RunAsync(api, manifest, exchangeDir)
                                              .GetAwaiter().GetResult();

                WriteResultObject(exchangeDir, result);
                // Carry-forward: drop the new root revision where the Altium script picks it up
                // on the next check-in to advance the project's AtlasPartCode.
                if (result.ok && !string.IsNullOrEmpty(result.new_root_part_number))
                {
                    try { File.WriteAllText(Path.Combine(exchangeDir, "current_part_code.txt"), result.new_root_part_number); }
                    catch { /* carry-forward is best-effort */ }
                }
                ShowSummary(result);
                return result.ok ? 0 : 1;
            }
            catch (UnauthorizedException)
            {
                TokenStore.Clear();
                MessageBox.Show("Your Atlas session has expired. Please sign in again and re-run check-in.",
                    "Atlas Altium", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                WriteResult(exchangeDir, manifest, false, "Session expired — signed out.");
                return 3;
            }
            catch (Exception ex)
            {
                AtlasErrorReporter.Show("Atlas Altium check-in failed", "AltiumBridge.Run", ex);
                WriteResult(exchangeDir, manifest, false, "Failed: " + ex.Message);
                return 1;
            }
        }

        /// <summary>Mirror of the add-ins' EnsureAuthenticated: reuse the shared LoginForm.</summary>
        private static bool EnsureAuthenticated()
        {
            var existing = TokenStore.Current();
            if (existing != null && !existing.IsExpired) return true;

            string preset = existing?.Email;
            if (existing != null && existing.IsExpired) TokenStore.Clear();

            var auth = new AuthService(OctopusBaseUrl);
            using (var dlg = new LoginForm(auth, preset))
            {
                return dlg.ShowDialog() == DialogResult.OK;
            }
        }

        private static void ShowSummary(AltiumResult r)
        {
            string body = r.message ?? (r.ok ? "Done." : "Failed.");
            if (r.bumped != null && r.bumped.Count > 0)
                body += "\n\nRevisions:\n  " + string.Join("\n  ", r.bumped);
            if (r.warnings != null && r.warnings.Count > 0)
                body += "\n\nWarnings:\n  " + string.Join("\n  ", r.warnings);

            MessageBox.Show(body, "Atlas Altium — " + (r.operation ?? "check-in"),
                MessageBoxButtons.OK, r.ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        // ---- result.json helpers (read back by the DelphiScript) ----

        private static void WriteResultObject(string exchangeDir, AltiumResult result)
        {
            try
            {
                File.WriteAllText(Path.Combine(exchangeDir, "result.json"),
                    JsonConvert.SerializeObject(result, Formatting.Indented));
            }
            catch { /* result.json is best-effort feedback; never fail the run over it */ }
        }

        private static void WriteResult(string exchangeDir, AltiumManifest m, bool ok, string message)
        {
            WriteResultObject(exchangeDir, new AltiumResult
            {
                ok = ok,
                operation = m?.operation,
                part_code = m?.part_code,
                message = message,
                warnings = m?.warnings,
            });
        }

        // ---- arg + path helpers ----

        private static string GetArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        private static string DefaultExchangeDir()
        {
            string env = Environment.GetEnvironmentVariable("ATLAS_ALTIUM_DIR");
            return !string.IsNullOrEmpty(env) ? env : @"C:\Users\Public\AtlasAltium";
        }
    }
}
