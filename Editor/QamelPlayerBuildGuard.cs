using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace QamelCapture.Editor
{
    /// <summary>Confirms embedded credentials have capture-only access before building.</summary>
    internal sealed class QamelPlayerBuildGuard : IPreprocessBuildWithReport
    {
        public int callbackOrder => int.MaxValue;

        public void OnPreprocessBuild(BuildReport report)
        {
            QamelBuildInclusion.Restore();
            var settings = FindSettings();
            ValidateSettings(settings.Where(s => s.includeInPlayerBuild), VerifyKey);
            QamelBuildInclusion.ExcludeSettings(settings);
        }

        internal static List<QamelSettings> FindSettings()
        {
            var settings = new List<QamelSettings>();
            // Inspect all settings assets, including duplicates and disabled ones.
            // A disabled capture switch does not strip serialized credentials.
            foreach (string guid in AssetDatabase.FindAssets("t:QamelSettings"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                    if (asset is QamelSettings value) settings.Add(value);
            }
            return settings;
        }

        internal static void ValidateSettings(IEnumerable<QamelSettings> settings,
            Func<string, string, bool> verify)
        {
            foreach (var value in settings)
            {
                if (value == null || string.IsNullOrWhiteSpace(value.apiKey)) continue;
                string endpoint = IngestRoutes.Normalize(value.endpoint);
                bool approved = false;
                if (IsSecureEndpoint(endpoint))
                {
                    try { approved = verify(endpoint, value.apiKey.Trim()); }
                    catch (Exception) { /* Never print transport errors that could contain credentials. */ }
                }
                if (!approved)
                    throw new BuildFailedException(
                        "[Qamel] Player build blocked: a Qamel settings key could not be verified as capture-only. " +
                        "Use an active capture upload key from your project's API keys page and a reachable, " +
                        "updated ingest server. Remove the key to build without uploads. Disabling capture " +
                        "alone does not remove the embedded key.");
            }
        }

        internal static bool IsSecureEndpoint(string endpoint)
        {
            return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
                string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) &&
                string.IsNullOrEmpty(uri.Fragment) &&
                (uri.Scheme == Uri.UriSchemeHttps ||
                 (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
        }

        internal static bool VerifyKey(string endpoint, string key)
        {
            if (!IsSecureEndpoint(endpoint)) return false;
            // This runs before the build, including in CI. HttpClient avoids
            // depending on Editor update callbacks while the build hook waits.
            using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) })
            using (var request = new HttpRequestMessage(HttpMethod.Post,
                endpoint + IngestRoutes.HealthPath))
            {
                request.Headers.TryAddWithoutValidation(IngestHeaders.Authorization, IngestHeaders.Bearer(key));
                request.Headers.TryAddWithoutValidation(IngestHeaders.Plugin, IngestHeaders.PluginValue());
                request.Content = new StringContent(QamelHealthCheck.BuildPayload(), Encoding.UTF8, "application/json");
                using (var response = client.SendAsync(request).GetAwaiter().GetResult())
                    return IsCaptureOnlyResponse((long)response.StatusCode,
                        response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            }
        }

        [Serializable]
        sealed class HealthResponse
        {
            public bool ok = false;
            public bool captureOnly = false;
        }

        internal static bool IsCaptureOnlyResponse(long status, string json)
        {
            if (status != 200 || string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                var response = JsonUtility.FromJson<HealthResponse>(json);
                return response != null && response.ok && response.captureOnly;
            }
            catch (Exception) { return false; }
        }
    }
}
