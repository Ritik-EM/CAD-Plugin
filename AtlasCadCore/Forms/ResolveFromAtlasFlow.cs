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
    /// Resolve every missing child reference in the active assembly using
    /// atlas as the source of truth — never the local file system.
    ///
    /// Flow:
    ///   1. Walk the assembly, collect missing children.
    ///   2. For each, parse the leading 10-char part_number from filename
    ///      and look it up in part_master_library.
    ///   3. If atlas has a native 3d_raw → download to a temp folder with
    ///      the exact filename SW expects.
    ///   4. For everything that atlas couldn't supply, show
    ///      MissingChildUploadForm so the user can attach local files —
    ///      with the option to release a new revision or just attach the
    ///      file to the existing revision.
    ///   5. Add the temp folder to SW search paths + reload so SW picks
    ///      up the resolved + uploaded children.
    /// </summary>
    public static class ResolveFromAtlasFlow
    {
        public static Task RunAsync(AtlasApiClient api, ICadAdapter adapter)
            => RunAsync(api, adapter, adapter?.GetActiveDocument(), silentIfNothingMissing: false);

        public static async Task RunAsync(
            AtlasApiClient api, ICadAdapter adapter, CadDocument doc,
            bool silentIfNothingMissing)
        {
            if (doc == null || !doc.IsAssembly)
            {
                if (!silentIfNothingMissing)
                    MessageBox.Show("Open an assembly first.", "Atlas — Resolve from Atlas",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var missing = adapter.FindMissingComponents(doc) ?? new List<MissingComponent>();
            if (missing.Count == 0)
            {
                if (!silentIfNothingMissing)
                    MessageBox.Show("No missing references — this assembly is fully resolved.",
                        "Atlas — Resolve from Atlas",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string resolveDir = Path.Combine(Path.GetTempPath(), "AtlasCad", "resolve");
            Directory.CreateDirectory(resolveDir);

            int resolvedFromAtlas = 0;
            var unresolved = new List<MissingComponent>();

            using (var progress = new ProgressForm("Atlas — Resolving children from atlas"))
            {
                progress.Show();
                progress.SetPhase($"Found {missing.Count} missing reference(s)…", 0, missing.Count);

                for (int i = 0; i < missing.Count; i++)
                {
                    var m = missing[i];
                    progress.SetPhase($"Looking up {i + 1}/{missing.Count}: {m.Filename}", i, missing.Count);

                    if (string.IsNullOrEmpty(m.PartNumber))
                    {
                        unresolved.Add(m);
                        continue;
                    }

                    string nativeKey = await TryResolveNativeKeyAsync(api, m.PartNumber);
                    if (nativeKey == null)
                    {
                        unresolved.Add(m);
                        continue;
                    }

                    try
                    {
                        string url = await api.GetS3DownloadUrlAsync(nativeKey);
                        string localPath = Path.Combine(resolveDir, m.Filename);
                        await api.DownloadFileAsync(url, localPath);
                        resolvedFromAtlas++;
                    }
                    catch
                    {
                        unresolved.Add(m);
                    }
                }

                progress.Done();
            }

            // Hand off unresolved children to the user — they pick local
            // files and choose whether to release a new revision or just
            // attach the 3d_raw to the existing one.
            int uploadedFromLocal = 0;
            if (unresolved.Count > 0)
            {
                uploadedFromLocal = await PromptAndUploadAsync(api, unresolved, resolveDir);
            }

            if (resolvedFromAtlas > 0 || uploadedFromLocal > 0)
            {
                adapter.AddSearchFolder(resolveDir);
                try { adapter.ReloadActiveDocument(); }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        "Reload failed — close and reopen the assembly manually.\n\n" + ex.Message,
                        "Atlas — Resolve from Atlas",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            // Summary (skipped when silentIfNothingMissing and everything is
            // perfectly resolved, so the Check Out flow can chain into this
            // without an extra popup when nothing's missing).
            if (!silentIfNothingMissing || unresolved.Count > 0 || uploadedFromLocal > 0)
            {
                int totalResolved = resolvedFromAtlas + uploadedFromLocal;
                var summary = new System.Text.StringBuilder();
                summary.AppendLine($"Resolved {totalResolved} of {missing.Count} missing reference(s):");
                summary.AppendLine($"  • from atlas: {resolvedFromAtlas}");
                summary.AppendLine($"  • uploaded by you: {uploadedFromLocal}");
                int skipped = missing.Count - totalResolved;
                if (skipped > 0)
                    summary.AppendLine($"  • skipped (no local file picked): {skipped}");
                MessageBox.Show(summary.ToString(), "Atlas — Resolve from Atlas",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private static async Task<int> PromptAndUploadAsync(
            AtlasApiClient api, List<MissingComponent> unresolved, string resolveDir)
        {
            using (var dlg = new MissingChildUploadForm(unresolved))
            {
                dlg.SendOtpRequested += async () =>
                {
                    try { await api.RequestReleaseRevisionOtpAsync(); }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Could not send OTP: " + ex.Message, "Atlas",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                };

                if (dlg.ShowDialog() != DialogResult.OK) return 0;

                var picked = dlg.Result ?? new List<MissingChildUploadForm.Row>();
                if (picked.Count == 0) return 0;

                // Build the upload tree (one entry per picked file). filename
                // is the LOCAL file's name (multipart matches by basename),
                // part_number is the one parsed from the SW-expected filename
                // — that's the atlas identity we're attaching the file to.
                var tree = picked.Select(r => (object)new
                {
                    part_number = r.PartNumber,
                    filename = Path.GetFileName(r.LocalPath),
                    step_filename = (string)null,
                    detected_description = (string)null,
                }).ToList();
                var paths = picked.Select(r => r.LocalPath).ToList();

                int uploaded = 0;
                try
                {
                    var result = await api.UploadPartMasterAsync(
                        tree, paths,
                        releaseNewRevision: dlg.ReleaseNewRevision,
                        otp: dlg.Otp);
                    uploaded = result?.attached?.Count ?? 0;

                    // Also copy the picked file into the resolve folder under
                    // the ORIGINAL filename SW expects, so SW finds it when
                    // it re-scans search folders on reload.
                    foreach (var r in picked)
                    {
                        string targetPath = Path.Combine(resolveDir, r.OriginalFilename);
                        try { File.Copy(r.LocalPath, targetPath, overwrite: true); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Upload failed:\n\n" + ex.Message,
                        "Atlas — Resolve from Atlas",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return uploaded;
            }
        }

        private static async Task<string> TryResolveNativeKeyAsync(AtlasApiClient api, string partNumber)
        {
            try
            {
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
                // ignored — caller treats null as "not resolvable from atlas"
            }
            return null;
        }
    }
}
