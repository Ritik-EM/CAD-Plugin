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
            public string parent_part_number;
        }

        /// <summary>
        /// Entry point. Downloads the root part's manifest, fetches every
        /// listed descendant to <paramref name="assemblyDir"/>, then for
        /// each sub-assembly recursively fetches ITS manifest + descendants
        /// (loop-protected via visited set). Returns total files downloaded.
        /// </summary>
        public static Task<int> PreflightAsync(
            AtlasApiClient api,
            ApiClient.PartMasterRevisionDto rootRev,
            string assemblyDir)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return PreflightRevisionAsync(api, rootRev, assemblyDir, visited, depth: 0);
        }

        private static async Task<int> PreflightRevisionAsync(
            AtlasApiClient api,
            ApiClient.PartMasterRevisionDto rev,
            string assemblyDir,
            HashSet<string> visited,
            int depth)
        {
            // Safety: deep CAD trees are usually < 8 levels. 16 caps any
            // pathological case.
            if (depth > 16) return 0;
            if (rev?.EffectiveRefs?.TreeJson == null) return 0;
            if (string.IsNullOrEmpty(assemblyDir)) return 0;
            if (!string.IsNullOrEmpty(rev.part_number) && !visited.Add(rev.part_number)) return 0;

            var manifest = await api.DownloadJsonByS3KeyAsync<TreeManifest>(rev.EffectiveRefs.TreeJson);
            if (manifest?.nodes == null || manifest.nodes.Count == 0) return 0;

            var nodes = manifest.nodes
                .Where(n => !string.IsNullOrEmpty(n.part_number) && !string.IsNullOrEmpty(n.filename))
                .ToList();
            if (nodes.Count == 0) return 0;

            int downloaded = 0;
            foreach (var node in nodes)
            {
                if (visited.Contains(node.part_number)) continue;

                string target = Path.Combine(assemblyDir, node.filename);
                ApiClient.PartMasterRevisionDto childRev = null;
                bool needsDownload = !File.Exists(target);

                // Look up the child's full revision so we can both (a) pull
                // the native file if missing on disk, and (b) recurse into
                // its own tree.json if it's an assembly.
                childRev = await ResolveLatestRevisionAsync(api, node.part_number);

                if (needsDownload && !string.IsNullOrEmpty(childRev?.EffectiveRefs?.Native3dRaw))
                {
                    try
                    {
                        string url = await api.GetS3DownloadUrlAsync(childRev.EffectiveRefs.Native3dRaw);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        await api.DownloadFileAsync(url, target);
                        downloaded++;
                    }
                    catch { /* per-child failure non-fatal */ }
                }

                // Recurse: if this child has its own manifest, pre-download
                // its grandchildren into the SAME assemblyDir. CATIA's
                // "same folder as parent" lookup catches everything in one
                // flat directory regardless of depth.
                if (childRev?.EffectiveRefs?.TreeJson != null)
                {
                    int nested = await PreflightRevisionAsync(api, childRev, assemblyDir, visited, depth + 1);
                    downloaded += nested;
                }
            }
            return downloaded;
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
