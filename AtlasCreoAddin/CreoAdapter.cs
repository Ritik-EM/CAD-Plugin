using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using AtlasCadCore.Adapter;
using AtlasCadCore.Utility;
using pfcls;

namespace AtlasCadPlugin.Creo
{
    // Creo Parametric implementation of ICadAdapter, built on the free VB API
    // (pfcls, async COM). Structurally mirrors AtlasCatiaAddin/CatiaAdapter.cs
    // (the closest MCAD template): defensive per-node try/catch, walk logged to
    // %APPDATA%\AtlasCad\walk_assembly.log, part number resolved from a
    // PART_NUMBER parameter then the filename, per-part STEP export with a
    // root-assembly size-gate, and NO SHA-256 in the adapter (the shared forms
    // hash anything with a FullPath after the walk).
    //
    // GROUNDING: the core navigation is now confirmed against PTC's own examples
    // shipped with Creo 10 — vbapi\vbapi_examples\pfcAssembliesExamples.vb and
    // pfcInterfaceExamples.vb — namely: session.CurrentModel (property),
    // model.Type == EpfcModelType.EpfcMDL_ASSEMBLY, assembly.ListFeaturesByType(
    // bool, EpfcFeatureType.EpfcFEATTYPE_COMPONENT), component.ModelDescr,
    // session.GetModelFromDescr / RetrieveModel, new CCpfcModelDescriptor().
    // CreateFromFileName(name), model.Export(path, instructions), and
    // assembly.AssembleComponent(solid, Nothing). Calls NOT covered by those
    // examples are still marked `// VERIFY (vbapidoc: <page>)` pointing at the
    // local reference under
    //   C:\Program Files\PTC\Creo 10.0.9.0\Common Files\vbapi\vbapidoc\api\<page>.
    //
    // STATUS: authored against docs+examples, NOT yet compiled/run — the dev seat
    // is the Educational Edition, which ships the VB API DOCS but not the pfcls
    // COM component, so this won't build until a COMMERCIAL Creo seat (with pfcls
    // registered) is available. Each VERIFY marker is a spot to confirm there.
    //
    // Creo divergences from CATIA (already accounted for below):
    //  - Every assembly node is FILE-BACKED (.prt/.asm) — no CATIA-style
    //    "embedded, file-less sub-assembly" branch, so the walk is simpler.
    //  - Creo appends on-disk version numbers (top.asm.3); ResolveOnDiskPath()
    //    resolves the highest-numbered file for a base name.
    //  - Part number lives in a model PARAMETER (IpfcParameterOwner.GetParam),
    //    one accessor for both parts and assemblies (CATIA needed two).
    //  - The child model is reached via component.ModelDescr (no ComponentPath).
    //  - 0-based COM sequences (CATIA was 1-based).
    public class CreoAdapter : ICadAdapter
    {
        // Bump on any behavior change; stamped into every walk_assembly.log run.
        public const string WalkAssemblyVersion = "2026-07-05-creo-modeldescr-v2";

        // Mirror of CatiaAdapter: skip the ROOT assembly's combined STEP once the
        // tree is large (the whole-tree tessellation is the writer-stall risk).
        // Sub-assembly STEPs are always kept.
        private const int RootAssemblyStepMaxParts = 100;

        private readonly IpfcBaseSession _session;

        public CreoAdapter(IpfcBaseSession session)
        {
            _session = session;
        }

        public string CadName => "Creo Parametric";

        // Lowercase, leading-dot — used by shared code and the ExportStep filter.
        public IReadOnlyList<string> NativeFileExtensions => new[] { ".prt", ".asm" };

        // ---- GetActiveDocument ------------------------------------------------

        public CadDocument GetActiveDocument()
        {
            IpfcModel model;
            // session.CurrentModel is a PROPERTY (confirmed: pfcAssembliesExamples.vb
            // `model = session.CurrentModel`).
            try { model = _session.CurrentModel; }
            catch { return null; }
            if (model == null) return null;

            string filename = ModelFileName(model);
            return new CadDocument
            {
                FullPath = ResolveOnDiskPath(model),
                Name = filename,
                IsAssembly = ModelIsAssembly(model),
                NativeHandle = model,
            };
        }

