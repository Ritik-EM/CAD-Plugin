using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using AtlasCadPlugin.Auth;

namespace AtlasCadPlugin.Forms
{
    /// <summary>
    /// Lists every historic version of a single assembly. Designer can pick
    /// a version and download its files to a folder for inspection
    /// (does not affect the current checked-out copy or change Atlas state).
    ///
    /// Admins can also flag a previous version as the new "active" version
    /// via the SetActiveVersion endpoint (P2.2) — wired here in the
    /// "Restore" button.
    /// </summary>
    public class VersionHistoryForm : Form
    {
        private readonly AtlasApiClient _api;
        private readonly string _assemblyId;
        private readonly string _assemblyName;

        private DataGridView _grid;
        private Button _downloadButton;
        private Button _restoreButton;
        private Button _closeButton;
        private Label _statusLabel;

        private List<VersionSummaryDto> _rows = new List<VersionSummaryDto>();

        public VersionHistoryForm(AtlasApiClient api, string assemblyId, string assemblyName)
        {
            _api = api;
            _assemblyId = assemblyId;
            _assemblyName = assemblyName;

            Text = $"Atlas — Version History: {assemblyName}";
            Width = 900;
            Height = 520;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(700, 400);

            BuildLayout();
            Load += async (s, e) => await ReloadAsync();
        }

        private void BuildLayout()
        {
            _downloadButton = new Button { Text = "Download…", Location = new Point(10, 10), Width = 120 };
            _downloadButton.Click += async (s, e) => await DownloadSelected();
            Controls.Add(_downloadButton);

            _restoreButton = new Button { Text = "Set as Active", Location = new Point(140, 10), Width = 120 };
            _restoreButton.Click += async (s, e) => await RestoreSelected();
            Controls.Add(_restoreButton);

            _closeButton = new Button
            {
                Text = "Close",
                Location = new Point(780, 10),
                Width = 90,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                DialogResult = DialogResult.Cancel,
            };
            _closeButton.Click += (s, e) => Close();
            Controls.Add(_closeButton);

            _statusLabel = new Label
            {
                Location = new Point(10, 450),
                Width = 860,
                Height = 22,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Text = "Ready.",
            };
            Controls.Add(_statusLabel);

            _grid = new DataGridView
            {
                Location = new Point(10, 50),
                Width = 860,
                Height = 390,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoGenerateColumns = false,
                RowHeadersVisible = false,
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Version", Name = "v", Width = 70 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Date", Name = "date", Width = 170 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Uploaded by", Name = "by", Width = 200 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Files", Name = "files", Width = 60 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Bumps", Name = "bumps", Width = 60 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Comment", Name = "comment", Width = 240 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Active?", Name = "active", Width = 60 });
            Controls.Add(_grid);
        }

        private async Task ReloadAsync()
        {
            _statusLabel.Text = "Loading…";
            try
            {
                _rows = await _api.ListVersionsAsync(_assemblyId);
                _grid.Rows.Clear();
                foreach (var v in _rows)
                {
                    int idx = _grid.Rows.Add(
                        v.version_number,
                        v.uploaded_at,
                        v.uploaded_by,
                        v.file_count,
                        v.revision_bumps?.Count ?? 0,
                        v.comment,
                        v.is_current ? "✓" : ""
                    );
                    if (v.is_current)
                        _grid.Rows[idx].DefaultCellStyle.BackColor = Color.Honeydew;
                }
                _statusLabel.Text = $"{_rows.Count} versions.";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "Error: " + ex.Message;
            }
        }

        private VersionSummaryDto SelectedVersion()
        {
            if (_grid.SelectedRows.Count == 0) return null;
            int idx = _grid.SelectedRows[0].Index;
            if (idx < 0 || idx >= _rows.Count) return null;
            return _rows[idx];
        }

        private async Task DownloadSelected()
        {
            var sel = SelectedVersion();
            if (sel == null)
            {
                MessageBox.Show("Pick a version first.", "Atlas", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var picker = new FolderBrowserDialog
            {
                Description = $"Download {_assemblyName} v{sel.version_number} to…",
                ShowNewFolderButton = true,
            })
            {
                if (picker.ShowDialog() != DialogResult.OK) return;
                string targetDir = Path.Combine(picker.SelectedPath,
                    $"{_assemblyName}_v{sel.version_number}");

                _statusLabel.Text = $"Downloading v{sel.version_number}…";
                try
                {
                    var detail = await _api.GetVersionAsync(_assemblyId, sel.version_number);
                    Directory.CreateDirectory(targetDir);
                    int n = 0;
                    foreach (var f in detail.files)
                    {
                        n++;
                        _statusLabel.Text = $"Downloading {n}/{detail.files.Count}: {f.filename}";
                        Application.DoEvents();
                        string targetPath = Path.Combine(targetDir,
                            f.relative_path.Replace('/', Path.DirectorySeparatorChar));
                        await _api.DownloadFileAsync(f.download_url, targetPath);
                    }
                    _statusLabel.Text = $"Downloaded {detail.files.Count} files to {targetDir}.";
                    System.Diagnostics.Process.Start("explorer.exe", targetDir);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Download failed:\n\n" + ex.Message, "Atlas",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    _statusLabel.Text = "Download failed.";
                }
            }
        }

        private async Task RestoreSelected()
        {
            var sel = SelectedVersion();
            if (sel == null) return;
            if (sel.is_current)
            {
                MessageBox.Show("This is already the current version.", "Atlas");
                return;
            }

            if (MessageBox.Show(
                    $"Make v{sel.version_number} the active version?\n\n" +
                    $"This does NOT delete v{_rows[0].version_number} — it creates a new " +
                    $"version that copies v{sel.version_number}'s files. Use only for rollbacks.",
                    "Atlas — Set as Active",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            try
            {
                _statusLabel.Text = "Restoring…";
                var result = await _api.SetActiveVersionAsync(_assemblyId, sel.version_number);
                _statusLabel.Text = $"v{sel.version_number} restored as v{result.version_number}.";
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Restore failed:\n\n" + ex.Message, "Atlas",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                _statusLabel.Text = "Restore failed.";
            }
        }
    }
}
