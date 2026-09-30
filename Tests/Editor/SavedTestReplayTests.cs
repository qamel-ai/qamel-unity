using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using QamelCapture.Editor.TestAuthoring;
using QamelCapture.TestAuthoring;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace QamelCapture.Tests
{
    public sealed class SavedTestReplayTests
    {
        const string ProjectId = "018f0000-0000-7000-8000-000000000001";
        const string TestId = "018f0000-0000-7000-8000-000000000002";
        const string VersionId = "018f0000-0000-7000-8000-000000000003";
        const string StateArtifactId = "018f0000-0000-7000-8000-000000000004";
        const string TraceArtifactId = "018f0000-0000-7000-8000-000000000005";
        const string StateStageId = "018f0000-0000-7000-8000-000000000006";
        const string ActionStageId = "018f0000-0000-7000-8000-000000000007";
        const string CheckStageId = "018f0000-0000-7000-8000-000000000008";
        const string OtherProjectId = "018f0000-0000-7000-8000-000000000101";
        const string OtherTestId = "018f0000-0000-7000-8000-000000000102";
        const string OtherVersionId = "018f0000-0000-7000-8000-000000000103";
        const string EditorKey = "qa_key_abcdefghijklmnopqrstuvwxyzABCDEF";

        class Provider : IQamelTestStateProvider
        {
            readonly string _providerId;
            readonly int _formatVersion;

            public Provider(string providerId = "qamel.fixture.state", int formatVersion = 2)
            {
                _providerId = providerId;
                _formatVersion = formatVersion;
            }

            public string ProviderId => _providerId;
            public int StateFormatVersion => _formatVersion;
            public void CaptureState(TestStateCaptureContext context)
            {
                context.Fail("not used");
            }
            public void RestoreState(TestStateRestoreContext context)
            {
                context.ReadyForReplay();
            }
        }

        sealed class OutcomeProvider : Provider, IQamelTestOutcomeProvider
        {
            public int OutcomeFormatVersion => 1;
            public TestOutcomeObservation ObserveOutcome()
            {
                return new TestOutcomeObservation("exit-reached", "Player reached exit");
            }
        }

        sealed class FakeEnvironment : ISavedTestReplayEnvironment
        {
            public string UnityRelease { get; set; } = "2022.3";
            public string ActiveScenePath { get; set; } = "Assets/FixtureScene.unity";
            public bool InputAvailable { get; set; } = true;
            public string InputUnavailableReason { get; set; }
            public string InputSystemVersion { get; set; } = "1.14.2";
            public bool TraceSupported { get; set; } = true;
            public string TraceError { get; set; }
            public int TraceInspectionCount { get; private set; }
            public IReadOnlyList<string> ActualLayouts { get; set; } =
                new[] { "Keyboard", "Mouse" };

            public bool TryInspectAndMapInputTrace(
                byte[] bytes,
                out IReadOnlyList<string> actualDeviceLayouts,
                out string error)
            {
                TraceInspectionCount++;
                actualDeviceLayouts = ActualLayouts;
                error = TraceError;
                return TraceSupported;
            }
        }

        [Test]
        public void ParsesCatalogAndKeepsServerDownloadReadinessAuthoritative()
        {
            byte[] stateBytes = { 1, 2, 3 };
            byte[] traceBytes = { 4, 5, 6, 7 };
            string json = CatalogJson(
                stateBytes,
                traceBytes,
                contractVersion: 2,
                downloadReady: false);

            Assert.IsTrue(SavedTestContract.TryReadCatalog(
                json,
                out var catalog,
                out var error), error);
            Assert.AreEqual(ProjectId, catalog.ProjectId);
            Assert.AreEqual(1, catalog.Tests.Count);
            Assert.AreEqual(1, catalog.Tests[0].Versions.Count);
            var version = catalog.Tests[0].Versions[0];
            Assert.AreEqual(VersionId, version.TestVersionId);
            Assert.IsFalse(
                version.ArtifactsReady,
                "uploaded display statuses must not override downloadReady=false");

            var registry = RegistryWith(new Provider());
            var compatibility = SavedTestReplayCompatibility.Evaluate(
                version,
                registry,
                new FakeEnvironment());
            Assert.IsFalse(compatibility.IsCompatible);
            StringAssert.Contains("not uploaded", compatibility.Reason);
        }

        [Test]
        public void CatalogRejectsAnUnsupportedDefinitionContract()
        {
            byte[] stateBytes = { 1 };
            byte[] traceBytes = { 2 };
            Assert.IsFalse(SavedTestContract.TryReadCatalog(
                CatalogJson(stateBytes, traceBytes, 1, true),
                out var catalog,
                out var error));
            Assert.IsNull(catalog);
            StringAssert.Contains("Version identity is incomplete", error);
        }

        [Test]
        public void DownloadedBytesAreVerifiedAndReconstructedExactlyInMemory()
        {
            byte[] stateBytes = { 3, 5, 8 };
            byte[] traceBytes = { 13, 21, 34, 55 };
            Assert.IsTrue(SavedTestContract.TryReadDownload(
                DownloadJson(stateBytes, traceBytes),
                ProjectId,
                CatalogVersion(stateBytes, traceBytes),
                out var version,
                out var parseError), parseError);

            var registry = RegistryWith(new Provider());
            var environment = new FakeEnvironment();
            var compatibility = SavedTestReplayCompatibility.Evaluate(
                version,
                registry,
                environment);
            Assert.IsTrue(compatibility.IsCompatible, compatibility.Reason);
            var traceCompatibility = SavedTestReplayCompatibility.ValidateDownloadedTrace(
                version,
                traceBytes,
                environment);
            Assert.IsTrue(traceCompatibility.IsCompatible, traceCompatibility.Reason);

            var downloaded = new Dictionary<string, byte[]>
            {
                [StateArtifactId] = stateBytes,
                [TraceArtifactId] = traceBytes,
            };
            Assert.IsTrue(SavedTestContract.TryBuildRecordedDraft(
                version,
                downloaded,
                out var draft,
                out var buildError), buildError);

            stateBytes[0] = 255;
            traceBytes[0] = 255;
            CollectionAssert.AreEqual(new byte[] { 3, 5, 8 }, draft.Anchor.GetPayloadCopy());
            CollectionAssert.AreEqual(
                new byte[] { 13, 21, 34, 55 },
                draft.InputTrace.GetBytesCopy());
            Assert.AreEqual("Reach the exit", draft.Name);
            Assert.AreEqual("The player reaches the exit.", draft.ExpectedOutcome);
            Assert.AreEqual(9, draft.InputTrace.Metrics.RetainedStateEventCount);
            Assert.AreEqual(2.5, draft.InputTrace.Metrics.RetainedDurationSeconds);
        }

        [Test]
        public void VersionThreeAutomaticOutcomeCheckIsValidatedAndReconstructed()
        {
            byte[] stateBytes = { 3, 5, 8 };
            byte[] traceBytes = { 13, 21, 34, 55 };
            Assert.IsTrue(SavedTestContract.TryReadCatalog(
                CatalogJson(
                    stateBytes,
                    traceBytes,
                    contractVersion: 3,
                    downloadReady: true,
                    automaticCheck: true),
                out var catalog,
                out var catalogError), catalogError);
            var selected = catalog.Tests[0].Versions[0];

            Assert.IsTrue(SavedTestContract.TryReadDownload(
                DownloadJson(
                    stateBytes,
                    traceBytes,
                    contractVersion: 3,
                    automaticCheck: true),
                ProjectId,
                selected,
                out var version,
                out var parseError), parseError);

            var registry = RegistryWith(new OutcomeProvider());
            var compatibility = SavedTestReplayCompatibility.Evaluate(
                version,
                registry,
                new FakeEnvironment());
            Assert.IsTrue(compatibility.IsCompatible, compatibility.Reason);

            Assert.IsTrue(SavedTestContract.TryBuildRecordedDraft(
                version,
                new Dictionary<string, byte[]>
                {
                    [StateArtifactId] = stateBytes,
                    [TraceArtifactId] = traceBytes,
                },
                out var draft,
                out var buildError), buildError);
            Assert.IsNotNull(draft.ExpectedObservation);
            Assert.AreEqual("exit-reached", draft.ExpectedObservation.Observation.Value);
            Assert.AreEqual("Player reached exit", draft.ExpectedObservation.Observation.Summary);
        }

        [Test]
        public void LeasedDownloadBindsDirectlyToTheAssignedVersionId()
        {
            byte[] stateBytes = { 3, 5, 8 };
            byte[] traceBytes = { 13, 21, 34, 55 };
            string json = DownloadJson(
                stateBytes,
                traceBytes,
                contractVersion: 3,
                automaticCheck: true);

            Assert.IsTrue(SavedTestContract.TryReadLeasedDownload(
                json,
                ProjectId,
                VersionId,
                out var version,
                out var error), error);
            Assert.AreEqual(VersionId, version.TestVersionId);
            Assert.IsFalse(SavedTestContract.TryReadLeasedDownload(
                json,
                ProjectId,
                OtherVersionId,
                out _,
                out var mismatch));
            StringAssert.Contains("leased Run", mismatch);
        }

        [Test]
        public void DownloadResponseMustMatchConnectedProjectAndSelectedImmutableVersion()
        {
            byte[] stateBytes = { 3, 5, 8 };
            byte[] traceBytes = { 13, 21, 34, 55 };
            var selected = CatalogVersion(stateBytes, traceBytes);
            string json = DownloadJson(stateBytes, traceBytes);

            AssertDownloadRejected(
                json.Replace(
                    "\"projectId\":\"" + ProjectId + "\"",
                    "\"projectId\":\"" + OtherProjectId + "\""),
                selected,
                "different project");
            AssertDownloadRejected(
                json.Replace(
                    "\"testId\":\"" + TestId + "\"",
                    "\"testId\":\"" + OtherTestId + "\""),
                selected,
                "different Test");
            AssertDownloadRejected(
                json.Replace(
                    "\"testVersionId\":\"" + VersionId + "\"",
                    "\"testVersionId\":\"" + OtherVersionId + "\""),
                selected,
                "different immutable Test Version");
            AssertDownloadRejected(
                json.Replace("\"versionNumber\":3", "\"versionNumber\":4"),
                selected,
                "different version number");
            AssertDownloadRejected(
                json.Replace("\"contractVersion\":2", "\"contractVersion\":3"),
                selected,
                "different definition contract");
            AssertDownloadRejected(
                json.Replace(new string('a', 64), new string('b', 64)),
                selected,
                "different definition checksum");
        }

        [Test]
        public void ReadContractDoesNotAcceptSpeculativeIdentityOrArtifactAliases()
        {
            byte[] stateBytes = { 3, 5, 8 };
            byte[] traceBytes = { 13, 21, 34, 55 };
            var selected = CatalogVersion(stateBytes, traceBytes);
            string json = DownloadJson(stateBytes, traceBytes);

            AssertDownloadRejected(
                json.Replace("\"testId\":", "\"id\":"),
                selected,
                "Test identity is incomplete");
            AssertDownloadRejected(
                json.Replace("\"testVersionId\":", "\"id\":"),
                selected,
                "Version identity is incomplete");
            AssertDownloadRejected(
                json.Replace("\"payloadArtifactId\":", "\"artifactId\":"),
                selected,
                "Starting state metadata is incomplete");
            AssertDownloadRejected(
                json.Replace("\"downloadExpiresAt\":", "\"expiresAt\":"),
                selected,
                "invalid signed download expiry");
        }

        [Test]
        public void CatalogSelectionIsBoundToTheCurrentCatalogInstance()
        {
            byte[] stateBytes = { 1 };
            byte[] traceBytes = { 2 };
            Assert.IsTrue(SavedTestContract.TryReadCatalog(
                CatalogJson(stateBytes, traceBytes, 2, true),
                out var current,
                out var currentError), currentError);
            Assert.IsTrue(SavedTestContract.TryReadCatalog(
                CatalogJson(stateBytes, traceBytes, 2, true),
                out var stale,
                out var staleError), staleError);

            var currentSelection = current.Tests[0].Versions[0];
            Assert.IsTrue(current.ContainsVersion(currentSelection));
            Assert.IsFalse(current.ContainsVersion(stale.Tests[0].Versions[0]));
        }

        [Test]
        public void LoadedDraftBindingRequiresExactConnectionProjectCatalogAndDraft()
        {
            byte[] stateBytes = { 1 };
            byte[] traceBytes = { 2 };
            Assert.IsTrue(SavedTestContract.TryReadCatalog(
                CatalogJson(stateBytes, traceBytes, 2, true),
                out var current,
                out var currentError), currentError);
            Assert.IsTrue(SavedTestContract.TryReadCatalog(
                CatalogJson(stateBytes, traceBytes, 2, true),
                out var refreshed,
                out var refreshedError), refreshedError);

            const string fingerprint = "connection-a";
            const string draftId = "draft-a";
            var binding = new LoadedSavedTestBinding(
                fingerprint,
                ProjectId,
                current,
                current.Tests[0].Versions[0],
                draftId);

            Assert.IsTrue(binding.Matches(
                fingerprint,
                ProjectId,
                current,
                draftId));
            Assert.IsFalse(binding.Matches(
                "connection-b",
                ProjectId,
                current,
                draftId));
            Assert.IsFalse(binding.Matches(
                fingerprint,
                OtherProjectId,
                current,
                draftId));
            Assert.IsFalse(binding.Matches(
                fingerprint,
                ProjectId,
                refreshed,
                draftId));
            Assert.IsFalse(binding.Matches(
                fingerprint,
                ProjectId,
                current,
                "draft-b"));

            var foreignSelection = new LoadedSavedTestBinding(
                fingerprint,
                ProjectId,
                current,
                refreshed.Tests[0].Versions[0],
                draftId);
            Assert.IsFalse(foreignSelection.Matches(
                fingerprint,
                ProjectId,
                current,
                draftId));
        }

        [Test]
        public void SavedReplayBindingReasonOnlyBlocksAStaleLoadedSavedDraft()
        {
            Assert.IsNull(TestLabSession.SavedReplayBindingBlockReason(
                currentDraftIsSavedVersion: false,
                bindingIsCurrent: false));
            Assert.IsNull(TestLabSession.SavedReplayBindingBlockReason(
                currentDraftIsSavedVersion: true,
                bindingIsCurrent: true));

            string reason = TestLabSession.SavedReplayBindingBlockReason(
                currentDraftIsSavedVersion: true,
                bindingIsCurrent: false);
            StringAssert.Contains("earlier connection or Test list", reason);
            StringAssert.Contains("Clear it", reason);
            StringAssert.Contains("load the saved Test again", reason);
        }

        [Test]
        public void PartialOrCorruptArtifactsFailBeforeDraftConstruction()
        {
            byte[] stateBytes = { 3, 5, 8 };
            byte[] traceBytes = { 13, 21, 34, 55 };
            Assert.IsTrue(SavedTestContract.TryReadDownload(
                DownloadJson(stateBytes, traceBytes),
                ProjectId,
                CatalogVersion(stateBytes, traceBytes),
                out var version,
                out var parseError), parseError);
            var traceArtifact = version.FindArtifact(TraceArtifactId);

            Assert.IsFalse(SavedTestContract.TryValidateArtifactBytes(
                traceArtifact,
                new byte[] { 13, 21 },
                out var shortError));
            StringAssert.Contains("2 bytes", shortError);

            Assert.IsFalse(SavedTestContract.TryValidateArtifactBytes(
                traceArtifact,
                new byte[] { 13, 21, 34, 54 },
                out var hashError));
            StringAssert.Contains("SHA-256", hashError);
            StringAssert.Contains("before changing the game", hashError);
        }

        [Test]
        public void ProviderFormatSceneAndInputMismatchesHaveExactReasons()
        {
            byte[] stateBytes = { 1 };
            byte[] traceBytes = { 2 };
            Assert.IsTrue(SavedTestContract.TryReadDownload(
                DownloadJson(stateBytes, traceBytes),
                ProjectId,
                CatalogVersion(stateBytes, traceBytes),
                out var version,
                out var error), error);

            var wrongProvider = SavedTestReplayCompatibility.Evaluate(
                version,
                RegistryWith(new Provider("another.provider")),
                new FakeEnvironment());
            StringAssert.Contains("another.provider", wrongProvider.Reason);
            StringAssert.Contains("qamel.fixture.state", wrongProvider.Reason);

            var wrongFormat = SavedTestReplayCompatibility.Evaluate(
                version,
                RegistryWith(new Provider(formatVersion: 7)),
                new FakeEnvironment());
            StringAssert.Contains("format v7", wrongFormat.Reason);
            StringAssert.Contains("requires v2", wrongFormat.Reason);

            var wrongScene = SavedTestReplayCompatibility.Evaluate(
                version,
                RegistryWith(new Provider()),
                new FakeEnvironment { ActiveScenePath = "Assets/Another.unity" });
            StringAssert.Contains("Assets/FixtureScene.unity", wrongScene.Reason);
            StringAssert.Contains("Assets/Another.unity", wrongScene.Reason);

            var unsavedScene = SavedTestReplayCompatibility.Evaluate(
                version,
                RegistryWith(new Provider()),
                new FakeEnvironment { ActiveScenePath = "" });
            StringAssert.Contains("in Edit Mode", unsavedScene.Reason);
            StringAssert.Contains("no saved asset path", unsavedScene.Reason);

            var wrongInputVersion = SavedTestReplayCompatibility.Evaluate(
                version,
                RegistryWith(new Provider()),
                new FakeEnvironment { InputSystemVersion = "1.7.0" });
            StringAssert.Contains("requires Input System 1.14.2", wrongInputVersion.Reason);
            StringAssert.Contains("1.7.0", wrongInputVersion.Reason);

            var wrongUnityRelease = SavedTestReplayCompatibility.Evaluate(
                version,
                RegistryWith(new Provider()),
                new FakeEnvironment { UnityRelease = "2023.2" });
            StringAssert.Contains("requires Unity 2022.3", wrongUnityRelease.Reason);
            StringAssert.Contains("2023.2", wrongUnityRelease.Reason);

            string disabledReason = NewInputTraceAdapter.ConfigurationUnavailableReasonFor(
                packageAvailable: true,
                inputSystemEnabled: false);
            var disabledInput = SavedTestReplayCompatibility.Evaluate(
                version,
                RegistryWith(new Provider()),
                new FakeEnvironment
                {
                    InputAvailable = false,
                    InputUnavailableReason = disabledReason,
                });
            StringAssert.Contains(disabledReason, disabledInput.Reason);
        }

        [Test]
        public void ActualTraceLayoutAndDeviceFailureStopsReplayPreflight()
        {
            byte[] stateBytes = { 1 };
            byte[] traceBytes = { 2 };
            Assert.IsTrue(SavedTestContract.TryReadDownload(
                DownloadJson(stateBytes, traceBytes),
                ProjectId,
                CatalogVersion(stateBytes, traceBytes),
                out var version,
                out var error), error);

            var layoutMismatch = SavedTestReplayCompatibility.ValidateDownloadedTrace(
                version,
                traceBytes,
                new FakeEnvironment { ActualLayouts = new[] { "Gamepad" } });
            Assert.IsFalse(layoutMismatch.IsCompatible);
            StringAssert.Contains("Gamepad", layoutMismatch.Reason);
            StringAssert.Contains("Keyboard, Mouse", layoutMismatch.Reason);

            var unmappable = SavedTestReplayCompatibility.ValidateDownloadedTrace(
                version,
                traceBytes,
                new FakeEnvironment
                {
                    TraceSupported = false,
                    TraceError = "No active input device maps recorded layout 'Keyboard'.",
                });
            Assert.IsFalse(unmappable.IsCompatible);
            StringAssert.Contains("No active input device", unmappable.Reason);
        }

        [Test]
        public void LoadedSavedReplayPreflightRechecksSceneAndInputBeforeEveryReplay()
        {
            byte[] stateBytes = { 1 };
            byte[] traceBytes = { 2 };
            Assert.IsTrue(SavedTestContract.TryReadDownload(
                DownloadJson(stateBytes, traceBytes),
                ProjectId,
                CatalogVersion(stateBytes, traceBytes),
                out var version,
                out var error), error);
            var registry = RegistryWith(new Provider());
            var environment = new FakeEnvironment();

            Assert.IsNull(TestLabSession.SavedReplayPreflightBlockReason(
                version,
                traceBytes,
                registry,
                environment));
            Assert.AreEqual(1, environment.TraceInspectionCount);

            environment.ActiveScenePath = "Assets/Another.unity";
            string sceneError = TestLabSession.SavedReplayPreflightBlockReason(
                version,
                traceBytes,
                registry,
                environment);
            StringAssert.Contains("Assets/FixtureScene.unity", sceneError);
            Assert.AreEqual(
                1,
                environment.TraceInspectionCount,
                "trace inspection should not run after metadata compatibility already failed");

            environment.ActiveScenePath = "Assets/FixtureScene.unity";
            environment.ActualLayouts = new[] { "Gamepad" };
            string inputError = TestLabSession.SavedReplayPreflightBlockReason(
                version,
                traceBytes,
                registry,
                environment);
            StringAssert.Contains("local input setup changed", inputError);
            StringAssert.Contains("Gamepad", inputError);
            Assert.AreEqual(2, environment.TraceInspectionCount);
        }

        [Test]
        public void UnityReleaseFamilyAllowsPatchDifferenceButNotMinorDifference()
        {
            Assert.IsTrue(SavedTestReplayCompatibility.SameUnityReleaseFamily(
                "2022.3.62f1",
                "2022.3.10f1"));
            Assert.IsTrue(SavedTestReplayCompatibility.SameUnityReleaseFamily(
                "6000.0.50f1",
                "6000.0.1f1"));
            Assert.IsFalse(SavedTestReplayCompatibility.SameUnityReleaseFamily(
                "2022.3.62f1",
                "2023.2.10f1"));
            Assert.IsFalse(SavedTestReplayCompatibility.SameUnityReleaseFamily(
                null,
                "2022.3.10f1"));
        }

        [Test]
        public void UnknownAdapterKeepsVersionVisibleButIncompatible()
        {
            byte[] stateBytes = { 1 };
            byte[] traceBytes = { 2 };
            string json = DownloadJson(stateBytes, traceBytes).Replace(
                "qamel.unity.replay_input_trace",
                "studio.unknown.action");
            Assert.IsTrue(SavedTestContract.TryReadDownload(
                json,
                ProjectId,
                CatalogVersion(stateBytes, traceBytes),
                out var version,
                out var error), error);

            var compatibility = SavedTestReplayCompatibility.Evaluate(
                version,
                RegistryWith(new Provider()),
                new FakeEnvironment());

            Assert.IsFalse(compatibility.IsCompatible);
            StringAssert.Contains("studio.unknown.action", compatibility.Reason);
            StringAssert.Contains("qamel.unity.replay_input_trace", compatibility.Reason);
        }

        [Test]
        public void CurrentUnityEnvironmentResolvesInstalledReplayComponents()
        {
            var environment = new SavedTestUnityEnvironment();

            Assert.IsFalse(string.IsNullOrWhiteSpace(environment.UnityRelease));
            if (NewInputTraceAdapter.IsSupportedByCurrentConfiguration)
            {
                Assert.IsTrue(environment.InputAvailable, environment.InputUnavailableReason);
                Assert.IsFalse(string.IsNullOrWhiteSpace(environment.InputSystemVersion));
            }
            else
            {
                Assert.IsFalse(environment.InputAvailable);
                Assert.AreEqual(
                    NewInputTraceAdapter.CurrentConfigurationUnavailableReason,
                    environment.InputUnavailableReason);
            }
        }

        [Test]
        public void CurrentUnityEnvironmentInspectsAndMapsAnActualSerializedInputTrace()
        {
            Keyboard keyboard = null;
            try
            {
                keyboard = InputSystem.AddDevice<Keyboard>();
                using (var trace = new InputEventTrace())
                {
                    trace.recordFrameMarkers = true;
                    trace.Enable();
                    InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Space));
                    InputSystem.Update();
                    InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                    InputSystem.Update();
                    trace.Disable();
                    var timings = new List<RecordedInputFrames.Timing>();
                    foreach (var evt in trace)
                        if (evt.type == InputEventTrace.FrameMarkerEvent)
                            timings.Add(new RecordedInputFrames.Timing
                            {
                                DeltaTime = 1f / 60f, TimeScale = 1, FixedDeltaTime = 0.02f
                            });
                    using (var stream = new MemoryStream())
                    {
                        trace.WriteTo(stream);
                        var native = stream.ToArray();
                        foreach (var bytes in new[] { native, RecordedInputFrames.Write(native, timings) })
                        {
                            var environment = new SavedTestUnityEnvironment();
                            Assert.IsTrue(environment.TryInspectAndMapInputTrace(bytes,
                                out var layouts, out var error), error);
                            CollectionAssert.Contains(layouts, "Keyboard");
                        }
                    }
                }
            }
            finally
            {
                if (keyboard != null && keyboard.added)
                    InputSystem.RemoveDevice(keyboard);
            }
        }

        [Test]
        public void InputConfigurationExplainsMissingPackageAndOldManagerOnlyMode()
        {
            StringAssert.Contains(
                "Input System package",
                NewInputTraceAdapter.ConfigurationUnavailableReasonFor(
                    packageAvailable: false,
                    inputSystemEnabled: false));
            string oldManagerOnly =
                NewInputTraceAdapter.ConfigurationUnavailableReasonFor(
                    packageAvailable: true,
                    inputSystemEnabled: false);
            StringAssert.Contains("Active Input Handling", oldManagerOnly);
            StringAssert.Contains("Input System Package (New) or Both", oldManagerOnly);
            Assert.IsNull(NewInputTraceAdapter.ConfigurationUnavailableReasonFor(
                packageAvailable: true,
                inputSystemEnabled: true));
        }

        [Test]
        public void NetworkOperationsCanCancelWithoutKeepingPartialState()
        {
            using (var catalog = new SavedTestCatalogOperation(
                       "http://localhost:3000",
                       EditorKey,
                       ProjectId,
                       startImmediately: false))
            {
                Assert.AreEqual(
                    TestLabPreferences.ComputeAuthoringFingerprint(
                        "http://localhost:3000",
                        EditorKey),
                    catalog.ConnectionFingerprint);
                catalog.Cancel();
                Assert.AreEqual(SavedTestCatalogState.Cancelled, catalog.State);
                Assert.IsNull(catalog.Catalog);
            }

            var selectedVersion = CatalogVersion(new byte[] { 1 }, new byte[] { 2 });
            using (var replay = new SavedTestReplayOperation(
                       "http://localhost:3000",
                       EditorKey,
                       ProjectId,
                       selectedVersion,
                       RegistryWith(new Provider()),
                       new FakeEnvironment(),
                       startImmediately: false))
            {
                Assert.AreEqual(
                    TestLabPreferences.ComputeAuthoringFingerprint(
                        "http://localhost:3000",
                        EditorKey),
                    replay.ConnectionFingerprint);
                Assert.AreEqual(ProjectId, replay.ExpectedProjectId);
                Assert.AreSame(selectedVersion, replay.SelectedVersion);
                replay.Cancel();
                Assert.AreEqual(SavedTestReplayDownloadState.Cancelled, replay.State);
                Assert.IsNull(replay.Version);
                Assert.IsNull(replay.ReadyDraft);
                StringAssert.Contains("before gameplay changed", replay.Status);
            }
        }

        [Test]
        public void BuildsScopedListAndVersionDownloadRoutes()
        {
            Assert.AreEqual(
                "https://qamel.ai/api/v1/tests",
                TestDefinitionRoutes.TestsUrl("https://qamel.ai/"));
            Assert.IsTrue(TestDefinitionRoutes.TryGetVersionDownloadUrl(
                "https://qamel.ai",
                VersionId,
                out var url));
            Assert.AreEqual(
                "https://qamel.ai/api/v1/test-versions/" + VersionId + "/download",
                url);
            Assert.IsTrue(TestDefinitionRoutes.IsValidArtifactDownloadUrl(
                "https://storage.example/object?token=secret"));
            Assert.IsFalse(TestDefinitionRoutes.IsValidArtifactDownloadUrl(
                "file:///tmp/trace.bin"));
        }

        static TestStateRegistry RegistryWith(IQamelTestStateProvider provider)
        {
            var registry = new TestStateRegistry();
            Assert.IsTrue(registry.Register(provider).Succeeded);
            return registry;
        }

        static SavedTestVersion CatalogVersion(byte[] stateBytes, byte[] traceBytes)
        {
            Assert.IsTrue(SavedTestContract.TryReadCatalog(
                CatalogJson(stateBytes, traceBytes, 2, true),
                out var catalog,
                out var error), error);
            return catalog.Tests[0].Versions[0];
        }

        static void AssertDownloadRejected(
            string json,
            SavedTestVersion selectedVersion,
            string expectedError)
        {
            Assert.IsFalse(SavedTestContract.TryReadDownload(
                json,
                ProjectId,
                selectedVersion,
                out var version,
                out var error));
            Assert.IsNull(version);
            StringAssert.Contains(expectedError, error);
        }

        static string CatalogJson(
            byte[] stateBytes,
            byte[] traceBytes,
            int contractVersion,
            bool downloadReady,
            bool automaticCheck = false)
        {
            return "{" +
                   "\"contractVersion\":1," +
                   "\"projectId\":\"" + ProjectId + "\"," +
                   "\"tests\":[{" +
                   "\"testId\":\"" + TestId + "\"," +
                   "\"name\":\"Reach the exit\"," +
                   "\"status\":\"validating\"," +
                   "\"currentVersionId\":\"" + VersionId + "\"," +
                   "\"updatedAt\":\"2026-09-04T00:00:00Z\"," +
                   "\"versions\":[" + VersionJson(
                       stateBytes,
                       traceBytes,
                       contractVersion,
                       downloadReady,
                       includeDownloadUrls: false,
                       automaticCheck: automaticCheck) + "]}]}";
        }

        static string DownloadJson(
            byte[] stateBytes,
            byte[] traceBytes,
            int contractVersion = 2,
            bool automaticCheck = false)
        {
            return "{" +
                   "\"contractVersion\":1," +
                   "\"projectId\":\"" + ProjectId + "\"," +
                   "\"test\":{" +
                   "\"testId\":\"" + TestId + "\"," +
                   "\"name\":\"Reach the exit\"," +
                   "\"status\":\"validating\"}," +
                   "\"version\":" + VersionJson(
                       stateBytes,
                       traceBytes,
                       contractVersion,
                       downloadReady: true,
                       includeDownloadUrls: true,
                       automaticCheck: automaticCheck) + "}";
        }

        static string VersionJson(
            byte[] stateBytes,
            byte[] traceBytes,
            int contractVersion,
            bool downloadReady,
            bool includeDownloadUrls,
            bool automaticCheck = false)
        {
            string stateDownload = includeDownloadUrls
                ? ",\"downloadUrl\":\"https://storage.example/state?token=one\"," +
                  "\"downloadExpiresAt\":\"2026-09-04T00:05:00Z\""
                : "";
            string traceDownload = includeDownloadUrls
                ? ",\"downloadUrl\":\"https://storage.example/trace?token=two\"," +
                  "\"downloadExpiresAt\":\"2026-09-04T00:05:00Z\""
                : "";
            string checkStage = automaticCheck
                ? "{\"stageId\":\"" + CheckStageId + "\"," +
                  "\"position\":3,\"role\":\"check\"," +
                  "\"executor\":\"scripted\"," +
                  "\"adapter\":\"qamel.unity.compare_outcome_observation\"," +
                  "\"configuration\":{" +
                  "\"expectedOutcome\":\"The player reaches the exit.\"," +
                  "\"providerId\":\"qamel.fixture.state\"," +
                  "\"outcomeFormatVersion\":1," +
                  "\"expectedValue\":\"exit-reached\"," +
                  "\"expectedSummary\":\"Player reached exit\"}}"
                : "{\"stageId\":\"" + CheckStageId + "\"," +
                  "\"position\":3,\"role\":\"check\"," +
                  "\"executor\":\"human\"," +
                  "\"adapter\":\"qamel.human.review_expected_outcome\"," +
                  "\"configuration\":{" +
                  "\"expectedOutcome\":\"The player reaches the exit.\"}}";
            return "{" +
                   "\"testVersionId\":\"" + VersionId + "\"," +
                   "\"versionNumber\":3," +
                   "\"contractVersion\":" + contractVersion + "," +
                   "\"definitionSha256\":\"" + new string('a', 64) + "\"," +
                   "\"createdAt\":\"2026-09-04T00:00:00Z\"," +
                   "\"downloadReady\":" + (downloadReady ? "true" : "false") + "," +
                   "\"startingState\":{" +
                   "\"providerId\":\"qamel.fixture.state\"," +
                   "\"formatVersion\":2," +
                   "\"label\":\"Fixture start\"," +
                   "\"capturedAt\":\"2026-09-04T00:00:00Z\"," +
                   "\"activeScenePath\":\"Assets/FixtureScene.unity\"," +
                   "\"payloadArtifactId\":\"" + StateArtifactId + "\"}," +
                   "\"stages\":[" +
                   "{\"stageId\":\"" + StateStageId + "\"," +
                   "\"position\":1,\"role\":\"setup\"," +
                   "\"executor\":\"scripted\"," +
                   "\"adapter\":\"qamel.unity.restore_starting_state\"," +
                   "\"configuration\":{}}," +
                   "{\"stageId\":\"" + ActionStageId + "\"," +
                   "\"position\":2,\"role\":\"action\"," +
                   "\"executor\":\"scripted\"," +
                   "\"adapter\":\"qamel.unity.replay_input_trace\"," +
                   "\"artifactId\":\"" + TraceArtifactId + "\"," +
                   "\"configuration\":{" +
                   "\"replayableInputEventCount\":9," +
                   "\"recordedDurationSeconds\":2.5," +
                   "\"unityRelease\":\"2022.3\"," +
                   "\"inputSystemVersion\":\"1.14.2\"," +
                   "\"deviceLayouts\":[\"Keyboard\",\"Mouse\"]}}," +
                   checkStage + "]," +
                   "\"artifacts\":[" +
                   "{\"artifactId\":\"" + StateArtifactId + "\"," +
                   "\"clientRef\":\"state-payload\"," +
                   "\"kind\":\"state_payload\"," +
                   "\"byteCount\":" + stateBytes.Length + "," +
                   "\"sha256\":\"" + Sha256(stateBytes) + "\"," +
                   "\"uploadStatus\":\"uploaded\"" + stateDownload + "}," +
                   "{\"artifactId\":\"" + TraceArtifactId + "\"," +
                   "\"clientRef\":\"input-trace\"," +
                   "\"kind\":\"input_trace\"," +
                   "\"byteCount\":" + traceBytes.Length + "," +
                   "\"sha256\":\"" + Sha256(traceBytes) + "\"," +
                   "\"uploadStatus\":\"uploaded\"" + traceDownload + "}]}";
        }

        static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(bytes);
                var hex = new StringBuilder(digest.Length * 2);
                foreach (byte value in digest) hex.Append(value.ToString("x2"));
                return hex.ToString();
            }
        }
    }
}
