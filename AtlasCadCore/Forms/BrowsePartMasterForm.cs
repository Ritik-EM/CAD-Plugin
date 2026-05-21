using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AtlasCadCore.Adapter;
using AtlasCadCore.ApiClient;
using AtlasCadCore.Utility;

namespace AtlasCadCore.Forms
{
    /// <summary>
    /// Replaces BrowseAtlasForm. Browses part_master_library directly: filter
    /// by release_type, free-text search, paginate. The selected row's full
    /// revision history is shown in the detail panel; user picks an action
    /// (Open in CAD / Insert into active assembly / Check Out).
    /// </summary>
    public class BrowsePartMasterForm : Form
    {
        private readonly AtlasApiClient _api;
        private readonly ICadAdapter _adapter;

        private ComboBox _releaseTypeCombo;
        private TextBox _searchBox;
        private Button _refreshBtn;
        private DataGridView _grid;
        private TextBox _detailBox;
        private Button _openBtn;
        private Button _insertBtn;
        private Button _checkoutBtn;
        private Button _cancelCheckoutBtn;
        private Button _contributeBtn;
        private ToolTip _checkoutTip;
        // Set once a STP-only part has been opened in this session, so we
        // only nag the user with the "imported geometry, no design intent"
        // info once per dialog instance.
        private bool _stpInfoShown;
        private Label _statusLabel;
        private Button _prevPageBtn;
        private Button _nextPageBtn;
        private Label _pageLabel;

        private System.Windows.Forms.Timer _searchDebounce;
        private int _page = 1;
        private int _totalPages = 1;
        private const int PageSize = 50;
        private List<PartMasterDocumentDto> _currentItems = new List<PartMasterDocumentDto>();
        private PartMasterDocumentDto _selected;

        public BrowsePartMasterForm(AtlasApiClient api, ICadAdapter adapter)
        {
            _api = api;
            _adapter = adapter;
            BuildUi();
            _ = ReloadAsync();
        }

        private void BuildUi()
        {
            Text = "Atlas — Browse Part Master Library";
            Size = new Size(1100, 650);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(900, 500);

            // Top bar
            var topPanel = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8) };
            topPanel.Controls.Add(new Label { Text = "Release type:", AutoSize = true, Location = new Point(8, 14) });
            _releaseTypeCombo = new ComboBox
            {
                Location = new Point(96, 10),
                Width = 140,
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            _releaseTypeCombo.Items.AddRange(new object[] { "All", "PROTO", "PRODUCTION", "ALTERNATE_PART" });
            _releaseTypeCombo.SelectedIndex = 0;
            _releaseTypeCombo.SelectedIndexChanged += (s, e) => { _page = 1; _ = ReloadAsync(); };
            topPanel.Controls.Add(_releaseTypeCombo);

            topPanel.Controls.Add(new Label { Text = "Search:", AutoSize = true, Location = new Point(252, 14) });
            _searchBox = new TextBox { Location = new Point(308, 10), Width = 360 };
            _searchBox.TextChanged += (s, e) =>
            {
                _searchDebounce?.Stop();
                _searchDebounce?.Start();
            };
            topPanel.Controls.Add(_searchBox);

            _searchDebounce = new System.Windows.Forms.Timer { Interval = 350 };
            _searchDebounce.Tick += (s, e) =>
            {
                _searchDebounce.Stop();
                _page = 1;
                _ = ReloadAsync();
            };

            _refreshBtn = new Button { Text = "Refresh", Location = new Point(682, 8), Width = 80 };
            _refreshBtn.Click += (s, e) => _ = ReloadAsync();
            topPanel.Controls.Add(_refreshBtn);

            Controls.Add(topPanel);

            // Split: grid on the left, details + actions on the right.
            // SplitterDistance / Panel2MinSize must be set AFTER the container
            // has a real Width — setting them on construction throws because
            // the default Width is 150 and 340 > 150 - Panel1MinSize.
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                FixedPanel = FixedPanel.Panel2,
            };
            split.HandleCreated += (s, e) =>
            {
                try
                {
                    int target = Math.Max(200, split.Width - 380);
                    split.Panel2MinSize = Math.Min(340, Math.Max(120, split.Width / 3));
                    split.SplitterDistance = target;
                }
                catch { /* layout race — best effort */ }
            };

