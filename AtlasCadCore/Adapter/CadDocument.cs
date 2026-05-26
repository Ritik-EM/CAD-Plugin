namespace AtlasCadCore.Adapter
{
    public class CadDocument
    {
        public string FullPath { get; set; }
        public string Name { get; set; }
        public bool IsAssembly { get; set; }
        public object NativeHandle { get; set; }
    }
}
