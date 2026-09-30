using System;
using System.Globalization;
using System.Text;
using QamelCapture.TestAuthoring;
using UnityEngine;
using UnityEngine.Networking;

namespace QamelCapture.Editor.TestAuthoring
{
    internal static class ConnectedTestRunnerReadiness
    {
        public static bool CanYieldLocalController(
            TestAuthoringControllerState state,
            bool hasCurrentDraft,
            bool currentDraftIsDurable)
        {
            if (state == TestAuthoringControllerState.Idle)
                return !hasCurrentDraft;
            if (!hasCurrentDraft || !currentDraftIsDurable)
                return false;

            return state == TestAuthoringControllerState.DraftReady ||
                   state == TestAuthoringControllerState.ReplayError ||
                   state == TestAuthoringControllerState.ReplayCancelled ||
                   state == TestAuthoringControllerState.AnchorExpired ||
                   state == TestAuthoringControllerState.Error;
        }
    }

    internal sealed class TestRunnerLease
    {
        public TestRunnerLease(
            string runnerId,
            string testRunId,
            string testVersionId,
            DateTimeOffset? leaseExpiresAt)
        {
            RunnerId = runnerId;
            TestRunId = testRunId;
            TestVersionId = testVersionId;
            LeaseExpiresAt = leaseExpiresAt;
        }

        public string RunnerId { get; }
        public string TestRunId { get; }
        public string TestVersionId { get; }
        public DateTimeOffset? LeaseExpiresAt { get; }
        public bool HasRun => !string.IsNullOrWhiteSpace(TestRunId);
    }

    internal enum TestRunnerLeaseState
    {
        Polling,
        Succeeded,
        Failed,
        Cancelled,
    }

    internal sealed class TestRunnerLeaseOperation : IDisposable
    {
        const int RequestTimeoutSeconds = 30;

        UnityWebRequest _request;
        bool _disposed;

        public TestRunnerLeaseOperation(
            string endpoint,
            string apiKey,
            string requestJson,
            bool startImmediately = true)
        {
            if (!TestDefinitionRoutes.IsValidBase(endpoint))
                throw new ArgumentException(
                    "Use an HTTPS Qamel endpoint or HTTP localhost for development.",
                    nameof(endpoint));
            string key = (apiKey ?? "").Trim();
            if (!TestDefinitionRoutes.IsValidProjectApiKey(key))
                throw new ArgumentException(
                    "A complete project API key beginning with qa_key_ is required.",
                    nameof(apiKey));
            if (string.IsNullOrWhiteSpace(requestJson))
                throw new ArgumentException("A runner descriptor is required.", nameof(requestJson));

            State = TestRunnerLeaseState.Polling;
            Status = "Checking Qamel for a compatible queued Run.";
            if (!startImmediately) return;

            _request = AuthorizedJsonPost(
                TestDefinitionRoutes.RunnerLeaseUrl(endpoint),
                key,
                requestJson);
            _request.timeout = RequestTimeoutSeconds;
            _request.SendWebRequest();
        }

        public TestRunnerLeaseState State { get; private set; }
        public string Status { get; private set; }
        public TestRunnerLease Lease { get; private set; }
        public bool IsFinished => State == TestRunnerLeaseState.Succeeded ||
                                  State == TestRunnerLeaseState.Failed ||
                                  State == TestRunnerLeaseState.Cancelled;

        public void Tick()
        {
            if (_disposed || IsFinished || _request == null || !_request.isDone) return;
            if (_request.result != UnityWebRequest.Result.Success)
            {
                Fail(SavedTestCatalogOperation.RequestFailure(_request, "Runner check"));
                return;
            }
            if (!TryReadLease(_request.downloadHandler?.text, out var lease, out var error))
            {
                Fail(error);
                return;
            }

            Lease = lease;
            DisposeRequest();
            State = TestRunnerLeaseState.Succeeded;
            Status = lease.HasRun
                ? "Qamel assigned a queued Run. Downloading its immutable Test Version."
                : "Local runner is ready. Waiting for a Run from Qamel.";
        }

