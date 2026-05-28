using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using AtlasCadCore.Adapter;
using AtlasCadCore.ApiClient;

namespace AtlasCadCore.Forms
{
    public class MissingChildUploadForm : Form
    {
        public class Row
        {
            public string PartNumber;        // parsed from filename
            public string OriginalFilename;  // what CAD was looking for
            public string LocalPath;         // user-chosen local file (.sldprt / .sldasm / .CATPart)
            // OR (mutually exclusive with LocalPath):
            public string AtlasPartNumber;   // user-picked existing atlas part_number
            public string AtlasDescription;  // for display only
        }

        private readonly AtlasApiClient _api;
        private readonly List<Row> _rows;
        private DataGridView _grid;
        private CheckBox _releaseRevisionCheck;
        private TextBox _otpBox;
        private Button _sendOtpBtn;
        private Label _otpHint;

        public List<Row> Result { get; private set; }
        public bool ReleaseNewRevision { get; private set; }
        public string Otp { get; private set; }

        public event Action SendOtpRequested;

        public MissingChildUploadForm(List<MissingComponent> missing, AtlasApiClient api = null)
        {
            _api = api;
            _rows = (missing ?? new List<MissingComponent>())
                .Select(m => new Row
                {
                    PartNumber = m.PartNumber ?? "(unknown)",
                    OriginalFilename = m.Filename,
                    LocalPath = null,
                    AtlasPartNumber = null,
                })
                .ToList();
            BuildUi();
            Populate();
        }

