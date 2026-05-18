using System;
using System.Collections.Generic;
using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace AtlasCadPlugin
{
    /// <summary>
    /// Generates STEP (AP214) exports for every SLDPRT/SLDASM in an assembly.
    /// Used during upload + check-in so the part_master_library always carries
    /// a portable STEP alongside the native SolidWorks file.
    ///
    /// STEPs are written to a temp folder; the caller adds them to the upload
    /// tree (with PartNumber matching the source file) and is responsible for
    /// cleaning up after the upload.
    /// </summary>
    public static class StepExporter
    {
        /// <summary>
        /// For each native file in the tree, open it via SolidWorks (if not
        /// already loaded), call SaveAs(*.stp), and return new AssemblyFileRef
        /// entries representing the STEP files. The returned refs carry the
        /// same PartNumber as the source so the backend can co-locate them.
        /// </summary>
        public static List<AssemblyFileRef> Export(
            ISldWorks swApp,
            IEnumerable<AssemblyFileRef> nativeFiles,
            string stagingDir)
        {
            Directory.CreateDirectory(stagingDir);

            // STEP options — AP214 is the modern standard with assembly + colour support.
            swApp.SetUserPreferenceIntegerValue(
                (int)swUserPreferenceIntegerValue_e.swStepAP, 214);

            var result = new List<AssemblyFileRef>();
            foreach (var f in nativeFiles)
            {
                string ext = Path.GetExtension(f.Filename).ToLowerInvariant();
                if (ext != ".sldprt" && ext != ".sldasm") continue;

                string stepName = Path.GetFileNameWithoutExtension(f.Filename) + ".stp";
                string stepPath = Path.Combine(stagingDir, stepName);

                // Component model may already be loaded by the active assembly;
                // OpenDoc6 is idempotent for files already in the SolidWorks session.
                int loadErrors = 0, loadWarnings = 0;
                int docType = ext == ".sldasm"
                    ? (int)swDocumentTypes_e.swDocASSEMBLY
                    : (int)swDocumentTypes_e.swDocPART;
                IModelDoc2 doc = (IModelDoc2)swApp.OpenDoc6(
                    f.FullPath, docType,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                    "", ref loadErrors, ref loadWarnings);
                if (doc == null) continue;

                int saveErrors = 0, saveWarnings = 0;
                bool ok = doc.Extension.SaveAs(
                    stepPath,
                    (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent
                        | (int)swSaveAsOptions_e.swSaveAsOptions_Copy,
                    null, ref saveErrors, ref saveWarnings);

                if (!ok || !File.Exists(stepPath)) continue;

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

        public static void Cleanup(string stagingDir)
        {
            try
            {
                if (Directory.Exists(stagingDir))
                    Directory.Delete(stagingDir, recursive: true);
            }
            catch { /* temp dir cleanup is best-effort */ }
        }
    }
}
