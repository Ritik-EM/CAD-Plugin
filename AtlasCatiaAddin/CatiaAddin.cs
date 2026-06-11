using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using AtlasCadCore.Adapter;
using AtlasCadCore.ApiClient;
using AtlasCadCore.Auth;
using AtlasCadCore.Forms;
using AtlasCadCore.Utility;
using Microsoft.Win32;
using INFITF;

namespace AtlasCadPlugin.Catia
{
    [Guid("D3A2B3D4-E5F6-7890-ABCD-EF1234567891")]
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDual)]
    public class CatiaAddin
    {
        // Fully-qualified — `Application` alone is ambiguous between
        // INFITF.Application (CATIA root automation object) and
        // System.Windows.Forms.Application (the WinForms one we get from
        // `using System.Windows.Forms;`). The CATIA one is what we want.
        private INFITF.Application _catApp;
        private AtlasApiClient _api;
        private AuthService _auth;
        private ICadAdapter _adapter;

        // File queued by the Browse window's "Open STEP File" / "View" buttons to
        // be opened in CATIA AFTER all .NET modal dialogs close — opening it while
        // a ShowDialog loop is active deadlocks CATIA's STA thread. See ShowMenu.
        private string _pendingOpenPath;

        private const string AtlasBaseUrl = "https://atlas.myeuler.in/";
        private const string OctopusBaseUrl = "https://octopus.eulerlogistics.com";

        public CatiaAddin()
        {
            System.Net.ServicePointManager.SecurityProtocol =
                System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls13;
        }

        public void Initialize(INFITF.Application catApp)
        {
            _catApp = catApp;
            _api = new AtlasApiClient(AtlasBaseUrl, "CATIA");
            _auth = new AuthService(OctopusBaseUrl);
            _adapter = new CatiaAdapter(_catApp);

            EnsureAuthenticated();
            _ = AutoUpdater.CheckAsync(_api);
        }

        /// <summary>Mouse-clickable action menu (replaces the type-a-number
        /// InputBox). The macro just calls Initialize then ShowMenu.</summary>
        public void ShowMenu()
        {
            try
            {
                if (!EnsureAuthenticated()) return;
                var actions = new List<MenuAction>
                {
                    new MenuAction("Ping (test connection)", OnPingClicked),
                    new MenuAction("Upload to Atlas", OnUploadClicked),
                    new MenuAction("Browse Part Master Library", OnBrowseClicked),
                    new MenuAction("Check In", OnCheckinClicked),
                    new MenuAction("Resolve from Atlas (fetch missing children)", OnResolveClicked),
                    new MenuAction("My Checkouts", OnMyCheckoutsClicked),
                    new MenuAction("Release Part Code", OnReleasePartNumberClicked),
                    new MenuAction("Sign Out", OnSignOutClicked),
                };
                _pendingOpenPath = null;
                using (var menu = new ActionMenuForm("Atlas — pick an action", actions))
                    menu.ShowDialog();

                // Perform any deferred CATIA document open now that BOTH the menu
                // and the Browse window have closed and no .NET modal loop is on
                // the stack. Doing Documents.Open here — back on CATIA's own
                // message loop — lets CATIA's interactive STEP import report
                // display and dismiss normally instead of deadlocking the STA
                // thread (the "CATIA frozen at 0% CPU after Open STEP File" bug).
                if (!string.IsNullOrEmpty(_pendingOpenPath))
                {
                    string toOpen = _pendingOpenPath;
                    _pendingOpenPath = null;
                    try { _adapter.OpenDocument(toOpen); }
                    catch (Exception ex) { AtlasErrorReporter.Show("Open failed", "ShowMenu.OpenAfterClose", ex); }
                }
            }
            catch (Exception ex) { AtlasErrorReporter.Show("Menu failed", "ShowMenu", ex); }
        }

        public void OnPingClicked() => _ = Run("Ping", async () =>
        {
            string result = await _api.PingAsync();
            var who = TokenStore.Current();
            string identity = who != null ? $"{who.DisplayName} <{who.Email}>" : "(not signed in)";
            MessageBox.Show($"Signed in as: {identity}\n\n{result}", "Atlas — Ping",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        });

        public void OnUploadClicked() => _ = Run("Upload", () => UploadToPartMasterForm.RunAsync(_api, _adapter));

        public void OnBrowseClicked()
        {
            try
            {
                using (var form = new BrowsePartMasterForm(_api, _adapter))
                {
                    form.ShowDialog();
                    // Capture the deferred open; ShowMenu performs it once this
                    // and the menu modal loop have both unwound (see ShowMenu).
                    _pendingOpenPath = form.OpenAfterClose;
                }
            }
            catch (UnauthorizedException) { HandleUnauthorized(); }
            catch (Exception ex) { AtlasErrorReporter.Show("Browse failed", "OnBrowseClicked", ex); }
        }

        public void OnCheckinClicked() => _ = Run("Checkin", () => CheckinFlow.RunAsync(_api, _adapter));

        public void OnResolveClicked() => _ = Run("Resolve", () => ResolveFromAtlasFlow.RunAsync(_api, _adapter));

        public void OnMyCheckoutsClicked()
        {
            try
            {
                using (var form = new MyCheckoutsForm(_api)) { form.ShowDialog(); }
            }
            catch (UnauthorizedException) { HandleUnauthorized(); }
            catch (Exception ex) { AtlasErrorReporter.Show("My Checkouts failed", "OnMyCheckoutsClicked", ex); }
        }

        public void OnReleasePartNumberClicked()
        {
            try
            {
                if (!EnsureAuthenticated()) return;
                using (var form = new ReleasePartNumberForm(_api)) { form.ShowDialog(); }
            }
            catch (UnauthorizedException) { HandleUnauthorized(); }
            catch (Exception ex) { AtlasErrorReporter.Show("Release Part Code failed", "OnReleasePartNumberClicked", ex); }
        }

        public void OnSignOutClicked()
        {
            TokenStore.Clear();
            MessageBox.Show("Signed out. Sign-in will appear next.", "Atlas");
            EnsureAuthenticated();
        }

        private bool EnsureAuthenticated()
        {
            var existing = TokenStore.Current();
            if (existing != null && !existing.IsExpired) return true;
            string preset = existing?.Email;
            if (existing != null && existing.IsExpired) TokenStore.Clear();
            using (var dlg = new LoginForm(_auth, preset))
                return dlg.ShowDialog() == DialogResult.OK;
        }

        private async Task Run(string context, Func<Task> work)
        {
            try { await work(); }
            catch (UnauthorizedException) { HandleUnauthorized(); }
            catch (Exception ex) { AtlasErrorReporter.Show(context, context, ex); }
        }

        private void HandleUnauthorized()
        {
            TokenStore.Clear();
            MessageBox.Show("Session expired. Please sign in again.", "Atlas");
            EnsureAuthenticated();
        }

        [ComRegisterFunction]
        private static void RegisterFunction(Type t)
        {
            using (RegistryKey key = Registry.LocalMachine.CreateSubKey(
                $@"SOFTWARE\DELMIA\CATIA\AddIns\{t.GUID}"))
            {
                key?.SetValue(null, t.FullName);
                key?.SetValue("Description", "Atlas — sync CAD files to Atlas PLM");
            }
        }

        [ComUnregisterFunction]
        private static void UnregisterFunction(Type t)
        {
            Registry.LocalMachine.DeleteSubKeyTree(
                $@"SOFTWARE\DELMIA\CATIA\AddIns\{t.GUID}",
                throwOnMissingSubKey: false);
        }
    }
}
