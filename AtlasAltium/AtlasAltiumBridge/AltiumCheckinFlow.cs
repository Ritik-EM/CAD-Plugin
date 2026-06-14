using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AtlasCadCore.ApiClient;
using AtlasCadCore.Utility;
using Newtonsoft.Json;

namespace AtlasCadPlugin.Altium
{
    /// <summary>
    /// The Altium analog of AtlasCadCore CheckinFlow, but for the ECAD single-part model:
    /// the whole project is ONE Atlas part_master (the part code). The .PrjPcb is the
    /// canonical native, the whole-board STEP fills the step slot, and every other file
    /// (schematics, PCB, libraries, BOM, PDF, Gerbers) is a companion under the same part.
    ///
    /// It deliberately calls api.CheckinAsync / api.UploadPartMasterAsync DIRECTLY rather
    /// than reusing CheckinFlow.RunAsync(api, adapter): those flows are MCAD-shaped (per-part
    /// STEP export, recursive assembly walk, 10-char-part-number tree validation on every
    /// node, checkout-tracking gate) and would fight Altium's flat one-part model. What we
    /// reuse is the genuinely valuable, proven machinery: AtlasApiClient's WAF-safe S3
    /// staging, the tree/companion contract, FileHashing, PartNumberParser.
    /// </summary>
    public static class AltiumCheckinFlow
    {
        public static async Task<AltiumResult> RunAsync(AtlasApiClient api, AltiumManifest m, string exchangeDir)
        {
            var result = new AltiumResult
            {
                operation = m.operation,
                part_code = m.part_code,
                warnings = m.warnings ?? new List<string>(),
            };

            if (string.IsNullOrWhiteSpace(m.part_code))
                throw new Exception("Manifest has no part_code; bind one to the project first.");

            // Normalize: deserialization can leave these null if the JSON had explicit nulls.
            m.source_files = m.source_files ?? new List<ManifestFile>();
            m.artifacts = m.artifacts ?? new List<ManifestArtifact>();

            // REQ 2: Altium generates OutJob outputs ASYNCHRONOUSLY, so the in-Altium script
            // can't harvest them in time. The script hands us the folders to scan; we (a
            // separate process) wait for generation to finish and harvest the artifacts here.
            if (m.artifacts.Count == 0 && m.artifact_scan_dirs != null && m.artifact_scan_dirs.Count > 0)
                m.artifacts = HarvestArtifactsWaiting(m.artifact_scan_dirs);

            // --- locate the canonical native (.PrjPcb) ---
            var projectFile = m.source_files
                .FirstOrDefault(f => string.Equals(f.role, "project", StringComparison.OrdinalIgnoreCase));
            if (projectFile == null || string.IsNullOrEmpty(projectFile.path) || !File.Exists(projectFile.path))
                throw new Exception("Manifest has no existing project (.PrjPcb) file to upload.");

            string nativePath = projectFile.path;
            string nativeFilename = Path.GetFileName(nativePath);

            // --- whole-board STEP -> step slot ---
            var stepArtifact = m.artifacts
                .FirstOrDefault(a => string.Equals(a.kind, "step", StringComparison.OrdinalIgnoreCase)
                                     && !string.IsNullOrEmpty(a.path) && File.Exists(a.path));
            string stepPath = stepArtifact?.path;
            string stepFilename = stepPath != null ? Path.GetFileName(stepPath) : null;

            // --- companions: every other bundled source file + every other artifact ---
            var companionPaths = new List<string>();
            var companionFilenames = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { nativeFilename };
            if (stepFilename != null) seen.Add(stepFilename);

            void AddCompanion(string path)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                string name = Path.GetFileName(path);
                if (!seen.Add(name)) return;   // dedupe by filename (Atlas stores by name under the part prefix)
                companionPaths.Add(path);
                companionFilenames.Add(name);
            }

            foreach (var f in m.source_files)
            {
                if (ReferenceEquals(f, projectFile)) continue;
                // Only file-based libs/docs are bundled. managed/database were already warned upstream.
                if (!string.Equals(f.bucket, "file", StringComparison.OrdinalIgnoreCase)) continue;
                AddCompanion(f.path);
            }
            foreach (var a in m.artifacts)
            {
                if (ReferenceEquals(a, stepArtifact)) continue;
                AddCompanion(a.path);
            }

