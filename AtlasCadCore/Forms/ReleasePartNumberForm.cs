using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AtlasCadCore.ApiClient;

namespace AtlasCadCore.Forms
{
    /// <summary>
    /// "Release Part Code" — the in-plugin equivalent of atlas-ui's
    /// Release Non-Standard Part Number page. Same cascading metadata dropdowns
    /// (vehicle category → model, project/category → major → minor group,
    /// aggregate → sub-aggregate), same live "next part number" preview, same
    /// PROTO release type, and NO OTP (matching atlas-ui's release UX).
    ///
    /// The actual mint goes through the CAD create-batch endpoint (which calls
    /// release_part_number with otp_required=False) so it reuses the plugin's
    /// existing auth path; the dropdown metadata is read straight from the same
    /// part-master endpoints atlas-ui uses.
    /// </summary>
    public class ReleasePartNumberForm : Form
    {
        private const string ProjectIdentifier = "non_standard";
        private const string ReleaseType = "PROTO";

        private readonly AtlasApiClient _api;

        private ComboBox _vehicleCombo, _modelCombo, _majorCombo, _minorCombo;
        private ComboBox _aggregateCombo, _subAggregateCombo, _sourceCombo, _availableCombo;
        private TextBox _descriptionBox;
        private Label _previewValue, _statusLabel;
        private Button _releaseBtn, _cancelBtn;

        private GroupTreeDto _groupTree = new GroupTreeDto();
        private List<AggregateConfigDto> _aggregateConfigs = new List<AggregateConfigDto>();
        private string _currentPreview;
        private int _previewSeq;
        private bool _suppress;

        public string MintedPartNumber { get; private set; }

        public ReleasePartNumberForm(AtlasApiClient api)
        {
            _api = api;
            Text = "Atlas — Release Part Code";
            Size = new Size(620, 600);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false; MaximizeBox = false;
            BuildUi();
            Load += async (s, e) => await InitAsync();
        }

        private void BuildUi()
        {
            var hdr = new Label
            {
                Location = new Point(20, 12),
                Size = new Size(560, 36),
                Text = "Release a new (non-standard) part code on Atlas.\n" +
                       "Pick the configuration below — the part number is previewed live.",
                ForeColor = Color.FromArgb(60, 60, 60),
            };
            Controls.Add(hdr);

            int y = 56;
            const int labelX = 20, fieldX = 200, fieldW = 380, rowH = 32;

            _vehicleCombo = AddCombo("Vehicle category:", ref y, labelX, fieldX, fieldW, rowH);
            _vehicleCombo.Items.AddRange(new object[] { new NamedOption("3W", "3W"), new NamedOption("4W", "4W") });
            _vehicleCombo.SelectedIndexChanged += async (s, e) => await OnVehicleChanged();

            _modelCombo = AddCombo("Model:", ref y, labelX, fieldX, fieldW, rowH);
            _modelCombo.SelectedIndexChanged += async (s, e) => await OnModelChanged();

            _majorCombo = AddCombo("Major group:", ref y, labelX, fieldX, fieldW, rowH);
            _majorCombo.SelectedIndexChanged += async (s, e) => await OnMajorChanged();

            _minorCombo = AddCombo("Minor group:", ref y, labelX, fieldX, fieldW, rowH);
            _minorCombo.SelectedIndexChanged += async (s, e) => await OnLeafChanged();

            _aggregateCombo = AddCombo("Aggregate:", ref y, labelX, fieldX, fieldW, rowH);
            _aggregateCombo.SelectedIndexChanged += (s, e) => OnAggregateChanged();

            _subAggregateCombo = AddCombo("Sub-aggregate:", ref y, labelX, fieldX, fieldW, rowH);
            _subAggregateCombo.SelectedIndexChanged += (s, e) => UpdateReleaseEnabled();

            Controls.Add(new Label { Text = "Description:", Location = new Point(labelX, y + 3), AutoSize = true });
            _descriptionBox = new TextBox
            {
                Location = new Point(fieldX, y), Width = fieldW,
                Multiline = true, Height = 48, ScrollBars = ScrollBars.Vertical,
            };
            _descriptionBox.TextChanged += (s, e) => UpdateReleaseEnabled();
            Controls.Add(_descriptionBox);
            y += 56;

            _sourceCombo = AddCombo("Source:", ref y, labelX, fieldX, fieldW, rowH);
            _sourceCombo.Items.AddRange(new object[]
            {
                new NamedOption("V", "V"), new NamedOption("V_P", "V_P"),
                new NamedOption("OLC", "OLC"), new NamedOption("F", "F"),
            });
            _sourceCombo.SelectedIndexChanged += (s, e) => UpdateReleaseEnabled();

            _availableCombo = AddCombo("Available for service:", ref y, labelX, fieldX, fieldW, rowH);
            _availableCombo.Items.AddRange(new object[] { new NamedOption("false", "No"), new NamedOption("true", "Yes") });

            // Preview panel
            var prevLabel = new Label { Text = "Part number preview:", Location = new Point(labelX, y + 6), AutoSize = true };
            Controls.Add(prevLabel);
            _previewValue = new Label
            {
                Location = new Point(fieldX, y), Width = fieldW, Height = 26,
                Font = new Font(FontFamily.GenericMonospace, 12, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 102, 51),
                Text = "—",
            };
            Controls.Add(_previewValue);
            y += 36;

            _statusLabel = new Label
            {
                Location = new Point(labelX, y), Size = new Size(560, 20),
                ForeColor = Color.DimGray, Text = "",
            };
            Controls.Add(_statusLabel);
            y += 28;

            _releaseBtn = new Button
            {
                Text = "Release on Atlas", Location = new Point(330, y), Width = 150, Height = 32,
                Enabled = false,
            };
            _releaseBtn.Click += async (s, e) => await OnReleaseAsync();
            Controls.Add(_releaseBtn);

            _cancelBtn = new Button
            {
                Text = "Cancel", Location = new Point(490, y), Width = 90, Height = 32,
                DialogResult = DialogResult.Cancel,
            };
            Controls.Add(_cancelBtn);

            AcceptButton = _releaseBtn;
            CancelButton = _cancelBtn;
        }