        // ---- SaveDocument (save ALL modified, like CatiaAdapter) ---------------

        public void SaveDocument(CadDocument doc)
        {
            LogWalk($"SaveDocument v={WalkAssemblyVersion}: saving modified models in session");
            int saved = 0, total = 0;
            try
            {
                // VERIFY (vbapidoc: t-pfcSession-BaseSession.html): ListModels() -> IpfcModels.
                IpfcModels models = _session.ListModels();
                total = models.Count;
                for (int i = 0; i < total; i++)   // Creo sequences are 0-based (CATIA was 1-based)
                {
                    IpfcModel m;
                    try { m = models[i]; }
                    catch (Exception ex) { LogWalk($"  model[{i}] item threw: {ex.Message}"); continue; }
                    if (SaveOneIfModified(m)) saved++;
                }
            }
            catch (Exception ex) { LogWalk($"SaveDocument: ListModels iterate threw: {ex.Message}"); }

            // Belt-and-braces for the active model.
            try { if (SaveOneIfModified(doc?.NativeHandle as IpfcModel)) saved++; } catch { }
            LogWalk($"SaveDocument: saved {saved} of {total} model(s)");
        }

        private static bool SaveOneIfModified(IpfcModel m)
        {
            if (m == null) return false;
            // VERIFY (vbapidoc: t-pfcModel-Model.html): Creo's pfc may not expose a
            // clean per-model "is modified" flag. If it does, gate on it here to
            // avoid version churn. Absent one, Save() is safe (writes a new version
            // only when needed). Save() itself is the important part.
            try
            {
                // Gate on IsModified so untouched models don't get a new on-disk version
                // (confirmed property: IpfcModel.IsModified).
                bool modified = true;
                try { modified = m.IsModified; } catch { }
                if (!modified) return false;
                m.Save();
                LogWalk($"  saved '{ModelFileName(m)}'");
                return true;
            }
            catch (Exception ex)
            {
                LogWalk($"  save FAILED for '{ModelFileName(m)}': {ex.Message}");
                return false;
            }
        }

        // ---- OpenDocument -----------------------------------------------------

        public void OpenDocument(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;
            try
            {
                // descriptor from filename (confirmed: pfcAssembliesExamples.vb
                // `(New CCpfcModelDescriptor).CreateFromFileName(componentFileName)`),
                // then RetrieveModel + display. RetrieveModel wants the folder on the
                // search path, so we add it first for a bare-filename resolve.
                AddSearchFolder(Path.GetDirectoryName(filePath));
                IpfcModelDescriptor descr = new CCpfcModelDescriptor().CreateFromFileName(Path.GetFileName(filePath));
                IpfcModel model = _session.RetrieveModel(descr);
                if (model != null)
                {
                    // VERIFY (vbapidoc: t-pfcSession-BaseSession.html CreateModelWindow,
                    // t-pfcModel-Model.html Display): bring the model on screen.
                    IpfcWindow win = _session.CreateModelWindow(model);
                    win.Activate();
                    model.Display();
                }
            }
            catch (Exception ex) { LogWalk($"OpenDocument('{filePath}') failed: {ex.Message}"); }
        }

        // ---- WalkAssembly (the core) ------------------------------------------

