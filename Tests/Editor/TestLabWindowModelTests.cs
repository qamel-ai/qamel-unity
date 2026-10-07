using NUnit.Framework;
using QamelCapture.Editor.TestAuthoring;
using QamelCapture.TestAuthoring;
using UnityEngine;

namespace QamelCapture.Tests
{
    public sealed class TestLabWindowModelTests
    {
        sealed class Provider : IQamelTestStateProvider
        {
            public bool HoldRestore { get; set; }
            public TestStateRestoreContext PendingRestore { get; private set; }
            public string ProviderId => "fixture.test-lab";
            public int StateFormatVersion => 1;

            public void CaptureState(TestStateCaptureContext context)
            {
                context.Succeed("Fixture start", new byte[] { 1, 2, 3 });
            }

            public void RestoreState(TestStateRestoreContext context)
            {
                if (HoldRestore)
                {
                    PendingRestore = context;
                    return;
                }
                context.ReadyForReplay();
            }
        }

        sealed class TraceAdapter : ITestInputTraceAdapter
        {
            readonly TestInputTraceMetrics _metrics = new TestInputTraceMetrics(
                4, 4, 2, 128, 512, 1024, true, 10, 12,
                new[] { "Keyboard", "Mouse" });

            public bool IsAvailable => true;
            public string UnavailableReason => null;
            public TestInputTraceStatus Status { get; private set; } = TestInputTraceStatus.Idle;
            public TestInputTraceMetrics Metrics => _metrics;
            public TestInputReplayStatus ReplayStatus { get; private set; } =
                TestInputReplayStatus.Idle;
            public TestInputReplayDiagnostics ReplayDiagnostics =>
                TestInputReplayDiagnostics.Empty();
            public TestInputTraceErrorCode ReplayErrorCode => TestInputTraceErrorCode.None;
            public string ReplayError => null;

            public TestInputTraceResult StartCapture()
            {
                Status = TestInputTraceStatus.Capturing;
                return TestInputTraceResult.Success();
            }

            public TestInputTraceResult PauseCapture()
            {
                Status = TestInputTraceStatus.Paused;
                return TestInputTraceResult.Success();
            }

            public TestInputTraceResult ResumeCapture()
            {
                Status = TestInputTraceStatus.Capturing;
                return TestInputTraceResult.Success();
            }

            public TestInputTraceSnapshotResult PauseAndSnapshot()
            {
                PauseCapture();
                return TestInputTraceSnapshotResult.Success(
                    new TestInputTraceSnapshot(new byte[] { 5, 8, 13 }, _metrics));
            }

            public void StopCapture()
            {
                Status = TestInputTraceStatus.Idle;
            }

            public TestInputTraceResult StartReplay(TestInputTraceSnapshot snapshot)
            {
                ReplayStatus = TestInputReplayStatus.Replaying;
                return TestInputTraceResult.Success();
            }

            public bool CancelReplay()
            {
                ReplayStatus = TestInputReplayStatus.Cancelled;
                return true;
            }

            public void CompleteReplay()
            {
                ReplayStatus = TestInputReplayStatus.Completed;
            }

            public void Dispose()
            {
                Status = TestInputTraceStatus.Disposed;
                ReplayStatus = TestInputReplayStatus.Disposed;
            }
        }

        [Test]
        public void EveryUnavailablePrimaryActionExplainsItsPrerequisite()
        {
            var editMode = TestLabWindowModel.Create(
                false, true, null, false, null, "", "");
            Assert.AreEqual(TestLabPrimaryAction.EnterPlayMode, editMode.PrimaryAction);
            Assert.IsTrue(editMode.PrimaryEnabled);

            var unsupported = TestLabWindowModel.Create(
                true, false, "Input System is disabled.", false, null, "", "");
            Assert.AreEqual(TestLabPrimaryAction.CaptureState, unsupported.PrimaryAction);
            Assert.IsFalse(unsupported.PrimaryEnabled);
            StringAssert.Contains("Input System", unsupported.DisabledReason);

            var missingProvider = TestLabWindowModel.Create(
                true, true, null, false, null, "", "");
            Assert.AreEqual(TestLabPrimaryAction.CaptureState, missingProvider.PrimaryAction);
            Assert.IsFalse(missingProvider.PrimaryEnabled);
            StringAssert.Contains("IQamelTestStateProvider", missingProvider.DisabledReason);
        }

