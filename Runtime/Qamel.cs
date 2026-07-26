namespace QamelCapture
{
    /// <summary>
    /// Public entry points for games to enrich the capture stream. All methods are
    /// safe to call even when Qamel is disabled or not configured (they no-op).
    /// </summary>
    public static class Qamel
    {
        /// <summary>Adds a custom breadcrumb message to the session log.</summary>
        public static void Log(string message)
        {
            Event("log", message);
        }

        /// <summary>
        /// Adds a named game event with optional data,
        /// e.g. <c>Qamel.Event("level_loaded", "level_3")</c>.
        /// </summary>
        public static void Event(string name, string data = null)
        {
            var runner = QamelRunner.Instance;
            if (runner != null && !string.IsNullOrEmpty(name)) runner.AddCustomEvent(name, data);
        }

        /// <summary>
        /// Programmatically files a bug report with the current rolling buffer,
        /// without opening the overlay.
        /// </summary>
        public static void TriggerReport(string text = null)
        {
            var runner = QamelRunner.Instance;
            if (runner != null) runner.TriggerReport(text ?? "");
        }

        /// <summary>True when Qamel capture is running in this session.</summary>
        public static bool IsRunning => QamelRunner.Instance != null;
    }
}