        private void BuildUi()
        {
            Text = "Atlas — Resolve Missing Children";
            Size = new Size(1080, 580);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(900, 400);

            var hdr = new Label
            {
                Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 0),
                Text = $"{_rows.Count} child part(s) couldn't be auto-resolved from atlas. " +
                       "For each row, either Browse a local file (will be uploaded), or " +
                       "Pick from Atlas (downloads the file from an existing atlas part_number). " +
                       "Leave a row blank to skip.",
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
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Part Number", Name = "pn", Width = 140, ReadOnly = true });
            // Filename column is EDITABLE — CATIA V5R21 doesn't tell us
            // whether a broken child was originally a .CATPart or
            // .CATProduct, so we default to .CATPart and let the user fix
            // it when they know the original was a sub-assembly. When the
            // user picks an atlas part we also auto-update the extension
            // to match atlas's native filename.
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Filename (editable)", Name = "filename", Width = 240, ReadOnly = false });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Resolution", Name = "picked", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "", Name = "browse", Text = "Browse Local…", UseColumnTextForButtonValue = true, Width = 110 });
            _grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "", Name = "atlas", Text = "Pick from Atlas…", UseColumnTextForButtonValue = true, Width = 130 });
            _grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "", Name = "clear", Text = "Clear", UseColumnTextForButtonValue = true, Width = 60 });
            _grid.CellContentClick += OnGridButtonClick;
            _grid.CellEndEdit += (s, e) =>
            {
                if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "filename") return;
                var edited = _grid.Rows[e.RowIndex].Cells["filename"].Value?.ToString();
                if (!string.IsNullOrWhiteSpace(edited))
                    _rows[e.RowIndex].OriginalFilename = edited.Trim();
            };

            var bottom = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };

            _releaseRevisionCheck = new CheckBox
            {
                Text = "Release as new revision (requires OTP — applies to Browse Local uploads only)",
                Location = new Point(10, 8),
                AutoSize = true,
                Checked = false,
            };
            _releaseRevisionCheck.CheckedChanged += (s, e) => UpdateOtpUi();
            bottom.Controls.Add(_releaseRevisionCheck);

            bottom.Controls.Add(new Label { Text = "OTP:", Location = new Point(10, 38), AutoSize = true });
            _otpBox = new TextBox
            {
                Location = new Point(50, 35), Width = 120, MaxLength = 6,
                Font = new Font(FontFamily.GenericMonospace, 11),
            };
            bottom.Controls.Add(_otpBox);
            _sendOtpBtn = new Button { Text = "Send OTP", Location = new Point(180, 33), Width = 100 };
            _sendOtpBtn.Click += (s, e) => SendOtpRequested?.Invoke();
            bottom.Controls.Add(_sendOtpBtn);
            _otpHint = new Label
            {
                Location = new Point(290, 38), AutoSize = true, ForeColor = Color.DimGray,
                Text = "(only required if releasing a new revision)",
            };
            bottom.Controls.Add(_otpHint);

            var ok = new Button { Text = "Continue", Size = new Size(120, 30), Anchor = AnchorStyles.Top | AnchorStyles.Right, DialogResult = DialogResult.None };
            ok.Click += (s, e) =>
            {
                ReleaseNewRevision = _releaseRevisionCheck.Checked;
                if (ReleaseNewRevision && _rows.Any(r => !string.IsNullOrEmpty(r.LocalPath)))
                {
                    string otp = (_otpBox.Text ?? "").Trim();
                    if (otp.Length != 6 || !otp.All(char.IsDigit))
                    {
                        MessageBox.Show("Enter the 6-digit OTP from your email.", "Atlas",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        _otpBox.Focus();
                        return;
                    }
                    Otp = otp;
                }
                Result = _rows.Where(r => !string.IsNullOrEmpty(r.LocalPath)
                                       || !string.IsNullOrEmpty(r.AtlasPartNumber)).ToList();
                DialogResult = DialogResult.OK;
                Close();
            };
            var cancel = new Button { Text = "Skip All", Size = new Size(100, 30), Anchor = AnchorStyles.Top | AnchorStyles.Right, DialogResult = DialogResult.Cancel };
            bottom.Resize += (s, e) =>
            {
                ok.Location = new Point(bottom.Width - ok.Width - 10, 88);
                cancel.Location = new Point(ok.Left - cancel.Width - 8, 88);
            };
            bottom.Controls.Add(ok);
            bottom.Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;

            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 56f));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 140f));
            outer.Controls.Add(hdr, 0, 0);
            outer.Controls.Add(_grid, 0, 1);
            outer.Controls.Add(bottom, 0, 2);
            Controls.Add(outer);

            UpdateOtpUi();
        }

        private void OnGridButtonClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var colName = _grid.Columns[e.ColumnIndex].Name;
            var row = _rows[e.RowIndex];

            if (colName == "browse")
            {
                using (var ofd = new OpenFileDialog
                {
                    Title = "Pick local file for " + row.PartNumber,
                    Filter = "Native CAD|*.sldprt;*.sldasm;*.CATPart;*.CATProduct|All files|*.*",
                    CheckFileExists = true,
                })
                {
                    if (ofd.ShowDialog() != DialogResult.OK) return;
                    row.LocalPath = ofd.FileName;
                    row.AtlasPartNumber = null;
                    row.AtlasDescription = null;

                    // Sync the editable filename column's extension to
                    // match the picked local file — so CATIA finds the
                    // dropped file under the right name.
                    SyncFilenameExtension(e.RowIndex, row, Path.GetExtension(ofd.FileName));

                    _grid.Rows[e.RowIndex].Cells["picked"].Value =
                        "local: " + Path.GetFileName(ofd.FileName);
                }
            }
            else if (colName == "atlas")
            {
                if (_api == null)
                {
                    MessageBox.Show("Atlas API client unavailable in this context.", "Atlas",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                using (var dlg = new PartMasterPickerDialog(_api, initialSearch: row.PartNumber))
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    if (string.IsNullOrEmpty(dlg.SelectedPartNumber)) return;
                    row.AtlasPartNumber = dlg.SelectedPartNumber;
                    row.AtlasDescription = dlg.SelectedDescription;
                    row.LocalPath = null;

                    // We don't know the atlas part's native filename here
                    // (would need an extra API call). Leave the extension
                    // as-is and trust the user — they can edit the
                    // filename column directly if the default guess is
                    // wrong for this child.

                    _grid.Rows[e.RowIndex].Cells["picked"].Value =
                        string.IsNullOrEmpty(row.AtlasDescription)
                            ? "atlas: " + row.AtlasPartNumber
                            : $"atlas: {row.AtlasPartNumber}  —  {row.AtlasDescription}";
                }
            }
            else if (colName == "clear")
            {
                row.LocalPath = null;
                row.AtlasPartNumber = null;
                row.AtlasDescription = null;
                _grid.Rows[e.RowIndex].Cells["picked"].Value = "";
            }
        }

        private void SyncFilenameExtension(int rowIndex, Row row, string newExt)
        {
            if (string.IsNullOrEmpty(newExt)) return;
            string current = row.OriginalFilename ?? "";
            string stem = string.IsNullOrEmpty(current)
                ? row.PartNumber ?? "unknown"
                : Path.GetFileNameWithoutExtension(current);
            string updated = stem + newExt;
            if (string.Equals(updated, current, StringComparison.OrdinalIgnoreCase)) return;
            row.OriginalFilename = updated;
            _grid.Rows[rowIndex].Cells["filename"].Value = updated;
        }

        private void UpdateOtpUi()
        {
            bool needsOtp = _releaseRevisionCheck.Checked;
            _otpBox.Enabled = needsOtp;
            _sendOtpBtn.Enabled = needsOtp;
            _otpHint.Text = needsOtp
                ? "click Send OTP, then enter the 6-digit code from your email"
                : "(only required if releasing a new revision)";
        }

        private void Populate()
        {
            foreach (var r in _rows)
                _grid.Rows.Add(r.PartNumber, r.OriginalFilename, "", null, null, null);
            _grid.PerformLayout();
            _grid.Refresh();
            if (_grid.Rows.Count > 0) _grid.CurrentCell = _grid.Rows[0].Cells["pn"];
        }
    }
}
