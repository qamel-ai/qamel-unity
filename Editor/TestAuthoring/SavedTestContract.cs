using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using QamelCapture.TestAuthoring;
using UnityEngine;

namespace QamelCapture.Editor.TestAuthoring
{
    internal static class SavedTestContract
    {
        const int ContractVersion = 1;
        const long MaximumArtifactBytes = 16 * 1024 * 1024;

        public static bool TryReadCatalog(
            string json,
            out SavedTestCatalog catalog,
            out string error)
        {
            catalog = null;
            error = null;
            CatalogResponseDto response;
            try
            {
                response = JsonUtility.FromJson<CatalogResponseDto>(json);
            }
            catch (Exception exception)
            {
                error = "Qamel returned invalid saved Test JSON: " + exception.Message;
                return false;
            }

            if (response == null || response.contractVersion != ContractVersion ||
                !Guid.TryParse(response.projectId, out _) || response.tests == null)
            {
                error = "Qamel returned an incomplete saved Test list.";
                return false;
            }

            var tests = new List<SavedTestRecord>(response.tests.Length);
            var seenTests = new HashSet<string>(StringComparer.Ordinal);
            var seenVersions = new HashSet<string>(StringComparer.Ordinal);
            for (var testIndex = 0; testIndex < response.tests.Length; testIndex++)
            {
                var testDto = response.tests[testIndex];
                if (!TryReadTestIdentity(testDto, out var testId, out var name, out error))
                {
                    error = "Saved Test " + (testIndex + 1) + ": " + error;
                    return false;
                }
                if (!seenTests.Add(testId))
                {
                    error = "Qamel returned the same saved Test more than once.";
                    return false;
                }
                if (testDto.versions == null)
                {
                    error = "Saved Test '" + name + "' has no version list.";
                    return false;
                }

                var versions = new List<SavedTestVersion>(testDto.versions.Length);
                for (var versionIndex = 0;
                     versionIndex < testDto.versions.Length;
                     versionIndex++)
                {
                    if (!TryConvertVersion(
                            testDto.versions[versionIndex],
                            testId,
                            name,
                            testDto.status,
                            out var version,
                            out error))
                    {
                        error = "Saved Test '" + name + "', version entry " +
                                (versionIndex + 1) + ": " + error;
                        return false;
                    }
                    if (!seenVersions.Add(version.TestVersionId))
                    {
                        error = "Qamel returned the same immutable Test Version more than once.";
                        return false;
                    }
                    versions.Add(version);
                }

                tests.Add(new SavedTestRecord(
                    testId,
                    name,
                    testDto.status,
                    testDto.currentVersionId,
                    testDto.updatedAt,
                    versions));
            }

            catalog = new SavedTestCatalog(response.projectId, tests);
            return true;
        }

