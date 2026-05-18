using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace AtlasCadPlugin.Forms
{
    /// <summary>
    /// Shown after the check-in preview returns. Lists every part the backend
    /// detected as changed and lets the designer tick which ones should
    /// actually get a new part_master revision. Default: every changed part
    /// is ticked (the safe assumption).
    /// </summary>
    public class ConfirmRevisionBumpsForm : Form
    {
        private readonly CheckinPreviewResult _preview;
        private DataGridView _grid;
        private TextBox _commentBox;
        private Button _confirmButton;
        private Button _cancelButton;
        private Label _summaryLabel;

        public List<RevisionBump> RevisionBumps { get; private set; } = new List<RevisionBump>();
        public string Comment { get; private set; } = "";

        public ConfirmRevisionBumpsForm(CheckinPreviewResult preview)
        {
            _preview = preview;
            Text = "Atlas — Confirm Check-In";
            Width = 760;
            Height = 560;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(600, 420);

            BuildLayout();
            PopulateGrid();
        }

        private void BuildLayout()
        {
            int totalChanged = _preview?.changed?.Count ?? 0;
            int totalUnchanged = _preview?.unchanged?.Count ?? 0;
            int totalAdded = _preview?.added?.Count ?? 0;

            _summaryLabel = new Label
            {
                Location = new Point(10, 10),
                Width = 720,
                Height = 50,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Text = $"Based on version {_preview?.based_on_version}: " +
                       $"{totalChanged} changed, {totalUnchanged} unchanged, {totalAdded} added.\n\n" +
                       $"Tick the parts you actually modified — those get a new revision in " +
                       $"part_master_library. Untick parts you don't want bumped (e.g. you opened " +
                       $"and saved without intending to change them).",
            };
            Controls.Add(_summaryLabel);

            _grid = new DataGridView
            {
                Location = new Point(10, 70),
                Width = 720,
                Height = 320,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoGenerateColumns = false,
                RowHeadersVisible = false,
            };
            _grid.Columns.Add(new DataGridViewCheckBoxColumn
            {
                HeaderText = "Bump?",
                Name = "bump",
                Width = 60,
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "Part Number",
                Name = "part_number",
                Width = 150,
                ReadOnly = true,
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "Filename",
                Name = "filename",
                Width = 300,
                ReadOnly = true,
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "Status",
                Name = "status",
                Width = 180,
                ReadOnly = true,
            });
            Controls.Add(_grid);

            var commentLabel = new Label
            {
                Text = "Comment (optional):",
                Location = new Point(10, 400),
                Width = 200,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };
            Controls.Add(commentLabel);

            _commentBox = new TextBox
            {
                Location = new Point(10, 420),
                Width = 720,
                Height = 50,
                Multiline = true,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };
            Controls.Add(_commentBox);

            _cancelButton = new Button
            {
                Text = "Cancel",
                Location = new Point(560, 480),
                Width = 80,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                DialogResult = DialogResult.Cancel,
            };
            Controls.Add(_cancelButton);

            _confirmButton = new Button
            {
                Text = "Confirm Check-In",
                Location = new Point(640, 480),
                Width = 100,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            };
            _confirmButton.Click += (s, e) => OnConfirm();
            Controls.Add(_confirmButton);

            AcceptButton = _confirmButton;
            CancelButton = _cancelButton;
        }

        private void PopulateGrid()
        {
            if (_preview?.changed != null)
            {
                foreach (var row in _preview.changed)
                {
                    int idx = _grid.Rows.Add(true, row.part_number, row.filename, "Changed (will bump)");
                    _grid.Rows[idx].DefaultCellStyle.BackColor = Color.LightYellow;
                }
            }
            if (_preview?.added != null)
            {
                foreach (var row in _preview.added)
                {
                    // Added parts don't need a bump — they're new entries at
                    // their current revision. Show them as info-only so the
                    // designer sees what's being introduced.
                    int idx = _grid.Rows.Add(false, row.part_number, row.filename, "New in this version");
                    _grid.Rows[idx].DefaultCellStyle.BackColor = Color.AliceBlue;
                    _grid.Rows[idx].Cells["bump"].ReadOnly = true;
                }
            }
        }

        private void OnConfirm()
        {
            var bumps = new List<RevisionBump>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                bool check = false;
                var cell = row.Cells["bump"];
                if (cell.Value != null && bool.TryParse(cell.Value.ToString(), out bool parsed))
                    check = parsed;
                if (!check) continue;

                // Only "Changed" rows can be bumped; "New" rows are read-only.
                string status = row.Cells["status"].Value?.ToString() ?? "";
                if (!status.StartsWith("Changed")) continue;

                bumps.Add(new RevisionBump
                {
                    part_number = row.Cells["part_number"].Value?.ToString(),
                    release_type = "PRODUCTION",
                });
            }
            RevisionBumps = bumps;
            Comment = _commentBox.Text ?? "";
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
