using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AtlasCadCore.ApiClient;

namespace AtlasCadCore.Forms
{
    public class CheckinPropagationForm : Form
    {
        public class TreeRow
        {
            public string PartNumber;
            public string ParentPartNumber;
            public string Filename;
            public int Depth;
            public bool PreCheckedAsChanged;
        }

        private readonly string _rootPartNumber;
        private readonly string _releaseType;
        private readonly List<TreeRow> _rows;
        private readonly Dictionary<string, string> _parentOf;
        private readonly AtlasApiClient _api;

        private DataGridView _grid;
        private TextBox _commentBox;
        private TextBox _otpBox;
        private Button _sendOtpBtn;
        private Label _otpStatusLabel;
        private Label _summaryLabel;

        public List<string> ChangedPartNumbers { get; private set; }
        public string Comment { get; private set; }
        public string Otp { get; private set; }

        public CheckinPropagationForm(string rootPartNumber, string releaseType, List<TreeRow> rows, AtlasApiClient api)
        {
            _rootPartNumber = rootPartNumber;
            _releaseType = releaseType ?? "(unknown)";
            _rows = rows ?? new List<TreeRow>();
            _api = api;
            _parentOf = _rows.ToDictionary(r => r.PartNumber, r => r.ParentPartNumber);
            BuildUi();
            PopulateGrid();
            RecomputeStatus();
        }

