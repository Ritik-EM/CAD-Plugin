using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AtlasCadCore.Adapter;
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

        /// <summary>
        /// If the contributed file is an assembly AND the user has that file
        /// open in CAD, walks the tree and writes a manifest JSON to temp.
        /// Returns the temp file path on success, null otherwise. Caller
        /// includes the file in the upload batch (P7.50).
        /// </summary>
        private static string TryBuildTreeJsonForActiveAssembly(
            ICadAdapter adapter, string nativeFilePath, string partNumber)
        {
            if (adapter == null) return null;
            string ext = Path.GetExtension(nativeFilePath).ToLowerInvariant();
            if (ext != ".catproduct" && ext != ".sldasm") return null;

            var active = adapter.GetActiveDocument();
            if (active == null || !active.IsAssembly) return null;
            if (!string.Equals(active.FullPath, nativeFilePath, StringComparison.OrdinalIgnoreCase))
                return null;

            var native = adapter.WalkAssembly(active) ?? new List<AssemblyFileRef>();
            var root = native.FirstOrDefault(n => n.IsRoot);
            string rootPn = root?.PartNumber ?? partNumber;

            var byParent = new Dictionary<string, List<AssemblyFileRef>>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in native)
            {
                if (string.IsNullOrEmpty(n.ParentPartNumber)) continue;
                if (!byParent.TryGetValue(n.ParentPartNumber, out var list))
                    byParent[n.ParentPartNumber] = list = new List<AssemblyFileRef>();
                list.Add(n);
            }

            var nodes = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootPn };
            void Walk(string pn)
            {
                if (!byParent.TryGetValue(pn, out var kids)) return;
                foreach (var k in kids)
                {
                    if (string.IsNullOrEmpty(k.PartNumber) || !seen.Add(k.PartNumber)) continue;
                    nodes.Add(new
                    {
                        part_number = k.PartNumber,
                        filename = k.Filename,
                        parent_part_number = k.ParentPartNumber,
                    });
                    Walk(k.PartNumber);
                }
            }
            Walk(rootPn);
            if (nodes.Count == 0) return null;

            var manifest = new
            {
                version = 1,
                root_part_number = partNumber,
                root_filename = Path.GetFileName(nativeFilePath),
                nodes = nodes,
            };

            string stageDir = Path.Combine(Path.GetTempPath(), "AtlasCad", "contribute_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stageDir);
            string treePath = Path.Combine(stageDir, partNumber + ".tree.json");
            File.WriteAllText(treePath,
                Newtonsoft.Json.JsonConvert.SerializeObject(manifest, Newtonsoft.Json.Formatting.Indented));
            return treePath;
        }

        public static async Task RunAsync(AtlasApiClient api, string partNumber, string nativeFilePath, string sourceLabel,
                                          ICadAdapter adapter = null)
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

            string treeFilename = null;
            string treePath = null;
            var filesToUpload = new List<string> { nativeFilePath };
            try
            {
                treePath = TryBuildTreeJsonForActiveAssembly(adapter, nativeFilePath, partNumber);
                if (!string.IsNullOrEmpty(treePath))
                {
                    treeFilename = Path.GetFileName(treePath);
                    filesToUpload.Add(treePath);
                }
            }
            catch { /* non-fatal: contribute still works without manifest */ }

            try
            {
                var tree = new List<object>
                {
                    new
                    {
                        part_number = partNumber,
                        filename = filename,
                        step_filename = (string)null,
                        tree_filename = treeFilename,
                        detected_description = comment,
                    }
                };
                var resp = await api.UploadPartMasterAsync(tree, filesToUpload);
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
