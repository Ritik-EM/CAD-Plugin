using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AtlasCadCore.Adapter;
using AtlasCadCore.Utility;
using INFITF;
using MECMOD;
using ProductStructureTypeLib;
using KnowledgewareTypeLib;

namespace AtlasCadPlugin.Catia
{
    public class CatiaAdapter : ICadAdapter, IRevisionDisplayAdapter
    {
        public const string WalkAssemblyVersion = "2026-06-06-revision-display-v1";

        private readonly Application _catApp;

        public CatiaAdapter(Application catApp)
        {
            _catApp = catApp;
        }

        public string CadName => "CATIA V5";

        public IReadOnlyList<string> NativeFileExtensions => new[] { ".catproduct", ".catpart" };

        public CadDocument GetActiveDocument()
        {
            Document doc;
            try { doc = _catApp.ActiveDocument; }
            catch { return null; }
            if (doc == null) return null;

            string fullPath = doc.FullName;
            string ext = Path.GetExtension(fullPath).ToLowerInvariant();
            return new CadDocument
            {
                FullPath = fullPath,
                Name = Path.GetFileName(fullPath),
                IsAssembly = ext == ".catproduct",
                NativeHandle = doc,
            };
        }

        public void SaveDocument(CadDocument doc)
        {
            // Save EVERY modified open document, not just the active one.
            // CATIA's Document.Save() does NOT cascade to modified children: an
            // in-context edit (e.g. changing a part's geometry or colour while
            // the assembly is active) leaves the child .CATPart dirty IN THE
            // SESSION but unsaved ON DISK. The walk then hashes the OLD file on
            // disk and the change is silently lost (no diff → no revision bump →
            // checkout shows the pre-edit part). Saving all dirty docs first
            // guarantees every edit is on disk before we walk/upload.
            //
            // Order doesn't matter for the final on-disk state — a CATProduct
            // stores child links by path, not embedded content, so the product
            // and its parts each persist their own data independently.
            string activePath = null;
            try { activePath = ((Document)doc?.NativeHandle)?.FullName; } catch { }
            LogWalk($"SaveDocument: saving all modified docs (active='{activePath}')");

            int saved = 0, total = 0;
            try
            {
                total = _catApp.Documents.Count;
                for (int i = 1; i <= total; i++)
                {
                    Document d;
                    try { d = _catApp.Documents.Item(i); }
                    catch (Exception ex) { LogWalk($"  doc[{i}] Item() threw: {ex.Message}"); continue; }
                    if (SaveOneIfModified(d)) saved++;
                }
            }
            catch (Exception ex) { LogWalk($"SaveDocument: Documents iterate threw: {ex.Message}"); }

            // Belt-and-braces: make sure the active doc is saved even if the
            // collection enumeration somehow skipped it (already-saved → no-op).
            try { if (SaveOneIfModified((Document)doc?.NativeHandle)) saved++; } catch { }

            LogWalk($"SaveDocument: saved {saved} modified doc(s) of {total} open");
        }

        // Saves one document iff it's modified and has a path on disk.
        // Best-effort: CATIA throws E_UNEXPECTED when there's nothing to save or
        // the doc was never saved to disk, so every call is guarded.
        private static bool SaveOneIfModified(Document d)
        {
            if (d == null) return false;

            bool needsSave = true;
            try { needsSave = !d.Saved; }
            catch { /* Saved unsupported on some doc types — attempt save anyway */ }
            if (!needsSave) return false;

            string full = null;
            try { full = d.FullName; } catch { }
            if (string.IsNullOrEmpty(full))
            {
                // Never saved to disk yet — SaveAs is the user's responsibility.
                LogWalk($"  skip modified doc with no path on disk");
                return false;
            }

            try
            {
                d.Save();
                LogWalk($"  saved '{Path.GetFileName(full)}'");
                return true;
            }
            catch (Exception ex)
            {
                LogWalk($"  save FAILED for '{Path.GetFileName(full)}': {ex.Message}");
                return false;
            }
        }

        public void OpenDocument(string filePath)
        {
            _catApp.Documents.Open(filePath);
        }

        // ---- WalkAssembly with skip-reasons + diagnostic log ----
        // Mirrors SolidWorksAdapter.WalkAssembly's contract: returns one
        // AssemblyFileRef per node in the tree, including suppressed/broken
        // ones with a populated SkipReason. The shared upload form filters
        // those out by `string.IsNullOrEmpty(n.SkipReason)`.

