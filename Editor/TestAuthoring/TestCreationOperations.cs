using System;
using UnityEngine;
using UnityEngine.Networking;

namespace QamelCapture.Editor.TestAuthoring
{
    internal sealed class ConnectedTestCreation
    {
        public string Id { get; set; }
        public string Status { get; set; }
        public string Name { get; set; }
        public string ExpectedOutcome { get; set; }
        public string ActiveScenePath { get; set; }
        public string LocalDraftId { get; set; }
        public double RecordedDurationSeconds { get; set; }
        public int ReplayableInputEventCount { get; set; }
        public string[] DeviceLayouts { get; set; }
        public string SavedTestId { get; set; }
        public string SavedTestVersionId { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }

        public bool IsTerminal => Status == "saved" || Status == "failed" ||
                                  Status == "cancelled" || Status == "expired";
    }

    internal static class TestCreationPollingPolicy
    {
        internal const double ActiveIntervalSeconds = 2;
        internal const double IdleIntervalSeconds = 5;
        internal const double RetryIntervalSeconds = 5;

        internal static double DelayAfterPoll(ConnectedTestCreation session) =>
            session == null || session.IsTerminal
                ? IdleIntervalSeconds
                : ActiveIntervalSeconds;
    }

    internal static class TestCreationProtocol
    {
        internal static string PollRequest(string installationId, string sessionId)
        {
            if (!Guid.TryParse(installationId, out var installation) || installation == Guid.Empty)
                throw new ArgumentException("A valid installation ID is required.", nameof(installationId));
            if (!string.IsNullOrWhiteSpace(sessionId) && !Guid.TryParse(sessionId, out _))
                throw new ArgumentException("A valid Test creation session ID is required.", nameof(sessionId));
            return "{\"contractVersion\":1,\"installationId\":\"" +
                   installation.ToString("D") + "\",\"sessionId\":" +
                   (string.IsNullOrWhiteSpace(sessionId)
                       ? "null"
                       : "\"" + Escape(sessionId) + "\"") + "}";
        }

        internal static string PreparingRequest(string installationId, string sessionId, string scenePath) =>
            Advance(installationId, sessionId, "requested", "preparing",
                new PreparingPayload { activeScenePath = Required(scenePath, 1024, "scene path") });

        internal static string RecordingRequest(string installationId, string sessionId) =>
            Advance(installationId, sessionId, "preparing", "recording", new EmptyPayload());

        internal static string DraftReadyRequest(
            string installationId,
            string sessionId,
            string draftId,
            double durationSeconds,
            long inputEventCount,
            string[] deviceLayouts)
        {
            if (!Guid.TryParseExact(draftId, "N", out _))
                throw new ArgumentException("A valid local draft ID is required.", nameof(draftId));
            if (durationSeconds < 0 || double.IsNaN(durationSeconds) || double.IsInfinity(durationSeconds))
                throw new ArgumentOutOfRangeException(nameof(durationSeconds));
            if (inputEventCount < 0 || inputEventCount > 1000000)
                throw new ArgumentOutOfRangeException(nameof(inputEventCount));
            deviceLayouts = deviceLayouts ?? Array.Empty<string>();
            if (deviceLayouts.Length > 16)
                throw new ArgumentException("Too many input device layouts.", nameof(deviceLayouts));
            for (var index = 0; index < deviceLayouts.Length; index++)
                Required(deviceLayouts[index], 120, "device layout");
            return Advance(installationId, sessionId, "stop_requested", "draft_ready",
                new DraftPayload
                {
                    localDraftId = draftId,
                    recordedDurationSeconds = durationSeconds,
                    replayableInputEventCount = (int)inputEventCount,
                    deviceLayouts = deviceLayouts,
                });
        }

        internal static string SavingRequest(string installationId, string sessionId) =>
            Advance(installationId, sessionId, "save_requested", "saving", new EmptyPayload());

        internal static string SavedRequest(
            string installationId,
            string sessionId,
            string testId,
            string testVersionId)
        {
            RequireGuid(testId, "Test ID");
            RequireGuid(testVersionId, "Test Version ID");
            return Advance(installationId, sessionId, "saving", "saved", new SavedPayload
            {
                testId = testId,
                testVersionId = testVersionId,
            });
        }

        internal static string FailedRequest(
            string installationId,
            string sessionId,
            string expectedStatus,
            string errorCode,
            string errorMessage)
        {
            if (!IsActiveStatus(expectedStatus))
                throw new ArgumentException("A failure must replace an active status.", nameof(expectedStatus));
            return Advance(installationId, sessionId, expectedStatus, "failed", new FailurePayload
            {
                errorCode = Required(errorCode, 120, "error code"),
                errorMessage = Required(errorMessage, 4000, "error message"),
            });
        }

