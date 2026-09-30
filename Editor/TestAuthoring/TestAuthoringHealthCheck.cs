using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace QamelCapture.Editor.TestAuthoring
{
    internal enum TestAuthoringHealthState
    {
        Idle,
        Checking,
        Connected,
        InvalidConfiguration,
        Rejected,
        InvalidResponse,
        Unreachable,
    }

    internal sealed class TestAuthoringHealthResult
    {
        public TestAuthoringHealthState State;
        public string ProjectId;
        public string ProjectName;
        public string Error;
    }

    /// <summary>
    /// Checks the Editor-only authoring connection without sharing credentials
    /// with the runtime capture settings or assemblies.
    /// </summary>
    [InitializeOnLoad]
    internal static class TestAuthoringHealthCheck
    {
        const int RequestTimeoutSeconds = 10;
        const int HealthContractVersion = 1;
        const double AutomaticRetryIntervalSeconds = 15;

        static string _observedFingerprint;
        static string _connectedFingerprint;
        static string _requestFingerprint;
        static UnityWebRequest _request;
        static double _nextAutomaticRetryAt;

        public static TestAuthoringHealthState State { get; private set; }
        public static string ProjectId { get; private set; }
        public static string ProjectName { get; private set; }
        public static string LastError { get; private set; }
        public static bool IsChecking => State == TestAuthoringHealthState.Checking;
        public static bool CanCheck => TestLabPreferences.IsEnabled && !IsChecking &&
            TryGetConfiguration(out _, out _, out _, out _);
        public static bool IsConnected =>
            TestLabPreferences.IsEnabled &&
            State == TestAuthoringHealthState.Connected &&
            string.Equals(
                _connectedFingerprint,
                CurrentFingerprint(),
                StringComparison.Ordinal);

        static TestAuthoringHealthCheck()
        {
            RestoreSessionConnection();
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.quitting += Shutdown;
        }

        public static void ObservePreferences()
        {
            if (!TestLabPreferences.IsEnabled || Application.isBatchMode) return;

            string fingerprint = CurrentFingerprint();
            if (string.Equals(
                    fingerprint,
                    _observedFingerprint,
                    StringComparison.Ordinal))
                return;

            _observedFingerprint = fingerprint;
            Invalidate();
        }

        public static void OnPreferencesChanged()
        {
            if (Application.isBatchMode) return;
            _observedFingerprint = CurrentFingerprint();
            Invalidate();
        }

        public static void CheckNow()
        {
            if (Application.isBatchMode) return;
            CheckForExplicitCliRun();
        }

        // Opt-in entry point for the internal executeMethod harness. Ordinary
        // batch test discovery must not connect to a user's saved project.
        internal static void CheckForExplicitCliRun()
        {
            if (!TestLabPreferences.IsEnabled) return;
            _observedFingerprint = CurrentFingerprint();
            StartCheck();
        }

        public static void MaintainConnection(bool shouldConnect)
        {
            if (!shouldConnect || Application.isBatchMode ||
                IsConnected || IsChecking ||
                !CanCheck || !ShouldRetryAutomatically(State))
                return;

            double now = EditorApplication.timeSinceStartup;
            if (now < _nextAutomaticRetryAt) return;
            StartCheck();
        }

        internal static bool ShouldRetryAutomatically(
            TestAuthoringHealthState state)
        {
            return state == TestAuthoringHealthState.Idle ||
                   state == TestAuthoringHealthState.Unreachable;
        }

        static void Invalidate()
        {
            CancelRequest();
            ClearProject();
            _nextAutomaticRetryAt = 0;

            if (!TryGetConfiguration(out _, out _, out var invalidState, out var error))
            {
                State = invalidState;
                LastError = error;
                return;
            }

            State = TestAuthoringHealthState.Idle;
            LastError = null;
        }

        static void StartCheck()
        {
            if (!TestLabPreferences.IsEnabled) return;
            CancelRequest();
            ClearProject();
            _nextAutomaticRetryAt =
                EditorApplication.timeSinceStartup +
                AutomaticRetryIntervalSeconds;
            if (!TryGetConfiguration(
                    out var endpoint,
                    out var apiKey,
                    out var invalidState,
                    out var error))
            {
                State = invalidState;
                LastError = error;
                return;
            }

            State = TestAuthoringHealthState.Checking;
            LastError = null;
            _requestFingerprint = ConfigurationFingerprint(endpoint, apiKey);
            try
            {
                _request = UnityWebRequest.Get(
                    TestDefinitionRoutes.RegistrationUrl(endpoint));
                _request.redirectLimit = 0;
                _request.SetRequestHeader(
                    IngestHeaders.Authorization,
                    IngestHeaders.Bearer(apiKey));
                _request.SetRequestHeader(
                    IngestHeaders.Plugin,
                    IngestHeaders.PluginValue());
                _request.timeout = RequestTimeoutSeconds;
                _request.SendWebRequest();
                EditorApplication.update += Poll;
            }
            catch (Exception exception)
            {
                CancelRequest();
                State = TestAuthoringHealthState.Unreachable;
                LastError = "Could not start the authoring connection check: " +
                            exception.Message;
            }
        }

        static void Poll()
        {
            if (_request == null)
            {
                EditorApplication.update -= Poll;
                return;
            }
            if (!_request.isDone) return;

            EditorApplication.update -= Poll;
            var request = _request;
            _request = null;
            long responseCode = request.responseCode;
            string responseBody = request.downloadHandler?.text;
            string requestError = request.error;
            string requestFingerprint = _requestFingerprint;
            _requestFingerprint = null;
            bool succeeded = request.result == UnityWebRequest.Result.Success &&
                             responseCode >= 200 && responseCode < 300;
            request.Dispose();

            if (!string.Equals(
                    requestFingerprint,
                    CurrentFingerprint(),
                    StringComparison.Ordinal))
            {
                Invalidate();
                return;
            }

            Apply(EvaluateResponse(
                succeeded,
                responseCode,
                responseBody,
                requestError), requestFingerprint);
        }

        static void Apply(
            TestAuthoringHealthResult result,
            string requestFingerprint)
        {
            ClearProject();
            State = result.State;
            LastError = result.Error;
            if (result.State != TestAuthoringHealthState.Connected) return;

            ProjectId = result.ProjectId;
            ProjectName = result.ProjectName;
            _connectedFingerprint = requestFingerprint;
            TestLabPreferences.RememberCheckedConnection(
                requestFingerprint,
                ProjectId,
                ProjectName);
        }

        static bool TryGetConfiguration(
            out string endpoint,
            out string apiKey,
            out TestAuthoringHealthState invalidState,
            out string error)
        {
            endpoint = TestLabPreferences.AuthoringEndpoint;
            apiKey = (TestLabPreferences.AuthoringApiKey ?? "").Trim();
            invalidState = TestAuthoringHealthState.InvalidConfiguration;
            error = null;

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                invalidState = TestAuthoringHealthState.Idle;
                return false;
            }
            if (!TestDefinitionRoutes.IsValidBase(endpoint))
            {
                error = "Use an HTTPS Qamel endpoint or HTTP localhost for development.";
                return false;
            }
            if (!TestDefinitionRoutes.IsValidProjectApiKey(apiKey))
            {
                invalidState = TestAuthoringHealthState.Rejected;
                error = "Paste the complete project API key beginning with qa_key_.";
                return false;
            }
            return true;
        }

        static void CancelRequest()
        {
            EditorApplication.update -= Poll;
            _requestFingerprint = null;
            if (_request == null) return;
            if (!_request.isDone) _request.Abort();
            _request.Dispose();
            _request = null;
        }

        static void Shutdown()
        {
            CancelRequest();
        }

        internal static void Stop()
        {
            CancelRequest();
            ClearProject();
            State = TestAuthoringHealthState.Idle;
            LastError = null;
        }

        static void ClearProject()
        {
            ProjectId = null;
            ProjectName = null;
            _connectedFingerprint = null;
            TestLabPreferences.ClearCheckedConnection();
        }

        static string CurrentFingerprint()
        {
            return TestLabPreferences.AuthoringFingerprint;
        }

        static string ConfigurationFingerprint(string endpoint, string apiKey)
        {
            return TestLabPreferences.ComputeAuthoringFingerprint(endpoint, apiKey);
        }

        static void RestoreSessionConnection()
        {
            string fingerprint = CurrentFingerprint();
            _observedFingerprint = fingerprint;
            if (TestLabPreferences.TryGetCheckedConnection(
                    fingerprint,
                    out var projectId,
                    out var projectName))
            {
                State = TestAuthoringHealthState.Connected;
                ProjectId = projectId;
                ProjectName = projectName;
                _connectedFingerprint = fingerprint;
                LastError = null;
                return;
            }

            Invalidate();
        }

        internal static bool TryReadResponse(
            string json,
            out string projectId,
            out string projectName,
            out string error)
        {
            projectId = null;
            projectName = null;
            error = null;

            HealthResponse response;
            try
            {
                response = JsonUtility.FromJson<HealthResponse>(json);
            }
            catch (Exception exception)
            {
                error = "Qamel returned invalid authoring health JSON: " +
                        exception.Message;
                return false;
            }

            if (response == null ||
                response.contractVersion != HealthContractVersion ||
                response.latestDefinitionContractVersion <
                    RecordedTestDefinitionSerializer.ContractVersion ||
                !response.ok ||
                !Guid.TryParse(response.projectId, out _) ||
                string.IsNullOrWhiteSpace(response.projectName))
            {
                error = "Qamel returned an incomplete authoring health response.";
                return false;
            }

            projectId = response.projectId;
            projectName = response.projectName.Trim();
            return true;
        }

        internal static TestAuthoringHealthResult EvaluateResponse(
            bool requestSucceeded,
            long responseCode,
            string responseBody,
            string requestError)
        {
            if (requestSucceeded && responseCode >= 200 && responseCode < 300)
            {
                if (TryReadResponse(
                        responseBody,
                        out var projectId,
                        out var projectName,
                        out var parseError))
                {
                    return new TestAuthoringHealthResult
                    {
                        State = TestAuthoringHealthState.Connected,
                        ProjectId = projectId,
                        ProjectName = projectName,
                    };
                }

                return new TestAuthoringHealthResult
                {
                    State = TestAuthoringHealthState.InvalidResponse,
                    Error = parseError,
                };
            }

            if (responseCode == 401 || responseCode == 403)
            {
                return new TestAuthoringHealthResult
                {
                    State = TestAuthoringHealthState.Rejected,
                    Error = "Qamel rejected this authoring connection. The key may be " +
                            "expired, revoked, for another project, or missing Test access.",
                };
            }

            string serverError = QamelJson.ExtractString(responseBody, "error");
            return new TestAuthoringHealthResult
            {
                State = TestAuthoringHealthState.Unreachable,
                Error = !string.IsNullOrWhiteSpace(serverError)
                    ? "Qamel could not verify the authoring connection (" +
                      responseCode + "): " + serverError + "."
                    : "Could not verify the authoring connection (" +
                      responseCode + " " + requestError + ").",
            };
        }

        [Serializable]
        sealed class HealthResponse
        {
            public int contractVersion;
            public int latestDefinitionContractVersion;
            public bool ok;
            public string projectId;
            public string projectName;
        }
    }
}