        private void BuildUi()
        {
            Text = "Atlas — Check In";
            Size = new Size(960, 620);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(780, 420);

            int preTickCount = _rows.Count(r => r.PreCheckedAsChanged);
            string preTickHint = preTickCount == 0
                ? "(No baseline hashes — tick the parts you modified manually.)"
                : $"{preTickCount} part(s) auto-ticked — their bytes changed vs the checked-out " +
                  "baseline. Un-tick / add ticks as needed; parent assemblies will be revision-bumped " +
                  "automatically.";

            var hdr = new Label
            {
                Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 0),
                Text = $"Check in: {_rootPartNumber}   (release_type: {_releaseType})\n\n" +
                       preTickHint,
            };

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                AutoGenerateColumns = false,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                EditMode = DataGridViewEditMode.EditOnEnter,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = 28,
            };
            _grid.RowTemplate.Height = 26;
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Modified", Name = "modified", Width = 80 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Part Number", Name = "part_number", Width = 130, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Filename", Name = "filename", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Depth", Name = "depth", Width = 60, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Will bump as", Name = "status", Width = 130, ReadOnly = true });
            _grid.CellValueChanged += (s, e) =>
            {
                if (e.ColumnIndex == _grid.Columns["modified"].Index) RecomputeStatus();
            };
            _grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };

            var bottom = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };

            bottom.Controls.Add(new Label { Text = "Comment (optional):", Location = new Point(10, 6), AutoSize = true });
            _commentBox = new TextBox
            {
                Location = new Point(10, 26),
                Width = bottom.Width - 28,
                Height = 50,
                Multiline = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
                MaxLength = 1000,
            };
            bottom.Controls.Add(_commentBox);

            bottom.Controls.Add(new Label { Text = "OTP (sent to your email):", Location = new Point(10, 86), AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
            _otpBox = new TextBox
            {
                Location = new Point(10, 106),
                Width = 140,
                MaxLength = 6,
                Font = new Font(FontFamily.GenericMonospace, 11),
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
            };
            bottom.Controls.Add(_otpBox);
            _sendOtpBtn = new Button
            {
                Text = "Send OTP",
                Location = new Point(158, 105),
                Width = 100,
                Height = 25,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
            };
            _sendOtpBtn.Click += async (s, e) => await OnSendOtpAsync();
            bottom.Controls.Add(_sendOtpBtn);
            _otpStatusLabel = new Label
            {
                Location = new Point(266, 109),
                AutoSize = true,
                ForeColor = Color.DimGray,
                Text = "click Send OTP — code valid for 3 minutes.",
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
            };
            bottom.Controls.Add(_otpStatusLabel);

            _summaryLabel = new Label
            {
                Location = new Point(10, 145),
                AutoSize = true,
                ForeColor = Color.DimGray,
                Text = "—",
            };
            bottom.Controls.Add(_summaryLabel);

            var ok = new Button { Text = "Confirm Check In", Width = 140, Height = 28, DialogResult = DialogResult.None, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            ok.Location = new Point(bottom.Width - 290, 160);
            ok.Click += (s, e) =>
            {
                string otp = (_otpBox.Text ?? "").Trim();
                if (otp.Length != 6 || !otp.All(char.IsDigit))
                {
                    MessageBox.Show("Enter the 6-digit OTP from your email.", "Atlas — Check In",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _otpBox.Focus();
                    return;
                }
                ChangedPartNumbers = _rows
                    .Where(r => IsChecked(r.PartNumber))
                    .Select(r => r.PartNumber)
                    .ToList();
                Comment = _commentBox.Text ?? "";
                Otp = otp;
                DialogResult = DialogResult.OK;
                Close();
            };
            bottom.Controls.Add(ok);

            var cancel = new Button { Text = "Cancel", Width = 100, Height = 28, DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            cancel.Location = new Point(bottom.Width - 130, 160);
            bottom.Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;

            var outer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
            };
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 72f));   // hdr
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));   // grid
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 200f));  // bottom
            outer.Controls.Add(hdr,    0, 0);
            outer.Controls.Add(_grid,  0, 1);
            outer.Controls.Add(bottom, 0, 2);
            Controls.Add(outer);
        }

        private async Task OnSendOtpAsync()
        {
            try
            {
                _sendOtpBtn.Enabled = false;
                _otpStatusLabel.ForeColor = Color.DimGray;
                _otpStatusLabel.Text = "sending…";
                await _api.RequestReleaseRevisionOtpAsync();
                _otpStatusLabel.ForeColor = Color.SeaGreen;
                _otpStatusLabel.Text = "OTP sent — check your email.";
            }
            catch (Exception ex)
            {
                _otpStatusLabel.ForeColor = Color.Firebrick;
                _otpStatusLabel.Text = "send failed: " + ex.Message;
            }
            finally
            {
                _sendOtpBtn.Enabled = true;
            }
        }

        private void PopulateGrid()
        {
            foreach (var r in _rows.OrderByDescending(r => r.Depth).ThenBy(r => r.PartNumber))
            {
                int idx = _grid.Rows.Add(
                    r.PreCheckedAsChanged,
                    r.PartNumber,
                    r.Filename ?? "—",
                    new string('·', r.Depth) + r.Depth,
                    "—");
                _grid.Rows[idx].Tag = r.PartNumber;
            }
        }

        private bool IsChecked(string partNumber)
        {
            foreach (DataGridViewRow row in _grid.Rows)
                if ((string)row.Tag == partNumber)
                    return Convert.ToBoolean(row.Cells["modified"].Value ?? false);
            return false;
        }

        private void RecomputeStatus()
        {
            var changedSet = new HashSet<string>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (Convert.ToBoolean(row.Cells["modified"].Value ?? false))
                    changedSet.Add((string)row.Tag);
            }

            var ancestorSet = new HashSet<string>();
            foreach (var pn in changedSet)
            {
                string cur = _parentOf.TryGetValue(pn, out var p) ? p : null;
                while (!string.IsNullOrEmpty(cur))
                {
                    ancestorSet.Add(cur);
                    cur = _parentOf.TryGetValue(cur, out var next) ? next : null;
                }
            }

            foreach (DataGridViewRow row in _grid.Rows)
            {
                string pn = (string)row.Tag;
                string status;
                Color colour;
                if (changedSet.Contains(pn)) { status = "changed";  colour = Color.DarkOrange; }
                else if (ancestorSet.Contains(pn)) { status = "ancestor"; colour = Color.SteelBlue; }
                else { status = "—"; colour = Color.DimGray; }
                row.Cells["status"].Value = status;
                row.Cells["status"].Style.ForeColor = colour;
            }

            int totalBump = changedSet.Count + ancestorSet.Count;
            _summaryLabel.Text = totalBump == 0
                ? "Nothing will be bumped — tick at least one Modified box."
                : $"{changedSet.Count} changed + {ancestorSet.Count} ancestor = {totalBump} revision bumps queued.";
        }
    }
}
