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
    /// First-time upload orchestrator. Plugin is attach-only — releasing
    /// new part_numbers happens on atlas-ui, never from the plugin. Sequence:
    ///   1. Walk active assembly tree + export STEP per native file.
    ///   2. POST /cad/part-master/upload — backend attaches files to existing
    ///      part_master revisions, returns missing_parts for unknowns.
    ///   3. For each missing part: show MissingPartChoiceForm offering
    ///      "Pick Existing" (attach the file to a different existing
    ///      part_number) or "Skip".
    ///   4. Summary lists every skipped / unreleased part_number so the user
    ///      knows what to release on atlas-ui before re-running Upload.
    ///
    /// Despite the "Form" name in the filename, this is a static orchestrator
    /// — the only WinForms it owns are ProgressForm + MissingPartChoiceForm,
    /// invoked inline. Kept as a class (not free function) so the API surface
    /// is namespaced + testable.
    /// </summary>
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

                    // Both assemblies and single parts go through this flow.
                    // For an assembly we walk its tree; for a single .sldprt
                    // we synthesise a one-entry list of just the open doc so
                    // the user can push a brand-new part to atlas without
                    // needing to wrap it in an assembly first.
                    List<AssemblyFileRef> native;
                    if (doc.IsAssembly)
                    {
                        progress.SetPhase("Walking assembly tree…");
                        native = adapter.WalkAssembly(doc) ?? new List<AssemblyFileRef>();
                        // Drop suppressed / missing-file / no-path entries —
                        // they're surfaced by WalkAssembly so Check In can
                        // warn about them, but Upload silently skips them
                        // to preserve the existing behaviour of "upload what
                        // we have, ignore the unfit instances".
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

                    // Pre-pass: for entries whose filename code isn't an
                    // exact 10-char match, try resolving against atlas with
                    // code+"00" (the convention atlas uses for the first
                    // PRODUCTION revision of a part). If we find a match,
                    // rewrite the entry to use the canonical part_number so
                    // the upload attaches to the existing entry instead of
                    // landing in missing_parts.
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
                    // Track parts the user neither attached-to-existing nor
                    // skipped — i.e. the ones that need to be released on
                    // atlas-ui before re-running Upload. We list them in the
                    // final summary so the user has an actionable next step.
                    var unreleasedAfterPicks = new List<MissingPartDto>();

                    if (stillMissing.Count > 0)
                    {
                        // Per missing part: ask Pick Existing / Skip. Creating
                        // brand-new part_numbers is intentionally NOT offered
                        // here — atlas-ui is the single source of truth for
                        // releasing new part_numbers (metadata + reviewer
                        // workflow). The plugin is attach-only.
                        progress.Hide();
                        var pickedExistingRemap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var m in stillMissing)
                        {
                            using (var dlg = new MissingPartChoiceForm(m.part_number, m.filename, api))
                            {
                                dlg.ShowDialog();
                                if (dlg.Choice == MissingPartChoiceForm.ChoiceKind.UseExisting
                                    && !string.IsNullOrEmpty(dlg.PickedExistingPartNumber))
                                {
                                    pickedExistingRemap[m.part_number] = dlg.PickedExistingPartNumber;
                                }
                                else
                                {
                                    // Skip OR closed-without-picking: this
                                    // part_number needs to be released on
                                    // atlas-ui before it can be uploaded.
                                    unreleasedAfterPicks.Add(m);
                                    skipped++;
                                }
                            }
                        }
                        progress.Show();

                        // Attach to existing part_numbers the user picked.
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
            // For files whose filename doesn't match the strict 10-char
            // part_number pattern, fall back through two stages:
            //   1. extract the leading alphanumeric code (e.g. "EL530012"
            //      from "EL530012_HEXAGON WELD NUT...sldprt") — this gives
            //      the ResolveAgainstAtlasAsync pre-pass a clean stem to
            //      pad with "00" and look up in atlas
            //   2. if even that fails, last-resort to the bare filename
            //      uppercased so the chooser dialog has *something* to show
            foreach (var e in entries)
            {
                if (!string.IsNullOrEmpty(e.PartNumber)) continue;
                e.PartNumber = AtlasCadCore.Utility.PartNumberParser
                                   .ExtractLeadingCode(e.Filename)
                    ?? Path.GetFileNameWithoutExtension(e.Filename ?? "")
                           .ToUpperInvariant();
            }
        }

        /// <summary>
        /// Pre-pass before /upload: for each entry whose part_number isn't a
        /// strict 10-char code, try resolving it against atlas with
        /// code+"00" exact match (atlas's first PRODUCTION revision
        /// convention). When a match is found, rewrite the entry to use the
        /// canonical atlas part_number so the upload attaches to the
        /// existing entry instead of landing in missing_parts.
        /// </summary>
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

        /// <summary>
        /// Search atlas for an exact part_number match. Returns the canonical
        /// part_number if a doc has a revision (any release_type) whose
        /// part_number equals `candidate`, otherwise null.
        /// </summary>
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
