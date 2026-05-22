using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using AtlasCadCore.Adapter;
using AtlasCadCore.ApiClient;
using AtlasCadCore.Utility;

namespace AtlasCadCore.Forms
{
    /// <summary>
    /// Orchestrator for the part-master check-in flow. Triggered from the
    /// ribbon "Check In" button (wired in P7.10).
    ///   1. Identify the checked-out root part via CheckoutTracker.
    ///   2. Walk the assembly tree (with parent links from the adapter).
    ///   3. Show CheckinPropagationForm — user ticks modified parts; dialog
    ///      computes ancestor propagation live.
    ///   4. Build CheckinTreeEntry payload + multipart files.
    ///   5. POST /cad/part-master/{root}/checkin — backend bumps revisions
    ///      leaves-first, attaches uploaded files, releases the lock.
    ///   6. Untrack the local path and show a summary of the bumps.
    /// </summary>
    public static class CheckinFlow
    {
        public static async Task RunAsync(AtlasApiClient api, ICadAdapter adapter)
        {
            CadDocument doc = adapter.GetActiveDocument();
            if (doc == null)
            {
                MessageBox.Show("Open the checked-out file first.", "Atlas — Check In",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string rootPartNumber = CheckoutTracker.ResolvePartNumberForPath(doc.FullPath);
            if (string.IsNullOrEmpty(rootPartNumber))
            {
                var tracked = CheckoutTracker.Snapshot();
                string trackedSummary = tracked.Count == 0
                    ? "(nothing currently checked out)"
                    : string.Join("\n", tracked.Select(kv => $"  {kv.Value}  ←  {kv.Key}"));
                MessageBox.Show(
                    "This file isn't tracked as a checked-out Atlas part.\n\n" +
                    $"Active doc:\n  {doc.FullPath ?? "(unsaved)"}\n\n" +
                    $"Tracked checkouts:\n{trackedSummary}\n\n" +
                    "Use Browse Part Master Library → Check Out first, edit, then Check In.",
                    "Atlas", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string releaseType = PartNumberParser.ReleaseTypeFromPartNumber(rootPartNumber);
            if (releaseType == null)
            {
                MessageBox.Show(
                    $"Couldn't derive release_type from {rootPartNumber}. " +
                    "The checked-out part_number doesn't follow the 10-char format.",
                    "Atlas", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            using (var progress = new ProgressForm("Atlas — Check In"))
            {
                progress.Show();
                string stepDir = null;
                try
                {
                    adapter.SaveDocument(doc);

                    // Both assemblies and single parts go through check-in.
                    // For an assembly we walk its tree; for a single .sldprt
                    // we synthesise a one-entry list of the active doc so
                    // the user can check in a single part — or check in
                    // with no changes ticked so the lock just gets released.
                    List<AssemblyFileRef> native;
                    if (doc.IsAssembly)
                    {
                        progress.SetPhase("Walking assembly tree…");
                        native = adapter.WalkAssembly(doc) ?? new List<AssemblyFileRef>();
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
                                "Atlas — Check In",
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
                                // The tracked part_number is the source of truth for
                                // the active doc's identity — use it directly instead
                                // of re-parsing the filename (which may have lost the
                                // canonical part_number suffix).
                                PartNumber = rootPartNumber,
                                ParentPartNumber = null,
                                NativeHandle = doc.NativeHandle,
                            }
                        };
                    }
                    if (native.Count == 0)
                    {
                        Beep();
                        MessageBox.Show("Nothing to check in.", "Atlas",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    // Surface components the adapter dropped (suppressed,
                    // missing on disk, no path, or no resolvable part_number)
                    // before we silently exclude them from the check-in tree.
                    // Silently dropping was hiding the parts a user had actually
                    // edited from the propagation grid.
                    var dropped = native.Where(n => !string.IsNullOrEmpty(n.SkipReason)).ToList();

                    // Always log what the adapter returned so we can diagnose
                    // "only N rows in the grid" complaints without forcing the
                    // user through a rebuild loop. Writes silently to
                    // %APPDATA%\AtlasCad\walk_assembly.log on every check-in.
                    try
                    {
                        string logDir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "AtlasCad");
                        Directory.CreateDirectory(logDir);
                        string logPath = Path.Combine(logDir, "walk_assembly.log");
                        var lines = new List<string>();
                        lines.Add($"=== {DateTime.Now:O} — Check In on {rootPartNumber} ===");
                        lines.Add($"WalkAssembly returned {native.Count} entries " +
                                  $"({native.Count - dropped.Count} healthy, {dropped.Count} dropped):");
                        foreach (var n in native)
                        {
                            string mark = string.IsNullOrEmpty(n.SkipReason) ? "ok " : "DROP";
                            string pn = n.PartNumber ?? "(no-pn)";
                            string reason = n.SkipReason ?? "";
                            lines.Add($"  [{mark}] pn={pn,-12} parent={n.ParentPartNumber ?? "-",-12} reason={reason,-15} path={n.FullPath}");
                        }
                        lines.Add("");
                        File.AppendAllLines(logPath, lines);
                    }
                    catch { }

                    if (dropped.Count > 0)
                    {
                        progress.Hide();
                        var msg = new StringBuilder();
                        msg.AppendLine($"{dropped.Count} component(s) in this assembly can't be checked in:");
                        msg.AppendLine();
                        foreach (var d in dropped.Take(20))
                        {
                            msg.AppendLine($"  • {d.Filename}");
                            msg.AppendLine($"      reason: {d.SkipReason}");
                        }
                        if (dropped.Count > 20)
                            msg.AppendLine($"  … {dropped.Count - 20} more");
                        msg.AppendLine();
                        msg.AppendLine("Fixes by reason:");
                        msg.AppendLine("  suppressed     — unsuppress in SW and try again.");
                        msg.AppendLine("  missing-file   — repath the component (the .sldprt isn't on disk).");
                        msg.AppendLine("  no-path        — component is virtual/in-place; save it as a real file first.");
                        msg.AppendLine("  no-part-number — set the PART_NUMBER custom property on the part, or rename the file");
                        msg.AppendLine("                   so it starts with a 10-char Atlas part_number (e.g. AN5T00980A_...).");
                        msg.AppendLine();
                        msg.AppendLine("Continue check-in anyway? Healthy components will still be processed.");
                        var resp = MessageBox.Show(msg.ToString(),
                            "Atlas — Check In", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                        if (resp != DialogResult.Yes) return;
                        progress.Show();
                        native = native.Where(n => string.IsNullOrEmpty(n.SkipReason)).ToList();
                    }
                    if (native.Count == 0)
                    {
                        Beep();
                        MessageBox.Show("Nothing left to check in after excluding dropped components.",
                            "Atlas", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    // Export a STEP per native file — each revision needs
                    // its own current .stp so atlas-ui's 3D viewer reflects
                    // the latest geometry for every part, not just the root.
                    // Adapter uses the already-loaded IModelDoc2 captured in
                    // WalkAssembly to skip OpenDoc6, and reports per-file
                    // progress so the user can see which child is exporting.
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

                    // Per-part bundle: native + optional STP + depth in tree.
                    var entries = BuildPartEntries(native, steps, rootPartNumber);

                    // Empty tree usually means every file's filename failed
                    // PartNumberParser. Surface this clearly rather than
                    // sending an empty tree to the backend (which would just
                    // return "Tree is empty").
                    if (entries.Count == 0)
                    {
                        progress.Hide();
                        MessageBox.Show(
                            "No parts in this assembly have recognisable Atlas part_numbers.\n\n" +
                            "Filenames must start with a 10-character part_number " +
                            "(letters and digits, e.g. \"AN5T01040A_door_rh.sldasm\").\n\n" +
                            "If your files don't match this convention, set the " +
                            "PART_NUMBER custom property on each component in SW " +
                            "to its atlas part_number.",
                            "Atlas — Check In",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    // Verify the resolved root part_number actually appears
                    // in the walked tree. If not, CheckoutTracker pointed at
                    // a different file than what's currently open — usually
                    // a stale entry from a previous session.
                    if (!entries.Any(e => string.Equals(e.PartNumber, rootPartNumber, StringComparison.OrdinalIgnoreCase)))
                    {
                        progress.Hide();
                        string treePns = string.Join(", ", entries.Take(5).Select(e => e.PartNumber));
                        if (entries.Count > 5) treePns += $", … ({entries.Count - 5} more)";
                        MessageBox.Show(
                            $"The currently open assembly doesn't match the checked-out part.\n\n" +
                            $"Checkout tracker says:  {rootPartNumber}\n" +
                            $"Tree root in SolidWorks: {entries.FirstOrDefault(e => string.IsNullOrEmpty(e.ParentPartNumber))?.PartNumber ?? "(none)"}\n" +
                            $"Tree contents: {treePns}\n\n" +
                            "Either open the correct file for this checkout, or go to " +
                            "Browse Part Master Library → select " + rootPartNumber +
                            " → Cancel Checkout to clear the stale lock and retry.",
                            "Atlas — Check In",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    // Pre-validate every part_number against part_master_library
                    // BEFORE we open the propagation dialog. If any are unknown
                    // to atlas (legacy parts, parts added in SW but never
                    // uploaded), the backend would return a 400 with a raw
                    // JSON message ("Tree references unknown part_numbers:
                    // [...]") that's hard to act on. Surface them here with
                    // their filenames + a clear "Upload to Atlas first" hint.
                    progress.SetPhase("Validating part_numbers against atlas…");
                    PartLookupResult lookup;
                    try
                    {
                        lookup = await api.LookupPartNumbersAsync(
                            entries.Select(e => e.PartNumber).ToList());
                    }
                    catch (Exception ex)
                    {
                        progress.Hide();
                        MessageBox.Show(
                            "Couldn't validate part_numbers against atlas:\n\n" + ex.Message +
                            "\n\nNetwork or auth issue. Check connection and try again.",
                            "Atlas — Check In",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    if (lookup?.missing != null && lookup.missing.Count > 0)
                    {
                        progress.Hide();
                        var missingSet = new HashSet<string>(lookup.missing, StringComparer.OrdinalIgnoreCase);
                        var missingDetails = entries
                            .Where(e => missingSet.Contains(e.PartNumber))
                            .ToList();

                        // Policy: the plugin no longer mints part_master
                        // entries on the fly. New part_numbers must be
                        // released via atlas-ui first (single source of
                        // truth for the metadata + reviewer workflow).
                        // Surface a clear list and bail; the lock stays
                        // held so the user can release on atlas-ui and
                        // retry Check In without re-Checking Out.
                        var msg = new StringBuilder();
                        msg.AppendLine(
                            $"{missingDetails.Count} part(s) in this assembly aren't released on atlas yet:");
                        msg.AppendLine();
                        foreach (var e in missingDetails.Take(50))
                            msg.AppendLine($"  • {e.PartNumber}   ({e.NativeFilename})");
                        if (missingDetails.Count > 50)
                            msg.AppendLine($"  … {missingDetails.Count - 50} more");
                        msg.AppendLine();
                        msg.AppendLine("Release these part_numbers on atlas-ui first, then click");
                        msg.AppendLine("Check In again. Your checkout lock stays held until you");
                        msg.AppendLine("Cancel Checkout or successfully Check In.");
                        MessageBox.Show(msg.ToString(),
                            "Atlas — Check In blocked: unreleased part_numbers",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    progress.Show();

                    // Pre-tick "Modified" for parts whose current sha256
                    // differs from the baseline captured at Check Out. The
                    // baseline is keyed by root part_number in FileHashStash;
                    // if nothing is stashed (e.g. checked out before this
                    // feature shipped), we just fall back to all-unticked.
                    var baseline = FileHashStash.Get(rootPartNumber)
                                   ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    bool HasBaselineChanged(PartEntry e)
                    {
                        if (!baseline.TryGetValue(e.PartNumber, out var oldSha)) return false;
                        if (string.IsNullOrEmpty(e.Sha256) || string.IsNullOrEmpty(oldSha)) return false;
                        return !string.Equals(oldSha, e.Sha256, StringComparison.OrdinalIgnoreCase);
                    }

                    progress.Hide();
                    List<string> changed;
                    string comment;
                    string otp;
                    using (var dlg = new CheckinPropagationForm(
                        rootPartNumber, releaseType,
                        entries.Select(e => new CheckinPropagationForm.TreeRow
                        {
                            PartNumber = e.PartNumber,
                            ParentPartNumber = e.ParentPartNumber,
                            Filename = e.NativeFilename,
                            Depth = e.Depth,
                            PreCheckedAsChanged = HasBaselineChanged(e),
                        }).ToList(),
                        api))
                    {
                        if (dlg.ShowDialog() != DialogResult.OK) return;
                        changed = dlg.ChangedPartNumbers ?? new List<string>();
                        comment = dlg.Comment;
                        otp = dlg.Otp;
                    }
                    progress.Show();

                    progress.SetPhase("Uploading + bumping revisions…");
                    var result = await api.CheckinAsync(
                        rootPartNumber: rootPartNumber,
                        tree: entries.Select(e => (object)e.ToTreeJson()),
                        releaseType: releaseType,
                        changed: changed,
                        comment: comment,
                        otp: otp,
                        filePaths: entries.SelectMany(e => e.AllPaths()));

                    progress.Done();

                    CheckoutTracker.Untrack(doc.FullPath);
                    // After a successful check-in the bumped revisions get
                    // fresh part_numbers and the local files are stale, so
                    // the baseline hashes are no longer meaningful.
                    FileHashStash.Clear(rootPartNumber);
                    MessageBox.Show(BuildSummary(result), "Atlas — Check In",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (UnauthorizedException)
                {
                    throw;  // bubble to the addin-level Run() wrapper
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Check-in failed:\n\n" + ex.Message,
                        "Atlas", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    if (progress.Visible) progress.Close();
                    try { if (stepDir != null) Directory.Delete(stepDir, recursive: true); } catch { }
                }
            }
        }

        private static void Beep() => System.Media.SystemSounds.Beep.Play();

        private static List<PartEntry> BuildPartEntries(
            List<AssemblyFileRef> native, List<AssemblyFileRef> steps, string rootPartNumber)
        {
            var stepByPart = steps
                .Where(s => !string.IsNullOrEmpty(s.PartNumber))
                .GroupBy(s => s.PartNumber)
                .ToDictionary(g => g.Key, g => g.First());

            // Index native by part_number first so depth can be computed.
            var nativeByPart = new Dictionary<string, AssemblyFileRef>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in native)
            {
                if (string.IsNullOrEmpty(n.PartNumber)) continue;
                if (!nativeByPart.ContainsKey(n.PartNumber))
                    nativeByPart[n.PartNumber] = n;
            }

            int DepthOf(string pn)
            {
                int d = 0;
                string cur = pn;
                while (true)
                {
                    if (!nativeByPart.TryGetValue(cur, out var r)) break;
                    if (string.IsNullOrEmpty(r.ParentPartNumber)) break;
                    d++;
                    cur = r.ParentPartNumber;
                    if (d > 64) break;  // safety against cycles
                }
                return d;
            }

            var result = new List<PartEntry>();
            foreach (var kv in nativeByPart)
            {
                var n = kv.Value;
                stepByPart.TryGetValue(n.PartNumber, out var step);
                result.Add(new PartEntry
                {
                    PartNumber = n.PartNumber,
                    ParentPartNumber = n.ParentPartNumber,
                    NativeFilename = n.Filename,
                    NativePath = n.FullPath,
                    StepFilename = step?.Filename,
                    StepPath = step?.FullPath,
                    Sha256 = n.Sha256,
                    Depth = DepthOf(n.PartNumber),
                });
            }
            return result;
        }

        private static string BuildSummary(CheckinResultDto result)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Check-in committed for {result.root_part_number} ({result.release_type}).");
            sb.AppendLine();
            int n = result.bumped?.Count ?? 0;
            if (n == 0)
            {
                sb.AppendLine("No revisions were bumped (no parts marked as modified).");
            }
            else
            {
                sb.AppendLine($"{n} part(s) revision-bumped:");
                foreach (var b in result.bumped)
                    sb.AppendLine($"  {b.old_part_number}  →  {b.new_part_number}");
            }
            return sb.ToString();
        }

        private class PartEntry
        {
            public string PartNumber;
            public string ParentPartNumber;
            public string NativeFilename;
            public string NativePath;
            public string StepFilename;
            public string StepPath;
            public string Sha256;
            public int Depth;

            public object ToTreeJson() => new
            {
                part_number = PartNumber,
                parent_part_number = ParentPartNumber,
                filename = NativeFilename,
                step_filename = StepFilename,
                sha256 = Sha256,
            };

            public IEnumerable<string> AllPaths()
            {
                if (!string.IsNullOrEmpty(NativePath)) yield return NativePath;
                if (!string.IsNullOrEmpty(StepPath)) yield return StepPath;
            }
        }
    }
}
