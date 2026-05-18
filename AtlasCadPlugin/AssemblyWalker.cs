using System;
using System.Collections.Generic;
using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace AtlasCadPlugin
{
    public class AssemblyFileRef
    {
        public string FullPath;
        public string RelativePath;
        public string Filename;
        public bool IsRoot;
    }

    /// <summary>
    /// Walks a SolidWorks assembly's component tree and returns the flat list
    /// of files we need to upload. Relative paths are preserved so SolidWorks
    /// can resolve references when the assembly is re-opened on another machine.
    /// </summary>
    public static class AssemblyWalker
    {
        public static List<AssemblyFileRef> Walk(IModelDoc2 doc)
        {
            if (doc == null)
                throw new InvalidOperationException("No active document");

            int docType = doc.GetType();
            if (docType != (int)swDocumentTypes_e.swDocASSEMBLY)
                throw new InvalidOperationException("Active document is not an assembly");

            string rootPath = doc.GetPathName();
            if (string.IsNullOrEmpty(rootPath))
                throw new InvalidOperationException("Assembly has not been saved to disk yet — save it first");

            string rootDir = Path.GetDirectoryName(rootPath);
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<AssemblyFileRef>();

            result.Add(new AssemblyFileRef
            {
                FullPath = rootPath,
                RelativePath = Path.GetFileName(rootPath),
                Filename = Path.GetFileName(rootPath),
                IsRoot = true,
            });
            seenPaths.Add(rootPath);

            AssemblyDoc asm = (AssemblyDoc)doc;
            // GetComponents(false) returns all components recursively (top-level + nested).
            object[] components = (object[])asm.GetComponents(false);
            if (components == null) return result;

            foreach (object comp in components)
            {
                Component2 c = comp as Component2;
                if (c == null) continue;
                if (c.IsSuppressed()) continue;

                string fullPath = c.GetPathName();
                if (string.IsNullOrEmpty(fullPath)) continue;
                if (!File.Exists(fullPath)) continue;
                if (seenPaths.Contains(fullPath)) continue;
                seenPaths.Add(fullPath);

                result.Add(new AssemblyFileRef
                {
                    FullPath = fullPath,
                    // Always use bare filename. SolidWorks-imported STEP assemblies
                    // place children in %LOCALAPPDATA%\Temp\swx****\, which would
                    // produce ".." path-traversal segments that break S3 presigned URLs.
                    // SolidWorks resolves children by filename in the assembly's
                    // directory on re-open, so flat layout works for our use case.
                    RelativePath = Path.GetFileName(fullPath),
                    Filename = Path.GetFileName(fullPath),
                    IsRoot = false,
                });
            }

            return result;
        }
    }
}
