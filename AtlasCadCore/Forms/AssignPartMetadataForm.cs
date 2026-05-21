using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AtlasCadCore.ApiClient;

namespace AtlasCadCore.Forms
{
    /// <summary>
    /// Shown by UploadToPartMaster when the upload returned missing_parts —
    /// each row is one part that has no part_master entry yet. User fills the
    /// metadata fields, OK builds the CreateBatchEntryDto list.
    ///
    /// Defaults panel at the top lets the user fill the common fields once
    /// and apply them to every empty cell — meaningful when uploading a tree
    /// where most parts share project/group/model.
    /// </summary>
    public class AssignPartMetadataForm : Form
    {
        private readonly List<MissingPartDto> _missing;
        private DataGridView _grid;
        private TextBox _defProject, _defVehicleCat, _defModel, _defMajor, _defMinor;
        private ComboBox _defReleaseType;

        public List<CreateBatchEntryDto> Result { get; private set; }

        public AssignPartMetadataForm(List<MissingPartDto> missing)
        {
            _missing = missing ?? new List<MissingPartDto>();
            BuildUi();
            PopulateGrid();
        }

        private void BuildUi()
        {
            Text = "Atlas — Assign metadata to new parts";
            Size = new Size(1100, 600);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(900, 420);

            var hdr = new Label
            {
                Dock = DockStyle.Fill, Padding = new Padding(8, 6, 8, 0),
                Text = $"{_missing.Count} part(s) don't exist in part_master_library yet. " +
                       "Fill in the metadata below — Atlas will mint a fresh part_number for each.",
            };
            // (hdr / defaults / _grid / btnPanel all added below in the
            // correct order — Top + Bottom siblings first, Fill grid LAST.)

            // Defaults panel
            var defaults = new GroupBox { Dock = DockStyle.Fill, Text = "Defaults (apply to empty cells)", Padding = new Padding(8) };
            int x = 10, y = 22, lblW = 80, fldW = 100, gap = 8;

            void AddDefault(string label, Control field)
            {
                var l = new Label { Text = label, AutoSize = true, Location = new Point(x, y + 4) };
                defaults.Controls.Add(l);
                field.Location = new Point(x + lblW, y);
                field.Width = fldW;
                defaults.Controls.Add(field);
                x += lblW + fldW + gap;
            }

            _defProject = new TextBox();
            AddDefault("Project:", _defProject);
            _defVehicleCat = new TextBox();
            AddDefault("Vehicle cat:", _defVehicleCat);
            _defModel = new TextBox();
            AddDefault("Model:", _defModel);
            _defMajor = new TextBox();
            AddDefault("Major:", _defMajor);
            _defMinor = new TextBox();
            AddDefault("Minor:", _defMinor);
            _defReleaseType = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            _defReleaseType.Items.AddRange(new object[] { "PROTO", "PRODUCTION", "ALTERNATE_PART" });
            _defReleaseType.SelectedIndex = 0;
            AddDefault("Release:", _defReleaseType);

            var applyBtn = new Button { Text = "Apply defaults", Location = new Point(x, y - 1), Width = 130 };
            applyBtn.Click += (s, e) => ApplyDefaults();
            defaults.Controls.Add(applyBtn);

            Controls.Add(defaults);

            // Grid
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                AutoGenerateColumns = false,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = 28,
            };
            _grid.RowTemplate.Height = 26;
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Detected PN", Name = "detected", Width = 120, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Filename", Name = "filename", Width = 160, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Description", Name = "description", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Project", Name = "project", Width = 90 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Vehicle cat", Name = "vehicle", Width = 90 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Model", Name = "model", Width = 90 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Major", Name = "major", Width = 70 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Minor", Name = "minor", Width = 70 });
            var rtCol = new DataGridViewComboBoxColumn
            {
                HeaderText = "Release", Name = "release", Width = 110,
                FlatStyle = FlatStyle.Flat,
            };
            rtCol.Items.AddRange(new object[] { "PROTO", "PRODUCTION", "ALTERNATE_PART" });
            _grid.Columns.Add(rtCol);
            // _grid will be added LAST (after btnPanel below) so its top
            // row doesn't get clipped under the docked defaults panel.

            // Buttons
            var btnPanel = new Panel { Dock = DockStyle.Fill, Height = 44 };
            var ok = new Button { Text = "Create & Upload", Location = new Point(btnPanel.Width - 280, 10), Anchor = AnchorStyles.Right, Width = 130, DialogResult = DialogResult.OK };
            ok.Click += (s, e) => OnOk();
            var cancel = new Button { Text = "Cancel", Location = new Point(btnPanel.Width - 140, 10), Anchor = AnchorStyles.Right, Width = 100, DialogResult = DialogResult.Cancel };
            btnPanel.Controls.Add(ok);
            btnPanel.Controls.Add(cancel);
            // 4-row TableLayoutPanel — deterministic layout, no z-order tricks.
            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));    // hdr
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 100f));   // defaults groupbox
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));    // grid
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 48f));    // btnPanel
            outer.Controls.Add(hdr,       0, 0);
            outer.Controls.Add(defaults,  0, 1);
            outer.Controls.Add(_grid,     0, 2);
            outer.Controls.Add(btnPanel,  0, 3);
            Controls.Add(outer);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void PopulateGrid()
        {
            foreach (var m in _missing)
            {
                _grid.Rows.Add(
                    m.part_number,
                    m.filename ?? "—",
                    m.detected_description ?? "",
                    "", "", "", "", "", "PROTO");
            }
            _grid.PerformLayout();
            _grid.Refresh();
            if (_grid.Rows.Count > 0) _grid.CurrentCell = _grid.Rows[0].Cells["detected"];
        }

        private void ApplyDefaults()
        {
            string proj = _defProject.Text.Trim();
            string vcat = _defVehicleCat.Text.Trim();
            string model = _defModel.Text.Trim();
            string major = _defMajor.Text.Trim();
            string minor = _defMinor.Text.Trim();
            string rt = _defReleaseType.SelectedItem as string ?? "PROTO";

            foreach (DataGridViewRow row in _grid.Rows)
            {
                FillIfEmpty(row.Cells["project"], proj);
                FillIfEmpty(row.Cells["vehicle"], vcat);
                FillIfEmpty(row.Cells["model"], model);
                FillIfEmpty(row.Cells["major"], major);
                FillIfEmpty(row.Cells["minor"], minor);
                if (string.IsNullOrWhiteSpace(row.Cells["release"].Value as string))
                    row.Cells["release"].Value = rt;
            }
        }

        private static void FillIfEmpty(DataGridViewCell cell, string val)
        {
            if (string.IsNullOrWhiteSpace(cell.Value as string) && !string.IsNullOrEmpty(val))
                cell.Value = val;
        }

        private void OnOk()
        {
            // Mandatory: project_identifier, major_group, minor_group, release_type.
            // description + model + vehicle_category are accepted as nullable.
            var entries = new List<CreateBatchEntryDto>();
            for (int i = 0; i < _grid.Rows.Count; i++)
            {
                var row = _grid.Rows[i];
                string project = (row.Cells["project"].Value as string)?.Trim();
                string major = (row.Cells["major"].Value as string)?.Trim();
                string minor = (row.Cells["minor"].Value as string)?.Trim();
                string release = row.Cells["release"].Value as string;
                if (string.IsNullOrEmpty(project) || string.IsNullOrEmpty(major) ||
                    string.IsNullOrEmpty(minor) || string.IsNullOrEmpty(release))
                {
                    MessageBox.Show($"Row {i + 1}: Project / Major / Minor / Release are required.", "Atlas",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    DialogResult = DialogResult.None;
                    return;
                }
                entries.Add(new CreateBatchEntryDto
                {
                    detected_part_number = row.Cells["detected"].Value as string,
                    project_identifier = project,
                    vehicle_category = NullIfBlank(row.Cells["vehicle"].Value as string),
                    model = NullIfBlank(row.Cells["model"].Value as string),
                    major_group = major,
                    minor_group = minor,
                    release_type = release,
                    description = NullIfBlank(row.Cells["description"].Value as string),
                });
            }
            Result = entries;
        }

        private static string NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
