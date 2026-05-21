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
    /// <summary>
    /// CATIA V5 implementation of ICadAdapter. Uses the CATIA V5 Automation
    /// API (INFITF / MECMOD / ProductStructureTypeLib) — same API surface
    /// the V5 macro / VBA story uses, accessible from C# via COM interop.
    ///
    /// Caveat: this is written against CATIA V5R30 conventions. Older
    /// releases may have signature mismatches; newer releases (3DEXPERIENCE)
    /// have moved much of this into different namespaces.
    /// </summary>
    public class CatiaAdapter : ICadAdapter
    {
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
            // CATIA distinguishes Part (CATPart) and Product (CATProduct).
            // Assembly file = .CATProduct.
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
            catDoc.Save();
        }

        public void OpenDocument(string filePath)
        {
            _catApp.Documents.Open(filePath);
        }

        public List<AssemblyFileRef> WalkAssembly(CadDocument doc)
        {
            var catDoc = (Document)doc.NativeHandle ??
                         throw new InvalidOperationException("No active document");

            string rootPath = catDoc.FullName;
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
            });
            seenPaths.Add(rootPath);

            // ProductDocument exposes a Product whose Products collection is
            // the immediate children. Recurse into each via its ReferenceProduct.
            var productDoc = (ProductDocument)catDoc;
            WalkProductTree(productDoc.Product, rootPn, result, seenPaths);
            return result;
        }

        private void WalkProductTree(Product parent, string parentPn,
                                      List<AssemblyFileRef> output, HashSet<string> seenPaths)
        {
            Products children = parent.Products;
            for (int i = 1; i <= children.Count; i++)
            {
                Product child = children.Item(i);
                // ReferenceProduct.Parent gives the underlying Document.
                Document refDoc = null;
                try { refDoc = (Document)child.ReferenceProduct.Parent; }
                catch { continue; }
                if (refDoc == null) continue;

                string fullPath = refDoc.FullName;
                if (string.IsNullOrEmpty(fullPath)) continue;
                if (!File.Exists(fullPath)) continue;
                if (!seenPaths.Add(fullPath)) continue;

                string childFilename = Path.GetFileName(fullPath);
                string childPn = ResolvePartNumber(refDoc, childFilename);
                output.Add(new AssemblyFileRef
                {
                    FullPath = fullPath,
                    RelativePath = childFilename,
                    Filename = childFilename,
                    IsRoot = false,
                    PartNumber = childPn,
                    ParentPartNumber = parentPn,
                });

                // Recurse — assemblies can contain assemblies.
                if (Path.GetExtension(fullPath).Equals(".CATProduct", StringComparison.OrdinalIgnoreCase))
                {
                    Product nested = child.ReferenceProduct;
                    if (nested != null) WalkProductTree(nested, childPn, output, seenPaths);
                }
            }
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

                // Open (or get cached) the source document, then ExportData.
                Document srcDoc;
                try { srcDoc = _catApp.Documents.Open(f.FullPath); }
                catch { continue; }
                if (srcDoc == null) continue;

                try
                {
                    // ExportData("path.stp", "stp") — second arg is the format
                    // identifier the CATIA exporter knows about.
                    srcDoc.ExportData(stepPath, "stp");
                }
                catch { continue; }

                if (!File.Exists(stepPath)) continue;

                result.Add(new AssemblyFileRef
                {
                    FullPath = stepPath,
                    Filename = stepName,
                    RelativePath = stepName,
                    IsRoot = false,
                    PartNumber = f.PartNumber,
                });
            }
            return result;
        }

        public void InsertComponent(CadDocument activeAssembly, string filePath)
        {
            var productDoc = (ProductDocument)activeAssembly.NativeHandle;
            // AddComponentsFromFiles takes an array of paths + a position-pattern
            // string ("*" = each at origin). Returns the array of new Products.
            object filesArray = new object[] { filePath };
            productDoc.Product.Products.AddComponentsFromFiles((Array)filesArray, "*");
        }

        public string ImportStepAsNative(string stpPath, string nativeOutPathHint)
        {
            // CATIA imports STP via Documents.Open — the engine detects the
            // extension and runs the appropriate translator. The opened
            // document is then SaveAs-ed to the target native path.
            Document imported = _catApp.Documents.Open(stpPath);
            if (imported == null)
                throw new InvalidOperationException($"CATIA could not import STEP ({stpPath}).");

            // Imported STEP becomes a CATPart (single body) or CATProduct
            // (assembly). Use the actual filename CATIA picked.
            string actualName = imported.Name;
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
            if (!File.Exists(outPath))
                throw new InvalidOperationException($"SaveAs '{outPath}' produced no file.");
            return outPath;
        }

        public List<MissingComponent> FindMissingComponents(CadDocument assembly)
        {
            // TODO: CATIA-specific implementation. CATIA's product structure
            // exposes broken children via Product.HasAReferenceProduct or
            // similar; left as a no-op until needed for the CATIA pilot.
            return new List<MissingComponent>();
        }

        public void AddSearchFolder(string folderPath)
        {
            // TODO: CATIA uses CATIA_INSTALL_PATH-style env vars or the
            // 'Linked Documents Localization' setting in Tools → Options.
        }

        public void ReloadActiveDocument()
        {
            var doc = _catApp?.ActiveDocument;
            if (doc == null) return;
            string path = doc.FullName;
            try { doc.Close(); } catch { }
            try { _catApp.Documents.Open(path); } catch { }
        }

        // ---- CATIA-specific helpers ----

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
    }
}
