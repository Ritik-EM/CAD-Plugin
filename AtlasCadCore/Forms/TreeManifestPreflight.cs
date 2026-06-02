using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AtlasCadCore.ApiClient;

namespace AtlasCadCore.Forms
{
    /// <summary>
    /// P7.49 / P7.58 — Pre-download children of an assembly from its
    /// tree.json manifest BEFORE the CAD app opens the parent. Recursively
    /// walks sub-assemblies so deep trees (3+ levels) resolve cleanly on
    /// the first open.
    ///
    /// Required for R2025: R2025's Product COM API does not expose the
    /// recorded filename for unresolved children, so the post-open Resolve
    /// flow cannot detect what needs downloading. The tree.json bypasses
    /// CATIA entirely — we ask atlas what the children are.
    /// </summary>
    public static class TreeManifestPreflight
    {
        public class TreeManifest
        {
            public int version;
            public string root_part_number;
            public string root_filename;
            public List<TreeNode> nodes;
        }

        public class TreeNode
        {
            public string part_number;
            public string filename;
            // Every distinct filename this part_number is referenced under in
            // the assembly. CATIA keeps N physical copies of the same part
            // (paste/insert → _1/_2/_3 suffixed files); Atlas stores one native
            // per part_number, so on checkout we download once and copy it to
            // every name here. Null on legacy manifests → falls back to
            // [filename].
            public List<string> filenames;
            public string parent_part_number;
        }

        /// <summary>Outcome of a preflight pass.</summary>
        public class PreflightResult
        {
            public int Downloaded;
            /// <summary>Manifest children whose native file isn't in Atlas, so
            /// they couldn't be pre-downloaded and will be missing on open.
            /// This is the authoritative "user must upload these" list —
            /// derived from tree.json, NOT from the CAD app's (unreliable)
            /// broken-reference detection.</summary>
            public List<NeedsUpload> Missing = new List<NeedsUpload>();
        }

        public class NeedsUpload
        {
            public string PartNumber;
            public string Filename;
            /// <summary>True = a part_master exists in Atlas but has no native
            /// 3d file attached; False = no part_master at all for this code.</summary>
            public bool InAtlasButNoNative;
        }

        /// <summary>
        /// Entry point. Downloads the root part's manifest, fetches every
        /// listed descendant to <paramref name="assemblyDir"/>, then for
        /// each sub-assembly recursively fetches ITS manifest + descendants
        /// (loop-protected via visited set). Returns what was downloaded and
        /// which children couldn't be resolved from Atlas.
        /// </summary>
        public static async Task<PreflightResult> PreflightAsync(
            AtlasApiClient api,
            ApiClient.PartMasterRevisionDto rootRev,
            string assemblyDir)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new PreflightResult();
            Log($"Preflight START root='{rootRev?.part_number}' dir='{assemblyDir}' " +
                $"rootTreeJson='{rootRev?.EffectiveRefs?.TreeJson ?? "(none)"}'");
            await PreflightRevisionAsync(api, rootRev, assemblyDir, visited, seenMissing, result, depth: 0);
            Log($"Preflight DONE downloaded={result.Downloaded} missing={result.Missing.Count}");
            return result;
        }

