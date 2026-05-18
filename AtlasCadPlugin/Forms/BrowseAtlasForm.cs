using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using AtlasCadPlugin.Auth;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace AtlasCadPlugin.Forms
{
    /// <summary>
    /// Browse Atlas — list assemblies with lock status, allow check-out / cancel-checkout.
    /// On check-out, downloads files to %TEMP%\AtlasCad\<assembly_id>\, opens the root
    /// in SolidWorks, and records the mapping in CheckoutTracker.
    /// </summary>
    public class BrowseAtlasForm : Form
    {
        private readonly AtlasApiClient _api;
        private readonly ISldWorks _swApp;

        private DataGridView _grid;
        private Button _refreshButton;
        private Button _checkoutButton;
        private Button _cancelCheckoutButton;
        private Button _historyButton;
        private Button _closeButton;
        private Label _statusLabel;

        private List<AssemblyDto> _rows = new List<AssemblyDto>();

        public BrowseAtlasForm(AtlasApiClient api, ISldWorks swApp)
        {
            _api = api;
            _swApp = swApp;

            Text = "Atlas — Browse Assemblies";
            Width = 900;
            Height = 500;
            StartPosition = FormStartPosition.CenterParent;

            BuildLayout();

            Load += async (s, e) => await ReloadAsync();
        }

        private void BuildLayout()
        {
            _refreshButton = new Button { Text = "Refresh", Location = new Point(10, 10), Width = 90 };
            _refreshButton.Click += async (s, e) => await ReloadAsync();
            Controls.Add(_refreshButton);

            _checkoutButton = new Button { Text = "Check Out", Location = new Point(110, 10), Width = 110 };
            _checkoutButton.Click += async (s, e) => await CheckoutSelected();
            Controls.Add(_checkoutButton);

            _cancelCheckoutButton = new Button { Text = "Cancel Checkout", Location = new Point(230, 10), Width = 130 };
            _cancelCheckoutButton.Click += async (s, e) => await CancelCheckoutSelected();
            Controls.Add(_cancelCheckoutButton);

            _historyButton = new Button { Text = "History…", Location = new Point(370, 10), Width = 90 };
            _historyButton.Click += (s, e) => OpenHistory();
            Controls.Add(_historyButton);

            _closeButton = new Button { Text = "Close", Location = new Point(780, 10), Width = 90 };
            _closeButton.Click += (s, e) => Close();
            Controls.Add(_closeButton);

            _statusLabel = new Label
            {
                Location = new Point(10, 440),
                Width = 870,
                AutoEllipsis = true,
                Text = "Ready.",
            };
            Controls.Add(_statusLabel);

            _grid = new DataGridView
            {
                Location = new Point(10, 50),
                Width = 870,
                Height = 380,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoGenerateColumns = false,
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name", DataPropertyName = "name", Width = 280 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Root File", DataPropertyName = "root_filename", Width = 220 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Version", DataPropertyName = "current_version", Width = 70 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", DataPropertyName = "status_display", Width = 250 });
            Controls.Add(_grid);
        }

        private async Task ReloadAsync()
        {
            try
            {
                _statusLabel.Text = "Loading...";
                _rows = await _api.ListAssembliesAsync();
                var displayRows = _rows.ConvertAll(a => new
                {
                    a.id,
                    a.name,
                    a.root_filename,
                    a.current_version,
                    status_display = string.IsNullOrEmpty(a.locked_by)
                        ? "Available"
                        : $"Checked out by {a.locked_by}",
                });
                _grid.DataSource = displayRows;
                _statusLabel.Text = $"{_rows.Count} assemblies.";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "Error: " + ex.Message;
            }
        }

        private AssemblyDto SelectedAssembly()
        {
            if (_grid.SelectedRows.Count == 0) return null;
            int idx = _grid.SelectedRows[0].Index;
            if (idx < 0 || idx >= _rows.Count) return null;
            return _rows[idx];
        }

        private async Task CheckoutSelected()
        {
            var sel = SelectedAssembly();
            if (sel == null)
            {
                MessageBox.Show("Select an assembly first.", "Atlas", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                _statusLabel.Text = $"Checking out {sel.name}...";
                Enabled = false;
                var result = await _api.CheckoutAsync(sel.id);

                string baseDir = Path.Combine(Path.GetTempPath(), "AtlasCad", sel.id);
                if (Directory.Exists(baseDir))
                    Directory.Delete(baseDir, recursive: true);
                Directory.CreateDirectory(baseDir);

                string rootLocalPath = null;
                int n = 0;
                foreach (var f in result.files)
                {
                    n++;
                    _statusLabel.Text = $"Downloading {n}/{result.files.Count}: {f.filename}";
                    Application.DoEvents();
                    string targetPath = Path.Combine(baseDir, f.relative_path.Replace('/', Path.DirectorySeparatorChar));
                    try
                    {
                        await _api.DownloadFileAsync(f.download_url, targetPath);
                    }
                    catch (Exception dlEx)
                    {
                        // Show truncated URL + full exception chain so we can diagnose
                        // SSL, DNS, network, or S3 presign errors.
                        string urlPreview = f.download_url.Length > 150
                            ? f.download_url.Substring(0, 150) + "..."
                            : f.download_url;
                        throw new Exception(
                            $"Download failed for file {n}/{result.files.Count}: {f.filename}\n\n" +
                            $"URL (truncated): {urlPreview}\n\n" +
                            $"Full error:\n{dlEx}",
                            dlEx);
                    }
                    if (f.is_root) rootLocalPath = targetPath;
                }

                if (rootLocalPath == null)
                    throw new Exception("Server returned no root file");

                CheckoutTracker.Track(rootLocalPath, sel.id);

                int errors = 0, warnings = 0;
                _swApp.OpenDoc6(
                    rootLocalPath,
                    (int)swDocumentTypes_e.swDocASSEMBLY,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                    "",
                    ref errors,
                    ref warnings
                );

                _statusLabel.Text = $"Checked out v{result.version_number} of {sel.name}. Make changes and click Check In.";
                await ReloadAsync();
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Check-out failed:\n\n" + ex.ToString(), "Atlas", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _statusLabel.Text = "Check-out failed.";
            }
            finally
            {
                Enabled = true;
            }
        }

        private void OpenHistory()
        {
            var sel = SelectedAssembly();
            if (sel == null)
            {
                MessageBox.Show("Pick an assembly first.", "Atlas", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var f = new VersionHistoryForm(_api, sel.id, sel.name))
            {
                f.ShowDialog();
            }
            // Refresh in case Set-as-Active changed current_version.
            _ = ReloadAsync();
        }

        private async Task CancelCheckoutSelected()
        {
            var sel = SelectedAssembly();
            if (sel == null) return;
            string me = TokenStore.Current()?.Email;
            if (sel.locked_by != me)
            {
                MessageBox.Show($"Only {sel.locked_by} can cancel this checkout.", "Atlas",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                await _api.CancelCheckoutAsync(sel.id);
                _statusLabel.Text = "Checkout cancelled.";
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Cancel failed:\n\n" + ex.Message, "Atlas", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
