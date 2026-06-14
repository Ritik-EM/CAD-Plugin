using System.Collections.Generic;

namespace AtlasCadPlugin.Altium
{
    /// <summary>
    /// Mirror of the JSON that AtlasCheckin.pas writes (see AtlasAltium/README.md).
    /// Newtonsoft deserializes snake_case field names directly (same convention as the
    /// AtlasCadCore DTOs — public fields, no JsonProperty attributes needed).
    /// </summary>
    public class AltiumManifest
    {
        public int schema_version;
        public string operation;     // "checkin" | "upload"
        public string part_code;
        public string project_name;
        public string comment;
        public List<ManifestFile> source_files = new List<ManifestFile>();
        public List<ManifestArtifact> artifacts = new List<ManifestArtifact>();
        public List<string> warnings = new List<string>();
    }

    public class ManifestFile
    {
        public string path;
        public string role;     // project | schematic | pcb | library | other
        public string bucket;   // file | managed | database
        public string warning;
    }

    public class ManifestArtifact
    {
        public string path;
        public string kind;     // bom | pdf | gerber | step
    }

    /// <summary>Written back to result.json for the DelphiScript to read + display.</summary>
    public class AltiumResult
    {
        public bool ok;
        public string operation;
        public string part_code;
        public string message;
        public List<string> bumped = new List<string>();
        public List<string> warnings = new List<string>();
    }
}
