namespace AtlasCadCore.Adapter
{
    /// <summary>
    /// One child reference inside an open assembly whose file isn't on
    /// this machine's disk. Produced by ICadAdapter.FindMissingComponents
    /// so the Resolve-from-Atlas flow can look each up by PartNumber, pull
    /// the right file from atlas, and unblock the user.
    /// </summary>
    public class MissingComponent
    {
        /// <summary>Bare filename SW was looking for, e.g. "AN5T01050A_ECO2.0 DOOR OUTER RH.sldprt".</summary>
        public string Filename;
        /// <summary>Full path SW reported as the expected location (may not exist on disk).</summary>
        public string ExpectedPath;
        /// <summary>10-char part_number extracted from the filename, or null if unparseable.</summary>
        public string PartNumber;
    }
}