        [Test]
        public void RecordingFormRequiresNameAndExpectedOutcomeThenShowsFixedDraft()
        {
            var registry = new TestStateRegistry();
            var provider = new Provider();
            Assert.IsTrue(registry.Register(provider).Succeeded);
            using (var controller = new TestAuthoringController(registry, new TraceAdapter()))
            {
                controller.CaptureStartingState(1);

                var readyToDemonstrate = TestLabWindowModel.Create(
                    true, true, null, true, controller, "", "");
                Assert.AreEqual("Ready to demonstrate", readyToDemonstrate.Title);
                Assert.AreEqual(TestLabPrimaryAction.None, readyToDemonstrate.PrimaryAction);
                Assert.IsFalse(readyToDemonstrate.PrimaryEnabled);
                StringAssert.Contains("Game view", readyToDemonstrate.Guidance);

                provider.HoldRestore = true;
                Assert.IsTrue(controller.StartDemonstration(1), controller.LastError);
                var preparing = TestLabWindowModel.Create(
                    true, true, null, true, controller, "", "");
                Assert.AreEqual("Preparing starting state", preparing.Title);
                Assert.AreEqual(TestLabPrimaryAction.None, preparing.PrimaryAction);
                Assert.IsFalse(preparing.PrimaryEnabled);
                Assert.IsFalse(controller.IsBackgroundRecording);

                Assert.IsTrue(provider.PendingRestore.ReadyForReplay());
                controller.Tick(0);

                var noName = TestLabWindowModel.Create(
                    true, true, null, true, controller, "", "Door opens");
                Assert.AreEqual(TestLabPrimaryAction.CreateDraft, noName.PrimaryAction);
                Assert.IsTrue(noName.ShowDraftForm);
                Assert.IsFalse(noName.PrimaryEnabled);
                StringAssert.Contains("name", noName.DisabledReason);

                var noOutcome = TestLabWindowModel.Create(
                    true, true, null, true, controller, "Open door", "");
                Assert.IsFalse(noOutcome.PrimaryEnabled);
                StringAssert.Contains("true", noOutcome.DisabledReason);

                var ready = TestLabWindowModel.Create(
                    true, true, null, true, controller,
                    "Open door", "The door opens");
                Assert.IsTrue(ready.PrimaryEnabled);

                Assert.IsTrue(controller.TryCreateDraft(
                    "Open door", "The door opens", out var draft), controller.LastError);
                Assert.AreEqual("Open door", draft.Name);
                Assert.AreEqual("The door opens", draft.ExpectedOutcome);

                var draftReady = TestLabWindowModel.Create(
                    true, true, null, true, controller, "", "");
                Assert.AreEqual(TestLabPrimaryAction.Replay, draftReady.PrimaryAction);
                Assert.AreEqual("Replay draft locally", draftReady.PrimaryLabel);
                Assert.IsTrue(draftReady.PrimaryEnabled);
                Assert.IsTrue(draftReady.ShowDraft);
                Assert.IsTrue(draftReady.CanClearDraft);

                Assert.IsTrue(controller.ClearDraft());
                Assert.AreEqual(TestAuthoringControllerState.Recording, controller.State);
                Assert.IsNull(controller.CurrentDraft);
            }
        }

