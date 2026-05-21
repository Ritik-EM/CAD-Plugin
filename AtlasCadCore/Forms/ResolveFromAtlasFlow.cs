using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AtlasCadCore.Adapter;
using AtlasCadCore.ApiClient;

namespace AtlasCadCore.Forms
{
    /// <summary>
    /// Orchestrator for the "Resolve from Atlas" ribbon action.
    ///
    /// Scenario: a designer received a `.sldasm` from someone else (or
    /// committed only the assembly file to git/email), but the child
    /// `.sldprt` files aren't on this machine's disk. SW opens the assembly
    /// with broken references (yellow triangles in the tree).
    ///
    /// The flow:
    ///   1. Walk the assembly via the adapter, collect every missing child
    ///      (component whose GetPathName resolves to a file that doesn't
    ///      exist locally).
    ///   2. For each missing child, parse the leading 10-char part_number
    ///      from the filename, look that part up in part_master_library
    ///      via the existing /part-master/part-number?search=… endpoint.
    ///   3. Pick the latest revision's 3d_raw S3 key, presign + download
    ///      to %TEMP%\AtlasCad\resolve\ — preserving the original filename
    ///      so SW recognises it.
    ///   4. Register that folder in SolidWorks' search paths.
    ///   5. Reload the active assembly so SW re-resolves children using
    ///      the new search folder.
    /// </summary>
    public static class ResolveFromAtlasFlow
    {
        public static async Task RunAsync(AtlasApiClient api, ICadAdapter adapter)
        {
            CadDocument doc = adapter.GetActiveDocument();
            if (doc == null || !doc.IsAssembly)
            {
                MessageBox.Show("Open an assembly first.", "Atlas — Resolve from Atlas",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var missing = adapter.FindMissingComponents(doc) ?? new List<MissingComponent>();
            if (missing.Count == 0)
            {
                MessageBox.Show("No missing references — this assembly is fully resolved.",
                    "Atlas — Resolve from Atlas",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string resolveDir = Path.Combine(Path.GetTempPath(), "AtlasCad", "resolve");
            Directory.CreateDirectory(resolveDir);

            int resolved = 0;
            var failures = new List<string>();

            using (var progress = new ProgressForm("Atlas — Resolve from Atlas"))
            {
                progress.Show();
                progress.SetPhase($"Found {missing.Count} missing reference(s)…", 0, missing.Count);

                for (int i = 0; i < missing.Count; i++)
                {
                    var m = missing[i];
                    progress.SetPhase($"Resolving {i + 1}/{missing.Count}: {m.Filename}", i, missing.Count);

                    if (string.IsNullOrEmpty(m.PartNumber))
                    {
                        failures.Add($"  {m.Filename} — filename doesn't start with a 10-char part_number");
                        continue;
                    }

                    string nativeKey = await TryResolveNativeKeyAsync(api, m.PartNumber);
                    if (nativeKey == null)
                    {
                        failures.Add($"  {m.Filename} — part {m.PartNumber} not found in atlas, or has no native file");
                        continue;
                    }

                    try
                    {
                        string url = await api.GetS3DownloadUrlAsync(nativeKey);
                        // Save with the EXACT filename SW expects so it matches
                        // when SW searches the folder on reload.
                        string localPath = Path.Combine(resolveDir, m.Filename);
                        await api.DownloadFileAsync(url, localPath);
                        resolved++;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"  {m.Filename} — download failed: {ex.Message}");
                    }
                }

                progress.Done();
            }

            if (resolved > 0)
            {
                adapter.AddSearchFolder(resolveDir);
            }

            var summary = new System.Text.StringBuilder();
            summary.AppendLine($"Resolved {resolved} of {missing.Count} missing reference(s).");
            summary.AppendLine();
            summary.AppendLine($"Files downloaded to: {resolveDir}");
            summary.AppendLine($"(added to SolidWorks search folders)");
            if (failures.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine("Not resolved:");
                foreach (var f in failures.Take(20)) summary.AppendLine(f);
                if (failures.Count > 20) summary.AppendLine($"  … {failures.Count - 20} more");
            }
            if (resolved > 0)
            {
                summary.AppendLine();
                summary.AppendLine("Reloading the assembly to pick up the resolved files…");
            }

            MessageBox.Show(summary.ToString(), "Atlas — Resolve from Atlas",
                MessageBoxButtons.OK,
                resolved == missing.Count ? MessageBoxIcon.Information : MessageBoxIcon.Warning);

            if (resolved > 0)
            {
                try { adapter.ReloadActiveDocument(); }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        "Reload failed — please close and reopen the assembly manually.\n\n" + ex.Message,
                        "Atlas — Resolve from Atlas",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        /// <summary>
        /// Find the S3 key for `partNumber`'s active 3d_raw native file. Uses
        /// the existing /part-master/part-number list endpoint with search,
        /// then filters client-side for the exact part_number match.
        /// </summary>
        private static async Task<string> TryResolveNativeKeyAsync(AtlasApiClient api, string partNumber)
        {
            try
            {
                // Search returns up to 50 candidate docs whose description or
                // any embedded part_number matches the query.
                var page = await api.ListPartMasterAsync(
                    releaseType: null, search: partNumber, page: 1, limit: 50);

                foreach (var d in page?.items ?? new List<PartMasterDocumentDto>())
                {
                    if (d.releases == null) continue;
                    foreach (var bucket in d.releases.Values)
                    {
                        if (bucket == null) continue;
                        foreach (var rev in bucket)
                        {
                            if (rev == null) continue;
                            if (!string.Equals(rev.part_number, partNumber, StringComparison.OrdinalIgnoreCase))
                                continue;
                            string nativeKey = rev.EffectiveRefs?.Native3dRaw;
                            if (!string.IsNullOrEmpty(nativeKey)) return nativeKey;
                        }
                    }
                }
            }
            catch
            {
                // Swallow — caller treats null as "not resolvable".
            }
            return null;
        }
    }
}
