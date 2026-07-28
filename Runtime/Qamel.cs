using System;

namespace QamelCapture
{
    /// <summary>
    /// Public entry points for games to enrich the capture stream. All methods are
    /// safe to call even when Qamel is disabled or not configured (they no-op).
    /// </summary>
    public static class Qamel
    {
        static readonly object IdentityGate = new object();
        static string _pendingPlayerId;
        static bool _hasPendingPlayerId;
        static QamelSettings.ParticipantKind _pendingParticipantKind;
        static bool _hasPendingParticipantKind;

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

        /// <summary>
        /// Associates subsequent capture data with an opaque player id from the
        /// game's own account system. Do not pass email addresses or display names.
        /// Calls made before Qamel starts are applied during initialization.
        /// </summary>
        public static void SetPlayerIdentity(string playerId)
        {
            QamelRunner runner;
            lock (IdentityGate)
            {
                runner = QamelRunner.Instance;
                _pendingPlayerId = playerId;
                _hasPendingPlayerId = true;
            }
            if (runner != null) runner.SetPlayerIdentity(playerId);
        }

        /// <summary>Returns capture to anonymous installation identity after logout.</summary>
        public static void ClearPlayerIdentity()
        {
            QamelRunner runner;
            lock (IdentityGate)
            {
                runner = QamelRunner.Instance;
                _pendingPlayerId = "";
                _hasPendingPlayerId = true;
            }
            if (runner != null) runner.ClearPlayerIdentity();
        }

        /// <summary>Marks subsequent capture as developer, playtester, or unknown.</summary>
        public static void SetParticipantKind(QamelSettings.ParticipantKind kind)
        {
            QamelRunner runner;
            lock (IdentityGate)
            {
                runner = QamelRunner.Instance;
                _pendingParticipantKind = kind;
                _hasPendingParticipantKind = true;
            }
            if (runner != null) runner.SetParticipantKind(kind);
        }

        /// <summary>The stable anonymous id for this game installation.</summary>
        public static string InstallationId =>
            QamelRunner.Instance != null ? QamelRunner.Instance.Identity.InstallationId : "";

        /// <summary>The current studio-supplied opaque player id, or empty.</summary>
        public static string PlayerId =>
            QamelRunner.Instance != null ? QamelRunner.Instance.Identity.ExternalPlayerId : "";

        internal static void ApplyPendingIdentity(QamelRunner runner)
        {
            lock (IdentityGate)
            {
                if (_hasPendingPlayerId)
                {
                    if (string.IsNullOrEmpty(_pendingPlayerId)) runner.ApplyInitialClearPlayer();
                    else runner.ApplyInitialPlayerIdentity(_pendingPlayerId);
                }
                if (_hasPendingParticipantKind)
                    runner.ApplyInitialParticipantKind(_pendingParticipantKind);
            }
        }

        /// <summary>True when Qamel capture is running in this session.</summary>
        public static bool IsRunning => QamelRunner.Instance != null;

        /// <summary>
        /// True while the built-in report form is showing. Qamel cannot consume the
        /// keypress that opened it (neither input backend allows that), so games
        /// that react to Escape or to the report hotkey themselves should check
        /// this before opening their own menu.
        /// </summary>
        public static bool IsReportFormOpen =>
            QamelRunner.Instance != null && QamelRunner.Instance.IsReportFormOpen;

        /// <summary>
        /// Raised when the built-in report form opens. Use it to pause with your
        /// own pause manager (and turn off <c>Pause While Reporting</c> so Qamel
        /// does not also touch <c>Time.timeScale</c>). Unsubscribe in OnDestroy.
        /// </summary>
        public static event Action ReportFormOpened;

        /// <summary>Raised when the report form closes, whether sent or cancelled.</summary>
        public static event Action ReportFormClosed;

        internal static void RaiseReportFormOpened()
        {
            Raise(ReportFormOpened, nameof(ReportFormOpened));
        }

        internal static void RaiseReportFormClosed()
        {
            Raise(ReportFormClosed, nameof(ReportFormClosed));
        }

        /// <summary>
        /// A game handler that throws must not take capture down with it: the
        /// runner would disable Qamel for the session over someone else's bug.
        /// </summary>
        static void Raise(Action handler, string name)
        {
            if (handler == null) return;
            try
            {
                handler();
            }
            catch (Exception e)
            {
                QLog.Warn("A " + name + " handler threw: " + e.Message);
            }
        }
    }
}
