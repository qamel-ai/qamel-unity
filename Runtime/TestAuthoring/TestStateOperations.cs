using System;
using UnityEngine.SceneManagement;

namespace QamelCapture.TestAuthoring
{
    public enum TestStateOperationKind
    {
        Capture,
        Restore
    }

    public enum TestStateOperationStatus
    {
        Pending,
        Succeeded,
        Failed,
        Cancelled
    }

    public enum TestStateErrorCode
    {
        None,
        MissingProvider,
        DuplicateProvider,
        InvalidProvider,
        Busy,
        InvalidAnchor,
        IncompatibleAnchor,
        InvalidPayload,
        Timeout,
        Cancelled,
        ProviderException,
        ProviderError
    }

    public sealed class TestStateOperation
    {
        internal TestStateOperation(TestStateOperationKind kind)
        {
            Kind = kind;
            Status = TestStateOperationStatus.Pending;
        }

        public TestStateOperationKind Kind { get; }
        public TestStateOperationStatus Status { get; internal set; }
        public TestStateErrorCode ErrorCode { get; internal set; }
        public string Error { get; internal set; }
        public float DurationSeconds { get; internal set; }
        public TestStateAnchor Anchor { get; internal set; }
        public bool ReadyForReplay { get; internal set; }
        public bool IsFinished => Status != TestStateOperationStatus.Pending;
    }

    /// <summary>
    /// Drives one provider operation at a time. A later controller can tick this
    /// from Play Mode without depending on an Editor window.
    /// </summary>
    public sealed class TestStateOperationCoordinator
    {
        readonly TestStateRegistry _registry;
        TestStateOperation _active;
        TestStateCaptureContext _captureContext;
        TestStateRestoreContext _restoreContext;
        float _timeoutSeconds;

