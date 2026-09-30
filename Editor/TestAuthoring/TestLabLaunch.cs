using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace QamelCapture.Editor.TestAuthoring
{
    // Hub chooses the local project. The link supplies identifiers only, never
    // credentials, an endpoint, a local path, or executable instructions.
    [InitializeOnLoad]
    internal static class TestLabLaunch
    {
        const string StatusKey = "Qamel.TestLab.LaunchStatus";
        const string PendingKey = "Qamel.TestLab.PendingLaunch";
        static SavedTestCatalogOperation _catalog;
        static string _projectId;
        static string _versionId;
        static string _fingerprint;
        static double _deadline;
        static bool _approved;
        static bool _healthStarted;
        static double _nextHealthAttemptAt;

        [Serializable]
        sealed class PendingRequest
        {
            public string projectId;
            public string versionId;
            public string fingerprint;
            public double deadline;
            public bool approved;
        }

        static TestLabLaunch()
        {
            EditorApplication.update += Update;
            AssemblyReloadEvents.beforeAssemblyReload += Suspend;
            EditorApplication.quitting += Cancel;
            var json = SessionState.GetString(PendingKey, "");
            if (!string.IsNullOrEmpty(json))
            {
                var request = JsonUtility.FromJson<PendingRequest>(json);
                _projectId = request.projectId;
                _versionId = request.versionId;
                _fingerprint = request.fingerprint;
                _deadline = request.deadline;
                _approved = request.approved;
            }
        }

        public static string Status => SessionState.GetString(StatusKey, "");

#if UNITY_6000_3_OR_NEWER
        [DeeplinkHandler("qamelcapture")]
#endif
        public static void Open(Uri uri)
        {
            if (!TestLabPreferences.IsEnabled)
            {
                SetStatus(TestLabPreferences.DisabledMessage);
                return;
            }
            if (ClientPresenceSession.HandleOpenLink(uri)) return;
            if (ClientConnectionSession.HandleLink(uri)) return;
            if (!TryReadRequest(uri, out var projectId, out var versionId))
            {
                SetStatus("This Qamel preparation link is invalid.");
                return;
            }
            // Persist the request before deferring UI work. A cold project can
            // reload its domain before the next delayCall is delivered.
            Begin(projectId, versionId);
        }

        internal static bool TryReadRequest(Uri uri, out string projectId, out string versionId)
        {
            projectId = null;
            versionId = null;
            if (uri == null || !uri.IsAbsoluteUri ||
                uri.Scheme != "com.unity.editor" || uri.Host != "editor" ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
                return false;
            var parts = uri.AbsolutePath.Split('/');
            if (parts.Length != 5 || parts[1] != "qamelcapture" || parts[2] != "prepare" ||
                !Guid.TryParseExact(parts[3], "D", out var project) || project == Guid.Empty ||
                !Guid.TryParseExact(parts[4], "D", out var version) || version == Guid.Empty)
                return false;
            projectId = project.ToString("D");
            versionId = version.ToString("D");
            return true;
        }

        static void Begin(string projectId, string versionId)
        {
            EditorApplication.delayCall += TestLabWindow.Open;
            if (_projectId != null)
            {
                SetStatus("A Test is already being prepared. Wait for it to finish.");
                return;
            }
            _projectId = projectId;
            _versionId = versionId;
            _fingerprint = TestLabPreferences.AuthoringFingerprint;
            _deadline = EditorApplication.timeSinceStartup + 120;
            _approved = false;
            _healthStarted = false;
            _nextHealthAttemptAt = 0;
            RememberPending();
            SetStatus("Checking the project and saved Test before preparing Unity.");
        }

        static void Update()
        {
            if (_projectId == null) return;
            if (!TestLabPreferences.IsEnabled) { Cancel(); return; }
            try
            {
                Continue();
            }
            catch (Exception)
            {
                Finish("Unity could not prepare this Test. Check the scene and project connection, then try again.");
            }
        }

        static void Continue()
        {
            if (_fingerprint != TestLabPreferences.AuthoringFingerprint)
            {
                Finish("The Qamel connection changed. Use Open Unity again on the queued Run.");
                return;
            }
            if (EditorApplication.timeSinceStartup > _deadline)
            {
                Finish("Preparing Unity timed out. Check the Qamel connection and try again.");
                return;
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode != EditorApplication.isPlaying)
                return;
            if (!CanPrepare(out var blockedReason))
            {
                Finish(blockedReason);
                return;
            }
            if (!_healthStarted)
            {
                if (EditorApplication.timeSinceStartup < _nextHealthAttemptAt) return;
                if (TestAuthoringHealthCheck.IsChecking) return;
                if (!TestAuthoringHealthCheck.CanCheck)
                {
                    Finish("Set up the Qamel project connection in Test Lab, then use Open Unity again on the queued Run.");
                    return;
                }
                _healthStarted = true;
                TestAuthoringHealthCheck.CheckNow();
                return;
            }
            if (TestAuthoringHealthCheck.IsChecking) return;
            if (!TestAuthoringHealthCheck.IsConnected)
            {
                if (ShouldRetryConnection(TestAuthoringHealthCheck.State))
                {
                    _healthStarted = false;
                    _nextHealthAttemptAt = EditorApplication.timeSinceStartup + 5;
                    SetStatus("Qamel did not respond yet. Retrying the connection automatically while Unity prepares.");
                    return;
                }
                Finish(TestAuthoringHealthCheck.LastError ?? "Qamel could not verify the project connection.");
                return;
            }
            if (!string.Equals(_projectId, TestAuthoringHealthCheck.ProjectId, StringComparison.OrdinalIgnoreCase))
            {
                Finish("This Unity project is connected to a different Qamel project. Choose the matching Unity project in Hub.");
                return;
            }
            if (_catalog == null)
                _catalog = new SavedTestCatalogOperation(
                    TestLabPreferences.AuthoringEndpoint,
                    TestLabPreferences.AuthoringApiKey,
                    _projectId);
            _catalog.Tick();
            if (!_catalog.IsFinished) return;
            if (_catalog.State != SavedTestCatalogState.Succeeded)
            {
                Finish(_catalog.Status);
                return;
            }
            var version = _catalog.Catalog.Tests.SelectMany(test => test.Versions)
                .FirstOrDefault(item => string.Equals(item.TestVersionId, _versionId, StringComparison.OrdinalIgnoreCase));
            if (version == null || !version.ArtifactsReady)
            {
                Finish("The requested Test version is missing or its uploads are incomplete.");
                return;
            }
            string scenePath = version.Requirements?.ActiveScenePath;
            if (!IsProjectScenePath(scenePath) || AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) == null)
            {
                Finish("The Test's saved scene is not present in this Unity project. Choose the matching project in Hub.");
                return;
            }
            if (!CanPrepare(out var reason))
            {
                Finish(reason);
                return;
            }
            if (EditorApplication.isPlaying && SceneManager.GetActiveScene().path == scenePath)
            {
                Finish("Test Lab is enabled. The queued Test will start automatically when the Local runner is ready.");
                return;
            }
            // A deep link may come from any website. Require an explicit local
            // confirmation before switching scenes or starting gameplay.
            if (!_approved)
            {
                if (!EditorUtility.DisplayDialog("Run Test in Unity",
                        "Prepare '" + version.TestName + "' in this project?\n\nQamel will " +
                        (EditorApplication.isPlaying ? "stop the current Play Mode session, " : "") +
                        "open " + scenePath + " and enter Play Mode. The queued Test will then start automatically.",
                        "Continue", "Cancel"))
                {
                    Finish("Preparation cancelled. Cancel the queued Run in Qamel if you no longer want it to run.");
                    return;
                }
                _approved = true;
                _deadline = EditorApplication.timeSinceStartup + 120;
                RememberPending();
            }
            if (EditorApplication.isPlaying)
            {
                SetStatus("Leaving Play Mode to open the saved Test scene.");
                EditorApplication.isPlaying = false;
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Finish("Preparation cancelled. Your scene was left open. Cancel the queued Run in Qamel if it is no longer wanted.");
                return;
            }
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            Finish("Play Mode requested. The queued Test will start automatically when the Local runner is ready.");
            EditorApplication.isPlaying = true;
        }

        internal static bool IsProjectScenePath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   path.StartsWith("Assets/", StringComparison.Ordinal) &&
                   path.EndsWith(".unity", StringComparison.Ordinal) &&
                   !path.Contains("\\") && !path.Split('/').Any(part => part == ".." || part == "." || part.Length == 0);
        }

        internal static bool ShouldRetryConnection(TestAuthoringHealthState state)
        {
            return state == TestAuthoringHealthState.Unreachable ||
                   state == TestAuthoringHealthState.Idle;
        }

        static bool CanPrepare(out string reason)
        {
            reason = null;
            if (Application.isBatchMode || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode != EditorApplication.isPlaying)
                reason = "Wait for Unity to finish loading or changing Play Mode, then try again.";
            else if (TestLabSession.IsConnectedRunBusy || TestLabSession.IsSaveBusy ||
                     TestLabSession.IsSavedReplayBusy || TestLabSession.Controller?.CurrentDraft != null ||
                     (TestLabSession.Controller != null && TestLabSession.Controller.State !=
                         QamelCapture.TestAuthoring.TestAuthoringControllerState.Idle))
                reason = "Finish your current Test Lab work and clear the local draft before preparing another Test.";
            return reason == null;
        }

        static void Finish(string status)
        {
            Cancel();
            SetStatus(status);
        }

        static void SetStatus(string status)
        {
            SessionState.SetString(StatusKey, status);
            Debug.Log("[Qamel Test Lab] " + status);
        }

        internal static void Cancel()
        {
            if (_projectId != null)
                SessionState.SetString(StatusKey, "Preparation interrupted. Use Open Unity again on the queued Run.");
            Suspend();
            SessionState.EraseString(PendingKey);
            _projectId = null;
            _versionId = null;
            _fingerprint = null;
            _approved = false;
            _healthStarted = false;
        }

        static void RememberPending()
        {
            // Only identifiers and approval state survive Editor domain reloads.
            // This is neither a persisted Test artifact nor a stored credential.
            SessionState.SetString(PendingKey, JsonUtility.ToJson(new PendingRequest
            {
                projectId = _projectId, versionId = _versionId,
                fingerprint = _fingerprint, deadline = _deadline, approved = _approved
            }));
        }

        static void Suspend()
        {
            _catalog?.Dispose();
            _catalog = null;
        }
    }
}