            // --- per-part tree manifest (lists every filename under this part) ---
            var allFilenames = new List<string> { nativeFilename };
            if (stepFilename != null) allFilenames.Add(stepFilename);
            allFilenames.AddRange(companionFilenames);

            string treeFilename = m.part_code + ".tree.json";
            string treePath = Path.Combine(exchangeDir, treeFilename);
            var treeManifest = new
            {
                version = 1,
                root_part_number = m.part_code,
                root_filename = nativeFilename,
                nodes = new[]
                {
                    new
                    {
                        part_number = m.part_code,
                        filename = nativeFilename,
                        filenames = allFilenames,
                        parent_part_number = (string)null,
                    }
                }
            };
            File.WriteAllText(treePath, JsonConvert.SerializeObject(treeManifest, Formatting.Indented));

            string sha = FileHashing.Sha256Hex(nativePath);

            // --- file set to stage (native + step + companions + the tree manifest) ---
            var filePaths = new List<string> { nativePath };
            if (stepPath != null) filePaths.Add(stepPath);
            filePaths.AddRange(companionPaths);
            filePaths.Add(treePath);

            string releaseType = PartNumberParser.ReleaseTypeFromPartNumber(m.part_code) ?? "PROTO";

            if (string.Equals(m.operation, "upload", StringComparison.OrdinalIgnoreCase))
            {
                // First-time sync: create the part_master entry (no revision bump).
                var node = new
                {
                    part_number = m.part_code,
                    filename = nativeFilename,
                    step_filename = stepFilename,
                    tree_filename = treeFilename,
                    companion_filenames = companionFilenames,
                    detected_description = m.project_name,
                };
                var up = await api.UploadPartMasterAsync(new object[] { node }, filePaths, inlineTree: true);
                int attached = up?.attached?.Count ?? 0;
                int missing = up?.missing_parts?.Count ?? 0;
                int already = up?.already_present?.Count ?? 0;
                result.ok = attached > 0 || (up?.new_revisions?.Count ?? 0) > 0;
                result.message = $"Upload: {attached} attached, {missing} missing, {already} already present. " +
                                 (already > 0 ? "Use Check In to revise an existing part." : "");
                return result;
            }
            else
            {
                // Check-in = new revision of an EXISTING part. The backend requires the part to
                // be checked out (locked) first — otherwise it returns resp_code 1001
                // "Part X is not checked out". Acquire the lock transparently, then check in
                // (which bumps the revision AND releases the lock), so "check-in" is self-contained.
                await EnsureCheckedOutAsync(api, m.part_code, result);

                var node = new
                {
                    part_number = m.part_code,
                    parent_part_number = (string)null,
                    filename = nativeFilename,
                    step_filename = stepFilename,
                    tree_filename = treeFilename,
                    companion_filenames = companionFilenames,
                    sha256 = sha,
                };
                var changed = new List<string> { m.part_code };
                // Altium's tree is a single node (~hundreds of bytes), far under the 8 KB WAF
                // limit, so send it INLINE. This makes check-in work against a backend that
                // doesn't yet read the S3 staging manifest (atlas-api's _load_tree_from_staging
                // is still undeployed). Once atlas-api ships that, inlineTree can drop to false.
                var ci = await api.CheckinAsync(
                    rootPartNumber: m.part_code,
                    tree: new object[] { node },
                    releaseType: releaseType,
                    changed: changed,
                    comment: m.comment,
                    otp: null,
                    filePaths: filePaths,
                    inlineTree: true);

                if (ci?.bumped != null)
                {
                    result.bumped = ci.bumped.Select(b => $"{b.old_part_number} -> {b.new_part_number}").ToList();
                    // The root's new revision — written to current_part_code.txt so the next
                    // Altium check-in advances the project's AtlasPartCode (carry-forward).
                    var rootBump = ci.bumped.FirstOrDefault(b =>
                        string.Equals(b.old_part_number, m.part_code, StringComparison.OrdinalIgnoreCase));
                    result.new_root_part_number = rootBump?.new_part_number;
                }
                result.ok = true;
                result.message = $"Checked in {m.part_code} ({releaseType}); {result.bumped.Count} revision bump(s).";
                return result;
            }
        }