        public List<AssemblyFileRef> WalkAssembly(CadDocument doc)
        {
            LogWalk($"WalkAssembly v={WalkAssemblyVersion} invoked");

            var rootModel = doc?.NativeHandle as IpfcModel
                            ?? throw new InvalidOperationException("No active model to walk");

            string rootPath = ResolveOnDiskPath(rootModel);
            if (string.IsNullOrEmpty(rootPath))
                throw new InvalidOperationException("Assembly has not been saved to disk yet — save it first");

            string rootFilename = ModelFileName(rootModel);
            var result = new List<AssemblyFileRef>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string rootPn = ResolvePartNumber(rootModel, rootFilename);
            result.Add(new AssemblyFileRef
            {
                FullPath = rootPath,
                RelativePath = rootFilename,
                Filename = rootFilename,
                IsRoot = true,
                PartNumber = rootPn,
                ParentPartNumber = null,
                NativeHandle = rootModel,
            });
            seenPaths.Add(rootPath);

            if (ModelIsAssembly(rootModel))
            {
                // Each level lists its own immediate component features and reaches
                // the child model directly via component.ModelDescr (no ComponentPath).
                WalkComponents(rootModel, rootPn, result, seenPaths);
            }
            LogWalk($"WalkAssembly: {result.Count} node(s)");
            return result;
        }

        // asm = the (sub-)assembly model whose immediate children we enumerate.
        private void WalkComponents(IpfcModel asm, string parentPn,
                                    List<AssemblyFileRef> output, HashSet<string> seenPaths)
        {
            IpfcFeatures comps;
            try
            {
                // ListFeaturesByType(VisibleOnly, EpfcFEATTYPE_COMPONENT) — arg order +
                // enum confirmed by pfcAssembliesExamples.vb (`assembly.ListFeaturesByType(
                // False, EpfcFeatureType.EpfcFEATTYPE_COMPONENT)`). A Model IS an IpfcSolid.
                comps = ((IpfcSolid)asm).ListFeaturesByType(true, EpfcFeatureType.EpfcFEATTYPE_COMPONENT);
            }
            catch (Exception ex) { LogWalk($"  ListFeaturesByType failed: {ex.Message}"); return; }
            if (comps == null) return;

            int count;
            try { count = comps.Count; } catch { return; }

            for (int i = 0; i < count; i++)
            {
                IpfcComponentFeat comp;
                // components.Item(i) -> IpfcComponentFeat (pfcAssembliesExamples.vb).
                try { comp = (IpfcComponentFeat)comps[i]; }
                catch { continue; }
                if (comp == null) continue;

                // Skip suppressed/unregenerated components.
                try
                {
                    // Status lives on IpfcFeature (IpfcComponentFeat inherits it, but the
                    // interop doesn't model that inheritance — cast). Returns an int code.
                    int st = ((IpfcFeature)comp).Status;
                    if (st != (int)EpfcFeatureStatus.EpfcFEAT_ACTIVE)
                    {
                        LogWalk($"  comp[{i}] status={st} — recorded as skip, not recursed");
                        output.Add(SkipNode(parentPn, "suppressed"));
                        continue;
                    }
                }
                catch { /* status unavailable — proceed */ }

                // Reach the child model directly through the component's model descriptor
                // (component.ModelDescr -> session.GetModelFromDescr; confirmed by
                // pfcAssembliesExamples.vb). No ComponentPath / intseq needed.
                IpfcModelDescriptor descr;
                try { descr = comp.ModelDescr; }
                catch (Exception ex) { LogWalk($"  comp[{i}] ModelDescr failed: {ex.Message}"); output.Add(SkipNode(parentPn, "no-path")); continue; }
                if (descr == null) { output.Add(SkipNode(parentPn, "no-path")); continue; }

                IpfcModel leaf = null;
                try { leaf = _session.GetModelFromDescr(descr); } catch { }
                if (leaf == null) { try { leaf = _session.RetrieveModel(descr); } catch { } }
                if (leaf == null)
                {
                    LogWalk($"  comp[{i}]: could not resolve leaf model from descriptor");
                    output.Add(SkipNode(parentPn, "no-path"));
                    continue;
                }

                string leafFile = ModelFileName(leaf);
                string leafPath = ResolveOnDiskPath(leaf);
                bool isAsm = ModelIsAssembly(leaf);

                string skipReason = null;
                if (string.IsNullOrEmpty(leafPath)) skipReason = "no-path";
                else if (!File.Exists(leafPath)) skipReason = "missing-file";

                // Dedup file-backed nodes by resolved path (first occurrence wins).
                if (!string.IsNullOrEmpty(leafPath) && !seenPaths.Add(leafPath))
                {
                    LogWalk($"  comp[{i}] '{leafFile}' — duplicate path, skipped");
                    continue;
                }

                string leafPn = ResolvePartNumber(leaf, leafFile);
                if (skipReason == null && string.IsNullOrEmpty(leafPn)) skipReason = "no-part-number";

                string display = string.IsNullOrEmpty(leafFile) ? "(unnamed component)" : leafFile;
                output.Add(new AssemblyFileRef
                {
                    FullPath = leafPath,
                    RelativePath = display,
                    Filename = display,
                    IsRoot = false,
                    PartNumber = leafPn,
                    ParentPartNumber = parentPn,
                    NativeHandle = leaf,
                    SkipReason = skipReason,
                });
                LogWalk($"  comp[{i}]: file='{leafFile}' pn='{leafPn}' isAsm={isAsm} skip='{skipReason}'");

                // Recurse into sub-assemblies (structure-driven, like CatiaAdapter).
                if (isAsm && skipReason != "suppressed")
                    WalkComponents(leaf, leafPn, output, seenPaths);
            }
        }