        public TestStateOperationCoordinator(TestStateRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public bool HasPendingOperation => _active != null && !_active.IsFinished;

        public TestStateOperation Capture(float timeoutSeconds)
        {
            ValidateTimeout(timeoutSeconds);
            if (HasPendingOperation)
                return ImmediateFailure(TestStateOperationKind.Capture, TestStateErrorCode.Busy,
                    "Another state operation is already running.");
            if (!_registry.TryGetProvider(out var provider))
                return ImmediateFailure(TestStateOperationKind.Capture, TestStateErrorCode.MissingProvider,
                    "No state provider is registered.");

            var operation = Begin(TestStateOperationKind.Capture, timeoutSeconds);
            var context = new TestStateCaptureContext(
                () => operation.IsFinished,
                (label, payload) => CompleteCapture(operation, provider, label, payload),
                error => FailProvider(operation, error));
            _captureContext = context;

            try
            {
                provider.Provider.CaptureState(context);
            }
            catch (Exception exception)
            {
                FailException(operation, exception);
            }

            return operation;
        }

        public TestStateOperation Restore(TestStateAnchor anchor, float timeoutSeconds)
        {
            ValidateTimeout(timeoutSeconds);
            if (HasPendingOperation)
                return ImmediateFailure(TestStateOperationKind.Restore, TestStateErrorCode.Busy,
                    "Another state operation is already running.");
            if (anchor == null)
                return ImmediateFailure(TestStateOperationKind.Restore, TestStateErrorCode.InvalidAnchor,
                    "A captured state anchor is required.");
            if (!_registry.TryGetProvider(out var provider))
                return ImmediateFailure(TestStateOperationKind.Restore, TestStateErrorCode.MissingProvider,
                    "No state provider is registered.");
            if (!string.Equals(anchor.ProviderId, provider.ProviderId, StringComparison.Ordinal) ||
                anchor.StateFormatVersion != provider.StateFormatVersion)
                return ImmediateFailure(TestStateOperationKind.Restore, TestStateErrorCode.IncompatibleAnchor,
                    "The captured state does not match the active provider and format version.");

            var operation = Begin(TestStateOperationKind.Restore, timeoutSeconds);
            var context = new TestStateRestoreContext(
                anchor.GetPayloadCopy(),
                () => operation.IsFinished,
                () => CompleteRestore(operation),
                error => FailProvider(operation, error));
            _restoreContext = context;

            try
            {
                provider.Provider.RestoreState(context);
            }
            catch (Exception exception)
            {
                FailException(operation, exception);
            }

            return operation;
        }

        public void Tick(float unscaledDeltaSeconds)
        {
            if (!HasPendingOperation)
                return;
            if (unscaledDeltaSeconds < 0)
                throw new ArgumentOutOfRangeException(nameof(unscaledDeltaSeconds));

            _active.DurationSeconds += unscaledDeltaSeconds;
            if (_active.DurationSeconds < _timeoutSeconds)
                return;

            CancelContexts();
            FinishFailure(_active, TestStateErrorCode.Timeout,
                $"State {_active.Kind.ToString().ToLowerInvariant()} timed out after {_timeoutSeconds:0.###} seconds.");
        }

        public bool Cancel()
        {
            if (!HasPendingOperation)
                return false;

            var operation = _active;
            CancelContexts();
            operation.Status = TestStateOperationStatus.Cancelled;
            operation.ErrorCode = TestStateErrorCode.Cancelled;
            operation.Error = "State operation was cancelled.";
            ClearActive(operation);
            return true;
        }

        TestStateOperation Begin(
            TestStateOperationKind kind,
            float timeoutSeconds)
        {
            _active = new TestStateOperation(kind);
            _timeoutSeconds = timeoutSeconds;
            return _active;
        }

        void CompleteCapture(
            TestStateOperation operation,
            RegisteredTestStateProvider provider,
            string stateLabel,
            byte[] payload)
        {
            if (!IsActive(operation))
                return;
            if (string.IsNullOrWhiteSpace(stateLabel))
            {
                FinishFailure(operation, TestStateErrorCode.InvalidPayload,
                    "The provider returned no state label.");
                return;
            }
            if (payload == null)
            {
                FinishFailure(operation, TestStateErrorCode.InvalidPayload,
                    "The provider returned a null state payload.");
                return;
            }

            var scene = SceneManager.GetActiveScene();
            operation.Anchor = new TestStateAnchor(
                provider.ProviderId,
                provider.StateFormatVersion,
                stateLabel,
                payload,
                scene.name,
                scene.path);
            operation.Status = TestStateOperationStatus.Succeeded;
            operation.ErrorCode = TestStateErrorCode.None;
            ClearActive(operation);
        }

        void CompleteRestore(TestStateOperation operation)
        {
            if (!IsActive(operation))
                return;

            operation.Status = TestStateOperationStatus.Succeeded;
            operation.ErrorCode = TestStateErrorCode.None;
            operation.ReadyForReplay = true;
            ClearActive(operation);
        }

        void FailProvider(TestStateOperation operation, string error)
        {
            if (!IsActive(operation))
                return;

            FinishFailure(operation, TestStateErrorCode.ProviderError,
                string.IsNullOrWhiteSpace(error) ? "The state provider reported an error." : error);
        }

        void FailException(TestStateOperation operation, Exception exception)
        {
            if (!IsActive(operation))
                return;

            FinishFailure(operation, TestStateErrorCode.ProviderException,
                $"State provider threw {exception.GetType().Name}: {exception.Message}");
        }

        void FinishFailure(TestStateOperation operation, TestStateErrorCode code, string error)
        {
            operation.Status = TestStateOperationStatus.Failed;
            operation.ErrorCode = code;
            operation.Error = error;
            ClearActive(operation);
        }

        void ClearActive(TestStateOperation operation)
        {
            if (!ReferenceEquals(_active, operation))
                return;

            _active = null;
            _captureContext = null;
            _restoreContext = null;
            _timeoutSeconds = 0;
        }

        bool IsActive(TestStateOperation operation)
        {
            return ReferenceEquals(_active, operation) && !operation.IsFinished;
        }

        void CancelContexts()
        {
            _captureContext?.CancelFromOwner();
            _restoreContext?.CancelFromOwner();
        }

        static TestStateOperation ImmediateFailure(
            TestStateOperationKind kind,
            TestStateErrorCode code,
            string error)
        {
            return new TestStateOperation(kind)
            {
                Status = TestStateOperationStatus.Failed,
                ErrorCode = code,
                Error = error
            };
        }

        static void ValidateTimeout(float timeoutSeconds)
        {
            if (timeoutSeconds <= 0 || float.IsNaN(timeoutSeconds) || float.IsInfinity(timeoutSeconds))
                throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        }
    }
}