        [Test]
        public void FocusPolicyAllowsOnlyGameViewAndNeverTestLabInteraction()
        {
            Assert.IsTrue(TestLabInputFocusPolicy.Evaluate(true, true, true, "TestLabWindow"));
            Assert.IsTrue(TestLabInputFocusPolicy.Evaluate(false, false, false, "GameView"));
            Assert.IsFalse(TestLabInputFocusPolicy.Evaluate(false, true, false, "GameView"));
            Assert.IsFalse(TestLabInputFocusPolicy.Evaluate(false, false, true, "GameView"));
            Assert.IsFalse(TestLabInputFocusPolicy.Evaluate(false, false, false, "InspectorWindow"));
            Assert.IsFalse(TestLabInputFocusPolicy.Evaluate(false, false, false, null));
        }

        [Test]
        public void DemonstrationStartOverlayCanBeHostedByATransientGameObject()
        {
            var overlayObject = new GameObject("Qamel overlay test");
            try
            {
                var overlay = overlayObject.AddComponent<TestLabDemonstrationStartOverlay>();
                Assert.NotNull(overlay);
                overlay.Present();
                Assert.IsFalse(overlay.StartRequested);
                overlay.Dismiss();
            }
            finally
            {
                Object.DestroyImmediate(overlayObject);
            }
        }

        [Test]
        public void FocusPolicyRequestsFocusWhenDemonstrationOrReplayIsReadyInPlayMode()
        {
            Assert.IsTrue(TestLabInputFocusPolicy.ShouldFocusGameView(
                TestAuthoringControllerState.DraftReady,
                TestAuthoringControllerState.Restoring,
                true));
            Assert.IsTrue(TestLabInputFocusPolicy.ShouldFocusGameView(
                TestAuthoringControllerState.CapturingStartingState,
                TestAuthoringControllerState.ReadyForDemonstration,
                true));
            Assert.IsTrue(TestLabInputFocusPolicy.ShouldFocusGameView(
                TestAuthoringControllerState.Settling,
                TestAuthoringControllerState.ReplayCountdown,
                true));
            Assert.IsTrue(TestLabInputFocusPolicy.ShouldFocusGameView(
                TestAuthoringControllerState.Settling,
                TestAuthoringControllerState.ReplayRunning,
                true));
            Assert.IsFalse(TestLabInputFocusPolicy.ShouldFocusGameView(
                TestAuthoringControllerState.ReplayCountdown,
                TestAuthoringControllerState.ReplayCountdown,
                true));
            Assert.IsFalse(TestLabInputFocusPolicy.ShouldFocusGameView(
                TestAuthoringControllerState.Settling,
                TestAuthoringControllerState.ReplayCountdown,
                false));
            Assert.IsFalse(TestLabInputFocusPolicy.ShouldFocusGameView(
                TestAuthoringControllerState.Restoring,
                TestAuthoringControllerState.Settling,
                true));
            Assert.IsFalse(TestLabInputFocusPolicy.ShouldFocusGameView(
                TestAuthoringControllerState.ReadyForDemonstration,
                TestAuthoringControllerState.Recording,
                true));
        }

