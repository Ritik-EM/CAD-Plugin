using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AtlasCadCore.ApiClient;

namespace AtlasCadCore.Forms
{
    /// <summary>
    /// One-shot table that lists every part_number atlas didn't recognise during
    /// an upload. Each row gets a Pick Existing… button that opens
    /// PartMasterPickerDialog; whatever the user picks lands in the row's
    /// "Attach to" cell. Rows left blank are treated as "skip — release on
    /// atlas-ui first".
    /// </summary>
    public class MissingPartsTableForm : Form
    {
        public class Row
        {
            public string DetectedPartNumber;
            public string Filename;
            public string PickedPartNumber;
            public string PickedDescription;
        }

        private readonly AtlasApiClient _api;
        private readonly List<Row> _rows;
        private readonly string _headerText;
        private readonly string _title;
        private DataGridView _grid;

        public IReadOnlyList<Row> Rows => _rows;

        /// <param name="headerText">Overrides the explanatory header. When null,
        /// the default "aren't released on atlas yet" wording is used (the
        /// post-upload case). Pass a custom string for other contexts (e.g.
        /// the pre-upload "assign a part_number" prompt).</param>
        /// <param name="title">Overrides the window title.</param>
        public MissingPartsTableForm(AtlasApiClient api, IEnumerable<MissingPartDto> missing,
                                     string headerText = null, string title = null)
        {
            _api = api;
            _rows = (missing ?? Enumerable.Empty<MissingPartDto>())
                .Select(m => new Row
                {
                    DetectedPartNumber = m.part_number ?? "(unknown)",
                    Filename = m.filename ?? "",
                })
                .ToList();
            _headerText = headerText;
            _title = title;
            BuildUi();
            Populate();
        }

        private void BuildUi()
        {
            Text = _title ?? "Atlas — Parts Not Found";
            Size = new Size(980, 520);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(780, 360);

            var hdr = new Label
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10, 8, 10, 0),
                Text = _headerText ??
                       ($"{_rows.Count} part_number(s) aren't released on atlas yet.\r\n" +
                        "For each row, click \"Pick Existing…\" to attach the file to an existing atlas " +
                        "part_number, or leave it blank to skip (release on atlas-ui first)."),
            };

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                AutoGenerateColumns = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                ReadOnly = false,
                EditMode = DataGridViewEditMode.EditOnEnter,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = 28,
            };
            _grid.RowTemplate.Height = 28;
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Detected Part Number", Name = "pn", Width = 170, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Filename", Name = "filename", Width = 240, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Attach to (atlas part_number)", Name = "picked", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "", Name = "pick", Text = "Pick Existing…", UseColumnTextForButtonValue = true, Width = 120 });
            // In-plugin release flow. Opens ReleasePartNumberForm (the same
            // full cascading-dropdown release UI as the standalone "Release Part
            // Code" action), then attaches the file to the minted part_number —
            // no need to leave the plugin for atlas-ui.
            _grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "", Name = "create", Text = "Create New…", UseColumnTextForButtonValue = true, Width = 110 });
            _grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "", Name = "clear", Text = "Clear", UseColumnTextForButtonValue = true, Width = 60 });
            _grid.CellContentClick += OnGridButtonClick;

            var bottom = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
            var okBtn = new Button
            {
                Text = "Continue",
                Size = new Size(120, 30),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                DialogResult = DialogResult.OK,
            };
            var cancelBtn = new Button
            {
                Text = "Skip All",
                Size = new Size(100, 30),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                DialogResult = DialogResult.Cancel,
            };
            bottom.Resize += (s, e) =>
            {
                okBtn.Location = new Point(bottom.Width - okBtn.Width - 10, 10);
                cancelBtn.Location = new Point(okBtn.Left - cancelBtn.Width - 8, 10);
            };
            bottom.Controls.Add(okBtn);
            bottom.Controls.Add(cancelBtn);
            AcceptButton = okBtn;
            CancelButton = cancelBtn;

            var outer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
            };
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 56f));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 56f));
            outer.Controls.Add(hdr, 0, 0);
            outer.Controls.Add(_grid, 0, 1);
            outer.Controls.Add(bottom, 0, 2);
            Controls.Add(outer);
        }

        private void OnGridButtonClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var colName = _grid.Columns[e.ColumnIndex].Name;
            if (colName == "pick")
            {
                var row = _rows[e.RowIndex];
                using (var dlg = new PartMasterPickerDialog(_api, initialSearch: row.DetectedPartNumber))
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    if (string.IsNullOrEmpty(dlg.SelectedPartNumber)) return;
                    row.PickedPartNumber = dlg.SelectedPartNumber;
                    row.PickedDescription = dlg.SelectedDescription;
                    _grid.Rows[e.RowIndex].Cells["picked"].Value =
                        string.IsNullOrEmpty(row.PickedDescription)
                            ? row.PickedPartNumber
                            : $"{row.PickedPartNumber}   —   {row.PickedDescription}";
                }
            }
            else if (colName == "create")
            {
                var row = _rows[e.RowIndex];
                // Same full release flow as the standalone "Release Part Code"
                // action (cascading metadata dropdowns + live preview), with a
                // context line naming the file we're minting a code for.
                string ctx = $"Releasing a code for file: {row.Filename}"
                    + (string.IsNullOrEmpty(row.DetectedPartNumber) ? "" : $"   (detected {row.DetectedPartNumber})");
                using (var dlg = new ReleasePartNumberForm(_api, ctx))
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    if (string.IsNullOrEmpty(dlg.MintedPartNumber)) return;
                    row.PickedPartNumber = dlg.MintedPartNumber;
                    row.PickedDescription = "newly released (PROTO)";
                    _grid.Rows[e.RowIndex].Cells["picked"].Value =
                        $"{row.PickedPartNumber}   —   {row.PickedDescription}";
                }
            }
            else if (colName == "clear")
            {
                _rows[e.RowIndex].PickedPartNumber = null;
                _rows[e.RowIndex].PickedDescription = null;
                _grid.Rows[e.RowIndex].Cells["picked"].Value = "";
            }
        }

        private void Populate()
        {
            foreach (var r in _rows)
                _grid.Rows.Add(r.DetectedPartNumber, r.Filename, "", null, null, null);
            if (_grid.Rows.Count > 0) _grid.CurrentCell = _grid.Rows[0].Cells["pn"];
        }
    }
}
