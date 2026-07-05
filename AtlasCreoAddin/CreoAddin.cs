using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using AtlasCadCore.Adapter;
using AtlasCadCore.ApiClient;
using AtlasCadCore.Auth;
using AtlasCadCore.Forms;
using AtlasCadCore.Utility;
using pfcls;

namespace AtlasCadPlugin.Creo
{
    // Entry point for the Creo integration. Unlike CATIA/SW/NX (in-process add-ins
    // loaded by the CAD), the free Creo VB API is ASYNC: this is a standalone app
    // that attaches to a RUNNING Creo session over COM, then drives the SAME shared
    // flows (UploadToPartMasterForm / CheckinFlow / BrowsePartMasterForm) as every
    // other CAD. A "Check in to Atlas" mapkey/ribbon button in Creo launches this
    // exe (or, later, signals a resident watcher like the Altium bridge).
    //
    // NOTE: won't run on the Educational Edition (no pfcls COM component). Needs a
    // commercial Creo seat with the VB API. VERIFY-marked calls are the ones to
    // confirm against the local vbapidoc / pfcAsynchronousModeExamples.vb.
    internal static class CreoAddin
    {
        private static AtlasApiClient _api;
        private static AuthService _auth;
        private static ICadAdapter _adapter;

        private const string AtlasBaseUrl = "https://atlas.myeuler.in/";
        private const string OctopusBaseUrl = "https://octopus.eulerlogistics.com";

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            System.Net.ServicePointManager.SecurityProtocol =
                System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls13;

            IpfcBaseSession session;
            try
            {
                // Attach to the running Creo session (confirmed pattern from the spike +
                // pfcAsynchronousModeExamples.vb): Connect(user, pass, host, timeoutSeconds).
                IpfcAsyncConnection conn = (new CCpfcAsyncConnection()).Connect(null, null, null, 5);
                session = conn.Session;   // VERIFY (t-pfcAsyncConnection-AsyncConnection.html): .Session
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Could not connect to a running Creo session.\n\n" +
                    "Start Creo Parametric (a commercial seat with the VB API), open your assembly, " +
                    "then re-launch Atlas.\n\n" + ex.Message,
                    "Atlas — Creo");
                return;
            }

            // Pin the reported version to THIS assembly BEFORE the first AtlasApiClient/
            // AuthService use (both bake it into a static User-Agent on first touch).
            PluginVersion.SetHost(typeof(CreoAddin).Assembly);
            _api = new AtlasApiClient(AtlasBaseUrl, "CREO");   // 2nd arg = X-Atlas-Cad-Source
            _auth = new AuthService(OctopusBaseUrl);
            _adapter = new CreoAdapter(session);

            if (!EnsureAuthenticated()) return;
            _ = AutoUpdater.CheckAsync(_api);

            ShowMenu();
        }

        private static void ShowMenu()
        {
            using (var menu = new Form
            {
                Text = "Atlas — Creo",
                Width = 320,
                Height = 340,
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
            })
            {
                var panel = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.TopDown,
                    Padding = new Padding(20),
                    WrapContents = false,
                };
                panel.Controls.Add(MenuButton("Upload to Atlas",
                    () => Run("Upload", () => UploadToPartMasterForm.RunAsync(_api, _adapter))));
                panel.Controls.Add(MenuButton("Browse / Check Out", () =>
                {
                    try { using (var f = new BrowsePartMasterForm(_api, _adapter)) f.ShowDialog(); }
                    catch (UnauthorizedException) { HandleUnauthorized(); }
                    catch (Exception ex) { AtlasErrorReporter.Show("Browse failed", "Browse", ex); }
                }));
                panel.Controls.Add(MenuButton("Check In",
                    () => Run("Checkin", () => CheckinFlow.RunAsync(_api, _adapter))));
                panel.Controls.Add(MenuButton("Release Part Code", () =>
                {
                    try { using (var f = new ReleasePartNumberForm(_api)) f.ShowDialog(); }  // VERIFY: ctor args
                    catch (Exception ex) { AtlasErrorReporter.Show("Release failed", "Release", ex); }
                }));
                panel.Controls.Add(MenuButton("Sign Out", () =>
                {
                    TokenStore.Clear();
                    MessageBox.Show("Signed out.", "Atlas");
                    EnsureAuthenticated();
                }));
                menu.Controls.Add(panel);
                Application.Run(menu);
            }
        }

        private static Button MenuButton(string text, Action onClick)
        {
            var b = new Button { Text = text, Width = 250, Height = 40, Margin = new Padding(4) };
            b.Click += (s, e) => onClick();
            return b;
        }

        private static bool EnsureAuthenticated()
        {
            var existing = TokenStore.Current();
            if (existing != null && !existing.IsExpired) return true;
            string preset = existing?.Email;
            if (existing != null && existing.IsExpired) TokenStore.Clear();
            using (var dlg = new LoginForm(_auth, preset))
                return dlg.ShowDialog() == DialogResult.OK;
        }

        private static async Task Run(string context, Func<Task> work)
        {
            try { await work(); }
            catch (UnauthorizedException) { HandleUnauthorized(); }
            catch (Exception ex) { AtlasErrorReporter.Show(context, context, ex); }
        }

        private static void HandleUnauthorized()
        {
            TokenStore.Clear();
            MessageBox.Show("Session expired. Please sign in again.", "Atlas");
            EnsureAuthenticated();
        }
    }
}
