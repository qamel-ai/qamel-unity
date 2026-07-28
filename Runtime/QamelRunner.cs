using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace QamelCapture
{
    /// <summary>
    /// Hidden runtime host created by <see cref="QamelBootstrap"/>. Owns the session,
    /// the recorders, the report overlay and the uploader. All Qamel failures are
    /// contained here: on repeated internal errors capture disables itself and logs
    /// one warning, never interrupting the game.
    /// </summary>
    [DefaultExecutionOrder(30000)]
    public sealed class QamelRunner : MonoBehaviour
    {
        public static QamelRunner Instance { get; private set; }

        QamelSettings _settings;
        SessionBuffer _buffer;
        FrameRecorder _frameRecorder;
        LogRecorder _logRecorder;
        InputRecorder _inputRecorder;
        ReportOverlay _overlay;
        Uploader _uploader;
        ChunkStreamer _streamer;
        ParticipantIdentity _identity;

        string _sessionId;
        DateTime _sessionStartUtc;
        System.Diagnostics.Stopwatch _clock;
        int _mainThreadId;
        readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();

        string _toast;
        float _toastUntil;
        bool _failed;
        double _nextAutoReportAt;
        int _pendingAutoReports;

        const double AutoReportCooldownSeconds = 60;

        /// <summary>Seconds since session start (monotonic, unaffected by timeScale).</summary>
        public double Now => _clock != null ? _clock.Elapsed.TotalSeconds : 0;

        /// <summary>True while the built-in report form is showing.</summary>
        public bool IsReportFormOpen => _overlay != null && _overlay.IsOpen;
        internal IdentitySnapshot Identity => _identity != null ? _identity.Snapshot() : default(IdentitySnapshot);

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;

            try
            {
                _settings = QamelSettings.LoadFromResources();
                if (_settings == null || !_settings.captureEnabled)
                {
                    enabled = false;
                    return;
                }

                // No key means nothing can ever be uploaded, and Qamel keeps no
                // data on disk -- capturing would only burn memory. Fail loudly
                // so a missing key can't slip into a playtest build unnoticed.
                if (string.IsNullOrWhiteSpace(_settings.apiKey) ||
                    string.IsNullOrWhiteSpace(_settings.endpoint))
                {
                    Debug.LogError(QLog.Prefix + "API key or endpoint is not set -- Qamel capture is DISABLED. " +
                                   "Set them in Project Settings > Qamel (stored in the QamelSettings asset).");
                    enabled = false;
                    return;
                }

                QLog.Verbose = _settings.verboseLogging;
                IngestRoutes.ResetSession();
                _sessionId = Guid.NewGuid().ToString("N");
                _sessionStartUtc = DateTime.UtcNow;
                _clock = System.Diagnostics.Stopwatch.StartNew();
                _identity = new ParticipantIdentity(_settings);
                Qamel.ApplyPendingIdentity(this);

                int maxFrames = Mathf.CeilToInt(_settings.bufferSeconds * Mathf.Max(1f, _settings.captureFps)) + 8;
                _buffer = new SessionBuffer(_settings.bufferSeconds, maxFrames);
                _buffer.AddEvent(Now, SessionEvents.Identity(Now, "init", Identity));

                Func<double> now = () => _clock.Elapsed.TotalSeconds;
                // The exception hook can fire from any thread; the actual report is
                // filed from Update on the main thread.
                _logRecorder = new LogRecorder(_buffer, now,
                    () => Interlocked.Exchange(ref _pendingAutoReports, 1));
                _inputRecorder = new InputRecorder(_settings, _buffer, now);
                _frameRecorder = new FrameRecorder(_settings, _buffer, now);
                _overlay = new ReportOverlay(_settings);
                _overlay.Submitted += TriggerReport;
                _overlay.Opened += Qamel.RaiseReportFormOpened;
                _overlay.Closed += Qamel.RaiseReportFormClosed;
                _uploader = new Uploader(_settings, this);

                StartCoroutine(_frameRecorder.CaptureLoop());

                if (_settings.continuousStreaming)
                {
                    _streamer = new ChunkStreamer(_settings, _buffer, now, () => Identity,
                        _sessionId, _sessionStartUtc,
                        (manifest, bytes, fileName) =>
                            _mainThreadQueue.Enqueue(() => _uploader.Enqueue(manifest, bytes, fileName, isChunk: true)));
                    StartCoroutine(_streamer.StreamLoop());
                }

                QLog.Notice("Session " + _sessionId + " started. Press " + _settings.reportHotkey + " to file a bug report.");
            }
            catch (Exception e)
            {
                Fail("initialization", e);
            }
        }

        void Update()
        {
            if (_failed || _buffer == null) return;
            try
            {
                while (_mainThreadQueue.TryDequeue(out var action)) action();

                if (Interlocked.Exchange(ref _pendingAutoReports, 0) == 1)
                    TryAutoReport();

                _logRecorder.Tick();
                if (!_overlay.IsOpen) _inputRecorder.Tick();
                _overlay.Tick();

                if (_settings.useBuiltInOverlay && !_overlay.IsOpen &&
                    CompatInput.GetKeyDown(_settings.reportHotkey))
                    _overlay.Open();
            }
            catch (Exception e)
            {
                Fail("update loop", e);
            }
        }

        void OnGUI()
        {
            if (_failed || _overlay == null) return;
            try
            {
                _overlay.OnGUI();

                // No QLog.Prefix here: the toast is drawn over the game and a
                // playtester should not be shown the name of the tooling.
                if (!string.IsNullOrEmpty(_toast) && Time.unscaledTime < _toastUntil)
                    GUI.Label(new Rect(12, Screen.height - 30, Screen.width - 24, 24), _toast);
            }
            catch (ExitGUIException)
            {
                // IMGUI control flow, not an error.
                throw;
            }
            catch (Exception e)
            {
                Fail("overlay", e);
            }
        }

        /// <summary>Files a report with the current rolling buffer contents.</summary>
        public void TriggerReport(string userText)
        {
            if (_failed || _buffer == null) return;
            try
            {
                if (!_uploader.CanUpload)
                {
                    // Player-facing text stays vendor-neutral: playtesters see this
                    // overlay inside someone else's game and need not know about
                    // Qamel. Diagnostics go to the console instead.
                    ShowToast("Bug report could not be sent.");
                    QLog.Warn("Report discarded: uploads are not configured. Qamel keeps data only in memory " +
                              "and on the Qamel servers; set the API key in Project Settings > Qamel.");
                    return;
                }

                double t = Now;
                string reportId = Guid.NewGuid().ToString("N").Substring(0, 12);
                _buffer.AddEvent(t, SessionEvents.Report(t, reportId, userText));

                var eventLines = new List<string>();
                var frames = new List<CapturedFrame>();
                _buffer.Snapshot(eventLines, frames);

                int frameW = 0, frameH = 0;
                if (frames.Count > 0)
                {
                    var last = frames[frames.Count - 1];
                    frameW = last.Width;
                    frameH = last.Height;
                }
                string manifest = ReportManifest.Build(new ReportManifestData
                {
                    SessionId = _sessionId,
                    ReportId = reportId,
                    ReportT = t,
                    UserText = userText,
                    EventCount = eventLines.Count,
                    FrameCount = frames.Count,
                    FrameWidth = frameW,
                    FrameHeight = frameH,
                    SessionStartedUtc = _sessionStartUtc,
                    BufferSeconds = _settings.bufferSeconds,
                    CaptureFps = _settings.captureFps,
                    Identity = Identity,
                    BuildId = _settings.buildId,
                });
                string fileName = "report_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + reportId + ".zip";

                ShowToast("Sending report...");
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        byte[] bytes = ReportBundler.BuildBundle(manifest, eventLines, frames);
                        _mainThreadQueue.Enqueue(() =>
                        {
                            _uploader.Enqueue(manifest, bytes, fileName, isChunk: false);
                            ShowToast("Bug report sent. Thanks!");
                            QLog.Info("Report " + reportId + " queued for upload (" + (bytes.Length / 1024) + " KB).");
                        });
                    }
                    catch (Exception e)
                    {
                        _mainThreadQueue.Enqueue(() => QLog.Warn("Failed to build report bundle: " + e.Message));
                    }
                });
            }
            catch (Exception e)
            {
                Fail("report", e);
            }
        }

        void TryAutoReport()
        {
            if (!_settings.autoReportOnException) return;
            // Rate-limited: a per-frame exception loop must not flood the server
            // with near-identical reports.
            if (Now < _nextAutoReportAt) return;
            _nextAutoReportAt = Now + AutoReportCooldownSeconds;

            QLog.Info("Unhandled exception detected; filing an automatic report.");
            TriggerReport("[auto] Unhandled exception (see logs.jsonl for the stack trace)");
        }

        internal void AddCustomEvent(string name, string data)
        {
            if (_failed || _buffer == null) return;
            double t = Now;
            _buffer.AddEvent(t, SessionEvents.Custom(t, name, data));
        }

        internal void SetPlayerIdentity(string playerId)
        {
            RunOnMainThread(() =>
            {
                if (_identity == null) return;
                if (!_identity.TrySetPlayer(playerId, out string error))
                {
                    QLog.Warn(error);
                    return;
                }
                AddIdentityEvent("set");
            });
        }

        internal void ClearPlayerIdentity()
        {
            RunOnMainThread(() =>
            {
                if (_identity == null) return;
                _identity.ClearPlayer();
                AddIdentityEvent("clear");
            });
        }

        internal void SetParticipantKind(QamelSettings.ParticipantKind kind)
        {
            RunOnMainThread(() =>
            {
                if (_identity == null) return;
                _identity.SetParticipantKind(kind);
                AddIdentityEvent("set");
            });
        }

        internal void ApplyInitialPlayerIdentity(string playerId)
        {
            if (!_identity.TrySetPlayer(playerId, out string error)) QLog.Warn(error);
        }

        internal void ApplyInitialClearPlayer()
        {
            _identity.ClearPlayer();
        }

        internal void ApplyInitialParticipantKind(QamelSettings.ParticipantKind kind)
        {
            _identity.SetParticipantKind(kind);
        }

        void AddIdentityEvent(string action)
        {
            if (_buffer == null) return;
            double t = Now;
            _buffer.AddEvent(t, SessionEvents.Identity(t, action, Identity));
        }

        void RunOnMainThread(Action action)
        {
            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId) action();
            else _mainThreadQueue.Enqueue(action);
        }

        void ShowToast(string message)
        {
            _toast = message;
            _toastUntil = Time.unscaledTime + 4f;
        }

        void Fail(string where, Exception e)
        {
            if (_failed) return;
            _failed = true;
            QLog.Warn("Capture disabled after an internal error in " + where + ": " + e.Message +
                      "\nQamel never interrupts the game. Restart play mode to re-enable.");
            try { Cleanup(); } catch { /* never propagate */ }

            // Report the plugin failure itself (never gameplay data), so this
            // project does not just quietly stop delivering reports.
            try
            {
                if (_settings != null && _settings.sendPluginDiagnostics &&
                    !string.IsNullOrWhiteSpace(_settings.apiKey) &&
                    !string.IsNullOrWhiteSpace(_settings.endpoint) &&
                    isActiveAndEnabled)
                {
                    StartCoroutine(PluginDiagnostics.Send(_settings, _sessionId, where, e, Identity));
                }
            }
            catch { /* the failure path must never throw */ }
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            Cleanup();
        }

        void Cleanup()
        {
            _streamer?.Stop();
            _logRecorder?.Dispose();
            _frameRecorder?.Dispose();
            _overlay?.Close();
        }
    }
}
