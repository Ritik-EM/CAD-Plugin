using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AtlasCadCore.ApiClient;

namespace AtlasCadCore.Forms
{
    /// <summary>
    /// Small modal: search part_master_library + pick an existing part_number.
    /// Used by the Upload flow when a detected filename code isn't found in
    /// atlas and the user wants to attach the file to an existing entry
    /// (rather than mint a brand-new part_number for it).
    /// </summary>
    public class PartMasterPickerDialog : Form
    {
        private readonly AtlasApiClient _api;
        private TextBox _searchBox;
        private DataGridView _grid;
        private Button _okBtn;
        private System.Windows.Forms.Timer _debounce;
        private List<Row> _rows = new List<Row>();

        public string SelectedPartNumber { get; private set; }
        public string SelectedDescription { get; private set; }

        private class Row
        {
            public string PartNumber;
            public string Description;
            public string ReleaseType;
            public string GroupLabel;
        }

        public PartMasterPickerDialog(AtlasApiClient api, string initialSearch = null)
        {
            _api = api;
            BuildUi();
            if (!string.IsNullOrEmpty(initialSearch))
                _searchBox.Text = initialSearch;
            _ = ReloadAsync();
        }

        private void BuildUi()
        {
            Text = "Atlas — Pick Existing Part";
            Size = new Size(820, 520);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(640, 380);

            var top = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8) };
            top.Controls.Add(new Label { Text = "Search:", Location = new Point(6, 12), AutoSize = true });
            _searchBox = new TextBox { Location = new Point(60, 8), Width = 700, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            _searchBox.TextChanged += (s, e) => { _debounce?.Stop(); _debounce?.Start(); };
            top.Controls.Add(_searchBox);
            _debounce = new System.Windows.Forms.Timer { Interval = 350 };
            _debounce.Tick += (s, e) => { _debounce.Stop(); _ = ReloadAsync(); };

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoGenerateColumns = false,
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Part Number", Name = "pn", Width = 130 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Description", Name = "desc", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Group", Name = "group", Width = 200 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Release", Name = "rt", Width = 110 });
            _grid.SelectionChanged += (s, e) => UpdateOkButton();
            _grid.CellDoubleClick += (s, e) => { if (_okBtn.Enabled) { _okBtn.PerformClick(); } };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(8) };
            _okBtn = new Button { Text = "Use This Part", Width = 130, Height = 28, DialogResult = DialogResult.None, Anchor = AnchorStyles.Right | AnchorStyles.Bottom, Enabled = false };
            _okBtn.Location = new Point(bottom.Width - 250, 10);
            _okBtn.Click += (s, e) =>
            {
                int idx = _grid.CurrentRow?.Index ?? -1;
                if (idx < 0 || idx >= _rows.Count) return;
                SelectedPartNumber = _rows[idx].PartNumber;
                SelectedDescription = _rows[idx].Description;
                DialogResult = DialogResult.OK;
                Close();
            };
            bottom.Controls.Add(_okBtn);
            var cancel = new Button { Text = "Cancel", Width = 100, Height = 28, DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Right | AnchorStyles.Bottom };
            cancel.Location = new Point(bottom.Width - 110, 10);
            bottom.Controls.Add(cancel);
            AcceptButton = _okBtn;
            CancelButton = cancel;

            // Docked edges first, Fill last (see MissingChildUploadForm note).
            Controls.Add(top);
            Controls.Add(bottom);
            Controls.Add(_grid);
        }

        private void UpdateOkButton()
        {
            _okBtn.Enabled = (_grid.CurrentRow?.Index ?? -1) >= 0 && _rows.Count > 0;
        }

        private async Task ReloadAsync()
        {
            string search = string.IsNullOrWhiteSpace(_searchBox.Text) ? null : _searchBox.Text.Trim();
            try
            {
                var page = await _api.ListPartMasterAsync(releaseType: null, search: search, page: 1, limit: 100);
                _rows.Clear();
                foreach (var d in page?.items ?? new List<PartMasterDocumentDto>())
                {
                    if (d.releases == null) continue;
                    foreach (var kv in d.releases)
                    {
                        foreach (var rev in kv.Value ?? new List<PartMasterRevisionDto>())
                        {
                            if (rev?.part_number == null) continue;
                            if (rev.active != true) continue;          // pick from active revisions only
                            _rows.Add(new Row
                            {
                                PartNumber = rev.part_number,
                                Description = d.description ?? "",
                                ReleaseType = kv.Key,
                                GroupLabel = $"{d.major_group}/{d.minor_group}",
                            });
                        }
                    }
                }
                _grid.Rows.Clear();
                foreach (var r in _rows.OrderBy(r => r.PartNumber))
                    _grid.Rows.Add(r.PartNumber, r.Description, r.GroupLabel, r.ReleaseType);
                UpdateOkButton();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Load failed: " + ex.Message, "Atlas",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
