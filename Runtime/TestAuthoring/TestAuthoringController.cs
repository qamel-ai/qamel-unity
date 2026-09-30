using System;

namespace QamelCapture.TestAuthoring
{
    internal enum TestAuthoringControllerState
    {
        Idle,
        CapturingStartingState,
        ReadyForDemonstration,
        PreparingDemonstration,
        Recording,
        DraftReady,
        Restoring,
        Settling,
        ReplayCountdown,
        ReplayRunning,
        NeedsReview,
        ReplayError,
        ReplayCancelled,
        AnchorExpired,
        Error,
        Disposed
    }

    /// <summary>
    /// UI-independent controller for one state anchor, bounded background input,
    /// and repeated local validation runs.
    /// </summary>
    internal sealed class TestAuthoringController : IDisposable
    {
        readonly TestStateOperationCoordinator _stateOperations;
        readonly TestStateRegistry _stateRegistry;
        readonly ITestInputTraceAdapter _inputTrace;
        readonly TestEvidenceBoundary _evidenceBoundary;
        readonly Func<int> _frameCounter;
        TestStateOperation _captureOperation;
        TestStateOperation _restoreOperation;
        bool _hasDemonstrationStart;
        double _demonstrationStart;
        bool _hasReplayStart;
        double _replayStart;
        long _captureRegistryRevision;
        long _anchorRegistryRevision;
        TestAuthoringControllerState _stateBeforeCapture;
        bool _resumePreviousTrace;
        bool _capturePausedForRun;
        bool _inputReplayAttemptedForRun;
        int _settleStartFrame;
        int _settleFrames;
        float _countdownSeconds;
        float _replayTimeoutSeconds;
        float _replayElapsedSeconds;
        bool _disposed;

        public TestAuthoringController(
            TestStateRegistry stateRegistry,
            ITestInputTraceAdapter inputTrace,
            TestEvidenceBoundary evidenceBoundary = null,
            Func<int> frameCounter = null)
        {
            if (stateRegistry == null)
                throw new ArgumentNullException(nameof(stateRegistry));
            _stateRegistry = stateRegistry;
            _inputTrace = inputTrace ?? throw new ArgumentNullException(nameof(inputTrace));
            _stateOperations = new TestStateOperationCoordinator(stateRegistry);
            _evidenceBoundary = evidenceBoundary ?? TestEvidenceBoundary.CurrentCaptureSession();
            _frameCounter = frameCounter ?? (() => UnityEngine.Time.frameCount);
        }

        public TestAuthoringControllerState State { get; private set; } =
            TestAuthoringControllerState.Idle;

        internal static bool RequiresPausedSimulation(TestAuthoringControllerState state)
        {
            return state == TestAuthoringControllerState.ReadyForDemonstration ||
                   state == TestAuthoringControllerState.PreparingDemonstration ||
                   state == TestAuthoringControllerState.Restoring ||
                   state == TestAuthoringControllerState.Settling ||
                   state == TestAuthoringControllerState.ReplayCountdown;
        }
        public TestStateAnchor CurrentAnchor { get; private set; }
        public RecordedTestDraft CurrentDraft { get; private set; }
        public LocalTestRun CurrentRun { get; private set; }
        public TestInputTraceErrorCode LastInputErrorCode { get; private set; }
        public TestStateErrorCode LastStateErrorCode { get; private set; }
        public TestOutcomeErrorCode LastOutcomeErrorCode { get; private set; }
        public string LastError { get; private set; }
        public TestEvidenceRange LastReplayEvidence { get; private set; }
        public TestInputTraceMetrics InputMetrics => _inputTrace.Metrics;
        public TestInputReplayDiagnostics ReplayDiagnostics => _inputTrace.ReplayDiagnostics;
        public bool IsBackgroundRecording => _inputTrace.Status == TestInputTraceStatus.Capturing;
        public bool CanCreateDraft => State == TestAuthoringControllerState.Recording &&
                                      !_inputTrace.Metrics.OverwroteEvents;
        public float ReplayCountdownRemainingSeconds { get; private set; }
        public bool IsRunActive => State == TestAuthoringControllerState.Restoring ||
                                   State == TestAuthoringControllerState.Settling ||
                                   State == TestAuthoringControllerState.ReplayCountdown ||
                                   State == TestAuthoringControllerState.ReplayRunning;