        private ComboBox AddCombo(string label, ref int y, int labelX, int fieldX, int fieldW, int rowH)
        {
            Controls.Add(new Label { Text = label, Location = new Point(labelX, y + 3), AutoSize = true });
            var combo = new ComboBox
            {
                Location = new Point(fieldX, y), Width = fieldW,
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            Controls.Add(combo);
            y += rowH;
            return combo;
        }

        private async Task InitAsync()
        {
            // available_for_service defaults to "No" (backend default is False).
            _suppress = true;
            _availableCombo.SelectedIndex = 0;
            _suppress = false;
            try
            {
                SetStatus("Loading aggregate config…", false);
                _aggregateConfigs = await _api.FetchAggregateConfigsAsync();
                PopulateAggregates();
                SetStatus("Pick a vehicle category to begin.", false);
            }
            catch (Exception ex) { SetStatus("Couldn't load aggregate config: " + ex.Message, true); }
        }

        // ── cascading handlers ────────────────────────────────────────────────

        private async Task OnVehicleChanged()
        {
            if (_suppress) return;
            string vc = SelVal(_vehicleCombo);
            ClearCombo(_modelCombo); ClearCombo(_majorCombo); ClearCombo(_minorCombo);
            _groupTree = new GroupTreeDto();
            if (string.IsNullOrEmpty(vc)) { await RefreshPreview(); return; }
            try
            {
                SetStatus("Loading models & groups…", false);
                var modelsTask = _api.FetchModelOptionsAsync(vc);
                var groupsTask = _api.FetchGroupTreeAsync(ProjectIdentifier, vc);
                await Task.WhenAll(modelsTask, groupsTask);

                _suppress = true;
                _modelCombo.Items.AddRange(modelsTask.Result.Cast<object>().ToArray());
                _groupTree = groupsTask.Result;
                _majorCombo.Items.AddRange(_groupTree.Majors.Cast<object>().ToArray());
                _suppress = false;

                SetStatus(modelsTask.Result.Count == 0
                    ? "No models found for this category." : "Select a model and group.", false);
            }
            catch (Exception ex) { SetStatus("Load failed: " + ex.Message, true); }
            await RefreshPreview();
        }

        private async Task OnModelChanged()
        {
            if (_suppress) return;
            // Mirror atlas-ui: changing model resets the group selection.
            ClearSelection(_majorCombo); ClearCombo(_minorCombo);
            await RefreshPreview();
        }

        private async Task OnMajorChanged()
        {
            if (_suppress) return;
            string major = SelVal(_majorCombo);
            ClearCombo(_minorCombo);
            if (!string.IsNullOrEmpty(major) &&
                _groupTree.MinorsByMajor.TryGetValue(major, out var minors))
            {
                _suppress = true;
                _minorCombo.Items.AddRange(minors.Cast<object>().ToArray());
                _suppress = false;
            }
            await RefreshPreview();
        }

        private async Task OnLeafChanged()
        {
            if (_suppress) return;
            await RefreshPreview();
        }

        private void OnAggregateChanged()
        {
            if (_suppress) return;
            PopulateSubAggregates();
            UpdateReleaseEnabled();
        }

        private void PopulateAggregates()
        {
            _suppress = true;
            ClearCombo(_aggregateCombo);
            foreach (var agg in _aggregateConfigs.Select(c => c.aggregate)
                         .Where(a => !string.IsNullOrEmpty(a)).Distinct(StringComparer.OrdinalIgnoreCase))
                _aggregateCombo.Items.Add(new NamedOption(agg, agg));
            _suppress = false;
        }

        private void PopulateSubAggregates()
        {
            string agg = SelVal(_aggregateCombo);
            _suppress = true;
            ClearCombo(_subAggregateCombo);
            if (!string.IsNullOrEmpty(agg))
                foreach (var sub in _aggregateConfigs
                             .Where(c => string.Equals(c.aggregate, agg, StringComparison.OrdinalIgnoreCase)
                                         && !string.IsNullOrEmpty(c.sub_aggregate))
                             .Select(c => c.sub_aggregate))
                    _subAggregateCombo.Items.Add(new NamedOption(sub, sub));
            _suppress = false;
        }

        // ── preview + release gating ──────────────────────────────────────────

        private async Task RefreshPreview()
        {
            string vc = SelVal(_vehicleCombo), model = SelVal(_modelCombo);
            string major = SelVal(_majorCombo), minor = SelVal(_minorCombo);

            int seq = ++_previewSeq;
            if (string.IsNullOrEmpty(vc) || string.IsNullOrEmpty(model) ||
                string.IsNullOrEmpty(major) || string.IsNullOrEmpty(minor))
            {
                _currentPreview = null;
                _previewValue.Text = "—";
                UpdateReleaseEnabled();
                return;
            }

            _previewValue.Text = "…";
            try
            {
                string pn = await _api.GenerateNextPartNumberAsync(
                    ProjectIdentifier, vc, model, major, minor, ReleaseType);
                if (seq != _previewSeq) return;   // a newer change superseded us
                _currentPreview = pn;
                _previewValue.Text = string.IsNullOrEmpty(pn) ? "—" : pn;
            }
            catch (Exception ex)
            {
                if (seq != _previewSeq) return;
                _currentPreview = null;
                _previewValue.Text = "—";
                SetStatus("Preview failed: " + ex.Message, true);
            }
            UpdateReleaseEnabled();
        }

        private void UpdateReleaseEnabled()
        {
            _releaseBtn.Enabled =
                !string.IsNullOrEmpty(_currentPreview) &&
                !string.IsNullOrEmpty(SelVal(_aggregateCombo)) &&
                !string.IsNullOrEmpty(SelVal(_subAggregateCombo)) &&
                !string.IsNullOrEmpty(SelVal(_sourceCombo)) &&
                !string.IsNullOrWhiteSpace(_descriptionBox.Text);
        }

        private async Task OnReleaseAsync()
        {
            var entry = new CreateBatchEntryDto
            {
                detected_part_number = null,
                project_identifier = ProjectIdentifier,
                vehicle_category = SelVal(_vehicleCombo),
                model = SelVal(_modelCombo),
                major_group = SelVal(_majorCombo),
                minor_group = SelVal(_minorCombo),
                release_type = ReleaseType,
                description = _descriptionBox.Text.Trim(),
                aggregate = SelVal(_aggregateCombo),
                sub_aggregate = SelVal(_subAggregateCombo),
                source = SelVal(_sourceCombo),
                available_for_service = string.Equals(SelVal(_availableCombo), "true", StringComparison.OrdinalIgnoreCase),
            };

            SetBusy(true, "Releasing on Atlas…");
            try
            {
                var result = await _api.CreateBatchAsync(new List<CreateBatchEntryDto> { entry });
                var created = result?.created != null && result.created.Count > 0 ? result.created[0] : null;
                if (created == null || string.IsNullOrEmpty(created.new_part_number))
                {
                    SetStatus("Atlas didn't return a part number — check atlas-api logs.", true);
                    return;
                }
                MintedPartNumber = created.new_part_number;
                try { Clipboard.SetText(MintedPartNumber); } catch { /* clipboard best-effort */ }
                MessageBox.Show(
                    $"Released part code:\n\n    {MintedPartNumber}\n\n" +
                    "(copied to clipboard)\n\nIt lands as PROTO / pending-preparation, " +
                    "the same as a release from atlas-ui.",
                    "Atlas — Part Code Released",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                SetStatus("Release failed: " + ex.Message, true);
            }
            finally { SetBusy(false, null); }
        }

        // ── small helpers ─────────────────────────────────────────────────────

        private static string SelVal(ComboBox c) => (c.SelectedItem as NamedOption)?.Value;

        private void ClearCombo(ComboBox c)
        {
            bool prev = _suppress; _suppress = true;
            c.SelectedIndex = -1;
            c.Items.Clear();
            _suppress = prev;
        }

        private void ClearSelection(ComboBox c)
        {
            bool prev = _suppress; _suppress = true;
            c.SelectedIndex = -1;
            _suppress = prev;
        }

        private void SetStatus(string msg, bool error)
        {
            _statusLabel.ForeColor = error ? Color.Firebrick : Color.DimGray;
            _statusLabel.Text = msg ?? "";
        }

        private void SetBusy(bool busy, string status)
        {
            _releaseBtn.Enabled = !busy && !string.IsNullOrEmpty(_currentPreview);
            _cancelBtn.Enabled = !busy;
            if (status != null) SetStatus(status, false);
            if (!busy) UpdateReleaseEnabled();
        }
    }
}
