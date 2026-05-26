using System;
using System.Collections.Generic;

namespace AtlasCadCore.Adapter
{
    public interface ICadAdapter
    {
        string CadName { get; }
        IReadOnlyList<string> NativeFileExtensions { get; }
        CadDocument GetActiveDocument();
        void SaveDocument(CadDocument doc);
        void OpenDocument(string filePath);
        List<AssemblyFileRef> WalkAssembly(CadDocument doc);

        List<AssemblyFileRef> ExportStep(CadDocument doc, IEnumerable<AssemblyFileRef> nativeFiles, string stagingDir, Action<int, int, string> progress = null);

        void InsertComponent(CadDocument activeAssembly, string filePath);
        string ImportStepAsNative(string stpPath, string nativeOutPathHint);

        List<MissingComponent> FindMissingComponents(CadDocument assembly);
        void AddSearchFolder(string folderPath);

        void ReloadActiveDocument();
    }
}
