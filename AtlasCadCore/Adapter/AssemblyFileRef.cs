namespace AtlasCadCore.Adapter
{
    public class AssemblyFileRef
    {
        public string FullPath;
        public string RelativePath;
        public string Filename;
        public bool IsRoot;
        public string PartNumber;
        public string ParentPartNumber;
        // Optional human description detected alongside the part number (e.g. from a
        // "<PartNumber>_<Description>" filename). Flows to the upload's
        // detected_description so "Create New" can pre-fill it. Null when unknown.
        public string Description;
        public string Sha256;
        public object NativeHandle;
        public string SkipReason;
    }
}
