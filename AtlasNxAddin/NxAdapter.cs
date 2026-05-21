using System;
using System.Collections.Generic;
using System.IO;
using AtlasCadCore.Adapter;
using AtlasCadCore.Utility;
using NXOpen;
using NXOpen.Assemblies;

namespace AtlasCadPlugin.Nx
{
    /// <summary>
    /// Siemens NX implementation of ICadAdapter via NX Open .NET.
    /// Targets NX 12+ API conventions (NXOpen namespace; Part / ComponentAssembly
    /// / Component model). For NX 11 and earlier, ComponentAssembly methods
    /// may have slightly different overloads.
    /// </summary>
    public class NxAdapter : ICadAdapter
    {
        private readonly Session _session;

        public NxAdapter()
        {
            _session = Session.GetSession();
        }

        public string CadName => "NX";

        public IReadOnlyList<string> NativeFileExtensions => new[] { ".prt" };

        public CadDocument GetActiveDocument()
        {
            Part workPart = _session.Parts.Work;
            if (workPart == null) return null;
            string path = workPart.FullPath;
            // NX assemblies and parts share the .prt extension. An "assembly"
            // here means ComponentAssembly.RootComponent has children.
            bool isAssembly = workPart.ComponentAssembly?.RootComponent != null
                              && workPart.ComponentAssembly.RootComponent.GetChildren().Length > 0;
            return new CadDocument
            {
                FullPath = path,
                Name = Path.GetFileName(path),
                IsAssembly = isAssembly,
                NativeHandle = workPart,
            };
        }

        public void SaveDocument(CadDocument doc)
        {
            var part = (Part)doc.NativeHandle;
            part.Save(BasePart.SaveComponents.True, BasePart.CloseAfterSave.False);
        }

        public void OpenDocument(string filePath)
        {
            PartLoadStatus status;
            _session.Parts.Open(filePath, out status);
            status?.Dispose();
        }

        public List<AssemblyFileRef> WalkAssembly(CadDocument doc)
        {
            var part = (Part)doc.NativeHandle ??
                       throw new InvalidOperationException("No active part");

            string rootPath = part.FullPath;
            if (string.IsNullOrEmpty(rootPath))
                throw new InvalidOperationException("Part has not been saved to disk yet");

            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<AssemblyFileRef>();

            string rootFilename = Path.GetFileName(rootPath);
            string rootPn = ResolvePartNumber(part, rootFilename);
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

            if (part.ComponentAssembly?.RootComponent != null)
                WalkComponent(part.ComponentAssembly.RootComponent, rootPn, result, seenPaths);

            return result;
        }