        public static bool TryReadDownload(
            string json,
            string expectedProjectId,
            SavedTestVersion selectedVersion,
            out SavedTestVersion version,
            out string error)
        {
            version = null;
            error = null;
            if (!Guid.TryParse(expectedProjectId, out _) || selectedVersion == null ||
                !Guid.TryParse(selectedVersion.TestId, out _) ||
                !Guid.TryParse(selectedVersion.TestVersionId, out _) ||
                selectedVersion.VersionNumber <= 0 ||
                selectedVersion.ContractVersion <= 0 ||
                !IsSha256(selectedVersion.DefinitionSha256))
            {
                error = "The selected saved Test Version identity is incomplete.";
                return false;
            }

            if (!TryReadDownloadResponse(
                    json,
                    expectedProjectId,
                    out version,
                    out error))
                return false;

            if (!string.Equals(
                    version.TestId,
                    selectedVersion.TestId,
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "Qamel returned a different Test than the selected Test Version.";
                version = null;
                return false;
            }
            if (!string.Equals(
                    version.TestVersionId,
                    selectedVersion.TestVersionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "Qamel returned a different immutable Test Version than requested.";
                version = null;
                return false;
            }
            if (version.VersionNumber != selectedVersion.VersionNumber)
            {
                error = "Qamel returned a different version number than the selected " +
                        "immutable Test Version.";
                version = null;
                return false;
            }
            if (version.ContractVersion != selectedVersion.ContractVersion)
            {
                error = "Qamel returned a different definition contract than the selected " +
                        "immutable Test Version.";
                version = null;
                return false;
            }
            if (!string.Equals(
                    version.DefinitionSha256,
                    selectedVersion.DefinitionSha256,
                    StringComparison.Ordinal))
            {
                error = "Qamel returned a different definition checksum than the selected " +
                        "immutable Test Version.";
                version = null;
                return false;
            }
            return true;
        }

        public static bool TryReadLeasedDownload(
            string json,
            string expectedProjectId,
            string expectedTestVersionId,
            out SavedTestVersion version,
            out string error)
        {
            version = null;
            error = null;
            if (!Guid.TryParse(expectedProjectId, out _) ||
                !Guid.TryParse(expectedTestVersionId, out _))
            {
                error = "The leased Test Version identity is incomplete.";
                return false;
            }
            if (!TryReadDownloadResponse(
                    json,
                    expectedProjectId,
                    out version,
                    out error))
                return false;
            if (string.Equals(
                    version.TestVersionId,
                    expectedTestVersionId,
                    StringComparison.OrdinalIgnoreCase))
                return true;

            error = "Qamel returned a different immutable Test Version than the leased Run.";
            version = null;
            return false;
        }

        static bool TryReadDownloadResponse(
            string json,
            string expectedProjectId,
            out SavedTestVersion version,
            out string error)
        {
            version = null;
            error = null;
            DownloadResponseDto response;
            try
            {
                response = JsonUtility.FromJson<DownloadResponseDto>(json);
            }
            catch (Exception exception)
            {
                error = "Qamel returned invalid Test Version JSON: " + exception.Message;
                return false;
            }

            if (response == null || response.contractVersion != ContractVersion ||
                !Guid.TryParse(response.projectId, out _) || response.test == null ||
                response.version == null)
            {
                error = "Qamel returned an incomplete Test Version download.";
                return false;
            }
            if (!string.Equals(
                    response.projectId,
                    expectedProjectId,
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "Qamel returned the Test Version download for a different project.";
                return false;
            }
            if (!TryReadTestIdentity(
                    response.test,
                    out var testId,
                    out var testName,
                    out error))
                return false;
            if (!TryConvertVersion(
                    response.version,
                    testId,
                    testName,
                    response.test.status,
                    out version,
                    out error))
                return false;

            foreach (var artifact in version.Artifacts)
            {
                if (!string.Equals(artifact.UploadStatus, "uploaded", StringComparison.Ordinal))
                {
                    error = "Artifact '" + ArtifactLabel(artifact) +
                            "' is " + (artifact.UploadStatus ?? "not uploaded") + ".";
                    version = null;
                    return false;
                }
                if (!TestDefinitionRoutes.IsValidArtifactDownloadUrl(artifact.DownloadUrl))
                {
                    error = "Qamel returned an invalid signed download URL for " +
                            ArtifactLabel(artifact) + ".";
                    version = null;
                    return false;
                }
                if (!DateTimeOffset.TryParse(
                        artifact.DownloadExpiresAt,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out _))
                {
                    error = "Qamel returned an invalid signed download expiry for " +
                            ArtifactLabel(artifact) + ".";
                    version = null;
                    return false;
                }
            }
            return true;
        }

        public static bool TryValidateArtifactBytes(
            SavedTestArtifact artifact,
            byte[] bytes,
            out string error)
        {
            error = null;
            if (artifact == null)
            {
                error = "The downloaded artifact declaration is missing.";
                return false;
            }
            if (bytes == null)
            {
                error = "The " + ArtifactLabel(artifact) + " download returned no bytes.";
                return false;
            }
            if (bytes.LongLength != artifact.ByteCount)
            {
                error = "The " + ArtifactLabel(artifact) + " download returned " +
                        bytes.LongLength + " bytes, but Test Version " +
                        "declares " + artifact.ByteCount + ".";
                return false;
            }

            string actual = Sha256(bytes);
            if (!string.Equals(actual, artifact.Sha256, StringComparison.Ordinal))
            {
                error = "The " + ArtifactLabel(artifact) +
                        " SHA-256 does not match the immutable Test Version. " +
                        "Replay was stopped before changing the game.";
                return false;
            }
            return true;
        }

        public static bool TryBuildRecordedDraft(
            SavedTestVersion version,
            IReadOnlyDictionary<string, byte[]> artifactBytes,
            out RecordedTestDraft draft,
            out string error)
        {
            draft = null;
            error = null;
            if (version == null)
            {
                error = "A downloaded Test Version is required.";
                return false;
            }
            if (artifactBytes == null)
            {
                error = "Downloaded Test Version artifacts are missing.";
                return false;
            }

            var state = version.StartingState;
            var action = version.Stages.FirstOrDefault(item => string.Equals(
                item.Adapter,
                SavedTestReplayCompatibility.InputTraceAdapter,
                StringComparison.Ordinal));
            var check = version.Stages.FirstOrDefault(item =>
                string.Equals(
                    item.Adapter,
                    SavedTestReplayCompatibility.HumanReviewAdapter,
                    StringComparison.Ordinal) ||
                string.Equals(
                    item.Adapter,
                    SavedTestReplayCompatibility.OutcomeObservationAdapter,
                    StringComparison.Ordinal));
            if (state == null || action == null || check == null)
            {
                error = "The downloaded definition is not a recorded Unity replay.";
                return false;
            }

            var stateArtifact = version.FindArtifact(state.PayloadArtifactId);
            var traceArtifact = version.FindArtifact(action.ArtifactId);
            if (stateArtifact == null || traceArtifact == null)
            {
                error = "The downloaded definition does not reference both replay artifacts.";
                return false;
            }
            if (!artifactBytes.TryGetValue(stateArtifact.ArtifactId, out var stateBytes) ||
                !artifactBytes.TryGetValue(traceArtifact.ArtifactId, out var traceBytes))
            {
                error = "The downloaded definition is missing state or input bytes.";
                return false;
            }
            if (!TryValidateArtifactBytes(stateArtifact, stateBytes, out error) ||
                !TryValidateArtifactBytes(traceArtifact, traceBytes, out error))
                return false;

            try
            {
                var anchor = new TestStateAnchor(
                    state.ProviderId,
                    state.FormatVersion,
                    state.Label,
                    stateBytes,
                    Path.GetFileNameWithoutExtension(state.ActiveScenePath),
                    state.ActiveScenePath);
                long replayableEvents = action.Configuration.ReplayableInputEventCount;
                double duration = action.Configuration.RecordedDurationSeconds;
                var metrics = new TestInputTraceMetrics(
                    retainedEventCount: replayableEvents,
                    recordedEventCount: replayableEvents,
                    retainedStateEventCount: replayableEvents,
                    retainedEventBytes: traceBytes.LongLength,
                    allocatedBytes: traceBytes.LongLength,
                    maximumBytes: traceBytes.LongLength,
                    hasEventTimes: true,
                    oldestEventTime: 0,
                    newestEventTime: duration,
                    deviceLayouts: action.Configuration.DeviceLayouts);
                var snapshot = new TestInputTraceSnapshot(traceBytes, metrics);
                TestExpectedOutcomeObservation expectedObservation = null;
                if (string.Equals(
                        check.Adapter,
                        SavedTestReplayCompatibility.OutcomeObservationAdapter,
                        StringComparison.Ordinal))
                {
                    expectedObservation = new TestExpectedOutcomeObservation(
                        check.Configuration.ProviderId,
                        check.Configuration.OutcomeFormatVersion,
                        new TestOutcomeObservation(
                            check.Configuration.ExpectedValue,
                            check.Configuration.ExpectedSummary));
                }
                draft = new RecordedTestDraft(
                    version.TestName,
                    check.Configuration.ExpectedOutcome,
                    anchor,
                    snapshot,
                    new TestEvidenceRange(false, 0, 0),
                    expectedObservation);
                return true;
            }
            catch (Exception exception)
            {
                error = "The downloaded Test Version could not be reconstructed in memory: " +
                        exception.Message;
                return false;
            }
        }

        static bool TryReadTestIdentity(
            TestDto dto,
            out string testId,
            out string name,
            out string error)
        {
            testId = null;
            name = null;
            error = null;
            if (dto == null)
            {
                error = "Test identity is missing.";
                return false;
            }
            testId = dto.testId;
            name = (dto.name ?? "").Trim();
            if (!Guid.TryParse(testId, out _) || string.IsNullOrWhiteSpace(name))
            {
                error = "Test identity is incomplete.";
                return false;
            }
            return true;
        }

        static bool TryConvertVersion(
            VersionDto dto,
            string testId,
            string testName,
            string testStatus,
            out SavedTestVersion version,
            out string error)
        {
            version = null;
            error = null;
            if (dto == null)
            {
                error = "Version metadata is missing.";
                return false;
            }

            string versionId = dto.testVersionId;
            if (!Guid.TryParse(versionId, out _) || dto.versionNumber <= 0 ||
                (dto.contractVersion != 2 &&
                 dto.contractVersion != RecordedTestDefinitionSerializer.ContractVersion) ||
                !IsSha256(dto.definitionSha256))
            {
                error = "Version identity is incomplete.";
                return false;
            }

            var stages = new List<SavedTestStage>();
            var seenStageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (dto.stages != null)
            {
                for (var index = 0; index < dto.stages.Length; index++)
                {
                    var item = dto.stages[index];
                    if (item == null || !Guid.TryParse(item.stageId, out _) ||
                        item.position <= 0 ||
                        string.IsNullOrWhiteSpace(item.role) ||
                        string.IsNullOrWhiteSpace(item.executor) ||
                        string.IsNullOrWhiteSpace(item.adapter))
                    {
                        error = "Stage " + (index + 1) + " is incomplete.";
                        return false;
                    }
                    if (!seenStageIds.Add(item.stageId))
                    {
                        error = "Stage " + (index + 1) +
                                " repeats an immutable Stage ID.";
                        return false;
                    }
                    var configuration = item.configuration ?? new StageConfigurationDto();
                    stages.Add(new SavedTestStage(
                        item.stageId,
                        item.position,
                        item.role,
                        item.executor,
                        item.adapter,
                        item.artifactId,
                        new SavedTestStageConfiguration(
                            configuration.replayableInputEventCount,
                            configuration.recordedDurationSeconds,
                            configuration.deviceLayouts,
                            configuration.unityRelease,
                            configuration.inputSystemVersion,
                            configuration.expectedOutcome,
                            configuration.providerId,
                            configuration.outcomeFormatVersion,
                            configuration.expectedValue,
                            configuration.expectedSummary)));
                }
                stages.Sort((left, right) => left.Position.CompareTo(right.Position));
            }

            var artifacts = new List<SavedTestArtifact>();
            if (dto.artifacts != null)
            {
                for (var index = 0; index < dto.artifacts.Length; index++)
                {
                    var item = dto.artifacts[index];
                    if (item == null || !Guid.TryParse(item.artifactId, out _) ||
                        string.IsNullOrWhiteSpace(item.kind) || item.byteCount <= 0 ||
                        item.byteCount > MaximumArtifactBytes || !IsSha256(item.sha256))
                    {
                        error = "Artifact " + (index + 1) + " is incomplete.";
                        return false;
                    }
                    artifacts.Add(new SavedTestArtifact(
                        item.artifactId,
                        item.clientRef,
                        item.kind,
                        item.byteCount,
                        item.sha256,
                        item.uploadStatus,
                        item.downloadUrl,
                        item.downloadExpiresAt));
                }
            }

            SavedTestStartingState startingState = null;
            if (dto.startingState != null)
            {
                string payloadArtifactId = dto.startingState.payloadArtifactId;
                if (string.IsNullOrWhiteSpace(dto.startingState.providerId) ||
                    dto.startingState.formatVersion <= 0 ||
                    string.IsNullOrWhiteSpace(dto.startingState.label) ||
                    string.IsNullOrWhiteSpace(dto.startingState.activeScenePath) ||
                    !Guid.TryParse(payloadArtifactId, out _))
                {
                    error = "Starting state metadata is incomplete.";
                    return false;
                }
                startingState = new SavedTestStartingState(
                    dto.startingState.providerId,
                    dto.startingState.formatVersion,
                    dto.startingState.label,
                    dto.startingState.capturedAt,
                    dto.startingState.activeScenePath,
                    payloadArtifactId);
            }

            SavedTestReplayRequirements requirements =
                RequirementsFrom(startingState, stages);

            bool artifactsReady = dto.downloadReady;

            version = new SavedTestVersion(
                testId,
                testName,
                testStatus,
                versionId,
                dto.versionNumber,
                dto.contractVersion,
                dto.definitionSha256,
                dto.createdAt,
                artifactsReady,
                requirements,
                startingState,
                stages,
                artifacts);
            return true;
        }

        static SavedTestReplayRequirements RequirementsFrom(
            SavedTestStartingState state,
            IEnumerable<SavedTestStage> stages)
        {
            var stageArray = stages == null ? Array.Empty<SavedTestStage>() : stages.ToArray();
            var action = stageArray.FirstOrDefault(item => string.Equals(
                item.Adapter,
                SavedTestReplayCompatibility.InputTraceAdapter,
                StringComparison.Ordinal));
            return new SavedTestReplayRequirements(
                action?.Configuration.UnityRelease,
                state?.ActiveScenePath,
                state?.ProviderId,
                state?.FormatVersion ?? 0,
                action?.Configuration.InputSystemVersion,
                action?.Configuration.DeviceLayouts,
                stageArray.Select(item => item.Adapter));
        }

        static bool IsSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64) return false;
            for (var index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'a' && character <= 'f')))
                    return false;
            }
            return true;
        }

        static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(bytes);
                var hex = new StringBuilder(digest.Length * 2);
                for (var index = 0; index < digest.Length; index++)
                {
                    hex.Append(digest[index].ToString(
                        "x2",
                        CultureInfo.InvariantCulture));
                }
                return hex.ToString();
            }
        }

        static string ArtifactLabel(SavedTestArtifact artifact)
        {
            if (artifact == null) return "artifact";
            if (string.Equals(artifact.Kind, "state_payload", StringComparison.Ordinal))
                return "starting state";
            if (string.Equals(artifact.Kind, "input_trace", StringComparison.Ordinal))
                return "input trace";
            return artifact.ClientRef ?? "artifact";
        }

        [Serializable]
        sealed class CatalogResponseDto
        {
            public int contractVersion;
            public string projectId;
            public TestDto[] tests;
        }

        [Serializable]
        sealed class DownloadResponseDto
        {
            public int contractVersion;
            public string projectId;
            public TestDto test;
            public VersionDto version;
        }

        [Serializable]
        sealed class TestDto
        {
            public string testId;
            public string name;
            public string status;
            public string currentVersionId;
            public string updatedAt;
            public VersionDto[] versions;
        }

        [Serializable]
        sealed class VersionDto
        {
            public string testVersionId;
            public int versionNumber;
            public int contractVersion;
            public string definitionSha256;
            public string createdAt;
            public bool downloadReady;
            public StartingStateDto startingState;
            public StageDto[] stages;
            public ArtifactDto[] artifacts;
        }

        [Serializable]
        sealed class StartingStateDto
        {
            public string providerId;
            public int formatVersion;
            public string label;
            public string capturedAt;
            public string activeScenePath;
            public string payloadArtifactId;
        }

        [Serializable]
        sealed class StageDto
        {
            public string stageId;
            public int position;
            public string role;
            public string executor;
            public string adapter;
            public StageConfigurationDto configuration;
            public string artifactId;
        }

        [Serializable]
        sealed class StageConfigurationDto
        {
            public long replayableInputEventCount;
            public double recordedDurationSeconds;
            public string[] deviceLayouts;
            public string unityRelease;
            public string inputSystemVersion;
            public string expectedOutcome;
            public string providerId;
            public int outcomeFormatVersion;
            public string expectedValue;
            public string expectedSummary;
        }

        [Serializable]
        sealed class ArtifactDto
        {
            public string artifactId;
            public string clientRef;
            public string kind;
            public long byteCount;
            public string sha256;
            public string uploadStatus;
            public string downloadUrl;
            public string downloadExpiresAt;
        }
    }
}