        [Test]
        public void SimulationPausesOnlyAcrossUnrecordedAuthoringAndCountdownTime()
        {
            Assert.IsTrue(TestLabInputFocusPolicy.ShouldPauseSimulation(
                TestAuthoringControllerState.ReadyForDemonstration,
                isPlaying: true,
                isBatchMode: false,
                isCapturingGameplayInput: true));
            Assert.IsTrue(TestLabInputFocusPolicy.ShouldPauseSimulation(
                TestAuthoringControllerState.PreparingDemonstration,
                isPlaying: true,
                isBatchMode: false,
                isCapturingGameplayInput: true));
            Assert.IsTrue(TestLabInputFocusPolicy.ShouldPauseSimulation(
                TestAuthoringControllerState.Recording,
                isPlaying: true,
                isBatchMode: false,
                isCapturingGameplayInput: false));
            Assert.IsFalse(TestLabInputFocusPolicy.ShouldPauseSimulation(
                TestAuthoringControllerState.Recording,
                isPlaying: true,
                isBatchMode: false,
                isCapturingGameplayInput: true));
            Assert.IsTrue(TestLabInputFocusPolicy.ShouldPauseSimulation(
                TestAuthoringControllerState.ReplayCountdown,
                isPlaying: true,
                isBatchMode: false,
                isCapturingGameplayInput: true));
            Assert.IsFalse(TestLabInputFocusPolicy.ShouldPauseSimulation(
                TestAuthoringControllerState.ReplayRunning,
                isPlaying: true,
                isBatchMode: false,
                isCapturingGameplayInput: false));
            Assert.IsTrue(TestLabInputFocusPolicy.ShouldPauseSimulation(
                TestAuthoringControllerState.ReplayCountdown,
                isPlaying: true,
                isBatchMode: true,
                isCapturingGameplayInput: false));
            foreach (var state in new[] { TestAuthoringControllerState.Restoring, TestAuthoringControllerState.Settling })
                Assert.IsTrue(TestLabInputFocusPolicy.ShouldPauseSimulation(state, true, false, true));
            Assert.IsFalse(TestLabInputFocusPolicy.ShouldPauseSimulation(
                TestAuthoringControllerState.Recording,
                isPlaying: false,
                isBatchMode: false,
                isCapturingGameplayInput: false));
        }

        [Test]
        public void SimulationPauseRestoresTheGamesExistingTimeAndAudioState()
        {
            var originalTimeScale = Time.timeScale;
            var originalAudioPause = AudioListener.pause;
            var pause = new TestLabSimulationPause();

            try
            {
                Time.timeScale = 0.375f;
                AudioListener.pause = false;

                pause.Sync(true);
                Assert.IsTrue(pause.IsPaused);
                Assert.AreEqual(0f, Time.timeScale);
                Assert.IsTrue(AudioListener.pause);

                pause.Sync(false);
                Assert.IsFalse(pause.IsPaused);
                Assert.AreEqual(0.375f, Time.timeScale);
                Assert.IsFalse(AudioListener.pause);
            }
            finally
            {
                pause.Release();
                Time.timeScale = originalTimeScale;
                AudioListener.pause = originalAudioPause;
            }
        }

        [Test]
        public void AuthoringFingerprintIsStableAndSensitiveToCredentialChanges()
        {
            string first = TestLabPreferences.ComputeAuthoringFingerprint(
                "https://qamel.ai/",
                "qa_key_fixture");
            string normalized = TestLabPreferences.ComputeAuthoringFingerprint(
                "https://qamel.ai",
                " qa_key_fixture ");
            string changed = TestLabPreferences.ComputeAuthoringFingerprint(
                "https://qamel.ai",
                "qa_key_other");

            Assert.AreEqual(64, first.Length);
            Assert.AreEqual(first, normalized);
            Assert.AreNotEqual(first, changed);
        }

        [Test]
        public void CheckedConnectionSurvivesOnlyForTheExactEndpointAndKey()
        {
            const string projectId = "018f0000-0000-7000-8000-000000000001";
            string fingerprint = TestLabPreferences.ComputeAuthoringFingerprint(
                "https://qamel.ai",
                "qa_key_fixture");
            try
            {
                TestLabPreferences.ClearCheckedConnection();
                TestLabPreferences.RememberCheckedConnection(
                    fingerprint,
                    projectId,
                    "Fixture Project");

                Assert.IsTrue(TestLabPreferences.TryGetCheckedConnection(
                    fingerprint,
                    out var restoredProjectId,
                    out var restoredProjectName));
                Assert.AreEqual(projectId, restoredProjectId);
                Assert.AreEqual("Fixture Project", restoredProjectName);
                Assert.IsFalse(TestLabPreferences.TryGetCheckedConnection(
                    TestLabPreferences.ComputeAuthoringFingerprint(
                        "https://qamel.ai",
                        "qa_key_rotated"),
                    out _,
                    out _));
                Assert.IsFalse(TestLabPreferences.TryGetCheckedConnection(
                    TestLabPreferences.ComputeAuthoringFingerprint(
                        "http://localhost:3000",
                        "qa_key_fixture"),
                    out _,
                    out _));
            }
            finally
            {
                TestLabPreferences.ClearCheckedConnection();
            }
        }

