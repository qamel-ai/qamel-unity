using System;
using System.Security.Cryptography;
using System.Text;
using QamelCapture.TestAuthoring;
using UnityEngine;
using UnityEngine.Networking;

namespace QamelCapture.Editor.TestAuthoring
{
    internal enum TestEvidenceUploadState
    {
        Registering,
        Uploading,
        Succeeded,
        Failed,
        Cancelled,
    }

    internal sealed class TestEvidenceUploadOperation : IDisposable
    {
        const int RequestTimeoutSeconds = 120;

        readonly byte[] _bytes;
        readonly TestEvidenceClip _clip;
        readonly string _apiKey;
        readonly string _registrationUrl;
        UnityWebRequest _request;
        bool _disposed;

        public TestEvidenceUploadOperation(
            string endpoint,
            string apiKey,
            string ownerId,
            bool runEvidence,
            TestEvidenceClip clip)
        {
            _clip = clip ?? throw new ArgumentNullException(nameof(clip));
            _bytes = clip.GetBytesCopy();
            _apiKey = (apiKey ?? "").Trim();
            if (!TestDefinitionRoutes.IsValidProjectApiKey(_apiKey))
                throw new ArgumentException(
                    "A complete project API key beginning with qa_key_ is required.",
                    nameof(apiKey));

            bool valid = runEvidence
                ? TestDefinitionRoutes.TryGetTestRunEvidenceUrl(
                    endpoint, ownerId, out _registrationUrl)
                : TestDefinitionRoutes.TryGetTestVersionEvidenceUrl(
                    endpoint, ownerId, out _registrationUrl);
            if (!valid)
                throw new ArgumentException("The Test evidence owner is invalid.", nameof(ownerId));

            State = TestEvidenceUploadState.Registering;
            Status = "Preparing bounded Test video evidence upload.";
            StartRegistration();
        }

        public TestEvidenceUploadState State { get; private set; }
        public string Status { get; private set; }
        public bool IsFinished => State == TestEvidenceUploadState.Succeeded ||
                                  State == TestEvidenceUploadState.Failed ||
                                  State == TestEvidenceUploadState.Cancelled;

        public void Tick()
        {
            if (_disposed || IsFinished || _request == null || !_request.isDone) return;
            if (_request.result != UnityWebRequest.Result.Success)
            {
                Fail(RequestFailure(_request));
                return;
            }

            if (State == TestEvidenceUploadState.Registering)
            {
                if (!TryReadRegistration(_request.downloadHandler.text, out var response, out var error))
                {
                    Fail(error);
                    return;
                }
                DisposeRequest();
                if (string.IsNullOrWhiteSpace(response.uploadUrl))
                {
                    State = TestEvidenceUploadState.Succeeded;
                    Status = response.status == "processed"
                        ? "Test evidence video is already ready."
                        : "Test evidence frame bundle is already being processed.";
                    return;
                }

                State = TestEvidenceUploadState.Uploading;
                Status = "Uploading the bounded Test evidence frame bundle.";
                _request = new UnityWebRequest(response.uploadUrl, UnityWebRequest.kHttpVerbPUT)
                {
                    uploadHandler = new UploadHandlerRaw(_bytes)
                    {
                        contentType = "application/zip",
                    },
                    downloadHandler = new DownloadHandlerBuffer(),
                    timeout = RequestTimeoutSeconds,
                };
                _request.SetRequestHeader("Content-Type", "application/zip");
                _request.SetRequestHeader("x-upsert", "true");
                _request.SendWebRequest();
                return;
            }

            DisposeRequest();
            State = TestEvidenceUploadState.Succeeded;
            Status = "Test evidence uploaded for video processing.";
        }

        public void Cancel()
        {
            if (IsFinished) return;
            _request?.Abort();
            DisposeRequest();
            State = TestEvidenceUploadState.Cancelled;
            Status = "Test evidence upload cancelled.";
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (!IsFinished) Cancel();
            DisposeRequest();
            _disposed = true;
        }

        void StartRegistration()
        {
            string json = new QamelJson().Begin()
                .Int("contractVersion", 1)
                .Int("bundleByteCount", _bytes.Length)
                .Str("sha256", Sha256(_bytes))
                .Int("durationMs", Math.Max(1, (int)Math.Round(_clip.DurationSeconds * 1000)))
                .Num("captureFps", _clip.CaptureFps)
                .Int("frameWidth", _clip.Width)
                .Int("frameHeight", _clip.Height)
                .Int("frameCount", _clip.FrameCount)
                .End();
            _request = new UnityWebRequest(_registrationUrl, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json))
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

        static bool TryReadRegistration(
            string json,
            out RegistrationResponse response,
            out string error)
        {
            response = null;
            error = null;
            try
            {
                response = JsonUtility.FromJson<RegistrationResponse>(json);
            }
            catch (Exception exception)
            {
                error = "Qamel returned invalid Test evidence JSON: " + exception.Message;
                return false;
            }

            bool acceptedStatus = response != null &&
                (response.status == "registered" || response.status == "uploaded" ||
                 response.status == "processing" || response.status == "processed" ||
                 response.status == "failed");
            bool uploadShape = response != null &&
                ((response.status == "registered" || response.status == "failed")
                    ? TestDefinitionRoutes.IsValidArtifactUploadUrl(response.uploadUrl)
                    : string.IsNullOrWhiteSpace(response.uploadUrl));
            if (response == null || response.contractVersion != 1 ||
                !Guid.TryParse(response.evidenceId, out _) || !acceptedStatus || !uploadShape)
            {
                error = "Qamel returned incomplete Test evidence registration.";
                return false;
            }
            return true;
        }

        static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var text = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash) text.Append(value.ToString("x2"));
                return text.ToString();
            }
        }

        static string RequestFailure(UnityWebRequest request)
        {
            string serverError = QamelJson.ExtractString(
                request.downloadHandler?.text,
                "error");
            return !string.IsNullOrWhiteSpace(serverError)
                ? "Qamel rejected Test evidence (" + request.responseCode + "): " +
                  serverError + "."
                : "Test evidence upload failed (" + request.responseCode + " " +
                  request.error + ").";
        }

        void Fail(string message)
        {
            DisposeRequest();
            State = TestEvidenceUploadState.Failed;
            Status = message;
        }

        void DisposeRequest()
        {
            _request?.Dispose();
            _request = null;
        }

        [Serializable]
        sealed class RegistrationResponse
        {
            public int contractVersion;
            public string evidenceId;
            public string status;
            public string uploadUrl;
        }
    }
}
