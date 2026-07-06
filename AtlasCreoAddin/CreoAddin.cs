using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
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
    // Entry point for the Creo integration. The free Creo VB API is ASYNC, so this
    // is a standalone .exe that attaches to a RUNNING Creo session over COM and then
    // runs in FULL ASYNCHRONOUS MODE: it registers Atlas commands in Creo's UI
    // (via UICreateCommand + Designate), pumps Creo's event loop so button clicks
    // call back into this process, and stays resident while Creo is open. The same
    // actions are also on a system-tray menu (works before the one-time ribbon
    // customization, and gives a clean Quit).
    //
    // Getting the buttons onto the ribbon is a ONE-TIME Creo step (they can't be
    // placed programmatically): with this app running, in Creo do
    //   File > Options > Customize Ribbon > Choose commands from "TOOLKIT Commands"
    //   > add the Atlas commands to a tab/group
    //   > Import/Export > "Save the Auxiliary Application User Interface"
    // (needs config option tk_enable_ribbon_custom_save = yes). That writes
    // text\toolkitribbonui.rbn next to this exe, which is auto-loaded on later runs.
    //
    // All button handlers drive the SAME shared Atlas WinForms flows as every other
    // CAD (UploadToPartMasterForm / BrowsePartMasterForm / CheckinFlow /
    // ReleasePartNumberForm). Won't run on the Educational Edition (no pfcls COM).
    internal static class CreoAddin
    {
        private static AtlasApiClient _api;
        private static AuthService _auth;
        private static ICadAdapter _adapter;

        // Held so we can Disconnect() from Creo on exit. Detaching cleanly frees the
        // async channel; if the process dies without it, Creo's async listener is left
        // half-open and the NEXT launch faults on Connect() with RPC_E_SERVERFAULT.
        private static IpfcAsyncConnection _conn;
        private static IpfcSession _session;

        // COM callback objects handed to Creo must stay rooted, or the GC collects
        // them while Creo still holds references and the next callback crashes.
        private static readonly List<object> _liveListeners = new List<object>();

        private static System.Windows.Forms.Timer _pump;
        private static bool _pumpBusy;
        private static bool _terminate;

        private const string AtlasBaseUrl = "https://atlas.myeuler.in/";
        private const string OctopusBaseUrl = "https://octopus.eulerlogistics.com";

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            System.Net.ServicePointManager.SecurityProtocol =
                System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls13;

            try
            {
                // Attach to the running Creo session (pfcAsynchronousModeExamples.vb):
                // Connect(user, pass, host, timeoutSeconds).
                _conn = (new CCpfcAsyncConnection()).Connect(null, null, null, 5);
                _session = _conn.Session;   // IpfcSession (UI methods live here)
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
            _adapter = new CreoAdapter((IpfcBaseSession)_session);

            _ = AutoUpdater.CheckAsync(_api);

            try { RegisterCommands(); }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to register Atlas commands in Creo.\n\n" + ex.Message, "Atlas — Creo");
                Disconnect();
                return;
            }

            RunEventLoop();   // blocks until Creo terminates or the user quits
            Disconnect();
        }

        // The five Atlas actions, shared by the ribbon commands and the tray menu.
        // (command name, label msg-key, help msg-key, tray text, handler)
        private static void ForEachCommand(Action<string, string, string, string, Action> visit)
        {
            visit("ATLAS_UPLOAD",  "USER Atlas Upload",  "USER Atlas Upload Help",  "Upload to Atlas",
                  () => { _ = Run("Upload", () => UploadToPartMasterForm.RunAsync(_api, _adapter)); });
            visit("ATLAS_BROWSE",  "USER Atlas Browse",  "USER Atlas Browse Help",  "Browse / Check Out",
                  ShowBrowse);
            visit("ATLAS_CHECKIN", "USER Atlas Checkin", "USER Atlas Checkin Help", "Check In",
                  () => { _ = Run("Checkin", () => CheckinFlow.RunAsync(_api, _adapter)); });
            visit("ATLAS_RELEASE", "USER Atlas Release", "USER Atlas Release Help", "Release Part Code",
                  ShowRelease);
            visit("ATLAS_SIGNOUT", "USER Atlas Signout", "USER Atlas Signout Help", "Sign Out",
                  SignOut);
        }

        // ---- register commands in Creo's UI ----------------------------------

        private static void RegisterCommands()
        {
            string msgFile = MessageFilePath();

            ForEachCommand((name, labelKey, helpKey, _trayText, onClick) =>
            {
                var listener = new CommandListener(onClick);
                _liveListeners.Add(listener);
                IpfcUICommand cmd = _session.UICreateCommand(name, listener);
                try
                {
                    // Designate makes the command appear under "TOOLKIT Commands" in
                    // Creo's Customize Ribbon dialog so the user can place it.
                    cmd.Designate(msgFile, labelKey, helpKey, null);
                }
                catch { /* label file unresolved — command still exists, shows its key */ }
            });

            // If the user has already customized the ribbon, load their layout so the
            // buttons appear this session too.
            TryLoadRibbonFile();

            // Learn when Creo shuts down so we can exit the loop cleanly.
            var term = new AsyncTerminateListener(() => _terminate = true);
            _liveListeners.Add(term);
            ((IpfcActionSource)_conn).AddActionListener(term);
        }

        private static void TryLoadRibbonFile()
        {
            try
            {
                string rbn = Path.Combine(AppDir(), "text", "toolkitribbonui.rbn");
                if (File.Exists(rbn)) _session.RibbonDefinitionfileLoad(rbn);
            }
            catch { /* not customized yet — commands live under Customize Ribbon + the tray */ }
        }

        // ---- full-async event loop + resident tray ---------------------------

        private static void RunEventLoop()
        {
            // Pump Creo's async events on the STA UI thread (must NOT be a threadpool
            // timer). Re-entrancy guard: modal Atlas dialogs run their own message
            // loop, which would otherwise fire this tick again mid-EventProcess.
            _pump = new System.Windows.Forms.Timer { Interval = 200 };
            _pump.Tick += (s, e) =>
            {
                if (_terminate) { _pump.Stop(); Application.ExitThread(); return; }
                if (_pumpBusy) return;
                _pumpBusy = true;
                try { if (_conn != null && _conn.IsRunning()) _conn.EventProcess(); }
                catch { /* transient async hiccup */ }
                finally { _pumpBusy = false; }
            };
            _pump.Start();

            using (var tray = BuildTray())
            using (var host = new HiddenHost())
            {
                Application.Run(host);   // resident until Creo closes / user Quits
                tray.Visible = false;
            }
        }

        private static NotifyIcon BuildTray()
        {
            var menu = new ContextMenuStrip();
            ForEachCommand((_n, _l, _h, trayText, onClick) =>
                menu.Items.Add(trayText, null, (s, e) => onClick()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit Atlas", null, (s, e) =>
            {
                _pump?.Stop();
                Application.ExitThread();
            });

            Icon ico;
            try { ico = Icon.ExtractAssociatedIcon(typeof(CreoAddin).Assembly.Location); }
            catch { ico = SystemIcons.Application; }

            return new NotifyIcon
            {
                Icon = ico,
                Text = "Atlas — Creo",
                Visible = true,
                ContextMenuStrip = menu,
            };
        }

        // A zero-size, never-shown form: provides the STA message pump the Atlas
        // dialogs + the event-loop timer need, and keeps the process resident.
        private sealed class HiddenHost : Form
        {
            public HiddenHost()
            {
                ShowInTaskbar = false;
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-4000, -4000);
                Size = new Size(1, 1);
                Opacity = 0;
            }
            protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);
        }

        // ---- flows / helpers -------------------------------------------------

        private static void ShowBrowse()
        {
            if (!EnsureAuthenticated()) return;
            try { using (var f = new BrowsePartMasterForm(_api, _adapter)) f.ShowDialog(); }
            catch (UnauthorizedException) { HandleUnauthorized(); }
            catch (Exception ex) { AtlasErrorReporter.Show("Browse failed", "Browse", ex); }
        }

        private static void ShowRelease()
        {
            if (!EnsureAuthenticated()) return;
            try { using (var f = new ReleasePartNumberForm(_api)) f.ShowDialog(); }
            catch (Exception ex) { AtlasErrorReporter.Show("Release failed", "Release", ex); }
        }

        private static void SignOut()
        {
            TokenStore.Clear();
            MessageBox.Show("Signed out.", "Atlas");
        }

        private static string AppDir() => Path.GetDirectoryName(typeof(CreoAddin).Assembly.Location);
        private static string MessageFilePath() => Path.Combine(AppDir(), "text", "atlas_creo_msg.txt");

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
            if (!EnsureAuthenticated()) return;
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

        // Detach from Creo WITHOUT ending its process (Disconnect, not End). Frees the
        // async channel so the next launch can Connect() again.
        private static void Disconnect()
        {
            try { if (_conn != null && _conn.IsRunning()) _conn.Disconnect(2); }
            catch { /* best-effort cleanup */ }
            finally { _conn = null; }
        }
    }

    // ---- COM callback listeners (rooted in _liveListeners) --------------------

    // Fires when the user clicks an Atlas ribbon button. GetClientInterfaceName tells
    // pfc which interface to route through (PTC's "CIP" mechanism).
    internal sealed class CommandListener : IpfcUICommandActionListener, ICIPClientObject, IpfcActionListener
    {
        private readonly Action _onClick;
        public CommandListener(Action onClick) { _onClick = onClick; }
        public string GetClientInterfaceName() => "IpfcUICommandActionListener";
        public void OnCommand()
        {
            try { _onClick(); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Atlas"); }
        }
    }

    // Fires when Creo Parametric is shutting down, so we can leave the event loop.
    internal sealed class AsyncTerminateListener : IpfcAsyncActionListener, ICIPClientObject, IpfcActionListener
    {
        private readonly Action _onTerminate;
        public AsyncTerminateListener(Action onTerminate) { _onTerminate = onTerminate; }
        public string GetClientInterfaceName() => "IpfcAsyncActionListener";
        public void OnTerminate(int _Status) { try { _onTerminate(); } catch { } }
    }
}