        /// <summary>
        /// Ensure the part is checked out by the current user before check-in. If the checkout
        /// call fails, proceed only if we already hold the lock (idempotent re-run); otherwise
        /// abort with a clear message (e.g. the part is locked by someone else).
        /// </summary>
        private static async Task EnsureCheckedOutAsync(AtlasApiClient api, string partCode, AltiumResult result)
        {
            try
            {
                await api.CheckoutPartMasterAsync(partCode);
                return;
            }
            catch (Exception ex)
            {
                bool weAlreadyHoldIt = false;
                try
                {
                    var mine = await api.MyCheckoutsAsync();
                    if (mine?.checkouts != null)
                        weAlreadyHoldIt = mine.checkouts.Any(c =>
                            string.Equals(c.part_number, partCode, StringComparison.OrdinalIgnoreCase));
                }
                catch { /* lookup failed; fall through to abort with the original error */ }

                if (!weAlreadyHoldIt)
                    throw new Exception($"Cannot check out {partCode} before check-in: {ex.Message}");

                result.warnings.Add($"{partCode} was already checked out by you; proceeding with check-in.");
            }
        }

        // ---- REQ 2: artifact harvest (Altium generates outputs asynchronously) ----

        /// <summary>
        /// Wait for Altium's async OutJob generation to finish, then return the artifacts found
        /// under the given dirs. We poll until the set of artifact files is stable (or a timeout);
        /// if nothing appears within a short grace window we give up (e.g. no outputs enabled).
        /// Runs in this separate process, so waiting never blocks Altium's generation.
        /// </summary>
        private static List<ManifestArtifact> HarvestArtifactsWaiting(List<string> dirs)
        {
            var start = DateTime.UtcNow;
            var maxWait = TimeSpan.FromMinutes(4);          // generous for a big-board STEP
            var firstFileGrace = TimeSpan.FromSeconds(90);  // give up if nothing ever appears
            int prevCount = -1, stable = 0;
            var found = ScanForArtifacts(dirs);

            while (DateTime.UtcNow - start < maxWait)
            {
                found = ScanForArtifacts(dirs);
                if (found.Count > 0)
                {
                    if (found.Count == prevCount) { stable++; if (stable >= 2) break; }
                    else { stable = 0; prevCount = found.Count; }
                }
                else if (DateTime.UtcNow - start > firstFileGrace)
                {
                    break;   // nothing generated (outputs disabled / none) — stop waiting
                }
                System.Threading.Thread.Sleep(3000);
            }

            var list = new List<ManifestArtifact>();
            foreach (var kv in found)
                list.Add(new ManifestArtifact { path = kv.Key, kind = kv.Value });
            return list;
        }

        /// <summary>Recursively scan dirs and classify files by extension (path -> kind).</summary>
        private static Dictionary<string, string> ScanForArtifacts(IEnumerable<string> dirs)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in dirs ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories); }
                catch { continue; }
                foreach (var f in files)
                {
                    var kind = ArtifactKind(f);
                    if (kind != null && !result.ContainsKey(f)) result[f] = kind;
                }
            }
            return result;
        }

        /// <summary>Classify a produced file by extension. Only "step" gets special handling
        /// downstream (→ 3d slot); everything else rides along as a companion.</summary>
        private static string ArtifactKind(string path)
        {
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();  // includes the dot
            if (ext == ".csv" || ext == ".xls" || ext == ".xlsx") return "bom";
            if (ext == ".pdf") return "pdf";
            if (ext == ".step" || ext == ".stp") return "step";
            if (ext == ".txt") return "gerber";                  // NC drill is often .txt
            if (ext.Length >= 3 && ext[1] == 'g') return "gerber"; // .gtl/.gbl/.gts/...
            return null;
        }
    }
}
