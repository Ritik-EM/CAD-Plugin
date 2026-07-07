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

        // Filename convention "<PartNumber>_<Description>", e.g.
        // "CA5E121200_FLAT BED 330.prt" or "CA5E1212_load body.prt". The part number
        // is the token BEFORE the first underscore. Returns it normalised to a 10-char
        // Atlas code (an 8-char base gets the default "00" revision) when the token is
        // a valid code, else null. Unlike ParseOrNull this never mistakes the start of
        // the description for a revision suffix.
        public static string PartNumberFromFilenameConvention(string filename)
            => NormalizeOrNull(LeadingTokenBeforeUnderscore(filename));

        // Normalise a raw part-number string (from a parameter or filename) to a 10-char
        // Atlas code: a 10-char code is used as-is; an 8-char base gets the default "00"
        // revision (e.g. "TEST0005" -> "TEST000500"). Returns null for anything else.
        private static readonly Regex BasePattern = new Regex("^[A-Z0-9]{8}$");
        public static string NormalizeOrNull(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return null;
            string s = candidate.Trim().ToUpperInvariant();
            if (ExactPattern.IsMatch(s)) return s;                 // 10-char full code
            if (BasePattern.IsMatch(s)) return s + "00";           // 8-char base -> default rev
            return null;
        }

        // The description portion of a "<PartNumber>_<Description>" filename: everything
        // after the first underscore, trimmed and with original casing. Null when there
        // is no underscore (or nothing follows it).
        public static string DescriptionFromFilenameConvention(string filename)
        {
            if (string.IsNullOrEmpty(filename)) return null;
            string bare = Path.GetFileNameWithoutExtension(filename);
            int us = bare.IndexOf('_');
            if (us < 0 || us + 1 >= bare.Length) return null;
            string desc = bare.Substring(us + 1).Trim();
            return string.IsNullOrEmpty(desc) ? null : desc;
        }

        private static string LeadingTokenBeforeUnderscore(string filename)
        {
            if (string.IsNullOrEmpty(filename)) return null;
            string bare = Path.GetFileNameWithoutExtension(filename).Trim().ToUpperInvariant();
            int us = bare.IndexOf('_');
            string token = (us > 0 ? bare.Substring(0, us) : bare).Trim();
            return token.Length == 0 ? null : token;
        }

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
