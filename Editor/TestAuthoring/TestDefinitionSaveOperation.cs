using System;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace QamelCapture.Editor.TestAuthoring
{
    internal enum TestDefinitionSaveState
    {
        Registering,
        Uploading,
        Confirming,
        SavingEvidence,
        Succeeded,
        Failed,
        Cancelled,
    }

    internal sealed class TestDefinitionSaveOperation : IDisposable
    {
        const int RequestTimeoutSeconds = 120;

        readonly RecordedTestDefinitionUpload _upload;
        readonly string _draftId;
        readonly string _endpoint;
        readonly string _apiKey;
        UnityWebRequest _request;
        TestEvidenceUploadOperation _evidenceOperation;
        RegistrationResponse _registration;
        int _artifactIndex;
        int _uploadedArtifactCount;
        bool _disposed;

        public TestDefinitionSaveOperation(
            string draftId,
            RecordedTestDefinitionUpload upload,
            string endpoint,
            string apiKey)
        {
            if (string.IsNullOrWhiteSpace(draftId))
                throw new ArgumentException("A draft ID is required.", nameof(draftId));
            _upload = upload ?? throw new ArgumentNullException(nameof(upload));
            _draftId = draftId;
            _apiKey = (apiKey ?? "").Trim();

            if (!TestDefinitionRoutes.IsValidBase(endpoint))
                throw new ArgumentException(
                    "Use an HTTPS Qamel endpoint or HTTP localhost for development.",
                    nameof(endpoint));
            _endpoint = TestDefinitionRoutes.Normalize(endpoint);
            if (!TestDefinitionRoutes.IsValidProjectApiKey(_apiKey))
                throw new ArgumentException(
                    "A complete project API key beginning with qa_key_ is required.",
                    nameof(apiKey));

            State = TestDefinitionSaveState.Registering;
            Status = "Registering the Test definition and its declared SHA-256 checksums.";
            StartRegistration(TestDefinitionRoutes.RegistrationUrl(_endpoint));
        }

        public string DraftId => _draftId;
        public TestDefinitionSaveState State { get; private set; }
        public string Status { get; private set; }
        public string TestId { get; private set; }
        public string TestVersionId { get; private set; }
        public string TestName => _upload.TestName;
        public int VersionNumber { get; private set; }
        public bool Created { get; private set; }
        public string TestUrl { get; private set; }
        public int StateByteCount => _upload.StateByteCount;
        public int InputTraceByteCount => _upload.InputTraceByteCount;
        public string StateSha256 => _upload.StateSha256;
        public string InputTraceSha256 => _upload.InputTraceSha256;
        public bool IsFinished => State == TestDefinitionSaveState.Succeeded ||
                                  State == TestDefinitionSaveState.Failed ||
                                  State == TestDefinitionSaveState.Cancelled;

        public void Tick()
        {
            if (_disposed || IsFinished) return;

            if (State == TestDefinitionSaveState.SavingEvidence)
            {
                _evidenceOperation?.Tick();
                if (_evidenceOperation == null || !_evidenceOperation.IsFinished) return;
                if (_evidenceOperation.State != TestEvidenceUploadState.Succeeded)
                {
                    Fail(_evidenceOperation.Status);
                    return;
                }
                _evidenceOperation.Dispose();
                _evidenceOperation = null;
                Succeed();
                return;
            }

            if (_request == null || !_request.isDone)
                return;

            if (_request.result != UnityWebRequest.Result.Success)
            {
                string failure = RequestFailure(_request);
                if (State == TestDefinitionSaveState.Uploading)
                {
                    var artifact = _registration.artifacts[_artifactIndex];
                    failure = BuildUploadFailureStatus(
                        artifact.clientRef,
                        _artifactIndex + 1,
                        _registration.artifacts.Length,
                        failure);
                }
                else if (State == TestDefinitionSaveState.Confirming)
                {
                    failure = "Final artifact confirmation failed. " + failure;
                }
                Fail(failure);
                return;
            }

            if (State == TestDefinitionSaveState.Registering ||
                State == TestDefinitionSaveState.Confirming)
            {
                if (!TryReadRegistration(
                        _request.downloadHandler.text,
                        out var registration,
                        out var error,
                        _upload.ContractVersion))
                {
                    Fail(error);
                    return;
                }

                if (State == TestDefinitionSaveState.Confirming)
                {
                    if (!SameRegistration(_registration, registration))
                    {
                        Fail("Qamel returned a different Test during final artifact confirmation.");
                        return;
                    }
                    if (!TryConfirmUploaded(registration, out error))
                    {
                        Fail(error);
                        return;
                    }

                    DisposeRequest();
                    StartEvidenceSave();
                    return;
                }

                _registration = registration;
                TestId = _registration.testId;
                TestVersionId = _registration.testVersionId;
                VersionNumber = _registration.versionNumber;
                Created = _registration.created;
                if (!TestDefinitionRoutes.TryGetTestUrl(
                        _endpoint,
                        TestId,
                        out var testUrl))
                {
                    Fail("Qamel returned an invalid Test ID.");
                    return;
                }
                TestUrl = testUrl;
                _artifactIndex = 0;
                DisposeRequest();
                StartNextUpload();
                return;
            }

            if (State == TestDefinitionSaveState.Uploading)
            {
                _uploadedArtifactCount += 1;
                _artifactIndex += 1;
                DisposeRequest();
                StartNextUpload();
            }
        }

        public void Cancel()
        {
            if (IsFinished) return;
            _request?.Abort();
            _evidenceOperation?.Cancel();
            DisposeRequest();
            State = TestDefinitionSaveState.Cancelled;
            Status = "Save cancelled. The same draft can be retried.";
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (!IsFinished) Cancel();
            DisposeRequest();
            _evidenceOperation?.Dispose();
            _evidenceOperation = null;
            _disposed = true;
        }

        void StartRegistration(string url)
        {
            _request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(_upload.Json))
                {
                    contentType = "application/json",
                },
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = RequestTimeoutSeconds,
                redirectLimit = 0,
            };
            _request.SetRequestHeader(IngestHeaders.Authorization, IngestHeaders.Bearer(_apiKey));
            _request.SetRequestHeader(IngestHeaders.Plugin, IngestHeaders.PluginValue());
            _request.SendWebRequest();
        }

        void StartNextUpload()
        {
            while (_artifactIndex < _registration.artifacts.Length &&
                   _registration.artifacts[_artifactIndex].uploadStatus == "uploaded")
            {
                _artifactIndex += 1;
            }

            if (_artifactIndex >= _registration.artifacts.Length)
            {
                if (_uploadedArtifactCount > 0)
                    StartConfirmation();
                else
                    StartEvidenceSave();
                return;
            }

            var artifact = _registration.artifacts[_artifactIndex];
            byte[] bytes = BytesFor(artifact.clientRef);
            if (bytes == null)
            {
                Fail("The server returned an unknown artifact reference.");
                return;
            }
            if (string.IsNullOrWhiteSpace(artifact.uploadUrl))
            {
                Fail("The server did not provide an artifact upload URL.");
                return;
            }

            State = TestDefinitionSaveState.Uploading;
            Status = BuildUploadStatus(
                artifact.clientRef,
                VersionNumber,
                _artifactIndex + 1,
                _registration.artifacts.Length);
            _request = new UnityWebRequest(
                artifact.uploadUrl,
                UnityWebRequest.kHttpVerbPUT)
            {
                uploadHandler = new UploadHandlerRaw(bytes)
                {
                    contentType = "application/octet-stream",
                },
                downloadHandler = new DownloadHandlerBuffer(),
            };
            _request.SetRequestHeader("Content-Type", "application/octet-stream");
            _request.SetRequestHeader("x-upsert", "false");
            _request.timeout = RequestTimeoutSeconds;
            _request.SendWebRequest();
        }

        void StartConfirmation()
        {
            State = TestDefinitionSaveState.Confirming;
            Status = "Confirming both artifact upload statuses for Test version " +
                     VersionNumber + ".";
            StartRegistration(TestDefinitionRoutes.RegistrationUrl(_endpoint));
        }

        void StartEvidenceSave()
        {
            if (!TestDefinitionRoutes.TryGetTestVersionEvidenceUrl(
                    _endpoint,
                    TestVersionId,
                    out _))
            {
                Fail("Qamel returned an invalid Test Version for visual evidence.");
                return;
            }

            DisposeRequest();
            State = TestDefinitionSaveState.SavingEvidence;
            Status = "Saving the bounded reference gameplay video.";
            try
            {
                _evidenceOperation = new TestEvidenceUploadOperation(
                    _endpoint,
                    _apiKey,
                    TestVersionId,
                    runEvidence: false,
                    _upload.ReferenceEvidenceClip);
            }
            catch (Exception exception)
            {
                Fail("Could not start the reference evidence upload: " + exception.Message);
            }
        }

        void Succeed()
        {
            State = TestDefinitionSaveState.Succeeded;
            Status = BuildCompletionStatus(
                VersionNumber,
                Created,
                _uploadedArtifactCount > 0);
        }

        byte[] BytesFor(string clientRef)
        {
            if (clientRef == RecordedTestDefinitionSerializer.StateArtifactRef)
                return _upload.StatePayload;
            if (clientRef == RecordedTestDefinitionSerializer.TraceArtifactRef)
                return _upload.InputTrace;
            return null;
        }

        internal static string BuildUploadStatus(
            string clientRef,
            int versionNumber,
            int artifactNumber,
            int artifactCount)
        {
            return "Uploading " + ArtifactLabel(clientRef) +
                   " for Test version " + versionNumber + " (artifact " +
                   artifactNumber + " of " + artifactCount +
                   "). Its SHA-256 was calculated from the exact in-memory bytes " +
                   "being sent; Qamel confirms upload presence and byte size.";
        }

        internal static string BuildUploadFailureStatus(
            string clientRef,
            int artifactNumber,
            int artifactCount,
            string failure)
        {
            return "Uploading " + ArtifactLabel(clientRef) + " failed (artifact " +
                   artifactNumber + " of " + artifactCount + "). " + failure;
        }

        internal static string BuildCompletionStatus(
            int versionNumber,
            bool created,
            bool uploadedThisAttempt)
        {
            string registration = "Saved Test version " + versionNumber + " (" +
                (created ? "new Test" : "existing Test") + "). ";
            return uploadedThisAttempt
                ? registration +
                  "Qamel confirmed both artifacts uploaded after checking object " +
                  "presence and byte size; their " +
                  "declared SHA-256 values remain available for download verification. " +
                  "The reference gameplay clip was uploaded for video processing."
                : registration +
                  "Qamel already reports both artifacts uploaded, with their declared " +
                  "SHA-256 values available for download verification. " +
                  "The reference gameplay clip is saved.";
        }

        internal static bool TryConfirmUploaded(
            RegistrationResponse response,
            out string error)
        {
            error = null;
            if (response?.artifacts == null || response.artifacts.Length != 2)
            {
                error = "Qamel returned incomplete final artifact confirmation.";
                return false;
            }

            var outstanding = new StringBuilder();
            for (var index = 0; index < response.artifacts.Length; index++)
            {
                var artifact = response.artifacts[index];
                if (artifact != null && artifact.uploadStatus == "uploaded")
                    continue;

                if (outstanding.Length > 0) outstanding.Append("; ");
                outstanding.Append(ArtifactLabel(artifact?.clientRef));
                outstanding.Append(" (artifact ");
                outstanding.Append(index + 1);
                outstanding.Append(" of ");
                outstanding.Append(response.artifacts.Length);
                outstanding.Append(") is ");
                outstanding.Append(artifact?.uploadStatus ?? "missing");
            }

            if (outstanding.Length == 0) return true;
            error = "Qamel has not confirmed all artifact uploads: " +
                    outstanding + ".";
            return false;
        }

        static bool SameRegistration(
            RegistrationResponse expected,
            RegistrationResponse actual)
        {
            return expected != null && actual != null &&
                   string.Equals(expected.testId, actual.testId, StringComparison.Ordinal) &&
                   string.Equals(
                       expected.testVersionId,
                       actual.testVersionId,
                       StringComparison.Ordinal) &&
                   expected.versionNumber == actual.versionNumber;
        }

        static string ArtifactLabel(string clientRef)
        {
            if (clientRef == RecordedTestDefinitionSerializer.StateArtifactRef)
                return "starting state";
            if (clientRef == RecordedTestDefinitionSerializer.TraceArtifactRef)
                return "input trace";
            return "artifact";
        }

        void Fail(string message)
        {
            _evidenceOperation?.Dispose();
            _evidenceOperation = null;
            DisposeRequest();
            State = TestDefinitionSaveState.Failed;
            Status = message + " The same draft can be retried.";
        }

        static string RequestFailure(UnityWebRequest request)
        {
            string serverError = QamelJson.ExtractString(
                request.downloadHandler?.text,
                "error");
            if (!string.IsNullOrWhiteSpace(serverError))
                return "Qamel rejected the save (" + request.responseCode + "): " +
                       serverError + ".";
            return "Save failed (" + request.responseCode + " " + request.error + ").";
        }

        void DisposeRequest()
        {
            _request?.Dispose();
            _request = null;
        }

        internal static bool TryReadRegistration(
            string json,
            out RegistrationResponse response,
            out string error,
            int expectedContractVersion = RecordedTestDefinitionSerializer.ContractVersion)
        {
            response = null;
            error = null;
            try
            {
                response = JsonUtility.FromJson<RegistrationResponse>(json);
            }
            catch (Exception exception)
            {
                error = "Qamel returned invalid registration JSON: " + exception.Message;
                return false;
            }

            if (response == null || response.contractVersion != expectedContractVersion ||
                !Guid.TryParse(response.testId, out _) ||
                !Guid.TryParse(response.testVersionId, out _) ||
                response.versionNumber < 1 ||
                response.artifacts == null || response.artifacts.Length != 2)
            {
                error = "Qamel returned an incomplete registration response.";
                return false;
            }

            bool hasState = false;
            bool hasTrace = false;
            foreach (var artifact in response.artifacts)
            {
                if (artifact == null || string.IsNullOrWhiteSpace(artifact.artifactId) ||
                    string.IsNullOrWhiteSpace(artifact.objectPath) ||
                    (artifact.uploadStatus != "pending" &&
                     artifact.uploadStatus != "size_mismatch" &&
                     artifact.uploadStatus != "uploaded"))
                {
                    error = "Qamel returned invalid artifact registration data.";
                    return false;
                }

                if (artifact.clientRef == RecordedTestDefinitionSerializer.StateArtifactRef)
                    hasState = true;
                else if (artifact.clientRef == RecordedTestDefinitionSerializer.TraceArtifactRef)
                    hasTrace = true;
                else
                {
                    error = "Qamel returned an unknown artifact reference.";
                    return false;
                }

                if (artifact.uploadStatus != "uploaded" &&
                    !TestDefinitionRoutes.IsValidArtifactUploadUrl(artifact.uploadUrl))
                {
                    error = "Qamel returned an invalid artifact upload URL.";
                    return false;
                }
            }

            if (!hasState || !hasTrace)
            {
                error = "Qamel did not register both definition artifacts.";
                return false;
            }
            return true;
        }

        [Serializable]
        internal sealed class RegistrationResponse
        {
            public int contractVersion;
            public bool created;
            public string testId;
            public string testVersionId;
            public int versionNumber;
            public RegistrationArtifact[] artifacts;
        }

        [Serializable]
        internal sealed class RegistrationArtifact
        {
            public string clientRef;
            public string artifactId;
            public string objectPath;
            public string uploadStatus;
            public string uploadUrl;
        }
    }
}