        internal static bool TryReadPoll(
            string json,
            out ConnectedTestCreation session,
            out string error)
        {
            session = null;
            error = null;
            PollResponseDto response;
            try { response = JsonUtility.FromJson<PollResponseDto>(json); }
            catch (Exception exception)
            {
                error = "Qamel returned invalid Test creation JSON: " + exception.Message;
                return false;
            }
            if (response == null || response.contractVersion != 1)
            {
                error = "Qamel returned an incomplete Test creation response.";
                return false;
            }
            if (!response.hasSession) return true;
            if (response.session == null)
            {
                error = "Qamel returned an incomplete Test creation response.";
                return false;
            }
            var dto = response.session;
            if (!Guid.TryParse(dto.id, out _) || !IsKnownStatus(dto.status))
            {
                error = "Qamel returned an invalid Test creation session.";
                return false;
            }
            if (NeedsScene(dto.status) && string.IsNullOrWhiteSpace(dto.activeScenePath))
            {
                error = "Qamel returned a Test creation session without its scene.";
                return false;
            }
            if (NeedsDraft(dto.status) &&
                (!Guid.TryParseExact(dto.localDraftId, "N", out _) ||
                 dto.recordedDurationSeconds < 0 || dto.replayableInputEventCount < 0 ||
                 dto.deviceLayouts == null || dto.deviceLayouts.Length > 16))
            {
                error = "Qamel returned an incomplete recorded draft summary.";
                return false;
            }
            if (NeedsDetails(dto.status) &&
                (string.IsNullOrWhiteSpace(dto.name) || string.IsNullOrWhiteSpace(dto.expectedOutcome)))
            {
                error = "Qamel returned a save request without reviewed Test details.";
                return false;
            }
            if (dto.status == "saved" &&
                (!Guid.TryParse(dto.savedTestId, out _) || !Guid.TryParse(dto.savedTestVersionId, out _)))
            {
                error = "Qamel returned an incomplete saved Test receipt.";
                return false;
            }
            if (dto.status == "failed" &&
                (string.IsNullOrWhiteSpace(dto.errorCode) || string.IsNullOrWhiteSpace(dto.errorMessage)))
            {
                error = "Qamel returned an incomplete Test creation failure.";
                return false;
            }
            session = new ConnectedTestCreation
            {
                Id = dto.id,
                Status = dto.status,
                Name = dto.name,
                ExpectedOutcome = dto.expectedOutcome,
                ActiveScenePath = dto.activeScenePath,
                LocalDraftId = dto.localDraftId,
                RecordedDurationSeconds = dto.recordedDurationSeconds,
                ReplayableInputEventCount = dto.replayableInputEventCount,
                DeviceLayouts = dto.deviceLayouts ?? Array.Empty<string>(),
                SavedTestId = dto.savedTestId,
                SavedTestVersionId = dto.savedTestVersionId,
                ErrorCode = dto.errorCode,
                ErrorMessage = dto.errorMessage,
            };
            return true;
        }

        internal static bool TryReadAdvance(
            string json,
            string expectedSessionId,
            string expectedStatus,
            out string error)
        {
            error = null;
            AdvanceResponseDto response;
            try { response = JsonUtility.FromJson<AdvanceResponseDto>(json); }
            catch (Exception exception)
            {
                error = "Qamel returned invalid Test creation JSON: " + exception.Message;
                return false;
            }
            if (response == null || response.contractVersion != 1 ||
                !string.Equals(response.sessionId, expectedSessionId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(response.status, expectedStatus, StringComparison.Ordinal))
            {
                error = "Qamel returned a different Test creation update.";
                return false;
            }
            return true;
        }

        static string Advance(
            string installationId,
            string sessionId,
            string expectedStatus,
            string nextStatus,
            object payload)
        {
            RequireGuid(installationId, "installation ID");
            RequireGuid(sessionId, "Test creation session ID");
            var prefix = "{\"contractVersion\":1,\"installationId\":\"" +
                         Escape(installationId) + "\",\"sessionId\":\"" + Escape(sessionId) +
                         "\",\"expectedStatus\":\"" + expectedStatus +
                         "\",\"nextStatus\":\"" + nextStatus + "\",\"payload\":";
            return prefix + JsonUtility.ToJson(payload) + "}";
        }

        static string Required(string value, int maxLength, string label)
        {
            value = (value ?? "").Trim();
            if (value.Length == 0 || value.Length > maxLength)
                throw new ArgumentException("A valid " + label + " is required.");
            return value;
        }

        static void RequireGuid(string value, string label)
        {
            if (!Guid.TryParse(value, out var parsed) || parsed == Guid.Empty)
                throw new ArgumentException("A valid " + label + " is required.");
        }

        static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        static bool IsActiveStatus(string value) => value == "requested" || value == "preparing" ||
            value == "recording" || value == "stop_requested" || value == "draft_ready" ||
            value == "save_requested" || value == "saving";
        static bool IsKnownStatus(string value) => IsActiveStatus(value) || value == "saved" ||
            value == "failed" || value == "cancelled" || value == "expired";
        static bool NeedsScene(string value) => value != "requested" && value != "failed" &&
            value != "cancelled" && value != "expired";
        static bool NeedsDraft(string value) => value == "draft_ready" || value == "save_requested" ||
            value == "saving" || value == "saved";
        static bool NeedsDetails(string value) => value == "save_requested" || value == "saving" ||
            value == "saved";

        [Serializable] sealed class PollResponseDto
        { public int contractVersion; public bool hasSession; public SessionDto session; }
        [Serializable] sealed class SessionDto
        {
            public string id, status, name, expectedOutcome, activeScenePath, localDraftId;
            public double recordedDurationSeconds;
            public int replayableInputEventCount;
            public string[] deviceLayouts;
            public string savedTestId, savedTestVersionId, errorCode, errorMessage;
        }
        [Serializable] sealed class AdvanceResponseDto
        { public int contractVersion; public string sessionId, status; }
        [Serializable] sealed class EmptyPayload { }
        [Serializable] sealed class PreparingPayload { public string activeScenePath; }
        [Serializable] sealed class DraftPayload
        {
            public string localDraftId;
            public double recordedDurationSeconds;
            public int replayableInputEventCount;
            public string[] deviceLayouts;
        }
        [Serializable] sealed class SavedPayload { public string testId, testVersionId; }
        [Serializable] sealed class FailurePayload { public string errorCode, errorMessage; }
    }

