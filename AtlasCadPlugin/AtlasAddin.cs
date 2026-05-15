using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swpublished;
using SolidWorks.Interop.swconst;
using AtlasCadPlugin.Forms;

namespace AtlasCadPlugin
{
    [Guid("E3A2B3D4-E5F6-7890-ABCD-EF1234567890")]
    [ComVisible(true)]
    public class AtlasAddin : ISwAddin
    {
        private int _addinCookie;
        private ISldWorks _swApp;
        private ICommandManager _cmdManager;
        private AtlasApiClient _api;

        // Ribbon command IDs.
        private const int CmdIdPing = 0;
        private const int CmdIdUpload = 1;
        private const int CmdIdBrowse = 2;
        private const int CmdIdCheckin = 3;
        private const int CmdIdSwitchUser = 4;

        // Atlas backend URL. Set this to your Mac LAN IP if backend runs there.
        // For demo: edit and rebuild, or move to a config file later.
        private const string AtlasBaseUrl = "http://192.168.1.100:8000";

        public bool ConnectToSW(object ThisSW, int Cookie)
        {
            _swApp = (ISldWorks)ThisSW;
            _addinCookie = Cookie;
            _swApp.SetAddinCallbackInfo2(0, this, _addinCookie);

            _api = new AtlasApiClient(AtlasBaseUrl);

            EnsureIdentity();

            _cmdManager = _swApp.GetCommandManager(_addinCookie);
            AddRibbonButtons();
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

        // ---- Ribbon ----

        private int GetCommandGroupId() => 1;

        private void AddRibbonButtons()
        {
            int errors = 0;
            ICommandGroup g = _cmdManager.CreateCommandGroup2(
                UserID: GetCommandGroupId(),
                Title: "Atlas",
                ToolTip: "Atlas PLM integration",
                Hint: "Atlas",
                Position: -1,
                IgnorePreviousVersion: true,
                Errors: ref errors
            );

            int both = (int)swCommandItemType_e.swMenuItem | (int)swCommandItemType_e.swToolbarItem;

            g.AddCommandItem2("Ping Atlas", -1, "Test connection to Atlas backend",
                "Ping", 0, nameof(OnPingClicked), "", CmdIdPing, both);

            g.AddCommandItem2("Upload Assembly", -1, "Upload the open assembly as a new entry",
                "Upload", 0, nameof(OnUploadClicked), "", CmdIdUpload, both);

            g.AddCommandItem2("Browse Atlas", -1, "Browse and check out assemblies",
                "Browse", 0, nameof(OnBrowseClicked), "", CmdIdBrowse, both);

            g.AddCommandItem2("Check In", -1, "Upload current assembly as new version of checked-out item",
                "Check In", 0, nameof(OnCheckinClicked), "", CmdIdCheckin, both);

            g.AddCommandItem2("Switch Identity", -1, "Change which user the plugin acts as",
                "Switch User", 0, nameof(OnSwitchUserClicked), "", CmdIdSwitchUser, both);

            g.HasToolbar = true;
            g.HasMenu = true;
            g.Activate();
        }

        // ---- Identity stub (replace with real auth in M1) ----

        private void EnsureIdentity()
        {
            if (!string.IsNullOrEmpty(IdentityStore.GetUserName())) return;
            using (var dlg = new IdentityPromptForm(current: null))
            {
                if (dlg.ShowDialog() == DialogResult.OK)
                    IdentityStore.SetUserName(dlg.EnteredName);
            }
        }

        // ---- Button callbacks (must be public — invoked by SolidWorks via COM) ----

        public void OnPingClicked() => _ = Run(async () =>
        {
            string result = await _api.PingAsync();
            string user = IdentityStore.GetUserName() ?? "(no identity)";
            MessageBox.Show($"Identity: {user}\n\n{result}", "Atlas — Ping",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        });

        public void OnUploadClicked() => _ = Run(async () =>
        {
            IModelDoc2 doc = (IModelDoc2)_swApp.ActiveDoc;
            if (doc == null) { MessageBox.Show("Open an assembly first."); return; }

            var tree = AssemblyWalker.Walk(doc);
            string assemblyName = Path.GetFileNameWithoutExtension(doc.GetPathName());

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await _api.UploadAssemblyAsync(assemblyName, tree);
            sw.Stop();

            MessageBox.Show(
                $"Uploaded {tree.Count} files in {sw.Elapsed.TotalSeconds:F1}s.\n\n" +
                $"Assembly: {result.name}\nID: {result.assembly_id}\nVersion: {result.version_number}",
                "Atlas — Upload",
                MessageBoxButtons.OK, MessageBoxIcon.Information
            );
        });

        public void OnBrowseClicked()
        {
            try
            {
                using (var form = new BrowseAtlasForm(_api, _swApp))
                {
                    form.ShowDialog();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Browse failed:\n\n" + ex, "Atlas", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public void OnCheckinClicked() => _ = Run(async () =>
        {
            IModelDoc2 doc = (IModelDoc2)_swApp.ActiveDoc;
            if (doc == null) { MessageBox.Show("Open the checked-out assembly first."); return; }

            string rootPath = doc.GetPathName();
            string assemblyId = CheckoutTracker.GetAssemblyId(rootPath);
            if (string.IsNullOrEmpty(assemblyId))
            {
                MessageBox.Show(
                    "This file isn't tracked as a checked-out Atlas assembly.\n\n" +
                    "Use Browse Atlas → Check Out first, edit, then Check In.",
                    "Atlas", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Force a save so we upload the user's edits, not the on-disk version
            // from before they pressed Ctrl+S.
            int saveErrors = 0, saveWarnings = 0;
            doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref saveErrors, ref saveWarnings);

            var tree = AssemblyWalker.Walk(doc);

            string comment = Microsoft.VisualBasic.Interaction.InputBox(
                "Optional comment for this version:", "Atlas — Check In", "");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await _api.CheckinAsync(assemblyId, tree, comment ?? "");
            sw.Stop();

            CheckoutTracker.Untrack(rootPath);

            MessageBox.Show(
                $"Checked in v{result.version_number} ({tree.Count} files, {sw.Elapsed.TotalSeconds:F1}s).",
                "Atlas — Check In",
                MessageBoxButtons.OK, MessageBoxIcon.Information
            );
        });

        public void OnSwitchUserClicked()
        {
            using (var dlg = new IdentityPromptForm(current: IdentityStore.GetUserName()))
            {
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    IdentityStore.SetUserName(dlg.EnteredName);
                    MessageBox.Show("Identity switched to: " + dlg.EnteredName, "Atlas",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private static async Task Run(Func<Task> work)
        {
            try { await work(); }
            catch (Exception ex)
            {
                MessageBox.Show("Atlas error:\n\n" + ex.Message, "Atlas",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ---- COM registration ----

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
                throwOnMissingSubKey: false
            );
        }
    }
}