        public TestStateOperation CaptureStartingState(float timeoutSeconds)
        {
            ThrowIfDisposed();
            if (State == TestAuthoringControllerState.CapturingStartingState ||
                State == TestAuthoringControllerState.PreparingDemonstration ||
                IsRunActive || State == TestAuthoringControllerState.NeedsReview)
            {
                return ImmediateStateFailure(TestStateErrorCode.Busy,
                    "The authoring controller is busy.");
            }

            _stateBeforeCapture = State;
            _resumePreviousTrace = CurrentAnchor != null &&
                                   _inputTrace.Status == TestInputTraceStatus.Capturing &&
                                   CanResumePreviousState(State);
            if (_resumePreviousTrace)
            {
                var pause = _inputTrace.PauseCapture();
                if (!pause.Succeeded)
                {
                    SetInputError(pause);
                    return ImmediateStateFailure(TestStateErrorCode.ProviderError, pause.Error);
                }
            }
            else
            {
                _inputTrace.StopCapture();
            }
            ClearError();

            _captureRegistryRevision = _stateRegistry.Revision;
            _captureOperation = _stateOperations.Capture(timeoutSeconds);
            var operation = _captureOperation;
            if (_captureOperation.Status == TestStateOperationStatus.Pending)
                State = TestAuthoringControllerState.CapturingStartingState;
            else
                FinishStateCapture();
            return operation;
        }

        public void Tick(float unscaledDeltaSeconds)
        {
            ThrowIfDisposed();
            if (unscaledDeltaSeconds < 0 || float.IsNaN(unscaledDeltaSeconds) ||
                float.IsInfinity(unscaledDeltaSeconds))
                throw new ArgumentOutOfRangeException(nameof(unscaledDeltaSeconds));

            if (State == TestAuthoringControllerState.CapturingStartingState &&
                _captureOperation != null)
            {
                _stateOperations.Tick(unscaledDeltaSeconds);
                if (_captureOperation.IsFinished)
                    FinishStateCapture();
                return;
            }

            if (State == TestAuthoringControllerState.PreparingDemonstration)
            {
                TickDemonstrationRestore(unscaledDeltaSeconds);
                return;
            }

            if (ProviderChangedBeforeInputReplay())
            {
                StopRunForProviderChange();
                return;
            }

            if (IsRunActive && CurrentRun != null)
                CurrentRun.TotalDurationSeconds += unscaledDeltaSeconds;

            switch (State)
            {
                case TestAuthoringControllerState.Restoring:
                    TickRestore(unscaledDeltaSeconds);
                    break;
                case TestAuthoringControllerState.Settling:
                    TickSettle(unscaledDeltaSeconds);
                    break;
                case TestAuthoringControllerState.ReplayCountdown:
                    TickCountdown(unscaledDeltaSeconds);
                    break;
                case TestAuthoringControllerState.ReplayRunning:
                    TickReplay(unscaledDeltaSeconds);
                    break;
                default:
                    CheckAnchorRegistration();
                    break;
            }
        }

        public bool CancelStateCapture()
        {
            ThrowIfDisposed();
            if (State != TestAuthoringControllerState.CapturingStartingState ||
                _captureOperation == null || !_stateOperations.Cancel())
                return false;

            FinishStateCapture();
            return true;
        }

        public bool StartDemonstration(float restoreTimeoutSeconds)
        {
            ThrowIfDisposed();
            if (State != TestAuthoringControllerState.ReadyForDemonstration)
                return SetControllerError(
                    "Capture a starting state before beginning the demonstration.");
            if (restoreTimeoutSeconds <= 0 || float.IsNaN(restoreTimeoutSeconds) ||
                float.IsInfinity(restoreTimeoutSeconds))
                throw new ArgumentOutOfRangeException(nameof(restoreTimeoutSeconds));
            if (_anchorRegistryRevision != _stateRegistry.Revision)
            {
                LastStateErrorCode = TestStateErrorCode.IncompatibleAnchor;
                LastError = "The state provider changed after this anchor was captured. " +
                            "Capture a new starting state.";
                State = TestAuthoringControllerState.AnchorExpired;
                return false;
            }

            ClearError();
            State = TestAuthoringControllerState.PreparingDemonstration;
            _restoreOperation = _stateOperations.Restore(
                CurrentAnchor,
                restoreTimeoutSeconds);
            if (_restoreOperation.IsFinished)
                FinishDemonstrationRestore();
            return State != TestAuthoringControllerState.Error &&
                   State != TestAuthoringControllerState.AnchorExpired;
        }

