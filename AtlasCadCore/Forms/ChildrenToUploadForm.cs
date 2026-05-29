using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace AtlasCadCore.Forms
{
    /// <summary>
    /// Informational, manifest-driven report shown after an assembly is
    /// opened/checked out. Lists every child part the assembly's tree.json
    /// references but that has no native file in Atlas — i.e. the parts the
    /// user still has to upload (via "Upload to Part Master") so that future
    /// checkouts are complete. Driven by the manifest rather than the CAD
    /// app's broken-reference detection, which is unreliable in cache /
    /// visualization mode.
    /// </summary>
    public class ChildrenToUploadForm : Form
    {
        public ChildrenToUploadForm(IList<TreeManifestPreflight.NeedsUpload> missing)
        {
            missing = missing ?? new List<TreeManifestPreflight.NeedsUpload>();

            Text = "Atlas — Parts Still To Upload";
            Size = new Size(760, 460);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(560, 320);

            var hdr = new Label
            {
                Dock = DockStyle.Top,
                Height = 64,
                Padding = new Padding(12, 10, 12, 0),
                Text =
                    $"This assembly references {missing.Count} part(s) that aren't in Atlas yet, " +
                    "so they couldn't be downloaded and will show as missing in CATIA.\r\n" +
                    "Open the assembly that has these files and use \"Upload to Part Master\" to " +
                    "upload them — then checkout will be complete for everyone.",
            };

            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                AutoGenerateColumns = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = 28,
            };
            grid.RowTemplate.Height = 26;
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Part Code", Name = "pn", Width = 170 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Filename", Name = "filename", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", Name = "status", Width = 190 });
            foreach (var m in missing)
            {
                grid.Rows.Add(
                    m.PartNumber ?? "(unknown)",
                    m.Filename ?? "",
                    m.InAtlasButNoNative ? "in Atlas, no native file" : "not in Atlas");
            }

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(12, 8, 12, 8) };
            var okBtn = new Button
            {
                Text = "OK",
                Size = new Size(100, 30),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                DialogResult = DialogResult.OK,
            };
            var copyBtn = new Button
            {
                Text = "Copy list",
                Size = new Size(100, 30),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
            };
            copyBtn.Click += (s, e) =>
            {
                try
                {
                    string text = string.Join(Environment.NewLine,
                        missing.Select(m => $"{m.PartNumber}\t{m.Filename}\t" +
                            (m.InAtlasButNoNative ? "in Atlas, no native file" : "not in Atlas")));
                    if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
                }
                catch { /* clipboard occasionally unavailable — non-fatal */ }
            };
            bottom.Resize += (s, e) =>
            {
                okBtn.Location = new Point(bottom.Width - okBtn.Width - 12, 8);
                copyBtn.Location = new Point(okBtn.Left - copyBtn.Width - 8, 8);
            };
            bottom.Controls.Add(okBtn);
            bottom.Controls.Add(copyBtn);
            AcceptButton = okBtn;

            Controls.Add(grid);
            Controls.Add(bottom);
            Controls.Add(hdr);
        }

        /// <summary>Shows the report modally if there's anything to report.</summary>
        public static void ShowIfAny(IList<TreeManifestPreflight.NeedsUpload> missing)
        {
            if (missing == null || missing.Count == 0) return;
            using (var f = new ChildrenToUploadForm(missing)) f.ShowDialog();
        }
    }
}
