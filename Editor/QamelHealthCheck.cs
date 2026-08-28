using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace QamelCapture.Editor
{
    /// <summary>
    /// Editor-time ingest key check. POSTs /v1/plugin/health when a developer
    /// saves a non-empty API key so the dashboard can mark capture connected
    /// without waiting for a play-mode report. Never runs in a player or in
    /// batchmode.
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
            Unreachable,
        }

        public static Result Status { get; private set; }
        public static bool IsChecking => Status == Result.Checking;

        static QamelSettings _pendingSettings;
        static double _due;
        static bool _debounceQueued;

        /// <summary>JSON body locked by tests to docs/capture-spec.md.</summary>
        public static string BuildPayload()
        {
            return new QamelJson().Begin()
                .Str("kind", "plugin_health")
                .Str("engine", "unity")
                .Str("plugin", "com.qamel.unity")
                .Str("plugin_version", QamelSettings.PluginVersion)
                .End();
        }

        /// <summary>
        /// Ping once per editor session for a given key when settings are open.
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

        static void Ping(QamelSettings settings)
        {
            if (settings == null) return;
            string key = TrimKey(settings.apiKey);
            if (key.Length == 0) return;
            if (IsChecking) return;
            if (SessionState.GetString(AttemptedKey, "") == key) return;

            SessionState.SetString(AttemptedKey, key);
            Status = Result.Checking;

            string endpoint = IngestRoutes.Normalize(settings.endpoint);
            if (endpoint.Length == 0)
            {
                Status = Result.Unreachable;
                return;
            }

            string url = IngestRoutes.Url(endpoint, IngestRoutes.HealthPath);
            byte[] body = Encoding.UTF8.GetBytes(BuildPayload());
            var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(body)
            {
                contentType = "application/json",
            };
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader(IngestHeaders.Authorization, IngestHeaders.Bearer(key));
            request.SetRequestHeader(IngestHeaders.Plugin, IngestHeaders.PluginValue());
            request.timeout = TimeoutSeconds;
            var operation = request.SendWebRequest();

            void Poll()
            {
                if (!operation.isDone) return;
                EditorApplication.update -= Poll;

                long status = request.responseCode;
                bool ok = request.result == UnityWebRequest.Result.Success &&
                          status >= 200 && status < 300;
                request.Dispose();

                if (ok)
                {
                    Status = Result.Connected;
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