        public List<AssemblyFileRef> WalkAssembly(CadDocument doc)
        {
            LogWalk($"WalkAssembly v={WalkAssemblyVersion} invoked");

            var catDoc = (Document)doc.NativeHandle ??
                         throw new InvalidOperationException("No active document");

            string rootPath = catDoc.FullName;
            if (string.IsNullOrEmpty(rootPath))
                throw new InvalidOperationException(
                    "Assembly has not been saved to disk yet — save it first");

            string ext = Path.GetExtension(rootPath).ToLowerInvariant();
            if (ext != ".catproduct")
                throw new InvalidOperationException("Active document is not a CATProduct assembly");

            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<AssemblyFileRef>();

            string rootFilename = Path.GetFileName(rootPath);
            string rootPn = ResolvePartNumber(catDoc, rootFilename);
            result.Add(new AssemblyFileRef
            {
                FullPath = rootPath,
                RelativePath = rootFilename,
                Filename = rootFilename,
                IsRoot = true,
                PartNumber = rootPn,
                ParentPartNumber = null,
                NativeHandle = catDoc,
            });
            seenPaths.Add(rootPath);

            var productDoc = (ProductDocument)catDoc;

            // CRITICAL: force the whole tree into Design Mode first. When CATIA's
            // cache system / Visualization Mode is on, leaf CATParts are loaded
            // only as lightweight .cgr representations — there is no real part
            // Document in the session, so Product.ReferenceProduct throws E_FAIL
            // and we can't resolve their file paths. That made every leaf part
            // come back path='' → skipped from the upload → "file not found" on
            // checkout. Design Mode loads the real parts so paths resolve.
            EnsureDesignMode(productDoc.Product);

            WalkProductTree(productDoc.Product, rootPn, rootPath, result, seenPaths,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            return result;
        }

        // Loads every component of the tree as a real (design-mode) Document.
        // Idempotent and best-effort: if a component's file is genuinely missing
        // it simply stays unresolved and is reported as missing downstream.
        //
        // Invoked entirely by reflection — same philosophy as the version-
        // agnostic accessors below. ApplyWorkMode is resolved by name (so a
        // differing wrapper shape can't break the build) and DESIGN_MODE is read
        // by name from whichever ProductStructureTypeLib interop is loaded (so
        // the enum's underlying int value, which varies by CATIA release, never
        // has to be hardcoded).
        private static void EnsureDesignMode(Product root)
        {
            if (root == null) return;
            try
            {
                object designMode = ResolveWorkModeValue("DESIGN_MODE");
                if (designMode == null)
                {
                    LogWalk("EnsureDesignMode: could not resolve CatWorkModeType.DESIGN_MODE");
                    return;
                }
                int designModeInt = Convert.ToInt32(designMode);
                root.GetType().InvokeMember(
                    "ApplyWorkMode",
                    BindingFlags.InvokeMethod | BindingFlags.Instance | BindingFlags.Public,
                    null, root, new object[] { designModeInt });
                LogWalk($"EnsureDesignMode: ApplyWorkMode(DESIGN_MODE={designModeInt}) applied");
            }
            catch (Exception ex)
            {
                LogWalk($"EnsureDesignMode: ApplyWorkMode threw: {ex.Message}");
            }
        }

        private static object ResolveWorkModeValue(string memberName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType("ProductStructureTypeLib.CatWorkModeType"); }
                catch { }
                if (t == null) { try { t = asm.GetType("CatWorkModeType"); } catch { } }
                if (t != null && t.IsEnum)
                {
                    try { return Enum.Parse(t, memberName); } catch { }
                }
            }
            return null;
        }