        public bool TryCreateDraft(
            string name,
            string expectedOutcome,
            out RecordedTestDraft draft)
        {
            ThrowIfDisposed();
            draft = null;
            if (State != TestAuthoringControllerState.Recording)
                return SetControllerError("A valid state anchor and active input recording are required.");
            if (string.IsNullOrWhiteSpace(name))
                return SetControllerError("Enter a test name before creating the draft.");
            if (string.IsNullOrWhiteSpace(expectedOutcome))
                return SetControllerError("Describe the expected outcome before creating the draft.");

            var snapshot = _inputTrace.PauseAndSnapshot();
            if (!snapshot.Succeeded)
            {
                LastInputErrorCode = snapshot.ErrorCode;
                LastError = snapshot.Error;
                State = snapshot.ErrorCode == TestInputTraceErrorCode.Overwritten
                    ? TestAuthoringControllerState.AnchorExpired
                    : TestAuthoringControllerState.Error;
                return false;
            }

            var hasEnd = _evidenceBoundary.TryReadSessionTime(out var demonstrationEnd);
            var hasRange = _hasDemonstrationStart && hasEnd;
            var evidence = new TestEvidenceRange(
                hasRange,
                hasRange ? _demonstrationStart : 0,
                hasRange ? demonstrationEnd : 0);
            if (!TryCaptureExpectedObservation(out var expectedObservation))
            {
                var resumeAfterObservationError = _inputTrace.ResumeCapture();
                if (!resumeAfterObservationError.Succeeded)
                {
                    LastInputErrorCode = resumeAfterObservationError.ErrorCode;
                    LastError += " Background input capture also could not resume: " +
                                 resumeAfterObservationError.Error;
                }
                State = TestAuthoringControllerState.Error;
                return false;
            }
            var candidate = new RecordedTestDraft(
                name,
                expectedOutcome,
                CurrentAnchor,
                snapshot.Snapshot,
                evidence,
                expectedObservation);

            var resume = _inputTrace.ResumeCapture();
            if (!resume.Succeeded)
            {
                LastInputErrorCode = resume.ErrorCode;
                LastError = "The input snapshot was created, but background input capture did not resume: " +
                            resume.Error;
                State = TestAuthoringControllerState.Error;
                return false;
            }

            draft = candidate;
            CurrentDraft = candidate;
            CurrentRun = null;
            ClearError();
            State = TestAuthoringControllerState.DraftReady;
            return true;
        }

        /// <summary>
        /// Loads a fully validated, immutable draft reconstructed by Editor tooling.
        /// Network, checksum, adapter, and environment compatibility checks remain
        /// outside this runtime controller. This method binds the draft to the
        /// currently registered provider revision so the existing stale-provider
        /// guard remains authoritative during replay.
        /// </summary>
        internal bool TryLoadDraftForReplay(RecordedTestDraft draft)
        {
            ThrowIfDisposed();
            if (draft == null)
                throw new ArgumentNullException(nameof(draft));
            if (State != TestAuthoringControllerState.Idle)
            {
                return SetControllerError(
                    "A saved Test can be loaded only from a clean, idle Test Lab.");
            }
            if (!_stateRegistry.TryGetProvider(out var provider))
            {
                LastStateErrorCode = TestStateErrorCode.MissingProvider;
                LastError = "Register the saved Test's state provider before replay.";
                return false;
            }
            if (!string.Equals(
                    provider.ProviderId,
                    draft.Anchor.ProviderId,
                    StringComparison.Ordinal))
            {
                LastStateErrorCode = TestStateErrorCode.IncompatibleAnchor;
                LastError = "The active state provider is '" + provider.ProviderId +
                            "', but this saved Test requires '" +
                            draft.Anchor.ProviderId + "'.";
                return false;
            }
            if (provider.StateFormatVersion != draft.Anchor.StateFormatVersion)
            {
                LastStateErrorCode = TestStateErrorCode.IncompatibleAnchor;
                LastError = "The active state provider format is v" +
                            provider.StateFormatVersion +
                            ", but this saved Test requires v" +
                            draft.Anchor.StateFormatVersion + ".";
                return false;
            }

            _inputTrace.StopCapture();
            var start = _inputTrace.StartCapture();
            if (!start.Succeeded)
                return SetInputError(start);

            CurrentAnchor = draft.Anchor;
            CurrentDraft = draft;
            CurrentRun = null;
            LastReplayEvidence = null;
            _anchorRegistryRevision = _stateRegistry.Revision;
            _hasDemonstrationStart = false;
            _hasReplayStart = false;
            ClearError();
            State = TestAuthoringControllerState.DraftReady;
            return true;
        }

