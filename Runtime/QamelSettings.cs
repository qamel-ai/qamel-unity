using UnityEngine;

namespace QamelCapture
{
    /// <summary>
    /// Project-wide configuration for Qamel Capture. Lives as a ScriptableObject at
    /// Assets/Qamel/Resources/QamelSettings.asset, created via Project Settings > Qamel.
    /// </summary>
    public sealed class QamelSettings : ScriptableObject
    {
        public const string ResourceName = "QamelSettings";
        /// <summary>Kept in sync with package.json by the release script.</summary>
        public const string PluginVersion = "0.1.3";

        public enum FlipMode
        {
            Auto = 0,
            ForceFlip = 1,
            NoFlip = 2,
        }

        public enum ParticipantKind
        {
            Unknown = 0,
            Developer = 1,
            Playtester = 2,
        }

        [Header("General")]
        [Tooltip("Master switch. When off, Qamel does nothing at runtime.")]
        public bool captureEnabled = true;

        [Header("Upload")]
        [Tooltip("Project API key from the Qamel dashboard. Required: Qamel keeps data only in memory and on the Qamel servers, never on the player's disk.")]
        public string apiKey = "";

        [Tooltip("Qamel ingest base URL. Leave as-is unless Qamel gave you a different ingest host; request paths are versioned below this base.")]
        public string endpoint = "https://ingest.qamel.ai";

        [Tooltip("Master switch for uploads. When off, Qamel captures nothing useful (there is no local storage), so this is mainly for temporarily muting the plugin.")]
        public bool uploadReports = true;

        [Header("Build context")]
        [Tooltip("Optional immutable build identifier from your CI or release pipeline. Prefer this over a distribution-channel name.")]
        public string buildId = "";

        [Tooltip("Who normally runs packaged builds made with these settings. Editor sessions are always marked as developer. Leave Unknown unless the build has a clear audience.")]
        public ParticipantKind defaultParticipantKind = ParticipantKind.Unknown;

        [Header("Capture")]
        [Tooltip("How many seconds of gameplay are kept in the rolling buffer and attached to each report.")]
        [Range(15, 600)]
        public int bufferSeconds = 120;

        [Tooltip("Frames captured per second for the rolling gameplay recording.")]
        [Range(1, 15)]
        public float captureFps = 6f;

        [Tooltip("Width of captured frames in pixels; height follows the screen aspect ratio. 1280 (~720p) is readable for humans and LLMs; drop to 640 to save bandwidth.")]
        [Range(240, 1280)]
        public int frameWidth = 1280;

        [Range(20, 95)]
        public int jpegQuality = 60;

        [Tooltip("Vertical flip of captured frames. Leave on Auto unless frames come out upside down.")]
        public FlipMode frameFlip = FlipMode.Auto;

        [Header("Input capture")]
        [Tooltip("Record key and mouse button presses as events (raw keys only, never assembled text).")]
        public bool captureInput = true;

        [Tooltip("Record low-rate normalized mouse position samples.")]
        public bool captureMousePosition = true;

        [Header("Reporting")]
        [Tooltip("Hotkey that opens the in-game bug report overlay.")]
        public KeyCode reportHotkey = KeyCode.F8;

        [Tooltip("Show Qamel's built-in report form. Turn off to use your own UI instead and call Qamel.TriggerReport(text) from it; capture and upload keep working either way.")]
        public bool useBuiltInOverlay = true;

        [Tooltip("Freeze the game (Time.timeScale = 0) and pause audio while the report form is open. Turn this off for multiplayer, where only this client would stop, or when your game pauses itself from Qamel.ReportFormOpened.")]
        public bool pauseWhileReporting = true;

        [Tooltip("Automatically file a report when an unhandled exception is logged, so the session context is delivered before a potential crash can lose the in-memory buffer. Rate-limited to one auto-report per minute.")]
        public bool autoReportOnException = true;

        [Header("Experimental: continuous streaming")]
        [Tooltip("Continuously upload capture chunks instead of keeping only a rolling buffer. Experimental; used to evaluate full-session capture.")]
        public bool continuousStreaming = false;

        [Tooltip("Seconds of capture per streamed chunk.")]
        [Range(2, 60)]
        public int streamChunkSeconds = 10;

        [Header("Updates")]
        [Tooltip("Ask Qamel once a day, in the editor only, whether a newer plugin version exists and show it in Project Settings > Qamel. A plain GET: no API key, no identifiers, no project data.")]
        public bool checkForUpdates = true;

        [Header("Diagnostics")]
        [Tooltip("Log Qamel's own informational messages to the console.")]
        public bool verboseLogging = false;

        [Tooltip("If Qamel itself hits an internal error, send a small diagnostic (plugin/engine/OS info and the error, never gameplay data) to the Qamel servers so the plugin can be fixed.")]
        public bool sendPluginDiagnostics = true;

        public static QamelSettings LoadFromResources()
        {
            return Resources.Load<QamelSettings>(ResourceName);
        }
    }
}
