using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AtlasCadCore.Adapter;
using AtlasCadCore.Utility;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace AtlasCadPlugin.SolidWorks
{
    public class SolidWorksAdapter : ICadAdapter
    {
        private readonly ISldWorks _swApp;

        public SolidWorksAdapter(ISldWorks swApp)
        {
            _swApp = swApp;
            _swApp.SetUserPreferenceIntegerValue(
                (int)swUserPreferenceIntegerValue_e.swStepAP, 214);
        }

        public string CadName => "SolidWorks";

        public IReadOnlyList<string> NativeFileExtensions => new[] { ".sldasm", ".sldprt" };

        public CadDocument GetActiveDocument()
        {
            var doc = (IModelDoc2)_swApp.ActiveDoc;
            if (doc == null) return null;
            int docType = doc.GetType();
            return new CadDocument
            {
                FullPath = doc.GetPathName(),
                Name = Path.GetFileName(doc.GetPathName()),
                IsAssembly = docType == (int)swDocumentTypes_e.swDocASSEMBLY,
                NativeHandle = doc,
            };
        }

        public void SaveDocument(CadDocument doc)
        {
            var swDoc = (IModelDoc2)doc.NativeHandle;
            int errors = 0, warnings = 0;
            swDoc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings);
        }

        public void OpenDocument(string filePath)
        {
            int docType = filePath.ToLowerInvariant().EndsWith(".sldasm")
                ? (int)swDocumentTypes_e.swDocASSEMBLY
                : (int)swDocumentTypes_e.swDocPART;
            int errors = 0, warnings = 0;
            _swApp.OpenDoc6(
                filePath, docType,
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                "", ref errors, ref warnings);
        }

        public const string WalkAssemblyVersion = "2026-05-21-skipreason-v1";

        public List<AssemblyFileRef> WalkAssembly(CadDocument doc)
        {
            try
            {
                string logDir = Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                    "AtlasCad");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(
                    Path.Combine(logDir, "walk_assembly.log"),
                    $"--- {DateTime.Now:O} SolidWorksAdapter.WalkAssembly v={WalkAssemblyVersion} invoked\n");
            }
            catch { }

            var swDoc = (IModelDoc2)doc.NativeHandle ??
                        throw new InvalidOperationException("No active document");

            int docType = swDoc.GetType();
            if (docType != (int)swDocumentTypes_e.swDocASSEMBLY)
                throw new InvalidOperationException("Active document is not an assembly");

            string rootPath = swDoc.GetPathName();
            if (string.IsNullOrEmpty(rootPath))
                throw new InvalidOperationException("Assembly has not been saved to disk yet — save it first");

            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<AssemblyFileRef>();

            string rootFilename = Path.GetFileName(rootPath);
            string rootPn = ResolvePartNumber(swDoc, rootFilename);
            result.Add(new AssemblyFileRef
            {
                FullPath = rootPath,
                RelativePath = rootFilename,
                Filename = rootFilename,
                IsRoot = true,
                PartNumber = rootPn,
                ParentPartNumber = null,
                NativeHandle = swDoc,
            });
            seenPaths.Add(rootPath);

            var asm = (AssemblyDoc)swDoc;
            WalkChildren(asm, rootPn, seenPaths, result);
            return result;
        }

        private void WalkChildren(AssemblyDoc parentAsm, string parentPn,
                                   HashSet<string> seenPaths, List<AssemblyFileRef> result)
        {
            object[] components = (object[])parentAsm.GetComponents(true);
            if (components == null) return;
            foreach (object comp in components)
            {
                Component2 c = comp as Component2;
                if (c == null) continue;

                string fullPath = c.GetPathName();
                string childFilename = string.IsNullOrEmpty(fullPath) ? null : Path.GetFileName(fullPath);
                bool suppressed = false;
                try { suppressed = c.IsSuppressed(); } catch { }

                string skipReason = null;
                if (suppressed) skipReason = "suppressed";
                else if (string.IsNullOrEmpty(fullPath)) skipReason = "no-path";
                else if (!File.Exists(fullPath)) skipReason = "missing-file";

                if (skipReason == null && !seenPaths.Add(fullPath)) continue;

                IModelDoc2 compDoc = null;
                try { compDoc = c.GetModelDoc2() as IModelDoc2; } catch { }
                string childPn = ResolvePartNumber(compDoc, childFilename);
                if (skipReason == null && string.IsNullOrEmpty(childPn))
                    skipReason = "no-part-number";

                string displayName = childFilename;
                if (string.IsNullOrEmpty(displayName))
                {
                    try { displayName = c.Name2; } catch { }
                    if (string.IsNullOrEmpty(displayName)) displayName = "(unnamed component)";
                }

                result.Add(new AssemblyFileRef
                {
                    FullPath = fullPath,
                    RelativePath = displayName,
                    Filename = displayName,
                    IsRoot = false,
                    PartNumber = childPn,
                    ParentPartNumber = parentPn,
                    NativeHandle = compDoc,
                    SkipReason = skipReason,
                });

                if (skipReason == null && compDoc != null
                    && compDoc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
                {
                    WalkChildren((AssemblyDoc)compDoc, childPn, seenPaths, result);
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
            var inputs = nativeFiles
                .Where(f => {
                    string ext = Path.GetExtension(f.Filename).ToLowerInvariant();
                    return ext == ".sldprt" || ext == ".sldasm";
                })
                .ToList();

            for (int i = 0; i < inputs.Count; i++)
            {
                var f = inputs[i];
                progress?.Invoke(i + 1, inputs.Count, f.Filename);

                string stepName = Path.GetFileNameWithoutExtension(f.Filename) + ".stp";
                string stepPath = Path.Combine(stagingDir, stepName);

                IModelDoc2 srcDoc = f.NativeHandle as IModelDoc2;
                bool opened = false;
                if (srcDoc == null)
                {
                    string ext = Path.GetExtension(f.Filename).ToLowerInvariant();
                    int docType = ext == ".sldasm"
                        ? (int)swDocumentTypes_e.swDocASSEMBLY
                        : (int)swDocumentTypes_e.swDocPART;
                    int loadErrors = 0, loadWarnings = 0;
                    srcDoc = (IModelDoc2)_swApp.OpenDoc6(
                        f.FullPath, docType,
                        (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                        "", ref loadErrors, ref loadWarnings);
                    opened = srcDoc != null;
                }
                if (srcDoc == null) continue;

                int saveErrors = 0, saveWarnings = 0;
                bool ok = srcDoc.Extension.SaveAs(
                    stepPath,
                    (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent
                        | (int)swSaveAsOptions_e.swSaveAsOptions_Copy,
                    null, ref saveErrors, ref saveWarnings);

                if (opened)
                {
                    try { _swApp.CloseDoc(f.FullPath); } catch { }
                }

                if (!ok || !File.Exists(stepPath)) continue;
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

        public string ImportStepAsNative(string stpPath, string nativeOutPathHint)
        {
            ImportStepData stepData = _swApp.GetImportFileData(stpPath) as ImportStepData;
            if (stepData != null)
            {
                stepData.MapConfigurationData = false;
            }
            int errors = 0;
            IModelDoc2 imported = _swApp.LoadFile4(stpPath, "r", stepData, ref errors) as IModelDoc2;
            if (imported == null)
                throw new InvalidOperationException(
                    $"SolidWorks could not import STEP file ({stpPath}). Errors={errors}");

            int importedType = imported.GetType();
            string ext = importedType == (int)swDocumentTypes_e.swDocASSEMBLY ? ".sldasm" : ".sldprt";
            string outPath = Path.Combine(
                Path.GetDirectoryName(nativeOutPathHint),
                Path.GetFileNameWithoutExtension(nativeOutPathHint) + ext);

            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            int saveErrors = 0, saveWarnings = 0;
            bool ok = imported.Extension.SaveAs(
                outPath,
                (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent
                    | (int)swSaveAsOptions_e.swSaveAsOptions_Copy,
                null, ref saveErrors, ref saveWarnings);
            _swApp.CloseDoc(imported.GetPathName());
            if (!ok || !File.Exists(outPath))
                throw new InvalidOperationException(
                    $"SaveAs '{outPath}' failed. Errors={saveErrors}");
            return outPath;
        }

        public void InsertComponent(CadDocument activeAssembly, string filePath)
        {
            var asm = (AssemblyDoc)((IModelDoc2)activeAssembly.NativeHandle);
            Component2 comp = asm.AddComponent5(
                filePath,
                (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                "", false, "", 0, 0, 0);
            if (comp == null)
                throw new InvalidOperationException("AddComponent5 returned null — see SolidWorks status bar.");
        }

        public List<MissingComponent> FindMissingComponents(CadDocument assembly)
        {
            var result = new List<MissingComponent>();
            var swDoc = (IModelDoc2)assembly.NativeHandle;
            if (swDoc == null) return result;
            if (swDoc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY) return result;

            var asm = (AssemblyDoc)swDoc;
            object[] components = (object[])asm.GetComponents(true);
            if (components == null) return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object comp in components)
            {
                var c = comp as Component2;
                if (c == null) continue;

                string path = c.GetPathName();
                if (string.IsNullOrEmpty(path)) continue;
                if (File.Exists(path)) continue;            // resolved already
                if (!seen.Add(path)) continue;              // dedupe — same .sldprt used multiple times

                string filename = Path.GetFileName(path);
                result.Add(new MissingComponent
                {
                    Filename = filename,
                    ExpectedPath = path,
                    PartNumber = PartNumberParser.ParseOrNull(filename),
                });
            }
            return result;
        }

        public void AddSearchFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return;
            const int FolderTypeReferencedDocuments = 0;
            string existing = _swApp.GetSearchFolders(FolderTypeReferencedDocuments) ?? "";
            foreach (string p in existing.Split(';'))
            {
                if (string.Equals(p?.Trim().TrimEnd('\\'), folderPath.TrimEnd('\\'),
                                  StringComparison.OrdinalIgnoreCase))
                    return; // already registered
            }
            string updated = string.IsNullOrEmpty(existing) ? folderPath : existing + ";" + folderPath;
            _swApp.SetSearchFolders(FolderTypeReferencedDocuments, updated);
        }

        public void ReloadActiveDocument()
        {
            var doc = (IModelDoc2)_swApp.ActiveDoc;
            if (doc == null) return;
            string path = doc.GetPathName();
            int docType = doc.GetType();
            _swApp.CloseDoc(path);
            int errors = 0, warnings = 0;
            _swApp.OpenDoc6(
                path, docType,
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                "", ref errors, ref warnings);
        }

        private static string ResolvePartNumber(IModelDoc2 doc, string filename)
        {
            string fromProperty = ReadCustomProperty(doc, "PART_NUMBER");
            if (!string.IsNullOrWhiteSpace(fromProperty) && PartNumberParser.LooksValid(fromProperty))
                return fromProperty.Trim().ToUpperInvariant();
            return PartNumberParser.ParseOrNull(filename);
        }

        private static string ReadCustomProperty(IModelDoc2 doc, string key)
        {
            if (doc == null) return null;
            var mgr = doc.Extension.CustomPropertyManager[""];
            if (mgr == null) return null;
            string val = "", evaluated = "";
            mgr.Get4(key, false, out val, out evaluated);
            return string.IsNullOrEmpty(evaluated) ? val : evaluated;
        }
    }
}