        private static AssemblyFileRef SkipNode(string parentPn, string reason) => new AssemblyFileRef
        {
            FullPath = null,
            RelativePath = null,
            Filename = null,
            IsRoot = false,
            PartNumber = null,
            ParentPartNumber = parentPn,
            NativeHandle = null,
            SkipReason = reason,
        };

        // ---- ExportStep (per-part; root size-gate) ----------------------------

        public List<AssemblyFileRef> ExportStep(CadDocument doc, IEnumerable<AssemblyFileRef> nativeFiles,
                                                string stagingDir, Action<int, int, string> progress = null)
        {
            Directory.CreateDirectory(stagingDir);
            var inputs = new List<AssemblyFileRef>(nativeFiles);
            var result = new List<AssemblyFileRef>();

            int geometryCount = 0;
            foreach (var gf in inputs)
                if (!string.IsNullOrEmpty(gf.Filename) && IsGeometry(gf.Filename)) geometryCount++;

            LogWalk($"ExportStep: {inputs.Count} candidate(s), {geometryCount} geometry file(s)");

            for (int i = 0; i < inputs.Count; i++)
            {
                var f = inputs[i];
                if (string.IsNullOrEmpty(f.Filename) || string.IsNullOrEmpty(f.FullPath)) continue;
                if (!IsGeometry(f.Filename)) continue;
                bool isAsm = Path.GetExtension(f.Filename).ToLowerInvariant() == ".asm";

                if (isAsm && f.IsRoot && geometryCount >= RootAssemblyStepMaxParts)
                {
                    LogWalk($"  SKIP root assembly STEP ({geometryCount} >= {RootAssemblyStepMaxParts})");
                    continue;
                }

                progress?.Invoke(i + 1, inputs.Count, f.Filename);

                string stepName = Path.GetFileNameWithoutExtension(f.Filename) + ".stp";
                string stepPath = Path.Combine(stagingDir, stepName);

                IpfcModel src = f.NativeHandle as IpfcModel;
                if (src == null)
                {
                    try
                    {
                        AddSearchFolder(Path.GetDirectoryName(f.FullPath));
                        IpfcModelDescriptor descr = new CCpfcModelDescriptor().CreateFromFileName(f.Filename);
                        src = _session.RetrieveModel(descr);
                    }
                    catch (Exception ex) { LogWalk($"  open '{f.Filename}' failed: {ex.Message}"); continue; }
                }
                if (src == null) continue;

                bool ok = false;
                try
                {
                    // model.Export(path, instructions) with a Create()-d instructions object is
                    // the confirmed pattern (pfcInterfaceExamples.vb: `model.Export(Nothing,
                    // vrmlInstructions)` where vrmlInstructions = (New CCpfc...).Create(...)).
                    // The STEP3D flavor + its Create() args are still VERIFY (vbapidoc:
                    //   t-pfcExport-STEP3DExportInstructions.html, t-pfcExport-GeometryFlags.html,
                    //   t-pfcExport-AssemblyConfiguration.html).
                    // Note: if a Creo build writes Export output to the working dir using the
                    // model name, set the CWD to stagingDir and harvest by name instead.
                    IpfcGeometryFlags flags = new CCpfcGeometryFlags().Create();
                    flags.AsSolids = true;   // VERIFY: property vs SetAsSolids(true)
                    IpfcSTEP3DExportInstructions instr =
                        new CCpfcSTEP3DExportInstructions().Create(
                            (int)EpfcAssemblyConfiguration.EpfcEXPORT_ASM_SINGLE_FILE, flags);
                    src.Export(stepPath, (IpfcExportInstructions)instr);
                    ok = File.Exists(stepPath);
                }
                catch (Exception ex) { LogWalk($"  Export '{f.Filename}' failed: {ex.Message}"); }

                if (!ok) continue;
                result.Add(new AssemblyFileRef
                {
                    FullPath = stepPath,
                    Filename = stepName,
                    RelativePath = stepName,
                    IsRoot = f.IsRoot,
                    PartNumber = f.PartNumber,
                });
            }
            LogWalk($"ExportStep: produced {result.Count} STEP file(s)");
            return result;
        }

