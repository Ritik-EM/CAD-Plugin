using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AtlasCadCore.ApiClient;
using Newtonsoft.Json;

namespace AtlasCadCore.Forms
{
    /// <summary>
    /// P7.49: Pre-download children of an assembly from its tree.json
    /// manifest before the CAD app opens the parent. Skips the broken-links
    /// dialog entirely for assemblies uploaded after P7.48. Returns the
    /// number of child files actually downloaded; 0 means either the
    /// manifest was absent (legacy assembly) or every child file was
    /// already present on disk.
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
        /// Walks the rootPart's tree.json (if any) and downloads each child
        /// node's native file to assemblyDir + filename. Existing files are
        /// left alone — checksum validation is not done here; callers that
        /// care should hash after download.
        /// </summary>
        public static async Task<int> PreflightAsync(
            AtlasApiClient api,
            ApiClient.PartMasterRevisionDto rootRev,
            string assemblyDir)
        {
            if (rootRev?.EffectiveRefs?.TreeJson == null) return 0;
            if (string.IsNullOrEmpty(assemblyDir)) return 0;

            var manifest = await api.DownloadJsonByS3KeyAsync<TreeManifest>(rootRev.EffectiveRefs.TreeJson);
            if (manifest?.nodes == null || manifest.nodes.Count == 0) return 0;

            // Collect part_numbers we need so we can resolve them in one
            // ListPartMaster pass instead of N calls.
            var nodes = manifest.nodes
                .Where(n => !string.IsNullOrEmpty(n.part_number) && !string.IsNullOrEmpty(n.filename))
                .ToList();
            if (nodes.Count == 0) return 0;

            int downloaded = 0;
            foreach (var node in nodes)
            {
                string target = Path.Combine(assemblyDir, node.filename);
                if (File.Exists(target)) continue;

                string nativeKey = await ResolveNativeS3KeyAsync(api, node.part_number);
                if (string.IsNullOrEmpty(nativeKey)) continue; // child has no native — skip silently

                try
                {
                    string url = await api.GetS3DownloadUrlAsync(nativeKey);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    await api.DownloadFileAsync(url, target);
                    downloaded++;
                }
                catch { /* per-child failure is non-fatal — the Resolve flow
                         catches anything we miss after the CAD opens. */ }
            }
            return downloaded;
        }

        private static async Task<string> ResolveNativeS3KeyAsync(AtlasApiClient api, string partNumber)
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
                            if (!string.Equals(rev?.part_number, partNumber, StringComparison.OrdinalIgnoreCase))
                                continue;
                            var refs = rev.EffectiveRefs;
                            if (refs == null) continue;
                            // Prefer native; fall back to step (caller will
                            // convert) only if native is genuinely missing.
                            if (!string.IsNullOrEmpty(refs.Native3dRaw)) return refs.Native3dRaw;
                        }
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
