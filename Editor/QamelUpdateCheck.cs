using System;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace QamelCapture.Editor
{
    /// <summary>
    /// Tells the developer when a newer plugin version exists.
    ///
    /// Unity's Package Manager offers no update for a git dependency: it resolves
    /// once and the consumer's lockfile pins that commit, so without this a studio
    /// stays on whatever they first installed. The check asks the configured ingest
    /// endpoint (so self-hosted hosts answer for their own users) and falls back to
    /// the public repo's manifest. It is editor-only, runs at most once a day,
    /// sends no key and no identifiers, and fails silently: a version check must
    /// never interrupt anyone's work.
    /// </summary>
    internal static class QamelUpdateCheck
    {
        const string LastCheckedKey = "Qamel.UpdateCheck.LastCheckedTicks";
        const string LatestKey = "Qamel.UpdateCheck.Latest";
        const string MinSupportedKey = "Qamel.UpdateCheck.MinSupported";
        const string NotesUrlKey = "Qamel.UpdateCheck.NotesUrl";
        const string AnnouncedKey = "Qamel.UpdateCheck.Announced";
        const string SkippedKey = "Qamel.UpdateCheck.Skipped";

        const double CheckIntervalHours = 24;
        const int TimeoutSeconds = 10;

        /// <summary>
        /// Used when the ingest host cannot be reached (a studio behind a proxy
        /// that only allows github.com, or an endpoint typo).
        /// </summary>
        const string ManifestFallbackUrl =
            "https://raw.githubusercontent.com/qamel-ai/qamel-unity/main/package.json";

        const string ReleasesUrl = "https://github.com/qamel-ai/qamel-unity/releases";

        public static string InstalledVersion => QamelSettings.PluginVersion;
        public static string LatestVersion => EditorPrefs.GetString(LatestKey, "");
        public static string MinSupportedVersion => EditorPrefs.GetString(MinSupportedKey, "");
        public static string SkippedVersion => EditorPrefs.GetString(SkippedKey, "");

        public static string NotesUrl
        {
            get
            {
                string stored = EditorPrefs.GetString(NotesUrlKey, "");
                return stored.Length > 0 ? stored : ReleasesUrl;
            }
        }

        /// <summary>True while a request is in flight, so the UI can say so.</summary>
        public static bool IsChecking { get; private set; }

        /// <summary>Last failure, shown only when the developer asked for a check.</summary>
        public static string LastError { get; private set; }

        public static bool UpdateAvailable =>
            QamelVersion.IsNewer(LatestVersion, InstalledVersion) &&
            !string.Equals(SkippedVersion, LatestVersion, StringComparison.Ordinal);

        /// <summary>
        /// The installed version is older than the oldest one Qamel still
        /// supports. Advisory on purpose: capture keeps working, and ingest is
        /// what actually retires a wire format (410).
        /// </summary>
        public static bool InstalledVersionUnsupported =>
            QamelVersion.IsNewer(MinSupportedVersion, InstalledVersion);

        public static void SkipCurrentLatest()
        {
            string latest = LatestVersion;
            if (latest.Length > 0) EditorPrefs.SetString(SkippedKey, latest);
        }

        [InitializeOnLoadMethod]
        static void ScheduleAutomaticCheck()
        {
            // Batch mode is CI and our own headless test runs: never phone home
            // from there. The settings asset is also not reliably loadable this
            // early, so the actual work waits for the first editor update.
            if (Application.isBatchMode) return;
            EditorApplication.update += RunAutomaticCheckOnce;
        }

        static void RunAutomaticCheckOnce()
        {
            EditorApplication.update -= RunAutomaticCheckOnce;

            var settings = QamelSettings.LoadFromResources();
            if (settings == null || !settings.checkForUpdates) return;
            if (!IntervalElapsed()) return;

            CheckNow(settings, userInitiated: false);
        }

        static bool IntervalElapsed()
        {
            string stored = EditorPrefs.GetString(LastCheckedKey, "");
            if (stored.Length == 0) return true;
            if (!long.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out long ticks))
            {
                return true;
            }
            var last = new DateTime(ticks, DateTimeKind.Utc);
            return (DateTime.UtcNow - last).TotalHours >= CheckIntervalHours;
        }

        /// <summary>
        /// Starts a check. <paramref name="userInitiated"/> checks bypass the daily
        /// throttle and surface their errors, because someone is watching.
        /// </summary>
        public static void CheckNow(QamelSettings settings, bool userInitiated)
        {
            if (IsChecking) return;
            IsChecking = true;
            LastError = null;
            EditorPrefs.SetString(LastCheckedKey,
                DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));

            string endpoint = settings != null ? IngestRoutes.Normalize(settings.endpoint) : "";
            string url = endpoint.Length > 0
                ? endpoint + IngestRoutes.LatestVersionPath + "?package=" + PackageName
                : ManifestFallbackUrl;

            Send(url, userInitiated, endpoint.Length > 0);
        }

        const string PackageName = "com.qamel.unity";

        static void Send(string url, bool userInitiated, bool allowFallback)
        {
            var request = UnityWebRequest.Get(url);
            request.timeout = TimeoutSeconds;
            var operation = request.SendWebRequest();

            void Poll()
            {
                if (!operation.isDone) return;
                EditorApplication.update -= Poll;

                bool ok = request.result == UnityWebRequest.Result.Success;
                string body = ok ? request.downloadHandler.text : null;
                string error = ok ? null : request.error;
                long status = request.responseCode;
                request.Dispose();

                if (ok && TryApply(body))
                {
                    IsChecking = false;
                    Announce(userInitiated);
                    return;
                }

                // The endpoint answered with something unusable (an old server
                // without the route, a captive portal). The public manifest is a
                // second opinion worth one request.
                if (allowFallback)
                {
                    Send(ManifestFallbackUrl, userInitiated, allowFallback: false);
                    return;
                }

                IsChecking = false;
                LastError = error ?? ("unexpected response" + (status > 0 ? " (HTTP " + status + ")" : ""));
                if (userInitiated || QLog.Verbose)
                {
                    QLog.Warn("Could not check for plugin updates: " + LastError);
                }
            }

            EditorApplication.update += Poll;
        }

        /// <summary>
        /// Reads a response body. Accepts either the Qamel payload or the package
        /// manifest used as a fallback, and refuses anything without a parseable
        /// version, so a proxy's HTML error page cannot become "latest".
        /// </summary>
        internal static bool TryParsePayload(string body, out string latest,
            out string minSupported, out string notesUrl)
        {
            latest = "";
            minSupported = "";
            notesUrl = "";
            if (string.IsNullOrWhiteSpace(body)) return false;

            LatestPayload payload;
            try
            {
                payload = JsonUtility.FromJson<LatestPayload>(body);
            }
            catch (Exception)
            {
                return false;
            }
            if (payload == null) return false;

            // The manifest fallback names the field "version".
            string version = !string.IsNullOrWhiteSpace(payload.latest)
                ? payload.latest
                : payload.version;
            if (!QamelVersion.TryParse(version, out _)) return false;

            latest = version.Trim();
            if (QamelVersion.TryParse(payload.minSupported, out _))
            {
                minSupported = payload.minSupported.Trim();
            }
            // Only an absolute HTTPS URL, since this ends up in Application.OpenURL.
            if (!string.IsNullOrWhiteSpace(payload.notesUrl) &&
                payload.notesUrl.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                notesUrl = payload.notesUrl.Trim();
            }
            return true;
        }

        static bool TryApply(string body)
        {
            if (!TryParsePayload(body, out string latest, out string minSupported,
                    out string notesUrl))
            {
                return false;
            }

            EditorPrefs.SetString(LatestKey, latest);
            EditorPrefs.SetString(MinSupportedKey, minSupported);
            EditorPrefs.SetString(NotesUrlKey, notesUrl);
            return true;
        }

        /// <summary>
        /// One console line per version, ever. A daily reminder about a version
        /// someone has already decided not to install is just noise.
        /// </summary>
        static void Announce(bool userInitiated)
        {
            if (InstalledVersionUnsupported)
            {
                QLog.Warn("This Qamel plugin (" + InstalledVersion + ") is older than the oldest " +
                          "supported version (" + MinSupportedVersion + "). Update it in " +
                          "Project Settings > Qamel.");
                return;
            }

            if (!UpdateAvailable)
            {
                if (userInitiated)
                {
                    QLog.Notice("Qamel plugin " + InstalledVersion + " is up to date.");
                }
                return;
            }

            if (!userInitiated &&
                string.Equals(EditorPrefs.GetString(AnnouncedKey, ""), LatestVersion,
                    StringComparison.Ordinal))
            {
                return;
            }

            EditorPrefs.SetString(AnnouncedKey, LatestVersion);
            QLog.Notice("Qamel plugin " + LatestVersion + " is available (you have " +
                        InstalledVersion + "). Update it in Project Settings > Qamel.");
        }

        [Serializable]
        class LatestPayload
        {
            public string latest;
            public string minSupported;
            public string notesUrl;

            /// <summary>Only set by the package.json fallback.</summary>
            public string version;
        }
    }
}
