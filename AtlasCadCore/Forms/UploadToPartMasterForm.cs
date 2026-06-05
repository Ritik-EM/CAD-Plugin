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
        // Bump when the upload flow changes so the log confirms which build is live.
        public const string UploadFlowVersion = "2026-05-30-perpart-prompt-v1";

        public static async Task RunAsync(AtlasApiClient api, ICadAdapter adapter)
        {
            Log($"RunAsync v={UploadFlowVersion}");
            CadDocument doc = adapter.GetActiveDocument();
            if (doc == null)
            {
                MessageBox.Show("Open a part or assembly first.", "Atlas — Upload",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // P7.51: STEP imports flatten a multi-file CAD assembly into one
            // in-memory tree with no on-disk children, which the rest of the
            // flow has no way to upload as a proper part_master tree. Reject
            // up front with a clear path forward instead of letting the user
            // hit the "N missing children" prompt.
            string activeExt = (System.IO.Path.GetExtension(doc.FullPath ?? "") ?? "").ToLowerInvariant();
            if (activeExt == ".stp" || activeExt == ".step")
            {
                MessageBox.Show(
                    "Atlas only accepts native CAD assemblies — not STEP files.\n\n" +
                    "STEP is a single-file format with no per-component files on disk, " +
                    "so we can't upload it as a multi-part assembly.\n\n" +
                    "What to do:\n" +
                    "  • If you have the original .sldasm / .CATProduct, open that instead.\n" +
                    "  • Or use CATIA's File → Save Management… to save every component as a " +
                    ".CATPart and the root as .CATProduct, then re-open and try again.",
                    "Atlas — Upload",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (var progress = new ProgressForm("Atlas — Upload to Part Master"))
            {
                progress.Show();
                // Paint a marquee "working" indicator immediately — the CATIA
                // calls below (design-mode load, walk) run synchronously on the
                // UI thread, so without an up-front phase the window would sit
                // blank until they finish. SetPhase pumps the message loop so
                // the loader is visible (and animates) through the wait.
                progress.SetPhase("Saving document…");
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
                        progress.SetPhase("Checking assembly references… (loading parts)");
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
                        var walked = adapter.WalkAssembly(doc) ?? new List<AssemblyFileRef>();
                        native = walked.Where(n => string.IsNullOrEmpty(n.SkipReason)).ToList();

                        // Parts that have a real native file on disk but whose
                        // filename didn't yield a valid part_number get a
                        // SkipReason of "no-part-number". Rather than silently
                        // dropping them (which leaves them missing on checkout),
                        // prompt the user to map/create a part_number per part,
                        // or skip it. Whatever they assign is folded back into
                        // the upload set.
                        var needsPartNumber = walked
                            .Where(n => n.SkipReason == "no-part-number"
                                        && !string.IsNullOrEmpty(n.FullPath)
                                        && File.Exists(n.FullPath))
                            .ToList();

                        Log($"walked={walked.Count} validNative={native.Count} needsPartNumber={needsPartNumber.Count}");
                        foreach (var n in walked)
                            Log($"  walked: file='{n.Filename}' pn='{n.PartNumber}' skip='{n.SkipReason ?? "(none)"}' " +
                                $"exists={(!string.IsNullOrEmpty(n.FullPath) && File.Exists(n.FullPath))}");

                        if (needsPartNumber.Count > 0)
                        {
                            progress.Hide();
                            native.AddRange(PromptAssignPartNumbers(api, needsPartNumber));
                            progress.Show();
                        }
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
                    // One STEP per part_number — repeated parts (CATIA _1/_2/_3
                    // copies) share a part_number and only the canonical native
                    // gets a STEP; exporting one per physical copy is wasted CATIA
                    // round-trips. (native still carries every copy for hashing /
                    // companion upload / the tree manifest.)
                    var stepInputs = native
                        .Where(n => !string.IsNullOrEmpty(n.PartNumber))
                        .GroupBy(n => n.PartNumber, StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.First())
                        .ToList();
                    progress.SetPhase("Exporting STEP files…", 0, stepInputs.Count);
                    var steps = adapter.ExportStep(
                        doc, stepInputs, stepDir,
                        progress: (cur, total, filename) =>
                            progress.SetPhase($"Exporting STEP {cur}/{total}: {filename}", cur, total)
                    ) ?? new List<AssemblyFileRef>();

                    progress.SetPhase("Hashing files…", 0, native.Count + steps.Count);
                    int hashed = 0;
                    var all = native.Concat(steps).ToList();
                    System.Threading.Tasks.Parallel.ForEach(all, f =>
                    {
                        // Embedded sub-assemblies have no native file on disk
                        // (they live inside the parent .CATProduct) — nothing to
                        // hash. They still ride along as tree nodes so the
                        // manifest keeps the full hierarchy.
                        if (!string.IsNullOrEmpty(f.FullPath) && File.Exists(f.FullPath))
                            f.Sha256 = FileHashing.Sha256Hex(f.FullPath);
                        int n = System.Threading.Interlocked.Increment(ref hashed);
                        progress.SetPhase($"Hashing files… {n}/{all.Count}", n, all.Count);
                    });

                    var byPart = BuildPerPartEntries(native, steps);

                    // P7.49: emit tree.json for every assembly entry so
                    // checkout can pre-download children. Required for
                    // R2025 broken-ref handling.
                    progress.SetPhase("Building assembly tree manifests…");
                    AttachTreeManifests(byPart, native, stepDir);

                    progress.SetPhase("Resolving part_numbers against atlas…");
                    byPart = await ResolveAgainstAtlasAsync(api, byPart);

                    LogUpload($"========== UPLOAD START: root='{doc.FullPath}' parts={byPart.Count} ==========");
                    foreach (var e in byPart)
                    {
                        LogUpload($"  pn='{e.PartNumber}' native='{e.NativeFilename ?? "-"}' step='{e.StepFilename ?? "-"}' " +
                                  $"tree='{e.TreeFilename ?? "-"}' companions=[{string.Join(", ", e.CompanionFilenames)}]");
                        foreach (var p in e.AllPaths())
                            LogUpload($"      file exists={File.Exists(p)} '{p}'");
                    }

                    progress.SetPhase($"Uploading {all.Count} files…");
                    var firstPass = await api.UploadPartMasterAsync(
                        tree: byPart.Select(e => (object)e.ToUploadJson()),
                        filePaths: byPart.SelectMany(e => e.AllPaths()));
                    LogUploadResult("first-pass", firstPass);

                    int attachedFirst = firstPass.attached?.Count ?? 0;
                    var stillMissing = firstPass.missing_parts ?? new List<MissingPartDto>();
                    int attachedFromPickedExisting = 0;
                    int skipped = 0;
                    var unreleasedAfterPicks = new List<MissingPartDto>();
                    // Parts Atlas refused to overwrite because they already have a
                    // native (3d_raw) — Upload won't clobber it; Check In revises.
                    var alreadyPresent = new List<MissingPartDto>(
                        firstPass.already_present ?? new List<MissingPartDto>());

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
                            LogUploadResult("picked-existing", pass);
                            attachedFromPickedExisting = pass?.attached?.Count ?? 0;
                            if (pass?.already_present != null)
                                alreadyPresent.AddRange(pass.already_present);
                        }
                    }

                    progress.Done();
                    int totalAttached = attachedFirst + attachedFromPickedExisting;
                    var summaryText = new System.Text.StringBuilder();
                    if (totalAttached == 0 && alreadyPresent.Count > 0 &&
                        unreleasedAfterPicks.Count == 0 && skipped == 0)
                    {
                        // Nothing uploaded — every part already has a native.
                        summaryText.AppendLine("Nothing uploaded.");
                        summaryText.AppendLine();
                        summaryText.AppendLine(
                            alreadyPresent.Count == 1
                                ? "This part already has a native (3D file) in Atlas."
                                : $"All {alreadyPresent.Count} part(s) already have a native (3D file) in Atlas.");
                        summaryText.AppendLine("Upload won't overwrite an existing native — use Check In to revise it.");
                        summaryText.AppendLine();
                        foreach (var m in alreadyPresent.Take(50))
                            summaryText.AppendLine($"  • {m.part_number}   ({m.filename})");
                        if (alreadyPresent.Count > 50)
                            summaryText.AppendLine($"  … {alreadyPresent.Count - 50} more");
                        MessageBox.Show(summaryText.ToString(),
                            "Atlas — Nothing to Upload",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    summaryText.AppendLine("Upload complete.");
                    summaryText.AppendLine();
                    summaryText.AppendLine($"Attached to existing part_master entries (auto): {attachedFirst}");
                    if (attachedFromPickedExisting > 0)
                        summaryText.AppendLine($"Attached to existing part_master entries (you picked): {attachedFromPickedExisting}");
                    if (alreadyPresent.Count > 0)
                    {
                        summaryText.AppendLine();
                        summaryText.AppendLine($"Skipped {alreadyPresent.Count} part(s) that already have a native in Atlas");
                        summaryText.AppendLine("(Upload won't overwrite — use Check In to revise):");
                        foreach (var m in alreadyPresent.Take(50))
                            summaryText.AppendLine($"  • {m.part_number}   ({m.filename})");
                        if (alreadyPresent.Count > 50)
                            summaryText.AppendLine($"  … {alreadyPresent.Count - 50} more");
                    }
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

        /// <summary>
        /// Per-part prompt for native files whose filename didn't produce a
        /// valid part_number. Reuses MissingPartsTableForm so the user gets the
        /// same Pick Existing… / Create New… / skip choices used elsewhere.
        /// Returns only the entries the user assigned a part_number to (with
        /// PartNumber set and SkipReason cleared, ready for upload); rows left
        /// blank are treated as "skip" and omitted.
        /// Rows align by index with <paramref name="needsPartNumber"/>.
        /// </summary>
        private static List<AssemblyFileRef> PromptAssignPartNumbers(
            AtlasApiClient api, List<AssemblyFileRef> needsPartNumber)
        {
            var assigned = new List<AssemblyFileRef>();

            var dtos = needsPartNumber.Select(n => new MissingPartDto
            {
                // Seed the picker's search box with the best guess we have so
                // the user isn't typing from scratch.
                part_number = AtlasCadCore.Utility.PartNumberParser.ExtractLeadingCode(n.Filename)
                              ?? Path.GetFileNameWithoutExtension(n.Filename ?? ""),
                filename = n.Filename,
            }).ToList();

            string header =
                $"{needsPartNumber.Count} part(s) don't have a valid Atlas part_number " +
                "(their filename doesn't match the part-number format).\r\n" +
                "For each part: \"Pick Existing…\" to map it to an Atlas part_number, " +
                "\"Create New…\" to mint one, or leave it blank to skip it. " +
                "Skipped parts are NOT uploaded and will be missing on checkout.";

            using (var dlg = new MissingPartsTableForm(api, dtos,
                       headerText: header, title: "Atlas — Assign Part Numbers"))
            {
                dlg.ShowDialog();
                for (int i = 0; i < needsPartNumber.Count && i < dlg.Rows.Count; i++)
                {
                    string picked = dlg.Rows[i].PickedPartNumber;
                    if (string.IsNullOrEmpty(picked)) continue; // skipped by user
                    needsPartNumber[i].PartNumber = picked;
                    needsPartNumber[i].SkipReason = null;
                    assigned.Add(needsPartNumber[i]);
                }
            }
            return assigned;
        }

        // Diagnostics → %AppData%\AtlasCad\walk_assembly.log (same file the
        // adapters write to, so the whole upload story is in one place).
        private static void Log(string line)
        {
            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AtlasCad");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(
                    Path.Combine(logDir, "walk_assembly.log"),
                    $"--- {DateTime.Now:O} UploadToPartMaster.{line}\n");
            }
            catch { /* logging must never break the upload */ }
        }

        // Upload-transaction diagnostics → %AppData%\AtlasCad\upload.log: the
        // exact payload we ship (per part: native / step / tree / companions)
        // and what atlas accepted vs. refused. Pairs with preflight.log on the
        // checkout side for an end-to-end picture.
        private static void LogUpload(string line)
        {
            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AtlasCad");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(
                    Path.Combine(logDir, "upload.log"),
                    $"--- {DateTime.Now:O} {line}\n");
            }
            catch { /* logging must never break the upload */ }
        }

        private static void LogUploadResult(string pass, UploadResultDto r)
        {
            if (r == null) { LogUpload($"=== RESULT ({pass}): null response ==="); return; }
            LogUpload($"=== RESULT ({pass}): attached={r.attached?.Count ?? 0} missing={r.missing_parts?.Count ?? 0} ===");
            foreach (var a in r.attached ?? new List<UploadAttachedDto>())
            {
                var rd = a.reference_documents;
                LogUpload($"  attached pn='{a.part_number}' 3d_raw='{rd?.Native3dRaw ?? "-"}' " +
                          $"3d='{rd?.Step3d ?? "-"}' 2d='{rd?.Drawing2d ?? "-"}' tree='{rd?.TreeJson ?? "-"}'");
            }
            foreach (var m in r.missing_parts ?? new List<MissingPartDto>())
                LogUpload($"  MISSING (not released on atlas) pn='{m.part_number}' file='{m.filename ?? "-"}'");
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

            var childrenByParent = new Dictionary<string, List<AssemblyFileRef>>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in native)
            {
                if (string.IsNullOrEmpty(n.ParentPartNumber)) continue;
                if (!childrenByParent.TryGetValue(n.ParentPartNumber, out var list))
                    childrenByParent[n.ParentPartNumber] = list = new List<AssemblyFileRef>();
                list.Add(n);
            }

            // Every distinct on-disk filename each part_number is referenced
            // under. CATIA can keep N physical copies of one part (paste/insert
            // yields _1/_2/_3 suffixed files); the manifest records them all so
            // checkout can recreate every name the parent assembly links to.
            var filenamesByPart = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in native)
            {
                if (string.IsNullOrEmpty(n.PartNumber) || string.IsNullOrEmpty(n.Filename)) continue;
                if (!filenamesByPart.TryGetValue(n.PartNumber, out var fns))
                    filenamesByPart[n.PartNumber] = fns = new List<string>();
                if (!fns.Any(x => string.Equals(x, n.Filename, StringComparison.OrdinalIgnoreCase)))
                    fns.Add(n.Filename);
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
                            filenames = filenamesByPart.TryGetValue(k.PartNumber, out var allFns) && allFns.Count > 0
                                ? allFns
                                : (string.IsNullOrEmpty(k.Filename) ? new List<string>() : new List<string> { k.Filename }),
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
            var byPart = new Dictionary<string, PartEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in native)
            {
                if (string.IsNullOrEmpty(n.PartNumber)) continue;
                if (!byPart.TryGetValue(n.PartNumber, out var entry))
                {
                    // First file for this part_number is the canonical native
                    // (becomes reference_documents.3d_raw — one per part).
                    AssemblyFileRef step = stepByPart.TryGetValue(n.PartNumber, out var s) ? s : null;
                    entry = new PartEntry
                    {
                        PartNumber = n.PartNumber,
                        NativeFilename = n.Filename,
                        NativePath = n.FullPath,
                        StepFilename = step?.Filename,
                        StepPath = step?.FullPath,
                        DetectedDescription = null,
                    };
                    byPart[n.PartNumber] = entry;
                    result.Add(entry);
                }
                else
                {
                    // Additional DISTINCT physical file sharing this part_number
                    // (CATIA stores repeated parts as _1/_2/_3 copies, each a
                    // separate document with its own internal UUID). Keep it as a
                    // companion so checkout can restore the byte-exact original —
                    // CATIA rejects a mere copy of the canonical as "wrong
                    // information". The part LIBRARY still keeps one canonical
                    // native; companions are stored alongside under the part.
                    if (string.IsNullOrEmpty(n.Filename) || string.IsNullOrEmpty(n.FullPath)) continue;
                    if (string.Equals(n.Filename, entry.NativeFilename, StringComparison.OrdinalIgnoreCase)) continue;
                    if (entry.CompanionFilenames.Any(f => string.Equals(f, n.Filename, StringComparison.OrdinalIgnoreCase))) continue;
                    entry.CompanionFilenames.Add(n.Filename);
                    entry.CompanionPaths.Add(n.FullPath);
                }
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
            // Set only for assembly entries. See AttachTreeManifests.
            public string TreeFilename;
            public string TreePath;
            // Extra distinct files sharing this part_number (CATIA _1/_2/_3
            // copies). Stored alongside the canonical so checkout restores each
            // byte-exact. Parallel lists: filename[i] ↔ path[i].
            public List<string> CompanionFilenames = new List<string>();
            public List<string> CompanionPaths = new List<string>();
            public string DetectedDescription;

            public object ToUploadJson() => new
            {
                part_number = PartNumber,
                filename = NativeFilename,
                step_filename = StepFilename,
                tree_filename = TreeFilename,
                companion_filenames = CompanionFilenames,
                detected_description = DetectedDescription,
            };

            public IEnumerable<string> AllPaths()
            {
                if (!string.IsNullOrEmpty(NativePath)) yield return NativePath;
                if (!string.IsNullOrEmpty(StepPath)) yield return StepPath;
                if (!string.IsNullOrEmpty(TreePath)) yield return TreePath;
                foreach (var p in CompanionPaths)
                    if (!string.IsNullOrEmpty(p)) yield return p;
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
                CompanionFilenames = CompanionFilenames,
                CompanionPaths = CompanionPaths,
                DetectedDescription = DetectedDescription,
            };
        }
    }
}
