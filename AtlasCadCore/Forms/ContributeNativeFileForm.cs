using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using AtlasCadCore.ApiClient;

namespace AtlasCadCore.Forms
{
    public class ContributeNativeFileForm : Form
    {
        private TextBox _commentBox;
        public bool Confirmed { get; private set; }
        public string Comment { get; private set; }

        public ContributeNativeFileForm(string partNumber, string nativeFilename, string sourceLabel)
        {
            Text = "Atlas — Contribute Native File";
            Size = new Size(560, 290);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;

            var msg = new Label
            {
                Location = new Point(20, 18),
                Width = 510,
                Height = 130,
                Text =
                    $"Upload this file as the native CAD for part {partNumber}?\n\n" +
                    $"   File:   {nativeFilename}\n" +
                    $"   Source: {sourceLabel}\n\n" +
                    "It will be attached to the active revision's 3d_raw slot. " +
                    "Future Check Outs of this part will download this file " +
                    "for editing instead of treating the part as STP-only.",
            };
            Controls.Add(msg);

            Controls.Add(new Label { Text = "Comment (optional):", Location = new Point(20, 152), AutoSize = true });
            _commentBox = new TextBox
            {
                Location = new Point(20, 172),
                Width = 510,
                MaxLength = 500,
            };
            Controls.Add(_commentBox);

            var upload = new Button
            {
                Text = "Upload as Native",
                Location = new Point(280, 210), Width = 130, DialogResult = DialogResult.OK,
            };
            upload.Click += (s, e) => { Confirmed = true; Comment = _commentBox.Text ?? ""; };
            Controls.Add(upload);

            var skip = new Button
            {
                Text = "Skip", Location = new Point(420, 210), Width = 110, DialogResult = DialogResult.Cancel,
            };
            Controls.Add(skip);

            AcceptButton = upload;
            CancelButton = skip;
        }

        public static async Task RunAsync(AtlasApiClient api, string partNumber, string nativeFilePath, string sourceLabel)
        {
            if (string.IsNullOrEmpty(partNumber) || string.IsNullOrEmpty(nativeFilePath) || !File.Exists(nativeFilePath))
                return;

            string filename = Path.GetFileName(nativeFilePath);
            bool ok;
            string comment;
            using (var dlg = new ContributeNativeFileForm(partNumber, filename, sourceLabel))
            {
                var result = dlg.ShowDialog();
                ok = result == DialogResult.OK && dlg.Confirmed;
                comment = dlg.Comment;
            }
            if (!ok) return;

            try
            {
                var tree = new List<object>
                {
                    new
                    {
                        part_number = partNumber,
                        filename = filename,
                        step_filename = (string)null,
                        detected_description = comment,
                    }
                };
                var resp = await api.UploadPartMasterAsync(tree, new[] { nativeFilePath });
                int attached = resp.attached?.Count ?? 0;
                int missing = resp.missing_parts?.Count ?? 0;
                if (attached > 0)
                {
                    MessageBox.Show(
                        $"Native file uploaded and attached to {partNumber}.",
                        "Atlas — Contribute Native File",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else if (missing > 0)
                {
                    MessageBox.Show(
                        $"Atlas no longer recognises {partNumber} — the part may have been deleted.",
                        "Atlas", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                else
                {
                    MessageBox.Show("Upload returned no attachments.", "Atlas",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Contribute failed:\n\n" + ex.Message,
                    "Atlas", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