        // Walks the Product tree depth-first. The KEY parity fix vs. the old
        // implementation: recursion into sub-assemblies is driven by STRUCTURE
        // (does this node aggregate child Products?), NOT by whether we managed
        // to resolve a ".CATProduct" path for it. Previously a sub-assembly
        // whose backing path couldn't be resolved — or whose filename didn't
        // parse to a part-number — was tagged with a SkipReason, and that both
        // dropped it from the upload AND suppressed recursion, so its entire
        // subtree of children went missing. SolidWorks never hit this because
        // it decides "recurse?" from the loaded doc's type (swDocASSEMBLY). We
        // now mirror that: any node with children is walked, regardless of its
        // own skip status (suppressed nodes excepted).
        //
        // We also recurse on the child INSTANCE (`child.Products`) rather than
        // `child.ReferenceProduct.Products`. Both expose the same components,
        // but the instance path doesn't depend on ReferenceProduct resolving,
        // which can fail on some CATIA builds.
        //
        // `containerPath` is the .CATProduct file that physically holds the
        // `parent` Product. It's how we recognise an EMBEDDED sub-assembly:
        // a node that aggregates children but has no file of its own — CATIA
        // stores its definition inside the containing .CATProduct, so
        // TryGetChildPath resolves it to `containerPath` (the assembly we're
        // already inside), NOT to a standalone file. Such nodes used to be
        // dropped by the path-dedup (their path collided with the root, which
        // is pre-seeded into seenPaths), taking their entire subtree with
        // them — every embedded sub-assembly and its children vanished from
        // the manifest. We now detect them, dedup them by part_number (their
        // container path is identical, so path-dedup can't tell two instances
        // apart), keep them as file-less structural tree nodes, and recurse.
        private void WalkProductTree(Product parent, string parentPn, string containerPath,
                                      List<AssemblyFileRef> output, HashSet<string> seenPaths,
                                      HashSet<string> seenEmbeddedPns)
        {
            Products children;
            try { children = parent.Products; }
            catch (Exception ex) { LogWalk($"  parent.Products threw: {ex.Message}"); return; }
            if (children == null) { LogWalk("  parent.Products == null"); return; }

            int count = 0;
            try { count = children.Count; }
            catch (Exception ex) { LogWalk($"  children.Count threw: {ex.Message}"); }
            LogWalk($"  parent PN='{parentPn}' has {count} immediate child node(s)");

            for (int i = 1; i <= count; i++)
            {
                Product child;
                try { child = children.Item(i); }
                catch (Exception ex) { LogWalk($"  child[{i}] Item() threw: {ex.Message}"); continue; }
                if (child == null) { LogWalk($"  child[{i}] == null"); continue; }

                string fullPath = TryGetChildPath(child);
                string childFilename = string.IsNullOrEmpty(fullPath) ? null : Path.GetFileName(fullPath);
                bool hasChildren = ProductHasChildren(child);

                // Per-node diagnostics — mirrors CollectMissingChildren so the
                // upload path is no longer a black box when children go missing.
                string cName = null, cPn = null, cDesc = null;
                try { cName = ProductName(child); } catch { }
                try { cPn = ProductPartNumber(child); } catch { }
                try { cDesc = ProductDescriptionInst(child); } catch { }
                LogWalk($"  child[{i}]: Name='{cName}' PN='{cPn}' DescInst='{cDesc}' " +
                        $"path='{fullPath}' hasChildren={hasChildren}");

                // ---- Embedded sub-assembly (no file of its own) ----
                // Resolves to the containing .CATProduct rather than a
                // standalone file. Must NOT go through the path-dedup below.
                bool embedded = hasChildren
                                && !string.IsNullOrEmpty(fullPath)
                                && string.Equals(fullPath, containerPath, StringComparison.OrdinalIgnoreCase);
                if (embedded)
                {
                    // The container's filename would parse to the PARENT's
                    // part_number, so take the embedded node's own part_number
                    // straight from the Product (CATIA's PartNumber property).
                    string embeddedPn = PartNumberParser.ParseOrNull(cPn);
                    bool? embActivated = TryReadBoolProp(child, "IsActivated");

                    if (string.IsNullOrEmpty(embeddedPn))
                    {
                        // No usable part_number: record a skip node for
                        // diagnostics but still recurse so its file-backed
                        // descendants aren't lost (best-effort: they hang off
                        // the nearest resolvable ancestor).
                        LogWalk($"  child[{i}]: embedded sub-assembly, no part_number — recursing under '{parentPn}'");
                        output.Add(new AssemblyFileRef
                        {
                            FullPath = null,
                            RelativePath = cName,
                            Filename = null,
                            IsRoot = false,
                            PartNumber = null,
                            ParentPartNumber = parentPn,
                            NativeHandle = null,
                            SkipReason = "no-part-number",
                        });
                        if (embActivated != false)
                            WalkProductTree(child, parentPn, containerPath, output, seenPaths, seenEmbeddedPns);
                        continue;
                    }

                    // Dedup repeated instances of the same embedded sub-
                    // assembly by part_number (path-dedup can't — they share
                    // the container path). First instance wins and is walked.
                    if (!seenEmbeddedPns.Add(embeddedPn))
                    {
                        LogWalk($"  child[{i}]: embedded '{embeddedPn}' already walked — skipped");
                        continue;
                    }

                    // Structural node: SkipReason stays null so it survives
                    // into `native` (gets a part_master + its own tree.json),
                    // but FullPath is null so the upload ships no native/step
                    // for it — there is no file to ship.
                    LogWalk($"  child[{i}]: embedded sub-assembly -> resolvedPN='{embeddedPn}'");
                    output.Add(new AssemblyFileRef
                    {
                        FullPath = null,
                        RelativePath = cName,
                        Filename = null,
                        IsRoot = false,
                        PartNumber = embeddedPn,
                        ParentPartNumber = parentPn,
                        NativeHandle = null,
                        SkipReason = null,
                    });

                    if (embActivated == false)
                    {
                        LogWalk($"  child[{i}]: embedded '{embeddedPn}' suppressed — not recursing");
                        continue;
                    }
                    LogWalk($"  child[{i}]: recursing into embedded sub-assembly '{embeddedPn}'");
                    // containerPath unchanged: the embedded node lives in the
                    // same .CATProduct, so its own embedded children resolve
                    // to that same container path too.
                    WalkProductTree(child, embeddedPn, containerPath, output, seenPaths, seenEmbeddedPns);
                    continue;
                }

                // ---- File-backed node (standalone .CATPart / .CATProduct) ----
                // Suppression detection: V5R21 doesn't expose IsActivated on
                // Product so the reflection helper returns null there → we
                // skip the check. V5R2025+ exposes it → suppressed components
                // get tagged explicitly.
                string skipReason = null;
                bool? activated = TryReadBoolProp(child, "IsActivated");
                if (activated == false) skipReason = "suppressed";
                else if (string.IsNullOrEmpty(fullPath)) skipReason = "no-path";
                else if (!System.IO.File.Exists(fullPath)) skipReason = "missing-file";

                // Dedup on the resolved path so a part reused across the tree is
                // uploaded once. Path-less nodes (skipReason already set) bypass
                // the dedup — a null key would otherwise collapse every path-
                // less node into one. We add to seenPaths for ANY resolved path
                // (even skipped ones) so the same file is never re-walked.
                bool duplicate = false;
                if (!string.IsNullOrEmpty(fullPath) && !seenPaths.Add(fullPath))
                    duplicate = true;

                if (duplicate)
                {
                    LogWalk($"  child[{i}]: duplicate of already-walked '{fullPath}' — skipped");
                    continue;
                }

                // Resolve part_number from the reference Document if it's
                // already loaded; don't force-open just to read it.
                Document refDoc = TryGetLoadedReferenceDoc(child);
                string childPn = ResolvePartNumber(refDoc, childFilename);
                if (skipReason == null && string.IsNullOrEmpty(childPn))
                    skipReason = "no-part-number";

                LogWalk($"  child[{i}]: -> resolvedPN='{childPn}' skipReason='{skipReason ?? "(none)"}' " +
                        $"fileExists={(!string.IsNullOrEmpty(fullPath) && System.IO.File.Exists(fullPath))}");

                string displayName = childFilename;
                if (string.IsNullOrEmpty(displayName))
                {
                    displayName = cName;
                    if (string.IsNullOrEmpty(displayName)) displayName = "(unnamed component)";
                }

                output.Add(new AssemblyFileRef
                {
                    FullPath = fullPath,
                    RelativePath = displayName,
                    Filename = displayName,
                    IsRoot = false,
                    PartNumber = childPn,
                    ParentPartNumber = parentPn,
                    NativeHandle = refDoc,
                    SkipReason = skipReason,
                });

                // Recurse into ANY node that aggregates children (a sub-
                // assembly), regardless of its own skip status — except
                // suppressed nodes, which the user deliberately turned off.
                // This is the fix for "sub-assembly children not extracted".
                // A file-backed sub-assembly becomes the container for its own
                // children, so pass its path down as the new containerPath.
                if (hasChildren && skipReason != "suppressed")
                {
                    LogWalk($"  child[{i}]: recursing into sub-assembly '{displayName}'");
                    WalkProductTree(child, childPn, fullPath, output, seenPaths, seenEmbeddedPns);
                }
            }
        }

