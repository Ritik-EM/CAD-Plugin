using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AtlasCadCore.ApiClient;

namespace AtlasCadCore.Forms
{
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

            var top = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
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
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = 28,
            };
            _grid.RowTemplate.Height = 26;
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Part Number", Name = "pn", Width = 130 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Description", Name = "desc", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Group", Name = "group", Width = 200 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Release", Name = "rt", Width = 110 });
            _grid.SelectionChanged += (s, e) => UpdateOkButton();
            _grid.CellDoubleClick += (s, e) => { if (_okBtn.Enabled) { _okBtn.PerformClick(); } };

            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                Padding = new Padding(8, 6, 8, 6),
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            bottom.Controls.Add(new Label { AutoSize = true, Anchor = AnchorStyles.Left, Text = "" }, 0, 0);

            _okBtn = new Button
            {
                Text = "Use This Part",
                Width = 130, Height = 28,
                DialogResult = DialogResult.None,
                Anchor = AnchorStyles.Right,
                Enabled = false,
                Margin = new Padding(4),
            };
            _okBtn.Click += (s, e) =>
            {
                int idx = _grid.CurrentRow?.Index ?? -1;
                if (idx < 0 || idx >= _rows.Count) return;
                SelectedPartNumber = _rows[idx].PartNumber;
                SelectedDescription = _rows[idx].Description;
                DialogResult = DialogResult.OK;
                Close();
            };
            bottom.Controls.Add(_okBtn, 1, 0);

            var cancel = new Button
            {
                Text = "Cancel",
                Width = 100, Height = 28,
                DialogResult = DialogResult.Cancel,
                Anchor = AnchorStyles.Right,
                Margin = new Padding(4),
            };
            bottom.Controls.Add(cancel, 2, 0);
            AcceptButton = _okBtn;
            CancelButton = cancel;

            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 44f));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 56f));
            outer.Controls.Add(top,    0, 0);
            outer.Controls.Add(_grid,  0, 1);
            outer.Controls.Add(bottom, 0, 2);
            Controls.Add(outer);
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
                            // Show every part number the search returns, regardless of
                            // revision state (active, PENDING_PREPARATION, etc.). The
                            // status is surfaced in the Release column so the user can
                            // still tell which revisions are live.
                            _rows.Add(new Row
                            {
                                PartNumber = rev.part_number,
                                Description = d.description ?? "",
                                ReleaseType = rev.active == true ? kv.Key : $"{kv.Key} ({rev.status ?? "inactive"})",
                                GroupLabel = $"{d.major_group}/{d.minor_group}",
                            });
                        }
                    }
                }
                // Save + restore search-box focus so the user can keep
                // typing while debounced searches come back.
                bool restoreSearchFocus = _searchBox.Focused;
                int caret = _searchBox.SelectionStart;
                int selLen = _searchBox.SelectionLength;

                _grid.Rows.Clear();
                foreach (var r in _rows.OrderBy(r => r.PartNumber))
                    _grid.Rows.Add(r.PartNumber, r.Description, r.GroupLabel, r.ReleaseType);
                _grid.PerformLayout();
                _grid.Refresh();
                if (_grid.Rows.Count > 0) _grid.CurrentCell = _grid.Rows[0].Cells["pn"];
                UpdateOkButton();

                if (restoreSearchFocus)
                {
                    _searchBox.Focus();
                    _searchBox.SelectionStart = caret;
                    _searchBox.SelectionLength = selLen;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Load failed: " + ex.Message, "Atlas",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
