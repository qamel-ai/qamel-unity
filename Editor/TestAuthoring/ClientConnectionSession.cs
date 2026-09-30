using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace QamelCapture.Editor.TestAuthoring
{
    [InitializeOnLoad]
    internal static class ClientConnectionSession
    {
        static PendingClientConnection _pending;
        static UnityWebRequest _request;
        static double _nextAttempt;
        internal static string Status { get; private set; }
        internal static bool HasPending => _pending != null ||
            !string.IsNullOrEmpty(TestLabPreferences.PendingConnection);
        static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        static ClientConnectionSession()
        {
            // Ordinary batch test discovery must not redeem a saved user request.
            if (Application.isBatchMode) return;
            try { _pending = JsonUtility.FromJson<PendingClientConnection>(TestLabPreferences.PendingConnection); }
            catch (Exception) { TestLabPreferences.PendingConnection = ""; }
            EditorApplication.update += Update;
            AssemblyReloadEvents.beforeAssemblyReload += Suspend;
            EditorApplication.quitting += Suspend;
        }

        internal static bool HandleLink(Uri uri)
        {
            if (!ClientConnectionProtocol.TryReadLink(uri, out var project, out var endpoint, out var state, out var code)) return false;
            if (!TestLabPreferences.IsEnabled) { SetStatus(TestLabPreferences.DisabledMessage); return true; }
            if (Application.isBatchMode) return true;
            if (endpoint != null)
            {
                if (Busy()) { SetStatus("Finish your current recording or Run and clear the local draft before connecting."); return true; }
                if (ClientConnectionProtocol.IsValid(_pending, Now, TestLabPreferences.AuthoringFingerprint))
                {
                    SetStatus("A connection is already waiting for approval. Finish it, or cancel it in Qamel settings.");
                    if (string.IsNullOrEmpty(_pending.code)) Application.OpenURL(ClientConnectionProtocol.ApprovalUrl(_pending));
                    return true;
                }
                Suspend();
                _nextAttempt = 0;
                string label = new DirectoryInfo(Application.dataPath).Parent.Name + " on " + SystemInfo.deviceName;
                _pending = new PendingClientConnection { projectId = project, endpoint = endpoint,
                    verifier = ClientConnectionProtocol.RandomToken(), state = ClientConnectionProtocol.RandomToken(),
                    apiKey = ClientConnectionProtocol.CreateKey(), fingerprint = TestLabPreferences.AuthoringFingerprint,
                    expiresAt = Now + 600, label = label.Substring(0, Math.Min(label.Length, 120)) };
                Remember();
                SetStatus("Opening Qamel to approve this Unity connection.");
            }
            else if (ClientConnectionProtocol.MatchesCallback(_pending, project, state, code, Now, TestLabPreferences.AuthoringFingerprint))
            {
                _pending.code = code;
                Remember();
                SetStatus("Finishing the Qamel connection.");
            }
            else SetStatus("This connection callback does not match a pending request in this Unity project. Start again from Qamel.");
            return true;
        }

        static bool Busy() => TestLabSession.IsConnectedRunBusy ||
            TestLabSession.IsBrowserTestCreationActive || TestLabSession.IsSaveBusy ||
            TestLabSession.IsSavedReplayBusy || TestLabSession.Controller?.CurrentDraft != null ||
            (TestLabSession.Controller != null && TestLabSession.Controller.State != QamelCapture.TestAuthoring.TestAuthoringControllerState.Idle);

        static void Update()
        {
            if (_pending == null) return;
            if (!TestLabPreferences.IsEnabled) { Cancel(); return; }
            try { Continue(); }
            catch (Exception) { Finish("Could not finish connecting. Start again from Qamel."); }
        }

        static void Continue()
        {
            if (!ClientConnectionProtocol.IsValid(_pending, Now, TestLabPreferences.AuthoringFingerprint))
            { Finish("The connection request expired or your settings changed. Start again from Qamel."); return; }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode != EditorApplication.isPlaying) return;
            if (!_pending.browserOpened)
            {
                _pending.browserOpened = true;
                Remember();
                Application.OpenURL(ClientConnectionProtocol.ApprovalUrl(_pending));
            }
            if (string.IsNullOrEmpty(_pending.code)) return;
            if (Busy()) { Finish("Connection cancelled to protect your current Test Lab work. Finish it before reconnecting."); return; }
            if (_request == null)
            {
                if (EditorApplication.timeSinceStartup < _nextAttempt) return;
                _request = new UnityWebRequest(_pending.endpoint + "/api/v1/client-connections/exchange", "POST") {
                    uploadHandler = new UploadHandlerRaw(ClientConnectionProtocol.ExchangeBytes(_pending)),
                    downloadHandler = new LimitedResponse(), timeout = 15, redirectLimit = 0,
                };
                _request.SetRequestHeader("Content-Type", "application/json");
                _request.SendWebRequest();
                return;
            }
            if (!_request.isDone) return;
            long status = _request.responseCode;
            bool success = status == 200 && _request.result == UnityWebRequest.Result.Success &&
                ClientConnectionProtocol.AcceptsResponse(((LimitedResponse)_request.downloadHandler).Body, _pending.projectId);
            Suspend();
            if (success)
            {
                TestLabPreferences.AuthoringEndpoint = _pending.endpoint;
                TestLabPreferences.AuthoringApiKey = _pending.apiKey;
                Finish("Qamel access is saved. Checking the connection.");
                TestAuthoringHealthCheck.OnPreferencesChanged();
                TestAuthoringHealthCheck.CheckNow();
            }
            else if (status == 0 || status == 408 || status == 429 || status >= 500)
            {
                _nextAttempt = EditorApplication.timeSinceStartup + 5;
                SetStatus("Qamel is temporarily unavailable. Retrying this connection.");
            }
            else Finish("Qamel could not accept this approval. Start again from Qamel.");
        }

        // No unbounded DownloadHandlerBuffer for an unauthenticated exchange response.
        sealed class LimitedResponse : DownloadHandlerScript
        {
            readonly MemoryStream _bytes = new MemoryStream();
            internal string Body => System.Text.Encoding.UTF8.GetString(_bytes.ToArray());
            protected override bool ReceiveData(byte[] data, int length)
            {
                if (data == null || length == 0) return true;
                if (_bytes.Length + length > 4096) return false;
                _bytes.Write(data, 0, length);
                return true;
            }
        }

        internal static void Cancel() => Finish("Pending connection cancelled. Existing Qamel access is unchanged.");

        static void Remember() => TestLabPreferences.PendingConnection = JsonUtility.ToJson(_pending);
        static void Suspend() { _request?.Abort(); _request?.Dispose(); _request = null; }
        static void Finish(string status) { Suspend(); _pending = null; TestLabPreferences.PendingConnection = ""; SetStatus(status); }
        static void SetStatus(string status) { Status = status; Debug.Log("[Qamel] " + status); }
    }
}
