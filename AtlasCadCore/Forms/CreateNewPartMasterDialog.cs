using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using AtlasCadCore.ApiClient;

namespace AtlasCadCore.Forms
{
    /// <summary>
    /// P7.59: lightweight in-plugin form for creating a new part_master
    /// entry on atlas without leaving the Upload flow. Mirrors the
    /// minimum-required fields atlas-api's create_batch endpoint accepts.
    /// On success, exposes <see cref="MintedPartNumber"/> so the caller
    /// (MissingPartsTableForm) can wire it back into the upload payload.
    /// </summary>
    public class CreateNewPartMasterDialog : Form
    {
        private readonly AtlasApiClient _api;
        private readonly string _detectedPartNumber;
        private readonly string _detectedFilename;

        private TextBox _projectBox, _majorBox, _minorBox, _descriptionBox;
        private ComboBox _releaseTypeCombo;
        private Button _createBtn, _cancelBtn;
        private Label _statusLabel;

        public string MintedPartNumber { get; private set; }
        public string MintedReleaseType { get; private set; }

        public CreateNewPartMasterDialog(AtlasApiClient api, string detectedPartNumber, string detectedFilename)
        {
            _api = api;
            _detectedPartNumber = detectedPartNumber ?? "";
            _detectedFilename = detectedFilename ?? "";

            Text = "Atlas — Create New Part Master";
            Size = new Size(560, 460);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false; MaximizeBox = false;

            BuildUi();
        }

        private void BuildUi()
        {
            var hdr = new Label
            {
                Location = new Point(20, 15),
                Size = new Size(520, 60),
                Text =
                    $"Create a new part_master entry on atlas for:\n" +
                    $"   Filename: {_detectedFilename}\n" +
                    $"   Detected: {_detectedPartNumber}\n",
            };
            Controls.Add(hdr);

            int y = 85;
            int labelX = 20, fieldX = 180, fieldW = 340;

            Controls.Add(new Label { Text = "Project identifier:", Location = new Point(labelX, y + 3), AutoSize = true });
            _projectBox = new TextBox { Location = new Point(fieldX, y), Width = fieldW };
            Controls.Add(_projectBox);
            y += 32;

            Controls.Add(new Label { Text = "Major group:", Location = new Point(labelX, y + 3), AutoSize = true });
            _majorBox = new TextBox { Location = new Point(fieldX, y), Width = fieldW };
            Controls.Add(_majorBox);
            y += 32;

            Controls.Add(new Label { Text = "Minor group:", Location = new Point(labelX, y + 3), AutoSize = true });
            _minorBox = new TextBox { Location = new Point(fieldX, y), Width = fieldW };
            Controls.Add(_minorBox);
            y += 32;

            Controls.Add(new Label { Text = "Release type:", Location = new Point(labelX, y + 3), AutoSize = true });
            _releaseTypeCombo = new ComboBox
            {
                Location = new Point(fieldX, y), Width = fieldW,
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            _releaseTypeCombo.Items.AddRange(new object[] { "PROTO", "PRODUCTION", "ALTERNATE_PART" });
            _releaseTypeCombo.SelectedIndex = 0;
            Controls.Add(_releaseTypeCombo);
            y += 32;

            Controls.Add(new Label { Text = "Description:", Location = new Point(labelX, y + 3), AutoSize = true });
            _descriptionBox = new TextBox
            {
                Location = new Point(fieldX, y), Width = fieldW,
                Multiline = true, Height = 60,
                ScrollBars = ScrollBars.Vertical,
            };
            Controls.Add(_descriptionBox);
            y += 80;

            _statusLabel = new Label
            {
                Location = new Point(labelX, y + 4),
                Size = new Size(520, 20),
                ForeColor = Color.DimGray,
                Text = "",
            };
            Controls.Add(_statusLabel);
            y += 28;

            _createBtn = new Button
            {
                Text = "Create on Atlas",
                Location = new Point(280, y), Width = 130, Height = 30,
            };
            _createBtn.Click += async (s, e) => await OnCreateAsync();
            Controls.Add(_createBtn);

            _cancelBtn = new Button
            {
                Text = "Cancel",
                Location = new Point(420, y), Width = 100, Height = 30,
                DialogResult = DialogResult.Cancel,
            };
            Controls.Add(_cancelBtn);

            AcceptButton = _createBtn;
            CancelButton = _cancelBtn;
        }

        private async Task OnCreateAsync()
        {
            string project = (_projectBox.Text ?? "").Trim();
            string major = (_majorBox.Text ?? "").Trim();
            string minor = (_minorBox.Text ?? "").Trim();
            string releaseType = _releaseTypeCombo.SelectedItem as string;
            string description = (_descriptionBox.Text ?? "").Trim();

            if (string.IsNullOrEmpty(project)) { Warn("Project identifier is required."); _projectBox.Focus(); return; }
            if (string.IsNullOrEmpty(major)) { Warn("Major group is required."); _majorBox.Focus(); return; }
            if (string.IsNullOrEmpty(minor)) { Warn("Minor group is required."); _minorBox.Focus(); return; }
            if (string.IsNullOrEmpty(releaseType)) { Warn("Release type is required."); return; }

            SetBusy(true, "Creating on atlas…");
            try
            {
                var entry = new CreateBatchEntryDto
                {
                    detected_part_number = _detectedPartNumber,
                    project_identifier = project,
                    major_group = major,
                    minor_group = minor,
                    release_type = releaseType,
                    description = string.IsNullOrEmpty(description) ? null : description,
                };
                var result = await _api.CreateBatchAsync(new List<CreateBatchEntryDto> { entry });
                if (result?.created == null || result.created.Count == 0)
                {
                    Warn("Atlas didn't return a new part_number — check atlas-api logs.");
                    return;
                }
                var created = result.created[0];
                MintedPartNumber = created.new_part_number;
                MintedReleaseType = created.release_type;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                Warn("Create failed: " + ex.Message);
            }
            finally { SetBusy(false, null); }
        }

        private void Warn(string msg)
        {
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = msg;
        }

        private void SetBusy(bool busy, string status)
        {
            _createBtn.Enabled = !busy;
            _cancelBtn.Enabled = !busy;
            if (status != null)
            {
                _statusLabel.ForeColor = Color.DimGray;
                _statusLabel.Text = status;
            }
        }
    }
}
