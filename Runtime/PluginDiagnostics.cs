using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace QamelCapture
{
    /// <summary>
    /// Reports Qamel's own internal failures to the Qamel servers (POST
    /// {endpoint}/v1/plugin-error), so a broken plugin surfaces instead of
    /// silently losing a tester's reports. The payload
    /// contains plugin/engine/OS info and the internal error only, never gameplay
    /// data. Fire-and-forget: one attempt, all failures swallowed, because this
    /// runs on the plugin's own failure path.
    /// </summary>
    internal static class PluginDiagnostics
    {
        const int RequestTimeoutSeconds = 30;
        const int MaxErrorChars = 1000;
        const int MaxStackChars = 4000;

        public static string BuildPayload(string sessionId, string where, Exception error)
        {
            return BuildPayload(sessionId, where, error, default(IdentitySnapshot), "");
        }

        public static string BuildPayload(
            string sessionId,
            string where,
            Exception error,
            IdentitySnapshot identity,
            string buildId)
        {
            var json = new QamelJson().Begin()
                .Str("schema", ReportBundler.SchemaVersion)
                .Str("kind", "plugin_error")
                .Str("session_id", sessionId ?? "")
                .Str("where", where)
                .Str("error", Truncate(error != null ? error.GetType().Name + ": " + error.Message : "", MaxErrorChars))
                .Str("stack", Truncate(error != null ? error.StackTrace : "", MaxStackChars));
            ReportManifest.AppendContext(json, identity, buildId);
            return json.End();
        }

        public static IEnumerator Send(
            QamelSettings settings,
            string sessionId,
            string where,
            Exception error,
            IdentitySnapshot identity)
        {
            string payload;
            try
            {
                payload = BuildPayload(sessionId, where, error, identity, settings.buildId);
            }
            catch
            {
                yield break;
            }

            string url = IngestRoutes.Url(settings.endpoint, IngestRoutes.PluginErrorPath);
            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload))
                {
                    contentType = "application/json",
                };
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Authorization", "Bearer " + settings.apiKey.Trim());
                request.SetRequestHeader("X-Qamel-Plugin", "unity/" + QamelSettings.PluginVersion);
                request.timeout = RequestTimeoutSeconds;

                yield return request.SendWebRequest();
                // Fire-and-forget: the outcome is intentionally ignored.
            }
        }

        static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
            return s.Substring(0, max) + "...[truncated]";
        }
    }
}