        public bool StartReplay(
            float restoreTimeoutSeconds,
            float replayTimeoutSeconds,
            float countdownSeconds,
            int settleFrames = 1)
        {
            ThrowIfDisposed();
            if (!CanStartReplay(State) || CurrentDraft == null)
                return SetControllerError("A ready draft is required before replay.");
            if (restoreTimeoutSeconds <= 0 || replayTimeoutSeconds <= 0 ||
                countdownSeconds < 0 || settleFrames < 1)
                throw new ArgumentOutOfRangeException(
                    nameof(restoreTimeoutSeconds),
                    "Replay timing values are invalid.");
            if (_anchorRegistryRevision != _stateRegistry.Revision)
            {
                LastStateErrorCode = TestStateErrorCode.IncompatibleAnchor;
                LastError = "The state provider changed after this anchor was captured. " +
                            "Capture a new starting state.";
                State = TestAuthoringControllerState.AnchorExpired;
                return false;
            }

            ClearError();
            CurrentRun = CurrentDraft.AddRun();
            _countdownSeconds = countdownSeconds;
            _replayTimeoutSeconds = replayTimeoutSeconds;
            _replayElapsedSeconds = 0;
            _settleFrames = settleFrames;
            ReplayCountdownRemainingSeconds = countdownSeconds;
            LastReplayEvidence = null;
            _hasReplayStart = false;
            _inputReplayAttemptedForRun = false;

            var pause = _inputTrace.PauseCapture();
            if (!pause.Succeeded)
            {
                FailRunForInput(pause.ErrorCode, pause.Error);
                return false;
            }
            _capturePausedForRun = true;

            State = TestAuthoringControllerState.Restoring;
            _restoreOperation = _stateOperations.Restore(
                CurrentDraft.Anchor,
                restoreTimeoutSeconds);
            if (_restoreOperation.IsFinished)
                FinishStateRestore();
            return State != TestAuthoringControllerState.ReplayError;
        }

        public bool CancelRun()
        {
            ThrowIfDisposed();
            if (!IsRunActive || CurrentRun == null)
                return false;

            if (State == TestAuthoringControllerState.Restoring &&
                _stateOperations.HasPendingOperation)
                _stateOperations.Cancel();
            if (State == TestAuthoringControllerState.ReplayRunning)
                _inputTrace.CancelReplay();

            EndReplayEvidence();
            if (_inputReplayAttemptedForRun)
                CurrentRun.AdapterDiagnostics = _inputTrace.ReplayDiagnostics;
            CurrentRun.Status = LocalTestRunStatus.Cancelled;
            if (CurrentRun.StateStageStatus == LocalTestStageStatus.Running)
                CurrentRun.StateStageStatus = LocalTestStageStatus.Cancelled;
            if (CurrentRun.ActionStageStatus == LocalTestStageStatus.Running ||
                CurrentRun.ActionStageStatus == LocalTestStageStatus.Pending)
                CurrentRun.ActionStageStatus = LocalTestStageStatus.Cancelled;
            CurrentRun.CheckStageStatus = LocalTestStageStatus.Cancelled;
            CurrentRun.Error = null;
            CurrentRun.CompletedAtUtc = DateTime.UtcNow;
            ClearError();
            FinishRunAndResumeCapture(TestAuthoringControllerState.ReplayCancelled);
            return true;
        }

        public bool RecordHumanVerdict(TestHumanVerdict verdict, string note)
        {
            ThrowIfDisposed();
            if (State != TestAuthoringControllerState.NeedsReview || CurrentRun == null ||
                CurrentRun.Status != LocalTestRunStatus.AwaitingReview)
                return SetControllerError("A completed replay is required before review.");

            CurrentRun.ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            switch (verdict)
            {
                case TestHumanVerdict.Worked:
                    CurrentRun.Status = LocalTestRunStatus.Pass;
                    CurrentRun.CheckStageStatus = LocalTestStageStatus.Pass;
                    break;
                case TestHumanVerdict.DidNotWork:
                    CurrentRun.Status = LocalTestRunStatus.Fail;
                    CurrentRun.CheckStageStatus = LocalTestStageStatus.Fail;
                    break;
                case TestHumanVerdict.CouldNotTell:
                    CurrentRun.Status = LocalTestRunStatus.NeedsReview;
                    CurrentRun.CheckStageStatus = LocalTestStageStatus.NeedsReview;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(verdict));
            }

            CurrentRun.CompletedAtUtc = DateTime.UtcNow;
            ClearError();
            State = TestAuthoringControllerState.DraftReady;
            return true;
        }

        public bool ReviseDraftMetadata(string name, string expectedOutcome)
        {
            ThrowIfDisposed();
            if (State != TestAuthoringControllerState.DraftReady || CurrentDraft == null)
                return SetControllerError("A completed local draft is required before editing its details.");
            if (CurrentDraft.ValidationHistory.Count > 0)
                return SetControllerError("Validated draft details cannot be changed. Record a new draft instead.");
            try
            {
                CurrentDraft.ReviseMetadata(name, expectedOutcome);
                ClearError();
                return true;
            }
            catch (ArgumentException exception)
            {
                return SetControllerError(exception.Message);
            }
        }

