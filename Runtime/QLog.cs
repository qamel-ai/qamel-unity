using UnityEngine;

namespace QamelCapture
{
    /// <summary>
    /// Internal logging. Everything goes through here so LogRecorder can filter
    /// Qamel's own output (by prefix) out of the captured session log.
    /// </summary>
    internal static class QLog
    {
        public const string Prefix = "[Qamel] ";

        public static bool Verbose;

        public static void Info(string message)
        {
            if (Verbose) Debug.Log(Prefix + message);
        }

        public static void Warn(string message)
        {
            Debug.LogWarning(Prefix + message);
        }
    }
}
