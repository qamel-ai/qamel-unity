using System;
using System.Collections.Generic;
using System.Linq;
using QamelCapture.TestAuthoring;
using UnityEngine.Networking;

namespace QamelCapture.Editor.TestAuthoring
{
    internal enum SavedTestCatalogState
    {
        Loading,
        Succeeded,
        Failed,
        Cancelled,
    }

    internal sealed class SavedTestCatalogOperation : IDisposable
    {
        const int RequestTimeoutSeconds = 30;

        readonly string _expectedProjectId;
        readonly string _connectionFingerprint;
        UnityWebRequest _request;
        bool _disposed;

        public SavedTestCatalogOperation(
            string endpoint,
            string apiKey,
            string expectedProjectId,
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
            if (!Guid.TryParse(expectedProjectId, out _))
                throw new ArgumentException("A connected project ID is required.",
                    nameof(expectedProjectId));

            _expectedProjectId = expectedProjectId;
            _connectionFingerprint = TestLabPreferences.ComputeAuthoringFingerprint(
                endpoint,
                key);
            State = SavedTestCatalogState.Loading;
            Status = "Loading saved Tests from Qamel.";
            if (startImmediately)
            {
                _request = AuthorizedGet(TestDefinitionRoutes.TestsUrl(endpoint), key);
                _request.timeout = RequestTimeoutSeconds;
                _request.SendWebRequest();
            }
        }

        public SavedTestCatalogState State { get; private set; }
        public string ConnectionFingerprint => _connectionFingerprint;
        public string Status { get; private set; }
        public SavedTestCatalog Catalog { get; private set; }
        public bool IsFinished => State == SavedTestCatalogState.Succeeded ||
                                  State == SavedTestCatalogState.Failed ||
                                  State == SavedTestCatalogState.Cancelled;

        public void Tick()
        {
            if (_disposed || IsFinished || _request == null || !_request.isDone) return;
            if (_request.result != UnityWebRequest.Result.Success)
            {
                Fail(RequestFailure(_request, "Saved Test list"));
                return;
            }
            if (!SavedTestContract.TryReadCatalog(
                    _request.downloadHandler?.text,
                    out var catalog,
                    out var error))
            {
                Fail(error);
                return;
            }
            if (!string.Equals(
                    catalog.ProjectId,
                    _expectedProjectId,
                    StringComparison.OrdinalIgnoreCase))
            {
                Fail("Qamel returned saved Tests for a different project.");
                return;
            }

            Catalog = catalog;
            DisposeRequest();
            State = SavedTestCatalogState.Succeeded;
            int versionCount = catalog.Tests.Sum(test => test.Versions.Count);
            Status = versionCount == 0
                ? "This project has no saved Test Versions yet."
                : "Loaded " + catalog.Tests.Count + " Test" +
                  (catalog.Tests.Count == 1 ? "" : "s") + " and " +
                  versionCount + " immutable version" +
                  (versionCount == 1 ? "" : "s") + ".";
        }

        public void Cancel()
        {
            if (IsFinished) return;
            _request?.Abort();
            DisposeRequest();
            Catalog = null;
            State = SavedTestCatalogState.Cancelled;
            Status = "Loading saved Tests was cancelled.";
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
            Catalog = null;
            State = SavedTestCatalogState.Failed;
            Status = error;
        }

        static UnityWebRequest AuthorizedGet(string url, string apiKey)
        {
            var request = UnityWebRequest.Get(url);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.redirectLimit = 0;
            request.SetRequestHeader(
                IngestHeaders.Authorization,
                IngestHeaders.Bearer(apiKey));
            request.SetRequestHeader(IngestHeaders.Plugin, IngestHeaders.PluginValue());
            return request;
        }

        internal static string RequestFailure(UnityWebRequest request, string operation)
        {
            string serverError = QamelJson.ExtractString(
                request.downloadHandler?.text,
                "error");
            if (!string.IsNullOrWhiteSpace(serverError))
            {
                return operation + " was rejected (" + request.responseCode + "): " +
                       serverError + ".";
            }
            return operation + " failed (" + request.responseCode + " " +
                   request.error + ").";
        }

        void DisposeRequest()
        {
            _request?.Dispose();
            _request = null;
        }
    }

    internal enum SavedTestReplayDownloadState
    {
        FetchingVersion,
        DownloadingArtifacts,
        Ready,
        Failed,
        Cancelled,
    }

    internal sealed class SavedTestReplayOperation : IDisposable
    {
        const int RequestTimeoutSeconds = 120;

