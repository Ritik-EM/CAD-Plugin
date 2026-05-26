using System.IO;
using System.Text.RegularExpressions;

namespace AtlasCadCore.Utility
{
    public static class PartNumberParser
    {
        private static readonly Regex ExactPattern = new Regex("^[A-Z0-9]{10}$");
        private static readonly Regex LeadingPattern = new Regex(@"^([A-Z0-9]{10})(?:[^A-Z0-9]|$)");
        private static readonly Regex BaseRevPattern = new Regex(
            @"^([A-Z0-9]{8})_([A-Z0-9]{2})(?:[^A-Z0-9]|$)");
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
