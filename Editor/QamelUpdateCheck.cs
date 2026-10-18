using System;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace QamelCapture.Editor
{
    /// <summary>
    /// Editor-only release checks. The published package is authoritative: a
    /// website deployment can lag behind a package release. No credentials or
    /// project data are sent. Cache and dismissal state belong to this project.
    /// </summary>
    internal static class QamelUpdateCheck
    {
        internal const string PublishedManifestUrl =
            "https://raw.githubusercontent.com/qamel-ai/qamel-unity/main/package.json";
        const string ChangesUrl = "https://github.com/qamel-ai/qamel-unity/commits/main";
        const int TimeoutSeconds = 10;
        const double RetrySeconds = 600;
        static double _nextTick;
        static double _retryAt;
        static double _idleSince = -1;
        static bool _manualResult;

        internal static string ProjectKey(string field) =>
            "Qamel.UpdateCheck.V2." + Hash128.Compute(Application.dataPath) + "." + field;

        public static string InstalledVersion =>
            QamelPackageUpdater.Installed?.version ?? QamelSettings.PluginVersion;
        public static string LatestVersion => EditorPrefs.GetString(ProjectKey("Latest"), "");
        public static string MinSupportedVersion => EditorPrefs.GetString(ProjectKey("MinSupported"), "");
        public static string SkippedVersion => EditorPrefs.GetString(ProjectKey("Skipped"), "");
        public static bool ReleaseVerified => EditorPrefs.GetBool(ProjectKey("Verified"), false);
        public static string NotesUrl => ChangeHistoryUrl(LatestVersion);
        public static bool IsChecking { get; private set; }
        public static string LastError { get; private set; }
        public static string LastResult { get; private set; }
        public static bool UpdateAvailable => QamelVersion.IsNewer(LatestVersion, InstalledVersion);
        public static bool IsSkipped => string.Equals(SkippedVersion, LatestVersion, StringComparison.Ordinal);
        public static bool ShowUpdate => UpdateAvailable && (!IsSkipped || _manualResult);
        public static bool InstalledVersionUnsupported =>
            QamelVersion.IsNewer(MinSupportedVersion, InstalledVersion);

        public static void SkipCurrentLatest()
        {
            if (LatestVersion.Length > 0) EditorPrefs.SetString(ProjectKey("Skipped"), LatestVersion);
            _manualResult = false;
        }

        [InitializeOnLoadMethod]
        static void ScheduleAutomaticCheck()
        {
            if (Application.isBatchMode) return;
            _nextTick = EditorApplication.timeSinceStartup + 10;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < _nextTick) return;
            _nextTick = now + 1;
            QamelPackageUpdater.ObserveCompletedUpdate();
            var settings = QamelSettings.LoadFromResources();
            if (!QamelPackageUpdater.EditorIsIdle || (settings != null && !settings.checkForUpdates))
            {
                _idleSince = -1;
                return;
            }
            if (!IsChecking && CheckIsDue(ReleaseVerified,
                    EditorPrefs.GetString(ProjectKey("LastSuccess"), ""), DateTime.UtcNow, now, _retryAt))
            {
                CheckNow(settings, userInitiated: false);
            }
            if (IsChecking || !ReleaseVerified || !UpdateAvailable || IsSkipped ||
                !QamelPackageUpdater.AutomaticUpdatesEnabled ||
                QamelPackageUpdater.AutomaticUpdateReason(QamelPackageUpdater.Installed) != null)
            {
                _idleSince = -1;
                return;
            }
            if (_idleSince < 0) _idleSince = now;
            if (now - _idleSince < 10) return;
            QamelPackageUpdater.TryAutomaticUpdate(LatestVersion);
        }

        internal static bool IntervalElapsed(string stored, DateTime now)
        {
            if (!long.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks) ||
                ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return true;
            var last = new DateTime(ticks, DateTimeKind.Utc);
            return last > now || (now - last).TotalHours >= 24;
        }

        internal static bool CheckIsDue(bool verified, string lastSuccess, DateTime now,
            double editorTime, double retryAt) =>
            editorTime >= retryAt && (!verified || IntervalElapsed(lastSuccess, now));

        internal static void CheckWhenDue(QamelSettings settings)
        {
            if (Application.isBatchMode || IsChecking || (settings != null && !settings.checkForUpdates)) return;
            if (CheckIsDue(ReleaseVerified, EditorPrefs.GetString(ProjectKey("LastSuccess"), ""),
                    DateTime.UtcNow, EditorApplication.timeSinceStartup, _retryAt))
                CheckNow(settings, userInitiated: false);
        }

        internal static string DescribeStatus(bool checking, bool updating, bool verified,
            string error, string installed, string latest)
        {
            if (updating) return "Updating...";
            if (checking) return "Checking...";
            if (!string.IsNullOrEmpty(error)) return "Check failed";
            if (!verified || !QamelVersion.TryParse(installed, out _) ||
                !QamelVersion.TryParse(latest, out _)) return "Not checked";
            if (QamelVersion.IsNewer(latest, installed)) return "Update available";
            if (QamelVersion.IsNewer(installed, latest)) return "Development version";
            return "Up to date";
        }

        public static void CheckNow(QamelSettings settings, bool userInitiated)
        {
            string endpoint = settings != null ? IngestRoutes.Normalize(settings.endpoint) : "https://ingest.qamel.ai";
            CheckSources(endpoint.Length > 0
                ? endpoint + IngestRoutes.LatestVersionPath + "?package=com.qamel.unity"
                : null, PublishedManifestUrl, userInitiated);
        }

        // Explicit sources let the editor tests exercise real requests against
        // a loopback server without contacting a user's project or public host.
        internal static void CheckSources(string endpointUrl, string manifestUrl, bool userInitiated)
        {
            if (IsChecking) return;
            IsChecking = true;
            _manualResult = userInitiated;
            LastError = null;
            LastResult = null;
            _idleSince = -1;
            EditorPrefs.SetBool(ProjectKey("Verified"), false);
            _retryAt = EditorApplication.timeSinceStartup + RetrySeconds;

            string endpointBody = null;
            string manifestBody = null;
            string manifestError = null;
            int remaining = string.IsNullOrEmpty(endpointUrl) ? 1 : 2;
            void Complete()
            {
                if (--remaining > 0) return;
                IsChecking = false;
                EditorWindow.focusedWindow?.Repaint();
                if (!ChooseRelease(endpointBody, manifestBody, out string latest,
                        out string minSupported, out string notesUrl, out bool verified))
                {
                    LastError = "Could not check for updates. " + (manifestError ?? "The version response was invalid.");
                    if (userInitiated || QLog.Verbose) QLog.Warn(LastError);
                    return;
                }
                EditorPrefs.SetString(ProjectKey("Latest"), latest);
                EditorPrefs.SetString(ProjectKey("MinSupported"), minSupported);
                EditorPrefs.SetString(ProjectKey("NotesUrl"), notesUrl.Length > 0 ? notesUrl : ChangeHistoryUrl(latest));
                EditorPrefs.SetBool(ProjectKey("Verified"), verified);
                if (verified)
                {
                    EditorPrefs.SetString(ProjectKey("LastSuccess"), DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
                    LastResult = UpdateAvailable
                        ? "Qamel " + latest + " is available. You have " + InstalledVersion + "."
                        : "Checked the published package. Qamel " + InstalledVersion + " is up to date.";
                }
                else
                {
                    LastError = "Could not verify the published package. Automatic updates will retry later.";
                }
                if (userInitiated) QLog.Notice(LastError ?? LastResult);
            }
            if (!string.IsNullOrEmpty(endpointUrl))
                Send(endpointUrl, (body, error) => { endpointBody = body; Complete(); });
            Send(manifestUrl, (body, error) =>
            {
                manifestBody = body;
                manifestError = error;
                Complete();
            });
        }

        static void Send(string url, Action<string, string> completed)
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
                request.Dispose();
                completed(body, error);
            }
            EditorApplication.update += Poll;
        }

        internal static bool ChooseRelease(string endpointBody, string manifestBody, out string latest,
            out string minSupported, out string notesUrl, out bool verified)
        {
            bool endpointOk = TryParsePayload(endpointBody, out string endpointLatest,
                out minSupported, out notesUrl);
            verified = TryParsePublishedManifest(manifestBody, out string publishedLatest);
            latest = verified ? publishedLatest : endpointLatest;
            if (verified) notesUrl = ChangeHistoryUrl(publishedLatest);
            return verified || endpointOk;
        }

        internal static string ChangeHistoryUrl(string latest)
        {
            return System.Text.RegularExpressions.Regex.IsMatch(latest ?? "",
                @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")
                ? "https://github.com/qamel-ai/qamel-unity/commits/v" + latest
                : ChangesUrl;
        }

        internal static bool TryParsePublishedManifest(string body, out string latest)
        {
            latest = "";
            if (!TryParsePayload(body, out _, out _, out _)) return false;
            var payload = JsonUtility.FromJson<LatestPayload>(body);
            if (payload.name != "com.qamel.unity" ||
                !System.Text.RegularExpressions.Regex.IsMatch(payload.version ?? "",
                    @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")) return false;
            latest = payload.version;
            return true;
        }

        internal static bool TryParsePayload(string body, out string latest,
            out string minSupported, out string notesUrl)
        {
            latest = "";
            minSupported = "";
            notesUrl = "";
            if (string.IsNullOrWhiteSpace(body)) return false;
            LatestPayload payload;
            try { payload = JsonUtility.FromJson<LatestPayload>(body); }
            catch (Exception) { return false; }
            if (payload == null || (!string.IsNullOrEmpty(payload.name) && payload.name != "com.qamel.unity") ||
                (!string.IsNullOrEmpty(payload.package) && payload.package != "com.qamel.unity")) return false;
            string version = !string.IsNullOrWhiteSpace(payload.latest) ? payload.latest : payload.version;
            if (!QamelVersion.TryParse(version, out _)) return false;
            latest = version.Trim();
            if (QamelVersion.TryParse(payload.minSupported, out _)) minSupported = payload.minSupported.Trim();
            if (Uri.TryCreate(payload.notesUrl, UriKind.Absolute, out var notes) &&
                notes.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(notes.UserInfo)) notesUrl = notes.AbsoluteUri;
            return true;
        }

        [Serializable]
        class LatestPayload
        {
            public string package;
            public string name;
            public string latest;
            public string version;
            public string minSupported;
            public string notesUrl;
        }
    }
}
