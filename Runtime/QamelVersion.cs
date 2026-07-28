namespace QamelCapture
{
    /// <summary>
    /// Semver comparison for "is the installed plugin behind?". Deliberately
    /// small: it only has to order the versions Qamel itself publishes, and be
    /// unable to throw on whatever a server or a manifest hands it. Unknown or
    /// malformed input compares as "not newer", so a bad answer never nags.
    ///
    /// Pre-release suffixes (`0.2.0-rc.1`) are recognised and ordered before the
    /// matching release, as semver requires, but not ranked among themselves.
    /// No UnityEngine dependency, so it is directly testable.
    /// </summary>
    internal static class QamelVersion
    {
        /// <summary>True when both parse and <paramref name="candidate"/> is greater.</summary>
        public static bool IsNewer(string candidate, string current)
        {
            return TryCompare(candidate, current, out int comparison) && comparison > 0;
        }

        /// <summary>True when both parse and <paramref name="candidate"/> is lower.</summary>
        public static bool IsOlder(string candidate, string current)
        {
            return TryCompare(candidate, current, out int comparison) && comparison < 0;
        }

        /// <summary>
        /// Orders two versions. False when either side is not a version at all,
        /// which callers must treat as "no opinion" rather than as equality.
        /// </summary>
        public static bool TryCompare(string left, string right, out int comparison)
        {
            comparison = 0;
            if (!TryParse(left, out var a) || !TryParse(right, out var b)) return false;
            comparison = a.CompareTo(b);
            return true;
        }

        public static bool TryParse(string value, out Parsed parsed)
        {
            parsed = default(Parsed);
            if (string.IsNullOrWhiteSpace(value)) return false;

            string text = value.Trim();
            if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V')) text = text.Substring(1);

            // Build metadata never affects ordering; a pre-release suffix does.
            int plus = text.IndexOf('+');
            if (plus >= 0) text = text.Substring(0, plus);

            bool preRelease = false;
            int dash = text.IndexOf('-');
            if (dash >= 0)
            {
                preRelease = dash + 1 < text.Length;
                text = text.Substring(0, dash);
            }

            string[] parts = text.Split('.');
            if (parts.Length < 1 || parts.Length > 3) return false;

            if (!TryParseNumber(parts[0], out int major)) return false;
            int minor = 0, patch = 0;
            if (parts.Length > 1 && !TryParseNumber(parts[1], out minor)) return false;
            if (parts.Length > 2 && !TryParseNumber(parts[2], out patch)) return false;

            parsed = new Parsed(major, minor, patch, preRelease);
            return true;
        }

        /// <summary>
        /// int.TryParse would accept "+1", " 1" and culture-specific digits, none
        /// of which belong in a version segment.
        /// </summary>
        static bool TryParseNumber(string text, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text) || text.Length > 9) return false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < '0' || c > '9') return false;
                value = value * 10 + (c - '0');
            }
            return true;
        }

        internal struct Parsed
        {
            public readonly int Major;
            public readonly int Minor;
            public readonly int Patch;
            public readonly bool PreRelease;

            public Parsed(int major, int minor, int patch, bool preRelease)
            {
                Major = major;
                Minor = minor;
                Patch = patch;
                PreRelease = preRelease;
            }

            public int CompareTo(Parsed other)
            {
                if (Major != other.Major) return Major < other.Major ? -1 : 1;
                if (Minor != other.Minor) return Minor < other.Minor ? -1 : 1;
                if (Patch != other.Patch) return Patch < other.Patch ? -1 : 1;
                if (PreRelease == other.PreRelease) return 0;
                return PreRelease ? -1 : 1;
            }
        }
    }
}