            // ---- Grid ----
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoGenerateColumns = false,
                RowHeadersVisible = false,
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Part No.", Name = "part_number", Width = 120 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Description", Name = "description", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Major/Minor", Name = "groups", Width = 100 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Latest Rev", Name = "latest_rev", Width = 110 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Formats", Name = "formats", Width = 130 });
            _grid.SelectionChanged += (s, e) => OnSelectionChanged();

            // Pager — docked bottom so it stays put when the panel resizes.
            var pagerPanel = new Panel { Dock = DockStyle.Bottom, Height = 36 };
            _prevPageBtn = new Button { Text = "‹ Prev", Location = new Point(8, 4), Width = 70 };
            _prevPageBtn.Click += async (s, e) => { if (_page > 1) { _page--; await ReloadAsync(); } };
            pagerPanel.Controls.Add(_prevPageBtn);

            _pageLabel = new Label { Location = new Point(86, 8), AutoSize = true, Text = "Page 1 / 1" };
            pagerPanel.Controls.Add(_pageLabel);

            _nextPageBtn = new Button { Text = "Next ›", Location = new Point(186, 4), Width = 70 };
            _nextPageBtn.Click += async (s, e) => { if (_page < _totalPages) { _page++; await ReloadAsync(); } };
            pagerPanel.Controls.Add(_nextPageBtn);

            // WinForms docks higher-Z-order siblings FIRST. Reliable idiom:
            // add the Fill child LAST so it lays out around already-docked
            // edges. Equivalently — add docked edges first.
            _grid.Dock = DockStyle.Fill;
            split.Panel1.Controls.Add(pagerPanel);
            split.Panel1.Controls.Add(_grid);