        public bool ClearDraft()
        {
            ThrowIfDisposed();
            if ((State != TestAuthoringControllerState.DraftReady &&
                 State != TestAuthoringControllerState.ReplayError &&
                 State != TestAuthoringControllerState.ReplayCancelled &&
                 State != TestAuthoringControllerState.AnchorExpired &&
                 State != TestAuthoringControllerState.Error) ||
                CurrentDraft == null)
                return false;

            bool resetToIdle = State == TestAuthoringControllerState.AnchorExpired ||
                               State == TestAuthoringControllerState.Error;
            CurrentDraft = null;
            CurrentRun = null;
            LastReplayEvidence = null;
            if (resetToIdle)
            {
                _inputTrace.StopCapture();
                CurrentAnchor = null;
            }
            ClearError();
            State = resetToIdle
                ? TestAuthoringControllerState.Idle
                : TestAuthoringControllerState.Recording;
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            if (_stateOperations.HasPendingOperation)
                _stateOperations.Cancel();
            if (_inputTrace.ReplayStatus == TestInputReplayStatus.Replaying)
                _inputTrace.CancelReplay();
            _inputTrace.Dispose();
            _disposed = true;
            State = TestAuthoringControllerState.Disposed;
        }

        void TickRestore(float unscaledDeltaSeconds)
        {
            if (_restoreOperation == null)
            {
                FailRunForState(TestStateErrorCode.ProviderError,
                    "The state restore operation was lost.");
                return;
            }

            _stateOperations.Tick(unscaledDeltaSeconds);
            CurrentRun.StateDurationSeconds = _restoreOperation.DurationSeconds;
            if (_restoreOperation.IsFinished)
                FinishStateRestore();
        }

        void TickDemonstrationRestore(float unscaledDeltaSeconds)
        {
            if (_restoreOperation == null)
            {
                LastStateErrorCode = TestStateErrorCode.ProviderError;
                LastError = "The demonstration starting-state restore operation was lost.";
                State = TestAuthoringControllerState.Error;
                return;
            }
            if (_anchorRegistryRevision != _stateRegistry.Revision)
            {
                if (_stateOperations.HasPendingOperation)
                    _stateOperations.Cancel();
                _restoreOperation = null;
                LastStateErrorCode = TestStateErrorCode.IncompatibleAnchor;
                LastError = "The state provider changed while the starting state was restored. " +
                            "Capture a new starting state.";
                State = TestAuthoringControllerState.AnchorExpired;
                return;
            }

            _stateOperations.Tick(unscaledDeltaSeconds);
            if (_restoreOperation.IsFinished)
                FinishDemonstrationRestore();
        }

        void TickSettle(float unscaledDeltaSeconds)
        {
            CurrentRun.SettleDurationSeconds += unscaledDeltaSeconds;
            if (_frameCounter() - _settleStartFrame < _settleFrames)
                return;

            CurrentRun.Status = LocalTestRunStatus.Countdown;
            State = TestAuthoringControllerState.ReplayCountdown;
            if (_countdownSeconds <= 0)
                StartInputReplay();
        }

        void TickCountdown(float unscaledDeltaSeconds)
        {
            CurrentRun.CountdownDurationSeconds += unscaledDeltaSeconds;
            ReplayCountdownRemainingSeconds = Math.Max(
                0,
                ReplayCountdownRemainingSeconds - unscaledDeltaSeconds);
            if (ReplayCountdownRemainingSeconds <= 0)
                StartInputReplay();
        }

        void TickReplay(float unscaledDeltaSeconds)
        {
            CurrentRun.ActionDurationSeconds += unscaledDeltaSeconds;
            _replayElapsedSeconds += unscaledDeltaSeconds;
            CurrentRun.AdapterDiagnostics = _inputTrace.ReplayDiagnostics;

            if (_inputTrace.ReplayStatus == TestInputReplayStatus.Completed)
            {
                CompleteReplayAction();
                return;
            }
            if (_inputTrace.ReplayStatus == TestInputReplayStatus.Error)
            {
                FailRunForInput(_inputTrace.ReplayErrorCode, _inputTrace.ReplayError);
                return;
            }
            if (_replayElapsedSeconds < _replayTimeoutSeconds)
                return;

            _inputTrace.CancelReplay();
            FailRunForInput(
                TestInputTraceErrorCode.ReplayTimeout,
                $"Input replay timed out after {_replayTimeoutSeconds:0.###} seconds.");
        }

        void FinishStateCapture()
        {
            if (_captureOperation == null)
                return;

            if (_captureOperation.Status != TestStateOperationStatus.Succeeded ||
                _captureOperation.Anchor == null ||
                _captureRegistryRevision != _stateRegistry.Revision)
            {
                LastStateErrorCode = _captureRegistryRevision != _stateRegistry.Revision
                    ? TestStateErrorCode.IncompatibleAnchor
                    : _captureOperation.ErrorCode;
                LastError = _captureRegistryRevision != _stateRegistry.Revision
                    ? "The state provider changed while the starting state was being captured."
                    : _captureOperation.Error;
                _captureOperation = null;
                RestorePreviousTraceOrError();
                return;
            }

            _inputTrace.StopCapture();
            CurrentAnchor = _captureOperation.Anchor;
            CurrentDraft = null;
            CurrentRun = null;
            LastReplayEvidence = null;
            _anchorRegistryRevision = _stateRegistry.Revision;
            _captureOperation = null;
            _hasDemonstrationStart = false;
            _demonstrationStart = 0;
            ClearError();
            State = TestAuthoringControllerState.ReadyForDemonstration;
            _resumePreviousTrace = false;
        }

