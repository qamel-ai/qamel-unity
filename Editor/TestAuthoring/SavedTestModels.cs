using System;
using System.Collections.Generic;
using System.Linq;

namespace QamelCapture.Editor.TestAuthoring
{
    internal sealed class SavedTestCatalog
    {
        readonly SavedTestRecord[] _tests;

        public SavedTestCatalog(string projectId, IEnumerable<SavedTestRecord> tests)
        {
            ProjectId = projectId;
            _tests = tests == null ? Array.Empty<SavedTestRecord>() : tests.ToArray();
        }

        public string ProjectId { get; }
        public IReadOnlyList<SavedTestRecord> Tests => Array.AsReadOnly(_tests);

        public bool ContainsVersion(SavedTestVersion version)
        {
            if (version == null) return false;
            return _tests.Any(test => test.Versions.Any(candidate =>
                ReferenceEquals(candidate, version)));
        }
    }

    internal sealed class LoadedSavedTestBinding
    {
        readonly SavedTestCatalog _catalog;
        readonly SavedTestVersion _selectedVersion;
        readonly string _draftId;

        public LoadedSavedTestBinding(
            string connectionFingerprint,
            string projectId,
            SavedTestCatalog catalog,
            SavedTestVersion selectedVersion,
            string draftId)
        {
            ConnectionFingerprint = connectionFingerprint;
            ProjectId = projectId;
            _catalog = catalog;
            _selectedVersion = selectedVersion;
            _draftId = draftId;
        }

        public string ConnectionFingerprint { get; }
        public string ProjectId { get; }

        public bool Matches(
            string connectionFingerprint,
            string projectId,
            SavedTestCatalog catalog,
            string draftId)
        {
            return !string.IsNullOrWhiteSpace(_draftId) &&
                   string.Equals(
                       ConnectionFingerprint,
                       connectionFingerprint,
                       StringComparison.Ordinal) &&
                   string.Equals(ProjectId, projectId, StringComparison.OrdinalIgnoreCase) &&
                   ReferenceEquals(_catalog, catalog) &&
                   _catalog != null &&
                   _catalog.ContainsVersion(_selectedVersion) &&
                   string.Equals(_draftId, draftId, StringComparison.Ordinal);
        }
    }

    internal sealed class SavedTestRecord
    {
        readonly SavedTestVersion[] _versions;

        public SavedTestRecord(
            string testId,
            string name,
            string status,
            string currentVersionId,
            string updatedAt,
            IEnumerable<SavedTestVersion> versions)
        {
            TestId = testId;
            Name = name;
            Status = status;
            CurrentVersionId = currentVersionId;
            UpdatedAt = updatedAt;
            _versions = versions == null ? Array.Empty<SavedTestVersion>() : versions.ToArray();
        }

        public string TestId { get; }
        public string Name { get; }
        public string Status { get; }
        public string CurrentVersionId { get; }
        public string UpdatedAt { get; }
        public IReadOnlyList<SavedTestVersion> Versions => Array.AsReadOnly(_versions);
    }

    internal sealed class SavedTestVersion
    {
        readonly SavedTestStage[] _stages;
        readonly SavedTestArtifact[] _artifacts;

        public SavedTestVersion(
            string testId,
            string testName,
            string testStatus,
            string testVersionId,
            int versionNumber,
            int contractVersion,
            string definitionSha256,
            string createdAt,
            bool artifactsReady,
            SavedTestReplayRequirements requirements,
            SavedTestStartingState startingState,
            IEnumerable<SavedTestStage> stages,
            IEnumerable<SavedTestArtifact> artifacts)
        {
            TestId = testId;
            TestName = testName;
            TestStatus = testStatus;
            TestVersionId = testVersionId;
            VersionNumber = versionNumber;
            ContractVersion = contractVersion;
            DefinitionSha256 = definitionSha256;
            CreatedAt = createdAt;
            ArtifactsReady = artifactsReady;
            Requirements = requirements;
            StartingState = startingState;
            _stages = stages == null ? Array.Empty<SavedTestStage>() : stages.ToArray();
            _artifacts = artifacts == null ? Array.Empty<SavedTestArtifact>() : artifacts.ToArray();
        }

        public string TestId { get; }
        public string TestName { get; }
        public string TestStatus { get; }
        public string TestVersionId { get; }
        public int VersionNumber { get; }
        public int ContractVersion { get; }
        public string DefinitionSha256 { get; }
        public string CreatedAt { get; }
        public bool ArtifactsReady { get; }
        public SavedTestReplayRequirements Requirements { get; }
        public SavedTestStartingState StartingState { get; }
        public IReadOnlyList<SavedTestStage> Stages => Array.AsReadOnly(_stages);
        public IReadOnlyList<SavedTestArtifact> Artifacts => Array.AsReadOnly(_artifacts);

        public SavedTestArtifact FindArtifact(string artifactId)
        {
            if (string.IsNullOrWhiteSpace(artifactId)) return null;
            return _artifacts.FirstOrDefault(item => string.Equals(
                item.ArtifactId,
                artifactId,
                StringComparison.Ordinal));
        }
    }

