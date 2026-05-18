using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AtlasCadPlugin.Forms
{
    /// <summary>
    /// Shown before Upload Assembly when one or more files don't have a
    /// part_number that exists in part_master_library. Designer can retype
    /// the part_number (in case of typo), click Verify to re-check, and
    /// finally click Upload once every row is OK.
    /// </summary>
    public class AssignPartNumbersForm : Form
    {
        private readonly AtlasApiClient _api;
        private readonly List<AssemblyFileRef> _files;

        private DataGridView _grid;
        private Button _verifyButton;
        private Button _uploadButton;
        private Button _cancelButton;
        private Label _statusLabel;

        public bool AllResolved { get; private set; }

        public AssignPartNumbersForm(AtlasApiClient api, List<AssemblyFileRef> files, PartLookupResult initialLookup)
        {
            _api = api;
            _files = files;

            Text = "Atlas — Assign Part Numbers";
            Width = 800;
            Height = 520;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(640, 400);

            BuildLayout();
            PopulateGrid(initialLookup);
        }

        private void BuildLayout()
        {
            _grid = new DataGridView
            {
                Location = new Point(10, 50),
                Width = 770,
                Height = 380,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoGenerateColumns = false,
                RowHeadersVisible = false,
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "Filename",
                Name = "filename",
                Width = 320,
                ReadOnly = true,
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "Part Number",
                Name = "part_number",
                Width = 160,
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "Status",
                Name = "status",
                Width = 270,
                ReadOnly = true,
            });
            Controls.Add(_grid);

            var headerLabel = new Label
            {
                Text = "Files in red couldn't be matched to a part in Atlas. Type the correct " +
                       "part number, then click Verify. Upload becomes available once all rows are OK.",
                Location = new Point(10, 10),
                Width = 770,
                Height = 36,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            Controls.Add(headerLabel);

            _statusLabel = new Label
            {
                Location = new Point(10, 440),
                Width = 500,
                Height = 22,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };
            Controls.Add(_statusLabel);

            _verifyButton = new Button
            {
                Text = "Verify",
                Location = new Point(520, 440),
                Width = 80,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            };
            _verifyButton.Click += async (s, e) => await Verify();
            Controls.Add(_verifyButton);

            _cancelButton = new Button
            {
                Text = "Cancel",
                Location = new Point(610, 440),
                Width = 80,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                DialogResult = DialogResult.Cancel,
            };
            Controls.Add(_cancelButton);

            _uploadButton = new Button
            {
                Text = "Upload",
                Location = new Point(700, 440),
                Width = 80,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Enabled = false,
            };
            _uploadButton.Click += (s, e) =>
            {
                CommitGridToFiles();
                AllResolved = true;
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(_uploadButton);

            CancelButton = _cancelButton;
        }

        private void PopulateGrid(PartLookupResult lookup)
        {
            _grid.Rows.Clear();
            var foundSet = new HashSet<string>(lookup?.found ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            foreach (var f in _files)
            {
                int row = _grid.Rows.Add(f.Filename, f.PartNumber ?? "", "");
                ApplyStatus(_grid.Rows[row], f.PartNumber, foundSet);
            }
            UpdateUploadButtonState();
        }

        private void ApplyStatus(DataGridViewRow row, string partNumber, HashSet<string> foundSet)
        {
            if (string.IsNullOrEmpty(partNumber))
            {
                row.Cells["status"].Value = "✗ no part number assigned";
                row.DefaultCellStyle.BackColor = Color.MistyRose;
            }
            else if (!PartNumberParser.LooksValid(partNumber))
            {
                row.Cells["status"].Value = "✗ invalid format (expected 10 alphanumeric chars)";
                row.DefaultCellStyle.BackColor = Color.MistyRose;
            }
            else if (foundSet.Contains(partNumber.ToUpperInvariant()))
            {
                row.Cells["status"].Value = "✓ found in part_master_library";
                row.DefaultCellStyle.BackColor = Color.Honeydew;
            }
            else
            {
                row.Cells["status"].Value = "✗ not found in part_master_library";
                row.DefaultCellStyle.BackColor = Color.MistyRose;
            }
        }

        private async Task Verify()
        {
            _verifyButton.Enabled = false;
            _statusLabel.Text = "Verifying…";
            try
            {
                var candidates = new List<string>();
                for (int i = 0; i < _grid.Rows.Count; i++)
                {
                    string pn = (_grid.Rows[i].Cells["part_number"].Value as string)?.Trim().ToUpperInvariant();
                    if (PartNumberParser.LooksValid(pn)) candidates.Add(pn);
                }

                PartLookupResult lookup = candidates.Count > 0
                    ? await _api.LookupPartNumbersAsync(candidates)
                    : new PartLookupResult { found = new List<string>(), missing = new List<string>() };

                var foundSet = new HashSet<string>(lookup.found ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                int okCount = 0;
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    string pn = (row.Cells["part_number"].Value as string)?.Trim().ToUpperInvariant();
                    ApplyStatus(row, pn, foundSet);
                    if (!string.IsNullOrEmpty(pn) && foundSet.Contains(pn)) okCount++;
                }
                _statusLabel.Text = $"{okCount}/{_grid.Rows.Count} resolved.";
                UpdateUploadButtonState();
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "Verify failed: " + ex.Message;
            }
            finally { _verifyButton.Enabled = true; }
        }

        private void UpdateUploadButtonState()
        {
            bool allOk = _grid.Rows.Cast<DataGridViewRow>().All(r =>
                r.Cells["status"].Value?.ToString().StartsWith("✓") == true);
            _uploadButton.Enabled = allOk;
        }

        private void CommitGridToFiles()
        {
            // Match by filename — order preserved when grid was built.
            for (int i = 0; i < _files.Count; i++)
            {
                string pn = (_grid.Rows[i].Cells["part_number"].Value as string)?.Trim().ToUpperInvariant();
                _files[i].PartNumber = pn;
            }
        }
    }
}
