using System;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace QamelCapture.Editor
{
    /// <summary>
    /// Editor-time ingest key check. POSTs /v1/plugin/health when a developer
    /// saves an API key and at startup. Optional Editor diagnostics refresh
    /// hourly and when update state changes. Never runs in a player or batchmode.
    /// </summary>
    internal static class QamelHealthCheck
    {
        const string AttemptedKey = "Qamel.Health.AttemptedKey";
        const double DebounceSeconds = 0.75;
        const int TimeoutSeconds = 10;

        public enum Result
        {
            Idle,
            Checking,
            Connected,
            InvalidKey,
            UnverifiedKey,
            Unreachable,
        }

        public static Result Status { get; private set; }
        public static bool IsChecking => Status == Result.Checking;

        static QamelSettings _pendingSettings;
        static double _due;
        static bool _debounceQueued;
        static double _nextPoll;

        static string HealthKey(string field) =>
            "Qamel.EditorHealth." + Hash128.Compute(Application.dataPath) + "." + field;

        internal static string EditorInstallationId
        {
            get
            {
                string key = HealthKey("InstallationId");
                string value = EditorPrefs.GetString(key, "");
                if (!Guid.TryParseExact(value, "N", out Guid id))
                {
                    value = Guid.NewGuid().ToString("N");
                    EditorPrefs.SetString(key, value);
                }
                else value = id.ToString("N");
                return value;
            }
        }

        [InitializeOnLoadMethod]
        static void Schedule()
        {
            if (Application.isBatchMode) return;
            _nextPoll = EditorApplication.timeSinceStartup + 10;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + 5;
            if (Application.isBatchMode || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                BuildPipeline.isBuildingPlayer || IsChecking || _debounceQueued) return;
            var settings = QamelSettings.LoadFromResources();
            if (settings == null || TrimKey(settings.apiKey).Length == 0) return;
            ObserveSettings(settings);
            if (IsChecking || !settings.sendPluginDiagnostics) return;
            string fingerprint = Fingerprint(settings, BuildPayload(settings));
            if (HeartbeatIsDue(fingerprint, SessionState.GetString(HealthKey("Fingerprint"), ""),
                    SessionState.GetString(HealthKey("AttemptAt"), ""),
                    SessionState.GetBool(HealthKey("Succeeded"), false), DateTime.UtcNow))
                Ping(settings, repeat: true);
        }

        internal static bool HeartbeatIsDue(string fingerprint, string previous, string attemptedAt,
            bool succeeded, DateTime now)
        {
            var attempted = QamelUpdateCheck.ReadCheckTime(attemptedAt);
            if (!attempted.HasValue || attempted.Value > now) return true;
            double elapsed = (now - attempted.Value).TotalSeconds;
            if (!succeeded) return elapsed >= 600;
            return elapsed >= 3600 || (fingerprint != previous && elapsed >= 30);
        }

        static string Fingerprint(QamelSettings settings, string payload) =>
            Hash128.Compute(IngestRoutes.Normalize(settings.endpoint) + "\n" + settings.apiKey + "\n" + payload).ToString();

        /// <summary>JSON body locked by tests to docs/capture-spec.md.</summary>
        public static string BuildPayload(QamelSettings settings = null)
        {
            string basic = new QamelJson().Begin()
                .Str("kind", "plugin_health")
                .Str("engine", "unity")
                .Str("plugin", "com.qamel.unity")
                .Str("plugin_version", QamelSettings.PluginVersion)
                .End();
            if (settings == null || !settings.sendPluginDiagnostics) return basic;
            var installed = QamelPackageUpdater.Installed;
            string source = installed != null ? installed.source.ToString() : "Unknown";
            if (source != "Git" && source != "Registry" && source != "Local" &&
                source != "Embedded" && source != "LocalTarball") source = "Unknown";
            string editor = new QamelJson().Begin()
                .Str("installation_id", EditorInstallationId)
                .Str("engine_version", Application.unityVersion)
                .Str("os", SystemInfo.operatingSystemFamily.ToString())
                .Str("install_source", source)
                .Str("update_mode", !settings.checkForUpdates ? "disabled" :
                    QamelPackageUpdater.AutomaticUpdatesEnabled ? "automatic" : "notify")
                .Str("update_status", DiagnosticUpdateStatus())
                .Str("latest_version", QamelUpdateCheck.LatestVersion)
                .Str("last_update_check_at", QamelUpdateCheck.LastSuccessfulCheckUtc?.ToString("o", CultureInfo.InvariantCulture))
                .End();
            return basic.Substring(0, basic.Length - 1) + ",\"editor\":" + editor + "}";
        }

        static string DiagnosticUpdateStatus()
        {
            if (QamelPackageUpdater.IsUpdating) return "updating";
            if (QamelUpdateCheck.IsChecking) return "checking";
            if (!string.IsNullOrEmpty(QamelPackageUpdater.LastError)) return "update_failed";
            if (!string.IsNullOrEmpty(QamelUpdateCheck.LastError)) return "check_failed";
            if (QamelUpdateCheck.UpdateAvailable && QamelUpdateCheck.IsSkipped) return "skipped";
            string status = QamelUpdateCheck.DescribeStatus(false, false, QamelUpdateCheck.ReleaseVerified,
                null, QamelUpdateCheck.InstalledVersion, QamelUpdateCheck.LatestVersion,
                QamelUpdateCheck.LastSuccess, DateTime.UtcNow);
            switch (status)
            {
                case "Update available": return "available";
                case "Development version": return "development";
                case "Up to date": return "current";
                case "Check overdue": return "overdue";
                default: return "not_checked";
            }
        }

        /// <summary>
        /// Validate once per Editor session, including when settings are opened.
        /// </summary>
        public static void ObserveSettings(QamelSettings settings)
        {
            if (Application.isBatchMode) return;
            if (settings == null) return;
            if (_debounceQueued) return;
            string key = TrimKey(settings.apiKey);
            if (key.Length == 0) return;
            if (SessionState.GetString(AttemptedKey, "") == key) return;
            Ping(settings);
        }

        /// <summary>Debounced ping after the API key field changes.</summary>
        public static void OnSettingsChanged(QamelSettings settings)
        {
            if (Application.isBatchMode) return;
            if (settings == null) return;
            string key = TrimKey(settings.apiKey);
            if (key.Length == 0)
            {
                Status = Result.Idle;
                SessionState.EraseString(AttemptedKey);
                return;
            }

            SessionState.EraseString(AttemptedKey);
            _pendingSettings = settings;
            _due = EditorApplication.timeSinceStartup + DebounceSeconds;
            if (_debounceQueued) return;
            _debounceQueued = true;
            EditorApplication.update += FlushDebounce;
        }

        /// <summary>Immediate ping (retry after invalid/unreachable).</summary>
        public static void CheckNow(QamelSettings settings)
        {
            if (Application.isBatchMode) return;
            SessionState.EraseString(AttemptedKey);
            Ping(settings);
        }

        static void FlushDebounce()
        {
            if (EditorApplication.timeSinceStartup < _due) return;
            EditorApplication.update -= FlushDebounce;
            _debounceQueued = false;
            var settings = _pendingSettings;
            _pendingSettings = null;
            Ping(settings);
        }

        static void Ping(QamelSettings settings, bool repeat = false)
        {
            if (settings == null) return;
            string key = TrimKey(settings.apiKey);
            if (key.Length == 0) return;
            if (IsChecking) return;
            if (!repeat && SessionState.GetString(AttemptedKey, "") == key) return;

            SessionState.SetString(AttemptedKey, key);
            Status = Result.Checking;

            string payload = BuildPayload(settings);
            SessionState.SetString(HealthKey("Fingerprint"), Fingerprint(settings, payload));
            SessionState.SetString(HealthKey("AttemptAt"), DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
            SessionState.SetBool(HealthKey("Succeeded"), false);

            string endpoint = IngestRoutes.Normalize(settings.endpoint);
            if (endpoint.Length == 0)
            {
                Status = Result.Unreachable;
                return;
            }

            string url = IngestRoutes.Url(endpoint, IngestRoutes.HealthPath);
            byte[] body = Encoding.UTF8.GetBytes(payload);
            var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(body)
            {
                contentType = "application/json",
            };
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader(IngestHeaders.Authorization, IngestHeaders.Bearer(key));
            request.SetRequestHeader(IngestHeaders.Plugin, IngestHeaders.PluginValue());
            request.timeout = TimeoutSeconds;
            UnityWebRequestAsyncOperation operation;
            try { operation = request.SendWebRequest(); }
            catch
            {
                request.Dispose();
                Status = Result.Unreachable;
                return;
            }

            void Poll()
            {
                if (!operation.isDone) return;
                EditorApplication.update -= Poll;

                long status = request.responseCode;
                bool ok = request.result == UnityWebRequest.Result.Success &&
                          status >= 200 && status < 300;
                string responseBody = request.downloadHandler.text;
                request.Dispose();

                if (ok)
                {
                    SessionState.SetBool(HealthKey("Succeeded"), true);
                    Status = QamelPlayerBuildGuard.IsCaptureOnlyResponse(status, responseBody)
                        ? Result.Connected : Result.UnverifiedKey;
                    return;
                }

                if (status == 401 || status == 403)
                {
                    Status = Result.InvalidKey;
                    return;
                }

                Status = Result.Unreachable;
            }

            EditorApplication.update += Poll;
            if (EditorWindow.focusedWindow != null) EditorWindow.focusedWindow.Repaint();
        }

        static string TrimKey(string apiKey)
        {
            return (apiKey ?? "").Trim();
        }
    }
}
