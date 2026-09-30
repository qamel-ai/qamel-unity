using System;

namespace QamelCapture.TestAuthoring
{
    /// <summary>One immutable, in-memory state anchor captured by the game.</summary>
    public sealed class TestStateAnchor
    {
        readonly byte[] _payload;

        internal TestStateAnchor(
            string providerId,
            int stateFormatVersion,
            string stateLabel,
            byte[] payload,
            string activeSceneName,
            string activeScenePath)
        {
            ProviderId = providerId;
            StateFormatVersion = stateFormatVersion;
            StateLabel = stateLabel;
            _payload = (byte[])payload.Clone();
            PayloadByteCount = _payload.Length;
            PayloadChecksum = ComputeChecksum(_payload);
            CapturedAtUtc = DateTime.UtcNow;
            ActiveSceneName = activeSceneName;
            ActiveScenePath = activeScenePath;
            ApplicationVersion = UnityEngine.Application.version;
            UnityVersion = UnityEngine.Application.unityVersion;
        }

        public string ProviderId { get; }
        public int StateFormatVersion { get; }
        public string StateLabel { get; }
        public int PayloadByteCount { get; }
        public string PayloadChecksum { get; }
        public DateTime CapturedAtUtc { get; }
        public string ActiveSceneName { get; }
        public string ActiveScenePath { get; }
        public string ApplicationVersion { get; }
        public string UnityVersion { get; }

        public byte[] GetPayloadCopy()
        {
            return (byte[])_payload.Clone();
        }

        static string ComputeChecksum(byte[] payload)
        {
            unchecked
            {
                const ulong offset = 14695981039346656037;
                const ulong prime = 1099511628211;
                var hash = offset;
                for (var index = 0; index < payload.Length; index++)
                {
                    hash ^= payload[index];
                    hash *= prime;
                }
                return hash.ToString("X16");
            }
        }
    }
}