        // ---- InsertComponent --------------------------------------------------

        public void InsertComponent(CadDocument activeAssembly, string filePath)
        {
            // Retrieve the component model then package it into the assembly. Matches
            // pfcAssembliesExamples.assembleByDatums: `assembly.AssembleComponent(
            // componentModel, Nothing)` (package, unconstrained). Constraining the
            // placement is a later refinement.
            try
            {
                var asm = activeAssembly?.NativeHandle as IpfcAssembly;
                if (asm == null) throw new InvalidOperationException("Active document is not an assembly");
                AddSearchFolder(Path.GetDirectoryName(filePath));
                IpfcModelDescriptor descr = new CCpfcModelDescriptor().CreateFromFileName(Path.GetFileName(filePath));
                IpfcSolid comp = _session.RetrieveModel(descr) as IpfcSolid;
                if (comp == null) throw new InvalidOperationException("Could not retrieve component");
                asm.AssembleComponent(comp, null);   // package (unconstrained)
            }
            catch (Exception ex)
            {
                LogWalk($"InsertComponent('{filePath}') failed: {ex.Message}");
                throw;
            }
        }

        // ---- ImportStepAsNative -----------------------------------------------

        public string ImportStepAsNative(string stpPath, string nativeOutPathHint)
        {
            // VERIFY (vbapidoc: t-pfcModel-ImportType.html, t-pfcSession-BaseSession.html
            //   ImportNewModel): the shipped example imports STEP as a FEATURE into an
            //   existing solid (solid.CreateImportFeat); creating a standalone native
            //   uses session.ImportNewModel. STEP imports as a part or assembly, so the
            //   extension isn't known ahead of time.
            try
            {
                string newName = Path.GetFileNameWithoutExtension(nativeOutPathHint);
                // Session.ImportNewModel(file, importFormat, newModelType, newName, filter).
                // A STEP file can land as a part or an assembly; GetImportSourceType reports
                // which so we create the right model type.
                int importFormat = (int)EpfcNewModelImportType.EpfcIMPORT_NEW_STEP;
                int newModelType;
                try { newModelType = _session.GetImportSourceType(stpPath, importFormat); }
                catch { newModelType = (int)EpfcModelType.EpfcMDL_PART; }
                IpfcModel imported =
                    _session.ImportNewModel(stpPath, importFormat, newModelType, newName, null);
                if (imported == null)
                    throw new InvalidOperationException($"Creo could not import STEP '{stpPath}'");

                imported.Save();   // saves under newName into the working dir
                string outPath = ResolveOnDiskPath(imported)
                                 ?? Path.Combine(Path.GetDirectoryName(nativeOutPathHint) ?? ".",
                                                 newName + "." + ModelExtension(imported));
                if (!File.Exists(outPath))
                    throw new InvalidOperationException($"STEP import produced no file at '{outPath}'");
                return outPath;
            }
            catch (Exception ex)
            {
                LogWalk($"ImportStepAsNative('{stpPath}') failed: {ex.Message}");
                throw;
            }
        }