        private void WalkComponent(Component parent, string parentPn,
                                    List<AssemblyFileRef> output, HashSet<string> seenPaths)
        {
            foreach (Component child in parent.GetChildren())
            {
                if (child == null) continue;
                if (child.IsSuppressed) continue;

                string fullPath = null;
                try { fullPath = child.Prototype?.OwningPart?.FullPath; }
                catch { continue; }
                if (string.IsNullOrEmpty(fullPath)) continue;
                if (!File.Exists(fullPath)) continue;
                if (!seenPaths.Add(fullPath)) continue;

                string filename = Path.GetFileName(fullPath);
                Part childPart = child.Prototype?.OwningPart as Part;
                string childPn = ResolvePartNumber(childPart, filename);
                output.Add(new AssemblyFileRef
                {
                    FullPath = fullPath,
                    RelativePath = filename,
                    Filename = filename,
                    IsRoot = false,
                    PartNumber = childPn,
                    ParentPartNumber = parentPn,
                });

                WalkComponent(child, childPn, output, seenPaths);
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
                if (ext != ".prt") continue;
                progress?.Invoke(i + 1, inputs.Count, f.Filename);

                string stepName = Path.GetFileNameWithoutExtension(f.Filename) + ".stp";
                string stepPath = Path.Combine(stagingDir, stepName);

                // Open part if not already loaded.
                Part srcPart;
                try
                {
                    PartLoadStatus status;
                    var loadResult = _session.Parts.Open(f.FullPath, out status);
                    srcPart = loadResult as Part;
                    status?.Dispose();
                }
                catch { continue; }
                if (srcPart == null) continue;

                try
                {
                    // NX 12+ exposes Step214Creator via PartCollection or
                    // DexManager. The shortest path is Part.SaveAs(stepPath)
                    // with explicit format inference — extension-driven.
                    PartSaveStatus saveStatus = srcPart.SaveAs(stepPath);
                    saveStatus?.Dispose();
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
            var part = (Part)activeAssembly.NativeHandle;
            var asm = part.ComponentAssembly;
            if (asm == null)
                throw new InvalidOperationException("Active part has no ComponentAssembly");

            PartLoadStatus loadStatus;
            // AddComponent signature: (filePath, referenceSetName, componentName,
            //   origin, orientationMatrix, layerNumber, loadStatus, allowDuplicates)
            asm.AddComponent(
                filePath,
                "Entire Part",
                Path.GetFileNameWithoutExtension(filePath),
                new Point3d(0, 0, 0),
                new Matrix3x3 { Xx = 1, Yy = 1, Zz = 1 },
                -1,
                out loadStatus,
                true);
            loadStatus?.Dispose();
        }

        public string ImportStepAsNative(string stpPath, string nativeOutPathHint)
        {
            // NX's Session.Parts.Open auto-imports STEP files based on the
            // extension via the built-in translator (Step214/242). The result
            // is loaded as the work part; we SaveAs to the .prt path. NX uses
            // a single .prt extension for both parts and assemblies, so the
            // caller's hint extension is honoured as-is.
            PartLoadStatus loadStatus = null;
            Part imported;
            try
            {
                imported = _session.Parts.Open(stpPath, out loadStatus) as Part;
            }
            finally
            {
                loadStatus?.Dispose();
            }
            if (imported == null)
                throw new InvalidOperationException($"NX could not import STEP ({stpPath}).");

            string outPath = Path.Combine(
                Path.GetDirectoryName(nativeOutPathHint),
                Path.GetFileNameWithoutExtension(nativeOutPathHint) + ".prt");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            PartSaveStatus saveStatus = null;
            try
            {
                saveStatus = imported.SaveAs(outPath);
            }
            finally
            {
                saveStatus?.Dispose();
            }
            if (!File.Exists(outPath))
                throw new InvalidOperationException($"SaveAs '{outPath}' produced no file.");
            return outPath;
        }

        public List<MissingComponent> FindMissingComponents(CadDocument assembly)
        {
            // TODO: NX-specific implementation. NX assemblies expose missing
            // children via PartLoadStatus / NXOpen.Assemblies.Component.IsSuppressed;
            // left as a no-op until needed for the NX pilot.
            return new List<MissingComponent>();
        }

        public void AddSearchFolder(string folderPath)
        {
            // TODO: NX uses UGII_SEARCH_DIRS env var (paths separated by ;).
            // Setting it at runtime is non-trivial; left as a no-op.
        }

        public void ReloadActiveDocument()
        {
            var part = _session?.Parts?.Work;
            if (part == null) return;
            string path = part.FullPath;
            PartCloseStatus closeStatus = null;
            try { closeStatus = part.Close(BasePart.CloseWholeTree.True, BasePart.CloseModified.UseResponses, null); }
            catch { }
            finally { closeStatus?.Dispose(); }
            PartLoadStatus loadStatus;
            try { _session.Parts.Open(path, out loadStatus); loadStatus?.Dispose(); } catch { }
        }

        // ---- NX-specific helpers ----

        private static string ResolvePartNumber(Part part, string filename)
        {
            string fromAttr = ReadUserAttribute(part, "PART_NUMBER");
            if (!string.IsNullOrWhiteSpace(fromAttr) && PartNumberParser.LooksValid(fromAttr))
                return fromAttr.Trim().ToUpperInvariant();
            return PartNumberParser.ParseOrNull(filename);
        }

        /// <summary>
        /// NX's analogue to SW custom properties is "User Attributes" on
        /// any NXObject. Read via GetUserAttribute(title, type, index).
        /// </summary>
        private static string ReadUserAttribute(Part part, string key)
        {
            if (part == null) return null;
            try
            {
                // -1 index means "any index" — NX user attributes can be arrays.
                NXObject.AttributeInformation info = part.GetUserAttribute(key, NXObject.AttributeType.String, -1);
                return info.StringValue;
            }
            catch
            {
                return null;
            }
        }
    }
}