        [Test]
        public void LastSavedTestSurvivesKeyRotationButNotProjectOrEndpointChanges()
        {
            const string projectId = "018f0000-0000-7000-8000-000000000001";
            const string testId = "018f0000-0000-7000-8000-000000000002";
            const string testVersionId = "018f0000-0000-7000-8000-000000000003";
            string originalEndpoint = TestLabPreferences.AuthoringEndpoint;
            try
            {
                TestLabPreferences.ClearLastSavedTest();
                TestLabPreferences.AuthoringEndpoint = "https://qamel.ai";
                TestLabPreferences.RememberLastSavedTest(
                    new TestLabSavedTestReceipt(
                        projectId,
                        "Fixture Project",
                        testId,
                        testVersionId,
                        "Open door",
                        2,
                        "https://qamel.ai/tests/" + testId));

                var restored = TestLabPreferences.LastSavedTestForProject(
                    "{018F0000-0000-7000-8000-000000000001}");
                Assert.NotNull(restored);
                Assert.AreEqual("Open door", restored.TestName);
                Assert.AreEqual(2, restored.VersionNumber);
                Assert.AreEqual("https://qamel.ai/tests/" + testId, restored.TestUrl);
                Assert.IsNull(TestLabPreferences.LastSavedTestForProject(
                    "018f0000-0000-7000-8000-000000000099"));

                TestLabPreferences.AuthoringEndpoint = "http://localhost:3000";
                Assert.IsNull(TestLabPreferences.LastSavedTestForProject(projectId));
            }
            finally
            {
                TestLabPreferences.ClearLastSavedTest();
                TestLabPreferences.AuthoringEndpoint = originalEndpoint;
            }
        }

        [Test]
        public void ReplayStatesExposeCancelThenHumanReviewAndRepeatedReplay()
        {
            var registry = new TestStateRegistry();
            Assert.IsTrue(registry.Register(new Provider()).Succeeded);
            var adapter = new TraceAdapter();
            var frame = 10;
            using (var controller = new TestAuthoringController(
                       registry, adapter, frameCounter: () => frame))
            {
                controller.CaptureStartingState(1);
                Assert.IsTrue(controller.StartDemonstration(1), controller.LastError);
                Assert.IsTrue(controller.TryCreateDraft(
                    "Open door", "The door opens", out var draft));
                Assert.IsTrue(controller.StartReplay(1, 5, 0));

                var settling = TestLabWindowModel.Create(
                    true, true, null, true, controller, "", "");
                Assert.AreEqual(TestLabPrimaryAction.CancelRun, settling.PrimaryAction);
                Assert.IsTrue(settling.PrimaryEnabled);
                Assert.IsTrue(settling.ShowRun);

                frame++;
                controller.Tick(0);
                Assert.AreEqual(TestAuthoringControllerState.ReplayRunning, controller.State);
                adapter.CompleteReplay();
                controller.Tick(0.25f);

                var review = TestLabWindowModel.Create(
                    true, true, null, true, controller, "", "");
                Assert.IsTrue(review.ShowReview);
                Assert.IsTrue(review.ShowDraft);
                Assert.IsFalse(review.PrimaryEnabled);
                Assert.AreEqual(LocalTestRunStatus.AwaitingReview, controller.CurrentRun.Status);

                Assert.IsTrue(controller.RecordHumanVerdict(
                    TestHumanVerdict.CouldNotTell, "Needs another look."));
                var readyAgain = TestLabWindowModel.Create(
                    true, true, null, true, controller, "", "");
                Assert.AreEqual(TestLabPrimaryAction.Replay, readyAgain.PrimaryAction);
                Assert.AreEqual("Replay draft locally again", readyAgain.PrimaryLabel);
                Assert.IsTrue(readyAgain.PrimaryEnabled);
                Assert.AreEqual(1, draft.ValidationHistory.Count);
                Assert.AreEqual(LocalTestRunStatus.NeedsReview,
                    draft.ValidationHistory[0].Status);
            }
        }