        // A Product node is a sub-assembly iff it aggregates child Products.
        // Leaf CATParts return 0. Robust across CATIA versions — no dependency
        // on ReferenceProduct or file-path resolution, which is exactly why we
        // use it to gate recursion instead of the old extension check.
        private static bool ProductHasChildren(Product p)
        {
            try
            {
                Products kids = p.Products;
                return kids != null && kids.Count > 0;
            }
            catch { return false; }
        }

        private static string TryGetChildPath(Product child)
        {
            // CATIA exposes the source filepath several ways depending on
            // whether the referenced doc is loaded. For LOADED docs:
            //   Product.ReferenceProduct.Parent (Document).FullName
            // For BROKEN/missing docs (file gone from disk), Parent is null
            // — in that case we fall back to extracting the filename from
            // Product.Name or Product.PartNumber so the caller still gets a
            // useful key (even if it's just "C-282088-1-F-3D.CATPart" with
            // no directory).
            try
            {
                Product refProd = child.ReferenceProduct;
                if (refProd != null)
                {
                    object parentObj = refProd.Parent;
                    Document parentDoc = parentObj as Document;
                    if (parentDoc != null)
                    {
                        string full = parentDoc.FullName;
                        if (!string.IsNullOrEmpty(full)) return full;
                    }
                }
            }
            catch { }

            return TryGuessFilename(child);
        }

        /// <summary>
        /// Best-effort filename inference for a broken/unloaded Product node.
        /// V5R21 exposes the source filename's stem via DescriptionInst when
        /// the referenced doc failed to load (e.g. "C-282088-1-F-3D"). For
        /// loaded children DescriptionInst is also populated but the primary
        /// path comes from ReferenceProduct.Parent.FullName — this is only
        /// hit as a fallback. Returns a bare filename (no directory); the
        /// caller resolves it against the parent assembly's folder.
        /// </summary>
        private static string TryGuessFilename(Product child)
        {
            // Strategy A (V5R21-confirmed): DescriptionInst holds the source
            // filename's stem for broken refs. CATPart is the overwhelmingly
            // common extension; if the user has broken sub-assemblies we'd
            // miss them with this assumption — accept that for now since we
            // can't tell parts from products from the API.
            try
            {
                string desc = ProductDescriptionInst(child);
                if (!string.IsNullOrEmpty(desc))
                {
                    string ext = Path.GetExtension(desc).ToLowerInvariant();
                    if (ext == ".catpart" || ext == ".catproduct" ||
                        ext == ".stp" || ext == ".step" || ext == ".model")
                        return desc;
                    return desc + ".CATPart";
                }
            }
            catch { }

            // Strategy B: scan Product.Name for an embedded filename — rare
            // but happens when the user manually re-named a broken node.
            try
            {
                string name = ProductName(child);
                if (!string.IsNullOrEmpty(name))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(
                        name,
                        @"([^\\/\[\]\s]+\.(?:CATPart|CATProduct|stp|step|model))",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success) return m.Groups[1].Value;
                }
            }
            catch { }

            // Strategy C: PartNumber as basename — for broken children in
            // V5R21 PartNumber is usually empty, but try anyway.
            try
            {
                string pn = ProductPartNumber(child);
                if (!string.IsNullOrEmpty(pn))
                    return pn + ".CATPart";
            }
            catch { }

