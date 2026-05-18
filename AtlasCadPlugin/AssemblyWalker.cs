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
        // Parsed from filename when the bare name matches the part_master_library
        // scheme (10 alphanumeric chars). May be edited by the user via
        // AssignPartNumbersForm before upload. Null means "unknown".
        public string PartNumber;
        // SHA-256 of file bytes, hex lowercase. Computed during upload for
        // integrity verification on the backend.
        public string Sha256;
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

            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<AssemblyFileRef>();

            string rootFilename = Path.GetFileName(rootPath);
            result.Add(new AssemblyFileRef
            {
                FullPath = rootPath,
                RelativePath = rootFilename,
                Filename = rootFilename,
                IsRoot = true,
                // Prefer the in-file PART_NUMBER custom property when present —
                // it's the canonical identifier. Filename parsing is fallback.
                PartNumber = ResolvePartNumber(doc, rootFilename),
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

                string childFilename = Path.GetFileName(fullPath);
                IModelDoc2 compDoc = c.GetModelDoc2() as IModelDoc2;
                result.Add(new AssemblyFileRef
                {
                    FullPath = fullPath,
                    // Always use bare filename. SolidWorks-imported STEP assemblies
                    // place children in %LOCALAPPDATA%\Temp\swx****\, which would
                    // produce ".." path-traversal segments that break S3 presigned URLs.
                    // SolidWorks resolves children by filename in the assembly's
                    // directory on re-open, so flat layout works for our use case.
                    RelativePath = childFilename,
                    Filename = childFilename,
                    IsRoot = false,
                    PartNumber = ResolvePartNumber(compDoc, childFilename),
                });
            }

            return result;
        }

        private static string ResolvePartNumber(IModelDoc2 doc, string filename)
        {
            string fromProperty = CustomProperties.Read(doc, CustomProperties.PartNumberKey);
            if (!string.IsNullOrWhiteSpace(fromProperty) && PartNumberParser.LooksValid(fromProperty))
                return fromProperty.Trim().ToUpperInvariant();
            return PartNumberParser.ParseOrNull(filename);
        }
    }
}
