namespace AtlasCadCore.Adapter
{
    /// <summary>
    /// Adapter-neutral handle to a currently-open CAD document. Wraps the
    /// CAD-specific object (IModelDoc2 / PartDocument / Part) so the
    /// orchestrator can pass it back to adapter methods without leaking
    /// the underlying type into Core.
    /// </summary>
    public class CadDocument
    {
        public string FullPath { get; set; }
        public string Name { get; set; }
        public bool IsAssembly { get; set; }
        // The CAD-specific object. Each adapter casts back to its native
        // type (IModelDoc2 for SW, MECMOD.PartDocument or ProductDocument
        // for CATIA, NXOpen.Part for NX) inside its own methods.
        public object NativeHandle { get; set; }
    }
}
