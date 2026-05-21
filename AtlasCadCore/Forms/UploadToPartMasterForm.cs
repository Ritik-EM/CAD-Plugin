using System;
using System.Collections.Generic;
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
    /// First-time upload orchestrator. Replaces the deleted "Upload Assembly"
    /// flow. Sequence:
    ///   1. Walk active assembly tree + export STEP per native file.
    ///   2. POST /cad/part-master/upload — backend attaches files to existing
    ///      part_master revisions, returns missing_parts.
    ///   3. If missing_parts: show AssignPartMetadataForm → /create-batch →
    ///      remap detected→new_part_number → repeat /upload with the renamed
    ///      subset of the tree so the new parts get their files attached.
    ///   4. Show summary.
    ///
    /// Despite the "Form" name in the filename, this is a static orchestrator
    /// — the only WinForms it owns are ProgressForm + AssignPartMetadataForm,
    /// invoked inline. Kept as a class (not free function) so the API surface
    /// is namespaced + testable.
    /// </summary>
    public static class UploadToPartMasterForm
    {
        public static async Task RunAsync(AtlasApiClient api, ICadAdapter adapter)
        {
            CadDocument doc = adapter.GetActiveDocument();
            if (doc == null || !doc.IsAssembly)
            {
                MessageBox.Show("Open an assembly first.", "Atlas — Upload",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var progress = new ProgressForm("Atlas — Upload to Part Master"))
            {
                progress.Show();
                string stepDir = null;
                try
                {
                    adapter.SaveDocument(doc);

                    progress.SetPhase("Walking assembly tree…");
                    var native = adapter.WalkAssembly(doc) ?? new List<AssemblyFileRef>();
                    EnsurePlaceholderPartNumbers(native);

                    // Export a STEP per native file. Adapter reuses the
                    // IModelDoc2 captured during WalkAssembly so OpenDoc6 is
                    // skipped — the main cost is then just SaveAs per child.
                    stepDir = Path.Combine(Path.GetTempPath(), "AtlasCadStep_" + Guid.NewGuid().ToString("N"));
                    progress.SetPhase("Exporting STEP files…", 0, native.Count);
                    var steps = adapter.ExportStep(
                        doc, native, stepDir,
                        progress: (cur, total, filename) =>
                            progress.SetPhase($"Exporting STEP {cur}/{total}: {filename}", cur, total)
                    ) ?? new List<AssemblyFileRef>();

                    progress.SetPhase("Hashing files…", 0, native.Count + steps.Count);
                    int hashed = 0;
                    var all = native.Concat(steps).ToList();
                    System.Threading.Tasks.Parallel.ForEach(all, f =>
                    {
                        f.Sha256 = FileHashing.Sha256Hex(f.FullPath);
                        int n = System.Threading.Interlocked.Increment(ref hashed);
                        progress.SetPhase($"Hashing files… {n}/{all.Count}", n, all.Count);
                    });

                    // Build one tree entry per part_number: pair native + step.
                    var byPart = BuildPerPartEntries(native, steps);

                    progress.SetPhase($"Uploading {all.Count} files…");
                    var firstPass = await api.UploadPartMasterAsync(
                        tree: byPart.Select(e => (object)e.ToUploadJson()),
                        filePaths: byPart.SelectMany(e => e.AllPaths()));

                    int attachedFirst = firstPass.attached?.Count ?? 0;
                    var missing = firstPass.missing_parts ?? new List<MissingPartDto>();
                    int createdSecond = 0;

                    if (missing.Count > 0)
                    {
                        progress.Hide();
                        List<CreateBatchEntryDto> userEntries;
                        using (var dlg = new AssignPartMetadataForm(missing))
                        {
                            if (dlg.ShowDialog() != DialogResult.OK) return;
                            userEntries = dlg.Result;
                        }
                        progress.Show();
                        progress.SetPhase($"Creating {userEntries.Count} new part_master entries…");
                        var created = await api.CreateBatchAsync(userEntries);
                        var remap = (created?.created ?? new List<CreatedEntryDto>())
                            .Where(c => !string.IsNullOrEmpty(c.detected_part_number))
                            .ToDictionary(c => c.detected_part_number, c => c.new_part_number);

                        // Build the subset tree for the just-created parts with
                        // their newly-minted part_numbers, then re-upload.
                        var subset = byPart
                            .Where(e => remap.ContainsKey(e.PartNumber))
                            .Select(e => e.WithPartNumber(remap[e.PartNumber]))
                            .ToList();
                        progress.SetPhase($"Uploading {subset.Sum(e => e.AllPaths().Count())} files for new parts…");
                        var secondPass = await api.UploadPartMasterAsync(
                            tree: subset.Select(e => (object)e.ToUploadJson()),
                            filePaths: subset.SelectMany(e => e.AllPaths()));
                        createdSecond = secondPass.attached?.Count ?? 0;
                    }

                    progress.Done();
                    MessageBox.Show(
                        $"Upload complete.\n\n" +
                        $"Attached to existing part_master entries: {attachedFirst}\n" +
                        $"New entries created + attached: {createdSecond}\n" +
                        (missing.Count > createdSecond ? $"Skipped (cancelled metadata): {missing.Count - createdSecond}\n" : ""),
                        "Atlas — Upload to Part Master",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                finally
                {
                    if (progress.Visible) progress.Close();
                    try { if (stepDir != null) Directory.Delete(stepDir, recursive: true); } catch { }
                }
            }
        }

        private static void EnsurePlaceholderPartNumbers(List<AssemblyFileRef> entries)
        {
            // For files whose filename doesn't match the 10-char part_number
            // pattern, fall back to the bare filename (uppercased) as the
            // "detected" part_number. The backend will report these in
            // missing_parts and the user will fill metadata to mint real ones.
            foreach (var e in entries)
            {
                if (!string.IsNullOrEmpty(e.PartNumber)) continue;
                e.PartNumber = Path.GetFileNameWithoutExtension(e.Filename ?? "").ToUpperInvariant();
            }
        }

        private static List<PartEntry> BuildPerPartEntries(
            List<AssemblyFileRef> native, List<AssemblyFileRef> steps)
        {
            var stepByPart = steps
                .Where(s => !string.IsNullOrEmpty(s.PartNumber))
                .GroupBy(s => s.PartNumber)
                .ToDictionary(g => g.Key, g => g.First());

            var result = new List<PartEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in native)
            {
                if (string.IsNullOrEmpty(n.PartNumber) || !seen.Add(n.PartNumber)) continue;
                AssemblyFileRef step = stepByPart.TryGetValue(n.PartNumber, out var s) ? s : null;
                result.Add(new PartEntry
                {
                    PartNumber = n.PartNumber,
                    NativeFilename = n.Filename,
                    NativePath = n.FullPath,
                    StepFilename = step?.Filename,
                    StepPath = step?.FullPath,
                    DetectedDescription = null,
                });
            }
            return result;
        }

        private class PartEntry
        {
            public string PartNumber;
            public string NativeFilename;
            public string NativePath;
            public string StepFilename;
            public string StepPath;
            public string DetectedDescription;

            public object ToUploadJson() => new
            {
                part_number = PartNumber,
                filename = NativeFilename,
                step_filename = StepFilename,
                detected_description = DetectedDescription,
            };

            public IEnumerable<string> AllPaths()
            {
                if (!string.IsNullOrEmpty(NativePath)) yield return NativePath;
                if (!string.IsNullOrEmpty(StepPath)) yield return StepPath;
            }

            public PartEntry WithPartNumber(string newPn) => new PartEntry
            {
                PartNumber = newPn,
                NativeFilename = NativeFilename,
                NativePath = NativePath,
                StepFilename = StepFilename,
                StepPath = StepPath,
                DetectedDescription = DetectedDescription,
            };
        }
    }
}
