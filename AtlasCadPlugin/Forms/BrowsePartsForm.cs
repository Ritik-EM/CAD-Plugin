using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace AtlasCadPlugin.Forms
{
    /// <summary>
    /// Lists part_master_library entries that have a native SLDPRT/SLDASM
    /// available, and inserts the selected one into the currently-active
    /// SolidWorks assembly via AddComponent5.
    ///
    /// File is downloaded to %TEMP%\AtlasParts\&lt;part_number&gt;\ before
    /// insertion — SolidWorks resolves the component reference by full path,
    /// so the cache directory must persist for the life of the assembly file.
    /// </summary>
    public class BrowsePartsForm : Form
    {
        private readonly AtlasApiClient _api;
        private readonly ISldWorks _swApp;

        private TextBox _searchBox;
        private Button _searchButton;
        private Button _insertButton;
        private Button _closeButton;
        private DataGridView _grid;
        private Label _statusLabel;

        private List<InsertablePartDto> _rows = new List<InsertablePartDto>();

        public BrowsePartsForm(AtlasApiClient api, ISldWorks swApp)
        {
            _api = api;
            _swApp = swApp;

            Text = "Atlas — Browse Parts (Insert into Assembly)";
            Width = 900;
            Height = 540;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(700, 400);

            BuildLayout();
            Load += async (s, e) => await Search();
        }

        private void BuildLayout()
        {
            Controls.Add(new Label { Text = "Search:", Location = new Point(10, 14), Width = 60 });
            _searchBox = new TextBox { Location = new Point(70, 10), Width = 400 };
            _searchBox.KeyDown += async (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await Search(); }
            };
            Controls.Add(_searchBox);

            _searchButton = new Button { Text = "Search", Location = new Point(475, 9), Width = 80 };
            _searchButton.Click += async (s, e) => await Search();
            Controls.Add(_searchButton);

            _insertButton = new Button { Text = "Insert into Active Assembly", Location = new Point(575, 9), Width = 200 };
            _insertButton.Click += async (s, e) => await InsertSelected();
            Controls.Add(_insertButton);

            _closeButton = new Button
            {
                Text = "Close",
                Location = new Point(785, 9),
                Width = 80,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                DialogResult = DialogResult.Cancel,
            };
            _closeButton.Click += (s, e) => Close();
            Controls.Add(_closeButton);

            _grid = new DataGridView
            {
                Location = new Point(10, 50),
                Width = 860,
                Height = 420,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoGenerateColumns = false,
                RowHeadersVisible = false,
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Part Number", Name = "pn", Width = 140 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Description", Name = "desc", Width = 360 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Filename", Name = "fn", Width = 220 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Release", Name = "rt", Width = 100 });
            Controls.Add(_grid);

            _statusLabel = new Label
            {
                Location = new Point(10, 480),
                Width = 860,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                AutoEllipsis = true,
                Text = "Ready.",
            };
            Controls.Add(_statusLabel);
        }

        private async Task Search()
        {
            _searchButton.Enabled = false;
            _statusLabel.Text = "Searching…";
            try
            {
                _rows = await _api.SearchInsertablePartsAsync(_searchBox.Text);
                _grid.Rows.Clear();
                foreach (var p in _rows)
                    _grid.Rows.Add(p.part_number, p.description ?? "", p.filename, p.release_type);
                _statusLabel.Text = $"{_rows.Count} parts.";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "Search failed: " + ex.Message;
            }
            finally { _searchButton.Enabled = true; }
        }

        private async Task InsertSelected()
        {
            if (_grid.SelectedRows.Count == 0)
            {
                MessageBox.Show("Pick a part first.", "Atlas");
                return;
            }
            int idx = _grid.SelectedRows[0].Index;
            if (idx < 0 || idx >= _rows.Count) return;
            var sel = _rows[idx];

            IModelDoc2 doc = (IModelDoc2)_swApp.ActiveDoc;
            if (doc == null || doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                MessageBox.Show("Open the assembly you want to insert into, then click Insert.", "Atlas");
                return;
            }

            _insertButton.Enabled = false;
            _statusLabel.Text = $"Fetching {sel.part_number}…";
            try
            {
                var insertUrl = await _api.GetInsertUrlAsync(sel.part_number);

                string cacheDir = Path.Combine(Path.GetTempPath(), "AtlasParts", sel.part_number);
                Directory.CreateDirectory(cacheDir);
                string targetPath = Path.Combine(cacheDir, insertUrl.filename);

                _statusLabel.Text = $"Downloading {insertUrl.filename}…";
                await _api.DownloadFileAsync(insertUrl.download_url, targetPath);

                _statusLabel.Text = "Inserting into assembly…";
                var asm = (AssemblyDoc)doc;
                // Insert at origin (0,0,0). User can drag to position after.
                // SolidWorks signature: AddComponent5(CompName, ConfigOption,
                //   NewConfigName, UseConfigForPartReferences, ExistConfigName, X, Y, Z)
                Component2 comp = asm.AddComponent5(
                    targetPath,
                    (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                    "", false, "", 0, 0, 0);

                if (comp == null)
                {
                    _statusLabel.Text = "AddComponent5 returned null — see SolidWorks status bar.";
                    return;
                }
                _statusLabel.Text = $"Inserted {sel.part_number}.";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Insert failed:\n\n" + ex.Message, "Atlas",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                _statusLabel.Text = "Insert failed.";
            }
            finally { _insertButton.Enabled = true; }
        }
    }
}
