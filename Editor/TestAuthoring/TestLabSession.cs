using System;
using System.Linq;
using QamelCapture.TestAuthoring;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace QamelCapture.Editor.TestAuthoring
{
    [InitializeOnLoad]
    internal static class TestLabSession
    {
        const float StateOperationTimeoutSeconds = 60;
        const float ReplayTimeoutSeconds = 120;
        const float ReplayCountdownSeconds = 3;
        const int DemonstrationInputSettleFrames = 2;
        const double RunnerPollIntervalSeconds = 3;
        const double RunnerRetryIntervalSeconds = 5;
        const double RunnerHeartbeatIntervalSeconds = 10;

        static NewInputTraceAdapter _inputTrace;
        static SceneReloadStateProvider _sceneReloadProvider;
        static TestAuthoringController _controller;
        static TestDefinitionSaveOperation _saveOperation;
        static TestDefinitionSaveOperation _rememberedSaveOperation;
        static SavedTestCatalogOperation _catalogOperation;
        static SavedTestReplayOperation _savedReplayOperation;
        static SavedTestReplayOperation _handledSavedReplayOperation;
        static TestRunnerLeaseOperation _runnerLeaseOperation;
        static TestRunHeartbeatOperation _runnerHeartbeatOperation;
        static SavedTestReplayOperation _runnerDownloadOperation;
        static TestRunResultUploadOperation _runnerResultOperation;
        static TestEvidenceUploadOperation _runnerEvidenceOperation;
        static TestEvidenceCapture _referenceEvidenceCapture;
        static TestEvidenceCapture _replayEvidenceCapture;
        static TestRunnerLease _runnerLease;
        static SavedTestVersion _runnerVersion;
        static LocalTestRun _runnerLocalRun;
        static string _runnerPendingResultJson;
        static bool _runnerEvidenceUploaded;
        static string _runnerConnectionFingerprint;
        static string _runnerStatus;
        static string _runnerError;
        static string _lastRunnerDiagnostic;
        static double _nextRunnerPollAt;
        static double _nextRunnerHeartbeatAt;
        static double _nextRunnerResultRetryAt;
        static TestCreationRequestOperation _testCreationOperation;
        static bool _testCreationOperationIsPoll;
        static ConnectedTestCreation _testCreation;
        static string _testCreationSessionId;
        static string _testCreationStatus;
        static string _testCreationError;
        static string _testCreationConnectionFingerprint;
        static double _nextTestCreationPollAt;
        static bool _testCreationOwnsLocalWork;
        static readonly SavedTestUnityEnvironment SavedReplayEnvironment =
            new SavedTestUnityEnvironment();
        static string _pendingSaveDraftId;
        static string _saveRequestStatus;
        static string _saveRequestError;
        static string _savedReplayRequestError;
        static string _loadedSavedTestId;
        static string _loadedSavedTestVersionId;
        static string _loadedSavedTestUrl;
        static int _loadedSavedVersionNumber;
        static LoadedSavedTestBinding _loadedSavedBinding;
        static SavedTestVersion _loadedSavedVersion;
        static byte[] _loadedSavedTraceBytes;
        static double _lastEditorTime;
        static TestAuthoringControllerState _lastObservedControllerState =
            TestAuthoringControllerState.Idle;
        static TestLabDemonstrationStartOverlay _demonstrationStartOverlay;
        static int _demonstrationStartNotBeforeFrame = -1;
        static readonly TestLabSimulationPause SimulationPause =
            new TestLabSimulationPause();

        static TestLabSession()
        {
            _saveRequestError = TestLabPreferences.ConsumeSaveInterruption();
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += Update;
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.quitting += OnQuitting;
        }

        public static TestStateRegistry Registry => TestStateRegistry.Global;
        public static TestAuthoringController Controller => _controller;
        public static bool HasController => _controller != null;
        public static bool InputAvailable => _inputTrace == null || _inputTrace.IsAvailable;
        public static string InputUnavailableReason => _inputTrace?.UnavailableReason;
        public static TestDefinitionSaveOperation SaveOperation => _saveOperation;
        public static bool IsSaveInProgress =>
            _saveOperation != null && !_saveOperation.IsFinished;
        public static bool IsSavePending => !string.IsNullOrWhiteSpace(_pendingSaveDraftId);
        public static bool IsSaveBusy => IsSaveInProgress || IsSavePending;
        public static string SaveRequestStatus => _saveRequestStatus;
        public static string SaveRequestError => _saveRequestError;
        public static SavedTestCatalogOperation CatalogOperation => _catalogOperation;
        public static SavedTestCatalog SavedTestCatalog =>
            _catalogOperation?.State == SavedTestCatalogState.Succeeded &&
            TestAuthoringHealthCheck.IsConnected &&
            string.Equals(
                _catalogOperation.ConnectionFingerprint,
                TestLabPreferences.AuthoringFingerprint,
                StringComparison.Ordinal) &&
            string.Equals(
                _catalogOperation.Catalog?.ProjectId,
                TestAuthoringHealthCheck.ProjectId,
                StringComparison.OrdinalIgnoreCase)
                ? _catalogOperation.Catalog
                : null;
        public static SavedTestReplayOperation SavedReplayOperation =>
            _savedReplayOperation;
        public static bool IsSavedReplayBusy =>
            _savedReplayOperation != null && !_savedReplayOperation.IsFinished;
        public static bool IsConnectedRunBusy => _runnerLease?.HasRun == true;
        public static bool IsBrowserTestCreationActive =>
            !string.IsNullOrWhiteSpace(_testCreationSessionId) ||
            (_testCreation != null && !_testCreation.IsTerminal);
        public static bool CanStopBrowserTestCreationLocally =>
            _testCreation != null &&
            IsBrowserTestCreationActive &&
            (_testCreation.Status == "preparing" ||
             _testCreation.Status == "recording" ||
             _testCreation.Status == "stop_requested");
        public static string BrowserTestCreationStatus => _testCreationStatus;
        public static string BrowserTestCreationError => _testCreationError;
        public static string ConnectedRunnerStatus => _runnerStatus;
        public static string ConnectedRunnerError => IsConnectedRunBusy
            ? _runnerError
            : ConnectedRunnerBlockedReason ?? _runnerError;
        internal static string ConnectedRunnerBlockedReason
        {
            get
            {
                if (!TestLabPreferences.IsEnabled) return "Enable Test Lab to connect this project.";
                if (!EditorApplication.isPlaying) return "The project is in Edit Mode.";
                if (!TestAuthoringHealthCheck.IsConnected)
                    return TestAuthoringHealthCheck.LastError ??
                           "Qamel connection: " + TestAuthoringHealthCheck.State + ".";
                if (_controller == null) return "The local replay controller has not initialized.";
                if (IsBrowserTestCreationActive)
                    return "Qamel is creating a recorded Test in this Unity project.";
                if (!InputAvailable) return InputUnavailableReason;
                if (string.IsNullOrWhiteSpace(SavedReplayEnvironment.ActiveScenePath))
                    return "The active scene has no saved asset path. Open the saved test scene in Edit Mode, then enter Play Mode.";
                if (IsSaveBusy) return "Waiting for the local Test save to finish.";
                if (IsSavedReplayBusy) return "Waiting for the local Test download to finish.";
                if (!LocalControllerCanYieldToRunner())
                    return "Finish and save or clear the local draft before accepting a Run.";
                return null;
            }
        }
        public static bool CurrentDraftIsDurable =>
            CurrentDraftIsSavedVersion ||
            (_controller?.CurrentDraft != null &&
             _saveOperation?.State == TestDefinitionSaveState.Succeeded &&
             string.Equals(
                 _saveOperation.DraftId,
                 _controller.CurrentDraft.DraftId,
                 StringComparison.Ordinal));
        public static bool IsConnectedRunnerReady =>
            !IsConnectedRunBusy && ConnectedRunnerBlockedReason == null;
        public static string SavedReplayRequestError => _savedReplayRequestError;
        public static bool CurrentDraftIsSavedVersion =>
            _controller?.CurrentDraft != null &&
            !string.IsNullOrWhiteSpace(_loadedSavedTestVersionId);
        public static string CurrentDraftReplayBlockedReason =>
            SavedReplayBindingBlockReason(
                CurrentDraftIsSavedVersion,
                LoadedSavedBindingIsCurrent());
        public static bool CanReplayCurrentDraft =>
            string.IsNullOrWhiteSpace(CurrentDraftReplayBlockedReason);
        public static string SavedReplayLocalStateBlockedReason =>
            SavedReplayLocalStateBlockReason(_controller);
        public static string LoadedSavedTestId => _loadedSavedTestId;
        public static string LoadedSavedTestVersionId => _loadedSavedTestVersionId;
        public static string LoadedSavedTestUrl => _loadedSavedTestUrl;
        public static int LoadedSavedVersionNumber => _loadedSavedVersionNumber;
        public static TestLabSavedTestReceipt LastSavedTest =>
            TestAuthoringHealthCheck.IsConnected
                ? TestLabPreferences.LastSavedTestForProject(
                    TestAuthoringHealthCheck.ProjectId)
                : null;

        public static void EnableExperimentalFeature()
        {
            bool wasEnabled = TestLabPreferences.IsEnabled;
            TestLabPreferences.IsEnabled = true;
            if (!wasEnabled) TestAuthoringHealthCheck.OnPreferencesChanged();
            EnsureController();
        }

        public static bool CanDisableExperimentalFeature =>
            !IsSaveBusy && !IsSavedReplayBusy && !IsConnectedRunBusy && !IsBrowserTestCreationActive &&
            _controller?.IsRunActive != true &&
            _controller?.State != TestAuthoringControllerState.CapturingStartingState &&
            _controller?.State != TestAuthoringControllerState.PreparingDemonstration &&
            _controller?.State != TestAuthoringControllerState.Recording;

        internal const string DisableBlockedMessage =
            "Finish or cancel the current operation in Test Lab before disabling. " +
            "For a local recording, create a draft first.";

        public static bool DisableExperimentalFeature(Func<bool> confirmDiscardLocalWork = null)
        {
            if (!CanDisableExperimentalFeature) return false;
            bool hasUnsavedWork = _controller?.CurrentDraft != null &&
                (!CurrentDraftIsDurable || _controller.CurrentDraft.ValidationHistory.Count > 0);
            if (hasUnsavedWork && !(confirmDiscardLocalWork?.Invoke() ?? false)) return false;
            TestLabPreferences.IsEnabled = false;
            if (ClientConnectionSession.HasPending) ClientConnectionSession.Cancel();
            ClientPresenceSession.Stop();
            TestLabLaunch.Cancel();
            TestAuthoringHealthCheck.Stop();
            _catalogOperation?.Dispose();
            _catalogOperation = null;
            ResetBrowserTestCreation();
            ResetConnectedRunner();
            BrowserCreationBackgroundExecution.Release();
            ClearSavedReplayOperation();
            DisposeController();
            return true;
        }

        public static void CaptureStartingState()
        {
            if (IsSavedReplayBusy || IsConnectedRunBusy || IsBrowserTestCreationActive) return;
            EnsureController();
            _controller?.CaptureStartingState(StateOperationTimeoutSeconds);
            ClearLoadedSavedVersionAfterDraftReplacement();
        }

        public static void CancelStateCapture()
        {
            _controller?.CancelStateCapture();
        }

        public static bool CreateDraft(
            string name,
            string expectedOutcome,
            out RecordedTestDraft draft)
        {
            draft = null;
            bool created = !IsConnectedRunBusy && !IsBrowserTestCreationActive &&
                           _controller != null &&
                           _controller.TryCreateDraft(name, expectedOutcome, out draft);
            if (created) ClearLoadedSavedVersion();
            return created;
        }

        static bool TryAttachReferenceEvidence(
            RecordedTestDraft draft,
            out bool pending,
            out string error)
        {
            pending = false;
            error = null;
            if (draft == null)
            {
                return false;
            }
            if (draft.ReferenceEvidenceClip != null) return true;
            if (_referenceEvidenceCapture == null)
            {
                error = "The reference gameplay video was not recorded.";
                return false;
            }
            _referenceEvidenceCapture.Tick();
            if (!_referenceEvidenceCapture.IsFinished)
            {
                pending = true;
                return false;
            }
            if (_referenceEvidenceCapture.State != TestEvidenceCaptureState.Succeeded)
            {
                error = _referenceEvidenceCapture.Status;
                return false;
            }
            draft.AttachReferenceEvidenceClip(_referenceEvidenceCapture.Clip);
            _referenceEvidenceCapture.Dispose();
            _referenceEvidenceCapture = null;
            return true;
        }

        public static void ClearDraft()
        {
            if (IsSaveBusy || IsSavedReplayBusy || IsConnectedRunBusy ||
                IsBrowserTestCreationActive) return;
            bool wasSavedVersion = CurrentDraftIsSavedVersion;
            if (_controller?.ClearDraft() == true)
            {
                if (wasSavedVersion)
                {
                    ClearSavedReplayOperation();
                    DisposeController();
                    EnsureController();
                }
                else
                {
                    ClearLoadedSavedVersion();
                }
            }
        }

        public static bool SaveCurrentDraft(out string error) =>
            SaveCurrentDraft(false, out error);

        static bool SaveCurrentDraft(bool browserCreation, out string error)
        {
            error = null;
            if (_controller?.CurrentDraft == null)
            {
                error = "Create a draft before saving it.";
                return false;
            }
            if (IsConnectedRunBusy)
            {
                error = "Qamel is executing a web-requested Run in this Play Mode session.";
                return false;
            }
            if (IsBrowserTestCreationActive && !browserCreation)
            {
                error = "Qamel is already creating this Test from the browser.";
                return false;
            }
            if (IsSaveBusy)
            {
                error = IsSavePending
                    ? "The connection is already being checked for this save."
                    : "A save is already in progress.";
                return false;
            }
            if (CurrentDraftIsSavedVersion)
            {
                error = "This immutable Test Version is already saved in Qamel.";
                return false;
            }
            if (IsSavedReplayBusy)
            {
                error = "Finish or cancel the saved Test download before saving.";
                return false;
            }
            if (_controller.IsRunActive ||
                _controller.State == TestAuthoringControllerState.NeedsReview)
            {
                error = "Finish or review the local replay before saving this draft.";
                return false;
            }
            if (_saveOperation != null &&
                _saveOperation.DraftId == _controller.CurrentDraft.DraftId &&
                _saveOperation.State == TestDefinitionSaveState.Succeeded)
            {
                error = "This draft's definition is already saved. Local replay results " +
                        "and notes are not uploaded yet.";
                return false;
            }

            _saveRequestError = null;
            _saveRequestStatus = null;
            var draft = _controller.CurrentDraft;
            _pendingSaveDraftId = draft.DraftId;
            _saveRequestStatus = "Checking the Qamel connection before saving. " +
                                 "Keep Play Mode active until the save finishes.";
            TestAuthoringHealthCheck.CheckNow();
            if (TestAuthoringHealthCheck.IsChecking)
                return true;

            error = TestAuthoringHealthCheck.LastError ??
                    "Qamel could not check this connection.";
            FailPendingSave(error);
            return false;
        }

        public static void ClearSaveRequestFeedback()
        {
            if (IsSaveBusy) return;
            _saveRequestStatus = null;
            _saveRequestError = null;
        }

        static bool StartSave(RecordedTestDraft draft, out string error)
        {
            error = null;
            _saveOperation?.Dispose();
            try
            {
                _saveOperation = new TestDefinitionSaveOperation(
                    draft.DraftId,
                    RecordedTestDefinitionSerializer.Serialize(draft),
                    TestLabPreferences.AuthoringEndpoint,
                    TestLabPreferences.AuthoringApiKey);
                _rememberedSaveOperation = null;
                _saveRequestStatus = null;
                _saveRequestError = null;
                return true;
            }
            catch (Exception exception)
            {
                _saveOperation = null;
                error = exception.Message;
                _saveRequestError = error;
                return false;
            }
        }

        public static void CancelSave()
        {
            _saveOperation?.Cancel();
        }

        public static void StopBrowserTestCreationLocally()
        {
            if (!CanStopBrowserTestCreationLocally) return;
            var creation = _testCreation;
            _testCreationOperation?.Dispose();
            _testCreationOperation = null;
            if (_testCreationOwnsLocalWork) ResetLocalCreationWork();
            _testCreationOwnsLocalWork = false;
            StartCreationFailure(
                creation,
                "stopped_locally",
                "The browser recording was stopped from Unity.");
            _testCreationStatus = "Stopping browser Test creation from Unity.";
        }

        public static bool StartReplay()
        {
            if (IsSaveBusy || IsSavedReplayBusy || IsConnectedRunBusy ||
                IsBrowserTestCreationActive ||
                _controller == null)
                return false;
            string bindingError = CurrentDraftReplayBlockedReason;
            if (!string.IsNullOrWhiteSpace(bindingError))
            {
                _savedReplayRequestError = bindingError;
                return false;
            }
            if (CurrentDraftIsSavedVersion)
            {
                string preflightError = SavedReplayPreflightBlockReason(
                    _loadedSavedVersion,
                    _loadedSavedTraceBytes,
                    Registry,
                    SavedReplayEnvironment);
                if (!string.IsNullOrWhiteSpace(preflightError))
                {
                    _savedReplayRequestError = preflightError;
                    return false;
                }
                _savedReplayRequestError = null;
            }
            return _controller.StartReplay(
                StateOperationTimeoutSeconds,
                ReplayTimeoutSeconds,
                ReplayCountdownSeconds);
        }

        public static bool CancelRun()
        {
            return _controller != null && _controller.CancelRun();
        }

        public static bool RecordVerdict(TestHumanVerdict verdict, string note)
        {
            return _controller != null && _controller.RecordHumanVerdict(verdict, note);
        }

        public static bool RefreshSavedTests(out string error)
        {
            error = null;
            if (!TestAuthoringHealthCheck.IsConnected)
            {
                error = "Check the Qamel connection before loading saved Tests.";
                return false;
            }
            if (IsSaveBusy || IsSavedReplayBusy)
            {
                error = "Finish the current Qamel operation before refreshing saved Tests.";
                return false;
            }
            if (IsConnectedRunBusy)
            {
                error = "Finish the web-requested Run before refreshing saved Tests.";
                return false;
            }
            if (IsBrowserTestCreationActive)
            {
                error = "Finish the browser Test creation before refreshing saved Tests.";
                return false;
            }

            _catalogOperation?.Dispose();
            try
            {
                _catalogOperation = new SavedTestCatalogOperation(
                    TestLabPreferences.AuthoringEndpoint,
                    TestLabPreferences.AuthoringApiKey,
                    TestAuthoringHealthCheck.ProjectId);
                _savedReplayRequestError = null;
                return true;
            }
            catch (Exception exception)
            {
                _catalogOperation = null;
                error = exception.Message;
                return false;
            }
        }

        public static SavedTestCompatibilityResult ReplayCompatibility(
            SavedTestVersion version)
        {
            return SavedTestReplayCompatibility.Evaluate(
                version,
                Registry,
                SavedReplayEnvironment);
        }

        public static bool StartSavedReplay(
            SavedTestVersion version,
            out string error)
        {
            error = null;
            if (!EditorApplication.isPlaying)
            {
                error = "Enter Play Mode before replaying a saved Test Version.";
                return false;
            }
            if (!TestAuthoringHealthCheck.IsConnected)
            {
                error = "Check the Qamel connection before replaying a saved Test.";
                return false;
            }
            if (version == null)
            {
                error = "Select an immutable Test Version to replay.";
                return false;
            }
            var catalog = SavedTestCatalog;
            if (catalog == null || !catalog.ContainsVersion(version))
            {
                error = "Load the saved Tests again before replaying this Test Version.";
                return false;
            }
            if (IsSaveBusy || IsSavedReplayBusy)
            {
                error = "Finish the current Qamel operation before starting this replay.";
                return false;
            }
            if (IsConnectedRunBusy)
            {
                error = "Finish the web-requested Run before replaying another version.";
                return false;
            }
            if (IsBrowserTestCreationActive)
            {
                error = "Finish the browser Test creation before replaying another version.";
                return false;
            }

            EnsureController();
            if (_controller == null)
            {
                error = "The local replay controller is not ready.";
                return false;
            }
            string localStateError = SavedReplayLocalStateBlockReason(_controller);
            if (!string.IsNullOrWhiteSpace(localStateError))
            {
                error = localStateError;
                return false;
            }

            var compatibility = ReplayCompatibility(version);
            if (!compatibility.IsCompatible)
            {
                error = compatibility.Reason;
                return false;
            }

            _savedReplayOperation?.Dispose();
            try
            {
                _savedReplayOperation = new SavedTestReplayOperation(
                    TestLabPreferences.AuthoringEndpoint,
                    TestLabPreferences.AuthoringApiKey,
                    TestAuthoringHealthCheck.ProjectId,
                    version,
                    Registry,
                    SavedReplayEnvironment);
                _handledSavedReplayOperation = null;
                _savedReplayRequestError = null;
                return true;
            }
            catch (Exception exception)
            {
                _savedReplayOperation = null;
                error = exception.Message;
                _savedReplayRequestError = error;
                return false;
            }
        }

        public static void CancelSavedReplay()
        {
            _savedReplayOperation?.Cancel();
        }

        static void Update()
        {
            if (!TestLabPreferences.IsEnabled) return;
            TestAuthoringHealthCheck.MaintainConnection(
                TestLabPreferences.IsEnabled);
            InvalidateSavedOperationsForConnectionChange();
            _catalogOperation?.Tick();
            _savedReplayOperation?.Tick();
            _saveOperation?.Tick();
            RememberSuccessfulSave();
            EnsureController();
            ContinuePendingSave();
            ContinueSavedReplay();
            ContinueBrowserTestCreation();
            if (_controller == null)
            {
                if (TestLabPreferences.IsEnabled) ReportRunnerBlocker();
                return;
            }

            SyncSimulationPause();
            var now = EditorApplication.timeSinceStartup;
            var delta = _lastEditorTime > 0 ? Math.Min(0.25, now - _lastEditorTime) : 0;
            _lastEditorTime = now;
            _sceneReloadProvider?.Tick();
            _controller.Tick((float)Math.Max(0, delta));
            ClearLoadedSavedVersionAfterDraftReplacement();
            if (TestLabInputFocusPolicy.ShouldFocusGameView(
                    _lastObservedControllerState,
                    _controller.State,
                    EditorApplication.isPlaying))
            {
                TestLabInputFocusPolicy.FocusGameView();
                if (_controller.State == TestAuthoringControllerState.ReplayRunning)
                    Debug.Log("[Qamel Test Lab] Replay started: application focused=" +
                              Application.isFocused + ", Game view focused=" +
                              TestLabInputFocusPolicy.IsGameViewFocused + ", cursor=" +
                              Cursor.lockState + ".");
            }
            SyncEvidenceCapture();
            ContinueBrowserTestCreation();
            ContinueDemonstrationStart();
            SyncSimulationPause();
            ContinueConnectedRunner();
            ReportRunnerBlocker();
            if (_controller != null)
                _lastObservedControllerState = _controller.State;
        }

        static void SyncEvidenceCapture()
        {
            if (_controller == null) return;
            var current = _controller.State;

            if (current == TestAuthoringControllerState.Recording &&
                _lastObservedControllerState != TestAuthoringControllerState.Recording)
            {
                _referenceEvidenceCapture?.Dispose();
                try
                {
                    _referenceEvidenceCapture = new TestEvidenceCapture();
                }
                catch (Exception exception)
                {
                    _referenceEvidenceCapture = null;
                    _saveRequestError = exception.Message;
                    Debug.LogWarning("[Qamel Test Lab] " + exception.Message);
                }
            }
            else if (_lastObservedControllerState == TestAuthoringControllerState.Recording &&
                     current != TestAuthoringControllerState.Recording)
            {
                _referenceEvidenceCapture?.Stop();
            }

            if (current == TestAuthoringControllerState.ReplayRunning &&
                _lastObservedControllerState != TestAuthoringControllerState.ReplayRunning &&
                IsConnectedRunBusy)
            {
                _replayEvidenceCapture?.Dispose();
                try
                {
                    _replayEvidenceCapture = new TestEvidenceCapture();
                }
                catch (Exception exception)
                {
                    _replayEvidenceCapture = null;
                    Debug.LogWarning("[Qamel Test Lab] " + exception.Message);
                }
            }
            else if (_lastObservedControllerState == TestAuthoringControllerState.ReplayRunning &&
                     current != TestAuthoringControllerState.ReplayRunning)
            {
                _replayEvidenceCapture?.Stop();
            }

            _referenceEvidenceCapture?.Tick();
            if (_referenceEvidenceCapture?.State == TestEvidenceCaptureState.Succeeded &&
                _controller.CurrentDraft != null &&
                _controller.CurrentDraft.ReferenceEvidenceClip == null)
            {
                _controller.CurrentDraft.AttachReferenceEvidenceClip(
                    _referenceEvidenceCapture.Clip);
                _referenceEvidenceCapture.Dispose();
                _referenceEvidenceCapture = null;
            }

            _replayEvidenceCapture?.Tick();
            if (_replayEvidenceCapture?.State == TestEvidenceCaptureState.Succeeded &&
                _controller.CurrentRun != null &&
                _controller.CurrentRun.ReplayEvidenceClip == null)
            {
                _controller.CurrentRun.ReplayEvidenceClip = _replayEvidenceCapture.Clip;
                _replayEvidenceCapture.Dispose();
                _replayEvidenceCapture = null;
            }
        }

        static void ContinueDemonstrationStart()
        {
            if (_controller == null ||
                !EditorApplication.isPlaying ||
                Application.isBatchMode)
            {
                ClearDemonstrationStartOverlay();
                return;
            }

            if (_controller.State == TestAuthoringControllerState.PreparingDemonstration)
                return;
            if (_controller.State != TestAuthoringControllerState.ReadyForDemonstration)
            {
                ClearDemonstrationStartOverlay();
                return;
            }

            if (_demonstrationStartOverlay == null)
            {
                var overlayObject = new GameObject("Qamel Start Demonstration")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                UnityEngine.Object.DontDestroyOnLoad(overlayObject);
                _demonstrationStartOverlay =
                    overlayObject.AddComponent<TestLabDemonstrationStartOverlay>();
                _demonstrationStartOverlay.Present();
                _demonstrationStartNotBeforeFrame = -1;
                return;
            }

            if (!_demonstrationStartOverlay.StartRequested)
                return;

            if (_demonstrationStartNotBeforeFrame < 0)
            {
                _demonstrationStartNotBeforeFrame =
                    Time.frameCount + DemonstrationInputSettleFrames;
                return;
            }
            if (Time.frameCount < _demonstrationStartNotBeforeFrame)
                return;

            _demonstrationStartNotBeforeFrame = -1;
            _controller.StartDemonstration(StateOperationTimeoutSeconds);
            if (_controller.State != TestAuthoringControllerState.PreparingDemonstration)
                ClearDemonstrationStartOverlay();
        }

        static void ClearDemonstrationStartOverlay()
        {
            _demonstrationStartNotBeforeFrame = -1;
            if (_demonstrationStartOverlay == null)
                return;

            var overlayObject = _demonstrationStartOverlay.gameObject;
            _demonstrationStartOverlay.Dismiss();
            _demonstrationStartOverlay = null;
            if (EditorApplication.isPlaying)
                UnityEngine.Object.Destroy(overlayObject);
            else
                UnityEngine.Object.DestroyImmediate(overlayObject);
        }

        static void SyncSimulationPause()
        {
            var shouldPause = _controller != null &&
                              TestLabInputFocusPolicy.ShouldPauseSimulation(
                                  _controller.State,
                                  EditorApplication.isPlaying,
                                  Application.isBatchMode,
                                  TestLabInputFocusPolicy.ShouldCaptureGameplayInput());
            SimulationPause.Sync(shouldPause);
        }

        static void ContinueBrowserTestCreation()
        {
            string fingerprint = TestLabPreferences.AuthoringFingerprint;
            if (!string.IsNullOrWhiteSpace(_testCreationConnectionFingerprint) &&
                !string.Equals(
                    _testCreationConnectionFingerprint,
                    fingerprint,
                    StringComparison.Ordinal))
            {
                if (_testCreationOwnsLocalWork) ResetLocalCreationWork();
                BrowserCreationBackgroundExecution.Release();
                ResetBrowserTestCreation();
                _testCreationStatus =
                    "Browser Test creation stopped after the Qamel connection changed.";
                _testCreationError =
                    "Reconnect the intended project, then start a new browser recording.";
                return;
            }
            _testCreationOperation?.Tick();
            if (_testCreationOperation != null && _testCreationOperation.IsFinished)
            {
                var operation = _testCreationOperation;
                _testCreationOperation = null;
                if (operation.State == TestCreationRequestState.Succeeded)
                {
                    if (_testCreationOperationIsPoll)
                    {
                        _testCreation = operation.Session;
                        _testCreationSessionId = _testCreation?.Id;
                        _testCreationError = null;
                        if (_testCreation == null)
                            BrowserCreationBackgroundExecution.Release();
                        else
                            BrowserCreationBackgroundExecution.Hold();
                        _nextTestCreationPollAt = EditorApplication.timeSinceStartup +
                                                  TestCreationPollingPolicy.DelayAfterPoll(
                                                      _testCreation);
                    }
                    else
                    {
                        _testCreation = null;
                        _testCreationError = null;
                        // Reconcile a state-changing acknowledgement immediately.
                        _nextTestCreationPollAt = 0;
                    }
                }
                else
                {
                    _testCreationError = operation.Status;
                    _nextTestCreationPollAt = EditorApplication.timeSinceStartup +
                                              TestCreationPollingPolicy.RetryIntervalSeconds;
                }
                operation.Dispose();
            }
            if (_testCreationOperation != null) return;

            if (_testCreation != null)
            {
                if (_testCreation.IsTerminal)
                {
                    _testCreationStatus = _testCreation.Status == "saved"
                        ? "Browser Test creation finished."
                        : _testCreation.Status == "cancelled"
                            ? "Browser Test creation was cancelled."
                            : _testCreation.Status == "expired"
                                ? "The browser Test creation request expired."
                                : _testCreation.ErrorMessage;
                    if (_testCreationOwnsLocalWork)
                        ResetLocalCreationWork();
                    _testCreationOwnsLocalWork = false;
                    BrowserCreationBackgroundExecution.Release();
                    _testCreation = null;
                    _testCreationSessionId = null;
                    _nextTestCreationPollAt = EditorApplication.timeSinceStartup +
                                              TestCreationPollingPolicy.IdleIntervalSeconds;
                    return;
                }

                if (ContinueLocalCreationState(_testCreation)) return;
            }

            if (!CanPollBrowserTestCreation(fingerprint) ||
                EditorApplication.timeSinceStartup < _nextTestCreationPollAt)
                return;
            try
            {
                _testCreationOperationIsPoll = true;
                _testCreationOperation = new TestCreationRequestOperation(
                    TestLabPreferences.AuthoringEndpoint,
                    TestLabPreferences.AuthoringApiKey,
                    TestCreationProtocol.PollRequest(
                        TestLabPreferences.RunnerInstallationId,
                        _testCreationSessionId),
                    poll: true);
                _testCreationConnectionFingerprint = fingerprint;
                _testCreationStatus = _testCreationOperation.Status;
                _nextTestCreationPollAt = EditorApplication.timeSinceStartup +
                                          TestCreationPollingPolicy.DelayAfterPoll(
                                              _testCreation);
            }
            catch (Exception exception)
            {
                _testCreationError = "Could not check for browser Test creation: " +
                                     exception.Message;
                _nextTestCreationPollAt = EditorApplication.timeSinceStartup +
                                          TestCreationPollingPolicy.RetryIntervalSeconds;
            }
        }

        static bool ContinueLocalCreationState(ConnectedTestCreation creation)
        {
            switch (creation.Status)
            {
                case "requested":
                {
                    string blocked = BrowserCreationStartBlockedReason();
                    if (blocked != null)
                    {
                        StartCreationFailure(creation, "local_busy", blocked);
                        return true;
                    }
                    StartCreationAdvance(
                        creation,
                        "preparing",
                        TestCreationProtocol.PreparingRequest(
                            TestLabPreferences.RunnerInstallationId,
                            creation.Id,
                            SceneManager.GetActiveScene().path));
                    return true;
                }
                case "preparing":
                    _testCreationStatus = "Preparing the saved scene and starting state.";
                    if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                        EditorApplication.isPlayingOrWillChangePlaymode != EditorApplication.isPlaying)
                        return false;
                    if (!EditorApplication.isPlaying)
                    {
                        string blocked = BrowserCreationStartBlockedReason();
                        if (blocked != null)
                        {
                            StartCreationFailure(creation, "local_busy", blocked);
                            return true;
                        }
                        _testCreationOwnsLocalWork = true;
                        EditorApplication.isPlaying = true;
                        return false;
                    }
                    if (!string.Equals(
                            SceneManager.GetActiveScene().path,
                            creation.ActiveScenePath,
                            StringComparison.Ordinal))
                    {
                        StartCreationFailure(
                            creation,
                            "scene_changed",
                            "The active scene changed after browser recording was requested.");
                        return true;
                    }
                    EnsureController();
                    if (_controller == null)
                    {
                        StartCreationFailure(
                            creation, "controller_unavailable",
                            "Unity could not initialize Test recording in Play Mode.");
                        return true;
                    }
                    if (_controller.State == TestAuthoringControllerState.Idle)
                    {
                        _testCreationOwnsLocalWork = true;
                        _controller.CaptureStartingState(StateOperationTimeoutSeconds);
                        return false;
                    }
                    if (_controller.State == TestAuthoringControllerState.ReadyForDemonstration)
                    {
                        ClearDemonstrationStartOverlay();
                        _controller.StartDemonstration(StateOperationTimeoutSeconds);
                        return false;
                    }
                    if (_controller.State == TestAuthoringControllerState.Recording)
                    {
                        TestLabInputFocusPolicy.FocusGameView();
                        StartCreationAdvance(
                            creation,
                            "recording",
                            TestCreationProtocol.RecordingRequest(
                                TestLabPreferences.RunnerInstallationId,
                                creation.Id));
                        return true;
                    }
                    if (_controller.State == TestAuthoringControllerState.CapturingStartingState ||
                        _controller.State == TestAuthoringControllerState.PreparingDemonstration)
                        return false;
                    StartCreationFailure(
                        creation, "preparation_failed",
                        _controller.LastError ?? "Unity could not prepare the recording.");
                    return true;

                case "recording":
                    _testCreationStatus = "Recording gameplay for the browser Test draft.";
                    if (_controller?.State == TestAuthoringControllerState.Recording)
                        return false;
                    StartCreationFailure(
                        creation, "recording_interrupted",
                        "The local recording was interrupted or Unity reloaded.");
                    return true;

                case "stop_requested":
                    _testCreationStatus = "Freezing the recorded input into a local draft.";
                    if (_controller == null)
                    {
                        StartCreationFailure(
                            creation, "recording_interrupted",
                            "The local recording is no longer available.");
                        return true;
                    }
                    if (_controller.State == TestAuthoringControllerState.Recording &&
                        !_controller.TryCreateDraft(
                            "Recorded Test",
                            "The demonstrated gameplay result is reproduced.",
                            out _))
                    {
                        StartCreationFailure(
                            creation, "draft_failed",
                            _controller.LastError ?? "Unity could not create the recorded draft.");
                        return true;
                    }
                    var draft = _controller.CurrentDraft;
                    if (_controller.State != TestAuthoringControllerState.DraftReady || draft == null)
                    {
                        StartCreationFailure(
                            creation, "draft_interrupted",
                            "The local recorded draft is no longer available.");
                        return true;
                    }
                    if (!TryAttachReferenceEvidence(
                            draft,
                            out var evidencePending,
                            out var evidenceError))
                    {
                        if (evidencePending)
                        {
                            _testCreationStatus =
                                "Finalizing the reference gameplay video in memory.";
                            return false;
                        }
                        StartCreationFailure(
                            creation,
                            "evidence_capture_failed",
                            evidenceError ?? _saveRequestError ??
                            "Unity could not capture the demonstrated gameplay video.");
                        return true;
                    }
                    StartCreationAdvance(
                        creation,
                        "draft_ready",
                        TestCreationProtocol.DraftReadyRequest(
                            TestLabPreferences.RunnerInstallationId,
                            creation.Id,
                            draft.DraftId,
                            draft.InputTrace.Metrics.RetainedDurationSeconds,
                            draft.InputTrace.Metrics.RetainedStateEventCount,
                            draft.InputTrace.Metrics.DeviceLayouts.ToArray()));
                    return true;

                case "draft_ready":
                    _testCreationStatus = "Recorded draft ready for browser review.";
                    if (!LocalDraftMatches(creation))
                    {
                        StartCreationFailure(
                            creation, "draft_interrupted",
                            "The local recorded draft was cleared or replaced.");
                        return true;
                    }
                    return false;

                case "save_requested":
                    if (!LocalDraftMatches(creation))
                    {
                        StartCreationFailure(
                            creation, "draft_interrupted",
                            "The reviewed local draft is no longer available.");
                        return true;
                    }
                    StartCreationAdvance(
                        creation,
                        "saving",
                        TestCreationProtocol.SavingRequest(
                            TestLabPreferences.RunnerInstallationId,
                            creation.Id));
                    return true;

                case "saving":
                    _testCreationStatus = "Saving the browser-reviewed Test to Qamel.";
                    if (!LocalDraftMatches(creation))
                    {
                        StartCreationFailure(
                            creation, "draft_interrupted",
                            "The reviewed local draft is no longer available.");
                        return true;
                    }
                    if ((_saveOperation == null ||
                         !string.Equals(
                             _saveOperation.DraftId,
                             creation.LocalDraftId,
                             StringComparison.Ordinal)) && !IsSavePending)
                    {
                        string saveError = null;
                        if (!_controller.ReviseDraftMetadata(
                                creation.Name,
                                creation.ExpectedOutcome) ||
                            !SaveCurrentDraft(true, out saveError))
                        {
                            StartCreationFailure(
                                creation, "save_failed",
                                saveError ?? _controller.LastError ??
                                "Unity could not start saving the reviewed Test.");
                            return true;
                        }
                        return false;
                    }
                    if (IsSaveBusy) return false;
                    if (_saveOperation?.State == TestDefinitionSaveState.Succeeded &&
                        string.Equals(
                            _saveOperation.DraftId,
                            creation.LocalDraftId,
                            StringComparison.Ordinal))
                    {
                        StartCreationAdvance(
                            creation,
                            "saved",
                            TestCreationProtocol.SavedRequest(
                                TestLabPreferences.RunnerInstallationId,
                                creation.Id,
                                _saveOperation.TestId,
                                _saveOperation.TestVersionId));
                        return true;
                    }
                    StartCreationFailure(
                        creation, "save_failed",
                        _saveRequestError ?? _saveOperation?.Status ??
                        "Unity could not save the reviewed Test.");
                    return true;
                default:
                    return false;
            }
        }

        static string BrowserCreationStartBlockedReason()
        {
            if (Application.isBatchMode)
                return "Browser recording is unavailable in a batch Unity process.";
            if (IsSaveBusy || IsSavedReplayBusy || IsConnectedRunBusy)
                return "Finish the current Test save, replay, or Run before starting a recording.";
            if (_controller != null &&
                (_controller.State != TestAuthoringControllerState.Idle ||
                 _controller.CurrentDraft != null))
                return "Finish and clear the current local Test Lab work before recording from the browser.";
            if (SceneManager.sceneCount != 1)
                return "Browser recording currently supports one loaded scene only.";
            var scene = SceneManager.GetActiveScene();
            if (!TestLabLaunch.IsProjectScenePath(scene.path) ||
                AssetDatabase.LoadAssetAtPath<SceneAsset>(scene.path) == null)
                return "Open a saved project scene before recording from the browser.";
            if (scene.isDirty || EditorSceneManager.GetActiveScene().isDirty)
                return "Save scene changes before recording from the browser.";
            return null;
        }

        static bool LocalDraftMatches(ConnectedTestCreation creation) =>
            _controller?.CurrentDraft != null &&
            _controller.State == TestAuthoringControllerState.DraftReady &&
            string.Equals(
                _controller.CurrentDraft.DraftId,
                creation.LocalDraftId,
                StringComparison.OrdinalIgnoreCase);

        static void StartCreationAdvance(
            ConnectedTestCreation creation,
            string nextStatus,
            string json)
        {
            try
            {
                _testCreationOperationIsPoll = false;
                _testCreationOperation = new TestCreationRequestOperation(
                    TestLabPreferences.AuthoringEndpoint,
                    TestLabPreferences.AuthoringApiKey,
                    json,
                    poll: false,
                    sessionId: creation.Id,
                    expectedStatus: nextStatus);
                _testCreationConnectionFingerprint =
                    TestLabPreferences.AuthoringFingerprint;
                _testCreationStatus = _testCreationOperation.Status;
                _testCreationError = null;
            }
            catch (Exception exception)
            {
                _testCreationError = "Could not update browser Test creation: " +
                                     exception.Message;
                _nextTestCreationPollAt = EditorApplication.timeSinceStartup +
                                          TestCreationPollingPolicy.RetryIntervalSeconds;
            }
        }

        static void StartCreationFailure(
            ConnectedTestCreation creation,
            string errorCode,
            string errorMessage)
        {
            StartCreationAdvance(
                creation,
                "failed",
                TestCreationProtocol.FailedRequest(
                    TestLabPreferences.RunnerInstallationId,
                    creation.Id,
                    creation.Status,
                    errorCode,
                    errorMessage));
        }

        static bool CanPollBrowserTestCreation(string fingerprint)
        {
            return !Application.isBatchMode && TestLabPreferences.IsEnabled &&
                   !EditorApplication.isCompiling && !EditorApplication.isUpdating &&
                   TestAuthoringHealthCheck.IsConnected &&
                   string.Equals(
                       fingerprint,
                       TestLabPreferences.AuthoringFingerprint,
                       StringComparison.Ordinal);
        }

        static void ResetLocalCreationWork()
        {
            if (_testCreation != null &&
                !string.IsNullOrWhiteSpace(_testCreation.LocalDraftId))
            {
                if (string.Equals(
                        _pendingSaveDraftId,
                        _testCreation.LocalDraftId,
                        StringComparison.Ordinal))
                {
                    _pendingSaveDraftId = null;
                    _saveRequestStatus = null;
                    _saveRequestError = null;
                }
                if (_saveOperation != null && !_saveOperation.IsFinished &&
                    string.Equals(
                        _saveOperation.DraftId,
                        _testCreation.LocalDraftId,
                        StringComparison.Ordinal))
                {
                    _saveOperation.Cancel();
                }
            }
            if (_controller == null || _controller.State == TestAuthoringControllerState.Idle)
                return;
            DisposeController();
            EnsureController();
        }

        static void ResetBrowserTestCreation()
        {
            _testCreationOperation?.Dispose();
            _testCreationOperation = null;
            _testCreation = null;
            _testCreationSessionId = null;
            _testCreationStatus = null;
            _testCreationError = null;
            _testCreationConnectionFingerprint = null;
            _nextTestCreationPollAt = 0;
            _testCreationOwnsLocalWork = false;
        }

        static void ContinueConnectedRunner()
        {
            string fingerprint = TestLabPreferences.AuthoringFingerprint;
            if (!string.IsNullOrWhiteSpace(_runnerConnectionFingerprint) &&
                !string.Equals(
                    _runnerConnectionFingerprint,
                    fingerprint,
                    StringComparison.Ordinal))
            {
                ResetConnectedRunner(
                    "The Qamel connection changed. Waiting to register this runner again.");
            }

            _runnerLeaseOperation?.Tick();
            _runnerHeartbeatOperation?.Tick();
            _runnerDownloadOperation?.Tick();
            _runnerEvidenceOperation?.Tick();
            _runnerResultOperation?.Tick();

            if (_runnerLease?.HasRun == true &&
                (!string.IsNullOrWhiteSpace(_runnerPendingResultJson) ||
                 _runnerLocalRun?.IsTerminal == true) &&
                ContinueConnectedRunHeartbeat())
            {
                FinishConnectedRun();
                return;
            }

            if (_runnerEvidenceOperation != null)
            {
                if (!_runnerEvidenceOperation.IsFinished) return;
                if (_runnerEvidenceOperation.State !=
                    TestEvidenceUploadState.Succeeded)
                {
                    _runnerError = _runnerEvidenceOperation.Status;
                    _runnerEvidenceOperation.Dispose();
                    _runnerEvidenceOperation = null;
                    _nextRunnerResultRetryAt =
                        EditorApplication.timeSinceStartup + RunnerRetryIntervalSeconds;
                    return;
                }
                _runnerEvidenceOperation.Dispose();
                _runnerEvidenceOperation = null;
                _runnerEvidenceUploaded = true;
                _runnerStatus = "Replay video evidence uploaded for processing.";
            }

            if (_runnerResultOperation != null)
            {
                if (!_runnerResultOperation.IsFinished) return;
                if (_runnerResultOperation.State == TestRunResultUploadState.Succeeded)
                {
                    _runnerStatus = _runnerResultOperation.Status;
                    _runnerError = null;
                    FinishConnectedRun();
                    return;
                }

                _runnerError = _runnerResultOperation.Status;
                _runnerResultOperation.Dispose();
                _runnerResultOperation = null;
                _nextRunnerResultRetryAt =
                    EditorApplication.timeSinceStartup + RunnerRetryIntervalSeconds;
            }

            if (!string.IsNullOrWhiteSpace(_runnerPendingResultJson))
            {
                if (!TestAuthoringHealthCheck.IsConnected ||
                    EditorApplication.timeSinceStartup < _nextRunnerResultRetryAt)
                    return;
                if (_runnerLocalRun?.ReplayEvidenceClip != null &&
                    !_runnerEvidenceUploaded)
                {
                    StartRunnerEvidenceUpload();
                    return;
                }
                StartRunnerResultUpload();
                return;
            }

            if (_runnerLocalRun != null)
            {
                if (ContinueConnectedRunHeartbeat())
                {
                    if (!_runnerLocalRun.IsTerminal)
                        _controller?.CancelRun();
                    FinishConnectedRun();
                    return;
                }
                if (!_runnerLocalRun.IsTerminal &&
                    _controller?.State == TestAuthoringControllerState.NeedsReview &&
                    RunnerUsesHumanReview(_runnerVersion))
                {
                    if (!_controller.RecordHumanVerdict(
                            TestHumanVerdict.CouldNotTell,
                            "No automatic outcome check is configured. Review the replay evidence."))
                    {
                        _runnerError = _controller.LastError ??
                            "The generic Run could not enter Needs review.";
                        return;
                    }
                    _runnerLocalRun = _controller.CurrentRun;
                }
                if (!_runnerLocalRun.IsTerminal) return;
                if (_runnerLocalRun.ReplayEvidenceClip == null &&
                    _replayEvidenceCapture != null)
                {
                    _replayEvidenceCapture.Tick();
                    if (!_replayEvidenceCapture.IsFinished) return;
                    if (_replayEvidenceCapture.State == TestEvidenceCaptureState.Succeeded)
                        _runnerLocalRun.ReplayEvidenceClip = _replayEvidenceCapture.Clip;
                    else
                        Debug.LogWarning("[Qamel Test Lab] " +
                                         _replayEvidenceCapture.Status);
                    _replayEvidenceCapture.Dispose();
                    _replayEvidenceCapture = null;
                }
                try
                {
                    _runnerPendingResultJson = TestRunResultSerializer.Serialize(
                        _runnerVersion,
                        _runnerLocalRun,
                        TestRunExecutionEnvironment.Current());
                    if (_runnerLocalRun.ReplayEvidenceClip != null)
                        StartRunnerEvidenceUpload();
                    else
                        StartRunnerResultUpload();
                }
                catch (Exception exception)
                {
                    _runnerError = "Could not prepare the Run result: " + exception.Message;
                }
                return;
            }

            if (_runnerDownloadOperation != null)
            {
                if (ContinueConnectedRunHeartbeat())
                {
                    _runnerDownloadOperation.Dispose();
                    _runnerDownloadOperation = null;
                    FinishConnectedRun();
                    return;
                }
                if (!_runnerDownloadOperation.IsFinished) return;
                if (_runnerDownloadOperation.State != SavedTestReplayDownloadState.Ready)
                {
                    _runnerError = _runnerDownloadOperation.Status;
                    ResetRunnerAssignmentForRetry();
                    return;
                }
                if (!LocalControllerCanYieldToRunner())
                {
                    _runnerStatus =
                        "A Run is assigned. Finish and save or clear the local draft first.";
                    return;
                }

                if (_controller.State != TestAuthoringControllerState.Idle)
                {
                    DisposeController();
                    EnsureController();
                }
                if (_controller == null ||
                    _controller.State != TestAuthoringControllerState.Idle)
                {
                    _runnerError =
                        "The local Test Lab could not prepare a clean replay controller.";
                    ResetRunnerAssignmentForRetry();
                    return;
                }

                _runnerVersion = _runnerDownloadOperation.Version;
                if (!_controller.TryLoadDraftForReplay(
                        _runnerDownloadOperation.ReadyDraft))
                {
                    _runnerError = _controller.LastError ??
                        "The leased Test Version could not be loaded.";
                    ResetRunnerAssignmentForRetry();
                    return;
                }
                _runnerDownloadOperation.Dispose();
                _runnerDownloadOperation = null;
                if (!_controller.StartReplay(
                        StateOperationTimeoutSeconds,
                        ReplayTimeoutSeconds,
                        ReplayCountdownSeconds))
                {
                    _runnerLocalRun = _controller.CurrentRun;
                    if (_runnerLocalRun == null)
                    {
                        _runnerError = _controller.LastError ??
                            "The leased Test Version could not start.";
                        ResetRunnerAssignmentForRetry();
                    }
                    return;
                }
                _runnerLocalRun = _controller.CurrentRun;
                _runnerStatus = "Executing the Run requested from Qamel.";
                _runnerError = null;
                return;
            }

            if (_runnerLeaseOperation != null)
            {
                if (!_runnerLeaseOperation.IsFinished) return;
                if (_runnerLeaseOperation.State != TestRunnerLeaseState.Succeeded)
                {
                    _runnerError = _runnerLeaseOperation.Status;
                    _runnerLeaseOperation.Dispose();
                    _runnerLeaseOperation = null;
                    _nextRunnerPollAt =
                        EditorApplication.timeSinceStartup + RunnerRetryIntervalSeconds;
                    return;
                }

                var lease = _runnerLeaseOperation.Lease;
                _runnerStatus = _runnerLeaseOperation.Status;
                _runnerLeaseOperation.Dispose();
                _runnerLeaseOperation = null;
                if (!lease.HasRun)
                {
                    _runnerError = null;
                    _nextRunnerPollAt =
                        EditorApplication.timeSinceStartup + RunnerPollIntervalSeconds;
                    return;
                }

                _runnerLease = lease;
                _nextRunnerHeartbeatAt =
                    EditorApplication.timeSinceStartup +
                    RunnerHeartbeatIntervalSeconds;
                _runnerStatus = "Downloading the assigned immutable Test Version.";
                _runnerError = null;
                try
                {
                    _runnerDownloadOperation = new SavedTestReplayOperation(
                        TestLabPreferences.AuthoringEndpoint,
                        TestLabPreferences.AuthoringApiKey,
                        TestAuthoringHealthCheck.ProjectId,
                        lease.TestVersionId,
                        Registry,
                        SavedReplayEnvironment);
                }
                catch (Exception exception)
                {
                    _runnerError = "Could not start the leased Test download: " +
                                   exception.Message;
                    ResetRunnerAssignmentForRetry();
                }
                return;
            }

            if (!CanPollConnectedRunner() ||
                EditorApplication.timeSinceStartup < _nextRunnerPollAt)
                return;

            TestRunExecutionEnvironment environment;
            try
            {
                environment = TestRunExecutionEnvironment.Current();
            }
            catch (Exception exception)
            {
                _runnerError = "Runner environment is incomplete: " + exception.Message;
                _nextRunnerPollAt =
                    EditorApplication.timeSinceStartup + RunnerRetryIntervalSeconds;
                return;
            }
            if (!TestRunnerLeaseOperation.TryBuildRequest(
                    TestLabPreferences.RunnerInstallationId,
                    Application.productName + " on " + SystemInfo.deviceName,
                    Registry,
                    environment,
                    out var requestJson,
                    out var requestError))
            {
                _runnerError = requestError;
                _nextRunnerPollAt =
                    EditorApplication.timeSinceStartup + RunnerRetryIntervalSeconds;
                return;
            }

            try
            {
                _runnerConnectionFingerprint = fingerprint;
                _runnerLeaseOperation = new TestRunnerLeaseOperation(
                    TestLabPreferences.AuthoringEndpoint,
                    TestLabPreferences.AuthoringApiKey,
                    requestJson);
                _runnerStatus = _runnerLeaseOperation.Status;
                _runnerError = null;
            }
            catch (Exception exception)
            {
                _runnerError = "Could not check Qamel for Runs: " + exception.Message;
                _nextRunnerPollAt =
                    EditorApplication.timeSinceStartup + RunnerRetryIntervalSeconds;
            }
        }

        static bool CanPollConnectedRunner()
        {
            return ConnectedRunnerBlockedReason == null;
        }

        internal static bool RunnerUsesHumanReview(SavedTestVersion version)
        {
            return version?.Stages != null && version.Stages.Any(stage =>
                string.Equals(
                    stage.Adapter,
                    SavedTestReplayCompatibility.HumanReviewAdapter,
                    StringComparison.Ordinal));
        }

        static void ReportRunnerBlocker()
        {
            string diagnostic = IsConnectedRunBusy ? null : ConnectedRunnerError;
            if (string.Equals(_lastRunnerDiagnostic, diagnostic, StringComparison.Ordinal)) return;
            _lastRunnerDiagnostic = diagnostic;
            Debug.Log("[Qamel Test Lab] " + (diagnostic ?? "Runner can check for work."));
        }

        static bool LocalControllerCanYieldToRunner()
        {
            return _controller != null &&
                   ConnectedTestRunnerReadiness.CanYieldLocalController(
                       _controller.State,
                       _controller.CurrentDraft != null,
                       CurrentDraftIsDurable);
        }

        static void StartRunnerResultUpload()
        {
            try
            {
                _runnerResultOperation?.Dispose();
                _runnerResultOperation = new TestRunResultUploadOperation(
                    TestLabPreferences.AuthoringEndpoint,
                    TestLabPreferences.AuthoringApiKey,
                    _runnerLease.TestRunId,
                    _runnerPendingResultJson);
                _runnerStatus = _runnerResultOperation.Status;
            }
            catch (Exception exception)
            {
                _runnerResultOperation = null;
                _runnerError = "Could not send the Run result: " + exception.Message;
                _nextRunnerResultRetryAt =
                    EditorApplication.timeSinceStartup + RunnerRetryIntervalSeconds;
            }
        }

        static void StartRunnerEvidenceUpload()
        {
            if (_runnerLease?.HasRun != true ||
                _runnerLocalRun?.ReplayEvidenceClip == null)
                return;
            try
            {
                _runnerEvidenceOperation?.Dispose();
                _runnerEvidenceOperation = new TestEvidenceUploadOperation(
                    TestLabPreferences.AuthoringEndpoint,
                    TestLabPreferences.AuthoringApiKey,
                    _runnerLease.TestRunId,
                    runEvidence: true,
                    _runnerLocalRun.ReplayEvidenceClip);
                _runnerStatus = _runnerEvidenceOperation.Status;
                _runnerError = null;
            }
            catch (Exception exception)
            {
                _runnerEvidenceOperation = null;
                _runnerError = "Could not send replay evidence: " + exception.Message;
                _nextRunnerResultRetryAt =
                    EditorApplication.timeSinceStartup + RunnerRetryIntervalSeconds;
            }
        }

        static bool ContinueConnectedRunHeartbeat()
        {
            if (_runnerLease?.HasRun != true) return false;

            if (_runnerHeartbeatOperation != null)
            {
                if (!_runnerHeartbeatOperation.IsFinished) return false;
                if (_runnerHeartbeatOperation.State != TestRunHeartbeatState.Succeeded)
                {
                    _runnerError = _runnerHeartbeatOperation.Status;
                    _runnerHeartbeatOperation.Dispose();
                    _runnerHeartbeatOperation = null;
                    _nextRunnerHeartbeatAt =
                        EditorApplication.timeSinceStartup +
                        RunnerRetryIntervalSeconds;
                    return false;
                }

                string runStatus = _runnerHeartbeatOperation.RunStatus;
                DateTimeOffset? leaseExpiresAt =
                    _runnerHeartbeatOperation.LeaseExpiresAt;
                _runnerHeartbeatOperation.Dispose();
                _runnerHeartbeatOperation = null;
                if (runStatus != "running")
                {
                    _runnerStatus = "Qamel reports the active Run as " +
                                    runStatus.Replace('_', ' ') + ".";
                    _runnerError = null;
                    _nextRunnerHeartbeatAt = 0;
                    return true;
                }

                _runnerLease = new TestRunnerLease(
                    _runnerLease.RunnerId,
                    _runnerLease.TestRunId,
                    _runnerLease.TestVersionId,
                    leaseExpiresAt);
                _runnerError = null;
                _nextRunnerHeartbeatAt =
                    EditorApplication.timeSinceStartup +
                    RunnerHeartbeatIntervalSeconds;
            }

            if (_runnerHeartbeatOperation != null ||
                !TestAuthoringHealthCheck.IsConnected ||
                EditorApplication.timeSinceStartup < _nextRunnerHeartbeatAt)
                return false;

            try
            {
                _runnerHeartbeatOperation = new TestRunHeartbeatOperation(
                    TestLabPreferences.AuthoringEndpoint,
                    TestLabPreferences.AuthoringApiKey,
                    _runnerLease.TestRunId,
                    _runnerLease.RunnerId);
            }
            catch (Exception exception)
            {
                _runnerError = "Could not keep the active Run connected: " +
                               exception.Message;
                _nextRunnerHeartbeatAt =
                    EditorApplication.timeSinceStartup +
                    RunnerRetryIntervalSeconds;
            }
            return false;
        }

        static void StopConnectedRunHeartbeat()
        {
            _runnerHeartbeatOperation?.Dispose();
            _runnerHeartbeatOperation = null;
            _nextRunnerHeartbeatAt = 0;
        }

        static void FinishConnectedRun()
        {
            StopConnectedRunHeartbeat();
            _runnerResultOperation?.Dispose();
            _runnerResultOperation = null;
            _runnerEvidenceOperation?.Dispose();
            _runnerEvidenceOperation = null;
            _runnerLease = null;
            _runnerVersion = null;
            _runnerLocalRun = null;
            _runnerPendingResultJson = null;
            _runnerEvidenceUploaded = false;
            _nextRunnerResultRetryAt = 0;
            _nextRunnerPollAt =
                EditorApplication.timeSinceStartup + RunnerPollIntervalSeconds;
            DisposeController();
            EnsureController();
        }

        static void ResetRunnerAssignmentForRetry()
        {
            bool hadRunnerDraft = _runnerVersion != null &&
                                  _controller?.CurrentDraft != null;
            _runnerDownloadOperation?.Dispose();
            _runnerDownloadOperation = null;
            _runnerVersion = null;
            _runnerLocalRun = null;
            _runnerPendingResultJson = null;
            _runnerEvidenceOperation?.Dispose();
            _runnerEvidenceOperation = null;
            _runnerEvidenceUploaded = false;
            _runnerLease = null;
            StopConnectedRunHeartbeat();
            if (hadRunnerDraft)
            {
                DisposeController();
                EnsureController();
            }
            _nextRunnerPollAt =
                EditorApplication.timeSinceStartup + RunnerRetryIntervalSeconds;
        }

        static void ResetConnectedRunner(string status = null)
        {
            _runnerLeaseOperation?.Dispose();
            _runnerLeaseOperation = null;
            StopConnectedRunHeartbeat();
            _runnerDownloadOperation?.Dispose();
            _runnerDownloadOperation = null;
            _runnerResultOperation?.Dispose();
            _runnerResultOperation = null;
            _runnerEvidenceOperation?.Dispose();
            _runnerEvidenceOperation = null;
            _runnerLease = null;
            _runnerVersion = null;
            _runnerLocalRun = null;
            _runnerPendingResultJson = null;
            _runnerEvidenceUploaded = false;
            _runnerConnectionFingerprint = null;
            _runnerStatus = status;
            _runnerError = null;
            _nextRunnerPollAt = 0;
            _nextRunnerHeartbeatAt = 0;
            _nextRunnerResultRetryAt = 0;
        }

        static void InvalidateSavedOperationsForConnectionChange()
        {
            string fingerprint = TestLabPreferences.AuthoringFingerprint;
            bool invalidCatalog = _catalogOperation != null &&
                !string.Equals(
                    _catalogOperation.ConnectionFingerprint,
                    fingerprint,
                    StringComparison.Ordinal);
            bool invalidReplay = _savedReplayOperation != null &&
                !string.Equals(
                    _savedReplayOperation.ConnectionFingerprint,
                    fingerprint,
                    StringComparison.Ordinal);
            if (!invalidCatalog && !invalidReplay) return;

            if (invalidCatalog)
            {
                _catalogOperation.Dispose();
                _catalogOperation = null;
            }
            if (invalidReplay)
            {
                _savedReplayOperation.Dispose();
                _savedReplayOperation = null;
                _handledSavedReplayOperation = null;
            }
            _savedReplayRequestError =
                "The Qamel endpoint or project API key changed. " +
                "Check the connection, then load the saved Tests again.";
        }

        static void ContinueSavedReplay()
        {
            if (_savedReplayOperation == null ||
                _savedReplayOperation == _handledSavedReplayOperation ||
                !_savedReplayOperation.IsFinished)
                return;

            _handledSavedReplayOperation = _savedReplayOperation;
            if (_savedReplayOperation.State != SavedTestReplayDownloadState.Ready)
                return;
            if (_controller == null || !EditorApplication.isPlaying)
            {
                _savedReplayRequestError =
                    "Play Mode ended before the saved Test could start replay.";
                return;
            }

            if (!TestAuthoringHealthCheck.IsConnected ||
                !string.Equals(
                    _savedReplayOperation.ExpectedProjectId,
                    TestAuthoringHealthCheck.ProjectId,
                    StringComparison.OrdinalIgnoreCase) ||
                SavedTestCatalog == null ||
                !SavedTestCatalog.ContainsVersion(_savedReplayOperation.SelectedVersion))
            {
                _savedReplayRequestError =
                    "The connected project or saved Test selection changed during download. " +
                    "Check the connection and load the saved Tests again.";
                return;
            }

            var version = _savedReplayOperation.Version;
            var compatibility = ReplayCompatibility(version);
            if (!compatibility.IsCompatible)
            {
                _savedReplayRequestError =
                    "The local replay environment changed after download. " +
                    compatibility.Reason;
                return;
            }
            if (!_controller.TryLoadDraftForReplay(
                    _savedReplayOperation.ReadyDraft))
            {
                _savedReplayRequestError = _controller.LastError ??
                    "The verified saved Test could not be loaded for replay.";
                return;
            }

            var loadedCatalog = SavedTestCatalog;
            _loadedSavedBinding = new LoadedSavedTestBinding(
                _savedReplayOperation.ConnectionFingerprint,
                _savedReplayOperation.ExpectedProjectId,
                loadedCatalog,
                _savedReplayOperation.SelectedVersion,
                _savedReplayOperation.ReadyDraft.DraftId);
            _loadedSavedTestId = version.TestId;
            _loadedSavedTestVersionId = version.TestVersionId;
            _loadedSavedVersionNumber = version.VersionNumber;
            _loadedSavedVersion = version;
            _loadedSavedTraceBytes =
                _savedReplayOperation.ReadyDraft.InputTrace.GetBytesCopy();
            TestDefinitionRoutes.TryGetTestUrl(
                TestLabPreferences.AuthoringEndpoint,
                version.TestId,
                out _loadedSavedTestUrl);
            if (!StartReplay())
            {
                _savedReplayRequestError = _savedReplayRequestError ??
                    _controller.LastError ??
                    "The verified saved Test could not start replay.";
            }
        }

        static void ContinuePendingSave()
        {
            if (!IsSavePending || TestAuthoringHealthCheck.IsChecking)
                return;

            if (!TestAuthoringHealthCheck.IsConnected)
            {
                FailPendingSave(
                    TestAuthoringHealthCheck.LastError ??
                    "Qamel could not verify this connection before saving.");
                return;
            }

            var draft = _controller?.CurrentDraft;
            if (draft == null || !string.Equals(
                    draft.DraftId,
                    _pendingSaveDraftId,
                    StringComparison.Ordinal))
            {
                FailPendingSave(
                    "The local draft changed while Qamel checked the connection. Try saving again.");
                return;
            }
            if (_controller.IsRunActive ||
                _controller.State == TestAuthoringControllerState.NeedsReview)
            {
                FailPendingSave(
                    "A local replay started while Qamel checked the connection. " +
                    "Finish or review it, then save again.");
                return;
            }

            if (draft.ReferenceEvidenceClip == null &&
                !TryAttachReferenceEvidence(
                    draft,
                    out var evidencePending,
                    out var evidenceError))
            {
                if (evidencePending)
                {
                    _saveRequestStatus = "Finalizing the reference gameplay video.";
                    return;
                }
                FailPendingSave(
                    evidenceError ?? "The reference gameplay video is unavailable.");
                return;
            }

            _pendingSaveDraftId = null;
            if (!StartSave(draft, out var error))
                _saveRequestError = error;
        }

        static void FailPendingSave(string error)
        {
            _pendingSaveDraftId = null;
            _saveRequestStatus = null;
            _saveRequestError = error;
        }

        static void RememberSuccessfulSave()
        {
            if (_saveOperation == null ||
                _saveOperation == _rememberedSaveOperation ||
                _saveOperation.State != TestDefinitionSaveState.Succeeded)
                return;

            TestLabPreferences.RememberLastSavedTest(
                TestAuthoringHealthCheck.ProjectId,
                TestAuthoringHealthCheck.ProjectName,
                _saveOperation);
            _rememberedSaveOperation = _saveOperation;
        }

        static void EnsureController()
        {
            if (!TestLabPreferences.IsEnabled || !EditorApplication.isPlaying)
            {
                DisposeController();
                return;
            }
            if (_controller != null)
                return;

            _sceneReloadProvider = new SceneReloadStateProvider(new UnitySceneReloadEnvironment());
            Registry.RegisterFallback(_sceneReloadProvider);
            _inputTrace = new NewInputTraceAdapter(
                captureAllowed: TestLabInputFocusPolicy.ShouldCaptureGameplayInput,
                selectReplayMode: CurrentReplayMode);
            _controller = new TestAuthoringController(Registry, _inputTrace);
            _lastObservedControllerState = _controller.State;
            _lastEditorTime = EditorApplication.timeSinceStartup;
        }

        static TestInputReplayMode CurrentReplayMode()
        {
            return Registry.TryGetProvider(out var registered)
                ? ReplayModeForProvider(registered.ProviderId)
                : TestInputReplayMode.RecordedFrames;
        }

        internal static TestInputReplayMode ReplayModeForProvider(string providerId)
        {
            // The plug-and-play fallback favors normal wall-clock playback.
            // Frame-locked playback remains available to explicit integrations
            // whose deterministic simulation requirements justify the tradeoff.
            return string.Equals(
                providerId,
                SceneReloadStateProvider.Id,
                StringComparison.Ordinal)
                ? TestInputReplayMode.RecordedTiming
                : TestInputReplayMode.RecordedFrames;
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
                EnsureController();
            else if (state == PlayModeStateChange.ExitingPlayMode)
            {
                InterruptSaveForPlayModeExit();
                InterruptSavedReplayForPlayModeExit();
                ResetConnectedRunner(
                    "Local runner stopped with Play Mode. Re-enter Play Mode to resume.");
                DisposeController();
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                TestLabPreferences.ConsumeSaveInterruption();
                DisposeController();
            }
        }

        static void InterruptSavedReplayForPlayModeExit()
        {
            if (IsSavedReplayBusy)
            {
                _savedReplayOperation.Cancel();
                _savedReplayRequestError =
                    "Saved Test download stopped because Play Mode ended. " +
                    "Enter Play Mode and try replaying the version again.";
            }
            ClearSavedReplayOperation();
            ClearLoadedSavedVersion();
        }

        static void ClearSavedReplayOperation()
        {
            _savedReplayOperation?.Dispose();
            _savedReplayOperation = null;
            _handledSavedReplayOperation = null;
        }

        static void InterruptSaveForPlayModeExit()
        {
            if (!IsSaveBusy) return;

            _saveOperation?.Cancel();
            _pendingSaveDraftId = null;
            _saveRequestStatus = null;
            _saveRequestError =
                "Save stopped because Play Mode ended. Enter Play Mode, recreate the " +
                "draft, and keep Play Mode active until Qamel confirms the save.";
            TestLabPreferences.RememberSaveInterruption(_saveRequestError);
        }

        static void DisposeController()
        {
            ClearDemonstrationStartOverlay();
            SimulationPause.Release();
            _referenceEvidenceCapture?.Dispose();
            _referenceEvidenceCapture = null;
            _replayEvidenceCapture?.Dispose();
            _replayEvidenceCapture = null;
            _controller?.Dispose();
            _controller = null;
            if (_sceneReloadProvider != null) Registry.Unregister(_sceneReloadProvider);
            _sceneReloadProvider = null;
            _inputTrace = null;
            _lastObservedControllerState = TestAuthoringControllerState.Idle;
            _lastEditorTime = 0;
            ClearLoadedSavedVersion();
        }

        static void ClearLoadedSavedVersion()
        {
            _loadedSavedBinding = null;
            _loadedSavedTestId = null;
            _loadedSavedTestVersionId = null;
            _loadedSavedTestUrl = null;
            _loadedSavedVersionNumber = 0;
            _loadedSavedVersion = null;
            _loadedSavedTraceBytes = null;
        }

        static void ClearLoadedSavedVersionAfterDraftReplacement()
        {
            if (ShouldClearLoadedSavedVersionAfterDraftReplacement(
                    _loadedSavedBinding != null,
                    _controller?.CurrentDraft))
            {
                ClearLoadedSavedVersion();
            }
        }

        internal static bool ShouldClearLoadedSavedVersionAfterDraftReplacement(
            bool hasLoadedSavedVersion,
            RecordedTestDraft currentDraft)
        {
            return hasLoadedSavedVersion && currentDraft == null;
        }

        static bool LoadedSavedBindingIsCurrent()
        {
            return _loadedSavedBinding != null &&
                   TestAuthoringHealthCheck.IsConnected &&
                   _loadedSavedBinding.Matches(
                       TestLabPreferences.AuthoringFingerprint,
                       TestAuthoringHealthCheck.ProjectId,
                       SavedTestCatalog,
                       _controller?.CurrentDraft?.DraftId);
        }

        internal static string SavedReplayBindingBlockReason(
            bool currentDraftIsSavedVersion,
            bool bindingIsCurrent)
        {
            if (!currentDraftIsSavedVersion || bindingIsCurrent) return null;
            return "This loaded saved Test belongs to an earlier connection or Test list. " +
                   "Clear it, check the connection, and load the saved Test again.";
        }

        internal static string SavedReplayLocalStateBlockReason(
            TestAuthoringController controller)
        {
            if (controller == null)
                return "Enable Test Lab and enter Play Mode before replaying a saved Test Version.";
            if (controller.State == TestAuthoringControllerState.Idle)
                return null;
            if (controller.CurrentDraft != null)
                return "Clear the current draft before replaying another saved Test Version.";
            return "A local authoring flow is already in progress. Finish it or restart Test Lab " +
                   "before replaying a saved Test Version.";
        }

        internal static string SavedReplayPreflightBlockReason(
            SavedTestVersion version,
            byte[] traceBytes,
            TestStateRegistry registry,
            ISavedTestReplayEnvironment environment)
        {
            if (version == null || traceBytes == null)
            {
                return "The exact downloaded saved Test is no longer available. Clear it and " +
                       "load the Test Version again.";
            }

            var compatibility = SavedTestReplayCompatibility.Evaluate(
                version,
                registry,
                environment);
            if (!compatibility.IsCompatible)
            {
                return "The local replay environment changed. " + compatibility.Reason;
            }

            var traceCompatibility = SavedTestReplayCompatibility.ValidateDownloadedTrace(
                version,
                traceBytes,
                environment);
            return traceCompatibility.IsCompatible
                ? null
                : "The local input setup changed. " + traceCompatibility.Reason;
        }

        static void OnQuitting()
        {
            BrowserCreationBackgroundExecution.Release();
            Shutdown();
        }

        static void Shutdown()
        {
            if (IsSaveBusy)
            {
                TestLabPreferences.RememberSaveInterruption(
                    "Save stopped because Unity reloaded or closed before it finished. " +
                    "Enter Play Mode, recreate the draft, and save again.");
            }
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.update -= Update;
            EditorApplication.quitting -= OnQuitting;
            _saveOperation?.Dispose();
            _saveOperation = null;
            _rememberedSaveOperation = null;
            _pendingSaveDraftId = null;
            _saveRequestStatus = null;
            _saveRequestError = null;
            _catalogOperation?.Dispose();
            _catalogOperation = null;
            _savedReplayOperation?.Dispose();
            _savedReplayOperation = null;
            _handledSavedReplayOperation = null;
            _savedReplayRequestError = null;
            ResetBrowserTestCreation();
            ResetConnectedRunner();
            DisposeController();
        }
    }
}
