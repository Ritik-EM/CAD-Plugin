namespace AtlasCadCore.Adapter
{
    /// <summary>
    /// Flat record describing one file to upload from an assembly. Populated
    /// by the per-CAD adapter (which walks the native assembly tree) and
    /// consumed by the upload/checkin orchestrator.
    /// </summary>
    public class AssemblyFileRef
    {
        public string FullPath;
        public string RelativePath;
        public string Filename;
        public bool IsRoot;
        // Resolved by the adapter — prefers a CAD-specific "PART_NUMBER"
        // custom property, falls back to filename parsing. Null = unknown;
        // upload flow will prompt the user to create a part_master entry.
        public string PartNumber;
        // PartNumber of the immediate parent assembly. Null for the root.
        // Adapters MUST populate this on recursive walks so the check-in
        // flow can compute ancestor-of-changed for revision propagation.
        public string ParentPartNumber;
        // Hex SHA-256 of the file bytes. Computed by Core (not the adapter)
        // because it's CAD-agnostic — same algorithm for SLDPRT, CATPart, PRT.
        public string Sha256;
        // Adapter-specific live CAD object (e.g. IModelDoc2 for SW). Set
        // during WalkAssembly so a later ExportStep can call SaveAs on the
        // already-loaded document instead of paying OpenDoc6 cost again.
        // Null when the document isn't open (e.g. suppressed components).
        public object NativeHandle;

        // Non-null = this entry SHOULDN'T be uploaded but is included so the
        // check-in flow can surface "N components dropped from the tree" with
        // a reason the user can act on (suppress, fix path, set PART_NUMBER).
        // Empty/null means the entry is healthy.
        // Values: "suppressed", "missing-file", "no-path", "no-part-number".
        public string SkipReason;
    }
}