    internal sealed class SavedTestReplayRequirements
    {
        readonly string[] _deviceLayouts;
        readonly string[] _adapters;

        public SavedTestReplayRequirements(
            string unityRelease,
            string activeScenePath,
            string stateProviderId,
            int stateFormatVersion,
            string inputSystemVersion,
            IEnumerable<string> deviceLayouts,
            IEnumerable<string> adapters)
        {
            UnityRelease = unityRelease;
            ActiveScenePath = activeScenePath;
            StateProviderId = stateProviderId;
            StateFormatVersion = stateFormatVersion;
            InputSystemVersion = inputSystemVersion;
            _deviceLayouts = deviceLayouts == null
                ? Array.Empty<string>()
                : deviceLayouts.ToArray();
            _adapters = adapters == null ? Array.Empty<string>() : adapters.ToArray();
        }

        public string UnityRelease { get; }
        public string ActiveScenePath { get; }
        public string StateProviderId { get; }
        public int StateFormatVersion { get; }
        public string InputSystemVersion { get; }
        public IReadOnlyList<string> DeviceLayouts => Array.AsReadOnly(_deviceLayouts);
        public IReadOnlyList<string> Adapters => Array.AsReadOnly(_adapters);
    }

    internal sealed class SavedTestStartingState
    {
        public SavedTestStartingState(
            string providerId,
            int formatVersion,
            string label,
            string capturedAt,
            string activeScenePath,
            string payloadArtifactId)
        {
            ProviderId = providerId;
            FormatVersion = formatVersion;
            Label = label;
            CapturedAt = capturedAt;
            ActiveScenePath = activeScenePath;
            PayloadArtifactId = payloadArtifactId;
        }

        public string ProviderId { get; }
        public int FormatVersion { get; }
        public string Label { get; }
        public string CapturedAt { get; }
        public string ActiveScenePath { get; }
        public string PayloadArtifactId { get; }
    }

    internal sealed class SavedTestStage
    {
        public SavedTestStage(
            string stageId,
            int position,
            string role,
            string executor,
            string adapter,
            string artifactId,
            SavedTestStageConfiguration configuration)
        {
            StageId = stageId;
            Position = position;
            Role = role;
            Executor = executor;
            Adapter = adapter;
            ArtifactId = artifactId;
            Configuration = configuration ?? new SavedTestStageConfiguration();
        }

        public string StageId { get; }
        public int Position { get; }
        public string Role { get; }
        public string Executor { get; }
        public string Adapter { get; }
        public string ArtifactId { get; }
        public SavedTestStageConfiguration Configuration { get; }
    }

    internal sealed class SavedTestStageConfiguration
    {
        readonly string[] _deviceLayouts;

        public SavedTestStageConfiguration(
            long replayableInputEventCount = 0,
            double recordedDurationSeconds = 0,
            IEnumerable<string> deviceLayouts = null,
            string unityRelease = null,
            string inputSystemVersion = null,
            string expectedOutcome = null,
            string providerId = null,
            int outcomeFormatVersion = 0,
            string expectedValue = null,
            string expectedSummary = null)
        {
            ReplayableInputEventCount = replayableInputEventCount;
            RecordedDurationSeconds = recordedDurationSeconds;
            _deviceLayouts = deviceLayouts == null
                ? Array.Empty<string>()
                : deviceLayouts.ToArray();
            UnityRelease = unityRelease;
            InputSystemVersion = inputSystemVersion;
            ExpectedOutcome = expectedOutcome;
            ProviderId = providerId;
            OutcomeFormatVersion = outcomeFormatVersion;
            ExpectedValue = expectedValue;
            ExpectedSummary = expectedSummary;
        }

        public long ReplayableInputEventCount { get; }
        public double RecordedDurationSeconds { get; }
        public IReadOnlyList<string> DeviceLayouts => Array.AsReadOnly(_deviceLayouts);
        public string UnityRelease { get; }
        public string InputSystemVersion { get; }
        public string ExpectedOutcome { get; }
        public string ProviderId { get; }
        public int OutcomeFormatVersion { get; }
        public string ExpectedValue { get; }
        public string ExpectedSummary { get; }
    }

    internal sealed class SavedTestArtifact
    {
        public SavedTestArtifact(
            string artifactId,
            string clientRef,
            string kind,
            long byteCount,
            string sha256,
            string uploadStatus,
            string downloadUrl,
            string downloadExpiresAt)
        {
            ArtifactId = artifactId;
            ClientRef = clientRef;
            Kind = kind;
            ByteCount = byteCount;
            Sha256 = sha256;
            UploadStatus = uploadStatus;
            DownloadUrl = downloadUrl;
            DownloadExpiresAt = downloadExpiresAt;
        }

        public string ArtifactId { get; }
        public string ClientRef { get; }
        public string Kind { get; }
        public long ByteCount { get; }
        public string Sha256 { get; }
        public string UploadStatus { get; }
        public string DownloadUrl { get; }
        public string DownloadExpiresAt { get; }
    }
}
