using System;

namespace QamelCapture.TestAuthoring
{
    public sealed class TestStateRegistrationResult
    {
        internal TestStateRegistrationResult(bool succeeded, TestStateErrorCode errorCode, string error)
        {
            Succeeded = succeeded;
            ErrorCode = errorCode;
            Error = error;
        }

        public bool Succeeded { get; }
        public TestStateErrorCode ErrorCode { get; }
        public string Error { get; }
    }

    internal sealed class RegisteredTestStateProvider
    {
        public IQamelTestStateProvider Provider;
        public string ProviderId;
        public int StateFormatVersion;
    }

    /// <summary>Prefers one game-owned provider over an optional built-in fallback.</summary>
    public sealed class TestStateRegistry
    {
        public static TestStateRegistry Global { get; } = new TestStateRegistry();

        RegisteredTestStateProvider _active;
        RegisteredTestStateProvider _fallback;
        long _revision;

        RegisteredTestStateProvider Effective => _active ?? _fallback;
        public bool HasProvider => Effective != null;
        public bool HasOutcomeProvider => Effective?.Provider is IQamelTestOutcomeProvider;
        public string ProviderId => Effective?.ProviderId;
        public int StateFormatVersion => Effective?.StateFormatVersion ?? 0;
        internal long Revision => _revision;

        public TestStateRegistrationResult Register(IQamelTestStateProvider provider)
        {
            if (provider == null)
                return Failed(TestStateErrorCode.InvalidProvider, "State provider cannot be null.");
            if (_active != null)
                return Failed(
                    TestStateErrorCode.DuplicateProvider,
                    $"State provider '{_active.ProviderId}' is already registered.");

            string providerId;
            int formatVersion;
            try
            {
                providerId = provider.ProviderId;
                formatVersion = provider.StateFormatVersion;
            }
            catch (Exception exception)
            {
                return Failed(
                    TestStateErrorCode.InvalidProvider,
                    "State provider metadata failed: " + exception.Message);
            }

            if (string.IsNullOrWhiteSpace(providerId))
                return Failed(TestStateErrorCode.InvalidProvider, "State provider ID is required.");
            if (formatVersion <= 0)
                return Failed(TestStateErrorCode.InvalidProvider, "State format version must be positive.");

            _active = new RegisteredTestStateProvider
            {
                Provider = provider,
                ProviderId = providerId,
                StateFormatVersion = formatVersion
            };
            _revision++;
            return new TestStateRegistrationResult(true, TestStateErrorCode.None, null);
        }

        public bool Unregister(IQamelTestStateProvider provider)
        {
            if (_fallback != null && ReferenceEquals(_fallback.Provider, provider))
            {
                _fallback = null;
                if (_active == null) _revision++;
                return true;
            }
            if (_active == null || !ReferenceEquals(_active.Provider, provider))
                return false;

            _active = null;
            _revision++;
            return true;
        }

        public void Clear()
        {
            if (Effective != null)
                _revision++;
            _active = null;
            _fallback = null;
        }

        internal TestStateRegistrationResult RegisterFallback(IQamelTestStateProvider provider)
        {
            if (_fallback != null)
                return Failed(TestStateErrorCode.DuplicateProvider, "A fallback is already registered.");
            // Reuse the same metadata validation without competing with the
            // studio's provider, including one registered later during startup.
            var validation = new TestStateRegistry();
            var result = validation.Register(provider);
            if (!result.Succeeded) return result;
            _fallback = validation._active;
            if (_active == null) _revision++;
            return result;
        }

        internal bool TryGetProvider(out RegisteredTestStateProvider provider)
        {
            provider = Effective;
            return provider != null;
        }

        static TestStateRegistrationResult Failed(TestStateErrorCode code, string error)
        {
            return new TestStateRegistrationResult(false, code, error);
        }
    }
}
