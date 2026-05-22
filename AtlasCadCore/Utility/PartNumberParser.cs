using System.IO;
using System.Text.RegularExpressions;

namespace AtlasCadCore.Utility
{
    /// <summary>
    /// 10-char alphanumeric part_number matcher.
    /// Format: CAT(1) MDL(1) MAJ(1) MIN(1) SEQ(4) REV(2)
    ///
    /// Filenames in the wild come in four shapes:
    ///   1. exact 10-char names: "AN5T01040A.sldasm"
    ///   2. part_number as a prefix with descriptive suffix:
    ///      "AN5T01040A_ECO2.0 DOOR ASSY RH.sldasm"
    ///   3. 8-char base code + underscore + 2-char revision + underscore + desc:
    ///      "EL530011_00_HEX WELDNUT M5x0.81.sldprt" → "EL53001100"
    ///      (legacy convention for standard-hardware parts).
    ///   4. 8-char base code alone (no explicit revision in the filename):
    ///      "EL530012_HEXAGON WELD NUT.sldprt" → "EL53001200"
    ///      Pads with "00" to match atlas's PRODUCTION-rev-00 convention,
    ///      keeping Check In's filename → part_number resolution symmetric
    ///      with the Check Out / Resolve-from-Atlas flow that already
    ///      tries the same code+"00" lookup against atlas.
    /// All four shapes resolve to the same 10-char canonical part_number.
    /// </summary>
    public static class PartNumberParser
    {
        // Whole filename is exactly the 10-char code.
        private static readonly Regex ExactPattern = new Regex("^[A-Z0-9]{10}$");
        // Filename starts with the 10-char code, followed by a non-alphanumeric
        // separator (`_`, ` `, `-`, etc.) or end-of-string. Bounded so we don't
        // greedy-match the first 10 of a longer alphanumeric run.
        private static readonly Regex LeadingPattern = new Regex(@"^([A-Z0-9]{10})(?:[^A-Z0-9]|$)");
        // 8-char base + underscore + 2-char revision. Some legacy filenames
        // (mostly standard hardware) store the 10-char part_number with an
        // underscore between the base code and the 2-char revision, then
        // another underscore before the description. We strip the inner
        // underscore to get back the canonical 10-char form.
        private static readonly Regex BaseRevPattern = new Regex(
            @"^([A-Z0-9]{8})_([A-Z0-9]{2})(?:[^A-Z0-9]|$)");
        // 8-char base code with no explicit revision: pad with "00". Used as
        // the last-resort match (after the strict 10-char and 8+_+2 forms)
        // because false positives are caught by the subsequent atlas lookup
        // anyway — and the alternative is silently dropping standard-hardware
        // components from the check-in tree.
        private static readonly Regex BareBasePattern = new Regex(
            @"^([A-Z0-9]{8})(?:[^A-Z0-9]|$)");

        public static string ParseOrNull(string filename)
        {
            if (string.IsNullOrEmpty(filename)) return null;
            string bare = Path.GetFileNameWithoutExtension(filename).Trim().ToUpperInvariant();
            if (ExactPattern.IsMatch(bare)) return bare;
            var leading = LeadingPattern.Match(bare);
            if (leading.Success) return leading.Groups[1].Value;
            var baseRev = BaseRevPattern.Match(bare);
            if (baseRev.Success) return baseRev.Groups[1].Value + baseRev.Groups[2].Value;
            var bareBase = BareBasePattern.Match(bare);
            if (bareBase.Success) return bareBase.Groups[1].Value + "00";
            return null;
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