        readonly string _expectedProjectId;
        readonly string _expectedTestVersionId;
        readonly SavedTestVersion _selectedVersion;
        readonly string _apiKey;
        readonly string _connectionFingerprint;
        readonly TestStateRegistry _registry;
        readonly ISavedTestReplayEnvironment _environment;
        readonly Dictionary<string, byte[]> _artifactBytes =
            new Dictionary<string, byte[]>(StringComparer.Ordinal);
        UnityWebRequest _request;
        SavedTestVersion _version;
        int _artifactIndex;
        bool _disposed;

        public SavedTestReplayOperation(
            string endpoint,
            string apiKey,
            string expectedProjectId,
            SavedTestVersion selectedVersion,
            TestStateRegistry registry,
            ISavedTestReplayEnvironment environment,
            bool startImmediately = true)
            : this(
                endpoint,
                apiKey,
                expectedProjectId,
                selectedVersion?.TestVersionId,
                selectedVersion,
                registry,
                environment,
                startImmediately)
        {
        }

        public SavedTestReplayOperation(
            string endpoint,
            string apiKey,
            string expectedProjectId,
            string leasedTestVersionId,
            TestStateRegistry registry,
            ISavedTestReplayEnvironment environment,
            bool startImmediately = true)
            : this(
                endpoint,
                apiKey,
                expectedProjectId,
                leasedTestVersionId,
                null,
                registry,
                environment,
                startImmediately)
        {
        }

        SavedTestReplayOperation(
            string endpoint,
            string apiKey,
            string expectedProjectId,
            string expectedTestVersionId,
            SavedTestVersion selectedVersion,
            TestStateRegistry registry,
            ISavedTestReplayEnvironment environment,
            bool startImmediately)
        {
            if (!Guid.TryParse(expectedProjectId, out _))
                throw new ArgumentException(
                    "A connected project ID is required.",
                    nameof(expectedProjectId));
            if (!TestDefinitionRoutes.TryGetVersionDownloadUrl(
                    endpoint,
                    expectedTestVersionId,
                    out var downloadUrl))
            {
                throw new ArgumentException(
                    "A valid Qamel endpoint and Test Version ID are required.",
                    nameof(expectedTestVersionId));
            }
            _apiKey = (apiKey ?? "").Trim();
            if (!TestDefinitionRoutes.IsValidProjectApiKey(_apiKey))
                throw new ArgumentException(
                    "A complete project API key beginning with qa_key_ is required.",
                    nameof(apiKey));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _environment = environment ?? throw new ArgumentNullException(nameof(environment));
            _expectedProjectId = expectedProjectId;
            _expectedTestVersionId = expectedTestVersionId;
            _selectedVersion = selectedVersion;
            _connectionFingerprint = TestLabPreferences.ComputeAuthoringFingerprint(
                endpoint,
                _apiKey);
            State = SavedTestReplayDownloadState.FetchingVersion;
            Status = "Requesting the immutable Test Version and short-lived downloads.";
            if (startImmediately)
            {
                _request = UnityWebRequest.Get(downloadUrl);
                _request.downloadHandler = new DownloadHandlerBuffer();
                _request.redirectLimit = 0;
                _request.timeout = RequestTimeoutSeconds;
                _request.SetRequestHeader(
                    IngestHeaders.Authorization,
                    IngestHeaders.Bearer(_apiKey));
                _request.SetRequestHeader(IngestHeaders.Plugin, IngestHeaders.PluginValue());
                _request.SendWebRequest();
            }
        }

        public SavedTestReplayDownloadState State { get; private set; }
        public string ExpectedProjectId => _expectedProjectId;
        public SavedTestVersion SelectedVersion => _selectedVersion;
        public string ConnectionFingerprint => _connectionFingerprint;
        public string Status { get; private set; }
        public SavedTestVersion Version => _version;
        public RecordedTestDraft ReadyDraft { get; private set; }
        public bool IsFinished => State == SavedTestReplayDownloadState.Ready ||
                                  State == SavedTestReplayDownloadState.Failed ||
                                  State == SavedTestReplayDownloadState.Cancelled;

