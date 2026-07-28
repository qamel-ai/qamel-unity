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

        /// <summary>
        /// Logged even with verbose logging off. Reserved for the one line that
        /// tells a developer capture is alive; without it, a correct setup and a
        /// broken one look identical.
        /// </summary>
        public static void Notice(string message)
        {
            Debug.Log(Prefix + message);
        }

        public static void Warn(string message)
        {
            Debug.LogWarning(Prefix + message);
        }
    }
}
