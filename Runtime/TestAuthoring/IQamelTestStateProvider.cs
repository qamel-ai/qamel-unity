using System;

namespace QamelCapture.TestAuthoring
{
    /// <summary>
    /// Game-owned boundary for capturing and restoring one opaque test state.
    /// Providers complete on Unity's main thread after any asynchronous work is ready.
    /// </summary>
    public interface IQamelTestStateProvider
    {
        string ProviderId { get; }
        int StateFormatVersion { get; }
        void CaptureState(TestStateCaptureContext context);
        void RestoreState(TestStateRestoreContext context);
    }

    public sealed class TestStateCaptureContext
    {
        readonly Func<bool> _ownerCancelled;
        readonly Action<string, byte[]> _succeed;
        readonly Action<string> _fail;
        bool _finished;
        bool _cancelled;

        internal TestStateCaptureContext(
            Func<bool> ownerCancelled,
            Action<string, byte[]> succeed,
            Action<string> fail)
        {
            _ownerCancelled = ownerCancelled;
            _succeed = succeed;
            _fail = fail;
        }

        public bool IsCancellationRequested => _cancelled || _ownerCancelled();

        public bool Succeed(string stateLabel, byte[] payload)
        {
            if (_finished || IsCancellationRequested)
                return false;

            _finished = true;
            _succeed(stateLabel, payload);
            return true;
        }

        public bool Fail(string error)
        {
            if (_finished || IsCancellationRequested)
                return false;

            _finished = true;
            _fail(error);
            return true;
        }

        internal void CancelFromOwner()
        {
            _cancelled = true;
            _finished = true;
        }
    }

    public sealed class TestStateRestoreContext
    {
        readonly byte[] _payload;
        readonly Func<bool> _ownerCancelled;
        readonly Action _ready;
        readonly Action<string> _fail;
        bool _finished;
        bool _cancelled;

        internal TestStateRestoreContext(
            byte[] payload,
            Func<bool> ownerCancelled,
            Action ready,
            Action<string> fail)
        {
            _payload = payload;
            _ownerCancelled = ownerCancelled;
            _ready = ready;
            _fail = fail;
        }

        public bool IsCancellationRequested => _cancelled || _ownerCancelled();

        /// <summary>
        /// Returns a provider-owned copy. Mutating it cannot change the captured anchor.
        /// </summary>
        public byte[] GetPayloadCopy()
        {
            return (byte[])_payload.Clone();
        }

        /// <summary>
        /// Signals that restore and any scene or setup work are complete and input may begin.
        /// </summary>
        public bool ReadyForReplay()
        {
            if (_finished || IsCancellationRequested)
                return false;

            _finished = true;
            _ready();
            return true;
        }

        public bool Fail(string error)
        {
            if (_finished || IsCancellationRequested)
                return false;

            _finished = true;
            _fail(error);
            return true;
        }

        internal void CancelFromOwner()
        {
            _cancelled = true;
            _finished = true;
        }
    }
}