            // ---- Detail panel — heading on top, scrollable text in the
            // middle, action buttons pinned to the bottom. ----
            var detailRoot = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };

            var detailLabel = new Label
            {
                Text = "Revision history",
                Dock = DockStyle.Top,
                Height = 24,
                Font = new Font(Font, FontStyle.Bold),
            };

            var actionPanel = new Panel { Dock = DockStyle.Bottom, Height = 120 };
            _openBtn = new Button { Text = "Open in " + _adapter.CadName, Location = new Point(0, 4), Width = 150, Enabled = false };
            _openBtn.Click += async (s, e) => await OnOpenAsync();
            actionPanel.Controls.Add(_openBtn);
            _insertBtn = new Button { Text = "Insert into Assembly", Location = new Point(156, 4), Width = 150, Enabled = false };
            _insertBtn.Click += async (s, e) => await OnInsertAsync();
            actionPanel.Controls.Add(_insertBtn);
            _checkoutBtn = new Button { Text = "Check Out", Location = new Point(0, 40), Width = 150, Enabled = false };
            _checkoutBtn.Click += async (s, e) => await OnCheckoutAsync();
            actionPanel.Controls.Add(_checkoutBtn);
            _cancelCheckoutBtn = new Button { Text = "Cancel Checkout", Location = new Point(156, 40), Width = 150, Enabled = false };
            _cancelCheckoutBtn.Click += async (s, e) => await OnCancelCheckoutAsync();
            actionPanel.Controls.Add(_cancelCheckoutBtn);
            _contributeBtn = new Button { Text = "Contribute Native File…", Location = new Point(0, 76), Width = 306, Enabled = false };
            _contributeBtn.Click += async (s, e) => await OnContributeNativeAsync();
            actionPanel.Controls.Add(_contributeBtn);
            _checkoutTip = new ToolTip();

            _detailBox = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font(FontFamily.GenericMonospace, 9),
                Dock = DockStyle.Fill,
            };

            // Add docked edges first, then the Fill last so it occupies what's
            // left rather than overlaying the edge children.
            detailRoot.Controls.Add(detailLabel);
            detailRoot.Controls.Add(actionPanel);
            detailRoot.Controls.Add(_detailBox);

            split.Panel2.Controls.Add(detailRoot);

            Controls.Add(split);

            // Status bar
            _statusLabel = new Label { Dock = DockStyle.Bottom, Height = 22, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0), Text = "Ready." };
            Controls.Add(_statusLabel);
        }

        // ---- Data loading ----

        private async Task ReloadAsync()
        {
            try
            {
                SetBusy(true, "Loading…");
                string releaseType = _releaseTypeCombo.SelectedItem as string;
                if (releaseType == "All") releaseType = null;
                string search = string.IsNullOrWhiteSpace(_searchBox.Text) ? null : _searchBox.Text.Trim();

                var page = await _api.ListPartMasterAsync(releaseType, search, _page, PageSize);
                _currentItems = page.items ?? new List<PartMasterDocumentDto>();
                _totalPages = Math.Max(1, page.pages);
                _page = Math.Min(Math.Max(1, page.page), _totalPages);

                _grid.Rows.Clear();
                foreach (var doc in _currentItems)
                {
                    var latest = LatestActiveRevision(doc, releaseType);
                    _grid.Rows.Add(
                        latest?.part_number ?? "(none)",
                        doc.description ?? "",
                        $"{doc.major_group}/{doc.minor_group}",
                        latest?.part_number?.Substring(Math.Max(0, latest.part_number.Length - 2)) ?? "—",
                        FormatBadges(latest));
                }

                _pageLabel.Text = $"Page {_page} / {_totalPages} ({page.total} total)";
                _prevPageBtn.Enabled = _page > 1;
                _nextPageBtn.Enabled = _page < _totalPages;
                _statusLabel.Text = $"Loaded {_currentItems.Count} parts.";
            }
            catch (UnauthorizedException)
            {
                MessageBox.Show("Your session has expired. Please sign in and reopen.", "Atlas",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Close();
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "Error: " + ex.Message;
                MessageBox.Show("Load failed:\n\n" + ex.Message, "Atlas",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        private void OnSelectionChanged()
        {
            int row = _grid.CurrentRow?.Index ?? -1;
            _selected = (row >= 0 && row < _currentItems.Count) ? _currentItems[row] : null;
            if (_selected == null)
            {
                _detailBox.Text = "";
                _openBtn.Enabled = _insertBtn.Enabled = _checkoutBtn.Enabled = _cancelCheckoutBtn.Enabled = false;
                return;
            }

            _detailBox.Text = BuildDetailText(_selected);

            string releaseType = _releaseTypeCombo.SelectedItem as string;
            if (releaseType == "All") releaseType = null;
            var latest = LatestActiveRevision(_selected, releaseType);
            var refs = latest?.EffectiveRefs;
            bool hasFile = CountRefs(refs) > 0;
            bool hasNative = !string.IsNullOrEmpty(refs?.Native3dRaw);
            bool hasActiveAssembly = false;
            try
            {
                var doc = _adapter.GetActiveDocument();
                hasActiveAssembly = doc != null && doc.IsAssembly;
            }
            catch { }

            _openBtn.Enabled = hasFile;
            _insertBtn.Enabled = hasFile && hasActiveAssembly;
            // Check Out is gated on a real native existing — STP-only parts
            // can't be meaningfully edited (no feature tree). User must use
            // Contribute Native File first to upload a real .sldprt / .sldasm.
            _checkoutBtn.Enabled = hasNative;
            _checkoutTip.SetToolTip(_checkoutBtn, hasNative
                ? "Lock the part and download the native file for editing."
                : "Disabled — no native CAD file in this revision. Use “Contribute Native File” to upload a real .sldprt/.sldasm first.");
            _cancelCheckoutBtn.Enabled = hasFile;
            // Contribute is enabled whenever a row is selected — the user
            // picks a local file to upload, regardless of what's currently
            // in the revision.
            _contributeBtn.Enabled = _selected != null;
        }

        // ---- Actions ----

        private async Task OnOpenAsync()
        {
            string pn = SelectedPartNumber();
            if (pn == null) return;
            try
            {
                SetBusy(true, "Downloading…");
                var picked = await PickAndDownloadAsync(pn, prefersNative: true);
                if (picked == null) { Beep("No openable file in this revision."); return; }

                string openPath = picked.Path;
                bool convertedFromStep = false;

                if (!picked.IsNative)
                {
                    // No native file in atlas — convert the STP locally so
                    // the user can at least *view* the geometry in SW. The
                    // result is dumb geometry (no feature tree, no design
                    // intent) and is kept STRICTLY LOCAL — we never upload
                    // STP-derived files back to atlas as fake natives.
                    string outHint = Path.Combine(
                        Path.GetDirectoryName(picked.Path),
                        Path.GetFileNameWithoutExtension(picked.Path)
                            + _adapter.NativeFileExtensions[0]);
                    SetBusy(true, "Converting STP to native (local only)…");
                    openPath = _adapter.ImportStepAsNative(picked.Path, outHint);
                    convertedFromStep = true;
                }

                _adapter.OpenDocument(openPath);
                _statusLabel.Text = $"Opened {Path.GetFileName(openPath)}.";

                if (convertedFromStep && !_stpInfoShown)
                {
                    _stpInfoShown = true;
                    SetBusy(false, null);
                    MessageBox.Show(
                        $"{pn} has only a STP file in atlas.\n\n" +
                        "It has been imported into SolidWorks as dumb geometry — " +
                        "you can view and measure it, but there is no feature " +
                        "tree to edit parametrically.\n\n" +
                        "If you have the real source .sldprt / .sldasm for this " +
                        "part, use the “Contribute Native File” button to upload " +
                        "it. That will unlock Check Out for everyone.",
                        "Atlas — Imported from STP",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex) { ShowError("Open failed", ex); }
            finally { SetBusy(false, null); }
        }

        private async Task OnInsertAsync()
        {
            string pn = SelectedPartNumber();
            if (pn == null) return;
            var active = _adapter.GetActiveDocument();
            if (active == null || !active.IsAssembly)
            {
                Beep("Open an assembly first.");
                return;
            }
            try
            {
                SetBusy(true, "Downloading…");
                string path = await DownloadPreferredAsync(pn, prefersNative: true);
                if (path == null) { Beep("No insertable file in this revision."); return; }
                _adapter.InsertComponent(active, path);
                _statusLabel.Text = $"Inserted {Path.GetFileName(path)}.";
            }
            catch (Exception ex) { ShowError("Insert failed", ex); }
            finally { SetBusy(false, null); }
        }

        private async Task OnCheckoutAsync()
        {
            string pn = SelectedPartNumber();
            if (pn == null) return;

            // Strict mode: Check Out requires a real native file. We refuse
            // STP-only checkouts because anything the user "edits" would be
            // dumb geometry — and Check In would then write that dumb file
            // back into atlas as if it were a real revision, destroying the
            // design intent for everyone. The Contribute Native File flow
            // is the deliberate path for getting a real .sldprt into atlas.
            string releaseType = _releaseTypeCombo.SelectedItem as string;
            if (releaseType == "All") releaseType = null;
            var rev = LatestActiveRevision(_selected, releaseType);
            if (string.IsNullOrEmpty(rev?.EffectiveRefs?.Native3dRaw))
            {
                MessageBox.Show(
                    $"{pn} doesn't have a native CAD file in atlas yet.\n\n" +
                    "Check Out is disabled for STP-only parts — editing would " +
                    "lose design intent for everyone.\n\n" +
                    "If you have the real source .sldprt / .sldasm, click " +
                    "“Contribute Native File” to upload it first. Then Check " +
                    "Out will work normally.",
                    "Atlas — Check Out not available",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            bool lockAcquired = false;
            try
            {
                SetBusy(true, "Acquiring lock…");
                var lockInfo = await _api.CheckoutPartMasterAsync(pn);
                lockAcquired = true;

                SetBusy(true, "Downloading native file…");
                var picked = await PickAndDownloadAsync(pn, prefersNative: true);
                if (picked == null || !picked.IsNative)
                {
                    // Defensive — we already checked native existence above,
                    // but a race (revision updated between selection and click)
                    // could land us here.
                    await _api.CancelCheckoutPartMasterAsync(pn);
                    lockAcquired = false;
                    Beep("Native file disappeared between selection and download. Lock released.");
                    return;
                }

                CheckoutTracker.Track(picked.Path, pn);
                _adapter.OpenDocument(picked.Path);

                // Resolve every child reference from atlas — never use the
                // local file system. Anything atlas can't supply with a
                // native is presented in MissingChildUploadForm for the
                // user to attach a local file.
                SetBusy(true, "Resolving child parts from atlas…");
                var openedDoc = _adapter.GetActiveDocument();
                await ResolveFromAtlasFlow.RunAsync(_api, _adapter, openedDoc, silentIfNothingMissing: true);

                _statusLabel.Text = $"Checked out {pn} (locked by {lockInfo.locked_by}).";
                MessageBox.Show(
                    $"Checked out {pn}.\n\n" +
                    $"Edit the file in {_adapter.CadName}, save, then click " +
                    "Check In on the ribbon.",
                    "Atlas — Check Out",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);

                // Browse dialog has done its job — close it so the user can
                // focus on the SW window.
                this.BeginInvoke(new Action(() => this.Close()));
            }
            catch (Exception ex)
            {
                if (lockAcquired)
                {
                    try { await _api.CancelCheckoutPartMasterAsync(pn); } catch { }
                }
                ShowError("Checkout failed", ex);
            }
            finally { SetBusy(false, null); }
        }

        private async Task OnContributeNativeAsync()
        {
            string pn = SelectedPartNumber();
            if (pn == null) return;

            string filter = string.Join(";", _adapter.NativeFileExtensions
                .Select(e => "*" + e));
            using (var dlg = new OpenFileDialog
            {
                Title = $"Pick the native CAD file to upload for {pn}",
                Filter = $"{_adapter.CadName} native files|{filter}|All files|*.*",
                CheckFileExists = true,
            })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                await ContributeNativeFileForm.RunAsync(
                    _api, pn, dlg.FileName,
                    sourceLabel: "selected from local disk");
            }
            // Refresh so the new file's badge shows immediately.
            await ReloadAsync();
        }

        private async Task OnCancelCheckoutAsync()
        {
            string pn = SelectedPartNumber();
            if (pn == null) return;
            if (MessageBox.Show($"Release the lock on {pn} without saving changes?",
                "Atlas — Cancel Checkout",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                SetBusy(true, "Releasing lock…");
                await _api.CancelCheckoutPartMasterAsync(pn);
                CheckoutTracker.UntrackByPartNumber(pn);
                _statusLabel.Text = $"Released lock on {pn}.";
            }
            catch (Exception ex) { ShowError("Cancel-checkout failed", ex); }
            finally { SetBusy(false, null); }
        }

        // ---- Helpers ----

        private string SelectedPartNumber()
        {
            string releaseType = _releaseTypeCombo.SelectedItem as string;
            if (releaseType == "All") releaseType = null;
            return LatestActiveRevision(_selected, releaseType)?.part_number;
        }

        /// <summary>Download the file that best matches the current CAD's native
        /// format. Falls back to STP if no native file is in the revision's drawing_X fields.
        /// Returns the local path or null if there is nothing to download.</summary>
        private async Task<string> DownloadPreferredAsync(string partNumber, bool prefersNative)
        {
            var picked = await PickAndDownloadAsync(partNumber, prefersNative);
            return picked?.Path;
        }

        /// <summary>
        /// Same as DownloadPreferredAsync but also returns whether the picked
        /// file is a native CAD file or a STP fallback — callers that need
        /// to differentiate (e.g. for Contribute-Native) use this variant.
        /// </summary>
        private async Task<PickedDownload> PickAndDownloadAsync(string partNumber, bool prefersNative)
        {
            string releaseType = _releaseTypeCombo.SelectedItem as string;
            if (releaseType == "All") releaseType = null;
            var rev = LatestActiveRevision(_selected, releaseType);
            var refs = rev?.EffectiveRefs;
            if (refs == null) return null;

            string pickedKey = null;
            bool isNative = false;
            if (prefersNative && !string.IsNullOrEmpty(refs.Native3dRaw))
            {
                pickedKey = refs.Native3dRaw;
                isNative = true;
            }
            if (pickedKey == null && !string.IsNullOrEmpty(refs.Step3d))
                pickedKey = refs.Step3d;
            if (pickedKey == null) return null;

            string url = await _api.GetS3DownloadUrlAsync(pickedKey);
            string fileName = Path.GetFileName(pickedKey);
            string targetDir = Path.Combine(Path.GetTempPath(), "AtlasCad", partNumber);
            string targetPath = Path.Combine(targetDir, fileName);
            await _api.DownloadFileAsync(url, targetPath);
            return new PickedDownload { Path = targetPath, IsNative = isNative };
        }

        private class PickedDownload
        {
            public string Path;
            public bool IsNative;
        }

        private static PartMasterRevisionDto LatestActiveRevision(PartMasterDocumentDto doc, string preferredReleaseType)
        {
            if (doc?.releases == null) return null;

            if (!string.IsNullOrEmpty(preferredReleaseType) && doc.releases.TryGetValue(preferredReleaseType, out var bucket))
            {
                var active = bucket?.FirstOrDefault(r => r.active == true);
                if (active != null) return active;
                // Some legacy docs have only one revision with no explicit
                // active flag — fall back to the last entry in that bucket.
                if (bucket != null && bucket.Count > 0) return bucket[bucket.Count - 1];
            }
            // No release_type filter (or empty preferred bucket) — walk all buckets in priority order
            foreach (var rt in new[] { "PRODUCTION", "PROTO", "ALTERNATE_PART" })
            {
                if (doc.releases.TryGetValue(rt, out var b))
                {
                    var a = b?.FirstOrDefault(r => r.active == true);
                    if (a != null) return a;
                    if (b != null && b.Count > 0) return b[b.Count - 1];
                }
            }
            return null;
        }

        private static string FormatBadges(PartMasterRevisionDto rev)
        {
            var refs = rev?.EffectiveRefs;
            if (refs == null) return "";
            var tags = new List<string>();
            // Tag the native CAD format by the extension on the 3d_raw key.
            if (!string.IsNullOrEmpty(refs.Native3dRaw))
            {
                string ext = Path.GetExtension(refs.Native3dRaw).ToLowerInvariant();
                if (ext == ".sldprt" || ext == ".sldasm") tags.Add("SW");
                else if (ext == ".catpart" || ext == ".catproduct") tags.Add("CATIA");
                else if (ext == ".prt") tags.Add("NX");
                else tags.Add("RAW");
            }
            if (!string.IsNullOrEmpty(refs.Step3d)) tags.Add("STP");
            if (!string.IsNullOrEmpty(refs.Drawing2d)) tags.Add("PDF");
            return string.Join(" ", tags);
        }

        private static string BuildDetailText(PartMasterDocumentDto doc)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Project   : {doc.project_identifier}");
            sb.AppendLine($"Group     : {doc.major_group}/{doc.minor_group}");
            sb.AppendLine($"Model     : {doc.model ?? "—"}");
            sb.AppendLine($"Created   : {doc.created_at} by {doc.created_by ?? "—"}");
            sb.AppendLine();
            sb.AppendLine("Revisions:");
            foreach (var kv in doc.releases ?? new Dictionary<string, List<PartMasterRevisionDto>>())
            {
                sb.AppendLine($"  [{kv.Key}]");
                foreach (var r in kv.Value ?? new List<PartMasterRevisionDto>())
                {
                    string activeMark = r.active == true ? "* " : "  ";
                    int fileCount = CountRefs(r.EffectiveRefs);
                    sb.AppendLine($"    {activeMark}{r.part_number}  ({fileCount} file{(fileCount == 1 ? "" : "s")})  by {r.created_by}");
                }
            }
            return sb.ToString();
        }

        private static int CountRefs(ReferenceDocumentsDto r)
        {
            if (r == null) return 0;
            int n = 0;
            if (!string.IsNullOrEmpty(r.Drawing2d)) n++;
            if (!string.IsNullOrEmpty(r.Step3d)) n++;
            if (!string.IsNullOrEmpty(r.Native3dRaw)) n++;
            return n;
        }

        private void SetBusy(bool busy, string text)
        {
            UseWaitCursor = busy;
            if (text != null) _statusLabel.Text = text;
            _refreshBtn.Enabled = !busy;
            _releaseTypeCombo.Enabled = !busy;
            _searchBox.Enabled = !busy;
        }

        private void Beep(string text)
        {
            _statusLabel.Text = text;
            System.Media.SystemSounds.Beep.Play();
        }

        private void ShowError(string title, Exception ex)
        {
            _statusLabel.Text = title + ": " + ex.Message;
            MessageBox.Show(ex.Message, "Atlas — " + title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