    internal enum TestCreationRequestState { Sending, Succeeded, Failed, Cancelled }

    internal sealed class TestCreationRequestOperation : IDisposable
    {
        const int RequestTimeoutSeconds = 15;
        readonly bool _poll;
        readonly string _sessionId;
        readonly string _expectedStatus;
        UnityWebRequest _request;
        bool _disposed;

        public TestCreationRequestOperation(
            string endpoint,
            string apiKey,
            string requestJson,
            bool poll,
            string sessionId = null,
            string expectedStatus = null,
            bool startImmediately = true)
        {
            if (!TestDefinitionRoutes.IsValidBase(endpoint))
                throw new ArgumentException("A valid Qamel endpoint is required.", nameof(endpoint));
            apiKey = (apiKey ?? "").Trim();
            if (!TestDefinitionRoutes.IsValidProjectApiKey(apiKey))
                throw new ArgumentException("A complete project API key is required.", nameof(apiKey));
            if (string.IsNullOrWhiteSpace(requestJson))
                throw new ArgumentException("A Test creation request is required.", nameof(requestJson));
            _poll = poll;
            _sessionId = sessionId;
            _expectedStatus = expectedStatus;
            State = TestCreationRequestState.Sending;
            Status = poll ? "Checking for browser Test creation work." : "Updating browser Test creation progress.";
            if (!startImmediately) return;
            _request = TestRunnerLeaseOperation.AuthorizedJsonPost(
                poll ? TestDefinitionRoutes.TestCreationPollUrl(endpoint) :
                    TestDefinitionRoutes.TestCreationAdvanceUrl(endpoint),
                apiKey,
                requestJson);
            _request.timeout = RequestTimeoutSeconds;
            _request.SendWebRequest();
        }

        public TestCreationRequestState State { get; private set; }
        public string Status { get; private set; }
        public ConnectedTestCreation Session { get; private set; }
        public bool IsFinished => State != TestCreationRequestState.Sending;

        public void Tick()
        {
            if (_disposed || IsFinished || _request == null || !_request.isDone) return;
            if (_request.result != UnityWebRequest.Result.Success)
            {
                Fail(SavedTestCatalogOperation.RequestFailure(_request, "Test creation"));
                return;
            }
            string error;
            ConnectedTestCreation session = null;
            bool valid;
            if (_poll)
            {
                valid = TestCreationProtocol.TryReadPoll(
                    _request.downloadHandler?.text,
                    out session,
                    out error);
            }
            else
            {
                valid = TestCreationProtocol.TryReadAdvance(
                    _request.downloadHandler?.text,
                    _sessionId,
                    _expectedStatus,
                    out error);
            }
            if (!valid) { Fail(error); return; }
            if (_poll) Session = session;
            DisposeRequest();
            State = TestCreationRequestState.Succeeded;
            Status = "Test creation request completed.";
        }

        public void Cancel()
        {
            if (IsFinished) return;
            _request?.Abort();
            DisposeRequest();
            State = TestCreationRequestState.Cancelled;
            Status = "Test creation request cancelled.";
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (!IsFinished) Cancel();
            DisposeRequest();
            _disposed = true;
        }

        void Fail(string error)
        {
            DisposeRequest();
            State = TestCreationRequestState.Failed;
            Status = error;
        }

        void DisposeRequest() { _request?.Dispose(); _request = null; }
    }
}
