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
    public static class UploadToPartMasterForm
    {
        public static async Task RunAsync(AtlasApiClient api, ICadAdapter adapter)
        {
            CadDocument doc = adapter.GetActiveDocument();
            if (doc == null)
            {
                MessageBox.Show("Open a part or assembly first.", "Atlas — Upload",
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

                    List<AssemblyFileRef> native;
                    if (doc.IsAssembly)
                    {
                        // Pre-flight: if the assembly references children that
                        // aren't on disk, fork into ResolveFromAtlasFlow so the
                        // user can attach them (Browse local OR Pick from Atlas)
                        // before we walk. Otherwise the walk silently drops
                        // them as SkipReason=missing-file.
                        var missing = adapter.FindMissingComponents(doc) ?? new List<MissingComponent>();
                        if (missing.Count > 0)
                        {
                            progress.Hide();
                            var resolveProceed = MessageBox.Show(
                                $"This assembly references {missing.Count} child file(s) that aren't on disk. " +
                                "Resolve them from Atlas first so they're included in the upload?",
                                "Atlas — Missing Children",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                            if (resolveProceed == DialogResult.Yes)
                                await ResolveFromAtlasFlow.RunAsync(api, adapter, doc, silentIfNothingMissing: true);
                            progress.Show();
                        }

                        progress.SetPhase("Walking assembly tree…");
                        native = adapter.WalkAssembly(doc) ?? new List<AssemblyFileRef>();
                        native = native.Where(n => string.IsNullOrEmpty(n.SkipReason)).ToList();
                    }
                    else
                    {
                        progress.SetPhase("Reading active part…");
                        string filename = Path.GetFileName(doc.FullPath ?? "");
                        if (string.IsNullOrEmpty(doc.FullPath) || string.IsNullOrEmpty(filename))
                        {
                            MessageBox.Show(
                                "The active part hasn't been saved to disk yet. " +
                                "Save it (Ctrl+S) and try again.",
                                "Atlas — Upload",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        native = new List<AssemblyFileRef>
                        {
                            new AssemblyFileRef
                            {
                                FullPath = doc.FullPath,
                                Filename = filename,
                                RelativePath = filename,
                                IsRoot = true,
                                PartNumber = AtlasCadCore.Utility.PartNumberParser.ParseOrNull(filename),
                                ParentPartNumber = null,
                                NativeHandle = doc.NativeHandle,
                            }
                        };
                    }
                    EnsurePlaceholderPartNumbers(native);

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

                    var byPart = BuildPerPartEntries(native, steps);

                    // Build a tree.json manifest for every assembly node and
                    // stage it for upload. Each assembly's manifest describes
                    // its full subtree (part_number + filename + parent), so
                    // checkout can pre-download every child file before the
                    // CAD app opens the parent.
                    progress.SetPhase("Building assembly tree manifests…");
                    AttachTreeManifests(byPart, native, stepDir);

                    progress.SetPhase("Resolving part_numbers against atlas…");
                    byPart = await ResolveAgainstAtlasAsync(api, byPart);

                    progress.SetPhase($"Uploading {all.Count} files…");
                    var firstPass = await api.UploadPartMasterAsync(
                        tree: byPart.Select(e => (object)e.ToUploadJson()),
                        filePaths: byPart.SelectMany(e => e.AllPaths()));

                    int attachedFirst = firstPass.attached?.Count ?? 0;
                    var stillMissing = firstPass.missing_parts ?? new List<MissingPartDto>();
                    int attachedFromPickedExisting = 0;
                    int skipped = 0;
                    var unreleasedAfterPicks = new List<MissingPartDto>();

                    if (stillMissing.Count > 0)
                    {
                        progress.Hide();
                        var pickedExistingRemap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        using (var dlg = new MissingPartsTableForm(api, stillMissing))
                        {
                            dlg.ShowDialog();
                            // Either OK (Continue) or Cancel (Skip All) — we honour
                            // each row's PickedPartNumber regardless; rows without a
                            // pick are treated as "skip".
                            foreach (var m in stillMissing)
                            {
                                var row = dlg.Rows.FirstOrDefault(r =>
                                    string.Equals(r.DetectedPartNumber, m.part_number, StringComparison.OrdinalIgnoreCase));
                                if (row != null && !string.IsNullOrEmpty(row.PickedPartNumber))
                                {
                                    pickedExistingRemap[m.part_number] = row.PickedPartNumber;
                                }
                                else
                                {
                                    unreleasedAfterPicks.Add(m);
                                    skipped++;
                                }
                            }
                        }
                        progress.Show();

                        if (pickedExistingRemap.Count > 0)
                        {
                            var subset = byPart
                                .Where(e => pickedExistingRemap.ContainsKey(e.PartNumber))
                                .Select(e => e.WithPartNumber(pickedExistingRemap[e.PartNumber]))
                                .ToList();
                            progress.SetPhase($"Uploading {subset.Count} file(s) against existing part_numbers…");
                            var pass = await api.UploadPartMasterAsync(
                                tree: subset.Select(e => (object)e.ToUploadJson()),
                                filePaths: subset.SelectMany(e => e.AllPaths()));
                            attachedFromPickedExisting = pass?.attached?.Count ?? 0;
                        }
                    }

                    progress.Done();
                    var summaryText = new System.Text.StringBuilder();
                    summaryText.AppendLine("Upload complete.");
                    summaryText.AppendLine();
                    summaryText.AppendLine($"Attached to existing part_master entries (auto): {attachedFirst}");
                    if (attachedFromPickedExisting > 0)
                        summaryText.AppendLine($"Attached to existing part_master entries (you picked): {attachedFromPickedExisting}");
                    if (unreleasedAfterPicks.Count > 0)
                    {
                        summaryText.AppendLine();
                        summaryText.AppendLine($"{unreleasedAfterPicks.Count} part_number(s) aren't released on atlas yet —");
                        summaryText.AppendLine("release them on atlas-ui first, then re-run Upload:");
                        foreach (var m in unreleasedAfterPicks.Take(50))
                            summaryText.AppendLine($"  • {m.part_number}   ({m.filename})");
                        if (unreleasedAfterPicks.Count > 50)
                            summaryText.AppendLine($"  … {unreleasedAfterPicks.Count - 50} more");
                    }
                    else if (skipped > 0)
                    {
                        summaryText.AppendLine($"Skipped: {skipped}");
                    }
                    MessageBox.Show(summaryText.ToString(),
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
            foreach (var e in entries)
            {
                if (!string.IsNullOrEmpty(e.PartNumber)) continue;
                e.PartNumber = AtlasCadCore.Utility.PartNumberParser
                                   .ExtractLeadingCode(e.Filename)
                    ?? Path.GetFileNameWithoutExtension(e.Filename ?? "")
                           .ToUpperInvariant();
            }
        }

        private static async Task<List<PartEntry>> ResolveAgainstAtlasAsync(
            AtlasApiClient api, List<PartEntry> entries)
        {
            if (entries == null || entries.Count == 0) return entries;
            var result = new List<PartEntry>(entries.Count);
            foreach (var e in entries)
            {
                string canonical = null;
                if (!string.IsNullOrEmpty(e.PartNumber) && e.PartNumber.Length < 10)
                {
                    string padded = e.PartNumber + "00";
                    canonical = await TryExactMatchAsync(api, padded);
                }
                if (canonical != null && !string.Equals(canonical, e.PartNumber, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(e.WithPartNumber(canonical));
                }
                else
                {
                    result.Add(e);
                }
            }
            return result;
        }

        private static async Task<string> TryExactMatchAsync(AtlasApiClient api, string candidate)
        {
            try
            {
                var page = await api.ListPartMasterAsync(
                    releaseType: null, search: candidate, page: 1, limit: 50);
                foreach (var d in page?.items ?? new List<PartMasterDocumentDto>())
                {
                    if (d.releases == null) continue;
                    foreach (var bucket in d.releases.Values)
                    {
                        if (bucket == null) continue;
                        foreach (var rev in bucket)
                        {
                            if (rev?.part_number == null) continue;
                            if (string.Equals(rev.part_number, candidate, StringComparison.OrdinalIgnoreCase))
                                return rev.part_number;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// For every entry whose native is an assembly (has at least one
        /// descendant in `native`), serialise the subtree to a tree.json
        /// file in `stageDir` and record the path on the PartEntry. The
        /// upload session ships the file alongside the native/step; backend
        /// classifies *.json into reference_documents.tree.
        /// </summary>
        private static void AttachTreeManifests(
            List<PartEntry> byPart, List<AssemblyFileRef> native, string stageDir)
        {
            if (byPart == null || native == null || native.Count == 0) return;
            Directory.CreateDirectory(stageDir);

            // pn → all descendants (recursive). Built once, queried per entry.
            var childrenByParent = new Dictionary<string, List<AssemblyFileRef>>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in native)
            {
                if (string.IsNullOrEmpty(n.ParentPartNumber)) continue;
                if (!childrenByParent.TryGetValue(n.ParentPartNumber, out var list))
                    childrenByParent[n.ParentPartNumber] = list = new List<AssemblyFileRef>();
                list.Add(n);
            }

            foreach (var entry in byPart)
            {
                if (!childrenByParent.ContainsKey(entry.PartNumber)) continue; // not an assembly

                var nodes = new List<object>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entry.PartNumber };
                void Walk(string pn)
                {
                    if (!childrenByParent.TryGetValue(pn, out var kids)) return;
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
                Walk(entry.PartNumber);
                if (nodes.Count == 0) continue;

                var manifest = new
                {
                    version = 1,
                    root_part_number = entry.PartNumber,
                    root_filename = entry.NativeFilename,
                    nodes = nodes,
                };
                // Filename prefixed with the part_number so it lives at a
                // stable S3 key per part_master, and so the rename-on-bump
                // logic in atlas-api (_upload_classified rename_from) shifts
                // it to the new part_number during release-new-revision.
                string treeFilename = entry.PartNumber + ".tree.json";
                string treePath = Path.Combine(stageDir, treeFilename);
                File.WriteAllText(treePath,
                    Newtonsoft.Json.JsonConvert.SerializeObject(manifest, Newtonsoft.Json.Formatting.Indented));
                entry.TreeFilename = treeFilename;
                entry.TreePath = treePath;
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
            // Set only for assembly entries (P7.49). The plugin generates a
            // manifest describing every descendant and uploads it alongside
            // the native + step files; backend stores its S3 key under
            // reference_documents.tree, plugin reads it on checkout to
            // pre-download children before CATIA/SW opens the assembly.
            public string TreeFilename;
            public string TreePath;
            public string DetectedDescription;

            public object ToUploadJson() => new
            {
                part_number = PartNumber,
                filename = NativeFilename,
                step_filename = StepFilename,
                tree_filename = TreeFilename,
                detected_description = DetectedDescription,
            };

            public IEnumerable<string> AllPaths()
            {
                if (!string.IsNullOrEmpty(NativePath)) yield return NativePath;
                if (!string.IsNullOrEmpty(StepPath)) yield return StepPath;
                if (!string.IsNullOrEmpty(TreePath)) yield return TreePath;
            }

            public PartEntry WithPartNumber(string newPn) => new PartEntry
            {
                PartNumber = newPn,
                NativeFilename = NativeFilename,
                NativePath = NativePath,
                StepFilename = StepFilename,
                StepPath = StepPath,
                TreeFilename = TreeFilename,
                TreePath = TreePath,
                DetectedDescription = DetectedDescription,
            };
        }
    }
}