        // ---- FindMissingComponents --------------------------------------------

        public List<MissingComponent> FindMissingComponents(CadDocument assembly)
        {
            var missing = new List<MissingComponent>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var rootModel = assembly?.NativeHandle as IpfcModel;
                if (rootModel == null || !ModelIsAssembly(rootModel)) return missing;
                CollectMissing(rootModel, missing, seen);
            }
            catch (Exception ex) { LogWalk($"FindMissingComponents failed: {ex.Message}"); }
            return missing;
        }

        private void CollectMissing(IpfcModel asm, List<MissingComponent> missing, HashSet<string> seen)
        {
            IpfcFeatures comps;
            try { comps = ((IpfcSolid)asm).ListFeaturesByType(true, EpfcFeatureType.EpfcFEATTYPE_COMPONENT); }
            catch { return; }
            if (comps == null) return;

            int count; try { count = comps.Count; } catch { return; }
            for (int i = 0; i < count; i++)
            {
                IpfcComponentFeat comp; IpfcModelDescriptor descr;
                try { comp = (IpfcComponentFeat)comps[i]; descr = comp.ModelDescr; } catch { continue; }
                if (descr == null) continue;

                IpfcModel leaf = null;
                try { leaf = _session.GetModelFromDescr(descr); } catch { }
                if (leaf == null) continue;   // truly-missing members usually can't be enumerated

                string file = ModelFileName(leaf);
                string path = ResolveOnDiskPath(leaf);
                if (!string.IsNullOrEmpty(path) && !File.Exists(path) && seen.Add(path))
                {
                    missing.Add(new MissingComponent
                    {
                        Filename = file,
                        ExpectedPath = path,
                        PartNumber = PartNumberParser.ParseOrNull(file),
                    });
                }
                if (ModelIsAssembly(leaf))
                    CollectMissing(leaf, missing, seen);
            }
        }

        // ---- AddSearchFolder --------------------------------------------------

