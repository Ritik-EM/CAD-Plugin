using System;
using System.IO;
using System.Net;
using System.Threading;
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
    ///
    /// Two modes:
    ///   (default / --manifest &lt;path&gt;)  one-shot: process a single manifest and exit.
    ///   --watch                          stay resident, watch the exchange dir, and process
    ///                                    each check-in the DelphiScript drops there. This is
    ///                                    what makes check-in one-click: the script writes a
    ///                                    request and the always-running watcher uploads it
    ///                                    (DelphiScript on this Altium build can't launch an EXE).
    ///
    /// Reuses AtlasCadCore (AtlasApiClient, Auth/TokenStore, the shared LoginForm) exactly like
    /// the SolidWorks/CATIA/NX add-ins.
    /// </summary>
    internal static class Program
    {
        private const string AtlasBaseUrl = "https://atlas.myeuler.in/";
        private const string OctopusBaseUrl = "https://octopus.eulerlogistics.com";

        // Exchange-dir filenames shared with AtlasCheckin.pas.
        private const string ManifestName  = "manifest.json";
        private const string TriggerName    = "request.trigger";   // script drops this AFTER manifest.json
        private const string AliveName      = "watcher.alive";     // liveness flag the script checks
        private const string ResultName     = "result.json";
        private const string PartCodeName   = "current_part_code.txt";
        private const string WatcherMutex   = "AtlasAltiumBridgeWatcher";   // single-instance (per session)

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;

            return HasFlag(args, "--watch") ? RunWatch() : RunOnce(args);
        }

        // ---- watch mode: stay resident and process each dropped request ----

        private static int RunWatch()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, WatcherMutex, out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("The Atlas Altium watcher is already running.",
                        "Atlas Altium watcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                string dir = DefaultExchangeDir();
                try { Directory.CreateDirectory(dir); } catch { /* best effort */ }
                string alivePath    = Path.Combine(dir, AliveName);
                string triggerPath  = Path.Combine(dir, TriggerName);
                string manifestPath = Path.Combine(dir, ManifestName);

                try
                {
                    // Auth happens lazily on the first real request, so the watcher starts
                    // silently (no surprise login dialog at Windows startup).
                    while (true)
                    {
                        try { File.WriteAllText(alivePath, DateTime.Now.ToString("o")); } catch { }

                        if (File.Exists(triggerPath))
                        {
                            // The script writes manifest.json fully BEFORE the trigger, so a
                            // present trigger means a complete manifest. Claim the trigger first
                            // (even on a bad manifest) so one failure can't spin the loop.
                            AltiumManifest manifest = TryReadManifest(manifestPath);
                            TryDelete(triggerPath);

                            if (manifest == null)
                            {
                                WriteResultObject(dir, MakeResult(null, false,
                                    "Could not read the check-in request (manifest.json missing or invalid)."));
                                ShowSummary(MakeResult(null, false,
                                    "Could not read the check-in request — see manifest.json."), background: true);
                            }
                            else
                            {
                                var result = ProcessRequest(manifest, dir);
                                ShowSummary(result, background: true);   // never block the watch loop
                            }
                        }

                        Thread.Sleep(1500);
                    }
                }
                finally
                {
                    TryDelete(alivePath);   // clean-exit only; a force-kill leaves a stale flag
                }
            }
        }

        // ---- one-shot mode (manual / --manifest) ----

        private static int RunOnce(string[] args)
        {
            string manifestPath = GetArg(args, "--manifest");
            if (string.IsNullOrEmpty(manifestPath))
                manifestPath = Path.Combine(DefaultExchangeDir(), ManifestName);
            string exchangeDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath));

            if (!File.Exists(manifestPath))
            {
                MessageBox.Show("Manifest not found:\n" + manifestPath,
                    "Atlas Altium", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }

            AltiumManifest manifest = TryReadManifest(manifestPath);
            if (manifest == null)
            {
                MessageBox.Show("Could not read the manifest:\n" + manifestPath,
                    "Atlas Altium", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }

            // Claim the request so a running watcher doesn't also process it.
            TryDelete(Path.Combine(exchangeDir, TriggerName));

            var result = ProcessRequest(manifest, exchangeDir);
            ShowSummary(result, background: false);
            return result.ok ? 0 : 1;
        }

        // ---- the actual check-in (shared by both modes); never throws ----

        private static AltiumResult ProcessRequest(AltiumManifest manifest, string exchangeDir)
        {
            try
            {
                if (!EnsureAuthenticated())
                {
                    var cancelled = MakeResult(manifest, false, "Sign-in cancelled — nothing uploaded.");
                    WriteResultObject(exchangeDir, cancelled);
                    return cancelled;
                }

                var api = new AtlasApiClient(AtlasBaseUrl, "Altium");
                var result = AltiumCheckinFlow.RunAsync(api, manifest, exchangeDir)
                                              .GetAwaiter().GetResult();
                WriteResultObject(exchangeDir, result);
                WritePartCodeForward(exchangeDir, result);
                return result;
            }
            catch (UnauthorizedException)
            {
                TokenStore.Clear();   // next request re-prompts login
                var expired = MakeResult(manifest, false,
                    "Atlas session expired — sign in again, then re-run the check-in.");
                WriteResultObject(exchangeDir, expired);
                return expired;
            }
            catch (Exception ex)
            {
                AtlasErrorReporter.Log("AltiumBridge.ProcessRequest", ex);
                var failed = MakeResult(manifest, false, "Failed: " + ex.Message);
                WriteResultObject(exchangeDir, failed);
                return failed;
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

        // ---- result + UI helpers ----

        private static AltiumResult MakeResult(AltiumManifest m, bool ok, string message)
        {
            return new AltiumResult
            {
                ok = ok,
                operation = m?.operation,
                part_code = m?.part_code,
                message = message,
                warnings = m?.warnings,
            };
        }

        private static void WritePartCodeForward(string exchangeDir, AltiumResult result)
        {
            // Carry-forward: drop the new root revision where the Altium script picks it up
            // on the next check-in to advance the project's AtlasPartCode.
            if (result != null && result.ok && !string.IsNullOrEmpty(result.new_root_part_number))
            {
                try { File.WriteAllText(Path.Combine(exchangeDir, PartCodeName), result.new_root_part_number); }
                catch { /* best effort */ }
            }
        }

        private static void ShowSummary(AltiumResult r, bool background)
        {
            string body = r?.message ?? (r != null && r.ok ? "Done." : "Failed.");
            if (r != null && r.bumped != null && r.bumped.Count > 0)
                body += "\n\nRevisions:\n  " + string.Join("\n  ", r.bumped);
            if (r != null && r.warnings != null && r.warnings.Count > 0)
                body += "\n\nWarnings:\n  " + string.Join("\n  ", r.warnings);

            string title = "Atlas Altium — " + (r?.operation ?? "check-in");
            var icon = (r != null && r.ok) ? MessageBoxIcon.Information : MessageBoxIcon.Warning;

            if (background)
            {
                // Show on a throwaway STA thread so a result dialog never blocks the watch loop.
                var t = new Thread(() => MessageBox.Show(body, title, MessageBoxButtons.OK, icon));
                t.IsBackground = true;
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
            }
            else
            {
                MessageBox.Show(body, title, MessageBoxButtons.OK, icon);
            }
        }

        private static AltiumManifest TryReadManifest(string path)
        {
            try { return JsonConvert.DeserializeObject<AltiumManifest>(File.ReadAllText(path)); }
            catch { return null; }
        }

        private static void WriteResultObject(string exchangeDir, AltiumResult result)
        {
            try
            {
                File.WriteAllText(Path.Combine(exchangeDir, ResultName),
                    JsonConvert.SerializeObject(result, Formatting.Indented));
            }
            catch { /* result.json is best-effort feedback; never fail the run over it */ }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        // ---- arg + path helpers ----

        private static bool HasFlag(string[] args, string flag)
        {
            foreach (var a in args)
                if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

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
