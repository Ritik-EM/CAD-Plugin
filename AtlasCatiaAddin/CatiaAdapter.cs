using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using AtlasCadCore.Adapter;
using AtlasCadCore.Utility;
using INFITF;
using MECMOD;
using ProductStructureTypeLib;
using KnowledgewareTypeLib;

namespace AtlasCadPlugin.Catia
{
    public class CatiaAdapter : ICadAdapter
    {
        public const string WalkAssemblyVersion = "2026-05-28-parity-v1";

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
            var catDoc = (Document)doc.NativeHandle;
            // CATIA throws E_UNEXPECTED if there's nothing to save or the doc
            // has never been saved to disk. Skip the call in those cases —
            // Save() is best-effort here.
            try
            {
                if (catDoc.Saved) return;
            }
            catch { /* Saved unsupported on some doc types — fall through */ }

            if (string.IsNullOrEmpty(catDoc.FullName))
                return; // never been saved; SaveAs is the user's problem

            try { catDoc.Save(); }
            catch (System.Runtime.InteropServices.COMException) { /* best-effort */ }
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
            WalkProductTree(productDoc.Product, rootPn, result, seenPaths);
            return result;
        }

        // Walks Product children without re-opening their backing Documents.
        // CATIA exposes the on-disk path via Product.ReferenceProduct.Parent
        // when the doc is already loaded, OR via the child Product's File-
        // related properties when it isn't. We avoid Documents.Open here to
        // skip the "Several editors are opened" warning (P7.39) — only fall
        // back to opening when we genuinely can't read the metadata otherwise.
        private void WalkProductTree(Product parent, string parentPn,
                                      List<AssemblyFileRef> output, HashSet<string> seenPaths)
        {
            Products children;
            try { children = parent.Products; }
            catch { return; }
            if (children == null) return;

            for (int i = 1; i <= children.Count; i++)
            {
                Product child;
                try { child = children.Item(i); }
                catch { continue; }
                if (child == null) continue;

                string fullPath = TryGetChildPath(child);
                string childFilename = string.IsNullOrEmpty(fullPath) ? null : Path.GetFileName(fullPath);

                // V5R21 doesn't expose Product.IsActivated, so we can't
                // explicitly tag suppressed children. They show up as
                // "no-path" or "missing-file" instead, which is fine for the
                // upload form's filter.
                string skipReason = null;
                if (string.IsNullOrEmpty(fullPath)) skipReason = "no-path";
                else if (!System.IO.File.Exists(fullPath)) skipReason = "missing-file";

                if (skipReason == null && !seenPaths.Add(fullPath))
                    continue;

                // Resolve part_number from the reference Document if it's
                // already loaded; don't force-open just to read it.
                Document refDoc = TryGetLoadedReferenceDoc(child);
                string childPn = ResolvePartNumber(refDoc, childFilename);
                if (skipReason == null && string.IsNullOrEmpty(childPn))
                    skipReason = "no-part-number";

                string displayName = childFilename;
                if (string.IsNullOrEmpty(displayName))
                {
                    try { displayName = child.get_Name(); } catch { }
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

                if (skipReason == null
                    && !string.IsNullOrEmpty(fullPath)
                    && Path.GetExtension(fullPath).Equals(".CATProduct", StringComparison.OrdinalIgnoreCase))
                {
                    Product nested = null;
                    try { nested = child.ReferenceProduct; } catch { }
                    if (nested != null) WalkProductTree(nested, childPn, output, seenPaths);
                }
            }
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
        /// CATIA's tree typically shows broken children as "InternalID
        /// [filename.CATPart]" — we extract the bracketed filename when
        /// present, otherwise treat PartNumber as the basename and append a
        /// likely extension. Returns a bare filename (no directory) so the
        /// caller knows to resolve it against the parent assembly's folder.
        /// </summary>
        private static string TryGuessFilename(Product child)
        {
            try
            {
                string name = child.get_Name();
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

            try
            {
                string pn = child.get_PartNumber();
                if (!string.IsNullOrEmpty(pn))
                    return pn + ".CATPart"; // best-guess extension
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
            string actualName = imported.get_Name();
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
                    try { nm = doc.get_Name(); } catch { }
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
                try { cName = child.get_Name(); } catch { }
                try { cPn = child.get_PartNumber(); } catch { }
                try { cDesc = child.get_DescriptionInst(); } catch { }
                try { cNom = child.get_Nomenclature(); } catch { }
                LogResolve($"  child[{i}]: Name='{cName}' PN='{cPn}' DescInst='{cDesc}' Nomenclature='{cNom}'");

                Product refProd = null;
                try { refProd = child.ReferenceProduct; } catch (Exception ex) { LogResolve($"    .ReferenceProduct threw: {ex.Message}"); }
                if (refProd != null)
                {
                    string rpName = null;
                    try { rpName = refProd.get_Name(); } catch { }
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
    }
}