        public void Cancel()
        {
            if (IsFinished) return;
            _request?.Abort();
            DisposeRequest();
            State = TestRunnerLeaseState.Cancelled;
            Status = "Runner check cancelled.";
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (!IsFinished) Cancel();
            DisposeRequest();
            _disposed = true;
        }

        internal static bool TryBuildRequest(
            string installationId,
            string runnerName,
            TestStateRegistry registry,
            TestRunExecutionEnvironment environment,
            out string json,
            out string error)
        {
            json = null;
            error = null;
            if (!Guid.TryParse(installationId, out _))
            {
                error = "The local runner installation ID is invalid.";
                return false;
            }
            if (registry == null || !registry.TryGetProvider(out var registered))
            {
                error = "Register a state provider before running Tests.";
                return false;
            }

            string name = string.IsNullOrWhiteSpace(runnerName)
                ? "Unity Test Lab"
                : runnerName.Trim();
            if (!(registered.Provider is IQamelTestOutcomeProvider outcomeProvider))
            {
                var humanReviewDto = new HumanReviewLeaseRequestDto
                {
                    contractVersion = 1,
                    runner = new HumanReviewRunnerDto
                    {
                        installationId = installationId,
                        name = name,
                        unityRelease = environment.UnityRelease,
                        inputSystemVersion = environment.InputSystemVersion,
                        runtimePlatform = environment.RuntimePlatform,
                        activeScenePath = environment.ActiveScenePath,
                        stateProviderId = registered.ProviderId,
                        stateFormatVersion = registered.StateFormatVersion,
                        stageAdapters = new[]
                        {
                            SavedTestReplayCompatibility.RestoreStateAdapter,
                            SavedTestReplayCompatibility.InputTraceAdapter,
                            SavedTestReplayCompatibility.HumanReviewAdapter,
                        },
                    },
                };
                json = JsonUtility.ToJson(humanReviewDto);
                return true;
            }

            int outcomeFormatVersion;
            try
            {
                outcomeFormatVersion = outcomeProvider.OutcomeFormatVersion;
            }
            catch (Exception exception)
            {
                error = "Outcome provider metadata failed: " + exception.Message;
                return false;
            }
            if (outcomeFormatVersion <= 0)
            {
                error = "The outcome provider format version must be positive.";
                return false;
            }

            var dto = new LeaseRequestDto
            {
                contractVersion = 1,
                runner = new RunnerDto
                {
                    installationId = installationId,
                    name = name,
                    unityRelease = environment.UnityRelease,
                    inputSystemVersion = environment.InputSystemVersion,
                    runtimePlatform = environment.RuntimePlatform,
                    activeScenePath = environment.ActiveScenePath,
                    stateProviderId = registered.ProviderId,
                    stateFormatVersion = registered.StateFormatVersion,
                    outcomeFormatVersion = outcomeFormatVersion,
                    stageAdapters = new[]
                    {
                        SavedTestReplayCompatibility.RestoreStateAdapter,
                        SavedTestReplayCompatibility.InputTraceAdapter,
                        SavedTestReplayCompatibility.OutcomeObservationAdapter,
                    },
                },
            };
            json = JsonUtility.ToJson(dto);
            return true;
        }

        internal static bool TryReadLease(
            string json,
            out TestRunnerLease lease,
            out string error)
        {
            lease = null;
            error = null;
            LeaseResponseDto response;
            try
            {
                response = JsonUtility.FromJson<LeaseResponseDto>(json);
            }
            catch (Exception exception)
            {
                error = "Qamel returned invalid runner JSON: " + exception.Message;
                return false;
            }
            if (response == null || response.contractVersion != 1 ||
                !Guid.TryParse(response.runnerId, out _))
            {
                error = "Qamel returned an incomplete runner response.";
                return false;
            }
            if (response.run == null ||
                (string.IsNullOrWhiteSpace(response.run.testRunId) &&
                 string.IsNullOrWhiteSpace(response.run.testVersionId) &&
                 string.IsNullOrWhiteSpace(response.run.leaseExpiresAt)))
            {
                lease = new TestRunnerLease(response.runnerId, null, null, null);
                return true;
            }
            if (!Guid.TryParse(response.run.testRunId, out _) ||
                !Guid.TryParse(response.run.testVersionId, out _) ||
                !DateTimeOffset.TryParse(
                    response.run.leaseExpiresAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var expiresAt))
            {
                error = "Qamel returned an incomplete leased Run.";
                return false;
            }
            lease = new TestRunnerLease(
                response.runnerId,
                response.run.testRunId,
                response.run.testVersionId,
                expiresAt);
            return true;
        }

