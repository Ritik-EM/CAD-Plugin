using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
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
        // Set true only by the tray "Quit Atlas" action. Distinguishes a user quit
        // (exit the process) from Creo simply closing (loop back and wait for the
        // next Creo session, so the app re-attaches without a manual relaunch).
        private static volatile bool _userQuit;

        // Depth of the currently-running Atlas flow(s). While > 0 the pump tick skips
        // EventProcess(): that call is a blocking cross-process COM call, and letting
        // it fire (every 200ms) while the flow hides/shows forms or runs a modal dialog
        // wedges the STA thread against Creo's async channel — the "Not Responding"
        // hang at the missing-parts dialog. Outgoing Creo calls the flow makes itself
        // (Save/Walk/STEP export) don't need EventProcess, and await continuations +
        // modal dialogs are serviced by Application.Run / the dialog's own nested loop,
        // so skipping EventProcess for the flow's duration is safe. Depth-counted so a
        // nested flow (e.g. a tray action fired mid-await) doesn't re-enable it early.
        private static int _flowDepth;

        // Held for the process lifetime. A second copy attaching to the SAME Creo
        // session would collide on UICreateCommand (PRO_TK_E_FOUND / XToolkitFound),
        // because the first copy already registered the ATLAS_* commands — and those
        // commands persist in the session until Creo itself closes. Refuse to start a
        // second instance rather than fail half-way through registration.
        private static System.Threading.Mutex _instanceMutex;

        private const string AtlasBaseUrl = "https://atlas.myeuler.in/";
        private const string OctopusBaseUrl = "https://octopus.eulerlogistics.com";

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            System.Net.ServicePointManager.SecurityProtocol =
                System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls13;

            // STA COM re-entrancy guard: our WinForms flows hide/show forms + run modal
            // dialogs, which pump the message loop while the out-of-process Creo COM
            // server may be mid-call. Without this filter a busy/re-entrant cross-process
            // call deadlocks the UI thread (hang at progress.Hide() before a dialog).
            ComMessageFilter.Register();

            // Only one Atlas add-in per machine/session. A second launch would hit
            // XToolkitFound while re-registering the commands the first one already made.
            bool isNew;
            _instanceMutex = new System.Threading.Mutex(true, @"Local\AtlasCreoAddin_SingleInstance", out isNew);
            if (!isNew)
            {
                MessageBox.Show(
                    "Atlas is already running for this Creo session.\n\n" +
                    "Use the Atlas system-tray icon to open the actions. To restart it, right-click " +
                    "that icon > Quit Atlas first, then relaunch.",
                    "Atlas — Creo");
                return;
            }

            // One-time, Creo-independent setup. Pin the reported version to THIS
            // assembly BEFORE the first AtlasApiClient/AuthService use (both bake it
            // into a static User-Agent on first touch).
            PluginVersion.SetHost(typeof(CreoAddin).Assembly);
            // Base URLs default to production but can be overridden for local testing
            // via the ATLAS_BASE_URL / OCTOPUS_BASE_URL environment variables, e.g.
            //   setx ATLAS_BASE_URL http://localhost:8081/
            // (launch a NEW Atlas from a shell where the var is set).
            string atlasUrl = FirstNonEmpty(Environment.GetEnvironmentVariable("ATLAS_BASE_URL"), AtlasBaseUrl);
            string octopusUrl = FirstNonEmpty(Environment.GetEnvironmentVariable("OCTOPUS_BASE_URL"), OctopusBaseUrl);
            Log($"Atlas base URL = {atlasUrl}  (Octopus = {octopusUrl})");
            _api = new AtlasApiClient(atlasUrl, "CREO");   // 2nd arg = X-Atlas-Cad-Source
            _auth = new AuthService(octopusUrl);
            _ = AutoUpdater.CheckAsync(_api);

            // Attach/run loop. WaitForCreo() blocks until a Creo session is available,
            // so this app can sit in the Windows Startup folder, launch at login BEFORE
            // Creo is open, and attach automatically the moment Creo starts — no manual
            // launch. When Creo closes we loop back and wait for the next session, so a
            // Creo restart re-attaches on its own. Exits only when the user picks
            // "Quit Atlas" from the tray.
            while (!_userQuit)
            {
                if (!WaitForCreo()) break;   // false => user quit while waiting

                _adapter = new CreoAdapter((IpfcBaseSession)_session);
                _liveListeners.Clear();      // drop listeners from any prior session
                _terminate = false;

                try { RegisterCommands(); }
                catch (Exception ex)
                {
                    Log("RegisterCommands failed: " + ex.Message);
                    Disconnect();
                    System.Threading.Thread.Sleep(3000);
                    continue;   // transient (Creo still starting up) — retry
                }

                RunEventLoop();   // blocks until Creo terminates or the user quits
                Disconnect();
            }
        }

        // Poll for a running Creo session, attaching as soon as one appears. Lets the
        // app be launched at login (Startup) before Creo is open — it waits quietly
        // and attaches the moment Creo starts. Returns false only if the user quit
        // while waiting (there's no UI during the wait, so that's via process exit).
        private static bool WaitForCreo()
        {
            bool announced = false;
            while (!_userQuit)
            {
                try
                {
                    // Connect(user, pass, host, timeoutSeconds) — pfcAsynchronousModeExamples.vb.
                    _conn = (new CCpfcAsyncConnection()).Connect(null, null, null, 5);
                    _session = _conn.Session;   // IpfcSession (UI methods live here)
                    Log("Attached to a running Creo session");
                    return true;
                }
                catch
                {
                    _conn = null; _session = null;
                    if (!announced) { Log("Waiting for a running Creo session…"); announced = true; }
                }
                System.Threading.Thread.Sleep(3000);
            }
            return false;
        }

        // The five Atlas actions, shared by the ribbon commands and the tray menu.
        // (command name, label msg-key, help msg-key, tray text, handler)
        private static void ForEachCommand(Action<string, string, string, string, Action> visit)
        {
            // labelKey/helpKey MUST match the message keys in text\atlas_creo_msg.txt
            // exactly (Creo message keys are plain tokens — no spaces, no '#').
            visit("ATLAS_UPLOAD",  "ATLAS_UPLOAD_LBL",  "ATLAS_UPLOAD_HELP",  "Upload to Atlas",
                  () => { _ = Run("Upload", () => UploadToPartMasterForm.RunAsync(_api, _adapter)); });
            visit("ATLAS_BROWSE",  "ATLAS_BROWSE_LBL",  "ATLAS_BROWSE_HELP",  "Browse / Check Out",
                  ShowBrowse);
            visit("ATLAS_CHECKIN", "ATLAS_CHECKIN_LBL", "ATLAS_CHECKIN_HELP", "Check In",
                  () => { _ = Run("Checkin", () => CheckinFlow.RunAsync(_api, _adapter)); });
            visit("ATLAS_RELEASE", "ATLAS_RELEASE_LBL", "ATLAS_RELEASE_HELP", "Release Part Code",
                  ShowRelease);
            visit("ATLAS_SIGNOUT", "ATLAS_SIGNOUT_LBL", "ATLAS_SIGNOUT_HELP", "Sign Out",
                  SignOut);
        }

        // ---- register commands in Creo's UI ----------------------------------

        private static void RegisterCommands()
        {
            Log("=== RegisterCommands ===");
            string msgFull = MessageFilePath();
            Log("msgFull=" + msgFull + " exists=" + File.Exists(msgFull));

            // Creo can only load a message file it can find. Our async app isn't
            // registered with a text path, so copy the message file into Creo's current
            // working directory too, and also try that bare filename in Designate().
            string cwd = null;
            try { cwd = ((IpfcBaseSession)_session).GetCurrentDirectory(); } catch (Exception ex) { Log("GetCurrentDirectory failed: " + ex.Message); }
            Log("cwd=" + cwd);
            string cwdMsgName = "atlas_creo_msg.txt";
            if (!string.IsNullOrEmpty(cwd))
            {
                try { File.Copy(msgFull, Path.Combine(cwd, cwdMsgName), true); Log("copied msg file to cwd"); }
                catch (Exception ex) { Log("copy to cwd failed: " + ex.Message); }
            }

            var summary = new StringBuilder();
            ForEachCommand((name, labelKey, helpKey, trayText, onClick) =>
            {
                var listener = new CommandListener(onClick);
                _liveListeners.Add(listener);
                IpfcUICommand cmd;
                try { cmd = _session.UICreateCommand(name, listener); Log(name + ": UICreateCommand OK"); }
                catch (Exception ex) { Log(name + ": UICreateCommand FAILED " + ex.Message); summary.AppendLine(name + " create FAILED"); return; }

                // Designate() is what lists the command under "TOOLKIT Commands". The
                // message-file arg is capped at 40 chars (XStringTooLong on a full path)
                // and the label/help/desc args are MESSAGE KEYS resolved from that file —
                // literal text throws XToolkitMsgNotFound. So: pass the bare filename
                // (Creo finds the copy we dropped in its cwd) + the real keys. The 4th
                // arg (description) is optional; try null first, then reuse the help key.
                var attempts = new (string tag, Action act)[]
                {
                    ("cwd-name+keys+null-desc", () => cmd.Designate(cwdMsgName, labelKey, helpKey, null)),
                    ("cwd-name+keys+help-desc", () => cmd.Designate(cwdMsgName, labelKey, helpKey, helpKey)),
                };
                bool done = false;
                foreach (var a in attempts)
                {
                    if (done) break;
                    try { a.act(); Log(name + ": Designate(" + a.tag + ") OK"); summary.AppendLine(name + ": OK (" + a.tag + ")"); done = true; }
                    catch (Exception ex) { Log(name + ": Designate(" + a.tag + ") FAILED " + ex.Message); }
                }
                if (!done) summary.AppendLine(name + ": all Designate attempts FAILED");
            });

            // If the user has already customized the ribbon, load their layout so the
            // buttons appear this session too.
            TryLoadRibbonFile();

            // Learn when Creo shuts down so we can exit the loop cleanly.
            var term = new AsyncTerminateListener(() => _terminate = true);
            _liveListeners.Add(term);
            try { ((IpfcActionSource)_conn).AddActionListener(term); Log("terminate listener added"); }
            catch (Exception ex) { Log("AddActionListener failed: " + ex.Message); }

            // Debug popup only when ATLAS_DEBUG is set — otherwise registration is
            // silent (this runs on every Creo open now that the app auto-attaches, so
            // a popup each time would be intrusive). The same info is always in the log.
            Log("registration summary: " + summary.ToString().Replace("\n", " | "));
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ATLAS_DEBUG")))
                MessageBox.Show(
                    "Atlas command registration (debug):\n\n" + summary.ToString() +
                    "\nAfter this, open File > Options > Customize Ribbon > Category: TOOLKIT Commands.\n\nLog: " + LogPath(),
                    "Atlas — Creo");
        }

        private static string FirstNonEmpty(string a, string b) =>
            !string.IsNullOrWhiteSpace(a) ? a.Trim() : b;

        private static string LogPath() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AtlasCad", "creo_addin.log");

        internal static void Log(string m)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath()));
                File.AppendAllText(LogPath(), DateTime.Now.ToString("O") + "  " + m + "\n");
            }
            catch { }
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
                // While an Atlas flow is running, do NOT service Creo's event channel.
                // EventProcess() is a blocking cross-process COM call; running it while
                // the flow hides/shows forms or runs a modal dialog wedges the UI thread
                // (the "Not Responding" hang). The timer keeps running so delivery
                // resumes the instant the flow ends — we just no-op each tick until then.
                // (Earlier this Stop()'d the timer for the flow, but stopping/restarting
                // the pump dropped the NEXT command's delivery — Upload ran only once.)
                if (_flowDepth > 0) return;
                _pumpBusy = true;
                try { if (_conn != null && _conn.IsRunning()) _conn.EventProcess(); }
                catch (Exception ex) { Log("pump EventProcess threw: " + ex.Message); }
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
                _userQuit = true;   // stop the attach/run loop — don't re-attach
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

        // Mark a flow active/inactive (see _flowDepth). While _flowDepth > 0 the pump
        // tick no-ops EventProcess so it can't wedge the UI thread; the timer itself
        // keeps running so command delivery resumes the instant the flow ends. Every
        // user-facing handler must be bracketed by these.
        private static void EnterFlow()
        {
            int d = System.Threading.Interlocked.Increment(ref _flowDepth);
            Log($"EnterFlow depth={d}");
        }

        private static void ExitFlow()
        {
            int d = System.Threading.Interlocked.Decrement(ref _flowDepth);
            if (d < 0) { System.Threading.Interlocked.Exchange(ref _flowDepth, 0); d = 0; }
            Log($"ExitFlow depth={d}");
        }

        private static void ShowBrowse()
        {
            EnterFlow();
            try
            {
                if (!EnsureAuthenticated()) return;
                using (var f = new BrowsePartMasterForm(_api, _adapter)) f.ShowDialog();
            }
            catch (UnauthorizedException) { HandleUnauthorized(); }
            catch (Exception ex) { AtlasErrorReporter.Show("Browse failed", "Browse", ex); }
            finally { ExitFlow(); }
        }

        private static void ShowRelease()
        {
            EnterFlow();
            try
            {
                if (!EnsureAuthenticated()) return;
                using (var f = new ReleasePartNumberForm(_api)) f.ShowDialog();
            }
            catch (Exception ex) { AtlasErrorReporter.Show("Release failed", "Release", ex); }
            finally { ExitFlow(); }
        }

        private static void SignOut()
        {
            EnterFlow();
            try
            {
                TokenStore.Clear();
                MessageBox.Show("Signed out.", "Atlas");
            }
            finally { ExitFlow(); }
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
            EnterFlow();
            try
            {
                if (!EnsureAuthenticated()) return;
                try { await work(); }
                catch (UnauthorizedException) { HandleUnauthorized(); }
                catch (Exception ex) { AtlasErrorReporter.Show(context, context, ex); }
            }
            finally { ExitFlow(); }
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
            CreoAddin.Log("OnCommand fired");
            try { _onClick(); }
            catch (Exception ex) { CreoAddin.Log("OnCommand threw: " + ex.Message); MessageBox.Show(ex.Message, "Atlas"); }
        }
    }

    // ---- OLE message filter (STA COM re-entrancy) ----------------------------
    // Our WinForms flows hide/show forms and run modal dialogs, which pump the message
    // loop while the out-of-process Creo COM server may be mid-call. Without a registered
    // message filter, a call the busy server rejects (SERVERCALL_RETRYLATER) deadlocks
    // the STA thread — observed as a hang at progress.Hide() before the missing-parts
    // dialog. Standard Office-automation fix (CoRegisterMessageFilter): retry rejected
    // calls and keep pumping while an outgoing call is pending.
    internal static class ComMessageFilter
    {
        [DllImport("ole32.dll")]
        private static extern int CoRegisterMessageFilter(IOleMessageFilter newFilter, out IOleMessageFilter oldFilter);

        // Must run on the STA thread.
        public static void Register()
        {
            try { CoRegisterMessageFilter(new Filter(), out _); } catch { /* best-effort */ }
        }

        [ComImport, Guid("00000016-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IOleMessageFilter
        {
            [PreserveSig] int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);
            [PreserveSig] int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);
            [PreserveSig] int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
        }

        private sealed class Filter : IOleMessageFilter
        {
            // SERVERCALL_ISHANDLED (0): accept incoming calls.
            public int HandleInComingCall(int callType, IntPtr caller, int tick, IntPtr info) => 0;

            // rejectType 2 == SERVERCALL_RETRYLATER (server busy). Return >=100 to retry
            // after that many ms; -1 cancels. Retry for up to 60s, then give up.
            public int RetryRejectedCall(IntPtr callee, int tick, int rejectType)
                => (rejectType == 2 && tick < 60000) ? 200 : -1;

            // PENDINGMSG_WAITDEFPROCESS (2): keep waiting for the reply while letting
            // default message processing run, so the UI + dialogs stay alive.
            public int MessagePending(IntPtr callee, int tick, int pendingType) => 2;
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
