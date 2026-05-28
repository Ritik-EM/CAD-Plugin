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
    public static class ResolveFromAtlasFlow
    {
        public static Task RunAsync(AtlasApiClient api, ICadAdapter adapter)
            => RunAsync(api, adapter, adapter?.GetActiveDocument(), silentIfNothingMissing: false);

        public static async Task RunAsync(
            AtlasApiClient api, ICadAdapter adapter, CadDocument doc,
            bool silentIfNothingMissing)
        {
            if (doc == null)
            {
                if (!silentIfNothingMissing)
                    MessageBox.Show("Open a file in SolidWorks first.",
                        "Atlas — Resolve from Atlas",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!doc.IsAssembly)
            {
                if (!silentIfNothingMissing)
                    MessageBox.Show(
                        "Resolve from Atlas only applies to assemblies — it " +
                        "downloads missing child files from atlas for the " +
                        "currently open .sldasm.\n\n" +
                        "The active document is a single part. If you want " +
                        "to push it to atlas, use the “Upload to Atlas” " +
                        "ribbon button instead.",
                        "Atlas — Resolve from Atlas",
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

            // We resolve into two places, in order of preference:
            //   1) The exact ExpectedPath baked into the assembly file
            //      (e.g. C:\Users\…\Desktop\C-282088-1-F-3D.CATPart) — when
            //      writable. CATIA's first lookup rule is the recorded
            //      absolute path, so dropping the file there means the
            //      broken-links dialog will NOT appear on subsequent opens.
            //   2) The assembly's own folder — CATIA / SW's second lookup.
            //   3) A temp resolve dir — last-ditch, plus AddSearchFolder.
            string assemblyDir = !string.IsNullOrEmpty(doc.FullPath)
                ? Path.GetDirectoryName(doc.FullPath)
                : null;
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
                    string searchCode = m.PartNumber
                        ?? AtlasCadCore.Utility.PartNumberParser.ExtractLeadingCode(m.Filename);
                    if (string.IsNullOrEmpty(searchCode))
                    {
                        unresolved.Add(m);
                        continue;
                    }

                    var found = await TryResolveFromAtlasAsync(api, searchCode);
                    if (found == null)
                    {
                        unresolved.Add(m);
                        continue;
                    }

                    try
                    {
                        string url = await api.GetS3DownloadUrlAsync(found.Value.S3Key);
                        string targetPath = ChooseTargetPath(m, assemblyDir, resolveDir);
                        Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                        if (found.Value.IsStep)
                        {
                            string stepTemp = Path.Combine(resolveDir,
                                "_step_" + Path.GetFileName(found.Value.S3Key));
                            await api.DownloadFileAsync(url, stepTemp);
                            string convertedPath = adapter.ImportStepAsNative(stepTemp, targetPath);
                            if (!string.Equals(convertedPath, targetPath, StringComparison.OrdinalIgnoreCase)
                                && File.Exists(convertedPath))
                            {
                                try { File.Copy(convertedPath, targetPath, overwrite: true); } catch { }
                            }
                        }
                        else
                        {
                            await api.DownloadFileAsync(url, targetPath);
                        }
                        resolvedFromAtlas++;
                    }
                    catch
                    {
                        unresolved.Add(m);
                    }
                }

                progress.Done();
            }

            int uploadedFromLocal = 0;
            int attachedFromAtlas = 0;
            if (unresolved.Count > 0)
            {
                (uploadedFromLocal, attachedFromAtlas) =
                    await PromptAndResolveAsync(api, adapter, unresolved, resolveDir, assemblyDir);
            }
            resolvedFromAtlas += attachedFromAtlas;

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

        /// <summary>
        /// Picks where to drop a resolved file. Prefers the exact path the
        /// assembly recorded (so CATIA's broken-links dialog doesn't fire on
        /// next open). Falls back to the assembly's own folder, then to the
        /// temp resolve dir. Returns the chosen target path; caller is
        /// responsible for creating the directory.
        /// </summary>
        private static string ChooseTargetPath(MissingComponent m, string assemblyDir, string resolveDir)
        {
            if (!string.IsNullOrEmpty(m.ExpectedPath))
            {
                try
                {
                    string parent = Path.GetDirectoryName(m.ExpectedPath);
                    if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                        return m.ExpectedPath;
                    if (!string.IsNullOrEmpty(parent))
                    {
                        Directory.CreateDirectory(parent);
                        return m.ExpectedPath;
                    }
                }
                catch { /* fall through */ }
            }
            if (!string.IsNullOrEmpty(assemblyDir) && Directory.Exists(assemblyDir))
                return Path.Combine(assemblyDir, m.Filename);
            return Path.Combine(resolveDir, m.Filename);
        }

        private static async Task<(int uploadedFromLocal, int attachedFromAtlas)> PromptAndResolveAsync(
            AtlasApiClient api, ICadAdapter adapter,
            List<MissingComponent> unresolved, string resolveDir, string assemblyDir)
        {
            using (var dlg = new MissingChildUploadForm(unresolved, api))
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

                if (dlg.ShowDialog() != DialogResult.OK) return (0, 0);

                var picked = dlg.Result ?? new List<MissingChildUploadForm.Row>();
                if (picked.Count == 0) return (0, 0);

                // Two buckets per row: local-file-to-upload vs atlas-pn-to-download.
                var localRows = picked.Where(r => !string.IsNullOrEmpty(r.LocalPath)).ToList();
                var atlasRows = picked.Where(r => !string.IsNullOrEmpty(r.AtlasPartNumber)).ToList();

                int uploaded = 0;
                if (localRows.Count > 0)
                {
                    var tree = localRows.Select(r => (object)new
                    {
                        part_number = r.PartNumber,
                        filename = Path.GetFileName(r.LocalPath),
                        step_filename = (string)null,
                        detected_description = (string)null,
                    }).ToList();
                    var paths = localRows.Select(r => r.LocalPath).ToList();
                    try
                    {
                        var result = await api.UploadPartMasterAsync(
                            tree, paths,
                            releaseNewRevision: dlg.ReleaseNewRevision,
                            otp: dlg.Otp);
                        uploaded = result?.attached?.Count ?? 0;
                        foreach (var r in localRows)
                        {
                            var stub = new MissingComponent
                            {
                                Filename = r.OriginalFilename,
                                ExpectedPath = unresolved
                                    .FirstOrDefault(u => string.Equals(u.Filename, r.OriginalFilename, StringComparison.OrdinalIgnoreCase))
                                    ?.ExpectedPath,
                            };
                            string targetPath = ChooseTargetPath(stub, assemblyDir, resolveDir);
                            try
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                                File.Copy(r.LocalPath, targetPath, overwrite: true);
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Upload failed:\n\n" + ex.Message,
                            "Atlas — Resolve from Atlas",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }

                int attachedFromAtlas = 0;
                foreach (var r in atlasRows)
                {
                    var found = await TryResolveFromAtlasAsync(api, r.AtlasPartNumber);
                    if (found == null) continue;
                    try
                    {
                        string url = await api.GetS3DownloadUrlAsync(found.Value.S3Key);
                        var stub = new MissingComponent
                        {
                            Filename = r.OriginalFilename,
                            ExpectedPath = unresolved
                                .FirstOrDefault(u => string.Equals(u.Filename, r.OriginalFilename, StringComparison.OrdinalIgnoreCase))
                                ?.ExpectedPath,
                        };
                        string targetPath = ChooseTargetPath(stub, assemblyDir, resolveDir);
                        Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                        if (found.Value.IsStep)
                        {
                            string stepTemp = Path.Combine(resolveDir,
                                "_step_" + Path.GetFileName(found.Value.S3Key));
                            await api.DownloadFileAsync(url, stepTemp);
                            string converted = adapter.ImportStepAsNative(stepTemp, targetPath);
                            if (!string.Equals(converted, targetPath, StringComparison.OrdinalIgnoreCase)
                                && File.Exists(converted))
                            {
                                try { File.Copy(converted, targetPath, overwrite: true); } catch { }
                            }
                        }
                        else
                        {
                            await api.DownloadFileAsync(url, targetPath);
                        }
                        attachedFromAtlas++;
                    }
                    catch { /* row stays unresolved; surfaced in summary */ }
                }

                return (uploaded, attachedFromAtlas);
            }
        }

        public struct ResolvedFile
        {
            public string S3Key;
            public bool IsStep;     
        }

        private static async Task<ResolvedFile?> TryResolveFromAtlasAsync(AtlasApiClient api, string code)
        {
            try
            {
                var page = await api.ListPartMasterAsync(
                    releaseType: null, search: code, page: 1, limit: 50);

                ResolvedFile? best = null;
                int bestScore = int.MaxValue;       // lower is better
                string padded = (code != null && code.Length < 10) ? code + "00" : null;

                void Consider(int score, string key, bool isStep)
                {
                    if (string.IsNullOrEmpty(key)) return;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = new ResolvedFile { S3Key = key, IsStep = isStep };
                    }
                }

                foreach (var d in page?.items ?? new List<PartMasterDocumentDto>())
                {
                    if (d.releases == null) continue;
                    foreach (var bucket in d.releases.Values)
                    {
                        if (bucket == null) continue;
                        foreach (var rev in bucket)
                        {
                            if (rev?.part_number == null) continue;
                            var refs = rev.EffectiveRefs;
                            if (refs == null) continue;

                            int matchTier;
                            if (string.Equals(rev.part_number, code, StringComparison.OrdinalIgnoreCase))
                                matchTier = 0;
                            else if (padded != null && string.Equals(rev.part_number, padded, StringComparison.OrdinalIgnoreCase))
                                matchTier = 1;
                            else if (rev.part_number.StartsWith(code, StringComparison.OrdinalIgnoreCase))
                                matchTier = 2;
                            else
                                continue;

                            int activeWeight = rev.active == true ? 0 : 1;
                            int baseScore = matchTier * 100 + activeWeight * 10;
                            Consider(baseScore + 0, refs.Native3dRaw, isStep: false);
                            Consider(baseScore + 1, refs.Step3d,      isStep: true);
                        }
                    }
                }

                return best;
            }
            catch
            {
                // ignored — caller treats null as "not resolvable from atlas"
            }
            return null;
        }
    }
}
