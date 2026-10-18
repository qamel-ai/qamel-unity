using System;
using UnityEditor;

namespace QamelCapture.Editor
{
    internal static class QamelSettingsLink
    {
        internal static string PlayKeysUrl(string ingestEndpoint)
        {
            // Local development serves the app and ingest API on the same host.
            if (Uri.TryCreate(ingestEndpoint, UriKind.Absolute, out var endpoint) &&
                (endpoint.Scheme == "http" || endpoint.Scheme == "https") &&
                endpoint.IsLoopback && string.IsNullOrEmpty(endpoint.UserInfo))
                return endpoint.GetLeftPart(UriPartial.Authority) + "/play-keys";
            return "https://qamel.ai/play-keys";
        }

        internal static bool IsSettingsLink(Uri uri)
        {
            return uri != null && uri.IsAbsoluteUri &&
                   uri.Scheme == "com.unity.editor" && uri.Host == "editor" &&
                   string.IsNullOrEmpty(uri.UserInfo) && uri.IsDefaultPort &&
                   string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) &&
                   (uri.AbsolutePath == "/qamelcapture/settings" ||
                    uri.AbsolutePath == "/qamelcapture/settings/");
        }

        internal static bool Handle(Uri uri)
        {
            if (!IsSettingsLink(uri)) return false;
            // Opening settings never installs a key or enables experimental testing.
            EditorApplication.delayCall += OpenSettings;
            return true;
        }

        internal static void OpenSettings()
        {
            SettingsService.OpenProjectSettings("Project/Qamel");
        }
    }
}
