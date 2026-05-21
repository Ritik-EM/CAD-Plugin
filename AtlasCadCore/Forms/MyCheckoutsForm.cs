using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AtlasCadCore.ApiClient;
using AtlasCadCore.Utility;

namespace AtlasCadCore.Forms
{
    /// <summary>
    /// Dialog showing every active part_master lock owned by the current
    /// user. Multi-select via checkboxes, then Release Selected (or Release
    /// All) calls /cancel-checkout per part and clears the local
    /// CheckoutTracker entry so the in-progress state on this machine
    /// matches the server.
    /// </summary>
    public class MyCheckoutsForm : Form
    {
        private readonly AtlasApiClient _api;
        private DataGridView _grid;
        private Label _statusLabel;
        private Button _refreshBtn;
        private Button _selectAllBtn;
        private Button _selectNoneBtn;
        private Button _releaseBtn;
        private List<CheckoutResultDto> _items = new List<CheckoutResultDto>();

        public MyCheckoutsForm(AtlasApiClient api)
        {
            _api = api;
            BuildUi();
            _ = ReloadAsync();
        }

        private void BuildUi()
        {
            Text = "Atlas — My Checkouts";
            Size = new Size(740, 460);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(560, 320);

            var top = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8) };
            _refreshBtn = new Button { Text = "Refresh", Location = new Point(6, 8), Width = 90 };
            _refreshBtn.Click += async (s, e) => await ReloadAsync();
            top.Controls.Add(_refreshBtn);
            _selectAllBtn = new Button { Text = "Select All", Location = new Point(104, 8), Width = 90 };
            _selectAllBtn.Click += (s, e) => SetAll(true);
            top.Controls.Add(_selectAllBtn);
            _selectNoneBtn = new Button { Text = "Select None", Location = new Point(202, 8), Width = 100 };
            _selectNoneBtn.Click += (s, e) => SetAll(false);
            top.Controls.Add(_selectNoneBtn);

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                AutoGenerateColumns = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                EditMode = DataGridViewEditMode.EditOnEnter,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = 28,
            };
            _grid.RowTemplate.Height = 26;
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "", Name = "sel", Width = 40 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Part Number", Name = "pn", Width = 140, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Locked At", Name = "lockedAt", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
            _grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8) };
            _statusLabel = new Label { Location = new Point(8, 14), AutoSize = true, Text = "Loading…", ForeColor = Color.DimGray };
            bottom.Controls.Add(_statusLabel);
            _releaseBtn = new Button
            {
                Text = "Release Selected",
                Width = 160, Height = 28,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                Enabled = false,
            };
            _releaseBtn.Location = new Point(bottom.Width - 280, 10);
            _releaseBtn.Click += async (s, e) => await ReleaseSelectedAsync();
            bottom.Controls.Add(_releaseBtn);
            var close = new Button
            {
                Text = "Close",
                Width = 100, Height = 28,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                DialogResult = DialogResult.Cancel,
            };
            close.Location = new Point(bottom.Width - 110, 10);
            bottom.Controls.Add(close);
            CancelButton = close;

            // Fill grid sent to back of z-order — see MissingChildUploadForm
            // note. Without this the grid's top + bottom rows are clipped
            // under the docked toolbar / bottom sibling.
            Controls.Add(top);
            Controls.Add(bottom);
            Controls.Add(_grid);
            _grid.SendToBack();
        }

        private async Task ReloadAsync()
        {
            try
            {
                SetBusy(true, "Loading…");
                var res = await _api.MyCheckoutsAsync();
                _items = res?.checkouts ?? new List<CheckoutResultDto>();

                _grid.Rows.Clear();
                foreach (var c in _items)
                {
                    _grid.Rows.Add(false, c.part_number, c.locked_at ?? "");
                }
                _grid.PerformLayout();
                _grid.Refresh();
                if (_grid.Rows.Count > 0) _grid.CurrentCell = _grid.Rows[0].Cells["pn"];
                _statusLabel.Text = _items.Count == 0
                    ? "No active checkouts."
                    : $"{_items.Count} active checkout(s).";
                _releaseBtn.Enabled = _items.Count > 0;
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "Load failed: " + ex.Message;
                MessageBox.Show("Load failed: " + ex.Message, "Atlas",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        private void SetAll(bool value)
        {
            foreach (DataGridViewRow row in _grid.Rows)
                row.Cells["sel"].Value = value;
        }

        private async Task ReleaseSelectedAsync()
        {
            var selected = new List<string>();
            for (int i = 0; i < _grid.Rows.Count; i++)
            {
                bool ticked = Convert.ToBoolean(_grid.Rows[i].Cells["sel"].Value ?? false);
                if (!ticked) continue;
                if (i < _items.Count) selected.Add(_items[i].part_number);
            }

            if (selected.Count == 0)
            {
                MessageBox.Show("Tick at least one row first.", "Atlas — My Checkouts",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var confirm = MessageBox.Show(
                $"Release locks on {selected.Count} part(s)?\n\n" +
                "Any unsaved edits in your local files will NOT be uploaded — " +
                "this is a Cancel Checkout, not a Check In.",
                "Atlas — My Checkouts",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            int released = 0;
            var failures = new List<string>();
            try
            {
                SetBusy(true, $"Releasing {selected.Count} lock(s)…");
                foreach (var pn in selected)
                {
                    try
                    {
                        await _api.CancelCheckoutPartMasterAsync(pn);
                        CheckoutTracker.UntrackByPartNumber(pn);
                        released++;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{pn}: {ex.Message}");
                    }
                }
            }
            finally
            {
                SetBusy(false, null);
            }

            var summary = new System.Text.StringBuilder();
            summary.AppendLine($"Released {released} of {selected.Count} lock(s).");
            if (failures.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine("Failed:");
                foreach (var f in failures.Take(10)) summary.AppendLine("  " + f);
                if (failures.Count > 10) summary.AppendLine($"  … {failures.Count - 10} more");
            }
            MessageBox.Show(summary.ToString(), "Atlas — My Checkouts",
                MessageBoxButtons.OK,
                failures.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);

            await ReloadAsync();
        }

        private void SetBusy(bool busy, string text)
        {
            UseWaitCursor = busy;
            if (text != null) _statusLabel.Text = text;
            _refreshBtn.Enabled = !busy;
            _selectAllBtn.Enabled = !busy;
            _selectNoneBtn.Enabled = !busy;
            _releaseBtn.Enabled = !busy && _items.Count > 0;
        }
    }
}