        void FinishStateRestore()
        {
            if (_restoreOperation == null)
                return;

            CurrentRun.StateDurationSeconds = _restoreOperation.DurationSeconds;
            if (_restoreOperation.Status == TestStateOperationStatus.Cancelled)
            {
                CancelRun();
                return;
            }
            if (_anchorRegistryRevision != _stateRegistry.Revision)
            {
                StopRunForProviderChange();
                return;
            }
            if (_restoreOperation.Status != TestStateOperationStatus.Succeeded ||
                !_restoreOperation.ReadyForReplay)
            {
                FailRunForState(_restoreOperation.ErrorCode, _restoreOperation.Error);
                return;
            }

            CurrentRun.StateStageStatus = LocalTestStageStatus.Succeeded;
            CurrentRun.Status = LocalTestRunStatus.Settling;
            _settleStartFrame = _frameCounter();
            _restoreOperation = null;
            State = TestAuthoringControllerState.Settling;
        }

        void FinishDemonstrationRestore()
        {
            if (_restoreOperation == null)
                return;

            if (_anchorRegistryRevision != _stateRegistry.Revision)
            {
                _restoreOperation = null;
                LastStateErrorCode = TestStateErrorCode.IncompatibleAnchor;
                LastError = "The state provider changed while the starting state was restored. " +
                            "Capture a new starting state.";
                State = TestAuthoringControllerState.AnchorExpired;
                return;
            }
            if (_restoreOperation.Status != TestStateOperationStatus.Succeeded ||
                !_restoreOperation.ReadyForReplay)
            {
                LastStateErrorCode = _restoreOperation.ErrorCode;
                LastError = _restoreOperation.Error;
                _restoreOperation = null;
                State = TestAuthoringControllerState.Error;
                return;
            }

            _restoreOperation = null;
            var start = _inputTrace.StartCapture();
            if (!start.Succeeded)
            {
                SetInputError(start);
                return;
            }

            _hasDemonstrationStart =
                _evidenceBoundary.TryReadSessionTime(out _demonstrationStart);
            ClearError();
            State = TestAuthoringControllerState.Recording;
        }

        void StartInputReplay()
        {
            if (State != TestAuthoringControllerState.ReplayCountdown)
                return;
            if (_anchorRegistryRevision != _stateRegistry.Revision)
            {
                StopRunForProviderChange();
                return;
            }

            _hasReplayStart = _evidenceBoundary.TryReadSessionTime(out _replayStart);
            _inputReplayAttemptedForRun = true;
            var start = _inputTrace.StartReplay(CurrentDraft.InputTrace);
            if (!start.Succeeded)
            {
                EndReplayEvidence();
                FailRunForInput(start.ErrorCode, start.Error);
                return;
            }

            CurrentRun.Status = LocalTestRunStatus.Replaying;
            CurrentRun.ActionStageStatus = LocalTestStageStatus.Running;
            CurrentRun.AdapterDiagnostics = _inputTrace.ReplayDiagnostics;
            _replayElapsedSeconds = 0;
            State = TestAuthoringControllerState.ReplayRunning;
        }

        void CompleteReplayAction()
        {
            EndReplayEvidence();
            CurrentRun.AdapterDiagnostics = _inputTrace.ReplayDiagnostics;
            CurrentRun.ActionStageStatus = LocalTestStageStatus.Succeeded;
            CurrentRun.CheckStageStatus = LocalTestStageStatus.Running;

            if (CurrentDraft.ExpectedObservation != null)
            {
                CompleteAutomatedOutcomeCheck();
                return;
            }

            CurrentRun.Status = LocalTestRunStatus.AwaitingReview;
            if (!FinishRunAndResumeCapture(TestAuthoringControllerState.NeedsReview))
                return;

            ClearError();
        }