        // Diagnostics → %AppData%\AtlasCad\preflight.log. Mirrors the walk log
        // so checkout/download is no longer a black box: every per-child
        // decision (found in atlas? has a native? downloaded / copied / missing)
        // is recorded, which is the authoritative answer to "is the file
        // actually on the server, or did the download just fail?".
        private static void Log(string line)
        {
            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AtlasCad");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(
                    Path.Combine(logDir, "preflight.log"),
                    $"--- {DateTime.Now:O} {line}\n");
            }
            catch { /* logging must never break checkout */ }
        }

        private static async Task PreflightRevisionAsync(
            AtlasApiClient api,
            ApiClient.PartMasterRevisionDto rev,
            string assemblyDir,
            HashSet<string> visited,
            HashSet<string> seenMissing,
            PreflightResult result,
            int depth)
        {
            // Safety: deep CAD trees are usually < 8 levels. 16 caps any
            // pathological case.
            if (depth > 16) { Log($"  [{depth}] depth cap hit for '{rev?.part_number}'"); return; }
            if (rev?.EffectiveRefs?.TreeJson == null)
            {
                // No manifest on this revision → can't pre-download anything.
                // For the ROOT this means the assembly's tree.json was never
                // stored on the server (so checkout finds nothing).
                Log($"  [{depth}] '{rev?.part_number}' has NO tree.json on server — nothing to pre-download");
                return;
            }
            if (string.IsNullOrEmpty(assemblyDir)) { Log($"  [{depth}] assemblyDir empty — abort"); return; }
            if (!string.IsNullOrEmpty(rev.part_number) && !visited.Add(rev.part_number)) return;

            TreeManifest manifest;
            try { manifest = await api.DownloadJsonByS3KeyAsync<TreeManifest>(rev.EffectiveRefs.TreeJson); }
            catch (Exception ex) { Log($"  [{depth}] '{rev.part_number}' tree.json download FAILED: {ex.Message}"); return; }
            if (manifest?.nodes == null || manifest.nodes.Count == 0)
            {
                Log($"  [{depth}] '{rev.part_number}' tree.json has 0 nodes");
                return;
            }

            var nodes = manifest.nodes
                .Where(n => !string.IsNullOrEmpty(n.part_number) && !string.IsNullOrEmpty(n.filename))
                .ToList();
            Log($"  [{depth}] '{rev.part_number}' manifest: {manifest.nodes.Count} nodes ({nodes.Count} with a filename)");
            if (nodes.Count == 0) return;

            foreach (var node in nodes)
            {
                if (visited.Contains(node.part_number)) continue;

                // Every filename this part is linked under (legacy manifests
                // only carry the single `filename`). CATIA keeps repeated parts
                // as distinct _1/_2/_3 files, so the parent assembly references
                // them all — we must place a native under each one or the open
                // fails with "files could not be found".
                var aliasNames = ((node.filenames != null && node.filenames.Count > 0)
                        ? node.filenames
                        : new List<string> { node.filename })
                    .Where(f => !string.IsNullOrEmpty(f))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var missingNames = aliasNames
                    .Where(f => !File.Exists(Path.Combine(assemblyDir, f)))
                    .ToList();

                // Look up the child's full revision so we can both (a) pull
                // the native file if missing on disk, and (b) recurse into
                // its own tree.json if it's an assembly.
                ApiClient.PartMasterRevisionDto childRev = await ResolveLatestRevisionAsync(api, node.part_number);

                bool hasNative = !string.IsNullOrEmpty(childRev?.EffectiveRefs?.Native3dRaw);

                Log($"    node pn='{node.part_number}' aliases={aliasNames.Count} missingOnDisk={missingNames.Count} " +
                    $"inAtlas={(childRev != null ? "yes" : "NO")} hasNative={(hasNative ? "yes" : "NO")} " +
                    $"3d_raw='{childRev?.EffectiveRefs?.Native3dRaw ?? "(none)"}'");

                // Companion files live next to the canonical native under the
                // same part prefix: cad/part-master/<pn>/<filename>. CATIA links
                // each instance by internal UUID, so every referenced file must
                // be the BYTE-EXACT original — a copy of the canonical is
                // rejected as "wrong information". So we download each alias from
                // its OWN key, and only clone the canonical as a last resort for
                // assemblies uploaded before companions were stored.
                string canonicalKey = childRev?.EffectiveRefs?.Native3dRaw;
                string keyPrefix = null;
                if (!string.IsNullOrEmpty(canonicalKey))
                {
                    int slash = canonicalKey.LastIndexOf('/');
                    if (slash >= 0) keyPrefix = canonicalKey.Substring(0, slash + 1);
                }
                string canonicalName = string.IsNullOrEmpty(canonicalKey) ? null : Path.GetFileName(canonicalKey);
                string canonicalPath = canonicalName != null ? Path.Combine(assemblyDir, canonicalName) : null;

                // 1) Ensure the canonical native is on disk (download from its key).
                if (canonicalPath != null && hasNative && !File.Exists(canonicalPath))
                {
                    try
                    {
                        string url = await api.GetS3DownloadUrlAsync(canonicalKey);
                        Directory.CreateDirectory(Path.GetDirectoryName(canonicalPath));
                        await api.DownloadFileAsync(url, canonicalPath);
                        result.Downloaded++;
                        Log($"      downloaded canonical '{canonicalName}'");
                    }
                    catch (Exception ex) { Log($"      canonical download FAILED for '{node.part_number}': {ex.Message}"); }
                }

                // 2) Materialise every referenced filename, byte-exact where
                //    possible (its own companion key), cloning the canonical
                //    only as a legacy fallback.
                foreach (var name in aliasNames)
                {
                    string dest = Path.Combine(assemblyDir, name);
                    if (File.Exists(dest)) continue;

                    bool got = false;
                    if (keyPrefix != null)
                    {
                        string key = keyPrefix + name;
                        try
                        {
                            string url = await api.GetS3DownloadUrlAsync(key);
                            Directory.CreateDirectory(Path.GetDirectoryName(dest));
                            await api.DownloadFileAsync(url, dest);
                            result.Downloaded++;
                            got = true;
                            Log($"      downloaded '{name}'");
                        }
                        catch (Exception ex) { Log($"      companion miss '{name}' (key='{key}'): {ex.Message}"); }
                    }

                    if (!got && canonicalPath != null && File.Exists(canonicalPath))
                    {
                        // Legacy fallback: no companion stored. Clone the
                        // canonical so the file at least exists — CATIA may still
                        // flag "wrong information" until the assembly is re-uploaded.
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(dest));
                            File.Copy(canonicalPath, dest, overwrite: false);
                            result.Downloaded++;
                            Log($"      cloned '{name}' from canonical (no companion on server — re-upload to fix)");
                        }
                        catch (Exception ex) { Log($"      clone FAILED '{name}': {ex.Message}"); }
                    }
                    else if (!got && !hasNative)
                    {
                        // No native in Atlas at all → the user must upload this
                        // part. Record it (deduped) for the post-open report.
                        if (seenMissing.Add(node.part_number))
                            result.Missing.Add(new NeedsUpload
                            {
                                PartNumber = node.part_number,
                                Filename = node.filename,
                                InAtlasButNoNative = childRev != null,
                            });
                    }
                }

                // Recurse: if this child has its own manifest, pre-download
                // its grandchildren into the SAME assemblyDir. CATIA's
                // "same folder as parent" lookup catches everything in one
                // flat directory regardless of depth.
                if (childRev?.EffectiveRefs?.TreeJson != null)
                {
                    await PreflightRevisionAsync(api, childRev, assemblyDir, visited, seenMissing, result, depth + 1);
                }
            }
        }

        private static async Task<ApiClient.PartMasterRevisionDto> ResolveLatestRevisionAsync(
            AtlasApiClient api, string partNumber)
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
                            if (string.Equals(rev?.part_number, partNumber, StringComparison.OrdinalIgnoreCase))
                                return rev;
                        }
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
