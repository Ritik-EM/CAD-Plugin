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
            await PreflightRevisionAsync(api, rootRev, assemblyDir, visited, seenMissing, result, depth: 0);
            return result;
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
            if (depth > 16) return;
            if (rev?.EffectiveRefs?.TreeJson == null) return;
            if (string.IsNullOrEmpty(assemblyDir)) return;
            if (!string.IsNullOrEmpty(rev.part_number) && !visited.Add(rev.part_number)) return;

            var manifest = await api.DownloadJsonByS3KeyAsync<TreeManifest>(rev.EffectiveRefs.TreeJson);
            if (manifest?.nodes == null || manifest.nodes.Count == 0) return;

            var nodes = manifest.nodes
                .Where(n => !string.IsNullOrEmpty(n.part_number) && !string.IsNullOrEmpty(n.filename))
                .ToList();
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

                if (missingNames.Count > 0)
                {
                    // Source for the copies: an alias already on disk, else
                    // download the native once into the first missing name.
                    string sourcePath = aliasNames
                        .Select(f => Path.Combine(assemblyDir, f))
                        .FirstOrDefault(File.Exists);

                    if (sourcePath == null && hasNative)
                    {
                        try
                        {
                            string url = await api.GetS3DownloadUrlAsync(childRev.EffectiveRefs.Native3dRaw);
                            sourcePath = Path.Combine(assemblyDir, missingNames[0]);
                            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath));
                            await api.DownloadFileAsync(url, sourcePath);
                            result.Downloaded++;
                            missingNames.RemoveAt(0);
                        }
                        catch { sourcePath = null; /* per-child failure non-fatal */ }
                    }

                    if (sourcePath != null)
                    {
                        // Fan the native out to every remaining referenced name.
                        foreach (var name in missingNames)
                        {
                            try
                            {
                                string dest = Path.Combine(assemblyDir, name);
                                if (File.Exists(dest)) continue;
                                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                                File.Copy(sourcePath, dest, overwrite: false);
                                result.Downloaded++;
                            }
                            catch { /* per-copy failure non-fatal */ }
                        }
                    }
                    else if (!hasNative)
                    {
                        // No native in Atlas AND not already on disk → the user
                        // must upload this part. Record it (deduped) for the
                        // post-open report.
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