        public void Tick()
        {
            if (_disposed || IsFinished || _request == null || !_request.isDone) return;
            if (_request.result != UnityWebRequest.Result.Success)
            {
                string label = State == SavedTestReplayDownloadState.FetchingVersion
                    ? "Test Version download preparation"
                    : "Artifact " + (_artifactIndex + 1) + " download";
                Fail(SavedTestCatalogOperation.RequestFailure(_request, label));
                return;
            }

            if (State == SavedTestReplayDownloadState.FetchingVersion)
            {
                string error;
                bool parsed = _selectedVersion != null
                    ? SavedTestContract.TryReadDownload(
                        _request.downloadHandler?.text,
                        _expectedProjectId,
                        _selectedVersion,
                        out _version,
                        out error)
                    : SavedTestContract.TryReadLeasedDownload(
                        _request.downloadHandler?.text,
                        _expectedProjectId,
                        _expectedTestVersionId,
                        out _version,
                        out error);
                if (!parsed)
                {
                    Fail(error);
                    return;
                }
                var compatibility = SavedTestReplayCompatibility.Evaluate(
                    _version,
                    _registry,
                    _environment);
                if (!compatibility.IsCompatible)
                {
                    Fail(compatibility.Reason);
                    return;
                }

                DisposeRequest();
                _artifactIndex = 0;
                StartNextArtifact();
                return;
            }

            var artifact = _version.Artifacts[_artifactIndex];
            byte[] bytes = _request.downloadHandler?.data;
            if (!SavedTestContract.TryValidateArtifactBytes(artifact, bytes, out var artifactError))
            {
                Fail(artifactError);
                return;
            }
            _artifactBytes[artifact.ArtifactId] = (byte[])bytes.Clone();
            _artifactIndex++;
            DisposeRequest();
            StartNextArtifact();
        }

        public void Cancel()
        {
            if (IsFinished) return;
            _request?.Abort();
            DisposeRequest();
            _artifactBytes.Clear();
            _version = null;
            ReadyDraft = null;
            State = SavedTestReplayDownloadState.Cancelled;
            Status = "Saved Test replay download was cancelled before gameplay changed.";
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (!IsFinished) Cancel();
            DisposeRequest();
            _artifactBytes.Clear();
            _disposed = true;
        }

        void StartNextArtifact()
        {
            if (_artifactIndex >= _version.Artifacts.Count)
            {
                CompleteDownload();
                return;
            }

            var artifact = _version.Artifacts[_artifactIndex];
            State = SavedTestReplayDownloadState.DownloadingArtifacts;
            Status = "Downloading " + ArtifactLabel(artifact) + " (artifact " +
                     (_artifactIndex + 1) + " of " + _version.Artifacts.Count +
                     ") into memory.";
            _request = UnityWebRequest.Get(artifact.DownloadUrl);
            _request.downloadHandler = new DownloadHandlerBuffer();
            _request.redirectLimit = 0;
            _request.timeout = RequestTimeoutSeconds;
            _request.SendWebRequest();
        }

        void CompleteDownload()
        {
            var compatibility = SavedTestReplayCompatibility.Evaluate(
                _version,
                _registry,
                _environment);
            if (!compatibility.IsCompatible)
            {
                Fail("The local replay environment changed during download. " +
                     compatibility.Reason);
                return;
            }

            var action = _version.Stages.First(item => string.Equals(
                item.Adapter,
                SavedTestReplayCompatibility.InputTraceAdapter,
                StringComparison.Ordinal));
            if (!_artifactBytes.TryGetValue(action.ArtifactId, out var traceBytes))
            {
                Fail("The input trace bytes are missing after download.");
                return;
            }
            var traceCompatibility = SavedTestReplayCompatibility.ValidateDownloadedTrace(
                _version,
                traceBytes,
                _environment);
            if (!traceCompatibility.IsCompatible)
            {
                Fail(traceCompatibility.Reason);
                return;
            }
            if (!SavedTestContract.TryBuildRecordedDraft(
                    _version,
                    _artifactBytes,
                    out var draft,
                    out var error))
            {
                Fail(error);
                return;
            }

            ReadyDraft = draft;
            _artifactBytes.Clear();
            State = SavedTestReplayDownloadState.Ready;
            Status = "Verified and reconstructed Test version " +
                     _version.VersionNumber +
                     " from its exact immutable state and input bytes.";
        }

        void Fail(string error)
        {
            DisposeRequest();
            _artifactBytes.Clear();
            ReadyDraft = null;
            State = SavedTestReplayDownloadState.Failed;
            Status = error;
        }

        static string ArtifactLabel(SavedTestArtifact artifact)
        {
            if (string.Equals(artifact.Kind, "state_payload", StringComparison.Ordinal))
                return "starting state";
            if (string.Equals(artifact.Kind, "input_trace", StringComparison.Ordinal))
                return "input trace";
            return artifact.ClientRef ?? "artifact";
        }

        void DisposeRequest()
        {
            _request?.Dispose();
            _request = null;
        }
    }
}