        void CompleteAutomatedOutcomeCheck()
        {
            if (!_stateRegistry.TryGetProvider(out var registered) ||
                !(registered.Provider is IQamelTestOutcomeProvider provider))
            {
                FailRunForOutcome(
                    TestOutcomeErrorCode.MissingProvider,
                    "The saved Test requires its game-owned outcome provider, but the " +
                    "active state provider does not expose one.");
                return;
            }

            var expected = CurrentDraft.ExpectedObservation;
            if (!string.Equals(
                    registered.ProviderId,
                    expected.ProviderId,
                    StringComparison.Ordinal))
            {
                FailRunForOutcome(
                    TestOutcomeErrorCode.IncompatibleProvider,
                    "The active outcome provider belongs to '" + registered.ProviderId +
                    "', but this Test requires '" + expected.ProviderId + "'.");
                return;
            }

            int formatVersion;
            TestOutcomeObservation actual;
            try
            {
                formatVersion = provider.OutcomeFormatVersion;
                actual = provider.ObserveOutcome();
            }
            catch (Exception exception)
            {
                FailRunForOutcome(
                    TestOutcomeErrorCode.ProviderError,
                    "The game-owned outcome provider failed: " + exception.Message);
                return;
            }

            if (formatVersion <= 0 || actual == null)
            {
                FailRunForOutcome(
                    TestOutcomeErrorCode.InvalidObservation,
                    "The game-owned outcome provider returned an invalid observation.");
                return;
            }
            if (formatVersion != expected.FormatVersion)
            {
                FailRunForOutcome(
                    TestOutcomeErrorCode.IncompatibleProvider,
                    "The outcome provider uses format v" + formatVersion +
                    ", but this Test requires v" + expected.FormatVersion + ".");
                return;
            }

            var comparison = new TestOutcomeComparison(expected.Observation, actual);
            CurrentRun.OutcomeComparison = comparison;
            if (comparison.Matches)
            {
                CurrentRun.Status = LocalTestRunStatus.Pass;
                CurrentRun.CheckStageStatus = LocalTestStageStatus.Pass;
            }
            else
            {
                CurrentRun.Status = LocalTestRunStatus.Fail;
                CurrentRun.CheckStageStatus = LocalTestStageStatus.Fail;
            }
            CurrentRun.CompletedAtUtc = DateTime.UtcNow;
            if (!FinishRunAndResumeCapture(TestAuthoringControllerState.DraftReady))
                return;
            ClearError();
        }

        bool TryCaptureExpectedObservation(
            out TestExpectedOutcomeObservation expectedObservation)
        {
            expectedObservation = null;
            if (!_stateRegistry.TryGetProvider(out var registered) ||
                !(registered.Provider is IQamelTestOutcomeProvider provider))
                return true;

            try
            {
                int formatVersion = provider.OutcomeFormatVersion;
                var observation = provider.ObserveOutcome();
                if (formatVersion <= 0 || observation == null)
                {
                    LastOutcomeErrorCode = TestOutcomeErrorCode.InvalidObservation;
                    LastError = "The game-owned outcome provider returned an invalid observation.";
                    return false;
                }
                expectedObservation = new TestExpectedOutcomeObservation(
                    registered.ProviderId,
                    formatVersion,
                    observation);
                return true;
            }
            catch (Exception exception)
            {
                LastOutcomeErrorCode = TestOutcomeErrorCode.ProviderError;
                LastError = "The game-owned outcome provider failed while the expected " +
                            "result was captured: " + exception.Message;
                return false;
            }
        }

        void FailRunForState(TestStateErrorCode code, string error)
        {
            LastStateErrorCode = code;
            LastError = string.IsNullOrWhiteSpace(error)
                ? "The starting state could not be restored."
                : error;
            CurrentRun.StateErrorCode = code;
            CurrentRun.StateStageStatus = LocalTestStageStatus.Error;
            CurrentRun.ActionStageStatus = LocalTestStageStatus.Pending;
            CurrentRun.CheckStageStatus = LocalTestStageStatus.Pending;
            FinishRunError();
        }

        void FailRunForInput(TestInputTraceErrorCode code, string error)
        {
            LastInputErrorCode = code;
            LastError = string.IsNullOrWhiteSpace(error)
                ? "The recorded actions could not be replayed."
                : error;
            CurrentRun.InputErrorCode = code;
            if (CurrentRun.StateStageStatus == LocalTestStageStatus.Running)
                CurrentRun.StateStageStatus = LocalTestStageStatus.Pending;
            CurrentRun.ActionStageStatus = LocalTestStageStatus.Error;
            CurrentRun.CheckStageStatus = LocalTestStageStatus.Pending;
            FinishRunError();
        }

        void FailRunForOutcome(TestOutcomeErrorCode code, string error)
        {
            LastOutcomeErrorCode = code;
            LastError = string.IsNullOrWhiteSpace(error)
                ? "The expected game outcome could not be evaluated."
                : error;
            CurrentRun.OutcomeErrorCode = code;
            CurrentRun.CheckStageStatus = LocalTestStageStatus.Error;
            FinishRunError();
        }

        void FinishRunError()
        {
            EndReplayEvidence();
            if (_inputReplayAttemptedForRun)
                CurrentRun.AdapterDiagnostics = _inputTrace.ReplayDiagnostics;
            CurrentRun.Status = LocalTestRunStatus.Error;
            CurrentRun.Error = LastError;
            CurrentRun.CompletedAtUtc = DateTime.UtcNow;
            FinishRunAndResumeCapture(TestAuthoringControllerState.ReplayError);
        }

