using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using AtlasCadCore.Adapter;
using AtlasCadCore.ApiClient;
using AtlasCadCore.Auth;
using AtlasCadCore.Forms;
using AtlasCadCore.Utility;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;

namespace AtlasCadPlugin.SolidWorks
{
    /// <summary>
    /// SolidWorks add-in shell. Owns the ribbon + COM registration, then
    /// delegates all business logic to AtlasCadCore via SolidWorksAdapter.
    ///
    /// COM GUID is preserved from the pre-refactor plugin so existing
    /// installations upgrade in place.
    /// </summary>
    [Guid("E3A2B3D4-E5F6-7890-ABCD-EF1234567890")]
    [ComVisible(true)]
    public class SolidWorksAddin : ISwAddin
    {
        private int _addinCookie;
        private ISldWorks _swApp;
        private ICommandManager _cmdManager;
        private AtlasApiClient _api;
        private AuthService _auth;
        private ICadAdapter _adapter;

        private const int CmdIdPing = 0;
        private const int CmdIdUpload = 1;
        private const int CmdIdBrowse = 2;
        private const int CmdIdCheckin = 3;
        private const int CmdIdResolve = 4;
        private const int CmdIdMyCheckouts = 5;
        private const int CmdIdSignOut = 6;

        private const string AtlasBaseUrl = "https://atlas.myeuler.in/";
        private const string OctopusBaseUrl = "https://octopus.eulerlogistics.com";

        public bool ConnectToSW(object ThisSW, int Cookie)
        {
            System.Net.ServicePointManager.SecurityProtocol =
                System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls13;

            _swApp = (ISldWorks)ThisSW;
            _addinCookie = Cookie;
            _swApp.SetAddinCallbackInfo2(0, this, _addinCookie);

            _api = new AtlasApiClient(AtlasBaseUrl);
            _auth = new AuthService(OctopusBaseUrl);
            _adapter = new SolidWorksAdapter(_swApp);

            EnsureAuthenticated();

            _cmdManager = _swApp.GetCommandManager(_addinCookie);
            AddRibbonButtons();

            _ = AutoUpdater.CheckAsync(_api);

            return true;
        }

        public bool DisconnectFromSW()
        {
            _cmdManager.RemoveCommandGroup2(GetCommandGroupId(), true);
            _cmdManager = null;
            _swApp = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return true;
        }

        private int GetCommandGroupId() => 1;

        private void AddRibbonButtons()
        {
            int errors = 0;
            ICommandGroup g = _cmdManager.CreateCommandGroup2(
                GetCommandGroupId(), "Atlas", "Atlas PLM integration", "Atlas",
                -1, true, ref errors);

            int both = (int)swCommandItemType_e.swMenuItem | (int)swCommandItemType_e.swToolbarItem;

            g.AddCommandItem2("Ping Atlas", -1, "Test connection to Atlas backend",
                "Ping", 0, nameof(OnPingClicked), "", CmdIdPing, both);
            g.AddCommandItem2("Upload to Atlas", -1, "Upload the open assembly to part_master_library",
                "Upload", 0, nameof(OnUploadClicked), "", CmdIdUpload, both);
            g.AddCommandItem2("Browse Part Master Library", -1, "Browse, open, insert, or check out parts",
                "Browse", 0, nameof(OnBrowseClicked), "", CmdIdBrowse, both);
            g.AddCommandItem2("Check In", -1, "Check in the currently-checked-out part",
                "Check In", 0, nameof(OnCheckinClicked), "", CmdIdCheckin, both);
            g.AddCommandItem2("Resolve from Atlas", -1, "Download missing child files for the open assembly from atlas",
                "Resolve", 0, nameof(OnResolveClicked), "", CmdIdResolve, both);
            g.AddCommandItem2("My Checkouts", -1, "Show parts you have checked out and release locks",
                "My Checkouts", 0, nameof(OnMyCheckoutsClicked), "", CmdIdMyCheckouts, both);
            g.AddCommandItem2("Sign Out", -1, "Clear stored token and sign in as a different user",
                "Sign Out", 0, nameof(OnSignOutClicked), "", CmdIdSignOut, both);

            g.HasToolbar = true;
            g.HasMenu = true;
            g.Activate();
        }

        private bool EnsureAuthenticated()
        {
            var existing = TokenStore.Current();
            if (existing != null && !existing.IsExpired) return true;
            string preset = existing?.Email;
            if (existing != null && existing.IsExpired) TokenStore.Clear();
            using (var dlg = new LoginForm(_auth, preset))
            {
                return dlg.ShowDialog() == DialogResult.OK;
            }
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
                using (var form = new BrowsePartMasterForm(_api, _adapter)) { form.ShowDialog(); }
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

        public void OnSignOutClicked()
        {
            var prev = TokenStore.Current();
            TokenStore.Clear();
            string who = prev?.Email ?? "(none)";
            MessageBox.Show($"Signed out: {who}\n\nSign-in dialog will appear next.", "Atlas",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            EnsureAuthenticated();
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
            MessageBox.Show("Your session has expired. Please sign in again.",
                "Atlas", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            EnsureAuthenticated();
        }

        [ComRegisterFunction]
        private static void RegisterFunction(Type t)
        {
            using (RegistryKey key = Registry.LocalMachine.CreateSubKey(
                $@"SOFTWARE\SolidWorks\AddIns\{{{t.GUID}}}"))
            {
                key.SetValue(null, 1, RegistryValueKind.DWord);
                key.SetValue("Title", "Atlas");
                key.SetValue("Description", "Atlas — sync CAD files to Atlas PLM");
            }
        }

        [ComUnregisterFunction]
        private static void UnregisterFunction(Type t)
        {
            Registry.LocalMachine.DeleteSubKeyTree(
                $@"SOFTWARE\SolidWorks\AddIns\{{{t.GUID}}}",
                throwOnMissingSubKey: false);
        }
    }
}