        [Test]
        public void DurableSaveRequiresConfigurationAndTracksConnectionCheckAndOperationState()
        {
            var disconnected = TestLabCloudSaveModel.CreateForState(
                false,
                "draft-a",
                false,
                null,
                TestDefinitionSaveState.Cancelled,
                null);
            Assert.IsFalse(disconnected.CanSave);
            StringAssert.Contains("connection", disconnected.DisabledReason);

            var ready = TestLabCloudSaveModel.CreateForState(
                true,
                "draft-a",
                false,
                null,
                TestDefinitionSaveState.Cancelled,
                null);
            Assert.IsTrue(ready.CanSave);
            Assert.IsFalse(ready.ShowCancel);
            Assert.AreEqual("Save to Qamel", ready.ButtonLabel);

            var checking = TestLabCloudSaveModel.CreateForState(
                true,
                "draft-a",
                false,
                null,
                TestDefinitionSaveState.Cancelled,
                null,
                connectionCheckPending: true);
            Assert.IsFalse(checking.CanSave);
            Assert.IsFalse(checking.ShowCancel);
            StringAssert.Contains("Checking", checking.DisabledReason);

            var validating = TestLabCloudSaveModel.CreateForState(
                true,
                "draft-a",
                false,
                null,
                TestDefinitionSaveState.Cancelled,
                null,
                localValidationBusy: true);
            Assert.IsFalse(validating.CanSave);
            StringAssert.Contains("review", validating.DisabledReason);

            var uploading = TestLabCloudSaveModel.CreateForState(
                true,
                "draft-a",
                true,
                "draft-a",
                TestDefinitionSaveState.Uploading,
                null);
            Assert.IsFalse(uploading.CanSave);
            Assert.IsTrue(uploading.ShowCancel);
            Assert.IsTrue(uploading.ShowOperationStatus);

            var confirming = TestLabCloudSaveModel.CreateForState(
                true,
                "draft-a",
                true,
                "draft-a",
                TestDefinitionSaveState.Confirming,
                null);
            Assert.IsFalse(confirming.CanSave);
            Assert.IsTrue(confirming.ShowCancel);
            Assert.IsTrue(confirming.ShowOperationStatus);

            var failed = TestLabCloudSaveModel.CreateForState(
                true,
                "draft-a",
                true,
                "draft-a",
                TestDefinitionSaveState.Failed,
                null);
            Assert.IsTrue(failed.CanSave);
            Assert.IsFalse(failed.ShowCancel);
            Assert.IsFalse(failed.ShowOpen);

            var succeeded = TestLabCloudSaveModel.CreateForState(
                true,
                "draft-a",
                true,
                "draft-a",
                TestDefinitionSaveState.Succeeded,
                "https://qamel.ai/tests/018f0000-0000-7000-8000-000000000001");
            Assert.IsFalse(succeeded.CanSave);
            Assert.IsTrue(succeeded.ShowOpen);
            Assert.AreEqual("Saved to Qamel", succeeded.ButtonLabel);
            StringAssert.Contains("not uploaded", succeeded.DisabledReason);
            Assert.AreEqual(
                "https://qamel.ai/tests/018f0000-0000-7000-8000-000000000001",
                succeeded.OpenUrl);
        }

        [Test]
        public void SaveInProgressForAnotherDraftStillBlocksAndCanBeCancelled()
        {
            var model = TestLabCloudSaveModel.CreateForState(
                true,
                "draft-b",
                true,
                "draft-a",
                TestDefinitionSaveState.Registering,
                null);

            Assert.IsFalse(model.CanSave);
            Assert.IsTrue(model.ShowCancel);
            Assert.IsTrue(model.ShowOperationStatus);
            Assert.IsFalse(model.ShowOpen);
        }
    }
}