        bool FinishRunAndResumeCapture(TestAuthoringControllerState targetState)
        {
            _restoreOperation = null;
            ReplayCountdownRemainingSeconds = 0;
            if (!_capturePausedForRun)
            {
                State = targetState;
                return true;
            }

            var resume = _inputTrace.ResumeCapture();
            _capturePausedForRun = false;
            if (resume.Succeeded)
            {
                State = targetState;
                return true;
            }

            LastInputErrorCode = resume.ErrorCode;
            LastError = "Replay cleanup could not resume background input capture: " + resume.Error;
            CurrentRun.InputErrorCode = resume.ErrorCode;
            CurrentRun.Error = LastError;
            CurrentRun.Status = LocalTestRunStatus.Error;
            CurrentRun.ActionStageStatus = LocalTestStageStatus.Error;
            CurrentRun.CheckStageStatus = LocalTestStageStatus.Pending;
            CurrentRun.CompletedAtUtc = DateTime.UtcNow;
            State = TestAuthoringControllerState.ReplayError;
            return false;
        }

        void EndReplayEvidence()
        {
            if (!_hasReplayStart)
                return;

            var hasEnd = _evidenceBoundary.TryReadSessionTime(out var replayEnd);
            LastReplayEvidence = new TestEvidenceRange(
                hasEnd,
                hasEnd ? _replayStart : 0,
                hasEnd ? replayEnd : 0);
            CurrentRun.ReplayEvidence = LastReplayEvidence;
            _hasReplayStart = false;
        }

        void RestorePreviousTraceOrError()
        {
            if (!_resumePreviousTrace)
            {
                State = TestAuthoringControllerState.Error;
                return;
            }

            var resume = _inputTrace.ResumeCapture();
            _resumePreviousTrace = false;
            if (!resume.Succeeded)
            {
                LastInputErrorCode = resume.ErrorCode;
                LastError += " Previous input recording also could not resume: " + resume.Error;
                State = TestAuthoringControllerState.Error;
                return;
            }

            State = _stateBeforeCapture;
        }

        void CheckAnchorRegistration()
        {
            if (CurrentAnchor == null || !CanResumePreviousState(State) ||
                _anchorRegistryRevision == _stateRegistry.Revision)
                return;

            _inputTrace.StopCapture();
            LastStateErrorCode = TestStateErrorCode.IncompatibleAnchor;
            LastError = "The state provider changed after this anchor was captured. " +
                        "Capture a new starting state.";
            State = TestAuthoringControllerState.AnchorExpired;
        }

        bool ProviderChangedBeforeInputReplay()
        {
            return (State == TestAuthoringControllerState.Restoring ||
                    State == TestAuthoringControllerState.Settling ||
                    State == TestAuthoringControllerState.ReplayCountdown) &&
                   _anchorRegistryRevision != _stateRegistry.Revision;
        }

        void StopRunForProviderChange()
        {
            if (State == TestAuthoringControllerState.Restoring &&
                _stateOperations.HasPendingOperation)
            {
                _stateOperations.Cancel();
            }
            FailRunForState(
                TestStateErrorCode.IncompatibleAnchor,
                "The state provider changed before recorded actions began. " +
                "Replay stopped before input was applied.");
        }

        bool SetInputError(TestInputTraceResult result)
        {
            LastInputErrorCode = result.ErrorCode;
            LastError = result.Error;
            State = TestAuthoringControllerState.Error;
            return false;
        }

        bool SetControllerError(string error)
        {
            LastError = error;
            return false;
        }

        void ClearError()
        {
            LastInputErrorCode = TestInputTraceErrorCode.None;
            LastStateErrorCode = TestStateErrorCode.None;
            LastOutcomeErrorCode = TestOutcomeErrorCode.None;
            LastError = null;
        }

        void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(TestAuthoringController));
        }

        static bool CanStartReplay(TestAuthoringControllerState state)
        {
            return state == TestAuthoringControllerState.DraftReady ||
                   state == TestAuthoringControllerState.ReplayError ||
                   state == TestAuthoringControllerState.ReplayCancelled;
        }

        static bool CanResumePreviousState(TestAuthoringControllerState state)
        {
            return state == TestAuthoringControllerState.ReadyForDemonstration ||
                   state == TestAuthoringControllerState.Recording ||
                   state == TestAuthoringControllerState.DraftReady ||
                   state == TestAuthoringControllerState.ReplayError ||
                   state == TestAuthoringControllerState.ReplayCancelled;
        }

        static TestStateOperation ImmediateStateFailure(TestStateErrorCode code, string error)
        {
            return new TestStateOperation(TestStateOperationKind.Capture)
            {
                Status = TestStateOperationStatus.Failed,
                ErrorCode = code,
                Error = error
            };
        }
    }
}
