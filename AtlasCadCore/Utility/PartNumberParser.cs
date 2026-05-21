using System.IO;
using System.Text.RegularExpressions;

namespace AtlasCadCore.Utility
{
    /// <summary>
    /// 10-char alphanumeric part_number matcher.
    /// Format: CAT(1) MDL(1) MAJ(1) MIN(1) SEQ(4) REV(2)
    ///
    /// Filenames in the wild come in two shapes:
    ///   1. exact 10-char names: "AN5T01040A.sldasm"
    ///   2. part_number as a prefix with descriptive suffix:
    ///      "AN5T01040A_ECO2.0 DOOR ASSY RH.sldasm"
    /// Both shapes resolve to the same part_number.
    /// </summary>
    public static class PartNumberParser
    {
        // Whole filename is exactly the 10-char code.
        private static readonly Regex ExactPattern = new Regex("^[A-Z0-9]{10}$");
        // Filename starts with the 10-char code, followed by a non-alphanumeric
        // separator (`_`, ` `, `-`, etc.) or end-of-string. Bounded so we don't
        // greedy-match the first 10 of a longer alphanumeric run.
        private static readonly Regex LeadingPattern = new Regex(@"^([A-Z0-9]{10})(?:[^A-Z0-9]|$)");

        public static string ParseOrNull(string filename)
        {
            if (string.IsNullOrEmpty(filename)) return null;
            string bare = Path.GetFileNameWithoutExtension(filename).Trim().ToUpperInvariant();
            if (ExactPattern.IsMatch(bare)) return bare;
            var match = LeadingPattern.Match(bare);
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// Extract any leading alphanumeric code from a filename, not just
        /// the strict 10-char form. Used by the Resolve-from-Atlas flow when
        /// project filenames use shorter codes (e.g. "EL530012_HEXAGON..."
        /// where "EL530012" is the part_number stem and atlas stores the
        /// full code with a revision suffix like "EL5300120A").
        /// Returns the leading alphanumeric run if it's at least `minLength`
        /// chars, otherwise null.
        /// </summary>
        public static string ExtractLeadingCode(string filename, int minLength = 6)
        {
            if (string.IsNullOrEmpty(filename)) return null;
            string bare = Path.GetFileNameWithoutExtension(filename).Trim().ToUpperInvariant();
            var match = LeadingCodePattern.Match(bare);
            if (!match.Success) return null;
            return match.Value.Length >= minLength ? match.Value : null;
        }

        private static readonly Regex LeadingCodePattern = new Regex("^[A-Z0-9]+");

        public static bool LooksValid(string candidate)
        {
            if (string.IsNullOrEmpty(candidate)) return false;
            return ExactPattern.IsMatch(candidate.Trim().ToUpperInvariant());
        }

        /// <summary>
        /// Derive release_type from the 2-char revision suffix:
        ///   - last char is a letter      → PROTO        (0A, 0B, ...)
        ///   - numeric, value &lt; 20     → PRODUCTION   (00, 01, ..., 19)
        ///   - numeric, value &gt;= 20    → ALTERNATE_PART (20, 21, ...)
        /// Returns null if the part_number doesn't look valid.
        /// </summary>
        public static string ReleaseTypeFromPartNumber(string partNumber)
        {
            if (!LooksValid(partNumber)) return null;
            string suffix = partNumber.Substring(8, 2);
            if (char.IsLetter(suffix[1])) return "PROTO";
            if (!int.TryParse(suffix, out int n)) return null;
            return n >= 20 ? "ALTERNATE_PART" : "PRODUCTION";
        }
    }
}
