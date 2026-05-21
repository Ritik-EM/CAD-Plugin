using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using AtlasCadCore.Adapter;

namespace AtlasCadCore.Forms
{
    /// <summary>
    /// Modal dialog shown after Check Out when some child references couldn't
    /// be resolved from atlas (either the part isn't in part_master_library
    /// or its active revision has no native 3d_raw). User attaches a local
    /// file per missing child and chooses whether to release a brand-new
    /// revision (with OTP) or just append the file to the existing revision.
    /// </summary>
    public class MissingChildUploadForm : Form
    {
        public class Row
        {
            public string PartNumber;       // parsed from filename
            public string OriginalFilename; // what SW was looking for
            public string LocalPath;        // user-chosen local file (.sldprt / .sldasm)
        }

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

        public MissingChildUploadForm(List<MissingComponent> missing)
        {
            _rows = (missing ?? new List<MissingComponent>())
                .Select(m => new Row
                {
                    PartNumber = m.PartNumber ?? "(unknown)",
                    OriginalFilename = m.Filename,
                    LocalPath = null,
                })
                .ToList();
            BuildUi();
            Populate();
        }

        private void BuildUi()
        {
            Text = "Atlas — Upload Missing Parts";
            Size = new Size(960, 560);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(760, 380);

            var hdr = new Label
            {
                Dock = DockStyle.Top, Height = 50, Padding = new Padding(10, 8, 10, 0),
                Text = $"{_rows.Count} child part(s) couldn't be downloaded from atlas (no native file " +
                       "available). For each one, click Browse… to attach the local .sldprt/.sldasm. " +
                       "Leave a row blank to skip that part.",
            };
            Controls.Add(hdr);

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
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Part Number", Name = "pn", Width = 130, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Filename (expected by SW)", Name = "filename", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Picked local file", Name = "local", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
            var btnCol = new DataGridViewButtonColumn { HeaderText = "", Name = "browse", Text = "Browse…", UseColumnTextForButtonValue = true, Width = 90 };
            _grid.Columns.Add(btnCol);
            _grid.CellContentClick += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                if (_grid.Columns[e.ColumnIndex].Name != "browse") return;
                using (var ofd = new OpenFileDialog
                {
                    Title = "Pick local file for " + _rows[e.RowIndex].PartNumber,
                    Filter = "Native CAD|*.sldprt;*.sldasm|All files|*.*",
                    CheckFileExists = true,
                })
                {
                    if (ofd.ShowDialog() != DialogResult.OK) return;
                    _rows[e.RowIndex].LocalPath = ofd.FileName;
                    _grid.Rows[e.RowIndex].Cells["local"].Value = Path.GetFileName(ofd.FileName);
                }
            };
            Controls.Add(_grid);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 130, Padding = new Padding(10) };

            _releaseRevisionCheck = new CheckBox
            {
                Text = "Release as new revision (requires OTP)",
                Location = new Point(10, 8),
                AutoSize = true,
                Checked = false,
            };
            _releaseRevisionCheck.CheckedChanged += (s, e) => UpdateOtpUi();
            bottom.Controls.Add(_releaseRevisionCheck);

            bottom.Controls.Add(new Label { Text = "OTP:", Location = new Point(10, 38), AutoSize = true });
            _otpBox = new TextBox
            {
                Location = new Point(50, 35),
                Width = 120,
                MaxLength = 6,
                Font = new Font(FontFamily.GenericMonospace, 11),
            };
            bottom.Controls.Add(_otpBox);
            _sendOtpBtn = new Button { Text = "Send OTP", Location = new Point(180, 33), Width = 100 };
            _sendOtpBtn.Click += (s, e) => SendOtpRequested?.Invoke();
            bottom.Controls.Add(_sendOtpBtn);
            _otpHint = new Label
            {
                Location = new Point(290, 38),
                AutoSize = true,
                ForeColor = Color.DimGray,
                Text = "(only required if releasing a new revision)",
            };
            bottom.Controls.Add(_otpHint);

            var ok = new Button { Text = "Upload & Continue", Location = new Point(bottom.Width - 290, 88), Width = 160, Height = 28, Anchor = AnchorStyles.Right | AnchorStyles.Bottom, DialogResult = DialogResult.None };
            ok.Click += (s, e) =>
            {
                ReleaseNewRevision = _releaseRevisionCheck.Checked;
                if (ReleaseNewRevision)
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
                Result = _rows.Where(r => !string.IsNullOrEmpty(r.LocalPath)).ToList();
                DialogResult = DialogResult.OK;
                Close();
            };
            bottom.Controls.Add(ok);

            var cancel = new Button { Text = "Skip uploads", Location = new Point(bottom.Width - 120, 88), Width = 100, Height = 28, Anchor = AnchorStyles.Right | AnchorStyles.Bottom, DialogResult = DialogResult.Cancel };
            bottom.Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(bottom);

            UpdateOtpUi();
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
            {
                _grid.Rows.Add(r.PartNumber, r.OriginalFilename, "", null);
            }
        }
    }
}