            return null;
        }

        private static Document TryGetLoadedReferenceDoc(Product child)
        {
            try
            {
                Product refProd = child.ReferenceProduct;
                if (refProd == null) return null;
                return refProd.Parent as Document;
            }
            catch { return null; }
        }

        public List<AssemblyFileRef> ExportStep(
            CadDocument doc,
            IEnumerable<AssemblyFileRef> nativeFiles,
            string stagingDir,
            Action<int, int, string> progress = null)
        {
            Directory.CreateDirectory(stagingDir);
            var result = new List<AssemblyFileRef>();
            var inputs = new List<AssemblyFileRef>(nativeFiles);

            for (int i = 0; i < inputs.Count; i++)
            {
                var f = inputs[i];
                // Skip file-less structural nodes (embedded sub-assemblies that
                // live inside the parent .CATProduct) — there's no native to
                // export a STEP from.
                if (string.IsNullOrEmpty(f.Filename) || string.IsNullOrEmpty(f.FullPath)) continue;
                string ext = Path.GetExtension(f.Filename).ToLowerInvariant();
                if (ext != ".catpart" && ext != ".catproduct") continue;
                progress?.Invoke(i + 1, inputs.Count, f.Filename);

                string stepName = Path.GetFileNameWithoutExtension(f.Filename) + ".stp";
                string stepPath = Path.Combine(stagingDir, stepName);

                Document srcDoc = f.NativeHandle as Document;
                bool opened = false;
                if (srcDoc == null)
                {
                    try { srcDoc = _catApp.Documents.Open(f.FullPath); opened = srcDoc != null; }
                    catch { continue; }
                }
                if (srcDoc == null) continue;

                try
                {
                    srcDoc.ExportData(stepPath, "stp");
                }
                catch { continue; }
                finally
                {
                    if (opened) { try { srcDoc.Close(); } catch { } }
                }

                if (!System.IO.File.Exists(stepPath)) continue;

                result.Add(new AssemblyFileRef
                {
                    FullPath = stepPath,
                    Filename = stepName,
                    RelativePath = stepName,
                    IsRoot = f.IsRoot,
                    PartNumber = f.PartNumber,
                });
            }
            return result;
        }

        public void InsertComponent(CadDocument activeAssembly, string filePath)
        {
            var productDoc = (ProductDocument)activeAssembly.NativeHandle;
            object filesArray = new object[] { filePath };
            productDoc.Product.Products.AddComponentsFromFiles((Array)filesArray, "*");
        }

        public string ImportStepAsNative(string stpPath, string nativeOutPathHint)
        {
            Document imported = _catApp.Documents.Open(stpPath);
            if (imported == null)
                throw new InvalidOperationException($"CATIA could not import STEP ({stpPath}).");

            // R21 generates Document.Name as get_Name()/set_Name(ref string)
            // — newer CATIA versions wrap it as a clean property accessor.
            string actualName = DocumentName(imported);
            string outPath = Path.Combine(
                Path.GetDirectoryName(nativeOutPathHint),
                Path.GetFileNameWithoutExtension(nativeOutPathHint) + Path.GetExtension(actualName));
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            try
            {
                imported.SaveAs(outPath);
            }
            finally
            {
                try { imported.Close(); } catch { }
            }
            if (!System.IO.File.Exists(outPath))
                throw new InvalidOperationException($"SaveAs '{outPath}' produced no file.");
            return outPath;
        }

        // ---- FindMissingComponents: drives "Resolve from Atlas" (P7.18) ----
        // Walks the product tree and returns any child whose recorded path
        // doesn't exist on disk. The shared ResolveFromAtlasFlow uses these
        // to fetch native files from atlas and place them where CATIA expects.
        public List<MissingComponent> FindMissingComponents(CadDocument assembly)
        {
            var result = new List<MissingComponent>();
            var catDoc = assembly?.NativeHandle as Document;
            if (catDoc == null) { LogResolve("FindMissingComponents: catDoc is null"); return result; }

            string assemblyDir = null;
            try { assemblyDir = Path.GetDirectoryName(catDoc.FullName); } catch { }
            LogResolve($"FindMissingComponents: assemblyDir={assemblyDir}");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Strategy 1: walk the Product tree (works when child refs are
            // exposed via Product.ReferenceProduct.Parent or via inferred
            // filenames in Product.Name).
            var productDoc = catDoc as ProductDocument;
            if (productDoc != null)
            {
                // Same Design-Mode load as WalkAssembly: without it, cache /
                // Visualization-mode leaf parts have no real Document and
                // Product.ReferenceProduct throws E_FAIL, so we can neither
                // resolve their paths nor tell present-on-disk from missing.
                EnsureDesignMode(productDoc.Product);
                CollectMissingChildren(productDoc.Product, assemblyDir, result, seen);
                LogResolve($"After Product tree walk: {result.Count} missing detected");
            }

            // Strategy 2: scan _catApp.Documents — V5R21 often creates a
            // Document entry with FullName set to the recorded path even
            // when the file failed to load. Anything in there whose path
            // is non-existent on disk is a missing ref we should resolve.
            try
            {
                int total = _catApp.Documents.Count;
                LogResolve($"_catApp.Documents.Count = {total}");
                for (int i = 1; i <= total; i++)
                {
                    Document doc;
                    try { doc = _catApp.Documents.Item(i); }
                    catch (Exception ex) { LogResolve($"  Documents[{i}] threw {ex.Message}"); continue; }
                    if (doc == null) continue;

                    string full = null;
                    string nm = null;
                    try { full = doc.FullName; } catch { }
                    try { nm = DocumentName(doc); } catch { }
                    LogResolve($"  Documents[{i}]: Name={nm} FullName={full}");

                    if (string.IsNullOrEmpty(full)) continue;
                    if (System.IO.File.Exists(full)) continue;
                    if (string.Equals(full, catDoc.FullName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Add(full)) continue;

                    string filename = Path.GetFileName(full);
                    result.Add(new MissingComponent
                    {
                        Filename = filename,
                        ExpectedPath = full,
                        PartNumber = PartNumberParser.ParseOrNull(filename),
                    });
                    LogResolve($"  -> added as missing: {filename}");
                }
            }
            catch (Exception ex) { LogResolve($"Documents scan threw: {ex.Message}"); }

            LogResolve($"FindMissingComponents result: {result.Count} entries");
            return result;
        }

        private static void LogResolve(string line)
        {
            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AtlasCad");
                Directory.CreateDirectory(logDir);
                System.IO.File.AppendAllText(
                    Path.Combine(logDir, "walk_assembly.log"),
                    $"--- {DateTime.Now:O} {line}\n");
            }
            catch { }
        }

        private static void CollectMissingChildren(
            Product parent, string assemblyDir,
            List<MissingComponent> output, HashSet<string> seen)
        {
            Products children;
            try { children = parent.Products; }
            catch (Exception ex) { LogResolve($"  parent.Products threw: {ex.Message}"); return; }
            if (children == null) { LogResolve("  parent.Products = null"); return; }

            int count = 0;
            try { count = children.Count; } catch (Exception ex) { LogResolve($"  children.Count threw: {ex.Message}"); }
            LogResolve($"  children.Count = {count}");

            for (int i = 1; i <= count; i++)
            {
                Product child;
                try { child = children.Item(i); }
                catch (Exception ex) { LogResolve($"  children.Item({i}) threw: {ex.Message}"); continue; }
                if (child == null) { LogResolve($"  children.Item({i}) = null"); continue; }

                string cName = null, cPn = null, cDesc = null, cNom = null;
                try { cName = ProductName(child); } catch { }
                try { cPn = ProductPartNumber(child); } catch { }
                try { cDesc = ProductDescriptionInst(child); } catch { }
                try { cNom = ProductNomenclature(child); } catch { }
                LogResolve($"  child[{i}]: Name='{cName}' PN='{cPn}' DescInst='{cDesc}' Nomenclature='{cNom}'");

                Product refProd = null;
                try { refProd = child.ReferenceProduct; } catch (Exception ex) { LogResolve($"    .ReferenceProduct threw: {ex.Message}"); }
                if (refProd != null)
                {
                    string rpName = null;
                    try { rpName = ProductName(refProd); } catch { }
                    Document rpParent = null;
                    try { rpParent = refProd.Parent as Document; } catch (Exception ex) { LogResolve($"    .ReferenceProduct.Parent threw: {ex.Message}"); }
                    string rpFull = null;
                    try { rpFull = rpParent?.FullName; } catch { }
                    LogResolve($"    ReferenceProduct.Name='{rpName}' Parent.FullName='{rpFull}'");
                }

                string path = TryGetChildPath(child);
                LogResolve($"    TryGetChildPath -> '{path}'");
                if (string.IsNullOrEmpty(path)) continue;

                // If TryGetChildPath gave us just a filename (no directory),
                // resolve it against the parent assembly's folder so we can
                // check existence and pass a real path to ResolveFromAtlasFlow.
                string fullPath = Path.IsPathRooted(path)
                    ? path
                    : (!string.IsNullOrEmpty(assemblyDir) ? Path.Combine(assemblyDir, path) : path);

                if (System.IO.File.Exists(fullPath)) continue;     // already resolved on disk
                if (!seen.Add(fullPath)) continue;                  // dedupe

                string filename = Path.GetFileName(fullPath);
                output.Add(new MissingComponent
                {
                    Filename = filename,
                    ExpectedPath = fullPath,
                    PartNumber = PartNumberParser.ParseOrNull(filename),
                });

                // Recurse into nested sub-assemblies that are loaded enough
                // to enumerate, even though their backing file is missing.
                if (Path.GetExtension(fullPath).Equals(".CATProduct", StringComparison.OrdinalIgnoreCase))
                {
                    Product nested = null;
                    try { nested = child.ReferenceProduct; } catch { }
                    if (nested != null) CollectMissingChildren(nested, assemblyDir, output, seen);
                }
            }
        }

        // ---- AddSearchFolder: tell CATIA where to look for referenced docs ----
        // CATIA's "Linked Documents Localization" setting takes a semicolon-
        // separated list of folders, exposed through the
        // SettingControllers infrastructure (`CATReffilesSettingCtrl`). We
        // append `folderPath` to whatever's already there.
        public void AddSearchFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return;
            try
            {
                var setting = _catApp.SettingControllers.Item("CATReffilesSettingCtrl");
                if (setting == null) return;

                // CATReffilesSettingCtrl exposes a string property
                // "LinkedDocumentsLocalization" (or analog) — names differ
                // across CATIA releases. We probe the common ones.
                string existing = TryReadSetting(setting, "LinkedDocumentsLocalization")
                                 ?? TryReadSetting(setting, "Folders")
                                 ?? "";

                foreach (string p in existing.Split(';'))
                {
                    if (string.Equals(p?.Trim().TrimEnd('\\'), folderPath.TrimEnd('\\'),
                                      StringComparison.OrdinalIgnoreCase))
                        return; // already registered
                }

                string updated = string.IsNullOrEmpty(existing)
                    ? folderPath
                    : existing + ";" + folderPath;

                if (!TryWriteSetting(setting, "LinkedDocumentsLocalization", updated))
                    TryWriteSetting(setting, "Folders", updated);

                try { setting.SaveRepository(); } catch { }
            }
            catch
            {
                // Setting controller unavailable on some CATIA editions —
                // shared code will still download files; CATIA just won't
                // auto-resolve them on the next open without a manual
                // Tools → Options entry.
            }
        }

        private static string TryReadSetting(SettingController setting, string property)
        {
            try
            {
                object val = setting.GetType()
                    .InvokeMember(property,
                        System.Reflection.BindingFlags.GetProperty,
                        null, setting, null);
                return val as string;
            }
            catch { return null; }
        }

        private static bool TryWriteSetting(SettingController setting, string property, string value)
        {
            try
            {
                setting.GetType()
                    .InvokeMember(property,
                        System.Reflection.BindingFlags.SetProperty,
                        null, setting, new object[] { value });
                return true;
            }
            catch { return false; }
        }

        public void ReloadActiveDocument()
        {
            var doc = _catApp?.ActiveDocument;
            if (doc == null) return;
            string path = doc.FullName;
            try { doc.Close(); } catch { }
            try { _catApp.Documents.Open(path); } catch { }
        }

        // ---- helpers ----

        private static string ResolvePartNumber(Document doc, string filename)
        {
            string fromProperty = ReadParameter(doc, "PART_NUMBER");
            if (!string.IsNullOrWhiteSpace(fromProperty) && PartNumberParser.LooksValid(fromProperty))
                return fromProperty.Trim().ToUpperInvariant();
            return PartNumberParser.ParseOrNull(filename);
        }

        /// <summary>
        /// CATIA's equivalent of custom properties is the Parameters collection
        /// hung off the Part / Product. String-typed parameters get added via
        /// the KnowledgewareTypeLib StrParam.
        /// </summary>
        private static string ReadParameter(Document doc, string key)
        {
            if (doc == null) return null;
            try
            {
                Parameters parameters = null;
                if (doc is PartDocument partDoc)
                    parameters = partDoc.Part.Parameters;
                else if (doc is ProductDocument prodDoc)
                    parameters = prodDoc.Product.UserRefProperties as Parameters;

                if (parameters == null) return null;

                Parameter param = parameters.Item(key);
                return param?.ValueAsString();
            }
            catch
            {
                return null;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // IRevisionDisplayAdapter — display-only spec-tree relabel (P-revdisp).
        //
        // Shows each component's CURRENT Atlas revision in the tree label (the
        // dome reads "AH120276_0H_FRONT WHEEL DOME" once Atlas bumped it to 0H)
        // by rewriting the product's PartNumber — the text before the parens.
        //
        // SAFE: changing PartNumber is display-only. CATIA links children by the
        // internal document UUID, not the PartNumber (proven earlier — patching
        // PartNumber did NOT fix a "wrong information" link error), so it can't
        // break parent→child resolution. The walk/upload/check-in resolve part
        // numbers from the FILENAME and the PART_NUMBER user parameter (see
        // ResolvePartNumber), never this product label, so relabelling can't
        // disturb part-number resolution — and the new label isn't a valid
        // 10-char code anyway, so even if it were read it'd be rejected.
        //
        // We SAVE the relabel here so the checkout baseline hash (recorded right
        // after this) captures it; otherwise check-in's save-all would persist
        // it later and the hash diff would look like a real edit → phantom bump.
        // ─────────────────────────────────────────────────────────────────────
        public int ApplyRevisionDisplay(CadDocument doc,
                                        IDictionary<string, string> currentPartNumberByFilename)
        {
            if (doc?.NativeHandle == null || currentPartNumberByFilename == null ||
                currentPartNumberByFilename.Count == 0)
                return 0;

            var prodDoc = doc.NativeHandle as ProductDocument;
            if (prodDoc == null) return 0;   // only assemblies have a tree to relabel

            LogWalk($"ApplyRevisionDisplay: v={WalkAssemblyVersion} map={currentPartNumberByFilename.Count} entries");

            int changed = 0;
            try
            {
                EnsureDesignMode(prodDoc.Product);

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Root product itself (its label also starts with the partcode).
                try
                {
                    string rootFile = string.IsNullOrEmpty(doc.FullPath)
                        ? null : Path.GetFileName(doc.FullPath);
                    if (!string.IsNullOrEmpty(rootFile) && seen.Add(rootFile) &&
                        TryApplyRevisionLabel(prodDoc.Product, rootFile, currentPartNumberByFilename))
                        changed++;
                }
                catch (Exception ex) { LogWalk($"ApplyRevisionDisplay: root relabel threw: {ex.Message}"); }

                ApplyRevisionDisplayWalk(prodDoc.Product, currentPartNumberByFilename, seen, ref changed);
            }
            catch (Exception ex) { LogWalk($"ApplyRevisionDisplay: threw: {ex.Message}"); }

            // Persist so the change survives and the baseline hash includes it.
            if (changed > 0)
            {
                try { SaveDocument(doc); }
                catch (Exception ex) { LogWalk($"ApplyRevisionDisplay: SaveDocument threw: {ex.Message}"); }
            }
            LogWalk($"ApplyRevisionDisplay: relabelled {changed} product(s)");
            return changed;
        }

        private void ApplyRevisionDisplayWalk(Product parent,
                                              IDictionary<string, string> map,
                                              HashSet<string> seen, ref int changed)
        {
            Products children;
            try { children = parent.Products; } catch { return; }
            if (children == null) return;
            int count;
            try { count = children.Count; } catch { return; }

            for (int i = 1; i <= count; i++)
            {
                Product child;
                try { child = children.Item(i); } catch { continue; }
                if (child == null) continue;

                string fullPath = TryGetChildPath(child);
                string filename = string.IsNullOrEmpty(fullPath) ? null : Path.GetFileName(fullPath);

                // The tree label before the parens is the REFERENCE product's
                // PartNumber, shared by every instance of the same file — so we
                // relabel the reference once per distinct filename.
                if (!string.IsNullOrEmpty(filename) && seen.Add(filename))
                {
                    Product refProd = null;
                    try { refProd = child.ReferenceProduct; } catch { }
                    if (TryApplyRevisionLabel(refProd ?? child, filename, map))
                        changed++;
                }

                if (ProductHasChildren(child))
                    ApplyRevisionDisplayWalk(child, map, seen, ref changed);
            }
        }

        private bool TryApplyRevisionLabel(Product target, string filename,
                                           IDictionary<string, string> map)
        {
            if (target == null || string.IsNullOrEmpty(filename)) return false;
            if (!map.TryGetValue(filename, out string currentPn) || string.IsNullOrEmpty(currentPn))
                return false;

            string oldLabel = ProductPartNumber(target);
            string newLabel = ComputeRevisionDisplay(oldLabel, filename, currentPn);
            if (string.IsNullOrEmpty(newLabel) ||
                string.Equals(newLabel, oldLabel, StringComparison.Ordinal))
                return false;

            bool ok = WriteStringProp(target, "PartNumber", newLabel);
            LogWalk($"  relabel '{filename}': '{oldLabel}' -> '{newLabel}' ({(ok ? "ok" : "FAILED")})");
            return ok;
        }

        // Swap only the 2-char revision token in a display label, preserving the
        // human description. Convention (PartNumberParser.BaseRevPattern):
        //   base8 + "_" + rev2 + "_" + description
        // e.g. "AH120276_0G_FRONT WHEEL DOME" + current pn "AH1202760H"
        //   ->  "AH120276_0H_FRONT WHEEL DOME".
        // Returns null (leave unchanged) if the label doesn't fit the convention
        // for this part's base — we never guess.
        private static string ComputeRevisionDisplay(string oldLabel, string filename, string currentPn)
        {
            if (string.IsNullOrEmpty(currentPn) || currentPn.Length < 10) return null;
            string baseCode = currentPn.Substring(0, 8);
            string newSuffix = currentPn.Substring(8, 2);

            string src = !string.IsNullOrEmpty(oldLabel)
                ? oldLabel
                : Path.GetFileNameWithoutExtension(filename);
            if (string.IsNullOrEmpty(src) || src.Length < 11) return null;

            // src must be "<baseCode>_<rev2>…" to be safely rewritable. (If it's
            // already showing newSuffix the result equals src and the caller's
            // equality check skips the write — so no idempotency guard needed.)
            if (!src.Substring(0, 8).Equals(baseCode, StringComparison.OrdinalIgnoreCase) ||
                src[8] != '_')
                return null;

            string rest = src.Substring(11);            // "_FRONT WHEEL DOME" (or "")
            return baseCode + "_" + newSuffix + rest;
        }

        private static void LogWalk(string line)
        {
            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AtlasCad");
                Directory.CreateDirectory(logDir);
                System.IO.File.AppendAllText(
                    Path.Combine(logDir, "walk_assembly.log"),
                    $"--- {DateTime.Now:O} CatiaAdapter.{line}\n");
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Version-agnostic COM property accessors (P7.53)
        //
        // Built against V5R21 Interop, code calls `obj.get_Name()` because R21's
        // typelib declared Name with [in, out] BSTR* — Interop generated a
        // method pair, not a property. R2025's typelib cleaned this up; the same
        // Interop call may not exist or may be named differently.
        //
        // CATIA's COM contracts (vtable slots, IIDs) are stable across versions;
        // only the .NET wrapper differs. These helpers reflect over whichever
        // wrapper shape is present, so the SAME compiled DLL runs against
        // either Interop / either CATIA version.
        //
        // Reflection cost is one-time-per-call lookup. Negligible compared to
        // the COM call itself (~ms).
        // ─────────────────────────────────────────────────────────────────────

        private static string ReadStringProp(object target, string propName)
        {
            if (target == null) return null;
            Type t = target.GetType();
            // Try clean property accessor first (R2025 shape)
            try
            {
                PropertyInfo pi = t.GetProperty(propName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (pi != null && pi.CanRead)
                    return pi.GetValue(target, null) as string;
            }
            catch { }
            // Fall back to get_X() method (R21 shape)
            try
            {
                MethodInfo mi = t.GetMethod("get_" + propName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase,
                    binder: null, types: Type.EmptyTypes, modifiers: null);
                if (mi != null)
                    return mi.Invoke(target, null) as string;
            }
            catch { }
            // Last resort: IDispatch via InvokeMember (works on any COM object
            // that supports automation, regardless of typelib generation).
            try
            {
                return t.InvokeMember(propName,
                    BindingFlags.GetProperty | BindingFlags.Instance | BindingFlags.Public,
                    null, target, null) as string;
            }
            catch { }
            return null;
        }

        // Mirror of ReadStringProp for WRITING a string COM property across the
        // two Interop shapes (clean setter → set_X(value) → IDispatch).
        private static bool WriteStringProp(object target, string propName, string value)
        {
            if (target == null) return false;
            Type t = target.GetType();
            // Clean property setter (R2025 shape).
            try
            {
                PropertyInfo pi = t.GetProperty(propName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (pi != null && pi.CanWrite)
                {
                    pi.SetValue(target, value, null);
                    return true;
                }
            }
            catch { }
            // set_X(value) method (R21 shape).
            try
            {
                MethodInfo mi = t.GetMethod("set_" + propName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase,
                    binder: null, types: new[] { typeof(string) }, modifiers: null);
                if (mi != null)
                {
                    mi.Invoke(target, new object[] { value });
                    return true;
                }
            }
            catch { }
            // Last resort: IDispatch SetProperty.
            try
            {
                t.InvokeMember(propName,
                    BindingFlags.SetProperty | BindingFlags.Instance | BindingFlags.Public,
                    null, target, new object[] { value });
                return true;
            }
            catch { }
            return false;
        }

        private static bool? TryReadBoolProp(object target, string propName)
        {
            if (target == null) return null;
            Type t = target.GetType();
            try
            {
                PropertyInfo pi = t.GetProperty(propName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (pi != null && pi.CanRead)
                {
                    object v = pi.GetValue(target, null);
                    if (v is bool b) return b;
                }
            }
            catch { }
            try
            {
                MethodInfo mi = t.GetMethod("get_" + propName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase,
                    binder: null, types: Type.EmptyTypes, modifiers: null);
                if (mi != null)
                {
                    object v = mi.Invoke(target, null);
                    if (v is bool b) return b;
                }
            }
            catch { }
            return null; // property doesn't exist on this CATIA version
        }

        private static string ProductName(Product p)        => ReadStringProp(p, "Name");
        private static string ProductPartNumber(Product p)  => ReadStringProp(p, "PartNumber");
        private static string ProductDescriptionInst(Product p) => ReadStringProp(p, "DescriptionInst");
        private static string ProductNomenclature(Product p) => ReadStringProp(p, "Nomenclature");
        private static string DocumentName(Document d)      => ReadStringProp(d, "Name");
    }
}
