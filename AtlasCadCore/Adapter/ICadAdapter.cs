using System;
using System.Collections.Generic;

namespace AtlasCadCore.Adapter
{
    /// <summary>
    /// Every per-CAD plugin implements this interface. Core takes an
    /// ICadAdapter and drives upload / checkin / browse-and-insert without
    /// knowing which CAD it's talking to.
    ///
    /// All methods MUST be safe to call from a background thread *except*
    /// where noted; CAD APIs are usually STA-only so adapters are responsible
    /// for marshalling back to the UI thread internally when needed.
    /// </summary>
    public interface ICadAdapter
    {
        /// <summary>Display name — "SolidWorks", "CATIA V5", "NX". Shown in UI.</summary>
        string CadName { get; }

        /// <summary>
        /// Native file extensions in priority order. The browse / checkout
        /// flows use this to pick which file to download from a part_master
        /// revision's reference_documents — e.g. SolidWorks prefers
        /// ".sldasm" over ".sldprt" when both exist.
        /// </summary>
        IReadOnlyList<string> NativeFileExtensions { get; }

        /// <summary>Returns null if no document is open.</summary>
        CadDocument GetActiveDocument();

        /// <summary>Persist any in-memory edits to disk. Called before walk/hash.</summary>
        void SaveDocument(CadDocument doc);

        /// <summary>Open a file (typically after download) into the CAD session.</summary>
        void OpenDocument(string filePath);

        /// <summary>
        /// Walk the assembly tree of `doc` and return every distinct file
        /// (root + recursive children). For each entry, the adapter should:
        ///   1. Set FullPath to the on-disk location.
        ///   2. Set Filename / RelativePath (bare filename is fine — see SolidWorksAdapter comment).
        ///   3. Resolve PartNumber from the CAD's custom-properties first,
        ///      falling back to PartNumberParser.ParseOrNull(filename).
        /// </summary>
        List<AssemblyFileRef> WalkAssembly(CadDocument doc);

        /// <summary>
        /// Generate a STEP (AP214) export for each native file in `nativeFiles`.
        /// Returns new AssemblyFileRef entries pointing at the .stp files.
        /// Each STEP ref carries the same PartNumber as its source so the
        /// backend can co-locate them.
        ///
        /// `progress` (if non-null) is invoked once per file BEFORE the slow
        /// SaveAs call with (currentIndex, totalCount, filename) — used by
        /// the UI to update its progress bar so the user can see which child
        /// is currently being exported.
        /// </summary>
        List<AssemblyFileRef> ExportStep(
            CadDocument doc,
            IEnumerable<AssemblyFileRef> nativeFiles,
            string stagingDir,
            Action<int, int, string> progress = null);

        /// <summary>
        /// Insert a downloaded part file into the active assembly at origin.
        /// `activeAssembly` is guaranteed to be IsAssembly = true.
        /// </summary>
        void InsertComponent(CadDocument activeAssembly, string filePath);

        /// <summary>
        /// Import a STEP file and save it as a native CAD file. Used by the
        /// contribute-native flow when a part_master only has a STP reference:
        /// plugin downloads the STP, calls this to materialise a native file,
        /// then uploads the native back via /part-master/upload.
        ///
        /// `nativeOutPathHint` suggests a target path; the adapter may pick
        /// a different extension (e.g. ".sldasm" instead of ".sldprt" if the
        /// STEP turns out to contain an assembly). The actual saved path is
        /// returned.
        /// </summary>
        string ImportStepAsNative(string stpPath, string nativeOutPathHint);

        /// <summary>
        /// Inspect the open assembly and return any child component whose
        /// file isn't on disk. The Resolve-from-Atlas flow uses this list
        /// to pull each missing file from atlas by part_number.
        /// </summary>
        List<MissingComponent> FindMissingComponents(CadDocument assembly);

        /// <summary>
        /// Add a folder to the CAD's reference-resolution search path. When
        /// the user reloads the assembly, the CAD looks in this folder for
        /// any child whose explicit path didn't resolve.
        /// </summary>
        void AddSearchFolder(string folderPath);

        /// <summary>Reload the active document from disk (used after Resolve to pick up downloaded children).</summary>
        void ReloadActiveDocument();
    }
}
