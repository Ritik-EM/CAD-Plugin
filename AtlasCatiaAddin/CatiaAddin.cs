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
using INFITF;

namespace AtlasCadPlugin.Catia
{
    /// <summary>
    /// CATIA V5 add-in entry point. CATIA loads add-ins via COM registration
    /// + a CATEnv environment entry pointing at the AtlasWorkbench class.
    /// On startup the CATIA engine calls CreateCommands() and CreateInterfaces()
    /// to hook up toolbars.
    ///
    /// The actual menu wiring is done via a CATScript macro (deployed by
    /// installer/CatiaInstaller.cmd into %CATCustom%\CATStartup\). The macro
    /// instantiates AtlasMenu and calls the ribbon callbacks below.
    /// </summary>
    [Guid("D3A2B3D4-E5F6-7890-ABCD-EF1234567891")]
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDual)]
    public class CatiaAddin
    {
        private Application _catApp;
        private AtlasApiClient _api;
        private AuthService _auth;
        private ICadAdapter _adapter;

        private const string AtlasBaseUrl = "http://172.16.2.159:8000";
        private const string OctopusBaseUrl = "https://octopus.eulerlogistics.com";

        public CatiaAddin()
        {
            System.Net.ServicePointManager.SecurityProtocol =
                System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls13;
        }

        public void Initialize(Application catApp)
        {
            _catApp = catApp;
            _api = new AtlasApiClient(AtlasBaseUrl);
            _auth = new AuthService(OctopusBaseUrl);
            _adapter = new CatiaAdapter(_catApp);

            EnsureAuthenticated();
            _ = AutoUpdater.CheckAsync(_api);
        }

        // ---- Menu callbacks (invoked by the CATScript macro on button click) ----

        public void OnPingClicked() => _ = Run(async () =>
        {
            string result = await _api.PingAsync();
            var who = TokenStore.Current();
            string identity = who != null ? $"{who.DisplayName} <{who.Email}>" : "(not signed in)";
            MessageBox.Show($"Signed in as: {identity}\n\n{result}", "Atlas — Ping",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        });

        public void OnUploadClicked() => _ = Run(() => UploadToPartMasterForm.RunAsync(_api, _adapter));

        public void OnBrowseClicked()
        {
            try
            {
                using (var form = new BrowsePartMasterForm(_api, _adapter)) { form.ShowDialog(); }
            }
            catch (UnauthorizedException) { HandleUnauthorized(); }
            catch (Exception ex)
            {
                MessageBox.Show("Browse failed:\n\n" + ex, "Atlas",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public void OnCheckinClicked() => _ = Run(() => CheckinFlow.RunAsync(_api, _adapter));

        public void OnSignOutClicked()
        {
            TokenStore.Clear();
            MessageBox.Show("Signed out. Sign-in will appear next.", "Atlas");
            EnsureAuthenticated();
        }

        // ---- Auth ----

        private bool EnsureAuthenticated()
        {
            var existing = TokenStore.Current();
            if (existing != null && !existing.IsExpired) return true;
            string preset = existing?.Email;
            if (existing != null && existing.IsExpired) TokenStore.Clear();
            using (var dlg = new LoginForm(_auth, preset))
                return dlg.ShowDialog() == DialogResult.OK;
        }

        private async Task Run(Func<Task> work)
        {
            try { await work(); }
            catch (UnauthorizedException) { HandleUnauthorized(); }
            catch (Exception ex) { MessageBox.Show("Atlas error:\n\n" + ex.Message, "Atlas"); }
        }

        private void HandleUnauthorized()
        {
            TokenStore.Clear();
            MessageBox.Show("Session expired. Please sign in again.", "Atlas");
            EnsureAuthenticated();
        }

        // ---- COM registration (HKLM + CATIA add-in registry entries) ----

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