        public void AddSearchFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return;
            try
            {
                // VERIFY (vbapidoc: t-pfcSession-BaseSession.html SetConfigOption):
                // Creo's search path is the multi-valued "search_path" config option
                // (search.pro). SetConfigOption typically APPENDS a search_path line.
                _session.SetConfigOption("search_path", folderPath);
            }
            catch (Exception ex) { LogWalk($"AddSearchFolder('{folderPath}') non-fatal: {ex.Message}"); }
        }

        // ---- ReloadActiveDocument ---------------------------------------------

        public void ReloadActiveDocument()
        {
            try
            {
                IpfcModel m = _session.CurrentModel;
                if (m == null) return;
                string file = ModelFileName(m);
                // VERIFY (t-pfcModel-Model.html Erase / t-pfcSession-BaseSession.html):
                // erase from session then retrieve fresh from disk.
                try { m.Erase(); } catch { }
                IpfcModelDescriptor descr = new CCpfcModelDescriptor().CreateFromFileName(file);
                IpfcModel fresh = _session.RetrieveModel(descr);
                if (fresh != null)
                {
                    IpfcWindow win = _session.CreateModelWindow(fresh);
                    win.Activate();
                    fresh.Display();
                }
            }
            catch (Exception ex) { LogWalk($"ReloadActiveDocument failed: {ex.Message}"); }
        }

        // ---- Part-number resolution (param first, then filename) --------------

        private static string ResolvePartNumber(IpfcModel model, string filename)
        {
            string fromParam = ReadModelParam(model, "PART_NUMBER");
            if (!string.IsNullOrWhiteSpace(fromParam) && PartNumberParser.LooksValid(fromParam))
                return fromParam.Trim().ToUpperInvariant();
            return PartNumberParser.ParseOrNull(filename);
        }

        private static string ReadModelParam(IpfcModel model, string key)
        {
            if (model == null) return null;
            try
            {
                // VERIFY (vbapidoc: t-pfcModelItem-Parameter.html, t-pfcModelItem-ParamValue.html):
                //   IpfcParameter p = ((IpfcParameterOwner)model).GetParam(key);   // null if absent
                //   IpfcParamValue v = p.Value;                                     // property or GetValue()
                //   if (v.discr == EpfcPARAM_STRING) return v.StringValue;
                var owner = model as IpfcParameterOwner;
                if (owner == null) return null;
                IpfcParameter p = owner.GetParam(key);   // null if absent
                if (p == null) return null;
                // Value is declared on IpfcBaseParameter; the interop doesn't surface that
                // inheritance on IpfcParameter, so cast. discr is an int discriminator.
                IpfcParamValue v = ((IpfcBaseParameter)p).Value;
                if (v == null) return null;
                if (v.discr == (int)EpfcParamValueType.EpfcPARAM_STRING) return v.StringValue;
                return null;
            }
            catch { return null; }
        }

        // ---- Model helpers ----------------------------------------------------

        private static string ModelFileName(IpfcModel m)
        {
            try { return m.FileName; }   // VERIFY (t-pfcModel-Model.html): FileName -> "top.asm"
            catch { return null; }
        }

        private static bool ModelIsAssembly(IpfcModel m)
        {
            try { return m.Type == (int)EpfcModelType.EpfcMDL_ASSEMBLY; }   // Type is an int code
            catch
            {
                string f = ModelFileName(m);
                return f != null && f.EndsWith(".asm", StringComparison.OrdinalIgnoreCase);
            }
        }

        private static string ModelExtension(IpfcModel m)
        {
            bool asm = ModelIsAssembly(m);
            return asm ? "asm" : "prt";
        }

        // Resolve the actual on-disk path of a model, accounting for Creo's version
        // suffixes (top.asm.3). Returns the highest-numbered existing file, or the
        // bare <dir>\<filename> if no versioned file is found.
        private static string ResolveOnDiskPath(IpfcModel m)
        {
            if (m == null) return null;
            try
            {
                // Descr.Path is the directory (blank until the model is saved); FileName is
                // "top.asm" (no version). If the descriptor path is blank, fall back to
                // Origin — the full path the model was last retrieved from.
                IpfcModelDescriptor d = m.Descr;
                string dir = null;
                try { dir = d?.Path; } catch { }
                string file = ModelFileName(m);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file))
                {
                    try { string origin = m.Origin; if (!string.IsNullOrEmpty(origin)) return origin; }
                    catch { }
                    return null;
                }
                return NewestVersionedFile(dir, file);
            }
            catch { return null; }
        }

        private static string NewestVersionedFile(string dir, string baseFile)
        {
            try
            {
                string exact = Path.Combine(dir, baseFile);
                string best = null; int bestVer = -1;
                foreach (string p in Directory.EnumerateFiles(dir, baseFile + ".*"))
                {
                    string suffix = Path.GetExtension(p).TrimStart('.');   // the ".N" after "top.asm"
                    if (int.TryParse(suffix, out int v) && v > bestVer) { bestVer = v; best = p; }
                }
                if (best != null) return best;
                if (File.Exists(exact)) return exact;
                return exact;   // report the canonical path even if not yet on disk
            }
            catch { return Path.Combine(dir, baseFile); }
        }

        private static bool IsGeometry(string filename)
        {
            string ext = Path.GetExtension(filename)?.ToLowerInvariant();
            return ext == ".prt" || ext == ".asm";
        }

        // ---- Diagnostic log (mirrors CatiaAdapter) ----------------------------

        private static void LogWalk(string line)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AtlasCad");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "walk_assembly.log"),
                    $"--- {DateTime.Now:O} CreoAdapter.{line}\n");
            }
            catch { }
        }
    }
}
