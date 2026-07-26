namespace QamelCapture
{
    /// <summary>
    /// Ingest URL construction, plus the two escape hatches that keep a build
    /// shipped today working after the server moves. An endpoint compiled into
    /// a player can never be changed, so the server can (a) hand a session a
    /// different base URL to use from now on and (b) answer <c>410</c> when a
    /// path is retired. Paths are versioned on the ingest host, so the base URL
    /// itself stays stable across format changes.
    /// </summary>
    internal static class IngestRoutes
    {
        public const string ReportPath = "/v1/report";
        public const string ChunkPath = "/v1/chunk";
        public const string PluginErrorPath = "/v1/plugin-error";

        /// <summary>
        /// Base URL the server asked this session to use instead of the
        /// configured endpoint. Session-scoped and never persisted (Qamel writes
        /// nothing to the player's disk), so every run starts from the settings
        /// asset and a handoff can be withdrawn by simply not sending it again.
        /// </summary>
        public static string SessionBaseOverride { get; private set; }

        public static void ResetSession()
        {
            SessionBaseOverride = null;
        }

        /// <summary>Trims trailing slashes and surrounding whitespace; null-safe.</summary>
        public static string Normalize(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return "";
            return baseUrl.Trim().TrimEnd('/');
        }

        /// <summary>
        /// True for an absolute HTTPS base URL, or an HTTP one on a loopback
        /// host (local development). Plain HTTP elsewhere is refused so a
        /// hijacked or mistyped handoff cannot downgrade transport security.
        /// </summary>
        public static bool IsValidBase(string baseUrl)
        {
            string normalized = Normalize(baseUrl);
            if (normalized.Length == 0) return false;
            if (normalized.IndexOf(' ') >= 0 || normalized.IndexOf('\t') >= 0) return false;

            if (normalized.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase))
                return normalized.Length > "https://".Length;

            if (normalized.StartsWith("http://", System.StringComparison.OrdinalIgnoreCase))
            {
                string host = normalized.Substring("http://".Length);
                int end = host.IndexOfAny(new[] { '/', ':' });
                if (end >= 0) host = host.Substring(0, end);
                return host.Equals("localhost", System.StringComparison.OrdinalIgnoreCase) ||
                       host == "127.0.0.1" ||
                       host == "[::1]";
            }

            return false;
        }

        /// <summary>
        /// Accepts a server-provided base URL for the rest of this session.
        /// Returns true when the override changed.
        /// </summary>
        public static bool TryAcceptHandoff(string candidate)
        {
            string normalized = Normalize(candidate);
            if (!IsValidBase(normalized)) return false;
            if (normalized == SessionBaseOverride) return false;

            SessionBaseOverride = normalized;
            QLog.Info("Server handed off ingest to " + normalized + " for this session.");
            return true;
        }

        /// <summary>Full request URL, honouring an accepted session handoff.</summary>
        public static string Url(string configuredBase, string path)
        {
            string root = !string.IsNullOrEmpty(SessionBaseOverride)
                ? SessionBaseOverride
                : Normalize(configuredBase);
            return root + path;
        }
    }
}
