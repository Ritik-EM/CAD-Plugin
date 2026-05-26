using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using AtlasCadCore.Adapter;
using AtlasCadCore.ApiClient;
using AtlasCadCore.Auth;
using AtlasCadCore.Forms;
using AtlasCadCore.Utility;
using NXOpen;
using NXOpen.MenuBar;

namespace AtlasCadPlugin.Nx
{
    public static class NxAddin
    {
        private static AtlasApiClient _api;
        private static AuthService _auth;
        private static ICadAdapter _adapter;

        private const string AtlasBaseUrl = "https://atlas.myeuler.in/";
        private const string OctopusBaseUrl = "https://octopus.eulerlogistics.com";

        public static int Menu_Startup(MenuBarManager menuBarManager)
        {
            try
            {
                System.Net.ServicePointManager.SecurityProtocol =
                    System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls13;

                _api = new AtlasApiClient(AtlasBaseUrl);
                _auth = new AuthService(OctopusBaseUrl);
                _adapter = new NxAdapter();

                menuBarManager.AddMenuButtonAction("ATLAS_PING_ACT", OnPingClicked);
                menuBarManager.AddMenuButtonAction("ATLAS_UPLOAD_ACT", OnUploadClicked);
                menuBarManager.AddMenuButtonAction("ATLAS_BROWSE_ACT", OnBrowseClicked);
                menuBarManager.AddMenuButtonAction("ATLAS_CHECKIN_ACT", OnCheckinClicked);
                menuBarManager.AddMenuButtonAction("ATLAS_SIGNOUT_ACT", OnSignOutClicked);

                EnsureAuthenticated();
                _ = AutoUpdater.CheckAsync(_api);

                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Atlas startup failed:\n\n" + ex, "Atlas");
                return 1;
            }
        }

        public static int Menu_Cleanup()
        {
            return 0;
        }

        public static MenuButtonEvent OnPingClicked(string buttonName, IntPtr handle)
        {
            _ = Run(async () =>
            {
                string result = await _api.PingAsync();
                var who = TokenStore.Current();
                string identity = who != null ? $"{who.DisplayName} <{who.Email}>" : "(not signed in)";
                MessageBox.Show($"Signed in as: {identity}\n\n{result}", "Atlas — Ping");
            });
            return MenuButtonEvent.Activate;
        }

        public static MenuButtonEvent OnUploadClicked(string buttonName, IntPtr handle)
        {
            _ = Run(() => UploadToPartMasterForm.RunAsync(_api, _adapter));
            return MenuButtonEvent.Activate;
        }

        public static MenuButtonEvent OnBrowseClicked(string buttonName, IntPtr handle)
        {
            try
            {
                using (var form = new BrowsePartMasterForm(_api, _adapter)) { form.ShowDialog(); }
            }
            catch (UnauthorizedException) { HandleUnauthorized(); }
            catch (Exception ex) { MessageBox.Show("Browse failed:\n\n" + ex, "Atlas"); }
            return MenuButtonEvent.Activate;
        }

        public static MenuButtonEvent OnCheckinClicked(string buttonName, IntPtr handle)
        {
            _ = Run(() => CheckinFlow.RunAsync(_api, _adapter));
            return MenuButtonEvent.Activate;
        }

        public static MenuButtonEvent OnSignOutClicked(string buttonName, IntPtr handle)
        {
            TokenStore.Clear();
            MessageBox.Show("Signed out.", "Atlas");
            EnsureAuthenticated();
            return MenuButtonEvent.Activate;
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

        private static async Task Run(Func<Task> work)
        {
            try { await work(); }
            catch (UnauthorizedException) { HandleUnauthorized(); }
            catch (Exception ex) { MessageBox.Show("Atlas error:\n\n" + ex.Message, "Atlas"); }
        }

        private static void HandleUnauthorized()
        {
            TokenStore.Clear();
            MessageBox.Show("Session expired. Please sign in again.", "Atlas");
            EnsureAuthenticated();
        }
    }
}
