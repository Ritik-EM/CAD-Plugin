using System.IO;
using System.Text.RegularExpressions;

namespace AtlasCadPlugin
{
    /// <summary>
    /// Parses an Atlas part_number out of a SLDPRT/SLDASM/STP filename.
    ///
    /// Format from atlas-api/app/api/part_master/v1/manager.py:
    ///   CAT(1) MDL(1) MAJ(1) MIN(1) SEQ(4) REV(2) = 10 alphanumeric chars.
    /// Example: "ELABC123400" → category E, model L, major A, minor B,
    /// sequence 1234, revision 00.
    ///
    /// Returns null if the bare filename doesn't match — the user gets
    /// prompted via AssignPartNumbersForm to correct it.
    /// </summary>
    public static class PartNumberParser
    {
        // Loose match by length + charset; backend's lookup endpoint confirms
        // the string actually exists in part_master_library.
        private static readonly Regex Pattern = new Regex("^[A-Z0-9]{10}$");

        public static string ParseOrNull(string filename)
        {
            if (string.IsNullOrEmpty(filename)) return null;
            string bare = Path.GetFileNameWithoutExtension(filename).Trim().ToUpperInvariant();
            return Pattern.IsMatch(bare) ? bare : null;
        }

        public static bool LooksValid(string candidate)
        {
            if (string.IsNullOrEmpty(candidate)) return false;
            return Pattern.IsMatch(candidate.Trim().ToUpperInvariant());
        }
    }
}