        void Fail(string error)
        {
            DisposeRequest();
            State = TestRunnerLeaseState.Failed;
            Status = error;
        }

        void DisposeRequest()
        {
            _request?.Dispose();
            _request = null;
        }

        internal static UnityWebRequest AuthorizedJsonPost(
            string url,
            string apiKey,
            string json)
        {
            var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
                downloadHandler = new DownloadHandlerBuffer(),
                redirectLimit = 0,
            };
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader(
                IngestHeaders.Authorization,
                IngestHeaders.Bearer(apiKey));
            request.SetRequestHeader(IngestHeaders.Plugin, IngestHeaders.PluginValue());
            return request;
        }

        [Serializable]
        sealed class LeaseRequestDto
        {
            public int contractVersion;
            public RunnerDto runner;
        }

        [Serializable]
        sealed class HumanReviewLeaseRequestDto
        {
            public int contractVersion;
            public HumanReviewRunnerDto runner;
        }

        [Serializable]
        sealed class RunnerDto
        {
            public string installationId;
            public string name;
            public string unityRelease;
            public string inputSystemVersion;
            public string runtimePlatform;
            public string activeScenePath;
            public string stateProviderId;
            public int stateFormatVersion;
            public int outcomeFormatVersion;
            public string[] stageAdapters;
        }

        [Serializable]
        sealed class HumanReviewRunnerDto
        {
            public string installationId;
            public string name;
            public string unityRelease;
            public string inputSystemVersion;
            public string runtimePlatform;
            public string activeScenePath;
            public string stateProviderId;
            public int stateFormatVersion;
            public string[] stageAdapters;
        }

        [Serializable]
        sealed class LeaseResponseDto
        {
            public int contractVersion;
            public string runnerId;
            public LeasedRunDto run;
        }

