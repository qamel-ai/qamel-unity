using System;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace QamelCapture.Editor.TestAuthoring
{
    internal static class ClientPresenceProtocol
    {
        internal const double IntervalSeconds = 10;
        internal static bool CanSend(bool enabled, bool batch, bool compiling, bool updating, string endpoint, string key) =>
            enabled && !batch && !compiling && !updating && TestDefinitionRoutes.IsValidBase(endpoint) &&
            TestDefinitionRoutes.IsValidProjectApiKey(key);

        [Serializable] sealed class Body { public int contractVersion = 1; public string installationId, name; }
        internal static byte[] Bytes(string installationId, string name)
        {
            if (!Guid.TryParseExact(installationId, "D", out var id) || id == Guid.Empty)
                throw new ArgumentException("Invalid client installation.");
            name = (name ?? "Unity Editor").Trim();
            if (name.Length == 0) name = "Unity Editor";
            // Bound by Unicode scalar values, without cutting a surrogate pair.
            if (name.Length > 200) name = name.Substring(0, char.IsHighSurrogate(name[199]) ? 199 : 200);
            return Encoding.UTF8.GetBytes(JsonUtility.ToJson(new Body { installationId = id.ToString("D"), name = name }));
        }

        internal static bool TryReadOpenLink(Uri uri, out string projectId)
        {
            projectId = null;
            if (uri == null || !uri.IsAbsoluteUri || uri.Scheme != "com.unity.editor" || uri.Host != "editor" ||
                !uri.IsDefaultPort || uri.Query != "" || uri.Fragment != "" || uri.UserInfo != "") return false;
            var parts = uri.AbsolutePath.Split('/');
            if (parts.Length != 4 || parts[1] != "qamelcapture" || parts[2] != "open" ||
                !Guid.TryParseExact(parts[3], "D", out var id) || id == Guid.Empty) return false;
            projectId = id.ToString("D");
            return true;
        }
    }

    // Independent of the Test Lab window, state providers, input adapters and
    // Play Mode. Presence never leases a Run or claims gameplay readiness.
    [InitializeOnLoad]
    internal static class ClientPresenceSession
    {
        const string PendingOpenKey = "Qamel.Client.PendingOpen";
        const string PendingUntilKey = "Qamel.Client.PendingUntil";
        static UnityWebRequest _request;
        static string _fingerprint;
        static string _rejectedFingerprint;
        static double _nextAttempt;
        static double _nextHealthCheck;

        static ClientPresenceSession()
        {
            if (Application.isBatchMode) return;
            if (!TestLabPreferences.IsEnabled) Stop();
            EditorApplication.update += Update;
            AssemblyReloadEvents.beforeAssemblyReload += Suspend;
            EditorApplication.quitting += Suspend;
        }

        internal static bool HandleOpenLink(Uri uri)
        {
            if (!ClientPresenceProtocol.TryReadOpenLink(uri, out var project)) return false;
            if (!TestLabPreferences.IsEnabled)
            {
                Debug.Log("[Qamel] " + TestLabPreferences.DisabledMessage);
                return true;
            }
            if (Application.isBatchMode) return true;
            SessionState.SetString(PendingOpenKey, project);
            SessionState.SetString(PendingUntilKey, DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds().ToString());
            _nextHealthCheck = 0;
            return true;
        }

        static void ContinueOpen()
        {
            string expected = SessionState.GetString(PendingOpenKey, "");
            if (expected.Length == 0) return;
            if (!long.TryParse(SessionState.GetString(PendingUntilKey, ""), out var deadline) ||
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= deadline)
            { FinishOpen("Could not reach Qamel. Check your connection and try Open Unity again."); return; }
            if (TestAuthoringHealthCheck.IsConnected)
            {
                if (TestAuthoringHealthCheck.ProjectId != expected)
                { FinishOpen("This Unity project is connected to a different Qamel project. Choose the matching project in Hub."); return; }
                _rejectedFingerprint = null;
                _nextAttempt = 0;
                FinishOpen("Unity is open and connected. The game will be prepared when you start a Test.");
            }
            else if (!TestAuthoringHealthCheck.IsChecking && EditorApplication.timeSinceStartup >= _nextHealthCheck)
            {
                _nextHealthCheck = EditorApplication.timeSinceStartup + 15;
                TestAuthoringHealthCheck.CheckNow();
            }
        }

        static void FinishOpen(string message)
        {
            SessionState.EraseString(PendingOpenKey);
            SessionState.EraseString(PendingUntilKey);
            Debug.Log("[Qamel] " + message);
        }

        static void Update()
        {
            if (Application.isBatchMode) return;
            if (!TestLabPreferences.IsEnabled) { if (_request != null) Suspend(); return; }
            try
            {
                if (!EditorApplication.isCompiling && !EditorApplication.isUpdating) ContinueOpen();
                string current = TestLabPreferences.AuthoringFingerprint;
                if (current != _fingerprint)
                { Suspend(); _fingerprint = current; _rejectedFingerprint = null; _nextAttempt = 0; }
                if (_request != null)
                {
                    if (!_request.isDone) return;
                    if (_request.responseCode == 401 || _request.responseCode == 403) _rejectedFingerprint = current;
                    Suspend();
                    _nextAttempt = EditorApplication.timeSinceStartup + ClientPresenceProtocol.IntervalSeconds;
                }
                if (_rejectedFingerprint == current || EditorApplication.timeSinceStartup < _nextAttempt ||
                    !ClientPresenceProtocol.CanSend(TestLabPreferences.IsEnabled, false, EditorApplication.isCompiling,
                        EditorApplication.isUpdating, TestLabPreferences.AuthoringEndpoint, TestLabPreferences.AuthoringApiKey)) return;
                _nextAttempt = EditorApplication.timeSinceStartup + ClientPresenceProtocol.IntervalSeconds;
                _request = new UnityWebRequest(TestDefinitionRoutes.Normalize(TestLabPreferences.AuthoringEndpoint) +
                    "/api/v1/client-connections/heartbeat", "POST") {
                    uploadHandler = new UploadHandlerRaw(ClientPresenceProtocol.Bytes(TestLabPreferences.RunnerInstallationId,
                        Application.productName + " on " + SystemInfo.deviceName)),
                    downloadHandler = new DiscardResponse(), timeout = 8, redirectLimit = 0,
                };
                _request.SetRequestHeader("Content-Type", "application/json");
                _request.SetRequestHeader("Authorization", "Bearer " + TestLabPreferences.AuthoringApiKey.Trim());
                _request.SendWebRequest();
            }
            catch (Exception)
            {
                // Failed check-ins expire in the browser. Never log keys, URLs
                // or noisy repeated errors, and never interfere with gameplay.
                Suspend();
                _nextAttempt = EditorApplication.timeSinceStartup + ClientPresenceProtocol.IntervalSeconds;
            }
        }

        sealed class DiscardResponse : DownloadHandlerScript
        {
            int _received;
            protected override bool ReceiveData(byte[] data, int length) { _received += length; return _received <= 4096; }
        }
        internal static void Stop()
        {
            Suspend();
            SessionState.EraseString(PendingOpenKey);
            SessionState.EraseString(PendingUntilKey);
        }
        static void Suspend() { _request?.Abort(); _request?.Dispose(); _request = null; }
    }
}