        [Serializable]
        sealed class LeasedRunDto
        {
            public string testRunId;
            public string testVersionId;
            public string leaseExpiresAt;
        }
    }

    internal enum TestRunResultUploadState
    {
        Uploading,
        Succeeded,
        Failed,
        Cancelled,
    }

    internal enum TestRunHeartbeatState
    {
        Sending,
        Succeeded,
        Failed,
        Cancelled,
    }

    internal sealed class TestRunHeartbeatOperation : IDisposable
    {
        const int RequestTimeoutSeconds = 15;

        readonly string _testRunId;
        UnityWebRequest _request;
        bool _disposed;

        public TestRunHeartbeatOperation(
            string endpoint,
            string apiKey,
            string testRunId,
            string runnerId,
            bool startImmediately = true)
        {
            if (!TestDefinitionRoutes.TryGetTestRunHeartbeatUrl(
                    endpoint,
                    testRunId,
                    out var heartbeatUrl))
                throw new ArgumentException(
                    "A valid Qamel endpoint and Test Run ID are required.",
                    nameof(testRunId));
            string key = (apiKey ?? "").Trim();
            if (!TestDefinitionRoutes.IsValidProjectApiKey(key))
                throw new ArgumentException(
                    "A complete project API key beginning with qa_key_ is required.",
                    nameof(apiKey));
            if (!Guid.TryParse(runnerId, out _))
                throw new ArgumentException(
                    "A valid Unity runner ID is required.",
                    nameof(runnerId));

            _testRunId = testRunId;
            State = TestRunHeartbeatState.Sending;
            Status = "Keeping the active Run connected to Qamel.";
            if (!startImmediately) return;

            string requestJson = JsonUtility.ToJson(new HeartbeatRequestDto
            {
                contractVersion = 1,
                runnerId = runnerId,
            });
            _request = TestRunnerLeaseOperation.AuthorizedJsonPost(
                heartbeatUrl,
                key,
                requestJson);
            _request.timeout = RequestTimeoutSeconds;
            _request.SendWebRequest();
        }

        public TestRunHeartbeatState State { get; private set; }
        public string Status { get; private set; }
        public string RunStatus { get; private set; }
        public DateTimeOffset? LeaseExpiresAt { get; private set; }
        public bool IsFinished => State == TestRunHeartbeatState.Succeeded ||
                                  State == TestRunHeartbeatState.Failed ||
                                  State == TestRunHeartbeatState.Cancelled;

        public void Tick()
        {
            if (_disposed || IsFinished || _request == null || !_request.isDone) return;
            if (_request.result != UnityWebRequest.Result.Success)
            {
                Fail(SavedTestCatalogOperation.RequestFailure(_request, "Run heartbeat"));
                return;
            }
            if (!TryReadHeartbeat(
                    _request.downloadHandler?.text,
                    _testRunId,
                    out var status,
                    out var leaseExpiresAt,
                    out var error))
            {
                Fail(error);
                return;
            }

            RunStatus = status;
            LeaseExpiresAt = leaseExpiresAt;
            DisposeRequest();
            State = TestRunHeartbeatState.Succeeded;
            Status = status == "running"
                ? "The active Run is connected to Qamel."
                : "Qamel reports the active Run as " + status.Replace('_', ' ') + ".";
        }

        public void Cancel()
        {
            if (IsFinished) return;
            _request?.Abort();
            DisposeRequest();
            State = TestRunHeartbeatState.Cancelled;
            Status = "Run heartbeat cancelled.";
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (!IsFinished) Cancel();
            DisposeRequest();
            _disposed = true;
        }

        internal static bool TryReadHeartbeat(
            string json,
            string expectedTestRunId,
            out string status,
            out DateTimeOffset? leaseExpiresAt,
            out string error)
        {
            status = null;
            leaseExpiresAt = null;
            error = null;
            HeartbeatResponseDto response;
            try
            {
                response = JsonUtility.FromJson<HeartbeatResponseDto>(json);
            }
            catch (Exception exception)
            {
                error = "Qamel returned invalid Run heartbeat JSON: " +
                        exception.Message;
                return false;
            }

            if (response == null || response.contractVersion != 1 ||
                !Guid.TryParse(response.testRunId, out _) ||
                !string.Equals(
                    response.testRunId,
                    expectedTestRunId,
                    StringComparison.OrdinalIgnoreCase) ||
                !IsRunStatus(response.status))
            {
                error = "Qamel returned an incomplete Run heartbeat response.";
                return false;
            }

            if (response.status == "running")
            {
                if (!DateTimeOffset.TryParse(
                        response.leaseExpiresAt,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var parsedExpiry))
                {
                    error = "Qamel returned an incomplete active Run heartbeat.";
                    return false;
                }
                leaseExpiresAt = parsedExpiry;
            }
            else if (!string.IsNullOrWhiteSpace(response.leaseExpiresAt))
            {
                error = "Qamel returned a lease for a completed Run.";
                return false;
            }

            status = response.status;
            return true;
        }

        static bool IsRunStatus(string status)
        {
            return status == "running" || status == "passed" ||
                   status == "failed" || status == "needs_review" ||
                   status == "error" || status == "cancelled";
        }

        void Fail(string error)
        {
            DisposeRequest();
            State = TestRunHeartbeatState.Failed;
            Status = error;
        }

        void DisposeRequest()
        {
            _request?.Dispose();
            _request = null;
        }

        [Serializable]
        sealed class HeartbeatRequestDto
        {
            public int contractVersion;
            public string runnerId;
        }

        [Serializable]
        sealed class HeartbeatResponseDto
        {
            public int contractVersion;
            public string testRunId;
            public string status;
            public string leaseExpiresAt;
        }
    }

    internal sealed class TestRunResultUploadOperation : IDisposable
    {
        const int RequestTimeoutSeconds = 30;

        readonly string _testRunId;
        UnityWebRequest _request;
        bool _disposed;

        public TestRunResultUploadOperation(
            string endpoint,
            string apiKey,
            string testRunId,
            string resultJson,
            bool startImmediately = true)
        {
            if (!TestDefinitionRoutes.TryGetTestRunResultUrl(
                    endpoint,
                    testRunId,
                    out var resultUrl))
                throw new ArgumentException(
                    "A valid Qamel endpoint and Test Run ID are required.",
                    nameof(testRunId));
            string key = (apiKey ?? "").Trim();
            if (!TestDefinitionRoutes.IsValidProjectApiKey(key))
                throw new ArgumentException(
                    "A complete project API key beginning with qa_key_ is required.",
                    nameof(apiKey));
            if (string.IsNullOrWhiteSpace(resultJson))
                throw new ArgumentException("A complete Test Run result is required.",
                    nameof(resultJson));

            _testRunId = testRunId;
            State = TestRunResultUploadState.Uploading;
            Status = "Sending the final Run result to Qamel.";
            if (!startImmediately) return;

            _request = TestRunnerLeaseOperation.AuthorizedJsonPost(resultUrl, key, resultJson);
            _request.timeout = RequestTimeoutSeconds;
            _request.SendWebRequest();
        }

        public TestRunResultUploadState State { get; private set; }
        public string Status { get; private set; }
        public string RunStatus { get; private set; }
        public bool IsFinished => State == TestRunResultUploadState.Succeeded ||
                                  State == TestRunResultUploadState.Failed ||
                                  State == TestRunResultUploadState.Cancelled;

        public void Tick()
        {
            if (_disposed || IsFinished || _request == null || !_request.isDone) return;
            if (_request.result != UnityWebRequest.Result.Success)
            {
                Fail(SavedTestCatalogOperation.RequestFailure(_request, "Run result"));
                return;
            }
            if (!TryReadCompletion(
                    _request.downloadHandler?.text,
                    _testRunId,
                    out var status,
                    out var error))
            {
                Fail(error);
                return;
            }

            RunStatus = status;
            DisposeRequest();
            State = TestRunResultUploadState.Succeeded;
            Status = "Qamel recorded the Run as " + status.Replace('_', ' ') + ".";
        }

        public void Cancel()
        {
            if (IsFinished) return;
            _request?.Abort();
            DisposeRequest();
            State = TestRunResultUploadState.Cancelled;
            Status = "Run result upload cancelled.";
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (!IsFinished) Cancel();
            DisposeRequest();
            _disposed = true;
        }

        internal static bool TryReadCompletion(
            string json,
            string expectedTestRunId,
            out string status,
            out string error)
        {
            status = null;
            error = null;
            CompletionResponseDto response;
            try
            {
                response = JsonUtility.FromJson<CompletionResponseDto>(json);
            }
            catch (Exception exception)
            {
                error = "Qamel returned invalid Run result JSON: " + exception.Message;
                return false;
            }
            if (response == null || response.contractVersion != 1 ||
                !Guid.TryParse(response.testRunId, out _) ||
                !string.Equals(
                    response.testRunId,
                    expectedTestRunId,
                    StringComparison.OrdinalIgnoreCase) ||
                !IsTerminalStatus(response.status))
            {
                error = "Qamel returned an incomplete Run result response.";
                return false;
            }
            status = response.status;
            return true;
        }

        static bool IsTerminalStatus(string status)
        {
            return status == "passed" || status == "failed" ||
                   status == "needs_review" || status == "error" ||
                   status == "cancelled";
        }

        void Fail(string error)
        {
            DisposeRequest();
            State = TestRunResultUploadState.Failed;
            Status = error;
        }

        void DisposeRequest()
        {
            _request?.Dispose();
            _request = null;
        }

        [Serializable]
        sealed class CompletionResponseDto
        {
            public int contractVersion;
            public string testRunId;
            public string status;
        }
    }
}
